using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P10 [ARMOR-PARITY] one-shot catcher (REV 7 C4). When
    /// <see cref="ArmorMapPolicy.UseRealSpecHP"/> flips true, this catcher fires once per
    /// TechId on the next ArmorMap.Compute call to emit a diff vs the v0.1 mass-as-HP
    /// path. Logs `[ARMOR-PARITY]` file-only with total-HP delta. Spawn-test gate (P3.1)
    /// gates the flag-flip commit on user review of these lines.
    /// </summary>
    internal static class ArmorMapParityGate
    {
        private static readonly HashSet<int> _logged = new HashSet<int>();
        private static readonly object _gate = new object();

        // Tolerance for the total-HP delta — beyond this the line carries a [PARITY-DRIFT] tag.
        private const float DriftTolerance = 0.25f;

        /// <summary>
        /// Fires once per techId. Caller passes the v0.2 ArmorMap (computed via the catalog
        /// overload) and a v0.1-equivalent ArmorMap (the mass-as-HP overload on the same poses).
        /// Computes per-axis total-HP and logs the delta + tolerance verdict.
        /// </summary>
        internal static void TryEmitOnce(int techIdValue, ArmorMap vNew, ArmorMap vOld)
        {
            lock (_gate)
            {
                if (_logged.Contains(techIdValue)) return;
                _logged.Add(techIdValue);
            }
            if (vNew == null || vOld == null) return;

            float totalNew = SumAllHp(vNew);
            float totalOld = SumAllHp(vOld);
            float denom = UnityEngine.Mathf.Max(UnityEngine.Mathf.Abs(totalOld), 0.001f);
            float relDelta = UnityEngine.Mathf.Abs(totalNew - totalOld) / denom;
            bool drift = relDelta > DriftTolerance;

            DebugTAC_AI.LogWarnFileOnly(
                key: "armor-parity-" + techIdValue,
                message: (drift ? "[PARITY-DRIFT] " : "") + "[ARMOR-PARITY] techId=" + techIdValue
                    + " v0.2-total=" + totalNew.ToString("F1")
                    + " v0.1-total=" + totalOld.ToString("F1")
                    + " relDelta=" + (relDelta * 100f).ToString("F1") + "%"
                    + " tolerance=" + (DriftTolerance * 100f).ToString("F0") + "%");
        }

        private static float SumAllHp(ArmorMap m)
        {
            float total = 0f;
            for (int x = 0; x < m.GridResolution.x; x++)
                for (int y = 0; y < m.GridResolution.y; y++)
                    for (int z = 0; z < m.GridResolution.z; z++)
                        total += m.CellHP(x, y, z);
            return total;
        }

        /// <summary>
        /// P11 T1: clear per-tech dedup so subsequent <see cref="TryEmitOnce"/> calls re-emit.
        /// Called from <see cref="ArmorMapPolicy"/> setter when UseRealSpecHP flips, so the
        /// next batch of rebuilds catches the new path's drift against the freshly-active flag.
        /// </summary>
        internal static void Reset()
        {
            lock (_gate) _logged.Clear();
        }
    }
}
