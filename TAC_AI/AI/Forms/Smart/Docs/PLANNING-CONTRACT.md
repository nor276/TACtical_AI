# PLANNING-CONTRACT.md

**Subsystem:** Planning/
**Form:** Smart
**Version:** 0.1.0
**Status:** AUTHORITATIVE — Defines Smart's strategic planner (PUCT MCTS), plan library, strategic state and action space, hybrid rollout evaluation, and the heuristic value function.

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The strategic search algorithm: PUCT (Predictor + UCT).
- The tree-node structure, selection rule, expansion policy, and backpropagation discipline.
- The strategic state representation that MCTS nodes hold.
- The strategic action space (the plan library).
- The hybrid rollout: short physics simulation + heuristic terminal value.
- The heuristic value function used at terminal states.
- The plan library: named team-level plans with parameters.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals and the "BEST AI" mandate.
- [ARCHITECTURE.md](ARCHITECTURE.md) for threading model, compute-budget gating, MP host/client gating.
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for worker pool, double-buffer, cancellation.
- [WORLD-CONTRACT.md](WORLD-CONTRACT.md) for `BeliefSnapshot` and intent categories that feed strategic state.
- [VEHICLE-CONTRACT.md](VEHICLE-CONTRACT.md) for `VehicleModelSnapshot` (per-tech capabilities used in rollouts and value function).
- [COORDINATION-CONTRACT.md](COORDINATION-CONTRACT.md) for the per-tech plan decomposition that turns team plans into `TacticalGoal`s.

**This document GOVERNS:**
- The team-level `StrategicPlan` that Coordination decomposes per tech.
- The cadence and compute discipline of the strategic planner.

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What Planning owns

Planning owns the team-level "what are we collectively doing" decision. Output: a `StrategicPlan` published every ~200–500 ms that Coordination decomposes into per-tech actions.

Planning does NOT own:
- Target assignment math — Coordination owns the Hungarian solve.
- Per-tech tactical positioning — Control's tactical optimizer owns this.
- Belief over the world — World owns this.

### 1.2 Decisions resolved at v0.1.0

[NORMATIVE]
- **MCTS variant: PUCT** (Predictor + UCT). Uniform prior at v0.1.0; learned policy slot ready for Learning subsystem (workflow step 1.10).
- **Rollout evaluation: hybrid.** Short physics rollout (~2–3 sec, simplified per-tech dynamics) + heuristic terminal value function.
- **Cadence: every 200–500 ms** (provisional per FORM-SPEC §1.2 disclaimer).
- **Strategic state representation: vector of team + enemy state aggregates.** Details §3.
- **Action space: the plan library.** Discrete set of named team plans with parameters. Details §6.

---

## SECTION 2: PUCT SEARCH

### 2.1 The selection rule

[NORMATIVE] At each tree node, the next action is chosen by:

```
a* = argmax_a  [ Q(s,a) + c_puct * P(s,a) * sqrt(N(s)) / (1 + N(s,a)) ]

where:
  Q(s,a) = mean value of simulations through edge (s,a)
  N(s,a) = visit count of edge (s,a)
  N(s)   = visit count of node s
  P(s,a) = prior probability of action a in state s (uniform at v0.1.0; learned later)
  c_puct = exploration constant (provisional: 1.4)
```

[NORMATIVE] At v0.1.0, the prior `P(s,a)` is uniform over the legal action set in `s` (any plan in the library is "legal" — strategic plans don't have hard preconditions). When Learning provides a learned policy, the prior is replaced; the rest of PUCT is unchanged.

### 2.2 Expansion

[NORMATIVE] When MCTS reaches a previously-unvisited state node, expand by enumerating all legal actions from the plan library (§6). Each action becomes an unvisited edge with `N=0` and `Q=0`.

[NORMATIVE] Expansion is the first-visit operation. Subsequent visits to the same state node select via the PUCT rule (§2.1).

### 2.3 Simulation (rollout)

[NORMATIVE] After expansion, the chosen action triggers a rollout (§4). The rollout's terminal value is the simulation result.

### 2.4 Backpropagation

[NORMATIVE] Standard MCTS backprop. After simulation returns value `v`:

```
For each (state, action) pair on the path from root to leaf:
    N(s,a) += 1
    Q(s,a) = Q(s,a) + (v - Q(s,a)) / N(s,a)  // incremental mean
```

[NORMATIVE] Backprop is on the same path the selection traversed. No off-path updates.

### 2.5 Tree reuse across ticks

[NORMATIVE] The tree persists between strategic ticks. On each tick:
1. The current strategic state becomes the new root.
2. The subtree rooted at the chosen action from the previous tick is preserved; the rest is discarded (since the chosen action led to where we are now).
3. The tree expands from the new root with whatever budget remains.

[RATIONALE] Reusing the subtree amortizes search effort. The strategic state doesn't change drastically between ticks; the prior tree's exploration is mostly still relevant.

### 2.6 Termination

[NORMATIVE] Search terminates on whichever happens first:
- Time budget expires (cancellation token fires, per ARCHITECTURE §2.3).
- Node count exceeds a limit (provisional: 5000 nodes total).
- All actions explored sufficiently (any action with `N < 5` is considered under-explored; the loop continues if any exists, up to time budget).

[NORMATIVE] The exploration limit (`N >= 5` per action) is a stated bound; rationale: PUCT's bound requires at least a few visits per action to estimate Q reliably. The specific value is provisional and tuned during self-play.

### 2.7 Action selection (after search)

[NORMATIVE] The chosen strategic action is `argmax_a N(root, a)` (highest visit count at the root). This is the standard MCTS choice — visit count is more robust than Q to noisy single-rollout outcomes.

[NORMATIVE] Confidence in the choice: `N(root, chosen) / N(root, total)`. If less than 0.4 (no clear winner), the plan publish marks the plan as "low-confidence" — Coordination handles this case by preserving last tick's plan.

---

## SECTION 3: STRATEGIC STATE REPRESENTATION

[NORMATIVE] A strategic state captures enough information to evaluate plans and roll out the team's next few seconds.

```
public sealed class StrategicState
{
    // Friendly side: our techs.
    public readonly IReadOnlyList<StrategicTechSummary> Friendly;
    // Hostile side: tracked enemy techs from BeliefSnapshot.
    public readonly IReadOnlyList<StrategicTechSummary> Hostile;
    // The plan currently in effect.
    public readonly StrategicPlan CurrentPlan;
    // Tick count since the current plan started.
    public readonly int PlanTickCount;
    // Optional: terrain summary (when Pathing provides one).
    public readonly TerrainSummary? Terrain;
}

public readonly struct StrategicTechSummary
{
    public readonly TechId Id;
    public readonly Vector3 PositionMean;
    public readonly Vector3 VelocityMean;
    public readonly float Heading;
    public readonly float HealthFraction;        // [0..1]
    public readonly float ThreatRating;          // computed from weapons + ammo + range
    public readonly float MobilityRating;        // top speed * turning ratio, normalized
    public readonly Vector3 PositionUncertainty; // diagonal of covariance (friendly = ~zero)
}
```

[NORMATIVE] The state is sized linearly in (friendly count + hostile count). For typical battles (5 friendly, 5 hostile) this is ~20 vector-of-floats — cheap to copy, cheap to hash, cheap to compare.

[NORMATIVE] State hashing for tree-node identity uses position bucketization (each tech's position rounded to a coarse grid, e.g., 5 m cells). This means slightly different positions are treated as the same state for search purposes — the search doesn't waste depth on micrometer distinctions.

[RATIONALE] The bucketization is what makes MCTS tractable at the strategic level — a state representation that distinguishes every floating-point position would make the tree infinitely branching.

---

## SECTION 4: STRATEGIC ROLLOUT

### 4.1 The hybrid rollout

[NORMATIVE] Given a strategic state `s` and an action `a` (a candidate plan), the rollout simulates ~2–3 seconds of team activity and returns a scalar value:

```
1. Apply action: switch CurrentPlan = a in the working state.
2. Decompose plan into per-tech intents (using Coordination's decomposition logic).
3. For each tech, simulate forward:
   - Forward-integrate position using the per-tech intent's desired velocity.
   - Bound by tech's MobilityProfile.TopSpeedForward.
   - Apply terrain blocking (when Terrain summary is available).
4. After 2-3 seconds simulated, evaluate the terminal state with §5's heuristic value function.
5. Return the heuristic score.
```

[NORMATIVE] The rollout uses **simplified per-tech dynamics**:
- Position advances at the desired velocity (from the decomposed intent), capped at top speed.
- No full per-actuator integration. No collision resolution. No damage.
- This is intentional: the rollout is for *strategic* lookahead, not for control simulation. Tactical/control rollouts (Control's `PhysicsRollout`) handle the fine grain.

[RATIONALE] Full physics rollouts at MCTS scale (hundreds per strategic tick) would consume the budget many times over. The simplified rollout is "good enough" because the heuristic terminal value (§5) captures most of what matters; the simulation just gets the team to a future state for evaluation.

### 4.2 Rollout depth

[NORMATIVE] Rollout depth: 2–3 seconds (provisional). Equivalent to ~40–60 simulation steps at 20 Hz internal step rate.

[NORMATIVE] Rollout terminates early if:
- The cancellation token fires (compute budget).
- A "decisive event" is detected: a friendly tech's `HealthFraction` reaches 0, or all hostile techs reach 0. In either case, the heuristic value is computed at the early-termination state.

### 4.3 Stochasticity

[NORMATIVE] The rollout is **deterministic** given the same state, action, and decomposition. Variance in MCTS comes from the tree's exploration of different action sequences, not from rollout noise.

[NORMATIVE] (When Learning lands and learning-augmented MCTS is enabled, the rollout MAY add stochastic per-step perturbations from the learned policy. v0.1.0 is fully deterministic.)

---

## SECTION 5: HEURISTIC VALUE FUNCTION

[NORMATIVE] Given a (rolled-out) terminal state, compute a scalar value that estimates how good the state is for our team:

```
value(state) =
    w_health    * HealthAdvantage(state)
  + w_position  * PositionQuality(state)
  + w_threat    * (-ThreatExposure(state))     // negative because exposure is bad
  + w_speed     * RelativeSpeedAdvantage(state)
  + w_coverage  * WeaponCoverage(state)
  + w_learned   * LearnedValue(state)           // zero until Learning subsystem
```

[NORMATIVE] Each term is in [-1, +1] range to keep weights commensurate. Total value is in roughly [-6, +6] before learned term lands.

### 5.1 HealthAdvantage

```
HealthAdvantage(state) =
    (sum of friendly HP) - (sum of hostile HP)
    -----------------------------------------
       sum of max HP across both teams
```

Range: [-1, +1]. We dominate → +1; they dominate → -1.

### 5.2 PositionQuality

```
PositionQuality(state) =
    mean over our techs of (
        EngagementRangeFitness(tech, nearest_hostile)
    )
```

`EngagementRangeFitness` returns 1.0 if the tech is at its preferred engagement range (~weapon range), drops off Gaussian-style as the range deviates. Range: [0, +1].

### 5.3 ThreatExposure

```
ThreatExposure(state) =
    mean over our techs of (
        WeaponCoverageFromEnemies(tech, hostiles)
    )
```

How many hostile weapons currently point at our techs. Range: [0, +1]. (Negative sign applied in the value sum because exposure is bad.)

### 5.4 RelativeSpeedAdvantage

```
RelativeSpeedAdvantage(state) =
    (mean friendly MobilityRating) - (mean hostile MobilityRating)
    -------------------------------------------------------------
       max across both teams
```

Faster team has tactical advantage (can dictate engagement). Range: [-1, +1].

### 5.5 WeaponCoverage

```
WeaponCoverage(state) =
    fraction of hostile techs that have at least one friendly weapon pointed at them
```

Range: [0, +1]. Good positioning has wide coverage.

### 5.6 Weights (provisional)

[NORMATIVE — provisional per FORM-SPEC §1 disclaimer]
- w_health = 2.0 (most important; survival dominates)
- w_position = 1.0
- w_threat = 1.5
- w_speed = 0.5
- w_coverage = 1.0
- w_learned = 0.0 (until Learning)

Tuned during self-play. Rationale per FORM-SPEC §5.4: "Health is the binary signal of winning; position and threat shape the path to that signal; speed and coverage are tactical advantages."

---

## SECTION 6: PLAN LIBRARY

### 6.1 The action space

[NORMATIVE] The strategic action space is a finite set of named team plans. Each plan has parameters that the MCTS state-action edge fixes at expansion time.

### 6.2 Initial plan set (v0.1.0)

| Plan | Parameters | Description |
|---|---|---|
| `EngageFocused` | primary_target_id | Concentrate fire on one enemy; supporting techs flank |
| `EngageDistributed` | (none) | Each tech engages whichever enemy is most efficient for them |
| `Skirmish` | (none) | Engage at preferred range; retreat when fired upon |
| `Flank` | target_id, side | Coordinated flank: half team holds, half moves wide |
| `DefensivePerimeter` | center_position, radius | Hold formation around center |
| `MobileScreen` | direction, advance_rate | Slow advance in a formation, screening for vulnerables |
| `FightingRetreat` | escape_direction | Withdraw while engaging trailing enemies |
| `Disengage` | rendezvous_position | Break contact; group up at rendezvous |
| `Bait` | target_id | One tech draws fire; others flank into the opening |
| `Hold` | (none) | Stop, conserve resources, wait |

[NORMATIVE] At v0.1.0, ten plans. Expand based on self-play insight (workflow step 1.11). Each plan is implemented by a decomposition function in Coordination ([COORDINATION-CONTRACT.md §6](COORDINATION-CONTRACT.md)).

[NORMATIVE] Plans are NOT pluggable. Adding a plan is a contract revision, not a runtime configuration.

### 6.3 Legality

[NORMATIVE] Most plans are always legal. Exceptions:
- `EngageFocused`, `Flank`, `Bait` require at least one hostile tech in `BeliefSnapshot`. (No targets → no plan.)
- `Disengage`, `FightingRetreat` require at least one friendly tech with `HealthFraction > 0.2`. (Already-dead team can't retreat.)
- `Bait` requires at least 2 friendly techs.

Illegal plans are pruned at expansion; MCTS does not explore them.

### 6.4 Action enumeration with parameters

[NORMATIVE] When expanding a node, the planner enumerates:
- All always-legal plans (no parameters) → one action each.
- `EngageFocused` with each currently-tracked hostile tech as `primary_target_id` → N_hostile actions.
- `Flank` with each tracked hostile × {left, right} → 2 × N_hostile actions.
- `Bait` similarly.
- `DefensivePerimeter` with the current friendly team's centroid as `center` (one action; radius computed from team spread).
- `MobileScreen` with current heading direction (one action; advance_rate from MobilityProfile aggregate).
- `Disengage` with rendezvous = nearest friendly base or scene-stationary-position (one action).
- `FightingRetreat` with escape direction opposite to the average hostile centroid (one action).

[NORMATIVE] Enumeration may produce 10–60 actions per node depending on hostile count. This is the branching factor; PUCT manages exploration.

---

## SECTION 7: ORCHESTRATION

### 7.1 The strategic planner instance

[NORMATIVE] One `StrategicPlanner` exists per team (typically per player, since each player has one team of Smart-driven techs).

### 7.2 Tick flow

[NORMATIVE] Every 200–500 ms (provisional; tuned per OD-3 soft cap):
1. Construct current `StrategicState` from `WorldModel.BeliefSnapshot` + `CoordinationState`.
2. Advance the tree's root to the current state (preserving the subtree from the previously-chosen action).
3. Run PUCT search until budget or node-count limit.
4. Select the highest-visit-count action at the root.
5. Publish the new `StrategicPlan` to a team-level `DoubleBuffer<StrategicPlan>` that Coordination reads.

[NORMATIVE] Workers run the search; the tick flow main-thread cost is just the snapshot construction + dispatch.

### 7.3 Host-authority

[NORMATIVE] Per ARCHITECTURE §3.2, planning runs only when `host == true`. On client, Coordination reads whatever plan was last replicated through Smart's normal control-output replication.

---

## SECTION 8: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/Planning/` contains six files:

| File | Owns |
|---|---|
| `StrategicPlanner.cs` | Orchestration, tick flow, plan publication. |
| `PUCTSearch.cs` | PUCT selection / expansion / simulation / backprop / tree reuse. |
| `StrategicState.cs` | `StrategicState`, `StrategicTechSummary`, hashing. |
| `PlanLibrary.cs` | The plan catalog with legality and parameter enumeration. |
| `StrategicRollout.cs` | Simplified per-tech rollout for MCTS simulation. |
| `StrategicValueFunction.cs` | Heuristic value function; per-term computation; weights. |

Six files within [FORM-SPEC §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives)'s range. Justification: each owns a distinct algorithmic concern. `PUCTSearch` and `StrategicRollout` are coupled but separable; merging produces a god-file.

---

## SECTION 9: DIAGNOSTICS INTEGRATION

[NORMATIVE] Planning exposes:

- `StrategicTickCompleted(int treeSize, TimeSpan duration, PlanId chosen, int chosenVisits, int totalVisits)` — per tick.
- `StrategicTickCancelled(int treeSize, PlanId bestSoFar)` — budget cut search short.
- `PlanLowConfidence(PlanId chosen, float confidence)` — when chosen.visits / total < 0.4.
- `PlanSwitchPending(PlanId previous, PlanId next, int previousPlanTickCount)` — when next tick will change plan.
- `RolloutCompleted(int rolloutDepth, float terminalValue, bool earlyTerminated)` — per rollout (likely high-volume).

[NORMATIVE] These flow into Diagnostics when authored.

---

## SECTION 10: OPEN ITEMS

[OPEN] **c_puct (exploration constant).** 1.4 provisional. Tune during self-play.
[OPEN] **Node-count budget.** 5000 nodes provisional.
[OPEN] **Min-visits-per-action threshold.** 5 provisional.
[OPEN] **State bucketization grid size.** 5 m cells provisional.
[OPEN] **Rollout depth.** 2–3 sec provisional within FORM-SPEC §1.2 range.
[OPEN] **Value function weights.** All provisional per §5.6.
[OPEN] **Plan library expansion.** 10 initial plans; self-play will reveal which others are needed.
[OPEN] **Strategic tick cadence.** 200–500 ms provisional.

---

## SECTION 11: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not the per-tech control decision. Planning produces team-level intent; Coordination decomposes; Control's tactical optimizer + MPC produces the per-tech action.

This contract is not the targeting decision. Planning chooses team posture; Coordination's Hungarian solve picks which tech targets which enemy.

This contract is not Smart's "behavior" or personality. Strategic plans are mechanical; how aggressive vs. conservative Smart plays emerges from the weighted heuristic value function (§5) and from learned augmentations when Learning lands.

This contract does not own the plan library's implementation details. The decompositions for `EngageFocused`, `Flank`, etc. live in [COORDINATION-CONTRACT.md §6](COORDINATION-CONTRACT.md). Planning only names the plans and parameters.

---

END OF PLANNING-CONTRACT.md v0.1.0
