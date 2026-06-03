using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Derived per-tech mobility characteristics. Per VEHICLE-CONTRACT §8.
    /// All values are derived from Mass + Thrust + Armor; not directly observed.
    /// </summary>
    public readonly struct MobilityProfile
    {
        public readonly float TopSpeedForward;
        public readonly float TopSpeedBackward;
        public readonly float TopSpeedLateral;
        public readonly float TurningRadiusAtTopSpeed;
        public readonly float ClimbAngleMax;          // radians
        public readonly bool WaterCapable;
        public readonly float VerticalAuthority;      // > 0 if can hover/lift
        public readonly float TippingSusceptibility;  // [0, 1]; 0 = stable, 1 = topples easily

        public MobilityProfile(
            float topFwd, float topBack, float topLat,
            float turnRadius, float climbAngle,
            bool waterCapable, float verticalAuth, float tipping)
        {
            TopSpeedForward = topFwd;
            TopSpeedBackward = topBack;
            TopSpeedLateral = topLat;
            TurningRadiusAtTopSpeed = turnRadius;
            ClimbAngleMax = climbAngle;
            WaterCapable = waterCapable;
            VerticalAuthority = verticalAuth;
            TippingSusceptibility = tipping;
        }

        public static MobilityProfile Default => new MobilityProfile(
            topFwd: 10f, topBack: 5f, topLat: 0f,
            turnRadius: 20f, climbAngle: Mathf.PI / 4f,
            waterCapable: false, verticalAuth: 0f, tipping: 0.5f);

        /// <summary>Derive mobility from the mass distribution and thrust field.</summary>
        public static MobilityProfile Derive(MassDistribution mass, ThrustField thrust, ArmorMap armor)
        {
            if (mass.TotalMass <= 0f) return Default;
            // Phase 6 (FIX-PLAN.md) — AUDIT-R2 §2.R2.F: bail to Default on any NaN/Inf
            // input. Without this, sqrt(NaN)=NaN propagates into every downstream
            // consumer (CapabilityFilters, role assignment, threat-field building).
            if (float.IsNaN(thrust.MaxLinearAccelPositive.z) || float.IsInfinity(thrust.MaxLinearAccelPositive.z)
                || float.IsNaN(thrust.MaxLinearAccelPositive.y) || float.IsInfinity(thrust.MaxLinearAccelPositive.y)
                || float.IsNaN(thrust.MaxAngularAccel.y) || float.IsInfinity(thrust.MaxAngularAccel.y))
                return Default;

            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.2: removed spurious factor of 2. Steady-
            // state force balance under quadratic drag is a_thrust = k·v² ⇒ v_max =
            // sqrt(a/k). The previous sqrt(2·a/k) inflated every top speed by √2 ≈ 1.41,
            // which propagated into RoleAssignment, PlanDecomposition, and the tactical
            // optimizer's reach/feasibility math.
            const float DragK = 0.05f;
            float topFwd = Mathf.Sqrt(Mathf.Max(0f, thrust.MaxLinearAccelPositive.z) / Mathf.Max(DragK, 1e-3f));
            float topBack = Mathf.Sqrt(Mathf.Max(0f, thrust.MaxLinearAccelNegative.z) / Mathf.Max(DragK, 1e-3f));
            float topLat = Mathf.Sqrt(Mathf.Max(0f, Mathf.Max(thrust.MaxLinearAccelPositive.x, thrust.MaxLinearAccelNegative.x)) / Mathf.Max(DragK, 1e-3f));

            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.2 turning-radius fix. Previous formula
            // `v / angAccel` had units m·s/rad. Centripetal-limited cornering: r = v² /
            // a_lat. Use the larger of left/right lateral acceleration as the limit.
            float aLat = Mathf.Max(thrust.MaxLinearAccelPositive.x, thrust.MaxLinearAccelNegative.x);
            float turnRadius = aLat > 1e-3f
                ? (topFwd * topFwd) / aLat
                : 100f;

            // Climb angle: limited by acceleration vs gravity component along slope.
            // sin(θ_max) = a_fwd / g. Cap at π/3 (60°) for ground-vehicle practicality.
            float climb = Mathf.Min(Mathf.Asin(Mathf.Clamp(thrust.MaxLinearAccelPositive.z / 9.81f, 0f, 1f)), Mathf.PI / 3f);

            // Vertical authority: thrust available against gravity.
            float verticalAuth = thrust.MaxLinearAccelPositive.y / 9.81f;

            // Water capable: deferred — needs block-type info from WaterMod's classification.
            bool water = false;

            // Tipping: COM height / footprint width — high COM = high tip risk.
            float footprint = armor.LocalBounds.size.x + armor.LocalBounds.size.z;
            float tipping = footprint > 1e-3f
                ? Mathf.Clamp(mass.CenterOfMassLocal.y / (footprint * 0.5f), 0f, 1f)
                : 0.5f;

            return new MobilityProfile(topFwd, topBack, topLat, turnRadius, climb, water, verticalAuth, tipping);
        }
    }
}
