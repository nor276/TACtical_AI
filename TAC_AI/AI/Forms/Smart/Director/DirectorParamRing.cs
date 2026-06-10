using System;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// One captured model snapshot. paramsCopy / adamM / adamV are caller-owned
    /// pre-allocated buffers refilled via StoreParameters / ReadAdamMoments INTO them
    /// under model.SaveMutex — never .Clone() per capture, so the ring causes no GC
    /// churn. Atomicity invariant: weights and Adam moments are read inside one
    /// lock(model.SaveMutex) so they can't straddle a TrainOneMinibatch Adam step.
    /// </summary>
    public sealed class ParamSnapshot
    {
        public byte ArchitectureVersion;
        public string ModelName;
        public readonly float[] paramsCopy;
        public readonly float[] adamM;
        public readonly float[] adamV;
        public long adamT;
        public float lr;
        public float baseLr;
        public long monoTick;
        public long wallclockUtcSec;
        public float intentTemp;
        public readonly float[] lrScales;
        public readonly float[] outcomeWeights;
        public bool frozen;
        public long frozenUntilMono;
        // True once this slot holds a real capture (not an empty pre-allocation).
        public bool Populated;

        public ParamSnapshot(int paramCount, int lrScaleCount, int outcomeCount)
        {
            paramsCopy = new float[paramCount];
            adamM = new float[paramCount];
            adamV = new float[paramCount];
            lrScales = new float[lrScaleCount];
            outcomeWeights = new float[outcomeCount];
        }
    }

    /// <summary>
    /// T1 in-memory rollback ring (§7.2 / §7.4). Per model: 3 healthy slots + 8
    /// pre-action slots (30-min TTL). Slots are pre-allocated float[] buffers
    /// (ParameterCount per model) so capture is GC-free. ~1 MB total at 4 models.
    /// Cleared on world reset.
    /// </summary>
    public static class DirectorParamRing
    {
        public const int HealthySlots = 3;
        public const int PreActionSlots = 8;
        public const int SlotsPerModel = HealthySlots + PreActionSlots; // 11
        public const int ModelCount = 4;
        public const double PreActionTtlSeconds = 30.0 * 60.0;

        // [modelId][slot]. Slots 0..2 = healthy ring; 3..10 = pre-action ring.
        private static readonly ParamSnapshot[][] _slots = new ParamSnapshot[ModelCount][];
        private static readonly object _gate = new object();
        // Per-model healthy ring cursor (round-robin evict-oldest).
        private static readonly int[] _healthyCursor = new int[ModelCount];
        // Per-model pre-action ring cursor.
        private static readonly int[] _preActionCursor = new int[ModelCount];
        private static bool _allocated;

        private static ILearnedModel ModelAt(int modelId)
        {
            switch ((ModelId)modelId)
            {
                case ModelId.Intent: return LearningService.Intent;
                case ModelId.ActionValue: return LearningService.ActionValue;
                case ModelId.TrajectoryResidual: return LearningService.Residual;
                case ModelId.ThreatAssessment: return LearningService.Threat;
            }
            return null;
        }

        private static void EnsureAllocated()
        {
            if (_allocated) return;
            int outcomeCount = DirectorTuning.OutcomeWeights.Length;
            for (int m = 0; m < ModelCount; m++)
            {
                var model = ModelAt(m);
                int pc = model != null ? model.ParameterCount : 1;
                _slots[m] = new ParamSnapshot[SlotsPerModel];
                for (int s = 0; s < SlotsPerModel; s++)
                    _slots[m][s] = new ParamSnapshot(pc, 4, outcomeCount);
            }
            _allocated = true;
        }

        /// <summary>
        /// Capture a trio of all 4 model snapshots into the healthy ring (oldest evicted).
        /// tau is read ONCE at the top so all 4 snapshots agree on intent temperature.
        /// </summary>
        public static void CaptureHealthy()
        {
            Capture(preAction: false, label: null);
        }

        /// <summary>
        /// Capture into the pre-action ring before a destructive verb (30-min TTL).
        /// </summary>
        public static void CapturePreAction(string label)
        {
            Capture(preAction: true, label: label);
        }

        private static void Capture(bool preAction, string label)
        {
            lock (_gate)
            {
                EnsureAllocated();

                // tau + tuning snapshot captured once for the whole trio.
                float capturedTau = Volatile.Read(ref LearningTuning.IntentTemperature);
                long capWall = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                for (int m = 0; m < ModelCount; m++)
                {
                    var model = ModelAt(m);
                    if (model == null) continue;

                    int slotIdx;
                    if (preAction)
                    {
                        slotIdx = HealthySlots + _preActionCursor[m];
                        _preActionCursor[m] = (_preActionCursor[m] + 1) % PreActionSlots;
                    }
                    else
                    {
                        slotIdx = _healthyCursor[m];
                        _healthyCursor[m] = (_healthyCursor[m] + 1) % HealthySlots;
                    }

                    ParamSnapshot snap = _slots[m][slotIdx];
                    if (snap.paramsCopy.Length != model.ParameterCount)
                    {
                        // Arch grew/shrank under us — re-alloc this slot at the new size.
                        snap = new ParamSnapshot(model.ParameterCount, 4, DirectorTuning.OutcomeWeights.Length);
                        _slots[m][slotIdx] = snap;
                    }

                    lock (model.SaveMutex)
                    {
                        snap.ArchitectureVersion = model.ArchitectureVersion;
                        model.StoreParameters(snap.paramsCopy);
                        model.ReadAdamMoments(snap.adamM, snap.adamV, out snap.adamT, out snap.lr, out snap.baseLr);
                        long frozenUntil = model.FrozenUntilTickMono;
                        snap.frozenUntilMono = frozenUntil;
                        snap.frozen = MonoClock.Now() < frozenUntil;
                        snap.monoTick = MonoClock.Now();
                        snap.wallclockUtcSec = capWall;
                    }

                    snap.ModelName = model.Id.ToString();
                    snap.intentTemp = capturedTau;
                    for (int i = 0; i < snap.lrScales.Length; i++)
                        snap.lrScales[i] = DirectorTuning.GetLrScale(i);
                    for (int i = 0; i < snap.outcomeWeights.Length; i++)
                        snap.outcomeWeights[i] = DirectorTuning.GetOutcomeWeight(i);
                    snap.Populated = true;
                }

                DirectorJournal.Append(preAction ? "PARAMRING-PRE-ACTION" : "PARAMRING-HEALTHY",
                    "label=" + (label ?? (preAction ? "pre" : "healthy"))
                    + " tau=" + capturedTau.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Most-recent populated healthy snapshot for a model. Null when the ring is cold.
        /// </summary>
        public static ParamSnapshot LatestHealthy(int modelId)
        {
            if ((uint)modelId >= ModelCount) return null;
            lock (_gate)
            {
                if (!_allocated) return null;
                var ring = _slots[modelId];
                // Walk back from the cursor (newest-1) to find the freshest populated slot.
                for (int back = 0; back < HealthySlots; back++)
                {
                    int idx = ((_healthyCursor[modelId] - 1 - back) % HealthySlots + HealthySlots) % HealthySlots;
                    if (ring[idx] != null && ring[idx].Populated) return ring[idx];
                }
                return null;
            }
        }

        /// <summary>True when any model has a populated healthy snapshot. Digest read.</summary>
        public static bool HasHealthy()
        {
            for (int m = 0; m < ModelCount; m++)
                if (LatestHealthy(m) != null) return true;
            return false;
        }

        /// <summary>
        /// Most-recent populated pre-action snapshot still within its 30-min TTL, else null.
        /// </summary>
        public static ParamSnapshot LatestPreAction(int modelId)
        {
            if ((uint)modelId >= ModelCount) return null;
            lock (_gate)
            {
                if (!_allocated) return null;
                long now = MonoClock.Now();
                long ttlTicks = (long)(PreActionTtlSeconds / MonoClock.TickFreq);
                var ring = _slots[modelId];
                for (int back = 0; back < PreActionSlots; back++)
                {
                    int idx = ((_preActionCursor[modelId] - 1 - back) % PreActionSlots + PreActionSlots) % PreActionSlots;
                    var snap = ring[HealthySlots + idx];
                    if (snap != null && snap.Populated && (now - snap.monoTick) <= ttlTicks) return snap;
                }
                return null;
            }
        }

        /// <summary>Drop expired pre-action slots (called from the Director tick).</summary>
        public static void SweepExpiredPreAction()
        {
            lock (_gate)
            {
                if (!_allocated) return;
                long now = MonoClock.Now();
                long ttlTicks = (long)(PreActionTtlSeconds / MonoClock.TickFreq);
                for (int m = 0; m < ModelCount; m++)
                {
                    var ring = _slots[m];
                    if (ring == null) continue;
                    for (int s = HealthySlots; s < SlotsPerModel; s++)
                    {
                        var snap = ring[s];
                        if (snap != null && snap.Populated && (now - snap.monoTick) > ttlTicks)
                            snap.Populated = false;
                    }
                }
            }
        }

        /// <summary>Clear the entire ring on world reset.</summary>
        public static void Clear()
        {
            lock (_gate)
            {
                for (int m = 0; m < ModelCount; m++)
                {
                    var ring = _slots[m];
                    if (ring == null) continue;
                    for (int s = 0; s < ring.Length; s++)
                        if (ring[s] != null) ring[s].Populated = false;
                    _healthyCursor[m] = 0;
                    _preActionCursor[m] = 0;
                }
            }
        }
    }
}
