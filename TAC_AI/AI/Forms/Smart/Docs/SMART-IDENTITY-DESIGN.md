# SmartIdentity — synthesized design (for review)

> Synthesized from 10 identical-prompt design agents. This document captures the convergent shape, calls out where outliers were reconciled against the actual source, and lists the open questions that need your call before code lands. **No implementation has been started.**

---

## 1. Goal + scope

Every Smart-driven tech is given a permanent **identity** at spawn — a purpose-classification orthogonal to `VehicleClass` (movement) and `Coordination.Role` (per-engagement tactic). Identity drives:

1. **What the tech's "target" is when the Coordinator isn't publishing one** — fills the flat-utility gap that currently leaves techs static.
2. **What success looks like** — identity tags every training event so each LearningService model accumulates signal that fits the tech's purpose (Hunter kill vs Gatherer delivery vs Base damage-absorbed).

Out of scope for this layer: Coordinator's PUCT / Hungarian / PlanLibrary internals; the MPC cost function; weapon-fire arc / range; the four LearningService models' internal architecture.

---

## 2. Locked-in decisions (you confirmed these)

| # | Decision | Source |
|---|---|---|
| L1 | **Identity source**: authored hint (RawTech) first, composition fallback if missing/Unknown | AskUserQuestion answer |
| L2 | **Identity stability**: stamped once at spawn, never recomputed (not on block change, not on team change) | AskUserQuestion answer |
| L3 | **"On a team"** = same `tank.Team` int as another live tech in `ManTechs.IterateTechs()` (strict; no team-relations) | AskUserQuestion answer |
| L4 | **There is always a target** — patterns differ by identity, but every identity produces a non-degenerate `TacticalGoal` every tick | Your wording: "Effectively, there should always be a target" |
| L5 | **No pollution** — implementation must not block, conflict with, or rip-out for future identity additions, Coordinator v0.2, or PlanLibrary expansion | Your wording: "refrain from implementation that could block or pollute the next" |

---

## 3. The enum (convergent shape)

```csharp
namespace TAC_AI.AI.Forms.Smart.Identity
{
    public enum SmartIdentity : byte
    {
        Generic = 0,        // unauthored + no composition signal → falls through to TacticalOptimizer.Step (current behavior)
        Hunter,             // mobile, armed, no anchor → seek+kill, meander when no target
        Sniper,             // long-range armed + slow → standoff at max range, lateral micro-meander
        Base,               // anchored / stationary-authored → defend in place, never moves
        Gatherer,           // resource collector → resource→base loop; flees when provoked
        AircraftSupport,    // air-class + ally on same team → orbit team centroid, suppress threats to allies
        AircraftHunter,     // air-class + no allies → Hunter pattern in 3D
    }

    public readonly struct SmartIdentityStamp
    {
        public readonly SmartIdentity Identity;
        public readonly bool FromAuthored;       // true = authored hint hit; false = composition fallback
        public readonly long ClassifiedAtTick;   // diagnostic only
        public readonly Vector3 SpawnAnchor;     // = tank.boundsCentreWorldNoCheck at OnTechSpawn time
                                                 // Used by: Base (hold-position fallback), Gatherer (solo "hub" target per Q2),
                                                 // Sniper (return-to-perch fallback), Hunter (deterministic Lissajous seed).
    }
}
```

**Why this shape (consensus across proposals):**
- **Generic exists** so the classifier never lies. Unauthored / ambiguous techs keep current behavior (zero-regression guarantee). [10/10]
- **Sniper is a distinct enum value, not a sub-flag on Hunter** [7/10]. Reconciliation: the goal-source shape is structurally different (standoff at max range with lateral meander vs. close-to-75% with wander), so distinct dispatch is cleaner than a flag-tested branch in HunterGoalSource. The user's wording "hunters, snipers, or whatever the AI calls them, they should meander" is honored — both meander, but at different scales.
- **Aircraft split** is the team-conditional fork you explicitly named. Decided once at spawn per L2; if a lone aircraft's allies later die, it keeps its identity (the AircraftHunter goal source naturally degenerates to nearest-hostile, behaviorally identical).

---

## 4. Rejected alternative identities (with rationale)

Other proposals introduced these — I'm recommending **against** including them in v0.1:

| Proposed | Source proposals | Why rejected for v0.1 |
|---|---|---|
| **Brawler** | 4, 6 | Close-range Hunter is still Hunter. "Close range" is a goal-source detail (preferred range = small fraction of weapon range), not an identity. Adding it inflates the enum without changing dispatch shape. |
| **Defender** (mobile escort tethered to a base) | 2-renamed, 5, 10 | You said "Bases need to simply focus on defense" — anchored. A *mobile* escort is not what you named. Mobile-defender behavior can land as a Coordinator-published EscortPlan in v0.2 without inventing a new identity. |
| **Sailor / Naval** | 3 | `BaseTerrain.Sea` is a movement-class concern (`VehicleClass.Submarine` already handles it), not an identity. A naval Hunter is still a Hunter. |
| **Scout** | 5 | Already a tactical role in `RoleAssignment.Role.Scout`. Tactical roles are per-engagement; identities are permanent. Don't mix axes. |
| **Energizer** | 9 | Rare authored AIType; can fold into Gatherer (resource-loop logic applies to "deliver energy to ally" cleanly) or defer entirely. v0.2 candidate. |
| **Guard** (patrolling defender) | 7 | Same reasoning as Defender — handled by future EscortPlan, not identity. |

**Bottom line**: ship exactly your named six (Hunter, Sniper, Base, Gatherer, AircraftSupport, AircraftHunter) + Generic. Anything else is a v0.2 question.

---

## 5. Classifier (where authored signals come from + composition fallback)

**Hookpoint**: `SmartForm.OnTechSpawn` (currently around `SmartForm.cs:158-249`), inserted after `helper.FormState = state;` and before `teamRuntime.RegisterTech(state)`. **Not** `SmartEventBridge.OnTankSpawned` — that also fires for non-Smart-driven techs and we don't need to classify them. [9/10 consensus]

**Pre-classification preparation** (resolves the "VehicleBuffer is Empty at spawn" gap that 4 proposals flagged): **DO NOT** rely on `state.TickMainThread(0.033f)` — its rebuild branch is gated by `LastObservationTick % 30 == 0` ([SmartRuntime.cs:152-184](../../../SmartRuntime.cs#L152-L184)) and the first call increments to 1, skipping the rebuild. Instead, expose a new public method `SmartPerTechState.RebuildVehicleSnapshotNow(Tank tank)` that calls `BlockCapture.CaptureFromTank` → `MobilityProfile.Derive` → `WeaponProfileBuilder.Build` → `ArmorMap.Compute` directly and writes the result to `VehicleBuffer`, bypassing the modulo gate. Call this from `OnTechSpawn` before the classifier runs. **Note for §12 Phase 1 honesty**: this relocates the first heavy rebuild from tick-30 to tick-0, which means the first `state.VehicleBuffer.Read()` in `World.RegisterTech` (currently at [SmartForm.cs:224-228](../../../SmartForm.cs#L224-L228)) sees real `Thrust` instead of `Empty`. `maxAccelEstimate` passed to Kalman registration changes from the `5f` fallback to the actual computed value — a small but real semantic change. See §12 Phase 1 caveat.

### 5.1 Authored signals (precedence order)

The Modified form already stamps `BaseTerrain` onto `TankAIHelper.AuthoredTerrain` via `AIECore.StampAuthoredIntent` (called from three sites in `RawTechLoader.cs`). The other authored field — `RawTechTemplate.purposes : HashSet<BasePurpose>` — is **read at spawn time but never stamped onto the per-tech helper**. To use it from the classifier we need to mirror it onto the helper.

**One additive change to TankAIHelper.cs** (next to `AuthoredTerrain` at [TankAIHelper.cs:49](../../../../TankAIHelper.cs#L49)):

```csharp
// Stamped at the same call site as AuthoredTerrain. Modified form ignores it; Smart consumes it.
// May be null on player-built / unauthored techs — classifier MUST null-check.
internal HashSet<BasePurpose> AuthoredPurposes;
```

Extend `AIECore.StampAuthoredIntent(Tank, BaseTerrain, HashSet<BasePurpose>)` (method declared at [AIECore.cs:479](../../../../AIECore.cs#L479); field assignment at [`:484`](../../../../AIECore.cs#L484)) and update the three call sites in [`Templates/RawTechLoader.cs`](../../../../../Templates/RawTechLoader.cs):
- Line 1102 — local is `toSpawn : RawTech`; pass `toSpawn.purposes` (lowercase, internal field, may be null per the existing guard at [RawTechLoader.cs:2572](../../../../../Templates/RawTechLoader.cs#L2572))
- Line 2264 — local is `filter : RawTechPopParams`; pass `filter.Purposes` (uppercase property at [RawTechPopParams.cs:82](../../../../../Templates/RawTechPopParams.cs#L82))
- Line 2315 — local is `BT : RawTech`; pass `BT.purposes` (lowercase, same field as site 1)

**No RawTech file format change** — every existing template already declares `purposes`.

**`AuthoredHintInvalidated` is NOT used by the classifier at spawn.** The flag is reset to `false` by `StampAuthoredIntent` itself ([AIECore.cs:485](../../../../AIECore.cs#L485)) and only set later on player block edits ([TankAIHelper.cs:836,866](../../../../TankAIHelper.cs#L836)). At classify-time it's almost always false. Identity is stamped once and never recomputed per L2; a future invalidation hook would need to consult this flag, but v0.1 does not.

### 5.2 Classification table

**Evaluation order is significant** (resolves multi-match ambiguity from review). The classifier runs rows top-to-bottom; first match wins:

1. **Base** (defensive intent overrides everything — including mobility)
2. **Gatherer** (resource intent, only if Base didn't fire)
3. **AircraftSupport** (aircraft authored AND has allies)
4. **AircraftHunter** (aircraft authored, no allies)
5. **Sniper** (authored marker — composition path deferred per Q4)
6. **Hunter** (catch-all for armed mobile ground/sea/space)
7. **Generic** (nothing matched)

| # | Identity | Authored signal (wins if set) | Composition fallback |
|---|---|---|---|
| 1 | **Base** | `purposes ∩ {Headquarters, Defense, TechProduction}` non-empty — **regardless of `NotStationary`** (Defense intent wins over Gatherer per Q5) | `Mobility.TopSpeedForward < 2` m/s AND `Weapons.Count > 0`. Note: `tank.IsAnchored` is **not** reliable at OnTechSpawn — the `RequestAnchored` component processes the flip in a later engine callback ([RawTechLoader.cs:2257,2309](../../../../../Templates/RawTechLoader.cs#L2257)). Authored Defense/HQ purposes are the trustworthy signal for Base. |
| 2 | **Gatherer** | `purposes ∩ {Harvesting, HarvestingNoHQ, Autominer}` non-empty | `Weapons.Count ≤ 1` AND `Mobility.TopSpeedForward ≥ 2`. Best-effort — see Q1. **Do NOT** read `helper.DediAI` for this — it's NOT set by `EnemyMind.Setup`; only by GUIAIManager (player picks), NetworkHandler (MP sync), and TankAIHelper reset paths. Authored enemy harvesters carry no DediAI signal. |
| 3 | **AircraftSupport** | `AuthoredTerrain ∈ {Air, Chopper, Space}` AND `hasSameTeamAlly == true` | `VehicleCapability.FromMobility(...).Class == Airplane` OR (`Class == Hover` AND `VerticalAuthority > 1.0`), AND `hasSameTeamAlly == true` |
| 4 | **AircraftHunter** | `AuthoredTerrain ∈ {Air, Chopper, Space}` AND `hasSameTeamAlly == false` | Same composition as AircraftSupport but `hasSameTeamAlly == false` |
| 5 | **Sniper** | `purposes` contains `BasePurpose.Sniper` | **Deferred to v0.2** — composition rule requires `WeaponProfile.Range` to reflect real values; placeholder `100f` ([WeaponProfile.cs:85](../../../Vehicle/WeaponProfile.cs#L85)) makes any range-based rule unreachable. v0.1 ships authored-Sniper only. |
| 6 | **Hunter** | `AuthoredTerrain ∈ {Land, Sea}` AND no prior row matched | `Weapons.Count ≥ 1` AND `Mobility.TopSpeedForward ≥ 5` AND no prior row matched |
| 7 | **Generic** | nothing else matched | — |

**Key corrections from review:**
- `Autominer` is **Gatherer only** (per [EBasePurpose.cs:36](../../../../../Templates/EBasePurpose.cs#L36) comment: "Can mine unlimited BB (DO NOT ATTACH THIS TAG TO HQs!!!)"). Removed from Base authored set.
- `Defense` in `purposes` makes Base win over Gatherer **regardless** of `NotStationary` (resolves Q5 ambiguity for mobile defensive harvesters).
- `Space` is in both Aircraft rows (was inconsistently split).
- `Hover` class with high vertical authority counts as aircraft for composition (handles chopper-class hovercraft that don't reach `Airplane` threshold).

**`hasSameTeamAlly` computation**: one O(N) pass of `ManTechs.inst.IterateTechs()` (instance method — `ManTechs` is a singleton) filtered by `tank.Team == self.Team && tank != self`. The `tank != self` exclusion is **load-bearing**, not defensive — `OnTechSpawn` fires from `DelayedSubscribe` ([TankAIHelper.cs:805](../../../../TankAIHelper.cs#L805)) after the engine has registered the new tank, so the newly-spawned tank IS in `IterateTechs()` already. Done at classify time, never repeated per L2/L3.

**Caveat for hostile-team aircraft**: `tank.Team < 0` is engine-overloaded to mean "enemy/hostile grouping" — two enemy techs on `Team == -1` will read as "same team" and both classify as AircraftSupport. This matches engine semantics elsewhere (e.g., [TacticalOptimizer.cs:133](../../../Control/TacticalOptimizer.cs#L133)) but worth noting.

**Determinism**: pure function over (helper, snapshot, team-count). No randomness. Allocation: zero in the hot path (HashSet membership tests reuse the stamped collection; null-check `AuthoredPurposes` first).

---

## 6. Per-identity goal source

```csharp
namespace TAC_AI.AI.Forms.Smart.Identity
{
    public interface ISmartGoalSource
    {
        SmartIdentity Identity { get; }
        TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                             BeliefSnapshot beliefs, IdentityContext ctx);
    }

    public readonly struct IdentityContext
    {
        public readonly TechId SelfTechId;
        public readonly SmartIdentityStamp Stamp;
        public readonly Vector3 TeamCentroid;      // UNDEFINED when HasAllies == false; consumers MUST gate on HasAllies first.
                                                   // Not a Vector3? to keep the struct allocation-free; HasAllies is the truth source.
                                                   // World origin (0,0,0) is a valid map position — never overload Vector3.zero as a sentinel.
        public readonly bool HasAllies;            // truth source for TeamCentroid validity
        public readonly TechId? NearestOwnBaseId;  // null if no team or no Base-identity tech on team
        public readonly long TickCounter;          // for deterministic per-tech meander seeding
    }
}
```

Goal sources are **stateless singletons** in a `Dictionary<SmartIdentity, ISmartGoalSource>` keyed by identity. Per-tech mutable scratch (sticky-target latch, meander phase, recent-damage flag, last-delivery destination) lives on `SmartPerTechState.IdentityScratch`.

| Identity | Target produced | No-target / meander | Flee / disengage | What it supersedes |
|---|---|---|---|---|
| **Hunter** | Nearest hostile in `beliefs.ByTech` filtered by `Team != Self.Team`; goal position = enemy position offset toward self by `0.75 × meanWeaponRange`. Sticky for ~4s. | Deterministic Lissajous-style drift around (team centroid if `HasAllies`, else self position). Period seeded from `Stamp.SpawnAnchor + TickCounter` — every Hunter picks a unique phase. ~60m radius. | None — Hunter commits. (No HP-based flee for v0.1 — see Q3.) | The flat-utility no-motion case of `TacticalOptimizer.Step`. Adam still re-seeds on external→identity transition. |
| **Sniper** | Same nearest-hostile pick. Goal position = self position offset toward enemy by **a small distance** (sit-and-shoot at the current weapon range). Heading toward target. | Lateral micro-shuffle ±5m perpendicular to target bearing, refreshes every 8s. | None for v0.1. | Hunter's "close to 75%" geometry. |
| **Base** | Nearest hostile within `1.5 × maxWeaponRange` (for turret aim only). Goal position = `ownBelief.PositionMean` — **always**. Heading rotates to face hostile. | None — `TacticalGoal.AtCurrent(...)`. | Never. | All optimizer drift. Anchored techs don't move. |
| **Gatherer** | If `HasAllies` AND nearest-base-on-team known: alternate goal between nearest resource node and team base (carry-toggle proxy from helper.RecentExtractor or mass-delta heuristic). If solo: nearest resource node, fall back to `Stamp.SpawnAnchor` as the "hub". See Q1/Q5. | When approaching node or returning to base, no meander — pursuit IS the goal. When idle (no resource visible): spiral search from spawn anchor. | Any hostile belief inside 80m AND `Weapons.Count(hostile) > 0` → goal = team centroid (or spawn anchor) offset 60m away from threat bearing. Sticky for 3s. | EngagementUtility entirely — Gatherers ignore weapon-fitness goal terms. WeaponFireController still fires opportunistically (it's overlaid by ContinuousController). |
| **AircraftSupport** | Nearest hostile threatening any same-team ally (highest threat-to-ally score). If none, orbit team centroid at ~80m radius / altitude offset. | Lazy orbit around team centroid. | None for v0.1. | Hunter's friendly-agnostic nearest-enemy choice. |
| **AircraftHunter** | Same as Hunter, with Y-coordinate floor (min cruise altitude). | Wide 3D wander (200m radius) around belief-centroid. | None for v0.1. | Same as Hunter. |
| **Generic** | **No goal source.** Dispatcher bypasses `ISmartGoalSource` entirely and calls `TacticalOptimizer.Step` inline (see §7.3). | n/a — current behavior. | n/a. | Nothing — backward-compat path. |

**Key invariant**: every non-Generic goal source produces a definite `TacticalGoal` every tick. Base intentionally produces an at-current goal (stays still); every other identity always has somewhere to be. This kills the flat-utility no-motion bug.

---

## 7. Plumbing

### 7.1 Storage on `SmartPerTechState`

```csharp
public SmartIdentityStamp IdentityStamp { get; private set; }
public ISmartGoalSource GoalSource { get; private set; }
internal IdentityScratch Scratch { get; } = new IdentityScratch();

internal void StampIdentity(SmartIdentityStamp stamp, ISmartGoalSource src)
{
    IdentityStamp = stamp;
    GoalSource = src;
}
```

### 7.2 Classifier invocation in `SmartForm.OnTechSpawn`

**Ordering is critical** — `SmartRuntime.World?.RegisterTech(...)` currently runs at [SmartForm.cs:230-236](../../../SmartForm.cs#L230-L236), AFTER `helper.FormState = state` at line 190. The classifier needs the BeliefState registered so future Hunter "nearest hostile" queries can include all techs. Insert the classifier **AFTER** `World.RegisterTech` and **BEFORE** `teamRuntime?.RegisterTech(state)`:

```csharp
// (existing) line 230-236 — World.RegisterTech runs first so this tech is in the belief set

// NEW — force a real vehicle-snapshot rebuild (the modulo-30 gate in TickMainThread doesn't fire here).
state.RebuildVehicleSnapshotNow(helper.tank);

// NEW — classify against the now-populated VehicleBuffer.
var stamp = SmartIdentityClassifier.Classify(helper, state.VehicleBuffer.Read(), helper.tank.Team);
var goalSource = SmartIdentityRegistry.For(stamp.Identity);
state.StampIdentity(stamp, goalSource);
DebugTAC_AI.Log("Smart.Identity: '" + helper.tank.name + "' = " + stamp.Identity
    + (stamp.FromAuthored ? " (authored)" : " (composition)"));

// (existing) teamRuntime.RegisterTech(state) runs after — so the team-runtime sees the stamp.
```

`SmartPerTechState.RebuildVehicleSnapshotNow(Tank tank)` is a new public method that runs the rebuild body unconditionally (bypassing the `% 30 == 0` gate in `TickMainThread`). Implementation = the same `BlockCapture` → `MassDistribution` → `ThrustMap` → `WeaponProfileBuilder` → `ArmorMap` → `MobilityProfile` chain currently in [SmartRuntime.cs:152-184](../../../SmartRuntime.cs#L152-L184), extracted into a callable method that both `TickMainThread` and `OnTechSpawn` can invoke.

**Behavioral honesty (revised from prior Phase 1 claim)**: this relocates the first heavy rebuild from the first `tick % 30 == 0` boundary to spawn time. Downstream consumers (notably `World.RegisterTech` reading `state.VehicleBuffer.Read().Thrust` for `maxAccelEstimate` at [SmartForm.cs:224-228](../../../SmartForm.cs#L224-L228)) now see real thrust values from frame 0 instead of the `5f` `MobilityProfile.Default` fallback. Kalman variance growth bounds change accordingly during the first ~30 ticks of every Smart tech's life. **This is an improvement, not a regression** — but it is NOT "identical to today." §12 Phase 1 caveat updated below.

### 7.3 Wire-in at `ContinuousController.OnOperationsTick`

The existing goal-selection block at [ContinuousController.cs:153-168](../../../Control/ContinuousController.cs#L153-L168) becomes a 3-tier precedence: **Coordinator → Identity (when non-null) → TacticalOptimizer fallback**.

**No `GenericGoalSource` class** — review found it unreachable as designed. Generic identities skip the source-dispatch path entirely and use the existing `TacticalOptimizer.Step` inline. The dispatch becomes:

```csharp
TacticalGoal goal;
var external = _readExternalGoal?.Invoke();
if (external.HasValue)
{
    goal = external.Value;
    _lastTickHadExternalGoal = true;
}
else
{
    if (_lastTickHadExternalGoal)
    {
        _tactical.Reset(ownBelief.PositionMean, ownBelief.HeadingMean);
        _lastTickHadExternalGoal = false;
    }
    var src = _readGoalSource?.Invoke();    // new ctor param: Func<ISmartGoalSource>
    if (src != null && src.Identity != SmartIdentity.Generic)
        goal = src.Produce(ownBelief, vehicle, beliefs, _readIdentityCtx());
    else
        goal = _tactical.Step(ownBelief, vehicle, beliefs);   // Generic + fallback path
}
```

The `src.Identity != Generic` guard intentionally bypasses identity dispatch for Generic, so `TacticalOptimizer.Step` runs directly. The `SmartIdentityRegistry` only contains non-Generic identities — there is no `GenericGoalSource`.

**`_readGoalSource` / `_readIdentityCtx` plumbing**: two new `Func<>` parameters added to the `ContinuousController` constructor (currently has 7 params, none for identity). `SmartPerTechState` wires `_readGoalSource = () => this.GoalSource` and `_readIdentityCtx = () => this.BuildIdentityContext()`. `BuildIdentityContext()` reads pre-aggregated team data from the owning `TeamRuntime`.

**New `TeamRuntime` aggregation needed for Phase 1**: `TeamRuntime` does not currently expose a team centroid. Add a `bool TryGetCentroid(out Vector3 centroid)` method that returns true + centroid only when at least one other tech is registered. Returns `false` if the team is solo (caller treats `HasAllies = false`). Cheap — aggregates over the existing `_techs` dictionary. Called from `BuildIdentityContext()` on the main thread.

**Coordinator authority preserved exactly**: the `external.HasValue` branch is unchanged. Identity sources run only when Coordinator hasn't published — exactly where the flat-utility no-motion bug lives. PUCT/Hungarian internals are not touched, PlanLibrary expansion is not constrained.

### 7.4 Thread safety

- `OnOperationsTick` runs on the main thread (gated by `SmartForm.Operations`).
- `SmartIdentityStamp` is a readonly struct, immutable.
- Goal sources read only immutable snapshots (`BeliefSnapshot`, `VehicleModelSnapshot`, `BeliefState`) + the captured `IdentityContext`.
- `SmartPerTechState.Scratch` is only mutated from `OnOperationsTick` (which is single-threaded per tech).
- `ManTechs.IterateTechs()` calls (for `hasSameTeamAlly` at spawn, and Gatherer's team-base lookup) are main-thread only — confirmed both call sites are.

---

## 8. RawTech integration — what's read, what's added

| Field | Read today by Modified form? | Smart needs? | Action |
|---|---|---|---|
| `TankAIHelper.AuthoredTerrain : BaseTerrain` | Yes — stamped by `StampAuthoredIntent` declared at [AIECore.cs:479](../../../../AIECore.cs#L479) (field write at [`:484`](../../../../AIECore.cs#L484)) | Yes | **No change.** Smart reads the same field at [TankAIHelper.cs:49](../../../../TankAIHelper.cs#L49). |
| `TankAIHelper.AuthoredHintInvalidated : bool` | Yes — set on block edits + splits at [TankAIHelper.cs:836,866](../../../../TankAIHelper.cs#L836) | **Not at classify-time** — flag is always `false` at spawn (reset by `StampAuthoredIntent` at [AIECore.cs:485](../../../../AIECore.cs#L485)). Reserved for a future invalidation hook only. | **No change at spawn.** Per L2, identity is never recomputed in v0.1. |
| `RawTech.purposes : HashSet<BasePurpose>` (and `RawTechPopParams.Purposes`) | No — read at spawn time by RawTechLoader, never stamped onto helper | Yes | **One additive change**: add `internal HashSet<BasePurpose> AuthoredPurposes` on TankAIHelper, stamp it in extended `StampAuthoredIntent`, pass through from the three call sites in [`Templates/RawTechLoader.cs`](../../../../../Templates/RawTechLoader.cs) (lines 1102, 2264, 2315). **No RawTech file format change.** |
| `TankAIHelper.DediAI : AIType` | Yes — but set by GUIAIManager (player UI), NetworkHandler (MP sync), and TankAIHelper reset paths — **NOT by `EnemyMind.Setup`**. Default at spawn is `AIType.Escort` ([TankAIHelper.cs:82](../../../../TankAIHelper.cs#L82)). | **DO NOT** read for Gatherer detection — authored enemy harvesters carry no DediAI signal at OnTechSpawn. | Identity classifier MUST rely on `AuthoredPurposes ∩ {Harvesting, HarvestingNoHQ, Autominer}` for authored Gatherer detection. DediAI is reserved for player-tagged techs only. |

**Backwards compatibility**: Modders **do not need to re-author** anything. Every existing RawTech already declares `terrain` and `purposes`. Unauthored techs (player builds, conversions, captured techs) hit composition fallback. Vanilla spawns work identically.

---

## 9. Training signal implications

Each identity tags every training event with its `SmartIdentity` value. The four LearningService trainers (Intent / ActionValue / TrajectoryResidual / ThreatAssessment) **stay as singletons** — the tag is for stratified data collection now and per-identity reward shaping later. No new model instances in v0.1.

| Identity | Primary model | "Good outcome" → publish event | Publish site |
|---|---|---|---|
| Hunter / Sniper / AircraftHunter | ActionValue + ThreatAssessment | `KillScored(attackerId, victimId, range)` when attacker had this victim as recent target | Extend `SmartEventBridge.OnTankDestroyed` — already wired |
| Base | ThreatAssessment | `BaseHeld(techId, hpFraction, durationSec)` — 60s of damage absorbed without falling AND no displacement | New periodic poll in `SmartPerTechState.TickMainThread` — cheap |
| Gatherer | Intent + ActionValue | `BlockDelivered(techId, destBaseId, count)` — proximity check (carrying-state proxy → near team base) | New per-tick check in `SmartPerTechState.TickMainThread`. Real engine-side delivery hook is a v0.2 item. |
| AircraftSupport | ActionValue | `AllyProtected(supportId, allyId)` — same-team ally takes no damage while AircraftSupport within 200m | Ring buffer of `DamageObserved` events on TeamRuntime; cross-reference against AircraftSupport proximity |
| Generic | (no identity-specific signal) | (existing path) | n/a |

**New struct on `WorldEventBus`**:

```csharp
public readonly struct IdentityOutcome
{
    public readonly TechId TechId;
    public readonly SmartIdentity Identity;
    public readonly OutcomeKind Kind;     // KillScored | BaseHeld | BlockDelivered | AllyProtected
    public readonly float Magnitude;
}
```

Each trainer's event queue gains an `Identity` field on its existing event payload struct (no queue forking) so future per-identity stratification is one-line. v0.1.0 collects tagged data only.

---

## 10. Pollution risks + how the design prevents them

| # | Risk | Mitigation |
|---|---|---|
| R1 | **Identity becomes a god-switch read across the stack**, making removal/replacement impossible. | Identity is read ONLY by goal sources (in `ContinuousController.OnOperationsTick`) and the IdentityOutcome publisher. **Nothing in `Control/SamplingMPC`, `Control/CostFunction`, `Pathing/`, or `Coordination/Hungarian/PUCT` reads `SmartIdentity`.** Hard design rule. |
| R2 | **Coordinator authority gets undermined** by identity sources overriding strategic plans. | Coordinator's `_readExternalGoal` precedence is preserved exactly as today (first check). Identity sources only run in the previously-flat fallback path. v0.2 PUCT can freely supersede identity by publishing a goal. |
| R3 | **Adding a new identity becomes an N-site edit** as identity dispatch is duplicated across modules. | `ISmartGoalSource` dispatch is a single `Dictionary<SmartIdentity, ISmartGoalSource>` populated at `SmartRuntime.Init`. Adding an identity = one enum value + one new producer class + one registry line + one classifier rule. Outcome publisher has one switch in one file. |
| R4 | **Stamped-at-spawn identity becomes stale** after player edits / team changes / captures. | Per L2, identity is stamped once and never recomputed. A future invalidation hook can be added (the `AuthoredHintInvalidated` flag already exists in TankAIHelper for terrain) but is **not wired in v0.1**. Stale identity is a known tradeoff. |
| R5 | **Per-identity training signals fragment the four shared LearningService models.** | Identity is a **tag** on training events, not a separate trainer. Single shared model per type; tag enables stratification later without forking. |
| R6 | **Forced-rebuild at spawn doubles main-thread cost.** | The rebuild was already happening on the first non-spawn tick (when `LastObservationTick % 30 == 0`). We're relocating it, not adding it. Net cost neutral. |

---

## 11. Open questions for you (need decisions before code lands)

| Q | Question | Default if you don't pick |
|---|---|---|
| Q1 | **Gatherer composition fallback is weak.** Without a `BlockRole.Harvester` or known harvester block-type list, composition-Gatherer is just "unarmed + mobile." Acceptable for v0.1, or add the block-role enum + type-hash whitelist now? | Accept v0.1 limitation — modders who want Gatherer should author `BasePurpose.Harvesting`. Composition-Gatherer is best-effort. |
| Q2 | **Solo Gatherer's "hub"**. You said "deliver to base if on a team, to a hub if not." Use `Stamp.SpawnAnchor` as the hub, or use the nearest neutral receiver/dispenser in the world? | `SpawnAnchor` — simplest, deterministic, and matches solo-prospector field behavior. |
| Q3 | **Flee / disengage rules for v0.1.** Hunter/Sniper/AircraftHunter currently never flee in my draft. Do you want HP-based flee in v0.1 (and if so, what threshold), or save it for v0.2? Gatherer flees on provocation regardless. | No HP-flee in v0.1 — keeps the "Hunters press" feel; revisit when health-belief tracking is solid. |
| Q4 | **Sniper composition threshold.** `mean WeaponProfile.Range > 180` won't fire until real weapon ranges land (currently placeholder `Range=100`). Ship Sniper as authored-only in v0.1 (composition-Sniper dead until v0.2), or use a different composition heuristic now? | **DECIDED — Authored-only Sniper for v0.1.** Composition row struck from §5.2 row 5; the table now explicitly notes "Deferred to v0.2." Composition-Sniper lands when WeaponProfileBuilder reads real ranges. |
| Q5 | **Mixed-purpose techs.** A tech with both `Harvesting` AND `Defense` purposes: Base or Gatherer? | **DECIDED — Defense always wins** (regardless of `NotStationary`). §5.2 Base rule now says `purposes ∩ {Headquarters, Defense, TechProduction}` non-empty — no `NotStationary` gate. A mobile defensive harvester classifies as Base. |
| Q6 | **In-game visibility.** Should identity show up in the existing AI Profile picker UI (so you can see "Hunter (authored)" next to a tech name), or stay debug-only? | Stay debug-only for v0.1, surface in UI v0.2 once it's stable. |

---

## 12. Implementation order (phased landing)

Each phase builds + deploys cleanly without behavioral regression. Phase 1 is pollution-proofing; Phase 2 is the visible win.

### Phase 1 — Seam (minimal behavior change)
1. Add `SmartIdentity` enum + `SmartIdentityStamp` struct + `ISmartGoalSource` interface + `IdentityContext` struct + empty `SmartIdentityRegistry`.
2. Add `SmartPerTechState.IdentityStamp` + `.GoalSource` + `StampIdentity()` + `RebuildVehicleSnapshotNow(Tank)` method (refactored from `TickMainThread` body).
3. Add `SmartIdentityClassifier.Classify` returning `SmartIdentity.Generic` only (stub — all real rules land in Phases 2-5).
4. Add `TeamRuntime.TryGetCentroid(out Vector3)` aggregation.
5. Add `TankAIHelper.AuthoredPurposes` field + extend `AIECore.StampAuthoredIntent(Tank, BaseTerrain, HashSet<BasePurpose>)` + update three `Templates/RawTechLoader.cs` call sites.
6. Wire `SmartForm.OnTechSpawn` (forced rebuild + classifier call, placed AFTER `World.RegisterTech`, BEFORE `teamRuntime.RegisterTech`).
7. Wire `ContinuousController.OnOperationsTick` goal-source check (two new `Func<>` ctor params).

**Behavioral note (corrected from prior "zero-regression" claim)**: every tech classifies as Generic in Phase 1 so the goal-source dispatch path falls through to `TacticalOptimizer.Step` exactly as today. **One behavior delta**: `World.RegisterTech` now receives real `Thrust.MaxLinearAccelPositive.y` for `maxAccelEstimate` at spawn instead of the `5f` `MobilityProfile.Default` fallback. Kalman variance growth bounds change accordingly during the first ~30 ticks of every Smart tech's life. This is an improvement (more accurate belief tracking from frame 0) — not a regression. Build + deploy and confirm the goal-source dispatch behaves as expected.

**No `GenericGoalSource` class needed** — Generic identities bypass the source-dispatch path entirely (see §7.3). Phase 1 step 4 (formerly "Add `GenericGoalSource`") removed.

### Phase 2 — Hunter (first visible win)
1. Implement `HunterGoalSource` (nearest-hostile pursuit + Lissajous meander).
2. Implement `SmartIdentityClassifier` Hunter rule (authored + composition).

Build + deploy. Spawn a Hunter in an empty test area — should meander deterministically. Spawn one with enemies — should pursue.

### Phase 3 — Base + Sniper
Stationary identities — easy to validate ("does it stay put / sit at max range and shoot?").

### Phase 4 — Gatherer
Carries the most v0.2 deferred work (real delivery hook, real resource belief). Ship with placeholder behavior + `SpawnAnchor` hub.

### Phase 5 — AircraftSupport + AircraftHunter
Aircraft split; needs the (currently still-flaky) lift/no-nosedive aircraft path solid first.

### Phase 6 — IdentityOutcome training events
Wire kill / damage-absorbed / delivery events with identity tag. No new models — just tagged collection.

### Future (v0.2+)
- Per-identity model heads (when training data warrants).
- Invalidation hook for block-edits / captures.
- Defender / Brawler / Energizer identities if observed gameplay calls for them.
- Real resource-node belief channel for Gatherer.

---

## 13. Convergence summary (where the 10 agents agreed / diverged)

**Universal agreement (10/10):**
- Hookpoint = `SmartForm.OnTechSpawn` (not `SmartEventBridge.OnTankSpawned`)
- Plumbing = ContinuousController.OnOperationsTick: Coordinator → Identity → TacticalOptimizer
- Generic fallback necessary for zero-regression
- Authored hint first, composition fallback second
- `AuthoredPurposes` is the right additive field; no RawTech format change needed
- Goal sources are stateless singletons; per-tech scratch lives on state
- Identity is a *tag* on training events, not new model heads

**Strong consensus (7-9/10), reconciled to majority:**
- Sniper as distinct enum value, not Hunter sub-flag (7/10) → adopted
- Stamp via `SmartIdentityStamp` struct rather than bare enum (4/10 proposed, adopted for diagnostic value)
- TacticalOptimizer bypassed entirely for non-Generic (6/10), not used as Adam-refines-around-identity (1/10) → bypass adopted

**Divergence, reconciled by reading the source:**
- "Extra" identities (Brawler / Defender / Scout / Sailor / Energizer / Guard): rejected per §4 — your named six are the right shipping set; the rest belong to tactical-role or v0.2 work.
- Force-rebuild-at-spawn vs. lazy classification: forced rebuild adopted (cleaner than provisional + reclassify).
- "On a team" definition: strict `tank.Team == ` int per your L3, no team-relations layer.

---

## 14. What I'm asking for

1. **Answer the 4 remaining open questions in §11** (Q4/Q5 now decided per review; Q1/Q2/Q3/Q6 still need a call — defaults acceptable).
2. **Confirm or amend the enum shape in §3** (adding/removing identities is cheap before code starts).
3. **Approve the implementation order in §12**, or rearrange phases. Note Phase 1 now has a small behavior delta (first-frame `maxAccelEstimate` improves from `5f` fallback to real value).
4. **Approve the two additive changes**:
   - `TankAIHelper.AuthoredPurposes` field + extended `StampAuthoredIntent` (touches `AIECore.cs` + `Templates/RawTechLoader.cs`).
   - `SmartPerTechState.RebuildVehicleSnapshotNow(Tank)` method (refactored from existing `TickMainThread` body — no new behavior, just a callable rebuild path).

Once approved, Phase 1 lands the seam + the one shared-helper change; Phase 2 (Hunter) is the first identity that visibly motivates motion.

---

## 15. Revision history

**Rev 2 (after 5-agent adversarial review):** applied the following corrections — all verified against the source:

- **§5 (pre-classification)**: `state.TickMainThread(0.033f)` does NOT force a rebuild (the `% 30 == 0` gate skips it). Replaced with explicit `SmartPerTechState.RebuildVehicleSnapshotNow(Tank)` method.
- **§5.1 / §8**: `AuthoredHintInvalidated` flag is always false at classify-time; references corrected to make this explicit. Reserved for a future invalidation hook only.
- **§5.1 / §8**: `helper.DediAI` is NOT set by `EnemyMind.Setup` (review found no such assignment); set only by GUIAIManager / NetworkHandler / TankAIHelper reset paths. Gatherer authored rule now uses `purposes` only — no DediAI dependency.
- **§5.2**: Explicit row evaluation order documented (1=Base → 7=Generic). Resolves multi-match ambiguity.
- **§5.2**: `Autominer` moved to Gatherer only (was in both Base and Gatherer authored sets); per `EBasePurpose.cs:36` comment, the tag is mobile-miner only.
- **§5.2**: Q5 rule clarified — `Defense` / `Headquarters` in purposes makes Base win regardless of `NotStationary`. Mobile defensive harvester classifies as Base.
- **§5.2**: Sniper composition row struck (the `Range > 180` rule was unreachable due to the `100f` placeholder in `WeaponProfile.cs:85`). v0.1 ships authored-only Sniper.
- **§5.2**: `ManTechs.IterateTechs()` corrected to `ManTechs.inst.IterateTechs()` (instance method on singleton).
- **§5.2**: `tank != self` filter elevated from "defensive" to "load-bearing correctness."
- **§5.2**: `tank.IsAnchored` caveat added — the flag may not yet be flipped at OnTechSpawn (anchor request processed in a later callback). Authored Defense/HQ purposes are the trustworthy Base signal.
- **§5.2**: Hover-class with high vertical authority counts as aircraft for composition (handles choppers that don't reach Airplane threshold).
- **§5.2**: `Space` consistently in both Aircraft rows (was inconsistently split).
- **§6**: `IdentityContext.TeamCentroid` no longer uses `Vector3.zero` sentinel — world origin is a valid position. Consumers MUST gate on `HasAllies` before reading.
- **§7.2**: Classifier insertion moved AFTER `SmartRuntime.World.RegisterTech` (was before) so the BeliefState is registered before identity stamps.
- **§7.3**: `GenericGoalSource` removed — the `!= Generic` guard made it unreachable. Generic falls through inline to `TacticalOptimizer.Step`. The `SmartIdentityRegistry` only contains non-Generic identities.
- **§7.3**: New `TeamRuntime.TryGetCentroid(out Vector3)` aggregation explicitly called out as a Phase 1 add.
- **§8**: File paths corrected — `Templates/RawTechLoader.cs` not `AI/RawTechLoader.cs`; `World/EventBus.cs` (class `WorldEventBus`) not `World/WorldEventBus.cs`. Line citations refined.
- **§11 Q4, Q5**: Resolved per above; marked DECIDED.
- **§12 Phase 1**: Honest about the one behavior delta (first-frame `maxAccelEstimate` improves). Removed the `GenericGoalSource` step. Added `TeamRuntime.TryGetCentroid` as an explicit step.

**Confirmations from review** (claims verified accurate as written):
- `AIECore.StampAuthoredIntent` declared at line 479, field write at line 484.
- Three `Templates/RawTechLoader.cs` call sites at exact lines 1102, 2264, 2315.
- All `BasePurpose` and `BaseTerrain` enum values named in the doc exist.
- `ContinuousController.cs:153-168` matches the doc's goal-selection snippet line-for-line.
- `SmartForm.OnTechSpawn` exists at the cited line range; insertion gap exists where claimed.
- `OnOperationsTick` is main-thread; goal sources will not run off-thread.
- `RoleAssignment.Role` is orthogonal to identity (verified — different axis).
- `WeaponProfile.Range` is hardcoded `100f` at `WeaponProfile.cs:85`.
