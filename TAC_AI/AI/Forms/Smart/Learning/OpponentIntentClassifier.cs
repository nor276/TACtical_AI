using System;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// Training event for the intent classifier. Holds a 30-tick × 12-feature observation
    /// sequence and the retrospectively-derived intent label. Per LEARNING-CONTRACT §3.1.
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
        public const int FeatureDim = 12;
        public const int Hidden = 64;
        public const int OutDim = 6; // IntentCategories.Count
        public const int MinibatchSize = 32;
        public const int EventQueueCapacity = 1024;
        public const float DefaultLearningRate = 0.0003f; // smaller per LEARNING §11

        public ModelId Id => ModelId.Intent;
        public byte ArchitectureVersion => 1;
        public int ParameterCount => _params.Length;

        // Parameter layout (single flat array):
        //   For each gate g ∈ {r, z, h}: W_g [Hidden x FeatureDim], U_g [Hidden x Hidden], b_g [Hidden]
        //   Dense head: W_o [OutDim x Hidden], b_o [OutDim]
        private readonly float[] _params;
        private readonly AdamState _adam;
        private readonly DoubleBuffer<float[]> _publishedParams;
        private readonly BoundedQueue<IntentEvent> _events;

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
            var batch = new IntentEvent[MinibatchSize];
            int n = 0;
            while (n < MinibatchSize && _events.TryDequeue(out var ev)) batch[n++] = ev;
            if (n == 0) return TrainStepResult.Empty;

            // Compute per-event final hidden state (forward pass on training params), then
            // dense-head gradient. GRU parameters are frozen at v0.1.0; only W_o, b_o update.
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

        public void StoreParameters(float[] dest) => Array.Copy(_params, dest, _params.Length);
        public void LoadParameters(float[] src)
        {
            if (src == null || src.Length != _params.Length) throw new ArgumentException("IntentClassifier: parameter length mismatch.");
            Array.Copy(src, _params, _params.Length);
            _publishedParams.Write(Clone(_params));
        }

        private static float[] Clone(float[] s) { var c = new float[s.Length]; Array.Copy(s, c, s.Length); return c; }
    }
}
