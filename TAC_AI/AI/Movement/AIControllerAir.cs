using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI
{
    internal class AIControllerAir : MovementControllerBase
    {
        internal static FieldInfo boostGet = typeof(Thruster).GetField("m_Force", BindingFlags.NonPublic | BindingFlags.Instance);
        //internal static FieldInfo boostDir = typeof(BoosterJet).GetField("m_LocalBoostDirection", BindingFlags.NonPublic | BindingFlags.Instance);
        //internal static FieldInfo fanDir = typeof(FanJet).GetField("m_LocalBoostDirection", BindingFlags.NonPublic | BindingFlags.Instance);

        public enum FlightType
        {
            Aircraft,   // Horizontal flight
            Helicopter, // Vertical flight
            VTOL,       // Both Horizontal and vertical flight
        }

        // Controller-internal flight-mode cache, set by each AICore.Initiate and read by this
        // controller's own throttle / wing / mayday logic (with its init-ordering semantics).
        // External / cross-pipeline consumers must query IAirMovementAICore (IsRotorcraft / IsFixedWing)
        // on AICore instead - this is no longer a cross-pipeline back-channel.
        internal FlightType FlyStyle;

        //Manuvering (Post-Pathfinding)
        public override Vector3 PathPoint => PathPointSet;// Aircraft-specific destination handling
        public Vector3 PathPointSet = Vector3.zero; // Aircraft-specific destination handling
        public float DestSuccessRad // When we have reached our airborne destination
        {
            get { try { return Helper.AutoSpacing; } catch { return 10; } }
        }

        // Forward for aircraft, Upwards for helicopters
        public float AdvisedThrottle = 0;               // Throttle to use when chasing or cruising
        public float MainThrottle = 0;                  // Ideal Throttle to chase after
        public float CurrentThrottle = 0;               // Throttle the craft knows it's going at

        public BlockManager.BlockIterator<ModuleBooster> Engines => Tank.blockman.IterateBlockComponents<ModuleBooster>();     // keep track of aircraft propultion
        public BlockManager.BlockIterator<ModuleAirBrake> Brakes => Tank.blockman.IterateBlockComponents<ModuleAirBrake>();     // keep tracck of airbrakes
        public BlockManager.BlockIterator<ModuleWing> Wings => Tank.blockman.IterateBlockComponents<ModuleWing>();          // keep track of the wings
        public bool NoProps = false;            // Do we have to rely on fuel only?
        public bool SkewedFlightCenter = false; // Are we going to struggle when turning?

        public float lastDataGatherTime = 0;
        public Vector3 PropBias = Vector3.zero; // Center of thrust (RAW) of all forwards props
        public float FwdThrust = 0;
        public float UpThrust = 0;
        public Vector3 BoostBias = Vector3.zero;// Center of thrust of all boosters, center of boost
        public float BoosterThrust = 0;
        public float UpTtWRatio = 0;

        public float SlowestPropLerpSpeed = 1;  // Slow action demand based on propeller responsiveness
        public float PropLerpValue = 10;        // aux value used for some engine calculations
        public float AerofoilSluggishness = 1;  // Slow action demand based on aerofoil responsiveness
        public float RollStrength = 1;          // How far to roll 90 degrees
        public Vector3 FlyingChillFactor = Vector3.one * 30;

        //Error-Checking
        public int ErrorsInTakeoff = 0;         // If this gets too high, then this tech isn't meant to fly
        public int ErrorsInUTurn = 0;           // If this gets too high, then this tech isn't meant to Immelmann
        public bool LargeAircraft = false;      // Restrict turning to 45 and no U-Turns
        //public float BoosterThrustBias = 0.5f;
        public bool ForcePitchUp = false;       // Emergency nose up
        public bool TakeOff = false;            // taking off from ground
        public bool Grounded = false;           // aircraft deemed too damaged to fly
        public bool TargetGrounded = false;     // Are we dealing with a target that is on the ground?
        public bool LowerEngines = false;       // Choppers: Too high! Too high!  Airplanes: Conserve booster fuel

        protected override void OnPreInitiate()
        {
            Tank.AttachEvent.Subscribe(OnAttach);
            Tank.DetachEvent.Subscribe(OnDetach);

            CurrentThrottle = 0;
            ErrorsInTakeoff = 0;
            ErrorsInUTurn = 0;
            TakeOff = true;
            Grounded = false;
            TargetGrounded = false;

            CheckAllFlightBlocks();   // computes PropBias / BoostBias / NoProps - SelectCore reads these
            CurrentThrottle = 0;

            DebugTAC_AI.Info(KickStart.ModID + ": (2) Tech " + Tank.name + " PropBias " + PropBias + ", BoostBias " + BoostBias);
        }

        protected override IMovementAICore SelectCore(Enemy.EnemyMind mind)
        {
            // Air sub-core is chosen purely from thrust geometry. The player and enemy ladders were
            // identical (P08 G.7 stopped the enemy branch from writing EvilCommander back), so they
            // unify here. NoProps techs fly on boosters, so they bias off BoostBias instead of PropBias.
            Vector3 bias = NoProps ? BoostBias : PropBias;
            if (bias.y > 0.6f)
                return new HelicopterAICore();   // vertical flight
            if (bias.y > 0.3f)
                return new VtolAICore();          // both horizontal and vertical
            return new AirplaneAICore();          // horizontal flight
        }

        protected override void OnPostInitiate()
        {
            // FlyStyle / RollStrength / PropLerpValue / FlyingChillFactor are populated by
            // CheckAllFlightBlocks (OnPreInitiate) and the chosen core's Initiate (run just before this).
            if (EnemyMind == null)
                DebugTAC_AI.LogAISetup(KickStart.ModID + ": Tech " + Tank.name + " has been assigned Non-NPT aircraft AI with flight mentality " + FlyStyle.ToString() + ", Roll intensity of " + RollStrength + ", Prop lerp of " + PropLerpValue + " and flying chill of " + FlyingChillFactor);
            else
                DebugTAC_AI.LogAISetup(KickStart.ModID + ": Tech " + Tank.name + " has been assigned Non-Player aircraft AI with " + EnemyMind.EvilCommander.ToString() + " mentality " + FlyStyle.ToString() + ", Roll intensity of " + RollStrength + ", Prop lerp of " + PropLerpValue + " and flying chill of " + FlyingChillFactor);
        }

        protected override void OnRecycle()
        {
            Tank.AttachEvent.Unsubscribe(OnAttach);
            Tank.DetachEvent.Unsubscribe(OnDetach);
            //DebugTAC_AI.Log(KickStart.ModID + ": Removed aircraft AI from " + Tank.name);
        }
        private void CheckEngines(bool firstCheck = false)
        {
            if (!firstCheck && Time.time < lastDataGatherTime)
                return;
            lastDataGatherTime = Time.time + 1f;
            float lowestDelta = 100;
            float guzzleLevel = 0;
            int consumeBoosters = 0;
            Vector3 biasDirection = Vector3.zero;
            Vector3 boostBiasDirection = Vector3.zero;

            FwdThrust = 0f;
            UpThrust = 0f;
            float boosterThrust = 0f;

            foreach (ModuleBooster module in Engines)
            {
                foreach (FanJet jet in module.transform.GetComponentsInChildren<FanJet>(true))
                {
                    float thrust = (float)RawTechBase.thrustRate.GetValue(jet);
                    float thrustRev = (float)RawTechBase.fanThrustRateRev.GetValue(jet);
                    Vector3 localFwd = jet.LocalThrustDirection;
                    Vector3 rawVec = localFwd * thrust;
                    if (localFwd.z < 0)
                        FwdThrust += -localFwd.z * thrustRev;
                    else
                        FwdThrust += Mathf.Max(localFwd.z * thrust, 0);
                    if (localFwd.y < 0)
                        UpThrust += -localFwd.y * thrustRev;
                    else
                        UpThrust += Mathf.Max(localFwd.y * thrust, 0);
                    biasDirection += new Vector3(Mathf.Abs(rawVec.x), Mathf.Abs(rawVec.y), Mathf.Abs(rawVec.z));
                    float spin = (float)RawTechBase.spinDat.GetValue(jet);
                    if (spin < lowestDelta)
                        lowestDelta = spin;
                }
                foreach (BoosterJet boost in module.transform.GetComponentsInChildren<BoosterJet>(true))
                {
                    Vector3 localFwd = -boost.LocalThrustDirection; // Booster force vector is negative
                    float force = (float)boostGet.GetValue(boost);
                    if (boost.ConsumesFuel)
                    {
                        consumeBoosters++;
                        guzzleLevel += boost.BurnRate;

                        if (localFwd.z > 0) // Booster force vector is negative
                            boosterThrust += Mathf.Max(localFwd.z * force, 0);
                        //We have to get the total thrust in here accounted for as well because the only way we CAN boost is ALL boosters firing!
                        boostBiasDirection += localFwd * force;
                    }
                    else
                    {
                        Vector3 rawVec = localFwd * force;
                        if (localFwd.z > 0)
                            FwdThrust += Mathf.Max(rawVec.z, 0);
                        if (localFwd.y > 0)
                            UpThrust += Mathf.Max(rawVec.y, 0);

                        //biasDirection += new Vector3(Mathf.Abs(rawVec.x), Mathf.Abs(rawVec.y), Mathf.Abs(rawVec.z));
                    }
                }
            }

            // this assumes IDEAL, which isn't always the case.  We have to compensate later on!
            float GravityForce = Tank.rbody.mass * Tank.GetGravityScale() * TankAIManager.GravMagnitude;
            UpTtWRatio = UpThrust / GravityForce;

            if (FwdThrust == 0 && UpThrust == 0)
            {
                NoProps = true;
                if (boostBiasDirection == Vector3.zero)
                {   //IT HAS NO VALID PROPS OR BOOSTERS!!!!
                    if (firstCheck)
                        DebugTAC_AI.LogAISetup(KickStart.ModID + ": Tech " + Tank.name + " DOES NOT HAVE ANY PROPS OR BOOSTERS TO FLY USING!!");
                }
                else if (firstCheck)
                    DebugTAC_AI.LogAISetup(KickStart.ModID + ": Tech " + Tank.name + " DOES NOT HAVE ANY PROPS TO FLY USING!!");
            }
            BoostBias = boostBiasDirection.normalized;

            biasDirection.Normalize();
            PropBias = biasDirection;
            if (firstCheck)
                DebugTAC_AI.LogAISetup(KickStart.ModID + ": Tech " + Tank.name + " PropBias " + PropBias + ", BoostBias " + BoostBias);
            if (Mathf.Abs(Vector3.Dot(PropBias, Vector3.right)) > 0.2f)
            {   //CENTER OF THRUST MAY BE OFF!!!
                SkewedFlightCenter = true;
                if (firstCheck)
                    DebugTAC_AI.LogAISetup(KickStart.ModID + ": Tech " + Tank.name + " reported to have off-centered thrust of a factor of " + Mathf.Abs(Vector3.Dot(biasDirection.normalized, Vector3.right)) + ".  \nAs all props don't have uniform thrust backwards and forwards (in relation to the root cab), the AI may not be able to fly correctly!!!");
            }
            else
                SkewedFlightCenter = false;
            SlowestPropLerpSpeed = lowestDelta;
            PropLerpValue = AIGlobals.PropLerpStrictness / SlowestPropLerpSpeed;
        }
        private void CheckWings()
        {
            float aerofoilSpeed = 100;
            foreach (ModuleWing module in Wings)
            {
                foreach (ModuleWing.Aerofoil foil in module.m_Aerofoils)
                {
                    if (foil.flapTurnSpeed > 0.01f)
                    {
                        if (foil.flapTurnSpeed < aerofoilSpeed)
                        {
                            aerofoilSpeed = foil.flapTurnSpeed;
                        }
                    }
                }
            }
            if (Helper.lastTechExtents >= AIGlobals.LargeAircraftSize)
            {
                DebugTAC_AI.LogAISetup("CheckWings(): LARGE AIRCRAFT " + Helper.lastTechExtents + " ramming: " + Helper.FullMelee);
                LargeAircraft = true;
            }
            else
            {
                DebugTAC_AI.LogAISetup("CheckWings(): Normal aircraft " + Helper.lastTechExtents + " ramming: " + Helper.FullMelee);
                LargeAircraft = false;
            }

            AerofoilSluggishness = AIGlobals.AerofoilSluggishnessBaseValue / aerofoilSpeed;
            if (FlyStyle == FlightType.Helicopter)
            {
                //FlyingChillFactor is calculated and set in HelicopterAICore.Initiate()
            }
            else
            {
                if (LargeAircraft)
                {
                    FlyingChillFactor = Vector3.one * AerofoilSluggishness * AIGlobals.LargeAircraftChillFactorMulti;
                    FlyingChillFactor.y = 5;    // need accuraccy for large aircraft bombing runs
                }
                else
                {
                    FlyingChillFactor = Vector3.one * AerofoilSluggishness * AIGlobals.AircraftChillFactorMulti;
                    FlyingChillFactor.y = 0.75f;  // Yaw isn't normally too strong on aircraft so we give it a boost.
                }
            }

            RollStrength = Mathf.Clamp(aerofoilSpeed * 2, 0.5f, 2);
        }
        private void CheckAllFlightBlocks()
        {
            CheckEngines(true);
            CheckWings();
        }

        //Navigation Director - set airborne positions for the plane to fly to based on lastDestination
        public override void DriveDirector(ref EControlCoreSet core)
        {
            TankAIHelper helper = Helper;
            Tank tank = Tank;

            if (helper == null)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  FIRED FlightDirector WITHOUT THE REQUIRED TankAIHelper MODULE!!!");
                return;
            }

            this.TestForMayday(helper, tank);

            if (helper.AIAlign == AIAlignment.Player)
            {
                this.ForcePitchUp = false;
                if (this.Grounded)
                {   //Become a ground vehicle for now
                    if (!AIEPathing.AboveHeightFromGroundTech(helper, helper.lastTechExtents * 2))
                    {
                        return;
                    }
                    return;
                }
                if (!this.TargetGrounded)
                    PathPointSet = AIEPathing.OffsetFromGroundA(helper.lastDestinationCore, helper);
                this.AICore.DriveDirector(ref core);
            }
            else if (helper.AIAlign == AIAlignment.NonPlayer) //enemy
            {
                if (!this.TargetGrounded)
                    PathPointSet = AIEPathing.OffsetFromGroundA(helper.lastDestinationCore, helper);

                this.AICore.DriveDirectorEnemy(EnemyMind, ref core);
            }
            return;
        }
        public override void DriveDirectorRTS(ref EControlCoreSet core)
        {
            TankAIHelper helper = Helper;
            Tank tank = Tank;

            if (helper == null)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  FIRED FlightDirectorRTS WITHOUT THE REQUIRED TankAIHelper MODULE!!!");
                return;
            }

            TestForMayday(helper, tank);

            if (helper.AIAlign == AIAlignment.Player)
            {
                this.ForcePitchUp = false;
                if (this.Grounded)
                {   //Become a ground vehicle for now
                    if (!AIEPathing.AboveHeightFromGroundTech(helper, helper.lastTechExtents * 2))
                    {
                        return;
                    }
                    return;
                }
                core.lastDestination = AIEPathing.OffsetFromGroundA(helper.RTSDestination, helper);
                this.AICore.DriveDirectorRTS(ref core);
            }
            else if (helper.AIAlign == AIAlignment.NonPlayer) //enemy
            {
                // No core.lastDestination ground-offset here on purpose: DriveDirectorEnemyRTS applies its
                // own OffsetFromGroundA + PreventCollisionWithGround (!TargetGrounded-guarded) to PathPointSet,
                // the value actually steered to. (The old line referenced helper.lastDestination, since removed.)
                // Deferred-10 fix: was calling the non-RTS enemy director (see AIControllerDefault).
                this.AICore.DriveDirectorEnemyRTS(EnemyMind, ref core);
            }
            return;
        }

        //Flight Maintainer - handle the flight between airborne positions
        public override void DriveMaintainer(ref EControlCoreSet core)
        {
            TankAIHelper helper = this.Helper;
            Tank tank = this.Tank;

            if (helper == null)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": AI " + tank.name + ":  FIRED FlightMaintainer WITHOUT THE REQUIRED TankAIHelper MODULE!!!");
                return;
            }
            if (Tank.beam.IsActive)
            {
                KillAllControl(Helper);
                return;
            }

            if (helper.FullBoost)
                helper.MaxBoost();

            this.AICore.DriveMaintainer(helper, tank, ref core);
            return;
        }

        public override void OnMoveWorldOrigin(IntVector3 move)
        {
            PathPointSet += move;
        }
        public override Vector3 GetDestination()
        {
            return Helper.lastDestinationCore;
        }

        private bool TestForMayday(TankAIHelper helper, Tank tank)
        {
            if (helper.PendingDamageCheck)
            {
                bool damaged = false;

                if (this.Engines.Count() < 1)
                    damaged = true;
                int wingCount = 0;
                if (this.FlyStyle == FlightType.Helicopter)
                {
                    this.CheckEngines();
                    if (this.NoProps)
                    {
                        if (this.BoostBias.y <= 0.6f)
                            damaged = true;
                    }
                    else
                    {
                        if (this.PropBias.y <= 0.6f)
                            damaged = true;
                    }
                }
                else
                {
                    foreach (ModuleWing wing in this.Wings)
                    {
                        wingCount += wing.m_Aerofoils.Length;
                    }
                    if (wingCount < 5)
                        damaged = true;
                }

                if (!AIERepair.CanRepairNow(tank))
                {
                    //if (!Grounded)
                    //    DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name + " has been damaged too badly with no parts to repair with");
                    return false;
                }
                if (damaged && !Grounded)
                    DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name + " is missing too many parts and has been deemed incapable of flight!");

                return damaged;
            }
            return false;
        }
        private void OnAttach(TankBlock block, Tank tank)
        {
            if (AIERepair.SystemsCheck(tank))
                Grounded = TestForMayday(tank.GetHelperInsured(), tank);
        }
        private void OnDetach(TankBlock block, Tank tank)
        {
            // P08 dead-code removal: entire body was commented out (~17 lines of abandoned damage-
            // check / Grounded re-eval). Modern equivalent runs through TankAIHelper.OnBlockDetaching.
        }
        public void UpdateThrottle(TankAIHelper helper)
        {
            bool boostJets = false;
            bool boostProps = false;
            if (NoProps)
            {
                if (FlyStyle == FlightType.Aircraft)
                {
                    if (MainThrottle > 0.1f && Helper.LocalSafeVelocity.z < AIGlobals.AirStallSpeed + 5 && !Tank.beam.IsActive)
                        boostJets = true;
                    else
                        boostJets = helper.FullBoost;
                }
                else // VTOL
                {
                    if (MainThrottle > 0.1f && Helper.LocalSafeVelocity.z < AIGlobals.AirStallSpeed + 5 && !Tank.beam.IsActive)
                        boostJets = true;
                    else if (MainThrottle > 0.1f && !AIEPathing.AboveHeightFromGroundTech(helper, Helper.lastTechExtents * 2) && !Tank.beam.IsActive)
                        boostJets = true;
                    else
                        boostJets = helper.FullBoost;
                }

                if (CurrentThrottle + (SlowestPropLerpSpeed * Time.deltaTime) < MainThrottle)
                {
                    CurrentThrottle += SlowestPropLerpSpeed * Time.deltaTime;
                }
                else if (CurrentThrottle - (SlowestPropLerpSpeed * Time.deltaTime) > MainThrottle)
                {
                    CurrentThrottle -= SlowestPropLerpSpeed * Time.deltaTime;
                }
                else
                {   //Snap
                    CurrentThrottle = MainThrottle;
                }
            }
            else
            {
                if (CurrentThrottle + (SlowestPropLerpSpeed * Time.deltaTime) < MainThrottle)
                {
                    CurrentThrottle += SlowestPropLerpSpeed * Time.deltaTime;
                }
                else if (CurrentThrottle - (SlowestPropLerpSpeed * Time.deltaTime) > MainThrottle)
                {
                    CurrentThrottle -= SlowestPropLerpSpeed * Time.deltaTime;
                }
                else
                {   //Snap
                    CurrentThrottle = MainThrottle;
                }
                if (FlyStyle == FlightType.Aircraft)
                {   // Some aircraft stall when pitching up - this should help avoid that
                    if (CurrentThrottle > 1f)
                    {
                        boostProps = true;
                    }
                    else
                    {
                        boostProps = false;
                    }
                }
            }
            CurrentThrottle = Mathf.Clamp(CurrentThrottle, -1, 1);
            helper.ProcessControl(Vector3.zero, Vector3.zero, Vector3.zero, boostProps, boostJets);
        }
        public void KillAllControl(TankAIHelper helper)
        {
            helper.ProcessControl(Vector3.zero, Vector3.zero, Vector3.zero, false, false);
        }

    }
}
