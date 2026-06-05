namespace TAC_AI.AI.Forms.Smart.Learning.Migrations
{
    /// <summary>
    /// Schema 1 — the floor. v0→v1 is a documented no-op (L-014: previously the floor was
    /// "undefined", which the new MigrationRegistry would have rejected as a hole; now it's
    /// an explicit `Up` that just bumps SchemaVersion 0→1). New profiles are written
    /// directly at schema 1 and never call this migration; only a hypothetical schema-0 file
    /// on disk would.
    /// </summary>
    [SmartMigration(fromVersion: 0, note: "v0→v1 floor (no transform)")]
    public static class M0001_InitialSchema
    {
        public const uint Version = 1;

        public static void Up(LoadedProfile profile)
        {
            // L-014 floor: bump the version stamp. No data transform.
            profile.SchemaVersion = Version;
        }
    }
}
