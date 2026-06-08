namespace TAC_AI.AI.Forms.Smart.Learning.Migrations
{
    /// <summary>
    /// FEATURE-EXPANSION-PLAN §7.5: schema 4 marks the coordinated ArchitectureVersion bump
    /// across all four learned models (Intent / ActionValue / Residual / Threat) — each
    /// jumps to ArchitectureVersion=3 in the same change. The on-disk schema layout is
    /// untouched (TLV body shape unchanged from §3); the bump exists so a v3-on-disk
    /// profile loaded by a v0.4 client correctly observes that the per-model arch ids have
    /// shifted and rejects any cached float[] blob whose length no longer matches.
    /// No-op transform — ProfilePersistence.LoadParameters already throws on
    /// ParameterCount mismatch, which dispatches to the "discard + Glorot init" recovery
    /// path documented in §2 "no-preservation-needed posture".
    ///
    /// Renumbered from 0003_* to avoid filename collision with the existing
    /// 0003_TlvSectionBody.cs migration (per F-02 reconciliation in §12).
    /// Modeled on 0002_BpttUnfreeze.cs.
    /// </summary>
    [SmartMigration(fromVersion: 3, note: "v3→v4 strategic-state expansion (coordinated arch bump; no transform)")]
    public static class M0004_StrategicStateExpansion
    {
        public const uint Version = 4;

        public static void Up(LoadedProfile profile)
        {
            // No data transformation. Per-model float[] payload shapes that no longer
            // match the new ArchitectureVersion will be rejected at LoadParameters and
            // fall through to fresh Glorot init in ApplyProfile's per-model catch.
            profile.SchemaVersion = Version;
        }
    }
}
