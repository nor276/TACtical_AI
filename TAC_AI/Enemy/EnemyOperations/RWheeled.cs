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
        {
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
                spacer = AIGlobals.GCRamSpacer;

            switch (mind.CommanderAttack)
            {
                case EAttackMode.Safety:
                    range = AIGlobals.MinCombatRangeDefault;
                    helper.AISetSettings.ObjectiveRange = spacer + range;
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
                    else if (helper.BlockedLineOfSight || KickStart.ShouldForceContinuousStrafe())
                    {
                        MoveSideways(helper, dist, ref direct);
                    }
                    else
                    {
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
                default:
                    range = AIGlobals.MinCombatRangeDefault;
                    helper.AISetSettings.ObjectiveRange = spacer + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    float holdEdge = spacer + (range * (helper.WasRetreatingInCombat ? 1.25f : 1f));
                    if (dist < spacer)
                    {
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
