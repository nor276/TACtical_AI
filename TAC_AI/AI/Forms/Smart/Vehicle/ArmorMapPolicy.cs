namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P3 default-OFF feature gate per V0.2-PLAN-REV7. Flip requires the P3.1 spawn-test
    /// gate per REV 3 — shifts ArmorMap weak-face scoring from mass-as-HP placeholder
    /// to real per-block SpecHP * HpFraction accumulation.
    /// </summary>
    public static class ArmorMapPolicy
    {
        /// <summary>
        /// When true (P11 post-flip default), ArmorMap.Compute uses the catalog-backed
        /// overload (accumulates <c>archetype.SpecHP * pose.HpFraction</c> per voxel) —
        /// physically-grounded face HP that survives armor-fraction reductions.
        /// When false, it uses the v0.1 mass-as-HP placeholder (<c>b.CurrentMass</c>) for
        /// bit-identical face-weakness ranking. Tunable: <c>armormap.useRealSpecHP</c>.
        /// </summary>
        public static bool UseRealSpecHP = true;
    }
}
