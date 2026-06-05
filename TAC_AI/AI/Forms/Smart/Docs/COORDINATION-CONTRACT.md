# COORDINATION-CONTRACT.md

**Subsystem:** Coordination/
**Form:** Smart
**Version:** 0.1.0
**Status:** AUTHORITATIVE — Defines Smart's team belief fusion (per-friendly LOS aggregation), target assignment (Hungarian), role assignment, per-tech plan decomposition, and the in-team comms bus.

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The per-friendly-tech LOS computation and its aggregation into the shared belief.
- The target assignment algorithm (Hungarian) and the cost matrix construction.
- The role assignment from team plan + tech capabilities + targets.
- The per-tech decomposition that turns a team-level `StrategicPlan` into per-tech `TacticalGoal`s.
- The plan implementations for each plan in [PLANNING-CONTRACT.md §6](PLANNING-CONTRACT.md#section-6-plan-library).
- The in-team comms bus and its message catalog.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals.
- [ARCHITECTURE.md](ARCHITECTURE.md) for threading model, MP host/client gating, cross-subsystem invariants.
- [WORLD-CONTRACT.md](WORLD-CONTRACT.md) for `BeliefState` shape, Kalman update mechanism, event bus.
- [VEHICLE-CONTRACT.md](VEHICLE-CONTRACT.md) for per-tech capability data the cost matrix consumes.
- [PLANNING-CONTRACT.md](PLANNING-CONTRACT.md) for the `StrategicPlan` shape and the plan library names.
- [CONTROL-CONTRACT.md](CONTROL-CONTRACT.md) for the `TacticalGoal` shape that decomposition produces.
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for worker pool, double-buffer, marshalling.

**This document GOVERNS:**
- The per-tech `TacticalGoal` publication that Control's tactical optimizer consumes.
- The observation injection into World's perception worker (per-friendly LOS becomes observations).

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What Coordination owns

Five coupled concerns:
1. **Team belief fusion** — aggregate per-friendly observations into the shared belief.
2. **LOS computation** — per friendly tech, per known enemy, current line of sight.
3. **Target assignment** — Hungarian matching of our techs to enemy targets given the current strategic plan.
4. **Role assignment** — given the plan + targets + tech capabilities, assign roles (pursuer, flanker, holder, etc.) per tech.
5. **Per-tech plan decomposition** — for each tech, produce the `TacticalGoal` that Control consumes.

### 1.2 Decisions resolved at v0.1.0

[NORMATIVE]
- **LOS model: per-friendly-tech aggregated** into shared belief via sequential Kalman updates (§3).
- **Target assignment: Hungarian** algorithm. Cost matrix encodes range-fitness, weapon coverage, threat, sticky bias, and plan-specific priorities (§4).
- **Role assignment: rule-based at v0.1.0.** Roles derived from `StrategicPlan` + target assignment + per-tech mobility (§5).
- **Plan decomposition: per-plan dispatch.** Each plan name in the library has a decomposition function (§6). Decomposition is deterministic given inputs.
- **Comms bus: synchronous typed message bus.** Same shape as World's event bus but separate (§7).

---

## SECTION 2: THE COORDINATION STATE OBJECT

[NORMATIVE] Coordination maintains a per-team state object updated each strategic tick:

```
public sealed class CoordinationState
{
    public readonly long TickStamp;
    public readonly StrategicPlan ActivePlan;            // from PLANNING-CONTRACT
    public readonly IReadOnlyDictionary<TechId, TargetAssignment> TargetMap;
    public readonly IReadOnlyDictionary<TechId, Role> RoleMap;
    public readonly IReadOnlyDictionary<TechId, IReadOnlyList<TechId>> LOSCoverage;
    // LOSCoverage[techA] = list of enemies that techA currently has LOS to.
}

public readonly struct TargetAssignment
{
    public readonly TechId TargetId;
    public readonly float AssignmentCost;
    public readonly bool StickyToPreviousTick;  // was previous tick's target preserved?
}
```

[NORMATIVE] `CoordinationState` is published via a team-level `DoubleBuffer<CoordinationState>` that Control reads (for per-tech `TacticalGoal` lookup) and that Diagnostics reads (for team-level visualization).

---

## SECTION 3: TEAM BELIEF FUSION

### 3.1 Per-friendly LOS

[NORMATIVE] Each strategic tick, Coordination computes:

```
For each friendly tech F in our team:
    For each tracked enemy E in BeliefSnapshot:
        hasLOS(F, E) = CheckLineOfSight(F.position, E.beliefMean, terrain, occluders)
```

[NORMATIVE] `CheckLineOfSight` uses TerraTech's `ManVisible` tracked-set for the primary path: if `F`'s tank has `E`'s tank in its `Visible` set, `hasLOS(F, E) = true`. Fallback (when `ManVisible` is unavailable, e.g., for techs out of `Visible`'s range): physics raycast from `F.position` toward `E.beliefMean`.

[NORMATIVE] LOS computation runs on the main thread (it reads engine objects). Results are stored in `CoordinationState.LOSCoverage`.

### 3.2 Observation injection

[NORMATIVE] When `hasLOS(F, E) == true` for the first time this tick (or any time during the tick if it transitions false → true), Coordination injects an observation into World's perception worker for E:

```
observation = {
    observerId: F.Id,
    targetId: E.Id,
    targetPosition: GetActualPosition(E.tank),  // ground truth from engine
    targetVelocity: GetActualVelocity(E.tank),
    targetHeading: GetActualHeading(E.tank),
    timestamp: currentTick
}
WorldEventBus.Publish(new EnemyObserved(observation))
```

[NORMATIVE] World's perception worker subscribes to `EnemyObserved` events and feeds them through the Kalman update pipeline. Multiple observations in the same tick from different friendlies result in sequential Kalman updates on the same belief — the fusion is automatic at the World layer.

### 3.3 The aggregate signal

[NORMATIVE] An enemy E is considered "in sight" by the team iff `∃F : hasLOS(F, E)`. This boolean is published in `BeliefSnapshot` per [WORLD-CONTRACT.md §2.1](WORLD-CONTRACT.md#section-2-belief-state) (`BeliefState.InSight`).

[NORMATIVE] When no friendly has LOS, the belief enters decay per [WORLD-CONTRACT.md §4](WORLD-CONTRACT.md#section-4-decay-model-out-of-sight) — variance grows, velocity damps. The team has lost visual on E.

### 3.4 Emergent scout behavior

[RATIONALE] This LOS model gives scouts implicit value: a tech positioned in cover that gets LOS on an otherwise-hidden enemy contributes the only observation feeding the team's belief about that enemy. The PUCT planner sees this benefit indirectly through the value function — a state where more enemies are "in sight" has higher `WeaponCoverage` and better-converged belief covariances feeding `ThreatExposure`.

---

## SECTION 4: TARGET ASSIGNMENT (HUNGARIAN)

### 4.1 The matrix

[NORMATIVE] Each strategic tick, build a cost matrix `C[i][j]`:
- Rows `i` = our techs (count: N_friendly).
- Columns `j` = candidate enemy targets (count: N_hostile_in_sight + N_hostile_recently_lost).

`C[i][j]` = cost for tech `i` to be assigned target `j`. Lower is better.

### 4.2 Cost terms

[NORMATIVE] Total cost is a weighted sum:

```
C[i][j] =
    c_range    * RangePenalty(tech_i, target_j)
  + c_weapon   * WeaponCoveragePenalty(tech_i, target_j)
  + c_threat   * ThreatPenalty(tech_i, target_j)
  + c_sticky   * (target_j == previousAssignment[tech_i] ? -1.0 : 0.0)
  + c_plan     * PlanPriorityPenalty(plan, tech_i, target_j)
  + c_overlap  * AssignmentOverlapPenalty(tech_i, target_j, partial_assignment)
```

[NORMATIVE] Term descriptions:
- **RangePenalty**: Gaussian-like penalty centered on tech_i's preferred engagement range; reaches max when target is far outside that range.
- **WeaponCoveragePenalty**: tech_i has weapons that can hit target_j given current pose? Yes → 0; no → large penalty.
- **ThreatPenalty**: how much will target_j hurt tech_i if engagement starts? Higher target ThreatRating + lower tech_i HealthFraction = higher cost.
- **StickyBonus**: if target_j was tech_i's target previous tick, reduce cost by `c_sticky`. Encourages target persistence; reduces oscillation.
- **PlanPriorityPenalty**: plan-specific bias. `EngageFocused`'s primary target gets large negative penalty for the lead tech; secondary techs get positive penalty unless they're flankers. Per-plan logic in §6.
- **AssignmentOverlapPenalty**: discourages multiple techs piling on same target in `EngageDistributed`; encourages it in `EngageFocused`.

[NORMATIVE] Weights (provisional per FORM-SPEC §1 disclaimer):
- `c_range = 1.0`
- `c_weapon = 5.0` (binary feasibility dominates)
- `c_threat = 1.0`
- `c_sticky = 0.3`
- `c_plan = 2.0`
- `c_overlap = 1.5`

### 4.3 The matching

[NORMATIVE] Run the Hungarian algorithm on `C` to find the assignment minimizing total cost. Standard O(n³) implementation; trivial at typical team sizes.

[NORMATIVE] Handle non-square matrices (more techs than targets, or vice versa): pad with virtual "no target" entries at cost `c_no_target = 0.5 * c_weapon` (cheaper than firing on a target you can't hit; more expensive than not engaging at all).

[NORMATIVE] After matching, publish `TargetMap` to `CoordinationState`.

### 4.4 Reassignment frequency

[NORMATIVE] Target assignment recomputes every strategic tick. Between ticks, per-tech control follows the assignment from the last tick's state. Rapid target oscillation is dampened by the sticky-bonus term.

[NORMATIVE] On significant events (target lost, target killed, new high-priority target appears), Coordination MAY recompute mid-tick — this is published as a `TargetMapUpdated` event on the comms bus (§7).

---

## SECTION 5: ROLE ASSIGNMENT

### 5.1 Role catalog

[NORMATIVE] At v0.1.0, six roles:

| Role | Description |
|---|---|
| `Pursuer` | Closes on assigned target; main engagement |
| `Flanker` | Approaches assigned target from a side angle |
| `Holder` | Maintains position; provides covering fire |
| `Retreater` | Withdraws from engagement |
| `Scout` | Extends LOS toward unobserved enemies |
| `Support` | Stays close to a Pursuer/Flanker; provides backup fire |

### 5.2 Assignment logic

[NORMATIVE] At v0.1.0, role assignment is **rule-based**:

```
For each tech t with assigned target:
    If active plan is Retreat / Disengage / FightingRetreat:
        role[t] = Retreater
    Else if t.MobilityProfile.TopSpeed > team_mean * 1.2:
        role[t] = Flanker (or Scout if no engaged enemy in sight)
    Else if t.MobilityProfile.TippingSusceptibility > 0.5:
        role[t] = Holder
    Else:
        role[t] = Pursuer

For each tech t without assigned target:
    If active plan needs scouting AND team has unobserved enemies:
        role[t] = Scout
    Else if a Pursuer or Flanker is nearby:
        role[t] = Support
    Else:
        role[t] = Holder
```

[NORMATIVE] Roles are published in `CoordinationState.RoleMap`.

[OPEN] Role assignment may become learning-augmented post-v1 (Learning subsystem). The slot is `LearnedRoleAdjustment` — when present, it modifies the rule-based output. Zero at v0.1.0.

---

## SECTION 6: PLAN DECOMPOSITION

[NORMATIVE] Each plan in [PLANNING-CONTRACT.md §6](PLANNING-CONTRACT.md#section-6-plan-library) has a decomposition function in Coordination. The function takes:
- The `StrategicPlan` (with parameters).
- `CoordinationState` (target assignment, role assignment, LOS map).
- `BeliefSnapshot` (target positions, velocities, intents).
- Per-tech `VehicleModelSnapshot` (capabilities).

And produces per-tech `TacticalGoal` (the input to Control's tactical optimizer):

```
TacticalGoal {
    Vector3 position;           // where to be
    float heading;              // which way to face
    Vector3 velocity;           // approach vector when arriving
    float lookAheadSeconds;     // typically matches MPC horizon ~1 sec
}
```

### 6.1 EngageFocused

```
For the assigned-as-Pursuer tech: position = engagement range from target along current line; heading toward target.
For Flanker techs: position = engagement range from target at flank angle (±90°); heading toward target.
For Holder techs: position = current position; heading toward target (covering fire).
For Support techs: position = midpoint between Pursuer and own current position; heading toward target.
```

### 6.2 EngageDistributed

Each tech approaches its independently-assigned target at its preferred engagement range.

### 6.3 Flank(target, side)

Pursuer drives toward target along current axis. All non-Holder techs swing to the specified side (±90° offset from line to target) at flanking distance (1.5× engagement range).

### 6.4 DefensivePerimeter

Techs distribute around the center at distance `radius`. Spacing maintains overlapping weapon coverage. Heading outward.

### 6.5 MobileScreen

Techs distribute laterally perpendicular to `direction`. All advance at `advance_rate`. Heading along advance vector with weapons facing forward.

### 6.6 FightingRetreat

All Retreater techs move along `escape_direction` at their top speed. Heading is opposite to escape direction (facing the engagement). The slowest tech sets pace; faster techs hold formation.

### 6.7 Disengage

All techs head toward `rendezvous_position` at top speed. Heading along velocity vector (no engagement). Skip weapons coverage.

### 6.8 Bait

Bait tech (assigned the high-threat target) drives toward target at slow speed, drawing fire. Remaining techs move into flank positions at 90° offset; activate engagement once bait tech reaches a "trigger" distance.

### 6.9 Hold

All techs stop. Heading stays at current. No engagement intent.

### 6.10 Skirmish

Per-tech: engage at max engagement range; retreat to `1.5 × max range` when shot at; advance when shot stops.

[NORMATIVE] The decomposition functions are pure functions of inputs. No side effects. Easy to test in isolation. Each function lives in `PlanDecomposition.cs` as a method named `Decompose<PlanName>`.

---

## SECTION 7: IN-TEAM COMMS BUS

### 7.1 The bus

[NORMATIVE] Coordination owns a Smart-internal comms bus separate from World's event bus. Same shape: typed pub/sub with synchronous main-thread dispatch.

```
public static class CoordinationCommsBus
{
    public static void Subscribe<TMsg>(Action<TMsg> handler) where TMsg : struct;
    public static void Unsubscribe<TMsg>(Action<TMsg> handler) where TMsg : struct;
    public static void Publish<TMsg>(in TMsg msg) where TMsg : struct;
}
```

### 7.2 Why a separate bus

[RATIONALE] World's event bus carries observation events (what we see). Coordination's comms bus carries team-state events (what we're collectively doing). Keeping them separate clarifies subscription intent — a subscriber that wants "the team's current target map" subscribes only to Coordination, not to the noisier event stream.

### 7.3 Message catalog (initial)

| Message | Payload | Published when |
|---|---|---|
| `TargetMapUpdated` | new `TargetMap` | Coordination publishes assignment |
| `RoleMapUpdated` | new `RoleMap` | Role assignment changed |
| `PlanDecomposed` | active `StrategicPlan` + decomposition timestamp | Plan changed and decomposition ran |
| `LOSGained` | `(observerId, targetId)` | A friendly gained LOS on a previously-unseen enemy |
| `LOSLost` | `(observerId, targetId)` | A friendly lost LOS on an enemy |
| `EngagementWindow` | `(targetId, durationSeconds)` | A bait/flank trigger window opened |

---

## SECTION 8: ORCHESTRATION

### 8.1 The coordinator instance

[NORMATIVE] One `Coordinator` exists per team. It owns the `CoordinationState` double-buffer.

### 8.2 Strategic tick flow

[NORMATIVE] Each strategic tick (driven by [PLANNING-CONTRACT.md](PLANNING-CONTRACT.md)'s publication of a new `StrategicPlan`):

1. **Update LOS** for every (friendly, enemy) pair (§3.1). Inject observations into World event bus.
2. **Build cost matrix** for target assignment (§4).
3. **Solve Hungarian** to produce `TargetMap`.
4. **Assign roles** (§5) using updated `TargetMap` + plan.
5. **Decompose plan** (§6) into per-tech `TacticalGoal`s. Publish each to Control's per-tech `DoubleBuffer<TacticalGoal>` (consumed in Control §2).
6. **Publish new `CoordinationState`** to the team-level double-buffer.
7. **Fire comms-bus events** for substantive changes.

[NORMATIVE] Total per-tick work is O(N_friendly × N_hostile) for the cost matrix + O((max(N_friendly, N_hostile))³) for Hungarian. At typical scales, sub-millisecond.

### 8.3 Host-only

[NORMATIVE] Coordination runs only when `host == true`. Clients receive replicated control state from the host (host-authority per OD-7).

---

## SECTION 9: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/Coordination/` contains five files:

| File | Owns |
|---|---|
| `Coordinator.cs` | Orchestration, tick flow, `CoordinationState` publication. |
| ~~`TeamBelief.cs`~~ | **REV 7 P5 Item 22:** DELETED (orphaned — zero `new TeamBelief(` callers). LOS aggregation inlined in `TeamRuntime.BuildTeamSnapshot` at `SmartRuntime.cs:401-429`; comms-bus interface is `WorldEventBus` directly. |
| `LineOfSight.cs` | Per-friendly LOS computation (`ManVisible` lookup, raycast fallback). Self-mask fix in REV 7 P5 prerequisite (`RaycastAll` + skip own colliders). |
| `LineOfSightProducer.cs` | **REV 7 P5 Item 23:** main-thread producer (round-robin observer cursor, 64-rays-per-frame cap, 150ms cadence). Per-team `DoubleBuffer<Dictionary<TechId, List<TechId>>>` published snapshot consumed by `TeamRuntime.BuildTeamSnapshot`. |
| `TargetAssignment.cs` | Cost matrix construction + Hungarian solver. |
| `RoleAssignment.cs` | Rule-based role assignment + slot for learned adjustment. |
| `PlanDecomposition.cs` | Per-plan `Decompose<PlanName>` functions. |

Six files (REV 7 P5: TeamBelief deleted, LineOfSightProducer added — same count). Within [FORM-SPEC §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives)'s range.

---

## SECTION 10: DIAGNOSTICS INTEGRATION

[NORMATIVE] Coordination exposes:

- `CoordinationTickCompleted(int losPairsComputed, TimeSpan duration)` — per strategic tick.
- `LOSGained(TechId observer, TechId target)` — observation event (same as comms bus, also for Diagnostics).
- `LOSLost(TechId observer, TechId target)` — sight lost event.
- `TargetReassigned(TechId tech, TechId previousTarget, TechId newTarget, float costDelta)` — when matching shifts.
- `RoleSwitched(TechId tech, Role previous, Role next)` — role change.
- `HungarianCost(float totalCost, int matrixSize)` — solver statistics.

---

## SECTION 11: OPEN ITEMS

[OPEN] **Cost matrix weights.** All provisional per §4.2; major self-play tuning target.
[OPEN] **Role assignment rules.** Rule-based at v0.1.0; consider learning-augmentation post-v1.
[OPEN] **Plan decomposition specifics.** Each `Decompose<PlanName>` body has provisional logic; tune based on self-play observation.
[OPEN] **Comms bus message catalog expansion.** 6 initial messages; expand as needed.
[OPEN] **LOS raycast cost.** May need throttling at high target counts. Profile.
[OPEN] **Strategic vs tactical tick alignment.** Coordination runs per strategic tick (200–500 ms); Control's tactical optimizer runs per Operations tick (~30 Hz). There's a per-tech latency between target reassignment and tactical pickup — Control's tactical buffer is read each Operations tick, so a fresh assignment from Coordination's strategic tick propagates within ~33 ms.

---

## SECTION 12: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not the strategic decision-making. Planning produces the team plan; Coordination implements it.

This contract is not the per-tech control. Coordination's output (`TacticalGoal`) is the input to Control's tactical optimizer; the actual driving math lives in Control.

This contract is not the targeting logic. Coordination assigns *which* enemy each tech focuses on; *when* and *how* to fire is Control's responsibility (weapon-fire controller, step 1.9).

This contract is not the observation engine. Coordination computes LOS and injects observations; World's perception worker actually runs the Kalman updates. Coordination is the producer; World is the consumer.

This contract is not the comms-bus implementation. The interface is owned here; the underlying lock-free publish/dispatch primitives are in [THREADING-CONTRACT.md](THREADING-CONTRACT.md).

---

END OF COORDINATION-CONTRACT.md v0.1.0
