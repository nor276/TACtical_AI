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
            // T4: band-aid auto-clear removed; root cause is in the FetchChargedChargers
            // branch below (now gated on energy<0.9f) so the existing
            // `if (helper.GetEnergyPercent() > 0.9f) mind.Hurt = false;` clear can fire
            // when a tech reaches/sits-at a charger with topped-off energy.
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
                        else if (!holdGround && helper.GetEnergyPercent() < 0.9f &&
                            AIECore.FetchChargedChargers(tank, mind.MaxCombatRange, out IAIFollowable posTrans, out _, tank.Team))
                        {
                            // T4: skip charger trip if energy is already topped off — lets the
                            // clear-Hurt check below fire instead of returning early.
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
                            helper.PendingDamageCheck = !RRepair.EnemyInstaRepair(tank, mind);
                            //helper.AttemptedRepairs++;
                        }
                        else
                        {
                            bool venPower = false;
                            if (mind.MainFaction == FactionSubTypes.VEN) venPower = true;
                            helper.PendingDamageCheck = RRepair.EnemyRepairStepper(helper, tank, mind, Super: venPower);
                            //helper.AttemptedRepairs++;
                        }
                        //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is repairing");
                        return true;
                    }
                    else
                        mind.Hurt = false;
                }
            }
            else
            {
                //helper.anchorAttempts = 0;
            }

            //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is lollygagging   " + mind.CommanderMind.ToString());

            if (holdGround)
                direct.SetLastDest(mind.sceneStationaryPos);
            else
            {
                // B6: heartbeat re-aggro. Without this, idle attitudes (Default / Miner /
                // Junker / Boss / NPCBaseHost / Guardian / Part*) never call
                // TryRefreshEnemyEnemy and stay in LollyGag forever once a target is lost.
                // HomingIdle below already re-acquires each tick (skip-via-NextFindTargetTime
                // throttle keeps the cost bounded). Gate on Provoked<=0 so a tech still in
                // combat hysteresis doesn't get its target overwritten by a bystander.
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
                    case EnemyAttitude.Junker:  // Huddle up by blocks on the ground
                        RScavenger.Scavenge(helper, tank, mind, ref direct);
                        break;
                    // P13 DEAD-2: removed empty OnRails / Invader cases — both were no-op `break`s
                    // identical to default, so routing them to default is behavior-preserving.
                    case EnemyAttitude.Guardian:
                        RGuardian.MotivateDefend(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.PartTurret:
                        // Load, Aim,    FIIIIIRRRRRRRRRRRRRRRRRRRRRRRRRRRE!!!
                        BMultiTech.MimicDefend(helper, tank);
                        BMultiTech.MTStatic(helper, tank, ref direct);
                        //EMultiTech.FollowTurretBelow(helper, helper.tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, tank); //lock rigidbody with closest non-MT Tech on build beam
                        break;
                    case EnemyAttitude.PartStatic:
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

        // B3/B10: combat-bucket retreat hysteresis helpers. Set on retreat-bucket entry,
        // cleared on advance-bucket entry. Consumed by RWheeled.AttackVroom advanceEdge
        // (widens range*1.25 → range*1.4 when retreating) and by TankAIHelper.AvoidAssist
        // (suppresses ally-spacing displacement while retreating). Originally RWheeled-only;
        // B10 extends application to RChopper/RNaval/RStarship.
        internal static void MarkRetreating(TankAIHelper helper) { helper.WasRetreatingInCombat = true; }
        internal static void MarkAdvancing(TankAIHelper helper) { helper.WasRetreatingInCombat = false; }

        internal static void DefaultIdle(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            // REVIVED reroll: on expiry pick a NEW wander dest + restart (was `== 1`, which the tick steps
            // skip, so the destination never re-randomized). Retuned via the actionPause seconds shim:
            // 1000t = 2.0s wander hold; coast (drive -> None) in the final 250t = 0.5s. See docs/21.
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
        /// <summary>
        /// B7+T2+D1: centralized no-target dispatch. Called by EnemyOperationsController
        /// before its R* switch when helper.lastEnemyGet is null. Replaces 6 duplicated
        /// per-handler null branches that had drifted (RStation skipped LollyGag entirely
        /// per D1; RAircraft uses LollyGagAir; RWheeled added a re-acquire + Provoked-hold
        /// that no other handler had).
        ///
        /// Behavior:
        ///   1. Re-acquire via TryRefreshEnemyEnemy (the T2 RWheeled hardening, now universal)
        ///   2. If still no target and Provoked > 0: hold position (DriveDest = None)
        ///      — Provoked but no target means combat hysteresis; wandering off via LollyGag
        ///      would lose the engagement.
        ///   3. Else dispatch by EvilCommander:
        ///        Airplane / Chopper -> LollyGagAir (aircraft-specific idle)
        ///        Stationary         -> LollyGag(holdGround:true)  (D1 revival; the
        ///                              holdGround overload re-pins to sceneStationaryPos
        ///                              and runs Hurt-auto-clear + Smrt/IntAIligent repair
        ///                              without the drive-to-charger branch)
        ///        else               -> LollyGag (standard idle dispatch)
        /// </summary>
        internal static void DispatchNoTargetIdle(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            // T2 (universal): single re-acquire before idling — covers single-tick range/LOS
            // flicker that would otherwise drop the target.
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            if (helper.lastEnemyGet != null)
                return;   // re-acquired; caller can proceed with the R* handler
            if (helper.Provoked > 0)
            {
                // Provoked but no target — hold, don't wander.
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
                    // D1 revived: holdGround=true is the Stationary-specific path —
                    // skips drive-to-charger (gated on !holdGround), pins to sceneStationaryPos,
                    // and runs Hurt-auto-clear + Smrt/IntAIligent repair branches.
                    LollyGag(helper, tank, mind, ref direct, holdGround: true);
                    return;
                default:
                    LollyGag(helper, tank, mind, ref direct);
                    return;
            }
        }

        internal static void HomingIdle(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            // B8: null-guard arguments + narrow catch to Unity-NRE class. The previous
            // bare catch{} (comment claimed "No tanks available") swallowed every exception
            // including real NREs from TryRefreshEnemyEnemy / visible teardown races.
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
            // Determines the weapons actions and aiming of the AI - Sub-Neutral (Coward)
            //if (mind.CommanderAttack == EnemyAttack.Coward)
            //{
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            helper.WantsToFight = helper.Provoked > 0;
            //}
        }

        internal static void Monitor(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            TeamBasePointer funds = RLoadedBases.GetTeamHQ(tank.Team);
            if (funds != null)
            {
                if ((funds.WorldPos.ScenePosition - tank.boundsCentreWorldNoCheck).sqrMagnitude > AIGlobals.MaximumNeutralMonitorSqr)
                    helper.ReleaseTarget();  // B15: was bare `helper.lastEnemy = null;` — left KeepEnemyFocus stuck, next SetPursuit silently no-op'd
                else
                    helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            }
            else  // Don't stalk cause that's rude
                helper.ReleaseTarget();  // B15: same — no HQ means full release
            helper.WantsToFight = false;
            if (helper.lastEnemyGet)
            {
                // B1 revival: SetPursuit's silent no-op against a stuck KeepEnemyFocus was why
                // this was disabled. After B1+B15 the same-target re-call self-heals the lock.
                helper.SetPursuit(helper.lastEnemyGet);
                if (ManBaseTeams.IsEnemy(tank.Team, helper.lastEnemyGet.tank.Team))
                    helper.WantsToFight = true;
            }
        }

        internal static void BaseAttack(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            var lastEnemyC = helper.lastEnemy;
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            //DebugTAC_AI.Log("Base " + tank.name + " has enemy: " + helper.lastEnemy.IsNotNull() + " prev " + lastEnemyC.IsNotNull());
            
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
            //DebugTAC_AI.Log("Tech " + tank.name + " has enemy: " + helper.lastEnemy.IsNotNull() + " prev " + lastEnemyC.IsNotNull()
            //    + " | Range " + helper.MaxCombatRange);
            helper.WantsToFight = false;
            if (helper.lastEnemyGet != null)
            {
                //Fire even when retreating - the AI's life depends on this!
                // P12 BUG-2/DEAD-4: proactive fire is now driven by this WantsToFight flag, which the
                // WeaponMaintainer Enemy case consumes (FIRE_ALL || WantsToFight). The old blanket
                // "lastCombatRange < MaxRangeFireAll -> FIRE_ALL = true; return;" was removed: it set the
                // unsafe blanket FIRE_ALL the P09 pass excised everywhere, only covered AidAttack, and was
                // clobbered every tick by BGeneral.ResetValues anyway.
                helper.WantsToFight = true;
            }
        }

        internal static void AimAttack(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            // Determines the weapons actions and aiming of the AI, this one is more fire-precise and used for turrets
            var lastEnemyC = helper.lastEnemy;
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);
            //DebugTAC_AI.Log("Sniper " + tank.name + " has enemy: " + helper.lastEnemy.IsNotNull() + " prev " + lastEnemyC.IsNotNull());
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
            // Alternative of the above - does not aim at enemies while mining
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
