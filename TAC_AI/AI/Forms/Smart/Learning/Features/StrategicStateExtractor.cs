using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning.Features
{
    // Background daemon (~30 Hz) that fuses BeliefSnapshot + per-tech SelfProbeSnapshot +
    // TerrainMap + ArmorMap + event-driven sidecars into per-tech StrategicStateVector
    // instances, scrubs NaN/Inf, and atomically publishes via StrategicStateBuffer.
    // Per FEATURE-EXPANSION-PLAN.md §4. Threading template: AetherFuser.cs:25-235.
    //
    // Threading discipline (plan §4.1):
    //   - Owns one thread on the WorkerPool. Cancellation via CancellationToken.
    //   - Host-gated + pause-gated identical to AetherFuser/GlobalCoordinatorDaemon.
    //   - Reads ONLY snapshots: WorldModel.FusedBuffer, SelfStateProbe.TryRead,
    //     TerrainMap (gated on !IsFreshlyAllocated), ArmorMap (immutable-per-chassis),
    //     and the event-driven sidecars' lock-protected rings.
    //   - NEVER touches Tank/TankAIHelper/TechAI/Transform/Rigidbody/Singleton.Manager<T>.
    //     Per F-04 / F-07 / F-11 all Unity-touching reads live on SelfStateProbe's
    //     main-thread Publish path, surfaced via SelfProbeSnapshot fields.
    public sealed class StrategicStateExtractor
    {
        // Canonical daemon name — read by Phase-4 wiring for
        // DaemonWatchdog.RegisterCanonical + WorkerHealthMonitor.RegisterCanonical.
        // MUST match the EnqueueLongRunning label exactly (plan §4.1 last bullet +
        // round-2 R2-15 BLOCKER on CanonicalRoster string identity).
        public const string CanonicalName = "StrategicStateExtractor";

        // 30 Hz target; same budget as AetherFuser.RunLoop.
        private const int TickPeriodMs = 33;

        // Per-tech K-nearest scan radius. Bounded loosely so the daemon never burns
        // CPU on a 4 km radial search; the bucket grid widens at TileSize=64 so this
        // covers ~9 tiles diag (per NearestTechCache adaptive-radius logic).
        public const float NearestHostileMaxRange = 600f;
        // Reserved for the reserved-multi-target / coord-handoff blocks (§3.3 [110..127]).
        public const float NearestFriendlyMaxRange = 400f;

        // Window for SumWithin / CountWithin: 1 s and 5 s in tick-domain (plan §4.2
        // converts seconds → ticks at the call site since MonoClock is ticks-based).
        private static readonly long OneSecondTicks =
            (long)(1.0 / MonoClock.TickFreq);
        private static readonly long FiveSecondsTicks =
            (long)(5.0 / MonoClock.TickFreq);

        // Diagnostics — single summary line every ~5 s (matches AetherFuser).
        private const int DiagnosticLogPeriodTicks = 150;
        private int _windowTickCount;
        private long _windowSumStopwatchTicks;
        private long _windowMaxStopwatchTicks;
        private int _windowTechsPublished;
        private int _windowSkippedTerrainStale;
        private int _windowSkippedNoProbe;

        private readonly CircuitBreaker _breaker = new CircuitBreaker(CanonicalName);

        // Snapshot inputs supplied by SmartRuntime at construction. The probe-lookup
        // delegate is the per-tech SelfStateProbe slot; the wiring phase routes it to
        // SmartPerTechState.SelfStateProbe (or equivalent).
        private readonly WorldModel _world;
        private readonly StrategicStateBuffer _output;
        private readonly NearestTechCache _nearestCache;
        private readonly TerrainMap _terrainMap;
        // HealthSidecar is held for forward compat — the SelfProbeSnapshot already
        // carries HpFraction so the daemon doesn't need to call Get(id) per tick.
        // GetAt(id, monoCutoff, ...) becomes relevant when reserved-shield-charge
        // slots get a formula (plan §3.2 / §3.3 reserved blocks).
        private readonly HealthSidecar _health;
        private readonly DamageHintBuffer _damageHints;
        private readonly RecentDamageDealtAccumulator _dealt;
        private readonly WeaponFireBuffer _fireBuffer;
        private readonly CargoStatePublisher _cargo;
        private readonly Func<TechId, SelfStateProbe> _probeLookup;

        public StrategicStateExtractor(
            WorldModel world,
            StrategicStateBuffer output,
            NearestTechCache nearestCache,
            TerrainMap terrainMap,
            HealthSidecar health,
            DamageHintBuffer damageHints,
            RecentDamageDealtAccumulator dealt,
            WeaponFireBuffer fireBuffer,
            CargoStatePublisher cargo,
            Func<TechId, SelfStateProbe> probeLookup)
        {
            _world = world;
            _output = output;
            _nearestCache = nearestCache;
            _terrainMap = terrainMap;
            _health = health;
            _damageHints = damageHints;
            _dealt = dealt;
            _fireBuffer = fireBuffer;
            _cargo = cargo;
            _probeLookup = probeLookup;
        }

        // Diagnostic accessor that also keeps the C# compiler from flagging
        // _health as unused. Phase-4 wiring can replace this with real GetAt reads.
        public bool HealthAvailable { get { return _health != null; } }

        // Spawn / recycle hooks. Per F-19: routed through IAIForm.OnTechRecycle by the
        // wiring phase, NOT Tank.TankRecycledEvent. OnTechSpawn is optional (lazy alloc).
        public void OnTechSpawn(TechId id)
        {
            if (_output != null) _output.OnTechSpawn(id);
        }
        public void OnTechRecycle(TechId id)
        {
            if (_output != null) _output.OnTechRecycle(id);
        }

        // Worker loop — same shape as AetherFuser.RunLoop. Pause-gate ahead of host-gate
        // per L-026; catch-and-continue per L-024 (TAE absorbed via AbortGuard).
        public void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(TickPeriodMs)) return;
                    continue;
                }
                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(TickPeriodMs)) return;
                    continue;
                }

                try { Tick(cancellation); }
                catch (OperationCanceledException) { return; }
                catch (ThreadAbortException)
                {
                    if (AbortGuard.Absorb(CanonicalName) == AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning(
                        "Smart.StrategicStateExtractor: tick threw " + ex.GetType().Name + ": " + ex.Message);
                    if (_breaker.Tripped()) return;
                }

                if (cancellation.WaitHandle.WaitOne(TickPeriodMs)) return;
            }
        }

        // One per-tick pass: snapshot fan-in, per-tech vector build, atomic publish.
        public void Tick(CancellationToken cancellation)
        {
            CancellationHelpers.ThrowIfCancelled(cancellation);
            long tickStartStopwatch = System.Diagnostics.Stopwatch.GetTimestamp();
            long nowMono = MonoClock.Now();

            // §4.2 step 0: read the world belief snapshot. F-11: WorldModel.FusedBuffer
            // is the PUBLIC accessor; AetherFuser._snapshotBuffer is private.
            BeliefSnapshot snapshot = _world != null ? _world.FusedBuffer.Read() : null;
            if (snapshot == null || snapshot.ByTech == null)
            {
                AccumulateTickDiagnostics(tickStartStopwatch, 0, 0, 0);
                return;
            }

            // §4.2 terrain-fresh gate per F-07 / R2-03. Until the first MainThreadTick
            // pass populates the grid every query returns ±Inf/NaN-y values; daemon
            // must skip the tick rather than poison the vector.
            bool terrainReady = _terrainMap != null && !_terrainMap.IsFreshlyAllocated;
            if (!terrainReady)
            {
                AccumulateTickDiagnostics(tickStartStopwatch, 0, 1, 0);
                return;
            }

            // Rebuild the spatial bucket grid once per tick before per-tech queries.
            if (_nearestCache != null) _nearestCache.Rebuild(snapshot);

            int published = 0;
            int skippedNoProbe = 0;

            // Iterate every team's Smart-driven tech. EnumerateTeams is internal but we
            // share the same assembly. RegisterTech adds to TeamRuntime._techs;
            // SnapshotTechIds enumerates the keys.
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                CancellationHelpers.ThrowIfCancelled(cancellation);
                if (team == null) continue;
                if (team.State != TeamLifecycleState.Active) continue;

                foreach (var techId in team.SnapshotTechIds())
                {
                    CancellationHelpers.ThrowIfCancelled(cancellation);

                    BeliefState selfBelief;
                    if (!snapshot.ByTech.TryGetValue(techId, out selfBelief)) continue;
                    if (selfBelief == null) continue;

                    SelfStateProbe probe = _probeLookup != null ? _probeLookup(techId) : null;
                    if (probe == null) { skippedNoProbe++; continue; }
                    SelfProbeSnapshot selfProbe = probe.TryRead();
                    if (selfProbe == null) { skippedNoProbe++; continue; }

                    // §6.5 lifecycle-race: vector carries the probe's Generation; readers
                    // discard stale generations.
                    var vec = new StrategicStateVector(
                        techId, selfBelief.Team, nowMono, selfProbe.Generation);

                    // Per-tech fill — every slot derived from snapshots above.
                    FillVector(vec, snapshot, selfBelief, selfProbe);

                    // §4.2 step 8: NaN/Inf scrub before atomic publish. Last write into
                    // Raw[] before Write.
                    vec.ScrubNonFinite();

                    _output.Publish(techId, vec);
                    published++;
                }
            }

            AccumulateTickDiagnostics(tickStartStopwatch, published, 0, skippedNoProbe);
        }

        // --- Per-tech derivation -------------------------------------------------

        private void FillVector(
            StrategicStateVector vec, BeliefSnapshot snapshot,
            BeliefState selfBelief, SelfProbeSnapshot selfProbe)
        {
            // K-nearest queries. K=1 hostile + K=1 friendly per plan §4.2; the
            // reserved-multi-target [110..119] AV block can be wired to K=3 later.
            NearestTechCache.KNearestResult[] hostiles = _nearestCache != null
                ? _nearestCache.QueryNearest(selfBelief.PositionMean, selfBelief.Id,
                    selfBelief.Team, NearestTechCache.TeamFilter.OtherTeam, 1, NearestHostileMaxRange)
                : new NearestTechCache.KNearestResult[0];
            bool hasHostile = hostiles.Length > 0;
            NearestTechCache.KNearestResult hostile = hasHostile ? hostiles[0] : default(NearestTechCache.KNearestResult);

            // Terrain samples — three points: self, target (if any), midpoint.
            // Pseudocode in plan uses `.xz`; Unity 4.6.1 has no swizzle so materialize
            // Vector2 explicitly.
            Vector2 selfXZ = new Vector2(selfBelief.PositionMean.x, selfBelief.PositionMean.z);
            float selfSlope = _terrainMap.SlopeAt(selfXZ);
            float selfTerrainH = _terrainMap.HeightAt(selfXZ);
            float selfHeightAboveTerrain = selfBelief.PositionMean.y - selfTerrainH;

            float tgtSlope = 0f;
            float tgtTerrainH = 0f;
            float tgtHeightAboveTerrain = 0f;
            float losBlocked = 0f;
            if (hasHostile && hostile.Belief != null)
            {
                Vector2 tgtXZ = new Vector2(hostile.Belief.PositionMean.x, hostile.Belief.PositionMean.z);
                tgtSlope = _terrainMap.SlopeAt(tgtXZ);
                tgtTerrainH = _terrainMap.HeightAt(tgtXZ);
                tgtHeightAboveTerrain = hostile.Belief.PositionMean.y - tgtTerrainH;
                losBlocked = _terrainMap.RaycastSegment(selfBelief.PositionMean, hostile.Belief.PositionMean) ? 1f : 0f;
            }

            // Recent-damage / fire windows. Per F-05 / R2-09 the new sidecar APIs take
            // (techId, fromMonoTick, toMonoTick) — convert seconds → ticks via TickFreq.
            long from1s = nowMonoFromVec(vec) - OneSecondTicks;
            long from5s = nowMonoFromVec(vec) - FiveSecondsTicks;
            // NOTE: DamageHintBuffer and RecentDamageDealtAccumulator BOTH expose a
            // public DamageSummary struct in the same TAC_AI.AI.Forms.Smart.World
            // namespace (Phase-2 sidecar reports — distinct types, same simple name).
            // The project as a whole won't compile until Phase-4 wiring renames one of
            // them; here we read only TotalMagnitude + DominantTypeCode which both
            // expose identically. We extract to local floats/bytes immediately so the
            // type-name ambiguity stays scoped to this method.
            float taken1sMag = 0f, taken5sMag = 0f;
            byte  taken1sType = 0;
            float dealt1sMag = 0f, dealt5sMag = 0f;
            byte  dealt1sType = 0;
            if (_damageHints != null)
            {
                var s1 = _damageHints.SumWithin(selfBelief.Id, from1s, vec.PublishedTickMono);
                taken1sMag = s1.TotalMagnitude; taken1sType = s1.DominantTypeCode;
                var s5 = _damageHints.SumWithin(selfBelief.Id, from5s, vec.PublishedTickMono);
                taken5sMag = s5.TotalMagnitude;
            }
            if (_dealt != null)
            {
                var s1 = _dealt.SumWithin(selfBelief.Id, from1s, vec.PublishedTickMono);
                dealt1sMag = s1.TotalMagnitude; dealt1sType = s1.DominantTypeCode;
                var s5 = _dealt.SumWithin(selfBelief.Id, from5s, vec.PublishedTickMono);
                dealt5sMag = s5.TotalMagnitude;
            }
            int shotsFired1s = _fireBuffer != null
                ? _fireBuffer.CountWithin(selfBelief.Id, from1s, vec.PublishedTickMono) : 0;

            // Cargo. SelfProbeSnapshot carries (num, capacity); CargoStatePublisher
            // carries (num, capacity, saleValue). Prefer the publisher when present;
            // fall back to the probe-cached pair.
            CargoSnapshot cargoSnap = _cargo != null ? _cargo.GetSnapshot(selfBelief.Id) : CargoSnapshot.Empty;
            int cargoNum = cargoSnap.CapacityKnown ? cargoSnap.NumChunks : selfProbe.CargoNumContents;
            int cargoCap = cargoSnap.CapacityKnown ? cargoSnap.CapacityChunks : selfProbe.CargoCapacity;
            float cargoFill = cargoCap > 0 ? (float)cargoNum / cargoCap : 0f;
            float cargoValue = cargoSnap.CargoSaleValue;

            // Target-side probe (only when hostile is on Smart team — otherwise default).
            SelfProbeSnapshot tgtProbeSnap = null;
            if (hasHostile && _probeLookup != null)
            {
                SelfStateProbe tgtProbe = _probeLookup(hostile.Id);
                if (tgtProbe != null) tgtProbeSnap = tgtProbe.TryRead();
            }

            // ---- Intent block (40 dims; §3.2) ---------------------------------
            FillIntentSlots(vec, selfBelief, selfProbe, hostile, hasHostile,
                selfSlope, tgtSlope, losBlocked,
                taken1sMag, taken1sType, dealt1sMag, dealt1sType, shotsFired1s,
                cargoFill);

            // ---- ActionValue block (128 dims; §3.3) ---------------------------
            FillActionValueSlots(vec, selfBelief, selfProbe, hostile, hasHostile,
                tgtProbeSnap, selfSlope, tgtSlope, selfHeightAboveTerrain,
                tgtHeightAboveTerrain, losBlocked,
                taken1sMag, taken5sMag, dealt1sMag, dealt5sMag,
                cargoNum, cargoCap, cargoFill, cargoValue);

            // ---- Residual block (48 dims; §3.4) -------------------------------
            // §4.3 + §3.4: Residual is FIRE-TIME-CAPTURED — populated when the
            // ContinuousController fire-commit site widens LeadResidualRecorder.Pending
            // with the FireTimeFeatures array (plan §4.4 readers section + R2-04).
            // The daemon does NOT pre-fill Residual on every tick; slots stay zero
            // until the fire-time capture path passes the slice to OnFireCommit.
            // Per §3.4 closing note: input dims at fire = [0..34]; reserved [35..47]
            // stay zero on forward. We leave the whole 48-slot block zero here; the
            // capture path is the only writer. Documented for the wiring phase.

            // ---- Threat block (48 dims; §3.5) ---------------------------------
            // Per §4.4 + R2-11: subject == attacker from the consumer's POV. Slope/
            // height under the attacker are SELF's terrain reads.
            FillThreatSlots(vec, selfBelief, selfProbe, hostile, hasHostile, tgtProbeSnap,
                selfSlope, selfHeightAboveTerrain, losBlocked,
                dealt1sMag, dealt5sMag, dealt1sType, shotsFired1s,
                taken1sMag, taken5sMag);
        }

        // Tiny helper so the SumWithin window-from-tick computation reads naturally.
        private static long nowMonoFromVec(StrategicStateVector vec) { return vec.PublishedTickMono; }

        // --- Intent block (40 dims, view-local) -----------------------------------

        private static void FillIntentSlots(
            StrategicStateVector vec, BeliefState selfBelief, SelfProbeSnapshot selfProbe,
            NearestTechCache.KNearestResult hostile, bool hasHostile,
            float selfSlope, float tgtSlope, float losBlocked,
            float dmgTaken1sMag, byte dmgTaken1sType,
            float dmgDealt1sMag, byte dmgDealt1sType, int shotsFired1s,
            float cargoFill)
        {
            float[] raw = vec.Raw;
            int B = StrategicStateVector.IntentBase;

            // Kinematic-vs-target.
            if (hasHostile && hostile.Belief != null)
            {
                Vector3 rel = hostile.Belief.PositionMean - selfBelief.PositionMean;
                raw[B + IntentSlots.RelPosX] = rel.x;
                raw[B + IntentSlots.RelPosZ] = rel.z;
                float relHead = hostile.Belief.HeadingMean - selfBelief.HeadingMean;
                raw[B + IntentSlots.RelHeadingSin] = Mathf.Sin(relHead);
                raw[B + IntentSlots.RelHeadingCos] = Mathf.Cos(relHead);
                Vector3 relV = hostile.Belief.VelocityMean - selfBelief.VelocityMean;
                raw[B + IntentSlots.RelVelX] = relV.x;
                raw[B + IntentSlots.RelVelZ] = relV.z;
                raw[B + IntentSlots.TargetSpeed] = hostile.Belief.VelocityMean.magnitude;
                raw[B + IntentSlots.TargetAgeSec] = hostile.Belief.AgeSeconds;
                raw[B + IntentSlots.TargetUncertaintyM] = hostile.Belief.UncertaintyMeters;
                raw[B + IntentSlots.TargetSightCode] = SightCode(hostile.Belief.Sight);
                raw[B + IntentSlots.Distance] = Mathf.Sqrt(hostile.SqrDistance);
                raw[B + IntentSlots.LosBlocked] = losBlocked;
                raw[B + IntentSlots.TargetTeamSignedDelta] = hostile.Team.Value - selfBelief.Team.Value;
                raw[B + IntentSlots.TargetIsHostile] = 1f;
                raw[B + IntentSlots.TargetHp] = 0f;        // belief carries no HP; consumer side reads from HealthSidecar.
                raw[B + IntentSlots.TargetTotalWeapons] = 0f;   // populated when target is on Smart team (via target-probe block below)
                raw[B + IntentSlots.TargetMassRatio] = 0f;
                raw[B + IntentSlots.TargetWeaponFireRateMean] = 0f;
                raw[B + IntentSlots.TargetWeaponRangeMax] = 0f;
                raw[B + IntentSlots.TargetAnchored] = 0f;
                raw[B + IntentSlots.RelSlopeAtTarget] = tgtSlope - selfSlope;
            }
            else
            {
                // No hostile in range — leave kinematic-vs-target zero; sight code still 0 (Lost).
                raw[B + IntentSlots.TargetSightCode] = 0f;
            }

            // Self composition.
            raw[B + IntentSlots.SelfHp] = selfProbe.HpFraction;
            raw[B + IntentSlots.SelfTotalWeapons] = selfProbe.WeaponAggregate.WeaponCount;
            raw[B + IntentSlots.SelfMass] = selfProbe.Mass / 1000f;        // tonnes
            raw[B + IntentSlots.SelfAnchored] = AnchorCode(selfProbe.AnchorState);
            raw[B + IntentSlots.RelSlopeAtSelf] = selfSlope;

            // Recent-damage rates (per-second).
            raw[B + IntentSlots.RecentDamageDealtRate] = dmgDealt1sMag;
            raw[B + IntentSlots.RecentDamageTakenRate] = dmgTaken1sMag;
            // Dominant damage-type code, normalized by /8 per §3.5 line 426.
            raw[B + IntentSlots.RecentDamageDealtType] = dmgDealt1sType / 8f;
            raw[B + IntentSlots.RecentDamageTakenType] = dmgTaken1sType / 8f;

            // Weapon aggregate (self).
            raw[B + IntentSlots.SelfWeaponFireRateMean] = selfProbe.WeaponAggregate.FireRateMean;
            raw[B + IntentSlots.SelfWeaponRangeMax] = selfProbe.WeaponAggregate.RangeMax;
            raw[B + IntentSlots.RecentShotsFiredRate] = shotsFired1s;
            raw[B + IntentSlots.CargoStateCode] = cargoFill;

            // World context. F-10: TimeOfDayCosine divides by 24, not 1.
            raw[B + IntentSlots.TimeOfDayCosine] = Mathf.Cos(2f * Mathf.PI * selfProbe.TimeOfDayHour / 24f);
            // Provoked is a countdown int (F-24) — > 0 = active.
            raw[B + IntentSlots.ProvokedFlag] = selfProbe.ProvokedCountdown > 0 ? 1f : 0f;

            // [36..39] reserved-mission + reserved-shield — leave zero.
            // Plan §3.2 line 121 marks these as ~10% headroom; no formula in §4.3.
        }

        // --- ActionValue block (128 dims) -----------------------------------------

        private static void FillActionValueSlots(
            StrategicStateVector vec,
            BeliefState selfBelief, SelfProbeSnapshot selfProbe,
            NearestTechCache.KNearestResult hostile, bool hasHostile,
            SelfProbeSnapshot tgtProbe,
            float selfSlope, float tgtSlope, float selfHeightAboveTerrain,
            float tgtHeightAboveTerrain, float losBlocked,
            float dmgTaken1sMag, float dmgTaken5sMag,
            float dmgDealt1sMag, float dmgDealt5sMag,
            int cargoNum, int cargoCap, float cargoFill, float cargoValue)
        {
            float[] raw = vec.Raw;
            int B = StrategicStateVector.ActionValueBase;

            // Self kinematic [0..9].
            raw[B + ActionValueSlots.SelfPosX] = selfBelief.PositionMean.x;
            raw[B + ActionValueSlots.SelfPosZ] = selfBelief.PositionMean.z;
            raw[B + ActionValueSlots.SelfHeadingSin] = Mathf.Sin(selfBelief.HeadingMean);
            raw[B + ActionValueSlots.SelfHeadingCos] = Mathf.Cos(selfBelief.HeadingMean);
            float selfSpeed = selfBelief.VelocityMean.magnitude;
            raw[B + ActionValueSlots.SelfVelMag] = selfSpeed;
            if (selfSpeed > 1e-3f)
            {
                float vh = Mathf.Atan2(selfBelief.VelocityMean.x, selfBelief.VelocityMean.z);
                raw[B + ActionValueSlots.SelfVelHeadingSin] = Mathf.Sin(vh);
                raw[B + ActionValueSlots.SelfVelHeadingCos] = Mathf.Cos(vh);
            }
            raw[B + ActionValueSlots.SelfSlopeUnder] = selfSlope;
            raw[B + ActionValueSlots.SelfHeightAboveTerrain] = selfHeightAboveTerrain;
            // Waterlogged: clamp((WaterHeight - y)/5, 0, 1). WaterHeight==-9001 means no
            // WaterMod — treat as fully dry.
            raw[B + ActionValueSlots.SelfWaterloggedFrac] =
                selfProbe.WaterHeight > -9000f ? Mathf.Clamp01((selfProbe.WaterHeight - selfBelief.PositionMean.y) / 5f) : 0f;

            // Self vehicle [10..19].
            raw[B + ActionValueSlots.SelfMass] = selfProbe.Mass / 1000f;
            raw[B + ActionValueSlots.SelfBlockCount] = selfProbe.BlockCount;
            raw[B + ActionValueSlots.SelfWeaponCount] = selfProbe.WeaponAggregate.WeaponCount;
            int totalW = selfProbe.WeaponAggregate.WeaponCount;
            raw[B + ActionValueSlots.SelfMeleeFraction] = totalW > 0
                ? (float)selfProbe.WeaponAggregate.MeleeWeaponCount / totalW : 0f;
            raw[B + ActionValueSlots.SelfWeaponRangeMax] = selfProbe.WeaponAggregate.RangeMax;
            raw[B + ActionValueSlots.SelfWeaponFireRateMean] = selfProbe.WeaponAggregate.FireRateMean;
            raw[B + ActionValueSlots.SelfWeaponDamagePerShotMean] = selfProbe.WeaponAggregate.DamagePerShotMean;
            raw[B + ActionValueSlots.SelfWeaponMuzzleVelMean] = selfProbe.WeaponAggregate.MuzzleVelMean;
            raw[B + ActionValueSlots.SelfHpFraction] = selfProbe.HpFraction;
            raw[B + ActionValueSlots.SelfReadyFireFraction] = totalW > 0
                ? (float)selfProbe.WeaponAggregate.ReadyToFireCount / totalW : 0f;

            // Self energy + anchor + cargo [20..27]. F-01: EnergyState has no Stored /
            // NetFlow fields — derive at consume time.
            raw[B + ActionValueSlots.SelfElectricStored] = selfProbe.Electric.CurrentAmount / 10000f;
            raw[B + ActionValueSlots.SelfElectricSpareCapacity] = selfProbe.Electric.SpareCapacity / 10000f;
            raw[B + ActionValueSlots.SelfElectricNetFlow] =
                (selfProbe.Electric.CurrentAmount - selfProbe.Electric.PreviousAmount) / 1000f;
            int totalE = selfProbe.EnergyModuleCount;
            raw[B + ActionValueSlots.SelfIsGeneratingFraction] = totalE > 0
                ? (float)selfProbe.GeneratingModuleCount / totalE : 0f;
            raw[B + ActionValueSlots.SelfAnchorState] = AnchorCode(selfProbe.AnchorState);
            raw[B + ActionValueSlots.SelfCargoFill] = cargoFill;
            raw[B + ActionValueSlots.SelfCargoValue] = cargoValue;
            raw[B + ActionValueSlots.SelfCargoBlockCount] = cargoNum;

            // Self armor + damage [28..35].
            // WeakFaceForwardDot = dot(forwardWorld, weakFaceNormalWorld) — both pre-
            // rotated to world frame on the SelfStateProbe main-thread path. F-06.
            raw[B + ActionValueSlots.SelfWeakFaceForwardDot] =
                Vector3.Dot(selfProbe.ForwardWorld, selfProbe.WeakFaceNormalWorld);
            // WeakFaceHp comes from a fresh QueryWeakFace against forward. ArmorMap is
            // chassis-immutable so the call is any-thread.
            float weakFaceHp = 0f;
            if (selfProbe.Armor != null && selfProbe.Armor != ArmorMap.Empty)
            {
                weakFaceHp = selfProbe.Armor.QueryWeakFace(Vector3.forward).TotalHP;
            }
            raw[B + ActionValueSlots.SelfWeakFaceHp] = weakFaceHp;
            raw[B + ActionValueSlots.SelfRecentDamageTaken1s] = dmgTaken1sMag;
            raw[B + ActionValueSlots.SelfRecentDamageTaken5s] = dmgTaken5sMag;
            raw[B + ActionValueSlots.SelfRecentDamageDealt1s] = dmgDealt1sMag;
            raw[B + ActionValueSlots.SelfRecentDamageDealt5s] = dmgDealt5sMag;
            if (selfProbe.HasLastAttacker)
            {
                Vector3 d = selfProbe.LastAttackerPosWorld - selfProbe.PositionWorld;
                raw[B + ActionValueSlots.SelfLastAttackerDistance] = d.magnitude;
                raw[B + ActionValueSlots.SelfLastAttackerAge] =
                    MonoClock.Seconds(selfProbe.LastAttackerSeenMono, vec.PublishedTickMono);
            }

            // Self misc [36..39].
            raw[B + ActionValueSlots.SelfProvokedFlag] = selfProbe.ProvokedCountdown > 0 ? 1f : 0f;
            // MaxAccelEstimate — belief carries the value (per §4.3 "direct (norm/20)").
            raw[B + ActionValueSlots.SelfMaxAccelEstimate] = selfBelief.MaxAccelerationEstimate / 20f;
            raw[B + ActionValueSlots.SelfSpeedAtObserve] = selfBelief.SpeedAtObserve;
            raw[B + ActionValueSlots.SelfRoleHintCode] = (int)selfProbe.SmartIdentity / 8f;

            // Target block — same shape as self [40..79]. Belief carries pos/vel/heading;
            // composition pulls from the target-side probe (only valid when target is on
            // a Smart team). When tgtProbe is null we leave the composition slots zero.
            if (hasHostile && hostile.Belief != null)
            {
                BeliefState tb = hostile.Belief;
                raw[B + ActionValueSlots.TargetPosX] = tb.PositionMean.x;
                raw[B + ActionValueSlots.TargetPosZ] = tb.PositionMean.z;
                raw[B + ActionValueSlots.TargetHeadingSin] = Mathf.Sin(tb.HeadingMean);
                raw[B + ActionValueSlots.TargetHeadingCos] = Mathf.Cos(tb.HeadingMean);
                float tgtSpeed = tb.VelocityMean.magnitude;
                raw[B + ActionValueSlots.TargetVelMag] = tgtSpeed;
                if (tgtSpeed > 1e-3f)
                {
                    float vh = Mathf.Atan2(tb.VelocityMean.x, tb.VelocityMean.z);
                    raw[B + ActionValueSlots.TargetVelHeadingSin] = Mathf.Sin(vh);
                    raw[B + ActionValueSlots.TargetVelHeadingCos] = Mathf.Cos(vh);
                }
                raw[B + ActionValueSlots.TargetSlopeUnder] = tgtSlope;
                raw[B + ActionValueSlots.TargetHeightAboveTerrain] = tgtHeightAboveTerrain;

                raw[B + ActionValueSlots.TargetMaxAccelEstimate] = tb.MaxAccelerationEstimate / 20f;
                raw[B + ActionValueSlots.TargetSpeedAtObserve] = tb.SpeedAtObserve;
            }
            if (tgtProbe != null)
            {
                raw[B + ActionValueSlots.TargetWaterloggedFrac] =
                    tgtProbe.WaterHeight > -9000f
                    ? Mathf.Clamp01((tgtProbe.WaterHeight - tgtProbe.PositionWorld.y) / 5f) : 0f;
                raw[B + ActionValueSlots.TargetMass] = tgtProbe.Mass / 1000f;
                raw[B + ActionValueSlots.TargetBlockCount] = tgtProbe.BlockCount;
                raw[B + ActionValueSlots.TargetWeaponCount] = tgtProbe.WeaponAggregate.WeaponCount;
                int tw = tgtProbe.WeaponAggregate.WeaponCount;
                raw[B + ActionValueSlots.TargetMeleeFraction] = tw > 0
                    ? (float)tgtProbe.WeaponAggregate.MeleeWeaponCount / tw : 0f;
                raw[B + ActionValueSlots.TargetWeaponRangeMax] = tgtProbe.WeaponAggregate.RangeMax;
                raw[B + ActionValueSlots.TargetWeaponFireRateMean] = tgtProbe.WeaponAggregate.FireRateMean;
                raw[B + ActionValueSlots.TargetWeaponDamagePerShotMean] = tgtProbe.WeaponAggregate.DamagePerShotMean;
                raw[B + ActionValueSlots.TargetWeaponMuzzleVelMean] = tgtProbe.WeaponAggregate.MuzzleVelMean;
                raw[B + ActionValueSlots.TargetHpFraction] = tgtProbe.HpFraction;
                raw[B + ActionValueSlots.TargetReadyFireFraction] = tw > 0
                    ? (float)tgtProbe.WeaponAggregate.ReadyToFireCount / tw : 0f;
                raw[B + ActionValueSlots.TargetElectricStored] = tgtProbe.Electric.CurrentAmount / 10000f;
                raw[B + ActionValueSlots.TargetElectricSpareCapacity] = tgtProbe.Electric.SpareCapacity / 10000f;
                raw[B + ActionValueSlots.TargetElectricNetFlow] =
                    (tgtProbe.Electric.CurrentAmount - tgtProbe.Electric.PreviousAmount) / 1000f;
                int teM = tgtProbe.EnergyModuleCount;
                raw[B + ActionValueSlots.TargetIsGeneratingFraction] = teM > 0
                    ? (float)tgtProbe.GeneratingModuleCount / teM : 0f;
                raw[B + ActionValueSlots.TargetAnchorState] = AnchorCode(tgtProbe.AnchorState);
                raw[B + ActionValueSlots.TargetWeakFaceForwardDot] =
                    Vector3.Dot(tgtProbe.ForwardWorld, tgtProbe.WeakFaceNormalWorld);
                if (tgtProbe.Armor != null && tgtProbe.Armor != ArmorMap.Empty)
                {
                    raw[B + ActionValueSlots.TargetWeakFaceHp] =
                        tgtProbe.Armor.QueryWeakFace(Vector3.forward).TotalHP;
                }
                raw[B + ActionValueSlots.TargetProvokedFlag] = tgtProbe.ProvokedCountdown > 0 ? 1f : 0f;
                raw[B + ActionValueSlots.TargetRoleHintCode] = (int)tgtProbe.SmartIdentity / 8f;
                // Target's recent-damage windows would require a second SumWithin pass
                // keyed on target — left zero here; the ContinuousController consumer
                // can fill TargetRecentDamage* via its own SumWithin call when it
                // assembles the typed event (per §3.3 docstring).
            }

            // Action one-hot [80..89] is written by the ContinuousController consumer
            // at action-dispatch time (plan §3.3 ActionOneHotBase block) — leave zero
            // here; the daemon doesn't know which action was selected.
            // Reserved [90..127] — mission/shield/multi-target/coord-handoff. No
            // formula in §4.3. Zero.
        }

        // --- Threat block (48 dims) -----------------------------------------------

        private static void FillThreatSlots(
            StrategicStateVector vec,
            BeliefState selfBelief, SelfProbeSnapshot selfProbe,
            NearestTechCache.KNearestResult attacker, bool hasAttacker,
            SelfProbeSnapshot victimProbe,
            float atkSlope, float atkHeightAboveTerrain, float losBlocked,
            float dmgDealt1sMag, float dmgDealt5sMag, byte dmgDealt1sType, int shotsFired1s,
            float dmgTaken1sMag, float dmgTaken5sMag)
        {
            // ThreatAssessmentModel reads attacker-side composition (slots 0..23) +
            // engagement geometry (24..31) + recent-damage windows (32..37). Per
            // §4.4 readers paragraph + R2-11: slot 35 is attacker-side (the damage
            // type the attacker dealt); slots 30 and 31 are the only victim-side
            // slots inside the 0..35 range. Per §9 LearningService row the consumer
            // calls Read(attackerId) for attacker-side and Read(victimId) for
            // victim-side; both calls go through this same vector layout. The
            // daemon publishes one per-tech vector — the consumer wires
            // attacker/victim differently.
            //
            // In the daemon's per-tech pass the subject IS the techId being
            // published. We fill the attacker-side block using selfProbe (so when
            // a downstream consumer Reads(attackerId) it gets the attacker's
            // composition). Victim-side slots [30, 31, 36, 37] are filled with
            // selfBelief data here too — the consumer Read(victimId) sees those
            // slots filled with the victim's own composition. Both vector reads
            // are valid because each tech publishes its own vector with its own
            // selfProbe data; the consumer just picks the slot range it needs.

            float[] raw = vec.Raw;
            int B = StrategicStateVector.ThreatBase;

            // Attacker composition [0..7]. Pulled from selfProbe — the subject of
            // this vector is the attacker from the consumer's POV.
            raw[B + ThreatSlots.AttackerBlockCount] = selfProbe.BlockCount;
            raw[B + ThreatSlots.AttackerMass] = selfProbe.Mass / 1000f;
            raw[B + ThreatSlots.AttackerWeaponCount] = selfProbe.WeaponAggregate.WeaponCount;
            int totalW = selfProbe.WeaponAggregate.WeaponCount;
            raw[B + ThreatSlots.AttackerMeleeFraction] = totalW > 0
                ? (float)selfProbe.WeaponAggregate.MeleeWeaponCount / totalW : 0f;
            raw[B + ThreatSlots.AttackerHpFraction] = selfProbe.HpFraction;
            raw[B + ThreatSlots.AttackerAnchorState] = AnchorCode(selfProbe.AnchorState);
            raw[B + ThreatSlots.AttackerRoleHintCode] = (int)selfProbe.SmartIdentity / 8f;
            raw[B + ThreatSlots.AttackerProvokedFlag] = selfProbe.ProvokedCountdown > 0 ? 1f : 0f;

            // Attacker weapon aggregate [8..15].
            raw[B + ThreatSlots.AttackerWeaponRangeMax] = selfProbe.WeaponAggregate.RangeMax;
            raw[B + ThreatSlots.AttackerWeaponRangeMean] = selfProbe.WeaponAggregate.RangeMean;
            raw[B + ThreatSlots.AttackerWeaponFireRateMean] = selfProbe.WeaponAggregate.FireRateMean;
            raw[B + ThreatSlots.AttackerWeaponDamagePerShotMean] = selfProbe.WeaponAggregate.DamagePerShotMean;
            raw[B + ThreatSlots.AttackerWeaponMuzzleVelMean] = selfProbe.WeaponAggregate.MuzzleVelMean;
            raw[B + ThreatSlots.AttackerEnergyWeaponFraction] = selfProbe.WeaponAggregate.EnergyWeaponFraction;
            raw[B + ThreatSlots.AttackerWeaponKindMixGun] = selfProbe.WeaponAggregate.KindMixGun;
            raw[B + ThreatSlots.AttackerWeaponKindMixBeamMelee] = selfProbe.WeaponAggregate.KindMixBeamMelee;

            // Attacker kinematic [16..23].
            raw[B + ThreatSlots.AttackerSpeed] = selfBelief.VelocityMean.magnitude;
            raw[B + ThreatSlots.AttackerMaxAccelEstimate] = selfBelief.MaxAccelerationEstimate / 20f;
            // Slope/height under self — subject == attacker in the per-tech vector.
            raw[B + ThreatSlots.AttackerSlopeUnder] = atkSlope;
            raw[B + ThreatSlots.AttackerHeightAboveTerrain] = atkHeightAboveTerrain;
            raw[B + ThreatSlots.AttackerWaterloggedFrac] =
                selfProbe.WaterHeight > -9000f
                ? Mathf.Clamp01((selfProbe.WaterHeight - selfBelief.PositionMean.y) / 5f) : 0f;
            // Cargo-fill: probe carries num/cap.
            int cap = selfProbe.CargoCapacity;
            raw[B + ThreatSlots.AttackerCargoFill] = cap > 0 ? (float)selfProbe.CargoNumContents / cap : 0f;
            raw[B + ThreatSlots.AttackerReadyFireFraction] = totalW > 0
                ? (float)selfProbe.WeaponAggregate.ReadyToFireCount / totalW : 0f;
            raw[B + ThreatSlots.AttackerEnergyStored] = selfProbe.Electric.CurrentAmount / 10000f;

            // Engagement geometry [24..31]. Only meaningful when an actual hostile
            // is in range — represents attacker→victim relation.
            if (hasAttacker && attacker.Belief != null)
            {
                raw[B + ThreatSlots.DistanceToVictim] = Mathf.Sqrt(attacker.SqrDistance);
                // Attacker forward dot toward victim — uses selfProbe.ForwardWorld.
                Vector3 toVictim = attacker.Belief.PositionMean - selfProbe.PositionWorld;
                float toV = toVictim.magnitude;
                if (toV > 1e-3f)
                {
                    raw[B + ThreatSlots.AttackerHeadingTowardVictimDot] =
                        Vector3.Dot(selfProbe.ForwardWorld, toVictim / toV);
                }
                raw[B + ThreatSlots.LosBlockedToVictim] = losBlocked;
                // WeaponInRange: distance <= RangeMax?
                float rmax = selfProbe.WeaponAggregate.RangeMax;
                raw[B + ThreatSlots.AttackerWeaponInRange] =
                    rmax > 0f && attacker.SqrDistance <= rmax * rmax ? 1f : 0f;
                // Aimable flag: 1 when LOS clear AND in range. Coarse proxy.
                raw[B + ThreatSlots.AttackerAimableFlag] =
                    (losBlocked < 0.5f && rmax > 0f && attacker.SqrDistance <= rmax * rmax) ? 1f : 0f;

                // VictimWeakFaceTowardAttackerDot — requires the victim's ArmorMap
                // queried against the attacker→victim direction in the victim's
                // local frame. We only have victimProbe when the victim is on a
                // Smart team. When present, query its ArmorMap; otherwise leave 0.
                if (victimProbe != null && victimProbe.Armor != null && victimProbe.Armor != ArmorMap.Empty)
                {
                    // The probe-side weakFaceNormalWorld is the baseline (forward-
                    // attack) face. A precise per-attacker query needs to rotate
                    // toVictim into the victim's local frame; the daemon doesn't
                    // own the victim's rotation. Best-effort: dot against attacker
                    // forward (a coarse proxy).
                    raw[B + ThreatSlots.VictimWeakFaceTowardAttackerDot] =
                        Vector3.Dot(victimProbe.WeakFaceNormalWorld, -selfProbe.ForwardWorld);
                }
            }
            // §4.4 + R2-11: when a downstream consumer calls Read(victimId), it
            // expects slots [30, 31, 36, 37] to be filled from the victim's OWN
            // vector. In the daemon's per-tech pass the subject IS the victim from
            // that consumer's POV, so we fill from selfProbe.
            raw[B + ThreatSlots.VictimWeakFaceHp] = 0f;
            if (selfProbe.Armor != null && selfProbe.Armor != ArmorMap.Empty)
            {
                raw[B + ThreatSlots.VictimWeakFaceHp] =
                    selfProbe.Armor.QueryWeakFace(Vector3.forward).TotalHP;
            }
            raw[B + ThreatSlots.VictimHpFraction] = selfProbe.HpFraction;

            // Recent-damage windows [32..37].
            raw[B + ThreatSlots.RecentDamageDealt1s] = dmgDealt1sMag;
            raw[B + ThreatSlots.RecentDamageDealt5s] = dmgDealt5sMag;
            raw[B + ThreatSlots.RecentShotsFired1s] = shotsFired1s;
            // Dominant dealt-type, attacker-side slot 35 per R2-11.
            raw[B + ThreatSlots.RecentDamageDealtTypeCode] = dmgDealt1sType / 8f;
            // Victim-side dmg-taken windows [36..37]. Subject == victim from the
            // consumer's POV — fill from the same SumWithin reads the AV self-block
            // already consumed.
            raw[B + ThreatSlots.RecentDamageTakenByVictim1s] = dmgTaken1sMag;
            raw[B + ThreatSlots.RecentDamageTakenByVictim5s] = dmgTaken5sMag;

            // [38..47] reserved — mission/team-context/shield. No formula in §4.3.
        }

        // --- Helpers --------------------------------------------------------------

        // §4.3 SightCode: enum → {1.0, 0.5, 0.25, 0.0}. Plan cites BeliefState.cs:108.
        private static float SightCode(SightState s)
        {
            switch (s)
            {
                case SightState.Fresh:    return 1f;
                case SightState.Coasting: return 0.5f;
                case SightState.Stale:    return 0.25f;
                default:                  return 0f;       // Lost
            }
        }

        // §4.3 AnchorStateCode: 0=floating, 0.5=anchored, 1=sky-anchored.
        private static float AnchorCode(byte anchorState)
        {
            if (anchorState == (byte)AnchorStateCode.SkyAnchored) return 1f;
            if (anchorState == (byte)AnchorStateCode.Anchored)    return 0.5f;
            return 0f;
        }

        // --- Diagnostics ----------------------------------------------------------

        private void AccumulateTickDiagnostics(long tickStartStopwatch, int published, int skippedTerrain, int skippedNoProbe)
        {
            long tickEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            long elapsed = tickEnd - tickStartStopwatch;
            _windowTickCount++;
            _windowSumStopwatchTicks += elapsed;
            if (elapsed > _windowMaxStopwatchTicks) _windowMaxStopwatchTicks = elapsed;
            _windowTechsPublished += published;
            _windowSkippedNoProbe += skippedNoProbe;
            if (skippedTerrain > 0) _windowSkippedTerrainStale += skippedTerrain;

            if (_windowTickCount >= DiagnosticLogPeriodTicks)
            {
                double avgMs = _windowSumStopwatchTicks * 1000.0 * MonoClock.TickFreq / _windowTickCount;
                double maxMs = _windowMaxStopwatchTicks * 1000.0 * MonoClock.TickFreq;
                DebugTAC_AI.Log(string.Format(
                    "Smart.StrategicStateExtractor[diag] ticks={0} avg={1:F2}ms max={2:F2}ms published={3} terrainStale={4} noProbe={5}",
                    _windowTickCount, avgMs, maxMs,
                    _windowTechsPublished, _windowSkippedTerrainStale, _windowSkippedNoProbe));
                _windowTickCount = 0;
                _windowSumStopwatchTicks = 0L;
                _windowMaxStopwatchTicks = 0L;
                _windowTechsPublished = 0;
                _windowSkippedTerrainStale = 0;
                _windowSkippedNoProbe = 0;
            }
        }
    }
}
