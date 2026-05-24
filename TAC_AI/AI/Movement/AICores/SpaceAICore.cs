using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    internal class SpaceAICore : IMovementAICore
    {
        private AIControllerDefault controller;
        private Tank tank;
        public float GetDrive => lastDrive;
        private float lastDrive = 0;

        public void Initiate(Tank tank, IMovementAIController controller)
        {
            this.controller = (AIControllerDefault)controller;
            this.controller.WaterPathing = WaterPathing.AllowWater;
            this.tank = tank;
            controller.Helper.GroundOffsetHeight = controller.Helper.lastTechExtents + AIGlobals.GroundOffsetGeneralAir;

            if (controller.Helper.Allied && controller.Helper.AutoAnchor)
            {
                if (tank.IsAnchored && !controller.Helper.PlayerAllowAutoAnchoring)
                    DebugTAC_AI.Log(KickStart.ModID + ": SpaceAICore - Should NOT be active when anchored UNLESS we have autoAnchor! StaticAICore should be in control!");
            }
            else if (tank.IsAnchored)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": SpaceAICore - Should NOT be active when anchored UNLESS we have autoAnchor! StaticAICore should be in control!");
            }
        }

        public Vector3 AvoidAssist(Vector3 targetIn, Vector3 predictionOffset)
        {
            throw new NotImplementedException();
        }

        public bool PlanningPathing(Vector3 Target, EDrivePathing aim)
        {
            float pathSuccessMulti = 1;

            controller.TargetDestination = WorldPosition.FromScenePosition(Target);
            switch (aim)
            {
                case EDrivePathing.IgnoreAll:
                case EDrivePathing.OnlyImmedeate:
                    controller.SetAutoPathfinding(false);
                    return false;
                case EDrivePathing.Path:
                    pathSuccessMulti = AIGlobals.AIPathingSuccessRad;
                    break;
                case EDrivePathing.PrecisePathIgnoreScenery:
                case EDrivePathing.PrecisePath:
                    pathSuccessMulti = AIGlobals.AIPathingSuccessRadPrecise;
                    break;
            }
            if (!AIEAutoPather.IsFarEnough(tank.boundsCentreWorldNoCheck, Target))
            {
                return false;
            }
            var helper = controller.Helper;
            if (!controller.AutoPathfind)
            {
                DebugTAC_AI.LogPathing(tank.name + ": PlanningPathing - Started pathfinding!");
                controller.WaterPathing = WaterPathing.AvoidWater;
                controller.SetAutoPathfinding(true);
            }
            if (controller.PathPlanned.Count > 0)
            {
                helper.AutoSpacing = 0;
                controller.PathPointSet = AIEPathing.OffsetFromGround(controller.PathPlanned.Peek().ScenePosition, helper, 1);
                if ((controller.PathPoint - tank.boundsCentreWorldNoCheck).WithinSquareXZ(tank.GetCheapBounds() * pathSuccessMulti))
                {
                    controller.PathPlanned.Dequeue();
                    DebugTAC_AI.LogPathing(tank.name + ": PlanningPathing - finished pathing to " + controller.PathPoint);
                    if (controller.PathPlanned.Count == 0)
                    {
                        DebugTAC_AI.LogPathing(tank.name + ": PlanningPathing - All Done!");
                        return false;
                    }
                    controller.PathPointSet = AIEPathing.OffsetFromGround(controller.PathPlanned.Peek().ScenePosition, helper, 1);
                }
                switch (aim)
                {
                    case EDrivePathing.Path:
                        controller.PathPointSet = helper.AvoidAssist(controller.PathPoint, helper.recentSpeed < helper.EstTopSped / AIGlobals.PlayerAISpeedPanicDividend);
                        break;
                    case EDrivePathing.PrecisePathIgnoreScenery:
                        controller.PathPointSet = helper.AvoidAssistPrecise(controller.PathPoint, helper.recentSpeed < helper.EstTopSped / AIGlobals.PlayerAISpeedPanicDividend, true);
                        break;
                    case EDrivePathing.PrecisePath:
                        controller.PathPointSet = helper.AvoidAssistPrecise(controller.PathPoint, helper.recentSpeed < helper.EstTopSped / AIGlobals.PlayerAISpeedPanicDividend);
                        break;
                }

                DebugTAC_AI.LogPathing(tank.name + ": PlanningPathing - Current pos is " + tank.boundsCentreWorldNoCheck + " and target is " + controller.PathPoint + " of waypoints left " + controller.PathPlanned.Count);

                return true;
            }
            return false;
        }
        public bool DriveDirectorRTS(ref EControlCoreSet core)
        {
            if (!VehicleUtils.GetPathingTargetRTS(controller, out Vector3 Target, ref core))
                return false;

            var helper = controller.Helper;
            if (!(helper.FullMelee && helper.lastEnemyGet.IsNotNull()))
                Target = AIEPathing.OffsetFromGroundH(Target, helper);
            controller.PathPointSet = AIEPathing.ModerateMaxAlt(Target, helper);

            if (PlanningPathing(Target, core.DrivePathing))
                return true;

            return ImmedeatePathing(Target, core.DrivePathing);
        }
        public bool DriveDirectorEnemyRTS(EnemyMind mind, ref EControlCoreSet core)
        {
            if (!VehicleUtils.GetPathingTargetRTSEnemy(controller, out Vector3 Target, ref core))
                return false;

            var helper = controller.Helper;
            if (!(helper.FullMelee && helper.lastEnemyGet.IsNotNull()))
                Target = AIEPathing.OffsetFromGroundH(Target, helper);
            controller.PathPointSet = AIEPathing.ModerateMaxAlt(Target, helper);

            if (!helper.Attempt3DNavi && PlanningPathing(Target, core.DrivePathing))
                return true;

            return ImmedeatePathing(Target, core.DrivePathing);
        }

        public bool DriveDirector(ref EControlCoreSet core)
        {
            if (!VehicleUtils.GetPathingTarget(controller, out Vector3 Target, ref core))
                return false;

            var helper = controller.Helper;
            if (!(helper.FullMelee && helper.lastEnemyGet.IsNotNull()))
                Target = AIEPathing.OffsetFromGroundH(Target, helper);
            controller.PathPointSet = AIEPathing.ModerateMaxAlt(Target, helper);

            if (PlanningPathing(Target, core.DrivePathing))
                return true;

            return ImmedeatePathing(Target, core.DrivePathing);
        }
        public bool ImmedeatePathing(Vector3 Target, EDrivePathing aim)
        {
            var helper = controller.Helper;

            switch (aim)
            {
                case EDrivePathing.IgnoreAll:
                    controller.PathPointSet = Target;
                    return true;
                case EDrivePathing.OnlyImmedeate:
                    break;
                case EDrivePathing.Path:
                    Target = helper.AvoidAssist(Target);
                    break;
                case EDrivePathing.PrecisePathIgnoreScenery:
                    Target = helper.AvoidAssistPrecise(Target, true, true);
                    break;
                case EDrivePathing.PrecisePath:
                    Target = helper.AvoidAssistPrecise(Target);
                    break;
            }

            if (!(helper.FullMelee && helper.lastEnemyGet.IsNotNull()))
                Target = AIEPathing.OffsetFromGroundH(Target, helper);

            controller.PathPointSet = AIEPathing.ModerateMaxAlt(Target, helper);
            DebugTAC_AI.LogPathing(tank.name + ": ImmedeatePathing - Current pos is " + tank.boundsCentreWorldNoCheck + " and target is " + controller.PathPoint);
            return true;
        }

        public bool DriveDirectorEnemy(EnemyMind mind, ref EControlCoreSet core)
        {
            if (!VehicleUtils.GetPathingTargetEnemy(controller, out Vector3 Target, ref core))
            {
                throw new NullReferenceException("DriveDirectorEnemy: GetPathingTargetEnemy returned false (target lookup failed or EnemyMind null) for " + controller.Tank?.name);
            }

            var helper = controller.Helper;
            Target = AIEPathing.OffsetFromGroundH(Target, helper);
            controller.PathPointSet = AIEPathing.ModerateMaxAlt(Target, helper);
            if (PlanningPathing(Target, core.DrivePathing))
                return true;

            return ImmedeatePathingEnemy(mind, Target, core.DrivePathing);
        }
        public bool ImmedeatePathingEnemy(EnemyMind mind, Vector3 Target, EDrivePathing aim)
        {
            var helper = controller.Helper;

            switch (aim)
            {
                case EDrivePathing.IgnoreAll:
                    controller.PathPointSet = Target;
                    return true;
                case EDrivePathing.OnlyImmedeate:
                    break;
                case EDrivePathing.Path:
                    Target = helper.AvoidAssist(Target);
                    break;
                case EDrivePathing.PrecisePathIgnoreScenery:
                    Target = helper.AvoidAssistPrecise(Target, true, true);
                    break;
                case EDrivePathing.PrecisePath:
                    Target = helper.AvoidAssistPrecise(Target);
                    break;
            }
            Target = AIEPathing.OffsetFromGroundH(Target, helper);

            controller.PathPointSet = AIEPathing.ModerateMaxAlt(Target, helper);
            DebugTAC_AI.LogPathing(tank.name + ": ImmedeatePathingEnemy - Current pos is " + tank.boundsCentreWorldNoCheck + " and target is " + controller.PathPoint);
            return true;
        }

        public bool DriveMaintainer(TankAIHelper helper, Tank tank, ref EControlCoreSet core)
        {
            float driveMulti = 1;

            Vector3 distDiff = controller.PathPoint - tank.boundsCentreWorldNoCheck;
            Vector3 turnVal;
            Vector3 forwardFlat = tank.rootBlockTrans.forward;
            forwardFlat.y = 0;
            forwardFlat = forwardFlat.normalized;
            if (helper.Navi3DDirect == Vector3.zero)
            {
                if (core.DriveDir == EDriveFacing.Backwards)
                    turnVal = AIGlobals.LookRot(tank.rootBlockTrans.InverseTransformDirection(-forwardFlat.normalized), tank.rootBlockTrans.InverseTransformDirection(Vector3.up)).eulerAngles;
                else
                    turnVal = AIGlobals.LookRot(tank.rootBlockTrans.InverseTransformDirection(forwardFlat.normalized), tank.rootBlockTrans.InverseTransformDirection(Vector3.up)).eulerAngles;

                turnVal.x = -AIGlobals.AngleUnsignedToSigned(turnVal.x) / 180f;

                turnVal.z = -AIGlobals.AngleUnsignedToSigned(turnVal.z) / 180f;

                turnVal.y = 0;

            }
            else
            {
                if (core.DriveDir == EDriveFacing.Backwards)
                    turnVal = AIGlobals.LookRot(tank.rootBlockTrans.InverseTransformDirection(-helper.Navi3DDirect), tank.rootBlockTrans.InverseTransformDirection(helper.Navi3DUp)).eulerAngles;
                else
                    turnVal = AIGlobals.LookRot(tank.rootBlockTrans.InverseTransformDirection(helper.Navi3DDirect), tank.rootBlockTrans.InverseTransformDirection(helper.Navi3DUp)).eulerAngles;

                Vector3 turnValUp = AIGlobals.LookRot(tank.rootBlockTrans.InverseTransformDirection(forwardFlat.normalized), tank.rootBlockTrans.InverseTransformDirection(Vector3.up)).eulerAngles;
                if (helper.Navi3DUp == Vector3.up)
                {
                    if (!helper.FullMelee && Vector3.Dot(helper.Navi3DDirect, tank.rootBlockTrans.forward) < 0.6f)
                    {
                        turnVal.x = turnValUp.x;
                        turnVal.x = -AIGlobals.AngleUnsignedToSigned(turnVal.x) / 180f;
                    }
                    else
                    {
                        turnVal.x = Mathf.Clamp(-AIGlobals.AngleUnsignedToSigned(turnVal.x) / 60f, -1, 1);
                    }
                    turnVal.z = turnValUp.z;
                    turnVal.z = -AIGlobals.AngleUnsignedToSigned(turnVal.z) / 180f;
                }
                else
                {
                    if (!helper.FullMelee && Vector3.Dot(helper.Navi3DUp, tank.rootBlockTrans.up) < 0.6f)
                    {
                        turnVal.z = turnValUp.z;
                        turnVal.z = -AIGlobals.AngleUnsignedToSigned(turnVal.z) / 180f;
                    }
                    else
                    {
                        turnVal.z = Mathf.Clamp(-AIGlobals.AngleUnsignedToSigned(turnVal.z) / 60f, -1, 1);
                    }
                    turnVal.x = turnValUp.x;
                    turnVal.x = Mathf.Clamp(-AIGlobals.AngleUnsignedToSigned(turnVal.x) / 60f, -1, 1);
                }

                turnVal.y = Mathf.Clamp(-AIGlobals.AngleUnsignedToSigned(turnVal.y) / 60f, -1, 1);

            }

            helper.Navi3DDirect = Vector3.zero;
            helper.Navi3DUp = Vector3.up;
            Vector3 TurnVal = Vector3.zero;
            if (helper.DoSteerCore)
            {
                if (helper.AdviseAwayCore)
                {
                    if (core.DriveDir == EDriveFacing.Perpendicular)
                    {
                        TurnVal = turnVal.Clamp01Box();
                        if (helper.lastEnemyGet.IsNotNull())
                        {
                            helper.Navi3DDirect = helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck;
                        }
                        else
                        {
                            VehicleUtils.TurnerHovership(tank.control, helper, -distDiff, ref core);
                        }
                    }
                    else if (core.DriveDir == EDriveFacing.Forwards)
                    {
                        TurnVal = turnVal.Clamp01Box();
                        if (helper.lastEnemyGet.IsNotNull())
                        {
                            helper.Navi3DDirect = helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck;
                        }
                        else
                        {
                            VehicleUtils.TurnerHovership(tank.control, helper, distDiff, ref core);
                        }
                    }
                    else if (core.DriveDir == EDriveFacing.Backwards)
                    {
                        TurnVal = turnVal.Clamp01Box();
                        VehicleUtils.TurnerHovership(tank.control, helper, -distDiff, ref core);
                    }
                    else
                    {
                        TurnVal.y = 0;
                    }
                }
                else
                {
                    if (core.DriveDir == EDriveFacing.Perpendicular)
                    {
                        TurnVal = turnVal.Clamp01Box();
                        if (helper.lastEnemyGet.IsNotNull())
                        {
                            if (Vector3.Dot(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck, tank.rootBlockTrans.right) < 0)
                            {
                                helper.Navi3DDirect = Vector3.Cross(Vector3.up, (helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).normalized).normalized;
                                helper.Navi3DUp = Vector3.Cross((helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).normalized, helper.Navi3DDirect).normalized;
                            }
                            else
                            {
                                helper.Navi3DDirect = Vector3.Cross((helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).normalized, Vector3.up).normalized;
                                helper.Navi3DUp = Vector3.Cross(helper.Navi3DDirect, (helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).normalized).normalized;
                            }
                        }
                        else
                        {
                            VehicleUtils.TurnerHovership(tank.control, helper, distDiff, ref core);
                        }
                    }
                    else if (core.DriveDir == EDriveFacing.Backwards)
                    {
                        TurnVal = turnVal.Clamp01Box();
                        VehicleUtils.TurnerHovership(tank.control, helper, -distDiff, ref core);
                    }
                    else if (core.DriveDir == EDriveFacing.Forwards)
                    {
                        TurnVal = turnVal.Clamp01Box();
                        if (helper.lastEnemyGet.IsNotNull())
                        {
                            helper.Navi3DDirect = helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck;
                        }
                        else
                        {
                            helper.Navi3DDirect = controller.PathPoint - tank.boundsCentreWorldNoCheck;
                        }
                    }
                    else
                    {
                        TurnVal = (turnVal * Mathf.Clamp(1 - Vector3.Dot(turnVal, tank.rootBlockTrans.forward), 0, 1)).Clamp01Box();
                        VehicleUtils.TurnerHovership(tank.control, helper, distDiff, ref core);
                    }
                }
            }
            else
                TurnVal = Vector3.zero;

            bool EmergencyUp = false;
            bool CloseToGroundWarning = false;
            if (!helper.IsMultiTech)
            {
                float height = helper.GetFrameHeight();
                if (height > tank.boundsCentreWorldNoCheck.y - helper.lastTechExtents)
                {
                    EmergencyUp = true;
                    CloseToGroundWarning = true;
                }
                else if (height > tank.boundsCentreWorldNoCheck.y - (helper.lastTechExtents * 2))
                {
                    CloseToGroundWarning = true;
                }
            }

            Vector3 driveVal;
            if (helper.AdviseAwayCore)
            {
                if (helper.lastEnemyGet.IsNotNull() && AIEPathing.IsUnderMaxAltPlayer(tank.boundsCentreWorldNoCheck.y))
                {
                    driveVal = InertiaTranslation(tank.rootBlockTrans.InverseTransformVector(InvertHorizontalPlane(distDiff.normalized)));

                    if (!CloseToGroundWarning)
                    {
                        if (helper.AIAlign == AIAlignment.Player && helper.lastPlayer.IsNotNull())
                        {
                            float playerOffsetH = helper.lastPlayer.tank.boundsCentreWorldNoCheck.y + helper.GroundOffsetHeight;
                            float leveler = Mathf.Clamp((playerOffsetH - tank.boundsCentreWorldNoCheck.y) / 20, -1, 1);
                            if (leveler > -1f)
                                driveVal.y = leveler;
                            else
                                driveVal.y = -1f;
                        }
                        else
                        {
                            float enemyOffsetH = helper.lastEnemyGet.tank.boundsCentreWorldNoCheck.y + helper.lastEnemyGet.tank.GetCheapBounds() + helper.GroundOffsetHeight;
                            float leveler = Mathf.Clamp((enemyOffsetH - tank.boundsCentreWorldNoCheck.y) / 6, -1, 1);
                            if (leveler > -1f)
                                driveVal.y = leveler;
                            else
                                driveVal.y = -1f;
                        }
                    }
                }
                else
                {
                    driveVal = InertiaTranslation(tank.rootBlockTrans.InverseTransformVector(InvertHorizontalPlane(distDiff.normalized)));
                }
            }
            else
            {
                if (helper.lastEnemyGet.IsNotNull() && !helper.IsMultiTech && AIEPathing.IsUnderMaxAltPlayer(tank.boundsCentreWorldNoCheck.y))
                {
                    driveVal = InertiaTranslation(tank.rootBlockTrans.InverseTransformVector(distDiff));
                    if (!CloseToGroundWarning)
                    {
                        if (helper.AIAlign == AIAlignment.Player && helper.lastPlayer.IsNotNull())
                        {
                            float playerOffsetH = helper.lastPlayer.tank.boundsCentreWorldNoCheck.y + helper.GroundOffsetHeight;
                            float leveler = Mathf.Clamp((playerOffsetH - tank.boundsCentreWorldNoCheck.y) / 20, -1, 1);
                            if (leveler > -1f)
                                driveVal.y = leveler;
                            else
                                driveVal.y = -1f;
                        }
                        else
                        {
                            float enemyOffsetH = helper.lastEnemyGet.tank.boundsCentreWorldNoCheck.y + helper.lastEnemyGet.tank.GetCheapBounds() + helper.GroundOffsetHeight;
                            float leveler = Mathf.Clamp((enemyOffsetH - tank.boundsCentreWorldNoCheck.y) / 6, -1, 1);
                            if (leveler > -1f)
                                driveVal.y = leveler;
                            else
                                driveVal.y = -1f;
                        }
                    }
                }
                else
                {
                    float range = helper.lastOperatorRange;
                    if (range < helper.AutoSpacing - 1)
                    {
                        driveVal = InertiaTranslation(tank.rootBlockTrans.InverseTransformVector(InvertHorizontalPlane(distDiff.normalized)) * 0.3f);
                    }
                    else if (range > helper.AutoSpacing + 1)
                    {
                        driveVal = InertiaTranslation(tank.rootBlockTrans.InverseTransformVector(distDiff));
                        if (core.DriveDir == EDriveFacing.Forwards || core.DriveDir == EDriveFacing.Backwards)
                            driveMulti = 1f;
                        else
                            driveMulti = 0.4f;
                    }
                    else
                        driveVal = InertiaTranslation(tank.rootBlockTrans.InverseTransformVector(distDiff));
                }
            }

            if (CloseToGroundWarning)
            {
                if (driveVal.y >= -0.3f && driveVal.y < 0f)
                    driveVal.y = 0;
                else if (driveVal.y != -1)
                {
                    driveVal.y += 0.5f;
                }
            }

            switch (helper.ThrottleState)
            {
                case AIThrottleState.PivotOnly:
                    driveVal.x = 0;
                    driveVal.z = 0;
                    break;
                case AIThrottleState.Yield:
                    driveVal = RegulateSpeed(driveVal, helper, ref core);
                    break;
                case AIThrottleState.FullSpeed:
                    if (helper.FullBoost)
                    {
                        driveMulti = 1;
                        if (helper.IsMultiTech || Vector3.Dot(driveVal, tank.rootBlockTrans.forward) > 0.75f)
                            helper.MaxBoost();
                    }
                    else if (helper.LightBoost)
                    {
                        if (helper.lightBoostFeatherTimer.Due)
                        {
                            if (helper.IsMultiTech || Vector3.Dot(driveVal, tank.rootBlockTrans.forward) > 0.75f)
                                helper.MaxBoost();
                            helper.lightBoostFeatherTimer.Set(0.5f);
                        }
                    }
                    break;
                case AIThrottleState.ForceSpeed:
                    driveMulti = Mathf.Abs(helper.DriveVar);
                    break;
                default:
                    break;
            }

            if (helper.FirePROPS)
                helper.MaxProps();

            Vector3 DriveVal;

            if (EmergencyUp)
            {
                DriveVal = (tank.rootBlockTrans.InverseTransformVector(Vector3.up) * 2).Clamp01Box();

                if (AIGlobals.ShowDebugFeedBack)
                {
                    DebugExtUtilities.DrawDirIndicator(tank.gameObject, 0, driveVal * helper.lastTechExtents, new Color(0, 0, 1));
                    DebugExtUtilities.DrawDirIndicator(tank.gameObject, 1, DriveVal * helper.lastTechExtents, new Color(1, 0, 0));
                }
                helper.ProcessControl(DriveVal, TurnVal, Vector3.zero, false, false);
                return true;
            }
            Vector3 final = (driveVal * driveMulti * Mathf.Clamp(distDiff.magnitude / 5, 0, 1)).Clamp01Box();
            final.x = final.x * AIGlobals.HovershipHorizontalDriveMulti;
            final.z = final.z * AIGlobals.HovershipHorizontalDriveMulti;
            if (final.y > 0)
                final.y = final.y * AIGlobals.HovershipUpDriveMulti;
            else
                final.y = final.y * AIGlobals.HovershipDownDriveMulti;

            if (core.DriveDir > EDriveFacing.Neutral)
            {
                if (final.y.Approximately(0, 0.4f))
                    final.y = 0;
                if (final.x.Approximately(0, 0.35f))
                    final.x = 0;
                if (final.z.Approximately(0, 0.35f))
                    final.z = 0;
            }
            DriveVal = final.Clamp01Box();

            if (AIGlobals.ShowDebugFeedBack)
            {
                if (!tank.IsAnchored)
                {
                    DebugExtUtilities.DrawDirIndicator(tank.gameObject, 0, distDiff, new Color(0, 1, 1));
                    DebugExtUtilities.DrawDirIndicator(tank.gameObject, 1, tank.rootBlockTrans.TransformVector(driveVal * helper.lastTechExtents * 2), new Color(0, 0, 1));
                    DebugExtUtilities.DrawDirIndicator(tank.gameObject, 2, tank.rootBlockTrans.TransformVector(DriveVal * helper.lastTechExtents * 2), new Color(1, 0, 0));
                }
                else if (helper.WantsToFight && helper.lastEnemyGet)
                {
                    if (ManBaseTeams.IsEnemy(tank.Team, helper.lastEnemyGet.tank.Team))
                        DebugExtUtilities.DrawDirIndicator(tank.gameObject, 0, helper.lastEnemyGet.centrePosition - tank.trans.position, new Color(0, 1, 1));
                }
            }
            lastDrive = DriveVal.z;
            if (helper.FixControlReversal(DriveVal.z))
                helper.ProcessControl(DriveVal, TurnVal.SetY(-TurnVal.y), Vector3.zero, false, false);
            else
                helper.ProcessControl(DriveVal, TurnVal, Vector3.zero, false, false);
            return true;
        }

        public bool TryAdjustForCombat(bool between, ref Vector3 pos, ref EControlCoreSet core)
        {
            TankAIHelper helper = controller.Helper;
            bool output = false;
            if (helper.ChaseThreat && !helper.Retreat && helper.lastEnemyGet.IsNotNull())
            {
                Vector3 targPos = helper.InterceptTargetDriving(helper.lastEnemyGet);
                output = true;
                core.DriveDir = EDriveFacing.Forwards;
                if (between && helper.theResource.IsNotNull() && helper.theResource.tank.IsNotNull())
                {
                    targPos = Between(targPos, helper.theResource.tank.boundsCentreWorldNoCheck);
                }
                helper.UpdateEnemyDistance(targPos);
                float driveDyna = Mathf.Clamp((helper.lastCombatRange - helper.MinCombatRange) / 3f, -1, 1);
                if (helper.SideToThreat)
                {
                    core.DriveDir = EDriveFacing.Perpendicular;
                    if (helper.FullMelee)
                    {
                        pos = targPos;
                        helper.AutoSpacing = 0;
                    }
                    else if (driveDyna == 1)
                    {
                        pos = helper.AvoidAssist(targPos);
                        helper.AutoSpacing = helper.lastTechExtents + helper.lastEnemyGet.GetCheapBounds() + 3;
                    }
                    else if (driveDyna < 0)
                    {
                        core.DriveDir = EDriveFacing.Backwards;
                        pos = helper.AvoidAssist(targPos);
                        helper.AutoSpacing = helper.lastTechExtents + helper.lastEnemyGet.GetCheapBounds() + 3;
                    }
                    else
                    {
                        pos = helper.AvoidAssist(targPos);
                        helper.AutoSpacing = helper.lastTechExtents + helper.lastEnemyGet.GetCheapBounds() + 3;
                    }
                }
                else
                {
                    core.DriveDir = EDriveFacing.Forwards;
                    if (helper.FullMelee)
                    {
                        pos = targPos;
                        helper.AutoSpacing = 0;
                    }
                    else if (driveDyna == 1)
                    {
                        pos = helper.AvoidAssist(targPos);
                        helper.AutoSpacing = helper.lastTechExtents + helper.lastEnemyGet.GetCheapBounds() + 5;
                    }
                    else if (driveDyna < 0)
                    {
                        core.DriveDest = EDriveDest.FromLastDestination;
                        pos = helper.AvoidAssist(targPos);
                        helper.AutoSpacing = 0.5f;
                    }
                    else
                    {
                        pos = helper.AvoidAssist(targPos);
                        helper.AutoSpacing = helper.lastTechExtents + helper.lastEnemyGet.GetCheapBounds() + 3;
                    }
                }
            }
            else
                helper.IgnoreEnemyDistance();
            controller.PathPointSet = pos;
            return output;
        }

        public bool TryAdjustForCombatEnemy(EnemyMind mind, ref Vector3 pos, ref EControlCoreSet core)
        {
            TankAIHelper helper = controller.Helper;
            bool output = false;
            if (!helper.Retreat && helper.lastEnemyGet.IsNotNull() && mind.CommanderMind != EnemyAttitude.OnRails)
            {
                output = true;
                core.DriveDir = EDriveFacing.Forwards;
                helper.UpdateEnemyDistance(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                float driveDyna = Mathf.Clamp((helper.lastCombatRange - mind.MinCombatRange) / 3f, -1, 1);

                if (mind.CommanderAttack == EAttackMode.Circle)
                {
                    if (helper.SideToThreat)
                        core.DriveDir = EDriveFacing.Perpendicular;
                    else
                        core.DriveDir = EDriveFacing.Forwards;
                    if (mind.CommanderMind == EnemyAttitude.Miner)
                    {
                        pos = RCore.GetTargetCoordinates(helper, helper.lastEnemyGet, mind);
                        helper.AutoSpacing = 0;
                    }
                    else if (driveDyna == 1)
                    {
                        pos = helper.AvoidAssist(RCore.GetTargetCoordinates(helper, helper.lastEnemyGet, mind));
                    }
                    else if (driveDyna < 0)
                    {
                        core.DriveDest = EDriveDest.FromLastDestination;
                        pos = helper.AvoidAssist(RCore.GetTargetCoordinates(helper, helper.lastEnemyGet, mind));
                    }
                    else
                    {
                        pos = helper.AvoidAssist(RCore.GetTargetCoordinates(helper, helper.lastEnemyGet, mind));
                    }
                }
                else
                {
                    if (helper.IsDirectedMovingFromDest)
                    {
                        core.DriveAwayFacingTowards();
                        pos = helper.AvoidAssist(RCore.GetTargetCoordinates(helper, helper.lastEnemyGet, mind));
                        helper.AutoSpacing = 0.5f;
                    }
                    else if (helper.IsDirectedMovingToDest)
                    {
                        if (mind.LikelyMelee)
                        {
                            core.DriveToFacingTowards();
                            pos = helper.AvoidAssist(RCore.GetTargetCoordinates(helper, helper.lastEnemyGet, mind));
                            helper.AutoSpacing = 0.5f;
                        }
                        else
                        {
                            core.DriveToFacingTowards();
                            pos = helper.AvoidAssist(RCore.GetTargetCoordinates(helper, helper.lastEnemyGet, mind));
                            helper.AutoSpacing = helper.lastTechExtents + helper.lastEnemyGet.GetCheapBounds() + 5;
                        }
                    }
                    else
                    {
                        pos = helper.AvoidAssist(RCore.GetTargetCoordinates(helper, helper.lastEnemyGet, mind));
                        helper.AutoSpacing = helper.lastTechExtents + helper.lastEnemyGet.GetCheapBounds() + 5;
                    }
                }
            }
            else
                helper.IgnoreEnemyDistance();
            controller.PathPointSet = pos;
            return output;
        }

        public Vector3 Between(Vector3 Target, Vector3 other)
        {
            return (Target + other) / 2;
        }

        private const float throttleDampen = 0.5f;
        private const float DampeningStrength = 0.75f;
        public Vector3 InertiaTranslation(Vector3 direction)
        {
            Tank tank = controller.Tank;
            if (tank.rbody == null)
                return direction;

            if (controller.Helper.AdvancedAI)
            {
                return direction + Vector3.ProjectOnPlane(-controller.Helper.LocalSafeVelocity * DampeningStrength, direction);
            }
            return direction * throttleDampen;
        }
        public Vector3 RegulateSpeed(Vector3 input, TankAIHelper helper, ref EControlCoreSet core)
        {
            if (helper.recentSpeed > AIGlobals.YieldSpeed)
                return (-controller.Helper.LocalSafeVelocity * DampeningStrength).SetY(input.y);
            else
                return input;
        }
        public Vector3 InvertHorizontalPlane(Vector3 direction)
        {
            direction.x = -direction.x * 150;
            direction.z = -direction.z * 150;
            return direction;
        }
    }
}
