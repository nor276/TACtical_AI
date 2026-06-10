using UnityEngine;
using TAC_AI.AI.Forms.Smart.Vehicle;

namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>Per PATHING-CONTRACT §4.3.</summary>
    public enum VehicleClass { Wheel, Hover, Airplane, Submarine, Walker }

    /// <summary>
    /// Per-vehicle capability descriptor consumed by ThreatField queries and TerrainMap
    /// traversability checks. Derived from <see cref="MobilityProfile"/> at vehicle-rebuild time.
    /// Per PATHING-CONTRACT §4.1.
    /// </summary>
    public readonly struct VehicleCapability
    {
        public readonly VehicleClass Class;
        public readonly float ClimbAngleMax;     // radians
        public readonly bool WaterCapable;
        public readonly float VerticalAuthority; // > 0 for hover/airplane
        public readonly float TippingSusceptibility;
        public readonly Vector3 PrimaryAxis;     // forward in local space

        public VehicleCapability(
            VehicleClass cls, float climbAngleMax, bool waterCapable,
            float verticalAuthority, float tipping, Vector3 primaryAxis)
        {
            Class = cls;
            ClimbAngleMax = climbAngleMax;
            WaterCapable = waterCapable;
            VerticalAuthority = verticalAuthority;
            TippingSusceptibility = tipping;
            PrimaryAxis = primaryAxis;
        }

        public static readonly VehicleCapability Default = new VehicleCapability(
            VehicleClass.Wheel, Mathf.PI / 4f, false, 0f, 0.5f, Vector3.forward);

        /// <summary>
        /// Derive VehicleCapability from a MobilityProfile per PATHING §4.3 thresholds.
        /// Classification is cached on the caller side — Pathing reads, does not re-classify.
        /// </summary>
        public static VehicleCapability FromMobility(MobilityProfile mob)
        {
            return FromMobility(mob, isWalker: false);
        }

        /// <summary>
        /// Walker-aware overload. When <paramref name="isWalker"/> is true and the tech
        /// isn't flyer/hover/submarine, classify as Walker so CapabilityRelevance's
        /// walker case applies and downstream consumers can use a leg-arc cone instead
        /// of the wheeled cone. ModuleWalker is reserved-unreachable in the current
        /// TerraTech version (per Chassis Decisions #7/#12); callers that synthesize
        /// walker-mobility hints from RawTech metadata or BlockKindFlags (when those
        /// bits become live) pass true here. Existing callers go through the
        /// no-hint overload above and continue to receive the historical Wheel default.
        /// </summary>
        public static VehicleCapability FromMobility(MobilityProfile mob, bool isWalker)
        {
            VehicleClass cls;
            if (!mob.WaterCapable && mob.VerticalAuthority > 1.5f) cls = VehicleClass.Airplane;
            else if (mob.VerticalAuthority >= 0.5f && mob.VerticalAuthority <= 1.5f) cls = VehicleClass.Hover;
            else if (mob.WaterCapable && mob.VerticalAuthority < 0.5f) cls = VehicleClass.Submarine;
            else if (isWalker) cls = VehicleClass.Walker;
            else cls = VehicleClass.Wheel;

            return new VehicleCapability(
                cls,
                mob.ClimbAngleMax,
                mob.WaterCapable,
                mob.VerticalAuthority,
                mob.TippingSusceptibility,
                Vector3.forward);
        }
    }

    /// <summary>
    /// Per-(enemy weapon, querying vehicle class) relevance factor per PATHING-CONTRACT §4.2.
    /// Result in [0.2, 1.0]: 1.0 if the weapon can effectively threaten this vehicle type,
    /// 0.2 if blind-spot / wrong elevation.
    ///
    /// v0.1.0 uses arc-elevation heuristics: weapons with high pitch arcs threaten
    /// airplanes; ground-pointing weapons threaten wheels/submarines; hovers eat both.
    /// TODO v0.2: replace with per-weapon target-type metadata (anti-air flag, etc.) once
    /// VEHICLE-CONTRACT exposes that classification from the block's profile.
    /// </summary>
    public static class CapabilityRelevance
    {
        public const float Min = 0.2f;
        public const float Max = 1.0f;

        public static float Compute(WeaponProfile w, VehicleClass queryClass)
        {
            switch (queryClass)
            {
                case VehicleClass.Airplane:
                    return w.PitchArcRadians > Mathf.PI / 3f ? Max : Min;
                case VehicleClass.Wheel:
                case VehicleClass.Submarine:
                case VehicleClass.Walker:
                    return w.PitchArcRadians < Mathf.PI / 2f ? Max : 0.4f;
                case VehicleClass.Hover:
                default:
                    return 0.7f; // hovers in the seam — both ground and low-air threats matter
            }
        }
    }
}
