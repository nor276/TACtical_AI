using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using TAC_AI.AI;
using TAC_AI.AI.Enemy;
using TAC_AI.Templates;

namespace TAC_AI.World
{
    public enum AITeamMode
    {
        Idle,
        Retreating,
        Defending,
        Attacking,
        SiegingPlayer,
    }
    public enum AIFounderMode
    {
        HomeIdle,
        HomeReturn,
        HomeDefend,
        VendorGo,
        VendorBuy,
        VendorMission,
        OtherAttack,
        ScriptWait,
    }
    public static class NP_PresenceExtensions
    {
        internal static bool IsValidAndRegistered(this NP_Presence presenseCheck)
        {
            return presenseCheck != null && presenseCheck.registered;
        }
    }

    /// <summary>
    /// The master class to command fleets of AI
    /// </summary>
    public class NP_Presence
    {
#if DEBUG
        protected static bool DoDebugLog = true;
#else
        protected static bool DoDebugLog = false;
#endif

        public virtual bool RequiresExistingTechs => true;
        public bool registered => ManEnemyWorld.AllTeamsUnloaded.ContainsKey(team);
        protected int team = AIGlobals.EnemyTeamsRangeStart;
        public int Team => team;
        public bool AnyLeftStanding => EBUs.Any() || EMUs.Any();
        protected bool canSiege = false;
        public NP_BaseUnit MainBase = null;
        public HashSet<NP_BaseUnit> EBUs = new HashSet<NP_BaseUnit>();
        public HashSet<NP_MobileUnit> EMUs = new HashSet<NP_MobileUnit>();
        protected HashSet<NP_TechUnit> Fighting = new HashSet<NP_TechUnit>();
        public bool IsFighting => Fighting.Any();

        protected float lastAttackedTimestep = 0;
        protected AITeamMode teamMode = AITeamMode.Idle;
        public AITeamMode TeamMode => teamMode;
        public bool attackStarted = false;
        public IntVector2 homeTile => MainBase != null ? MainBase.tilePos :
            WorldPosition.FromGameWorldPosition(Singleton.cameraTrans.position).TileCoord;
        protected IntVector2 lastAttackTile;
        public IntVector2 attackTile => lastAttackTile;
        protected IntVector2 lastEventTile;
        public HashSet<IntVector2> tilesHasOwnTechs = new HashSet<IntVector2>();
        public HashSet<IntVector2> scannedPositions = new HashSet<IntVector2>();
        public HashSet<IntVector2> scannedEnemyTiles = new HashSet<IntVector2>();
        public Visible lastTarget
        {
            get
            {
                if (lastTargetUpdateCount == 0)
                    return null;
                return _lastTarget;
            }
            set
            {
                _lastTarget = value;
                lastTargetUpdateCount = ManEnemyWorld.OperatorTicksKeepTarget;
            }
        }
        private Visible _lastTarget = null;
        private int lastTargetUpdateCount = 0;
        protected void TickTargetExpiry() { if (lastTargetUpdateCount > 0) lastTargetUpdateCount--; }

        public NP_Presence(int Team)
        {
            team = Team;
        }

        // Checks
        public bool HasMobileETUs()
        {
            return EMUs.ToList().Exists(delegate (NP_MobileUnit cand) { return cand.MoveSpeed > 12; });
        }
        public bool HasANYTechs()
        {
            return EBUs.Count > 0 || EMUs.Count > 0 || RLoadedBases.TeamActiveMobileTechCount(Team) > 0 || RLoadedBases.TeamActiveAnyBaseCount(Team) > 0;
        }
        public int GlobalTotalTechCount()
        {
            return GlobalMakerBaseCount() + GlobalMobileTechCount();
        }
        public int GlobalMakerBaseCount()
        {
            return EBUs.Count + RLoadedBases.TeamActiveMakerBaseCount(Team);
        }
        public int GlobalMobileTechCount()
        {
            return EMUs.Count + RLoadedBases.TeamActiveMobileTechCount(Team);
        }

        public int GlobalTeamCost()
        {
            int teamValue = 0;
            foreach (var item in EMUs)
            {
                try
                {
                    teamValue += item.tech.m_TechData.GetValue();
                }
                catch { }
            }
            foreach (var item in EBUs)
            {
                try
                {
                    teamValue += item.tech.m_TechData.GetValue();
                }
                catch { }
            }
            foreach (var item in ManTechs.inst.IterateTechs())
            {
                if (item.Team == Team)
                {
                    try
                    {
                        teamValue += item.GetValue();
                    }
                    catch { }
                }
            }
            return teamValue;
        }

        /// <summary>
        /// Returns false if the team should be removed
        /// </summary>
        /// <returns></returns>
        public virtual bool UpdateOperatorRTS(List<NP_TechUnit> TUDestroyed)
        {
            PresenceDebug(KickStart.ModID + ": UpdateGrandCommandRTS - Turn for Team " + Team);
            attackStarted = false;
            TickTargetExpiry();
            HandleUnitRecon();
            HandleCombat(TUDestroyed);
            HandleUnitMoving();
            UpdateRevenue();
            UnloadedBases.TryUnloadedBaseOperations(this);
            HandleRepairs();
            HandleRecharge();
            UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);
            if (MainBase != null)
            {   // To make sure little bases are not totally stagnant - the AI is presumed to be mining aand doing missions
                PresenceDebugDEV(KickStart.ModID + ": UpdateGrandCommandRTS - Team final funds " + MainBase.BuildBucks);
            }
            return AnyLeftStanding;
        }
        /// <summary>
        /// Returns false if our team no longer exists
        /// </summary>
        /// <returns></returns>
        public virtual bool UpdateOperator()
        {
            PresenceDebug(KickStart.ModID + ": UpdateGrandCommand - Turn for Team " + Team);
            attackStarted = false;
            UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);

            if (MainBase != null)
                UnloadedBases.PurgeIfNeeded(this, MainBase);
            return AnyLeftStanding;
        }

        public virtual void UpdateMaintainer(float timeDelta)
        {
            // The techs move every UpdateMoveDelay seconds
            foreach (var item in Fighting)
            {
                item.MovementSceneDelta(timeDelta);
            }
        }

        // Basics
        protected void HandleRepairs()
        {
            NP_BaseUnit funds = UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);

            float healMulti;
            if (WasInCombat())
                healMulti = ManEnemyWorld.OperatorTickDelay / Mathf.Max(AIERepair.eDelayCombat, 1);
            else
                healMulti = ManEnemyWorld.OperatorTickDelay / Mathf.Max(AIERepair.eDelaySafe, 1);

            int numHealed = 0;
            if (funds != null)
            {
                foreach (NP_BaseUnit EBU in EBUs)
                {
                    try
                    {
                        if (EBU.Health < EBU.MaxHealth)
                        {
                            if (funds.BuildBucks > ManEnemyWorld.HealthRepairCost * healMulti)
                            {
                                funds.SpendBuildBucks((int)(ManEnemyWorld.HealthRepairCost * healMulti));
                                EBU.Health = Math.Min(EBU.MaxHealth, EBU.Health + (int)(ManEnemyWorld.HealthRepairRate * healMulti));
                                numHealed++;
                            }
                        }
                    }
                    catch { }
                }
                foreach (NP_TechUnit ETU in EMUs)
                {
                    try
                    {
                        if (ETU.Health < ETU.MaxHealth)
                        {
                            if (funds.BuildBucks > ManEnemyWorld.HealthRepairCost * healMulti)
                            {
                                funds.SpendBuildBucks((int)(ManEnemyWorld.HealthRepairCost * healMulti));
                                ETU.Health = Math.Min(ETU.MaxHealth, ETU.Health + (int)(ManEnemyWorld.HealthRepairRate * healMulti));
                                numHealed++;
                            }
                        }
                    }
                    catch { }
                }
                if (numHealed > 0)
                {
                }
            }
        }
        protected void HandleRecharge()
        {
            long excessEnergy = 0;
            foreach (var item in EBUs)
            {
                excessEnergy += item.Generate(ManEnemyWorld.OperatorTickDelay, !ManTimeOfDay.inst.NightTime);
            }
            PresenceDebugDEV("HandleRecharge - Generated " + excessEnergy + " energy");
            if (MainBase != null && excessEnergy > 0)
            {
                foreach (var item in ManEnemyWorld.GetUnloadedTechsInTile(MainBase.tilePos))
                {
                    if (item.teamInst == this)
                    {
                        item.Recharge(ref excessEnergy);
                        if (excessEnergy <= 0)
                            return;
                    }
                }
            }
            PresenceDebugDEV("HandleRecharge - Remaining " + excessEnergy + " energy");
        }

        // Recon
        protected void HandleUnitRecon()
        {
            scannedPositions.Clear();
            UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);

            tilesHasOwnTechs.Clear();
            foreach (NP_TechUnit ETU in EMUs)
            {
                try
                {
                    if (!tilesHasOwnTechs.Contains(ETU.tilePos))
                        tilesHasOwnTechs.Add(ETU.tilePos);
                }
                catch { }
            }
            foreach (NP_TechUnit ETU in EBUs)
            {
                try
                {
                    if (!tilesHasOwnTechs.Contains(ETU.tilePos))
                        tilesHasOwnTechs.Add(ETU.tilePos);
                }
                catch { }
            }

            if (MainBase != null)
            {
                UnloadedBases.GetScannedTilesAroundTech(MainBase); // This happens first - home defense is more important

                if (!attackStarted)
                {
                    foreach (var item in tilesHasOwnTechs)
                    {
                        UnloadedBases.GetScannedTilesAtCoord(this, item);
                    }
                    if (UnloadedBases.SearchPatternCacheSort(this, MainBase.tilePos, scannedEnemyTiles, out var posEnemy))
                        SetAttackMode(posEnemy);
                }
                else if (teamMode == AITeamMode.Idle)
                {
                    if (UnloadedBases.SearchPatternCacheNoSort(this, scannedEnemyTiles, out var posEnemy))
                        SetDefendMode(posEnemy);
                }
            }
        }

        // Combat
        protected void HandleCombat(List<NP_TechUnit> TUDestroyed)
        {
            Fighting.Clear();
            float damageTime = (float)ManEnemyWorld.OperatorTickDelay / ManEnemyWorld.ExpectedDPSDelitime;
            foreach (IntVector2 TT in tilesHasOwnTechs)
            {
                if (Singleton.Manager<ManWorld>.inst.CheckIsTileAtPositionLoaded(Singleton.Manager<ManWorld>.inst.TileManager.CalcTileCentreScene(TT)))
                {
                    continue; // tile loaded
                }
                if (ManEnemyWorld.TryGetConflict(TT, Team, out List<NP_TechUnit> Allies, out List<NP_TechUnit> Enemies))
                {
                    int damageTurn = DistributeDamageThisTurn(TT, Allies, damageTime, Enemies, TUDestroyed);
                    if (damageTurn != 0)
                        ReportCombat("-- This Turn Total: " + damageTurn + " --");
                }
                else
                {
                    continue;
                }
            }
            if (AIECore.RetreatingTeams.Contains(team))
            {
                if (TankAIManager.GetTeamTanks(team).Any())
                    teamMode = AITeamMode.Retreating;
                else
                {
                    AIECore.TeamRetreat(team, false, true);
                    teamMode = AITeamMode.Idle;
                }
            }
        }
        protected int DistributeDamageThisTurn(IntVector2 TT, List<NP_TechUnit> Allied, float damageTime, List<NP_TechUnit> Enemies, List<NP_TechUnit> TUDestroyed)
        {
            if (Allied.Count == 0 || Enemies.Count == 0)
                return 0;
            ReportCombat("Combat underway at " + TT + " | " + Team + " vs " + Enemies.FirstOrDefault().tech.m_TeamID);
            OnCombat();
            int allyIndex = 0;
            int damageTotal = 0;
            Enemies.Shuffle();
            for (int evilIndex = 0; evilIndex < Enemies.Count && allyIndex < Allied.Count; evilIndex++)
            {
                NP_TechUnit target = Enemies[evilIndex];
                int damageWave = 1;
                int attacksThisFrame = UnityEngine.Random.Range(1, Allied.Count - allyIndex);
                ReportCombat("Team " + Team + " is firing at Target " + target.Name + " with " + attacksThisFrame + " Techs!");
                for (int step = 0; step < attacksThisFrame; step++)
                {
                    int damageUnit;
                    var Ally = Allied[allyIndex];
                    if (Ally is NP_BaseUnit)
                    {
                        damageUnit = Mathf.CeilToInt(Ally.AttackPower * ManEnemyWorld.BaseAccuraccy);
                        ReportCombat("  Base " + Ally.Name + " - Power [" + damageUnit + "], Accuraccy[" + ManEnemyWorld.BaseAccuraccy + "]");
                    }
                    else
                    {
                        float mobileAcc = ManEnemyWorld.MobileAccuraccy - (ManEnemyWorld.MobileSpeedAccuraccyReduction * Ally.GetSpeed());
                        damageUnit = Mathf.CeilToInt(Ally.AttackPower * mobileAcc);
                        ReportCombat("  Unit " + Ally.Name + " - Power [" + damageUnit + "], Accuraccy[" + mobileAcc + "]");
                        Fighting.Add(Ally);
                    }
                    allyIndex++;
                    damageWave += damageUnit;
                }
                float EvasionEnemy;
                if (target is NP_BaseUnit)
                    EvasionEnemy = ManEnemyWorld.BaseEvasion;
                else
                    EvasionEnemy = target.GetSpeed() * ManEnemyWorld.MobileSpeedToEvasion;
                if (EvasionEnemy < 1)
                    EvasionEnemy = 1;
                else
                    EvasionEnemy = UnityEngine.Random.Range(1, EvasionEnemy);
                ReportCombat("-- Enemy Dodge Roll: " + EvasionEnemy + " --");
                damageWave = Mathf.CeilToInt((Math.Max(5 * attacksThisFrame, damageWave) * damageTime) / EvasionEnemy);
                ReportCombat("-- TotalRecalc: " + damageWave + " --");
                damageTotal += damageWave;
                if (target.RecieveDamage(damageWave))
                {
                    try
                    {
                        ReportCombat("Enemy " + target.Name + " has been destroyed!");
                        if (target is NP_BaseUnit EBU && ManBaseTeams.TryGetBaseTeamDynamicOnly(target.Team, out var ETD))
                        {
                            var lootGain = ETD.StealBuildBucks();
                            if (lootGain > 0)
                            {
                                if (UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this) != null)
                                    MainBase.AddBuildBucks(lootGain);
                            }
                        }
                    }
                    catch { }
                    TUDestroyed.Add(target);
                }
            }
            return damageTotal;
        }

        // Movement
        protected void HandleUnitMoving()
        {
            UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);
            if (MainBase == null)
            {   // Attack the player
                MoveAllETUsNoMainBase();
            }
            else
            {   // Manage Base operations
                switch (teamMode)
                {
                    case AITeamMode.Retreating:
                    case AITeamMode.Defending:
                        lastEventTile = homeTile;
                        if (!MoveAllETUs())
                            PresenceDebug("   No movement this turn.");
                        break;
                    case AITeamMode.Attacking:
                        lastEventTile = lastAttackTile;
                        if (!MoveAllETUs())
                            PresenceDebug("   No movement this turn.");
                        break;
                    case AITeamMode.SiegingPlayer:
                        lastEventTile = lastAttackTile;
                        if (!MoveAllETUs())
                            PresenceDebug("   No movement this turn.");
                        break;
                    case AITeamMode.Idle:
                    default:
                        lastEventTile = homeTile;
                        if (canSiege && UnloadedBases.IsPlayerWithinProvokeDist(MainBase.tilePos, out Tank offender))
                        {
                            PresenceDebug("Team " + TeamNamer.GetTeamName(team) +  " can attack your base!  Threshold: " + EMUs.Count + " / " + (KickStart.EnemyTeamTechLimit / 2f));
                            var player = Singleton.playerTank;
                            if (player != null && ManEnemySiege.CheckShouldLaunchSiege(this, offender))
                            {
                                SetSiegeMode(ManWorld.inst.TileManager.SceneToTileCoord(player.boundsCentreWorldNoCheck));
                            }
                        }
                        break;
                }
                PresenceDebugDEV("Main Base is " + MainBase.Name + " at " + MainBase.tilePos);
                PresenceDebugDEV("- TeamMode: " + teamMode + ",  EventTile: " + lastEventTile);
            }
        }
        protected void MoveAllETUsNoMainBase()
        {
            int count = EMUs.Count;
            for (int step = 0; step < count; step++)
            {
                NP_MobileUnit ETU = EMUs.ElementAt(step);
                if (ETU.GetSpeed() < 10)
                    continue;
                IntVector2 playerCoord = WorldPosition.FromGameWorldPosition(Singleton.cameraTrans.position).TileCoord;
                if (!Singleton.Manager<ManWorld>.inst.CheckIsTileAtPositionLoaded(Singleton.Manager<ManWorld>.inst.TileManager.CalcTileCentreScene(ETU.tilePos)))
                {
                    if (!ETU.isMoving)
                    {
                        ManEnemyWorld.StrategicMoveQueue(ETU, playerCoord, OnUnitReachDestinationNoBase, out bool fail);
                        if (fail)
                        {
                            ManEnemyWorld.StopManagingUnit(ETU);
                            step--;
                            count--;
                        }
                    }
                    else
                        PresenceDebug("Unit " + ETU.Name + " is moving");
                }
            }
        }

        private static StringBuilder moving = new StringBuilder();
        private static StringBuilder startedMove = new StringBuilder();
        /// <summary>
        ///
        /// </summary>
        /// <returns>True if a tech is moving</returns>
        private bool MoveAllETUs()
        {
            bool techsMoving = false;
            if (DoDebugLog)
            {
                int count = EMUs.Count;
                try
                {
                    for (int step = 0; step < count; step++)
                    {
                        NP_MobileUnit ETU = EMUs.ElementAt(step);
                        if (ETU.tilePos == lastEventTile)
                            continue;
                        if (!ETU.isMoving)
                        {
                            if (!MoveETU(ETU, ref techsMoving))
                            {
                                ManEnemyWorld.StopManagingUnit(ETU);
                                step--;
                                count--;
                            }
                        }
                        else
                        {
                            moving.Append(ETU.Name + ", ");
                            techsMoving = true;
                        }
                    }
                    if (startedMove.Length > 0)
                    {
                        PresenceDebug("   Unit(s) " + startedMove.ToString() + " have started moving to tile " + lastEventTile);
                    }
                    if (moving.Length > 0)
                    {
                        PresenceDebug("   Unit(s) " + moving.ToString() + " are moving");
                    }
                }
                finally
                {
                    moving.Clear();
                    startedMove.Clear();
                }
            }
            else
            {
                int count = EMUs.Count;
                for (int step = 0; step < count; step++)
                {
                    NP_MobileUnit ETU = EMUs.ElementAt(step);
                    if (ETU.tilePos == lastEventTile)
                        continue;
                    if (!ETU.isMoving)
                    {
                        if (!MoveETU(ETU, ref techsMoving))
                        {
                            ManEnemyWorld.StopManagingUnit(ETU);
                            step--;
                            count--;
                        }
                    }
                    else
                        techsMoving = true;
                }
            }
            return techsMoving;
        }

        protected virtual bool MoveETU(NP_MobileUnit ETU, ref bool techsMoving)
        {
            if (ManEnemyWorld.StrategicMoveQueue(ETU, lastEventTile, OnUnitReachDestinationBase, out bool fail))
            {
                startedMove.Append(ETU.Name + ", ");
                techsMoving = true;
            }
            return !fail;
        }
        internal void ChangeTeamOfAllTechsUnloaded(int newTeam)
        {
            team = newTeam;
            foreach (var item in EBUs)
            {
                if (item.tech != null)
                    item.tech.m_TeamID = team;
            }
            foreach (var item in EMUs)
            {
                if (item.tech != null)
                    item.tech.m_TeamID = team;
            }
        }

        protected void OnUnitReachDestinationNoBase(TileMoveCommand TMC, bool pathSuccess, bool activeScene)
        {
            if (pathSuccess && activeScene)
            {
                if (TMC.TryGetActiveTank(out var tank) && Singleton.playerTank)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": OnUnitReachDestinationNoBase for " + tank.name);
                    var helper = tank.GetHelperInsured();
                    helper.DirectRTSDest(Singleton.playerTank.boundsCentreWorldNoCheck);
                    helper.SetRTSState(true);
                }
            }
        }
        protected void OnUnitReachDestinationBase(TileMoveCommand TMC, bool pathSuccess, bool activeScene)
        {
            if (pathSuccess && activeScene)
            {
                if (TMC.TryGetActiveTank(out var tank))
                {
                    var inst = tank.GetHelperInsured();
                    var mind = inst.GetComponent<EnemyMind>();
                    if (mind != null && lastTarget != null && lastTarget.isActive)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": OnUnitReachDestinationBase for " + tank.name);
                        mind.GetRevengeOn(lastTarget);
                    }
                }
            }
        }

        internal void UpdateRevenue()
        {
            foreach (NP_BaseUnit EBU in EBUs)
            {
                if (Singleton.Manager<ManVisible>.inst.GetTrackedVisible(EBU.ID) == null)
                {
                    EBU.AddBuildBucks(EBU.revenue + ManEnemyWorld.ExpansionIncome);
                }
            }
            NP_BaseUnit mainBase = UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);

            if (mainBase != null)
            {
                mainBase.AddBuildBucks(ManEnemyWorld.PassiveHQBonusIncome * ManEnemyWorld.OperatorTickDelay);
                if (AIGlobals.TurboAICheat)
                {
                    mainBase.AddBuildBucks(25000 * ManEnemyWorld.OperatorTickDelay);
                }
            }
        }
        public bool AddBuildBucks(int add)
        {
            NP_BaseUnit EBU = UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);
            if (EBU != null)
            {
                EBU.AddBuildBucks(add);
                return true;
            }
            return false;
        }

        internal void SetAttackMode(IntVector2 tilePos, Visible target = null)
        {
            attackStarted = true;
            teamMode = AITeamMode.Attacking;
            lastAttackTile = tilePos;
            lastTarget = target;
        }
        internal void SetSiegeMode(IntVector2 tilePos)
        {
            attackStarted = true;
            teamMode = AITeamMode.SiegingPlayer;
            lastAttackTile = tilePos;
            lastTarget = null;
        }
        internal void SetDefendMode(IntVector2 tilePos)
        {
            teamMode = AITeamMode.Defending;
            lastAttackTile = tilePos;
        }
        internal void ResetModeToIdle()
        {
            teamMode = AITeamMode.Idle;
            NP_BaseUnit mainBase = UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);
            if (mainBase != null)
            {
                lastAttackTile = mainBase.tilePos;
            }
        }

        // MISC
        internal void PresenceDebug(string thing)
        {
#if DEBUG
            DebugTAC_AI.Log(thing);
#endif
        }
        internal void PresenceDebugDEV(string thing)
        {
#if DEBUG
            DebugTAC_AI.Log(thing);
#endif
        }
        public static void ReportCombat(string thing)
        {
#if DEBUG
            DebugTAC_AI.Log(KickStart.ModID + ": EnemyPresence - " + thing);
#endif
            ManEnemyWorld.AddToCombatLog(thing);
        }
        public bool WasInCombat()
        {
            return lastAttackedTimestep > Time.time;
        }
        internal void OnCombat()
        {
            lastAttackedTimestep = Time.time + (ManEnemyWorld.OperatorTickDelay * 2);
            if (teamMode == AITeamMode.Idle)
                teamMode = AITeamMode.Attacking;
        }

        public int BuildBucks()
        {
            int count = 0;
            foreach (NP_BaseUnit EBU in EBUs)
            {
                count += EBU.BuildBucks;
            }
            return count;
        }

    }

    /// <summary>
    /// The enemy base in world-relations
    /// REVISED: this is the automatic per-team fleet controller (founder/troll handling on top of NP_Presence), not a single base.
    /// </summary>
    public class NP_Presence_Automatic : NP_Presence
    {

        internal NP_MobileUnit teamFounder;
        private Tank teamFounderActive;
        private AIFounderMode founderMode = AIFounderMode.HomeIdle;
        private int lastFounderStopUpdateTicks = 0;

        public NP_Presence_Automatic(int Team, bool canLaunchSieges) : base (Team)
        {
            canSiege = canLaunchSieges;
        }

        /// <summary>
        /// Returns false if the team should be removed
        /// </summary>
        /// <returns></returns>
        public override bool UpdateOperatorRTS(List<NP_TechUnit> TUDestroyed)
        {
            if (Team == SpecialAISpawner.trollTeam)
            {
                HandleTraderTrolls();
                return EBUs.Count > 0 || EMUs.Count > 0;
            }
            if (GlobalMakerBaseCount() == 0)
            {
                DebugTAC_AI.Info(KickStart.ModID + ": UpdateGrandCommandRTS - Team " + Team + " has no production bases");
                return EBUs.Count > 0 || EMUs.Count > 0;
            }
            PresenceDebug(KickStart.ModID + ": UpdateGrandCommandRTS - Turn for Team " + Team);
            attackStarted = false;
            TickTargetExpiry();
            if (lastFounderStopUpdateTicks > 0)
                lastFounderStopUpdateTicks--;
            TryGetFounderUnloaded(out teamFounder);
            HandleUnitRecon();
            HandleCombat(TUDestroyed);
            HandleUnitMoving();
            UpdateRevenue();
            UnloadedBases.TryUnloadedBaseOperations(this);
            HandleRepairs();
            HandleRecharge();
            UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);
            if (MainBase != null)
            {   // To make sure little bases are not totally stagnant - the AI is presumed to be mining aand doing missions
                PresenceDebugDEV(KickStart.ModID + ": UpdateGrandCommandRTS - Team final funds " + MainBase.BuildBucks);
            }
            return AnyLeftStanding;
        }

        public override bool UpdateOperator()
        {
            if (Team == SpecialAISpawner.trollTeam)
            {
                HandleTraderTrolls();
                return AnyLeftStanding;
            }
            if (GlobalMakerBaseCount() == 0)
            {
                DebugTAC_AI.Info(KickStart.ModID + ": UpdateGrandCommand - Team " + Team + " has no production bases");
                return false;
            }
            PresenceDebug(KickStart.ModID + ": UpdateGrandCommand - Turn for Team " + Team);
            attackStarted = false;
            UnloadedBases.RefreshTeamMainBaseIfAnyPossible(this);

            if (MainBase != null)
                UnloadedBases.PurgeIfNeeded(this, MainBase);
            return AnyLeftStanding;
        }

        protected override bool MoveETU(NP_MobileUnit ETU, ref bool techsMoving)
        {
            if (ETU.isFounder)
            {
                if (!DoFounderMovement(ETU, ref techsMoving))
                {
                    return false;
                }
                return true;
            }
            else
                return base.MoveETU(ETU, ref techsMoving);
        }

        // Checks
        public bool ShouldReturnToBase()
        {
            return teamMode == AITeamMode.Retreating || teamMode == AITeamMode.Defending;
        }

        // Founder
        /// <summary>
        /// Don't use for end-of task sets
        /// </summary>
        /// <param name="initial"></param>
        /// <param name="anon"></param>
        /// <returns></returns>
        private static bool CanFounderSwitchState(AIFounderMode initial, AIFounderMode anon)
        {
            switch (initial)
            {
                case AIFounderMode.HomeIdle:
                case AIFounderMode.VendorGo:
                case AIFounderMode.VendorBuy:
                    return true;
                case AIFounderMode.HomeReturn:
                    if (anon == AIFounderMode.VendorGo)
                        return false;
                    return true;
                case AIFounderMode.HomeDefend:
                    if (anon == AIFounderMode.HomeIdle)
                        return true;
                    return false;
                case AIFounderMode.VendorMission:
                case AIFounderMode.ScriptWait:
                    return false;
                case AIFounderMode.OtherAttack:
                    switch (anon)
                    {
                        case AIFounderMode.HomeIdle:
                        case AIFounderMode.HomeReturn:
                        case AIFounderMode.VendorGo:
                        case AIFounderMode.VendorBuy:
                        case AIFounderMode.VendorMission:
                        case AIFounderMode.OtherAttack:
                            return false;
                        case AIFounderMode.HomeDefend:
                        case AIFounderMode.ScriptWait:
                            return true;
                        default:
                            throw new Exception(KickStart.ModID + ": EnemyPresence.CanSwitchState anon variable is invalid " + anon.ToString());
                    }
                default:
                    throw new Exception(KickStart.ModID + ": EnemyPresence.CanSwitchState initial variable is invalid " + initial.ToString());
            }
        }
        /// <summary>
        /// WORK IN PROGRESS - returns false if it failed
        /// </summary>
        /// <param name="ETU"></param>
        /// <param name="techsMoving"></param>
        private bool DoFounderMovement(NP_TechUnit ETU, ref bool techsMoving)
        {
            AIFounderMode takeAction;
            switch (teamMode)
            {
                case AITeamMode.Retreating:
                case AITeamMode.Defending:
                    takeAction = AIFounderMode.HomeDefend;
                    break;
                case AITeamMode.Attacking:
                    takeAction = AIFounderMode.OtherAttack;
                    break;
                default:
                    takeAction = AIFounderMode.HomeIdle;
                    break;
            }
            if (takeAction != AIFounderMode.HomeIdle && CanFounderSwitchState(founderMode, takeAction))
            {
                founderMode = takeAction;
                // Base is under attack
                if (ManEnemyWorld.StrategicMoveQueue(ETU, lastEventTile, OnUnitReachDestinationBase, out bool fail))
                {
                    PresenceDebug(" Founder " + ETU.Name.ToString() + " is moving to tile " + lastEventTile + " to do " + founderMode);
                    techsMoving = true;
                }
                if (fail)
                    return false;
            }
            else
            {   // Do random things
                if (!SetFounderDestination(ETU, ref techsMoving))
                    return false;
            }
            return true;
        }
        /// <summary>
        /// WORK IN PROGRESS
        /// </summary>
        /// <param name="ETU"></param>
        /// <param name="techsMoving"></param>
        private bool SetFounderDestination(NP_TechUnit ETU, ref bool techsMoving)
        {
            if (MainBase == null || lastFounderStopUpdateTicks > 0)
                return true;
            if (CanFounderSwitchState(founderMode, AIFounderMode.VendorGo))
            {
                founderMode = AIFounderMode.VendorGo;
                PresenceDebug(" Founder " + ETU.Name.ToString() + " is moving to tile " + lastEventTile + " to do " + founderMode);
                if (ManWorld.inst.TryFindNearestVendorPos(MainBase.WorldPos.GameWorldPosition, out var vendorPosWorld))
                {
                    if (ManEnemyWorld.StrategicMoveQueue(ETU, WorldPosition.FromGameWorldPosition(vendorPosWorld).TileCoord,
                        OnFounderReachVendor, out bool fail))
                    {
                        techsMoving = true;
                    }
                    return !fail;
                }
            }
            else if (founderMode == AIFounderMode.VendorBuy)
            {
                founderMode = AIFounderMode.HomeReturn;
                if (ManEnemyWorld.StrategicMoveQueue(ETU, MainBase.tilePos, OnFounderReachVendor, out bool fail))
                {
                    techsMoving = true;
                }
                if (fail) return false;
            }
            else if (CanFounderSwitchState(founderMode, AIFounderMode.HomeIdle))
            {
                founderMode = AIFounderMode.HomeIdle;
                if (ManEnemyWorld.StrategicMoveQueue(ETU, MainBase.tilePos, OnFounderReachVendor, out bool fail))
                {
                    techsMoving = true;
                }
                if (fail) return false;
            }
            return true;
        }
        private void OnFounderReachVendor(TileMoveCommand TMC, bool pathSuccess, bool activeScene)
        {
            if (pathSuccess)
            {
                founderMode = AIFounderMode.VendorBuy;
                lastFounderStopUpdateTicks = 6;
                if (activeScene)
                {
                    if (ManWorld.inst.TryFindNearestVendorPos(MainBase.WorldPos.GameWorldPosition, out var vendorPosWorld))
                    {
                        if (TryGetFounderActive(out teamFounderActive))
                        {
                            var helper = teamFounderActive.GetHelperInsured();
                            helper.DirectRTSDest(WorldPosition.FromGameWorldPosition(vendorPosWorld).ScenePosition);
                            helper.SetRTSState(true);
                        }
                    }
                }
            }
            else
                founderMode = AIFounderMode.HomeReturn;
        }

        // Special
        private void HandleTraderTrolls()
        {
            int count = EMUs.Count;
            for (int step = 0; step < count;)
            {
                try
                {
                    NP_TechUnit ETUcase = EMUs.ElementAt(step);
                    if ((ETUcase.tilePos - WorldPosition.FromScenePosition(Singleton.playerPos).TileCoord).WithinBox(KickStart.CullFarEnemyBasesDistance))
                    {
                        UnloadedBases.RemoteRemove(ETUcase);
                        count--;
                    }
                    else
                        step++;
                }
                catch { }
            }
        }

        // MISC
        public bool TryGetFounderUnloaded(out NP_MobileUnit ETUFounder)
        {
            ETUFounder = null;
            if (teamFounder != null && !teamFounder.IsNullOrTechMissing())
            {
                ETUFounder = teamFounder;
                return true;
            }
            else
            {
                foreach (var item in EMUs)
                {
                    if (!item.IsNullOrTechMissing())
                    {
                        TechData tech = item.tech.m_TechData;
                        if (tech.IsTeamFounder())
                        {
                            teamFounder = item;
                            ETUFounder = item;
                            break;
                        }
                    }
                }
                if (teamFounder != null && !teamFounder.IsNullOrTechMissing())
                {
                    EMUs.Remove(teamFounder);
                    return true;
                }
                return false;
            }
        }
        public bool TryGetFounderActive(out Tank founderActive)
        {
            founderActive = null;
            if (teamFounderActive != null)
            {
                founderActive = teamFounderActive;
                return true;
            }
            else
            {
                foreach (var item in ManTechs.inst.IterateTechs())
                {
                    if (item.Team == Team && item.IsTeamFounder())
                    {
                        teamFounderActive = item;
                        founderActive = item;
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
