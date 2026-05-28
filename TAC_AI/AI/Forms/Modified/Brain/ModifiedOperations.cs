using UnityEngine;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Engine;
using TAC_AI.AI.Movement;
using TAC_AI.World;

namespace TAC_AI.AI
{
    /// <summary>
    /// Per-tech operations-dispatch brain - DETACHED from TankAIHelper into the Modified form (v2) as extension methods.
    /// Covers the four top-level dispatch entrypoints: RunStaticOperations (anchored repair tick), RunAlliedOperations
    /// (allied AI type resolution + OpsController/RTS fork), RunEnemyOperations (retreat posture + BeEvil/BeEvilLight),
    /// and RunRTSNavi (RTS waypoint driving for allied/player techs). State accessed through the TankAIHelper bus:
    /// BoltsFired, Attempt3DNavi, Retreat, RTSControlled, OpsController, lastAIType/lastAITypeResolved, DriverType,
    /// lastEnemyGet, RTSDestination, lastDestinationCore, lastOperatorRange, AvoidStuff, FullBoost, AutoSpacing,
    /// ThrottleState, DelayedAnchorClock, CanAutoAnchor, AutoAnchor, PlayerAllowAutoAnchoring, unanchorCountdown,
    /// anchorAttempts, CanAnchorNow. RCore.BeEvil/BeEvilLight are static calls on RCore in TAC_AI.AI.Enemy; they receive
    /// (helper, helper.tank) by convention. BGeneral.ResetValues/RTSCombat are static calls in TAC_AI.AI.
    /// </summary>
    public static class ModifiedOperations
    {
        public static void RunStaticOperations(this TankAIHelper helper)
        {
            if (Singleton.Manager<ManWorld>.inst.CheckIsTileAtPositionLoaded(helper.tank.boundsCentreWorldNoCheck))
                helper.TryRepairStatic();
        }

        public static void RunAlliedOperations(this TankAIHelper helper)
        {
            var aI = helper.tank.AI;

            helper.CheckTryRepairAllied();

            helper.BoltsFired = false;
            helper.Attempt3DNavi = false;

            // REVISED: target focus/Provoke decay (UpdateTargetCombatFocus) now runs once up-front for ALL allied techs, including PlayerFocused ones (was only on the non-PlayerFocused branch); the per-tick ActionPause decrement moved out to OnPreUpdate.
            helper.UpdateTargetCombatFocus();

            if (helper.tank.PlayerFocused)
            {
                if (KickStart.AllowPlayerRTSHUD)
                {
#if DEBUG
                    if (ManWorldRTS.PlayerIsInRTS && ManWorldRTS.DevCamLock == DebugCameraLock.LockTechToCam)
                    {
                        if (helper.tank.rbody)
                        {
                            helper.tank.rbody.MovePosition(Singleton.cameraTrans.position + (Vector3.up * 75));
                            return;
                        }
                    }
#endif
                    if (KickStart.AutopilotPlayer)
                    {
                        helper.Retreat = helper.DetermineRetreatPosture();
                        if (helper.RTSControlled)
                        {
                            DebugTAC_AI.LogWarnPlayerOncePerKey(
                                "PlayerRTSDetour:" + helper.tank.name,
                                "AI " + helper.tank.name + ": player-side RTS detour active (RunRTSNavi(true) instead of OpsController.Execute).", null);
                            helper.RunRTSNavi(true);
                        }
                        else
                            ProfileRunner.RunAllied(new AIContext(helper));
                    }
                }
                return;
            }
            // REVISED: a failed TryGetCurrentAIType now clears lastAITypeResolved (so SetToActive stays false and the tech is suspended) instead of silently falling through with a stale Idle.
            if (!aI.TryGetCurrentAIType(out helper.lastAIType))
            {
                if (helper.lastAITypeResolved)
                    DebugTAC_AI.Log(KickStart.ModID + ": " + helper.tank.name +
                        " - TryGetCurrentAIType FAILED; AI input suspended until resolved.");
                helper.lastAITypeResolved = false;
                helper.lastAIType = AITreeType.AITypes.Idle;
                return;
            }
            helper.lastAITypeResolved = true;
            if (helper.SetToActive)
            {


                helper.Retreat = helper.DetermineRetreatPosture();

                if (helper.RTSControlled && helper.IsRTSReceivable)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "AlliedRTSDetour:" + helper.tank.name,
                        "AI " + helper.tank.name + ": allied-side RTS detour active (RunRTSNavi instead of OpsController.Execute).", null);
                    helper.RunRTSNavi();
                }
                else
                    ProfileRunner.RunAllied(new AIContext(helper));
            }
        }

        // REVISED: the per-tick `ActionPause -= AIClockPeriod` decrement was removed here (it is now seconds-based and self-counts); only the retreat posture is computed before BeEvil(Light).
        public static void RunEnemyOperations(this TankAIHelper helper, bool light = false)
        {
            helper.DetermineRetreatPostureEnemy();
            if (light)
                RCore.BeEvilLight(helper, helper.tank);
            else
            {
                RCore.BeEvil(helper, helper.tank);
            }
        }

        public static void RunRTSNavi(this TankAIHelper helper, bool isPlayerTech = false)
        {

            EControlOperatorSet direct = helper.GetDirectedControl();
            if (helper.DriverType == AIDriverType.Pilot)
            {
                if (helper.DediAI == AIType.Escort && !helper.IsGoingToPositionalRTSDest &&
                    (helper.lastEnemyGet == null || !helper.lastEnemyGet.isActive))
                    helper.RTSDestination = helper.tank.boundsCentreWorldNoCheck;

                helper.GetDistanceFromTask2D(helper.lastDestinationCore, 0);
                helper.Attempt3DNavi = true;
                BGeneral.ResetValues(helper, ref direct);
                helper.AvoidStuff = true;

                float range = (helper.MaxObjectiveRange * 4) + helper.lastTechExtents;
                direct.DriveDest = EDriveDest.ToLastDestination;
                if (AIEPathing.ObstructionAwarenessAny(helper.DodgeSphereCenter, helper, helper.DodgeSphereRadius) ||
                    AIEPathing.ObstructionAwarenessTerrain(helper.DodgeSphereCenter, helper, helper.DodgeSphereRadius))
                    helper.ThrottleState = AIThrottleState.Yield;
                if (helper.lastEnemyGet != null && helper.lastEnemyGet.isActive)
                {
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                }

                if (helper.tank.wheelGrounded)
                {
                    if (!helper.AutoHandleObstruction(ref direct, helper.lastOperatorRange, true, true))
                        helper.SettleDown();
                }
                else
                {
                    if (helper.lastOperatorRange < (helper.lastTechExtents * 2) + 5)
                    {

                    }
                    else if (helper.lastOperatorRange > range)
                    {
                        helper.FullBoost = true;
                    }
                    else
                    {

                    }
                }
            }
            else
            {
                helper.GetDistanceFromTask(helper.lastDestinationCore);
                bool needsToSlowDown = helper.IsOrbiting();

                helper.Attempt3DNavi = helper.DriverType == AIDriverType.Astronaut;
                BGeneral.ResetValues(helper, ref direct);
                helper.AvoidStuff = true;
                if (needsToSlowDown || AIEPathing.ObstructionAwarenessAny(helper.DodgeSphereCenter, helper, helper.DodgeSphereRadius)
                    || AIEPathing.ObstructionAwarenessSetPieceAny(helper.DodgeSphereCenter, helper, helper.DodgeSphereRadius))
                    helper.ThrottleState = AIThrottleState.Yield;
                if (helper.DediAI == AIType.Escort && !helper.IsGoingToPositionalRTSDest &&
                    (helper.lastEnemyGet == null || !helper.lastEnemyGet.isActive))
                {
                    direct.STOP(helper);
                    BGeneral.RTSCombat(helper, helper.tank);
                    helper.SetDirectedControl(direct);
                    return;
                }

                bool MoveQueue = ManWorldRTS.HasMovementQueue(helper);
                direct.DriveToFacingTowards();
                if (helper.lastOperatorRange < (helper.lastTechExtents * 2) + 32 && !MoveQueue)
                {
                    helper.SettleDown();
                    helper.ThrottleState = AIThrottleState.PivotOnly;
                    if (helper.DelayedAnchorClock < AIGlobals.BaseAnchorMinimumTimeDelay)
                        helper.DelayedAnchorClock++;
                    if (helper.CanAutoAnchor)
                    {
                        if (!helper.tank.IsAnchored)
                        {
                            DebugTAC_AI.Log(KickStart.ModID + ": AI " + helper.tank.name + ":  Setting camp!");
                            helper.TryInsureAutoAnchor();
                        }
                    }
                }
                else
                {
                    helper.anchorAttempts = 0;
                    helper.DelayedAnchorClock = 0;
                    if (helper.unanchorCountdown > 0)
                        {  }
                    if (helper.AutoAnchor && helper.PlayerAllowAutoAnchoring && helper.tank.Anchors.NumPossibleAnchors >= 1)
                    {
                        if (helper.tank.Anchors.NumIsAnchored > 0)
                        {
                            helper.unanchorCountdown = 15;
                            helper.Unanchor();
                        }
                    }
                    if (!helper.AutoAnchor && helper.tank.IsAnchored)
                    {
                        BGeneral.RTSCombat(helper, helper.tank);
                        helper.SetDirectedControl(direct);
                        return;
                    }
                    if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.PlayerAISpeedPanicDividend))
                    {
                        helper.TryHandleObstruction(true, helper.lastOperatorRange, false, true, ref direct);
                    }
                    else
                    {
                        if (MoveQueue)
                            helper.AutoSpacing = 0;
                        else
                            helper.AutoSpacing = Mathf.Max(helper.lastTechExtents + 2, 0.5f);
                        helper.SettleDown();
                    }
                }
            }
            helper.SetDirectedControl(direct);
            BGeneral.RTSCombat(helper, helper.tank);
        }
    }
}
