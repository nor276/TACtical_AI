# Chassis (REV 3.1) -- typed block catalog with honest per-block thrust geometry

**One-line pitch.** Replace the per-spawn `GetComponent` storm and most of the role-axis-override switch in `ThrustMap.Compute` with a process-wide, lazily-populated, immutable **typed catalog** keyed on `BlockType`, plus a per-instance `BlockInstancePose` (full `Quaternion`, not lossy `LocalForward`). Aggregate propulsion for boosters / hover / wings / aircraft by **vector-summing block-local thrust samples rotated into chassis frame**. **Wheels keep a per-EmitterKind axis substitution** (`EmitterKind.WheelRoll -> chassis +Z`) -- preserved deliberately because wheel `LocalForward` is the roll axis (per `ThrustMap.cs:65-72`), not a role-override smell. Preserve the public `VehicleModelSnapshot` surface verbatim except for one additive ctor parameter (`BlockKindCounts`).

---

## Revision History

| Rev | Status | Notes |
|---|---|---|
| 1 | NEEDS-REVISION (5/5 adversarial reviewers) | C# 9 `new()` in headline; "WeaponProfile preserved bit-identically" false (today is placeholders); `WeaponRound.m_MuzzleVelocity` reflection target wrong (public field on `FireData`); "no role override" headline contradicted by wheel carve-out; ctor-site count inflated (claimed 6, real 3); consumer count undercounted (claimed 22, real 24); FanJet vs BoosterJet conflated; chassis-frame rotation self-contradicting; `SpecHP`/`HpFraction` referenced but never declared; yaw heuristic mis-framed as "additive lower bound"; `ModuleHammer` is phantom (0 references); `BlockKindCounts` strict-superset tolerance is type-mismatched; `VerticalAuthority` example inverted; Step 9 build-broken window understated. |
| 2 | SHIP-WITH-MINOR-FIXES (5/5 -- all REV 1 flaws fixed, 5 new convergent + several singleton paragraph-level issues found) | All 10 convergent flaws + 6 credible singletons addressed. Headline reframed to acknowledge wheel carve-out as deliberate per-EmitterKind preservation. `WeaponProfile` kept at placeholder values in v0.1 (new Decision #8); real reflected weapon values explicitly deferred to v0.2. `WeaponRound.m_MuzzleVelocity` dropped from reflection cache (FireData direct field access). FanJet probe split from BoosterJet probe. Rotation pattern resolved to `rootBlockTrans.InverseTransformDirection` (10x more uses in live code than `cachedLocalRotation.rot`); Step 6 hard-gate moved before Compute lock. `SpecHP`/`HpFraction` removed from schema; ArmorMap HP migration deferred to v0.2. Ctor sites split 3 direct + 3 indirect-via-`.Empty()`. Consumer count restated as 24. `ModuleHammer` references removed; `Drill`/`Repair` bits reserved-unreachable for v0.1. `BlockKindCounts` tolerance reworded as concrete per-flag-tally invariant. `VerticalAuthority` example corrected to tilted hover pads. Step 9 reframed as 3-commit migration with quantified build-broken windows. Yaw heuristic restated as `Min(3f, Max(real_torque_yaw, existing_heuristic_yaw))` clamped max-selector. |
| 3 | SHIP-WITH-MINOR-FIXES 3/3 (architecture sound; 6 convergent + 10 singleton MINOR/MAJOR doc-touch items) | All 5 REV 2 convergent flaws + 5 credible singletons addressed. BoosterJet "no transform needed" claim corrected. FanJet probe path single-inversion. Rotation source reconciled (Quaternion via `cachedLocalRotation.rot` for `BlockInstancePose`, Vector3 via `InverseTransformDirection` for `ThrustEmitter`). Migration Step 6 promoted to Step 3.5. `BlockKindCounts` declared `readonly struct`. `EmitterKind` cleaned. `Learning/LearningService.cs:222-225` reclassified as doc-comment-only. `ForwardDirectionLocal` deferred to v0.2. Step 8 honestly estimated. Self-contradictory rename sentence excised. |
| **3.1** | **Ready to migrate** | REV 3.1 lint pass. **Two MAJOR doc-readiness items (S1/S2) closed**: `ModuleHover` MaxForce defer-to-v0.2 with explicit 2g placeholder formula `block.CurrentMass * 9.81 * 2.0f` (matches today's `ThrustMap.cs:63`); same placeholder applied to `ModuleWing`. Step 3.5 hard-gate gains 5th bullet asserting probe-vs-production frame equivalence (`pose.LocalRotation * probeAxis == tank.rootBlockTrans.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection))` within float-eps on tilted-mount booster fixture). **Convergent paragraph-level lint (3/3)**: stale "Step 6" references at lines 212 + 446 corrected to Step 3.5; `BlockKindCounts` GC-profile entry updated from "struct/class, ~64 B" to "readonly struct, 72 B"; `AIControllerAir.cs:158/177` dropped from `InverseTransformDirection` citation list (those sites read bare `LocalThrustDirection`, no inversion). **2-reviewer convergent**: `ThreatField.cs` de-duplicated (consumer-impact tables); outside-Vehicle count corrected to 5 (was inflated by counting a Vehicle/ entry); `BarrelDirBlockLocal` v0.1 population path specified (probe-time `block.transform.InverseTransformDirection(weapon.transform.forward)`, read by no v0.1 consumer per Decision #2 deferral). **Singletons applied**: Decision #12 typo fixed (`FanLift` -> `FanJet`); Step 8a 23-vs-24 oscillation resolved; canonical `BlockKindCounts` struct-declaration block added to schema section; `WeaponKindFlag` defaults to `GunFixed` in v0.1 matching `VehicleModel.cs:134` fall-through; `spinDat` explicitly deferred to v0.2 with note; memory footprint restated as ~300-600 B/archetype (100-600 KB typical, ~3 MB worst-case heavy modding); Decision #13 diagonal-mount aggregation formula documented. |

---

## Background -- why redesign

The current `Vehicle/` layer has three intertwined pathologies:

1. **`BlockObservation` lossy-compresses geometry** to a single `Vector3 LocalForward` plus a single-value `BlockRole` enum (`VehicleModel.cs:67-108`). This drops the block's full mount rotation and forces single-role classification on blocks that have multiple modules (anchored turret, hover+wing, weapon+drill).

2. **`ThrustMap.Compute` has a role-override switch** (`ThrustMap.cs:75-89`) that *discards* the captured `LocalForward` and substitutes hardcoded axes per role (`Wheel/Walker -> +Z`, `Hover -> +Y`, `Jet/Prop -> b.LocalForward`). The comment at `ThrustMap.cs:65-72` explicitly documents why for wheels: wheel-block `LocalForward` is the roll axis, not the chassis forward, so summing it naively returns ~0. **For wheels, the role override is correct;** for hover and aircraft, it discards real geometry. This redesign keeps the wheel substitution (as a per-`EmitterKind` choice, not a per-`BlockRole` choice) and removes the rest.

3. **`BlockCapture.CaptureFromTank` invokes `GetComponent<Module*>` ~5x per block per rebuild** (`VehicleModel.cs:122-134`). On a 50-tech wave of 100-block techs, that's tens of thousands of reflection calls per spawn frame -- exactly what the existing `[TIMING]` instrumentation at `SmartRuntime.cs:218-246` was authored to measure.

The right invariant: **propulsion geometry is per-`BlockType` (immutable, prefab-derived) data; mount pose is per-instance data; chassis-frame thrust is the vector sum of (mount-rotation x block-local-thrust-axis) at composition time, with wheel emitters substituting chassis +Z as a deliberate per-EmitterKind choice**.

---

## Core design

### Block catalog shape

```csharp
public static class SmartRuntime {
    public static TypedBlockCatalog BlockCatalog { get; private set; }  // init in Init, Reset in Shutdown
}

public sealed class TypedBlockCatalog {
    // Key = (int)TankBlock.BlockType -- the same int VehicleModel.cs:82 already uses;
    // stable per session, verified at BlockIndexer.cs:81/94/106/156-159.
    // C# 7.3-compatible explicit construction (no target-typed new()).
    private readonly ConcurrentDictionary<int, BlockArchetype> _byType =
        new ConcurrentDictionary<int, BlockArchetype>();
    private long _hits, _misses;

    // MAIN-THREAD ONLY. Asserts main-thread in DEBUG.
    public BlockArchetype GetOrProbe(TankBlock liveOrPrefab) { ... }

    // Worker-safe. Returns null if not yet probed (workers never trigger probing).
    public BlockArchetype TryGet(int typeKey) { ... }

    public void Reset();  // called only from SmartRuntime.Shutdown, AFTER WorkerLifecycleRegistry.CancelAllAndJoin
}

public sealed class BlockArchetype {       // immutable; all readonly fields set in ctor
    public readonly int TypeKey;
    public readonly BlockKindFlags Kinds;  // [Flags] uint
    public readonly float SpecMass;        // from block.CurrentMass at probe time on prefab
    public readonly int CellCount;
    public readonly ThrustEmitter[] Emitters;   // length 0 if non-propulsion
    public readonly bool HasWeapon;             // C# 7.3: bool + WeaponSpec separate; NO Nullable<T> for struct
    public readonly WeaponSpec Weapon;          // valid only when HasWeapon == true
    public readonly Bounds LocalBounds;
}

public readonly struct ThrustEmitter {
    public readonly EmitterKind Kind;      // WheelRoll, FanJet, BoosterImpulse, HoverPad, WingLift (per Decision #12)
    public readonly Vector3 LocalAxis;     // unit vector in BLOCK-LOCAL frame, push direction
    public readonly Vector3 LocalMount;    // block-local offset from block origin
    public readonly float MaxForceN;
    public readonly float ReverseForceN;   // wheels brake/reverse; jets typically 0
    public readonly EmitterFlags Flags;    // Bidirectional, AerodynamicScaling, ConsumesFuel
}

public readonly struct WeaponSpec {
    public readonly WeaponKindFlag Kind;   // GunFixed | GunTurret | Beam -- v0.1 defaults to GunFixed (per S6)
    public readonly Vector3 BarrelDirBlockLocal;  // probe-populated; UNREAD by v0.1 consumers (Decision #2 defer)
    public readonly float Range, ProjectileVelocity, DamagePerShot, FireRateHz;
    public readonly float YawArcRad, PitchArcRad;
    public readonly int AmmoCapacity;
    public readonly bool IsEnergyWeapon;
    // NOTE: in v0.1, WeaponSpec scalar fields are populated with TODAY'S placeholder values
    // (range=100, projVel=200, damage=30, fireRateHz=1.5, ammoCap=100, isEnergy=false)
    // for full Decision-#8 behavior preservation. Kind defaults to GunFixed matching the
    // VehicleModel.cs:134 fall-through behavior today. BarrelDirBlockLocal IS populated
    // by the probe via block.transform.InverseTransformDirection(weapon.transform.forward)
    // but is read by NO v0.1 consumer (Decision #2 defers ForwardDirectionLocal honest-
    // geometry to v0.2). v0.2 swaps Kind/scalars/BarrelDirBlockLocal to real reflected values.
}

public readonly struct BlockKindCounts {
    // 18 int fields, one per BlockKindFlags bit. 72 B total, value-semantics (no heap alloc).
    public readonly int Structural, Wheel, Hover, Booster, Wing, Walker;
    public readonly int WeaponFixed, WeaponTurret;
    public readonly int Anchor, Conveyor, Holder, Producer, Repair, Shield;
    public readonly int EnergyStore, Generator, Drill, GyroStabilizer;
    // From(poses, catalog) walks poses, sums archetype.Kinds bits per bit-position.
    public static BlockKindCounts From(BlockInstancePose[] poses, TypedBlockCatalog catalog);
}
```

**Population.** `GetOrProbe` reaches `ManSpawn.inst.GetBlockPrefab(BlockType)` (verified at `AIGlobals.cs:51`, `AIERepair.cs:241/304/342`, `AIWiki.cs:38`, `BlockIndexer.cs:159`) -- returns `TankBlock`, not `GameObject` (verified). On the prefab, the probe walks the verified modules:

- `ModuleBooster` -- has both **FanJet children** and **BoosterJet children** (verified separation at `AIControllerAir.cs:151-198` and `AIControllerDefault.cs:248-287`). The probe walks each child explicitly. **Both paths use the production aggregator wrapping pattern** to convert child-component direction into block-local frame -- verified consistent in every live aggregator (`AIControllerDefault.cs:257/263/279/285`, `AIControllerAir.cs:158/177`, `AIECore.cs:602/615`, `Enemy/RCore.cs:279/295`):
  - **FanJet** path: read `(float)RawTechBase.thrustRate.GetValue(jet)` for forward thrust, `(float)RawTechBase.fanThrustRateRev.GetValue(jet)` for reverse thrust. `EffectorForward` is read in world-space then unwound into block-local via `block.transform.InverseTransformDirection(jet.EffectorForward)`. (Production aggregators wrap with `tank.rootBlockTrans.InverseTransformDirection(jet.EffectorForward)` because they want chassis-frame directly; the probe wants block-local because the per-instance pose then rotates back into chassis. NO double-transform: take exactly one inversion at probe time, then `pose.LocalRotation * emitter.LocalAxis` at composition time.) One `ThrustEmitter` of kind `FanJet` per FanJet child. **Sign convention from `AIControllerAir.cs:177`**: aggregators negate the result for the bias-direction subtraction (`biasDirection -= ...`); the emitter axis stored on the archetype is the **positive push direction**, with the sign convention left to the aggregator.
  - **BoosterJet** path: read `(float)AIGlobals.BoostForceField.GetValue(boost)` (the existing cached `Thruster.m_Force` reflection at `AIGlobals.cs:40`). `boost.LocalThrustDirection` is **jet-local, not block-local** -- production aggregators consistently wrap it: `block.transform.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection))` (pattern verified at `AIControllerDefault.cs:285`, `AIECore.cs:615`, `Enemy/RCore.cs:295`). The probe does the same. One `ThrustEmitter` of kind `BoosterImpulse` per BoosterJet child. **REV 2 was wrong to say "no transform needed"** -- the production aggregators all transform via `boost.transform.TransformDirection` first.
- `ModuleHover` -- bit-detection only via `GetComponent<ModuleHover>() != null`. **MaxForce is a 2g placeholder in v0.1**: `MaxForceN = block.CurrentMass * 9.81f * 2.0f` -- matches the per-block lift placeholder `ThrustMap.cs:63` uses today, so v0.1 ships bit-identical hover behavior. `LocalAxis = Vector3.up` in block-local frame. One `ThrustEmitter` of kind `HoverPad` per hover module instance. **v0.2 follow-up**: real per-pad MaxForce reflection target needs verification (REV 3 REV 3.1 lint noted this as the only remaining epistemic gap with the same shape as the prior `ModuleHammer` phantom; v0.1 explicitly defers).
- `ModuleWing` -- aerodynamic-scaling channel; `Flags |= AerodynamicScaling`. Local lift axis = `Vector3.up` block-local. **MaxForce 2g placeholder in v0.1** matching `ModuleHover`. v0.2 wires real wing lift from aerodynamic-pass module fields.
- `ModuleWheels` -- one `ThrustEmitter` of kind `WheelRoll` per wheel. `LocalAxis` is the wheel's roll axis (typically block-local +X, but irrelevant -- see Wheel substitution below).
- `ModuleWeapon` (only this v0.1; `WeaponSpec` placeholder values per Decision #8).
- `ModuleAnchor` -- bit only (verified `TankAIHelper.Anchoring.cs:113`).
- `ModuleItemConveyor` -- bit only (verified `AIERepair.cs:166`, `RawTechLoader.cs:3136`).
- `ModuleItemHolder` -- bit only when `Flags.Collector` is set (verified `Enemy/RCore.cs:504/838`).
- `ModuleItemProducer` -- bit only (verified `AIERepair.cs:490`).
- `ModuleShieldGenerator` -- bit only (verified `Enemy/RCore.cs:23-25/328`; **NOT** `ModuleShield`).
- `ModuleEnergyStore`, `ModuleEnergy` -- bits only.

**Reflection cache, pre-cached at `SmartRuntime.Init`:**

- `typeof(Thruster).GetField("m_Force", NonPublic|Instance)` -- via the existing `AIGlobals.BoostForceField` static; not a new cache. (BoosterJet path.)
- `RawTechBase.thrustRate` and `RawTechBase.fanThrustRateRev` -- existing fields used at `AIControllerAir.cs:151-198`. Cached on first FanJet probe.
- **NOT cached** (intentionally): `WeaponRound.m_MuzzleVelocity` -- it is a **public field on `FireData`** (verified at `EWeapSetup.cs:51,144,149,151`, `RWeapSetup.cs:105,110,112`), accessed via direct field read, no reflection ever. The REV 1 doc was wrong to list this in the FieldInfo cache.
- **NOT cached** in v0.1: `WeaponRound.m_Damage`, projectile-spec fields. These are needed only by the future v0.2 real-WeaponSpec migration (see Decision #8). The REV 2 v0.1 ships placeholders.
- **NOT cached** in v0.1: `RawTechBase.spinDat` (read by `AIControllerDefault.cs:254` + `AIControllerAir.cs:171` today for spool-state gating). v0.1 ships max-rated FanJet thrust without spool modulation (Failure mode #8 acknowledges the lie). **v0.2 deferral**: when FanJet spool gating lands, add `spinDat` to the pre-cache list alongside `thrustRate`/`fanThrustRateRev`.

If reflection on any prefab fails, the probe records a zero-force emitter, logs once via `LogWarnFileOnly` with the `[CHASSIS]` tag (per user's `ai-warning-routing-preference` memory), and caches the degraded archetype. **Failures are not retried** -- repeated catalog churn on a poison-pill block is worse than a degraded archetype.

**Lifecycle.** Created at `SmartRuntime.Init`. Optional synchronous Prewarm at form Init walks `ManSpawn.inst.GetLoadedTankBlockNames()` (verified pattern at `AIWiki.cs:36`, `BlockIndexer.cs:156-157`) to amortize probe cost into the loading-screen window. `Reset()` is called **only** from `SmartRuntime.Shutdown` and **after** `WorkerLifecycleRegistry.CancelAllAndJoin` (verified ordering at `SmartRuntime.cs:549-573`).

**Memory.** ~300-600 bytes per archetype (base ~250 B + per-emitter ~56 B × typical 1-4 emitters + optional ~60 B `WeaponSpec` for weapon archetypes) x ~300-1000 distinct types per typical session = **~100-600 KB process-wide, allocated once**. Heavy-modding worst-case (5000 distinct types) bounded at **~3 MB**. The "60-200 KB" claim in REV 2/3 understated by ignoring emitter-array + WeaponSpec contributions; REV 3.1 corrects to honest accounting.

### Per-block thrust geometry

```csharp
public readonly struct BlockInstancePose {
    public readonly int TypeKey;
    public readonly Vector3 LocalPosition;            // block.cachedLocalPosition (chassis frame)
    public readonly Quaternion LocalRotation;         // block.cachedLocalRotation.rot -- Quaternion-native
    public readonly float CurrentMass;                // block.CurrentMass at composition time
    // NO HpFraction in v0.1 -- ArmorMap HP migration deferred to v0.2 (no SpecHP either)
}
```

**Chassis-frame rotation -- read pattern resolved (REV 3).** Two patterns coexist in live code; REV 3 uses the right one for each role:

- **Schema (block pose stored on `BlockInstancePose.LocalRotation`):** `block.cachedLocalRotation.rot` -- the Quaternion source verified at `AIERepair.cs:190/211` and `RawTechLoader.cs:3172`. This is a serializable per-block rotation in the chassis frame; the type matches `Quaternion` natively. This is what the schema needs.
- **Production aggregator pattern, used at probe time to unwind child-component directions:** `block.transform.InverseTransformDirection(...)` returns `Vector3`. Used inside `ArchetypeProbe` to convert FanJet `EffectorForward` and BoosterJet wrapped `LocalThrustDirection` into block-local axes (stored on `ThrustEmitter.LocalAxis`). Verified at `AIControllerDefault.cs:257/263/279/285`, `AIECore.cs:602/615`, `Enemy/RCore.cs:279/295`. (Note: `AIControllerAir.cs:158/177` read bare `LocalThrustDirection` in block-local frame for per-component thrust sums -- they do NOT follow the inversion pattern; cited separately as the air-controller exception. The probe matches the inversion pattern, not the air-controller bare-reads.)

The two patterns are NOT alternatives -- they answer different questions:
- `BlockInstancePose.LocalRotation` = "where is this block oriented within the chassis right now" -> Quaternion via `cachedLocalRotation.rot`.
- `ThrustEmitter.LocalAxis` = "in this block-type's prefab, what direction does this emitter push" -> Vector3 via `block.transform.InverseTransformDirection(child.transform.TransformDirection(child.LocalThrustDirection))` at probe time.

Composition math (`ThrustField.Compute`): `chassisAxis = pose.LocalRotation * emitter.LocalAxis` -- Quaternion times Vector3 returns the rotated Vector3 in the chassis frame. Type-coherent.

**Migration Step 3.5 (the hard gate, moved up from Step 6 in REV 2) empirically verifies BOTH read patterns on a fixture block BEFORE `ThrustField.Compute` is written**, so any orientation-mismatch on real blocks surfaces before the Compute pass locks in.

**Composition math** (in `ThrustField.Compute`):

```
For each BlockInstancePose pose in poses:
  archetype = catalog.TryGet(pose.TypeKey)
  if archetype == null continue  // not yet probed -- skip, will appear next rebuild
  for each ThrustEmitter emitter in archetype.Emitters:
    Vector3 chassisAxis;
    if (emitter.Kind == EmitterKind.WheelRoll) {
        // Deliberate per-EmitterKind preservation: wheel-block LocalForward is the
        // roll axis, not the chassis forward (see ThrustMap.cs:65-72 comment).
        // Substitute chassis +Z. This is NOT a role override -- it's a per-emitter-kind
        // choice, isolated to WheelRoll. v0.2 may replace with first-principles
        // wheel-roll model.
        chassisAxis = Vector3.forward;
    } else {
        // Vector geometry: pose's rotation maps block-local axis to chassis-local axis.
        chassisAxis = pose.LocalRotation * emitter.LocalAxis;
    }
    Vector3 worldForce = chassisAxis * emitter.MaxForceN;
    posAccel += worldForce / mass.TotalMass;
    // (reverse handled symmetrically for emitters with ReverseForceN > 0)
```

Angular capacity: `tau = (r - CoM) x F` per emitter, summed, projected onto principal inertia axes from `MassDistribution`. For wheel-only techs we also keep the existing yaw heuristic **as a clamped max-selector with real torque, not an additive lower bound**:

```
yawCapacity = Mathf.Min(3f, Mathf.Max(realTorqueYaw, existingYawHeuristic));
// where existingYawHeuristic = max(Min(posAccel.x, negAccel.x) * 0.1f,
//                                  Max(posAccel.z, negAccel.z) * 0.3f)
// matches today's ThrustMap.cs:119-121 verbatim.
```

This is flagged as a deliberate preservation per the `organic-vs-bug design value` memory: don't replace the 30%-of-forward shim with raw physics in this PR; clamp at the max of the two.

### Multi-axis mobility budget

`ThrustField` (renames `ThrustMap` -- both the class and the `VehicleModelSnapshot.Thrust` property type -- via the 3-commit migration in Step 8; preserves every existing public field):

```csharp
public sealed class ThrustField {  // ThrustMap renamed; VehicleModelSnapshot.Thrust property type updated
    public readonly Vector3 MaxLinearAccelPositive;   // PRESERVED -- scalar reads still work
    public readonly Vector3 MaxLinearAccelNegative;   // PRESERVED
    public readonly Vector3 MaxAngularAccel;          // PRESERVED
    public readonly IReadOnlyList<PropulsionBlock> Blocks;  // PRESERVED for SmartTestSuite + diagnostic

    // NEW additive fields (not consumed by v0.1; v0.2 wires them):
    public readonly Vector3 LiftBudget;          // dedicated Y channel -- distinguishes hover from wheel
    public readonly Vector3 GroundTractionBudget;
    public readonly Vector3 JetBudget;
    public readonly Vector3 AeroLiftBudget;
    public readonly PropulsionTopology Topology;  // Wheel1D, Hover2D, Aircraft3D, Walker, Mixed, Static
}
```

`MobilityProfile` keeps every existing public field unchanged (`TopSpeedForward`, `TopSpeedBackward`, `TopSpeedLateral`, `TurningRadiusAtTopSpeed`, `ClimbAngleMax`, `WaterCapable`, `VerticalAuthority`, `TippingSusceptibility`). New additive field: `PropulsionTopology Topology`.

**Behavioral change to `VerticalAuthority` -- corrected example.** Today `VerticalAuthority = MaxLinearAccelPositive.y / 9.81`. After the refactor, a **tilted hover pad** (mounted at an angle, e.g. on a sloped roof) contributes partial-Y to the lift budget instead of full-Y; identity classifications on edge techs around `HoverAsAircraftMinVerticalAuth = 1.0f` and `AircraftCompositionMinVerticalAuth = 1.5f` may shift downward by the cosine of the tilt. (REV 1's "vertical-mounted booster on a wheel tank" example was wrong: today's code already handles axis-aligned boosters correctly via the `Jet -> b.LocalForward` branch -- the real risk surface is tilted mounts where geometry was previously rounded to the role's nominal axis.) Parallel-run gate quantifies the delta on fixture techs before flipping over.

### Spawn pipeline

```
SmartForm.OnTechSpawn (line 262, unchanged)
  -> state.RebuildVehicleSnapshotNow()
  -> SmartPerTechState.RebuildVehicleSnapshotInternal()  // PRESERVED entry point, INTERNALS replaced:
      var poses = ChassisCapture.Capture(tank, SmartRuntime.BlockCatalog);
        // single pass over tank.blockman.IterateBlocks()
        // per block: catalog.GetOrProbe(block) -- O(1) after first sighting per type per session
        // per block: read cachedLocalPosition, cachedLocalRotation.rot (rotation lock per Step 3.5), CurrentMass
        // ZERO GetComponent calls on instances after warmup
      var mass     = MassDistribution.Compute(poses, BlockCatalog);
      var thrust   = ThrustField.Compute(poses, BlockCatalog, mass);
      var weapons  = WeaponProfileBuilder.Build(poses, BlockCatalog, tank);
        // V0.1: WeaponProfile fields stay at TODAY'S placeholder values (Range=100,
        // Damage=30, etc.). See Decision #8. v0.2 swaps in real reflected values.
        // Dynamic ammo/cooldown ALSO stays at placeholder in v0.1 -- no new GetComponent storm.
      var armor    = ArmorMap.Compute(poses, BlockCatalog, new Vector3Int(8,8,8));
        // V0.1: mass-as-HP placeholder PRESERVED -- no SpecHP, no HpFraction.
      var mobility = MobilityProfile.Derive(mass, thrust, armor);
      var kindCounts = BlockKindCounts.From(poses, BlockCatalog);  // NEW
      var snapshot = new VehicleModelSnapshot(TechId.Value, LastObservationTick,
                       mass, thrust, weapons, armor, KinematicTracker.Latest, mobility,
                       kindCounts);  // NEW ctor param -- the one additive break
      VehicleBuffer.Write(snapshot);  // PRESERVED -- DoubleBuffer publish
```

The first-sighting probe cost on novel BlockTypes inside a spawn frame is non-zero -- bounded by `~5-10 distinct new types x ~12 reflection ops`. With Prewarm at form Init, this is amortized into the loading screen. Without Prewarm, a 50-tech wave introducing 10 novel types pays ~120 reflection ops once, then 0 forever. Compare to today: 50 x 100 x 5 = **25,000 `GetComponent` calls per wave**, every wave.

---

## New block roles

`BlockKindFlags` is a `[Flags] uint`:

```csharp
[Flags] public enum BlockKindFlags : uint {
    None        = 0,
    Structural  = 1 << 0,
    Wheel       = 1 << 1,
    Hover       = 1 << 2,
    Booster     = 1 << 3,
    Wing        = 1 << 4,
    Walker      = 1 << 5,   // reserved-unreachable; ModuleWalker absent in this TerraTech version
    WeaponFixed = 1 << 6,
    WeaponTurret= 1 << 7,
    Anchor      = 1 << 8,   // ModuleAnchor (verified)
    Conveyor    = 1 << 9,   // ModuleItemConveyor (verified)
    Holder      = 1 << 10,  // ModuleItemHolder w/ Collector flag (verified)
    Producer    = 1 << 11,  // ModuleItemProducer (verified)
    Repair      = 1 << 12,  // RESERVED-UNREACHABLE in v0.1 -- no verified probe target;
                            // v0.2 follow-up when ModuleHealBeam/BlockType allowlist is identified
    Shield      = 1 << 13,  // ModuleShieldGenerator (verified -- NOT ModuleShield)
    EnergyStore = 1 << 14,  // ModuleEnergyStore
    Generator   = 1 << 15,  // ModuleEnergy with generator OutputCondition
    Drill       = 1 << 16,  // RESERVED-UNREACHABLE in v0.1 -- no verified probe target;
                            // v0.2 follow-up when drill-block identification path is verified
    GyroStabilizer = 1 << 17,
}
```

(REV 2: `Walker`/`Drill`/`Repair` are **reserved bit slots that no probe sets in v0.1**. They are listed so the bit-layout is stable across v0.1/v0.2 -- ABI consideration for the snapshot consumers that will eventually read them. `SmartIdentityClassifier` v0.1 must therefore not produce identity decisions that depend on `Drill` or `Repair` -- those rows are deferred with the probe.)

`BlockRole` (the existing single-value enum) is kept as a backward-compat shim: `BlockArchetype.PrimaryKind()` returns a precedence-ordered single role for any code path that wants the legacy view. Existing callers of `b.Role == BlockRole.Wheel` (which after the refactor exist only inside the `Vehicle/` folder -- verified by grep) continue to compile.

`SmartIdentityClassifier` gains **optional, additive** new field reads on `vehicle.BlockKindCounts`. New rows in v0.1 (drill/repair-dependent rows deferred):

- `Anchor count > 0` -> strong Base composition signal
- `Conveyor | Holder count > 0` -> Gatherer composition signal (partial fix for `SmartIdentityClassifier.cs:71-83` Q1 limitation; Drill-based Gatherer signal lands in v0.2)

Existing rows (authored hint, mobility thresholds) are not removed. New rows are strictly additive.

---

## Consumer impact (honest file-by-file)

**Touched files inside `Vehicle/`:**

| File | LOC | Change |
|---|---|---|
| `Vehicle/VehicleModel.cs` | ~80 removed, ~5 added | Delete `BlockObservation`, `BlockCapture`. Re-type `VehicleModelSnapshot.Thrust` from `ThrustMap` to `ThrustField`. Add NEW `BlockKindCounts` field + corresponding ctor param. Update `Empty(id)` factory body. |
| `Vehicle/ThrustMap.cs` | ~140 replaced | Rename to `ThrustField.cs`. Delete non-wheel role-switch (lines 75-89 cases for Hover/Jet/Prop/etc.); preserve wheel-substitution as per-EmitterKind choice in `ThrustField.Compute`. Vector-aggregation over `BlockInstancePose[]`. Preserve `MaxLinearAccelPositive/Negative/MaxAngularAccel/Blocks` surface. Add `LiftBudget/GroundTractionBudget/JetBudget/AeroLiftBudget/Topology`. Preserve `PropulsionBlock` struct for `SmartTestSuite` constructor compat. Keep wheel-yaw clamped-max-selector at 30%-of-forward. |
| `Vehicle/MassDistribution.cs` | ~10 | Compute signature changes `BlockObservation[]` -> `BlockInstancePose[]`. Math unchanged. |
| `Vehicle/ArmorMap.cs` | ~5 | Compute signature changes `BlockObservation[]` -> `BlockInstancePose[]`. **HP source PRESERVED at mass-as-HP placeholder in v0.1.** (`SpecHP`/`HpFraction` removed from schema -- deferred to v0.2.) |
| `Vehicle/WeaponProfile.cs` | ~10 | `WeaponProfileBuilder.Build` signature changes. **Field values PRESERVED at today's placeholders in v0.1** (`Range=100`, `ProjVel=200`, `Damage=30`, `FireRateHz=1.5`, arc widths unchanged, `AmmoCapacity=100`, `AmmoCurrent=100`, `Cooldown=0`, `IsEnergy=false`). See Decision #8. Real reflected values land in v0.2. |
| `Vehicle/MobilityProfile.cs` | ~30 added | Derive signature: `ThrustMap` -> `ThrustField`. Add `Topology` field. NEW additive only; existing fields preserved. |

**New files (csproj-registered -- old-style csproj per `modularization-refactor-progress` memory):**

| File | LOC |
|---|---|
| `Vehicle/TypedBlockCatalog.cs` | ~120 |
| `Vehicle/BlockArchetype.cs` | ~80 |
| `Vehicle/ThrustEmitter.cs` | ~30 |
| `Vehicle/WeaponSpec.cs` | ~40 |
| `Vehicle/BlockInstancePose.cs` | ~25 |
| `Vehicle/BlockKindFlags.cs` | ~30 |
| `Vehicle/ChassisCapture.cs` | ~80 |
| `Vehicle/ArchetypeProbe.cs` | ~180 (the only place that does engine reflection; smaller than REV 1 because Weapon real-fields deferred) |
| `Vehicle/PropulsionTopology.cs` | ~15 |
| `Vehicle/BlockKindCounts.cs` | ~40 |

**Touched files outside `Vehicle/` (verified count: 5):**

| File | Change | Why |
|---|---|---|
| `Smart/SmartRuntime.cs` | ~15 lines | Add `BlockCatalog` static, init in `Init()`, `Reset()` in `Shutdown()` AFTER `CancelAllAndJoin`. `RebuildVehicleSnapshotInternal` body swap (lines ~227-244). Preserve the 6-step `[TIMING]` granularity. **Plus**: `SmartRuntime.cs:239` direct `new VehicleModelSnapshot(...)` gains `BlockKindCounts` argument. |
| `Smart/Tests/SmartTestSuite.cs` | ~10 lines | Direct `new VehicleModelSnapshot(...)` at `SmartTestSuite.cs:318` gains `BlockKindCounts` argument. Also: 3 sites constructing `new ThrustMap(new PropulsionBlock[0], ...)` at lines 168, 180, 313 get the `ThrustMap` -> `ThrustField` type rename. |
| `Smart/SmartForm.cs` | **ZERO functional changes.** | `OnTechSpawn:262-278` reads `state.VehicleBuffer.Read().Thrust.MaxLinearAccelPositive.magnitude` -- still works. Note: `magnitude` reads larger for techs with previously-collapsed lateral capacity, slightly inflating `BeliefState.MaxAccelerationEstimate` -- flagged below in Decisions. |
| `Smart/TACtical_AI.csproj` | ~12 lines | Register every new `.cs` (old-style csproj). |
| `Pathing/ThreatField.cs` | **0 lines** (propagation only) | `.Empty()` factory call at `ThreatField.cs:195` inherits the new `BlockKindCounts` field via the factory — no per-call edit needed at this consumer. Listed here for awareness; counts as a zero-change consumer below.

(REV 3.1 correction: REV 2 inflated this to count 6 by double-listing `Smart/Vehicle/VehicleModel.cs`, which lives inside `Vehicle/` and is counted in the Vehicle/ table above. Real outside-Vehicle touch is 5 files; ThreatField is zero-change here.)|

**Direct `new VehicleModelSnapshot(...)` call sites: 3** (verified via grep `^new VehicleModelSnapshot\(`):
1. `Vehicle/VehicleModel.cs:176` -- inside `Empty(int)` factory body.
2. `Smart/SmartRuntime.cs:239` -- production rebuild path.
3. `Tests/SmartTestSuite.cs:318` -- test setup.

**Indirect `Empty(...)` propagating call sites: 3** (sites that call `VehicleModelSnapshot.Empty(int)` and propagate the new field transparently):
- `SmartRuntime.cs:88`
- `SmartRuntime.cs:372`
- `Pathing/ThreatField.cs:195`

These do **not** need per-line edits; they inherit the new field through the factory.

**TRULY ZERO-change direct consumers of `VehicleModelSnapshot` (verified by grep -- 24 files reference the type total; REV 3 reclassifies `Learning/LearningService.cs:222-225` as a doc-comment-only false consumer per REV 2 review; **honest direct-consumer count is 23**; 17 consume it without ctor/Empty calls; subset listed below):**

- `Control/PhysicsRollout.cs` -- reads `MaxLinearAccelPositive.z`, `MaxLinearAccelNegative.z`, `MaxAngularAccel.y`, `Mass.TotalMass`, `Mobility.VerticalAuthority`. All preserved on `ThrustField`.
- `Control/ContinuousController.cs` -- reads `Mobility.VerticalAuthority`, `Mobility.TopSpeedForward`. Preserved.
- `Control/SamplingMPC.cs` -- takes `VehicleModelSnapshot vehicle` parameter at line 66; reads `vehicle.Mass` and `vehicle.Thrust`. Preserved.
- `Control/CostFunction.cs` -- reads `Weapons` only (not `Thrust`). Preserved.
- `Control/TacticalOptimizer.cs` -- reads `Mobility.TopSpeedForward`, `Weapons`. Preserved.
- `Control/WeaponFireController.cs` -- reads `Weapons[i]`. Preserved.
- `Coordination/TargetAssignment.cs` -- reads `Kinematics.PositionWorld`, `Weapons[i].Range`. Preserved.
- `Coordination/PlanDecomposition.cs` -- reads `Mobility.TopSpeedForward`, `Kinematics.PositionWorld`, `Weapons[i].Range`. Preserved.
- `Coordination/RoleAssignment.cs` -- reads `Mobility.TopSpeedForward`, `Mobility.TippingSusceptibility`. Preserved.
- `Coordination/Coordinator.cs` -- consumes the `IReadOnlyDictionary<TechId, VehicleModelSnapshot>` surface. Preserved.
- `Pathing/ThreatField.cs` -- reads `Weapons` collection AND calls `.Empty(int)` (propagation site, see above).
- `Identity/SmartIdentityClassifier.cs` -- reads `Mobility.TopSpeedForward`, `Mobility.VerticalAuthority`, `Weapons.Count`. Optional NEW reads of `BlockKindCounts` are additive.
- `Identity/{Hunter,Base,Sniper,Gatherer,AircraftHunter,AircraftSupport}GoalSource.cs` (6 files) -- read `Weapons[i].Range` only. Preserved.
- `Identity/SmartIdentity.cs` -- preserved.
- ~~`Learning/LearningService.cs:222-225`~~ -- REV 2 review revealed this is a `/// summary` doc-comment mention, **not a runtime consumer**. REV 3 removes from the direct-consumer list. Grep total of 24 stands, but direct-consumer count is 23.
- `World/AetherFuser.cs` -- does not read `VehicleModelSnapshot`. (Listed for completeness; not a consumer.)

**`MobilityProfile`-only consumers (read the profile, not the snapshot) -- reclassified as INDIRECT, NOT direct consumers per REV 2:**

- `Pathing/CapabilityFilters.cs` -- `FromMobility(MobilityProfile, ...)` takes the profile as a parameter. Does NOT reference `VehicleModelSnapshot` type. (Note: `VerticalAuthority` value changes for some techs -- see Decisions.)
- `Pathing/TerrainMap.cs` -- reads `Mobility.VerticalAuthority`, `Mobility.WaterCapable` via the profile parameter, not the snapshot.

**Honest consumer count: 23 direct consumers + 1 doc-comment-only mention = 24 grep hits across `Smart/`.** REV 1 undercounted by 2 (missed `SamplingMPC.cs:66`, then-undiscovered `Learning/LearningService.cs:222-225` mention). REV 1 also misclassified `Pathing/CapabilityFilters.cs` and `Pathing/TerrainMap.cs` as direct consumers (they only see `MobilityProfile`). REV 2 took the grep total of 24 at face value; REV 3 distinguishes runtime consumers (23) from doc-comment mentions (1 in `LearningService`).

---

## Threading model

**Unchanged contract.** `VehicleModelSnapshot` remains a `sealed class` with `readonly` fields, published via `DoubleBuffer<VehicleModelSnapshot>` per-tech. All snapshot consumers (workers + main) read `VehicleBuffer.Read()` exactly as today.

**New shared state: `TypedBlockCatalog._byType`** -- `ConcurrentDictionary<int, BlockArchetype>`.

- **Writes (`GetOrProbe`) are MAIN-THREAD ONLY.** Asserts main-thread in DEBUG. Triggered exclusively from `ChassisCapture.Capture`, which runs from `SmartPerTechState.TickMainThread` or `RebuildVehicleSnapshotNow` (both main-thread-only per `SmartRuntime.cs:213`).
- **Reads (`TryGet`) are worker-safe.** `BlockArchetype` is sealed, all-readonly. Workers traversing a snapshot's `BlockInstancePose[]` can call `catalog.TryGet(pose.TypeKey)` without locking. **Workers do NOT trigger probing.**
- **`ConcurrentDictionary.GetOrAdd` factory may run more than once under contention** (.NET BCL contract). In practice one writer means zero contention; the probe is idempotent (pure function of the prefab), so an extra discarded archetype is wasted work, not a correctness bug.

**Snapshot-to-catalog dependency.** `BlockInstancePose` carries only `TypeKey` (an int), not a direct archetype reference. Aggregation (`ThrustField`, `WeaponProfileBuilder`, etc.) **dereferences the catalog at snapshot-construction time** and stores resolved values directly on the snapshot. Workers consuming the snapshot do NOT need to call `catalog.TryGet` -- the snapshot is self-contained.

**`Shutdown` ordering:** `WorkerLifecycleRegistry.CancelAllAndJoin` -> workers drop snapshot references -> `BlockCatalog.Reset()` -> archetypes GC'd. Verified at `SmartRuntime.cs:549-573`.

**No new locks. No new long-running workers. No changes to `AetherFuser`, `GlobalPlannerDaemon`, `GlobalCoordinatorDaemon`, `CircuitBreaker`.**

---

## GC profile (honest accounting)

**Current per-rebuild (verified):**
- `BlockCapture.CaptureFromTank` allocates `List<BlockObservation>(16)`, grows to N, `ToArray()` -> 2 heap allocations + array
- `ThrustMap.Compute` allocates `List<PropulsionBlock>(blocks.Length)` + ToArray
- `WeaponProfileBuilder.Build` allocates `List<WeaponProfile>` + ToArray
- `ArmorMap.Compute` allocates `float[8x8x8]` = 2 KB
- `VehicleModelSnapshot` sealed-class instance
- **~7-9 heap allocations per rebuild + transient ~3 KB**
- **Plus: 5 `GetComponent<Module*>` per block per rebuild** -- Unity-side reflection cost, not .NET GC, but the dominant pathology under spawn storms.

**New per-rebuild:**
- `BlockInstancePose[]` -- one array of struct (~40 B each x N blocks). One allocation.
- `WeaponProfile[]` -- one allocation, exact-sized after archetype precount.
- `ArmorMap` `float[]` -- preserved.
- `VehicleModelSnapshot` -- preserved.
- `BlockKindCounts` -- one `readonly struct`, 72 B inline on the snapshot (no heap allocation per Decision #11; 18 int fields x 4 B each).
- **~5 heap allocations per rebuild + transient ~3 KB**
- **Zero `GetComponent` calls on weapon blocks in v0.1** (placeholder values; v0.2 introduces ammo/cooldown reads).

**Per-session amortized:**
- ~300-1000 archetype probes total, **once ever**. Each probe does ~10-15 reflection ops on a prefab. After warmup, the catalog hit rate stabilizes at ~99%.
- Catalog footprint: ~60-200 KB process-wide, fixed.

**Honest framing:** the per-rebuild managed-GC delta is modest. The real win is **eliminating the Unity-side `GetComponent` storm**. The `[TIMING]` diagnostic at `SmartRuntime.cs:218-246` will show the dramatic improvement (from milliseconds to hundreds of microseconds per rebuild). The "5x reduction in managed GC" claim from input designs was overstated; the truth is "modest GC win, major reflection-cost win, both real."

---

## What stays from current implementation

- `VehicleModelSnapshot` sealed-class shape preserved. **One additive break: ctor signature gains `BlockKindCounts` parameter.** 3 direct ctor-call sites updated (`VehicleModel.cs:176` Empty body, `SmartRuntime.cs:239`, `SmartTestSuite.cs:318`). 3 `.Empty()` propagation sites unchanged. `Pathing/ThreatField.cs:195` uses `.Empty(int)` -- propagates transparently.
- `DoubleBuffer<VehicleModelSnapshot>` per-tech `VehicleBuffer` publish surface -- unchanged.
- `SmartPerTechState.RebuildVehicleSnapshotNow` entry point + `% 30` rebuild cadence -- unchanged.
- `MassDistribution` struct shape, `Empty`, point-mass aggregation math -- unchanged.
- `MobilityProfile` field set preserved. New `Topology` field is additive.
- `WeaponProfile` struct + `WeaponFireMode` enum -- preserved bit-identically. **Field VALUES also preserved at today's placeholders in v0.1** (Decision #8). v0.2 swaps in real reflected values.
- `ArmorMap` class, `QueryWeakFace`, `GridResolution`, `LocalBounds` -- preserved. **HP source preserved at mass-as-HP placeholder.**
- `KinematicState` struct + `KinematicTracker` -- entirely unchanged (no block coupling).
- `BlockRole` enum kept as backward-compat shim returning `PrimaryKind()` precedence.
- `PropulsionBlock` struct preserved on `ThrustField.Blocks` for `SmartTestSuite` constructor compat.
- `[TIMING]` diagnostic at `SmartRuntime.cs:218-246` -- preserved, with 6-step granularity (do NOT collapse to 2 steps; the bisection is load-bearing for spawn-freeze investigation).
- Existing wheel-yaw heuristic -- preserved as **clamped max-selector under real torque**, not additive lower bound.
- `BeliefState.MaxAccelerationEstimate` path (`SmartForm.cs:274-277`) -- preserved.
- Per the `comments-stripped-recomment-style` memory: new files get terse author-voice comments, not patch-notation.

---

## What gets deleted

| Item | Reason |
|---|---|
| `BlockObservation` struct (in `VehicleModel.cs`) | Lossy single-vector + single-role compression. Replaced by `BlockInstancePose` + `BlockArchetype` split. |
| `BlockCapture` static class | Per-block 5x `GetComponent` storm. Replaced by `ChassisCapture` + `ArchetypeProbe` (amortized to once-per-type-per-session). |
| `ThrustMap.Compute` non-wheel role-switch (`ThrustMap.cs:75-89` cases for Hover/Jet/Prop) | Discarded geometry. Replaced by vector aggregation. **Wheel case preserved as per-EmitterKind axis substitution in `ThrustField.Compute`.** |
| `ThrustMap.cs` filename | Renamed to `ThrustField.cs` (class also renamed). Migration: 3-commit window (see Step 8). |

`BlockRole` enum is **kept** as a deprecated backward-compat shim (extension method `BlockKindFlags.ToPrimaryRole()`). Don't delete it in v0.1.

---

## Failure modes considered

1. **`ManSpawn.inst.GetBlockPrefab` returns null** (mid-load, mod not yet registered, corrupt save).
   *Mitigation:* `BlockArchetype.Unknown` sentinel -- zero emitters, zero functions, 1 kg, `BlockKindFlags.None`. NOT cached under the real key (retry on next sighting). One-time `LogWarnFileOnly[CHASSIS]` per unknown type.

2. **Reflection field missing** (`Thruster.m_Force`, `RawTechBase.thrustRate`, `RawTechBase.fanThrustRateRev`).
   *Mitigation:* `FieldInfo` cached at `SmartRuntime.Init`. Null check at cache-build, logged once, archetype records 0 N for that channel. Per-kind fallback: booster -> `Vector3.forward` block-local, hover/wing -> `Vector3.up`. **Worst case degrades to today's behavior, never worse.**

3. **`ModuleAnchor` / `ModuleItemConveyor` etc. don't exist on modded blocks.**
   *Mitigation:* Probe silently emits zero bits; `BlockKindFlags.None`-equivalent. SmartIdentityClassifier's existing thresholds carry the load.

4. **`pose.LocalRotation * wheel emitter local +Z` lands on chassis +X** (the documented `ThrustMap.cs:67-72` bug).
   *Mitigation:* For `EmitterKind.WheelRoll`, `chassisAxis = Vector3.forward` directly at composition time. **Deliberate preservation of today's working substitution**, isolated to wheels, documented as per-EmitterKind not per-Role. Verified at migration Step 3.5 (rotation hard-gate, pure wheel tank fixture) and re-verified at Step 5 parallel-run gate (which lists "pure wheel tank" in its fixture set).

5. **Catalog grows unbounded under heavy modding** (5000+ types).
   *Mitigation:* `ConcurrentDictionary` handles 10k entries trivially. ~2 MB worst case. No LRU eviction in v0.1.

6. **`tank.rootBlockTrans.InverseTransformDirection` returns wrong frame on partially-built techs** (during rebuild).
   *Mitigation:* `RebuildVehicleSnapshotInternal` runs from `OnTechSpawn` or the `% 30` cadence on a complete tech. Documented invariant.

7. **`VerticalAuthority` value shift reclassifies edge techs.**
   *Mitigation:* Parallel-run gate at migration Step 5 quantifies the delta on fixture techs before flipping over. If shift > 10% on representative techs, hold and re-tune `HoverAsAircraftMinVerticalAuth` / `AircraftCompositionMinVerticalAuth` before swap.

8. **FanJet spool-state lie** (catalog caches steady-state max; spinup takes ~1s).
   *Mitigation:* Acknowledged behavior change. `VerticalAuthority` reads max-rated even during spinup. If this regresses takeoff-classification feel, v0.2 can add a `spoolFraction` per-instance modulation; v0.1 ships the lie and observes.

9. **Cascade of `MobilityProfile` field shifts (TopSpeedLateral, TurningRadiusAtTopSpeed, ClimbAngleMax) under honest-rotation aggregation.**
   *Mitigation:* Parallel-run gate at Step 5 quantifies deltas on these fields too, not just `TopSpeedForward` + `VerticalAuthority`. Tolerance: each field within +/-15% on the fixture set.

---

## Migration plan (build-stable, parallel-run gated)

Per the `multi-agent-verification` memory and the `[VEHICLE-PARITY]` gate pattern several reviews recommended:

1. **Add new files** flat under `Vehicle/` (matches existing layout per Decision #14): `BlockKindFlags`, `TypedBlockCatalog`, `BlockArchetype`, `ThrustEmitter`, `WeaponSpec`, `BlockInstancePose`, `ChassisCapture`, `ArchetypeProbe`, `PropulsionTopology`, `BlockKindCounts`. Register each in `TACtical_AI.csproj` (old-style). **Builds clean. No call sites yet.**

2. **Wire `SmartRuntime.BlockCatalog`**: add static property, init in `Init()`, `Reset()` in `Shutdown()` AFTER `CancelAllAndJoin`. Add catalog hit/miss counters to the existing `[TIMING]` diagnostic. **Builds clean. Catalog exists, unused.**

3. **Implement `ArchetypeProbe` against the verified module set.** Pre-cache `FieldInfo` for `Thruster.m_Force` (via `AIGlobals.BoostForceField` existing static), `RawTechBase.thrustRate`, `RawTechBase.fanThrustRateRev`. **No reflection on `FireData.m_MuzzleVelocity` -- public field, direct access only.** No reflection on `WeaponRound.m_Damage` in v0.1 (Decision #8 keeps placeholder values). Add per-prefab unit tests. **Builds clean. No production path yet.**

3.5. **HARD GATE -- Empirically verify rotation read patterns BEFORE `ThrustField.Compute` is written.** Spawn fixture; for a representative block (one fixed-mount weapon block + one tilted hover block + one tilted-mount BoosterJet block), log all five of:
   - `block.cachedLocalRotation.rot * Vector3.forward` (the schema's `pose.LocalRotation` source)
   - `tank.rootBlockTrans.InverseTransformDirection(block.transform.forward)` (the production aggregator pattern)
   - `block.transform.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection))` (BoosterJet probe path)
   - `block.transform.InverseTransformDirection(jet.EffectorForward)` (FanJet probe path)
   - **Probe-vs-production frame equivalence assertion** (the actual correctness invariant for the BoosterImpulse path): on the tilted-mount booster fixture, compute `composed = pose.LocalRotation * archetype.Emitters[0].LocalAxis` from the probe output + per-instance pose; compute `direct = tank.rootBlockTrans.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection))` from the production aggregator pattern. **Assert `Vector3.Distance(composed, direct) < 1e-4f`.** This proves the probe's two-step (probe-to-block-local at archetype time, rotate-to-chassis at composition time) yields the same chassis-frame vector as the production aggregator's one-step (rootBlockTrans inversion).
   
   Confirm: (a) `cachedLocalRotation.rot` and chassis-frame `InverseTransformDirection` agree within float-eps for an axis-aligned block; (b) `WheelDriveAlignsWithChassisForward` (wheel emitter local +Z -> chassis +Z) holds; (c) the probe-vs-production frame-equivalence assertion above passes on a non-axis-aligned (tilted) booster. **Lock the `ArchetypeProbe` + `ThrustField.Compute` rotation read patterns based on this empirical comparison BEFORE Step 4.** If any pattern mis-matches, the spec adjusts the read pattern in the "Per-block thrust geometry" section above before any Compute code is written.

4. **Add `ThrustField.cs`** alongside `ThrustMap.cs` initially -- both compile, neither calls the other. New `ThrustField.Compute(BlockInstancePose[], ...)` signature uses the rotation patterns locked in Step 3.5. Build clean. **(Renaming + consumer migration is Step 8 to bound the build-broken window.)**

5. **PARALLEL-RUN GATE** -- `KickStart.NewVehiclePipeline` static bool, default `false`. Inside `RebuildVehicleSnapshotInternal`, when flag is true, run BOTH paths and compare:
   - `MaxLinearAccelPositive`/`Negative` magnitude (each axis: tolerance +/-10%)
   - `MaxAngularAccel.y` (tolerance +/-15%)
   - `VerticalAuthority` (tolerance +/-10% absolute, abs-delta < 0.3)
   - `TopSpeedForward` / `TopSpeedBackward` / `TopSpeedLateral` (tolerance +/-15%)
   - `TurningRadiusAtTopSpeed` (tolerance +/-20%)
   - `ClimbAngleMax` (tolerance +/-15%)
   - `Weapons.Count` (strict equality; placeholder values are bit-identical to today)
   - `BlockKindCounts` per-bit tally: **for each bit, NEW count >= OLD-equivalent role tally** (the right invariant -- captures the "multi-role insight" without claiming bit-set-superset against a single-role enum)
   - `Mass.TotalMass` (tolerance +/-1%; should be bit-identical)
   
   Log `[VEHICLE-PARITY]` discrepancies file-only. **Fixture set:** pure wheel tank, hover with side fans, wheel + vertical booster (today's already-correct case -- confirms no regression), **tilted hover pad tank** (the real risk case for `VerticalAuthority`), flat-mount booster aircraft, anchored base, gatherer with conveyor (no Drill bit in v0.1). Hold the swap until each delta is explained.

6. **Flip `KickStart.NewVehiclePipeline = true`**. `RebuildVehicleSnapshotInternal` now uses new path only. Old `BlockCapture` still compiles. **BUILD-BROKEN WINDOW: NONE.** Both paths coexist.

7. **In-game smoke test**: spawn fixture wave, confirm `[TIMING]` shows steady-state catalog hit rate >= 99% within ~60s of play. Confirm no identity-row flips on fixture techs. Spawn a side-mounted-booster tech, confirm the MPC commands honest lateral motion (via v0.1's incidental `BeliefState.MaxAccelerationEstimate.magnitude` increase, even though `LinearCapacityPositive.x` itself isn't yet consumed). Push HELD until user confirms.

8. **3-commit cleanup migration (honest build-broken windows):**
   - **Commit 8a** (~30-45 min build-broken window -- honest estimate for the 23-consumer rename ripple, 24 grep hits including 1 doc-comment in `LearningService`): Rename `ThrustMap` class -> `ThrustField` class. Re-type `VehicleModelSnapshot.Thrust` from `ThrustMap` to `ThrustField`. Update 3 ctor sites with `BlockKindCounts` argument (`VehicleModel.cs:176`, `SmartRuntime.cs:239`, `SmartTestSuite.cs:318`). Update `SmartTestSuite.cs` lines 168/180/313 `new ThrustMap(...)` -> `new ThrustField(...)`. **`MobilityProfile.Derive(...)` signature also changes** (`ThrustMap` -> `ThrustField` parameter type) -- additional caller updates. Verify the 23 runtime consumer files compile against the new type name (most reads are property accessors -- `.MaxLinearAccelPositive` etc. -- so the rename ripples through type-inferred locals; some consumer files may need a fully-qualified type name update if they had `ThrustMap` in an explicit cast or generic argument). Build green only at end of commit; transient red is expected. (REV 2's 15-min estimate was optimistic for the full rename ripple across 23 consumers + `MobilityProfile.Derive` -- REV 3 honestly states 30-45 min.)
   - **Commit 8b** (no build-broken window): Delete `BlockObservation`, `BlockCapture`, the file `ThrustMap.cs` (now empty after rename + role-switch deletion). Build green.
   - **Commit 8c** (no build-broken window): Delete dead references in audit notes, `WORLD-CONTRACT.md`, etc. Doc-only.

9. **Add optional identity enrichment** (separate commit): `SmartIdentityClassifier` reads `vehicle.BlockKindCounts.Anchor`, `.Conveyor`, `.Holder` to firm up Base/Gatherer detection. Strictly additive. Drill-based and Repair-based rows deferred to v0.2.

10. **v0.2 (separate PR, gated)**: (a) Real `WeaponSpec` reflected values from `FireData` + `WeaponRound`, INCLUDING `ForwardDirectionLocal` honest-geometry (REV 3 moves Decision-#2 into v0.2 -- v0.1 freezes today's lossy `tank.InverseTransformDirection(block.transform.forward)` formula in `WeaponProfileBuilder`); widen parallel-run gate to cover Threat/Aim cascade. (b) Drill/Repair probe targets identified (`ModuleHealBeam`? `BlockType` allowlist?), bits set. (c) `ArmorMap` HP migration to per-archetype `SpecHP` with `ModuleDamage` reads + per-instance `HpFraction`. (d) Rewire `PhysicsRollout` to read `LiftBudget` + `JetBudget` separately for hover-class techs. (e) `CapabilityFilters.FromMobility` switches to `PropulsionTopology` instead of `VerticalAuthority` thresholds. Each behind a config flag.

---

## Decisions requiring user approval

These are behavior deltas. The user should sign off before merge per the `organic-vs-bug design value` memory ("flag the tradeoff, don't auto-polish"):

1. **`VerticalAuthority` honest geometry (corrected example).** Today: `MaxLinearAccelPositive.y / 9.81`. After: same formula, but +Y reflects honest rotated-emitter geometry. The real risk surface is **tilted hover pads** -- a hover mounted at an angle now contributes only `cos(tilt)` to Y instead of full Y. (Axis-aligned vertical boosters already work correctly today via the `Jet -> b.LocalForward` branch -- no delta there.) Identity thresholds `HoverAsAircraftMinVerticalAuth = 1.0` and `AircraftCompositionMinVerticalAuth = 1.5` may need re-tuning for tilted-mount cases. **Parallel-run gate quantifies the delta before flip.**

2. **REV 3 update: `WeaponProfile.ForwardDirectionLocal` honest geometry moved to v0.2 with the rest of WeaponSpec real-values migration.** Today the per-weapon `ForwardDirectionLocal` is computed via `tank.InverseTransformDirection(block.transform.forward)` -- lossy on side-mounted weapons. REV 2 proposed switching to `pose.LocalRotation * archetype.Weapon.BarrelDirBlockLocal` in v0.1, but the REV 2 adversarial review noted that change would tighten `WeaponFireController.IsAimedWithinArc` cones without flowing through the parallel-run gate -- a combat-feel-altering change muddying #8's "pure refactor" framing. **REV 3 keeps the old lossy formula in v0.1** so `WeaponProfileBuilder` produces bit-identical `ForwardDirectionLocal` values to today. v0.2 swaps to the geometry-honest formula alongside the real `WeaponSpec` field values, with its own parallel-run gate covering arc-deltas. Zero v0.1 behavior delta from this read.

3. **Wheel `WheelRoll` chassis +Z substitution preserved.** A per-EmitterKind axis substitution isolated to `EmitterKind.WheelRoll`, documented as deliberate. Not a role override -- it's a per-emitter-kind choice. User should know it exists and that v0.2 may replace with a first-principles wheel-roll model.

4. **`BeliefState.MaxAccelerationEstimate` value growth.** `SmartForm.cs:274-277` computes `Mathf.Max(thrust.MaxLinearAccelPositive.magnitude, ...Negative.magnitude)`. With honest multi-axis budgets, magnitudes grow for techs whose previously-collapsed lateral capacity now contributes. Aether Kalman Q-bounds downstream see larger values. Likely benign (estimate upward bound was conservative); flagged anyway.

5. **Wheel-yaw clamped-max-selector preserved (not raw torque).** Per critique consensus, the existing wheel-yaw heuristic is load-bearing for "feel" even if physically inaccurate. v0.1 keeps it via `Min(3f, Max(real_torque_yaw, existing_heuristic_yaw))` -- the max of real and heuristic, clamped at 3 rad/s^2. Alternative: ship raw torque only, accept the handling regression, expose tunable -- NOT chosen for v0.1.

6. **REV 3 corrected: `VehicleModelSnapshot` ctor gains `BlockKindCounts` parameter.** The one strict break in the "preserved contract" claim. **3 direct ctor-site updates** (mechanical) + 3 `.Empty()` propagating sites (transparent). **Honest count: 23 direct consumers of `VehicleModelSnapshot`** (REV 2's 24 included `Learning/LearningService.cs:222-225`, which is only a `/// summary` doc-comment mention, not a runtime read -- grep showed up but it's a false consumer). It's "22 of 23 consumers see no source change; 1 ctor signature grows by one param, 3 direct call sites updated."

7. **`Walker` / `Drill` / `Repair` enum bits reserved but unreachable in v0.1.** `ModuleWalker` absent in this TerraTech version; `ModuleHammer` (REV 1's claimed Drill+Repair source) has zero references in the codebase, so no verified probe target. All three bits are reserved bit slots; no probe sets them; classifier rules that depend on them are deferred. Identifying real probe targets for Drill (drill-block allowlist or `ModuleHealBeam`?) and Repair is a v0.2 follow-up.

8. **NEW: `WeaponProfile` field values stay at today's placeholders in v0.1.** Today (`WeaponProfileBuilder.cs:84-103`) every `WeaponProfile` is hardcoded: `Range=100`, `ProjVel=200`, `Damage=30`, `FireRateHz=1.5`, fixed-weapon arcs `pi/4` yaw + `pi/6` pitch, turret arcs `pi` + `pi/2`, `AmmoCap=100`, `AmmoCur=100`, `Cooldown=0`, `IsEnergy=false`. Chassis v0.1 **preserves these placeholder values** even though the architecture supports real reflected reads. Reason: swapping in real per-block values silently cascades into:
   - `TargetAssignment` priority recompute (range-weighted)
   - `ThreatField` splat radii (range-weighted)
   - `PlanDecomposition` engagement-range goals
   - `WeaponFireController.IsAimedWithinArc` arc widths
   - Goal-source `Weapons[i].Range` reads (6 identity files)
   
   That's a combat-feel-altering change that should ride its own parallel-run gate and explicit user approval. v0.2 ships the real values with its own gate. **v0.1 ships Chassis as pure refactor: geometry honest, scalar values frozen.**

9. **NEW: `BlockKindCounts` parity-tolerance is per-flag tally, not "strict superset."** REV 1 said the new `BlockKindCounts` must be "strict-superset of authored-purposes" -- that's a type mismatch (purposes are a `BasePurpose` enum, not per-flag counts). REV 2 correctly states: for each `BlockKindFlags` bit, the new tally must be **greater than or equal to the equivalent old `BlockRole` count** (because multi-role blocks now contribute to multiple bits where the old single-role enum could only pick one). This captures the multi-role insight without false-equating it to a `BasePurpose` superset.

10. **NEW: `ArmorMap` HP source stays at mass-as-HP placeholder in v0.1.** REV 1 referenced `archetype.SpecHP` and `pose.HpFraction` that were never declared in the schema. REV 2 cuts these from the schema and defers the HP migration to v0.2. `ArmorMap.Compute` signature changes (param type from `BlockObservation[]` to `BlockInstancePose[]`), but the HP-derivation math is unchanged. Zero behavior delta on armor.

11. **REV 3 NEW: `BlockKindCounts` is a `readonly struct`, not a class.** Unanimous REV 2-review recommendation. Matches sibling shapes (`BlockInstancePose`, `WeaponSpec`, `ThrustEmitter` are all `readonly struct`s), avoids per-rebuild heap allocation, and keeps the `VehicleModelSnapshot` constructor's value-semantics consistent. Layout: 18 `int` counts (one per `BlockKindFlags` bit) = 72 B; passed by value through the ctor and read by value at consumer call sites.

12. **REV 3 NEW: `EmitterKind` enum: `FanJet` and `BoosterImpulse` are the only ModuleBooster-child kinds in v0.1.** REV 2's enum listed `JetExhaust` as a separate value but no probe path produced it (would be a dead enum value confusing implementers). REV 3 removes `JetExhaust`. The `EmitterKind` enum in v0.1: `WheelRoll`, `FanJet` (was `FanLift` in REV 2 -- REV 3 renamed to match the production module child name verified at `AIControllerAir.cs:151-198`), `BoosterImpulse`, `HoverPad`, `WingLift`. `Walker` reserved-unreachable per Decision #7.

13. **REV 3 NEW: Budget-vector axis convention.** `LiftBudget`, `JetBudget`, `GroundTractionBudget`, `AeroLiftBudget` are all `Vector3` -- `.x` / `.y` / `.z` are chassis-frame axes (same convention as `MaxLinearAccelPositive`). For each budget, the `.y` component is the dominant axis (Y is up in TerraTech world; chassis-Y aligns with world-Y for upright chassis), with `.x` and `.z` capturing off-axis contributions from tilted mounts. `LiftBudget.y` is the sum of vertical lift from hover pads + wings + upward-mounted boosters; `JetBudget.z` is the sum of forward push from boosters + fan-jets in the forward orientation. `AeroLiftBudget` is flag-gated (only emitters with `EmitterFlags.AerodynamicScaling` contribute -- typically `WingLift`). **Diagonal-mount aggregation (REV 3.1 clarification):** a tilted emitter contributes to all three budget components in proportion to the components of `pose.LocalRotation * emitter.LocalAxis * MaxForceN`. Concretely: a 45°-tilted forward-mounted booster contributes `MaxForceN/sqrt(2)` to BOTH `JetBudget.x` and `JetBudget.z` (the diagonal split). Aggregation buckets: `JetBudget` sums all `FanJet` + `BoosterImpulse` emitter vectors; `LiftBudget` sums all `HoverPad` + `WingLift`; `GroundTractionBudget` sums all `WheelRoll` (after the per-EmitterKind chassis-+Z substitution); `AeroLiftBudget` sums only emitters with `EmitterFlags.AerodynamicScaling`. v0.1 computes all four; v0.2 wires `PhysicsRollout` to read them separately for hover-class techs.

14. **REV 3 NEW: New files live flat under `Vehicle/`** (no `Catalog/` or `Capture/` subfolders). Matches existing `Vehicle/` layout (every existing file is direct under `Vehicle/`, no subfolders). Avoids a layout reshuffle that the old-style csproj `<Compile Include="...">` registration would need to track separately. Step 1's "subfolder" mention was a draft leftover.

---

## Open questions

- **Catalog Prewarm at form Init: synchronous (blocks load by ~100-500ms) or deferred to first idle frame?** Synchronous is simpler and the load-screen window already exists; defer the question to in-game profiling.

- **`WeaponDynamicState` (per-rebuild ammo/cooldown read) GC impact under heavy combat.** Not relevant in v0.1 (placeholder values, no GetComponent calls). Becomes relevant in v0.2 when real values land.

- **Mod hot-reload catalog invalidation path.** No hook today. Document as limitation; expose `BlockCatalog.Reset()` for mod-manager scripts that want to call it manually.

- **`MaxAngularAccel.z` (roll axis) mapping under honest geometry.** `AUDIT.md:215` notes today's code maps yaw to `.y` -- that's preserved. Roll-axis aggregation under the new vector pipeline is well-defined but unconsumed in v0.1. v0.2 follow-up if any consumer wants real roll budgets.

- **`ConcurrentDictionary` vs `Dictionary + lock` for the catalog.** Single-writer + many-reader pattern could use a regular Dictionary with `Volatile.Read` of the reference (lock-free reads, lock on write). ConcurrentDictionary is slightly over-engineered. Defer the choice; profile if it matters. Listed for record-keeping; not blocking.
