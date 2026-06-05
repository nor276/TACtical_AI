using System.Collections.Concurrent;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// P4 Item 10: per-tech intent snapshot. Categorical intent label + confidence + tick.
    /// CategoryIndex maps to <see cref="IntentCategories"/> (Aggressing=0..Idle=5).
    /// Readonly struct; published into <see cref="IntentRegistry"/> by future P7 producer
    /// (<c>LearningService.OpponentIntentClassifier</c>); consumed by classifier rules in P6+.
    /// </summary>
    public readonly struct IntentSnapshot
    {
        public readonly int CategoryIndex;
        public readonly float Confidence;
        public readonly long PublishedTickMono;

        public IntentSnapshot(int categoryIndex, float confidence, long publishedTickMono)
        {
            CategoryIndex = categoryIndex;
            Confidence = confidence;
            PublishedTickMono = publishedTickMono;
        }
    }

    /// <summary>
    /// P4 Item 10: per-tech intent sidecar. Producer-only at v0.2 ship — no v0.2 consumer
    /// reads from it (P7's <c>OpponentIntentClassifier</c> will publish; identity classifier
    /// rule for "Provoked" / "Patrol" gating reads in v0.3+).
    ///
    /// Storage is <see cref="ConcurrentDictionary{TKey, TValue}"/> because the future
    /// producer (LearningService trainer worker per LearningService.cs:50-90) writes from
    /// a worker thread while consumers read from the main thread.
    ///
    /// Lifecycle: owned by <see cref="SmartRuntime"/>, cleared on Shutdown, per-tech
    /// entries Forget'd from <see cref="SmartRuntime.Deregister"/> and
    /// <see cref="WorldModel.DeregisterTech"/>.
    /// </summary>
    public sealed class IntentRegistry : ITechSidecar
    {
        private readonly ConcurrentDictionary<TechId, IntentSnapshot> _byTech =
            new ConcurrentDictionary<TechId, IntentSnapshot>();

        public void Publish(TechId id, IntentSnapshot snapshot) => _byTech[id] = snapshot;

        public bool TryGet(TechId id, out IntentSnapshot snapshot)
            => _byTech.TryGetValue(id, out snapshot);

        public void Forget(TechId id) { IntentSnapshot _; _byTech.TryRemove(id, out _); }

        public void Clear() => _byTech.Clear();

        public int Count => _byTech.Count;

        // L-028 ITechSidecar wiring.
        public string Name => "IntentRegistry";
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotKeys()
        {
            // ConcurrentDictionary.Keys is a stable snapshot for our cadence (60s leak-watch).
            return (System.Collections.Generic.IReadOnlyCollection<TechId>)_byTech.Keys;
        }
    }
}
