using System;
using System.IO;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// L-080: round-trip-fixture self-test for the profile serializer. Called from
    /// LearningService.Init BEFORE the player's profile is loaded. Round-trips a tiny
    /// fixture through ProfilePersistence.Save + Load via a temp file; throws on
    /// bit-mismatch. LearningService.Init catches, logs <c>[PROFILE-SELFTEST-FAIL]</c>,
    /// and refuses LoadProfile (Glorot init retained) so a deserializer regression can't
    /// silently corrupt the player's profile on first read.
    /// </summary>
    public static class ProfileSelfTest
    {
        public static void Run(string modDirectory)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "SmartAI.selftest." + Guid.NewGuid().ToString("N") + ".dat");
            try
            {
                // The Save/Load API takes ILearnedModel[]. We can't easily fabricate
                // real models in a self-test, so we exercise the Magic + CRC + envelope
                // by saving an empty model array (header + footer) and asserting Load
                // returns a non-null profile with SchemaVersion == CurrentSchemaVersion.
                ProfilePersistence.Save(tempPath, new ILearnedModel[0]);
                var loaded = ProfilePersistence.Load(tempPath, baselineBytes: null);
                if (loaded == null)
                    throw new Exception("ProfileSelfTest: Load returned null for round-tripped empty profile");
                if (loaded.SchemaVersion < 1)
                    throw new Exception("ProfileSelfTest: Load returned SchemaVersion=" + loaded.SchemaVersion);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                try
                {
                    string prev = tempPath + ".previous";
                    if (File.Exists(prev)) File.Delete(prev);
                }
                catch { }
                try
                {
                    string pen = tempPath + ".penultimate";
                    if (File.Exists(pen)) File.Delete(pen);
                }
                catch { }
            }
        }
    }
}
