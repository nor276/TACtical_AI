using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// One propulsion block's thrust contribution. Preserved from the pre-Chassis
    /// ThrustMap struct shape for SmartTestSuite constructor compat + diagnostic reads.
    /// </summary>
    public readonly struct PropulsionBlock
    {
        public readonly Vector3 MountPositionLocal;
        public readonly Vector3 ThrustDirectionLocal;
        public readonly float MaxThrust;
        public readonly BlockRole Type;

        public PropulsionBlock(Vector3 pos, Vector3 dir, float maxThrust, BlockRole type)
        {
            MountPositionLocal = pos;
            ThrustDirectionLocal = dir;
            MaxThrust = maxThrust;
            Type = type;
        }
    }

    /// <summary>
    /// Chassis Step 4 (REV 3.1): per-tech thrust capability map. Replaces ThrustMap
    /// internally; the public surface (MaxLinearAccelPositive/Negative/MaxAngularAccel/Blocks)
    /// is bit-identical to ThrustMap so the 23 consumer files compile against either type
    /// once Step 8a renames VehicleModelSnapshot.Thrust to this type.
    ///
    /// Key differences from ThrustMap:
    /// - Inputs: BlockInstancePose[] (per-tech) + BlockArchetype lookups (catalog).
    ///   No per-block GetComponent storm.
    /// - Aggregation: vector-summation of (pose.LocalRotation * emitter.LocalAxis * MaxForceN)
    ///   for boosters / fan-jets / hover / wing, with per-EmitterKind wheel substitution
    ///   (WheelRoll -> chassis +Z) per Decision #3.
    /// - Yaw budget: clamped max-selector (real torque vs 30%-of-forward heuristic),
    ///   not "additive lower bound" -- matches ThrustMap.cs:119-121 max-selector semantics
    ///   per the C10 fix.
    /// - New additive fields (not consumed by v0.1; v0.2 wires them):
    ///   LiftBudget / GroundTractionBudget / JetBudget / AeroLiftBudget / Topology.
    /// </summary>
    public sealed class ThrustField
    {
        public IReadOnlyList<PropulsionBlock> Blocks { get; }
        public Vector3 MaxLinearAccelPositive { get; }   // +X, +Y, +Z magnitudes (m/s²)
        public Vector3 MaxLinearAccelNegative { get; }   // -X, -Y, -Z magnitudes (m/s²)
        public Vector3 MaxAngularAccel { get; }          // pitch, yaw, roll (rad/s²)

        // New additive fields per Decision #13.
        public Vector3 LiftBudget { get; }
        public Vector3 GroundTractionBudget { get; }
        public Vector3 JetBudget { get; }
        public Vector3 AeroLiftBudget { get; }
        public PropulsionTopology Topology { get; }

        public ThrustField(
            IReadOnlyList<PropulsionBlock> blocks,
            Vector3 posAccel, Vector3 negAccel, Vector3 angAccel,
            Vector3 liftBudget, Vector3 groundTractionBudget,
            Vector3 jetBudget, Vector3 aeroLiftBudget,
            PropulsionTopology topology)
        {
            Blocks = blocks ?? new PropulsionBlock[0];
            MaxLinearAccelPositive = posAccel;
            MaxLinearAccelNegative = negAccel;
            MaxAngularAccel = angAccel;
            LiftBudget = liftBudget;
            GroundTractionBudget = groundTractionBudget;
            JetBudget = jetBudget;
            AeroLiftBudget = aeroLiftBudget;
            Topology = topology;
        }

        public static ThrustField Empty { get; } = new ThrustField(
            new PropulsionBlock[0],
            Vector3.zero, Vector3.zero, Vector3.zero,
            Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero,
            PropulsionTopology.Static);

        /// <summary>
        /// Build the thrust field from per-block poses + the catalog + total mass.
        /// Vector aggregation; no per-block GetComponent storm.
        /// </summary>
        public static ThrustField Compute(
            BlockInstancePose[] poses,
            TypedBlockCatalog catalog,
            float totalMass)
        {
            if (poses == null || poses.Length == 0 || catalog == null || totalMass <= 0f)
                return Empty;

            Vector3 posF = Vector3.zero;       // sum of positive-side chassis-frame forces
            Vector3 negF = Vector3.zero;
            Vector3 lift = Vector3.zero;
            Vector3 ground = Vector3.zero;
            Vector3 jet = Vector3.zero;
            Vector3 aero = Vector3.zero;

            // PropulsionBlock list for SmartTestSuite ctor compat. Sized eagerly.
            var blockList = new List<PropulsionBlock>(poses.Length);
            bool sawWheel = false, sawHover = false, sawJet = false, sawWing = false;

            for (int p = 0; p < poses.Length; p++)
            {
                var pose = poses[p];
                var arch = catalog.TryGet(pose.TypeKey);
                if (arch == null || arch.Emitters == null) continue;

                for (int e = 0; e < arch.Emitters.Length; e++)
                {
                    var em = arch.Emitters[e];
                    Vector3 chassisAxis;
                    BlockRole legacyRole;

                    if (em.Kind == EmitterKind.WheelRoll)
                    {
                        // Deliberate per-EmitterKind substitution per Decision #3.
                        chassisAxis = Vector3.forward;
                        legacyRole = BlockRole.Wheel;
                        sawWheel = true;
                    }
                    else
                    {
                        // Vector geometry: pose's Quaternion rotates block-local push axis
                        // into chassis frame. Type-coherent: Quaternion * Vector3 -> Vector3.
                        chassisAxis = pose.LocalRotation * em.LocalAxis;
                        switch (em.Kind)
                        {
                            case EmitterKind.HoverPad:       legacyRole = BlockRole.Hover; sawHover = true; break;
                            case EmitterKind.FanJet:         legacyRole = BlockRole.Jet;   sawJet   = true; break;
                            case EmitterKind.BoosterImpulse: legacyRole = BlockRole.Jet;   sawJet   = true; break;
                            case EmitterKind.WingLift:       legacyRole = BlockRole.Prop;  sawWing  = true; break;
                            default:                         legacyRole = BlockRole.Structural; break;
                        }
                    }

                    // P2 Item 37: per-EmitterKind multiplier applied immediately before
                    // bucket aggregation. Defaults all 1.0f -> bit-identical to v0.1.
                    // CMA-ES (Training) can mutate these to bias per-kind effective output.
                    float kindMul = EmitterKindMultipliers.For(em.Kind);
                    Vector3 forwardForce = chassisAxis * (em.MaxForceN * kindMul);
                    AccumulateSigned(ref posF, ref negF, forwardForce);

                    if (em.ReverseForceN > 0f)
                    {
                        Vector3 reverseForce = -chassisAxis * (em.ReverseForceN * kindMul);
                        AccumulateSigned(ref posF, ref negF, reverseForce);
                    }

                    // Per-Decision-#13 budgets: each emitter contributes to one bucket via Kind.
                    switch (em.Kind)
                    {
                        case EmitterKind.WheelRoll:       ground += new Vector3(0f, 0f, em.MaxForceN); break;
                        case EmitterKind.HoverPad:        lift += forwardForce; break;
                        case EmitterKind.WingLift:        lift += forwardForce; break;
                        case EmitterKind.FanJet:          jet += forwardForce; break;
                        case EmitterKind.BoosterImpulse:  jet += forwardForce; break;
                    }
                    if ((em.Flags & EmitterFlags.AerodynamicScaling) != 0)
                        aero += forwardForce;

                    blockList.Add(new PropulsionBlock(
                        pos: pose.LocalPosition,
                        dir: chassisAxis,
                        maxThrust: em.MaxForceN,
                        type: legacyRole));
                }
            }

            // Accelerations (force / mass).
            Vector3 posAccel = posF / totalMass;
            Vector3 negAccel = negF / totalMass;
            Vector3 liftA = lift / totalMass;
            Vector3 groundA = ground / totalMass;
            Vector3 jetA = jet / totalMass;
            Vector3 aeroA = aero / totalMass;

            // Yaw heuristic preserved as clamped max-selector per C10.
            // Today's ThrustMap.cs:119-121:
            //   yawFromLateral = Min(posAccel.x, negAccel.x) * 0.1f;
            //   yawFromForward = Max(posAccel.z, negAccel.z) * 0.3f;
            //   yawCapacity = Min(3f, Max(yawFromLateral, yawFromForward));
            float yawFromLateral = Mathf.Min(posAccel.x, negAccel.x) * 0.1f;
            float yawFromForward = Mathf.Max(posAccel.z, negAccel.z) * 0.3f;
            float existingHeuristicYaw = Mathf.Max(yawFromLateral, yawFromForward);
            // Real torque yaw is left at 0 in v0.1 (per-emitter tau = r x F aggregation is a
            // v0.2 follow-up); the max-selector reduces to today's heuristic. v0.2 will sum
            // real tau across emitters and feed it as the first argument to the Max below.
            float realTorqueYaw = 0f;
            float yawCapacity = Mathf.Min(3f, Mathf.Max(realTorqueYaw, existingHeuristicYaw));

            var maxAng = new Vector3(
                Mathf.Min(posAccel.y, negAccel.y) * 0.1f,
                yawCapacity,
                Mathf.Min(posAccel.z, negAccel.z) * 0.1f);

            // Derive topology: simple rule per Decision #13 axis convention.
            PropulsionTopology topo = DeriveTopology(sawWheel, sawHover, sawJet, sawWing);

            return new ThrustField(
                blocks: blockList,
                posAccel: posAccel, negAccel: negAccel, angAccel: maxAng,
                liftBudget: liftA, groundTractionBudget: groundA,
                jetBudget: jetA, aeroLiftBudget: aeroA,
                topology: topo);
        }

        private static void AccumulateSigned(ref Vector3 pos, ref Vector3 neg, Vector3 f)
        {
            if (f.x > 0f) pos.x += f.x; else neg.x += -f.x;
            if (f.y > 0f) pos.y += f.y; else neg.y += -f.y;
            if (f.z > 0f) pos.z += f.z; else neg.z += -f.z;
        }

        private static PropulsionTopology DeriveTopology(bool wheel, bool hover, bool jet, bool wing)
        {
            if (jet && wing) return PropulsionTopology.Aircraft3D;
            if (hover)       return PropulsionTopology.Hover2D;
            if (wheel && !jet && !hover && !wing) return PropulsionTopology.Wheel1D;
            if (!wheel && !hover && !jet && !wing) return PropulsionTopology.Static;
            return PropulsionTopology.Mixed;
        }
    }
}
