using System.Collections.Generic;
using TAC_AI.AI;
using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning.Features;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Vehicle
{
    // THE one hard-correct. A nosediving Aviator destroys itself and poisons the S4 signal
    // (environmental death misreads as a KillScored). 3 s sustained window (1.5 s detect +
    // 1.5 s confirm). Detection only - the actuator + mailbox writer live in their own files.
    //
    // Returns Fired=false ALWAYS: §9.8 says GroundedAircraft does NOT emit an IdentityOutcome.
    // On a confirmed pathology it calls GuardCorrectiveActuator.RaiseGroundedAircraftRecovery
    // directly so the worker's generic rising-edge IdentityOutcome publish never runs for it.
    public sealed class GroundedAircraftGuard : IBehaviorGuard
    {
        public const float AltitudeFloorM = 2.0f;
        public const float DescendSpeedYThreshold = -3.0f;
        public const float PitchDownThresholdDeg = -45.0f;
        public const float DetectSec = 1.5f;
        public const float ConfirmSec = 1.5f;
        public const float SpawnGraceSec = 10.0f;
        public const float ReturnToBaseRadiusM = 30.0f;
        public const float StrafeTargetMaxDistM = 80.0f;

        // Bit flags for the GuardEvaluationRequest - terrain height at helper pos.
        private const ushort ReqTerrainAtSelf = 0x0001;

        private sealed class TechWindow
        {
            public long FirstSeenMono;
            public long DetectStartMono;  // 0 = not currently in pathology
            public bool Confirmed;        // already fired this episode
            public long LastRequestMono;  // key of the terrain request emitted last tick
        }

        private static readonly Dictionary<int, TechWindow> _windows = new Dictionary<int, TechWindow>();

        private static readonly SmartIdentity[] _identities = new[]
        {
            SmartIdentity.AircraftHunter, SmartIdentity.AircraftSupport,
        };

        public string Name => "GroundedAircraft";
        public GuardSeverity Severity => GuardSeverity.HardCorrect;
        public int WindowSec => 3;
        public SmartIdentity[] AppliesToIdentities => _identities;
        // Active anywhere with aircraft, not just S4 - return null so the scenario gate is open.
        public int[] AppliesToScenarios => null;

        public bool ShouldEvaluate(GuardContext ctx)
        {
            if (ctx.Snapshot == null) return false;
            if (ctx.Helper == null || ctx.Tank == null) return false;
            if (ctx.Helper.DediAI != AIType.Aviator) return false;
            // Aviator misclassified as a land hover-miner - observe-only, never correct.
            if (ctx.Helper.AuthoredTerrain == TerraTechETCUtil.BaseTerrain.Land) return false;
            if (ctx.Helper.IsPlayerControlled) return false;
            if (ctx.Helper.AIAlign == AIAlignment.Player) return false;
            return true;
        }

        public GuardVerdict Evaluate(GuardContext ctx)
        {
            var v = new GuardVerdict();
            var snap = ctx.Snapshot;
            int key = ctx.TechId.Value;
            long now = ctx.MonoTick;

            TechWindow win;
            if (!_windows.TryGetValue(key, out win))
            {
                win = new TechWindow { FirstSeenMono = now };
                _windows[key] = win;
            }

            // Spawn-drop grace: a tech just dropping in is not nosediving.
            if (MonoClock.Seconds(win.FirstSeenMono, now) < SpawnGraceSec)
            {
                win.DetectStartMono = 0;
                win.Confirmed = false;
                v.Fired = false;
                return v;
            }

            // Pitch from forward - no marshal needed, ForwardWorld is published main-thread.
            float pitchDeg = Mathf.Asin(Mathf.Clamp(Vector3.Dot(snap.ForwardWorld, Vector3.up), -1f, 1f))
                * Mathf.Rad2Deg;

            // Terrain height comes through the GuardEvaluationRequest round-trip. Read last
            // tick's fulfilled result (keyed by the mono we stamped last tick); enqueue a
            // fresh request for next tick and remember its key.
            float altitudeAboveTerrain;
            bool haveAltitude = TryReadAltitude(ctx, snap, win.LastRequestMono, out altitudeAboveTerrain);
            EnqueueTerrainRequest(ctx);
            win.LastRequestMono = now;
            if (!haveAltitude)
            {
                // No fulfilled terrain result yet (off-tile or first tick) - skip eval.
                v.Fired = false;
                return v;
            }

            bool descending = ctx.MeanRecentSpeedY < DescendSpeedYThreshold;
            bool lowAltitude = altitudeAboveTerrain < AltitudeFloorM;
            bool nosePitch = pitchDeg < PitchDownThresholdDeg;
            bool pathology = lowAltitude && descending && nosePitch;

            if (!pathology)
            {
                win.DetectStartMono = 0;
                win.Confirmed = false;
                v.Fired = false;
                return v;
            }

            // Sane exceptions.
            if (snap.AnchorState != 0)
            {
                win.DetectStartMono = 0;
                v.ExceptionMatched = true; v.ExceptionId = 1; v.Fired = false;
                return v;
            }
            // Strafing / diving on a live ground target - intentional weapons delivery. Use
            // bg-safe attacker-pos snapshot as the combat proxy (lastEnemyGet is main-thread).
            if (snap.ProvokedCountdown > 0 && snap.HasLastAttacker)
            {
                float distToTarget = (snap.LastAttackerPosWorld - snap.PositionWorld).magnitude;
                if (distToTarget < StrafeTargetMaxDistM)
                {
                    win.DetectStartMono = 0;
                    v.ExceptionMatched = true; v.ExceptionId = 2; v.Fired = false;
                    return v;
                }
            }
            // Return-to-base landing: near the spawn anchor AND descending is a landing flare.
            if (ctx.IdentityStamp.SpawnAnchor != Vector3.zero)
            {
                float distAnchor = (snap.PositionWorld - ctx.IdentityStamp.SpawnAnchor).magnitude;
                if (distAnchor < ReturnToBaseRadiusM)
                {
                    win.DetectStartMono = 0;
                    v.ExceptionMatched = true; v.ExceptionId = 3; v.Fired = false;
                    return v;
                }
            }

            // Detect -> confirm windowing. DetectStart latches; fire only after detect+confirm.
            if (win.DetectStartMono == 0)
            {
                win.DetectStartMono = now;
                win.Confirmed = false;
                v.Fired = false;
                return v;
            }
            float sustained = MonoClock.Seconds(win.DetectStartMono, now);
            if (sustained < (DetectSec + ConfirmSec))
            {
                v.Fired = false;
                return v;
            }
            if (win.Confirmed)
            {
                // Already raised this episode; actuator owns the cooldown / anti-loop.
                v.Fired = false;
                return v;
            }
            win.Confirmed = true;

            bool onActiveTarget = snap.ProvokedCountdown > 0 && snap.HasLastAttacker;
            GuardCorrectiveActuator.RaiseGroundedAircraftRecovery(ctx.TechId,
                snap.PositionWorld, snap.ForwardWorld, onActiveTarget);

            // Fired stays false - §9.8: no IdentityOutcome for the hard-correct.
            v.Fired = false;
            v.EvidenceFields = "alt=" + altitudeAboveTerrain.ToString("F1")
                + " vY=" + ctx.MeanRecentSpeedY.ToString("F1") + " pitch=" + pitchDeg.ToString("F0");
            return v;
        }

        private static bool TryReadAltitude(GuardContext ctx, SelfProbeSnapshot snap,
            long lastRequestMono, out float altitude)
        {
            altitude = 0f;
            if (lastRequestMono == 0L) return false; // no request emitted yet
            GuardEvaluationResult result;
            if (!SpawnEnvelopeExecutor.TryTakeGuardResult(ctx.TechId, lastRequestMono, out result))
                return false;
            if (!result.Valid || !result.TerrainHeightOnTile) return false;
            altitude = snap.PositionWorld.y - result.TerrainHeight;
            return true;
        }

        private static void EnqueueTerrainRequest(GuardContext ctx)
        {
            try
            {
                WorldEventBus.PublishFromWorker(
                    new GuardEvaluationRequest(ctx.TechId, ctx.MonoTick, ReqTerrainAtSelf));
            }
            catch { /* bus may be tearing down; skip this tick */ }
        }

        public static void ForgetTech(TechId techId)
        {
            _windows.Remove(techId.Value);
        }
    }
}
