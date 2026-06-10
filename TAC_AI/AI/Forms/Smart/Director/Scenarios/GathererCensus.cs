using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Scenarios
{
    // Census of resource chunks within a radius. Shared by ApplyChunkSpawn (which needs
    // the deficit) and the IdleGathererGuard reachability test (which needs at-least-3).
    // Single source of truth keeps the LayerMask cached + the OverlapSphere result shape
    // identical across consumers. Main-thread only — Physics.OverlapSphere is not safe
    // off-thread.
    public static class GathererCensus
    {
        private static int _cachedPickupMask = -1;

        public static int PickupLayerMask
        {
            get
            {
                if (_cachedPickupMask < 0) _cachedPickupMask = LayerMask.GetMask("Pickup");
                return _cachedPickupMask;
            }
        }

        // Returns the number of pickups within radius. Hits array is the raw
        // Physics.OverlapSphere result so callers that want to walk it can; pass null when
        // count is all you need.
        public static int ChunksInRange(Vector3 pos, float radius, out Collider[] hits)
        {
            int mask = PickupLayerMask;
            if (mask == 0)
            {
                hits = null;
                return 0;
            }
            hits = Physics.OverlapSphere(pos, radius, mask);
            return hits != null ? hits.Length : 0;
        }

        // Discard the hit list when the caller only needs the count.
        public static int ChunksInRange(Vector3 pos, float radius)
        {
            Collider[] hits;
            return ChunksInRange(pos, radius, out hits);
        }
    }
}
