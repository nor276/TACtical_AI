# Aether — The Witness World Model (REV 3.1)

> **Pitch:** Replace the matrix Kalman with a per-tech immutable trace that snapshots noiseless observations directly and dead-reckons via closed-form damped extrapolation, where the only uncertainty channel is **age-since-last-observation**.

---

## Revision History

| Rev | Status | Notes |
|---|---|---|
| 1 | NEEDS-REVISION (5/5 adversarial reviewers) | Foundational consumer audit wrong; phantom-fix claims; impossible C# 7.3 type-alias plan; under-counted migration scope ~20-30×; behavior changes mis-framed as preservations. See `AETHER-DESIGN-REVIEW.md`. |
| 2 | SHIP-WITH-MINOR-FIXES 3/5, NEEDS-REVISION 2/5 | All 15 REV 1 convergent flaws FULLY-FIXED. 7 new convergent paragraph-level contradictions: intent field appears in 3 sections with conflicting status; `WithTeam` missing from spec but called by `WorldModel.UpdateTeam`; GC table double-counts; `TeamBelief.cs` LOC=0 wrong; `PositionObservation` extraction step missing; `PositionAt` signature gaps; `FromInitial` factory missing. |
| 3 | SHIP-WITH-MINOR-FIXES 3/3 | Spec contradictions resolved: intent fully deleted from v0.1 (sidecar in v0.2); `WithTeam` + `NewlyObserved` factories added to class spec; GC table collapsed; `TeamBelief.cs` budgeted at ~15 LOC; PositionObservation extraction inserted as migration step; `MonoClock.TickFreq` exposed so `PositionAt`/`VelocityAt`/`ConfidenceAt` have single-arg signature; `RegisterTech` signature change from `float heading` → `Vector3 forward` flagged honestly. Diagnosis wording sharpened (R≈0.5 ≠ R≈0). |
| **3.1** | **Awaiting migration** | Pure doc lint: title bumped to REV 3.1; both `RegisterTech` callers named (`SmartForm.cs:280` + `SmartEventBridge.cs:286`); both `PositionObservation.Standard()` callsites named (`SmartForm.cs:285` primary + `SmartForm.cs:644` orphan-sweep); migration step 1.5 picks variant (a) — extract to own file; `PositionObservation.cs` file-table row marked NEW; `BeliefStateFactory` file location in step 1 made explicit; ctor visibility documented; renamed `FromInitial` → `NewlyObserved` to preserve the existing factory name. |

---

## Decisions Requiring User Approval

These are **deliberate behavior changes** vs today, not preservations. Each needs an explicit accept/reject before migration code lands.

| # | Change | Today's behavior | Aether default | Reason to accept | Reason to reject |
|---|---|---|---|---|---|
| **D1** | **`ThreatField` Lost-gate** | `ThreatField.Build` (`Pathing/ThreatField.cs:181-211`) has **no Sight or Lost gating**; every non-friendly belief splats forever. | Skip threat sources at `SightState.Lost` (default 8 s out-of-sight). | Long-lost enemies stop dragging threat splats across the field; threat field becomes responsive. | Player likes the "ghost enemy still warns me" behavior — organic-feel preference per memory. |
| **D2** | **Velocity input smoothing** | Kalman gain<1 against `R=0.5` partially smooths each observation against the prior. Smoothing strength is data-dependent (decays as covariance shrinks). | **OFF by default** — raw `rbody.velocity` becomes `LastSeenVelocity` with no smoothing. | Matches user memory `organic-vs-bug design value` — don't auto-smooth. Single-frame collision spikes become visible in the trace but are bounded (`MaxAccel·dt` upper bound). | If physics spikes become a problem in spawn-test, an explicit smoothing knob can be added without redesign. |
| **D3** | **`BeliefUpdated` event cadence** | Producer is `PerceptionWorker.cs:80-86` — fires on >1 m position-mean shift (~30×N Hz upper bound). **Subscribers: zero today** (verified: `Subscribe<BeliefUpdated>` returns no hits). | Same trigger semantics, same cadence. No edge-triggering on Sight transitions (the v0.1 doc claim was wrong — there are no subscribers to optimize for). | Identical to today; no risk. | None — no current consumer. |
| **D4** | **`MaxAccelEstimate` refresh** | Set once at `World.RegisterTech` from `VehicleBuffer.Read().Thrust` calc for friendlies. Hardcoded **5f** for enemies in `SmartEventBridge.RegisterExternalTech` (line 289). Never refreshed. (See `AUDIT.md:100-101` for the pre-existing staleness issue.) | Same — set once at register, no refresh. The 5f enemy fallback is a **pre-existing issue** flagged for a separate fix; out-of-scope for Aether. | Smallest change footprint. No new sidecar registry. | If you want a `MaxAccelRegistry` sidecar refreshed by `VehicleModel` rebuild events, that's a separate (compatible) follow-up — flag it now to add to v0.2. |

**Recommended:** accept D2/D3/D4 (low-risk, matches today's spirit); defer D1 to a follow-up tuning pass under a tunable so you can A/B in-game. If D1 is rejected outright, `ThreatField` keeps splatting for Lost techs — Aether still ships, just doesn't add the gate.

---

## Background

The current Smart belief subsystem (`TAC_AI/AI/Forms/Smart/World/`) carries a 7-dimensional Gaussian per tracked tech: 7-float mean, 49-float covariance, 6-float intent vector, propagated by a diagonal-S Kalman filter (`KalmanUpdate.cs`) against synthetic measurement noise (`PositionObservation.Standard` hardcodes 0.5 / 1.0 / 0.05). Four numerical guards (`HasTooLarge` reset, `sDiag` floor 1e-3, positive-diagonal clamp 1e-6, `Symmetrize`) defend against K-amplification pathologies that arise from variance arithmetic against an effectively-zero-noise source.

**The diagnosis (unanimous across 10 independent designs):** observations are deterministic main-thread reads of `tank.boundsCentreWorldNoCheck` / `tank.rbody.velocity` / `tank.rootBlockTrans.forward`. The synthetic R the current Kalman fuses against (`PositionObservation.Standard`: 0.5 / 1.0 / 0.05) is small but nonzero, so today's Kalman gain *does* produce real smoothing — gain<1 against R=0.5 partially absorbs each observation against the prior, with smoothing strength data-dependent on the current covariance trace. **The problem isn't that R=0** — it's that R isn't physically sourced from anything. The 0.5/1.0/0.05 are arbitrary tuning constants standing in for noise that doesn't exist; the variance accounting downstream produces uncertainty values no consumer reads as a covariance matrix. As the covariance shrinks across many observations, the gain converges toward I anyway. So we get cost-of-Kalman without information-of-Kalman, and the smoothing the gain<1 happens to provide is a side-effect that decision D2 makes explicit rather than incidental.

**`BeliefState.PositionVariance` consumer audit (verified against live code):**

| Site | Reads | Pattern | Status |
|---|---|---|---|
| `BeliefDecay.IsLost` (`BeliefDecay.cs:62-67`) | `v.x + v.y + v.z > 600f` (threshold = 3 × 200) | trace > scalar | **Has zero production callers.** Only the test suite calls it. Documented in `EventBus.cs:61` as "the active mechanism" but no production code invokes it. |
| `SmartRuntime.SummarizeBelief` (`SmartRuntime.cs:385`) | `pv.x + pv.z` (XZ-trace) | trace > scalar | Diagnostic / log summary. |
| `SmartTestSuite.cs:94,95,105,107` | `.x` only | per-component | Tests only — rewritten in v0.1. |
| `KalmanUpdate.cs:179-181` | `S[i*N+i] += z.PositionVariance` | observation noise, different identifier | This is `PositionObservation.PositionVariance` (the synthetic R), not `BeliefState.PositionVariance`. Different field on a different type. Goes away with the Kalman path. |

So `BeliefState.PositionVariance` has **one production consumer** (`SmartRuntime.SummarizeBelief`, `pv.x + pv.z`), **one dead-code consumer** (`BeliefDecay.IsLost`), and **four test reads** (`.x` only). The 49-float covariance carries no information any live consumer reads.

**`BeliefState.CovComponent` consumer audit (verified against live code):** zero non-internal readers. The only access is `KalmanUpdate` itself and `BeliefState` internals. Confirms full deletion.

**Costs the redesign eliminates:**
- ~3 KB/tick of `float[49]` covariance allocations × 30 Hz × N techs ≈ 90 KB/s per tech (most of the current Gen0 pressure on this subsystem).
- The K-amplification bug class and all four numerical guards that defend against it.
- Angular-wrap NaN sinks (`WrapAngleToPi` with finite-guard).
- The PSD-violation and 1e23 mean-explosion failure modes.

**What stays (unchanged):** observation cadence, lifecycle hooks, `WorldEventBus`, `DoubleBuffer<T>`, host-authority gating, the per-tick fresh-Dictionary publish pattern (already what `PerceptionWorker.cs:49` does today), and the `BeliefSnapshot.ByTech` consumer contract.

---

## Core Design

### Per-tech state shape

The C# **type name stays `BeliefState`** to avoid a 25-30 file rename ripple. The CLASS BEHIND THE NAME is replaced with a new shape (described below). Internally the design is documented as "TechTrace" for clarity, but the public type the rest of the codebase references is still `BeliefState`.

```csharp
// World/BeliefState.cs — full rewrite, same name
public sealed class BeliefState                       // sealed class, immutable
{
    public readonly TechId       Id;                  // 4 B
    public readonly TeamId       Team;                // 4 B

    // Anchors (last-observed values, frozen until next observation)
    public readonly Vector3      LastSeenPosition;    // 12 B
    public readonly Vector3      LastSeenVelocity;    // 12 B
    public readonly Vector3      LastSeenForward;     // 12 B (unit 3-vector; pitch/roll preserved for aircraft)
    public readonly long         LastObservedTickMono;// 8 B  (Stopwatch.GetTimestamp source)
    public readonly long         PublishedTickMono;   // 8 B  (perception tick that built this trace)
    public readonly float        SpeedAtObserve;      // 4 B  (|LastSeenVelocity|, cached)
    public readonly float        MaxAccelerationEstimate; // 4 B (preserved from current API)

    // Sight/staleness
    public readonly SightState   Sight;               // byte: Fresh|Coasting|Stale|Lost

    // Pre-baked at publish time — readers are field loads, not method calls
    public readonly Vector3      PositionMean;        // 12 B (coast-extrapolated to PublishedTickMono)
    public readonly Vector3      VelocityMean;        // 12 B (decayed to PublishedTickMono)
    public readonly Vector3      ForwardXZ;           // 12 B (LastSeenForward XZ-normalized; ThreatField reads this directly)
    public readonly float        HeadingMean;         // 4 B  (Atan2(ForwardXZ.x, ForwardXZ.z) pre-baked; no per-read trig)
    public readonly float        AgeSeconds;          // 4 B  (cached at publish)
    public readonly float        UncertaintyMeters;   // 4 B  (closed-form kinematic radius)

    // Compat surface — same property names AS TODAY (zero consumer source change)
    public bool   InSight          => Sight == SightState.Fresh;
    public long   LastObservedTick => LastObservedTickMono;

    // PositionVariance: invariant `3 · UncertaintyMeters²` shim, satisfies BOTH
    // arithmetic patterns (pv.x+pv.z = 2·U²; pv.x+pv.y+pv.z = 3·U²). Since the
    // only production reader is SmartRuntime.SummarizeBelief (pv.x+pv.z), this
    // is comfortably over-spec'd for compatibility.
    public Vector3 PositionVariance => new Vector3(UncertaintyMeters * UncertaintyMeters,
                                                   UncertaintyMeters * UncertaintyMeters,
                                                   UncertaintyMeters * UncertaintyMeters);

    // Coast-aware accessors for opt-in consumers. All three resolve dt internally
    // via the static MonoClock.TickFreq (= 1.0 / Stopwatch.Frequency) so callers
    // don't have to thread tickFreq through every callsite.
    //
    // Lost-state clamp: when Sight == Lost, all three return frozen values —
    // PositionAt → LastSeenPosition, VelocityAt → Vector3.zero, ConfidenceAt → 0.
    // They do NOT extrapolate beyond LostAfterSec; consumers that need post-Lost
    // motion must check Sight first.
    public Vector3 PositionAt(long nowMono);
    public Vector3 VelocityAt(long nowMono);
    public float   ConfidenceAt(long nowMono);          // 1.0 if Fresh; linear 1→0 between Stale and Lost; 0 if Lost
}

// BeliefState's constructor is `internal` — all construction goes through BeliefStateFactory.
// This is enforced by accessibility, not just convention; external assemblies cannot bypass.

// Factories — these are the only construction paths. Co-located in BeliefState.cs (not a separate file).
public static class BeliefStateFactory
{
    // First observation, called by Observer.Submit on register or freshly-observed-after-gap.
    // Name preserves the existing BeliefState.NewlyObserved static factory (today at BeliefState.cs:145,
    // called from WorldModel.RegisterTech:59 and 5 SmartTestSuite sites) so callers don't rename.
    public static BeliefState NewlyObserved(TechId id, TeamId team,
        Vector3 pos, Vector3 vel, Vector3 fwd, float maxAccel, long tickMono);

    // Coast forward from a prior trace — called by AetherFuser for techs not observed this tick.
    // Used inside DeadReckon.Coast.
    public static BeliefState FromCoast(BeliefState prev,
        Vector3 newPosition, Vector3 newVelocity, float ageSeconds,
        float uncertainty, SightState sight, long nowMono);

    // Team-change preserving all other state — called by WorldModel.UpdateTeam
    // (wired from SmartEventBridge.OnTankTeamChanged, Phase-3.3 captured-tech allegiance fix).
    // Replaces the old instance WithTeam method; constructs a fresh sealed-class instance.
    // (No array-sharing concern because the new shape has no array fields.)
    public static BeliefState WithTeam(BeliefState prior, TeamId newTeam);
}
```

**Why sealed class, not struct:**
- `DoubleBuffer<T>` stores reference via `object _current` + `Interlocked.Exchange` (`Threading/DoubleBuffer.cs:27-44`) — class is the right shape.
- Eliminates the 60-80 B struct-tearing hazard.
- One allocation per tech per perception tick (~120 B Gen0). Comparable to today's per-tick `float[49]` covariance alloc.

**Why pre-bake `PositionMean` / `VelocityMean` / `ForwardXZ` / `HeadingMean` / `AgeSeconds` at publish time, not lazy on read:**
- Hot-path readers (`CostFunction.WeaponAlignmentCost`, MPC sample rollouts) read `PositionMean` thousands of times per frame. Method-call-and-Mathf.Exp per read would be strictly worse than today's field load.
- Pre-baking preserves **snapshot immutability**: a consumer holding a `BeliefSnapshot` for multiple ticks sees stable values within that snapshot — matches today's contract.
- Consumers that want predicted state at a future horizon (MPC step h+1, weapon lead time `+projTime`) call the explicit `PositionAt(nowMono)` / `VelocityAt(nowMono)` opt-in API. These extrapolate from `LastSeenPosition`/`LastSeenVelocity` (NOT from `PositionMean`), so the result is consistent regardless of when the trace was published.
- `ForwardXZ` is pre-baked specifically because `ThreatField.cs:202-203` reads `HeadingMean` and reconstructs the forward vector via `sin`/`cos` — exposing `ForwardXZ` directly lets the consumer skip the trig roundtrip in a tight loop.

### In-sight model

When `tank` is enumerated by `ManTechs.IterateTechs` during `SmartForm.ObserveWorldTechsIfDue`:

1. Read `tank.boundsCentreWorldNoCheck`, `tank.rbody.velocity`, `tank.rootBlockTrans.forward` on main thread.
2. Reject if any component is non-finite (preserves existing `WorldModel.RecordObservation` NaN/Inf guard).
3. Call `Observer.Submit(id, pos, vel, fwd)` — single-writer per-tech atomic slot, lock-free.

Per **decision D2 (default OFF)**, no smoothing is applied — raw values become `LastSeenPosition` / `LastSeenVelocity`.

The Aether worker drains intake slots, and for each tech with a fresh observation constructs a new `BeliefState` where `LastSeen* = raw observation`, `PositionMean = LastSeenPosition`, `VelocityMean = LastSeenVelocity`, `AgeSeconds = 0`, `Sight = Fresh`, `UncertaintyMeters = BaseObservationUncertainty` (0.5 m floor — never zero, so consumer thresholds behave). **The observation IS the state.**

### Out-of-sight model

For techs not refreshed this tick, the worker computes a fresh `BeliefState` via closed-form damped-velocity integration:

```csharp
// World/DeadReckon.cs
public static class DeadReckon
{
    public const float VelocityTimeConstantSec = 10f;   // τ in the exp(-dt/τ) decay; matches today's BeliefDecay.DampVelocity
    public const float MaxCoastSec             = 12f;   // saturation horizon (no coast computed beyond this)
    public const float StaleAfterSec           = 1.0f;
    public const float LostAfterSec            = 8.0f;
    public const float BaseObservationUncertainty = 0.5f;

    public static BeliefState Coast(BeliefState prev, long nowMono, double tickFreq)
    {
        float dt = (float)((nowMono - prev.LastObservedTickMono) / tickFreq);
        if (dt <= 0f || float.IsNaN(dt)) return prev;             // clock-skew / NaN guard

        float decay     = Mathf.Exp(-dt / VelocityTimeConstantSec);
        Vector3 vCoast  = prev.LastSeenVelocity * decay;
        Vector3 pDrift  = prev.LastSeenVelocity * VelocityTimeConstantSec * (1f - decay);

        // Hard kinematic clamp: bounded extrapolation distance
        float kinematicCap = Mathf.Min(prev.SpeedAtObserve * VelocityTimeConstantSec, 250f);
        if (pDrift.sqrMagnitude > kinematicCap * kinematicCap)
            pDrift = pDrift.normalized * kinematicCap;

        Vector3 position = prev.LastSeenPosition + pDrift;
        float uncertainty = BaseObservationUncertainty
                          + prev.SpeedAtObserve * dt
                          + 0.5f * prev.MaxAccelerationEstimate * dt * dt;

        SightState sight =
            dt >= LostAfterSec  ? SightState.Lost    :
            dt >= StaleAfterSec ? SightState.Stale   :
                                  SightState.Coasting;

        return BeliefState.FromCoast(prev, position, vCoast, dt, uncertainty, sight, nowMono);
    }
}
```

**Heading is NOT extrapolated.** `LastSeenForward` and the pre-baked `ForwardXZ`/`HeadingMean` stay at the last-observed value. Angular velocity is not in the observation channel; integrating it would be fiction. This matches today's behaviour (Kalman heading process noise was tied to vel and never actually updated heading mean) but is now explicit.

**Damage hints** (from `SmartEventBridge.OnTankDamage` at `SmartEventBridge.cs:298-326`): today already publishes `Vector3.zero` as the damage position because the engine `ManDamage.DamageInfo` struct has no hit-direction field. So there is **no live "anchor elevation" bug to fix**. The previous Aether v1 doc was wrong to claim otherwise. A future `DamageHintBuffer` would be a NEW sidecar channel for if/when damage hints carry direction data; **defer to v0.2**. Aether v0.1 ships without it.

### Uncertainty channel

A **single 4-byte float**: `UncertaintyMeters = BaseObservationUncertainty + Speed·dt + 0.5·MaxAccel·dt²` — the kinematic upper bound on plausible displacement.

Plus a categorical companion:

```csharp
public enum SightState : byte { Fresh, Coasting, Stale, Lost }
```

**Why a scalar replaces 49 floats:** the consumer audit (see Background) confirmed `BeliefState.PositionVariance` is read by one production site (`SmartRuntime.SummarizeBelief`, `pv.x + pv.z`) and one dead site (`BeliefDecay.IsLost`, which has no production callers). `CovComponent` has zero non-internal readers. No consumer reads off-diagonals, anisotropic uncertainty, velocity variance, or heading variance. The matrix carried no information.

**Calibration shim (resolves convergent flaw C2):** the compat property returns `Vector3(U², U², U²)` — invariant `3 · U²` shim. This satisfies **both** possible consumer arithmetic patterns:
- `SmartRuntime.SummarizeBelief` reads `pv.x + pv.z = 2·U²`. Monotone in `U`, fine for diagnostic logging.
- A future `pv.x + pv.y + pv.z` reader would see `3·U²`. Also monotone.

Since `BeliefDecay.IsLost` has no production callers, the categorical `SightState.Lost` flag is the load-bearing gate. The `SightState` transitions are wired into the only behavior change that consumes them: `ThreatField` (per **decision D1** — needs user approval).

---

## Threading Model

**Single writer per channel, many readers, lock-free.** No new primitives — uses `DoubleBuffer<T>` (`Threading/DoubleBuffer.cs`) verbatim.

### Writers
- **Main thread only** for kinematic observations: `SmartForm.ObserveWorldTechsIfDue` calls `Observer.Submit`.
- **Main thread only** for lifecycle (`RegisterTech` / `DeregisterTech` / `UpdateTeam`) — preserved from today.
- **No phantom marshalling claims.** Verification:
  - `SmartEventBridge.OnTankDamage` (`cs:298-326`) and `OnTankTeamChanged` (`cs:332-368`) fire on the main thread today; `Publish` (synchronous) is correct. **No change needed.**
  - `TeamBelief.InjectObservations` (`Coordination/TeamBelief.cs:53`) **has zero callers in the repo** — no production race exists. **No change needed.** (If a future producer is added, route through `Observer.Submit` from the main thread or through `PublishFromWorker` from a worker. Aether v0.1 doesn't pre-fix a phantom.)
  - `PerceptionWorker.cs:86` already uses `PublishFromWorker` for `BeliefUpdated` (Phase 3.2 fix). Preserved verbatim in `AetherFuser`.

### Worker (AetherFuser, renamed from PerceptionWorker)
- Single worker thread, same `WorkerPool` registration, same `RunLoop` shape, same host-authority gate (`SmartRuntime.IsHost`), same catch-and-continue exception isolation.
- Each tick (~30 Hz): drain intake slots, build new `BeliefState` instances (observed: from raw values; coasting: via `DeadReckon.Coast`), publish per-tech and fused snapshots.

### Publication

**Single primitive choice: keep `DoubleBuffer<T>`. Per-tick fresh `Dictionary` allocation is honest accounting of today's existing behaviour.**

- **Per-tech buffer:** `DoubleBuffer<BeliefState>` per `PerTechEntry` — `Interlocked.Exchange` of the reference. Wait-free reader via `Volatile.Read`. **Same primitive and behaviour as today.**
- **Fused snapshot:** `DoubleBuffer<BeliefSnapshot>` — the worker constructs a **fresh `Dictionary<TechId, BeliefState>` each tick** (matches `PerceptionWorker.cs:49` today) and wraps it in a new `BeliefSnapshot` before `Write`. Readers iterate the immutable dictionary held by their captured snapshot reference; GC reclaims the old dict once all readers release it.

This **does not** use 3-buffer rotation. The previous v1 design proposed 3-buffer rotation but claimed `DoubleBuffer<T>` "preserved verbatim" — those two claims are contradictory (`DoubleBuffer<T>` is single-slot atomic-swap; rotation needs a new primitive with reader epochs). v2 resolves by choosing the simpler primitive at the cost of one Dictionary allocation per publish tick — same as today's `PerceptionWorker`, so **no GC regression vs today.**

If under live load the per-tick Dictionary allocation becomes a profiler hotspot, a `TripleBuffer<T>` with explicit reader-epoch refcount can be added as a separate primitive (~80 LOC) without changing the consumer surface. That's a v0.2 follow-up, not a v0.1 requirement.

### Memory ordering
- All cross-thread reference publication uses `Interlocked.Exchange` (full fence on .NET 4.6.1, per `DoubleBuffer.cs:22-25`).
- `long LastObservedTickMono` and `PublishedTickMono` writes are inside the `BeliefState` constructor and never mutated after publish — no torn-read risk on 32-bit Mono. (`FromCoast` returns a new instance via the same constructor; same guarantee.)

### Time source
- **Single shared `MonoClock`** static type. API:
  - `MonoClock.Now()` → `long` (calls `Stopwatch.GetTimestamp()`; lock-free; thread-safe by virtue of Stopwatch's own thread-safety; no shared mutable field).
  - `MonoClock.TickFreq` → `double` (= `1.0 / Stopwatch.Frequency`; static readonly, computed once at static-init). All dt math uses this.
  - `AetherFuser` calls `MonoClock.Now()` once per tick into a local `_tickNowMono` to stamp every `BeliefState` it constructs that tick (consistent in-tick timestamps). Other consumers call `MonoClock.Now()` whenever they need current time — no contention.
- Time deltas computed as `(now - then) * MonoClock.TickFreq` (or equivalently `(now - then) / (double)Stopwatch.Frequency`) — portable across Mono runtimes.
- `Environment.TickCount` is **already used** at `SmartForm.cs:602` for observation throttling. Aether doesn't touch it — preserved verbatim.

---

## GC Profile (honest accounting)

**Steady state per 30 Hz observation tick, N tracked techs.** Each tech is either observed-this-tick or coasting-this-tick, not both — they're mutually exclusive per row, so we count once per tech.

| Channel | Allocation per tick | At N=30 |
|---|---|---|
| `BeliefState` construction (1 per tech: observed OR coasting) | 1 per tech, ~136 B (sealed-class with CLR header + 110 B payload) | ~4.1 KB |
| Fused `Dictionary<TechId, BeliefState>` (pre-sized to ~256) | 1 per publish | ~2-3 KB |
| `BeliefSnapshot` wrapper | 1 per publish, ~32 B | ~32 B |
| `Observer` intake (pre-allocated SPSC slots, see `ObservationIntake.cs`) | 0 | 0 |

**Total: ~6-7 KB/tick × 30 Hz ≈ 180-210 KB/s during full combat with 30 techs.**

**Comparison to today:** today's `PerceptionWorker.cs:49` allocates the same fresh `Dictionary<TechId, BeliefState>` per tick (~2-3 KB), AND a `BeliefState` per tech (mean `float[7]` + cov `float[49]` + intent `float[6]` arrays inside it ≈ 250 B per tech amortized). At N=30 that's ~3 KB dict + ~7.5 KB BeliefState payload ≈ 10-12 KB/tick. So **Aether is ~40-50% lower allocation rate** than today, AND:
- Every allocated byte is **load-bearing** (data a consumer reads).
- The deleted bytes were **variance accounting for measurement noise that doesn't exist**.
- The K-amplification bug class is gone (and its four numerical guards).

The headline is both "fewer bytes" and "every byte information consumers use."

**Per-spawn cost:** one `PerTechEntry` (~120 B) lifetime allocation. No `IntentRegistry` allocations until v0.2.

**Per-tech-recycle:** slot released, per-tech buffer's reference cleared.

---

## Consumer Impact (honest per-file edit budget)

All paths under `f:\Tac-AI\Modified\TACtical_AI-master\TACtical_AI-master\TAC_AI\`.

The C# type name `BeliefState` is **preserved**. The class behind the name is replaced. No `using` aliases, no global rename. C# 7.3's file-scoped `using = ` aliases would not satisfy generic type constraints (`DoubleBuffer<BeliefState>`) across 25+ files; v2 sidesteps the problem entirely.

| File | LOC delta | Change |
|---|---:|---|
| `AI\Forms\Smart\World\BeliefState.cs` | **±100** | Class body fully rewritten to the shape above. Constructor signature changes (8-arg ctor goes away, new `BeliefState(...)` and `BeliefState.FromCoast(...)` factories). Same type name, same namespace. |
| `AI\Forms\Smart\World\KalmanUpdate.cs` | **–290** | **DELETE.** Replaced by `Observer.cs` (write path) + `DeadReckon.cs` (coast math). |
| `AI\Forms\Smart\World\BeliefDecay.cs` | **–85** | **DELETE.** `DampVelocity` folds into `DeadReckon.Coast`; `IsLost` has no production callers; `ApplyTerrainBiasing` was an unimplemented stub. |
| `AI\Forms\Smart\World\PerceptionWorker.cs` | rename **±60** | Renamed to `AetherFuser.cs`. Body shrinks ~70%: drain intake, coast non-observed, build dictionary, publish. Same `RunLoop` skeleton, host gate, exception-catch pattern preserved. |
| `AI\Forms\Smart\World\Observer.cs` | **+90 (new)** | Write path: `Submit(TechId, Vector3 pos, Vector3 vel, Vector3 fwd)`. NaN/Inf guard at entry. Single-writer-per-slot intake. |
| `AI\Forms\Smart\World\ObservationIntake.cs` | **+60 (new)** | Per-tech SPSC intake slots, pre-allocated, lock-free. |
| `AI\Forms\Smart\World\DeadReckon.cs` | **+50 (new)** | The coast math above. Pure static. |
| `AI\Forms\Smart\World\SightState.cs` | **+10 (new)** | The enum + tiny extension methods. |
| `AI\Forms\Smart\World\MonoClock.cs` | **+30 (new)** | Wraps `Stopwatch.GetTimestamp` + dt conversion. |
| `AI\Forms\Smart\World\WorldModel.cs` | **±25** | `PerTechEntry` swaps `DoubleBuffer<BeliefState>` content to the new BeliefState shape (same type name; just the class definition changed). Public surface mostly preserved with **one signature change**: `RegisterTech` takes `Vector3 forward` instead of `float heading` (today's `float heading` came from `tank.rootBlockTrans.eulerAngles.y`; the new shape stores 3D forward to preserve aircraft pitch/roll per the class spec). **Two callers** updated: `SmartForm.cs:280-286` and `SmartEventBridge.cs:286-290` (`RegisterExternalTech`). `RecordObservation` becomes a thin pass-through to `Observer.Submit`. `UpdateTeam` (today calls `prior.WithTeam(newTeam)` per Phase-3.3 captured-tech allegiance fix wired from `SmartEventBridge.cs:354`) keeps that contract — `WithTeam` is preserved as a factory on the new BeliefState (see class spec). |
| `AI\Forms\Smart\World\PositionObservation.cs` | **NEW** (extracted in step 1.5) → **±-15** (trimmed in step 5) | Extracted from `KalmanUpdate.cs:9-30` to its own file in migration step 1.5 (build-green checkpoint). Then in step 5, constructor signature trimmed: drop `PositionVariance` / `VelocityVariance` / `HeadingVariance` fields (synthetic noise — no longer meaningful); `Standard()` factory removed. |
| `AI\Forms\Smart\SmartForm.cs` | **±12** | `ObserveWorldTechsIfDue` calls `Observer.Submit(id, pos, vel, fwd)` directly at **both** construction sites: the primary observation loop (~line 285) AND the orphan-sweep lazy-register path at line 644. Drops `PositionObservation.Standard(...)` from both. Throttle (env.TickCount-based at line 602), orphan sweep, live-id set preserved verbatim. Also updates the `World.RegisterTech` callsite to pass `Vector3 forward` instead of `float heading` (per WorldModel row). |
| `AI\Forms\Smart\SmartRuntime.cs` | **0** | `SummarizeBelief` (line 385) unchanged; `pv.x + pv.z` now sums `2 · U²`. Monotone in age; calibration parity is by design (D3 / shim). |
| `AI\Forms\Smart\Control\ContinuousController.cs` | **0** | Field reads (`PositionMean`, `VelocityMean`, `HeadingMean`) unchanged. Null-check pattern preserved (`BeliefState` is still a class). |
| `AI\Forms\Smart\Control\WeaponFireController.cs` | **+6 to +12** (depends on D1/D2) | Currently reads `target.PositionMean` + `target.VelocityMean` for lead calc (`cs:103-109`). Aether v0.1 keeps that — the pre-baked values already match today's behaviour for fresh observations. Switching to `target.VelocityAt(MonoClock.Now())` for stale targets is a follow-up worth doing — would add a `MonoClock.Now()` call + 2-3 lines in the lead calc + the import. This is **not in the v0.1 scope** to keep the migration small; flag as v0.2. |
| `AI\Forms\Smart\Control\CostFunction.cs` | **0** | No source change. MPC rollout reads `PositionMean` / `VelocityMean` unchanged. |
| `AI\Forms\Smart\Pathing\ThreatField.cs` | **0 OR +1** (per D1) | If D1 accepted: add 1-line `if (belief.Sight == SightState.Lost) continue;` to `ThreatField.Build`. If D1 rejected: no change. The current code (`ThreatField.cs:181-211`) has **no Sight/Lost gating** today. Also add `ForwardXZ` read at line 202-203 in place of `HeadingMean → sin/cos` reconstruction (1-2 line micro-opt). |
| `AI\Forms\Smart\Pathing\PathingService.cs` | **0** | `FusedBuffer.Read()` returns same shape. |
| `AI\Forms\Smart\Coordination\TargetAssignment.cs` | **0** | `MaxAccelerationEstimate` preserved as a field. |
| `AI\Forms\Smart\Coordination\TeamBelief.cs` | **±15** | `InjectObservations` (lines 53-72) has zero callers, but compiles against `PositionObservation.Standard()` (deleted) and the old `World.RecordObservation(PositionObservation)` signature. v0.1 stubs the method body to `throw new NotImplementedException("Aether v0.2: route ally-LOS observations through Observer.Submit")` and drops the deleted-symbol references so the file compiles. A future PR wires it to `Observer.Submit` when a real producer is added. |
| `AI\Forms\Smart\Coordination\PlanDecomposition.cs` | **0** | No change. |
| `AI\Forms\Smart\Identity\*GoalSource.cs` (6 files) | **0** | `PositionMean` / `VelocityMean` / `HeadingMean` preserved. |
| `AI\Forms\Smart\Learning\LearningService.cs` | **0** | **Does not currently subscribe to BeliefUpdated** (verified: zero `Subscribe<BeliefUpdated>` calls). v0.1 doesn't add a subscription. Intent migration off `BeliefState` deferred to v0.2 (see below). |
| `AI\Forms\Smart\Integration\SmartEventBridge.cs` | **+2** | `OnTankDamage` / `OnTankTeamChanged` fire on main thread today; synchronous `Publish` is correct — no marshalling added. **One change**: `RegisterExternalTech` at line 286 also calls `World.RegisterTech` with `heading: 0f` — update to pass `Vector3 forward = Vector3.forward` (or `tank.rootBlockTrans.forward` if available at that callsite) to match the new WorldModel signature. |
| `AI\Forms\Smart\Tests\SmartTestSuite.cs` | **±200** | Delete Kalman tests (`UpdateWithObservation` variance-shrink, `Propagate` variance-grow, `DampVelocity` damping, `BeliefDecay.IsLost`). Add new tests: observation-fidelity (record-then-read returns identical position), bounded-coast (extrapolation ≤ `v₀·τ`), monotone-confidence, lost-flag-at-`LostAfterSec`, re-observe-snaps-to-fresh, NaN-rejection, ForwardXZ-matches-Atan2. The 8-arg `BeliefState` ctor in test setup (`SmartTestSuite.cs:146`) gets rewritten to the new factory. |
| `AI\Forms\Smart\World\EventBus.cs` | **0 (v0.1)** | `BeliefUpdated` event kept with same producer trigger. `TechLost` stays `[Obsolete]` — no consumer asked for it in v0.1; reactivate in v0.2 if needed. |
| `AI\Forms\Smart\Docs\WORLD-CONTRACT.md` | **±200** | Sections §2 (BeliefState shape), §3 (Kalman), §4 (decay) rewritten. Add §5 documenting `SightState` transitions and the per-tick fresh-Dict publish pattern. |

**Honest total: ~25-30 files touched, roughly 400-600 LOC net (most of it deletes + new files; consumer source changes are ~10 LOC across 2-3 files).** The v1 doc's "~5 lines" was wrong by ~50×.

---

## What Stays (verified)

- `TechId` / `TeamId` structs and stable-identity policy.
- `WorldEventBus`: copy-on-write subscriber arrays, `PublishFromWorker` → `DrainMainThreadQueue` relay. `BeliefUpdated`, `DamageObserved`, `TechSpawned`, `TechDespawned`, **`TechTeamChanged`** (the Smart-internal struct; the engine event is `ManTechs.TankTeamChangedEvent`), `WorldSaving/Saved/Loading/Loaded` events preserved.
- `DoubleBuffer<T>` primitive — used unchanged for per-tech and fused snapshot publishing.
- `WorkerPool`, `WorkerLifecycleRegistry`, `CancellationHelpers`, host-authority gate.
- Lifecycle hooks: `OnTechSpawn`, `OnTechRecycle`, `OnWorldReset`, `OnTankDestroyed`, the orphan sweep in `SmartForm.ObserveWorldTechsIfDue` (env.TickCount-based throttle at line 602, `CompareExchange` race guard, reused scratch buffers).
- `KinematicTracker` / `KinematicState` / `VehicleModelSnapshot` pipeline (separate subsystem).
- `BeliefSnapshot` type name and `IReadOnlyDictionary<TechId, BeliefState>.ByTech` consumer contract.
- `ManTechs.IterateTechs` observation source.
- Save/load sinks, multi-thread safety contracts.
- `Environment.TickCount` throttle at `SmartForm.cs:602` (used today; preserved).
- `PerceptionWorker.cs:86`'s `PublishFromWorker` pattern (preserved in the renamed `AetherFuser`).
(Intent field is deleted from `BeliefState` in v0.1 — see "What Gets Deleted" and "Out of Scope for v0.1" — because nothing live populates it past initial Glorot-zeros today. A real per-tech intent producer wires through a sidecar `IntentRegistry` in v0.2.)

---

## What Gets Deleted (verified)

| Class / Type | LOC | Why |
|---|---:|---|
| `KalmanUpdate` (entire file) | 290 | Matrix-Kalman against deterministic observations is mathematically degenerate (K→I at R=0). Four numerical guards become unreachable. |
| `BeliefDecay` (entire file) | 85 | `DampVelocity` and `ApplyTerrainBiasing` are called only from `PerceptionWorker.cs:71-72`, which is itself being rewritten in this PR (renamed `AetherFuser`, new loop body using `DeadReckon.Coast`). `IsLost` has zero production callers (only `SmartTestSuite.cs:147` references it; tests rewritten). `EventBus.cs:61`'s `[Obsolete]` comment referencing `BeliefDecay.IsLost` as "the active mechanism" also gets cleaned in this PR. |
| `BeliefState`'s old `_mean`/`_cov`/`_intent` `float[]` fields + their `MeanArrayInternal`/`CovArrayInternal`/`IntentArrayInternal` accessors + `MeanComponent(i)` / `CovComponent(i,j)` per-component accessors + the 8-arg constructor + `WithTeam` instance method | ~250 (in-file) | Class body replaced (name kept). Replaced by inline value fields (see class spec) and the three `BeliefStateFactory` factories. Old `WithTeam` array-aliasing path goes away — `BeliefStateFactory.WithTeam(prior, newTeam)` constructs a fresh instance copying all fields with the new TeamId; no array sharing. |
| `PositionObservation.PositionVariance` / `VelocityVariance` / `HeadingVariance` fields + `Standard()` factory | ~30 | Synthetic noise; never sourced from physics. Constructor trimmed. |
| `PerTechEntry.LatestObservation` + `HasFreshObservation` flag pair | ~10 | Replaced by per-tech `Observer` intake slots. |

---

## Failure Modes Considered

| Failure mode | Mitigation |
|---|---|
| **Reader-vs-writer race on fused dictionary** | Each publish creates a **fresh `Dictionary<TechId, BeliefState>`**; `DoubleBuffer<T>.Write` swaps the wrapping `BeliefSnapshot` reference atomically. Readers iterate the dictionary held by their captured snapshot — never the writer's in-progress one. Same pattern as today. |
| **`BeliefState` torn read across threads** | Sealed class with all-readonly fields, published via single `Interlocked.Exchange`. Field writes happen inside the constructor on one thread; readers `Volatile.Read` the reference and see fully-constructed object. |
| **NaN/Inf from physics-broken `rbody.velocity`** | Filtered at `Observer.Submit` (preserved from current `WorldModel.RecordObservation` guard). Rejected observations leave prior trace ageing naturally. No covariance state to poison. |
| **Coast extrapolation drift over long occlusion** | Hard kinematic cap at `min(v₀·τ, 250 m)`. Velocity exp-decay saturates position drift. After `LostAfterSec=8s`, `SightState.Lost` is set. |
| **Clock skew across threads** | Each `BeliefState` carries its own `LastObservedTickMono` / `PublishedTickMono`; consumers compute dt against their own `MonoClock.Now()` read. No shared mutable `_tickNow` field across threads. Negative `dt` clamped to 0 in `DeadReckon.Coast` and `BeliefState.PositionAt`/`VelocityAt`. |
| **Float-precision drift over long sessions** | `Stopwatch.GetTimestamp()` is monotonic and not rebased (it counts from process start; the underlying tick counter never wraps within realistic session lengths — wraps at ~292 years for a 1 GHz tick rate). On `OnWorldReset`, per-tech `LastObservedTickMono` values are cleared with the per-tech state, so post-reload techs start from fresh observation timestamps. Deltas always use `long` ticks; `float` derivations are always on bounded `dt < MaxCoastSec` (12 s). |
| **`PositionVariance` unit shift breaking thresholds** | Compat property returns `Vector3(U², U², U²)` — invariant `3·U²`. `SmartRuntime.SummarizeBelief` reads `pv.x + pv.z = 2·U²` (monotone in U). No production threshold breaks. |
| **Re-acquisition snap breaks mid-rollout MPC** | MPC rollouts capture the `BeliefSnapshot` reference at request-build time; the snapshot is immutable. Snap visible to the NEXT MPC request, not mid-rollout. |
| **Slot reuse leaking state to new tech** | On `DeregisterTech`: slot's `BeliefState` reference cleared. New tech at recycled slot starts from a freshly-constructed `BeliefState`. |
| **`AetherFuser` exception inside worker** | Catch-and-log-and-continue pattern preserved from `PerceptionWorker.RunLoop:135-147`. Single exception does not corrupt slot table (all writes are reference swaps). |
| **OnWorldReset mid-tick** | Worker idles via host-gate while reset runs on main thread; cleared dictionaries published as `BeliefSnapshot.Empty`. Workers holding prior snapshots see orphaned but valid data and exit their tick normally. |

---

## Migration Plan

1. **Land new types side-by-side.** Add under `TAC_AI/AI/Forms/Smart/World/`: `SightState.cs` (enum + extension methods), `MonoClock.cs` (Stopwatch wrapper + `TickFreq` static readonly), `ObservationIntake.cs` (per-tech SPSC slot ring). **Hold** `Observer.cs`, `DeadReckon.cs`, and `BeliefStateFactory` until step 4 — these reference the new `BeliefState` shape and can't compile in isolation. `BeliefStateFactory`'s three static factories (`NewlyObserved` / `FromCoast` / `WithTeam`) live in the same file as the rewritten `BeliefState.cs` in step 4 — no separate `BeliefStateFactory.cs` file. Register every new .cs in the old-style `.csproj` per build-setup memory. Build green; no consumer changes yet.

1.5. **Extract `PositionObservation` from `KalmanUpdate.cs`.** The struct is currently nested inside `KalmanUpdate.cs:9-30`. **Variant (a), chosen:** move it to its own file `World/PositionObservation.cs` (unchanged shape) so deleting `KalmanUpdate.cs` in step 4 doesn't take the struct with it. The trim of `PositionVariance`/`VelocityVariance`/`HeadingVariance` fields and `Standard()` factory happens in step 5. Build green.

2. **User approval of decisions D1-D4.** Read the "Decisions Requiring User Approval" section. Recorded responses determine the next steps' content. Default if not answered: D2/D3/D4 accepted, D1 deferred (no Lost gate added).

3. **Shadow-mode dual-publish (optional).** If desired before swap-over: inside `WorldModel`, add a parallel `_traceByPath` reading via `Observer.Submit`. Both paths run; an A/B comparator logs divergence between today's Kalman `PositionMean` and Aether's `PositionMean`. Expected: identical in-sight; divergent only during coast (Aether saturates at `v₀·τ`, Kalman drifts unbounded). Skip this step if you trust the spawn-test gate.

4. **Rewrite `BeliefState` class body + delete `KalmanUpdate` + delete `BeliefDecay`.** Rename `PerceptionWorker` → `AetherFuser` and replace its `Tick` body with the new drain/coast/publish loop. Inside `WorldModel.RecordObservation`: thin pass-through to `Observer.Submit`. `WorldModel.UpdateTeam`: call the new `BeliefStateFactory.WithTeam(prior, newTeam)` instead of `prior.WithTeam(newTeam)` (semantically identical). Stub `TeamBelief.InjectObservations` to `throw new NotImplementedException(...)` so it compiles. Build. The `SmartTestSuite` Kalman/BeliefDecay tests fail to compile (expected — they reference deleted symbols).

4.5. **Rewrite `SmartTestSuite` Kalman/BeliefDecay tests** per the file table. Build green. (Without this step, the build stays broken between step 4 and step 6.)

5. **Trim `PositionObservation` + `SmartForm.ObserveWorldTechsIfDue` callsite.** Drop the `PositionVariance` / `VelocityVariance` / `HeadingVariance` args at construction. Build green.

6. **Conditional: apply D1.** If accepted, add the 1-line `Sight == SightState.Lost` continue at `ThreatField.cs:181-211`. Add the `ForwardXZ` read at line 202-203 to skip the trig roundtrip.

7. **Run `SmartTestSuite`.** All new tests green. Add diagnostic assertions: NaN observation rejection, monotone uncertainty, bounded coast.

8. **Update `WORLD-CONTRACT.md`** sections §2/§3/§4. Mark `AUDIT.md` and `FIX-PLAN.md` notes referencing "Kalman gain", "sDiag floor", "positive-diagonal clamp", "HasTooLarge reset" as `SUPERSEDED-BY-AETHER` — they describe no code that exists.

9. **Spawn-test in Steam build per build-setup memory.** Run combat scenarios at three scales: 1+4 open ground, 12 mid-game, 30+ Ragnarok wave. Verify: no NREs, no NaN-spike log lines, GC profile stable in Unity Profiler, behavioural parity in pursuit / threat / MPC (no decisive-feel regression per user memory). Multi-agent fan-out verification per memory pattern.

10. **Push via `_push3` mirror-clone** after spawn-test passes.

---

## Out of Scope for v0.1 (deferred to v0.2)

- **Intent migration to `IntentRegistry` sidecar.** The 6-float intent field (`IntentArrayInternal` / `IntentProb` accessor) is **deleted from `BeliefState` in v0.1** because nothing live populates it today past initial Glorot-zeros. When a real per-tech intent producer is wired, it writes to a sidecar `IntentRegistry` keyed by `TechId` in v0.2; consumers gain a `BeliefSnapshot.IntentFor(techId)` accessor.
- **`DamageHintBuffer` channel.** Today's `OnTankDamage` already publishes `Vector3.zero` (engine struct has no hit direction); there's no anchor-elevation bug to fix. If/when damage hints carry direction, add the buffer in v0.2.
- **`MaxAccelRegistry` sidecar.** Per D4, set-once-at-register is today's behaviour and stays. The 5f-fallback for enemies (per `AUDIT.md:100-101`) is a pre-existing issue addressed separately.
- **`WeaponFireController` switch to `VelocityAt(now)`.** v0.1 keeps the pre-baked `VelocityMean` read; v0.2 plumbs `MonoClock.Now()` through `ContinuousController` and switches the lead calc to the coast-aware accessor.
- **`TripleBuffer<T>` primitive.** Only justified if profiler shows per-tick Dictionary allocation is a real hotspot under live load.
- **`TechLost` event reactivation.** Stays `[Obsolete]` until a consumer asks for it.

---

## Open Questions

1. **Velocity time constant τ:** kept at 10 s to match today's `BeliefDecay.DampVelocity`. Should this become a tunable in the existing Smart Modified 49-tunable framework?
2. **Coast for aircraft:** `LastSeenForward` is a full 3D unit vector (pitch/roll preserved); `HeadingMean`/`ForwardXZ` are XZ-only. Aircraft goal sources that want full 3D heading read `LastSeenForward` directly. Should aircraft goal sources be updated as part of v0.1 or v0.2?
3. **`BeliefSnapshot.ByTech` enumerator boxing in tight loops:** `Dictionary<TechId, BeliefState>` enumeration boxes its struct-enumerator if cast to `IEnumerable`. Audit consumer `foreach` patterns for accidental boxing as part of step 6.
4. **Re-acquisition snap easing:** v0.1 ships hard snap (matches organic-feel preference). If MPC sample-coherence becomes an issue under live testing, expose a one-tick easing flag.
