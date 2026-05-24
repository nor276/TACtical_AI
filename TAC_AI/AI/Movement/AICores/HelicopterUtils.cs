using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    internal class HelicopterUtils
    {
        public static void AngleTowardsUp(AIControllerAir pilot, Vector3 positionToMoveTo,
            Vector3 positionToLookAt, ref EControlCoreSet core, bool ForceAccend = false)
        {
            TankAIHelper helper = pilot.Helper;
            Tank tank = pilot.Tank;
            Vector3 turnVal;
            float upVal = tank.rootBlockTrans.up.y;
            bool isInControl;
            if (helper.FullMelee)
            {
                isInControl = upVal >= 0.375f;
            }
            else
                isInControl = upVal > 0.425f;
            DeterminePitchRoll(tank, pilot, positionToMoveTo, positionToLookAt, helper, !isInControl, isInControl, ref core);
            Vector3 fwdDelta;
            if (ForceAccend || !isInControl)
            {
                Vector3 forwardFlat = helper.Navi3DDirect;
                forwardFlat.y = -tank.rootBlockTrans.forward.y;
                forwardFlat.Normalize();
                helper.Navi3DDirect = forwardFlat;
                helper.Navi3DUp = Vector3.up;
                fwdDelta = tank.rootBlockTrans.InverseTransformDirection(forwardFlat);
                turnVal = AIGlobals.LookRot(fwdDelta, tank.rootBlockTrans.InverseTransformDirection(Vector3.up)).eulerAngles;
            }
            else
            {
                fwdDelta = tank.rootBlockTrans.InverseTransformDirection(helper.Navi3DDirect);
                turnVal = AIGlobals.LookRot(fwdDelta, tank.rootBlockTrans.InverseTransformDirection(helper.Navi3DUp)).eulerAngles;
            }
            bool needsTurnControl = fwdDelta.z < 0.65f;

            turnVal.x = Mathf.Clamp(-(AIGlobals.AngleUnsignedToSigned(turnVal.x) / pilot.FlyingChillFactor.x), -1, 1);

            turnVal.y = Mathf.Clamp(-(AIGlobals.AngleUnsignedToSigned(turnVal.y) / pilot.FlyingChillFactor.y), -1, 1);

            turnVal.z = Mathf.Clamp(-(AIGlobals.AngleUnsignedToSigned(turnVal.z) / pilot.FlyingChillFactor.z), -1, 1);

            if (Mathf.Abs(turnVal.x) < 0.05f)
                turnVal.x = 0;
            if (Mathf.Abs(turnVal.y) < 0.05f)
                turnVal.y = 0;
            if (Mathf.Abs(turnVal.z) < 0.05f)
                turnVal.z = 0;

            if (helper.SafeVelocity.y < -6)
                turnVal.y = 0;

            Vector3 TurnVal = turnVal.Clamp01Box();

            float xOffset = 0;
            if (core.DriveDir == EDriveFacing.Perpendicular && helper.lastEnemyGet != null)
            {
                if (helper.LocalSafeVelocity.x > 0)
                    xOffset = 0.4f;
                else
                    xOffset = -0.4f;
            }

            Vector3 DriveVar = -helper.LocalSafeVelocity / pilot.PropLerpValue;
            float xFactor = fwdDelta.z - 0.65f;
            if (needsTurnControl)
                DriveVar.x = 0;
            else
                DriveVar.x = Mathf.Clamp(DriveVar.x + xOffset, -1, 1) * xFactor;
            DriveVar.z = Mathf.Clamp(DriveVar.z , -1, 1);
            DriveVar.y = 0;
            if (isInControl)
            {
                Vector3 nudge = tank.rootBlockTrans.InverseTransformPoint(positionToMoveTo) / helper.lastTechExtents;
                if (helper.ThrottleState == AIThrottleState.PivotOnly)
                {
                }
                else if (helper.IsDirectedMovingFromDest)
                {
                    if (!needsTurnControl)
                        DriveVar.x = -nudge.x * xFactor;
                    DriveVar.z = -nudge.z;
                }
                else if (helper.IsDirectedMovingToDest)
                {
                    if (!needsTurnControl)
                        DriveVar.x = nudge.x * xFactor;
                    DriveVar.z = nudge.z;
                }
            }
            DriveVar.y = pilot.CurrentThrottle;

            Vector3 DriveVal = DriveVar.Clamp01Box();


            if (AIGlobals.ShowDebugFeedBack)
            {
                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 0, positionToMoveTo - tank.boundsCentreWorldNoCheck, new Color(0, 1, 1));
                if (ForceAccend || !isInControl)
                    DebugExtUtilities.DrawDirIndicator(tank.gameObject, 1, (tank.rootBlockTrans.TransformPoint(DriveVar) - tank.trans.position).normalized * helper.lastTechExtents, new Color(1, 1, 0));
                else
                    DebugExtUtilities.DrawDirIndicator(tank.gameObject, 1, (tank.rootBlockTrans.TransformPoint(DriveVar) - tank.trans.position).normalized * helper.lastTechExtents, new Color(0, 0, 1));
                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 2, helper.Navi3DUp * pilot.Helper.lastTechExtents, new Color(1, 0, 0));
                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 3, helper.Navi3DDirect * pilot.Helper.lastTechExtents, new Color(1, 1, 1));
            }

            if (helper.FixControlReversal(DriveVal.z))
                TurnVal = TurnVal.SetY(-turnVal.y);
            helper.ProcessControl(DriveVal, TurnVal, Vector3.zero, false, false);
        }
        private static void DeterminePitchRoll(Tank tank, AIControllerAir pilot, Vector3 DestPosWorld, Vector3 LookPosWorld,
            TankAIHelper helper, bool avoidCrash, bool PointAtTarget, ref EControlCoreSet core)
        {
            float pitchDampening = Mathf.Lerp(16, 64, Mathf.InverseLerp(1, 64, helper.lastTechExtents));
            float rotateDividend = pitchDampening / pilot.SlowestPropLerpSpeed;
            Vector3 Heading;
            if (PointAtTarget)
            {
                Heading = (LookPosWorld - tank.boundsCentreWorldNoCheck).normalized;
            }
            else
                Heading = (DestPosWorld - tank.boundsCentreWorldNoCheck).normalized;
            Vector3 fFlat = Heading;
            Vector3 HeadingLocal = tank.rootBlockTrans.InverseTransformVector(Heading);

            Vector3 tankForward = tank.rootBlockTrans.forward;
            float tankUp = tank.rootBlockTrans.up.y;
            Vector3 directUp;
            Vector3 rFlat;
            Vector3 veloLocal = helper.LocalSafeVelocity;
            bool inertiaDampen;
            switch (helper.ThrottleState)
            {
                case AIThrottleState.PivotOnly:
                    inertiaDampen = true;
                    break;
                case AIThrottleState.Yield:
                    if (veloLocal.z > AIGlobals.YieldSpeed)
                        inertiaDampen = true;
                    else if (veloLocal.z < -AIGlobals.YieldSpeed)
                        inertiaDampen = true;
                    else
                        inertiaDampen = false;
                    break;
                case AIThrottleState.FullSpeed:
                case AIThrottleState.ForceSpeed:
                    inertiaDampen = false;
                    break;
                default:
                    throw new NotImplementedException("Unknown ThrottleState " + helper.ThrottleState);
            }
            if (!inertiaDampen && !PointAtTarget)
            {
                fFlat.y = 0;
                fFlat.Normalize();
                if (pilot.LowerEngines || avoidCrash)
                    fFlat.y = 0;
                else if (Vector2.Dot(Heading.ToVector2XZ(), tankForward.ToVector2XZ()) < AIGlobals.ChopperAngleDoPitchPercent)
                {
                    inertiaDampen = true;
                }
                else
                {
                    if (helper.IsDirectedMovingFromDest)
                        fFlat.y = Mathf.Clamp((veloLocal.z / (rotateDividend * Mathf.Abs(HeadingLocal.z))) + AIGlobals.ChopperAngleNudgePercent,
                            -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
                    else if (helper.IsDirectedMovingToDest)
                        fFlat.y = Mathf.Clamp((veloLocal.z / (rotateDividend * Mathf.Abs(HeadingLocal.z))) - AIGlobals.ChopperAngleNudgePercent,
                            -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
                    else
                        fFlat.y = Mathf.Clamp(veloLocal.z / (rotateDividend * Mathf.Abs(HeadingLocal.z)),
                            -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
                }

            }
            if (inertiaDampen)
            {
                if (veloLocal.z > AIGlobals.ChopperSpeedCounterPitch)
                    fFlat.y = Mathf.Clamp((veloLocal.z / rotateDividend)
                        + AIGlobals.ChopperAngleNudgePercent, -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
                else if (veloLocal.z < -AIGlobals.ChopperSpeedCounterPitch)
                    fFlat.y = Mathf.Clamp((veloLocal.z / rotateDividend)
                        - AIGlobals.ChopperAngleNudgePercent, -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
                else
                    fFlat.y = Mathf.Clamp(veloLocal.z / rotateDividend,
                        -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
            }

            if (tankUp > 0)
                rFlat = tank.rootBlockTrans.right;
            else
                rFlat = -tank.rootBlockTrans.right.SetY(0).normalized;
            if (!avoidCrash)
            {
                if (core.DriveDir == EDriveFacing.Perpendicular)
                {
                    if (veloLocal.x >= 0)
                        rFlat.y = Mathf.Clamp((veloLocal.x / rotateDividend) - AIGlobals.ChopperAngleNudgePercent,
                            -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
                    else
                        rFlat.y = Mathf.Clamp((veloLocal.x / rotateDividend) + AIGlobals.ChopperAngleNudgePercent,
                            -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
                }
                else
                    rFlat.y = Mathf.Clamp(veloLocal.x / (rotateDividend * Mathf.Abs(HeadingLocal.x)),
                        -AIGlobals.ChopperMaxDeltaAnglePercent, AIGlobals.ChopperMaxDeltaAnglePercent);
            }

            if (fFlat.y > AIGlobals.ChopperMaxDeltaAnglePercent)
            {
                fFlat.y = AIGlobals.ChopperMaxDeltaAnglePercent;
                fFlat.Normalize();
                fFlat.y = AIGlobals.ChopperMaxDeltaAnglePercent;
            }
            else if (fFlat.y < -AIGlobals.ChopperMaxDeltaAnglePercent)
            {
                fFlat.y = -AIGlobals.ChopperMaxDeltaAnglePercent;
                fFlat.Normalize();
                fFlat.y = -AIGlobals.ChopperMaxDeltaAnglePercent;
            }

            directUp = Vector3.Cross(tankForward, rFlat.normalized);
            float angleRestrict = 1f - AIGlobals.ChopperMaxDeltaAnglePercent;
            if (directUp.y < angleRestrict)
                directUp = directUp.SetY(angleRestrict).normalized.SetY(angleRestrict).normalized;
            else
                directUp.Normalize();

            helper.Navi3DDirect = fFlat.normalized;
            helper.Navi3DUp = directUp;
        }
        internal class HelperGUI : MonoBehaviour
        {
            static HelperGUI helpGUI = null;
            private Rect Window = new Rect(0, 0, 280, 145);
            public static void Init()
            {
                if (helpGUI == null)
                    helpGUI = new GameObject().AddComponent<HelperGUI>();
            }
            public void OnGUI()
            {
                try
                {
                    Window = GUI.Window(2958715, Window, GUIWindow, "Settings");
                }
                catch { }
            }
            private void GUIWindow(int ID)
            {
                GUILayout.Label("ChopperThrottleAntiBounce: " + AIGlobals.ChopperDownAntiBounce.ToString("F"));
                AIGlobals.ChopperDownAntiBounce = Mathf.Round(GUILayout.HorizontalSlider(
                    AIGlobals.ChopperDownAntiBounce, 0.5f, 2f) * 20f) / 20f;
                GUILayout.Label("ChopperThrottleDamper: " + AIGlobals.ChopperThrottleDamper.ToString("F"));
                AIGlobals.ChopperThrottleDamper = Mathf.Round(GUILayout.HorizontalSlider(
                    AIGlobals.ChopperThrottleDamper, 1f, 5f) * 20f) / 20f;
                GUI.DragWindow();
            }
        }
        public static float ThrustLeveler = 1f;
        public static int Iterations = 0;
        public static float ModerateUpwardsThrust(Tank tank, TankAIHelper helper, AIControllerAir pilot, float targetHeight, bool ForceUp = false)
        {
            pilot.LowerEngines = false;
            float final;
            if (ForceUp)
                final = 1;
            else if (tank.rbody)
            {
                final = pilot.MainThrottle;
                float speedDownTime, deltaDists, beginStopDist;
                float deltaHeight = targetHeight - tank.boundsCentreWorldNoCheck.y;
                float throttleLerp = pilot.SlowestPropLerpSpeed * Time.deltaTime * 0.9f;
                float throttleCurTo0 = final / pilot.SlowestPropLerpSpeed;
                float accel = ((pilot.UpTtWRatio * final) - 1f) * TankAIManager.GravMagnitude;
                float velo = helper.SafeVelocity.y;
                float stopDist = 0;
                if (accel > 0 != velo > 0)
                    stopDist = velo * Mathf.Abs(velo) / Mathf.Abs(accel * 2);

                if (deltaHeight < 0)
                {
                    if (velo > 0.25f)
                    {
                        final -= throttleLerp;
                    }
                    else
                    {
                        deltaDists = Mathf.Abs(stopDist / deltaHeight);
                        beginStopDist = Mathf.Clamp01(1.1f - deltaDists);
                        speedDownTime = Mathf.Abs(velo / accel) * AIGlobals.ChopperDownAntiBounce;
                        if (deltaDists > 1f)
                        {
                            final += throttleLerp;
                        }
                        else if (speedDownTime < throttleCurTo0 + 0.5f)
                        {
                            final += throttleLerp * Mathf.Clamp01((throttleCurTo0 + AIGlobals.ChopperThrottleDamper) *
                                beginStopDist / speedDownTime);
                        }
                        else if (speedDownTime < throttleCurTo0 + AIGlobals.ChopperThrottleDamper)
                        {
                            final += throttleLerp * Mathf.Clamp01((throttleCurTo0 + AIGlobals.ChopperThrottleDamper) *
                                beginStopDist / speedDownTime);
                        }
                        else
                        {
                            final -= throttleLerp * beginStopDist;
                        }
                    }
                }
                else
                {
                    if (velo < -0.25f)
                    {
                        final += throttleLerp;
                    }
                    else
                    {
                        deltaDists = Mathf.Abs(stopDist / deltaHeight);
                        beginStopDist = Mathf.Clamp01(1.1f - deltaDists);
                        speedDownTime = Mathf.Abs(velo / accel);
                        if (speedDownTime < throttleCurTo0)
                        {
                            final -= throttleLerp;
                        }
                        else if (speedDownTime < throttleCurTo0 + 0.5f)
                        {
                            final -= throttleLerp * Mathf.Clamp01((throttleCurTo0 + AIGlobals.ChopperThrottleDamper) *
                                beginStopDist / speedDownTime);
                        }
                        else if (speedDownTime < throttleCurTo0 + AIGlobals.ChopperThrottleDamper)
                        {
                            final -= throttleLerp * Mathf.Clamp01((throttleCurTo0 + AIGlobals.ChopperThrottleDamper) *
                                beginStopDist / speedDownTime);
                        }
                        else
                        {
                            final += throttleLerp * beginStopDist;
                        }
                    }
                }

            }
            else
                final = 0f;

            if (final > 1.25f && pilot.BoostBias.y > 0.65f)
                helper.FullBoost = true;
            return Mathf.Clamp(final, -0.1f, 1);
        }
        public static float ModerateUpwardsThrust_LEGACY(Tank tank, TankAIHelper helper, AIControllerAir pilot, float targetHeight, bool ForceUp = false)
        {
            pilot.LowerEngines = false;
            float dampScale = 4f / pilot.PropLerpValue;
            float mulVal = Mathf.Clamp01(8f / pilot.PropLerpValue);
            float final = ((targetHeight - tank.boundsCentreWorldNoCheck.y) * mulVal) + pilot.UpTtWRatio;
            if (ForceUp)
                final = 1;
            else
            {
                if (final > 1.6f)
                {
                }
                if (final > 1.2f)
                {
                    if (helper.SafeVelocity.y > 4f)
                    {
                        final = Mathf.Max(final - Mathf.Pow(Mathf.Max(helper.SafeVelocity.y - 4f, 0), 2) * dampScale, 0);
                    }
                }
                else if (final > 0.8f)
                {
                    if (helper.SafeVelocity.y > 2f)
                    {
                        final = Mathf.Max(final - Mathf.Pow(Mathf.Max(helper.SafeVelocity.y - 2f, 0), 2) * dampScale, 0);
                    }
                }
                else if (final > 0.4f)
                {
                    if (helper.SafeVelocity.y > 1f)
                    {
                        final = Mathf.Max(final - Mathf.Pow(Mathf.Max(helper.SafeVelocity.y - 1f, 0), 2) * dampScale * 0.8f, 0);
                    }
                }
                else if (final >= 0f)
                {
                    if (helper.SafeVelocity.y > 0f)
                    {
                        final = Mathf.Max(final - Mathf.Pow(helper.SafeVelocity.y, 2) * dampScale * 0.4f, 0);
                    }
                }
                else if (final > -0.1f)
                    final = -0.1f;
                else if (final > -0.4f)
                {
                    pilot.LowerEngines = true;
                    final = 0f;
                    if (helper.SafeVelocity.y < 0f)
                    {
                        final = Mathf.Pow(helper.SafeVelocity.y, 2) * dampScale * 0.4f;
                    }
                }
                else if (final > -0.8f)
                {
                    pilot.LowerEngines = true;
                    final = -0.1f;
                    if (helper.SafeVelocity.y < 0f)
                    {
                        final = Mathf.Pow(helper.SafeVelocity.y, 2) * dampScale * 0.8f;
                    }
                }
                else
                {
                    pilot.LowerEngines = true;
                    final = -0.1f;
                    if (helper.SafeVelocity.y < 0f)
                    {
                        final = Mathf.Pow(helper.SafeVelocity.y, 2) * dampScale;
                    }
                }
            }

            if (final > 1.25f && pilot.BoostBias.y > 0.65f)
                helper.FullBoost = true;
            else
                helper.FullBoost = helper.lastOperatorRange > AIGlobals.GroundAttackStagingDist / 3;

            return Mathf.Clamp(final, -0.1f, 1);
        }
        public static void UpdateThrottleCopter(AIControllerAir pilot)
        {
            TankAIHelper helper = pilot.Helper;
            if (pilot.NoProps)
            {
                if (pilot.MainThrottle > 0.1 && !AIEPathing.AboveHeightFromGroundTech(helper, helper.GroundOffsetHeight) && !pilot.Tank.beam.IsActive)
                    helper.MaxBoost();
            }
            else
            {
                if (pilot.ForcePitchUp)
                    helper.MaxProps();
                else if (helper.FullBoost)
                    helper.MaxBoost();
            }
            pilot.CurrentThrottle = Mathf.Clamp(pilot.MainThrottle, 0, 1);
        }
    }
}
