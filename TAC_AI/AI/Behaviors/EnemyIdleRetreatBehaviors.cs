using TAC_AI.AI;
using TAC_AI.AI.Engine;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Behaviors
{
    // Enemy no-target / retreat behaviors (the pre-pass and post-pass slots the EnemyOperationsController ran
    // outside its EvilCommander switch). NoTargetIdle re-acquires/idles and, by CommanderMind attitude, routes
    // internally (RGeneral.LollyGag) to mine/scavenge/guard. Retreat seeks HQ / flees the last enemy. Enemy-only.

    internal sealed class EnemyNoTargetIdle : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Idle;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Enemy.Idle.NoTarget";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RGeneral.DispatchNoTargetIdle(c.Helper, c.Tank, c.Mind, ref op);
        }
    }

    internal sealed class EnemyRetreat : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Retreat;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Enemy.Retreat";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RCore.GetRetreatLocation(c.Helper, c.Tank, c.Mind, ref op);
        }
    }
}
