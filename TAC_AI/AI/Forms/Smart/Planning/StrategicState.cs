using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Planning
{
    /// <summary>Compact per-tech state for strategic search. Per PLANNING-CONTRACT §3.</summary>
    public readonly struct StrategicTechSummary
    {
        public readonly TechId Id;
        public readonly Vector3 PositionMean;
        public readonly Vector3 VelocityMean;
        public readonly float Heading;
        public readonly float HealthFraction;
        public readonly float ThreatRating;
        public readonly float MobilityRating;
        public readonly float PositionUncertainty;

        public StrategicTechSummary(TechId id, Vector3 pos, Vector3 vel, float heading,
            float health, float threat, float mobility, float uncertainty)
        {
            Id = id; PositionMean = pos; VelocityMean = vel; Heading = heading;
            HealthFraction = health; ThreatRating = threat;
            MobilityRating = mobility; PositionUncertainty = uncertainty;
        }
    }

    /// <summary>
    /// Strategic-search state. Position bucketization keeps the tree finite
    /// (cells of 5 m per axis per PLANNING §3 bucketization).
    /// </summary>
    public sealed class StrategicState
    {
        public IReadOnlyList<StrategicTechSummary> Friendly { get; }
        public IReadOnlyList<StrategicTechSummary> Hostile { get; }
        public PlanLibrary.StrategicPlan CurrentPlan { get; }
        public int PlanTickCount { get; }

        public StrategicState(
            IReadOnlyList<StrategicTechSummary> friendly,
            IReadOnlyList<StrategicTechSummary> hostile,
            PlanLibrary.StrategicPlan currentPlan,
            int planTickCount)
        {
            Friendly = friendly ?? new StrategicTechSummary[0];
            Hostile = hostile ?? new StrategicTechSummary[0];
            CurrentPlan = currentPlan;
            PlanTickCount = planTickCount;
        }

        public override int GetHashCode()
        {
            const float BucketSizeMeters = 5f;
            unchecked
            {
                int h = 17;
                h = h * 31 + Friendly.Count;
                h = h * 31 + Hostile.Count;
                h = h * 31 + (int)CurrentPlan.Type;
                for (int i = 0; i < Friendly.Count; i++)
                {
                    var f = Friendly[i];
                    h = h * 31 + Mathf.RoundToInt(f.PositionMean.x / BucketSizeMeters);
                    h = h * 31 + Mathf.RoundToInt(f.PositionMean.z / BucketSizeMeters);
                }
                for (int i = 0; i < Hostile.Count; i++)
                {
                    var ho = Hostile[i];
                    h = h * 31 + Mathf.RoundToInt(ho.PositionMean.x / BucketSizeMeters);
                    h = h * 31 + Mathf.RoundToInt(ho.PositionMean.z / BucketSizeMeters);
                }
                return h;
            }
        }
    }
}
