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
        private static TerrainMap _terrain;
        private static ConcurrentDictionary<TeamId, DoubleBuffer<ThreatFieldSnapshot>> _threatFields;
        private static BoundedQueue<PathRequest> _requestQueue;
        private static ConcurrentDictionary<TechId, Trajectory> _lastPaths;
        private static int _running; // 0 = stopped, 1 = running
        // L-018: singleton backpressure. Constructed in Init, nulled in Shutdown. PathSolveLoop
        // reads via the field directly (same-assembly internal access); external consumers
        // (debug GUI, console, watchdog) read the read-only interface via the Backpressure
        // accessor below. Volatile read so accessors see Init→Shutdown transitions promptly.
        private static TAC_AI.AI.Forms.Smart.Threading.PathRequestBackpressure _backpressure;

        public static TerrainMap CurrentTerrain => _terrain;
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
            _terrain = new TerrainMap();
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
            WorldResetRegistry.Register(bp);
            WorldResetRegistry.RegisterLambda("PathingService.ThreatFields",
                () => _threatFields?.Clear());
            WorldResetRegistry.RegisterLambda("PathingService.LastPaths",
                () => _lastPaths?.Clear());

            _pool.EnqueueLongRunning(ThreatFieldRebuildLoop, "ThreatFieldRebuild");
            _pool.EnqueueLongRunning(PathSolveLoop, "PathSolve");
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
            _terrain = null;
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
            // Rebuild a fresh TerrainMap so the next refresh samples the new world's
            // terrain — keeping the old grid would treat the new mission's elevations
            // as the old mission's heights (AUDIT-R2 §2.R2.B TerrainMap origin frozen).
            _terrain = new TerrainMap();
        }

        // ---- Public query / submit API (any thread) ----

        public static IThreatField GetThreatField(TeamId team)
        {
            if (_threatFields == null) return new ThreatField(ThreatFieldSnapshot.Empty, _terrain);
            var buf = _threatFields.GetOrAdd(team,
                _ => new DoubleBuffer<ThreatFieldSnapshot>(ThreatFieldSnapshot.Empty));
            return new ThreatField(buf.Read(), _terrain);
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
            if (_terrain != null && _terrain.IsRefreshDue())
                _terrain.RefreshFromMainThread();
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
                    var result = optimizer.Solve(
                        req.Start, req.StartVelocity, req.Goal, req.GoalVelocity,
                        req.Duration, threatField, _terrain, req.Capability,
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
