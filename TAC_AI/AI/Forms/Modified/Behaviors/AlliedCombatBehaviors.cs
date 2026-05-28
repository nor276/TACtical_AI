using TAC_AI.AI;
using TAC_AI.AI.Engine;
using TAC_AI.AI.AlliedOperations;

namespace TAC_AI.AI.Behaviors
{
    // Allied combat/escort behaviors. Each Tick reproduces exactly one AlliedOperationsController switch case
    // (the aim call + the movement call), so behavior is preserved. Escort dispatches by DriverType internally
    // (the Escort inner switch - one cohesive escort module across loco). Allied combat does NOT merge with enemy
    // combat (Decision 2): the tactical models differ (escort-player vs attack-archetype).

    internal sealed class AlliedEscort : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Support;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Allied.Escort";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            var helper = c.Helper;
            var tank = c.Tank;
            ref EControlOperatorSet op = ref c.OperatorRef();
            switch (helper.DriverType)
            {
                case AIDriverType.Tank:
                    BGeneral.AidDefend(helper, tank);
                    BEscort.MotivateMove(helper, tank, ref op);
                    break;
                case AIDriverType.Astronaut:
                    BGeneral.AidDefend(helper, tank);
                    BAstrotech.MotivateSpace(helper, tank, ref op);
                    break;
                case AIDriverType.Sailor:
                    BGeneral.AidDefend(helper, tank);
                    BBuccaneer.MotivateBote(helper, tank, ref op);
                    break;
                case AIDriverType.Pilot:
                    BAviator.Dogfighting(helper, tank);
                    BAviator.MotivateFly(helper, tank, ref op);
                    break;
                case AIDriverType.AutoSet:
                    DebugTAC_AI.LogError(KickStart.ModID + ": AutoSet reached dispatch for "
                        + helper.tank.name + " — upstream resolve missing. DediAI=" + helper.DediAI);
                    helper.ExecuteAutoSetNoCalibrate();
                    break;
                default:
                    DebugTAC_AI.Log(KickStart.ModID + ": AIDriver is set to an invalid state - " + helper.DriverType);
                    DebugTAC_AI.Log(KickStart.ModID + ": RESETTING TO DEFAULTS");
                    helper.SetDriverType(AIDriverType.Tank);
                    break;
            }
        }
    }

    internal sealed class AlliedAssault : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Allied.Assault";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            BAssassin.ShootToDestroy(c.Helper, c.Tank);
            BAssassin.MotivateKill(c.Helper, c.Tank, ref op);
        }
    }
}
