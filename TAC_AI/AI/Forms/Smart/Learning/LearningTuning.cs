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
    }
}
