# DIAGNOSTICS-CONTRACT.md

**Subsystem:** Diagnostics/
**Form:** Smart
**Version:** 0.1.0
**Status:** AUTHORITATIVE — Defines Smart's decision logging, compute-budget allocator, performance monitor, debug visualization, replay log, and the discipline for consuming diagnostic events emitted by every other Smart subsystem.

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The decision log: format, retention, per-decision record fields, persistence policy.
- The compute-budget allocator: per-subsystem allocation rules, dynamic rebalancing under load.
- The performance monitor: per-subsystem timing, queue-depth tracking, starved-frame counter.
- The debug visualization: what's drawn, when, with which color conventions; in-game toggle GUI.
- The replay log: tick-level delta log format, replay tooling.
- The discipline for subscribing to other subsystems' diagnostic events.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals, particularly §3.2.3 (decision-trace observability).
- [ARCHITECTURE.md](ARCHITECTURE.md) for compute-budget gating (§2.3 — Diagnostics enforces this), error categories, host/client gating.
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for the worker primitives Diagnostics observes.
- Each subsystem contract's "Diagnostics Integration" section for the events Diagnostics consumes.

**This document GOVERNS:**
- The unified compute-budget surface that all anytime optimizers obey.
- The decision-log format that future tooling reads.

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What Diagnostics owns

Five coupled concerns:
1. **Decision log** — append-only record of every substantive Smart decision (target chosen, plan selected, retreat triggered, etc.).
2. **Compute budget allocator** — distributes the per-tick budget across subsystems; rebalances dynamically.
3. **Performance monitor** — measures actual per-subsystem cost; reports starved frames.
4. **Debug visualization** — in-game overlays (belief covariances, threat field, planned paths, current target assignments).
5. **Replay log** — full tick-level delta record for offline analysis.

### 1.2 Decisions resolved at v0.1.0

[NORMATIVE]
- **Decision log format: append-only binary with index.** Per-entry record is ~50 bytes; per-session log file with separate index for fast replay. Format owned in §2.
- **Compute budget allocator: priority-based dynamic.** Each subsystem has a baseline allocation; under load, budget shifts from low-priority work to high-priority. Mechanism in §3.
- **Performance monitor: sampled per-subsystem.** Tracks p50/p99 latencies, queue depths, drop counts, starved-frame events. Lightweight.
- **Debug viz: opt-in per layer.** Each layer (belief, threat, paths, targets, rollouts) has its own toggle. Hidden by default; enabled via a debug GUI (`DrawPathingDebugGUI` is the entry point).
- **Replay log format: delta-based, tick-aligned.** Each tick records only what changed since the previous tick. Replayable via a separate offline tool.

### 1.3 Opportunistic authorship

[NORMATIVE] Per [WORKFLOW.md §3](WORKFLOW.md#section-3-the-diagnostics-subsystem-is-opportunistic), Diagnostics is authored alongside whichever subsystem currently needs it. v0.1.0 documents the full intent; specific implementations land progressively. Subsystems land their diagnostic event emitters first (per their contracts' "Diagnostics Integration" sections); Diagnostics subscribes and consumes when its implementation arrives.

---

## SECTION 2: DECISION LOG

### 2.1 Per-entry record

[NORMATIVE] Each decision log entry is a fixed-size binary record:

```
struct DecisionLogEntry  // 48 bytes
{
    long Timestamp;            // 8 bytes — host clock ms
    int TechId;                // 4 bytes — relevant tech (0 if team-level)
    int SubsystemId;           // 4 bytes — see §2.2
    int DecisionKindId;        // 4 bytes — see §2.2
    int InputSummaryHash;      // 4 bytes — hash of relevant inputs (for deduplication)
    int OutputHash;            // 4 bytes — hash of decision outcome
    int RationaleTagId;        // 4 bytes — see §2.4
    int SequenceId;            // 4 bytes — monotonic per-session
    int Reserved;              // 4 bytes — padding for alignment
    long ParentDecisionId;     // 8 bytes — for tracing decision causality
}
```

[NORMATIVE] Records are written to an append-only file (`<mod-dir>/SmartAI/Logs/decisions-{session_start_ms}.bin`) plus an in-memory ring buffer of the last N=10000 entries for fast in-game inspection.

### 2.2 Subsystem and decision-kind taxonomies

[NORMATIVE] Subsystem IDs are stable integers:
- 1 World, 2 Vehicle, 3 Control, 4 Planning, 5 Coordination, 6 Pathing, 7 Learning, 8 Training, 9 Threading, 10 (reserved).

[NORMATIVE] Decision kinds are subsystem-scoped:
- World: `BeliefSeen`, `BeliefLost`, `BeliefReacquired`, `IntentClassified`.
- Vehicle: `VehicleRebuilt`, `WeakFaceIdentified`.
- Control: `TacticalGoalChanged`, `MPCProfilePublished`, `WeaponFired`, `WeaponHeld`, `WeaponSwitched`.
- Planning: `StrategicPlanChosen`, `PlanSwitched`.
- Coordination: `TargetReassigned`, `RoleSwitched`, `LOSGained`, `LOSLost`.
- Pathing: `PathOptimized`, `PathFailed`.
- Learning: `ProfileLoaded`, `MinibatchTrained`, `ModelDiverged`.

[NORMATIVE] Each decision kind is identified by `(SubsystemId, KindIndex)`; the catalog is owned in `DiagnosticsTaxonomy.cs`.

### 2.3 Per-decision rationale

[NORMATIVE] `RationaleTagId` indexes into a static rationale dictionary — a short string explaining why this decision was made:

```
Examples:
  "ExpectedValue > threshold"
  "HysteresisOverride"
  "Plan changed: Engage → Retreat"
  "Compute budget exceeded; partial result"
  "Belief covariance crossed threshold"
```

[NORMATIVE] The dictionary is loaded once per session. Rationale strings are interned; IDs are reused across decisions of the same kind for the same reason.

### 2.4 Retention

[NORMATIVE] Decision logs persist until:
- Session ends — files remain on disk.
- Disk fills — log rotation deletes oldest session files. (Provisional: keep last 30 days or 100 MB total, whichever is less. Cleanup runs at session start.)

[NORMATIVE] In-memory ring buffer (last 10000 entries) is always accessible during a session via the in-game debug GUI.

### 2.5 Replay tooling

[NORMATIVE] An offline replay tool (in the `Smart.Training.Tools/` sub-namespace) reads decision log files and reconstructs the sequence of decisions for analysis. The tool is dev-only (`#if SMART_DEV`).

---

## SECTION 3: COMPUTE BUDGET ALLOCATOR

### 3.1 The budget surface

[NORMATIVE] Per-tick compute budget is expressed as a fixed-point time value (microseconds). The allocator publishes a per-subsystem allocation each tick:

```
public static class ComputeBudgetAllocator
{
    public static int AllocationFor(SubsystemId subsystem);   // microseconds for this tick
    public static int RemainingFor(SubsystemId subsystem);    // updated as work consumes
    public static void ConsumeFor(SubsystemId subsystem, int microseconds);
    public static bool ShouldYield(SubsystemId subsystem);    // true when budget exhausted
}
```

[NORMATIVE] Anytime optimizers check `ShouldYield(...)` at known yield points; on `true`, they cancel and publish their current best result.

### 3.2 Base allocation (provisional)

[NORMATIVE] Default per-tick budget allocations (provisional per FORM-SPEC §1 disclaimer):

| Subsystem | Baseline allocation per Operations tick |
|---|---|
| World perception | 1000 µs |
| Vehicle rebuild | 500 µs |
| Tactical optimizer (per tech) | 200 µs (× tech count) |
| MPC (per tech) | 500 µs (× tech count) |
| Planning strategic | 2000 µs (every ~200 ms) |
| Coordination | 300 µs |
| Pathing | 1500 µs |
| Learning training | 500 µs |

[NORMATIVE] Total at typical scales (5 techs): ~12 ms per tick. Within FORM-SPEC OD-3 soft cap (per platform target framerate).

### 3.3 Priority-based dynamic rebalancing

[NORMATIVE] When a high-priority subsystem indicates it needs more budget (via `RequestMore(subsystem, additionalMicroseconds)`), the allocator shifts budget from lower-priority subsystems for the current tick only:

Priority order (provisional, descending):
1. Control (MPC + tactical) — directly affects per-frame motion
2. World perception — feeds everything
3. Pathing — feeds Control
4. Planning + Coordination — high-level but slow-changing
5. Vehicle rebuild — only matters when blocks change
6. Learning training — can wait

[NORMATIVE] Donations come from low-priority subsystems' allocations. If the request exceeds available donations, it's partially granted.

### 3.4 Starved-frame events

[NORMATIVE] When a subsystem's `ShouldYield` returns true before that subsystem has produced a useful result (per anytime contract), the allocator fires a `StarvedFrame(subsystem)` event. Diagnostics counts these; sustained starvation across many ticks indicates per-platform budget is too tight.

---

## SECTION 4: PERFORMANCE MONITOR

### 4.1 Sampled instrumentation

[NORMATIVE] Each subsystem reports its actual per-tick cost via:

```
public static class PerfMonitor
{
    public static void RecordDuration(SubsystemId subsystem, int microseconds);
    public static void RecordQueueDepth(string queueName, int depth);
    public static void RecordEvent(EventCategory category);
    public static PerfReport CurrentReport();
}
```

[NORMATIVE] PerfMonitor maintains rolling statistics per subsystem:
- p50, p95, p99 duration over the last 1000 ticks.
- Mean and peak queue depths.
- Event-category counts (Threading drops, Smart event-bus dispatches, etc.).

[NORMATIVE] Reports are accessible via the in-game debug GUI and exported to a per-session perf log on session end.

### 4.2 Frame timing

[NORMATIVE] PerfMonitor subscribes to Unity's frame timing (via `Time.deltaTime` sampling) and computes:
- Mean framerate over the last 60 frames.
- Frame timing variance.
- Per-tick "framerate impact" — how much Smart's compute contributed to deviation from target framerate.

[NORMATIVE] When framerate-impact exceeds threshold (provisional: 2× per-platform target), an `OverBudget` event fires. The allocator (§3) responds by tightening allocations.

---

## SECTION 5: DEBUG VISUALIZATION

### 5.1 Visualization layers

[NORMATIVE] Six optional in-game viz layers:

| Layer | What it draws |
|---|---|
| `BeliefCovariances` | Per-tech belief mean as a sphere; covariance as an ellipsoid; intent distribution as a color tint. |
| `ThreatField` | Threat field as a heat map projected on the ground plane. |
| `PlannedPaths` | Per-tech planned B-spline trajectories. |
| `TargetAssignments` | Lines from our techs to their assigned targets. |
| `MPCRollouts` | Last N candidate MPC rollouts (greyed out by cost weight). |
| `LOSCoverage` | LOS rays from friendlies to enemies (colored by recency). |

[NORMATIVE] Each layer is independently toggleable via the debug GUI (`DrawPathingDebugGUI`).

### 5.2 Performance impact

[NORMATIVE] Each layer's render cost is reported via PerfMonitor. Layers that exceed a threshold (provisional: 0.5 ms) print a warning suggesting they be disabled in active gameplay.

### 5.3 Render protocol

[NORMATIVE] Rendering uses Unity's Gizmos or LineRenderer APIs. All renders are gated on the layer-enabled flag; disabled layers do no work. No rendering happens on client (per ARCHITECTURE §3.2 host-authority — clients don't have the data anyway).

---

## SECTION 6: REPLAY LOG

### 6.1 Format

[NORMATIVE] The replay log is a tick-delta log:

```
struct ReplayDelta
{
    long Tick;
    DeltaCategory Category;     // BeliefChanged, PlanChanged, TargetMapChanged, ControlProfileSampled, etc.
    int PayloadLength;
    byte[] Payload;             // category-specific
}
```

[NORMATIVE] Each tick, subsystems publish deltas of their published state since the previous tick (e.g., only beliefs that updated; only target assignments that changed). The log file is a sequence of these deltas, prefixed with a session header that includes scenario metadata and starting state.

[NORMATIVE] Files: `<mod-dir>/SmartAI/Replays/replay-{session_start_ms}.bin`. Same retention policy as decision logs (§2.4).

### 6.2 Compression

[NORMATIVE] v0.1.0 does NOT compress replay logs; revisit if files exceed 100 MB per session.

### 6.3 Replay tool

[NORMATIVE] The offline replay tool (`Smart.Training.Tools.ReplayPlayer`) reconstructs a session from the log: timeline scrubbing, per-subsystem state inspection at each tick, decision-log overlay. Dev-only.

---

## SECTION 7: EVENT SUBSCRIPTIONS

[NORMATIVE] Diagnostics subscribes (on main thread, during `InitGlobal`) to every subsystem's diagnostic event surface. Each subscription routes events to:
- Decision log entries (where applicable).
- Performance monitor counters.
- Debug viz updates.
- Replay log entries.

[NORMATIVE] The subscription table is owned in `DiagnosticsService.cs`. Adding a new diagnostic event from a subsystem requires updating this table; the discipline is "no event is consumed silently."

### 7.1 Default handlers

[NORMATIVE] Per [THREADING-CONTRACT.md §10](THREADING-CONTRACT.md#section-10-diagnostics-integration) and each contract's diagnostics section: subsystems' diagnostic events have default handlers (typically `DebugTAC_AI.Log` at INFO/WARNING level) registered before Diagnostics is authored. When Diagnostics is authored, default handlers are replaced with Diagnostics' own. The transition is opaque to the emitter.

---

## SECTION 8: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/Diagnostics/` contains six files:

| File | Owns |
|---|---|
| `DiagnosticsService.cs` | Orchestration, subscription table, lifecycle. |
| `DecisionLog.cs` | Append-only log, in-memory ring buffer, retention policy. |
| `ComputeBudgetAllocator.cs` | Per-subsystem allocation, dynamic rebalancing, starved-frame events. |
| `PerfMonitor.cs` | Duration/queue/event instrumentation, rolling stats, frame-timing observation. |
| `DebugViz.cs` | In-game overlay rendering, layer toggles, viz GUI. |
| `ReplayLog.cs` | Tick-delta logging, file management. |

Six files within [FORM-SPEC §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives)'s range. Each owns one observability concern.

Sub-namespace `Smart.Training.Tools` (under `Training/`, `#if SMART_DEV` only) contains the offline replay tool and decision log inspector. Not part of Diagnostics' six files.

---

## SECTION 9: HOST/CLIENT BEHAVIOR

[NORMATIVE] Per ARCHITECTURE §3.2:
- **Host:** All Diagnostics active. Logs written. Viz available.
- **Client:** Diagnostics inactive. Logs not written. Viz disabled (no data to render). Compute budget allocator runs but allocates 0 to all subsystems (clients no-op).

---

## SECTION 10: OPEN ITEMS

[OPEN] **Decision-kind catalog expansion.** Initial set in §2.2; expand as subsystems land and identify decisions worth logging.
[OPEN] **Per-platform compute budget targets.** OD-3 in FORM-SPEC; specific values are downstream of self-play measurement.
[OPEN] **Rationale dictionary contents.** Built up as subsystems are implemented; specific strings tuned as needed.
[OPEN] **Replay tool features.** Beyond timeline scrubbing, what advanced features (diff between sessions, A/B comparison, etc.).
[OPEN] **Compression for replay logs.** Off at v0.1.0.
[OPEN] **Networked diagnostic streaming.** Could stream events to an external tool for analysis. Not at v0.1.0.
[OPEN] **Telemetry export.** Should successful playthroughs feed back to Training (with player consent)? Not at v0.1.0; revisit.

---

## SECTION 11: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not the subsystems' diagnostic event emitters. Each subsystem owns its own event emission; Diagnostics owns the consumption side.

This contract is not Smart's runtime decision-making. Diagnostics observes; it does not decide.

This contract is not Unity's standard profiler. The PerfMonitor is Smart-internal — for Smart's compute budget and starvation tracking, not for general Unity profiling.

This contract is not user-facing analytics. Diagnostics is for developers / dev-mode players; release builds disable all but `DebugTAC_AI` error logging (per ARCHITECTURE §4).

---

END OF DIAGNOSTICS-CONTRACT.md v0.1.0
