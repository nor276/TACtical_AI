namespace TAC_AI.AI.Forms.Smart.Identity
{
    /// <summary>
    /// P6 Item 25/26/28 shared tunable flags. All default OFF — v0.1 classification preserved
    /// bit-identically until a flag is flipped + the P6.1 spawn-test gate has been run.
    ///
    /// Flipping any one of these reroutes a slice of techs that today classify as Hunter or
    /// Gatherer to a new identity, with observable combat-feel consequences. See
    /// V0.2-PLAN-REV7.md P6 for per-flag scope.
    /// </summary>
    public static class SmartIdentityTuning
    {
        /// <summary>
        /// P6 Item 26 / P11 post-flip default ON: ground armed mobile techs with Shield blocks
        /// classify as RepairSupport (orbit damaged friendly within 80m, facing-threat phase
        /// from P11 Item 68) instead of Hunter. Tunable: <c>smart.identity.enableRepairSupport</c>.
        /// </summary>
        public static bool EnableRepairSupport = true;

        /// <summary>
        /// P6 Item 25 / P11 post-flip default ON: techs with Conveyor/Holder blocks AND
        /// ≤1 weapon classify as Gatherer composition (widens the catchment to include
        /// cargo-block-carrying techs that previously fell through to Hunter).
        /// Tunable: <c>smart.identity.useHarvesterModulesForGatherer</c>.
        /// </summary>
        public static bool UseHarvesterModulesForGatherer = true;

        /// <summary>
        /// P6 Item 28 / P11 post-flip default ON: solo armed mobile ground techs classify as
        /// Patrol (meander spawn anchor + react to nearby hostiles in Produce-time) instead
        /// of Hunter. Largest combat-feel delta because it steals from Hunter.
        /// Tunable: <c>smart.identity.enablePatrol</c>.
        /// </summary>
        public static bool EnablePatrol = true;

        /// <summary>
        /// P6 Item 25 reserved tunable: future v0.3 boost for Drill-bearing techs in the
        /// Gatherer classification. Body in classifier is a `// TODO v0.3` no-op for v0.2 —
        /// kept here so the flag's name is reserved and P3-style spawn-test telemetry can
        /// reference it before the consumer lands.
        /// </summary>
        public static bool UseDrillForGathererBoost = false;
    }
}
