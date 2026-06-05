using System;
using System.Collections.Concurrent;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P7 Item 16: per-target observation-sequence buffer feeding the intent classifier.
    ///
    /// Maintains a per-TechId ring of <see cref="SeqLen"/> rows × <see cref="FeatureDim"/>
    /// floats. When a target's ring is full and a label can be derived,
    /// <see cref="TryBuildEvent"/> produces an <see cref="IntentEvent"/> for the classifier's
    /// training queue.
    ///
    /// v0.2 label source: deferred — label inference (mapping recent feature trajectory →
    /// IntentCategory) is its own research task. <see cref="TryBuildEvent"/> currently
    /// returns false unconditionally so no training events fire from this path; the sequence
    /// data IS captured per-tick so a future label-inference pass can drain it. Recorded
    /// here as a v0.2 plan gap (see Implementation Gaps Log in V0.2-PLAN-REV7).
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
        public const int FeatureDim = OpponentIntentClassifier.FeatureDim;  // 12

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
        /// <see cref="IntentEvent"/> for <paramref name="target"/> using the heuristic
        /// labeler below. Returns false when the ring isn't full OR when the heuristic
        /// can't assign a label confidently.
        ///
        /// The heuristic looks at the buffered sequence's kinematic statistics:
        /// speed mean, closing rate vs <paramref name="ownAnchorWorld"/>, and angular
        /// velocity magnitude. Heuristic labels:
        ///   speed mean &lt; <see cref="IdleSpeedThreshold"/> → Idle
        ///   speed mean &lt; <see cref="HoldingSpeedThreshold"/> → Holding
        ///   closing rate &gt; <see cref="AggressingClosingThreshold"/> → Aggressing
        ///   closing rate &lt; -<see cref="AggressingClosingThreshold"/> → Retreating
        ///   |angular velocity| max &gt; <see cref="FlankingAngVelThreshold"/> → Flanking
        ///   else → Repositioning
        ///
        /// Bootstrap supervision: the labels ARE coarse — a real classifier replaces this
        /// in v0.3+. The point is to feed the BPTT path real (sequence, label) tuples so
        /// the GRU+Dense head can learn the broad-stroke separation.
        /// </summary>
        public bool TryBuildEvent(TechId target, Vector3 ownAnchorWorld, out IntentEvent ev)
        {
            ev = default(IntentEvent);
            Ring r;
            if (!_byTech.TryGetValue(target, out r)) return false;

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

            int label = ClassifyHeuristic(snap, ownAnchorWorld);
            if (label < 0) return false;

            ev = new IntentEvent(target, snap, label);
            return true;
        }

        /// <summary>Legacy single-arg overload retained for back-compat — always returns false.</summary>
        public bool TryBuildEvent(TechId target, out IntentEvent ev)
            => TryBuildEvent(target, Vector3.zero, out ev);   // ownAnchor=0 → no closing rate; labeler skips

        // P11 T2 Item 50 heuristic thresholds. Provisional — tune during spawn-test.
        private const float IdleSpeedThreshold = 0.3f;       // m/s
        private const float HoldingSpeedThreshold = 1.0f;
        private const float AggressingClosingThreshold = 5.0f; // m closure across the seq window
        private const float FlankingAngVelThreshold = 1.0f;    // rad/s peak angular velocity

        private static int ClassifyHeuristic(float[] snap, Vector3 ownAnchorWorld)
        {
            float meanSpeed = 0f;
            float maxAng = 0f;
            for (int row = 0; row < SeqLen; row++)
            {
                int o = row * FeatureDim;
                // Row layout per SmartPerTechState.TickMainThread:
                // [0..2] PositionWorld xyz, [3..5] VelocityWorld xyz, [6..7] HeadingWorld xz,
                // [8..10] AngularVelocityWorld xyz, [11] LastObservationTick.
                float vx = snap[o + 3], vy = snap[o + 4], vz = snap[o + 5];
                meanSpeed += Mathf.Sqrt(vx * vx + vy * vy + vz * vz);
                float ax = snap[o + 8], ay = snap[o + 9], az = snap[o + 10];
                float am = Mathf.Sqrt(ax * ax + ay * ay + az * az);
                if (am > maxAng) maxAng = am;
            }
            meanSpeed /= SeqLen;

            // Closing rate: distance shrink from first row to last row. Only meaningful when
            // ownAnchorWorld is non-zero (caller supplied a real anchor).
            float closingRate = 0f;
            bool haveAnchor = ownAnchorWorld.sqrMagnitude > 1e-6f;
            if (haveAnchor)
            {
                Vector3 firstPos = new Vector3(snap[0], snap[1], snap[2]);
                int lastOff = (SeqLen - 1) * FeatureDim;
                Vector3 lastPos = new Vector3(snap[lastOff + 0], snap[lastOff + 1], snap[lastOff + 2]);
                float distFirst = (firstPos - ownAnchorWorld).magnitude;
                float distLast = (lastPos - ownAnchorWorld).magnitude;
                closingRate = distFirst - distLast;   // positive → closing on us
            }

            if (meanSpeed < IdleSpeedThreshold) return IntentCategories.Idle;
            if (meanSpeed < HoldingSpeedThreshold) return IntentCategories.Holding;
            if (haveAnchor && closingRate > AggressingClosingThreshold) return IntentCategories.Aggressing;
            if (haveAnchor && closingRate < -AggressingClosingThreshold) return IntentCategories.Retreating;
            if (maxAng > FlankingAngVelThreshold) return IntentCategories.Flanking;
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
