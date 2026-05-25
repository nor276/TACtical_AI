using UnityEngine;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Engine
{
    /// <summary>
    /// The full read/WRITE bus facade that behaviors and strategies receive instead of a raw TankAIHelper.
    /// The surface was derived empirically (a mechanical grep of every helper/mind/AIControl write, call,
    /// and read across the operations/cores layers) and is FROZEN here before any consumer migrates.
    /// Encapsulation is advisory - behaviors are same-assembly so internal members stay reachable - the
    /// value is a documented contract and discipline, not hard enforcement.
    ///
    /// Backing types: TankAIHelper (the bus + sinks) and EnemyMind (the typed enemy facade). The live
    /// drive-intent operator (EControlOperatorSet, a struct whose verbs also write helper.DriveVar/
    /// ThrottleState) is OWNED by the context so behaviors mutate it in place via the verb methods and the
    /// engine harvests it after the tick - preserving by-ref mutation without threading ref through Tick.
    ///
    /// Deliberately NOT on the behavior surface (stay internal to their owners): the raw lastEnemy write +
    /// OnHit (damage path / MP receive / loose-block assignment, which must not acquire a focus lock),
    /// RunRTSNaviEnemy (engine RTS navi), the enemy-mind SETUP writes (done by enemy auto-select, Step 5).
    /// </summary>
    public interface IAIContext
    {
        // ---- identity / references ----
        Tank Tank { get; }
        string Name { get; }
        bool IsEnemy { get; }                 // has an EnemyMind
        AIDriverType DriverType { get; }
        IMovementAIController MovementController { get; }
        AIAlignment TickAIAlign { get; }      // the latched alignment - never the live AIAlign
        AITreeType.AITypes LastAIType { get; }

        // ---- reads: ranges + combat posture (computed/allied-clamped on the helper) ----
        float MinCombatRange { get; }
        float MaxCombatRange { get; }
        float MaxObjectiveRange { get; }
        float LastCombatRange { get; }
        float LastOperatorRange { get; }
        bool FullMelee { get; }
        bool AdvancedAI { get; }
        bool AllMT { get; }
        float DamageThreshold { get; }
        bool CombatWantsCircleNow();

        // read-write combat posture
        EAttackMode AttackMode { get; set; }
        float TurretFraction { get; set; }
        bool WasRetreatingInCombat { get; set; }
        bool WantsToFight { get; set; }
        bool ChaseThreat { get; set; }
        bool Retreat { get; set; }            // internal set on the helper; reachable same-assembly
        int Provoked { get; set; }
        float Urgency { get; set; }
        float UrgencyOverload { get; set; }
        bool FIRE_ALL { get; set; }
        bool FirePROPS { get; set; }
        bool ForceSetBeam { get; set; }
        int WeaponDelayClock { get; set; }

        // ---- reads: physics ----
        Vector3 SafeVelocity { get; }
        Vector3 LocalSafeVelocity { get; }
        float RecentSpeed { get; }
        float RecentSpeedSigned { get; }
        bool MakingNetProgress { get; }
        float LastTechExtents { get; }
        float GroundOffsetHeight { get; }
        Vector3 DodgeSphereCenter { get; }
        float GetFrameHeight();
        bool IsTechMovingAbs(float minSpeed);
        bool IsOrbiting();
        bool IsOrbiting(float minimumCloseInSpeedSqr);

        // read-write physics knob (behaviors lower EstTopSped to slow the logical clock)
        float EstTopSped { get; set; }

        // ---- reads: targeting ----
        Visible LastEnemyGet { get; }
        bool BlockedLineOfSight { get; set; }
        bool CollectedTarget { get; set; }
        Visible GetPlayerTech();

        // ---- throttle / drive intent (writable state) ----
        AIThrottleState ThrottleState { get; set; }
        float DriveVar { get; set; }
        float AutoSpacing { get; set; }
        bool FullBoost { get; set; }
        bool LightBoost { get; set; }
        bool Attempt3DNavi { get; set; }
        bool AvoidStuff { get; set; }
        bool SecondAvoidence { get; set; }
        Vector3 Navi3DDirect { get; set; }
        Vector3 Navi3DUp { get; set; }

        // ---- objective / role slots (writable) ----
        Visible TheResource { get; set; }
        Visible TheResourceNode { get; set; }
        Visible TheGuardedAlly { get; set; }
        Visible TheHostTech { get; set; }
        Tank TheBase { get; set; }
        Tank LastCloseAlly { get; }
        IAIFollowable LastBasePos { get; set; }
        float LastBaseExtremes { get; set; }
        Visible LastPlayer { get; set; }
        bool FoundBase { get; set; }
        bool FoundGoal { get; set; }
        bool IsMultiTech { get; set; }
        TankBlock HeldBlock { get; }
        Transform Obst { get; set; }

        // ---- multitech offsets (writable; zeroed on teardown) ----
        Vector3 MTOffsetPos { get; set; }
        Vector3 MTOffsetRot { get; set; }
        Vector3 MTOffsetRotUp { get; set; }
        bool MTLockedToTechBeam { get; set; }
        bool MTMimicHostAvail { get; set; }

        // ---- timers / clocks (writable) ----
        int ActionPause { get; set; }
        int DelayedAnchorClock { get; set; }
        int UnanchorCountdown { get; set; }
        float NextFindTargetTime { get; set; }
        bool PendingDamageCheck { get; set; }
        float RepairStepperClock { get; set; }
        int FrustrationMeter { get; set; }
        AIRunState RunState { get; set; }
        bool TechIsApproaching { get; set; }

        // ---- anchoring ----
        bool AutoAnchor { get; set; }
        bool IsAutoAnchored { get; }
        bool CanAttemptAnchor { get; }
        bool CanAutoAnchor { get; }
        bool PlayerAllowAutoAnchoring { get; }
        void TryInsureAutoAnchor();
        void TryInsureManualAnchor();
        void Unanchor();
        void AnchorIgnoreChecks(bool forced = false);

        // ---- navigation / destination / RTS state (reads) ----
        EDriveDest DriveDestDirected { get; }
        bool IsDirectedMovingToDest { get; }
        bool IsDirectedMovingFromDest { get; }
        bool AdviseAwayCore { get; }
        bool DoSteerCore { get; }
        Vector3 LastDestinationCore { get; }
        Vector3 LastDestinationOp { get; }
        bool RTSControlled { get; set; }
        bool IsGoingToPositionalRTSDest { get; }

        // ---- AISetSettings struct field setters (never expose the struct by value - writes would be lost) ----
        bool SideToThreat { get; }
        void SetCombatSpacing(float v);
        void SetCombatChase(float v);
        void SetObjectiveRange(float v);
        void SetScanRange(float v);
        void SetAdvancedAI(bool v);
        void SetAllMT(bool v);
        void SetFullMelee(bool v);
        void SetSideToThreat(bool v);
        void SetAutoRepair(bool v);
        void SetUseInventory(bool v);

        // ---- EnemyMind facade (reads behaviors use; setup writes done elsewhere) ----
        bool MindLikelyMelee { get; }
        bool MindAttackPlayer { get; }
        bool MindCanCallRetreat { get; }
        bool MindCanDoRetreat { get; }
        bool MindInvertBullyPriority { get; }
        Vector3 MindSceneStationaryPos { get; }
        bool MindSolarsAvail { get; }
        void SetCommanderAttack(EAttackMode mode);   // proxies AttackMode through the mind

        // ---- target verbs (distinct, NOT interchangeable) ----
        bool SetPursuit(Visible target);             // acquires AND sets KeepEnemyFocus
        bool SetPursuit(Visible target, bool force);
        void EndPursuit();                           // drops the lock, keeps the target
        void ReleaseTarget();                        // drops both
        Visible TryRefreshEnemyAllied();
        Visible TryRefreshEnemyEnemy(bool invertBullyPriority);

        // ---- the owned drive-intent operator: verbs mutate it in place + write throttle atomically ----
        void OpStop();
        void OpForwards();
        void OpReverse();
        void OpFaceDest();
        void OpDriveToFacingTowards();
        void OpDriveToFacingBackwards();
        void OpDriveAwayFacingTowards();
        void OpDriveAwayFacingAway();
        void OpDriveToFacingPerp();
        void OpDriveAwayFacingPerp();
        void OpResetActions();
        void OpSetLastDest(Vector3 posScene);
        void MarkOperatorDirty();

        // ---- engine-service entry points behaviors call directly (operate on the owned operator) ----
        void TryHandleObstruction(bool hasMessaged, float dist, bool useRush, bool useGun);
        bool AutoHandleObstruction(float dist = 0, bool useRush = false, bool useGun = true, float div = 4);
        void RemoveObstruction(float searchRad = 12);
        void SettleDown(bool stopCore = true);
        float GetDistanceFromTask(Vector3 taskLocation, float additionalSpacing = 0);
        float GetDistanceFromTask2D(Vector3 taskLocation, float additionalSpacing = 0);
        void SetDistanceFromTaskUnneeded();
        bool CanStoreEnergy();
        float GetEnergyPercent();
        void DropAllItemsInCollectors();
        bool HoldBlock(Visible tb);
        void DropBlock();
        void DropBlock(Vector3 throwDirection);
        void MaxBoost();
        void MaxProps();
        Vector3 InterceptTargetDriving(Visible targetTank);
        Vector3 RoughPredictTarget(Tank targetTank);
        float IgnoreEnemyDistance();
        float UpdateEnemyDistance(Vector3 enemyPosition);
        void UpdateDamageThreshold();
        void UpdateVanillaAvoidence();
        void InsureTechMemor(string context, bool doFirstSave);
        Vector3 AvoidAssist(Vector3 targetIn, bool avoidStatic = true);
        Vector3 AvoidAssistPrecise(Vector3 targetIn, bool avoidStatic = true, bool ignoreDestructable = false);
        Vector3 GetOtherDir(Tank targetToAvoid);
        bool FixControlReversal(float drive);
        void RequestControllerSwap();
        void SetDriverType(AIDriverType driverType);
        string GetCoreControlString();

        // ---- strategy-facing sinks (these alone may touch tank.control, via the helper) ----
        void DriveControlSink(float drive);
        void ProcessControl(Vector3 driveVal, Vector3 turnVal, Vector3 throttle, bool props, bool jets);
        void SteerControl(Vector3 direction, float throttle);
        void SetDirectedControl(EControlOperatorSet cont);
        EControlOperatorSet GetDirectedControl();

        // ---- neighbour access + teardown ----
        Visible GetNeighbourTarget(Tank neighbour);   // mimic-fire reads a neighbour's lastEnemyGet
        void BehaviorTeardownState();
    }
}
