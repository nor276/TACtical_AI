using UnityEngine;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Engine
{
    /// <summary>
    /// The single IAIContext implementation, backed by a TankAIHelper (the bus + sinks) and, for enemies,
    /// an EnemyMind (the typed enemy facade). One instance per tech, reused across ticks. The live drive
    /// operator is owned here: the engine calls LoadOperator() before a behavior ticks, the behavior mutates
    /// it through the Op* verbs, and the engine calls CommitOperator() to push it back into the helper.
    /// Delegation preserves every computed getter / allied capability clamp for free (we just call the
    /// helper's existing members), so behavior is unchanged by the indirection.
    /// </summary>
    public sealed class AIContext : IAIContext
    {
        private readonly TankAIHelper helper;
        private EnemyMind mind;
        private EControlOperatorSet _operator = EControlOperatorSet.Default;

        public AIContext(TankAIHelper helper, EnemyMind mind = null)
        {
            this.helper = helper;
            this.mind = mind;
        }
        public TankAIHelper Helper => helper;
        public EnemyMind Mind => mind;
        public void UpdateMind(EnemyMind newMind) { mind = newMind; }

        // engine-facing operator round-trip (not on the behavior interface)
        public void LoadOperator() { _operator = helper.GetDirectedControl(); }
        public void CommitOperator() { helper.SetDirectedControl(_operator); }
        // by-ref access to the live operator, for behavior wrappers that delegate to the existing
        // (helper, tank, ref EControlOperatorSet) handlers. Engine/wrapper-facing, not on IAIContext.
        public ref EControlOperatorSet OperatorRef() => ref _operator;

        // ---- identity / references ----
        public Tank Tank => helper.tank;
        public string Name => helper.name;
        public bool IsEnemy => mind != null;
        public AIDriverType DriverType => helper.DriverType;
        public IMovementAIController MovementController => helper.MovementController;
        public AIAlignment TickAIAlign => helper.TickAIAlign;
        public AITreeType.AITypes LastAIType => helper.lastAIType;

        // ---- ranges + combat posture ----
        public float MinCombatRange => helper.MinCombatRange;
        public float MaxCombatRange => helper.MaxCombatRange;
        public float MaxObjectiveRange => helper.MaxObjectiveRange;
        public float LastCombatRange => helper.lastCombatRange;
        public float LastOperatorRange => helper.lastOperatorRange;
        public bool FullMelee => helper.FullMelee;
        public bool AdvancedAI => helper.AdvancedAI;
        public bool AllMT => helper.AllMT;
        public float DamageThreshold => helper.DamageThreshold;
        public bool CombatWantsCircleNow() => helper.CombatWantsCircleNow();

        public EAttackMode AttackMode { get => helper.AttackMode; set => helper.AttackMode = value; }
        public float TurretFraction { get => helper.TurretFraction; set => helper.TurretFraction = value; }
        public bool WasRetreatingInCombat { get => helper.WasRetreatingInCombat; set => helper.WasRetreatingInCombat = value; }
        public bool WantsToFight { get => helper.WantsToFight; set => helper.WantsToFight = value; }
        public bool ChaseThreat { get => helper.ChaseThreat; set => helper.ChaseThreat = value; }
        public bool Retreat { get => helper.Retreat; set => helper.Retreat = value; }
        public int Provoked { get => helper.Provoked; set => helper.Provoked = value; }
        public float Urgency { get => helper.Urgency; set => helper.Urgency = value; }
        public float UrgencyOverload { get => helper.UrgencyOverload; set => helper.UrgencyOverload = value; }
        public bool FIRE_ALL { get => helper.FIRE_ALL; set => helper.FIRE_ALL = value; }
        public bool FirePROPS { get => helper.FirePROPS; set => helper.FirePROPS = value; }
        public bool ForceSetBeam { get => helper.ForceSetBeam; set => helper.ForceSetBeam = value; }
        public int WeaponDelayClock { get => helper.WeaponDelayClock; set => helper.WeaponDelayClock = value; }

        // ---- physics ----
        public Vector3 SafeVelocity => helper.SafeVelocity;
        public Vector3 LocalSafeVelocity => helper.LocalSafeVelocity;
        public float RecentSpeed => helper.recentSpeed;
        public float RecentSpeedSigned => helper.recentSpeedSigned;
        public bool MakingNetProgress => helper.MakingNetProgress;
        public float LastTechExtents => helper.lastTechExtents;
        public float GroundOffsetHeight => helper.GroundOffsetHeight;
        public Vector3 DodgeSphereCenter => helper.DodgeSphereCenter;
        public float GetFrameHeight() => helper.GetFrameHeight();
        public bool IsTechMovingAbs(float minSpeed) => helper.IsTechMovingAbs(minSpeed);
        public bool IsOrbiting() => helper.IsOrbiting();
        public bool IsOrbiting(float minimumCloseInSpeedSqr) => helper.IsOrbiting(minimumCloseInSpeedSqr);
        public float EstTopSped { get => helper.EstTopSped; set => helper.EstTopSped = value; }

        // ---- targeting ----
        public Visible LastEnemyGet => helper.lastEnemyGet;
        public bool BlockedLineOfSight { get => helper.BlockedLineOfSight; set => helper.BlockedLineOfSight = value; }
        public bool CollectedTarget { get => helper.CollectedTarget; set => helper.CollectedTarget = value; }
        public Visible GetPlayerTech() => helper.GetPlayerTech();

        // ---- throttle / drive intent ----
        public AIThrottleState ThrottleState { get => helper.ThrottleState; set => helper.ThrottleState = value; }
        public float DriveVar { get => helper.DriveVar; set => helper.DriveVar = value; }
        public float AutoSpacing { get => helper.AutoSpacing; set => helper.AutoSpacing = value; }
        public bool FullBoost { get => helper.FullBoost; set => helper.FullBoost = value; }
        public bool LightBoost { get => helper.LightBoost; set => helper.LightBoost = value; }
        public bool Attempt3DNavi { get => helper.Attempt3DNavi; set => helper.Attempt3DNavi = value; }
        public bool AvoidStuff { get => helper.AvoidStuff; set => helper.AvoidStuff = value; }
        public bool SecondAvoidence { get => helper.SecondAvoidence; set => helper.SecondAvoidence = value; }
        public Vector3 Navi3DDirect { get => helper.Navi3DDirect; set => helper.Navi3DDirect = value; }
        public Vector3 Navi3DUp { get => helper.Navi3DUp; set => helper.Navi3DUp = value; }

        // ---- objective / role slots ----
        public Visible TheResource { get => helper.theResource; set => helper.theResource = value; }
        public Visible TheResourceNode { get => helper.theResourceNode; set => helper.theResourceNode = value; }
        public Visible TheGuardedAlly { get => helper.theGuardedAlly; set => helper.theGuardedAlly = value; }
        public Visible TheHostTech { get => helper.theHostTech; set => helper.theHostTech = value; }
        public Tank TheBase { get => helper.theBase; set => helper.theBase = value; }
        public Tank LastCloseAlly => helper.lastCloseAlly;
        public IAIFollowable LastBasePos { get => helper.lastBasePos; set => helper.lastBasePos = value; }
        public float LastBaseExtremes { get => helper.lastBaseExtremes; set => helper.lastBaseExtremes = value; }
        public Visible LastPlayer { get => helper.lastPlayer; set => helper.lastPlayer = value; }
        public bool FoundBase { get => helper.foundBase; set => helper.foundBase = value; }
        public bool FoundGoal { get => helper.foundGoal; set => helper.foundGoal = value; }
        public bool IsMultiTech { get => helper.IsMultiTech; set => helper.IsMultiTech = value; }
        public TankBlock HeldBlock => helper.HeldBlock;
        public Transform Obst { get => helper.Obst; set => helper.Obst = value; }

        // ---- multitech offsets ----
        public Vector3 MTOffsetPos { get => helper.MTOffsetPos; set => helper.MTOffsetPos = value; }
        public Vector3 MTOffsetRot { get => helper.MTOffsetRot; set => helper.MTOffsetRot = value; }
        public Vector3 MTOffsetRotUp { get => helper.MTOffsetRotUp; set => helper.MTOffsetRotUp = value; }
        public bool MTLockedToTechBeam { get => helper.MTLockedToTechBeam; set => helper.MTLockedToTechBeam = value; }
        public bool MTMimicHostAvail { get => helper.MTMimicHostAvail; set => helper.MTMimicHostAvail = value; }

        // ---- timers / clocks ----
        public int ActionPause { get => helper.ActionPause; set => helper.actionPause = value; }
        public int DelayedAnchorClock { get => helper.DelayedAnchorClock; set => helper.DelayedAnchorClock = value; }
        public int UnanchorCountdown { get => helper.unanchorCountdown; set => helper.unanchorCountdown = value; }
        public float NextFindTargetTime { get => helper.NextFindTargetTime; set => helper.NextFindTargetTime = value; }
        public bool PendingDamageCheck { get => helper.PendingDamageCheck; set => helper.PendingDamageCheck = value; }
        public float RepairStepperClock { get => helper.RepairStepperClock; set => helper.RepairStepperClock = value; }
        public int FrustrationMeter { get => helper.FrustrationMeter; set => helper.FrustrationMeter = value; }
        public AIRunState RunState { get => helper.RunState; set => helper.RunState = value; }
        public bool TechIsApproaching { get => helper.techIsApproaching; set => helper.techIsApproaching = value; }

        // ---- anchoring ----
        public bool AutoAnchor { get => helper.AutoAnchor; set => helper.AutoAnchor = value; }
        public bool IsAutoAnchored => helper.IsAutoAnchored;
        public bool CanAttemptAnchor => helper.CanAttemptAnchor;
        public bool CanAutoAnchor => helper.CanAutoAnchor;
        public bool PlayerAllowAutoAnchoring => helper.PlayerAllowAutoAnchoring;
        public void TryInsureAutoAnchor() => helper.TryInsureAutoAnchor();
        public void TryInsureManualAnchor() => helper.TryInsureManualAnchor();
        public void Unanchor() => helper.Unanchor();
        public void AnchorIgnoreChecks(bool forced = false) => helper.AnchorIgnoreChecks(forced);

        // ---- navigation / destination / RTS ----
        public EDriveDest DriveDestDirected => helper.DriveDestDirected;
        public bool IsDirectedMovingToDest => helper.IsDirectedMovingToDest;
        public bool IsDirectedMovingFromDest => helper.IsDirectedMovingFromDest;
        public bool AdviseAwayCore => helper.AdviseAwayCore;
        public bool DoSteerCore => helper.DoSteerCore;
        public Vector3 LastDestinationCore => helper.lastDestinationCore;
        public Vector3 LastDestinationOp => helper.lastDestinationOp;
        public bool RTSControlled { get => helper.RTSControlled; set => helper.RTSControlled = value; }
        public bool IsGoingToPositionalRTSDest => helper.IsGoingToPositionalRTSDest;

        // ---- AISetSettings struct field setters ----
        public bool SideToThreat => helper.SideToThreat;
        public void SetCombatSpacing(float v) => helper.AISetSettings.CombatSpacing = v;
        public void SetCombatChase(float v) => helper.AISetSettings.CombatChase = v;
        public void SetObjectiveRange(float v) => helper.AISetSettings.ObjectiveRange = v;
        public void SetScanRange(float v) => helper.AISetSettings.ScanRange = v;
        public void SetAdvancedAI(bool v) => helper.AISetSettings.AdvancedAI = v;
        public void SetAllMT(bool v) => helper.AISetSettings.AllMT = v;
        public void SetFullMelee(bool v) => helper.AISetSettings.FullMelee = v;
        public void SetSideToThreat(bool v) => helper.AISetSettings.SideToThreat = v;
        public void SetAutoRepair(bool v) => helper.AISetSettings.AutoRepair = v;
        public void SetUseInventory(bool v) => helper.AISetSettings.UseInventory = v;

        // ---- EnemyMind facade ----
        public bool MindLikelyMelee => mind != null && mind.LikelyMelee;
        public bool MindAttackPlayer => mind != null && mind.AttackPlayer;
        public bool MindCanCallRetreat => mind != null && mind.CanCallRetreat;
        public bool MindCanDoRetreat => mind != null && mind.CanDoRetreat;
        public bool MindInvertBullyPriority => mind != null && mind.InvertBullyPriority;
        public Vector3 MindSceneStationaryPos => mind != null ? mind.sceneStationaryPos : Vector3.zero;
        public bool MindSolarsAvail => mind != null && mind.SolarsAvail;
        public void SetCommanderAttack(EAttackMode mode)
        {
            if (mind != null) mind.CommanderAttack = mode;
            else helper.AttackMode = mode;
        }

        // ---- target verbs ----
        public bool SetPursuit(Visible target) => helper.SetPursuit(target);
        public bool SetPursuit(Visible target, bool force) => helper.SetPursuit(target, force);
        public void EndPursuit() => helper.EndPursuit();
        public void ReleaseTarget() => helper.ReleaseTarget();
        public Visible TryRefreshEnemyAllied() => helper.TryRefreshEnemyAllied();
        public Visible TryRefreshEnemyEnemy(bool invertBullyPriority) => helper.TryRefreshEnemyEnemy(invertBullyPriority);

        // ---- owned operator verbs ----
        public void OpStop() => _operator.STOP(helper);
        public void OpForwards() => _operator.Forwards(helper);
        public void OpReverse() => _operator.Reverse(helper);
        public void OpFaceDest() => _operator.FaceDest();
        public void OpDriveToFacingTowards() => _operator.DriveToFacingTowards();
        public void OpDriveToFacingBackwards() => _operator.DriveToFacingBackwards();
        public void OpDriveAwayFacingTowards() => _operator.DriveAwayFacingTowards();
        public void OpDriveAwayFacingAway() => _operator.DriveAwayFacingAway();
        public void OpDriveToFacingPerp() => _operator.DriveToFacingPerp();
        public void OpDriveAwayFacingPerp() => _operator.DriveAwayFacingPerp();
        public void OpResetActions() => _operator.ResetActions();
        public void OpSetLastDest(Vector3 posScene) => _operator.SetLastDest(posScene);
        public void MarkOperatorDirty() => helper.MarkOperatorDirty();

        // ---- engine-service entry points (operate on the owned operator) ----
        public void TryHandleObstruction(bool hasMessaged, float dist, bool useRush, bool useGun)
        {
            helper.TryHandleObstruction(hasMessaged, dist, useRush, useGun, ref _operator);
            helper.MarkOperatorDirty();
        }
        public bool AutoHandleObstruction(float dist = 0, bool useRush = false, bool useGun = true, float div = 4)
        {
            bool r = helper.AutoHandleObstruction(ref _operator, dist, useRush, useGun, div);
            helper.MarkOperatorDirty();
            return r;
        }
        public void RemoveObstruction(float searchRad = 12) => helper.RemoveObstruction(searchRad);
        public void SettleDown(bool stopCore = true) => helper.SettleDown(stopCore);
        public float GetDistanceFromTask(Vector3 taskLocation, float additionalSpacing = 0) => helper.GetDistanceFromTask(taskLocation, additionalSpacing);
        public float GetDistanceFromTask2D(Vector3 taskLocation, float additionalSpacing = 0) => helper.GetDistanceFromTask2D(taskLocation, additionalSpacing);
        public void SetDistanceFromTaskUnneeded() => helper.SetDistanceFromTaskUnneeded();
        public bool CanStoreEnergy() => helper.CanStoreEnergy();
        public float GetEnergyPercent() => helper.GetEnergyPercent();
        public void DropAllItemsInCollectors() => helper.DropAllItemsInCollectors();
        public bool HoldBlock(Visible tb) => helper.HoldBlock(tb);
        public void DropBlock() => helper.DropBlock();
        public void DropBlock(Vector3 throwDirection) => helper.DropBlock(throwDirection);
        public void MaxBoost() => helper.MaxBoost();
        public void MaxProps() => helper.MaxProps();
        public Vector3 InterceptTargetDriving(Visible targetTank) => helper.InterceptTargetDriving(targetTank);
        public Vector3 RoughPredictTarget(Tank targetTank) => helper.RoughPredictTarget(targetTank);
        public float IgnoreEnemyDistance() => helper.IgnoreEnemyDistance();
        public float UpdateEnemyDistance(Vector3 enemyPosition) => helper.UpdateEnemyDistance(enemyPosition);
        public void UpdateDamageThreshold() => helper.UpdateDamageThreshold();
        public void UpdateVanillaAvoidence() => helper.UpdateVanillaAvoidence();
        public void InsureTechMemor(string context, bool doFirstSave) => helper.InsureTechMemor(context, doFirstSave);
        public Vector3 AvoidAssist(Vector3 targetIn, bool avoidStatic = true) => helper.AvoidAssist(targetIn, avoidStatic);
        public Vector3 AvoidAssistPrecise(Vector3 targetIn, bool avoidStatic = true, bool ignoreDestructable = false) => helper.AvoidAssistPrecise(targetIn, avoidStatic, ignoreDestructable);
        public Vector3 GetOtherDir(Tank targetToAvoid) => helper.GetOtherDir(targetToAvoid);
        public bool FixControlReversal(float drive) => helper.FixControlReversal(drive);
        public void RequestControllerSwap() => helper.RequestMovementControllerSwap(TankAIHelper.MovementSwapReason.SwitchAI);
        public void SetDriverType(AIDriverType driverType) => helper.SetDriverType(driverType);
        public string GetCoreControlString() => helper.GetCoreControlString();

        // ---- strategy-facing sinks ----
        public void DriveControlSink(float drive) => helper.DriveControl = drive;
        public void ProcessControl(Vector3 driveVal, Vector3 turnVal, Vector3 throttle, bool props, bool jets) => helper.ProcessControl(driveVal, turnVal, throttle, props, jets);
        public void SteerControl(Vector3 direction, float throttle) => helper.SteerControl(direction, throttle);
        public void SetDirectedControl(EControlOperatorSet cont) => helper.SetDirectedControl(cont);
        public EControlOperatorSet GetDirectedControl() => helper.GetDirectedControl();

        // ---- neighbour access + teardown ----
        public Visible GetNeighbourTarget(Tank neighbour)
        {
            if (neighbour == null) return null;
            var h = neighbour.GetComponent<TankAIHelper>();
            return h != null ? h.lastEnemyGet : null;
        }

        /// <summary>
        /// Narrow per-behavior reset (NOT a whole-component teardown). Release the target, stop the core,
        /// drop a carried block, zero MT offsets, reset the operator to Default + mark it dirty, drop combat
        /// intent. Refined further as behaviors land (Step 4) - kept conservative here.
        /// </summary>
        public void BehaviorTeardownState()
        {
            helper.ReleaseTarget();
            helper.SettleDown(true);
            helper.WantsToFight = false;
            helper.MTOffsetPos = Vector3.zero;
            helper.MTOffsetRot = Vector3.zero;
            helper.MTOffsetRotUp = Vector3.zero;
            _operator = EControlOperatorSet.Default;
            helper.SetDirectedControl(_operator);
            helper.MarkOperatorDirty();
        }
    }
}
