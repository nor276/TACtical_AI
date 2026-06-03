# FORM-SPECIFICATION.md

**Form:** Smart (working name; final pinned per Open Decision OD-1)
**Version:** 0.2.3
**Status:** AUTHORITATIVE — Top-level for the Smart form's spec set. This document defines what Smart is, why it exists, what is non-negotiable, and what is excluded.

---

## CHANGES SINCE 0.2.2

Verification pass against the codebase: OD-9 (Modified default-fallback gap) updated with full context discovered by reading `TAC_AI/TAC_AI.csproj` and the on-disk file tree. The `.csproj` contains ~100 phantom `<Compile Include="AI\Forms\Modified\..." />` entries that reference files which do not exist on disk; the codebase is in a mid-refactor state. This is shell-side context the user may or may not be aware of; Smart's design is unaffected, but Smart's eventual implementation requires a clean compile state.

## CHANGES SINCE 0.2.1

- §7 Open Decisions OD-3, OD-4, OD-7, OD-8 **resolved** per the Layer 2 Q&A round:
  - OD-3 (compute budget): defer to self-play measurement with a soft cap of "no framerate drop below per-platform target."
  - OD-4 (threading): work-stealing pool of `Math.Max(2, Environment.ProcessorCount - 2)` workers at normal priority.
  - OD-7 (MP determinism): host-authority. Smart runs substantively only when `host==true`; clients no-op.
  - OD-8 (attract behavior): demo mode. Smart runs in non-mission states; specific demo content deferred to subsystem contract.

## CHANGES SINCE 0.2.0

- Added project-local `[OPEN]` marker declaration to reading conventions (§0). The marker is used in Smart's spec set to flag unresolved questions tracked elsewhere; doctrine §4.2 lists the standard markers without forbidding additions, and the project-local declaration removes ambiguity.
- Added a provisional-value disclaimer at the top of §1, per the DOCTRINE-PATTERNS.md "provisional-value disclaimers" pattern. Numeric values in §1 (planning horizons, step counts, cadences, model sizes) are illustrative starting points; the structural requirements are authoritative.
- Added §5.10 (file-granularity directive) requiring one coherent responsibility per Smart source file; subsystems land at ~3–7 files on average; god classes and excessive subdivision are both violations.

## CHANGES SINCE 0.1.0

- Removed deference to `NEW_FORM_INTENT.md`. The intent doc was treated as a project specification in v0.1.0; doctrine §6.4 (do not preserve prior designs as sacred) names this as a recurring failure mode. The intent doc is now treated as transitional reference material that will be deleted once its substance is captured across this spec set.
- Absorbed the nine architectural-property statements (formerly "capability classes") directly into §1 as Smart's defining properties.
- Absorbed the form-vs-shell ownership boundary directly into §1.4.
- Absorbed the "why a separate form" rationale directly into §2.
- Removed the deference matrix to subsystem contracts (the 10 stub contracts at v0.1.0 were premature per doctrine §8.3 and have been deleted). Subsystem contracts are authored as the work reaches them.

---

## SECTION 0: AUTHORITY AND READING ORDER

This document is **authoritative** for: Smart's project-level facts (what Smart is, why, what counts as done, what is excluded); the form-vs-shell ownership boundary; Smart's AI-collaborator directives; the open decisions tracked at the project level.

This document **defers to** [DOCTRINE.md](../../../../../Doctrine%20Documentation/DOCTRINE.md) for methodology that applies across all my work.

This document **governs** [ARCHITECTURE.md](ARCHITECTURE.md) (component map, threading, tick lifecycle) and [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md) (the shell surface Smart consumes), and will govern subsystem contracts as they are authored.

Conflicts between this document and any document below resolve in favor of this document. Conflicts between this document and DOCTRINE.md resolve in favor of DOCTRINE.md (this document is project-level; DOCTRINE.md is global).

Reading conventions: RFC 2119 keywords (MUST, SHOULD, MAY) and the markers [NORMATIVE], [CANNOT CHANGE], [SAFE TO CHANGE], [RATIONALE], [INFORMATIVE EXAMPLE] per DOCTRINE.md §4. This spec set additionally uses **`[OPEN]`** to mark unresolved questions tracked elsewhere (in this document's §7 Open Decisions, in [SHELL-API-GUIDE.md §12](SHELL-API-GUIDE.md#section-12-open-questions-consolidated), or in subsystem contracts when authored). `[OPEN]` is a project-local addition; it has no equivalent in DOCTRINE.md §4.2.

---

## SECTION 1: WHAT SMART IS

Smart is an `IAIForm` implementation for the TAC AI mod for TerraTech. It is **a sibling to the existing Modified form, not a successor**. Both forms coexist in the registry; the player picks per-game which AI drives their techs.

Smart is defined by nine architectural properties (§1.1–§1.9 below) and one ownership boundary (§1.10). These together are Smart's identity. Behavioral specifics — combat strategy, target selection, retreat thresholds, engagement-range philosophy, "feel," difficulty knobs — are NOT part of Smart's identity. They are designed during the build, in the owning subsystem contracts, and may evolve without changing what Smart is.

[CANNOT CHANGE] The architectural properties in §1.1–§1.9 are Smart's contract with itself. A change to any of them is a major-version change, not an incremental revision. Changes to the ownership boundary in §1.10 are similarly load-bearing.

[NORMATIVE — provisional values] Numeric values that appear in §1.1–§1.9 (planning horizons such as "0.5–2 sec," step counts such as "~10 gradient steps," cadences such as "~60 Hz" or "200–500 ms" or "~1 Hz," model sizes such as "kilobytes of parameters," evaluation costs such as "microseconds") are **illustrative starting points**, not contract values. The structural requirements — that the controller solves MPC over a planning horizon, that the tactical optimizer is gradient-based, that learned models are small and fast enough to evaluate within budget — are authoritative. The specific numbers are design-target estimates that will be tuned via self-play (§1.7) and pinned during workflow implementation. A future revision that changes a specific number to fit measured workload is not a major-version change; a future revision that changes the structural requirement is. (Per DOCTRINE-PATTERNS.md "provisional-value disclaimers.")

### 1.1 Event-driven internally

[NORMATIVE] Smart subscribes to game events — damage taken, projectile fired by an enemy, target acquired, target lost, line-of-sight transition, block destroyed, ally killed, weapon overheated — and dispatches reactive handlers when each fires. The `IAIForm` tick hooks (`Directors`, `Operations`, `PostUpdate`) are mostly no-ops; the substantive work happens in event callbacks. A low-frequency ambient "vital signs" tick (~1 Hz, driven by an internal timer checked from `Operations`) catches anything not event-driven.

[RATIONALE] This is not an optimization — it is a different programming model from polling-based AI. Reactions become immediate; ambient cost drops to near zero; behavior decouples from simulation framerate.

### 1.2 Continuous-time control via MPC

[NORMATIVE] Smart uses no waypoints. The continuous controller runs every physics tick (or every other, budget-permitting) and solves a small model-predictive control problem: given current physics state, desired physics state, and a planning horizon of ~0.5 to 2 seconds, the controller computes the throttle / steer / brake / weapon-fire profile that best achieves the desired state. Output is the *current* control input, recomputed continuously so disturbances (terrain, enemy fire, lost blocks) are absorbed each frame.

[CANNOT CHANGE] No arrival radii. No "follow waypoint, then next waypoint." Continuous trajectory shaping.

The desired physics state is set by the tactics layer; the MPC layer does not know *why* it wants that state.

### 1.3 Full vehicle introspection

[NORMATIVE] Smart maintains a physical model of every visible tech — friendly and hostile — derived from the block list. The model includes:

- Mass distribution and center of mass (updates when blocks attach or detach).
- Thrust map: each propulsion block (booster, jet, wheel, hover) as a thrust vector at its mount point; sum is available control authority per direction.
- Weapon mount table: each weapon block as a 3D position + arc cone + range + projectile velocity + damage + fire rate + ammo state + current cooldown.
- Armor / vulnerability map: block density and material per face; weak-side identification per target.
- Kinematic tracker: position, velocity, acceleration, jerk estimator.
- Mobility profile: top speed, turning radius, climb angle, water capability, vertical authority.

[RATIONALE] This is the data needed to answer questions the polling-based AI cannot formulate — "what's this enemy's weakest face?", "what's its maximum acceleration?", "will it tip if I shoot its top?", "what's optimal engagement range against its weapon loadout vs. *this other* enemy's loadout?"

### 1.4 Bayesian belief state

[NORMATIVE] Smart holds every observed tech as a **probability distribution over its state** — position, velocity, intent, weapon state — rather than as a pointer to a current observation. While in sight, the distribution is a tight Gaussian. Out of sight, it spreads at a rate bounded by the tech's maximum acceleration, biased by terrain (channels narrow; open ground widens) and last-observed velocity. Re-acquisition collapses the belief via Bayesian update.

[NORMATIVE] Planning runs against the belief, not against a point. Expected utility is integrated over the belief cloud — (damage I deal if they're there) − (damage I take if they're there). Smart makes decisions about enemies it cannot currently see, using only the prior plus elapsed time.

### 1.5 Gradient-based and rollout-based decisions

[NORMATIVE] Two stronger decision primitives replace heuristic argmax:

- **Tactical positioning** (per-tech, per-tick): gradient ascent over a continuous parameterization of "where I want to be in N seconds." Objective is a differentiable function of (position, heading, time) returning expected utility. Target ~10 gradient steps; sub-millisecond per tech.
- **Strategic decisions** (team-level, every 200–500 ms): Monte Carlo tree search over candidate plans, evaluated by physics-aware short rollouts that respect thrust limits, weapon ranges, and turning radii. Score each candidate; pick best; replan when conditions change.

Behavior is bounded by what the optimizer can discover, not by the cases the author thought of.

### 1.6 Online learning with persistent per-player memory

[NORMATIVE] Several small models train continuously while the game runs:

- **Opponent intent classifier** (per-player): maps recent opponent state sequences to a distribution over high-level intents (aggressing, retreating, flanking, repositioning, baiting). Architecture is an open decision (OD-5); small RNN, gradient-boosted trees, or similar are all acceptable.
- **Action-value estimator**: Q-function over (state features, action) → expected outcome. Bootstrapped from rollouts; refined from observed real outcomes.
- **Trajectory residual model**: learned correction to the kinematic tracker, capturing per-player motion habits.
- **Threat assessment model**: learned actual damage output and survivability of opponent compositions, not theoretical block-data values.

[NORMATIVE] Models MUST be small (kilobytes of parameters, microseconds to evaluate). Deep-learning frameworks are NOT a dependency; the models are simple enough to implement directly in C# with gradient descent on minibatches drawn from the running combat log.

[NORMATIVE] Per-player profile files persist to disk under `<mod-dir>/SmartAI/Profiles/<player-id>.<ext>` (extension per OD-6). Profiles are loaded when the player joins; saved on player leave, world unload, and form deactivation. Per DOCTRINE.md §2.4, destructive operations on profile files MUST be recoverable; the specific mechanism is chosen per LEARNING-CONTRACT when that contract is authored.

[CANNOT CHANGE] Per-player profiles persist across sessions. This is the mechanism by which Smart feels personally responsive to specific players, rather than playing the same way against everyone.

### 1.7 Self-play training infrastructure (dev-only)

[NORMATIVE] Smart's source tree includes a self-play harness used during development: two instances of Smart play against each other on procedurally generated battlefields; outcomes are logged and scored. The logs are used to:

- Tune utility-function constants via evolutionary search (CMA-ES, random search with elitism, or similar).
- Pretrain the §1.6 learning components on synthetic but realistic data.
- Find edge cases that would never appear in human playtests.

[CANNOT CHANGE] The shipped Smart form is **pretrained**, not blank-slate. Online learning refines from real player behavior on top of the pretrained baseline.

[NORMATIVE] The self-play harness MUST be excluded from release builds via conditional compilation. The release artifact contains the shipped pretrained baseline and the runtime; it does not contain training infrastructure.

### 1.8 Team-level shared cognition

[NORMATIVE] Friendly Smart-driven techs share their world models via a fused team-level belief. Each tech publishes its observations; each reads from the fused belief when planning. The strategy layer computes the team's optimal action vector and decomposes it into per-tech assignments — coordinated focus fire, coordinated flanking, coordinated retreats — emergent from team-scale optimization rather than independent per-tech decision-making.

### 1.9 Anytime computation, compute-budget aware

[NORMATIVE] Smart tracks its own compute budget. Every optimizer is anytime:

1. Returns a valid (possibly suboptimal) result on early cancellation.
2. Monotonically improves with additional compute.
3. Responds to cancellation within a bounded latency.

When the player has 3 enemies engaged, the tactical optimizer runs more gradient steps; when 50, fewer. Strategic rollouts: more in calm, fewer in chaos. Smart scales to load without crashing framerate.

[NORMATIVE] Compute-budget targets (CPU ceiling per battle scale) are an open decision (OD-3). Initial values are obtained from self-play measurement (§1.7) before release.

### 1.10 Ownership boundary

[CANNOT CHANGE] Everything not strictly required by the `IAIForm` contract is **owned by Smart**. The ownership boundary is:

| Concern | Owner |
|---|---|
| World model, belief state, perception | Smart |
| Vehicle introspection (mass, thrust, weapons, armor) | Smart |
| Kinematic trackers | Smart |
| Pathing primitive (trajectory optimization) | Smart |
| Obstacle / terrain representation | Smart |
| Tactical optimizer (gradient-based) | Smart |
| Strategic planner + MCTS | Smart |
| Continuous controller (MPC) | Smart |
| Weapon fire controller | Smart |
| Energy / power manager | Smart |
| Team-level coordination + shared belief | Smart |
| Target assignment, role assignment | Smart |
| Online learning models + training pipeline | Smart |
| Self-play harness (dev-only) | Smart |
| Persistent per-player memory | Smart |
| Decision logging, performance monitoring, debug viz | Smart |
| Form-owned worker pool, double-buffer, event queue | Smart |
| `IAIForm` registration + form-selector wiring | Shell (existing `AIFormRegistry`) |
| `TankAIHelper` bus (where the engine reads control output) | Shell |
| `EControlOperatorSet` / `tank.control` write surface | Shell-provided; Smart writes |
| Game event hooks (damage, block, projectile, spawn, etc.) | Shell-provided; Smart subscribes |
| Mod boot / config / option-panel publishing | Shell |

[NORMATIVE] Smart MUST NOT read or call:

- `AIEPathMapper`, `AIEAutoPather`, `AIEPathing` (Modified's pathing).
- Modified's combat brain, target scanner, or operations dispatch (currently embedded in `RCore.*`, `BGeneral.*`, and `TankAIHelper` partial classes).
- The shared obstacle grid, perception cache, or thread pool that Modified uses.
- `AISettings.cs` keys that Modified consumes for tuning. Smart has its own tuning surface.

[NORMATIVE] What Smart writes back to the shell is the same minimal contract any `IAIForm` writes: control input on `TankControl` and intent on `EControlOperatorSet`, via the existing one-line bridge. Everything before that point is Smart's program.

[RATIONALE] This boundary is the entire reason Smart is a separate form rather than a Modified refactor. Smart's value is being a different *kind of program*, with capabilities the existing architecture cannot express. Coupling to Modified's internals would erode this; the boundary is non-negotiable.

---

## SECTION 2: WHY SMART EXISTS

Modified has accreted aesthetic decisions over many phases of tuning — decisive-feel restore, lead caps, turret duty cycle, `holdSpacing` tweaks, `AnyNonSea` soft tiebreakers, and others — that depart from the original TerraTech AI. Continuing to evolve Modified negotiates with that accumulated taste. There is no clean baseline to A/B against, and the architecture inherits assumptions made years ago when only one form existed.

Building Smart as a sibling, without consulting Modified's lineage, solves both problems:

- The in-game form selector already supports per-player form choice with no cross-coupling. The user picks which AI runs; both forms coexist; neither's evolution constrains the other.
- Smart's architecture is built from scratch against the `IAIForm` contract and the shell surface, not by extending Modified. Whatever Smart is, it is because of the nine architectural properties in §1, not because of what Modified was.

Smart is **not an upgrade** to Modified. The result may be ten times the size of Modified; that is acceptable and expected. The intent is a fundamentally different program, not a smarter behavior tree on top of the same architecture.

[INFORMATIVE] Whether Modified should be frozen on aesthetic changes from the point Smart's work begins, or walked back to "literal-original-plus-bug-fixes," is decided outside Smart's spec set. Smart's existence does not require either resolution.

---

## SECTION 3: GOALS

### 3.1 Primary goals (non-negotiable)

[NORMATIVE]

**3.1.1 `IAIForm` compliance.** Smart MUST implement every method of the `IAIForm` interface as documented in [SHELL-API-GUIDE.md §2](SHELL-API-GUIDE.md#section-2-the-iaiform-interface). Empty bodies are acceptable for hooks Smart does not exercise.

**3.1.2 Architectural properties realized.** Smart MUST realize §1.1–§1.9. Each property is load-bearing for Smart's identity; absence of any one means Smart is not Smart.

**3.1.3 Ownership boundary enforced.** Smart MUST honor §1.10. No Smart source file reads `AIEPathMapper`, `AIEAutoPather`, `AIEPathing`, or any of Modified's combat-brain types.

**3.1.4 Form coexistence.** Smart MUST NOT alter Modified's behavior at runtime. Activating Smart deactivates Modified globally (per the existing `AIFormRegistry.SetActive` mechanism); both forms remain in the registry and either can be reselected without restarting the game.

**3.1.5 Loud failure.** Per DOCTRINE.md §2.5, nothing in Smart dies silently. Worker thread failures, missing learned-model files, corrupted profile files, compute-budget exhaustion — each produces either a logged warning with descriptive context plus a degraded-but-valid running state, or a loud descriptive failure. Per-failure-category choice is made in the owning subsystem contract.

**3.1.6 Per-player profile recoverability.** Per DOCTRINE.md §2.4, destructive operations on per-player profile files MUST be recoverable. The mechanism (snapshot-before-write, append-only history, soft-delete, etc.) is chosen in the Learning subsystem contract when it is authored.

### 3.2 Secondary goals (important but yieldable in conflict)

[NORMATIVE]

**3.2.1 Compute-budget targets met.** Smart SHOULD scale compute with load (§1.9) such that framerate stays above a per-platform target. Specific targets are OD-3.

**3.2.2 Pretrained baseline shipped.** Smart SHOULD ship with pretrained learned models (§1.6, §1.7). The shipped values come from the self-play harness; online learning refines them per player. Shipping without pretraining (blank-slate models) is acceptable for early releases.

**3.2.3 Decision-trace observability.** Smart SHOULD log enough decision-trace data that a "why did the AI do X?" question is answerable from logs without reproducing the run. Format and retention are decided in the Diagnostics subsystem contract.

**3.2.4 Multiplayer correctness.** Smart SHOULD operate correctly in TerraTech multiplayer. The determinism boundary (whether stochastic decisions need to agree across host and client) is OD-7.

### 3.3 Non-goals (explicitly excluded)

[NORMATIVE]

**3.3.1 Smart is not a Modified upgrade.** Smart does not replace Modified, does not borrow Modified's tuning, and does not inherit Modified's combat aesthetics.

**3.3.2 Smart does not extract Modified into a `ModifiedForm.cs`.** Modified's logic currently lives embedded in the shell (`RCore.*`, `BGeneral.*`, `TankAIHelper` partials). Extracting it is a separate work item. The `AIFormRegistry.DefaultFormId = "Modified"` default-fallback gap that results is captured as OD-9.

**3.3.3 Smart does not consume Modified's pathing primitives.** No reference to `AIEPathMapper`, `AIEAutoPather`, `AIEPathing` in Smart's source.

**3.3.4 Smart does not extend the `IAIForm` contract.** If a Smart design appears to require a new `IAIForm` method, the design is probably wrong (every other form would need to change too). The requirement is pushed back into Smart-internal architecture instead. Exception: a genuinely shell-level capability gap is raised as a separate work item, not silently absorbed.

**3.3.5 Smart's behavioral character is not specified here.** Combat strategy, target-selection algorithm, retreat threshold, engagement range, "feel" — none of these are decided in this document. They are designed during the build, in the owning subsystem contracts. Smart's architectural properties permit it to be aggressive, conservative, melee, ranged, decisive, methodical, or any combination; which it actually is, is design work.

**3.3.6 Smart does not introduce a second plugin extension point.** The TAC AI mod has exactly one plugin point: `IAIForm` itself. Smart is one such form. Smart does not add a registry of optimizers, models, maneuvers, or any other Smart-internal pluggable. Per DOCTRINE.md §2.3, registries earn their cost only when there are multiple growing variants — Smart has none.

---

## SECTION 4: SUCCESS CRITERIA (v1)

[NORMATIVE] Smart is "done" for v1 when:

**4.1** Smart appears in the in-game form selector and can be selected and deselected without affecting Modified.

**4.2** When Smart is active and the player engages combat, every Smart-driven tech moves, fires, and responds to damage without unhandled exceptions.

**4.3** Per-player profiles persist across sessions: loaded on player join, saved on player leave or world unload, recoverable on corruption.

**4.4** The self-play harness produces match outcomes that the evolutionary tuner can score.

**4.5** Compute-budget cutoffs prevent the form from dropping framerate below the per-platform target (target value per OD-3).

**4.6** No subsystem contract has unresolved [OPEN] markers at v1 release that are not also tracked in [Section 7](#section-7-open-decisions).

[INFORMATIVE] Beyond v1, Smart's success is measured by combat quality — but quality is not a specification, it is a design target the user evaluates. Spec compliance is the binary "done."

---

## SECTION 5: AI-COLLABORATOR DIRECTIVES

These are Smart-specific overlays on DOCTRINE.md §6. They name failure modes specific to this project.

[NORMATIVE]

**5.1 Do not consult Modified's source while designing Smart.** The architectural-properties contract in §1 is built clean against the `IAIForm` surface. Reading Modified's pathing, combat brain, or tuning while drafting Smart contracts will produce derivative designs and silently rebuild Modified's accreted taste. Smart's value emerges from designing it from first principles against the shell surface, not from reusing Modified's shapes. Doctrine §6.4 applies generally; in Smart's case, Modified is the prior design that must not be preserved.

**5.2 Do not extend the `IAIForm` contract to fit Smart.** Per §3.3.4. The contract is the shell's authority; Smart fits within it.

**5.3 Do not prematurely commit to algorithm choices.** §1 names capabilities (MPC, MCTS, Bayesian belief, gradient ascent), not specific implementations. Picking a specific MCTS variant, MPC solver, or model architecture before its subsystem contract is drafted is over-specification.

**5.4 Do not add defensive bounds without a stated reason.** Per DOCTRINE.md §6.10. The compute-budget target (§1.9) has a stated reason (player-facing framerate). Other bounds (max techs tracked, max gradient steps, max rollout count, max profile size) need a stated reason traceable to hardware, contract, business, or observed failure mode. "Just in case" caps are forbidden.

**5.5 Do not embed learned-model parameters in source.** Learned-model weights are persistent business data per DOCTRINE.md §2.4. They live in profile files and the shipped pretrained baseline file; not in `.cs` files. Hardcoded weights in source are forbidden.

**5.6 Do not write per-tech state outside `helper.FormState`.** Per [SHELL-API-GUIDE.md §6](SHELL-API-GUIDE.md#section-6-tankaihelper-field-surface), `FormState` is the form-owned slot. Form-side dictionaries keyed by tech leak across form swaps and are forbidden.

**5.7 Do not touch TerraTech engine objects from form-owned workers.** Per [SHELL-API-GUIDE.md §9](SHELL-API-GUIDE.md#section-9-threading), the Unity API is main-thread-only. Worker code that reads `Tank`, `TankBlock`, `Visible`, `Rigidbody`, `Transform`, or any `ManXxx.inst` from a worker thread produces undefined behavior at unpredictable times.

**5.8 Do not put solver work in `ControlFrame`.** `ControlFrame` runs on the Unity main thread, per-frame. It MUST be a fast lookup against a pre-computed solution. Heavy work belongs in `Operations` or in workers.

**5.9 Do not preserve transitional reference material as authoritative.** Prior design documents preceding this spec set (intent docs, working notes, brainstorm artifacts) are reference material, not contracts. Specs that defer to such material re-introduce the §6.4 failure mode that motivated v0.2.0 of this document. When new subsystem contracts are authored, they absorb the relevant substance inline; references to the prior material itself are not permitted.

**5.10 Each Smart source file MUST have one coherent responsibility.** Subsystem directories land at **~3–7 files on average**. Two violations are equally forbidden: a god class (one file spanning multiple responsibilities — e.g., `World/PerceptionAndBeliefAndKinematics.cs`) and excessive subdivision (many tiny files where one concept is fragmented — e.g., `World/PositionField.cs` + `World/VelocityField.cs` + `World/AccelerationField.cs` + `World/JerkField.cs` when one `KinematicTracker.cs` would do).
[RATIONALE] Granularity is chosen for code clarity at the call site, not for spec-set tidiness or refactoring future-proofing. A file is the unit a future reader loads to understand one concept; both god classes and overly granular suites force that reader to load more context than the concept needs. The ~3–7 number is not a hard cap — a single coherent subsystem with two clean files is fine, and a subsystem with twelve genuinely distinct responsibilities is also fine — it is the gravity point that calls a violation to attention when crossed in either direction.

---

## SECTION 6: TECHNICAL IDENTITY

[NORMATIVE]

| Field | Value |
|---|---|
| `IAIForm.Id` | `Smart` (working; final per OD-1) |
| `IAIForm.DisplayName` | `Smart` (working; final per OD-1) |
| Source location | `TAC_AI/AI/Forms/Smart/` |
| Spec set location | `TAC_AI/AI/Forms/Smart/Docs/` |
| Persistent profile location | `<mod-dir>/SmartAI/Profiles/<player-id>.<ext>` |
| Persistent profile format | OD-6 |
| Game engine | **Unity 2018.4.13f1 LTS** |
| Scripting backend | **.NET Framework 4.6.1** |
| C# language version | **7.3** |
| Runtime dependencies | TerraTech engine; TAC AI shell (`IAIForm`, `AIFormRegistry`, `TankAIHelper`, `IAIContext`, `EControlOperatorSet`, `IMovementAIController`, `MovementControllerBase`); workshop deps already referenced by TAC AI (`SafeSaves`, `TerraTechETCUtil`, `0Harmony`); no third-party ML library; **no Unity 2019+ packages** (no Jobs, no Burst, no Mathematics, no UI Toolkit) |
| Build configuration | Release excludes Smart's `Training/` subtree via conditional compilation; symbol per OD chosen when Training contract is authored |

---

## SECTION 7: OPEN DECISIONS

These are decisions the user has not yet made. Each blocks specific work; each names where it will be resolved.

[NORMATIVE]

**OD-1: Form name.** Working name "Smart." Final pinned after design has shape. Affects `IAIForm.Id`, `IAIForm.DisplayName`, folder name `TAC_AI/AI/Forms/Smart/`, profile-directory name `<mod-dir>/SmartAI/`, and every reference to "Smart" in this spec set.

**OD-3: Compute-budget policy. [RESOLVED v0.2.2]** Resolution: defer to self-play measurement. v1 ships with one hard rule — **Smart MUST NOT cause framerate to drop below the per-platform target framerate**. Per-subsystem compute-budget allocations are tuned during self-play (workflow step 1.11) and pinned before release. Specific budget values (ms per frame, per subsystem) are not pre-committed — they are measured outputs, not invented inputs (per §5.4). Per-platform target framerate is itself a downstream value; placeholder for desktop releases is 60 FPS. See [ARCHITECTURE.md §2.3](ARCHITECTURE.md#23-compute-budget-gating).

**OD-4: Threading model details. [RESOLVED v0.2.2]** Resolution: Smart owns a single **work-stealing worker pool** sized `Math.Max(2, Environment.ProcessorCount - 2)`. The `-2` reserves cores for the Unity main thread and renderer; the `Max(2, …)` keeps Smart parallelizable on dual-core systems. Normal thread priority — workers MUST NOT preempt rendering. No hard upper cap on worker count (per §5.4). Subsystem contract details — lock-free queue choice, double-buffer primitive shape, cancellable-task implementation, worker-registration discipline — are deferred to the Threading subsystem contract when authored. See [ARCHITECTURE.md §2.1](ARCHITECTURE.md#21-what-runs-where).

**OD-5: ML model architectures and sizes.** Per-model parameter count and inference cost (§1.6). Decided in the Learning subsystem contract.

**OD-6: Persistence format.** JSON, binary, versioning scheme for profile files. Decided in the Learning subsystem contract. The choice MUST satisfy DOCTRINE.md §2.4 (recoverability) and §2.8 (schema changes are recorded migrations).

**OD-7: Multiplayer determinism boundary. [RESOLVED v0.2.2]** Resolution: **host-authority**. Smart runs substantively only when the shell signals `host==true` (MP host or single-player). When `host==false` (MP client), Smart's per-tick hooks are called but perform no substantive work — they MAY update display-only state, but MUST NOT independently compute control or dispatch worker requests. The host's control output is replicated to clients via TerraTech's existing net layer. Workers run only on the host. Smart's design is therefore unconstrained by determinism: ambient RNG, parallel work ordering, and timing-based decisions are all permitted. See [ARCHITECTURE.md §3.2](ARCHITECTURE.md#32-mp-hostclient-gating).

**OD-8: Attract/title-screen behavior. [RESOLVED v0.2.2]** Resolution: **demo behaviors**. Smart runs in non-mission states (attract screen, main menu, game-over screen) in a demonstration mode. Running Smart in attract serves three purposes simultaneously: showcasing capabilities (player-facing), exercising Smart in a controlled environment (development/QA), and generating training data (self-play continuation). The specific demo content — a live self-play match, a scripted maneuver showcase, an alternating-form exhibition, or another mechanism — is owned by the Training subsystem contract (or a dedicated attract-mode subsystem) when authored. See [ARCHITECTURE.md §3.3](ARCHITECTURE.md#33-attract--out-of-mission-behavior). Sub-question [OPEN] OD-8a — whether demo IS the self-play harness running visibly, or a separate mechanism — recorded in ARCHITECTURE §3.3.

**OD-9: Modified default-fallback gap. [RESOLVED v0.2.3 — leave as-is during spec phase]** Status: the user deliberately removed the `Modified/` subtree from disk to prevent context pollution while constructing Smart (consistent with [§5.1](#section-5-ai-collaborator-directives) "do not consult Modified's source while designing Smart" and DOCTRINE.md §6.4). The `.csproj` retains ~100 phantom `<Compile Include="AI\Forms\Modified\..." />` entries left in place by design.

User directive: leave the phantom entries alone unless they interfere with the current task.

Implications:
- **Spec phase (current):** zero impact. Spec work is design, not build.
- **Implementation kickoff (workflow step 1.3 onward):** the phantom entries WILL block compilation. Resolution at that point (developer's workspace state, not a permanent codebase change) — comment out the `AI\Forms\Modified\*` `<Compile>` directives locally, or remove them, or restore Modified if the user wants the default-fallback to point at a real form.
- **`AIFormRegistry.DefaultFormId = "Modified"`** at runtime: with Modified absent and Smart + Vanilla registered, `SetActive("Modified")` falls through to whichever form sorts first by id; the persisted `KickStart.AIFormSelected` is applied later when the options panel publishes its dropdown ([SHELL-API-GUIDE.md §3.4](SHELL-API-GUIDE.md#34-bootstrap)).

**OD-10: Mod-unload `DeInitGlobal` gap. [PARTIALLY RESOLVED v0.2.2 — confirmed; fix pending]** Confirmed by Layer 3 research: `AIModuleBootstrap.DeInitAIModules()` calls `AIFormRegistry.Clear()` without first calling `SetActive(null)` or `Active.DeInitGlobal()`. If Smart is active at mod-unload, Smart's workers leak. Two resolution paths:
- **Shell-side fix (recommended):** add `active?.DeInitGlobal()` to `AIFormRegistry.Clear()` as a defensive last call. One-line change; benefits Vanilla and any future form. Requires a shell-level commit, which is outside Smart's spec set's authority.
- **Smart-side workaround:** Smart wraps `KickStart.DeInitAIModules` via Harmony to run worker teardown before `Clear()` runs. Less clean; introduces a Smart-specific shell coupling.

The user's choice between these paths is pending. Smart's Threading subsystem contract assumes one of them happens; the registry-cooperation discipline in [THREADING-CONTRACT.md §7](THREADING-CONTRACT.md#section-7-worker-lifecycle-registry) works correctly when `DeInitGlobal` runs; it does not protect against the bypassed-`Clear()` case without a workaround. See [SHELL-API-GUIDE.md §10](SHELL-API-GUIDE.md#section-10-kickstart-lifecycle).

[INFORMATIVE] OD-2 ("Modified policy" — frozen vs. walked back) is about Modified's evolution, not Smart's, and is not tracked in Smart's spec set. The gap in the numbering is deliberate.

---

## SECTION 8: WHAT THIS DOCUMENT IS NOT

[INFORMATIVE]

This document is not Smart's design. It is the project-level container around Smart's design. The design lives in subsystem contracts, authored as the workflow reaches them.

This document is not a Modified specification. Modified's policy, tuning, and evolution are not Smart's concerns; references to Modified here are limited to the "Smart does not consume Modified's internals" boundary statement (§1.10, §3.3).

This document is not authority for the shell. The shell's contract is `IAIForm` and the surrounding `TankAIHelper` / `AIFormRegistry` / `IAIContext` code, documented from Smart's consumption side in [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md). Shell-side changes belong in shell-side spec work, which is outside Smart's spec set.

This document is not a tutorial on `IAIForm` or on the TAC AI mod. New sessions reading this document are expected to have read DOCTRINE.md and to be familiar with the TAC AI mod's general purpose.

---

END OF FORM-SPECIFICATION.md v0.2.0
