using System;
using System.Reflection;
using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    internal static class AIEBeam
    {
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
                {
                    if (!pilot.Grounded || AIEPathing.AboveHeightFromGroundTech(helper, helper.lastTechExtents))
                    {
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
                        helper.beamFlipTimer.Set(AIGlobals.BeamFlipTippedHoldSecs);
                    if (helper.MTLockedToTechBeam && helper.IsMultiTech)
                    {
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

        private static bool IsTechTippedOver(Tank tank, TankAIHelper helper)
        {
            if (tank.rootBlockTrans.up.y < 0)
            {
                return true;
            }
            else if (tank.rootBlockTrans.up.y < 0.25f)
            {
                return helper.recentSpeed < helper.EstTopSped / 8;
            }
            return false;
        }

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
