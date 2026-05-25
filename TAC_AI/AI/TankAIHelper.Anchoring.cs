using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TAC_AI.AI
{
    // Anchoring service (partial-class split of TankAIHelper, Step 2). The anchor state machine: the public
    // TryInsure*/Unanchor/AnchorIgnoreChecks set AnchorState (bus); the private DoAnchor*/DoUnAnchor workers
    // consume it and call the vanilla TechAnchors APIs (incl. the cached ConfigureJoint reflection MI). The
    // anchor STATE + computed Can* query properties stay on the core bus; this file owns the actions.
    public partial class TankAIHelper
    {
        private static MethodInfo MI = typeof(TechAnchors).GetMethod("ConfigureJoint", BindingFlags.NonPublic | BindingFlags.Instance);
        public void TryInsureAutoAnchor()
        {
            if (!tank.IsAnchored && CanAnchorNow)
            {
                AnchorState = AIAnchorState.AnchorAuto;
            }
        }
        public void TryInsureManualAnchor()
        {
            if (!tank.IsAnchored && CanAnchorNow)
            {
                AnchorState = AIAnchorState.Anchor;
            }
        }
        public void AnchorIgnoreChecks(bool forced = false)
        {
            if (forced)
            {
                DebugTAC_AI.LogDevOnlyAssert(KickStart.ModID + ": AI " + tank.name + ":  TryReallyAnchor(true)");
                AnchorState = AIAnchorState.ForceAnchor;
            }
            else
                AnchorState = AIAnchorState.Anchor;
        }
        public void AdjustAnchors()
        {
            bool prevAnchored = tank.IsAnchored;
            DoUnAnchor();
            if (!tank.IsAnchored)
            {
                AnchorIgnoreChecks(prevAnchored);
            }
        }

        public void Unanchor()
        {
            AnchorState = AIAnchorState.Unanchor;
            AnchorStateAIInsure = true;
        }

        public void AnchorStatic()
        {
            AnchorState = AIAnchorState.AnchorStaticAI;
        }

        private void DoAnchorStatic()
        {
            SetDriverType(AIDriverType.Stationary);
        }

        private void DoAnchor(bool forced)
        {
            if (!tank.IsAnchored && anchorAttempts <= AIGlobals.MaxAnchorAttempts)
            {
                anchorAttempts++;
                tank.Anchors.TryAnchorAll(true);
                if (tank.Anchors.NumIsAnchored > 0)
                {
                    ExpectAITampering = true;
                    anchorAttempts = 0;
                    if (AnchorState == AIAnchorState.AnchorStaticAI)
                        DoAnchorStatic();
                    AnchorState = AIAnchorState.Anchored;
                    return;
                }

                bool worked = false;
                Vector3 startPosTrans = tank.trans.position;
                tank.FixupAnchors(false);
                if (tank.Anchors.NumIsAnchored > 0)
                {
                    ExpectAITampering = true;
                    anchorAttempts = 0;
                    if (AnchorState == AIAnchorState.AnchorStaticAI)
                        DoAnchorStatic();
                    AnchorState = AIAnchorState.Anchored;
                    return;
                }
                Vector3 startPos = tank.visible.centrePosition;
                Quaternion tankFore = AIGlobals.LookRot(tank.trans.forward.SetY(0).normalized, Vector3.up);
                tank.visible.Teleport(startPos, tankFore, true, true);
                for (int step = 0; step < 6; step++)
                {
                    if (!tank.IsAnchored)
                    {
                        Vector3 newPos = startPos + Vector3.down;
                        newPos.y += step / 4f;
                        tank.visible.Teleport(newPos, tankFore, false, true);
                        tank.Anchors.TryAnchorAll();
                    }
                    if (tank.IsAnchored)
                    {
                        worked = true;
                        break;
                    }
                    tank.FixupAnchors(true);
                }
                var anchors = tank.blockman.IterateBlockComponents<ModuleAnchor>();
                if (!worked && anchors.Count() > 0)
                {
                    if (AIGlobals.IsAttract || forced)
                    {
                        DebugTAC_AI.Assert(true, (AIGlobals.IsAttract ? "(ATTRACT BASE)" : "(FORCED)") + " screw you i'm anchoring anyways, I don't give a f*bron about your anchor checks!");
                        foreach (var item in anchors)
                        {
                            item.AnchorToGround();
                            if (item.AnchorGeometryActive)
                            {
                                tank.Anchors.AddAnchor(item);
                            }
                        }
                        tank.grounded = true;
                        MI.Invoke(tank.Anchors, new object[0]);
                        worked = true;
                    }
                    else
                    {
                        tank.trans.position = startPosTrans + (Vector3.up * 0.1f);
                    }
                }
                if (worked)
                {
                    ExpectAITampering = true;
                    anchorAttempts = 0;
                    if (AnchorState == AIAnchorState.AnchorStaticAI)
                        DoAnchorStatic();
                    RecalibrateMovementAIController();
                }
                ExpectAITampering = true;
                tank.visible.Teleport(startPos, tankFore, true, true);
                // REVISED: a failed anchor attempt now resets anchorAttempts so the tech can retry from scratch later rather than staying stuck at the attempt cap.
                if (!worked)
                    anchorAttempts = 0;
                AnchorState = AIAnchorState.None;
            }
        }
        private void DoUnAnchor()
        {
            if (tank.IsAnchored || tank.Anchors.NumIsAnchored > 0)
            {
                tank.Anchors.UnanchorAll(true);
                if (!tank.IsAnchored && AIAlign == AIAlignment.Player)
                {
                    WakeAIForChange();
                }
            }
            // REVISED: AnchorState is now always reset to None (moved out of the `was anchored` branch), so the unanchor state is cleared even when there was nothing to unanchor.
            AnchorState = AIAnchorState.None;
            anchorAttempts = 0;
        }
    }
}
