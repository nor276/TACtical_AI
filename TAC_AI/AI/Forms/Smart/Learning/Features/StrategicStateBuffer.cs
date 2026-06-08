using System.Collections.Concurrent;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning.Features
{
    // Per-tech double-buffer registry for StrategicStateVector publication. The
    // extractor (background daemon, ~30 Hz) writes per-tech; trainer threads + the
    // ContinuousController fire-time callsite read. Per FEATURE-EXPANSION-PLAN.md
    // §4.4 publication protocol.
    //
    // Discipline mirrors TerrainMap / DamageHintBuffer: ConcurrentDictionary keyed
    // by TechId, per-tech DoubleBuffer<StrategicStateVector> for the atomic swap.
    // Lazy slot allocation on first Publish — the daemon discovers techs via
    // BeliefSnapshot.ByTech enumeration; no explicit OnTechSpawn hook required.
    // Forget on WorldModel.DeregisterTech tear-down.
    public sealed class StrategicStateBuffer
    {
        private readonly ConcurrentDictionary<TechId, DoubleBuffer<StrategicStateVector>> _byTech =
            new ConcurrentDictionary<TechId, DoubleBuffer<StrategicStateVector>>();

        // Atomic publish from the extractor daemon. Per §6.2 protocol; null vectors
        // are ignored (defensive — shouldn't happen but guards a misuse).
        public void Publish(TechId id, StrategicStateVector vector)
        {
            if (vector == null) return;
            var buf = _byTech.GetOrAdd(id, _ => new DoubleBuffer<StrategicStateVector>(null));
            buf.Write(vector);
        }

        // Background-side read. Returns null when the tech has no slot yet, or has
        // been Forgotten. Caller is responsible for stale-generation discard via
        // StrategicStateVector.Generation comparison.
        public StrategicStateVector TryRead(TechId id)
        {
            DoubleBuffer<StrategicStateVector> buf;
            if (!_byTech.TryGetValue(id, out buf)) return null;
            return buf.Read();
        }

        // Lifecycle hook from TeamRuntime.RegisterTech — pre-allocates the slot so
        // the first Publish doesn't race the GetOrAdd. Optional; lazy allocation
        // works either way. Kept for symmetry with the spawn/recycle pair below.
        public void OnTechSpawn(TechId id)
        {
            _byTech.GetOrAdd(id, _ => new DoubleBuffer<StrategicStateVector>(null));
        }

        // Lifecycle hook from WorldModel.DeregisterTech + IAIForm.OnTechRecycle.
        // Drops the slot so a recycled tech-id doesn't read a previous-lifecycle
        // vector. Mirrors DamageHintBuffer.Forget at DamageHintBuffer.cs:163.
        public void OnTechRecycle(TechId id)
        {
            DoubleBuffer<StrategicStateVector> _;
            _byTech.TryRemove(id, out _);
        }

        // Synonym for OnTechRecycle — keeps the established DamageHintBuffer/Sidecar
        // verb in places that already call Forget(id) on a lifecycle exit.
        public void Forget(TechId id) { OnTechRecycle(id); }

        public void Clear() { _byTech.Clear(); }

        public int SubjectCount { get { return _byTech.Count; } }
    }
}
