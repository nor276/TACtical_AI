using System;

namespace TAC_AI.AI.Forms.Smart.Learning.Migrations
{
    /// <summary>
    /// L-014: tag for schema migration types. MigrationRegistry's static ctor walks the
    /// executing assembly looking for `[SmartMigration]` types and asserts that the
    /// FromVersion sequence covers `[0, MaxSchemaVersion)` without gaps. A missing
    /// migration is a TypeInitializationException at first Init — the schema is not
    /// permitted to ship with a hole in the version ladder.
    ///
    /// Each migration type MUST expose `public static void Up(LoadedProfile p)`. The
    /// registry resolves Up via reflection (one-time cost at static-init). Up MUST be
    /// idempotent — RunForward calls it exactly once per (current → target) step, but
    /// a corrupted profile that's already partially migrated would call it again on
    /// the next load attempt.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class SmartMigrationAttribute : Attribute
    {
        /// <summary>Schema version this migration applies to (FromVersion → FromVersion + 1).</summary>
        public uint FromVersion { get; }

        /// <summary>Optional human-readable note for the migration log line.</summary>
        public string Note { get; }

        public SmartMigrationAttribute(uint fromVersion, string note = null)
        {
            FromVersion = fromVersion;
            Note = note ?? string.Empty;
        }
    }
}
