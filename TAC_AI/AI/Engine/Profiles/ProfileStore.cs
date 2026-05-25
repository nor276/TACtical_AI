using System.Collections.Generic;

namespace TAC_AI.AI.Engine.Profiles
{
    /// <summary>
    /// The catalog of named AIProfiles available for selection. Populated at boot (default profiles that
    /// reproduce today's DediAI / EvilCommander behavior, plus any discovered ones). Step 5 wires the
    /// per-tank selection into ModuleAIExtension save/load (+ the profileChanged rebuild trigger) and the
    /// allied MP RPC; this Step-1 store is the in-memory definition table those hang off of.
    /// </summary>
    public static class ProfileStore
    {
        private static readonly Dictionary<string, AIProfile> profiles = new Dictionary<string, AIProfile>();

        public static void Clear() => profiles.Clear();

        public static void Register(AIProfile profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.Id)) return;
            profiles[profile.Id] = profile;
        }

        public static bool TryGet(string id, out AIProfile profile) => profiles.TryGetValue(id, out profile);

        public static IEnumerable<AIProfile> All => profiles.Values;

        public static int Count => profiles.Count;
    }
}
