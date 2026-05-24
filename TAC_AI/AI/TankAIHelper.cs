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
    public class TankAIHelper : MonoBehaviour, IWorldTreadmill
    {
        // B8: removed `internal static bool updateErrored` — was a scene-wide latch that
        // suppressed every subsequent tank's first failure. Replaced with per-tank-per-site
        // LogWarnPlayerOncePerKey at each former callsite (UpdateLastTechExtentsIfNeeded,
        // OnUpdateHostAIDirectors, OnUpdateHostAIOperations, CheckRebuildAlignment,
        // TankAIManager.FixedUpdate).

        public Tank tank;
        public AITreeType.AITypes lastAIType;
        //Tweaks (controlled by Module)
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
        /// <summary>
        /// T5: chokepoint for DriverType writes. Resolves AutoSet inline when the tank is
        /// ready (blockman populated); otherwise stores AutoSet to be resolved later by
        /// Subscribe/DelayedSubscribe. This prevents AutoSet from reaching dispatch in
        /// AlliedOperationsController.Execute, where the silent fallback to Tank would
        /// hide every upstream caller that forgot to resolve.
        /// </summary>
        public void SetDriverType(AIDriverType driverType)
        {
            if (driverType == AIDriverType.AutoSet && tank != null && tank.blockman != null && tank.blockman.blockCount > 0)
                ExecuteAutoSetNoCalibrate();   // resolves AutoSet -> concrete type
            else
                DriverType = driverType;       // may store AutoSet if tank not ready; Subscribe resolves
            RequestMovementControllerSwap(MovementSwapReason.SetDriverType);
        }
        // J: every requester of a deferred movement-controller swap names itself, so a swap storm can
        // be attributed via the swap log / state-history ring. The raw flag's setter is private - all
        // writers (including KickStart / RCore) request through RequestMovementControllerSwap; only the
        // consumer (RecalibrateMovementAIController) clears it.
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
                AppendHistory("SwapReq " + reason);   // record the request that flips clean -> dirty
            lastSwapRequest = reason;
            MovementAIControllerDirty = true;
        }
        public AIType DediAI = AIType.Escort;
        public EAttackMode AttackMode = EAttackMode.Circle; // How to attack the enemy
        public float TurretFraction = 0f;   // 0 = all front-fixed weapons, 1 = all wide-gimbal turrets; drives the combat circle/face duty cycle (set in [R/E]WeapSetup.GetAttackStrat)
        private float combatCyclePhase01 = -1f;   // per-tech random phase offset for that duty cycle, lazily seeded on first use so neighbouring techs desync
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
        // B7: distinguish "user chose Idle" from "TryGetCurrentAIType failed".
        // Resolution success is tracked separately so a failed resolve doesn't masquerade
        // as a legitimate Idle and silently freeze ControlTech / UpdateTechControl.
        internal bool lastAITypeResolved = false;
        public bool SetToActive => lastAITypeResolved && lastAIType != AITreeType.AITypes.Idle;
        public bool AITypeUnresolved => !lastAITypeResolved;
        // B6: MT slaves (MTTurret/MTStatic/MTMimic) cannot receive RTS commands directly —
        // they slave to their host's transform via BMultiTech.MimicAllClosestAlly / beam-lock.
        // The non-MT host receives the waypoint and drags affiliated MTs via lastTechExtents.
        public bool IsRTSReceivable => !IsMultiTech;
        // B12: tracks consecutive UpdateTechControl ticks with null MovementController;
        // escalates from "warn once" to "persistent invariant violation" at N=30.
        private int consecutiveNullMovementControllerTicks;
        // B3: tick-stable AIAlign snapshot. Captured at end of OnPreUpdate (after
        // CheckRebuildAlignment), released in OnPostUpdate. Directors and Operations are
        // independently staggered across MULTIPLE frames per helper, so without this snapshot
        // a dirtyAI flip between phases let Directors configure pathing as Player while
        // Operations dispatched enemy behavior with allied-pathing setup intact.
        private AIAlignment tickAIAlign = AIAlignment.Static;
        private bool tickAlignmentLatched = false;
        internal AIAlignment TickAIAlign => tickAlignmentLatched ? tickAIAlign : AIAlign;
        public bool NotInBeam => BeamTimeoutClock == 0;
        public bool CanCopyControls => !IsMultiTech || tank.PlayerFocused;
        public bool CanUseBuildBeam => !(tank.IsAnchored && !PlayerAllowAutoAnchoring);
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
        public bool ChaseThreat = true;
        public bool RequestBuildBeam = true;

        public bool AdvancedAI => Allied ? (AISetSettings.AdvancedAI && AILimitSettings.AdvancedAI) : AISetSettings.AdvancedAI;
        public bool AllMT => Allied ? (AISetSettings.AllMT && AILimitSettings.AllMT) : AISetSettings.AllMT;
        public bool FullMelee => Allied ? (AISetSettings.FullMelee && AILimitSettings.FullMelee) : AISetSettings.FullMelee;
        public bool SideToThreat => Allied ? (AISetSettings.SideToThreat && AILimitSettings.SideToThreat) : AISetSettings.SideToThreat;

        /// <summary>
        /// Combat-facing duty cycle. Returns true when the tech should CIRCLE (broadside) this instant, false to
        /// FACE the target. Circles for ~<see cref="TurretFraction"/> of each <see cref="KickStart.CombatFacingCyclePeriod"/>
        /// and faces the rest, so a tech with half its weapons on wide-gimbal turrets strafes ~half the time and faces
        /// ~half (giving front-fixed guns their firing windows). TurretFraction 0 always faces, 1 always circles.
        /// Meant to be sampled at Director/Operations cadence; the per-tech phase offset desyncs neighbouring techs.
        /// </summary>
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

        public bool AutoRepair => Allied ? (AISetSettings.AutoRepair && AILimitSettings.AutoRepair) : AISetSettings.AutoRepair;
        public bool UseInventory => Allied ? (AISetSettings.UseInventory && AILimitSettings.UseInventory) : AISetSettings.UseInventory;

        public bool AutoAnchor = false;
        public bool SecondAvoidence = false;

        // Distance operations - Automatically accounts for tech sizes
        public AISettingsSet AISetSettings = AISettingsSet.DefaultSettable;
        public AISettingsLimit AILimitSettings = default;
        public float MinCombatRange => AISetSettings.CombatSpacing;
        public float MaxCombatRange => AISetSettings.CombatChase;
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
        /*
        public bool isAssassinAvail = false;    //Is there an Assassin-enabled AI on this tech?
        public bool isAegisAvail = false;       //Is there an Aegis-enabled AI on this tech?

        public bool isProspectorAvail = false;  //Is there a Prospector-enabled AI on this tech?
        public bool isScrapperAvail = false;    //Is there a Scrapper-enabled AI on this tech?
        public bool isEnergizerAvail = false;   //Is there a Energizer-enabled AI on this tech?

        public bool isAviatorAvail = false;
        public bool isAstrotechAvail = false;
        public bool isBuccaneerAvail = false;
        */

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
        // Hysteresis counter for BlockedLineOfSight. LOS checks run every 0.6s; a single intermittent
        // hit (terrain bump, ally passing through) shouldn't flip the public flag and trigger combat-FSM
        // mode changes (MoveSideways <-> stand-and-shoot). Require N=2 consecutive blocked checks
        // before declaring blocked; clear on any unblocked check.
        private int _losBlockedStreak = 0;
        // Combat-bucket hysteresis flag. Set by RWheeled.AttackVroom when the tech was in the
        // retreat (close-range) bucket on the previous tick, used to require extra clearance
        // before flipping to the advance bucket. Prevents oscillation at the spacer+range edge.
        public bool WasRetreatingInCombat = false;
        // One-shot latch for RCore.EnsureEnemyMind regen recovery. Prevents infinite-recursion /
        // log-spam if GenerateEnemyAI keeps failing for this tech (e.g. mid-recycle race).
        // Cleared on successful Mind resolution and on Recycled.
        internal bool beEvilRegenAttempted = false;

        public AIAlignment AIAlign = AIAlignment.Static;             // 0 is static, 1 is ally, 2 is enemy
        public AIWeaponState WeaponState = AIWeaponState.Normal;    // 0 is sleep, 1 is target, 2 is obsticle, 3 is mimic
        public bool UpdateDirectorsAndPathing = false;       // Collision avoidence active this FixedUpdate frame?
        public bool UsingAirControls = false; // true => AIControllerAir; false => AIControllerDefault/Static
        internal int FrustrationMeter = 0;  // tardiness buildup before we use our guns to remove obsticles
        internal float Urgency = 0;         // tardiness buildup before we just ignore obstructions
        internal float UrgencyOverload = 0; // builds up too much if our max speed was set too high

        /*
        private bool damageCheck = true;
        public bool PendingDamageCheck
        {
            get { return damageCheck; }
            set
            {
                DebugTAC_AI.Log("PendingDamageCheck set by: " + StackTraceUtility.ExtractStackTrace());
                damageCheck = value;
            }
        }
        // */ public bool PendingDamageCheck = true;

        public float DamageThreshold = 0;   // How much damage have we taken? (100 is total destruction)

        internal Vector3 lastDestinationOp => ControlOperator.lastDestination; // Where we drive to in the world
        internal Vector3 lastDestinationCore => ControlCore.lastDestination;// Vector3.zero;    // Where we drive to in the world

        /*
        internal Vector3 lastDestination {
            get { return lastDestinationBugTest; }
            set {
                DebugTAC_AI.Log("lastDestination set by: " + StackTraceUtility.ExtractStackTrace());
                lastDestinationBugTest = value; 
            }
        }
        internal Vector3 lastDestinationBugTest = Vector3.zero;    // Where we drive to in the world
        */
        internal float lastOperatorRange { get { return _lastOperatorRange; } private set { _lastOperatorRange = value; } }
        private float _lastOperatorRange = 0;
        internal float lastCombatRange => _lastCombatRange;
        private float _lastCombatRange = 0;
        internal float lastPathPointRange = 0;
        public float NextFindTargetTime = 0;      // Updates to wait before target swatching

        internal bool hasAI = false;    // Has an active AI module
        internal AIDirtyState dirtyAI = AIDirtyState.Not;  // Update Player AI state if needed
        public enum AIDirtyState
        {
            Not,
            Dirty,
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
                // B1: enforce the pair-invariant. A null target cannot be "locked";
                // any site that nulls lastEnemy directly (RGeneral.Monitor, PlayerRTSUI,
                // network handlers) must also drop KeepEnemyFocus or future
                // SetPursuit(newTarget) calls silently no-op against a phantom lock.
                if (value == null && KeepEnemyFocus)
                    KeepEnemyFocus = false;
            }
        }
        // B5: RTS "hold-position-attack" mode — commander manually painted a target with no
        // move order. Strong intent signal, deserves the broadest retention tolerance (subject
        // to B6's hard cap below).
        public bool RTSManualTargetLock => RTSControlled && RTSDestInternal == RTSDisabled;
        // B5: generic sticky-pursuit. Covers BOTH RTS manual locks AND OnHit/focus-fire/revenge
        // pursuits set via SetPursuit(force:true). Without this, a tech hit at long range loses
        // its attacker on the very next validation tick — defeating the explicit B7 grace intent.
        // B6 hard-cap still applies via the range purges checking RTSLockMaxRangeMultiplier.
        public bool PreserveEnemyTarget => RTSManualTargetLock || KeepEnemyFocus;
        public Visible lastLockOnTarget;
        public Transform Obst;
        internal Tank lastCloseAlly;
        // Non-Tech specific objective AI Handling
        internal float lastBaseExtremes = 10;
        internal Tank theBase = null;
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
        internal Visible theResourceNode = null;
        internal Visible theHostTech = null;
        internal Visible theGuardedAlly = null;
        internal IAIFollowable lastBasePos;
        internal bool foundBase = false;
        internal bool foundGoal = false;

        internal HashSet<Tank> MultiTechsAffiliated = new HashSet<Tank>();
        internal bool MTMimicHostAvail = false;
        internal bool MTLockedToTechBeam = false;
        internal Vector3 MTOffsetPos = Vector3.zero;
        internal Vector3 MTOffsetRot = Vector3.forward;
        internal Vector3 MTOffsetRotUp = Vector3.up;

        //  !!ADVANCED!!
        internal bool Attempt3DNavi = false;
        internal Vector3 Navi3DDirect = Vector3.zero;   // Forwards facing for 3D
        internal Vector3 Navi3DUp = Vector3.zero;       // Upwards direction for 3D
        public float GroundOffsetHeight = AIGlobals.GroundOffsetGeneralAir;           // flote above ground this dist
        internal Snapshot lastBuiltTech = null;
        internal Vector3 PathPoint => MovementController.PathPoint;

        // DelayedAnchorClock shim — continuous rest-duration via a Time.time start-stamp (was a per-pass
        // count-up: role/MP-dependent ~4-8s SP, up to 12s MP). Now uniform & seconds-based: ++ keeps timing,
        // =0 interrupts. settle seconds = BaseAnchorMinimumTimeDelay / AnchorTicksPerSecond. SOURCE OF TRUTH: docs/21.
        internal const float AnchorTicksPerSecond = 5f;   // legacy ~5 ticks/s; threshold 20 ticks => 4.0s anchor settle
        private float anchorRestStart = -1f;
        internal int DelayedAnchorClock
        {
            get { return anchorRestStart < 0f ? 0 : Mathf.Min((int)AIGlobals.BaseAnchorMinimumTimeDelay, Mathf.CeilToInt((Time.time - anchorRestStart) * AnchorTicksPerSecond)); }
            set { if (value <= 0) anchorRestStart = -1f; else if (anchorRestStart < 0f) anchorRestStart = Time.time; }
        }
        internal AITimer lightBoostFeatherTimer;   // feathers LightBoost ~every 0.5s (was a framerate-coupled frame counter)
        internal float RepairStepperClock = 0;
        internal short BeamTimeoutClock = 0;
        internal int WeaponDelayClock = 0;
        // actionPause shim: backed by a seconds-based AITimer. The old field was a frame counter drained
        // by AIClockPeriod every Pre tick (= 500/s at the SP canonical rate AIClockPeriodSet 10 / fixedDeltaTime 0.02).
        // Backing it with an AITimer makes every pause hold framerate- and SP/MP-invariant while the ~40 call
        // sites keep their legacy tick values and value comparisons. Effective seconds are the SOURCE-OF-TRUTH
        // table in docs/21_timing-cadence.md — update there whenever a hold value changes.
        private AITimer actionPauseTimer;
        internal AITimer beamFlipTimer;                          // AIEBeam tipped-over flip-beam timer (own field; revived)
        internal const float ActionPauseTicksPerSecond = 500f;   // AIClockPeriodSet(10) / fixedDeltaTime(0.02)
        internal int actionPause                                 // when [val > 0], used to halt other actions
        {
            get { return actionPauseTimer.Running ? Mathf.CeilToInt(actionPauseTimer.Remaining * ActionPauseTicksPerSecond) : 0; }
            set { if (value <= 0) actionPauseTimer.Clear(); else actionPauseTimer.Set(value / ActionPauseTicksPerSecond); }
        }
        public int ActionPause
        {
            get => actionPause;
            private set => actionPause = value;
        }
        // unanchorCountdown shim — countdown warn timer (was a per-pass tick countdown from 15; the --
        // decrements were removed, it self-counts). =15 arms ~3s. SOURCE OF TRUTH: docs/21.
        private AITimer unanchorTimer;
        internal int unanchorCountdown                 // aux warning timer for unanchor
        {
            get { return unanchorTimer.Running ? Mathf.CeilToInt(unanchorTimer.Remaining * AnchorTicksPerSecond) : 0; }
            set { if (value <= 0) unanchorTimer.Clear(); else unanchorTimer.Set(value / AnchorTicksPerSecond); }
        }

        // Hierachy System:
        //   Operations --[ControlPre]-> Maintainer --[ControlPost]-> Core
        // We need to tell the AI some important information:
        /// <summary>
        /// T3: Goal-state for the "set goal (slow tick), drive towards goal (per-frame)" loop.
        /// PRODUCER: RunAlliedOperations / RunRTSNavi / RunEnemyOperations, staggered by
        ///   TankAIManager.OperationsToUpdateThisFrame (~every KickStart.AIClockPeriod frames).
        /// CONSUMER: UpdateTechControl -> MovementController.DriveDirector, every vanilla
        ///   physics tick.
        /// STALENESS CONTRACT: this value WILL lag the world by up to AIClockPeriod frames
        /// in nominal conditions, and may lag arbitrarily if ops ticks are skipped. Movement
        /// code (DriveDirector / DriveMaintainer) MUST remain safe when reading a stale goal
        /// (e.g. dest already passed). Stale-age > AIClockPeriod*3 logs a diagnostic.
        /// Callers mutating via `ref ControlOperator` (TryHandleObstruction, SetLastDest)
        /// must call MarkOperatorDirty() to keep age tracking honest.
        /// </summary>
        private EControlOperatorSet ControlOperator = EControlOperatorSet.Default;
        // Baseline frame for staleness. Primed to "now" on every (re)activation in OnEnable so a
        // pooled helper returning to service isn't measured against a tick from a previous life
        // (or the 0 default), then refreshed by SetDirectedControl / MarkOperatorDirty.
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
        internal bool IsDirectedMovingFromDest => ControlOperator.DriveDest == EDriveDest.FromLastDestination;

        internal EDriveFacing DriveDirDirected => ControlOperator.DriveDir;
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
            // P08 B-NEW8-2: NaN-guard the persisted struct. EControlCoreSet.lastDestination has
            // a NaN guard in its property setter, but bulk struct-copy bypasses it. Check before
            // assignment so a tainted destination becomes Default rather than poisoning Maintainer.
            if (cont.lastDestination.IsNaN())
            {
                DebugTAC_AI.Exception("SetCoreControl - cont.lastDestination was NaN; falling back to Default");
                ControlCore = EControlCoreSet.Default;
                return;
            }
            ControlCore = cont;
        }

        internal bool DoSteerCore => ControlCore.DriveDir > EDriveFacing.Neutral;

        internal bool AdviseAwayCore => ControlCore.DriveDest == EDriveDest.FromLastDestination;

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
        /*
        internal bool AvoidStuff {
            get { return _AvoidStuff; }
            set {
                if (!value)
                    DebugTAC_AI.Log("AvoidStuff disabled by: " + StackTraceUtility.ExtractStackTrace().ToString());
                _AvoidStuff = value;
            }
        }*/

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
                    //DebugTAC_AI.Assert(true, "RTSControlled set to " + value);
                    isRTSControlled = value;
                    foreach (ModuleAIExtension AIEx in AIList)
                    {
                        AIEx.RTSActive = isRTSControlled;
                    }
                }
            }
        } // force the tech to be controlled by RTS
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
                // Risk-1 fix: compute the new RTSDestInternal BEFORE broadcasting. Previously the
                // broadcast read the stale value, so MP clients received the prior waypoint on every set.
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
            // B6: MT slaves have no autonomous nav — their pose is driven by their mimic-host
            // every tick. Forward the waypoint to the host so clicking any sub-piece in RTS
            // mode works as the player expects.
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
        public Func<TankAIHelper, ExtControlStatus, bool> AIControlOverride = null;
        public bool PlayerAllowAutoAnchoring = false;   // Allow auto-anchor
        public bool ExpectAITampering = false;

        // ----------------------------  AI Cores  ---------------------------- 
        public IMovementAIController MovementController;
        public AIEAutoPather autoPather => (MovementController is AIControllerDefault def) ? def.Pathfinder : null;

        // ----------------------------  Awareness Subscriptions  ----------------------------
        // B5: bounded-retry counter for DelayedSubscribe self-recovery. Cleared on success or in Recycled.
        private int delayedSubscribeRetries = 0;
        private const int MaxDelayedSubscribeRetries = 5;

        public TankAIHelper Subscribe()
        {
            if (tank != null)
            {
                DebugTAC_AI.Assert("Game attempted to fire Subscribe for TankAIHelper twice.");
                return this;
            }
            tank = GetComponent<Tank>();
            // B6: defensive null guard. If GetHelperInsured was ever called on a GameObject without
            // a Tank component, the original code NRE'd on the next line with no context. Now we
            // log loud and destroy the orphan helper so it can't poison downstream consumers.
            if (tank == null)
            {
                DebugTAC_AI.LogError(KickStart.ModID + ": TankAIHelper.Subscribe - attached to GameObject '"
                    + (gameObject ? gameObject.name : "<destroyed>") + "' without a Tank component. Destroying orphan helper.");
                Destroy(this);
                return null;
            }
            // Intentional: force lazy init of cached bounds before downstream code reads them.
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
            // T6: nameof gives compile-time rename safety; AISubscribeDelay names the magic 0.1f
            // (documents why the delay exists — lets blockBounds/blockCount settle).
            Invoke(nameof(DelayedSubscribe), AIGlobals.AISubscribeDelay);
            return this;
        }
        public void DelayedSubscribe()
        {
            // B5: precondition guard — tank may have been recycled/pooled during the 0.1s window,
            // or blockman may not yet have finished building.
            if (this == null || tank == null || tank.blockman == null)
            {
                if (++delayedSubscribeRetries <= MaxDelayedSubscribeRetries && tank != null && enabled)
                {
                    Invoke(nameof(DelayedSubscribe), AIGlobals.AISubscribeDelay);
                    return;
                }
                // Risk-4 fix: distinct key from the partial-init catch below so the popups don't dedup each other.
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
            // B5: narrowed catch — only the realistic exceptions (component lifecycle races) are
            // swallowed. Logic bugs in ExecuteAutoSetNoCalibrate/SetDriverType now surface.
            catch (Exception e) when (e is NullReferenceException || e is MissingReferenceException)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey("DelayedSubscribe.partial:" + tank.name,
                    "DelayedSubscribe partial init for " + tank.name, e);
            }
            finally
            {
                // ALWAYS mark dirty so UpdateLastTechExtentsIfNeeded recovers next frame.
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
            //DebugTAC_AI.Log(KickStart.ModID + ": On Attach " + tank.name);
            EstTopSped = 1;
            //LastBuildClock = 0;
            PendingHeightCheck = true;
            dirtyExtents = true;
            // BUG-8: the weapon-loadout LOS classification is computed lazily once and cached. A
            // composition change can flip the tech between needing LOS (direct fire) and not
            // (indirect/artillery). For NonPlayer the DirtyAndReboot path below clears it via
            // ResetOnSwitchAlignments, but for Player techs ApplyPlayerAlignment early-returns and
            // never resets it. Null it here so the next SyncLineOfSight (gated to TargetValidationDelay,
            // ~0.6s) re-derives from the surviving blocks. No per-tick cost.
            WeaponAimType = AIWeaponType.Unknown;
            dirtyAI = AIAlign == AIAlignment.NonPlayer ? AIDirtyState.DirtyAndReboot : AIDirtyState.Dirty;
            if (AIAlign == AIAlignment.Player)
            {
                PendingPlayerRecompose = true;   // P11: re-pick movement class/core on composition change
                try
                {
                    if (!tank.FirstUpdateAfterSpawn && !PendingDamageCheck && TechMemor)
                    {
                        //DebugTAC_AI.Log(KickStart.ModID + ": Saved TechMemor for " + tank.name);
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
            // BUG-8: re-derive weapon LOS classification on block loss. Losing all direct-fire
            // weapons (turret shot off) and being left with only indirect artillery must drop the
            // cached Direct classification, or the tech keeps requiring LOS it no longer needs and
            // CheckEnemyAndAiming keeps raycasting/blacklisting targets artillery could arc onto.
            // Lazy: next SyncLineOfSight recomputes (gated to TargetValidationDelay). No per-tick cost.
            WeaponAimType = AIWeaponType.Unknown;
            // Risk-3 fix: mirror OnBlockAttached. Losing key blocks mid-combat (wings shot off,
            // last hover module destroyed) needs to re-classify NonPlayer techs so AICore reflects
            // the surviving block configuration instead of the original EvilCommander assignment.
            if (AIAlign == AIAlignment.NonPlayer)
                dirtyAI = AIDirtyState.DirtyAndReboot;
            if (AIAlign == AIAlignment.Player)
            {
                PendingPlayerRecompose = true;   // P11: re-pick movement class/core on composition change
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
            // B5: cancel any pending DelayedSubscribe so it doesn't fire on a torn-down helper.
            CancelInvoke(nameof(DelayedSubscribe));
            delayedSubscribeRetries = 0;
            // B4/B7: clear the one-shot regen latch so recovery can fire again on next spawn.
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

        // T3: re-prime the ControlOperator staleness baseline on (re)activation. The helper
        // component is pooled with its Tank GameObject, so a recycled-then-respawned tech
        // re-enters service via `enabled = true` (OnTankAddition / OnTankChange) WITHOUT
        // re-running Subscribe(). Without this, controlOperatorSetTick still held its value
        // from a previous life (or the 0 default on first spawn), and the per-frame consumer
        // (RunMovementBridge -> UpdateTechControl) computed ControlOperatorAgeFrames =
        // Time.frameCount - <ancient tick> = the whole scene's frame count, tripping the
        // "ControlOperator stale by N frames" warning once per spawn before the first staggered
        // Operations pass (~AIClockPeriod frames out) could set it honestly. Unity fires OnEnable
        // synchronously at the `enabled = true` assignment, i.e. during spawn-event dispatch and
        // before the control loop runs this tech, so the baseline is fresh by the time any
        // consumer reads it. Genuine ops-starvation (no Operations pass within AIClockPeriod*3
        // of activation) still trips the warning correctly.
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
            MarkOperatorDirty();  // T3: keep age tracking honest on in-place mutation

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
                    //DebugTAC_AI.Log(KickStart.ModID + ": Anonymous sender error");
                    //return;
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
                        SetDriverType(driver);  // T5: route through chokepoint so AutoSet resolves
                    }
                    RequestMovementControllerSwap(MovementSwapReason.RemoteAIType);

                    //TankDescriptionOverlay overlay = (TankDescriptionOverlay)GUIAIManager.bubble.GetValue(tank);
                    //overlay.Update();
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
            SecondAvoidence = false;// Should the AI avoid two techs at once?
            ChaseThreat = true;
            ActionPause = 0;

            if (tank.PlayerFocused)
            {   // player gets full control
                AIWorkingModes = AIEnabledModes.All;
            }
            else
            {
                AIWorkingModes = AIEnabledModes.None;
                /*
                isAegisAvail = false;
                isAssassinAvail = false;

                isProspectorAvail = false;
                isScrapperAvail = false;
                isEnergizerAvail = false;

                isAstrotechAvail = false;
                isAviatorAvail = false;
                isBuccaneerAvail = false;
                */
            }

            AIList.Clear();
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
            // REMOVE any AI states that have been removed!!!
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
            // D2: removed dead `if (hasAnchorableAI)` block — both `hasAnchorableAI` and
            // `TryAnchor()` no longer exist. Auto-anchor is now handled per-frame by
            // TryInsureAutoAnchor() driven from AIControllerStatic.DriveDirector + behavior
            // modules, with proper CanAutoAnchor / CanAnchorSafely guards.
        }
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
                RunState = AIRunState.Default;   // ControlTech_Prefix returns true → vanilla runs
                AIAlign = AIAlignment.Static;    // still ignore him for combat targeting
                t.AI.TryGetCurrentAIType(out AITreeType.AITypes curTree);
                DebugTAC_AI.Log(KickStart.ModID + ": Neutral Tech " + t.name
                    + " has vanilla AI tree " + curTree + " — handing control to vanilla.");
            }
        }

        public void ResetOnSwitchAlignments(Tank unused)
        {
            // Invariant: a live alignment switch should already own a MovementController (created in
            // Subscribe -> SetupDefaultMovementAIController). DebugTAC_AI.Assert logs only when the
            // condition is true, so this fires precisely when the controller IS null - which is
            // expected during teardown (Recycled calls this) and otherwise means an alignment switch
            // ran before Subscribe set the controller up. Either case is recovered lazily by
            // OnPreUpdate / RecalibrateMovementAIController; this is a diagnostic, not a guard.
            DebugTAC_AI.Assert(MovementController == null,
                "ResetOnSwitchAlignments: MovementController null on " + (tank.IsNotNull() ? tank.name : "<recycled>")
                + " - expected during teardown, otherwise an alignment switch ran before Subscribe set it up.");
            //DebugTAC_AI.Log(KickStart.ModID + ": Resetting all for " + tank.name);
            maxBlockCount = tank.blockman.blockCount;
            WantsToFight = false;            lastAIType = AITreeType.AITypes.Idle;
            lastAITypeResolved = true;  // B7: genuine reset to Idle (not resolution failure)
            AttackMode = EAttackMode.AutoSet;
            dirtyExtents = true;
            dirtyAI = AIDirtyState.Dirty;
            PlayerAllowAutoAnchoring = !tank.IsAnchored;
            ExpectAITampering = false;
            GroundOffsetHeight = AIGlobals.GroundOffsetGeneralAir;
            Provoked = 0;
            ActionPause = 0;
            KeepEnemyFocus = false;
            _losLostGraceTimer = 0f;        // B7: clear LOS-grace on alignment swap
            ResetDamageAccumulator();       // B2/T2: clear per-attacker damage buckets
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
            // P08 B-NEW10-2 + P09 T-9-1: SettleDown() already resets ControlCore (SetCoreControlStop)
            // and clears IsTryingToUnjam — the previously-explicit calls here were redundant.
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

            //enabled = false; // why the heck did I put this here? this is WHY EVERYTHING WAS BROKEN

            //TankDescriptionOverlay overlay = (TankDescriptionOverlay)GUIAIManager.bubble.GetValue(tank);
            //overlay.Update();
        }

        // Re-entrancy-safe controller swap (build-then-publish). The new controller is fully
        // constructed and Initiate()d into a local before MovementController is reassigned, and the
        // old controller is recycled only AFTER the field points at the new one. Recycle() ->
        // DestroyImmediate can synchronously re-enter via engine callbacks (e.g. OnMoveWorldOrigin)
        // that read MovementController, so this guarantees the field is never observably null and
        // never points at a half-Initiated controller (AICore == null) mid-swap. Same-type swaps
        // reuse the existing component (re-Initiate in place), matching prior GetOrAddComponent behavior.
        private T SwapMovementController<T>(EnemyMind mind) where T : Component, IMovementAIController
        {
            if (MovementController is T existing)
            {
                existing.Initiate(tank, this, mind);
                return existing;
            }
            IMovementAIController previous = MovementController;
            T built = gameObject.AddComponent<T>();
            built.Initiate(tank, this, mind);   // fully wired (Tank/Helper/AICore) before publish
            MovementController = built;          // atomic single publish
            if (previous != null)
                previous.Recycle();              // teardown AFTER the field already points at the new one
            return built;
        }

        private void SetupDefaultMovementAIController()
        {
            UsingAirControls = false;
            SwapMovementController<AIControllerDefault>(null);
            LogMovementControllerSwapIfChanged();
        }

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
                    // P08 G.7 escape hatch: if the existing AIControllerAir has flagged the tech
                    // Grounded (= TestForMayday verdict: damaged beyond flight), demote to ground
                    // class instead of re-installing Air. Next CheckRebuildAlignment cycle reads
                    // the new EvilCommander and routes through AIControllerDefault. Prevents the
                    // "rotorless airplane flailing forever on AIControllerAir" trap.
                    // P08 G.8: instead of hardcoding Wheeled, re-run BlockSetEnemyHandling so the
                    // demoted tech gets the best surviving-block class (Naval if floaters,
                    // Stationary if anchored, Wheeled as final fallback).
                    if (MovementController is AIControllerAir existingAir && existingAir.Grounded)
                    {
                        var oldClass = enemy.EvilCommander;
                        Enemy.RCore.BlockSetEnemyHandling(tank, enemy, false);
                        // Defensive guard: if the classifier still picks an air class (shouldn't
                        // happen for a Grounded tech, but guard against pathological cases),
                        // force Wheeled to avoid an infinite demote-and-recompute loop.
                        if (MovementDispatch.ContainerForEnemy(enemy.EvilCommander) == MovementContainerKind.Air)
                            enemy.EvilCommander = EnemyHandling.Wheeled;
                        DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name + " demoting from "
                            + oldClass + " → " + enemy.EvilCommander + " (Grounded — no longer flightworthy)");
                        // fall through to default branch below
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
                // B2: was `throw new Exception`. Throwing from inside RecalibrateMovementAIController
                // poisons the OnUpdateHostAIDirectors / OnUpdateClientAIDirectors loop for every
                // other tank that frame. Caller (RecalibrateMovementAIController) now self-heals
                // the invariant; if it gets here with null enemy despite that, log loud and fall
                // through to default movement so the tech becomes a non-combat dummy until next
                // CheckRebuildAlignment cycle restores state.
                DebugTAC_AI.LogError(KickStart.ModID + ": RecalMoveAIControllerNPT for " + tank.name
                    + " reached with null EnemyMind despite caller guard — falling back to default.");
                return true;
            }
        }
        private bool RecalMoveAIControllerPlayer()
        {
            if (MovementDispatch.ContainerForPlayer(DriverType) == MovementContainerKind.Static && AnchorState != AIAnchorState.Unanchor)
            {
                SwapMovementController<AIControllerStatic>(null);
                return false;
            }
            else if (MovementDispatch.ContainerForPlayer(DriverType) == MovementContainerKind.Air)
            {
                // P08 G.8: mirror the NPT Grounded-demote escape hatch for player-allied air techs.
                // Player path is keyed on DriverType (not EvilCommander); on demote we switch to
                // Tank so the next recal installs AIControllerDefault → LandAICore. Without this,
                // an allied airplane that loses wings would glide-crash and become a permanent
                // dead tech still attached to AIControllerAir.
                if (MovementController is AIControllerAir existingAir && existingAir.Grounded)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name + " (player) demoting from Pilot (Grounded — no longer flightworthy)");
                    DriverType = AIDriverType.Tank;
                    // fall through to default branch
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
                //DebugTAC_AI.Assert("RecalibrateMovementAIController for " + tank.name + ", type " + DriverType);
                UsingAirControls = false;
                var enemy = gameObject.GetComponent<EnemyMind>();
                // B2: self-heal the AIAlign==NonPlayer && no-EnemyMind invariant violation. Common
                // causes: OnPreUpdate firing before CheckRebuildAlignment on a respawn, or external
                // EnemyMind destruction. MP-aware: client must not author EnemyMind (host owns
                // enemy state), so downgrade to Static and re-dirty for host sync.
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
                    // Re-fetch in case the self-heal or NPT branch installed a new Mind.
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

        // P11 HIGH-fix: a player tech that gains/loses locomotion blocks while ALREADY classified
        // Player never re-picked its movement controller or AICore. OnBlockAttached/OnBlockDetaching
        // only set dirtyAI=Dirty (not DirtyAndReboot) for Player, and ApplyPlayerAlignment early-returns
        // for an already-Player tech, so DriverType was never re-derived and MovementAIControllerDirty
        // was never raised. This re-derives DriverType from the current blocks (the same auto-detect
        // path used at Subscribe) and requests a controller swap only when the class actually changed -
        // or when on AIControllerAir, where the inner FlyStyle/core (Heli/VTOL/Airplane) can change
        // without DriverType changing. Debounced via PendingPlayerRecompose so a multi-block build edit
        // collapses to a single evaluation, and a swap is requested only on a real change (no thrash).
        // NOTE: DriverType is treated here as auto-derived-from-blocks (the contract used at Subscribe
        // and by the Grounded-demote hatch). If a manual "driver pin" is ever added, gate this on it.
        private bool PendingPlayerRecompose = false;
        private void ReevaluatePlayerMovementIfNeeded()
        {
            if (!PendingPlayerRecompose)
                return;
            PendingPlayerRecompose = false;
            if (AIAlign != AIAlignment.Player)
                return;
            AIDriverType before = DriverType;
            ExecuteAutoSetNoCalibrate();   // re-derive DriverType from current blocks + availability gates
            if (DriverType != before || MovementController is AIControllerAir)
                RequestMovementControllerSwap(MovementSwapReason.PlayerRecompose);   // deferred swap (consumed in OnUpdate*AIDirectors)
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

        internal void OnHit(ManDamage.DamageInfo dingus)
        {
            // B2/T2: trip on EITHER a single big hit OR a sustained sub-threshold series
            // from the same attacker. Big-hit fast path preserves original semantics.
            bool tripped = dingus.Damage > AIGlobals.DamageAlertThreshold;
            Tank src = dingus.SourceTank;
            bool srcAlive = (bool)src;
            if (!tripped && srcAlive)
            {
                if (AccumulateAndCheckThreat(src, dingus.Damage))
                    tripped = true;
            }
            if (!tripped) return;

            // B3: target-independent reactions (Provoked / FIRE_ALL / UI banner / cache
            // invalidation) fire whether or not the attacker is still alive. A kamikaze
            // or self-destruct-on-impact destroys SourceTank in the same frame, but the
            // defender absolutely should still react.
            Provoked = AIGlobals.ProvokeTime;
            InvalidateTargetCache();  // T5: damage source may be a new fast threat
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

            // Target-dependent reactions: only when SourceTank is still alive.
            if (srcAlive && SetPursuit(src.visible, force: true))
            {
                if (tank.IsAnchored)
                {
                    // Execute remote orders to allied units - Attack that threat!
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

            SettleDown();
            ReleaseTarget();
            WantsToFight = false;
            WeaponDelayClock = 0;
            MTOffsetPos = Vector3.zero;
            MTOffsetRot = Vector3.forward;
            MTOffsetRotUp = Vector3.up;
            MTLockedToTechBeam = false;
            MTMimicHostAvail = false;
            //World.PlayerRTSControl.ReleaseControl(this);
        }
        public void SetAIControl(AITreeType.AITypes type)
        {
            //DebugTAC_AI.Log(KickStart.ModID + ": ForceAllAIsToEscort() - Setting AIType to " + type);
            tank.AI.SetBehaviorType(type);
            //DebugTAC_AI.Log(KickStart.ModID + ": ForceAllAIsToEscort() - Set AIType");
        }
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
            //DebugTAC_AI.Log(KickStart.ModID + ": ForceAllAIsToEscort()");
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
                    //DebugTAC_AI.Log(KickStart.ModID + ": ForceAllAIsToEscort() - Getting AIType");
                    if (tank.AI.TryGetCurrentAIType(out AITreeType.AITypes type))
                        DebugTAC_AI.Info(KickStart.ModID + ": AI type is " + type.ToString());
                    //DebugTAC_AI.Log(KickStart.ModID + ": ForceAllAIsToEscort() - Got AIType");
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
                        lastAITypeResolved = true;  // B7: explicit Idle assignment, not resolution failure
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

        // ----------------------------  GUI Formatter  ---------------------------- 
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
                {   // Show auto-anchor debug
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
                                    output = AILOC.Gen_MoveTo + StringLookup.GetItemName(theResource.m_ItemType); //theResource.name;
                                                                                                                 //StringLookup.GetItemName(new ItemTypeInfo(ObjectTypes.Chunk, (int)CT));
                            }
                            else
                            {
                                if (!CT.Any())
                                    output = AILOC.Collect + "rock";
                                else
                                    output = AILOC.Collect + StringLookup.GetItemName(theResource.m_ItemType);//theResource.name;
                                                                                                          //output = "Mining " + StringLookup.GetItemName(new ItemTypeInfo(ObjectTypes.Chunk, (int)CT));
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
            /*
            if (PursuingTarget)
            {
                output = "Getting revenge for comrade";
                return;
            }*/
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
                                        output = AILOC.Gen_MoveTo + StringLookup.GetItemName(theResource.m_ItemType);//theResource.name;
                                                                                                                    //StringLookup.GetItemName(new ItemTypeInfo(ObjectTypes.Chunk, (int)CT));
                                }
                                else
                                {
                                    if (CT.Any())
                                        output = AILOC.Collect + "rock";
                                    else
                                        output = AILOC.Collect + StringLookup.GetItemName(theResource.m_ItemType);//theResource.name;
                                                                                                              //output = "Mining " + StringLookup.GetItemName(new ItemTypeInfo(ObjectTypes.Chunk, (int)CT));
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

        // ----------------------------  Information Handling  ---------------------------- 
        private int maxBlockCount = 1;
        private int lastBlockCount = 1;
        public bool CanDetectHealth()
        {
            return true;//TechMemor || AdvancedAI; 
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
                return 0; // Slow/Stopped
            if (IsTryingToUnjam)
                return 0;
            if (Attempt3DNavi || MovementController is AIControllerAir)
            {
                return SafeVelocity.magnitude;
            }
            else
            {
                if (!(bool)tank.rootBlockTrans)
                    return 0; // There's some sort of error in play
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
        public bool IsTechMovingAbs(float minSpeed)
        {
            if (tank.rbody.IsNull())
                return true; // Stationary techs do not get the panic message
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
                // A tech pivoting in place to face its goal is making progress, not stuck. Counts the
                // bad-turn-radius "twitch" pattern as motion so it doesn't feed the unjam FSM.
                return Mathf.Abs(tank.rbody.angularVelocity.y) > AIGlobals.AngularProgressThreshold;
            }
        }
        // P09 B-1-4: IsTechMovingSigned had zero callers and a dead negative-minSpeed branch.
        // Removed. IsTechMovingAbs (unsigned magnitude) and IsTechMovingActual (no throttle bypass)
        // cover all current consumers.
        public bool IsTechMovingActual(float minSpeed)
        {
            if (tank.rbody.IsNull())
                return true; // Stationary techs do not get the panic message
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
                return Mathf.Abs(tank.rbody.angularVelocity.y) > AIGlobals.AngularProgressThreshold;
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
        public Visible GetPlayerTech()
        {
            // B10: null-guard the failing operations explicitly; on exception, invalidate
            // lastPlayer (returning null) so callers' IsNotNull() guards short-circuit
            // instead of acting on stale state. Allied tanks no longer chase a torn-down
            // player tank for ticks after the player disconnects/respawns.
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
                // Teammate not found this tick — stale lastPlayer is unsafe; invalidate.
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

        // ----------------------------  Primary Operations  ---------------------------- 
        /// <summary>
        /// T4: per-frame movement bridge from ModuleTechController.ExecuteControl_Prefix.
        /// Returns true if mod AI took over (suppressing vanilla); false to let vanilla run.
        /// Distinct from GlobalPatches.TechAIPatches.ControlTech_Prefix (the Harmony prefix
        /// on vanilla TechAI.ControlTech, which Harmony's name convention locks to that name).
        /// </summary>
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
                        {   // override EVERYTHING
                            return true;
                            //return false;
                        }
                        else if (RunState == AIRunState.Advanced)
                        {
                            if (AIAlign == AIAlignment.Player)
                            {
                                //DebugTAC_AI.Log(KickStart.ModID + ": AI Valid!");
                                //DebugTAC_AI.Log(KickStart.ModID + ": (TankAIHelper) is " + tank.gameObject.GetComponent<AIEnhancedCore.TankAIHelper>().wasEscort);
                                //tankAIHelp.AIState &&
                                if (SetToActive)
                                {
                                    //DebugTAC_AI.Log(KickStart.ModID + ": Running BetterAI");
                                    //DebugTAC_AI.Log(KickStart.ModID + ": Patched Tank ExecuteControl(TankAIHelper)");
                                    UpdateTechControl(thisControl);
                                    return true;
                                }
                            }
                            else if (AIAlign == AIAlignment.NonPlayer)// && KickStart.enablePainMode)
                            {   // This should turn off ONLY for land enemy AI!
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
                    {   // override EVERYTHING
                        return true;
                    }
                    else if (RunState == AIRunState.Advanced)
                    {
                        if (AIAlign == AIAlignment.Player)
                        {
                            //DebugTAC_AI.Log(KickStart.ModID + ": AI Valid!");
                            //DebugTAC_AI.Log(KickStart.ModID + ": (TankAIHelper) is " + tank.gameObject.GetComponent<AIEnhancedCore.TankAIHelper>().wasEscort);
                            if (tank.PlayerFocused)
                            {
                                //SetRTSState(true);
                                UpdateTechControl(thisControl);
                                return true;
                            }
                            else if (SetToActive)
                            {
                                //DebugTAC_AI.Log(KickStart.ModID + ": Running BetterAI");
                                //DebugTAC_AI.Log(KickStart.ModID + ": Patched Tank ExecuteControl(TankAIHelper)");
                                UpdateTechControl(thisControl);
                                return true;
                            }
                        }
                        else if (AIAlign == AIAlignment.NonPlayer)//KickStart.enablePainMode 
                        {   // This should turn off ONLY for land enemy AI!
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
        {   // The interface method for actually handling the tank - note that this fires at a different rate
            if (AIControlOverride != null && !AIControlOverride(this, ExtControlStatus.MaintainersAndDirectors))
                return;
            CurHeight = -500;

            // B12: actively self-heal instead of waiting for next OnPreUpdate, dedup the
            // log per-tank, and escalate after N consecutive failures so a persistent
            // invariant violation is observable instead of silent.
            if (MovementController is null)
            {
                RecalibrateMovementAIController();
                if (MovementController is null)
                {
                    consecutiveNullMovementControllerTicks++;
                    string tankKey = tank ? tank.name : "<recycled>";
                    if (consecutiveNullMovementControllerTicks == 1)
                        DebugTAC_AI.LogWarnPlayerOncePerKey(
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

            // T3: surface dangerously-stale ControlOperator so over-budget AI populations
            // (helpersActive.Count >> AIClockPeriod) become observable instead of silently
            // producing torn movement decisions.
            if (IsControlOperatorStale)
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "ControlOperatorStale:" + (tank ? tank.name : "<recycled>"),
                    "AI " + (tank ? tank.name : "<recycled>") + ": ControlOperator stale by " +
                    ControlOperatorAgeFrames + " frames (>" + (KickStart.AIClockPeriod * 3) + ").", null);

            AIEBeam.BeamMaintainer(thisControl, this, tank);
            if (UpdateDirectorsAndPathing)
            {
                //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Fired CollisionAvoidUpdate!");
                // B11: per-site, per-tank dedup so 4 distinct failure modes stay distinguishable
                // and one scene-wide first-failure doesn't mute every subsequent site.
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

                UpdateDirectorsAndPathing = false; // incase they fall out of sync
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
            // B9: ActionPause decrement moved to OnPreUpdate (runs every tick, not staggered).

            // B4: target-focus bookkeeping is an invariant of every allied tick, regardless
            // of who is driving — a manually-driven tank must not hold a stale lock on a
            // vanished enemy, and the player should be able to "un-provoke" by disengaging.
            UpdateTargetCombatFocus();

            if (tank.PlayerFocused)
            {
                //updateCA = true;
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
                        Retreat = DetermineRetreatPosture();  // B5: single assignment, no compounding mutation
                        if (RTSControlled)
                        {
                            // T6 (symmetric): player-side RTS detour. One log per tank per session.
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
            // B7: distinguish resolution failure from genuine Idle. Log on transition only.
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
                //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Fired DelayedUpdate!");

                //updateCA = true;
                //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  current mode " + DediAI.ToString());

                Retreat = DetermineRetreatPosture();  // B5: single assignment, no compounding mutation

                // B6: MT slaves can't receive RTS directly — non-MT host gets the waypoint
                // and drags affiliated MTs via MultiTechsAffiliated/lastTechExtents.
                if (RTSControlled && IsRTSReceivable)
                {   //Overrides the Allied Operations for RTS Use
                    // T6 (symmetric): allied-side RTS detour. One log per tank per session.
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "AlliedRTSDetour:" + tank.name,
                        "AI " + tank.name + ": allied-side RTS detour active (RunRTSNavi instead of OpsController.Execute).", null);
                    RunRTSNavi();
                }
                else
                    OpsController.Execute();
            }
        }
        private void RunEnemyOperations(bool light = false)
        {
            //BEGIN THE PAIN!
            //updateCA = true;
            // B9: ActionPause decrement moved to OnPreUpdate (runs every tick, not staggered).
            DetermineRetreatPostureEnemy();
            if (light)
                RCore.BeEvilLight(this, tank);
            else
            {
                RCore.BeEvil(this, tank);
            }
        }

        private void RunRTSNavi(bool isPlayerTech = false)
        {   // Alternative Operator for RTS

            //ProceedToObjective = true;
            EControlOperatorSet direct = GetDirectedControl();
            if (DriverType == AIDriverType.Pilot)
            {
                if (DediAI == AIType.Escort && !IsGoingToPositionalRTSDest && 
                    (lastEnemyGet == null || !lastEnemyGet.isActive))
                    RTSDestination = tank.boundsCentreWorldNoCheck;

                GetDistanceFromTask2D(lastDestinationCore, 0);
                //lastOperatorRange = (DodgeSphereCenter - lastDestinationCore).magnitude;
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
                    {   // Far behind, must catch up
                        FullBoost = true; // boost in forwards direction towards objective
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
                    //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  RTS - resting");
                    if (DelayedAnchorClock < AIGlobals.BaseAnchorMinimumTimeDelay)
                        DelayedAnchorClock++;
                    //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": " + AutoAnchor + " | " + PlayerAllowAnchoring + " | " + (tank.Anchors.NumPossibleAnchors >= 1) + " | " + (DelayedAnchorClock >= AIGlobals.BaseAnchorMinimumTimeDelay) + " | " + !DANGER);
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
                {   // Time to go!
                    anchorAttempts = 0;
                    DelayedAnchorClock = 0;
                    //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  RTS - Moving");
                    if (unanchorCountdown > 0)
                        { /* unanchorCountdown self-counts via its AITimer now */ }
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
                    // P09 B-10-2: Player RTS uses IsTechMovingAbs (treats `Abs(GetDrive) < 0.5f`
                    // as moving — lenient, allows idle throttle to count as motion). Enemy RTS
                    // counterpart at TankAIHelper.cs ~3282 uses IsTechMovingActual (strict, no
                    // throttle bypass) and EnemyAISpeedPanicDividend. Asymmetry is intentional:
                    // enemies shouldn't bypass unjam-checks via idle throttle (they have no
                    // operator to recover them), while player RTS is more forgiving.
                    if (!IsTechMovingAbs(EstTopSped / AIGlobals.PlayerAISpeedPanicDividend))
                    {   //OBSTRUCTION MANAGEMENT
                        TryHandleObstruction(true, lastOperatorRange, false, true, ref direct);
                    }
                    else
                    {
                        //var val = LocalSafeVelocity.z;
                        //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Output " + val + " | TopSpeed/2 " + (EstTopSped / 2) + " | TopSpeed/4 " + (EstTopSped / 4));
                        /*
                        ThrottleState = AIThrottleState.ForceSpeed;
                        float driveVal = Mathf.Min(1, lastOperatorRange / 10);
                        DriveVar = driveVal;
                        */
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
        {   // Alternative Operator for RTS
            //DebugTAC_AI.Log("RunRTSNaviEnemy - " + tank.name);
            /*
            if (!KickStart.AllowStrategicAI)
            {
                RTSControlled = false;
                return;
            }*/
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
                    {   // Far behind, must catch up
                        FullBoost = true; // boost in forwards direction towards objective
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
                    //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  RTS - resting");
                    if (DelayedAnchorClock < AIGlobals.BaseAnchorMinimumTimeDelay)
                        DelayedAnchorClock++;
                    //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": " + AutoAnchor + " | " + PlayerAllowAnchoring + " | " + (tank.Anchors.NumPossibleAnchors >= 1) + " | " + (DelayedAnchorClock >= 15) + " | " + !DANGER);
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
                {   // Time to go!
                    anchorAttempts = 0;
                    DelayedAnchorClock = 0;
                    //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  RTS - Moving");
                    if (unanchorCountdown > 0)
                        { /* unanchorCountdown self-counts via its AITimer now */ }
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
                    {   //OBSTRUCTION MANAGEMENT
                        TryHandleObstruction(true, lastOperatorRange, false, true, ref direct);
                    }
                    else
                    {
                        //var val = LocalSafeVelocity.z;
                        //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Output " + val + " | TopSpeed/2 " + (EstTopSped / 2) + " | TopSpeed/4 " + (EstTopSped / 4));
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

        // OnPreUpdate -> Directors -> Operations -> OnPostUpdate
        internal void OnPreUpdate()
        {
            if (MovementController == null)
            {
                DebugTAC_AI.Assert(MovementController == null, "MOVEMENT CONTROLLER IS NULL");
                //SetupDefaultMovementAIController();
                RecalibrateMovementAIController();
            }
            recentSpeedSigned = GetSpeed();
            recentSpeed = recentSpeedSigned;
            if (recentSpeed < 1)
                recentSpeed = 1;
            // B2: EstTopSped tracking moved here from HostAIOperations and ClientAIOperations.
            // Runs every Pre tick (not staggered) so the high-water mark is never under-sampled.
            if (EstTopSped < recentSpeed)
                EstTopSped = recentSpeed;
            // actionPause now self-counts via its backing AITimer (seconds-based) — no manual decrement needed.
            UpdateLastTechExtentsIfNeeded();
            CheckRebuildAlignment();
            // B3: latch the alignment for this tick AFTER CheckRebuildAlignment so any pending
            // dirtyAI mutation has been consumed. Directors + Operations (which may be staggered
            // across many frames) both read TickAIAlign and see a consistent value.
            tickAIAlign = AIAlign;
            tickAlignmentLatched = true;
            UpdateCollectors();
        }
        internal void OnPostUpdate()
        {
            // B3: release the latch; next OnPreUpdate re-latches after CheckRebuildAlignment.
            tickAlignmentLatched = false;
            ManageAILockOn();
            UpdateBlockHold();
            RunPostOps();
            ShowCollisionAvoidenceDebugThisFrame();
        }
        private static List<Tank> TempMultiTechRecalibrate = new List<Tank>();
        private void UpdateLastTechExtentsIfNeeded()
        {//Handler for the improved AI, gets the job done.
            try
            {
                if (dirtyExtents)
                {
                    dirtyExtents = false;
                    tank.blockman.CheckRecalcBlockBounds();
                    lastTechExtents = (tank.blockBounds.size.magnitude / 2) + 2;
                    // Insure we are STILL the tracking target!
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

        // if (!OverrideAllControls), then { Directors -> Operations }
        internal void OnUpdateHostAIDirectors()
        {
            try
            {
                if (MovementAIControllerDirty)
                    RecalibrateMovementAIController();
                if (RunState == AIRunState.Advanced)
                {
                    switch (TickAIAlign)  // B3: tick-stable snapshot
                    {
                        case AIAlignment.Player: // Player-Controlled techs
                            UpdateDirectorsAndPathing = true;
                            break;
                        case AIAlignment.NonPlayer: // Enemy / Enemy Base Team
                            if (KickStart.enablePainMode)
                                UpdateDirectorsAndPathing = true;
                            break;
                        default:// Static tech
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
                switch (TickAIAlign)  // B3: tick-stable snapshot
                {
                    case AIAlignment.Player: // Player-Controlled techs
                        if (!OverrideControl)
                            CheckEnemyAndAiming();
                        if (IsTryingToUnjam)
                        {
                            TryHandleObstruction(true, lastOperatorRange, false, true, ref ControlOperator);
                            MarkOperatorDirty();  // T3
                        }
                        else
                            RunAlliedOperations();
                        break;
                    case AIAlignment.NonPlayer: // Enemy / Enemy Base Team
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
                                        MarkOperatorDirty();  // T3
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
                                        MarkOperatorDirty();  // T3
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
                    default:// Static tech
                        DriveVar = 0;
                        RunStaticOperations();
                        break;
                }
            }
            catch (Exception e)
            {
                // B1: REVIVED original LogWarnPlayerOnce (uncommented). The throw added during
                // debugging was killing the entire scheduler pass (kept latching B8's static
                // updateErrored permanently). Per-key dedup lets distinct techs each surface
                // their first failure; subsequent identical failures stop popups but keep
                // the Debug.Log line firing for post-mortem.
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnUpdateHostAIOperations:" + (tank ? tank.name : "<recycled>"),
                    "OnUpdateHostAIOperations() Critical error on " + (tank ? tank.name : "<recycled>"), e);
            }
        }

        internal void OnUpdateClientAIDirectors()
        {
            if (MovementAIControllerDirty)
                RecalibrateMovementAIController();
            switch (TickAIAlign)  // B3: tick-stable snapshot
            {
                case AIAlignment.Static:// Static tech
                    DriveVar = 0;
                    break;
                case AIAlignment.Player: // Player-Controlled techs
                    UpdateDirectorsAndPathing = true;
                    break;
                case AIAlignment.NonPlayer: // Enemy / Enemy Base Team
                    UpdateDirectorsAndPathing = true;
                    break;
            }
        }
        internal void OnUpdateClientAIOperations()
        {
            // B2: wrapped in try/catch so a single client-ops exception can't cascade
            // through TankAIManager's outer try and kill all subsequent helpers' client ops
            // that frame. Behavior dispatch is intentionally absent — client is host-
            // authoritative for control state via networking. EstTopSped tracking moved to
            // OnPreUpdate (runs every tick, not staggered, so the high-water mark is accurate).
            try
            {
                if (TickAIAlign == AIAlignment.Static)  // B3: tick-stable snapshot (parity with other phases)
                {
                    DriveVar = 0;
                    RunStaticOperations();  // local-only ops parity with host path
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
        // T1: alignment-dispatch context. The original CheckRebuildAlignment had three near-
        // identical 60-line blocks (MP-client / MP-host / SP). The real per-context deltas are
        // narrow — see the per-Apply* methods below. Roles dispatch via this enum.
        private enum MpRole { SpHost, MpHost, MpClient }

        private void CheckRebuildAlignment()
        {
            if (tank.blockman.blockCount == 0)
                return; // IT'S NOT READY YET
            if (dirtyAI == AIDirtyState.Not)
                return;

            bool rebootSameAIAlign = dirtyAI == AIDirtyState.DirtyAndReboot;
            dirtyAI = AIDirtyState.Not;
            // B1: RunState is derived state owned by the rebuild dispatcher. Reset stale Default
            // (from a prior HandOffToVanillaForNeutral) before dispatch — the neutral-vanilla
            // branch below re-asserts Default if still applicable. Self-healing transitions.
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

                // Host-only preamble: flush extents to network before any alignment switch.
                if (role == MpRole.MpHost && dirtyExtents)
                {
                    dirtyExtents = false;
                    tank.netTech.SaveTechData();
                }

                DispatchAlignment(rebootSameAIAlign, role);
                ReevaluatePlayerMovementIfNeeded();   // P11: re-pick movement class after a player composition change
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "RebuildAlignment:" + (tank ? tank.name : "<recycled>"),
                    "RebuildAlignment() Critical error on " + (tank ? tank.name : "<recycled>"), e);
            }
        }

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
            // T1: the neutral-vanilla handoff is host-side only. Client never enters this branch
            // (matches the original code, which omitted it from the client block entirely).
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
            // Host/SP only: persist memorised tech blueprint. Client doesn't author this.
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
            // Host/SP only: set up the build-bookmark scaffold for blueprint-driven repairs.
            if (role != MpRole.MpClient)
                AIEBases.SetupBookmarkBuilder(this);
            AIAlign = AIAlignment.PlayerNoAI;
        }

        private void ApplyNonPlayerAlignment(bool rebootSame, MpRole role)
        {
            if (AIAlign == AIAlignment.NonPlayer && !rebootSame) return;
            ResetOnSwitchAlignments(tank);
            AIAlign = AIAlignment.NonPlayer;
            // T1: collapsed namespace inconsistency (was RCore.GenerateEnemyAI on client,
            // Enemy.RCore.GenerateEnemyAI on host/SP — same symbol, different qualifiers).
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

        // ----------------------------  Pathfinding Processor  ---------------------------- 
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
                //ManWorld.inst.GetTerrainHeight(tank.boundsCentreWorldNoCheck, out float height);
                //CurHeight = height;
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
            //DebugTAC_AI.Log(KickStart.ModID + ": GetOtherDir");
            Vector3 inputOffset = tank.boundsCentreWorldNoCheck - targetToAvoid.boundsCentreWorldNoCheck;
            float inputSpacing = targetToAvoid.GetCheapBounds() + lastTechExtents + DodgeStrength;
            Vector3 Final = tank.boundsCentreWorldNoCheck + (inputOffset.normalized * inputSpacing);
            return Final;
        }
        internal Vector3 GetDir(Tank targetToAvoid)
        {
            //DebugTAC_AI.Log(KickStart.ModID + ": GetDir");
            Vector3 inputOffset = tank.boundsCentreWorldNoCheck - targetToAvoid.boundsCentreWorldNoCheck;
            float inputSpacing = targetToAvoid.GetCheapBounds() + lastTechExtents + DodgeStrength;
            Vector3 Final = tank.boundsCentreWorldNoCheck - (inputOffset.normalized * inputSpacing);
            return Final;
        }

        internal static List<KeyValuePair<Vector3, float>> posWeights = new List<KeyValuePair<Vector3, float>>();
        internal Vector3 AvoidAssist(Vector3 targetIn, bool AvoidStatic = true)
        {
            //IsLikelyJammed = false;
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            // Skip sideways destination re-targeting during active combat retreat — it fights the
            // retreat vector and adds visible wiggle. Lower-level pathing still handles immediate
            // obstacle reactions; this just suppresses the high-level ally-spacing / scenery-dodge
            // displacement of the destination point.
            if (WasRetreatingInCombat)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssist IS NaN!!");
                //TankAIManager.FetchAllAllies();
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                posWeights.Clear();
                if (SecondAvoidence && AlliesAlt.Count > 1)// MORE processing power
                {
                    lastCloseAlly = AIEPathing.SecondClosestAlly(AlliesAlt, tank.boundsCentreWorldNoCheck, out Tank lastCloseAlly2, 
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name + " and " + lastCloseAlly2.name);
                            //IsLikelyJammed = true;
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name);
                            //IsLikelyJammed = true;
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
                    //DebugTAC_AI.Log(KickStart.ModID + ": Ally is " + lastAllyDist + " dist away");
                    //DebugTAC_AI.Log(KickStart.ModID + ": Trigger threshold is " + (lastTechExtents + Extremes(lastCloseAlly.blockBounds.extents) + 4) + " dist away");
                    //if (lastCloseAlly == null)
                    //    DebugTAC_AI.Log(KickStart.ModID + ": ALLY IS NULL");
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name);
                        //IsLikelyJammed = true;
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
                // P08 B-NEW8-1: was `if (IsDirectedMovingFromDest) Log(INVERTED); Log(normal);` —
                // an `if` with no `else` double-logged the inverted-path failure under two keys,
                // defeating LogWarnPlayerOnce dedup. Add the missing `else`.
                if (IsDirectedMovingFromDest)
                    DebugTAC_AI.LogWarnPlayerOnce("AvoidAssist()[INVERTED] Critical error", e);
                else
                    DebugTAC_AI.LogWarnPlayerOnce("AvoidAssist() Critical error", e);
                return targetIn;
            }
        }
        internal Vector3 AvoidAssistPrecise(Vector3 targetIn, bool AvoidStatic = true, bool IgnoreDestructable = false)
        {
            //  MORE DEMANDING THAN THE ABOVE!
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            // P08 B-NEW8-5: mirror the WasRetreatingInCombat early-return from AvoidAssist
            // (TankAIHelper.cs ~3580) so Path and PrecisePath behave consistently under retreat.
            // Author's intent in AvoidAssist was "suppress sideways re-targeting during retreat
            // to prevent wiggle"; PrecisePath should follow the same rule.
            if (WasRetreatingInCombat)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistPrecise IS NaN!!");
                //TankAIManager.FetchAllAllies();
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                posWeights.Clear();
                if (SecondAvoidence && AlliesAlt.Count > 1)// MORE processing power
                {
                    lastCloseAlly = AIEPathing.SecondClosestAllyPrecision(AlliesAlt, tank.boundsCentreWorldNoCheck, out Tank lastCloseAlly2, 
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name + " and " + lastCloseAlly2.name);
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name);
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
                    //DebugTAC_AI.Log(KickStart.ModID + ": Ally is " + lastAllyDist + " dist away");
                    //DebugTAC_AI.Log(KickStart.ModID + ": Trigger threshold is " + (lastTechExtents + Extremes(lastCloseAlly.blockBounds.extents) + 4) + " dist away");
                    //if (lastCloseAlly == null)
                    //    DebugTAC_AI.Log(KickStart.ModID + ": ALLY IS NULL");
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name);
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
            //IsLikelyJammed = false;
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            // P08 G.4 sibling-of-B-NEW8-5: mirror the WasRetreatingInCombat early-return so all
            // four AvoidAssist* variants (Path / Precise / Prediction / AirSpacing) share the
            // author's intent: suppress sideways re-targeting during active combat retreat.
            if (WasRetreatingInCombat)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistPrediction IS NaN!!");
                //TankAIManager.FetchAllAllies();
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
                if (SecondAvoidence && AlliesAlt.Count > 1)// MORE processing power
                {
                    lastCloseAlly = AIEPathing.SecondClosestAlly(AlliesAlt, posOffset, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name + " and " + lastCloseAlly2.name);
                            //IsLikelyJammed = true;
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name);
                            //IsLikelyJammed = true;
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
                    //DebugTAC_AI.Log(KickStart.ModID + ": Ally is " + lastAllyDist + " dist away");
                    //DebugTAC_AI.Log(KickStart.ModID + ": Trigger threshold is " + (lastTechExtents + Extremes(lastCloseAlly.blockBounds.extents) + 4) + " dist away");
                    //if (lastCloseAlly == null)
                    //    DebugTAC_AI.Log(KickStart.ModID + ": ALLY IS NULL");
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ": Spacing from " + lastCloseAlly.name);
                        //IsLikelyJammed = true;
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
            // P08 G.4 sibling-of-B-NEW8-5: WasRetreatingInCombat parity (see AvoidAssist).
            if (WasRetreatingInCombat)
                return targetIn;
            try
            {
                Tank lastCloseAlly;
                float lastAllyDist;
                // P08 G.4 sibling-of-B-NEW4-7: was `DodgeSphereCenter / Responsiveness` —
                // DodgeSphereCenter is an absolute world position; dividing by Responsiveness
                // (often << 1 for sluggish planes) scales it toward origin and corrupts moveSpace.
                Vector3 DSO = DodgeSphereCenter;
                float moveSpace = (DSO - tank.boundsCentreWorldNoCheck).magnitude;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                if (SecondAvoidence && AlliesAlt.Count > 1)// MORE processing power
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
                    // DebugTAC_AI.Log(KickStart.ModID + ": ALLY IS NULL");
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
                //TankAIManager.FetchAllAllies();
            }
            return targetIn;
        }
        
        private void UpdatePhysicsInfo()
        {
            if (tank.rbody.IsNotNull())
            {
                var velo = tank.rbody.velocity;
                if (!velo.IsNaN() && !float.IsInfinity(velo.x)
                    && !float.IsInfinity(velo.z) && !float.IsInfinity(velo.y))
                {
                    DodgeSphereCenter = tank.boundsCentreWorldNoCheck + velo.Clamp(lowMaxBoundsVelo, highMaxBoundsVelo);
                    DodgeSphereRadius = lastTechExtents + Mathf.Clamp(recentSpeed / 2f, 1f, 63f); // Strict
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
                if ((bool)tank.rbody)   // So that drifting is minimized
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
            if ((bool)tank.rbody)   // So that drifting is minimized
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
                if ((bool)tank.rbody)   // So that drifting is minimized
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
            if ((bool)tank.rbody)   // So that drifting is minimized
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
            lastOperatorRange = 96; //arbitrary
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
        public void TryHandleObstruction(bool hasMessaged, float dist, bool useRush, bool useGun, ref EControlOperatorSet direct)
        {
            //Something is in the way - try fetch the scenery to shoot at
            //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Obstructed");
            if (!hasMessaged)
            {
                //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Can't move there - something's in the way!");
            }

            ControlCore.FlagBusyUnstucking();
            // P09 B-9-1: was unconditionally clearing IsTryingToUnjam, then re-setting it at the
            // FM > 120 thresholds. Created a false→true round-trip every tick within the beam
            // window, leaving a latent torn-read hazard for any consumer of IsTechMovingAbs/Signed/
            // Actual that runs between the clear and re-set. Gate on FM <= UnjamUpdateStart so the
            // flag stays latched throughout the beam window (120-260) and clears naturally when
            // SettleDown or soft-decay brings FM back below threshold.
            if (FrustrationMeter <= AIGlobals.UnjamUpdateStart)
                IsTryingToUnjam = false;
            // P09 B-3-2: parked techs (DriveDir == Stop) aren't trying to move - don't accumulate
            // frustration or escalate to beam-fire. Neutral is NOT included here because
            // BGeneral.ResetValues sets Neutral by default, so the vast majority of unjam calls
            // arrive with Neutral and rely on the legacy "treat as Forwards" behavior.
            if (direct.DriveDir == EDriveFacing.Stop)
                return;
            // Soft decay: if there's any real motion (forward creep or genuine rotation), bleed the
            // meter so accumulated brief twitches across a long maneuver don't snowball into a beam
            // trigger. Read rbody.velocity directly rather than recentSpeed — recentSpeed is floored
            // at 1f (see UpdateAIControl), so a > 0.5f check on it would always pass and the decay
            // would over-trigger.
            if (FrustrationMeter > 0 && (bool)tank.rbody &&
                (tank.rbody.velocity.sqrMagnitude > 0.25f
                 || Mathf.Abs(tank.rbody.angularVelocity.y) > AIGlobals.AngularProgressThreshold))
            {
                FrustrationMeter = Mathf.Max(0, FrustrationMeter - Mathf.Max(1, KickStart.AIClockPeriod / 2));
            }
            ThrottleState = AIThrottleState.FullSpeed;
            if (direct.DriveDir == EDriveFacing.Backwards)
            {   // we are likely driving backwards
                ThrottleState = AIThrottleState.ForceSpeed;
                DriveVar = -1;

                if (Urgency >= 0)
                    Urgency += KickStart.AIClockPeriod / 5f;
                if (UrgencyOverload > AIGlobals.UrgencyOverloadReconsideration)
                {
                    //Are we just randomly angry for too long? let's fix that
                    AIECore.AIMessage(tech: tank, ref hasMessaged, tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    EstTopSped = 1;
                    AvoidStuff = true;
                    UrgencyOverload = 0;
                }
                else if (useRush && dist > MaxObjectiveRange * 2)
                {
                    //SCREW IT - GO FULL SPEED WE ARE TOO FAR BEHIND!
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
                    {
                        SettleDown();
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
                        ForceSetBeam = true;
                    }
                }
                else if (AIGlobals.UnjamUpdateFire < FrustrationMeter)
                {   //Shoot the freaking tree
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = -0.5f;
                }
                else
                {   // Gun the throttle
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = -1f;
                }
            }
            else
            {   // we are likely driving forwards
                ThrottleState = AIThrottleState.ForceSpeed;
                DriveVar = 1;

                if (Urgency >= 0)
                    Urgency += KickStart.AIClockPeriod / 5f;
                if (UrgencyOverload > AIGlobals.UrgencyOverloadReconsideration)
                {
                    //Are we just randomly angry for too long? let's fix that
                    AIECore.AIMessage(tech: tank, ref hasMessaged, tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    EstTopSped = 1;
                    AvoidStuff = true;
                    UrgencyOverload = 0;
                }
                else if (useRush && dist > MaxObjectiveRange * 2)
                {
                    //SCREW IT - GO FULL SPEED WE ARE TOO FAR BEHIND!
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
                    {
                        SettleDown();
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
                        ForceSetBeam = true;
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
                {   // Gun the throttle
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
            float bestValue = 250000; // 500
            int steps = ObstList.Count;
            if (steps <= 0)
            {
                //DebugTAC_AI.Log(KickStart.ModID + ": GetObstruction - DID NOT HIT ANYTHING");
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
            //DebugTAC_AI.Log(KickStart.ModID + ": GetObstruction - found " + ObstList.ElementAt(bestStep).name);
            return ObstList.ElementAt(bestStep).trans;
        }
        public void RemoveObstruction(float searchRad = 12)
        {
            // P09 B-4-5: re-acquire on Unity-destroyed OR out of 1.5x searchRad (tech moved past).
            // Previously only re-acquired on bare-null, so we kept firing at the original
            // obstacle's position long after rotating/moving past it.
            float staleRadSqr = (searchRad * 1.5f) * (searchRad * 1.5f);
            bool outOfRange = Obst != null
                && (Obst.position - tank.boundsCentreWorldNoCheck).sqrMagnitude > staleRadSqr;
            if (Obst == null || outOfRange)
            {
                Obst = GetObstruction(searchRad);
                Urgency += KickStart.AIClockPeriod / 5f;
            }
            // P09 B-9-4: do NOT set FIRE_ALL. FIRE_ALL drives tank.control.FireControl=true which
            // fires EVERY armed weapon at its independent aim (turrets in resting LOS, missiles at
            // last enemy lock) with no friendly-fire gate - allies in turret LOS get shot. The
            // Obsticle WeaponState path already drives weapon fire safely via AimAndFireWeapons +
            // sceneryBitMask-gated GetObstruction (scenery-only). FIRE_ALL was redundant + dangerous.
        }
        public void SettleDown(bool stopCore = true)
        {
            UrgencyOverload = 0;
            Urgency = 0;
            FrustrationMeter = 0;
            Obst = null;
            IsTryingToUnjam = false;
            // P08 B-NEW10-3: clear residual unjam mutations from ControlCore so the Maintainer
            // doesn't continue driving with FlagBusyUnstucking / DriveAwayFacingTowards state after
            // the unjam flag clears. Next Director slot will repopulate ControlCore with fresh intent.
            //
            // Close-range twitch fix: that hard-stop is only correct when EXITING an unjam (or on
            // recycle - hence the default stays true). Combat-hold callers (the RWheeled buckets)
            // call SettleDown every Operations tick and then immediately set their own drive intent
            // on the operator; stomping ControlCore to Stop here injected an idle frame that the
            // every-frame Maintainer held until the next (slow) Director rebuild, producing the
            // visible movement<->idle flap. Those callers pass stopCore:false.
            if (stopCore)
                SetCoreControlStop();
            ForceSetBeam = false;
            // P09 B-4-4: guard against ModulePatches.UpdateAim_Prefix NRE on the freshly-nulled Obst.
            // Only reset WeaponState if it's actually pointing at Obsticle - don't clobber active
            // Enemy combat that happened to call SettleDown for unrelated reasons. WeaponDirector
            // re-asserts WeaponState every tick anyway, so this is a one-tick guard for the bad window.
            if (WeaponState == AIWeaponState.Obsticle)
                WeaponState = AIWeaponState.Normal;
            // P09 B-5-1: zero the beam timeout so the beam doesn't keep running for up to 40 more
            // ticks after the FSM reset. Beam pipeline re-arms next tick if FSM still wants it.
            BeamTimeoutClock = 0;
            // P09 B-5-2: clear FIRE_ALL. BGeneral.ResetValues only runs from active Operations -
            // a tech with no live op (or one that just finished) would keep firing post-unjam.
            FIRE_ALL = false;
        }

        // ----------------------------  General Targeting  ----------------------------
        internal void AimAndFireWeapons(Vector3 aimWorld, float aimRadius)
        {
            // P12 BUG-6: small techs auto-fire here without consulting FIRE_ALL (intended convenience for
            // tiny scouts/melee). But the Obsticle case passes aimRadius 3f, so small techs would spray
            // obstructions - including invulnerable scenery - every tick. Exclude the Obsticle aim state;
            // obstacle fire stays gated on FIRE_ALL like every other state.
            if (maxBlockCount < AIGlobals.SmolTechBlockThreshold && aimRadius > 0f
                && ActiveAimState != AIWeaponState.Obsticle)
                FireAllWeapons();
            tank.control.TargetPositionWorld = aimWorld;
            tank.control.TargetRadiusWorld = aimRadius;
        }
        internal void FireAllWeapons() => tank.control.FireControl = true;
        internal void MaxBoost() => tank.control.BoostControlJets = true;
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

        internal void SuppressFiring(bool Disable)
        {
            try
            {
                // P12 BUG-9: compare against the ACTUAL component state, not a cached flag. If anything
                // external ever flips tank.Weapons.enabled out from under us, the next call self-heals
                // instead of latching the stale value forever. Still only logs / churns the Unity enable
                // lifecycle on a real change (enabled == Disable means the two are out of sync).
                if (tank.Weapons.enabled == Disable)
                {
                    DebugTAC_AI.Info(KickStart.ModID + ": AI " + tank.name + " of Team " + tank.Team + ":  Disabled weapons: " + Disable);
                    tank.Weapons.enabled = !Disable;
                }
                // Per-tick gate — must run every call during suppression, not just on edge.
                if (Disable)
                    tank.control.FireControl = false;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("SuppressFiring() Critical error", e);
            }
        }
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
                            // B10: reuse magnitude (vec / d) instead of .normalized (recomputes sqrt).
                            // The 0.05f guard skips overlapping techs — Physics.Raycast with a zero-
                            // direction vector is undefined; two coincident centres have nothing
                            // between them, so leaving wasBlockedThisCheck=false is correct.
                            Vector3 dir = vec / targetDistance;
                            if (Physics.Raycast(pos, dir, out RaycastHit hit,
                                Mathf.Min(targetDistance, MaxCombatRange), TargetMask, QueryTriggerInteraction.Ignore)
                                && hit.distance < targetDistance)
                            {
                                wasBlockedThisCheck = true;
                            }
                        }
                        // B4+T1: validation-side hysteresis. A held target is only released past
                        // MaxCombatRange * CombatRangeRetentionMult. Acquisition (FindEnemy /
                        // FindEnemyAir) uses the same multiplier (squared form) so the keep/drop
                        // boundary is symmetric. P05 made the universal per-tick TryRefreshEnemyEnemy
                        // dispatch via RGeneral.DispatchNoTargetIdle, so this hysteresis is the only
                        // thing standing between every range-edge wobble and the LollyGag flicker.
                        // B6: two-tier — even PreserveEnemyTarget can't exceed RTSLockMaxRangeMultiplier
                        // (hard cap) to prevent cross-map perma-chase in multiplayer RTS.
                        float hardCap = MaxCombatRange * AIGlobals.RTSLockMaxRangeMultiplier;
                        if (targetDistance > hardCap
                            || (targetDistance > MaxCombatRange * AIGlobals.CombatRangeRetentionMult && !PreserveEnemyTarget))
                        {
                            DebugTAC_AI.LogTargeting(tank, "Target released CheckEnemyAndAiming: out of range");
                            ReleaseTarget();
                        }
                    }
                }
                // Apply hysteresis: only flip BlockedLineOfSight on 2 consecutive blocked checks,
                // clear on any unblocked one. Stops MoveSideways<->stand-and-shoot half-second flicker
                // from an ally or terrain bump briefly cutting LOS.
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
        public Visible TryRefreshEnemyAllied()
        {
            if ((bool)lastPlayer)
            {
                Tank playerTank = lastPlayer.tank;
                Visible playerTarget = playerTank.Weapons.GetManualTarget();
                // B8: honor the player's manual target as an "attack request" only when
                // (a) target is valid and not friendly/teammate, (b) the player is actively
                // firing (FireControl) — passive radar lock is NOT an attack request, and
                // (c) relations are mutable. Then DegradeRelations promotes neutral→enemy
                // so the IsEnemy purge in CheckEnemyAndAiming won't undo us next tick.
                // Mirrors the GUINPTInteraction "Annoy the Team" diplomatic event.
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
                        // Commit the attack request: degrade relations so subsequent
                        // IsEnemy checks pass. Damage=DamageAngerDropRelations forces
                        // the drop past the angerThreshold accumulator (consistent with
                        // GUINPTInteraction.cs:411 "Annoy the Team" semantics).
                        ManBaseTeams.DegradeRelations(tank.Team, targTeam, AIGlobals.DamageAngerDropRelations);
                    }
                    if (ManBaseTeams.IsEnemy(tank.Team, targTeam))
                    {
                        // B8: was `Provoked = 0; EndPursuit(); lastEnemy = playerTarget;` —
                        // caused UpdateTargetCombatFocus to instantly EndPursuit on the next
                        // tick AND (post-B1) left KeepEnemyFocus=false so the range purge
                        // would drop the acquired target immediately. Mirror B13's MimicDefend
                        // pattern: SetPursuit(force:true) acquires WITH the lock so the next
                        // purge tick honors PreserveEnemyTarget.
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
        private float _losLostGraceTimer = 0f;   // B7: real-time seconds since LOS lost on a held in-range target
        private void UpdateTargetCombatFocus()
        {
            if (Provoked > 0)
            {
                Provoked -= KickStart.AIClockPeriod;
                _losLostGraceTimer = 0f;       // actively combatting — grace doesn't accrue
                return;
            }
            // D5: clamp Provoked to 0. Looks redundant (we're inside `Provoked <= 0`)
            // but `RRepair.cs:159,182` use strict `Provoked == 0` checks. Decay can
            // overshoot to a small negative (e.g. `ProvokeTimeShort - AIClockPeriod`
            // under networked play with AIClockPeriod=30); without this clamp,
            // RRepair would route into the combat-delay branch forever since decay
            // never re-runs once we're in this `<=0` arm.
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
            // B7: in-range. If a direct-fire shooter has lost sight, hold pursuit for
            // LOSLostGraceTime before dropping — covers brief occlusion behind cover.
            // Indirect-fire (artillery, NeedsLineOfSight==false) ignores LOS gating
            // and behaves as before (pursuit held purely on range).
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
        /// <summary>
        /// T2: Evaluates whether this allied tech should disengage; returns the Retreat verdict.
        /// Does NOT perform target acquisition — targeting is handled separately by
        /// CheckEnemyAndAiming. Decision is based on team retreat list, distance from
        /// base/player, and DamageThreshold.
        /// </summary>
        // B5: returns the retreat decision; callers assign Retreat once.
        // Removes the fragile structural-coupling-to-return that prevented double-eval
        // — repeating the call now just re-assigns the same value rather than compounding.
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
            //bool DoNotEngage = false;
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
        private float lastTargetGatherRangeSqr = 0;
        private List<Tank> targetCache = new List<Tank>();
        // T5: force a fresh scan next call. Called from OnHit so damage events can't
        // be hidden behind a stale-cache window.
        internal void InvalidateTargetCache() { lastTargetGatherTime = 0; }
        private List<Tank> GatherTargetTechsInRange(float gatherRangeSqr)
        {
            // T5: also re-scan if the requested radius grew — a cache populated with a
            // smaller radius would miss new entrants the larger query expects.
            if (lastTargetGatherTime > Time.time && gatherRangeSqr <= lastTargetGatherRangeSqr)
            {
                return targetCache;
            }
            // T5: shorter cache in combat (Provoked or actively pursuing a target).
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
                    // B6 two-tier: hard cap always fires; soft cap respects PreserveEnemyTarget.
                    float sqr = (target.tank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                    bool pastHardCap = sqr > TargetRangeSqr * AIGlobals.RTSLockMaxRangeMultiplierSqr;
                    bool pastSoftCap = sqr > TargetRangeSqr * AIGlobals.CombatRangeRetentionMultSqr;
                    if (pastHardCap || (pastSoftCap && !PreserveEnemyTarget))
                    {
                        DebugTAC_AI.LogTargeting(tank, "Target released FindEnemy: out of range");
                        target = null;
                        EndPursuit();
                    }
                    else if (PreserveEnemyTarget || NextFindTargetTime <= Time.time) // Carry on chasing the target
                    {
                        return target;
                    }
                }
            }

            if (AttackMode == EAttackMode.Random)
            {
                // D1: filter-then-pick. Original code iterated a random-length prefix of
                // techs[] and overwrote `target` every step without break — so the result
                // was the LAST in-range valid enemy in a random prefix (deterministic given
                // list order, biased toward the tail). Pesterer intent (per comment on
                // PestererSwitchDelay) is a uniformly-random enemy in range.
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
        private Visible ScanAirborneRandom(List<Tank> techs, int launchCount, bool preferAirborne, ref float TargetRangeSqr, Vector3 scanCenter)
        {
            // D1: filter-then-pick. Original code tightened TargetRangeSqr=dist on every
            // valid candidate — that turned this "Random" helper into a smallest-distance
            // picker. Random-mode (Pesterer) intent is a uniformly-random in-range enemy
            // matching the airborne preference; ref TargetRangeSqr left unmodified so the
            // ground-preference fallback (?? chain in FindEnemyAir) still sees the original cap.
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
        private Visible FindEnemyAir(bool InvertBullyPriority)
        {
            Visible target = lastEnemyGet;

            float TargetRangeSqr = MaxCombatRange * MaxCombatRange;
            // B9: aircraft scan from the velocity-led DodgeSphereCenter — consistent across
            // Random/Strong/Closest branches AND the staleness check below. Was previously
            // set in Random branch only; Strong inherited the bounds-centre and large/fast
            // aircraft made sub-optimal Strong picks. DodgeSphereCenter falls back to
            // boundsCentreWorldNoCheck when rbody is null (UpdatePhysicsInfo), so this is
            // strictly a superset of the old behaviour.
            Vector3 scanCenter = DodgeSphereCenter;

            if (target != null)
            {
                if (!target.isActive || !ManBaseTeams.IsEnemy(tank.Team, target.tank.Team))
                {
                    target = null;
                }
                else
                {
                    // B6 two-tier: hard cap always fires; soft cap respects PreserveEnemyTarget.
                    float sqr = (target.tank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                    bool pastHardCap = sqr > TargetRangeSqr * AIGlobals.RTSLockMaxRangeMultiplierSqr;
                    bool pastSoftCap = sqr > TargetRangeSqr * AIGlobals.CombatRangeRetentionMultSqr;
                    if (pastHardCap || (pastSoftCap && !PreserveEnemyTarget))
                    {
                        DebugTAC_AI.LogTargeting(tank, "Target released FindEnemy: out of range");
                        target = null;
                        EndPursuit();
                    }
                    else if (PreserveEnemyTarget || NextFindTargetTime <= Time.time) // Carry on chasing the target
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
                    // Prefer ground targets (bases). Fall back to airborne if none.
                    target = ScanAirborneStrong(techs, launchCount, preferAirborne: false, invertBully: true, ref TargetRangeSqr, scanCenter)
                          ?? ScanAirborneStrong(techs, launchCount, preferAirborne: true, invertBully: true, ref TargetRangeSqr, scanCenter);
                }
                else
                {
                    // Prefer airborne targets (smallest in scan). Fall back to ground if none.
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
            // P08 B-NEW8-4: guard against destroyed-mid-frame Visible (lastEnemyGet.tank can
            // transition to a Unity-destroyed reference between target selection and consumption).
            if (targetTank.IsNull() || targetTank.tank.IsNull())
                return tank.boundsCentreWorldNoCheck;
            if (AdvancedAI)   // Rough Target leading
            {
                return RoughPredictTarget(targetTank.tank);
            }
            else
                return targetTank.tank.boundsCentreWorldNoCheck;
        }
        private const float MaxBoundsVelo = 350;
        private static Vector3 lowMaxBoundsVelo = -new Vector3(MaxBoundsVelo, MaxBoundsVelo, MaxBoundsVelo);
        private static Vector3 highMaxBoundsVelo = new Vector3(MaxBoundsVelo, MaxBoundsVelo, MaxBoundsVelo);
        public Vector3 RoughPredictTarget(Tank targetTank)
        {
            // P08 G.5 sibling-of-B-NEW8-4: same destroyed-mid-frame Tank exposure as
            // InterceptTargetDriving. Guard upstream so every caller (RCore.cs:755 +
            // AirplaneAICore.cs:905-1038 fan-out) gets the protection.
            if (targetTank.IsNull())
                return tank.boundsCentreWorldNoCheck;
            if (DriverType != AIDriverType.Stationary && targetTank.rbody.IsNotNull())
            {
                var velo = targetTank.rbody.velocity;
                if (!velo.IsNaN() && !float.IsInfinity(velo.x)
                    && !float.IsInfinity(velo.z) && !float.IsInfinity(velo.y)
                    && lastCombatRange < float.MaxValue)  // skip the IgnoreEnemyDistance() sentinel
                {
                    return targetTank.boundsCentreWorldNoCheck + (velo.Clamp(lowMaxBoundsVelo, highMaxBoundsVelo) *
                        (lastCombatRange * AIGlobals.TargetVelocityLeadPredictionMulti));
                }
            }
            return targetTank.boundsCentreWorldNoCheck;
        }

        // ----------------------------  Lock-On Targeting  ---------------------------- 
        private void ManageAILockOn()
        {
            switch (ActiveAimState)
            {
                case AIWeaponState.Enemy:
                    if (lastEnemyGet.IsNotNull())
                    {   // Allow the enemy AI to finely select targets
                        // T5: site-level guard to avoid Vector3.ToString() alloc when flag off.
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
                    // Rare-but-live: ActiveAimState is only set to Mimic in AIEWeapons.WeaponMaintainer's
                    // IsMultiTech branch (when a multi-tech follower's ally has a target). Solo techs never
                    // reach this case - they resolve to Enemy/Obsticle/Normal instead. Mirror the ally's
                    // lock-on target so the follower's reticule tracks what the ally is shooting at.
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
                {   // Cannot do as camera breaks
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

        // ----------------------------  Chase Handling  ----------------------------
        public int Provoked = 0;           // Were we hit from afar?
        public bool KeepEnemyFocus { get; private set; } = false;     // Chasing specified target?

        // B2/T2: per-attacker rolling damage. AccumulateAndCheckThreat is called from
        // OnHit when the single-shot DamageAlertThreshold is NOT met — sustained low-DPS
        // fire still trips the alert via the cumulative threshold.
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
                _damageBuckets.Remove(id);   // consume on trip; re-arm window for next salvo
                return true;
            }
            _damageBuckets[id] = new DamageBucket { Accumulator = acc, LastUpdateTime = now };
            return false;
        }
        internal void ResetDamageAccumulator() => _damageBuckets.Clear();
        /// <summary>
        /// T3: enum result for SetPursuit. Callers that only care about "did we acquire?"
        /// can use the bool shims; callers that need to distinguish "blocked by a held
        /// lock" (B1) from "target was invalid" (B15 stuck-target debug) can switch on this.
        /// </summary>
        public enum SetPursuitResult : byte
        {
            Set            = 0,  // newly assigned (lastEnemy changed, KeepEnemyFocus=true)
            AlreadyTarget  = 1,  // target == lastEnemy, dest refreshed and lock self-healed
            BlockedByLock  = 2,  // KeepEnemyFocus held a different target and force=false
            NullTarget     = 3,  // target was null
            InvalidTank    = 4,  // target non-null but target.tank was null/destroyed
        }
        public bool SetPursuit(Visible target) => IsSetPursuitSuccess(TrySetPursuit(target, false));
        public bool SetPursuit(Visible target, bool force) => IsSetPursuitSuccess(TrySetPursuit(target, force));
        /// <summary>T3: maps SetPursuitResult to the bool the legacy callers expect.</summary>
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
            // B1: same-target re-call refreshes destination and self-heals the lock.
            // The per-tick TryRefreshEnemyEnemy in every R* handler depends on this to
            // keep ControlOperator.lastDestination glued to a moving enemy and to
            // restore KeepEnemyFocus if it was cleared by the setter invariant.
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
            MarkOperatorDirty();  // T3: keep age tracking honest on in-place mutation
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
        /// <summary>
        /// B14/B15: canonical full target release - nulls lastEnemy AND clears KeepEnemyFocus
        /// atomically. Use for alignment switches, AI re-init, validation purge, monitor
        /// out-of-radius. Do NOT use inside candidate-scanning loops that only reject a
        /// LOCAL `target` variable - use EndPursuit() alone there (see FindEnemy 1.21x purge
        /// and FindEnemyAir 1.21x purge - they release a local, not the committed lastEnemy).
        /// </summary>
        public void ReleaseTarget()
        {
            lastEnemy = null;   // setter auto-clears KeepEnemyFocus via the B1 invariant
        }
        public bool InRangeOfTarget(float distance)
        {
            return InRangeOfTarget(lastEnemyGet, distance);
        }
        public bool InRangeOfTarget(Visible target, float distance)
        {
            return (target.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).sqrMagnitude <= distance * distance;
        }

        // ----------------------------  Anchor Management  ---------------------------- 

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
            //DebugTAC_AI.Assert("AdjustAnchors()");
            DoUnAnchor();
            if (!tank.IsAnchored)
            {
                AnchorIgnoreChecks(prevAnchored);
            }
        }

        public void Unanchor()
        {
            //DebugTAC_AI.Assert("Unanchor()");
            //DoUnAnchor();
            AnchorState = AIAnchorState.Unanchor;
            AnchorStateAIInsure = true;
        }

        public void AnchorStatic()
        {
            //DebugTAC_AI.Assert("AnchorStatic()");
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
                //DebugTAC_AI.LogDevOnly(KickStart.ModID + ": AI " + tank.name + ":  TryReallyAnchor(" + forced + ")");
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
                //tank.FixupAnchors(true, true); // Breaks everything
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
                //Quaternion tankStartRot = tank.trans.rotation;
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
                if (!worked)
                    anchorAttempts = 0;
                AnchorState = AIAnchorState.None;
            }
        }
        private void DoUnAnchor()
        {
            //DebugTAC_AI.Log("DoUnAnchor()");
            if (tank.IsAnchored || tank.Anchors.NumIsAnchored > 0)
            {
                //DebugTAC_AI.Log("DoUnAnchor() - activated");
                tank.Anchors.UnanchorAll(true);
                if (!tank.IsAnchored && AIAlign == AIAlignment.Player)
                {
                    //DebugTAC_AI.Log("DoUnAnchor() - success");
                    WakeAIForChange();
                }
            }
            AnchorState = AIAnchorState.None;
            anchorAttempts = 0;
        }

        // ----------------------------  Logistics  ---------------------------- 
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
                {   //clip it into the Tech to send to inventory 
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
                //DebugTAC_AI.Assert(true, KickStart.ModID + ": Tech " + tank.name + " called HoldBlock in networked environment. This is not supported!");if (TB.block)
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
                    // if ((heldBlock.visible.centrePosition - tank.boundsCentreWorldNoCheck).magnitude > 16)
                    //     heldBlock.visible.centrePosition = tank.boundsCentreWorldNoCheck + (Vector3.up * (lastTechExtents + 3));
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
                // P08 B-NEW10-3: clear residual unjam mutations from ControlCore (see SettleDown).
                SetCoreControlStop();
                CancelInvoke();
                //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  Allowing approach");
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

        // ----------------------------  Self-Repair  ---------------------------- 
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
                // Else we have NO ROOT and therefore no blocks(?!?),
                //   do nothing because putting in zero will immedeately break things
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
                {   // Combat repairs (combat mechanic)
                    //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " RepairCombat");
                    AIERepair.RepairStepper(this, tank, TechMemor, true, Combat: true);
                }
                else
                {   // Repairs in peacetime
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
                {   // Combat repairs (combat mechanic)
                    //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " RepairCombat");
                    AIERepair.RepairStepper(this, tank, TechMemor, AdvancedAI, Combat: true);
                }
                else
                {   // Repairs in peacetime
                    //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " Repair");
                    if (AdvancedAI) // faster for smrt
                        AIERepair.InstaRepair(tank, TechMemor, KickStart.AIClockPeriod);
                    else
                        AIERepair.RepairStepper(this, tank, TechMemor);
                }
            }
            UpdateDamageThreshold();
        }
        public void DelayedRepairUpdate()
        {   //OBSOLETE until further notice
            // Dynamic timescaled update that fires when needed, less for slow techs, fast for large techs
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

        // ----------------------------  Debug Collector  ---------------------------- 
        private void ShowCollisionAvoidenceDebugThisFrame()
        {
            if (AIGlobals.ShowDebugFeedBack && Input.GetKey(KeyCode.LeftShift))// && AIECore.debugVisuals)
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
