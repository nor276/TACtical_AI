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
        //Same as RWheeled but has terrain avoidence
        public static void AttackWhish(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            //The Handler that tells the Tank (Naval) what to do movement-wise. REVISED: "(Escort)" label was a copy-paste from the shared handler template.
            // REVISED (overview): null-target idle case hoisted out to the controller (no longer handled here).
            // ObjectiveRange = spacer + range is now written once before the if/else chain (was not set at all),
            // bucket transitions mark advance/retreat hysteresis, and a mind.MinCombatRange writeback was added at the end.
            BGeneral.ResetValues(helper, ref direct);
            helper.Attempt3DNavi = true;
            helper.AvoidStuff = true;

            // REVISED: the lastEnemyGet == null -> LollyGag early-return was removed; no-target idle is now handled
            // upstream in the controller (target guaranteed live here), so the Homing null-guard is gone too.
            if (mind.CommanderMind == EnemyAttitude.Homing)
            {
                if ((helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).magnitude > mind.MaxCombatRange)
                {
                    bool isMending = RGeneral.LollyGag(helper, tank, mind, ref direct);
                    if (isMending)
                        return;
                }
            }
            RGeneral.Engadge(helper, tank, mind);

            float enemyExt = helper.lastEnemyGet.GetCheapBounds();
            float dist = helper.GetDistanceFromTask(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck, enemyExt);
            float range = AIGlobals.MinCombatRangeDefault + helper.lastTechExtents;
            float spacer = helper.lastTechExtents + enemyExt;

            if (mind.MainFaction == FactionSubTypes.GC && mind.CommanderAttack != EAttackMode.Safety)
                spacer = AIGlobals.GCRamSpacer;

            direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
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
                    RGeneral.MarkRetreating(helper);
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
                    RGeneral.MarkAdvancing(helper);
                    helper.ThrottleState = AIThrottleState.PivotOnly;
                }
                else if (dist < helper.lastTechExtents + enemyExt + (range * 2))
                {
                    RGeneral.MarkAdvancing(helper);
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
                    RGeneral.MarkAdvancing(helper);
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
            else
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
            // REVISED: writes the chosen range back to the mind for downstream weapon checks (was not written before);
            // subtracts lastTechExtents because naval range was inflated by it above.
            mind.MinCombatRange = range - helper.lastTechExtents;
        }
    }
}
