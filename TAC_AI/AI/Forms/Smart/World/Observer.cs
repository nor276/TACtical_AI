using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Write path for noiseless main-thread observations. Per AETHER-DESIGN.md "In-sight model".
    ///
    /// Single entry point: <see cref="Submit"/>. Called from SmartForm.ObserveWorldTechsIfDue
    /// on the main thread. Publishes the raw observation to the per-tech intake slot via the
    /// seqlock primitive in <see cref="ObservationIntakeSlot.Write"/>. The AetherFuser worker
    /// drains intake slots on its next tick.
    ///
    /// NaN/Inf observations are rejected at submission (matches the existing
    /// WorldModel.RecordObservation guard — physics-broken rbody.velocity is the typical
    /// source). Rejected observations leave the prior trace ageing naturally; no covariance
    /// to poison.
    ///
    /// Velocity smoothing (D2) is NOT applied here — Observer.Submit publishes raw values.
    /// AetherFuser applies smoothing (if AetherTuning.VelocitySmoothingStrength > 0) when
    /// it constructs the fresh-observed BeliefState, where the prior trace is available
    /// without re-reading the slot.
    /// </summary>
    public static class Observer
    {
        /// <summary>
        /// Submit a noiseless main-thread observation. Single-writer per tech (the main thread).
        /// </summary>
        public static void Submit(ObservationIntake intake, TechId id,
            Vector3 position, Vector3 velocity, Vector3 forward, long tickMono)
        {
            if (intake == null) return;
            if (NotFinite(position) || NotFinite(velocity) || NotFinite(forward)) return;
            var slot = intake.GetOrCreate(id);
            slot.Write(position, velocity, forward, tickMono);
        }

        private static bool NotFinite(Vector3 v)
        {
            return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);
        }
    }
}
