# AUDIT.md

**Source:** Ten parallel reviewers, one per domain, all reading actual code (not specs) and cross-referencing against the contracts.
**Scope:** Every file under `TAC_AI/AI/Forms/Smart/` plus the surrounding integration points.
**Convention:** Findings already on the documented v0.1.0 honest-gap list (block-capture stubs, GRU BPTT, baseline binary, LOS via `Physics.Raycast`, etc.) were excluded by design and are not reported here.

---

## Executive summary

**The project does not currently compile.** Two unrelated compile-time bugs were found, each blocks the whole assembly:

1. `DebugTAC_AI.LogWarning(...)` is called from Threading and other Smart files but **no such method exists** on the existing `DebugTAC_AI` class.
2. `DoubleBuffer<T>` is constrained `where T : class` but Planning + Coordination instantiate `DoubleBuffer<PlanLibrary.StrategicPlan>` against a `readonly struct`.

Beyond compile failures, two **system-level correctness holes** sit on top of code that otherwise would run:

3. `PerceptionWorker` is fully written but **never instantiated or enqueued** — every downstream consumer (Planner, Coordinator, ThreatField, Controller) runs against a permanently-empty world model.
4. `LearningService.Shutdown` saves the profile while trainer workers are still mutating `_params` — **torn weights persisted to disk** on every shutdown.

Then 20+ high-severity bugs span math errors (Kalman gain divides by wrong S diagonal, top-speed has spurious √2, CHOMP threat term averages instead of integrates, sep-CMA-ES post-step σ poisons rank-μ, OutcomeScorer's swap zeroes two of five score components), races (ControlProfile RMW between MPC worker and main, World observation race, terrain nulled before workers cancelled), and contract violations (client overwrites host-replicated TankControl every frame, planners run unconditional CPU work on clients).

Sections below: numbered by severity, grouped by domain. Every finding has a file:line ref. Suggestions are concrete; "investigate further" appears where the right fix needs human judgment.

---

## 1. Critical — Cannot ship until fixed

### 1.1 Compile failure: `DebugTAC_AI.LogWarning` does not exist

`DebugTAC_AI` exposes `Log`, `LogError`, `LogWarnPlayerOnce`, `LogWarnFileOnly`, `WarnTagged`, `Warning(string)` — but **no `LogWarning(string)` overload**. Smart-side call sites:

- [WorkerPool.cs:175, 197](../../Threading/WorkerPool.cs)
- [WorkerLifecycleRegistry.cs:100, 124](../../Threading/WorkerLifecycleRegistry.cs)
- [ThreadingDiagnostics.cs:60, 67](../../Threading/ThreadingDiagnostics.cs)
- All `LearningService` / `TrainingMatch` / `PathingService` / etc. — pattern is repo-wide.

Because `WorkerPool`'s **static ctor** invokes `InstallDefaultHandlers`, the type never finishes initialization, so any reference to `WorkerPool` triggers a TypeInitializationException at runtime even past the compile error.

**Fix:** add `internal static void LogWarning(string)` + an exception-taking overload to `DebugTAC_AI` routing through `UnityEngine.Debug.LogWarning`. OR replace all sites with the existing `DebugTAC_AI.WarnTagged(subsystem, message)`.

### 1.2 Compile failure: `DoubleBuffer<StrategicPlan>` violates `where T : class`

- [StrategicPlanner.cs:15, 18, 23](../../Planning/StrategicPlanner.cs) — declares `DoubleBuffer<PlanLibrary.StrategicPlan>` against a `readonly struct`.
- [Coordinator.cs:22, 30](../../Coordination/Coordinator.cs) — inherits the same break.
- [DoubleBuffer.cs:19](../../Threading/DoubleBuffer.cs) — class constraint `where T : class`.

CS0452. Planning + Coordination do not compile.

**Fix:** introduce a sealed `StrategicPlanBox` wrapper (`public readonly StrategicPlan Value;`) and use `DoubleBuffer<StrategicPlanBox>` everywhere. Update PLANNING-CONTRACT §7.2 example accordingly.

### 1.3 `PerceptionWorker` is never instantiated or enqueued

[PerceptionWorker.cs](../../World/PerceptionWorker.cs) is fully written. `grep "new PerceptionWorker"` returns **zero hits**.

[SmartRuntime.Init](../../SmartRuntime.cs) constructs `WorldModel`, `PathingService`, `LearningService` — but no `PerceptionWorker`. Consequence:

- `WorldModel.FusedBuffer` is initialized to `BeliefSnapshot.Empty` and **never written**.
- Per-tech buffers receive their `NewlyObserved` belief at spawn and **never update**.
- `TeamRuntime.BuildStrategicState` / `BuildTeamSnapshot` see empty beliefs → Planner sees no techs.
- `PathingService.ThreatFieldRebuildLoop` builds empty threat fields.
- `SmartForm.Operations` reads stale spawn-time belief for `OnOperationsTick`.

The entire world→strategy→control pipeline is dead on arrival.

**Fix:** instantiate `PerceptionWorker` in `SmartRuntime.Init` and `Pool.Enqueue(worker.RunLoop)`. Decide host-gating policy at the dispatch boundary (relates to §3.1 below).

### 1.4 `LearningService.Shutdown` races trainer workers — torn profile written to disk

[LearningService.cs:78-91](../../Learning/LearningService.cs)

Shutdown flips `_running`, unsubscribes the damage handler, then calls `SaveProfile → ProfilePersistence.Save → ILearnedModel.StoreParameters`, which `Array.Copy`s each model's `_params` into a temp buffer. The four `TrainerWorker.RunLoop` instances are **not cancelled by Shutdown** — they continue running `_adam.Step(_params, grad)` on pool threads concurrently with the main-thread read. Result: torn weights persisted on every shutdown; the next session loads inconsistent parameters and CRC may still pass.

**Fix:** read from the published `DoubleBuffer` (which is the post-step snapshot) instead of `_params` directly; **or** cancel + join trainer workers before saving.

---

## 2. High — Will produce wrong behavior under common conditions

### 2.1 World — perception math + race

**[KalmanUpdate.cs:155-164](../../World/KalmanUpdate.cs) — Kalman gain divides by wrong S diagonal.**
`K = P·S⁻¹`. With diagonal S, `K[row,col] = P[row,col] / S[col,col]`. The code uses `S[row,row]` instead. On the diagonal (row==col) this is identical, but as soon as `Propagate` introduces P[pos,vel] couplings, every off-diagonal K entry uses the wrong divisor — position innovations fold into velocity corrections with position's S, and vice versa.

Fix: replace `S[row*N+row]` with `S[col*N+col]`, or restrict K to its diagonal under the diagonal-S approximation.

**[WorldModel.cs:79-102](../../World/WorldModel.cs) — observation race.**
Main thread writes `entry.LatestObservation = ...; entry.HasFreshObservation = true;` with no barriers. Worker reads both during Tick, **resets the flag**, and PublishPerTechBelief writes `PriorBelief`. The `PositionObservation` struct (3 Vector3 + 3 floats) is not atomic — worker can see torn payload with stale flag, or freshly-recorded observations can be silently dropped by the reset.

Fix: route observations through a Threading channel (drain in worker), or guard with per-entry lock around (read, decide, reset).

### 2.2 Vehicle — mobility math

**[MobilityProfile.cs:53-56](../../Vehicle/MobilityProfile.cs) — `TurningRadiusAtTopSpeed = topFwd / angularAccel.y` has units `m·s/rad`, not meters.**
Centripetal-limited is `r = v²/a_lat`; angular-velocity-limited is `r = v/ω`. Current formula matches neither.

**[MobilityProfile.cs:47-51](../../Vehicle/MobilityProfile.cs) — Top speed has spurious factor of 2.**
Steady-state quadratic-drag balance gives `v_max = sqrt(a/k)`, not `sqrt(2·a/k)`. All top speeds (forward, back, lateral) inflated by √2. Consumed by RoleAssignment, PlanDecomposition, TacticalOptimizer.

**[SmartForm.cs:97](../../SmartForm.cs) — `maxAccelEstimate` is permanently stale.**
Reads `VehicleBuffer.Read().Mobility.VerticalAuthority` at spawn, before any vehicle rebuild has run. Buffer holds `MobilityProfile.Default` with VerticalAuthority=0, so `maxAccelEstimate` always evaluates to **5.0f** for every tech, ground or air, and never updates. Fed to BeliefState.MaxAccelerationEstimate and consumed by Kalman's Q-noise bound.

Fix: defer `World.RegisterTech` until first vehicle-rebuild, or expose a `World.UpdateMaxAccel(techId, value)` API and call it from the rebuild path. Use horizontal accel max, not vertical authority.

### 2.3 Control — actuator + cost contract violations

**[ContinuousController.cs:201-246](../../Control/ContinuousController.cs) — `_controlProfileBuffer` RMW race.**
`OverlayFireDecisionOntoProfile` (main) and `RunMPCWork` (worker) both read-modify-write the same DoubleBuffer reference non-atomically. Interleave: main reads A, worker reads A, worker writes B with new Steps + old aim, main writes C with old Steps + new aim. **Published C drops worker's fresh Steps.** With H=20 horizon and a busy MPC, dropped publishes are recoverable but produce stale control exactly when freshness matters most.

Fix: split steps and weapon-decision into separate buffers, OR let only RunMPCWork publish Steps and have OnOperationsTick perform overlay synchronously after the worker writes.

**[CostFunction.cs:115-124](../../Control/CostFunction.cs) — `ThreatFieldCost` averages instead of integrating.**
CONTROL-CONTRACT §5.4 mandates `sum over steps`. Implementation returns `total / trajectory.Length`. Averaging makes the threat term magnitude horizon-length-invariant, defeating the W_Threat=2.0 vs W_Reach=1.0 dominance — Smart will not route around enemies even with PATHING fully live.

Fix: drop the `/ trajectory.Length` divide.

**[ContinuousController.cs:297-328](../../Control/ContinuousController.cs) — `OnControlFrame` ignores receding-horizon index + stale-fallback.**
CONTROL-CONTRACT §9.2: `index = clamp(currentTick - profile.ValidFromTick, 0, Steps.Length-1)`. Implementation always reads `Steps[0]` and discards the `currentFrameTick` parameter (called with `0L` from SmartForm). §9.3's hold-last + neutral-after-N-stale-ticks is also missing. Stale MPC publishes apply forever.

**[ContinuousController.cs:297-318](../../Control/ContinuousController.cs) — Brake semantics inconsistent.**
Actuator maps `drive = Throttle - Brake` (reverse when brake held). [PhysicsRollout.cs:75-87](../../Control/PhysicsRollout.cs) treats brake as `(1 - Brake)` thrust multiplier PLUS a `-velocity·Brake·mass·2` damping term. MPC samples search a physics that doesn't match the actuator response — optimal control vector at solve time doesn't behave as predicted.

Fix: pick one semantics. Either clamp `drive = max(0, Throttle)·(1 - Brake) + min(0, Throttle)` at the actuator, or change PhysicsRollout to integrate `fwdAccel = (Throttle - Brake)·accelCapacity` with no separate damping.

**[ContinuousController.cs:297-328](../../Control/ContinuousController.cs) — `OnControlFrame` writes TankControl on client.**
CONTROL-CONTRACT §9.4 says ControlFrame is a no-op on client (engine replicates host's TankControl). Implementation writes `CollectMovementInput(0,0,1,...)` + `FireControl = false` every frame from the initial `Neutral` buffer, **overwriting the host-replicated state on every client tick**. Stuck/non-firing clients in MP.

Fix: gate on host (plumb the flag through, or read `SmartRuntime.IsHost`).

**[WeaponFireController.cs:275-286](../../Control/WeaponFireController.cs) — Aim arc check is yaw-only.**
`IsAimedWithinArc` and [`CostFunction.WeaponAlignmentCost`](../../Control/CostFunction.cs#L158-L165) both compute a yaw-only rotation of weapon forward and compare against `YawArcRadians`. `PitchArcRadians` is never read. Airplane / elevated-target engagements fail the spec's arc semantics.

### 2.4 Planning — non-determinism hazard

**[StrategicRollout.cs:17-26](../../Planning/StrategicRollout.cs) — `(int)(RolloutHorizonSeconds / StepDt)` unguarded.**
Both operands are public-static-mutable tuning fields. CMA-ES write of `StepDt = 0` → `+Infinity` → `(int)+Infinity = int.MinValue` → loop never executes → rollout returns input state unchanged → silent broken value function. Negative StepDt produces silently-zero iterations too.

Fix: clamp `int steps = (StepDt > 0 && RolloutHorizonSeconds > 0) ? Mathf.Max(1, Mathf.RoundToInt(RolloutHorizonSeconds / StepDt)) : 1;`.

### 2.5 Coordination — plan correctness

**[PlanDecomposition.cs:147-153](../../Coordination/PlanDecomposition.cs) — `DefensivePerimeter` stores position-angle as yaw.**
`angle = (i/n)·2π` is the azimuth of the position offset; the outward yaw is `π/2 − α`. Code passes `angle` itself as heading, so techs face the wrong way except at α=π/4 and 5π/4. Spec §6.4 says "Heading outward."

Fix: `float heading = Mathf.PI * 0.5f - angle;` (then normalize to (−π, π]).

**[TargetAssignment.cs:42, 67-101](../../Coordination/TargetAssignment.cs) — `COverlap` declared but never applied.**
COORDINATION-CONTRACT §4.2 makes overlap encourage-for-EngageFocused / discourage-for-EngageDistributed a NORMATIVE term. Implementation has the constant but no overlap term in the cost loop, and the Hungarian solver is strict 1-to-1 — **EngageFocused can never assign more than one tech to its primary target**, defeating the plan's purpose.

Fix: duplicate the primary target as K columns at −CPlan for EngageFocused; add a soft penalty pass for EngageDistributed.

**[TargetAssignment.cs:96-98](../../Coordination/TargetAssignment.cs) — Plan-bias bonus is role-agnostic.**
Spec §4.2 calls for lead-tech bonus + secondary penalty (unless flanker). Code applies `c -= CPlan` to every tech uniformly. Combined with 1-to-1 Hungarian, this just pulls whichever tech is cheapest onto the primary, regardless of role.

### 2.6 Learning + Training — math + lifecycle

**[EvolutionarySearch.cs:218-237](../../Training/EvolutionarySearch.cs) — rank-μ reads post-step σ.**
Line 218 multiplies Sigma[j] by `sigmaMultiplier`. Lines 229+ compute `y = (population[k][j] − Mean[j]) / Sigma[j]` — but Sigma is now **post-multiplier**, not the σ that produced the sample. Standard CMA-ES (Ros-Hansen 2008) requires pre-step σ. Successful-step multipliers (>1) damp rank-μ by 1/multiplier²; unsuccessful (<1) inflate it.

Additionally, `oldVar = Sigma[j]²` at line 232 is the post-multiplier value, so the step-size multiplier is **double-applied to the diagonal C**.

**[EvolutionarySearch.cs:232-235](../../Training/EvolutionarySearch.cs) — C update is multiplicative scale of oldVar.**
`newVar = oldVar · ((1 − c1 − cμ) + c1·p² + cμ·rankMu)` — every term carries `oldVar`. Correct Ros-Hansen form is `C_j ← (1 − c1 − cμ)·C_j + c1·p_σ_j² + cμ·Σ w_k·y_kj²` (no oldVar on the latter two). The rank-update can never add variance independent of current scale; σ effectively just decays multiplicatively.

Fix: snapshot pre-step Sigma, use it for both y-normalization and the additive (not multiplicative) rank-update terms.

**[OutcomeScorer.cs:115-136](../../Training/OutcomeScorer.cs) — `DeriveWinner` swap zeroes HP + shots.**
Both-alive tiebreaker computes opponent score on a `swapped` MatchOutcome that sets `OurStartingHpTotal = 0`, `OurEndHpTotal = 0`, `OurShotsFired = 0`. `FriendlyPreservation` returns 0 (line 103-105 guard); `WeaponEfficiency` returns 0 (line 109 guard). With Preserve=2.0 + Efficiency=0.5 weights, Them is systematically penalized in every time-limit-reached match — Us wins all tiebreaks.

Fix: add `ThemStartingHpTotal`, `ThemEndHpTotal`, `ThemShotsFired` to MatchOutcome; populate from event subscribers; populate the swap from those.

**[TrainingMatch.cs:85-156](../../Training/TrainingMatch.cs) — `StopCoroutine` leaks subscriptions + timescale.**
Cleanup (Unsubscribe at 143-144, Teardown→DisableHeadlessMode) is **after** the while loop, not in finally. External `host.StopCoroutine(handle)` leaves WorldEventBus subscriptions registered and `Time.timeScale` at 15×. On next match the stale subscribers double-fire.

Fix: use a wrapper IEnumerator that pumps the inner one and runs cleanup in `Dispose()` (IEnumerator from a generator implements IDisposable; StopCoroutine triggers Dispose).

### 2.7 Integration — host gating + shutdown ordering

**[SmartRuntime.cs:187-188](../../SmartRuntime.cs) — TeamRuntime starts workers with no host-authority gate.**
`pool.Enqueue(Planner.RunLoop)` + `pool.Enqueue(Coordinator.RunLoop)` run forever doing PUCT search, Hungarian assignment, role assignment, decomposition. No host check at the dispatch boundary or inside the loops. PathingService.ThreatFieldRebuildLoop + PathSolveLoop have the same issue. ARCHITECTURE §3.2 explicitly forbids substantive work on host==false. Clients spin on CPU work they never use.

Fix: gate per-tick body on `volatile bool SmartRuntime.IsHost`. When false, sleep without working.

**[SmartRuntime.cs:362-383](../../SmartRuntime.cs) — `PathingService.Shutdown` nulls `_terrain` before `CancelAllAndJoin`.**
PathSolveLoop reads `_terrain` and passes it to `TrajectoryOptimizer.Solve` → `TerrainPenalty` calls `terrain.IsTraversable` + `terrain.HeightAt` without null guard. Window between Shutdown setting null and worker observing cancellation → NRE in the optimizer.

Fix: reorder — CancelAllAndJoin first, then PathingService.Shutdown.

**[SmartRuntime.cs:357-358](../../SmartRuntime.cs) — Profile dir doubles "SmartAI".**
SmartRuntime: `modDir = <cwd>/Mods/SmartAI`. LearningService: `_profileDir = Path.Combine(modDir, "SmartAI", "Profiles")`. Final path: `<cwd>/Mods/SmartAI/SmartAI/Profiles`. Spec wants `<mod-dir>/SmartAI/Profiles/...` once. External tools shipping baseline binaries to spec path won't find anything.

Fix: drop the trailing `SmartAI` from SmartRuntime's modDir.

---

## 3. Medium — Will produce wrong behavior under uncommon conditions, or clear smells

### 3.1 Threading + cross-thread discipline

- **[WorkerLifecycleRegistry.cs:11-23](../../Threading/WorkerLifecycleRegistry.cs)** — `WorkerHandle` missing the permanent-termination fallback callback mandated by THREADING-CONTRACT §6.2/§7.1. Subsystems cannot register per-worker fallbacks as the contract requires.
- **[ThreadingDiagnostics.cs:36, 43-46](../../Threading/ThreadingDiagnostics.cs)** — `_defaultHandlersInstalled` is a plain bool guarding subscription; two threads racing `InstallDefaultHandlers` both subscribe → every diagnostic logs twice. Use `Interlocked.CompareExchange`.
- **[WorkerLifecycleRegistry.cs:81-150](../../Threading/WorkerLifecycleRegistry.cs)** — `CancelAllAndJoin` does not dispose any CTS; SmartRuntime relies on Pool.Dispose to handle that, but the contract says CancelAllAndJoin is self-contained. Any future caller invoking only CancelAllAndJoin leaks every WorkerHandle's CTS + the root CTS.
- **[BoundedQueue.cs:49-86](../../Threading/BoundedQueue.cs)** — `ApproximateCount` can transiently observe negative values; the over-capacity check can fire when the queue is actually below capacity, drop-evicting legitimate items.
- **[CancellationHelpers.cs:22-25](../../Threading/CancellationHelpers.cs)** — `CreateLinked()` with zero parents returns a CTS linked to nothing — silent cancellation-discipline failure.

### 3.2 World

- **[BeliefState.cs:145-148](../../World/BeliefState.cs)** — Initial intent prior is uniform 1/6 across all categories; WORLD-CONTRACT §2.2 specifies team-conditioned prior (uniform over Aggressing/Retreating/Flanking/Holding for hostiles; biased toward Holding/Idle for allies/neutrals).
- **[WorldModel.cs:41-49](../../World/WorldModel.cs)** — `HasFreshObservation` bool read across threads without volatile or barriers. On ARM, worker may see stale false long after main set true.
- **[PerceptionWorker.cs:96-103](../../World/PerceptionWorker.cs)** — `HasSignificantChange` ignores the covariance-trace-doubling clause the contract specifies; only position delta is checked.
- **[BeliefDecay.cs:40-46](../../World/BeliefDecay.cs)** + **[KalmanUpdate.cs:208-213](../../World/KalmanUpdate.cs)** — `DampVelocity` / `ApplyDamageObservation` construct returned BeliefState with shared array references (`prior.CovArrayInternal`, etc.). Future code mutating either side silently corrupts the other.

### 3.3 Vehicle

- **[ThrustMap.cs:82-86](../../Vehicle/ThrustMap.cs)** — `MaxAngularAccel` maps Z-axis linear acceleration to roll (physically wrong — Z is forward, roll comes from Y/X offsets), uses linear m/s² as rad/s², and `Min()` of asymmetric thrust collapses to the weaker side.
- **[ArmorMap.cs:5-20, 56-121](../../Vehicle/ArmorMap.cs)** — `ArmorQueryResult.WeakestPointLocal` missing; `QueryRegion(Bounds)` not implemented per VEHICLE-CONTRACT §6.4.

### 3.4 Control

- **[WeaponFireController.cs:314-331](../../Control/WeaponFireController.cs)** — `_energyReserveFraction` is strictly monotonic-down with no refill. After ~50 firing ticks reserve hits 0 and all energy weapons silently suppress forever.
- **[ContinuousController.cs:266-273](../../Control/ContinuousController.cs)** — `ShiftLeft` holds the final element instead of seeding zero, biasing trajectory tail toward whatever the last-ever solve produced.
- **[SamplingMPC.cs:38-41](../../Control/SamplingMPC.cs)** — RNG is time-seeded (`new Random()`) — defeats self-play determinism + replay.
- **[PhysicsRollout.cs:75-87](../../Control/PhysicsRollout.cs)** — Brake force double-applied (thrust multiplier AND velocity damper).
- **[ManeuverLibrary.cs:1-75](../../Control/ManeuverLibrary.cs)** — ManeuverLibrary primitives are never referenced anywhere. CONTROL-CONTRACT §3.4/§6.5 warm-start reset is unimplemented.
- **[ContinuousController.cs:30-55](../../Control/ContinuousController.cs)** — `ControlProfile.WeaponFireCommits` is per-profile; CONTROL-CONTRACT §9.1 puts it per-`ControlVector`. Disagreement.
- **[WeaponFireController.cs:246-265](../../Control/WeaponFireController.cs)** — `PerWeaponState.CurrentTargetSlot` is an **index** into the per-tick belief list; dict enumeration order isn't stable, so slot 3 may point to a different tech tick-over-tick. Store `TechId` not index.
- **[TacticalOptimizer.cs:60-88](../../Control/TacticalOptimizer.cs)** — `LearningRate = 0.5` (spec default is 0.1); per-tick deltas of ~5m of goal movement can oscillate around the optimum.
- **[ContinuousController.cs:266-273](../../Control/ContinuousController.cs)** — Single-pending-slot drops are silent; no diagnostic when `Interlocked.Exchange` returns non-null prior.

### 3.5 Pathing

- **[TrajectoryOptimizer.cs:240-252](../../Pathing/TrajectoryOptimizer.cs)** — `TerrainPenalty` doesn't check `IsPopulated`; unpopulated grid treats ground as y=0, producing spurious underground penalty for any path with y<-0.5 before the first refresh (~10s after Init, longer in regions outside the 2km cache).
- **[TrajectoryOptimizer.cs:227-237, 267-280](../../Pathing/TrajectoryOptimizer.cs)** — No cost term anchors `curve(0)` to `start`. For uniform cubic B-spline, `P(0) = (cps[0] + 4·cps[1] + cps[2])/6`. `cps[2]` is free (`firstFree = PinnedPerEnd = 2`), so optimizer can move it anywhere lowering threat/smooth/length cost. Trajectory's first sample can be far from the vehicle's actual position.

### 3.6 Planning

- **[StrategicPlanner.cs:29-53](../../Planning/StrategicPlanner.cs)** — Low-confidence plan handling (PLANNING-CONTRACT §2.7) unimplemented. 51%-vs-49% splits will flip plan tick-over-tick, exactly the thrash the contract forbids.
- **[StrategicState.cs:51-74](../../Planning/StrategicState.cs)** — `GetHashCode` overridden without `Equals` (CS0659). Hash is currently dead code; transposition tables would silently break.
- **[PlanLibrary.cs:58](../../Planning/PlanLibrary.cs)** — `EngageDistributed` gated on `nHostile > 0`, but PLANNING-CONTRACT §6.3 only lists EngageFocused/Flank/Bait as hostile-gated.
- **[StrategicValueFunction.cs:61-78](../../Planning/StrategicValueFunction.cs)** — `ThreatExposure` divides by `N_friendly · N_hostile` instead of meaning over friendlies. Under-represents exposure when many hostiles are in range of one friendly.

### 3.7 Coordination

- **[PlanDecomposition.cs:164-176](../../Coordination/PlanDecomposition.cs)** — `MobileScreen` uses `plan.Scalar` (m/s) as a meters offset; per-tech base positions prevent formation convergence.
- **[PlanDecomposition.cs:210-230](../../Coordination/PlanDecomposition.cs)** — `Bait` sends every non-bait flanker to the same exact point.
- **[PlanDecomposition.cs:116-138](../../Coordination/PlanDecomposition.cs)** — `Flank` ignores roles; Pursuers and Holders both swing wide instead of pursuing / holding.
- **[PlanDecomposition.cs:179-193](../../Coordination/PlanDecomposition.cs)** — `FightingRetreat` paces each tech at its own top speed; spec §6.6 says slowest tech sets the pace.
- **[Coordinator.cs:60-78](../../Coordination/Coordinator.cs)** — Unified `CoordinationState { TargetMap, RoleMap, LOSCoverage, ActivePlan }` is never published. RoleMap is computed but only used internally; Diagnostics + other consumers have no access.
- **[TargetAssignment.cs:67-102](../../Coordination/TargetAssignment.cs)** — Cost matrix reads `vehicle.Kinematics.PositionWorld` from VehicleBuffer (refreshed every 30 ticks ≈ 1s stale). The fresher KinematicBuffer isn't consulted.
- **[LineOfSight.cs:27-49](../../Coordination/LineOfSight.cs)** — Raycast origin is inside observer's own colliders with no self-mask. Once wired, every enemy reports occluded.

### 3.8 Learning

- **[ActionValueEstimator.cs:81-93](../../Learning/ActionValueEstimator.cs)** (+ ResidualModel, ThreatModel, IntentClassifier) — Every `Evaluate` allocates fresh forward-pass buffers per call. LEARNING-CONTRACT §4.4 explicitly mandates allocation-free inference. IntentClassifier allocates 90 arrays per inference call (3 per timestep × 30).
- **All four models — `TrainOneMinibatch` processes partial batches.** Spec §4.2 step 1 says "Wait for queue to reach minibatch size; drain 32." Implementation drains 1..32 and trains on whatever arrives — effectively multiplies learning rate by 1/n vs full-batch.

### 3.9 Learning persistence + Training

- **[TrainingMatch.cs:212-213](../../Training/TrainingMatch.cs)** — `OnDamageEvent` gates on `AttackerIfKnown.Value != 0` instead of the dedicated `HasAttacker` bool. Sentinel-value collisions misclassify damage.
- **[LearningService.cs:70-72, 187-202](../../Learning/LearningService.cs)** — `DamageObserved` has subscribers but **no producer publishes it anywhere in the repo**. The threat-training pipeline that step 1.10 advertises as the v0.1.0 live path is silently dormant.
- **[TrainingMatch.cs:100-115](../../Training/TrainingMatch.cs)** — Try/catch wraps two field assignments that cannot throw; the real risk surface (CountAliveAndHp, event handlers) is inside the loop body with no protection.
- **[ProfilePersistence.cs:100-101](../../Learning/ProfilePersistence.cs)** — `File.Delete` then `File.Move` on Windows leaves a window where readers see no file. Use `File.Replace` for atomic NTFS replace.
- **[PretrainingPipeline.cs:69-75](../../Training/PretrainingPipeline.cs)** — Reads `LearningService.Intent` etc. statically; NRE if called before `LearningService.Init`.

### 3.10 Integration

- **[SmartForm.cs:122-142](../../SmartForm.cs)** — `Operations` runs `TickMainThread` (writes KinematicBuffer/VehicleBuffer) and `PathingService.MainThreadTick` on the client. ARCHITECTURE §3.2 forbids mutating world model from observation on host==false.
- **[SmartForm.cs:101-110](../../SmartForm.cs)** — `OnTechRecycle` / `Operations` silently no-op on FormState type-mismatch. ARCHITECTURE §4 E5 requires loud failure.
- **[SmartRuntime.cs:346-360](../../SmartRuntime.cs)** — All long-running workers (planner, coordinator, threat-field, path-solve, trainers) start at Init with no host check at dispatch boundary. Spec §3.2 says the check goes at the dispatch boundary — boundary doesn't exist for these loops.
- **[ContinuousController.cs:322-327](../../Control/ContinuousController.cs)** — Writes `FireControl = false` every frame from the initial `Neutral` buffer before any real MPC publish, stomping any other component's intent.
- **[SmartForm.cs:56-99](../../SmartForm.cs)** — `OnTechSpawn` assigns `helper.FormState = state` (line 72) before completing `teamRuntime.RegisterTech`, `World.RegisterTech`, etc. Partial-failure in any later step leaves the helper holding a partially-registered state.

---

## 4. Low — Concrete impact but minor

Grouped briefly with file refs only; descriptions in agent reports.

**Threading:** [WorkerPool worker loop has no outer catch](../../Threading/WorkerPool.cs), [`_disposed` not volatile](../../Threading/WorkerPool.cs), [EnqueueRequestAndCompute consumes request post-cancellation](../../Threading/MarshallingPatterns.cs), [DoubleBuffer<float[]> in Learning models violates immutability discipline](../../Learning/ActionValueEstimator.cs).

**World:** [`WrapAngle*` use unbounded while loops (NaN/large-angle hazard)](../../World/KalmanUpdate.cs), [process-noise heading variance uses linear-accel proxy (dimensionally wrong)](../../World/KalmanUpdate.cs), [PerceptionWorker comment says "continue" but catch re-throws + no watchdog](../../World/PerceptionWorker.cs), [`BeliefSnapshot.Empty` is a mutable Dictionary cast as IReadOnlyDictionary](../../World/BeliefState.cs).

**Vehicle:** [`QueryWeakFace` skips zero-HP cells (will miss real holes once HP plumbed)](../../Vehicle/ArmorMap.cs), [Inertia tensor computed but never consumed](../../Vehicle/MassDistribution.cs), [`_priorPosition`/`_priorDeltaTime` are dead state](../../Vehicle/KinematicTracker.cs).

**Control:** [`_states` grows monotonically with peak weapon count](../../Control/WeaponFireController.cs), [Box-Muller discards second normal](../../Control/SamplingMPC.cs), [`WeaponAlignmentCost` drops local-Y forward component](../../Control/CostFunction.cs), [props/jets gating has no hysteresis](../../Control/ContinuousController.cs).

**Pathing:** [Hover gets spurious high-altitude penalty](../../Pathing/TrajectoryOptimizer.cs), [Trajectory N<4 throws IndexOutOfRange](../../Pathing/TrajectoryOptimizer.cs), [`SamplePoints=1` divides by zero](../../Pathing/TrajectoryOptimizer.cs), [`NormalAt` does 4 independent buffer reads + can mix old/new heights](../../Pathing/TerrainMap.cs).

**Planning:** [Public-static mutable tuning fields read without barriers (race with CMA-ES writes)](../../Planning/StrategicRollout.cs), [`ActionEquals/ActionHash` collapse plans by Type only, dropping Vector/Scalar parameters](../../Planning/PlanLibrary.cs), [Seed-rollout action chosen by Dictionary enumeration order (non-deterministic)](../../Planning/PUCTSearch.cs).

**Coordination:** [EngageFocused heading uses toTarget from current pos, not from flank goal](../../Coordination/PlanDecomposition.cs), [Disengage produces zero velocity if already at rendezvous](../../Coordination/PlanDecomposition.cs), [Duplicate `previousAssignments.TryGetValue` per row](../../Coordination/TargetAssignment.cs), [`NeedsScout` NREs on null `losCoverage`](../../Coordination/RoleAssignment.cs).

**Learning:** [xorshift32 stuck-at-zero if seed becomes 0](../../Learning/OnlineTrainer.cs), [LoadParameters mutates `_params` without synchronization](../../Learning/ActionValueEstimator.cs), [Unnecessary `dPost2` allocation](../../Learning/ActionValueEstimator.cs), [`BackprogateMlp` typo (should be Backpropagate)](../../Learning/ActionValueEstimator.cs).

**Training:** [`LoadCheckpoint` discards RNG state — partial reproducibility](../../Training/EvolutionarySearch.cs), [`SaveCheckpoint` has no snapshot-before-write](../../Training/EvolutionarySearch.cs), [`IsLibraryPlateaued` returns true on the first new-high library evaluation](../../Training/SelfPlayHarness.cs), [`_modifiedSinceLoad` plain bool (no volatile)](../../Learning/LearningService.cs), [Population=1 produces +Inf/NaN](../../Training/EvolutionarySearch.cs).

**Integration:** [SmartMovementController DriveDirector* leave EControlCoreSet bus stale on client](../../SmartMovementController.cs), [Empty teams accumulate idle workers (TechCount→0 never reaped)](../../SmartRuntime.cs).

---

## 5. Info — Observations, not bugs

- World — `Publish<TEvent>` signature drops the `in` modifier the contract specifies (functionally equivalent for structs; `Action<T>` can't take `in`).
- World — `BeliefDecay.IsLost` interprets "position variance > 200 m²" as trace > 600 (3 axes averaged); ambiguous in spec.
- Pathing — `ThreatField` LOS doc comment claims "constant 1.0 at v0.1.0" but raycast IS active. Comment is stale.
- Pathing — `TerrainBiasingShape` ignores its `spreadRadius` parameter.
- Pathing — Convergence test uses Euclidean gradient norm, can hide per-CP outliers.
- Planning — PUCT stores `sum of values` and divides on read instead of contract's incremental-mean form (algebraically equivalent).
- Coordination — Hungarian solver (classical potentials + shortest-augmenting-path) verified correct.
- Learning — `Evaluate` returns variable named `logits` that's actually post-softmax (cosmetic).
- Learning persistence — Section count hardcoded to 4; future schema-2 with 5 sections would silently drop the 5th.

---

## Cross-cutting themes

Several patterns recur across domains; addressing them would close multiple findings:

### Theme A: Host-authority gating is missing at the dispatch boundary

ARCHITECTURE §3.2 says workers may run on client but **must not receive substantive work**. Smart starts ALL long-running workers (planner, coordinator, threat-field, path-solve, four trainers, perception if it gets wired) at `Init` with **no host check anywhere on the dispatch path**. Result: clients spin doing real CPU work permanently, and ControlFrame writes neutral commands every frame overwriting host-replicated state.

Closes: 2.3 OnControlFrame on client, 2.7 TeamRuntime no gate, 2.7 PathingService no gate, 3.10 Operations runs on client, 3.10 SmartRuntime workers start without host gate.

**Fix shape:** add `internal static volatile bool IsHost` on SmartRuntime, set from `SmartForm.Operations(helper, host)`. Every long-running RunLoop checks this at the top of each iteration and sleeps when false. ControlFrame early-returns when false.

### Theme B: Public-static mutable tuning fields are read without barriers

The const→static loosening for CMA-ES wiring introduced **24+ public-static-mutable fields** (cost weights, MPC sample count, PUCT NodeBudget, StrategicRollout horizon, etc.) read from worker threads without `Volatile.Read` or snapshot capture. Today the only writer is the harness (rare), so the race is mostly latent — but the JIT is free to hoist these reads, and a mid-tick write produces observably inconsistent values inside one search/rollout/solve.

Closes: 2.4 StepDt divide, 3.6 ThreatExposure, and lots of low-severity Planning entries.

**Fix shape:** publish a `PlannerTuningSnapshot` / `ControlTuningSnapshot` via `DoubleBuffer<...>`. Capture once per outer-loop iteration. Removes the JIT-hoisting concern and gives CMA-ES a clean "publish new snapshot" affordance.

### Theme C: Spec-vs-code drift cluster around §5/§6/§9 of multiple contracts

The contracts often look implemented but skip NORMATIVE sub-clauses:

- World §2.2 team-conditioned intent prior → uniform 1/6
- Control §5.4 sum (integral) → mean
- Control §9.2 receding-horizon index + §9.3 stale-fallback → always Steps[0]
- Control §9.4 client no-op → writes anyway
- Control §10.6 lead-vs-secondary plan bias → uniform
- Planning §2.7 low-confidence handling → unconditional publish
- Planning §6.3 EngageDistributed gating → also gated even though spec doesn't say to
- Coordination §4.2 COverlap term → missing entirely
- Coordination §6.4 outward heading → angle stored as yaw
- Coordination §6.6 slowest-tech pace → per-tech top speed
- Coordination §6.5 shared formation anchor → per-tech anchor
- Coordination §6.8 distributed flank positions → all the same point
- Learning §4.2 wait-for-full-minibatch → trains on partial
- Learning §4.4 allocation-free inference → allocates per call
- Learning §6.1 atomic rename → Delete-then-Move (non-atomic on Windows)
- Threading §6.2/§7.1 per-handle termination callback → no callback field

These are not v0.1.0 honest gaps — they're authoring drift. Either the code changes to match the spec, or the spec is amended to match what the code actually does. Each entry is a small concrete decision.

### Theme D: Lifecycle ordering is fragile

`SmartRuntime.Shutdown` orders: `LearningService.Shutdown` → `PathingService.Shutdown` → `CancelAllAndJoin` → `Pool.Dispose` → `World.Clear`. Three issues:

1. `LearningService.Shutdown` saves while trainers still run (Critical 1.4)
2. `PathingService.Shutdown` nulls `_terrain` while path-solve still runs (High 2.7)
3. Theme A: no startup ordering for host-gate visibility

**Fix shape:** invert. Cancel-and-join FIRST (everything stops), then each subsystem's Shutdown is a synchronous cleanup of already-quiesced state.

---

## How to use this document

The findings are deliberately fine-grained — many "high" entries are 2-line fixes. The expected path:

1. **Resolve Critical 1.1 + 1.2 first.** Nothing else can be tested until the project compiles.
2. **Resolve Critical 1.3 (PerceptionWorker not wired).** Without it everything downstream is degenerate; in-engine testing would be misleading.
3. **Resolve Critical 1.4 (Shutdown race).** Profiles must persist correctly or every cycle starts from junk.
4. **Theme A pass — Host gating.** Single touch across SmartRuntime + each long-running loop; closes ~6 issues at once.
5. **Theme C pass — Contract drift.** Go through one contract at a time; decide spec-vs-code per clause.
6. Address remaining High then Medium per-domain.

Reviewers were intentionally unaware of each other's findings; minor duplications across domains are real signals (an issue showing up in two reviews is twice as likely to be real). Where one reviewer marked something low and another marked the same thing high, take the higher reading.

---

## Audit metadata

- 10 parallel reviewers, one per domain.
- ~917k tokens consumed by reviewers.
- 312 tool uses (mostly file reads + grep).
- Run duration: 8.5 minutes wall clock.
- Spec docs consulted: every CONTRACT.md in `Smart/Docs/` plus ARCHITECTURE.md and WORKFLOW.md.

END OF AUDIT.md
