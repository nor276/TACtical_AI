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
    internal static class RStarship
    {
        //Same as RWheeled but has multi-plane support
        public static void AttackZoom(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            //The Handler that tells the Tank (Escort) what to do movement-wise
            BGeneral.ResetValues(helper, ref direct);
            helper.Attempt3DNavi = true;
            helper.AvoidStuff = true;

            // P13 BUG-2: lastEnemyGet.tank guaranteed non-null here — the centralized null-target
            // guard in EnemyOperationsController now rejects a null .tank too, not just the Visible.
            if (mind.CommanderMind == EnemyAttitude.Homing)
            {
                if ((helper.lastEnemyGet.tank.boundsCentreWorld - tank.boundsCentreWorld).magnitude > mind.MaxCombatRange)
                {
                    bool isMending = RGeneral.LollyGag(helper, tank, mind, ref direct);
                    if (isMending)
                        return;
                }
            }
            // B7: null-target case centralized in EnemyOperationsController.Execute.
            RGeneral.Engadge(helper, tank, mind);

            float enemyExt = helper.lastEnemyGet.GetCheapBounds();
            float dist = helper.GetDistanceFromTask(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck, enemyExt);
            // T3: needsToSlowDown applied in Ranged + default arms only (Safety/Circle skipped).
            // Pre-switch `ThrottleState = ForceSpeed` at line 40 enforces forward lift physics
            // for starships; applying Yield in Safety/Circle would freeze a fleeing/orbiting
            // starship (stall). RChopper applies the same arms but adds a ladder-demotion use
            // appropriate for hover physics.
            bool needsToSlowDown = helper.IsOrbiting();
            float range;
            float spacing = helper.lastTechExtents + enemyExt;

            helper.ThrottleState = AIThrottleState.ForceSpeed;
            helper.DriveVar = 1;
            switch (mind.CommanderAttack)
            {
                case EAttackMode.Safety:
                    range = AIGlobals.SpacingRangeHoverer;
                    helper.AISetSettings.ObjectiveRange = spacing + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = true;
                    direct.DriveAwayFacingTowards();
                    if (dist < spacing + range)
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        helper.FullBoost = true;
                    }
                    else if (dist < spacing + (range*2))
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                    }
                    break;
                case EAttackMode.Circle:
                    range = AIGlobals.SpacingRangeHoverer;
                    helper.AISetSettings.ObjectiveRange = spacing + range;
                    // Circle intentionally `SideToThreat = false` — cannot strafe while firing shells, will miss (artillery alignment).
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    helper.AutoSpacing = range;
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                        helper.SettleDown();
                    // Melee makes the AI ignore the avoid, making the AI ram into the enemy
                    if (!mind.LikelyMelee && dist < spacing + 2)
                    {
                        direct.DriveAwayFacingPerp();
                    }
                    else if (mind.MaxCombatRange < spacing + range)
                    {
                        direct.DriveToFacingPerp();
                    }
                    else
                    {
                        helper.FullBoost = true;
                        direct.DriveToFacingPerp();
                    }
                    break;
                case EAttackMode.Ranged:// Spyper does not support melee
                    range = AIGlobals.SpacingRangeSpyperAir;
                    helper.AISetSettings.ObjectiveRange = spacing + range;
                    helper.AISetSettings.SideToThreat = false; // cannot strafe while firing shells it seems, will miss
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    if (needsToSlowDown)
                        helper.ThrottleState = AIThrottleState.Yield;
                    if (dist < spacing + range)
                    {
                        RGeneral.MarkRetreating(helper);   // B10
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        direct.DriveAwayFacingTowards();
                        helper.ThrottleState = AIThrottleState.ForceSpeed;
                        helper.DriveVar = 1;
                    }
                    else if (dist < spacing + (range * 1.5f))
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        helper.ThrottleState = AIThrottleState.PivotOnly;
                        direct.DriveAwayFacingTowards();
                    }
                    else if (dist < spacing + (range * 2))
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        direct.DriveToFacingTowards();
                    }
                    else
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        helper.FullBoost = true;
                        direct.DriveToFacingTowards();
                    }
                    break;
                default:    // T2: Chase/Strong/Random/AutoSet share kinematics — target-selection differentiation lives in TankAIHelper.FindEnemy
                    range = AIGlobals.SpacingRangeHoverer;
                    helper.AISetSettings.ObjectiveRange = spacing + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    if (needsToSlowDown)
                        helper.ThrottleState = AIThrottleState.Yield;
                    if (!mind.LikelyMelee && dist < spacing + 2)
                    {
                        RGeneral.MarkRetreating(helper);   // B10
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        direct.DriveAwayFacingTowards();

                    }
                    else if (!mind.LikelyMelee && dist < spacing + range)
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        helper.ThrottleState = AIThrottleState.PivotOnly;
                        direct.DriveToFacingTowards();

                    }
                    else if (dist < spacing + (range * 1.25f))
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        direct.DriveToFacingTowards();
                    }
                    else
                    {
                        RGeneral.MarkAdvancing(helper);    // B10
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                        helper.FullBoost = true;
                        direct.DriveToFacingTowards();
                    }
                    break;
            }
            mind.MinCombatRange = range;
        }
    }
}
