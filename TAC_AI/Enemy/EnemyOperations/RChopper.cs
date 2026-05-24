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
    internal static class RChopper
    {
        /*  
            Circle,     // Orbit while firing the target, and randomly switch directions every now and then
            Grudge,     // Chase whoever hit this Chopper last
            Coward,     // Avoid danger, and fly high
            Bully,      // Attack other aircraft over ground structures.  If inverted, prioritize ground structures over aircraft
            Pesterer,   // Randomly switch targets on 5 second intervals
            Spyper,     // OPPOSITE!!!  Bombs the enemy from high above instead! 
        */
        public static void AttackShwa(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            BGeneral.ResetValues(helper, ref direct);
            helper.Attempt3DNavi = false;
            helper.AvoidStuff = true;

            // P13 BUG-2: lastEnemyGet.tank guaranteed non-null here — EnemyOperationsController.Execute
            // treats a null .tank as "no target" before dispatch (the old `IsNotNull()` guard only
            // validated the Visible, never its .tank).
            if (mind.CommanderMind == EnemyAttitude.Homing)
            {
                if ((helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).magnitude > mind.MaxCombatRange)
                {
                    // P13 BUG-1: choppers mend with the air idle (LollyGagAir, owned by RAircraft),
                    // not the ground LollyGag — matches the Chopper routing in DispatchNoTargetIdle.
                    bool isMending = RAircraft.LollyGagAir(helper, tank, mind, ref direct);
                    if (isMending)
                        return;
                }
            }
            // B7: null-target case centralized in EnemyOperationsController.Execute.
            RGeneral.Engadge(helper, tank, mind);

            float enemyExt = helper.lastEnemyGet.GetCheapBounds();
            //float prevDist = helper.lastOperatorRange;
            float dist = helper.GetDistanceFromTask(helper.lastDestinationCore);
            // T3: needsToSlowDown applied in Ranged + default arms only (Safety/Circle deliberately
            // skipped). RChopper additionally uses needsToSlowDown as a ladder discriminator
            // (`|| needsToSlowDown` → PivotOnly) at lines 141, 179 — chopper-specific hover-physics
            // demotion. RStarship lacks the ladder use (forced-speed lift physics).
            bool needsToSlowDown = helper.IsOrbiting();
            float range;
            float spacing = helper.lastTechExtents + enemyExt;

            switch (mind.CommanderAttack)
            {
                case EAttackMode.Safety:
                    range = AIGlobals.SpacingRangeHoverer;
                    helper.AISetSettings.ObjectiveRange = spacing + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = true;
                    direct.DriveAwayFacingAway();
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - (Vector3.down * 50));
                    if (dist < spacing + (range / 4))
                    {
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                    }
                    else if (dist < spacing + range)
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    }
                    break;
                case EAttackMode.Circle:
                    range = AIGlobals.SpacingRangeHoverer;
                    helper.AISetSettings.SideToThreat = true;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                        helper.SettleDown();
                    if (dist < spacing + 2)
                    {
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        direct.DriveToFacingTowards();
                    }
                    else if (mind.MaxCombatRange < spacing + range)
                    {
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        direct.DriveToFacingPerp();
                    }
                    else
                    {
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        direct.DriveToFacingPerp();
                    }
                    break;
                case EAttackMode.Ranged:
                    if (mind.LikelyMelee)
                    {// Bomber
                        range = AIGlobals.BomberDropZoneTolerance;
                        helper.AISetSettings.ObjectiveRange = spacing + range;
                        helper.AISetSettings.SideToThreat = false;
                        helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                        direct.DriveDest = EDriveDest.ToLastDestination;
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        if (needsToSlowDown)
                            helper.ThrottleState = AIThrottleState.Yield;
                        if (dist < spacing + 2)
                        {
                            direct.DriveAwayFacingTowards();
                        }
                        else if (dist < spacing + range)
                        {
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                            else
                                helper.SettleDown();
                        }
                        else
                        {
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                            else
                                helper.SettleDown();
                        }
                    }
                    else
                    {
                        range = AIGlobals.SpacingRangeSpyperAir;
                        helper.AISetSettings.ObjectiveRange = spacing + range;
                        helper.AISetSettings.SideToThreat = false;
                        helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                        direct.DriveDest = EDriveDest.ToLastDestination;
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        if (needsToSlowDown)
                            helper.ThrottleState = AIThrottleState.Yield;
                        if (dist < spacing + range)
                        {
                            RGeneral.MarkRetreating(helper);   // B10
                            direct.DriveAwayFacingTowards();
                        }
                        else if (dist < spacing + (range * 1.25f) || needsToSlowDown)
                        {
                            RGeneral.MarkAdvancing(helper);    // B10
                            helper.ThrottleState = AIThrottleState.PivotOnly;
                        }
                        else if (dist < spacing + (range * 1.75f))
                        {
                            RGeneral.MarkAdvancing(helper);    // B10
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                            else
                                helper.SettleDown();
                        }
                        else
                        {
                            RGeneral.MarkAdvancing(helper);    // B10
                            helper.LightBoost = true;
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                            else
                                helper.SettleDown();
                        }
                    }

                    break;
                default:    // T2: Chase/Strong/Random/AutoSet share kinematics — target-selection differentiation lives in TankAIHelper.FindEnemy
                    range = AIGlobals.SpacingRangeHoverer;
                    helper.AISetSettings.ObjectiveRange = spacing + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    if (needsToSlowDown)
                        helper.ThrottleState = AIThrottleState.Yield;
                    if (dist < spacing + 2)
                    {
                        RGeneral.MarkRetreating(helper);   // B10
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        direct.DriveAwayFacingTowards();
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                    }
                    else if (dist < spacing + range || needsToSlowDown)
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        helper.ThrottleState = AIThrottleState.PivotOnly;
                        direct.DriveDest = EDriveDest.ToLastDestination;
                    }
                    else if (dist < spacing + (range * 1.25f))
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        direct.DriveDest = EDriveDest.ToLastDestination;
                    }
                    else
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        helper.FullBoost = true;
                        direct.DriveDest = EDriveDest.ToLastDestination;
                    }
                    break;
            }
            mind.MinCombatRange = range;   // B6: publish per-tick combat range to mind for downstream weapon/range checks
        }
    }
}
