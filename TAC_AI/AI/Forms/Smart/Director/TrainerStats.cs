using System;
using TAC_AI.AI.Forms.Smart.Learning;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// 12 reservoirs (Loss / ParamDelta / Entropy × 4 ModelIds). Lock-based ring +
    /// sort-on-read, mirroring PathRequestBackpressure._latencyReservoir at
    /// PathRequestBackpressure.cs:61 (field), :64 (lock), :152-161 (push), :163-178
    /// (Percentile snapshot-and-sort).
    ///
    /// Sizing (RM-10 LOCKED): LossReservoir=256, ParamDeltaReservoir=64. Entropy=256
    /// (matches Loss cadence; one push per minibatch).
    ///
    /// Host-process-only, never persisted, reset on host-loss.
    /// </summary>
    public static class TrainerStats
    {
        public const int LossReservoirCapacity = 256;
        public const int ParamDeltaReservoirCapacity = 64;
        public const int EntropyReservoirCapacity = 256;

        // One reservoir per (kind, model). ModelId cardinality = 4.
        private static readonly Reservoir[] _lossReservoirs = NewReservoirs(LossReservoirCapacity);
        private static readonly Reservoir[] _paramDeltaReservoirs = NewReservoirs(ParamDeltaReservoirCapacity);
        private static readonly Reservoir[] _entropyReservoirs = NewReservoirs(EntropyReservoirCapacity);

        private static Reservoir[] NewReservoirs(int cap)
        {
            return new Reservoir[]
            {
                new Reservoir(cap),
                new Reservoir(cap),
                new Reservoir(cap),
                new Reservoir(cap),
            };
        }

        /// <summary>
        /// Push a LossBefore sample, normalised by EWMA-of-|reward| for ActionValue (reward
        /// magnitude bias-out). Other models pass <paramref name="normReward"/>=0 and the
        /// raw LossBefore is pushed.
        /// </summary>
        public static void LossPush(ModelId id, float lossBefore, float lossAfter, int batchSize, float normReward)
        {
            int slot = (int)id;
            if ((uint)slot >= 4) return;
            if (float.IsNaN(lossBefore) || float.IsInfinity(lossBefore)) return;
            float sample = lossBefore;
            if (id == ModelId.ActionValue && normReward > 0.1f)
            {
                sample = lossBefore / normReward;
            }
            _lossReservoirs[slot].Push(sample);
        }

        /// <summary>
        /// Push architecture-invariant ‖Δθ‖₂ / sqrt(N_params). Caller computes the L2 on
        /// (paramAfter − paramBefore) outside any lock.
        /// </summary>
        public static void ParamDeltaPush(ModelId id, float archInvariantL2)
        {
            int slot = (int)id;
            if ((uint)slot >= 4) return;
            if (float.IsNaN(archInvariantL2) || float.IsInfinity(archInvariantL2)) return;
            _paramDeltaReservoirs[slot].Push(archInvariantL2);
        }

        /// <summary>
        /// Push normalised Shannon entropy (H / ln(OutDim)) for Intent. Caller has already
        /// guarded degenerate softmax (sum < 1e-12); we still NaN-clamp defensively.
        /// </summary>
        public static void EntropyPush(ModelId id, float normalisedEntropy)
        {
            int slot = (int)id;
            if ((uint)slot >= 4) return;
            if (float.IsNaN(normalisedEntropy) || float.IsInfinity(normalisedEntropy)) return;
            _entropyReservoirs[slot].Push(normalisedEntropy);
        }

        // ---- Read accessors used by ConstraintEngine ----
        public static int LossSampleCount(ModelId id)
        {
            int slot = (int)id;
            return (uint)slot >= 4 ? 0 : _lossReservoirs[slot].Count;
        }
        public static int ParamDeltaSampleCount(ModelId id)
        {
            int slot = (int)id;
            return (uint)slot >= 4 ? 0 : _paramDeltaReservoirs[slot].Count;
        }
        public static int EntropySampleCount(ModelId id)
        {
            int slot = (int)id;
            return (uint)slot >= 4 ? 0 : _entropyReservoirs[slot].Count;
        }

        public static float LossMean(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _lossReservoirs[slot].Mean(); }
        public static float LossVariance(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _lossReservoirs[slot].Variance(); }
        public static float LossP50(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _lossReservoirs[slot].Percentile(0.50f); }
        public static float LossP99(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _lossReservoirs[slot].Percentile(0.99f); }

        public static float ParamDeltaP50(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _paramDeltaReservoirs[slot].Percentile(0.50f); }
        public static float ParamDeltaP99(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _paramDeltaReservoirs[slot].Percentile(0.99f); }
        public static float ParamDeltaMean(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _paramDeltaReservoirs[slot].Mean(); }

        public static float EntropyMean(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _entropyReservoirs[slot].Mean(); }
        public static float EntropyP50(ModelId id) { int slot = (int)id; return (uint)slot >= 4 ? 0f : _entropyReservoirs[slot].Percentile(0.50f); }

        /// <summary>Reset all reservoirs. Called on host-loss.</summary>
        public static void ResetAll()
        {
            for (int i = 0; i < 4; i++)
            {
                _lossReservoirs[i].Reset();
                _paramDeltaReservoirs[i].Reset();
                _entropyReservoirs[i].Reset();
            }
        }

        // Lock-based ring + sort-on-read. Mirrors PathRequestBackpressure._latencyReservoir.
        private sealed class Reservoir
        {
            private readonly float[] _samples;
            private int _cursor;
            private int _count;
            private readonly object _lock = new object();
            private readonly int _capacity;

            public Reservoir(int capacity)
            {
                _capacity = capacity;
                _samples = new float[capacity];
            }

            public int Count
            {
                get { lock (_lock) { return _count; } }
            }

            public void Push(float sample)
            {
                lock (_lock)
                {
                    _samples[_cursor] = sample;
                    _cursor = (_cursor + 1) % _capacity;
                    if (_count < _capacity) _count++;
                }
            }

            public void Reset()
            {
                lock (_lock)
                {
                    _count = 0;
                    _cursor = 0;
                }
            }

            public float Mean()
            {
                lock (_lock)
                {
                    if (_count == 0) return 0f;
                    float sum = 0f;
                    for (int i = 0; i < _count; i++) sum += _samples[i];
                    return sum / _count;
                }
            }

            public float Variance()
            {
                float[] copy;
                int n;
                lock (_lock)
                {
                    n = _count;
                    if (n < 2) return 0f;
                    copy = new float[n];
                    Array.Copy(_samples, copy, n);
                }
                float mean = 0f;
                for (int i = 0; i < n; i++) mean += copy[i];
                mean /= n;
                float sumSq = 0f;
                for (int i = 0; i < n; i++) { float d = copy[i] - mean; sumSq += d * d; }
                return sumSq / (n - 1);
            }

            public float Percentile(float fraction)
            {
                float[] copy;
                int n;
                lock (_lock)
                {
                    n = _count;
                    if (n == 0) return 0f;
                    copy = new float[n];
                    Array.Copy(_samples, copy, n);
                }
                Array.Sort(copy);
                int idx = (int)(fraction * (n - 1));
                if (idx < 0) idx = 0;
                if (idx >= n) idx = n - 1;
                return copy[idx];
            }
        }
    }
}
