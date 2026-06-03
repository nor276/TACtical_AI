namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Categorical staleness of a belief trace. Per AETHER-DESIGN.md "Uncertainty channel".
    ///
    /// Fresh    — observed this perception tick. Position/Velocity are the raw observation.
    /// Coasting — observed within StaleAfterSec ago; dead-reckoned forward via DeadReckon.Coast.
    /// Stale    — observed between StaleAfterSec and LostAfterSec; coast continues, uncertainty grows.
    /// Lost     — observed >= LostAfterSec ago; consumers gate behavior on this flag.
    ///
    /// SightState replaces the old per-trace variance-trace threshold (BeliefDecay.IsLost,
    /// which had no production callers) as the load-bearing staleness signal. ThreatField,
    /// CostFunction, MPC samplers, etc. dispatch on this single byte instead of reading a
    /// 49-float covariance.
    /// </summary>
    public enum SightState : byte
    {
        Fresh = 0,
        Coasting = 1,
        Stale = 2,
        Lost = 3,
    }

    public static class SightStateExtensions
    {
        /// <summary>True only for Fresh (observed this tick).</summary>
        public static bool IsObserved(this SightState s) => s == SightState.Fresh;

        /// <summary>True only for Lost (>= LostAfterSec out of sight).</summary>
        public static bool IsLost(this SightState s) => s == SightState.Lost;

        /// <summary>True for Coasting or Stale — recent enough to dead-reckon meaningfully.</summary>
        public static bool IsCoasting(this SightState s) => s == SightState.Coasting || s == SightState.Stale;
    }
}
