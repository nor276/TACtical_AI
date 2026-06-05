using TerraTechETCUtil;  // RawTechBase

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P2 Item 6: FanJet spool-state probe via the cached <c>RawTechBase.spinDat</c>
    /// FieldInfo (the same handle the production code uses at AIECore.cs:600 and
    /// Enemy/RCore.cs:277). Threshold 10 mirrors the production "fan is spun up" gate.
    /// </summary>
    internal static class SpinDatProbe
    {
        // Production spool threshold — taken from AIECore.cs:600 + RCore.cs:277.
        // The expression `(float)RawTechBase.spinDat.GetValue(jet) <= 10` reads spool-down;
        // we invert (> 10 means spooled up).
        private const float SpoolUpThreshold = 10f;

        /// <summary>
        /// Returns the raw spool value normalized into [0, 1] approximating "spool fraction."
        /// Below the threshold the value can climb very fast on engine update; we clamp to 1
        /// once over the threshold so consumers can treat &gt;= 1 as "fully spooled."
        /// Returns false on reflection miss; caller's 'out' is set to 0.
        /// </summary>
        internal static bool TryReadSpoolFraction(FanJet jet, out float spoolFraction)
        {
            spoolFraction = 0f;
            if (jet == null) return false;
            try
            {
                float raw = (float)RawTechBase.spinDat.GetValue(jet);
                spoolFraction = raw <= 0f
                    ? 0f
                    : (raw >= SpoolUpThreshold ? 1f : raw / SpoolUpThreshold);
                return true;
            }
            catch
            {
                spoolFraction = 0f;
                return false;
            }
        }

        /// <summary>
        /// Convenience predicate matching production semantics: true iff the fan is
        /// considered "spun up" enough to commit thrust. Falls to false on reflection miss.
        /// </summary>
        internal static bool IsSpoolUp(FanJet jet)
        {
            if (jet == null) return false;
            try
            {
                return (float)RawTechBase.spinDat.GetValue(jet) > SpoolUpThreshold;
            }
            catch
            {
                return false;
            }
        }
    }
}
