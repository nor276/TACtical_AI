using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TerraTechETCUtil;
using TAC_AI.AI.Enemy.EnemyOperations;
using TAC_AI.Templates;

namespace TAC_AI.AI.Enemy
{
    public class EnemyMind : MonoBehaviour
    {
        public Tank Tank { get; private set; }
        public TankAIHelper AIControl { get; private set; }
        internal EnemyOperationsController EnemyOpsController;
        public AIERepair.DesignMemory TechMemor => AIControl.TechMemor;

        private EnemyHandling evilCommander = EnemyHandling.Wheeled;
        public EnemyHandling EvilCommander
        {
            get => evilCommander;
            set
            {
                switch (value)
                {
                    case EnemyHandling.Chopper:
                    case EnemyHandling.Airplane:
                        AIControl.SetDriverType(AIDriverType.Pilot);
                        break;
                    case EnemyHandling.Starship:
                        AIControl.SetDriverType(AIDriverType.Astronaut);
                        break;
                    case EnemyHandling.Naval:
                        AIControl.SetDriverType(AIDriverType.Sailor);
                        break;
                    case EnemyHandling.SuicideMissile:
                        AIControl.SetDriverType(AIDriverType.Pilot);
                        break;
                    case EnemyHandling.Stationary:
                        AIControl.SetDriverType(AIDriverType.Stationary);
                        break;
                    default:
                        AIControl.SetDriverType(AIDriverType.Tank);
                        break;
                }
                evilCommander = value;
            }
        }
        public EnemyAttitude CommanderMind = EnemyAttitude.Default;
        public EAttackMode CommanderAttack
        {
            get => AIControl.AttackMode;
            set { AIControl.AttackMode = value; }
        }
        public EnemySmarts CommanderSmarts = EnemySmarts.Default;
        public EnemyBolts CommanderBolts = EnemyBolts.Default;
        public EnemyStanding CommanderAlignment = EnemyStanding.Enemy;

        public FactionSubTypes MainFaction = FactionSubTypes.GSO;
        public bool StartedAnchored = false;
        public bool AllowRepairsOnFly = false;
        public bool InvertBullyPriority = false;
        public bool AllowInvBlocks = false;
        public bool LikelyMelee
        {
            get => AIControl.FullMelee;
            set { AIControl.AISetSettings.FullMelee = value; }
        }

        public bool SolarsAvail { get; internal set; } = false;
        public bool Hurt { get; internal set; } = false;
        public float MaxCombatRange
        {
            get => AIControl.MaxCombatRange;
            set => AIControl.AISetSettings.CombatChase = value;
        }
        public float MinCombatRange
        {
            get => AIControl.MinCombatRange;
            set => AIControl.AISetSettings.CombatSpacing = value;
        }
        internal Vector3 sceneStationaryPos = Vector3.zero;

        internal bool IsPopulation => Tank.IsPopulation;
        internal bool BuildAssist = false;

        internal int BoltsQueued = 0;

        public bool AttackPlayer => CommanderAlignment == EnemyStanding.Enemy;
        public bool CanCallRetreat => ManBaseTeams.IsBaseTeamAny(Tank.Team);
        public bool CanDoRetreat => ManBaseTeams.IsBaseTeamAny(Tank.Team) || IsPopulation;

        private float initWindowEndTime;

        public void Initiate()
        {
            Tank = gameObject.GetComponent<Tank>();
            AIControl = gameObject.GetComponent<TankAIHelper>();
            AIControl.FinishedRepairEvent.Subscribe(OnFinishedRepairs);
            Tank.DamageEvent.Unsubscribe(AIControl.OnHit);
            Tank.DamageEvent.Subscribe(OnHit);
            Tank.AttachEvent.Subscribe(OnBlockAdd);
            Tank.DetachEvent.Subscribe(OnBlockLoss);

            AIControl.ResetAISettings();

            initWindowEndTime = Time.time + AIGlobals.EnemyInitGrace;
            try
            {
                foreach (TankBlock b in Tank.blockman.IterateBlocks())
                    TryAbortSelfDestruct(b);
            }
            catch (Exception e) { DebugTAC_AI.LogWarnPlayerOnce("EnemyMind.Initiate sweep failed", e); }
        }
        public void Refresh()
        {
            if (Tank == null)
                throw new NullReferenceException("Tank null");
            if (AIControl == null)
                throw new NullReferenceException("AIControl null");
            if (AIControl.MovementController == null)
                throw new NullReferenceException("AIControl.MovementController null");

            var kept = gameObject.EnforceSingleComponent<EnemyMind>("EnemyMind.Refresh");
            if (kept != this) return;

            initWindowEndTime = Time.time + AIGlobals.EnemyInitGrace;
            EnemyOpsController = new EnemyOperationsController(this);
            AIControl.RunState = AIRunState.Advanced;
            AIControl.MovementController.UpdateEnemyMind(this);
            AIControl.AvoidStuff = true;
            AIControl.ReleaseTarget();
            BoltsQueued = 0;
            try
            {
                MainFaction = TankExtentions.GetMainCorp(Tank);
            }
            catch
            {
                MainFaction = FactionSubTypes.GSO;
            }
        }
        public void SetForRemoval()
        {
            Tank.DamageEvent.Unsubscribe(OnHit);
            if (AIControl)
                Tank.DamageEvent.Subscribe(AIControl.OnHit);
            else
                DebugTAC_AI.Assert("NULL AIControl (TankAIHelper) in Tech " + Tank.name);
            Tank.AttachEvent.Unsubscribe(OnBlockAdd);
            Tank.DetachEvent.Unsubscribe(OnBlockLoss);
            AIControl.FinishedRepairEvent.Unsubscribe(OnFinishedRepairs);
            AIControl.MovementController.UpdateEnemyMind(null);
            DestroyImmediate(this);
        }

        public void OnBlockAdd(TankBlock blockAdd, Tank tonk)
        {
            if (tonk.FirstUpdateAfterSpawn || Time.time <= initWindowEndTime
                || RawTechLoader.Rebuilding)
            {
                TryAbortSelfDestruct(blockAdd);
            }
        }
        private static void TryAbortSelfDestruct(TankBlock b)
        {
            if (b == null) return;
            var dmg = b.GetComponent<Damageable>();
            if (dmg == null || dmg.Health <= 0) return;
            if (b.damage != null) b.damage.AbortSelfDestruct();
        }
        public void OnWorldMove(IntVector3 move)
        {
            sceneStationaryPos += move;
        }

        public void OnHit(ManDamage.DamageInfo dingus)
        {
            bool tripped = dingus.Damage > AIGlobals.DamageAlertThreshold;
            Tank src = dingus.SourceTank;
            bool srcAlive = (bool)src;
            int srcTeam = dingus.SourceTeamID;
            bool teamKnown = srcTeam != int.MaxValue;
            if (!tripped && srcAlive && AIControl.AccumulateAndCheckThreat(src, dingus.Damage))
                tripped = true;
            if (!tripped) return;

            Hurt = true;
            if (ManBaseTeams.IsSubNeutralBaseTeam(Tank.Team))
            {
                if (teamKnown && srcTeam == ManPlayer.inst.PlayerTeam)
                    ManBaseTeams.AttackComplainPlayer(Tank.boundsCentreWorldNoCheck, Tank.Team);
                if (teamKnown && ManBaseTeams.TryGetBaseTeamDynamicOnly(Tank.Team, out var ETD))
                {
                    ETD.DegradeRelations(srcTeam, dingus.Damage);
                    return;
                }
            }
            else
            {
                AIControl.FIRE_ALL = true;
            }
            if (AIControl.Provoked <= 0 && srcAlive)
            {
                AIControl.lastEnemy = src.visible;
                GetRevengeOn(AIControl.lastEnemyGet);
                if (Tank.IsAnchored && Tank.GetComponent<RLoadedBases.EnemyBaseFunder>())
                {
                    RLoadedBases.RequestFocusFireNPTs(this, AIControl.lastEnemyGet, RequestSeverity.AllHandsOnDeck);
                }
                else if (CommanderSmarts > EnemySmarts.Mild)
                {
                    if (ManBaseTeams.IsSubNeutralBaseTeam(Tank.Team))
                        RLoadedBases.RequestFocusFireNPTs(this, AIControl.lastEnemyGet, RequestSeverity.Warn);
                    else
                        RLoadedBases.RequestFocusFireNPTs(this, AIControl.lastEnemyGet, RequestSeverity.ThinkMcFly);
                }
            }
            AIControl.Provoked = AIGlobals.ProvokeTime;
        }
        public static void OnBlockLoss(TankBlock blockLoss, Tank tonk)
        {
            try
            {
                if (tonk.FirstUpdateAfterSpawn || RawTechLoader.Rebuilding)
                    return;
                var mind = tonk.GetComponent<EnemyMind>();
                mind.AIControl.FIRE_ALL = true;
                mind.Hurt = true;
                mind.AIControl.PendingDamageCheck = true;
                if (mind.BoltsQueued == 0 && ManNetwork.IsHost)
                {
                    if (!blockLoss.GetComponent<ModuleTechController>())
                    {
                        if ((bool)mind.TechMemor)
                        {
                            if (mind.TechMemor.ChanceGrabBackBlock())
                                return;
                            mind.ChanceDestroyBlock(blockLoss);
                        }
                        else
                            mind.ChanceDestroyBlock(blockLoss);
                    }
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnBlockLoss:" + (tonk != null ? tonk.name : "<null>"),
                    "OnBlockLoss failed; damage bookkeeping may be incomplete for this tech", e);
            }
        }
        public void ChanceDestroyBlock(TankBlock blockLoss)
        {
            try
            {
                if (ManLicenses.inst.GetLicense(ManSpawn.inst.GetCorporation(blockLoss.BlockType)).CurrentLevel < ManLicenses.inst.GetBlockTier(blockLoss.BlockType) && !KickStart.AllowOverleveledBlockDrops)
                {
                    if (ManNetwork.IsNetworked)
                        ManLooseBlocks.inst.RequestDespawnBlock(blockLoss, DespawnReason.Host);
                    blockLoss.damage.SelfDestruct(0.6f);
                }
                else
                {
                    if (UnityEngine.Random.Range(0, 100) >= KickStart.EnemyBlockDropChance )
                    {
                        if (ManNetwork.IsNetworked)
                            ManLooseBlocks.inst.RequestDespawnBlock(blockLoss, DespawnReason.Host);
                        blockLoss.damage.SelfDestruct(0.75f);
                    }
                }
            }
            catch
            {
                if (UnityEngine.Random.Range(0, 100) >= KickStart.EnemyBlockDropChance )
                    blockLoss.damage.SelfDestruct(0.6f);
            }
        }

        public void OnFinishedRepairs(TankAIHelper unused)
        {
            try
            {
                if (TechMemor)
                {
                    if (Tank.name.Contains('⟰'))
                    {
                        Tank.SetName(Tank.name.Replace(" ⟰", ""));
                        RCore.GenerateEnemyAI(AIControl, Tank);
                        AIControl.AIAlign = AIAlignment.NonPlayer;
                        DebugTAC_AI.Log(KickStart.ModID + ": (Rechecking blocks) Enemy AI " + Tank.name + " of Team " + Tank.Team + ":  Ready to kick some Tech!");
                        BuildAssist = false;
                    }
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnFinishedRepairs:" + (Tank != null ? Tank.name : "<null>"),
                    "OnFinishedRepairs failed; post-repair state may be inconsistent", e);
            }
        }

        public void GetRevengeOn(Visible target = null, bool forced = false)
        {
            if ((forced || !AIControl.KeepEnemyFocus) && (forced || CommanderAttack != EAttackMode.Safety))
            {
                if (forced)
                {
                    if (CommanderAttack == EAttackMode.Safety)
                        CommanderAttack = EAttackMode.Chase;

                    switch (CommanderMind)
                    {
                        case EnemyAttitude.Default:
                        case EnemyAttitude.Homing:
                            break;
                        default:
                            CommanderMind = EnemyAttitude.Default;
                            break;
                    }
                }
                AIControl.SetPursuit(target, forced);
            }
        }
        public void EndAggro()
        {
            if (AIControl.KeepEnemyFocus)
            {
                if (AIControl.lastEnemyGet == null || !AIControl.InRangeOfTarget(MaxCombatRange))
                {
                    if (Tank.blockman.IterateBlockComponents<ModuleItemHolderBeam>().Count() != 0)
                        CommanderMind = EnemyAttitude.Miner;
                    else if (AIECore.FetchClosestBlockReceiver(Tank.boundsCentreWorldNoCheck, MaxCombatRange, out _, out _, Tank.Team))
                        CommanderMind = EnemyAttitude.Junker;
                }
                AIControl.EndPursuit();
            }
        }

        public bool InMaxCombatRangeOfTarget()
        {
            switch (CommanderAttack)
            {
                case EAttackMode.Ranged:
                    return AIControl.InRangeOfTarget(AIGlobals.SpyperMaxCombatRange);
                default:
                    return AIControl.InRangeOfTarget(MaxCombatRange);
            }
        }

    }
}
