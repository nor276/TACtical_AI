using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TAC_AI.AI;
using TAC_AI.AI.Enemy;
using TAC_AI.Templates;
using TAC_AI.World;
using TAC_AI.AI.Movement;
using SafeSaves;
using TerraTechETCUtil;
using System.Runtime.CompilerServices;
using Snapshots;

#if !STEAM
using ModHelper.Config;
#endif

namespace TAC_AI
{
    // Previously an extension to RandomAdditions, TACtical AI is the AI branch of the mod.
    //   Featuring a simple to use, fully-fledged AI which adapts to its vehicle nearly instantly
    //   based on the parts attached to it, with minimal manual intervention.
    /// REVISED (overview): init/deinit hardened — Init/DeInit cycle counters + 10s EmitHealthPulse diagnostic; PatchMod now idempotent per-batch (patchedBatches); DeInit now symmetrically restores vanilla settings (block-survival, fragility, pop limit) and unsubscribes events; MainOfficialInitIterate reordered (PatchMod runs mid-init, pop override + wiki moved in); SafeSaves hook failure now FatalError.
    public class KickStart
    {
        internal const string ModID = "Advanced AI";
        internal const string ModCommandID = "TAC_AI";

        public static FactionSubTypes factionAttractOST = FactionSubTypes.NULL;

        public static bool UseProcedualEnemyBaseSpawning = false;
        public static bool DoPopSpawnCostCheck = false;

        // v2 isolation: pathfinder config/debug knobs, moved off the form's AIEPathMapper so KickStartExtras /
        // DebugRawTechSpawner / TankAIManager don't name a form type. The active form's pathfinder reads these.
        public static int PathRequestsToCalcPerFrame = 1;
#if DEBUG
        public static bool ShowPathingGIZMO = true;
#else
        public static bool ShowPathingGIZMO = false;
#endif

#if STEAM
        public static bool ShouldBeActive = false;
        public static bool UseClassicRTSControls = true;
#else
        public static bool ShouldBeActive = true;
        public static bool UseClassicRTSControls = false;
#endif
        public static bool UseNumpadForGrouping = false;//
        /// <summary> Toggles retreat state! </summary>
        internal static KeyCode RetreatHotkey = KeyCode.I;
        public static int RetreatHotkeySav = (int)RetreatHotkey;
        /// <summary> Toggles RTS Mode </summary>
        internal static KeyCode CommandHotkey = KeyCode.K;
        public static int CommandHotkeySav = (int)CommandHotkey;
        /// <summary> Fires bolts on selected Techs </summary>
        internal static KeyCode CommandBoltsHotkey = KeyCode.X;
        public static int CommandBoltsHotkeySav = (int)CommandBoltsHotkey;
        /// <summary> Hold to select multiple </summary>
        internal static KeyCode MultiSelect = KeyCode.LeftShift;
        public static int MultiSelectKeySav = (int)MultiSelect;
        /// <summary> Access the AI modal </summary>
        internal static KeyCode ModeSelect = KeyCode.J;
        public static int ModeSelectKeySav = (int)ModeSelect;
        /// <summary> Interact with NPTss </summary>
        internal static KeyCode NPTInteract = KeyCode.T;
        public static int NPTInteractKeySav = (int)NPTInteract;

        public static float TerrainHeight => ManWorldGeneratorExt.CurrentTotalHeight;
        public static float TerrainHeightOffset => ManWorldGeneratorExt.CurrentMinHeight;

        /// <summary>
        /// This is the Tech limit PER AI team
        /// </summary>
        internal static int EnemyTeamTechLimit = 6;// Allow the bases plus 6 additional capacity of the AIs' choosing

        public static float SavedDefaultEnemyFragility;
        public static float SavedDefaultBlockSurvivalChance = -1f;
        public static int SavedDefaultPopulationLimit = -1;
        internal static int initCycleCount = 0;
        internal static int deInitCycleCount = 0;
        public static bool DoLogHealthPulse = false;
        public static bool DoLogOwnership = false;
        private static bool healthPulseScheduled = false;
        // REVISED: NEW — 10s diagnostic pulse; always emits the spawn-attempt summary, and (when DoLogHealthPulse) logs helper/team/anchor counts.
        internal static void EmitHealthPulse()
        {
            try { World.ManEnemyWorld.EmitSpawnAttemptSummary(); } catch {  }
            if (!DoLogHealthPulse) return;
            try
            {
                int playerHelpers = 0, enemyHelpers = 0, anchored = 0;
                foreach (var h in AI.AIECore.IterateAllHelpers())
                {
                    if (h == null) continue;
                    if (h.AIAlign == AIAlignment.Player) playerHelpers++;
                    else if (h.AIAlign == AIAlignment.NonPlayer) enemyHelpers++;
                    if (h.tank != null && h.tank.IsAnchored) anchored++;
                }
                DebugTAC_AI.LogTagged("Pulse", "Helpers: total=" + AI.AIECore.HelperCountNoCheck
                    + " player=" + playerHelpers + " enemy=" + enemyHelpers + " anchored=" + anchored);
                DebugTAC_AI.LogTagged("Pulse", "Strategic: NPTeams=" + World.ManEnemyWorld.NPTeamCountForPulse);
            }
            catch (Exception e) { DebugTAC_AI.Log("[TAC_AI:Pulse] failed - " + e); }
        }

        public static int MaxEnemyWorldCapacity
        {
            get
            {
                return AIPopMaxLimit;
            }
        }// How many techs that can exist before giving up tech splitting?
        internal static int MaxEnemyBaseLimit = 3;  // How many different enemy team bases are allowed to exist in one instance
        // REVISED: HQ limit raised from 1 to 24.
        internal static int MaxEnemyHQLimit = 24;    // How many HQs are allowed to exist in one instance
        /// <summary>
        /// Maker bases (excludes defenses)
        /// </summary>
        internal static int MaxBasesPerTeam = 6;    // How many base expansions can a single team perform?

        /// <summary>
        /// For handling Operations
        /// </summary>
        internal static short AIClockPeriod // How frequently we update
        {
            get
            {
                return ManNetwork.IsNetworked ? AIGlobals.NetAIClockPeriod : AIClockPeriodSet;
            }
        }
        public static short AIClockPeriodSet = 10;        // How frequently we update

#if STEAM
        public static bool EnableBetterAI = false;  // This is toggled based on if the mod is "enabled" by official
#else
        public static bool EnableBetterAI = true;
#endif
        // Selected global AI form/mode id (Vanilla / Basic / Modified / future). Persisted via config; applied to
        // AIFormRegistry.SetActive after forms are discovered. Default = the full Modified behavior.
        public static string AIFormSelected = "Modified";
        internal static bool AutopilotPlayer => AutopilotPlayerMain;
        internal static bool AutopilotPlayerMain {
            get => _AutopilotPlayerMain;
            set {
                if (_AutopilotPlayerMain != value)
                {
                    OnSetPlayerAutopilot(value);
                    _AutopilotPlayerMain = value;
                }
            }
        }
        private static bool _AutopilotPlayerMain = false;
        private static void OnSetPlayerAutopilot(bool state)
        {
            if (state)
            {
                UIHelpersExt.BigF5broningBannerSP("Self-Driving Enabled");
                Tank playerTank = Singleton.playerTank;
                if (playerTank)
                {
                    var tankInst = playerTank.GetHelperInsured();
                    if (tankInst)
                    {
                        tankInst.RTSDestination = playerTank.boundsCentreWorldNoCheck;
                        ManWorldRTS.inst.TechMovementQueue.Remove(tankInst.tank.visible.ID);
                        if (tankInst.lastAIType != AITreeType.AITypes.Escort)
                            tankInst.WakeAIForChange(true);
                        tankInst.SetRTSState(true);
                        // REVISED: requests a reasoned controller swap (PlayerAutopilot) instead of bluntly setting MovementAIControllerDirty.
                        tankInst.RequestMovementControllerSwap(TankAIHelper.MovementSwapReason.PlayerAutopilot);
                    }
                }
            }
            else
            {
                UIHelpersExt.BigF5broningBannerSP("Self-Driving Disabled");
            }
        }

        /// <summary>
        /// For handing Directors
        /// </summary>
        public static int AIDodgeCheapness = 20;
        // REVISED: NEW config tunable — period (seconds) of the combat circle/face duty cycle; combat alternates between circling and facing proportional to turret fraction.
        // DECISIVE default 0: period <= 0.05 makes CombatWantsCircleNow() a STABLE decision (mostly-turreted techs circle, mostly-fixed face) instead of flipping
        // drive direction on a timer (the timed flip + random per-tech phase read as "drives off in random directions" / lost commitment). Raise it to re-enable the cycle.
        public static float CombatFacingCyclePeriod = 0f;
        public static int AIPopMaxLimit = 8;
        public static bool MuteNonPlayerRacket = true;
        public static bool DisplayEnemyEvents = true;
        public static bool AllowOverleveledBlockDrops { get { return EnemyBlockDropChance == 100; } } // Obsolete - true when
        public static bool enablePainMode = true;
        public static bool EnemyEradicators = false;    // Insanely large or powerful Techs
        public static bool EnemiesHaveCreativeInventory = false;

        public static bool AISelfRepair => ManNetwork.IsNetworked ? AllowAISelfRepairInMP : AllowAISelfRepair;
        public static bool AllowAISelfRepair = true;
        public static bool AllowAISelfRepairInMP = false;

        public static int ForceRemoveOverEnemyMaxCap = 4;
        public static bool ActiveSpawnFoundersOffScene = false;
        /// <summary> % Chance NPT Founders spawn when a tile is loaded for the first time </summary>
        public static float SpawnFoundersPositional = 0.05f;//0.2f;
        internal static bool AllowEnemiesToStartBases { get { return MaxEnemyBaseLimit != 0; } }
        internal static bool AllowEnemyBaseExpand { get { return MaxBasesPerTeam != 0; } }
        public static int LandEnemyOverrideChance {
            get { return LandEnemyOverrideChanceSav; }
            internal set { LandEnemyOverrideChanceSav = value; }
        }
#if STEAM
        public static int LandEnemyOverrideChanceSav = 20;
#else
        public static int LandEnemyOverrideChanceSav = 10;
#endif
        public static bool AllowAirEnemiesToSpawn = true;
        public static float AirEnemiesSpawnRate = 1;
        public static bool AllowSeaEnemiesToSpawn = true;
        /// <summary>
        /// Block spawning of Vanilla Techs when applicable
        /// </summary>
        public static bool TryForceOnlyPlayerSpawns = false;
        /// <summary>
        /// Block spawning of This mod's Global Population Techs when applicable
        /// </summary>
        public static bool TryForceOnlyPlayerLocalSpawns = false;
        public static bool AllowEnemiesToMine = true;
        public static bool DesignsToLog = false;
        public static bool CommitDeathMode = false;
        public static bool CatMode = false;

        /// <summary>
        /// Allows player to use the RTS HUD
        /// </summary>
        public static bool AllowPlayerRTSHUD = true;
        /// <summary>
        /// Allows the enemies to act outside of the active play area.  Might be laggy
        /// </summary>
        public static bool AllowStrategicAI = true;
        public static List<EnemyMaxDistLimit> limitTypes = new List<EnemyMaxDistLimit>()
        {
            new EnemyMaxDistLimit("Never"),
            new EnemyMaxDistLimit("Beyond 4 Tiles"),
            new EnemyMaxDistLimit("Beyond 8 Tiles"),
            new EnemyMaxDistLimit("Beyond 16 Tiles"),
            new EnemyMaxDistLimit("Beyond 32 Tiles"),
        };
        public static List<BaseTeamsUpdateRate> limitAIBaseRate = new List<BaseTeamsUpdateRate>()
        {
            new BaseTeamsUpdateRate("Random 4"),
            new BaseTeamsUpdateRate("Random 8"),
            new BaseTeamsUpdateRate("Random 16"),
            new BaseTeamsUpdateRate("Random 32"),
            new BaseTeamsUpdateRate("ALL"),
        };
        public static int CullFarEnemyBasesMode = 0;
        public static bool CullFarEnemyBases => CullFarEnemyBasesDistance != int.MaxValue;
        public static void UpdateCullDist()
        {
            switch (CullFarEnemyBasesMode)
            {
                case 0:
                    CullFarEnemyBasesDistance = int.MaxValue;
                    break;
                case 1:
                    CullFarEnemyBasesDistance = 4;
                    break;
                case 2:
                    CullFarEnemyBasesDistance = 8;
                    break;
                case 3:
                    CullFarEnemyBasesDistance = 16;
                    break;
                case 4:
                    CullFarEnemyBasesDistance = 32;
                    break;
                default:
                    throw new NotImplementedException("Unknown EnemyMaxDistLimit mode: " + CullFarEnemyBasesMode);
            }
        }
        public static int CullFarEnemyBasesDistance = 8;// How far from the player should enemy bases be removed
        public static int EnemyBaseUpdateMode = 2;
        // from the world? IN TILES
        public static float EnemySellGainModifier = 1; // multiply enemy sell gains by this value

        //public static bool DestroyTreesInWater = false;

        // Set on startup

        // MOD SUPPORT
        internal static bool IsRandomAdditionsPresent = false;
        internal static bool isWaterModPresent = false;
        internal static bool isControlBlocksPresent = false;
        internal static bool isTweakTechPresent = false;
        internal static bool isWeaponAimModPresent = false;
        // REVISED: NEW — Circle attack mode (which needs target leading) is only forced continuous when TweakTech or WeaponAimMod is present.
        internal static bool ShouldForceContinuousStrafe() => isTweakTechPresent || isWeaponAimModPresent;
        internal static bool isBlockInjectorPresent = false;
        internal static bool isPopInjectorPresent = false;
        internal static bool isAnimeAIPresent = false;

        internal static bool isConfigHelperPresent = false;
        internal static bool isNativeOptionsPresent = false;


        // Set ingame
        public static int Difficulty
        {
            get
            {
                if (CommitDeathMode)
                    return 9001;
                else
                    return difficulty;
            }
        }

#if STEAM
        public static int difficulty = 85; // insure that only smart enemies spawn for STEAM release for now
#else
        public static int difficulty = 50;
#endif
        // REVISED: NEW difficulty bounds — ApplyDifficulty clamps to the floor only (no upper cap enforced); slider runs to 1500.
        public const int DifficultyMin = -50;
        public const int DifficultyMax = 1500;

        public static int EnemyBlockDropChance = 40;

        public static bool WarnOnEnemyLock = true;
        public static bool DisableEnemyFogOfWar = true;


        //Calculated
        public static int LastRawTechCount = 0;
        public static int LowerDifficulty { get { return Mathf.Clamp(Difficulty - 50, 0, 99); } }
        public static int UpperDifficulty { get { return Mathf.Clamp(Difficulty + 50, 1, 100); } }
        public static int BaseDifficulty
        {
            get
            {
                if (CommitDeathMode)
                    return 9;
                else
                {
                    return 6 + (int)(Difficulty / 50);
                }
            }
        }

        public static int lastPlayerTechPrice = 0;
        public static int EnemySpawnPriceMatching
        {
            get
            {
                if (CommitDeathMode)
                    return int.MaxValue;
                if (Singleton.playerTank.IsNotNull())
                {
                    lastPlayerTechPrice = RawTechBase.GetBBCost(Singleton.playerTank);
                }
                int priceMax = (int)((((float)(Difficulty + 50) / 100) + 0.5f) * lastPlayerTechPrice);
                // Easiest results in 50% max player cost spawns, Hardest results in 250% max player cost spawns, Regular is is 150% max player cost spawns.
                return Mathf.Max(lastPlayerTechPrice / 2, priceMax);
            }
        }
        public static bool AllowSniperSpawns { get { return LowerDifficulty >= 50; } }

        public static bool CanUseMenu { get { return !ManPauseGame.inst.IsPaused; } }

        public static bool IsIngame { get { return !ManPauseGame.inst.IsPaused && !ManPointer.inst.IsInteractionBlocked; } }

        public static void ReleaseControl(string Name = null)
        {
            string focused = GUI.GetNameOfFocusedControl();
            if (Name == null)
            {
                DebugTAC_AI.Info(KickStart.ModID + ": GUI - Releasing control of " + (focused.NullOrEmpty() ? "unnamed" : focused));
                GUI.FocusControl(null);
                GUI.UnfocusWindow();
                GUIUtility.hotControl = 0;
            }
            else
            {
                if (focused == Name)
                {
                    DebugTAC_AI.Info(KickStart.ModID + ": GUI - Releasing control of " + (focused.NullOrEmpty() ? "unnamed" : focused));
                    GUI.FocusControl(null);
                    GUI.UnfocusWindow();
                    GUIUtility.hotControl = 0;
                }
            }
        }

        internal static bool firedAfterBlockInjector = false;
        public static bool SpecialAttract = false;
        internal static AttractType SpecialAttractNum = 0;
        public static int retryForBote = 0;
        public static Vector3 SpecialAttractPos;

        public static float WaterHeight
        {
            get
            {
                float outValue = -9001;
                try
                {
                    if (isWaterModPresent)
                        outValue = GetWaterHeightCanCrash();
                }
                catch (Exception)
                {
                }
                return outValue;
            }
        }
        public static float GetWaterHeightCanCrash()
        {
            return WaterMod.QPatch.WaterHeight;
        }
        public static Harmony harmonyInstance = new Harmony("legionite.tactical_ai");
        private static bool hasPatched = false;
        internal static bool HasHookedUpToSafeSaves = false;

        internal static bool isSteamManaged = false;

        public static bool VALIDATE_MODS()
        {
            isSteamManaged = LookForMod("NLogManager");

            if (!LookForMod("0Harmony"))
            {
                DebugTAC_AI.ErrorReport("This mod NEEDS Harmony to function!  Please subscribe to it on the Steam Workshop");
                return false;
            }

            isConfigHelperPresent = ModStatusChecker.IsConfigHelperPresent;
            isNativeOptionsPresent = ModStatusChecker.IsNativeOptionsPresent;
            if (isConfigHelperPresent && !isNativeOptionsPresent)
            {
                DebugTAC_AI.Warning("ConfigHelper is active but NativeOptions is missing! You need both to use ingame settings.");
            }
            if (!isConfigHelperPresent && isNativeOptionsPresent)
            {
                DebugTAC_AI.Warning("NativeOptions is active but ConfigHelper is missing! You need both to use ingame settings.");
            }
            if (!isSteamManaged && (isConfigHelperPresent || isNativeOptionsPresent))
            {
                DebugTAC_AI.Warning("Ingame settings requires launching from TTSMM (with 0LogManager) to insure success");
            }

            if (LookForMod("RandomAdditions"))
            {
                DebugTAC_AI.Log(ModID + ": Found RandomAdditions!  Enabling advanced AI for parts!");
                IsRandomAdditionsPresent = true;
            }
            else IsRandomAdditionsPresent = false;

            if (LookForMod("WaterMod"))
            {
                try
                {
                    _ = GetWaterHeightCanCrash();
                    isWaterModPresent = true;
                    DebugTAC_AI.Log(ModID + ": Found Water Mod!  Enabling water-related features!");
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(ModID + ": Found Water Mod!  Failed to hook... " + e);
                }
            }
            else isWaterModPresent = false;

            if (LookForMod("Control Block"))
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Control Blocks!  Letting RawTech loader override unassigned swivels to auto-target!");
                isControlBlocksPresent = true;
            }
            else isControlBlocksPresent = false;

            if (LookForMod("WeaponAimMod"))
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Found WeaponAimMod!  Halting aim-related changes and letting WeaponAimMod take over!");
                isWeaponAimModPresent = true;
            }
            else isWeaponAimModPresent = false;

            if (LookForMod("TweakTech"))
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Found TweakTech!  Applying changes to AI!");
                isTweakTechPresent = true;
            }
            else isTweakTechPresent = false;

            if (LookForMod("BlockInjector"))
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Found Block Injector!  Setting up modded base support!");
                isBlockInjectorPresent = true;
            }
            else isBlockInjectorPresent = false;

            if (LookForMod("PopulationInjector"))
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Found Population Injector!  Holding off on using built-in spawning system!");
                isPopInjectorPresent = true;
            }
            else isPopInjectorPresent = false;

            if (LookForMod("AnimeAI"))
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Found Anime AI!  Hooking into commentary system and actions!");
                isAnimeAIPresent = true;
            }
            else isAnimeAIPresent = false;

            SafeSaves.ManSafeSaves.DisableExternalBackupSaving = true;
            return true;
        }

        // REVISED: NEW per-batch patch guard — each batch is tracked in patchedBatches so a retry after a partial-patch failure won't double-patch already-applied batches.
        private static HashSet<Type> patchedBatches = new HashSet<Type>();
        private static void PatchBatchOnce(Type batch)
        {
            if (patchedBatches.Contains(batch))
                return;
            if (MassPatcher.MassPatchAllWithin(harmonyInstance, batch, "TACtical_AI"))
                patchedBatches.Add(batch);
            else
                DebugTAC_AI.ErrorReport("Error on patching " + batch.Name);
        }
        public static void PatchMod()
        {
            DebugTAC_AI.Log(KickStart.ModID + ": Patch Call");
            if (!hasPatched)
            {
                try
                {
                    PatchBatchOnce(typeof(GlobalPatches));
                    PatchBatchOnce(typeof(ManagerPatches));
                    PatchBatchOnce(typeof(UIPatches));
                    PatchBatchOnce(typeof(ModulePatches));

                    harmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
                    DebugTAC_AI.Log(KickStart.ModID + ": Patched");
                    hasPatched = true;
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Error on patch");
                    DebugTAC_AI.Log(e);
                    DebugTAC_AI.ErrorReport("Error on patching base game");
                }
            }
        }

        public static void HookToSafeSaves()
        {
            try
            {
                Assembly assemble = Assembly.GetExecutingAssembly();
                DebugTAC_AI.Log(KickStart.ModID + ": DLL is " + assemble.GetName());
                ManSafeSaves.RegisterSaveSystem(assemble, OnSaveManagers, OnLoadManagers);
                HasHookedUpToSafeSaves = true;
            }
            // REVISED: SafeSaves hook failure now logs the exception and raises FatalError (was a plain ErrorReport).
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Error on RegisterSaveSystem: " + e);
                DebugTAC_AI.FatalError(KickStart.ModID + ": Error on hooking to SafeSaves — save/load of mod state will NOT work this session.");
            }
        }

        public static void InitSpecialPatch()
        {
            DebugTAC_AI.DoLogLoading = true;
            Debug_TTExt.LogAll = true;
            var targetMethod = typeof(SnapshotServiceDesktop).GetMethod("UpdateSnapshotCacheOnStartup", BindingFlags.Instance | BindingFlags.Public).
                GetCustomAttribute<IteratorStateMachineAttribute>().StateMachineType.
                GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
            harmonyInstance.Patch(targetMethod, transpiler: new HarmonyMethod(typeof(KickStart).
                GetMethod("SpecialPatchTranspiler", BindingFlags.NonPublic | BindingFlags.Static)));
            BlockIndexer.UseVanillaFallbackSnapUtility = false;
        }
        private static IEnumerable<CodeInstruction> SpecialPatchTranspiler(IEnumerable<CodeInstruction> collection)
        {
            bool found = false;
            foreach (var item in collection)
            {
                if (!found && item.operand is string str && str == "/Snapshots")
                    item.operand = "/SnapshotsCommunity";
                yield return item;
            }
        }

        public static void MainOfficialInit()
        {

            //Where the fun begins
#if STEAM
            DebugTAC_AI.Log(KickStart.ModID + ": MAIN (Steam Workshop Version) startup");
            if (!VALIDATE_MODS())
            {
                return;
            }
#else
            DebugTAC_AI.Log(KickStart.ModID + ": Startup was invoked by TTSMM!  Set-up to handle LATE initialization.");
#endif
            //throw new NullReferenceException("CrashHandle");

            //SafeSaves.DebugSafeSaves.LogAll = true;
            //Initiate the madness
            HookToSafeSaves();

            //TinySettingsUtil.TryLoadFromDiskStatic<AIGlobals>("TAC_AI_Globals");

            LegModExt.InsurePatches();
            ManBaseTeams.Initiate();
            TankAIManager.Initiate();
            AIGlobals.InitSharedMenu();
            GUIAIManager.Initiate();
            RawTechExporter.Initiate();
            RLoadedBases.BaseFunderManager.Initiate();
            ManEnemyWorld.Initiate();
            SpecialAISpawner.Initiate();
            GUINPTInteraction.Initiate();

            PatchMod();

            AIERepair.RefreshDelays();
            // Because official fails to init this while switching modes
            SpecialAISpawner.DetermineActiveOnModeType();
            TankAIManager.inst.CorrectBlocksList();

            InitSettings();

            if (!isPopInjectorPresent)
                OverrideEnemyMax();
            _ = AIWiki.hintADV;
#if STEAM
            EnableBetterAI = true;
#endif
            if (CustomAttract.Attracts == null)
            {
                CustomAttract.Attracts = CustomAttract.InitAttracts;
            }
            AIWiki.InitWiki();
            ManGameMode.inst.ModeSwitchEvent.Subscribe(OnModeSwitch);
#if DEBUG
            InitSpecialPatch();
#endif
            InvokeHelper.BlocksPostChangeEvent.Subscribe(AfterBlocksLoaded);
        }
        private static void AfterBlocksLoaded()
        {
            DelayedBaseLoader();
        }
        private static void OnModeSwitch()
        {
            factionAttractOST = FactionSubTypes.NULL;
            var prof = ManProfile.inst.GetCurrentUser();
            if (prof != null)
            {
                ManMusic.inst.SetMusicMixerVolume(prof.m_SoundSettings.m_MusicVolume);
            }
        }

#if STEAM
        // REVISED: boot sequence reordered/hardened — failed VALIDATE_MODS now yield breaks (was falling through); LegModExt.InsurePatches added; PatchMod moved up to mid-init (after GUINPTInteraction, before RefreshDelays); OverrideEnemyMax (gated on !PopInjector), AIWiki warmup/InitWiki, and the BlocksPostChangeEvent->AfterBlocksLoaded subscription folded in.
        public static IEnumerable<float> MainOfficialInitIterate()
        {
            //Where the fun begins
            DebugTAC_AI.Log(KickStart.ModID + ": MAIN [ITERATOR] (Steam Workshop Version) startup");
            if (!VALIDATE_MODS())
            {
                yield return 1f;
                yield break;
            }

            //Initiate the madness
            HookToSafeSaves();
            LegModExt.InsurePatches();
            yield return 0.14f;

            ManBaseTeams.Initiate();
            TankAIManager.Initiate();
            yield return 0.22f;

            AIGlobals.InitSharedMenu();
            GUIAIManager.Initiate();
            yield return 0.30f;

            RawTechExporter.Initiate();
            yield return 0.38f;

            RLoadedBases.BaseFunderManager.Initiate();
            yield return 0.46f;

            ManEnemyWorld.Initiate();
            yield return 0.54f;

            SpecialAISpawner.Initiate();
            yield return 0.62f;

            GUINPTInteraction.Initiate();
            yield return 0.68f;

            PatchMod();
            yield return 0.76f;

            AIERepair.RefreshDelays();
            SpecialAISpawner.DetermineActiveOnModeType();
            TankAIManager.inst.CorrectBlocksList();
            InitSettings();
            yield return 0.86f;

            if (!isPopInjectorPresent)
                OverrideEnemyMax();
            _ = AIWiki.hintADV;
            EnableBetterAI = true;
            if (CustomAttract.Attracts == null)
            {
                CustomAttract.Attracts = CustomAttract.InitAttracts;
            }
            AIWiki.InitWiki();
            ManGameMode.inst.ModeSwitchEvent.Subscribe(OnModeSwitch);
            InvokeHelper.BlocksPostChangeEvent.Subscribe(AfterBlocksLoaded);
            yield return 1f;
        }
        private static bool launched = false;
        public static void ENCAPSULATEErrorSaveConfig()
        {
            try
            {
                KickStartConfigHelper.Save();
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Error on Option & Config saving");
                DebugTAC_AI.Log(e);
                DebugTAC_AI.ErrorReport("Error on hook with ConfigHelper");
            }
        }
        public static void ENCAPSULATEErrorInitConfig()
        {
            try
            {
                KickStartConfigHelper.PushExtModConfigHandling();
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Error on Option & Config setup");
                DebugTAC_AI.Log(e);
                DebugTAC_AI.ErrorReport("Error on hooks with ConfigHelper/NativeOptions");
            }
        }
        public static void ENCAPSULATEErrorInitModOptions()
        {
            try
            {
                KickStartNativeOptions.PushExtModOptionsHandling();
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Error on NativeOptions setup");
                DebugTAC_AI.Log(e);
                DebugTAC_AI.ErrorReport("Error on hooks with NativeOptions");
            }
        }
        public static void InitSettings()
        {
            if (launched)
                return;
            launched = true;
            // REVISED: bumps the init lifecycle counter and schedules the 10s EmitHealthPulse repeat (once).
            initCycleCount++;
            DebugTAC_AI.Log("[TAC_AI:Lifecycle] Init #" + initCycleCount
                + " (DeInit cycles seen: " + deInitCycleCount + ")");
            if (!healthPulseScheduled)
            {
                InvokeHelper.InvokeSingleRepeat(EmitHealthPulse, 10f);
                healthPulseScheduled = true;
            }
            GUINPTInteraction.InsureNetHooks();
            if (isNativeOptionsPresent && isConfigHelperPresent)
            {
                try
                {
                    ENCAPSULATEErrorInitConfig();
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Error on Option & Config setup");
                    DebugTAC_AI.Log(e);
                    DebugTAC_AI.ErrorReport("Error on hooks with ConfigHelper/NativeOptions");
                }
            }
            else if (isNativeOptionsPresent)
            {
                try
                {
                    ENCAPSULATEErrorInitModOptions();
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Error on NativeOptions setup");
                    DebugTAC_AI.Log(e);
                    DebugTAC_AI.ErrorReport("Error on hooks with NativeOptions");
                }
            }
            else if (isConfigHelperPresent)
            {
                try
                {
                    KickStartConfigHelper.PushExtModConfigHandlingConfigOnly();
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Error on ConfigHelper setup");
                    DebugTAC_AI.Log(e);
                    DebugTAC_AI.ErrorReport("Error on hooks with ConfigHelper");
                }
            }

            UpdateCullDist();
            //InvokeHelper.ModsPostLoadEvent.Subscribe(AIWiki.InsureAllValidAIs);
            InvokeHelper.BlocksPostChangeEvent.Subscribe(AIWiki.InsureAllValidAIs);
        }

        public static void DeInitCheck()
        {
            if (TankAIManager.inst)
            {
                TankAIManager.inst.CheckNextFrameNeedsDeInit();
            }
        }

        public static void DeInitALL()
        {
            ManGameMode.inst.ModeSwitchEvent.Unsubscribe(OnModeSwitch);
            float timeStart = Time.realtimeSinceStartup;
            DebugTAC_AI.Log(KickStart.ModID + ": Doing mod DeInit. Garbage Cleanup... current " + GC.GetTotalMemory(false));
            EnableBetterAI = false;
            // REVISED: DeInit now clears the launched guard, unsubscribes the BlocksPostChangeEvent handlers, and cancels the EmitHealthPulse repeat so re-init starts clean.
            launched = false;

            try
            {
                InvokeHelper.BlocksPostChangeEvent.Unsubscribe(AfterBlocksLoaded);
                InvokeHelper.BlocksPostChangeEvent.Unsubscribe(AIWiki.InsureAllValidAIs);
            }
            catch (Exception eEvt)
            {
                DebugTAC_AI.Log(ModID + ": [DeInit] BlocksPostChangeEvent unsubscribe failed - " + eEvt);
            }

            if (healthPulseScheduled)
            {
                try { InvokeHelper.CancelInvokeSingleRepeat(EmitHealthPulse); } catch { }
                healthPulseScheduled = false;
            }

            deInitCycleCount++;
            DebugTAC_AI.Log("[TAC_AI:Lifecycle] DeInit #" + deInitCycleCount
                + " (Init cycles seen: " + initCycleCount + ")");

            // REVISED: NEW symmetric restore — puts vanilla block-survival chance, enemy fragility and the pop limit back to their saved defaults, then clears the popup cache.
            try
            {
                if (Globals.inst != null)
                {
                    if (SavedDefaultBlockSurvivalChance >= 0f)
                    {
                        DebugTAC_AI.Log(ModID + ": [DeInit] Globals.m_BlockSurvivalChance: "
                            + Globals.inst.m_BlockSurvivalChance + " → " + SavedDefaultBlockSurvivalChance + " (restored)");
                        Globals.inst.m_BlockSurvivalChance = SavedDefaultBlockSurvivalChance;
                    }
                    if (SavedDefaultEnemyFragility > 0f)
                    {
                        DebugTAC_AI.Log(ModID + ": [DeInit] Globals.detachMeterFillFactor: "
                            + Globals.inst.moduleDamageParams.detachMeterFillFactor + " → "
                            + SavedDefaultEnemyFragility + " (restored)");
                        Globals.inst.moduleDamageParams.detachMeterFillFactor = SavedDefaultEnemyFragility;
                    }
                }
                if (SavedDefaultPopulationLimit >= 0 && ManPop.inst != null && limitBreak != null)
                {
                    DebugTAC_AI.Log(ModID + ": [DeInit] ManPop.m_PopulationLimit restored to " + SavedDefaultPopulationLimit);
                    limitBreak.SetValue(ManPop.inst, SavedDefaultPopulationLimit);
                    SavedDefaultPopulationLimit = -1;
                }
                AIGlobals.ResetPopupCache();
            }
            catch (Exception eRestore)
            {
                DebugTAC_AI.Log(ModID + ": [DeInit] settings restore failed - " + eRestore);
            }
            if (hasPatched)
            {
                try
                {
                    if (CustomAttract.Attracts != null)
                    {
                        foreach (var item in CustomAttract.Attracts)
                        {
                            item.Release();
                        }
                        CustomAttract.Attracts = null;
                    }
                    MassPatcher.MassUnPatchAllWithin(harmonyInstance, typeof(ModulePatches), "TACtical_AI");
                    MassPatcher.MassUnPatchAllWithin(harmonyInstance, typeof(UIPatches), "TACtical_AI");
                    MassPatcher.MassUnPatchAllWithin(harmonyInstance, typeof(ManagerPatches), "TACtical_AI");
                    MassPatcher.MassUnPatchAllWithin(harmonyInstance, typeof(GlobalPatches), "TACtical_AI");
                    harmonyInstance.UnpatchAll("legionite.tactical_ai");
                    hasPatched = false;
                    patchedBatches.Clear();
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Error on un-patch");
                    DebugTAC_AI.Log(e);
                }
            }

            // DE-INIT ALL
            // REVISED: teardown order changed — GUINPTInteraction now torn down first and ManBaseTeams last; the AIEPathMapper.DepoolUnusedTiles call was dropped.
            // Step 7: tear down the modular-AI registry (run host-only tunable restores, drop behavior/profile registrations).
            TAC_AI.AI.Engine.AIModuleBootstrap.DeInitAIModules();
            GUINPTInteraction.DeInit();
            SpecialAISpawner.DeInit();
            ManEnemyWorld.DeInit();
            RLoadedBases.BaseFunderManager.DeInit();
            RawTechExporter.DeInit();
            GUIAIManager.DeInit();
            TankAIManager.DeInit();
            ManBaseTeams.DeInit();
            try
            {
                ManSafeSaves.UnregisterSaveSystem(Assembly.GetExecutingAssembly(), OnSaveManagers, OnLoadManagers);
                HasHookedUpToSafeSaves = false;
            }
            catch { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            DebugTAC_AI.Log(KickStart.ModID + ": Garbage Cleanup finished with " + GC.GetTotalMemory(false) + " remaining.");
            DebugTAC_AI.Log(KickStart.ModID + ": Operation took " + (Time.realtimeSinceStartup - timeStart) + " seconds.");
        }

#else
        public static void Main()
        {
            //Where the fun begins
            DebugTAC_AI.Log(KickStart.ModID + ": MAIN (TTMM Version) startup");
            if (!VALIDATE_MODS())
                return;
            //Initiate the madness
#if DEBUG
            DebugTAC_AI.Log("-----------------------------------------");
            DebugTAC_AI.Log("-----------------------------------------");
            DebugTAC_AI.Log("        !!! TAC_AI DEBUG MODE !!!");
            DebugTAC_AI.Log("-----------------------------------------");
            DebugTAC_AI.Log("-----------------------------------------");
#endif
            try
            {
                if (isSteamManaged)
                {   // Since TTSMM launches this instead LATE when +managettmm is active, we need to compensate by initing ALL on init in this case.
                    MainOfficialInit();
                    return;
                }
                PatchMod();
                HookToSafeSaves();
                // REVISED: TTMM init path brought in line with the Steam path — added LegModExt.InsurePatches, ManBaseTeams/InitSharedMenu/SpecialAISpawner/GUINPTInteraction init; uses RLoadedBases.BaseFunderManager (was RBases) and drops the old GUIEvictionNotice.Initiate.
                LegModExt.InsurePatches();

                ManBaseTeams.Initiate();
                TankAIManager.Initiate();
                AIGlobals.InitSharedMenu();
                GUIAIManager.Initiate();
                RawTechExporter.Initiate();
                RLoadedBases.BaseFunderManager.Initiate();
                ManEnemyWorld.Initiate();
                SpecialAISpawner.Initiate();
                GUINPTInteraction.Initiate();

                AIERepair.RefreshDelays();
                try
                {
                    KickStartOptions.PushExtModOptionsHandling();
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Error on Option & Config setup");
                    DebugTAC_AI.Log(e);
                }
            }
            catch
            {
                try
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Error on TTMM Init setup - there are missing dependancies!");
                    DebugTAC_AI.FatalError("Make sure you have installed SafeSaves (and RandomAdditions if it crashes again on start) in TTMM.");
                }
                catch { };
            }
        }

#endif

        public static void DelayedBaseLoader()
        {
            DebugTAC_AI.Log(KickStart.ModID + ": LAUNCHED MODDED BLOCKS BASE VALIDATOR");
            BlockIndexer.ConstructBlockLookupList();
            ModTechsDatabase.ValidateAllStringTechs();
            DebugRawTechSpawner.Initiate();
            firedAfterBlockInjector = true;
        }

        // Phase 3.4 (FIX-PLAN.md): expose save/load lifecycle as static events so the Smart
        // subsystem (and any future subsystem) can subscribe without competing with the
        // existing ManSafeSaves registration. Doing == true → pre-save / pre-load; Doing
        // == false → post-save / post-load. Subscribers must be exception-safe; KickStart
        // catches and logs.
        public static event Action<bool> SaveSink;
        public static event Action<bool> LoadSink;

        public static void OnSaveManagers(bool Doing)
        {
            if (Doing)
            {
                ManBaseTeams.OnWorldSave();
                ManWorldRTS.OnWorldSave();
            }
            else
            {
                ManBaseTeams.OnWorldFinishSave();
                ManWorldRTS.OnWorldFinishSave();
            }
            try { SaveSink?.Invoke(Doing); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("KickStart.SaveSink: " + ex.Message); }
        }
        public static void OnLoadManagers(bool Doing)
        {
            DebugTAC_AI.Log("OnLoadManagers");
            if (Doing)
            {
                ManBaseTeams.OnWorldPreLoad();
                ManWorldRTS.OnWorldPreLoad();
            }
            else
            {
                ManBaseTeams.OnWorldLoad();
                ManWorldRTS.OnWorldLoad();
            }
            try { LoadSink?.Invoke(Doing); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("KickStart.LoadSink: " + ex.Message); }
        }

        internal static FieldInfo limitBreak = typeof(ManPop).GetField("m_PopulationLimit", BindingFlags.NonPublic | BindingFlags.Instance);
        // REVISED: FatalErrors if the reflection field is missing, stashes the vanilla pop limit into SavedDefaultPopulationLimit (for DeInit restore) before overriding, and logs on failure instead of swallowing.
        public static void OverrideEnemyMax()
        {
            if (limitBreak == null)
            {
                DebugTAC_AI.FatalError(ModID + ": OverrideEnemyMax - ManPop.m_PopulationLimit field not found (vanilla API change?). Pop cap NOT overridden; enemy spawns will be capped at vanilla limit.");
                return;
            }
            try
            {
                if (SavedDefaultPopulationLimit < 0)
                    SavedDefaultPopulationLimit = (int)limitBreak.GetValue(ManPop.inst);
                limitBreak.SetValue(ManPop.inst, AIPopMaxLimit);
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(ModID + ": OverrideEnemyMax failed - " + e);
            }
        }

        public static void TryHookUpToSafeSavesIfNeeded()
        {
            if (!HasHookedUpToSafeSaves)
            {
                try
                {
                    ManSafeSaves.RegisterSaveSystem(Assembly.GetExecutingAssembly());
                    HasHookedUpToSafeSaves = true;
                }
                catch { DebugTAC_AI.Log(KickStart.ModID + ": Error on RegisterSaveSystem"); }
            }
        }

        public static bool LookForMod(string name)
        {
            if (name == "RandomAdditions")
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.FullName.StartsWith(name))
                    {
                        if (assembly.GetType("KickStart") != null)
                            return true;
                    }
                }
            }
            else
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.FullName.StartsWith(name))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Only call for cases where we want only vanilla corps!
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static FactionSubTypes GetCorpExtended(BlockTypes type)
        {
            return (FactionSubTypes)Singleton.Manager<ManSpawn>.inst.GetCorporation(type);
        }

        public static bool TransferLegacyIfNeeded(AIType type, out AIType newType, out AIDriverType driver)
        {
            newType = type;
            driver = AIDriverType.Tank;
            if (type >= AIType.Aviator)
            {
                newType = AIType.Escort;
                switch (type)
                {
                    case AIType.Astrotech:
                        driver = AIDriverType.Astronaut;
                        break;
                    case AIType.Aviator:
                        driver = AIDriverType.Pilot;
                        break;
                    case AIType.Buccaneer:
                        driver = AIDriverType.Sailor;
                        break;
                }
                return true;
            }
            return false;
        }
        public static AIType GetLegacy(AIType type, AIDriverType driver)
        {
            if (type == AIType.Escort)
            {
                switch (driver)
                {
                    case AIDriverType.Astronaut:
                        return AIType.Astrotech;
                    case AIDriverType.Pilot:
                        return AIType.Aviator;
                    case AIDriverType.Sailor:
                        return AIType.Buccaneer;
                    default:
                        return AIType.Escort;
                }
            }
            return type;
        }

    }

    public static class VectorExtentions
    {
        public static bool WithinBox(this Vector3 vec, float extents)
        {
            return vec.x >= -extents && vec.x <= extents && vec.y >= -extents && vec.y <= extents && vec.z >= -extents && vec.z <= extents;
        }
        public static bool WithinSquareXZ(this Vector3 vec, float extents)
        {
            return vec.x >= -extents && vec.x <= extents && vec.z >= -extents && vec.z <= extents;
        }
        public static bool WithinBox(this IntVector2 vec, int extents)
        {
            return vec.x >= -extents && vec.x <= extents && vec.y >= -extents && vec.y <= extents;
        }
        public static Vector3 Clamp01Box(this Vector3 vec)
        {
            return Vector3.Min(Vector3.Max(-Vector3.one, vec), Vector3.one);
        }
        public static float AbsMax(this Vector3 vec)
        {
            return Mathf.Max(Mathf.Abs(vec.x), Mathf.Abs(vec.y), Mathf.Abs(vec.z));
        }
    }
}
