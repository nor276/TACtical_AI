using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Coordination;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.Learning.Features;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Planning;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart
{
    /// <summary>
    /// Smart's per-tech state object. Stored opaquely in TankAIHelper.FormState by SmartForm.
    /// One instance per Smart-driven tech. Per CONTROL-CONTRACT §2.1.
    ///
    /// Holds the Coordination-published external tactical goal buffer (with freshness TTL).
    /// ContinuousController prefers a fresh external goal over running its own tactical
    /// optimizer; absent or stale, it falls back per CONTROL §6.
    /// </summary>
    internal sealed class SmartPerTechState
    {
        public Tank Tank { get; }
        public TankAIHelper Helper { get; }
        public TechId TechId { get; }
        /// <summary>
        /// Per FIX-PLAN.md Phase 3.3 + AUDIT-R2 §2.R2.B: TeamId is mutable so a tech
        /// captured / converted at runtime can be moved between team registries. The
        /// setter is internal — only <see cref="UpdateTeam"/> may write it, called from
        /// <see cref="SmartEventBridge"/> when the engine raises a team-changed event.
        /// </summary>
        public TeamId TeamId { get; private set; }
        internal void UpdateTeam(TeamId newTeam) { TeamId = newTeam; }

        public KinematicTracker KinematicTracker { get; }
        public DoubleBuffer<KinematicState> KinematicBuffer { get; }
        public DoubleBuffer<VehicleModelSnapshot> VehicleBuffer { get; }
        public DoubleBuffer<TacticalGoal> ExternalGoalBuffer { get; }
        public DoubleBuffer<TargetAssignment> ExternalTargetBuffer { get; }
        public ContinuousController Controller { get; }

        // Per-tech main-thread publisher feeding StrategicStateExtractor (Phase-4 wiring).
        // Constructed alongside the other per-tech buffers; ticked main-thread inside
        // TickMainThread via Publish (gated on tank presence). The extractor's probeLookup
        // delegate routes (TechId → SelfStateProbe) through TeamRuntime.TryGetTechState.
        public SelfStateProbe SelfStateProbe { get; }

        // Last published Hp fraction proxy — pulled from HealthSidecar.Get on the tick.
        // Cached so the probe Publish call has a finite value without a per-tick lookup
        // when the sidecar is null (early Init race).
        private float _hpFractionCache = 1f;

        public long LastObservationTick { get; private set; }

        // Coordination publishes per-tech goals + per-tech target assignments on its
        // own cadence (~300ms). Both are accepted as "fresh" within ExternalGoalMaxAgeMs.
        private const int ExternalGoalMaxAgeMs = 1000;
        private int _externalGoalWriteMs = int.MinValue;
        private int _externalTargetWriteMs = int.MinValue;
        // Reference to the owning team's runtime — used to read friendly-positions for
        // friendly-fire avoidance in the weapon-fire controller. Set after construction
        // by TeamRuntime.RegisterTech.
        internal TeamRuntime OwnerTeam;

        // Per-tech identity stamped once by SmartIdentityClassifier at OnTechSpawn. Drives
        // identity-specific goal sources via the ContinuousController fallback path. Generic
        // is the safe default - the controller bypasses the goal-source dispatch entirely for
        // Generic and calls TacticalOptimizer.Step inline. See Docs/SMART-IDENTITY-DESIGN.md.
        public SmartIdentityStamp IdentityStamp { get; private set; } = SmartIdentityStamp.DefaultGeneric;
        public ISmartGoalSource GoalSource { get; private set; }

        // Stamped once by SmartForm.OnTechSpawn. Identity is immutable per design L2.
        // Second call asserts in debug to catch accidental reclassification.
        internal void StampIdentity(SmartIdentityStamp stamp, ISmartGoalSource src)
        {
            IdentityStamp = stamp;
            GoalSource = src;
            // Mirror the identity byte into the probe so the daemon's SelfRoleHintCode
            // slot (§3.3 [39]) reads the right value without a back-channel lookup.
            if (SelfStateProbe != null) SelfStateProbe.Identity = stamp.Identity;
        }

        public SmartPerTechState(
            Tank tank,
            TankAIHelper helper,
            TeamId teamId,
            WorkerPool pool,
            Func<BeliefSnapshot> readBeliefs)
        {
            Tank = tank;
            Helper = helper;
            TechId = TechId.FromTank(tank);
            TeamId = teamId;

            KinematicTracker = new KinematicTracker();
            KinematicBuffer = new DoubleBuffer<KinematicState>(KinematicState.Zero);
            VehicleBuffer = new DoubleBuffer<VehicleModelSnapshot>(VehicleModelSnapshot.Empty(TechId.Value));
            ExternalGoalBuffer = new DoubleBuffer<TacticalGoal>(TacticalGoal.AtCurrent(Vector3.zero, 0f));
            ExternalTargetBuffer = new DoubleBuffer<TargetAssignment>(TargetAssignment.None);
            SelfStateProbe = new SelfStateProbe();

            Controller = new ContinuousController(
                pool, teamId, VehicleBuffer, KinematicBuffer, readBeliefs,
                readExternalGoal: TryReadFreshExternalGoal,
                readCoordinationTarget: TryReadFreshExternalTargetId,
                readFriendlyPositions: ReadFriendlyPositionsExcludingSelf,
                readGoalSource: ReadGoalSource,
                readIdentityCtx: BuildIdentityContext,
                readGuardGoal: ReadGuardGoal,
                // Per-tech seed for SamplingMPC sample independence (Phase 7 / AUDIT R1 2.4).
                // Phase 11 (verify-build): 2654435761 (Knuth's multiplicative-hash constant)
                // exceeds int.MaxValue so the literal is parsed as long, making the
                // expression long-typed and rejecting the implicit narrowing to int. Use
                // explicit uint arithmetic with an unchecked cast to int — wraps cleanly,
                // preserves the hash mixing semantics, and stays inside int.
                mpcSeed: unchecked((int)((uint)TechId.Value * 2654435761u + (uint)teamId.Value)));
        }

        // Delegate exposed to ContinuousController. Returns the per-tech goal source (set by
        // StampIdentity) or null if identity is Generic / not yet stamped. Phase 1: always null
        // because the registry is empty and every tech classifies Generic - controller falls
        // through to TacticalOptimizer.Step unchanged from today's behavior.
        private ISmartGoalSource ReadGoalSource() => GoalSource;

        // Main-thread read of the per-tech GroundedAircraft hard-correct slot for the
        // controller's priority-0 check. Defensive RM-9 sweep: reject a slot whose
        // TechIdAtWrite no longer matches this tech (pool-recycle race). Stamps the helper's
        // LastAppliedGoalKind sidecar when a live slot is returned (attribution).
        private Director.Guards.GuardInjectedGoalSlot ReadGuardGoal()
        {
            if (Helper == null) return null;
            var slots = Helper.GuardInjectedGoals;
            if (slots == null) return null;
            int slotIdx = Director.Guards.GuardRegistry.HardCorrectSlotIndexFor(
                Director.Guards.GuardId.GroundedAircraft);
            if (slotIdx < 0 || slotIdx >= slots.Length) return null;
            var slot = System.Threading.Volatile.Read(ref slots[slotIdx]);
            if (slot == null) return null;
            if (slot.TechIdAtWrite != TechId.Value)
            {
                System.Threading.Interlocked.CompareExchange(ref slots[slotIdx], null, slot);
                return null;
            }
            Helper.LastAppliedGoalKind = slot.Kind;
            return slot;
        }

        // Builds the per-tick IdentityContext consumed by goal sources. TeamCentroid is only
        // valid when HasAllies is true (TeamRuntime.TryGetCentroid is the truth source).
        // Main thread only.
        private IdentityContext BuildIdentityContext()
        {
            bool hasAllies = false;
            Vector3 centroid = Vector3.zero;
            var team = OwnerTeam;
            if (team != null) hasAllies = team.TryGetCentroid(TechId, out centroid);
            // Tick counter from controller's internal counter is not directly accessible; use
            // LastObservationTick as a per-tech deterministic seed for meander phase. Good enough
            // for Phase 1 (no identity reads it yet).
            return new IdentityContext(TechId, IdentityStamp, centroid, hasAllies,
                nearestOwnBaseId: null, tickCounter: LastObservationTick);
        }

        /// <summary>
        /// Called by TeamRuntime.PublishGoals on the Coordinator worker thread. Marks the
        /// goal fresh as of <see cref="Environment.TickCount"/>.
        /// </summary>
        public void WriteExternalGoal(TacticalGoal goal)
        {
            ExternalGoalBuffer.Write(goal);
            Interlocked.Exchange(ref _externalGoalWriteMs, Environment.TickCount);
        }

        /// <summary>
        /// Returns the latest external goal if it was written within
        /// <see cref="ExternalGoalMaxAgeMs"/>, else null. Called from
        /// ContinuousController on the main (Operations) thread each tick.
        /// </summary>
        public TacticalGoal? TryReadFreshExternalGoal()
        {
            int stamp = Volatile.Read(ref _externalGoalWriteMs);
            if (stamp == int.MinValue) return null;
            // Environment.TickCount wraps every ~25 days; the difference is correct
            // for short intervals (we cap at 1 s).
            int ageMs = unchecked(Environment.TickCount - stamp);
            if (ageMs < 0 || ageMs > ExternalGoalMaxAgeMs) return null;
            return ExternalGoalBuffer.Read();
        }

        public void WriteExternalTarget(TargetAssignment target)
        {
            ExternalTargetBuffer.Write(target);
            Interlocked.Exchange(ref _externalTargetWriteMs, Environment.TickCount);
        }

        public TechId? TryReadFreshExternalTargetId()
        {
            int stamp = Volatile.Read(ref _externalTargetWriteMs);
            if (stamp == int.MinValue) return null;
            int ageMs = unchecked(Environment.TickCount - stamp);
            if (ageMs < 0 || ageMs > ExternalGoalMaxAgeMs) return null;
            var assignment = ExternalTargetBuffer.Read();
            return assignment.IsValid ? assignment.TargetId : (TechId?)null;
        }

        /// <summary>
        /// Per CONTROL §10.7 — returns positions of friendly Smart-driven techs on the
        /// same team for friendly-fire raycast. Excludes self. v0.1.0 uses kinematic-buffer
        /// position (most recent main-thread observation); slightly stale but main-thread
        /// reads are cheap and the ray test forgives small offsets via FriendlyFireRadius.
        /// </summary>
        public System.Collections.Generic.List<Vector3> ReadFriendlyPositionsExcludingSelf()
        {
            var team = OwnerTeam;
            if (team == null) return null;
            return team.SnapshotFriendlyPositionsExcluding(TechId);
        }

        /// <summary>Main-thread tick: refresh kinematic, vehicle snapshot. Called from SmartForm.Operations.</summary>
        public void TickMainThread(float dt)
        {
            KinematicTracker.Observe(Tank, dt);
            KinematicBuffer.Write(KinematicTracker.Latest);

            // Vehicle rebuild on demand. v0.1.0: every Nth tick; later: debounced on block events.
            LastObservationTick++;
            if (LastObservationTick % 30 == 0)
            {
                RebuildVehicleSnapshotInternal();
            }

            // Phase-4 wiring: publish a SelfProbeSnapshot every main-thread tick so the
            // StrategicStateExtractor daemon has fresh self-state. Reads from cached
            // VehicleBuffer (ArmorMap, weapon agg) and HealthSidecar. Wrapped in try/catch
            // so a snapshot failure on one tech doesn't drop Operations for the rest.
            try
            {
                if (SelfStateProbe != null && Tank != null)
                {
                    var vsnap = VehicleBuffer.Read();
                    if (SmartRuntime.Health != null)
                        _hpFractionCache = SmartRuntime.Health.Get(TechId);

                    // Stamp cached aggregates from the last vehicle rebuild. WeaponAggregate
                    // here is derived from the existing WeaponProfile (count + ranges); the
                    // probe's main-thread Publish path reads them through StampCachedAggregates.
                    var wagg = BuildWeaponAggregateFromSnapshot(vsnap);
                    int blockCountTotal = SumKindCounts(vsnap.KindCounts);
                    SelfStateProbe.StampCachedAggregates(
                        wagg,
                        blockCount: blockCountTotal,
                        energyModuleCount: vsnap.KindCounts.EnergyStore,
                        generatingModuleCount: vsnap.KindCounts.Generator);

                    int cargoNum = 0, cargoCap = 0;
                    if (SmartRuntime.CargoState != null)
                    {
                        var cs = SmartRuntime.CargoState.GetSnapshot(TechId);
                        if (cs.CapacityKnown) { cargoNum = cs.NumChunks; cargoCap = cs.CapacityChunks; }
                    }

                    SelfStateProbe.Publish(
                        TechId, TeamId,
                        Tank, Helper,
                        vsnap.Armor, _hpFractionCache,
                        cargoNum, cargoCap);
                }
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.SelfStateProbe.Publish[" + TechId + "]: " + ex.Message);
            }

            // P7 Item 16 (Phase-3 §7.1 widening 12 → 40): append the self-tech's IntentSlots
            // view from the published StrategicStateVector as one timestep in the per-tech
            // observation ring. The extractor daemon already populates all 40 slots from
            // BeliefSnapshot + SelfProbeSnapshot + nearest-hostile + DamageHints + sidecars
            // at ~30 Hz; we just slice IntentBase..IntentBase+39 into the row and hand it to
            // the ring. Skip the record when the daemon hasn't published yet (early ticks
            // before the extractor warms up) so we never feed half-populated rows into the
            // heuristic labeler.
            var obsBuf = Learning.LearningService.ObservationSequence;
            if (obsBuf != null && SmartRuntime.StrategicVectors != null)
            {
                var vec = SmartRuntime.StrategicVectors.TryRead(TechId);
                if (vec != null && vec.Raw != null
                    && vec.Raw.Length >= StrategicStateVector.IntentBase + StrategicStateVector.IntentDim)
                {
                    var row = new float[Learning.TargetObservationSequenceBuffer.FeatureDim];
                    int B = StrategicStateVector.IntentBase;
                    // Copy the full 40-slot IntentSlots view verbatim. Field-by-field would
                    // re-list the daemon's work; Array.Copy keeps the producer honest about
                    // §3.2 being the single source of truth.
                    Array.Copy(vec.Raw, B, row, 0, StrategicStateVector.IntentDim);
                    obsBuf.RecordRow(TechId, row, MonoClock.Now());
                }
            }
        }

        /// <summary>
        /// Force a synchronous vehicle-snapshot rebuild bypassing the % 30 tick gate. Called
        /// from SmartForm.OnTechSpawn so SmartIdentityClassifier sees real composition data
        /// (not VehicleModelSnapshot.Empty) and downstream consumers like World.RegisterTech's
        /// maxAccelEstimate read get a real value from frame 0 instead of the 5f fallback.
        /// </summary>
        public void RebuildVehicleSnapshotNow()
        {
            // Observe kinematics first so the snapshot carries fresh kinematic state.
            KinematicTracker.Observe(Tank, 0f);
            KinematicBuffer.Write(KinematicTracker.Latest);
            RebuildVehicleSnapshotInternal();
        }

        // Shared rebuild body. Called from TickMainThread on the % 30 boundary and from
        // RebuildVehicleSnapshotNow at spawn. Main thread only.
        private void RebuildVehicleSnapshotInternal()
        {
            // [TEMP DIAGNOSTIC] Time the first VehicleRebuildTimingCap full vehicle-rebuild
            // passes (the heavy synchronous block-introspection path: BlockCapture +
            // MassDistribution + ThrustMap + WeaponProfileBuilder + ArmorMap + MobilityProfile).
            // Strong candidate for the spawn freeze if first rebuild on a large tech is heavy.
            // Tagged with "[TIMING]" for grep; remove with the rest of the diagnostic block.
            bool doTime = System.Threading.Interlocked.Increment(ref VehicleRebuildTimingCounter) <= VehicleRebuildTimingCap;
            int rebuildIdx = doTime ? VehicleRebuildTimingCounter - 1 : -1;
            System.Diagnostics.Stopwatch swRebuildTotal = doTime ? System.Diagnostics.Stopwatch.StartNew() : null;
            System.Diagnostics.Stopwatch swRebuild = doTime ? System.Diagnostics.Stopwatch.StartNew() : null;

            // Chassis (Step 8a/8b): production pipeline only. ChassisCapture walks the tank
            // once, populating the typed BlockCatalog lazily; mass/thrust/weapons/armor all
            // consume the BlockInstancePose[] directly. No BlockObservation, no role-override.
            var poses = Vehicle.ChassisCapture.Capture(Tank, SmartRuntime.BlockCatalog);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] tech '" + (Tank != null ? Tank.name : "<null>") + "' ChassisCapture (" + (poses != null ? poses.Length : 0) + " poses): " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var mass = MassDistribution.Compute(poses);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] MassDistribution: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var thrust = Vehicle.ThrustField.Compute(poses, SmartRuntime.BlockCatalog, mass.TotalMass);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] ThrustField: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var weapons = WeaponProfileBuilder.Build(poses, SmartRuntime.BlockCatalog);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] WeaponProfileBuilder: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            // P3 Item 5: ArmorMapPolicy.UseRealSpecHP gates the catalog-backed overload
            // (per-block ModuleDamage.maxHealth * pose.HpFraction) vs the v0.1 mass-as-HP
            // fallback. Default OFF preserves v0.1 face-weakness ranking bit-identically.
            // P11 T1 Item 44: when policy is ON the v0.2 path becomes the live armor;
            // compute the v0.1 grid in parallel ONCE per tech to feed ArmorMapParityGate.
            // Dedup at the gate means subsequent rebuilds skip the second Compute cost.
            ArmorMap armor;
            if (Vehicle.ArmorMapPolicy.UseRealSpecHP)
            {
                armor = ArmorMap.Compute(poses, SmartRuntime.BlockCatalog, new Vector3Int(8, 8, 8));
                var armorV01 = ArmorMap.Compute(poses, new Vector3Int(8, 8, 8));
                Vehicle.ArmorMapParityGate.TryEmitOnce(TechId.Value, armor, armorV01);
            }
            else
            {
                armor = ArmorMap.Compute(poses, new Vector3Int(8, 8, 8));
            }
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] ArmorMap: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var mobility = MobilityProfile.Derive(mass, thrust, armor);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] MobilityProfile: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var kindCounts = Vehicle.BlockKindCounts.From(poses, SmartRuntime.BlockCatalog);
            var snapshot = new VehicleModelSnapshot(
                TechId.Value,
                LastObservationTick,
                mass, thrust, weapons, armor,
                KinematicTracker.Latest, mobility,
                kindCounts);
            VehicleBuffer.Write(snapshot);
            if (doTime) DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] TOTAL: " + swRebuildTotal.ElapsedMilliseconds + "ms");
        }

        // [TEMP DIAGNOSTIC] Static counter so a session-wide cap of timing logs avoids spam
        // when many techs hit their 30-tick rebuild boundaries together. Lives at the type
        // level rather than instance so all techs share the cap.
        internal static int VehicleRebuildTimingCounter = 0;
        internal const int VehicleRebuildTimingCap = 10;

        // Sum every BlockKindCounts field so the probe gets a block-count total without
        // adding a method to the (struct) BlockKindCounts file.
        private static int SumKindCounts(BlockKindCounts k)
        {
            return k.Structural + k.Wheel + k.Hover + k.Booster + k.Wing + k.Walker
                + k.WeaponFixed + k.WeaponTurret + k.Anchor + k.Conveyor + k.Holder
                + k.Producer + k.Repair + k.Shield + k.EnergyStore + k.Generator
                + k.Drill + k.GyroStabilizer;
        }

        // Build a WeaponAggregateSnapshot from the live vehicle snapshot. WeaponProfile
        // is the existing rebuild output (no extra walk); count and the aggregate stats
        // collapse to a single pass. Kept light: ReadyToFireCount is a heuristic of
        // CooldownRemaining<=0; MeleeWeaponCount counts profiles with Range<=2m (engine
        // hammer/drill convention).
        private static WeaponAggregateSnapshot BuildWeaponAggregateFromSnapshot(VehicleModelSnapshot vsnap)
        {
            if (vsnap == null || vsnap.Weapons == null || vsnap.Weapons.Count == 0)
                return WeaponAggregateSnapshot.Empty;
            int total = vsnap.Weapons.Count;
            int melee = 0, ready = 0, energy = 0, gunCount = 0, beamMelee = 0;
            float rangeMax = 0f, rangeSum = 0f, fireSum = 0f, dmgSum = 0f, muzzleSum = 0f;
            for (int i = 0; i < total; i++)
            {
                var w = vsnap.Weapons[i];
                if (w.Range > rangeMax) rangeMax = w.Range;
                rangeSum += w.Range;
                fireSum += w.FireRateHz;
                dmgSum += w.DamagePerProjectile;
                muzzleSum += w.ProjectileVelocity;
                if (w.CooldownRemaining <= 0f) ready++;
                if (w.IsEnergyWeapon) energy++;
                if (w.Range <= 2f) { melee++; beamMelee++; }
                else gunCount++;
            }
            float inv = total > 0 ? 1f / total : 0f;
            return new WeaponAggregateSnapshot(
                weaponCount: total,
                meleeWeaponCount: melee,
                readyToFireCount: ready,
                rangeMax: rangeMax,
                rangeMean: rangeSum * inv,
                fireRateMean: fireSum * inv,
                damagePerShotMean: dmgSum * inv,
                muzzleVelMean: muzzleSum * inv,
                energyWeaponFraction: energy * inv,
                kindMixGun: gunCount * inv,
                kindMixBeamMelee: beamMelee * inv);
        }
    }

    /// <summary>
    /// Per-team workers and tech registry. One instance per active team observed
    /// by Smart. Owns this team's StrategicPlanner + Coordinator + per-tech registry,
    /// and supplies the cross-thread snapshots both workers consume.
    ///
    /// Construction starts the long-running planner/coordinator workers via the
    /// shared WorkerPool. Shutdown happens via WorkerLifecycleRegistry cancellation
    /// (no per-team teardown needed beyond clearing the runtime map).
    ///
    /// Per ARCHITECTURE §3 host-authority + COORDINATION-CONTRACT §8 lifecycle.
    /// </summary>
    /// <summary>
    /// L-019: TeamRuntime lifecycle FSM. Transitions are one-way and atomic via
    /// Interlocked.CompareExchange. Daemons read State via Volatile.Read so a worker mid-loop
    /// sees the transition on the next iteration without lock acquisition.
    ///   - Active:   normal operation; RegisterTech accepts new state
    ///   - Draining: tech count dropped to 0 + grace timer started; RegisterTech REFUSES
    ///   - Disposed: reaper has called Dispose() and removed from _teams; further reads no-op
    /// </summary>
    internal enum TeamLifecycleState : int
    {
        Active = 0,
        Draining = 1,
        Disposed = 2,
    }

    internal sealed class TeamRuntime : IDisposable
    {
        public TeamId TeamId { get; }
        public StrategicPlanner Planner { get; }
        public Coordinator Coordinator { get; }

        /// <summary>
        /// L-063: monotonic creation counter (incremented in GetOrCreateTeam every new
        /// instance). Lets stale callers detect "the TeamRuntime I cached is a different
        /// instance than the current one for this TeamId" — important when a team is
        /// reaped, then a new tech joins with the same TeamId and a fresh TeamRuntime is
        /// created. Compare via TeamId+Generation pair.
        /// </summary>
        public long Generation { get; internal set; }

        // L-019: lifecycle FSM. Read via Volatile.Read(ref _state) from daemon threads;
        // write via Interlocked.CompareExchange (one-way transitions only).
        private int _state = (int)TeamLifecycleState.Active;
        public TeamLifecycleState State => (TeamLifecycleState)System.Threading.Volatile.Read(ref _state);

        // L-019: per-team RW-lock guarding _techs against the race between MigrateTech
        // (L-043: atomic two-team operation) and daemon iteration. Reader-writer because
        // daemon snapshots are the hot path (every coordinator tick); migrations are rare.
        // IDisposable kernel handle — released in Dispose() so reaped teams don't leak.
        internal readonly System.Threading.ReaderWriterLockSlim _techsLock =
            new System.Threading.ReaderWriterLockSlim(System.Threading.LockRecursionPolicy.NoRecursion);

        // Snapshot of techs currently driven by Smart on this team.
        private readonly ConcurrentDictionary<TechId, SmartPerTechState> _techs =
            new ConcurrentDictionary<TechId, SmartPerTechState>();

        // P5 Item 22 (REV 6): shared empty-LOS sentinel for BuildTeamSnapshot. RoleAssignment.
        // NeedsScout only reads .Count on each entry, so a single static List instance is
        // safe to alias across teams + across snapshots. Preserves REV 3 S1 NeedsScout
        // TRUE-always behavior when LineOfSightProducer.Enabled=false (every tech gets a
        // count==0 entry → NeedsScout returns TRUE on first iteration).
        private static readonly System.Collections.Generic.List<TechId> _sharedEmptyTechIdList =
            new System.Collections.Generic.List<TechId>(0);

        // Plan-tick counter used by StrategicState (resets when plan type changes).
        private int _planTickCount;
        private PlanLibrary.PlanType _lastPlanType = PlanLibrary.PlanType.Hold;

        public TeamRuntime(TeamId teamId, WorkerPool pool)
        {
            TeamId = teamId;
            Planner = new StrategicPlanner(BuildStrategicState);
            Coordinator = new Coordinator(Planner.PlanBuffer, BuildTeamSnapshot, PublishGoals, PublishTargets,
                // L-046: hard-stop predicate. Capture `this` so StepOnce reads our live State.
                isActive: () => this.State == TeamLifecycleState.Active);
            // Aether/T2: planner + coordinator are driven by GlobalPlannerDaemon + GlobalCoordinatorDaemon
            // (started once in SmartRuntime.Init). No per-team RunLoop threads — under heavy load the
            // prior 2-threads-per-team pattern proliferated to ~70 long-running threads, contending
            // with AetherFuser for CPU and producing the 500-600 ms preempt spikes observed in the
            // post-Aether diagnostic. Per the 9-agent threading review's T2 patch.
        }

        /// <summary>
        /// L-019: returns true when the tech was registered, false when the team is
        /// Draining/Disposed (caller MUST log [TEAM-LIFECYCLE-REGISTER-REFUSED] and abort
        /// the spawn cleanly — see SmartForm.cs:248 + SmartEventBridge.cs ~356).
        /// State check MUST precede the state.OwnerTeam mutation so a refused registration
        /// doesn't leave OwnerTeam set on a state without team membership.
        /// </summary>
        public bool RegisterTech(SmartPerTechState state)
        {
            if (state == null) return false;
            if (State != TeamLifecycleState.Active)
            {
                DebugTAC_AI.LogWarnFileOnly(
                    "team-lifecycle-register-refused-" + TeamId.Value + "-" + state.TechId.Value,
                    "[TEAM-LIFECYCLE-REGISTER-REFUSED] team=" + TeamId.Value
                    + " tech=" + state.TechId.Value + " state=" + State);
                return false;
            }
            state.OwnerTeam = this;
            _techs[state.TechId] = state;
            // FEATURE-EXPANSION-PLAN Phase-4: pre-allocate the StrategicStateVector slot so
            // the first extractor Publish doesn't race a lazy GetOrAdd. Optional per buffer
            // contract — lazy alloc works either way — but matches the OnTechSpawn/Recycle
            // pairing that Smart's lifecycle audit (L-028) treats as the canonical edge.
            SmartRuntime.StrategicVectors?.OnTechSpawn(state.TechId);
            return true;
        }
        public bool DeregisterTech(TechId id)
        {
            if (_techs.TryRemove(id, out var state)) { state.OwnerTeam = null; return true; }
            return false;
        }
        public int TechCount => _techs.Count;

        /// <summary>L-056: snapshot of TechIds for the leak watchdog's live-set union.</summary>
        public System.Collections.Generic.IEnumerable<TechId> SnapshotTechIds() => _techs.Keys;

        /// <summary>
        /// L-019: atomic one-way transition Active→Draining→Disposed. Returns true when
        /// the caller's transition won, false when the state was already at or past target
        /// (caller should treat as no-op, not error). Logs every winning transition.
        /// </summary>
        public bool TryTransitionTo(TeamLifecycleState target)
        {
            int targetInt = (int)target;
            while (true)
            {
                int current = System.Threading.Volatile.Read(ref _state);
                if (current >= targetInt) return false;     // already at or past target
                if (System.Threading.Interlocked.CompareExchange(ref _state, targetInt, current) == current)
                {
                    DebugTAC_AI.LogWarnFileOnly("team-lifecycle-" + TeamId.Value + "-" + target,
                        "[TEAM-LIFECYCLE] team=" + TeamId.Value
                        + " state=" + (TeamLifecycleState)current + "→" + target);
                    return true;
                }
                // CAS failed; another writer raced — re-read and retry.
            }
        }

        /// <summary>
        /// L-019: release the RWLockSlim kernel handle. Called from TeamReaperDaemon (L-044)
        /// after the Disposed transition has been won AND the team has been TryRemove'd from
        /// SmartRuntime._teams. After Dispose the TeamRuntime is unreferenced and GC-eligible.
        /// </summary>
        public void Dispose()
        {
            try { _techsLock.Dispose(); }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarning("TeamRuntime.Dispose[" + TeamId.Value + "]: " + ex.Message);
            }
        }

        /// <summary>P11 T4 Item 54: per-tech state lookup for cross-subsystem accessors.</summary>
        internal SmartPerTechState TryGetTechState(TechId id)
        {
            return _techs.TryGetValue(id, out var state) ? state : null;
        }

        /// <summary>
        /// Per-team centroid of Smart-driven tech positions, EXCLUDING <paramref name="excludeSelf"/>.
        /// Returns true + centroid when at least one other tech is registered on this team;
        /// returns false (and centroid undefined) when the team is solo. Caller MUST gate on
        /// the bool, NOT on a Vector3.zero sentinel (world origin is a valid position).
        /// Used by Smart identity goal sources via IdentityContext.HasAllies / TeamCentroid.
        /// Main thread only.
        /// </summary>
        public bool TryGetCentroid(TechId excludeSelf, out Vector3 centroid)
        {
            centroid = Vector3.zero;
            int count = 0;
            foreach (var kv in _techs)
            {
                if (kv.Key == excludeSelf) continue;
                centroid += kv.Value.KinematicBuffer.Read().PositionWorld;
                count++;
            }
            if (count == 0) return false;
            centroid /= count;
            return true;
        }

        /// <summary>
        /// Snapshot of every Smart-driven friendly tech's last-observed position, EXCEPT
        /// <paramref name="excludeSelf"/>. Called per-tech-tick by the weapon-fire controller.
        /// </summary>
        public System.Collections.Generic.List<Vector3> SnapshotFriendlyPositionsExcluding(TechId excludeSelf)
        {
            var list = new System.Collections.Generic.List<Vector3>(_techs.Count);
            foreach (var kv in _techs)
            {
                if (kv.Key == excludeSelf) continue;
                var k = kv.Value.KinematicBuffer.Read();
                list.Add(k.PositionWorld);
            }
            return list;
        }

        /// <summary>
        /// BUG-026: walk the fused belief set for techs hostile to this team and surface a
        /// VehicleModelSnapshot for any that Smart owns (i.e., they are Smart-driven on a
        /// different team). For techs Smart doesn't own (non-Smart enemies, neutrals), the
        /// dict carries no entry — ThreatFieldBuilder falls back to its baseline rating /
        /// radius constants for those, preserving the v0.1.0 cost-bound behavior.
        ///
        /// Worker-thread safe: BeliefSnapshot is immutable; GetSmartPerTechVehicleBuffer
        /// reads VehicleBuffer via DoubleBuffer (lock-free). Allocates a dict per call
        /// because ThreatFieldBuilder needs a stable view for its build pass.
        /// </summary>
        internal System.Collections.Generic.IReadOnlyDictionary<TechId, VehicleModelSnapshot> EnemyVehicleSnapshots()
        {
            var beliefs = SmartRuntime.World?.FusedBuffer.Read();
            if (beliefs == null || beliefs.ByTech == null || beliefs.ByTech.Count == 0)
                return null;
            Dictionary<TechId, VehicleModelSnapshot> result = null;
            foreach (var pair in beliefs.ByTech)
            {
                if (pair.Value.Team.Value == TeamId.Value) continue;
                var buf = SmartRuntime.GetSmartPerTechVehicleBuffer(pair.Key);
                if (buf == null) continue;
                if (result == null) result = new Dictionary<TechId, VehicleModelSnapshot>();
                result[pair.Key] = buf.Read();
            }
            return result;
        }

        // ---- StrategicState assembly (called from StrategicPlanner worker) ----
        private StrategicState BuildStrategicState()
        {
            var beliefSnap = SmartRuntime.World?.FusedBuffer.Read();
            if (beliefSnap == null) return null;

            var friendly = new List<StrategicTechSummary>(_techs.Count);
            foreach (var kv in _techs)
            {
                if (!beliefSnap.ByTech.TryGetValue(kv.Key, out var b)) continue;
                friendly.Add(SummarizeBelief(kv.Key, b, kv.Value.VehicleBuffer.Read()));
            }

            // Hostiles: every belief whose tech is not on our team and not in our registry.
            var hostile = new List<StrategicTechSummary>();
            foreach (var pair in beliefSnap.ByTech)
            {
                if (_techs.ContainsKey(pair.Key)) continue;
                if (pair.Value.Team.Value == TeamId.Value) continue;
                hostile.Add(SummarizeBelief(pair.Key, pair.Value, VehicleModelSnapshot.Empty(pair.Key.Value)));
            }

            var currentPlan = Planner.PlanBuffer.Read();
            if (currentPlan.Type != _lastPlanType) { _lastPlanType = currentPlan.Type; _planTickCount = 0; }
            _planTickCount++;

            return new StrategicState(friendly, hostile, currentPlan, _planTickCount);
        }

        private static StrategicTechSummary SummarizeBelief(TechId id, BeliefState b, VehicleModelSnapshot v)
        {
            // P11 T2 Item 51: HP from HealthSidecar when available; v0.1 proxy (1.0f) otherwise.
            // This unblocks the ActionValueEstimator's reward signal — the SummarizeBelief output
            // is what `StrategicState.Friendly[].Health` returns, which Coordinator reads.
            float health = SmartRuntime.Health != null ? SmartRuntime.Health.Get(id) : 1.0f;
            // Guard NaN / out-of-range to keep StrategicTechSummary consumers happy.
            if (float.IsNaN(health) || health < 0f) health = 0f;
            if (health > 1f) health = 1f;
            float threat = v.Weapons != null ? Mathf.Min(v.Weapons.Count, 8) / 8f : 0f;
            float mobility = v.Mobility.TopSpeedForward;
            var pv = b.PositionVariance;
            float uncertainty = pv.x + pv.z;
            return new StrategicTechSummary(
                id, b.PositionMean, b.VelocityMean, b.HeadingMean,
                health, threat, mobility, uncertainty);
        }

        // ---- TeamSnapshot assembly (called from Coordinator worker) ----
        private TeamSnapshot BuildTeamSnapshot()
        {
            var beliefSnap = SmartRuntime.World?.FusedBuffer.Read();
            if (beliefSnap == null) return null;

            var ourTechs = new List<TechId>(_techs.Count);
            var vehiclesByTech = new Dictionary<TechId, VehicleModelSnapshot>(_techs.Count);
            var losCoverage = new Dictionary<TechId, List<TechId>>(_techs.Count);

            // P5 Item 22 (REV 6): read LOS coverage from LineOfSightProducer when Enabled.
            // When disabled, use the shared per-tech empty sentinel to preserve REV 3 S1
            // NeedsScout TRUE-always (per-tech count==0). When enabled, read the per-team
            // published snapshot (Volatile.Read through DoubleBuffer — lock-free worker read).
            // The lookup is per-tech: techs the producer reported get real lists; others get
            // the sentinel.
            System.Collections.Generic.IReadOnlyDictionary<TechId, List<TechId>> losSnapshot = null;
            if (Coordination.LineOfSightProducer.Enabled)
                losSnapshot = Coordination.LineOfSightProducer.SnapshotForTeam(TeamId);

            foreach (var kv in _techs)
            {
                ourTechs.Add(kv.Key);
                vehiclesByTech[kv.Key] = kv.Value.VehicleBuffer.Read();
                List<TechId> seen;
                if (losSnapshot != null && losSnapshot.TryGetValue(kv.Key, out seen))
                    losCoverage[kv.Key] = seen;
                else
                    losCoverage[kv.Key] = _sharedEmptyTechIdList;  // REV 3 S1 sentinel
            }

            // P11 T1 Item 46: LOS coverage parity emit. When the producer is ON, count
            // techs / total enemies seen / techs-with-zero-coverage and emit once per team.
            // Per-team dedup at the gate (ConcurrentDictionary), so subsequent snapshots
            // skip the count. Worker-thread call is safe — the gate uses ConcurrentDictionary.
            if (Coordination.LineOfSightProducer.Enabled)
            {
                int techCount = ourTechs.Count;
                int totalEnemiesSeen = 0;
                int techsWithZeroCoverage = 0;
                foreach (var lk in losCoverage)
                {
                    int n = lk.Value != null ? lk.Value.Count : 0;
                    totalEnemiesSeen += n;
                    if (n == 0) techsWithZeroCoverage++;
                }
                Coordination.LosCoverageParityGate.TryEmitOnce(TeamId.Value, techCount, totalEnemiesSeen, techsWithZeroCoverage);
            }

            var hostileBeliefs = new List<BeliefState>();
            foreach (var pair in beliefSnap.ByTech)
            {
                if (_techs.ContainsKey(pair.Key)) continue;
                if (pair.Value.Team.Value == TeamId.Value) continue;
                hostileBeliefs.Add(pair.Value);
            }

            // P7 Item 17: stamp TeamId so the Coordinator-side edge tracker can pass it
            // to PlanTransitionPublisher without needing a back-ref to TeamRuntime.
            return new TeamSnapshot(ourTechs, hostileBeliefs, beliefSnap, vehiclesByTech, losCoverage, TeamId);
        }

        // ---- Per-tech goal publish (called from Coordinator worker) ----
        private void PublishGoals(Dictionary<TechId, TacticalGoal> goals)
        {
            if (goals == null) return;
            foreach (var kv in goals)
            {
                if (_techs.TryGetValue(kv.Key, out var state))
                    state.WriteExternalGoal(kv.Value);
            }
        }

        // ---- Per-tech target publish (called from Coordinator worker) ----
        private void PublishTargets(Dictionary<TechId, TargetAssignment> targets)
        {
            if (targets == null) return;
            foreach (var kv in targets)
            {
                if (_techs.TryGetValue(kv.Key, out var state))
                    state.WriteExternalTarget(kv.Value);
            }
        }
    }

    /// <summary>
    /// Smart's process-wide globals: WorkerPool, WorldModel, per-team runtimes,
    /// lifecycle hooks. Initialized by SmartForm.InitGlobal; shut down by
    /// SmartForm.DeInitGlobal.
    ///
    /// Static for easy access from any subsystem; the lifecycle is single-instance
    /// per form-active session per ARCHITECTURE §3.2 host-authority.
    /// </summary>
    public static class SmartRuntime
    {
        public static WorkerPool Pool { get; private set; }
        public static WorldModel World { get; private set; }
        public static AetherFuser Perception { get; private set; }

        /// <summary>
        /// Chassis (REV 3.1): process-wide typed per-block-type catalog. Init creates;
        /// Shutdown AFTER WorkerLifecycleRegistry.CancelAllAndJoin calls Reset (workers
        /// quiesced before archetype references drop). Worker-safe read via TryGet; writes
        /// (GetOrProbe) are main-thread-only and triggered exclusively from ChassisCapture.
        /// </summary>
        public static Vehicle.TypedBlockCatalog BlockCatalog { get; private set; }

        /// <summary>
        /// P4 Item 10: per-tech intent sidecar. Producer-only at v0.2 ship (no v0.2 consumer
        /// reads from it). Created in Init after WorldModel/BlockCatalog; cleared in Shutdown
        /// before BlockCatalog reset. Forget hooked from Deregister + WorldModel.DeregisterTech.
        /// </summary>
        public static IntentRegistry IntentSidecar { get; private set; }

        /// <summary>
        /// P4 Item 11: per-victim damage-event ring buffer. Subscribes to
        /// WorldEventBus&lt;DamageObserved&gt; via DamageHintBuffer.Wire at Init. No v0.2
        /// consumer reads from it (P6 RepairSupport uses HpFraction only for v0.2; v0.3
        /// facing-threat orbit would consume).
        /// </summary>
        public static DamageHintBuffer DamageHints { get; private set; }

        /// <summary>
        /// P6 Item 26: per-tech tank-wide HpFraction sidecar (block-survival ratio per
        /// TankAIHelper.cs:3267 semantic). Populated by SmartForm.ObserveWorldTechsIfDue;
        /// read by RepairSupportGoalSource. Lifecycle mirrors IntentSidecar/DamageHints.
        /// </summary>
        public static HealthSidecar Health { get; private set; }

        // FEATURE-EXPANSION-PLAN Phase-4 wiring: the four new feature-pipeline sidecars
        // plus the per-tech StrategicStateVector publisher and the extractor daemon. All
        // ctored in Init after Health, torn down in Shutdown after WorkerLifecycleRegistry
        // cancels the extractor.
        public static NearestTechCache NearestTechCache { get; private set; }
        public static RecentDamageDealtAccumulator DealtAccumulator { get; private set; }
        public static WeaponFireBuffer WeaponFires { get; private set; }
        public static CargoStatePublisher CargoState { get; private set; }
        public static StrategicStateBuffer StrategicVectors { get; private set; }
        public static StrategicStateExtractor StrategicExtractor { get; private set; }

        // Director-side projectile-fire counter. OrbitNoFireGuard reads CountWithin to
        // detect zero-fire over its window. Setter swaps in the real publisher on install;
        // consumers null-tolerant.
        public static Director.Publishers.ProjectileFiredPublisher Projectiles { get; set; }

        // Per-team runtimes (Planning + Coordination workers scoped per team).
        // Lazy creation on first OnTechSpawn for each team — happens on the main thread,
        // so the GetOrAdd factory race is not a concern in practice.
        private static readonly ConcurrentDictionary<TeamId, TeamRuntime> _teams =
            new ConcurrentDictionary<TeamId, TeamRuntime>();

        public static bool IsRunning => Pool != null && Pool.IsRunning && World != null;

        /// <summary>
        /// Phase 5 (FIX-PLAN.md) host-authority flag. Defaults true so single-player and
        /// pre-host-aware test runs work unchanged; updated from <see cref="SmartForm.Operations"/>
        /// and <see cref="SmartForm.Directors"/> on each tick. Read by every long-running
        /// worker at the top of each iteration — when false, workers idle without dispatching
        /// substantive work per ARCHITECTURE §3.2. Declared `volatile` so the cross-thread
        /// read from worker loops observes main-thread writes promptly.
        /// </summary>
        public static volatile bool IsHost = true;

        /// <summary>
        /// L-004: Unity-pause flag. SmartLifecycleShim (L-025) sets this from
        /// OnApplicationPause / OnApplicationFocus on the main thread; every daemon checks
        /// IsPaused BEFORE IsHost (L-026) so a paused host stops doing work without losing
        /// its authority bit. Declared volatile for the same cross-thread visibility reason
        /// as <see cref="IsHost"/>.
        /// </summary>
        public static volatile bool IsPaused = false;

        /// <summary>
        /// L-059: producer-side gate for training event enqueue, ref-counted so
        /// concurrent OnHostLost / OnHostGained / Director rollback do not clobber
        /// each other. Effective flag is (disableCount == 0 &amp;&amp; IsHost).
        /// Producers (LearningService.OnDamageObserved, LeadResidualRecorder.RecordObserved,
        /// PlanTransitionPublisher.Publish) check the effective flag, NOT the bare counter.
        /// </summary>
        private static int _acceptingDisableCount;
        public static bool AcceptingTrainingEvents
        {
            get { return Volatile.Read(ref _acceptingDisableCount) == 0 && IsHost; }
        }
        public static void AcceptingTrainingEvents_Inc()
        {
            Interlocked.Increment(ref _acceptingDisableCount);
        }
        public static void AcceptingTrainingEvents_Dec()
        {
            int newVal = Interlocked.Decrement(ref _acceptingDisableCount);
            if (newVal < 0)
            {
                // Underflow — defensively clamp back to 0 and journal.
                Interlocked.Exchange(ref _acceptingDisableCount, 0);
                DebugTAC_AI.LogWarnFileOnly("smart-runtime-accepting-underflow",
                    "[SMART-RUNTIME] AcceptingTrainingEvents_Dec underflow — counter clamped to 0");
            }
        }

        // Director pause channel. Ref-counted so multiple verbs can pause
        // concurrently and trainers stay paused until everyone releases.
        private static int _directorPaused;
        public static bool IsDirectorPaused
        {
            get { return Volatile.Read(ref _directorPaused) != 0; }
        }
        public static void RequestDirectorPause()
        {
            Interlocked.Increment(ref _directorPaused);
        }
        public static void ReleaseDirectorPause()
        {
            int newVal = Interlocked.Decrement(ref _directorPaused);
            if (newVal < 0)
            {
                Interlocked.Exchange(ref _directorPaused, 0);
                DebugTAC_AI.LogWarnFileOnly("smart-runtime-director-pause-underflow",
                    "[SMART-RUNTIME] ReleaseDirectorPause underflow — counter clamped to 0");
            }
        }

        // Pause-token surface — thin delegate to TrainerBarrier for callers that don't
        // depend on the Coordination namespace directly. Mutual-exclusion lock on
        // _pausingOwner so host-change-during-rollback serialises.
        public static void AcquirePauseToken(string owner)
        {
            Coordination.TrainerBarrier.AcquirePauseToken(owner);
        }
        public static void ReleasePauseToken(string owner)
        {
            Coordination.TrainerBarrier.ReleasePauseToken(owner);
        }

        // Live tech census used by ConstraintEngine gating + digest. O(teams) — small
        // (~10 teams worst case) so unbounded enumeration is fine. Walks the roster
        // every call; no caching.
        public static int LiveSmartTechCount
        {
            get
            {
                int total = 0;
                foreach (var kv in _teams)
                {
                    var team = kv.Value;
                    if (team == null) continue;
                    total += team.TechCount;
                }
                return total;
            }
        }

        /// <summary>
        /// TechId → SmartIdentity lookup walking every team's per-tech registry. Used by
        /// IdentityReplayBank to stamp envelopes at tee time. Returns Generic when the
        /// tech is unknown to any Smart team (caller treats Generic as the catch-all cell).
        /// </summary>
        public static Identity.SmartIdentity TryGetSmartIdentity(TechId id)
        {
            foreach (var kv in _teams)
            {
                var team = kv.Value;
                if (team == null) continue;
                var state = team.TryGetTechState(id);
                if (state != null) return state.IdentityStamp.Identity;
            }
            return Identity.SmartIdentity.Generic;
        }

        /// <summary>
        /// Per-identity live tech count — counts SmartPerTechState entries whose
        /// IdentityStamp.Identity matches. Used by §4 hunter / gatherer gates.
        /// </summary>
        public static int LiveSmartTechCountFor(Identity.SmartIdentity identity)
        {
            int total = 0;
            foreach (var kv in _teams)
            {
                var team = kv.Value;
                if (team == null) continue;
                foreach (var techId in team.SnapshotTechIds())
                {
                    var state = team.TryGetTechState(techId);
                    if (state == null) continue;
                    if (state.IdentityStamp.Identity == identity) total++;
                }
            }
            return total;
        }

        /// <summary>
        /// Aggregate tech-minutes for (identity, scenario) over <paramref name="windowSec"/>.
        /// Scenario id is informational - per-(identity,scenario) life-history is not
        /// tracked here; returns live-count × windowMinutes as the lower-bound denominator
        /// used by ConstraintEngine guard-rate normalization. INSUFFICIENT_DATA at caller
        /// is keyed on the live count, not the minutes.
        /// </summary>
        public static float TechLifeMinutesInWindow(Identity.SmartIdentity identity, int scenario, int windowSec)
        {
            if (windowSec <= 0) return 0f;
            int live = LiveSmartTechCountFor(identity);
            if (live <= 0) return 0f;
            return live * (windowSec / 60f);
        }

        // L-004: latch protecting RequestShutdown so concurrent SmartLifecycleShim hooks
        // (Application.quitting + Singleton.ApplicationQuitEvent + OnDestroy) all resolve to
        // exactly one Shutdown invocation regardless of fire order or thread.
        private static int _shutdownRequested;

        /// <summary>
        /// L-004: shutdown entry point for the SmartLifecycleShim. CompareExchange latch
        /// ensures exactly one caller actually delegates to Shutdown; losers log a no-op
        /// line for observability. Idempotency of the underlying Shutdown (Pool==null guard
        /// at line 735) makes the latch defensive belt-and-suspenders — losing the race
        /// would still produce correct behavior, but the log distinction matters for
        /// diagnosing which Unity hook actually fired first across sessions.
        /// </summary>
        public static void RequestShutdown(string source)
        {
            int prior = System.Threading.Interlocked.CompareExchange(ref _shutdownRequested, 1, 0);
            if (prior == 0)
            {
                DebugTAC_AI.Log("Smart.Runtime: RequestShutdown WIN source='" + (source ?? "<null>") + "'");
                try { Shutdown(); }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Runtime: RequestShutdown.Shutdown threw "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }
            else
            {
                DebugTAC_AI.LogWarnFileOnly("smart-shutdown-loser-" + (source ?? "anon"),
                    "Smart.Runtime: RequestShutdown LOSER source='" + (source ?? "<null>")
                    + "' (already shut down or in progress)");
            }
        }

        public static void Init()
        {
            if (Pool != null) return; // idempotent

            // P9 Item 31: reset the calibration aggregator so a fresh Init doesn't carry
            // stale trip data from a prior Init/Shutdown cycle within the same process.
            Threading.CircuitBreakerCalibration.ResetSession();
            // P10 Item 41 (REV 7): reset the periodic VEHICLE-PARITY sweep counters too.
            Tests.VehicleParityCatcher.ResetSession();

            // Register identity goal sources.
            // P6 Item 27 (REV 5/7): Generic IS registered as of v0.2. Its Produce delegates
            // to the per-controller TacticalOptimizer via IdentityContext.TacticalGoalHandle
            // (wrapped at the dispatch site in ContinuousController). Per-instance Adam state
            // is preserved; behavior is bit-identical to the v0.1 inline bypass at the
            // optimizer call level.
            // P6 Items 26 + 28: RepairSupport + Patrol register their goal sources here.
            // Their classifier rows are gated default-OFF on SmartIdentityTuning flags, so
            // these entries sit unused at default until the user flips the flag + spawn-tests.
            SmartIdentityRegistry.Register(new GenericGoalSource());          // P6 Item 27
            SmartIdentityRegistry.Register(new HunterGoalSource());
            SmartIdentityRegistry.Register(new BaseGoalSource());
            SmartIdentityRegistry.Register(new SniperGoalSource());
            SmartIdentityRegistry.Register(new GathererGoalSource());
            SmartIdentityRegistry.Register(new AircraftSupportGoalSource());
            SmartIdentityRegistry.Register(new AircraftHunterGoalSource());
            SmartIdentityRegistry.Register(new RepairSupportGoalSource());    // P6 Item 26
            SmartIdentityRegistry.Register(new PatrolGoalSource());           // P6 Item 28

            // [TEMP DIAGNOSTIC] Per-sub-call timing to bisect the observed ~3s init freeze.
            // Tagged with "[TIMING]" so the lines are greppable; remove this block once the
            // freeze cause is identified and addressed.
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            Pool = new WorkerPool("Smart", WorkerPool.DefaultWorkerCount);
            DebugTAC_AI.Log("Smart.Init[TIMING] WorkerPool ctor (" + Pool.WorkerCount + " threads): " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            World = new WorldModel();
            TechLifecycleRegistry.Register(World);   // L-028: WorldModel.PerTechState sidecar
            DebugTAC_AI.Log("Smart.Init[TIMING] WorldModel ctor: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Chassis Step 2: catalog wired here, populated lazily by ChassisCapture's
            // GetOrProbe calls. Empty at Init.
            BlockCatalog = new Vehicle.TypedBlockCatalog();
            DebugTAC_AI.Log("Smart.Init[TIMING] BlockCatalog ctor: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // P2 Item 8: optional eager prewarm. Default OFF — when Enabled is true,
            // walks ManSpawn.inst.GetLoadedTankBlockNames() and primes every prefab.
            Vehicle.CatalogPrewarm.Run(BlockCatalog);
            DebugTAC_AI.Log("Smart.Init[TIMING] CatalogPrewarm: " + sw.ElapsedMilliseconds + "ms (Enabled="
                + Vehicle.CatalogPrewarm.Enabled + ", probed=" + Vehicle.CatalogPrewarm.LastPrewarmCount + ")");
            sw.Restart();

            // P2 Item 9: register the hot-reload invalidation surface. Subscribes the
            // catalog's InvalidateAll to the OnAllInvalidated event so any future external
            // hot-reload trigger (P10 console command, v0.3 engine event) flushes the
            // cache. RegisterHotReloadHook subscribes to InvokeHelper.BlocksPostChangeEvent
            // (P2 cleanup pass — see Implementation Gaps Log in V0.2-PLAN-REV7).
            var catalogRef = BlockCatalog;
            Vehicle.CatalogInvalidation.OnAllInvalidated += () => catalogRef?.InvalidateAll();
            Vehicle.CatalogInvalidation.OnTypeInvalidated += tk => catalogRef?.InvalidateType(tk);
            Vehicle.CatalogInvalidation.RegisterHotReloadHook();
            DebugTAC_AI.Log("Smart.Init[TIMING] CatalogInvalidation hook: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // P4 Item 10: IntentRegistry sidecar — empty dict at startup; zero v0.2 consumer.
            // P4 Item 11: DamageHintBuffer sidecar — subscribes to WorldEventBus<DamageObserved>
            // via Wire; multi-subscriber-safe alongside LearningService.OnDamageObserved.
            // P6 Item 26: HealthSidecar — populated by SmartForm.ObserveWorldTechsIfDue;
            // read by RepairSupportGoalSource.
            IntentSidecar = new IntentRegistry();
            DamageHints = new DamageHintBuffer();
            DamageHintBuffer.Wire(DamageHints);
            Health = new HealthSidecar();

            // FEATURE-EXPANSION-PLAN Phase-4: feature-pipeline sidecars. Order matters —
            // RecentDamageDealtAccumulator + CargoStatePublisher + WeaponFireBuffer must
            // exist BEFORE the extractor's ctor; StrategicStateBuffer is the extractor's
            // output. Sidecars register with TechLifecycleRegistry below so Forget(id)
            // fan-out covers them.
            NearestTechCache = new NearestTechCache();
            DealtAccumulator = new RecentDamageDealtAccumulator();
            RecentDamageDealtAccumulator.Wire(DealtAccumulator);
            WeaponFires = new WeaponFireBuffer();
            CargoState = new CargoStatePublisher();
            CargoStatePublisher.Wire(CargoState);
            StrategicVectors = new StrategicStateBuffer();
            // L-028 + L-029: wire all 8 sidecars into TechLifecycleRegistry. L-055 (Wave 2)
            // refactors SmartRuntime.Deregister to enumerate the registry instead of the
            // prior hand-coded fan-out, and TechLeakWatchdog (L-056, Wave 2) walks each
            // sidecar's SnapshotKeys at 60s cadence to detect leaks.
            TechLifecycleRegistry.Register(IntentSidecar);                              // L-028
            TechLifecycleRegistry.Register(DamageHints);                                // L-028
            TechLifecycleRegistry.Register(Health);                                     // L-028
            // FEATURE-EXPANSION-PLAN Phase-4 sidecars — all ITechSidecar implementors.
            TechLifecycleRegistry.Register(DealtAccumulator);
            TechLifecycleRegistry.Register(WeaponFires);
            TechLifecycleRegistry.Register(CargoState);
            // L-028: 5 more — WorldModel (registered later in Init after World construction),
            // LeadResidualRecorder (registered by LearningService.Init via its Instance),
            // TargetObservationSequenceBuffer (registered by LearningService.Init). The
            // 3 wired here are the runtime-state sidecars owned by SmartRuntime itself.
            // L-029: two adapter sidecars wrapping per-service state.
            TechLifecycleRegistry.Register(new TAC_AI.AI.Forms.Smart.World.PathingLastPathsSidecar());
            TechLifecycleRegistry.Register(new TAC_AI.AI.Forms.Smart.World.CoordinatorAssignmentsSidecar());
            DebugTAC_AI.Log("Smart.Init[TIMING] IntentSidecar + DamageHints + Health ctor + Wire: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Phase 3.1 (FIX-PLAN.md): PerceptionWorker was authored but never instantiated
            // or enqueued anywhere (AUDIT R1 1.3). Without it, World.FusedBuffer stayed at
            // BeliefSnapshot.Empty forever and every downstream consumer (Planner,
            // Coordinator, ThreatField, Controller) ran against a dead world. This single
            // wiring fix unblocks the entire downstream chain — multiple planners called it
            // "the highest-leverage edit in the plan" (Plan 2 #6, Plan 6 #2).
            Perception = new AetherFuser(World, World.FusedBuffer);
            Pool.EnqueueLongRunning(Perception.RunLoop, "AetherFuser");
            DebugTAC_AI.Log("Smart.Init[TIMING] AetherFuser ctor + enqueue: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Aether/T2 (9-agent threading review): one global daemon drives every team's
            // Planner and Coordinator. Replaces the prior pattern of 2 threads per team.
            // Under heavy load the prior pattern proliferated to ~70 long-running threads
            // and contended with AetherFuser; collapsing to 2 daemons removes the contention
            // without changing per-team contracts (PlanBuffer / StateBuffer publish surfaces
            // unchanged).
            Pool.EnqueueLongRunning(Planning.GlobalPlannerDaemon.RunLoop, "GlobalPlanner");
            Pool.EnqueueLongRunning(Coordination.GlobalCoordinatorDaemon.RunLoop, "GlobalCoordinator");
            // L-044: TeamReaperDaemon enqueue. Reset clears prior-session grace stamps so a
            // re-init within the same process doesn't carry forward eviction state.
            Coordination.TeamReaperDaemon.Reset();
            Coordination.TeamReaperDaemon.Enqueue(Pool);
            // L-056: TechLeakWatchdog. 60s cadence; logs [TECH-LEAK] on any sidecar key
            // not present in the live SmartRuntime + WorldModel union.
            TAC_AI.AI.Forms.Smart.World.TechLeakWatchdog.Reset();
            TAC_AI.AI.Forms.Smart.World.TechLeakWatchdog.Enqueue(Pool);
            DebugTAC_AI.Log("Smart.Init[TIMING] GlobalPlanner+GlobalCoordinator+TeamReaper+TechLeakWatchdog enqueue: " + sw.ElapsedMilliseconds + "ms");

            // L-049 + L-066: register canonical daemon respawn factories with both watchdogs.
            // Each factory re-enqueues the daemon on the same pool with the canonical label.
            // Captured pool reference is by closure; if Shutdown nulled Pool the factory
            // returns null (EnqueueLongRunning's _disposed guard).
            var poolForFactories = Pool;
            Threading.DaemonWatchdog.Reset();
            Threading.WorkerHealthMonitor.Reset();
            void Reg(string name, Action<System.Threading.CancellationToken> body)
            {
                Func<string> f = () => poolForFactories?.EnqueueLongRunning(body, name);
                Threading.DaemonWatchdog.RegisterCanonical(name, f);
                Threading.WorkerHealthMonitor.RegisterCanonical(name, f);
            }
            Reg("AetherFuser", Perception != null ? new Action<System.Threading.CancellationToken>(Perception.RunLoop) : null);
            Reg("GlobalPlanner", Planning.GlobalPlannerDaemon.RunLoop);
            Reg("GlobalCoordinator", Coordination.GlobalCoordinatorDaemon.RunLoop);
            // BUG-025: register respawn factories for TeamReaper + TechLeakWatchdog (both
            // enqueued just above). Without these, if either dies the watchdog logs
            // factory=missing and skips respawn. AutosaveWorker is enqueued from
            // LearningService.Init; its factory registration lives there.
            {
                var poolRef = poolForFactories;
                Func<string> reaperFactory = () => poolRef != null
                    ? Coordination.TeamReaperDaemon.Enqueue(poolRef)
                    : null;
                Func<string> leakFactory = () => poolRef != null
                    ? TAC_AI.AI.Forms.Smart.World.TechLeakWatchdog.Enqueue(poolRef)
                    : null;
                Threading.DaemonWatchdog.RegisterCanonical("TeamReaper", reaperFactory);
                Threading.WorkerHealthMonitor.RegisterCanonical("TeamReaper", reaperFactory);
                Threading.DaemonWatchdog.RegisterCanonical("TechLeakWatchdog", leakFactory);
                Threading.WorkerHealthMonitor.RegisterCanonical("TechLeakWatchdog", leakFactory);
            }
            sw.Restart();

            PathingService.Init(Pool);
            DebugTAC_AI.Log("Smart.Init[TIMING] PathingService.Init: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // FEATURE-EXPANSION-PLAN Phase-4 daemon: StrategicStateExtractor. Constructed
            // AFTER PathingService.Init so CurrentTerrain is non-null. probeLookup walks
            // every TeamRuntime for a SmartPerTechState matching TechId; null for unknown
            // (daemon skips). Registered with both DaemonWatchdog + WorkerHealthMonitor below.
            StrategicExtractor = new StrategicStateExtractor(
                World, StrategicVectors, NearestTechCache, PathingService.CurrentTerrain,
                Health, DamageHints, DealtAccumulator, WeaponFires, CargoState,
                LookupSelfStateProbe);
            Pool.EnqueueLongRunning(StrategicExtractor.RunLoop, StrategicStateExtractor.CanonicalName);
            // Respawn factory + canonical-roster registration (matches the Reg() pattern at
            // SmartRuntime.cs:1036 — captured pool variable, null-safe). Uses the extractor's
            // CanonicalName const so DaemonWatchdog identity stays in lockstep with the label.
            {
                var extractorRef = StrategicExtractor;
                var poolRef = Pool;
                Func<string> extractorFactory = () => poolRef?.EnqueueLongRunning(
                    extractorRef.RunLoop, StrategicStateExtractor.CanonicalName);
                Threading.DaemonWatchdog.RegisterCanonical(StrategicStateExtractor.CanonicalName, extractorFactory);
                Threading.WorkerHealthMonitor.RegisterCanonical(StrategicStateExtractor.CanonicalName, extractorFactory);
            }
            DebugTAC_AI.Log("Smart.Init[TIMING] StrategicStateExtractor ctor + enqueue: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Phase 5 (FIX-PLAN.md) — AUDIT R1 2.7: the previous path was
            // <cwd>/Mods/SmartAI; LearningService.Init then appended ANOTHER "SmartAI"
            // segment, producing <cwd>/Mods/SmartAI/SmartAI/Profiles. Pass the parent
            // <cwd>/Mods so LearningService's single append produces the spec'd
            // <mod-root>/SmartAI/Profiles. TODO v0.2: replace with KickStart's verified
            // mod-root accessor when the API surface is confirmed.
            string modDir = System.IO.Path.Combine(System.Environment.CurrentDirectory, "Mods");
            LearningService.Init(Pool, modDir);
            DebugTAC_AI.Log("Smart.Init[TIMING] LearningService.Init: " + sw.ElapsedMilliseconds + "ms");

            DebugTAC_AI.Log("Smart.Runtime: initialized with " + Pool.WorkerCount + " workers; perception worker live. [TIMING] TOTAL " + swTotal.ElapsedMilliseconds + "ms");

            // P10 Item 42 (REV 7): single, grep-able "v0.2 booted" line so the spawn-test
            // operator can confirm the build under test is the v0.2 build. All P1-P9 default-OFF
            // gates keep this build bit-identical to v0.1 at default tunings; flip the relevant
            // gate via console (smart.tunables.list / SmartConsoleCommands) before spawn-testing.
            DebugTAC_AI.Log("Smart.Runtime: v0.2 boot complete — spawn-test green-light");
        }

        public static void Shutdown()
        {
            if (Pool == null) return;

            DebugTAC_AI.Log("Smart.Runtime: shutting down.");

            // L-067: tell the watchdogs we're tearing down so they stop alerting + don't
            // attempt respawns mid-Shutdown (which would fight CancelAllAndJoin).
            try { Threading.WorkerHealthMonitor.BeginShutdown(); } catch { }
            // Director lifecycle plug between watchdog quiet + barrier teardown.
            // BeginShutdown sets the kill switch, blocks up to 10s for in-flight rollback,
            // never uses Thread.Abort.
            try { Director.TrainingDirector.BeginShutdown(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime.Shutdown: Director.BeginShutdown threw: " + ex.Message); }
            try { Coordination.TrainerBarrier.ClearAll(); } catch { }

            // L-078: quit-save BEFORE CancelAllAndJoin so workers are still alive to flush.
            // The coordinator's HasFired latch makes this idempotent against any prior
            // SmartLifecycleShim hook firing.
            try { Learning.QuitSaveCoordinator.Flush(Learning.QuitSaveCoordinator.DefaultDeadlineMs, "SmartRuntime.Shutdown"); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime.Shutdown: QuitSaveCoordinator threw: " + ex.Message); }

            // P11 T8 Item 64: ensure any active FrameRecorder session is flushed + closed
            // before workers cancel. The recorder holds a BinaryWriter on a file handle that
            // must release before the process tears down to avoid truncated output.
            try { Tests.FrameRecorder.Stop(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime: FrameRecorder.Stop threw: " + ex.Message); }

            // Phase 4 (FIX-PLAN.md): INVERT the order. Previously LearningService /
            // PathingService Shutdown ran BEFORE CancelAllAndJoin — trainer workers
            // could still be mutating _params while the main thread saved them
            // (R1 1.4); PathSolveLoop could still dereference _terrain after the
            // PathingService Shutdown nulled it (R1 2.7). Now: workers are cancelled
            // and joined FIRST so subsystem cleanup operates on quiesced state.
            // Plan 5 insight #3: this one ordering change closes four critical races
            // (R1 1.4, R1 2.7, R2 1.R2-N, R2 2.R2.E) simultaneously.
            WorkerLifecycleRegistry.CancelAllAndJoin(TimeSpan.FromSeconds(2));

            // With workers quiesced, subsystem cleanup is race-free.
            try { LearningService.Shutdown(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime: LearningService.Shutdown threw: " + ex.Message); }
            try { PathingService.Shutdown(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime: PathingService.Shutdown threw: " + ex.Message); }

            try { Pool.Dispose(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime: pool dispose threw: " + ex.Message); }
            Pool = null;

            // P9 Item 31: emit CircuitBreaker session report (no-op when zero trips). Path
            // mirrors the LearningService mod-dir convention so the log lands next to the
            // SmartAI/Profiles directory tree.
            try
            {
                string modDir = System.IO.Path.Combine(System.Environment.CurrentDirectory, "Mods");
                Threading.CircuitBreakerCalibration.WriteSessionReport(modDir);
            }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime: CB session report threw: " + ex.Message); }

            // BUG-014: dispose every TeamRuntime before dropping refs. Each team holds a
            // ReaderWriterLockSlim kernel handle; without explicit Dispose() the reaper has
            // already been cancelled by CancelAllAndJoin above, so the team handles leak per
            // shutdown cycle. Iterate snapshot to avoid mutating the collection while disposing.
            foreach (var teamPair in _teams)
            {
                try { teamPair.Value?.Dispose(); }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Runtime.Shutdown: TeamRuntime.Dispose["
                        + teamPair.Key.Value + "] threw: " + ex.Message);
                }
            }
            _teams.Clear();
            WorldEventBus.ClearAll();
            World?.Clear();
            World = null;
            Perception = null;

            // P4 Items 10+11 + P6 Item 26: tear down sidecars AFTER worker quiesce (above),
            // BEFORE BlockCatalog reset. DamageHintBuffer.Unwire unsubscribes the static
            // delegate so the next Init's re-Wire doesn't double-subscribe.
            DamageHintBuffer.Unwire();
            DamageHints?.Clear();
            DamageHints = null;
            IntentSidecar?.Clear();
            IntentSidecar = null;
            Health?.Clear();
            Health = null;

            // FEATURE-EXPANSION-PLAN Phase-4: matched Wire/Unwire pair on the two static-
            // singleton sidecars (RecentDamageDealtAccumulator + CargoStatePublisher) so a
            // subsequent Init's Wire doesn't double-subscribe. WeaponFireBuffer is per-tech
            // wired (no Wire/Unwire static pair) — Clear drops every per-tech subscription.
            // StrategicExtractor's RunLoop has already exited via CancelAllAndJoin above.
            RecentDamageDealtAccumulator.Unwire();
            DealtAccumulator?.Clear();
            DealtAccumulator = null;
            CargoStatePublisher.Unwire();
            CargoState?.Clear();
            CargoState = null;
            WeaponFires?.Clear();
            WeaponFires = null;
            StrategicVectors?.Clear();
            StrategicVectors = null;
            NearestTechCache = null;
            StrategicExtractor = null;

            // P5 Item 23: drop the producer's per-team snapshots so the next session
            // doesn't see stale dicts. Reset is idempotent.
            Coordination.LineOfSightProducer.Reset();

            // Chassis: Reset() AFTER CancelAllAndJoin per CHASSIS-DESIGN.md threading
            // ordering. Workers are already quiesced (above); any archetype references
            // they were holding via snapshots are GC-eligible by now. Clearing the
            // catalog before workers join would race a TryGet that's mid-flight.
            BlockCatalog?.Reset();
            BlockCatalog = null;
        }

        /// <summary>
        /// Get the TeamRuntime for this team, creating (and starting its workers) if absent.
        /// Called from SmartForm.OnTechSpawn on the main thread.
        /// </summary>
        // L-063: monotonic generation counter for fresh TeamRuntime instances.
        private static long _teamGenerationCounter;

        internal static TeamRuntime GetOrCreateTeam(TeamId teamId)
        {
            if (Pool == null) return null;
            return _teams.GetOrAdd(teamId, id =>
            {
                var rt = new TeamRuntime(id, Pool);
                rt.Generation = System.Threading.Interlocked.Increment(ref _teamGenerationCounter);
                return rt;
            });
        }

        /// <summary>Look up an existing TeamRuntime, or null if none. Used by OnTechRecycle.</summary>
        internal static TeamRuntime GetTeam(TeamId teamId)
        {
            return _teams.TryGetValue(teamId, out var team) ? team : null;
        }

        /// <summary>
        /// L-043: atomic two-team tech migration. Used by SmartEventBridge.OnTankTeamChanged
        /// in place of the (Deregister(old) + Update + Register(new)) sequence that left a
        /// window where the tech existed in neither team's snapshot. Locks both teams' RW
        /// locks in TeamId order (stable, prevents AB/BA deadlock) and runs the swap
        /// inside the joint write lock. Returns true on success, false if either team
        /// is missing or the new team refused the registration.
        /// </summary>
        internal static bool MigrateTech(TeamId oldTeamId, TeamId newTeamId, TechId techId)
        {
            if (oldTeamId.Equals(newTeamId)) return true;   // no-op
            var oldTeam = GetTeam(oldTeamId);
            var newTeam = GetOrCreateTeam(newTeamId);
            if (oldTeam == null || newTeam == null) return false;

            // Lock order: lower-valued TeamId first.
            var first = oldTeamId.Value < newTeamId.Value ? oldTeam : newTeam;
            var second = ReferenceEquals(first, oldTeam) ? newTeam : oldTeam;

            first._techsLock.EnterWriteLock();
            try
            {
                second._techsLock.EnterWriteLock();
                try
                {
                    var state = oldTeam.TryGetTechState(techId);
                    if (state == null) return false;
                    oldTeam.DeregisterTech(techId);
                    state.UpdateTeam(newTeamId);
                    if (!newTeam.RegisterTech(state))
                    {
                        // New team refused (Draining/Disposed) — tech is unregistered on
                        // both sides. Caller (SmartEventBridge) logs and lets the routing
                        // reclaim path pick it up.
                        return false;
                    }
                    return true;
                }
                finally { second._techsLock.ExitWriteLock(); }
            }
            finally { first._techsLock.ExitWriteLock(); }
        }

        /// <summary>
        /// L-044: TeamReaperDaemon-only removal of a TeamRuntime. Caller MUST have already
        /// won the Disposed transition + called Dispose(). Returns true when the team was
        /// actually removed (false = already removed by a racing reaper).
        /// </summary>
        internal static bool TryRemoveTeam(TeamId teamId)
        {
            return _teams.TryRemove(teamId, out _);
        }

        /// <summary>
        /// L-011: walk every TeamRuntime asking which one owns <paramref name="techId"/>.
        /// Used by SmartForm orphan-sweep as the LAST-RESORT TeamId recovery path when
        /// WorldModel.PerTechEntry.PriorBelief is null (the BeliefState chain populates
        /// PriorBelief on every observation today — but if a future regression breaks
        /// that invariant, this walk keeps cleanup correct instead of silently passing
        /// `default(TeamId)` to Deregister and skipping per-team state cleanup).
        /// Returns true + the team id; false leaves <paramref name="teamId"/> at default.
        /// Caller logs `[TECH-LEAK-NO-TEAM]` when this returns false.
        /// </summary>
        public static bool FindTeamForTech(TechId techId, out TeamId teamId)
        {
            foreach (var kv in _teams)
            {
                var team = kv.Value;
                if (team == null) continue;
                if (team.TryGetTechState(techId) != null)
                {
                    teamId = team.TeamId;
                    return true;
                }
            }
            teamId = default(TeamId);
            return false;
        }

        /// <summary>
        /// Aether/T3 (9-agent threading review): single tech-cleanup fan-out used by every
        /// site that retires a tech. Replaces the prior pattern of 3 duplicated cleanups
        /// (OnTechRecycle / OnTankDestroyed / orphan-sweep) that drifted apart subtly.
        ///
        /// Preconditions vary by callsite:
        /// - OnTechRecycle (form lifecycle): helper alive, tank alive, both passed.
        /// - OnTankDestroyed (combat death): helper alive, tank in death sequence; both passed.
        /// - Orphan-sweep (engine purged the GameObject): helper unknown, tank gone; both null.
        ///
        /// Safe to call with any combination of null helper/tank. Idempotent.
        /// </summary>
        public static void Deregister(TechId techId, TeamId teamId, TankAIHelper helperOrNull, Tank tankOrNull)
        {
            GetTeam(teamId)?.DeregisterTech(techId);

            // L-055: fan-out via TechLifecycleRegistry. The registered sidecars (8 of them,
            // wired in Init + LearningService.Init) cover Intent/DamageHints/Health/World/
            // ObservationSequence/LeadResidual + PathingService._lastPaths +
            // Coordinator._previousAssignments. Adding a new sidecar in the future means
            // ONE Register call, not a PR-review-only convention.
            TAC_AI.AI.Forms.Smart.World.TechLifecycleRegistry.ForgetAll(techId);

            // FEATURE-EXPANSION-PLAN Phase-4: StrategicStateBuffer is NOT an ITechSidecar
            // (it's a per-tech double-buffer registry, not a per-id sidecar dict), so the
            // Forget call has to fire here outside the registry sweep. SelfStateProbe lives
            // on SmartPerTechState — the lifecycle reset just bumps Generation so any pending
            // bg-thread Read sees the stale-discard sentinel.
            StrategicVectors?.OnTechRecycle(techId);
            StrategicExtractor?.OnTechRecycle(techId);
            // SelfStateProbe lives on the SmartPerTechState we just dropped from the team —
            // its lifecycle ends with the SmartPerTechState ref. No explicit Forget needed.

            // SmartEventBridge stays out of the registry — it's keyed by Tank (not TechId
            // alone) and the L-010 TechId-shadow path is here below.
            if (tankOrNull != null)
            {
                try { Integration.SmartEventBridge.DetachPerTank(tankOrNull); }
                catch (System.Exception ex)
                {
                    DebugTAC_AI.LogWarning("SmartRuntime.Deregister.DetachPerTank(Tank): " + ex.Message);
                }
            }
            else
            {
                // L-010: orphan path — no Tank ref available; fall back to TechId shadow map.
                try { Integration.SmartEventBridge.DetachPerTank(techId); }
                catch (System.Exception ex)
                {
                    DebugTAC_AI.LogWarning("SmartRuntime.Deregister.DetachPerTank(TechId): " + ex.Message);
                }
            }
            if (helperOrNull != null && helperOrNull.FormState != null)
                helperOrNull.FormState = null;
        }

        /// <summary>
        /// Enumerate active TeamRuntimes. Used by PathingService.ThreatFieldRebuildLoop
        /// to rebuild every active team's threat field per tick.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<TeamRuntime> EnumerateTeams() => _teams.Values;

        // Helper lookup for the guard-eval executor. Main-thread only — caller passes
        // the techId then dereferences helper.tank. Returns null when the tech is not on
        // any Smart team (likely despawned mid-roundtrip).
        public static TankAIHelper LookupHelperByTechId(TechId id)
        {
            foreach (var kv in _teams)
            {
                var team = kv.Value;
                if (team == null) continue;
                var state = team.TryGetTechState(id);
                if (state != null) return state.Helper;
            }
            return null;
        }

        // FEATURE-EXPANSION-PLAN Phase-4: probeLookup delegate for StrategicStateExtractor.
        // Walks every TeamRuntime looking for the SmartPerTechState with the matching id.
        // Returns null for techs we don't own — extractor then skips that tech.
        internal static SelfStateProbe LookupSelfStateProbe(TechId id)
        {
            foreach (var kv in _teams)
            {
                var team = kv.Value;
                if (team == null) continue;
                var state = team.TryGetTechState(id);
                if (state != null) return state.SelfStateProbe;
            }
            return null;
        }

        /// <summary>
        /// P11 T4 Item 54: look up a Smart-driven tech's VehicleModelSnapshot. Walks every
        /// team's per-tech registry and returns the matching SmartPerTechState's
        /// VehicleBuffer (which holds the latest snapshot via DoubleBuffer). Returns null
        /// when the tech isn't on any Smart-driven team — caller MUST handle the null
        /// to honor the EnemyVehicleSnapshots cost-bound contract.
        /// </summary>
        public static DoubleBuffer<VehicleModelSnapshot> GetSmartPerTechVehicleBuffer(TechId id)
        {
            foreach (var kv in _teams)
            {
                var team = kv.Value;
                if (team == null) continue;
                var state = team.TryGetTechState(id);
                if (state != null) return state.VehicleBuffer;
            }
            return null;
        }

        /// <summary>
        /// P11 T6 Item 56: per-tech Role lookup from the team's most-recent CoordinationState
        /// (worker-side publish via DoubleBuffer; reads are lock-free). Returns true + role
        /// when the tech is registered AND the team's Coordinator has published a role for it
        /// at least once; returns false otherwise (caller uses a neutral default).
        /// </summary>
        public static bool TryGetTechRole(TechId id, out Coordination.Role role)
        {
            role = Coordination.Role.Holder;
            foreach (var kv in _teams)
            {
                var team = kv.Value;
                if (team == null) continue;
                if (team.TryGetTechState(id) == null) continue;
                var state = team.Coordinator?.StateBuffer?.Read();
                if (state == null) return false;
                if (state.RoleMap != null && state.RoleMap.TryGetValue(id, out role))
                    return true;
                return false;
            }
            return false;
        }
    }
}
