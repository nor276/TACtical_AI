using TAC_AI.AI;
using TAC_AI.AI.Engine;
using TAC_AI.AI.AlliedOperations;

namespace TAC_AI.AI.Behaviors
{
    // Allied support behaviors (charge / multitech / stationary base hold). Each reproduces one
    // AlliedOperationsController case verbatim (aim call + movement call). MTTurret and MTStatic shared an
    // identical body, so one module covers both. Base hold/assault are the DriverType==Stationary sub-cases.

    internal sealed class AlliedEnergizer : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Support;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Allied.Energizer";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            BGeneral.SelfDefend(c.Helper, c.Tank);
            BEnergizer.MotivateCharge(c.Helper, c.Tank, ref op);
        }
    }

    internal sealed class AlliedMultiTechStatic : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Support;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Allied.MultiTech.Static";   // covers both MTTurret and MTStatic (identical bodies)
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            BMultiTech.MimicDefend(c.Helper, c.Tank);
            BMultiTech.MTStatic(c.Helper, c.Tank, ref op);
            BMultiTech.BeamLockWithinBounds(c.Helper, c.Tank);
        }
    }

    internal sealed class AlliedMultiTechMimic : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Support;
        public override VehicleClass VehicleClass => VehicleClass.Shared;
        public override string Id => "Allied.MultiTech.Mimic";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            BMultiTech.MimicAllClosestAlly(c.Helper, c.Tank, ref op);
        }
    }

    internal sealed class AlliedBaseHold : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Support;
        public override VehicleClass VehicleClass => VehicleClass.Static;
        public override string Id => "Allied.Base.Hold";   // DriverType==Stationary, non-Assault
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            BBase.HoldProtect(c.Helper, c.Tank, ref op);
            BGeneral.AidDefend(c.Helper, c.Tank);
        }
    }

    internal sealed class AlliedBaseAssault : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Static;
        public override string Id => "Allied.Base.Assault";   // DriverType==Stationary, DediAI==Assault
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            BAssassin.ShootToDestroy(c.Helper, c.Tank);
            BBase.HoldProtect(c.Helper, c.Tank, ref op);
        }
    }
}
