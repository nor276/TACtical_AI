using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Mass distribution + center of mass + inertia tensor. Computed from a BlockInstancePose[]
    /// at rebuild time. Per VEHICLE-CONTRACT §3.
    ///
    /// All fields are tech-local; world-space values are derived by applying the tech's transform.
    /// </summary>
    public readonly struct MassDistribution
    {
        public readonly float TotalMass;
        public readonly Vector3 CenterOfMassLocal;
        public readonly float InertiaXX, InertiaYY, InertiaZZ; // diagonal inertia (kg m²)
        public readonly float InertiaXY, InertiaXZ, InertiaYZ; // off-diagonal

        public MassDistribution(float totalMass, Vector3 com,
            float ixx, float iyy, float izz,
            float ixy, float ixz, float iyz)
        {
            TotalMass = totalMass;
            CenterOfMassLocal = com;
            InertiaXX = ixx; InertiaYY = iyy; InertiaZZ = izz;
            InertiaXY = ixy; InertiaXZ = ixz; InertiaYZ = iyz;
        }

        public static MassDistribution Empty => new MassDistribution(
            0f, Vector3.zero, 1f, 1f, 1f, 0f, 0f, 0f);

        /// <summary>
        /// Aggregate over the block list. Each block contributes to total mass and to the
        /// mass-weighted COM. Inertia is the parallel-axis sum of per-block point masses
        /// (treating each block as a point mass at its local position — a reasonable approximation
        /// for blocks whose individual extent is small relative to the tech).
        /// </summary>
        public static MassDistribution Compute(BlockInstancePose[] poses)
        {
            if (poses == null || poses.Length == 0) return Empty;

            float total = 0f;
            float sx = 0f, sy = 0f, sz = 0f;

            for (int i = 0; i < poses.Length; i++)
            {
                var b = poses[i];
                total += b.CurrentMass;
                sx += b.CurrentMass * b.LocalPosition.x;
                sy += b.CurrentMass * b.LocalPosition.y;
                sz += b.CurrentMass * b.LocalPosition.z;
            }

            if (total <= 0f) return Empty;
            var com = new Vector3(sx / total, sy / total, sz / total);

            // Inertia about COM, point-mass approximation.
            float ixx = 0f, iyy = 0f, izz = 0f;
            float ixy = 0f, ixz = 0f, iyz = 0f;
            for (int i = 0; i < poses.Length; i++)
            {
                var b = poses[i];
                float dx = b.LocalPosition.x - com.x;
                float dy = b.LocalPosition.y - com.y;
                float dz = b.LocalPosition.z - com.z;
                float m = b.CurrentMass;
                ixx += m * (dy * dy + dz * dz);
                iyy += m * (dx * dx + dz * dz);
                izz += m * (dx * dx + dy * dy);
                ixy -= m * dx * dy;
                ixz -= m * dx * dz;
                iyz -= m * dy * dz;
            }

            return new MassDistribution(total, com, ixx, iyy, izz, ixy, ixz, iyz);
        }
    }
}
