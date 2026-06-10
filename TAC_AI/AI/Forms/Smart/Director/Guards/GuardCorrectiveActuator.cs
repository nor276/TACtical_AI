using System;
using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Central actuator for the ONE hard-correct in the design (GroundedAircraft). The only
    // RaiseX surface. Receives a main-thread-published (boundsCentre, forward) snapshot from
    // the firing guard - it NEVER reads tank.boundsCentreWorld itself off the bg thread.
    //
    // State machine per tech: Active (5 s ceiling) -> RampingDown (2 s) -> Cooldown (15 s)
    // -> Idle. Anti-loop: >= 3 fires in 90 s -> 5 min suppression + [GUARD-AIRCRAFT-CHRONIC].
    public static class GuardCorrectiveActuator
    {
        public const double RecoveryCeilingSec = 5.0;
        public const double RampDownSec = 2.0;
        public const double CooldownSec = 15.0;
        public const double AntiLoopWindowSec = 90.0;
        public const int AntiLoopFireCeiling = 3;
        public const double SuppressionSec = 300.0;
        public const float ClimbRate = 6.0f;
        public const float RecoveryAltitudeGain = 30f;

        private sealed class TechCorrection
        {
            public long ActiveUntilMono;     // 0 = not active
            public long RampUntilMono;        // 0 = no ramp scheduled
            public long CooldownUntilMono;    // re-fire blocked before this
            public long SuppressedUntilMono;  // 5 min chronic suppression
            public long FirstFireInWindowMono;
            public int FiresInWindow;
            public bool RampHandoffWritten;
        }

        private static readonly object _gate = new object();
        private static readonly Dictionary<int, TechCorrection> _byTech =
            new Dictionary<int, TechCorrection>();

        // Called by GroundedAircraftGuard on a confirmed fire. position + forward are the
        // main-thread snapshot the guard already pulled from GuardEvaluationResult /
        // SelfProbeSnapshot. onActiveTarget = strafing-mode-collapse telemetry hint.
        public static void RaiseGroundedAircraftRecovery(TechId techId, Vector3 currentBoundsCentre,
            Vector3 currentForward, bool onActiveTarget)
        {
            long now = MonoClock.Now();
            int key = techId.Value;
            bool fire = false;
            lock (_gate)
            {
                TechCorrection tc;
                if (!_byTech.TryGetValue(key, out tc))
                {
                    tc = new TechCorrection();
                    _byTech[key] = tc;
                }

                if (now < tc.SuppressedUntilMono) return;
                if (now < tc.CooldownUntilMono) return;
                if (tc.ActiveUntilMono != 0 && now < tc.ActiveUntilMono) return; // already correcting

                // Anti-loop window accounting.
                if (tc.FirstFireInWindowMono == 0L
                    || MonoClock.Seconds(tc.FirstFireInWindowMono, now) > (float)AntiLoopWindowSec)
                {
                    tc.FirstFireInWindowMono = now;
                    tc.FiresInWindow = 0;
                }
                tc.FiresInWindow++;
                if (tc.FiresInWindow >= AntiLoopFireCeiling)
                {
                    tc.SuppressedUntilMono = now + MonoClock.FromSeconds(SuppressionSec);
                    tc.ActiveUntilMono = 0;
                    tc.RampUntilMono = 0;
                    GuardTelemetry.RecordOscillation();
                    DirectorJournal.Append("GuardAircraftChronic",
                        "tech=" + key + " oscill=" + tc.FiresInWindow + " suppress=" + (int)SuppressionSec + "s");
                    DebugTAC_AI.LogWarnFileOnly("guard-aircraft-chronic-" + key,
                        "[GUARD-AIRCRAFT-CHRONIC]:tech=" + key + " oscill=" + tc.FiresInWindow);
                    return;
                }

                tc.ActiveUntilMono = now + MonoClock.FromSeconds(RecoveryCeilingSec);
                tc.RampUntilMono = 0;
                tc.RampHandoffWritten = false;
                fire = true;
            }

            if (!fire) return;

            if (onActiveTarget) GuardTelemetry.RecordHardCorrectOnActiveTarget();

            Vector3 goalPos = new Vector3(currentBoundsCentre.x,
                currentBoundsCentre.y + RecoveryAltitudeGain, currentBoundsCentre.z);
            float heading = HeadingFromForward(currentForward);
            long expires = now + MonoClock.FromSeconds(RecoveryCeilingSec);
            GroundedAircraftRecoveryGoalSource.Write(techId, goalPos, heading, expires,
                GuardGoalKind.HoverToAltitude);

            DirectorJournal.Append("GuardCorrectionApplied",
                "guard=GroundedAircraft tech=" + key + " kind=HoverToAltitude alt=+" + (int)RecoveryAltitudeGain);
        }

        // Driven by BehaviorGuardWorker.Tick (1 Hz). Nulls expired slots, writes the 2 s
        // MaintainAltitude ramp-down handoff once, then drops into cooldown. Wrapped in
        // try/finally so an exception mid-sweep never leaves a stale slot pointing at a
        // 5-s-old goal.
        public static void SweepExpired()
        {
            long now = MonoClock.Now();
            List<int> ramps = null;
            List<int> releases = null;
            try
            {
                lock (_gate)
                {
                    foreach (var kv in _byTech)
                    {
                        var tc = kv.Value;
                        if (tc.ActiveUntilMono != 0 && now >= tc.ActiveUntilMono)
                        {
                            // Ceiling hit. Schedule ramp-down once, then enter cooldown.
                            if (!tc.RampHandoffWritten)
                            {
                                tc.RampHandoffWritten = true;
                                tc.RampUntilMono = now + MonoClock.FromSeconds(RampDownSec);
                                tc.CooldownUntilMono = now + MonoClock.FromSeconds(RampDownSec + CooldownSec);
                                tc.ActiveUntilMono = 0;
                                if (ramps == null) ramps = new List<int>();
                                ramps.Add(kv.Key);
                            }
                        }
                        else if (tc.RampUntilMono != 0 && now >= tc.RampUntilMono)
                        {
                            tc.RampUntilMono = 0;
                            if (releases == null) releases = new List<int>();
                            releases.Add(kv.Key);
                        }
                    }
                }
            }
            finally
            {
                if (ramps != null)
                {
                    for (int i = 0; i < ramps.Count; i++)
                    {
                        var tid = new TechId(ramps[i]);
                        // Hand off to a low-priority MaintainAltitude slot for a smooth 2 s
                        // window instead of an instant Goal.None cliff.
                        GroundedAircraftRecoveryGoalSource.WriteRampDown(tid,
                            now + MonoClock.FromSeconds(RampDownSec));
                        DirectorJournal.Append("GuardCorrectionReleased",
                            "guard=GroundedAircraft tech=" + ramps[i] + " phase=ramp-down");
                    }
                }
                if (releases != null)
                {
                    for (int i = 0; i < releases.Count; i++)
                    {
                        var tid = new TechId(releases[i]);
                        GroundedAircraftRecoveryGoalSource.Clear(tid);
                        DirectorJournal.Append("GuardCorrectionReleased",
                            "guard=GroundedAircraft tech=" + releases[i] + " phase=idle");
                    }
                }
            }
        }

        // Clear every per-tech mailbox slot. Called by rollback step 6.5 + Director shutdown.
        public static void ForceReleaseAll()
        {
            lock (_gate)
            {
                foreach (var kv in _byTech)
                {
                    var tid = new TechId(kv.Key);
                    try { GroundedAircraftRecoveryGoalSource.Clear(tid); }
                    catch { /* best-effort during teardown */ }
                    var tc = kv.Value;
                    tc.ActiveUntilMono = 0;
                    tc.RampUntilMono = 0;
                    tc.RampHandoffWritten = false;
                }
            }
        }

        public static void Reset()
        {
            lock (_gate) _byTech.Clear();
        }

        private static float HeadingFromForward(Vector3 fwd)
        {
            Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
            if (flat.sqrMagnitude < 1e-6f) return 0f;
            return Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        }
    }
}
