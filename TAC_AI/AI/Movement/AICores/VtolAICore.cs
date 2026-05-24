using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    internal class VtolAICore : AirplaneAICore, IMovementAICore
    {
        // IAirMovementAICore: a VTOL is not pure fixed-wing (air target-acq treats it like a hovering
        // craft); inherits IsRotorcraft => false from AirplaneAICore.
        public override bool IsFixedWing => false;
        public override void Initiate(Tank tank, IMovementAIController pilot)
        {
            base.Initiate(tank, pilot);
            this.pilot.FlyStyle = AIControllerAir.FlightType.VTOL;
            pilot.Helper.GroundOffsetHeight = pilot.Helper.lastTechExtents + AIGlobals.GroundOffsetAircraft;
        }
        public override bool DriveMaintainer(TankAIHelper helper, Tank tank, ref EControlCoreSet core)
        {
            // P08 B-NEW5-3 partial: was missing beam check that AirplaneAICore.DriveMaintainer has.
            // Without this, a beamed-mid-flight VTOL runs cruise/takeoff logic against an inactive tech.
            if (tank.beam.IsActive)
            {
                // KillAllControl lives on AIControllerAir, not TankAIHelper.
                pilot.KillAllControl(helper);
                return true;
            }
            if (pilot.Grounded)
            {   //Become a ground vehicle for now
                if (!AIEPathing.AboveHeightFromGroundTech(helper, helper.lastTechExtents * 2))
                {
                    // P08 G.8: parity with HelicopterAICore Grounded branch (line ~50) — call
                    // inherited DriveMaintainerEmergLand instead of silently returning. Without
                    // this, a Grounded VTOL near terrain sat with zero input forever.
                    DriveMaintainerEmergLand(helper, tank, ref core);
                    return false;
                }
                // P08 G.7: controlled descent mirroring HelicopterAICore (VTOLs use heli helpers
                // when ForcePitchUp). Target ground level so ModerateUpwardsThrust modulates
                // thrust DOWN for safe landing.
                pilot.ForcePitchUp = true;
                AIEPathMapper.GetAltitudeLoadedOnly(tank.boundsCentreWorldNoCheck, out float groundHeight);
                pilot.MainThrottle = HelicopterUtils.ModerateUpwardsThrust(tank, helper, pilot, groundHeight, false);
                pilot.UpdateThrottle(helper);
                HelicopterUtils.AngleTowardsUp(pilot, tank.boundsCentreWorldNoCheck, helper.lastDestinationCore, ref core, true);
                return true;
            }
            if (tank.wheelGrounded || pilot.ForcePitchUp)
            {   // Try and takeoff like helicopter
                pilot.MainThrottle = HelicopterUtils.ModerateUpwardsThrust(tank, helper, pilot,
                    AIEPathing.OffsetFromGroundA(tank.boundsCentreWorldNoCheck, helper, helper.lastTechExtents * 2).y);
                pilot.UpdateThrottle(helper);
                HelicopterUtils.AngleTowardsUp(pilot, tank.boundsCentreWorldNoCheck, helper.lastDestinationCore, ref core, true);
            }
            else
            {   //Fly like plane
                if (PerformUTurn > 0)
                {   //The Immelmann Turn
                    //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "  U-Turn level " + pilot.PerformUTurn + "  throttle " + pilot.CurrentThrottle);
                    pilot.MainThrottle = 1;
                    pilot.UpdateThrottle(helper);
                    if ( helper.LocalSafeVelocity.z < AIGlobals.AirStallSpeed - 4)
                    {   //ABORT!!!
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "  Aborted U-Turn with velocity " + helper.LocalSafeVelocity.z);
                        PerformUTurn = -1;
                    }
                    else if (Vector3.Dot(Vector3.down,  helper.SafeVelocity.normalized) > 0.4f)
                    {   //ABORT!!!
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + "  Aborted U-Turn as too much movement to the ground");
                        PerformUTurn = -1;
                    }
                    if (PerformUTurn == 1)
                    {
                        AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck + tank.rootBlockTrans.forward * 100);
                        if (pilot.CurrentThrottle > 0.95)
                            PerformUTurn = 2;
                    }
                    else if (PerformUTurn == 2)
                    {
                        AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck + (Vector3.up * 100));
                        if (Vector3.Dot(tank.rootBlockTrans.forward, Vector3.up) > 0.75f)
                            PerformUTurn = 3;
                    }
                    else if (PerformUTurn == 3)
                    {
                        AngleTowards(helper, tank, pilot, pilot.PathPointSet);
                        if (Vector3.Dot((pilot.PathPointSet - tank.boundsCentreWorldNoCheck).normalized, tank.rootBlockTrans.forward) > 0.6f)
                            PerformUTurn = 0;
                    }
                    return true;
                }
                else if (PerformUTurn == -1)
                {
                    pilot.MainThrottle = 1;
                    pilot.UpdateThrottle(helper);
                    AngleTowards(helper, tank, pilot, pilot.PathPointSet);
                    if (Vector3.Dot(tank.rootBlockTrans.forward, (pilot.PathPointSet - tank.boundsCentreWorldNoCheck).normalized) > 0)
                        PerformUTurn = 0;
                    return true;
                }
                else
                {
                    // P08 B-NEW5-3 partial: was calling UpdateThrottle without setting MainThrottle
                    // from AdvisedThrottle first, so VTOL cruise inherited whatever MainThrottle was
                    // last set (often `1f` from takeoff/U-turn). Mirror AirplaneAICore.DriveMaintainer.
                    pilot.MainThrottle = pilot.AdvisedThrottle;
                    pilot.UpdateThrottle(helper);
                    AngleTowards(helper, tank, pilot, pilot.PathPointSet);
                }
            }

            return true;
        }
    }
}
