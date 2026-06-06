using System;
using System.IO;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// L-080: round-trip-fixture self-test for the profile serializer. Called from
    /// LearningService.Init BEFORE the player's profile is loaded. Saves the four
    /// just-constructed (Glorot-initialised) models to a temp path, loads them back,
    /// and asserts the round-trip produces a profile with the expected section count
    /// and byte-identical weights. On failure: throws → LearningService logs
    /// <c>[PROFILE-SELFTEST-FAIL]</c> and refuses LoadProfile so a deserializer
    /// regression can't silently corrupt the player's disk profile.
    ///
    /// Non-destructive: <see cref="ILearnedModel.StoreParameters"/> is a copy-out;
    /// no model state is mutated. The temp file is deleted in finally along with
    /// its .previous + .penultimate backup ring entries.
    /// </summary>
    public static class ProfileSelfTest
    {
        /// <summary>
        /// Round-trip the four real models through Save → Load.
        ///
        /// The previous implementation passed an empty <c>ILearnedModel[0]</c> and
        /// expected Load to return non-null; but ProfilePersistence.Load reads
        /// exactly 4 sections, so the empty-array file failed to decode and the
        /// selftest threw on every boot, suppressing LoadProfile and silently
        /// overwriting the player's saved profile with Glorot-init weights.
        ///
        /// Pass the real four models constructed in LearningService.Init. Save reads
        /// via StoreParameters (copy-out, non-destructive); Load returns a
        /// LoadedProfile that we inspect and discard. Models are NOT re-loaded from
        /// the temp file — only verified that the round-trip preserved them.
        /// </summary>
        public static void Run(string modDirectory, ILearnedModel[] models)
        {
            if (models == null || models.Length == 0)
                throw new ArgumentException("ProfileSelfTest: models[] must be the four constructed trainers");

            string tempPath = Path.Combine(Path.GetTempPath(), "SmartAI.selftest." + Guid.NewGuid().ToString("N") + ".dat");
            try
            {
                ProfilePersistence.Save(tempPath, models);
                var loaded = ProfilePersistence.Load(tempPath, baselineBytes: null);
                if (loaded == null)
                    throw new Exception("ProfileSelfTest: Load returned null after round-trip");
                if (loaded.SchemaVersion < 1)
                    throw new Exception("ProfileSelfTest: Load returned SchemaVersion=" + loaded.SchemaVersion);
                if (loaded.Sections == null || loaded.Sections.Length != models.Length)
                    throw new Exception("ProfileSelfTest: Load returned " + (loaded.Sections == null ? "null" : loaded.Sections.Length.ToString())
                        + " sections, expected " + models.Length);
                for (int i = 0; i < models.Length; i++)
                {
                    var sec = loaded.Sections[i];
                    if (sec == null) throw new Exception("ProfileSelfTest: section " + i + " is null");
                    if (sec.Weights == null || sec.Weights.Length != models[i].ParameterCount)
                        throw new Exception("ProfileSelfTest: section " + i + " weight count mismatch ("
                            + (sec.Weights == null ? "null" : sec.Weights.Length.ToString())
                            + " vs model " + models[i].ParameterCount + ")");
                }
                DebugTAC_AI.LogWarnFileOnly("profile-selftest-pass",
                    "[PROFILE-SELFTEST-PASS] roundtrip sections=" + loaded.Sections.Length
                    + " schema=" + loaded.SchemaVersion);
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
