using TAC_AI.AI.Tunables;

namespace TAC_AI.AI.Forms.Smart.Director
{
    // Single source of truth for every smart.director.* tunable. TrainingModeTunables.cs
    // is NOT touched. Each registration binds to a static backing field read by consumers
    // (SmartForm GUI, guard workers, digest cadence). Bind callbacks store plain values —
    // none touch Unity directly, so no main-thread marshal is required for this set.
    public static class DirectorTunables
    {
        public const string Category = "Director";

        // ---- backing fields (read by consumers) ----
        public static volatile bool GuiShow;
        public static volatile bool GuardsEnabled = true;
        public static int DigestPeriodSec = 600;
        public static int InboxPollHz = 5;

        public static float GuardMovementBand = 0.10f;
        public static float GuardRoleBand = 0.12f;
        public static float GuardCombatBand = 0.10f;
        public static float GuardAircraftSafetyBand = 2.0f;

        public static float OverextendedHunterLeashM = 250f;
        public static float AircraftReleaseRampSec = 2.0f;
        public static int AircraftChronicWindowSec = 90;
        public static float OverdueDeliveryRefireCooldownSec = 30f;

        public static float ForceAscendTargetAltitude = 30f;
        public static float ForceAscendDuration = 5f;
        public static float GuardOverrideQMargin = 0.05f;

        public static int AllyProtectLruCapacity = 128;
        public static float AllyProtectDamageBudget = 500f;
        public static int BaseHeldWindowSec = 60;
        public static float BaseHeldDamageThreshold = 25f;

        public static int RollbackRateLimitSec = 300;
        public static int LrScaleRateLimitSec = 30;
        public static int ScenarioRespawnRateLimitSec = 30;
        public static int ChunkRegenBudgetPerTick = 6;
        public static float ChunkAnnulusInnerM = 40f;
        public static float ChunkAnnulusOuterM = 200f;

        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            TunableRegistry.RegisterBool("smart.director.gui.show", Category, false,
                bind: v => GuiShow = v, menuLabel: "Show Director panel");
            TunableRegistry.RegisterBool("smart.director.guards.enabled", Category, true,
                bind: v => GuardsEnabled = v, menuLabel: "Guards enabled");

            TunableRegistry.RegisterInt("smart.director.digest_period_sec", Category, 600, 60, 3600, 60,
                bind: v => DigestPeriodSec = v, menuLabel: "Digest period (s)");
            TunableRegistry.RegisterInt("smart.director.inbox_poll_hz", Category, 5, 1, 20, 1,
                bind: v => InboxPollHz = v, menuLabel: "Inbox poll Hz");

            TunableRegistry.RegisterFloat("smart.director.guard.movement_band", Category, 0.10f, 0f, 1f, 0.01f,
                bind: v => GuardMovementBand = v);
            TunableRegistry.RegisterFloat("smart.director.guard.role_band", Category, 0.12f, 0f, 1f, 0.01f,
                bind: v => GuardRoleBand = v);
            TunableRegistry.RegisterFloat("smart.director.guard.combat_band", Category, 0.10f, 0f, 1f, 0.01f,
                bind: v => GuardCombatBand = v);
            TunableRegistry.RegisterFloat("smart.director.guard.aircraft_safety_band", Category, 2.0f, 0f, 20f, 0.5f,
                bind: v => GuardAircraftSafetyBand = v);

            TunableRegistry.RegisterFloat("smart.director.guard.overextended_hunter.leash_m", Category, 250f, 50f, 1000f, 10f,
                bind: v => OverextendedHunterLeashM = v);
            TunableRegistry.RegisterFloat("smart.director.guards.aircraft_release_ramp_sec", Category, 2.0f, 0.5f, 10f, 0.5f,
                bind: v => AircraftReleaseRampSec = v);
            TunableRegistry.RegisterInt("smart.director.guards.aircraft_chronic_window_sec", Category, 90, 30, 300, 10,
                bind: v => AircraftChronicWindowSec = v);
            TunableRegistry.RegisterFloat("smart.director.guards.overdue_delivery_refire_cooldown_sec", Category, 30f, 5f, 300f, 5f,
                bind: v => OverdueDeliveryRefireCooldownSec = v);

            TunableRegistry.RegisterFloat("smart.director.guard.forceascend_target_altitude", Category, 30f, 5f, 200f, 5f,
                bind: v => ForceAscendTargetAltitude = v);
            TunableRegistry.RegisterFloat("smart.director.guard.forceascend_duration", Category, 5f, 1f, 30f, 0.5f,
                bind: v => ForceAscendDuration = v);
            TunableRegistry.RegisterFloat("smart.director.guard.override_qmargin", Category, 0.05f, 0f, 1f, 0.01f,
                bind: v => GuardOverrideQMargin = v);

            TunableRegistry.RegisterInt("smart.director.ally_protect.lru_capacity", Category, 128, 32, 512, 16,
                bind: v => AllyProtectLruCapacity = v);
            TunableRegistry.RegisterFloat("smart.director.ally_protect.damage_budget", Category, 500f, 50f, 5000f, 50f,
                bind: v => AllyProtectDamageBudget = v);
            TunableRegistry.RegisterInt("smart.director.base_held.window_sec", Category, 60, 30, 300, 10,
                bind: v => BaseHeldWindowSec = v);
            TunableRegistry.RegisterFloat("smart.director.base_held.damage_threshold", Category, 25f, 1f, 1000f, 5f,
                bind: v => BaseHeldDamageThreshold = v);

            TunableRegistry.RegisterInt("smart.director.rollback.rate_limit_sec", Category, 300, 30, 3600, 30,
                bind: v => RollbackRateLimitSec = v);
            TunableRegistry.RegisterInt("smart.director.lr_scale.rate_limit_sec", Category, 30, 5, 600, 5,
                bind: v => LrScaleRateLimitSec = v);
            TunableRegistry.RegisterInt("smart.director.scenario_respawn.rate_limit_sec", Category, 30, 5, 600, 5,
                bind: v => ScenarioRespawnRateLimitSec = v);
            TunableRegistry.RegisterInt("smart.director.chunkregen.budget_per_tick", Category, 6, 1, 32, 1,
                bind: v => ChunkRegenBudgetPerTick = v);
            TunableRegistry.RegisterFloat("smart.director.chunkregen.annulus_inner_m", Category, 40f, 0f, 200f, 5f,
                bind: v => ChunkAnnulusInnerM = v);
            TunableRegistry.RegisterFloat("smart.director.chunkregen.annulus_outer_m", Category, 200f, 50f, 500f, 10f,
                bind: v => ChunkAnnulusOuterM = v);
        }
    }
}
