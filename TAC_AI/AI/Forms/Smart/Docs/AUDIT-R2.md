# AUDIT-R2.md

**Source:** Ten parallel reviewers, fresh angles, all read [AUDIT.md](AUDIT.md) first to avoid duplicating round 1.
**Scope:** Same as round 1 (every file under `TAC_AI/AI/Forms/Smart/`) but through 10 different lenses.
**Convention:** Findings on the documented v0.1.0 honest-gap list (block-capture stubs, GRU BPTT, baseline binary, etc.) and round-1 findings are excluded. This is what round 1 MISSED.

---

## Executive summary

Round 2 turned up **substantially more material than round 1**, including:

### Five additional compile failures round 1 missed

1. `DoubleBuffer<KinematicState>` — readonly struct, violates `where T : class` (CS0452)
2. `DoubleBuffer<TacticalGoal>` — readonly struct, same constraint break
3. `DoubleBuffer<TargetAssignment>` — readonly struct, same break (these three each compile-break SmartRuntime + ContinuousController independently of round 1's StrategicPlan finding)
4. **`System.ValueTuple` is not in .NET 4.6.1 reference assemblies** — every `ILearnedModel.TrainOneMinibatch()` returns `(float, float, int)`. The project targets v4.6.1 with no `System.ValueTuple` reference; CS0518 across all four model files.
5. **`WeaponFireController.LearnedResidual(WeaponId)`** is called with `TechId` at line 151 — these are independent readonly structs with no implicit conversion. CS1503.

Round 1 found 2 compile failures; round 2 brings the count to **at least 7**. None can be ignored.

### Three more "system-level dead code" holes

6. **Time.timeScale = 15 is global** with no MP guard — a developer triggering self-play during a multiplayer session desyncs every client immediately.
7. **`SMART_DEV` symbol is never defined in any csproj configuration.** Every Training/ file is wrapped in `#if SMART_DEV`. The SteamDev configuration has a malformed `DefineConstants` value (`"TRACE;DEBUG;STEAM, DEV"` — that's three symbols, the third literally `"STEAM, DEV"` with comma-space). Even if a developer fixes compile failures, the entire Training subsystem ships as empty translation units.
8. **No MonoBehaviour anywhere in Smart.** `SelfPlayHarness.BeginAsync(MonoBehaviour host, ...)` is the canonical entry point. No Smart type extends MonoBehaviour. No callable entry point to run a self-play session exists.

### Cumulative consequence

Even with compile failures fixed, with `SMART_DEV` defined, with a `MonoBehaviour` driver authored, **the harness still produces zero learning** because:
- `ProjectileFired` has no producer → `WeaponEfficiency` score term is always 0
- `LoadLibrary` always returns null + logs a warning every call → library evaluation is permanent log spam + rate=0 → after 10 evaluations the plateau detector auto-halts training
- `PretrainingPipeline.Run()` calls the synchronous `TrainingMatch.Run()` whose body is `/* simulation skipped */` — 1000-iteration training loop spawns and despawns real tanks producing zero training events
- `DamageObserved` still has no producer (carried from round 1)

### Plus ~80 additional high/medium findings

Across hot-path GC pressure, exception safety, lifecycle leaks, API consistency, NaN/Infinity hazards, hidden coupling, doc-vs-code drift, and dev-tool ergonomics. Detailed below by domain.

---

## 1. Critical — Cannot ship until fixed (round-2 additions)

### 1.R2-A Three more `DoubleBuffer<readonly struct>` compile failures

| File | Buffer | Type |
|---|---|---|
| [SmartRuntime.cs:33-36, 64-67](../SmartRuntime.cs) | `KinematicBuffer` | `DoubleBuffer<KinematicState>` — readonly struct |
| [SmartRuntime.cs:35, 66](../SmartRuntime.cs) | `ExternalGoalBuffer` | `DoubleBuffer<TacticalGoal>` — readonly struct |
| [SmartRuntime.cs:36, 67](../SmartRuntime.cs) | `ExternalTargetBuffer` | `DoubleBuffer<TargetAssignment>` — readonly struct |
| [ContinuousController.cs:75, 81, 101, 108, 124](../../Control/ContinuousController.cs) | mirror of all three | cascade-break in Control |

Round 1 found this issue for `StrategicPlan` only. Same fix shape applies: introduce sealed-class wrappers (`KinematicStateBox`, `TacticalGoalBox`, `TargetAssignmentBox`), or drop the class constraint on `DoubleBuffer<T>` and use `Interlocked.Exchange` on `object`.

### 1.R2-B `System.ValueTuple` not in .NET 4.6.1 reference assemblies

[OnlineTrainer.cs:39](../../Learning/OnlineTrainer.cs) declares `(float lossBefore, float lossAfter, int batchSize) TrainOneMinibatch();`. [OnlineTrainer.cs:182](../../Learning/OnlineTrainer.cs) deconstructs via `var (_, _, batch) = _model.TrainOneMinibatch();`. All four model implementations return tuples ([ActionValueEstimator.cs:96/102/124](../../Learning/ActionValueEstimator.cs), [OpponentIntentClassifier.cs:163/168/192](../../Learning/OpponentIntentClassifier.cs), [ThreatAssessmentModel.cs:82/87/102](../../Learning/ThreatAssessmentModel.cs), [TrajectoryResidualModel.cs:86/91/106](../../Learning/TrajectoryResidualModel.cs)).

`System.ValueTuple<,,>` was first added to mscorlib in .NET Framework 4.7; on 4.6.1 it lives in the separate `System.ValueTuple.dll` (NuGet package). The .csproj targets `v4.6.1` with no `<Reference Include="System.ValueTuple" />` in [TAC_AI.csproj:58-188](../../../../TAC_AI.csproj). CS0518 `Predefined type 'System.ValueTuple\`3' is not defined or imported`.

**Fix options:** (a) bump target to v4.7.2, (b) add `System.ValueTuple` NuGet reference, (c) replace tuple returns with `public readonly struct TrainStepResult { ... }` — smallest diff, no NuGet.

### 1.R2-C `LearnedResidual` signature mismatch

[WeaponFireController.cs:312](../../Control/WeaponFireController.cs) declares `private static Vector3 LearnedResidual(WeaponId targetId, float dt)`. [WeaponFireController.cs:151](../../Control/WeaponFireController.cs) calls `LearnedResidual(target.Id, projTime)` where `target` is `BeliefState` and `BeliefState.Id` is `TechId`. CS1503: cannot convert from `TechId` to `WeaponId`.

**Fix:** change parameter to `TechId targetId` — matches LEARNING-CONTRACT §3.3 spec text and CONTROL-CONTRACT §10.3 reference.

### 1.R2-D `ManSafeSaves` integration is entirely absent + `OnWorldReset` is no-op

[SmartForm.cs:36-52, 164-169](../../SmartForm.cs)

SHELL-API-GUIDE §7.2 (OQ-7 resolved) is explicit: Smart registers its own save system from `InitGlobal`. Grep for `ManSafeSaves.RegisterSaveSystem` across all Smart files returns **zero** results. `WorldSaving`/`WorldSaved`/`WorldLoading`/`WorldLoaded` event structs are defined in EventBus.cs but have zero publishers and zero subscribers. `OnWorldReset` is documented + shipped as a no-op while SHELL-API §2.6 / WORLD §7 / PATHING §3.x make it NORMATIVE.

**Consequence:** every world save fires through `ManSafeSaves` and Smart receives no signal — the learning profile is only flushed at form-DeInit time. On world LOAD, Smart's per-tech World registry, per-team threat fields, `_lastPaths` cache, and `TerrainMap` snapshot all carry over from the previous mission.

### 1.R2-E `TechId` hardcoded to `Tank.GetInstanceID()` — process-local

[BeliefState.cs:7-18](../../World/BeliefState.cs) + [SmartRuntime.cs:60](../SmartRuntime.cs)

`Tank.GetInstanceID()` is **Unity-process-local** — the SAME replicated tank has different instance IDs on host vs each client. TerraTech's canonical cross-machine identifier is `tank.netTech.netId.Value` (verified in use at TankAIHelper.cs:620, 1450).

**Consequences in MP:**
- Damage events with `AttackerIfKnown : TechId` use local IDs — useless for any cross-machine correlation
- `TrainingMatch._instanceIdToTeam` works only because Training runs on one machine
- **Host handover:** every per-tech state (KinematicBuffer, VehicleBuffer, ExternalGoalBuffer, controller warm-start, last-paths, threat-field cache, beliefs) is keyed by the disconnected host's instance IDs. The new host's Unity assigns fresh IDs — **no migration path exists** for any of this state.

### 1.R2-F `Time.timeScale = 15` is process-global with no MP guard

[TrainingMatch.cs:36, 339-340](../../Training/TrainingMatch.cs)

`EnableHeadlessMode` sets Unity's global `Time.timeScale` to 15. **Not network-replicated.** If a player triggers self-play while their TerraTech process is also driving an MP session (host or client), every tank, projectile, and physics step runs 15× faster than the rest of the network — immediate desync, projectile-position divergence, likely outright disconnect. `#if SMART_DEV` is the only protection, and 1.R2-G shows that's currently the case anyway — but the moment SMART_DEV is enabled, this is a release-blocking MP bug.

**Fix:** `EnableHeadlessMode` must early-return when `ManNetwork.IsNetworked` is true. `DisableHeadlessMode` should restore the captured pre-set timescale, not hard-write 1f.

### 1.R2-G `WorkerLoop` silently discards work via `TryDequeue(out _)`

[WorkerPool.cs:146-150](../../Threading/WorkerPool.cs)

After the outer `TryDequeue` at line 119 fails, the idle branch SpinWaits then performs `if (!_queue.TryDequeue(out _)) Thread.Sleep(0);`. The intent is "recheck the queue" but `TryDequeue(out _)` REMOVES the item it dequeues. If an `Enqueue` lands in the small SpinWait window, the recheck dequeues it, throws it on the floor, and falls through to Sleep(0). **Work delegate never executed, never re-enqueued.** MPC requests / path requests / coordinator dispatches can be silently dropped under load.

**Fix:** remove the inner TryDequeue entirely — the next outer-loop iteration handles dequeue: `Thread.SpinWait(...); Thread.Sleep(0); continue;`.

### 1.R2-H `SMART_DEV` symbol is never defined; Training/ is dead in every build

[TAC_AI.csproj:22, 31, 39, 50](../../../../TAC_AI.csproj)

Configurations and their `DefineConstants`:
- Debug: `TRACE;DEBUG`
- Release: `TRACE`
- Steam: `TRACE;STEAM`
- SteamDev: `TRACE;DEBUG;STEAM, DEV` ← **malformed**: MSBuild splits on `;`, so the third symbol literally becomes `"STEAM, DEV"` with comma-space.

**No configuration defines `SMART_DEV`.** Every file in Training/ compiles to an empty translation unit. `SelfPlayHarness.BeginAsync` does not exist in any built assembly. Round 1's Training findings are all inert under default configurations.

**Fix:** add a `Dev` configuration with `TRACE;DEBUG;SMART_DEV`, document in WORKFLOW.md. Fix the malformed `"STEAM, DEV"` to `"STEAM;DEV"` if both were intended.

### 1.R2-I No MonoBehaviour anywhere in Smart

[SelfPlayHarness.cs:61-65](../../Training/SelfPlayHarness.cs)

`BeginAsync(MonoBehaviour host, int maxGen, CancellationToken ct)` is the canonical async entry. Grep for `: MonoBehaviour` in Smart/ returns nothing — `SmartMovementController` extends `MovementControllerBase`, `SmartForm` is `IAIForm`, the rest are plain classes. **There is no MonoBehaviour anywhere in Smart that could be passed as `host`.**

Even after compile fixes + SMART_DEV defined, there is still no way to invoke the harness. The synchronous `Run()` exists but is documented as zero-duration stalemate.

**Fix:** author a minimal `SmartTrainingDriver : MonoBehaviour` in Training/, gated on `#if SMART_DEV`, attached to a hidden DontDestroyOnLoad GameObject by `SmartForm.InitGlobal` (or by a Harmony patch). Add a parameterless `BeginAsync(int maxGen, CancellationToken)` overload that lazily creates its own host.

### 1.R2-J `SamplingMPC` Lambda → NaN-poisons entire control output

[SamplingMPC.cs:107-111](../../Control/SamplingMPC.cs)

`weights[i] = Mathf.Exp(-(costs[i] - minCost) / Lambda)`. For the minimum-cost sample `costs[i] - minCost = 0`. If `Lambda = 0` (`TrainingMatch.ApplyHyperParameters` clamps at line 260 but the static itself has no internal floor — any reset path or non-harness writer can produce 0), then `0/0 = NaN`. `Mathf.Exp(NaN) = NaN`. All weights NaN. `sumW = NaN`. `if (sumW < 1e-9f)` does not catch NaN (`NaN < 1e-9 = false`). Every output ControlVector becomes (NaN, NaN, NaN), flows to TankControl, propagates into `previousMean` — **no recovery without external reset**.

**Fix:** inside `Solve`, snapshot `float lambda = Mathf.Max(Lambda, 1e-6f);` once at solve entry.

### 1.R2-K `SmartForm.Operations`/`ControlFrame` have no try/catch — one bad tech breaks shell per-tick loop

[SmartForm.cs:122-160](../../SmartForm.cs)

`Operations` calls `state.TickMainThread(0.033f)` (BlockCapture + MassDistribution + ThrustMap + WeaponProfileBuilder + ArmorMap + MobilityProfile), then `PathingService.MainThreadTick`, then `state.Controller.OnOperationsTick` (which overlays + ComputeFireCommits). **None wrapped in try/catch.** A single NRE in BlockCapture for a tech mid-mutation, or a malformed ControlProfile, throws up into the shell. The shell typically iterates all techs in one frame; an unhandled throw **aborts the iteration and subsequent techs do not get ticked that frame.** One bad tech kills the global frame.

**Fix:** wrap each state-method call in per-tech try/catch in SmartForm; on catch log + continue. The shell's per-tech iteration must not be at the mercy of one tech's failure.

### 1.R2-L Exception inside coroutine while-loop body bypasses Unsubscribe + Teardown

[TrainingMatch.cs:111-156](../../Training/TrainingMatch.cs)

Round 1 finding 2.6 noted `host.StopCoroutine` skips cleanup. The same defect exists for an exception thrown **inside** the while loop body. `CountAliveAndHp` at line 121 iterates `_spawnedTanks` — any of these can NRE if a Tank's blockman becomes null during recycling between iterations. The throw bypasses the cleanup block at line 141-150 (which is not in try/finally — Unity coroutines can't yield inside try/finally). Subscriptions remain active, `Time.timeScale` stays at 15, spawned tanks aren't despawned. **Next match: double-fire subscribers + game at 15× timescale.**

Distinct from the StopCoroutine variant because one bad Tank is a routine hazard, not user action.

### 1.R2-M `LoadLibrary` returns null + warns every call → training auto-halts after 50 generations

[ScenarioGenerator.cs:155-158](../../Training/ScenarioGenerator.cs)

Unconditionally executes `LogWarning` then `return null`. `SelfPlayHarness.RunLibraryEvaluationCoroutine` iterates `LibraryNames` calling LoadLibrary once per name — N warnings per evaluation. Every iteration `continue`s because `scen == null`, so `total` stays at 0 and `rate = 0f` is pushed to `_libraryHistory` each evaluation. `IsLibraryPlateaued` compares `best - currentRate < 0.01f`; with both 0, that's `0 < 0.01` = true once the queue fills (after PlateauWindow=10 library evaluations). **Training auto-halts after 50 generations** (10 × 5 gen-per-library-eval), silently, not because of real plateau.

### 1.R2-N `LearningService.Shutdown` saves while trainers may be mutating `_params`

[LearningService.cs:78-91](../../Learning/LearningService.cs)

Round 1 1.4 caught the race in concept. Round 2 adds the concrete `_modifiedSinceLoad` non-atomic-read race that compounds it. `_modifiedSinceLoad` is a plain bool; OnDamageObserved sets it to true; Shutdown reads it without volatile. (1) Shutdown reads false even though OnDamageObserved set it true → save skipped → fresh weights lost. (2) After Shutdown skips save, an in-flight OnDamageObserved completes Enqueue + sets the flag → no one to save it anymore.

Round 1 fix needs to be combined with this: make `_modifiedSinceLoad` Interlocked + drain pending subscribers before reading.

---

## 2. High — Will produce wrong behavior under common conditions

### 2.R2.A Compile-cascade

- **[ContinuousController.cs:75, 81, 101, 108, 124](../../Control/ContinuousController.cs)** — round-2 DoubleBuffer<struct> failures cascade through Control; needs the same fix as 1.R2-A.

### 2.R2.B MP / host-handover / serialization

- **[SmartForm.cs:81-89](../../SmartForm.cs)** — `m_USE_AVOIDANCE = false` written on every machine including clients. Client visuals/local-prediction lose avoidance backup with no MPC computing on client. Symmetric restore on OnTechRecycle missing.
- **No subscription to `TankTeamChangedEvent`** — captured/converted techs stuck on old team in Smart's registries. SHELL-API-GUIDE §7.2 + WORLD-CONTRACT §5.2 mandate it; grep finds zero subscribers and zero publishers.
- **[LearningService.cs:32, 41-46](../../Learning/LearningService.cs)** — `_currentPlayerId` hardcoded `"local"`. Every MP session overwrites the same file. Solo profile corrupted by MP session's learning. LAN testing on one machine races on the file. No per-save-game key.
- **All SmartRuntime state in-memory only.** Host disconnect/migration loses all in-session learning + tactical state; new host's `SmartRuntime` is whatever was running on their client (empty, per round-1 1.3).
- **[SmartRuntime.cs:362-383](../SmartRuntime.cs)** — Shutdown clears `_teams` before cancelling workers; planner/coordinator can read `SmartRuntime.World` after `World.Clear` runs.
- **[TerrainMap.cs:83-94](../../Pathing/TerrainMap.cs)** — `_origin` is `readonly` after construction. Unity's float-origin shifting (`OnMoveWorldOrigin`) never updates TerrainMap. After a world recenter, every world-space query falls into the OLD origin's box.
- **[SmartForm.cs:56-99](../../SmartForm.cs)** — `OnTechSpawn` creates per-team workers on clients with no host check.

### 2.R2.C Hot-path allocations (round 1 only caught Learning's Evaluate)

The full picture of GC pressure under steady-state combat is much worse:

- **[KalmanUpdate.cs:56-57, 70, 132-178](../../World/KalmanUpdate.cs)** — 8 fresh `float[]` arrays per in-sight belief update at 30 Hz. ~310 KB/s for 10 in-sight techs.
- **[PUCTSearch.cs:22-31, 50, 75, 93](../../Planning/PUCTSearch.cs)** — each `PUCTNode` allocates 5 Dictionaries. NodeBudget=5000 × 5 = **25,000 Dictionaries per Search**, plus Lists per Iterate. **~50K+ heap allocations per planner Search**. Worst hotspot in the codebase by alloc count.
- **[WeaponFireController.cs:94, 99, 109-112, 316-321](../../Control/WeaponFireController.cs)** — 6 arrays + sorted List + closure-capturing Comparison per tech per tick.
- **[SamplingMPC.cs:60-94](../../Control/SamplingMPC.cs)** — 130+ small arrays per MPC dispatch. ~39K array allocations/s under 5-tech 30 Hz combat.
- **[ContinuousController.cs:175-184, 201-212](../../Control/ContinuousController.cs)** — fresh `MPCRequest` + `ControlProfile` + `ControlVector[]` per tech per tick. ~150 of each per second for 5 techs.
- **[StrategicRollout.cs:23-24](../../Planning/StrategicRollout.cs)** — 2 fresh team Lists per PUCT iteration × 5000 = 10,000 Lists per Search.
- **[EventBus.cs:112-132](../../World/EventBus.cs)** — `Publish` snapshots handler list via `ToArray()` **under lock** on every call.
- **[PathingService.cs:109-115](../../Pathing/PathingService.cs)** — fresh `ThreatField` wrapper + factory closure per tick.
- Plus medium: PlanLibrary.LegalActions per expansion, PhysicsRollout.Simulate per MPC sample, Hungarian solver, WorldModel.SnapshotPerTechState, PerceptionWorker.Tick, TacticalOptimizer.Step, TrajectoryOptimizer.Solve, SmartRuntime team builders, every PlanDecomposition method, RoleAssignment, ThreatField.Evaluate (no early-exit), ThreatFieldBuilder.

**Estimated steady-state under 5-tech 1-team fight: >1 MB/s** from Kalman + MPC + per-tick orchestration alone, ignoring PUCT (which adds another order of magnitude per planner tick).

### 2.R2.D Exception safety

- **[ProfilePersistence.cs:100-101](../../Learning/ProfilePersistence.cs)** — Delete-then-Move loses most-recent save on Move failure. Use `File.Replace`.
- **[PathingService.cs:187-202](../../Pathing/PathingService.cs)** — PathSolveLoop swallows Solve exceptions; requester reads stale Trajectory indefinitely.
- **[SmartRuntime.cs:346-360](../SmartRuntime.cs)** — `Init` is not exception-safe. `LearningService.Init` sets `_running = 1` before constructing models; if `new OpponentIntentClassifier()` throws OOM, `_running == 1 && Intent == null`. Subsequent Shutdown's SaveProfile NREs in ProfilePersistence.
- **[Coordinator.cs:60-78](../../Coordination/Coordinator.cs)** — TickOnce mutates `_previousAssignments` BEFORE publish. Stage 2 or 3 throw → next tick's Hungarian sticky bias references assignments no tech was actually told about.
- **[SmartForm.cs:56-99](../../SmartForm.cs)** — OnTechSpawn multi-step registration with no compensating teardown. Partial failure leaves helper with FormState set but tech absent from World.
- **[ContinuousController.cs:297-328](../../Control/ContinuousController.cs)** — OnControlFrame throws on malformed profile → Unity per-frame callback exception kills frame loop. No try/catch.

### 2.R2.E Lifecycle leaks

- **[SmartForm.cs:47-52](../../SmartForm.cs)** — DeInitGlobal does NOT clear `helper.FormState` on live techs (SHELL-API-GUIDE §6.3 NORMATIVE). Every Smart-driven TankAIHelper retains a SmartPerTechState transitively holding Controller + TacticalOptimizer + SamplingMPC + WeaponFireController + 2 DoubleBuffers + KinematicTracker + 4 more DoubleBuffers + OwnerTeam→TeamRuntime (already-dead Planner/Coordinator with PUCT trees of ~5000 nodes each). **Each tech leaks roughly 1-10 MB.**
- **[LearningService.cs:34-37, 78-91](../../Learning/LearningService.cs)** — Shutdown does not null Intent/ActionValue/Residual/Threat. Models hold `float[]` + AdamState + DoubleBuffer + BoundedQueue. Intent classifier alone ~360KB. Across mod reloads/form swaps they leak indefinitely.
- **[PathingService.cs:70, 88, 130-133, 190-196](../../Pathing/PathingService.cs)** — `_lastPaths` grows per-TechId with no recycle-time eviction. No TechDespawned subscription. Trajectories for despawned techs accumulate forever during a session.

### 2.R2.F NaN / Infinity / overflow paths round 1 didn't catalogue

- **[OpponentIntentClassifier.cs:213, 222](../../Learning/OpponentIntentClassifier.cs)** — trainer worker IndexOutOfRange if Label outside [0, 6). When DamageObserved/event producer goes live with sentinel labels, trainer silently crashes per-minibatch.
- **[BeliefDecay.cs:27, 35-37](../../World/BeliefDecay.cs)** — `DampVelocity` amplifies (not damps) velocity when dt is negative; NaN dt poisons belief permanently. `IsLost` returns false for NaN trace — corrupted belief never garbage-collected.
- **[TargetAssignment.cs:67-101](../../Coordination/TargetAssignment.cs)** — Hungarian silently produces wrong assignment when any input cost is NaN. NaN comparisons are always false so `minv[j]` never updates → `j1=-1` → augmenting path exits early → partial result.
- **[PhysicsRollout.cs:97-102](../../Control/PhysicsRollout.cs)** — duplicate WrapAngle while-loop hangs on infinite heading. Same bug at CostFunction.cs:81-82 — both share KalmanUpdate's pattern that round 1 flagged.
- **[CostFunction.cs:69, 134](../../Control/CostFunction.cs)** — `trajectory[Length-1]` indexes empty arrays. StateReachCost + WeaponAlignmentCost have no length guard.
- **[MobilityProfile.cs:49-51, 60](../../Vehicle/MobilityProfile.cs)** — sqrt becomes NaN on negative input; Asin(NaN) propagates. NaN-laced MobilityProfile flows to every downstream classification.
- **[KalmanUpdate.cs:91-100](../../World/KalmanUpdate.cs)** — Q-noise computation overflows when maxA is large/Inf; entire covariance NaN-poisoned permanently.

### 2.R2.G Coupling violations + hidden cycles

- **[PathingService.cs:147-174](../../Pathing/PathingService.cs)** — ThreatFieldRebuildLoop calls `SmartRuntime.EnumerateTeams()` and dereferences `TeamRuntime.EnemyVehicleSnapshots()`. SmartRuntime→Pathing (Init) + Pathing→SmartRuntime→TeamRuntime→{Planning, Coordination} = **hard cycle**. Violates PATHING-CONTRACT §0.
- **[ContinuousController.cs:171, 196](../../Control/ContinuousController.cs)** — Bypasses its own constructor's DI model with `PathingService` statics. Constructor signature lies about what the class needs; untestable.
- **[PerceptionWorker.cs:80-83](../../World/PerceptionWorker.cs)** — `WorldEventBus.Publish` runs on worker thread, violating WORLD-CONTRACT §8.4 main-thread-only invariant.

### 2.R2.H Hyperparameter wiring is fundamentally broken

- **[TrainingMatch.cs:242-289](../../Training/TrainingMatch.cs)** — CMA-ES tuning surface cannot disambiguate name collisions. Six fields are documented "CMA-ES tunable" but **unreachable** from the harness because they share short names across classes:
  - `WThreat` collides: CostFunction (reachable), StrategicValueFunction (reachable), **TrajectoryOptimizer (unreachable)**
  - `WReach` collides: CostFunction (reachable), **TrajectoryOptimizer (unreachable)**
  - `LearningRate` collides: TacticalOptimizer (reachable), **TrajectoryOptimizer (unreachable)**
  - `StepDt` collides: SamplingMPC (no key), StrategicRollout (no key)
  - `WSmooth`, `WLength`, `WVelocity` on TrajectoryOptimizer: **no keys at all**
- **[OpponentIntentClassifier.cs:84-94, similar](../../Learning/OpponentIntentClassifier.cs)** + the four model `Evaluate`s allocate fresh forward-pass buffers per call. Round 1 noted this generally; ActionValueEstimator alone allocates 7 arrays per Evaluate, ~512 array allocations per minibatch step.

### 2.R2.I Doc-vs-code drift that contradicts the spec

- **[CostFunction.cs:109-124](../../Control/CostFunction.cs)** — Summary says "Trapezoid integration"; body is arithmetic mean. Round 1 caught the math bug; round 2 adds that the doc EXPLICITLY misrepresents what the code does, so spec-comparison reviews silently false-positive.
- **[WorldModel.cs:34-37](../../World/WorldModel.cs)** — class doc: "entries mutated only by main-thread; worker only reads". `PublishPerTechBelief` (called from worker) mutates entries. Round 1 2.1 caught the race; this catches the doc misdescription.
- **[EventBus.cs:77-83](../../World/EventBus.cs)** — bus doc: "Workers MUST NOT call Publish directly"; PerceptionWorker does exactly that.
- **[ContinuousController.cs:275-296](../../Control/ContinuousController.cs)** — Doc cites CONTROL §9.1 to justify `Steps[0]`; §9.2 mandates receding-horizon indexing — the doc cites the clause it violates.
- **[LearningService.cs:22-28](../../Learning/LearningService.cs)** — claims the DamageObserved → Threat pipeline is "end-to-end" while round 1 3.9 found it has no producer.
- **[ProfilePersistence.cs:89-101](../../Learning/ProfilePersistence.cs)** — inline comment "atomic rename per §6.1 steps 3-5"; actual sequence is Delete-then-Move (non-atomic on Windows).
- **[LineOfSight.cs:36-38](../../Coordination/LineOfSight.cs)** — comment says "Mask out the observer's own colliders"; code applies no mask. Exact kind of false reassurance that lets the bug survive review.
- **[EvolutionarySearch.cs:220-235](../../Training/EvolutionarySearch.cs)** — doc writes the CORRECT Ros-Hansen rank-μ formula, then implements the BUGGY multiplicative form on the very next line. Self-contradicting mid-paragraph.

### 2.R2.J Dev-tool / testability holes

- **All strategic / coordination / per-tech state is `internal`** in [SmartRuntime.cs:25, 168, 396, 405](../SmartRuntime.cs). No `[InternalsVisibleTo]` declared. **No external dev tool can read** the current StrategicPlan, TargetAssignment map, TacticalGoal, ControlProfile, BeliefState, or PUCT visit counts.
- **[ProjectileFired](../../Control/WeaponFireController.cs)** — has subscribers (TrainingMatch) but **no producer anywhere in the repo**. WeaponFireController raises nothing. `outcome.OurShotsFired` is permanently 0; `WeaponEfficiency` term dead. With `w_efficiency = 0.5`, half a unit of CMA-ES score is unavailable.
- **[PretrainingPipeline.cs:50-58](../../Training/PretrainingPipeline.cs)** — 1000-iteration loop calls synchronous `TrainingMatch.Run()` whose body is `/* simulation skipped */`. Spawns and despawns 1000 real Tanks for zero training. Then saves the still-Glorot baseline.
- **[SmartForm.cs:171-176](../../SmartForm.cs)** — `DrawPathingDebugGUI` is empty. DIAGNOSTICS-CONTRACT.md (313 lines) describes a decision log, replay log, debug viz, compute-budget allocator, performance monitor. **Zero implementation files exist.** A developer's only investigation surface is `DebugTAC_AI.Log` lines.
- **[ThreadingDiagnostics.cs:94-106](../../Threading/ThreadingDiagnostics.cs)** — `RaiseQueueDepthSampled` + `RaiseWorkerIdle` defined but never invoked. The two signals a dev needs most ("is my queue backing up", "are workers starving") never fire.
- **[EventBus.cs:57-74](../../World/EventBus.cs)** — 17 event types defined; **11 have neither producer nor consumer**. The contract surface is essentially aspirational. No marker (Obsolete, XML doc) indicates v0.1.0 wired status.
- **No test files in the repo.** TRAINING-CONTRACT §358 promises "tests that exercise SelfPlayHarness on a tiny scenario" — `find` returns zero test files.

---

## 3. Medium — Selected

### 3.R2.A Inconsistent service-lifecycle conventions

[SmartRuntime.cs:335-344](../SmartRuntime.cs) — Four subsystems expose `IsRunning` four different ways:
- PathingService: `Volatile.Read(ref _running) == 1` ✓
- LearningService: same ✓
- SmartRuntime: `Pool != null && Pool.IsRunning && World != null` — three non-volatile reads
- WorkerPool: `!_disposed && !_rootCts.IsCancellationRequested` — `_disposed` is plain bool

`Pool` and `World` are auto-properties. Worker threads read `SmartRuntime.World` without barriers at multiple call sites. Shutdown writes null. Workers can observe `World != null` after Shutdown's null write.

### 3.R2.B Multiple "TickStamp" time bases under one name

[KinematicTracker.cs:14, BeliefState.cs:166/71, ContinuousController.cs:32-33, ThreatField.cs:42, TerrainMap.cs:13, VehicleModel.cs:99](#)

Six types carry `TickStamp` (or close variant) with **four different underlying counter sources**: per-tracker monotonic counters, `PerceptionWorker._currentTick`, `ContinuousController._tickCounter`, `Stopwatch.GetTimestamp()`, plus `SmartRuntime.LastObservationTick`. A consumer comparing `ControlProfile.ValidFromTick` against `BeliefSnapshot.TickStamp` is comparing two unrelated 0-based counters; comparing either against `TerrainMapSnapshot.TickStamp` is comparing a small counter to a Stopwatch tick (~1e8/s). CONTROL-CONTRACT §9.2 wants `currentTick - profile.ValidFromTick` — but no single "currentTick" they could agree on exists.

### 3.R2.C Five "Empty" placeholder conventions; one allocates per read

[BeliefState.cs:175, ArmorMap.cs:19, ArmorMap.cs:36, MassDistribution.cs:28, VehicleModel.cs:128, TerrainMap.cs:32](#)

- `static readonly T Empty = new T(...)` — three places
- `static T Empty { get; } = new T(...)` — two places
- `static T Empty => new T(...)` (allocates per call) — **MassDistribution**
- Static parameterized methods — two places

`MassDistribution.Empty` is invoked from VehicleModelSnapshot.Empty per spawn; allocates a fresh 56-byte struct each access while parallel ArmorMap.Empty short-circuits. Real GC smell.

### 3.R2.D Inconsistent validation

[Various](#) — Constructors throw on bad input in some places (BeliefState, BoundedQueue, WorkerPool, MarshallingPatterns); silently accept null deps in others (ContinuousController, Coordinator, StrategicPlanner). ARCHITECTURE §4 E5 "loud failure" policy honored unevenly. A developer adding a new caller has no policy to follow.

### 3.R2.E Worker pacing config: 4 spellings, 3 storage shapes, 1 hard-coupled TTL

[StrategicPlanner.cs:12, Coordinator.cs:20, OnlineTrainer.cs:171, PathingService.cs:62-63, PerceptionWorker.cs:111](#)

- Properties with `MillisecondsPerTick`
- A property with `MillisecondsPerPoll` (different word, same concept)
- Private const `ThreatFieldTickMs`
- Local const `TickPeriodMs` declared inside RunLoop

`SmartRuntime.ExternalGoalMaxAgeMs = 1000` hard-codes the TTL for goals Coordinator publishes. If `Coordinator.MillisecondsPerTick` is tuned above 1000, the TTL silently expires every published goal before Control reads it. No structural relation between the two.

### 3.R2.F Field-modify-then-throw + races

- **[Coordinator.cs:65](../../Coordination/Coordinator.cs)** — `_previousAssignments = targets` BEFORE publish. Stage 2/3 throw → next tick references assignments no tech actually pursued. Sticky-bias self-reinforcing in the wrong direction.
- **[SmartRuntime.cs:191-200](../SmartRuntime.cs)** — `RegisterTech` sets `state.OwnerTeam = this` before `_techs[id] = state`. Reverse order safer. Partial failure leaves dangling OwnerTeam pointer.
- **[EvolutionarySearch.cs:203-241](../../Training/EvolutionarySearch.cs)** — `Update` mutates `EvolutionPath + Sigma + Mean` in non-atomic sequence. Any throw in the middle leaves search state inconsistent across (Mean, Sigma, EvolutionPath) — CMA-ES convergence guarantees evaporate.
- **[EvolutionarySearch.cs:251-292](../../Training/EvolutionarySearch.cs)** — `SaveCheckpoint` truncates existing file before writing; disk-full mid-write corrupts checkpoint with no snapshot-before-write.

### 3.R2.G More numerical edge cases

- **[TerrainMap.cs:87-93](../../Pathing/TerrainMap.cs)** — public ctor allows `cellSize = 0` → all queries divide by zero. NormalAt produces NaN normal → IsTraversable always returns true → terrain silently considered traversable everywhere.
- **[ArmorMap.cs:38-43, 56-121](../../Vehicle/ArmorMap.cs)** — public ctor allows zero-resolution. `QueryWeakFace` with zero attack direction returns bogus +X face.
- **[ContinuousController.cs:162](../../Control/ContinuousController.cs)** — `Atan2(HeadingWorld.x, HeadingWorld.z)` silently returns 0 for vertical heading. MPC starts believing the tech faces world-east.
- **[OnlineTrainer.cs:49, 64-69](../../Learning/OnlineTrainer.cs)** — AdamState.T overflow after ~2.1B steps. `Math.Pow(0.9, -2.1B) = +Infinity` → biasCorr1 = -Infinity → mHat = 0 → optimizer silently freezes. Same risk in TacticalOptimizer._t.
- **[WeaponFireController.cs:246-265](../../Control/WeaponFireController.cs)** — hysteresis breaks when current target moves out of range. ExpectedValue returns 0; any other target with bestValue > 0 immediately wins. Target-flutter every tick.
- **[ThreatField.cs:115-118](../../Pathing/ThreatField.cs)** — Gaussian returns NaN when Radius is exactly 0 (squeezes past `< 1e-3f` guard via float strictness).
- **[PlanDecomposition.cs:203-206, 220-227](../../Coordination/PlanDecomposition.cs)** — Disengage and Bait normalize zero direction when start/target coincide. Heading silently 0.
- **[BeliefDecay.cs:55-61](../../World/BeliefDecay.cs)** — `IsLost` returns false for NaN-trace beliefs; poisoned beliefs immortal.

### 3.R2.H Documentation drift (continuation of 2.R2.I)

- **[KalmanUpdate.cs:152-164](../../World/KalmanUpdate.cs)** — comment claims "diagonal approximation, K_ii ≈ P_ii / S_ii"; body fills full off-diagonal K matrix using wrong divisor.
- **[KalmanUpdate.cs:197-213](../../World/KalmanUpdate.cs)** — ApplyDamageObservation summary says "Updates only position with a high variance"; body is a stub.
- **[BeliefState.cs:48-58](../../World/BeliefState.cs)** — "Immutable; every update produces a new instance" — but KalmanUpdate / BeliefDecay share array refs.
- **[BeliefState.cs:145-148](../../World/BeliefState.cs)** — comment cites WORLD §2.2 to justify uniform intent prior; §2.2 mandates team-conditioned (round 1 3.2).
- **[BeliefDecay.cs:11-12, 67-70](../../World/BeliefDecay.cs)** — "activates when PATHING lands at step 1.8"; Pathing has landed; ApplyTerrainBiasing still no-op.
- **[TacticalOptimizer.cs:110-112](../../Control/TacticalOptimizer.cs)** — stale TODOs cite "PATHING.TerrainMap lands" / "COORDINATION lands"; both have landed.
- **[KinematicTracker.cs:35-36](../../Vehicle/KinematicTracker.cs)** — doc claims "History buffer of 4 samples"; only one prior sample stored.
- **[ArmorMap.cs:26](../../Vehicle/ArmorMap.cs)** — doc claims "Resolution adaptive to tech extents"; caller passes fixed 8×8×8.
- **[WeaponProfile.cs:60-67](../../Vehicle/WeaponProfile.cs)** — doc says "builder emits an empty list"; code populates placeholder values.
- **[TrajectoryOptimizer.cs:221](../../Pathing/TrajectoryOptimizer.cs)** — comment claims "Trapezoidal weighting"; body is left-Riemann sum.
- **[MarshallingPatterns.cs:63-67](../../Threading/MarshallingPatterns.cs)** — comment says "freshest pending wins"; actually FIFO drains oldest survivor.
- ~~**[TeamBelief.cs:10-12](../../Coordination/TeamBelief.cs)**~~ — **REV 7 P5 Item 22: file DELETED** (orphaned class — zero `new TeamBelief(` callers tree-wide). The audit finding is moot; LOS aggregation now lives inline in `TeamRuntime.BuildTeamSnapshot` at `SmartRuntime.cs:401-429`.

---

## 4. Low + info — counts only

Round 2 generated approximately 35 low-severity findings and 8 info-grade observations across all domains. See raw output for full enumeration. Notable categories:

- **WrapAngle while-loops** duplicate at CostFunction.cs:81-82, PhysicsRollout.cs:97-102, PerceptionWorker (rethrow pattern), Threading (LogWarning cascade) — pattern duplication makes single fix difficult to spread.
- **Stale TODO markers** citing subsystems that have already landed: PUCTSearch ("when Learning ships"), StrategicValueFunction ("when ActionValueEstimator lands"), TacticalOptimizer (3 separate stale citations), TerrainMap (water check), ThreatField (LOS already wired).
- **API naming inconsistencies**: TargetAssignment_Hungarian (underscore-suffix, unique pattern), TrajectoryOptimizer.Result (nested), SmartMovementController (not sealed), various.

---

## Cross-cutting themes (round 2 specific)

### Theme E — Process-global state with no MP safety net

`Time.timeScale`, the `tank.GetInstanceID()`-keyed registries, and the `local.bin` profile path are all process-global with no host/client awareness. Combined with the missing `ManSafeSaves` integration, the entire persistence + multiplayer story is broken. Even single-player session-to-session loss is real (1.R2-D + 1.R2-E + 1.R2-M).

### Theme F — Dead code that looks alive

Eleven WorldEventBus event types have zero producers and zero consumers. DIAGNOSTICS-CONTRACT.md is 313 lines describing implementation files that don't exist. PretrainingPipeline runs 1000 zero-duration matches. RunLibraryEvaluation iterates a name list that always returns null. PerceptionWorker is never instantiated (carried from round 1). LearningService has 3 of 4 dormant handlers (round 1) + the live one has no producer (round 1's 3.9 + this round's 2.R2.J for ProjectileFired). The system has substantial **infrastructure shaped like training that doesn't train**.

### Theme G — Allocation cost will surface as soon as Smart actually runs

~1 MB/s steady-state allocation under combat conditions is dominated by the planner's PUCT search (~50K allocations per Search at 3.3 Hz strategic cadence). Even before unit tests can run, the GC pressure alone will be visible at runtime. Smart's per-tick budget is effectively zero allocations — getting there requires refactoring nearly every cost path (most as caller-supplied buffer parameters; some as pool/free-list patterns).

### Theme H — There is no way to inspect or test anything

No MonoBehaviour entry point. No `[InternalsVisibleTo]`. No public state-snapshot API. No debug viz. No diagnostics implementation. No test files. The cumulative ergonomic surface is: "compile errors → ship → maybe log lines". A developer running Smart for the first time has no path to verify any subsystem actually works.

---

## How to use this document, combined with round 1

The combined R1+R2 documents are now comprehensive — a third round would primarily catch round-2 misses, not new categories. The fix priority should be:

1. **R1 Critical 1.1 + 1.2 + R2 1.R2-A + 1.R2-B + 1.R2-C** — get the project to compile. Five distinct compile failures from one fix pass should resolve.
2. **R1 Critical 1.3 (PerceptionWorker) + R2 1.R2-D (ManSafeSaves/OnWorldReset)** — without these, nothing downstream is observable.
3. **R1 Critical 1.4 + R2 1.R2-N (Shutdown race + modified-flag race)** — protect profile integrity before extending learning.
4. **R2 1.R2-G + 1.R2-K + 1.R2-L (work loss + per-tick exception safety + coroutine cleanup)** — three concrete robustness fixes that prevent silent failures.
5. **R2 1.R2-H + 1.R2-I (build configuration + driver MonoBehaviour)** — without these, the dev cycle is unrunnable.
6. **R1 Theme A (host gating) + R2 2.R2.B (MP failures)** — close the multiplayer story before any in-engine MP testing.
7. **Theme C (round 1) + Theme E/F (round 2) — contract drift + dead infrastructure** — walk through each `CONTRACT.md` and decide: code matches spec, or amend spec to match code, or delete the contract clause if no implementation will be done.
8. **R2 1.R2-M (LoadLibrary spam) + 2.R2.J (ProjectileFired no producer) + 2.R2.H (hyperparameter name collisions)** — together they make the training loop produce zero score gradient.
9. Remaining numerical edge cases (Theme R2.G), allocation refactoring (Theme R2.C), and lifecycle / leak fixes.

### Round 2 audit metadata

- 10 parallel reviewers, one per domain.
- ~1.82M tokens consumed by reviewers (vs ~917K in round 1).
- 732 tool uses (vs 312 in round 1).
- Run duration: 15.1 minutes wall clock (vs 8.5 in round 1).
- Reviewers read AUDIT.md before their pass and were instructed to flag only NEW findings.

END OF AUDIT-R2.md
