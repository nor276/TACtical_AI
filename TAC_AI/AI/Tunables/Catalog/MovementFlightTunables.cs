namespace TAC_AI.AI.Tunables.Catalog
{
    /// <summary>
    /// Step 6 batch 2b - movement/avoidance multipliers, pathing/anchor, flight + dive-FSM params. const->static
    /// + bind, defaults = AIGlobals originals (behavior preserved). Registered from AIModuleBootstrap.InitAIModules.
    /// </summary>
    public static class MovementFlightTunables
    {
        public static void Register()
        {
            // ---- movement / avoidance ----
            TunableRegistry.RegisterFloat("Movement.DefaultDodgeStrengthMultiplier", "Movement", 1.75f, 0f, 5f, 0.05f,
                menuLabel: "Dodge strength", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.DefaultDodgeStrengthMultiplier = v);
            TunableRegistry.RegisterFloat("Movement.AirborneDodgeStrengthMultiplier", "Movement", 0.4f, 0f, 5f, 0.05f,
                menuLabel: "Airborne dodge strength", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.AirborneDodgeStrengthMultiplier = v);
            TunableRegistry.RegisterFloat("Movement.PlayerAISpeedPanicDividend", "Movement", 8f, 1f, 30f, 1f,
                menuLabel: "Player speed-panic divisor", display: v => v.ToString("0"),
                bind: v => AIGlobals.PlayerAISpeedPanicDividend = v);
            TunableRegistry.RegisterFloat("Movement.EnemyAISpeedPanicDividend", "Movement", 9f, 1f, 30f, 1f,
                menuLabel: "Enemy speed-panic divisor", display: v => v.ToString("0"),
                bind: v => AIGlobals.EnemyAISpeedPanicDividend = v);
            TunableRegistry.RegisterFloat("Movement.BoosterThrustBias", "Movement", 0.5f, 0f, 1f, 0.05f,
                menuLabel: "Booster thrust bias", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.BoosterThrustBias = v);

            // ---- pathing / anchor ----
            TunableRegistry.RegisterFloat("Pathing.AIPathingSuccessRad", "Pathing", 2.4f, 0.5f, 10f, 0.1f,
                menuLabel: "Pathing success radius", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.AIPathingSuccessRad = v);
            TunableRegistry.RegisterFloat("Unjam.BeamFlipTippedHoldSecs", "Pathing", 1.5f, 0f, 10f, 0.5f,
                menuLabel: "Unjam beam tipped-hold (s)", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.BeamFlipTippedHoldSecs = v);
            TunableRegistry.RegisterInt("Anchor.AnchorAimDampening", "Anchor", 45, 1, 180, 5,
                menuLabel: "Anchor aim dampening", bind: v => AIGlobals.AnchorAimDampening = v);

            // ---- flight ----
            TunableRegistry.RegisterFloat("Flight.GroundOffsetGeneralAir", "Flight", 10f, 0f, 100f, 1f,
                menuLabel: "Air ground offset", display: v => v.ToString("0"),
                bind: v => AIGlobals.GroundOffsetGeneralAir = v);
            TunableRegistry.RegisterFloat("Flight.GroundOffsetAircraft", "Flight", 22f, 0f, 100f, 1f,
                menuLabel: "Aircraft ground offset", display: v => v.ToString("0"),
                bind: v => AIGlobals.GroundOffsetAircraft = v);
            TunableRegistry.RegisterFloat("Flight.GroundOffsetChopper", "Flight", 8.5f, 0f, 100f, 0.5f,
                menuLabel: "Chopper ground offset", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.GroundOffsetChopper = v);
            TunableRegistry.RegisterFloat("Flight.AirStallSpeed", "Flight", 42f, 0f, 200f, 1f,
                menuLabel: "Aircraft stall speed", display: v => v.ToString("0"),
                bind: v => AIGlobals.AirStallSpeed = v);
            TunableRegistry.RegisterFloat("Flight.AirMaxYaw", "Flight", 0.45f, 0f, 1f, 0.05f,
                menuLabel: "Aircraft max yaw", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.AirMaxYaw = v);
            TunableRegistry.RegisterFloat("Flight.AirMaxYawBankOnly", "Flight", 0.75f, 0f, 1f, 0.05f,
                menuLabel: "Aircraft max yaw (bank-only)", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.AirMaxYawBankOnly = v);
            TunableRegistry.RegisterFloat("Flight.ImmelmanTtWRThreshold", "Flight", 1.5f, 0f, 5f, 0.1f,
                menuLabel: "Immelmann thrust:weight threshold", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.ImmelmanTtWRThreshold = v);

            // ---- dive FSM ----
            TunableRegistry.RegisterFloat("Flight.MinDiveAGL", "Flight", 60f, 0f, 500f, 5f,
                menuLabel: "Min dive altitude (AGL)", display: v => v.ToString("0"),
                bind: v => AIGlobals.MinDiveAGL = v);
            TunableRegistry.RegisterFloat("Flight.MinRecoverHold", "Flight", 1.5f, 0f, 15f, 0.5f,
                menuLabel: "Dive recover hold min (s)", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.MinRecoverHold = v);
            TunableRegistry.RegisterFloat("Flight.MaxRecoverHold", "Flight", 8f, 0f, 30f, 0.5f,
                menuLabel: "Dive recover hold max (s)", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.MaxRecoverHold = v);
            TunableRegistry.RegisterFloat("Flight.PostRecoverCooldown", "Flight", 2f, 0f, 15f, 0.5f,
                menuLabel: "Post-dive cooldown (s)", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.PostRecoverCooldown = v);
        }
    }
}
