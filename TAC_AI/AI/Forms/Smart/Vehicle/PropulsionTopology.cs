namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: high-level propulsion shape derived from the emitter mix.
    ///
    /// Static     -- anchored or stationary (no propulsion).
    /// Wheel1D    -- ground-only forward/back; lateral via differential steering only.
    /// Hover2D    -- vertical lift + horizontal translation; no fixed ground orientation.
    /// Aircraft3D -- full 3D mobility with aero-scaled lift.
    /// Walker     -- reserved-unreachable v0.1 per Decision #7.
    /// Mixed      -- multi-channel propulsion that doesn't fit cleanly (e.g., wheel + booster
    ///               combinations); MobilityProfile.Derive handles via per-axis caps.
    ///
    /// Consumed in v0.2 by CapabilityFilters + ContinuousController per Step 10.
    /// </summary>
    public enum PropulsionTopology : byte
    {
        Static     = 0,
        Wheel1D    = 1,
        Hover2D    = 2,
        Aircraft3D = 3,
        Walker     = 4,
        Mixed      = 5,
    }
}
