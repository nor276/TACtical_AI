using TAC_AI.AI;
using TAC_AI.AI.Engine;
using TAC_AI.AI.Enemy.EnemyOperations;

namespace TAC_AI.AI.Behaviors
{
    // Enemy combat (Attack-category) behaviors - one per locomotion, wrapping the preserved R* attack handlers.
    // These are the EvilCommander-keyed movement-slot behaviors the profile runner ticks for an enemy with a
    // live target. Logic is unchanged: each Tick forwards to the existing R*.AttackXxx(helper, tank, mind, ref op).
    // Enemy-only (require an EnemyMind); allied combat is a separate module (Decision 2 - combat does not merge).

    internal sealed class EnemyWheeledAttack : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Land;
        public override string Id => "Enemy.Attack.Wheeled";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RWheeled.AttackVroom(c.Helper, c.Tank, c.Mind, ref op);
        }
    }

    internal sealed class EnemyNavalAttack : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Sea;
        public override string Id => "Enemy.Attack.Naval";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RNaval.AttackWhish(c.Helper, c.Tank, c.Mind, ref op);
        }
    }

    internal sealed class EnemyStarshipAttack : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Space;
        public override string Id => "Enemy.Attack.Starship";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RStarship.AttackZoom(c.Helper, c.Tank, c.Mind, ref op);
        }
    }

    internal sealed class EnemyChopperAttack : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Air;
        public override string Id => "Enemy.Attack.Chopper";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RChopper.AttackShwa(c.Helper, c.Tank, c.Mind, ref op);
        }
    }

    internal sealed class EnemyAircraftAttack : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Air;
        public override string Id => "Enemy.Attack.Aircraft";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RAircraft.AttackWoosh(c.Helper, c.Tank, c.Mind, ref op);
        }
    }

    internal sealed class EnemyStationAttack : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Static;
        public override string Id => "Enemy.Attack.Station";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RStation.AttackWham(c.Helper, c.Tank, c.Mind, ref op);
        }
    }

    internal sealed class EnemyCrashMissileAttack : BehaviorBase
    {
        public override AIBehaviorCategory Category => AIBehaviorCategory.Attack;
        public override VehicleClass VehicleClass => VehicleClass.Shared;   // ram - loco-agnostic
        public override string Id => "Enemy.Attack.CrashMissile";
        public override void Tick(IAIContext ctx)
        {
            var c = C(ctx);
            ref EControlOperatorSet op = ref c.OperatorRef();
            RCrashMissile.AttackCrash(c.Helper, c.Tank, c.Mind, ref op);
        }
    }
}
