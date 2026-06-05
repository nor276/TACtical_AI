using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>
    /// L-033: atomic holder for the current TerrainMap. Replaces the prior pattern where
    /// PathingService mutated a `_terrain` field directly — a read on the threat-field
    /// thread could see the half-replaced terrain during OnWorldReset. The publication
    /// holder swaps the reference via <see cref="Volatile.Write"/>; readers do a single
    /// volatile read and either see the old terrain (fully valid) or the new one (fully
    /// constructed by Init). No torn reads possible.
    ///
    /// Generation counter increments on every swap so L-035's IsFreshlyAllocated check can
    /// observe "the terrain I'm holding is from a prior world reset, discard cached
    /// derivatives" without needing reference equality semantics across resets.
    /// </summary>
    public sealed class TerrainPublication
    {
        private TerrainMap _current;
        private long _generation;

        public long Generation => System.Threading.Interlocked.Read(ref _generation);

        public TerrainMap Read()
        {
            return System.Threading.Volatile.Read(ref _current);
        }

        /// <summary>Swap in a new TerrainMap. Returns the prior reference for cleanup.</summary>
        public TerrainMap Publish(TerrainMap next)
        {
            var prior = System.Threading.Interlocked.Exchange(ref _current, next);
            System.Threading.Interlocked.Increment(ref _generation);
            return prior;
        }

        public void Clear()
        {
            System.Threading.Volatile.Write(ref _current, null);
            // Generation NOT reset — preserves monotonic ordering across Shutdown/Init.
        }
    }
}
