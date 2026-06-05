using System.Threading;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P2 Item 3 / P11 T6 Item 60 (POST-DECOMPILE RESHAPE): ModuleHover max-force reader.
    ///
    /// Original v0.2 implementation guessed at 4 candidate field names (`m_MaxLiftForce` /
    /// `maxLiftForce` / `m_MaxForce` / `m_LiftSpring`) — NONE of which exist on
    /// <see cref="ModuleHover"/>. Decompile of
    /// <c>F:\Tac-AI\Decompiled\AssemblyCSharp\ModuleHover.cs</c> verifies the actual
    /// abstraction: a private float <c>m_HoverPower</c> (default 0.5f, range 0-1) plus the
    /// public <c>HoverPower</c> property. The lift "force" isn't a single scalar on
    /// ModuleHover — actual lift is produced by <c>BoosterJet</c> sub-components whose
    /// per-jet m_MaxForce is multiplied by m_HoverPower at <c>BoosterJet.InitHoverPower</c>.
    ///
    /// This reshape returns a force estimate by reading <c>HoverPower</c> directly (verified
    /// public property — no reflection needed) and combining with the 2g baseline fallback.
    /// A full per-jet sum would require iterating <c>tank.blockman</c> and is out of scope
    /// at probe time (we don't have the live block list); follow-up Item 60.1 is a
    /// <c>BoosterJetProbe</c> decompile pass on BoosterJet's per-jet m_MaxForce field.
    ///
    /// Discovery logging: one <c>[HOVER-PROBE]</c> file-only line on first call per process
    /// so the operator can verify the new path is firing instead of silently using the
    /// 2g placeholder.
    /// </summary>
    internal static class HoverReflection
    {
        private static int _loggedOnce;

        /// <summary>
        /// Returns a hover-lift estimate. Uses verified <c>HoverPower</c> when present;
        /// falls back to <paramref name="fallback2g"/> when ModuleHover is null. NEVER weaker
        /// than v0.1 — fallback path matches v0.1 ArchetypeProbe behavior bit-identically.
        /// </summary>
        internal static float ReadMaxForce(ModuleHover hover, float fallback2g)
        {
            if (hover == null) return fallback2g;

            // HoverPower is a 0-1 normalized scalar (verified field at ModuleHover.cs:49).
            // Scale the 2g baseline by (0.5 + HoverPower) so a HoverPower=0 module reports
            // half-strength (degenerate hover blocks still contribute *some* lift estimate)
            // and HoverPower=1 reports 150% of baseline. This is an approximation — the real
            // lift comes from per-jet sums — but it tracks the design intent and is honest
            // about the surface available without per-jet plumbing.
            float power = hover.HoverPower;
            if (power < 0f) power = 0f;
            if (power > 1f) power = 1f;
            float estimate = fallback2g * (0.5f + power);

            if (Interlocked.CompareExchange(ref _loggedOnce, 1, 0) == 0)
            {
                DebugTAC_AI.LogWarnFileOnly("hover-probe",
                    "[HOVER-PROBE] ModuleHover detected: HoverPower=" + power.ToString("F2")
                    + " fallback2g=" + fallback2g.ToString("F1")
                    + " estimate=" + estimate.ToString("F1")
                    + " (P11 T6 Item 60 — verified field; per-jet BoosterJet probe = Item 60.1 follow-up)");
            }
            return estimate;
        }
    }
}
