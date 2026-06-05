using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// One global thread that drives every team's <see cref="Coordinator.StepOnce"/>.
    /// Per the 9-agent threading review (T2): collapses the prior pattern of one
    /// Coordinator thread per team to a single daemon.
    ///
    /// Cadence: 300 ms per cycle (matches the per-team Coordinator's prior MillisecondsPerTick).
    /// Each cycle iterates <see cref="SmartRuntime.EnumerateTeams"/> and calls StepOnce on
    /// each team's coordinator. Sleep-budget logic identical to <see cref="Planning.GlobalPlannerDaemon"/>.
    /// </summary>
    public static class GlobalCoordinatorDaemon
    {
        public const int CyclePeriodMs = 300;

        // Aether/T4: circuit breaker. Trips on sustained exceptions; daemon exits cleanly.
        private static readonly CircuitBreaker _breaker = new CircuitBreaker("GlobalCoordinatorDaemon");

        public static void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                // L-026: pause-gate ahead of host-gate. See GlobalPlannerDaemon for rationale.
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
                        // L-045: skip teams that are not Active. Draining teams stop
                        // receiving new work even though they still own state; Disposed
                        // shouldn't appear (reaper TryRemove'd them) but the guard is cheap.
                        if (team.State != TeamLifecycleState.Active) continue;
                        team.Coordinator.StepOnce();
                    }
                }
                catch (System.OperationCanceledException) { return; }
                // L-024: explicit TAE catch ahead of generic Exception.
                catch (System.Threading.ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("GlobalCoordinator")
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (System.Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.GlobalCoordinatorDaemon: " + ex.GetType().Name + ": " + ex.Message);
                    if (_breaker.Tripped()) return;
                }

                long cycleEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedMs = (cycleEnd - cycleStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                int sleepMs = CyclePeriodMs - (int)elapsedMs;
                if (sleepMs > 0)
                {
                    if (cancellation.WaitHandle.WaitOne(sleepMs)) return;
                }
            }
        }
    }
}
