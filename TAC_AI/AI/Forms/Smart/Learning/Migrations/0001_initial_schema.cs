namespace TAC_AI.AI.Forms.Smart.Learning.Migrations
{
    /// <summary>
    /// Schema 1 — the initial schema. Per LEARNING-CONTRACT §8 + DOCTRINE §2.8
    /// forward-only-migrations, every schema version gets its own numbered file.
    /// This one is the floor: no transformation defined; new profiles are written
    /// directly at schema 1.
    ///
    /// When schema 2 lands, add 0002_xxx.cs with FromVersion=1, ToVersion=2,
    /// and wire it into <see cref="MigrationRunner.RunForward"/>'s dispatch switch.
    /// </summary>
    public static class M0001_InitialSchema
    {
        public const uint Version = 1;
    }
}
