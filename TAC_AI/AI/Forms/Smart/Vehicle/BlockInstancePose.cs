using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: per-tank-per-block instance pose. Produced by ChassisCapture, consumed by
    /// ThrustField/MassDistribution/ArmorMap/WeaponProfileBuilder.
    ///
    /// LocalRotation is Quaternion-native via block.cachedLocalRotation.rot per the
    /// "Per-block thrust geometry" section of CHASSIS-DESIGN.md (verified live source at
    /// AIERepair.cs:190/211 and RawTechLoader.cs:3172). Composition math
    /// `pose.LocalRotation * archetype.Emitters[i].LocalAxis` yields the chassis-frame
    /// thrust direction (type-coherent: Quaternion * Vector3 -> Vector3).
    /// </summary>
    public readonly struct BlockInstancePose
    {
        public readonly int TypeKey;
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalRotation;
        public readonly float CurrentMass;
        // NO HpFraction in v0.1 -- ArmorMap HP migration deferred to v0.2 (no SpecHP either).

        public BlockInstancePose(int typeKey, Vector3 localPosition, Quaternion localRotation, float currentMass)
        {
            TypeKey = typeKey;
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            CurrentMass = currentMass;
        }
    }
}
