# WORKFLOW.md

**Form:** Smart
**Version:** 0.1.0
**Status:** PROVISIONAL — Captures the intended build sequence for Smart. Not a hard plan; may revise as work progresses.

---

## SECTION 0: AUTHORITY AND PURPOSE

This document is authoritative for: the build sequence Smart follows from spec-set creation through v1 release; which subsystem contracts are authored at which step.

This document **defers to** [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for what each step delivers, [ARCHITECTURE.md](ARCHITECTURE.md) for cross-cutting concerns, and subsystem contracts (authored as steps reach them) for per-step internals.

This document is **build orientation, not a contract**. It does not impose obligations on Smart's design; it sequences the work. The actual progression MAY interleave or reorder steps where dependencies permit. Numbers in this document mark sequence position, not version-bump targets.

[SAFE TO CHANGE] The step boundaries, the per-step scope, and the interleaving. The sequence is a guide.

---

## SECTION 1: THE TWELVE STEPS

Spec phase status: contracts for steps 1.4–1.11 are drafted; remaining work is implementation. Status columns track contract authoring (left) and implementation (right). "Spec ✓" means the subsystem contract has been authored at the workflow step; "Impl —" means implementation has not yet begun.

| # | Step | Spec | Impl |
|---|---|---|---|
| 1 | Strip context | ✓ | n/a |
| 2 | Write the API guide | ✓ | n/a |
| 3 | Scaffold the form | n/a | ✓ (skeleton) |
| 4 | Build threading infrastructure | ✓ (Threading) | ✓ (v0.1.0) |
| 5 | Build the world model + perception worker | ✓ (World + Vehicle) | ✓ (v0.1.0; Vehicle block-capture stubbed pending TerraTech API verification) |
| 6 | Build the tactical optimizer + MPC controller | ✓ (Control v0.2.0 incl. weapon fire) | ✓ (v0.1.0; TankControl write API stubbed) |
| 7 | Build the strategic planner + MCTS | ✓ (Planning + Coordination) | ✓ (v0.1.0; wired into SmartRuntime via per-team TeamRuntime; ContinuousController consumes Coordination's TacticalGoal with 1 s freshness TTL; LOS raycast TerraTech-API stub; PUCT tree rebuilt per tick) |
| 8 | Build the threat field + pathing primitive | ✓ (Pathing) | ✓ (v0.1.0; ThreatFieldCost activated in Control; numerical CHOMP gradient + identity preconditioner; TerrainMap refreshes main-thread; baseline rating for unknown-vehicle enemies) |
| 9 | Build the weapon fire controller | ✓ (Control v0.2.0 revision) | ✓ (v0.1.0; **full TankControl actuator write live** — movement (`CollectMovementInput`) + fire (`FireControl`+`TargetPositionWorld`+`TargetRadiusWorld`); vanilla avoidance suppressed via `m_USE_AVOIDANCE = false`; OQ-5/OQ-6 resolved; friendly-fire raycast active via TerrainMap; coordination target override per §10.6; per-weapon hysteresis/lead/salvo coordination/ammo conservation/energy budget intact) |
| 10 | Build the learning components, untrained | ✓ (Learning) | ✓ (v0.1.0; all 4 models forward+inference; MLPs full backprop+Adam; GRU dense-head training only — BPTT TODO v0.2; ProfilePersistence with CRC32 + snapshot-before-write + corrupt preservation; DamageObserved→Threat event handler live; Intent/ActionValue/Residual handlers dormant pending sequence-buffer/plan-transition plumbing) |
| 11 | Build the self-play harness | ✓ (Training) | ✓ (v0.1.0; all 6 files wrapped in `#if SMART_DEV`; OutcomeScorer + sep-CMA-ES + ScenarioGenerator procedural rules + PretrainingPipeline model-averaging math live; tech spawn + despawn LIVE via `RawTechLoader` / `RemoveFromGame`; hyperparameter write-through LIVE — 16 CMA-ES tunables loosened to `public static`; **coroutine match driver LIVE** — `TrainingMatch.SimulateCoroutineFull` yields `WaitForFixedUpdate` and accumulates real telemetry via `WorldEventBus.DamageObserved`/`ProjectileFired` subscriptions scoped per match; alive predicate verified at `tank.visible.isActive && tank.blockman.blockCount > 0`; `SelfPlayHarness.BeginAsync(MonoBehaviour, maxGen, ct)` is the canonical entry point; library JSON loader TODO v0.2; baseline-metadata footer TODO v0.2; programmatic terrain seed TODO v0.2; render suppression for headless still partial — Time.timeScale only) |
| 12 | Ship a pretrained AI | (operational milestone, not a spec) | — |

The Diagnostics subsystem contract was authored opportunistically alongside Training (per §3 below). All ten subsystem contracts now exist.

### 1.1 Strip context [COMPLETE]

Clear the active conversation / agent state, audit the doc tree, remove mental dependence on Modified-form decisions. Smart is not a fork of Modified; it is a sibling.

**Delivered:** Smart's spec-set scaffolding (this directory) created from a clean reading of doctrine + a one-time consultation of `NEW_FORM_INTENT.md` to extract project-level substance.

### 1.2 Write the API guide [COMPLETE]

Document the shell surface Smart will consume: every `TankAIHelper` member read, every event subscription, every `tank.control` write, the threading guarantees, the safe-vs-main-thread distinctions. Smart's implementation grounds against this surface.

**Delivered:** [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md).

### 1.3 Scaffold the form [SKELETON LANDED]

Empty `SmartForm.cs` implementing `IAIForm`, registered (by virtue of existing in the assembly), selectable in-game via the existing form selector. Compiles. Picks up in the form selector. Ticks no behavior.

**Verification gates:**
- Smart can be activated and deactivated cleanly without affecting Modified or Vanilla.
- `InitGlobal` and `DeInitGlobal` run without errors on activation/deactivation.
- The persisted `KickStart.AIFormSelected = "Smart"` is honored after restart.

**Blocking decisions:** all resolved.
- OD-7 (MP determinism) — host-authority; ambient RNG OK.
- OD-8 (attract-screen behavior) — demo mode; no-op at v1.3 (demo content arrives with Training, step 1.11).

**Delivered:**
- `TAC_AI/AI/Forms/Smart/SmartForm.cs` — 25-member `IAIForm` skeleton; all methods no-op or return neutral defaults; passes registry discovery; `Id = "Smart"`; `DisplayName = "Smart (advanced AI — in development)"`.
- `TAC_AI/AI/Forms/Smart/SmartMovementController.cs` — extends `MovementControllerBase` mirroring Vanilla's pattern; 7 abstract overrides; SelectCore returns null; Drive* callbacks are no-ops at v1.3.
- `TAC_AI/TAC_AI.csproj` updated — both files added to the Compile group alongside Vanilla's entries.

**Build status:** Smart's files are valid C# 7.3 against the .NET Framework 4.6.1 target. The project's existing phantom `AI\Forms\Modified\*` `<Compile>` entries (per FORM-SPEC OD-9) still block a clean build; resolution is a separate developer-workspace decision.

**In-game verification gates not yet run:** the verification gates above (Smart selectable, Init/DeInit clean, persistence honored) require running the mod inside TerraTech, which is out of scope for the spec/skeleton phase. They become runnable once the build state is resolved.

### 1.4 Build threading infrastructure

Worker pool, double-buffer, lock-free event queue, cancellable tasks. Threading first because every subsequent subsystem depends on its primitives.

**Verification gates:**
- Thread lifecycle survives form switches without leaking.
- Cancellable tasks actually cancel within a bounded latency.
- The double-buffer swap is race-free under contention.
- Workers terminate within `DeInitGlobal` ([ARCHITECTURE.md §5 I3](ARCHITECTURE.md#section-5-cross-subsystem-invariants)).

**Subsystem contract authored:** Threading.

### 1.5 Build the world model + perception worker

Belief state, kinematic trackers, vehicle introspection. Smart observes the battlefield correctly before it acts on it.

**Verification gates:**
- Debug visualization confirms the belief state tracks reality (tight Gaussian while in sight; spreading distribution while hidden).
- Vehicle introspection updates correctly on block attach/detach events.
- Perception worker publishes snapshots without main-thread stalls.

**Subsystem contracts authored:** World, Vehicle.

### 1.6 Build the tactical optimizer + MPC controller

Continuous control over Smart's own model. Smart can be told "go to X" and continuously navigates there, with the MPC absorbing disturbances. No combat yet.

**Verification gates:**
- A Smart-driven tech given a destination via the tactical optimizer reaches it under realistic terrain.
- The MPC absorbs disturbances (terrain bumps, block loss) without oscillation.
- `ControlFrame` consumption stays O(constant) — solver work happens in workers.

**Subsystem contract authored:** Control.

### 1.7 Build the strategic planner + MCTS

Team-level plan selection and decomposition into per-tech tactical goals. Smart can coordinate movement across multiple techs without combat goals.

**Verification gates:**
- A Smart-driven team given a team-level objective (e.g., "occupy this region") decomposes into coherent per-tech assignments.
- Strategic cadence stays within target (~200–500 ms).
- Plan revisions on changed conditions take effect within one strategic cycle.

**Subsystem contracts authored:** Planning, Coordination.

### 1.8 Build the threat field + pathing primitive

Smart considers danger when navigating. Movement becomes tactical even without an active engagement.

**Verification gates:**
- Threat field correctly identifies high-exposure regions given current belief state.
- Pathing produces continuous trajectories that respect vehicle capabilities and avoid the threat field.
- Trajectories the MPC consumes are followable (no path the controller cannot execute).

**Subsystem contract authored:** Pathing.

### 1.9 Build the weapon fire controller

Smart fires its weapons effectively at known targets given its world model. Engagement begins.

**Verification gates:**
- Smart-driven techs land hits at expected rates against stationary targets.
- Weapon fire respects arc, range, cooldown, and ammo constraints.
- Multi-weapon coordination (which weapon fires at which target) emerges from the per-tech weapon-fire controller.

**Subsystem contract revised:** Control (extended with weapon-fire timing).

### 1.10 Build the learning components, untrained

Wire up the opponent intent classifier, action-value estimator, trajectory residual model, threat assessment model. The online trainer. Persistent memory. The data pipeline.

**Verification gates:**
- Combat events flow into minibatches; gradient updates produce updated parameters; updated parameters affect subsequent decisions.
- Per-player profiles save and load correctly across sessions.
- Corruption recovery works (corrupt profile → fall back to baseline; surface corruption to logs).

**Subsystem contract authored:** Learning.

### 1.11 Build the self-play harness

Two-instance AI vs. AI on procedurally generated battlefields. Outcomes logged and scored. Use the logs to pretrain learning components and tune utility-function constants via evolutionary search.

**Verification gates:**
- Self-play matches complete and produce scored outcomes.
- The evolutionary search measurably improves outcome scores over generations.
- Pretrained baseline weights produced by self-play improve real-mission performance over blank-slate weights.

**Subsystem contract authored:** Training (dev-only; excluded from release builds).

### 1.12 Ship a pretrained AI

The shipped Smart form ships with the self-play-pretrained baseline. Online learning then refines per-player as the player plays.

**Verification gates:**
- v1 release criteria from [FORM-SPECIFICATION.md §4](FORM-SPECIFICATION.md#section-4-success-criteria-v1) met.
- No subsystem contract has unresolved [OPEN] markers not tracked elsewhere.

---

## SECTION 2: WHAT VIABLE LOOKS LIKE

Steps 1.5–1.9 produce a viable Smart — one that observes the battlefield, navigates continuously, plans at the team level, considers danger, and fires effectively. Steps 1.10–1.12 produce the *mean* one — one that learns from the specific player and is pretrained against discovered edge cases.

The user controls when to release which version.

---

## SECTION 3: THE DIAGNOSTICS SUBSYSTEM IS OPPORTUNISTIC

[NORMATIVE] The Diagnostics subsystem contract is authored alongside whichever subsystem it currently observes. No single step in §1 "owns" Diagnostics. Specifically:

- Decision-log skeleton lands at step 1.6 (when there are decisions to log).
- Compute-budget allocator + starved-frame counter land at step 1.6 (when the first compute budget consumer exists).
- Debug visualization lands progressively, one subsystem at a time.
- Replay log lands when self-play needs it (step 1.11).

The contract MAY be drafted in pieces (Diagnostics v0.1.0 with just the decision log, then v0.2.0 adding the budget allocator, etc.) or as one document revised in place.

---

## SECTION 4: INTERLEAVING AND DEPENDENCIES

[INFORMATIVE]

```
1.1 ─→ 1.2 ─→ 1.3 ─→ 1.4 ─→ 1.5 ─┬─→ 1.6 ─→ 1.7 ─→ 1.8 ─→ 1.9 ─┬─→ 1.10 ─→ 1.11 ─→ 1.12
                                 │                              │
                                 ▼                              ▼
                              (viable from 1.9 if              (pretrained from 1.11)
                               1.10 deferred)
```

Hard dependencies:
- 1.5 (world model) needs 1.4 (threading) for its perception worker.
- 1.6 (MPC) needs 1.5 (world model) for state inputs.
- 1.7 (strategic) needs 1.5 (world model, fused team belief) and 1.6 (tactical) to decompose into.
- 1.8 (pathing) needs 1.5 (world model) for the threat field.
- 1.9 (weapons) needs 1.5 and 1.6.
- 1.10 (learning) needs 1.9 (combat events to learn from) for the data pipeline to be exercised.
- 1.11 (self-play) needs 1.9 (combat is what gets played) and 1.10 (the learning targets being trained).

Soft dependencies (parallelizable):
- 1.7 and 1.8 can land in either order.
- 1.10's plumbing can be wired before 1.9 lands if data is mocked.

---

END OF WORKFLOW.md v0.1.0
