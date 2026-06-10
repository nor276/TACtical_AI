using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Control
{
    /// <summary>Captured-by-value MPC request payload. Crosses main thread → worker.</summary>
    internal sealed class MPCRequest
    {
        public VehicleModelSnapshot Vehicle;
        public RolloutState InitialState;
        public TacticalGoal Goal;
        public BeliefSnapshot Beliefs;
        public IThreatField ThreatField;          // null when PathingService inactive
        public VehicleCapability Capability;
        public long TickStamp;
    }

    /// <summary>
    /// Per-frame control profile published by MPC, consumed by ControlFrame.
    /// Per CONTROL-CONTRACT §9.1. Aim fields added at step 1.9 — the engine surface
    /// is global FireControl + TargetPositionWorld, so the controller publishes the
    /// chosen aim point alongside the per-weapon commit booleans.
    /// </summary>
    public sealed class ControlProfile
    {
        public long ValidFromTick { get; }
        public long ValidThroughTick { get; }
        public IReadOnlyList<ControlVector> Steps { get; }
        public IReadOnlyList<bool> WeaponFireCommits { get; }
        public bool FireAny { get; }
        public Vector3 AimPointWorld { get; }
        public float AimRadiusWorld { get; }

        public ControlProfile(long validFrom, long validThrough, ControlVector[] steps, bool[] weaponCommits,
            bool fireAny, Vector3 aimPointWorld, float aimRadiusWorld)
        {
            ValidFromTick = validFrom;
            ValidThroughTick = validThrough;
            Steps = steps ?? Array.Empty<ControlVector>();
            WeaponFireCommits = weaponCommits ?? Array.Empty<bool>();
            FireAny = fireAny;
            AimPointWorld = aimPointWorld;
            AimRadiusWorld = aimRadiusWorld;
        }

        // Initial profile published into the buffer at controller construction and after any
        // failed solve. ControlVector.Neutral is brake=1 (parking brake on) - that overrides
        // every published MPC step with drive=0 in the actuator path mapping
        // (drive = throttle * (1 - brake) + reverse, brake=1 => drive=0). Switched to Zero
        // (throttle=0, steer=0, brake=0) so a tech that hasn't yet received an MPC publish
        // freely rolls instead of actively braking - the first MPC publish then takes over.
        public static ControlProfile Neutral(long tick) =>
            new ControlProfile(tick, tick, new[] { ControlVector.Zero }, Array.Empty<bool>(),
                fireAny: false, aimPointWorld: Vector3.zero, aimRadiusWorld: 0f);
    }

    /// <summary>
    /// Per-tech orchestrator. Per CONTROL-CONTRACT §2. Owned by SmartPerTechState.
    ///
    /// Operations tick flow:
    ///   1. Host-only gate.
    ///   2. Belief read; bail if absent.
    ///   3. Tactical optimizer step → new TacticalGoal published.
    ///   4. Single-slot atomic exchange of MPC request; worker dispatched if none in flight.
    ///   5. Weapon fire commits computed on main thread (cheap).
    ///
    /// ControlFrame: read latest ControlProfile, apply current-step ControlVector and
    /// weapon commits to TankControl.
    /// </summary>
    public sealed class ContinuousController
    {
        private readonly WorkerPool _pool;
        private readonly TeamId _team;
        private readonly DoubleBuffer<VehicleModelSnapshot> _vehicleBuffer;
        private readonly DoubleBuffer<KinematicState> _kinematicBuffer;
        private readonly Func<BeliefSnapshot> _readBeliefs;
        private readonly Func<TacticalGoal?> _readExternalGoal;
        private readonly Func<TechId?> _readCoordinationTarget;
        private readonly Func<List<Vector3>> _readFriendlyPositions;
        // Identity-driven goal source + per-tick context. Wired by SmartPerTechState after
        // SmartIdentityClassifier stamps. Both may be null on Phase 1 (registry is empty,
        // all techs classify Generic). Coordinator's _readExternalGoal still takes precedence;
        // identity dispatch only runs when external is absent. See Docs/SMART-IDENTITY-DESIGN.md.
        private readonly Func<ISmartGoalSource> _readGoalSource;
        private readonly Func<IdentityContext> _readIdentityCtx;
        // Hard-correct mailbox reader (GroundedAircraft slot). Returns the live slot or null.
        // Wired by SmartPerTechState; reads helper.GuardInjectedGoals on the main thread.
        private readonly Func<Director.Guards.GuardInjectedGoalSlot> _readGuardGoal;
        // [GUARD-MAILBOX-STALE] counter — slots whose deadline is older than ceiling + 2 s.
        private long _guardMailboxStaleCount;

        private readonly DoubleBuffer<TacticalGoal> _tacticalGoalBuffer;
        private readonly DoubleBuffer<ControlProfile> _controlProfileBuffer;

        private readonly TacticalOptimizer _tactical = new TacticalOptimizer();

        // P6 Item 27: per-controller delegate that closes over _tactical.Step so the
        // Generic dispatch can route through the registry symmetrically with the other
        // identities. Allocated once in the ctor; reused every tick (no per-tick GC).
        // Signature matches TacticalOptimizer.Step verified at TacticalOptimizer.cs:59.
        private readonly System.Func<BeliefState, VehicleModelSnapshot, BeliefSnapshot, TacticalGoal> _tacticalHandle;
        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.4: SamplingMPC was zero-arg-constructed,
        // which seeds Random() with a time-based clock — so two techs spawned in the
        // same ms saw identical sample streams (lock-step trajectories) and training
        // runs were non-reproducible. Seed deterministically from team in the ctor body.
        private readonly SamplingMPC _mpc;
        private readonly WeaponFireController _weapons = new WeaponFireController();

        // Single-pending-slot pattern for MPC requests (THREADING §4 marshalling).
        private MPCRequest _pendingRequest;

        // Aether/T1 (9-agent threading review): replaced the prior bare-bit `int _mpcInFlight`
        // (0 = idle, 1 = a worker is running) with a token-and-watchdog pair. The bare bit
        // leaked if the worker died between mark and clear (Thread.Abort, exception escaping
        // finally, WorkerPool slot drop) — leaving _mpcInFlight = 1 forever, so subsequent
        // dispatches always saw "in flight" and skipped, freezing the tech.
        //
        // New scheme: _mpcDispatchedTickMono holds the MonoClock timestamp of the most recent
        // dispatch. 0 means idle. A dispatch is allowed if value is 0 OR value is older than
        // MpcWatchdogMs ago (the prior dispatch is presumed dead). On completion the worker
        // CAS-clears only if its dispatch token still matches — if the watchdog already took
        // over, the stale worker silently loses the CAS and doesn't clobber the active dispatch.
        private long _mpcDispatchedTickMono;
        private const int MpcWatchdogMs = 200;

        // Warm-start carried across solves.
        private ControlVector[] _previousMean;

        // Tracks whether the previous tick used a Coordination-supplied goal so we can
        // re-seed the tactical optimizer's Adam state on transitions back to local control.
        private bool _lastTickHadExternalGoal;

        private long _tickCounter;

        // ActionValue producer state — FEATURE-EXPANSION-PLAN §7.2 + R2-06. Q-learning
        // wants (s, a, r, s'); we can't enqueue until s' is known, so the producer caches
        // the prior tick's (state, action, reward) and emits on the NEXT tick once the
        // current state has been published by the StrategicStateExtractor. _pendingActionValid
        // = false on first tick + after any frame where StrategicVectors.TryRead returned null
        // (early ticks before the extractor publishes).
        private bool _pendingActionValid;
        private float[] _pendingActionState;       // s — copy of AV slice at decision time
        private int _pendingActionIndex;           // a — ActionGoalClass index
        private float _pendingActionReward;        // r — computed against 0.5s lookback
        // Q-learning discount factor. Plan §7.2 doesn't pin a value; 0.95 matches the
        // short-horizon receding-decision cadence (Operations ~30 Hz, ~0.5s reward window).
        private const float ActionValueGamma = 0.95f;

        // [TEMP DIAGNOSTIC] Per-controller cap so we see the first few goal-dispatch outcomes
        // without spamming the log. Sized to cover ~1s of Operations ticks (30Hz).
        private int _goalDispatchLogCount;
        private const int GoalDispatchLogCap = 6;
        // [TEMP DIAGNOSTIC] Per-controller cap on ControlFrame actuator-write logs.
        private int _controlFrameLogCount;
        private const int ControlFrameLogCap = 6;

        public DoubleBuffer<TacticalGoal> TacticalGoalBuffer => _tacticalGoalBuffer;
        public DoubleBuffer<ControlProfile> ControlProfileBuffer => _controlProfileBuffer;

        public ContinuousController(
            WorkerPool pool,
            TeamId team,
            DoubleBuffer<VehicleModelSnapshot> vehicleBuffer,
            DoubleBuffer<KinematicState> kinematicBuffer,
            Func<BeliefSnapshot> readBeliefs,
            Func<TacticalGoal?> readExternalGoal = null,
            Func<TechId?> readCoordinationTarget = null,
            Func<List<Vector3>> readFriendlyPositions = null,
            Func<ISmartGoalSource> readGoalSource = null,
            Func<IdentityContext> readIdentityCtx = null,
            Func<Director.Guards.GuardInjectedGoalSlot> readGuardGoal = null,
            int mpcSeed = 0)
        {
            _pool = pool;
            _team = team;
            // Deterministic-per-team seed when caller doesn't supply one. team.Value*7919
            // spreads neighbour teams; +1 keeps SamplingMPC's "seed==0 means clock" branch
            // out of play.
            int seed = mpcSeed != 0 ? mpcSeed : team.Value * 7919 + 1;
            _mpc = new SamplingMPC(seed);
            _vehicleBuffer = vehicleBuffer;
            _kinematicBuffer = kinematicBuffer;
            _readBeliefs = readBeliefs;
            _readExternalGoal = readExternalGoal;
            _readCoordinationTarget = readCoordinationTarget;
            _readFriendlyPositions = readFriendlyPositions;
            _readGoalSource = readGoalSource;
            _readIdentityCtx = readIdentityCtx;
            _readGuardGoal = readGuardGoal;

            // P6 Item 27: capture per-controller TacticalGoalHandle that closes over _tactical.
            // GenericGoalSource.Produce invokes this via ctx.TacticalGoalHandle to delegate
            // back to the same _tactical.Step call the v0.1 inline-bypass branch used —
            // preserves per-instance Adam state, behavior bit-identical at the optimizer.
            _tacticalHandle = (belief, vehicle, beliefs) => _tactical.Step(belief, vehicle, beliefs);

            // Initialize buffers with neutral values so ControlFrame never reads null.
            _tacticalGoalBuffer = new DoubleBuffer<TacticalGoal>(TacticalGoal.AtCurrent(Vector3.zero, 0f));
            _controlProfileBuffer = new DoubleBuffer<ControlProfile>(ControlProfile.Neutral(0));
        }

        /// <summary>Called by SmartForm.Operations(helper, host). Host-only.</summary>
        public void OnOperationsTick(bool host, BeliefState ownBelief)
        {
            if (!host) return;
            _tickCounter++;
            if (ownBelief == null) return;

            var vehicle = _vehicleBuffer.Read();
            var kinematic = _kinematicBuffer.Read();
            var beliefs = _readBeliefs != null ? _readBeliefs() : BeliefSnapshot.Empty;

            // Goal selection: 3-tier precedence per Docs/SMART-IDENTITY-DESIGN.md sec 7.3:
            //   1. Coordination's published goal wins when fresh (unchanged).
            //   2. Identity goal source (Phase 1+): produces a TacticalGoal based on the per-tech
            //      identity stamp. Generic identities bypass this entirely - the registry has no
            //      Generic entry, so _readGoalSource returns null and we fall through to (3).
            //   3. TacticalOptimizer.Step (Generic fallback): existing Adam optimizer behavior.
            // External->local transition still re-seeds Adam (regardless of whether the local
            // path is identity-driven or tactical) so a future identity removal cleanly resumes.
            TacticalGoal goal;
            var external = _readExternalGoal?.Invoke();
            bool usedExternal = false, usedIdentity = false, usedTactical = false;
            SmartIdentity dispatchIdentity = SmartIdentity.Generic;
            // Lifted out of the identity branch — the AV producer at the end of this
            // method needs SelfTechId on every tick to slice StrategicVectors. baseCtx
            // is also reused as the identity-source ctx when src != null.
            var baseCtx = _readIdentityCtx != null ? _readIdentityCtx() : default(IdentityContext);

            // Priority 0: hard-correct mailbox PREEMPTS the coordinator. A burning aircraft
            // recovery goal must win over coordinated engagement — safety > coordination.
            // Soft / observe guards never reach here (telemetry-only, no slot). The goal
            // flows into the same MPC dispatch + weapon pass below so the tech actually
            // climbs; we only short-circuit the external / identity / tactical selection.
            TacticalGoal hardCorrectGoal;
            bool usedHardCorrect = TryReadHardCorrectGoal(kinematic, out hardCorrectGoal);
            if (usedHardCorrect)
            {
                goal = hardCorrectGoal;
                // Preserve Adam-reseed semantics: next non-AntiCrash tick sees the right
                // external-transition edge.
                _lastTickHadExternalGoal = external.HasValue;
            }
            else if (external.HasValue)
            {
                goal = external.Value;
                _lastTickHadExternalGoal = true;
                usedExternal = true;
            }
            else
            {
                if (_lastTickHadExternalGoal)
                {
                    _tactical.Reset(ownBelief.PositionMean, ownBelief.HeadingMean);
                    _lastTickHadExternalGoal = false;
                }
                var src = _readGoalSource?.Invoke();
                if (src != null)
                {
                    // P6 Item 27: registered Generic dispatches via the handle pattern, so
                    // we no longer special-case Identity == Generic here. The wrap with
                    // _tacticalHandle is what lets GenericGoalSource invoke _tactical.Step
                    // (per-instance Adam state preserved) via the symmetric registry path.
                    var ctx = baseCtx.WithTacticalGoalHandle(_tacticalHandle);
                    goal = src.Produce(ownBelief, vehicle, beliefs, ctx);
                    usedIdentity = true;
                    dispatchIdentity = src.Identity;
                }
                else
                {
                    // S1 SAFETY: preserve direct fallback when registry returns null for the
                    // identity (e.g. init order race, test harness). Without this, src.Produce
                    // on a null src would throw; this branch keeps v0.1 behavior on the
                    // unhappy path.
                    goal = _tactical.Step(ownBelief, vehicle, beliefs);
                    usedTactical = true;
                }
                // Final NaN/Inf guard on the produced goal. If the belief was non-finite for
                // any reason that slipped past the upstream Kalman guard, the goal source
                // would produce NaN goals and poison the MPC. Fall back to a safe at-current
                // goal anchored at the kinematic position (always finite from engine rbody).
                if (float.IsNaN(goal.Position.x) || float.IsNaN(goal.Position.y) || float.IsNaN(goal.Position.z)
                    || float.IsInfinity(goal.Position.x) || float.IsInfinity(goal.Position.y) || float.IsInfinity(goal.Position.z)
                    || float.IsNaN(goal.Heading) || float.IsInfinity(goal.Heading))
                {
                    goal = TacticalGoal.AtCurrent(kinematic.PositionWorld, 0f);
                }
            }
            _tacticalGoalBuffer.Write(goal);

            // [TEMP DIAGNOSTIC] Log the first N goal-dispatch outcomes per controller so we
            // can see which branch fires + the goal-vs-current-position delta. If the delta
            // is large but the tech still isn't moving, the issue is MPC or actuator-side.
            //
            // P6 Item 27: Generic-classified techs now log branch="IDENTITY=Generic" instead
            // of the pre-v0.2 "TACTICAL" label. The underlying _tactical.Step call is
            // bit-identical (handle delegate wraps the same call) — P10 parity-catcher must
            // treat "IDENTITY=Generic" as equivalent to "TACTICAL" for v0.1 parity comparison.
            if (_goalDispatchLogCount < GoalDispatchLogCap)
            {
                _goalDispatchLogCount++;
                string branch = usedHardCorrect ? "GUARD-HARDCORRECT"
                              : usedExternal ? "EXTERNAL"
                              : usedIdentity ? ("IDENTITY=" + dispatchIdentity)
                              : "TACTICAL";
                float deltaPos = (goal.Position - ownBelief.PositionMean).magnitude;
                DebugTAC_AI.Log("Smart.Dispatch[#" + _goalDispatchLogCount + "] tick=" + _tickCounter
                    + " branch=" + branch
                    + " ownPos=" + ownBelief.PositionMean.ToString("F1")
                    + " goalPos=" + goal.Position.ToString("F1")
                    + " delta=" + deltaPos.ToString("F1") + "m");
            }

            // ===== ActionValue producer (FEATURE-EXPANSION-PLAN §7.2 + R2-06) =====
            // Q-learning needs (s, a, r, s'). s and a are determined here at the decision
            // boundary; s' is only known on the NEXT tick (after MPC+actuators have closed
            // the loop and the StrategicStateExtractor has republished). Pattern: cache
            // (prevState, prevAction, prevReward), and on the next tick we have s' so we
            // emit (prevState, prevAction, prevReward, sNext) at the same tick we capture
            // a fresh (state, action, reward) into the pending slot.
            //
            // Skipped when:
            //   - L-059 pause-safe gate: !SmartRuntime.AcceptingTrainingEvents
            //   - early-spawn race: SelfTechId default or StrategicVectors slot empty
            //   - learning subsystem not yet alive
            PublishActionValueEvent(baseCtx.SelfTechId, goal, ownBelief, kinematic, usedExternal,
                usedIdentity, dispatchIdentity, _readCoordinationTarget != null ? _readCoordinationTarget() : (TechId?)null);

            // Initial RolloutState matches current kinematic.
            float heading = Mathf.Atan2(kinematic.HeadingWorld.x, kinematic.HeadingWorld.z);
            var x0 = new RolloutState(kinematic.PositionWorld, kinematic.VelocityWorld, heading, kinematic.AngularVelocityWorld);

            // Pathing v0.1.0: query the active per-team threat field and derive the
            // VehicleCapability from the latest vehicle snapshot's Mobility (PATHING §4.3).
            // The threat-field reference is captured here on the main thread and crosses
            // into the MPC worker by-value through MPCRequest — PathingService's
            // GetThreatField returns a fresh wrapper around the latest snapshot, safe
            // to read from any thread.
            var threatField = PathingService.IsRunning ? PathingService.GetThreatField(_team) : null;
            var capability = VehicleCapability.FromMobility(vehicle.Mobility);

            // Single-pending-slot exchange: latest request wins.
            var newRequest = new MPCRequest
            {
                Vehicle = vehicle,
                InitialState = x0,
                Goal = goal,
                Beliefs = beliefs,
                ThreatField = threatField,
                Capability = capability,
                TickStamp = _tickCounter,
            };
            Interlocked.Exchange(ref _pendingRequest, newRequest);

            // Aether/T1: dispatch if idle OR if the watchdog deems the prior dispatch dead.
            long now = World.MonoClock.Now();
            long prevToken = Interlocked.Read(ref _mpcDispatchedTickMono);
            bool watchdogElapsed = prevToken != 0L
                && World.MonoClock.Seconds(prevToken, now) * 1000f > MpcWatchdogMs;
            if (prevToken == 0L || watchdogElapsed)
            {
                if (Interlocked.CompareExchange(ref _mpcDispatchedTickMono, now, prevToken) == prevToken)
                {
                    if (watchdogElapsed)
                        DebugTAC_AI.LogWarning("Smart.MPC watchdog: prior dispatch token "
                            + prevToken + " exceeded " + MpcWatchdogMs + "ms; reclaiming.");
                    long myToken = now;
                    _pool?.Enqueue(ct => RunMPCWork(ct, myToken));
                }
            }

            // Weapon fire decision computed on main thread (lead, friendly-fire raycast,
            // hysteresis, salvo coordination, energy budget). Cheap. Overlaid onto the
            // active profile so fire timing is independent of MPC publish cadence.
            var coordinationTarget = _readCoordinationTarget?.Invoke();
            var friendlies = _readFriendlyPositions?.Invoke();
            var terrain = (ITerrainMap)PathingService.CurrentTerrain;
            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.7: energy weapons need a refill path
            // or they self-lock the first time the reserve dips below threshold. Refill
            // happens here against the Operations cadence (the only fixed timebase we
            // share with the fire pass).
            _weapons.ApplyEnergyRefillTick(Time.fixedDeltaTime);
            // P4 Item 12: pass nowMono so WeaponFireController can call PositionAt/VelocityAt
            // when AetherTuning.UseCoastAwareLead is true. When false (default), nowMono is
            // ignored and the raw-mean path is byte-identical to v0.1.
            long fireNowMono = World.MonoClock.Now();
            var decision = _weapons.ComputeFireCommits(vehicle, kinematic, beliefs, coordinationTarget, friendlies, terrain, fireNowMono);
            OverlayFireDecisionOntoProfile(decision);

            // P7 Item 18: record fire-commit for residual learning. When the decision fires,
            // capture (predicted impact point, own position, projected flight time, fire-tick).
            // A later observation of the same target inside the slop window will trigger
            // LeadResidualRecorder.OnObservation to enqueue a ResidualEvent.
            if (decision != null && decision.FireAny)
            {
                var recorder = TAC_AI.AI.Forms.Smart.Learning.LeadResidualRecorder.Instance;
                if (recorder != null)
                {
                    // Approximate projTime from aim radius and target distance: aim radius
                    // is bounded by the lead solver's accuracy, distance is the lead-time
                    // proxy here. WeaponFireController doesn't currently expose per-target
                    // projTime in the WeaponFireDecision (v0.3 surface); use the distance/
                    // muzzle-velocity approximation from the primary weapon's spec.
                    float dist = (decision.AimPointWorld - kinematic.PositionWorld).magnitude;
                    float bestMuzzle = 0f;
                    if (vehicle.Weapons != null)
                    {
                        for (int wi = 0; wi < vehicle.Weapons.Count; wi++)
                        {
                            float mv = vehicle.Weapons[wi].ProjectileVelocity;
                            if (mv > bestMuzzle) bestMuzzle = mv;
                        }
                    }
                    float projTime = bestMuzzle > 0f ? dist / bestMuzzle : 0f;
                    if (projTime > 0f)
                    {
                        // FEATURE-EXPANSION-PLAN §3.4 R2-04: slice the Residual block out
                        // of the published StrategicStateVector for this tech so the
                        // model trains against the fire-time feature shape, not the
                        // arrival-time one. SmartRuntime.StrategicVectors and the slot
                        // base offsets/dim are public.
                        float[] fireFeatures = ExtractResidualSlice(decision.AimTargetId);
                        recorder.OnFireCommit(
                            decision.AimTargetId,
                            decision.AimPointWorld,
                            kinematic.PositionWorld,
                            projTime,
                            fireNowMono,
                            fireFeatures);
                    }
                }
            }
        }

        // Priority-0 hard-correct mailbox read. Returns true + the recovery goal when a live
        // non-expired slot is present. Stale slots (deadline + 2 s buffer past) are rejected
        // and counted under [GUARD-MAILBOX-STALE]. NaN/Inf positions fall back to AtCurrent.
        private bool TryReadHardCorrectGoal(KinematicState kinematic, out TacticalGoal goal)
        {
            goal = default(TacticalGoal);
            if (_readGuardGoal == null) return false;
            var slot = _readGuardGoal();
            if (slot == null) return false;

            long now = World.MonoClock.Now();
            long staleCeiling = slot.ExpiresMono + World.MonoClock.FromSeconds(2.0);
            if (now > staleCeiling)
            {
                Interlocked.Increment(ref _guardMailboxStaleCount);
                DebugTAC_AI.LogWarnFileOnly("guard-mailbox-stale",
                    "[GUARD-MAILBOX-STALE] count=" + Interlocked.Read(ref _guardMailboxStaleCount));
                return false;
            }
            if (now > slot.ExpiresMono) return false; // expired but inside grace — let it lapse

            var g = slot.Goal;
            if (float.IsNaN(g.Position.x) || float.IsNaN(g.Position.y) || float.IsNaN(g.Position.z)
                || float.IsInfinity(g.Position.x) || float.IsInfinity(g.Position.y) || float.IsInfinity(g.Position.z)
                || float.IsNaN(g.Heading) || float.IsInfinity(g.Heading))
            {
                goal = TacticalGoal.AtCurrent(kinematic.PositionWorld, 0f);
                return true;
            }
            goal = g;
            return true;
        }

        // FEATURE-EXPANSION-PLAN §3.4 R2-04: slice the Residual block out of the
        // StrategicStateVector published for the AimTarget. Returns a float[] of length
        // TrajectoryResidualModel.FeatureDim — null when the buffer hasn't been ctored
        // (early Init race) or the target has no published vector yet. The slice copies
        // out so the recorder's pending slot doesn't alias a freshly-republished vector.
        private static float[] ExtractResidualSlice(TechId aimTargetId)
        {
            var buffer = SmartRuntime.StrategicVectors;
            if (buffer == null) return null;
            var vec = buffer.TryRead(aimTargetId);
            if (vec == null || vec.Raw == null) return null;
            int dim = Learning.Features.StrategicStateVector.ResidualDim;
            int b = Learning.Features.StrategicStateVector.ResidualBase;
            if (vec.Raw.Length < b + dim) return null;
            var slice = new float[dim];
            Array.Copy(vec.Raw, b, slice, 0, dim);
            return slice;
        }

        // ===== ActionValue producer helpers (§7.2 + R2-06) =====
        // Decision-boundary publisher: snapshots the AV state slice, encodes the action
        // one-hot index, computes the reward against the 0.5s HealthSidecar lookback, and
        // emits the deferred (s, a, r, s') tuple to the trainer queue. One-tick deferred:
        // the s' that completes the prior tick's tuple is THIS tick's state snapshot, so
        // we enqueue the previous pending tuple and seed the next one in the same call.
        private void PublishActionValueEvent(
            TechId selfTechId,
            TacticalGoal goal,
            BeliefState ownBelief,
            KinematicState kinematic,
            bool usedExternal,
            bool usedIdentity,
            SmartIdentity dispatchIdentity,
            TechId? coordinationTarget)
        {
            // L-059 producer gate — drop while trainer drain is in progress. Also
            // clear the pending (s, a, r) slot so when the gate later re-opens we
            // don't enqueue a (state, action, reward, s') tuple whose state pre-dates
            // the pause and s' lands gigaseconds later. Per BUG-066.
            if (!SmartRuntime.AcceptingTrainingEvents)
            {
                _pendingActionValid = false;
                _pendingActionState = null;
                return;
            }

            var learning = Learning.LearningService.ActionValue;
            if (learning == null) return;   // subsystem not running

            var buffer = SmartRuntime.StrategicVectors;
            if (buffer == null) return;

            // selfTechId default (Value == 0) indicates _readIdentityCtx returned default —
            // SmartPerTechState hasn't wired the ctx delegate yet (early spawn race).
            if (selfTechId.Value == 0) return;

            var vec = buffer.TryRead(selfTechId);
            if (vec == null || vec.Raw == null) return;

            // Slice the 128-dim AV state from Raw[40..167]. Copies out so the trainer's
            // event doesn't alias a freshly-republished vector — extractor mutates Raw
            // elements in place under its DoubleBuffer write.
            int dim = Learning.Features.StrategicStateVector.ActionValueDim;
            int b   = Learning.Features.StrategicStateVector.ActionValueBase;
            if (vec.Raw.Length < b + dim) return;
            var currentState = new float[dim];
            System.Array.Copy(vec.Raw, b, currentState, 0, dim);

            // Encode the just-selected action one-hot. We do NOT touch the published
            // vector — the extractor owns the action slot. Write into our local slice
            // so the trainer sees the action embedded in s and s' consistently.
            int actionIdx = (int)MapGoalToActionClass(usedExternal, usedIdentity, dispatchIdentity,
                goal, ownBelief, kinematic, coordinationTarget);
            int oneHotBase = Learning.Features.ActionValueSlots.ActionOneHotBase;
            // Defensive clear of the 10-slot one-hot window in the local copy then set
            // the single active bit — the extractor's value, if any, is action-irrelevant
            // for training (it's a producer-private field).
            int oneHotCount = Learning.Features.ActionValueSlots.ActionOneHotCount;
            for (int i = 0; i < oneHotCount; i++) currentState[oneHotBase + i] = 0f;
            if (actionIdx >= 0 && actionIdx < oneHotCount)
                currentState[oneHotBase + actionIdx] = 1f;

            // Reward — §7.2 formula. ComputeReward swallows null health / missing target
            // and returns finite zero on degenerate cases; safe to call unconditionally.
            long nowMono = World.MonoClock.Now();
            bool hasTarget = coordinationTarget.HasValue && coordinationTarget.Value.Value != 0;
            TechId targetForReward = hasTarget ? coordinationTarget.Value : default(TechId);
            float reward = Learning.ActionValueEstimator.ComputeReward(
                SmartRuntime.Health, selfTechId, targetForReward, hasTarget, nowMono);

            // Emit the prior pending tuple now that s' (= currentState) is known. The
            // event copies are aliased to currentState as s'; the prior tuple's state
            // is its own array — no aliasing across events.
            if (_pendingActionValid && _pendingActionState != null)
            {
                var avEvent = new Learning.ActionValueEvent(
                    _pendingActionState, _pendingActionIndex, _pendingActionReward,
                    currentState, ActionValueGamma);
                learning.EventQueue.Enqueue(avEvent);
                // Tee into IdentityReplayBank. Source-tech is selfTechId.
                var avIdentity = TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.ResolveIdentity(selfTechId);
                TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.TeeActionValue(avIdentity, avEvent);
            }

            // Seed the next pending tuple. currentState is now BOTH s' of the just-
            // enqueued event AND s of the next event; that's fine because the trainer
            // only reads (no mutation) and we replace the pending-state ref on the
            // next call before reaching back into this slot.
            _pendingActionState = currentState;
            _pendingActionIndex = actionIdx;
            _pendingActionReward = reward;
            _pendingActionValid = true;
        }

        // Goal-class mapping table (§7.2 final paragraph). Mapping lives here next to
        // the producer for review. External branches inspect the goal shape (AtCurrent
        // = zero-velocity goal pinned at/near self; Attack = a coordination target was
        // supplied alongside; Move = anywhere else). Identity branches collapse the
        // per-identity goal kind to the bucket it most naturally falls into; Sniper
        // shares Hunter's seek-kill character, Patrol shares Generic's wander/react.
        private static Learning.ActionGoalClass MapGoalToActionClass(
            bool usedExternal,
            bool usedIdentity,
            SmartIdentity dispatchIdentity,
            TacticalGoal goal,
            BeliefState ownBelief,
            KinematicState kinematic,
            TechId? coordinationTarget)
        {
            if (usedExternal)
            {
                bool hasTarget = coordinationTarget.HasValue && coordinationTarget.Value.Value != 0;
                if (hasTarget) return Learning.ActionGoalClass.External_Attack;
                // AtCurrent = zero-velocity goal whose position is within a small radius
                // of own kinematic position. The Coordinator publishes TacticalGoal
                // .AtCurrent for hold-position; magnitude check tolerates Kalman jitter.
                Vector3 here = kinematic.PositionWorld;
                float dx = goal.Position.x - here.x;
                float dz = goal.Position.z - here.z;
                float distSq = dx * dx + dz * dz;
                bool atCurrent = goal.Velocity.sqrMagnitude < 0.01f && distSq < 4f * 4f;
                return atCurrent
                    ? Learning.ActionGoalClass.External_AtCurrent
                    : Learning.ActionGoalClass.External_Move;
            }

            if (usedIdentity)
            {
                switch (dispatchIdentity)
                {
                    case SmartIdentity.Hunter:
                    case SmartIdentity.Sniper:
                        return Learning.ActionGoalClass.Identity_Hunter;
                    case SmartIdentity.Base:
                        return Learning.ActionGoalClass.Identity_Base;
                    case SmartIdentity.Gatherer:
                        return Learning.ActionGoalClass.Identity_Gatherer;
                    case SmartIdentity.AircraftHunter:
                    case SmartIdentity.AircraftSupport:
                        return Learning.ActionGoalClass.Identity_Air;
                    case SmartIdentity.RepairSupport:
                        return Learning.ActionGoalClass.Identity_Support;
                    case SmartIdentity.Patrol:
                    case SmartIdentity.Generic:
                    default:
                        return Learning.ActionGoalClass.Identity_Generic;
                }
            }

            // Pure TacticalOptimizer.Step fallback (no registered identity source, no
            // external goal) — bucketed as the dedicated tactical-fallback class.
            return Learning.ActionGoalClass.Tactical_Fallback;
        }

        private void OverlayFireDecisionOntoProfile(WeaponFireDecision decision)
        {
            var current = _controlProfileBuffer.Read();
            if (current == null || decision == null) return;
            var steps = new ControlVector[current.Steps.Count];
            for (int i = 0; i < steps.Length; i++) steps[i] = current.Steps[i];
            var overlay = new ControlProfile(
                current.ValidFromTick, current.ValidThroughTick,
                steps, decision.Commits,
                decision.FireAny, decision.AimPointWorld, decision.AimRadiusWorld);
            _controlProfileBuffer.Write(overlay);
        }

        private void RunMPCWork(CancellationToken token, long myDispatchToken)
        {
            try
            {
                var req = Interlocked.Exchange(ref _pendingRequest, null);
                if (req == null) return;

                token.ThrowIfCancellationRequested();

                // Sanitize the MPC warm-start. _previousMean carries forward across Solve calls
                // as the next sample-distribution mean. A single past Solve that ingested a NaN
                // goal (e.g. before the Phase-7 belief-NaN guard landed, or any future leak)
                // permanently poisons _previousMean - Mathf.Clamp(NaN, ...) returns NaN, every
                // sampled Throttle/Steer/Brake is NaN, every PhysicsRollout position is NaN,
                // every cost is NaN, and the NaN-safe weighted average is NaN regardless of
                // the uniform-weight fallback. The actuator then writes NaN drive and the tech
                // stops moving forever. Detect + reset before passing to Solve.
                if (_previousMean != null && HasNonFiniteControl(_previousMean))
                    _previousMean = null;

                var solution = _mpc.Solve(
                    req.Vehicle, req.InitialState, req.Goal, req.Beliefs,
                    req.ThreatField, req.Capability,
                    _previousMean, token);
                if (solution == null) return;

                // Also sanitize the solve output before using it as next-tick warm start.
                if (HasNonFiniteControl(solution))
                {
                    for (int i = 0; i < solution.Length; i++) solution[i] = ControlVector.Zero;
                }

                _previousMean = ShiftLeft(solution);

                // Preserve last-known weapon decision so the MPC publish doesn't wipe
                // aim or fire state — the per-tick overlay in OnOperationsTick will
                // refresh both on the very next tick anyway, but a same-tick read of
                // the profile by ControlFrame should still see the prior decision.
                var prior = _controlProfileBuffer.Read();
                bool[] priorCommits = prior != null
                    ? CopyCommits(prior.WeaponFireCommits)
                    : Array.Empty<bool>();
                var profile = new ControlProfile(
                    validFrom: req.TickStamp,
                    validThrough: req.TickStamp + solution.Length,
                    steps: solution,
                    weaponCommits: priorCommits,
                    fireAny: prior?.FireAny ?? false,
                    aimPointWorld: prior?.AimPointWorld ?? Vector3.zero,
                    aimRadiusWorld: prior?.AimRadiusWorld ?? 0f);
                _controlProfileBuffer.Write(profile);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            finally
            {
                // Aether/T1: CAS-clear only if our dispatch token is still active. If the
                // watchdog already took over (because we ran longer than MpcWatchdogMs),
                // a new dispatch is running and we MUST NOT clobber its token.
                Interlocked.CompareExchange(ref _mpcDispatchedTickMono, 0L, myDispatchToken);
            }
        }

        private static bool[] CopyCommits(IReadOnlyList<bool> src)
        {
            if (src == null || src.Count == 0) return Array.Empty<bool>();
            var copy = new bool[src.Count];
            for (int i = 0; i < src.Count; i++) copy[i] = src[i];
            return copy;
        }

        private static bool HasNonFiniteControl(ControlVector[] seq)
        {
            if (seq == null) return false;
            for (int i = 0; i < seq.Length; i++)
            {
                var c = seq[i];
                if (float.IsNaN(c.Throttle) || float.IsInfinity(c.Throttle)) return true;
                if (float.IsNaN(c.Steer) || float.IsInfinity(c.Steer)) return true;
                if (float.IsNaN(c.Brake) || float.IsInfinity(c.Brake)) return true;
            }
            return false;
        }

        private static ControlVector[] ShiftLeft(ControlVector[] seq)
        {
            if (seq == null || seq.Length == 0) return seq;
            var shifted = new ControlVector[seq.Length];
            for (int i = 0; i < seq.Length - 1; i++) shifted[i] = seq[i + 1];
            shifted[seq.Length - 1] = seq[seq.Length - 1]; // hold final
            return shifted;
        }

        /// <summary>
        /// Called by SmartForm.ControlFrame each frame. Reads the latest ControlProfile and
        /// writes movement + fire + aim signals to TankControl.
        ///
        /// Verified TerraTech surface (OQ-5 resolved):
        /// - <c>tank.control.CollectMovementInput(driveVec, turnVec, throttleVec, props, jets)</c>
        ///   — the canonical multi-axis movement sink, used by Vanilla form's avoidance
        ///   pipeline. DriveVec.z is forward intent; TurnVec.y is yaw intent;
        ///   ThrottleVec is per-axis throttle magnitude.
        /// - <c>tank.control.FireControl</c> (bool) — global fire flag.
        /// - <c>tank.control.TargetPositionWorld</c> + <c>TargetRadiusWorld</c> — aim point.
        ///
        /// ControlVector → engine mapping (CONTROL-CONTRACT §9.2):
        ///   drive = clamp(Throttle - Brake, -1, 1)    // brake counters throttle
        ///   turn  = Steer                              // yaw
        ///   throttleMagnitude = 1                       // full per-axis multiplier
        ///   props/jets toggled when VehicleCapability classifies as Hover/Airplane.
        ///
        /// Per CONTROL §9.1 we use Steps[0] (the immediate-next action in the MPC's
        /// receding horizon). The Steps[] slot beyond index 0 is reserved for the
        /// extrapolation path when MPC publishes are stale per §9.3.
        /// </summary>
        private long _frameTickCounter;

        public void OnControlFrame(TankControl control, long currentFrameTick)
        {
            if (control == null) return;
            // Phase 5 (FIX-PLAN.md): CONTROL-CONTRACT §9.4 specifies ControlFrame is a
            // no-op on client (the engine net layer already replicates host's TankControl).
            // Without this guard, every client overwrites the host-replicated state every
            // frame with the initial Neutral profile (drive=0, brake=1, fire=false) —
            // freezing every Smart-driven tech on every client (AUDIT R1 2.3 + R2 1.R2-K).
            if (!SmartRuntime.IsHost) return;
            var profile = _controlProfileBuffer.Read();
            if (profile == null) return;

            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: previously always used Steps[0],
            // ignoring the receding-horizon design (the MPC publishes a multi-step
            // plan and the controller is supposed to step through it until a fresher
            // plan arrives). The result was that MPC publishing at every Operations
            // tick (~30Hz) was effectively wasted — we re-applied step zero every
            // frame and never benefited from a horizon. Now: pick the step index
            // matching the controller's frame-tick offset from ValidFromTick. When
            // the profile is stale (counter beyond ValidThroughTick) hold the final
            // extrapolated step rather than reverting to Neutral — drives degrade
            // gracefully when the MPC stalls.
            _frameTickCounter++;

            // ---- Movement ----
            // Always use Steps[0]. The previous receding-horizon indexing tried to step
            // through Steps[] by (frameTick - profile.ValidFromTick), but the two counters
            // are wholly unrelated: ValidFromTick comes from _tickCounter (Operations cadence,
            // ~30 Hz) while frameTick comes from _frameTickCounter (per-render-frame, ~50-60 Hz).
            // The counters drift apart within seconds, frameTick crosses ValidThroughTick
            // (=ValidFromTick + 20), and the index pins to Steps[last] - the "held final"
            // extrapolation step that after ShiftLeft tends toward low throttle. Tech ends up
            // stuck on a near-neutral extrapolation while every MPC publish gets ignored
            // except its terminal step. Until a real shared timebase is plumbed (CONTROL §9.3),
            // Steps[0] is the safe choice: the MPC publishes ~30 Hz, ControlFrame fires ~60 Hz,
            // so on average each MPC step is consumed by 2 frames - good enough for v0.1.
            if (profile.Steps != null && profile.Steps.Count > 0)
            {
                var step = profile.Steps[0];
                // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.7: brake is now applied actuator-
                // side as an additional multiplier on the forward component instead of
                // a naive throttle-minus-brake mixing. With the old (Throttle - Brake)
                // form, requesting (throttle=0.3, brake=0.7) produced reverse (-0.4)
                // when the spec calls for "decelerate by 0.7" — making MPC samples
                // that select brake during forward motion accidentally reverse.
                float fwd = Mathf.Max(0f, step.Throttle);
                float rev = Mathf.Min(0f, step.Throttle);
                float drive = fwd * (1f - Mathf.Clamp01(step.Brake)) + rev;
                drive = Mathf.Clamp(drive, -1f, 1f);
                Vector3 driveLocal = new Vector3(0f, 0f, drive);
                Vector3 turnLocal = new Vector3(0f, step.Steer, 0f);
                Vector3 throttleMagnitude = Vector3.one;

                // Props vs jets per vehicle class. Hovers and airplanes need vertical
                // authority engaged; ground techs leave both off.
                var vehicle = _vehicleBuffer.Read();
                bool useJets = vehicle.Mobility.VerticalAuthority > 1.5f;
                bool useProps = vehicle.Mobility.VerticalAuthority >= 0.5f && !useJets;

                control.CollectMovementInput(driveLocal, turnLocal, throttleMagnitude, useProps, useJets);

                // [TEMP DIAGNOSTIC] Log the first N actuator writes per controller so we see
                // what value reaches TankControl. If drive is consistently 0 here, the MPC
                // is publishing near-zero throttle profiles. If drive is non-zero but tech
                // doesn't move, the engine is ignoring CollectMovementInput.
                if (_controlFrameLogCount < ControlFrameLogCap)
                {
                    _controlFrameLogCount++;
                    DebugTAC_AI.Log("Smart.ControlFrame[#" + _controlFrameLogCount
                        + "] step=(thr=" + step.Throttle.ToString("F2")
                        + ",str=" + step.Steer.ToString("F2")
                        + ",brk=" + step.Brake.ToString("F2") + ")"
                        + " drive=" + drive.ToString("F2")
                        + " props=" + useProps + " jets=" + useJets
                        + " vAuth=" + vehicle.Mobility.VerticalAuthority.ToString("F2")
                        + " topSpd=" + vehicle.Mobility.TopSpeedForward.ToString("F1"));
                }
            }
            else if (_controlFrameLogCount < ControlFrameLogCap)
            {
                _controlFrameLogCount++;
                DebugTAC_AI.Log("Smart.ControlFrame[#" + _controlFrameLogCount
                    + "] BAIL: profile.Steps null or empty.");
            }

            // ---- Fire + aim ----
            control.FireControl = profile.FireAny;
            if (profile.FireAny && profile.AimRadiusWorld > 0f)
            {
                control.TargetPositionWorld = profile.AimPointWorld;
                control.TargetRadiusWorld = profile.AimRadiusWorld;
            }
        }
    }
}
