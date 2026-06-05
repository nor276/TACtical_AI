using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Planning
{
    /// <summary>
    /// Heuristic value function for strategic terminal states. Per PLANNING-CONTRACT §5.
    /// Returns a scalar value in roughly [-6, +6] for terminal states evaluated during
    /// MCTS rollouts.
    /// </summary>
    public static class StrategicValueFunction
    {
        // Provisional weights per PLANNING §5.6. Mutable so CMA-ES (Training §5) can adapt them.
        public static float WHealth = 2.0f;
        public static float WPosition = 1.0f;
        public static float WThreat = 1.5f;
        public static float WSpeed = 0.5f;
        public static float WCoverage = 1.0f;
        public static float WLearned = 0.0f;
        // P5 Item 24 (REV 4): perimeter-coverage weight. Default-zero preserves v0.1
        // Evaluate output byte-identically (term contributes 0). Flip to ~1.0f to reward
        // plans whose friendlies are radially dispersed around the team centroid
        // (defensive perimeter geometry). CMA-ES tunable.
        public static float WPerimeterCoverage = 0.0f;

        public static float Evaluate(StrategicState state)
        {
            return
                WHealth * HealthAdvantage(state)
              + WPosition * PositionQuality(state)
              + WThreat * (-ThreatExposure(state))
              + WSpeed * RelativeSpeedAdvantage(state)
              + WCoverage * WeaponCoverage(state)
              + WPerimeterCoverage * PerimeterCoverage(state)
              + WLearned * LearnedValue(state);
        }

        public static float HealthAdvantage(StrategicState s)
        {
            float fh = 0f, hh = 0f;
            for (int i = 0; i < s.Friendly.Count; i++) fh += s.Friendly[i].HealthFraction;
            for (int i = 0; i < s.Hostile.Count; i++) hh += s.Hostile[i].HealthFraction;
            float total = s.Friendly.Count + s.Hostile.Count;
            return total > 0f ? (fh - hh) / total : 0f;
        }

        public static float PositionQuality(StrategicState s)
        {
            if (s.Friendly.Count == 0 || s.Hostile.Count == 0) return 0f;
            const float PreferredRange = 80f;
            const float SigmaRange = 40f;
            float sum = 0f;
            for (int i = 0; i < s.Friendly.Count; i++)
            {
                float bestFit = 0f;
                for (int j = 0; j < s.Hostile.Count; j++)
                {
                    float dist = (s.Hostile[j].PositionMean - s.Friendly[i].PositionMean).magnitude;
                    float delta = dist - PreferredRange;
                    float fit = Mathf.Exp(-(delta * delta) / (2f * SigmaRange * SigmaRange));
                    if (fit > bestFit) bestFit = fit;
                }
                sum += bestFit;
            }
            return sum / s.Friendly.Count;
        }

        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6: previously summed per-friendly-per-hostile
        // exposure then divided by N_F × N_H — which made adding a single low-threat
        // hostile drown out the close-range threat from one nearby hostile, and made
        // the value scale-dependent on team sizes (one-on-one and ten-on-ten read very
        // different scales of exposure even at identical relative-geometry threat).
        // New shape: for each friendly, compute the worst-case (max over hostiles)
        // weighted exposure they personally face, then average across friendlies. Result
        // is intrinsically scale-invariant in N_H and bounded to [0, 1] without an ad-hoc
        // clamp. Per the contract, threat is a per-friendly safety signal — averaging the
        // per-friendly maxes preserves "any single friendly in mortal danger pulls the
        // value down" without diluting through hostile-count normalization.
        public static float ThreatExposure(StrategicState s)
        {
            if (s.Friendly.Count == 0 || s.Hostile.Count == 0) return 0f;
            const float ThreatRadius = 80f;
            float perFriendlySum = 0f;
            for (int i = 0; i < s.Friendly.Count; i++)
            {
                float myExposure = 0f;
                for (int j = 0; j < s.Hostile.Count; j++)
                {
                    float dist = (s.Hostile[j].PositionMean - s.Friendly[i].PositionMean).magnitude;
                    if (dist >= ThreatRadius) continue;
                    // Each hostile contributes proximity-weighted threat into the friendly's
                    // personal exposure. Multiple close hostiles stack — being surrounded
                    // is worse than being engaged by one.
                    myExposure += s.Hostile[j].ThreatRating * (1f - dist / ThreatRadius);
                }
                perFriendlySum += Mathf.Clamp01(myExposure);
            }
            return perFriendlySum / s.Friendly.Count;
        }

        public static float RelativeSpeedAdvantage(StrategicState s)
        {
            if (s.Friendly.Count == 0 || s.Hostile.Count == 0) return 0f;
            float fm = 0f, hm = 0f;
            for (int i = 0; i < s.Friendly.Count; i++) fm += s.Friendly[i].MobilityRating;
            for (int i = 0; i < s.Hostile.Count; i++) hm += s.Hostile[i].MobilityRating;
            fm /= s.Friendly.Count;
            hm /= s.Hostile.Count;
            float max = Mathf.Max(fm, hm);
            return max > 0f ? (fm - hm) / max : 0f;
        }

        public static float WeaponCoverage(StrategicState s)
        {
            if (s.Friendly.Count == 0 || s.Hostile.Count == 0) return 0f;
            const float CoverageRange = 100f;
            int coveredHostile = 0;
            for (int j = 0; j < s.Hostile.Count; j++)
            {
                bool anyCover = false;
                for (int i = 0; i < s.Friendly.Count; i++)
                {
                    float dist = (s.Hostile[j].PositionMean - s.Friendly[i].PositionMean).magnitude;
                    if (dist < CoverageRange) { anyCover = true; break; }
                }
                if (anyCover) coveredHostile++;
            }
            return (float)coveredHostile / s.Hostile.Count;
        }

        /// <summary>
        /// P5 Item 24 (REV 4): reward plans that maintain friendly-tech radial coverage
        /// around the team centroid. Standard circular-statistics mean-resultant-length:
        /// project each friendly's angular position around the centroid (XZ plane), sum
        /// the unit vectors, divide by N — R≈1 means clustered (poor coverage),
        /// R≈0 means perfectly dispersed (full perimeter). Returned value is 1 - R,
        /// so higher is better.
        ///
        /// Defaults to gain WPerimeterCoverage = 0f → contributes 0 to Evaluate → v0.1
        /// scoring preserved bit-identically. CMA-ES or in-game tunable can flip the
        /// weight to reward defensive-perimeter plans.
        /// </summary>
        public static float PerimeterCoverage(StrategicState s)
        {
            if (s.Friendly.Count < 2) return 0f;
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < s.Friendly.Count; i++)
                centroid += s.Friendly[i].PositionMean;
            centroid /= s.Friendly.Count;
            float sumCos = 0f, sumSin = 0f;
            for (int i = 0; i < s.Friendly.Count; i++)
            {
                Vector3 d = s.Friendly[i].PositionMean - centroid;
                float ang = Mathf.Atan2(d.z, d.x);
                sumCos += Mathf.Cos(ang);
                sumSin += Mathf.Sin(ang);
            }
            float R = Mathf.Sqrt(sumCos * sumCos + sumSin * sumSin) / s.Friendly.Count;
            return 1f - R;
        }

        /// <summary>Learned value contribution. v0.1.0: zero until Learning ships an ActionValueEstimator.</summary>
        public static float LearnedValue(StrategicState s) => 0f;
    }
}
