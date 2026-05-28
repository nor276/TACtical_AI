# TAC_AI Plugin-Form Refactor (v2) — True Form Isolation

> **Status:** PLAN (pre-execution, awaiting approval). **Execution model:** single push-through — non-functional
> until the final integration gate; owner backs up the tree first; compile-only checkpoints allowed. This is the
> refactor v1 *should* have been: v1 built the shell + registry + a thin dispatcher, but left the AI brain outside
> the form. v2 moves the **entire brain** into the form's folder.

## 0. Goal

A **form** is a self-contained AI brain living in its own folder under `AI/Forms/<Name>/`, containing **all** of its
logic — targeting, every locomotion's movement and pathing, combat, retreat, turret/weapon aim, idle, economy. The
rest of the mod is a **thin shell** wrapped around the active form. Forms are discovered by existing in a folder and
published as a global, in-game-selectable mode (already built in v1). Dropping in a new folder = a new brain.

**Locked decisions (from the owner):**
1. **Thin shell, pure isolation.** Each form re-owns its full brain including pathfinding and recovery. Forms share
   **no behavior code** with each other. Duplication is accepted; the practical workflow for a new *full* form is
   "copy the `Modified/` folder as a template, then modify." A *simple* form can be tiny (the shell's sensing + sinks
   make a minimal brain viable, like v1's Basic).
2. **Vanilla = true game-AI handback** (verified feasible — §7).
3. **Global active form** (mod-wide, one at a time). Switching deinits the old form and inits the new.
4. **Make the plan, then push through. No buildable state in between.**

**Hard constraints:** Modified preserves current behavior exactly (it is the relocated current brain). No world
simulation is swept into a form. No dead code at the end.

## 1. The three layers (the boundary)

### SHELL — form-agnostic, stays outside all forms
The engine plumbing and the per-tech "body" the form drives:
- **Scheduler:** `TankAIManager` (the tick loop) — calls the active form's hooks.
- **Per-tech body — `TankAIHelper` (slimmed):**
  - **Lifecycle:** Subscribe/DelayedSubscribe/Recycled/OnEnable/OnBlock*/RefreshAI/ReValidateAI, the alignment FSM
    (CheckRebuildAlignment/DispatchAlignment/Apply*Alignment), component registration.
  - **Tick entry points** (now thin — they delegate to the active form): OnPreUpdate, OnUpdateHost/ClientAIDirectors,
    OnUpdateHost/ClientAIOperations, OnPostUpdate, and `RunMovementBridge → UpdateTechControl` (the per-frame Harmony
    bridge from `ModuleTechController.ExecuteControl`).
  - **Control sinks ("hands"):** ProcessControl, SteerControl, AimAndFireWeapons, FireAllWeapons, MaxBoost/MaxProps,
    the `DriveControl` setter, SuppressFiring, DoAnchor/DoUnAnchor, SetAIControl, and the movement-controller swap
    primitive (`SwapMovementController<T>`/AddComponent/Recycle).
  - **Shared-state bus (the form↔shell contract):** the fields the sinks/sensing/save/MP read & write — `lastEnemy`/
    `lastEnemyGet`, `DriveVar`, `ThrottleState`, `Navi3DDirect`/`Navi3DUp`, `AutoSpacing`, `ControlOperator`/
    `ControlCore`, `WantsToFight`, `Retreat`, `theResource`/objective slots, `WeaponState`/`ActiveAimState`,
    `AttackMode`, `TurretFraction`, the boost/beam holds, etc. **The form writes intent here; the shell actuates it.**
  - **Sensing (reads world → bus, no decisions):** UpdatePhysicsInfo, GetSpeed, GetFrameHeight, SyncLineOfSight
    (classify Direct/Indirect), GatherTargetTechsInRange, AccumulateAndCheckThreat. StatusText (display) stays shell.
  - **Per-tech form-state bag:** new `public object FormState;` (cast by the form to its own type) — where the form
    keeps per-tech objects it owns (its movement controller, pathers, FSM state). Populated in the form's spawn hook,
    cleared on Recycle. Keeps the shell form-agnostic.
- **Per-tech enemy state facade — `EnemyMind`:** stays shell. It is per-tech *state* (EvilCommander/CommanderAttack/
  smarts/bolts), created at spawn, wired into MP/save and the controller-swap (the EvilCommander setter). Forms
  **read** it; re-deriving it per form is too invasive and offers no benefit.
- **Cross-cutting services (built in v1, stay):** `AIFormRegistry` (discovery), the in-game Form selector + tunables
  UI (`TunableOptionsPublisher`), save/load (`ModuleAIExtension`), multiplayer (`NetworkHandler`), `GUIAIManager`.
- **Harmony patches** (`PatchBatch/*`) — shell.
- **Shared world-geometry (NEW shell):** two carve-outs that world/spawning legitimately need (they are world
  FACTS, not AI pathfinding):
  - `TerrainHeightCache` — the world-tile heightmap altitude cache + tile-event subscription extracted from
    `AIEPathMapper` (`GetAltitudeLoadedOnly`/`GetHighestAltInRadius`/terrain-height queries). The form's pathfinder
    and the shell `TerrainQuery` both read it.
  - `TerrainQuery` — the terrain height/water query functions extracted from `AIEPathing` (`AboveTheSea`,
    `AboveTheSeaForcedAccurate`, `AboveHeightFromGround*`, `OffsetFromGround*`/`OffsetToSea`/`SnapOffset*`,
    `ModerateMaxAlt`, `IsUnderMaxAltPlayer`) — calls `TerrainHeightCache`.
  - **The A\* pathfinder itself is FORM-owned** (locked decision: pathfinding fully in the form). The form's
    pathfinder reads `TerrainHeightCache`; the shell never reads the form's pathfinder.
- **Tunables storage:** `AIGlobals` statics stay shell (the binding targets). See §8.

### FORM — per-folder, owns the entire brain
Everything that *decides and drives*. For `Modified/` this is the relocated current AI (§5). A form internally
organizes into subfolders as it likes (e.g. `Modified/Movement/`, `/Operations/`, `/Combat/`, `/Pathing/`).

### WORLD SIM — never inside a form
Stays as shared world systems regardless of active form: everything in `World/` (ManEnemyWorld, ManWorldRTS,
ManBaseTeams, ManEnemySiege, NP_*, UnloadedBases, TileMoveCommand, TechUnitGroup, PlayerRTSUI, SelectHalo,
TechLoaderExt), enemy spawning + base economy (`RLoadedBases` world parts, `RMission`, the world-touching parts of
`RCore`), and `Templates/` (RawTechLoader/SpecialAISpawner/etc.). World decides *what techs exist*; the form decides
*how a tech drives*.

## 2. Active-form model

One form is active mod-wide (`KickStart.AIFormSelected` → `AIFormRegistry.Active`). The shell's tick hooks dispatch
to `AIFormRegistry.Active`. Switching forms: shell calls `old.DeInitGlobal()` then `new.InitGlobal()` and re-runs each
live tech's spawn hook so the new form can build its per-tech `FormState`. Because only one form is ever live, a form
may own global singletons (e.g. the path-mapper) without cross-form conflict.

## 3. The form interface (`IAIForm` v2)

v1's `IAIForm` was `{Id, DisplayName, RunEnemy, RunAllied}` — operations only. v2 expands it to own the **full per-tech
lifecycle** plus the per-frame control bridge and global init/deinit:

```csharp
public interface IAIForm
{
    string Id { get; }
    string DisplayName { get; }

    // Global (mod-wide) lifecycle — called when this form becomes / stops being the active form.
    void InitGlobal();      // register singletons (path mapper), tunables, etc.
    void DeInitGlobal();     // tear them down

    // Per-tech lifecycle — the shell calls these from TankAIHelper's existing tick phases.
    void OnTechSpawn(IAIContext ctx);     // build FormState, attach movement controller, etc.
    void OnTechRecycle(IAIContext ctx);   // tear down FormState

    void PreUpdate(IAIContext ctx);                  // from OnPreUpdate
    void Directors(IAIContext ctx);                  // from OnUpdateHost/ClientAIDirectors (controller recalibrate/pathing)
    void Operations(IAIContext ctx, EnemyMind mind); // from OnUpdateHost/ClientAIOperations (the decision tick; mind null for allied)
    void PostUpdate(IAIContext ctx);                 // from OnPostUpdate

    // Per-FRAME control bridge — from UpdateTechControl. The form translates bus intent → sink calls
    // (movement directors/maintainers, weapon aim, beam). This is where "entirely different pathing" lives.
    void ControlFrame(IAIContext ctx, TankControl control);
}
```

The shell's `TankAIHelper` tick methods become thin forwarders to `AIFormRegistry.Active?.<hook>(...)`. A form that
wants different pathing simply implements `ControlFrame`/`Directors` differently; it is never forced into the
Default/Air/Static controller pattern.

**Bus contract:** the form reads sensing + writes intent through `IAIContext` (extended to expose the sinks +
sensing the brain needs). `IAIContext`/`AIContext` (built in v1) become the real, load-bearing API — not the bypassed
facade they were in v1.

## 4. Shell surface exposed to forms (via `IAIContext`)

Sinks: `ProcessControl`, `SteerControl`, `AimAndFireWeapons`, `FireAllWeapons`, `MaxBoost/MaxProps`, `DriveControl`,
`SuppressFiring`, `DoAnchor/DoUnAnchor`, `SetAIControl`, `SwapMovementController<T>`/AddComponent helper, the
`FormState` slot, the tank `gameObject`/`Tank`/`rbody`. Sensing/reads: physics bus (recentSpeed, DodgeSphere,
SafeVelocity, MakingNetProgress, lastTechExtents), `GatherTargetTechsInRange`, `GetFrameHeight`, `TerrainQuery.*`,
`AccumulateAndCheckThreat`, and the full bus fields (read/write). Audit the existing `IAIContext` and add the missing
sink/sensing members; remove members no real form needs.

## 5. File moves — the `Modified/` extraction

Relocate (physically, into `AI/Forms/Modified/<sub>/`) and re-namespace the per-tank brain. Used **only** by per-tank
AI (verified — no World/spawning callers), so safe to move:

- **Movement (`Modified/Movement/`):** `IMovementAIController`, `MovementControllerBase`, `MovementDispatch`,
  `AIControllerDefault`, `AIControllerAir`, `AIControllerStatic`; AICores `LandAICore`, `SeaAICore`, `SpaceAICore`,
  `StaticAICore`, `HelicopterAICore`, `VtolAICore`, `AirplaneAICore`(+.Combat/.FlightControl); utils `VehicleUtils`,
  `HelicopterUtils`, `MultiTechUtils`, `IMovementAICore`.
- **Pathing (`Modified/Pathing/`):** `AIEAutoPather`(+2D/3D), `AIEPathMapper`, and the **navigation** half of
  `AIEPathing` (ObstDodgeOffset, ObstructionAwareness, AllyList, the drive-approx helpers). The terrain-query half →
  shell `TerrainQuery`.
- **Weapon/beam (`Modified/Combat/`):** `AIEWeapons` (WeaponDirector/Maintainer), `AIEBeam` (BeamMaintainer), plus the
  weapon-aim decision parts of `TankAIHelper.WeaponFire.cs`, `EWeapSetup`, `RWeapSetup`.
- **Operations — enemy (`Modified/Operations/Enemy/`):** the `BeEvil`/`BeEvilLight`/`CommonEvilOp`/`RunEvilOperations`
  brain from `RCore` (minus world seam, §6), `RGeneral`, `RBolts`, `RRepair`, `RPathfinding`, and
  `EnemyOperations/*` (RWheeled/RNaval/RStarship/RChopper/RAircraft/RStation/RCrashMissile/RMiner/RScavenger/
  RGuardian + EnemyOperationsController) + the v1 enemy behavior wrappers.
- **Operations — allied (`Modified/Operations/Allied/`):** `BGeneral`, `AlliedOperations/*` (BEscort/BAegis/
  BProspector/BScrapper/BEnergizer/BMultiTech/BAssault/… + AlliedOperationsController) + the v1 allied wrappers.
- **Decision logic from `TankAIHelper` partials → `Modified/`:** `.Targeting` (FindEnemy/refresh/focus),
  `.Avoidance` (AvoidAssist*), `.Unjam` (obstruction FSM), `.DutyCycle` (CombatWantsCircleNow), the decision parts of
  `.Anchoring` (the Try*Anchor decisions; DoAnchor/DoUnAnchor sinks stay shell). `.Physics`, `.Controller` (swap
  primitive), `.StatusText` stay shell.
- **Dispatch (`Modified/`):** `ModifiedForm` (v2 hooks), the v1 `AIContext`/profiles/registry if still used internally.

Estimated ~18–22k lines relocate. The csproj (old-style explicit `<Compile Include>`) must list every moved/renamed
file — a mechanical but exhaustive update.

## 6. Mixed-file splits (precise seams)

- **`RCore.cs`:** keep the per-tech tick (BeEvil/BeEvilLight/CommonEvilOp/RunEvilOperations/CombatChecking/spawn-time
  GenerateEnemyAI/AutoSetIntelligence/etc.) in the form. Extract the two world-touching methods —
  `GetRetreatLocation` (reads team HQ / NP_Presence / UnloadedBases) and `CheckShouldMakeBase` (calls
  `RawTechLoader.TryStartBase`) — behind a shell **world-service** interface (`IWorldAIServices`) the form calls. This
  keeps "where do I retreat to / should I found a base" as a world query, not form-owned.
- **`AIEPathMapper.cs` (1230L):** split into shell `TerrainHeightCache` (altitude grid + `ManWorld` tile-event
  subscription + the `GetAltitude*`/`GetHighestAltInRadius*` queries) and the form pathfinder (the per-tank pather
  queue + `RegisterPather`/`StopPather`/the FixedUpdate route-calc loop), which reads `TerrainHeightCache`.
- **`AIEPathing.cs` (1032L):** split into shell `TerrainQuery` (the terrain height/water geometry funcs — calls
  `TerrainHeightCache`) + form nav helpers (`AllyList` family, `ObstDodgeOffset`, `ObstructionAwareness` family).
  Redirect the external/world callers to `TerrainQuery`: RLoadedBases:1401/1462, RawTechLoader:239/255/378/581/3028/
  3034, SpecialAISpawner:233/1069, GlobalPatches:100, CustomAttract:167. (AIECore's `AllyList` caller —
  `FetchLowestChargeAlly` — is charge/economy brain and moves into the form, so it keeps using the form nav class.)
- **A\* engine → form:** `AIEAutoPather`/`AIEAutoPather2D`/`AIEAutoPather3D` + the pather-runner half of
  `AIEPathMapper` move into `Modified/Pathing/`.
- **Additional brain files** (beyond §5's list, found during the split): `AIECore` (charge/ally helpers),
  `AIEBases`, `AIERepair`, `AIEBeam`, `AIEWeapons`, `CombatUtils`, `BGeneral`, `EWeapSetup`, `AIESplitHandler` — all
  per-tech brain; classify + move in Phase 3.
- **`RLoadedBases.cs` / `RMission.cs`:** stay world (base economy, expansion, mission setup). The little spawn-time
  per-tech setup (`SetupBaseAI`/`SetupBaseType`/`SpecificNameCases`) is invoked from the world spawn path and writes
  the tech's `EnemyMind` — treat as world-side spawn config that seeds shell `EnemyMind` (not form-owned).
- **`ManWorldRTS.cs:1747`** reads `AIControllerDefault.PathPlanned` to draw RTS paths. Since the controller moves into
  the form, expose the planned path generically: a shell read-only `IReadOnlyList<Vector3> CurrentPlannedPath` on the
  helper that the active form populates (Modified fills it from its pather; other forms may leave it empty → RTS path
  draw simply shows nothing for that form).

## 7. Vanilla form — true handback (verified feasible)

Mechanism already exists and ships for neutral techs (`HandOffToVanillaForNeutral`): `AIRunState.Default` = "use
vanilla AI", and the Harmony prefixes (`TechAI.ControlTech` / `ModuleTechController.ExecuteControl`) pass control to
the **stock** AI when RunState==Default. The one landmine: `CheckRebuildAlignment` (and `RefreshAI`) auto-promote
Default→Advanced on every alignment rebuild. Fix:
1. Add `bool ForceVanillaAI` to `TankAIHelper` (set true while the active form is Vanilla).
2. Guard the two auto-promotions: `if (RunState == Default && !ForceVanillaAI) RunState = Advanced;`
3. In `DispatchAlignment`, when `ForceVanillaAI`, apply the handoff (RunState=Default, AIAlign=Static) each rebuild.
4. `VanillaForm` hooks: `InitGlobal` flips all live helpers to the handback; per-tech spawn sets it; `ControlFrame`/
   `Operations` are no-ops (the mod doesn't drive). No Harmony changes needed.

This makes Vanilla a genuine return to TerraTech's own AI — not "mod AI off."

## 8. Tunables / save / MP / UI

- **Tunables:** `AIGlobals` statics stay shell (binding storage). The *catalog* of Modified's knobs moves into
  `Modified/` and is registered from `ModifiedForm.InitGlobal()` (so a form publishes its own tunables when active).
  The publisher UI stays shell. A new form registers its own catalog or none.
- **Save/MP/UI:** unchanged in mechanism (shell). `AIFormSelected` persistence + the Form selector + per-tank profile
  picker + MP profile sync all stay. (Per-tank *profiles* remain a Modified-internal concept; other forms may ignore
  them.)

## 9. Execution (push-through, ordered)

Code is non-functional until the final gate; build once at the end. Ordered so signatures settle early:
1. **Shell carve-out:** define `IAIForm` v2, extend `IAIContext` with sinks/sensing, add `FormState` +
   `CurrentPlannedPath` + `ForceVanillaAI` to `TankAIHelper`, add `TerrainQuery` + `IWorldAIServices`. Make the
   `TankAIHelper` tick methods thin forwarders to `AIFormRegistry.Active`.
2. **Split mixed files** (§6): RCore world seam, AIEPathing→TerrainQuery+nav, ManWorldRTS path read.
3. **Move the brain into `Modified/`** (§5), re-namespace, fix references, register every file in the csproj.
4. **Re-wire Modified to the v2 hooks:** ModifiedForm implements the full lifecycle; its `ControlFrame` reproduces
   `UpdateTechControl`'s director/maintainer sequence; its `Directors` reproduces RecalibrateMovementAIController.
5. **Vanilla** (§7) + keep/retire **Basic** (rebuild Basic as a minimal v2 form, or drop it — owner's call).
6. **Compile-clean, multi-agent equivalence review** (Modified must behave exactly as today), build, deploy, playtest.
7. **Push** to `nor276/TACtical_AI` on explicit go-ahead.

## 10. Risks & honest scope

- **Large & invasive.** ~18–22k lines relocate and re-namespace; the value is structural (true isolation), not new
  behavior. Modified must come out byte-equivalent in behavior — regression risk is real; mitigated by behavior-
  preserving moves + multi-agent equivalence review + playtest before any push.
- **Pure-isolation cost (accepted):** a new *full* form duplicates the brain (copy `Modified/` as a template). Simple
  forms stay small via the shell services. This is the owner's explicit tradeoff.
- **`EnemyMind` stays shell** (per-tech state, deeply wired). Forms read it; they don't redefine it. If a future form
  needs a different enemy-state shape, that is a later, separate seam.
- **No buildable state mid-flight** (owner's chosen model) — back up the tree before starting.
