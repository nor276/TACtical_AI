using System;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// Training event for threat assessment: composition features → empirical threat
    /// derived from observed engagements. Per LEARNING-CONTRACT §3.4.
    /// </summary>
    public readonly struct ThreatEvent
    {
        public readonly TechId AttackerId;
        public readonly float[] Features;          // length FeatureDim
        public readonly float ObservedThreat;       // empirical D/T/max_dps in [0, 1]

        public ThreatEvent(TechId attackerId, float[] features, float observedThreat)
        {
            AttackerId = attackerId; Features = features; ObservedThreat = observedThreat;
        }
    }

    /// <summary>
    /// Threat assessment model: MLP 2×64 → scalar in roughly [0,1]. Per FEATURE-EXPANSION-PLAN §7.4 + §3.5.
    /// FeatureDim 25→48 (36 attacker/victim + 12 reserved per §3.5 slot layout); hidden 32→64;
    /// ArchitectureVersion 1→3 (rev2 was an unshipped sketch, rev3 is the §7.4 sizing).
    /// Slot map lives in StrategicStateVector.ThreatSlots — the LearningService ThreatEvent
    /// builder (LearningService.cs:544-694) must populate features per that constant block.
    /// Replaces the heuristic threat_rating in VehicleModel once trained.
    /// </summary>
    public sealed class ThreatAssessmentModel : ILearnedModel
    {
        public const int FeatureDim = 48;
        public const int H1 = 64;
        public const int H2 = 64;
        public const int MinibatchSize = 32;
        public const int EventQueueCapacity = 1024;
        public const float DefaultLearningRate = 0.001f;

        public ModelId Id => ModelId.ThreatAssessment;
        public byte ArchitectureVersion => 3;
        public int ParameterCount => _params.Length;

        private readonly float[] _params;
        private readonly AdamState _adam;
        private readonly DoubleBuffer<float[]> _publishedParams;
        private readonly BoundedQueue<ThreatEvent> _events;
        // L-013: ref-type lock target; TrainerWorker.RunLoop locks this around
        // TrainOneMinibatch; SnapshotManager + QuitSaveCoordinator lock around StoreParameters.
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
            ThreatEvent _;
            for (int i = 0; i < MinibatchSize; i++) { if (!_events.TryDequeue(out _)) break; }
        }

        private readonly int _w1Offset, _b1Offset, _w2Offset, _b2Offset, _w3Offset, _b3Offset, _totalParams;

        public BoundedQueue<ThreatEvent> EventQueue => _events;

        public ThreatAssessmentModel()
        {
            _w1Offset = 0;
            _b1Offset = _w1Offset + H1 * FeatureDim;
            _w2Offset = _b1Offset + H1;
            _b2Offset = _w2Offset + H2 * H1;
            _w3Offset = _b2Offset + H2;
            _b3Offset = _w3Offset + 1 * H2;
            _totalParams = _b3Offset + 1;

            _params = new float[_totalParams];
            MlpUtil.GlorotInit(_params, _w1Offset, FeatureDim, H1);
            MlpUtil.GlorotInit(_params, _w2Offset, H1, H2);
            MlpUtil.GlorotInit(_params, _w3Offset, H2, 1);
            _adam = new AdamState(_totalParams, DefaultLearningRate);
            _publishedParams = new DoubleBuffer<float[]>(Clone(_params));
            _events = new BoundedQueue<ThreatEvent>(EventQueueCapacity, "Learning.ThreatEvents");
        }

        public float Evaluate(float[] features)
        {
            var p = _publishedParams.Read();
            var h1 = new float[H1];
            MlpUtil.MatMulAdd(p, _w1Offset, p, _b1Offset, features, h1, FeatureDim, H1);
            MlpUtil.Relu(h1, H1);
            var h2 = new float[H2];
            MlpUtil.MatMulAdd(p, _w2Offset, p, _b2Offset, h1, h2, H1, H2);
            MlpUtil.Relu(h2, H2);
            var y = new float[1];
            MlpUtil.MatMulAdd(p, _w3Offset, p, _b3Offset, h2, y, H2, 1);
            return y[0];
        }

        public TrainStepResult TrainOneMinibatch()
        {
            var batch = new ThreatEvent[MinibatchSize];
            int n = 0;
            while (n < MinibatchSize && _events.TryDequeue(out var ev)) batch[n++] = ev;
            if (n == 0) return TrainStepResult.Empty;

            var grad = new float[_totalParams];
            float lossBefore = 0f;
            for (int i = 0; i < n; i++) lossBefore += SingleStepGradient(batch[i], grad);
            float invN = 1f / n;
            for (int i = 0; i < _totalParams; i++) grad[i] *= invN;
            lossBefore /= n;

            _adam.Step(_params, grad);
            _publishedParams.Write(Clone(_params));

            float lossAfter = 0f;
            for (int i = 0; i < n; i++) lossAfter += ComputeLoss(batch[i]);
            lossAfter /= n;
            return new TrainStepResult(lossBefore, lossAfter, n);
        }

        private float SingleStepGradient(ThreatEvent ev, float[] gradAccum)
        {
            var pre1 = new float[H1]; var post1 = new float[H1];
            var pre2 = new float[H2]; var post2 = new float[H2];
            var y = new float[1];
            MlpUtil.MatMulAdd(_params, _w1Offset, _params, _b1Offset, ev.Features, pre1, FeatureDim, H1);
            Array.Copy(pre1, post1, H1); MlpUtil.Relu(post1, H1);
            MlpUtil.MatMulAdd(_params, _w2Offset, _params, _b2Offset, post1, pre2, H1, H2);
            Array.Copy(pre2, post2, H2); MlpUtil.Relu(post2, H2);
            MlpUtil.MatMulAdd(_params, _w3Offset, _params, _b3Offset, post2, y, H2, 1);

            float dy = y[0] - ev.ObservedThreat;
            float lossSum = dy * dy;

            for (int i = 0; i < H2; i++) gradAccum[_w3Offset + i] += dy * post2[i];
            gradAccum[_b3Offset] += dy;
            var dPost2 = new float[H2];
            for (int i = 0; i < H2; i++) dPost2[i] = _params[_w3Offset + i] * dy;
            var dPre2 = new float[H2];
            for (int i = 0; i < H2; i++) dPre2[i] = pre2[i] > 0f ? dPost2[i] : 0f;

            for (int o = 0; o < H2; o++)
            {
                int row = _w2Offset + o * H1;
                gradAccum[_b2Offset + o] += dPre2[o];
                for (int i = 0; i < H1; i++) gradAccum[row + i] += dPre2[o] * post1[i];
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

            for (int o = 0; o < H1; o++)
            {
                int row = _w1Offset + o * FeatureDim;
                gradAccum[_b1Offset + o] += dPre1[o];
                for (int i = 0; i < FeatureDim; i++) gradAccum[row + i] += dPre1[o] * ev.Features[i];
            }
            return 0.5f * lossSum;
        }

        private float ComputeLoss(ThreatEvent ev)
        {
            float y = Evaluate(ev.Features);
            float d = y - ev.ObservedThreat;
            return 0.5f * d * d;
        }

        /// <summary>
        /// L-013: drain whatever events are in the queue (up to MinibatchSize) and run a
        /// final partial-minibatch step under the SaveMutex. Returns event count consumed.
        /// </summary>
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
            if (src == null || src.Length != _params.Length) throw new ArgumentException("ThreatModel: parameter length mismatch.");
            // Lock so a Director rollback / SnapshotManager restore can't race the
            // trainer's _publishedParams.Write mid-copy.
            lock (_saveMutex)
            {
                Array.Copy(src, _params, _params.Length);
                _publishedParams.Write(Clone(_params));
            }
        }

        /// <summary>
        /// P11 T3 Item 53: real Glorot reset. Seeds MlpUtil with seed XOR ModelId so two
        /// models reset in sequence don't get correlated weight streams, re-runs the
        /// ctor's Glorot init for the 3 weight matrices, zero-fills biases, resets the
        /// Adam optimizer state, and publishes the new params via the inference DoubleBuffer.
        /// </summary>
        public void Reset(int seed)
        {
            MlpUtil.ResetSeed(seed ^ (int)Id);
            MlpUtil.GlorotInit(_params, _w1Offset, FeatureDim, H1);
            MlpUtil.ZeroFill(_params, _b1Offset, H1);
            MlpUtil.GlorotInit(_params, _w2Offset, H1, H2);
            MlpUtil.ZeroFill(_params, _b2Offset, H2);
            MlpUtil.GlorotInit(_params, _w3Offset, H2, 1);
            MlpUtil.ZeroFill(_params, _b3Offset, 1);
            _adam.Reset();
            _publishedParams.Write(Clone(_params));
        }

        private static float[] Clone(float[] s) { var c = new float[s.Length]; Array.Copy(s, c, s.Length); return c; }
    }
}
