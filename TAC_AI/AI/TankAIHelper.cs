using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI.AlliedOperations;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;
using TAC_AI.Templates;
using TAC_AI.World;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI
{
    /// <summary>
    /// This AI either runs normally in Singleplayer, or on the Server in Multiplayer
    /// </summary>
    /// REVISED (overview): AttackEnemy renamed WantsToFight; combat Circle stop-and-shoot replaced by the TurretFraction duty cycle (CombatWantsCircleNow).
    /// REVISED (overview): MovementController swaps now route through RequestMovementControllerSwap/SwapMovementController and MovementDispatch container kinds.
    /// REVISED (overview): DetermineCombat split into retreat-only DetermineRetreatPosture(Enemy); target ownership centralized in SetPursuit/TrySetPursuit/ReleaseTarget with range/LOS-grace hysteresis.
    /// REVISED (overview): tick-counter fields (DelayedAnchorClock/unanchorCountdown/actionPause/lightBoost) migrated to seconds-based AITimer shims; CheckRebuildAlignment refactored into DispatchAlignment + Apply*Alignment by MpRole.
    public class TankAIHelper : MonoBehaviour, IWorldTreadmill
    {

        public Tank tank;
        public AITreeType.AITypes lastAIType;
        //Tweaks (controlled by Module)
        /// <summary> The type of vehicle the AI controls </summary>
        public AIDriverType DriverType
        {
            get => driveType;
            private set
            {
                if (driveType != value)
                {
                    //DebugTAC_AI.Assert("Driver Type change " + driveType + " -> " + value);
                    driveType = value;
                }
            }
        }
        private AIDriverType driveType = AIDriverType.AutoSet;
        // REVISED: AutoSet with a built tank now resolves the driver immediately via ExecuteAutoSetNoCalibrate instead of storing AutoSet verbatim; controller swap routed through RequestMovementControllerSwap.
        public void SetDriverType(AIDriverType driverType)
        {
            if (driverType == AIDriverType.AutoSet && tank != null && tank.blockman != null && tank.blockman.blockCount > 0)
                ExecuteAutoSetNoCalibrate();
            else
                DriverType = driverType;
            RequestMovementControllerSwap(MovementSwapReason.SetDriverType);
        }
        // REVISED: replaces the bare `MovementAIControllerDirty = true` writes everywhere; records the reason + appends to state history so controller swaps are traceable.
        internal enum MovementSwapReason
        {
            SetDriverType, Subscribe, Recycled, RemoteAIType, ReValidate, AlignmentReset,
            ExecuteAutoSet, PlayerRecompose, SwitchAI, ForceEscort, WakeForChange,
            PlayerAutopilot, EnemyGenerate, EnemyMindSetup,
        }
        private bool MCD = false;
        private MovementSwapReason lastSwapRequest;
        public bool MovementAIControllerDirty
        {
            get => MCD;
            private set => MCD = value;
        }
        internal void RequestMovementControllerSwap(MovementSwapReason reason)
        {
            if (!MCD)
                AppendHistory("SwapReq " + reason);
            lastSwapRequest = reason;
            MovementAIControllerDirty = true;
        }
        /// <summary> The task the AI will perform </summary>
        public AIType DediAI = AIType.Escort;
        /// <summary> How to attack the enemy </summary>
        public EAttackMode AttackMode = EAttackMode.Circle; // How to attack the enemy
        // REVISED: TurretFraction = wide-gimbal-turret share of weapons (0 all front-fixed, 1 all turreted), set in (R/E)WeapSetup.GetAttackStrat; drives the combat circle/face duty cycle.
        public float TurretFraction = 0f;
        private float combatCyclePhase01 = -1f;  // per-tech random phase offset so neighbours desync their circle/face windows
        private AlliedOperationsController _OpsController;
        internal AlliedOperationsController OpsController
        {
            get
            {
                if (_OpsController != null)
                {
                    return _OpsController;
                }
                else
                {
                    _OpsController = new AlliedOperationsController(this);
                    return _OpsController;
                }
            }
        }
        public List<ModuleAIExtension> AIList;
        public AIERepair.DesignMemory TechMemor { get; internal set; }
        public void InsureTechMemor(string context, bool doFirstSave)
        {
            if (TechMemor.IsNull())
            {
                TechMemor = tank.gameObject.AddComponent<AIERepair.DesignMemory>();
                TechMemor.Initiate(doFirstSave);

                DebugTAC_AI.LogAISetup(KickStart.ModID + ": Tech " + tank.name + " Setup for DesignMemory (" + context + ")");
            }
        }

        // Checking Booleans
        public bool AIDriving {
            get
            {
                if (AIAlign != AIAlignment.Static)
                {
                    if (RTSControlled)
                        return true;
                    return !tank.TechIsActivePlayer();
                }
                else
                    return false;

            }
        }
        public bool Allied => AIAlign == AIAlignment.Player;
        public bool IsPlayerControlled => AIAlign == AIAlignment.PlayerNoAI || AIAlign == AIAlignment.Player;
        public bool ActuallyWorks => hasAI || tank.PlayerFocused;
        // REVISED: SetToActive now also requires lastAITypeResolved (set once TryGetCurrentAIType succeeds) so the AI stays suspended until its vanilla AI type is known, instead of treating an unresolved tech as active.
        internal bool lastAITypeResolved = false;
        public bool SetToActive => lastAITypeResolved && lastAIType != AITreeType.AITypes.Idle;
        public bool AITypeUnresolved => !lastAITypeResolved;
        public bool IsRTSReceivable => !IsMultiTech;
        private int consecutiveNullMovementControllerTicks;
        // REVISED: AIAlign is latched at OnPreUpdate and read as TickAIAlign through the tick, so an alignment switch mid-tick cannot make Directors/Operations branch inconsistently.
        private AIAlignment tickAIAlign = AIAlignment.Static;
        private bool tickAlignmentLatched = false;
        internal AIAlignment TickAIAlign => tickAlignmentLatched ? tickAIAlign : AIAlign;
        public bool NotInBeam => BeamTimeoutClock == 0;
        public bool CanCopyControls => !IsMultiTech || tank.PlayerFocused;
        public bool CanUseBuildBeam => !(tank.IsAnchored && !PlayerAllowAutoAnchoring);
        // REVISED: gained the `unanchorCountdown <= 0` term so a tech that just unanchored cannot immediately re-auto-anchor during the unanchor warn window.
        public bool CanAutoAnchor => AutoAnchor && PlayerAllowAutoAnchoring && !WantsToFight && tank.Anchors.NumPossibleAnchors > 0
            && tank.Anchors.NumIsAnchored == 0 && DelayedAnchorClock >= AIGlobals.BaseAnchorMinimumTimeDelay
            && unanchorCountdown <= 0 && CanAnchorNow;
        public bool IsAutoAnchored => AutoAnchor && PlayerAllowAutoAnchoring && tank.Anchors.NumIsAnchored > 0;
        public bool CanAnchorNow => CanAttemptAnchor && CanAnchorSafely;
        public bool CanAnchorSafely => !lastEnemyGet || (lastEnemyGet && lastCombatRange > AIGlobals.SafeAnchorDist);
        public bool CanAttemptAnchor => anchorAttempts <= AIGlobals.MaxAnchorAttempts;
        public bool MovingAndOrHasTarget => tank.IsAnchored ? lastEnemyGet : DriverType == AIDriverType.Pilot ||
            (DriveDirDirected > EDriveFacing.Neutral && (ThrottleState == AIThrottleState.ForceSpeed || DoSteerCore));
        public bool UsingPathfinding => ControlCore.DrivePathing >= EDrivePathing.Path;

        // Settables in ModuleAIExtension - "turns on" functionality on the host Tech, none of these force it off
        /// <summary> Should the other mimic AIs ignore controls from this Tech?
        /// Additionally when anchored, ignore collision with this Tech? </summary>
        private bool _isMultiTech = false;
        public bool IsMultiTech
        {
            get => _isMultiTech;
            set
            {
                if (KickStart.DoLogOwnership && value != _isMultiTech)
                    DebugTAC_AI.LogOwnership("IsMultiTech", _isMultiTech, value);
                _isMultiTech = value;
            }
        }
        /// <summary> Should the AI chase the enemy? </summary>
        public bool ChaseThreat = true;
        /// <summary> Should the AI Auto-BuildBeam on flip? </summary>
        public bool RequestBuildBeam = true;

        // Player Toggleable
        /// <summary> Should the AI take combat calculations and retreat if nesseary? </summary>
        public bool AdvancedAI => Allied ? (AISetSettings.AdvancedAI && AILimitSettings.AdvancedAI) : AISetSettings.AdvancedAI;
        /// <summary> Should the AI only follow player movement while in MT mode? </summary>
        public bool AllMT => Allied ? (AISetSettings.AllMT && AILimitSettings.AllMT) : AISetSettings.AllMT;
        /// <summary> Should the AI ram the enemy? </summary>
        public bool FullMelee => Allied ? (AISetSettings.FullMelee && AILimitSettings.FullMelee) : AISetSettings.FullMelee;
        /// <summary> Should the AI circle the enemy? </summary>
        public bool SideToThreat => Allied ? (AISetSettings.SideToThreat && AILimitSettings.SideToThreat) : AISetSettings.SideToThreat;

        // REVISED: new turret-fraction duty cycle. Returns true (circle) for ~TurretFraction of each KickStart.CombatFacingCyclePeriod, else false (face).
        // Replaces the old ActionPause>120 stop-and-shoot gate in the combat Circle bucket; read by RWheeled Circle + allied LandAICore.
        public bool CombatWantsCircleNow()
        {
            float frac = Mathf.Clamp01(TurretFraction);
            if (frac <= 0f)
                return false;
            if (frac >= 1f)
                return true;
            float period = KickStart.CombatFacingCyclePeriod;
            if (period <= 0.05f)
                return frac >= 0.5f;
            if (combatCyclePhase01 < 0f)
                combatCyclePhase01 = UnityEngine.Random.value;
            float phase = (Time.time / period + combatCyclePhase01) % 1f;
            return phase < frac;
        }

        // Repair Auxilliaries
        /// <summary> Auto-repair builds the tech to the last memory state.
        /// This does not save between play sessions. </summary>
        public bool AutoRepair => Allied ? (AISetSettings.AutoRepair && AILimitSettings.AutoRepair) : AISetSettings.AutoRepair;
        /// <summary> Draw from player inventory reserves </summary>
        public bool UseInventory => Allied ? (AISetSettings.UseInventory && AILimitSettings.UseInventory) : AISetSettings.UseInventory;


        // Additional
        /// <summary> Should the AI toggle the anchor when it is still? </summary>
        public bool AutoAnchor = false;
        /// <summary> Should the AI avoid two techs at once? </summary>
        public bool SecondAvoidence = false;

        // Distance operations - Automatically accounts for tech sizes
        public AISettingsSet AISetSettings = AISettingsSet.DefaultSettable;
        public AISettingsLimit AILimitSettings = default;
        /// <summary> Spacing: The range the AI will linger from the enemy while attacking if PursueThreat is true </summary>
        public float MinCombatRange => AISetSettings.CombatSpacing;
        /// <summary> Chase: How far should we pursue the enemy? </summary>
        public float MaxCombatRange => AISetSettings.CombatChase;
        /// <summary> The range the AI will linger from the target objective in general </summary>
        public float MaxObjectiveRange => AISetSettings.ObjectiveRange;
        internal float JobSearchRange
        {
            get => AISetSettings.GetJobRange(tank);
            set => AISetSettings.ScanRange = value;
        }

        // Allied AI Operating Allowed types (self-filling)
        // WARNING - These values are set to TRUE when called.
        private AIEnabledModes AIWorkingModes = AIEnabledModes.None;
        public bool isAssassinAvail //Is there an Assassin-enabled AI on this tech?
        {
            get { return AIWorkingModes.HasFlag(AIEnabledModes.Assassin); }
            set { AIWorkingModes |= AIEnabledModes.Assassin; }
        }
        public bool isAegisAvail    //Is there an Aegis-enabled AI on this tech?
        {
            get { return AIWorkingModes.HasFlag(AIEnabledModes.Aegis); }
            set { AIWorkingModes |= AIEnabledModes.Aegis; }
        }
        public bool isProspectorAvail  //Is there a Prospector-enabled AI on this tech?
        {
            get { return AIWorkingModes.HasFlag(AIEnabledModes.Prospector); }
            set { AIWorkingModes |= AIEnabledModes.Prospector; }
        }
        public bool isScrapperAvail   //Is there a Scrapper-enabled AI on this tech?
        {
            get { return AIWorkingModes.HasFlag(AIEnabledModes.Scrapper); }
            set { AIWorkingModes |= AIEnabledModes.Scrapper; }
        }
        public bool isEnergizerAvail   //Is there a Energizer-enabled AI on this tech?
        {
            get { return AIWorkingModes.HasFlag(AIEnabledModes.Energizer); }
            set { AIWorkingModes |= AIEnabledModes.Energizer; }
        }
        public bool isAviatorAvail
        {
            get { return AIWorkingModes.HasFlag(AIEnabledModes.Aviator); }
            set { AIWorkingModes |= AIEnabledModes.Aviator; }
        }
        public bool isAstrotechAvail
        {
            get { return AIWorkingModes.HasFlag(AIEnabledModes.Astrotech); }
            set { AIWorkingModes |= AIEnabledModes.Astrotech; }
        }
        public bool isBuccaneerAvail
        {
            get { return AIWorkingModes.HasFlag(AIEnabledModes.Buccaneer); }
            set { AIWorkingModes |= AIEnabledModes.Buccaneer; }
        }

        // General AI Handling
        public AIRunState RunState {
            get => _RunState;
            set
            {
                if (_RunState != value)
                {
                    switch (value)
                    {
                        case AIRunState.Off:
                        case AIRunState.Default:
                        case AIRunState.Advanced:
                            break;
                        default:
                            throw new InvalidOperationException("TankAIHelper.RunState set to invalid state " + value);
                    }
                    _RunState = value;
                }
            }
        }
        private AIRunState _RunState = AIRunState.Advanced;      // Disable the AI to make way for Default AI

        /// <summary>
        /// 0 is off, 1 is enemy, 2 is obsticle
        /// REVISED: AIWeaponState now reads Normal(0), Enemy(1), HoldFire(2), Obsticle(3), Mimic(4).
        /// </summary>
        public AIWeaponState ActiveAimState = AIWeaponState.Normal;
        public AIWeaponType WeaponAimType = AIWeaponType.Unknown;
        public void ResetToNormalAimState()
        {
            SuppressFiring(false);
            WeaponState = AIWeaponState.Normal;
            ActiveAimState = AIWeaponState.Normal;
        }
        public bool NeedsLineOfSight => WeaponAimType == AIWeaponType.Direct;
        public bool BlockedLineOfSight = false;
        // REVISED: BlockedLineOfSight is now debounced - it only flips true after LosBlockedStreakThreshold consecutive blocked LOS checks (counter below), to stop strafe/stand flicker.
        private int _losBlockedStreak = 0;
        // REVISED: combat-bucket hysteresis flag - set while retreating in the Ranged bucket; read by AvoidAssist* to suppress sideways re-targeting so the retreat vector wins.
        public bool WasRetreatingInCombat = false;
        internal bool beEvilRegenAttempted = false;

        public AIAlignment AIAlign = AIAlignment.Static;             // 0 is static, 1 is ally, 2 is enemy REVISED: AIAlignment now reads Static(0), PlayerNoAI(1), Player(2), NonPlayer(3), Neutral(4).
        public AIWeaponState WeaponState = AIWeaponState.Normal;    // 0 is sleep, 1 is target, 2 is obsticle, 3 is mimic REVISED: AIWeaponState now reads Normal(0), Enemy(1), HoldFire(2), Obsticle(3), Mimic(4).
        public bool UpdateDirectorsAndPathing = false;       // Collision avoidence active this FixedUpdate frame? REVISED: gates whether the director + pathing pass (WeaponDirector / DriveDirector) runs this FixedUpdate frame.
        public bool UsingAirControls = false; // Use the not-VehicleAICore cores. REVISED: VehicleAICore (the old combined driver) was removed; this now means the per-type cores - Land/Sea/Space/Static plus the Air cores.
        internal int FrustrationMeter = 0;  // tardiness buildup before we use our guns to remove obsticles
        internal float Urgency = 0;         // tardiness buildup before we just ignore obstructions
        internal float UrgencyOverload = 0; // builds up too much if our max speed was set too high

        /// <summary>
        /// Repairs requested?
        /// </summary>
 public bool PendingDamageCheck = true;

        public float DamageThreshold = 0;   // How much damage have we taken? (100 is total destruction)

        // Directional Handling
        /// <summary>
        /// IN WORLD SPACE
        /// Handles all Director/Operator decisions
        /// </summary>
        internal Vector3 lastDestinationOp => ControlOperator.lastDestination; // Where we drive to in the world
        /// <summary>
        /// IN WORLD SPACE
        /// Handles all Core decisions
        /// </summary>
        internal Vector3 lastDestinationCore => ControlCore.lastDestination;// Vector3.zero;    // Where we drive to in the world

        internal float lastOperatorRange { get { return _lastOperatorRange; } private set { _lastOperatorRange = value; } }
        private float _lastOperatorRange = 0;
        internal float lastCombatRange => _lastCombatRange;
        private float _lastCombatRange = 0;
        internal float lastPathPointRange = 0;
        public float NextFindTargetTime = 0;      // Updates to wait before target swatching

        //AutoCollection
        internal bool hasAI = false;    // Has an active AI module
        /// <summary>
        /// Set to dirty when we make any changes to the AI
        /// </summary>
        internal AIDirtyState dirtyAI = AIDirtyState.Not;  // Update Player AI state if needed
        public enum AIDirtyState
        {
            Not,
            /// <summary>Reboots the AI if it just changed alignment</summary>
            Dirty,
            /// <summary>Forces the AI to reboot as if it was just loaded into the world, very costly.</summary>
            DirtyAndReboot,
        }
        internal bool dirtyExtents = false;    // The Tech has new blocks attached recently

        internal float EstTopSped = 0;
        internal float recentSpeed = 1;
        internal float recentSpeedSigned = 0;
        private int anchorAttempts = 0;
        internal float lastTechExtents = 1;
        internal float lastAuxVal = 0;
        public Visible lastPlayer;
        public Visible lastEnemyGet { get => lastEnemy; }
        // REVISED: lastEnemy is now a property; clearing it to null also drops KeepEnemyFocus so the target lock cannot outlive its target.
        private Visible _lastEnemy = null;
        internal Visible lastEnemy
        {
            get => _lastEnemy;
            set
            {
                if (KickStart.DoLogOwnership && value != _lastEnemy)
                    DebugTAC_AI.LogOwnership("lastEnemy",
                        _lastEnemy != null ? _lastEnemy.name : null,
                        value != null ? value.name : null);
                _lastEnemy = value;
                if (value == null && KeepEnemyFocus)
                    KeepEnemyFocus = false;
            }
        }
        public bool RTSManualTargetLock => RTSControlled && RTSDestInternal == RTSDisabled;
        // REVISED: PreserveEnemyTarget now also true while KeepEnemyFocus is held (not just RTS manual lock), so a SetPursuit-locked target survives range hysteresis.
        public bool PreserveEnemyTarget => RTSManualTargetLock || KeepEnemyFocus;
        public Visible lastLockOnTarget;
        public Transform Obst;
        internal Tank lastCloseAlly;
        // Non-Tech specific objective AI Handling
        internal float lastBaseExtremes = 10;
        /// <summary>
        /// Counts also as [recharge home, block rally]
        /// </summary>
        internal Tank theBase = null;
        /// <summary>
        /// Counts also as [loose block, target enemy, target to charge]
        /// </summary>
        private Visible _theResource = null;
        internal Visible theResource
        {
            get => _theResource;
            set
            {
                if (KickStart.DoLogOwnership && value != _theResource)
                    DebugTAC_AI.LogOwnership("theResource",
                        _theResource != null ? _theResource.name : null,
                        value != null ? value.name : null);
                _theResource = value;
            }
        }
        // REVISED: new dedicated objective slots split out from the overloaded theResource (host MT tech, guarded ally, resource node).
        internal Visible theResourceNode = null;
        internal Visible theHostTech = null;
        internal Visible theGuardedAlly = null;
        /// <summary>
        /// The EXACT transform that we want to close in on
        /// </summary>
        internal IAIFollowable lastBasePos;
        internal bool foundBase = false;
        internal bool foundGoal = false;

        // MultiTech AI Handling
        internal HashSet<Tank> MultiTechsAffiliated = new HashSet<Tank>();
        internal bool MTMimicHostAvail = false;
        internal bool MTLockedToTechBeam = false;
        internal Vector3 MTOffsetPos = Vector3.zero;
        internal Vector3 MTOffsetRot = Vector3.forward;
        internal Vector3 MTOffsetRotUp = Vector3.up;

        //  !!ADVANCED!!
        /// <summary>
        /// Use 3D navigation  (VehicleAICore)
        /// REVISED: VehicleAICore was removed; 3D navigation now runs through SpaceAICore, selected via MovementDispatch.
        /// Normally this AI navigates on a 2D plane but this enables it to follow height.
        /// </summary>
        internal bool Attempt3DNavi = false;
        /// <summary>
        /// In WORLD space rotation, position relative from Tech mass center
        /// </summary>
        internal Vector3 Navi3DDirect = Vector3.zero;   // Forwards facing for 3D
        /// <summary>
        /// In WORLD space rotation, position relative from Tech mass center
        /// </summary>
        internal Vector3 Navi3DUp = Vector3.zero;       // Upwards direction for 3D
        public float GroundOffsetHeight = AIGlobals.GroundOffsetGeneralAir;           // flote above ground this dist
        internal Snapshot lastBuiltTech = null;
        internal Vector3 PathPoint => MovementController.PathPoint;

        //Timestep
        // REVISED: the old framerate/MP-dependent tick counters (DelayedAnchorClock, unanchorCountdown, actionPause, LightBoostFeatheringClock) are now seconds-based shims over AITimer.
        // Legacy int set-values are interpreted at fixed ticks-per-second (Anchor=5, ActionPause=500) and the timers self-count via Time.time, so the old `++`/`--`/`-=AIClockPeriod` steps are gone and durations are SP=MP invariant.
        internal const float AnchorTicksPerSecond = 5f;
        private float anchorRestStart = -1f;
        internal int DelayedAnchorClock
        {
            get { return anchorRestStart < 0f ? 0 : Mathf.Min((int)AIGlobals.BaseAnchorMinimumTimeDelay, Mathf.CeilToInt((Time.time - anchorRestStart) * AnchorTicksPerSecond)); }
            set { if (value <= 0) anchorRestStart = -1f; else if (anchorRestStart < 0f) anchorRestStart = Time.time; }
        }
        internal AITimer lightBoostFeatherTimer;
        internal float RepairStepperClock = 0;
        internal short BeamTimeoutClock = 0;
        internal int WeaponDelayClock = 0;
        private AITimer actionPauseTimer;
        internal AITimer beamFlipTimer;
        internal const float ActionPauseTicksPerSecond = 500f;
        internal int actionPause
        {
            get { return actionPauseTimer.Running ? Mathf.CeilToInt(actionPauseTimer.Remaining * ActionPauseTicksPerSecond) : 0; }
            set { if (value <= 0) actionPauseTimer.Clear(); else actionPauseTimer.Set(value / ActionPauseTicksPerSecond); }
        }
        public int ActionPause
        {
            get => actionPause;
            private set => actionPause = value;
        }
        private AITimer unanchorTimer;
        internal int unanchorCountdown
        {
            get { return unanchorTimer.Running ? Mathf.CeilToInt(unanchorTimer.Remaining * AnchorTicksPerSecond) : 0; }
            set { if (value <= 0) unanchorTimer.Clear(); else unanchorTimer.Set(value / AnchorTicksPerSecond); }
        }

        // Hierachy System:
        //   Operations --[ControlPre]-> Maintainer --[ControlPost]-> Core
        //Drive Direction Handlers
        // We need to tell the AI some important information:
        //  Target Destination
        //  Direction to point while heading to the target
        //  Driving direction in relation to driving to the target
        private EControlOperatorSet ControlOperator = EControlOperatorSet.Default;
        // REVISED: ControlOperator now stamps the frame it was last set (SetDirectedControl / MarkOperatorDirty) so UpdateTechControl can detect and warn when the Operations->Maintainer handoff goes stale.
        private int controlOperatorSetTick = 0;
        internal int ControlOperatorAgeFrames => Time.frameCount - controlOperatorSetTick;
        internal bool IsControlOperatorStale =>
            ControlOperatorAgeFrames > KickStart.AIClockPeriod * 3;
        internal void MarkOperatorDirty() => controlOperatorSetTick = Time.frameCount;

        internal EControlOperatorSet GetDirectedControl()
        {
            return ControlOperator;
        }
        internal void SetDirectedControl(EControlOperatorSet cont)
        {
            ControlOperator = cont;
            controlOperatorSetTick = Time.frameCount;
        }
        internal bool IsDirectedMoving => ControlOperator.DriveDest != EDriveDest.None;
        internal bool IsDirectedMovingToDest => ControlOperator.DriveDest > EDriveDest.FromLastDestination;
        // REVISED: now keys purely on DriveDest==FromLastDestination; dropped the extra `ForceSpeed && DriveVar<-0.01` reverse-detection term.
        internal bool IsDirectedMovingFromDest => ControlOperator.DriveDest == EDriveDest.FromLastDestination;

        /// <summary> Drive direction </summary>
        internal EDriveFacing DriveDirDirected => ControlOperator.DriveDir;
        /// <summary> Move to a dynamic target </summary>
        internal EDriveDest DriveDestDirected => ControlOperator.DriveDest;
        private EControlCoreSet ControlCore = EControlCoreSet.Default;
        public string GetCoreControlString()
        {
            return ControlCore.ToString();
        }
        public void SetCoreControlStop()
        {
            SetCoreControl(EControlCoreSet.Default);
        }
        public void SetCoreControl(EControlCoreSet cont)
        {
            // REVISED: rejects a NaN lastDestination, falling back to Default rather than poisoning the core control with NaN.
            if (cont.lastDestination.IsNaN())
            {
                DebugTAC_AI.Exception("SetCoreControl - cont.lastDestination was NaN; falling back to Default");
                ControlCore = EControlCoreSet.Default;
                return;
            }
            ControlCore = cont;
        }

        /// <summary> Do we steer to target destination? </summary>
        internal bool DoSteerCore => ControlCore.DriveDir > EDriveFacing.Neutral;

        /// <summary> Drive AWAY from target </summary>
        internal bool AdviseAwayCore => ControlCore.DriveDest == EDriveDest.FromLastDestination;

        //Finals
        /// <summary> Leave at 0 to disable automatic spacing </summary>
        public float AutoSpacing = 0;              // Minimum radial spacing distance from destination
        public float DriveVar { get; set; } = 0; // Forwards drive (-1, 1)
        public float GetDrive => MovementController.GetDrive;

        public AIThrottleState ThrottleState { get; set; } = AIThrottleState.FullSpeed;
        private bool _wantsToFight = false;
        public bool WantsToFight
        {
            get => _wantsToFight;
            set
            {
                if (KickStart.DoLogOwnership && value != _wantsToFight)
                    DebugTAC_AI.LogOwnership("WantsToFight", _wantsToFight, value);
                _wantsToFight = value;
            }
        }

        private readonly System.Collections.Generic.Queue<string> stateHistoryRing
            = new System.Collections.Generic.Queue<string>(StateHistoryCapacity);
        private const int StateHistoryCapacity = 32;
        internal void AppendHistory(string evt)
        {
            if (stateHistoryRing.Count >= StateHistoryCapacity)
                stateHistoryRing.Dequeue();
            stateHistoryRing.Enqueue("frame=" + Time.frameCount + " " + evt);
        }
        internal string GetStateHistorySnapshot()
        {
            if (stateHistoryRing.Count == 0) return "(empty)";
            return string.Join("\n  ", stateHistoryRing.ToArray());
        }
        public bool AvoidStuff { get; internal set; } = true;            // Try avoiding allies and obsticles

        internal AIAnchorState AnchorState = AIAnchorState.None;
        internal bool AnchorStateAIInsure = false;

        public bool FIRE_ALL { get; internal set; } = false;   // hold down tech's spacebar
        internal bool FullBoost = false;            // hold down boost button
        internal bool LightBoost = false;           // moderated booster pressing
        internal bool FirePROPS = false;            // hold down prop button
        internal bool ForceSetBeam = false;         // activate build beam
        public bool CollectedTarget = false;      // this Tech's storage objective status (resources, blocks, energy)
        public bool Retreat { get; internal set; } = false;              // ignore enemy position and follow intended destination (but still return fire)

        public bool Avoiding { get; internal set; } = false;             // We are currently avoiding something
        public bool IsTryingToUnjam { get; internal set; } = false;      // Is this tech unjamming?
        public bool PendingHeightCheck
        {// Queue a driving depth check for a naval tech - or any tech that really needs this lol

            get => _LowestPointOnTech == -1;
            set
            {
                if (value)
                    _LowestPointOnTech = -1;
                else if (_LowestPointOnTech == -1)
                    _LowestPointOnTech = 0;
            }
        }
        public float LowestPointOnTech
        {// The lowest point in relation to the tech's block-based center
            get
            {
                if (_LowestPointOnTech == -1)
                    GetLowestPointOnTech();
                return _LowestPointOnTech;
            }
            set => _LowestPointOnTech = value;
        }
        private float _LowestPointOnTech = 0;       // the lowest point in relation to the tech's block-based center
        internal bool BoltsFired = false;

        /// <summary>
        /// ONLY SET EXTERNALLY BY NETWORKING
        /// </summary>
        public bool isRTSControlled { get; internal set; } = false;
        public bool RTSControlled
        {
            get { return isRTSControlled; }
            set
            {
                if (isRTSControlled != value)
                {
                    if (ManNetwork.IsNetworked)
                        NetworkHandler.TryBroadcastRTSControl(tank.netTech.netId.Value, value);
                    isRTSControlled = value;
                    foreach (ModuleAIExtension AIEx in AIList)
                    {
                        AIEx.RTSActive = isRTSControlled;
                    }
                }
            }
        }
        public bool IsGoingToPositionalRTSDest => RTSDestInternal != RTSDisabled;
        public static IntVector3 RTSDisabled => AIGlobals.RTSDisabled;
        public ManWorldRTS.CommandLink RTSCommand = default;
        public IntVector3 RTSDestination
        {
            get
            {
                if (RTSDestInternal == RTSDisabled)
                {
                    if (lastEnemyGet != null)
                        return new IntVector3(lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    else if (Obst != null)
                        return new IntVector3(Obst.position + Vector3.up);
                    return new IntVector3(tank.boundsCentreWorldNoCheck);
                }
                return new IntVector3(RTSDestInternal);
            }
            set
            {
                // REVISED: RTSDestInternal is now computed BEFORE the network broadcast (was after), so the air/ground-offset-corrected value is what gets broadcast and saved.
                if (value == RTSDisabled)
                    RTSDestInternal = RTSDisabled;
                else if (DriverType == AIDriverType.Astronaut || DriverType == AIDriverType.Pilot)
                    RTSDestInternal = AIEPathing.OffsetFromGroundA(new IntVector3(value), this, AIGlobals.GroundOffsetRTSAir);
                else
                    RTSDestInternal = new IntVector3(value);

                if (ManNetwork.IsNetworked)
                {
                    try
                    {
                        if (tank.netTech)
                            NetworkHandler.TryBroadcastRTSCommand(tank.netTech.netId.Value, RTSDestInternal);
                    }
                    catch (Exception e)
                    {
                        DebugTAC_AI.LogWarnPlayerOnce("RTSDestination Server update Critical error", e);
                    }
                }

                foreach (ModuleAIExtension AIEx in AIList)
                {
                    AIEx.SaveRTS(this, RTSDestInternal);
                }
            }
        }

        private IntVector3 RTSDestInternal = RTSDisabled;

        public Vector3 DriveTargetLocation
        {
            get
            {
                if (RTSControlled && IsGoingToPositionalRTSDest)
                    return RTSDestination;
                else
                    return MovementController.GetDestination();
            }
        }

        internal void DirectRTSDest(Vector3 Pos)
        {
            // REVISED: a MultiTech member now forwards the RTS destination to its host tech's helper instead of setting its own.
            if (IsMultiTech && theHostTech != null && theHostTech.tank != null)
            {
                theHostTech.tank.GetHelperInsured().DirectRTSDest(Pos);
                return;
            }
            RTSDestInternal = Pos;
            foreach (ModuleAIExtension AIEx in AIList)
            {
                AIEx.SaveRTS(this, RTSDestInternal);
            }
        }

        public static bool OverrideAndFlyAway(TankAIHelper helper, ExtControlStatus control)
        {
            if (control == ExtControlStatus.MaintainersAndDirectors)
            {
                if (helper.DriverType != AIDriverType.Astronaut)
                {
                    var enemy = helper.GetComponent<EnemyMind>();
                    if (enemy)
                        enemy.EvilCommander = EnemyHandling.Starship;
                    helper.SetDriverType(AIDriverType.Astronaut);
                }
                helper.Unanchor();
                helper.MaxBoost();
                return false;
            }
            return true;
        }
        public enum ExtControlStatus
        {
            Operators,
            MaintainersAndDirectors,
            Recycle,
        }
        /// <summary>
        /// Force the tech to be controlled by external means.
        /// Returning true lets the AI in this mod OVERRIDE what you put in!.
        /// </summary>
        public Func<TankAIHelper, ExtControlStatus, bool> AIControlOverride = null;
        public bool PlayerAllowAutoAnchoring = false;   // Allow auto-anchor
        /// <summary> Set the AI back to Escort next update REVISED: flags that an anchor/unanchor changed the Tech so RunPostOps calls WakeAIForChange next update. </summary>
        public bool ExpectAITampering = false;


        // ----------------------------  AI Cores  ----------------------------
        public IMovementAIController MovementController;
        public AIEAutoPather autoPather => (MovementController is AIControllerDefault def) ? def.Pathfinder : null;

        private int delayedSubscribeRetries = 0;
        private const int MaxDelayedSubscribeRetries = 5;

        // ----------------------------  Awareness Subscriptions  ----------------------------
        public TankAIHelper Subscribe()
        {
            if (tank != null)
            {
                DebugTAC_AI.Assert("Game attempted to fire Subscribe for TankAIHelper twice.");
                return this;
            }
            tank = GetComponent<Tank>();
            // REVISED: Subscribe now bails (and self-destructs) if there is no Tank component, instead of NRE-ing later; AILimitSettings construction also moved after this guard.
            if (tank == null)
            {
                DebugTAC_AI.LogError(KickStart.ModID + ": TankAIHelper.Subscribe - attached to GameObject '"
                    + (gameObject ? gameObject.name : "<destroyed>") + "' without a Tank component. Destroying orphan helper.");
                Destroy(this);
                return null;
            }
            Vector3 _ = tank.boundsCentreWorld;
            AILimitSettings = new AISettingsLimit(this);
            AIList = new List<ModuleAIExtension>();
            ManWorldTreadmill.inst.AddListener(this);
            tank.DamageEvent.Subscribe(OnHit);
            if (DriverType == AIDriverType.AutoSet)
                DriverType = AIECore.HandlingDetermine(tank, this);
            SetupDefaultMovementAIController();
            RequestMovementControllerSwap(MovementSwapReason.Subscribe);
            AIECore.AddHelper(this);
            ResetAISettings();
            Invoke(nameof(DelayedSubscribe), AIGlobals.AISubscribeDelay);
            return this;
        }
        public void DelayedSubscribe()
        {
            // REVISED: now retries (up to MaxDelayedSubscribeRetries) when the blockman is not yet ready instead of running once and swallowing all exceptions; dirtyAI/dirtyExtents set in a finally so they always fire.
            if (this == null || tank == null || tank.blockman == null)
            {
                if (++delayedSubscribeRetries <= MaxDelayedSubscribeRetries && tank != null && enabled)
                {
                    Invoke(nameof(DelayedSubscribe), AIGlobals.AISubscribeDelay);
                    return;
                }
                DebugTAC_AI.LogWarnPlayerOncePerKey("DelayedSubscribe.abort:" + (tank ? tank.name : "<recycled>"),
                    "DelayedSubscribe aborted: tank recycled or blockman never ready", null);
                delayedSubscribeRetries = 0;
                return;
            }
            try
            {
                lastTechExtents = (tank.blockBounds.size.magnitude / 2) + 2;
                if (lastTechExtents < 1)
                {
                    Debug.LogError("lastTechExtents is below 1: " + lastTechExtents);
                    lastTechExtents = 1;
                }
                maxBlockCount = tank.blockman.blockCount;
                if (DriverType == AIDriverType.AutoSet)
                    ExecuteAutoSetNoCalibrate();
                else
                    SetDriverType(DriverType);
                delayedSubscribeRetries = 0;
            }
            catch (Exception e) when (e is NullReferenceException || e is MissingReferenceException)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey("DelayedSubscribe.partial:" + tank.name,
                    "DelayedSubscribe partial init for " + tank.name, e);
            }
            finally
            {
                dirtyAI = AIDirtyState.Dirty;
                dirtyExtents = true;
            }
        }

        public void ResetAISettings()
        {
            AILimitSettings.Recalibrate();
            AISetSettings = new AISettingsSet(AILimitSettings);
        }
        private void OnBlockAttached(TankBlock newBlock, Tank tank)
        {
            EstTopSped = 1;
            PendingHeightCheck = true;
            dirtyExtents = true;
            // REVISED: block attach now invalidates WeaponAimType (re-detect aim type) and, for enemies, forces DirtyAndReboot (full AI regen) rather than a plain Dirty; player techs flag PendingPlayerRecompose for driver re-evaluation.
            WeaponAimType = AIWeaponType.Unknown;
            dirtyAI = AIAlign == AIAlignment.NonPlayer ? AIDirtyState.DirtyAndReboot : AIDirtyState.Dirty;
            if (AIAlign == AIAlignment.Player)
            {
                PendingPlayerRecompose = true;
                try
                {
                    if (!tank.FirstUpdateAfterSpawn && !PendingDamageCheck && TechMemor)
                    {
                        TechMemor.SaveTech();
                    }
                }
                catch (Exception eMemSave) { DebugTAC_AI.LogWarnPlayerOnce("[TAC_AI:catch:Repair] TechMemor.SaveTech on attach", eMemSave); }
            }
            else if (AIAlign == AIAlignment.NonPlayer)
            {
                if (newBlock.GetComponent<ModulePacemaker>())
                    tank.Holders.SetHeartbeatSpeed(TechHolders.HeartbeatSpeed.Fast);
            }
        }
        private void OnBlockDetaching(TankBlock removedBlock, Tank tank)
        {
            EstTopSped = 1;
            recentSpeed = 1;
            PendingHeightCheck = true;
            PendingDamageCheck = true;
            dirtyExtents = true;
            // REVISED: same as attach - detach now resets WeaponAimType, reboots enemy AI (DirtyAndReboot), and flags player recompose.
            WeaponAimType = AIWeaponType.Unknown;
            if (AIAlign == AIAlignment.NonPlayer)
                dirtyAI = AIDirtyState.DirtyAndReboot;
            if (AIAlign == AIAlignment.Player)
            {
                PendingPlayerRecompose = true;
                try
                {
                    removedBlock.visible.EnableOutlineGlow(false, cakeslice.Outline.OutlineEnableReason.ScriptHighlight);
                }
                catch { }
                dirtyAI = AIDirtyState.Dirty;
            }
        }
        internal void Recycled()
        {
            // REVISED: recycle now cancels any pending DelayedSubscribe and unsubscribes the treadmill + damage-event listeners, preventing orphaned callbacks on a recycled helper.
            CancelInvoke(nameof(DelayedSubscribe));
            delayedSubscribeRetries = 0;
            beEvilRegenAttempted = false;
            try { if (ManWorldTreadmill.inst != null) ManWorldTreadmill.inst.RemoveListener(this); } catch { }
            try { if (tank != null) tank.DamageEvent.Unsubscribe(OnHit); } catch { }
            DropBlock();
            WantsToFight = false;            ResetToNormalAimState();
            FinishedRepairEvent.EnsureNoSubscribers();
            maxBlockCount = 0;
            DamageThreshold = 0;
            PlayerAllowAutoAnchoring = false;
            isRTSControlled = false;
            DriverType = AIDriverType.AutoSet;
            AttackMode = EAttackMode.AutoSet;
            RequestMovementControllerSwap(MovementSwapReason.Recycled);
            DediAI = AIType.Escort;
            NextFindTargetTime = 0;
            RemoveBookmarkBuilder();
            if (TechMemor.IsNotNull())
            {
                TechMemor.Remove();
                TechMemor = null;
            }
            ResetOnSwitchAlignments(null);
            ResetAISettings();
            enabled = false;
        }

        // REVISED: new lifecycle hook - re-enabling the helper marks the ControlOperator dirty so its age/staleness is reset and it is not treated as stale on the first tick back.
        private void OnEnable() => MarkOperatorDirty();

        public void SetRTSState(bool RTSEnabled)
        {
            RTSControlled = RTSEnabled;
            foreach (ModuleAIExtension AIEx in AIList)
            {
                if (AIEx)
                    AIEx.RTSActive = isRTSControlled;
                else
                    DebugTAC_AI.Log(KickStart.ModID + ": NULL ModuleAIExtension IN " + tank.name);
            }
        }
        public void OnMoveWorldOrigin(IntVector3 move)
        {
            if (RTSDestInternal != RTSDisabled)
                RTSDestInternal += move;
            ControlOperator.SetLastDest(ControlOperator.lastDestination + move);
            MarkOperatorDirty();

            if (MovementController != null)
                MovementController.OnMoveWorldOrigin(move);
        }
        public void TrySetAITypeRemote(NetPlayer sender, AIType type, AIDriverType driver)
        {
            if (ManNetwork.IsNetworked)
            {
                if (sender == null)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Host changed AI");
                }
                if (sender.CurTech?.Team == tank.Team)
                {
                    if (type != AIType.Null)
                    {
                        OnSwitchAI(true);
                        DediAI = type;
                    }
                    if (driver != AIDriverType.Null)
                    {
                        OnSwitchAI(false);
                        if (DriverType == AIDriverType.Stationary && driver != AIDriverType.Stationary)
                        {
                            Unanchor();
                            PlayerAllowAutoAnchoring = true;
                        }
                        else
                        {
                            TryInsureManualAnchor();
                            PlayerAllowAutoAnchoring = false;
                        }
                        // REVISED: now goes through SetDriverType (resolves AutoSet, requests a swap) instead of assigning DriverType directly.
                        SetDriverType(driver);
                    }
                    RequestMovementControllerSwap(MovementSwapReason.RemoteAIType);

                }
                else
                    DebugTAC_AI.Log(KickStart.ModID + ": TrySetAITypeRemote - Invalid request received - player tried to change AI of Tech that wasn't theirs");
            }
            else
                DebugTAC_AI.Log(KickStart.ModID + ": TrySetAITypeRemote - Invalid request received - Tried to change AI type when not connected to a server!? \n  The UI handles this automatically!!!\n" + StackTraceUtility.ExtractStackTrace());
        }

        public bool CanDoBlockReplacement()
        {
            foreach (ModuleAIExtension AIEx in AIList)
            {
                if (AIEx.SelfRepairAI)
                    return true;
            }
            return false;
        }

        private void ReValidateAI()
        {
            AutoAnchor = false;
            SecondAvoidence = false;
            ChaseThreat = true;
            ActionPause = 0;

            if (tank.PlayerFocused)
            {
                AIWorkingModes = AIEnabledModes.All;
            }
            else
            {
                AIWorkingModes = AIEnabledModes.None;
            }

            AIList.Clear();
            // REVISED: builds AIList by iterating ModuleAIExtension components directly, instead of iterating ModuleAIBot blocks and fetching their AIExtension sibling.
            foreach (ModuleAIExtension AIE in tank.blockman.IterateBlockComponents<ModuleAIExtension>())
            {
                if (AIE.IsNotNull())
                    AIList.Add(AIE);
            }
            DebugTAC_AI.Info(KickStart.ModID + ": AI list for Tech " + tank.name + " has " + AIList.Count() + " entries");
            foreach (ModuleAIExtension AIEx in AIList)
            {
                if (AIEx.Aegis)
                    isAegisAvail = true;
                if (AIEx.Assault)
                    isAssassinAvail = true;

                if (AIEx.Prospector)
                    isProspectorAvail = true;
                if (AIEx.Scrapper)
                    isScrapperAvail = true;
                if (AIEx.Energizer)
                    isEnergizerAvail = true;

                if (AIEx.Aviator)
                    isAviatorAvail = true;
                if (AIEx.Buccaneer)
                    isBuccaneerAvail = true;
                if (AIEx.Astrotech)
                    isAstrotechAvail = true;

                if (AIEx.AutoAnchor)
                    AutoAnchor = true;
                if (AIEx.AdvAvoidence)
                    SecondAvoidence = true;

                if (AIEx.RTSActive)
                {
                    SetRTSState(true);
                    RTSDestination = AIEx.GetRTSScenePos();
                }
            }
            AILimitSettings.Recalibrate();
            switch (DediAI)
            {
                case AIType.Aegis:
                    if (isAegisAvail) break;
                    DediAI = AIType.Escort;
                    break;
                case AIType.Assault:
                    if (isAssassinAvail) break;
                    DediAI = AIType.Escort;
                    break;
                case AIType.Prospector:
                    if (isProspectorAvail) break;
                    DediAI = AIType.Escort;
                    break;
                case AIType.Scrapper:
                    if (isScrapperAvail) break;
                    DediAI = AIType.Escort;
                    break;
                case AIType.Energizer:
                    if (isEnergizerAvail) break;
                    DediAI = AIType.Escort;
                    break;
                case AIType.Aviator:
                    if (isAviatorAvail) break;
                    DriverType = AIDriverType.Tank;
                    DediAI = AIType.Escort;
                    break;
                case AIType.Buccaneer:
                    if (isBuccaneerAvail) break;
                    DriverType = AIDriverType.Tank;
                    DediAI = AIType.Escort;
                    break;
                case AIType.Astrotech:
                    if (isAstrotechAvail) break;
                    DriverType = AIDriverType.Tank;
                    DediAI = AIType.Escort;
                    break;
            }

            if (DriverType == AIDriverType.AutoSet)
            {
                ExecuteAutoSetNoCalibrate();
            }
            else if (AIECore.ShouldBeStationary(tank, this))
                DriverType = AIDriverType.Stationary;

            RequestMovementControllerSwap(MovementSwapReason.ReValidate);

            if (AttackMode == EAttackMode.AutoSet)
                AttackMode = EWeapSetup.GetAttackStrat(tank, this);
        }
        public void RefreshAI()
        {
            AvoidStuff = true;
            UsingAirControls = false;
            RunState = AIRunState.Advanced;
            MultiTechsAffiliated.Clear();

            ReValidateAI();

            ProcessControl(Vector3.zero, Vector3.zero, Vector3.zero, false, false);
            tank.control.SetBeamControlState(false);
            tank.control.FireControl = false;

            if (CanDoBlockReplacement() || AIEBases.CheckIfTechNeedsToBeBuilt(this))
            {
                InsureTechMemor("RefreshAI", false);
            }
            else
            {
                if (TechMemor.IsNotNull())
                {
                    TechMemor.Remove();
                    TechMemor = null;
                }
            }
            try
            {
                tank.AttachEvent.Unsubscribe(OnBlockAttached);
                tank.DetachEvent.Unsubscribe(OnBlockDetaching);
            }
            catch (Exception eEvtUnsub) { DebugTAC_AI.LogWarnPlayerOnce("[TAC_AI:catch:Player] block-event unsubscribe", eEvtUnsub); }

            try
            {
                tank.AttachEvent.Subscribe(OnBlockAttached);
                tank.DetachEvent.Subscribe(OnBlockDetaching);
            }
            catch (Exception eEvtSub) { DebugTAC_AI.LogWarnPlayerOnce("[TAC_AI:catch:Player] block-event subscribe", eEvtSub); }
            AIEBases.SetupTechAutoConstruction(this);
        }
        // REVISED: new helpers. A neutral tech running a vanilla Flee/Specific/FacePlayer tree is left to vanilla: RunState is dropped to Default and alignment forced Static so this mod stops driving it.
        private static bool NeutralTechHasOwnVanillaAI(Tank t)
        {
            if (t == null || t.AI == null) return false;
            if (!t.AI.TryGetCurrentAIType(out AITreeType.AITypes curTree)) return false;
            return curTree == AITreeType.AITypes.Flee
                || curTree == AITreeType.AITypes.Specific
                || curTree == AITreeType.AITypes.FacePlayer;
        }
        private void HandOffToVanillaForNeutral(Tank t)
        {
            if (RunState != AIRunState.Default)
            {
                RunState = AIRunState.Default;
                AIAlign = AIAlignment.Static;
                t.AI.TryGetCurrentAIType(out AITreeType.AITypes curTree);
                DebugTAC_AI.Log(KickStart.ModID + ": Neutral Tech " + t.name
                    + " has vanilla AI tree " + curTree + " — handing control to vanilla.");
            }
        }

        public void ResetOnSwitchAlignments(Tank unused)
        {
            DebugTAC_AI.Assert(MovementController == null,
                "ResetOnSwitchAlignments: MovementController null on " + (tank.IsNotNull() ? tank.name : "<recycled>")
                + " - expected during teardown, otherwise an alignment switch ran before Subscribe set it up.");
            maxBlockCount = tank.blockman.blockCount;
            WantsToFight = false;            lastAIType = AITreeType.AITypes.Idle;
            lastAITypeResolved = true;
            AttackMode = EAttackMode.AutoSet;
            dirtyExtents = true;
            dirtyAI = AIDirtyState.Dirty;
            PlayerAllowAutoAnchoring = !tank.IsAnchored;
            ExpectAITampering = false;
            GroundOffsetHeight = AIGlobals.GroundOffsetGeneralAir;
            Provoked = 0;
            ActionPause = 0;
            KeepEnemyFocus = false;
            // REVISED: alignment reset now also clears the LOS-lost grace timer and the per-source damage accumulator (and uses ReleaseTarget/SettleDown below) so combat-focus state does not survive an alignment switch.
            _losLostGraceTimer = 0f;
            ResetDamageAccumulator();
            MultiTechsAffiliated.Clear();

            AIAlign = AIAlignment.Static;
            RunState = AIRunState.Advanced;
            AnchorState = AIAnchorState.None;
            AnchorStateAIInsure = false;
            WeaponAimType = AIWeaponType.Unknown;
            BlockedLineOfSight = false;
            PendingDamageCheck = true;
            ActiveAimState = 0;
            RepairStepperClock = 0;
            AvoidStuff = true;
            EstTopSped = 1;
            recentSpeed = 1;
            anchorAttempts = 0;
            DelayedAnchorClock = 0;
            foundBase = false;
            foundGoal = false;
            lastBasePos = null;
            lastPlayer = null;
            ReleaseTarget();
            lastLockOnTarget = null;
            lastCloseAlly = null;
            theBase = null;
            theResource = null;
            SettleDown();
            DropBlock();
            isRTSControlled = false;
            RTSDestInternal = RTSDisabled;
            lastTargetGatherTime = 0;
            ChaseThreat = true;
            tank.visible.EnableOutlineGlow(false, cakeslice.Outline.OutlineEnableReason.ScriptHighlight);
            World.ManWorldRTS.ReleaseControl(this);
            var Funds = tank.gameObject.GetComponent<RLoadedBases.EnemyBaseFunder>();
            if (Funds.IsNotNull())
                Funds.OnRecycle(tank);
            var Mem = tank.gameObject.GetComponent<AIERepair.DesignMemory>();
            if (Mem.IsNotNull() && !BookmarkBuilder.Exists(tank))
            {
                Mem.Remove();
                TechMemor = null;
            }
            var Mind = tank.gameObject.GetComponent<EnemyMind>();
            if (Mind.IsNotNull())
                Mind.SetForRemoval();
            var Select = tank.gameObject.GetComponent<SelectHalo>();
            if (Select.IsNotNull())
                Select.Remove();

            if (DriverType == AIDriverType.AutoSet)
                DriverType = AIECore.HandlingDetermine(tank, this);
            RequestMovementControllerSwap(MovementSwapReason.AlignmentReset);

            ProcessControl(Vector3.zero, Vector3.zero, Vector3.zero, false, false);
            tank.control.SetBeamControlState(false);
            tank.control.FireControl = false;

            ResetToNormalAimState();


        }

        // REVISED: single generic swap helper replacing the duplicated null-out/Recycle/GetOrAddComponent blocks in every Recal* path; reuses the existing controller if it is already the right type, else recycles the old one after building the new.
        private T SwapMovementController<T>(EnemyMind mind) where T : Component, IMovementAIController
        {
            if (MovementController is T existing)
            {
                existing.Initiate(tank, this, mind);
                return existing;
            }
            IMovementAIController previous = MovementController;
            T built = gameObject.AddComponent<T>();
            built.Initiate(tank, this, mind);
            MovementController = built;
            if (previous != null)
                previous.Recycle();
            return built;
        }

        private void SetupDefaultMovementAIController()
        {
            UsingAirControls = false;
            SwapMovementController<AIControllerDefault>(null);
            LogMovementControllerSwapIfChanged();
        }

        // REVISED: controller selection now keyed on MovementDispatch.ContainerForEnemy(EvilCommander) (Static/Air/Default) instead of explicit EnemyHandling checks (Stationary, Chopper/Airplane).
        // REVISED: a flying enemy whose air controller reports Grounded (no longer flightworthy) is demoted via BlockSetEnemyHandling/Wheeled rather than being kept airborne; null EnemyMind now self-heals to Default instead of throwing.
        private bool RecalMoveAIControllerNPT(EnemyMind enemy)
        {
            if (enemy.IsNotNull())
            {
                if ((MovementDispatch.ContainerForEnemy(enemy.EvilCommander) == MovementContainerKind.Static || enemy.StartedAnchored) && AnchorState != AIAnchorState.Unanchor)
                {
                    DriverType = AIDriverType.Stationary;
                    SwapMovementController<AIControllerStatic>(enemy);
                    return false;
                }
                if (MovementDispatch.ContainerForEnemy(enemy.EvilCommander) == MovementContainerKind.Air)
                {
                    if (MovementController is AIControllerAir existingAir && existingAir.Grounded)
                    {
                        var oldClass = enemy.EvilCommander;
                        Enemy.RCore.BlockSetEnemyHandling(tank, enemy, false);
                        if (MovementDispatch.ContainerForEnemy(enemy.EvilCommander) == MovementContainerKind.Air)
                            enemy.EvilCommander = EnemyHandling.Wheeled;
                        DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name + " demoting from "
                            + oldClass + " → " + enemy.EvilCommander + " (Grounded — no longer flightworthy)");
                    }
                    else
                    {
                        SwapMovementController<AIControllerAir>(enemy);
                        UsingAirControls = true;
                        return false;
                    }
                }
                return true;
            }
            else
            {
                DebugTAC_AI.LogError(KickStart.ModID + ": RecalMoveAIControllerNPT for " + tank.name
                    + " reached with null EnemyMind despite caller guard — falling back to default.");
                return true;
            }
        }
        // REVISED: keyed on MovementDispatch.ContainerForPlayer(DriverType) instead of explicit Stationary/Pilot checks; a grounded (non-flightworthy) Pilot tech is demoted to Tank rather than kept on air controls.
        private bool RecalMoveAIControllerPlayer()
        {
            if (MovementDispatch.ContainerForPlayer(DriverType) == MovementContainerKind.Static && AnchorState != AIAnchorState.Unanchor)
            {
                SwapMovementController<AIControllerStatic>(null);
                return false;
            }
            else if (MovementDispatch.ContainerForPlayer(DriverType) == MovementContainerKind.Air)
            {
                if (MovementController is AIControllerAir existingAir && existingAir.Grounded)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name + " (player) demoting from Pilot (Grounded — no longer flightworthy)");
                    DriverType = AIDriverType.Tank;
                }
                else
                {
                    SwapMovementController<AIControllerAir>(null);
                    UsingAirControls = true;
                    return false;
                }
            }
            return true;
        }
        private string lastLoggedMovementControllerType = null;
        private void LogMovementControllerSwapIfChanged()
        {
            string current = MovementController?.GetType().Name ?? "<null>";
            if (current != lastLoggedMovementControllerType)
            {
                DebugTAC_AI.LogTagged("Movement", "Tech " + DebugTAC_AI.VisibleName(tank) + " core swap "
                    + (lastLoggedMovementControllerType ?? "<init>") + " → " + current
                    + " (DriverType=" + DriverType + ", AIAlign=" + AIAlign + ", requestedBy=" + lastSwapRequest + ")");
                AppendHistory("CoreSwap " + (lastLoggedMovementControllerType ?? "<init>") + "→" + current);
                lastLoggedMovementControllerType = current;
            }
        }
        private void RecalibrateMovementAIController()
        {
            try
            {
                UsingAirControls = false;
                var enemy = gameObject.GetComponent<EnemyMind>();
                // REVISED: a NonPlayer tech missing its EnemyMind now self-heals (client demotes to Static+dirty; host regenerates the enemy AI) instead of the controller swap throwing on the missing mind.
                if (AIAlign == AIAlignment.NonPlayer && enemy.IsNull())
                {
                    DebugTAC_AI.LogWarnPlayerOnce(KickStart.ModID +
                        ": Recalibrate found NonPlayer alignment with no EnemyMind on " +
                        tank.name + " - invariant violation, self-healing.", null);
                    if (ManNetwork.IsNetworked && !ManNetwork.IsHost)
                    {
                        AIAlign = AIAlignment.Static;
                        dirtyAI = AIDirtyState.Dirty;
                    }
                    else
                    {
                        Enemy.RCore.GenerateEnemyAI(this, tank);
                        enemy = gameObject.GetComponent<EnemyMind>();
                    }
                }
                if (AIAlign == AIAlignment.NonPlayer)
                {
                    if (!RecalMoveAIControllerNPT(enemy))
                        return;
                    enemy = gameObject.GetComponent<EnemyMind>();
                }
                else
                {
                    if (!RecalMoveAIControllerPlayer())
                        return;
                }
                SwapMovementController<AIControllerDefault>(enemy);
                return;
            }
            finally
            {
                MovementAIControllerDirty = false;
                LogMovementControllerSwapIfChanged();
            }
        }

        public void ExecuteAutoSet()
        {
            ExecuteAutoSetNoCalibrate();
            RequestMovementControllerSwap(MovementSwapReason.ExecuteAutoSet);
        }
        public void ExecuteAutoSetNoCalibrate()
        {
            DriverType = AIECore.HandlingDetermine(tank, this);
            switch (DriverType)
            {
                case AIDriverType.Astronaut:
                    if (!isAstrotechAvail)
                        DriverType = AIDriverType.Tank;
                    break;
                case AIDriverType.Pilot:
                    if (!isAviatorAvail)
                        DriverType = AIDriverType.Tank;
                    break;
                case AIDriverType.Sailor:
                    if (!isBuccaneerAvail)
                        DriverType = AIDriverType.Tank;
                    break;
                case AIDriverType.AutoSet:
                    DriverType = AIDriverType.Tank;
                    break;
                case AIDriverType.Tank:
                case AIDriverType.Stationary:
                    break;
                default:
                    DebugTAC_AI.LogError(KickStart.ModID + ": Encountered illegal AIDriverType on Allied AI Driver HandlingDetermine!");
                    break;
            }
            DebugTAC_AI.Log(KickStart.ModID + ": ExecuteAutoSetNoCalibrate() " + tank.name + " guessing driver is " + DriverType);
        }

        // REVISED: new player-recompose pass. When blocks were added/removed (PendingPlayerRecompose), re-derives the driver type; only requests a controller swap if the driver actually changed (or it is still on air controls).
        private bool PendingPlayerRecompose = false;
        private void ReevaluatePlayerMovementIfNeeded()
        {
            if (!PendingPlayerRecompose)
                return;
            PendingPlayerRecompose = false;
            if (AIAlign != AIAlignment.Player)
                return;
            AIDriverType before = DriverType;
            ExecuteAutoSetNoCalibrate();
            if (DriverType != before || MovementController is AIControllerAir)
                RequestMovementControllerSwap(MovementSwapReason.PlayerRecompose);
        }

        public static ExtUsageHint.UsageHint UnitAttacked =
            new ExtUsageHint.UsageHint(KickStart.ModID, "TankAIHelper.UnitBesieged", new LocExtStringMod(
            new Dictionary<LocalisationEnums.Languages, string>()
            {
                { LocalisationEnums.Languages.US_English, "Your team " + AltUI.EnemyString("is under attack")},
                { LocalisationEnums.Languages.Japanese, "あなたのチーム" + AltUI.EnemyString("が攻撃されています！")},
            }), 3, true);
        public static LocExtStringMod LOC_PlayerAttacked = new LocExtStringMod(
            new Dictionary<LocalisationEnums.Languages, string>()
            {
                { LocalisationEnums.Languages.US_English, "You are under attack"},
                { LocalisationEnums.Languages.Japanese, "攻撃を受けている！"},
            });

        public static LocExtStringMod LOC_PlayerBaseAttacked = new LocExtStringMod(
            new Dictionary<LocalisationEnums.Languages, string>()
            {
                { LocalisationEnums.Languages.US_English, "Base is under attack"},
                { LocalisationEnums.Languages.Japanese, "基地が攻撃を受けている！"},
            });

        // REVISED: OnHit now also trips on CUMULATIVE damage from one source (AccumulateAndCheckThreat / decaying per-source buckets), not just a single hit over DamageAlertThreshold; small sustained chip damage now provokes.
        // REVISED: Provoked/FIRE_ALL/cache-invalidate are set first regardless of source liveness, then SetPursuit(force:true) is attempted only if the source is still alive (was nested entirely inside a SetPursuit gate before).
        internal void OnHit(ManDamage.DamageInfo dingus)
        {
            bool tripped = dingus.Damage > AIGlobals.DamageAlertThreshold;
            Tank src = dingus.SourceTank;
            bool srcAlive = (bool)src;
            if (!tripped && srcAlive)
            {
                if (AccumulateAndCheckThreat(src, dingus.Damage))
                    tripped = true;
            }
            if (!tripped) return;

            Provoked = AIGlobals.ProvokeTime;
            InvalidateTargetCache();
            FIRE_ALL = true;
            if (ManWorldRTS.PlayerIsInRTS && tank.Team == ManPlayer.inst.PlayerTeam)
            {
                if (tank.PlayerFocused)
                {
                    PlayerRTSUI.RTSDamageWarnings(1.5f, 0.75f);
                    UIHelpersExt.BigF5broningBannerSP(LOC_PlayerAttacked.ToString(), false);
                }
                else if (tank.IsAnchored)
                {
                    PlayerRTSUI.RTSDamageWarnings(0.5f, 0.25f);
                    UIHelpersExt.BigF5broningBannerSP(LOC_PlayerBaseAttacked.ToString(), true);
                }
                else
                {
                    ManSFX.inst.PlayUISFX(ManSFX.UISfxType.RadarOn);
                    UnitAttacked.Show();
                }
            }

            if (srcAlive && SetPursuit(src.visible, force: true))
            {
                if (tank.IsAnchored)
                {
                    AIECore.RequestFocusFirePlayer(tank, lastEnemyGet, RequestSeverity.AllHandsOnDeck);
                }
                else
                {
                    switch (DediAI)
                    {
                        case AIType.Prospector:
                        case AIType.Scrapper:
                        case AIType.Energizer:
                            AIECore.RequestFocusFirePlayer(tank, lastEnemyGet, RequestSeverity.Warn);
                            break;
                        default:
                            AIECore.RequestFocusFirePlayer(tank, lastEnemyGet, RequestSeverity.ThinkMcFly);
                            break;
                    }
                }
            }
        }
        internal void OnSwitchAI(bool resetRTSstate)
        {
            DebugTAC_AI.Assert(tank == null, "OnSwitchAI: tank is null on helper " + name);
            DebugTAC_AI.Assert(!System.Enum.IsDefined(typeof(AIType), DediAI),
                "OnSwitchAI: DediAI out of range = " + (int)DediAI);
            AppendHistory("OnSwitchAI(resetRTS=" + resetRTSstate + ") from DediAI=" + DediAI + " Driver=" + DriverType);
            AvoidStuff = true;
            EstTopSped = 1;
            foundBase = false;
            foundGoal = false;
            lastBasePos = null;
            lastPlayer = null;
            lastCloseAlly = null;
            theBase = null;
            IsTryingToUnjam = false;
            ChaseThreat = true;
            ActionPause = 0;
            DropBlock();
            if (resetRTSstate)
            {
                isRTSControlled = false;
                foreach (ModuleAIExtension AIEx in AIList)
                {
                    AIEx.RTSActive = isRTSControlled;
                }
                tank.visible.EnableOutlineGlow(false, cakeslice.Outline.OutlineEnableReason.ScriptHighlight);
            }
            RequestMovementControllerSwap(MovementSwapReason.SwitchAI);

            // REVISED: switching AI now also fully settles movement, releases the target, clears WeaponDelayClock, and zeroes the MultiTech offset/mimic state so leftover MT state cannot bleed into the new role.
            SettleDown();
            ReleaseTarget();
            WantsToFight = false;
            WeaponDelayClock = 0;
            MTOffsetPos = Vector3.zero;
            MTOffsetRot = Vector3.forward;
            MTOffsetRotUp = Vector3.up;
            MTLockedToTechBeam = false;
            MTMimicHostAvail = false;
        }
        public void SetAIControl(AITreeType.AITypes type)
        {
            tank.AI.SetBehaviorType(type);
        }
        // REVISED: ForceAllAIsToEscort no longer hardcodes vanilla Escort; stationary/turret/anchored techs now map to vanilla Idle (Escort only for mobile roles) so anchored techs do not get a movement AI tree.
        private AITreeType.AITypes ChooseAppropriateVanillaAIType()
        {
            bool stationary = DediAI == AIType.MTStatic
                              || DediAI == AIType.MTTurret
                              || DriverType == AIDriverType.Stationary
                              || (tank != null && tank.IsAnchored);
            return stationary ? AITreeType.AITypes.Idle : AITreeType.AITypes.Escort;
        }

        public void ForceAllAIsToEscort(bool Do)
        {
            try
            {
                if (Do)
                {
                    AITreeType.AITypes chosen = ChooseAppropriateVanillaAIType();
                    DebugTAC_AI.FirstFire("VanillaMap." + DediAI + "->" + chosen,
                        "DediAI=" + DediAI + " (Driver=" + DriverType + ") → vanilla.AI=" + chosen);
                    if (ManNetwork.IsNetworked && tank.netTech.IsNotNull())
                    {
                        Singleton.Manager<ManNetwork>.inst.SendToServer(TTMsgType.SetAIMode, new SetAIModeMessage
                        {
                            m_AIAction = chosen
                        }, tank.netTech.netId);
                    }
                    else
                    {
                        SetAIControl(chosen);
                        lastAIType = chosen;
                    }
                    if (tank.AI.TryGetCurrentAIType(out AITreeType.AITypes type))
                        DebugTAC_AI.Info(KickStart.ModID + ": AI type is " + type.ToString());
                }
                else
                {
                    if (ManNetwork.IsNetworked && tank.netTech.IsNotNull())
                    {
                        Singleton.Manager<ManNetwork>.inst.SendToServer(TTMsgType.SetAIMode, new SetAIModeMessage
                        {
                            m_AIAction = AITreeType.AITypes.Idle
                        }, tank.netTech.netId);
                    }
                    else
                    {
                        SetAIControl(AITreeType.AITypes.Idle);
                        lastAIType = AITreeType.AITypes.Idle;
                        lastAITypeResolved = true;
                    }
                }
                dirtyAI = AIDirtyState.Dirty;
                RequestMovementControllerSwap(MovementSwapReason.ForceEscort);
            }
            catch (Exception e)
            {
                throw e;
            }
        }
        private AIType lastLoggedDediAI = AIType.Null;
        private AIDriverType lastLoggedDriverType = AIDriverType.AutoSet;
        public void WakeAIForChange(bool immedeateRebuildAlignment = false)
        {
            ForceAllAIsToEscort(true);
            RequestMovementControllerSwap(MovementSwapReason.WakeForChange);
            if (immedeateRebuildAlignment)
                ForceRebuildAlignment();

            if (DediAI != lastLoggedDediAI || DriverType != lastLoggedDriverType)
            {
                DebugTAC_AI.LogTagged("Mode", "Tech " + DebugTAC_AI.VisibleName(tank)
                    + " entered " + DediAI
                    + " (was " + lastLoggedDediAI + ", AIAlign=" + AIAlign
                    + ", Driver=" + DriverType + ")");
                AppendHistory("ModeEntry DediAI=" + DediAI + " Driver=" + DriverType + " AIAlign=" + AIAlign);
                lastLoggedDediAI = DediAI;
                lastLoggedDriverType = DriverType;
            }
        }

        internal string GetActionStatus(out bool cantDo)
        {
            cantDo = false;
            if (tank.IsPlayer)
            {
                if (!KickStart.AutopilotPlayer)
                    return AILOC.AutoDisabled;
            }
            else if (AIAlign != AIAlignment.NonPlayer)
            {
                if (!ActuallyWorks)
                    return AILOC.NoAI;
                else if (!SetToActive)
                {
                    if (AIAlign != AIAlignment.NonPlayer)
                        return AILOC.AutoDisabled;
                }
            }
            if (Retreat && !IsMultiTech)
            {
                if (tank.IsAnchored)
                    return AILOC.AIAnchoredDefRetreat;
                return AILOC.AIRetreat;
            }
            string output = AILOC.AIArrived;
            try
            {
                if (RTSControlled)
                {
                    GetActionOperatorsPositional(ref output, ref cantDo);
                }
                else
                {
                    if (AIAlign == AIAlignment.NonPlayer)
                    {
                        GetActionOperatorsNonPlayer(ref output, ref cantDo);
                    }
                    else
                    {
                        GetActionOperatorsAllied(ref output, ref cantDo);
                    }
                }
                if (AIGlobals.ShowDebugFeedBack)
                {
                    output =  "[" + DriverType + "]\n" + output + "\nDirect [" + ControlCore.DriveDir + "], Dest [" + ControlCore.DriveDest +
                        "]\nWeaponState [" + WeaponState +
                        "]\nMinRange [" + AutoSpacing.ToString("0.000") +
                        (ThrottleState == AIThrottleState.ForceSpeed ? ("]\nDrive[F] [" + DriveVar.ToString("0.000") + ", " +
                        GetDrive.ToString("0.000")) :
                        ("]\nDrive [" + GetDrive.ToString("0.000"))) +
                        "]\nThrottle [" + ThrottleState + "]";
                }
            }
            catch
            {
                output = AILOC.AIProcessing;
            }
            return output;
        }
        private void GetActionOperatorsPositional(ref string output, ref bool cantDo)
        {
            if (tank.IsAnchored)
            {
                if (IsAutoAnchored)
                {
                }
                else
                {
                    if (lastEnemyGet)
                        output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                        output = AILOC.AIStationary;
                }
                return;
            }
            switch (DriverType)
            {
                case AIDriverType.Astronaut:
                    if (lastEnemyGet)
                        output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                    {
                        if (WeaponState == AIWeaponState.Obsticle)
                            output = AILOC.Remove + AILOC.Obst;
                        else
                        {
                            switch (ControlOperator.DriveDest)
                            {
                                case EDriveDest.FromLastDestination:
                                    output = AILOC.Gen_MoveFrom + AILOC.Destination;
                                    break;
                                case EDriveDest.ToLastDestination:
                                    output = AILOC.Gen_MoveTo + AILOC.Destination;
                                    break;
                                case EDriveDest.ToBase:
                                    output = AILOC.Gen_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                    break;
                                case EDriveDest.ToMine:
                                    output = AILOC.Gen_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                                    break;
                                default:
                                    output = AILOC.AIArrived;
                                    break;
                            }
                        }
                    }
                    break;
                case AIDriverType.Pilot:
                    if (lastEnemyGet)
                    {
                        output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    }
                    else
                    {
                        if (MovementController is AIControllerAir air)
                        {
                            if (air.Grounded)
                            {
                                cantDo = true;
                                output = AILOC.Fly_Grounded;
                            }
                            else
                            {
                                if (WeaponState == AIWeaponState.Obsticle)
                                    output = AILOC.Fly_Crashed;
                                else
                                {
                                    switch (ControlOperator.DriveDest)
                                    {
                                        case EDriveDest.FromLastDestination:
                                            output = AILOC.Fly_MoveFrom + AILOC.Destination;
                                            break;
                                        case EDriveDest.ToLastDestination:
                                            output = AILOC.Fly_MoveTo + AILOC.Destination;
                                            break;
                                        case EDriveDest.ToBase:
                                            output = AILOC.Fly_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                            break;
                                        case EDriveDest.ToMine:
                                            output = AILOC.Fly_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                                            break;
                                        default:
                                            output = AILOC.AIArrived;
                                            break;
                                    }
                                }
                            }
                        }
                        else
                            output = "Unhandled error in switch";
                    }
                    AIControllerAir Air = MovementController as AIControllerAir;
                    if (Air.AICore is AirplaneAICore plane)
                    {
                        if (plane.PerformDiveAttack > 0)
                        {
                            switch (plane.PerformDiveAttack)
                            {
                                case 1:
                                    output += "\n" + AILOC.Fly_Dive + AILOC.FaceTowards + AILOC.Target;
                                    break;
                                case 2:
                                    output += "\n" + AILOC.Fly_Dive + AILOC.Fly_Dive2;
                                    break;
                                default:
                                    output += "\n" + AILOC.Fly_Dive + AILOC.Code + plane.PerformDiveAttack + "]";
                                    break;
                            }
                        }
                        if (plane.PerformUTurn > 0)
                        {
                            switch (plane.PerformUTurn)
                            {
                                case 1:
                                    output += "\n" + AILOC.Fly_UTurn + AILOC.FaceAwayFrom + AILOC.Target;
                                    break;
                                case 2:
                                    output += "\n" + AILOC.Fly_UTurn + AILOC.Fly_UTurn2;
                                    break;
                                case 3:
                                    output += "\n" + AILOC.Fly_UTurn + AILOC.FaceTowards + AILOC.Target;
                                    break;
                                default:
                                    output += "\n" + AILOC.Fly_UTurn + AILOC.Code + plane.PerformUTurn + "]";
                                    break;
                            }
                        }
                    }
                    break;
                case AIDriverType.Sailor:
                    if (lastEnemyGet)
                        output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                    {
                        if (WeaponState == AIWeaponState.Obsticle)
                            output = AILOC.Remove + AILOC.Obst;
                        else
                        {
                            switch (ControlOperator.DriveDest)
                            {
                                case EDriveDest.FromLastDestination:
                                    output = AILOC.Sea_MoveFrom + AILOC.Destination;
                                    break;
                                case EDriveDest.ToLastDestination:
                                    output = AILOC.Sea_MoveTo + AILOC.Destination;
                                    break;
                                case EDriveDest.ToBase:
                                    output = AILOC.Sea_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                    break;
                                case EDriveDest.ToMine:
                                    output = AILOC.Sea_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                                    break;
                                default:
                                    output = AILOC.AIArrived;
                                    break;
                            }
                        }
                    }
                    break;
                case AIDriverType.Stationary:
                    if (lastEnemyGet)
                        output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                    {
                        output = AILOC.AIStationaryBase;
                    }
                    break;
                default:
                    if (lastEnemyGet)
                        output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                    {
                        if (WeaponState == AIWeaponState.Obsticle)
                            output = AILOC.Remove + AILOC.Obst;
                        else
                        {
                            switch (ControlOperator.DriveDest)
                            {
                                case EDriveDest.FromLastDestination:
                                    output = AILOC.Gnd_MoveFrom + AILOC.Destination;
                                    break;
                                case EDriveDest.ToLastDestination:
                                    output = AILOC.Gnd_MoveTo + AILOC.Destination;
                                    break;
                                case EDriveDest.ToBase:
                                    output = AILOC.Gnd_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                    break;
                                case EDriveDest.ToMine:
                                    output = AILOC.Gnd_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                                    break;
                                default:
                                    output = AILOC.AIArrived;
                                    break;
                            }
                        }
                    }
                    break;
            }
        }
        private void GetActionOperatorsAllied(ref string output, ref bool cantDo)
        {
            switch (DediAI)
            {
                case AIType.Aegis:
                    if (lastEnemyGet)
                        output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else if (theResource)
                        output = AILOC.Protect + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                    else
                        output = GUIAIManager.LOC_Protect_req.ToString().Replace("#", (JobSearchRange + AIGlobals.FindItemScanRangeExtension).ToString());
                    break;
                case AIType.Assault:
                    if (DriveDestDirected == EDriveDest.ToBase)
                    {
                        if (theBase)
                        {
                            if (WeaponState == AIWeaponState.Obsticle)
                                output = AILOC.Remove + AILOC.Obst;
                            else if (recentSpeed > 8)
                                output = AILOC.Gen_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                            else if (GetEnergyPercent() <= 0.95f)
                                output = AILOC.Collect + AILOC.Energy;
                            else
                                output = AILOC.SearchFor + AILOC.Enemy;
                        }
                        else
                            output = GUIAIManager.LOC_Battery_req;
                    }
                    else
                    {
                        if (theResource)
                        {
                            if (lastEnemyGet)
                            {
                                output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                            }
                            else
                                output = AILOC.Gen_MoveTo + AILOC.Enemy;
                        }
                        else
                            output = GUIAIManager.LOC_Scout_desc + "\n" + AILOC.SearchFor + AILOC.Enemy;
                    }
                    break;
                case AIType.Energizer:
                    if (DriveDestDirected == EDriveDest.ToBase)
                    {
                        if (theBase)
                        {
                            if (WeaponState == AIWeaponState.Obsticle)
                                output = AILOC.Remove + AILOC.Obst;
                            else if (recentSpeed > 8)
                                output = AILOC.Gen_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                            else if (GetEnergyPercent() <= 0.95f)
                                output = AILOC.Collect + AILOC.Energy;
                            else
                                output = GUIAIManager.LOC_Charger_desc + "\n" + AILOC.SearchFor + AILOC.Ally + AILOC.Energy + AILOC.Request;
                        }
                        else
                        {
                            cantDo = true;
                            output = GUIAIManager.LOC_Battery_req;
                        }
                    }
                    else
                    {
                        if (theResource)
                        {
                            if (WeaponState == AIWeaponState.Obsticle)
                                output = AILOC.Remove + AILOC.Obst;
                            else if (recentSpeed > 8)
                                output = AILOC.Gen_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                            else
                                output = AILOC.Giving + AILOC.Energy;
                        }
                        else
                            output = GUIAIManager.LOC_Charger_desc + "\n" + AILOC.SearchFor + AILOC.Ally;
                    }
                    break;
                case AIType.Escort:
                    switch (DriverType)
                    {
                        case AIDriverType.Astronaut:
                            if (lastEnemyGet)
                                output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                            else
                            {
                                if (WeaponState == AIWeaponState.Obsticle)
                                    output = AILOC.Remove + AILOC.Obst;
                                else
                                {
                                    switch (ControlOperator.DriveDest)
                                    {
                                        case EDriveDest.FromLastDestination:
                                            output = AILOC.Gen_MoveFrom + AILOC.Player;
                                            break;
                                        case EDriveDest.ToLastDestination:
                                            output = AILOC.Gen_MoveTo + AILOC.Player;
                                            break;
                                        case EDriveDest.ToBase:
                                            output = AILOC.Gen_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                            break;
                                        case EDriveDest.ToMine:
                                            output = AILOC.Gen_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                                            break;
                                        default:
                                            output = GUIAIManager.LOC_Space_desc + "\n" + GUIAIManager.LOC_Escort_desc;
                                            break;
                                    }
                                }
                            }
                            break;
                        case AIDriverType.Pilot:
                            if (lastEnemyGet)
                                output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                            else
                            {
                                if (MovementController is AIControllerAir air)
                                {
                                    if (air.Grounded)
                                    {
                                        cantDo = true;
                                        output = AILOC.Fly_Grounded;
                                    }
                                    else
                                    {
                                        if (WeaponState == AIWeaponState.Obsticle)
                                            output = AILOC.Fly_Crashed;
                                        else
                                        {
                                            switch (ControlOperator.DriveDest)
                                            {
                                                case EDriveDest.FromLastDestination:
                                                    output = AILOC.Fly_MoveFrom + AILOC.Player;
                                                    break;
                                                case EDriveDest.ToLastDestination:
                                                    output = AILOC.Fly_MoveTo + AILOC.Player;
                                                    break;
                                                case EDriveDest.ToBase:
                                                    output = AILOC.Fly_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                                    break;
                                                case EDriveDest.ToMine:
                                                    output = AILOC.Fly_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                                                    break;
                                                default:
                                                    output = GUIAIManager.LOC_Air_desc + "\n" + GUIAIManager.LOC_Escort_desc;
                                                    break;
                                            }
                                        }
                                    }
                                }
                                else
                                    output = "Unhandled error in switch";
                            }
                            break;
                        case AIDriverType.Sailor:
                            if (lastEnemyGet)
                                output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                            else
                            {
                                if (WeaponState == AIWeaponState.Obsticle)
                                    output = AILOC.Remove + AILOC.Obst;
                                else
                                {
                                    switch (ControlOperator.DriveDest)
                                    {
                                        case EDriveDest.FromLastDestination:
                                            output = AILOC.Sea_MoveFrom + AILOC.Player;
                                            break;
                                        case EDriveDest.ToLastDestination:
                                            output = AILOC.Sea_MoveTo + AILOC.Player;
                                            break;
                                        case EDriveDest.ToBase:
                                            output = AILOC.Sea_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                            break;
                                        case EDriveDest.ToMine:
                                            output = AILOC.Sea_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                                            break;
                                        default:
                                            output = GUIAIManager.LOC_Water_desc + "\n" + GUIAIManager.LOC_Escort_desc;
                                            break;
                                    }
                                }
                            }
                            break;
                        default:
                            if (lastEnemyGet)
                                output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                            else
                            {
                                if (WeaponState == AIWeaponState.Obsticle)
                                    output = AILOC.Remove + AILOC.Obst;
                                else
                                {
                                    switch (ControlOperator.DriveDest)
                                    {
                                        case EDriveDest.FromLastDestination:
                                            output = AILOC.Gnd_MoveFrom + AILOC.Player;
                                            break;
                                        case EDriveDest.ToLastDestination:
                                            output = AILOC.Gnd_MoveTo + AILOC.Player;
                                            break;
                                        case EDriveDest.ToBase:
                                            output = AILOC.Gnd_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                            break;
                                        case EDriveDest.ToMine:
                                            output = AILOC.Gnd_MoveTo + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                                            break;
                                        default:
                                            output = GUIAIManager.LOC_Tank_desc + "\n" + GUIAIManager.LOC_Escort_desc;
                                            break;
                                    }
                                }
                            }
                            break;
                    }
                    break;
                case AIType.MTMimic:
                    if (!AllMT)
                    {
                        if ((bool)theResource?.tank)
                            output = AILOC.Mimic + AILOC.Player;
                        else
                        {
                            cantDo = true;
                            output = AILOC.SearchFor + AILOC.Player;
                        }
                    }
                    else
                    {
                        if ((bool)theResource?.tank)
                            output = AILOC.Mimic + (theResource.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theResource.name);
                        else
                        {
                            cantDo = true;
                            output = AILOC.SearchFor + AILOC.Ally;
                        }
                    }
                    break;
                case AIType.MTStatic:
                    if (WantsToFight)
                    {
                        output = "Weapons Active";
                    }
                    else
                        output = GUIAIManager.LOC_Static_desc;
                    break;
                case AIType.MTTurret:
                    if ((bool)lastEnemyGet)
                    {
                        if (WantsToFight)
                            output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                        else
                            output = AILOC.FaceTowards + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    }
                    else
                        output = GUIAIManager.LOC_Turret_desc;
                    break;
                case AIType.Prospector:
                    if (DriveDestDirected == EDriveDest.ToBase)
                    {
                        if ((bool)theBase)
                        {
                            if (WeaponState == AIWeaponState.Obsticle)
                                output = AILOC.Remove + AILOC.Obst;
                            else if (recentSpeed > 8)
                                output = AILOC.Gen_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                            else
                                output = AILOC.Giving + AILOC.Cargo;
                        }
                        else
                        {
                            cantDo = true;
                            output = GUIAIManager.LOC_Miner_req;
                        }
                    }
                    else
                    {
                        if ((bool)theResource?.resdisp)
                        {
                            IEnumerable<ChunkTypes> CT = theResource.resdisp.AllDispensableItems();
                            if (recentSpeed > 8)
                            {
                                if (!CT.Any())
                                    output = AILOC.Gen_MoveTo + "rock";
                                else
                                    output = AILOC.Gen_MoveTo + StringLookup.GetItemName(theResource.m_ItemType);
                            }
                            else
                            {
                                if (!CT.Any())
                                    output = AILOC.Collect + "rock";
                                else
                                    output = AILOC.Collect + StringLookup.GetItemName(theResource.m_ItemType);
                            }
                        }
                        else
                        {
                            if (ActionPause > 0)
                            {
                                output = "Reversing [" + ActionPause + "]...";
                            }
                            else
                                output = GUIAIManager.LOC_Miner_desc.ToString().Replace("#", (JobSearchRange + AIGlobals.FindItemScanRangeExtension).ToString())
                                    + "\nNo resources in " + (JobSearchRange + AIGlobals.FindItemScanRangeExtension) + "m";
                        }
                    }
                    break;
                case AIType.Scrapper:
                    if (DriveDestDirected == EDriveDest.ToBase)
                    {
                        if ((bool)theBase)
                        {
                            if (WeaponState == AIWeaponState.Obsticle)
                                output = AILOC.Remove + AILOC.Obst;
                            else if (recentSpeed > 8)
                                output = AILOC.Gen_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                            else
                                output = AILOC.Giving + AILOC.Cargo;
                        }
                        else
                        {
                            cantDo = true;
                            output = GUIAIManager.LOC_Collect_req;
                        }
                    }
                    else
                    {
                        if ((bool)theResource?.block)
                        {
                            BlockTypes BT = theResource.block.BlockType;
                            if (WeaponState == AIWeaponState.Obsticle)
                                output = AILOC.Remove + AILOC.Obst;
                            else if (recentSpeed > 8)
                            {
                                if (BT == BlockTypes.GSOAIController_111)
                                    output = AILOC.Gen_MoveTo + "block";
                                else
                                    output = AILOC.Gen_MoveTo + StringLookup.GetItemName(new ItemTypeInfo(ObjectTypes.Block, (int)BT));
                            }
                            else
                            {
                                if (BT == BlockTypes.GSOAIController_111)
                                    output = AILOC.Collect + "block";
                                else
                                    output = AILOC.Collect + StringLookup.GetItemName(new ItemTypeInfo(ObjectTypes.Block, (int)BT));
                            }
                        }
                        else
                            output = GUIAIManager.LOC_Miner_desc.ToString().Replace("#", (JobSearchRange + AIGlobals.FindItemScanRangeExtension).ToString())
                                +"\nNo blocks in " + (JobSearchRange + AIGlobals.FindItemScanRangeExtension) + "m";
                    }
                    break;
            }
        }
        private void GetActionOperatorsNonPlayer(ref string output, ref bool cantDo)
        {
            var mind = GetComponent<EnemyMind>();
            switch (mind.CommanderMind)
            {
                case EnemyAttitude.Homing:
                    if (lastEnemyGet)
                        GetActionOperatorsNonPlayerCombat(mind, ref output, ref cantDo);
                    else
                    {
                        output = "Looking for trouble (Homing)!";
                    }
                    break;
                case EnemyAttitude.Miner:
                    if (lastEnemyGet)
                        GetActionOperatorsNonPlayerCombat(mind, ref output, ref cantDo);
                    else
                    {
                        if (DriveDestDirected == EDriveDest.ToBase)
                        {
                            if ((bool)theBase)
                            {
                                if (WeaponState == AIWeaponState.Obsticle)
                                    output = AILOC.Remove + AILOC.Obst;
                                else if (recentSpeed > 8)
                                    output = AILOC.Gen_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                else
                                    output = AILOC.Giving + AILOC.Cargo;
                            }
                            else
                            {
                                cantDo = true;
                                output = GUIAIManager.LOC_Miner_req;
                            }
                        }
                        else
                        {
                            if ((bool)theResource?.resdisp)
                            {
                                IEnumerable<ChunkTypes> CT = theResource.resdisp.AllDispensableItems();
                                if (recentSpeed > 8)
                                {
                                    if (CT.Any())
                                        output = AILOC.Gen_MoveTo + "rock";
                                    else
                                        output = AILOC.Gen_MoveTo + StringLookup.GetItemName(theResource.m_ItemType);
                                }
                                else
                                {
                                    if (CT.Any())
                                        output = AILOC.Collect + "rock";
                                    else
                                        output = AILOC.Collect + StringLookup.GetItemName(theResource.m_ItemType);
                                }
                            }
                            else
                                output = GUIAIManager.LOC_Miner_desc.ToString().Replace("#", (JobSearchRange + AIGlobals.FindItemScanRangeExtension).ToString())
                                    + "\nNo resources in " + (JobSearchRange + AIGlobals.FindItemScanRangeExtension) + "m";
                        }
                    }
                    break;
                case EnemyAttitude.Junker:
                    if (lastEnemyGet)
                        GetActionOperatorsNonPlayerCombat(mind, ref output, ref cantDo);
                    else
                    {
                        if (DriveDestDirected == EDriveDest.ToBase)
                        {
                            if ((bool)theBase)
                            {
                                if (WeaponState == AIWeaponState.Obsticle)
                                    output = AILOC.Remove + AILOC.Obst;
                                else if (recentSpeed > 8)
                                    output = AILOC.Gen_MoveTo + (theBase.name.NullOrEmpty() ? AILOC.UnknownUnnamed : theBase.name);
                                else
                                    output = AILOC.Giving + AILOC.Cargo;
                            }
                            else
                            {
                                cantDo = true;
                                output = GUIAIManager.LOC_Collect_req;
                            }
                        }
                        else
                        {
                            if ((bool)theResource?.block)
                            {
                                BlockTypes BT = theResource.block.BlockType;
                                if (WeaponState == AIWeaponState.Obsticle)
                                    output = AILOC.Remove + AILOC.Obst;
                                else if (recentSpeed > 8)
                                {
                                    if (BT == BlockTypes.GSOAIController_111)
                                        output = AILOC.Gen_MoveTo + "block";
                                    else
                                        output = AILOC.Gen_MoveTo + StringLookup.GetItemName(theResource.m_ItemType);
                                }
                                else
                                {
                                    if (BT == BlockTypes.GSOAIController_111)
                                        output = AILOC.Collect + "block";
                                    else
                                        output = AILOC.Collect + StringLookup.GetItemName(theResource.m_ItemType);
                                }
                            }
                            else
                                output = GUIAIManager.LOC_Miner_desc.ToString().Replace("#", (JobSearchRange + AIGlobals.FindItemScanRangeExtension).ToString())
                                    + "\nNo blocks in " + (JobSearchRange + AIGlobals.FindItemScanRangeExtension) + "m";
                        }
                    }
                    break;
                case EnemyAttitude.OnRails:
                    if (lastEnemyGet)
                        output = AILOC.Fighting + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                    {
                        output = ">SCRIPT<";
                    }
                    break;
                case EnemyAttitude.NPCBaseHost:
                    if (lastEnemyGet)
                        GetActionOperatorsNonPlayerCombat(mind, ref output, ref cantDo);
                    else
                    {
                        output = "Managing Base";
                    }
                    break;
                case EnemyAttitude.Boss:
                    if (lastEnemyGet)
                        GetActionOperatorsNonPlayerCombat(mind, ref output, ref cantDo);
                    else
                    {
                        output = "Plotting next attack...";
                    }
                    break;
                case EnemyAttitude.Invader:
                    if (lastEnemyGet)
                        GetActionOperatorsNonPlayerCombat(mind, ref output, ref cantDo);
                    else
                    {
                        output = "Invading";
                    }
                    break;
                default:
                    if (lastEnemyGet)
                        GetActionOperatorsNonPlayerCombat(mind, ref output, ref cantDo);
                    else
                    {
                        GetActionOperatorsPositional(ref output, ref cantDo);
                    }
                    break;
            }
        }
        private void GetActionOperatorsNonPlayerCombat(EnemyMind mind, ref string output, ref bool cantDo)
        {
            switch (mind.CommanderAttack)
            {
                case EAttackMode.Safety:
                    if (BlockedLineOfSight)
                        output = AILOC.CombatOperator + AILOC.HideFrom + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else if (ControlCore.DriveDest == EDriveDest.ToLastDestination)
                        output = AILOC.CombatOperator + AILOC.Gen_MoveTo + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                        output = AILOC.CombatOperator + AILOC.Gen_MoveFrom + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    break;
                case EAttackMode.Ranged:
                    if (BlockedLineOfSight)
                        output = AILOC.CombatOperator + AILOC.FaceTowards + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else if (ControlCore.DriveDest == EDriveDest.ToLastDestination)
                        output = AILOC.CombatOperator + AILOC.Gen_MoveTo + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                        output = AILOC.CombatOperator + AILOC.Gen_MoveFrom + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    break;
                default:
                    if (BlockedLineOfSight)
                        output = AILOC.CombatOperator + AILOC.SearchFor + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else if (ControlCore.DriveDest == EDriveDest.ToLastDestination)
                        output = AILOC.CombatOperator + AILOC.Gen_MoveTo + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    else
                        output = AILOC.CombatOperator + AILOC.Gen_MoveFrom + (lastEnemyGet.name.NullOrEmpty() ? AILOC.UnknownUnnamed : lastEnemyGet.name);
                    break;
            }
        }

        private int maxBlockCount = 1;
        private int lastBlockCount = 1;
        public bool CanDetectHealth()
        {
            return true;
        }
        public float GetHealth()
        {
            return GetHealthPercent() * (maxBlockCount * 10);
        }
        public float GetHealthMax()
        {
            return maxBlockCount * 10;
        }
        public float GetHealth100()
        {
            if (!CanDetectHealth())
                return 100;
            return 100 - DamageThreshold;
        }
        public float GetHealthPercent()
        {
            if (!CanDetectHealth())
                return 1;
            return (100 - DamageThreshold) / 100;
        }
        public float GetSpeed()
        {
            if (tank.rbody.IsNull())
                return 0;
            if (IsTryingToUnjam)
                return 0;
            if (Attempt3DNavi || MovementController is AIControllerAir)
            {
                return SafeVelocity.magnitude;
            }
            else
            {
                if (!(bool)tank.rootBlockTrans)
                    return 0;
                return LocalSafeVelocity.z;
            }
        }
        public bool CanStoreEnergy()
        {
            var energy = tank.EnergyRegulator.Energy(TechEnergy.EnergyType.Electric);
            return energy.storageTotal > 1;
        }
        public float GetEnergy()
        {
            var energy = tank.EnergyRegulator.Energy(TechEnergy.EnergyType.Electric);
            if (energy.storageTotal < 1)
                return 0;

            return energy.storageTotal - energy.spareCapacity;
        }
        public float GetEnergyMax()
        {
            var energy = tank.EnergyRegulator.Energy(TechEnergy.EnergyType.Electric);
            if (energy.storageTotal < 1)
                return 1;

            return energy.storageTotal;
        }
        public float GetEnergyPercent()
        {
            var energy = tank.EnergyRegulator.Energy(TechEnergy.EnergyType.Electric);
            if (energy.storageTotal < 1)
                return 0;

            return (energy.storageTotal - energy.spareCapacity) / energy.storageTotal;
        }
        // REVISED: ground IsTechMoving* count yaw (angularVelocity.y > AngularProgressThreshold) as moving so a tech
        // pivoting in place is not treated as stuck - BUT only while MakingNetProgress, so a tech grinding/jittering
        // between obstacles (driving hard, yawing, but no net displacement over the window) is still flagged stuck.
        // IsTechMovingSigned was removed.
        public bool IsTechMovingAbs(float minSpeed)
        {
            if (tank.rbody.IsNull())
                return true;
            if (IsTryingToUnjam)
                return false;
            if (Attempt3DNavi || MovementController is AIControllerAir)
            {
                return SafeVelocity.sqrMagnitude > minSpeed * minSpeed;
            }
            else
            {
                if (!(bool)tank.rootBlockTrans)
                    return false;
                if (Mathf.Abs(LocalSafeVelocity.z) > minSpeed || Mathf.Abs(GetDrive) < 0.5f)
                    return true;
                return Mathf.Abs(tank.rbody.angularVelocity.y) > AIGlobals.AngularProgressThreshold && MakingNetProgress;
            }
        }
        public bool IsTechMovingActual(float minSpeed)
        {
            if (tank.rbody.IsNull())
                return true;
            if (IsTryingToUnjam)
                return false;
            if (Attempt3DNavi || MovementController is AIControllerAir)
            {
                return SafeVelocity.sqrMagnitude > minSpeed * minSpeed;
            }
            else
            {
                if (!(bool)tank.rootBlockTrans)
                    return false;
                if (Mathf.Abs(LocalSafeVelocity.z) > minSpeed)
                    return true;
                return Mathf.Abs(tank.rbody.angularVelocity.y) > AIGlobals.AngularProgressThreshold && MakingNetProgress;
            }
        }
        public bool HasAnchorAI()
        {
            foreach (var AIEx in AIList)
            {
                if (AIEx.GetComponent<ModuleAnchor>())
                {
                    if (ManWorld.inst.GetTerrainHeight(AIEx.transform.position, out float height))
                        if (AIEx.GetComponent<ModuleAnchor>().HeightOffGroundForMaxAnchor() > height)
                            return true;
                }
            }
            return false;
        }
        // REVISED: now null-guards tank/network/playerTank throughout and returns null (clearing lastPlayer) on failure, instead of returning the stale lastPlayer; SP path returns the live player tank's visible directly.
        public Visible GetPlayerTech()
        {
            if (tank == null)
            {
                lastPlayer = null;
                return null;
            }
            if (ManNetwork.IsNetworked)
            {
                var net = ManNetwork.inst;
                if (net == null)
                {
                    lastPlayer = null;
                    return null;
                }
                try
                {
                    var techs = net.GetAllPlayerTechs();
                    if (techs != null)
                    {
                        foreach (Tank thatTech in techs)
                        {
                            if (thatTech != null && thatTech.Team == tank.Team && thatTech.visible != null)
                                return thatTech.visible;
                        }
                    }
                }
                catch (Exception e) when (e is NullReferenceException || e is MissingReferenceException)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "GetPlayerTech.Net:" + tank.name,
                        "GetPlayerTech (networked) failed; invalidating lastPlayer", e);
                    lastPlayer = null;
                    return null;
                }
                lastPlayer = null;
                return null;
            }
            var pt = Singleton.playerTank;
            if (pt == null || pt.visible == null)
            {
                lastPlayer = null;
                return null;
            }
            return pt.visible;
        }
        private void GetLowestPointOnTech()
        {
            float lowest = 0;
            IEnumerable<IntVector3> lowCells = tank.blockman.GetLowestOccupiedCells();
            Quaternion forwardGrid = tank.rootBlockTrans.localRotation;
            foreach (IntVector3 intVector in lowCells)
            {
                Vector3 cellPosLocal = forwardGrid * intVector;
                if (cellPosLocal.y < lowest)
                    lowest = cellPosLocal.y;
            }
            DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  lowest point set " + lowest);
            _LowestPointOnTech = lowest;
            PendingHeightCheck = false;
        }
        public bool TestIsLowestPointOnTech(TankBlock block)
        {
            bool isTrue = false;
            if (block == null)
                return false;
            Quaternion forward = AIGlobals.LookRot(tank.rootBlockTrans.forward, tank.rootBlockTrans.up);
            IntVector3[] filledCells = block.filledCells;
            foreach (IntVector3 intVector in filledCells)
            {
                Vector3 Locvec = block.cachedLocalPosition + block.cachedLocalRotation * intVector;
                Vector3 cellPosLocal = (forward * Locvec) - tank.rootBlockTrans.InverseTransformPoint(tank.boundsCentreWorldNoCheck);
                if (cellPosLocal.y < LowestPointOnTech)
                {
                    LowestPointOnTech = cellPosLocal.y;
                    isTrue = true;
                }
            }
            if (isTrue)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  lowest point set " + LowestPointOnTech);
            }
            return isTrue;
        }

        // REVISED: renamed from ControlTech; this is the per-frame movement bridge invoked by the ExecuteControl Harmony prefix.
        // REVISED: the AIControlOverride early-returns are now inverted (return true when the override returns false), so the override's return value gates correctly.
        public bool RunMovementBridge(TankControl thisControl)
        {
            if (ManNetwork.IsNetworked)
            {
                if (ManNetwork.IsHost)
                {
                    if (tank.TechIsActivePlayer())
                    {
                        if (Singleton.playerTank == tank && RTSControlled)
                        {
                            UpdateTechControl(thisControl);
                            return true;
                        }
                        else
                            SuppressFiring(false);
                    }
                    else
                    {
                        if (tank.FirstUpdateAfterSpawn)
                        {
                            if (!tank.IsAnchored && AutoAnchor)
                            {
                                AnchorIgnoreChecks(true);
                            }
                        }
                        else if (AIControlOverride != null && !AIControlOverride(this, ExtControlStatus.MaintainersAndDirectors))
                        {
                            return true;
                        }
                        else if (RunState == AIRunState.Advanced)
                        {
                            if (AIAlign == AIAlignment.Player)
                            {
                                if (SetToActive)
                                {
                                    UpdateTechControl(thisControl);
                                    return true;
                                }
                            }
                            else if (AIAlign == AIAlignment.NonPlayer)
                            {
                                UpdateTechControl(thisControl);
                                return true;
                            }
                        }
                    }
                }
                else
                {
                    if (KickStart.AllowPlayerRTSHUD && KickStart.AutopilotPlayer && Singleton.playerTank == tank)
                    {
                        if (tank.PlayerFocused)
                        {
                            if (!RTSControlled)
                                SetRTSState(true);
                            if (RTSDestInternal == RTSDisabled)
                                RTSDestination = tank.boundsCentreWorldNoCheck;
                            UpdateTechControl(thisControl);
                            return true;
                        }
                    }
                }
            }
            else
            {
                if (!tank.PlayerFocused || KickStart.AutopilotPlayer)
                {
                    if (tank.FirstUpdateAfterSpawn)
                    {
                        if (tank.GetComponent<RequestAnchored>())
                        {
                            AnchorIgnoreChecks();
                        }
                    }
                    else if (AIControlOverride != null && !AIControlOverride(this, ExtControlStatus.MaintainersAndDirectors))
                    {
                        return true;
                    }
                    else if (RunState == AIRunState.Advanced)
                    {
                        if (AIAlign == AIAlignment.Player)
                        {
                            if (tank.PlayerFocused)
                            {
                                UpdateTechControl(thisControl);
                                return true;
                            }
                            else if (SetToActive)
                            {
                                UpdateTechControl(thisControl);
                                return true;
                            }
                        }
                        else if (AIAlign == AIAlignment.NonPlayer)
                        {
                            UpdateTechControl(thisControl);
                            return true;
                        }
                    }
                }
            }
            SuppressFiring(false);
            return false;
        }
        private void UpdateTechControl(TankControl thisControl)
        {
            if (AIControlOverride != null && !AIControlOverride(this, ExtControlStatus.MaintainersAndDirectors))
                return;
            CurHeight = -500;

            // REVISED: a null MovementController now triggers a recalibrate-and-retry (and a throttled file-only warning, escalating at 30 ticks) instead of just logging; the tech skips driving this tick rather than NRE-ing.
            if (MovementController is null)
            {
                RecalibrateMovementAIController();
                if (MovementController is null)
                {
                    consecutiveNullMovementControllerTicks++;
                    string tankKey = tank ? tank.name : "<recycled>";
                    if (consecutiveNullMovementControllerTicks == 1)
                        DebugTAC_AI.LogWarnFileOnly(
                            "NullMovementController:" + tankKey,
                            "AI " + tankKey + ": MovementController null after recalibrate - tank will not drive this tick.", null);
                    else if (consecutiveNullMovementControllerTicks == 30)
                        DebugTAC_AI.LogWarnPlayerOncePerKey(
                            "NullMovementController:persistent:" + tankKey,
                            "AI " + tankKey + ": MovementController STILL null after 30 ticks - persistent invariant violation, manual intervention required.", null);
                    return;
                }
            }
            if (consecutiveNullMovementControllerTicks != 0)
                consecutiveNullMovementControllerTicks = 0;

            if (IsControlOperatorStale)
                DebugTAC_AI.LogWarnFileOnly(
                    "ControlOperatorStale:" + (tank ? tank.name : "<recycled>"),
                    "AI " + (tank ? tank.name : "<recycled>") + ": ControlOperator stale by " +
                    ControlOperatorAgeFrames + " frames (>" + (KickStart.AIClockPeriod * 3) + ").", null);

            AIEBeam.BeamMaintainer(thisControl, this, tank);
            if (UpdateDirectorsAndPathing)
            {
                try
                {
                    AIEWeapons.WeaponDirector(thisControl, this, tank);
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "UTC-WeaponDirector-" + tank.name,
                        "AI " + tank.name + ": WeaponDirector error", e);
                }

                try
                {
                    if (!IsTryingToUnjam)
                    {
                        Avoiding = false;
                        EControlCoreSet coreCont = new EControlCoreSet(ControlOperator);
                        if (RTSControlled)
                            MovementController.DriveDirectorRTS(ref coreCont);
                        else
                            MovementController.DriveDirector(ref coreCont);
                        SetCoreControl(coreCont);
                    }
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "UTC-DriveDirector-" + tank.name,
                        "AI " + tank.name + ": DriveDirector error", e);
                }

                UpdateDirectorsAndPathing = false;
            }
            if (NotInBeam)
            {
                try
                {
                    AIEWeapons.WeaponMaintainer(this, tank);
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "UTC-WeaponMaintainer-" + tank.name,
                        "AI " + tank.name + ":  WeaponMaintainer error", e);
                }
                try
                {
                    MovementController.DriveMaintainer(ref ControlCore);
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "UTC-DriveMaintainer-" + tank.name,
                        "AI " + tank.name + ": DriveMaintainer error", e);
                }
            }
        }

        private void RunStaticOperations()
        {
            if (Singleton.Manager<ManWorld>.inst.CheckIsTileAtPositionLoaded(tank.boundsCentreWorldNoCheck))
                TryRepairStatic();
        }
        private void RunAlliedOperations()
        {
            var aI = tank.AI;

            CheckTryRepairAllied();

            BoltsFired = false;
            Attempt3DNavi = false;

            // REVISED: target focus/Provoke decay (UpdateTargetCombatFocus) now runs once up-front for ALL allied techs, including PlayerFocused ones (was only on the non-PlayerFocused branch); the per-tick ActionPause decrement moved out to OnPreUpdate.
            UpdateTargetCombatFocus();

            if (tank.PlayerFocused)
            {
                if (KickStart.AllowPlayerRTSHUD)
                {
#if DEBUG
                        if (ManWorldRTS.PlayerIsInRTS && ManWorldRTS.DevCamLock == DebugCameraLock.LockTechToCam)
                        {
                            if (tank.rbody)
                            {
                                tank.rbody.MovePosition(Singleton.cameraTrans.position + (Vector3.up * 75));
                                return;
                            }
                        }
#endif
                    if (KickStart.AutopilotPlayer)
                    {
                        Retreat = DetermineRetreatPosture();
                        if (RTSControlled)
                        {
                            DebugTAC_AI.LogWarnPlayerOncePerKey(
                                "PlayerRTSDetour:" + tank.name,
                                "AI " + tank.name + ": player-side RTS detour active (RunRTSNavi(true) instead of OpsController.Execute).", null);
                            RunRTSNavi(true);
                        }
                        else
                            OpsController.Execute();
                    }
                }
                return;
            }
            // REVISED: a failed TryGetCurrentAIType now clears lastAITypeResolved (so SetToActive stays false and the tech is suspended) instead of silently falling through with a stale Idle.
            if (!aI.TryGetCurrentAIType(out lastAIType))
            {
                if (lastAITypeResolved)
                    DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name +
                        " - TryGetCurrentAIType FAILED; AI input suspended until resolved.");
                lastAITypeResolved = false;
                lastAIType = AITreeType.AITypes.Idle;
                return;
            }
            lastAITypeResolved = true;
            if (SetToActive)
            {


                Retreat = DetermineRetreatPosture();

                if (RTSControlled && IsRTSReceivable)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "AlliedRTSDetour:" + tank.name,
                        "AI " + tank.name + ": allied-side RTS detour active (RunRTSNavi instead of OpsController.Execute).", null);
                    RunRTSNavi();
                }
                else
                    OpsController.Execute();
            }
        }
        // REVISED: the per-tick `ActionPause -= AIClockPeriod` decrement was removed here (it is now seconds-based and self-counts); only the retreat posture is computed before BeEvil(Light).
        private void RunEnemyOperations(bool light = false)
        {
            DetermineRetreatPostureEnemy();
            if (light)
                RCore.BeEvilLight(this, tank);
            else
            {
                RCore.BeEvil(this, tank);
            }
        }

        private void RunRTSNavi(bool isPlayerTech = false)
        {

            EControlOperatorSet direct = GetDirectedControl();
            if (DriverType == AIDriverType.Pilot)
            {
                if (DediAI == AIType.Escort && !IsGoingToPositionalRTSDest &&
                    (lastEnemyGet == null || !lastEnemyGet.isActive))
                    RTSDestination = tank.boundsCentreWorldNoCheck;

                GetDistanceFromTask2D(lastDestinationCore, 0);
                Attempt3DNavi = true;
                BGeneral.ResetValues(this, ref direct);
                AvoidStuff = true;

                float range = (MaxObjectiveRange * 4) + lastTechExtents;
                direct.DriveDest = EDriveDest.ToLastDestination;
                if (AIEPathing.ObstructionAwarenessAny(DodgeSphereCenter, this, DodgeSphereRadius) ||
                    AIEPathing.ObstructionAwarenessTerrain(DodgeSphereCenter, this, DodgeSphereRadius))
                    ThrottleState = AIThrottleState.Yield;
                if (lastEnemyGet != null && lastEnemyGet.isActive)
                {
                    direct.SetLastDest(lastEnemyGet.tank.boundsCentreWorldNoCheck);
                }

                if (tank.wheelGrounded)
                {
                    if (!AutoHandleObstruction(ref direct, lastOperatorRange, true, true))
                        SettleDown();
                }
                else
                {
                    if (lastOperatorRange < (lastTechExtents * 2) + 5)
                    {

                    }
                    else if (lastOperatorRange > range)
                    {
                        FullBoost = true;
                    }
                    else
                    {

                    }
                }
            }
            else
            {
                GetDistanceFromTask(lastDestinationCore);
                bool needsToSlowDown = IsOrbiting();

                Attempt3DNavi = DriverType == AIDriverType.Astronaut;
                BGeneral.ResetValues(this, ref direct);
                AvoidStuff = true;
                if (needsToSlowDown || AIEPathing.ObstructionAwarenessAny(DodgeSphereCenter, this, DodgeSphereRadius)
                    || AIEPathing.ObstructionAwarenessSetPieceAny(DodgeSphereCenter, this, DodgeSphereRadius))
                    ThrottleState = AIThrottleState.Yield;
                if (DediAI == AIType.Escort && !IsGoingToPositionalRTSDest &&
                    (lastEnemyGet == null || !lastEnemyGet.isActive))
                {
                    direct.STOP(this);
                    BGeneral.RTSCombat(this, tank);
                    SetDirectedControl(direct);
                    return;
                }

                bool MoveQueue = ManWorldRTS.HasMovementQueue(this);
                direct.DriveToFacingTowards();
                if (lastOperatorRange < (lastTechExtents * 2) + 32 && !MoveQueue)
                {
                    SettleDown();
                    ThrottleState = AIThrottleState.PivotOnly;
                    if (DelayedAnchorClock < AIGlobals.BaseAnchorMinimumTimeDelay)
                        DelayedAnchorClock++;
                    if (CanAutoAnchor)
                    {
                        if (!tank.IsAnchored)
                        {
                            DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Setting camp!");
                            TryInsureAutoAnchor();
                        }
                    }
                }
                else
                {
                    anchorAttempts = 0;
                    DelayedAnchorClock = 0;
                    if (unanchorCountdown > 0)
                        {  }
                    if (AutoAnchor && PlayerAllowAutoAnchoring && tank.Anchors.NumPossibleAnchors >= 1)
                    {
                        if (tank.Anchors.NumIsAnchored > 0)
                        {
                            unanchorCountdown = 15;
                            Unanchor();
                        }
                    }
                    if (!AutoAnchor && tank.IsAnchored)
                    {
                        BGeneral.RTSCombat(this, tank);
                        SetDirectedControl(direct);
                        return;
                    }
                    if (!IsTechMovingAbs(EstTopSped / AIGlobals.PlayerAISpeedPanicDividend))
                    {
                        TryHandleObstruction(true, lastOperatorRange, false, true, ref direct);
                    }
                    else
                    {
                        if (MoveQueue)
                            AutoSpacing = 0;
                        else
                            AutoSpacing = Mathf.Max(lastTechExtents + 2, 0.5f);
                        SettleDown();
                    }
                }
            }
            SetDirectedControl(direct);
            BGeneral.RTSCombat(this, tank);
        }
        internal void RunRTSNaviEnemy(EnemyMind mind)
        {
            switch (DediAI)
            {
                case AIType.MTTurret:
                case AIType.MTStatic:
                case AIType.MTMimic:
                    IsMultiTech = true;
                    break;
                default:
                    IsMultiTech = false;
                    break;
            }

            EControlOperatorSet direct = GetDirectedControl();
            BGeneral.ResetValues(this, ref direct);
            if (mind.EvilCommander == EnemyHandling.Airplane)
            {
                lastOperatorRange = (DodgeSphereCenter - lastDestinationCore).magnitude;
                Attempt3DNavi = true;
                AvoidStuff = true;

                float range = (MaxObjectiveRange * 4) + lastTechExtents;
                direct.DriveDest = EDriveDest.ToLastDestination;
                if (AIEPathing.ObstructionAwarenessAny(DodgeSphereCenter, this, DodgeSphereRadius) ||
                    AIEPathing.ObstructionAwarenessTerrain(DodgeSphereCenter, this, DodgeSphereRadius))
                    ThrottleState = AIThrottleState.Yield;

                if (tank.wheelGrounded)
                {
                    if (!AutoHandleObstruction(ref direct, lastOperatorRange, true, true))
                        SettleDown();
                }
                else
                {
                    if (lastOperatorRange < (lastTechExtents * 2) + 5)
                    {

                    }
                    else if (lastOperatorRange > range)
                    {
                        FullBoost = true;
                    }
                    else
                    {

                    }
                }
            }
            else
            {
                float prevDist = lastOperatorRange;
                GetDistanceFromTask(lastDestinationCore);
                bool needsToSlowDown = IsOrbiting();

                Attempt3DNavi = mind.EvilCommander == EnemyHandling.Starship;
                AvoidStuff = true;
                bool AutoAnchor = mind.CommanderSmarts >= EnemySmarts.Meh;
                if (needsToSlowDown || AIEPathing.ObstructionAwarenessAny(DodgeSphereCenter, this, DodgeSphereRadius)
                    || AIEPathing.ObstructionAwarenessSetPieceAny(DodgeSphereCenter, this, DodgeSphereRadius))
                    ThrottleState = AIThrottleState.Yield;
                bool MoveQueue = ManWorldRTS.HasMovementQueue(this);
                if (lastOperatorRange < (lastTechExtents * 2) + 32 && !MoveQueue)
                {
                    SettleDown();
                    ThrottleState = AIThrottleState.PivotOnly;
                    if (DelayedAnchorClock < AIGlobals.BaseAnchorMinimumTimeDelay)
                        DelayedAnchorClock++;
                    if (AutoAnchor && !WantsToFight && tank.Anchors.NumPossibleAnchors >= 1
                        && DelayedAnchorClock >= AIGlobals.BaseAnchorMinimumTimeDelay && CanAnchorNow)
                    {
                        if (!tank.IsAnchored && anchorAttempts <= AIGlobals.MaxAnchorAttempts)
                        {
                            DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Setting camp!");
                            TryInsureAutoAnchor();
                            anchorAttempts++;
                        }
                    }
                }
                else
                {
                    anchorAttempts = 0;
                    DelayedAnchorClock = 0;
                    if (unanchorCountdown > 0)
                        {  }
                    if (AutoAnchor && tank.Anchors.NumPossibleAnchors >= 1)
                    {
                        if (tank.Anchors.NumIsAnchored > 0)
                        {
                            unanchorCountdown = 15;
                            Unanchor();
                        }
                    }
                    if (!AutoAnchor && tank.IsAnchored)
                    {
                        RGeneral.RTSCombat(this, tank, mind);
                        SetDirectedControl(direct);
                        return;
                    }
                    if (!IsTechMovingActual(EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                    {
                        TryHandleObstruction(true, lastOperatorRange, false, true, ref direct);
                    }
                    else
                    {
                        if (MoveQueue)
                            AutoSpacing = 0;
                        else
                            AutoSpacing = Mathf.Max(lastTechExtents + 2, 0.5f);
                        SettleDown();
                    }
                }
            }
            SetDirectedControl(direct);
            RGeneral.RTSCombat(this, tank, mind);
        }

        internal void OnPreUpdate()
        {
            if (MovementController == null)
            {
                DebugTAC_AI.Assert(MovementController == null, "MOVEMENT CONTROLLER IS NULL");
                RecalibrateMovementAIController();
            }
            // REVISED: signed speed now kept (recentSpeedSigned) alongside the clamped recentSpeed, and EstTopSped is tracked here every Pre frame (was scattered per-branch in the Operations phase).
            recentSpeedSigned = GetSpeed();
            recentSpeed = recentSpeedSigned;
            if (recentSpeed < 1)
                recentSpeed = 1;
            if (EstTopSped < recentSpeed)
                EstTopSped = recentSpeed;
            UpdateLastTechExtentsIfNeeded();
            CheckRebuildAlignment();
            // REVISED: AIAlign is latched into tickAIAlign for the rest of the tick (TickAIAlign), so Directors/Operations see a consistent alignment even if it changes mid-tick.
            tickAIAlign = AIAlign;
            tickAlignmentLatched = true;
            UpdateCollectors();
        }
        internal void OnPostUpdate()
        {
            tickAlignmentLatched = false;
            ManageAILockOn();
            UpdateBlockHold();
            RunPostOps();
            ShowCollisionAvoidenceDebugThisFrame();
        }
        // REVISED: the global `updateErrored` latch (which permanently muted ALL further critical-error warnings after the first one anywhere) was removed; each tick method now logs via per-tank-keyed LogWarnPlayerOncePerKey so errors keep being reported per tech. The Operations error no longer rethrows.
        private static List<Tank> TempMultiTechRecalibrate = new List<Tank>();
        private void UpdateLastTechExtentsIfNeeded()
        {
            try
            {
                if (dirtyExtents)
                {
                    dirtyExtents = false;
                    tank.blockman.CheckRecalcBlockBounds();
                    lastTechExtents = (tank.blockBounds.size.magnitude / 2) + 2;
                    TempMultiTechRecalibrate.AddRange(MultiTechsAffiliated);
                    MultiTechsAffiliated.Clear();
                    foreach (var item in TempMultiTechRecalibrate)
                    {
                        if (item == null || !item.visible.isActive)
                            continue;
                        var otherHelp = item.GetHelperInsured();
                        MultiTechsAffiliated.Add(item);
                        float extendedExts = (item.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).AbsMax() + otherHelp.lastTechExtents;

                        if (extendedExts > lastTechExtents)
                            lastTechExtents = extendedExts;
                    }
                    TempMultiTechRecalibrate.Clear();
                    if (lastTechExtents < 1)
                    {
                        Debug.LogError("lastTechExtents is below 1: " + lastTechExtents);
                        lastTechExtents = 1;
                    }
                    if (!PendingDamageCheck)
                        maxBlockCount = tank.blockman.blockCount;
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "UpdateLastTechExtentsIfNeeded:" + (tank ? tank.name : "<recycled>"),
                    "UpdateLastTechExtentsIfNeeded() Critical error on " + (tank ? tank.name : "<recycled>"), e);
            }
        }

        internal void OnUpdateHostAIDirectors()
        {
            try
            {
                if (MovementAIControllerDirty)
                    RecalibrateMovementAIController();
                if (RunState == AIRunState.Advanced)
                {
                    switch (TickAIAlign)
                    {
                        case AIAlignment.Player:
                            UpdateDirectorsAndPathing = true;
                            break;
                        case AIAlignment.NonPlayer:
                            if (KickStart.enablePainMode)
                                UpdateDirectorsAndPathing = true;
                            break;
                        default:
                            DriveVar = 0;
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnUpdateHostAIDirectors:" + (tank ? tank.name : "<recycled>"),
                    "OnUpdateHostAIDirectors() Critical error on " + (tank ? tank.name : "<recycled>"), e);
            }
        }
        internal void OnUpdateHostAIOperations()
        {
            try
            {
                UpdatePhysicsInfo();
                bool OverrideControl = AIControlOverride != null;
                if (OverrideControl)
                {
                    CheckEnemyAndAiming();
                    if (!AIControlOverride(this, ExtControlStatus.Operators))
                        return;
                }
                switch (DediAI)
                {
                    case AIType.MTTurret:
                    case AIType.MTStatic:
                    case AIType.MTMimic:
                        IsMultiTech = true;
                        break;
                    default:
                        IsMultiTech = false;
                        break;
                }
                switch (TickAIAlign)
                {
                    // REVISED: unjam paths (here and the NonPlayer cases below) now MarkOperatorDirty after TryHandleObstruction so the freshly-written ControlOperator is not flagged stale; per-branch EstTopSped tracking removed (moved to OnPreUpdate).
                    case AIAlignment.Player:
                        if (!OverrideControl)
                            CheckEnemyAndAiming();
                        if (IsTryingToUnjam)
                        {
                            TryHandleObstruction(true, lastOperatorRange, false, true, ref ControlOperator);
                            MarkOperatorDirty();
                        }
                        else
                            RunAlliedOperations();
                        break;
                    case AIAlignment.NonPlayer:
                        if (KickStart.enablePainMode)
                        {
                            switch (RunState)
                            {
                                case AIRunState.Off:
                                    break;
                                case AIRunState.Default:
                                    if (!OverrideControl)
                                        CheckEnemyAndAiming();
                                    if (IsTryingToUnjam)
                                    {
                                        TryHandleObstruction(true, lastOperatorRange, false, true, ref ControlOperator);
                                        MarkOperatorDirty();
                                        var mind = GetComponent<EnemyMind>();
                                        if (mind)
                                            RCore.ScarePlayer(mind, this, tank);
                                    }
                                    else
                                        RunEnemyOperations(true);
                                    break;
                                case AIRunState.Advanced:
                                    if (!OverrideControl)
                                        CheckEnemyAndAiming();
                                    if (IsTryingToUnjam)
                                    {
                                        TryHandleObstruction(true, lastOperatorRange, false, true, ref ControlOperator);
                                        MarkOperatorDirty();
                                        var mind = GetComponent<EnemyMind>();
                                        if (mind)
                                            RCore.ScarePlayer(mind, this, tank);
                                    }
                                    else
                                        RunEnemyOperations();
                                    break;
                            }
                        }
                        break;
                    default:
                        DriveVar = 0;
                        RunStaticOperations();
                        break;
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnUpdateHostAIOperations:" + (tank ? tank.name : "<recycled>"),
                    "OnUpdateHostAIOperations() Critical error on " + (tank ? tank.name : "<recycled>"), e);
            }
        }

        internal void OnUpdateClientAIDirectors()
        {
            if (MovementAIControllerDirty)
                RecalibrateMovementAIController();
            switch (TickAIAlign)
            {
                case AIAlignment.Static:
                    DriveVar = 0;
                    break;
                case AIAlignment.Player:
                    UpdateDirectorsAndPathing = true;
                    break;
                case AIAlignment.NonPlayer:
                    UpdateDirectorsAndPathing = true;
                    break;
            }
        }
        // REVISED: client operations now actually runs RunStaticOperations for Static techs (was a no-op that only cached EstTopSped); wrapped in try/catch with throttled logging. Player/NonPlayer no longer do per-tick work here.
        internal void OnUpdateClientAIOperations()
        {
            try
            {
                if (TickAIAlign == AIAlignment.Static)
                {
                    DriveVar = 0;
                    RunStaticOperations();
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnUpdateClientAIOperations:" + (tank ? tank.name : "<recycled>"),
                    "OnUpdateClientAIOperations() Critical error on " + (tank ? tank.name : "<recycled>"), e);
            }
        }

        internal void OnTechTeamChange(bool rebootSameAIAlign = false)
        {
            dirtyAI = rebootSameAIAlign ? AIDirtyState.DirtyAndReboot : AIDirtyState.Dirty;
            PlayerAllowAutoAnchoring = !tank.IsAnchored;
            ResetToNormalAimState();
        }
        internal void ForceRebuildAlignment(bool rebootSameAIAlign = false)
        {
            dirtyAI = rebootSameAIAlign ? AIDirtyState.DirtyAndReboot : AIDirtyState.Dirty;
            CheckRebuildAlignment();
        }
        // REVISED: the old SP-host vs NonHostClient code was duplicated top-to-bottom; the three cases now collapse into one MpRole enum threaded through DispatchAlignment + the per-alignment Apply* helpers (client-only steps gated by role != MpClient).
        private enum MpRole { SpHost, MpHost, MpClient }

        // REVISED: early-returns when not dirty (inverted guard), now bumps RunState Default->Advanced on rebuild, and routes through DispatchAlignment; the whole body is wrapped in one try/catch with throttled logging.
        private void CheckRebuildAlignment()
        {
            if (tank.blockman.blockCount == 0)
                return;
            if (dirtyAI == AIDirtyState.Not)
                return;

            bool rebootSameAIAlign = dirtyAI == AIDirtyState.DirtyAndReboot;
            dirtyAI = AIDirtyState.Not;
            if (RunState == AIRunState.Default)
                RunState = AIRunState.Advanced;
            hasAI = tank.AI.CheckAIAvailable();
            lastLockOnTarget = null;
            WantsToFight = false;            SuppressFiring(false);

            MpRole role = !ManNetwork.IsNetworked ? MpRole.SpHost
                        :  ManNetwork.IsHost      ? MpRole.MpHost
                                                  : MpRole.MpClient;
            try
            {
                TankAIManager.UpdateTechTeam(tank);

                if (role == MpRole.MpHost && dirtyExtents)
                {
                    dirtyExtents = false;
                    tank.netTech.SaveTechData();
                }

                DispatchAlignment(rebootSameAIAlign, role);
                ReevaluatePlayerMovementIfNeeded();
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "RebuildAlignment:" + (tank ? tank.name : "<recycled>"),
                    "RebuildAlignment() Critical error on " + (tank ? tank.name : "<recycled>"), e);
            }
        }

        // REVISED: new alignment dispatcher (Player/PlayerNoAI/NonPlayer/Static). New branch: a neutral tech that already has its own vanilla AI tree (Flee/Specific/FacePlayer) is handed off to vanilla via HandOffToVanillaForNeutral instead of being forced Static.
        private void DispatchAlignment(bool rebootSame, MpRole role)
        {
            if (ManSpawn.IsPlayerTeam(tank.Team))
            {
                bool playerAllied = hasAI || (ManWorldRTS.PlayerIsInRTS && tank.PlayerFocused);
                if (playerAllied) ApplyPlayerAlignment(rebootSame, role);
                else              ApplyPlayerNoAIAlignment(rebootSame, role);
            }
            else if (!tank.IsNeutral())
            {
                ApplyNonPlayerAlignment(rebootSame, role);
            }
            else if (role != MpRole.MpClient && NeutralTechHasOwnVanillaAI(tank))
            {
                HandOffToVanillaForNeutral(tank);
            }
            else
            {
                ApplyStaticAlignment(rebootSame, role);
            }
        }

        private static string LogSuffixFor(MpRole role) => role == MpRole.MpClient ? " (NonHostClient)" : "";

        private void ApplyPlayerAlignment(bool rebootSame, MpRole role)
        {
            if (AIAlign == AIAlignment.Player && !rebootSame) return;
            ResetOnSwitchAlignments(tank);
            RemoveEnemyMatters();
            AIAlign = AIAlignment.Player;
            RefreshAI();
            if (role != MpRole.MpClient && (bool)TechMemor && !BookmarkBuilder.Exists(tank))
                TechMemor.SaveTech();
            DebugTAC_AI.Log(KickStart.ModID + ": Allied AI " + tank.name + ":  Checked up and good to go!" + LogSuffixFor(role));
        }

        private void ApplyPlayerNoAIAlignment(bool rebootSame, MpRole role)
        {
            DriveVar = 0;
            if (AIAlign == AIAlignment.PlayerNoAI && !rebootSame) return;
            DebugTAC_AI.Log(KickStart.ModID + ": PlayerNoAI Tech " + tank.name + ": reset" + LogSuffixFor(role));
            ResetOnSwitchAlignments(tank);
            RemoveEnemyMatters();
            if (role != MpRole.MpClient)
                AIEBases.SetupBookmarkBuilder(this);
            AIAlign = AIAlignment.PlayerNoAI;
        }

        private void ApplyNonPlayerAlignment(bool rebootSame, MpRole role)
        {
            if (AIAlign == AIAlignment.NonPlayer && !rebootSame) return;
            ResetOnSwitchAlignments(tank);
            AIAlign = AIAlignment.NonPlayer;
            Enemy.RCore.GenerateEnemyAI(this, tank);
            DebugTAC_AI.Log(KickStart.ModID + ": Enemy AI " + tank.name + " of Team " + tank.Team + ":  Ready to kick some Tech!" + LogSuffixFor(role));
        }

        private void ApplyStaticAlignment(bool rebootSame, MpRole role)
        {
            DriveVar = 0;
            if (AIAlign == AIAlignment.Static && !rebootSame) return;
            DebugTAC_AI.Log(KickStart.ModID + ": Static Tech " + tank.name + ": reset" + LogSuffixFor(role));
            ResetOnSwitchAlignments(tank);
            RemoveEnemyMatters();
            if (role != MpRole.MpClient)
                AIEBases.SetupBookmarkBuilder(this);
            AIAlign = AIAlignment.Static;
        }

        private void RunPostOps()
        {
            if (ExpectAITampering)
            {
                WakeAIForChange();
                ExpectAITampering = false;
            }
            else
            {
                switch (AnchorState)
                {
                    case AIAnchorState.None:
                    case AIAnchorState.Anchored:
                        break;
                    case AIAnchorState.Anchor:
                    case AIAnchorState.AnchorAuto:
                    case AIAnchorState.AnchorStaticAI:
                        DoAnchor(false);
                        break;
                    case AIAnchorState.ForceAnchor:
                        DoAnchor(true);
                        break;
                    case AIAnchorState.Unanchor:
                        DoUnAnchor();
                        WakeAIForChange();
                        break;
                    default:
                        var temp = AnchorState;
                        AnchorState = AIAnchorState.None;
                        throw new NotImplementedException("unknown AnchorState - " + temp);
                }
            }
        }

        internal float DriveControl
        {
            set => tank.control.DriveControl = value;
        }
        internal void UpdateVanillaAvoidence()
        {
            tank.control.m_Movement.m_USE_AVOIDANCE = AvoidStuff;
        }
        public bool FixControlReversal(float Drive)
        {
            var thisControl = tank.control;
            return (thisControl.ActiveScheme == null || !thisControl.ActiveScheme.ReverseSteering) &&
                Drive < -0.01f &&
                Vector3.Dot(SafeVelocity, thisControl.Tech.rootBlockTrans.forward) < 0f;
        }
        internal void ProcessControl(Vector3 DriveVal, Vector3 TurnVal, Vector3 Throttle, bool props, bool jets)
        {
            tank.control.CollectMovementInput(DriveVal, TurnVal, Throttle, props, jets);
        }
        internal void SteerControl(Vector3 direction, float throttle)
        {
            tank.control.m_Movement.FaceDirection(tank, direction, throttle);
        }
        internal float DodgeStrength
        {
            get
            {
                if (UsingAirControls)
                    return AIGlobals.AirborneDodgeStrengthMultiplier * lastOperatorRange;
                return AIGlobals.DefaultDodgeStrengthMultiplier * lastOperatorRange;
            }
        }
        public Vector3 DodgeSphereCenter { get; private set; } = Vector3.zero;
        public Vector3 SafeVelocity { get; private set; } = Vector3.zero;
        public Vector3 LocalSafeVelocity { get; private set; } = Vector3.zero;
        public float DodgeSphereRadius { get; private set; } = 1;
        private float CurHeight = 0;
        public float GetFrameHeight()
        {
            if (CurHeight == -500)
            {
                CurHeight = AIEPathMapper.GetAltitudeCached(tank.boundsCentreWorldNoCheck);
            }
            return CurHeight;
        }
        public Vector3 GetDirAuto(Tank tank)
        {
            if (IsDirectedMovingFromDest)
                return GetDir(tank);
            return GetOtherDir(tank);
        }
        internal Vector3 GetOtherDir(Tank targetToAvoid)
        {
            Vector3 inputOffset = tank.boundsCentreWorldNoCheck - targetToAvoid.boundsCentreWorldNoCheck;
            float inputSpacing = targetToAvoid.GetCheapBounds() + lastTechExtents + DodgeStrength;
            Vector3 Final = tank.boundsCentreWorldNoCheck + (inputOffset.normalized * inputSpacing);
            return Final;
        }
        internal Vector3 GetDir(Tank targetToAvoid)
        {
            Vector3 inputOffset = tank.boundsCentreWorldNoCheck - targetToAvoid.boundsCentreWorldNoCheck;
            float inputSpacing = targetToAvoid.GetCheapBounds() + lastTechExtents + DodgeStrength;
            Vector3 Final = tank.boundsCentreWorldNoCheck - (inputOffset.normalized * inputSpacing);
            return Final;
        }

        internal static List<KeyValuePair<Vector3, float>> posWeights = new List<KeyValuePair<Vector3, float>>();
        // REVISED: while WasRetreatingInCombat, AvoidAssist keeps the ObstDodgeOffset scenery dodge (a tech should never ram scenery, even backing off) but skips the ally-spacing weights so they do not fight the combat-FSM retreat vector; the Precise/Prediction/AirSpacing variants still fully early-out. The obsolete AvoidAssistInv_OBS variant was removed.
        internal Vector3 AvoidAssist(Vector3 targetIn, bool AvoidStatic = true)
        {
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssist IS NaN!!");
                return targetIn;
            }
            if (WasRetreatingInCombat)
            {   // REVISED: while retreating, keep the scenery dodge (a tech should never ram scenery, even while
                // backing off) but skip ally-spacing so it does not fight the retreat vector. ObstDodgeOffset also
                // sets ThrottleState = Yield when it finds scenery, so the retreat eases off near obstacles too.
                Vector3 obstRetreatOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out bool obstRetreat, AdvancedAI);
                if (obstRetreat)
                    return (targetIn + obstRetreatOff * 2f) / 3f;
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                posWeights.Clear();
                if (SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAlly(AlliesAlt, tank.boundsCentreWorldNoCheck, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAlly(AlliesAlt, tank.boundsCentreWorldNoCheck, out lastAllyDist, this);
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                        Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                if (posWeights.Count == 0)
                    return targetIn;
                Vector3 posCombined = targetIn;
                float totalWeight = 1;
                foreach (var item in posWeights)
                {
                    totalWeight += item.Value;
                    posCombined += item.Key * item.Value;
                }
                this.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                if (IsDirectedMovingFromDest)
                    DebugTAC_AI.LogWarnPlayerOnce("AvoidAssist()[INVERTED] Critical error", e);
                else
                    DebugTAC_AI.LogWarnPlayerOnce("AvoidAssist() Critical error", e);
                return targetIn;
            }
        }
        internal Vector3 AvoidAssistPrecise(Vector3 targetIn, bool AvoidStatic = true, bool IgnoreDestructable = false)
        {
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            if (WasRetreatingInCombat)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistPrecise IS NaN!!");
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                posWeights.Clear();
                if (SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAllyPrecision(AlliesAlt, tank.boundsCentreWorldNoCheck, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAllyPrecision(AlliesAlt, tank.boundsCentreWorldNoCheck, out lastAllyDist, this);
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
                        Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                if (posWeights.Count == 0)
                    return targetIn;
                Vector3 posCombined = targetIn;
                float totalWeight = 1;
                foreach (var item in posWeights)
                {
                    totalWeight += item.Value;
                    posCombined += item.Key * item.Value;
                }
                this.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("AvoidAssistPrecise() Critical error", e);
                return targetIn;
            }
        }
        internal Vector3 AvoidAssistPrediction(Vector3 targetIn, float Foresight)
        {
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            if (WasRetreatingInCombat)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistPrediction IS NaN!!");
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                Vector3 posOffset = tank.boundsCentreWorldNoCheck + (SafeVelocity * Foresight);
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                posWeights.Clear();
                if (SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAlly(AlliesAlt, posOffset, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAlly(AlliesAlt, posOffset, out lastAllyDist, this);
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                        Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                if (posWeights.Count == 0)
                    return targetIn;
                Vector3 posCombined = targetIn;
                float totalWeight = 1;
                foreach (var item in posWeights)
                {
                    totalWeight += item.Value;
                    posCombined += item.Key * item.Value;
                }
                this.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("AvoidAssistPrediction() Critical error", e);
                return targetIn;
            }
        }
        internal Vector3 AvoidAssistAirSpacing(Vector3 targetIn, float Responsiveness)
        {
            if (WasRetreatingInCombat)
                return targetIn;
            try
            {
                Tank lastCloseAlly;
                float lastAllyDist;
                // REVISED: the dodge-sphere center no longer divides by Responsiveness; DSO is the raw DodgeSphereCenter (Responsiveness arg now unused for this offset).
                Vector3 DSO = DodgeSphereCenter;
                float moveSpace = (DSO - tank.boundsCentreWorldNoCheck).magnitude;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                if (SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAllyPrecision(AlliesAlt, DSO, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                        {
                            IntVector3 ProccessedVal2 = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            Avoiding = true;
                            return (targetIn + ProccessedVal2) / 3;
                        }
                        IntVector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                        Avoiding = true;
                        return (targetIn + ProccessedVal) / 2;
                    }

                }
                lastCloseAlly = AIEPathing.ClosestAllyPrecision(AlliesAlt, DSO, out lastAllyDist, this);
                this.lastCloseAlly = lastCloseAlly;
                if (lastCloseAlly == null)
                {
                    return targetIn;
                }
                if (lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                {
                    IntVector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                    Avoiding = true;
                    return (targetIn + ProccessedVal) / 2;
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("AvoidAssistAirSpacing() Critical error", e);
                return targetIn;
            }
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistAirSpacing IS NaN!!");
            }
            return targetIn;
        }

        // REVISED: windowed net-progress tracker (see AIGlobals.StuckNetProgress*). MakingNetProgress goes false when the
        // tech has barely moved over the last window - lets IsTechMoving*/the unjam soft-decay tell a real wedge (driving
        // hard but pinned) from an in-place pivot or jitter, which the angular-velocity fallback alone could not.
        private Vector3 netProgressLastPos = Vector3.zero;
        private float netProgressNextCheck = 0f;
        public bool MakingNetProgress { get; private set; } = true;
        private void UpdatePhysicsInfo()
        {
            if (Time.time >= netProgressNextCheck)
            {
                Vector3 curProgressPos = tank.boundsCentreWorldNoCheck;
                if (netProgressNextCheck > 0f)
                {
                    // scale the "made progress" bar by the tech's own top speed so a slow/heavy mover isn't falsely flagged stuck
                    float minProg = Mathf.Max(AIGlobals.StuckNetProgressFloor,
                        EstTopSped * AIGlobals.StuckNetProgressWindow * AIGlobals.StuckNetProgressFraction);
                    MakingNetProgress = (curProgressPos - netProgressLastPos).sqrMagnitude > minProg * minProg;
                }
                netProgressLastPos = curProgressPos;
                netProgressNextCheck = Time.time + AIGlobals.StuckNetProgressWindow;
            }
            if (tank.rbody.IsNotNull())
            {
                var velo = tank.rbody.velocity;
                if (!velo.IsNaN() && !float.IsInfinity(velo.x)
                    && !float.IsInfinity(velo.z) && !float.IsInfinity(velo.y))
                {
                    DodgeSphereCenter = tank.boundsCentreWorldNoCheck + velo.Clamp(lowMaxBoundsVelo, highMaxBoundsVelo);
                    DodgeSphereRadius = lastTechExtents + Mathf.Clamp(recentSpeed / 2f, 1f, 63f);
                    SafeVelocity = velo;
                    LocalSafeVelocity = tank.rootBlockTrans.InverseTransformVector(velo);
                    return;
                }
            }
            DodgeSphereCenter = tank.boundsCentreWorldNoCheck;
            DodgeSphereRadius = lastTechExtents;
            SafeVelocity = Vector3.zero;
            LocalSafeVelocity = Vector3.zero;
        }
        // REVISED: the AIClockPeriod/40 scale factor is now float division ((float)AIClockPeriod/40f); it was integer division that evaluated to 0 in both SP and MP, disabling the orbit check. IsOrbiting_LEGACY was removed.
        public bool IsOrbiting(float minimumCloseInSpeedSqr = AIGlobals.MinimumCloseInSpeedSqr)
        {
            return GetPathPointDeltaDistSq() * ((float)KickStart.AIClockPeriod / 40f) <
                Mathf.Max(minimumCloseInSpeedSqr, EstTopSped / 3) && !Avoiding &&
                Vector3.Dot((PathPoint - tank.boundsCentreWorldNoCheck).normalized, tank.rootBlockTrans.forward) < 0.5f;
        }
        public float GetDistanceFromTask(Vector3 taskLocation, float additionalSpacing = 0)
        {
            if (Attempt3DNavi)
            {
                Vector3 veloFlat;
                if ((bool)tank.rbody)
                {
                    veloFlat = SafeVelocity;
                    veloFlat.y = 0;
                }
                else
                    veloFlat = Vector3.zero;
                lastOperatorRange = (tank.boundsCentreWorldNoCheck + veloFlat - taskLocation).magnitude - additionalSpacing;
                return lastOperatorRange;
            }
            else
            {
                return GetDistanceFromTask2D(taskLocation, additionalSpacing);
            }
        }
        public float GetDistanceFromTask2D(Vector3 taskLocation, float additionalSpacing = 0)
        {
            Vector3 veloFlat;
            if ((bool)tank.rbody)
            {
                veloFlat = SafeVelocity;
                veloFlat.y = 0;
            }
            else
                veloFlat = Vector3.zero;
            lastOperatorRange = (tank.boundsCentreWorldNoCheck.ToVector2XZ() + veloFlat.ToVector2XZ() - taskLocation.ToVector2XZ()).magnitude - additionalSpacing;
            return lastOperatorRange;
        }
        public float GetPathPointDeltaDistSq()
        {
            if (Attempt3DNavi)
            {
                Vector3 veloFlat;
                if ((bool)tank.rbody)
                {
                    veloFlat = SafeVelocity;
                    veloFlat.y = 0;
                }
                else
                    veloFlat = Vector3.zero;
                float distPrev = lastPathPointRange;
                lastPathPointRange = (tank.boundsCentreWorldNoCheck + veloFlat - PathPoint).sqrMagnitude;
                return lastPathPointRange - distPrev;
            }
            else
                return GetPathPointDeltaDistSq2D();
        }
        public float GetPathPointDeltaDistSq2D()
        {
            Vector3 veloFlat;
            if ((bool)tank.rbody)
            {
                veloFlat = SafeVelocity;
                veloFlat.y = 0;
            }
            else
                veloFlat = Vector3.zero;
            float distPrev = lastPathPointRange;
            lastPathPointRange = (tank.boundsCentreWorldNoCheck.ToVector2XZ() + veloFlat.ToVector2XZ() - PathPoint.ToVector2XZ()).sqrMagnitude;
            return lastPathPointRange - distPrev;
        }
        public void SetDistanceFromTaskUnneeded()
        {
            lastOperatorRange = 96;
        }
        public bool AutoHandleObstruction(ref EControlOperatorSet direct, float dist = 0, bool useRush = false, bool useGun = true, float div = 4)
        {
            if (!IsTechMovingAbs(EstTopSped / div))
            {
                TryHandleObstruction(!AIECore.Feedback, dist, useRush, useGun, ref direct);
                return true;
            }
            return false;
        }
        // REVISED: unjam FSM reworked - clears IsTryingToUnjam below UnjamUpdateStart; bails immediately on a Stop facing; soft-decays FrustrationMeter when the tech is actually making linear/angular progress; the UnjamUpdateEnd ceiling calls SettleDown(false)+return (full reset, but no hard core-Stop, so the hand-off keeps momentum) instead of pinning FrustrationMeter=45; the old `45 <` literal gate is now AIGlobals.UnjamUpdateFire. The build-beam phase (120-240) only sets ForceSetBeam when AIEBeam.IsTechTippedOver - upright-but-stuck techs stay in the throttle/fire phase instead of flickering the beam.
        public void TryHandleObstruction(bool hasMessaged, float dist, bool useRush, bool useGun, ref EControlOperatorSet direct)
        {
            if (!hasMessaged)
            {
            }

            ControlCore.FlagBusyUnstucking();
            if (FrustrationMeter <= AIGlobals.UnjamUpdateStart)
                IsTryingToUnjam = false;
            if (direct.DriveDir == EDriveFacing.Stop)
                return;
            // REVISED: bleed the meter on actual net progress (MakingNetProgress over the window), not on instantaneous
            // velocity/yaw - a tech grinding/jittering against an obstacle used to bleed the meter via its yaw chatter and
            // never escalate. Now only real displacement decays it, so a genuine wedge climbs the ladder and gets unjammed.
            if (FrustrationMeter > 0 && MakingNetProgress)
            {
                FrustrationMeter = Mathf.Max(0, FrustrationMeter - Mathf.Max(1, KickStart.AIClockPeriod / 2));
            }
            ThrottleState = AIThrottleState.FullSpeed;
            if (direct.DriveDir == EDriveFacing.Backwards)
            {
                ThrottleState = AIThrottleState.ForceSpeed;
                DriveVar = -1;

                if (Urgency >= 0)
                    Urgency += KickStart.AIClockPeriod / 5f;
                if (UrgencyOverload > AIGlobals.UrgencyOverloadReconsideration)
                {
                    AIECore.AIMessage(tech: tank, ref hasMessaged, tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    EstTopSped = 1;
                    AvoidStuff = true;
                    UrgencyOverload = 0;
                }
                else if (useRush && dist > MaxObjectiveRange * 2)
                {
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = -1f;
                    Urgency += KickStart.AIClockPeriod / 5f;
                }
                else if (AIGlobals.UnjamUpdateStart < FrustrationMeter)
                {
                    IsTryingToUnjam = true;
                    FrustrationMeter += KickStart.AIClockPeriod;
                    if (AIGlobals.UnjamUpdateEnd < FrustrationMeter)
                    {   // REVISED: ceiling reset no longer hard-stops the core (SettleDown(false)) so the hand-off
                        // back to the standard AI keeps momentum instead of dead-stopping in place each cycle.
                        SettleDown(false);
                        return;
                    }
                    else if (AIGlobals.UnjamUpdateDrop < FrustrationMeter)
                    {
                        ControlCore.DriveToFacingTowards();
                        ForceSetBeam = false;
                        ThrottleState = AIThrottleState.ForceSpeed;
                        DriveVar = 1;
                    }
                    else
                    {
                        ControlCore.DriveToFacingTowards();
                        ThrottleState = AIThrottleState.ForceSpeed;
                        DriveVar = 1;
                        // REVISED: only force the build-beam when actually tipped over - the beam is for righting a
                        // flipped tech, not freeing an upright-but-stuck one (which only flickered beam up/drop).
                        ForceSetBeam = AIEBeam.IsTechTippedOver(tank, this);
                    }
                }
                else if (AIGlobals.UnjamUpdateFire < FrustrationMeter)
                {
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = -0.5f;
                }
                else
                {
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = -1f;
                }
            }
            else
            {
                ThrottleState = AIThrottleState.ForceSpeed;
                DriveVar = 1;

                if (Urgency >= 0)
                    Urgency += KickStart.AIClockPeriod / 5f;
                if (UrgencyOverload > AIGlobals.UrgencyOverloadReconsideration)
                {
                    AIECore.AIMessage(tech: tank, ref hasMessaged, tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    EstTopSped = 1;
                    AvoidStuff = true;
                    UrgencyOverload = 0;
                }
                else if (useRush && dist > MaxObjectiveRange * 2)
                {
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = 1f;
                    Urgency += KickStart.AIClockPeriod / 5f;
                }
                else if (AIGlobals.UnjamUpdateStart < FrustrationMeter)
                {
                    IsTryingToUnjam = true;
                    FrustrationMeter += KickStart.AIClockPeriod;
                    if (AIGlobals.UnjamUpdateEnd < FrustrationMeter)
                    {   // REVISED: ceiling reset no longer hard-stops the core (SettleDown(false)) so the hand-off
                        // back to the standard AI keeps momentum instead of dead-stopping in place each cycle.
                        SettleDown(false);
                        return;
                    }
                    else if (AIGlobals.UnjamUpdateDrop < FrustrationMeter)
                    {
                        ForceSetBeam = false;
                        ControlCore.DriveAwayFacingTowards();
                        ThrottleState = AIThrottleState.ForceSpeed;
                        DriveVar = -1;
                    }
                    else
                    {
                        ControlCore.DriveAwayFacingTowards();
                        ThrottleState = AIThrottleState.ForceSpeed;
                        DriveVar = -1;
                        // REVISED: only force the build-beam when actually tipped over (see backwards branch).
                        ForceSetBeam = AIEBeam.IsTechTippedOver(tank, this);
                    }
                }
                else if (AIGlobals.UnjamUpdateFire < FrustrationMeter)
                {
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = 0.5f;
                }
                else
                {
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = 1f;
                }
            }
        }
        private Transform GetObstruction(float searchRad)
        {
            List<Visible> ObstList;
            if (tank.rbody)
                ObstList = AIEPathing.ObstructionAwareness(tank.boundsCentreWorldNoCheck + SafeVelocity, this, searchRad);
            else
                ObstList = AIEPathing.ObstructionAwareness(tank.boundsCentreWorldNoCheck, this, searchRad);
            int bestStep = 0;
            float bestValue = 250000;
            int steps = ObstList.Count;
            if (steps <= 0)
            {
                return null;
            }
            for (int stepper = 0; steps > stepper; stepper++)
            {
                float temp = Mathf.Clamp((ObstList.ElementAt(stepper).centrePosition - tank.boundsCentreWorldNoCheck).sqrMagnitude - ObstList.ElementAt(stepper).Radius, 0, 500);
                if (bestValue > temp && temp != 0)
                {
                    bestStep = stepper;
                    bestValue = temp;
                }
            }
            return ObstList.ElementAt(bestStep).trans;
        }
        // REVISED: re-fetches the obstruction when the cached Obst has drifted out of 1.5x searchRad (was only
        // re-fetched when null). Restored the unconditional FIRE_ALL = true: the fire phase needs it to actually
        // shoot the obstacle clear - without it a tech stuck on destructible scenery could never blast free and
        // looped the beam-recovery. The Obsticle weapon state aims at Obst, so the fire is directed at the obstacle.
        public void RemoveObstruction(float searchRad = 12)
        {
            float staleRadSqr = (searchRad * 1.5f) * (searchRad * 1.5f);
            bool outOfRange = Obst != null
                && (Obst.position - tank.boundsCentreWorldNoCheck).sqrMagnitude > staleRadSqr;
            if (Obst == null || outOfRange)
            {
                Obst = GetObstruction(searchRad);
                Urgency += KickStart.AIClockPeriod / 5f;
            }
            FIRE_ALL = true;
        }
        // REVISED: SettleDown now does a full unjam reset - clears IsTryingToUnjam, ForceSetBeam, BeamTimeoutClock, FIRE_ALL, the Obsticle weapon state, and (unless stopCore=false) issues a Stop via SetCoreControlStop.
        public void SettleDown(bool stopCore = true)
        {
            UrgencyOverload = 0;
            Urgency = 0;
            FrustrationMeter = 0;
            Obst = null;
            IsTryingToUnjam = false;
            if (stopCore)
                SetCoreControlStop();
            ForceSetBeam = false;
            if (WeaponState == AIWeaponState.Obsticle)
                WeaponState = AIWeaponState.Normal;
            BeamTimeoutClock = 0;
            FIRE_ALL = false;
        }

        // REVISED: small-tech auto-fire now also requires aimRadius>0 and a non-Obsticle aim state, so a tech aiming at an obstruction (or with no aim) does not blind-fire.
        internal void AimAndFireWeapons(Vector3 aimWorld, float aimRadius)
        {
            if (maxBlockCount < AIGlobals.SmolTechBlockThreshold && aimRadius > 0f
                && ActiveAimState != AIWeaponState.Obsticle)
                FireAllWeapons();
            tank.control.TargetPositionWorld = aimWorld;
            tank.control.TargetRadiusWorld = aimRadius;
        }
        internal void FireAllWeapons() => tank.control.FireControl = true;
        internal void MaxBoost() => tank.control.BoostControlJets = true;
        // REVISED: MaxProps now sets BoostControlProps (was setting BoostControlJets, same as MaxBoost).
        internal void MaxProps() => tank.control.BoostControlProps = true;

        private static int TargetMask = Globals.inst.layerScenery.mask | Globals.inst.layerSceneryCoarse.mask |
            Globals.inst.layerSceneryFader.mask | Globals.inst.layerTerrain.mask | Globals.inst.layerLandmark.mask;
        private float LastWeapCheck = 0;
        private void SyncLineOfSight()
        {
            try
            {
                if (WeaponAimType == AIWeaponType.Unknown)
                {
                    int WeaponsNeedLOS = 0;
                    int WeaponsNoNeedLOS = 0;
                    foreach (var item in tank.blockman.IterateBlocks())
                    {
                        BlockDetails BD = BlockIndexer.GetBlockDetails(item.BlockType);
                        if (BD.IsWeapon && !BD.IsCab)
                        {
                            if (BD.IsMelee || BD.IsShortRanged)
                                WeaponsNoNeedLOS++;
                            else
                            {
                                var gun = item.GetComponent<ModuleWeaponGun>();
                                if (gun && gun.AimWithTrajectory() && gun.GetRange() > 500 &&
                                    (gun.m_SeekingRounds || gun.GetVelocity() < 60f))
                                    WeaponsNoNeedLOS++;
                                else
                                    WeaponsNeedLOS++;
                            }
                        }
                    }
                    if (WeaponsNeedLOS < WeaponsNoNeedLOS)
                        WeaponAimType = AIWeaponType.Indirect;
                    else
                        WeaponAimType = AIWeaponType.Direct;
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("CheckCanHitTarget() Critical error", e);
            }
        }

        // REVISED: drops the lastSuppressedState cache; now keys off the actual tank.Weapons.enabled state and, when disabling, just clears FireControl instead of forcing an aim at the tech's own center.
        internal void SuppressFiring(bool Disable)
        {
            try
            {
                if (tank.Weapons.enabled == Disable)
                {
                    DebugTAC_AI.Info(KickStart.ModID + ": AI " + tank.name + " of Team " + tank.Team + ":  Disabled weapons: " + Disable);
                    tank.Weapons.enabled = !Disable;
                }
                if (Disable)
                    tank.control.FireControl = false;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("SuppressFiring() Critical error", e);
            }
        }
        // REVISED: LOS is now debounced - it accumulates into _losBlockedStreak and only sets BlockedLineOfSight after LosBlockedStreakThreshold consecutive blocked checks; raycast distance is clamped to the target distance and skips when nearly coincident.
        // REVISED: target drop now uses two-tier range hysteresis (hard cap MaxCombatRange*RTSLockMaxRangeMultiplier, soft cap *CombatRangeRetentionMult honoured only when not PreserveEnemyTarget) and goes through ReleaseTarget.
        private void CheckEnemyAndAiming()
        {
            if (LastWeapCheck < Time.time)
            {
                LastWeapCheck = Time.time + AIGlobals.TargetValidationDelay;
                SyncLineOfSight();
                bool wasBlockedThisCheck = false;
                if (lastEnemyGet)
                {
                    if (!lastEnemyGet.isActive || lastEnemyGet.tank.blockman.blockCount == 0 ||
                        !Tank.IsEnemy(tank.Team, lastEnemyGet.tank.Team))
                    {
                        DebugTAC_AI.LogTargeting(tank, "Target released CheckEnemyAndAiming: dead/wrong-team");
                        ReleaseTarget();
                    }
                    else
                    {
                        Vector3 pos = tank.boundsCentreWorld + Vector3.up;
                        Vector3 vec = lastEnemy.tank.boundsCentreWorld - pos;
                        float targetDistance = vec.magnitude;
                        if (NeedsLineOfSight && targetDistance > 0.05f)
                        {
                            Vector3 dir = vec / targetDistance;
                            if (Physics.Raycast(pos, dir, out RaycastHit hit,
                                Mathf.Min(targetDistance, MaxCombatRange), TargetMask, QueryTriggerInteraction.Ignore)
                                && hit.distance < targetDistance)
                            {
                                wasBlockedThisCheck = true;
                            }
                        }
                        float hardCap = MaxCombatRange * AIGlobals.RTSLockMaxRangeMultiplier;
                        if (targetDistance > hardCap
                            || (targetDistance > MaxCombatRange * AIGlobals.CombatRangeRetentionMult && !PreserveEnemyTarget))
                        {
                            DebugTAC_AI.LogTargeting(tank, "Target released CheckEnemyAndAiming: out of range");
                            ReleaseTarget();
                        }
                    }
                }
                if (wasBlockedThisCheck)
                {
                    _losBlockedStreak++;
                    if (_losBlockedStreak >= AIGlobals.LosBlockedStreakThreshold)
                        BlockedLineOfSight = true;
                }
                else
                {
                    _losBlockedStreak = 0;
                    BlockedLineOfSight = false;
                }
            }
        }
        // REVISED: adopting the player's manual target now validates team/teammate, degrades relations (DegradeRelations) when the player fires on a not-yet-enemy alterable team, and only locks pursuit (SetPursuit force) once the target is actually an enemy - instead of unconditionally setting lastEnemy to the player's target.
        // REVISED: air-vs-ground branch keyed on AICore IsFixedWing instead of AIControllerAir.FlyStyle==Aircraft.
        public Visible TryRefreshEnemyAllied()
        {
            if ((bool)lastPlayer)
            {
                Tank playerTank = lastPlayer.tank;
                Visible playerTarget = playerTank.Weapons.GetManualTarget();
                if (playerTarget && playerTarget.tank != null && playerTarget.isActive &&
                    playerTarget.tank.CentralBlock &&
                    playerTarget.tank.Team != tank.Team &&
                    !ManBaseTeams.IsTeammate(tank.Team, playerTarget.tank.Team))
                {
                    int targTeam = playerTarget.tank.Team;
                    if (!ManBaseTeams.IsEnemy(tank.Team, targTeam) &&
                        playerTank.control.FireControl &&
                        ManBaseTeams.CanAlterRelations(tank.Team, targTeam))
                    {
                        ManBaseTeams.DegradeRelations(tank.Team, targTeam, AIGlobals.DamageAngerDropRelations);
                    }
                    if (ManBaseTeams.IsEnemy(tank.Team, targTeam))
                    {
                        Provoked = AIGlobals.ProvokeTime;
                        SetPursuit(playerTarget, force: true);
                        return lastEnemy;
                    }
                }
            }
            if (MovementController is AIControllerAir air && air.AICore is IAirMovementAICore aircore && aircore.IsFixedWing)
            {
                lastEnemy = FindEnemyAir(false);
            }
            else
                lastEnemy = FindEnemy(false);
            return lastEnemy;
        }
        // REVISED: dropped the unused `pos` ranked-target param (1st/2nd/3rd-closest selection removed downstream); air branch keyed on IsFixedWing.
        public Visible TryRefreshEnemyEnemy(bool InvertBullyPriority)
        {
            if (MovementController is AIControllerAir air && air.AICore is IAirMovementAICore aircore && aircore.IsFixedWing)
            {
                lastEnemy = FindEnemyAir(InvertBullyPriority);
            }
            else
                lastEnemy = FindEnemy(InvertBullyPriority);
            return lastEnemy;
        }
        // REVISED: rewritten as early-return cascade and now owns the Provoked decrement (Provoked-=AIClockPeriod moved here). Adds a LOS-lost grace window: while Provoke=0 and the held target's LOS is blocked, it holds for LOSLostGraceTime before EndPursuit, instead of dropping immediately - so a target behind brief cover is kept.
        private float _losLostGraceTimer = 0f;
        private void UpdateTargetCombatFocus()
        {
            if (Provoked > 0)
            {
                Provoked -= KickStart.AIClockPeriod;
                _losLostGraceTimer = 0f;
                return;
            }
            Provoked = 0;

            if (!lastEnemyGet)
            {
                _losLostGraceTimer = 0f;
                EndPursuit();
                return;
            }
            if (!InRangeOfTarget(MaxCombatRange))
            {
                _losLostGraceTimer = 0f;
                EndPursuit();
                return;
            }
            if (NeedsLineOfSight && BlockedLineOfSight)
            {
                _losLostGraceTimer += KickStart.AIClockPeriod * Time.fixedDeltaTime;
                if (_losLostGraceTimer >= AIGlobals.LOSLostGraceTime)
                {
                    _losLostGraceTimer = 0f;
                    EndPursuit();
                }
            }
            else
            {
                _losLostGraceTimer = 0f;
            }
        }
        internal float UpdateEnemyDistance(Vector3 enemyPosition)
        {
            _lastCombatRange = (enemyPosition - tank.boundsCentreWorldNoCheck).magnitude;
            return _lastCombatRange;
        }
        internal float IgnoreEnemyDistance()
        {
            _lastCombatRange = float.MaxValue;
            return _lastCombatRange;
        }
        // REVISED: renamed from DetermineCombat and now RETURNS the retreat verdict (callers do `Retreat = DetermineRetreatPosture()`) instead of writing Retreat as a side effect; it no longer acquires targets, only drops a now-friendly held target and decides retreat.
        private bool DetermineRetreatPosture()
        {
            if (lastEnemyGet?.tank)
                if (!Tank.IsEnemy(tank.Team, lastEnemyGet.tank.Team))
                {
                    ReleaseTarget();
                }
            if (AIECore.RetreatingTeams.Contains(tank.Team))
                return true;

#if !STEAM
                if (KickStart.isAnimeAIPresent)
                {
                    if (AnimeAICompat.PollShouldRetreat(tank, this, out bool verdict))
                        return verdict;
                }
#endif

            bool DoNotEngage = false;
            if (DediAI == AIType.Assault && lastBasePos.IsNotNull())
            {
                if (MaxCombatRange * 2 < (lastBasePos.position - tank.boundsCentreWorldNoCheck).magnitude)
                {
                    DoNotEngage = true;
                }
                else if (AdvancedAI)
                {
                    if (DamageThreshold > 30)
                    {
                        DoNotEngage = true;
                    }
                }
            }
            else if (lastPlayer.IsNotNull())
            {
                if (DriverType == AIDriverType.Pilot)
                {
                    if (!RTSControlled && MaxCombatRange * 4 < (lastPlayer.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).magnitude)
                    {
                        DoNotEngage = true;
                    }
                    else if (AdvancedAI)
                    {
                        if (DamageThreshold > 20)
                        {
                            DoNotEngage = true;
                        }
                    }
                }
                else if (DediAI != AIType.Assault)
                {
                    if (!RTSControlled && MaxCombatRange < (lastPlayer.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).magnitude)
                    {
                        DoNotEngage = true;
                    }
                    else if (AdvancedAI)
                    {
                        if (DamageThreshold > 30)
                        {
                            DoNotEngage = true;
                        }
                    }
                }
            }
            return DoNotEngage;
        }
        private void DetermineRetreatPostureEnemy()
        {
            Retreat = AIGlobals.IsNotAttract && AIECore.RetreatingTeams.Contains(tank.Team);

#if !STEAM
                if (KickStart.isAnimeAIPresent)
                {
                    if (AnimeAICompat.PollShouldRetreat(tank, this, out bool verdict))
                    {
                        Retreat = verdict;
                        return;
                    }
                }
#endif
        }

        private float lastTargetGatherTime = 0;
        // REVISED: target-scan cache is now range-aware (re-gathers if the requested range exceeds what was last cached) and refreshes faster in combat (TargetCacheRefreshIntervalCombat when Provoked/has-target vs idle TargetCacheRefreshInterval); InvalidateTargetCache forces a re-scan (called from OnHit).
        private float lastTargetGatherRangeSqr = 0;
        private List<Tank> targetCache = new List<Tank>();
        internal void InvalidateTargetCache() { lastTargetGatherTime = 0; }
        private List<Tank> GatherTargetTechsInRange(float gatherRangeSqr)
        {
            if (lastTargetGatherTime > Time.time && gatherRangeSqr <= lastTargetGatherRangeSqr)
            {
                return targetCache;
            }
            float interval = (Provoked > 0f || lastEnemyGet?.tank)
                ? AIGlobals.TargetCacheRefreshIntervalCombat
                : AIGlobals.TargetCacheRefreshInterval;
            lastTargetGatherTime = Time.time + interval;
            lastTargetGatherRangeSqr = gatherRangeSqr;
            targetCache.Clear();
            foreach (Tank cTank in TankAIManager.GetTargetTanks(tank.Team))
            {
                if (cTank != tank && cTank.visible.isActive)
                {
                    float dist = (cTank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).sqrMagnitude;
                    if (dist < gatherRangeSqr)
                    {
                        targetCache.Add(cTank);
                    }
                }
            }
            return targetCache;
        }
        // REVISED: dropped the `pos` ranked-target param and the whole 1st/2nd/3rd-closest pos-switch machinery, plus the UseVanillaTargetFetching shortcut; held-target retain now uses the same two-tier range hysteresis (hard/soft cap) as CheckEnemyAndAiming and Random mode picks uniformly from in-range candidates.
        private Visible FindEnemy(bool InvertBullyPriority)
        {
            Visible target = lastEnemyGet;

            float TargetRangeSqr = MaxCombatRange * MaxCombatRange;
            Vector3 scanCenter = tank.boundsCentreWorldNoCheck;

            if (target?.tank)
            {
                if (!target.isActive || !ManBaseTeams.IsEnemy(tank.Team, target.tank.Team))
                {
                    target = null;
                }
                else
                {
                    float sqr = (target.tank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                    bool pastHardCap = sqr > TargetRangeSqr * AIGlobals.RTSLockMaxRangeMultiplierSqr;
                    bool pastSoftCap = sqr > TargetRangeSqr * AIGlobals.CombatRangeRetentionMultSqr;
                    if (pastHardCap || (pastSoftCap && !PreserveEnemyTarget))
                    {
                        DebugTAC_AI.LogTargeting(tank, "Target released FindEnemy: out of range");
                        target = null;
                        EndPursuit();
                    }
                    else if (PreserveEnemyTarget || NextFindTargetTime <= Time.time)
                    {
                        return target;
                    }
                }
            }

            if (AttackMode == EAttackMode.Random)
            {
                List<Tank> techs = GatherTargetTechsInRange(TargetRangeSqr);
                var valid = new List<Visible>(techs.Count);
                for (int step = 0; step < techs.Count; step++)
                {
                    Tank cTank = techs[step];
                    if (cTank == tank || !cTank.visible.isActive) continue;
                    if (!ManBaseTeams.IsEnemy(tank.Team, cTank.Team)) continue;
                    float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                    if (dist < TargetRangeSqr) valid.Add(cTank.visible);
                }
                if (valid.Count > 0)
                    target = valid[UnityEngine.Random.Range(0, valid.Count)];
                NextFindTargetTime = Time.time + AIGlobals.PestererSwitchDelay;
            }
            else if (AttackMode == EAttackMode.Strong)
            {
                List<Tank> techs = GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                if (InvertBullyPriority)
                {
                    int BlockCount = 0;
                    for (int step = 0; step < launchCount; step++)
                    {
                        Tank cTank = techs.ElementAt(step);
                        if (cTank != tank && ManBaseTeams.IsEnemy(tank.Team, cTank.Team))
                        {
                            float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                            if (cTank.blockman.blockCount > BlockCount && dist < TargetRangeSqr)
                            {
                                BlockCount = cTank.blockman.blockCount;
                                target = cTank.visible;
                            }
                        }
                    }
                }
                else
                {
                    int BlockCount = 262144;
                    for (int step = 0; step < launchCount; step++)
                    {
                        Tank cTank = techs.ElementAt(step);
                        if (cTank != tank && ManBaseTeams.IsEnemy(tank.Team, cTank.Team))
                        {
                            float dist = (cTank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).sqrMagnitude;
                            if (cTank.blockman.blockCount < BlockCount && dist < TargetRangeSqr)
                            {
                                BlockCount = cTank.blockman.blockCount;
                                target = cTank.visible;
                            }
                        }
                    }
                }
                NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
            }
            else
            {
                NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
                if (AttackMode == EAttackMode.Chase && target != null)
                {
                    if (target.isActive)
                        return target;
                }

                List<Tank> techs = GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                for (int step = 0; step < launchCount; step++)
                {
                    Tank cTank = techs.ElementAt(step);
                    if (cTank != tank && ManBaseTeams.IsEnemy(tank.Team, cTank.Team))
                    {
                        float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                        if (dist < TargetRangeSqr)
                        {
                            TargetRangeSqr = dist;
                            target = cTank.visible;
                        }
                    }
                }
            }
            return target;
        }
        // REVISED: new extracted air-scan helpers. They replace FindEnemyAir's inline altitude-sorted scans; FindEnemyAir now calls them twice (airborne-first, then ground) so air targets are preferred without the old per-tech altitudeHigh comparison.
        private Visible ScanAirborneRandom(List<Tank> techs, int launchCount, bool preferAirborne, ref float TargetRangeSqr, Vector3 scanCenter)
        {
            var valid = new List<Visible>(launchCount);
            for (int step = 0; step < launchCount; step++)
            {
                Tank cTank = techs[step];
                if (cTank == tank || !ManBaseTeams.IsEnemy(tank.Team, cTank.Team)) continue;
                if (cTank.visible == null || !cTank.visible.isActive) continue;
                if (AIEPathing.AboveHeightFromGround(cTank.boundsCentreWorldNoCheck) != preferAirborne) continue;
                float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                if (dist < TargetRangeSqr) valid.Add(cTank.visible);
            }
            if (valid.Count == 0) return null;
            return valid[UnityEngine.Random.Range(0, valid.Count)];
        }
        private Visible ScanAirborneStrong(List<Tank> techs, int launchCount, bool preferAirborne, bool invertBully, ref float TargetRangeSqr, Vector3 scanCenter)
        {
            Visible target = null;
            int BlockCount = invertBully ? 0 : 262144;
            for (int step = 0; step < launchCount; step++)
            {
                Tank cTank = techs.ElementAt(step);
                if (cTank == tank || !ManBaseTeams.IsEnemy(tank.Team, cTank.Team))
                    continue;
                bool isAirborne = AIEPathing.AboveHeightFromGround(cTank.boundsCentreWorldNoCheck);
                if (isAirborne != preferAirborne)
                    continue;
                float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                if (dist >= TargetRangeSqr)
                    continue;
                int bc = cTank.blockman.blockCount;
                bool better = invertBully ? (bc > BlockCount) : (bc < BlockCount);
                if (better)
                {
                    BlockCount = bc;
                    target = cTank.visible;
                }
            }
            return target;
        }
        // REVISED: dropped the `pos` param and the inline altitude-ranked scans; scanCenter is now DodgeSphereCenter throughout, held-target retain uses the two-tier range hysteresis, and Random/Strong delegate to ScanAirborne* (airborne-preferred, then ground).
        private Visible FindEnemyAir(bool InvertBullyPriority)
        {
            Visible target = lastEnemyGet;

            float TargetRangeSqr = MaxCombatRange * MaxCombatRange;
            Vector3 scanCenter = DodgeSphereCenter;

            if (target != null)
            {
                if (!target.isActive || !ManBaseTeams.IsEnemy(tank.Team, target.tank.Team))
                {
                    target = null;
                }
                else
                {
                    float sqr = (target.tank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                    bool pastHardCap = sqr > TargetRangeSqr * AIGlobals.RTSLockMaxRangeMultiplierSqr;
                    bool pastSoftCap = sqr > TargetRangeSqr * AIGlobals.CombatRangeRetentionMultSqr;
                    if (pastHardCap || (pastSoftCap && !PreserveEnemyTarget))
                    {
                        DebugTAC_AI.LogTargeting(tank, "Target released FindEnemy: out of range");
                        target = null;
                        EndPursuit();
                    }
                    else if (PreserveEnemyTarget || NextFindTargetTime <= Time.time)
                    {
                        return target;
                    }
                }
            }
            if (AttackMode == EAttackMode.Random)
            {
                List<Tank> techs = GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                target = ScanAirborneRandom(techs, launchCount, true, ref TargetRangeSqr, scanCenter)
                      ?? ScanAirborneRandom(techs, launchCount, false, ref TargetRangeSqr, scanCenter);
                NextFindTargetTime = Time.time + AIGlobals.PestererSwitchDelay;
            }
            else if (AttackMode == EAttackMode.Strong)
            {
                List<Tank> techs = GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                if (InvertBullyPriority)
                {
                    target = ScanAirborneStrong(techs, launchCount, preferAirborne: false, invertBully: true, ref TargetRangeSqr, scanCenter)
                          ?? ScanAirborneStrong(techs, launchCount, preferAirborne: true, invertBully: true, ref TargetRangeSqr, scanCenter);
                }
                else
                {
                    target = ScanAirborneStrong(techs, launchCount, preferAirborne: true, invertBully: false, ref TargetRangeSqr, scanCenter)
                          ?? ScanAirborneStrong(techs, launchCount, preferAirborne: false, invertBully: false, ref TargetRangeSqr, scanCenter);
                }
                NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
            }
            else
            {
                NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
                if (AttackMode == EAttackMode.Chase && target != null)
                {
                    if (target.isActive)
                        return target;
                }

                List<Tank> techs = GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                for (int step = 0; step < launchCount; step++)
                {
                    Tank cTank = techs.ElementAt(step);
                    if (cTank != tank && ManBaseTeams.IsEnemy(tank.Team, cTank.Team))
                    {
                        float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                        if (dist < TargetRangeSqr)
                        {
                            TargetRangeSqr = dist;
                            target = cTank.visible;
                        }
                    }
                }
            }
            return target;
        }
        public Vector3 InterceptTargetDriving(Visible targetTank)
        {
            if (targetTank.IsNull() || targetTank.tank.IsNull())
                return tank.boundsCentreWorldNoCheck;
            if (AdvancedAI)
            {
                return RoughPredictTarget(targetTank.tank);
            }
            else
                return targetTank.tank.boundsCentreWorldNoCheck;
        }
        private const float MaxBoundsVelo = 350;
        private static Vector3 lowMaxBoundsVelo = -new Vector3(MaxBoundsVelo, MaxBoundsVelo, MaxBoundsVelo);
        private static Vector3 highMaxBoundsVelo = new Vector3(MaxBoundsVelo, MaxBoundsVelo, MaxBoundsVelo);
        // REVISED: null-guards the target and now applies lead prediction whenever lastCombatRange is finite (lastCombatRange < float.MaxValue) instead of only within EnemyExtendActionRange, so long-range shots also lead.
        public Vector3 RoughPredictTarget(Tank targetTank)
        {
            if (targetTank.IsNull())
                return tank.boundsCentreWorldNoCheck;
            if (DriverType != AIDriverType.Stationary && targetTank.rbody.IsNotNull())
            {
                var velo = targetTank.rbody.velocity;
                if (!velo.IsNaN() && !float.IsInfinity(velo.x)
                    && !float.IsInfinity(velo.z) && !float.IsInfinity(velo.y)
                    && lastCombatRange < float.MaxValue)
                {
                    return targetTank.boundsCentreWorldNoCheck + (velo.Clamp(lowMaxBoundsVelo, highMaxBoundsVelo) *
                        (lastCombatRange * AIGlobals.TargetVelocityLeadPredictionMulti));
                }
            }
            return targetTank.boundsCentreWorldNoCheck;
        }

        private void ManageAILockOn()
        {
            switch (ActiveAimState)
            {
                case AIWeaponState.Enemy:
                    if (lastEnemyGet.IsNotNull())
                    {
                        if (!DebugTAC_AI.NoLogTargeting)
                            DebugTAC_AI.LogTargeting(tank, "Overriding targeting to aim at " + lastEnemy.name + " pos " + lastEnemy.tank.boundsCentreWorldNoCheck);
                        lastLockOnTarget = lastEnemyGet;
                    }
                    break;
                case AIWeaponState.Obsticle:
                    if (Obst != null && Obst.gameObject)
                    {
                        var resTarget = Obst.GetComponent<Visible>();
                        if (resTarget)
                        {
                            DebugTAC_AI.LogTargeting(tank, "Overriding targeting to aim at obstruction");
                            lastLockOnTarget = resTarget;
                        }
                    }
                    break;
                case AIWeaponState.Mimic:
                    if (lastCloseAlly.IsNotNull())
                    {
                        DebugTAC_AI.LogTargeting(tank, "Overriding targeting to aim at player's target");
                        var helperAlly = lastCloseAlly.GetHelperInsured();
                        if (helperAlly.ActiveAimState == AIWeaponState.Enemy)
                            lastLockOnTarget = helperAlly.lastEnemyGet;
                    }
                    break;
            }

            if (lastLockOnTarget)
            {
                bool playerAim = tank.PlayerFocused && !ManWorldRTS.PlayerIsInRTS;
                if (!lastLockOnTarget.isActive || (playerAim && !tank.control.FireControl))
                {
                    lastLockOnTarget = null;
                    return;
                }
                if (lastLockOnTarget == tank.visible)
                {
                    DebugTAC_AI.Assert("Tech " + (tank.name.NullOrEmpty() ? "<NULL>" : tank.name) + " tried to lock-on to itself!!!");
                    lastLockOnTarget = null;
                    return;
                }
                if (!playerAim && lastLockOnTarget.resdisp && ActiveAimState != AIWeaponState.Obsticle)
                {
                    lastLockOnTarget = null;
                    return;
                }

                float maxDist;
                if (ManNetwork.IsNetworked)
                    maxDist = tank.Weapons.m_ManualTargetingSettingsMAndKB.m_ManualTargetingRadiusMP;
                else
                    maxDist = tank.Weapons.m_ManualTargetingSettingsMAndKB.m_ManualTargetingRadiusSP;
                if (_lastCombatRange > maxDist)
                {
                    lastLockOnTarget = null;
                }
            }
        }

        public int Provoked = 0;
        public bool KeepEnemyFocus { get; private set; } = false;

        // REVISED: new per-source cumulative-damage accumulator. Each attacker has a decaying bucket (DamageAlertDecayPerSec); returns true once a source's accumulated damage crosses DamageAlertCumulativeThreshold, so sustained small hits can provoke even when no single hit exceeds DamageAlertThreshold.
        private struct DamageBucket { public float Accumulator; public float LastUpdateTime; }
        private readonly Dictionary<int, DamageBucket> _damageBuckets = new Dictionary<int, DamageBucket>(4);
        internal bool AccumulateAndCheckThreat(Tank source, float damage)
        {
            if (source == null || damage <= 0f) return false;
            int id = source.GetInstanceID();
            float now = Time.time;
            float acc = 0f;
            if (_damageBuckets.TryGetValue(id, out var bucket))
            {
                float elapsed = now - bucket.LastUpdateTime;
                acc = Mathf.Max(0f, bucket.Accumulator - AIGlobals.DamageAlertDecayPerSec * elapsed);
            }
            acc += damage;
            if (acc >= AIGlobals.DamageAlertCumulativeThreshold)
            {
                _damageBuckets.Remove(id);
                return true;
            }
            _damageBuckets[id] = new DamageBucket { Accumulator = acc, LastUpdateTime = now };
            return false;
        }
        internal void ResetDamageAccumulator() => _damageBuckets.Clear();
        public enum SetPursuitResult : byte
        {
            Set            = 0,
            AlreadyTarget  = 1,
            BlockedByLock  = 2,
            NullTarget     = 3,
            InvalidTank    = 4,
        }
        // REVISED: SetPursuit reworked over TrySetPursuit which returns a SetPursuitResult and takes a `force` flag. A held KeepEnemyFocus target now blocks a new pursuit (BlockedByLock) unless force=true; previously KeepEnemyFocus simply short-circuited and only-null cleared focus. Setting a target also MarkOperatorDirty.
        public bool SetPursuit(Visible target) => IsSetPursuitSuccess(TrySetPursuit(target, false));
        public bool SetPursuit(Visible target, bool force) => IsSetPursuitSuccess(TrySetPursuit(target, force));
        private static bool IsSetPursuitSuccess(SetPursuitResult r) =>
            r == SetPursuitResult.Set || r == SetPursuitResult.AlreadyTarget;
        public SetPursuitResult TrySetPursuit(Visible target) => TrySetPursuit(target, false);
        public SetPursuitResult TrySetPursuit(Visible target, bool force)
        {
            if (target == null)
            {
                if (KeepEnemyFocus) KeepEnemyFocus = false;
                return SetPursuitResult.NullTarget;
            }
            if (!(bool)target.tank) return SetPursuitResult.InvalidTank;
            if (target == lastEnemy)
            {
                ControlOperator.SetLastDest(target.tank.boundsCentreWorldNoCheck);
                MarkOperatorDirty();
                KeepEnemyFocus = true;
                return SetPursuitResult.AlreadyTarget;
            }
            if (KeepEnemyFocus && !force) return SetPursuitResult.BlockedByLock;
            lastEnemy = target;
            ControlOperator.SetLastDest(target.tank.boundsCentreWorldNoCheck);
            MarkOperatorDirty();
            KeepEnemyFocus = true;
            return SetPursuitResult.Set;
        }
        public void EndPursuit()
        {
            if (KeepEnemyFocus)
            {
                KeepEnemyFocus = false;
            }
        }
        // REVISED: new hard target-clear. Unlike EndPursuit (which only drops the KeepEnemyFocus lock), ReleaseTarget nulls lastEnemy outright (and the lastEnemy setter then also clears KeepEnemyFocus).
        public void ReleaseTarget()
        {
            lastEnemy = null;
        }
        public bool InRangeOfTarget(float distance)
        {
            return InRangeOfTarget(lastEnemyGet, distance);
        }
        public bool InRangeOfTarget(Visible target, float distance)
        {
            return (target.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).sqrMagnitude <= distance * distance;
        }


        private static MethodInfo MI = typeof(TechAnchors).GetMethod("ConfigureJoint", BindingFlags.NonPublic | BindingFlags.Instance);
        public void TryInsureAutoAnchor()
        {
            if (!tank.IsAnchored && CanAnchorNow)
            {
                AnchorState = AIAnchorState.AnchorAuto;
            }
        }
        public void TryInsureManualAnchor()
        {
            if (!tank.IsAnchored && CanAnchorNow)
            {
                AnchorState = AIAnchorState.Anchor;
            }
        }
        public void AnchorIgnoreChecks(bool forced = false)
        {
            if (forced)
            {
                DebugTAC_AI.LogDevOnlyAssert(KickStart.ModID + ": AI " + tank.name + ":  TryReallyAnchor(true)");
                AnchorState = AIAnchorState.ForceAnchor;
            }
            else
                AnchorState = AIAnchorState.Anchor;
        }
        public void AdjustAnchors()
        {
            bool prevAnchored = tank.IsAnchored;
            DoUnAnchor();
            if (!tank.IsAnchored)
            {
                AnchorIgnoreChecks(prevAnchored);
            }
        }

        public void Unanchor()
        {
            AnchorState = AIAnchorState.Unanchor;
            AnchorStateAIInsure = true;
        }

        public void AnchorStatic()
        {
            AnchorState = AIAnchorState.AnchorStaticAI;
        }

        private void DoAnchorStatic()
        {
            SetDriverType(AIDriverType.Stationary);
        }

        private void DoAnchor(bool forced)
        {
            if (!tank.IsAnchored && anchorAttempts <= AIGlobals.MaxAnchorAttempts)
            {
                anchorAttempts++;
                tank.Anchors.TryAnchorAll(true);
                if (tank.Anchors.NumIsAnchored > 0)
                {
                    ExpectAITampering = true;
                    anchorAttempts = 0;
                    if (AnchorState == AIAnchorState.AnchorStaticAI)
                        DoAnchorStatic();
                    AnchorState = AIAnchorState.Anchored;
                    return;
                }

                bool worked = false;
                Vector3 startPosTrans = tank.trans.position;
                tank.FixupAnchors(false);
                if (tank.Anchors.NumIsAnchored > 0)
                {
                    ExpectAITampering = true;
                    anchorAttempts = 0;
                    if (AnchorState == AIAnchorState.AnchorStaticAI)
                        DoAnchorStatic();
                    AnchorState = AIAnchorState.Anchored;
                    return;
                }
                Vector3 startPos = tank.visible.centrePosition;
                Quaternion tankFore = AIGlobals.LookRot(tank.trans.forward.SetY(0).normalized, Vector3.up);
                tank.visible.Teleport(startPos, tankFore, true, true);
                for (int step = 0; step < 6; step++)
                {
                    if (!tank.IsAnchored)
                    {
                        Vector3 newPos = startPos + Vector3.down;
                        newPos.y += step / 4f;
                        tank.visible.Teleport(newPos, tankFore, false, true);
                        tank.Anchors.TryAnchorAll();
                    }
                    if (tank.IsAnchored)
                    {
                        worked = true;
                        break;
                    }
                    tank.FixupAnchors(true);
                }
                var anchors = tank.blockman.IterateBlockComponents<ModuleAnchor>();
                if (!worked && anchors.Count() > 0)
                {
                    if (AIGlobals.IsAttract || forced)
                    {
                        DebugTAC_AI.Assert(true, (AIGlobals.IsAttract ? "(ATTRACT BASE)" : "(FORCED)") + " screw you i'm anchoring anyways, I don't give a f*bron about your anchor checks!");
                        foreach (var item in anchors)
                        {
                            item.AnchorToGround();
                            if (item.AnchorGeometryActive)
                            {
                                tank.Anchors.AddAnchor(item);
                            }
                        }
                        tank.grounded = true;
                        MI.Invoke(tank.Anchors, new object[0]);
                        worked = true;
                    }
                    else
                    {
                        tank.trans.position = startPosTrans + (Vector3.up * 0.1f);
                    }
                }
                if (worked)
                {
                    ExpectAITampering = true;
                    anchorAttempts = 0;
                    if (AnchorState == AIAnchorState.AnchorStaticAI)
                        DoAnchorStatic();
                    RecalibrateMovementAIController();
                }
                ExpectAITampering = true;
                tank.visible.Teleport(startPos, tankFore, true, true);
                // REVISED: a failed anchor attempt now resets anchorAttempts so the tech can retry from scratch later rather than staying stuck at the attempt cap.
                if (!worked)
                    anchorAttempts = 0;
                AnchorState = AIAnchorState.None;
            }
        }
        private void DoUnAnchor()
        {
            if (tank.IsAnchored || tank.Anchors.NumIsAnchored > 0)
            {
                tank.Anchors.UnanchorAll(true);
                if (!tank.IsAnchored && AIAlign == AIAlignment.Player)
                {
                    WakeAIForChange();
                }
            }
            // REVISED: AnchorState is now always reset to None (moved out of the `was anchored` branch), so the unanchor state is cleared even when there was nothing to unanchor.
            AnchorState = AIAnchorState.None;
            anchorAttempts = 0;
        }

        public TankBlock HeldBlock => heldBlock;
        private TankBlock heldBlock;
        private Vector3 blockHoldPos = Vector3.zero;
        private Quaternion blockHoldRot = Quaternion.identity;
        private bool blockHoldOffset = false;
        private void UpdateBlockHold()
        {
            if (heldBlock?.visible)
            {
                if (!ManNetwork.IsNetworked)
                {
                    if (!heldBlock.visible.isActive)
                    {
                        try
                        {
                            DropBlock();
                        }
                        catch { }
                        heldBlock = null;
                    }
                    else if (heldBlock.visible.InBeam || heldBlock.IsAttached)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "'s grabbed block was thefted!");
                        DropBlock();
                    }
                    else if (ManPointer.inst.targetVisible == heldBlock.visible)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "'s grabbed block was grabbed by player!");
                        DropBlock();
                    }
                    else
                    {
                        Vector3 moveVec;
                        if (blockHoldOffset)
                        {
                            moveVec = tank.trans.TransformPoint(blockHoldPos) - heldBlock.transform.position;
                            float dotVal = Vector3.Dot(moveVec.normalized, Vector3.down);
                            if (dotVal > 0.75f)
                                moveVec.y += moveVec.ToVector2XZ().magnitude / 3;
                            else
                            {
                                moveVec.y -= moveVec.ToVector2XZ().magnitude / 3;
                            }
                            Vector3 finalPos = heldBlock.transform.position;
                            finalPos += moveVec / ((100 / AIGlobals.BlockAttachDelay) * Time.fixedDeltaTime);
                            if (finalPos.y < tank.trans.TransformPoint(blockHoldPos).y)
                                finalPos.y = tank.trans.TransformPoint(blockHoldPos).y;
                            heldBlock.transform.position = finalPos;
                            if (heldBlock.rbody)
                            {
                                if (tank.rbody)
                                    heldBlock.rbody.velocity = SafeVelocity.SetY(0);
                                heldBlock.rbody.AddForce(-(TankAIManager.GravVector * heldBlock.AverageGravityScaleFactor), ForceMode.Acceleration);
                                Vector3 forward = tank.trans.TransformDirection(blockHoldRot * Vector3.forward);
                                Vector3 up = tank.trans.TransformDirection(blockHoldRot * Vector3.up);
                                Quaternion rotChangeWorld = AIGlobals.LookRot(forward, up);
                                heldBlock.rbody.MoveRotation(Quaternion.RotateTowards(heldBlock.transform.rotation, rotChangeWorld,
                                    (360 / AIGlobals.BlockAttachDelay) * Time.fixedDeltaTime));
                            }
                            heldBlock.visible.SetLockTimout(Visible.LockTimerTypes.Interactible, 0.25f);
                        }
                        else
                        {
                            moveVec = tank.boundsCentreWorldNoCheck + (Vector3.up * (lastTechExtents + 3)) - heldBlock.visible.centrePosition;
                            moveVec = Vector3.ClampMagnitude(moveVec * 4, AIGlobals.ItemGrabStrength);
                            if (heldBlock.rbody)
                                heldBlock.rbody.AddForce(moveVec - (TankAIManager.GravVector * heldBlock.AverageGravityScaleFactor), ForceMode.Acceleration);
                            heldBlock.visible.SetLockTimout(Visible.LockTimerTypes.Interactible, 0.25f);
                        }
                    }
                }
                else if (ManNetwork.IsHost)
                {
                    if (!heldBlock.visible.isActive)
                    {
                        DropBlock();
                    }
                    else if (heldBlock.visible.InBeam || heldBlock.IsAttached)
                    {
                        DropBlock();
                    }
                    else
                    {
                        if (tank.CentralBlock)
                            heldBlock.visible.centrePosition = tank.CentralBlock.centreOfMassWorld;
                        else
                            heldBlock.visible.centrePosition = tank.boundsCentreWorldNoCheck;
                    }
                }
            }
        }
        internal bool HoldBlock(Visible TB)
        {
            if (!TB)
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": Tech " + tank.name + " attempted to illegally grab NULL Visible");
            }
            else if (ManNetwork.IsNetworked)
            {
                if (TB.block && Singleton.playerTank)
                {
                    TB.Teleport(Singleton.playerTank.boundsCentreWorld, Quaternion.identity);
                }
            }
            else if (TB.block)
            {
                if (TB.isActive)
                {
                    if (TB.InBeam)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "'s target block was thefted by a tractor beam!");
                    }
                    else
                    {
                        if (TB.rbody)
                        {
                            ColliderSwapper CS;
                            if (heldBlock && heldBlock != TB.block)
                            {
                                DropBlock();
                            }
                            blockHoldOffset = false;
                            if (ManNetwork.IsNetworked)
                                return true;
                            heldBlock = TB.block;
                            CS = heldBlock.GetComponent<ColliderSwapper>();
                            if (CS)
                                CS.EnableCollision(false);

                            return true;
                        }
                        else
                            DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "'s target block HAS NO RBODY");
                    }
                }
            }
            else
                DebugTAC_AI.Assert(true, KickStart.ModID + ": Tech " + tank.name + " attempted to illegally grab "
                    + (!TB.name.NullOrEmpty() ? TB.name : "NULL")
                    + " of type " + TB.type + " when they are only allowed to grab blocks");
            return false;
        }
        internal bool HoldBlock(Visible TB, RawBlockMem BM)
        {
            if (!TB)
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": Tech " + tank.name + " attempted to illegally grab NULL Visible");
            }
            else if (ManNetwork.IsNetworked)
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": Tech " + tank.name + " called HoldBlock in networked environment. This is not supported!");
            }
            else if (TB.block)
            {
                if (TB.isActive)
                {
                    if (TB.InBeam)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "'s target block was thefted by a tractor beam!");
                    }
                    else
                    {
                        if (TB.rbody)
                        {
                            ColliderSwapper CS;
                            if (heldBlock && heldBlock != TB.block)
                            {
                                DropBlock();
                            }
                            blockHoldOffset = true;
                            blockHoldPos = BM.p;
                            blockHoldRot = new OrthoRotation(BM.r);
                            if (ManNetwork.IsNetworked)
                                return true;
                            heldBlock = TB.block;
                            CS = heldBlock.GetComponent<ColliderSwapper>();
                            if (CS)
                                CS.EnableCollision(false);

                            return true;
                        }
                        else
                            DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "'s target block HAS NO RBODY");
                    }
                }
            }
            else
                DebugTAC_AI.Assert(true, KickStart.ModID + ": Tech " + tank.name + " attempted to illegally grab "
                    + (!TB.name.NullOrEmpty() ? TB.name : "NULL")
                    + " of type " + TB.type + " when they are only allowed to grab blocks");
            return false;
        }
        internal void DropBlock(Vector3 throwDirection)
        {
            if (heldBlock)
            {
                if (heldBlock.rbody)
                {
                    heldBlock.rbody.velocity = throwDirection.normalized * AIGlobals.ItemThrowVelo;
                }
                var CS = heldBlock.GetComponent<ColliderSwapper>();
                if (CS)
                    CS.EnableCollision(true);
                heldBlock.visible.SetLockTimout(Visible.LockTimerTypes.Interactible, 0);
                heldBlock = null;
            }
        }
        internal void DropBlock()
        {
            if (heldBlock)
            {
                var CS = heldBlock.GetComponent<ColliderSwapper>();
                if (CS)
                    CS.EnableCollision(true);
                heldBlock.visible.SetLockTimout(Visible.LockTimerTypes.Interactible, 0);
                heldBlock = null;
            }
        }

        private bool denyCollect = false;
        internal bool techIsApproaching = false;
        internal TankAIHelper ApproachingTech;
        public void SlowForApproacher(TankAIHelper Approaching)
        {
            if (AvoidStuff)
            {
                AvoidStuff = false;
                IsTryingToUnjam = false;
                // REVISED: now issues a core Stop (SetCoreControlStop) when yielding to an approaching tech, so it actually halts instead of just disabling avoidance.
                SetCoreControlStop();
                CancelInvoke();
                Invoke("EndSlowForApproacher", 2);
            }
            if (!techIsApproaching)
                ApproachingTech = Approaching;
            techIsApproaching = true;
        }
        private void EndSlowForApproacher()
        {
            if (!AvoidStuff)
            {
                AvoidStuff = true;
            }

            techIsApproaching = false;
            ApproachingTech = null;
        }
        public void DropAllItemsInCollectors()
        {
            denyCollect = true;
            CancelInvoke("StopDropAllItems");
            Invoke("StopDropAllItems", 2);
        }
        private void UpdateCollectors()
        {
            if (denyCollect)
            {
                foreach (ModuleItemHolder hold in tank.blockman.IterateBlockComponents<ModuleItemHolder>())
                {
                    ModuleItemHolder.AcceptFlags flag = ModuleItemHolder.AcceptFlags.Chunks;
                    if (!hold.GetComponent<ModuleItemConsume>() && !hold.IsEmpty && hold.Acceptance == flag && hold.IsFlag(ModuleItemHolder.Flags.Collector))
                    {
                        hold.DropAll();
                    }
                }
            }
        }
        private void StopDropAllItems()
        {
            denyCollect = false;
        }

        internal Event<TankAIHelper> FinishedRepairEvent = new Event<TankAIHelper>();
        internal void UpdateDamageThreshold()
        {
            int blockC = tank.blockman.blockCount;
            if (maxBlockCount <= blockC)
                maxBlockCount = blockC;
            if (maxBlockCount == 1)
            {
                var root = tank.blockman.GetRootBlock();
                if (root != null)
                {
                    DamageThreshold = (1f - (root.visible.damageable.Health / (float)root.damage.maxHealth)) * 100;
                    lastBlockCount = blockC;
                }
            }
            else
            {
                if (lastBlockCount != blockC)
                {
                    DamageThreshold = (1f - (blockC / (float)maxBlockCount)) * 100;
                    lastBlockCount = blockC;
                }
            }
        }
        private void TryRepairStatic()
        {
            if (BookmarkBuilder.TryGet(tank, out BookmarkBuilder builder))
            {
                AILimitSettings.OverrideForBuilder();
                if (TechMemor.IsNull())
                {
                    builder.HookUp(this);
                    DebugTAC_AI.Assert(KickStart.ModID + ": Tech " + tank.name + "TryRepairStatic has a BookmarkBuilder but NO TechMemor!");
                }
                if (lastEnemyGet != null)
                {
                    AIERepair.RepairStepper(this, tank, TechMemor, true, Combat: true);
                }
                else
                {
                    AIERepair.RepairStepper(this, tank, TechMemor);
                }
            }
            UpdateDamageThreshold();
        }
        public void CheckTryRepairAllied()
        {
            if (Singleton.Manager<ManWorld>.inst.CheckIsTileAtPositionLoaded(tank.boundsCentreWorldNoCheck))
                TryRepairAllied();
        }
        private void TryRepairAllied()
        {
            bool builderExists = BookmarkBuilder.TryGet(tank, out BookmarkBuilder builder);
            if (builderExists && TechMemor.IsNull())
            {
                builder.HookUp(this);
                DebugTAC_AI.Assert(KickStart.ModID + ": Tech " + tank.name + "TryRepairAllied has a BookmarkBuilder but NO TechMemor!");
            }
            if (builderExists || (AutoRepair && (!tank.PlayerFocused || ManWorldRTS.PlayerIsInRTS) && (KickStart.AISelfRepair || tank.IsAnchored)))
            {
                if (builderExists)
                {
                    AISetSettings.OverrideForBuilder();
                    AILimitSettings.OverrideForBuilder();
                }
                if (lastEnemyGet != null)
                {
                    AIERepair.RepairStepper(this, tank, TechMemor, AdvancedAI, Combat: true);
                }
                else
                {
                    if (AdvancedAI)
                        AIERepair.InstaRepair(tank, TechMemor, KickStart.AIClockPeriod);
                    else
                        AIERepair.RepairStepper(this, tank, TechMemor);
                }
            }
            UpdateDamageThreshold();
        }
        public void DelayedRepairUpdate()
        {
        }
        private void RemoveEnemyMatters()
        {
            var AISettings = tank.GetComponent<AIBookmarker>();
            if (AISettings.IsNotNull())
                DestroyImmediate(AISettings);
        }
        private void RemoveBookmarkBuilder()
        {
            if (BookmarkBuilder.TryGet(tank,
                out BookmarkBuilder Builder))
                Builder.Finish(this);
        }

        private void ShowCollisionAvoidenceDebugThisFrame()
        {
            if (AIGlobals.ShowDebugFeedBack && Input.GetKey(KeyCode.LeftShift))
            {
                try
                {
                    Vector3 boundsC = tank.boundsCentreWorldNoCheck;
                    Vector3 boundsCUp = tank.boundsCentreWorldNoCheck + (Vector3.up * lastTechExtents);
                    DebugExtUtilities.DrawDirIndicatorCircle(boundsC + (Vector3.up * 128), Vector3.up, Vector3.forward, JobSearchRange, Color.blue);
                    if (tank.IsAnchored && !CanAutoAnchor)
                    {
                        DebugExtUtilities.DrawDirIndicatorRecPrizExt(boundsC, Vector3.one * lastTechExtents, Color.yellow);
                        if (lastEnemyGet != null && lastEnemyGet.isActive)
                        {
                            DebugExtUtilities.DrawDirIndicatorCircle(boundsCUp, Vector3.up, Vector3.forward, MaxCombatRange, new Color(1, 0.6f, 0.6f));
                            DebugExtUtilities.DrawDirIndicatorCircle(boundsCUp, Vector3.up, Vector3.forward, MinCombatRange, Color.red);
                            DebugExtUtilities.DrawDirIndicator(lastEnemyGet.tank.boundsCentreWorldNoCheck,
                                lastEnemyGet.tank.boundsCentreWorldNoCheck + Vector3.up * lastEnemyGet.GetCheapBounds(), Color.red);
                        }
                    }
                    else
                    {
                        DebugExtUtilities.DrawDirIndicatorSphere(boundsC, lastTechExtents, Color.yellow);
                        DebugExtUtilities.DrawDirIndicatorSphere(DodgeSphereCenter, DodgeSphereRadius, Color.gray);
                        if (Attempt3DNavi)
                        {
                            DebugExtUtilities.DrawDirIndicatorSphere(boundsC, MaxObjectiveRange, Color.cyan);
                            if (lastEnemyGet != null && lastEnemyGet.isActive)
                            {
                                DebugExtUtilities.DrawDirIndicatorSphere(boundsC, MaxCombatRange, new Color(1, 0.6f, 0.6f));
                                DebugExtUtilities.DrawDirIndicatorSphere(boundsC, MinCombatRange, Color.red);
                                DebugExtUtilities.DrawDirIndicator(lastEnemyGet.tank.boundsCentreWorldNoCheck,
                                    lastEnemyGet.tank.boundsCentreWorldNoCheck + Vector3.up * lastEnemyGet.GetCheapBounds(), Color.red);
                            }
                        }
                        else
                        {
                            DebugExtUtilities.DrawDirIndicatorCircle(boundsCUp, Vector3.up, Vector3.forward, MaxObjectiveRange, Color.cyan);
                            if (lastEnemyGet != null && lastEnemyGet.isActive)
                            {
                                DebugExtUtilities.DrawDirIndicatorCircle(boundsCUp, Vector3.up, Vector3.forward, MaxCombatRange, new Color(1, 0.6f, 0.6f));
                                DebugExtUtilities.DrawDirIndicatorCircle(boundsCUp, Vector3.up, Vector3.forward, MinCombatRange, Color.red);
                                DebugExtUtilities.DrawDirIndicator(lastEnemyGet.tank.boundsCentreWorldNoCheck,
                                    lastEnemyGet.tank.boundsCentreWorldNoCheck + Vector3.up * lastEnemyGet.GetCheapBounds(), Color.red);
                            }
                        }
                    }
                    if (lastPlayer != null && lastPlayer.isActive)
                    {
                        DebugExtUtilities.DrawDirIndicator(lastPlayer.tank.boundsCentreWorldNoCheck,
                            lastPlayer.tank.boundsCentreWorldNoCheck + Vector3.up * lastPlayer.GetCheapBounds(), Color.white);
                    }
                    if (Obst != null)
                    {
                        float rad = 6;
                        if (Obst.GetComponent<Visible>())
                            rad = Obst.GetComponent<Visible>().Radius;
                        DebugExtUtilities.DrawDirIndicator(Obst.position, Obst.position + Vector3.up * rad, Color.gray);
                    }
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log("Error on Debug Draw " + e);
                }
            }
        }

    }
}
