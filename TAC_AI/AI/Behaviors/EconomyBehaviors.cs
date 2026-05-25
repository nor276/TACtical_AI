using TAC_AI.AI;
using TAC_AI.AI.Engine;
using TAC_AI.AI.AlliedOperations;
using TAC_AI.AI.Enemy.EnemyOperations;

namespace TAC_AI.AI.Behaviors
{
    // MERGED twin economy behaviors (Decision 2): one module per concept, alignment-dispatched to the preserved
    // path - enemy uses the R* handler, ally uses the aim + B* handler. No body rewrite. Allied profiles wire
    // these as the movement role (DediAI Prospector/Scrapper); enemy economy by default still flows through the
    // attitude router (EnemyNoTargetIdle -> LollyGag), but the enemy path here lets a profile drive it directly.

    internal sealed class MineBehavior : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Economy;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Economy.Mine";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            if (c.IsEnemy)
                RMiner.MineYerOwnBusiness(c.Helper, c.Tank, c.Mind, ref op);
            else
            {
                BGeneral.SelfDefend(c.Helper, c.Tank);
                BProspector.MotivateMine(c.Helper, c.Tank, ref op);
            }
        }
    }

    internal sealed class ScavengeBehavior : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Economy;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Economy.Scavenge";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            if (c.IsEnemy)
                RScavenger.Scavenge(c.Helper, c.Tank, c.Mind, ref op);
            else
            {
                BGeneral.SelfDefend(c.Helper, c.Tank);
                BScrapper.MotivateFind(c.Helper, c.Tank, ref op);
            }
        }
    }
}
