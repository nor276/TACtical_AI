using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// Per-friendly-tech line-of-sight computation. Per COORDINATION-CONTRACT §3.1.
    /// Result feeds <see cref="TeamBelief"/> for aggregation across the team.
    /// </summary>
    public static class LineOfSight
    {
        /// <summary>
        /// Compute the set of enemies that <paramref name="observer"/> currently has LOS to.
        /// Uses physics raycast as the v0.1.0 implementation (TerraTech's ManVisible.IsVisible
        /// would be cleaner but requires API verification — TODO v0.2).
        /// Main thread only.
        /// </summary>
        public static List<TechId> ComputeLOS(
            Tank observer,
            IReadOnlyList<BeliefState> enemies,
            float maxRange = 300f)
        {
            var seen = new List<TechId>(4);
            if (observer == null || enemies == null) return seen;

            Vector3 originWorld = observer.boundsCentreWorldNoCheck;
            for (int i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                Vector3 toEnemy = e.PositionMean - originWorld;
                float dist = toEnemy.magnitude;
                if (dist > maxRange) continue;
                Vector3 dir = toEnemy / Mathf.Max(dist, 1e-3f);

                // Raycast from observer toward enemy belief mean. Mask out the observer's own colliders.
                // TODO v0.2: confirm the layer mask + Physics raycast API matches TerraTech's expectations.
                if (Physics.Raycast(originWorld, dir, out var hit, dist))
                {
                    // If the first hit is within tolerance of the enemy position, we have LOS.
                    if ((hit.point - e.PositionMean).sqrMagnitude < 25f)
                        seen.Add(e.Id);
                    // else: occluded — terrain or other tech in the way.
                }
                else
                {
                    // No hit means clear line to the enemy (within maxRange).
                    seen.Add(e.Id);
                }
            }
            return seen;
        }
    }
}
