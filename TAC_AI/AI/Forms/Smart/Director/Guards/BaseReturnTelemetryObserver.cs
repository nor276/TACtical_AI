using System;
using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Telemetry-only sidecar for OverdueDeliveryGuard. The initial IdentityOutcome rides
    // out through BehaviorGuardWorker on Fired=true (using the verdict magnitude); this
    // observer schedules a 30 s kinematic-correlation tracker that emits a SECOND
    // IdentityOutcome scaled by distance-toward-base closed.
    //
    // NEVER touches GuardCorrectiveActuator. NEVER writes the mailbox. NEVER emits a
    // GuardCorrectionInjected outcome. Pure training-signal pipe.
    internal static class BaseReturnTelemetryObserver
    {
        public const float KinematicWindowSec = 30f;

        private struct PendingTracker
        {
            public long EmitMono;
            public TechId BaseTechId;
            public Vector3 BasePosAtEmit;
            public Vector3 TechPosAtEmit;
        }

        private static readonly object _gate = new object();
        private static readonly Dictionary<TechId, PendingTracker> _pending =
            new Dictionary<TechId, PendingTracker>();

        // Stamp the initial-fire pose. Bumps the telemetry counter so digests count the
        // soft-correct fires distinctly from observe/warn buckets.
        public static void Schedule(TechId techId, TechId baseTechId, Vector3 techPos,
            Vector3 basePos)
        {
            GuardTelemetry.RecordSoftCorrectTelemetryFire();
            lock (_gate)
            {
                _pending[techId] = new PendingTracker
                {
                    EmitMono = MonoClock.Now(),
                    BaseTechId = baseTechId,
                    BasePosAtEmit = basePos,
                    TechPosAtEmit = techPos,
                };
            }
        }

        // Poll once per guard tick. When any tracker's age >= 30 s, emit the
        // displacement-toward-base scaling outcome and drop the tracker. Caller passes the
        // current bg-safe pos for the tech.
        public static void PollKinematicCorrelation(TechId techId, Vector3 techPosNow,
            long nowMono)
        {
            PendingTracker tracker;
            bool ready = false;
            lock (_gate)
            {
                if (!_pending.TryGetValue(techId, out tracker)) return;
                float age = MonoClock.Seconds(tracker.EmitMono, nowMono);
                if (age >= KinematicWindowSec)
                {
                    _pending.Remove(techId);
                    ready = true;
                }
            }
            if (!ready) return;

            float distBefore = (tracker.BasePosAtEmit - tracker.TechPosAtEmit).magnitude;
            float distAfter = (tracker.BasePosAtEmit - techPosNow).magnitude;
            float closedMeters = distBefore - distAfter;
            float mag = closedMeters / KinematicWindowSec;
            if (mag < 0f) mag = 0f;
            if (mag > 1f) mag = 1f;

            byte source = GuardRegistry.BuildSource((int)GuardId.OverdueDelivery, false);
            var outcome = new IdentityOutcome(techId, SmartIdentity.Gatherer,
                OutcomeKind.GuardViolation_Role, mag, source);
            try { WorldEventBus.PublishFromWorker(outcome); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("base-return-kinematic",
                    "[AIWARN] BaseReturnTelemetryObserver.Poll "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            GuardTelemetry.RecordSoftCorrectKinematicCorrelation();
        }

        public static void ForgetTech(TechId techId)
        {
            lock (_gate) _pending.Remove(techId);
        }

        public static void Clear()
        {
            lock (_gate) _pending.Clear();
        }
    }
}
