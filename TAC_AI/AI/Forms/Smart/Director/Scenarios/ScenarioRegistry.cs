using TAC_AI.Templates;
using TerraTechETCUtil;

namespace TAC_AI.AI.Forms.Smart.Director.Scenarios
{
    /// <summary>
    /// Scenario id encoding. Stored as int in DirectorState.ActiveScenario.
    /// </summary>
    public enum ScenarioId : byte
    {
        None = 0,
        S1 = 1,         // Open Brawl
        S2 = 2,         // SubNeutral Resource Race
        S3 = 3,         // Defensive Hold
        S4 = 4,         // Air Sortie
        S5 = 5,         // Mining + Defend
        S6prime = 6,    // Patrol Stress
    }

    /// <summary>
    /// Per-scenario static recipe — wave timings, populations, what to ask Spawn() to produce.
    /// All fields immutable. ScenarioWorker reads from this; never modified at runtime.
    /// </summary>
    public sealed class ScenarioRecipe
    {
        public readonly ScenarioId Id;
        public readonly string Name;
        public readonly int TargetTechCount;        // healthy population target
        public readonly int HostileTeamCount;       // teams ScenarioWorker mints as hostile
        public readonly int SubNeutralTeamCount;    // teams ScenarioWorker mints as SubNeutral
        public readonly bool UsesPlayerAITeammate;  // S5: uses ManBaseTeams.GetTeamAIBaseTeam
        public readonly bool RequiresChunkRegen;    // S2 / S5 only
        public readonly bool HasHostileEngagement;  // S1 / S3 / S4 / S5 / S6prime
        public readonly int MobilePerHostileTeam;   // initial mobile attack count
        public readonly int BasePerScenario;        // initial base count
        public readonly int EscortPerBase;          // escort mobile count next to base
        public readonly int InitialChunkTarget;     // when ChunkRegen active
        public readonly float SpawnRadiusM;         // distance from centroid for ring spawns
        public readonly BaseTerrain Terrain;
        public readonly FactionLevel Progression;
        public readonly int MaxPrice;
        public readonly int Grade;
        public readonly int BaseGrade;
        public readonly RawTechOffset MobileOffset;
        public readonly float AttackerAltitudeY;    // S4 only — 400 m above terrain
        public readonly int WaveIntervalSec;        // S3/S4/S5/S6prime
        public readonly int MaintenanceCheckSec;    // S1 top-up cadence
        public readonly bool AnchorSubNeutralRelations; // S2/S5 anti-degradation tick
        public readonly bool SpawnWaveStaggered;    // S3 attacker waves staggered 30 s apart
        // INSUFFICIENT_DATA gating — expected events/min per identity per scenario. Constraint
        // engine reads these when grading the metric. Two header numbers per scenario
        // (combat-rate floor, gatherer-rate floor); rest routes through identity-rate
        // constraints already in the registry.
        public readonly float ExpectedKillRatePerMin;
        public readonly float ExpectedDeliveryRatePerMin;

        public ScenarioRecipe(ScenarioId id, string name, int targetTechCount, int hostileTeamCount,
            int subNeutralTeamCount, bool usesPlayerAITeammate, bool requiresChunkRegen,
            bool hasHostileEngagement, int mobilePerHostileTeam, int basePerScenario,
            int escortPerBase, int initialChunkTarget, float spawnRadiusM, BaseTerrain terrain,
            FactionLevel progression, int maxPrice, int grade, int baseGrade,
            RawTechOffset mobileOffset, float attackerAltitudeY, int waveIntervalSec,
            int maintenanceCheckSec, bool anchorSubNeutralRelations, bool spawnWaveStaggered,
            float expectedKillRatePerMin, float expectedDeliveryRatePerMin)
        {
            Id = id;
            Name = name;
            TargetTechCount = targetTechCount;
            HostileTeamCount = hostileTeamCount;
            SubNeutralTeamCount = subNeutralTeamCount;
            UsesPlayerAITeammate = usesPlayerAITeammate;
            RequiresChunkRegen = requiresChunkRegen;
            HasHostileEngagement = hasHostileEngagement;
            MobilePerHostileTeam = mobilePerHostileTeam;
            BasePerScenario = basePerScenario;
            EscortPerBase = escortPerBase;
            InitialChunkTarget = initialChunkTarget;
            SpawnRadiusM = spawnRadiusM;
            Terrain = terrain;
            Progression = progression;
            MaxPrice = maxPrice;
            Grade = grade;
            BaseGrade = baseGrade;
            MobileOffset = mobileOffset;
            AttackerAltitudeY = attackerAltitudeY;
            WaveIntervalSec = waveIntervalSec;
            MaintenanceCheckSec = maintenanceCheckSec;
            AnchorSubNeutralRelations = anchorSubNeutralRelations;
            SpawnWaveStaggered = spawnWaveStaggered;
            ExpectedKillRatePerMin = expectedKillRatePerMin;
            ExpectedDeliveryRatePerMin = expectedDeliveryRatePerMin;
        }
    }

    /// <summary>
    /// Static recipe lookup. 6 entries: S1..S5 + S6prime. Index by (int)ScenarioId.
    /// </summary>
    public static class ScenarioRegistry
    {
        // 7-slot array (None=0 unused); index = (int)ScenarioId.
        public static readonly ScenarioRecipe[] All;

        static ScenarioRegistry()
        {
            All = new ScenarioRecipe[7];

            // S1 Open Brawl — 4 hostile teams of 3 mobile each, single-faction land brawl.
            // VEN is mid-tier per FactionLevel enum at FactionLevel.cs:12-23.
            All[(int)ScenarioId.S1] = new ScenarioRecipe(
                id: ScenarioId.S1, name: "OpenBrawl",
                targetTechCount: 12, hostileTeamCount: 4, subNeutralTeamCount: 0,
                usesPlayerAITeammate: false, requiresChunkRegen: false, hasHostileEngagement: true,
                mobilePerHostileTeam: 3, basePerScenario: 0, escortPerBase: 0,
                initialChunkTarget: 0, spawnRadiusM: 200f, terrain: BaseTerrain.Land,
                progression: FactionLevel.VEN, maxPrice: 12000, grade: 3, baseGrade: 99,
                mobileOffset: RawTechOffset.RaycastTerrainAndScenery, attackerAltitudeY: 0f,
                waveIntervalSec: 0, maintenanceCheckSec: 5,
                anchorSubNeutralRelations: false, spawnWaveStaggered: false,
                expectedKillRatePerMin: 0.5f, expectedDeliveryRatePerMin: 0f);

            // S2 SubNeutral Resource Race — 4 SubNeutral teams; 1 Harvesting base + 2-3 Prospector
            // mobiles each. Shared chunk pool. Anti-degradation tick on SubNeutral relations.
            All[(int)ScenarioId.S2] = new ScenarioRecipe(
                id: ScenarioId.S2, name: "SubNeutralRace",
                targetTechCount: 12, hostileTeamCount: 0, subNeutralTeamCount: 4,
                usesPlayerAITeammate: false, requiresChunkRegen: true, hasHostileEngagement: false,
                mobilePerHostileTeam: 3, basePerScenario: 4, escortPerBase: 0,
                initialChunkTarget: 50, spawnRadiusM: 220f, terrain: BaseTerrain.Land,
                progression: FactionLevel.VEN, maxPrice: 10000, grade: 3, baseGrade: 2,
                mobileOffset: RawTechOffset.RaycastTerrainAndScenery, attackerAltitudeY: 0f,
                waveIntervalSec: 0, maintenanceCheckSec: 30,
                anchorSubNeutralRelations: true, spawnWaveStaggered: false,
                expectedKillRatePerMin: 0f, expectedDeliveryRatePerMin: 0.5f);

            // S3 Defensive Hold — 1 player-allied base + 3 escorts; 3 hostile attacker teams,
            // each sends 3-tech wave every 90 s, staggered 30 s.
            All[(int)ScenarioId.S3] = new ScenarioRecipe(
                id: ScenarioId.S3, name: "DefensiveHold",
                targetTechCount: 12, hostileTeamCount: 3, subNeutralTeamCount: 0,
                usesPlayerAITeammate: true, requiresChunkRegen: false, hasHostileEngagement: true,
                mobilePerHostileTeam: 3, basePerScenario: 1, escortPerBase: 3,
                initialChunkTarget: 0, spawnRadiusM: 180f, terrain: BaseTerrain.Land,
                progression: FactionLevel.VEN, maxPrice: 11000, grade: 3, baseGrade: 3,
                mobileOffset: RawTechOffset.RaycastTerrainAndScenery, attackerAltitudeY: 0f,
                waveIntervalSec: 90, maintenanceCheckSec: 10,
                anchorSubNeutralRelations: false, spawnWaveStaggered: true,
                expectedKillRatePerMin: 0.4f, expectedDeliveryRatePerMin: 0f);

            // S4 Air Sortie — 1 land base + 3 Aviator escorts (+60 m); 1 hostile team sends
            // 4 Air attackers at 400 m altitude every 120 s.
            All[(int)ScenarioId.S4] = new ScenarioRecipe(
                id: ScenarioId.S4, name: "AirSortie",
                targetTechCount: 8, hostileTeamCount: 1, subNeutralTeamCount: 0,
                usesPlayerAITeammate: true, requiresChunkRegen: false, hasHostileEngagement: true,
                mobilePerHostileTeam: 4, basePerScenario: 1, escortPerBase: 3,
                initialChunkTarget: 0, spawnRadiusM: 250f, terrain: BaseTerrain.Air,
                progression: FactionLevel.VEN, maxPrice: 14000, grade: 3, baseGrade: 3,
                mobileOffset: RawTechOffset.OffGround60Meters, attackerAltitudeY: 400f,
                waveIntervalSec: 120, maintenanceCheckSec: 15,
                anchorSubNeutralRelations: false, spawnWaveStaggered: false,
                expectedKillRatePerMin: 0.3f, expectedDeliveryRatePerMin: 0f);

            // S5 Mining + Defend — 1 player AITeammate base + 2 Prospectors + 2 ground escorts;
            // 1 hostile incursion every 75-90 s. ChunkRegen active.
            All[(int)ScenarioId.S5] = new ScenarioRecipe(
                id: ScenarioId.S5, name: "MiningDefend",
                targetTechCount: 8, hostileTeamCount: 1, subNeutralTeamCount: 0,
                usesPlayerAITeammate: true, requiresChunkRegen: true, hasHostileEngagement: true,
                mobilePerHostileTeam: 3, basePerScenario: 1, escortPerBase: 4,
                initialChunkTarget: 50, spawnRadiusM: 220f, terrain: BaseTerrain.Land,
                progression: FactionLevel.VEN, maxPrice: 11000, grade: 3, baseGrade: 3,
                mobileOffset: RawTechOffset.RaycastTerrainAndScenery, attackerAltitudeY: 0f,
                waveIntervalSec: 80, maintenanceCheckSec: 15,
                anchorSubNeutralRelations: true, spawnWaveStaggered: false,
                expectedKillRatePerMin: 0.2f, expectedDeliveryRatePerMin: 0.5f);

            // S6prime Patrol Stress — 1 player base + 4 Patrol + 2 RepairSupport; 2 hostile teams
            // send 2-tech harassment waves alternately every 60 s from opposite bearings.
            All[(int)ScenarioId.S6prime] = new ScenarioRecipe(
                id: ScenarioId.S6prime, name: "PatrolStress",
                targetTechCount: 10, hostileTeamCount: 2, subNeutralTeamCount: 0,
                usesPlayerAITeammate: true, requiresChunkRegen: false, hasHostileEngagement: true,
                mobilePerHostileTeam: 2, basePerScenario: 1, escortPerBase: 6,
                initialChunkTarget: 0, spawnRadiusM: 220f, terrain: BaseTerrain.Land,
                progression: FactionLevel.VEN, maxPrice: 11000, grade: 3, baseGrade: 3,
                mobileOffset: RawTechOffset.RaycastTerrainAndScenery, attackerAltitudeY: 0f,
                waveIntervalSec: 60, maintenanceCheckSec: 15,
                anchorSubNeutralRelations: false, spawnWaveStaggered: true,
                expectedKillRatePerMin: 0.3f, expectedDeliveryRatePerMin: 0f);
        }

        /// <summary>Returns the recipe or null when scenario id is None / out of range.</summary>
        public static ScenarioRecipe Lookup(int id)
        {
            if (id <= 0 || id >= All.Length) return null;
            return All[id];
        }

        public static ScenarioRecipe Lookup(ScenarioId id)
        {
            return Lookup((int)id);
        }

        /// <summary>True when active scenario has hostile engagement (S1/S3/S4/S5/S6prime).</summary>
        public static bool HasHostileEngagement(int id)
        {
            var r = Lookup(id);
            return r != null && r.HasHostileEngagement;
        }

        /// <summary>True when active scenario uses ChunkRegen (S2 / S5).</summary>
        public static bool RequiresChunkRegen(int id)
        {
            var r = Lookup(id);
            return r != null && r.RequiresChunkRegen;
        }
    }
}
