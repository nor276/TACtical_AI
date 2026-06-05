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
        // P3 Item 5: per-instance HP fraction in [0, 1]. Defaults to 1.0 (healthy) when
        // the ctor isn't passed a value; ChassisCapture populates via ArmorHpProbe when
        // ArmorMapPolicy.UseRealSpecHP is true. Consumed by ArmorMap.Compute (catalog
        // overload) — pose.HpFraction * archetype.SpecHP per voxel.
        public readonly float HpFraction;

        public BlockInstancePose(int typeKey, Vector3 localPosition, Quaternion localRotation,
                                 float currentMass, float hpFraction = 1f)
        {
            TypeKey = typeKey;
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            CurrentMass = currentMass;
            HpFraction = hpFraction;
        }
    }
}
