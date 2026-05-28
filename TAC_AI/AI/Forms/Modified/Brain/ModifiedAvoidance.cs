using System;
using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    /// <summary>
    /// Avoidance brain - DETACHED from TankAIHelper into the Modified form (v2) as extension methods. Ally/obstacle
    /// dodging vector math. The control sinks (ProcessControl/SteerControl/UpdateVanillaAvoidence), the control-state
    /// utility FixControlReversal, the DodgeStrength tuning property and the GetFrameHeight terrain sensing stay on the
    /// shell helper. AvoidStuff/SecondAvoidence/WasRetreatingInCombat/Avoiding/lastCloseAlly state stays on the bus.
    /// posWeights is a non-reentrant static scratch list (cleared at the top of each AvoidAssist* call).
    /// </summary>
    public static class ModifiedAvoidance
    {
        internal static List<KeyValuePair<Vector3, float>> posWeights = new List<KeyValuePair<Vector3, float>>();

        // Terrain-altitude sensing (reads the form's AIEPathMapper cache). CurHeight (the per-frame cache) stays on the
        // shell bus, reset to -500 by ControlFrame each control frame.
        public static float GetFrameHeight(this TankAIHelper helper)
        {
            if (helper.CurHeight == -500)
            {
                helper.CurHeight = AIEPathMapper.GetAltitudeCached(helper.tank.boundsCentreWorldNoCheck);
            }
            return helper.CurHeight;
        }

        public static Vector3 GetDirAuto(this TankAIHelper helper, Tank tank)
        {
            if (helper.IsDirectedMovingFromDest)
                return helper.GetDir(tank);
            return helper.GetOtherDir(tank);
        }

        public static Vector3 GetOtherDir(this TankAIHelper helper, Tank targetToAvoid)
        {
            Vector3 inputOffset = helper.tank.boundsCentreWorldNoCheck - targetToAvoid.boundsCentreWorldNoCheck;
            float inputSpacing = targetToAvoid.GetCheapBounds() + helper.lastTechExtents + helper.DodgeStrength;
            Vector3 Final = helper.tank.boundsCentreWorldNoCheck + (inputOffset.normalized * inputSpacing);
            return Final;
        }

        public static Vector3 GetDir(this TankAIHelper helper, Tank targetToAvoid)
        {
            Vector3 inputOffset = helper.tank.boundsCentreWorldNoCheck - targetToAvoid.boundsCentreWorldNoCheck;
            float inputSpacing = targetToAvoid.GetCheapBounds() + helper.lastTechExtents + helper.DodgeStrength;
            Vector3 Final = helper.tank.boundsCentreWorldNoCheck - (inputOffset.normalized * inputSpacing);
            return Final;
        }

        public static Vector3 AvoidAssist(this TankAIHelper helper, Vector3 targetIn, bool AvoidStatic = true)
        {
            if (!helper.AvoidStuff || helper.tank.IsAnchored)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssist IS NaN!!");
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(helper.tank);
                posWeights.Clear();
                if (helper.SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAlly(AlliesAlt, helper.tank.boundsCentreWorldNoCheck, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, helper);
                    if (lastCloseAlly && lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < helper.lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI);
                            Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly) + helper.GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            helper.Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI);
                            Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            helper.Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAlly(AlliesAlt, helper.tank.boundsCentreWorldNoCheck, out lastAllyDist, helper);
                    if (lastCloseAlly != null && lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI);
                        Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        helper.Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                if (posWeights.Count == 0)
                    return targetIn;
                Vector3 posCombined = targetIn;
                float totalWeight = 1;
                foreach (var item in posWeights)
                {
                    totalWeight += item.Value;
                    posCombined += item.Key * item.Value;
                }
                helper.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                if (helper.IsDirectedMovingFromDest)
                    DebugTAC_AI.LogWarnPlayerOnce("AvoidAssist()[INVERTED] Critical error", e);
                else
                    DebugTAC_AI.LogWarnPlayerOnce("AvoidAssist() Critical error", e);
                return targetIn;
            }
        }

        public static Vector3 AvoidAssistPrecise(this TankAIHelper helper, Vector3 targetIn, bool AvoidStatic = true, bool IgnoreDestructable = false)
        {
            if (!helper.AvoidStuff || helper.tank.IsAnchored)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistPrecise IS NaN!!");
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(helper.tank);
                posWeights.Clear();
                if (helper.SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAllyPrecision(AlliesAlt, helper.tank.boundsCentreWorldNoCheck, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, helper);
                    if (lastCloseAlly && lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < helper.lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI, IgnoreDestructable);
                            Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly) + helper.GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            helper.Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI, IgnoreDestructable);
                            Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            helper.Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAllyPrecision(AlliesAlt, helper.tank.boundsCentreWorldNoCheck, out lastAllyDist, helper);
                    if (lastCloseAlly != null && lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI, IgnoreDestructable);
                        Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        helper.Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, AvoidStatic, out obst, helper.AdvancedAI, IgnoreDestructable);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                if (posWeights.Count == 0)
                    return targetIn;
                Vector3 posCombined = targetIn;
                float totalWeight = 1;
                foreach (var item in posWeights)
                {
                    totalWeight += item.Value;
                    posCombined += item.Key * item.Value;
                }
                helper.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("AvoidAssistPrecise() Critical error", e);
                return targetIn;
            }
        }

        public static Vector3 AvoidAssistPrediction(this TankAIHelper helper, Vector3 targetIn, float Foresight)
        {
            if (!helper.AvoidStuff || helper.tank.IsAnchored)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistPrediction IS NaN!!");
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                Vector3 posOffset = helper.tank.boundsCentreWorldNoCheck + (helper.SafeVelocity * Foresight);
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(helper.tank);
                posWeights.Clear();
                if (helper.SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAlly(AlliesAlt, posOffset, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, helper);
                    if (lastCloseAlly && lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < helper.lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, true, out obst, helper.AdvancedAI);
                            Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly) + helper.GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            helper.Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, true, out obst, helper.AdvancedAI);
                            Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            helper.Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, true, out obst, helper.AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAlly(AlliesAlt, posOffset, out lastAllyDist, helper);
                    if (lastCloseAlly != null && lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, true, out obst, helper.AdvancedAI);
                        Vector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        helper.Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(helper.tank, helper, true, out obst, helper.AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                if (posWeights.Count == 0)
                    return targetIn;
                Vector3 posCombined = targetIn;
                float totalWeight = 1;
                foreach (var item in posWeights)
                {
                    totalWeight += item.Value;
                    posCombined += item.Key * item.Value;
                }
                helper.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("AvoidAssistPrediction() Critical error", e);
                return targetIn;
            }
        }

        public static Vector3 AvoidAssistAirSpacing(this TankAIHelper helper, Vector3 targetIn, float Responsiveness)
        {
            try
            {
                Tank lastCloseAlly;
                float lastAllyDist;
                Vector3 DSO = helper.DodgeSphereCenter;
                float moveSpace = (DSO - helper.tank.boundsCentreWorldNoCheck).magnitude;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(helper.tank);
                if (helper.SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAllyPrecision(AlliesAlt, DSO, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, helper);
                    if (lastCloseAlly && lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < helper.lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                        {
                            IntVector3 ProccessedVal2 = helper.GetDirAuto(lastCloseAlly) + helper.GetDirAuto(lastCloseAlly2);
                            helper.Avoiding = true;
                            return (targetIn + ProccessedVal2) / 3;
                        }
                        IntVector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly);
                        helper.Avoiding = true;
                        return (targetIn + ProccessedVal) / 2;
                    }

                }
                lastCloseAlly = AIEPathing.ClosestAllyPrecision(AlliesAlt, DSO, out lastAllyDist, helper);
                helper.lastCloseAlly = lastCloseAlly;
                if (lastCloseAlly == null)
                {
                    return targetIn;
                }
                if (lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                {
                    IntVector3 ProccessedVal = helper.GetDirAuto(lastCloseAlly);
                    helper.Avoiding = true;
                    return (targetIn + ProccessedVal) / 2;
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("AvoidAssistAirSpacing() Critical error", e);
                return targetIn;
            }
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistAirSpacing IS NaN!!");
            }
            return targetIn;
        }
    }
}
