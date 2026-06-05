using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// Per-friendly-tech line-of-sight computation. Per COORDINATION-CONTRACT §3.1.
    /// Aggregation across the team lives inline in <c>TeamRuntime.BuildTeamSnapshot</c>
    /// (SmartRuntime.cs:401-429) per REV 7 P5 Item 22 — the old <c>TeamBelief.cs</c>
    /// aggregator was orphaned (zero callers) and was deleted.
    /// </summary>
    public static class LineOfSight
    {
        /// <summary>
        /// Compute the set of enemies that <paramref name="observer"/> currently has LOS to.
        /// Uses physics raycast as the v0.1.0 implementation (TerraTech's ManVisible.IsVisible
        /// would be cleaner but requires API verification — TODO v0.3).
        /// Main thread only.
        ///
        /// P5 prerequisite (REV 7 — AUDIT.md:250 self-mask bug fix): the original
        /// single-Raycast path treated the observer's own colliders as the first hit
        /// (boundsCentreWorld lives inside the tank's bounds), so every enemy reported
        /// occluded. Now uses <see cref="Physics.RaycastAll"/> + skips hits whose
        /// transform.root matches the observer's, picks the nearest non-self hit as
        /// the true occluder. Alloc per query is bounded by Unity's RaycastAll buffer.
        /// </summary>
        public static List<TechId> ComputeLOS(
            Tank observer,
            IReadOnlyList<BeliefState> enemies,
            float maxRange = 300f)
        {
            var seen = new List<TechId>(4);
            if (observer == null || enemies == null) return seen;

            Vector3 originWorld = observer.boundsCentreWorldNoCheck;
            Transform observerRoot = observer.transform;

            for (int i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                Vector3 toEnemy = e.PositionMean - originWorld;
                float dist = toEnemy.magnitude;
                if (dist > maxRange) continue;
                Vector3 dir = toEnemy / Mathf.Max(dist, 1e-3f);

                // P5 prerequisite (REV 7): RaycastAll + self-filter. The observer's
                // boundsCentreWorld is inside its own colliders, so a single Physics.Raycast
                // would return the observer itself as the first hit. We walk all hits and
                // take the nearest one whose transform.root is NOT the observer.
                RaycastHit[] hits = Physics.RaycastAll(originWorld, dir, dist);
                bool hasNonSelfHit = false;
                float nearestT = float.MaxValue;
                Vector3 nearestPoint = Vector3.zero;
                for (int h = 0; h < hits.Length; h++)
                {
                    var hcol = hits[h].collider;
                    if (hcol == null) continue;
                    if (hcol.transform.root == observerRoot) continue;  // skip our own colliders
                    if (hits[h].distance < nearestT)
                    {
                        nearestT = hits[h].distance;
                        nearestPoint = hits[h].point;
                        hasNonSelfHit = true;
                    }
                }

                if (hasNonSelfHit)
                {
                    // If the nearest non-self hit is within tolerance of the enemy position, we have LOS.
                    if ((nearestPoint - e.PositionMean).sqrMagnitude < 25f)
                        seen.Add(e.Id);
                    // else: occluded — terrain or other tech in the way.
                }
                else
                {
                    // No non-self hit means clear line to the enemy (within maxRange).
                    seen.Add(e.Id);
                }
            }
            return seen;
        }
    }
}
