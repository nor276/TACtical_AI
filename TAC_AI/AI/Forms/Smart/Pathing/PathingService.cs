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

        public PathRequest(TechId tech, TeamId myTeam,
            Vector3 start, Vector3 startVel,
            Vector3 goal, Vector3 goalVel,
            float duration, VehicleCapability capability)
        {
            Tech = tech;
            MyTeam = myTeam;
            Start = start;
            StartVelocity = startVel;
            Goal = goal;
            GoalVelocity = goalVel;
            Duration = duration;
            Capability = capability;
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

        public static TerrainMap CurrentTerrain => _terrain;
        public static bool IsRunning => Volatile.Read(ref _running) == 1;

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

        public static void RequestPath(PathRequest req)
        {
            if (_requestQueue == null) return;
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
            while (!token.IsCancellationRequested)
            {
                // Phase 5 (FIX-PLAN.md): host-authority gate.
                if (!SmartRuntime.IsHost)
                {
                    if (token.WaitHandle.WaitOne(SolveLoopIdleWaitMs)) return;
                    continue;
                }
                PathRequest req;
                if (!_requestQueue.TryDequeue(out req))
                {
                    if (token.WaitHandle.WaitOne(SolveLoopIdleWaitMs)) return;
                    continue;
                }
                try
                {
                    var threatField = GetThreatField(req.MyTeam);
                    _lastPaths.TryGetValue(req.Tech, out var warm);
                    var result = optimizer.Solve(
                        req.Start, req.StartVelocity, req.Goal, req.GoalVelocity,
                        req.Duration, threatField, _terrain, req.Capability,
                        warm, token);
                    if (result?.Trajectory != null)
                        _lastPaths[req.Tech] = result.Trajectory;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Pathing.SolveLoop: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }
}
