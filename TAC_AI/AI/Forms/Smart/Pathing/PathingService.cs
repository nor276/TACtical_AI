using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>
    /// Per-request path solve job. Submitted by Control via
    /// <see cref="PathingService.RequestPath"/>. Per PATHING-CONTRACT §7.1.
    ///
    /// Carries the requesting tech's TeamId so the solver can pick the per-team threat
    /// field (excluding own-team techs from threat sources).
    /// </summary>
    public readonly struct PathRequest
    {
        public readonly TechId Tech;
        public readonly TeamId MyTeam;
        public readonly Vector3 Start;
        public readonly Vector3 StartVelocity;
        public readonly Vector3 Goal;
        public readonly Vector3 GoalVelocity;
        public readonly float Duration;
        public readonly VehicleCapability Capability;
        /// <summary>
        /// P9 Item 30: request priority (0=lowest, 255=highest, 128=default/normal).
        /// PathSolveLoop sheds requests with Priority &lt; 128 when
        /// <see cref="PathRequestBackpressure.ShouldShedLowPriority"/> is true. Default 128
        /// preserves v0.1 behavior — no production caller is shed unless they explicitly
        /// opt into low-priority requests.
        /// </summary>
        public readonly byte Priority;
        /// <summary>
        /// L-017: monotonic enqueue timestamp (from MonoClock.Now). Set by
        /// PathingService.RequestPath at enqueue time via the internal ctor below — callers
        /// CANNOT set this directly via the public ctor (which leaves it at 0L = "stamp
        /// pending"). L-085 dequeue-side expiry uses (Now - stamp) &gt; TtlMs to drop
        /// stale requests before solving.
        /// </summary>
        public readonly long EnqueueMonoTimestamp;

        public PathRequest(TechId tech, TeamId myTeam,
            Vector3 start, Vector3 startVel,
            Vector3 goal, Vector3 goalVel,
            float duration, VehicleCapability capability,
            byte priority = 128)
            : this(tech, myTeam, start, startVel, goal, goalVel, duration, capability, priority, 0L)
        {
        }

        // L-017: internal ctor used by PathingService.RequestPath to stamp at enqueue time.
        // The public ctor delegates with 0L sentinel so existing call sites compile unchanged;
        // RequestPath rewrites the request via this ctor before pushing onto the queue.
        internal PathRequest(TechId tech, TeamId myTeam,
            Vector3 start, Vector3 startVel,
            Vector3 goal, Vector3 goalVel,
            float duration, VehicleCapability capability,
            byte priority, long enqueueMonoTimestamp)
        {
            Tech = tech;
            MyTeam = myTeam;
            Start = start;
            StartVelocity = startVel;
            Goal = goal;
            GoalVelocity = goalVel;
            Duration = duration;
            Capability = capability;
            Priority = priority;
            EnqueueMonoTimestamp = enqueueMonoTimestamp;
        }
    }

    /// <summary>
    /// Smart's pathing orchestrator. Per PATHING-CONTRACT §7.
    ///
    /// Owns:
    /// - one singleton <see cref="TerrainMap"/> (refresh on main-thread tick),
    /// - a per-team threat-field cache rebuilt continuously at ~30 Hz,
    /// - a bounded path-request queue drained by one consumer worker,
    /// - the last-path cache per tech for synchronous read.
    ///
    /// Started by <see cref="SmartRuntime.Init"/>; shut down via WorkerLifecycleRegistry
    /// cancellation.
    ///
    /// THREADING: queries (GetThreatField, GetLastPath, CurrentTerrain reads) are safe
    /// from any thread; TerrainMap refresh is main-thread-only via
    /// <see cref="MainThreadTick"/>.
    /// </summary>
    public static class PathingService
    {
        private const int ThreatFieldTickMs = 33;        // ~30 Hz
        private const int SolveLoopIdleWaitMs = 20;      // sleep when queue empty
        private const int DefaultRequestQueueCapacity = 64;

        private static WorkerPool _pool;
        // BUG-013 / BUG-050: TerrainPublication is the L-033 atomic-swap holder. Readers
        // (worker threads) do a single Volatile.Read and see either old or new — never
        // half-published. OnWorldReset publishes a fresh TerrainMap via the holder rather
        // than mutating a field directly.
        private static readonly TerrainPublication _terrainPub = new TerrainPublication();
        private static ConcurrentDictionary<TeamId, DoubleBuffer<ThreatFieldSnapshot>> _threatFields;
        private static BoundedQueue<PathRequest> _requestQueue;
        private static ConcurrentDictionary<TechId, Trajectory> _lastPaths;
        private static int _running; // 0 = stopped, 1 = running
        // BUG-051: WorldResetRegistry has no Unregister; without this latch a form-swap
        // sequence (DeInit → Init) appends three entries (plus the bp closure pin) every
        // cycle and accumulates linearly with form-swap count. SmartForm.EnsureLegacyResetsRegistered
        // uses the same Interlocked.Exchange latch pattern.
        private static int _worldResetRegistered;
        // L-018: singleton backpressure. Constructed in Init, nulled in Shutdown. PathSolveLoop
        // reads via the field directly (same-assembly internal access); external consumers
        // (debug GUI, console, watchdog) read the read-only interface via the Backpressure
        // accessor below. Volatile read so accessors see Init→Shutdown transitions promptly.
        private static TAC_AI.AI.Forms.Smart.Threading.PathRequestBackpressure _backpressure;

        public static TerrainMap CurrentTerrain => _terrainPub.Read();
        public static bool IsRunning => Volatile.Read(ref _running) == 1;
        /// <summary>L-018: read-only backpressure surface. null when !IsRunning.</summary>
        public static IPathingBackpressureReadout Backpressure
            => System.Threading.Volatile.Read(ref _backpressure);

        /// <summary>
        /// Start the pathing workers. Idempotent. Called by SmartRuntime.Init.
        /// </summary>
        public static void Init(WorkerPool pool)
        {
            if (pool == null) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return; // already running

            _pool = pool;
            _terrainPub.Publish(new TerrainMap());
            _threatFields = new ConcurrentDictionary<TeamId, DoubleBuffer<ThreatFieldSnapshot>>();
            _requestQueue = new BoundedQueue<PathRequest>(DefaultRequestQueueCapacity, "Pathing.PathRequests");
            _lastPaths = new ConcurrentDictionary<TechId, Trajectory>();
            // L-018: lift backpressure to singleton so the debug GUI + console + watchdog
            // can read it via Backpressure without needing a per-loop reference.
            var bp = new TAC_AI.AI.Forms.Smart.Threading.PathRequestBackpressure();
            System.Threading.Volatile.Write(ref _backpressure, bp);
            // L-016 + L-034: register backpressure + the two private-field reset lambdas
            // with WorldResetRegistry so OnWorldReset hits all path-cache surfaces in one
            // collapsed call (L-069 in Wave 3 replaces SmartForm's OnWorldReset body with
            // a single ResetAll invocation).
            // BUG-051: one-time latch — WorldResetRegistry has no Unregister and the entries
            // would pin the prior-cycle backpressure closure via capture. The lambdas read
            // the live static fields each invocation, so re-registration is unnecessary;
            // the backpressure reset routes through the _backpressure field for the same
            // reason.
            if (Interlocked.Exchange(ref _worldResetRegistered, 1) == 0)
            {
                WorldResetRegistry.RegisterLambda("PathingService.Backpressure",
                    () => Volatile.Read(ref _backpressure)?.ResetForWorldChange());
                WorldResetRegistry.RegisterLambda("PathingService.ThreatFields",
                    () => _threatFields?.Clear());
                WorldResetRegistry.RegisterLambda("PathingService.LastPaths",
                    () => _lastPaths?.Clear());
            }

            _pool.EnqueueLongRunning(ThreatFieldRebuildLoop, "ThreatFieldRebuild");
            _pool.EnqueueLongRunning(PathSolveLoop, "PathSolve");

            // BUG-004: register both pathing daemons with the watchdogs. The canonical
            // roster lists ThreatFieldRebuild + PathSolve but no factory was registered,
            // so a TAE or unhandled escape leaves them dead permanently. Capture the pool
            // by closure with null-guard against a racing Shutdown.
            var poolRef = _pool;
            Func<string> threatFactory = () => poolRef?.EnqueueLongRunning(ThreatFieldRebuildLoop, "ThreatFieldRebuild");
            Func<string> solveFactory = () => poolRef?.EnqueueLongRunning(PathSolveLoop, "PathSolve");
            Threading.DaemonWatchdog.RegisterCanonical("ThreatFieldRebuild", threatFactory);
            Threading.DaemonWatchdog.RegisterCanonical("PathSolve", solveFactory);
            Threading.WorkerHealthMonitor.RegisterCanonical("ThreatFieldRebuild", threatFactory);
            Threading.WorkerHealthMonitor.RegisterCanonical("PathSolve", solveFactory);
        }

        /// <summary>
        /// Stop accepting new work. The actual loop cancellation goes through
        /// <see cref="WorkerLifecycleRegistry.CancelAllAndJoin"/> on SmartRuntime.Shutdown.
        /// </summary>
        public static void Shutdown()
        {
            if (Interlocked.Exchange(ref _running, 0) == 0) return;
            _threatFields?.Clear();
            _lastPaths?.Clear();
            _terrainPub.Clear();
            _pool = null;
            // L-018: drop singleton ref so external Backpressure readers see null until next Init.
            System.Threading.Volatile.Write(ref _backpressure, null);
        }

        /// <summary>
        /// Phase 3.5 (FIX-PLAN.md): non-destructive per-mission reset. Clears every per-
        /// mission cache (terrain snapshot, per-team threat fields, per-tech last-paths)
        /// but leaves the worker threads alive. Called from <see cref="SmartForm.OnWorldReset"/>
        /// when the engine transitions between missions in a single process. Workers
        /// rebuild populated caches on their next tick.
        /// </summary>
        public static void OnWorldReset()
        {
            if (!IsRunning) return;
            _threatFields?.Clear();
            _lastPaths?.Clear();
            _lastFailedMono.Clear();
            // Rebuild a fresh TerrainMap so the next refresh samples the new world's
            // terrain — keeping the old grid would treat the new mission's elevations
            // as the old mission's heights (AUDIT-R2 §2.R2.B TerrainMap origin frozen).
            // BUG-013 / BUG-050: atomic swap via TerrainPublication so a worker mid-read
            // sees old-or-new, never a half-published reference.
            _terrainPub.Publish(new TerrainMap());
        }

        // ---- Public query / submit API (any thread) ----

        public static IThreatField GetThreatField(TeamId team)
        {
            var terrain = _terrainPub.Read();
            if (_threatFields == null) return new ThreatField(ThreatFieldSnapshot.Empty, terrain);
            var buf = _threatFields.GetOrAdd(team,
                _ => new DoubleBuffer<ThreatFieldSnapshot>(ThreatFieldSnapshot.Empty));
            return new ThreatField(buf.Read(), terrain);
        }

        public static ThreatFieldSnapshot GetThreatFieldSnapshot(TeamId team)
        {
            if (_threatFields == null) return ThreatFieldSnapshot.Empty;
            return _threatFields.TryGetValue(team, out var buf) ? buf.Read() : ThreatFieldSnapshot.Empty;
        }

        /// <summary>
        /// L-061: drop the per-team threat-field DoubleBuffer for <paramref name="teamId"/>.
        /// Called by TeamReaperDaemon (L-044) just before the team is removed from
        /// SmartRuntime._teams so the cache doesn't leak. Also intended for
        /// SmartEventBridge.OnTankTeamChanged + orphan-sweep (full wiring lands with L-061's
        /// caller-side sweep in Wave 2). Idempotent — TryRemove on the dictionary handles
        /// missing keys silently.
        /// </summary>
        /// <summary>
        /// L-029: drop per-tech last-path cache for <paramref name="techId"/>. Called via
        /// sidecar adapter (PathingLastPathsSidecar) on every Forget fan-out.
        /// </summary>
        public static void ForgetTech(TechId techId)
        {
            _lastPaths?.TryRemove(techId, out _);
        }

        /// <summary>L-029: snapshot of TechIds with last-path entries (for leak-watch).</summary>
        /// <summary>
        /// L-042: GetLastPath with freshness gate. Returns the cached trajectory only when
        /// it was solved within <paramref name="maxStaleMs"/> ms; otherwise returns null
        /// so the caller can decide to re-request rather than steer on a stale path.
        /// Default 2000 ms = paths older than 2 seconds are discarded.
        /// </summary>
        public static Trajectory GetLastPathFresh(TechId tech, int maxStaleMs = 2000)
        {
            if (_lastPaths == null) return null;
            if (!_lastPaths.TryGetValue(tech, out var t) || t == null) return null;
            long now = MonoClock.Now();
            float ageS = MonoClock.Seconds(t.SolvedAtMono, now);
            if (ageS * 1000f > maxStaleMs) return null;
            return t;
        }

        public static System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotLastPathKeys()
        {
            var d = _lastPaths;
            if (d == null) return System.Array.Empty<TechId>();
            return (System.Collections.Generic.IReadOnlyCollection<TechId>)d.Keys;
        }

        public static void ForgetTeam(TeamId teamId)
        {
            if (_threatFields == null) return;
            if (_threatFields.TryRemove(teamId, out _))
            {
                DebugTAC_AI.LogWarnFileOnly("pathing-forget-team-" + teamId.Value,
                    "[PATHING-FORGET-TEAM] teamId=" + teamId.Value);
            }
        }

        // Per-(techId, dest-bucket) last-failed timestamp ring. Bucket = world XZ
        // quantised at 64 m so re-querying the same general area within a few seconds
        // shares the same suppression entry. Capacity-bounded to avoid leak in
        // long-running sessions; LRU evicts on overflow.
        private const int LastFailedCapacity = 256;
        private const float LastFailedBucketSizeM = 64f;
        private static readonly ConcurrentDictionary<long, long> _lastFailedMono =
            new ConcurrentDictionary<long, long>();

        private static long PackTechDestBucket(TechId tech, Vector3 dest)
        {
            int bx = Mathf.FloorToInt(dest.x / LastFailedBucketSizeM);
            int bz = Mathf.FloorToInt(dest.z / LastFailedBucketSizeM);
            // 16-bit X, 16-bit Z, 32-bit tech.
            uint bxU = unchecked((uint)bx) & 0xFFFFu;
            uint bzU = unchecked((uint)bz) & 0xFFFFu;
            return ((long)tech.Value << 32) | ((long)bxU << 16) | bzU;
        }

        /// <summary>
        /// O(1) traversability check via the published TerrainMap. When the map is
        /// freshly allocated (no refresh yet) we return true (assume-reachable) so
        /// guards do not fire on cold-start. A* fallback deferred; this is the cheap
        /// O(1) traversability gate.
        /// </summary>
        public static bool IsReachable(Vector3 origin, Vector3 dest, VehicleCapability capability)
        {
            var terrain = _terrainPub.Read();
            if (terrain == null) return true;
            if (terrain.IsFreshlyAllocated) return true;
            var oXZ = new Vector2(origin.x, origin.z);
            var dXZ = new Vector2(dest.x, dest.z);
            if (!terrain.IsTraversable(oXZ, capability)) return false;
            if (!terrain.IsTraversable(dXZ, capability)) return false;
            return true;
        }

        /// <summary>
        /// Returns true when (tech, dest-bucket) had a failed query within the last
        /// <paramref name="secondsBack"/>. Guards consult this to suppress repeat
        /// pathology fires on terrain the solver already knows is unreachable.
        /// </summary>
        public static bool LastQueryFailedRecently(TechId techId, Vector3 dest, float secondsBack)
        {
            long key = PackTechDestBucket(techId, dest);
            long stamp;
            if (!_lastFailedMono.TryGetValue(key, out stamp)) return false;
            float age = MonoClock.Seconds(stamp, MonoClock.Now());
            return age < secondsBack;
        }

        /// <summary>Stamps a (tech, dest-bucket) failed-query timestamp. LRU-evicts at capacity.</summary>
        public static void RecordQueryFailed(TechId techId, Vector3 dest)
        {
            long key = PackTechDestBucket(techId, dest);
            _lastFailedMono[key] = MonoClock.Now();
            if (_lastFailedMono.Count > LastFailedCapacity)
            {
                // Cheap LRU: drop one arbitrary stale entry (older than 5 min).
                long now = MonoClock.Now();
                foreach (var kv in _lastFailedMono)
                {
                    if (MonoClock.Seconds(kv.Value, now) > 300f)
                    {
                        long ignore;
                        _lastFailedMono.TryRemove(kv.Key, out ignore);
                        break;
                    }
                }
            }
        }

        public static void RequestPath(PathRequest req)
        {
            if (_requestQueue == null) return;
            // L-017: stamp enqueue timestamp via the internal ctor. If a caller pre-stamped
            // (test harness, replay), respect their value; otherwise mint a fresh one.
            if (req.EnqueueMonoTimestamp == 0L)
            {
                req = new PathRequest(req.Tech, req.MyTeam, req.Start, req.StartVelocity,
                    req.Goal, req.GoalVelocity, req.Duration, req.Capability, req.Priority,
                    MonoClock.Now());
            }
            _requestQueue.Enqueue(req);
        }

        public static Trajectory GetLastPath(TechId tech)
        {
            if (_lastPaths == null) return null;
            return _lastPaths.TryGetValue(tech, out var t) ? t : null;
        }

        /// <summary>
        /// Main-thread tick — invoked from SmartForm.Operations. Drives the periodic
        /// TerrainMap refresh (Unity's terrain API is not thread-safe per PATHING §3.2).
        /// </summary>
        public static void MainThreadTick()
        {
            var terrain = _terrainPub.Read();
            if (terrain != null && terrain.IsRefreshDue())
                terrain.RefreshFromMainThread();
        }

        // ---- Workers ----

        private static void ThreatFieldRebuildLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // Phase 5 (FIX-PLAN.md): host-authority gate. Threat-field rebuild is
                // host-authoritative; client doesn't run the MPC that would consume it.
                // L-026: pause-gate ahead of host-gate.
                if (SmartRuntime.IsPaused)
                {
                    if (token.WaitHandle.WaitOne(ThreatFieldTickMs)) return;
                    continue;
                }
                if (!SmartRuntime.IsHost)
                {
                    if (token.WaitHandle.WaitOne(ThreatFieldTickMs)) return;
                    continue;
                }
                try
                {
                    var beliefs = SmartRuntime.World?.FusedBuffer.Read();
                    if (beliefs != null)
                    {
                        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                        foreach (var team in SmartRuntime.EnumerateTeams())
                        {
                            var snap = ThreatFieldBuilder.Build(
                                beliefs, team.EnemyVehicleSnapshots(), team.TeamId, stamp);
                            var buf = _threatFields.GetOrAdd(team.TeamId,
                                _ => new DoubleBuffer<ThreatFieldSnapshot>(ThreatFieldSnapshot.Empty));
                            buf.Write(snap);
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                // L-024: explicit TAE catch ahead of generic Exception.
                catch (System.Threading.ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("ThreatFieldRebuild")
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Pathing.ThreatFieldLoop: " + ex.GetType().Name + ": " + ex.Message);
                }
                if (token.WaitHandle.WaitOne(ThreatFieldTickMs)) return;
            }
        }

        private static void PathSolveLoop(CancellationToken token)
        {
            var optimizer = new TrajectoryOptimizer();
            // L-018: singleton backpressure now lives on PathingService; this loop reads it
            // via the private field. External consumers go through the IPathingBackpressureReadout
            // accessor `PathingService.Backpressure`. Local null-guard for the rare race where
            // PathSolveLoop runs one tick after Shutdown nulled the field.
            var backpressure = _backpressure;
            if (backpressure == null) return;
            // P11 T7 Item 63: periodic [PATHING-SHED] telemetry log. Surfaces the previously-
            // orphaned DroppedSinceLastReport counter so an operator can see whether the
            // backpressure ever activated. Resets the counter on emission so the next report
            // window is a fresh measurement.
            const int DroppedLogIntervalMs = 60000;
            int lastDroppedLogMs = System.Environment.TickCount;
            while (!token.IsCancellationRequested)
            {
                // L-026: pause-gate ahead of host-gate.
                if (SmartRuntime.IsPaused)
                {
                    if (token.WaitHandle.WaitOne(SolveLoopIdleWaitMs)) return;
                    continue;
                }
                // Phase 5 (FIX-PLAN.md): host-authority gate.
                if (!SmartRuntime.IsHost)
                {
                    if (token.WaitHandle.WaitOne(SolveLoopIdleWaitMs)) return;
                    continue;
                }

                // P9 Item 30: observe queue depth before TryDequeue. The Observe call is
                // cheap (one fraction compare + optional time stamp); the actual drop
                // happens below if the dequeued request is low-priority during shed mode.
                backpressure.Observe(
                    _requestQueue.ApproximateCount, _requestQueue.Capacity, World.MonoClock.Now());

                // P11 T7 Item 63: emit [PATHING-SHED] log every DroppedLogIntervalMs window
                // when the dropped count is non-zero, then reset for the next window.
                int nowMs = System.Environment.TickCount;
                if (unchecked(nowMs - lastDroppedLogMs) >= DroppedLogIntervalMs)
                {
                    long dropped = backpressure.DroppedTotal;
                    if (dropped > 0L)
                    {
                        DebugTAC_AI.LogWarnFileOnly("pathing-shed",
                            "[PATHING-SHED] dropped " + dropped + " low-priority requests in the last "
                            + (DroppedLogIntervalMs / 1000) + "s (queue ApproxCount="
                            + _requestQueue.ApproximateCount + "/" + _requestQueue.Capacity + ")");
                        backpressure.ResetDroppedCounter();
                    }
                    // L-040: [PATHING-DEPTH] 60s observability log. Fires every window even
                    // when dropped == 0 so operator sees the trend before shed-mode activates.
                    DebugTAC_AI.LogWarnFileOnly("pathing-depth",
                        "[PATHING-DEPTH] window=" + (DroppedLogIntervalMs / 1000) + "s"
                        + " queue=" + _requestQueue.ApproximateCount + "/" + _requestQueue.Capacity
                        + " shedActive=" + backpressure.ShedActive
                        + " droppedTotal=" + backpressure.DroppedTotal
                        + " expiredTotal=" + backpressure.ExpiredTotal
                        + " p50=" + backpressure.SolveLatencyMsP50.ToString("0.0") + "ms"
                        + " p99=" + backpressure.SolveLatencyMsP99.ToString("0.0") + "ms");
                    lastDroppedLogMs = nowMs;
                }

                PathRequest req;
                if (!_requestQueue.TryDequeue(out req))
                {
                    if (token.WaitHandle.WaitOne(SolveLoopIdleWaitMs)) return;
                    continue;
                }

                // L-085: dequeue-side TTL expiry. Path requests older than RequestTtlMs are
                // dropped without solving — the requesting tech has likely moved far enough
                // that the path is stale. RecordExpired bumps the [PATHING-DEPTH] expired
                // counter so the operator can distinguish shed-drop from staleness-drop.
                const int RequestTtlMs = 2000;
                if (req.EnqueueMonoTimestamp != 0L)
                {
                    long ageMs = (long)(World.MonoClock.Seconds(req.EnqueueMonoTimestamp, MonoClock.Now()) * 1000f);
                    if (ageMs > RequestTtlMs)
                    {
                        backpressure.RecordExpired();
                        continue;
                    }
                }

                // P9 Item 30: shed low-priority work while shed mode is active.
                if (backpressure.ShedActive && req.Priority < 128)
                {
                    backpressure.RecordDropped();
                    continue;  // drop without solving; downstream consumer will re-request if needed
                }

                try
                {
                    var threatField = GetThreatField(req.MyTeam);
                    _lastPaths.TryGetValue(req.Tech, out var warm);
                    // L-041: time the solve for the p50/p99 reservoir.
                    var solveSw = System.Diagnostics.Stopwatch.StartNew();
                    // BUG-013 / BUG-050: pull the terrain via the atomic publication holder
                    // so the worker observes a fully-published TerrainMap rather than racing
                    // the OnWorldReset reference swap.
                    var terrainSnapshot = _terrainPub.Read();
                    var result = optimizer.Solve(
                        req.Start, req.StartVelocity, req.Goal, req.GoalVelocity,
                        req.Duration, threatField, terrainSnapshot, req.Capability,
                        warm, token);
                    solveSw.Stop();
                    backpressure.RecordSolveLatency((float)solveSw.Elapsed.TotalMilliseconds);
                    if (result?.Trajectory != null)
                    {
                        result.Trajectory.SolvedAtMono = MonoClock.Now();   // L-042
                        _lastPaths[req.Tech] = result.Trajectory;
                    }
                }
                catch (OperationCanceledException) { return; }
                // L-024: explicit TAE catch ahead of generic Exception.
                catch (System.Threading.ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("PathSolve")
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Pathing.SolveLoop: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }
}
