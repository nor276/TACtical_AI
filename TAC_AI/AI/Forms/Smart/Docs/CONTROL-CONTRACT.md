# CONTROL-CONTRACT.md

**Subsystem:** Control/
**Form:** Smart
**Version:** 0.3.0
**Status:** AUTHORITATIVE — Defines Smart's continuous controller (sampling-based MPC), per-tech tactical optimizer (Adam gradient ascent), physics rollout, multi-objective cost function, maneuver library, weapon-fire controller with embedded energy management, and the engine-facing movement controller.

---

## CHANGES SINCE 0.2.0

Verification pass against the codebase:

- **§8 (SmartMovementController) rewritten** with the actual 15-member `IMovementAIController` interface read from [TAC_AI/AI/Contracts/IMovementAIController.cs](../../Contracts/IMovementAIController.cs). Replaces the prior "deferred to implementation time" punt with concrete per-member behavior. Notes [`MovementControllerBase`](../../../Movement/MovementControllerBase.cs) as a reference base class Smart MAY extend (`internal abstract`, so direct interface implementation is also valid).
- **`ImmutableArray<T>` references replaced with `IReadOnlyList<T>`** in struct definitions (consistent with [THREADING-CONTRACT.md §4.1a](THREADING-CONTRACT.md) BCL-only discipline; `System.Collections.Immutable` NuGet not added).

## CHANGES SINCE 0.1.0

- §1.1 expanded to include weapon-fire controller and embedded energy management per workflow step 1.9.
- §1.2 added four weapon-fire decisions resolved at the Step 1.9 Q&A round.
- §9 (ControlFrame integration) revised to describe how `ControlVector.WeaponFireCommits` is populated.
- §10 added: Weapon Fire Controller (lead computation, per-weapon fire decisions, multi-weapon coordination, target switching hysteresis, friendly fire avoidance, energy management, ammo conservation).
- §11 (File Layout) updated: 8 files, adding `WeaponFireController.cs` (energy management embedded).
- §12 (Diagnostics) gained weapon-fire events.
- §13 (Open Items) and §14 (What this is not) updated to reflect step 1.9 closing the weapon-fire deferral.

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The continuous controller (MPC) implementation shape: sampling-based (MPPI / CEM family).
- The per-tech tactical optimizer: Adam gradient ascent over "where do I want to be in N seconds."
- The physics rollout used to evaluate candidate control sequences (consumes Vehicle's VehicleModelSnapshot).
- The multi-objective cost function: reach + effort + threat + weapon alignment, with explicit slots for learned terms.
- The maneuver library: reusable trajectory primitives used as warm-starts.
- The `ControlFrame` write protocol: control profile publication and per-frame consumption.
- The engine-facing `IMovementAIController` implementation (OQ-10 resolution).
- The choice between intent-bus and direct-TankControl-write paths (OQ-6 resolution).

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals, "BEST AI" mandate, and AI-collaborator directives.
- [ARCHITECTURE.md](ARCHITECTURE.md) for threading model, MP host/client gating, compute-budget gating.
- [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md) for `IAIForm` hook semantics, `TankControl` / `EControlOperatorSet` surfaces, `IMovementAIController` reference.
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for worker pool, `DoubleBuffer<T>`, bounded queue, cancellation tokens.
- [VEHICLE-CONTRACT.md](VEHICLE-CONTRACT.md) for `VehicleModelSnapshot` and physics-rollout inputs.
- [WORLD-CONTRACT.md](WORLD-CONTRACT.md) for belief-state inputs (target predictions for the cost function).

**This document GOVERNS:**
- The `ControlFrame` consumption pattern (`TankControl` direct write).
- The per-tech `SmartMovementController` instance the shell holds.
- The weapon-fire-controller hooks added in [step 1.9](WORKFLOW.md#19-build-the-weapon-fire-controller) (this contract revised then).

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What this contract owns at v0.2.0

Control/ owns five coupled concerns:

1. **Continuous controller (MPC)** — every physics tick (or every other, budget-permitting), produce a per-frame control profile that drives the tech toward its current tactical goal.
2. **Per-tech tactical optimizer** — every Operations tick, gradient-ascent over "where do I want to be in N seconds" to update the goal the MPC chases.
3. **Engine-facing movement controller** — the `IMovementAIController` instance the shell holds; mostly a no-op shell because the substantive control work lives in `ControlFrame`.
4. **Maneuver library** — reusable trajectory primitives used to warm-start MPC sampling.
5. **Weapon-fire controller** (added at v0.2.0) — per-frame per-weapon fire decisions: lead computation, multi-weapon coordination, target switching hysteresis, friendly fire avoidance, energy management for energy weapons, ammo conservation.

### 1.2 Decisions resolved at v0.1.0–v0.2.0

[NORMATIVE]
- **OQ-6 resolved (Layer 4 Step 1.6):** MPC writes directly to `TankControl` in `ControlFrame`, bypassing the existing `EControlOperatorSet` intent bus and the engine's `AIControllerDefault` / `AIControllerAir` / `AIControllerStatic` movement controllers. MPC's frame-by-frame control profiles are too fine-grained for the intent-bus abstraction.
- **MPC class:** **Sampling-based** (MPPI variant; details §3). Generate N candidate control sequences each tick, roll each through Vehicle physics, weight by cost. Naturally handles nonlinear tech dynamics; embarrassingly parallel; ready to upgrade to learning-augmented when Learning lands.
- **Cost function:** **Multi-objective** with four explicit terms (state error, control effort, threat-field avoidance, weapon alignment) and a slot for learned terms (§5).
- **Tactical optimizer:** **Adam** gradient ascent (§6). β₁=0.9, β₂=0.999, ε=1e-8.
- **OQ-10 (IMovementAIController) deferred:** Smart's `SmartMovementController` is a thin shell that implements the interface for shell compliance. The exact method set is determined at implementation time by reading `VanillaMovementController` and the `IMovementAIController` interface definition; the contract requires that whatever methods exist either no-op or pass through to the buffer-driven control flow (§8).
- **v0.2.0 weapon-fire resolutions** (Step 1.9 Q&A round):
  - **Lead computation:** linear Kalman extrapolation from World's belief velocity, with a slot for Learning's trajectory residual model (currently zero) (§10.3).
  - **Multi-weapon coordination:** hybrid — close-range weapons fire independently; long-range / high-damage weapons coordinate salvos. `FireMode` derived from weapon profile at VehicleModel build time (§10.5).
  - **Target switching hysteresis:** 30% expected-value improvement threshold + 30-tick minimum sticky duration prevents oscillation (§10.6).
  - **Friendly fire avoidance:** raycast check before commit; suppress if friendly tech in projectile path within tolerance (§10.7).
  - **Energy management embedded:** energy weapons consume from a per-tech energy budget tracked by the weapon fire controller; priority-ordered when budget is tight (§10.8).

---

## SECTION 2: CONTINUOUS CONTROLLER (ORCHESTRATION)

### 2.1 Per-tech instance

[NORMATIVE] One `ContinuousController` exists per Smart-driven tech. It lives inside the tech's `FormState` object alongside other per-tech state. Lifecycle: created in `OnTechSpawn`, disposed in `OnTechRecycle`.

[NORMATIVE] The controller owns:
- A reference to the tech's `DoubleBuffer<VehicleModelSnapshot>` (read).
- A reference to the world-level `DoubleBuffer<BeliefSnapshot>` (read).
- A per-tech `DoubleBuffer<TacticalGoal>` — the tactical optimizer publishes here.
- A per-tech `DoubleBuffer<ControlProfile>` — MPC publishes here; ControlFrame consumes.
- A `TacticalOptimizer` instance.
- A bounded queue for MPC work requests.

### 2.2 Tick flow (Operations hook)

[NORMATIVE] When `SmartForm.Operations(helper, host)` is called and `host == true`:

1. **Belief check.** Read the latest `BeliefSnapshot` from World. If the belief about own tech is missing or stale (lost-threshold per WORLD §4.1), publish a neutral control profile and return.
2. **Tactical update.** Step the `TacticalOptimizer` by ~10 Adam iterations (§6); publish the new `TacticalGoal` to the per-tech buffer.
3. **MPC dispatch.** Enqueue an MPC work item with `(VehicleModelSnapshot, KinematicState, TacticalGoal, BeliefSnapshot)` captured-by-value. The bounded queue's drop-oldest policy (per THREADING §5) discards prior pending MPC requests for this tech; the freshest snapshot wins.
4. **No-op on client.** When `host == false`, return without doing any of the above (per ARCHITECTURE §3.2 host-authority).

[NORMATIVE] The worker pulls the MPC work, runs the sampling MPC (§3), and publishes a `ControlProfile` to the per-tech buffer. The next `ControlFrame` reads that profile.

### 2.3 Why per-tech and not pooled

[RATIONALE] Per-tech instances keep state (Adam moment estimates, last tactical goal, last published control profile) cleanly scoped. The actual *work* runs in the shared worker pool from Threading; the per-tech instance is just the orchestration handle. No god-controller managing all techs.

---

## SECTION 3: SAMPLING-BASED MPC (MPPI)

### 3.1 The algorithm

[NORMATIVE] On each MPC dispatch, run the following loop in a worker thread:

```
Input:  vehicle (VehicleModelSnapshot)
        x0 (current KinematicState, ground truth from main thread)
        goal (TacticalGoal — desired state at horizon end)
        beliefs (BeliefSnapshot — for cost-function threat/weapon terms)
        previousMean (mean control trajectory from last solve, for warm-start)

Hyperparameters:
        N = number of samples (initial: 128)
        H = horizon steps (initial: 20 steps × 0.05 s = 1.0 s horizon)
        Σ = sampling covariance (per-control-dimension variance)
        λ = MPPI temperature (controls how strongly low-cost samples dominate)

Algorithm:
        for sample i in 1..N:
            U_i = previousMean + noise_i   (noise sampled from N(0, Σ) per step)
            traj_i = PhysicsRollout(vehicle, x0, U_i, H)   (§4)
            cost_i = CostFunction(traj_i, U_i, goal, beliefs)   (§5)

        # MPPI weighted average (importance sampling)
        weight_i = exp(-cost_i / λ)
        weight_i = weight_i / sum(weights)
        U_optimal = sum(weight_i * U_i)

        # Publish the resulting control trajectory
        publishControlProfile(U_optimal, vehicle.kinematics.tickStamp + H)
        previousMean = shift(U_optimal, 1)   # warm-start: shift left by one step
```

[NORMATIVE] The algorithm is **anytime** (per ARCHITECTURE §2.3). If the cancellation token fires mid-sampling (compute budget exceeded), publish the best result so far weighted only over samples completed; do not skip the publish step. A late-cancelled solve still produces *something*.

### 3.2 Hyperparameter values (provisional)

[NORMATIVE — provisional per FORM-SPEC §1 disclaimer]
- N = 128 samples (target).
- H = 20 steps × 0.05 s = 1.0 s horizon (target; within FORM-SPEC §1.2's 0.5–2 s range).
- λ (temperature) = 1.0.
- Σ (sampling covariance): per-control-dimension variance scaled to the dimension's actuator range (e.g., σ_throttle = 0.3 normalized; σ_steer = 0.4 normalized).

Tuned during self-play (workflow step 1.11).

### 3.3 Parallelism within a single solve

[NORMATIVE] The N samples are independent. The implementation MAY split them across multiple workers from the Threading pool, fanning out the rollouts and joining the cost computation. Concrete fan-out factor is an implementation choice; the contract requires the result to be deterministic given identical inputs *up to non-determinism inherent in parallel sample ordering* (per ARCHITECTURE §3.2, Smart is host-authority; non-determinism is permitted).

### 3.4 Warm-start

[NORMATIVE] `previousMean` is shifted left by one step at the end of each solve to use as the next solve's warm-start. This is the standard MPC receding-horizon pattern: the controller already computed most of what the next solve needs.

[NORMATIVE] When the tactical goal changes substantially (norm of delta > threshold), the warm-start is reset to zero or to a maneuver-library primitive (§7) that approximates the new goal.

### 3.5 Why MPPI vs CEM vs random shooting

[RATIONALE] MPPI is chosen because:
- Importance-weighted average gives smooth, robust solutions that don't snap between samples.
- The temperature λ tunes between greedy (low λ, pick best) and explorative (high λ, average over many).
- Handles non-differentiable cost terms (weapon-in-arc is a binary check) cleanly.
- Standard in modern robotics; well-studied; many reference implementations available for guidance.

CEM (Cross-Entropy Method) is an alternative — iterative refinement of the sampling distribution. Worth revisiting if MPPI shows convergence problems on Smart's cost landscape; the upgrade is contained to this section.

---

## SECTION 4: PHYSICS ROLLOUT

### 4.1 What it computes

[NORMATIVE] `PhysicsRollout(vehicle, x0, U, H) → trajectory` simulates a tech's motion forward over H timesteps given:
- `vehicle` — `VehicleModelSnapshot` (mass, thrust map, mobility).
- `x0` — initial `KinematicState`.
- `U` — control sequence of length H, each step a control vector `(throttle, steer, brake)` (and per-mount-point thrust activation for techs with discrete propulsion choices).
- `H` — horizon length in steps.

Output: trajectory of `H+1` states.

### 4.2 The dynamics

[NORMATIVE] Each rollout step applies:

```
For step k in 0..H-1:
    # Map control to forces and torques via thrust map.
    F_net = sum over propulsion blocks of (control_k applied to block.thrust direction * block.maxThrust)
    τ_net = sum over propulsion blocks of (block.mountPos × F_block)

    # Add environmental forces (gravity, drag estimate).
    F_net += gravity * vehicle.mass.totalMass
    F_net -= dragCoefficient * |x_k.velocity| * x_k.velocity

    # Newton-Euler integration.
    a_lin = F_net / vehicle.mass.totalMass
    a_ang = vehicle.mass.inertiaInverse * τ_net

    x_{k+1}.velocity = x_k.velocity + a_lin * dt
    x_{k+1}.position = x_k.position + x_{k+1}.velocity * dt
    x_{k+1}.angularVelocity = x_k.angularVelocity + a_ang * dt
    x_{k+1}.heading += x_{k+1}.angularVelocity.y * dt
```

[NORMATIVE] The rollout uses the **full** Vehicle physical model (mass distribution, thrust map, mobility profile), NOT a simplified placeholder. Cost: O(H × num_propulsion_blocks) per rollout. For 20 steps and ~10 propulsion blocks per tech and 128 samples, that's ~25k operations per MPC solve per tech. Cheap.

### 4.3 Approximations

[NORMATIVE] The rollout uses approximations chosen for "good enough at horizon distance":
- Drag is quadratic (not exact aerodynamics).
- No terrain contact resolution — the rollout ignores collisions and ground contact within the horizon. A trajectory that physically would have hit a wall is *not* invalidated by the rollout; the cost function's threat-field term (§5) penalizes intersecting obstacles instead.
- No block damage during rollout — the tech doesn't lose blocks mid-rollout even if the rollout's trajectory would take damage. The cost function's threat-field term takes care of "this trajectory is dangerous" without modeling block loss.

[RATIONALE] Full physical fidelity in rollout would make 128 samples × 20 steps × full physics computationally infeasible. The cost function penalties replace the explicit physics for non-dynamics concerns.

### 4.4 Per-vehicle-type specialization

[NORMATIVE] The dynamics integrator is generic — it computes from the thrust map. Specializations are NOT required, BUT:
- For airplanes: a lift estimate (from forward speed and surface area) is added to F_net. Provisional approximation; revise if observed inaccuracy.
- For hovers: a damping torque is added to prevent rollover. Provisional approximation.

These specializations live inside `PhysicsRollout.cs`, gated on `MobilityProfile` flags.

---

## SECTION 5: COST FUNCTION (MULTI-OBJECTIVE)

### 5.1 Components

[NORMATIVE] The total cost of a candidate trajectory `traj` and control sequence `U`:

```
cost(traj, U, goal, beliefs) =
    w_reach     * StateReachCost(traj[H], goal)
  + w_effort    * ControlEffortCost(U)
  + w_threat    * ThreatFieldCost(traj, beliefs)
  + w_weapon    * WeaponAlignmentCost(traj, U, beliefs)
  + w_learned   * LearnedCost(traj, U, beliefs)        # zero until Learning lands
```

[NORMATIVE] Each term is non-negative; lower is better. Weights `w_*` are Smart-internal tunables, provisional values declared in §5.6.

### 5.2 State reach cost

```
StateReachCost(x_H, goal):
    return ||goal.position - x_H.position||² + α_heading * (goal.heading - x_H.heading)²
       + β_velocity * ||goal.velocity - x_H.velocity||²
```

[NORMATIVE] Position error dominates; heading and velocity errors have smaller weights. The tactical optimizer (§6) produces `goal`; this term is what makes MPC pursue tactical decisions.

### 5.3 Control effort cost

```
ControlEffortCost(U):
    return sum over steps k of (
        γ_throttle * U[k].throttle²
      + γ_steer    * U[k].steer²
      + γ_smoothness * |U[k] - U[k-1]|²
    )
```

[NORMATIVE] The smoothness penalty (last term) suppresses bang-bang control; the per-magnitude penalty prefers low-effort solutions when they reach the goal equally well.

### 5.4 Threat-field cost

[NORMATIVE] Integral of the threat field along the trajectory:

```
ThreatFieldCost(traj, beliefs):
    return sum over steps k of (threatField(traj[k].position, beliefs))
```

`threatField(p, beliefs)` is owned by Pathing (when authored, [PATHING-CONTRACT.md]). Until Pathing exists, the function returns zero — this term contributes nothing at v0.1.0. The cost-function infrastructure is in place; the data isn't.

### 5.5 Weapon alignment cost

[NORMATIVE] Penalize trajectories that point our weapons away from relevant targets:

```
WeaponAlignmentCost(traj, U, beliefs):
    target = SelectMostRelevantTarget(beliefs)   # highest threat * exposure
    return sum over steps k of (
        for each weapon w in our vehicle.weapons:
            angle = AngleFromArc(w, traj[k], target.predictedPosition(k))
            if angle > w.arcExtent:
                cost += angle - w.arcExtent       # weapon pointing wrong way
    )
```

[NORMATIVE] Trajectories that keep our weapons trained on relevant targets are preferred even when those trajectories don't directly serve the tactical goal. This is what couples movement to firing without requiring the tactical optimizer to know about weapons.

### 5.6 Weights (provisional)

[NORMATIVE — provisional]
- w_reach = 1.0 (baseline)
- w_effort = 0.05
- w_threat = 2.0 (when Pathing lands; until then this term is zero regardless of weight)
- w_weapon = 0.5
- w_learned = 0.0 (until Learning lands)

Sub-weights: α_heading = 5.0, β_velocity = 0.5, γ_throttle = γ_steer = 0.1, γ_smoothness = 1.0.

Tuned during self-play. Per FORM-SPEC §5.4, these values trace to a stated reason: "starting points chosen to make state-error dominate while threat (when present) outranks reach, and effort is a minor regularizer." Future revision is welcome with measurement.

---

## SECTION 6: TACTICAL OPTIMIZER (ADAM GRADIENT ASCENT)

### 6.1 What it optimizes

[NORMATIVE] The tactical optimizer chooses, per tech, the *best position and heading to occupy in N seconds*. Output: `TacticalGoal` consumed by §2 and §3.

```
TacticalGoal {
    Vector3 position;
    float heading;
    Vector3 velocity;       // typically zero or toward-target; "where I want to end up moving"
    float lookAheadSeconds; // typically 1.0 sec, matches MPC horizon
}
```

### 6.2 The utility function

[NORMATIVE] The utility of a candidate goal `g`:

```
Utility(g, beliefs, vehicle) =
    u_engagement(g, beliefs)    # range to most relevant target within preferred engagement band
  + u_armor_facing(g, beliefs)  # keep my weak face away from threats (uses Vehicle.ArmorMap)
  + u_cover(g, beliefs)         # is g behind terrain that blocks enemy LOS?
  + u_team_role(g, beliefs)     # team-level role from Coordination (when authored; zero until then)
  + u_kinematic_feasibility(g, vehicle, beliefs)   # can I actually reach g in lookAheadSeconds?
```

Each `u_*` is differentiable with respect to `g`. Gradients flow into Adam.

[NORMATIVE] The exact functional forms of the `u_*` terms are owned by this contract but elaborated incrementally as the simulator runs and tuning happens. v0.1.0 declares the slots; the math fills in during implementation.

### 6.3 Adam loop

[NORMATIVE] Per-tech, per `Operations` tick:

```
Initialize: g = previous_g (continuity); m = previous_m; v = previous_v; t = previous_t
beliefs = world.BeliefSnapshot.Read()
vehicle = own.VehicleModelSnapshot.Read()

For step in 1..K (K = 10 initial):
    t += 1
    ∇U = ∂Utility/∂g (analytic or autodiff)
    m = β1 * m + (1 - β1) * ∇U
    v = β2 * v + (1 - β2) * ∇U²
    m̂ = m / (1 - β1^t)
    v̂ = v / (1 - β2^t)
    g += lr * m̂ / (sqrt(v̂) + ε)

publishTacticalGoal(g)
previous_g = g; previous_m = m; previous_v = v; previous_t = t
```

[NORMATIVE] Hyperparameters: β1 = 0.9, β2 = 0.999, ε = 1e-8. Learning rate `lr` provisional at 0.1 (units depend on Utility's gradient scale; tuned during self-play).

[NORMATIVE] Tactical optimization runs on the main thread inside `Operations`. Target: sub-millisecond per tech for 10 steps. If the per-tech budget is exceeded, the current `g` is published as-is and the optimizer continues next tick.

### 6.4 Why per-Operations-tick, not per-frame

[RATIONALE] The tactical layer changes goals on a timescale slower than control. A 30 Hz tactical update (one per Operations tick) is enough; the MPC fills in between with reactive control. Running tactical at 60+ Hz wastes compute on indistinguishably different goals.

### 6.5 Goal reset

[NORMATIVE] On `TechSpawned` for a Smart-driven tech, `g` is initialized to the tech's current position with zero velocity and current heading. Adam state (m, v, t) is initialized to zero.

On a substantial belief change (e.g., new high-priority target acquired, target lost, tech retreating), the tactical optimizer's Adam state is reset (m=0, v=0, t=0) and `g` is re-seeded from the maneuver library (§7).

---

## SECTION 7: MANEUVER LIBRARY

### 7.1 Purpose

[NORMATIVE] The maneuver library is a set of named, parameterized trajectory primitives. Two uses:
1. **MPC warm-start seeds** when the warm-start from the previous solve is invalidated.
2. **Tactical goal seeds** when the tactical optimizer needs a fresh starting point.

### 7.2 Initial primitive set (v0.1.0)

[NORMATIVE — provisional]

| Name | Parameters | Approximate shape |
|---|---|---|
| `StraightLineApproach` | target_position, speed | Drive toward target at constant speed |
| `BankTurn` | turn_radius, direction | Constant-radius turn |
| `Strafe` | lateral_distance, target_direction | Sideways move while facing target |
| `HoldPosition` | (none) | Stay put |
| `Retreat` | escape_direction, distance | Move away from threats |
| `SCurveApproach` | target_position, amplitude | Serpentine path to target (harder to hit) |

[NORMATIVE] Each primitive returns either a control sequence (for MPC warm-start) or a `TacticalGoal` (for tactical seed). The set is intentionally small at v0.1.0; expand based on self-play insight (workflow step 1.11).

### 7.3 Composition

[NORMATIVE] Primitives can be chained (StraightLineApproach until distance < X, then Strafe). Composition logic lives in `ManeuverLibrary.cs`; consumers request named primitives or chains by id.

---

## SECTION 8: SMARTMOVEMENTCONTROLLER (OQ-10)

### 8.1 The actual interface (resolved from in-repo source)

[NORMATIVE] `IMovementAIController` is defined at [TAC_AI/AI/Contracts/IMovementAIController.cs](../../Contracts/IMovementAIController.cs). Smart's controller MUST implement all 15 members:

```csharp
// 7 properties
IMovementAICore AICore { get; }
Tank Tank { get; }
TankAIHelper Helper { get; }
Enemy.EnemyMind EnemyMind { get; }
Vector3 PathPoint { get; }
float GetDrive { get; }
bool Grounded { get; }

// 8 methods
void Initiate(Tank tank, TankAIHelper helper, Enemy.EnemyMind mind = null);
void UpdateEnemyMind(Enemy.EnemyMind mind);
void DriveDirector(ref EControlCoreSet core);
void DriveDirectorRTS(ref EControlCoreSet core);
void DriveMaintainer(ref EControlCoreSet core);
void OnMoveWorldOrigin(IntVector3 move);
Vector3 GetDestination();
void Recycle();
```

### 8.2 Implementation strategy

[NORMATIVE] Smart's `SmartMovementController` is a `MonoBehaviour` added to `helper.gameObject` via `AddComponent` (per the Vanilla pattern). It extends [`TAC_AI/AI/Movement/MovementControllerBase`](../../../Movement/MovementControllerBase.cs) — the same `internal abstract` reference base that `VanillaMovementController` extends. The base class provides default Tank/Helper/EnemyMind/AICore storage, an Initiate template that calls into `SelectCore`, GetDrive forwarding, OnPre/OnPost/OnRecycle hooks, and the `Recycle()` `DestroyImmediate` plumbing. Subclass overrides reduce to: `SelectCore`, `PathPoint`, `DriveDirector`, `DriveDirectorRTS`, `DriveMaintainer`, `OnMoveWorldOrigin`, `GetDestination` — seven members.

[NORMATIVE] `SmartMovementController.SelectCore(mind)` returns `null` — Smart's MPC does not use the per-vehicle-class `IMovementAICore` system. `MovementControllerBase.Initiate` null-guards a null core.

[NORMATIVE] Accessibility: `MovementControllerBase` is `internal`, but Smart's controller lives in the same assembly (`TAC_AI`), so accessibility is not a constraint. Smart's controller is also `internal` (mirrors Vanilla).

### 8.3 Per-member behavior (the 7 abstracts Smart overrides)

[NORMATIVE]

- **`SelectCore(mind)`** — returns `null`. Smart bypasses the per-vehicle-class core system.
- **`PathPoint`** — returns the next sample along the currently-published `ControlProfile`'s implied trajectory, OR Smart's current tactical-goal position when no profile exists, OR `Tank.boundsCentreWorldNoCheck` at v1.3 scaffold. Read by ManWorldRTS for path drawing per [SHELL-API-GUIDE.md §6](SHELL-API-GUIDE.md#section-6-tankaihelper-field-surface).
- **`DriveDirector(ref core)`** / **`DriveDirectorRTS(ref core)`** / **`DriveMaintainer(ref core)`** — at v1.3, no-ops. Future: mirror the latest MPC-derived intent to `EControlCoreSet` so any engine-side consumer reading the bus sees consistent state. The actual throttle/steer/brake commits flow through `ControlFrame` (§9), not through this bus; these writes are for engine-side bus consistency only.
- **`OnMoveWorldOrigin(move)`** — at v1.3, no-op. Future: apply the world-recenter offset to any cached world-space positions in Smart's per-tech state (planned-path samples, tactical goals).
- **`GetDestination()`** — returns `TacticalOptimizer`'s current goal position (the "where I want to be in N seconds" point), OR `Tank.boundsCentreWorldNoCheck` at v1.3 scaffold.

[INFORMATIVE] The non-abstract members (`AICore`, `Tank`, `Helper`, `EnemyMind`, `GetDrive`, `Grounded`, `Initiate`, `UpdateEnemyMind`, `Recycle`) are inherited from `MovementControllerBase` and do not need to be reimplemented. The base's `GetDrive` returns `AICore?.GetDrive ?? 0f` — which is `0f` for Smart because `AICore` is null. Override only if Smart needs a non-zero throttle reported to the shell's status text or downstream readers; v1.3 leaves the default `0f`.

### 8.4 Why three `Drive*` callbacks instead of one

[INFORMATIVE] The engine's per-tick flow calls all three: `DriveDirector` during normal AI, `DriveDirectorRTS` when RTS control is active, `DriveMaintainer` for the per-frame maintenance pass. Smart's writes to all three are the same in v0.1.0 (the MPC's latest output mirrored to `EControlCoreSet`); the three entry points exist so the engine can dispatch to the right one based on its tick phase. Smart MAY specialize per-phase in future revisions.

### 8.2 The shell-form interface obligation

[NORMATIVE] Per [SHELL-API-GUIDE.md §2.6](SHELL-API-GUIDE.md#26-v2-isolation-seams), the shell calls Smart's `CreateMovementController(helper, kind, mind)` from recalibration paths in `TankAIHelper.Controller.cs`. Smart's implementation:

```csharp
public IMovementAIController CreateMovementController(TankAIHelper helper, MovementContainerKind kind, EnemyMind mind)
{
    // Return the same shell instance regardless of kind; Smart's MPC is unified.
    if (helper.MovementController is SmartMovementController existing) {
        existing.Refresh(helper.tank, helper, mind);
        return existing;
    }
    IMovementAIController previous = helper.MovementController;
    var built = helper.gameObject.AddComponent<SmartMovementController>();
    built.Initiate(helper.tank, helper, mind);
    if (previous != null) previous.Recycle();
    return built;
}
```

(Pattern mirrors `VanillaForm.CreateMovementController`.)

### 8.3 The shell-form responsibilities the controller carries

[NORMATIVE] Even as a thin shell, `SmartMovementController` is what `helper.MovementController` resolves to. The shell may call methods on it via the `IMovementAIController` surface (e.g., for diagnostics, status text). Each method either:
- Returns inert defaults (e.g., `int` air-maneuver-state queries return 0).
- Reads from per-tech Smart state and returns derived values.

The implementation is small — typically one file, fewer than 100 lines.

---

## SECTION 9: CONTROLFRAME INTEGRATION

### 9.1 The publish-consume protocol

[NORMATIVE] MPC publishes a `ControlProfile` to a per-tech `DoubleBuffer<ControlProfile>`. `ControlFrame` reads it.

```
public sealed class ControlProfile
{
    public readonly long ValidFromTick;   // tick stamp at which step[0] applies
    public readonly long ValidThroughTick; // last tick covered
    public readonly IReadOnlyList<ControlVector> Steps;  // per-tick controls; length = H
}

public readonly struct ControlVector
{
    public readonly float Throttle;       // [-1, +1]
    public readonly float Steer;          // [-1, +1]
    public readonly float Brake;          // [0, 1]
    public readonly IReadOnlyList<bool> WeaponFireCommits;  // per-weapon fire commit; populated per §10
    // ... per-mount-point thrust enables for techs with discrete propulsion choices ...
}
```

### 9.2 Per-frame consumption

[NORMATIVE] In `SmartForm.ControlFrame(helper, tankControl)`:

1. Read the latest `ControlProfile` from the per-tech buffer.
2. If profile is null (first-frame, before any MPC solve has completed) OR stale (`currentTick > profile.ValidThroughTick`): write neutral control to `tankControl` (no throttle, no steer, brake on) and return.
3. Compute the step index: `index = (currentTick - profile.ValidFromTick).Clamp(0, profile.Steps.Length - 1)`.
4. Read `profile.Steps[index]`.
5. Write `tankControl.Throttle = step.Throttle`, etc. (Exact `TankControl` write API determined at implementation time by reading existing movement controllers — see [SHELL-API-GUIDE.md OQ-5](SHELL-API-GUIDE.md#section-12-open-questions-consolidated).)

[NORMATIVE] `ControlFrame` MUST be O(1) — read, lookup, write. No solver work; no allocations on the hot path; per [FORM-SPECIFICATION.md §5.8](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives).

### 9.3 Stale profile policy

[NORMATIVE] If `currentTick > profile.ValidThroughTick`, MPC has not produced a new profile in time. Two cases:
- Brief stale (1–2 ticks beyond ValidThroughTick): extrapolate using the last step (hold last control).
- Sustained stale (> 5 ticks beyond): something is wrong — log a `WARNING` via the Diagnostics surface and fall back to neutral control.

[NORMATIVE] Sustained stale should be exceedingly rare; if it happens often, MPC is starved (Diagnostics surfaces this as starved-frame events).

### 9.4 Host vs client

[NORMATIVE] Per [ARCHITECTURE.md §3.2](ARCHITECTURE.md#32-mp-hostclient-gating), `ControlFrame` runs on both host and client. On client, the `ControlProfile` buffer is empty (MPC didn't run); the implementation falls back to "consume whatever the engine replicated to `tankControl`" — effectively, ControlFrame is a no-op on client because TerraTech's net layer already replicated the host's TankControl.

---

## SECTION 10: WEAPON FIRE CONTROLLER

### 10.1 Purpose

[NORMATIVE] The weapon-fire controller populates `ControlVector.WeaponFireCommits` each frame. One commit boolean per weapon block on the tech; `true` = "fire this weapon this frame."

[NORMATIVE] The controller runs in the same Operations-tick flow as the MPC and tactical optimizer. Fire decisions are computed each Operations tick and embedded in the published `ControlProfile`; `ControlFrame` reads the per-frame commit booleans like any other control component.

### 10.2 Inputs

Per Operations tick:
- Current `KinematicState` for own tech (from Vehicle).
- `BeliefSnapshot` from World (for target predictions).
- `CoordinationState.TargetMap` from Coordination (assigned target per tech).
- Per-weapon `WeaponProfile` from Vehicle (cooldown, ammo, arc, projectile velocity, mount, fire rate, damage).
- Multi-weapon coordination state (own-internal; persists between ticks).

### 10.3 Lead computation (linear Kalman + Learning slot)

[NORMATIVE] For each weapon `w` aimed at target `t`, compute lead:

```
projectileFlightTime = distance(weapon.mountWorld, t.beliefMean.position) / weapon.projectileVelocity
predictedPosition    = t.beliefMean.position + t.beliefMean.velocity * projectileFlightTime
                     + LearnedResidual(t.id, projectileFlightTime)    // zero until Learning
requiredAimDirection = (predictedPosition - weapon.mountWorld).normalized
```

[NORMATIVE] `LearnedResidual(targetId, dt)` is a slot for Learning's trajectory residual model (per [LEARNING-CONTRACT.md §3.3](LEARNING-CONTRACT.md)). Returns zero at Control v0.2.0. When Learning lands and the residual model is trained on player-specific motion habits, this correction activates without requiring a Control contract revision — the slot interface is stable.

[NORMATIVE] The weapon is "aimed" if `requiredAimDirection` is within its arc cone given current tech pose. Arc cone shape from `WeaponProfile.ForwardDirectionLocal`, `YawArcRadians`, `PitchArcRadians`; tech pose from `KinematicState.HeadingWorld`.

### 10.4 Per-weapon fire decision

[NORMATIVE] Weapon `w` fires this frame iff ALL of:

1. **Aimed:** §10.3's aim check passes.
2. **Cooldown ready:** `w.CooldownRemaining <= 0`.
3. **Ammo available:** `w.AmmoCurrent > 0`.
4. **Target alive:** `t` still present in `BeliefSnapshot` with `HealthFraction > 0`.
5. **Friendly fire clear:** §10.7 check passes.
6. **Energy available:** for energy weapons, §10.8 check passes.
7. **Coordination condition met:** §10.5 check passes.

[NORMATIVE] If any check fails, `WeaponFireCommits[w.index] = false` for this frame.

### 10.5 Multi-weapon coordination (hybrid)

[NORMATIVE] Each weapon has a `FireMode` derived from its `WeaponProfile` at VehicleModel build time:

```
FireMode = Continuous if (w.FireRateHz > 2.0 AND w.DamagePerProjectile < damageThreshold)
        else Salvo    if (w.FireRateHz < 1.0 OR w.DamagePerProjectile > damageThreshold)
        else Continuous   // default
```

Where `damageThreshold` is provisional (~50 damage units, tuned during self-play).

[NORMATIVE] `Continuous` weapons fire whenever conditions 1–6 in §10.4 are met. They want sustained DPS; no coordination requirement.

[NORMATIVE] `Salvo` weapons fire only when:
- This weapon's conditions 1–6 are met, AND
- At least N other `Salvo` weapons on this tech are simultaneously ready and aimed at the same target. N is provisional: 2 for techs with 2–4 salvo weapons; 3 for techs with 5+.

[NORMATIVE] The coordination check builds a "ready salvo set" each Operations tick (the subset of salvo weapons that pass conditions 1–6 against the assigned target). If `|ready_salvo_set| >= N`, all weapons in the set fire this frame. Otherwise they hold.

[RATIONALE] Hybrid coordination balances sustained DPS (continuous weapons keep firing) with burst damage (salvo weapons coordinate for higher single-tick damage that disrupts movement and may overwhelm armor regeneration).

### 10.6 Target switching hysteresis

[NORMATIVE] Per weapon, maintain a `CurrentTargetId` and `TicksSinceLastSwitch` counter. The current target is sticky:

```
ShouldSwitchTarget(w, candidate_target) =
    expectedValue(w, candidate_target) > expectedValue(w, w.CurrentTargetId) * 1.3
    AND TicksSinceLastSwitch > 30
```

Where `expectedValue(w, t) = damagePerShot * hitProbability(w, t) * targetThreatRating(t)`. `hitProbability` is a heuristic from arc fit, lead error, and range; threat rating from VehicleModel.

[NORMATIVE] The 1.3× threshold + 30-tick minimum sticky duration prevents per-frame target oscillation when two targets are roughly equivalent. Smart commits.

[NORMATIVE] When the Coordination subsystem reassigns the tech's primary target (via `CoordinationState.TargetMap`), per-weapon hysteresis is OVERRIDDEN — the weapon switches to the new assigned target immediately. Hysteresis is for *within-engagement* opportunistic switching, not for top-down strategic reassignment.

### 10.7 Friendly fire avoidance

[NORMATIVE] Before commiting fire on weapon `w` against `requiredAimDirection`:

```
maxRange    = min(w.Range, distance(w.mountWorld, predictedPosition))
clearLength = TerrainMap.RaycastSegment(w.mountWorld, w.mountWorld + requiredAimDirection * maxRange)
if (any friendly tech bounding-box intersects the ray within clearLength
    AND the intersection is closer than the target):
    suppress fire (set WeaponFireCommits[w.index] = false)
```

[NORMATIVE] Tolerance: if the friendly is offset from the ray by more than `1.5 * w.projectileSpread`, the shot is considered safe.

[NORMATIVE] Friendly-fire raycast is O(N_friendly) per weapon check. For typical squads, negligible. If profile shows it dominating, cache by (mount, direction) within a frame.

### 10.8 Energy controller (embedded)

[NORMATIVE] For energy weapons (subset of `Salvo` or `Continuous` modes; flagged by `WeaponProfile.IsEnergyWeapon`), fire commits also consume from a per-tech energy budget.

```
public sealed class EnergyState
{
    public float ReserveFraction;          // [0..1] of capacity
    public IReadOnlyList<EnergyDraw> ScheduledDraws;  // commitments this tick
}

public readonly struct EnergyDraw
{
    public readonly int WeaponIndex;
    public readonly float CostFraction;     // fraction of capacity consumed
    public readonly int Priority;           // higher = preferred under contention
}
```

[NORMATIVE] Each Operations tick:
1. Read current `EnergyState.ReserveFraction` from Vehicle's energy estimate (or directly from `Tank` if exposed).
2. Build list of pending energy-weapon fires (from §10.4–10.6 outputs).
3. Sort by priority (salvo > continuous; high-damage > low-damage).
4. Greedily commit fires while running budget remains; suppress lower-priority commits when budget exhausted.

[NORMATIVE] Priority is derived: `priority = (FireMode == Salvo ? 100 : 0) + DamagePerProjectile`. Higher number fires first.

[NORMATIVE] Reserve is conserved when `ReserveFraction < 0.2` AND combat is ongoing: only the highest-priority energy weapon fires; lower-priority energy weapons hold. This gives Smart's energy weapons a fallback "save for crit moment" behavior.

### 10.9 Ammo conservation

[NORMATIVE] Per weapon, when `w.AmmoCurrent / w.AmmoCapacity < 0.3` AND at least one hostile is in BeliefSnapshot with `InSight == true`:

- `Continuous`-mode weapons: reduce effective fire rate by half (every other tick they're allowed; the alternating tick gets `false` commit even when all checks pass).
- `Salvo`-mode weapons: increase the coordination requirement N by 1 (require more weapons simultaneously aligned before firing the salvo).

[NORMATIVE] This is best-effort preservation; not a hard cap. The threshold and reduction factors are provisional per [FORM-SPECIFICATION.md §1 disclaimer](FORM-SPECIFICATION.md); tuned during self-play.

---

## SECTION 11: FILE LAYOUT

[NORMATIVE] Control's source files split between `TAC_AI/AI/Forms/Smart/Control/` and `TAC_AI/AI/Forms/Smart/` (root):

| File | Location | Owns |
|---|---|---|
| `ContinuousController.cs` | `Control/` | Per-tech orchestration; Operations-tick flow; ControlFrame consumption protocol. |
| `SamplingMPC.cs` | `Control/` | MPPI loop; sample generation; importance-weighted aggregation; warm-start management. |
| `PhysicsRollout.cs` | `Control/` | Per-step Newton-Euler integration; thrust map application; airplane/hover specializations. |
| `CostFunction.cs` | `Control/` | Multi-objective cost; per-term computation; weight management. |
| `TacticalOptimizer.cs` | `Control/` | Adam loop; utility function; per-tech state (m, v, t). |
| `ManeuverLibrary.cs` | `Control/` | Named primitive set; composition; warm-start factory. |
| `WeaponFireController.cs` | `Control/` | Lead computation, per-weapon fire decisions, multi-weapon coordination, target switching hysteresis, friendly-fire avoidance, embedded energy management, ammo conservation. |
| `SmartMovementController.cs` | `Smart/` (root) | Thin `IMovementAIController` shell. Lives at the form root mirroring `VanillaMovementController.cs`'s placement next to `VanillaForm.cs`. |

Seven files in `Control/` + one at root. Justification: each owns one coherent concern. `SmartMovementController` sits at the form root because it is the form-side shell that satisfies the shell's `IMovementAIController` invariant — symmetric placement with `VanillaMovementController.cs` in `Forms/Vanilla/`. Energy management is embedded in `WeaponFireController.cs` (rather than a separate `EnergyController.cs`) because energy weapons firing IS what energy is for — tight coupling, shared per-tick state, and a clean cost-of-living check ("is this energy weapon firing?") that benefits from inline access to the fire decisions.

---

## SECTION 12: DIAGNOSTICS INTEGRATION

[NORMATIVE] Control exposes the following diagnostic events:

- `MPCDispatched(TechId id, long tickStamp)` — when an MPC solve is enqueued.
- `MPCCompleted(TechId id, int samplesEvaluated, TimeSpan duration, float bestCost)` — when MPC publishes.
- `MPCCancelled(TechId id, int samplesEvaluated, float partialBestCost)` — when budget cuts solve short.
- `TacticalGoalUpdated(TechId id, TacticalGoal previous, TacticalGoal next)` — after each Operations tick.
- `ControlProfileStale(TechId id, long ticksStale)` — when ControlFrame falls back to extrapolation or neutral.
- `WeaponAlignmentTriggered(TechId id, WeaponId wid, float angleFromArc)` — when the cost penalty fires significantly.
- `WeaponFired(TechId id, WeaponId wid, TechId targetId, float predictedLead)` — per fire commit.
- `WeaponSuppressedFriendlyFire(TechId id, WeaponId wid, TechId blockingFriendly)` — friendly-fire raycast hit.
- `WeaponTargetSwitched(TechId id, WeaponId wid, TechId previousTarget, TechId newTarget, float valueRatio)` — when hysteresis allows a switch.
- `EnergyReserveDepleted(TechId id, int weaponsSuppressed)` — energy controller suppresses fires.
- `AmmoConservationActive(TechId id, WeaponId wid, float ammoFraction)` — ammo-conservation gating in effect.

[NORMATIVE] These flow into Diagnostics when authored. Until then, Threading's default-handler discipline applies.

---

## SECTION 13: OPEN ITEMS

[OPEN] **MPC sample count N.** 128 provisional; tune from measurement.
[OPEN] **Horizon length H.** 20 steps × 0.05 s = 1.0 s provisional; within FORM-SPEC §1.2's 0.5–2 s range. Tune.
[OPEN] **MPPI temperature λ.** 1.0 provisional.
[OPEN] **Sampling covariance Σ.** Per-control-dimension variance values provisional.
[OPEN] **Cost-function weights w_*, γ_*, α, β.** All provisional per §5.6; major tuning target for self-play (step 1.11).
[OPEN] **Tactical learning rate.** 0.1 provisional; depends on Utility's gradient scale.
[OPEN] **Number of tactical gradient steps K per tick.** 10 provisional; sub-millisecond budget per tech.
[OPEN] **`TankControl` write API specifics.** Per [SHELL-API-GUIDE.md OQ-5](SHELL-API-GUIDE.md#section-12-open-questions-consolidated). Resolved at implementation time by reading existing movement controllers under `TAC_AI/AI/Movement/`.
[OPEN] **`IMovementAIController` exact interface.** Per OQ-10. Resolved at implementation time.
[OPEN] **Maneuver library expansion.** v0.1.0 ships 6 primitives; self-play will reveal which others are needed.
[OPEN] **Per-vehicle-type specialization triggers.** When to specialize the rollout (e.g., dedicated airplane vs hover paths). Pinned at implementation when concrete inaccuracies are observed.
[OPEN] **Salvo coordination N.** 2–3 provisional based on salvo-weapon count.
[OPEN] **Salvo/continuous fire-mode threshold.** Damage threshold ~50 units provisional.
[OPEN] **Target switching hysteresis ratio.** 1.3× provisional.
[OPEN] **Sticky duration.** 30 ticks (~1 sec) provisional.
[OPEN] **Friendly fire raycast tolerance.** 1.5 × projectile spread provisional.
[OPEN] **Energy reserve threshold.** 0.2 fraction provisional.
[OPEN] **Ammo conservation threshold.** 0.3 fraction provisional.
[OPEN] **`Tank` energy accessor.** Whether the engine's `Tank` directly exposes a battery / energy fraction or whether Smart must estimate from observed consumption. Resolved at implementation time by reading existing TAC AI weapon code under `TAC_AI/AI/TankAIHelper.WeaponFire.cs`.

---

## SECTION 14: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not Smart's strategic decision making. Tactical optimizer takes a "what do I want at horizon end" — but *what makes one goal better than another* is informed by belief state, vehicle capabilities, and team coordination. The high-level strategic plan that picks "engage this enemy" or "retreat to that position" is Planning's responsibility (step 1.7).

This contract is not Smart's pathing. The threat-field cost term references threats Pathing computes; this contract does not own how the threat field is built.

This contract is not the high-level target assignment. Coordination's Hungarian solve owns which tech engages which enemy; the weapon-fire controller's per-weapon target switching (§10.6) is for opportunistic within-engagement decisions, not for top-down team-level assignment changes.

This contract is not the learned trajectory residual model. §10.3 references `LearnedResidual(targetId, dt)` as a slot — the model itself is owned by [LEARNING-CONTRACT.md](LEARNING-CONTRACT.md) when authored.

This contract does not own the existing TerraTech movement controllers (`AIControllerDefault`, `AIControllerAir`, `AIControllerStatic`). Smart bypasses them entirely (OQ-6 resolution); they are not modified by Smart.

This contract does not own TerraTech's weapon-block engine code. Smart commits "fire this weapon" via `TankControl`; the engine handles the actual projectile spawn, damage application, cooldown countdown, and ammo decrement. Smart observes the resulting state (cooldown, ammo) through the next VehicleModel snapshot.

---

END OF CONTROL-CONTRACT.md v0.1.0
