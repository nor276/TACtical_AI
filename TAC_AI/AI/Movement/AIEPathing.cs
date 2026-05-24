using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.World;

namespace TAC_AI.AI.Movement
{
    internal static class AIEPathing
    {
        internal static HashSet<Tank> AllyList(Tank tank)
        {
            HashSet<Tank> transfer = TankAIManager.GetNonEnemyTanks(tank.Team);
            if (transfer == null)
                throw new NullReferenceException("AllyList unable to secure HashSet<Tank> of Techs to target?");
            return transfer;
        }

        public const float ShipDepth = -3;
        public const float DefaultExtraSpacing = 2;


        private static List<Visible> ObstList = new List<Visible>();
        internal static List<Visible> ObstructionAwareness(Vector3 posWorld, TankAIHelper helper, float radAdd = DefaultExtraSpacing, bool ignoreDestructable = false)
        {
            ObstList.Clear();
            try
            {
                if (ignoreDestructable)
                {
                    foreach (Visible vis in Singleton.Manager<ManVisible>.inst.VisiblesTouchingRadius(posWorld, helper.lastTechExtents + radAdd, AIGlobals.sceneryBitMask))
                    {
                        if (vis.resdisp.IsNotNull() && vis.isActive && vis.damageable.Invulnerable
                            && AIECore.IndestructableScenery.Contains(vis.resdisp.GetSceneryType()))
                        {
                            ObstList.Add(vis);
                        }
                    }
                }
                else
                {
                    foreach (Visible vis in Singleton.Manager<ManVisible>.inst.VisiblesTouchingRadius(posWorld, helper.lastTechExtents + radAdd, AIGlobals.sceneryBitMask))
                    {
                        if (vis.resdisp.IsNotNull() && vis.isActive)
                        {
                            ObstList.Add(vis);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Error on ObstructionAwareness");
                DebugTAC_AI.Log(e);
            }
            return ObstList;
        }
        public static bool ObstructionAwarenessAny(Vector3 posWorld, TankAIHelper helper, float radius)
        {
            try
            {
                foreach (Visible vis in Singleton.Manager<ManVisible>.inst.VisiblesTouchingRadius(posWorld, radius, AIGlobals.sceneryBitMask))
                {
                    if (vis.resdisp.IsNotNull() && vis.isActive)
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Error on ObstructionAwarenessAny");
                DebugTAC_AI.Log(e);
            }
            return false;
        }

        private static Vector3 ObstOtherDir(Tank tank, TankAIHelper helper, Visible vis)
        {
            Vector3 inputOffset = tank.transform.position - vis.centrePosition;
            float inputSpacing = vis.Radius + helper.lastTechExtents + helper.DodgeStrength;
            Vector3 Final = (inputOffset.normalized * inputSpacing) + tank.transform.position;
            return Final;
        }
        public static Vector3 ObstDodgeOffset(Tank tank, TankAIHelper helper, bool DoDodge, out bool worked, bool useTwo = false, bool ignoreDestructable = false)
        {
            if (helper.IsDirectedMovingFromDest)
                return ObstDodgeOffsetInv(tank, helper, DoDodge, out worked, useTwo, ignoreDestructable);
            worked = false;
            if (!DoDodge || KickStart.AIDodgeCheapness >= 75 || helper.DriveDestDirected == EDriveDest.ToMine || helper.DriveDestDirected == EDriveDest.ToBase)
                return Vector3.zero;
            Vector3 Offset = Vector3.zero;

            if (tank.rbody == null)
                return Vector3.zero;

            List<Visible> ObstList = ObstructionAwareness(tank.boundsCentreWorldNoCheck + helper.SafeVelocity, helper, 2, ignoreDestructable);
            try
            {
                int bestStep = 0;
                int auxStep = 0;
                float bestValue = 1500;
                float auxBestValue = 1500;
                int steps = ObstList.Count;
                bool moreThan2 = false;
                if (steps <= 0)
                    return Vector3.zero;
                else if (steps > 1)
                    moreThan2 = true;
                for (int stepper = 0; steps > stepper; stepper++)
                {
                    float dist = (ObstList.ElementAt(stepper).centrePosition - tank.boundsCentreWorldNoCheck).magnitude;
                    float temp = Mathf.Max(dist - ObstList.ElementAt(stepper).Radius, 0f);
                    if (bestValue > temp)
                    {
                        auxStep = bestStep;
                        bestStep = stepper;
                        auxBestValue = bestValue;
                        bestValue = temp;
                    }
                    else if (useTwo && bestValue < temp && auxBestValue > temp)
                    {
                        auxStep = stepper;
                        auxBestValue = temp;
                    }
                }
                helper.ThrottleState = AIThrottleState.Yield;
                worked = true;
                if (useTwo && moreThan2)
                {
                    if (ObstructionAwarenessSetPiece(tank.boundsCentreWorldNoCheck + helper.SafeVelocity, tank, helper, out Vector3 posMon))
                        Offset = (ObstOtherDir(tank, helper, ObstList.ElementAt(bestStep)) + ObstOtherDir(tank, helper, ObstList.ElementAt(auxStep)) + posMon) / 3;
                    else
                        Offset = (ObstOtherDir(tank, helper, ObstList.ElementAt(bestStep)) + ObstOtherDir(tank, helper, ObstList.ElementAt(auxStep))) / 2;
                }
                else
                {
                    if (ObstructionAwarenessSetPiece(tank.boundsCentreWorldNoCheck + helper.SafeVelocity, tank, helper, out Vector3 posMon))
                        Offset = (ObstOtherDir(tank, helper, ObstList.ElementAt(bestStep)) + posMon) / 2;
                    else
                        Offset = ObstOtherDir(tank, helper, ObstList.ElementAt(bestStep));
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Error on ObstDodgeOffset");
                DebugTAC_AI.Log(e);
            }
            return Offset;
        }

        private static Vector3 ObstDir(Tank tank, TankAIHelper helper, Visible vis)
        {
            Vector3 inputOffset = tank.transform.position - vis.centrePosition;
            float inputSpacing = vis.Radius + helper.lastTechExtents + helper.DodgeStrength;
            Vector3 Final = -(inputOffset.normalized * inputSpacing) + tank.transform.position;
            return Final;
        }
        private static Vector3 ObstDodgeOffsetInv(Tank tank, TankAIHelper helper, bool DoDodge, out bool worked, bool useTwo = false, bool ignoreDestructable = false)
        {
            worked = false;
            if (!DoDodge || KickStart.AIDodgeCheapness >= 60 || helper.DriveDestDirected == EDriveDest.ToMine || helper.DriveDestDirected == EDriveDest.ToBase)
                return Vector3.zero;
            Vector3 Offset = Vector3.zero;

            if (tank.rbody == null)
                return Vector3.zero;

            List<Visible> ObstList = ObstructionAwareness(tank.boundsCentreWorldNoCheck + helper.SafeVelocity, helper, 2, ignoreDestructable);
            try
            {
                int bestStep = 0;
                int auxStep = 0;
                float bestValue = 1500;
                float auxBestValue = 1500;
                int steps = ObstList.Count;
                bool moreThan2 = false;
                if (steps <= 0)
                    return Vector3.zero;
                else if (steps > 1)
                    moreThan2 = true;
                for (int stepper = 0; steps > stepper; stepper++)
                {
                    float dist = (ObstList.ElementAt(stepper).centrePosition - tank.boundsCentreWorldNoCheck).magnitude;
                    float temp = Mathf.Max(dist - ObstList.ElementAt(stepper).Radius, 0f);
                    if (bestValue > temp)
                    {
                        auxStep = bestStep;
                        bestStep = stepper;
                        auxBestValue = bestValue;
                        bestValue = temp;
                    }
                    else if (useTwo && bestValue < temp && auxBestValue > temp)
                    {
                        auxStep = stepper;
                        auxBestValue = temp;
                    }
                }
                helper.ThrottleState = AIThrottleState.Yield;
                worked = true;
                if (useTwo && moreThan2)
                {
                    if (ObstructionAwarenessSetPiece(tank.boundsCentreWorldNoCheck + helper.SafeVelocity, tank, helper, out Vector3 posMon, true))
                        Offset = (ObstDir(tank, helper, ObstList.ElementAt(bestStep)) + ObstDir(tank, helper, ObstList.ElementAt(auxStep)) + posMon) / 3;
                    else
                        Offset = (ObstDir(tank, helper, ObstList.ElementAt(bestStep)) + ObstDir(tank, helper, ObstList.ElementAt(auxStep))) / 2;
                }
                else
                {
                    if (ObstructionAwarenessSetPiece(tank.boundsCentreWorldNoCheck + helper.SafeVelocity, tank, helper, out Vector3 posMon, true))
                        Offset = (ObstDir(tank, helper, ObstList.ElementAt(bestStep)) + posMon) / 2;
                    else
                        Offset = ObstDir(tank, helper, ObstList.ElementAt(bestStep));
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Error on ObstDodgeOffset");
                DebugTAC_AI.Log(e);
            }
            return Offset;
        }

        public static bool ObstructionAwarenessSetPiece(Vector3 posScene, Tank tank, TankAIHelper helper, out Vector3 pos, bool invert = false)
        {
            pos = Vector3.zero;
            ManWorld world = Singleton.Manager<ManWorld>.inst;
            if (!helper.tank.IsAnchored && TankAIManager.SetPieces.Count > 0)
            {
                List<ManWorld.TerrainSetPiecePlacement> ObstList = TankAIManager.SetPieces;
                float inRange = 270;
                bool isInRange = false;
                foreach (var item in ObstList)
                {
                    if ((item.m_WorldPosition.ScenePosition - posScene).WithinSquareXZ(inRange))
                    {
                        isInRange = true;
                        break;
                    }
                }
                if (!isInRange)
                {
                    if (world.CheckIfInsideSceneryBlocker(SceneryBlocker.BlockMode.Spawn, posScene, helper.lastTechExtents + 12))
                    {
                        if (world.LandmarkSpawner.GetNearestBlocker(posScene, out Vector3 landmarkWorld))
                        {
                            pos = landmarkWorld;
                            return true;
                        }
                    }
                    return false;
                }
                try
                {
                    LayerMask monuments = Globals.inst.layerLandmark.mask;
                    Ray ray = new Ray(posScene, helper.tank.rootBlockTrans.forward);
                    Physics.Raycast(ray, out RaycastHit hitInfo, world.TileSize, monuments, QueryTriggerInteraction.Collide);
                    if ((bool)hitInfo.collider)
                    {
                        if (hitInfo.collider.GetComponent<TerrainSetPiece>())
                        {
                            TerrainSetPiece piece = hitInfo.collider.GetComponent<TerrainSetPiece>();
                            if (invert)
                            {
                                pos = ObstDirSetPiece(tank, helper, posScene, piece);
                            }
                            else
                            {
                                pos = ObstOtherDirSetPiece(tank, helper, posScene, piece);
                            }
                            return true;
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": Error on ObstructionAwarenessMonument");
                    DebugTAC_AI.Log(e);
                }
            }
            return false;
        }
        public static bool ObstructionAwarenessSetPieceAny(Vector3 posScene, TankAIHelper helper, float radius)
        {
            if (!helper.tank.IsAnchored && ManWorld.inst.GetSetPiecePlacement().Count > 0)
            {
                if (ManWorld.inst.CheckIfInsideSceneryBlocker(SceneryBlocker.BlockMode.Spawn, posScene, radius))
                {
                    return true;
                }
            }
            return false;
        }
        public static bool ObstructionAwarenessTerrain(Vector3 posScene, TankAIHelper helper, float radius)
        {
            if (!helper.tank.IsAnchored)
            {
                float height = AIEPathMapper.GetHighestAltInRadius(posScene, radius, false);
                if (height > posScene.y - radius)
                    return true;
            }
            return false;
        }
        public static Vector3 ObstOtherDirSetPiece(Tank tank, TankAIHelper helper, Vector3 pos, TerrainSetPiece vis)
        {
            Vector3 inputOffset = tank.transform.position - pos;
            float inputSpacing = vis.GetApproxCellRadius() + helper.lastTechExtents + helper.DodgeStrength;
            Vector3 Final = (inputOffset.normalized * inputSpacing) + tank.transform.position;
            return Final;
        }
        public static Vector3 ObstDirSetPiece(Tank tank, TankAIHelper helper, Vector3 pos, TerrainSetPiece vis)
        {
            Vector3 inputOffset = tank.transform.position - pos;
            float inputSpacing = vis.GetApproxCellRadius() + helper.lastTechExtents + helper.DodgeStrength;
            Vector3 Final = -(inputOffset.normalized * inputSpacing) + tank.transform.position;
            return Final;
        }

        private static bool AvoidInvalidOrIgnoreable(Tank tank)
        {
            if (tank != null && tank.visible.isActive)
            {
                TankAIHelper help = tank.GetHelperInsured();
                return (help.IsMultiTech && tank.IsAnchored) || help.DediAI == AIType.Aegis;
            }
            return true;
        }
        private static bool FollowInvalidOrIgnoreable(Tank tank)
        {
            if (tank != null && tank.visible.isActive)
            {
                TankAIHelper help = tank.GetHelperInsured();
                return help.IsMultiTech || help.DediAI == AIType.Aegis;
            }
            return true;
        }
        public static Tank ClosestAlly(IEnumerable<Tank> AlliesAlt, Vector3 tankPos, out float bestValue, TankAIHelper thisTank)
        {
            bestValue = 500;
            Tank closestTank = null;
            try
            {
                foreach (Tank otherTech in AlliesAlt)
                {
                    if (AvoidInvalidOrIgnoreable(otherTech) || thisTank.tank == otherTech ||
                        thisTank.MultiTechsAffiliated.Contains(otherTech))
                        continue;
                    float temp = (otherTech.boundsCentreWorldNoCheck - tankPos).sqrMagnitude;
                    if (bestValue > temp)
                    {
                        bestValue = temp;
                        closestTank = otherTech;
                    }
                }
                if (closestTank != null)
                    bestValue = (closestTank.boundsCentreWorldNoCheck - tankPos).magnitude;
            }
            catch
            {
            }
            return closestTank;
        }
        public static Tank ClosestAllyPrecision(IEnumerable<Tank> AlliesAlt, Vector3 tankPos, out float bestValue, TankAIHelper thisTank)
        {
            bestValue = 500;
            Tank closestTank = null;
            try
            {
                foreach (Tank otherTech in AlliesAlt)
                {
                    if (AvoidInvalidOrIgnoreable(otherTech) || thisTank.tank == otherTech ||
                        thisTank.MultiTechsAffiliated.Contains(otherTech))
                        continue;
                    float temp = (otherTech.boundsCentreWorldNoCheck - tankPos).sqrMagnitude - otherTech.GetCheapBounds();
                    if (bestValue > temp)
                    {
                        bestValue = temp;
                        closestTank = otherTech;
                    }
                }
                if (closestTank != null)
                    bestValue = (closestTank.boundsCentreWorldNoCheck - tankPos).magnitude;
            }
            catch
            {
            }
            return closestTank;
        }

        public static Tank SecondClosestAlly(IEnumerable<Tank> AlliesAlt, Vector3 tankPos, out Tank secondTank, out float bestValue, out float auxBestValue, TankAIHelper thisTank)
        {
            bestValue = 500;
            auxBestValue = 500;
            secondTank = null;
            Tank closestTank = null;
            try
            {
                foreach (Tank otherTech in AlliesAlt)
                {
                    if (AvoidInvalidOrIgnoreable(otherTech) || thisTank.tank == otherTech ||
                        thisTank.MultiTechsAffiliated.Contains(otherTech))
                        continue;
                    float temp = (otherTech.boundsCentreWorldNoCheck - tankPos).sqrMagnitude;
                    if (bestValue > temp)
                    {
                        secondTank = otherTech;
                        closestTank = otherTech;
                        auxBestValue = bestValue;
                        bestValue = temp;
                    }
                    else if (bestValue < temp && auxBestValue > temp)
                    {
                        secondTank = otherTech;
                        auxBestValue = temp;
                    }
                }
                if (secondTank != null)
                    auxBestValue = (secondTank.boundsCentreWorldNoCheck - tankPos).magnitude;
                if (closestTank != null)
                    bestValue = (closestTank.boundsCentreWorldNoCheck - tankPos).magnitude;
                return closestTank;
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Crash on SecondClosestAlly " + e);
            }
            DebugTAC_AI.Log(KickStart.ModID + ": SecondClosestAlly - COULD NOT FETCH TANK");
            secondTank = null;
            return null;
        }
        public static Tank SecondClosestAllyPrecision(IEnumerable<Tank> AlliesAlt, Vector3 tankPos, out Tank secondTank, out float bestValue, out float auxBestValue, TankAIHelper thisTank)
        {
            bestValue = 500;
            auxBestValue = 500;
            secondTank = null;
            Tank closestTank = null;
            try
            {
                foreach (Tank otherTech in AlliesAlt)
                {
                    if (AvoidInvalidOrIgnoreable(otherTech) || thisTank.tank == otherTech ||
                        thisTank.MultiTechsAffiliated.Contains(otherTech))
                        continue;
                    float temp = (otherTech.boundsCentreWorldNoCheck - tankPos).sqrMagnitude - otherTech.GetCheapBounds();
                    if (bestValue > temp)
                    {
                        secondTank = otherTech;
                        closestTank = otherTech;
                        auxBestValue = bestValue;
                        bestValue = temp;
                    }
                    else if (bestValue < temp && auxBestValue > temp)
                    {
                        secondTank = otherTech;
                        auxBestValue = temp;
                    }
                }
                if (secondTank != null)
                    auxBestValue = (secondTank.boundsCentreWorldNoCheck - tankPos).magnitude;
                if (closestTank != null)
                    bestValue = (closestTank.boundsCentreWorldNoCheck - tankPos).magnitude;
                return closestTank;
            }
            catch
            {
            }
            DebugTAC_AI.Log(KickStart.ModID + ": SecondClosestAllyPrecision - COULD NOT FETCH TANK");
            secondTank = null;
            return null;
        }

        public static Tank ClosestUnanchoredAllyAegis(IEnumerable<Tank> AlliesAlt, Vector3 tankPos, float rangeSqr, out float bestValue, TankAIHelper thisTank)
        {
            bestValue = rangeSqr;
            Tank closestTank = null;
            try
            {
                foreach (Tank otherTech in AlliesAlt)
                {
                    if (FollowInvalidOrIgnoreable(otherTech) || thisTank.tank == otherTech || otherTech.IsAnchored)
                        continue;
                    float temp = (otherTech.boundsCentreWorldNoCheck - tankPos).sqrMagnitude;
                    if (bestValue > temp)
                    {
                        bestValue = temp;
                        closestTank = otherTech;
                    }
                }
                if (closestTank == null)
                    return null;
                bestValue = (closestTank.boundsCentreWorldNoCheck - tankPos).magnitude;
            }
            catch
            {
            }
            return closestTank;
        }

        public static Vector3 GetDriveApproxAirDirector(Tank tankToCopy, TankAIHelper AIHelp, out bool IsMoving)
        {
            Tank tank = AIHelp.tank;
            Vector3 end;
            Vector3 offsetTo = tankToCopy.trans.InverseTransformPoint(tank.boundsCentreWorldNoCheck) - tankToCopy.blockBounds.center;

            TankControl.State controlCopyTarget = tankToCopy.control.CurState;

            Vector3 InputLineVal = controlCopyTarget.m_InputMovement;
            if (tankToCopy.control.GetThrottle(0, out float throttleX))
            {
                InputLineVal.x += throttleX;
            }
            if (tankToCopy.control.GetThrottle(1, out float throttleY))
            {
                InputLineVal.y += throttleY;
            }
            if (tankToCopy.control.GetThrottle(2, out float throttleZ))
            {
                InputLineVal.z += throttleZ;
            }
            InputLineVal = InputLineVal.Clamp01Box();

            Vector3 DAdjuster = InputLineVal * 2000;
            Vector3 RAdjuster = controlCopyTarget.m_InputRotation * -1;
            Vector3 MoveDirectionUnthrottled = ((Quaternion.Euler(RAdjuster.x, RAdjuster.y, RAdjuster.z) * offsetTo) - offsetTo).normalized * (1000 * AIHelp.lastTechExtents);

            Vector3 posToGo = MoveDirectionUnthrottled + DAdjuster;

            if (AIHelp.AutoAnchor)
            {
                if (tankToCopy.IsAnchored)
                {
                    if (AIHelp.CanAutoAnchor)
                        AIHelp.TryInsureAutoAnchor();
                }
                else
                {
                    if (AIHelp.IsAutoAnchored)
                        AIHelp.Unanchor();
                }
            }

            end = tankToCopy.trans.TransformPoint(posToGo + tankToCopy.blockBounds.center);
            IsMoving = !(InputLineVal + controlCopyTarget.m_InputRotation).Approximately(Vector3.zero, 0.05f);
            return end;
        }
        public static Vector3 GetDriveApproxAirMaintainer(Tank tankToCopy, TankAIHelper AIHelp, out bool IsMoving)
        {
            Tank tank = AIHelp.tank;
            Vector3 end;
            Vector3 offsetTo = tankToCopy.trans.InverseTransformPoint(tank.boundsCentreWorldNoCheck) - tankToCopy.blockBounds.center;

            TankControl.State controlCopyTarget = tankToCopy.control.CurState;

            Vector3 InputLineVal = controlCopyTarget.m_InputMovement;
            if (tankToCopy.control.GetThrottle(0, out float throttleX))
            {
                InputLineVal.x += throttleX;
            }
            if (tankToCopy.control.GetThrottle(1, out float throttleY))
            {
                InputLineVal.y += throttleY;
            }
            if (tankToCopy.control.GetThrottle(2, out float throttleZ))
            {
                InputLineVal.z += throttleZ;
            }
            InputLineVal = InputLineVal.Clamp01Box();

            Vector3 DAdjuster = InputLineVal * 2000;
            Vector3 RAdjuster = controlCopyTarget.m_InputRotation * -1;
            Vector3 MoveDirectionUnthrottled = ((Quaternion.Euler(RAdjuster.x, RAdjuster.y, RAdjuster.z) * offsetTo) - offsetTo).normalized * (1000 * AIHelp.lastTechExtents);

            Vector3 posToGo = MoveDirectionUnthrottled + DAdjuster;

            AIHelp.ProcessControl(Vector3.zero, Vector3.zero, Vector3.zero,
                controlCopyTarget.m_BoostProps, controlCopyTarget.m_BoostJets);

            if (AIHelp.AutoAnchor)
            {
                if (tankToCopy.IsAnchored)
                {
                    if (AIHelp.CanAutoAnchor)
                        AIHelp.TryInsureAutoAnchor();
                }
                else
                {
                    if (AIHelp.IsAutoAnchored)
                        AIHelp.Unanchor();
                }
            }

            end = tankToCopy.trans.TransformPoint(posToGo + tankToCopy.blockBounds.center);
            IsMoving = !(InputLineVal + controlCopyTarget.m_InputRotation).Approximately(Vector3.zero, 0.05f);
            return end;
        }
        public static bool AboveHeightFromGround(Vector3 posScene, float groundOffset = 50)
        {
            float final_y;
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(posScene, out float height);
            if (terrain)
                final_y = height + groundOffset;
            else
                final_y = 50 + groundOffset;
            if (KickStart.isWaterModPresent)
            {
                if (KickStart.WaterHeight > height)
                    final_y = KickStart.WaterHeight + groundOffset;
            }
            return (posScene.y > final_y);
        }
        public static bool AboveHeightFromGroundRadius(Vector3 posScene, float radius, float groundOffset = 50)
        {
            float final_y;
            bool terrain = AIEPathMapper.GetHighestAltInRadiusLoadedOnly(posScene, radius, out float height, false);
            if (terrain)
                final_y = height + groundOffset;
            else
                final_y = 50 + groundOffset;
            if (KickStart.isWaterModPresent)
            {
                if (KickStart.WaterHeight > height)
                    final_y = KickStart.WaterHeight + groundOffset;
            }
            return (posScene.y > final_y);
        }
        public static bool AboveHeightFromGroundTech(TankAIHelper helper, float groundOffset = 50)
        {
            float final_y;
            float height = helper.GetFrameHeight();
            final_y = height + groundOffset;
            if (KickStart.isWaterModPresent)
            {
                if (KickStart.WaterHeight > height)
                    final_y = KickStart.WaterHeight + groundOffset;
            }
            return (helper.tank.boundsCentreWorldNoCheck.y > final_y);
        }
        public static bool AboveTheSea(Vector3 posScene)
        {
            if (!KickStart.isWaterModPresent)
                return false;
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(posScene, out float height);
            if (terrain)
            {
                if (height < KickStart.WaterHeight)
                    return true;
            }
            else if (50 < KickStart.WaterHeight)
                    return true;
            return false;
        }
        public static bool AboveTheSeaForcedAccurate(Vector3 posScene)
        {
            if (!KickStart.isWaterModPresent)
                return false;
            float height = ManWorld.inst.TileManager.GetTerrainHeightAtPosition(posScene, out _);
            if (height < KickStart.WaterHeight)
                return true;
            return false;
        }
        public static bool AboveTheSea(TankAIHelper helper)
        {
            return helper.GetFrameHeight() > KickStart.WaterHeight;
        }

        public static Vector3 OffsetFromGround(Vector3 input, TankAIHelper helper, float groundOffset = 0)
        {
            float final_y;
            Vector3 final = input;
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(input, out float height);
            if (groundOffset == 0) groundOffset = helper.GroundOffsetHeight;
            if (terrain)
                final_y = height + groundOffset;
            else
                final_y = 50 + groundOffset;
            if (KickStart.isWaterModPresent)
            {
                if (KickStart.WaterHeight > height)
                    final_y = KickStart.WaterHeight + groundOffset;
            }
            if (input.y < final_y)
            {
                final.y = final_y;
            }
            return final;
        }

        public static Vector3 OffsetFromGroundH(Vector3 input, TankAIHelper helper, float groundOffset = 0)
        {
            float final_y;
            Vector3 final = input;
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(input, out float height);
            if (groundOffset == 0) groundOffset = helper.GroundOffsetHeight;
            if (terrain)
                final_y = height + groundOffset;
            else
                final_y = 50 + groundOffset;
            if (helper.AdviseAwayCore)
            {
                try
                {
                    if (KickStart.isWaterModPresent)
                    {
                        if (KickStart.WaterHeight > height)
                            final_y = KickStart.WaterHeight + groundOffset;
                    }
                    if (input.y < final_y)
                    {
                        final.x = helper.tank.boundsCentreWorldNoCheck.x;
                        final.z = helper.tank.boundsCentreWorldNoCheck.z;
                        final.y = height;
                    }
                    else
                    {
                        final.y = helper.tank.boundsCentreWorldNoCheck.y;
                    }
                }
                catch (Exception eAlt) { DebugTAC_AI.LogWarnPlayerOnce("[TAC_AI:catch:Movement] altitude AdviseAwayCore", eAlt); }
            }
            else
            {
                if (KickStart.isWaterModPresent)
                {
                    if (KickStart.WaterHeight > height)
                        final_y = KickStart.WaterHeight + groundOffset;
                }
                if (input.y < final_y)
                {
                    final.y = final_y;
                }
            }
            return final;
        }
        public static Vector3 OffsetFromGroundA(Vector3 input, TankAIHelper helper, float groundOffset = 0)
        {
            float final_y;
            Vector3 final = input;
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(input, out float height);
            if (groundOffset == 0) groundOffset = helper.GroundOffsetHeight;
            if (terrain)
                final_y = height + groundOffset;
            else
                final_y = 50 + groundOffset;
            if (KickStart.isWaterModPresent)
            {
                if (KickStart.WaterHeight > height)
                    final_y = KickStart.WaterHeight + groundOffset;
            }
            if (input.y < final_y)
            {
                final.y = final_y;
            }
            return final;
        }
        public static Vector3 SnapOffsetFromGroundA(Vector3 input, TankAIHelper helper, float groundOffset = 0)
        {
            float final_y;
            Vector3 final = input;
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(input, out float height);
            if (groundOffset == 0) groundOffset = helper.GroundOffsetHeight;
            if (terrain)
                final_y = height + groundOffset;
            else
                final_y = 50 + groundOffset;
            if (KickStart.isWaterModPresent)
            {
                if (KickStart.WaterHeight > height)
                    final_y = KickStart.WaterHeight + groundOffset;
            }
            final.y = final_y;
            return final;
        }
        public static Vector3 SnapOffsetFromGroundA(Vector3 input, float groundOffset = 35)
        {
            float final_y;
            Vector3 final = input;
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(input, out float height);
            if (terrain)
                final_y = height + groundOffset;
            else
                final_y = 50 + groundOffset;
            if (KickStart.isWaterModPresent)
            {
                if (KickStart.WaterHeight > height)
                    final_y = KickStart.WaterHeight + groundOffset;
            }
            final.y = final_y;
            return final;
        }
        public static Vector3 OffsetFromGroundAAlt(Vector3 input, float groundOffset = 35)
        {
            float final_y;
            Vector3 final = input;
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(input, out float height);
            if (terrain)
                final_y = height + groundOffset;
            else
                final_y = 50 + groundOffset;
            if (KickStart.isWaterModPresent)
            {
                if (KickStart.WaterHeight > height)
                    final_y = KickStart.WaterHeight + groundOffset;
            }
            if (input.y < final_y)
                final.y = final_y;
            return final;
        }

        public static Vector3 OffsetToSea(Vector3 input, Tank tank, TankAIHelper helper)
        {
            Vector3 final = input;
            float heightTank;
            if (tank.rbody != null)
                AIEPathMapper.GetAltitudeLoadedOnly(tank.boundsCentreWorldNoCheck + helper.SafeVelocity.Clamp(-75 * Vector3.one, 75 * Vector3.one), out heightTank);
            else
                AIEPathMapper.GetAltitudeLoadedOnly(tank.boundsCentreWorldNoCheck, out heightTank);
            bool terrain = AIEPathMapper.GetAltitudeLoadedOnly(input, out float height);
            if (terrain)
            {
                float operatingDepth = tank.boundsCentreWorldNoCheck.y + helper.LowestPointOnTech;
                if (height > operatingDepth || heightTank > operatingDepth)
                {
                    int stepxM = 5;
                    int stepzM = 5;
                    int vecCount = 0;
                    Vector3 posAll = Vector3.zero;
                    for (int stepz = 0; stepz < stepzM; stepz++)
                    {
                        for (int stepx = 0; stepx < stepxM; stepx++)
                        {
                            Vector3 wow = tank.boundsCentreWorldNoCheck;
                            wow.x += (stepx * 20) - 50;
                            wow.z += (stepz * 20) - 50;
                            if (!AIEPathMapper.GetAltitudeLoadedOnly(wow, out float heightC))
                                continue;
                            if (heightC < heightTank)
                            {
                                posAll += wow;
                                vecCount++;
                                helper.ThrottleState = AIThrottleState.Yield;
                            }
                        }
                    }
                    if (vecCount == 25)
                    {
                        if (helper.AdviseAwayCore)
                        {
                            final = helper.tank.boundsCentreWorldNoCheck + ((input - helper.tank.boundsCentreWorldNoCheck).normalized * helper.DodgeStrength);
                        }
                        else
                            final = helper.tank.boundsCentreWorldNoCheck - ((input - helper.tank.boundsCentreWorldNoCheck).normalized * helper.DodgeStrength);
                    }
                    else if (vecCount > 0)
                    {
                        if (helper.AdviseAwayCore)
                        {
                            final = helper.tank.boundsCentreWorldNoCheck - ((tank.boundsCentreWorldNoCheck - (posAll / vecCount)).normalized * helper.DodgeStrength);
                        }
                        else
                            final = helper.tank.boundsCentreWorldNoCheck + ((tank.boundsCentreWorldNoCheck - (posAll / vecCount)).normalized * helper.DodgeStrength);
                    }
                }
            }
            final.y = KickStart.WaterHeight;
            return final;
        }
        public static Vector3 SnapOffsetToSea(Vector3 input)
        {
            Vector3 final = input;
            final.y = KickStart.WaterHeight;
            if (AIEPathMapper.GetAltitudeLoadedOnly(input, out float height))
            {
                if (height > final.y)
                    final.y = height;
            }

            return final;
        }
        public static Vector3 OffsetFromSea(Vector3 input, Tank tank, TankAIHelper helper)
        {
            if (!KickStart.isWaterModPresent)
                return input;
            float heightTank;
            if (tank.rbody != null)
                heightTank = helper.SafeVelocity.Clamp(-75 * Vector3.one, 75 * Vector3.one).y + tank.boundsCentreWorldNoCheck.y - (helper.lastTechExtents / 2);
            else
                heightTank = tank.boundsCentreWorldNoCheck.y - (helper.lastTechExtents / 2);
            Vector3 final = input;
            if (heightTank < KickStart.WaterHeight)
            {
                int stepxM = 3;
                int stepzM = 3;
                float highestHeight = KickStart.WaterHeight - helper.lastTechExtents * AIGlobals.WaterDepthTechHeightPercent;
                Vector3 posBest = Vector3.zero;
                for (int stepz = 0; stepz < stepzM; stepz++)
                {
                    for (int stepx = 0; stepx < stepxM; stepx++)
                    {
                        Vector3 wow = tank.boundsCentreWorldNoCheck;
                        wow.x -= 45;
                        wow.z -= 45;
                        wow.x += stepx * 30;
                        wow.z += stepz * 30;
                        if (!AIEPathMapper.GetAltitudeLoadedOnly(wow, out float heightC))
                            continue;
                        if (heightC > highestHeight)
                        {
                            highestHeight = heightC;
                            posBest = wow;
                            helper.ThrottleState = AIThrottleState.Yield;
                        }
                    }
                }
                if (highestHeight > KickStart.WaterHeight)
                {
                    if (helper.AdviseAwayCore)
                    {
                        final = helper.tank.boundsCentreWorldNoCheck + (helper.tank.boundsCentreWorldNoCheck - posBest);
                    }
                    else
                        final = posBest;
                }
                else
                {
                    if (helper.AdviseAwayCore)
                    {
                        final = helper.tank.boundsCentreWorldNoCheck + ((input - helper.tank.boundsCentreWorldNoCheck).normalized * helper.DodgeStrength);
                    }
                    else
                        final = helper.tank.boundsCentreWorldNoCheck - ((input - helper.tank.boundsCentreWorldNoCheck).normalized * helper.DodgeStrength);
                }
            }

            return final;
        }

        internal static Vector3 ModerateMaxAlt(Vector3 moderate, TankAIHelper helper)
        {
            if ((bool)Singleton.playerTank && !ManWorldRTS.PlayerIsInRTS)
            {
                if (moderate.y > AIGlobals.AirWanderMaxHeight + Singleton.playerPos.y)
                {
                    return SnapOffsetFromGroundA(moderate, helper);
                }
            }
            else
            {
                try
                {
                    if (moderate.y > AIGlobals.AirWanderMaxHeight + TankAIManager.terrainHeight)
                    {
                        return SnapOffsetFromGroundA(moderate, helper);
                    }
                }
                catch (Exception eSnap) { DebugTAC_AI.LogWarnPlayerOnce("[TAC_AI:catch:Movement] SnapOffsetFromGroundA", eSnap); }
            }
            return moderate;
        }
        internal static bool IsUnderMaxAltPlayer(float height)
        {
            if ((bool)Singleton.playerTank && !ManWorldRTS.PlayerIsInRTS)
            {
                if (height > AIGlobals.AirWanderMaxHeight + Singleton.playerPos.y)
                    return false;
            }
            else
            {
                try
                {
                    if (height > AIGlobals.AirWanderMaxHeight + TankAIManager.terrainHeight)
                        return false;
                }
                catch (Exception eMax) { DebugTAC_AI.LogWarnPlayerOnce("[TAC_AI:catch:Movement] IsUnderMaxAltPlayer terrainHeight", eMax); }
            }
            return true;
        }

    }
}
