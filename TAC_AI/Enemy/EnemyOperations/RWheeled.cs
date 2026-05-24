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
    internal static class RWheeled
    {
        private static void MoveSideways(TankAIHelper helper, float dist, ref EControlOperatorSet direct)
        {   // Continuous circle
            // B4: clamp negative dist (can occur when GC ram override at AttackVroom:60 sets
            // spacer = AIGlobals.GCRamSpacer = -32 then dist = distToTarget - enemyExt drifts
            // below zero on tech-inside-enemy overlap). TryHandleObstruction only compares
            // dist > MaxObjectiveRange*2 (no division), so negative is harmless today, but
            // the clamp is defense-in-depth for future maintainers.
            if (dist < 0f) dist = 0f;
            helper.AISetSettings.SideToThreat = true;
            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend)
                || 10 < helper.FrustrationMeter)
                helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
            else
            {
                helper.SettleDown(false);
                direct.DriveToFacingPerp();
                // Orbit radius enforced by AutoSpacing + ObjectiveRange in caller; do not
                // re-add per-frame distance buckets here.
            }
        }
        public static void AttackVroom(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            BGeneral.ResetValues(helper, ref direct);
            helper.Attempt3DNavi = false;
            helper.AvoidStuff = true;

            //DebugTAC_AI.Log("RWheeled.TryAttack - " + tank.name);

            float distToTarget = 0;
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
            // B7: null-target case now handled by EnemyOperationsController.Execute via
            // RGeneral.DispatchNoTargetIdle before this method runs. P13 BUG-2: that guard now
            // also rejects a null lastEnemyGet.tank, so both lastEnemyGet and .tank are guaranteed
            // non-null here (the old Homing `IsNotNull()` only checked the Visible, not .tank).
            RGeneral.Engadge(helper, tank, mind);

            if (distToTarget == 0)
                distToTarget = helper.GetDistanceFromTask(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);

            float enemyExt = helper.lastEnemyGet.GetCheapBounds();
            float dist = distToTarget - enemyExt;
            float range;

            float spacer = helper.lastTechExtents + enemyExt;
            if (mind.MainFaction == FactionSubTypes.GC && mind.CommanderAttack != EAttackMode.Safety)
                spacer = AIGlobals.GCRamSpacer; // ram no matter what, or get close for snipers

            switch (mind.CommanderAttack)
            {
                case EAttackMode.Safety:
                    range = AIGlobals.MinCombatRangeDefault;
                    helper.AISetSettings.ObjectiveRange = spacer + range;
                    // B3: Safety is always-retreat; mark hysteresis so a Safety→Ranged transition
                    // at close range enters Ranged with advanceEdge = range*1.4 (not 1.25),
                    // preventing the very edge-jitter the flag was added to fix.
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
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    if (dist > spacer + range)
                    {   // APPROACH: still closing the gap — drive STRAIGHT at the enemy rather than
                        // strafing/weaving in. The circle/face engagement only starts once at stand-off,
                        // so a Circle tech beelines to its target then engages "when there".
                        helper.AISetSettings.SideToThreat = false;
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                        else
                        {
                            helper.SettleDown(false);
                            direct.DriveToFacingTowards();
                        }
                    }
                    else if (helper.BlockedLineOfSight || KickStart.ShouldForceContinuousStrafe())
                    {   // In range, no clear shot (or forced): continuous circle to find an angle
                        MoveSideways(helper, dist, ref direct);
                    }
                    else
                    {   // In range: turret-fraction duty cycle — circle (broadside) for ~TurretFraction of the
                        // time so wide-gimbal turrets keep their arcs, then face so front-fixed weapons fire.
                        if (helper.CombatWantsCircleNow())
                        {   // Circle phase
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / (AIGlobals.EnemyAISpeedPanicDividend * 2))
                                || 10 < helper.FrustrationMeter)   // D3 revive: parity with MoveSideways:18 escalation
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, false, true, ref direct);
                            else
                            {
                                helper.SettleDown(false);
                                direct.DriveToFacingPerp();
                            }
                        }
                        else
                        {   // Face phase - put the front on target for fixed forward weapons
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

                    // Hysteresis: if we were retreating last tick, require an extra 15% margin
                    // before flipping to the advance/pivot bucket. Otherwise the tech oscillates
                    // across the spacer+range edge each frame, producing visible wiggle.
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
                            direct.DriveToFacingTowards(); // point at the objective
                            helper.SettleDown(false);
                        }
                        RGeneral.MarkAdvancing(helper);
                    }
                    else if (dist < spacer + (range * 1.5f))
                    {
                        helper.ThrottleState = AIThrottleState.ForceSpeed;
                        helper.DriveVar = 1;
                        direct.DriveToFacingTowards(); // point at the objective
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
                default:    // T2: Chase/Strong/Random/AutoSet share kinematics — target-selection differentiation lives in TankAIHelper.FindEnemy
                    range = AIGlobals.MinCombatRangeDefault;
                    helper.AISetSettings.ObjectiveRange = spacer + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    // Hysteresis (mirrors the Ranged arm's advanceEdge above): once holding or
                    // closing, require an extra 25% range margin before flipping to the advance
                    // bucket. Without it a tech parked near the spacer+range edge oscillates
                    // hold<->advance every Operations tick - the close-range "twitch".
                    float holdEdge = spacer + (range * (helper.WasRetreatingInCombat ? 1.25f : 1f));
                    if (dist < spacer)
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
                    {   // hold position and pivot to aim
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
