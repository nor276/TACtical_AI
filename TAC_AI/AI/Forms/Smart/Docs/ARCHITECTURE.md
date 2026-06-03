# ARCHITECTURE.md

**Form:** Smart
**Version:** 0.2.1
**Status:** AUTHORITATIVE — Component map, threading model, tick lifecycle, error categories, and cross-cutting invariants for the Smart form.

---

## CHANGES SINCE 0.2.0

- §2.1 (What runs where) now specifies the work-stealing pool sizing formula per resolved OD-4.
- §2.3 (Compute-budget gating) now states the soft-cap policy per resolved OD-3.
- §3 (Tick lifecycle) restructured into subsections; added §3.2 (MP host/client gating) per resolved OD-7 and §3.3 (Attract / out-of-mission behavior) per resolved OD-8.
- §7 changed from "Open Decisions Owned Here" to "Architecture Decisions" showing all four ODs resolved.

## CHANGES SINCE 0.1.0

- Removed deference to `NEW_FORM_INTENT.md`. All component-map and threading content is now stated directly as Smart's design, not by reference to the intent doc.
- Component map (§1) now lists subsystem directories as Smart's source organization, with each entry's scope stated inline.
- Removed the per-subsystem-contract deference matrix (the 10 contracts that table pointed at were deleted as premature per doctrine §8.3).
- Pruned the cross-subsystem invariants from four to two: kept I1 (single-writer per tech-state field) and I3 (workers terminate within DeInitGlobal); dropped I2 and I4 as speculative for designs that don't exist yet.

---

## SECTION 0: AUTHORITY AND READING ORDER

This document is **authoritative** for: the Smart source-tree organization; the threading model (what runs on which thread, marshalling discipline, compute-budget gating); how `IAIForm` hooks map onto Smart's internal event loop; the error-handling categories Smart uses; cross-subsystem invariants; OD-7 (MP determinism) and OD-8 (attract behavior).

This document **defers to** [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals, ownership boundary, and AI-collaborator directives; [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md) for shell hook signatures and thread affinity; [DOCTRINE.md](../../../../../Doctrine%20Documentation/DOCTRINE.md) for methodology.

This document **governs** subsystem contracts as they are authored. Subsystem contracts defer here for cross-cutting concerns (threading, lifecycle, error handling, compute budget).

Reading conventions per DOCTRINE.md §4.

---

## SECTION 1: COMPONENT MAP

[NORMATIVE] Smart's source tree under `TAC_AI/AI/Forms/Smart/`:

```
SmartForm.cs                     IAIForm wrapper; thin dispatch into the event loop
Docs/                            this spec set
World/                           belief state, perception, event bus, world model
Vehicle/                         per-tech physical model (mass, thrust, weapons, armor, kinematics, mobility)
Planning/                        tactical optimizer, strategic planner, plan library, MCTS
Control/                         continuous controller (MPC), maneuver library, weapon-fire timing, energy scheduling
Pathing/                         trajectory optimization, terrain map, threat field
Coordination/                    shared belief, target assignment, role assignment, in-team comms
Learning/                        online models, training pipeline, persistent per-player profile I/O
Diagnostics/                     decision logging, performance monitoring, debug viz, replay logs
Training/                        self-play harness, evolutionary search, scenario generation (DEV-ONLY)
Threading/                       worker pool, double-buffer, lock-free event queue, cancellable tasks
Profiles/                        persistent profile data (runtime, not source)
```

[NORMATIVE] **`SmartForm.cs` is the only Smart source file the shell names.** The shell sees `SmartForm : IAIForm` and nothing else. All subsystem types are private to Smart.

[NORMATIVE] **Smart adds no plugin extension points.** The 10 subsystem folders are a fixed structural set, not a registry of pluggables. Adding an 11th subsystem is a spec revision, not a runtime extension. Per DOCTRINE.md §2.3, registries earn their cost only when there are multiple growing variants; none of Smart's subsystems are multi-variant.

[NORMATIVE] Each subsystem will have its own contract document (e.g., `WORLD-CONTRACT.md`) authored when the workflow reaches that subsystem. Until each contract is authored, the corresponding subsystem's design lives in the user's conversation context, not in this spec set. This document does not pre-stub the missing contracts (per DOCTRINE.md §8.3: component specs are inline as code is written).

---

## SECTION 2: PROCESS AND THREADING MODEL

### 2.1 What runs where

[NORMATIVE]

| Layer | Thread | Cadence |
|---|---|---|
| `IAIForm` shell hooks (`InitGlobal`, `OnTechSpawn`, `Directors`, `Operations`, `PostUpdate`, `ControlFrame`, etc.) | Unity main | Per shell hook (see [SHELL-API-GUIDE.md §2](SHELL-API-GUIDE.md#section-2-the-iaiform-interface)) |
| Smart's reactive event handlers (damage, projectile, block destroyed, etc.) | Unity main | When the corresponding shell event fires |
| Smart's ambient ~1 Hz vital-signs pass | Unity main | Driven by an internal timer checked from `Operations` |
| Perception (world-model maintenance) | Form-owned worker | Continuous; published via double-buffer swap |
| Tactical optimizer (per-tech gradient ascent) | Form-owned worker | Per-tech, ~60 Hz target |
| Strategic planner / MCTS | Form-owned worker | Team-level, ~2–5 Hz target |
| Pathing (trajectory optimization through cost field) | Form-owned worker | On request |
| Continuous controller (MPC solve) | Form-owned worker | Per physics tick, budget-permitting |
| `ControlFrame` consumption of pre-computed control | Unity main | Per frame |
| Online learning gradient updates | Form-owned worker | When minibatch is ready |
| Self-play harness (DEV-ONLY) | Form-owned worker(s) | Active only in dev builds with self-play enabled |

[NORMATIVE] **Worker pool sizing.** Smart owns a single **work-stealing worker pool** sized `Math.Max(2, Environment.ProcessorCount - 2)`. The `-2` reserves cores for the Unity main thread and renderer; the `Max(2, …)` keeps Smart parallelizable on dual-core systems. Workers run at **normal thread priority** — workers MUST NOT preempt rendering. No hard upper cap on worker count is enforced (per [FORM-SPECIFICATION.md §5.4](FORM-SPECIFICATION.md): invented defensive bounds without measurement are forbidden); if measurement later shows diminishing returns past N workers, the cap is added with the measurement as its rationale.

[NORMATIVE] **Work-stealing over dedicated** because Smart's task mix is heterogeneous (tactical: fast; strategic: slow; perception: continuous; pathing: bursty). Work-stealing maximizes utilization under heterogeneity; dedicated pools waste cycles when one subsystem is busy while others idle.

The pool's lock-free primitives, cancellable-task implementation, double-buffer shape, and worker-registration discipline are owned by the Threading subsystem contract when authored. This document fixes only the pool size, the work-stealing choice, and the priority.

### 2.2 Marshalling discipline

[NORMATIVE] All cross-thread data flow follows two rules:

**R1 — Workers never touch TerraTech engine objects.** No `Tank`, `TankBlock`, `Visible`, `Rigidbody`, `Transform`, `ManXxx.inst` access from any form-owned worker. The Unity API is main-thread-only; violation produces undefined behavior at unpredictable times. Workers read plain-data snapshots (POD, struct, sealed class with primitive/value fields) only.

**R2 — Snapshots are double-buffered.** The main thread writes the next snapshot (typically from a shell-hook entry or event handler); workers read the previous snapshot. Atomic publish swaps the references. Reads during a swap see either the old or new snapshot — never a torn snapshot.

The double-buffer / lock-free queue / cancellable-task primitives are owned by the Threading subsystem contract when authored.

### 2.3 Compute-budget gating

[NORMATIVE] **Soft-cap policy.** Smart MUST NOT cause framerate to drop below the per-platform target framerate. This is the **only hard constraint** on Smart's compute consumption. Per-subsystem budget allocations are tuned during self-play (workflow step 1.11) and pinned before release. Specific budget values (ms per frame, per subsystem) are not pre-committed; they are measured outputs of self-play, not invented inputs (per [FORM-SPECIFICATION.md §5.4](FORM-SPECIFICATION.md)).

[NORMATIVE] Per [FORM-SPECIFICATION.md §1.9](FORM-SPECIFICATION.md), every optimizer is anytime:

1. Returns a valid result on early cancellation.
2. Monotonically improves with additional compute.
3. Cancels within a bounded latency.

[NORMATIVE] Smart maintains a per-tick compute budget visible to all optimizer subsystems. Optimizers consume from the budget; when the budget is depleted, in-flight optimizers cancel.

[NORMATIVE] If an optimizer cannot return a useful result within its budget, Smart logs a warning naming the optimizer and proceeds with the best partial result. Per DOCTRINE.md §2.5, this is the "log and continue in degraded mode" pattern; the warning makes the degradation operator-visible.

Per-subsystem allocation within the soft-cap policy is the Diagnostics subsystem contract's responsibility when authored.

---

## SECTION 3: TICK LIFECYCLE

### 3.1 Per-tech tick order

[NORMATIVE] The shell calls Smart hooks per-tech in this order on each tick:

```
Directors(host)         ─── recalibrate movement controller; gate pathing for this tick
       │
       ▼
Operations(host)        ─── decision/orchestration entry point
       │   ├── checks Smart's internal timer; fires the 1 Hz ambient pass if due
       │   ├── ProfileRunner dispatches RunEnemy / RunAllied for this tech
       │   └── per-tech tactical optimizer requests are issued (consumed by workers)
       ▼
PostUpdate              ─── post-tick brain (lock-on, block hold)
```

Per frame (independent of per-tech tick rate):

```
ControlFrame(control)   ─── per-frame fast lookup against the latest published control solution
```

[NORMATIVE] **Smart's internal event loop runs above `Operations`.** Smart's reactive handlers (damage taken, projectile fired, block destroyed, etc.) fire when the corresponding shell event fires — on the main thread, outside the per-tick cadence. The ambient 1 Hz tick (checked from `Operations`) catches anything not event-driven.

[NORMATIVE] **`ControlFrame` is purely consumptive.** It MUST NOT do solver work. The MPC solve happens in a worker; `ControlFrame` reads the latest published control solution and writes to `TankControl`. Per [FORM-SPECIFICATION.md §5.8](FORM-SPECIFICATION.md).

### 3.2 MP host/client gating

[NORMATIVE] The shell calls per-tech hooks (`Directors`, `Operations`, `PostUpdate`) with a `host` boolean indicating whether the local node is the MP host (or in single-player). Smart's host-authority policy (per [FORM-SPECIFICATION.md §7 OD-7](FORM-SPECIFICATION.md#section-7-open-decisions)):

- **`host == true`:** Smart's substantive work runs. Workers dispatch optimizer requests; event handlers act on belief state; `Operations` may compute new tactical objectives.
- **`host == false`:** Smart's per-tick hooks are called but MUST perform **no substantive work**. Workers MUST NOT receive new requests from per-tick paths. Smart MAY update display-only state (e.g., a visualizer reading the replicated control output) but MUST NOT independently compute control, mutate the world model from observation, or run optimizers.

[RATIONALE] Host-authority eliminates the determinism constraints that would otherwise straitjacket Smart's design (parallel work ordering, ambient RNG, timing-based decisions). The cost is that clients do not have an independent local view of Smart's planning; the benefit is design freedom on the host. The trade favors Smart's "BEST AI" mandate.

[NORMATIVE] `ControlFrame` runs on both host and client (the engine reads the control bus regardless). On client, `ControlFrame` consumes the locally-replicated control state without recomputing it. Smart's `ControlFrame` implementation MUST tolerate being called with no preceding solver work on this node — the consumption shape is the same; the source of the published solution is just different.

[NORMATIVE] **Worker activation gate.** Smart's `InitGlobal` may still spin up workers on a client (so a mid-game host-handover does not require cold start), but workers MUST NOT receive substantive work while `host==false`. The check happens at the dispatch boundary, not inside each worker.

### 3.3 Attract / out-of-mission behavior

[NORMATIVE] Smart runs in non-mission states (attract screen, main menu, game-over screen) in a **demonstration mode**, per [FORM-SPECIFICATION.md §7 OD-8](FORM-SPECIFICATION.md#section-7-open-decisions). The shell's per-tech hooks are not called outside a mission (no techs exist), so demo activity is initiated by Smart on its own schedule.

[RATIONALE] Running Smart in the attract screen serves three purposes simultaneously: it demonstrates Smart's capabilities to the player (showpiece value), it exercises Smart in a controlled environment (development/QA value), and it generates training data (self-play continuation value). The latter aligns directly with the "BEST AI" mandate — every minute the player spends in menus is a minute of additional self-play-driven improvement.

[NORMATIVE] The specific demo content is owned by the Training subsystem contract (or a dedicated attract-mode subsystem) when authored. This document records only that Smart is **active in non-mission states**; what it does is a downstream decision.

[OPEN] **OD-8a: Demo content mechanism.** Whether the attract demo IS the self-play harness running visibly, or a separate showcase mechanism. The trade:
- **Demo == self-play:** Shares code; the player sees real Smart-vs-Smart matches; training data accumulates while menus are open. **But** self-play is currently scoped dev-only per [FORM-SPECIFICATION.md §1.7](FORM-SPECIFICATION.md) (Training/ excluded from release). If demo == self-play ships, the Training subsystem (or a release-permitted subset of it) must be release-included. This requires reopening [FORM-SPECIFICATION.md §1.7](FORM-SPECIFICATION.md)'s release-exclusion rule.
- **Demo == separate mechanism:** Self-play stays dev-only; demo is a smaller showcase (scripted maneuvers, a single-tech showpiece). Doesn't generate training data. Less code reuse.

Resolved in the Training subsystem contract when authored.

---

## SECTION 4: ERROR HANDLING

[NORMATIVE] Per DOCTRINE.md §2.5, nothing in Smart dies silently. Failure categories:

**E1 — Worker thread failure.** A Smart-owned worker thread throws an uncaught exception.
*Pattern:* log a warning naming the worker and the exception. Restart the worker if it has retry budget; fail with a loud descriptive error if retry budget is exhausted. Smart continues with reduced capability (the dependent optimizer falls back to its last-known-good output or to a heuristic default).

**E2 — Persistent-profile load failure.** A per-player profile file is corrupt or unreadable.
*Pattern:* log a warning naming the file and the corruption category. Preserve the corrupt file as `<player-id>.<ext>.corrupt-<timestamp>` so the player can investigate. Fall back to the shipped pretrained baseline. Per DOCTRINE.md §2.4, profile recoverability is non-negotiable.

**E3 — Compute-budget exhaustion.** An anytime optimizer is cancelled before producing a useful result.
*Pattern:* log a warning naming the optimizer and the budget exceeded. Proceed with the best partial result. Increment a starved-frame counter for [Diagnostics observability](SHELL-API-GUIDE.md#section-9-threading) when the Diagnostics contract is authored.

**E4 — Per-tech event-handler exception.** A Smart event handler (damage, block, projectile) throws while processing one tech.
*Pattern:* catch at the handler boundary; log a warning naming the tech and the exception; continue processing other techs. One bad tech does not take down Smart for all techs.

**E5 — `FormState` ownership-discipline violation.** `OnTechRecycle` or another hook encounters a `helper.FormState` that is not Smart's expected type (or is null when it should not be).
*Pattern:* fail loudly with a descriptive error. This is an invariant violation, not a recoverable runtime condition. The form-swap discipline in [SHELL-API-GUIDE.md §2.3](SHELL-API-GUIDE.md#23-ontechspawnontechrecycle--per-tech-lifecycle) is supposed to prevent this; if it occurs, the discipline is being violated and Smart needs to know.

[NORMATIVE] No `catch` block in Smart may produce only a debug-level log and continue. Either the catch produces an operator-visible signal (warning + degraded mode) or it rethrows. The "swallowed exception + debug log" pattern is forbidden per DOCTRINE.md §2.5.

---

## SECTION 5: CROSS-SUBSYSTEM INVARIANTS

[NORMATIVE] Invariants that no single subsystem can declare alone.

**I1 — Single-writer per tech-state field.** Each writable slot on `helper.FormState` and on each property of `IAIContext` is written by exactly one Smart subsystem. Per-field ownership is declared in each subsystem contract; the no-double-writer rule itself is enforced here. Two subsystems writing the same field is a bug, full stop.

**I3 — All workers terminate within `DeInitGlobal`.** Per [SHELL-API-GUIDE.md §9.3](SHELL-API-GUIDE.md#93-form-swap-and-worker-cancellation), a form swap MUST leave no Smart worker running. Every worker registers with the Threading subsystem's lifecycle at construction so `DeInitGlobal` can enumerate and cancel all of them. Workers that bypass registration are an invariant violation; failure to terminate within `DeInitGlobal` is a bug.

[INFORMATIVE] Two further invariants were drafted in v0.1.0 ("compute-budget consumers register at startup" and "per-player profiles save before player-leave returns") and removed at v0.2.0 as premature — they assume design patterns in subsystems that have not yet been designed. They will be reintroduced (or revised) when the owning subsystem contracts are authored.

---

## SECTION 6: EXTENSION POINTS

[NORMATIVE] Smart exposes zero plugin extension points internally. No registry of optimizers, no registry of learned models, no registry of maneuvers. Per [FORM-SPECIFICATION.md §3.3.6](FORM-SPECIFICATION.md) and DOCTRINE.md §2.3.

[RATIONALE] Smart owns its program top to bottom. A pluggable Smart-internal would create a second contract surface for Smart contributors; the cost of that surface is not paid back at Smart's scope.

---

## SECTION 7: ARCHITECTURE DECISIONS

[INFORMATIVE] The following decisions are owned by this document. All four resolved at v0.2.1.

**OD-3 — Compute-budget policy. [RESOLVED v0.2.1]** Defer to self-play measurement; the only hard constraint is "no framerate drop below per-platform target." Details: §2.3.

**OD-4 — Threading model basics. [RESOLVED v0.2.1]** Work-stealing pool sized `Math.Max(2, Environment.ProcessorCount - 2)` at normal thread priority. Details: §2.1.

**OD-7 — MP determinism boundary. [RESOLVED v0.2.1]** Host-authority. Substantive computation runs only when `host == true`; clients no-op (display-only state permitted). Details: §3.2.

**OD-8 — Attract / out-of-mission behavior. [RESOLVED v0.2.1]** Demo mode. Smart runs in non-mission states; specific demo content owned by Training subsystem contract. Sub-question OD-8a (demo == self-play?) [OPEN]. Details: §3.3.

---

## SECTION 8: WHAT THIS DOCUMENT IS NOT

[INFORMATIVE]

This document is not the design of any specific subsystem. Each subsystem's design lives in its own contract, authored when the workflow reaches it.

This document is not a Unity / TerraTech engine reference. The shell surface is documented in [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md); the engine surface is in the TerraTech assemblies (out of repo).

This document is not a tuning catalog. Specific values (worker count, queue capacity, profile size cap, compute-budget bytes) belong in their owning subsystem contracts when authored. Per DOCTRINE.md §6.10, each such value must trace to a stated real-world reason; this document does not pre-invent values to anchor them to.

---

END OF ARCHITECTURE.md v0.2.0
