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
                        team.Planner.PlanOnce(cancellation);
                    }
                }
                catch (System.OperationCanceledException) { return; }
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
    }
}
