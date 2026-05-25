namespace TAC_AI.AI.Tunables.Catalog
{
    /// <summary>
    /// Step 6 batch 2a - combat ranges + targeting/scan timings + relations cooldown. Same const->static + bind
    /// pattern as CombatTunables (defaults equal the AIGlobals originals = behavior preserved at boot). Readers of
    /// AIGlobals.X see tuned values via bind. Registered from AIModuleBootstrap.InitAIModules.
    /// </summary>
    public static class RangeTimingTunables
    {
        public static void Register()
        {
            // ---- combat ranges ----
            TunableRegistry.RegisterInt("Combat.BossMaxCombatRange", "Combat", 250, 20, 1000, 10,
                menuLabel: "Boss max combat range", bind: v => AIGlobals.BossMaxCombatRange = v);
            TunableRegistry.RegisterInt("Combat.InvaderMaxCombatRange", "Combat", 250, 20, 1000, 10,
                menuLabel: "Invader max combat range", bind: v => AIGlobals.InvaderMaxCombatRange = v);
            TunableRegistry.RegisterInt("Combat.DefaultMaxTargetingRange", "Combat", 1500, 100, 5000, 50,
                menuLabel: "Max targeting range", bind: v => AIGlobals.DefaultMaxTargetingRange = v);
            TunableRegistry.RegisterInt("Combat.DefaultEnemyScanRange", "Combat", 150, 20, 1000, 10,
                menuLabel: "Enemy scan range", bind: v => AIGlobals.DefaultEnemyScanRange = v);
            TunableRegistry.RegisterInt("Combat.BaseFounderMaxCombatRange", "Combat", 60, 10, 500, 5,
                menuLabel: "Base-founder max combat range", bind: v => AIGlobals.BaseFounderMaxCombatRange = v);
            TunableRegistry.RegisterFloat("Combat.SpyperMaxCombatRange", "Combat", 175f, 20f, 500f, 5f,
                menuLabel: "Spyper max combat range", display: v => v.ToString("0"),
                bind: v => AIGlobals.SpyperMaxCombatRange = v);
            TunableRegistry.RegisterFloat("Combat.MinCombatRangeSpyper", "Combat", 60f, 0f, 200f, 5f,
                menuLabel: "Spyper min combat range", display: v => v.ToString("0"),
                bind: v => AIGlobals.MinCombatRangeSpyper = v);

            // ---- timings ----
            TunableRegistry.RegisterFloat("Timing.TargetCacheRefreshInterval", "Timing", 1.5f, 0.1f, 10f, 0.1f,
                menuLabel: "Target refresh interval (s)", display: v => v.ToString("0.0"),
                bind: v => AIGlobals.TargetCacheRefreshInterval = v);
            TunableRegistry.RegisterFloat("Timing.TargetCacheRefreshIntervalCombat", "Timing", 0.4f, 0.05f, 5f, 0.05f,
                menuLabel: "Target refresh interval - combat (s)", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.TargetCacheRefreshIntervalCombat = v);
            TunableRegistry.RegisterFloat("Timing.TargetValidationDelay", "Timing", 0.6f, 0.05f, 5f, 0.05f,
                menuLabel: "Target validation delay (s)", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.TargetValidationDelay = v);
            TunableRegistry.RegisterFloat("Timing.ScanDelay", "Timing", 0.5f, 0.05f, 5f, 0.05f,
                menuLabel: "Scan delay (s)", display: v => v.ToString("0.00"),
                bind: v => AIGlobals.ScanDelay = v);

            // ---- relations ----
            TunableRegistry.RegisterFloat("Combat.DamageAngerCoolRatePerSec", "Retreat", 25f, 0f, 200f, 5f,
                menuLabel: "Relations cool rate / s", display: v => v.ToString("0"),
                bind: v => AIGlobals.DamageAngerCoolRatePerSec = v);
        }
    }
}
