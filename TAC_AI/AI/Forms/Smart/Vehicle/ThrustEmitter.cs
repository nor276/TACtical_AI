using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: a single propulsion element on a block prefab (one FanJet child, one
    /// BoosterJet child, one wheel, etc.). Lives on <see cref="BlockArchetype.Emitters"/>
    /// as immutable per-block-type data; never per-instance.
    ///
    /// LocalAxis is the unit push direction in BLOCK-LOCAL frame -- aggregated at composition
    /// time via pose.LocalRotation * emitter.LocalAxis (Quaternion-times-Vector3 yields the
    /// chassis-frame thrust direction).
    ///
    /// EmitterKind.WheelRoll is treated specially by ThrustField.Compute (substituted with
    /// chassis +Z per Decision #3); for that kind, LocalAxis is irrelevant in v0.1.
    /// </summary>
    public readonly struct ThrustEmitter
    {
        public readonly EmitterKind Kind;
        public readonly Vector3 LocalAxis;     // unit vector, block-local frame, positive push direction
        public readonly Vector3 LocalMount;    // block-local offset from block origin
        public readonly float MaxForceN;
        public readonly float ReverseForceN;   // 0 for jets; > 0 for wheels (brake/reverse)
        public readonly EmitterFlags Flags;

        public ThrustEmitter(
            EmitterKind kind,
            Vector3 localAxis,
            Vector3 localMount,
            float maxForceN,
            float reverseForceN,
            EmitterFlags flags)
        {
            Kind = kind;
            LocalAxis = localAxis;
            LocalMount = localMount;
            MaxForceN = maxForceN;
            ReverseForceN = reverseForceN;
            Flags = flags;
        }
    }
}
