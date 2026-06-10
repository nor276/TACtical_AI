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

        // Director freeze + Adam round-trip surface. Backing field is
        // `private long _frozenUntilTickMono`; access via Interlocked.Read/Exchange
        // (volatile long is illegal CS0677).
        bool Frozen { get; }
        long FrozenUntilTickMono { get; set; }

        // AdamSnapshot returned BY VALUE — Director cannot mutate _adam directly. The
        // 7-Action invariant is enforced by language, not just code review.
        void LoadAdamState(AdamSnapshot snap);
        AdamSnapshot GetAdamSnapshot();
        void ReadAdamMoments(float[] mDest, float[] vDest, out long T, out float lr, out float baseLr);

        // Soft-freeze drain hook — Director discards a minibatch-worth of events when
        // the model is soft-frozen so the queue doesn't grow unbounded.
        void DrainOneMinibatchDiscard();

        // P11 T3 Item 53: deterministic Glorot/orthogonal weight reset + Adam state zero.
        // Each implementer re-runs its constructor's init block under MlpUtil.ResetSeed
        // (seed XOR model id for stream independence), then re-publishes the params via its
        // DoubleBuffer. The seed parameter makes the reset reproducible (same seed → same
        // weights), so tests + spawn-test campaigns can compare runs.
        void Reset(int seed);

        /// <summary>
        /// L-013: per-model mutex coordinating TrainOneMinibatch / StoreParameters /
        /// FlushPendingForPersist. Returns the model's `private readonly object _saveMutex`
        /// so callers can `lock(model.SaveMutex)` to serialise a save against an in-flight
        /// training step. C# lock requires a reference type, so this is `object`, not a
        /// value-typed primitive.
        /// </summary>
        object SaveMutex { get; }

        /// <summary>
        /// L-013: drain whatever queued events the trainer has not yet folded into model
        /// params, run a final partial-minibatch step if possible, and publish the
        /// resulting params. Called from QuitSaveCoordinator (L-078) before SaveProfile so
        /// the disk image includes the very latest training. Returns the number of events
        /// folded into the partial step (0 if the queue was empty).
        /// </summary>
        int FlushPendingForPersist();
    }

    /// <summary>
    /// Immutable snapshot of Adam state returned to Director rollback. M / V cloned
    /// at snapshot time so the caller cannot mutate the model's live buffers.
    /// </summary>
    public readonly struct AdamSnapshot
    {
        public readonly float[] M;
        public readonly float[] V;
        public readonly long T;
        public readonly float LR;
        public readonly float BaseLR;

        public AdamSnapshot(float[] m, float[] v, long t, float lr, float baseLr)
        {
            M = m;
            V = v;
            T = t;
            LR = lr;
            BaseLR = baseLr;
        }
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
        // Immutable baseline LR captured at construction so rollback can round-trip
        // the model back to its untouched configuration. LearningRate mutates via
        // SetLearningRate; BaseLearningRate never does.
        public readonly float BaseLearningRate;

        public AdamState(int paramCount, float learningRate)
        {
            M = new float[paramCount];
            V = new float[paramCount];
            T = 0;
            LearningRate = learningRate;
            BaseLearningRate = learningRate;
        }

        /// <summary>
        /// P11 T3 Item 53: zero the Adam moments + tick counter. Called from
        /// <see cref="ILearnedModel.Reset(int)"/> right after the weights are re-initialized
        /// so the optimizer doesn't push fresh Glorot weights with stale momentum (which
        /// would undo the variance scaling Glorot just established).
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < M.Length; i++) { M[i] = 0f; V[i] = 0f; }
            T = 0;
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

        /// <summary>
        /// P11 T3 Item 53: orthogonal initialization for square recurrent weight matrices
        /// (GRU's U_r / U_z / U_h hidden-hidden gates). Generates an n×n Gaussian matrix
        /// then applies modified Gram-Schmidt to produce an orthonormal basis — important
        /// for recurrent stability under BPTT (orthogonal matrices have unit singular
        /// values, so gradients neither explode nor vanish through repeated multiplication).
        /// Deterministic given the static seed; <see cref="ResetSeed"/> controls reproducibility.
        /// </summary>
        public static void OrthogonalInit(float[] dest, int offset, int n)
        {
            // Step 1: fill n×n with Gaussian samples (Box-Muller from two NextUniform calls).
            int total = n * n;
            for (int i = 0; i < total; i += 2)
            {
                float u1 = Math.Max(NextUniform(), 1e-9f);
                float u2 = NextUniform();
                float r = (float)Math.Sqrt(-2.0 * Math.Log(u1));
                float theta = 2f * (float)Math.PI * u2;
                dest[offset + i] = r * (float)Math.Cos(theta);
                if (i + 1 < total) dest[offset + i + 1] = r * (float)Math.Sin(theta);
            }
            // Step 2: modified Gram-Schmidt to orthonormalize rows (row-major n×n).
            for (int row = 0; row < n; row++)
            {
                int rOff = offset + row * n;
                // Subtract projections onto previously-orthonormalized rows.
                for (int prev = 0; prev < row; prev++)
                {
                    int pOff = offset + prev * n;
                    float dot = 0f;
                    for (int k = 0; k < n; k++) dot += dest[rOff + k] * dest[pOff + k];
                    for (int k = 0; k < n; k++) dest[rOff + k] -= dot * dest[pOff + k];
                }
                // Normalize this row.
                float norm = 0f;
                for (int k = 0; k < n; k++) norm += dest[rOff + k] * dest[rOff + k];
                norm = (float)Math.Sqrt(norm);
                if (norm < 1e-9f)
                {
                    // Degenerate row (vanishingly small after projection) — fall back to identity.
                    for (int k = 0; k < n; k++) dest[rOff + k] = (k == row) ? 1f : 0f;
                }
                else
                {
                    float inv = 1f / norm;
                    for (int k = 0; k < n; k++) dest[rOff + k] *= inv;
                }
            }
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

        /// <summary>
        /// L-071: TTL on the holding buffer for paused trainers. Default 5 minutes — if
        /// a trainer stays Paused longer (extended host migration), discard any drained
        /// minibatch slots so the resume doesn't replay stale events. int.MaxValue =
        /// effectively disabled (test override).
        /// </summary>
        public int HoldingBufferTtlMs { get; set; } = 5 * 60 * 1000;
        private int _pausedAtTickMs;
        private long _ttlDiscardsTotal;
        public long TtlDiscardsTotal => System.Threading.Interlocked.Read(ref _ttlDiscardsTotal);

        // L-058: trainer FSM. Active = normal training; Pausing = drain-in-flight; Paused
        // = parked at barrier (signaled); Resuming = transitioning back to Active. All
        // transitions happen on the trainer thread itself; HostChanged event flips _hostLost
        // which the trainer reads at the top of each iteration.
        public enum Phase { Active, Pausing, Paused, Resuming }

        private readonly ILearnedModel _model;
        private readonly string _trainerName;
        private volatile bool _hostLost;
        // Director pause channel: separate from _hostLost so a rollback can pause
        // training without going through host migration. volatile long is illegal
        // (CS0677) — use Volatile.Read/Write static helpers.
        private long _directorPaused;
        private volatile Phase _phase = Phase.Active;
        public Phase CurrentPhase => _phase;

        // Param-delta snapshot buffers — lazy-allocated on first use so cold-start
        // trainers don't pay 2 × ParameterCount × float upfront. Lives on the worker
        // (not the model) per Phase-2 plan: BEFORE/AFTER are trainer-side concerns.
        private float[] _paramBefore;
        private float[] _paramAfter;
        // Step counter: every 32 successful minibatches we push a ‖Δθ‖₂ sample.
        private int _stepsSinceParamSnapshot;
        public const int ParamSnapshotEveryNSteps = 32;

        // Diagnostic-freeze drop reporting watermark — only emit [DIAGNOSTIC-DROP] when
        // BoundedQueue.DroppedCount has advanced by >=100 since the last report.
        private long _lastDiagDropReport;

        public TrainerWorker(ILearnedModel model)
        {
            _model = model;
            _trainerName = "Trainer-" + model.Id;
            // L-058 barrier register so HostAuthorityCoordinator's WaitAll (L-070 caller)
            // can synchronize across all trainers when host is lost.
            TAC_AI.AI.Forms.Smart.Coordination.TrainerBarrier.Register(_trainerName);
            // L-058: subscribe to HostChanged so we can flip _hostLost without polling
            // SmartRuntime.IsHost (which is also read for legacy gate compatibility).
            try
            {
                TAC_AI.AI.Forms.Smart.World.WorldEventBus.Subscribe<TAC_AI.AI.Forms.Smart.World.HostChanged>(OnHostChanged);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainerWorker[" + _trainerName + "] HostChanged subscribe failed: " + ex.Message);
            }
        }

        // Per-model BoundedQueue.DroppedCount accessor for the diagnostic-freeze drop
        // surface. The 4 model types own typed BoundedQueues at EventQueue; we read by
        // dispatching on the model concrete type.
        private static long QueueDroppedCount(ILearnedModel model)
        {
            var intent = model as OpponentIntentClassifier;
            if (intent != null) return intent.EventQueue.DroppedCount;
            var av = model as ActionValueEstimator;
            if (av != null) return av.EventQueue.DroppedCount;
            var resid = model as TrajectoryResidualModel;
            if (resid != null) return resid.EventQueue.DroppedCount;
            var threat = model as ThreatAssessmentModel;
            if (threat != null) return threat.EventQueue.DroppedCount;
            return 0L;
        }

        private void OnHostChanged(TAC_AI.AI.Forms.Smart.World.HostChanged ev)
        {
            // Pause-token held while we flip _hostLost so a host-change racing a
            // rollback serialises behind the rollback's pause-token hold.
            string ownerTag = "OnHostChanged-" + _trainerName;
            TAC_AI.AI.Forms.Smart.Coordination.TrainerBarrier.AcquirePauseToken(ownerTag);
            try
            {
                _hostLost = !ev.IsHost;
            }
            finally
            {
                TAC_AI.AI.Forms.Smart.Coordination.TrainerBarrier.ReleasePauseToken(ownerTag);
            }
        }

        public void RunLoop(CancellationToken cancellation)
        {
            try
            {
            while (!cancellation.IsCancellationRequested)
            {
                // Phase 5 (FIX-PLAN.md): pause training on client. Open question #1 in
                // FIX-PLAN.md decided to pause for consistency — clients should not
                // mutate their local copy of the learning profile while the host's
                // canonical profile is the source of truth. The worker stays loaded so
                // a future host-handover does not pay cold-start cost.
                // L-026: pause-gate ahead of host-gate. Trainer parks during pause to keep
                // all CPU cores yielded to the host process.
                if (SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                    continue;
                }

                // L-058: host-loss FSM. Transition Active→Pausing→Paused (signal barrier)
                // → wait for !_hostLost → Resuming → Active. The legacy IsHost gate below
                // still works as a fallback (single-player + tests).
                // Director pause channel folded in — rollback can demand pause.
                if ((_hostLost || Volatile.Read(ref _directorPaused) != 0L || SmartRuntime.IsDirectorPaused) && _phase == Phase.Active)
                {
                    _phase = Phase.Pausing;
                    _pausedAtTickMs = System.Environment.TickCount;
                    DebugTAC_AI.LogWarnFileOnly("trainer-hostchange-" + _trainerName + "-pausing",
                        "[TRAINER-HOSTCHANGE] trainer='" + _trainerName + "' Active→Pausing (draining minibatch)");
                    // Drain remaining queue work via FlushPendingForPersist (L-013) so we
                    // checkpoint a clean param image.
                    try { _model.FlushPendingForPersist(); }
                    catch (Exception ex) { DebugTAC_AI.LogWarning("TrainerWorker drain: " + ex.Message); }
                    _phase = Phase.Paused;
                    TAC_AI.AI.Forms.Smart.Coordination.TrainerBarrier.Signal(_trainerName);
                    DebugTAC_AI.LogWarnFileOnly("trainer-hostchange-" + _trainerName + "-paused",
                        "[TRAINER-HOSTCHANGE] trainer='" + _trainerName + "' Paused (barrier signaled)");
                }
                if (_phase == Phase.Paused)
                {
                    // L-071: TTL discard. If we've been Paused longer than the TTL, drain
                    // the queue to prevent stale-event replay on resume.
                    int pausedMs = unchecked(System.Environment.TickCount - _pausedAtTickMs);
                    if (pausedMs > HoldingBufferTtlMs)
                    {
                        try
                        {
                            int discarded = _model.FlushPendingForPersist();
                            if (discarded > 0)
                            {
                                System.Threading.Interlocked.Add(ref _ttlDiscardsTotal, discarded);
                                DebugTAC_AI.LogWarnFileOnly("trainer-hold-discard-" + _trainerName,
                                    "[TRAINER-HOLD-DISCARD] trainer='" + _trainerName
                                    + "' discarded=" + discarded + " pausedMs=" + pausedMs);
                            }
                            _pausedAtTickMs = System.Environment.TickCount; // re-stamp to throttle log
                        }
                        catch (Exception ex) { DebugTAC_AI.LogWarning("TrainerWorker TTL drain: " + ex.Message); }
                    }
                    // Resume gate requires host returned AND Director released (both the
                    // per-trainer channel and the SmartRuntime-level pause counter).
                    if (!_hostLost && Volatile.Read(ref _directorPaused) == 0L && !SmartRuntime.IsDirectorPaused)
                    {
                        _phase = Phase.Resuming;
                        DebugTAC_AI.LogWarnFileOnly("trainer-hostchange-" + _trainerName + "-resuming",
                            "[TRAINER-HOSTCHANGE] trainer='" + _trainerName + "' Paused→Resuming");
                        // Memory barrier — ensures the _directorPaused read is ordered before
                        // the _phase write so a concurrent rollback doesn't see Active+paused.
                        Volatile.Read(ref _directorPaused);
                        _phase = Phase.Active;
                        DebugTAC_AI.LogWarnFileOnly("trainer-hostchange-" + _trainerName + "-active",
                            "[TRAINER-HOSTCHANGE] trainer='" + _trainerName + "' Active");
                    }
                    else
                    {
                        if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                        continue;
                    }
                }

                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                    continue;
                }

                // Director freeze gate. When the model is frozen we skip the Adam
                // step + DoubleBuffer.Write entirely. Diagnostic mode leaves the typed
                // event queue alone (lets it backpressure-drop at capacity; the BoundedQueue
                // drop counter surfaces the data loss). Soft mode drains a minibatch worth
                // of events via DrainOneMinibatchDiscard so the queue does not grow unbounded.
                // AdamState.T is preserved (not reset) so bias-correction stays consistent
                // on resume.
                byte freezeMode;
                if (TAC_AI.AI.Forms.Smart.Director.Actions.FreezeModelAction.IsFrozen(_model, out freezeMode))
                {
                    if (freezeMode == TAC_AI.AI.Forms.Smart.Director.Actions.FreezeModelAction.ModeSoft)
                    {
                        try { _model.DrainOneMinibatchDiscard(); }
                        catch (Exception ex)
                        {
                            DebugTAC_AI.LogWarnFileOnly("trainer-freeze-drain-" + _model.Id,
                                "[TRAINER-FREEZE] drain threw " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                    else
                    {
                        // Diagnostic: surface backpressure-driven drops at a per-100-events
                        // cadence so the operator can see data loss without spamming.
                        // BoundedQueue.DroppedCount is monotonic; we coarse-bucket on /100.
                        long dropped = QueueDroppedCount(_model);
                        long lastReported = Interlocked.Read(ref _lastDiagDropReport);
                        if (dropped - lastReported >= 100)
                        {
                            if (Interlocked.CompareExchange(ref _lastDiagDropReport, dropped, lastReported) == lastReported)
                            {
                                DebugTAC_AI.LogWarnFileOnly("director-diagnostic-drop-" + _model.Id,
                                    "[DIAGNOSTIC-DROP] model=" + _model.Id + " dropped=" + dropped
                                    + " mode=diagnostic");
                            }
                        }
                    }
                    if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                    continue;
                }

                try
                {
                    // L-013: serialise TrainOneMinibatch against StoreParameters/
                    // FlushPendingForPersist so a save in flight does not snapshot a torn
                    // half-step (Adam Step mid-pass over _params). Lock granularity is the
                    // whole minibatch — fine because batches are 32 events / ~1ms typical.
                    TrainStepResult result;
                    // Param snapshot due? Capture BEFORE under the same lock so the L2
                    // diff is consistent with the AFTER snapshot.
                    bool snapshotDue = _stepsSinceParamSnapshot >= ParamSnapshotEveryNSteps - 1;
                    if (snapshotDue && _paramBefore == null)
                    {
                        _paramBefore = new float[_model.ParameterCount];
                        _paramAfter = new float[_model.ParameterCount];
                    }
                    lock (_model.SaveMutex)
                    {
                        if (snapshotDue) _model.StoreParameters(_paramBefore);
                        result = _model.TrainOneMinibatch();
                        if (snapshotDue) _model.StoreParameters(_paramAfter);
                    }
                    // If no batch was ready, sleep to avoid busy-spin.
                    if (result.BatchSize == 0)
                    {
                        if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                    }
                    else
                    {
                        // Push loss into the reservoir. Source==Live gating wires up once
                        // IdentityReplayBank has separable Live/Replay channels (deferred).
                        float normReward = 0f;
                        var av = _model as ActionValueEstimator;
                        if (av != null) normReward = av.EwmaAbsReward;
                        Director.TrainerStats.LossPush(_model.Id, result.LossBefore, result.LossAfter, result.BatchSize, normReward);

                        // Every 32 steps push a ‖Δθ‖₂ / sqrt(N) sample.
                        if (snapshotDue)
                        {
                            float sumSq = 0f;
                            int len = _paramAfter.Length;
                            for (int i = 0; i < len; i++)
                            {
                                float d = _paramAfter[i] - _paramBefore[i];
                                sumSq += d * d;
                            }
                            float l2 = (float)Math.Sqrt(sumSq);
                            float archInvariant = len > 0 ? l2 / (float)Math.Sqrt(len) : 0f;
                            Director.TrainerStats.ParamDeltaPush(_model.Id, archInvariant);
                            _stepsSinceParamSnapshot = 0;
                        }
                        else
                        {
                            _stepsSinceParamSnapshot++;
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                // L-024: explicit TAE catch before generic Exception — CLR auto-rethrows
                // TAE past catch(Exception). Trainer label embeds model id so AbortGuard's
                // per-worker storm window is per-model (not per-process).
                catch (System.Threading.ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("Trainer-" + _model.Id)
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Learning.Trainer[" + _model.Id + "]: " + ex.GetType().Name + ": " + ex.Message);
                    if (cancellation.WaitHandle.WaitOne(MillisecondsPerPoll)) return;
                }
            }
            }
            finally
            {
                // Pair the ctor's HostChanged subscribe. Without this the LearningService
                // respawn factory pins every dead TrainerWorker in WorldEventBus's
                // multicast list, growing per Init/Shutdown cycle (BUG-012).
                try
                {
                    TAC_AI.AI.Forms.Smart.World.WorldEventBus.Unsubscribe<TAC_AI.AI.Forms.Smart.World.HostChanged>(OnHostChanged);
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("TrainerWorker[" + _trainerName + "] HostChanged unsubscribe failed: " + ex.Message);
                }
            }
        }
    }
}
