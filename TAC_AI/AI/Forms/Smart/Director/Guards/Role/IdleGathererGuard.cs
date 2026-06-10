using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Role
{
    // Observe-only. Gatherer with no cargo + no progress + reachable chunks nearby.
    // Terrain isolation is a common false-positive surface.
    public sealed class IdleGathererGuard : IBehaviorGuard
    {
        public const float MeanSpeedXZMax = 1.0f;
        public const float ChunkCensusRadiusM = 200f;
        public const int MinReachableChunks = 3;

        private static readonly SmartIdentity[] _identities = new[] { SmartIdentity.Gatherer };
        private static readonly int[] _scenarios = new[]
        {
            (int)ScenarioId.S2, (int)ScenarioId.S5,
        };

        public string Name => "IdleGatherer";
        public GuardSeverity Severity => GuardSeverity.Observe;
        public int WindowSec => 60;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => _scenarios;

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

            // Cargo empty via the bg-safe snapshot.
            bool cargoEmpty = snap.CargoNumContents <= 0;
            if (!cargoEmpty)
            {
                v.Fired = false;
                return v;
            }

            bool slow = ctx.MeanRecentSpeedXZ < MeanSpeedXZMax;
            if (!slow)
            {
                v.Fired = false;
                return v;
            }

            // No BlockDelivered within window — telemetry sidecar field stamped by the
            // publisher path. 0 = never delivered; treat as no event. Interlocked.Read
            // guards the 64-bit field against torn reads on 32-bit JIT.
            long lastDelivered = System.Threading.Interlocked.Read(
                ref ctx.Helper.LastBlockDeliveredMono);
            if (lastDelivered != 0L)
            {
                float ageSec = MonoClock.Seconds(lastDelivered, ctx.MonoTick);
                if (ageSec < WindowSec)
                {
                    v.Fired = false;
                    return v;
                }
            }

            // Sane exceptions FIRST so chronic-suppression doesn't lock on terrain.
            if (ctx.Helper.AIAlign == AIAlignment.Player)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 1;
                v.Fired = false;
                return v;
            }
            if (snap.AnchorState != 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 2;
                v.Fired = false;
                return v;
            }
            // PathingService recent-failure gate — solver already knows the bucket is
            // unreachable; don't fire the guard on that.
            if (PathingService.LastQueryFailedRecently(ctx.TechId, snap.PositionWorld, 30f))
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 3;
                v.Fired = false;
                return v;
            }

            // Reachability gate suffices here. Physics.OverlapSphere chunk-presence walk
            // is main-thread only.
            if (!PathingService.IsReachable(snap.PositionWorld,
                snap.PositionWorld + Vector3.forward * ChunkCensusRadiusM,
                VehicleCapability.Default))
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 4;
                v.Fired = false;
                return v;
            }

            v.Fired = true;
            v.Magnitude = 1f;
            v.EvidenceFields = "cargo=0 meanXZ=" + ctx.MeanRecentSpeedXZ.ToString("F2");
            return v;
        }
    }
}
