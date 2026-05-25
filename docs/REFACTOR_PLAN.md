# TAC_AI Modularization Refactor — Complete Transition Plan

> **Status:** PLAN (pre-execution). **Execution model:** single push-through — the codebase will be NON-FUNCTIONAL
> until the final integration gate. No staging/running of intermediate states (compile-only checks are allowed and
> expected; see §8). Owner backs up the tree manually before start. File structure changes greatly.

## 0. Goal & Constraints

**Goal.** Turn the AI from large monolithic files into a modular system where:
- AI behaviors (Attack, Circle, Retreat, Charge, Strafe, Idle, Mine, Scavenge, Escort, Protect, Charge-heal, …) are
  individual files, **auto-discovered by being in the folder for their vehicle type** (Land/Sea/Air/Space/Static/Shared)
  and **selectable in-game**.
- A tank's AI is defined by **composable PROFILES** (e.g. an Attack profile + a Retreat profile + an Idle profile).
- Behaviors can be **swapped in and out at runtime**; the engine is **hardened** against it.
- Every hardcoded tuning value (distance, timing, noise, …) becomes a **tunable** via a central registry, surfaced
  in-game and adjustable live where safe.

**Hard constraints.**
- **Behavior-preservation:** current behavior is PRESERVED, only SEGMENTED. No functionality lost.
- **No dead code:** every current behavior/value maps to a target home; the old monolith blocks are deleted at the end.
- **Push-through:** the tree need not run between steps. Compile-only checkpoints catch signature drift; the single
  run/verify is at the very end (§8).

**Facts that shape every decision below:**
1. **The shared-state "bus" is enormous and bidirectional.** `lastEnemyGet` is read in ~40 files; `ThrottleState`/
   `DriveVar`/`lastEnemy`/`Navi3DDirect`/`AutoSpacing`/`theResource` are *written* across ~24-34 files including the
   movement cores, World managers, the network layer, and the save module. The modular layers route this through an
   explicit interface, not extract it away.
2. **`EnemyMind` is a typed façade over `TankAIHelper`, not separable state.** `mind.Min/MaxCombatRange`/`LikelyMelee`/
   `CommanderAttack` proxy `AIControl.AISetSettings.*`/`AttackMode`; `mind.EvilCommander`'s setter triggers a controller
   swap via `SetDriverType`. The mind is part of the helper bus.
3. **Two independent decomposition axes.** *Alignment* (ally vs enemy) and *locomotion* (Land/Sea/Air/Space/Static).
   **The alignment axis is fully unified — ally and enemy run the SAME behavior modules** (the only difference is who
   selects them and a few alignment parameters). **The locomotion axis stays separate** — per-loco strategies, with each
   locomotion's existing fixes localized to its own module.

## 1. Target Architecture (layers)

```
                +-------------------------------------------------------------+
   per-frame    |  TankAIManager  (scheduler: Pre→Directors→Operations→Post)  |   << stays (engine)
   schedule     +-------------------------------------------------------------+
                                  | drives each tech's tick
                                  v
   per-tech      +------------------------------------------------------------+
   component     |  TankAIHelper  (SLIMMED: identity + shared-state bus +      |   << stays, shrunk
                 |  tick entry points + tank.control SINKS)                    |
                 +------------------------------------------------------------+
                    |  exposes IAIContext = a FULL read/WRITE bus facade
                    v
   ENGINE SERVICES (extracted from TankAIHelper; STATEFUL + ORDER-DEPENDENT):
     PhysicsInfo → Targeting → (behaviors) → Avoidance · Unjam · WeaponFire · Anchoring · DutyCycle ·
     ControllerManagement · StatusText
                    |
   BEHAVIORS (swappable, folder-discovered per vehicle type; SHARED across ally/enemy), by CATEGORY:
     Attack/Engage · Retreat/Safety · Circle/Strafe · Idle/Patrol · Economy(Mine/Scavenge) ·
     Support(Escort/Protect/Charge/MultiTech)
     -> alignment is a PARAMETER, not a separate code path; allies and enemies use the same modules
                    |
   STRATEGIES (consumed by the cores), one IMPLEMENTATION PER LOCOMOTION (formulas differ per loco):
     Steering · Throttle · Standoff · Pathing
                    |
   MOVEMENT CORES (per-locomotion frame loop; thin): Land · Sea · Space · Airplane · Helicopter · Vtol · Static

   CROSS-CUTTING (foundation):
     Tunable Registry (declare-once → in-game UI + persist + typed handle + classified apply; per-knob show/rename)
     Module Loader (boot-time type scan by folder/attribute → register behaviors+profiles → publish)
     Profile System (per-tank composition; allies player-select, enemies auto-select; saved; MP-synced)
```

**The drive-intent bus is the universal module I/O contract.** Behaviors produce an `EControlOperatorSet` the engine
consumes as `EControlCoreSet` (the pair already exists in `AIEnums.cs`). The operator is a **struct passed by `ref`** and
its verb methods (`Forwards`/`Reverse`/`STOP`) also write `helper.DriveVar`/`ThrottleState`, so `IAIContext` writes the
struct AND the throttle fields atomically and preserves by-ref mutation. **The `EDriveDest`/`EDriveFacing`/
`EDrivePathing`/`EnemySmarts`/`EnemyStanding` enums and the `[Flags]` `AIToggleFlags` are ordinally compared and/or
serialized to save — their member order is load-bearing and must never change.**

## 2. New File / Folder Structure (target)

```
TAC_AI/
  AI/
    Engine/
      IAIContext.cs              # the FULL read/write bus facade behaviors/strategies receive
      AIContext.cs               # impl backed by TankAIHelper (+ the EnemyMind façade)
      Services/                  # extracted from TankAIHelper; tick in a fixed order (§3.4)
        PhysicsInfoService.cs TargetingService.cs AvoidanceService.cs UnjamService.cs
        WeaponFireService.cs AnchorService.cs DutyCycleService.cs ControllerManager.cs StatusTextService.cs
      Registry/
        AIModuleRegistry.cs      # discovered behaviors/profiles keyed by vehicle type + category
        AIModuleLoader.cs        # boot-time type scan (ReflectionTypeLoadException-safe) + publish
      Profiles/
        AIProfile.cs  ProfileStore.cs (save+MP sync)  AllyProfileSelectionUI.cs  EnemyProfileAutoSelect.cs
    Tunables/
      Tunable.cs                 # descriptor: name, category, type, default, min/max/step, applyMode, apply, menuVisible, menuLabel
      TunableRegistry.cs         # registration → BindConfig(backing static field) + NativeOptions + typed handle
      Catalog/                   # the declarations ("the files that begin the process")
        CombatTunables.cs  MovementTunables.cs  TimingTunables.cs  SpawnTunables.cs ...
      LiveTuningOverlay.cs       # optional in-world mid-game tuning GUI
    Behaviors/                   # ONE set, shared by ally + enemy (alignment is a parameter)
      Shared/   { Attack/  Retreat/  Idle/  Economy/  Support/ }   # loco-agnostic behaviors
      Land/  Sea/  Air/  Space/  Static/   { Attack/  Retreat/  Idle/  ... }
    Strategies/
      Steering/  Throttle/  Standoff/  Pathing/   # one implementation per locomotion under each
    Movement/
      Cores/ { LandAICore SeaAICore SpaceAICore AirplaneAICore HelicopterAICore VtolAICore StaticAICore }
      Controllers/ { AIControllerDefault Air Static, MovementControllerBase, MovementDispatch }
      VehiclePathing/ { VehicleUtils(split) AIEPathing AIEPathMapper AIEAutoPather* }
    TankAIHelper.cs              # slimmed: state bus + tick entry + sinks
    ModuleAIExtension.cs         # extended: persists the per-tank profile selection
  (World/, Templates/, PatchBatch/ stay; Enemy/EnemyOperations + AI/AlliedOperations dissolve into AI/Behaviors/*)
```

## 3. Core Contracts

### 3.1 `IAIContext` — a full read/WRITE bus facade
Behaviors and strategies receive `IAIContext`, never raw `TankAIHelper`. Because the real coupling is read- AND
write-heavy and spans the cores, the network layer, and the save module, **the complete surface is derived empirically
before it is frozen** — a mechanical grep of `helper\.\w+\s*=`, `mind\.\w+\s*=`, `AIControl\.\w+\s*=`, and
`helper\.\w+\(` across `Behaviors`/`*Operations`/`BGeneral`/`RGeneral`/`AICores`, completed BEFORE Step 4. The surface
includes at least:

- **Reads:** `Tank`, range getters (`Min/Max/ObjectiveRange`), `TickAIAlign`, `DriverType`, physics (`SafeVelocity`,
  `recentSpeed`, `recentSpeedSigned`, `EstTopSped`, `MakingNetProgress`, `lastOperatorRange`, `lastCombatRange`),
  targeting (`lastEnemyGet`, `InRangeOfTarget`, `BlockedLineOfSight`), combat posture (`WasRetreatingInCombat`,
  `TurretFraction`, `CombatWantsCircleNow()`), and tunables (typed handles — §4).
- **Writable state (intent setters / struct mutators):** `ThrottleState`, `DriveVar`, `AutoSpacing`, `Retreat`,
  `AvoidStuff`, `Attempt3DNavi`, `FullBoost`/`LightBoost`/`FirePROPS`/`ForceSetBeam`, `FIRE_ALL`, `Urgency`/
  `UrgencyOverload`, `Provoked`, `EstTopSped` (behaviors lower it to slow the clock), `CollectedTarget`, the objective
  slots (`theResource`/`theResourceNode`/`theBase`/`theGuardedAlly`/`lastBasePos`), and the 3D-nav state
  (`Navi3DDirect`/`Navi3DUp`, core↔core cross-frame, not pure intent).
  - **`AISetSettings` is a STRUCT** — expose explicit in-place setters (`SetSideToThreat`, `SetObjectiveRange`,
    `SetCombatSpacing`, `SetCombatChase`, `SetFullMelee`, …); never a by-value getter (writes to a copy are lost). The
    `EnemyMind` range/melee/attack setters wrap these same fields and are exposed through the context too.
- **Target verbs (distinct, NOT interchangeable):** `SetPursuit(force)` *acquires and SETS `KeepEnemyFocus`*;
  `EndPursuit()` drops the lock but keeps the target; `ReleaseTarget()` drops both; `SetTargetNoFocus(Visible)` is the
  raw unlocked write. Behaviors use these; the raw `lastEnemy` write stays internal for the damage path
  (`EnemyMind.OnHit`), the MP RTS-attack receive (clients), and loose-block assignment, which must NOT acquire a lock.
- **Engine-service entry points behaviors call directly:** `TryHandleObstruction(…, ref operator)`, `SettleDown`,
  `IsTechMovingAbs`, `GetDistanceFromTask`, `TryInsureAutoAnchor`/`Unanchor`, the drive verbs (`DriveToFacingTowards`,
  `DriveAwayFacingTowards`, `DriveToFacingPerp`, …), `MarkOperatorDirty`, `RequestMovementControllerSwap(reason)`.
- **Strategy-facing sinks:** `DriveControl`, `FixControlReversal`, `ProcessControl`, `SteerControl`,
  `AimAndFireWeapons` — these alone may touch `tank.control`.
- **Neighbour access:** `GetContextFor(Tank)` (mimic-fire reads a neighbour's `lastEnemyGet`).

Encapsulation is advisory (behaviors are same-assembly, so `internal` members remain reachable); the value is a
documented contract and discipline, not hard enforcement.

### 3.2 `IBehavior`
```
interface IBehavior {
    AIBehaviorCategory Category { get; }   // Attack / Retreat / Idle / Economy / Support (the existing divisions)
    VehicleClass VehicleClass { get; }     // Land/Sea/Air/Space/Static/Shared (from folder/attribute)
    string Id { get; }                     // stable name (for selection + save)
    void Tick(IAIContext ctx);             // produce drive intent; alignment read from ctx, not a separate class
    void OnInstall(IAIContext ctx);        // idempotent install hook
    void OnRemove(IAIContext ctx);         // teardown via BehaviorTeardownState (§7)
}
```
A behavior is the extracted body of one handler-case/`TryAdjustForCombat` block. **It is alignment-agnostic** — the
ally and enemy versions of a behavior are the *same* module; where they genuinely differed (e.g. ally-vs-enemy target
refresh) that difference becomes a parameter read from `IAIContext`. It touches only `IAIContext` and owns its tunables.

### 3.3 The per-locomotion movement strategy IS `IMovementAICore`; large cores split into feature-complete components
**Decision 7 (refined after core discovery):** the per-loco movement strategy already exists as `IMovementAICore` +
the concrete cores (`LandAICore`/`SeaAICore`/`SpaceAICore`/`StaticAICore`/`AirplaneAICore`/`HelicopterAICore`/
`VtolAICore`), which are already separate files and already runtime-swappable (selected via `MovementDispatch`/
`SwapMovementController`). Adding fine-grained `ISteeringStrategy`/`IThrottleMode`/`IStandoffProfile`/`IPathingMode`
objects would be redundant with that seam AND over-fragment cohesive frame-loop math, so **those speculative interfaces
are removed**. Step 3 instead: (a) split the genuinely-large `AirplaneAICore` (~1631 lines) into feature-complete
partial components (Dive-FSM / U-turn / Steering / Throttle / Combat), each a cohesive unit swappable with new logic;
(b) de-duplicate the byte-identical shared code (`DriveMaintainerEmergLand` across Air/Heli; the Navi3D steering
preamble + broadside cross-product across Sea/Space/Static) into shared helpers; (c) leave the moderate cores
(492–817 lines) cohesive as-is — their combat fixes are already localized + documented there.

The per-vehicle differences are in formula SHAPE, not just constants (Land's `Yield` is direction-aware on
`recentSpeedSigned` using `YieldSpeed` while Sea's is magnitude-only; Land's standoff has the bounds-aware dead-band +
FACE-ENEMY `±destDirect` flip while Sea/Space use a simpler `driveDyna/3` clamp + cross-product broadside) — which is
exactly why each core stays its own implementation rather than collapsing to one parameterized formula.

**Existing fixes stay with the locomotion they were made for, localized into that loco's strategy module, and are NOT
ported across locomotions** — Land's bounds-aware standoff dead-band + FACE-ENEMY reverse + the obstacle-avoidance and
duty-cycle work live in the Land strategy/behavior modules; Sea/Space/Air keep their current combat math. As each fix
moves into its module it carries **ample in-code notes** describing the behavior it produces, the symptom it addressed,
and why the per-loco difference exists, so a future reader understands each module standalone. The refactor relocates
and de-duplicates only the shared *preamble*, preserves every per-vehicle tunable value, and changes no behavior. All
strategy constants become tunables (§4).

### 3.4 Engine services — stateful and order-dependent
The services are NOT stateless; their tick order is a contract: **PhysicsInfo first** (everyone reads its `recentSpeed`/
`MakingNetProgress`/`EstTopSped`) → **WeaponFire LOS sync** (sets `WeaponAimType`) → **Targeting** (reads it) →
**behaviors** (set `WasRetreatingInCombat`/`AutoSpacing`/posture) → **Avoidance / Unjam / Maintainer** (consume them).
Preserve: `posWeights` as deliberately non-reentrant `static` scratch (co-located with AvoidanceService, single-thread
only); per-tech `combatCyclePhase01` on DutyCycle (don't re-sync neighbours); per-instance `_damageBuckets`; and the
`static FieldInfo` reflection caches' type-init timing/null-guards. `TryHandleObstruction` is invoked BY behaviors with
the operator by `ref` (and must `MarkOperatorDirty` after) — Unjam is both a service and behavior-callable.

## 4. Tunable Registry, Loader, In-game UI

### 4.1 Declare-once tunables — surface ALL, with per-knob show/rename switches
Every tunable is registered (all ~450 surfaced by default); the descriptor carries simple **code switches** so the
in-game menu can be curated without removing the tunable or its persistence:
```
Tune.Register("Combat.ReverseInnerFraction", category:"Combat", default:0.35f, min:0, max:1, step:0.05f,
              applyMode: ApplyMode.LiveSafe, display: v => v.ToString("0.00"),
              menuVisible: true, menuLabel: "Reverse stand-off fraction");
```
- **`menuVisible`** (one-line flip) hides a knob from the in-game menu while keeping it code-readable and persisted;
  **`menuLabel`** renames its in-game label. Both are pure code switches — curating the menu never changes a value, its
  config key, or its readers.
- **The registry OWNS the backing static field** and binds persistence (`ModHelper.BindConfig`) against that field
  (BindConfig reflects on a settable static field, not a property). Reads use an **O(1) typed handle**
  (`Tune.Float("…")` resolving a cached handle), never a per-frame string-keyed dictionary lookup.
- **Migrating `AIGlobals` constants:** `public const` values cannot become registry reads — `const` is compile-time-
  inlined and const-cascades (e.g. `UnjamUpdateDrop = UnjamUpdateStart + UnjamUpdateTicks`, `*Sqr = x*x`,
  `DamageAlertDecayPerSec`) require compile-time constants. Each tunable `const` is physically converted to a
  `public static` registry-backed field, and any const derived from it is re-expressed as runtime computation.
  Constants used only in const-expressions, attributes, `case` labels, or default parameters stay `const` and are not
  exposed. Old names are aliases only where the member was already `static`.
- **Every descriptor carries a classified `apply`:** `ApplyMode.BootOnly` (cadence/integer-truncation-sensitive values
  like `AIClockPeriod`; applied only on reload), `LiveSafe` (read-fresh / push to subscribers), or
  `WorldAffectingHostOnly` (mutates vanilla `Globals`/`ManPop`/`ManTechs` or re-inits a subsystem — host-only). The
  apply fires **only when the value actually changed** (per-descriptor dirty-tracking), never blanket-wired to the
  global save event. Side-effecting applies (rebuild-all-enemy-AIs, `RefreshDelays`, `OverrideEnemyMax`, RTS re-init,
  block-drop/pop mutation) are first-class callbacks. Any tunable that mutates vanilla state registers a symmetric
  DeInit restore.
- **`*InMP` pairs are first-class** (`AISelfRepair`/`…InMP`; `AIClockPeriod` is MP-fixed to `NetAIClockPeriod`) so the
  UI never silently no-ops in multiplayer. `AIClockPeriod`'s dual role (scheduler cadence AND per-tick logical timestep)
  is split or documented.
- **Shared-scope tunables are tagged with ALL their read-sites** (the no-orphan checklist maps readers) so the UI can
  mark a knob global vs per-profile-overridable honestly.

### 4.2 Boot hook (one entry point, all init paths)
Three init sequences exist — `MainOfficialInit()`, `MainOfficialInitIterate()` (Steam coroutine), and the TTMM `Main()`
(whose non-Steam-managed branch publishes options directly and never calls `InitSettings`). Centralize the registry
build + behavior/profile scan + option publish into one `InitAIModules()` called from all three, placed after
`RawTechExporter.Initiate()` and before/within the options publish, inside the existing `EncapsulateSafeInit`/
`ENCAPSULATEError*` guard, idempotent across DeInit→reInit, and torn down (options cleared, registrations dropped,
vanilla mutations restored) in `DeInitALL`.

### 4.3 Module loader / folder discovery
Behaviors are compiled C# classes; "loaded by being in the folder for its vehicle type" = a boot-time type scan that
registers every `IBehavior`/`AIProfile` type keyed by its folder/namespace (vehicle class) + a category attribute — no
central switch. The scan tolerates partial type-load (`try { GetTypes() } catch (ReflectionTypeLoadException e) { use
e.Types.Where(t => t != null) }`) because optional dependencies (WeaponAimMod/TweakTech/Control_Block/WaterMod) are
soft-referenced. Type discovery runs pre-`PatchMod`; any behavior `Initiate` touching a Harmony-patched API runs
post-`PatchMod`. (A future extension can hot-load external behavior DLLs from a runtime folder, modeled on the
`RawTechExporter` directory+watcher pattern; not the initial mechanism.)

### 4.4 Live tuning
NativeOptions applies on menu-save; an optional in-world overlay (modeled on `InitSharedMenu`) writes `LiveSafe`
registry values directly for mid-battle iteration, inheriting persistence via the existing `onOptionsSaved →
WriteConfigJsonFile` hook.

## 5. Profile System (per-tank, saved, MP)

A **Profile** composes, per tank: one behavior per category and a movement core, plus optional tunable overrides. **The
categories mirror the functional divisions the code already has** (combat attack-mode, idle/patrol, economy, support
roles) — preserved as-is, not a new taxonomy. The dispatch reproduces the real shape of today's controllers, which is
more than "one behavior in order": each tick runs a **no-target pre-pass** (`DispatchNoTargetIdle`), an **aim pass** in
parallel with the movement behavior (`AidDefend`/`SelfDefend`/`ShootToDestroy`/`MimicDefend`, setting `WantsToFight`/
`FIRE_ALL`), the **movement behavior** (by `ref operator`), and a **retreat post-pass overlay** (`if Retreat →
GetRetreatLocation`). Model the profile as `{ pre-pass, aim-pass, movement-behavior, post-pass }` slots. This replaces
three hardcoded tables: `AlliedOperationsController` (`DediAI × DriverType`), `EnemyOperationsController`
(`EvilCommander`), and `ModuleAIExtension.AlterExisting` (per-AI-block presets) — **and because ally and enemy share the
same behavior modules (§3.2), both controllers collapse onto one profile runner; alignment is just which selector feeds
it.**

- **Selection differs by alignment:**
  - **Allies are player-selectable, exactly as today** — the in-game per-tank picker (today's `GUIAIManager` AI-type
    selection) is extended to choose profiles, so players keep their options. Allied selection stays **clamped to
    block-granted capabilities** via `AISettingsSet.Sync` (allied-only, as today; re-clamped on block attach/detach,
    which already force `dirtyAI`).
  - **Enemies are auto-selected only** — there is no enemy player-selection UI (a human isn't deciding enemy AI). The
    enemy profile is assigned automatically at spawn, preserving today's derivation (`RWeapSetup.GetAttackStrat` →
    `EAttackMode`/`TurretFraction`; `RCore` role/`EvilCommander` setup). Enemy profiles are **unclamped** (no
    block-capability limit), as today.
- **Movement core is coupled to `DriverType`.** A profile that implies a different core routes through `SetDriverType`/
  `RequestMovementControllerSwap` (deferred), keeping `DriverType` (read at many combat/aim/anchor sites) in sync; a
  behavior never `AddComponent`s a core mid-tick.
- **Save/load:** persist the selection as new `[SSaveField]`(s) on `ModuleAIExtension`. `LoadToTech` currently sets
  `dirtyAI=Dirty` only when `DediAI`/`DriverType` changed — add a **`profileChanged` term** so a same-role profile
  change still triggers the rebuild that installs it. Apply the profile *before* the rebuild, sequenced relative to both
  `LoadToTech`'s own dirty-set and the load-time block-attach dirty events; snapshot it in `OnSerializeSnapshot`. For
  unloaded enemy techs, ride the selection on `NP_TechUnit` if enemy profiles must survive unload.
- **Default profiles reproduce today's behavior** per `DediAI`/`EvilCommander`, so behavior is preserved out of the box.
- **MP:** a new `TTMsgType` (pattern of `AITypeChangeMessage`) syncs an *allied* profile choice; it explicitly
  replicates the `sender.CurTech?.Team == tank.Team` anti-spoof guard, applies locally only after broadcast, and keeps
  planning host-authoritative. (Enemy profiles are host-assigned, so no client→host enemy selection exists.) The
  pre-existing `HostExists`-never-reset re-host gap is a prerequisite fix if allied profile-sync must survive
  host→menu→host in one session.

## 6. Decomposition Map — every current piece → target home (no-orphan accounting)

A living checklist lists every behavior in the discovery catalog with its target module + a DONE flag, plus a
per-behavior **input/output diff** against the original handler (the checklist catches *missing* behaviors; the I/O diff
catches *mis-wired* ones). Mapping rules:
- **Core drive verbs** (`EControlOperatorSet` mutators) → the shared vocabulary used by all behaviors (kept; surfaced
  via `IAIContext` since they also write `helper.DriveVar`/`ThrottleState`).
- **Ally and enemy AI become the SAME behavior modules.** Each conceptual behavior is ONE shared module; the only
  per-alignment difference is a parameter (ally-vs-enemy target refresh, the selection front-end). The twins collapse:
  RMiner≡BProspector (mine), RScavenger≡BScrapper (scavenge), BAegis≡RGuardian (protect), the four escort variants ≡ one
  escort module, the enemy combat buckets ≡ the allied combat path. The two `*OperationsController` switches collapse
  onto the single profile runner (§5).
- **`RGeneral`/`BGeneral` loco-agnostic helpers** (Engadge, LollyGag, Scurry, Mark*, CanRetreat, DefaultIdle/HomingIdle,
  fire-intent helpers, ResetValues, DispatchNoTargetIdle) → `Behaviors/Shared/`.
- **The cores' `TryAdjustForCombat(Enemy)` blocks + `VehicleUtils.Turner`/throttle/standoff** → per-loco Strategy
  implementations (§3.3). The locomotion axis stays separate even though the alignment axis unifies.
- **`TankAIHelper` regions** → the engine services (§3.4); identity + state bus + sinks + tick entry stay.
- **Existing behavior fixes travel with their code** into the specific module/strategy they belong to and are
  documented in-place with ample notes (§3.3, §7) — they are localized, never globally re-applied or ported to another
  locomotion.
- **Every magic number** (band factors, ranges, timers/ActionPause, steering-angle consts, RANDRange, energy/damage
  gates, escort Urgency ladders, economy distance bands, retreat-posture thresholds, anchor teleport consts) → a Tunable
  in `Tunables/Catalog/`, each tagged with its read-sites.
- **Exit criterion / dead-code elimination:** once a handler/switch/inline block is fully represented by modules, the
  original is DELETED. The refactor is "done" when the two `*OperationsController` switches, the per-mode handler bodies,
  the duplicated core logic, and the now-orphaned consts are gone — replaced by registry-driven profile composition.

## 7. Hardening Contract — runtime behavior install/remove

- **Install/teardown only via the dirty-flag path, never live mid-tick.** Set `dirtyAI`/`RequestMovementControllerSwap`;
  install/teardown happens in `OnPreUpdate → CheckRebuildAlignment` (the un-staggered Pre phase). **Controller-swap
  consumption is on the STAGGERED Directors phase**, so a requested swap lands on the tech's next Directors slot —
  possibly several frames later under high population. The contract tolerates this latency (or moves swap consumption to
  the Pre phase).
- **Tick order is fixed:** Pre(all) → Directors(staggered) → Operations(staggered) → Post(all). Behaviors plan in the
  Operations cadence (~1/`AIClockPeriod` of techs per frame), drive-execute in the Maintainer (~every frame).
- **Alignment latch:** read `TickAIAlign`, never live `AIAlign`; a profile swap defers its effect to the next
  `OnPreUpdate`. **Operator staleness:** any behavior writing drive intent keeps `ControlOperator` fresh each Operations
  pass and calls `MarkOperatorDirty` on out-of-band writes.
- **`BehaviorTeardownState()`** — a narrow reset (release target, SettleDown, drop carried block, zero MT offsets, clear
  operator→Default + MarkOperatorDirty, request default controller, CancelInvoke pending, reset combat/anchor/aim/posture
  latches). Distinct from `ResetOnSwitchAlignments`, which tears down whole *components* and is not a per-behavior op.
- **Null-safety = skip, never throw.** A behavior tolerates being ticked before its dependencies exist; every tick
  method catches and logs per-tech. `enabled=false` (not list-removal) is the unload lever for a whole helper.
- **MP:** allied selection is player-driven + broadcast + host-authoritative; enemy selection is host-assigned. Clients
  run the *client* Director/Ops arms plus the Maintainer and may write `lastEnemy`/RTS bus state, so behavior writes
  that mutate authoritative WORLD state are host-gated.

## 8. Migration Work Order (push-through; compile-checked)

A **compile-only `MSBuild`** runs after each step to catch signature/interface drift early (this does not stage or run
the mod). The single functional run/verify is at Step 8.

1. **Foundation (new files):** `Tunable`/`TunableRegistry` + `Catalog/` (start with combat knobs), `IAIContext`/
   `AIContext` (+ the `EnemyMind` façade), `IBehavior` + strategy interfaces, `AIModuleRegistry`/`AIModuleLoader`,
   `AIProfile`/`ProfileStore`. **Freeze the complete `IAIContext` surface here** from the §3.1 grep — before any consumer
   migrates.
2. **Slim TankAIHelper (partial-class split, Decision 6):** make `TankAIHelper` a `partial class` and move each service
   area's methods (+ its area-exclusive private fields/statics) into its own `TankAIHelper.<Area>.cs` file, in dependency
   order (StatusText first — trivial; then PhysicsInfo, WeaponFire LOS, Targeting, Avoidance, Unjam, Anchoring, DutyCycle,
   ControllerManager). Pure code-move, no logic edits → zero behavior change. Core `TankAIHelper.cs` keeps identity +
   shared-state bus + tick entry + `tank.control` sinks. *Compile-check after each area.*
3. **Extract strategies** (Steering/Throttle/Standoff/Pathing) as per-loco implementations, each loco's fixes localized +
   documented (§3.3); cores reduced to the per-locomotion frame loop + strategy selection; centralize the shared
   Maintainer preamble. *Compile-check.*
4. **Extract behaviors** into `Behaviors/<class>/<category>/` modules implementing `IBehavior`, **unifying ally/enemy
   twins into one alignment-parameterized module each**; register via the loader. *Compile-check.*
5. **Profile system:** replace the two OperationsController switches + `MovementDispatch`/`AlterExisting` presets with
   the single profile runner (pre/aim/movement/post slots); wire allied player-selection (extending today's picker, with
   the clamp) and enemy auto-selection; wire `ModuleAIExtension` save/load (+ the `profileChanged` rebuild trigger) and
   the allied MP RPC; default profiles reproduce today's behavior. *Compile-check.*
6. **Tunables migration:** convert remaining `AIGlobals`/inline magic numbers to registered tunables (const→static with
   de-cascading per §4.1), classified applies, MP pairs, `menuVisible`/`menuLabel` set per knob. *Compile-check.*
7. **In-game UI:** auto-generate options from the registry (honoring `menuVisible`/`menuLabel`, collapsing the
   hand-wired controls) + the allied profile picker + the optional live overlay; wire `InitAIModules()` into all three
   init paths + DeInit symmetry. *Compile-check.*
8. **Integration & dead-code elimination:** delete the replaced switches/handlers/inline blocks; reconcile against the
   no-orphan checklist + per-behavior I/O diffs; THEN the single build → multi-agent review → behavior-preservation
   verify (default profiles match prior `DediAI`/`EvilCommander` behavior) → deploy → push.

## 9. Risks & Invariants

- **The shared-state bus IS the `IAIContext` interface** — derived empirically and frozen before Step 4; never
  extracted away. The central coupling.
- **Two unification axes, handled differently:** the **alignment** axis (ally/enemy) is fully unified into shared
  behavior modules; the **locomotion** axis stays as separate per-loco strategies, with each loco's existing fixes
  localized to its module and documented — not ported across locomotions. Don't conflate them.
- **Enum order is load-bearing** for `EDriveDest`/`EDriveFacing`/`EDrivePathing`/`EnemySmarts`/`EnemyStanding` and the
  `[Flags]` save enum — never reorder; the registry never exposes them as reorderable dropdowns.
- **`const` migration is structural** (physical `const`→`static` + de-cascading; const-only values excluded); the first
  compile after Step 6 is the proof.
- **Three init paths + DeInit symmetry** via the single `InitAIModules()`.
- **MP host-authority + allied-only capability clamp + save-apply-before-dirty-rebuild** are preserved.
- **Silent-regression risk:** the codebase's pervasive `try/catch`-log-and-continue means a compiling-but-mis-wired
  extraction degrades AI invisibly — hence the per-behavior I/O diff at Step 8 and the compile-checks throughout.
- **Performance:** resolve a tank's behavior set ONCE at profile-install (cache concrete `IBehavior` refs on the
  helper); tunable reads on the Maintainer / `AvoidAssist` inner loop are O(1) typed-handle reads.

## 10. Locked Decisions

1. **Profile categories — keep what exists.** Categories mirror the code's current functional divisions; no new taxonomy.
2. **Ally and enemy AI unify where they genuinely match — PARTIAL, per discovery (refined).** Both controllers
   collapse onto one profile runner, but twin unification is selective: **Economy** (RMiner≡BProspector mine,
   RScavenger≡BScrapper scavenge) and **Protect** (RGuardian≡BAegis) merge into one alignment-parameterized module each;
   **Combat** (enemy attack-archetypes via CommanderAttack vs allied escort + ShootToDestroy) and **Idle** (enemy
   attitude-driven re-scan vs allied none) are GENUINELY different and stay as per-alignment modules — forcing them to
   merge would rewrite tuned combat AI. Alignment is a parameter where the logic matches; a separate module where it
   does not.
3. **Surface all tunables, with code-level show/rename switches.** Every knob is registered; per-tunable `menuVisible`
   (hide from the in-game menu) and `menuLabel` (rename) are one-line code switches that don't affect the value,
   persistence, or readers.
4. **Allies player-select (as today, clamped to block capabilities); enemies auto-select only** (no human decides enemy
   AI). Enemy profiles are assigned at spawn from loadout, unclamped.
5. **Keep existing fixes; localize each into the module it now belongs to and document its behavior amply.** Fixes are
   relocated with their code, not ported across locomotions; each carries thorough in-code notes describing the behavior
   and the issue it addresses.
8. **Step 4/5 = wrap + selective-merge + runner (owner choice).** Each existing operation handler (`RWheeled`/
   `BEscort`/`RMiner`/…) becomes a REGISTERED `IBehavior` whose `Tick` invokes the existing logic through the context
   (logic preserved, not rewritten) — so behaviors are swappable + profile-composable with minimal regression risk. Only
   the clean twins merge (Decision 2). The profile runner replaces the two `*OperationsController` SWITCH bodies: the
   per-tick flow becomes GetDirectedControl → tick the profile's {idle pre-pass, aim-pass, movement, retreat post-pass}
   behaviors → SetDirectedControl, exactly mirroring `Execute()`. Enemy profile auto-assigned from `EvilCommander`
   (+attitude); allied from `DediAI`×`DriverType`. The wrapped `R*`/`B*` methods stay (called by the wrappers, not dead);
   only the switch dispatch + `AlterExisting` presets are replaced. Behavior modules are grouped into cohesive
   per-class/category files (multiple `IBehavior` classes per file is fine — the loader keys on TYPE, so grouping does
   not reduce swappability), honoring the no-over-fragmentation rule.

7. **The per-loco movement strategy is `IMovementAICore` itself; large cores split into feature-complete components.**
   The cores are already separate, already runtime-swappable per-loco implementations, so the speculative fine-grained
   `ISteeringStrategy`/`IThrottleMode`/`IStandoffProfile`/`IPathingMode` interfaces are REMOVED (redundant +
   over-fragmenting). Step 3 = split only the genuinely-large core (`AirplaneAICore`) into cohesive feature-complete
   partial files (Dive/U-turn/Steering/Throttle/Combat) + de-dup the byte-identical shared code; moderate cores stay
   whole. Grouping rule (owner): components must be feature-complete + localized + swappable — not mechanical slices.

6. **Step 2 engine services are a partial-class split, not separate objects.** `TankAIHelper` becomes a `partial class`
   spread across one file per service area (`TankAIHelper.Physics.cs`, `.Targeting.cs`, `.Avoidance.cs`, `.Unjam.cs`,
   `.Anchoring.cs`, `.WeaponFire.cs`, `.DutyCycle.cs`, `.Controller.cs`, `.StatusText.cs`) with the identity + shared bus
   + tick entry remaining in `TankAIHelper.cs`. Same type, same fields, same accessibility - zero call-site changes and
   zero behavior change. Engine services are NOT runtime-swapped (only behaviors are, §7), so object separation buys
   organization, not capability; the partial split delivers the segmentation with no regression surface. Area-exclusive
   private fields/statics (`posWeights`, `combatCyclePhase01`, `netProgressLastPos/NextCheck`, `low/highMaxBoundsVelo`)
   move into their area's partial file; shared bus fields stay in the core file. Behaviors still reach services through
   `IAIContext` exactly as today (`AIContext` delegates to the helper, which is now physically segmented).
