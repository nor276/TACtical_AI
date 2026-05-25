using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    /// REVISED (overview): now an IAirMovementAICore via AirplaneAICore (IsFixedWing => false).
    /// DriveMaintainer gained a beam-active KillAllControl short-circuit, and the grounded branch
    /// now actively recovers (EmergLand below tech height, else powered vertical takeoff via
    /// HelicopterUtils.ModerateUpwardsThrust + AngleTowardsUp) where it previously did nothing.
    internal class VtolAICore : AirplaneAICore, IMovementAICore
    {
        // REVISED: VTOL reports as rotorcraft-style (not fixed-wing) for cross-pipeline IAirMovementAICore queries.
        public override bool IsFixedWing => false;
        public override void Initiate(Tank tank, IMovementAIController pilot)
        {
            base.Initiate(tank, pilot);
            this.pilot.FlyStyle = AIControllerAir.FlightType.VTOL;
            pilot.Helper.GroundOffsetHeight = pilot.Helper.lastTechExtents + AIGlobals.GroundOffsetAircraft;
        }
        public override bool DriveMaintainer(TankAIHelper helper, Tank tank, ref EControlCoreSet core)
        {
            // REVISED: beam-active now kills all control and returns immediately.
            if (tank.beam.IsActive)
            {
                pilot.KillAllControl(helper);
                return true;
            }
            if (pilot.Grounded)
            {
                // REVISED: grounded recovery added; previously a no-op. Below tech height -> EmergLand,
                // otherwise force pitch up and apply powered vertical thrust to climb back to flight.
                if (!AIEPathing.AboveHeightFromGroundTech(helper, helper.lastTechExtents * 2))
                {
                    DriveMaintainerEmergLand(helper, tank, ref core);
                    return false;
                }
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
                    // REVISED: cruise now drives MainThrottle from AdvisedThrottle before updating throttle.
                    pilot.MainThrottle = pilot.AdvisedThrottle;
                    pilot.UpdateThrottle(helper);
                    AngleTowards(helper, tank, pilot, pilot.PathPointSet);
                }
            }

            return true;
        }
    }
}
