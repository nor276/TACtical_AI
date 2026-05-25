using TAC_AI.AI.Enemy;

namespace TAC_AI.AI
{
    internal enum MovementContainerKind { Default, Air, Static }
    internal enum MovementCoreKind { None, Land, Sea, Space }

    /// REVISED (overview): NEW — single switch-based mapping table that routes (DriverType / EnemyHandling)
    /// to an outer container kind and an inner core kind. Replaces the scattered inline if/switch decisions
    /// the old combined VehicleAICore made; the outer layer (RecalMoveAIController*) reads ContainerFor*,
    /// AIControllerDefault.SelectCore reads CoreFor*. Coarse outer split (most types collapse into Default),
    /// fine inner split via CoreFor* (Land/Sea/Space, None = unmapped -> Land+warn fallback at the call site).
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
                case AIDriverType.Stationary: return MovementCoreKind.Space;
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
                case EnemyHandling.Stationary:     return MovementCoreKind.Space;
                default:                           return MovementCoreKind.None;
            }
        }
    }
}
