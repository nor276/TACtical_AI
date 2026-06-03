using System;
using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Smart-internal tech identifier. Prefers the TerraTech net-replicated identifier
    /// (<c>tank.netTech.netId.Value</c>) when networked so the SAME replicated tank has
    /// the same TechId on host and every client. Falls back to <c>Tank.GetInstanceID()</c>
    /// in single-player. Per FIX-PLAN.md Phase 5 + AUDIT-R2 §1.R2-E.
    /// </summary>
    public readonly struct TechId : IEquatable<TechId>
    {
        public readonly int Value;
        public TechId(int value) { Value = value; }
        public bool Equals(TechId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TechId t && Equals(t);
        public override int GetHashCode() => Value;
        public override string ToString() => "Tech#" + Value;
        public static bool operator ==(TechId a, TechId b) => a.Value == b.Value;
        public static bool operator !=(TechId a, TechId b) => a.Value != b.Value;

        /// <summary>
        /// Build a TechId from a Tank. Uses <c>netTech.netId.Value</c> when networked
        /// and non-zero (cross-machine stable). In single-player the netId is typically
        /// zero — the fallback to <see cref="UnityEngine.Object.GetInstanceID"/> is
        /// process-local but unique within the session, which is all SP needs.
        /// </summary>
        public static TechId FromTank(Tank tank)
        {
            if (tank == null) return default(TechId);
            try
            {
                if (ManNetwork.IsNetworked && tank.netTech != null)
                {
                    int netId = (int)tank.netTech.netId.Value;
                    if (netId != 0) return new TechId(netId);
                }
            }
            catch { /* netTech may not be ready; fall through */ }
            return new TechId(tank.GetInstanceID());
        }
    }

    /// <summary>Smart-internal team identifier.</summary>
    public readonly struct TeamId : IEquatable<TeamId>
    {
        public readonly int Value;
        public TeamId(int value) { Value = value; }
        public bool Equals(TeamId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TeamId t && Equals(t);
        public override int GetHashCode() => Value;
        public override string ToString() => "Team#" + Value;
        public static bool operator ==(TeamId a, TeamId b) => a.Value == b.Value;
        public static bool operator !=(TeamId a, TeamId b) => a.Value != b.Value;
    }

    /// <summary>
    /// Intent categories per WORLD-CONTRACT §2.2. Stable indices; consumers reference by index.
    /// Aether v0.1: intent moved off BeliefState. The 6-category surface is preserved here
    /// for OpponentIntentClassifier (which still uses Count as its softmax output dim).
    /// v0.2: per-tech intent producer wires through a sidecar IntentRegistry.
    /// </summary>
    public static class IntentCategories
    {
        public const int Aggressing = 0;
        public const int Retreating = 1;
        public const int Flanking = 2;
        public const int Repositioning = 3;
        public const int Holding = 4;
        public const int Idle = 5;
        public const int Count = 6;
    }

    /// <summary>
    /// Per-tech belief — Aether shape. Per AETHER-DESIGN.md (REV 3.1).
    ///
    /// Replaces the prior 7-mean + 49-cov + 6-intent shape. The 7×7 covariance is gone
    /// (carried no information any consumer read). Intent is gone (no live producer; sidecar
    /// in v0.2). The new state is a noiseless observation snapshot plus closed-form coast
    /// extrapolation.
    ///
    /// SHAPE:
    ///   Anchors (last-observed values, frozen until next observation):
    ///     LastSeenPosition / LastSeenVelocity / LastSeenForward / LastObservedTickMono /
    ///     SpeedAtObserve / MaxAccelerationEstimate
    ///   Sight + age:
    ///     Sight (categorical) / PublishedTickMono / AgeSeconds / UncertaintyMeters
    ///   Pre-baked at publish (field-load reads for hot consumers):
    ///     PositionMean / VelocityMean / ForwardXZ / HeadingMean
    ///
    /// CONSTRUCTION: only via <see cref="BeliefStateFactory"/> — the constructor is internal.
    /// </summary>
    public sealed class BeliefState
    {
        public readonly TechId Id;
        public readonly TeamId Team;

        // Anchors — last-observed values, frozen until next observation.
        public readonly Vector3 LastSeenPosition;
        public readonly Vector3 LastSeenVelocity;
        public readonly Vector3 LastSeenForward;
        public readonly long    LastObservedTickMono;
        public readonly long    PublishedTickMono;
        public readonly float   SpeedAtObserve;
        public readonly float   MaxAccelerationEstimate;
        public readonly SightState Sight;

        // Pre-baked at publish — reads are field loads, not function calls.
        public readonly Vector3 PositionMean;
        public readonly Vector3 VelocityMean;
        public readonly Vector3 ForwardXZ;          // XZ-normalized forward; preserves trig roundtrip
        public readonly float   HeadingMean;        // Atan2(ForwardXZ.x, ForwardXZ.z), cached
        public readonly float   AgeSeconds;
        public readonly float   UncertaintyMeters;

        // -- Compat surface — preserved API consumers use today --
        /// <summary>True only for fresh observations (Sight == Fresh).</summary>
        public bool InSight => Sight == SightState.Fresh;

        /// <summary>Monotonic-tick alias of LastObservedTickMono (preserves the existing accessor name).</summary>
        public long LastObservedTick => LastObservedTickMono;

        /// <summary>
        /// Compat shim. Today's consumers read <c>pv.x + pv.z</c> (SmartRuntime.SummarizeBelief)
        /// expecting an m² quantity monotone in age. Returns <c>(U², U², U²)</c> so every
        /// per-axis arithmetic pattern produces a monotone-in-U scalar:
        ///   <c>pv.x + pv.z = 2·U²</c>; <c>pv.x + pv.y + pv.z = 3·U²</c>.
        /// </summary>
        public Vector3 PositionVariance => new Vector3(
            UncertaintyMeters * UncertaintyMeters,
            UncertaintyMeters * UncertaintyMeters,
            UncertaintyMeters * UncertaintyMeters);

        internal BeliefState(
            TechId id, TeamId team,
            Vector3 lastSeenPosition, Vector3 lastSeenVelocity, Vector3 lastSeenForward,
            long lastObservedTickMono, long publishedTickMono,
            float speedAtObserve, float maxAccelerationEstimate, SightState sight,
            Vector3 positionMean, Vector3 velocityMean, Vector3 forwardXZ, float headingMean,
            float ageSeconds, float uncertaintyMeters)
        {
            Id = id;
            Team = team;
            LastSeenPosition = lastSeenPosition;
            LastSeenVelocity = lastSeenVelocity;
            LastSeenForward = lastSeenForward;
            LastObservedTickMono = lastObservedTickMono;
            PublishedTickMono = publishedTickMono;
            SpeedAtObserve = speedAtObserve;
            MaxAccelerationEstimate = maxAccelerationEstimate;
            Sight = sight;
            PositionMean = positionMean;
            VelocityMean = velocityMean;
            ForwardXZ = forwardXZ;
            HeadingMean = headingMean;
            AgeSeconds = ageSeconds;
            UncertaintyMeters = uncertaintyMeters;
        }

        // -- Coast-aware accessors. dt computed via MonoClock.TickFreq internally. --

        /// <summary>
        /// Position extrapolated to <paramref name="nowMono"/>. Lost-state clamp: returns
        /// LastSeenPosition (no extrapolation past LostAfterSec).
        /// </summary>
        public Vector3 PositionAt(long nowMono)
        {
            if (Sight == SightState.Lost) return LastSeenPosition;
            float dt = MonoClock.Seconds(LastObservedTickMono, nowMono);
            if (dt <= 0f) return PositionMean;
            return DeadReckon.ExtrapolatePosition(LastSeenPosition, LastSeenVelocity, SpeedAtObserve, dt);
        }

        /// <summary>
        /// Velocity extrapolated to <paramref name="nowMono"/>. Lost-state clamp: returns
        /// Vector3.zero (no extrapolation past LostAfterSec).
        /// </summary>
        public Vector3 VelocityAt(long nowMono)
        {
            if (Sight == SightState.Lost) return Vector3.zero;
            float dt = MonoClock.Seconds(LastObservedTickMono, nowMono);
            if (dt <= 0f) return VelocityMean;
            return DeadReckon.ExtrapolateVelocity(LastSeenVelocity, dt);
        }

        /// <summary>
        /// Confidence in [0, 1]. 1.0 Fresh; linear 1→0 between StaleAfterSec and LostAfterSec;
        /// 0 if Lost.
        /// </summary>
        public float ConfidenceAt(long nowMono)
        {
            if (Sight == SightState.Lost) return 0f;
            if (Sight == SightState.Fresh) return 1f;
            float dt = MonoClock.Seconds(LastObservedTickMono, nowMono);
            return DeadReckon.ConfidenceAt(dt);
        }
    }

    /// <summary>
    /// Construction surface for <see cref="BeliefState"/>. Per AETHER-DESIGN.md "Per-tech state shape".
    /// All BeliefState instances are built through one of these three factories; the constructor
    /// is internal.
    /// </summary>
    public static class BeliefStateFactory
    {
        /// <summary>
        /// First observation or freshly-observed-after-gap. Preserves the name of the
        /// previous shape's static factory so callers don't rename.
        /// </summary>
        public static BeliefState NewlyObserved(TechId id, TeamId team,
            Vector3 pos, Vector3 vel, Vector3 fwd, float maxAccel, long tickMono)
        {
            Vector3 fwdXZ = new Vector3(fwd.x, 0f, fwd.z);
            if (fwdXZ.sqrMagnitude < 1e-6f) fwdXZ = Vector3.forward;
            else fwdXZ = fwdXZ.normalized;
            float heading = Mathf.Atan2(fwdXZ.x, fwdXZ.z);
            float speed = vel.magnitude;

            return new BeliefState(
                id, team,
                lastSeenPosition: pos,
                lastSeenVelocity: vel,
                lastSeenForward: fwd,
                lastObservedTickMono: tickMono,
                publishedTickMono: tickMono,
                speedAtObserve: speed,
                maxAccelerationEstimate: maxAccel,
                sight: SightState.Fresh,
                positionMean: pos,
                velocityMean: vel,
                forwardXZ: fwdXZ,
                headingMean: heading,
                ageSeconds: 0f,
                uncertaintyMeters: DeadReckon.BaseObservationUncertainty);
        }

        /// <summary>
        /// Coast forward from a prior trace — called by AetherFuser for techs not observed this tick,
        /// via <see cref="DeadReckon.Coast"/>. Anchors stay frozen; PositionMean/VelocityMean/AgeSeconds/
        /// UncertaintyMeters/PublishedTickMono/Sight update.
        /// </summary>
        public static BeliefState FromCoast(BeliefState prev,
            Vector3 newPosition, Vector3 newVelocity, float ageSeconds,
            float uncertainty, SightState sight, long nowMono)
        {
            return new BeliefState(
                prev.Id, prev.Team,
                lastSeenPosition: prev.LastSeenPosition,
                lastSeenVelocity: prev.LastSeenVelocity,
                lastSeenForward: prev.LastSeenForward,
                lastObservedTickMono: prev.LastObservedTickMono,
                publishedTickMono: nowMono,
                speedAtObserve: prev.SpeedAtObserve,
                maxAccelerationEstimate: prev.MaxAccelerationEstimate,
                sight: sight,
                positionMean: newPosition,
                velocityMean: newVelocity,
                forwardXZ: prev.ForwardXZ,
                headingMean: prev.HeadingMean,
                ageSeconds: ageSeconds,
                uncertaintyMeters: uncertainty);
        }

        /// <summary>
        /// Team-change preserving every other field — called by <see cref="WorldModel.UpdateTeam"/>
        /// (wired from <c>SmartEventBridge.OnTankTeamChanged</c>, Phase-3.3 captured-tech allegiance fix).
        /// Replaces the old instance <c>WithTeam</c> method; constructs a fresh sealed-class instance.
        /// No array-sharing concern because the new shape has no array fields.
        /// </summary>
        public static BeliefState WithTeam(BeliefState prior, TeamId newTeam)
        {
            return new BeliefState(
                prior.Id, newTeam,
                lastSeenPosition: prior.LastSeenPosition,
                lastSeenVelocity: prior.LastSeenVelocity,
                lastSeenForward: prior.LastSeenForward,
                lastObservedTickMono: prior.LastObservedTickMono,
                publishedTickMono: prior.PublishedTickMono,
                speedAtObserve: prior.SpeedAtObserve,
                maxAccelerationEstimate: prior.MaxAccelerationEstimate,
                sight: prior.Sight,
                positionMean: prior.PositionMean,
                velocityMean: prior.VelocityMean,
                forwardXZ: prior.ForwardXZ,
                headingMean: prior.HeadingMean,
                ageSeconds: prior.AgeSeconds,
                uncertaintyMeters: prior.UncertaintyMeters);
        }
    }

    /// <summary>
    /// Fused team-wide snapshot of every tracked tech's belief. Published per perception tick.
    /// Consumers (Planning, Pathing, Control) read this; per-tech consumers can also read the
    /// per-tech buffer in <see cref="WorldModel"/>.
    ///
    /// Aether v0.1: shape preserved verbatim — same type name, same ByTech contract. The
    /// underlying BeliefState class shape is what changed.
    /// </summary>
    public sealed class BeliefSnapshot
    {
        public long TickStamp { get; }
        public IReadOnlyDictionary<TechId, BeliefState> ByTech { get; }

        public BeliefSnapshot(long tickStamp, IReadOnlyDictionary<TechId, BeliefState> byTech)
        {
            TickStamp = tickStamp;
            ByTech = byTech ?? throw new ArgumentNullException(nameof(byTech));
        }

        public static readonly BeliefSnapshot Empty = new BeliefSnapshot(0L, new Dictionary<TechId, BeliefState>());
    }
}
