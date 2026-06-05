namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P2 Item 37: per-EmitterKind multiplier table applied by ThrustField.Compute
    /// to each emitter's MaxForceN / ReverseForceN immediately before bucket aggregation.
    /// All defaults 1.0f → bit-identical to v0.1. CMA-ES (Training) can mutate these
    /// to bias per-propulsion-kind effective output; in-game tunables (P10) can expose
    /// them for live tweaking. Tunable category: "Chassis".
    /// </summary>
    public static class EmitterKindMultipliers
    {
        public static float WheelMul   = 1.0f;
        public static float FanJetMul  = 1.0f;
        public static float BoosterMul = 1.0f;
        public static float HoverMul   = 1.0f;
        public static float WingMul    = 1.0f;

        public static float For(EmitterKind k)
        {
            switch (k)
            {
                case EmitterKind.WheelRoll:      return WheelMul;
                case EmitterKind.FanJet:         return FanJetMul;
                case EmitterKind.BoosterImpulse: return BoosterMul;
                case EmitterKind.HoverPad:       return HoverMul;
                case EmitterKind.WingLift:       return WingMul;
                default:                          return 1f;
            }
        }
    }
}
