using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    /// <summary>
    /// Unjam / obstruction FSM brain - DETACHED from TankAIHelper into the Modified form (v2) as extension methods.
    /// Detects a wedged/blocked tech and drives recovery (rush / gun-free / build-beam). Unjam STATE (ForceSetBeam/
    /// IsTryingToUnjam/BeamTimeoutClock/FIRE_ALL/Obst/Urgency*/FrustrationMeter) stays on the shell bus; the
    /// bus-state reset (helper.SettleDown, which also issues the stop sink) stays on TankAIHelper. The two obstruction
    /// methods take the drive operator by ref. Behaviors reach these via IAIContext.
    /// </summary>
    public static class ModifiedUnjam
    {
        public static bool AutoHandleObstruction(this TankAIHelper helper, ref EControlOperatorSet direct, float dist = 0, bool useRush = false, bool useGun = true, float div = 4)
        {
            if (!helper.IsTechMovingAbs(helper.EstTopSped / div))
            {
                helper.TryHandleObstruction(!AIECore.Feedback, dist, useRush, useGun, ref direct);
                return true;
            }
            return false;
        }

        public static void TryHandleObstruction(this TankAIHelper helper, bool hasMessaged, float dist, bool useRush, bool useGun, ref EControlOperatorSet direct)
        {
            if (!hasMessaged)
            {
            }

            helper.ControlCore.FlagBusyUnstucking();
            if (helper.FrustrationMeter <= AIGlobals.UnjamUpdateStart)
                helper.IsTryingToUnjam = false;
            if (direct.DriveDir == EDriveFacing.Stop)
                return;
            if (helper.FrustrationMeter > 0 && helper.MakingNetProgress)
            {
                helper.FrustrationMeter = Mathf.Max(0, helper.FrustrationMeter - Mathf.Max(1, KickStart.AIClockPeriod / 2));
            }
            helper.ThrottleState = AIThrottleState.FullSpeed;
            if (direct.DriveDir == EDriveFacing.Backwards)
            {
                helper.ThrottleState = AIThrottleState.ForceSpeed;
                helper.DriveVar = -1;

                if (helper.Urgency >= 0)
                    helper.Urgency += KickStart.AIClockPeriod / 5f;
                if (helper.UrgencyOverload > AIGlobals.UrgencyOverloadReconsideration)
                {
                    AIECore.AIMessage(tech: helper.tank, ref hasMessaged, helper.tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    helper.EstTopSped = 1;
                    helper.AvoidStuff = true;
                    helper.UrgencyOverload = 0;
                }
                else if (useRush && dist > helper.MaxObjectiveRange * 2)
                {
                    if (useGun)
                        helper.RemoveObstruction();
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = -1f;
                    helper.Urgency += KickStart.AIClockPeriod / 5f;
                }
                else if (AIGlobals.UnjamUpdateStart < helper.FrustrationMeter)
                {
                    helper.IsTryingToUnjam = true;
                    helper.FrustrationMeter += KickStart.AIClockPeriod;
                    if (AIGlobals.UnjamUpdateEnd < helper.FrustrationMeter)
                    {
                        helper.SettleDown(false);
                        return;
                    }
                    else if (AIGlobals.UnjamUpdateDrop < helper.FrustrationMeter)
                    {
                        helper.ControlCore.DriveToFacingTowards();
                        helper.ForceSetBeam = false;
                        helper.ThrottleState = AIThrottleState.ForceSpeed;
                        helper.DriveVar = 1;
                    }
                    else
                    {
                        helper.ControlCore.DriveToFacingTowards();
                        helper.ThrottleState = AIThrottleState.ForceSpeed;
                        helper.DriveVar = 1;
                        helper.ForceSetBeam = AIEBeam.IsTechTippedOver(helper.tank, helper);
                    }
                }
                else if (AIGlobals.UnjamUpdateFire < helper.FrustrationMeter)
                {
                    helper.FrustrationMeter += KickStart.AIClockPeriod;
                    helper.UrgencyOverload += KickStart.AIClockPeriod;
                    if (useGun)
                        helper.RemoveObstruction();
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = -0.5f;
                }
                else
                {
                    helper.FrustrationMeter += KickStart.AIClockPeriod;
                    helper.UrgencyOverload += KickStart.AIClockPeriod;
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = -1f;
                }
            }
            else
            {
                helper.ThrottleState = AIThrottleState.ForceSpeed;
                helper.DriveVar = 1;

                if (helper.Urgency >= 0)
                    helper.Urgency += KickStart.AIClockPeriod / 5f;
                if (helper.UrgencyOverload > AIGlobals.UrgencyOverloadReconsideration)
                {
                    AIECore.AIMessage(tech: helper.tank, ref hasMessaged, helper.tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    helper.EstTopSped = 1;
                    helper.AvoidStuff = true;
                    helper.UrgencyOverload = 0;
                }
                else if (useRush && dist > helper.MaxObjectiveRange * 2)
                {
                    if (useGun)
                        helper.RemoveObstruction();
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = 1f;
                    helper.Urgency += KickStart.AIClockPeriod / 5f;
                }
                else if (AIGlobals.UnjamUpdateStart < helper.FrustrationMeter)
                {
                    helper.IsTryingToUnjam = true;
                    helper.FrustrationMeter += KickStart.AIClockPeriod;
                    if (AIGlobals.UnjamUpdateEnd < helper.FrustrationMeter)
                    {
                        helper.SettleDown(false);
                        return;
                    }
                    else if (AIGlobals.UnjamUpdateDrop < helper.FrustrationMeter)
                    {
                        helper.ForceSetBeam = false;
                        helper.ControlCore.DriveAwayFacingTowards();
                        helper.ThrottleState = AIThrottleState.ForceSpeed;
                        helper.DriveVar = -1;
                    }
                    else
                    {
                        helper.ControlCore.DriveAwayFacingTowards();
                        helper.ThrottleState = AIThrottleState.ForceSpeed;
                        helper.DriveVar = -1;
                        helper.ForceSetBeam = AIEBeam.IsTechTippedOver(helper.tank, helper);
                    }
                }
                else if (AIGlobals.UnjamUpdateFire < helper.FrustrationMeter)
                {
                    helper.FrustrationMeter += KickStart.AIClockPeriod;
                    helper.UrgencyOverload += KickStart.AIClockPeriod;
                    if (useGun)
                        helper.RemoveObstruction();
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = 0.5f;
                }
                else
                {
                    helper.FrustrationMeter += KickStart.AIClockPeriod;
                    helper.UrgencyOverload += KickStart.AIClockPeriod;
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = 1f;
                }
            }
        }

        private static Transform GetObstruction(this TankAIHelper helper, float searchRad)
        {
            List<Visible> ObstList;
            if (helper.tank.rbody)
                ObstList = AIEPathing.ObstructionAwareness(helper.tank.boundsCentreWorldNoCheck + helper.SafeVelocity, helper, searchRad);
            else
                ObstList = AIEPathing.ObstructionAwareness(helper.tank.boundsCentreWorldNoCheck, helper, searchRad);
            int bestStep = 0;
            float bestValue = 250000;
            int steps = ObstList.Count;
            if (steps <= 0)
            {
                return null;
            }
            for (int stepper = 0; steps > stepper; stepper++)
            {
                float temp = Mathf.Clamp((ObstList.ElementAt(stepper).centrePosition - helper.tank.boundsCentreWorldNoCheck).sqrMagnitude - ObstList.ElementAt(stepper).Radius, 0, 500);
                if (bestValue > temp && temp != 0)
                {
                    bestStep = stepper;
                    bestValue = temp;
                }
            }
            return ObstList.ElementAt(bestStep).trans;
        }

        public static void RemoveObstruction(this TankAIHelper helper, float searchRad = 12)
        {
            float staleRadSqr = (searchRad * 1.5f) * (searchRad * 1.5f);
            bool outOfRange = helper.Obst != null
                && (helper.Obst.position - helper.tank.boundsCentreWorldNoCheck).sqrMagnitude > staleRadSqr;
            if (helper.Obst == null || outOfRange)
            {
                helper.Obst = helper.GetObstruction(searchRad);
                helper.Urgency += KickStart.AIClockPeriod / 5f;
            }
            helper.FIRE_ALL = true;
        }
    }
}
