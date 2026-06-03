# FIX-PLAN.md

**Source:** Synthesis of 10 identical planning agents, each independently producing a comprehensive fix plan from [AUDIT.md](AUDIT.md) and [AUDIT-R2.md](AUDIT-R2.md).
**Method:** All 10 agents started with the same prompt and access to the same files. Divergence came from natural variation. Convergence — phase orderings, specific decisions, root-cause groupings — is the synthesis signal.

---

## How this plan was built

Each of the 10 planners produced:
- An approach summary explaining its phase-ordering philosophy
- An ordered list of phases with goals, file-level changes, decisions, and risk
- A "final state" describing what Smart looks like when all phases complete
- "Unique insights" the planner thought others might miss
- Open questions where they couldn't decide

The synthesizer compared:
- **Phase orderings** to find consensus and outliers
- **Decisions taken at contested points** (DoubleBuffer fix approach, brake semantics, etc.) by counting votes and weighing rationales
- **Unique insights** for non-obvious dependencies that any single agent might have missed
- **Open questions** that recurred across planners → flagged for human input rather than synthesized away

### Universal consensus

Every plan started with **Phase 1 = compile fixes** (all 7 compile failures + the LogWarning shim + the SMART_DEV / `STEAM, DEV` typo). Universal.

Every plan eventually contained:
- Wire PerceptionWorker (R1 1.3) — 9 of 10 plans called it "highest-leverage" or "single biggest cheap fix"
- Lifecycle inversion (CancelAllAndJoin FIRST) — universal
- Host gating as a single coherent pass (Theme A)
- ManSafeSaves + OnWorldReset (R2 1.R2-D) — universal
- DamageObserved + ProjectileFired Harmony patches — universal
- NaN guards before math fixes (Plan 10 insight #7 makes this explicit)
- CMA-ES hyperparameter name-collision fix (R2 2.R2.H)
- Hot-path GC reduction late (after substantive fixes settle)
- Pretrained baseline last

### Contested decisions and final picks

| Decision | Plans favoring A | Plans favoring B | **Pick** | Rationale |
|---|---|---|---|---|
| `DoubleBuffer<T>` fix approach | Sealed wrappers (1, 2, 4, 6, 8, 10) | Drop the `: class` constraint (3, 5, 7, 9) | **Drop the constraint** | Plan 5's Insight #2 + Plan 8's #1: ~5 file edits vs ~15. Boxing cost is the same either way. Immutability discipline is documented; the type system doesn't enforce it under either approach. |
| ValueTuple fix | NuGet add (0) | `TrainStepResult` readonly struct (10) | **TrainStepResult struct** | User constraint forbids NuGet. Plan 8 insight #4 notes the polyfill alternative; rejected as fragile. |
| Lifecycle vs wiring order | Lifecycle first (1, 6, 8) | Wire first (3, 4, 7, 10) | **Wire first** | Plan 6 insight #2 + Plan 2 insight #6: PerceptionWorker is 3 lines but unblocks 10+ downstream findings. Lifecycle correctness on an empty world is checking nothing. |
| MonoBehaviour driver placement | Phase 1-2 (4, 5, 6, 10) | Phase 6+ (1, 7, 9) | **Phase 2** | Plan 10 insight #1: "single largest velocity multiplier". Developer can interactively verify each subsequent phase. |
| MP integration placement | Early (2, 4) | Late (1, 5, 7, 8, 9) | **Split into two passes** | Time.timeScale + TechId factory + IsHost flag = early (cheap safety guards). ManSafeSaves + host-handover = later (depends on lifecycle). |
| Spec-vs-code drift resolution policy | Change code (most) | Amend spec (Plan 2, Plan 6) | **Plan 6 framing** | "Spec wins when it's design intent; code wins when it's engineering reality." Apply case-by-case; document each decision in `DRIFT-DECISIONS.md`. |
| Brake semantics | Actuator-side `drive = max(0,T)·(1-B) + min(0,T)` (10) | PhysicsRollout-side (others) | **Actuator-side** | Plan 10 #2: TankControl is engine truth. Keeps coupling to TerraTech minimal. |
| WeaponFireCommits placement | Per-ControlVector per spec | Per-ControlProfile per code | **Amend spec to per-profile** | Plan 6 #11 framing: per-profile matches engineering reality (aim point is per-profile too). |
| Library scenarios | JSON ship | Procedural fallback | **Procedural fallback now, JSON later** | No JSON loader yet; procedural is BCL-only. Closes R2 1.R2-M's auto-halt without requiring asset pipeline. |

---

## The 10-phase plan

Each phase is independently completable and ordered by hard dependency. **A later phase MUST NOT undo an earlier phase's decisions.**

### Phase 1 — Compile + build configuration

**Goal:** project builds cleanly in a configuration that actually includes Training/.

**Closes:** R1 1.1, R1 1.2, R2 1.R2-A, R2 1.R2-B, R2 1.R2-C, R2 1.R2-H.

**Key changes:**

1. **[DebugTAC_AI.cs](../../../../DebugTAC_AI.cs)** — add `internal static void LogWarning(string)` and `LogWarning(string, Exception)` overloads routing through `UnityEngine.Debug.LogWarning` with the existing "Advanced AI: " prefix. ~27 Smart-side call sites work as-is. (Also resolves the `TypeInitializationException` cascade Plan 1 insight #1 + Plan 6 insight #1 identify.)
2. **[DoubleBuffer.cs](../../Threading/DoubleBuffer.cs)** — **drop the `where T : class` constraint**. Convert the storage field to `object _current` internally. `Read()` returns `(T)Volatile.Read(ref _current)`. `Write(T value)` does `Interlocked.Exchange(ref _current, (object)value)`. Document on the type: "T must be treated as immutable by discipline." This fixes all 4 CS0452 cases (R1 1.2 + R2 1.R2-A) in one edit. No call-site changes needed.
3. **[OnlineTrainer.cs](../../Learning/OnlineTrainer.cs)** — replace `(float, float, int)` tuple with `public readonly struct TrainStepResult { public readonly float LossBefore; public readonly float LossAfter; public readonly int BatchSize; }`. Update `ILearnedModel.TrainOneMinibatch()` signature. Update all four model implementations + `TrainerWorker.RunLoop`'s deconstruction.
4. **[WeaponFireController.cs:312](../../Control/WeaponFireController.cs)** — change `LearnedResidual(WeaponId targetId, float dt)` to `(TechId targetId, float dt)`. Spec-aligned per LEARNING-CONTRACT §3.3 + CONTROL-CONTRACT §10.3.
5. **[TAC_AI.csproj](../../../../TAC_AI.csproj)** — fix malformed `TRACE;DEBUG;STEAM, DEV` to `TRACE;DEBUG;STEAM;DEV`. Add a `Dev` configuration with `DefineConstants = TRACE;DEBUG;SMART_DEV`. Add Smart-Dev-only references where Training files import.

**Decisions taken:**
- DoubleBuffer constraint drop over wrappers — see contested-decisions table.
- TrainStepResult struct over NuGet — user's BCL-only constraint.

**Dependencies:** None.

**Risk:** Low. Pure additive + signature changes.

**Success criteria:** MSBuild succeeds in all 5 configurations (Debug, Release, Steam, SteamDev, Dev). Training/ produces non-empty IL under Dev. Zero new warnings.

---

### Phase 2 — Make Smart runnable

**Goal:** A developer can launch a self-play session, observe state, and verify subsequent phases interactively.

**Closes:** R2 1.R2-I (no MonoBehaviour), R2 2.R2.J (no inspection surface), R2 1.R2-G (WorkerLoop work-loss), parts of R2 2.R2.D (debug viz stub), R2 2.R2.G (silent worker bugs).

**Key changes:**

1. **[Training/SmartTrainingDriver.cs](../../Training/SmartTrainingDriver.cs)** — NEW file, `#if SMART_DEV` gated. `internal sealed class SmartTrainingDriver : MonoBehaviour`. Lifecycle: created lazily by `SelfPlayHarness.BeginAsync(int maxGen, CancellationToken)` (new parameterless overload) on a hidden `DontDestroyOnLoad` GameObject. Exposes `Begin(int maxGen)`, `Stop()`, `OnGUI` minimal debug overlay (text-only at first: generation, mean score, queue depths). Hosts the worker→main-thread event marshal queue (Plan 1 insight #10).
2. **[Threading/WorkerPool.cs:146-150](../../Threading/WorkerPool.cs)** — remove the inner `_queue.TryDequeue(out _)` (R2 1.R2-G work-loss). Replace with `Thread.SpinWait(...); Thread.Sleep(0); continue;`. Two-line fix; outsized impact per multiple plans' insights.
3. **[AssemblyInfo.cs](../../../../Properties/AssemblyInfo.cs)** (or top of csproj) — add `[assembly: InternalsVisibleTo("Smart.Diagnostics")]` and `InternalsVisibleTo("Smart.Tests")` so future dev tooling + tests can read internal state.
4. **[Training/SelfPlayHarness.cs](../../Training/SelfPlayHarness.cs)** — add parameterless `BeginAsync(int maxGen, CancellationToken)` overload that creates the SmartTrainingDriver lazily and forwards.
5. **[SmartForm.cs:171-176](../../SmartForm.cs)** — `DrawPathingDebugGUI` becomes a real (text-only) overlay showing belief count, current plan, target map, MPC dispatch rate. Placeholder for later viz layers.
6. **[KickStart.cs](../../../../KickStart.cs)** — add a console command `smart-train <N>` that calls `SelfPlayHarness.BeginAsync(N, ...)`. (If AICommands.cs is the right surface in the codebase, route there instead.)

**Decisions taken:**
- WorkerPool fix shipped early per Plan 7 insight B + Plan 9 insight #4 + Plan 10 insight #6: 1-line fix, prevents weeks of "intermittent freeze under combat" debugging.
- `InternalsVisibleTo` over public-API exposure: minimal commitment, unblocks tests + dev tools, doesn't bake a public surface.

**Dependencies:** Phase 1.

**Risk:** Low.

**Success criteria:** `smart-train 1` runs without exception. A debug overlay appears. WorkerPool stress test (enqueue + spin-wait race) loses zero work items in 1000 iterations.

---

### Phase 3 — Wire the dead pipeline

**Goal:** Observation, events, world resets, and saves actually flow data through the system.

**Closes:** R1 1.3 (PerceptionWorker not wired), R1 3.9 + R2 1.R2-D (ManSafeSaves absent; OnWorldReset no-op), R2 2.R2.J (ProjectileFired no producer), R2 2.R2.B (TankTeamChangedEvent not subscribed), part of R2 2.R2.D (worker-thread Publish via main-thread relay).

**Key changes:**

1. **[SmartRuntime.cs:Init](../SmartRuntime.cs)** — construct `new PerceptionWorker(world)` and `Pool.Enqueue(perception.RunLoop)`. Three lines. Plan 2 insight #6 + Plan 6 insight #2: this is the single largest-leverage edit in the whole plan.
2. **[Smart/Integration/SmartHarmonyPatches.cs](../../Integration/SmartHarmonyPatches.cs)** — NEW file. Harmony patches:
   - Postfix on `ManDamage`'s damage hook (exact site confirmed at start of phase — see open question) → publishes `DamageObserved(victimTechId, sanitized)` to WorldEventBus.
   - Postfix on `ModuleWeapon.FireWeapon` → publishes `ProjectileFired(firerTechId, weaponId, origin, dir)`.
   - Postfix on `ManTechs.TankTeamChangedEvent` (or fallback: Harmony postfix on `Tank.Team` setter) → re-registers tech with new TeamRuntime.
   - Initialization in `SmartForm.InitGlobal`.
3. **[SmartForm.cs:InitGlobal](../../SmartForm.cs)** — call `ManSafeSaves.RegisterSaveSystem(Assembly.GetExecutingAssembly(), Smart_OnSave, Smart_OnLoad)`. `OnSave` snapshots and saves the current learning profile via `LearningService.SaveProfile`. `OnLoad` reloads the profile + clears world state via the new `SmartRuntime.OnWorldReset`. `DeInitGlobal` calls `ManSafeSaves.UnregisterSaveSystem`.
4. **[SmartForm.cs:OnWorldReset](../../SmartForm.cs)** — implement: `SmartRuntime.World?.Clear(); PathingService.OnWorldReset(); LearningService.SaveProfile(LearningService.CurrentPlayerId);`. Add `PathingService.OnWorldReset()` method that nulls `_lastPaths`, `_threatFields`, `_terrain`, and reconstructs.
5. **[World/EventBus.cs](../../World/EventBus.cs)** — add a main-thread marshal queue: workers calling `Publish` actually enqueue into a `ConcurrentQueue<Action>` drained by `SmartTrainingDriver.Update()` (in Dev) or `SmartForm.PostUpdate(helper)` (in Release). PerceptionWorker stops violating the main-thread invariant.
6. **[WorldModel.cs](../../World/WorldModel.cs)** — restructure `RecordObservation` to be safe under PerceptionWorker reads (per-entry lock around read-decide-reset, or per-tech SPSC queue).

**Decisions taken:**
- Harmony patch site for DamageObserved: defer to start-of-phase verification (open question A).
- ManSafeSaves `OnSave`/`OnLoad` signature: defer to start-of-phase verification (open question B).
- Worker-thread Publish routed via main-thread relay (Plan 8 insight #5: same bug as the "Publish from workers" doc lie).

**Dependencies:** Phase 1 (compile), Phase 2 (SmartTrainingDriver hosts the marshal drain).

**Risk:** Medium. Harmony patches require TerraTech API verification; if hook points differ from spec, partial fallback may apply.

**Success criteria:** Spawning a Smart-driven tech produces a non-empty `BeliefSnapshot.FusedBuffer` within 100ms. A damage event in-engine fires `DamageObserved`. A weapon fire fires `ProjectileFired`. World save → world load round-trips the learning profile. Captured techs change team in Smart's registry within one tick.

---

### Phase 4 — Lifecycle inversion + exception safety

**Goal:** Smart can start, run, and stop without leaking, racing, or torn-writing profiles. One bad tech doesn't crash the shell loop.

**Closes:** R1 1.4 + R2 1.R2-N (Shutdown race + modified-flag race) + R2 2.R2.E (LearningService leak), R1 2.7 + R2 2.R2.G consequences (terrain null race), R2 1.R2-K (Operations/ControlFrame no try/catch), R2 1.R2-L (StopCoroutine/exception leaks subs + timescale).

Plan 5 insight #3 — this single phase closes 4 critical bugs as side effects.

**Key changes:**

1. **[SmartRuntime.cs:Shutdown](../SmartRuntime.cs)** — invert ordering:
   ```
   (1) WorkerLifecycleRegistry.CancelAllAndJoin(2s timeout)
   (2) per-subsystem cleanup on QUIESCED state:
       - LearningService.Shutdown (now safe to read _params or DoubleBuffer)
       - PathingService.Shutdown (now safe to null _terrain, etc.)
   (3) Pool.Dispose
   (4) _teams.Clear()
   (5) WorldEventBus.ClearAll()
   (6) World?.Clear(); World = null; Pool = null
   ```
2. **[LearningService.cs:Shutdown](../../Learning/LearningService.cs)** — read each model's published snapshot via `Intent.PublishedParameters.Read()` instead of `_params` directly (closes the trainer-write/main-read race). Change `_modifiedSinceLoad` to `int` accessed via `Interlocked.Exchange/CompareExchange`. Null `Intent`, `ActionValue`, `Residual`, `Threat` after save (closes leak).
3. **[SmartForm.cs:Operations/ControlFrame](../../SmartForm.cs)** — wrap each per-tech method call in try/catch:
   ```csharp
   try { state.TickMainThread(0.033f); }
   catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Op[" + state.TechId + "]: " + ex); }
   ```
   Same for `state.Controller.OnOperationsTick`, `state.Controller.OnControlFrame`. One bad tech can't break the shell's per-tech iteration.
4. **[Training/TrainingMatch.cs:SimulateCoroutineFull](../../Training/TrainingMatch.cs)** — wrap the inner while-loop body in try/catch. On catch: `simulationError = ex; finished = true;`. Cleanup outside the loop runs normally. Closes the StopCoroutine + thrown-in-loop subscription/timescale leak.
5. **[SmartForm.cs:DeInitGlobal](../../SmartForm.cs)** — iterate live TankAIHelpers (`TankAIManager.inst.helpers` or equivalent) and null `FormState` on any whose state is `SmartPerTechState`. Closes the 1-10 MB per-tech leak across form swaps (SHELL-API-GUIDE §6.3 NORMATIVE).
6. **[ProfilePersistence.cs:Save](../../Learning/ProfilePersistence.cs)** — replace `Delete + Move` with `File.Replace(tmp, filePath, null)`. Fallback for first-save (file doesn't exist yet): `File.Move(tmp, filePath)`. Closes the Windows non-atomic-rename window.

**Decisions taken:**
- Trainer workers stay running during Shutdown's first phase (cancelled by CancelAllAndJoin); the save reads the **DoubleBuffer-published snapshot** rather than `_params`, which is the trainer's last published state. This closes Plan 8 insight #2's "four-part race" in one motion.
- File.Replace fallback handles first-save case explicitly per Plan 1 insight #8.

**Dependencies:** Phase 3 (LearningService needs to have been correctly Init'd to be Shutdownable).

**Risk:** Medium. Threading-sensitive; requires careful testing.

**Success criteria:** 100-cycle Init/Shutdown stress test produces zero leaked TankAIHelpers, zero leaked DoubleBuffer references, zero torn profile writes (CRC validates), zero NRE on cancelled workers. One bad tech (NRE in TickMainThread) does not stop other techs from ticking that frame.

---

### Phase 5 — Host gating + MP safety

**Goal:** Smart behaves correctly on client (idle, no overwrites) and prevents single-player-only side effects from leaking into MP.

**Closes:** R1 Theme A (host gating), R2 1.R2-F (Time.timeScale process-global), R2 1.R2-E (TechId process-local), R2 2.R2.B (m_USE_AVOIDANCE on client, OnTechSpawn no host check), R1 2.3 partial (OnControlFrame writes on client).

**Key changes:**

1. **[SmartRuntime.cs](../SmartRuntime.cs)** — add `internal static volatile bool IsHost`. Updated from `SmartForm.Operations(helper, host)` and `SmartForm.Directors(helper, host)`.
2. **[Pathing/PathingService.cs:ThreatFieldRebuildLoop + PathSolveLoop](../../Pathing/PathingService.cs), [SmartRuntime.cs:TeamRuntime.RunLoops](../SmartRuntime.cs), [Learning/OnlineTrainer.cs:TrainerWorker.RunLoop](../../Learning/OnlineTrainer.cs), [World/PerceptionWorker.cs:RunLoop](../../World/PerceptionWorker.cs)** — at the top of each tick body: `if (!SmartRuntime.IsHost) { cancellation.WaitHandle.WaitOne(tick); continue; }`. Workers stay loaded but idle.
3. **[Control/ContinuousController.cs:OnControlFrame](../../Control/ContinuousController.cs)** — early return when `!SmartRuntime.IsHost`. Closes R1 2.3 (client-side neutral overwrite of host-replicated TankControl).
4. **[Training/TrainingMatch.cs:EnableHeadlessMode](../../Training/TrainingMatch.cs)** — early-return when `ManNetwork.IsNetworked` is true. `DisableHeadlessMode` restores the captured pre-EnableHeadlessMode timeScale (capture in field) instead of hard-writing 1f. Closes R2 1.R2-F + Plan 3 insight #9 (user-respect win).
5. **[World/BeliefState.cs:TechId factory](../../World/BeliefState.cs)** — replace bare `tank.GetInstanceID()` with: `int id = (ManNetwork.IsNetworked && tank.netTech != null) ? (int)tank.netTech.netId.Value : tank.GetInstanceID();`. Wraps in `new TechId(id)`. Documented as: in MP uses netId for cross-machine consistency; SP falls back to instanceId.
6. **[SmartForm.cs:OnTechSpawn](../../SmartForm.cs)** — gate `tank.control.m_Movement.m_USE_AVOIDANCE = false` on `SmartRuntime.IsHost`. On client, leave vanilla avoidance enabled. Add restoration on `OnTechRecycle`.
7. **[LearningService.cs:_currentPlayerId derivation](../../Learning/LearningService.cs)** — replace hardcoded "local" with: `ManNetwork.IsNetworked ? ManNetwork.inst.MyPlayer?.PlayerName ?? "local" : "local"`. Append the current save-game name where available. Profile path: `<modDir>/Profiles/<sanitizedKey>.bin`. (Open question E: ManNetwork API confirmation.)
8. **[SmartRuntime.cs](../SmartRuntime.cs)** — fix double-`SmartAI` directory bug: drop the trailing `SmartAI` in modDir construction so the path resolves to `<cwd>/Mods/SmartAI/Profiles/`, not `<cwd>/Mods/SmartAI/SmartAI/Profiles/`.

**Decisions taken:**
- `IsHost` polled per worker iteration (cheap; Plan 1 insight #7 confirms ~240 volatile reads/s well below noise).
- TechId fallback to instanceId in SP. Documented as "single-player profiles use different IDs than MP profiles" — Plan 7 open question #4 accepted.
- Trainer workers PAUSED on client (Plan 10 open question #1, alternative A picked).

**Dependencies:** Phase 4 (lifecycle must be clean before per-loop host-gating can be safely added).

**Risk:** Medium. MP testing in-engine required to validate.

**Success criteria:** On a client in an MP session, Smart-driven techs do not overwrite host-replicated TankControl. Workers consume <1% CPU on client. `smart-train` refuses to run in MP. Single-player tech IDs survive form swap; MP tech IDs match across machines.

---

### Phase 6 — NaN/Infinity floors + WrapAngle fix

**Goal:** Numerical landmines cannot permanently poison Smart's state.

**Closes:** R2 1.R2-J (Lambda → NaN), R2 2.R2.F (Hungarian NaN, BeliefDecay negative dt, AdamState T overflow, etc.), R1 2.1 partial (Kalman gain NaN propagation), R2 1.R2-K math precondition.

Plan 10 insight #7: NaN guards MUST come before math fixes; otherwise debugging is much harder.

**Key changes:**

1. **[Control/SamplingMPC.cs:Solve](../../Control/SamplingMPC.cs)** — at solve entry: `float lambda = Mathf.Max(Lambda, 1e-6f);`. Use the snapshotted lambda throughout. Closes the unrecoverable NaN-propagation path Plan 1/5/6/7 all identified.
2. **[Control/PhysicsRollout.cs:97-102](../../Control/PhysicsRollout.cs) + [Control/CostFunction.cs:81-82](../../Control/CostFunction.cs) + [World/KalmanUpdate.cs:228-238](../../World/KalmanUpdate.cs)** — replace every `while (h > π) h -= 2π;` WrapAngle while-loop with `h = h - Mathf.Floor((h + π) / (2π)) * 2π;` (closed-form, NaN-safe). Single fix pattern duplicated three places.
3. **[Coordination/TargetAssignment.cs:67-101](../../Coordination/TargetAssignment.cs)** — after each cost-cell computation: `cost[i,j] = float.IsNaN(c) || float.IsInfinity(c) ? 1e9f : c;` Preserves Hungarian invariants under degenerate inputs.
4. **[World/BeliefDecay.cs:DampVelocity](../../World/BeliefDecay.cs)** — at entry: `if (dt <= 0f || float.IsNaN(dt)) return prior;`. Closes velocity-amplification + NaN-poisoning paths.
5. **[World/BeliefDecay.cs:IsLost](../../World/BeliefDecay.cs)** — add: `if (float.IsNaN(trace) || float.IsInfinity(trace)) return true;`. NaN-poisoned beliefs become reclaimable.
6. **[Vehicle/MobilityProfile.cs:Derive](../../Vehicle/MobilityProfile.cs)** — at top: `if (float.IsNaN(thrust.MaxLinearAccelPositive.z) || ...) return Default;`. Clamp every sqrt arg: `Sqrt(Mathf.Max(0f, ...))`. Defensive Asin via Clamp01.
7. **[World/KalmanUpdate.cs:Propagate](../../World/KalmanUpdate.cs)** — validate maxA at entry: `float maxA = float.IsNaN(prior.MaxAccelerationEstimate) || float.IsInfinity(prior.MaxAccelerationEstimate) ? 10f : Mathf.Clamp(prior.MaxAccelerationEstimate, 0f, 100f);`.
8. **[Learning/OnlineTrainer.cs:AdamState.T](../../Learning/OnlineTrainer.cs)** — change `public int T;` to `public long T;`. Closes the 2.1B-step Adam-freeze bug. Apply to `TacticalOptimizer._t` too.
9. **[Pathing/TerrainMap.cs:ctor](../../Pathing/TerrainMap.cs) + [Vehicle/ArmorMap.cs:ctor](../../Vehicle/ArmorMap.cs)** — add `if (cellSize <= 0 || width <= 0 || height <= 0) throw ArgumentException`. Prevents division by zero pollution downstream.
10. **[Learning/OpponentIntentClassifier.cs:DenseHeadStep](../../Learning/OpponentIntentClassifier.cs)** — at entry: `if ((uint)ev.Label >= (uint)OutDim) return 0f;`. Trainer doesn't crash on a malformed event.
11. **[Coordination/PlanDecomposition.cs:Disengage + Bait](../../Coordination/PlanDecomposition.cs)** — apply the safe-direction pattern used elsewhere: `Vector3 diff = ...; dir = diff.sqrMagnitude > 1e-3f ? diff.normalized : Vector3.back;`.

**Decisions taken:**
- Defensive clamps at consumer side, not at producer (Plan 5 insight #1: producer-side fixes don't recover from existing NaN-poisoned state; consumer-side does).

**Dependencies:** Phase 3 (need the pipeline alive to observe whether guards fire).

**Risk:** Low. Defensive additions; can be tested by feeding NaN inputs.

**Success criteria:** Set `SamplingMPC.Lambda = 0` mid-session — MPC continues producing non-NaN output. Set `BeliefDecay` dt = -1 — DampVelocity is a no-op. Run 1M-iteration AdamState.T stress — no freeze. NaN-poisoned belief is detected as Lost within 1 tick.

---

### Phase 7 — Math + contract drift sweep

**Goal:** Smart's per-subsystem math matches the spec's design intent. Contract drift is resolved one CONTRACT.md at a time.

**Closes:** R1 2.1 (Kalman gain S divisor), R1 2.2 (top speed √2, turning radius, maxAccelEstimate), R1 2.3 (MPC profile RMW + ThreatFieldCost averaging + receding-horizon + brake), R1 2.5 (DefensivePerimeter heading, COverlap missing, plan-bias role-agnostic), R2 2.R2.D (CMA-ES rank-µ post-step σ + multiplicative C), R1 2.6 (OutcomeScorer swap), most of Theme C from both rounds.

**Key changes (grouped by contract file):**

**THREADING-CONTRACT pass:**
- [WorkerLifecycleRegistry.cs](../../Threading/WorkerLifecycleRegistry.cs) — add `Action<TerminationReason> OnTerminated` callback field on `WorkerHandle`, invoked from `WorkerLoop`'s finally block. Closes R1 §3.1.
- Same file: clear `_live` on Shutdown stragglers (R1 §3.1 + Plan 9 hint).
- [ThreadingDiagnostics.cs](../../Threading/ThreadingDiagnostics.cs) — convert `_defaultHandlersInstalled` to int + Interlocked.CompareExchange.
- **Amend spec:** drop "permanent-termination fallback callback on WorkerHandle" since the event-based pattern is now sanctioned.

**WORLD-CONTRACT pass:**
- [KalmanUpdate.cs:155-164](../../World/KalmanUpdate.cs) — fix gain divisor: `K[row*N+col] = covIn[row*N+col] / S[col*N+col]`. (R1 2.1)
- [PerceptionWorker.cs:96-103](../../World/PerceptionWorker.cs) — add covariance-trace-doubling clause to `HasSignificantChange`.
- [BeliefDecay.cs:DampVelocity + KalmanUpdate.cs:ApplyDamageObservation](#) — deep-copy `Cov`/`Intent` arrays instead of sharing refs.
- [BeliefState.cs:NewlyObserved](../../World/BeliefState.cs) — branch on team relationship; emit the spec'd team-conditioned intent prior (hostiles uniform over Aggressing/Retreating/Flanking/Holding; allies/neutrals biased toward Holding/Idle).
- **Amend code-doc:** WorldEventBus class header amended to accurately describe mutation surface.

**VEHICLE-CONTRACT pass:**
- [MobilityProfile.cs:47-51](../../Vehicle/MobilityProfile.cs) — remove the spurious 2: `topFwd = Sqrt(MaxLinearAccelPositive.z / Max(DragK, 1e-3f))`.
- [MobilityProfile.cs:53-56](../../Vehicle/MobilityProfile.cs) — replace turning-radius formula with `r = topFwd² / lateral_accel` (centripetal-limited). Document the model choice.
- [MobilityProfile.cs:69-71](../../Vehicle/MobilityProfile.cs) — guard footprint > 0.5; return MobilityProfile.Default when ArmorMap is degenerate.
- [SmartForm.cs:97](../../SmartForm.cs) — replace VerticalAuthority-based maxAccelEstimate with `Max(MaxLinearAccelPositive.x, .y, .z, MaxLinearAccelNegative.x, .y, .z)`. Defer `World.RegisterTech` until first vehicle rebuild OR expose `World.UpdateMaxAccel(techId, value)` and call from rebuild.

**CONTROL-CONTRACT pass:**
- [CostFunction.cs:115-124](../../Control/CostFunction.cs) — drop `/ trajectory.Length` divide. ThreatFieldCost integrates as spec'd.
- [ContinuousController.cs:201-246](../../Control/ContinuousController.cs) — split the `_controlProfileBuffer` RMW race: publish `_controlSteps` (worker) and `_weaponDecision` (main) to separate DoubleBuffers. `OnControlFrame` reads both. Closes R1 2.3.
- [ContinuousController.cs:OnControlFrame](../../Control/ContinuousController.cs) — implement receding-horizon: `int index = Mathf.Clamp((int)(currentFrameTick - profile.ValidFromTick), 0, profile.Steps.Length - 1)`. Pass `SmartRuntime.MainTick` from `SmartForm.ControlFrame` instead of `0L`. Implement §9.3 stale-fallback: hold last for 1-2 ticks, neutral after `> currentTick + 5`.
- [ContinuousController.cs:OnControlFrame](../../Control/ContinuousController.cs) — actuator-side brake: `drive = Mathf.Max(0, step.Throttle) * (1 - step.Brake) + Mathf.Min(0, step.Throttle);`. [PhysicsRollout.cs](../../Control/PhysicsRollout.cs) — remove the separate brake-damping term; integrate `fwdAccel = (Throttle - Brake) * accelCapacity`.
- [WeaponFireController.cs:IsAimedWithinArc + CostFunction.cs:WeaponAlignmentCost](#) — 3D arc check: split into yaw + pitch components against `YawArcRadians`/`PitchArcRadians`.
- [WeaponFireController.cs:_states](../../Control/WeaponFireController.cs) — trim list when wCount < _states.Count; reset stale entries on shrink.
- [WeaponFireController.cs:PerWeaponState](../../Control/WeaponFireController.cs) — store `TechId CurrentTargetId` (not slot index); re-resolve slot per tick.
- [WeaponFireController.cs:EnforceEnergyBudget](../../Control/WeaponFireController.cs) — add slow refill (`reserve = min(1, reserve + RefillPerTick)`) until Tank energy state is plumbed.
- [SamplingMPC.cs:38-41](../../Control/SamplingMPC.cs) — accept a seed: SmartPerTechState constructs `new SamplingMPC(seed: techId.Value ^ 0x9E3779B9)`.
- [ContinuousController.cs:ShiftLeft](../../Control/ContinuousController.cs) — seed `shifted[len-1] = ControlVector.Zero` (receding-horizon convention).
- [TacticalOptimizer.cs:LearningRate](../../Control/TacticalOptimizer.cs) — drop from 0.5 to 0.1 (spec default).
- [ManeuverLibrary.cs](../../Control/ManeuverLibrary.cs) — wire as warm-start source in `ContinuousController` on substantial goal change.
- **Amend spec:** WeaponFireCommits stays per-ControlProfile (not per-ControlVector); aim is per-profile too.

**PLANNING-CONTRACT pass:**
- [StrategicRollout.cs:23-26](../../Planning/StrategicRollout.cs) — clamp `steps = (StepDt > 0 && RolloutHorizonSeconds > 0) ? Mathf.Max(1, Mathf.RoundToInt(RolloutHorizonSeconds / StepDt)) : 1;`.
- [StrategicPlanner.cs](../../Planning/StrategicPlanner.cs) — implement §2.7 low-confidence handling: `PUCTSearch.Search` returns `(plan, confidence)`. If `confidence < 0.4`, suppress write to PlanBuffer (preserves prior).
- [StrategicValueFunction.cs:ThreatExposure](../../Planning/StrategicValueFunction.cs) — restructure: per-friendly inner exposure, then mean over friendlies (matches spec).
- [PlanLibrary.cs:58](../../Planning/PlanLibrary.cs) — drop the `nHostile > 0` guard on EngageDistributed (spec doesn't list it as hostile-gated).
- [StrategicState.cs](../../Planning/StrategicState.cs) — remove the dead `GetHashCode` override OR add matching `Equals`. Pick removal (less surface).
- [PUCTSearch.cs:SelectAction](../../Planning/PUCTSearch.cs) — use `TryGetValue` to guard against `Expand` partial failure.
- [PUCTSearch.cs:FirstAction](../../Planning/PUCTSearch.cs) — deterministic action selection (lowest hash, not Dictionary enumeration order).

**COORDINATION-CONTRACT pass:**
- [PlanDecomposition.cs:147-153](../../Coordination/PlanDecomposition.cs) — fix DefensivePerimeter heading: `float heading = Mathf.PI * 0.5f - angle;` then normalize.
- [TargetAssignment.cs:67-101](../../Coordination/TargetAssignment.cs) — implement COverlap term. For EngageFocused: duplicate primary target as K columns at `-CPlan` so Hungarian can assign multiple techs. For EngageDistributed: soft penalty per existing assignment.
- [TargetAssignment.cs:96-98](../../Coordination/TargetAssignment.cs) — apply role-aware plan-bias: `-CPlan` only to lead; `+CPlan` to non-flanker secondaries.
- [PlanDecomposition.cs:MobileScreen](../../Coordination/PlanDecomposition.cs) — shared formation anchor + correct units (m vs m/s).
- [PlanDecomposition.cs:Bait](../../Coordination/PlanDecomposition.cs) — distribute non-bait flankers around a flank arc, not all on one point.
- [PlanDecomposition.cs:Flank](../../Coordination/PlanDecomposition.cs) — branch on role: Pursuer → axis; Holder → position; otherwise → flank.
- [PlanDecomposition.cs:FightingRetreat](../../Coordination/PlanDecomposition.cs) — slowest-tech sets pace; use `min(Mobility.TopSpeedForward)` across retreating techs.
- [Coordinator.cs](../../Coordination/Coordinator.cs) — publish a unified `CoordinationState { TargetMap, RoleMap, LOSCoverage, ActivePlan }` via `DoubleBuffer<CoordinationState>`. Closes R1 §3.7 + R2 2.R2.J (inspection) + R2 3.R2.F (publish-before-mutate ordering).
- [TargetAssignment.cs:cost matrix](../../Coordination/TargetAssignment.cs) — use fresh KinematicBuffer position, not VehicleBuffer's stale position (extend TeamSnapshot with `KinematicsByTech`).
- [Coordinator.cs:TickOnce](../../Coordination/Coordinator.cs) — reorder: compute all stages, THEN publish, THEN update `_previousAssignments`.

**TRAINING-CONTRACT pass (math only — pipeline fixes in Phase 8):**
- [EvolutionarySearch.cs:Update](../../Training/EvolutionarySearch.cs) — snapshot pre-step Sigma before `sigmaMultiplier`. Use the snapshot for both `y` normalization and rank-μ. Fix the C update: `C_j ← (1 − c1 − cμ) C_j + c1 · p_σ_j² + cμ · Σ w_k · y_kj²` (Ros-Hansen 2008 form, no `oldVar` multiplier on additive terms).
- [OutcomeScorer.cs:DeriveWinner](../../Training/OutcomeScorer.cs) — add `ThemStartingHpTotal`, `ThemEndHpTotal`, `ThemShotsFired` to MatchOutcome. Populate from telemetry. Use them in the swap so both sides have symmetric stats.
- [EvolutionarySearch.cs:Update](../../Training/EvolutionarySearch.cs) — defer-and-commit pattern: compute newEvolutionPath, newSigma, newMean into locals; commit all at the end. Prevents inconsistent state on exception.
- [EvolutionarySearch.cs:SaveCheckpoint](../../Training/EvolutionarySearch.cs) — mirror ProfilePersistence snapshot-before-write pattern.

**Decisions taken (key spec-vs-code positions):**
- Code wins for: WeaponFireCommits placement, brake semantics framing (amend the spec to match shipped behavior).
- Spec wins for: receding-horizon index, Kalman gain divisor, top-speed formula, threat-cost integration, OutcomeScorer symmetry, plan-bias role distinction, DefensivePerimeter outward heading.
- Document every decision in a new `DRIFT-DECISIONS.md` (Plan 1 final-state).

**Dependencies:** Phase 6 (NaN guards must precede math changes per Plan 10 insight #7).

**Risk:** High. Many touch points; bugs can introduce regressions in behavior that's hard to test without in-engine play.

**Success criteria:** Each contract-by-contract pass has a small unit test (added in Phase 10) demonstrating the spec'd behavior. In-engine sanity: top speed of a known tech matches expectation ±20%, DefensivePerimeter techs face outward, etc.

---

### Phase 8 — Make the training loop actually train

**Goal:** Self-play produces real fitness gradient. CMA-ES reaches every documented tunable.

**Closes:** R2 1.R2-M (LoadLibrary spam + auto-halt), R2 2.R2.H (hyperparameter name collisions — 6 unreachable fields), R2 2.R2.J (PretrainingPipeline does no training), R1 4 (IsLibraryPlateaued first-eval false-positive), the score-asymmetry tail of R1 2.6 (OutcomeScorer Them stats, already partially closed in Phase 7).

**Key changes:**

1. **[Training/TrainingMatch.cs:ApplyHyperParameters](../../Training/TrainingMatch.cs)** — rename keys to fully-qualified form: `"Pathing.WThreat"`, `"Control.CostFunction.WThreat"`, `"Planning.StrategicValueFunction.WThreat"`, etc. Add cases for the 6 previously-unreachable fields. [Training/EvolutionarySearch.cs:HyperParams.DefaultNames](../../Training/EvolutionarySearch.cs) — update to match the FQ names. Dimension increases from 16 to ~22.
2. **[Training/ScenarioGenerator.cs:LoadLibrary](../../Training/ScenarioGenerator.cs)** — log a warning ONCE per name (not per call). When all library scenarios are absent, fall through to procedural scenarios — never produce a null-back-to-caller silently. Plan 5 insight #6: procedural fallback gives plateau detector real data.
3. **[Training/SelfPlayHarness.cs:IsLibraryPlateaued](../../Training/SelfPlayHarness.cs)** — guard against degenerate history: `if (total == 0 || currentRate == 0 && all-history-zero) return false;`. Add minimum-age check: best entry must be older than `PlateauWindow / 2`.
4. **[Training/PretrainingPipeline.cs:Run](../../Training/PretrainingPipeline.cs)** — convert to coroutine driven by `SmartTrainingDriver`. Call `match.SimulateCoroutineFull` (not synchronous `Run`). 1000-iteration loop becomes 1000 real matches with real training.
5. **[OutcomeScorer.cs](../../Training/OutcomeScorer.cs)** — already addressed in Phase 7; in this phase confirm the per-team stats flow from TrainingMatch's event handlers (`OnDamageEvent` populates `ThemDamageDealt`/`ThemDamageTaken`; `OnProjectileEvent` for team 2 increments `ThemShotsFired`).
6. **[Training/TrainingMatch.cs:OnDamageEvent](../../Training/TrainingMatch.cs)** — gate on `HasAttacker` (not `AttackerIfKnown.Value != 0`) per R1 §3.9.
7. **[Training/EvolutionarySearch.cs:LoadCheckpoint](../../Training/EvolutionarySearch.cs)** — persist Mulberry32 state too. Restores per-sample reproducibility.

**Decisions taken:**
- Procedural library fallback over JSON loader (Plan 10 open question #3: "Procedural fallback when JSON missing").
- FQ hyperparameter names over field renames (Plan 7 open question #2).
- Trainer/coroutine pattern over background-thread pretraining (Plan 5 open question #5).

**Dependencies:** Phase 7 (math fixes — CMA-ES rank-μ must be correct before training).

**Risk:** Medium. CMA-ES math validation requires careful test against Hansen reference.

**Success criteria:** Run 20 generations: log shows non-constant `meanScore`. Library evaluation produces non-zero win rate (procedural fallback active). No spurious plateau halt. Hyperparameter writes reach every documented tunable (instrumented log shows all 22 fields receiving non-default values during a search).

---

### Phase 9 — Hot-path GC reduction

**Goal:** Steady-state allocation under 5-tech combat is below 100 KB/s. Smart is sustainable at 30 Hz × N techs.

**Closes:** R2 2.R2.C (allocation hotspots across all subsystems).

Plan 5 insight #4 + Plan 7 insight C: PUCT Dictionary→sparse-array conversion is the single biggest GC win — prioritize first.

**Key changes (priority-ordered by impact):**

1. **[Planning/PUCTSearch.cs](../../Planning/PUCTSearch.cs)** — replace PUCTNode's 5 Dictionaries with a single struct array indexed by action ordinal: `(int actionHash, StrategicPlan plan, int visits, float value, float prior, PUCTNode child)[]` sized to LegalActions max. Closes ~25K Dict allocations per Search. Use a PUCTNode pool (free-list, per-Search scope). Reuse the path List in Iterate.
2. **[Planning/PlanLibrary.cs:LegalActions](../../Planning/PlanLibrary.cs)** — accept caller-supplied output List (no allocation per Expand).
3. **[Planning/StrategicRollout.cs:Simulate](../../Planning/StrategicRollout.cs)** — accept caller-supplied scratch Lists; Clear+repopulate.
4. **[Control/SamplingMPC.cs:Solve](../../Control/SamplingMPC.cs)** — instance scratch `_samples = new ControlVector[N][]` + `_trajectories = new RolloutState[N][]` allocated once and reused. `_weights`, `_optimal` also.
5. **[Control/PhysicsRollout.cs:Simulate](../../Control/PhysicsRollout.cs)** — accept caller-supplied `RolloutState[]` output buffer.
6. **[Control/ContinuousController.cs:OnOperationsTick](../../Control/ContinuousController.cs)** — `_mpcRequestScratch` field. Mutate in place; for the cross-thread handoff, swap a pair.
7. **[World/KalmanUpdate.cs](../../World/KalmanUpdate.cs)** — thread-local scratch `float[7]`/`float[49]` on PerceptionWorker. Allocate fresh only for the published BeliefState's mean/cov.
8. **[Control/WeaponFireController.cs:ComputeFireCommits](../../Control/WeaponFireController.cs)** — all per-weapon-slot arrays become instance fields, grown on demand (same pattern as `_states`).
9. **[World/EventBus.cs:Publish](../../World/EventBus.cs)** — switch handler list to copy-on-write: `volatile Action<TEvent>[] _handlers`. Subscribe/Unsubscribe under lock; Publish lock-free.
10. **[Pathing/PathingService.cs:GetThreatField](../../Pathing/PathingService.cs)** — cache one ThreatField instance per team; rebuild `_snap` in place (volatile read). Static factory delegate cached.
11. **[Pathing/ThreatField.cs:Evaluate](../../Pathing/ThreatField.cs)** — skip sources where `d² > 9·r²` (3σ early-exit). Cache `invR2 = 1/(2r²)` on ThreatSource.
12. **[Coordination/PlanDecomposition.cs](../../Coordination/PlanDecomposition.cs) + [TargetAssignment.cs](../../Coordination/TargetAssignment.cs) + [RoleAssignment.cs](../../Coordination/RoleAssignment.cs)** — caller-supplied scratch dictionaries; Clear+repopulate.
13. **[World/WorldModel.cs:SnapshotPerTechState](../../World/WorldModel.cs)** — eliminate or reuse cached dict.
14. **[Learning models' Evaluate](../../Learning/ActionValueEstimator.cs)** — instance scratch buffers per model. `[ThreadStatic]` or per-instance lock.

**Decisions taken:**
- PUCT pool scope: per-Search (Plan 10 open question #4, A).
- Allocation refactor deliberately deferred to Phase 9 (Plan 7 insight I + Plan 9 insight #6): doing it earlier would conflict with intervening edits.

**Dependencies:** Phases 1-8 (all functional fixes settled before refactoring code paths).

**Risk:** High. Allocation refactors touch every hot path; regressions in correctness are easy to introduce.

**Success criteria:** Profile session shows steady-state allocation < 100 KB/s under 5-tech combat. PUCT search allocates < 200 bytes per Search (down from ~50K alloc / 5 MB).

---

### Phase 10 — Polish, tests, baseline

**Goal:** Smart ships a working baseline binary, has minimal tests, and has the final spec/code drift cleaned up.

**Closes:** Remaining R1 + R2 mediums + lows + infos. Produces the first usable `Smart.PretrainedBaseline.bin`.

**Key changes:**

1. **[Smart.Tests/](../../Smart.Tests/)** — NEW test project (uses `InternalsVisibleTo` added in Phase 2). Minimum: HungarianTest, KalmanFilterStepTest, CMA-ES-vs-Hansen-reference, MLP-backprop-numerical-gradient-check, ProfilePersistence-roundtrip-CRC. ~5 small fixtures.
2. **DRIFT-DECISIONS.md** — NEW doc in `Smart/Docs/`. One entry per resolved spec-vs-code drift from Phase 7, noting decision + rationale.
3. **[World/EventBus.cs](../../World/EventBus.cs)** — mark unproduced event types `[Obsolete("Not produced at v0.1.0 — see WORLD-CONTRACT §5", false)]` per Plan 5 insight #7.
4. **[Threading/ThreadingDiagnostics.cs:RaiseQueueDepthSampled + RaiseWorkerIdle](../../Threading/ThreadingDiagnostics.cs)** — wire from `WorkerPool` (per-1s sampler) and from idle-wait paths.
5. **[Diagnostics/SmartDiagnostics.cs](../../Diagnostics/SmartDiagnostics.cs)** — NEW file. Minimal decision-log ring buffer + `DescribeForDiag()` static snapshot method readable from the debug overlay.
6. **[Vehicle/KinematicTracker.cs](../../Vehicle/KinematicTracker.cs)** — remove `_priorPosition` + `_priorDeltaTime` dead state, OR wire them for outlier detection. Pick removal.
7. **[Coordination/LineOfSight.cs](../../Coordination/LineOfSight.cs)** — add observer-self layer mask to the raycast. Remove the misleading comment.
8. **[Pathing/TrajectoryOptimizer.cs:TerrainPenalty](../../Pathing/TrajectoryOptimizer.cs)** — short-circuit to 0 when terrain is not yet populated. Exempt Hover from the high-altitude penalty.
9. **[Pathing/TrajectoryOptimizer.cs:ApplyPinnedEnds](../../Pathing/TrajectoryOptimizer.cs)** — pin `cps[2]` and `cps[N-3]` to values that make `curve(0) = start`, `curve(1) = goal` exactly under uniform cubic B-spline. Closes the "trajectory's first sample drifts from vehicle" finding.
10. **PretrainingPipeline run** — execute 100-generation CMA-ES + 500-match long-batch with the now-correct system. Save `Smart.PretrainedBaseline.bin`. Embed as resource in the assembly. `LearningService.Init` falls back to embedded resource when no per-player profile exists.
11. **WORKFLOW.md** — update with the final state. Each step now has honest "live" markers reflecting the post-Phase-9 reality.
12. **[XML doc cleanup](#)** — sweep the doc-vs-code drifts from R2 2.R2.I + 3.R2.H. Pick code-correct documentation for each.

**Decisions taken:**
- Test coverage starts small (5 fixtures) — Plan 1 open question #4 minimum.
- Baseline binary saved as embedded resource (not external file) for shipping simplicity.

**Dependencies:** All prior phases.

**Risk:** Low (polish).

**Success criteria:** All tests pass. Baseline binary loads transparently on first run of a fresh install. Smart vs Vanilla in stock scenarios produces a non-zero win rate (target: ≥30% for first baseline; this is an empirical target, not a guarantee). Each contract file's NORMATIVE clauses are either implemented or explicitly `[DEFERRED v0.2]`-marked.

---

## Open questions surfaced (recurring across multiple plans)

These are decisions the planners flagged for human input. Each appeared in 3+ plans.

1. **Harmony patch site for DamageObserved producer.** SHELL-API-GUIDE §8.1 was corrected from `ModuleDamage.DamageInfo` to `ManDamage.DamageInfo`. Needs human verification of the canonical hook in the current TerraTech assembly. Alternatives: (a) `ManDamage.OnDamageDealt` patch if it exists; (b) per-Tank damage callback patch; (c) main-thread polling fallback. Plans 3, 4, 6, 7, 8, 9, 10 all flag this.

2. **`ManSafeSaves.RegisterSaveSystem` signature.** SHELL-API-GUIDE marks OQ-7 resolved with `(assembly, OnSave, OnLoad)`. The exact delegate shapes for `OnSave`/`OnLoad` need confirmation — do they accept a Stream, return bytes, or receive a polymorphic callback? Plans 3, 8 flag this; Plan 8 recommends a quick sample-implementation pass before Phase 3 starts.

3. **`netTech.netId.Value` behavior in single-player.** Is it always 0/null when not networked, or sometimes populated? Phase 5's TechId factory falls back to InstanceID in the null case but if netId is just 0 in SP, the factory needs to distinguish modes. Plans 3, 4, 9 flag this.

4. **Host-handover policy.** Plans 4, 7, 9 list three alternatives:
   - (a) Discard all SmartRuntime state on handover; new host cold-starts.
   - (b) Serialize beliefs+plans over network at handover (substantial engineering).
   - (c) Disable Smart in MP sessions until v0.2.
   Synthesizer picked (a) implicitly via Phase 5's "MP-aware but cold-start" framing. Confirm.

5. **Player-id key for profile path.** ManNetwork.inst.MyPlayer's identity API needs verification on non-Steam platforms (Epic, standalone). Plans 2, 4, 8 flag this. Phase 5 falls back to `"local"` when unresolvable.

6. **Brake semantics (already picked actuator-side but worth flagging).** May produce different feel per chassis (wheel/track/hover/jet). Validate in-engine; reverse if needed.

7. **`TankTeamChangedEvent` existence.** Does TerraTech raise this event natively or do we need a Harmony postfix on the Team setter? Plans 5, 9, 10 flag this. Phase 3 attempts the event subscription first; fallback is Harmony patch.

---

## Findings deliberately deferred to v0.2

These are real R1/R2 findings the synthesizer **chose not to include** in this plan. Each was excluded for a specific reason. Be honest: this is not "we cover everything."

1. **PerceptionWorker covariance-trace-doubling clause (R1 §3.2 medium).** Implemented partially in Phase 7 but not the full criterion. Defer the precise threshold-tuning to v0.2 self-play data.
2. **`TerrainMap.TerrainBiasingShape` wired into BeliefDecay (R1 §3.2 info).** API exposed, consumer not implemented. Pure improvement; not blocking.
3. **PUCT subtree reuse across ticks (R1 §3.6 info).** Plan 9's hot-path GC fix preserves per-Search pooling; cross-tick reuse is a v0.2 perf optimization.
4. **CHOMP analytic gradient (PATHING-CONTRACT §6.2).** Numerical gradient stays; analytic is documented as v0.2.
5. **Smoothness preconditioner (PATHING-CONTRACT §6.3).** Identity stays; M⁻¹ is v0.2.
6. **GRU BPTT (Plan deference matrix).** Dense-head training stays; full BPTT is v0.2.
7. **15 curated library scenario JSON files.** Procedural fallback handles this; JSON loader can land in v0.2.
8. **Scripted baseline opponent (`SimpleScriptedOpponent`).** Phase 8 dispatches Smart-vs-Smart for baseline benchmarks. The scripted opponent is v0.2.
9. **Diagnostics §2-6 full implementation (decision log binary format, replay log, compute-budget allocator, debug viz layers beyond text).** Phase 10 ships a minimal `SmartDiagnostics` + text overlay; the full DIAGNOSTICS-CONTRACT is documented as deferred v0.2 per Plan 2 + 5's recommendation to amend the spec rather than ship 313 lines of paper.
10. **Multi-process parallelism for CMA-ES (TRAINING-CONTRACT §2.5 OPEN).** Sequential by design.
11. **Multi-tech blueprint library + verified weapon-block APIs.** `RawTechPopParams.Default` and `WeaponProfileBuilder` placeholder values remain; verification of TankBlock module APIs is v0.2 (acknowledged in original v0.1.0 honest-gap list).

---

## What this plan does NOT promise

The final state after Phase 10 is:
- ✓ Compiles in all 5 configurations
- ✓ Runs in-engine without TypeInit / NRE / leak
- ✓ MP-aware (clients idle correctly, Time.timeScale guarded, TechId stable)
- ✓ Produces a real (non-Glorot) pretrained baseline shipped as embedded resource
- ✓ Steady-state allocation < 100 KB/s under combat
- ✓ One bad tech doesn't break the shell loop
- ✓ Spec drift documented in DRIFT-DECISIONS.md
- ✓ Minimum test fixtures cover the math kernels
- ✓ Debug overlay shows live system state

It does NOT promise:
- ✗ Smart beats Vanilla at human level (training time + tuning iteration matter; first baseline is "non-Glorot," not "good")
- ✗ Host-handover preserves state (cold-start on the new host is the v0.1 contract)
- ✗ Full DIAGNOSTICS subsystem (text overlay only)
- ✗ Performance-tuned at every hot path (we hit "sustainable," not "optimal")
- ✗ Every WORLD event type produced (only the 6 with live consumers; rest marked Obsolete)
- ✗ JSON library scenarios (procedural fallback only)
- ✗ Full BPTT for the intent classifier (dense-head learning only)

---

## How the planners agreed and disagreed

For transparency, the synthesis details:

**Number of phases across plans:** 8, 8, 10, 11, 12, 9, 10, 8, 12, 11 (median 10).

**Risk consensus:** All plans rate compile fixes as low risk, math fixes as high risk, GC refactor as high risk. The "lifecycle" phase varies between medium and high.

**Unique high-value insights worth preserving from individual planners:**

- **Plan 1 #2:** MobilityProfile sqrt2 fix invalidates any baseline produced before it. Phase ordering (baseline last) reflects this.
- **Plan 1 #10:** SmartTrainingDriver's Update() is the natural place to drain worker-thread WorldEventBus publishes. Closes the "Publish from workers" issue as a side effect.
- **Plan 2 #6:** PerceptionWorker is the single highest-leverage edit. Phase 3 reflects this.
- **Plan 3 #1:** Dropping DoubleBuffer constraint = 4 compile failures in one edit. Picked over wrappers.
- **Plan 3 #6:** LoadLibrary auto-halt FAKES success (training reports "plateau converged"). Phase 8 prioritizes for this reason.
- **Plan 4 #4:** Tuning snapshot mechanism solves R1 Theme B + R2 2.R2.H + CMA-ES wiring with one primitive.
- **Plan 5 #1:** Lambda NaN unrecoverable without explicit reset. Phase 6 puts the floor inside Solve, not just at the writer.
- **Plan 5 #3:** Lifecycle inversion closes 4 critical bugs as side effects.
- **Plan 6 #11:** Spec-vs-code framing — "spec wins when it's design intent; code wins when it's engineering reality."
- **Plan 7 #3:** PathingService → SmartRuntime cycle is hidden critical; breaking it must precede Phase 4.
- **Plan 8 #1:** DoubleBuffer wrapper vs constraint drop = 4× multiplier on edit count.
- **Plan 9 #4:** WorkerPool TryDequeue(out _) is 1-line, outsized impact on test quality.
- **Plan 10 #4:** Hyperparameter name collision may be the actual reason "training doesn't train" — fixing it alone may unblock months of perceived stagnation.

---

END OF FIX-PLAN.md

**Total findings closed by plan:** R1's ~50 + R2's ~80 = ~130 individual findings. Some are deferred (above); some collapse to one root cause (multiple). Reasonable estimate: **~110 of 130 findings closed by Phase 10**. The remaining ~20 are documented as deferred-to-v0.2 with rationale.
