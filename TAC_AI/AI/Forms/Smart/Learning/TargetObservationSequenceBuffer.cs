using System;
using System.Collections.Concurrent;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Learning.Features;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P7 Item 16: per-self-tech observation-sequence buffer feeding the intent classifier.
    ///
    /// Maintains a per-TechId ring of <see cref="SeqLen"/> rows × <see cref="FeatureDim"/>
    /// floats. Each row is the self-tech's StrategicStateVector IntentSlots view (40 floats
    /// describing self's relationship to its current K-nearest hostile per FEATURE-EXPANSION-PLAN
    /// §3.2). When the ring is full and a label can be derived, <see cref="TryBuildEvent"/>
    /// produces an <see cref="IntentEvent"/> for the classifier's training queue.
    ///
    /// FeatureDim widened 12 → 40 (Phase-3 §7.1) so rows match
    /// <see cref="OpponentIntentClassifier.FeatureDim"/>. The 40 slots are filled by the
    /// caller (SmartPerTechState.TickMainThread) reading from
    /// <see cref="StrategicStateBuffer.TryRead"/> + slicing the IntentBase view.
    ///
    /// Lifecycle: owned by <see cref="LearningService"/>; <see cref="Deregister"/> hooked
    /// from <c>SmartRuntime.Deregister</c> + <c>WorldModel.DeregisterTech</c> so dead
    /// techs don't leak ring storage.
    /// </summary>
    public sealed class TargetObservationSequenceBuffer : TAC_AI.AI.Forms.Smart.World.ITechSidecar
    {
        // L-028 ITechSidecar wiring.
        public string Name => "TargetObservationSequenceBuffer";
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotKeys()
            => (System.Collections.Generic.IReadOnlyCollection<TechId>)_byTech.Keys;
        public void Forget(TechId id) => Deregister(id);

        public const int SeqLen = OpponentIntentClassifier.SeqLen;          // 30
        public const int FeatureDim = OpponentIntentClassifier.FeatureDim;  // 40 post-Phase-3

        private sealed class Ring
        {
            internal readonly float[] _slots = new float[SeqLen * FeatureDim];
            internal int _head;   // next write row index
            internal int _count;  // 0..SeqLen
            internal long _lastTickMono;
        }

        private readonly ConcurrentDictionary<TechId, Ring> _byTech =
            new ConcurrentDictionary<TechId, Ring>();

        /// <summary>
        /// Append a feature row for <paramref name="target"/>. Caller is responsible for
        /// passing a row of length <see cref="FeatureDim"/>. Rows shorter than FeatureDim
        /// are silently rejected (no partial writes). Per-tech ring rotates head when full.
        /// Thread-safety: per-Ring lock — multiple producers per target are safe.
        /// </summary>
        public void RecordRow(TechId target, float[] row, long tickMono)
        {
            if (row == null || row.Length < FeatureDim) return;
            var ring = _byTech.GetOrAdd(target, _ => new Ring());
            lock (ring._slots)
            {
                int offset = ring._head * FeatureDim;
                for (int i = 0; i < FeatureDim; i++) ring._slots[offset + i] = row[i];
                ring._head = (ring._head + 1) % SeqLen;
                if (ring._count < SeqLen) ring._count++;
                ring._lastTickMono = tickMono;
            }
        }

        /// <summary>
        /// P11 T2 Item 50 (RESHAPED — was the v0.3-deferred stub): attempt to build an
        /// <see cref="IntentEvent"/> for <paramref name="selfTech"/> using the heuristic
        /// labeler below. Returns false when the ring isn't full OR when the heuristic
        /// can't assign a label confidently.
        ///
        /// Phase-3 §7.1 widening: rows are now 40-slot IntentSlots views (self's relationship
        /// to its K-nearest hostile). The labeler indexes via <c>IntentSlots.*</c> names —
        /// never via literal offsets — so a future slot reshuffle stays one-edit.
        ///
        /// Heuristic labels:
        ///   target speed mean &lt; IdleSpeedThreshold → Idle
        ///   target speed mean &lt; HoldingSpeedThreshold → Holding
        ///   distance shrink across seq &gt; +AggressingClosingThreshold → Aggressing
        ///   distance shrink &lt; -AggressingClosingThreshold → Retreating
        ///   max heading-change rate across seq &gt; FlankingHeadingRateThreshold → Flanking
        ///   else → Repositioning
        ///
        /// <paramref name="ownAnchorWorld"/> is retained on the signature for back-compat
        /// but unused — the new Distance slot (IntentSlots.Distance) is the canonical
        /// self↔target distance signal.
        ///
        /// Bootstrap supervision: the labels ARE coarse — a real classifier replaces this
        /// in v0.3+. The point is to feed the BPTT path real (sequence, label) tuples so
        /// the GRU+Dense head can learn the broad-stroke separation.
        /// </summary>
        public bool TryBuildEvent(TechId selfTech, Vector3 ownAnchorWorld, out IntentEvent ev)
        {
            ev = default(IntentEvent);
            Ring r;
            if (!_byTech.TryGetValue(selfTech, out r)) return false;

            // Snapshot the ring under lock so the labeler reads consistent data
            // without holding the lock while we compute statistics.
            float[] snap = new float[SeqLen * FeatureDim];
            lock (r._slots)
            {
                if (r._count < SeqLen) return false;
                // Linearize the ring so snap[0] is the OLDEST row, snap[(SeqLen-1)*FD] is newest.
                int oldestRow = r._head; // when full, head points at oldest
                for (int row = 0; row < SeqLen; row++)
                {
                    int srcRow = (oldestRow + row) % SeqLen;
                    int srcOff = srcRow * FeatureDim;
                    int dstOff = row * FeatureDim;
                    for (int j = 0; j < FeatureDim; j++) snap[dstOff + j] = r._slots[srcOff + j];
                }
            }

            int label = ClassifyHeuristic(snap);
            if (label < 0) return false;

            ev = new IntentEvent(selfTech, snap, label);
            return true;
        }

        /// <summary>Legacy single-arg overload — labeler ignores the anchor under the
        /// Phase-3 layout (Distance slot supplies closing rate directly).</summary>
        public bool TryBuildEvent(TechId selfTech, out IntentEvent ev)
            => TryBuildEvent(selfTech, Vector3.zero, out ev);

        // P11 T2 Item 50 heuristic thresholds. Provisional — tune during spawn-test.
        private const float IdleSpeedThreshold = 0.3f;       // m/s
        private const float HoldingSpeedThreshold = 1.0f;
        private const float AggressingClosingThreshold = 5.0f; // m closure across the seq window
        // Max per-row heading change (rad) — proxy for "yaw rate" when a real ang-vel
        // slot isn't present in IntentSlots. atan2(sin,cos) deltas are bounded in [-π,π];
        // a ~1.0 rad/row jump across 30 rows reads as a clear flanking arc.
        private const float FlankingHeadingRateThreshold = 1.0f;

        private static int ClassifyHeuristic(float[] snap)
        {
            // Mean target speed across the seq window — IntentSlots.TargetSpeed is the
            // absolute speed of the K-nearest hostile (m/s), pre-baked by the daemon.
            float meanTargetSpeed = 0f;
            float prevHeadingSin = snap[IntentSlots.RelHeadingSin];
            float prevHeadingCos = snap[IntentSlots.RelHeadingCos];
            float maxHeadingRate = 0f;
            for (int row = 0; row < SeqLen; row++)
            {
                int o = row * FeatureDim;
                meanTargetSpeed += snap[o + IntentSlots.TargetSpeed];

                // Heading-change-rate proxy for "angular velocity" — wrap delta into
                // [-π,π] via atan2 of the rotation difference. Skip row 0.
                if (row > 0)
                {
                    float s = snap[o + IntentSlots.RelHeadingSin];
                    float c = snap[o + IntentSlots.RelHeadingCos];
                    // delta = current - previous; sin/cos pair encodes a unit vector;
                    // dot/cross of the two pairs gives the signed angle between them.
                    float dotH = c * prevHeadingCos + s * prevHeadingSin;
                    float crossH = s * prevHeadingCos - c * prevHeadingSin;
                    float dAng = Mathf.Abs(Mathf.Atan2(crossH, dotH));
                    if (dAng > maxHeadingRate) maxHeadingRate = dAng;
                    prevHeadingSin = s;
                    prevHeadingCos = c;
                }
            }
            meanTargetSpeed /= SeqLen;

            // Closing rate: pre-baked Distance slot read at row 0 vs row SeqLen-1.
            // Positive = distance shrank → target is closing on self → Aggressing.
            int lastOff = (SeqLen - 1) * FeatureDim;
            float distFirst = snap[0 + IntentSlots.Distance];
            float distLast = snap[lastOff + IntentSlots.Distance];
            float closingRate = distFirst - distLast;

            // Sight gating: if the target was Lost for the whole window, the distance
            // signal is junk (extractor zeroes out kinematic slots when no hostile).
            // TargetSightCode is 1=Fresh, 0.5=Coasting, 0.25=Stale, 0=Lost; require
            // at least the newest sample to be non-Lost.
            float lastSight = snap[lastOff + IntentSlots.TargetSightCode];
            if (lastSight <= 0f) return IntentCategories.Idle;

            if (meanTargetSpeed < IdleSpeedThreshold) return IntentCategories.Idle;
            if (meanTargetSpeed < HoldingSpeedThreshold) return IntentCategories.Holding;
            if (closingRate > AggressingClosingThreshold) return IntentCategories.Aggressing;
            if (closingRate < -AggressingClosingThreshold) return IntentCategories.Retreating;
            if (maxHeadingRate > FlankingHeadingRateThreshold) return IntentCategories.Flanking;
            return IntentCategories.Repositioning;
        }

        /// <summary>
        /// P11 T2 Item 50: periodic drain. Walks every target ring; for each ring that's
        /// full, runs <see cref="TryBuildEvent"/> and enqueues the resulting IntentEvent
        /// into <paramref name="classifierQueue"/>. Called from <c>LearningService</c>
        /// periodic tick at low cadence (~1 Hz) so the classifier always has fresh events
        /// to train on without overwhelming the queue.
        /// </summary>
        public int DrainAndEnqueue(Threading.BoundedQueue<IntentEvent> classifierQueue,
            Vector3 ownAnchorWorld, int maxDrainsPerCall = 64)
        {
            if (classifierQueue == null) return 0;
            int drained = 0;
            foreach (var kv in _byTech)
            {
                if (drained >= maxDrainsPerCall) break;
                IntentEvent ev;
                if (TryBuildEvent(kv.Key, ownAnchorWorld, out ev))
                {
                    classifierQueue.Enqueue(ev);
                    drained++;
                }
            }
            return drained;
        }

        /// <summary>Drop the per-target ring (despawn / Forget).</summary>
        public void Deregister(TechId id) { Ring _; _byTech.TryRemove(id, out _); }

        public void Clear() => _byTech.Clear();

        public int TargetCount => _byTech.Count;
    }
}
