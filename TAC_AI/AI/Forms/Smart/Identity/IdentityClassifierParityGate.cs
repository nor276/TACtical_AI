using System.Collections.Concurrent;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    /// <summary>
    /// P10 [IDENT-PARITY] one-shot catcher (REV 7 C4). When any
    /// <see cref="SmartIdentityTuning"/> flag flips true, this catcher fires once per tech
    /// on the next classification to emit the v0.1 → v0.2 identity reroute. Logs
    /// `[IDENT-PARITY]` file-only with the old/new identity pair. Spawn-test gate (P6.1)
    /// gates the flag-flip commit on user review.
    ///
    /// Per REV 7 P6: with all SmartIdentityTuning flags=false, Row 6 (RepairSupport),
    /// Row 6.5 (Patrol), and Row 2.5 (HarvesterModulesForGatherer) are skipped → v0.1
    /// classification preserved bit-identically. With a flag flipped true, some techs that
    /// today classify as Hunter reroute to the new identity — this catcher reports each
    /// reroute exactly once per tech so the user can verify expected re-routes happened.
    /// </summary>
    internal static class IdentityClassifierParityGate
    {
        private static readonly ConcurrentDictionary<int, byte> _logged = new ConcurrentDictionary<int, byte>();

        /// <summary>
        /// Fires once per techIdValue when the v0.2 identity differs from v0.1. Caller
        /// passes both (computed by running Classify once with all flags off and once with
        /// current flags) — catcher dedups and logs the reroute.
        /// </summary>
        internal static void TryEmitOnce(int techIdValue,
            SmartIdentity v01Identity, SmartIdentity v02Identity, string techName)
        {
            if (v01Identity == v02Identity) return;   // no reroute, no log
            if (!_logged.TryAdd(techIdValue, 1)) return;

            DebugTAC_AI.LogWarnFileOnly(
                key: "ident-parity-" + techIdValue,
                message: "[IDENT-PARITY] tech='" + (techName ?? "<unnamed>") + "' techId=" + techIdValue
                    + " v0.1=" + v01Identity
                    + " v0.2=" + v02Identity
                    + " (reroute caused by SmartIdentityTuning flag flip)");
        }

        /// <summary>P11 T1: clear per-tech dedup so next classify re-emits per tech.</summary>
        internal static void Reset()
        {
            _logged.Clear();
        }
    }
}
