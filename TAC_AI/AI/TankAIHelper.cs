using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Forms;
using TAC_AI.AI.Movement;
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
    // REVISED: split into per-service partial files (TankAIHelper.<Area>.cs) - identity + shared-state bus + tick
    // entry stay here; physics/targeting/weaponfire/avoidance/unjam/anchoring/dutycycle/controller/statustext move out.
    public partial class TankAIHelper : MonoBehaviour, IWorldTreadmill
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
        // Authored driver-type intent (stamped from RawTech at spawn). Any/AnyNonSea = no concrete intent
        // (author opted out, run heuristic). HandlingDetermine maps a concrete terrain to a driver and
        // short-circuits. AnyNonSea is a soft tiebreaker that disqualifies Sailor in the heuristic.
        // Cleared on Recycle; Invalidated flag set true on player block edits (PendingPlayerRecompose path) +
        // on split-off children (AIESplitHandler) - once the live blueprint diverges from the author's
        // declaration, the heuristic should take back over.
        internal BaseTerrain AuthoredTerrain = BaseTerrain.Any;
        internal bool AuthoredHintInvalidated = false;
        // Authored RawTech purposes (Headquarters / Defense / Harvesting / Sniper / ...) stamped at spawn
        // alongside AuthoredTerrain. May be null on player-built / unauthored techs - consumers MUST null-check.
        // Modified form ignores this; Smart form's IdentityClassifier reads it.
        internal System.Collections.Generic.HashSet<BasePurpose> AuthoredPurposes;
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
        /// <summary> Selected composable AI profile id (allied, Step 5b). When set + found in ProfileStore, the
        /// ProfileRunner uses it to OVERRIDE the default DediAI auto-assignment; null = auto-assign (preserved
        /// behavior). The in-game picker + persistence + MP sync are wired in Step 7. </summary>
        public string SelectedAIProfileId = null;
        /// <summary> v2: per-tech state OWNED by the active AI form (the form casts this to its own type). Built in
        /// IAIForm.OnTechSpawn, cleared in OnTechRecycle. Keeps the shell form-agnostic - it never inspects this. </summary>
        public object FormState = null;
        /// <summary>
        /// L-008: routing decision stamped exactly once per spawn (success or failure) by
        /// AIFormRegistry.RouteTech (L-027). After DelayedSubscribe completes Routing.FormId
        /// must be non-null — RoutingCompletenessCatcher (L-081) enforces this invariant.
        /// Cleared by Pool.Recycled() / Subscribe() (L-052).
        /// </summary>
        public RoutingDecision Routing = default(RoutingDecision);
        /// <summary> v2: set true while the active form is "Vanilla" - guards the alignment FSM from auto-promoting
        /// RunState Default->Advanced, so the tech stays handed back to TerraTech's own AI. </summary>
        public bool ForceVanillaAI = false;
        /// <summary> v2: read seam for RTS path drawing (ManWorldRTS). The active form's pathfinder points this at its
        /// live planned-route collection (e.g. AIControllerDefault's PathPlanned queue), so the drawn line tracks
        /// waypoints as they are consumed; null/empty = nothing to draw. Keeps the shell from casting to a form-owned
        /// movement controller type. ManWorldRTS only reads it when UsingPathfinding is true. </summary>
        public IReadOnlyCollection<WorldPosition> CurrentPlannedPath = null;
        /// <summary> How to attack the enemy </summary>
        public EAttackMode AttackMode = EAttackMode.Circle; // How to attack the enemy
        // REVISED: TurretFraction = wide-gimbal-turret share of weapons (0 all front-fixed, 1 all turreted), set in (R/E)WeapSetup.GetAttackStrat; drives the combat circle/face duty cycle.
        public float TurretFraction = 0f;
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
        internal int consecutiveNullMovementControllerTicks;
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
        // v2: internal (read/written by the detached ModifiedTargeting brain extension). _losLostGraceTimer moved here
        // from the old TankAIHelper.Targeting.cs partial (it is also reset by shell code), kept on the bus.
        internal int _losBlockedStreak = 0;
        internal float _losLostGraceTimer = 0f;
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
        public Vector3 lastDestinationCore => ControlCore.lastDestination;// Vector3.zero;    // Where we drive to in the world

        // Cargo fill fraction. Main-thread accessor used by IdleGatherer sane-exception +
        // the OverdueDelivery soft-correct path. Delegates to CargoStatePublisher (which
        // owns the GetTotalCapacityForLimiter sum + chunk-count maintenance via
        // tank.Holders events) when wired; falls back to a direct holder walk when the
        // publisher is not up. SelfProbeSnapshot.CargoNumContents / CargoCapacity is the
        // bg-thread path.
        public float GetCargoFraction()
        {
            if (tank == null) return 0f;
            var cargo = TAC_AI.AI.Forms.Smart.SmartRuntime.CargoState;
            if (cargo != null)
            {
                var snap = cargo.GetSnapshot(TAC_AI.AI.Forms.Smart.World.TechId.FromTank(tank));
                if (snap.CapacityChunks > 0) return (float)snap.NumChunks / snap.CapacityChunks;
            }
            if (tank.Holders == null) return 0f;
            int cap = 0;
            try
            {
                foreach (var holder in tank.Holders)
                {
                    if (holder == null) continue;
                    cap += holder.GetTotalCapacityForLimiter();
                }
            }
            catch { return 0f; }
            if (cap <= 0) return 0f;
            int contents = 0;
            try
            {
                foreach (var holder in tank.Holders)
                {
                    if (holder == null) continue;
                    if (!holder.IsEmpty) contents += 1;
                }
            }
            catch { return 0f; }
            return Mathf.Clamp01((float)contents / cap);
        }

        internal float lastOperatorRange { get { return _lastOperatorRange; } private set { _lastOperatorRange = value; } }
        internal float _lastOperatorRange = 0;   // v2: internal so the detached ModifiedNavi brain writes it directly
        internal float lastCombatRange => _lastCombatRange;
        internal float _lastCombatRange = 0;
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
        // Guard-window rings. 4 Hz writer, 480 slots = 120 s (sized for Homesickness).
        // recentSpeedSigned = Dot(velocity, forward). recentSpeedXZ = horizontal speed.
        // recentSpeedY = vertical component (Aviator pathology). All filled main-thread
        // inside UpdatePhysicsInfo via _recentSpeedNextCheck gate. GuardWorker reads via
        // window-mean / window-fraction helpers; never writes.
        internal const int GuardRingDepth = 480;
        internal const float GuardRingTickSec = 0.25f;
        internal readonly float[] recentSpeedSigned = new float[GuardRingDepth];
        internal readonly float[] recentSpeedXZ = new float[GuardRingDepth];
        internal readonly float[] recentSpeedY = new float[GuardRingDepth];
        internal int recentSpeedCursor = 0;
        internal bool recentSpeedWrapped = false;
        internal float recentSpeedNextCheck = 0f;
        // Net-progress ring (1 Hz; 30 slots = 30 s rolling). True = made net progress in the
        // last StuckNetProgressWindow. Advanced when netProgressNextCheck fires.
        internal const int NetProgressRingDepth = 30;
        internal readonly bool[] _netProgressRing = new bool[NetProgressRingDepth];
        internal int _netProgressCursor = 0;
        internal bool _netProgressWrapped = false;
        // Volatile companion for FrustrationMeter so GuardWorker (bg) can read without a
        // torn-read on 32-bit JIT. Written main-thread inside UpdatePhysicsInfo.
        internal volatile int PublishedFrustrationMeter = 0;
        // Telemetry-only sidecar fields for guards. Long writes are not atomic on 32-bit
        // x86 - use Interlocked.Exchange to write and Interlocked.Read to read from the bg
        // guard worker.
        internal long LastBlockDeliveredMono = 0L;
        internal long LastAllySightingMono = 0L;
        internal int AssignedAllyTechId = 0;
        // MoveStyle cache for guard-side identity hints. 0 = unset; values stamped by
        // HandlingDetermine path. Guards read for documentation only.
        internal byte CachedMoveStyle = 0;
        // Per-tech hard-correct goal mailbox. Indexed by (byte)GuardOrdinal, restricted to
        // hard-correct ordinals (slot 0 = GroundedAircraft, slot 1 reserved). Slots are
        // reference cells written by GuardCorrectiveActuator via Interlocked.Exchange and
        // read by ContinuousController on the main thread. Soft / observe guards have NO
        // slot - telemetry-only.
        public const int HardCorrectGuardCount = 2;
        public readonly TAC_AI.AI.Forms.Smart.Director.Guards.GuardInjectedGoalSlot[] GuardInjectedGoals
            = new TAC_AI.AI.Forms.Smart.Director.Guards.GuardInjectedGoalSlot[HardCorrectGuardCount];
        // Last goal kind the controller actually applied after a hard-correct substitution.
        // Kinematic-detection sidecar for GuardCorrectionInjected attribution.
        public byte LastAppliedGoalKind = 0;
        internal int anchorAttempts = 0;
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
        internal EControlOperatorSet ControlOperator = EControlOperatorSet.Default;
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
        internal bool IsDirectedMovingFromDest => ControlOperator.DriveDest == EDriveDest.FromLastDestination ||
            (ThrottleState == AIThrottleState.ForceSpeed && DriveVar < -0.01f);

        /// <summary> Drive direction </summary>
        internal EDriveFacing DriveDirDirected => ControlOperator.DriveDir;
        /// <summary> Move to a dynamic target </summary>
        internal EDriveDest DriveDestDirected => ControlOperator.DriveDest;
        internal EControlCoreSet ControlCore = EControlCoreSet.Default;
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
                    // v2 isolation: shell reads raw terrain via TerrainQuery (OffsetFromGroundAAlt == raw OffsetFromGroundA) instead of the form's cached AIEPathing. Minor cached->raw delta on the RTS-air destination snap.
                    RTSDestInternal = TerrainQuery.OffsetFromGroundAAlt(new IntVector3(value), AIGlobals.GroundOffsetRTSAir);
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
        // v2 isolation: dead `autoPather` accessor removed (it returned AIControllerDefault.Pathfinder; only consumer was the RTS path draw, which now reads the CurrentPlannedPath seam).

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
            // L-052: Subscribe-time routing stamp. FormId stays null (DelayedSubscribe is
            // what actually calls RouteTech with the active form). This stamp tells the
            // RoutingCompletenessCatcher (L-081) "this tech is in flight; don't scream yet".
            Routing = new RoutingDecision
            {
                FormId = null,
                TimestampMs = System.Environment.TickCount,
                Reason = "Subscribed",
            };
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
            // L-050: split try-block. Inner try around HandlingDetermine/ExecuteAutoSet/
            // SetDriverType — a NRE here used to swallow OnTechSpawn too. Now: on
            // HandlingDetermine failure, fall back to DriverType=Tank + stamp the failure
            // reason on the Routing record; either way OnTechSpawn ALWAYS runs (via funnel).
            try
            {
                lastTechExtents = (tank.blockBounds.size.magnitude / 2) + 2;
                if (lastTechExtents < 1)
                {
                    Debug.LogError("lastTechExtents is below 1: " + lastTechExtents);
                    lastTechExtents = 1;
                }
                maxBlockCount = tank.blockman.blockCount;

                // Inner try — HandlingDetermine is the prime NRE source.
                string handlingReason = "DelayedSubscribe";
                try
                {
                    if (DriverType == AIDriverType.AutoSet)
                        ExecuteAutoSetNoCalibrate();
                    else
                        SetDriverType(DriverType);
                }
                catch (Exception eh) when (eh is NullReferenceException || eh is MissingReferenceException)
                {
                    DriverType = AIDriverType.Tank;   // safe default — Modified handles Tank baseline
                    handlingReason = "HandlingDetermineFailed";
                    DebugTAC_AI.LogWarnFileOnly("delayed-subscribe-handling-fail-" + tank.name,
                        "[TECH-ROUTE] HandlingDetermine threw " + eh.GetType().Name
                        + " for tech='" + tank.name + "' — falling back to Tank driver");
                    // Stamp failure on Routing for the funnel to pick up.
                    Routing.ExceptionType = eh.GetType().Name;
                    Routing.ExceptionMessage = eh.Message;
                }

                // L-050/L-027: OnTechSpawn ALWAYS runs via the funnel (was conditional on
                // HandlingDetermine succeeding). RouteTech is idempotent (FormId==ActiveId
                // fast-path) so the Subscribe-time stamp doesn't cause double-spawn.
                TAC_AI.AI.Forms.AIFormRegistry.RouteTech(this, handlingReason);
                delayedSubscribeRetries = 0;
            }
            catch (Exception e)
            {
                // L-050: outer catch is now generic Exception (was NRE/MRE only). Any
                // surviving exception is logged + we still stamp routing as failed so
                // the orphan watchdog (L-081) doesn't see a null FormId.
                DebugTAC_AI.LogWarnPlayerOncePerKey("DelayedSubscribe.partial:" + tank.name,
                    "DelayedSubscribe partial init for " + tank.name, e);
                Routing.ExceptionType = e.GetType().Name;
                Routing.ExceptionMessage = e.Message;
                TAC_AI.AI.Forms.AIFormRegistry.RouteTech(this, "DelayedSubscribeFailed");
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
                AuthoredHintInvalidated = true;
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
                AuthoredHintInvalidated = true;
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
            // L-052: drop the routing record so pool-reuse starts with FormId=null and
            // the OnPreUpdate reclaim path (L-051) can re-route the helper fresh.
            // Same-key remove from LiveRoutings is best-effort — GetInstanceID is stable
            // across Unity recycle.
            try
            {
                TAC_AI.AI.Forms.AIFormRegistry.LiveRoutings.TryRemove(GetInstanceID(), out _);
            }
            catch { /* harmless if registry not present */ }
            Routing = default(TAC_AI.AI.Forms.RoutingDecision);
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
            AuthoredTerrain = BaseTerrain.Any;
            AuthoredHintInvalidated = false;
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
            // v2: let the active form tear down its per-tech FormState so pooled tech reuse starts fresh.
            TAC_AI.AI.Forms.AIFormRegistry.Active?.OnTechRecycle(this);
            // Reduced-scope cleanup for the new guard-window rings + companion fields. Stale
            // data on a pool-reused tech would otherwise feed the next spawn's first window.
            System.Array.Clear(recentSpeedSigned, 0, recentSpeedSigned.Length);
            System.Array.Clear(recentSpeedXZ, 0, recentSpeedXZ.Length);
            System.Array.Clear(recentSpeedY, 0, recentSpeedY.Length);
            recentSpeedCursor = 0;
            recentSpeedWrapped = false;
            recentSpeedNextCheck = 0f;
            System.Array.Clear(_netProgressRing, 0, _netProgressRing.Length);
            _netProgressCursor = 0;
            _netProgressWrapped = false;
            PublishedFrustrationMeter = 0;
            System.Threading.Interlocked.Exchange(ref LastBlockDeliveredMono, 0L);
            System.Threading.Interlocked.Exchange(ref LastAllySightingMono, 0L);
            AssignedAllyTechId = 0;
            CachedMoveStyle = 0;
            // RM-9: null the hard-correct mailbox so a pool-reused tech does not inherit a
            // 5-s-old recovery goal from the prior occupant.
            for (int gi = 0; gi < GuardInjectedGoals.Length; gi++)
                System.Threading.Interlocked.Exchange(ref GuardInjectedGoals[gi], null);
            LastAppliedGoalKind = 0;
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
        // Step 7: allied composable-profile MP receive. Same anti-spoof guard as TrySetAITypeRemote
        // (only the owning team, or the host, may change a tech's profile). The runner reads
        // SelectedAIProfileId live, so no rebuild is strictly required; OnSwitchAI refreshes state.
        public void TrySetAIProfileRemote(NetPlayer sender, string profileId)
        {
            if (!ManNetwork.IsNetworked)
                return;
            if (sender == null || sender.CurTech?.Team == tank.Team)
            {
                SelectedAIProfileId = string.IsNullOrEmpty(profileId) ? null : profileId;
                OnSwitchAI(true);
            }
            else
                DebugTAC_AI.Log(KickStart.ModID + ": A player tried to change the AI profile of a Tech that wasn't theirs");
        }
        public void TrySetAITypeRemote(NetPlayer sender, AIType type, AIDriverType driver)
        {
            if (ManNetwork.IsNetworked)
            {
                if (sender == null)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Host changed AI");
                }
                if (sender == null || sender.CurTech?.Team == tank.Team)
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
                AttackMode = TAC_AI.AI.Forms.AIFormRegistry.Active?.SelectAlliedAttackStrat(this) ?? AttackMode;
        }
        public void RefreshAI()
        {
            AvoidStuff = true;
            UsingAirControls = false;
            if (!ForceVanillaAI) RunState = AIRunState.Advanced;
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
            if (!ForceVanillaAI) RunState = AIRunState.Advanced;
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
            this.ReleaseTarget();
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

        // ControllerManager: SwapMovementController / SetupDefaultMovementAIController / RecalMoveAIControllerNPT / RecalMoveAIControllerPlayer / LogMovementControllerSwapIfChanged / RecalibrateMovementAIController moved to TankAIHelper.Controller.cs (Step 2).

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
            if (DriverType != before || UsingAirControls)
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

            if (srcAlive && this.SetPursuit(src.visible, force: true))
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
            this.ReleaseTarget();
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
                        if (UsingAirControls)
                        {
                            if (MovementController.Grounded)
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
                    // v2 isolation: airplane maneuver status comes from the active form (no AirplaneAICore name in the shell).
                    int diveState = TAC_AI.AI.Forms.AIFormRegistry.Active?.GetAirDiveState(this) ?? 0;
                    if (diveState > 0)
                    {
                        switch (diveState)
                        {
                            case 1:
                                output += "\n" + AILOC.Fly_Dive + AILOC.FaceTowards + AILOC.Target;
                                break;
                            case 2:
                                output += "\n" + AILOC.Fly_Dive + AILOC.Fly_Dive2;
                                break;
                            default:
                                output += "\n" + AILOC.Fly_Dive + AILOC.Code + diveState + "]";
                                break;
                        }
                    }
                    int uTurnState = TAC_AI.AI.Forms.AIFormRegistry.Active?.GetAirUTurnState(this) ?? 0;
                    if (uTurnState > 0)
                    {
                        switch (uTurnState)
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
                                output += "\n" + AILOC.Fly_UTurn + AILOC.Code + uTurnState + "]";
                                break;
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
                                if (UsingAirControls)
                                {
                                    if (MovementController.Grounded)
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
            if (Attempt3DNavi || UsingAirControls)
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
            if (Attempt3DNavi || UsingAirControls)
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
            if (Attempt3DNavi || UsingAirControls)
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
                            TAC_AI.AI.Forms.AIFormRegistry.Active?.ControlFrame(this, thisControl);
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
                                    TAC_AI.AI.Forms.AIFormRegistry.Active?.ControlFrame(this, thisControl);
                                    return true;
                                }
                            }
                            else if (AIAlign == AIAlignment.NonPlayer)
                            {
                                TAC_AI.AI.Forms.AIFormRegistry.Active?.ControlFrame(this, thisControl);
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
                            TAC_AI.AI.Forms.AIFormRegistry.Active?.ControlFrame(this, thisControl);
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
                                TAC_AI.AI.Forms.AIFormRegistry.Active?.ControlFrame(this, thisControl);
                                return true;
                            }
                            else if (SetToActive)
                            {
                                TAC_AI.AI.Forms.AIFormRegistry.Active?.ControlFrame(this, thisControl);
                                return true;
                            }
                        }
                        else if (AIAlign == AIAlignment.NonPlayer)
                        {
                            TAC_AI.AI.Forms.AIFormRegistry.Active?.ControlFrame(this, thisControl);
                            return true;
                        }
                    }
                }
            }
            SuppressFiring(false);
            return false;
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
                if (TerrainQuery.ObstructionAwarenessAny(DodgeSphereCenter, DodgeSphereRadius) ||
                    TerrainQuery.ObstructionAwarenessTerrain(DodgeSphereCenter, tank.IsAnchored, DodgeSphereRadius))
                    ThrottleState = AIThrottleState.Yield;

                if (tank.wheelGrounded)
                {
                    if (!this.AutoHandleObstruction(ref direct, lastOperatorRange, true, true))
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
                this.GetDistanceFromTask(lastDestinationCore);
                bool needsToSlowDown = this.IsOrbiting();

                Attempt3DNavi = mind.EvilCommander == EnemyHandling.Starship;
                AvoidStuff = true;
                bool AutoAnchor = mind.CommanderSmarts >= EnemySmarts.Meh;
                if (needsToSlowDown || TerrainQuery.ObstructionAwarenessAny(DodgeSphereCenter, DodgeSphereRadius)
                    || TerrainQuery.ObstructionAwarenessSetPieceAny(DodgeSphereCenter, tank.IsAnchored, DodgeSphereRadius))
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
                        TAC_AI.AI.Forms.AIFormRegistry.Active?.RunEnemyRTSCombat(this, tank, mind);
                        SetDirectedControl(direct);
                        return;
                    }
                    if (!IsTechMovingActual(EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                    {
                        this.TryHandleObstruction(true, lastOperatorRange, false, true, ref direct);
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
            TAC_AI.AI.Forms.AIFormRegistry.Active?.RunEnemyRTSCombat(this, tank, mind);
        }

        // L-051: per-frame cap on reclaim work — at most 3 helpers re-routed per frame.
        // Counter is keyed by Time.frameCount so it auto-resets each new frame.
        private static int _reclaimFrameCount;
        private static int _reclaimCountThisFrame;
        private const int MaxReclaimsPerFrame = 3;

        internal void OnPreUpdate()
        {
            // L-051: routing-reclaim hook. If DelayedSubscribe's Invoke was dropped (FMOD
            // pause, Pool.Return mid-fade), the helper still has Routing.FormId==null when
            // OnPreUpdate first runs. Re-route via the funnel — idempotent if a late
            // DelayedSubscribe eventually fires. Capped at 3/frame to bound the cost of a
            // mass-spawn batch where every helper needs reclaim same-frame.
            if (Routing.FormId == null && tank != null && tank.blockman != null && enabled)
            {
                int frame = UnityEngine.Time.frameCount;
                if (_reclaimFrameCount != frame)
                {
                    _reclaimFrameCount = frame;
                    _reclaimCountThisFrame = 0;
                }
                if (_reclaimCountThisFrame < MaxReclaimsPerFrame)
                {
                    _reclaimCountThisFrame++;
                    try { TAC_AI.AI.Forms.AIFormRegistry.RouteTech(this, "Reclaimed"); }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarning("OnPreUpdate.Reclaim: " + ex.Message);
                    }
                }
            }

            if (MovementController == null)
            {
                DebugTAC_AI.Assert(MovementController == null, "MOVEMENT CONTROLLER IS NULL");
                RecalibrateMovementAIController();
            }
            // EstTopSped is tracked here every Pre frame.
            recentSpeed = GetSpeed();
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
            // v2: lock-on + block-hold brain owned by the active form (ModifiedForm.PostUpdate); anchor-consume + debug stay shell.
            TAC_AI.AI.Forms.AIFormRegistry.Active?.PostUpdate(this);
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

        // v2: thin forwarder - the directors body is owned by the active AI form (ModifiedTick.HostDirectorsBody).
        internal void OnUpdateHostAIDirectors()
        {
            TAC_AI.AI.Forms.AIFormRegistry.Active?.Directors(this, true);
        }
        // v2: thin forwarder - the operations routing body is owned by the active AI form (ModifiedTick.HostOperationsBody).
        internal void OnUpdateHostAIOperations()
        {
            TAC_AI.AI.Forms.AIFormRegistry.Active?.Operations(this, true);
        }

        // v2: thin forwarders - client director/operations bodies owned by the active form (ModifiedTick.Client*Body).
        internal void OnUpdateClientAIDirectors()
        {
            TAC_AI.AI.Forms.AIFormRegistry.Active?.Directors(this, false);
        }
        internal void OnUpdateClientAIOperations()
        {
            TAC_AI.AI.Forms.AIFormRegistry.Active?.Operations(this, false);
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
            if (RunState == AIRunState.Default && !ForceVanillaAI)
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
        // Avoidance: UpdateVanillaAvoidence / GetOtherDir / AvoidAssist / AvoidAssistPrecise / AvoidAssistPrediction / AvoidAssistAirSpacing + posWeights moved to TankAIHelper.Avoidance.cs (Step 2).

        // PhysicsInfo net-progress tracker + UpdatePhysicsInfo moved to TankAIHelper.Physics.cs (Step 2).
        // REVISED: the AIClockPeriod/40 scale factor is now float division ((float)AIClockPeriod/40f); it was integer division that evaluated to 0 in both SP and MP, disabling the orbit check. IsOrbiting_LEGACY was removed.
        // Unjam: AutoHandleObstruction / TryHandleObstruction / RemoveObstruction / SettleDown moved to TankAIHelper.Unjam.cs (Step 2).

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

        internal static int TargetMask = Globals.inst.layerScenery.mask | Globals.inst.layerSceneryCoarse.mask |
            Globals.inst.layerSceneryFader.mask | Globals.inst.layerTerrain.mask | Globals.inst.layerLandmark.mask;
        // Targeting: CheckEnemyAndAiming / TryRefreshEnemyAllied / TryRefreshEnemyEnemy / UpdateTargetCombatFocus / UpdateEnemyDistance / IgnoreEnemyDistance + LastWeapCheck moved to TankAIHelper.Targeting.cs (Step 2).
        // REVISED: renamed from DetermineCombat and now RETURNS the retreat verdict (callers do `Retreat = DetermineRetreatPosture()`) instead of writing Retreat as a side effect; it no longer acquires targets, only drops a now-friendly held target and decides retreat.

        internal float lastTargetGatherTime = 0;   // v2: internal for the detached ModifiedTargetScan brain
        // REVISED: target-scan cache is now range-aware (re-gathers if the requested range exceeds what was last cached) and refreshes faster in combat (TargetCacheRefreshIntervalCombat when Provoked/has-target vs idle TargetCacheRefreshInterval); InvalidateTargetCache forces a re-scan (called from OnHit).
        internal float lastTargetGatherRangeSqr = 0;
        internal List<Tank> targetCache = new List<Tank>();
        internal void InvalidateTargetCache() { lastTargetGatherTime = 0; }
        private const float MaxBoundsVelo = 350;
        // PhysicsInfo bounds-velocity clamp statics moved to TankAIHelper.Physics.cs (Step 2).
        // REVISED: null-guards the target and now applies lead prediction whenever lastCombatRange is finite (lastCombatRange < float.MaxValue) instead of only within EnemyExtendActionRange, so long-range shots also lead.


        public int Provoked = 0;
        public bool KeepEnemyFocus { get; internal set; } = false;

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
        // Targeting: SetPursuit / TrySetPursuit / IsSetPursuitSuccess / EndPursuit / ReleaseTarget / InRangeOfTarget + SetPursuitResult moved to TankAIHelper.Targeting.cs (Step 2).


        // Anchoring: TryInsureAutoAnchor / TryInsureManualAnchor / AnchorIgnoreChecks / Unanchor / DoAnchorStatic / DoAnchor / DoUnAnchor + the ConfigureJoint reflection static moved to TankAIHelper.Anchoring.cs (Step 2).

        public TankBlock HeldBlock => heldBlock;
        internal TankBlock heldBlock;
        internal Vector3 blockHoldPos = Vector3.zero;
        internal Quaternion blockHoldRot = Quaternion.identity;
        internal bool blockHoldOffset = false;
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
        internal void TryRepairStatic()
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
