# VEHICLE-CONTRACT.md

**Subsystem:** Vehicle/
**Form:** Smart
**Version:** 0.1.0
**Status:** AUTHORITATIVE — Defines Smart's per-tech physical model: mass distribution, thrust map, weapon profile table, armor map, kinematic tracker, mobility profile, and the rebuild-on-block-change protocol.

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The `VehicleModel` data structure: every field that describes a tech's physical state.
- The rebuild trigger and protocol when blocks attach or detach.
- The mass distribution and center-of-mass computation.
- The thrust map: propulsion-block enumeration and per-direction control authority.
- The weapon profile table: per-weapon position, arc, range, projectile velocity, damage, fire rate, ammo, cooldown.
- The armor / vulnerability map: voxelized representation and face-direction query semantics.
- The kinematic tracker: position / velocity / acceleration / jerk estimation from main-thread observations.
- The mobility profile: derived top speed, turning radius, climb angle, water capability, vertical authority.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals and AI-collaborator directives.
- [ARCHITECTURE.md](ARCHITECTURE.md) for threading, lifecycle, and cross-subsystem invariants.
- [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md) for block-attached/detaching event signatures and `Tank.blockman` access patterns.
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for `DoubleBuffer<T>`, `WorkerPool`, marshalling patterns.

**This document GOVERNS:**
- Consumers reading `VehicleModel` snapshots — [WORLD-CONTRACT.md](WORLD-CONTRACT.md) (uses kinematic tracker + max-acceleration estimate for belief decay), and later Planning, Control, Pathing.

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What this contract owns

Vehicle/ owns the per-tech physical model. One `VehicleModel` exists per visible tech (friendly or hostile); it is rebuilt when blocks change and continuously updated by the kinematic tracker.

### 1.2 Decisions resolved at v0.1.0

[NORMATIVE]
- **Rebuild trigger:** **debounced** — block events mark the tech's model dirty; rebuild runs once at the next perception worker tick. Resolved at the Layer 4 Vehicle Q&A round.
- **Armor map representation:** **voxelized grid** with resolution adaptive to tech extents. Resolved at the Layer 4 Vehicle Q&A round.
- **Storage location:** per-tech `VehicleModel` snapshot lives in a `DoubleBuffer<VehicleModelSnapshot>` published by the rebuild worker; the `helper.FormState` slot (per [SHELL-API-GUIDE.md §6](SHELL-API-GUIDE.md#section-6-tankaihelper-field-surface)) holds Smart's per-tech state object, which contains a reference to the buffer.
- **Mass and COM computation:** aggregate over the block list, NOT read from `Tank.rbody.mass` or `Tank.rbody.centerOfMass`. The block-list aggregate is more accurate immediately after block events (TerraTech's rigidbody mass lags by one physics step); the cost is one block iteration per rebuild, which is amortized by debouncing.

---

## SECTION 2: THE VEHICLEMODEL SNAPSHOT

[NORMATIVE] `VehicleModel` is a snapshot type — immutable, publishable via `DoubleBuffer<T>` per [THREADING-CONTRACT.md §4](THREADING-CONTRACT.md#section-4-double-buffer-primitive). Schematic shape:

```
public sealed class VehicleModelSnapshot
{
    public readonly TechId Id;
    public readonly long ConstructedAtTick;       // monotonic; helps consumers detect updates
    public readonly MassDistribution Mass;
    public readonly ThrustMap Thrust;
    public readonly IReadOnlyList<WeaponProfile> Weapons;
    public readonly ArmorMap Armor;
    public readonly KinematicState Kinematics;    // updated more frequently than the rest
    public readonly MobilityProfile Mobility;     // derived from Mass + Thrust + Armor
}
```

[NORMATIVE] All fields except `Kinematics` are stable across ticks while no block events occur. `Kinematics` is updated each perception tick (continuous main-thread observation).

[RATIONALE] Splitting `Kinematics` from the rest is a memory-economy choice: a per-tick rebuild of the entire snapshot would copy the (large) `Armor` voxel grid every tick for techs whose blocks haven't changed. The contract permits the implementation to publish a partial snapshot (kinematics-only) more often than the full snapshot; the consumer reads whichever is current. Concrete optimization decision is deferred to implementation; the contract requires the kinematics tracker to be readable per-tick.

---

## SECTION 3: MASS DISTRIBUTION

### 3.1 What it computes

[NORMATIVE] `MassDistribution` for a tech:
- Total mass (sum of per-block mass).
- Center of mass (mass-weighted average position of every block, in tech-local coordinates).
- Inertia tensor (3×3 symmetric matrix; mass-distributed moment of inertia in tech-local coords).

[NORMATIVE] Inertia is included even though TerraTech's rigidbody exposes its own inertia: Smart's planner reasons about *future* configurations under hypothetical block-loss scenarios that the rigidbody can't model.

### 3.2 Computation

[NORMATIVE] Iterate `Tank.blockman` block list (on the main thread, during the rebuild trigger). For each block:
- Read its mass from the block's data or static type lookup.
- Read its position in tech-local coordinates.
- Accumulate to running sums.

After iteration, compute the COM and normalize the inertia tensor.

[NORMATIVE] The iteration is on the main thread inside a rebuild closure scheduled by the dirty-tracker (see §9). The closure copies primitive values out of `TankBlock` instances and queues the heavy math to a worker; the worker computes the final `MassDistribution` and publishes via the buffer.

### 3.3 API sketch

```
public readonly struct MassDistribution
{
    public readonly float TotalMass;
    public readonly Vector3 CenterOfMassLocal;
    public readonly Matrix3x3 InertiaTensorLocal;
}
```

---

## SECTION 4: THRUST MAP

### 4.1 What it computes

[NORMATIVE] `ThrustMap` summarizes the tech's control authority — what forces and torques it can apply in each direction.

For each propulsion block (booster, jet, wheel, hover, prop), record:
- Mount position (tech-local).
- Mount orientation (thrust direction in tech-local).
- Maximum thrust magnitude.
- Type (so Control can model differential dynamics — wheels respond differently to brakes than jets do).
- Activation state if known (jets that can be toggled vs always-on).

Aggregate to:
- Max linear acceleration available in each cardinal direction (6 values: +X, -X, +Y, -Y, +Z, -Z, tech-local).
- Max angular acceleration available in each axis (3 values).

### 4.2 Why aggregate AND per-block

[RATIONALE] The aggregate is what Planning reads ("can this tech accelerate at 8 m/s² forward?"). The per-block list is what Control reads (the MPC needs to know which actuators it's commanding to achieve the desired motion). Both serializations exist because both consumers exist.

### 4.3 API sketch

```
public sealed class ThrustMap
{
    public readonly IReadOnlyList<PropulsionBlock> Blocks;
    public readonly Vector3 MaxLinearAccelPositive;   // X, Y, Z
    public readonly Vector3 MaxLinearAccelNegative;   // X, Y, Z (magnitudes)
    public readonly Vector3 MaxAngularAccel;          // pitch, yaw, roll
}

public readonly struct PropulsionBlock
{
    public readonly Vector3 MountPositionLocal;
    public readonly Vector3 ThrustDirectionLocal;
    public readonly float MaxThrust;
    public readonly PropulsionType Type;             // Wheel, Jet, Hover, Prop, etc.
}
```

---

## SECTION 5: WEAPON PROFILE TABLE

> **REV 7 (v0.2):** §5 is updated to reflect the Chassis honest-geometry pipeline + the P1 `WeaponSpecPolicy.UseReflectedScalars` gate. Pre-v0.2 §5.1/§5.2/§5.3 narrative is replaced; semantics are preserved.

### 5.1 What it computes — separated static spec vs dynamic profile

[NORMATIVE] The Chassis pipeline (P1 / `Vehicle/ChassisCapture.cs`) splits the weapon model into two concerns:

1. **`WeaponSpec` (static, per-block-type)** — captured once at archetype probe time (`Vehicle/ArchetypeProbe.cs`) and cached in `Vehicle/TypedBlockCatalog`:
   - `EmitterKind` (see §11) — Fixed / Turreted / Drone / Missile / Beam / etc.
   - Mount position + forward direction (block-local).
   - Yaw + pitch arc (radians) — turret rotation envelope.
   - Range, projectile velocity, damage-per-projectile, fire rate.
   - Per-`EmitterKind` multipliers (`Vehicle/EmitterKindMultipliers.cs`) — pure switch; defaults all `1.0f` (v0.1 bit-identical).
2. **`WeaponProfile` (per-tech-instance, dynamic)** — built by `Vehicle/WeaponProfileBuilder.cs` at vehicle-rebuild time, one per equipped weapon block, joining the static `WeaponSpec` with this instance's:
   - World-space mount transform (from `BlockInstancePose`).
   - Ammo state, cooldown remaining, current charge level.

[NORMATIVE] Static data flows through the catalog; dynamic data flows through the per-tech rebuild. Planning reads `VehicleModelSnapshot.Weapons` (List<WeaponProfile>) — a coherent view of both.

### 5.2 The `UseReflectedScalars` parity gate

[NORMATIVE] **P1 Item:** `Vehicle/WeaponSpecPolicy.cs` carries a `UseReflectedScalars` boolean (default **OFF**). When OFF, `WeaponSpec` values come from the v0.1 reflection-and-fallback path; bit-identical to v0.1 behavior. When ON, values are read from the live `Module*` instances via `Vehicle/WeaponReflectionCache.cs`.

[NORMATIVE] The flip is gated by `WeaponSpecParityGate` (`Vehicle/WeaponSpecParityGate.cs`): the gate emits one `[WEAPON-PARITY]` log line per archetype-key on first observation, tagged `[PARITY-DRIFT]` when the new value diverges from the old beyond tolerance. This is **diagnostic only** — the live spec is whichever side the policy gate selects.

### 5.3 API sketch (v0.2)

```csharp
public readonly struct WeaponSpec    // static, in TypedBlockCatalog; one per block type
{
    public readonly EmitterKind Kind;
    public readonly Vector3     MountPositionLocal;
    public readonly Vector3     ForwardDirectionLocal;
    public readonly float       YawArcRadians;
    public readonly float       PitchArcRadians;
    public readonly float       Range;
    public readonly float       ProjectileVelocity;
    public readonly float       DamagePerProjectile;
    public readonly float       FireRateHz;

    public static WeaponSpec PlaceholderFixed(Vector3 forwardLocal);   // safe default
}

public readonly struct WeaponProfile   // dynamic, in VehicleModelSnapshot; one per equipped weapon
{
    public readonly WeaponId    Id;
    public readonly WeaponSpec  Spec;                   // join to static catalog
    public readonly Vector3     MountPositionWorld;     // from BlockInstancePose
    public readonly Vector3     ForwardDirectionWorld;
    public readonly int         AmmoCurrent;
    public readonly int         AmmoCapacity;
    public readonly float       CooldownRemaining;
}
```

---

## SECTION 6: ARMOR MAP (VOXELIZED GRID)

> **REV 7 (v0.2):** §6 is updated for P3 `ArmorMapPolicy.UseRealSpecHP` + `ArmorMapParityGate`. The voxel-grid shape and query semantics are preserved.

### 6.1 Structure

[NORMATIVE] `ArmorMap` (`Vehicle/ArmorMap.cs`) is a 3D voxel grid covering the tech's bounding volume. v0.2 default resolution is **8×8×8** (`new Vector3Int(8, 8, 8)`), set at `SmartPerTechState.RebuildVehicleSnapshotInternal`.

- Per-voxel data: aggregate hit-points + density (fraction of cell filled).
- Coordinate origin: tech-local, axis-aligned to tech axes (NOT world axes).

### 6.2 The `UseRealSpecHP` policy gate

[NORMATIVE] **P3 Item 5:** `Vehicle/ArmorMapPolicy.cs` carries a `UseRealSpecHP` boolean (default **OFF**). The compute call branches on it:

```csharp
var armor = Vehicle.ArmorMapPolicy.UseRealSpecHP
    ? ArmorMap.Compute(poses, SmartRuntime.BlockCatalog, new Vector3Int(8, 8, 8))   // v0.2 path
    : ArmorMap.Compute(poses, new Vector3Int(8, 8, 8));                              // v0.1 fallback
```

| Policy | HP source per voxel |
|---|---|
| **OFF (default)** | block mass aggregated into the voxel — the v0.1 "mass-as-HP" approximation; face-weakness ranking is bit-identical to v0.1 |
| **ON** | catalog-backed `ModuleDamage.maxHealth × pose.HpFraction` per block, aggregated into the voxel — physically grounded HP that survives armor-fraction reductions |

[NORMATIVE] **`ArmorMapParityGate`** (`Vehicle/ArmorMapParityGate.cs`) compares total HP across both grids when the policy is flipped, with `DriftTolerance = 0.25` (25% of total HP). One log line per tech-id; `[PARITY-DRIFT]` tag when total-HP delta exceeds tolerance.

### 6.3 Face-direction queries — unchanged from v0.1

[NORMATIVE] Primary query: "given an attack direction `d` (tech-local), what's the weak face?"
1. Compute the cardinal/intercardinal direction nearest to `-d` (the side facing the attack).
2. Walk the slab of voxels on that side; sum hit-points; find the minimum-HP face.
3. Return: face index, total HP of that face, weakest sub-region within the face.

[NORMATIVE] Queries are constant-time (small grid; no raycasts at query time). The voxel grid trades a few KB per tech and a one-shot rebuild cost for many cheap face queries during planning — far better-amortized than per-query raycasts.

### 6.4 API sketch

```csharp
public sealed class ArmorMap
{
    public readonly Vector3Int GridResolution;
    public readonly Bounds LocalBounds;

    public ArmorQueryResult QueryWeakFace(Vector3 attackDirectionLocal);
    public ArmorQueryResult QueryRegion(Bounds regionLocal);

    public static ArmorMap Compute(BlockInstancePose[] poses, Vector3Int gridResolution);
    public static ArmorMap Compute(BlockInstancePose[] poses, TypedBlockCatalog catalog, Vector3Int gridResolution);  // v0.2 (real HP)
}

public readonly struct ArmorQueryResult
{
    public readonly Vector3 FaceCenterLocal;
    public readonly Vector3 FaceNormalLocal;
    public readonly float TotalHP;
    public readonly Vector3 WeakestPointLocal;
}
```

---

## SECTION 7: KINEMATIC TRACKER

> **REV 7 (v0.2):** §7 is updated to reflect that smoothing landed as the EWMA design but is **not pre-filtering rbody velocity reads** — Aether's D2 decision keeps raw rbody.velocity as the trace anchor (see [WORLD-CONTRACT.md §3.2](WORLD-CONTRACT.md#section-3-aether-fusion-replaces-kalman-update)).

### 7.1 What it tracks

[NORMATIVE] Per-tech estimator for instantaneous physical state. The fields surfaced via `KinematicState` are unchanged from v0.1:
- Position (world-space).
- Linear velocity (world-space).
- Linear acceleration (world-space; estimated from velocity history).
- Angular velocity (world-space).
- Jerk (rate-of-change of acceleration; estimated from acceleration history).
- Heading (forward vector, world-space).

### 7.2 Update cadence

[NORMATIVE] The kinematic tracker (`Vehicle/KinematicTracker.cs`) runs on the **main thread** during `SmartPerTechState.TickMainThread`, driven by `SmartForm.Operations` at the engine's fixed-update rate (~33 ms target — see [WORLD-CONTRACT.md §3.1](WORLD-CONTRACT.md#section-3-aether-fusion-replaces-kalman-update)). Per tick:
1. Read `Tank.boundsCentreWorldNoCheck`, `Tank.rbody.velocity`, `Tank.rbody.angularVelocity`, `Tank.rootBlockTrans.forward`.
2. Update internal history buffer (last N samples).
3. Estimate acceleration as `(velocity_now − velocity_prev) / dt`.
4. Estimate jerk similarly.
5. Publish the new `KinematicState` via `SmartPerTechState.KinematicBuffer.Write`.

[NORMATIVE] All five steps execute on the main thread because they read engine objects. The tracker MUST complete in O(1) regardless of how many techs are tracked.

### 7.3 Smoothing — derived fields only

[NORMATIVE] **Smoothing applies to derived fields (acceleration, jerk) only.** Velocity is NOT pre-filtered — `LastSeenVelocity` in the Aether trace is the raw `rbody.velocity` read at observation time. This is the Aether D2 decision: explicit, no incidental smoothing from a Kalman gain. Single-frame collision spikes are bounded by `MaxAccelerationEstimate × dt` and visible to consumers.

[NORMATIVE] Acceleration and jerk MUST be smoothed (the 4-sample EWMA pattern v0.1 prescribed). Smoothing parameter remains provisional; tune during self-play.

### 7.4 API sketch

```csharp
public readonly struct KinematicState
{
    public readonly Vector3 PositionWorld;
    public readonly Vector3 VelocityWorld;
    public readonly Vector3 AccelerationWorld;
    public readonly Vector3 AngularVelocityWorld;
    public readonly Vector3 JerkWorld;
    public readonly Vector3 HeadingWorld;
    public readonly long TickStamp;

    public static KinematicState Zero { get; }
}
```

---

## SECTION 8: MOBILITY PROFILE (DERIVED)

[NORMATIVE] `MobilityProfile` is derived from `Mass`, `Thrust`, and `Armor` (mass + thrust → acceleration; armor + thrust on dorsal blocks → tipping susceptibility).

Computed at rebuild time, alongside the other components:
- Top speed (forward, backward, lateral) — from thrust map and drag estimates.
- Turning radius at top speed — from angular acceleration and forward velocity.
- Climb angle (max slope the tech can ascend) — from wheel/leg coupling to terrain (heuristic if no contact model).
- Water capability — from waterproof block fraction and buoyancy.
- Vertical authority — from upward thrust available vs gravity.
- Tipping susceptibility — from COM height above support polygon.

[NORMATIVE] `MobilityProfile` is read-only by consumers; never written outside the rebuild closure.

```
public readonly struct MobilityProfile
{
    public readonly float TopSpeedForward;
    public readonly float TopSpeedBackward;
    public readonly float TopSpeedLateral;
    public readonly float TurningRadiusAtTopSpeed;
    public readonly float ClimbAngleMax;
    public readonly bool WaterCapable;
    public readonly float VerticalAuthority;        // negative if can't hover
    public readonly float TippingSusceptibility;   // 0 = stable, 1 = topples easily
}
```

---

## SECTION 9: REBUILD PROTOCOL

### 9.1 Dirty tracking

[NORMATIVE] Each per-tech state object (in `helper.FormState`) holds a `dirty` flag and a `lastDirtiedAt` timestamp.

[NORMATIVE] Block-attach/detach events (subscribed via World's event bus — see [WORLD-CONTRACT.md §5](WORLD-CONTRACT.md#section-5-event-bus)) set `dirty = true` and update `lastDirtiedAt`.

### 9.2 Debounce

[NORMATIVE] On each perception worker tick (~30 Hz target; per WORLD-CONTRACT §6), if `dirty == true`, schedule a rebuild:
1. Capture the block-list snapshot on the main thread (iterate `Tank.blockman`; copy block positions, types, masses, weapon parameters into a transient struct).
2. Enqueue a worker task with the captured snapshot.
3. Clear the dirty flag.

The worker computes the new `MassDistribution`, `ThrustMap`, `Weapons`, `ArmorMap`, `MobilityProfile`, and publishes a new `VehicleModelSnapshot` via the per-tech `DoubleBuffer`.

[NORMATIVE] Only one in-flight rebuild per tech at a time. If a second block event fires while a rebuild is running, the dirty flag is re-set; the next tick coalesces. The in-flight rebuild's output is published normally; the next rebuild will reflect the newer state.

### 9.3 Kinematic update separate path

[NORMATIVE] The kinematic tracker (§7) runs every tick on the main thread, regardless of rebuild status. It reads from a separate `DoubleBuffer<KinematicState>` per tech, so consumers reading `VehicleModelSnapshot.Kinematics` get the latest kinematic estimate even when a full rebuild hasn't run for several ticks.

[RATIONALE] Kinematics (position/velocity) is high-frequency; rebuilds (mass/armor/weapons) are low-frequency. Treating them with the same cadence wastes work or starves the planner.

### 9.4 Initial snapshot

[NORMATIVE] When a tech first becomes visible (`TechSpawned` event from World's event bus), Smart constructs an initial `VehicleModelSnapshot` synchronously on the main thread, publishes it via the buffer, and only then enqueues the first kinematic update tick. Consumers that read the snapshot before the first rebuild see a valid initial state; they never see `null`.

---

## SECTION 10: HOW TO WRITE A NEW GOALSOURCE

> **REV 7 (v0.2):** Author-facing guide added per P10 Item 43. GoalSources are the per-identity "what does this tech want to do this tick" producers consumed by `ContinuousController.OnOperationsTick`. The 9-source registry is in `Identity/`; adding a 10th follows this template.

### 10.1 Anatomy of an `ISmartGoalSource`

[NORMATIVE] An `ISmartGoalSource` implementation is a stateless singleton that maps `IdentityContext + BeliefSnapshot + own VehicleModelSnapshot → TacticalGoal`. Contract: `Identity/SmartIdentity.cs:73-78`.

```csharp
public sealed class MyNewGoalSource : ISmartGoalSource
{
    public SmartIdentity Identity => SmartIdentity.MyNewKind;

    public TacticalGoal Produce(in IdentityContext ctx,
                                BeliefSnapshot beliefs,
                                VehicleModelSnapshot vehicle)
    {
        // 1. Pull facts: ctx.TechId, ctx.TeamCentroid (gate on ctx.HasAllies), ctx.Stamp.IsAuthored.
        // 2. Scan beliefs.ByTech for the most relevant tech (hostile/ally/objective per role).
        // 3. Build a goal: TacticalGoal.AtPosition(target, urgency) or TacticalGoal.AtCurrent(...).
        // 4. Return — caller (ContinuousController) writes the goal into the tech's external-goal buffer.
        if (TryFindHostile(beliefs, ctx.TechId, out var hostilePos))
            return TacticalGoal.AtPosition(hostilePos, urgency: 1.0f);
        return TacticalGoal.AtCurrent(vehicle.Kinematics.PositionWorld, urgency: 0f);
    }
}
```

### 10.2 Registration

[NORMATIVE] Register the singleton in `SmartRuntime.Init` alongside the existing 9 entries:

```csharp
SmartIdentityRegistry.Register(new MyNewGoalSource());
```

[NORMATIVE] The classifier (`Identity/SmartIdentityClassifier.cs`) MUST be able to produce `SmartIdentity.MyNewKind` for at least one composition rule, or the new source will be registered but never selected. Add a classifier branch; gate it behind a default-OFF `SmartIdentityTuning` flag if the rule is experimental.

### 10.3 csproj registration

[NORMATIVE] Old-style csproj — every new `.cs` file needs an explicit entry:

```xml
<Compile Include="AI\Forms\Smart\Identity\MyNewGoalSource.cs" />
```

### 10.4 Worker safety

[NORMATIVE] `Produce` is called from `ContinuousController.OnOperationsTick` on the **main thread**. It is safe to read engine objects through `vehicle.Kinematics`. It is NOT safe to subscribe to worker-side events from inside `Produce`; sidecars (e.g., `HealthSidecar`, `DamageHintBuffer`) are the supported cross-thread surface and are populated by main-thread observers (`SmartForm.ObserveWorldTechsIfDue`).

[NORMATIVE] The singleton MUST be reentrant — multiple techs of the same identity hit `Produce` per tick. Hold no per-tech state in the source; use `ctx.TechId` to key into sidecars if persistent state is needed.

### 10.5 Bit-identical preservation pattern

[NORMATIVE] When the new source is meant as a **refinement** of existing behavior (not new behavior), gate it default-OFF behind a `SmartIdentityTuning` flag and have the classifier route the affected composition to the new identity only when the flag is true. This preserves v0.1 behavior under default tunings and lets a spawn-test campaign flip the gate without re-deploying.

[NORMATIVE] Reference implementations to study before authoring a 10th source:
- **`HunterGoalSource`** — minimal closing-on-hostile template.
- **`GathererGoalSource`** — deterministic Lissajous + Conveyor/Holder composition gating.
- **`PatrolGoalSource`** — TechId-seeded meander + Produce-time hostile scan.
- **`GenericGoalSource`** — `TacticalGoalHandle` indirection so per-instance Adam state in `TacticalOptimizer` is preserved (the v0.1 inline-bypass behavior is call-equivalent).

---

## SECTION 11: HOW TO ADD A NEW EMITTERKIND

> **REV 7 (v0.2):** Author-facing guide added per P10 Item 43. EmitterKinds (`Vehicle/EmitterKind.cs`) classify thrust-producing block subtypes so `ThrustField` aggregation can apply per-kind multipliers (`Vehicle/EmitterKindMultipliers.cs`). The enum is **closed for v0.2** by design — adding a new kind is a v0.3 procedure.

### 11.1 When to add a new EmitterKind

[INFORMATIVE] Add a new `EmitterKind` only when the engine introduces a thrust block that:
1. Produces meaningfully different force characteristics from existing kinds (e.g., a directional spool-up effect that none of `WheelDrive` / `BoosterStraight` / `HoverPad` / `JetForward` model), AND
2. Has at least one downstream consumer that wants to bias against it (multiplier ≠ 1.0).

[INFORMATIVE] Adding a kind whose multiplier stays 1.0 forever adds enum churn without behavior change. Prefer extending an existing kind's probe rules.

### 11.2 Four-site checklist

[NORMATIVE] Adding a new `EmitterKind` requires edits at **four** sites (per `V0.2-PLAN.md §769`):

1. **`Vehicle/EmitterKind.cs`** — add the enum entry. Append; do not reorder (multiplier table is index-keyed in spirit even though the switch is name-keyed — keep entries stable for grep continuity).

   ```csharp
   public enum EmitterKind
   {
       Unknown = 0,
       WheelDrive,
       BoosterStraight,
       HoverPad,
       JetForward,
       JetReverse,
       // ...
       MyNewKind,   // append
   }
   ```

2. **`Vehicle/ArchetypeProbe.cs`** — add a branch that detects the new kind during prefab probing. Use the same reflection + name-pattern pattern the existing branches follow; fall through to `Unknown` on miss.

   ```csharp
   else if (IsMyNewKindBlock(prefab, modules))
       em.Kind = EmitterKind.MyNewKind;
   ```

3. **`Vehicle/ThrustField.cs::Compute`** — the per-block aggregation loop reads `EmitterKindMultipliers.For(em.Kind)` and multiplies it into `em.MaxForceN` and `em.ReverseForceN` immediately before bucket aggregation. **No code change needed at this site** if the multiplier table covers the new kind (next step).

4. **`Vehicle/EmitterKindMultipliers.cs`** — add a `case` to the switch. Default the multiplier to **1.0f** unless there's a downstream behavior reason to bias.

   ```csharp
   public static float For(EmitterKind k)
   {
       switch (k)
       {
           case EmitterKind.WheelDrive:    return 1.0f;
           // ...
           case EmitterKind.MyNewKind:     return 1.0f;   // bit-identical to v0.1 default
           default:                        return 1.0f;
       }
   }
   ```

### 11.3 csproj registration

[NORMATIVE] No new `.cs` files are introduced for an `EmitterKind` add — all four edits are to existing files. No csproj entry needed.

### 11.4 Spawn-test gate

[NORMATIVE] Even when the multiplier defaults to 1.0f (bit-identical), spawn-test the affected tech compositions: the `ArchetypeProbe` branch is a new code path and probe misclassification would re-route blocks between kinds in `ThrustField.Compute`'s aggregation. Verify via `WeaponSpecParityGate` analog or by enabling `CatalogPrewarm` and grepping the probe output.

[NORMATIVE] When the multiplier is set ≠ 1.0f (behavior-shifting), gate the kind behind a default-OFF flag in `SmartIdentityTuning` or equivalent — the same default-OFF discipline P1-P9 used. Flip it during a spawn-test campaign; revert via `smart.preset.load` if regressions show up.

---

## SECTION 12: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/Vehicle/` contains seven files:

| File | Owns |
|---|---|
| `VehicleModel.cs` | `VehicleModelSnapshot` aggregate + per-tech double-buffer registration. |
| `MassDistribution.cs` | `MassDistribution` struct + block-list aggregation math. |
| `ThrustMap.cs` | `ThrustMap`, `PropulsionBlock`, `PropulsionType` enum, per-direction aggregation. |
| `WeaponProfile.cs` | `WeaponProfile` struct + per-weapon-block extraction + cooldown/ammo tick update. |
| `ArmorMap.cs` | `ArmorMap` with voxel grid + `QueryWeakFace`/`QueryRegion` implementations. |
| `KinematicTracker.cs` | `KinematicState`, history buffer, EWMA smoothing, per-tick update. |
| `MobilityProfile.cs` | `MobilityProfile` + derivation logic from Mass + Thrust + Armor. |

Seven files sits at the upper end of [FORM-SPECIFICATION.md §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives)'s ~3–7 range. Justification: each of the seven represents a distinct concept with non-overlapping responsibility. Consolidation candidates (e.g., merge `MobilityProfile` into `VehicleModel`) were considered and rejected — Mobility has its own derivation logic and query surface; merging produces a god-file.

---

## SECTION 13: DIAGNOSTICS INTEGRATION

[NORMATIVE] Vehicle exposes the following diagnostic events to the Diagnostics subsystem (when authored):

- `VehicleRebuilt(TechId id, TimeSpan duration, int blockCount)` — fires when a rebuild completes.
- `KinematicUpdated(TechId id, KinematicState state)` — fires per-tick; subscribers may sample.
- `VehicleConstructed(TechId id)` — first model built for this tech.
- `VehicleDisposed(TechId id)` — tech recycled; model gone.

[NORMATIVE] Vehicle does NOT log these events directly. Diagnostics subscribes when it exists; until then, Threading's default-handler discipline (per [THREADING-CONTRACT.md §10](THREADING-CONTRACT.md#section-10-diagnostics-integration)) applies.

---

## SECTION 14: OPEN ITEMS

[OPEN] **Voxel grid resolution scaling formula.** Adaptive to tech extents, but the specific formula (cells per meter? cells per block? log scale?) is decided at implementation. Trigger for pinning: profiling armor-query cost against grid size.

[OPEN] **Mass / inertia source.** Reading per-block mass from TerraTech's block-type database vs computing from block size. The former is more accurate; the latter is faster. Implementation choice; consult existing TAC AI code for block-mass access patterns.

[OPEN] **Kinematic smoothing parameter.** The 4-sample EWMA size is provisional. Tune during self-play.

[OPEN] **Weapon profile data source.** Static weapon properties (range, projectile velocity, fire rate) — read from `ModuleWeapon` instance at rebuild time, or from a Smart-internal weapon-type catalog (pre-loaded). Implementation choice; trade-off is "always-fresh from engine" vs "cached for query speed."

---

## SECTION 15: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not Smart's targeting logic. The weapon profile table provides the *capability* data ("this weapon has 50m range, 200°/s turret slew, currently 0.4s cooldown"); decisions about which target to fire at and when belong to Planning and Control.

This contract is not Smart's vehicle simulation. The kinematic tracker is observation-based — it reads what the engine reports. The "what would happen if I applied throttle X" question is owned by Control's physics rollout, which uses the thrust map and mass distribution from here.

This contract is not generic — VehicleModel is Smart's view of a tech for Smart's planner. Other subsystems with their own physical-model needs (none currently exist) would not inherit from this; they'd build their own.

---

END OF VEHICLE-CONTRACT.md v0.1.0
