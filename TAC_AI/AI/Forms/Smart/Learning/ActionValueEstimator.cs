using System;
using System.Collections.Concurrent;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// Training event for Q-learning. Captures (state s, action a, reward r, next-state s').
    /// Submitted by LearningService on the main thread from plan-transition observations.
    /// Per LEARNING-CONTRACT §3.2 + §4.1.
    /// </summary>
    public readonly struct ActionValueEvent
    {
        public readonly float[] State;       // length StateDim
        public readonly int Action;          // plan index [0, 10)
        public readonly float Reward;        // immediate reward
        public readonly float[] NextState;   // length StateDim
        public readonly float Gamma;         // discount factor (caller supplies)

        public ActionValueEvent(float[] s, int a, float r, float[] sp, float gamma)
        {
            State = s; Action = a; Reward = r; NextState = sp; Gamma = gamma;
        }
    }

    /// <summary>
    /// Q-value estimator: MLP 2×64. Per LEARNING-CONTRACT §3.2.
    /// Input is the flattened strategic state + candidate-action features (size
    /// <see cref="StateDim"/> = 50).
    ///
    /// Architecture:
    ///   input(50) → Dense(50→64) → ReLU → Dense(64→64) → ReLU → Dense(64→1) → Q
    /// Forward + backward + Adam fully implemented. TD-error training.
    /// </summary>
    public sealed class ActionValueEstimator : ILearnedModel
    {
        public const int StateDim = 50;
        public const int H1 = 64;
        public const int H2 = 64;
        public const int MinibatchSize = 32;
        public const int EventQueueCapacity = 1024;
        public const float DefaultLearningRate = 0.001f;

        public ModelId Id => ModelId.ActionValue;
        public byte ArchitectureVersion => 1;
        public int ParameterCount => _params.Length;

        // Parameter layout (single flat array for serialization):
        //   W1 [H1 x StateDim] + b1 [H1] + W2 [H2 x H1] + b2 [H2] + W3 [1 x H2] + b3 [1]
        private readonly float[] _params;
        private readonly AdamState _adam;
        private readonly DoubleBuffer<float[]> _publishedParams;
        private readonly BoundedQueue<ActionValueEvent> _events;

        private readonly int _w1Offset, _b1Offset, _w2Offset, _b2Offset, _w3Offset, _b3Offset;
        private readonly int _totalParams;

        public BoundedQueue<ActionValueEvent> EventQueue => _events;

        public ActionValueEstimator()
        {
            _w1Offset = 0;
            _b1Offset = _w1Offset + H1 * StateDim;
            _w2Offset = _b1Offset + H1;
            _b2Offset = _w2Offset + H2 * H1;
            _w3Offset = _b2Offset + H2;
            _b3Offset = _w3Offset + 1 * H2;
            _totalParams = _b3Offset + 1;

            _params = new float[_totalParams];
            MlpUtil.GlorotInit(_params, _w1Offset, StateDim, H1);
            MlpUtil.GlorotInit(_params, _w2Offset, H1, H2);
            MlpUtil.GlorotInit(_params, _w3Offset, H2, 1);
            _adam = new AdamState(_totalParams, DefaultLearningRate);
            _publishedParams = new DoubleBuffer<float[]>(CloneFloats(_params));
            _events = new BoundedQueue<ActionValueEvent>(EventQueueCapacity, "Learning.ActionValueEvents");
        }

        // ---- Inference (any thread; reads published params) ----
        public float Evaluate(float[] state)
        {
            var p = _publishedParams.Read();
            var h1 = new float[H1];
            MlpUtil.MatMulAdd(p, _w1Offset, p, _b1Offset, state, h1, StateDim, H1);
            MlpUtil.Relu(h1, H1);
            var h2 = new float[H2];
            MlpUtil.MatMulAdd(p, _w2Offset, p, _b2Offset, h1, h2, H1, H2);
            MlpUtil.Relu(h2, H2);
            var y = new float[1];
            MlpUtil.MatMulAdd(p, _w3Offset, p, _b3Offset, h2, y, H2, 1);
            return y[0];
        }

        // ---- Training (worker thread) ----
        public TrainStepResult TrainOneMinibatch()
        {
            // Drain up to MinibatchSize events.
            var batch = new ActionValueEvent[MinibatchSize];
            int n = 0;
            while (n < MinibatchSize && _events.TryDequeue(out var ev)) batch[n++] = ev;
            if (n == 0) return TrainStepResult.Empty;

            // Accumulate gradients across the minibatch.
            var grad = new float[_totalParams];
            float lossBefore = 0f;
            for (int i = 0; i < n; i++)
            {
                lossBefore += SingleStepGradient(batch[i], grad);
            }
            // Average the gradient.
            float invN = 1f / n;
            for (int i = 0; i < _totalParams; i++) grad[i] *= invN;
            lossBefore /= n;

            _adam.Step(_params, grad);
            _publishedParams.Write(CloneFloats(_params));

            // Compute post-update loss on the same batch (diagnostic).
            float lossAfter = 0f;
            for (int i = 0; i < n; i++) lossAfter += ComputeLoss(batch[i]);
            lossAfter /= n;

            return new TrainStepResult(lossBefore, lossAfter, n);
        }

        /// <summary>
        /// Single-event squared-TD-error gradient. Computes target = r + γ * Q(s', a*)
        /// using a frozen pass of the model with current params, then back-propagates
        /// δ = Q(s,a) - target through the MLP. Accumulates into <paramref name="gradAccum"/>.
        /// Returns the per-event squared error.
        /// </summary>
        private float SingleStepGradient(ActionValueEvent ev, float[] gradAccum)
        {
            // For simplicity v0.1.0 uses single-output Q (not a Q(s,a) for each discrete a).
            // We embed the candidate action into the state vector (last 10 dims are one-hot
            // action + 9 dims parameters; see LEARNING-CONTRACT §3.2 input layout). The
            // target uses the same model on the next state with the SAME action — proper
            // max_a' Q(s', a') would require evaluating many candidate-action embeddings;
            // TODO v0.2 once the candidate action enumeration plumbing is in place.
            float qSA = ForwardCached(ev.State, out var pre1, out var post1, out var pre2, out var post2);
            float qSPrimeA = Evaluate(ev.NextState); // uses published params (stable target)
            float target = ev.Reward + ev.Gamma * qSPrimeA;
            float delta = qSA - target;

            // Backprop dL/dQ = δ (squared error gradient).
            BackprogateMlp(delta, ev.State, pre1, post1, pre2, post2, gradAccum);

            return 0.5f * delta * delta;
        }

        private float ComputeLoss(ActionValueEvent ev)
        {
            float qSA = Evaluate(ev.State);
            float target = ev.Reward + ev.Gamma * Evaluate(ev.NextState);
            float delta = qSA - target;
            return 0.5f * delta * delta;
        }

        /// <summary>
        /// Forward pass on training-side <see cref="_params"/> (not the published buffer).
        /// Returns Q and writes pre/post-activation buffers for backprop.
        /// </summary>
        private float ForwardCached(float[] x, out float[] pre1, out float[] post1, out float[] pre2, out float[] post2)
        {
            pre1 = new float[H1]; post1 = new float[H1];
            pre2 = new float[H2]; post2 = new float[H2];
            MlpUtil.MatMulAdd(_params, _w1Offset, _params, _b1Offset, x, pre1, StateDim, H1);
            Array.Copy(pre1, post1, H1);
            MlpUtil.Relu(post1, H1);
            MlpUtil.MatMulAdd(_params, _w2Offset, _params, _b2Offset, post1, pre2, H1, H2);
            Array.Copy(pre2, post2, H2);
            MlpUtil.Relu(post2, H2);
            var y = new float[1];
            MlpUtil.MatMulAdd(_params, _w3Offset, _params, _b3Offset, post2, y, H2, 1);
            return y[0];
        }

        /// <summary>
        /// Backprop the scalar output gradient <paramref name="dy"/> through the 3-layer MLP,
        /// accumulating into <paramref name="gradAccum"/>. Standard MLP chain rule.
        /// </summary>
        private void BackprogateMlp(float dy, float[] x, float[] pre1, float[] post1, float[] pre2, float[] post2, float[] gradAccum)
        {
            // Layer 3: W3 [1 x H2], b3 [1]. dL/dW3[o,i] = dy * post2[i]. dL/db3 = dy. dL/dpost2 = W3^T * dy.
            for (int i = 0; i < H2; i++) gradAccum[_w3Offset + i] += dy * post2[i];
            gradAccum[_b3Offset] += dy;

            var dPost2 = new float[H2];
            for (int i = 0; i < H2; i++) dPost2[i] = _params[_w3Offset + i] * dy;

            // ReLU derivative on layer 2.
            var dPre2 = new float[H2];
            for (int i = 0; i < H2; i++) dPre2[i] = pre2[i] > 0f ? dPost2[i] : 0f;

            // Layer 2: W2 [H2 x H1], b2 [H2].
            for (int o = 0; o < H2; o++)
            {
                int row = _w2Offset + o * H1;
                float d = dPre2[o];
                gradAccum[_b2Offset + o] += d;
                for (int i = 0; i < H1; i++) gradAccum[row + i] += d * post1[i];
            }

            var dPost1 = new float[H1];
            for (int i = 0; i < H1; i++)
            {
                float sum = 0f;
                for (int o = 0; o < H2; o++) sum += _params[_w2Offset + o * H1 + i] * dPre2[o];
                dPost1[i] = sum;
            }

            var dPre1 = new float[H1];
            for (int i = 0; i < H1; i++) dPre1[i] = pre1[i] > 0f ? dPost1[i] : 0f;

            // Layer 1: W1 [H1 x StateDim], b1 [H1].
            for (int o = 0; o < H1; o++)
            {
                int row = _w1Offset + o * StateDim;
                float d = dPre1[o];
                gradAccum[_b1Offset + o] += d;
                for (int i = 0; i < StateDim; i++) gradAccum[row + i] += d * x[i];
            }
        }

        // ---- Serialization ----
        public void StoreParameters(float[] dest)
        {
            Array.Copy(_params, dest, _params.Length);
        }
        public void LoadParameters(float[] src)
        {
            if (src == null || src.Length != _params.Length)
                throw new ArgumentException("ActionValueEstimator: parameter length mismatch.");
            Array.Copy(src, _params, _params.Length);
            _publishedParams.Write(CloneFloats(_params));
        }

        private static float[] CloneFloats(float[] src)
        {
            var copy = new float[src.Length];
            Array.Copy(src, copy, src.Length);
            return copy;
        }
    }
}
