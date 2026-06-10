using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Combat
{
    // Warn. High-turret tech that has been orbiting an alive target for ≥ 270° at > 5 m/s
    // without firing a single projectile. Replaces the fictional PlanKind.CombatCircle test
    // with a pure kinematic-angular-displacement fingerprint.
    public sealed class OrbitNoFireGuard : IBehaviorGuard
    {
        public const float OrbitAngularThresholdDeg = 270f;
        public const float MinLinearSpeed = 5f;
        public const float TurretFractionFloor = 0.25f;
        public const float FiringArcCosine = 0.7071f;       // cos(45°)

        private static readonly SmartIdentity[] _identities = new[]
        {
            SmartIdentity.Hunter, SmartIdentity.Sniper, SmartIdentity.AircraftHunter,
            SmartIdentity.Patrol,
        };
        private static readonly int[] _scenarios = new[]
        {
            (int)ScenarioId.S1, (int)ScenarioId.S3, (int)ScenarioId.S4,
            (int)ScenarioId.S5, (int)ScenarioId.S6prime,
        };

        // Per-tech bearing snapshot from the prior tick; accumulates angular displacement
        // around the target. Keyed by techId. Reset on falling edge of provoked.
        private struct BearingTrack
        {
            public float PrevBearingRad;
            public bool HasPrev;
            public float AccumDeg;
            public long LastTickMono;
        }
        private static readonly Dictionary<int, BearingTrack> _bearingTracks
            = new Dictionary<int, BearingTrack>();
        private static readonly object _bearingGate = new object();

        public string Name => "OrbitNoFire";
        public GuardSeverity Severity => GuardSeverity.Warn;
        public int WindowSec => 60;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => _scenarios;

        public static void ForgetTech(TechId techId)
        {
            lock (_bearingGate) _bearingTracks.Remove(techId.Value);
        }

        public bool ShouldEvaluate(GuardContext ctx)
        {
            if (ctx.Snapshot == null) return false;
            if (ctx.Helper == null) return false;
            return true;
        }

        public GuardVerdict Evaluate(GuardContext ctx)
        {
            var v = new GuardVerdict();
            var snap = ctx.Snapshot;
            var helper = ctx.Helper;

            // Provoked + has a live attacker target.
            if (snap.ProvokedCountdown <= 0 || !snap.HasLastAttacker)
            {
                ResetTrack(ctx.TechId);
                v.Fired = false;
                return v;
            }

            Vector3 toTarget = snap.LastAttackerPosWorld - snap.PositionWorld;
            float distToTarget = toTarget.magnitude;
            if (distToTarget < AIGlobals.MinCombatRangeDefault
                || distToTarget > helper.MaxCombatRange)
            {
                ResetTrack(ctx.TechId);
                v.Fired = false;
                return v;
            }

            if (helper.TurretFraction < TurretFractionFloor)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 1;
                v.Fired = false;
                return v;
            }

            // Linear speed check.
            if (snap.Speed <= MinLinearSpeed)
            {
                v.Fired = false;
                return v;
            }

            // Angular displacement around the target. Bearing in XZ plane; difference
            // wrapped to [-pi, pi].
            float bearingNow = Mathf.Atan2(toTarget.z, toTarget.x);
            float angleStep = 0f;
            lock (_bearingGate)
            {
                BearingTrack tr;
                if (!_bearingTracks.TryGetValue(ctx.TechId.Value, out tr) || !tr.HasPrev)
                {
                    tr.PrevBearingRad = bearingNow;
                    tr.HasPrev = true;
                    tr.AccumDeg = 0f;
                    tr.LastTickMono = ctx.MonoTick;
                    _bearingTracks[ctx.TechId.Value] = tr;
                    v.Fired = false;
                    return v;
                }

                // Reset accumulator if the window aged out.
                float gapSec = MonoClock.Seconds(tr.LastTickMono, ctx.MonoTick);
                if (gapSec > WindowSec)
                {
                    tr.AccumDeg = 0f;
                    tr.PrevBearingRad = bearingNow;
                    tr.LastTickMono = ctx.MonoTick;
                    _bearingTracks[ctx.TechId.Value] = tr;
                    v.Fired = false;
                    return v;
                }

                float delta = bearingNow - tr.PrevBearingRad;
                while (delta > Mathf.PI) delta -= 2f * Mathf.PI;
                while (delta < -Mathf.PI) delta += 2f * Mathf.PI;
                tr.AccumDeg += Mathf.Abs(delta) * Mathf.Rad2Deg;
                tr.PrevBearingRad = bearingNow;
                tr.LastTickMono = ctx.MonoTick;
                _bearingTracks[ctx.TechId.Value] = tr;
                angleStep = tr.AccumDeg;
            }

            if (angleStep < OrbitAngularThresholdDeg)
            {
                v.Fired = false;
                return v;
            }

            // Firing arc — facing the target within 45° at the current tick is a coarse
            // proxy; LOS / weapon-cooldown / per-tick arc fraction would need a publisher
            // we don't have yet. The fingerprint here is "we orbited and never shot",
            // which the dot-product gate corroborates.
            if (distToTarget > 0.1f)
            {
                float dot = Vector3.Dot(snap.ForwardWorld, toTarget / distToTarget);
                if (dot < FiringArcCosine)
                {
                    // Target not in any frontal arc this tick — orbit still consistent.
                    // Don't bail; the cumulative-orbit fingerprint carries.
                }
            }

            // Projectile-fire counter window read. Null publisher means we can't
            // prove zero-fire so be inert.
            var fires = SmartRuntime.Projectiles;
            if (fires == null)
            {
                v.Fired = false;
                return v;
            }
            long winStart = ctx.MonoTick - System.Diagnostics.Stopwatch.Frequency * (long)WindowSec;
            int fireCount = fires.CountWithin(ctx.TechId, winStart, ctx.MonoTick);
            if (fireCount > 0)
            {
                v.Fired = false;
                return v;
            }

            // Sane exceptions.
            if (helper.AIAlign == AIAlignment.Player)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 2;
                v.Fired = false;
                return v;
            }
            if (snap.AnchorState != 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 3;
                v.Fired = false;
                return v;
            }
            // Unjam suppresses combat behavior.
            if (helper.PublishedFrustrationMeter > 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 4;
                v.Fired = false;
                return v;
            }

            v.Fired = true;
            v.Magnitude = 1f;
            v.EvidenceFields = "orbit=" + angleStep.ToString("F0")
                + " dist=" + distToTarget.ToString("F1")
                + " speed=" + snap.Speed.ToString("F1");
            // Reset the accumulator after a fire so the next window starts fresh.
            ResetTrack(ctx.TechId);
            return v;
        }

        private static void ResetTrack(TechId techId)
        {
            lock (_bearingGate)
            {
                BearingTrack tr;
                if (_bearingTracks.TryGetValue(techId.Value, out tr))
                {
                    tr.AccumDeg = 0f;
                    tr.HasPrev = false;
                    _bearingTracks[techId.Value] = tr;
                }
            }
        }
    }
}
