using TAC_AI.AI;
using TAC_AI.AI.Engine;
using TAC_AI.AI.AlliedOperations;
using TAC_AI.AI.Enemy.EnemyOperations;

namespace TAC_AI.AI.Behaviors
{
    // MERGED twin Protect behavior (Decision 2): RGuardian (enemy) and BAegis (allied) are ~95% identical
    // (anchor/fire gates differ by alignment/smarts, handled inside the preserved handlers). One module,
    // alignment-dispatched. Allied profiles wire it as the movement role (DediAI Aegis); enemy guardians by
    // default flow through the attitude router, with this enemy path available for direct profile assignment.

    internal sealed class ProtectBehavior : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Support;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Support.Protect";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            if (c.IsEnemy)
                RGuardian.MotivateDefend(c.Helper, c.Tank, c.Mind, ref op);
            else
            {
                BGeneral.AidDefend(c.Helper, c.Tank);
                BAegis.MotivateProtect(c.Helper, c.Tank, ref op);
            }
        }
    }
}
