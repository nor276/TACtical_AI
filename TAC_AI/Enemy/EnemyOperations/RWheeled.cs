using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TerraTechETCUtil;
using TAC_AI.AI;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI.Enemy.EnemyOperations
{
    /// REVISED (overview): null-target idle case hoisted out to the controller (no longer handled here).
    /// Circle bucket reworked into a turret-fraction DUTY CYCLE: circles a fraction of the time via
    /// helper.CombatWantsCircleNow() (was the old ActionPause > 120 stop-and-shoot), plus an APPROACH
    /// GATE that drives straight at the enemy while dist > spacer + range. Ranged/default buckets gained
    /// WasRetreatingInCombat hysteresis (advanceEdge / holdEdge) to stop bucket snap-flipping.
    internal static class RWheeled
    {
        private static void MoveSideways(TankAIHelper helper, float dist, ref EControlOperatorSet direct)
        {   // Continuous circle
            // REVISED: clamp negative dist (target inside own bounds) to 0 before obstruction handling.
            if (dist < 0f) dist = 0f;
            helper.AISetSettings.SideToThreat = true;
            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend)
                || 10 < helper.FrustrationMeter)
                helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
            else
            {
                helper.SettleDown(false);
                direct.DriveToFacingPerp();
            }
        }
        public static void AttackVroom(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            BGeneral.ResetValues(helper, ref direct);
            helper.Attempt3DNavi = false;
            helper.AvoidStuff = true;


            float distToTarget = 0;
            // REVISED: the lastEnemyGet == null -> LollyGag early-return was removed; the no-target idle
            // case is now handled upstream in EnemyOperationsController before dispatch (target guaranteed live here).
            // The Homing mend below no longer needs its old lastEnemyGet null-guard for the same reason.
            if (mind.CommanderMind == EnemyAttitude.Homing)
            {
                distToTarget = (tank.boundsCentreWorldNoCheck - helper.lastEnemyGet.tank.boundsCentreWorldNoCheck).magnitude;
                if (distToTarget > mind.MaxCombatRange)
                {
                    bool isMending = RGeneral.LollyGag(helper, tank, mind, ref direct);
                    if (isMending)
                        return;
                }
            }
            RGeneral.Engadge(helper, tank, mind);

            if (distToTarget == 0)
                distToTarget = helper.GetDistanceFromTask(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);

            float enemyExt = helper.lastEnemyGet.GetCheapBounds();
            float dist = distToTarget - enemyExt;
            float range;

            float spacer = helper.lastTechExtents + enemyExt;
            if (mind.MainFaction == FactionSubTypes.GC && mind.CommanderAttack != EAttackMode.Safety)
                spacer = AIGlobals.GCRamSpacer;// ram no matter what, or get close for snipers

            switch (mind.CommanderAttack)
            {
                case EAttackMode.Safety:
                    range = AIGlobals.MinCombatRangeDefault;
                    helper.AISetSettings.ObjectiveRange = spacer + range;
                    // REVISED: all SettleDown() calls in this method now pass stopCore:false (was the default true),
                    // so the tech keeps its core spinning while settling. REVISED: MarkRetreating/MarkAdvancing
                    // set helper.WasRetreatingInCombat, the new hysteresis flag widening the Ranged/default advance edges.
                    RGeneral.MarkRetreating(helper);
                    if ((bool)helper.lastEnemyGet)
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    else
                        RGeneral.Scurry(helper, tank, mind);
                    helper.WantsToFight = true;
                    if (dist < spacer + range)
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            helper.FullBoost = true;
                        }
                    }
                    else if (dist < spacer + (range * 2))
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                        else
                            helper.SettleDown(false);
                    }
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = true;
                    direct.DriveAwayFacingAway();
                    break;
                case EAttackMode.Circle:
                    range = AIGlobals.MinCombatRangeDefault;
                    helper.AISetSettings.ObjectiveRange = spacer + range;
                    helper.AISetSettings.SideToThreat = true;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    helper.AutoSpacing = range;
                    // REVISED: Circle is an engage/approach mode, never a deliberate retreat, so clear the
                    // WasRetreatingInCombat latch here. The Circle bucket used to leave the flag untouched, which
                    // let a stale "retreating" state from a prior Safety/Ranged frame carry into the approach charge
                    // and circling - and the AvoidAssist retreat early-out then disabled obstacle avoidance the whole
                    // time (the tech rammed scenery mid-pursuit). The Ranged/default buckets still set it per-distance.
                    RGeneral.MarkAdvancing(helper);
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    // REVISED: new APPROACH GATE - while still out of range (dist > spacer + range) drive straight
                    // toward the enemy instead of circling, so the tech closes before orbiting.
                    if (dist > spacer + range)
                    {
                        helper.AISetSettings.SideToThreat = false;
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            direct.DriveToFacingTowards();
                        }
                    }
                    // REVISED: forced continuous strafe (TweakTech/WeaponAimMod) now only applies to turret-heavy techs
                    // that can actually fire while circling. A mostly-front-fixed tech (TurretFraction below the
                    // threshold) falls through to the duty cycle so it still gets a face/fire window instead of orbiting
                    // forever without firing. BlockedLineOfSight still strafes regardless (repositioning for a shot).
                    else if (helper.BlockedLineOfSight
                        || (KickStart.ShouldForceContinuousStrafe() && helper.TurretFraction >= AIGlobals.ContinuousStrafeMinTurretFraction))
                    {
                        MoveSideways(helper, dist, ref direct);
                    }
                    else
                    {
                        // REVISED: turret-fraction DUTY CYCLE - circles (DriveToFacingPerp) while CombatWantsCircleNow()
                        // is true, else faces the enemy for a firing window. Replaced the old ActionPause > 120
                        // stop-and-shoot timer; the old mind.Hurt -> randomize actionPause line is gone with it.
                        if (helper.CombatWantsCircleNow())
                        {
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / (AIGlobals.EnemyAISpeedPanicDividend * 2))
                                || 10 < helper.FrustrationMeter)
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                            else
                            {
                                helper.SettleDown(false);
                                direct.DriveToFacingPerp();
                            }
                        }
                        else
                        {
                            helper.AISetSettings.SideToThreat = false;
                            helper.SettleDown(false);
                            direct.DriveToFacingTowards();
                        }
                    }
                    break;
                case EAttackMode.Ranged:
                    range = AIGlobals.MinCombatRangeSpyper;
                    helper.AISetSettings.ObjectiveRange = spacer + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);

                    // REVISED: advanceEdge replaces the fixed range*1.25 hold boundary with hysteresis - widens to
                    // range*1.4 while WasRetreatingInCombat so the FSM doesn't snap-flip retreat<->advance frame to frame.
                    float advanceEdge = spacer + (range * (helper.WasRetreatingInCombat ? 1.4f : 1.25f));

                    if (dist < spacer + (range * 0.65f))
                    {
                        direct.DriveAwayFacingTowards();
                        helper.ThrottleState = AIThrottleState.ForceSpeed;
                        helper.DriveVar = -1;
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                        else
                            helper.SettleDown(false);
                        RGeneral.MarkRetreating(helper);
                    }
                    else if (dist < spacer + range)
                    {
                        if (helper.BlockedLineOfSight)
                            MoveSideways(helper, dist, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            direct.DriveAwayFacingTowards();
                        }
                        RGeneral.MarkRetreating(helper);
                    }
                    else if (dist < advanceEdge)
                    {
                        if (helper.BlockedLineOfSight)
                            MoveSideways(helper, dist, ref direct);
                        else
                        {
                            helper.ThrottleState = AIThrottleState.PivotOnly;
                            direct.DriveToFacingTowards();
                            helper.SettleDown(false);
                        }
                        RGeneral.MarkAdvancing(helper);
                    }
                    else if (dist < spacer + (range * 1.5f))
                    {
                        helper.ThrottleState = AIThrottleState.ForceSpeed;
                        helper.DriveVar = 1;
                        direct.DriveToFacingTowards();
                        helper.SettleDown(false);
                        RGeneral.MarkAdvancing(helper);
                    }
                    else if (dist < spacer + (range * 1.75f))
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            direct.DriveToFacingTowards();
                        }
                        RGeneral.MarkAdvancing(helper);
                    }
                    else
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            helper.FullBoost = true;
                            direct.DriveDest = EDriveDest.ToLastDestination;
                        };
                        RGeneral.MarkAdvancing(helper);
                    }
                    break;
                default:    // Others
                    range = AIGlobals.MinCombatRangeDefault;
                    helper.AISetSettings.ObjectiveRange = spacer + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    // REVISED: holdEdge adds the same WasRetreatingInCombat hysteresis to the hold boundary (range*1.25
                    // while retreating, else range*1). The advance bucket below also widened from range*1.25 to range*1.5.
                    float holdEdge = spacer + (range * (helper.WasRetreatingInCombat ? 1.25f : 1f));
                    // REVISED: reverse/stand-off band widened from "dist < spacer" (which only triggered once hulls were
                    // already overlapping) to spacer + range*0.5, so non-melee brawlers back off to a modest gap instead
                    // of piling up nose-to-nose. Melee techs still charge (DriveToFacingTowards below).
                    if (dist < spacer + (range * 0.5f))
                    {   // too close?
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend) && !mind.LikelyMelee)
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            if (mind.LikelyMelee)
                                direct.DriveToFacingTowards();
                            else
                                direct.DriveAwayFacingTowards();
                        }
                        RGeneral.MarkRetreating(helper);
                    }
                    else if (dist < holdEdge)
                    {
                        if (helper.BlockedLineOfSight)
                            MoveSideways(helper, dist, ref direct);
                        else
                        {
                            helper.ThrottleState = AIThrottleState.PivotOnly;
                            direct.DriveDest = EDriveDest.ToLastDestination;
                        }
                        RGeneral.MarkRetreating(helper);
                    }
                    else if (dist < spacer + (range * 1.5f))
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            direct.DriveToFacingTowards();
                        }
                        RGeneral.MarkAdvancing(helper);
                    }
                    else
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            helper.FullBoost = true;
                            direct.DriveToFacingTowards();
                        }
                        RGeneral.MarkAdvancing(helper);
                    }
                    break;
            }
            mind.MinCombatRange = range;
        }
    }
}
