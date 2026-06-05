namespace TAC_AI.AI.Forms.Smart.Learning.Migrations
{
    /// <summary>
    /// P8 Item 19: schema 2 marks the intent classifier's GRU as unfrozen (BPTT enabled).
    /// No-op migration — the flat parameter array layout is unchanged (same offsets, same
    /// total size). A v0.1 profile loads byte-identical to a v0.2 profile; the difference
    /// is which gradients flow on subsequent training steps, not the on-disk shape.
    ///
    /// Per DOCTRINE §2.8 forward-only-migrations, every schema bump gets its own numbered
    /// file even when no-op — keeps the version ladder honest. Wired into
    /// <see cref="MigrationRunner.RunForward"/>'s dispatch switch as case 1 → Up(profile).
    /// </summary>
    [SmartMigration(fromVersion: 1, note: "v1→v2 GRU BPTT unfreeze (no transform)")]
    public static class M0002_BpttUnfreeze
    {
        public const uint Version = 2;

        public static void Up(LoadedProfile profile)
        {
            // Parameter array shape unchanged. Bump the version stamp on the in-memory
            // profile so subsequent saves write the new version. No data transformation.
            profile.SchemaVersion = Version;
        }
    }
}
