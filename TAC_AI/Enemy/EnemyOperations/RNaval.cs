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
    internal static class RNaval
    {
        public static void AttackWhish(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            //The Handler that tells the Tank (Escort) what to do movement-wise
            BGeneral.ResetValues(helper, ref direct);
            helper.Attempt3DNavi = true;
            helper.AvoidStuff = true;

            // P13 BUG-2: lastEnemyGet.tank guaranteed non-null here — the centralized null-target
            // guard in EnemyOperationsController now rejects a null .tank too, not just the Visible.
            if (mind.CommanderMind == EnemyAttitude.Homing)
            {
                if ((helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).magnitude > mind.MaxCombatRange)
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
            float range = AIGlobals.MinCombatRangeDefault + helper.lastTechExtents;
            float spacer = helper.lastTechExtents + enemyExt;

            if (mind.MainFaction == FactionSubTypes.GC && mind.CommanderAttack != EAttackMode.Safety)
                spacer = AIGlobals.GCRamSpacer; // ram no matter what, or get close for snipers

            direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
            // B9: parallel-write ObjectiveRange for every arm (matches RWheeled/RChopper/RStarship per-bucket writebacks)
            helper.AISetSettings.ObjectiveRange = spacer + range;
            if (mind.CommanderAttack == EAttackMode.Safety)
            {
                helper.AISetSettings.SideToThreat = false;
                helper.Retreat = true;
                direct.DriveAwayFacingAway();
                if (dist < spacer + (range / 4))
                {
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                    {
                        helper.SettleDown();
                        helper.FullBoost = true;
                    }
                }
                else if (dist < spacer + range)
                {
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                        helper.SettleDown();
                }
            }
            else if (mind.CommanderAttack == EAttackMode.Circle)
            {
                helper.AISetSettings.SideToThreat = true;
                helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                    helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                else
                {
                    helper.SettleDown();
                    if (dist < helper.lastTechExtents + enemyExt + 2)
                    {
                        direct.DriveAwayFacingPerp();
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    }
                    else if (mind.MaxCombatRange < spacer + range)
                    {
                        direct.DriveToFacingPerp();
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    }
                    else
                    {
                        direct.DriveToFacingPerp();
                        helper.FullBoost = true;
                    }
                }
            }
            else if (mind.CommanderAttack == EAttackMode.Ranged)
            {
                helper.AISetSettings.SideToThreat = true;
                helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                if (dist < spacer + (range / 2))
                {
                    RGeneral.MarkRetreating(helper);   // B10
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                    {
                        helper.SettleDown();
                        direct.DriveAwayFacingTowards();
                    }
                }
                else if (dist < spacer + range)
                {
                    RGeneral.MarkAdvancing(helper);    // B10
                    helper.ThrottleState = AIThrottleState.PivotOnly;
                }
                else if (dist < helper.lastTechExtents + enemyExt + (range * 2))
                {
                    RGeneral.MarkAdvancing(helper);    // B10
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                    {
                        helper.SettleDown();
                        direct.DriveToFacingPerp();
                    }
                }
                else
                {
                    RGeneral.MarkAdvancing(helper);    // B10
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                    {
                        helper.SettleDown();
                        helper.FullBoost = true;
                        direct.DriveToFacingPerp();
                    }
                }
            }
            else    // T2: Chase/Strong/Random/AutoSet share kinematics — target-selection differentiation lives in TankAIHelper.FindEnemy
            {
                helper.AISetSettings.SideToThreat = false;
                helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                if (dist < spacer + 2)
                {
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                    {
                        direct.DriveAwayFacingTowards();
                        helper.SettleDown();
                    }
                }
                else if (dist < spacer + range)
                {
                    helper.ThrottleState = AIThrottleState.PivotOnly;
                    direct.DriveToFacingPerp();
                }
                else if (dist < spacer + (range * 1.25f))
                {
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                    {
                        helper.SettleDown();
                        direct.DriveToFacingPerp();
                    }
                }
                else
                {
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                        helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                    else
                    {
                        helper.SettleDown();
                        helper.FullBoost = true;
                        direct.DriveToFacingPerp();
                    }
                }
            }
            // B6: publish per-tick combat range to mind. RNaval's `range` (line 35) bakes
            // `helper.lastTechExtents` in; subtract it on writeback so the stored value
            // matches the RWheeled/RStarship unit-contract that downstream consumers
            // (AICore driveDyna, GUI slider, P12 weapon range) expect.
            mind.MinCombatRange = range - helper.lastTechExtents;
        }
    }
}
