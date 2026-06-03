using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;

namespace TAC_AI.AI.Forms.Smart.Planning
{
    /// <summary>
    /// Per-team strategic planner. Per PLANNING-CONTRACT §7. Runs in a long-living
    /// worker that ticks every ~300 ms.
    /// </summary>
    public sealed class StrategicPlanner
    {
        public int MillisecondsPerTick { get; set; } = 300;

        private readonly PUCTSearch _puct = new PUCTSearch();
        private readonly DoubleBuffer<PlanLibrary.StrategicPlan> _planBuffer;
        private readonly System.Func<StrategicState> _readStateFn;

        public DoubleBuffer<PlanLibrary.StrategicPlan> PlanBuffer => _planBuffer;

        public StrategicPlanner(System.Func<StrategicState> readState)
        {
            _readStateFn = readState;
            _planBuffer = new DoubleBuffer<PlanLibrary.StrategicPlan>(PlanLibrary.StrategicPlan.Hold);
        }

        /// <summary>
        /// One planning step. Reads state, runs PUCT search, publishes the resulting plan.
        /// Exception-isolated: a failure logs and returns; the caller (daemon or RunLoop) keeps
        /// driving subsequent ticks.
        ///
        /// Aether/T2: extracted from RunLoop so a global daemon (<see cref="GlobalPlannerDaemon"/>)
        /// can drive every team's planner from one thread instead of 1 thread per team.
        /// </summary>
        public void PlanOnce(CancellationToken cancellation)
        {
            try
            {
                if (_readStateFn != null)
                {
                    var state = _readStateFn();
                    if (state != null)
                    {
                        var plan = _puct.Search(state, cancellation);
                        _planBuffer.Write(plan);
                    }
                }
            }
            catch (System.OperationCanceledException) { throw; }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.StrategicPlanner: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Long-running loop. Retained for single-team / test usage; production wiring now
        /// uses <see cref="GlobalPlannerDaemon"/> which calls <see cref="PlanOnce"/> per team.
        /// </summary>
        public void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(MillisecondsPerTick)) return;
                    continue;
                }
                try { PlanOnce(cancellation); }
                catch (System.OperationCanceledException) { return; }
                if (cancellation.WaitHandle.WaitOne(MillisecondsPerTick)) return;
            }
        }
    }
}
