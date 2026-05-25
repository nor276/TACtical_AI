using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    // Unjam / obstruction service (partial-class split of TankAIHelper, Step 2). Detects a wedged/blocked
    // tech and drives the recovery (rush / gun-free / build-beam) + SettleDown reset. Unjam state
    // (ForceSetBeam/IsTryingToUnjam/BeamTimeoutClock/FIRE_ALL/Obst/Urgency*) stays on the core bus; this
    // file owns the logic. SettleDown calls the SetCoreControlStop sink (kept in core). Both obstruction
    // methods take the drive operator by ref. Behaviors reach these via IAIContext.
    public partial class TankAIHelper
    {
        public bool AutoHandleObstruction(ref EControlOperatorSet direct, float dist = 0, bool useRush = false, bool useGun = true, float div = 4)
        {
            if (!IsTechMovingAbs(EstTopSped / div))
            {
                TryHandleObstruction(!AIECore.Feedback, dist, useRush, useGun, ref direct);
                return true;
            }
            return false;
        }
        // REVISED: unjam FSM reworked - clears IsTryingToUnjam below UnjamUpdateStart; bails immediately on a Stop facing; soft-decays FrustrationMeter when the tech is actually making linear/angular progress; the UnjamUpdateEnd ceiling calls SettleDown(false)+return (full reset, but no hard core-Stop, so the hand-off keeps momentum) instead of pinning FrustrationMeter=45; the old `45 <` literal gate is now AIGlobals.UnjamUpdateFire. The build-beam phase (120-240) only sets ForceSetBeam when AIEBeam.IsTechTippedOver - upright-but-stuck techs stay in the throttle/fire phase instead of flickering the beam.
        public void TryHandleObstruction(bool hasMessaged, float dist, bool useRush, bool useGun, ref EControlOperatorSet direct)
        {
            if (!hasMessaged)
            {
            }

            ControlCore.FlagBusyUnstucking();
            if (FrustrationMeter <= AIGlobals.UnjamUpdateStart)
                IsTryingToUnjam = false;
            if (direct.DriveDir == EDriveFacing.Stop)
                return;
            // REVISED: bleed the meter on actual net progress (MakingNetProgress over the window), not on instantaneous
            // velocity/yaw - a tech grinding/jittering against an obstacle used to bleed the meter via its yaw chatter and
            // never escalate. Now only real displacement decays it, so a genuine wedge climbs the ladder and gets unjammed.
            if (FrustrationMeter > 0 && MakingNetProgress)
            {
                FrustrationMeter = Mathf.Max(0, FrustrationMeter - Mathf.Max(1, KickStart.AIClockPeriod / 2));
            }
            ThrottleState = AIThrottleState.FullSpeed;
            if (direct.DriveDir == EDriveFacing.Backwards)
            {
                ThrottleState = AIThrottleState.ForceSpeed;
                DriveVar = -1;

                if (Urgency >= 0)
                    Urgency += KickStart.AIClockPeriod / 5f;
                if (UrgencyOverload > AIGlobals.UrgencyOverloadReconsideration)
                {
                    AIECore.AIMessage(tech: tank, ref hasMessaged, tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    EstTopSped = 1;
                    AvoidStuff = true;
                    UrgencyOverload = 0;
                }
                else if (useRush && dist > MaxObjectiveRange * 2)
                {
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = -1f;
                    Urgency += KickStart.AIClockPeriod / 5f;
                }
                else if (AIGlobals.UnjamUpdateStart < FrustrationMeter)
                {
                    IsTryingToUnjam = true;
                    FrustrationMeter += KickStart.AIClockPeriod;
                    if (AIGlobals.UnjamUpdateEnd < FrustrationMeter)
                    {   // REVISED: ceiling reset no longer hard-stops the core (SettleDown(false)) so the hand-off
                        // back to the standard AI keeps momentum instead of dead-stopping in place each cycle.
                        SettleDown(false);
                        return;
                    }
                    else if (AIGlobals.UnjamUpdateDrop < FrustrationMeter)
                    {
                        ControlCore.DriveToFacingTowards();
                        ForceSetBeam = false;
                        ThrottleState = AIThrottleState.ForceSpeed;
                        DriveVar = 1;
                    }
                    else
                    {
                        ControlCore.DriveToFacingTowards();
                        ThrottleState = AIThrottleState.ForceSpeed;
                        DriveVar = 1;
                        // REVISED: only force the build-beam when actually tipped over - the beam is for righting a
                        // flipped tech, not freeing an upright-but-stuck one (which only flickered beam up/drop).
                        ForceSetBeam = AIEBeam.IsTechTippedOver(tank, this);
                    }
                }
                else if (AIGlobals.UnjamUpdateFire < FrustrationMeter)
                {
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = -0.5f;
                }
                else
                {
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = -1f;
                }
            }
            else
            {
                ThrottleState = AIThrottleState.ForceSpeed;
                DriveVar = 1;

                if (Urgency >= 0)
                    Urgency += KickStart.AIClockPeriod / 5f;
                if (UrgencyOverload > AIGlobals.UrgencyOverloadReconsideration)
                {
                    AIECore.AIMessage(tech: tank, ref hasMessaged, tank.name + ": Overloaded urgency!  ReCalcing top speed!");
                    EstTopSped = 1;
                    AvoidStuff = true;
                    UrgencyOverload = 0;
                }
                else if (useRush && dist > MaxObjectiveRange * 2)
                {
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = 1f;
                    Urgency += KickStart.AIClockPeriod / 5f;
                }
                else if (AIGlobals.UnjamUpdateStart < FrustrationMeter)
                {
                    IsTryingToUnjam = true;
                    FrustrationMeter += KickStart.AIClockPeriod;
                    if (AIGlobals.UnjamUpdateEnd < FrustrationMeter)
                    {   // REVISED: ceiling reset no longer hard-stops the core (SettleDown(false)) so the hand-off
                        // back to the standard AI keeps momentum instead of dead-stopping in place each cycle.
                        SettleDown(false);
                        return;
                    }
                    else if (AIGlobals.UnjamUpdateDrop < FrustrationMeter)
                    {
                        ForceSetBeam = false;
                        ControlCore.DriveAwayFacingTowards();
                        ThrottleState = AIThrottleState.ForceSpeed;
                        DriveVar = -1;
                    }
                    else
                    {
                        ControlCore.DriveAwayFacingTowards();
                        ThrottleState = AIThrottleState.ForceSpeed;
                        DriveVar = -1;
                        // REVISED: only force the build-beam when actually tipped over (see backwards branch).
                        ForceSetBeam = AIEBeam.IsTechTippedOver(tank, this);
                    }
                }
                else if (AIGlobals.UnjamUpdateFire < FrustrationMeter)
                {
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    if (useGun)
                        RemoveObstruction();
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = 0.5f;
                }
                else
                {
                    FrustrationMeter += KickStart.AIClockPeriod;
                    UrgencyOverload += KickStart.AIClockPeriod;
                    ThrottleState = AIThrottleState.ForceSpeed;
                    DriveVar = 1f;
                }
            }
        }
        private Transform GetObstruction(float searchRad)
        {
            List<Visible> ObstList;
            if (tank.rbody)
                ObstList = AIEPathing.ObstructionAwareness(tank.boundsCentreWorldNoCheck + SafeVelocity, this, searchRad);
            else
                ObstList = AIEPathing.ObstructionAwareness(tank.boundsCentreWorldNoCheck, this, searchRad);
            int bestStep = 0;
            float bestValue = 250000;
            int steps = ObstList.Count;
            if (steps <= 0)
            {
                return null;
            }
            for (int stepper = 0; steps > stepper; stepper++)
            {
                float temp = Mathf.Clamp((ObstList.ElementAt(stepper).centrePosition - tank.boundsCentreWorldNoCheck).sqrMagnitude - ObstList.ElementAt(stepper).Radius, 0, 500);
                if (bestValue > temp && temp != 0)
                {
                    bestStep = stepper;
                    bestValue = temp;
                }
            }
            return ObstList.ElementAt(bestStep).trans;
        }
        // REVISED: re-fetches the obstruction when the cached Obst has drifted out of 1.5x searchRad (was only
        // re-fetched when null). Restored the unconditional FIRE_ALL = true: the fire phase needs it to actually
        // shoot the obstacle clear - without it a tech stuck on destructible scenery could never blast free and
        // looped the beam-recovery. The Obsticle weapon state aims at Obst, so the fire is directed at the obstacle.
        public void RemoveObstruction(float searchRad = 12)
        {
            float staleRadSqr = (searchRad * 1.5f) * (searchRad * 1.5f);
            bool outOfRange = Obst != null
                && (Obst.position - tank.boundsCentreWorldNoCheck).sqrMagnitude > staleRadSqr;
            if (Obst == null || outOfRange)
            {
                Obst = GetObstruction(searchRad);
                Urgency += KickStart.AIClockPeriod / 5f;
            }
            FIRE_ALL = true;
        }
        // REVISED: SettleDown now does a full unjam reset - clears IsTryingToUnjam, ForceSetBeam, BeamTimeoutClock, FIRE_ALL, the Obsticle weapon state, and (unless stopCore=false) issues a Stop via SetCoreControlStop.
        public void SettleDown(bool stopCore = true)
        {
            UrgencyOverload = 0;
            Urgency = 0;
            FrustrationMeter = 0;
            Obst = null;
            IsTryingToUnjam = false;
            if (stopCore)
                SetCoreControlStop();
            ForceSetBeam = false;
            if (WeaponState == AIWeaponState.Obsticle)
                WeaponState = AIWeaponState.Normal;
            BeamTimeoutClock = 0;
            FIRE_ALL = false;
        }
    }
}
