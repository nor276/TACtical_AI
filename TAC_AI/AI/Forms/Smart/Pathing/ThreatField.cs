using System;
using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>
    /// Pre-computed per-enemy contributor to the threat field. Constructed at snapshot
    /// build-time; immutable. Per PATHING-CONTRACT §2.3.
    /// </summary>
    public readonly struct ThreatSource
    {
        public readonly TechId Id;
        public readonly Vector3 Position;
        public readonly Vector3 ForwardWorld;     // for arc anisotropy (yaw)
        public readonly float Radius;             // effective threat range (m)
        public readonly float Rating;             // aggregate damage capability
        public readonly float AmmoFactor;         // [0, 1]
        public readonly IReadOnlyList<WeaponProfile> Weapons;

        public ThreatSource(TechId id, Vector3 position, Vector3 forwardWorld, float radius,
            float rating, float ammoFactor, IReadOnlyList<WeaponProfile> weapons)
        {
            Id = id;
            Position = position;
            ForwardWorld = forwardWorld;
            Radius = radius;
            Rating = rating;
            AmmoFactor = ammoFactor;
            Weapons = weapons ?? Array.Empty<WeaponProfile>();
        }
    }

    /// <summary>
    /// Latest published threat field. Stateless reads; queries consume via IThreatField.
    /// Per PATHING-CONTRACT §2.3.
    /// </summary>
    public sealed class ThreatFieldSnapshot
    {
        public long TickStamp { get; }
        public IReadOnlyList<ThreatSource> Sources { get; }

        public ThreatFieldSnapshot(long tickStamp, IReadOnlyList<ThreatSource> sources)
        {
            TickStamp = tickStamp;
            Sources = sources ?? Array.Empty<ThreatSource>();
        }

        public static readonly ThreatFieldSnapshot Empty = new ThreatFieldSnapshot(0L, Array.Empty<ThreatSource>());
    }

    /// <summary>Threat-field query interface per PATHING-CONTRACT §2.4.</summary>
    public interface IThreatField
    {
        float Evaluate(Vector3 point, VehicleCapability filter);
        Vector3 Gradient(Vector3 point, VehicleCapability filter);
    }

    /// <summary>
    /// Read-only wrapper around a <see cref="ThreatFieldSnapshot"/> implementing the
    /// query interface per PATHING-CONTRACT §2.1 functional form.
    ///
    /// arc_anisotropy and los_factor are treated as locally constant for the gradient
    /// per §2.2 CHOMP-style relaxation.
    ///
    /// LOS factor at v0.1.0 is a constant 1.0 (no LOS test) — activating it requires
    /// passing a TerrainMap reference into Evaluate. TODO v0.2: ThreatField holds a
    /// reference to the active TerrainMap snapshot for raycast-based LOS shading.
    /// </summary>
    public sealed class ThreatField : IThreatField
    {
        private readonly ThreatFieldSnapshot _snap;
        private readonly ITerrainMap _terrain; // optional; used for LOS factor (null OK)

        public ThreatField(ThreatFieldSnapshot snap, ITerrainMap terrain = null)
        {
            _snap = snap ?? ThreatFieldSnapshot.Empty;
            _terrain = terrain;
        }

        public ThreatFieldSnapshot Snapshot => _snap;

        public float Evaluate(Vector3 point, VehicleCapability filter)
        {
            float total = 0f;
            for (int i = 0; i < _snap.Sources.Count; i++)
            {
                total += Contribution(_snap.Sources[i], point, filter, includeLos: true);
            }
            return total;
        }

        public Vector3 Gradient(Vector3 point, VehicleCapability filter)
        {
            // Sum per-source Gaussian gradients per §2.2.
            // ∇c_e(p) = c_e(p) * -(p - e.position) / e.radius²
            Vector3 g = Vector3.zero;
            for (int i = 0; i < _snap.Sources.Count; i++)
            {
                var src = _snap.Sources[i];
                if (src.Radius < 1e-3f) continue;
                float c = Contribution(src, point, filter, includeLos: true);
                Vector3 delta = point - src.Position;
                float invR2 = 1f / (src.Radius * src.Radius);
                g += c * (-delta * invR2);
            }
            return g;
        }

        // ---- core per-source contribution ----
        private float Contribution(ThreatSource src, Vector3 point, VehicleCapability filter, bool includeLos)
        {
            if (src.Radius < 1e-3f) return 0f;
            Vector3 delta = point - src.Position;
            float d2 = delta.sqrMagnitude;
            float gaussian = Mathf.Exp(-d2 / (2f * src.Radius * src.Radius));

            float arcAniso = ArcAnisotropy(src, delta);
            float los = includeLos ? LosFactor(src, point) : 1f;
            float capability = CapabilityFactor(src, filter.Class);

            return src.Rating * gaussian * arcAniso * los * src.AmmoFactor * capability;
        }

        private static float ArcAnisotropy(ThreatSource src, Vector3 delta)
        {
            // Project onto XZ; compare yaw of delta vs forward.
            Vector2 fwdXZ = new Vector2(src.ForwardWorld.x, src.ForwardWorld.z);
            Vector2 toPtXZ = new Vector2(delta.x, delta.z);
            if (fwdXZ.sqrMagnitude < 1e-6f || toPtXZ.sqrMagnitude < 1e-6f) return 1f;
            fwdXZ.Normalize();
            toPtXZ.Normalize();
            float cosA = Vector2.Dot(fwdXZ, toPtXZ); // 1 = directly ahead, -1 = behind
            // Map to [0.2, 1.0] per §2.1.
            return Mathf.Lerp(0.2f, 1.0f, (cosA + 1f) * 0.5f);
        }

        private float LosFactor(ThreatSource src, Vector3 point)
        {
            if (_terrain == null) return 1f;
            return _terrain.RaycastSegment(src.Position, point) ? 1f : 0.3f;
        }

        private static float CapabilityFactor(ThreatSource src, VehicleClass queryClass)
        {
            if (src.Weapons.Count == 0) return 1f;
            float total = 0f;
            float weighted = 0f;
            for (int i = 0; i < src.Weapons.Count; i++)
            {
                var w = src.Weapons[i];
                float wCap = Mathf.Max(0.01f, w.DamagePerProjectile * w.FireRateHz);
                total += wCap;
                weighted += wCap * CapabilityRelevance.Compute(w, queryClass);
            }
            return total > 1e-6f ? weighted / total : 1f;
        }
    }

    /// <summary>
    /// Builds a <see cref="ThreatFieldSnapshot"/> from world beliefs + per-enemy vehicle
    /// snapshots. Stateless; called per Pathing tick. Per PATHING-CONTRACT §2.3.
    ///
    /// Friendly techs (same team as <paramref name="myTeam"/>) are excluded — Smart does
    /// not run from its own teammates.
    /// </summary>
    public static class ThreatFieldBuilder
    {
        public const float DefaultMinThreatRadius = 30f;
        public const float DefaultMaxThreatRadius = 500f;
        // Per WORLD-CONTRACT, Smart's belief tracks observed enemies but the perception
        // worker does NOT synthesize VehicleModelSnapshots for them at v0.1.0. Without that
        // data the per-enemy rating/radius math would emit zero (no weapons) — masking the
        // threat field entirely. Until enemy-vehicle inference lands, fall back to a baseline
        // so the field is non-trivial.
        public const float BaselineEnemyRating = 30f;
        public const float BaselineEnemyRadius = 100f;

        public static ThreatFieldSnapshot Build(
            BeliefSnapshot beliefs,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehiclesByTech,
            TeamId myTeam,
            long tickStamp)
        {
            if (beliefs == null) return ThreatFieldSnapshot.Empty;

            var sources = new List<ThreatSource>();
            foreach (var pair in beliefs.ByTech)
            {
                if (pair.Value.Team.Value == myTeam.Value) continue;
                VehicleModelSnapshot v = (vehiclesByTech != null && vehiclesByTech.TryGetValue(pair.Key, out var vv))
                    ? vv
                    : VehicleModelSnapshot.Empty(pair.Key.Value);

                float radius = ComputeRadius(v);
                float rating = ComputeRating(v);
                float ammoFactor = ComputeAmmoFactor(v);

                // Forward world ≈ belief heading rotated to a world XZ vector.
                float headingRad = pair.Value.HeadingMean;
                Vector3 forwardWorld = new Vector3(Mathf.Sin(headingRad), 0f, Mathf.Cos(headingRad));

                sources.Add(new ThreatSource(
                    pair.Key,
                    pair.Value.PositionMean,
                    forwardWorld,
                    radius, rating, ammoFactor,
                    v.Weapons));
            }
            return new ThreatFieldSnapshot(tickStamp, sources);
        }

        private static float ComputeRating(VehicleModelSnapshot v)
        {
            if (v.Weapons.Count == 0) return BaselineEnemyRating;
            float r = 0f;
            for (int i = 0; i < v.Weapons.Count; i++)
            {
                var w = v.Weapons[i];
                r += w.DamagePerProjectile * Mathf.Max(0.1f, w.FireRateHz);
            }
            return r;
        }

        private static float ComputeRadius(VehicleModelSnapshot v)
        {
            if (v.Weapons.Count == 0) return BaselineEnemyRadius;
            float maxR = 0f;
            for (int i = 0; i < v.Weapons.Count; i++)
            {
                if (v.Weapons[i].Range > maxR) maxR = v.Weapons[i].Range;
            }
            return Mathf.Clamp(maxR, DefaultMinThreatRadius, DefaultMaxThreatRadius);
        }

        private static float ComputeAmmoFactor(VehicleModelSnapshot v)
        {
            if (v.Weapons.Count == 0) return 1f;
            float totalCur = 0f, totalCap = 0f;
            for (int i = 0; i < v.Weapons.Count; i++)
            {
                var w = v.Weapons[i];
                if (w.IsEnergyWeapon || w.AmmoCapacity <= 0)
                {
                    totalCur += 1f;
                    totalCap += 1f;
                    continue;
                }
                totalCur += w.AmmoCurrent;
                totalCap += w.AmmoCapacity;
            }
            return totalCap > 0f ? Mathf.Clamp01(totalCur / totalCap) : 1f;
        }
    }
}
