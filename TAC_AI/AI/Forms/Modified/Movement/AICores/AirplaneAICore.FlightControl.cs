using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    // Flight control (feature-complete component of AirplaneAICore, Step 3). Low-level maneuvering:
    // U-turn/Immelmann, roll-upright, 3D AngleTowards steering, throttle advice, ground-collision recovery.
    internal partial class AirplaneAICore
    {
        // Utilities
        private const float UprightBankNudgeMultiplierFighter = 0.5f;
        private const float UprightBankNudgeMultiplierSlow = 0.75f;

        public void UTurn(TankAIHelper helper, Tank tank, AIControllerAir pilot)
        {
            pilot.MainThrottle = 1;
            pilot.UpdateThrottle(helper);
            if (helper.LocalSafeVelocity.z < AIGlobals.AirStallSpeed)
            {   //ABORT!!!
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": Tech " + tank.name + "  Aborted U-Turn with velocity " + helper.LocalSafeVelocity.z);
                PerformUTurn = -1;
                pilot.ErrorsInUTurn++;
                if (pilot.ErrorsInUTurn > 3)
                    DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " has failed to U-Turn/Immelmann over 3 times and will no longer try");
            }
            else if (Vector3.Dot(Vector3.down, helper.SafeVelocity.normalized) > 0.6f)
            {   //ABORT!!!
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": Tech " + tank.name + "  Aborted U-Turn as too much movement to the ground");
                PerformUTurn = -1;
                pilot.ErrorsInUTurn++;
                if (pilot.ErrorsInUTurn > 3)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": Tech " + tank.name + " has failed to U-Turn/Immelmann over 3 times and will no longer try");
            }
            if (PerformUTurn == 1)
            {   // Accelerate
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": Tech " + tank.name + " Executing U-Turn[1]...");
                AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck +
                    (tank.rootBlockTrans.forward.SetY(0).normalized.SetY(0.4f) * 300));
                if (pilot.CurrentThrottle > 0.95)
                    PerformUTurn = 2;
            }
            else if (PerformUTurn == 2)
            {   // Pitch Up
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": Tech " + tank.name + " Executing U-Turn[2]...");
                AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck + (tank.rootBlockTrans.forward.SetY(1.75f).normalized * 100));
                if (Vector3.Dot(tank.rootBlockTrans.forward, Vector3.up) > 0.65f)
                    PerformUTurn = 3;
            }
            else if (PerformUTurn == 3)
            {   // Aim back at target
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": Tech " + tank.name + " Executing U-Turn[3]...");
                AngleTowards(helper, tank, pilot, pilot.PathPointSet.SetY(tank.boundsCentreWorldNoCheck.y));
                if (Vector3.Dot((pilot.PathPointSet - tank.boundsCentreWorldNoCheck).normalized, tank.rootBlockTrans.forward) > 0.1f)
                {
                    // REVISED: stage 3 (aim-back) now advances to the new roll-level stage 4 instead of
                    // completing at 0; no longer pokes PerformDiveAttack (the dive FSM owns that now).
                    pilot.ErrorsInUTurn = 0;
                    PerformUTurn = 4;
                }
            }
            else if (PerformUTurn == 4)
            {   // REVISED: new roll-level stage; holds heading until upright (up.y > 0.5), then completes.
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": Tech " + tank.name + " Executing U-Turn[4 roll-level]...");
                AngleTowards(helper, tank, pilot, pilot.PathPointSet.SetY(tank.boundsCentreWorldNoCheck.y));
                if (tank.rootBlockTrans.up.y > 0.5f)
                {
                    PerformUTurn = 0;
                }
            }
        }
        public Vector3 DetermineRollUpright(Tank tank, AIControllerAir pilot, Vector3 Navi3DDirect, bool forceUp, out float nudgeTargPosUp)
        {
            //Vector3 turnValUp = AIGlobals.LookRot(tank.rootBlockTrans.forward, tank.rootBlockTrans.InverseTransformDirection(Vector3.up)).eulerAngles;
            nudgeTargPosUp = 0;

            if (forceUp)
                return Vector3.up;
            Vector3 Heading = tank.rootBlockTrans.InverseTransformDirection(Navi3DDirect);
            float fwdHeading = Heading.ToVector2XZ().normalized.y;
            bool targetLevelElevation = Navi3DDirect.y > -0.6f && Navi3DDirect.y < 0.6f;

            Vector3 upright = Vector3.up;
            if (PerformUTurn == 3)
            {
                //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "  Stage 3 Immelmann");
                upright = Vector3.down;
            }
            else if (PerformUTurn == 4)
            {   // REVISED: roll-level stage forces upright (Vector3.up) to roll out of the inverted stage 3.
                upright = Vector3.up;
            }
            else if (tank.rootBlockTrans.up.y < -0.4f)
            {   // handle invalid request to go upside down
                //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "  IS UPSIDE DOWN AND IS TRYING TO GET UPRIGHT");
                // Stay upright
            }
            else if ((PerformUTurn > 0 && !pilot.LargeAircraft && !BankOnly) || pilot.ForcePitchUp)
            {
                // Stay upright
            }
            else if (fwdHeading < -0.325f && Heading.y < 0.6f && targetLevelElevation && PerformUTurn == 0 &&
                AIEPathing.IsUnderMaxAltPlayer(tank.boundsCentreWorldNoCheck.y))
            {   // Check we are not facing target, not pointing up, target is level elevation,
                //  are within interactive height limit, and we aren't already doing UTurn
                //DebugTAC_AI.Log("directed is " + Navi3DDirect);
                if (pilot.ErrorsInUTurn > 3)    // Aircraft failed Immelmann over 3 times in a row
                    PerformUTurn = -1;
                else if (pilot.LargeAircraft || BankOnly)   // Large aircraft cannot do the Immelmann
                    PerformUTurn = -1;
                else                            // Perform the Immelmann turn, or better known as the "U-Turn"
                    PerformUTurn = 1;
            }
            else if (pilot.LargeAircraft || BankOnly)
            {
                // Because we likely yaw slower, we should bank as much as possible
                if (targetLevelElevation && fwdHeading < 0.925f - (0.2f / pilot.RollStrength))
                {
                    if (Heading.x > 0f)
                    { // We roll to aim at target
                      //DebugTAC_AI.Log(KickStart.ModID + ": (HVY) Tech " + tank.name + "  Roll turn Right");
                        Vector3 rFlat = GetExactRightAlignedWorld(tank, false);
                        rFlat.y = -pilot.RollStrength / 2;
                        upright = Vector3.Cross(tank.rootBlockTrans.forward, rFlat.normalized).normalized;
                        nudgeTargPosUp = UprightBankNudgeMultiplierSlow;
                    }
                    else if (Heading.x < 0f)
                    { // We roll to aim at target
                      //DebugTAC_AI.Log(KickStart.ModID + ": (HVY) Tech " + tank.name + "  Roll turn Left");
                        Vector3 rFlat = GetExactRightAlignedWorld(tank, false);
                        rFlat.y = pilot.RollStrength / 2;
                        upright = Vector3.Cross(tank.rootBlockTrans.forward, rFlat.normalized).normalized;
                        nudgeTargPosUp = UprightBankNudgeMultiplierSlow;
                    }
                }
            }
            else
            {
                if (targetLevelElevation && fwdHeading < 0.85f - (0.2f / pilot.RollStrength))
                {
                    if (Heading.x > 0f)
                    { // We roll to aim at target
                      //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "  Roll turn Right");
                        Vector3 rFlat = GetExactRightAlignedWorld(tank, true);
                        rFlat.y = -pilot.RollStrength;
                        upright = Vector3.Cross(tank.rootBlockTrans.forward, rFlat.normalized).normalized;
                        nudgeTargPosUp = UprightBankNudgeMultiplierFighter;
                    }
                    else if (Heading.x < 0f)
                    { // We roll to aim at target
                      //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "  Roll turn Left");
                        Vector3 rFlat = GetExactRightAlignedWorld(tank, true);
                        rFlat.y = pilot.RollStrength;
                        upright = Vector3.Cross(tank.rootBlockTrans.forward, rFlat.normalized).normalized;
                        nudgeTargPosUp = UprightBankNudgeMultiplierFighter;
                    }
                }
            }
            //DebugTAC_AI.Log(KickStart.ModID + ": upwards direction " + tank.name + "  is " + direct.y);

            return upright; // IS IN WORLD SPACE
        }
        public void AngleTowards(TankAIHelper helper, Tank tank,
            AIControllerAir pilot, Vector3 destPos, bool EmergencyUp = false)
        {
            //AI Steering Rotational
            Transform root = tank.rootBlockTrans;

            if (pilot.LargeAircraft)
            {
                if (!AIEPathing.AboveHeightFromGround(pilot.Helper.DodgeSphereCenter, AIGlobals.GroundOffsetAircraft))
                {
                    EmergencyUp = true;
                }
            }
            else if (!AIEPathing.AboveHeightFromGround(pilot.Helper.DodgeSphereCenter, helper.lastTechExtents + 4))
            {
                EmergencyUp = true;
            }
            Vector3 noseDirect = (destPos - tank.boundsCentreWorldNoCheck).normalized;
            if (EmergencyUp)// || root.forward.y < -AIGlobals.AircraftDangerDive)
            {   // CRASH LIKELY, PULL UP!
                //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is trying to break from a crash-dive " + root.forward.y);
                noseDirect = new Vector3(0, 1.45f, 0) + root.forward.SetY(0).normalized;
            }
            else if (noseDirect.y < -AIGlobals.AircraftMaxDive)
            {
                noseDirect = noseDirect.SetY(-AIGlobals.AircraftMaxDive).normalized;
            }
            else if (Vector3.Dot(noseDirect, root.forward) < 0 && !pilot.ForcePitchUp && PerformUTurn == 0)
            {
                // Try deal with turns well exceeding 90 degrees
                Vector3 clamped = root.InverseTransformVector(noseDirect);
                if (clamped.z < 0)
                {
                    clamped.y = 0;
                    clamped.z = 0;
                }
                noseDirect = root.TransformVector(clamped);
                // Level when turning far
                noseDirect = noseDirect.SetY(0).normalized;
                noseDirect.y = 0.1f;
            }
            helper.Navi3DDirect = noseDirect.normalized;

            helper.Navi3DUp = DetermineRollUpright(tank, pilot, helper.Navi3DDirect, EmergencyUp, out float upNudge);
            if (helper.Navi3DDirect.y > -0.35f)
            {
                helper.Navi3DDirect.y += upNudge;
                helper.Navi3DDirect = helper.Navi3DDirect.normalized;
            }

            Vector3 ForwardsLocal = root.InverseTransformDirection(helper.Navi3DDirect);
            Vector3 turnVal = AIGlobals.LookRot(ForwardsLocal, Vector3.up).eulerAngles;
            Vector3 UpLocal = root.InverseTransformDirection(helper.Navi3DUp);
            Vector3 turnValUp = AIGlobals.LookRot(Vector3.forward, UpLocal).eulerAngles;
            //Vector3 forwardFlat = tank.rootBlockTrans.forward;
            //forwardFlat.y = 0;

            //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " steering RAW" + turnVal);

            //Convert turnVal to runnable format
            // PITCH
            turnVal.x = Mathf.Clamp(-(AIGlobals.AngleUnsignedToSigned(turnVal.x) / pilot.FlyingChillFactor.x), -1, 1);
            // YAW
            turnVal.y = Mathf.Clamp(-(AIGlobals.AngleUnsignedToSigned(turnVal.y) / pilot.FlyingChillFactor.y), -1, 1);
            // ROLL
            turnValUp.z = Mathf.Clamp(-(AIGlobals.AngleUnsignedToSigned(turnValUp.z) / pilot.FlyingChillFactor.z), -1, 1);

            // Control oversteer since there's no proper control limiter for overyaw
            if (BankOnly)
            {
                turnVal.y = Mathf.Clamp(turnVal.y, -AIGlobals.AirMaxYawBankOnly, AIGlobals.AirMaxYawBankOnly);
            }
            else
            {
                turnVal.y = Mathf.Clamp(turnVal.y, -AIGlobals.AirMaxYaw, AIGlobals.AirMaxYaw);
            }

            //Stop Wobble
            if (Mathf.Abs(turnVal.x) < 0.01f)
                turnVal.x = 0;
            if (Mathf.Abs(turnVal.y) < 0.01f)
                turnVal.y = 0;
            if (Mathf.Abs(turnValUp.z) < 0.01f)
                turnValUp.z = 0;

            //Lock yaw AND limit roll when pitch operation is OUTSTANDING
            if (Mathf.Abs(turnVal.x) > 0.9f && EmergencyUp)
            {
                turnVal.y = 0;
                turnValUp.z = Mathf.Clamp(turnValUp.z, -0.25f, 0.25f);
            }


            //helper.Navi3DDirect = (position - tank.boundsCentreWorldNoCheck).normalized;

            if (tank.rootBlockTrans.up.y < 0)
            {   // upside down due to a unfindable oversight in code - just override the bloody thing when it happens
                //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "  IS UPSIDE DOWN AND IS TRYING TO GET UPRIGHT");

                //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " steering" + turnVal);
                //turnVal.z = -Mathf.Clamp(turnVal.z * 10, -1, 1);
            }

            //Turn our work in to process
            turnVal.z = turnValUp.z;
            Vector3 TurnVal = turnVal.Clamp01Box();

            // DRIVE
            Vector3 DriveVar = Vector3.forward * pilot.CurrentThrottle;

            //Turn our work in to processing
            //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " steering" + turnVal);
            Vector3 DriveVal = DriveVar.Clamp01Box();

            // Blue is the target destination, Red is up

            // DEBUG FOR DRIVE ERRORS

            if (AIGlobals.ShowDebugFeedBack)
            {
                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 0, destPos - tank.boundsCentreWorldNoCheck, new Color(0, 1, 1)); //TEAL
                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 1, helper.Navi3DDirect * pilot.Helper.lastTechExtents * 3, new Color(0, 0, 1));//BLUE
                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 2, helper.Navi3DUp * pilot.Helper.lastTechExtents * 3, new Color(1, 0, 0));//RED
            }
            // We never drive backwards, so we do not need to correct that
            helper.ProcessControl(DriveVal, TurnVal, Vector3.zero, false, false);
            return;
        }
        public void AdviseThrottle(AIControllerAir pilot, TankAIHelper helper, Tank tank, Vector3 target)
        {
            if (pilot.AdvisedThrottle == -1)
            {
                if (tank.rbody.IsNotNull())
                {
                    if (helper.LocalSafeVelocity.z > AIGlobals.AirStallSpeed)
                    {
                        float ExtAvoid = helper.AutoSpacing;
                        if (helper.lastPlayer.IsNotNull())
                            ExtAvoid = helper.lastPlayer.GetCheapBounds();
                        float Extremes = ExtAvoid + helper.lastTechExtents + AIGlobals.PathfindingExtraSpace;
                        float throttleToSet = 1;
                        float foreTarg = tank.rootBlockTrans.InverseTransformPoint(target).z;

                        if (foreTarg > 0)
                            throttleToSet = (foreTarg - Extremes) / pilot.PropLerpValue;
                        pilot.AdvisedThrottle = Mathf.Clamp(throttleToSet, 0, 1);

                        if (!pilot.LowerEngines)
                        {   // Save fuel for chasing the enemy
                            if (pilot.NoProps)
                            {
                                if (!pilot.ForcePitchUp && foreTarg > Extremes && helper.SafeVelocity.y > -10 && Vector3.Dot((target - tank.boundsCentreWorldNoCheck).normalized, tank.rootBlockTrans.forward) > 0.6)
                                    helper.FullBoost = true;
                                else
                                    helper.FullBoost = false;
                            }
                            else
                            {
                                if (!pilot.ForcePitchUp && throttleToSet > 1.25f && helper.SafeVelocity.y > -10 && Vector3.Dot((target - tank.boundsCentreWorldNoCheck).normalized, tank.rootBlockTrans.forward) > 0.6)
                                    helper.FullBoost = true;
                                else
                                    helper.FullBoost = false;
                            }
                        }
                        else
                            helper.FullBoost = false;
                        return;
                    }
                }
                pilot.AdvisedThrottle = 1;
            }
        }
        public void AdviseThrottleTarget(AIControllerAir pilot, TankAIHelper helper, Tank tank, Visible target)
        {
            if (pilot.AdvisedThrottle == -1)
            {
                if (tank.rbody.IsNotNull())
                {
                    if (helper.LocalSafeVelocity.z > AIGlobals.AirStallSpeed)
                    {
                        float throttleToSet = 1;
                        float foreTarg = tank.rootBlockTrans.InverseTransformPoint(target.tank.boundsCentreWorldNoCheck).z;
                        float Extremes = target.GetCheapBounds() + helper.lastTechExtents + 5;
                        if (foreTarg > 0)
                            throttleToSet = (foreTarg - Extremes) / pilot.PropLerpValue;
                        pilot.AdvisedThrottle = Mathf.Clamp(throttleToSet, 0, 1);

                        if (pilot.NoProps)
                        {
                            if (!pilot.ForcePitchUp && foreTarg > Extremes && helper.SafeVelocity.y > -10 && Vector3.Dot((target.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).normalized, tank.rootBlockTrans.forward) > 0.6)
                                helper.FullBoost = true;
                            else
                                helper.FullBoost = false;
                        }
                        else
                        {
                            if (!pilot.ForcePitchUp && throttleToSet > 1.25f && helper.LocalSafeVelocity.y > -10 && Vector3.Dot((target.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).normalized, tank.rootBlockTrans.forward) > 0.6)
                                helper.FullBoost = true;
                            else
                                helper.FullBoost = false;
                        }
                        return;
                    }
                }
                pilot.AdvisedThrottle = 1;
            }
        }

        public Vector3 TryGetVelocityOffset(Tank tank, AIControllerAir pilot)
        {
            if (tank.rbody.IsNotNull())
                return tank.boundsCentreWorldNoCheck + (pilot.Helper.SafeVelocity * pilot.AerofoilSluggishness);
            return tank.boundsCentreWorldNoCheck;
        }

        public void PreventCollisionWithGround(AIControllerAir pilot, float groundOffset, bool unresponsiveAir)
        {
            float groundOffsetF = groundOffset; //pilot.AerofoilSluggishness
            if (unresponsiveAir)
            {
                if (!AIEPathing.AboveHeightFromGround(pilot.Helper.DodgeSphereCenter, groundOffsetF + pilot.Helper.lastTechExtents))
                {
                    //DebugTAC_AI.Assert(!AIEPathing.IsUnderMaxAltPlayer(deltaAim), "PreventCollisionWithGround called while height is too high");
                    //DebugTAC_AI.Log(pilot.Helper.tank.name + " -  deltaMovementClock = " + pilot.deltaMovementClock + " | slugishness = " + pilot.AerofoilSluggishness + " | deltaAim y " + deltaAim.y + " | vs " + groundOffsetF);
                    //DebugTAC_AI.Log(pilot.Helper.tank.name + " - GOING UP (HVY)");
                    pilot.ForcePitchUp = true;
                    pilot.PathPointSet.y = pilot.Helper.tank.boundsCentreWorldNoCheck.y;
                    pilot.PathPointSet += Vector3.up * (pilot.PathPointSet - pilot.Helper.tank.boundsCentreWorldNoCheck).ToVector2XZ().magnitude * 4;
                }
            }
            else
            {
                if (!AIEPathing.AboveHeightFromGround(pilot.Helper.DodgeSphereCenter, groundOffsetF))
                {
                    //DebugTAC_AI.Assert(!AIEPathing.IsUnderMaxAltPlayer(deltaAim), "PreventCollisionWithGround called while height is too high");
                    pilot.PathPointSet = AIEPathing.ModerateMaxAlt(pilot.PathPointSet, pilot.Helper);
                    //DebugTAC_AI.Log(pilot.Helper.tank.name + " -  deltaMovementClock = " + pilot.deltaMovementClock.y + " | slugishness = " + pilot.AerofoilSluggishness + " | deltaAim y " + deltaAim.y + " | vs " + groundOffsetF +
                    //    " | tech: " + pilot.Helper.tank.trans.position);
                    //DebugTAC_AI.Log(pilot.Helper.tank.name + " - GOING UP");
                    pilot.ForcePitchUp = true;
                    pilot.PathPointSet.y = pilot.Helper.tank.boundsCentreWorldNoCheck.y;
                    pilot.PathPointSet += Vector3.up * (pilot.PathPointSet - pilot.Helper.tank.boundsCentreWorldNoCheck).ToVector2XZ().magnitude * 4;
                }
            }
        }

        public Vector3 GetExactRightAlignedWorld(Tank tank, bool useLegacy)
        {
            if (useLegacy)
            {
                //return GetExactRightAlignedWorldLegacy(tank);
            }

            Vector3 right;
            if (tank.rootBlockTrans.forward.y >= -0.8f && tank.rootBlockTrans.forward.y <= 0.8f)
            {
                right = Vector3.Cross(Vector3.up, tank.rootBlockTrans.forward.SetY(0).normalized).SetY(0).normalized;
                if (AIGlobals.ShowDebugFeedBack)
                    DebugExtUtilities.DrawDirIndicator(tank.gameObject, 7, right * 24, new Color(1, 1, 0, 1));
                return right;
            }
            else
            {
                return GetExactRightAlignedWorldLegacy(tank);
            }

        }

        public static Vector3 GetExactRightAlignedWorldLegacy(Tank tank)
        {
            Vector3 rFlat;
            if (tank.rootBlockTrans.up.y > 0)
                rFlat = tank.rootBlockTrans.right;
            else
                rFlat = -tank.rootBlockTrans.right;
            rFlat.y = 0;
            rFlat.Normalize();
            if (AIGlobals.ShowDebugFeedBack)
                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 7, rFlat * 24, new Color(1, 1, 0, 1));
            return rFlat;
        }
    }
}
