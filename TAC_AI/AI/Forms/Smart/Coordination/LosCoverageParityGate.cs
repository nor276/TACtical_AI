using System.Collections.Concurrent;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// P10 [LOS-PARITY] one-shot catcher (REV 7 C4). When
    /// <see cref="LineOfSightProducer.Enabled"/> flips true, this catcher fires once per
    /// TeamId on the next TeamRuntime.BuildTeamSnapshot to emit the size of the data-driven
    /// LOS dict vs the v0.1 always-empty placeholder. Logs `[LOS-PARITY]` file-only.
    /// Spawn-test gate (P5.1) gates the flag-flip commit on user review.
    ///
    /// Per REV 3 S1: at v0.1 every tech got `losCoverage[kv.Key] = new List&lt;TechId&gt;()` →
    /// RoleAssignment.NeedsScout returned TRUE-always. With LineOfSightProducer.Enabled=true
    /// the dict gets real per-tech LOS lists from the producer's published snapshot.
    /// Parity catcher reports the per-tech-coverage-count distribution so the user can see
    /// "X techs covered Y enemies on average" instead of "everything zeroes out."
    /// </summary>
    internal static class LosCoverageParityGate
    {
        private static readonly ConcurrentDictionary<int, byte> _logged = new ConcurrentDictionary<int, byte>();

        /// <summary>
        /// Fires once per teamIdValue. Caller passes the per-tech-LOS-count list (e.g.
        /// number of enemies each friendly currently sees) — catcher reports total +
        /// average + zero-count techs (which would route to Role.Scout in v0.2).
        /// </summary>
        internal static void TryEmitOnce(int teamIdValue, int techCount,
            int totalEnemiesSeen, int techsWithZeroCoverage)
        {
            if (!_logged.TryAdd(teamIdValue, 1)) return;

            float avgCoverage = techCount > 0 ? (float)totalEnemiesSeen / techCount : 0f;
            DebugTAC_AI.LogWarnFileOnly(
                key: "los-parity-" + teamIdValue,
                message: "[LOS-PARITY] teamId=" + teamIdValue
                    + " techs=" + techCount
                    + " totalCoverage=" + totalEnemiesSeen
                    + " avg=" + avgCoverage.ToString("F2") + "/tech"
                    + " zeroCoverageTechs=" + techsWithZeroCoverage
                    + " (v0.1-baseline: all techs zero → NeedsScout=TRUE-always)");
        }

        /// <summary>P11 T1: clear per-team dedup so next BuildTeamSnapshot re-emits per team.</summary>
        internal static void Reset()
        {
            _logged.Clear();
        }
    }
}
