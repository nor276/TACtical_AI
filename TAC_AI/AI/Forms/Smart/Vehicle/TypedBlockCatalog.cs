using System.Collections.Concurrent;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: process-wide typed per-block-type catalog. Lifecycle owned by SmartRuntime
    /// (Init creates; Shutdown AFTER WorkerLifecycleRegistry.CancelAllAndJoin calls Reset).
    ///
    /// Per CHASSIS-DESIGN.md threading model:
    ///   - GetOrProbe writes (probes new types) are MAIN-THREAD ONLY, triggered exclusively
    ///     from ChassisCapture during snapshot construction. Asserts in DEBUG.
    ///   - TryGet reads are worker-safe; sealed-class BlockArchetype with readonly fields
    ///     makes references safe to read from any thread without locking.
    ///   - GetOrAdd factory may run more than once under contention per .NET BCL contract,
    ///     but with a single writer in practice contention is zero. Probe is idempotent.
    ///
    /// Hit/miss counters expose catalog hit-rate to the [TIMING] diagnostic in SmartRuntime.
    /// </summary>
    public sealed class TypedBlockCatalog
    {
        private readonly ConcurrentDictionary<int, BlockArchetype> _byType
            = new ConcurrentDictionary<int, BlockArchetype>();

        private long _hits;
        private long _misses;

        public long Hits   => Interlocked.Read(ref _hits);
        public long Misses => Interlocked.Read(ref _misses);
        public int  Count  => _byType.Count;

        /// <summary>
        /// Worker-safe lookup. Returns null if the typeKey hasn't been probed yet.
        /// Increments hit/miss counters via Interlocked.
        /// </summary>
        public BlockArchetype TryGet(int typeKey)
        {
            if (_byType.TryGetValue(typeKey, out var archetype))
            {
                Interlocked.Increment(ref _hits);
                return archetype;
            }
            Interlocked.Increment(ref _misses);
            return null;
        }

        /// <summary>
        /// Main-thread-only. Returns the archetype for the given typeKey; probes if absent.
        /// Probe failures return the Unknown sentinel (NOT cached -- retry next sighting).
        /// </summary>
        public BlockArchetype GetOrProbe(TankBlock blockPrefabOrInstance)
        {
            if (blockPrefabOrInstance == null) return BlockArchetype.Unknown;
            int typeKey = (int)blockPrefabOrInstance.BlockType;
            if (_byType.TryGetValue(typeKey, out var existing))
            {
                Interlocked.Increment(ref _hits);
                return existing;
            }
            Interlocked.Increment(ref _misses);
            var probed = ArchetypeProbe.Probe(blockPrefabOrInstance, typeKey);
            if (probed != null && probed != BlockArchetype.Unknown)
            {
                // GetOrAdd factory race: under main-thread-only writes this is exactly-once,
                // but defensive in case probe is ever re-entered. The returned archetype
                // becomes the canonical one; any duplicate is discarded by GetOrAdd.
                return _byType.GetOrAdd(typeKey, probed);
            }
            // Unknown sentinel -- not cached, retry next sighting (Failure mode #1).
            return BlockArchetype.Unknown;
        }

        /// <summary>
        /// Called from SmartRuntime.Shutdown AFTER WorkerLifecycleRegistry.CancelAllAndJoin
        /// per SmartRuntime.cs:549-573 ordering. Workers are quiesced before references drop.
        /// </summary>
        public void Reset()
        {
            _byType.Clear();
            Interlocked.Exchange(ref _hits, 0L);
            Interlocked.Exchange(ref _misses, 0L);
        }

        /// <summary>
        /// P2 Item 9: drop a single archetype so the next sighting re-probes. Worker-safe
        /// (ConcurrentDictionary remove); the next TryGet returns null, the next GetOrProbe
        /// re-probes.
        /// </summary>
        public bool InvalidateType(int typeKey)
        {
            BlockArchetype _;
            return _byType.TryRemove(typeKey, out _);
        }

        /// <summary>
        /// P2 Item 9: drop every cached archetype. Worker-safe. Used by mod hot-reload
        /// triggers via CatalogInvalidation.OnAllInvalidated. Hit/miss counters NOT
        /// reset (they're cumulative diagnostics).
        /// </summary>
        public void InvalidateAll()
        {
            _byType.Clear();
        }
    }
}
