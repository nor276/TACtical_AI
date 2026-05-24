using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TerraTechETCUtil;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Enemy.EnemyOperations;
using TAC_AI.AI.AlliedOperations;

namespace TAC_AI.AI.Enemy
{
    public static class RGeneral
    {
        public const float RANDRange = 125;
        public static bool CanRetreat(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            if (!mind.CanDoRetreat)
                return false;
            if (!tank.IsAnchored && mind.Hurt)
            {
                if (mind.CommanderSmarts >= EnemySmarts.Meh && helper.CanStoreEnergy() &&
                    helper.GetEnergyPercent() < AIGlobals.BatteryRetreatPercent)
                {
                    if (mind.SolarsAvail && !Singleton.Manager<ManTimeOfDay>.inst.NightTime)
                        return true;
                    else if (AIECore.ChargedChargerExists(tank, mind.MaxCombatRange, tank.Team))
                        return true;
                }
                if (mind.CommanderSmarts == EnemySmarts.Smrt &&
                    helper.DamageThreshold < AIGlobals.RetreatBelowTechDamageThreshold)
                {
                    return true;
                }
            }
            return AIECore.RetreatingTeams.Contains(tank.Team);
        }

        internal static bool LollyGag(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct, bool holdGround = false)
        {
            bool isRegenerating = false;
            if (mind.Hurt)
            {
                if (mind.CommanderSmarts >= EnemySmarts.Meh)
                {
                    if (helper.CanStoreEnergy())
                    {
                        if (mind.SolarsAvail && !Singleton.Manager<ManTimeOfDay>.inst.NightTime && tank.Anchors.NumPossibleAnchors > 0 && !tank.IsAnchored)
                        {
                            if (helper.CanAttemptAnchor)
                            {
                                helper.TryInsureAutoAnchor();
                            }
                            else
                            {
                                DefaultIdle(helper, tank, mind, ref direct);
                            }
                        }
                        else if (!holdGround && helper.GetEnergyPercent() < 0.9f &&
                            AIECore.FetchChargedChargers(tank, mind.MaxCombatRange, out IAIFollowable posTrans, out _, tank.Team))
                        {
                            direct.SetLastDest(posTrans.position);
                            return true;
                        }
                        if (helper.GetEnergyPercent() > 0.9f)
                        {
                            mind.Hurt = false;
                        }
                    }
                    else
                    {
                        mind.Hurt = false;
                    }
                    if (tank.IsAnchored && !mind.StartedAnchored)
                    {
                        isRegenerating = true;
                    }
                }
                if (mind.CommanderSmarts == EnemySmarts.Smrt)
                {
                    if (helper.PendingDamageCheck)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is repairing");
                        return true;
                    }
                    else
                        mind.Hurt = false;
                }
                if (mind.CommanderSmarts >= EnemySmarts.IntAIligent)
                {
                    if (helper.PendingDamageCheck)
                    {
                        if (helper.GetEnergyPercent() > 0.5f)
                        {
                            helper.PendingDamageCheck = !RRepair.EnemyInstaRepair(tank, mind);
                        }
                        else
                        {
                            bool venPower = false;
                            if (mind.MainFaction == FactionSubTypes.VEN) venPower = true;
                            helper.PendingDamageCheck = RRepair.EnemyRepairStepper(helper, tank, mind, Super: venPower);
                        }
                        return true;
                    }
                    else
                        mind.Hurt = false;
                }
            }
            else
            {
            }


            if (holdGround)
                direct.SetLastDest(mind.sceneStationaryPos);
            else
            {
                if (helper.lastEnemyGet == null && helper.Provoked <= 0
                    && helper.NextFindTargetTime <= Time.time)
                {
                    helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
                    helper.NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
                }
                switch (mind.CommanderMind)
                {
                    case EnemyAttitude.Default:
                        DefaultIdle(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Homing:
                        HomingIdle(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Miner:
                        RMiner.MineYerOwnBusiness(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.NPCBaseHost:
                        RMiner.MineYerOwnBusiness(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Boss:
                        RScavenger.Scavenge(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Junker:
                        RScavenger.Scavenge(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Guardian:
                        RGuardian.MotivateDefend(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.PartTurret:
                        BMultiTech.MimicDefend(helper, tank);
                        BMultiTech.MTStatic(helper, tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, tank);
                        break;
                    case EnemyAttitude.PartStatic:
                        BMultiTech.MimicDefend(helper, tank);
                        BMultiTech.MTStatic(helper, tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, tank);
                        break;
                    case EnemyAttitude.PartMimic:
                        BMultiTech.MimicAllClosestAlly(helper, tank, ref direct);
                        break;
                    default:
                        break;
                }
            }

            return isRegenerating;
        }
        internal static void Engadge(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            if (!mind.StartedAnchored && tank.IsAnchored)
            {
                helper.Unanchor();
            }
        }

        internal static void MarkRetreating(TankAIHelper helper) { helper.WasRetreatingInCombat = true; }
        internal static void MarkAdvancing(TankAIHelper helper) { helper.WasRetreatingInCombat = false; }

        internal static void DefaultIdle(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            if (helper.ActionPause <= 0)
            {
                direct.SetLastDest(GetRANDPos(tank));
                helper.actionPause = 1000;
            }
            if (helper.ActionPause > 250)
                direct.DriveDest = EDriveDest.ToLastDestination;
            else
                direct.DriveDest = EDriveDest.None;
        }
        internal static void DispatchNoTargetIdle(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            if (helper.lastEnemyGet != null)
                return;
            if (helper.Provoked > 0)
            {
                direct.DriveDest = EDriveDest.None;
                return;
            }
            switch (mind.EvilCommander)
            {
                case EnemyHandling.Airplane:
                case EnemyHandling.Chopper:
                    RAircraft.LollyGagAir(helper, tank, mind, ref direct);
                    return;
                case EnemyHandling.Stationary:
                    LollyGag(helper, tank, mind, ref direct, holdGround: true);
                    return;
                default:
                    LollyGag(helper, tank, mind, ref direct);
                    return;
            }
        }

        internal static void HomingIdle(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            if (helper == null || tank == null || mind == null)
            {
                DebugTAC_AI.LogError("HomingIdle called with null helper/tank/mind; skipping");
                return;
            }
            try
            {
                var target = helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
                if (target && target.tank != null)
                    direct.SetLastDest(target.tank.boundsCentreWorldNoCheck);
                else
                    DefaultIdle(helper, tank, mind, ref direct);
            }
            catch (Exception e) when (e is NullReferenceException || e is MissingReferenceException)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "HomingIdle:" + tank.name,
                    "HomingIdle NRE on " + tank.name + " - target/visible state went null mid-frame", e);
                DefaultIdle(helper, tank, mind, ref direct);
            }
        }
        internal static Vector3 GetRANDPos(Tank tank)
        {
            Vector3 final = tank.boundsCentreWorldNoCheck;

            final.x += UnityEngine.Random.Range(-RANDRange, RANDRange);
            final.y += UnityEngine.Random.Range(-RANDRange, RANDRange);
            final.z += UnityEngine.Random.Range(-RANDRange, RANDRange);

            return final;
        }

        internal static void Scurry(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            helper.WantsToFight = helper.Provoked > 0;
        }

        internal static void Monitor(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            TeamBasePointer funds = RLoadedBases.GetTeamHQ(tank.Team);
            if (funds != null)
            {
                if ((funds.WorldPos.ScenePosition - tank.boundsCentreWorldNoCheck).sqrMagnitude > AIGlobals.MaximumNeutralMonitorSqr)
                    helper.ReleaseTarget();
                else
                    helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            }
            else
                helper.ReleaseTarget();
            helper.WantsToFight = false;
            if (helper.lastEnemyGet)
            {
                helper.SetPursuit(helper.lastEnemyGet);
                if (ManBaseTeams.IsEnemy(tank.Team, helper.lastEnemyGet.tank.Team))
                    helper.WantsToFight = true;
            }
        }

        internal static void BaseAttack(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            var lastEnemyC = helper.lastEnemy;
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);

            if (helper.lastEnemyGet != null)
            {
                helper.WantsToFight = true;
            }
            else
                helper.WantsToFight = false;

        }

        internal static void AidAttack(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            var lastEnemyC = helper.lastEnemy;
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            helper.WantsToFight = false;
            if (helper.lastEnemyGet != null)
            {
                helper.WantsToFight = true;
            }
        }

        internal static void AimAttack(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            var lastEnemyC = helper.lastEnemy;
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            if (helper.lastEnemyGet != null)
            {
                Vector3 aimTo = (helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).normalized;
                helper.WeaponDelayClock += KickStart.AIClockPeriod;
                if (helper.Attempt3DNavi)
                {
                    if (helper.SideToThreat)
                    {
                        float dot = Vector3.Dot(tank.rootBlockTrans.right, aimTo);
                        if (dot > 0.45f || dot < -0.45f || helper.WeaponDelayClock >= 150)
                        {
                            helper.WantsToFight = true;
                            helper.WeaponDelayClock = 150;
                        }
                    }
                    else
                    {
                        if (Vector3.Dot(tank.rootBlockTrans.forward, aimTo) > 0.45f || helper.WeaponDelayClock >= 150)
                        {
                            helper.WantsToFight = true;
                            helper.WeaponDelayClock = 150;
                        }
                    }
                }
                else
                {
                    if (helper.SideToThreat)
                    {
                        float dot = Vector2.Dot(tank.rootBlockTrans.right.ToVector2XZ(), aimTo.ToVector2XZ());
                        if (dot > 0.45f || dot < -0.45f || helper.WeaponDelayClock >= 150)
                        {
                            helper.WantsToFight = true;
                            helper.WeaponDelayClock = 150;
                        }
                    }
                    else
                    {
                        if (Vector2.Dot(tank.rootBlockTrans.forward.ToVector2XZ(), aimTo.ToVector2XZ()) > 0.45f || helper.WeaponDelayClock >= 150)
                        {
                            helper.WantsToFight = true;
                            helper.WeaponDelayClock = 150;
                        }
                    }
                }
            }
            else
            {
                helper.WeaponDelayClock = 0;
                helper.WantsToFight = false;
            }
        }

        internal static void SelfDefense(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            if (helper.Obst == null)
            {
                AidAttack(helper, tank, mind);
            }
            else
                helper.WantsToFight = true;
        }

        internal static void RTSCombat(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            if (helper.lastEnemyGet != null)
            {
                helper.WantsToFight = true;
                if (!helper.lastEnemyGet.isActive)
                    helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            }
            else
            {
                helper.WantsToFight = false;
                helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            }
        }

    }
}
