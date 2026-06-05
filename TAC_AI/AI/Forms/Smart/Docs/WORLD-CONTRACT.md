# WORLD-CONTRACT.md

**Subsystem:** World/
**Form:** Smart
**Version:** 0.1.0
**Status:** AUTHORITATIVE — Defines Smart's belief state, perception worker, Smart-internal event bus, hidden-state inference, and snapshot publication to downstream subsystems.

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The per-tech `BeliefState`: multivariate Gaussian over continuous state + categorical distribution over intent.
- The Bayesian update math on re-acquisition (Kalman filter).
- The decay model when a tech is out of sight (acceleration-bounded variance growth + terrain biasing).
- The Smart-internal event bus: which events exist, who publishes them, dispatch discipline.
- The perception worker: cadence, scheduling, marshalling discipline.
- The world-model snapshot publication (per-tech belief snapshots + fused world snapshot).
- Shell event subscriptions: World owns the shell-side subscriptions and re-broadcasts as Smart-internal events.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals, AI-collaborator directives.
- [ARCHITECTURE.md](ARCHITECTURE.md) for threading model, MP host/client gating, cross-subsystem invariants.
- [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md) for shell event signatures and `Tank` field access patterns.
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for `DoubleBuffer<T>`, `WorkerPool`, marshalling patterns.
- [VEHICLE-CONTRACT.md](VEHICLE-CONTRACT.md) for `VehicleModelSnapshot` and `KinematicState` (which World consumes as observation input).

**This document GOVERNS:**
- The event bus that other subsystems subscribe to. Planning, Pathing, Control, Learning, Diagnostics all consume World's Smart-internal events.
- Per-tech belief snapshots consumed by Planning (expected utility integration), Pathing (threat field), Control (target prediction).

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What this contract owns

World/ owns three coupled concerns:
1. **Per-tech belief state** — what Smart believes about every tech it has ever observed, expressed as a probability distribution.
2. **Smart-internal event bus** — the publish/subscribe primitive that translates shell events into Smart-internal events and broadcasts them to consumers.
3. **Perception worker** — the off-main-thread loop that maintains belief state from main-thread observations.

### 1.2 Decisions resolved at v0.1.0

[NORMATIVE]
- **Belief representation:** **multivariate Gaussian over continuous state + categorical distribution over intent.** Continuous state is (position, velocity, heading) as a 7-dimensional Gaussian with full 7×7 covariance. Intent is a discrete distribution over a small set of categories. Resolved at the Layer 4 World Q&A round.
- **Event flow:** **Smart-internal event bus, owned by World.** World subscribes to shell events and re-broadcasts as Smart-internal events. Other subsystems subscribe to the bus. Resolved at the Layer 4 World Q&A round.
- **Re-acquisition update:** **Linear Kalman filter.** Process model is kinematic propagation; measurement model is direct observation of position/velocity. Process noise derived from Vehicle's max-acceleration estimate. Measurement noise per-observation-type.
- **Decay model:** **Kinematic variance growth bounded by max acceleration + terrain biasing.** Position covariance grows at a rate set by the kinematic uncertainty bound; terrain narrows the spread along blocked directions.
- **Perception cadence:** **~30 Hz target** for the perception worker. Tunable per OD-3 soft cap.

---

## SECTION 2: BELIEF STATE — AETHER TRACE MODEL

> **REV 7 (v0.2):** §2/§3/§4 were originally a 7D Gaussian + Kalman + covariance-decay design. The implementation that shipped is **Aether** — an immutable per-tech *trace* (last-observed anchors + age-driven uncertainty) computed by closed-form dead-reckoning rather than matrix arithmetic. Pre-Aether section text is preserved as `SUPERSEDED-BY-AETHER` cross-references in `AUDIT.md` / `FIX-PLAN.md`; this contract now describes the live design. Authoritative architectural narrative: [AETHER-DESIGN.md](AETHER-DESIGN.md).

### 2.1 Structure

[NORMATIVE] Per observed tech, the belief state is an **immutable trace** of the most recent observation, plus pre-baked coast-extrapolated fields and a single scalar `UncertaintyMeters`. The class name `BeliefState` is preserved — many consumers reference it — but the shape is **not** a Gaussian:

```csharp
public sealed class BeliefState   // sealed, immutable; constructed only via BeliefStateFactory
{
    public readonly TechId       Id;
    public readonly TeamId       Team;

    // Anchors — frozen at the last observation; never mutated thereafter.
    public readonly Vector3      LastSeenPosition;
    public readonly Vector3      LastSeenVelocity;
    public readonly Vector3      LastSeenForward;          // unit 3-vector; pitch/roll preserved (aircraft)
    public readonly long         LastObservedTickMono;     // Stopwatch.GetTimestamp source
    public readonly long         PublishedTickMono;        // the perception tick that built this trace
    public readonly float        SpeedAtObserve;           // |LastSeenVelocity|, cached
    public readonly float        MaxAccelerationEstimate;  // kinematic bound for uncertainty growth

    // Sight transitions — see §4 for the staleness state machine.
    public readonly SightState   Sight;                    // Fresh | Coasting | Stale | Lost

    // Pre-baked at publish time so consumers do field loads, not method calls.
    public readonly Vector3      PositionMean;             // coast-extrapolated to PublishedTickMono
    public readonly Vector3      VelocityMean;             // age-decayed to PublishedTickMono
    public readonly Vector3      ForwardXZ;                // LastSeenForward, XZ-normalized
    public readonly float        HeadingMean;              // Atan2(ForwardXZ.x, ForwardXZ.z), pre-baked
    public readonly float        AgeSeconds;               // cached at publish
    public readonly float        UncertaintyMeters;        // closed-form kinematic radius (single scalar)

    // Compatibility shims — same property names AS v0.1, zero consumer source change.
    public bool   InSight          => Sight == SightState.Fresh;
    public long   LastObservedTick => LastObservedTickMono;
    // PositionVariance: invariant `3·U²` shim satisfying both (pv.x+pv.z) and (pv.x+pv.y+pv.z) consumers.
    public Vector3 PositionVariance => new Vector3(UncertaintyMeters * UncertaintyMeters, …, …);

    // Coast-aware accessors — opt-in; consumers that need NOW values instead of publish-tick values.
    // dt resolves via MonoClock.TickFreq so callers don't thread it through.
    // Lost-state clamp: when Sight == Lost, PositionAt → LastSeenPosition, VelocityAt → zero,
    // ConfidenceAt → 0. None extrapolate beyond LostAfterSec.
    public Vector3 PositionAt(long nowMono);
    public Vector3 VelocityAt(long nowMono);
    public float   ConfidenceAt(long nowMono);             // 1.0 if Fresh; linear 1→0 across Stale; 0 if Lost
}
```

[NORMATIVE] `BeliefState` is sealed, immutable, and produced exclusively via `BeliefStateFactory`. The shape carries **no covariance matrix** and **no intent distribution** — both were removed in the Aether migration. The single uncertainty channel is `UncertaintyMeters`, derived from `(AgeSeconds, MaxAccelerationEstimate)` by a closed-form kinematic bound (§3.3).

[NORMATIVE] `Vector3 PositionVariance` is a back-compat shim. Live production consumers read it as a scalar trace (`pv.x + pv.z` in `SmartRuntime.SummarizeBelief`); the shim returns three identical squared-uncertainty entries so both 2-axis and 3-axis trace readers see consistent values. New code SHOULD read `UncertaintyMeters` directly.

### 2.2 Intent — DELETED IN AETHER, SIDECAR IN v0.2

[NORMATIVE] The v0.1 6-category intent distribution (`Aggressing / Retreating / Flanking / Repositioning / Holding / Idle`) is **not part of `BeliefState`**. The historical reasoning was that Learning's intent classifier would fill it; the live consumer count for this field across v0.1 + v0.2 is **zero**, so the field was removed entirely with Aether.

[NORMATIVE] **v0.2 sidecar (P4 Item 10):** intent classification is now an **opt-in producer-only sidecar** — `IntentRegistry` (`World/IntentRegistry.cs`), owned by `SmartRuntime.IntentSidecar`. The category enum and any consumer surface remain forward-looking; producers may write to the sidecar without affecting the trace. No v0.2 consumer reads intent. When intent does become consumed (post-v0.2), the registry feeds it; it never goes back into the immutable trace.

[RATIONALE] Keeping intent off the trace preserves the trace's hot-path size (~120 B per tech vs the prior ~250 B), avoids the publish-time cost of carrying a value no live consumer reads, and lets the sidecar be replaced or extended without touching the dozens of code paths that hold a `BeliefState` reference.

### 2.3 Immutability and publishing

[NORMATIVE] `BeliefState` is constructed once per Aether tick (or once at register) and never mutated. The Aether fuser (§3) publishes per-tech traces and a single fused `BeliefSnapshot` via `DoubleBuffer<T>`. Consumers read the latest snapshot lock-free.

[NORMATIVE] `BeliefSnapshot` is unchanged in shape from v0.1:

```csharp
public sealed class BeliefSnapshot
{
    public readonly long TickStamp;
    public readonly IReadOnlyDictionary<TechId, BeliefState> ByTech;
}
```

[NORMATIVE] **Fresh-dictionary-per-publish:** `ByTech` is a freshly-allocated `Dictionary<TechId, BeliefState>` each tick. This is the v0.1 `PerceptionWorker` pattern preserved verbatim — consumers may hold a snapshot reference across many ticks; mutating the dict would break them.

[RATIONALE] Per-tech buffers serve single-tech consumers (e.g., a Control loop). The fused snapshot serves team-level consumers (Planner, Coordinator, ThreatField). Both are cheap to maintain; the fused snapshot is built once per Aether tick from the same trace cache.

---

## SECTION 3: AETHER FUSION (REPLACES KALMAN UPDATE)

> **Migration note:** prior text described a 7×7 Kalman with per-tick covariance propagation and matrix gain. None of that exists in the v0.2 codebase. The four numerical guards that defended the matrix path (`HasTooLarge` reset, `sDiag` floor 1e-3, positive-diagonal clamp 1e-6, `Symmetrize`) are deleted with their owner. See [AETHER-DESIGN.md "Costs the redesign eliminates"](AETHER-DESIGN.md) for the K-amplification class and PSD-violation failure modes that no longer exist.

### 3.1 Fuser cadence and ownership

[NORMATIVE] `AetherFuser` (`World/AetherFuser.cs`) is the sole producer of `BeliefState` traces. It runs as a long-running worker enqueued by `SmartRuntime.Init` at the ~33 ms (~30 Hz) cadence preserved from v0.1.

[NORMATIVE] Each fuser tick:
1. **Drain** pending position observations from the per-tech intake queues (filled by main-thread `Observer.Submit` from `SmartForm.ObserveWorldTechsIfDue`, cadence-matched at ~33 ms).
2. **Per tech in the drain set:** build a new immutable `BeliefState` via `BeliefStateFactory.NewlyObserved(...)` — anchors snapshot the just-drained observation; pre-baked fields are computed inline.
3. **Per tech NOT in the drain set:** if the trace is younger than `LostAfterSec`, build a new immutable `BeliefState` via `BeliefStateFactory.Coast(prior, nowMono)` — anchors carry forward; pre-baked fields advance per the closed-form coast model (§3.2); `SightState` transitions per §4.
4. **Publish** the per-tech trace into the per-tech `DoubleBuffer`. Build the fused dictionary (§2.3); publish it via `FusedBuffer.Write`.

[NORMATIVE] The fuser owns its iteration order; no Operations or Coordinator code reads a partially-published snapshot. Publish points (per-tech buffer write + fused buffer write) are the *only* memory-visibility barriers consumers see — between them, the fuser may take seconds to traverse a large tech population without breaking anyone.

### 3.2 Position / velocity dead-reckoning (no matrix arithmetic)

[NORMATIVE] When a tech is observed (`Fresh`), the trace anchors snapshot `tank.boundsCentreWorldNoCheck`, `tank.rbody.velocity`, and `tank.rootBlockTrans.forward` *unmodified* — the observations are **deterministic noiseless reads** of engine state; no measurement-noise term applies. Decision **D2** (see [AETHER-DESIGN.md](AETHER-DESIGN.md)) made the lack of input smoothing explicit; raw rbody velocity becomes `LastSeenVelocity` without an EWMA pre-filter. A physics-frame velocity spike is bounded by the kinematic accelerator (§3.3) and visible to downstream consumers, matching the `organic-vs-bug design value` (memory).

[NORMATIVE] When the tech is NOT observed (`Coasting` or `Stale`), `PositionMean` / `VelocityMean` are computed by closed-form damped extrapolation rather than by matrix propagation:

```
dt              = (nowMono - LastObservedTickMono) * MonoClock.TickFreq    // seconds since last observation
velocityDecay   = exp(-dt / VelocityDecayTimeConstant)                     // exponential damping toward zero
VelocityMean    = LastSeenVelocity * velocityDecay
PositionMean    = LastSeenPosition + LastSeenVelocity * VelocityDecayTimeConstant * (1 - velocityDecay)
                                                                            // analytic integral of (LastVel · exp(-t/τ))
```

[NORMATIVE] `VelocityDecayTimeConstant` is provisional (~10 s — the v0.1 design's velocity decay constant, kept). The decay is mild: a tech we haven't seen for 10 s is increasingly likely to have stopped, but a tech moving fast in a known direction is still moving in that direction for the first second or two of coasting.

### 3.3 Uncertainty growth — single scalar, closed-form

[NORMATIVE] `UncertaintyMeters` replaces the 49-float covariance entirely. It is computed at publish time from the kinematic bound:

```
UncertaintyMeters = 0.5 * MaxAccelerationEstimate * AgeSeconds²
```

i.e. the radius a tech could have traversed under maximum acceleration from rest in `AgeSeconds`. This is monotone in age and proportional to `MaxAccelerationEstimate` — fast techs' uncertainty grows faster, the same physical intuition the prior process-noise design carried.

[NORMATIVE] No clamping is applied at the production site. Once `AgeSeconds > LostAfterSec` the trace transitions to `SightState.Lost` (§4); `UncertaintyMeters` continues to be reported but consumers SHOULD gate on `Sight == Lost` rather than on a radius threshold (no v0.2 consumer compares `UncertaintyMeters > N`).

### 3.4 Observation events outside the perception pass

[NORMATIVE] Some events convey position evidence outside the periodic observation sweep:
- **`DamageObserved`** at one of our techs — published by `SmartEventBridge` per-tank `DamageEvent` (P3.3 / R2 §2.R2.J). Routed to `LearningService.OnDamageObserved` + `DamageHintBuffer` (P4 Item 11 sidecar). **No v0.2 consumer feeds damage hints back into the trace** — `BeliefState` is observation-driven only. The DamageHintBuffer surface is producer-only at ship; v0.3 facing-threat orbit may consume it.
- **`BlockDestroyed`** on a hostile tech — confirms the tech still exists. Producer-only at v0.2; the periodic sweep will pick up the fresh kinematics on the next tick.
- **`ProjectileFired`** — telegraphs the firing tech's pose. No v0.2 consumer; this hook is reserved for v0.3 lead-aim work.

[NORMATIVE] Aether deliberately does **not** mix sparse observation evidence into the trace. Every trace value comes from `Observer.Submit` (the main-thread periodic sweep) or `RegisterTech` (the initial snapshot). The Sparse-Kalman-update path the v0.1 contract called for never existed in the v0.1 code and is not in v0.2.

---

## SECTION 4: STALENESS AND COASTING (REPLACES DECAY MODEL)

### 4.1 SightState machine — discrete, age-driven

[NORMATIVE] Each `BeliefState` carries a single `SightState` byte representing how recently the tech was observed. Transitions are driven by `AgeSeconds` at publish time:

| State | Condition | Meaning |
|---|---|---|
| `Fresh` | `AgeSeconds < CoastingAfterSec` (default ≈ 0.1 s) | observed this perception tick — anchors and pre-baked fields are the just-drained observation |
| `Coasting` | `CoastingAfterSec ≤ AgeSeconds < StaleAfterSec` (default ≈ 1 s) | not observed this tick but still moving on the prior anchor; coast extrapolation is trustworthy |
| `Stale` | `StaleAfterSec ≤ AgeSeconds < LostAfterSec` (default ≈ 4 s) | extrapolation is degrading; `ConfidenceAt` ramps linearly from 1 → 0 over this window |
| `Lost` | `AgeSeconds ≥ LostAfterSec` (default ≈ 8 s) | trace is frozen; `PositionAt → LastSeenPosition`, `VelocityAt → Vector3.zero`, `ConfidenceAt → 0`; coast accessors do NOT extrapolate further |

[NORMATIVE] State thresholds (`CoastingAfterSec` / `StaleAfterSec` / `LostAfterSec`) are tunables defined in `World/AetherTuning.cs` (or equivalent), read once per fuser construction.

[NORMATIVE] Consumers branch on `SightState` rather than computing age themselves. The compatibility shim `InSight => Sight == SightState.Fresh` preserves the v0.1 boolean for legacy callsites.

### 4.2 ThreatField Lost-gate — D1 decision pending

[NORMATIVE] `ThreatField.Build` (`Pathing/ThreatField.cs`) currently splats every non-friendly belief regardless of sight state — the pre-Aether behavior. The Aether design **D1** decision proposes skipping threat sources at `SightState.Lost`, so long-lost enemies stop dragging threat splats across the field.

[NORMATIVE] D1 is **deferred to a follow-up tuning pass** under a tunable so the user can A/B in-game (per the `organic-vs-bug design value` memory — "ghost enemy still warns me" may be preferred behavior). Until that lands, ThreatField behavior matches v0.1.

### 4.3 Terrain biasing — INFORMATIVE, NOT IN AETHER

[INFORMATIVE] The v0.1 contract called for directional shaping of the covariance using a terrain map (constraining uncertainty along blocked directions). With Aether's single-scalar `UncertaintyMeters` (no covariance to shape), this feature has no natural home in the trace. The semantic — "a tech last seen heading into a canyon can only spread along the canyon" — would now belong to **path-aware uncertainty** in `PathingService` (sample reachable cells within `UncertaintyMeters` of `PositionMean`), not in `World`.

[INFORMATIVE] No v0.2 work item delivers this; flagged here so a v0.3 design pass knows the responsibility moved from World to Pathing.

---

## SECTION 5: EVENT BUS

### 5.1 Behavioral contract

[NORMATIVE] `WorldEventBus` is a Smart-internal pub/sub primitive. Subscribers register typed handlers; publishers fire events. Dispatch is **synchronous on the publisher's thread** (events fire from main-thread shell hooks and dispatch to main-thread subscribers on the same thread).

```
public static class WorldEventBus
{
    public static void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
    public static void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
    public static void Publish<TEvent>(in TEvent ev) where TEvent : struct;
}
```

[NORMATIVE] Events are value types (`struct`) to avoid GC pressure under high event rates (damage events can fire hundreds of times per second during heavy combat). Subscribers receive events by `in` reference; they MUST NOT modify the event.

[NORMATIVE] Subscriber exceptions are caught at the dispatch boundary, logged via `DebugTAC_AI.LogError` naming the event type and the subscriber's method, and dispatch continues to other subscribers. Per [ARCHITECTURE.md §4 E4](ARCHITECTURE.md#section-4-error-handling), one bad subscriber does not break the others.

### 5.2 Event catalog (initial set)

[NORMATIVE] World publishes the following Smart-internal events:

| Event | Triggered by | Payload |
|---|---|---|
| `TechSpawned` | Shell `ManTechs.TankPostSpawnEvent` | TechId, TeamId, position |
| `TechDespawned` | Shell `ManTechs.OnStoppedTrackingVisible` or tech recycle | TechId |
| `TechTeamChanged` | Shell `ManTechs.TankTeamChangedEvent` | TechId, oldTeam, newTeam |
| `TechSeen` | Perception worker: tech entered observation range or LOS | TechId, KinematicState |
| `TechLost` | Perception worker: tech left observation range or LOS | TechId, LastKnownPosition |
| `DamageObserved` | Shell `Tank.DamageEvent` | TechId, DamageInfo (sanitized), attackerHint |
| `BlockAttached` | Shell `blockAttachedEvent` | TechId, BlockType, position |
| `BlockDetached` | Shell `blockDetachingEvent` | TechId, BlockType, position |
| `ProjectileFired` | Smart's Harmony patch on `ModuleWeapon.Fire` (per [SHELL-API-GUIDE.md OQ-8 resolution](SHELL-API-GUIDE.md#section-12-open-questions-consolidated)) | TechId, WeaponId, FireOrigin, FireDirection |
| `PlayerJoined` | Smart's Harmony patch on `NetPlayer.OnStartClient` | PlayerId |
| `PlayerLeft` | Smart's Harmony patch on `NetPlayer.OnDestroy` (or polling fallback) | PlayerId |
| `WorldStarted` | Shell `ManGameMode.ModeStartEvent` | — |
| `WorldModeSwitched` | Shell `ManGameMode.ModeSwitchEvent` | newMode |
| `WorldSaving` | Smart's `ManSafeSaves.RegisterSaveSystem` `OnSave(Doing=true)` callback | — |
| `WorldSaved` | Smart's `ManSafeSaves.RegisterSaveSystem` `OnSave(Doing=false)` callback | — |
| `WorldLoading` | Smart's `ManSafeSaves.RegisterSaveSystem` `OnLoad(Doing=true)` callback | — |
| `WorldLoaded` | Smart's `ManSafeSaves.RegisterSaveSystem` `OnLoad(Doing=false)` callback | — |
| `BeliefUpdated` | Perception worker after Kalman update | TechId, BeliefState |

[NORMATIVE] This catalog is extensible — new event types can be added without breaking existing subscribers (each `Subscribe<TEvent>` is type-keyed independently). Adding an event type that consumers may want is a v-bump on this contract; adding a payload field to an existing event is a breaking change.

### 5.3 Sanitized payloads

[NORMATIVE] Event payloads NEVER contain references to engine objects (`Tank`, `TankBlock`, `Visible`, `Rigidbody`, etc.). They contain Smart-internal identifiers (`TechId`, `BlockType`, `PlayerId`, `WeaponId`) and primitive values (positions, directions, damage amounts).

[RATIONALE] If event payloads held engine references, subscribers in workers (which would receive deferred events) could not safely read them — they'd be touching engine objects from worker threads, violating R1. Sanitization is enforced at the bus boundary.

[NORMATIVE] The `Tank` → `TechId` lookup is maintained by World; the mapping is one-to-one and stable across the tech's lifetime in the world.

### 5.4 Worker subscriptions

[NORMATIVE] Most subscriptions are main-thread (subscribers run on the publishing thread). For subsystems whose work is in a worker (e.g., Learning's online trainer collecting damage events for minibatches), the worker registers a *main-thread relay* that captures the event payload (which is value-typed) into a Threading queue the worker drains. The relay is a one-liner; the contract requires that workers never subscribe directly.

```
// In subsystem startup (main thread):
WorldEventBus.Subscribe<DamageObserved>(ev => damageEventQueue.Enqueue(ev));

// In subsystem worker:
while (damageEventQueue.TryDequeue(out var ev))
    AccumulateForTraining(ev);
```

---

## SECTION 6: PERCEPTION WORKER

### 6.1 Behavioral contract

[NORMATIVE] The `PerceptionWorker` runs in the Threading worker pool. Each tick (target ~30 Hz):

1. Read every tracked tech's current `KinematicState` from Vehicle (the kinematic tracker runs on main thread and publishes its own buffer; the worker reads the published snapshot).
2. For each tech currently in sight: apply Kalman update with the kinematic observation as measurement.
3. For each tech not in sight: apply propagation + terrain biasing + velocity decay.
4. For each tech, if intent classifier is available (Learning subsystem), apply intent update.
5. Publish per-tech `BeliefState` to each per-tech double-buffer.
6. Construct fused `BeliefSnapshot` and publish to the world-level double-buffer.
7. Fire `BeliefUpdated` event for each tech with significant change.

[NORMATIVE] The full pass MUST complete within the compute budget allocated by Diagnostics (per [ARCHITECTURE.md §2.3](ARCHITECTURE.md#23-compute-budget-gating)). If the budget is exceeded mid-pass, the worker cancels via the cancellation token, leaving previously-completed tech belief states updated; the rest are stale until the next pass.

### 6.2 Cadence and host-authority gate

[NORMATIVE] Perception runs ONLY when `host == true`. On clients (`host == false`), perception is suspended; belief state is whatever was published last on the host (replicated through TerraTech's standard net layer for any state visible via control output).

[NORMATIVE] Cadence of ~30 Hz is the target; the worker self-paces to fit the compute budget. A starved frame increments the diagnostics counter (per [THREADING-CONTRACT.md §10](THREADING-CONTRACT.md#section-10-diagnostics-integration)) but does not block subsequent ticks.

### 6.3 Initial state

[NORMATIVE] When a tech first appears (`TechSpawned`), the perception worker constructs an initial `BeliefState`:
- Position mean from observation; covariance high (5 m² diagonal).
- Velocity mean zero; covariance high (10 m²/s² diagonal).
- Heading mean from observation; covariance medium (0.1 rad² diagonal).
- Intent distribution: prior (per §2.2).

The initial state publishes immediately so consumers always see a valid belief.

---

## SECTION 7: SHELL EVENT SUBSCRIPTION DISCIPLINE

[NORMATIVE] World owns Smart's shell-event subscriptions. The discipline:

- `InitGlobal`: subscribe to global shell events (`ManGameMode`, `ManTechs`, `ManVisible`, etc.). Register Smart's `ManSafeSaves` save system. Apply Smart's Harmony patches for projectile-fired and player-join/leave (per [SHELL-API-GUIDE.md OQ-8 and OQ-9 resolutions](SHELL-API-GUIDE.md#section-12-open-questions-consolidated)).
- `OnTechSpawn`: subscribe per-tech shell events (`Tank.DamageEvent`, `blockAttachedEvent`, `blockDetachingEvent`).
- `OnTechRecycle`: unsubscribe per-tech shell events; fire `TechDespawned`.
- `DeInitGlobal`: unsubscribe all global shell events; unhook `ManSafeSaves`; unpatch Smart's Harmony patches; tear down the event bus.
- `OnWorldReset`: clear all belief state; clear all event-bus pending state (none, since dispatch is synchronous); preserve subscriptions.

[NORMATIVE] Per [SHELL-API-GUIDE.md §3.4](SHELL-API-GUIDE.md#section-3-registration-and-activation) and the OQ-3 resolution, Smart's first `InitGlobal` may happen after the first `ManGameMode.ModeStartEvent` fires. World tolerates joining mid-mission: the next `TechSpawned`/`TankPostSpawnEvent` is treated as the entry point, and belief state is built from observation onward.

---

## SECTION 8: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/World/` contains six files:

| File | Owns |
|---|---|
| `WorldModel.cs` | Aggregate per-tech state container, lifecycle, `BeliefSnapshot` publication. |
| `BeliefState.cs` | `BeliefState`, `BeliefSnapshot`, immutability discipline, intent category constants. |
| `KalmanUpdate.cs` | Process model, measurement model, Kalman gain, Joseph-form update, sparse-observation handlers. |
| `BeliefDecay.cs` | Out-of-sight propagation, terrain biasing, velocity decay, lost-threshold logic. |
| `EventBus.cs` | `WorldEventBus`, event-type registry, dispatch loop, exception isolation. |
| `PerceptionWorker.cs` | Worker loop, per-tick orchestration, compute-budget cooperation. |

Six files — within [FORM-SPECIFICATION.md §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives)'s ~3–7 range. The math (Kalman, decay) is split out from the data (BeliefState) because the math is non-trivial and worth isolating for testability.

---

## SECTION 9: DIAGNOSTICS INTEGRATION

[NORMATIVE] World exposes the following diagnostic events:

- `WorldPerceptionTickCompleted(int trackedTechCount, TimeSpan duration)` — per perception tick.
- `BeliefStateLost(TechId id)` — when a tech crosses the "fully unknown" threshold.
- `BeliefStateReacquired(TechId id, float covarianceCollapse)` — when a re-acquisition collapses high uncertainty.
- `EventBusDispatched(Type eventType, int subscriberCount, TimeSpan dispatchDuration)` — per event published.
- `EventBusSubscriberException(Type eventType, string subscriberName, Exception ex)` — fires the subscriber error.

[NORMATIVE] These flow into Diagnostics when that subsystem is authored.

---

## SECTION 10: OPEN ITEMS

[OPEN] **Intent category set.** Six categories at v0.1.0 (Aggressing, Retreating, Flanking, Repositioning, Holding, Idle). Whether to expand (e.g., Baiting, Patrolling) or shrink is a Learning subsystem decision; the classifier's output shape pins the catalog.

[OPEN] **Measurement noise values (§3.3).** Provisional numbers; tuned during self-play.

[OPEN] **Decay velocity damping time constant (~10 sec, §4.3).** Provisional; tune during self-play.

[OPEN] **Lost-threshold variance (~200 m², §4.1).** Provisional; tune.

[OPEN] **Terrain biasing implementation.** No-op until Pathing exists (§4.2). Activated when [PATHING-CONTRACT.md](PATHING-CONTRACT.md) is authored.

[OPEN] **Event bus thread-safety details.** Synchronous main-thread dispatch is the v0.1.0 model. If a future worker subsystem needs to *publish* (not just receive via relay), this needs revisiting; the answer is likely a Threading-channel-based publish queue that the bus drains on main thread.

---

## SECTION 11: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not Smart's decision-making. World *describes* the world; it does not *decide* anything. Planning, Control, and Pathing read World's belief snapshots and decide.

This contract is not the intent classifier. World *holds* the intent distribution; Learning *computes* it from observed behavior. World defines the format and the slot; Learning fills it.

This contract is not the shell event surface. World *consumes* shell events through the bus seam; the shell event signatures themselves are documented in [SHELL-API-GUIDE.md §7](SHELL-API-GUIDE.md#section-7-game-events).

This contract is not a generic worldview API. `WorldModel` is shaped by Smart's planner needs; subsystems with different needs (e.g., a Diagnostics historical-trace view) read the same snapshots but build their own derived views.

---

END OF WORLD-CONTRACT.md v0.1.0
