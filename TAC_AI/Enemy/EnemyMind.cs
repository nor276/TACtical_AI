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
        }// The way the Enemy acts if there's a threat.

        public bool SolarsAvail { get; internal set; } = false;        // Do we currently have solar panels
        public bool Hurt { get; internal set; } = false;               // Are we damaged?
        public float MaxCombatRange// Aggro range
        {
            get => AIControl.MaxCombatRange;
            set => AIControl.AISetSettings.CombatChase = value;
        }
        public float MinCombatRange
        {
            get => AIControl.MinCombatRange;
            set => AIControl.AISetSettings.CombatSpacing = value;
        }
        internal Vector3 sceneStationaryPos = Vector3.zero;  // For stationary techs like Wingnut who must hold ground

        internal bool IsPopulation => Tank.IsPopulation;
        internal bool BuildAssist = false;

        internal int BoltsQueued = 0;

        public bool AttackPlayer => CommanderAlignment == EnemyStanding.Enemy;
        //public bool AttackAny => CommanderAlignment < EnemyStanding.SubNeutral;
        public bool CanCallRetreat => ManBaseTeams.IsBaseTeamAny(Tank.Team);
        public bool CanDoRetreat => ManBaseTeams.IsBaseTeamAny(Tank.Team) || IsPopulation;

        // T3: mod-owned "just-initiated" window. Replaces fragile reliance on the vanilla
        // Tank.FirstUpdateAfterSpawn flag, which is already cleared by the time our
        // DelayedSubscribe→GenerateEnemyAI→Initiate chain runs (~0.1s after spawn).
        private float initWindowEndTime;

        public void Initiate()
        {
            //DebugTAC_AI.Log(KickStart.ModID + ": Launching Enemy AI for " + Tank.name);
            Tank = gameObject.GetComponent<Tank>();
            AIControl = gameObject.GetComponent<TankAIHelper>();
            AIControl.FinishedRepairEvent.Subscribe(OnFinishedRepairs);
            Tank.DamageEvent.Unsubscribe(AIControl.OnHit);
            Tank.DamageEvent.Subscribe(OnHit);
            Tank.AttachEvent.Subscribe(OnBlockAdd);
            Tank.DetachEvent.Subscribe(OnBlockLoss);

            AIControl.ResetAISettings();

            // T3: AttachEvent cannot fire retroactively. Sweep already-attached blocks now —
            // by the time Initiate runs, FirstUpdateAfterSpawn is typically already cleared,
            // so the OnBlockAdd handler would otherwise never see the initial roster.
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

            // T4: log+destroy extras (instead of log-only). Keep the first/oldest instance.
            // If this Refresh is on an extra, EnforceSingleComponent destroys us and the kept one
            // continues to run. The kept one still receives this Refresh call on its own subscription.
            var kept = gameObject.EnforceSingleComponent<EnemyMind>("EnemyMind.Refresh");
            if (kept != this) return;

            //DebugTAC_AI.Log(KickStart.ModID + ": Refreshing Enemy AI for " + Tank.name);
            // T3: also refresh the init window on re-evaluation (team change, mission reassign)
            // so legitimate post-Refresh block attaches still get the grace.
            initWindowEndTime = Time.time + AIGlobals.EnemyInitGrace;
            EnemyOpsController = new EnemyOperationsController(this);
            AIControl.RunState = AIRunState.Advanced;
            AIControl.MovementController.UpdateEnemyMind(this);
            AIControl.AvoidStuff = true;
            AIControl.ReleaseTarget();  // B14: was bare EndPursuit() — left stale lastEnemy for one tick. Mind re-init is an alignment boundary; match OnSwitchAI semantics.
            BoltsQueued = 0;
            try
            {
                MainFaction = TankExtentions.GetMainCorp(Tank);   //Will help determine their Attitude
            }
            catch
            {   // can't always get this 
                MainFaction = FactionSubTypes.GSO;
            }
        }
        public void SetForRemoval()
        {
            //DebugTAC_AI.Log(KickStart.ModID + ": Removing Enemy AI for " + Tank.name);
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
            // T3 + T7: replaced the FirstUpdateAfterSpawn gate (almost always false by the time
            // we run, see Initiate comment) with a mod-owned grace window. Also explicit null
            // checks replace the bare catch{} that previously hid all errors. Genuine bugs now
            // surface; transient missing-component cases are skipped cleanly.
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
            // B2/T2: trip on single big hit OR sustained sub-threshold series.
            bool tripped = dingus.Damage > AIGlobals.DamageAlertThreshold;
            Tank src = dingus.SourceTank;
            bool srcAlive = (bool)src;
            int srcTeam = dingus.SourceTeamID;        // B3: cached int — survives source death
            bool teamKnown = srcTeam != int.MaxValue;
            if (!tripped && srcAlive && AIControl.AccumulateAndCheckThreat(src, dingus.Damage))
                tripped = true;
            if (!tripped) return;

            Hurt = true;
            if (ManBaseTeams.IsSubNeutralBaseTeam(Tank.Team))
            {
                // B3: team identity comes from SourceTeamID (cached), so relations still
                // degrade against a now-dead attacker (kamikaze, missile self-destruct).
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
                AIControl.FIRE_ALL = true; // we do not want subneutrals firing all - this WILL cause collateral
            }
            // B3: pursuit + remote-fire only fire when the attacker is still alive.
            if (AIControl.Provoked <= 0 && srcAlive)
            {
                AIControl.lastEnemy = src.visible;
                GetRevengeOn(AIControl.lastEnemyGet);
                if (Tank.IsAnchored && Tank.GetComponent<RLoadedBases.EnemyBaseFunder>())
                {
                    // Execute remote orders to allied units - Attack that threat!
                    RLoadedBases.RequestFocusFireNPTs(this, AIControl.lastEnemyGet, RequestSeverity.AllHandsOnDeck);
                }
                else if (CommanderSmarts > EnemySmarts.Mild)
                {
                    // Execute remote orders to allied units - Attack that threat!
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
                {   // do NOT destroy blocks on split Techs!
                    if (!blockLoss.GetComponent<ModuleTechController>())
                    {
                        if ((bool)mind.TechMemor)
                        {   // cannot self-destruct timer cabs or death
                            if (mind.TechMemor.ChanceGrabBackBlock())
                                return;// no destroy block
                            mind.ChanceDestroyBlock(blockLoss);
                        }
                        else
                            mind.ChanceDestroyBlock(blockLoss);
                    }
                }
            }
            catch (Exception e)
            {
                // SPRINT1-B9: was silent catch{}. Block-loss bookkeeping (FIRE_ALL, Hurt,
                // PendingDamageCheck, ChanceDestroyBlock) is tick-adjacent and event-fired;
                // swallowed exceptions left enemies in a wrong combat state with no signal.
                // Per-tank key so each distinct tech surfaces its first failure.
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
                    blockLoss.damage.SelfDestruct(0.6f); // - no get illegal blocks
                }
                else
                {
                    if (UnityEngine.Random.Range(0, 100) >= KickStart.EnemyBlockDropChance /* SPRINT1-1.6: was (0,99) — Range is maxExclusive, so the previous code never hit a roll of 99 and never matched the 100% setting cleanly. */)
                    {
                        if (ManNetwork.IsNetworked)
                            ManLooseBlocks.inst.RequestDespawnBlock(blockLoss, DespawnReason.Host);
                        blockLoss.damage.SelfDestruct(0.75f);
                    }
                }
            }
            catch
            {
                if (UnityEngine.Random.Range(0, 100) >= KickStart.EnemyBlockDropChance /* SPRINT1-1.6: was (0,99) — Range is maxExclusive, so the previous code never hit a roll of 99 and never matched the 100% setting cleanly. */)
                    blockLoss.damage.SelfDestruct(0.6f);
            }
        }

        public void OnFinishedRepairs(TankAIHelper unused)
        {
            try
            {
                //DebugTAC_AI.Log(KickStart.ModID + ": OnFinishedRepair");
                if (TechMemor)
                {
                    //DebugTAC_AI.Log(KickStart.ModID + ": TechMemor");
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
                // SPRINT1-B9: was silent catch{}. Repair-finish state transitions
                // (name un-mark, AI regeneration, alignment, BuildAssist clear) are
                // event-fired and would otherwise leave the tech in a half-repaired
                // state with no visible error. Per-tank key dedups per tech.
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
                    if (CommanderAttack == EAttackMode.Safety) // no time for chickens
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
                // Don't revert to Miner/Junker if an enemy is still nearby — otherwise the next
                // LollyGag tick dispatches RMiner.MineYerOwnBusiness while the tech is still being
                // shot at, producing the "wander off mid-combat" wiggle.
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
