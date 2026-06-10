using System;
using System.Collections.Concurrent;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    // 10-class goal label for Q-learning action 'a'. Per FEATURE-EXPANSION-PLAN §7.2 + R2-06:
    // ContinuousController arbitrates over TacticalGoal via 3-tier precedence (external /
    // identity / tactical) — NOT PlanLibrary.PlanType (which lives in team Coordination).
    // The class is the cross-product of precedence-source × goal-kind, collapsed to ten
    // discrete buckets matching AV[80..89] one-hot. ContinuousController.cs owns the
    // selection-time mapping table; this enum is the wire-format the producer encodes.
    public enum ActionGoalClass : byte
    {
        External_AtCurrent  = 0,
        External_Move       = 1,
        External_Attack     = 2,
        Identity_Generic    = 3,
        Identity_Hunter     = 4,
        Identity_Base       = 5,
        Identity_Gatherer   = 6,
        Identity_Air        = 7,
        Identity_Support    = 8,
        Tactical_Fallback   = 9,
    }

    /// <summary>
    /// Training event for Q-learning. Captures (state s, action a, reward r, next-state s').
    /// Submitted by ContinuousController at the decision boundary; one-tick deferred enqueue
    /// once s' is known. Per FEATURE-EXPANSION-PLAN §7.2 + LEARNING-CONTRACT §3.2 + §4.1.
    /// </summary>
    public readonly struct ActionValueEvent
    {
        public readonly float[] State;       // length StateDim = 128 (AV slice of StrategicStateVector)
        public readonly int Action;          // ActionGoalClass cast — [0, 10)
        public readonly float Reward;        // (selfHpDelta − targetHpDelta) / elapsedSec
        public readonly float[] NextState;   // length StateDim
        public readonly float Gamma;         // discount factor (caller supplies)

        public ActionValueEvent(float[] s, int a, float r, float[] sp, float gamma)
        {
            State = s; Action = a; Reward = r; NextState = sp; Gamma = gamma;
        }
    }

    /// <summary>
    /// Q-value estimator: MLP 2×128. Per FEATURE-EXPANSION-PLAN §7.2 + LEARNING-CONTRACT §3.2.
    /// Input is the 128-dim ActionValue slice of StrategicStateVector (40 self + 40 target +
    /// 10 action one-hot + 38 reserved).
    ///
    /// Architecture:
    ///   input(128) -> Dense(128->128) -> ReLU -> Dense(128->128) -> ReLU -> Dense(128->1) -> Q
    /// Forward + backward + Adam fully implemented. TD-error training.
    /// </summary>
    public sealed class ActionValueEstimator : ILearnedModel
    {
        public const int StateDim = 128;
        public const int H1 = 128;
        public const int H2 = 128;
        public const int MinibatchSize = 32;
        public const int EventQueueCapacity = 1024;
        public const float DefaultLearningRate = 0.001f;

        // Reward window — §7.2 says 0.5s lookback against HealthSidecar.GetAt.
        public const double RewardLookbackSeconds = 0.5;

        public ModelId Id => ModelId.ActionValue;
        public byte ArchitectureVersion => 3;
        public int ParameterCount => _params.Length;

        // Parameter layout (single flat array for serialization):
        //   W1 [H1 x StateDim] + b1 [H1] + W2 [H2 x H1] + b2 [H2] + W3 [1 x H2] + b3 [1]
        private readonly float[] _params;
        private readonly AdamState _adam;
        private readonly DoubleBuffer<float[]> _publishedParams;
        private readonly BoundedQueue<ActionValueEvent> _events;
        // L-013: per-model save mutex; TrainerWorker locks around TrainOneMinibatch.
        private readonly object _saveMutex = new object();
        public object SaveMutex => _saveMutex;

        // Director freeze ticket. Plain long with Interlocked accessors —
        // volatile long is illegal CS0677.
        private long _frozenUntilTickMono;
        public bool Frozen
        {
            get { return System.Threading.Interlocked.Read(ref _frozenUntilTickMono) > TAC_AI.AI.Forms.Smart.World.MonoClock.Now(); }
        }
        public long FrozenUntilTickMono
        {
            get { return System.Threading.Interlocked.Read(ref _frozenUntilTickMono); }
            set { System.Threading.Interlocked.Exchange(ref _frozenUntilTickMono, value); }
        }
        public void LoadAdamState(AdamSnapshot snap)
        {
            if (snap.M == null || snap.V == null) return;
            lock (_saveMutex)
            {
                if (snap.M.Length == _adam.M.Length) Array.Copy(snap.M, _adam.M, snap.M.Length);
                if (snap.V.Length == _adam.V.Length) Array.Copy(snap.V, _adam.V, snap.V.Length);
                _adam.T = snap.T;
                _adam.LearningRate = snap.LR;
            }
        }
        public AdamSnapshot GetAdamSnapshot()
        {
            lock (_saveMutex)
            {
                return new AdamSnapshot(
                    (float[])_adam.M.Clone(),
                    (float[])_adam.V.Clone(),
                    _adam.T, _adam.LearningRate, _adam.BaseLearningRate);
            }
        }
        public void ReadAdamMoments(float[] mDest, float[] vDest, out long T, out float lr, out float baseLr)
        {
            lock (_saveMutex)
            {
                if (mDest != null && mDest.Length >= _adam.M.Length) Array.Copy(_adam.M, mDest, _adam.M.Length);
                if (vDest != null && vDest.Length >= _adam.V.Length) Array.Copy(_adam.V, vDest, _adam.V.Length);
                T = _adam.T;
                lr = _adam.LearningRate;
                baseLr = _adam.BaseLearningRate;
            }
        }
        public void DrainOneMinibatchDiscard()
        {
            ActionValueEvent _;
            for (int i = 0; i < MinibatchSize; i++) { if (!_events.TryDequeue(out _)) break; }
        }

        // Rolling EWMA of |reward| — TrainerWorker reads this to normalise the
        // actionvalue_loss_variance_ratio reservoir push. Updated inside TrainOneMinibatch
        // under SaveMutex (the trainer thread already holds it).
        private float _ewmaAbsReward;
        public float EwmaAbsReward { get { return _ewmaAbsReward; } }
        private const float EwmaAlpha = 0.05f;

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

            // Accumulate gradients across the minibatch + roll the EWMA of |reward|.
            var grad = new float[_totalParams];
            float lossBefore = 0f;
            float absRewardMean = 0f;
            for (int i = 0; i < n; i++)
            {
                lossBefore += SingleStepGradient(batch[i], grad);
                float r = batch[i].Reward;
                if (!float.IsNaN(r) && !float.IsInfinity(r)) absRewardMean += r >= 0f ? r : -r;
            }
            absRewardMean /= n;
            _ewmaAbsReward = (1f - EwmaAlpha) * _ewmaAbsReward + EwmaAlpha * absRewardMean;
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
        /// Single-event squared-TD-error gradient. Computes target = r + γ * max_a' Q(s', a')
        /// by evaluating Q on s' across all <see cref="ActionOneHotCount"/> candidate
        /// action one-hots (10 forward passes per training step), then back-propagates
        /// δ = Q(s,a) - target through the MLP. Accumulates into <paramref name="gradAccum"/>.
        /// Per FEATURE-EXPANSION-PLAN §7.2 — Q-learning, not SARSA.
        /// </summary>
        private float SingleStepGradient(ActionValueEvent ev, float[] gradAccum)
        {
            // ActionValue slot layout (StrategicStateVector ActionValueSlots): the
            // event's State[] is the AV slice already, so the one-hot window is at
            // [ActionOneHotBase .. ActionOneHotBase+ActionOneHotCount). For the max-Q
            // target we evaluate the next state once per candidate one-hot and keep
            // the largest output.
            float qSA = ForwardCached(ev.State, out var pre1, out var post1, out var pre2, out var post2);
            float maxQNext = MaxQOverActions(ev.NextState);
            float target = ev.Reward + ev.Gamma * maxQNext;
            float delta = qSA - target;

            // Backprop dL/dQ = δ (squared error gradient).
            BackprogateMlp(delta, ev.State, pre1, post1, pre2, post2, gradAccum);

            return 0.5f * delta * delta;
        }

        private float ComputeLoss(ActionValueEvent ev)
        {
            float qSA = Evaluate(ev.State);
            float target = ev.Reward + ev.Gamma * MaxQOverActions(ev.NextState);
            float delta = qSA - target;
            return 0.5f * delta * delta;
        }

        // Q-learning target: max_a' Q(s', a'). Rewrites the one-hot window in a temp
        // copy of s' across all 10 candidate actions and returns the maximum output.
        // 10 forward passes per training step per event — acceptable at the 32-event
        // minibatch / ~30Hz training cadence (~9600 forward passes/sec; the cached
        // matmul path is ~1us per call so ~10ms/sec worst case).
        private float MaxQOverActions(float[] sPrime)
        {
            int oneHotBase = Features.ActionValueSlots.ActionOneHotBase;
            int oneHotCount = Features.ActionValueSlots.ActionOneHotCount;
            var probe = new float[sPrime.Length];
            Array.Copy(sPrime, probe, sPrime.Length);
            // Clear the one-hot window — the event's NextState carries the action
            // selected at s' time; for max_a' we evaluate each candidate fresh.
            for (int i = 0; i < oneHotCount; i++) probe[oneHotBase + i] = 0f;
            float maxQ = float.NegativeInfinity;
            for (int a = 0; a < oneHotCount; a++)
            {
                probe[oneHotBase + a] = 1f;
                float q = Evaluate(probe);
                if (q > maxQ) maxQ = q;
                probe[oneHotBase + a] = 0f;
            }
            return maxQ;
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

        // ---- Reward helper (§7.2) ----
        // r = (self.HpFractionDelta - target.HpFractionDelta) / elapsedSec
        // Lookback = 0.5s via HealthSidecar.GetAt(id, now - 0.5s-in-mono-ticks). The
        // producer (Wiring phase, ContinuousController) calls this at the decision
        // boundary; missing samples (tech just spawned, target absent) collapse to the
        // current frame so the formula degrades to zero-delta / safe-finite rather than
        // throwing or returning NaN. elapsedSec floors at a small epsilon to avoid divide-
        // by-zero on the first sample.
        public static float ComputeReward(
            HealthSidecar health,
            TechId self,
            TechId target,
            bool hasTarget,
            long nowMono)
        {
            if (health == null) return 0f;
            long lookbackTicks = (long)(RewardLookbackSeconds / MonoClock.TickFreq);
            long cutoff = nowMono - lookbackTicks;

            float selfNow = health.Get(self);
            float selfThen;
            if (!health.GetAt(self, cutoff, out selfThen)) selfThen = selfNow;
            float selfDelta = selfNow - selfThen;

            float targetDelta = 0f;
            if (hasTarget)
            {
                float tgtNow = health.Get(target);
                float tgtThen;
                if (!health.GetAt(target, cutoff, out tgtThen)) tgtThen = tgtNow;
                targetDelta = tgtNow - tgtThen;
            }

            float elapsedSec = MonoClock.Seconds(cutoff, nowMono);
            if (elapsedSec < 1e-3f) elapsedSec = 1e-3f;

            float r = (selfDelta - targetDelta) / elapsedSec;
            if (float.IsNaN(r) || float.IsInfinity(r)) return 0f;
            return r;
        }

        // ---- Serialization ----
        /// <summary>L-013: see ThreatAssessmentModel.FlushPendingForPersist.</summary>
        public int FlushPendingForPersist()
        {
            lock (_saveMutex)
            {
                var result = TrainOneMinibatch();
                return result.BatchSize;
            }
        }

        public void StoreParameters(float[] dest)
        {
            Array.Copy(_params, dest, _params.Length);
        }
        public void LoadParameters(float[] src)
        {
            if (src == null || src.Length != _params.Length)
                throw new ArgumentException("ActionValueEstimator: parameter length mismatch.");
            // Lock so a Director rollback / SnapshotManager restore can't race the
            // trainer's _publishedParams.Write mid-copy.
            lock (_saveMutex)
            {
                Array.Copy(src, _params, _params.Length);
                _publishedParams.Write(CloneFloats(_params));
            }
        }

        /// <summary>P11 T3 Item 53: real Glorot reset — 3-weight + 3-bias MLP.</summary>
        public void Reset(int seed)
        {
            MlpUtil.ResetSeed(seed ^ (int)Id);
            MlpUtil.GlorotInit(_params, _w1Offset, StateDim, H1);
            MlpUtil.ZeroFill(_params, _b1Offset, H1);
            MlpUtil.GlorotInit(_params, _w2Offset, H1, H2);
            MlpUtil.ZeroFill(_params, _b2Offset, H2);
            MlpUtil.GlorotInit(_params, _w3Offset, H2, 1);
            MlpUtil.ZeroFill(_params, _b3Offset, 1);
            _adam.Reset();
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
