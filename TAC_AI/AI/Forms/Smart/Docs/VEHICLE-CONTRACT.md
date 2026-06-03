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

### 5.1 What it computes

[NORMATIVE] One `WeaponProfile` per weapon block on the tech:
- Mount position (tech-local).
- Forward direction (tech-local) — where the weapon points before turret rotation.
- Arc cone (max turret rotation in pitch/yaw, in radians).
- Range (effective max range; projectile dies past this).
- Projectile velocity (m/s).
- Damage per projectile.
- Fire rate (rounds per second).
- Ammo state (current count, capacity).
- Current cooldown (seconds until next shot ready).

[NORMATIVE] Static data (mount, range, fire rate, damage, projectile velocity) is captured at rebuild time. Dynamic data (current cooldown, ammo state) is updated continuously by the kinematic tracker pass per-tick (it reads these from the weapon block).

### 5.2 Why include dynamic data in the snapshot

[RATIONALE] Planning needs to know "can this enemy fire at me right now?" — that requires current cooldown and ammo. Including dynamic data in the snapshot means Planning reads a coherent view; it does not need to dual-source.

### 5.3 API sketch

```
public readonly struct WeaponProfile
{
    public readonly WeaponId Id;
    public readonly Vector3 MountPositionLocal;
    public readonly Vector3 ForwardDirectionLocal;
    public readonly float YawArcRadians;
    public readonly float PitchArcRadians;
    public readonly float Range;
    public readonly float ProjectileVelocity;
    public readonly float DamagePerProjectile;
    public readonly float FireRateHz;
    public readonly int AmmoCurrent;
    public readonly int AmmoCapacity;
    public readonly float CooldownRemaining;
}
```

---

## SECTION 6: ARMOR MAP (VOXELIZED GRID)

### 6.1 Structure

[NORMATIVE] `ArmorMap` is a 3D voxel grid covering the tech's bounding volume, with resolution adaptive to tech extents.

- Grid resolution: chosen so that each voxel is approximately one block-cell on a side. Typical: 6×6×6 to 16×16×16 for small-to-large techs.
- Per-voxel data: aggregate hit-points + material type (worst-case among contained blocks) + occupancy density (fraction of cell filled by blocks).
- Coordinate origin: tech-local, axis-aligned to tech axes (NOT world axes).

### 6.2 Face-direction queries

[NORMATIVE] The primary query: "given an attack direction `d` (in tech-local), what's the weak face?" Implementation:

1. Compute the cardinal/intercardinal direction nearest to `-d` (the side facing the attack).
2. Walk the slab of voxels on that side; sum hit-points; find the minimum-HP face.
3. Return: face index, total HP of that face, weakest sub-region within the face.

[NORMATIVE] Queries are constant-time (per the voxel grid traversal — the grid is small). No raycasts at query time; raycasts are not part of this design.

### 6.3 Why voxels and not raycast-on-demand

[RATIONALE] The cost trade-off (Q&A Layer 4): voxel grid takes ~few KB per tech and constant-time queries; raycast costs 1-2ms per query when the tech is large and the ray sampling is dense. Planning issues many face-direction queries per planning tick (one per candidate engagement angle); the voxel grid amortizes far better.

### 6.4 API sketch

```
public sealed class ArmorMap
{
    public readonly Vector3Int GridResolution;
    public readonly Bounds LocalBounds;
    // Internal voxel data: per-cell HP + material + density.

    public ArmorQueryResult QueryWeakFace(Vector3 attackDirectionLocal);
    public ArmorQueryResult QueryRegion(Bounds regionLocal);
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

### 7.1 What it tracks

[NORMATIVE] Per-tech estimator for instantaneous physical state:
- Position (world-space).
- Linear velocity (world-space).
- Linear acceleration (world-space; estimated from velocity history).
- Angular velocity (world-space).
- Jerk (rate-of-change of acceleration; estimated from acceleration history).
- Heading (forward vector, world-space).

### 7.2 Update cadence

[NORMATIVE] The kinematic tracker runs on the **main thread** during the perception pass (driven from `Operations`). Each tick:
1. Read `Tank.boundsCentreWorldNoCheck`, `Tank.rbody.velocity`, `Tank.rbody.angularVelocity`, `Tank.transform.forward`.
2. Update internal history buffer (last N samples).
3. Estimate acceleration as `(velocity_now - velocity_prev) / dt`.
4. Estimate jerk similarly.
5. Publish the new `KinematicState` snapshot.

[NORMATIVE] Updates run on main thread because they read engine objects. Heavy filtering (smoothing, outlier rejection) is permitted but kept lightweight; the tracker MUST complete in O(1) regardless of how many techs are tracked.

### 7.3 Smoothing

[NORMATIVE] Acceleration and jerk MUST be smoothed (e.g., a 4-sample exponentially-weighted moving average) to suppress single-frame numerical noise. The smoothing parameter is provisional (per [FORM-SPECIFICATION.md §1 disclaimer](FORM-SPECIFICATION.md)); tune during self-play.

### 7.4 API sketch

```
public readonly struct KinematicState
{
    public readonly Vector3 PositionWorld;
    public readonly Vector3 VelocityWorld;
    public readonly Vector3 AccelerationWorld;
    public readonly Vector3 AngularVelocityWorld;
    public readonly Vector3 JerkWorld;
    public readonly Vector3 HeadingWorld;
    public readonly long TickStamp;
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

## SECTION 10: FILE LAYOUT

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

## SECTION 11: DIAGNOSTICS INTEGRATION

[NORMATIVE] Vehicle exposes the following diagnostic events to the Diagnostics subsystem (when authored):

- `VehicleRebuilt(TechId id, TimeSpan duration, int blockCount)` — fires when a rebuild completes.
- `KinematicUpdated(TechId id, KinematicState state)` — fires per-tick; subscribers may sample.
- `VehicleConstructed(TechId id)` — first model built for this tech.
- `VehicleDisposed(TechId id)` — tech recycled; model gone.

[NORMATIVE] Vehicle does NOT log these events directly. Diagnostics subscribes when it exists; until then, Threading's default-handler discipline (per [THREADING-CONTRACT.md §10](THREADING-CONTRACT.md#section-10-diagnostics-integration)) applies.

---

## SECTION 12: OPEN ITEMS

[OPEN] **Voxel grid resolution scaling formula.** Adaptive to tech extents, but the specific formula (cells per meter? cells per block? log scale?) is decided at implementation. Trigger for pinning: profiling armor-query cost against grid size.

[OPEN] **Mass / inertia source.** Reading per-block mass from TerraTech's block-type database vs computing from block size. The former is more accurate; the latter is faster. Implementation choice; consult existing TAC AI code for block-mass access patterns.

[OPEN] **Kinematic smoothing parameter.** The 4-sample EWMA size is provisional. Tune during self-play.

[OPEN] **Weapon profile data source.** Static weapon properties (range, projectile velocity, fire rate) — read from `ModuleWeapon` instance at rebuild time, or from a Smart-internal weapon-type catalog (pre-loaded). Implementation choice; trade-off is "always-fresh from engine" vs "cached for query speed."

---

## SECTION 13: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not Smart's targeting logic. The weapon profile table provides the *capability* data ("this weapon has 50m range, 200°/s turret slew, currently 0.4s cooldown"); decisions about which target to fire at and when belong to Planning and Control.

This contract is not Smart's vehicle simulation. The kinematic tracker is observation-based — it reads what the engine reports. The "what would happen if I applied throttle X" question is owned by Control's physics rollout, which uses the thrust map and mass distribution from here.

This contract is not generic — VehicleModel is Smart's view of a tech for Smart's planner. Other subsystems with their own physical-model needs (none currently exist) would not inherit from this; they'd build their own.

---

END OF VEHICLE-CONTRACT.md v0.1.0
