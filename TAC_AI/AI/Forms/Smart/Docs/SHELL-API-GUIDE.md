# SHELL-API-GUIDE.md

**Form:** Smart (working name per [FORM-SPECIFICATION.md OD-1](FORM-SPECIFICATION.md#section-7-open-decisions))
**Version:** 0.2.2
**Status:** AUTHORITATIVE — Documents the shell surface the Smart form consumes. Grounded against the current `TAC_AI` source at the time of writing.

---

## CHANGES SINCE 0.2.1

Verification pass against the actual codebase. Two specific corrections:

- **`ModuleDamage.DamageInfo` → `ManDamage.DamageInfo`** in §7.1 (event catalog table) and §11 (engine type allowlist). Verified against the actual handler signature at `TankAIHelper.cs:1326` (`internal void OnHit(ManDamage.DamageInfo dingus)`).
- **OQ-10 resolved conclusively.** The `IMovementAIController` interface is in-repo at [TAC_AI/AI/Contracts/IMovementAIController.cs](../../Contracts/IMovementAIController.cs); §2.6 now embeds the actual 15-member surface (7 properties + 8 methods). The companion reference base class [`MovementControllerBase`](../../../Movement/MovementControllerBase.cs) is also noted. §12 open-questions table updated.

## CHANGES SINCE 0.2.0

- Layer 3 research findings absorbed. Several `[OPEN]` questions are now resolved with empirical findings from a code investigation of `TAC_AI/`:
  - **OQ-3** resolved (§3.4): there IS a pre-first-tick gap; Smart subscribes to `ManGameMode.ModeStartEvent` from `InitGlobal` to bridge it.
  - **OQ-7** resolved (§7.2): TerraTech uses `ManSafeSaves.RegisterSaveSystem(assembly, OnSave, OnLoad)` with `bool Doing` (pre/post) flag.
  - **OQ-8** resolved (§7.2): no public projectile-fired event exists; Smart owns a Harmony patch on `ModuleWeapon.Fire()`.
  - **OQ-9** resolved (§7.2): no public player join/leave event; Smart owns Harmony patches on `NetPlayer.OnStartClient` / `OnStartServer`.
  - **OQ-12** resolved (§10): the mod-unload `DeInitGlobal` leak is real — `AIFormRegistry.Clear()` does NOT call `DeInitGlobal` on the active form; the shell-side fix is recommended.
  - **OQ-13** resolved (§7.1): `Tank.DamageEvent` is additive; multiple subscribers coexist safely.
- §12 open-questions table updated to reflect resolutions.

## CHANGES SINCE 0.1.0

- Removed all deference to `NEW_FORM_INTENT.md`. Statements that previously cited "the intent doc declares X" are now stated as Smart's own design (per FORM-SPECIFICATION) or as shell facts (per the in-repo source).
- Reworked the [Section 11](#section-11-what-smart-deliberately-does-not-consume) "what Smart does not consume" boundary to be stated as a SHELL-API-GUIDE invariant directly, not as a reference.
- Reworked the [Section 12](#section-12-open-questions-consolidated) open-questions table to route resolutions to "the subsystem contract for X when authored" rather than to specific named subsystem contracts (which were prematurely created at v0.1.0 and have been deleted).

---

## SECTION 0: AUTHORITY AND READING ORDER

This document is **authoritative** for: the shape of the shell surface Smart consumes; how Smart discovers itself, registers, activates, deactivates, switches; which calls cross threads, which run on Unity main thread only, and what mutation discipline applies; the consolidated list of shell-surface open questions Smart's implementation must resolve before code lands.

This document **defers to** [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for what Smart is and what it deliberately does not consume; [ARCHITECTURE.md](ARCHITECTURE.md) for Smart's internal threading and error handling; the actual source under `TAC_AI/` when this document and the code disagree (the code wins; this document is a bug if so); TerraTech engine types when their signatures are not visible in this repo.

This document **governs** Smart's consumption of the shell surface. Subsystem contracts that touch the shell defer here for the exact signatures and thread affinity.

Reading conventions per [DOCTRINE.md](../../../../../Doctrine%20Documentation/DOCTRINE.md) §4.

---

## SECTION 1: SHELL-FORM RELATIONSHIP (overview)

Smart is an `IAIForm` implementation discovered by reflection. The shell — `AIFormRegistry`, `TankAIHelper`, `ProfileRunner`, `AIModuleBootstrap`, and the existing TerraTech Harmony patches — hands Smart per-tech control through a fixed set of hook methods on the `IAIForm` interface. Smart returns control writes through `TankControl` (engine type) and through the `EControlOperatorSet` intent bus.

[NORMATIVE] All shell hooks run on the **Unity main thread** unless explicitly noted. Smart is free to spin its own workers, but any data the workers read from or write to shell-side state MUST be marshalled to the main thread before crossing the form boundary. See [Section 9 (Threading)](#section-9-threading).

[NORMATIVE] Forms are **stateless singletons**. One instance of `SmartForm` is shared across all techs. Per-tech state lives in `TankAIHelper.FormState` (an `object` slot the form casts to its own type). See [Section 2.3](#23-ontechspawnontechrecycle--per-tech-lifecycle).

---

## SECTION 2: THE `IAIForm` INTERFACE

Defined at [TAC_AI/AI/Forms/IAIForm.cs:22-72](../../IAIForm.cs#L22-L72). Twenty-five methods grouped into seven roles.

### 2.1 Identity

```csharp
string Id { get; }              // stable, unique key for the active-form setting and the registry
string DisplayName { get; }     // human-readable label for the in-game form selector
```

[NORMATIVE] `Id` MUST be a stable string. The persisted active-form setting (`KickStart.AIFormSelected`) is keyed by this. Renaming the `Id` after release invalidates saved player preferences. Smart's `Id` is decided per [FORM-SPECIFICATION.md OD-1](FORM-SPECIFICATION.md#section-7-open-decisions); "Smart" is the working value.

[NORMATIVE] `DisplayName` is shown in the form selector UI ([TAC_AI/AI/Tunables/TunableOptionsPublisher.cs:83-99](../../../Tunables/TunableOptionsPublisher.cs)) and SHOULD describe Smart in one line.

### 2.2 `InitGlobal` / `DeInitGlobal` — global lifecycle

```csharp
void InitGlobal();
void DeInitGlobal();
```

[NORMATIVE] Called by `AIFormRegistry.SetActive` ([AIFormRegistry.cs:58-71](../../AIFormRegistry.cs#L58-L71)) when Smart becomes the active form (`InitGlobal`) and when Smart ceases to be active (`DeInitGlobal`, called on the *outgoing* form). Exceptions are caught by the registry and logged via `DebugTAC_AI.LogError` — they do NOT abort the switch.

[NORMATIVE] `InitGlobal` runs on the main thread. Smart's responsibilities here:
- Spin up form-owned workers (per [ARCHITECTURE.md §2](ARCHITECTURE.md#section-2-process-and-threading-model)).
- Walk `AIECore.IterateAllHelpers()` if Smart needs to attach per-tech state to already-spawned techs.
- Subscribe to cross-tech events Smart needs (per [Section 7](#section-7-game-events)).

[NORMATIVE] `DeInitGlobal` MUST be symmetric: tear down workers, unsubscribe events, clear per-tech state from all live helpers. Anything `InitGlobal` allocated, `DeInitGlobal` releases.

[RATIONALE] The shell guarantees `DeInitGlobal` is called before the next form's `InitGlobal`, so a clean Smart→Other switch never leaves Smart's workers running.

[OPEN] **OQ-1:** Whether Smart's per-player profile writes flush within `DeInitGlobal` (mid-mission swap loses progress otherwise) or are deferred to next save (cleaner but loses in-flight learning). Resolved in the Learning subsystem contract.

### 2.3 `OnTechSpawn` / `OnTechRecycle` — per-tech lifecycle

```csharp
void OnTechSpawn(TankAIHelper helper);
void OnTechRecycle(TankAIHelper helper);
```

[NORMATIVE] Called from [TankAIHelper.cs:805](../../TankAIHelper.cs#L805) (spawn) and [TankAIHelper.cs:906](../../TankAIHelper.cs#L906) (recycle). Smart receives the per-tech `TankAIHelper` and populates `helper.FormState` with Smart's per-tech state object on spawn; clears it on recycle.

[NORMATIVE] The shell never inspects `helper.FormState` ([TankAIHelper.cs:87-89](../../TankAIHelper.cs#L87-L89)). The slot is opaque to the shell.

[NORMATIVE] Smart MUST cast `helper.FormState` to its own per-tech state type in every method that uses it, and MUST handle the null-or-other-form case (a tech that swapped from another form mid-mission may have a null or other-form `FormState`). Form-swap discipline: the outgoing form's `DeInitGlobal` clears `FormState` for every live helper; the incoming form's `InitGlobal` populates `FormState` for every live helper.

### 2.4 `Directors` / `Operations` / `PostUpdate` — per-tick hooks

```csharp
void Directors(TankAIHelper helper, bool host);
void Operations(TankAIHelper helper, bool host);
void PostUpdate(TankAIHelper helper);
```

[NORMATIVE] Called per-tick from [TankAIHelper.cs:2809-2826 and 2762](../../TankAIHelper.cs#L2809-L2826). Order: `Directors` → `Operations` → `PostUpdate`.

| Hook | Called from | Phase responsibility (per IAIForm.cs comments) |
|---|---|---|
| `Directors` | `OnUpdateHostAIDirectors` / `OnUpdateClientAIDirectors` | Recalibrate movement controller; gate pathing for this tick. |
| `Operations` | `OnUpdateHostAIOperations` / `OnUpdateClientAIOperations` | Decision / orchestration tick. |
| `PostUpdate` | `OnPostUpdate` | Post-tick brain (lock-on, block hold). |

[NORMATIVE] `host` parameter:
- `host == true` on the MP host or in single-player.
- `host == false` on a network client (lighter tick).

[NORMATIVE] Per [FORM-SPECIFICATION.md §1.1](FORM-SPECIFICATION.md), Smart is event-driven internally. Smart MAY no-op these tick hooks and do the substantive work in event callbacks plus an ambient 1 Hz vital-signs pass — but MUST still implement the methods. Empty bodies are acceptable; throwing or blocking is not.

[NORMATIVE] These hooks run on the Unity main thread. Heavy compute MUST be dispatched to form-owned workers and consumed in a later tick.

### 2.5 `ControlFrame` — per-frame control bridge

```csharp
void ControlFrame(TankAIHelper helper, TankControl control);
```

[NORMATIVE] Called per-frame from `TankAIHelper.RunMovementBridge` ([TankAIHelper.cs:2517-2620](../../TankAIHelper.cs#L2517-L2620)), which is itself called by the `ModuleTechController.ExecuteControl` Harmony prefix. This is the **only** hook Smart should write `TankControl` from. There are seven call sites in `RunMovementBridge` distinguished by MP host/client, alignment, RTS state, and `SetToActive`; Smart does not need to distinguish them — Smart receives the same `control` reference in each.

[NORMATIVE] `ControlFrame` runs on the Unity main thread. It MUST be O(constant) in compute. Per [FORM-SPECIFICATION.md §5.8](FORM-SPECIFICATION.md) and [ARCHITECTURE.md §3](ARCHITECTURE.md#section-3-tick-lifecycle), `ControlFrame` is consumptive only — the MPC solve runs in a worker; `ControlFrame` reads the latest published solution.

[NORMATIVE] If `RunMovementBridge` returns false, the shell suppresses firing and `ControlFrame` did not run for this frame. Smart MUST tolerate a frame in which `ControlFrame` is skipped.

### 2.6 v2 isolation seams

```csharp
void OnWorldReset();
void DrawPathingDebugGUI();
IMovementAIController CreateMovementController(TankAIHelper helper, MovementContainerKind kind, EnemyMind mind);
int GetAirDiveState(TankAIHelper helper);
int GetAirUTurnState(TankAIHelper helper);
```

[NORMATIVE] `OnWorldReset`: called on world unload/teardown. Smart MUST reset its world-model, belief state, and any pathfinding caches. Form-owned workers SHOULD continue running (no need to tear down workers per world); their input snapshots are reset.

[NORMATIVE] `DrawPathingDebugGUI`: called from the debug spawner. Smart's diagnostics layer draws here (specific contents per the Diagnostics subsystem contract when authored).

[NORMATIVE] `CreateMovementController`: returns a per-tech `IMovementAIController` (interface defined in the engine namespace; see [VanillaMovementController.cs](../../Vanilla/VanillaMovementController.cs) for a reference implementation). Called from [TankAIHelper.Controller.cs:22-136](../../../TankAIHelper.Controller.cs). `MovementContainerKind` is one of `Default`, `Air`, `Static`. Smart is free to return the same controller instance for all kinds (Smart's MPC controller is unified per [FORM-SPECIFICATION.md §1.2](FORM-SPECIFICATION.md)) or distinct controllers per kind.

[NORMATIVE] The returned controller MUST be a `MonoBehaviour` (the existing pattern is `helper.gameObject.AddComponent<...>()`). The shell stores it in `helper.MovementController` and never names the concrete type.

[NORMATIVE] `GetAirDiveState` / `GetAirUTurnState`: return 0 if the tech is not in an airplane maneuver. Used by the status-text overlay ([TankAIHelper.cs:1657, 1673](../../TankAIHelper.cs#L1657)). Smart returns 0 unless its Control subsystem implements airplane-specific maneuvers with externally-visible state.

[RESOLVED v0.2.2 — was OQ-10] The interface is in-repo at [TAC_AI/AI/Contracts/IMovementAIController.cs](../../Contracts/IMovementAIController.cs). Full surface (verified against source):

```csharp
public interface IMovementAIController
{
    IMovementAICore AICore { get; }
    Tank Tank { get; }
    TankAIHelper Helper { get; }
    Enemy.EnemyMind EnemyMind { get; }
    Vector3 PathPoint { get; }   // where the tech is moving towards
    float GetDrive { get; }       // forwards drive of the tech
    bool Grounded { get; }        // v2: true only for grounded air controller

    void Initiate(Tank tank, TankAIHelper helper, Enemy.EnemyMind mind = null);
    void UpdateEnemyMind(Enemy.EnemyMind mind);
    void DriveDirector(ref EControlCoreSet core);
    void DriveDirectorRTS(ref EControlCoreSet core);
    void DriveMaintainer(ref EControlCoreSet core);
    void OnMoveWorldOrigin(IntVector3 move);
    Vector3 GetDestination();
    void Recycle();
}
```

15 members total: 7 properties + 8 methods. The three `Drive*` methods write to the `EControlCoreSet` intent bus (passed by `ref`). Per [CONTROL-CONTRACT.md §8](CONTROL-CONTRACT.md#section-8-smartmovementcontroller-oq-10), Smart's controller writes the latest MPC-derived intent to those buffers as a thin pass-through; the substantive control work goes through `ControlFrame` per OQ-6.

A reference base class [`TAC_AI/AI/Movement/MovementControllerBase.cs`](../../../Movement/MovementControllerBase.cs) provides the common plumbing (Tank/Helper/AICore/EnemyMind storage, GetDrive forwarding, Initiate template, OnPre/OnPost/OnRecycle hooks); Smart MAY extend it OR implement the interface directly (Vanilla's controller does the latter).

### 2.7 Combat policy + per-tick combat dispatch

```csharp
EAttackMode SelectEnemyAttackStrat(Tank tank, EnemyMind mind);
EAttackMode SelectAlliedAttackStrat(TankAIHelper helper);
bool EnemyHasArtillery(BlockManager bm);
void RunEnemyCombatChecking(TankAIHelper helper, Tank tank, EnemyMind mind);
void RunEnemyMonitor(TankAIHelper helper, Tank tank, EnemyMind mind);
void RunEnemyBolts(TankAIHelper helper, Tank tank, EnemyMind mind);
void RunEnemyRepairStep(TankAIHelper helper, Tank tank, EnemyMind mind, bool venPower);
void RunEnemyRTSCombat(TankAIHelper helper, Tank tank, EnemyMind mind);
```

[NORMATIVE] These are shell-driven hooks the existing `TankAIHelper` orchestration calls during housekeeping, stance switching, retreat, etc. Smart implements them so that **the shell names no Smart-internal combat type**. Per [FORM-SPECIFICATION.md §1.10](FORM-SPECIFICATION.md), Smart owns all combat logic; these methods are the shell's call sites for it.

[NORMATIVE] `SelectAlliedAttackStrat` is called from [TankAIHelper.cs:1106](../../TankAIHelper.cs#L1106) when the helper resolves its attack mode. Returning a value other than the helper's prior mode causes the helper to switch stance.

[NORMATIVE] `RunEnemyRTSCombat` is called from [TankAIHelper.cs:2716 and 2735](../../TankAIHelper.cs#L2716) during the enemy RTS combat path. Smart's tactical/strategic optimizer is invoked here for enemy techs.

[OPEN] **OQ-2:** Combat-dispatch surface mapping. Five `RunEnemy*` methods — Smart must decide which map to distinct Planning/Control behaviors and which collapse into one. Resolved when the Planning and Control subsystem contracts are authored.

### 2.8 `RunEnemy` / `RunAllied` — per-tech dispatch

```csharp
void RunEnemy(AIContext ctx, EnemyMind mind);
void RunAllied(AIContext ctx);
```

[NORMATIVE] Called from [ProfileRunner.cs:17 and 23](../../../Engine/ProfileRunner.cs) on every per-tech operations tick. This is the main behavioral entry point on the bus. `AIContext` is the read/write bus exposing the helper's mutable state ([Section 4](#section-4-aicontext-bus)).

[NORMATIVE] Per [FORM-SPECIFICATION.md §1.1](FORM-SPECIFICATION.md), Smart's `RunAllied` / `RunEnemy` are mostly no-ops at tick; the real work happens in event callbacks with an ambient 1 Hz pass for liveness. Smart MUST still tolerate being called every tick (the shell does call every tick).

---

## SECTION 3: REGISTRATION AND ACTIVATION

### 3.1 Registry

[NORMATIVE] `AIFormRegistry` ([AIFormRegistry.cs](../../AIFormRegistry.cs)) discovers `IAIForm` implementations by reflection over the executing assembly. To register:

1. Implement `IAIForm` in a non-abstract, non-interface class with a parameterless constructor anywhere in `TAC_AI.dll`.
2. Return a unique non-empty `Id`.

That is the entire registration protocol. Smart's existence in the assembly is its registration.

[NORMATIVE] If registration throws, the registry logs an error and continues with the forms that did register ([AIFormRegistry.cs:46-49](../../AIFormRegistry.cs#L46-L49)). A broken Smart form does not break Vanilla.

### 3.2 Default form

[NORMATIVE] `AIFormRegistry.DefaultFormId = "Modified"` ([AIFormRegistry.cs:17](../../AIFormRegistry.cs#L17)). There is currently no `ModifiedForm.cs` file — Modified's logic is embedded in the shell (`RCore.*`, `BGeneral.*`, `TankAIHelper` partials). The default falls through to whatever is registered when "Modified" is absent.

[NORMATIVE] Per [FORM-SPECIFICATION.md §3.3.2](FORM-SPECIFICATION.md), Smart does not touch the Modified extraction. The default-fallback gap is captured as [FORM-SPECIFICATION.md OD-9](FORM-SPECIFICATION.md#section-7-open-decisions).

### 3.3 Activation

[NORMATIVE] `AIFormRegistry.SetActive(id)` ([AIFormRegistry.cs:58-71](../../AIFormRegistry.cs#L58-L71)) selects the active form. Sequence:

1. Resolve the requested `id`. If unknown, try `DefaultFormId`. If still unknown, pick the first registered form.
2. If the new form is the same as the current form, no-op.
3. Otherwise: call `current.DeInitGlobal()` (errors logged, swallowed), set `active = next`, call `next.InitGlobal()` (errors logged, swallowed).

[NORMATIVE] `ActiveId` is updated even when `InitGlobal` throws. Smart's `InitGlobal` MUST be defensive enough to leave Smart in a usable degraded state on error, or to rethrow only after cleaning up its own partial allocations.

### 3.4 Bootstrap

[NORMATIVE] `AIFormRegistry.ScanAndRegister()` is called by `AIModuleBootstrap.InitAIModules()` ([AIModuleBootstrap.cs:33](../../../Engine/AIModuleBootstrap.cs#L33)), which is called from `ProfileRunner.RunEnemy` and `ProfileRunner.RunAllied` ([ProfileRunner.cs:15-22](../../../Engine/ProfileRunner.cs)) — i.e., lazily, on first per-tech tick. `KickStart.MainOfficialInit` does NOT call `ScanAndRegister` directly.

[NORMATIVE] The persisted form-id (`KickStart.AIFormSelected`, [KickStart.cs:143](../../../../KickStart.cs#L143)) is applied to `SetActive` from `TunableOptionsPublisher` ([TunableOptionsPublisher.cs:87](../../../Tunables/TunableOptionsPublisher.cs#L87)) when the options panel publishes its dropdown. The active form may be the registry default until the player opens the options panel.

[RESOLVED v0.2.1 — was OQ-3] Yes, there is a pre-first-tick gap. `AIModuleBootstrap.InitAIModules()` (which calls `AIFormRegistry.ScanAndRegister` and applies the persisted `KickStart.AIFormSelected`) runs lazily from `ProfileRunner.RunEnemy/RunAllied` — i.e., on the first per-tech tick of a mission. `ManGameMode.ModeStartEvent` fires *before* that point, during world load. Existing TAC AI code subscribes to `ModeStartEvent` from elsewhere (e.g., [ManEnemyWorld.cs:376](../../../Enemy/ManEnemyWorld.cs)). Smart's resolution: subscribe to `ManGameMode.inst.ModeStartEvent` from Smart's `InitGlobal` so that whenever Smart becomes active (whether at first tick or via the options panel later), it catches subsequent world starts. For the first-mission case before Smart has had any `InitGlobal` run, Smart misses the very first `ModeStartEvent`; the World subsystem MUST tolerate joining the mission late and reconstructing state from the next snapshot.

---

## SECTION 4: AICONTEXT BUS

[NORMATIVE] `IAIContext` ([IAIContext.cs](../../../Engine/IAIContext.cs)) is the read/write bus passed to `RunEnemy` / `RunAllied`. It is the documented contract for what behaviors mutate on the helper. Backing types: `TankAIHelper` (the bus + sinks) and `EnemyMind` (the typed enemy facade).

[NORMATIVE] The bus is **frozen** at its current shape — [`IAIContext.cs:7-22`](../../../Engine/IAIContext.cs#L7-L22) declares so explicitly. Smart treats this as the only sanctioned read/write surface for `RunEnemy` / `RunAllied`.

The bus exposes ~150 properties and methods grouped into:

| Group | Members (representative) |
|---|---|
| Identity / refs | `Tank`, `IsEnemy`, `MovementController`, `TickAIAlign` |
| Combat posture (R/W) | `AttackMode`, `WantsToFight`, `Retreat`, `Provoked`, `Urgency`, `FIRE_ALL` |
| Physics (R) | `SafeVelocity`, `LocalSafeVelocity`, `RecentSpeed`, `GroundOffsetHeight`, `DodgeSphereCenter` |
| Targeting | `LastEnemyGet`, `SetPursuit`, `EndPursuit`, `ReleaseTarget`, `TryRefreshEnemy*` |
| Throttle / drive intent (W) | `ThrottleState`, `DriveVar`, `AutoSpacing`, `FullBoost`, `Attempt3DNavi`, `Navi3DDirect` |
| Objective slots (W) | `TheResource`, `TheBase`, `LastPlayer`, `IsMultiTech`, `HeldBlock` |
| Timers / clocks (W) | `ActionPause`, `DelayedAnchorClock`, `NextFindTargetTime`, `PendingDamageCheck` |
| Anchoring | `AutoAnchor`, `TryInsureAutoAnchor`, `Unanchor`, `AnchorIgnoreChecks` |
| Drive operator verbs | `OpStop`, `OpForwards`, `OpReverse`, `OpDriveToFacingTowards`, `OpSetLastDest`, `MarkOperatorDirty` |
| Engine services | `TryHandleObstruction`, `AutoHandleObstruction`, `SettleDown`, `MaxBoost`, `HoldBlock`, `DropBlock` |
| Strategy sinks (write tank.control) | `DriveControlSink`, `ProcessControl`, `SteerControl`, `SetDirectedControl`, `GetDirectedControl` |
| Mind facade | `MindLikelyMelee`, `MindAttackPlayer`, `MindCanCallRetreat`, `MindSceneStationaryPos` |
| Teardown | `BehaviorTeardownState` |

[NORMATIVE] Smart MAY use the bus as little or as much as it wants. Per [FORM-SPECIFICATION.md §1.10](FORM-SPECIFICATION.md), Smart owns its own world model, planner, and controller — the bus is the **shell's** read/write surface, not Smart's internal one. Smart writes the bus only where the shell or other non-Smart code reads it: throttle/drive intent, combat posture, targeting state, anchoring.

[RATIONALE] Even an event-driven, internally-MPC-controlled Smart must write bus state for things the shell renders to the player (status text, anchoring, RTS HUD) and things other systems read (`EnemyMind` reads `AttackMode`; multiplayer replicates `RTSControlled`).

[OPEN] **OQ-4:** The minimum set of bus properties Smart writes for shell-readback. Resolved when the Control subsystem contract is authored.

---

## SECTION 5: CONTROL OUTPUT (`TankControl` and `EControlOperatorSet`)

There are two layers of control output.

### 5.1 `TankControl` — direct per-frame engine API

[NORMATIVE] `TankControl` is a TerraTech engine type (not in this repo). Smart writes to it in `ControlFrame`. The shell's existing usage (`RunMovementBridge` + the Harmony `ExecuteControl` prefix) treats the form's writes as authoritative for the frame.

**OQ-5 [RESOLVED v0.2.3]:** Verified during step 1.9 + 1.11 implementation. The canonical multi-axis write is `tank.control.CollectMovementInput(driveVec, turnVec, throttleVec, props, jets)` used by `TankAIHelper.ProcessControl`. Single-axis sinks: `tank.control.DriveControl` (float), `tank.control.FireControl` (bool), `tank.control.BoostControlJets`/`BoostControlProps` (bools), `tank.control.TargetPositionWorld`/`TargetRadiusWorld` (Vector3/float). Vanilla collision-avoidance is disabled per-Smart-tech via `tank.control.m_Movement.m_USE_AVOIDANCE = false` so Smart's MPC threat-field cost owns avoidance without conflict.

### 5.2 `EControlOperatorSet` — drive-intent bus

[NORMATIVE] `EControlOperatorSet` ([AIEnums.cs:8-100](../../../../AIEnums.cs#L8-L100)) is a struct exposing `DriveDest`, `DriveDir`, `lastDestination`, plus verb methods (`STOP`, `Forwards`, `Reverse`, `FaceDest`, `DriveToFacingTowards`, etc.). It is mutated **in place** by the bus (the verbs also write `helper.DriveVar` and `helper.ThrottleState`).

[NORMATIVE] Access pattern from a behavior:

```csharp
EControlOperatorSet direct = ctx.GetDirectedControl();
direct.Forwards(helper);          // mutates direct AND helper.ThrottleState / DriveVar
ctx.SetDirectedControl(direct);   // publish back
ctx.MarkOperatorDirty();          // tell the engine the bus changed
```

(Existing pattern at [TankAIHelper.cs:2635](../../TankAIHelper.cs#L2635) and surrounding `RunRTSNaviEnemy` code.)

[NORMATIVE] The engine **harvests** the bus state after `Operations` returns and feeds it through `EControlCoreSet` ([AIEnums.cs:101-203](../../../../AIEnums.cs#L101-L203)) into the next `ControlFrame`.

[NORMATIVE] Smart has two paths to publish control:

- **High-level intent via the bus.** Set `DriveDest`, `DriveDir`, `lastDestination` through `EControlOperatorSet`. The engine resolves this into actuation via the existing movement-controller chain. Higher latency, higher abstraction, MP-replication-safe.
- **Direct write via `TankControl` in `ControlFrame`.** Bypass the bus; write throttle/steer/brake directly. Lower latency; Smart handles frame-rate; MP-replication implications unknown.

**OQ-6 [RESOLVED v0.2.3]:** Direct `TankControl` write. Per the Control contract authoring and the step-1.9/1.11 implementation, Smart's MPC publishes a `ControlProfile` that `SmartForm.ControlFrame` consumes and writes verbatim to `tank.control` via `CollectMovementInput` + `FireControl` + `TargetPositionWorld`. `SmartMovementController.DriveDirector*` callbacks remain no-ops; the engine's `EControlCoreSet` intent bus is not touched. This decouples Smart from any bus-reader assumptions baked into other engine paths.

### 5.3 Strategy sinks on the bus

[NORMATIVE] `IAIContext` exposes `DriveControlSink`, `ProcessControl`, `SteerControl`, `SetDirectedControl` (and the `Op*` verbs). The `IAIContext.cs` comment names these as "the strategy-facing sinks (these alone may touch tank.control, via the helper)" — meaning bus-mutation goes through these, not direct field assignment.

[NORMATIVE] Smart SHOULD prefer these sinks when writing the bus, to keep behavior compatible with the existing tick discipline (in particular `MarkOperatorDirty`).

---

## SECTION 6: TANKAIHELPER FIELD SURFACE

Smart receives `TankAIHelper helper` in every per-tech hook. The fields/properties Smart is expected to read or write are documented through `IAIContext` (Section 4). Direct field access on the helper (not via `IAIContext`) is permitted (same-assembly) but discouraged.

[NORMATIVE] Fields Smart **owns**:

- `helper.FormState` ([TankAIHelper.cs:89](../../TankAIHelper.cs#L89)): `object` slot. Set in `OnTechSpawn`, cleared in `OnTechRecycle`. Cast in every other method. The shell never inspects this.
- `helper.CurrentPlannedPath` ([TankAIHelper.cs:97](../../TankAIHelper.cs#L97)): `IReadOnlyCollection<WorldPosition>` — read seam for `ManWorldRTS` to draw Smart's planned path. Smart sets this to its current trajectory (or leaves null). Only read when `helper.UsingPathfinding` is true.

[NORMATIVE] Fields Smart **toggles to gate shell behavior**:

- `helper.ForceVanillaAI` ([TankAIHelper.cs:92](../../TankAIHelper.cs#L92)): used only by the Vanilla form. Smart does NOT touch this.
- `helper.RunState` (via `ctx.RunState`): one of `Default` / `Advanced` / etc. Smart expects `Advanced` (the shell's "the mod is driving" state). Smart does NOT need to touch `RunState` if it stays at `Advanced`.

[NORMATIVE] Fields Smart **reads as input**:

- `helper.tank` — the underlying `Tank`. Used to subscribe to `Tank.DamageEvent`, read block list, etc.
- `helper.AIAlign`, `helper.TickAIAlign` — alignment; latched at `OnPreUpdate`.
- `helper.SetToActive` — whether the helper is ready to drive (vanilla AI type resolved and not Idle).

---

## SECTION 7: GAME EVENTS

TerraTech events are exposed by `Tank` and the `ManXxx` singletons. The existing TAC AI code subscribes to several already; Smart joins them by subscribing on its own behalf in `OnTechSpawn` (per-tech events) or `InitGlobal` (global events) and unsubscribing on the matching teardown.

### 7.1 Per-tech events (subscribe in `OnTechSpawn`, unsubscribe in `OnTechRecycle`)

| Event | Source | Existing subscription (reference) |
|---|---|---|
| Damage taken | `tank.DamageEvent` (`ManDamage.DamageInfo` payload) | [TankAIHelper.cs:766](../../TankAIHelper.cs#L766) subscribes `OnHit` |
| Block attached | `tank.blockman.blockAttachedEvent` (signature `(TankBlock, Tank)`) | [TankAIHelper.cs:825](../../TankAIHelper.cs#L825) `OnBlockAttached` |
| Block detaching | `tank.blockman.blockDetachingEvent` | [TankAIHelper.cs:874](../../TankAIHelper.cs#L874) `OnBlockDetaching` |

[NORMATIVE] Smart's per-tech event handlers MUST be additive (subscribe a separate delegate, not replace the existing one) to avoid breaking the helper's bookkeeping.

[RESOLVED v0.2.1 — was OQ-13] `Tank.DamageEvent` is additive. The existing TAC AI code already runs with two coexisting subscribers — [TankAIHelper.cs:766](../../TankAIHelper.cs#L766) (`OnHit` for general helper bookkeeping) and [EnemyMind.cs:124](../../../Enemy/EnemyMind.cs#L124) (`OnHit` for enemy-specific logic; subscribed in `Initiate`, unsubscribed in `SetForRemoval` with re-subscription of the helper handler). Multiple subscribers coexist safely; Smart adds its own delegate alongside without ordering or replacement.

### 7.2 Global events (subscribe in `InitGlobal`, unsubscribe in `DeInitGlobal`)

| Event | Singleton | Existing subscription (reference) |
|---|---|---|
| Tank post-spawn | `ManTechs.inst.TankPostSpawnEvent` | [TankAIManager.cs:69](../../TankAIManager.cs#L69) |
| Tank team changed | `ManTechs.inst.TankTeamChangedEvent` | [TankAIManager.cs:70](../../TankAIManager.cs#L70) |
| Player tank changed | `ManTechs.inst.PlayerTankChangedEvent` | [TankAIManager.cs:71](../../TankAIManager.cs#L71) |
| Tracking lost | `ManVisible.inst.OnStoppedTrackingVisible` | [TankAIManager.cs:72](../../TankAIManager.cs#L72) |
| Mode start | `ManGameMode.inst.ModeStartEvent` | [TankAIManager.cs:73](../../TankAIManager.cs#L73) |
| Mode switch | `ManGameMode.inst.ModeSwitchEvent` | [KickStart.cs](../../../../KickStart.cs) |

[RESOLVED v0.2.1 — was OQ-7] **Save/load.** TerraTech exposes save/load not as an event but as a registered hook: `ManSafeSaves.RegisterSaveSystem(assembly, OnSave, OnLoad)`. Both callbacks take a `bool Doing` parameter — `true` is the pre-operation call (right before save/load happens), `false` is the post-operation call. The existing TAC AI mod hooks this from [KickStart.cs:560-574](../../../../KickStart.cs) (`HookToSafeSaves`); the callbacks live at [KickStart.cs:1021-1047](../../../../KickStart.cs) (`OnSaveManagers`, `OnLoadManagers`). Smart registers its own save system (with its own assembly reference) from `InitGlobal` for per-player profile persistence.

[RESOLVED v0.2.1 — was OQ-8] **Projectile-fired.** No public event exists on `Tank`, `ModuleWeapon`, or in the TerraTech engine surface visible to TAC AI. The existing weapon-related code in [TankAIHelper.WeaponFire.cs](../../../TankAIHelper.WeaponFire.cs) is an *output* (Smart's "decide to fire" surface), not an input. For Smart to observe enemy weapon firing, **Smart owns a Harmony patch on `ModuleWeapon.Fire()`** (or whichever method emits a projectile in the TerraTech weapon types). The patch raises a Smart-internal event the World subsystem consumes. This is a new shell coupling Smart introduces; document it in the World subsystem contract when authored.

[RESOLVED v0.2.1 — was OQ-9] **Player join/leave.** No public events on `ManNetwork`. TerraTech's existing network handling uses Harmony patches on `NetPlayer.OnStartClient` and `NetPlayer.OnStartServer` (see [NetworkHandler.cs:629-650](../../../../Network/NetworkHandler.cs) for the existing TAC AI pattern). For leave detection, `NetPlayer.OnDestroy` (or equivalent teardown method) is the candidate. Smart owns its own Harmony patches for player-join/leave, used by the Learning subsystem to load/save per-player profiles. Alternative: poll `ManNetwork.inst.GetNumPlayers()` / `GetPlayer(index)` and diff against the prior tick's set — simpler but adds tick cost.

---

## SECTION 8: MOVEMENT CONTROLLER CONTRACT (`IMovementAIController`)

[NORMATIVE] `IMovementAIController` is the per-tech movement abstraction the shell holds. Smart constructs an instance in `CreateMovementController` ([Section 2.6](#26-v2-isolation-seams)) and the shell stores it on `helper.MovementController`.

[NORMATIVE] Recalibration call sites in [TankAIHelper.Controller.cs:22-136](../../../TankAIHelper.Controller.cs):

- `Default` kind: spawn path.
- `Static` kind: when the helper's `DediAI` resolves to a static/anchored type.
- `Air` kind: when the tech is an airplane (enemy `EvilCommander == EnemyHandling.Airplane`).

[NORMATIVE] The Vanilla form's controller ([VanillaMovementController.cs](../../Vanilla/VanillaMovementController.cs)) is the reference for the contract. Smart's controller MUST be a `MonoBehaviour` added to `helper.gameObject` via `AddComponent`.

(OQ-10 from §2.6 applies here as well.)

---

## SECTION 9: THREADING

### 9.1 What runs on which thread

[NORMATIVE] All `IAIForm` hook methods run on the Unity main thread:

- `InitGlobal` / `DeInitGlobal` (from `AIFormRegistry.SetActive`).
- `OnTechSpawn` / `OnTechRecycle` (from `TankAIHelper.OnPool` / `OnSpawn` / `Recycled`).
- `Directors` / `Operations` / `PostUpdate` (from `TankAIHelper.OnUpdate*`).
- `ControlFrame` (from `RunMovementBridge`, called from `ModuleTechController.ExecuteControl`).
- `RunEnemy` / `RunAllied` (from `ProfileRunner`, called from `Operations`).
- `CreateMovementController` (from `TankAIHelper.Controller.cs` recalibration paths).
- Combat-policy hooks (`SelectAlliedAttackStrat`, etc.).

[NORMATIVE] Game-event callbacks (damage, block attach/detach, tank spawn) fire on the Unity main thread (TerraTech engine convention).

### 9.2 Form-owned workers

[NORMATIVE] Smart is free to own background threads / `Task` schedulers / a custom worker pool. Two disciplines apply:

- **Workers MUST NOT touch any TerraTech engine object** (`Tank`, `TankBlock`, `Visible`, `Rigidbody`, `Transform`, any `ManXxx.inst`). The Unity API is main-thread-only.
- **Anything crossing the form boundary MUST be marshalled.** Workers write to form-owned data structures (double-buffered); main-thread hooks read the published snapshot.

The marshalling discipline lives in the Threading subsystem contract.

### 9.3 Form-swap and worker cancellation

[NORMATIVE] `DeInitGlobal` MUST signal workers to cancel and wait for them to exit cleanly before returning. The shell may proceed to `InitGlobal` on the next form immediately after `DeInitGlobal` returns — workers must not survive `DeInitGlobal`.

---

## SECTION 10: KICKSTART LIFECYCLE

[NORMATIVE] `KickStart.MainOfficialInit` ([KickStart.cs](../../../../KickStart.cs)) is the mod's main bootstrap, called by `KickStartTAC_AI.Init` via `ModStatusChecker.EncapsulateSafeInit` ([KickStartTAC_AI.cs:39-46](../../../../KickStartTAC_AI.cs#L39-L46)). It does NOT call `AIFormRegistry.ScanAndRegister` directly — registry init is lazy via `ProfileRunner` / `AIModuleBootstrap.InitAIModules`.

[NORMATIVE] `KickStart.DeInitALL` ([KickStart.cs:840](../../../../KickStart.cs#L840)) tears down event subscriptions and restores vanilla globals. Smart's `DeInitGlobal` is called from `AIFormRegistry.SetActive` on form switch; it is NOT directly called by `KickStart.DeInitALL` (the registry's `Clear` is called there instead, [AIModuleBootstrap.cs:48](../../../Engine/AIModuleBootstrap.cs#L48)).

[RESOLVED v0.2.1 — was OQ-12; status: confirmed risk, mitigation needed] **Mod-unload leak confirmed.** `AIModuleBootstrap.DeInitAIModules()` ([AIModuleBootstrap.cs:43-51](../../../Engine/AIModuleBootstrap.cs#L43-L51)) calls `AIFormRegistry.Clear()` directly, without first calling `SetActive(null)` or `Active.DeInitGlobal()`. `AIFormRegistry.Clear()` ([AIFormRegistry.cs:73](../../AIFormRegistry.cs#L73)) zeroes state with no `DeInitGlobal` call. If a form is active at mod-unload, the form's workers leak.

**Recommended shell-side fix:** add `active?.DeInitGlobal()` to `AIFormRegistry.Clear()` as a defensive last call before zeroing. One-line change; benefits Vanilla and any future form too.

**Smart-side workaround if the shell fix is not made:** Smart subscribes to a teardown event earlier in the chain (e.g., wrap `KickStart.DeInitAIModules` via Harmony, or observe the `KickStart.ShouldBeActive` transition) and runs Smart's worker teardown there. Less clean than the shell fix but achievable.

Tracked in [FORM-SPECIFICATION.md OD-10](FORM-SPECIFICATION.md#section-7-open-decisions) for the user's decision on which path Smart takes.

---

## SECTION 11: WHAT SMART DELIBERATELY DOES NOT CONSUME

[NORMATIVE] Smart MUST NOT read or call:

- `AIEPathMapper`, `AIEAutoPather`, `AIEPathing` — Modified's pathing. Smart owns its own pathing per [FORM-SPECIFICATION.md §1.10](FORM-SPECIFICATION.md).
- Modified's combat brain, target scanner, operations dispatch — currently embedded in `RCore.*`, `BGeneral.*`, and `TankAIHelper` partials. Smart owns its own combat per [FORM-SPECIFICATION.md §1.10](FORM-SPECIFICATION.md).
- Modified-specific tuning (`AISettings.cs` keys Modified consumes). Smart has its own tuning surface.
- Modified's shared obstacle grid, perception cache, or thread pool.

[NORMATIVE] What Smart MAY consume from the shell beyond this guide:

- The `IAIContext` bus (Section 4).
- The minimal field surface on `TankAIHelper` (Section 6).
- TerraTech engine types (`Tank`, `TankControl`, `TankBlock`, `Visible`, `ManXxx.inst` singletons, `ManDamage.DamageInfo`).
- `Singleton.playerTank`, `Singleton.Manager<>.inst` for global state.

[NORMATIVE] Any consumption beyond this list is a NEW shell-form coupling that MUST be documented in this section before the code lands.

---

## <a name="open-questions"></a>SECTION 12: OPEN QUESTIONS (consolidated)

A future session resolving Smart's design fills these in. Each one points to the subsystem contract that owns the resolution (the contracts do not yet exist; they are authored as the workflow reaches each subsystem per DOCTRINE.md §8.3).

| # | Question | Status |
|---|---|---|
| OQ-1 | Mid-mission form-swap persistence flush | Open; resolved by Learning subsystem contract |
| OQ-2 | Combat-dispatch surface mapping for the five `RunEnemy*` methods | Open; resolved by Planning + Control contracts |
| OQ-3 | Pre-first-tick subscription window | **[RESOLVED v0.2.1]** — gap confirmed; Smart subscribes to `ManGameMode.ModeStartEvent` from `InitGlobal`. See §3.4. |
| OQ-4 | AIContext bus write set — minimum properties Smart writes | Open; resolved by Control contract |
| OQ-5 | `TankControl` write API | **[RESOLVED v0.2.3]** — movement: `tank.control.CollectMovementInput(driveVec, turnVec, throttleVec, props, jets)` (verified at [TankAIHelper.Avoidance.cs:26](../../TankAIHelper.Avoidance.cs#L26)); fire: `tank.control.FireControl` + `TargetPositionWorld` + `TargetRadiusWorld`; vanilla collision-avoidance disabled per-Smart-tech via `tank.control.m_Movement.m_USE_AVOIDANCE = false`. |
| OQ-6 | MPC output path — intent bus or direct `TankControl` | **[RESOLVED v0.2.3]** — direct `TankControl` write per Smart's ControlFrame; the `EControlCoreSet` intent bus is left untouched (SmartMovementController's `DriveDirector*` callbacks are no-ops). This decouples Smart from the bus-reader assumptions baked into other engine paths. |
| OQ-7 | Save/load event hook | **[RESOLVED v0.2.1]** — `ManSafeSaves.RegisterSaveSystem(assembly, OnSave, OnLoad)`. See §7.2. |
| OQ-8 | Projectile-fired event source | **[RESOLVED v0.2.1]** — no public event; Smart owns Harmony patch on `ModuleWeapon.Fire()`. See §7.2. |
| OQ-9 | Player join/leave event source | **[RESOLVED v0.2.1]** — no public event; Smart owns Harmony patches on `NetPlayer.OnStartClient`/`OnStartServer`. See §7.2. |
| OQ-10 | `IMovementAIController` full interface | **[RESOLVED v0.2.2]** — interface read from [TAC_AI/AI/Contracts/IMovementAIController.cs](../../Contracts/IMovementAIController.cs); 7 properties + 8 methods. See §8 above. |
| OQ-12 | Mod-unload `DeInitGlobal` leak | **[RESOLVED v0.2.1 — confirmed risk; fix pending]** — `Clear()` does not call `DeInitGlobal`. Shell-side fix recommended; Smart-side workaround available. See §10 and FORM-SPEC OD-10. |
| OQ-13 | Per-tech `Tank.DamageEvent` additivity | **[RESOLVED v0.2.1]** — additive; multiple subscribers coexist (already confirmed in code with TankAIHelper + EnemyMind both subscribing). See §7.1. |

[INFORMATIVE] OQ-11 (Modified default-fallback) and OQ-14, OQ-15 (MP determinism, attract behavior) from v0.1.0 have moved fully to [FORM-SPECIFICATION.md OD-9](FORM-SPECIFICATION.md#section-7-open-decisions) and [ARCHITECTURE.md §7](ARCHITECTURE.md#section-7-open-decisions-owned-here) respectively; they are no longer tracked here as shell-surface questions.

---

## SECTION 13: CROSS-REFERENCE INDEX

Files this guide references in `TAC_AI/`:

- [AI/Forms/IAIForm.cs](../../IAIForm.cs)
- [AI/Forms/AIFormRegistry.cs](../../AIFormRegistry.cs)
- [AI/Forms/Vanilla/VanillaForm.cs](../../Vanilla/VanillaForm.cs)
- [AI/Forms/Vanilla/VanillaMovementController.cs](../../Vanilla/VanillaMovementController.cs)
- [AI/TankAIHelper.cs](../../../TankAIHelper.cs)
- [AI/TankAIHelper.Controller.cs](../../../TankAIHelper.Controller.cs)
- [AI/TankAIManager.cs](../../../TankAIManager.cs)
- [AI/AIEnums.cs](../../../../AIEnums.cs)
- [AI/Engine/IAIContext.cs](../../../Engine/IAIContext.cs)
- [AI/Engine/AIModuleBootstrap.cs](../../../Engine/AIModuleBootstrap.cs)
- [AI/Engine/ProfileRunner.cs](../../../Engine/ProfileRunner.cs)
- [AI/Tunables/TunableOptionsPublisher.cs](../../../Tunables/TunableOptionsPublisher.cs)
- [KickStart.cs](../../../../KickStart.cs)
- [KickStartTAC_AI.cs](../../../../KickStartTAC_AI.cs)

This document is read alongside [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) and [ARCHITECTURE.md](ARCHITECTURE.md).

---

END OF SHELL-API-GUIDE.md v0.2.0
