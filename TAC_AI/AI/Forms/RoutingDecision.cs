namespace TAC_AI.AI.Forms
{
    /// <summary>
    /// L-008: per-tech routing record stamped by <see cref="AIFormRegistry"/>.RouteTech
    /// exactly once per spawn (success path) or in catch (failure path). Carried on
    /// TankAIHelper.Routing. After DelayedSubscribe FormId must be non-null;
    /// RoutingCompletenessCatcher (L-081) scans for null entries every 5s and self-heals.
    /// </summary>
    public struct RoutingDecision
    {
        /// <summary>Form id that claimed the tech ("Smart" / "Modified" / "Vanilla" / "Basic" / null=unrouted).</summary>
        public string FormId;
        /// <summary>Environment.TickCount when the routing was stamped. 0 = never stamped.</summary>
        public int TimestampMs;
        /// <summary>
        /// Why this form claimed the tech. Used by the routing debug overlay (L-053) and
        /// by file-only [TECH-ROUTE] log analysis. Free-form string; common values:
        ///   "Subscribed"            — initial stamp at Subscribe before DelayedSubscribe runs (FormId=null)
        ///   "DelayedSubscribe"      — happy path, full setup completed
        ///   "HandlingDetermineFailed" — HandlingDetermine threw; fell back to Tank driver
        ///   "OnTechSpawnFailed"     — active form's OnTechSpawn threw; fell back to DefaultFormId
        ///   "Reclaimed"             — OnPreUpdate reclaim path picked up a dropped Invoke
        ///   "FormSwapped"           — AIFormRegistry.SetActive drained + re-routed
        /// </summary>
        public string Reason;
        /// <summary>Exception type when Reason ends in "Failed"; null otherwise.</summary>
        public string ExceptionType;
        /// <summary>Exception message when Reason ends in "Failed"; null otherwise.</summary>
        public string ExceptionMessage;
    }
}
