using System;
using System.Reflection;
using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    internal static class AIEBeam
    {
        // Vanilla CalcHoverOrientation captures rootBlockTrans.forward at EnableBeam time and locks
        // the float-target to it. After a pre-beam twitch the tech is often facing sideways, so the
        // post-beam drop releases it pointing away from the goal — feeding the next stuck cycle.
        // We overwrite m_HoverOrient on the activation frame so the hover target faces the goal.
        private static readonly FieldInfo hoverOrient = typeof(TankBeam).GetField(
            "m_HoverOrient", BindingFlags.NonPublic | BindingFlags.Instance);

        // P09 T-7-1: convert the silent early-return on hoverOrient==null into a first-fire log so
        // a future vanilla API rename is visible in the session log instead of degrading silently.
        private static void LogHoverOrientNullOnce()
        {
            DebugTAC_AI.FirstFire("AIEBeam.AlignBeamToGoal.hoverOrientNull",
                "TankBeam.m_HoverOrient not found (vanilla API change?) - beam will use vanilla forward-lock alignment");
        }

        public static void BeamMaintainer(TankControl thisControl, TankAIHelper helper, Tank tank)
        {
            try
            {
                if (!helper.CanUseBuildBeam)
                {
                    if (tank.beam.IsActive)
                        tank.beam.EnableBeam(false);
                    return;
                }

                // P09 B-6-1 + B-6-2: was setting BeamTimeoutClock=0 in upright/timeout branches
                // but NOT calling EnableBeam(false), then falling through to line ~65 where
                // ForceSetBeam (still true since FSM only clears it at FM>240 or in SettleDown)
                // re-armed the clock to 35 same-frame. The 40-tick safety valve + upright-release
                // were no-ops. Fix: release fully (disable beam, clear ForceSetBeam so FSM re-asserts
                // next tick if still warranted, early-return to skip the re-arm gate).
                bool releasedThisTick = false;
                if (helper.BeamTimeoutClock > 0)
                {
                    bool justActivated = !tank.beam.IsActive;
                    if (justActivated)
                    {
                        tank.beam.EnableBeam(true);
                        AlignBeamToGoal(helper, tank);
                    }
                    helper.FullBoost = false;
                    helper.LightBoost = false;
                    thisControl.BoostControlJets = false;
                    if (tank.rootBlockTrans.up.y > 0.95f)
                    {
                        helper.BeamTimeoutClock = 0;
                        tank.beam.EnableBeam(false);
                        helper.ForceSetBeam = false;
                        releasedThisTick = true;
                    }
                    else if (helper.BeamTimeoutClock > 40)
                    {
                        helper.BeamTimeoutClock = 0;
                        tank.beam.EnableBeam(false);
                        helper.ForceSetBeam = false;
                        releasedThisTick = true;
                    }
                    else
                        helper.BeamTimeoutClock++;
                }
                else if (tank.beam.IsActive)
                    tank.beam.EnableBeam(false);

                if (releasedThisTick)
                    return;

                if (helper.MovementController is AIControllerAir pilot)
                {   // Handoff all operations to AIEAirborne
                    if (!pilot.Grounded || AIEPathing.AboveHeightFromGroundTech(helper, helper.lastTechExtents))
                    {   //Become a ground vehicle for now
                        if (tank.grounded && IsTechTippedOver(tank, helper))
                        {
                            helper.BeamTimeoutClock = 10;
                        }
                        return;
                    }
                }

                if (!helper.IsMultiTech && helper.ForceSetBeam && helper.RequestBuildBeam)
                {
                    helper.BeamTimeoutClock = 35;
                }
                else if (!helper.IsMultiTech && IsTechTippedOver(tank, helper) && helper.RequestBuildBeam)
                {
                    if (helper.Attempt3DNavi)
                    {
                        // REVIVED (own timer): tipped over continuously for BeamFlipTippedHoldSecs -> fire the flip beam.
                        if (helper.beamFlipTimer.Due)
                            helper.BeamTimeoutClock = 1;
                    }
                    else
                    {
                        helper.BeamTimeoutClock = 1;
                    }
                }
                else
                {
                    if (helper.Attempt3DNavi)
                        helper.beamFlipTimer.Set(AIGlobals.BeamFlipTippedHoldSecs);   // re-arm while upright; flip beam fires only after continuous tipped-over time
                    if (helper.MTLockedToTechBeam && helper.IsMultiTech)
                    {   //Override and disable most driving abilities - We are going to follow the host tech!
                        if ((bool)helper.theResource?.tank)
                        {
                            if (helper.theResource.tank.beam.IsActive)
                            {
                                //tank.beam.EnableBeam(true);
                                var allyTrans = helper.theResource.tank.trans;
                                tank.rbody.velocity = Vector3.zero;
                                helper.tank.trans.rotation = AIGlobals.LookRot(allyTrans.TransformDirection(helper.MTOffsetRot), allyTrans.TransformDirection(helper.MTOffsetRotUp));
                                helper.tank.trans.position = allyTrans.TransformPoint(helper.MTOffsetPos);
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            { DebugTAC_AI.Log(KickStart.ModID + ": Error in AIEBeam - " + e); }
        }

        private static bool IsTechTippedOver(Tank tank, TankAIHelper helper)
        {   // It's more crude than the built-in but should take less to process.
            if (tank.rootBlockTrans.up.y < 0)
            {   // the Tech is literally sideways
                return true;
            }
            else if (tank.rootBlockTrans.up.y < 0.25f)
            {   // If we are still moving, we DON'T Build Beam - we are climbing a slope
                return helper.recentSpeed < helper.EstTopSped / 8;
            }
            //tank.AI.IsTankOverturned()
            return false;
        }

        private static void AlignBeamToGoal(TankAIHelper helper, Tank tank)
        {
            if (hoverOrient == null) { LogHoverOrientNullOnce(); return; }
            if (tank.beam == null) return;
            // Vector3.zero is the uninitialized-destination sentinel. If we let it through, toGoal
            // would point from the tech toward world origin — a bogus heading for any tech that
            // isn't at (0,0,0). Skip alignment in that case and let vanilla's current-forward lock
            // stand.
            if (helper.lastDestinationCore == Vector3.zero) return;
            Vector3 toGoal = helper.lastDestinationCore - tank.boundsCentreWorldNoCheck;
            toGoal.y = 0;
            if (toGoal.sqrMagnitude > 1f)
                hoverOrient.SetValue(tank.beam, AIGlobals.LookRot(toGoal.normalized, Vector3.up));
        }
    }
}
