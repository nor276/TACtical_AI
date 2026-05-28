using System;
using System.Reflection;
using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    /// REVISED (overview): beam release now actively disables the beam and clears ForceSetBeam when the tech rights itself or times out, then early-returns.
    /// On beam activation, AlignBeamToGoal rotates the hover orientation toward the destination so the tech faces its goal while righting.
    /// Tipped-over flip retry replaced the actionPause counter ramp with a beamFlipTimer hold.
    internal static class AIEBeam
    {
        // REVISED: reflection handle to TankBeam.m_HoverOrient, used by AlignBeamToGoal to steer the beam-righting orientation
        private static readonly FieldInfo hoverOrient = typeof(TankBeam).GetField(
            "m_HoverOrient", BindingFlags.NonPublic | BindingFlags.Instance);

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

                bool releasedThisTick = false;
                if (helper.BeamTimeoutClock > 0)
                {
                    // REVISED: on first activation, orient the beam toward the destination before righting
                    bool justActivated = !tank.beam.IsActive;
                    if (justActivated)
                    {
                        tank.beam.EnableBeam(true);
                        AlignBeamToGoal(helper, tank);
                    }
                    helper.FullBoost = false;
                    helper.LightBoost = false;
                    thisControl.BoostControlJets = false;
                    // REVISED: when upright again OR timed out, now actively disables the beam and clears ForceSetBeam, then early-returns this tick
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
                    {   // REVISED: flip retry now gated by beamFlipTimer.Due (was an actionPause counter ramp); 3D-navi techs wait out the hold before re-beaming
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
                    // REVISED: while upright, arm the flip-hold timer (was actionPause = 0) so the next tip-over honors BeamFlipTippedHoldSecs
                    if (helper.Attempt3DNavi)
                        helper.beamFlipTimer.Set(AIGlobals.BeamFlipTippedHoldSecs);
                    if (helper.MTLockedToTechBeam && helper.IsMultiTech)
                    {   //Override and disable most driving abilities - We are going to follow the host tech!
                        if ((bool)helper.theResource?.tank)
                        {
                            if (helper.theResource.tank.beam.IsActive)
                            {
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

        // REVISED: exposed internal so TryHandleObstruction can gate the build-beam phase on actual tip-over
        // (the unjam FSM was force-beaming upright-but-stuck techs, causing a pointless beam up/drop loop).
        internal static bool IsTechTippedOver(Tank tank, TankAIHelper helper)
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

        // REVISED: new - on beam activation, points the beam-hover orientation at the horizontal heading to lastDestinationCore so the tech rights itself facing its goal; no-ops if the API field is gone, no goal set, or goal is too close
        private static void AlignBeamToGoal(TankAIHelper helper, Tank tank)
        {
            if (hoverOrient == null) { LogHoverOrientNullOnce(); return; }
            if (tank.beam == null) return;
            if (helper.lastDestinationCore == Vector3.zero) return;
            Vector3 toGoal = helper.lastDestinationCore - tank.boundsCentreWorldNoCheck;
            toGoal.y = 0;
            if (toGoal.sqrMagnitude > 1f)
                hoverOrient.SetValue(tank.beam, AIGlobals.LookRot(toGoal.normalized, Vector3.up));
        }
    }
}
