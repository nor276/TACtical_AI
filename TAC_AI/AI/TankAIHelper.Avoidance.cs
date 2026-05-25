using System;
using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    // Avoidance service (partial-class split of TankAIHelper, Step 2). Ally/obstacle dodging vector math.
    // posWeights is deliberately a NON-REENTRANT static scratch list (cleared at the top of each AvoidAssist*
    // call) - single-threaded use only; kept co-located here. AvoidStuff/SecondAvoidence state stay on the
    // core bus. No tank.control writes except UpdateVanillaAvoidence toggling the vanilla avoidance flag.
    public partial class TankAIHelper
    {
        internal void UpdateVanillaAvoidence()
        {
            tank.control.m_Movement.m_USE_AVOIDANCE = AvoidStuff;
        }
        public bool FixControlReversal(float Drive)
        {
            var thisControl = tank.control;
            return (thisControl.ActiveScheme == null || !thisControl.ActiveScheme.ReverseSteering) &&
                Drive < -0.01f &&
                Vector3.Dot(SafeVelocity, thisControl.Tech.rootBlockTrans.forward) < 0f;
        }
        internal void ProcessControl(Vector3 DriveVal, Vector3 TurnVal, Vector3 Throttle, bool props, bool jets)
        {
            tank.control.CollectMovementInput(DriveVal, TurnVal, Throttle, props, jets);
        }
        internal void SteerControl(Vector3 direction, float throttle)
        {
            tank.control.m_Movement.FaceDirection(tank, direction, throttle);
        }
        internal float DodgeStrength
        {
            get
            {
                if (UsingAirControls)
                    return AIGlobals.AirborneDodgeStrengthMultiplier * lastOperatorRange;
                return AIGlobals.DefaultDodgeStrengthMultiplier * lastOperatorRange;
            }
        }
        // PhysicsInfo dodge-sphere + velocity properties moved to TankAIHelper.Physics.cs (Step 2).
        private float CurHeight = 0;
        public float GetFrameHeight()
        {
            if (CurHeight == -500)
            {
                CurHeight = AIEPathMapper.GetAltitudeCached(tank.boundsCentreWorldNoCheck);
            }
            return CurHeight;
        }
        public Vector3 GetDirAuto(Tank tank)
        {
            if (IsDirectedMovingFromDest)
                return GetDir(tank);
            return GetOtherDir(tank);
        }
        internal Vector3 GetOtherDir(Tank targetToAvoid)
        {
            Vector3 inputOffset = tank.boundsCentreWorldNoCheck - targetToAvoid.boundsCentreWorldNoCheck;
            float inputSpacing = targetToAvoid.GetCheapBounds() + lastTechExtents + DodgeStrength;
            Vector3 Final = tank.boundsCentreWorldNoCheck + (inputOffset.normalized * inputSpacing);
            return Final;
        }
        internal Vector3 GetDir(Tank targetToAvoid)
        {
            Vector3 inputOffset = tank.boundsCentreWorldNoCheck - targetToAvoid.boundsCentreWorldNoCheck;
            float inputSpacing = targetToAvoid.GetCheapBounds() + lastTechExtents + DodgeStrength;
            Vector3 Final = tank.boundsCentreWorldNoCheck - (inputOffset.normalized * inputSpacing);
            return Final;
        }

        internal static List<KeyValuePair<Vector3, float>> posWeights = new List<KeyValuePair<Vector3, float>>();
        // REVISED: while WasRetreatingInCombat, AvoidAssist keeps the ObstDodgeOffset scenery dodge (a tech should never ram scenery, even backing off) but skips the ally-spacing weights so they do not fight the combat-FSM retreat vector; the Precise/Prediction/AirSpacing variants still fully early-out. The obsolete AvoidAssistInv_OBS variant was removed.
        internal Vector3 AvoidAssist(Vector3 targetIn, bool AvoidStatic = true)
        {
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssist IS NaN!!");
                return targetIn;
            }
            if (WasRetreatingInCombat)
            {   // REVISED: while retreating, keep the scenery dodge (a tech should never ram scenery, even while
                // backing off) but skip ally-spacing so it does not fight the retreat vector. ObstDodgeOffset also
                // sets ThrottleState = Yield when it finds scenery, so the retreat eases off near obstacles too.
                Vector3 obstRetreatOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out bool obstRetreat, AdvancedAI);
                if (obstRetreat)
                    return (targetIn + obstRetreatOff * 2f) / 3f;
                return targetIn;
            }
            try
            {
                bool obst;
                Tank lastCloseAlly;
                float lastAllyDist;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                posWeights.Clear();
                if (SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAlly(AlliesAlt, tank.boundsCentreWorldNoCheck, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAlly(AlliesAlt, tank.boundsCentreWorldNoCheck, out lastAllyDist, this);
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                        Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
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
                this.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                if (IsDirectedMovingFromDest)
                    DebugTAC_AI.LogWarnPlayerOnce("AvoidAssist()[INVERTED] Critical error", e);
                else
                    DebugTAC_AI.LogWarnPlayerOnce("AvoidAssist() Critical error", e);
                return targetIn;
            }
        }
        internal Vector3 AvoidAssistPrecise(Vector3 targetIn, bool AvoidStatic = true, bool IgnoreDestructable = false)
        {
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            if (WasRetreatingInCombat)
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
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                posWeights.Clear();
                if (SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAllyPrecision(AlliesAlt, tank.boundsCentreWorldNoCheck, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAllyPrecision(AlliesAlt, tank.boundsCentreWorldNoCheck, out lastAllyDist, this);
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
                        Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, AvoidStatic, out obst, AdvancedAI, IgnoreDestructable);
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
                this.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("AvoidAssistPrecise() Critical error", e);
                return targetIn;
            }
        }
        internal Vector3 AvoidAssistPrediction(Vector3 targetIn, float Foresight)
        {
            if (!AvoidStuff || tank.IsAnchored)
                return targetIn;
            if (WasRetreatingInCombat)
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
                Vector3 posOffset = tank.boundsCentreWorldNoCheck + (SafeVelocity * Foresight);
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                posWeights.Clear();
                if (SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAlly(AlliesAlt, posOffset, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 8));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 2));
                        }
                        else
                        {
                            Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                            Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                            if (obst)
                                posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                            Avoiding = true;
                            posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                        }
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 2));
                    }
                }
                else
                {
                    lastCloseAlly = AIEPathing.ClosestAlly(AlliesAlt, posOffset, out lastAllyDist, this);
                    if (lastCloseAlly != null && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace)
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
                        Vector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                        if (obst)
                            posWeights.Add(new KeyValuePair<Vector3, float>(obstOff, 4));
                        Avoiding = true;
                        posWeights.Add(new KeyValuePair<Vector3, float>(ProccessedVal, 1));
                    }
                    else
                    {
                        Vector3 obstOff = AIEPathing.ObstDodgeOffset(tank, this, true, out obst, AdvancedAI);
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
                this.lastCloseAlly = lastCloseAlly;
                return posCombined / totalWeight;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("AvoidAssistPrediction() Critical error", e);
                return targetIn;
            }
        }
        internal Vector3 AvoidAssistAirSpacing(Vector3 targetIn, float Responsiveness)
        {
            if (WasRetreatingInCombat)
                return targetIn;
            try
            {
                Tank lastCloseAlly;
                float lastAllyDist;
                // REVISED: the dodge-sphere center no longer divides by Responsiveness; DSO is the raw DodgeSphereCenter (Responsiveness arg now unused for this offset).
                Vector3 DSO = DodgeSphereCenter;
                float moveSpace = (DSO - tank.boundsCentreWorldNoCheck).magnitude;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                if (SecondAvoidence && AlliesAlt.Count > 1)
                {
                    lastCloseAlly = AIEPathing.SecondClosestAllyPrecision(AlliesAlt, DSO, out Tank lastCloseAlly2,
                        out lastAllyDist, out float lastAuxVal, this);
                    if (lastCloseAlly && lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                        {
                            IntVector3 ProccessedVal2 = GetDirAuto(lastCloseAlly) + GetDirAuto(lastCloseAlly2);
                            Avoiding = true;
                            return (targetIn + ProccessedVal2) / 3;
                        }
                        IntVector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                        Avoiding = true;
                        return (targetIn + ProccessedVal) / 2;
                    }

                }
                lastCloseAlly = AIEPathing.ClosestAllyPrecision(AlliesAlt, DSO, out lastAllyDist, this);
                this.lastCloseAlly = lastCloseAlly;
                if (lastCloseAlly == null)
                {
                    return targetIn;
                }
                if (lastAllyDist < lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                {
                    IntVector3 ProccessedVal = GetDirAuto(lastCloseAlly);
                    Avoiding = true;
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
