using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning.Features;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Per-tick read-only guard context. Pre-allocated by BehaviorGuardWorker and reused
    // across guards-on-the-same-tech to avoid GC churn at 1 Hz × N techs × 11 guards.
    // Mutable fields are reassigned per tech; consumers MUST NOT cache the reference
    // across ticks.
    public sealed class GuardContext
    {
        // Scenario + global state.
        public int ActiveScenario;
        public long MonoTick;
        public int LiveSmartTechCount;

        // Per-tech identity. Helper reads happen on background thread — bg consumers
        // MUST go through SelfProbeSnapshot / PublishedFrustrationMeter, NOT off
        // Transform / rb.velocity.
        public TankAIHelper Helper;
        public Tank Tank;          // For TechId calc + lifetime checks only.
        public TechId TechId;
        public SmartIdentity Identity;
        public SmartIdentityStamp IdentityStamp;
        public SelfProbeSnapshot Snapshot;

        // Cached scratch. Filled by the worker before delegating to guards so each
        // guard does not re-walk the ring.
        public float MeanRecentSpeedSigned;
        public float MeanRecentSpeedXZ;
        public float MeanRecentSpeedY;
        public float FractionSpeedSignedBelowZero;
        public Vector3 NetDisplacement;
        public Vector3 WindowStartPosition;
        public Vector3 LastDestinationCore;

        // Configured tunable values. Hard-coded by the worker today; DirectorTunables
        // registry wires here when present.
        public float OverextendedHunterLeashM;
    }
}
