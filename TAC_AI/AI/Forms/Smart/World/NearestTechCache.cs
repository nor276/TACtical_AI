using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Tile-bucket spatial grid over BeliefSnapshot.ByTech for K-nearest queries from the
    /// StrategicStateExtractor daemon (plan §8.3). Bucket size 64 m. Rebuilt once per
    /// daemon tick from the snapshot; queries scan a 3×3 neighborhood of the origin's tile
    /// and top-K by sqr-distance.
    ///
    /// Threading: pure data. Both Rebuild and Query run on the background extractor
    /// thread — NO Unity touches, no Singleton.Manager access, no Transform/Rigidbody.
    /// Vector3 arithmetic + Mathf.Atan2 are thread-safe scalar math.
    ///
    /// Lifetime: one instance per StrategicStateExtractor; the extractor owns it and
    /// calls Rebuild(snapshot) at the head of each tick before per-tech queries.
    /// </summary>
    public sealed class NearestTechCache
    {
        /// <summary>Tile edge length, meters. Plan §6.4 / §11.</summary>
        public const float TileSize = 64f;

        /// <summary>
        /// Inverse for the hot path — Rebuild and every Query divide world coords by
        /// TileSize, so cache the reciprocal once.
        /// </summary>
        private const float InvTileSize = 1f / TileSize;

        /// <summary>Filter mode for QueryNearest.</summary>
        public enum TeamFilter : byte
        {
            /// <summary>Any team. The subject itself is still excluded.</summary>
            Any = 0,
            /// <summary>Only entries whose Team equals the supplied team. Subject excluded.</summary>
            SameTeam = 1,
            /// <summary>Only entries whose Team differs from the supplied team. Subject excluded.</summary>
            OtherTeam = 2,
        }

        /// <summary>
        /// One K-nearest hit. Deltas are origin-relative world-frame; the consumer rotates
        /// to local frame at the slot-fill site (it knows its own forward and we don't).
        /// </summary>
        public struct KNearestResult
        {
            public TechId Id;
            public TeamId Team;
            public float SqrDistance;
            /// <summary>target.PositionMean − origin (world frame).</summary>
            public Vector3 RelativePos;
            /// <summary>target.VelocityMean (world frame); caller subtracts its own vel.</summary>
            public Vector3 TargetVelocity;
            /// <summary>Atan2(RelativePos.x, RelativePos.z) — world-XZ bearing, radians.</summary>
            public float BearingRad;
            /// <summary>Pre-baked belief ref — saves the consumer a second ByTech lookup.</summary>
            public BeliefState Belief;
        }

        // tile coord -> tech ids in that tile. Rebuilt every tick; capacity reused.
        private readonly Dictionary<IntVector2, List<TechId>> _buckets = new Dictionary<IntVector2, List<TechId>>(64);

        // Pool of List<TechId> we hand out + reclaim on Rebuild so steady-state allocates zero.
        private readonly Stack<List<TechId>> _listPool = new Stack<List<TechId>>(32);

        // Last rebuilt snapshot — the BeliefState refs we hold in the buckets come from here.
        private BeliefSnapshot _snapshot = BeliefSnapshot.Empty;

        // Scratch list for top-K accumulation. Cleared per Query.
        private readonly List<KNearestResult> _scratch = new List<KNearestResult>(32);

        /// <summary>Snapshot the cache was last rebuilt from.</summary>
        public BeliefSnapshot CurrentSnapshot { get { return _snapshot; } }

        /// <summary>Number of tiles currently populated. Diagnostic.</summary>
        public int BucketCount { get { return _buckets.Count; } }

        /// <summary>
        /// Rebuild the bucket grid from <paramref name="snapshot"/>. Daemon calls this once
        /// per tick before iterating techs. Null or empty snapshot clears the grid.
        /// </summary>
        public void Rebuild(BeliefSnapshot snapshot)
        {
            // Reclaim bucket lists to the pool — keep their capacity for reuse.
            foreach (var kv in _buckets)
            {
                var list = kv.Value;
                list.Clear();
                _listPool.Push(list);
            }
            _buckets.Clear();

            if (snapshot == null || snapshot.ByTech == null)
            {
                _snapshot = BeliefSnapshot.Empty;
                return;
            }
            _snapshot = snapshot;

            foreach (var pair in snapshot.ByTech)
            {
                var belief = pair.Value;
                if (belief == null) continue;
                var coord = TileOf(belief.PositionMean);
                List<TechId> bucket;
                if (!_buckets.TryGetValue(coord, out bucket))
                {
                    bucket = _listPool.Count > 0 ? _listPool.Pop() : new List<TechId>(4);
                    _buckets[coord] = bucket;
                }
                bucket.Add(pair.Key);
            }
        }

        /// <summary>
        /// Top-K techs within <paramref name="maxRange"/> of <paramref name="origin"/>, filtered
        /// by team. Excludes the subject TechId. Results sorted ascending by SqrDistance.
        /// Returned array length is min(K, found).
        /// </summary>
        public KNearestResult[] QueryNearest(Vector3 origin, TechId subject, TeamId team,
            TeamFilter filter, int K, float maxRange)
        {
            if (K <= 0 || maxRange <= 0f || _buckets.Count == 0)
                return EmptyResults;

            float maxSqr = maxRange * maxRange;
            var byTech = _snapshot.ByTech;
            _scratch.Clear();

            // 3x3 neighborhood of origin tile covers up to 1.5*TileSize range; if maxRange
            // exceeds TileSize we widen the scan radius to ceil(maxRange / TileSize).
            int rTiles = 1;
            if (maxRange > TileSize)
            {
                rTiles = Mathf.CeilToInt(maxRange * InvTileSize);
            }
            IntVector2 centre = TileOf(origin);

            for (int dz = -rTiles; dz <= rTiles; dz++)
            {
                for (int dx = -rTiles; dx <= rTiles; dx++)
                {
                    IntVector2 coord;
                    coord.x = centre.x + dx;
                    coord.y = centre.y + dz;
                    List<TechId> bucket;
                    if (!_buckets.TryGetValue(coord, out bucket)) continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        var id = bucket[i];
                        if (id == subject) continue;
                        BeliefState belief;
                        if (!byTech.TryGetValue(id, out belief) || belief == null) continue;

                        if (filter == TeamFilter.SameTeam && belief.Team != team) continue;
                        if (filter == TeamFilter.OtherTeam && belief.Team == team) continue;

                        Vector3 rel = belief.PositionMean - origin;
                        float sqr = rel.x * rel.x + rel.y * rel.y + rel.z * rel.z;
                        if (sqr > maxSqr) continue;

                        KNearestResult hit;
                        hit.Id = id;
                        hit.Team = belief.Team;
                        hit.SqrDistance = sqr;
                        hit.RelativePos = rel;
                        hit.TargetVelocity = belief.VelocityMean;
                        hit.BearingRad = Mathf.Atan2(rel.x, rel.z);
                        hit.Belief = belief;
                        _scratch.Add(hit);
                    }
                }
            }

            int found = _scratch.Count;
            if (found == 0) return EmptyResults;

            // Partial selection: small K, small scratch — insertion sort over the prefix is
            // O(found * K) and beats a full sort for K=1..4 typical usage.
            int take = K < found ? K : found;
            for (int i = 0; i < take; i++)
            {
                int best = i;
                float bestSqr = _scratch[i].SqrDistance;
                for (int j = i + 1; j < found; j++)
                {
                    if (_scratch[j].SqrDistance < bestSqr)
                    {
                        best = j;
                        bestSqr = _scratch[j].SqrDistance;
                    }
                }
                if (best != i)
                {
                    var tmp = _scratch[i];
                    _scratch[i] = _scratch[best];
                    _scratch[best] = tmp;
                }
            }

            var result = new KNearestResult[take];
            for (int i = 0; i < take; i++) result[i] = _scratch[i];
            return result;
        }

        /// <summary>
        /// Convenience wrapper matching plan §4.2 pseudocode. Returns the single nearest
        /// hostile (any team ≠ subject's team) within <paramref name="maxRange"/>, or null
        /// if none. Subject pose read from <paramref name="snapshot"/>.
        /// </summary>
        public BeliefState NearestHostile(TechId subject, BeliefSnapshot snapshot, float maxRange)
        {
            return Nearest1(subject, snapshot, TeamFilter.OtherTeam, maxRange);
        }

        /// <summary>
        /// Convenience wrapper matching plan §4.2 pseudocode. Returns the single nearest
        /// friendly (same-team, excluding subject) within <paramref name="maxRange"/>.
        /// </summary>
        public BeliefState NearestFriendly(TechId subject, BeliefSnapshot snapshot, float maxRange)
        {
            return Nearest1(subject, snapshot, TeamFilter.SameTeam, maxRange);
        }

        private BeliefState Nearest1(TechId subject, BeliefSnapshot snapshot, TeamFilter filter, float maxRange)
        {
            if (snapshot == null || snapshot.ByTech == null) return null;
            BeliefState self;
            if (!snapshot.ByTech.TryGetValue(subject, out self) || self == null) return null;
            var hits = QueryNearest(self.PositionMean, subject, self.Team, filter, 1, maxRange);
            return hits.Length > 0 ? hits[0].Belief : null;
        }

        /// <summary>Tile-coord of a world-position. Mathf.FloorToInt handles negative coords correctly.</summary>
        public static IntVector2 TileOf(Vector3 worldPos)
        {
            IntVector2 c;
            c.x = Mathf.FloorToInt(worldPos.x * InvTileSize);
            c.y = Mathf.FloorToInt(worldPos.z * InvTileSize);
            return c;
        }

        private static readonly KNearestResult[] EmptyResults = new KNearestResult[0];
    }
}
