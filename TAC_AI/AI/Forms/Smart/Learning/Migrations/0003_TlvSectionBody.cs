namespace TAC_AI.AI.Forms.Smart.Learning.Migrations
{
    /// <summary>
    /// L-079: schema 3 — TLV per-section body format. v0.2 used a bare float[] payload
    /// per section; v3 wraps it in `tag_count[2]` then `(tag_id[2], byte_length[4], payload)`
    /// tuples so future schema bumps can add fields (e.g. optimizer-state, lr-schedule)
    /// without invalidating the on-disk layout.
    ///
    /// In-memory: <c>Section.Weights</c> remains the canonical float[] view (consumers
    /// assume float[]); the TLV layer lives in ProfilePersistence's load/save path.
    /// M0003 sniffs v≤2 in-memory and wraps the flat float[] into a notional TLV
    /// representation; weights stay in Section.Weights. Re-emit on next Save preserves
    /// UnknownTags verbatim (handled by ProfilePersistence schema-conditional Read/Write).
    /// </summary>
    [SmartMigration(fromVersion: 2, note: "v2→v3 TLV body (no parameter transform)")]
    public static class M0003_TlvSectionBody
    {
        public const uint Version = 3;

        public static void Up(LoadedProfile profile)
        {
            // No in-memory transform — Section.Weights stays as the canonical float[] view.
            // The TLV wrapper is purely a disk-format change handled by ProfilePersistence's
            // schema-conditional Read/Write at load/save time.
            profile.SchemaVersion = Version;
        }
    }
}
