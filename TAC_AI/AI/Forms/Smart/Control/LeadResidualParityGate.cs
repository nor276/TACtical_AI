using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Control
{
    /// <summary>
    /// P10 [LEAD-PARITY] one-shot catcher (REV 7 C4). When
    /// <see cref="World.AetherTuning.UseCoastAwareLead"/> flips true, this catcher fires
    /// once per target on the next WeaponFireController lead computation to emit a diff
    /// vs the v0.1 raw-mean lead. Logs `[LEAD-PARITY]` file-only with predicted-position
    /// delta in meters. Spawn-test gate (P4.1) gates the flag-flip commit on user review.
    ///
    /// REV 3 S2: lead math diverges in TWO independent ways — `dt &gt; 0` case (extrapolation
    /// kicks in) and Lost-state case (VelocityAt zeros out). Catcher reports BOTH if
    /// applicable so the user can spot Lost-state regressions independently.
    /// </summary>
    internal static class LeadResidualParityGate
    {
        private static readonly HashSet<int> _logged = new HashSet<int>();
        private static readonly object _gate = new object();

        // Tolerance in meters: predicted-position delta. Beyond this → [PARITY-DRIFT].
        private const float DriftToleranceMeters = 5.0f;

        /// <summary>
        /// Fires once per targetTechId. Caller computes both predicted-position values
        /// (v0.1 raw-mean and v0.2 coast-aware) and passes them. Catcher dedups and logs.
        /// </summary>
        internal static void TryEmitOnce(int targetTechIdValue,
            Vector3 vNewPredicted, Vector3 vOldPredicted,
            float projTimeSec, bool targetIsLost)
        {
            lock (_gate)
            {
                if (_logged.Contains(targetTechIdValue)) return;
                _logged.Add(targetTechIdValue);
            }
            float deltaMeters = (vNewPredicted - vOldPredicted).magnitude;
            bool drift = deltaMeters > DriftToleranceMeters;

            DebugTAC_AI.LogWarnFileOnly(
                key: "lead-parity-" + targetTechIdValue,
                message: (drift ? "[PARITY-DRIFT] " : "") + "[LEAD-PARITY] targetId=" + targetTechIdValue
                    + " v0.2-pred=" + vNewPredicted.ToString("F1")
                    + " v0.1-pred=" + vOldPredicted.ToString("F1")
                    + " delta=" + deltaMeters.ToString("F2") + "m"
                    + " projTime=" + projTimeSec.ToString("F2") + "s"
                    + (targetIsLost ? " [LOST-STATE]" : " [FRESH-STATE]")
                    + " tolerance=" + DriftToleranceMeters.ToString("F1") + "m");
        }

        /// <summary>P11 T1: clear per-target dedup so the next fire commit re-emits per target.</summary>
        internal static void Reset()
        {
            lock (_gate) _logged.Clear();
        }
    }
}
