#if SMART_DEV
using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Training
{
    /// <summary>Per TRAINING-CONTRACT §3.1. Procedural terrain seed + tunables.</summary>
    public readonly struct TerrainSpec
    {
        public readonly int Seed;
        public readonly float Roughness;     // [0, 1]
        public readonly int HillCount;
        public readonly float ElevationRange;
        public readonly Vector2 PlayableAreaSize;

        public TerrainSpec(int seed, float roughness, int hillCount, float elevationRange, Vector2 area)
        {
            Seed = seed; Roughness = roughness; HillCount = hillCount;
            ElevationRange = elevationRange; PlayableAreaSize = area;
        }
    }

    /// <summary>Blueprint reference. v0.1.0 uses a registry-by-name; blueprint payload (block list) is owned by the loader (TODO v0.2).</summary>
    public readonly struct TechBlueprint
    {
        public readonly string Name;
        public TechBlueprint(string name) { Name = name; }
    }

    public readonly struct SpawnSpec
    {
        public readonly TeamId Team;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly TechBlueprint Blueprint;

        public SpawnSpec(TeamId team, Vector3 pos, Quaternion rot, TechBlueprint bp)
        {
            Team = team; Position = pos; Rotation = rot; Blueprint = bp;
        }
    }

    public sealed class Scenario
    {
        public string Id;
        public TerrainSpec Terrain;
        public IReadOnlyList<SpawnSpec> Spawns;
        public float MatchDurationLimit;
        public Vector2 PlayableAreaSize;
    }

    /// <summary>
    /// Per TRAINING-CONTRACT §3.2 procedural rules + §3.3 library dispatch.
    ///
    /// Procedural generation is deterministic given a seed — all randomness flows
    /// through a single Mulberry32 PRNG so replays are reproducible.
    ///
    /// Library scenarios are referenced by name; v0.1.0 ships zero library files —
    /// the dispatcher falls through to procedural until JSON loader is wired (TODO v0.2).
    /// </summary>
    public static class ScenarioGenerator
    {
        public enum Difficulty { Easy, Medium, Hard }

        // Provisional 15-name list per §3.3; loader is stubbed at v0.1.0.
        public static readonly string[] LibraryNames =
        {
            "desert_open_duel", "canyon_ambush", "hill_assault", "naval_engagement",
            "airfield_aerial", "urban_choke", "mixed_class_fleet", "scout_and_screen",
            "heavy_armor_brawl", "swarm_skirmish", "lopsided_advantage", "retreat_and_pursue",
            "formation_assault", "bait_setup", "time_pressure",
        };

        // Provisional blueprint pool — names only at v0.1.0; the loader maps name → block list TODO v0.2.
        public static readonly string[] BlueprintPool =
        {
            "Scout_Wheel_S", "Scout_Hover_S", "Skirmisher_Wheel_M", "Brawler_Wheel_M",
            "Sniper_Wheel_M", "Heavy_Wheel_L", "Interceptor_Air_S", "Bomber_Air_M",
            "Submarine_S", "Walker_M",
        };

        /// <summary>Procedural scenario from seed + difficulty. Deterministic.</summary>
        public static Scenario Procedural(int seed, Difficulty difficulty)
        {
            var rng = new Mulberry32((uint)seed);
            float minArea, maxArea, roughness, durationLimit;
            int teamSizeMin, teamSizeMax;
            int hillCount;
            float elevation;

            switch (difficulty)
            {
                case Difficulty.Easy:
                    minArea = 200f; maxArea = 400f; roughness = 0.2f;
                    teamSizeMin = 2; teamSizeMax = 3; hillCount = 2; elevation = 5f;
                    durationLimit = 180f;
                    break;
                case Difficulty.Hard:
                    minArea = 500f; maxArea = 800f; roughness = 0.8f;
                    teamSizeMin = 3; teamSizeMax = 5; hillCount = 8; elevation = 30f;
                    durationLimit = 300f;
                    break;
                default: // Medium
                    minArea = 300f; maxArea = 600f; roughness = 0.5f;
                    teamSizeMin = 2; teamSizeMax = 4; hillCount = 5; elevation = 15f;
                    durationLimit = 240f;
                    break;
            }

            float areaSide = Mathf.Lerp(minArea, maxArea, rng.NextFloat());
            var area = new Vector2(areaSide, areaSide);
            var terrain = new TerrainSpec(seed, roughness, hillCount, elevation, area);

            int usCount = teamSizeMin + (int)(rng.NextFloat() * (teamSizeMax - teamSizeMin + 1));
            int themCount = teamSizeMin + (int)(rng.NextFloat() * (teamSizeMax - teamSizeMin + 1));

            var spawns = new List<SpawnSpec>(usCount + themCount);
            float spawnRadius = areaSide * 0.35f;

            for (int i = 0; i < usCount; i++)
            {
                Vector3 pos = new Vector3(-spawnRadius + rng.NextFloat() * 30f, 0f, (i - usCount / 2f) * 15f);
                Quaternion rot = Quaternion.Euler(0f, 90f + rng.NextFloat() * 30f - 15f, 0f);
                spawns.Add(new SpawnSpec(new TeamId(1), pos, rot, PickBlueprint(rng)));
            }
            for (int i = 0; i < themCount; i++)
            {
                Vector3 pos = new Vector3(spawnRadius - rng.NextFloat() * 30f, 0f, (i - themCount / 2f) * 15f);
                Quaternion rot = Quaternion.Euler(0f, 270f + rng.NextFloat() * 30f - 15f, 0f);
                spawns.Add(new SpawnSpec(new TeamId(2), pos, rot, PickBlueprint(rng)));
            }

            return new Scenario
            {
                Id = "procedural-" + seed,
                Terrain = terrain,
                Spawns = spawns,
                MatchDurationLimit = durationLimit,
                PlayableAreaSize = area,
            };
        }

        private static TechBlueprint PickBlueprint(Mulberry32 rng)
        {
            int idx = (int)(rng.NextFloat() * BlueprintPool.Length);
            if (idx >= BlueprintPool.Length) idx = BlueprintPool.Length - 1;
            return new TechBlueprint(BlueprintPool[idx]);
        }

        /// <summary>
        /// Load a library scenario by name. v0.2 will read a serialized scenario blob;
        /// for v0.1.0 we produce a deterministic procedural scenario seeded by the name
        /// so the library evaluation in <see cref="SelfPlayHarness.RunLibraryEvaluation"/>
        /// always has fixed reference scenarios to score the current CMA-ES mean against
        /// — instead of silently skipping every entry (which made the library win-rate
        /// always 0% and the plateau detector misfire on generation 5).
        ///
        /// Phase 8 (FIX-PLAN.md): the procedural fallback. Difficulty is interpolated
        /// from the name's index — easier names early, harder names late — so the suite
        /// covers the full difficulty range.
        /// </summary>
        public static Scenario LoadLibrary(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                DebugTAC_AI.LogWarning("Smart.Training.ScenarioGenerator: empty library name; null returned.");
                return null;
            }
            // Stable hash so the same name always loads the same scenario regardless of
            // when called in a session. String.GetHashCode is not stable across .NET
            // versions but Mod uses the same runtime within a session — and the names
            // are baked in source so the hash is reproducible run-to-run on one binary.
            uint seed = unchecked((uint)name.GetHashCode()) | 1u; // avoid 0
            // Pick difficulty by name position in LibraryNames.
            int idx = System.Array.IndexOf(LibraryNames, name);
            Difficulty difficulty;
            if (idx < 0) difficulty = Difficulty.Medium;
            else if (idx < LibraryNames.Length / 3) difficulty = Difficulty.Easy;
            else if (idx < (LibraryNames.Length * 2) / 3) difficulty = Difficulty.Medium;
            else difficulty = Difficulty.Hard;

            var scen = Procedural((int)seed, difficulty);
            scen.Id = "library-" + name; // override Id so OutcomeScorer surfaces the source
            return scen;
        }

        /// <summary>Per §3.4: every 20th match uses a library scenario.</summary>
        public static Scenario PickForMatch(int matchCounter, int seed, Difficulty difficulty)
        {
            if (matchCounter > 0 && (matchCounter % 20 == 19))
            {
                int libIdx = (matchCounter / 20) % LibraryNames.Length;
                var lib = LoadLibrary(LibraryNames[libIdx]);
                if (lib != null) return lib;
            }
            return Procedural(seed, difficulty);
        }
    }

    /// <summary>
    /// Mulberry32 — small fast deterministic PRNG. We don't use Unity.Random or
    /// System.Random so harness reproducibility doesn't depend on either's
    /// undocumented internals.
    /// </summary>
    public sealed class Mulberry32
    {
        private uint _state;
        public Mulberry32(uint seed) { _state = seed; }

        public uint NextUInt()
        {
            _state += 0x6D2B79F5u;
            uint t = _state;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + (t ^ (t >> 7)) * (t | 61);
            return t ^ (t >> 14);
        }

        public float NextFloat() => (NextUInt() & 0xFFFFFF) / (float)0x1000000;

        public float NextGaussian()
        {
            float u1 = 1f - NextFloat();
            float u2 = 1f - NextFloat();
            return Mathf.Sqrt(-2f * Mathf.Log(Mathf.Max(u1, 1e-9f))) * Mathf.Cos(2f * Mathf.PI * u2);
        }
    }
}
#endif
