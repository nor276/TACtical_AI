using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;

namespace TAC_AI.AI.Forms.Smart.Planning
{
    /// <summary>
    /// One global thread that drives every team's <see cref="StrategicPlanner.PlanOnce"/>.
    /// Per the 9-agent threading review (T2): replaces the prior pattern of one Planner
    /// thread per team, which under heavy load proliferated to 30+ long-running threads
    /// and contended with AetherFuser / Coordinator daemons for CPU.
    ///
    /// Cadence: 300 ms per cycle (matches the per-team Planner's prior MillisecondsPerTick).
    /// Each cycle iterates <see cref="SmartRuntime.EnumerateTeams"/> and calls PlanOnce on
    /// each team's planner. The cycle's wall-time is bounded by the slowest team's plan
    /// (PUCT search); if total > 300 ms, the next cycle starts immediately with no sleep.
    ///
    /// Host-gate, catch-and-continue, OperationCanceledException-exits — same shape as
    /// every other long-running RunLoop in Smart.
    /// </summary>
    public static class GlobalPlannerDaemon
    {
        public const int CyclePeriodMs = 300;

        // Aether/T4: circuit breaker. Trips on sustained exceptions; daemon exits cleanly.
        private static readonly CircuitBreaker _breaker = new CircuitBreaker("GlobalPlannerDaemon");

        public static void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                // L-026: IsPaused checked BEFORE IsHost so a paused host yields CPU even
                // though it retains authority. Pause is observable to the operator via the
                // [SMART-LIFECYCLE-PAUSE] log; daemon-side this is silent.
                if (SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
                    continue;
                }
                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
                    continue;
                }

                long cycleStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    foreach (var team in SmartRuntime.EnumerateTeams())
                    {
                        if (cancellation.IsCancellationRequested) return;
                        // L-045: skip non-Active teams (Draining/Disposed). Planner work is
                        // expensive (CMA-ES + MCTS); cheap fast-path skip pays for itself.
                        if (team.State != TeamLifecycleState.Active) continue;
                        team.Planner.PlanOnce(cancellation);
                        // L-062: emit one low-priority exploration probe per team per cycle.
                        // Priority=64 (< default 128) so PathRequestBackpressure has real
                        // work to drop when shed-mode activates. The probe target is a
                        // forward-looking 100m offset from the team's centroid — close
                        // enough that real paths might already cover it, distant enough that
                        // when paths are scarce the solver actually computes something.
                        try { TryEmitExplorationProbe(team); }
                        catch (System.Exception ex)
                        {
                            DebugTAC_AI.LogWarning("Smart.GlobalPlannerDaemon: probe failed: " + ex.Message);
                        }
                    }
                }
                catch (System.OperationCanceledException) { return; }
                // L-024: explicit TAE catch ahead of generic Exception.
                catch (System.Threading.ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("GlobalPlanner")
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (System.Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.GlobalPlannerDaemon: " + ex.GetType().Name + ": " + ex.Message);
                    if (_breaker.Tripped()) return;
                }

                long cycleEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedMs = (cycleEnd - cycleStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                int sleepMs = CyclePeriodMs - (int)elapsedMs;
                if (sleepMs > 0)
                {
                    if (cancellation.WaitHandle.WaitOne(sleepMs)) return;
                }
                // else: skipped sleep — cycle took longer than CyclePeriodMs; start next cycle immediately.
            }
        }

        /// <summary>
        /// L-062: best-effort low-priority exploration probe for backpressure observability.
        /// Picks any tech on the team as the requestor; targets a forward offset from its
        /// position. Priority=64 (below normal 128) so the shed branch in PathSolveLoop
        /// actually has work to drop when the queue saturates. Silent no-op on any error.
        /// </summary>
        private static void TryEmitExplorationProbe(TeamRuntime team)
        {
            if (team == null) return;
            foreach (var techId in team.SnapshotTechIds())
            {
                var state = team.TryGetTechState(techId);
                if (state == null) continue;
                var snapshot = state.VehicleBuffer?.Read();
                if (snapshot == null) continue;
                var kin = snapshot.Kinematics;
                UnityEngine.Vector3 pos = kin.PositionWorld;
                UnityEngine.Vector3 vel = kin.VelocityWorld;
                UnityEngine.Vector3 heading = kin.HeadingWorld;
                if (heading.sqrMagnitude < 0.001f) heading = UnityEngine.Vector3.forward;
                UnityEngine.Vector3 goal = pos + heading.normalized * 100f;
                var capability = TAC_AI.AI.Forms.Smart.Pathing.VehicleCapability.FromMobility(snapshot.Mobility);
                var req = new Pathing.PathRequest(
                    techId, team.TeamId,
                    pos, vel,
                    goal, UnityEngine.Vector3.zero,
                    duration: 5f,
                    capability: capability,
                    priority: 64);   // < 128 = sheddable
                Pathing.PathingService.RequestPath(req);
                return;   // one probe per team per cycle is enough
            }
        }
    }
}
