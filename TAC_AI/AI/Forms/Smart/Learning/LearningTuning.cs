using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P8 Item 19: learning-subsystem tunables. v0.2 ships with full BPTT through the
    /// intent classifier's GRU enabled by default per the plan ("default on once Phase 8
    /// ships"). One-flip revert path for if the trainer worker's CircuitBreaker trips on
    /// NaN — flipping <see cref="UseFullBPTT"/> back to false reverts the classifier to
    /// the v0.1 dense-head-only training path that already shipped.
    /// </summary>
    public static class LearningTuning
    {
        /// <summary>
        /// When true (default), <see cref="OpponentIntentClassifier.TrainOneMinibatch"/>
        /// invokes the full GRU BPTT path (<see cref="GruBackprop.Forward"/> + Backward)
        /// — gradients flow through all 9 GRU gate parameter slots
        /// (W_r/U_r/b_r/W_z/U_z/b_z/W_h/U_h/b_h) plus the dense head.
        ///
        /// When false, the classifier falls back to the v0.1 dense-head-only path
        /// (TrainOneMinibatch_DenseOnly). GRU gate parameters stay at their Glorot-init
        /// values; only W_o/b_o train. Behavior identical to v0.1 pre-P8.
        /// </summary>
        public static bool UseFullBPTT = true;

        /// <summary>
        /// Softmax temperature for OpponentIntentClassifier. Read-once-per-batch via
        /// Volatile.Read at the top of TrainOneMinibatch_FullBptt; that local divides
        /// the logits before MlpUtil.Softmax. Director temp_adjust writes here.
        /// </summary>
        public static volatile float IntentTemperature = 1f;

        /// <summary>
        /// Per-OutcomeKind reward weight, indexed by (byte)OutcomeKind. Reward-aggregation
        /// path (IdentityOutcomeConsumer wiring lands in a later phase) reads this. Phase 5
        /// will insert GuardViolation_* values into the enum before Count and the array
        /// resizes automatically. Default 1.0 for everything ships today; GuardViolation_*
        /// will default to 0.0 (observe-only) when Phase 5 lands them.
        /// </summary>
        public static readonly float[] OutcomeWeights = NewOutcomeWeights();

        private static float[] NewOutcomeWeights()
        {
            var arr = new float[(int)OutcomeKind.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = 1f;
            return arr;
        }
    }
}
