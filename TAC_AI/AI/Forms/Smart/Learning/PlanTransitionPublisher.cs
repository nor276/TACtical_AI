using TAC_AI.AI.Forms.Smart.Planning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P7 Item 17: publisher for <see cref="PlanTransition"/> events. Called from
    /// <c>Coordinator.TickOnce</c> after the unified StateBuffer.Write whenever the
    /// observed plan type changes (edge tracker on the Coordinator side per REV 2 C10 fix).
    ///
    /// Wraps <see cref="WorldEventBus.PublishFromWorker"/> because Coordinator runs on
    /// <c>GlobalCoordinatorDaemon</c> per SmartRuntime.cs:548 — events must marshal to
    /// the main thread via the relay queue (EventBus.cs:218-221).
    ///
    /// v0.2 reward source: 0f placeholder. Real reward (e.g. plan-outcome estimate from
    /// ActionValueEstimator) computed by a separate observation pass — documented gap.
    /// </summary>
    public static class PlanTransitionPublisher
    {
        public static void OnPlanPublished(TeamId team, PlanLibrary.PlanType prior,
            PlanLibrary.PlanType next, float reward, long tickMono)
        {
            WorldEventBus.PublishFromWorker(new PlanTransition(team, prior, next, reward, tickMono));
        }
    }
}
