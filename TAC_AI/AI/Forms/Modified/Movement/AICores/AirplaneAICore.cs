using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    /// REVISED (overview): now implements IAirMovementAICore (IsFixedWing => true). The scattered
    /// PerformDiveAttack integer writes are replaced by a real 4-state dive FSM (DiveState
    /// Idle/Approach/Commit/Recover) ticked once per DriveMaintainer; PerformDiveAttack is now a
    /// read-only HUD shim derived from diveState. Aim point comes from a priority resolver
    /// (ResolveAimPos) and dive entry/commit is gated by altitude advantage with Recover hold/escape
    /// timers and a post-recover cooldown. Combat now backs off facing the enemy (DriveAwayFacingTowards),
    /// gates on WantsToFight (was AttackEnemy), and U-Turn gained a roll-level stage 4.
    // REVISED: split into feature-complete partials (Step 3) - Combat nav -> AirplaneAICore.Combat.cs,
    // low-level flight control (U-turn/roll/steering/throttle/ground-avoid) -> AirplaneAICore.FlightControl.cs.
    internal partial class AirplaneAICore : IAirMovementAICore
    {
        public static bool DoPlayerAutopilotAirLogging = false;

        internal AIControllerAir pilot;
        internal TankAIHelper Helper => pilot.Helper;
        private float groundOffset => AIGlobals.GroundOffsetAircraft + Helper.lastTechExtents;
        public float GetDrive => pilot.CurrentThrottle;
        // REVISED: IAirMovementAICore identity exposed for cross-pipeline fixed-wing branching.
        public bool IsRotorcraft => false;
        public virtual bool IsFixedWing => true;
        public bool BankOnly = false;

        // REVISED: dive logic is now a proper FSM. diveState is the only persisted dive state (writer:
        // TransitionTo); nextApproachAllowedTime/commitAltLowSince/diveStateEnteredAt drive cooldown,
        // commit alt-low hysteresis, and Recover hold/escape timers. PerformDiveAttack became a HUD shim.
        internal enum DiveState { Idle, Approach, Commit, Recover }
        internal DiveState diveState = DiveState.Idle;
        private float nextApproachAllowedTime = 0f;
        private float commitAltLowSince = -1f;
        private float diveStateEnteredAt = 0f;
        public int PerformDiveAttack
        {
            get
            {
                switch (diveState)
                {
                    case DiveState.Approach: return 1;
                    case DiveState.Commit:   return 2;
                    case DiveState.Recover:  return 3;
                    default:                 return 0;
                }
            }
        }
        public int PerformUTurn = 0;

        public virtual void Initiate(Tank tank, IMovementAIController pilotSet)
        {
            pilot = (AIControllerAir) pilotSet;
            pilot.FlyStyle = AIControllerAir.FlightType.Aircraft;
            Helper.GroundOffsetHeight = Helper.lastTechExtents + AIGlobals.GroundOffsetAircraft;
            float GravityForce = tank.rbody.mass * tank.GetGravityScale() * TankAIManager.GravMagnitude;
            float totalFwdThrust = pilot.FwdThrust + pilot.BoosterThrust * AIGlobals.BoosterThrustBias;
            BankOnly = totalFwdThrust < AIGlobals.ImmelmanTtWRThreshold * GravityForce;

            if (BankOnly)
            {
                DebugTAC_AI.LogAISetup(KickStart.ModID + ": Tech " + tank.name + " does not apply enough forwards thrust " +
                    totalFwdThrust + " vs " + (AIGlobals.ImmelmanTtWRThreshold * GravityForce) + " to perform an immelmann.");
            }
        }

        /// <summary>
        /// Drives the Tech to the desired location (AIControllerAir.PathPointSet) in world space
        /// REVISED: destination field is now AIControllerAir.PathPointSet, changed from AirborneDest.
        /// </summary>
        /// <param name="thisControl"></param>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        /// <returns></returns>
        public virtual bool DriveMaintainer(TankAIHelper helper, Tank tank, ref EControlCoreSet core)
        {
            if (pilot.Grounded)
            {
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": " + tank.name + " is GROUNDED!!!");
                if (!AIEPathing.AboveHeightFromGround(tank.boundsCentreWorldNoCheck, helper.lastTechExtents * 2))
                {
                    DriveMaintainerEmergLand(helper, tank, ref core);
                    return false;
                }
                // REVISED: downed (above-height) plane now resets the dive FSM to Idle, cuts throttle and
                // glides nose-down forward instead of the previous behaviour.
                if (diveState != DiveState.Idle) TransitionTo(DiveState.Idle);
                pilot.MainThrottle = 0;
                PerformUTurn = 0;
                pilot.UpdateThrottle(helper);
                Vector3 glide = tank.rootBlockTrans.forward;
                glide.y = 0;
                glide = glide.normalized;
                glide.y = -0.3f;
                AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck + (glide * 200));
                return true;
            }

            if (tank.beam.IsActive && helper.recentSpeed < 8)
            {
                pilot.MainThrottle = 0;
                PerformUTurn = 0;
                pilot.UpdateThrottle(helper);
                Vector3 flat = tank.rootBlockTrans.forward;
                flat.y = 0;
                flat = flat.normalized;
                flat.y = 0.5f;
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank,KickStart.ModID + ": Tech " + tank.name + " is in build beam");
                AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck + (flat * 1000));
            }
            else if (tank.grounded || pilot.ForcePitchUp)
            {
                pilot.MainThrottle = 1;
                PerformUTurn = 0;
                pilot.UpdateThrottle(helper);
                Vector3 flat = tank.rootBlockTrans.forward;
                flat.y = 0;
                flat = flat.normalized;
                flat.y = 1f;
                if (DoPlayerAutopilotAirLogging)
                    DebugTAC_AI.LogSpecific(tank, KickStart.ModID + ": Tech " + tank.name + " is grounded: " + tank.grounded + " | is ForcePitchUp: " + pilot.ForcePitchUp);
                AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck + (flat * 1000));
            }
            else
            {
                // REVISED: normal flight now ticks the dive FSM (when dive-eligible or mid-dive) and lets it
                // own the tick while non-Idle; falls through to shared U-turn / cruise otherwise. Replaces the
                // old inline PerformDiveAttack==1/2 + UTurn branching.
                Vector3? aimPosOpt = ResolveAimPos(helper, tank);
                bool diveEligible = pilot.TargetGrounded && aimPosOpt.HasValue;
                if (diveEligible || diveState != DiveState.Idle)
                {
                    TickDiveStateMachine(helper, tank, aimPosOpt);
                    if (diveState != DiveState.Idle)
                        return true;
                }

                if (TryRunUTurn(helper, tank))
                    return true;

                pilot.MainThrottle = pilot.AdvisedThrottle;
                pilot.UpdateThrottle(helper);
                AngleTowards(helper, tank, pilot, pilot.PathPointSet);
            }

            return true;
        }


        private void TransitionTo(DiveState next)
        {
            diveState = next;
            diveStateEnteredAt = Time.time;
        }

        /// REVISED: dive aim-point priority resolver (enemy -> resource -> base -> cached lastDestinationOp).
        /// Reads lastDestinationOp (operator intent), NOT lastDestinationCore which DriveDirector overwrites
        /// and can default to zero. Cached fallback is gated on !IsControlOperatorStale and bounded by
        /// DiveCachedAimMaxRange so a stale, map-distant goal can't spin up an Approach. Null => no dive.
        private Vector3? ResolveAimPos(TankAIHelper helper, Tank tank)
        {
            if (helper.lastEnemyGet?.tank != null)
                return helper.lastEnemyGet.tank.boundsCentreWorldNoCheck;
            if (helper.theResource != null)
                return helper.theResource.tank != null
                    ? helper.theResource.tank.boundsCentreWorldNoCheck
                    : helper.theResource.centrePosition;
            if (helper.theBase != null)
                return helper.theBase.boundsCentreWorldNoCheck;
            Vector3 cached = helper.lastDestinationOp;
            if (cached != Vector3.zero
                && !helper.IsControlOperatorStale
                && (cached - tank.boundsCentreWorldNoCheck).sqrMagnitude
                    < AIGlobals.DiveCachedAimMaxRange * AIGlobals.DiveCachedAimMaxRange)
                return cached;
            return null;
        }

        private static float MyAGLAboveTarget(Vector3 aimPosWorld, Tank tank)
        {
            if (AIEPathMapper.GetAltitudeLoadedOnly(aimPosWorld, out float groundH))
                return tank.boundsCentreWorldNoCheck.y - groundH;
            return tank.boundsCentreWorldNoCheck.y - aimPosWorld.y;
        }

        /// REVISED: shared U-turn / reorient dispatch extracted from the old inline branches. PerformUTurn>0
        /// runs UTurn; ==-1 reorients toward PathPointSet. Returns true when the tick was consumed.
        private bool TryRunUTurn(TankAIHelper helper, Tank tank)
        {
            if (PerformUTurn > 0)
            {
                UTurn(helper, tank, pilot);
                return true;
            }
            if (PerformUTurn == -1)
            {
                pilot.MainThrottle = 1f;
                pilot.UpdateThrottle(helper);
                AngleTowards(helper, tank, pilot, pilot.PathPointSet);
                if (Vector3.Dot(tank.rootBlockTrans.forward,
                        (pilot.PathPointSet - tank.boundsCentreWorldNoCheck).normalized) > 0)
                    PerformUTurn = 0;
                return true;
            }
            return false;
        }

        /// REVISED: the dive FSM proper (replaces the scattered PerformDiveAttack int logic). Idle->Approach
        /// needs a grounded target ahead, staged distance, and the post-recover cooldown elapsed;
        /// Approach->Commit requires nose-on (hNorm.z>=0.92) AND altAdvantage>=MinDiveAGL; Commit->Recover on
        /// nose-up/too-close/ForcePitchUp or sustained low altitude (CommitRecoverAltHysteresis); Recover holds
        /// MinRecoverHold..MaxRecoverHold then re-arms with a cooldown. Lost target mid-dive forces Recover with
        /// a synthetic 500m climb-out. U-turn runs except in Recover (climb-out wins).
        private void TickDiveStateMachine(TankAIHelper helper, Tank tank, Vector3? aimPosNullable)
        {
            if (!aimPosNullable.HasValue)
            {
                if (diveState != DiveState.Idle && diveState != DiveState.Recover)
                    TransitionTo(DiveState.Recover);
                if (diveState == DiveState.Idle) return;
                aimPosNullable = tank.boundsCentreWorldNoCheck + Vector3.up * 500f;
            }
            Vector3 aimPos = aimPosNullable.Value;

            Vector3 posOffset = aimPos - helper.DodgeSphereCenter;
            float dist = posOffset.magnitude;
            float dist2D = posOffset.SetY(0).magnitude;
            Vector3 headingLocal = tank.rootBlockTrans.InverseTransformDirection(aimPos - tank.boundsCentreWorldNoCheck);
            Vector3 hNorm = headingLocal.sqrMagnitude > 1e-6f ? headingLocal.normalized : Vector3.forward;
            float altAdvantage = MyAGLAboveTarget(aimPos, tank);

            switch (diveState)
            {
                case DiveState.Idle:
                    if (pilot.TargetGrounded && dist2D > AIGlobals.GroundAttackStagingDist && headingLocal.z < 0
                        && Time.time >= nextApproachAllowedTime)
                        TransitionTo(DiveState.Approach);
                    break;
                case DiveState.Approach:
                    if (!pilot.TargetGrounded)
                        TransitionTo(DiveState.Idle);
                    else if (pilot.ForcePitchUp || dist < 32f)
                        TransitionTo(DiveState.Recover);
                    else if (headingLocal.z > 0 && hNorm.z >= 0.92f
                             && altAdvantage >= AIGlobals.MinDiveAGL
                             && PerformUTurn == 0)
                    {
                        commitAltLowSince = -1f;
                        TransitionTo(DiveState.Commit);
                    }
                    break;
                case DiveState.Commit:
                    {
                        bool altTooLow = altAdvantage < AIGlobals.MinDiveAGL * 0.4f;
                        if (altTooLow)
                        {
                            if (commitAltLowSince < 0f) commitAltLowSince = Time.time;
                        }
                        else commitAltLowSince = -1f;
                        bool altAbort = altTooLow && (Time.time - commitAltLowSince >= AIGlobals.CommitRecoverAltHysteresis);
                        if (headingLocal.z < 0 || dist < 32f || pilot.ForcePitchUp || altAbort)
                            TransitionTo(DiveState.Recover);
                    }
                    break;
                case DiveState.Recover:
                    if ((Time.time - diveStateEnteredAt > AIGlobals.MinRecoverHold
                         && altAdvantage > AIGlobals.MinDiveAGL)
                        || Time.time - diveStateEnteredAt > AIGlobals.MaxRecoverHold)
                    {
                        TransitionTo(DiveState.Idle);
                        nextApproachAllowedTime = Time.time + AIGlobals.PostRecoverCooldown;
                    }
                    break;
            }

            if (diveState == DiveState.Idle) return;

            if (diveState != DiveState.Recover && TryRunUTurn(helper, tank))
                return;

            switch (diveState)
            {
                case DiveState.Approach:
                    pilot.MainThrottle = 1f;
                    pilot.UpdateThrottle(helper);
                    if (headingLocal.z < 0.35f)
                    {
                        Vector3 AwayFlat = (tank.boundsCentreWorldNoCheck - pilot.PathPointSet).normalized;
                        AwayFlat.y = 0;
                        AwayFlat = AwayFlat.normalized;
                        AwayFlat.y = 0.2f;
                        AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck + AwayFlat.normalized * 1000f);
                    }
                    else
                    {
                        AngleTowards(helper, tank, pilot, pilot.LargeAircraft ? pilot.PathPointSet : aimPos);
                    }
                    break;
                case DiveState.Commit:
                    if (Helper.GetSpeed() < AIGlobals.AirStallSpeed + 16f || headingLocal.y > -0.25f)
                        pilot.AdvisedThrottle = 1f;
                    else
                        pilot.AdvisedThrottle = 0f;
                    pilot.MainThrottle = pilot.AdvisedThrottle;
                    pilot.UpdateThrottle(helper);
                    AngleTowards(helper, tank, pilot, pilot.LargeAircraft ? pilot.PathPointSet : aimPos);
                    break;
                case DiveState.Recover:
                    pilot.MainThrottle = 1f;
                    pilot.UpdateThrottle(helper);
                    AngleTowards(helper, tank, pilot, tank.boundsCentreWorldNoCheck + Vector3.up * 500f);
                    break;
            }
        }

        /// <summary>
        /// A very limited version of the VehicleAICore DriveMaintainer for downed aircraft
        /// REVISED: VehicleAICore was removed; this mirrors the equivalent land logic now in LandAICore.DriveMaintainer.
        /// </summary>
        /// <param name="thisControl"></param>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        /// <returns></returns>
        public bool DriveMaintainerEmergLand(TankAIHelper helper, Tank tank, ref EControlCoreSet core)
        {
            TankControl.ControlState control3D = (TankControl.ControlState)VehicleUtils.controlGet.GetValue(tank.control);

            control3D.m_State.m_InputRotation = Vector3.zero;
            control3D.m_State.m_InputMovement = Vector3.zero;
            VehicleUtils.controlGet.SetValue(tank.control, control3D);
            // REVISED: steer reference is now lastDestinationCore, changed from lastDestinationOp.
            Vector3 destDirect = helper.lastDestinationCore - tank.boundsCentreWorldNoCheck;
            // DEBUG FOR DRIVE ERRORS
            if (AIGlobals.ShowDebugFeedBack)
                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 0, destDirect, new Color(0, 1, 1));

            helper.DriveControl = 0f;
            if (helper.DoSteerCore)
            {
                if (helper.AdviseAwayCore)
                {   //Move from target
                    if (core.DriveDir == EDriveFacing.Backwards)//EDriveType.Backwards
                    {   // Face back TOWARDS target
                        VehicleUtils.Turner(helper, -destDirect, 0, ref core);
                        helper.DriveControl = 1f;
                    }
                    else if (core.DriveDir == EDriveFacing.Perpendicular)
                    {   //Drive to target driving sideways, but obey distance
                        VehicleUtils.Turner(helper, -destDirect, 0, ref core);
                        //DebugTAC_AI.Log("Orbiting away");
                        helper.DriveControl = 1f;
                    }
                    else
                    {   // Face front TOWARDS target
                        VehicleUtils.Turner(helper, destDirect, 0, ref core);
                        helper.DriveControl = -1f;
                    }
                }
                else if (core.DriveDir == EDriveFacing.Perpendicular)
                {   //Drive to target driving sideways, but obey distance
                    //int range = (int)(destDirect).magnitude;
                    float range = helper.lastOperatorRange;
                    if (range < helper.AutoSpacing + 2)
                    {
                        VehicleUtils.Turner(helper, -destDirect, 0, ref core);
                        //DebugTAC_AI.Log("Orbiting out " + helper.MinimumRad + " | " + destDirect);
                    }
                    else if (range > helper.AutoSpacing + 22)
                    {
                        VehicleUtils.Turner(helper, destDirect, 0, ref core);
                        //DebugTAC_AI.Log("Orbiting in " + helper.MinimumRad);
                    }
                    else  //ORBIT!
                    {
                        Vector3 aimDirect;
                        if (Vector3.Dot(destDirect.normalized, tank.rootBlockTrans.right) < 0)
                            aimDirect = Vector3.Cross(destDirect.normalized, Vector3.down);
                        else
                            aimDirect = Vector3.Cross(destDirect.normalized, Vector3.up);
                        VehicleUtils.Turner(helper, aimDirect, 0, ref core);
                        //DebugTAC_AI.Log("Orbiting hold " + helper.MinimumRad);
                    }
                    helper.DriveControl = 1f;
                }
                else
                {
                    VehicleUtils.Turner(helper, destDirect, 0, ref core);//Face the music
                                                                                    //DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  driving to " + helper.lastDestination);
                    if (helper.AutoSpacing > 0)
                    {
                        //if (helper.DriveDir == EDriveType.Perpendicular)
                        //    helper.DriveControl = 1f;
                        float range = helper.lastOperatorRange;
                        if (core.DriveDir <= EDriveFacing.Neutral)
                            helper.DriveControl = 0f;
                        else if (range < helper.AutoSpacing - 1)
                        {
                            if (core.DriveDir == EDriveFacing.Forwards)
                                helper.DriveControl = -1f;
                            else if (core.DriveDir == EDriveFacing.Backwards)
                                helper.DriveControl = 1f;
                            else
                                helper.DriveControl = 0;

                        }
                        else if (range > helper.AutoSpacing + 1)
                        {
                            if (core.DriveDir == EDriveFacing.Forwards)
                                helper.DriveControl = 1f;
                            else if (core.DriveDir == EDriveFacing.Backwards)
                                helper.DriveControl = -1f;
                            else
                                helper.DriveControl = 1f;
                        }
                    }
                }
            }
            else
                helper.DriveControl = 0;

            // Overrides to translational drive
            if (core.DriveDir == EDriveFacing.Stop)
            {
                helper.DriveControl = 0f;
                return true;
            }
            if (core.DriveDir == EDriveFacing.Neutral)
            {   // become brakeless
                helper.DriveControl = 0.001f;
                return true;
            }

            // Operate normally
            switch (helper.ThrottleState)
            {
                case AIThrottleState.PivotOnly:
                    helper.DriveControl = 0;
                    break;
                case AIThrottleState.Yield:
                    if (core.DriveDir == EDriveFacing.Backwards)
                    {
                        if (helper.recentSpeed > 10)
                            helper.DriveControl = 0.2f;
                        else
                            helper.DriveControl = -1f;
                    }
                    else
                    {   // works with forwards
                        if (helper.recentSpeed > 10)
                            helper.DriveControl = -0.2f;
                        else
                            helper.DriveControl = 1f;
                    }
                    break;
                case AIThrottleState.FullSpeed:
                    // REVISED: FullSpeed now always drives 1; dropped the FullBoost/LightBoost gate.
                    helper.DriveControl = 1;
                    break;
                case AIThrottleState.ForceSpeed:
                    helper.DriveControl = helper.DriveVar;
                    // Downed Aircraft can't boost as their engines are damaged
                    if (helper.FullBoost || helper.LightBoost)
                        helper.DriveControl = 1;
                    break;
                default:
                    break;
            }
            return true;
        }

        /// <summary>
        /// Player automatic AI version (player following)
        /// Declares 3D points in WORLD space (PathPointSet)
        /// REVISED: destination field is now PathPointSet, changed from AirborneDest.
        /// </summary>
        /// <returns>Execution was successful</returns>
        public bool DriveDirector(ref EControlCoreSet core)
        {
            pilot.AdvisedThrottle = -1;
            Helper.AutoSpacing = AIGlobals.AircraftDestSuccessRadius + Helper.lastTechExtents;
            if (Helper.IsMultiTech)
            {   //Override and disable most driving abilities
                pilot.PathPointSet = MultiTechUtils.HandleMultiTech(Helper, pilot.Tank, ref core);
                return true;
            }
            else if (Helper.DriveDestDirected == EDriveDest.ToBase)
            {
                pilot.AdvisedThrottle = -1;
                pilot.LowerEngines = true;
                if (Helper.lastBasePos.IsNotNull())
                {
                    core.DriveDir = EDriveFacing.Forwards;
                    pilot.PathPointSet = Helper.AvoidAssistPrecise(Helper.lastBasePos.position);
                }
                // REVISED: when within DestSuccessRad, sidestep to orbit; dropped the else-branch that
                // overwrote PathPointSet with lastDestinationOp (same removal applies in ToMine/Aegis below).
                if ((pilot.PathPointSet - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                {
                    pilot.PathPointSet += (-pilot.Tank.rootBlockTrans.right.SetY(0).normalized * 129);
                }
                pilot.TargetGrounded = !AIEPathing.AboveHeightFromGround(Helper.lastDestinationOp, pilot.AerofoilSluggishness + groundOffset);
            }
            else if (Helper.DriveDestDirected == EDriveDest.ToMine)
            {
                pilot.AdvisedThrottle = -1;
                if (Helper.theResource.tank != null)
                {
                    pilot.LowerEngines = true;
                    if (Helper.ThrottleState == AIThrottleState.PivotOnly)
                    {
                        core.DriveDir = EDriveFacing.Forwards;
                        core.lastDestination = Helper.theResource.tank.boundsCentreWorldNoCheck;
                        pilot.PathPointSet = core.lastDestination;
                    }
                    else
                    {
                        if (Helper.FullMelee)
                        {
                            core.DriveDir = EDriveFacing.Forwards;
                            core.lastDestination = Helper.theResource.tank.boundsCentreWorldNoCheck;
                            pilot.PathPointSet = core.lastDestination;
                            Helper.AutoSpacing = 2;
                        }
                        else
                        {
                            core.DriveDir = EDriveFacing.Forwards;
                            core.lastDestination = Helper.theResource.tank.boundsCentreWorldNoCheck;
                            pilot.PathPointSet = Helper.AvoidAssistPrecise(core.lastDestination);
                            Helper.AutoSpacing = Helper.lastTechExtents + 2;
                        }
                    }
                }
                else
                {
                    pilot.LowerEngines = false;
                    if (Helper.ThrottleState == AIThrottleState.PivotOnly)
                    {
                        core.DriveDir = EDriveFacing.Forwards;
                        core.lastDestination = Helper.theResource.trans.position;
                        pilot.PathPointSet = core.lastDestination;
                    }
                    else
                    {
                        if (Helper.FullMelee)
                        {
                            core.DriveDir = EDriveFacing.Forwards;
                            core.lastDestination = Helper.theResource.trans.position;
                            pilot.PathPointSet = Helper.AvoidAssistPrecise(core.lastDestination);
                        }
                        else
                        {
                            core.DriveDir = EDriveFacing.Forwards;
                            core.lastDestination = Helper.theResource.centrePosition;
                            pilot.PathPointSet = Helper.AvoidAssistPrecise(core.lastDestination);
                        }
                    }
                }
                if ((pilot.PathPointSet - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                {
                    pilot.PathPointSet += GetOrbitFlight();
                }
                pilot.TargetGrounded = !AIEPathing.AboveHeightFromGround(Helper.lastDestinationOp, pilot.AerofoilSluggishness + groundOffset);
            }
            else if (Helper.DediAI == AIType.Aegis || (pilot.EnemyMind && pilot.EnemyMind.CommanderMind == EnemyAttitude.Guardian))
            {
                Helper.theResource = AIEPathing.ClosestUnanchoredAllyAegis(TankAIManager.GetTeamTanks(pilot.Tank.Team),
                    pilot.Tank.boundsCentreWorldNoCheck, Helper.MaxCombatRange * Helper.MaxCombatRange, out _,
                    pilot.Helper).visible;
                // REVISED: Aegis guard now records theGuardedAlly and only falls back to escort positioning
                // when out of operator range OR TryAdjustForCombat declines (was: always combat-adjust, then
                // clamp by lastCombatRange afterward). Out-of-range ignores enemy distance.
                Helper.theGuardedAlly = Helper.theResource;
                bool aegisOutOfRange = Helper.lastOperatorRange > Helper.MaxCombatRange;
                if (aegisOutOfRange || !TryAdjustForCombat(true, ref pilot.PathPointSet, ref core))
                {
                    if (aegisOutOfRange) Helper.IgnoreEnemyDistance();
                    if (Helper.theResource.IsLiveTechTarget())
                    {
                        if (Helper.DriveDestDirected == EDriveDest.FromLastDestination)
                        {
                            pilot.LowerEngines = false;
                            core.DriveDir = EDriveFacing.Forwards;
                            core.lastDestination = Helper.theResource.tank.transform.position;
                            pilot.PathPointSet = core.lastDestination;
                        }
                        else if (Helper.DriveDestDirected == EDriveDest.ToLastDestination)
                        {
                            pilot.LowerEngines = true;
                            core.DriveDir = EDriveFacing.Forwards;
                            core.lastDestination = Helper.theResource.tank.transform.position;
                            pilot.PathPointSet = Helper.AvoidAssist(core.lastDestination);
                        }
                    }
                }
                if ((Helper.lastDestinationOp - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                {
                    pilot.PathPointSet += GetOrbitFlight();
                }
            }
            else
            {
                if (TryAdjustForCombat(false, ref pilot.PathPointSet, ref core))
                {
                    pilot.LowerEngines = true;
                }
                else
                {
                    if (Helper.DriveDestDirected == EDriveDest.ToLastDestination)
                    {
                        pilot.LowerEngines = true;
                        if ((Helper.lastDestinationOp - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                        {
                            pilot.PathPointSet = Helper.lastDestinationOp + (pilot.Tank.rootBlockTrans.forward * 500);
                        }
                        else
                        {
                            pilot.PathPointSet = Helper.lastDestinationOp;
                        }
                    }
                    else if (Helper.DriveDestDirected == EDriveDest.FromLastDestination)
                    {
                        pilot.LowerEngines = false;
                        pilot.PathPointSet = ((pilot.Tank.trans.position - Helper.lastDestinationOp).normalized * (pilot.DestSuccessRad * 2)) + pilot.Tank.boundsCentreWorldNoCheck;
                    }
                    else
                    {
                        if ((pilot.PathPointSet - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                        {
                            pilot.PathPointSet += GetOrbitFlight();
                        }
                        else
                        {
                            pilot.PathPointSet = Helper.lastDestinationOp;
                        }
                    }
                }
            }
            bool unresponsiveAir = pilot.LargeAircraft || BankOnly;

            bool NoRamOrTargetNotInPath;
            // REVISED: melee ram-gate now keys off Helper.WantsToFight, changed from Helper.AttackEnemy
            // (same swap recurs in the other DriveDirector variants and the LikelyMelee enemy branch).
            if (Helper.FullMelee && Helper.WantsToFight)
            {
                if (Helper.lastEnemyGet?.tank && pilot.Tank.rootBlockTrans.InverseTransformVector(Helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - pilot.Tank.boundsCentreWorldNoCheck).z > 0.75f)
                    NoRamOrTargetNotInPath = false;
                else
                    NoRamOrTargetNotInPath = true;
            }
            else
                NoRamOrTargetNotInPath = true;
            bool AvoidCrash = unresponsiveAir || NoRamOrTargetNotInPath;

            if (!Helper.FullMelee)
                pilot.PathPointSet = AIEPathing.OffsetFromGroundA(pilot.PathPointSet, Helper);
            pilot.PathPointSet = AIEPathing.ModerateMaxAlt(pilot.PathPointSet, Helper);
            pilot.PathPointSet = AvoidAssist(pilot.PathPointSet, pilot.Helper.DodgeSphereCenter);

            if (Helper.FullMelee && !unresponsiveAir)
                pilot.AdvisedThrottle = 1;
            else
                AdviseThrottle(pilot, Helper, pilot.Tank, pilot.PathPointSet);

            if (AvoidCrash && !pilot.TargetGrounded)
                PreventCollisionWithGround(pilot, groundOffset, unresponsiveAir);
            if (Helper.ThrottleState == AIThrottleState.Yield)
                pilot.ForcePitchUp = true;
            return true;
        }

        /// <summary>
        /// Player click-based AI version (player RTS line following)
        /// Declares 3D points in WORLD space (PathPointSet)
        /// REVISED: destination field is now PathPointSet, changed from AirborneDest.
        /// </summary>
        /// <returns>Execution was successful</returns>
        public bool DriveDirectorRTS(ref EControlCoreSet core)
        {
            pilot.AdvisedThrottle = -1;
            Helper.AutoSpacing = AIGlobals.AircraftDestSuccessRadius + Helper.lastTechExtents;

            if (Helper.IsMultiTech)
            {   //Override and disable most driving abilities
                pilot.PathPointSet = MultiTechUtils.HandleMultiTech(Helper, pilot.Tank, ref core);
                return true;
            }

            pilot.LowerEngines = true;
            if (!Helper.IsGoingToPositionalRTSDest)
            {   // We are pursuing
                if (!TryAdjustForCombat(false, ref pilot.PathPointSet, ref core)) // When set to chase then chase
                {
                    if ((Helper.lastDestinationOp - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                    {
                        pilot.PathPointSet += GetOrbitFlight();
                    }
                    else
                    {
                        pilot.PathPointSet = Helper.lastDestinationOp;
                    }
                }
            }
            else
            {
                Helper.IgnoreEnemyDistance();
                pilot.TargetGrounded = false;
                core.lastDestination = Helper.RTSDestination;
                pilot.PathPointSet = Helper.RTSDestination;
                if ((core.lastDestination - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                {
                    pilot.PathPointSet += GetOrbitFlight();
                }
                else
                {
                    pilot.PathPointSet = Helper.RTSDestination;
                }
            }

            bool unresponsiveAir = pilot.LargeAircraft || BankOnly;

            bool NoRamOrTargetNotInPath;
            if (Helper.FullMelee && Helper.WantsToFight)
            {
                if (Helper.lastEnemyGet?.tank && pilot.Tank.rootBlockTrans.InverseTransformVector(Helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - pilot.Tank.boundsCentreWorldNoCheck).z > 0.75f)
                    NoRamOrTargetNotInPath = false;
                else
                    NoRamOrTargetNotInPath = true;
            }
            else
                NoRamOrTargetNotInPath = true;
            bool AvoidCrash = unresponsiveAir || NoRamOrTargetNotInPath;

            if (AvoidCrash)
                pilot.PathPointSet = AIEPathing.OffsetFromGroundA(pilot.PathPointSet, Helper);
            pilot.PathPointSet = AIEPathing.ModerateMaxAlt(pilot.PathPointSet, Helper);
            pilot.PathPointSet = AvoidAssist(pilot.PathPointSet, pilot.Helper.DodgeSphereCenter);

            if (Helper.FullMelee && !unresponsiveAir)
                pilot.AdvisedThrottle = 1;
            else
                AdviseThrottle(pilot, Helper, pilot.Tank, pilot.PathPointSet);

            if (AvoidCrash && !pilot.TargetGrounded)
                PreventCollisionWithGround(pilot, groundOffset, unresponsiveAir);
            if (Helper.ThrottleState == AIThrottleState.Yield)
                pilot.ForcePitchUp = true;
            return true;
        }

        public bool DriveDirectorEnemyRTS(EnemyMind mind, ref EControlCoreSet core)
        {
            pilot.AdvisedThrottle = -1;
            Helper.AutoSpacing = AIGlobals.AircraftDestSuccessRadius + Helper.lastTechExtents;

            if (Helper.IsMultiTech)
            {   //Override and disable most driving abilities
                pilot.PathPointSet = MultiTechUtils.HandleMultiTech(Helper, pilot.Tank, ref core);
                return true;
            }

            pilot.LowerEngines = true;
            if (!Helper.IsGoingToPositionalRTSDest)
            {
                if (!TryAdjustForCombatEnemy(mind, ref pilot.PathPointSet, ref core)) // When set to chase then chase
                {
                    if ((Helper.lastDestinationOp - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                    {
                        pilot.PathPointSet += GetOrbitFlight();
                    }
                    else
                    {
                        pilot.PathPointSet = Helper.lastDestinationOp;
                    }
                }

            }
            else
            {
                Helper.IgnoreEnemyDistance();
                pilot.TargetGrounded = false;
                core.lastDestination = Helper.RTSDestination;
                pilot.PathPointSet = Helper.RTSDestination;
                if ((core.lastDestination - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                {
                    pilot.PathPointSet += GetOrbitFlight();
                }
                else
                {
                    pilot.PathPointSet = Helper.RTSDestination;
                }
            }

            bool unresponsiveAir = pilot.LargeAircraft || BankOnly;

            bool NoRamOrTargetNotInPath;
            if (Helper.FullMelee && Helper.WantsToFight)
            {
                if (Helper.lastEnemyGet?.tank && pilot.Tank.rootBlockTrans.InverseTransformVector(Helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - pilot.Tank.boundsCentreWorldNoCheck).z > 0.75f)
                    NoRamOrTargetNotInPath = false;
                else
                    NoRamOrTargetNotInPath = true;
            }
            else
                NoRamOrTargetNotInPath = true;
            bool AvoidCrash = unresponsiveAir || NoRamOrTargetNotInPath;

            if (AvoidCrash)
                pilot.PathPointSet = AIEPathing.OffsetFromGroundA(pilot.PathPointSet, Helper);
            pilot.PathPointSet = AIEPathing.ModerateMaxAlt(pilot.PathPointSet, Helper);
            pilot.PathPointSet = AvoidAssist(pilot.PathPointSet, pilot.Helper.DodgeSphereCenter);

            if (Helper.FullMelee && !unresponsiveAir)
                pilot.AdvisedThrottle = 1;
            else
                AdviseThrottle(pilot, Helper, pilot.Tank, pilot.PathPointSet);

            if (AvoidCrash && !pilot.TargetGrounded)
                PreventCollisionWithGround(pilot, groundOffset, unresponsiveAir);
            if (Helper.ThrottleState == AIThrottleState.Yield)
                pilot.ForcePitchUp = true;
            return true;
        }

        /// <summary>
        /// Non-Player automatic AI version
        /// Declares 3D points in WORLD space (PathPointSet)
        /// REVISED: destination field is now PathPointSet, changed from AirborneDest.
        /// </summary>
        /// <returns>Execution was successful</returns>
        public bool DriveDirectorEnemy(EnemyMind mind, ref EControlCoreSet core)
        {
            pilot.AdvisedThrottle = -1;
            pilot.ForcePitchUp = false;
            Helper.AutoSpacing = AIGlobals.AircraftDestSuccessRadius + Helper.lastTechExtents;
            if (pilot.Grounded)
            {   //Become a ground vehicle for now
                if (!AIEPathing.AboveHeightFromGroundTech(Helper, Helper.lastTechExtents * 2))
                {
                    return false;
                }
                //Try fighting the controls to land safely

                return true;
            }
            if (TryAdjustForCombatEnemy(mind, ref pilot.PathPointSet, ref core))
            {
                pilot.LowerEngines = true;
            }
            else if (!mind.AttackPlayer)
            {
                if (Helper.DriveDestDirected == EDriveDest.ToLastDestination)
                {
                    core.lastDestination = AIEPathing.OffsetFromGroundA(Helper.lastDestinationOp, Helper);
                    if ((core.lastDestination - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                    {
                        pilot.PathPointSet = core.lastDestination + (pilot.Tank.rootBlockTrans.forward * 500);
                    }
                    else
                    {
                        pilot.PathPointSet = core.lastDestination;
                    }
                }
                else
                    // No-target idle: lift-only OffsetFromGroundA, not SnapOffsetFromGroundA. Snap would FORCE the
                    // PathPointSet down to terrain+offset altitude, and when the aircraft is currently above that the
                    // tech sees a downward heading - dives - PreventCollisionWithGround pulls it back up - next tick
                    // snaps it down again. Pure oscillation. Lift-only keeps the aircraft's current altitude.
                    pilot.PathPointSet = AIEPathing.OffsetFromGroundA(pilot.Tank.boundsCentreWorldNoCheck + pilot.Tank.rootBlockTrans.forward, Helper);
            }
            else
            {
                pilot.LowerEngines = false;
                if ((pilot.PathPointSet - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                {

                    pilot.PathPointSet += GetOrbitFlight();
                }
                else if (Helper.DriveDestDirected == EDriveDest.ToLastDestination)
                {
                    core.lastDestination = AIEPathing.OffsetFromGroundA(Helper.lastDestinationOp, Helper);
                    if ((core.lastDestination - pilot.Tank.boundsCentreWorldNoCheck).magnitude < pilot.DestSuccessRad)
                    {
                        pilot.PathPointSet = core.lastDestination + (pilot.Tank.rootBlockTrans.forward * 500);
                    }
                    else
                    {
                        pilot.PathPointSet = core.lastDestination;
                    }
                }
                else if (Helper.DriveDestDirected == EDriveDest.FromLastDestination)
                {
                    pilot.PathPointSet = ((pilot.Tank.boundsCentreWorldNoCheck - AIEPathing.OffsetFromGroundA(Helper.lastDestinationOp, Helper))
                        .normalized * (pilot.DestSuccessRad * 2)) + pilot.Tank.boundsCentreWorldNoCheck;
                }
                else
                {
                    Helper.lastPlayer = Helper.GetPlayerTech();
                    if (Helper.lastPlayer.IsLiveTechTarget())
                    {
                        pilot.PathPointSet.y = (Helper.lastPlayer.tank.boundsCentreWorldNoCheck + (Vector3.up * (Helper.GroundOffsetHeight / 5))).y;
                    }
                    else
                    {
                        // Lift-only (see comment above) - prevents the nose-dive / pull-up oscillation when there's
                        // no live target and lastPlayer is unavailable.
                        pilot.PathPointSet = AIEPathing.OffsetFromGroundA(pilot.Tank.boundsCentreWorldNoCheck + pilot.Tank.rootBlockTrans.forward, Helper);

                    }
                }
            }
            bool unresponsiveAir = pilot.LargeAircraft || BankOnly;
            bool NoRamOrTargetNotInPath;
            if (mind.LikelyMelee && Helper.WantsToFight)
            {
                if (Helper.lastEnemyGet?.tank && pilot.Tank.rootBlockTrans.InverseTransformVector(Helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - pilot.Tank.boundsCentreWorldNoCheck).z > 0.75f)
                    NoRamOrTargetNotInPath = false;
                else
                    NoRamOrTargetNotInPath = true;
            }
            else
                NoRamOrTargetNotInPath = true;
            bool AvoidCrash = unresponsiveAir || NoRamOrTargetNotInPath;

            if (AvoidCrash)
                pilot.PathPointSet = AIEPathing.OffsetFromGroundA(pilot.PathPointSet, Helper);
            pilot.PathPointSet = Helper.AvoidAssistPrediction(pilot.PathPointSet, pilot.AerofoilSluggishness);

            if (Helper.FullMelee && !unresponsiveAir)
                pilot.AdvisedThrottle = 1;
            else
                AdviseThrottle(pilot, Helper, pilot.Tank, pilot.PathPointSet);

            pilot.PathPointSet = AIEPathing.ModerateMaxAlt(pilot.PathPointSet, Helper);

            if (AvoidCrash && !pilot.TargetGrounded)
                PreventCollisionWithGround(pilot, groundOffset, unresponsiveAir);
            if (Helper.ThrottleState == AIThrottleState.Yield)
                pilot.ForcePitchUp = true;
            return true;
        }

        // Combat navigation (TryAdjustForCombat / TryAdjustForCombatEnemy) moved to AirplaneAICore.Combat.cs (Step 3).

        private float Responsiveness => (AIGlobals.AerofoilSluggishnessBaseValue * 2) / pilot.AerofoilSluggishness;

        /// <summary>
        /// An airborne version of the Player AI pathfinding which handles obstructions
        /// </summary>
        /// <param name="targetIn"></param>
        /// <param name="predictionOffset"></param>
        /// <returns></returns>
        public Vector3 AvoidAssist(Vector3 targetIn, Vector3 predictionOffset)
        {
            //The method to determine if we should avoid an ally nearby while navigating to the target
            TankAIHelper helper = Helper;
            Tank tank = pilot.Tank;

            try
            {
                Tank lastCloseAlly;
                float lastAllyDist;
                // REVISED: dropped the "predictionOffset /= Responsiveness" rescale; the prediction offset
                // is now used as-is for ally-avoidance spacing.
                float moveSpace = (predictionOffset - pilot.Tank.boundsCentreWorldNoCheck).magnitude;
                HashSet<Tank> AlliesAlt = AIEPathing.AllyList(tank);
                if (helper.SecondAvoidence && AlliesAlt.Count > 1)// MORE processing power
                {
                    lastCloseAlly = AIEPathing.SecondClosestAllyPrecision(AlliesAlt, predictionOffset,
                        out Tank lastCloseAlly2, out lastAllyDist, out float lastAuxVal, pilot.Helper);
                    if (lastCloseAlly && lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                    {
                        if (lastCloseAlly2 && lastAuxVal < helper.lastTechExtents + lastCloseAlly2.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                        {
                            IntVector3 ProccessedVal2 = helper.GetOtherDir(lastCloseAlly) + helper.GetOtherDir(lastCloseAlly2);
                            return (targetIn + ProccessedVal2) / 3;
                        }
                        IntVector3 ProccessedVal = helper.GetOtherDir(lastCloseAlly);
                        return (targetIn + ProccessedVal) / 2;
                    }

                }
                lastCloseAlly = AIEPathing.ClosestAllyPrecision(AlliesAlt, predictionOffset, out lastAllyDist, pilot.Helper);
                if (lastCloseAlly == null)
                {
                    // DebugTAC_AI.Log(KickStart.ModID + ": ALLY IS NULL");
                    return targetIn;
                }
                if (lastAllyDist < helper.lastTechExtents + lastCloseAlly.GetCheapBounds() + AIGlobals.PathfindingExtraSpace + moveSpace)
                {
                    IntVector3 ProccessedVal = helper.GetOtherDir(lastCloseAlly);
                    return (targetIn + ProccessedVal) / 2;
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Crash on AvoidAssistAir " + e);
                return targetIn;
            }
            if (targetIn.IsNaN())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AvoidAssistAir IS NaN!!");
                //TankAIManager.FetchAllAllies();
            }
            return targetIn;
        }

        public Vector3 GetOrbitFlight()
        {
            Vector3 lFlat;
            if (pilot.Tank.rootBlockTrans.up.y > 0)
                lFlat = -pilot.Tank.rootBlockTrans.right + (pilot.Tank.rootBlockTrans.forward * 2);
            else
                lFlat = pilot.Tank.rootBlockTrans.right + (pilot.Tank.rootBlockTrans.forward * 2);
            lFlat.y = -0.1f;
            //DebugTAC_AI.Log(KickStart.ModID + ": GetOrbitFlight");
            return lFlat * 126;
        }
        public Vector3 Between(Vector3 Target, Vector3 other)
        {
            return (Target + other) / 2;
        }

        // Low-level flight control (U-turn / roll-upright / AngleTowards steering / throttle advice / ground-collision recovery / right-aligned roll vectors) moved to AirplaneAICore.FlightControl.cs (Step 3).
    }
}
