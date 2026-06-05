using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Tunables.Catalog
{
    /// <summary>
    /// P4 Item 15: tunable catalog for the Aether belief subsystem.
    /// Bind pattern matches <see cref="CombatTunables"/>, <see cref="RangeTimingTunables"/>,
    /// <see cref="MovementFlightTunables"/> — registration discards the returned handle and
    /// uses <c>bind:</c> to write through to the existing static field, so the field stays
    /// the single source of truth for hot-path reads (e.g. <c>AetherFuser.cs:85</c> for
    /// VelocitySmoothingStrength, <c>WeaponFireController</c> for UseCoastAwareLead).
    /// Defaults equal the current field values so <c>bind(default)</c> at boot is a no-op
    /// = behavior preserved.
    ///
    /// Registered from <see cref="TAC_AI.AI.Engine.AIModuleBootstrap.InitAIModules"/>
    /// alongside the other catalogs.
    /// </summary>
    public static class AetherTunables
    {
        public static void Register()
        {
            TunableRegistry.RegisterFloat(
                key: "Aether.VelocitySmoothingStrength",
                category: "Aether",
                defaultValue: 0.35f,
                min: 0f, max: 1f, step: 0.05f,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Aether D2 velocity smoothing",
                display: v => v.ToString("0.00"),
                bind: v => AetherTuning.VelocitySmoothingStrength = v,
                readSites: new[] { "AetherFuser.Tick: fresh-observation Lerp(rawVel, priorVel, strength)" });

            TunableRegistry.RegisterBool(
                key: "Aether.UseCoastAwareLead",
                category: "Aether",
                defaultValue: true,   // P11 post-flip default (was false)
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Coast-aware weapon-fire lead",
                bind: v => AetherTuning.UseCoastAwareLead = v,
                readSites: new[] { "WeaponFireController.ComputeFireCommits: lead-math source (PositionMean/VelocityMean vs PositionAt/VelocityAt)" });

            // P9 Item 31 (REV 7 C12): CircuitBreaker calibration knobs. Setter-only bind
            // syncs the AetherTuning static field; CircuitBreaker.cs ctor reads the static
            // when caller passes the literal const default (sentinel-overlay pattern).
            TunableRegistry.RegisterInt(
                key: "threading.circuit.maxFailures",
                category: "Threading",
                defaultValue: TAC_AI.AI.Forms.Smart.Threading.CircuitBreaker.DefaultMaxFailures,  // 10
                min: 1, max: 100, step: 1,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Circuit Breaker — Max Failures",
                bind: v => AetherTuning.CircuitBreakerMaxFailures = v);

            TunableRegistry.RegisterInt(
                key: "threading.circuit.windowMs",
                category: "Threading",
                defaultValue: TAC_AI.AI.Forms.Smart.Threading.CircuitBreaker.DefaultWindowMs,    // 30000
                min: 1000, max: 600000, step: 1000,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Circuit Breaker — Window (ms)",
                bind: v => AetherTuning.CircuitBreakerWindowMs = v);
        }
    }
}
