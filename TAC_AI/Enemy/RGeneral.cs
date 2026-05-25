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
    /// REVISED (overview): added DispatchNoTargetIdle (controller's no-target entry: rescan, hold while Provoked, else route to LollyGag/LollyGagAir).
    /// LollyGag now does an idle re-scan before the CommanderMind switch and gates charger-fetch on low energy. DefaultIdle action-pause
    /// timing reworked. HomingIdle hardened with null-guard and typed NRE catch. Monitor releases/re-pursues targets via ReleaseTarget/SetPursuit.
    public static class RGeneral
    {
        public const float RANDRange = 125;
        public static bool CanRetreat(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            if (!mind.CanDoRetreat)
                return false;
            if (!tank.IsAnchored && mind.Hurt)// && helper.lastDestination.Approximately(tank.boundsCentreWorldNoCheck, 10)
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
            if (mind.Hurt)// && helper.lastDestination.Approximately(tank.boundsCentreWorldNoCheck, 10)
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
                            {   //Try to find new spot
                                DefaultIdle(helper, tank, mind, ref direct);
                            }
                        }
                        // REVISED: charger-fetch now gated on energy < 0.9f, so a near-full tech no longer drives off to chargers
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
                        //Cannot repair block damage or recharge shields!
                        mind.Hurt = false;
                    }
                    if (tank.IsAnchored && !mind.StartedAnchored)
                    {
                        isRegenerating = true;
                    }
                }
                if (mind.CommanderSmarts == EnemySmarts.Smrt)
                {
                    if (helper.PendingDamageCheck) //&& helper.AttemptedRepairs < 3)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is repairing");
                        return true;
                    }
                    else
                        mind.Hurt = false;
                }
                if (mind.CommanderSmarts >= EnemySmarts.IntAIligent)
                {
                    if (helper.PendingDamageCheck) //&& helper.AttemptedRepairs < 4)
                    {
                        if (helper.GetEnergyPercent() > 0.5f)
                        {
                            //flex yee building speeds on them players
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
                // REVISED: idle re-scan added before the attitude switch - when targetless, unprovoked, and the scan
                // cooldown has elapsed, retry target acquisition (ScanDelay-throttled) so idle techs reacquire enemies.
                if (helper.lastEnemyGet == null && helper.Provoked <= 0
                    && helper.NextFindTargetTime <= Time.time)
                {
                    helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
                    helper.NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
                }
                switch (mind.CommanderMind)
                {
                    case EnemyAttitude.Default: // do dumb stuff
                        DefaultIdle(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Homing:  // Get nearest tech regardless of max combat range and attack them
                        HomingIdle(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Miner:   // mine resources
                        RMiner.MineYerOwnBusiness(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.NPCBaseHost: // mine resources - will run off to do missions later
                        RMiner.MineYerOwnBusiness(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Boss:        // Tidy base - will run off to do missions later
                        RScavenger.Scavenge(helper, tank, mind, ref direct);
                        break;
                    //The case below I still have to think of a reason for them to do the things
                    case EnemyAttitude.Junker:  // Huddle up by blocks on the ground
                        RScavenger.Scavenge(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Guardian:
                        RGuardian.MotivateDefend(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.PartTurret:
                        // Load, Aim,    FIIIIIRRRRRRRRRRRRRRRRRRRRRRRRRRRE!!!
                        BMultiTech.MimicDefend(helper, tank);
                        BMultiTech.MTStatic(helper, tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, tank); //lock rigidbody with closest non-MT Tech on build beam
                        break;
                    case EnemyAttitude.PartStatic:
                        // Defend and sit like good guard dog
                        BMultiTech.MimicDefend(helper, tank);
                        BMultiTech.MTStatic(helper, tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, tank); //lock rigidbody with closest non-MT Tech on build beam
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

        // REVISED: new retreat-posture flag helpers - set/clear helper.WasRetreatingInCombat for combat retreat tracking
        internal static void MarkRetreating(TankAIHelper helper) { helper.WasRetreatingInCombat = true; }
        internal static void MarkAdvancing(TankAIHelper helper) { helper.WasRetreatingInCombat = false; }

        // Handle being bored AIs
        internal static void DefaultIdle(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            // REVISED: action-pause cadence reworked - on expiry pick a new random dest and reset pause to 1000 (was 60),
            // then drive toward it while pause > 250, else stop. Old code set pause 60 / drove while pause > 15.
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
        // REVISED: new centralized no-target entry called by EnemyOperationsController before its EvilCommander switch.
        // Re-acquires via TryRefreshEnemyEnemy; bails if a target appears; holds (DriveDest=None) while still Provoked;
        // else routes by locomotion - Airplane/Chopper to LollyGagAir, Stationary to LollyGag(holdGround:true), else LollyGag.
        // Replaces the former per-R*-handler null-target guards.
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

        // REVISED: hardened - added null-guard on helper/tank/mind, the target null-check now also requires target.tank != null,
        // and the bare catch{} is now a typed NRE/MissingReference catch that logs once-per-key and falls back to DefaultIdle.
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

        /// <summary>
        /// Only is used to keep track of enemies
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        /// <param name="mind"></param>
        internal static void Scurry(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            helper.WantsToFight = helper.Provoked > 0;
        }

        /// <summary>
        /// Only is used to keep track of enemies
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        /// <param name="mind"></param>
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
            else  // Don't stalk cause that's rude
                helper.ReleaseTarget();
            helper.WantsToFight = false;
            if (helper.lastEnemyGet)
            {
                // REVISED: now formally locks pursuit via SetPursuit (was a bare lastEnemy hold); drops use ReleaseTarget()
                // above so KeepEnemyFocus is cleared rather than leaving stale focus from a raw lastEnemy = null.
                helper.SetPursuit(helper.lastEnemyGet);
                if (ManBaseTeams.IsEnemy(tank.Team, helper.lastEnemyGet.tank.Team))
                    helper.WantsToFight = true;
            }
        }

        // HOSTILITIES
        /// <summary>
        /// Base attack
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        internal static void BaseAttack(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            // Determines the weapons actions and aiming of the AI
            var lastEnemyC = helper.lastEnemy;
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);

            if (helper.lastEnemyGet != null)
            {
                helper.WantsToFight = true;
            }
            else
                helper.WantsToFight = false;

        }

        /// <summary>
        /// Attack like default
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        internal static void AidAttack(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            // Determines the weapons actions and aiming of the AI
            var lastEnemyC = helper.lastEnemy;
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            helper.WantsToFight = false;
            if (helper.lastEnemyGet != null)
            {
                //Fire even when retreating - the AI's life depends on this!
                helper.WantsToFight = true;
            }
        }

        /// <summary>
        /// Hold fire until aiming at target cab-forwards or after some time
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        internal static void AimAttack(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            // Determines the weapons actions and aiming of the AI, this one is more fire-precise and used for turrets
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

        /// <summary>
        /// Prioritize removal of obsticles over attacking enemy
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        internal static void SelfDefense(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            // Alternative of the above - does not aim at enemies while mining
            if (helper.Obst == null)
            {
                AidAttack(helper, tank, mind);
            }
            else
                helper.WantsToFight = true;
        }

        /// <summary>
        /// Stay focused on first target if the unit is order to focus-fire
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        internal static void RTSCombat(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            // Determines the weapons actions and aiming of the AI
            if (helper.lastEnemyGet != null)
            {   // focus fire like Grudge
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
