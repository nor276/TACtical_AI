using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI;
using TAC_AI.Templates;
using TAC_AI.World;
using TerraTechETCUtil;
using UnityEngine;
using static Rewired.Data.Mapping.HardwareJoystickMap;
using static WaterMod.SurfacePool;
using static WobblyLaser;

namespace TAC_AI
{
    public static class AIGlobalsExt
    {
        public static bool TechIsActivePlayer(this Tank tank)
        {
            try
            {
                if (ManNetwork.IsNetworked)
                    return ManNetwork.inst.IsPlayerTechID(tank.visible.ID);
                else
                    return tank.PlayerFocused;
            }
            catch { }
            return false;
        }
    }
    /// <summary>
    /// Stores all global information for this mod. Edit at your own risk.
    /// </summary>
    public class AIGlobals
    {
        // Note improve AI navigation around water - they keep driving into the water and get stuck
        public static bool IsNotAttract => ManGameMode.inst.GetCurrentGameType() != ManGameMode.GameType.Attract;

        private static FieldInfo getCamTank = typeof(TankCamera).GetField("m_hideHud", BindingFlags.NonPublic | BindingFlags.Instance);
        public static bool GetHideHud => (bool)getCamTank.GetValue(TankCamera.inst);
        public static bool HideHud = false;
        public static bool IsBlockAIAble(BlockTypes BT)
        {
            if (BT != BlockTypes.GSOAIController_111)
            {
                if (ManMods.inst.IsModdedBlock(BT))
                {
                    var block = ManSpawn.inst.GetBlockPrefab(BT);
                    if (block)
                    {
                        var AI = block.GetComponent<ModuleAIBot>();
                        if (AI && (AI.AITypesEnabled.Contains(TechAI.AITypes.Escort) ||
                            AI.AITypesEnabled.Contains(TechAI.AITypes.Guard)))
                            return true;
                    }
                }
                else
                {
                    var BA = ManSpawn.inst.GetBlockAttributes(BT);
                    if (BA.Contains(BlockAttributes.AI))
                        return true;
                }
            }
            return false;
        }
        public static bool IsBlockAIAble(string name)
        {
            return IsBlockAIAble(BlockIndexer.GetBlockIDLogFree(name));
        }
        public static bool IsTechAIAble(Tank tech)
        {
            if (tech != null)
            {
                return tech.GetHelperInsured().hasAI;
            }
            return false;
        }
        public static bool IsTechAIAble(ManSaveGame.StoredTech tech)
        {
            if (tech != null)
            {
                foreach (var item in tech.m_TechData.m_BlockSpecs)
                {
                    if (IsBlockAIAble(item.block))
                        return true;
                }
            }
            return false;
        }

        public const float SleepRangeSpacing = 16;
        public static bool IsInSleepRange(Vector3 posScene)
        {
            // REVISED: null-guards rangeOverride, falling back to vanilla 200 if the reflection field is missing.
            float sleepRange = TankAIManager.rangeOverride != null
                ? (float)TankAIManager.rangeOverride.GetValue(ManTechs.inst) : 200f;
            return !ManNetwork.IsNetworked &&
                (posScene - Singleton.cameraTrans.position).sqrMagnitude >
                (sleepRange * sleepRange) - SleepRangeSpacing;
        }

        public const float EradicateEffectMaxDistanceSqr = 200 * 200;

        public static Rewired.Player controllerExt = null;

        public static bool PlayerFireCommand(int team)
        {
            if (ManNetwork.IsNetworked)
                return PlayerMPFireCommand(team);
            else
                return PlayerClientFireCommand();
        }
        public static bool PlayerClientFireCommand()
        {
            try
            {
                if (controllerExt == null)
                    controllerExt = Rewired.ReInput.players.GetPlayer(ManPlayer.inst.PlayerTeam);
                if (controllerExt != null && controllerExt.GetButton(2))
                    return true;
            }
            catch { }
            return false;
        }
        public static bool PlayerMPFireCommand(int Team)
        {
            if (ManNetwork.IsHost)
            {
                try
                {
                    for (int step = 0; step < ManNetwork.inst.GetNumPlayers(); step++)
                    {
                        NetPlayer NP = ManNetwork.inst.GetPlayer(step);
                        if (NP && NP.HasTech() && Team == NP.TechTeamID && NP.CurTech.tech.control.FireControl)
                            return NP.CurTech.tech.control.FireControl;
                    }
                }
                catch { }
            }
            return false;
        }

        public static bool PlayerIsOnLowQualitySettings => ManProfile.inst.GetCurrentUser().m_GraphicsSettings.m_QualityLevel <= 1;


        // AIERepair contains the self-repair stats
        // EnemyWorldManager contains the unloaded enemy stats

        //-------------------------------------
        //              CONSTANTS
        //-------------------------------------
        // SPAWNING
        public const int SmolTechBlockThreshold = 24;
        public const int DefenderWeaponCount = 12;
        public const int HomingWeaponCount = 25;
        public const int BossTechSize = 150;
        public const int LethalTechSize = 256;
        public const int MaxEradicatorTechs = 2;
        public const int MaxBlockLimitAttract = 128;

        // BASES
        public static bool CancelOnErrorTech = true;
        public const short BaseAnchorMinimumTimeDelay = 20;
        public const int NaturalBaseSpacingFromOriginTiles = 2;
        public const int NaturalBaseSpacingTiles = 2;
        public const int NaturalBaseCostBase = 250000;
        public const int NaturalBaseCostScalingWithCoordDist = 27500;
        public const float NaturalBaseDifficultyScalingWithCoordDist = 0.135f;
        public const float NaturalBaseFactionDifficultyScalingWithCoordDist = 0.2f;

        // GENERAL AI PARAMETERS
        public const float DefaultMaxObjectiveRange = 750;
        public const float TargetVelocityLeadPredictionMulti = 0.01f; // for projectiles of speed 100
        // REVISED: NEW — caps aim-lead time-of-flight so far-target solutions don't over-lead.
        public const float LeadPredictionMaxTOF = 3f;
        public const float StationaryMoveDampening = 6;
        public const int TeamRangeStart = 256;
        public const short NetAIClockPeriod = 30;

        public const float TargetCacheRefreshInterval = 1.5f;  // Seconds until we try to gather enemy Techs within range
        // REVISED: NEW — faster enemy-scan cache refresh while in combat (idle still uses the 1.5s interval).
        public const float TargetCacheRefreshIntervalCombat = 0.4f;

        internal static GUIButtonMadness ModularMenu;
        private const int IDGUI = 8037315;
        private static GUI_BM_Element[] MenuButtons = new GUI_BM_Element[]
        {
            new GUI_BM_Element_Simple()
            {
                Name = "Bribe",
                OnIcon = null,
                OnDesc = () => {
                    return "Buy them out";
                },
                ClampSteps = 0,
                LastVal = 0,
                OnSet = (float in1) => {
                    if (GUINPTInteraction.lastTank)
                    {
                        int techCost = Mathf.RoundToInt(RawTechTemplate.GetBBCost(GUINPTInteraction.lastTank) * BribeMulti);
                        GUINPTInteraction.TrySendNPTBribe(ManNetwork.inst.MyPlayer, GUINPTInteraction.lastTank, 0);
                        ModularMenu.CloseGUI();
                    }
                    return 0;
                },
            },
            new GUI_BM_Element_Simple()
            {
                Name = "Info",
                OnIcon = null,
                OnDesc = () => {
                    return "Open details pane";
                },
                ClampSteps = 0,
                LastVal = 0,
                OnSet = (float in1) => {
                    GUINPTInteraction.LaunchSubMenuClickable();
                    ModularMenu.CloseGUI();
                    return 0;
                },
            },
            new GUI_BM_Element_Simple()
            {
                Name = "Insult",
                OnIcon = null,
                OnDesc = () => {
                    return "Anger and annoy them";
                },
                ClampSteps = 0,
                LastVal = 0,
                OnSet = (float in1) => {
                    if (GUINPTInteraction.lastTank)
                    {
                        GUINPTInteraction.TrySendNPTBribe(ManNetwork.inst.MyPlayer, GUINPTInteraction.lastTank, 0);
                        ModularMenu.CloseGUI();
                    }
                    return 0;
                },
            },
            new GUI_BM_Element_Simple()
            {
                Name = "Missions",
                OnIcon = null,
                OnDesc = () => {
                    return "See what they want";
                },
                ClampSteps = 0,
                LastVal = 0,
                OnSet = (float in1) => {
                    if (GUINPTInteraction.lastTank)
                    {
                        int techCost = Mathf.RoundToInt(RawTechTemplate.GetBBCost(GUINPTInteraction.lastTank) * BribeMulti);
                        GUINPTInteraction.TrySendNPTBribe(ManNetwork.inst.MyPlayer, GUINPTInteraction.lastTank, 0);
                        ModularMenu.CloseGUI();
                    }
                    return 0;
                },
            },
        };

        internal static void InitSharedMenu()
        {
            if (ModularMenu != null)
                return;
            DebugTAC_AI.Log("AIGlobals.InitSharedMenu()");
            ModularMenu = GUIButtonMadness.Initiate(IDGUI, "ERROR", MenuButtons);
        }

        public static IntVector3 RTSDisabled => IntVector3.invalid;
        // General
        public const int LonerEnemyTeam = ManSpawn.NewEnemyTeam;
        public const int DefaultEnemyTeam = ManSpawn.FirstEnemyTeam;
        public const float YieldSpeed = 10;
        public static bool AllowWeaponsDisarm2 = false;
        public const bool BaseSubNeutralsCuriousFollow = true;
        public const float defaultExpandRad = 24f;
        public const float defaultExpandRadRange = 192f;

        // Elevation
        public const float GroundOffsetGeneralAir = 10;
        public const float GroundOffsetRTSAir = 24;
        public const float GroundOffsetAircraft = 22;
        public const float GroundOffsetChopper = 8.5f;
        public const float GroundOffsetChopperExtra = 5f;
        public const float GroundOffsetCrashWarnChopperDelta = -2.5f;

        // Anchors
        public const float SafeAnchorDist = 50f;     // enemy too close to anchor
        /// <summary> How much do we dampen anchor movements by? </summary>
        public const int AnchorAimDampening = 45;
        public const short MaxAnchorAttempts = 3;//12;

        // Unjamming
        public const int UnjamUpdateFire = 25;
        public const int UnjamUpdateStart = 120;
        public const int UnjamUpdateTicks = 120;
        public const int UnjamUpdateEndDelay = 20;

        public const int UrgencyOverloadReconsideration = 180;//80
        public const int UnjamUpdateDrop = UnjamUpdateStart + UnjamUpdateTicks;
        public const int UnjamUpdateEnd = UnjamUpdateDrop + UnjamUpdateEndDelay;
        public const float AngularProgressThreshold = 0.5f;
        // REVISED: windowed net-progress check used by IsTechMoving* / the unjam soft-decay. A tech that covers less than
        // max(StuckNetProgressFloor, EstTopSped * StuckNetProgressWindow * StuckNetProgressFraction) world units over the
        // window (while commanding drive) is making no net progress, so yaw jitter no longer masks a genuine wedge.
        // The threshold scales with EstTopSped so a slow/heavy tech making genuine slow headway is not falsely flagged
        // stuck (it tracks the same speed basis as the EnemyAISpeedPanicDividend gate); the floor covers near-zero EstTopSped.
        public const float StuckNetProgressWindow = 1.5f;
        public const float StuckNetProgressFraction = 0.2f;
        public const float StuckNetProgressFloor = 1f;

        public const float AISubscribeDelay = 0.1f;

        public const float EnemyInitGrace = 1.0f;

        // Pathfinding
        internal static Bitfield<ObjectTypes> emptyBitMask = new Bitfield<ObjectTypes>();
        internal static Bitfield<ObjectTypes> blockBitMask = new Bitfield<ObjectTypes>(new ObjectTypes[1] { ObjectTypes.Block });
        internal static Bitfield<ObjectTypes> techBitMask = new Bitfield<ObjectTypes>(new ObjectTypes[1] { ObjectTypes.Vehicle });
        internal static Bitfield<ObjectTypes> sceneryBitMask = new Bitfield<ObjectTypes>(new ObjectTypes[1] { ObjectTypes.Scenery });
        internal static Bitfield<ObjectTypes> crashBitMask = new Bitfield<ObjectTypes>(new ObjectTypes[2] { ObjectTypes.Scenery, ObjectTypes.Vehicle });

        public const float AIPathingSuccessRad = 2.4f; // How far should the tech radius from the path point to consider finishing the path point?
        public const float AIPathingSuccessRadPrecise = 1.2f; // How far should the tech radius from the path point to consider finishing the path point?

        public const int PathfindingExtraSpace = 6;  // Extra pathfinding space
        public const float DefaultDodgeStrengthMultiplier = 1.75f;  // The motivation in trying to move away from a tech in the way
        public const float AirborneDodgeStrengthMultiplier = 0.4f;  // The motivation in trying to move away from a tech in the way
        public const float FindItemScanRangeExtension = 50;
        public const float FindBaseScanRangeExtension = 500;
        // REVISED: retuned reverse-away holds; these are now actionPause tick set-values read at 500 ticks/sec (ReverseDelay 500 = 1.0s, ReverseFromResourceDelay 300 = 0.6s) via the AITimer shim.
        public const int ReverseDelay = 500;
        public const int ReverseFromResourceDelay = 300;
        // REVISED: NEW — seconds the 3D-navi tech must stay tipped over before firing a build beam to flip upright.
        public const float BeamFlipTippedHoldSecs = 1.5f;
        public const float PlayerAISpeedPanicDividend = 8;
        public const float EnemyAISpeedPanicDividend = 9;
        /// <summary>Depth that land Techs are able to drive into</summary>
        public const float WaterDepthTechHeightPercent = 0.35f;

        // Control the aircrafts and AI
        public const float PropLerpStrictness = 10f;// 10
        public const int MaxTakeoffFailiures = 240;
        public const float BoosterThrustBias = 0.5f;
        /// <summary> TtWR = Thrust to Weight Ratio </summary>
        public const float ImmelmanTtWRThreshold = 1.5f;
        public static float ChopperDownAntiBounce = 0.5f;
        public static float ChopperThrottleDamper = 1.25f;

        public const float AircraftDestSuccessRadius = 32;
        public const float AerofoilSluggishnessBaseValue = 30;
        public const float AircraftMaxDive = 0.75f;
        public const float AircraftChillFactorMulti = 4.5f;         // More accuraccy, less responsiveness
        public const float LargeAircraftChillFactorMulti = 1.25f;   // More responsiveness, less accuraccy

        public const float AirNPTDespawnHeightOffset = 250f; // Beyond this height from the player the AI will be despawned
        public const float AirNPTMaxHeightOffset = 150f;     // How high the AI is allowed to go while in the Attract mode
        public const float AirWanderMaxHeightIngame = 100f;//75;// How high the AI is allowed to go while wandering randomly above the player
        public static float AirWanderMaxHeight => IsNotAttract ? AirWanderMaxHeightIngame : AirNPTMaxHeightOffset;         // How far the AI is allowed to go while wandering randomly above the player
        public const float AirPromoteSpaceHeight = 150;     // The height the player, beyond passing, will encounter more spacecraft
        public const float AirMaxYaw = 0.45f;//0.2f; // 0 - 1 (float)
        public const float AirMaxYawBankOnly = 0.75f; // 0 - 1 (float)

        public const float ChopperYChillFactorMulti = 0.2f;
        public const float ChopperXZChillFactorMulti = 2.5f;
        public const float ChopperMaxDeltaAnglePercent = 0.325f;
        public const float ChopperAngleNudgePercent = 0.15f;
        public const float ChopperAngleDoPitchPercent = 0.2f;
        public const float ChopperMaxAnglePercent = 0.35f;
        public const float ChopperSpeedCounterPitch = 12f;

        public const float HovershipHorizontalDriveMulti = 1.25f;
        public const float HovershipUpDriveMulti = 1f;
        public const float HovershipDownDriveMulti = 0.6f;

        public const int LargeAircraftSize = 15;            // The size of which we count an aircraft as large
        /// <summary> IN m/s !!!</summary>
        public const float AirStallSpeed = 42;//25          // The speed of which most wings begin to stall at
        public const float GroundAttackStagingDistMain = 275;
        public static float GroundAttackStagingDist => IsNotAttract ? 120 : GroundAttackStagingDistMain;   // Distance to fly (in meters!) before turning back
        public const float TechSplitDelay = 0.5f;

        // Item Handling
        public const float MinimumCloseInSpeedSqr = 2.56f;      // If we are closing in on our target slower than this (with wrong heading), we drive slowly
        public const float BlockAttachDelay = 0.75f;        // How long until we actually attach the block when playing the placement animation
        public const float MaxBlockGrabRange = 47.5f;       // Less than player range to compensate for precision
        public const float MaxBlockGrabRangeAlt = 5;        // Lowered range to allow scrap magnets to have a chance
        public const float ItemGrabStrength = 1750;         // The max acceleration to apply when holding an item
        public const float ItemThrowVelo = 115;             // The max velocity to apply when throwing an item
        public const float AircraftHailMaryRange = 65f;     // Try throw things this far away for aircraft
        //  because we don't want to burn daylight trying to land and takeoff again

        // Charger Parameters
        public const float minimumChargeFractionToConsider = 0.75f;

        // Combat Parameters
        public const float TargetValidationDelay = 0.6f;//1.5f;
        // REVISED: default targeting range widened from 150 to 1500.
        public const int DefaultMaxTargetingRange = 1500;

        // Combat target switching
        public const int ProvokeTime = 200;         // Roughly around 200/40 = 5 seconds
        public const int ProvokeTimeShort = 80;
        // REVISED: NEW — seconds a target stays held after LOS is lost (behind cover) before pursuit ends.
        public const float LOSLostGraceTime = 3.0f;
        public const int DamageAlertThreshold = 45;// Above this damage we react to the threat
        // REVISED: NEW — cumulative damage-alert model: damage sums toward DamageAlertCumulativeThreshold and decays at DamageAlertDecayPerSec, so steady chip damage eventually trips the alert.
        public const float DamageAlertCumulativeThreshold = 60f;
        public const float DamageAlertDecayWindowSeconds = 3.0f;
        public const float DamageAlertDecayPerSec = DamageAlertCumulativeThreshold / DamageAlertDecayWindowSeconds;
        public const float ScanDelay = 0.5f;        // Seconds until we try to find a appropreate target
        public const float PestererSwitchDelay = 12.5f; // Seconds before Pesterers find a new random target

        // REVISED: NEW flicker controls — keep a held target until past 1.5x range, require 2 blocked-LOS checks before asserting cover, and cap RTS-locked targets at 2.5x range.
        public const float CombatRangeRetentionMult = 1.5f;
        public const float CombatRangeRetentionMultSqr = CombatRangeRetentionMult * CombatRangeRetentionMult;
        public const int LosBlockedStreakThreshold = 2;
        public const float RTSLockMaxRangeMultiplier = 2.5f;
        public const float RTSLockMaxRangeMultiplierSqr = RTSLockMaxRangeMultiplier * RTSLockMaxRangeMultiplier;

        // ENEMY AI PARAMETERS
        // Active Enemy AI Techs
        public const float EnemyTeamAwarenessUpdateDelay = 6;
        public const float DamageAngerDropRelations = 2500;//2500
        // REVISED: renamed from DamageAngerCoolPerSec and now a true per-second rate (25), no longer pre-multiplied by EnemyTeamAwarenessUpdateDelay — the per-tick decay is computed at the consumer.
        public const float DamageAngerCoolRatePerSec = 25;
        public const int DefaultEnemyScanRange = 150;
        public const int TileFringeDist = 96;
        public const float BatteryRetreatPercent = 0.25f;

        // Attack Detection/Chase ranges
        public const int DefaultEnemyMaxCombatRange = 150;
        public const int PassiveMaxCombatRange = 75;
        public const int BaseFounderMaxCombatRange = 60;     //
        public const int BossMaxCombatRange = 250;        //
        public const int InvaderMaxCombatRange = 250;        //
        public const float SpyperMaxCombatRange = 175;    //

        // Combat Minimum Spacing Ranges
        public const float MinCombatRangeDefault = 12;
        public const float MinCombatRangeSpyper = 60;
        // REVISED: combat back-off dead-band. A tech holds-and-faces between (hull contact + MinCombatRange*this) and the
        // full stand-off, and only reverses (FACE-ENEMY back off) when pushed inside that inner radius - so it stops
        // constantly back-pedalling front-on just to maintain the stand-off. Lower = backs off less / holds sooner.
        public const float CombatReverseInnerFraction = 0.35f;
        // REVISED: when a mod forces continuous strafe (TweakTech/WeaponAimMod -> ShouldForceContinuousStrafe), only
        // techs with at least this turret share keep strafing endlessly (their turrets can fire mid-circle). A
        // mostly-front-fixed tech below this falls back to the duty cycle so it still gets a face/fire window.
        public const float ContinuousStrafeMinTurretFraction = 0.5f;
        public const float SpacingRangeSpyperAir = 72;
        public const float SpacingRangeAircraft = 24;
        public const float SpacingRangeChopper = 12;
        public const float SpacingRangeHoverer = 18;
        // REVISED: NEW spacing params — bomber drop-zone alignment tolerance, and a negative spacer that drives ramming units to close inside the target.
        public const float BomberDropZoneTolerance = 8;
        public const float GCRamSpacer = -32f;

        // Non-Player Base Checks
        public static bool StartingBasesAreAirdropped = false;
        public static float EnemyBaseMakerChance = 25;
        public const float StartBaseMinSpacing = 450;
        public static bool AllowInfAutominers = true;
        public static bool NoBuildWhileInCombat = true;
        public const int MinimumBBToTryExpand = 10000; // Before expanding
        public const int MinimumBBToTryBribe = 100000;
        public const float BribeMulti = 1.5f;
        public const int BaseExpandChance = 65;
        public const int MinResourcesReqToCollect = 12;
        public const int EnemyBaseMiningMaxRange = 250;
        public const int EnemyExtendActionRangeShort = 500;
        public const int EnemyExtendActionRange = EnemyExtendActionRangeShort + 32; //the extra 32 to account for tech sizes
        // REVISED: NEW — extra range past sleep cutoff within which enemies are kept active so combat doesn't freeze at the edge.
        public const float EnemyKeepAwakeRange = 700f;

        // REVISED: NEW dive-attack FSM tunables — MinDiveAGL gates Approach->Commit (x0.4 = commit->recover); Min/MaxRecoverHold are the Recover dwell floor/escape; CommitRecoverAltHysteresis debounces terrain-column jitter before aborting a dive; PostRecoverCooldown is the minimum gap before a new dive (kills the climb-dive yo-yo); DiveCachedAimMaxRange caps cached-aim reuse.
        public const float MinDiveAGL = 60f;
        public const float MinRecoverHold = 1.5f;
        public const float MaxRecoverHold = 8f;
        public const float CommitRecoverAltHysteresis = 0.3f;
        public const float PostRecoverCooldown = 2.0f;
        public const float DiveCachedAimMaxRange = 2000f;
        // REVISED: NEW — minimum distance from the player a raid spawn may appear.
        public const int RaidMinSpawnDistance = 96;
        public const float RetreatBelowTechDamageThreshold = 50;
        public const float RetreatBelowTeamDamageThreshold = 30;

        public const int MPEachBaseProfits = 250;
        public const float RaidCooldownTimeSecs = 1200;
        public const int IgnoreBaseCullingTilesFromOrigin = 8388607;
        /// <summary>
        /// SaveLoadDelay
        /// </summary>
        public const float SLDBeforeBuilding = 90;
        public const float DelayBetweenBuilding = 30;

        // REVISED: now a real squared distance (175^2); was written 75 ^ 2 which is integer XOR (= 73), not 75 squared.
        public const float MaximumNeutralMonitorSqr = 175f * 175f;

        // Colors
        internal static Color PlayerColor => AltUI.ColorDefaultPlayer;
        internal static Color PlayerAutoColor = new Color(0.35f, 0.85f, 0.475f, 1);
        // ENEMY BASE TEAMS
        internal static Color EnemyColor => AltUI.ColorDefaultEnemy;

        internal static Color NeutralColor => AltUI.ColorDefaultNeutral;
        internal static Color SubNeutralColor = new Color(0.5f, 0, 0.5f, 1);
        internal static Color FriendlyColor => AltUI.ColorDefaultFriendly;

        /// <summary> increments NEGATIVELY </summary>
        public const int EnemyTeamsRangeStart = -1073741828;
                                               //2147483647
        internal static bool IsAttract => ManGameMode.inst.GetCurrentGameType() == ManGameMode.GameType.Attract;

        private const float BaseChanceNonHosileDefaultMulti = 0.1f;
        public static float AttackableNeutralBaseChanceMulti = 0.5f * BaseChanceNonHosileDefaultMulti;
        public static float FriendlyBaseChanceMulti = 0.25f * BaseChanceNonHosileDefaultMulti;
        public static float NonHostileBaseChance => AttackableNeutralBaseChanceMulti;
        public static float FriendlyBaseChance => FriendlyBaseChanceMulti;

        internal static bool TurboAICheat
        {
            get { return SpecialAISpawner.CreativeMode && Input.GetKey(KeyCode.LeftShift) && Input.GetKey(KeyCode.KeypadPlus); }
        }

#if DEBUG
 internal static bool ShowDebugFeedBack = false;
#else
        internal static bool ShowDebugFeedBack = false;
#endif

        public static float AngleUnsignedToSigned(float angle)
        {
            angle = angle % 360;
            if (angle > 180)
                angle -= 360;
            return angle;
        }
        public static WorldPosition GetWorldPos(ManSaveGame.StoredTech tank)
        {
            if (tank.m_WorldPosition == default)
                return WorldPosition.FromScenePosition(tank.GetBackwardsCompatiblePosition());
            return tank.m_WorldPosition;
        }
        public static ManSaveGame.StoredTech FindStoredTech(int visID, IntVector2 coord, bool searchInJSONToo)
        {
            WorldTile WT = ManWorld.inst.TileManager.LookupTile(coord);
            if (WT != null && WT.SaveData != null && WT.SaveData.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out var vals))
            {   // Our TrackedVisible does exist, now to find it...
                for (int step = 0; step < vals.Count; step++)
                {
                    var val = vals[step];
                    if (val != null && val.m_ID == visID)
                    {
                        return val as ManSaveGame.StoredTech;
                    }
                }
            }
            var tile = ManSaveGame.inst.GetStoredTile(coord, false);
            if (tile != null && tile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out vals))
            {   // Try in the unloaded tile!?
                for (int step = 0; step < vals.Count; step++)
                {
                    var val = vals[step];
                    if (val != null && val.m_ID == visID)
                    {
                        return val as ManSaveGame.StoredTech;
                    }
                }
            }
            if (searchInJSONToo)
            {
                var jsonTiles = ManSaveGame.inst.CurrentState?.m_StoredTilesJSON;
                if (jsonTiles != null && jsonTiles.TryGetValue(coord, out string jsonTile) && !jsonTile.NullOrEmpty())
                {
                    ManSaveGame.StoredTile storedTile = null;
                    ManSaveGame.LoadObjectFromRawJson(ref storedTile, jsonTile, false, false);
                    if (storedTile != null && storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out vals))
                    {   // Try in the unloaded tile!?
                        for (int step = 0; step < vals.Count; step++)
                        {
                            var val = vals[step];
                            if (val != null && val.m_ID == visID)
                            {
                                return val as ManSaveGame.StoredTech;
                            }
                        }
                    }
                }
            }
            // Our storedTech DOES NOT EXISTS
            return null;
        }
        public static ManSaveGame.StoredTech RemoveStoredTech(int visID, IntVector2 coord, bool removeFromJSONToo)
        {
            WorldTile WT = ManWorld.inst.TileManager.LookupTile(coord);
            if (WT != null)
            {
                if (WT.StoredVisiblesWaitingToLoad != null)
                {
                    var storVis = WT.StoredVisiblesWaitingToLoad.Find(x => x.m_ID == visID);
                    if (storVis != null && storVis is ManSaveGame.StoredTech tech)
                        return tech;
                }
                if (WT.SaveData != null && WT.SaveData.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out var vals))
                {
                    for (int step = 0; step < vals.Count; step++)
                    {
                        var val = vals[step];
                        if (val != null && val.m_ID == visID)
                        {
                            vals.RemoveAt(step);
                            return val as ManSaveGame.StoredTech;
                        }
                    }
                }
            }
            var tile = ManSaveGame.inst.GetStoredTile(coord, false);
            if (tile != null && tile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out var vals2))
            {   // Try in the unloaded tiles!?
                for (int step = 0; step < vals2.Count; step++)
                {
                    var val = vals2[step];
                    if (val != null && val.m_ID == visID)
                    {
                        vals2.RemoveAt(step);
                        return val as ManSaveGame.StoredTech;
                    }
                }
            }
            if (removeFromJSONToo)
            {
                var jsonTiles = ManSaveGame.inst.CurrentState?.m_StoredTilesJSON;
                if (jsonTiles != null && jsonTiles.TryGetValue(coord, out string jsonTile) && !jsonTile.NullOrEmpty())
                {
                    ManSaveGame.StoredTile storedTile = null;
                    ManSaveGame.LoadObjectFromRawJson(ref storedTile, jsonTile, false, false);
                    if (storedTile != null && storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out vals2))
                    {   // Try in the unloaded tile!?
                        for (int step = 0; step < vals2.Count; step++)
                        {
                            var val = vals2[step];
                            if (val != null && val.m_ID == visID)
                            {
                                vals2.RemoveAt(step);
                                ManSaveGame.inst.CurrentState.m_StoredTilesJSON[coord] = ManSaveGame.SaveObjectToRawJson(storedTile);
                                for (int step2 = 0; step2 < vals2.Count; step2++)
                                {
                                    var val2 = vals2[step2];
                                    if (val2 != null && val2.m_ID == visID)
                                    {
                                        ManUI.inst.ShowErrorPopup("Impossible!  We removed the visible of ID \"" + val2.m_ID +
                                            "\" from the serial data but it still exists!");
                                        //throw new InvalidOperationException("Impossible!  We removed the tech from the serial data but it still exists!");
                                    }
                                }
                                return val as ManSaveGame.StoredTech;
                            }
                        }
                    }
                }
            }
            // Our storedTech DOES NOT EXISTS
            return null;
        }

        /// <summary>
        /// WIP
        /// </summary>
        /// <param name="posScene"></param>
        /// <param name="anchored"></param>
        /// <returns></returns>
        public static RadarTypes DetermineRadarType(int ID, Vector3 posScene, bool anchored)
        {
            WorldPosition WP = WorldPosition.FromScenePosition(posScene);
            return DetermineRadarType(ID, true, anchored);
        }
        public static RadarTypes DetermineRadarType(int ID, bool show, bool anchored)
        {
            if (!show)
            {
                ManBaseTeams.inst.HiddenVisibles.Add(ID);
                return RadarTypes.Hidden;
            }
            ManBaseTeams.inst.HiddenVisibles.Remove(ID);
            return anchored ? RadarTypes.Base : RadarTypes.Vehicle;
        }

        // Utilities
        public static Quaternion LookRot(Vector3 forward) => LookRot(forward, Vector3.up);
        public static Quaternion LookRot(Vector3 forward, Vector3 up)
        {
            if (forward.ApproxZero())
                return Quaternion.identity;
            return Quaternion.LookRotation(forward, up);
        }
        public static bool VisibleIsSafelyRemoveable(int visibleID, int team)
        {
            return (ManBaseTeams.IsBaseTeamDynamicOrUnregistered(team) || team == DefaultEnemyTeam || team == LonerEnemyTeam) &&
                !IsPlayerTeam(team) && (team != ManSpawn.NeutralTeam) && !TankAIManager.MissionTechs.Contains(visibleID);
        }
        public static bool BaseTeamVisibleIsSafelyRemoveable(int visibleID, int team)
        {
            return ManBaseTeams.IsBaseTeamDynamicOrUnregistered(team) && !ManBaseTeams.IsPlayerOwnedAIBaseTeam(team) &&
                !IsPlayerTeam(team) && (team != ManSpawn.NeutralTeam) && !TankAIManager.MissionTechs.Contains(visibleID);
        }
        public static bool TechIsSafelyRemoveable(Tank tech)
        {
            if (tech == null)
                return false;
            int team = tech.Team;
            return (ManBaseTeams.IsBaseTeamDynamicOrUnregistered(team) || tech.IsPopulation || team == DefaultEnemyTeam || team == LonerEnemyTeam) &&
                !IsPlayerTeam(team) && (team != ManSpawn.NeutralTeam) && !TankAIManager.MissionTechs.Contains(tech.visible.ID) &&
                (tech.name != "DPS Target");
        }
        public static bool TechIsSafelyRemoveable(ManSaveGame.StoredTech tech, bool excludeDefaultEnemyTeam = false)
        {
            if (tech == null)
                return false;
            int team = tech.m_TeamID;
            return (ManBaseTeams.IsBaseTeamDynamicOrUnregistered(team) || tech.m_IsPopulation || (excludeDefaultEnemyTeam ? false : team == DefaultEnemyTeam) || team == LonerEnemyTeam) &&
                !IsPlayerTeam(team) && team != ManSpawn.NeutralTeam;
        }

        /// <summary>
        /// Returns true if the target visible is loaded in a way that DOES NOT overlap unloaded tiles!!!
        /// </summary>
        /// <param name="tileCoord"></param>
        /// <returns></returns>
        public static bool CanPlaceSafelyInTile(IntVector2 tileCoord, IntVector2 overlapDir)
        {
            if (overlapDir != IntVector2.zero)
            {
                return ManWorld.inst.TileManager.CheckAllOverlappedNeighboursLoaded(tileCoord, overlapDir, false);
            }
            return false;
        }
        public static bool PlayerCanDetectTile(IntVector2 tileCoord)
        {
            if (KickStart.DisableEnemyFogOfWar)
                return true;
            return false;
        }
        // REVISED: split throttle now a Time.unscaledTime deadline instead of a bool flag reset by a scheduled Invoke; self-clears with no callback.
        private static float nextSplitAllowedTime = 0f;
        public static bool CanSplitTech(float delay = TechSplitDelay)
        {
            if (Time.unscaledTime < nextSplitAllowedTime)
                return false;
            nextSplitAllowedTime = Time.unscaledTime + delay;
            return true;
        }
        /// <summary>
        /// Set to -1 to recache the count immedeately next update
        /// </summary>
        internal static int SceneTechCount = -1;
        public static bool AtSceneTechMaxSpawnLimit()
        {
            if (SceneTechCount == -1)
            {
                try
                {
                    SceneTechCount = ManTechs.inst.IterateTechsWhere(x => TechIsSafelyRemoveable(x)).Count();
                }
                catch (Exception e)
                {
                    SceneTechCount = 0;
                    DebugTAC_AI.Log(KickStart.ModID + ": AtSceneTechMax() - Error on IterateTechs Fetch");
                    DebugTAC_AI.Log(e);
                }
            }
            return SceneTechCount >= KickStart.MaxEnemyWorldCapacity;
        }
        public static bool SceneTechMaxNeedsRemoval(out int needsRemovalCount)
        {
            if (SceneTechCount == -1)
            {
                try
                {
                    SceneTechCount = ManTechs.inst.IterateTechsWhere(x => TechIsSafelyRemoveable(x)).Count();
                }
                catch (Exception e)
                {
                    SceneTechCount = 0;
                    DebugTAC_AI.Log(KickStart.ModID + ": SceneTechMaxNeedsRemoval() - Error on IterateTechs Fetch");
                    DebugTAC_AI.Log(e);
                }
            }
            int threshold = KickStart.MaxEnemyWorldCapacity + KickStart.ForceRemoveOverEnemyMaxCap;
            needsRemovalCount = Mathf.Max(0, SceneTechCount - threshold);
            return SceneTechCount >= threshold;
        }

        public static bool IsPlayerTeam(int team)
        {
            return ManNetwork.IsNetworked ? IsMPPlayerTeam(team) : ManPlayer.inst.PlayerTeam == team;
        }

        public static bool IsMPPlayerTeam(int team)
        {
            return ManSpawn.LobbyTeamIDFromTechTeamID(team) != int.MaxValue;
        }

        private static List<Tank> TEMP = new List<Tank>();
        /// <summary>
        /// WILL RESET ON NEXT CALL
        /// </summary>
        /// <param name="team"></param>
        /// <returns></returns>
        public static List<Tank> GetAllPlayerControlledTechs()
        {
            TEMP.Clear();
            foreach (var item in ManTechs.inst.IterateTechs())
            {
                if (item?.visible != null && item.TechIsActivePlayer())
                    TEMP.Add(item);
            }
            return TEMP;
        }

        internal static NP_Types GetNPTTeamTypeForDebug(int team)
        {
            if (team == ManPlayer.inst.PlayerTeam)
                return NP_Types.Player;
            else if (ManBaseTeams.IsBaseTeamAny(team))
            {
                switch (ManBaseTeams.GetRelationsWritablePriority(team, ManBaseTeams.playerTeam, TeamRelations.Enemy))
                {
                    case TeamRelations.Enemy:
                        return NP_Types.Enemy;
                    case TeamRelations.SubNeutral:
                        return NP_Types.SubNeutral;
                    case TeamRelations.Neutral:
                        return NP_Types.Neutral;
                    case TeamRelations.Friendly:
                        return NP_Types.Friendly;
                    case TeamRelations.AITeammate:
                        return NP_Types.Player;
                    default:
                        return NP_Types.NonNPT;
                }
            }
            else
                return NP_Types.NonNPT;
        }

        public enum EDebugSpawnOverride
        {
            Randomized,
            Ally,
            SubNeutral,
            DefaultEnemy,
            Player,
        }
        public static EDebugSpawnOverride SpawnDebugOverride
        {
            get
            {
                if (Input.GetKey(KeyCode.LeftAlt))
                    return EDebugSpawnOverride.Player;
                else if (Input.GetKey(KeyCode.LeftControl))
                {
                    if (Input.GetKey(KeyCode.LeftShift))
                        return EDebugSpawnOverride.DefaultEnemy;
                    else
                        return EDebugSpawnOverride.Ally;
                }
                else if (Input.GetKey(KeyCode.LeftShift))
                    return EDebugSpawnOverride.SubNeutral;
                else
                    return EDebugSpawnOverride.Randomized;
            }
        }
        public static int GetRandomBaseTeam(bool debugSpawned, bool forceValidTeam)
        {
            if (debugSpawned && !forceValidTeam)
            {
                switch (SpawnDebugOverride)
                {
                    case EDebugSpawnOverride.Ally:
                        return GetRandomAllyBaseTeam();
                    case EDebugSpawnOverride.SubNeutral:
                        return GetRandomSubNeutralBaseTeam();
                    case EDebugSpawnOverride.DefaultEnemy:
                        return DefaultEnemyTeam;
                    case EDebugSpawnOverride.Player:
                        return ManPlayer.inst.PlayerTeam;
                    case EDebugSpawnOverride.Randomized:
                    default:
                        break;
                }
            }

            if (UnityEngine.Random.Range(0f, 1f) <= NonHostileBaseChance)
            {
                if (UnityEngine.Random.Range(0f, 1f) <= FriendlyBaseChance)
                    return GetRandomAllyBaseTeam(false);
                else
                    return GetRandomSubNeutralBaseTeam(false);
            }
            return GetRandomEnemyBaseTeam(false);
        }
        public static int GetRandomEnemyBaseTeam(bool forceNew = true)
        {
            if (!forceNew && ManBaseTeams.inst.teams.Any() && UnityEngine.Random.Range(0, 1f) <= ManBaseTeams.PercentChanceExisting &&
                ManBaseTeams.TryGetExistingBaseTeamWithPlayerAlignment(TeamRelations.Enemy, out var teamInst))
                return teamInst.teamID;
            teamInst = ManBaseTeams.GetNewBaseTeam(TeamRelations.Enemy);
            return teamInst.teamID;
        }
        /// <summary>
        /// Attackable neutral
        /// </summary>
        public static int GetRandomSubNeutralBaseTeam(bool forceNew = true)
        {
            if (!forceNew && ManBaseTeams.inst.teams.Any() && UnityEngine.Random.Range(0, 1f) <= ManBaseTeams.PercentChanceExisting &&
                ManBaseTeams.TryGetExistingBaseTeamWithPlayerAlignment(TeamRelations.SubNeutral, out var teamInst))
                return teamInst.teamID;
            teamInst = ManBaseTeams.GetNewBaseTeam(TeamRelations.SubNeutral);
            return teamInst.teamID;
        }
        public static int GetRandomAllyBaseTeam(bool forceNew = true)
        {
            if (!forceNew && ManBaseTeams.inst.teams.Any() && UnityEngine.Random.Range(0, 1f) <= ManBaseTeams.PercentChanceExisting &&
                ManBaseTeams.TryGetExistingBaseTeamWithPlayerAlignment(TeamRelations.Friendly, out var teamInst))
                return teamInst.teamID;
            teamInst = ManBaseTeams.GetNewBaseTeam(TeamRelations.Enemy);
            teamInst.SetFriendly(ManPlayer.inst.PlayerTeam);
            return teamInst.teamID;
        }

        public static TrackedVisible GetTrackedVisible(int ID)
        {
            return ManVisible.inst.GetTrackedVisible(ID);
        }
        public static TrackedVisible GetTrackedVisible(Tank tech)
        {
            if (ManNetwork.IsNetworked)
            {
                try
                {
                    TrackedVisible TV = ManVisible.inst.GetTrackedVisibleByHostID(tech.netTech.HostID);
                    Singleton.Manager<ManNetwork>.inst.SendToServer(TTMsgType.UnspawnTech, new UnspawnTechMessage
                    {
                        m_HostID = TV.HostID,
                        m_CheatBypassInventory = true,
                    }
                    );
                }
                catch { }
                return null;
            }
            else
            {
                return ManVisible.inst.GetTrackedVisible(tech.visible.ID);
            }
        }

        internal static bool PopupColored(string text, int team, WorldPosition pos)
        {
            switch (ManBaseTeams.GetRelationsWritablePriority(team, ManBaseTeams.playerTeam, TeamRelations.Enemy))
            {
                case TeamRelations.Enemy:
                    PopupEnemyInfo(text, pos);
                    return true;
                case TeamRelations.SubNeutral:
                    PopupSubNeutralInfo(text, pos);
                    return true;
                case TeamRelations.Neutral:
                    PopupNeutralInfo(text, pos);
                    return true;
                case TeamRelations.Friendly:
                    PopupAllyInfo(text, pos);
                    return true;
                case TeamRelations.AITeammate:
                    PopupPlayerInfo(text, pos);
                    return true;
                default:
                    break;
            }
            return false;
        }

        private static bool playerSavedOver = false;
        private static FloatingTextOverlayData playerOverEdit;
        private static GameObject playerTextStor;
        internal static void PopupPlayerInfo(string text, WorldPosition pos)
        {

            if (!playerSavedOver)
            {
                playerTextStor = AltUI.CreateCustomPopupInfo("NewTextPlayer", PlayerColor, out playerOverEdit);
                playerSavedOver = true;
            }

            AltUI.PopupCustomInfo(text, pos, playerOverEdit);
        }

        private static bool enemySavedOver = false;
        private static FloatingTextOverlayData enemyOverEdit;
        private static GameObject enemyTextStor;
        internal static void PopupEnemyInfo(string text, WorldPosition pos)
        {
            if (!enemySavedOver)
            {
                enemyTextStor = AltUI.CreateCustomPopupInfo("NewTextEnemy", EnemyColor, out enemyOverEdit);
                enemySavedOver = true;
            }

            AltUI.PopupCustomInfo(text, pos, enemyOverEdit);
        }

        private static bool subNeutralSavedOver = false;
        private static FloatingTextOverlayData subNeutralOverEdit;
        private static GameObject subNeutralTextStor;
        internal static void PopupSubNeutralInfo(string text, WorldPosition pos)
        {
            if (!subNeutralSavedOver)
            {
                subNeutralTextStor = AltUI.CreateCustomPopupInfo("NewTextSubNeutral", SubNeutralColor, out subNeutralOverEdit);
                subNeutralSavedOver = true;
            }
            AltUI.PopupCustomInfo(text, pos, subNeutralOverEdit);
        }

        private static bool neutralSavedOver = false;
        private static FloatingTextOverlayData NeutralOverEdit;
        private static GameObject neutralTextStor;
        internal static void PopupNeutralInfo(string text, WorldPosition pos)
        {
            if (!neutralSavedOver)
            {
                neutralTextStor = AltUI.CreateCustomPopupInfo("NewTextNeutral", NeutralColor, out NeutralOverEdit);
                neutralSavedOver = true;
            }
            AltUI.PopupCustomInfo(text, pos, NeutralOverEdit);
        }

        private static bool AllySavedOver = false;
        private static FloatingTextOverlayData AllyOverEdit;
        private static GameObject AllyTextStor;
        internal static void PopupAllyInfo(string text, WorldPosition pos)
        {

            if (!AllySavedOver)
            {
                AllyTextStor = AltUI.CreateCustomPopupInfo("NewTextAlly", FriendlyColor, out AllyOverEdit);
                AllySavedOver = true;
            }
            AltUI.PopupCustomInfo(text, pos, AllyOverEdit);
        }

        // REVISED: NEW — clears the cached team-popup overlay objects/flags so DeInit doesn't leave stale popup references across mode reloads.
        internal static void ResetPopupCache()
        {
            playerSavedOver = enemySavedOver = subNeutralSavedOver = neutralSavedOver = AllySavedOver = false;
            playerOverEdit = enemyOverEdit = subNeutralOverEdit = NeutralOverEdit = AllyOverEdit = null;
            playerTextStor = enemyTextStor = subNeutralTextStor = neutralTextStor = AllyTextStor = null;
        }

        internal static bool TileNeverLoadedBefore(IntVector2 coord) => ManWorld.inst.IsTileUsableForNewSetPiece(coord);
        internal static bool TileLoadedCanSpawnNewEnemy(Vector3 posScene, float radius)
        {
            TileManager TM = ManWorld.inst.TileManager;
            WorldPosition WP = WorldPosition.FromScenePosition(posScene);
            IntVector2 tileCoord = WP.TileCoord;
            Vector2 RadSqr = new Vector2(radius, radius);
            Vector2 vector = TM.CalcMinWorldCoords(tileCoord) + ManWorld.inst.TerrainGenerationOffset - RadSqr;
            Vector2 vector2 = TM.CalcMaxWorldCoords(tileCoord) + ManWorld.inst.TerrainGenerationOffset + RadSqr;
            foreach (SceneryBlocker item in ManWorld.inst.VendorSpawner.SceneryBlockersOverlappingWorldCoords(vector, vector2))
            {
                if (item.IsBlockingPos(SceneryBlocker.BlockMode.Regrow, posScene, radius))
                    return false;
            }
            foreach (SceneryBlocker item in ManWorld.inst.LandmarkSpawner.SceneryBlockersOverlappingWorldCoords(vector, vector2))
            {
                if (item.IsBlockingPos(SceneryBlocker.BlockMode.Regrow, posScene, radius))
                    return false;
            }
            if (ManEncounterPlacement.IsOverlappingSafeAreaOrEncounter(posScene, radius))
                return false;
            WorldTile worldTile = TM.LookupTile(tileCoord, false);
            if (worldTile != null && worldTile.IsLoaded)
            {
                foreach (var item in worldTile.Visibles[1])
                {
                    if ((item.Value.centrePosition - posScene).WithinSquareXZ(radius))
                        return false;
                }
            }
            else
            {
                ManSaveGame.StoredTile storedTile = Singleton.Manager<ManSaveGame>.inst.GetStoredTile(tileCoord, false);
                List<ManSaveGame.StoredVisible> list;
                if (storedTile != null && storedTile.m_StoredVisibles.TryGetValue(1, out list) && list != null)
                {
                    foreach (ManSaveGame.StoredVisible storedVisible in list)
                    {
                        if ((storedVisible.GetBackwardsCompatiblePosition() - posScene).WithinSquareXZ(radius))
                            return false;
                    }
                }
            }
            return true;
        }

        public static bool CanPurgeTradingStation(int team, string locName)//GSO Trading Station - GSOTradingStation
        {
            return team == ManSpawn.NeutralTeam && !locName.NullOrEmpty() &&
                (locName == "GSOTradingStation" || locName == "GSO Trading Station");
        }
        public static bool CanPurgeTeamNotPlayerOwned(int team)
        {
            return !ManBaseTeams.IsPlayerOwnedAIBaseTeam(team) && !IsPlayerTeam(team);
        }
        internal static int MassDeleteALLOrphanTrackedVisibles()
        {
            int removeCount = 0;
            try
            {
                var cullList = new List<TrackedVisible>(ManVisible.inst.AllTrackedVisibles);
                foreach (var item in cullList)
                {
                    if (item != null && CanPurgeTeamNotPlayerOwned(item.TeamID) && !item.IsQuestObject &&
                        !item.IsVendor && item.ObjectType == ObjectTypes.Vehicle)
                    {
                        ManVisible.inst.StopTrackingVisible(item.ID);
                        removeCount++;
                    }
                }

                DebugTAC_AI.DevPopupLog(KickStart.ModID + ": Removed [" + removeCount + "] orphaned visibles.");
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log("Failed to LogAllTrackedEnemyBaseVisibles() - " + e);
            }
            return removeCount;
        }
        internal static int MassDeleteALLEnemyTeamTechs()
        {
            int removeCount = 0;
            try
            {
                var cullList = new List<TrackedVisible>(ManVisible.inst.AllTrackedVisibles);
                foreach (var item in cullList)
                {
                    if (item != null && (CanPurgeTeamNotPlayerOwned(item.TeamID)))
                    {
                        if (ManBaseTeams.IsBaseTeamAny(item.TeamID))
                        {
                            ManVisible.inst.StopTrackingVisible(item.ID);
                            removeCount++;
                        }
                        else if (ManBaseTeams.IsBaseTeamDynamicOrUnregistered(item.TeamID))
                        {
                            ManVisible.inst.StopTrackingVisible(item.ID);
                            removeCount++;
                        }
                    }
                }
                var iterateTEchs = new List<Tank>(ManTechs.inst.CurrentTechs);
                foreach (var item in iterateTEchs)
                {
                    if (item != null && CanPurgeTeamNotPlayerOwned(item.Team))
                    {
                        if (ManBaseTeams.IsBaseTeamAny(item.Team))
                        {
                            Purge(item);
                            removeCount++;
                        }
                        else if (ManBaseTeams.IsBaseTeamDynamicOrUnregistered(item.Team))
                        {
                            Purge(item);
                            removeCount++;
                        }
                    }
                }
                var jsonTiles = ManSaveGame.inst.CurrentState?.m_StoredTilesJSON;
                if (jsonTiles != null && jsonTiles != null)
                {
                    ManSaveGame.StoredTile storedTile = null;
                    bool changed = true;
                    while (changed)
                    {   // too kuking lazy to deal with the stupid iterator exception so keep whailing at it until we do no more changes
                        changed = false;
                        try
                        {
                            foreach (var tile in new Dictionary<IntVector2, string>(jsonTiles))
                            {
                                if (tile.Value.NullOrEmpty())
                                    continue;
                                ManSaveGame.LoadObjectFromRawJson(ref storedTile, tile.Value, false, false);
                                if (storedTile != null && storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out var vals2))
                                {   // Try in the unloaded tile!?
                                    int removedFromThis = 0;
                                    for (int step = 0; step < vals2.Count; step++)
                                    {
                                        var val = vals2[step] as ManSaveGame.StoredTech;
                                        if (val != null && (BaseTeamVisibleIsSafelyRemoveable(val.m_ID, val.m_TeamID) || (val.m_TechData?.LocalisedName != null
                                            && CanPurgeTradingStation(val.m_TeamID, val.m_TechData.LocalisedName.m_Id))))
                                        {
                                            vals2.RemoveAt(step);
                                            step--;
                                            removedFromThis++;
                                            changed = true;
                                        }
                                    }
                                    if (removedFromThis > 0)
                                    {
                                        ManUI.inst.ShowErrorPopup("REMOVED " + removedFromThis + " TECHS FROM TILE AT COORD " + tile.Key);
                                        ManSaveGame.inst.CurrentState.m_StoredTilesJSON.Remove(tile.Key);
                                        ManSaveGame.inst.CurrentState.m_StoredTilesJSON.Add(tile.Key, ManSaveGame.SaveObjectToRawJson(storedTile));
                                    }
                                }
                            }
                        }
                        catch (Exception) { }
                    }
                }

            }
            catch (Exception e)
            {
                DebugTAC_AI.Log("Failed to LogAllTrackedEnemyBaseVisibles() - " + e);
            }
            return removeCount;
        }
        internal static void LogAllTrackedEnemyBaseVisibles(bool DoCullInvalid)
        {
            if (!ManNetwork.IsHost)
                return;
            if (DebugTAC_AI.DoLogInfos)
                DebugTAC_AI.Log(KickStart.ModID + ": Doing BaseTeam TrackedVisibles sanity check...");
            else
                DebugTAC_AI.Log(KickStart.ModID + ": Checking for BaseTeam TrackedVisibles errors...");
            try
            {
                int removeCount = 0;
                var cullList = new List<TrackedVisible>(ManVisible.inst.AllTrackedVisibles);
                foreach (var item in cullList)
                {
                    if (item != null)
                    {
                        if (ManBaseTeams.IsBaseTeamAny(item.TeamID))
                        {
                            if (item.wasDestroyed)
                            {
                                DebugTAC_AI.Info(KickStart.ModID + ": ID [" + item.ID + "] DESTROYED, tracked, registered team"
                                    + (DoCullInvalid ? " - REMOVED" : string.Empty));
                                if (DoCullInvalid)
                                {
                                    ManVisible.inst.StopTrackingVisible(item.ID);
                                    removeCount++;
                                }
                            }
                            else if (item.visible != null)
                            {
                                DebugTAC_AI.Info(KickStart.ModID + ": ID [" + item.ID + "] Active, tracked, registered team");
                            }   // The visible is null!!! - now check to see if it exists somewhere
                            else if (ManSaveGame.inst.LookupSerializedVisible(item.HostID) != null)
                            {   // It EXISTS, we do not remove
                                DebugTAC_AI.Info(KickStart.ModID + ": ID [" + item.ID + "] Serialized, tracked, registered team");
                            }
                            else
                            {   // It might be in limbo???
                                WorldPosition WP = item.GetWorldPosition();
                                WorldTile tile = ManWorld.inst.TileManager.LookupTile(WP.TileCoord);
                                if (tile != null && tile.StoredVisiblesWaitingToLoad != null &&
                                    tile.StoredVisiblesWaitingToLoad.Exists(x => x.m_ID == item.ID))
                                {
                                    DebugTAC_AI.Info(KickStart.ModID + ": ID [" + item.ID + "] Loading, tracked, registered team");
                                }
                                else if (ManTechs.inst.IterateTechs().Any(x => x.visible.ID == item.HostID))
                                {
                                    DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]!!! Active NOT hooked to TV, tracked, registered team");
                                }
                                else
                                {   // UNLOADED!?!
                                    var ST = FindStoredTech(item.ID, WP.TileCoord, false);
                                    if (ST != null)
                                        DebugTAC_AI.Info(KickStart.ModID + ": ID [" + item.ID + "] Stored, tracked, registered team");
                                    else
                                    {
                                        if (ST != null)
                                            DebugTAC_AI.Info(KickStart.ModID + ": ID [" + item.ID + "] JSON Stored, tracked, registered team");
                                        else
                                        {
                                            DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]!!! NOT existing, tracked, registered team"
                                                + (DoCullInvalid ? " - REMOVED" : string.Empty));
                                            if (DoCullInvalid)
                                            {
                                                ManVisible.inst.StopTrackingVisible(item.ID);
                                                removeCount++;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (ManBaseTeams.IsBaseTeamDynamicOrUnregistered(item.TeamID))
                        {
                            if (item.wasDestroyed)
                            {
                                DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]! DESTROYED, tracked, team NOT registered"
                                    + (DoCullInvalid ? " - REMOVED" : string.Empty));
                                if (DoCullInvalid)
                                {
                                    ManVisible.inst.StopTrackingVisible(item.ID);
                                    removeCount++;
                                }
                            }
                            else if (item.visible != null)
                            {
                                DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]! Active, tracked, team NOT registered");
                            }   // The visible is null!!! - now check to see if it exists somewhere
                            else if (ManSaveGame.inst.LookupSerializedVisible(item.HostID) != null)
                            {   // It EXISTS, we do not remove
                                DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]! Serialized, tracked, team NOT registered");
                            }
                            else
                            {   // It might be in limbo???
                                WorldPosition WP = item.GetWorldPosition();
                                WorldTile tile = ManWorld.inst.TileManager.LookupTile(WP.TileCoord);
                                if (tile != null && tile.StoredVisiblesWaitingToLoad != null &&
                                    tile.StoredVisiblesWaitingToLoad.Exists(x => x.m_ID == item.ID))
                                {
                                    DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]! Loading, tracked, team NOT registered");
                                }
                                else if (ManTechs.inst.IterateTechs().Any(x => x.visible.ID == item.HostID))
                                {
                                    DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]!!!! Active NOT hooked to TV, tracked, team NOT registered");
                                }
                                else
                                {   // UNLOADED!?!
                                    var ST = FindStoredTech(item.ID, WP.TileCoord, false);
                                    if (ST != null)
                                        DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]!!!! Stored, tracked, team NOT registered");
                                    else
                                    {
                                        ST = FindStoredTech(item.ID, WP.TileCoord, true);
                                        if (ST != null)
                                            DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]!!!! JSON Stored, tracked, team NOT registered");
                                        else
                                        {
                                            DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.ID + "]!!!! NOT existing, tracked, team NOT registered"
                                                + (DoCullInvalid ? " - REMOVED" : string.Empty));
                                            if (DoCullInvalid)
                                            {
                                                ManVisible.inst.StopTrackingVisible(item.ID);
                                                removeCount++;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                foreach (var item in ManTechs.inst.IterateTechs())
                {
                    if (item != null)
                    {
                        if (ManBaseTeams.IsBaseTeamAny(item.Team))
                        {
                            TrackedVisible TV = ManVisible.inst.GetTrackedVisible(item.visible.ID);
                            if (TV == null)
                                DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.visible.ID + "]!!!  Active NOT hooked to TV, NOT tracked, registered team");
                        }
                        else if (ManBaseTeams.IsBaseTeamDynamicOrUnregistered(item.Team))
                        {
                            TrackedVisible TV = ManVisible.inst.GetTrackedVisible(item.visible.ID);
                            if (TV == null)
                                DebugTAC_AI.DevPopupLog(KickStart.ModID + ": ID [" + item.visible.ID + "]!!!! Active NOT hooked to TV, NOT tracked, team NOT registered");
                        }
                    }
                }

                DebugTAC_AI.DevPopupLog(KickStart.ModID + ": Removed [" + removeCount + "] invalid visibles.");
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log("Failed to LogAllTrackedEnemyBaseVisibles() - " + e);
            }
        }

        internal static void AbsolutelyDestroy(int ID)
        {
            if (!ManNetwork.IsHost)
                return;
            try
            {
                var tV = ManVisible.inst.AllTrackedVisibles.FirstOrDefault(x => x != null && x.ID == ID);
                if (tV != null)
                {
                    ManVisible.inst.ObliterateTrackedVisibleFromWorld(tV);
                    return;
                }
                var getter = ManTechs.inst.CurrentTechs.FirstOrDefault(x => x?.visible != null && x.visible.ID == ID);
                if (getter != null)
                {
                    getter.visible.RemoveFromGame();
                    return;
                }
                DebugTAC_AI.Assert(KickStart.ModID + ": AbsolutelyDestroy - failed to purge visible!!!!");
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log("Failed to AbsolutelyDestroy() - " + e);
            }
        }
        internal static void SanityCheckActuallyDestroyedFromSerialization(int ID)
        {
            if (!ManNetwork.IsHost)
                return;
            try
            {
                var tV = ManVisible.inst.AllTrackedVisibles.FirstOrDefault(x => x != null && x.ID == ID);
                if (tV != null)
                {
                    ManVisible.inst.ObliterateTrackedVisibleFromWorld(tV);
                    return;
                }
                var getter = ManTechs.inst.CurrentTechs.FirstOrDefault(x => x?.visible != null && x.visible.ID == ID);
                if (getter != null)
                {
                    getter.visible.RemoveFromGame();
                    return;
                }
                DebugTAC_AI.Assert(KickStart.ModID + ": AbsolutelyDestroy - failed to purge visible!!!!");
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log("Failed to AbsolutelyDestroy() - " + e);
            }
        }

        //Actions

        /// <summary>
        /// Remove a Tech from existance
        /// </summary>
        /// <param name="tech"></param>
        /// <param name="removeFromJSONToo"></param>
        internal static void Purge(ManSaveGame.StoredTech tech, bool removeFromJSONToo)
        {   //
            if (ManNetwork.IsNetworked)
            {
            }
            else
            {
                RemoveStoredTech(tech.m_ID, GetWorldPos(tech).TileCoord, removeFromJSONToo);
            }
        }
        /// <summary>
        /// Remove a Tech from existance
        /// </summary>
        /// <param name="tech"></param>
        /// <param name="player"></param> REVISED: this overload only takes the tech parameter; there is no player parameter
        internal static void Purge(Tank tech)
        {   //
            if (ManNetwork.IsNetworked)
            {
                PurgeHost(tech.visible.ID, tech.name);
            }
            else
            {
                if (!PurgeHost(tech.visible.ID, tech.name))
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Purge - Trying to Purge by visible " + tech.name);
                    tech.visible.RemoveFromGame();
                }
            }
        }
        /// <summary>
        /// Remove a Tech from existance
        /// </summary>
        /// <param name="HostVisibleID"></param>
        /// <param name="name"></param>
        internal static bool PurgeHost(int HostVisibleID, string name)
        {   //
            if (!ManNetwork.IsHost)
                throw new Exception(KickStart.ModID + ": SpecialAISpawner.PurgeHost called on non-host");
            DebugTAC_AI.Log(KickStart.ModID + ": PurgeHost - Name " + name + " | " + HostVisibleID + "  Callstack: " + StackTraceUtility.ExtractStackTrace());
            if (ManNetwork.IsNetworked)
            {
                try
                {
                    TrackedVisible TV = ManVisible.inst.GetTrackedVisibleByHostID(HostVisibleID);
                    Singleton.Manager<ManNetwork>.inst.SendToServer(TTMsgType.UnspawnTech, new UnspawnTechMessage
                    {
                        m_HostID = TV.HostID,
                        m_CheatBypassInventory = true,
                    }
                    );
                    DebugTAC_AI.Log(KickStart.ModID + ": Purge - PURGED " + name + " (MP)");
                    AIGlobals.SceneTechCount = -1;
                    return true;
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Purge - Failed to purge " + name + " (MP)");
                    foreach (var item in new List<TrackedVisible>(ManVisible.inst.AllTrackedVisibles))
                    {
                        if (item == null)
                            continue;
                        if (item.ObjectType == ObjectTypes.Vehicle)
                        {
                            if (ManWorld.inst.TileManager.IsTileAtPositionLoaded(item.Position))
                            {
                                if (item.wasDestroyed || item.visible == null)
                                {
                                    if (ManBaseTeams.IsBaseTeamDynamic(item.TeamID))
                                    {
                                        DebugTAC_AI.Log("  Invalid Base Team Tech visible " + item.ID + ",  Team " + item.TeamID + ",  Destroyed " + item.wasDestroyed);
                                        ManVisible.inst.StopTrackingVisible(item.ID);
                                    }
                                }
                            }
                        }
                    }
                    DebugTAC_AI.Log(KickStart.ModID + ": Purge - Error backtrace - " + e);
                }
            }
            else
            {
                try
                {
                    TrackedVisible TV = ManVisible.inst.GetTrackedVisible(HostVisibleID);
                    if (TV != null)
                    {
                        ManVisible.inst.ObliterateTrackedVisibleFromWorld(TV);
                        if (ManTechs.inst.IterateTechs().Any(x => x != null &&x.visible.ID == HostVisibleID))
                            throw new InvalidOperationException("Purge didn't work");
                        else
                            DebugTAC_AI.Log(KickStart.ModID + ": Purge - PURGED " + name);
                    }
                    else
                    {
                        AbsolutelyDestroy(HostVisibleID);
                    }
                    SceneTechCount = -1;
                    return true;
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Purge - Failed to purge " + name + " (SINGLE player)");
                    foreach (var item in new List<TrackedVisible>(ManVisible.inst.AllTrackedVisibles))
                    {
                        if (item == null)
                            continue;
                        if (item.ObjectType == ObjectTypes.Vehicle)
                        {
                            if (ManWorld.inst.TileManager.IsTileAtPositionLoaded(item.Position))
                            {
                                if (item.wasDestroyed || item.visible == null)
                                {
                                    if (ManBaseTeams.IsBaseTeamDynamic(item.TeamID))
                                    {
                                        DebugTAC_AI.Log("  Invalid Base Team Tech visible " + item.ID + ",  Team " + item.TeamID + ",  Destroyed " + item.wasDestroyed);
                                        ManVisible.inst.StopTrackingVisible(item.ID);
                                    }
                                }
                            }
                        }
                    }
                    DebugTAC_AI.Log(KickStart.ModID + ": Purge - Error backtrace - " + e);
                }
            }
            return false;
        }
        /// <summary>
        /// Remove a Tech from existance the cool way
        /// </summary>
        /// <param name="tech"></param>
        /// <param name="player"></param> REVISED: this method only takes the tech parameter; there is no player parameter
        internal static void Eradicate(Tank tech)
        {   //
            if (ManNetwork.IsNetworked)
            {   // Too laggy to use distintegrate and explode - we just remove it normally instead.
                try
                {
                    TrackedVisible TV = ManVisible.inst.GetTrackedVisibleByHostID(tech.netTech.HostID);
                    Singleton.Manager<ManNetwork>.inst.SendToServer(TTMsgType.UnspawnTech, new UnspawnTechMessage
                    {
                        m_HostID = TV.HostID,
                        m_CheatBypassInventory = true,
                    }
                    );
                }
                catch { }
            }
            else
            {
                if (PlayerIsOnLowQualitySettings ||
                    ((Singleton.playerPos - tech.boundsCentreWorld).sqrMagnitude > EradicateEffectMaxDistanceSqr))
                {
                    Purge(tech);
                    return;
                }
                foreach (TankBlock block in tech.blockman.IterateBlocks())
                {
                    try
                    {
                        if (!block.damage.AboutToDie)
                            block.damage.SelfDestruct(0.5f);
                    }
                    // REVISED: block self-destruct failure now warned once instead of silently swallowed.
                    catch (Exception eDest) { DebugTAC_AI.LogWarnPlayerOnce("[TAC_AI:catch:Globals] eradicate block self-destruct", eDest); }
                }
                tech.blockman.Disintegrate();
            }
        }
    }
}
