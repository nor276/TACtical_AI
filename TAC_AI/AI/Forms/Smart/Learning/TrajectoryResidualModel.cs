using System;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// Training event for the trajectory-residual model. Captures the features at fire
    /// time and the observed residual (= actual position at projectile arrival minus
    /// the linear extrapolation). Per LEARNING-CONTRACT §3.3.
    /// </summary>
    public readonly struct ResidualEvent
    {
        public readonly TechId TargetId;
        public readonly float[] Features;  // length FeatureDim
        public readonly Vector3 ObservedResidual;

        public ResidualEvent(TechId targetId, float[] features, Vector3 observedResidual)
        {
            TargetId = targetId; Features = features; ObservedResidual = observedResidual;
        }
    }

    /// <summary>
    /// Trajectory residual model: MLP 2×64 -> 3D output. Per LEARNING-CONTRACT §3.3 / plan §7.3.
    /// Predicts the position correction beyond linear extrapolation for weapon-lead use.
    /// Inference fills <see cref="CONTROL-CONTRACT.md §10.3"/>'s LearnedResidual slot.
    ///
    /// FeatureDim = 48 matches StrategicStateVector.ResidualDim (plan §3.4). Input layout:
    ///   [0..7]   projection geometry
    ///   [8..15]  shooter kinematics
    ///   [16..23] target kinematics
    ///   [24..31] weapon spec
    ///   [32..34] LinearExtrapXYZ -- the engine's linear-extrapolation prediction at fire
    ///            time, fed as INPUT. NOT ObservedResidualXYZ; that is the training LABEL
    ///            (Vector3 ResidualEvent.ObservedResidual) and must never appear in the
    ///            feature vector (round-1 F-09 anti-leakage).
    ///   [35..47] reserved bases (zero-filled by the extractor today).
    ///
    /// Round-2 R2-04: features are stamped at fire time, not query time. Wiring widens
    /// LeadResidualRecorder.Pending with float[] FireTimeFeatures and OnFireCommit takes
    /// a float[] arg so the captured fire-instant features feed ResidualEvent verbatim.
    /// </summary>
    public sealed class TrajectoryResidualModel : ILearnedModel
    {
        public const int FeatureDim = 48;
        public const int H1 = 64;
        public const int H2 = 64;
        public const int OutDim = 3;
        public const int MinibatchSize = 32;
        public const int EventQueueCapacity = 1024;
        public const float DefaultLearningRate = 0.001f;

        public ModelId Id => ModelId.TrajectoryResidual;
        public byte ArchitectureVersion => 3;
        public int ParameterCount => _params.Length;

        private readonly float[] _params;
        private readonly AdamState _adam;
        private readonly DoubleBuffer<float[]> _publishedParams;
        private readonly BoundedQueue<ResidualEvent> _events;
        // L-013: per-model save mutex; TrainerWorker locks around TrainOneMinibatch, saves
        // lock around StoreParameters / FlushPendingForPersist.
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
            ResidualEvent _;
            for (int i = 0; i < MinibatchSize; i++) { if (!_events.TryDequeue(out _)) break; }
        }

        private readonly int _w1Offset, _b1Offset, _w2Offset, _b2Offset, _w3Offset, _b3Offset, _totalParams;

        public BoundedQueue<ResidualEvent> EventQueue => _events;

        public TrajectoryResidualModel()
        {
            _w1Offset = 0;
            _b1Offset = _w1Offset + H1 * FeatureDim;
            _w2Offset = _b1Offset + H1;
            _b2Offset = _w2Offset + H2 * H1;
            _w3Offset = _b2Offset + H2;
            _b3Offset = _w3Offset + OutDim * H2;
            _totalParams = _b3Offset + OutDim;

            _params = new float[_totalParams];
            MlpUtil.GlorotInit(_params, _w1Offset, FeatureDim, H1);
            MlpUtil.GlorotInit(_params, _w2Offset, H1, H2);
            MlpUtil.GlorotInit(_params, _w3Offset, H2, OutDim);
            _adam = new AdamState(_totalParams, DefaultLearningRate);
            _publishedParams = new DoubleBuffer<float[]>(Clone(_params));
            _events = new BoundedQueue<ResidualEvent>(EventQueueCapacity, "Learning.ResidualEvents");
        }

        public Vector3 Evaluate(float[] features)
        {
            var p = _publishedParams.Read();
            var h1 = new float[H1];
            MlpUtil.MatMulAdd(p, _w1Offset, p, _b1Offset, features, h1, FeatureDim, H1);
            MlpUtil.Relu(h1, H1);
            var h2 = new float[H2];
            MlpUtil.MatMulAdd(p, _w2Offset, p, _b2Offset, h1, h2, H1, H2);
            MlpUtil.Relu(h2, H2);
            var y = new float[OutDim];
            MlpUtil.MatMulAdd(p, _w3Offset, p, _b3Offset, h2, y, H2, OutDim);
            return new Vector3(y[0], y[1], y[2]);
        }

        public TrainStepResult TrainOneMinibatch()
        {
            var batch = new ResidualEvent[MinibatchSize];
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

        private float SingleStepGradient(ResidualEvent ev, float[] gradAccum)
        {
            // Forward (training params).
            var pre1 = new float[H1]; var post1 = new float[H1];
            var pre2 = new float[H2]; var post2 = new float[H2];
            var y = new float[OutDim];
            MlpUtil.MatMulAdd(_params, _w1Offset, _params, _b1Offset, ev.Features, pre1, FeatureDim, H1);
            Array.Copy(pre1, post1, H1); MlpUtil.Relu(post1, H1);
            MlpUtil.MatMulAdd(_params, _w2Offset, _params, _b2Offset, post1, pre2, H1, H2);
            Array.Copy(pre2, post2, H2); MlpUtil.Relu(post2, H2);
            MlpUtil.MatMulAdd(_params, _w3Offset, _params, _b3Offset, post2, y, H2, OutDim);

            // MSE loss vs ObservedResidual. dL/dy = (y - obs).
            var target = new[] { ev.ObservedResidual.x, ev.ObservedResidual.y, ev.ObservedResidual.z };
            var dy = new float[OutDim];
            float lossSum = 0f;
            for (int o = 0; o < OutDim; o++)
            {
                float d = y[o] - target[o];
                dy[o] = d;
                lossSum += d * d;
            }

            // Backprop layer 3.
            for (int o = 0; o < OutDim; o++)
            {
                int row = _w3Offset + o * H2;
                gradAccum[_b3Offset + o] += dy[o];
                for (int i = 0; i < H2; i++) gradAccum[row + i] += dy[o] * post2[i];
            }
            var dPost2 = new float[H2];
            for (int i = 0; i < H2; i++)
            {
                float sum = 0f;
                for (int o = 0; o < OutDim; o++) sum += _params[_w3Offset + o * H2 + i] * dy[o];
                dPost2[i] = sum;
            }
            var dPre2 = new float[H2];
            for (int i = 0; i < H2; i++) dPre2[i] = pre2[i] > 0f ? dPost2[i] : 0f;

            // Backprop layer 2.
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

            // Backprop layer 1.
            for (int o = 0; o < H1; o++)
            {
                int row = _w1Offset + o * FeatureDim;
                gradAccum[_b1Offset + o] += dPre1[o];
                for (int i = 0; i < FeatureDim; i++) gradAccum[row + i] += dPre1[o] * ev.Features[i];
            }

            return 0.5f * lossSum;
        }

        private float ComputeLoss(ResidualEvent ev)
        {
            Vector3 pred = Evaluate(ev.Features);
            Vector3 d = pred - ev.ObservedResidual;
            return 0.5f * (d.x * d.x + d.y * d.y + d.z * d.z);
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
            if (src == null || src.Length != _params.Length) throw new ArgumentException("ResidualModel: parameter length mismatch.");
            // Lock so a Director rollback / SnapshotManager restore can't race the
            // trainer's _publishedParams.Write mid-copy.
            lock (_saveMutex)
            {
                Array.Copy(src, _params, _params.Length);
                _publishedParams.Write(Clone(_params));
            }
        }

        /// <summary>P11 T3 Item 53: real Glorot reset — same shape as ThreatAssessmentModel.</summary>
        public void Reset(int seed)
        {
            MlpUtil.ResetSeed(seed ^ (int)Id);
            MlpUtil.GlorotInit(_params, _w1Offset, FeatureDim, H1);
            MlpUtil.ZeroFill(_params, _b1Offset, H1);
            MlpUtil.GlorotInit(_params, _w2Offset, H1, H2);
            MlpUtil.ZeroFill(_params, _b2Offset, H2);
            MlpUtil.GlorotInit(_params, _w3Offset, H2, OutDim);
            MlpUtil.ZeroFill(_params, _b3Offset, OutDim);
            _adam.Reset();
            _publishedParams.Write(Clone(_params));
        }

        private static float[] Clone(float[] s) { var c = new float[s.Length]; Array.Copy(s, c, s.Length); return c; }
    }
}
