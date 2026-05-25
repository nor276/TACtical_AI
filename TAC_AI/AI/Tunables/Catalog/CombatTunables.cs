namespace TAC_AI.AI.Tunables.Catalog
{
    /// <summary>
    /// The first migrated batch of high-value AI-feel tunables - "the files that begin the process". Each was an
    /// AIGlobals const, now a mutable static field; the registration binds the registry value back to that static
    /// (bind), so existing readers of AIGlobals.X see tuned values without being repointed. Defaults equal the
    /// original AIGlobals values, so bind(default) at boot is a no-op = behavior preserved. menuVisible/menuLabel
    /// curate the in-game menu; readSites is the no-orphan map. More batches register here / in sibling catalogs.
    /// </summary>
    public static class CombatTunables
    {
        // Note: registrations discard their handle (readers consume the value via the bound AIGlobals static,
        // not via a handle) - matches RangeTimingTunables / MovementFlightTunables. No stored handle fields.
        public static void Register()
        {
            // ---- Combat ----
            TunableRegistry.RegisterFloat(
                "Combat.ReverseInnerFraction", "Combat", 0.35f, 0f, 1f, 0.05f,
                menuLabel: "Reverse stand-off fraction", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.CombatReverseInnerFraction = v,
                readSites: new[] { "LandAICore.TryAdjustForCombat(Enemy): reverseInner band" });

            TunableRegistry.RegisterFloat(
                "Combat.ContinuousStrafeMinTurretFraction", "Combat", 0.5f, 0f, 1f, 0.05f,
                menuLabel: "Min turret fraction to strafe", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.ContinuousStrafeMinTurretFraction = v,
                readSites: new[] { "RWheeled.Circle: gate ShouldForceContinuousStrafe" });

            TunableRegistry.RegisterFloat(
                "Combat.MinCombatRangeDefault", "Combat", 12f, 0f, 100f, 1f,
                menuLabel: "Default min combat range", display: v => v.ToString("0"),
                bind: v => AIGlobals.MinCombatRangeDefault = v,
                readSites: new[] { "enemy/ally combat-spacing default" });

            TunableRegistry.RegisterInt(
                "Combat.DefaultEnemyMaxCombatRange", "Combat", 150, 20, 500, 5,
                menuLabel: "Default enemy max combat range",
                bind: v => AIGlobals.DefaultEnemyMaxCombatRange = v,
                readSites: new[] { "enemy combat-chase default" });

            TunableRegistry.RegisterInt(
                "Combat.PassiveMaxCombatRange", "Combat", 75, 10, 300, 5,
                menuLabel: "Passive max combat range",
                bind: v => AIGlobals.PassiveMaxCombatRange = v,
                readSites: new[] { "passive/non-aggressive combat-chase" });

            // ---- Retreat / damage ----
            TunableRegistry.RegisterInt(
                "Combat.DamageAlertThreshold", "Retreat", 45, 0, 500, 5,
                menuLabel: "Damage-alert (provoke) threshold",
                bind: v => AIGlobals.DamageAlertThreshold = v,
                readSites: new[] { "OnHit: single-hit provoke trip" });

            TunableRegistry.RegisterFloat(
                "Combat.RetreatBelowTechDamageThreshold", "Retreat", 50f, 0f, 100f, 5f,
                menuLabel: "Retreat below tech health %", display: v => v.ToString("0"),
                bind: v => AIGlobals.RetreatBelowTechDamageThreshold = v,
                readSites: new[] { "DetermineRetreatPosture: tech-damage gate" });

            TunableRegistry.RegisterFloat(
                "Combat.RetreatBelowTeamDamageThreshold", "Retreat", 30f, 0f, 100f, 5f,
                menuLabel: "Retreat below team health %", display: v => v.ToString("0"),
                bind: v => AIGlobals.RetreatBelowTeamDamageThreshold = v,
                readSites: new[] { "DetermineRetreatPosture: team-damage gate" });

            TunableRegistry.RegisterInt(
                "Combat.ProvokeTime", "Combat", 200, 0, 1000, 10,
                menuLabel: "Provoke duration (ticks)",
                bind: v => AIGlobals.ProvokeTime = v,
                readSites: new[] { "Provoked set on hit / manual target" });

            TunableRegistry.RegisterFloat(
                "Combat.LOSLostGraceTime", "Combat", 3f, 0f, 15f, 0.5f,
                menuLabel: "LOS-lost grace (s)", display: v => v.ToString("0.0") + "s",
                bind: v => AIGlobals.LOSLostGraceTime = v,
                readSites: new[] { "UpdateTargetCombatFocus: hold target behind cover" });

            // ---- Targeting / aim ----
            TunableRegistry.RegisterFloat(
                "Combat.TargetVelocityLeadPredictionMulti", "Targeting", 0.01f, 0f, 0.1f, 0.005f,
                menuLabel: "Target lead-prediction multiplier", display: v => v.ToString("0.000"),
                bind: v => AIGlobals.TargetVelocityLeadPredictionMulti = v,
                readSites: new[] { "RoughPredictTarget / intercept lead" });

            TunableRegistry.RegisterFloat(
                "Combat.LeadPredictionMaxTOF", "Targeting", 3f, 0f, 10f, 0.5f,
                menuLabel: "Lead-prediction max time-of-flight", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.LeadPredictionMaxTOF = v,
                readSites: new[] { "intercept lead clamp" });

            // ---- Movement / flight ----
            TunableRegistry.RegisterFloat(
                "Movement.YieldSpeed", "Movement", 10f, 0f, 50f, 1f,
                menuLabel: "Yield (slow-down) speed", display: v => v.ToString("0"),
                bind: v => AIGlobals.YieldSpeed = v,
                readSites: new[] { "Land throttle Yield: direction-aware slow-down" });

            TunableRegistry.RegisterFloat(
                "Movement.AircraftMaxDive", "Movement", 0.75f, 0f, 1f, 0.05f,
                menuLabel: "Aircraft max dive", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.AircraftMaxDive = v,
                readSites: new[] { "AirplaneAICore.FlightControl: dive clamp" });

            // ---- Anchor ----
            TunableRegistry.RegisterFloat(
                "Anchor.SafeAnchorDist", "Anchor", 50f, 0f, 200f, 5f,
                menuLabel: "Safe anchor distance", display: v => v.ToString("0"),
                bind: v => AIGlobals.SafeAnchorDist = v,
                readSites: new[] { "CanAnchorSafely: enemy distance gate" });

            // ---- Stuck / net-progress (engine cadence detail - hidden) ----
            TunableRegistry.RegisterFloat(
                "Combat.StuckNetProgressWindow", "Combat", 1.5f, 0.25f, 10f, 0.25f,
                menuVisible: false, display: v => v.ToString("0.0") + "s",
                bind: v => AIGlobals.StuckNetProgressWindow = v,
                readSites: new[] { "UpdatePhysicsInfo: net-progress window" });

            TunableRegistry.RegisterFloat(
                "Combat.StuckNetProgressFraction", "Combat", 0.2f, 0.01f, 1f, 0.01f,
                menuVisible: false,
                bind: v => AIGlobals.StuckNetProgressFraction = v,
                readSites: new[] { "UpdatePhysicsInfo: net-progress threshold" });

            TunableRegistry.RegisterInt(
                "Combat.StuckNetProgressFloor", "Combat", 1, 0, 50, 1,
                menuVisible: false,
                bind: v => AIGlobals.StuckNetProgressFloor = v,
                readSites: new[] { "UpdatePhysicsInfo: net-progress floor" });
        }
    }
}
