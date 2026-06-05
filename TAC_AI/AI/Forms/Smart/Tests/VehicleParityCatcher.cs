using System;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Coordination;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Vehicle;

namespace TAC_AI.AI.Forms.Smart.Tests
{
    /// <summary>
    /// P10 Item 41 / P11 T7 Item 62 (POST-DECOMPILE EVENT-DRIVEN REWRITE).
    ///
    /// Original v0.2 implementation was a 30-second periodic heartbeat that walked
    /// <see cref="ManTechs.IterateTechs"/> but never actually emitted parity — the per-tech
    /// work was a commented-out no-op. The "regression catcher" claim in the Gaps Log was
    /// false.
    ///
    /// P11 rewrites this as an event-driven re-emit hook. The 5 parity gates already fire
    /// once-per-key on the LIVE DISPATCH PATH (wired in P11 T1 Items 44-47):
    ///   - <see cref="ArmorMapParityGate"/> at SmartRuntime.RebuildVehicleSnapshotInternal
    ///   - <see cref="WeaponSpecParityGate"/> at ArchetypeProbe.Probe
    ///   - <see cref="LeadResidualParityGate"/> at WeaponFireController.ComputeFireCommits
    ///   - <see cref="LosCoverageParityGate"/> at TeamRuntime.BuildTeamSnapshot
    ///   - <see cref="IdentityClassifierParityGate"/> at SmartForm.OnTechSpawn
    /// They dedup per-key, so each gate fires exactly once per (tech / target / archetype /
    /// team) per session.
    ///
    /// The remaining "regression catcher" intent — "when the user flips a policy mid-session,
    /// re-emit so the operator sees the drift on EXISTING techs" — is fulfilled by
    /// <see cref="ResetAllGates"/>. Callers flip the policy flag, call ResetAllGates(), and
    /// the gates fire afresh on the next dispatch path through each subsystem.
    ///
    /// A periodic walk would have re-paid the expensive ArmorMap/Classify work on every
    /// 30-second tick for every tech forever — a bad trade for a feature that's only useful
    /// at flip moments. ResetAllGates is O(5) and the next dispatch tick does the rest.
    /// </summary>
    public static class VehicleParityCatcher
    {
        /// <summary>
        /// Master gate. Default ON — the per-gate dedup makes the live dispatch path's
        /// parity emission cheap, so there's no reason to leave the catcher disabled.
        /// Flip false during spawn-test if the [X-PARITY] log noise becomes a problem.
        /// </summary>
        public static bool Enabled = true;

        private static long _resetCount;

        /// <summary>Number of times <see cref="ResetAllGates"/> has been called this session.</summary>
        public static long ResetCount => System.Threading.Volatile.Read(ref _resetCount);

        /// <summary>
        /// Main-thread entry kept for back-compat with the SmartForm.Operations call site.
        /// No longer does periodic work — the regression-catcher contract is now fulfilled
        /// by <see cref="ResetAllGates"/> being called at policy-flip time. This method is
        /// a no-op; the SmartForm.Operations site can be removed in a future cleanup.
        /// </summary>
        public static void MainThreadTick() { /* no-op since P11 T7 Item 62 */ }

        /// <summary>
        /// Clear per-key dedup on every parity gate. The next dispatch path through each
        /// subsystem (vehicle rebuild / weapon fire / classify / team snapshot / probe) will
        /// re-emit. Call this after flipping any of:
        ///   - <see cref="ArmorMapPolicy"/>.UseRealSpecHP
        ///   - <see cref="WeaponSpecPolicy"/>.UseReflectedScalars / UseHonestBarrelGeometry
        ///   - <c>AetherTuning.UseCoastAwareLead</c>
        ///   - <see cref="Coordination.LineOfSightProducer"/>.Enabled
        ///   - any <see cref="SmartIdentityTuning"/> flag
        ///
        /// Idempotent. Safe to call from any thread — each gate's Reset is itself thread-safe.
        /// </summary>
        public static void ResetAllGates()
        {
            if (!Enabled) return;
            try
            {
                ArmorMapParityGate.Reset();
                WeaponSpecParityGate.Reset();
                LeadResidualParityGate.Reset();
                LosCoverageParityGate.Reset();
                IdentityClassifierParityGate.Reset();
                ChassisFixtureGate.Reset();
                System.Threading.Interlocked.Increment(ref _resetCount);
                DebugTAC_AI.LogWarnFileOnly("parity-reset",
                    "[PARITY-RESET] all 6 gate dedup states cleared — next dispatch path through each "
                    + "subsystem will re-emit [X-PARITY] log lines. resetCount=" + ResetCount);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("parity-reset-err",
                    "VehicleParityCatcher.ResetAllGates threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Reset session counters. Called from SmartRuntime.Init.</summary>
        public static void ResetSession()
        {
            System.Threading.Interlocked.Exchange(ref _resetCount, 0L);
        }
    }
}
