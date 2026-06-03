using System;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// Identifies one of Smart's four learned models. Stable on-disk values (do not
    /// renumber — they're written into per-model section headers). Per LEARNING §5.2.
    /// </summary>
    public enum ModelId : byte
    {
        Intent = 0,
        ActionValue = 1,
        TrajectoryResidual = 2,
        ThreatAssessment = 3,
    }

    /// <summary>
    /// Result of one minibatch training step. Per FIX-PLAN.md Phase 1.3 — replaces
    /// the C# 7 tuple return (which requires System.ValueTuple, absent in the
    /// .NET 4.6.1 reference assemblies) with a BCL-only readonly struct. All three
    /// values are preserved: pre-step loss, post-step loss, and the actual batch
    /// size consumed.
    /// </summary>
    public readonly struct TrainStepResult
    {
        public readonly float LossBefore;
        public readonly float LossAfter;
        public readonly int BatchSize;

        public TrainStepResult(float lossBefore, float lossAfter, int batchSize)
        {
            LossBefore = lossBefore;
            LossAfter = lossAfter;
            BatchSize = batchSize;
        }

        public static readonly TrainStepResult Empty = new TrainStepResult(0f, 0f, 0);
    }

    /// <summary>
    /// Trainable model contract. Each of the four models implements this; the
    /// <see cref="OnlineTrainer"/> drives them through a uniform train-step API
    /// without knowing their internal architecture.
    /// </summary>
    public interface ILearnedModel
    {
        ModelId Id { get; }
        byte ArchitectureVersion { get; }
        int ParameterCount { get; }

        // Serialization round-trip. Used by ProfilePersistence.
        void StoreParameters(float[] dest);
        void LoadParameters(float[] src);

        // Trainer-driven step. Implementations consume from their own bounded queues
        // (held by LearningService) — this method runs a single minibatch and
        // publishes new parameters to the model's inference DoubleBuffer. Returns
        // TrainStepResult.Empty (BatchSize == 0) when the queue held fewer than
        // minibatch-size events.
        TrainStepResult TrainOneMinibatch();
    }

    /// <summary>
    /// Adam optimizer state for one parameter array. Per LEARNING §4.2.
    /// </summary>
    public sealed class AdamState
    {
        public readonly float[] M;
        public readonly float[] V;
        // Phase 6 (FIX-PLAN.md) — AUDIT-R2 §2.R2.F: T was int; at sustained 30 minibatches/s
        // it would overflow to int.MinValue after ~2.1 billion steps (~828 days). After
        // overflow, Math.Pow(Beta1, negative T) returns +Infinity → biasCorr = -Infinity →
        // mHat = 0 → optimizer silently freezes. long supports ~9 quintillion steps.
        public long T;
        public float Beta1 = 0.9f;
        public float Beta2 = 0.999f;
        public float Epsilon = 1e-8f;
        public float LearningRate;

        public AdamState(int paramCount, float learningRate)
        {
            M = new float[paramCount];
            V = new float[paramCount];
            T = 0;
            LearningRate = learningRate;
        }

        /// <summary>Apply one Adam step in-place. <paramref name="grad"/> length must equal <paramref name="parameters"/>.</summary>
        public void Step(float[] parameters, float[] grad)
        {
            T++;
            float oneBeta1 = 1f - Beta1, oneBeta2 = 1f - Beta2;
            float biasCorr1 = 1f - (float)Math.Pow(Beta1, T);
            float biasCorr2 = 1f - (float)Math.Pow(Beta2, T);
            for (int i = 0; i < parameters.Length; i++)
            {
                float g = grad[i];
                M[i] = Beta1 * M[i] + oneBeta1 * g;
                V[i] = Beta2 * V[i] + oneBeta2 * g * g;
                float mHat = M[i] / biasCorr1;
                float vHat = V[i] / biasCorr2;
                parameters[i] -= LearningRate * mHat / ((float)Math.Sqrt(vHat) + Epsilon);
            }
        }
    }

    /// <summary>
    /// Math primitives used across the model files. All allocation-free
    /// (caller supplies output buffers). Per LEARNING-CONTRACT inference budget.
    /// </summary>
    public static class MlpUtil
    {
        private static int _seed = 17;

        /// <summary>
        /// Glorot (Xavier) initialization for a weight matrix with <paramref name="fanIn"/>
        /// inputs and <paramref name="fanOut"/> outputs. Writes <c>fanIn*fanOut</c> floats
        /// starting at <paramref name="offset"/>. Deterministic given the static seed —
        /// reset via <see cref="ResetSeed"/> for tests.
        /// </summary>
        public static void GlorotInit(float[] dest, int offset, int fanIn, int fanOut)
        {
            float scale = (float)Math.Sqrt(6.0 / (fanIn + fanOut));
            int count = fanIn * fanOut;
            for (int i = 0; i < count; i++) dest[offset + i] = (NextUniform() * 2f - 1f) * scale;
        }

        public static void ZeroFill(float[] dest, int offset, int count)
        {
            for (int i = 0; i < count; i++) dest[offset + i] = 0f;
        }

        public static void ResetSeed(int seed) { _seed = seed; }

        /// <summary>Deterministic pseudo-random uniform [0, 1). Tiny xorshift32.</summary>
        public static float NextUniform()
        {
            uint x = unchecked((uint)_seed);
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            _seed = unchecked((int)x);
            return (x & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>y = W * x + b. W is row-major (out × in). x length=inDim, y length=outDim, b length=outDim.</summary>
        public static void MatMulAdd(float[] W, int wOffset, float[] b, int bOffset, float[] x, float[] y, int inDim, int outDim)
        {
            for (int o = 0; o < outDim; o++)
            {
                float sum = b[bOffset + o];
                int row = wOffset + o * inDim;
                for (int i = 0; i < inDim; i++) sum += W[row + i] * x[i];
                y[o] = sum;
            }
        }

        public static void Relu(float[] inOut, int len)
        {
            for (int i = 0; i < len; i++) if (inOut[i] < 0f) inOut[i] = 0f;
        }

        /// <summary>Returns 1.0 where the value is &gt; 0, 0 otherwise (the ReLU derivative).</summary>
        public static void ReluDeriv(float[] preActivation, float[] dest, int len)
        {
            for (int i = 0; i < len; i++) dest[i] = preActivation[i] > 0f ? 1f : 0f;
        }

        public static float Sigmoid(float x) => 1f / (1f + (float)Math.Exp(-x));
        public static float TanhF(float x) => (float)Math.Tanh(x);

        /// <summary>Softmax over <paramref name="len"/> elements in-place. Numerically stable.</summary>
        public static void Softmax(float[] inOut, int len)
        {
            float max = inOut[0];
            for (int i = 1; i < len; i++) if (inOut[i] > max) max = inOut[i];
            float sum = 0f;
            for (int i = 0; i < len; i++) { inOut[i] = (float)Math.Exp(inOut[i] - max); sum += inOut[i]; }
            float inv = sum > 1e-12f ? 1f / sum : 0f;
            for (int i = 0; i < len; i++) inOut[i] *= inv;
        }

        /// <summary>Categorical cross-entropy loss over a probability distribution against a one-hot label index.</summary>
        public static float CrossEntropy(float[] probs, int labelIndex)
        {
            float p = Math.Max(probs[labelIndex], 1e-12f);
            return -(float)Math.Log(p);
        }
    }

    /// <summary>
    /// Per-model long-running training worker. Polls the model's
    /// <see cref="ILearnedModel.TrainOneMinibatch"/> on a sleep cadence; the model
    /// owns whether a minibatch is ready to consume. Per LEARNING §4.2.
    /// </summary>
    public sealed class TrainerWorker
    {
        public int MillisecondsPerPoll { get; set; } = 100;

        private readonly ILearnedModel _model;
        public TrainerWorker(ILearnedModel model) { _model = model; }

        public void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                // Phase 5 (FIX-PLAN.md): pause training on client. Open question #1 in
                // FIX-PLAN.md decided to pause for consistency — clients should not
                // mutate their local copy of the learning profile while the host's
                // canonical profile is the source of truth. The worker stays loaded so
                // a future host-handover does not pay cold-start cost.
                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                    continue;
                }
                try
                {
                    var result = _model.TrainOneMinibatch();
                    // If no batch was ready, sleep to avoid busy-spin.
                    if (result.BatchSize == 0)
                    {
                        if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Learning.Trainer[" + _model.Id + "]: " + ex.GetType().Name + ": " + ex.Message);
                    if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                }
            }
        }
    }
}
