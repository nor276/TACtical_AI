using UnityEngine;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;

namespace TAC_AI.AI.AlliedOperations
{
    internal static class BAssassin
    {
        internal const int reverseFromResourceTime = 35;
        public static void MotivateKill(TankAIHelper helper, Tank tank, ref EControlOperatorSet direct)
        {
            //The Handler that tells the Tank (Assassin) what to do movement-wise
            helper.IsMultiTech = false;
            helper.Attempt3DNavi = (helper.DriverType == AIDriverType.Pilot || helper.DriverType == AIDriverType.Astronaut);

            float dist = (tank.boundsCentreWorldNoCheck - helper.lastDestinationCore).magnitude;
            bool hasMessaged = false;
            helper.SetDistanceFromTaskUnneeded();
            helper.AvoidStuff = true;

            BGeneral.ResetValues(helper, ref direct);

            TechEnergy.EnergyState state = tank.EnergyRegulator.Energy(TechEnergy.EnergyType.Electric);
            if (helper.CollectedTarget)
            {
                if ((state.storageTotal - state.spareCapacity) / state.storageTotal < 0.4f)
                {
                    helper.CollectedTarget = false;
                    helper.actionPause = reverseFromResourceTime;
                }
            }
            else
            {
                if ((state.storageTotal - state.spareCapacity) / state.storageTotal > 0.95f)
                {
                    helper.CollectedTarget = true;
                    helper.actionPause = AIGlobals.ReverseDelay;
                }
            }

            if (!helper.CollectedTarget || helper.Retreat)
            {
                if (helper.ActionPause > 0)
                {   // BRANCH - Reverse from Resources
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Reversing from resources...");
                    direct.Reverse(helper);
                    // REVISED: no longer decrements actionPause on arrival/reverse states (same removal repeated throughout this method)
                    {  }
                    return;
                }
                helper.foundBase = AIECore.FetchChargedChargers(tank, helper.JobSearchRange * 2.5f, out helper.lastBasePos, out helper.theBase, tank.Team);
                if (!helper.foundBase)
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Searching for nearest charger!");
                    helper.EstTopSped = 1;//slow down the clock to reduce lagg
                    if (helper.theBase == null)
                        return; // There's no base!
                    helper.lastBaseExtremes = helper.theBase.GetCheapBounds();
                }
                helper.ThrottleState = AIThrottleState.ForceSpeed;
                helper.DriveVar = 1;

                if (dist < helper.lastBaseExtremes + helper.lastTechExtents + 3)
                {
                    helper.theBase.GetHelperInsured().SlowForApproacher(helper);
                    if (helper.recentSpeed == 1)
                    {
                        hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Trying to unjam...");
                        helper.AvoidStuff = false;
                        helper.DriveVar = -1;
                    }
                    else
                    {
                        hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Arrived at nearest charger and recharging!");
                        helper.AvoidStuff = false;
                        {  }
                        helper.ThrottleState = AIThrottleState.Yield;
                        helper.SettleDown();
                    }
                }
                else if (dist < helper.lastBaseExtremes + helper.lastTechExtents + 8)
                {
                    helper.theBase.GetHelperInsured().SlowForApproacher(helper);
                    if (helper.recentSpeed < 3)
                    {
                        hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Trying to unjam...");
                        helper.AvoidStuff = false;
                        helper.TryHandleObstruction(hasMessaged, dist, false, false, ref direct);
                    }
                    else if (helper.recentSpeed < 8)
                    {
                        hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Rattling off resources...");
                        helper.AvoidStuff = false;
                        helper.ThrottleState = AIThrottleState.Yield;
                    }
                    else
                    {
                        hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Yielding base approach...");
                        helper.AvoidStuff = false;
                        helper.ThrottleState = AIThrottleState.Yield;
                        helper.SettleDown();
                    }
                }
                else if (dist < helper.lastBaseExtremes + helper.lastTechExtents + 12)
                {
                    helper.theBase.GetHelperInsured().SlowForApproacher(helper);
                    if (helper.recentSpeed < 3)
                    {
                        hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  unjamming from base...");
                        helper.TryHandleObstruction(hasMessaged, dist, false, true, ref direct);
                    }
                    else
                    {
                        hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Arrived at base!");
                        {  }
                        helper.SettleDown();
                    }
                }
                else if (helper.recentSpeed < 3)
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Removing obstruction on way to base...");
                    helper.TryHandleObstruction(hasMessaged, dist, false, true, ref direct);
                }
                AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Heading back to base!");
                direct.DriveDest = EDriveDest.ToBase;
                helper.foundGoal = false;
            }
            else
            {
                if (helper.ActionPause > 0)
                {   // BRANCH - Reverse from Base
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Reversing from base...");
                    direct.Reverse(helper);
                    {  }
                    return;
                }
                if (!helper.foundGoal)
                {
                    helper.EstTopSped = 1;//slow down the clock to reduce lagg
                    helper.foundGoal = AIECore.FindTarget(tank, helper, helper.theResource, out var tmpRes);
                    helper.theResource = tmpRes;
                    AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Scanning for enemies...");
                    if (!helper.foundGoal)
                    {
                        helper.foundBase = AIECore.FetchChargedChargers(tank, helper.JobSearchRange * 2.5f, out helper.lastBasePos, out helper.theBase, tank.Team);
                        if (helper.theBase == null)
                            return; // There's no base!
                        helper.lastBaseExtremes = helper.theBase.GetCheapBounds();
                    }
                    return; // There's no enemies left!
                }
                else if (!helper.theResource?.tank?.visible || !helper.theResource.tank.visible.isActive || !ManBaseTeams.IsEnemy(tank.Team, helper.theResource.tank.Team))
                {
                    helper.foundGoal = false;
                    helper.CollectedTarget = false;
                    helper.theResource = null;
                    helper.SettleDown();
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Target destroyed or disbanded.");
                    return;
                }
                direct.SetLastDest(helper.theResource.tank.boundsCentreWorldNoCheck);

                if (dist < helper.lastTechExtents + helper.AutoSpacing)
                {
                    if (!helper.FullMelee)
                        helper.ThrottleState = AIThrottleState.PivotOnly;
                    helper.SettleDown();
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Close to enemy at " + helper.theResource.centrePosition);
                }
                else if (dist < helper.lastTechExtents + helper.MaxCombatRange)
                {
                    helper.AutoHandleObstruction(ref direct);
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Engadging the enemy at " + helper.theResource.centrePosition);
                }
                else if (helper.recentSpeed < 2)
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Removing obstruction at " + tank.transform.position);
                    helper.TryHandleObstruction(hasMessaged, dist, false, true, ref direct);
                }
                AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Moving out to fight at " + helper.theResource.centrePosition + " |Tech is at " + tank.boundsCentreWorldNoCheck);
                direct.DriveDest = EDriveDest.ToBase;
                helper.foundBase = false;
            }
        }

        public static void ShootToDestroy(TankAIHelper helper, Tank tank)
        {
            // Determines the weapons actions and aiming of the AI, this one is more fire-precise and used for turrets
            helper.WantsToFight = false;

            if (helper.theResource)
            {
                helper.lastEnemy = helper.theResource;
            }
            else
                helper.TryRefreshEnemyAllied();

            if (helper.lastEnemyGet != null)
            {
                Vector3 aimTo = (helper.lastEnemyGet.transform.position - tank.transform.position).normalized;
                helper.WeaponDelayClock += KickStart.AIClockPeriod;
                if (helper.SideToThreat)
                {
                    // REVISED: dropped the always-true "|| magnitude > -0.15f" clause; now fires only when actually aligned (< 0.15f) or the clock maxes
                    if (Mathf.Abs((tank.rootBlockTrans.right - aimTo).magnitude) < 0.15f || helper.WeaponDelayClock >= 150)
                    {
                        helper.WantsToFight = true;
                        helper.WeaponDelayClock = 150;
                    }
                }
                else
                {
                    if (Mathf.Abs((tank.rootBlockTrans.forward - aimTo).magnitude) < 0.15f || helper.WeaponDelayClock >= 150)
                    {
                        helper.WantsToFight = true;
                        helper.WeaponDelayClock = 150;
                    }
                }
            }
            else
            {
                helper.WeaponDelayClock = 0;
                helper.WantsToFight = false;
            }
        }
    }
}
