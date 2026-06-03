# PATHING-CONTRACT.md

**Subsystem:** Pathing/
**Form:** Smart
**Version:** 0.2.0
**Status:** AUTHORITATIVE — Defines Smart's continuous threat field, sampled terrain map, per-vehicle capability filters, B-spline trajectory representation, CHOMP-inspired gradient-based trajectory optimization, and the pathing service orchestration.

---

## CHANGES SINCE 0.1.0

Verification pass against the codebase:

- **§3.0 added** documenting the relationship to the existing [`TerrainQuery`](../../../Movement/TerrainQuery.cs) static class (shell-level one-shot terrain reads). Smart's `TerrainMap` and `TerrainQuery` coexist with distinct purposes; both call `ManWorld.inst.TileManager.GetTerrainHeightAtPosition` so values agree.
- **§3.1 noted** the sampling source explicitly (`ManWorld.inst.TileManager.GetTerrainHeightAtPosition`) — the verified actual TerraTech terrain-height API.
- **`ImmutableArray<T>` references replaced with `IReadOnlyList<T>`** in struct definitions (per [THREADING-CONTRACT.md §4.1a](THREADING-CONTRACT.md) BCL-only discipline).

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The threat field: continuous function shape, per-enemy contribution, gradient computation.
- The terrain map: sampling resolution, refresh policy, query API.
- The per-vehicle capability filters that mask threat-field contributions by vehicle type.
- The B-spline trajectory representation used as the optimization variable.
- The CHOMP-inspired gradient descent algorithm: cost function, gradient flow, smoothness metric.
- The pathing service: on-request and continuous-refresh dispatch, worker integration.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals and the "BEST AI" mandate.
- [ARCHITECTURE.md](ARCHITECTURE.md) for threading model, MP host/client gating, compute-budget gating.
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for worker pool, double-buffer, bounded queue.
- [WORLD-CONTRACT.md](WORLD-CONTRACT.md) for `BeliefSnapshot` (enemy positions feed threat field) and terrain biasing (Pathing publishes the terrain map that World's belief decay consumes).
- [VEHICLE-CONTRACT.md](VEHICLE-CONTRACT.md) for `VehicleModelSnapshot.Mobility` (drives capability filter selection) and weapon-mount data for threat anisotropy.

**This document GOVERNS:**
- The threat-field query interface that [CONTROL-CONTRACT.md §5.4 `ThreatFieldCost`](CONTROL-CONTRACT.md#section-5-cost-function-multi-objective) calls.
- The terrain-map query interface that [WORLD-CONTRACT.md §4.2 terrain biasing](WORLD-CONTRACT.md#section-4-decay-model-out-of-sight) calls.
- The trajectory output consumed by Control's tactical optimizer and MPC.

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What Pathing owns

Pathing owns four coupled concerns:
1. **Threat field** — a function that returns expected damage exposure at any point in space.
2. **Terrain map** — a function that returns terrain height (and derived slope/normal) at any point.
3. **Per-vehicle capability filters** — modify threat-field queries based on the querying vehicle's mobility.
4. **Continuous trajectory optimization** — given start, goal, threat field, terrain map, and a capability filter, produce a smooth trajectory minimizing cost.

### 1.2 Decisions resolved at v0.1.0

[NORMATIVE]
- **Threat field: continuous function.** Sum of per-enemy radial basis contributions (Gaussian-like) plus weapon-arc anisotropy. Differentiable; supports gradient-descent optimization.
- **Pathing algorithm: CHOMP-inspired gradient descent.** B-spline trajectory parameterization; cost = threat + terrain + smoothness + reach; gradient flow on the trajectory space.
- **Terrain map: sampled height grid with periodic refresh.** ~1 m horizontal resolution; refresh every 10 sec or on world-modification events.
- **Capability gating: unified threat field + per-vehicle filters.** One field, queried through a per-vehicle filter that masks threats irrelevant to that vehicle type.

---

## SECTION 2: THREAT FIELD (continuous function)

### 2.1 Functional form

[NORMATIVE] The threat field at a query point `p` is:

```
threat(p, beliefs) = sum over each hostile tech e in beliefs of (
    contribution_e(p)
)

contribution_e(p) =
    e.threat_rating
  * exp( -distance(p, e.position)² / (2 * e.threat_radius²) )
  * arc_anisotropy(p, e)
  * los_factor(p, e)
  * ammo_factor(e)
```

[NORMATIVE] Term descriptions:
- `e.threat_rating` — per-tech aggregate damage capability (from VehicleModel weapon profiles: sum of damage * fire_rate over weapons with ammo).
- `e.threat_radius` — effective range, derived from longest weapon range on `e`.
- `arc_anisotropy(p, e)` — multiplier in [0.2, 1.0]: 1.0 if `p` is inside `e`'s weapon arcs at firing distance; 0.2 if `p` is in a blind spot. Weapon arcs read from `e.weapons[*].forwardDirection` + `e.weapons[*].yawArc`.
- `los_factor(p, e)` — multiplier in [0.3, 1.0]: 1.0 if the line `e` → `p` is unobstructed; 0.3 if terrain blocks LOS (cheap raycast against terrain map).
- `ammo_factor(e)` — multiplier in [0.0, 1.0]: weighted by remaining ammo across `e`'s weapons. Enemies out of ammo are zero-threat.

### 2.2 Gradient

[NORMATIVE] The threat field is differentiable with respect to `p`. The gradient is:

```
∇threat(p) = sum over hostile e of ∇contribution_e(p)

∇contribution_e(p) =
    e.threat_rating
  * (-(p - e.position) / e.threat_radius²)    // gradient of the Gaussian
  * exp( -distance(p, e.position)² / (2 * e.threat_radius²) )
  * arc_anisotropy(p, e)
  * los_factor(p, e)
  * ammo_factor(e)
```

(Approximation: `arc_anisotropy` and `los_factor` are treated as locally constant for the gradient — they change slowly relative to the Gaussian envelope. This is the standard CHOMP-style relaxation; precise gradients of the anisotropy term are not needed for convergence at the optimization scale.)

[NORMATIVE] Gradient computation runs in the same worker as the threat-field evaluation. Per-query cost is O(N_enemies); cheap.

### 2.3 Threat snapshot lifecycle

[NORMATIVE] The threat field is constructed from a `BeliefSnapshot` (per [WORLD-CONTRACT.md §2.3](WORLD-CONTRACT.md#section-2-belief-state)). On every Pathing tick (~30 Hz target), Pathing reads the latest `BeliefSnapshot` and builds a fresh `ThreatFieldSnapshot`:

```
public sealed class ThreatFieldSnapshot
{
    public readonly long TickStamp;
    public readonly IReadOnlyList<ThreatSource> Sources;
    // ThreatSource = pre-computed per-enemy data (position, radius, rating, arcs, LOS test seeds, ammo)
}
```

[NORMATIVE] The snapshot is published via `DoubleBuffer<ThreatFieldSnapshot>` and consumed by all threat queries until superseded. Queries are stateless; they only read the published snapshot.

### 2.4 Threat field as input to Control

[NORMATIVE] [CONTROL-CONTRACT.md §5.4](CONTROL-CONTRACT.md#section-5-cost-function-multi-objective) defines `ThreatFieldCost(trajectory, beliefs)` as the integral of threat along a candidate trajectory. With Pathing's threat field present, that cost term activates — it was a no-op at Control v0.1.0 pending Pathing.

[NORMATIVE] Control reads through the threat-field interface that Pathing exposes:

```
public interface IThreatField
{
    float Evaluate(Vector3 point, VehicleCapability filter);
    Vector3 Gradient(Vector3 point, VehicleCapability filter);
}
```

The active threat field is read from `Pathing.CurrentThreatField` (the latest `DoubleBuffer<ThreatFieldSnapshot>`).

---

## SECTION 3: TERRAIN MAP (sampled height grid)

### 3.0 Relationship to existing `TerrainQuery`

[NORMATIVE] An existing static class [`TAC_AI/AI/Movement/TerrainQuery`](../../../Movement/TerrainQuery.cs) provides shell-level terrain reads via `ManWorld.inst.TileManager.GetTerrainHeightAtPosition`. It exposes `AboveTheSea`, `AboveTheSeaForcedAccurate`, `AboveHeightFromGround`, `OffsetFromGroundAAlt`, `SnapOffsetToSea`, plus obstruction-awareness helpers (`ObstructionAwarenessAny`, `*SetPieceAny`, `*Terrain`).

[NORMATIVE] Smart's `TerrainMap` does NOT replace `TerrainQuery`. The two coexist with distinct purposes:

- **`TerrainQuery`** (shell): one-shot, uncached, accurate. Used by shell-side spawn validation, attract, world-side RTS navigation. Reads the engine's terrain manager directly.
- **`TerrainMap`** (Smart): per-frame cached representation for Smart's pathing-optimizer hot path. Populates by sampling the same `ManWorld.inst.TileManager.GetTerrainHeightAtPosition` (so values agree with `TerrainQuery`'s reads), but caches into a grid for O(1) repeated queries.

[NORMATIVE] Smart MAY call `TerrainQuery` for one-shot validation (e.g., during scenario generation in Training, or when validating a strategic plan's destination). `TerrainMap` is for Smart's worker-thread reads inside pathing solves.

### 3.1 Structure

[NORMATIVE] The terrain map is a 2D grid over the playable area:

```
public sealed class TerrainMapSnapshot
{
    public readonly long TickStamp;
    public readonly Vector2 WorldOrigin;        // (x, z) of grid cell [0, 0]
    public readonly float CellSize;             // ~1 m provisional
    public readonly int Width;                  // cells in x-axis
    public readonly int Height;                 // cells in z-axis
    public readonly IReadOnlyList<float> HeightSamples;  // row-major; length = Width * Height
}
```

[NORMATIVE] Resolution: 1 m horizontal provisional. Typical map size: a few square km. Memory: ~16 MB for a 4 km² map at 1 m. Acceptable for a desktop release.

[NORMATIVE] Sample source: `ManWorld.inst.TileManager.GetTerrainHeightAtPosition(scenePos, out _)` — same call `TerrainQuery` uses. This guarantees Smart's cached terrain heights agree with the shell-level reads.

[NORMATIVE] Larger maps OR finer resolution would require chunked loading (only the player's region resident in memory). v0.1.0 ships unchunked; chunking is deferred per OPEN.

### 3.2 Refresh policy

[NORMATIVE] The terrain map is refreshed:
- **At world load**: full population from TerraTech's terrain query API (one-time cost; runs at `OnWorldReset` or first `ManGameMode.ModeStartEvent`).
- **Periodically every 10 sec** (provisional): re-sample regions flagged as "potentially modified."
- **On world-modification event** (if TerraTech exposes one — research note OQ-7-pathing): targeted refresh of affected cells.

[NORMATIVE] Periodic refresh runs in a Pathing worker; the full grid is double-buffered so queries see either old or new, never partial.

### 3.3 Query API

[NORMATIVE]

```
public interface ITerrainMap
{
    float HeightAt(Vector2 worldXZ);              // bilinear interpolation between cells
    Vector3 NormalAt(Vector2 worldXZ);             // computed from neighboring cells
    float SlopeAt(Vector2 worldXZ);                // 0 = flat; π/2 = vertical
    bool IsTraversable(Vector2 worldXZ, VehicleCapability filter);  // gating by capability
    bool RaycastSegment(Vector3 from, Vector3 to);  // cheap segment-vs-grid raycast for LOS
}
```

[NORMATIVE] `IsTraversable` consumes a `VehicleCapability` to gate by climb angle, water tolerance, etc. — a steep cliff is not traversable for a wheel but may be for an airplane.

[NORMATIVE] The segment raycast is used by the threat field's `los_factor` (§2.1) and by trajectory optimization to detect terrain intersection.

### 3.4 Terrain biasing for World's belief decay

[NORMATIVE] [WORLD-CONTRACT.md §4.2](WORLD-CONTRACT.md#section-4-decay-model-out-of-sight) declares terrain biasing as a no-op until Pathing exists. With Pathing v0.1.0:

```
public Vector3[3] TerrainBiasingShape(Vector3 meanPosition, float spreadRadius)
{
    // Sample terrain in a sphere around meanPosition;
    // identify blocked directions (cells with slope > max_traversable);
    // return three orthogonal scaling axes that compress the covariance along blocked directions.
}
```

[NORMATIVE] This API is what World reads to apply terrain biasing. Implementation lives in TerrainMap.cs.

---

## SECTION 4: VEHICLE CAPABILITY FILTERS

### 4.1 Filter representation

[NORMATIVE] A `VehicleCapability` is a small struct derived from `VehicleModelSnapshot.Mobility`:

```
public readonly struct VehicleCapability
{
    public readonly VehicleClass Class;          // Wheel, Hover, Airplane, Submarine, Walker
    public readonly float ClimbAngleMax;
    public readonly bool WaterCapable;
    public readonly float VerticalAuthority;     // hover / airplane → > 0
    public readonly float TippingSusceptibility;
    public readonly Vector3 PrimaryAxis;          // forward-facing direction in local space
}
```

### 4.2 Filtering threat queries

[NORMATIVE] When Control or Pathing queries the threat field, it provides a `VehicleCapability`. The filter modifies the per-enemy contribution:

```
ApplyCapabilityFilter(contribution_e, queryPoint, vehicle) =
    contribution_e * relevance_factor(e, vehicle)

relevance_factor(e, vehicle) =
    sum over each weapon w in e.weapons of (
        (per-weapon damage capability) * (1.0 if w can hit a vehicle at queryPoint else 0.2)
    )
    / total_threat_capability_of(e)
```

[NORMATIVE] Concretely:
- A wheel-driven tech queries: short-range cannons aimed upward have low `relevance_factor` (can't physically hit a ground tech in their arcs). Result: those threats contribute less to the field for that wheel.
- An airplane queries: anti-air weapons have HIGH `relevance_factor`. Ground cannons that can't elevate have low.
- A hover queries: both ground and low-altitude threats matter.

[NORMATIVE] The relevance computation runs at threat-field construction time (per snapshot), not per query, by precomputing per-(enemy, capability-class) lookup tables.

### 4.3 Vehicle class derivation

[NORMATIVE] `VehicleClass` is derived from `MobilityProfile`:
- **Airplane** if `WaterCapable == false && VerticalAuthority > 1.5` and primarily-forward thrust.
- **Hover** if `VerticalAuthority` is in `[0.5, 1.5]`.
- **Submarine** if `WaterCapable == true && primarily underwater`.
- **Walker** if no propulsion blocks but anchored-mobility (legs).
- **Wheel** otherwise (default for ground techs).

[NORMATIVE] Classification runs once per VehicleModel rebuild; cached on `MobilityProfile`. Pathing reads, does not classify.

---

## SECTION 5: PATH REPRESENTATION

### 5.1 B-spline trajectory

[NORMATIVE] A trajectory is represented as a B-spline curve:

```
public sealed class Trajectory
{
    public readonly IReadOnlyList<Vector3> ControlPoints;   // ~8-16 points provisional
    public readonly float Duration;                            // seconds
    // Cubic B-spline interpolation provides the smooth path.
}
```

[NORMATIVE] Number of control points: 8 for short paths (~10 m), 16 for long paths (~100 m). The number is fixed per solve (chosen by the requester); intermediate points interpolate via B-spline basis functions.

[NORMATIVE] B-spline output:
- `traj.Position(t)` for `t ∈ [0, 1]` — interpolated position.
- `traj.Velocity(t)` — first derivative.
- `traj.Acceleration(t)` — second derivative.

These derivatives are needed by the optimizer (smoothness) and by Control consumers (rollout warm-starts).

### 5.2 Why B-spline over piecewise linear

[RATIONALE] B-splines give analytic C² continuity (smooth position, velocity, acceleration) with few control points. Piecewise linear has C⁰ at junctions (velocity discontinuous), requiring much higher control-point density for the same smoothness — wastes degrees of freedom in the optimization.

[RATIONALE] B-spline gradients with respect to control points are well-known and cheap to compute.

### 5.3 Endpoints

[NORMATIVE] The first and last control points are pinned to start and goal positions respectively. The first two segments interpolate from the start with initial velocity matching the current kinematic state.

[NORMATIVE] Intermediate control points are the optimization variables. With 16 total and 2 pinned at each end, 12 are free — small enough for sub-millisecond gradient computation.

---

## SECTION 6: CHOMP-INSPIRED TRAJECTORY OPTIMIZATION

### 6.1 The cost function

[NORMATIVE] The total cost of a trajectory `ξ`:

```
cost(ξ) =
    w_threat    * ∫ threat(ξ(t), capability) dt            // integral of threat along path
  + w_terrain   * ∫ terrain_cost(ξ(t)) dt                  // height-derived penalties
  + w_smooth    * ∫ ||ξ''(t)||² dt                          // path acceleration norm
  + w_length    * ∫ ||ξ'(t)|| dt                            // arc length penalty
  + w_reach     * ||ξ(1) - goal||²                          // endpoint reach
  + w_velocity  * ||ξ'(1) - goal_velocity||²                // endpoint velocity match
```

[NORMATIVE] Integrals are approximated by Gauss quadrature over the parameter `t ∈ [0, 1]` at ~10-20 sample points. Cheap.

### 6.2 The gradient flow

[NORMATIVE] The optimizer updates each free control point `q_i`:

```
q_i ← q_i - lr * (∂cost / ∂q_i)
```

The partial derivative `∂cost / ∂q_i` is computed analytically by chain rule:
- `∂(∫ threat dt) / ∂q_i` decomposes via B-spline basis functions into per-sample contributions; each contribution uses Pathing's `Gradient` API (§2.2).
- `∂(∫ smoothness dt) / ∂q_i` is computable as a fixed linear operator on control points (the standard B-spline Laplacian).
- `∂(reach + velocity) / ∂q_i` is straightforward.

[NORMATIVE] Implementation owns the analytic gradient math; tested against numerical finite differences during development.

### 6.3 The CHOMP metric

[NORMATIVE] CHOMP's contribution over plain gradient descent is the **trajectory-space metric** — the Riemannian metric is the smoothness norm, so the gradient step naturally preserves smoothness:

```
q_i ← q_i - lr * (∂cost / ∂q_i)_smoothness-corrected
```

[NORMATIVE] In implementation: precompute the smoothness metric matrix `M` (a sparse symmetric matrix on control points) and apply the inverse as a preconditioner on the gradient. The update becomes:

```
q ← q - lr * M⁻¹ * ∇cost
```

[NORMATIVE] This is the canonical CHOMP step. The matrix is small (`N x N` where `N` is the free control point count), pre-factored once per optimizer instance, applied each step.

### 6.4 Iteration

[NORMATIVE] Each optimization solve:
- Initialize trajectory: warm-start from previous solve if available; otherwise straight line from start to goal.
- Run ~20 gradient steps (provisional).
- Terminate early if gradient norm drops below convergence threshold OR cancellation token fires.
- Output: optimized trajectory.

[NORMATIVE] Convergence threshold and step count are provisional per [FORM-SPECIFICATION.md §1 disclaimer](FORM-SPECIFICATION.md); tune during self-play.

### 6.5 Per-solve compute

[NORMATIVE] Per solve: 20 gradient steps × O(N control points × M sample points × O(threat-eval)). For N=12, M=20, O(threat-eval) ≈ 10 enemies: ~50,000 operations. Sub-millisecond on a modern CPU.

### 6.6 Anytime behavior

[NORMATIVE] On cancellation, return the current best trajectory regardless of convergence. Per [THREADING-CONTRACT.md §3](THREADING-CONTRACT.md#section-3-cancellation-model), `OperationCanceledException` is thrown at the next step check; the optimizer catches it and publishes the in-flight trajectory.

---

## SECTION 7: ORCHESTRATION

### 7.1 The pathing service

[NORMATIVE] `PathingService` is the entry point. It owns the threat-field and terrain-map buffers, the per-vehicle filter cache, and the worker that runs optimizations.

```
public static class PathingService
{
    public static ThreatFieldSnapshot CurrentThreatField { get; }
    public static TerrainMapSnapshot CurrentTerrain { get; }

    public static void RequestPath(PathRequest req);    // bounded queue; drop-oldest per consumer
    public static Trajectory GetLastPath(TechId tech);  // synchronous read
}

public readonly struct PathRequest
{
    public readonly TechId Tech;
    public readonly Vector3 Start;
    public readonly Vector3 Goal;
    public readonly Vector3 GoalVelocity;
    public readonly float Duration;
    public readonly VehicleCapability Capability;
}
```

### 7.2 Request flow

[NORMATIVE] When Control's tactical optimizer needs a path:
1. Build `PathRequest`.
2. Call `PathingService.RequestPath(req)`. Returns immediately; non-blocking.
3. Read `PathingService.GetLastPath(tech)` on subsequent ticks. Returns the latest computed trajectory for the tech (or `null` if none yet).

[NORMATIVE] The request queue is a `BoundedQueue<PathRequest>` (per [THREADING-CONTRACT.md §5](THREADING-CONTRACT.md#section-5-bounded-queue-drop-oldest)) with capacity equal to (active tech count). Drop-oldest policy: the freshest request for each tech wins.

### 7.3 Continuous refresh

[NORMATIVE] Independent of explicit requests, Pathing maintains continuous-refresh background work for all currently-active techs:
- Every strategic tick (200–500 ms), refresh trajectories for techs whose situation has changed (target reassignment, role switch, significant belief update).
- Every 10 sec, refresh trajectories for "settled" techs (long-term holders, defensive perimeter) to capture slow threat changes.

[NORMATIVE] Continuous-refresh requests are merged with explicit requests in the bounded queue. The freshest wins.

### 7.4 Host-only

[NORMATIVE] Per ARCHITECTURE §3.2, Pathing runs only when `host == true`. On client, threat-field and terrain-map snapshots are not maintained; queries return zero/sentinel values. (Control's MPC on client doesn't run anyway, so this is fine.)

---

## SECTION 8: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/Pathing/` contains five files:

| File | Owns |
|---|---|
| `PathingService.cs` | Orchestration, request queue, worker dispatch, public API. |
| `ThreatField.cs` | Continuous-function representation, per-enemy contribution, gradients, snapshot lifecycle. |
| `TerrainMap.cs` | Sampled height grid, refresh policy, query API, raycast, terrain biasing helper. |
| `TrajectoryOptimizer.cs` | B-spline representation, CHOMP cost function, gradient computation, metric preconditioner, iteration loop. |
| `CapabilityFilters.cs` | `VehicleCapability` derivation, per-vehicle relevance computation, filter caches. |

Five files. Mid-range of [FORM-SPEC §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives). Justification: each file owns a coherent algorithmic concern. The trajectory optimizer subsumes path representation (B-spline) because they're inseparable for the gradient math.

---

## SECTION 9: DIAGNOSTICS INTEGRATION

[NORMATIVE] Pathing exposes:

- `PathingRequestQueued(TechId tech, Vector3 goal)` — at request submission.
- `PathingRequestDropped(TechId tech, int queueLength)` — when bounded queue drops oldest.
- `PathingSolveCompleted(TechId tech, int gradientSteps, TimeSpan duration, float finalCost)` — on solve completion.
- `PathingSolveCancelled(TechId tech, int gradientSteps, float partialCost)` — on cancellation.
- `ThreatFieldRebuilt(int sourceCount, TimeSpan duration)` — per tick.
- `TerrainRefreshed(int cellsUpdated, TimeSpan duration)` — per periodic refresh.
- `TerrainGridStale(float secondsSinceRefresh)` — when the grid hasn't refreshed in expected window.

---

## SECTION 10: OPEN ITEMS

[OPEN] **Terrain map resolution.** 1 m provisional. Larger missions may need 2 m for memory; smaller missions may benefit from 0.5 m for accuracy.
[OPEN] **Threat field per-enemy radius scaling.** Currently `e.threat_radius = longest weapon range`. Consider also weighting by projectile velocity (slower projectiles → effectively shorter range).
[OPEN] **Terrain refresh cadence.** 10 sec provisional; revisit if TerraTech exposes a modification event.
[OPEN] **Map chunking.** Unchunked at v0.1.0; revisit when missions exceed ~16 km² or memory constraints force it.
[OPEN] **CHOMP step count K.** 20 provisional.
[OPEN] **B-spline control point count.** 8–16 provisional; tune per typical path length.
[OPEN] **Cost weights.** All `w_*` provisional; major self-play tuning target.
[OPEN] **Convergence threshold for early exit.** Provisional gradient-norm threshold; tune.
[OPEN] **Continuous refresh strategy.** Heuristic at v0.1.0; consider replacing with a relevance-prioritized scheduler post-v1.

---

## SECTION 11: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not Smart's combat decision. Threat field describes danger; Pathing finds routes that minimize danger. The decision to engage vs avoid danger lives in Planning / Coordination / Control.

This contract is not the per-tech control. The trajectory output is a *desired path*; Control's MPC produces the actuator commands to follow it.

This contract is not the obstacle representation. Pathing handles soft costs (threat, terrain steepness) and treats them as continuous. Hard obstacles (walls, water boundaries for non-water-capable techs) are handled by the `IsTraversable` filter on the terrain map and as steep terrain penalties; truly impassable regions get effectively-infinite terrain cost.

This contract does not own enemy threat modeling. The threat field consumes pre-computed threat data from VehicleModel.Weapons; it doesn't compute weapon-vs-armor damage. Damage modeling is a Vehicle / Control concern.

This contract does not own per-vehicle motion dynamics. Capability filters describe what kind of threats matter to which vehicle; the actual motion (how a wheel moves vs how a hover moves) lives in Vehicle's mobility profile + Control's physics rollout.

---

END OF PATHING-CONTRACT.md v0.1.0
