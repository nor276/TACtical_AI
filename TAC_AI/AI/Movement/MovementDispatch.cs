using TAC_AI.AI.Enemy;

namespace TAC_AI.AI
{
    internal enum MovementContainerKind { Default, Air, Static }
    // None = the enum value isn't mapped to a Default-container core (caller warns + falls back to Land).
    internal enum MovementCoreKind { None, Land, Sea, Space }

    // Single source of truth for the movement-class mapping that used to be duplicated across two
    // layers. The outer container choice (TankAIHelper.RecalMoveAIController*) consults ContainerFor*;
    // the inner Default-container core choice (AIControllerDefault.SelectCore) consults CoreFor*.
    //   - The Air container picks its own sub-core (Helicopter/VTOL/Airplane) from thrust geometry.
    //   - The Static container always uses StaticAICore.
    // Anchor-state gating and the Grounded-demote escape hatch are NOT pure functions of the enum, so
    // they stay in RecalMoveAIController*; this table only owns the (enum -> class) part.
    internal static class MovementDispatch
    {
        public static MovementContainerKind ContainerForPlayer(AIDriverType driver)
        {
            switch (driver)
            {
                case AIDriverType.Stationary: return MovementContainerKind.Static;
                case AIDriverType.Pilot:      return MovementContainerKind.Air;
                default:                      return MovementContainerKind.Default;
            }
        }

        public static MovementContainerKind ContainerForEnemy(EnemyHandling handling)
        {
            switch (handling)
            {
                case EnemyHandling.Stationary: return MovementContainerKind.Static;
                case EnemyHandling.Chopper:
                case EnemyHandling.Airplane:   return MovementContainerKind.Air;
                default:                       return MovementContainerKind.Default;
            }
        }

        public static MovementCoreKind CoreForPlayer(AIDriverType driver)
        {
            switch (driver)
            {
                case AIDriverType.AutoSet:
                case AIDriverType.Tank:       return MovementCoreKind.Land;
                case AIDriverType.Sailor:     return MovementCoreKind.Sea;
                case AIDriverType.Astronaut:
                case AIDriverType.Stationary: return MovementCoreKind.Space; // unanchored-stationary fallback
                default:                      return MovementCoreKind.None;
            }
        }

        public static MovementCoreKind CoreForEnemy(EnemyHandling handling)
        {
            switch (handling)
            {
                case EnemyHandling.Wheeled:        return MovementCoreKind.Land;
                case EnemyHandling.Naval:          return MovementCoreKind.Sea;
                case EnemyHandling.Starship:
                case EnemyHandling.SuicideMissile:
                case EnemyHandling.Stationary:     return MovementCoreKind.Space; // unanchored-stationary fallback
                default:                           return MovementCoreKind.None;
            }
        }
    }
}
