using System.Collections.Concurrent;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// P6 Item 26 (REV 7) — per-tech tank-wide HpFraction sidecar via block-survival ratio.
    ///
    /// Matches the production multi-block HP semantic from <c>TankAIHelper.cs:3267</c>
    /// (<c>DamageThreshold = (1 - blockC/maxBlockCount) * 100</c>). Tracks
    /// <c>(current, max)</c> per TechId; max ratchets upward to handle in-game block
    /// additions. NEVER reduces max — damage is measured against the tech's high-water mark.
    ///
    /// Populated by main-thread <c>SmartForm.ObserveWorldTechsIfDue</c> (33ms cadence).
    /// Read by <see cref="TAC_AI.AI.Forms.Smart.Identity.SmartIdentity.RepairSupport"/>
    /// goal source. Lifecycle mirrors P4 IntentSidecar/DamageHints pattern (cleared in
    /// Shutdown; Forget hooked from <c>SmartRuntime.Deregister</c> + <c>WorldModel.DeregisterTech</c>).
    ///
    /// Single-block tanks: <c>blockCount = 1 = maxBlockCount</c> always → <c>HpFraction = 1.0</c>
    /// until the block is destroyed (which despawns the tech). Single-block HP loss is below
    /// the granularity this sidecar tracks; acceptable for v0.2 RepairSupport heuristics
    /// (which target visible block loss on multi-block techs).
    /// </summary>
    public sealed class HealthSidecar : ITechSidecar
    {
        private struct HpState { public int Current; public int Max; }

        private readonly ConcurrentDictionary<TechId, HpState> _state =
            new ConcurrentDictionary<TechId, HpState>();

        // L-028 ITechSidecar wiring.
        public string Name => "HealthSidecar";
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotKeys()
            => (System.Collections.Generic.IReadOnlyCollection<TechId>)_state.Keys;

        /// <summary>
        /// Record current block count for <paramref name="id"/>. First call snapshots max;
        /// subsequent calls ratchet max upward if blockCount grows. Zero/negative block
        /// counts are ignored.
        /// </summary>
        public void Record(TechId id, int blockCount)
        {
            if (blockCount <= 0) return;
            _state.AddOrUpdate(id,
                addValueFactory: _ => new HpState { Current = blockCount, Max = blockCount },
                updateValueFactory: (_, s) => new HpState
                {
                    Current = blockCount,
                    Max = blockCount > s.Max ? blockCount : s.Max
                });
        }

        /// <summary>Tank-wide HpFraction in [0, 1]. Returns 1f for unknown techs (safe default).</summary>
        public float Get(TechId id)
        {
            HpState s;
            if (!_state.TryGetValue(id, out s) || s.Max <= 0) return 1f;
            float frac = (float)s.Current / s.Max;
            return frac < 0f ? 0f : (frac > 1f ? 1f : frac);
        }

        public void Forget(TechId id) { HpState _; _state.TryRemove(id, out _); }
        public void Clear() => _state.Clear();
        public int Count => _state.Count;
    }
}
