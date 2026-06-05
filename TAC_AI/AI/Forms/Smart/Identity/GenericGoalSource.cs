using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    /// <summary>
    /// P6 Item 27 (REV 5/7): default goal source for <see cref="SmartIdentity.Generic"/>.
    /// Stateless singleton dispatcher; delegates to the per-controller TacticalOptimizer via
    /// <see cref="IdentityContext.TacticalGoalHandle"/> (set up by ContinuousController at
    /// dispatch time via <see cref="IdentityContext.WithTacticalGoalHandle"/>). Adam state
    /// remains per-controller on <c>ContinuousController._tactical</c>; no contamination.
    ///
    /// Behavior preservation (call-equivalence proof):
    ///   v0.1 inline bypass: <c>_tactical.Step(ownBelief, vehicle, beliefs)</c>
    ///   v0.2 registered:    <c>src.Produce → ctx.TacticalGoalHandle.Invoke → _tactical.Step(ownBelief, vehicle, beliefs)</c>
    /// Same _tactical instance, same Step call, same 3 args, same Adam state. ~10ns delta.
    /// </summary>
    public sealed class GenericGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.Generic;

        public TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                                    BeliefSnapshot beliefs, IdentityContext ctx)
        {
            if (ctx.TacticalGoalHandle != null)
                return ctx.TacticalGoalHandle.Invoke(ownBelief, vehicle, beliefs);

            // S1 SAFETY (REV 5): defensive at-current goal rather than default(TacticalGoal).
            // The ContinuousController fallback at dispatch (src == null branch) already
            // catches the unregistered case; this catches the (theoretically-unreachable but
            // cheap-to-guard) case of src != null with handle == null — e.g. if a non-
            // ContinuousController call site ever invokes Produce without wrapping the ctx.
            return TacticalGoal.AtCurrent(ownBelief.PositionMean, ownBelief.HeadingMean);
        }
    }
}
