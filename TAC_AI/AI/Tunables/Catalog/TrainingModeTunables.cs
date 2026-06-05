using TAC_AI.AI.Forms.Smart.Tooling;

namespace TAC_AI.AI.Tunables.Catalog
{
    /// <summary>
    /// Tunable catalog entry for the training-mode tech-spawn filter. Registers a single
    /// in-game-menu-visible bool that flips <see cref="TrainingModeFilter.Enabled"/>. Same
    /// catalog pattern as <see cref="SmartParityTunables"/> and <see cref="AetherTunables"/>.
    ///
    /// Default OFF — gameplay is identical to today unless the operator enables it. When
    /// ON, RawTech spawn candidates containing missile/artillery/rail/torpedo/mortar/sniper
    /// keywords are rejected + re-rolled up to 5 times before falling through.
    ///
    /// In-game surface:
    ///   - Tunables menu category "Training" → bool "Limit spawns (training mode)"
    ///   - F8 LiveTweakerPanel (auto-renders as a toggle row)
    ///   - Console: smart.training.enable on|off, smart.training.status
    /// </summary>
    public static class TrainingModeTunables
    {
        public static void Register()
        {
            TunableRegistry.RegisterBool(
                key: "training.enabled",
                category: "Training",
                defaultValue: false,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Limit spawns (no missile/rail/artillery; for AI training)",
                bind: v =>
                {
                    TrainingModeFilter.Enabled = v;
                    DebugTAC_AI.Log("Smart.TrainingMode: " + (v ? "ENABLED" : "disabled")
                        + " (counters reset)");
                    TrainingModeFilter.ResetCounters();
                });
        }
    }
}
