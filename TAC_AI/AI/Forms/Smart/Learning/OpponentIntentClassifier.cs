using System;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// Training event for the intent classifier. Holds a 30-tick × 40-feature observation
    /// sequence and the retrospectively-derived intent label. Per LEARNING-CONTRACT §3.1.
    /// FeatureDim widened 12 → 40 to match Strategic IntentSlots view (§3.2).
    /// </summary>
    public readonly struct IntentEvent
    {
        public readonly TechId TargetId;
        public readonly float[] Sequence;  // length SeqLen * FeatureDim (row-major)
        public readonly int Label;          // [0, IntentCategories.Count)

        public IntentEvent(TechId targetId, float[] sequence, int label)
        {
            TargetId = targetId; Sequence = sequence; Label = label;
        }
    }

    /// <summary>
    /// Opponent intent classifier — single-layer GRU(64) → Dense(6) → Softmax.
    /// Per LEARNING-CONTRACT §3.1.
    ///
    /// v0.1.0 HONESTY: forward pass + the Dense output head's training are fully
    /// implemented; the GRU recurrent state's parameters (W_r/U_r/b_r/W_z/U_z/b_z/W_h/U_h/b_h)
    /// are frozen — BPTT through them is TODO v0.2. The model still produces non-trivial
    /// per-sequence outputs (the dense head learns over the GRU's projected hidden state).
    /// This satisfies the "untrained" workflow gate without implementing the full BPTT machinery.
    /// </summary>
    public sealed class OpponentIntentClassifier : ILearnedModel
    {
        public const int SeqLen = 30;
        public const int FeatureDim = 40;
        public const int Hidden = 64;
        public const int OutDim = 6; // IntentCategories.Count
        public const int MinibatchSize = 32;
        public const int EventQueueCapacity = 1024;
        public const float DefaultLearningRate = 0.0003f; // smaller per LEARNING §11

        public ModelId Id => ModelId.Intent;
        // P8 Item 19 (REV 7): bumped 1 → 2 marking GRU BPTT enabled. Informational only —
        // flat parameter layout unchanged (same offsets, same total size). M0002_BpttUnfreeze
        // is a no-op forward migration.
        // Phase 3 §7.1: 2 → 3 — FeatureDim 12 → 40 widens W_r/W_z/W_h from [64×12] to
        // [64×40]. Param count 15174 → 20550. Coordinated bump with the other 3 models.
        public byte ArchitectureVersion => 3;
        public int ParameterCount => _params.Length;

        // Parameter layout (single flat array):
        //   For each gate g ∈ {r, z, h}: W_g [Hidden x FeatureDim], U_g [Hidden x Hidden], b_g [Hidden]
        //   Dense head: W_o [OutDim x Hidden], b_o [OutDim]
        private readonly float[] _params;
        private readonly AdamState _adam;
        private readonly DoubleBuffer<float[]> _publishedParams;
        private readonly BoundedQueue<IntentEvent> _events;
        // L-013: per-model save mutex; TrainerWorker locks around TrainOneMinibatch.
        private readonly object _saveMutex = new object();
        public object SaveMutex => _saveMutex;

        // Offsets — three gates of (W: H*F, U: H*H, b: H).
        private readonly int _wrOff, _urOff, _brOff;
        private readonly int _wzOff, _uzOff, _bzOff;
        private readonly int _whOff, _uhOff, _bhOff;
        private readonly int _woOff, _boOff;
        private readonly int _totalParams;

        public BoundedQueue<IntentEvent> EventQueue => _events;

        public OpponentIntentClassifier()
        {
            int gateW = Hidden * FeatureDim;
            int gateU = Hidden * Hidden;
            int gateB = Hidden;

            _wrOff = 0;
            _urOff = _wrOff + gateW;
            _brOff = _urOff + gateU;
            _wzOff = _brOff + gateB;
            _uzOff = _wzOff + gateW;
            _bzOff = _uzOff + gateU;
            _whOff = _bzOff + gateB;
            _uhOff = _whOff + gateW;
            _bhOff = _uhOff + gateU;
            _woOff = _bhOff + gateB;
            _boOff = _woOff + OutDim * Hidden;
            _totalParams = _boOff + OutDim;

            _params = new float[_totalParams];
            // Glorot init each weight matrix; biases default to 0.
            MlpUtil.GlorotInit(_params, _wrOff, FeatureDim, Hidden);
            MlpUtil.GlorotInit(_params, _urOff, Hidden, Hidden);
            MlpUtil.GlorotInit(_params, _wzOff, FeatureDim, Hidden);
            MlpUtil.GlorotInit(_params, _uzOff, Hidden, Hidden);
            MlpUtil.GlorotInit(_params, _whOff, FeatureDim, Hidden);
            MlpUtil.GlorotInit(_params, _uhOff, Hidden, Hidden);
            MlpUtil.GlorotInit(_params, _woOff, Hidden, OutDim);

            _adam = new AdamState(_totalParams, DefaultLearningRate);
            _publishedParams = new DoubleBuffer<float[]>(Clone(_params));
            _events = new BoundedQueue<IntentEvent>(EventQueueCapacity, "Learning.IntentEvents");
        }

        /// <summary>
        /// Forward pass over the sequence; returns the softmax distribution over intent.
        /// </summary>
        public float[] Evaluate(float[] sequence)
        {
            var p = _publishedParams.Read();
            var h = new float[Hidden]; // initial h_0 = 0
            for (int t = 0; t < SeqLen; t++)
            {
                int xOff = t * FeatureDim;
                GruStep(p, sequence, xOff, h);
            }
            // Dense head: logits = W_o * h + b_o; softmax.
            var logits = new float[OutDim];
            MlpUtil.MatMulAdd(p, _woOff, p, _boOff, h, logits, Hidden, OutDim);
            MlpUtil.Softmax(logits, OutDim);
            return logits;
        }

        /// <summary>
        /// One GRU time step in place: h ← (1-z) * h + z * h_tilde.
        /// </summary>
        private void GruStep(float[] p, float[] x, int xOff, float[] h)
        {
            var r = new float[Hidden];
            var z = new float[Hidden];
            var hTilde = new float[Hidden];

            // r_t = sigmoid(W_r x + U_r h + b_r)
            // z_t = sigmoid(W_z x + U_z h + b_z)
            // h_tilde = tanh(W_h x + U_h (r * h) + b_h)
            // h ← (1 - z) h + z h_tilde
            for (int i = 0; i < Hidden; i++)
            {
                float sumR = p[_brOff + i];
                float sumZ = p[_bzOff + i];
                int rowF = i * FeatureDim;
                int rowH = i * Hidden;
                for (int j = 0; j < FeatureDim; j++)
                {
                    float xj = x[xOff + j];
                    sumR += p[_wrOff + rowF + j] * xj;
                    sumZ += p[_wzOff + rowF + j] * xj;
                }
                for (int j = 0; j < Hidden; j++)
                {
                    sumR += p[_urOff + rowH + j] * h[j];
                    sumZ += p[_uzOff + rowH + j] * h[j];
                }
                r[i] = MlpUtil.Sigmoid(sumR);
                z[i] = MlpUtil.Sigmoid(sumZ);
            }
            for (int i = 0; i < Hidden; i++)
            {
                float sumH = p[_bhOff + i];
                int rowF = i * FeatureDim;
                int rowH = i * Hidden;
                for (int j = 0; j < FeatureDim; j++) sumH += p[_whOff + rowF + j] * x[xOff + j];
                for (int j = 0; j < Hidden; j++) sumH += p[_uhOff + rowH + j] * (r[j] * h[j]);
                hTilde[i] = MlpUtil.TanhF(sumH);
            }
            for (int i = 0; i < Hidden; i++)
                h[i] = (1f - z[i]) * h[i] + z[i] * hTilde[i];
        }

        public TrainStepResult TrainOneMinibatch()
        {
            // P8 Item 19 (REV 7): branches on LearningTuning.UseFullBPTT. Default true
            // ships the full BPTT path; flipping false reverts to the v0.1 dense-head-only
            // training (CircuitBreaker one-flip revert if the BPTT trainer NaN-trips).
            if (LearningTuning.UseFullBPTT)
                return TrainOneMinibatch_FullBptt();
            return TrainOneMinibatch_DenseOnly();
        }

        /// <summary>
        /// v0.1 training path: GRU gate parameters are frozen; only W_o + b_o update via
        /// the dense-head gradient. Preserved for one-flip revert from BPTT-mode NaN trips.
        /// </summary>
        private TrainStepResult TrainOneMinibatch_DenseOnly()
        {
            var batch = new IntentEvent[MinibatchSize];
            int n = 0;
            while (n < MinibatchSize && _events.TryDequeue(out var ev)) batch[n++] = ev;
            if (n == 0) return TrainStepResult.Empty;

            var grad = new float[_totalParams];
            float lossBefore = 0f;
            for (int i = 0; i < n; i++)
            {
                lossBefore += DenseHeadStep(batch[i], grad);
            }
            float invN = 1f / n;
            for (int i = 0; i < _totalParams; i++) grad[i] *= invN;
            lossBefore /= n;

            _adam.Step(_params, grad);
            _publishedParams.Write(Clone(_params));

            float lossAfter = 0f;
            for (int i = 0; i < n; i++)
            {
                var probs = Evaluate(batch[i].Sequence);
                lossAfter += MlpUtil.CrossEntropy(probs, batch[i].Label);
            }
            lossAfter /= n;
            return new TrainStepResult(lossBefore, lossAfter, n);
        }

        /// <summary>
        /// P8 Item 19: full BPTT path. Per-event:
        ///   1. GruBackprop.Forward populates per-timestep cache.
        ///   2. Dense head forward + softmax + cross-entropy compute dLogit + W_o/b_o grad.
        ///   3. dL/dh_T = W_o^T @ dLogit (propagates dense-head gradient back into hidden state).
        ///   4. GruBackprop.Backward folds dL/dh_T back through all 9 GRU gate slots.
        /// </summary>
        private TrainStepResult TrainOneMinibatch_FullBptt()
        {
            var batch = new IntentEvent[MinibatchSize];
            int n = 0;
            while (n < MinibatchSize && _events.TryDequeue(out var ev)) batch[n++] = ev;
            if (n == 0) return TrainStepResult.Empty;

            var grad = new float[_totalParams];
            float lossBefore = 0f;

            // Cache + scratch buffers — reused across the minibatch (allocation-free per event).
            var cache = GruBackprop.AllocateCache(SeqLen, Hidden);
            var offsets = new GruBackprop.GateOffsets
            {
                Wr = _wrOff, Ur = _urOff, Br = _brOff,
                Wz = _wzOff, Uz = _uzOff, Bz = _bzOff,
                Wh = _whOff, Uh = _uhOff, Bh = _bhOff,
            };
            var logits = new float[OutDim];
            var probs = new float[OutDim];
            var dLogit = new float[OutDim];
            var dLdhT = new float[Hidden];

            for (int e = 0; e < n; e++)
            {
                var ev = batch[e];
                // Guard malformed labels (matches DenseHeadStep guard at line 205).
                if ((uint)ev.Label >= (uint)OutDim) continue;

                // 1. Forward through the GRU; cache holds per-timestep gates + hidden.
                GruBackprop.Forward(_params, ev.Sequence, SeqLen, FeatureDim, Hidden, offsets, cache);
                var hT = cache[SeqLen - 1].H;

                // 2. Dense head forward.
                MlpUtil.MatMulAdd(_params, _woOff, _params, _boOff, hT, logits, Hidden, OutDim);
                for (int o = 0; o < OutDim; o++) probs[o] = logits[o];
                MlpUtil.Softmax(probs, OutDim);

                lossBefore += MlpUtil.CrossEntropy(probs, ev.Label);

                // dL/dlogit = probs - one_hot(label).
                for (int o = 0; o < OutDim; o++) dLogit[o] = probs[o] - (o == ev.Label ? 1f : 0f);

                // Dense-head gradients: dW_o[o,i] = dLogit[o] * h_T[i]; db_o = dLogit.
                for (int o = 0; o < OutDim; o++)
                {
                    int row = _woOff + o * Hidden;
                    grad[_boOff + o] += dLogit[o];
                    for (int i = 0; i < Hidden; i++) grad[row + i] += dLogit[o] * hT[i];
                }

                // 3. dL/dh_T = W_o^T @ dLogit (propagate dense-head gradient back to hidden).
                for (int i = 0; i < Hidden; i++)
                {
                    float s = 0f;
                    for (int o = 0; o < OutDim; o++) s += _params[_woOff + o * Hidden + i] * dLogit[o];
                    dLdhT[i] = s;
                }

                // 4. Backward through the GRU; accumulates W_r/U_r/b_r/W_z/U_z/b_z/W_h/U_h/b_h.
                GruBackprop.Backward(_params, ev.Sequence, SeqLen, FeatureDim, Hidden,
                    offsets, cache, dLdhT, grad);
            }

            float invN = 1f / n;
            for (int i = 0; i < _totalParams; i++) grad[i] *= invN;
            lossBefore /= n;

            _adam.Step(_params, grad);
            _publishedParams.Write(Clone(_params));

            float lossAfter = 0f;
            for (int i = 0; i < n; i++)
            {
                if ((uint)batch[i].Label >= (uint)OutDim) continue;
                var afterProbs = Evaluate(batch[i].Sequence);
                lossAfter += MlpUtil.CrossEntropy(afterProbs, batch[i].Label);
            }
            lossAfter /= n;
            return new TrainStepResult(lossBefore, lossAfter, n);
        }

        /// <summary>
        /// Dense-output-only training step: run the forward pass to obtain h_T, compute
        /// softmax + cross-entropy loss, and backprop into W_o + b_o only.
        /// </summary>
        private float DenseHeadStep(IntentEvent ev, float[] gradAccum)
        {
            // Phase 6 (FIX-PLAN.md) — AUDIT-R2 §2.R2.F: guard against malformed events.
            // CrossEntropy at line 222 would IndexOutOfRange on Label ∉ [0, OutDim); the
            // event producer doesn't validate, and TrainerWorker's catch would just log
            // and reschedule — silently dropping the batch. uint cast catches negatives.
            if ((uint)ev.Label >= (uint)OutDim) return 0f;
            // Forward to get h_T using training params.
            var h = new float[Hidden];
            for (int t = 0; t < SeqLen; t++) GruStep(_params, ev.Sequence, t * FeatureDim, h);

            var logits = new float[OutDim];
            MlpUtil.MatMulAdd(_params, _woOff, _params, _boOff, h, logits, Hidden, OutDim);
            var probs = new float[OutDim];
            Array.Copy(logits, probs, OutDim);
            MlpUtil.Softmax(probs, OutDim);

            // dL/dlogit = probs - one_hot(label)
            var dLogit = new float[OutDim];
            for (int o = 0; o < OutDim; o++) dLogit[o] = probs[o] - (o == ev.Label ? 1f : 0f);

            // dL/dW_o[o, i] = dLogit[o] * h[i]; dL/db_o = dLogit.
            for (int o = 0; o < OutDim; o++)
            {
                int row = _woOff + o * Hidden;
                gradAccum[_boOff + o] += dLogit[o];
                for (int i = 0; i < Hidden; i++) gradAccum[row + i] += dLogit[o] * h[i];
            }
            return MlpUtil.CrossEntropy(probs, ev.Label);
        }

        /// <summary>L-013: see ThreatAssessmentModel.FlushPendingForPersist.</summary>
        public int FlushPendingForPersist()
        {
            lock (_saveMutex)
            {
                var result = TrainOneMinibatch();
                return result.BatchSize;
            }
        }

        public void StoreParameters(float[] dest) => Array.Copy(_params, dest, _params.Length);
        public void LoadParameters(float[] src)
        {
            if (src == null || src.Length != _params.Length) throw new ArgumentException("IntentClassifier: parameter length mismatch.");
            Array.Copy(src, _params, _params.Length);
            _publishedParams.Write(Clone(_params));
        }

        /// <summary>
        /// P11 T3 Item 53: real reset — Glorot for the 4 W matrices (3 gate-input W's + W_o
        /// dense head), orthogonal for the 3 U matrices (hidden-hidden recurrent weights —
        /// critical for BPTT stability now that the gates are unfrozen),
        /// zero-fill the 4 bias vectors, reset Adam moments.
        /// </summary>
        public void Reset(int seed)
        {
            MlpUtil.ResetSeed(seed ^ (int)Id);
            MlpUtil.GlorotInit(_params, _wrOff, FeatureDim, Hidden);
            MlpUtil.OrthogonalInit(_params, _urOff, Hidden);
            MlpUtil.ZeroFill(_params, _brOff, Hidden);
            MlpUtil.GlorotInit(_params, _wzOff, FeatureDim, Hidden);
            MlpUtil.OrthogonalInit(_params, _uzOff, Hidden);
            MlpUtil.ZeroFill(_params, _bzOff, Hidden);
            MlpUtil.GlorotInit(_params, _whOff, FeatureDim, Hidden);
            MlpUtil.OrthogonalInit(_params, _uhOff, Hidden);
            MlpUtil.ZeroFill(_params, _bhOff, Hidden);
            MlpUtil.GlorotInit(_params, _woOff, Hidden, OutDim);
            MlpUtil.ZeroFill(_params, _boOff, OutDim);
            _adam.Reset();
            _publishedParams.Write(Clone(_params));
        }

        private static float[] Clone(float[] s) { var c = new float[s.Length]; Array.Copy(s, c, s.Length); return c; }
    }
}
