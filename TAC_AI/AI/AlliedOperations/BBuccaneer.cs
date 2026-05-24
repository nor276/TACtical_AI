using UnityEngine;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;

namespace TAC_AI.AI.AlliedOperations
{
    internal static class BBuccaneer {
        public static void MotivateBote(TankAIHelper helper, Tank tank, ref EControlOperatorSet direct)
        {
            helper.lastPlayer = helper.GetPlayerTech();
            helper.IsMultiTech = false;
            helper.Attempt3DNavi = true;

            if (!KickStart.isWaterModPresent)
            {
                helper.DediAI = AIType.Escort;
                return;
            }
            BGeneral.ResetValues(helper, ref direct);
            helper.AvoidStuff = true;

            if (helper.lastPlayer == null)
                return;
            bool hasMessaged = false;
            if (helper.lastPlayer == tank.visible)
            {
                OnIdle(helper, tank, ref direct, ref hasMessaged);
                direct.STOP(helper);
                return;
            }
            float playerExt = helper.lastPlayer.GetCheapBounds();
            float dist = helper.GetDistanceFromTask(helper.lastPlayer.tank.boundsCentreWorldNoCheck, helper.lastPlayer.GetCheapBounds());
            float range = helper.MaxObjectiveRange + helper.lastTechExtents + playerExt;

            if ((bool)helper.lastEnemyGet && !helper.Retreat)
            {
                if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.PlayerAISpeedPanicDividend))
                {
                    helper.TryHandleObstruction(hasMessaged, dist, true, true, ref direct);
                }
                return;
            }

            if (dist < helper.lastTechExtents + playerExt + 2)
            {
                helper.DelayedAnchorClock = 0;
                hasMessaged = AIECore.AIMessage(tech: tank, ref hasMessaged, tank.name + ":  Giving the player some room...");
                direct.DriveDest =  EDriveDest.FromLastDestination;
                helper.ThrottleState = AIThrottleState.ForceSpeed;
                helper.DriveVar = -1;
                if (helper.unanchorCountdown > 0)
                    {  }
                if (helper.AutoAnchor && helper.PlayerAllowAutoAnchoring && tank.Anchors.NumPossibleAnchors >= 1)
                {
                    if (tank.Anchors.NumIsAnchored > 0)
                    {
                        helper.unanchorCountdown = 15;
                        helper.Unanchor();
                    }
                }
            }
            else if (dist < range + playerExt && dist > (range * 0.75f))
            {
                hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ": Departing!");
                direct.DriveDest = EDriveDest.ToLastDestination;
                helper.DelayedAnchorClock = 0;
                if (helper.unanchorCountdown > 0)
                    {  }
                if (helper.AutoAnchor && helper.PlayerAllowAutoAnchoring && tank.Anchors.NumPossibleAnchors >= 1)
                {
                    if (tank.Anchors.NumIsAnchored > 0)
                    {
                        AIECore.AIMessage(tank, ref hasMessaged, tank.name + ": Time to pack up and move out!");
                        helper.unanchorCountdown = 15;
                        helper.Unanchor();
                    }
                }
            }
            else if (dist >= range + playerExt)
            {
                helper.DelayedAnchorClock = 0;
                direct.DriveDest = EDriveDest.ToLastDestination;
                helper.ThrottleState = AIThrottleState.ForceSpeed;
                helper.DriveVar = 1f;

                if (dist > range * 2)
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Oh Crafty they are too far!");
                    helper.Urgency += KickStart.AIClockPeriod / 2f;
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = 1f;
                    if (helper.UrgencyOverload > 0)
                        helper.UrgencyOverload--;
                }
                if (helper.UrgencyOverload > 50)
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    helper.EstTopSped = 1;
                    helper.AvoidStuff = true;
                    helper.UrgencyOverload = 0;
                }
                if (helper.Urgency > 20)
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ": I AM SUPER FAR BEHIND!");
                    helper.AvoidStuff = false;
                    helper.FullBoost = true;
                    helper.UrgencyOverload += KickStart.AIClockPeriod / 5f;
                }
                else if (helper.Urgency > 2)
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ": Wait for meeeeeeeeeee!");
                    helper.AvoidStuff = false;
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = 1;
                    helper.LightBoost = true;
                    helper.UrgencyOverload += KickStart.AIClockPeriod / 5f;
                }
                else if (helper.Urgency > 1 && helper.recentSpeed < 10)
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ": GET OUT OF THE WAY NUMBNUT!");
                    helper.AvoidStuff = false;
                    helper.FIRE_ALL = true;
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = 0.5f;
                    helper.UrgencyOverload += KickStart.AIClockPeriod / 5f;
                }
                if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.PlayerAISpeedPanicDividend))
                {
                    helper.TryHandleObstruction(hasMessaged, dist, true, true, ref direct);
                }
                else if (!helper.IsTechMovingAbs(helper.EstTopSped / 2))
                {
                    hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ": Trying to catch up!");
                    helper.Urgency += KickStart.AIClockPeriod / 5f;
                    helper.ThrottleState = AIThrottleState.ForceSpeed;
                    helper.DriveVar = 1;
                }
                else
                {
                    helper.AvoidStuff = true;
                    helper.SettleDown();
                }
            }
            else if (dist < (range / 2))
            {
                hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Settling");
                helper.AvoidStuff = true;
                helper.SettleDown();
                if (helper.DelayedAnchorClock < AIGlobals.BaseAnchorMinimumTimeDelay)
                    helper.DelayedAnchorClock++;
                if (helper.CanAutoAnchor)
                {
                    if (tank.Anchors.NumIsAnchored == 0 && helper.CanAttemptAnchor)
                    {
                        AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Setting camp!");
                        helper.TryInsureAutoAnchor();
                    }
                }
            }
            else
            {
                OnIdle(helper, tank, ref direct, ref hasMessaged);
            }
        }
        private static void OnIdle(TankAIHelper helper, Tank tank, ref EControlOperatorSet direct, ref bool hasMessaged)
        {
            hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  in resting state");
            helper.AvoidStuff = true;
            helper.SettleDown();
            helper.DriveVar = 0;
            if (helper.DelayedAnchorClock < AIGlobals.BaseAnchorMinimumTimeDelay)
                helper.DelayedAnchorClock++;
            if (helper.CanAutoAnchor && tank.Anchors.NumIsAnchored == 0)
            {
                AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Setting camp!");
                helper.TryInsureAutoAnchor();
            }
        }
    }
}
