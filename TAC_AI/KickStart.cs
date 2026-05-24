using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
//using Harmony;
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
    public class KickStart
    {
        internal const string ModID = "Advanced AI";
        internal const string ModCommandID = "TAC_AI";

        public static FactionSubTypes factionAttractOST = FactionSubTypes.NULL;

        public static bool UseProcedualEnemyBaseSpawning = false;
        public static bool DoPopSpawnCostCheck = false;

#if STEAM
        public static bool ShouldBeActive = false;//
        public static bool UseClassicRTSControls = true;//
#else
        public static bool ShouldBeActive = true;//
        public static bool UseClassicRTSControls = false;//
#endif
        public static bool UseNumpadForGrouping = false;//
        internal static KeyCode RetreatHotkey = KeyCode.I;
        public static int RetreatHotkeySav = (int)RetreatHotkey;
        internal static KeyCode CommandHotkey = KeyCode.K;
        public static int CommandHotkeySav = (int)CommandHotkey;
        internal static KeyCode CommandBoltsHotkey = KeyCode.X;
        public static int CommandBoltsHotkeySav = (int)CommandBoltsHotkey;
        internal static KeyCode MultiSelect = KeyCode.LeftShift;
        public static int MultiSelectKeySav = (int)MultiSelect;
        internal static KeyCode ModeSelect = KeyCode.J;
        public static int ModeSelectKeySav = (int)ModeSelect;
        internal static KeyCode NPTInteract = KeyCode.T;
        public static int NPTInteractKeySav = (int)NPTInteract;

        public static float TerrainHeight => ManWorldGeneratorExt.CurrentTotalHeight;
        public static float TerrainHeightOffset => ManWorldGeneratorExt.CurrentMinHeight;

        internal static int EnemyTeamTechLimit = 6;// Allow the bases plus 6 additional capacity of the AIs' choosing

        public static float SavedDefaultEnemyFragility;
        public static float SavedDefaultBlockSurvivalChance = -1f;
        public static int SavedDefaultPopulationLimit = -1;
        internal static int initCycleCount = 0;
        internal static int deInitCycleCount = 0;
        public static bool DoLogHealthPulse = false;
        public static bool DoLogOwnership = false;
        private static bool healthPulseScheduled = false;
        internal static void EmitHealthPulse()
        {
            // Spawn-attempt summary always emits when there's activity (it self-gates on dAttempt==0).
            try { World.ManEnemyWorld.EmitSpawnAttemptSummary(); } catch { /* never let pulse crash the InvokeRepeat */ }
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
        internal static int MaxEnemyHQLimit = 24;   // How many HQs are allowed to exist in one instance
        internal static int MaxBasesPerTeam = 6;    // How many base expansions can a single team perform?

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
                        tankInst.RequestMovementControllerSwap(TankAIHelper.MovementSwapReason.PlayerAutopilot);
                    }
                }
            }
            else
            {
                UIHelpersExt.BigF5broningBannerSP("Self-Driving Disabled");
            }
        }

        public static int AIDodgeCheapness = 20;
        public static float CombatFacingCyclePeriod = 4f;   // Seconds for one full circle+face combat cycle (turret-fraction duty cycle). Higher = slower alternation between broadside and facing.
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
        public static bool TryForceOnlyPlayerSpawns = false;
        public static bool TryForceOnlyPlayerLocalSpawns = false;
        public static bool AllowEnemiesToMine = true;
        public static bool DesignsToLog = false;
        public static bool CommitDeathMode = false;
        public static bool CatMode = false;

        public static bool AllowPlayerRTSHUD = true;
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

        internal static bool IsRandomAdditionsPresent = false;
        internal static bool isWaterModPresent = false;
        internal static bool isControlBlocksPresent = false;
        internal static bool isTweakTechPresent = false;
        internal static bool isWeaponAimModPresent = false;
        // T9: combat-FSM Circle mode forces continuous strafe when either mod is present.
        // Both mods reshape aim such that stationary firing is no longer optimal.
        // Single named predicate for the RWheeled.cs:113 mod-presence OR; do NOT generalize to
        // sibling sites (EWeapSetup / AIECore / BAviator / ModulePatches use distinct semantics).
        internal static bool ShouldForceContinuousStrafe() => isTweakTechPresent || isWeaponAimModPresent;
        internal static bool isBlockInjectorPresent = false;
        internal static bool isPopInjectorPresent = false;
        internal static bool isAnimeAIPresent = false;

        internal static bool isConfigHelperPresent = false;
        internal static bool isNativeOptionsPresent = false;

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
        public const int DifficultyMin = -50;
        public const int DifficultyMax = 1500;
        // values > 150 spawn only the smartest enemies
        // -50 means only the simpleton AI spawns

        public static int EnemyBlockDropChance = 40;

        public static bool WarnOnEnemyLock = true;
        public static bool DisableEnemyFogOfWar = true;

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
                    return int.MaxValue; // allow EVERYTHING
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
                    //outValue = -100;
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

            SafeSaves.ManSafeSaves.DisableExternalBackupSaving = true;// Speed it up
            return true;
        }

        // Per-batch patch tracking — if any batch succeeded on a prior call, don't retry it.
        // Prevents double-patch when PatchAll throws (which leaves hasPatched=false) but individual
        // MassPatcher batches already succeeded.
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
            catch (Exception e)
            {
                // Fatal-class: no SafeSaves hook means OnSaveManagers / OnLoadManagers never fire,
                // so ManBaseTeams / ManWorldRTS / ManEnemyWorld state never persists or restores.
                // Continuing past this would silently corrupt saves. Surface as popup.
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
            
#if STEAM
            DebugTAC_AI.Log(KickStart.ModID + ": MAIN (Steam Workshop Version) startup");
            if (!VALIDATE_MODS())
            {
                return;
            }
#else
            DebugTAC_AI.Log(KickStart.ModID + ": Startup was invoked by TTSMM!  Set-up to handle LATE initialization.");
#endif
            HookToSafeSaves();

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

            // PatchMod call sites: line 574 (MainOfficialInit, here), line 653 (iterator path,
            // MainOfficialInitIterate), line 910 (pure-TTMM branch of Main). All guarded by
            // hasPatched + per-batch patchedBatches so duplication is safe.
            PatchMod();

            AIERepair.RefreshDelays();
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
        public static IEnumerable<float> MainOfficialInitIterate()
        {
            DebugTAC_AI.Log(KickStart.ModID + ": MAIN [ITERATOR] (Steam Workshop Version) startup");
            if (!VALIDATE_MODS())
            {
                yield return 1f;
                yield break;
            }

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

            // Patch BEFORE InitSettings — matches MainOfficialInit order (PatchMod@574, InitSettings@580).
            // The previous iterator inverted these, which caused config/options-init to run before
            // Harmony patches were installed (BUG-3).
            PatchMod();
            yield return 0.76f;

            AIERepair.RefreshDelays();
            SpecialAISpawner.DetermineActiveOnModeType();
            TankAIManager.inst.CorrectBlocksList();
            InitSettings();
            yield return 0.86f;

            // Bring iterator in sync with MainOfficialInit (lines 582-597) — these were silently
            // missing from the iterator path, leaving Steam-iterator boots with vanilla population
            // cap, no AIWiki, and no BlocksPostChangeEvent subscription (BUG-3).
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
            DebugTAC_AI.Log(KickStart.ModID + ": MAIN (TTMM Version) startup");
            if (!VALIDATE_MODS())
                return;
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

        /// <summary>
        /// Pipeline 03 (World Load/Save) — SAVE dispatch.
        ///
        /// SafeSaves invokes this twice per save cycle:
        ///   Doing=true   -> pre-snapshot phase  (OnWorldSave on each manager)
        ///   Doing=false  -> post-write phase    (OnWorldFinishSave on each manager)
        ///
        /// Manager call order (intentional, matches OnLoadManagers):
        ///   1. ManBaseTeams (team roster, HiddenVisibles, TradingSellOffers, etc.)
        ///   2. ManWorldRTS  (UnitGroups via UnitGroupsSerial shadow)
        /// </summary>
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
        }
        /// <summary>
        /// Pipeline 03 (World Load/Save) — LOAD dispatch and canonical phase sequence.
        ///
        /// Full ordered sequence for a disk-load (single source of truth — handlers
        /// elsewhere depend on this ordering and must NOT be re-ordered casually):
        ///
        ///   Phase 1 (OnLoadManagers, Doing=true) — PRE-DESERIALIZATION RESET:
        ///     - ManBaseTeams.OnWorldPreLoad()  -> ResetSessionState + null teams sentinel
        ///     - ManWorldRTS.OnWorldPreLoad()   -> clears live UnitGroups buckets
        ///
        ///   Phase 2 (SafeSaves) — DESERIALIZATION:
        ///     SafeSaves populates [SSaveField] members on ManBaseTeams.inst and
        ///     ManWorldRTS.inst from disk. Manager bodies do NOT run during this phase.
        ///
        ///   Phase 3 (OnLoadManagers, Doing=false) — POST-DESERIALIZATION FIX-UP:
        ///     - ManBaseTeams.OnWorldLoad()  -> initializes any [SSaveField] still null
        ///                                     (treats teams==null as "fresh world" sentinel,
        ///                                      runs MigrateTeamsToNewSaveFormat if so);
        ///                                     post-load PurgeOrphanedSellOffers.
        ///     - ManWorldRTS.OnWorldLoad()   -> rebuilds UnitGroups from UnitGroupsSerial;
        ///                                     normalizes bucket count.
        ///
        ///   Phase 4 (ModeStartEvent) — MODE ACTIVATION:
        ///     - ManEnemyWorld.OnWorldLoad(mode) -> clears NPTTeams/RegisteredByID/
        ///                                         QueuedUnitMoves; mode-gate; subscribes
        ///                                         OnTileTechsBeforeLoad + OnWorldLoadEnd.
        ///                                         Flips enabledThis=true, draining any
        ///                                         Harmony-postfix events buffered during
        ///                                         deserialization (B-05).
        ///     - ManBaseTeams.OnModeStart(mode)  -> InsureDefaultTeams + network hooks.
        ///
        ///   Phase 5 (ModeStartEvent, second subscriber) — POST-TILE FIX-UP:
        ///     - ManEnemyWorld.OnWorldLoadEnd(mode) -> purges m_VisiblesFailedToRestore;
        ///                                            registers unloaded stored techs from
        ///                                            AllTrackedVisibles via TryRegister-
        ///                                            TechUnloaded; sets WorldWasTouchedBy-
        ///                                            TAC_AI; conditionally taints
        ///                                            m_FileHasBeenTamperedWith (T-04).
        ///
        /// Phases 4 and 5 still subscribe to raw ModeStartEvent (not OnLoadManagers)
        /// because they must fire on mode transitions even when no disk-load occurred
        /// (e.g. starting a fresh world). The implicit assumption is that SafeSaves
        /// load (Phases 1-3) completes before ModeStartEvent dispatches Phase 4 —
        /// verified by call-order in vanilla's save/load path.
        /// </summary>
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
        }

        internal static FieldInfo limitBreak = typeof(ManPop).GetField("m_PopulationLimit", BindingFlags.NonPublic | BindingFlags.Instance);
        public static void OverrideEnemyMax()
        {
            if (limitBreak == null)
            {
                // Fatal-class: downstream code (spawn pipelines, NP_Presence) assumes the pop cap
                // was raised. Silently leaving vanilla cap in place causes mass spawn-suppression
                // bugs that look unrelated. Surface as popup so the user sees it.
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
