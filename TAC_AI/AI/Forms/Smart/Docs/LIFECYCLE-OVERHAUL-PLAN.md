# LIFECYCLE-OVERHAUL-PLAN

Single-commit, BEST-not-simplest overhaul of Smart's lifecycle surface. Synthesised from 10
sibling-subsystem design reports, then revised against a 3-verifier audit pass. 4,677 LOC across
85 implementation items (80 production + 5 verifier-discovered, see §9), 60 test entries,
~38 source files.

---

## 1. Executive summary

The Smart form's lifecycle is silently brittle. FMOD's `OnApplicationPauseManual True`
Thread.Aborts every WorkerPool and daemon thread; ApplicationQuit never reaches
`SmartRuntime.Shutdown` because `AIFormRegistry.Clear()` zeros `active` without calling
`DeInitGlobal`; per-tech routing through `DelayedSubscribe` silently drops 8 of 12 attract
techs; `OnTechRecycle` leaks per-tech state into six sidecars; trainer workers park on
`!IsHost` mid-minibatch and lose unflushed events on host migration; profile saves only fire
on game-save or clean Shutdown (neither happens on FMOD pause); world reset splices stale
terrain with fresh threat snapshots under live workers; the path-request backpressure system
is structurally untestable (no producer sets `Priority != 128`); `TeamRuntime` instances
accumulate monotonically for the entire session; and there is no per-thread liveness signal
anywhere — `SmartRuntime.IsRunning` returns `true` while every worker is dead.

Scope: 10 subsystems, one push. Every item is **ESSENTIAL** unless tagged OPTIONAL. No "v0.3"
deferments. Total LOC budget: **4,677**. Per-subsystem budgets: Thread-Abort 473, Unity
Lifecycle 203, Per-Tech Routing 275, Orphan Cleanup 645, Trainer-Host 438, Profile
Save/Load 740, Terrain Reset 570, PathBackpressure 393, Team Lifecycle 370, Worker Watchdog 570.

**BEST-not-simplest philosophy:** every subsystem rejected a 1-line fix in favour of a
defense-in-depth solution that (a) closes the failure today, (b) locks the invariant with a
SMART_DEV test, (c) emits an operator-visible structured log on every state transition, and
(d) survives the next refactor by encoding the contract in code rather than in PR review.

**Single-commit discipline:** items are ordered so the build is never red mid-implementation.
Interfaces and registries land before consumers; consumers land before producers; tests land
last.

---

## 2. Cross-subsystem dependency graph + topological order

Global IDs are renumbered `L-001..L-085` (L-001..L-080 from the 10 reviewer reports + L-081..L-085
added by the verifier audit, see §9). Wave assignment is the topological layer (Wave 0 = no
deps, Wave N = depends on Wave <N). Within a wave, items can land in any order — between waves,
strict ordering required.

### Wave 0 — pure additions, no internal deps
| Global | Origin ID | Subsystem | Title |
|---|---|---|---|
| L-001 | TAC-1 | thread-abort | AbortGuard helper with storm-detection |
| L-002 | TAC-6 | thread-abort | Expose `WorkerLifecycleRegistry.IsTearingDown` |
| L-003 | TAC-7 | thread-abort | Add `TerminationReason.AbortStorm` enum value |
| L-004 | UL-2 | unity-lifecycle | `SmartRuntime.IsPaused` + `RequestShutdown` latch |
| L-005 | UL-4 | unity-lifecycle | `IsBackground=true` invariant lock + `SnapshotLive()` re-add |
| L-006 | UL-5 | unity-lifecycle | `AIFormRegistry.Clear()` calls `active.DeInitGlobal()` |
| L-007 | UL-6 | unity-lifecycle | SmartForm Install/Uninstall shim (unconditional) |
| L-008 | TR-1 | per-tech-routing | `RoutingDecision` struct + field on `TankAIHelper` |
| L-009 | AC-1 | orphan-cleanup | `ITechSidecar` + `TechLifecycleRegistry` |
| L-010 | AC-5 | orphan-cleanup | `SmartEventBridge.DetachPerTank(TechId)` shadow map |
| L-011 | AC-6 | orphan-cleanup | Orphan-sweep TeamId recovery (Team field + walk) |
| L-012 | TH-1 | trainer-host | `HostChanged` event + `HostAuthority` phase enum |
| L-013 | PS-3 | profile-save | `ILearnedModel.FlushPendingForPersist` + `SaveMutex` |
| L-014 | PS-4 | profile-save | Migration registry + `[SmartMigration]` attribute |
| L-015 | PS-6 | profile-save | Two-deep backup ring + `[PROFILE-LOAD-FAIL]` log |
| L-016 | WR-1 | terrain-reset | `IWorldResettable` + `WorldResetRegistry` |
| L-017 | PRB-1 | path-backpressure | `EnqueueMonoTimestamp` on `PathRequest` |
| L-018 | PRB-2 | path-backpressure | `PathRequestBackpressure` promoted to PathingService static |
| L-019 | TL-1 | team-lifecycle | `TeamRuntime` lifecycle FSM + per-team RW-lock |
| L-020 | WHW-1 | worker-watchdog | `WorkerLifecycleRegistry.SnapshotLive()` accessor |
| L-021 | WHW-4 | worker-watchdog | `WorkerPool.EnqueueLongRunning` returns name |

### Wave 1 — depend only on Wave 0
| Global | Origin ID | Subsystem | Title | Deps |
|---|---|---|---|---|
| L-022 | TAC-2 | thread-abort | `WorkerPool.WorkerLoop` outer-catch TAE handling | L-001, L-003 |
| L-023 | TAC-3 | thread-abort | `EnqueueLongRunning` self-respawning supervisor | L-001 |
| L-024 | TAC-4 | thread-abort | Daemon RunLoop explicit TAE catch (×8 sites) | L-001 |
| L-025 | UL-1 | unity-lifecycle | `SmartLifecycleShim` MonoBehaviour | L-004 |
| L-026 | UL-3 | unity-lifecycle | `IsPaused` gate added to 9 daemons | L-004 |
| L-027 | TR-2 | per-tech-routing | `AIFormRegistry.RouteTech` funnel + `LiveRoutings` | L-008 |
| L-028 | AC-2 | orphan-cleanup | Wire 6 existing sidecars into registry | L-009 |
| L-029 | AC-3 | orphan-cleanup | Wire Coordinator + PathingService as sidecars | L-009 |
| L-030 | TH-2 | trainer-host | `HostAuthorityCoordinator` (edge detect + debounce + publish) | L-012 |
| L-031 | TH-7 | trainer-host | `TrainerBarrier` registry (CountdownEvent) | L-012 |
| L-032 | PS-7 | profile-save | `OnEngineSave` adds uniform `[PROFILE-MANUAL-SAVE]` log | L-015 |
| L-033 | WR-2 | terrain-reset | Atomic `TerrainPublication` holder swap | L-016 |
| L-034 | WR-4 | terrain-reset | `PathRequestBackpressure` WorldResetRegistry hook | L-016, L-018 |
| L-035 | WR-5 | terrain-reset | `TerrainMap.IsFreshlyAllocated` flag | L-016, L-033 |
| L-036 | WR-6 | terrain-reset | `WorldResetting`/`WorldResetCompleted` events | L-016 |
| L-037 | PRB-4 | path-backpressure | Hysteresis + `[PATHING-SHED]`/`[PATHING-SHED-CLEAR]` edge logs | L-018 |
| L-038 | PRB-5 | path-backpressure | `DrawPathingDebugGUI` queue/shed/p50/p99 | L-018 |
| L-039 | PRB-6 | path-backpressure | `smart.runtime.status` pathing block | L-018 |
| L-040 | PRB-7 | path-backpressure | `[PATHING-DEPTH]` 60s histogram log | L-018 |
| L-041 | PRB-8 | path-backpressure | Per-solve latency reservoir (p50/p99) | L-018 |
| L-042 | PRB-10 | path-backpressure | `GetLastPathFresh` + `Trajectory.SolvedAtMono` | L-017 |
| L-043 | TL-2 | team-lifecycle | `MigrateTech` atomic two-team operation | L-019 |
| L-044 | TL-3 | team-lifecycle | `TeamReaperDaemon` with grace period | L-019, L-023, L-061 |
| L-045 | TL-4 | team-lifecycle | Daemon iteration honours lifecycle state | L-019 |
| L-046 | TL-8 | team-lifecycle | Coordinator hard-stop on disposal mid-StepOnce | L-019, L-045 |
| L-047 | WHW-2 | worker-watchdog | `WorkerHealthMonitor` class | L-020 |
| L-048 | WHW-6 | worker-watchdog | `WorkerPool.ReplaceDeadWorkers()` | L-020 |
| L-085 | PRB-3 | path-backpressure | Dequeue-side expiry + `ExpiredCount` | L-017, L-018 |

### Wave 2 — depend on Wave 1
| Global | Origin ID | Subsystem | Title | Deps |
|---|---|---|---|---|
| L-049 | TAC-5 | thread-abort | `DaemonWatchdog` canonical-roster respawn | L-002, L-022, L-023 |
| L-050 | TR-3 | per-tech-routing | Split `DelayedSubscribe` try-block + route via funnel | L-027 |
| L-051 | TR-4 | per-tech-routing | `OnPreUpdate` reclaim path | L-027, L-008 |
| L-052 | TR-5 | per-tech-routing | `Recycled()`/`Subscribe()` reset symmetry | L-027 |
| L-053 | TR-6 | per-tech-routing | `AIFormRegistry.DrawRoutingDebugGUI` | L-027 |
| L-054 | TR-8 | per-tech-routing | `SetActive`: drain + re-route every live helper | L-007, L-027 |
| L-055 | AC-4 | orphan-cleanup | Refactor `SmartRuntime.Deregister` to use registry | L-009, L-028, L-029, L-010, L-011 |
| L-056 | AC-8 | orphan-cleanup | `TechLeakWatchdog` daemon (60s cadence) | L-009, L-023, L-028, L-029 |
| L-057 | TH-3 | trainer-host | `SmartForm.Operations:382` calls `HostAuthorityCoordinator.Notify` | L-030 |
| L-058 | TH-4 | trainer-host | `TrainerWorker` FSM (Active/Pausing/Paused/Resuming) | L-012, L-030, L-031 |
| L-059 | TH-6 | trainer-host | `SmartRuntime.AcceptingTrainingEvents` + producer gate | L-030 |
| L-060 | PS-1 | profile-save | Periodic autosave worker (30s dirty-only) | L-022, L-023 |
| L-061 | WR-3 | terrain-reset | `PathingService.ForgetTeam` from EventBridge + orphan-sweep | L-033 |
| L-062 | PRB-9 | path-backpressure | `GlobalPlannerDaemon` Priority=64 exploration probe | L-017, L-018, L-037 |
| L-063 | TL-5 | team-lifecycle | TeamId reuse fresh-state guarantee | L-019, L-044 |
| L-064 | TL-6 | team-lifecycle | `AetherTuning` entries for grace period + cadence | L-044 |
| L-065 | TL-7 | team-lifecycle | Lifetime counters surfaced in `DrawPathingDebugGUI` | L-019, L-043, L-044 |
| L-066 | WHW-3 | worker-watchdog | `SmartRuntime.Init` registers daemons with monitor | L-047, L-021 |
| L-067 | WHW-11 | worker-watchdog | `Shutdown` calls `WorkerHealthMonitor.BeginShutdown` | L-002, L-047 |
| L-081 | TR-7 | per-tech-routing | `RoutingCompletenessCatcher.MainThreadTick` (promoted) | L-027, L-051 |
| L-082 | TR-9 | per-tech-routing | Remove `[TEMP DIAGNOSTIC]` block (promoted) | L-027, L-053 |
| L-083 | AC-7 | orphan-cleanup | Delete duplicate fan-out in `WorldModel.DeregisterTech` (promoted) | L-055 |

### Wave 3 — depend on Wave 2
| Global | Origin ID | Subsystem | Title | Deps |
|---|---|---|---|---|
| L-068 | WR-7 | terrain-reset | `PathingResetTests.cs` (6 tests, SMART_DEV) | L-016, L-033, L-061, L-034, L-035, L-036 |
| L-069 | WR-8 | terrain-reset | `SmartForm.OnWorldReset` collapses to `WorldResetRegistry.ResetAll` | L-016, L-036 |
| L-070 | TH-5 | trainer-host | `LearningService.OnHostLost`/`OnHostGained` (Save + Reload) | L-012, L-058, L-013 |
| L-071 | TH-9 | trainer-host | Holding-buffer TTL discard (5 min default) | L-058 |
| L-072 | WHW-5 | worker-watchdog | `SmartForm.Operations` calls `WorkerHealthMonitor.Tick()` | L-047, L-066 |
| L-073 | WHW-7 | worker-watchdog | `DrawPathingDebugGUI` "Workers: live/expected" line | L-047 |
| L-074 | WHW-8 | worker-watchdog | `smart.workers.list` console command | L-020, L-047 |
| L-075 | WHW-9 | worker-watchdog | `smart.workers.respawn` console command | L-047, L-048 |
| L-076 | WHW-10 | worker-watchdog | `smart.runtime.status` surfaces backpressure/spikes/CB trips | L-018, L-047 |

### Wave 4 — test-only (SMART_DEV), depend on everything they verify
| Global | Origin ID | Subsystem | Title | Deps |
|---|---|---|---|---|
| L-077 | TAC-8 | thread-abort | `AbortSurvivalTests.cs` (6 tests) | L-001, L-002, L-003, L-022, L-023, L-024, L-049 |
| L-078 | PS-2 | profile-save | Three-headed quit save (`Singleton.ApplicationQuitEvent` + `ProcessExit` + `UnhandledException`) | L-013, L-022 |
| L-079 | PS-5 | profile-save | TLV per-section body format (schema 3) | L-014 |
| L-080 | PS-8 | profile-save | `ProfileSelfTest.Run` on Init | L-013, L-014, L-079 |
| L-084 | AC-9 | orphan-cleanup | `ASSERT_TECH_DEREGISTERED` tests (promoted) | L-009, L-010, L-028, L-029, L-055 |

> Note: L-077 LOC dwarf other test items; the test files appear in Wave 4 alongside late
> production deps. PS-2 (quit save) is reordered to Wave 4 because it consumes the matured
> `FlushPendingForPersist`+`SaveMutex` pair from L-013 plus the absorbed-abort behaviour from
> L-022. PS-5 (TLV) and PS-8 (selftest) require PS-4's registry (L-014).

Renumbering note: L-072..L-080 follow continuously from the wave-3 table; the table breaks above
are visual only. L-081..L-085 are verifier-discovered items added in §9 (TR-7→L-081
RoutingCompletenessCatcher, TR-9→L-082 [TEMP DIAGNOSTIC] removal, AC-7→L-083 WorldModel
duplicate fan-out delete, AC-9→L-084 ASSERT_TECH_DEREGISTERED tests, PRB-3→L-085 dequeue-side
expiry). Final total = **85 items**.

> Item-count audit: thread-abort 8 + unity-lifecycle 6 + per-tech-routing 9 (+TR-7,TR-9
> promoted) + orphan-cleanup 9 (+AC-7,AC-9 promoted) + trainer-host 9 + profile-save 8 +
> terrain-reset 8 + path-backpressure 10 (+PRB-3 promoted) + team-lifecycle 8 + worker-watchdog
> 11 = **85 items** total after promoting the 5 previously-unnumbered cross-dep rows to global
> IDs (see §9). All ship in one commit.

---

## 3. Per-subsystem detail

### 3.1 Thread-abort cascade survival

**Symptom.** FMOD `OnApplicationPauseManual True` triggers Mono Thread.Abort on every worker
and daemon. 22 WorkerPool threads die UnexpectedExit; 9 long-running daemons (AetherFuser,
GlobalPlanner, GlobalCoordinator, ThreatField, PathSolve, 4 Trainers) die silently with TAE
auto-rethrown past every `catch (Exception)`. Every Smart-driven tech is permanently DOA on
resume.

**Root cause.** Three layered defects: (1) `WorkerPool.WorkerLoop` TAE catch is inside the
work-dispatch branch only — the idle SpinWait/Sleep(0) branch is unprotected; (2)
`EnqueueLongRunning` wraps the lambda in one try/catch that ResetsAbort then returns — the
dedicated thread terminates permanently after a single abort; (3) every daemon RunLoop catches
only `Exception`/`OperationCanceledException`, so per ECMA-335 §12.4.2.5 the CLR
auto-rethrows TAE past every non-matching catch.

**Best fix.** Layered defense: centralised `AbortGuard` with storm detection (8/10s trips);
`WorkerLoop` outer try/catch wraps BOTH work and idle branches with `OperationCanceled→
ThreadAbortException→Exception` order; `EnqueueLongRunning` becomes a self-respawning
supervisor; every daemon RunLoop gets explicit TAE catch wrapping both tick and
`WaitHandle.WaitOne`; `DaemonWatchdog` with 9-name canonical roster scans the registry every
1s and respawns missing daemons (5-respawn cap); new `TerminationReason.AbortStorm`
distinguishes storm exit from worker fault; SMART_DEV test suite injects real `Thread.Abort`
to lock invariants.

**Rationale.** Simplest fix (sprinkle TAE catch in 7 RunLoops) addresses layer 3 only; leaves
WorkerPool idle-branch hole and `EnqueueLongRunning`'s permanent-exit defect intact, and
provides no upper bound on retry. The BEST fix locks all three layers and adds an auto-recovery
surface that the storm-trip exit can use; ships in one commit and survives both FMOD-cascade
and future deterministic daemon bugs.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| Sprinkle TAE catch + ResetAbort in 7 RunLoops | Layer 3 only; WorkerPool idle branch + EnqueueLongRunning permanent-exit still die on next pause |
| `for(;;)` outer retry, no AbortGuard helper | No storm cap; bug-induced abort storm tight-loops, burns a core |
| `AppDomain.UnhandledException` global ResetAbort | Mono 2018.4 does not raise UnhandledException for TAE in background threads (verified by repro); cannot ResetAbort from other thread |
| Harmony-patch `Thread.Abort` globally | Bypasses Unity shutdown signaling; deadlocks `CancelAllAndJoin`; hostile cross-cut |
| Treat TAE same as Exception, share retry budget | CLR auto-rethrows TAE past `catch (Exception)` before retryCount increments; cannot work |
| Catch TAE only at WorkerPool boundary | Daemons run on dedicated threads outside WorkerLoop; they die regardless |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-001 | AbortGuard helper | `Smart/Threading/AbortGuard.cs` (new) | 90 | — | `Absorb` returns `Continue` or `ExitForRespawn`; storm threshold 8/10s; per-worker ConcurrentDictionary state; never throws | Delete file + revert call sites to inline `ResetAbort+continue` |
| L-002 | Expose `IsTearingDown` | `Smart/Threading/WorkerLifecycleRegistry.cs:33-38` | 6 | — | Public read-only property over private `_cancelAllInProgress` (insert adjacent to field declaration) | Make property private; DaemonWatchdog must hold its own flag |
| L-003 | `TerminationReason.AbortStorm` enum | `Smart/Threading/ThreadingDiagnostics.cs:8-16` | 3 | — | Default handler routes `AbortStorm` to `[WORKER-ABORT-STORM]` | Revert enum; fall back to `RetryBudgetExhausted` |
| L-022 | `WorkerLoop` outer TAE catch | `Smart/Threading/WorkerPool.cs:168-245` | 25 | L-001, L-003 | Outer catch order `OCE→TAE→Exception`; idle branch protected; `AbortStorm` set on storm; range covers full WorkerLoop method body (opening `try` through method close-brace) | Revert WorkerLoop diff |
| L-023 | `EnqueueLongRunning` supervisor | `Smart/Threading/WorkerPool.cs:122-157` | 35 | L-001 | Re-invokes `work(token)` on TAE; breaks on OCE/Exception; storm trip breaks (watchdog re-spawns). RISK: re-entering `work()` restarts the RunLoop's outer `while` shell; mid-iteration state inside `work()` (Adam `_M/_V` partial step, AetherFuser `_lastBelief` mid-fuse, Coordinator `_previousAssignments` mid-reassign) is left in whatever state TAE interrupted. L-058 TrainerWorker FSM mitigates trainer case; other RunLoops treat themselves as idempotent across mid-iteration TAE — verify by code-review pass when each RunLoop's L-024 catch lands | Revert lambda to single try/catch return |
| L-024 | Daemon RunLoop TAE catches (×8) | `Smart/World/AetherFuser.cs:197-218`; `Smart/Learning/OnlineTrainer.cs:281-300`; `Smart/Planning/GlobalPlannerDaemon.cs:31-58`; `Smart/Coordination/GlobalCoordinatorDaemon.cs:26-53`; `Smart/Pathing/PathingService.cs:181-209`; `Smart/Pathing/PathingService.cs:227-285`; `Smart/Coordination/Coordinator.cs:128-141`; `Smart/Planning/StrategicPlanner.cs:59-72` | 64 | L-001 | Catch order `OCE→TAE→Exception`. Restructure each RunLoop to a single outer `try` whose body covers the entire iteration (host-gate `WaitOne` + tick + post-tick `WaitOne`); re-throw OCE inside `catch(OCE)` by `return` (preserves current shutdown semantics). 8 sites: AetherFuser, OnlineTrainer (TrainerWorker.RunLoop — covers 4 instances), GlobalPlannerDaemon, GlobalCoordinatorDaemon, PathingService.ThreatField, PathingService.PathSolve, Coordinator.RunLoop, StrategicPlanner.RunLoop. Names match registry handles | Revert each catch insertion; daemons fall back to L-023 supervisor |
| L-049 | `DaemonWatchdog` canonical roster | `Smart/Threading/DaemonWatchdog.cs` (new); `Smart/SmartRuntime.cs:696-707`; `Smart/SmartForm.cs:386` (hook site) | 110 | L-002, L-022, L-023 | 9-name canonical roster — LITERAL strings matching `EnqueueLongRunning` `label` parameters: `AetherFuser`, `GlobalPlanner`, `GlobalCoordinator`, `ThreatFieldRebuild`, `PathSolve`, `Trainer-Intent`, `Trainer-ActionValue`, `Trainer-Residual`, `Trainer-Threat`. 1000ms debounced scan via `Interlocked.CompareExchange` on `_lastDaemonWatchdogTickMs` (mirror of L-072's `WorkerHealthMonitor.Tick` pattern at SmartForm.cs:386); 5-respawn-per-session cap; skips when `WorkerLifecycleRegistry.IsTearingDown` (L-002 property) | Comment out `RegisterCanonical`+`Scan` calls |
| L-077 | `AbortSurvivalTests.cs` | `Smart/Tests/AbortSurvivalTests.cs` (new); `Smart/Tests/SmartTestSuite.cs:32-50` | 140 | L-001, L-002, L-003, L-022, L-023, L-024, L-049 | 6 tests: WorkerPool absorbs abort; LongRunning respawns; AbortGuard trips on storm; DaemonWatchdog respawns; daemon survives abort during `WaitOne`; no canonical daemon missing after 10 aborts. SMART_DEV-gated | Remove tests from `RunAll` list |

**Tests:** see L-077; 6 named asserts in `AbortSurvivalTests.cs`.

**Risks.** Self-respawning supervisor (L-023) could mask a deterministic bug — mitigated by
re-entering only on TAE; `Exception`/`OCE` break. `Thread.ResetAbort` may throw on unusual
Mono paths — mitigated by AbortGuard wrapping in own try/catch; watchdog won't respawn during
`IsTearingDown`. Storm threshold may fire false positives — per-worker counter; Unity's pause
delivers one abort/worker/pause-event, so 8 in 10s requires 8 distinct pause events. Adding
TAE catch before `Exception` changes semantics — verified no Smart code relies on TAE
auto-rethrow (grep clean); locked by L-077 tests.

**Cross-subsystem deps:** L-049 must coordinate with `WorkerLifecycleRegistry.IsTearingDown`
(L-002); `[WORKER-ABORT-ABSORBED]`/`[WORKER-ABORT-STORM]`/`[DAEMON-RESPAWN]` tags should be
registered with diagnostics subsystem; CircuitBreaker failure budget must remain independent
from AbortGuard storm counter; `ScanAndRespawn` must hook from `SmartForm.Operations` via a
`CompareExchange(ref _lastDaemonWatchdogTickMs)` 1000ms interval gate (mirror of the
`WorkerHealthMonitor.Tick` pattern at SmartForm.cs:386 in L-072). Note: there is no
`SmartForm.MainThreadTick` or `SmartRuntime.OnMainThreadTick` surface today — Operations is
the canonical per-frame main-thread entry point.

---

### 3.2 Unity lifecycle hooks + invariant locks

**Symptom.** Three latent failures: (a) real app-quit never reaches `SmartRuntime.Shutdown`
because `AIFormRegistry.Clear()` zeros `active` without calling `DeInitGlobal` (OD-10); (b)
Mono Thread.Aborts on pause despite `IsBackground=true`; existing TAE catches only protect
inner work delegates, not the outer dequeue/wait frames; (c) future refactor that drops
`IsBackground=true` silently regresses with no test or assert.

**Root cause.** Smart treats Unity process-lifecycle as out-of-scope; the only defense is
`IsBackground=true` (a soft Mono convention — Mono still Thread.Aborts background threads on
pause). No subscription to `Application.quitting` or `Singleton.ApplicationQuitEvent`. No
asserting test that locks `IsBackground=true`. `AIFormRegistry.Clear()` documented as broken
since OD-10 with no fix.

**Best fix.** `SmartLifecycleShim` MonoBehaviour (DontDestroyOnLoad, unconditional — NOT
SMART_DEV-gated) subscribes both `Application.quitting` AND `Singleton.ApplicationQuitEvent`,
fires `SmartRuntime.RequestShutdown()` via `Interlocked.CompareExchange` latch; implements
`OnApplicationPause(bool)` + `OnApplicationFocus(bool)` setting `SmartRuntime.IsPaused` so all
9 daemons gate on `IsPaused` BEFORE the host-gate; implements `OnDestroy()` as third
fallback. Lock `IsBackground=true` with `Debug.Assert` before both `thread.Start()` sites plus
`SmartTestSuite.Threads_AllBackground_AfterInit` enumerating `WorkerLifecycleRegistry.SnapshotLive()`.
Close OD-10 in `AIFormRegistry.Clear()` with `try { active?.DeInitGlobal(); } catch`.

**Rationale.** Simplest fix is the OD-10 one-liner alone. Rejected because: addresses 1 of 3
modes; leaves pause-storm fully unaddressed (more frequent than quit); leaves `IsBackground`
invariant unlocked. The four interlocking pieces ship as one commit (two new files, four
touch-point edits). Belt-AND-suspenders: assume Unity fires pause+abort+quit in any order
multiple times on multiple threads — `CompareExchange` latch makes order irrelevant.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| OD-10 one-liner only | Addresses 1 of 3 modes; pause-storm + IsBackground regression remain |
| Harmony-patch `ManSafeSaves`/`AIModuleBootstrap` | Smart-specific shell coupling; strictly worse than the one-line shell fix |
| Subscribe `Application.quitting` only | Late/missing on hard-crash/task-manager-kill |
| Subscribe `Singleton.ApplicationQuitEvent` only | If Smart is mod-unloaded before Singleton fires, event never reaches Smart |
| Combine `IsHost && !IsPaused` into one flag | Loses independent log observability; pause and host are independent events |
| `Debug.Assert` only, skip test | Assert is no-op in release; test catches regression at next test-run boundary regardless |
| SMART_DEV-gate the shim | Lifecycle correctness is required in release; gating re-exhibits cascade |
| `Application.lowMemory`/`unloading` instead of `quitting` | Wrong events |
| Inline shim into SmartForm | `OnApplicationPause`/`OnDestroy` are Unity messages requiring MonoBehaviour |
| `lock(_shutdownLock)` instead of `Interlocked` | Holds calling thread for ~2s in quit path; CompareExchange returns immediately for loser |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-004 | `IsPaused` + `RequestShutdown` latch | `Smart/SmartRuntime.cs:607,734` | 25 | — | `volatile bool IsPaused` adjacent to `IsHost`; `RequestShutdown` uses `Interlocked.CompareExchange` to LOG winner/loser distinction (subsequent calls observably no-op), then delegates to existing `Shutdown()` — Shutdown's `Pool == null` guard at SmartRuntime.cs:735 remains the actual idempotency mechanism | Remove field+method; downstream becomes no-op |
| L-005 | `IsBackground` assert + test + `SnapshotLive` | `Smart/Threading/WorkerPool.cs:89,156`; `Smart/Threading/WorkerLifecycleRegistry.cs:64`; `Smart/Tests/SmartTestSuite.cs:36` | 35 | — | `Debug.Assert(thread.IsBackground)` at the line of each `thread.Start()` call (lines 89 in ctor, 156 in EnqueueLongRunning); test enumerates registry asserting `IsBackground==true`. Accessor name = **`SnapshotLive()`** (matching L-020 which owns the watchdog architecture). XML doc names BOTH `WorkerHealthMonitor` and `SmartTestSuite.Threads_AllBackground_AfterInit` as consumers (not "sole caller"). For pool workers `WorkerHandle.Name` = `Smart#N` (no `SmartWorker-` prefix); for long-running threads `Name` = `SmartLR-label-counter`. Test regex must accept both shapes | Remove asserts+test; delete `SnapshotLive()` |
| L-006 | `Clear()` calls `DeInitGlobal()` | `Smart/../AIFormRegistry.cs:75` | 8 | — | `try{active?.DeInitGlobal();}catch{}` runs BEFORE `forms.Clear(); active=null;`; log line confirms. Note: `SetActive` at AIFormRegistry.cs:69 ALSO calls `active?.DeInitGlobal()` on form swap; Smart's `DeInitGlobal` must remain idempotent for the SetActive→Clear sequence at shutdown to be safe (already true per §3.2 risks below) | Single 8-line revert |
| L-007 | SmartForm Install/Uninstall (unconditional) | `Smart/SmartForm.cs:56,123` | 10 | L-025 | Install on the line preceding `SmartRuntime.Init();` (currently line 57) — line 56; Uninstall on the line following `SmartRuntime.Shutdown();` (currently line 122) — line 123. BOTH OUTSIDE `#if SMART_DEV` (NOT at the `#endif` lines :81/:95 which were the previous incorrect cite). Install BEFORE Init; Uninstall AFTER Shutdown; both wrapped in try/catch. Install must be idempotent on AIFormRegistry.SetActive re-entry (form swap fires DeInitGlobal→InitGlobal — check DontDestroyOnLoad GameObject existence) | Remove the two call lines |
| L-025 | `SmartLifecycleShim` MonoBehaviour | `Smart/SmartLifecycleShim.cs` (new) | 95 | L-004 | DontDestroyOnLoad; subscribes `Application.quitting`+`Singleton.ApplicationQuitEvent`; implements `OnApplicationPause`/`Focus`/`OnDestroy`; logs each Shutdown source. Install ordering enforced by L-007 (must precede SmartRuntime.Init) | Delete file + remove Install call |
| L-026 | `IsPaused` gate in 6 daemon-class sites (covers 9 runtime threads) | `Smart/World/AetherFuser.cs:197`; `Smart/Planning/GlobalPlannerDaemon.cs:31`; `Smart/Coordination/GlobalCoordinatorDaemon.cs:26`; `Smart/Pathing/PathingService.cs:181,227`; `Smart/Learning/OnlineTrainer.cs:281` | 30 | L-004 | Daemon checks `IsPaused` FIRST then `IsHost`; uses `cancellation.WaitHandle.WaitOne(periodMs)`; pattern identical across sites. Insert immediately BEFORE the existing `if (!SmartRuntime.IsHost)` line at each cited location. Note: OnlineTrainer.cs:281 is the TrainerWorker.RunLoop body — one textual edit covers 4 runtime trainer instances (`Trainer-Intent`, `Trainer-ActionValue`, `Trainer-Residual`, `Trainer-Threat`) all sharing the same RunLoop class | Remove the inserted blocks |

**Tests:** `Threads_AllBackground_AfterInit`, `Shutdown_Idempotent_OnRepeatRequestShutdown`,
`IsPaused_StopsDaemonWork`, `AIFormRegistry_Clear_RunsDeInit` (all in `SmartTestSuite.cs`).

**Risks.** `Singleton.ApplicationQuitEvent` thread-affinity is undocumented — mitigated by
`RequestShutdown`'s `Interlocked.CompareExchange`. `OnApplicationPause` may fire before
`InitGlobal` installed the shim — Install at SmartForm.cs:56 (before SmartRuntime.Init) closes
the order-of-operations concern; existing TAE catch absorbs the abort as last resort.
Re-added `SnapshotLive` may be cleaned-up again — xml-doc names `WorkerHealthMonitor` and
`SmartTestSuite` as its two canonical consumers. Static
`Application.quitting` could leak SmartForm instances — shim subscribes static method (no
closure), Uninstall unsubscribes both. Main-thread pause handler racing daemon locks — only
sets volatile flag, no lock acquisition. `Clear()` now blocks 2s on mod-reload —
documented; existing log line explains stall. Vanilla form `DeInitGlobal` is no-op; Smart's
is idempotent.

**Cross-subsystem deps:** L-026's `IsPaused` gate is the upstream fix for the cascade; the
TAE catches (Section 3.1) become last-resort. L-005 depends on re-adding `SnapshotLive()`
(SAME accessor exposed by L-020 in §3.10 — coordinated naming). L-004's `RequestShutdown` is
a thin CompareExchange wrapper that logs winner/loser then delegates to the unchanged
existing `Shutdown` (idempotent via `Pool == null` guard). L-006 is a shell-side edit
(`AIFormRegistry.cs`) — confirm project policy permits it; FORM-SPECIFICATION.md:350
recommends, SHELL-API-GUIDE OQ-12 marks fix pending.

---

### 3.3 Per-tech `OnTechSpawn` routing

**Symptom.** Of ~12 attract techs spawning in a session, only 4 reach `SmartForm.OnTechSpawn`;
the other 8+ (Hawkeye Assault, Bullfrog, Vertu experimental O, Vertu pyro, Thermal RRat, TINY
TANKS, Maikro, …) silently route to Modified despite Smart being active. They all get
`CreateMovementController` but never `OnTechSpawn`; they run Modified's brain forever and the
operator has no in-game signal of which form claimed which tech.

**Root cause.** Three converging failures in `Subscribe() → Invoke(DelayedSubscribe, 0.1f) →
OnTechSpawn`: (1) `DelayedSubscribe` wraps `ExecuteAutoSetNoCalibrate`+`SetDriverType`+
`OnTechSpawn` in a single try-catch (NRE | MissingReferenceException) — any NRE in
`HandlingDetermine`/`BD.HasWings`/`ModuleWing.m_Aerofoils` iteration fires the catch and
`OnTechSpawn` is never called; (2) `MonoBehaviour.Invoke` silently dropped if `enabled=false`
between schedule and fire (FMOD-pause, `Pool.Return` during world fade); (3) `Subscribe()`
defers `OnTechSpawn` by 100ms but `GetHelperInsured` can swap the active form during that
window so the eventual `OnTechSpawn` runs against the new active form.

**Best fix.** Explicit, observable, atomic routing decision: `RoutingDecision` struct on
`TankAIHelper` stamped exactly once by `AIFormRegistry.RouteTech(helper, reason)` funnel.
`DelayedSubscribe` splits HandlingDetermine vs OnTechSpawn into separate try-blocks; failures
fall back to Modified explicitly. `OnPreUpdate` reclaim path covers dropped Invokes. `LiveRoutings`
ConcurrentDictionary feeds an IMGUI overlay. `RoutingCompletenessCatcher` walks `ManTechs`
every 5s and screams (and self-heals) on any live tech with `FormId==null`.

**Rationale.** Simplest fix is the existing try/catch — and it IS the bug. "Just call
OnTechSpawn from Subscribe()" fails because `tank.blockman` isn't ready yet. The BEST fix
accepts the delay is load-bearing, the exception path is hostile, Unity's Invoke is lossy,
and the active-form-swap race is real. Addresses all four by making the decision observable
+ atomic + reclaimable + isolated. Single commit; watchdog catcher prevents regression.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| Move `OnTechSpawn` into `Subscribe()` | `tank.blockman` not populated; NRE on every spawn |
| Coroutine instead of Invoke | StartCoroutine on disabled MB throws; needs separate dispatcher GameObject |
| Call from `TankAIManager.OnTankAddition` | `OnTankAddition` fires before RawTech blockman hydration completes |
| Catch all and always proceed to OnTechSpawn | Shell-side `SetupDefaultMovementAIController` + downstream alignment FSM/RunState handling read `DriverType`; running with `DriverType=AutoSet` leaves the helper with a default controller and wrong movement classification (the identity classifier itself reads vehicle.Mobility/Weapons/BaseTerrain, not DriverType) |
| Stamp active form at Subscribe + lock | Solves race 3 only |
| Single guard log at OnTechSpawn entry | Already exists; output_log.txt proves OnTechSpawn never called for missing techs |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-008 | `RoutingDecision` struct + field | `AI/TankAIHelper.cs:93` | 18 | — | After DelayedSubscribe (success or failure), `helper.Routing.FormId != null`; fields `FormId`/`TimestampMs`/`Reason`/`ExceptionType`/`ExceptionMessage` | Delete struct + field |
| L-027 | `RouteTech` funnel + `LiveRoutings` | `AI/Forms/AIFormRegistry.cs` (append after SetActive at L73) | 55 | L-008 | Idempotent (second call logs no-op via FormId==ActiveId fast-path — this funnel-level guard is the actual protection against double-OnTechSpawn, NOT each form's `OnTechSpawn` impl); per-funnel try/catch around `Active.OnTechSpawn`; on exception stamps **`Reason=OnTechSpawnFailed`** (matches L-053 color-code key) then falls back to `DefaultFormId`; `LiveRoutings` is `ConcurrentDictionary<int,RoutingDecision>` keyed by `InstanceID`; always logs `[TECH-ROUTE]` via `DebugTAC_AI.LogWarnFileOnly` (silent, per-key dedup, file-only — matches user AI-warning-routing-preference) | Remove funnel; restore direct `Active?.OnTechSpawn` |
| L-050 | Split DelayedSubscribe try-block | `AI/TankAIHelper.cs:780-822` | 30 | L-027 | HandlingDetermine in own inner try; on failure `DriverType=Tank` AND `RouteTech(reason=HandlingDetermineFailed)`; outer catch widens to Exception; OnTechSpawn moved INTO funnel | Restore single try-catch + direct call |
| L-051 | `OnPreUpdate` reclaim | `AI/TankAIHelper.cs` | 20 | L-027, L-008 | If `Routing.FormId==null && tank.blockman!=null && enabled`, call `RouteTech(Reclaimed)`; one null compare per OnPreUpdate; idempotent. Per-frame cap: `Time.frameCount`-keyed static counter caps reclaims at 3 techs per frame; counter resets each new frame; bumped LOC 12→20 covers the counter | Delete the hook + cap |
| L-052 | `Recycled()`/`Subscribe()` reset symmetry | `AI/TankAIHelper.cs:879-912, 750-779` | 10 | L-027 | Pool reuse starts with `FormId=null` so reclaim can fire; Recycled removes from `LiveRoutings`; Subscribe stamps `Reason=Subscribed` with `FormId=null`+`Timestamp=Now` | Drop reset/remove calls |
| L-053 | `DrawRoutingDebugGUI` | `AI/Forms/AIFormRegistry.Debug.cs` (new partial-class file — add `partial` keyword to declaration in `AIFormRegistry.cs:15`); `Smart/SmartForm.cs:575-615`; **register new .cs in TAC_AI.csproj** (old-style csproj policy) | 65 | L-027 | Scrollable IMGUI list of every `LiveRoutings` entry: Tech/Form/Reason/Age/ExceptionType; per-FormId counts at top; color-codes Modified-fallback yellow, OnTechSpawnFailed red; invoked from `DrawPathingDebugGUI` | Remove GUI methods + revert partial split |
| L-054 | `SetActive` drain + re-route | `AI/Forms/AIFormRegistry.cs:58-73` | 25 | L-007, L-027 | Teardown sequence: (1) iterate live helpers via `AIECore.IterateAllHelpers()` (mirror pattern at SmartForm.cs:104) and call OLD form's `OnTechRecycle` directly; (2) set ActiveId; (3) for each live helper, `RouteTech(FormSwapped)` into NEW form via funnel. After form swap every live helper has `Routing.FormId==ActiveId`. SmartEventBridge.Install/Uninstall (L-007) ordering: shim must be wired before step (3) so new form receives DamageObserved on-spawn. Funnel idempotency check (FormId==ActiveId) prevents double-OnTechSpawn even though Modified's impl is not internally idempotent | Revert SetActive |
| L-081 | `RoutingCompletenessCatcher.MainThreadTick` | `Smart/Tests/RoutingCompletenessCatcher.cs` (new) | 70 | L-027, L-051 | Walks ManTechs every 5s; asserts `FormId!=null` OR `age<1s`; uses `DebugTAC_AI.LogWarnPlayerOncePerKey` (popup once-per-tank, the self-heal warning surface) with `[ROUTING-ORPHAN]`; self-heals via `RouteTech(Reclaimed)`; default-ON | Delete file + hook line |
| L-082 | Remove `[TEMP DIAGNOSTIC]` block | `Smart/SmartForm.cs:171-188` | -18 | L-027, L-053 | Subsumed by unified `[TECH-ROUTE]` log; `IsRunning=false` becomes funnel fallback decision | Restore diagnostic block |

> L-081 (TR-7) and L-082 (TR-9) were originally unnumbered cross-dep rows; promoted to global
> IDs in the verifier pass (§9) so the wave-topological tooling can sequence them. Both ship in
> the same commit chunk as L-053; they cannot land independently of L-053 in any case.

**Tests:** `RoutingCompletenessTest`, `RouteTech_Idempotent`,
`RouteTech_OnTechSpawnThrows_FallsBackToModified`,
`DelayedSubscribe_HandlingDetermineThrows_StillRoutes`,
`OnPreUpdate_ReclaimsDroppedInvoke`, `FormSwap_RoutesAllLiveHelpersToNewForm`,
`RoutingCompletenessCatcher_Selfheals`, `RoutingOverlay_RendersWithoutAllocation`.

**Risks.** OnPreUpdate reclaim could race a still-pending DelayedSubscribe — funnel is
idempotent. `LiveRoutings` keyed by `GetInstanceID` could collide on same-frame Unity recycling
— `Recycled()` explicitly removes the entry. Modified's `OnTechSpawn` could be called twice on
form-swap — protection comes from the FUNNEL's idempotency check (`Routing.FormId==ActiveId`
fast-path returns no-op), NOT from `ModifiedForm.OnTechSpawn`'s implementation (which is NOT
internally idempotent — `ModifiedForm.cs:34` unconditionally re-allocates `ModifiedTechState`,
dropping `combatCyclePhase01` and `LastWeapCheck`). OnTechSpawn cost (~5-30 ms) × same-frame
reclaim — cap reclaim work per-frame at 3 techs via `Time.frameCount`-keyed static counter
(budgeted in L-051's 20 LOC). Cross-sub: threading-lifecycle fix eliminates FMOD-pause window
so TR-4 reclaim becomes lighter but still needed for HandlingDetermine-throws case. GUI
namespace pollution in `AIFormRegistry.cs` — place `DrawRoutingDebugGUI` in
`AIFormRegistry.Debug.cs` partial-class file (requires adding `partial` keyword at
AIFormRegistry.cs:15 + csproj registration). Operator-facing log spam — routine `[TECH-ROUTE]`
uses `DebugTAC_AI.LogWarnFileOnly` (silent, per-key dedup, file-only); `[ROUTING-ORPHAN]`
self-heal warning uses `DebugTAC_AI.LogWarnPlayerOncePerKey`.

**Cross-subsystem deps:** threading-lifecycle, smart-runtime IsRunning gate (needs stable
`IsRunning` getter + Restart hook), identity-classifier robustness (reduces how often we fall
back to Modified), smart-event-bridge race (L-054 form-swap drain must coordinate with
`SmartEventBridge.Install/Uninstall`).

---

### 3.4 OnTechRecycle + orphan-cleanup fan-out

**Symptom.** After tech destruction, per-tech state survives in one or more sidecars causing
worker-thread NRE on next iteration: (1) `SmartEventBridge._damageHandlers` leaks Tank refs
because the orphan-sweep path in `SmartRuntime.Deregister` is guarded with
`if (tankOrNull != null)` at SmartRuntime.cs:846 — so `DetachPerTank` is NEVER CALLED on the
orphan path (the call is skipped at the call site, not "invoked-then-early-returns"); (2)
`Coordinator._previousAssignments` keyed by TechId never receives Forget; (3)
`PathingService._lastPaths` keyed by TechId never receives Forget; (4) orphan-sweep would not
recover TeamId if `PriorBelief==null` — current code is invariantly non-null (WorldModel
`NewlyObserved` populates and UpdateTeam/PublishPerTechBelief only write non-null), so L-011
is belt-and-suspenders for a future regression and for direct `SmartRuntime` queries with no
World entry; (5) Deregister fan-out ordering lets workers observe half-deregistered tech;
(6) failures inside any Forget abort the rest; (7) no test or watchdog proves the invariant.

**Root cause.** `Deregister` is a single best-effort linear fan-out conflating three lifecycle
paths (`OnTechRecycle`/`OnTankDestroyed`/orphan-sweep) into one signature, but two pieces of
cleanup are conditional on inputs the orphan-sweep cannot supply. `Coordinator` and
`PathingService` sidecars were added after the fan-out was authored and never wired. No
central canonical list; each subsystem registers its own Forget by convention via PR review.
No asserting watchdog walks live-tech-id set against sidecar key-sets.

**Best fix.** `TechLifecycleRegistry` as single canonical source of truth: each sidecar
self-registers a Forget delegate + Keys snapshot delegate. `SmartRuntime.Deregister` becomes a
thin fan-out wrapping each Forget in its own try/catch and logging `[TECH-LEAK-FORGET-FAIL]`
on exception. Orphan-sweep gains robust TeamId recovery (PriorBelief.Team OR cached
team-on-Register OR walk every TeamRuntime). `SmartEventBridge` tracks `TechId→Tank` weak
shadow map. New `TechLeakWatchdog` daemon (60s cadence) computes live-tech-id union from
`ManTechs.IterateTechs` + WorldModel + every TeamRuntime, walks each sidecar's snapshot, logs
`[TECH-LEAK]` on `sidecar.Keys ⊆ live` failure. SMART_DEV test `ASSERT_TECH_DEREGISTERED`
spawns a fixture, recycles, asserts every registered sidecar reports Forget.

**Rationale.** Simplest fix: add two Forgets + TeamId fallback (~10 LOC). Closes today's leaks
but next sidecar (IntentClassifier worker output, AttackTargetingSidecar in v0.3) silently
misses. BEST establishes a compiler+test+watchdog-enforced invariant. Per-fix try/catch
isolation is non-negotiable for the threading-lifecycle problem this whole plan addresses.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| Add two missing Forgets + TeamId fallback only (~10 LOC) | Doesn't establish enforced invariant; next sidecar will miss |
| `ConditionalWeakTable<Tank,SidecarState>` for everything | Doesn't work for TechId-keyed sidecars (int not ref); GC async leaves corpses across worker ticks |
| `IDisposable` PerTechHandle on FormState | Half the sidecars populated by non-Smart-driven techs (orphan-sweep targets) where FormState handle doesn't exist |
| Harmony postfix every TryRemove with central log | Engine-side concern; overkill; Harmony startup cost on Init |
| Per-tech RAII via using/finally in OnTechRecycle | Doesn't handle orphan-sweep path (no OnTechRecycle call — engine purged GameObject) |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-009 | `ITechSidecar` + `TechLifecycleRegistry` | `Smart/World/TechLifecycleRegistry.cs` (new) | 110 | — | Interface `{Name; Forget(TechId); SnapshotKeys()}`; `Register` idempotent; `ForgetAll` per-sidecar try/catch with `[TECH-LEAK-FORGET-FAIL]`; SMART_DEV assert sidecar's snapshot doesn't contain TechId post-Forget | Delete file + revert call sites |
| L-010 | `DetachPerTank(TechId)` + shadow map | `Smart/Integration/SmartEventBridge.cs` | 45 | — | `ConcurrentDictionary<TechId,Tank> _damageHandlersByTechId` mirrors `_damageHandlers`; populated in `AttachPerTank`; both signatures work; orphan-sweep can detach with null tank | Drop TechId overload + shadow map |
| L-011 | Orphan-sweep TeamId fallback | `Smart/SmartForm.cs`; `Smart/SmartRuntime.cs` | 40 | — | `PerTechEntry.Team` captured at RegisterTech; `FindTeamForTech(TechId)` walks `_teams.Values`; orphan-sweep consults entry.Team → FindTeamForTech → `default` with `[TECH-LEAK-NO-TEAM]` log | Drop Team field + walk |
| L-028 | Wire 6 sidecars into registry | `Smart/World/IntentRegistry.cs`; `Smart/World/DamageHintBuffer.cs`; `Smart/World/HealthSidecar.cs`; `Smart/World/ObservationIntake.cs`; `Smart/Learning/TargetObservationSequenceBuffer.cs`; `Smart/Learning/LeadResidualRecorder.cs` | 90 | L-009 | Each implements `ITechSidecar` with constant Name; `SnapshotKeys` returns fresh `List<TechId>` (cheap); ITechSidecar adapter calls existing per-sidecar API verbatim — **NOT a rename**. Existing methods: `IntentRegistry.Forget`, `DamageHintBuffer.Forget`, `HealthSidecar.Forget`, `ObservationIntake.Remove` (not Forget), `TargetObservationSequenceBuffer.Deregister` (not Forget), `LeadResidualRecorder.Forget`. Implementer must preserve the existing method names; adapter bridges to the canonical `ITechSidecar.Forget(TechId)` shape | Remove implements + SnapshotKeys |
| L-029 | Coordinator + PathingService as sidecars | `Smart/Coordination/Coordinator.cs`; `Smart/Pathing/PathingService.cs` | 85 | L-009 | `Coordinator.ForgetTech(TechId)` AND the existing TickOnce reassignment at `Coordinator.cs:148` (`_previousAssignments = targets;`) AND the Hungarian read at line 146 (`TargetAssignment_Hungarian.Assign(..., _previousAssignments, ...)`) ALL acquire a new private `_assignmentsLock` object — the Hungarian receives the dict by ref and reads during compute, so the lock must wrap the ENTIRE TickOnce stage that touches `_previousAssignments`, not just the dict mutation. `PathingService.ForgetTech` removes from `_lastPaths` (null-safe ConcurrentDictionary.TryRemove — TOCTOU with PathSolveLoop's TryGetValue+write at lines 273/279 is bounded: a Forget firing mid-solve may leave a phantom entry until next Forget tick, acceptable for the 60s watchdog cadence). Self-register from per-instance TeamRuntime ctor (N Coordinator instances) / `PathingService.Init`; deregister Coordinator from sidecar registry on TeamRuntime Disposed transition (L-044) | Remove ForgetTech + registration |
| L-055 | Refactor `Deregister` to use registry | `Smart/SmartRuntime.cs` | 50 | L-009, L-028, L-029, L-010, L-011 | Strict order: `DetachPerTank → team.DeregisterTech → registry.ForgetAll → World.DeregisterTech → helper.FormState=null`; **registry.ForgetAll runs BEFORE World.DeregisterTech** so any TechDespawned subscriber that consults a sidecar sees post-Forget state; each step own try/catch with `[TECH-LEAK-DEREGISTER]`; thread-callable | Revert to inline fan-out |
| L-083 | Delete duplicate fan-out in `WorldModel.DeregisterTech` (promoted from AC-7) | `Smart/World/WorldModel.cs:88-92` | -15 | L-055 | `WorldModel.DeregisterTech` only touches `_byTech` + `_intake` + publishes `TechDespawned`; sidecar Forgets owned by `SmartRuntime.Deregister`. CANNOT land independently of L-055 (would fire sidecar forgets twice during the chunk-window) | Restore inline Forgets |
| L-056 | `TechLeakWatchdog` daemon | `Smart/World/TechLeakWatchdog.cs` (new); `Smart/SmartRuntime.cs` | 130 | L-009, L-023, L-028, L-029 | Long-running via `Pool.EnqueueLongRunning` (relies on **L-023 hardened supervisor** to survive FMOD pause cascades — without L-023, watchdog dies on first abort); 60000ms `WaitOne`; snapshots `ManTechs.IterateTechs` (main-thread via WorldEventBus + 100ms timeout + stale fallback) ∪ WorldModel ∪ TeamRuntimes; walks each sidecar's SnapshotKeys; `[TECH-LEAK]` per `(sidecar,hash)` dedup | Remove file + EnqueueLongRunning call |
| L-084 | `ASSERT_TECH_DEREGISTERED` tests (promoted from AC-9) | `Smart/Tests/SmartTestSuite.cs` | 110 | L-009, L-010, L-028, L-029, L-055 | 3 tests: every registered sidecar forgotten after Deregister; poison-pill doesn't starve others; chronic leak logs once per session. SMART_DEV-gated | Remove tests |

**Tests:** `TechLifecycleRegistry_DeregisterFanOut_EveryRegisteredSidecarForgotten`,
`TechLifecycleRegistry_OneSidecarThrows_OthersStillForget`,
`TechLeakWatchdog_ChronicLeak_LogsOncePerSession`,
`SmartEventBridge_DetachPerTank_OrphanSweepPath_DropsHandler`,
`OrphanSweep_TeamIdFallback_FindsTeamViaTeamRuntimeWalk`.

**Risks.** Watchdog needs main-thread `ManTechs.IterateTechs` — posts one-shot to WorldEventBus
main-thread queue, 100ms timeout, falls back to stale-but-safe Smart-internal live set.
`ITechSidecar` API churn — implement explicitly; only `SnapshotKeys` is new public surface.
Per-step try/catch could hide real exceptions — distinct `[TECH-LEAK-*]` tags grep-findable;
SmartTrainingDriver dev UI gains cumulative leak count. Coordinator reassigns
`_previousAssignments` every tick — `ForgetTech`, the TickOnce reassignment at line 148, AND
the Hungarian read at line 146 all share the new `_assignmentsLock` (L-029's LOC bumped to 85
to cover wrapping the entire Hungarian-read+reassign stage, since Hungarian receives the dict
by ref and reads during compute). SnapshotKeys on lock-protected Rings — `ConcurrentDictionary.Keys` is
already snapshot; no extra lock. Race between `OnTechRecycle` main-thread + worker-thread
watchdog — watchdog 60s cadence tolerates transient mismatches; true leak persists
indefinitely vs ~100ms race window.

**Cross-subsystem deps:** threading-lifecycle-hooks (watchdog daemon enqueued via the hardened
`EnqueueLongRunning`); cancellation/shutdown ordering (`TechLifecycleRegistry.Clear` AFTER
`CancelAllAndJoin`); SmartEventBridge uninstall must clear the new shadow map; FMOD pause
recovery — watchdog must not fire during pause window (add 'recently resumed' grace skipping
next 60s tick); ObservationIntake.Remove must wire as a sidecar.

---

### 3.5 Trainer worker lifecycle vs IsHost transitions

**Symptom.** After host→client transition the 4 TrainerWorker loops silently park on
top-of-loop `!IsHost` gate. In-flight minibatch publishes half-trained parameters into
inference DoubleBuffer. Producers keep enqueueing on every host so drop-oldest BoundedQueue
evicts genuine pending events. Profile is never flushed at handover (Save only at Shutdown).
On promotion, trainer resumes against in-memory state without re-reading the on-disk profile
written by the previous host. No `[TRAINER-HOSTCHANGE]` log line.

**Root cause.** `SmartRuntime.IsHost` is plain `volatile bool` with no edge detection, no
observer surface, no transactional handover. TrainerWorker treats IsHost as per-iteration
poll, never as event — cannot distinguish "still client" from "just became client
mid-minibatch". Producers (OnDamageObserved, 1Hz drain, LeadResidualRecorder,
PlanTransitionPublisher) never gate on IsHost. Profile lifecycle bound only to
`LearningService.Init`/`Shutdown` — no checkpoint-on-handover, no reload-on-promotion, no
transactional fence. `_publishedParams.Write` is unconditional.

**Best fix.** Make host-transition first-class observable event: `HostChanged` struct on
WorldEventBus and `HostAuthorityCoordinator` owning edge detection + per-tick debounce + fan-out.
Each TrainerWorker grows lifecycle FSM (Active/Pausing/Paused/Resuming) with three barriers:
(a) on host→client finish in-flight minibatch (the whole `TrainOneMinibatch` call — atomic unit
exposed by ILearnedModel); drain leftovers into holding buffer with TTL; (b)
`LearningService.OnHostLost` runs synchronous
SaveProfile (single-flight CompareExchange, atomic .tmp+rename); (c) `LearningService.OnHostGained`
re-reads on-disk profile if mtime newer than `_profileLoadedAtMs`, republishes via DoubleBuffer
BEFORE unparking. Every trainer logs `[TRAINER-HOSTCHANGE]` per transition. Producers gate via
`SmartRuntime.AcceptingTrainingEvents`; dropped events emit coalesced 1/min
`[TRAINER-DROPPED-NONHOST]` counter.

**Rationale.** Simplest fix races torn-read against in-flight `_adam.Step`. No observability.
No future-proof. The BEST fix encodes the transition as an event so future subsystems
(Coordinator role re-elections, PathingService threat-field repurpose, AetherFuser belief
flush) subscribe to the same edge. Cost: +1 small coordinator file (~120 LOC) + ~30 LOC FSM in
TrainerWorker; entirely orthogonal to thread-abort work.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| Save+Reload directly from SmartForm.Operations IsHost flip | Races torn-read against in-flight `_adam.Step`; no observability; reproduces polling antipattern |
| Treat host migration as full Shutdown+Init cycle | ~3s init freeze per transition; destroys BlockCatalog and per-tech state; risk of double-subscribe Harmony patches |
| Keep training, gate `_publishedParams.Write` | Adam `_M/V/T` accumulates against events host never saw; reload would zero `_params` but not optimizer momentum; locks in silent divergence |
| Per-trainer ManualResetEventSlim, no FSM | Solves park-vs-poll efficiency; leaves in-flight + producer + publish + checkpoint untouched |
| Gate at BoundedQueue producer (TryEnqueueIfHost) | Closes silent-eviction; doesn't close in-flight publish or checkpoint; distributes IsHost knowledge to ~6 producers |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-012 | `HostChanged` event + `HostAuthority` enum | `Smart/World/EventBus.cs:89-105,260-301` | 25 | — | `ClearAll` clears `HostChanged` subscribers; struct fields readonly value-typed; `PhaseSource` enum logged. Insertion sites: HostChanged struct in the existing struct cluster near line 89-105; ClearSubscribers<HostChanged> in the ClearAll block | Delete struct+enum |
| L-030 | `HostAuthorityCoordinator` | `Smart/Coordination/HostAuthorityCoordinator.cs` (new) | 120 | L-012 | `Interlocked.CompareExchange` single-flight per frame; 250ms `ConfirmStableTransitionMs` suppresses flapping; `Notify` main-thread-only — captures `Thread.CurrentThread.ManagedThreadId` from `SmartRuntime.Init` (assumed main thread — true because Init fires from `SmartForm.InitGlobal` on Unity boot path); throws with `[HOSTAUTH-THREAD-VIOLATION]` if violated; `_lastObservedIsHost` volatile | Delete file + MainThreadId stamp |
| L-031 | `TrainerBarrier` | `Smart/Coordination/TrainerBarrier.cs` (new) | 60 | L-012 | Register/Deregister are `Interlocked.Increment/Decrement`; CountdownEvent sized to live count (not assumed-4); 500ms hard timeout; `SnapshotPhases()` for diagnostics. Trainer that registers AFTER barrier already armed records a deferred-pause flag the trainer checks at next RunLoop top — ensuring late-registering trainer skips work and goes straight to Paused without blocking the (already-completed) barrier | Delete file; OnHostLost falls back to blind 200ms `Thread.Sleep` |
| L-057 | `Operations:382` calls `Notify` | `Smart/SmartForm.cs:382` | 3 | L-030 | Coordinator becomes sole writer of `SmartRuntime.IsHost` (field retained for the 9 legacy reader sites: SmartForm.cs:260, GlobalCoordinatorDaemon.cs:26, ContinuousController.cs:520, Coordinator.cs:132, StrategicPlanner.cs:63, GlobalPlannerDaemon.cs:31, OnlineTrainer.cs:281, AetherFuser.cs:197, PathingService.cs:181,227 — they continue polling). `HostChanged` event is the additional surface for observers that need edge semantics (TrainerWorker FSM via L-058 subscription) | Restore direct assignment |
| L-058 | TrainerWorker FSM | `Smart/Learning/OnlineTrainer.cs:265-303` | 95 | L-012, L-030, L-031 | `Active→Pausing` set BEFORE next `TrainOneMinibatch` entry (Interlocked.Read at top of loop); the entire current `TrainOneMinibatch` call completes atomically (cannot inspect mid-Adam.Step boundary through ILearnedModel interface); `[TRAINER-HOSTCHANGE]` per transition with `InFlightStepCompleted`/`HoldingBufferDrained`/`TransitionLatencyMs`; holding buffer hardcoded **64** (= 2× MinibatchSize since MinibatchSize is a per-model const not on ILearnedModel interface; all 4 models use 32); ADD finally block to RunLoop (currently has try/catch/catch but no finally) — Unsubscribe in finally AND barrier Deregister in finally BEFORE registry Deregister; existing `if (!SmartRuntime.IsHost)` poll at OnlineTrainer.cs:281-285 is replaced by phase-state check (`if (phase == Paused) WaitOne; continue`) | Revert to poll-based gate |
| L-059 | `AcceptingTrainingEvents` + producer gate | `Smart/SmartRuntime.cs:607`; `Smart/Learning/LearningService.cs:221,494`; `Smart/Learning/LeadResidualRecorder.cs:94`; `Smart/Learning/PlanTransitionPublisher.cs:23`; `Smart/Learning/TargetObservationSequenceBuffer.cs:180` (indirect via DrainAndEnqueue) | 40 | L-030 | Gate the 2 direct sites (LearningService.cs:494, LeadResidualRecorder.cs:94), PLUS gate the `DrainAndEnqueue` caller at LearningService.cs:221 (which internally enqueues via TargetObservationSequenceBuffer.cs:180), PLUS gate `PlanTransitionPublisher.OnPlanPublished` early-return on `!AcceptingTrainingEvents`. Per-model drop counter Interlocked + 60s emit; reset on emit. TestProducerGateCoverage scans for any of: `.EventQueue.Enqueue(`, `DrainAndEnqueue(`, `WorldEventBus.PublishFromWorker(new PlanTransition` — and asserts each is preceded by an `AcceptingTrainingEvents` read | Inline-restore each call site |
| L-070 | OnHostLost / OnHostGained | `Smart/Learning/LearningService.cs:112-154` (append after Shutdown); plus call sites in `Smart/Learning/LearningService.cs:71` (Init subscription) | 75 | L-012, L-058, L-013 | OnHostLost runs ONLY after barrier reports Paused (500ms timeout → `[HOSTAUTH-PAUSE-TIMEOUT]` + skip save); uses existing **`File.Replace`** atomic path at `ProfilePersistence.cs:112` (with `File.Move` fallback at `:117-118` for non-NTFS PlatformNotSupportedException). OnHostGained captures `_profileLoadedAtMs`, reloads iff disk-newer, then for each of the 4 models wraps `LoadParameters` in try/catch ArgumentException: on length-mismatch (e.g. host wrote v=N+1 while client at v=N), logs `[LEARNING-HOSTCHANGE] reload-skip model=X reason=arch-mismatch` and keeps in-memory parameters; on success republishes via DoubleBuffer BEFORE Resume; single `[LEARNING-HOSTCHANGE]` per transition (Resume proceeds regardless — trainer continues training from whatever weights survived) | Unsubscribe + delete methods |
| L-071 | Holding-buffer TTL | `Smart/Learning/OnlineTrainer.cs:265-303` | 20 | L-058 | TTL checked inside existing `WaitHandle.WaitOne` loop; bound by cancellation token; `[TRAINER-HOLD-DISCARD]` with discard count + Paused duration | Set TTL to `int.MaxValue` |

**Tests:** `TestHostTransitionFlush`, `TestHostFlapDebounce`,
`TestTrainerInFlightStepCompletes`, `TestProducerGateCoverage`,
`TestProfileReloadOnPromotionRespectsMtime`, `TestBarrierPartialInitDoesNotDeadlock`.

**Risks.** OnHostLost blocks main thread up to 500ms — happy path <100ms; timeout surfaces
`[HOSTAUTH-PAUSE-TIMEOUT]` rather than hiding. SaveProfile during FMOD pause cascade —
`.tmp+rename` already atomic; startup `.tmp` scavenger added (~8 LOC inside Init). HostChanged
subscriber leak — L-012 explicitly clears in `ClearAll` outside pragma block; test runs two
Init/Shutdown cycles asserting subscriber count is 0. Drop counter allocation — emit cadence
1/min; Interlocked.Increment is alloc-free. Host migration racing Shutdown — barrier
Deregister called in trainer's finally BEFORE registry Deregister; CountdownEvent already
sized to 0. TTL discard mid-promotion — Paused→Resuming has priority via cancellation token;
worst case 100ms before Resume.

**Cross-subsystem deps:** threading-lifecycle-hooks (HostChanged via WorldEventBus must stay
main-thread-only); checkpoint flush on shutdown (shares ProfilePersistence.Save with
profile-save subsystem); FMOD pause path (synchronous Save could collide); worker-pool
cancellation contract (WaitHandle.WaitOne in Paused phase respects worker CTS); engine event
bridge (assumes Operations fires per frame).

---

### 3.6 Profile save/load + migration

**Symptom.** When FMOD fires `OnApplicationPauseManual True` and daemons die,
`SmartForm.DeInitGlobal` never runs because Unity process is exiting. `LearningService.Shutdown`
never called; in-flight minibatches in BoundedQueues are abandoned; parameter updates since
last `KickStart.SaveSink` are lost. If Save IS called mid-quit and Thread.Abort interrupts
BinaryWriter, `File.Replace` can leave truncated `.tmp` and untouched main file, or zero-length
main on certain Mono unloads. Migration ladder fragile: `MigrationRunner.RunForward` throws
on unknown version, surfacing as "Glorot init" with no distinction between fresh install and
rejection.

**Root cause.** Save only triggered from KickStart.SaveSink and Shutdown — neither fires on
process quit. Training has no "pending publish" notion; minibatches consumed since last save
are lost on hard exit because Save is not periodically re-fired. Migrations dispatched by
hand-maintained switch; no registry; no assertion every numbered file is wired; no
v0.3-safe pattern for field renames (LoadedProfile.Section.Weights is flat float[]).

**Best fix.** Four-layered durability: (a) 30s timer autosave registered with
WorkerLifecycleRegistry (dirty-only); (b) `Singleton.ApplicationQuitEvent` +
`AppDomain.ProcessExit` + `AppDomain.UnhandledException` three-headed last-chance save with
200ms reserved budget; (c) `ILearnedModel.FlushPendingForPersist` drains EventQueue into
synchronous final minibatch under shared mutex with worker; (d) TLV per-section body with
schema_version + reflection-built `[SmartMigration(fromVersion=N)]` registry asserting numbered
files present at static init. Corruption recovery widens to ring of two backups
(`.previous`+`.penultimate`). Structured `[PROFILE-LOAD-FAIL]` distinguishes corruption from
cold start. 200ms save reserve carved from 2s WorkerLifecycle budget.

**Rationale.** Simplest: subscribe ApplicationQuitEvent + call SaveProfile. Rejected because
(1) tears `_params` during in-flight `_adam.Step` — saves corrupt half-step; (2) discards
unprocessed EventQueue (~30s of gameplay observations); (3) does nothing for migration
fragility (user mandate: no v0.3 deferments); (4) periodic autosave protects against crashes
(segfault/OOM) which quit-event cannot. The BEST fix is four interlocking layers.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| Synchronous SaveProfile in OnApplicationQuit handler only | Loses M ≈ unbounded; tears `_params`; no game-CRASH recovery; doesn't address migrations |
| Write-ahead log of per-minibatch gradients | Massive scope; DoubleBuffer IS the checkpoint; periodic snapshotting equally correct + dramatically cheaper |
| JSON schema instead of TLV binary | No JSON dep; Newtonsoft pulls 600KB; JsonUtility can't serialize float[]>4096 |
| Save every minibatch | 10Hz × 4MB = 40MB/s; thrashes SSD |
| `File.Replace`'s `destinationBackupFileName` | Backup is the file at moment of replace, which on torn write is the half-written file |
| Skip per-model FlushPendingForPersist | Quit save is the last save; unprocessed queue ARE the training signal for last 30s |
| Process-wide save mutex | Blocks all 4 trainers; per-model mutex saves ~40ms trainer downtime per cycle |
| `SaveProfile(reason)` with conditional flush | Periodic saves that don't flush lose events systematically; uniformity reduces test surface |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-013 | FlushPendingForPersist + SaveMutex | `Smart/Learning/OnlineTrainer.cs:47-70,288`; `Smart/Learning/ThreatAssessmentModel.cs:82-103`; `Smart/Learning/OpponentIntentClassifier.cs:166-175`; `Smart/Learning/ActionValueEstimator.cs`; `Smart/Learning/TrajectoryResidualModel.cs`; `Smart/Learning/ProfilePersistence.cs:74-84` | 130 | — | `ILearnedModel.SaveMutex { get; }` (returns `object` — C# `lock` requires ref type; each model declares `private readonly object _saveMutex = new object(); public object SaveMutex => _saveMutex;`) + `FlushPendingForPersist()`. TrainerWorker.RunLoop wraps `_model.TrainOneMinibatch()` at OnlineTrainer.cs:288 in `lock(_model.SaveMutex)` (NOT inside the model's `_adam.Step` — the call site is the lock granularity); Save wraps `StoreParameters` in same lock; Flush returns 0 on empty / BatchSize on partial | Revert interface + lock acquisitions |
| L-014 | Migration registry + attribute | `Smart/Learning/ProfilePersistence.cs:298-323`; `Smart/Learning/Migrations/SmartMigrationAttribute.cs` (new); `Smart/Learning/Migrations/0001_initial_schema.cs:12`; `Smart/Learning/Migrations/0002_BpttUnfreeze.cs:13` | 110 | — | Static ctor walks executing assembly; asserts coverage of `[0, CurrentSchemaVersion)`; throws TypeInitializationException at first Init if any version missing; each migration exposes `public static void Up(LoadedProfile p)`. M0001 (currently bare `Version = 1` const with comment "floor: no transformation defined") gains a no-op `Up(LoadedProfile)` that bumps SchemaVersion from 0→1 — the floor comment is updated to reflect that v0→v1 is now a documented no-op rather than "undefined" | Revert to switch-statement |
| L-015 | Two-deep backup ring + `[PROFILE-LOAD-FAIL]` | `Smart/Learning/ProfilePersistence.cs:44-152`; `Smart/Learning/LearningService.cs:163-186` | 60 | — | Save rotation: `.previous → .penultimate` (delete old penultimate first), Copy current → `.previous`, then `.tmp + File.Replace`; Load fallback `primary → .previous → .penultimate → baseline → null`; `[PROFILE-LOAD-FAIL] tier=...` line emitted only when fallback was needed (cold start uses `[PROFILE-COLD-START]`); `.corrupt-<unixMs>` preserved for any tier | Restore single .previous |
| L-032 | Uniform `[PROFILE-MANUAL-SAVE]` log on OnEngineSave | `Smart/SmartForm.cs:130-142` | 15 | L-015 | OnEngineSave path unchanged but adds `[PROFILE-MANUAL-SAVE] source=engineSave` so all save sources emit uniform regex `\[PROFILE-(MANUAL-SAVE\|AUTOSAVE\|QUIT-SAVE)\] ` | Remove the one LogWarnFileOnly call |
| L-060 | Periodic autosave worker | `Smart/Learning/LearningService.cs:71-106`; `Smart/Learning/AutosaveWorker.cs` (new) | 95 | L-021, L-022, L-023 | 30s cadence registered with WorkerLifecycleRegistry; dirty-only via `_modifiedSinceLoad`; `[PROFILE-AUTOSAVE] period=30000ms saved=N skipped=M`. L-021 changes EnqueueLongRunning to return the resolved thread name (non-null on success); L-060 throws `InvalidOperationException` if returned name is null OR if WorkerLifecycleRegistry does not contain the autosave handle within 100ms after Enqueue (verifies registration via SnapshotLive lookup) | Delete file + revert Init/Shutdown block |
| L-078 | Three-headed quit save | `Smart/SmartForm.cs:60-95`; `Smart/Learning/QuitSaveCoordinator.cs` (new); `Smart/Threading/WorkerLifecycleRegistry.cs:76`; `Smart/SmartRuntime.cs:754` (Shutdown sequencing) | 155 | L-013, L-022 | `HasFired` Interlocked-set → idempotent across 3 sources; 200ms reserved from 2s WorkerLifecycle budget. **SmartRuntime.Shutdown sequencing**: runs `QuitSaveCoordinator.Flush(deadline=200ms)` BEFORE `CancelAllAndJoin(remaining = TimeSpan.FromMilliseconds(2000 - elapsed))` — passing a shrunken timeout into the existing 2-second join. Falls back to `DoubleBuffer.Read` if mutex contention >100ms; `[PROFILE-QUIT-SAVE] source= saved= elapsed= flush=Y/4` always | Remove 3 subscriptions + delete coordinator |
| L-079 | TLV per-section body (schema 3) | `Smart/Learning/ProfilePersistence.cs:19-24,40-100,200-220`; `Smart/Learning/Migrations/0003_TlvSectionBody.cs` (new) | 125 | L-014 | Body: `tag_count[2]` then `tag_id[2] byte_length[4] payload`; tag 0x0001=weights; unknown tags SKIPPED on load and preserved in **new `Section.UnknownTags<(ushort,byte[])>` field** (Section class at lines 19-24 must be extended). Load path at lines 200-220 must become schema-conditional: `if (SchemaVersion >= 3) ReadTlvBody(...) else ReadFlatFloats(...)`; the existing size-assertion at line 208 (`if (paramBytes != paramCount * 4)`) only applies to the flat-float branch. M0003 sniffs v≤2 in-memory and wraps flat float[] into UnknownTags-shape with weights staying in `Section.Weights` (consumers like ApplyProfile assume float[]) — in-memory representation keeps `.Weights` as canonical view computed from tag 0x0001; disk format becomes TLV. Re-emit on next Save preserves UnknownTags verbatim | Revert schema to 2; v0.3 files become unloadable |
| L-080 | `ProfileSelfTest.Run` on Init | `Smart/Learning/LearningService.cs:71-85`; `Smart/Learning/ProfileSelfTest.cs` (new) | 50 | L-013, L-014, L-079 | Round-trips fixture through temp path; throws on bit-mismatch; LearningService.Init catches, logs `[PROFILE-SELFTEST-FAIL]`, refuses LoadProfile (Glorot retained) | Delete file + remove call |

**Tests:** `Autosave_FiresEvery30sWhenDirty`, `Autosave_SkipsWhenNotDirty`,
`QuitSave_FiresOnApplicationQuit`, `QuitSave_IdempotentAcrossAllThreeSources`,
`QuitSave_HonorsBudget`, `Trainer_HoldsSaveMutexDuringStep`, `Save_DoesNotTearParameters`,
`Flush_DrainsResidualEvents`, `MigrationRegistry_CoversAllVersions`,
`MigrationRegistry_RejectsFutureSchema`, `MigrationLadder_RunsForwardInOrder`,
`Migrate_V2ToV3_RoundTrip`, `Load_SkipsUnknownTag`, `Save_MaintainsTwoBackups`,
`Load_FallsThroughAllTiers`, `Load_AllTiersFail_GlorotFallback`,
`AllThreeSaveSources_LogUniformly`, `SelfTest_DetectsBrokenSerializer`.

**Risks.** Autosave hitch — runs on background thread; SaveMutex contention only briefly
stalls one trainer (≤15ms invisible). ApplicationQuitEvent fires after thread-abort — 3-way
redundancy; SaveMutex respects finally (Mono 4.6 respects); 200ms watchdog cap; fallback to
DoubleBuffer.Read on contention. Reflection registry cost ~10-30ms — one-shot per process,
cached. TLV rollback breaks v0.3 files on v0.2 — schema sniff rejects future-schema; release
notes warn. FlushPendingForPersist Adam.Step cost — ~5ms × 4 ≤ 20ms inside 200ms budget; cap
at one minibatch. Two-deep backup = 3MB/player — trivial vs 50MB TerraTech saves.
ApplicationQuitEvent subscription lifetime — subscribe ONCE process-wide; never unsubscribes.
AppDomain.UnhandledException recursion — pre-allocated byte buffers + direct File.WriteAllBytes;
catch swallow recursive failures.

**Cross-subsystem deps:** threading-lifecycle-hooks, worker-pool quit handling,
logging+watchdog.

---

### 3.7 Terrain + path-cache world reset

**Symptom.** After save reload or map change Smart-driven techs operate against polluted
Pathing: (1) PathSolveLoop and ThreatFieldRebuildLoop can briefly pair new mission's threat
snapshot with OLD mission's TerrainMap reference (non-volatile static swapped under live
workers); (2) per-team ThreatField double-buffers for vanished teams never evicted; (3)
PathRequestBackpressure state inside long-running PathSolveLoop closure survives transition
carrying shed-mode latch and drop counters; (4) newly-allocated TerrainMap has
`IsPopulated=false` for multi-second refresh window with no signal of "world is reset, not yet
populated"; (5) no per-subsystem `[WORLD-RESET]` log, so partial OnWorldReset failure leaves
half-cleared cache with no audit.

**Root cause.** `PathingService.OnWorldReset` treats `_terrain` as exclusive but
ThreatFieldRebuildLoop and PathSolveLoop concurrently read both `_terrain` and `_threatFields`
with no publication barrier. Reset performs independent writes (`_threatFields.Clear`,
`_lastPaths.Clear`, `_terrain = new TerrainMap()`) workers can interleave. `_threatFields`
keyed by TeamId in ConcurrentDictionary that grows on demand but never shrinks.
PathRequestBackpressure instantiated INSIDE PathSolveLoop body. No subsystem-aware
`OnWorldReset` contract — adding a new cache requires editing OnWorldReset and is silently
forgotten on miss.

**Best fix.** `IWorldResettable` registry in PathingService + atomic terrain swap via
`Volatile.Write` of a generation-counter-bearing reference holder + per-subsystem
`[WORLD-RESET]` log lines. `PathingService.ForgetTeam(TeamId)` called from
`SmartEventBridge.OnTankTeamChanged` and from orphan-sweep when team empties. Lift
PathRequestBackpressure to PathingService static so OnWorldReset can `ResetForWorldTransition()`.
`TerrainMap.IsFreshlyAllocated` flag distinguishes "never populated" from "world just reset,
refresh pending" — MPC continues with last-known terrain until new pass publishes. Tests in
`Tests/PathingResetTests.cs` (SMART_DEV).

**Rationale.** Simplest is volatile + Clear in OnTankTeamChanged + 3 lines. Rejected because:
volatile makes per-field reads ordered, NOT (_terrain,_threatFields) atomic; Clear on team
change kills threat data for all teams in 33ms window; backpressure in closure unreachable
from anywhere else; no per-subsystem log means future addition silently regresses. BEST
treats reset as first-class lifecycle event with publication semantics + opt-in + observable
per-step success + atomicity. Also pre-req for V0.3 multi-world.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| Add `volatile` to `_terrain` only | Closes per-field tearing; splice race remains; team-forget + backpressure + IsPopulated all unaddressed |
| ManualResetEvent gate on PathSolveLoop + ThreatFieldRebuildLoop | Main-thread latency to longest in-flight Solve (~80ms); worker past wait but before publish still splices; reintroduces lock deadlock surface |
| Tear down all PathingService workers per reset | Defeats "workers survive reset"; loses SettleDown belief warmup; thread Start/Stop on Mono is the cascade trigger |
| Sweep stale teams every 60s | 60s lag keeps stale threat alive across team conversions |
| TerrainMap returns up+slope=0+traversable=true silently when unpopulated | Today's exact bug — "tech falls through cliffs" silent multi-second window |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-016 | `IWorldResettable` + `WorldResetRegistry` | `Smart/Pathing/IWorldResettable.cs` (new); `Smart/Pathing/WorldResetRegistry.cs` (new) | 80 | — | Every cache MUST implement + register at Init; test asserts 4 registered objects + 2 inline-lambda registrations: **TerrainMap** (class), **LineOfSightProducer** (class, has `Reset()` at LineOfSightProducer.cs:77), **PathRequestBackpressure** (class, after L-018 promotion), TeamReaperDaemon-owned cleanup; PLUS 2 inline lambdas wrapping the private `PathingService._threatFields` and `PathingService._lastPaths` ConcurrentDictionary fields (NOT separate classes — registry stores lambda closures that capture these private fields' clear semantics). The fictitious `ThreatFieldCache` / `LastPathCache` / `TeamCacheTracker` class names from earlier drafts do NOT exist as standalone types. `[WORLD-RESET]` per hook with name+elapsed+outcome | Delete files; revert OnWorldReset to direct-field-reset |
| L-033 | Atomic `TerrainPublication` holder | `Smart/Pathing/PathingService.cs:77-142`; `Smart/Pathing/TerrainPublication.cs` (new) | 90 | L-016 | `Volatile.Read(ref _publication)` returns immutable `{Terrain, ThreatFields, Generation, PublishedTickMono}`; GetThreatField + PathSolveLoop read publication once per iteration; `(terrain, threatFields)` pair guaranteed coherent. Publication-holder swap is DEFERRED until the new TerrainMap completes its first incremental refresh (IsFreshlyAllocated flips false per L-035) — until then the old publication serves as "last-known" backing for consumers | Revert to (_terrain,_threatFields) pair |
| L-034 | PathRequestBackpressure WorldResetRegistry hook | `Smart/Pathing/PathingService.cs:217`; `Smart/Threading/PathRequestBackpressure.cs` | 25 | L-016, L-018 | After L-018 lifts PathRequestBackpressure to PathingService static, L-034 adds `ResetForWorldTransition()` method on the concrete PathRequestBackpressure class (NOT on the `IPathingBackpressureReadout` interface — readout is read-only) + IWorldResettable registration call. Reset clears `_highWaterStartMono=0`, `_shouldShed=false`, `_dropped=0`; `[PATHING-SHED-RESET]` with pre-reset counts; PathSolveLoop observes cleared state next iteration | Remove ResetForWorldTransition + registration |
| L-035 | `TerrainMap.IsFreshlyAllocated` flag | `Smart/Pathing/TerrainMap.cs:11-34,64-115,171-181` | 20 | L-016, L-033 | TerrainMapSnapshot is co-located inside `TerrainMap.cs` at lines 11-34 (NOT a separate `TerrainMapSnapshot.cs` file — that file does not exist; cited range includes the snapshot class). New TerrainMap → `IsFreshlyAllocated=true`; clear on first successful publish; consumers MUST read PathingService publication holder (per L-033 deferred-swap semantic) to access last-known terrain; branch on FreshlyAllocated (use last-known via publication holder) vs IsPopulated (use defaults) | Remove field; fall back to IsPopulated |
| L-036 | `WorldResetting`/`WorldResetCompleted` events | `Smart/World/EventBus.cs`; `Smart/SmartForm.cs:550-573` | 25 | L-016 | WorldResetting carries prior-mission tick count; WorldResetCompleted carries new-tick + duration; published BEFORE/AFTER any hook fires; `[WORLD-RESET]` block canonical order; `[WORLD-RESET-PARTIAL]` on failure | Drop events; fall back to log-polling |
| L-061 | `ForgetTeam(TeamId)` from team-empty detection | `Smart/Pathing/PathingService.cs`; `Smart/Integration/SmartEventBridge.cs:357`; `Smart/SmartRuntime.cs:835`; `Smart/Coordination/TeamReaperDaemon.cs` (L-044 Disposed transition) | 35 | L-033, L-044 | ForgetTeam logs `[PATHING-FORGET-TEAM] teamId=N` with resulting `_threatFields.Count`. Invocation gate: only fire when team actually empties — call from L-044 TeamReaperDaemon's `Disposed` transition (primary site, fires on grace-period eviction) AND from SmartEventBridge.OnTankTeamChanged ONLY when `team.TechCount == 0` after the per-tech DeregisterTech (NOT unconditionally per-migration — that would erase threat field for teams that still have remaining techs). Also invoke from `SmartRuntime.Deregister` orphan-sweep when last tech removal empties team. DEBUG assert every TeamId in `_threatFields` has matching TeamRuntime via newly-exposed `SmartRuntime.HasTeam(teamId)` predicate; bounded by active count | Remove ForgetTeam call (method remains idempotent no-op) |
| L-068 | `PathingResetTests.cs` (SMART_DEV) | `Smart/Tests/PathingResetTests.cs` (new); `Smart/Tests/SmartTestSuite.cs:32` | 280 | L-016, L-033, L-061, L-034, L-035, L-036 | 6 tests lock every invariant above; SmartTestSuite.RunAll picks up; all SMART_DEV-gated | Delete file + remove 6 TestRunner.Run lines |
| L-069 | `OnWorldReset` collapses to `ResetAll` | `Smart/SmartForm.cs:550-573` | 15 | L-016, L-036 | Reduces to publish WorldResetting → ResetAll(callerId='OnWorldReset') → publish WorldResetCompleted; grep `\[WORLD-RESET\]` returns ≥7 lines per cycle (1 start + 6 per-hook + 1 completed/partial) | Restore inline World.Clear + PathingService.OnWorldReset |

**Tests:** `RegistryEnumeratesAllExpectedHooks`, `AtomicSwap_NoSpliceUnderLoad`,
`ThreatFieldDictionary_NoGrowthAcrossTeamFlips`, `BackpressureLatchClearsOnReset`,
`FreshlyAllocatedFlag_FlipsCorrectly`, `WorldResetEventsBracketAllResets`.

**Risks.** TerrainPublication allocation per reset ≈ 32 bytes × 10Hz training = 320 b/s GC,
negligible. ForgetTeam from worker thread racing main-thread Init of a re-converted team —
worker removal happens against same generation observed; GetOrAdd lazily re-creates next tick.
Static PathRequestBackpressure shared across loops — future high-pri loop registers own key in
Dictionary<string,Backpressure>. `[WORLD-RESET]` spam under training high-frequency resets —
file-only via DebugTAC_AI.LogWarnFileOnly tag 'world-reset'; 100 resets = 800 lines within
rotation. Partial failures now visible — intended observability gain; identifies WHICH hook
failed.

**Cross-subsystem deps:** threading-lifecycle-hooks (IWorldResettable may need mirroring with
AetherFuser/GlobalPlannerDaemon/GlobalCoordinatorDaemon reset); world-model-belief-reset
(events live in World/EventBus.cs; align vocabulary with WorldModel.Clear / AetherFuser);
learning-profile-flush (IWorldResettable hook should include
LearningService.ObservationSequence + LeadResidualRecorder); FMOD pause cancellation (our
tests assume workers alive at OnWorldReset).

---

### 3.8 PathRequestBackpressure + per-request lifecycle

**Symptom.** Path-request pipeline is unobservable scaffolding with half-built backpressure:
(1) PathRequest has no EnqueueMonoTimestamp — stale requests solved against ancient
(Start,Goal) pairs whose requester moved 100m or died; (2) ShouldShedLowPriority lives inside
PathSolveLoop stack frame and is never exported — operator running DrawPathingDebugGUI or
smart.runtime.status cannot see whether system is shedding; (3) 60s `[PATHING-SHED]` log only
fires when `drops>0` — if shed-mode latches but `Priority<128` never arrives (today's reality:
zero producers set Priority!=128) operator sees nothing while loop drops fresh dispatches;
(4) no `[PATHING-SHED-CLEAR]` hysteresis — flag flaps 3s after each burst with no log; (5)
PathRequest.Priority is dead public API (every caller defaults 128); (6) zero p50/p99 solve
latency — slow solver indistinguishable from healthy one.

**Root cause.** PathRequestBackpressure was authored as Observe-only widget owned by
stack-local with no static accessor, no enqueue-time stamp, no hysteresis, no producer wiring,
no histogram/latency surface. Single log path gates on `dropped>0` which is provably zero
(no RequestPath callers in TAC_AI tree; even when called, default Priority=128 never trips
`req.Priority<128`). "Scaffold that compiles but cannot be observed" — exactly the
silent-failure mode ARCHITECTURE.md §4 forbids.

**Best fix.** Promote PathRequestBackpressure to PathingService-owned singleton with public
read accessors (ShouldShedLowPriority, QueueDepth, DroppedSinceLastReport, ExpiredCount,
SolveLatencyP50Ms, SolveLatencyP99Ms). Add EnqueueMonoTimestamp + dequeue expiry (1500ms
default) BEFORE priority check. Hysteresis: trip ≥75% sustained 3s; CLEAR ≤25% sustained 3s
with edge-triggered `[PATHING-SHED]`/`[PATHING-SHED-CLEAR]` (no periodic). 60s
`[PATHING-DEPTH]` histogram log (4 buckets) so operator sees trend independent of drops.
Wire real low-priority producer: GlobalPlannerDaemon enqueues one Priority=64 exploration
probe per registered team per cycle. Surface all counters in DrawPathingDebugGUI +
smart.runtime.status. Lock invariants with 6 tests.

**Rationale.** Simplest: "make ShouldShedLowPriority static-readable and add a third log
line." Rejected: Priority byte stays dead (shed-mode structurally unverifiable); stale-request
solves still write garbage Trajectories; flag flap produces noise but no clarity; no
p50/p99 hides degraded solver. BEST means lifecycle fully observable AND structurally
exercised by a real producer that proves the shed path works on every play session, not just
in unit tests. Single commit: all six pieces ship together because producer is meaningless
without expiry guard.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| Just promote ShouldShedLowPriority + add SHED-CLEAR log | 4 structural holes remain |
| Drop shed-mode entirely (let BoundedQueue drop-oldest handle it) | Drop-oldest is wrong eviction (would drop fresh MPC, keep stale probes) |
| `PathRequest` as class with `Cancel()` | GC pressure; breaks readonly-struct ABI; cancellation in every producer |
| Circuit breaker on solve latency | Trips on exceptions not slow solves; second breaker triples failure surface; observability beats automated suicide |
| Probe producer in GlobalCoordinatorDaemon | Coordinator semantic is role assignment; planner already iterating teams |
| Tick-relative expiry instead of wall-clock ms | Tick-relative mis-fires during pause; MonoClock is right time source per AETHER-DESIGN |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-017 | `EnqueueMonoTimestamp` on PathRequest | `Smart/Pathing/PathingService.cs:17-52` | 15 | — | XML doc forbids direct caller-set; new private/internal ctor accepts `(tech, myTeam, start, startVel, goal, goalVel, duration, capability, priority, enqueueMonoTimestamp)`; public ctor delegates with `stamp=0L` sentinel; RequestPath rewrites: `var stamped = new PathRequest(req.Tech, req.MyTeam, ..., MonoClock.Now()); Enqueue(stamped);`; test asserts `stamp>0` after RequestPath returns | Revert field + ctor overload (also revert L-085 expiry) |
| L-018 | Promote Backpressure singleton + read API | `Smart/Threading/PathRequestBackpressure.cs:17-60`; `Smart/Pathing/PathingService.cs:76-115,211-217` | 45 | — | `IPathingBackpressureReadout` (lives at `Smart/Pathing/`) exposes ShedActive, QueueDepth, QueueCapacity, DroppedTotal, ExpiredTotal, p50/p99, LastShedTransitionMono, LastClearTransitionMono. Public static accessor: `PathingService.Backpressure` returns the singleton IPathingBackpressureReadout (null when `!IsRunning`). Constructed in Init, reset/cleared at PathingService.Shutdown (lines 108-115) by setting singleton ref to null; PathSolveLoop references singleton. Renames `ShouldShedLowPriority` → `ShedActive` and `DroppedSinceLastReport` → `DroppedTotal`; OLD names preserved as `[Obsolete]` aliases until L-037 lands (intermediate compile gate — single-commit ship merges both slices) | Re-collapse to stack-local; null-guard public accessors |
| L-085 | Dequeue-side expiry + ExpiredCount (promoted from PRB-3) | `Smart/Pathing/PathingService.cs:255-268`; `Smart/Threading/PathRequestBackpressure.cs` | 25 | L-017, L-018 | Const PathRequestExpiryMs=1500 (5× median 300ms cadence); expiry BEFORE priority check (correctness); MonoClock.Seconds clamps neg dt; expired Priority=128 still drops. Note: MonoClock wraps `Stopwatch.GetTimestamp()` which IS wall-clock and ticks during pause — L-004/L-026 IsPaused gate suppresses enqueues during pause so post-resume queue does not contain pre-pause stale stamps | Remove expiry branch |
| L-037 | Hysteresis + edge-triggered logs | `Smart/Threading/PathRequestBackpressure.cs:38-53`; `Smart/Pathing/PathingService.cs:239-254` | 35 | L-018 | LowWaterFraction=0.25; ClearSustainMs=3000; Observe returns ShedTransition enum {None,Tripped,Cleared}; exactly one log per transition; periodic-gated-on-dropped DELETED (this deletion removes the consumer of L-018's renamed `DroppedSinceLastReport`/`ResetDroppedCounter` — L-018+L-037 must ship as a coordinated chunk). Coordinate with L-040 — both modify Observe(); merge their edits | Revert to void return + restore periodic log |
| L-038 | DrawPathingDebugGUI queue/shed/p50/p99 | `Smart/SmartForm.cs:575-615` | 25 | L-018, L-041 | Pathing block +2 lines; row yellow when shed=ON; box height 110→146; null-guarded shutdown race. p50/p99 fields display as `-` until L-041's accessors arrive (single-commit ships them together) | Revert label additions |
| L-039 | `smart.runtime.status` Pathing block | `Smart/Tooling/SmartConsoleCommands.cs:189-206` | 18 | L-018, L-041 | RuntimeStatus appends `Pathing.Queue: D/Cap Shed:ON|OFF Drop=N Exp=M\nPathing.Latency: p50=Yms p99=Zms`; null-guarded | Revert appends |
| L-040 | `[PATHING-DEPTH]` 60s histogram | `Smart/Pathing/PathingService.cs:211-254`; `Smart/Threading/PathRequestBackpressure.cs` | 35 | L-018 | Observe increments one of 4 bucket counters; PathSolveLoop emits every 60s regardless of activity; buckets reset; operator sees queue trending without drops. Coordinate with L-037 — both modify Observe(); ship in same chunk | Remove bucket counters + emission |
| L-041 | Reservoir sampling p50/p99 | `Smart/Threading/PathRequestBackpressure.cs`; `Smart/Pathing/PathingService.cs:270-286` | 75 | L-018 | RecordSolveLatency + read accessors; Vitter Algorithm R, 128-sample lock-free reservoir; uses `[ThreadStatic] Random` (or `System.Threading.ThreadLocal<Random>`) for thread-safe RNG (single shared Random is not thread-safe on .NET 4.6.1); Stopwatch wrap with finally; reset on Shutdown. LOC=75 covers full Vitter impl + percentile snapshot-and-sort + Stopwatch wrap | Delete RecordSolveLatency + reservoir + Stopwatch wrap |
| L-042 | `GetLastPathFresh` + `Trajectory.SolvedAtMono` | `Smart/Pathing/PathingService.cs:157-161`; `Smart/Pathing/TrajectoryOptimizer.cs:17-32` | 30 | L-017 | Trajectory.SolvedAtMono declared as `public long SolvedAtMono { get; private set; }` initialized to 0L, mutated via internal setter from PathingService.PathSolveLoop when `_lastPaths[req.Tech]=result.Trajectory`; GetLastPathFresh returns null when `now-SolvedAtMono>maxAgeMs`; existing GetLastPath stays | Delete GetLastPathFresh + field |
| L-062 | GlobalPlannerDaemon Priority=64 probe | `Smart/Planning/GlobalPlannerDaemon.cs:27-62`; `Smart/Pathing/PathingService.cs:17-52` | 90 | L-017, L-018, L-037 | After PlanOnce, ≤1 probe per team per cycle; Priority=64; PathRequest spec: `Start = team centroid (TryGetCentroid with sentinel TechId.Invalid) or Vector3.zero fallback`, `Tech = TechId.Invalid` (probe is team-scoped, not tech-scoped — PathSolveLoop must tolerate Invalid by writing to a per-team probe-cache slot rather than `_lastPaths`), `MyTeam = team.TeamId`, `Goal = team-centroid + ±200m quadrant-offset cycled round-robin (NE, NW, SE, SW)`, `Capability = VehicleCapability.Default`, `Duration = 5s`. SUPPRESSED when `!IsHost / !IsRunning / empty team / PathingService.Backpressure?.ShedActive == true`; own try/catch with `pathing-probe` tag | Delete probe block |

**Tests:** `PRB_PathRequest_Stamped_At_Enqueue`, `PRB_Stale_Request_Rejected_At_Dequeue`,
`PRB_Hysteresis_OneShot_PerTransition`, `PRB_Backpressure_Reset_On_Shutdown`,
`PRB_Exploration_Probe_Enqueues_LowPri`, `PRB_Trajectory_Stamped_With_SolvedAt`.

**Risks.** Readonly-struct ABI break if non-default ctor required — keep existing ctor;
RequestPath itself re-creates stamped record. Probe at 300ms × N teams under low capacity —
gated on ShedActive==false; ≤1/cycle/team; Priority=64 drops first. Reservoir sampling
torn writes on .NET 4.6 — Interlocked.Exchange on double supported; reader copies into stack
buffer; staleness OK for diagnostic. Hysteresis thresholds may mis-trip at small cap=64 —
LowWaterFraction tunable const; [PATHING-DEPTH] gives steady-state distribution. Cross-sub:
PRB-9 depends on GlobalPlannerDaemon alive — `[PATHING-DEPTH]` 0 across windows = strong
starvation signal; watchdog should treat "no [PATHING-DEPTH] in 180s" as daemon-death
heuristic. PathRequest +8 bytes — 64×8=512 bytes total footprint immaterial.

**Cross-subsystem deps:** threading-lifecycle-hooks, watchdog-restart,
globalplanner-daemon-lifecycle, diagnostics log routing, smartconsole runtime status.

---

### 3.9 Per-team coordinator/planner lifecycle

**Symptom.** TeamRuntime instances created lazily in `SmartRuntime._teams` are never removed
during session — only cleared wholesale at Shutdown. Across raids/captures/base swaps/PvP
reshuffles, `_teams` grows monotonically. Every daemon cycle iterates `_teams.Values` so
per-tick cost grows O(teams-ever-seen). `OnTankTeamChanged` performs non-atomic 4-step
migration (`OldTeam.DeregisterTech → state.UpdateTeam → World.UpdateTeam → NewTeam.RegisterTech`)
— racing daemon can observe tech in neither team, in old team with new TeamId stamped, or
doubly published. Per-team mutable state never reset; disbanded TeamId later reused inherits
stale state. Zero `[TEAM-LIFECYCLE]` logging.

**Root cause.** Garbage-collection gap (lazy create has no symmetric eviction; nothing acts on
DeregisterTech post-condition) + atomicity gap (4 independent mutations across 4 files with no
migration lock honoured by daemon iteration). Both share common ancestor: TeamRuntime has no
lifecycle FSM, so neither cleanup nor migration has a state to coordinate against. AUDIT-R2
§2.R2.B left team-change race as ACCEPTED-FOR-V0.2.

**Best fix.** Promote TeamRuntime to self-cleaning lifecycle FSM (Active → Draining →
Disposed) with three pieces: (A) lifecycle state + per-team `ReaderWriterLockSlim` daemons
take in read mode for whole iteration so cross-key migrations appear atomic; (B) single
`MigrateTech(state, newTeamId)` entry point taking migration lock, holding both team locks in
TeamId order (deadlock-free), swapping registration+UpdateTeam+World.UpdateTeam under locks,
never publishing to both; (C) `TeamReaperDaemon` (10s cadence) finds teams with TechCount==0
older than 30s default, flips Active→Draining (rejects new RegisterTech), waits one full
Coordinator+Planner cycle for in-flight publishes to drain, then Draining→Disposed and
TryRemoves. Every transition emits `[TEAM-LIFECYCLE]`. Daemons skip Disposed teams and bail
mid-iteration if their own team flips Disposed.

**Rationale.** Simplest: `if (team.TechCount==0) _teams.TryRemove(teamId,...)` inside
DeregisterTech. Rejected: (1) races daemon iteration (planner mid-PUCT holds team ref); (2)
races respawn (engine PurgeHost+Respawn pattern churns allocation); (3) races migration
(TechCount→0 mid-Deregister triggers eviction the next-line Register re-creates); (4) no
observability. BEST decouples eviction in time (grace period), coordinates with daemons
(migration lock + FSM), and is observable.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| Evict inside DeregisterTech | 4 races above |
| Single global team-table mutex | Daemons hold the lock for ~5-15 ms during StepOnce/PlanOnce, blocking unrelated main-thread Spawn / Team-change paths; RW-lock gives per-key granularity (rejection rationale corrected — ContinuousController.OnControlFrame does not access `_teams`, the actual contention is GetOrCreateTeam + OnTankTeamChanged) |
| Defer to Shutdown (status quo) | Confirmed leak; TryRemove has 0 callers |
| Per-team daemon thread (revert T2) | Was removed because it proliferated to ~70 threads contending with Aether |
| Copy-on-write team list per tick | Defers race; allocates List<TeamRuntime> per tick × 2 daemons × 30Hz |
| Volatile flag, no RW-lock | Volatile gives ordering not exclusion; can't tell daemon to stop atomically |
| Pagination of EnumerateTeams | Right idea for v0.4+ but premature; pagination on leaky table just delays manifestation |
| ConcurrentDictionary.TryUpdate with version stamps | Per-key; migration is two-key; no CD primitive spans without external lock |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-019 | TeamRuntime lifecycle FSM + RW-lock | `Smart/SmartRuntime.cs:314-358`; caller updates at `Smart/SmartForm.cs:248`, `Smart/Integration/SmartEventBridge.cs:356` (RegisterTech callers must capture+log return) | 85 | — | Transitions Active→Draining→Disposed only (Interlocked.CompareExchange + Debug.Assert); log per transition; **RegisterTech returns bool false when State!=Active**. State check must happen BEFORE `state.OwnerTeam = this;` mutation (current line 350) — otherwise refused-state leaves OwnerTeam set on a state with no team membership. Callers SmartForm.cs:248 and SmartEventBridge.cs:356 (currently discard return) MUST capture the bool: on false, log `[TEAM-LIFECYCLE-REGISTER-REFUSED]` and abort the spawn cleanly. **TeamRuntime gains Dispose() method** called from L-044 reaper Disposed transition; Dispose releases the ReaderWriterLockSlim kernel handle (RWLockSlim implements IDisposable; without explicit Dispose every reaped team leaks a kernel handle). State field read primitive: `Volatile.Read(ref _state)` consistent across all daemon read sites | Revert enum + TransitionTo + lock field |
| L-043 | MigrateTech atomic two-team | `Smart/SmartRuntime.cs:856` (insert after Deregister at 833-856, NOT at the EnumerateTeams docstring at :858-862); `Smart/Integration/SmartEventBridge.cs:346-357` | 55 | L-019 | Locks in TeamId.Value ascending order; Debug post-condition: exactly one team holds techId; if newTeam Disposed/Draining force-create fresh under migration lock | Revert MigrateTech; SmartEventBridge falls back to 4-step |
| L-044 | TeamReaperDaemon | `Smart/Coordination/TeamReaperDaemon.cs` (new); `Smart/SmartRuntime.cs:707` | 90 | L-019, L-023, L-061 | 10s cadence respecting cancellation; piggybacks on L-023 hardened EnqueueLongRunning (without it reaper dies on first FMOD pause). Predicate `TechCount==0 && now-FirstEmptyMonoTick>TeamEvictionGraceSeconds`. Eviction sequence on Disposed transition (BEFORE TryRemove): (a) call `PathingService.ForgetTeam(team.TeamId)` (per L-061) to clear `_threatFields` entry — otherwise per-team threat-field DoubleBuffers leak indefinitely; (b) call `team.Dispose()` to release the L-019 RWLockSlim kernel handle; (c) `_teams.TryRemove(teamId,...)`. Active→Draining → sleep 2× max(planner,coordinator) period → recheck → Disposed → cleanup → TryRemove; recheck-abort transitions back to Active if respawned | Comment out EnqueueLongRunning line |
| L-045 | Daemon iteration honours lifecycle | `Smart/Coordination/GlobalCoordinatorDaemon.cs:35-39`; `Smart/Planning/GlobalPlannerDaemon.cs:40-44`; `Smart/Pathing/PathingService.cs:192` | 30 | L-019 | Daemon foreach `TryEnterRead` at top; skip with log-once-per-cycle on false; release in finally; helper extension `TryRunUnderReadLock(Action)` keeps DRY. Guard `TryEnterRead` against `ObjectDisposedException` — when L-044 reaper TryRemoves a team mid-foreach, the snapshot enumeration may still hold a reference to a disposed TeamRuntime; catch ObjectDisposedException → skip team | Revert to direct foreach |
| L-046 | Coordinator hard-stop on disposal mid-StepOnce | `Smart/Coordination/Coordinator.cs:109-122`; `Smart/Planning/StrategicPlanner.cs:34-53` | 30 | L-019, L-045 | Add overload pair: `StepOnce()` keeps the owner-less signature for Coordinator.RunLoop (Coordinator.cs:137) and StrategicPlanner.RunLoop (StrategicPlanner.cs:68) callers, forwarding to `StepOnce(owner: null)`. New `StepOnce(TeamRuntime owner)` reads `owner?.StateVolatile` at top; bails on owner!=null AND State!=Active (no publish); Debug back-ref check; file-only dedup log on skip. Same shape for PlanOnce. LOC bumped 20→30 to cover overload pair | Remove State checks (L-045 read-lock still protects) |
| L-063 | TeamId reuse fresh-state guarantee | `Smart/SmartRuntime.cs:809,336` | 20 | L-019, L-044 | Ctor stamps `CreatedMonoTick` + monotonic Generation; GetOrCreateTeam never returns Disposed; log shows gen=2 for TeamId=1 proves re-creation; all per-team mutable state is instance-field | Drop Generation+CreatedMonoTick (lose log column only) |
| L-064 | AetherTuning entries | `Smart/World/AetherTuning.cs`; **`AI/Tunables/Catalog/AetherTunables.cs`** (Register method — the actual TunableRegistry entry point invoked from `AIModuleBootstrap.InitAIModules`) | 35 | L-044 | TeamEvictionGraceSeconds default 30.0 range [0,300]; TeamReaperCyclePeriodMs default 10000 range [1000,60000]; visible in LiveTweakerPanel via TunableMenuBridge. (a) Add 2 static fields to AetherTuning.cs (~6 LOC); (b) Add 2 RegisterFloat/RegisterInt blocks to AetherTunables.Register (~25 LOC binding the new fields). SmartConsoleCommands.cs is NOT the registration site — only [DevCommand] handlers live there | Remove fields + Register entries; const fallback |
| L-065 | Diagnostic counters | `Smart/SmartRuntime.cs:862`; `Smart/SmartForm.cs:583-615` | 25 | L-019, L-043, L-044 | LifetimeTeamsCreated/Evicted/Migrations Interlocked; DrawPathingDebugGUI shows 'Teams: N (created=X evicted=Y migrations=Z)'; invariant `Created-Evicted == _teams.Count` ±1 verified per tick; one-line summary at Shutdown | Drop counters + GUI line |

**Tests:** `TeamLifecycle_EmptyTeam_EvictedAfterGracePeriod`,
`TeamLifecycle_RespawnWithinGrace_AbortsDrain`,
`TeamLifecycle_MigrateTech_AtomicallyExactlyOneTeamHoldsIt`,
`TeamLifecycle_DaemonRespectsDrainingState`, `TeamLifecycle_LeakInvariant_CreatedMinusEvictedEqualsLive`,
`TeamLifecycle_DaemonIteration_NoDeadlockOnReaper`, `TeamLifecycle_TeamIdReuse_FreshState`.

**Risks.** Grace period (30s) holds memory for never-respawning teams — TeamRuntime ~few KB
(no MPC arrays/PUCT tree retained); tunable. RW-lock overhead — read-acquire lock-free
fast-path; reaper write every 10s; read-side contention essentially zero. MigrateTech two-team
deadlock — strict TeamId-ascending order codified by editorconfig grep rule; no other
two-team lock path exists. Reaper mid-publish loses goal — explicit 2× period sleep between
Draining and Disposed; L-046 State check second layer; published goals stale-tolerant (1s
TTL). Disposed teams referenced by `SmartPerTechState.OwnerTeam` — DeregisterTech already
clears; L-019's Active→Draining also walks empty `_techs`. Cross-sub TAE: L-044 piggybacks on
`EnqueueLongRunning` (Section 3.1). Perf: L-045 TryEnterRead ~50ns × 50 teams × 30Hz = 75µs/s,
negligible vs ~5-15ms StepOnce.

**Cross-subsystem deps:** threading-lifecycle-hooks (TeamReaperDaemon piggybacks on the
hardened EnqueueLongRunning); smart-event-bridge team-change handler (L-043 replaces 4-step
migration); aether-tuning-flags (L-064 adds two AetherTuning entries); global-daemon-cycle-pacing
(L-045 re-baseline elapsedMs budget); world-model-team-rebind (L-043 calls World.UpdateTeam
under migration lock — verify thread-safety).

---

### 3.10 Worker health watchdog + diagnostics surface

**Symptom.** When FMOD fires `OnApplicationPauseManual True`, all 22 WorkerPool threads die
UnexpectedExit AND 9 long-running daemons die silently. The form selector still says "Active"
because `SmartRuntime.IsRunning` checks only `Pool != null && Pool.IsRunning && World != null`
— neither flag observes per-thread liveness. `SmartConsoleCommands.RuntimeStatus` does not
list workers. `DrawPathingDebugGUI` shows configured worker count, not live count. Every
Smart-driven tech permanently DOA with zero visible signal until operator manually scrolls
output_log.txt.

**Root cause.** Three gaps stack: (1) `WorkerLifecycleRegistry.Deregister` fires on every
worker exit path inside finally, so live count collapses to zero — but nothing READS the
registry between Init and CancelAllAndJoin. (2) Pool object stays non-null and `Pool.IsRunning`
returns true (only flips on Dispose), so `SmartRuntime.IsRunning` lies. (3) Each daemon's
RunLoop is one-shot — no Respawn entry point or expected-count anchor anywhere, so even
knowing a daemon is dead, you cannot re-spawn just that daemon. Watchdog architecture is
entirely absent.

**Best fix.** `WorkerHealthMonitor` records SPAWN MANIFEST (name → factory delegate for every
daemon + expected pool worker count) at Init time. `Snapshot()` returns live/expected counts +
missing-name list. Polled from `SmartForm.Operations`. Fires `[SMART-WORKERS-DEAD]` exactly
once per drop-edge AND immediately invokes per-daemon Respawn through recorded factory.
Pool workers respawned via new `WorkerPool.ReplaceDeadWorkers()`. DrawPathingDebugGUI adds
red-when-degraded "Workers: live/expected" line. `smart.workers.list` enumerates registry +
CTS status; `smart.workers.respawn` forces manifest-driven recovery. `smart.runtime.status`
surfaces backpressure dropped, AetherFuser spikes, CircuitBreaker trip totals. Watchdog
NEVER false-fires: diffs live names against a snapshot taken when all expected names were
present.

**Rationale.** Simplest: single `Pool.HasDeadWorkers` polled from Operations. Rejected because
(a) cannot recover (operator has to /reload form, losing learned profile); (b) cannot
distinguish which worker died (different factories needed — AetherFuser owns CircuitBreaker
you do NOT want to reset, Trainer-Threat owns TrainerWorker with model refs to reuse);
(c) cannot dedup (Operations at ~30 Hz × N techs spams 30N times/sec). Watchdog must be
FACTORY-CARRYING + EDGE-TRIGGERED + IDEMPOTENT.

**Rejected alternatives.**

| Alternative | Reason rejected |
|---|---|
| `Pool.IsRunning` returns false on any dead worker | Disables every healthy worker too; same lack of operator signal; cascades failure |
| Subscribe `ThreadingDiagnostics.WorkerTerminated`, respawn from event | Handler runs on dying thread inside finally; spawning Thread there during cascade races abort itself |
| Unity MonoBehaviour with own Update() | Second lifecycle to manage; Operations already runs at ~30Hz per tech and is canonical Smart main-thread tick |
| Pool-internal restart of dead `_workers[i]` | Pool only knows generic 22 workers, not 9 named daemons (dedicated threads outside `_workers[]`) |
| Log `[SMART-WORKERS-DEAD]` every tick until recovery | 240 lines/sec with 8 Smart techs; drowns diagnostic |
| Persist worker health to disk | CircuitBreakerCalibration session report already covers post-mortem; watchdog needs LIVE |

**Implementation items.**

| ID | Title | Files | LOC | Deps | Invariants locked | Rollback |
|---|---|---|---|---|---|---|
| L-020 | `SnapshotLive()` accessor | `Smart/Threading/WorkerLifecycleRegistry.cs:64` | 28 | — | Returns array-copy under `_lock`; P11 T7 Item 63 deleted Live() — re-add as `SnapshotLive()` (NOT `LiveSnapshot()`; canonical name owned by L-020 and consumed by L-005 + L-047); XML doc names BOTH `WorkerHealthMonitor` and `SmartTestSuite.Threads_AllBackground_AfterInit` as consumers (not "sole consumer"); snapshot taken WITHOUT `_cancelAllInProgress` flip. For pool workers `WorkerHandle.Name` carries the bare workerName ('Smart#N' — no `SmartWorker-` prefix); for long-running threads `Name` = `SmartLR-label-counter`. Test/L-047 manifest design must NOT assume `SmartWorker-` prefix on pool handles | Delete SnapshotLive() |
| L-021 | EnqueueLongRunning returns name | `Smart/Threading/WorkerPool.cs:122-157`; `Smart/Pathing/PathingService.cs:100-101` | 12 | — | Signature void→string returning resolved 'SmartLR-label-N'; non-null on success; null when `_disposed` (current silent-return failure path now signals via null). Callers may ignore return value (source-compatible); WorkerHealthMonitor uses returned name as canonical key — but stores manifest keyed by the BARE label ('AetherFuser', 'Trainer-Intent') not the full thread name (counter changes per respawn) | Revert signature; use convention name |
| L-047 | `WorkerHealthMonitor` class | `Smart/Threading/WorkerHealthMonitor.cs` (new) | 220 | L-020 | Manifest append-only during Init, sealed at SealManifest. **Manifest keys by bare daemon label** ('AetherFuser', 'GlobalPlanner', 'GlobalCoordinator', 'ThreatFieldRebuild', 'PathSolve', 'Trainer-Intent', 'Trainer-ActionValue', 'Trainer-Residual', 'Trainer-Threat') — NOT the full thread name (which carries per-spawn counter that changes on respawn, breaking dedup forever after the first respawn). Diff against `SnapshotLive()` does a label-strip from `SmartLR-{label}-{counter}` and matches by `{label}`. Snapshot() returns value struct (never null); drop-edge dedup via `_lastReportedMissingSet`; re-arms when set returns empty; per-name respawn-in-flight CAS guard; log format `[SMART-WORKERS-DEAD] expected=N live=M missing=name1(age=Ts) — auto-respawn fired`. Per-name registration timestamp (`MonoClock.Now()` at RegisterDaemon) and per-name first-missing-detected timestamp (set at drop-edge, cleared at re-arm) maintained inside monitor — `age` token = `Now - firstMissingAt`. Tick() never throws (try/catch + LogWarnFileOnly internal failure) | Delete file + revert Init registrations |
| L-048 | `ReplaceDeadWorkers()` | `Smart/Threading/WorkerPool.cs:160-245` | 75 | L-020 | Walks `_workers[i]` for `!thread.IsAlive` (canonical .NET 'thread terminated' predicate — cleaner than ThreadState `Stopped|Aborted` bitwise check); allocates NEW Thread same name; new per-worker CTS linked to root; Deregisters dead handle, registers new; returns replacement count; idempotent on alive workers; guarded `if (_disposed) return 0` (covers Dispose-window race) | Delete method; watchdog only respawns named daemons |
| L-066 | Init registers daemons with monitor | `Smart/SmartRuntime.cs:694-708`; `Smart/Learning/LearningService.cs:89-92` | 70 | L-047, L-021 | Each EnqueueLongRunning wrapped by `WorkerHealthMonitor.RegisterDaemon(label, factoryDelegate)` capturing exact same lambda. **Factory contract clarification**: factory must be IDEMPOTENT on respawn — for AetherFuser the factory reuses the existing `Perception` static field reference (test asserts `ReferenceEquals(Perception, postRespawnPerception)`); for Trainers it reuses TrainerWorker instances stored as static refs in LearningService. Factory delegate captures the static field by closure; on respawn factory reads `Perception` (must be non-null OR re-throw to skip respawn — L-067 BeginShutdown gates the shutdown-window null). Same for Intent/ActionValue/Residual/Threat statics in LearningService. SealManifest at Init end; Reset at top of Init | Revert wrap calls |
| L-067 | Shutdown calls BeginShutdown | `Smart/SmartRuntime.cs:754` | 8 | L-002, L-047 | BeginShutdown flips an internal `_shutdownInProgress` flag that suppresses `[SMART-WORKERS-DEAD]` during legitimate teardown. Read ALSO covers `WorkerLifecycleRegistry.IsTearingDown` (the L-002 public property over private `_cancelAllInProgress`) so the registry's own teardown signal is independently honoured — no separate mirror state. Reset called after CancelAllAndJoin | Skip BeginShutdown (one false-positive during shutdown) |
| L-072 | Operations calls Tick() | `Smart/SmartForm.cs:386` | 10 | L-047, L-066 | CompareExchange on `_lastWatchdogTickMs` 500ms interval (same pattern as ObserveWorldTechsIfDue); placed BEFORE per-state branch; wrapped in try/catch with `watchdog-tick-err` tag | Revert try-block |
| L-073 | DrawPathingDebugGUI Workers line | `Smart/SmartForm.cs:583-615` | 22 | L-047 | Reads `Snapshot()` (cheap struct); GUI.contentColor=Color.red on degraded, restored after; format `Workers: M/N alive` (green) or `Workers: M/N DEGRADED` with a second sub-line `missing=name1,name2,...` (truncated to fit W=280px box); box height 110→128 (or 110→146 if 2-line wrap) | Revert GUI line |
| L-074 | `smart.workers.list` command | `Smart/Tooling/SmartConsoleCommands.cs:189` | 40 | L-020, L-047 | Per-line `name=X thread=Y(IsAlive=... ThreadState=...) cts=cancelled|active ageSec=N`; sorted alphabetically; summary line `TOTAL: live=M expected=N missing=K`; access Cheat, user Host | Delete DevCommand |
| L-075 | `smart.workers.respawn` command | `Smart/Tooling/SmartConsoleCommands.cs:189` | 30 | L-047, L-048 | Calls `ForceRespawnAll()` then `ReplaceDeadWorkers`; format `Respawned daemons: name1,name2 (failed: name3=ex); pool replaced: K`; optional `<name>` arg for single-daemon | Delete DevCommand |
| L-076 | runtime.status surfaces backpressure/spikes/CB | `Smart/Tooling/SmartConsoleCommands.cs:189-207`; `Smart/Pathing/PathingService.cs:78`; `Smart/Threading/CircuitBreakerCalibration.cs:36` | 55 | L-018, L-047 | Three new lines: PathBackpressure dropped/depth/cap; AetherFuser spikes/last; CB trips per-daemon. New `GetPathBackpressureSnapshot()` accessor on PathingService (requires L-018 singleton lift — without it the backpressure is stack-local inside PathSolveLoop and unreachable). CB `SnapshotTotals()` returns `List<CircuitBreakerTripCount>` where `CircuitBreakerTripCount` is a new named readonly struct in CircuitBreakerCalibration.cs (`public readonly struct CircuitBreakerTripCount { public readonly string Name; public readonly int Count; ... }`) — **DO NOT use `List<(string,int)>`**: .NET 4.6.1 BCL lacks `System.ValueTuple`, and the project explicitly avoids it (see `OnlineTrainer.cs:21-26` TrainStepResult and `PUCTSearch.cs:9-13` for the canonical named-struct pattern). Backwards-compatible (appended lines only) | Delete appended blocks |

**Tests:** `WorkerHealthMonitor_NeverFires_WhenAllAlive`,
`WorkerHealthMonitor_DropEdge_FiresOnceThenDedups`, `WorkerHealthMonitor_RespawnIdempotent`,
`WorkerLifecycleRegistry_SnapshotLive_ReturnsAll`,
`WorkerPool_ReplaceDeadWorkers_RespectsAliveWorkers`, `Watchdog_LogLineFormat_Stable`.

**Risks.** Tick churn during partial cascade — dedup prevents steady-state spam; 9 lines
during real cascade is informative. Respawn storm if Mono keeps aborting new threads — per-name
RespawnAttempts counter; ≥5 in 60s gives up on that daemon with `[SMART-WORKER-RESPAWN-GIVING-UP]`.
Reused Perception/TrainerWorker carries breaker `_failureCount` — correct behavior; bounded by
respawn-attempts cap. EnqueueLongRunning signature break — IL-level source-compatible; XML doc
remarks. New static singleton — bound to Init/Shutdown; no MonoBehaviour. Cross-sub: WHW-3
captures factory delegates referencing Perception/GlobalPlannerDaemon — test
`SnapshotLive_ReturnsAll` catches if AetherFuser not in live set 500ms post-Init.

**Cross-subsystem deps:** threading-lifecycle-hooks, thread-abort handling, pause-cascade
recovery, smart-console-commands surface, draw-pathing-debug-gui.

---

## 4. File-by-file impact map

Files touched, sorted by directory. Each row shows which global items edit that file (so the
implementer can batch edits per-file).

| File | Items |
|---|---|
| `Smart/SmartForm.cs` | L-006†, L-007, L-025‡, L-032, L-038, L-053, L-057, L-065, L-069, L-072, L-073, L-078, L-082 |
| `Smart/SmartRuntime.cs` | L-004, L-011, L-019, L-043, L-044, L-046, L-049, L-055, L-059, L-061, L-063, L-065, L-066, L-067 |
| `Smart/SmartLifecycleShim.cs` (new) | L-025 |
| `Smart/Threading/AbortGuard.cs` (new) | L-001 |
| `Smart/Threading/DaemonWatchdog.cs` (new) | L-049 |
| `Smart/Threading/WorkerPool.cs` | L-005, L-021, L-022, L-023, L-048 |
| `Smart/Threading/WorkerLifecycleRegistry.cs` | L-002, L-005, L-020 |
| `Smart/Threading/WorkerHealthMonitor.cs` (new) | L-047 |
| `Smart/Threading/PathRequestBackpressure.cs` | L-018, L-034, L-037, L-040, L-041, L-085 |
| `Smart/Threading/ThreadingDiagnostics.cs` | L-003 |
| `Smart/Threading/CircuitBreakerCalibration.cs` | L-076 |
| `Smart/World/AetherFuser.cs` | L-024, L-026 |
| `Smart/World/EventBus.cs` | L-012, L-036 |
| `Smart/World/WorldModel.cs` | L-083 |
| `Smart/World/TechLifecycleRegistry.cs` (new) | L-009 |
| `Smart/World/TechLeakWatchdog.cs` (new) | L-056 |
| `Smart/World/IntentRegistry.cs` | L-028 |
| `Smart/World/DamageHintBuffer.cs` | L-028 |
| `Smart/World/HealthSidecar.cs` | L-028 |
| `Smart/World/ObservationIntake.cs` | L-028 |
| `Smart/World/AetherTuning.cs` | L-064 |
| `Smart/Planning/GlobalPlannerDaemon.cs` | L-024, L-026, L-045, L-062 |
| `Smart/Planning/StrategicPlanner.cs` | L-046 |
| `Smart/Coordination/Coordinator.cs` | L-024, L-029, L-046 |
| `Smart/Coordination/GlobalCoordinatorDaemon.cs` | L-024, L-026, L-045 |
| `Smart/Coordination/TeamReaperDaemon.cs` (new) | L-044 |
| `Smart/Coordination/HostAuthorityCoordinator.cs` (new) | L-030 |
| `Smart/Coordination/TrainerBarrier.cs` (new) | L-031 |
| `Smart/Pathing/PathingService.cs` | L-017, L-018, L-021, L-024, L-026, L-029, L-033, L-037, L-040, L-041, L-042, L-045, L-061, L-062, L-076, L-085 |
| `Smart/Pathing/TerrainMap.cs` (TerrainMapSnapshot class is co-located inside this file at lines 11-34 — NO separate TerrainMapSnapshot.cs file exists) | L-035 |
| `Smart/Pathing/TerrainPublication.cs` (new) | L-033 |
| `Smart/Pathing/TrajectoryOptimizer.cs` | L-042 |
| `Smart/Pathing/IWorldResettable.cs` (new) | L-016 |
| `Smart/Pathing/WorldResetRegistry.cs` (new) | L-016 |
| `Smart/Learning/OnlineTrainer.cs` | L-013, L-024, L-026, L-058, L-071 |
| `Smart/Learning/ThreatAssessmentModel.cs` | L-013 |
| `Smart/Learning/OpponentIntentClassifier.cs` | L-013 |
| `Smart/Learning/ActionValueEstimator.cs` | L-013 |
| `Smart/Learning/TrajectoryResidualModel.cs` | L-013 |
| `Smart/Learning/LearningService.cs` | L-059, L-060, L-066, L-070, L-080 |
| `Smart/Learning/ProfilePersistence.cs` | L-013, L-014, L-015, L-079 |
| `Smart/Learning/ProfileSelfTest.cs` (new) | L-080 |
| `Smart/Learning/AutosaveWorker.cs` (new) | L-060 |
| `Smart/Learning/QuitSaveCoordinator.cs` (new) | L-078 |
| `Smart/Learning/LeadResidualRecorder.cs` | L-059 |
| `Smart/Learning/PlanTransitionPublisher.cs` | L-059 |
| `Smart/Learning/TargetObservationSequenceBuffer.cs` | L-028 |
| `Smart/Learning/Migrations/SmartMigrationAttribute.cs` (new) | L-014 |
| `Smart/Learning/Migrations/0001_initial_schema.cs` | L-014 |
| `Smart/Learning/Migrations/0002_BpttUnfreeze.cs` | L-014 |
| `Smart/Learning/Migrations/0003_TlvSectionBody.cs` (new) | L-079 |
| `Smart/Integration/SmartEventBridge.cs` | L-010, L-019 (RegisterTech caller), L-043, L-061 |
| `AI/TankAIHelper.cs` (shell — path is `TAC_AI/AI/TankAIHelper.cs`, NOT `AI/Forms/TankAIHelper.cs`) | L-008, L-050, L-051, L-052 |
| `AI/Forms/AIFormRegistry.cs` (shell) | L-006, L-027, L-053 (add `partial` keyword), L-054 |
| `AI/Forms/AIFormRegistry.Debug.cs` (new partial-class shell file) | L-053 |
| `AI/Tunables/Catalog/AetherTunables.cs` (shell — actual TunableRegistry registration site) | L-064 |
| `Smart/Tooling/SmartConsoleCommands.cs` | L-039, L-074, L-075, L-076 |
| `Smart/Tests/SmartTestSuite.cs` | L-005, L-068, L-077, L-084, multiple per-subsystem tests |
| `Smart/Tests/AbortSurvivalTests.cs` (new) | L-077 |
| `Smart/Tests/PathingResetTests.cs` (new) | L-068 |
| `Smart/Tests/RoutingCompletenessCatcher.cs` (new) | L-081 |

> † L-006 = `AIFormRegistry.Clear()` edit (shell file)
> ‡ L-025 lives in its own new file; SmartForm just calls Install/Uninstall (L-007)

---

## 5. Test matrix

| Test | File | Asserts | Locks (item IDs) |
|---|---|---|---|
| WorkerPool_AbsorbsAbort_ContinuesProcessing | Tests/AbortSurvivalTests.cs | Worker.IsAlive + Counter reaches 100 after 5 aborts in 50ms gaps | L-022 |
| LongRunning_RespawnsOnAbort | Tests/AbortSurvivalTests.cs | Counter daemon resumes incrementing after single Thread.Abort | L-023 |
| AbortGuard_TripsOnStorm | Tests/AbortSurvivalTests.cs | 8 absorbs in <10s; 8th returns ExitForRespawn; one `[WORKER-ABORT-STORM]` line | L-001 |
| DaemonWatchdog_RespawnsCanonical | Tests/AbortSurvivalTests.cs | Fake canonical daemon respawned via factory, capped at 5/session | L-049 |
| DaemonRunLoop_AbortDuringWaitOne_Survives | Tests/AbortSurvivalTests.cs | Abort during `WaitHandle.WaitOne` does not terminate; next tick within 200ms | L-024 |
| WorkerLifecycle_NoCanonicalDaemonMissingAfterRepeatedAborts | Tests/AbortSurvivalTests.cs | After 10 aborts per daemon, registry contains all 9 canonical names | L-022 + L-023 + L-024 + L-049 |
| Threads_AllBackground_AfterInit | Tests/SmartTestSuite.cs | Every WorkerHandle.Thread.IsBackground == true; snapshot.Length ≥ 9 (the 9 canonical daemons) | L-005 |
| Shutdown_Idempotent_OnRepeatRequestShutdown | Tests/SmartTestSuite.cs | RequestShutdown twice → IsRunning==false + Pool==null + no throw | L-004 |
| IsPaused_StopsDaemonWork | Tests/SmartTestSuite.cs | GlobalPlannerDaemon counter advances ≤1 while paused, ≥2 after clear | L-026 |
| AIFormRegistry_Clear_RunsDeInit | Tests/SmartTestSuite.cs | After Clear: IsRunning==false + Pool==null | L-006 |
| RoutingCompletenessTest | Tests/SmartTestSuite.cs | 12 RawTech spawns + 2s wait → LiveRoutings.Count==12 + FormId!=null + no OnTechSpawnFailed | L-027 + L-050 + L-051 |
| RouteTech_Idempotent | Tests/SmartTestSuite.cs | Two RouteTech calls → exactly one OnTechSpawn invocation | L-027 |
| RouteTech_OnTechSpawnThrows_FallsBackToModified | Tests/SmartTestSuite.cs | Active throws → FormId=='Modified' + ExceptionType=='NullReferenceException' | L-027 |
| DelayedSubscribe_HandlingDetermineThrows_StillRoutes | Tests/SmartTestSuite.cs | Inject HandlingDetermine NRE → DriverType=Tank + FormId='Smart' + Reason=HandlingDetermineFailed | L-050 |
| OnPreUpdate_ReclaimsDroppedInvoke | Tests/SmartTestSuite.cs | Drop Invoke + enable + OnPreUpdate → FormId!=null + Reason=Reclaimed | L-051 |
| FormSwap_RoutesAllLiveHelpersToNewForm | Tests/SmartTestSuite.cs | SetActive('Modified') with 5 helpers → all 5 FormId=='Modified' + Reason=FormSwapped | L-054 |
| RoutingCompletenessCatcher_Selfheals | Tests/RoutingCompletenessCatcher.cs | Inject helper FormId=null age=10s → `[ROUTING-ORPHAN]` once + FormId!=null after tick | L-081 |
| RoutingOverlay_RendersWithoutAllocation | Tests/SmartTestSuite.cs | DrawRoutingDebugGUI with 50 helpers → GC delta <1KB/frame | L-053 |
| TechLifecycleRegistry_DeregisterFanOut_EveryRegisteredSidecarForgotten | Tests/SmartTestSuite.cs | Every sidecar's SnapshotKeys does not contain the TechId after Deregister | L-009 + L-028 + L-029 + L-055 |
| TechLifecycleRegistry_OneSidecarThrows_OthersStillForget | Tests/SmartTestSuite.cs | Poison index-2; sidecars 0,1,3,4,5 still receive Forget; `[TECH-LEAK-FORGET-FAIL]` logged | L-009 + L-055 |
| TechLeakWatchdog_ChronicLeak_LogsOncePerSession | Tests/SmartTestSuite.cs | Phantom-key sidecar → exactly one `[TECH-LEAK]` across two RunLoop ticks | L-056 |
| SmartEventBridge_DetachPerTank_OrphanSweepPath_DropsHandler | Tests/SmartTestSuite.cs | DetachPerTank(TechId) drops _damageHandlers count by 1 | L-010 |
| OrphanSweep_TeamIdFallback_FindsTeamViaTeamRuntimeWalk | Tests/SmartTestSuite.cs | PriorBelief.null + entry.Team default → FindTeamForTech recovers; team.DeregisterTech true | L-011 |
| TestHostTransitionFlush | Tests/SmartTestSuite.cs | host→client→host: checkpoint byte-identical to clean Shutdown; all events accounted; 8 `[TRAINER-HOSTCHANGE]` lines; drop counter exactly == injected | L-030 + L-058 + L-070 + L-059 + L-031 |
| TestHostFlapDebounce | Tests/SmartTestSuite.cs | Notify(false)+Notify(true) within 250ms → zero HostChanged events | L-030 |
| TestTrainerInFlightStepCompletes | Tests/SmartTestSuite.cs | HostChanged(false) during _adam.Step → Step completes + _publishedParams.Write once + InFlightStepCompleted=true | L-058 |
| TestProducerGateCoverage | Tests/SmartTestSuite.cs | Roslyn-style scan: every `.EventQueue.Enqueue(` preceded by `AcceptingTrainingEvents` read | L-059 |
| TestProfileReloadOnPromotionRespectsMtime | Tests/SmartTestSuite.cs | Older disk → no reload; newer disk → LoadParameters + republish BEFORE Resume | L-070 |
| TestBarrierPartialInitDoesNotDeadlock | Tests/SmartTestSuite.cs | 2 of 4 trainers registered → barrier sizes to 2; OnHostLost completes <500ms | L-031 |
| Autosave_FiresEvery30sWhenDirty | Tests/SmartTestSuite.cs | 65 simulated seconds + dirty → SaveProfile ≥2 calls | L-060 |
| Autosave_SkipsWhenNotDirty | Tests/SmartTestSuite.cs | 65s without dirty → 0 SaveProfile + `[PROFILE-AUTOSAVE] saved=0 skipped=2` | L-060 |
| QuitSave_FiresOnApplicationQuit | Tests/SmartTestSuite.cs | Singleton.ApplicationQuitEvent.Send() → SaveProfile exactly once within 250ms | L-078 |
| QuitSave_IdempotentAcrossAllThreeSources | Tests/SmartTestSuite.cs | Fire all 3 sources → SaveProfile exactly once total | L-078 |
| QuitSave_HonorsBudget | Tests/SmartTestSuite.cs | Mutex held 500ms → QuitSave returns <250ms + `BUDGET-EXCEEDED` log + saved file intact | L-078 + L-013 |
| Trainer_HoldsSaveMutexDuringStep | Tests/SmartTestSuite.cs | Concurrent Monitor.TryEnter blocked ≥1ms during TrainOneMinibatch | L-013 |
| Save_DoesNotTearParameters | Tests/SmartTestSuite.cs | Adversarial trainer mutating 100ms + concurrent Save → saved float[] bit-identical to some observed snapshot | L-013 |
| Flush_DrainsResidualEvents | Tests/SmartTestSuite.cs | 5 events queued (batch=32; actual MinibatchSize const in all 4 models) + FlushPendingForPersist → returns 5 + Adam.T++ + _params changed | L-013 |
| MigrationRegistry_CoversAllVersions | Tests/SmartTestSuite.cs | Every integer in [0, CurrentSchemaVersion) has exactly one [SmartMigration] handler | L-014 |
| MigrationRegistry_RejectsFutureSchema | Tests/SmartTestSuite.cs | SchemaVersion = Current+1 → TryLoad false + failureCategory='future-schema' + corrupt preserved | L-014 |
| MigrationLadder_RunsForwardInOrder | Tests/SmartTestSuite.cs | v0 → v2 invokes M0001.Up + M0002.Up in order; final SchemaVersion==2 | L-014 |
| Migrate_V2ToV3_RoundTrip | Tests/SmartTestSuite.cs | v2 flat float[] file loads as v3 + weights bit-identical | L-079 |
| Load_SkipsUnknownTag | Tests/SmartTestSuite.cs | v3 with 0xFFFF tag → loads + preserved in UnknownTags + re-emitted on Save | L-079 |
| Save_MaintainsTwoBackups | Tests/SmartTestSuite.cs | 3 Saves → .penultimate==save1, .previous==save2, primary==save3 | L-015 |
| Load_FallsThroughAllTiers | Tests/SmartTestSuite.cs | Corrupt primary+.previous → .penultimate loads + `[PROFILE-LOAD-FAIL] tier=penultimate` | L-015 |
| Load_AllTiersFail_GlorotFallback | Tests/SmartTestSuite.cs | Corrupt all → null + `[PROFILE-LOAD-FAIL] tier=glorot` + Glorot retained | L-015 |
| AllThreeSaveSources_LogUniformly | Tests/SmartTestSuite.cs | OnEngineSave + autosave + quit → 3 distinct lines matching `\[PROFILE-(MANUAL-SAVE\|AUTOSAVE\|QUIT-SAVE)\] ` | L-032 + L-060 + L-078 |
| SelfTest_DetectsBrokenSerializer | Tests/SmartTestSuite.cs | Mock model returns nondeterministic bytes → SelfTest throws + `[PROFILE-SELFTEST-FAIL]` + Init proceeds without LoadProfile | L-080 |
| RegistryEnumeratesAllExpectedHooks | Tests/PathingResetTests.cs | WorldResetRegistry.Count == 6 with canonical names | L-016 |
| AtomicSwap_NoSpliceUnderLoad | Tests/PathingResetTests.cs | 1000 OnWorldReset × 64 worker threads at 1kHz → zero splices (terrain.Gen==threatFields.Gen) | L-033 |
| ThreatFieldDictionary_NoGrowthAcrossTeamFlips | Tests/PathingResetTests.cs | 100 team flips → _threatFields bounded by active count + `[PATHING-FORGET-TEAM]` per flip | L-061 |
| BackpressureLatchClearsOnReset | Tests/PathingResetTests.cs | 1000 saturation ticks → ShouldShed==true; OnWorldReset → ShouldShed==false + `[PATHING-SHED-RESET]` | L-034 |
| FreshlyAllocatedFlag_FlipsCorrectly | Tests/PathingResetTests.cs | New TerrainMap IsFreshlyAllocated=true + IsPopulated=false; after refresh → flips | L-035 |
| WorldResetEventsBracketAllResets | Tests/PathingResetTests.cs | 10 reset cycles → exactly 10 WorldResetting + 10 WorldResetCompleted in order | L-036 |
| PRB_PathRequest_Stamped_At_Enqueue | Tests/SmartTestSuite.cs | EnqueueMonoTimestamp > 0 within 100ms of MonoClock.Now() | L-017 |
| PRB_Stale_Request_Rejected_At_Dequeue | Tests/SmartTestSuite.cs | Stamp=Now-2000ms → ShouldExpire true + ExpiredTotal++ | L-085 |
| PRB_Hysteresis_OneShot_PerTransition | Tests/SmartTestSuite.cs | 80% for 3500ms then 10% for 3500ms → exactly one Tripped + one Cleared | L-037 |
| PRB_Backpressure_Reset_On_Shutdown | Tests/SmartTestSuite.cs | After mutate+Shutdown+Init → all counters 0 + ShedActive false | L-018 |
| PRB_Exploration_Probe_Enqueues_LowPri | Tests/SmartTestSuite.cs | ExplorationProbe.IssueOneShotForTeam → QueueDepth++ + Priority<128 + stamp>0 | L-062 |
| PRB_Trajectory_Stamped_With_SolvedAt | Tests/SmartTestSuite.cs | After solve → _lastPaths[].SolvedAtMono>0 + GetLastPathFresh(maxAge=1000) non-null | L-042 |
| TeamLifecycle_EmptyTeam_EvictedAfterGracePeriod | Tests/SmartTestSuite.cs | grace=0 + cycle=200ms + 800ms sleep → team evicted + log + counters (created=1,evicted=1) | L-044 |
| TeamLifecycle_RespawnWithinGrace_AbortsDrain | Tests/SmartTestSuite.cs | grace=10s + re-register within 100ms + 12s wait → team retained, no eviction log | L-044 |
| TeamLifecycle_MigrateTech_AtomicallyExactlyOneTeamHoldsIt | Tests/SmartTestSuite.cs | 50 migrations + 4 observer threads × 2s × 1kHz → zero {T,T} or {F,F} snapshots | L-043 |
| TeamLifecycle_DaemonRespectsDrainingState | Tests/SmartTestSuite.cs | Force Draining + StepOnce+PlanOnce → PublishTick did not advance + skip log | L-046 |
| TeamLifecycle_LeakInvariant_CreatedMinusEvictedEqualsLive | Tests/SmartTestSuite.cs | (Created'-Created)-(Evicted'-Evicted) == (Count'-Count) at quiescence | L-019 + L-044 |
| TeamLifecycle_DaemonIteration_NoDeadlockOnReaper | Tests/SmartTestSuite.cs | 8 teams + 8 evictions + 5s parallel daemon/reaper → both terminate <100ms post-cancel | L-045 + L-044 |
| TeamLifecycle_TeamIdReuse_FreshState | Tests/SmartTestSuite.cs | After eviction + GetOrCreateTeam(42) → new team.PublishTick==0 + Generation==prior+1 | L-063 |
| WorkerHealthMonitor_NeverFires_WhenAllAlive | Tests/SmartTestSuite.cs | After Init + 500ms + 10 Ticks → FireCount==0 | L-047 |
| WorkerHealthMonitor_DropEdge_FiresOnceThenDedups | Tests/SmartTestSuite.cs | Deregister fake → exactly one `[SMART-WORKERS-DEAD]` + one respawn; deregister DIFFERENT fake → second drop-edge fires once | L-047 |
| WorkerHealthMonitor_RespawnIdempotent | Tests/SmartTestSuite.cs | Respawn('AetherFuser') twice rapid → factory invoked once; after register → 'already alive' | L-047 |
| WorkerLifecycleRegistry_SnapshotLive_ReturnsAll | Tests/SmartTestSuite.cs | Post-Init Length == pool.WorkerCount + 9 (canonical 9 daemons); names match regex `^Smart#\d+$` (pool workers) or `^SmartLR-` (long-running) — pool WorkerHandle.Name does NOT carry `SmartWorker-` prefix (that's only on Thread.Name) | L-020 |
| WorkerPool_ReplaceDeadWorkers_RespectsAliveWorkers | Tests/SmartTestSuite.cs | 4-worker pool + abort _workers[1] → ReplaceDeadWorkers returns 1; [1] new ref; [0,2,3] same refs | L-048 |
| Watchdog_LogLineFormat_Stable | Tests/SmartTestSuite.cs | Drop-edge missing {A,B} → log matches regex `\[SMART-WORKERS-DEAD\] expected=\d+ live=\d+ missing=A\(age=\d+s\),B\(age=\d+s\) — auto-respawn fired` | L-047 |

---

## 6. Rollout plan (single commit, ordered for never-red build)

All items land in **one commit**. Implementation order matters so each compile point is green.
Group by wave; within wave, batch by file to minimise context switches.

**Pre-flight:** create the 21 new files empty with `namespace TAC_AI.AI.Forms.Smart…`
declarations to satisfy csproj registration. The csproj is old-style so every new .cs must be
registered (per build-setup memory). Full list: `Smart/SmartLifecycleShim.cs`,
`Smart/Threading/AbortGuard.cs`, `Smart/Threading/DaemonWatchdog.cs`,
`Smart/Threading/WorkerHealthMonitor.cs`, `Smart/World/TechLifecycleRegistry.cs`,
`Smart/World/TechLeakWatchdog.cs`, `Smart/Coordination/TeamReaperDaemon.cs`,
`Smart/Coordination/HostAuthorityCoordinator.cs`, `Smart/Coordination/TrainerBarrier.cs`,
`Smart/Pathing/TerrainPublication.cs`, `Smart/Pathing/IWorldResettable.cs`,
`Smart/Pathing/WorldResetRegistry.cs`, `Smart/Learning/ProfileSelfTest.cs`,
`Smart/Learning/AutosaveWorker.cs`, `Smart/Learning/QuitSaveCoordinator.cs`,
`Smart/Learning/Migrations/SmartMigrationAttribute.cs`,
`Smart/Learning/Migrations/0003_TlvSectionBody.cs`, `Smart/Tests/AbortSurvivalTests.cs`,
`Smart/Tests/PathingResetTests.cs`, `Smart/Tests/RoutingCompletenessCatcher.cs`,
`AI/Forms/AIFormRegistry.Debug.cs`.

**Wave 0 — Foundations (no internal deps, mostly new files + struct/enum additions).** Order
within wave is free, but file-batch:
1. `Smart/Threading/`: L-001 (AbortGuard new), L-002 (Registry IsTearingDown), L-003
   (ThreadingDiagnostics enum), L-020 (Registry SnapshotLive), L-021 (WorkerPool return name).
2. `Smart/SmartRuntime.cs` field additions: L-004 (IsPaused + RequestShutdown), L-019
   (TeamRuntime FSM + lock).
3. `Smart/../TankAIHelper.cs` field additions: L-008 (RoutingDecision struct).
4. `Smart/Learning/` & `Smart/World/`: L-013 (FlushPendingForPersist), L-014 (Migration
   registry + attribute + 0001/0002 attribute tagging), L-015 (two-deep backup ring).
5. `Smart/World/EventBus.cs`: L-012 (HostChanged event).
6. `Smart/Pathing/`: L-016 (IWorldResettable + WorldResetRegistry new), L-017
   (EnqueueMonoTimestamp on PathRequest).
7. `Smart/Threading/PathRequestBackpressure.cs`: L-018 (promote to singleton).
8. `Smart/Integration/SmartEventBridge.cs`: L-010 (DetachPerTank(TechId) + shadow map).
9. `Smart/SmartRuntime.cs` orphan-sweep: L-011 (TeamId fallback).
10. `Smart/World/TechLifecycleRegistry.cs`: L-009 (new file).
11. `Smart/../AIFormRegistry.cs`: L-006 (Clear() calls DeInitGlobal).

**Wave 1 — Consumers of Wave 0 surfaces.** Compile-time these are dependent on Wave 0
existing. Within wave, file-batch:
1. `Smart/Threading/WorkerPool.cs`: L-005 (asserts), L-022 (WorkerLoop), L-023 (EnqueueLongRunning
   supervisor), L-048 (ReplaceDeadWorkers).
2. `Smart/Threading/WorkerHealthMonitor.cs`: L-047 (new file).
3. Daemon RunLoops batch: L-024 (×7 sites) and L-026 (×9 sites — same files mostly).
4. `Smart/SmartLifecycleShim.cs`: L-025 (new file), then `Smart/SmartForm.cs`: L-007
   (Install/Uninstall calls).
5. `Smart/Coordination/HostAuthorityCoordinator.cs`: L-030 (new file).
6. `Smart/Coordination/TrainerBarrier.cs`: L-031 (new file).
7. `Smart/../AIFormRegistry.cs`: L-027 (RouteTech funnel + LiveRoutings).
8. Sidecar wires: L-028 (6 sidecar files), L-029 (Coordinator + PathingService).
9. `Smart/Pathing/TerrainPublication.cs`: L-033 (new file) — then PathingService
   atomic swap edits.
10. `Smart/Pathing/PathingService.cs` + PathRequestBackpressure: L-034 (lift to static + reset
    hook).
11. `Smart/Pathing/TerrainMap.cs` + `TerrainMapSnapshot.cs`: L-035 (IsFreshlyAllocated).
12. `Smart/World/EventBus.cs` + `Smart/SmartForm.cs`: L-036 (WorldResetting events).
13. PathRequestBackpressure hysteresis + Pathing GUI + console: L-037, L-038, L-039, L-040,
    L-041 batched by file.
14. `Smart/Pathing/TrajectoryOptimizer.cs` + PathingService: L-042
    (GetLastPathFresh + SolvedAtMono).
15. `Smart/Learning/SmartForm.cs` save log: L-032 (PROFILE-MANUAL-SAVE).
16. `Smart/SmartRuntime.cs`: L-043 (MigrateTech), L-044 (TeamReaperDaemon enqueue).
17. `Smart/Coordination/TeamReaperDaemon.cs`: L-044 body (new file).
18. Daemon iteration: L-045 (×3 daemons).
19. Coordinator/Planner hard-stop: L-046.

**Wave 2 — Build on Wave 1 wiring.** Order:
1. `Smart/Threading/DaemonWatchdog.cs`: L-049 (new file) + SmartRuntime.Init registration.
2. `Smart/../TankAIHelper.cs`: L-050, L-051, L-052 (DelayedSubscribe split + reclaim +
   reset).
3. `Smart/../AIFormRegistry.cs`: L-053 (DrawRoutingDebugGUI partial file
   `AIFormRegistry.Debug.cs`), L-054 (SetActive drain).
4. `Smart/SmartRuntime.cs`: L-055 (Deregister refactor) + WorldModel.DeregisterTech cleanup
   (L-083, formerly AC-7). L-056 (TechLeakWatchdog enqueue).
5. `Smart/World/TechLeakWatchdog.cs`: L-056 body (new file).
6. `Smart/SmartForm.cs:382`: L-057 (Operations.Notify).
7. `Smart/Learning/OnlineTrainer.cs`: L-058 (TrainerWorker FSM), L-059 (producer gate sites:
   batch across LearningService, LeadResidualRecorder, PlanTransitionPublisher).
8. `Smart/Learning/AutosaveWorker.cs`: L-060 body + LearningService Init/Shutdown
   registration.
9. `Smart/Pathing/PathingService.cs` + SmartEventBridge + SmartRuntime.Deregister: L-061
   (ForgetTeam call sites).
10. `Smart/Planning/GlobalPlannerDaemon.cs`: L-062 (Priority=64 probe).
11. `Smart/SmartRuntime.cs` (TeamRuntime ctor) + GetOrCreateTeam: L-063 (fresh-state
    Generation).
12. `Smart/World/AetherTuning.cs` + Tooling: L-064.
13. `Smart/SmartRuntime.cs` counters + `Smart/SmartForm.cs` GUI: L-065.
14. `Smart/SmartRuntime.cs` Init + `Smart/Learning/LearningService.cs`: L-066 (register
    daemons with WorkerHealthMonitor).
15. `Smart/SmartRuntime.cs` Shutdown: L-067 (BeginShutdown).

**Wave 3 — Final consumers + tests.** Order:
1. `Smart/Tests/PathingResetTests.cs`: L-068 (new file) + SmartTestSuite registration.
2. `Smart/SmartForm.cs:550-573`: L-069 (OnWorldReset collapses).
3. `Smart/Learning/LearningService.cs`: L-070 (OnHostLost / OnHostGained).
4. `Smart/Learning/OnlineTrainer.cs`: L-071 (TTL discard).
5. `Smart/SmartForm.cs:386`: L-072 (Operations Tick).
6. `Smart/SmartForm.cs:583-615`: L-073 (Workers GUI line).
7. `Smart/Tooling/SmartConsoleCommands.cs`: L-074, L-075, L-076 (three commands).
8. `Smart/Tests/AbortSurvivalTests.cs`: L-077 (new file) + SmartTestSuite registration.
9. `Smart/Learning/QuitSaveCoordinator.cs`: L-078 (new file) + SmartForm subscription +
   WorkerLifecycleRegistry signature change.
10. `Smart/Learning/Migrations/0003_TlvSectionBody.cs`: L-079 (new file) + ProfilePersistence
    schema 3.
11. `Smart/Learning/ProfileSelfTest.cs`: L-080 (new file) + LearningService.Init call.
12. `Smart/Tests/SmartTestSuite.cs`: L-084 (formerly AC-9) + all per-subsystem test method
    bodies + Wave-3 routing/runtime tests.

**Csproj registration:** every new .cs file (count: 21, see Pre-flight list above) must be
added to the .csproj `<Compile Include="..." />` block. Do this LAST so the build is green
only when every file has content (avoid empty-file compile errors mid-implementation).

**Comment style:** terse author-voice, NOT generated patch-notation (per memory).

---

## 7. Acceptance criteria

The overhaul is shipped when ALL of the following are visible from spawn-test:

**Process lifecycle (Section 3.1 + 3.2 + 3.6):**
- After alt-tab → return, ALL of these survive: AetherFuser, GlobalPlanner, GlobalCoordinator,
  ThreatField, PathSolve, 4 Trainers. Verified via `smart.workers.list` showing 9 daemons +
  expected pool worker count, all `IsAlive=true`.
- `output_log.txt` contains zero `WorkerTerminated reason=UnexpectedExit` lines after a
  pause/resume cycle. `[WORKER-ABORT-ABSORBED]` lines MAY appear (informative).
- Game-quit (graceful or force-kill) reaches one of `Application.quitting` /
  `Singleton.ApplicationQuitEvent` / `AppDomain.ProcessExit` / `AppDomain.UnhandledException`
  paths; `output_log.txt` contains exactly one `[PROFILE-QUIT-SAVE]` line with `saved=1`,
  `elapsed=<200ms`, `flush=4/4`.
- Profile autosave fires every 30s when dirty; `output_log.txt` contains periodic
  `[PROFILE-AUTOSAVE] period=30000ms saved=1 skipped=0` lines.
- `Threads_AllBackground_AfterInit` passes in SmartTestSuite.

**Per-tech routing (Section 3.3):**
- Every spawned attract tech (12+) has a corresponding `[TECH-ROUTE]` line. Form count
  matches `AIFormRegistry.LiveRoutings` rendered in F3 overlay (or smart-runtime debug GUI).
- Zero `[ROUTING-ORPHAN]` lines after 5 minutes of gameplay.
- Switching active form via in-game menu emits N×`[TECH-ROUTE] reason=FormSwapped` lines for
  N live techs.

**Orphan cleanup (Section 3.4):**
- After 30 mins of combat (techs spawning, dying, switching teams), `output_log.txt` contains
  zero `[TECH-LEAK]` lines from TechLeakWatchdog.
- After a save-reload cycle, every registered sidecar's `SnapshotKeys` count drops to match
  the new mission's live tech count (verified in-game via SmartTrainingDriver debug UI).

**Trainer host transitions (Section 3.5):**
- MP host migration produces exactly 8 `[TRAINER-HOSTCHANGE]` lines (4 trainers × 2
  transitions); one `[LEARNING-HOSTCHANGE]` per transition; no `[HOSTAUTH-PAUSE-TIMEOUT]`.
- After a host→client transition, no `[TRAINER-DROPPED-NONHOST]` events for IsHost==true
  producers; coalesced 1/min entries on client side reflect actual non-host gameplay.

**World reset (Section 3.7):**
- After save load, `output_log.txt` contains a `[WORLD-RESET]` block: 1 starting + 6 per-hook
  + 1 completed (or 'partial'). No `[WORLD-RESET-PARTIAL]` in normal flow.
- After a team conversion event, `output_log.txt` contains `[PATHING-FORGET-TEAM] teamId=N`
  and `_threatFields.Count` decreases.

**Path backpressure (Section 3.8):**
- After 60s of gameplay, `output_log.txt` contains at least one `[PATHING-DEPTH] window=60s
  b0-25=A b25-50=B b50-75=C b75-100=D shedTransitions=K expired=E droppedLowPri=F p50=Yms
  p99=Zms`.
- GlobalPlannerDaemon issues at least one Priority=64 probe per team per cycle (visible in
  the histogram b0-25 bucket non-zero).
- `smart.runtime.status` shows `Pathing.Queue: D/Cap   Shed: ON|OFF   Drop=N Exp=M` and
  `Pathing.Latency: p50=Yms p99=Zms`.

**Team lifecycle (Section 3.9):**
- After a 30-minute session with multiple team flips, `output_log.txt` contains balanced
  `[TEAM-LIFECYCLE] team=N state=Active created` and `[TEAM-LIFECYCLE] team=N state=Disposed
  evicted` lines (Created - Evicted == live _teams.Count).
- DrawPathingDebugGUI shows `Teams: N (created=X evicted=Y migrations=Z)` with non-zero
  evicted+migrations after sustained play.
- Shutdown emits `Smart.Runtime: lifecycle summary teams_created=X evicted=Y migrations=Z
  final_count=N` with `Created-Evicted == final_count`.

**Worker watchdog (Section 3.10):**
- `smart.workers.list` shows live=expected after Init (all 22 pool workers + 9 daemons).
- If a daemon is force-killed (test injection), within 1s `output_log.txt` contains
  `[SMART-WORKERS-DEAD] expected=N live=N-1 missing=<name>(age=Ts) — auto-respawn fired`
  followed by `[SMART-WORKER-RESPAWN] name=<name> factory=ok`.
- After respawn, `smart.workers.list` shows live=expected again.
- `smart.runtime.status` shows PathBackpressure dropped, AetherFuser spikes, CB trips and
  `Watchdog: live=M/N respawnAttempts=K respawnSuccess=L respawnFailed=F` lines.

**Profile durability (Section 3.6):**
- `Load_FallsThroughAllTiers` passes; corrupt primary+.previous loads .penultimate with
  `[PROFILE-LOAD-FAIL] tier=penultimate`.
- `MigrationRegistry_CoversAllVersions` passes; missing-migration would have failed at static
  init.

**Smoke test (run after spawn-test):**
- SmartTestSuite full run reports zero failures across all 60 test entries (Section 5 matrix).
- `output_log.txt` from a 60-minute mixed-mode session (pause cycles + form switches + save
  reload + MP host swap if possible) shows ≤10 `[*-PARTIAL]` or `[*-FAIL]` lines combined
  across all subsystems — anything more indicates a real regression.

---

## 8. Honesty section — out-of-scope items with named blockers

This overhaul has **no v0.3 deferments**. Items below are either out-of-scope by intent
(orthogonal to lifecycle) or genuinely blocked by a verified-missing engine API. No phase
numbers.

| Item | Why out-of-scope | Named blocker |
|---|---|---|
| MP-replicated `HostChanged` event across all clients | This plan handles edge-detection on each local instance independently. Cross-network host-authority synchronisation requires a Steamworks lobby-event surface. | Verified missing: no public `ManNetwork` event for "lobby host changed" that fires on non-host clients. Adding would require `NetworkMessage<HostChanged>` protocol design + a `Steamworks.NET` upgrade investigation that is outside Smart's scope. |
| Per-mission training-profile isolation (separate weights for campaign vs Adventure vs Co-op) | Today profile is keyed only by `playerId`; mission-mode separation would benefit Adventure-mode boss-fight specialisation. | Verified missing: no documented engine surface for "current mission archetype" that survives loading screens. `KickStart` exposes some flags but not a stable archetype enum. Would require `IsMissionArchetype` Harmony patch on `ManSaveGame`. |
| `ManTechs.IterateTechs` thread-safe accessor | `TechLeakWatchdog` (L-056) currently posts a main-thread one-shot via WorldEventBus + 100ms wait + stale fallback. A direct thread-safe enumerator would be cleaner. | Engine-side: `ManTechs._tankList` is a `List<Tank>` mutated from main-thread `OnTankAddition`/`OnTankDestroyedEvent`. Making it thread-safe requires either a snapshot every frame (allocation) or a CoW pattern in `ManTechs` itself — outside Smart. |
| `Application.quitting` ordering vs `OnApplicationPause(true)` cascade | When alt-tab triggers pause AND user quits before resuming, the two events can interleave. L-078's `HasFired` latch makes this safe but the *ordering* is not guaranteed. | Unity-side: Mono's threading semantics on Unity 2018.4 do not document a strict ordering between `OnApplicationPause` and the various quit hooks. The defensive `Interlocked.CompareExchange` latch is the canonical response. |
| Bounded HoldingBuffer size in TrainerWorker FSM (L-058) under truly pathological producer storms | L-058 caps at 2× MinibatchSize. A misbehaving producer (e.g., loop pumping `LearningService.OnDamageObserved`) could fill it within one tick. | Today's producers (DamageObserved, 1Hz drain, LeadResidual, PlanTransition) are all bounded per-frame. Hardening would mean per-producer rate limits — a Smart-internal v0.4 concern. |
| `WorldResetting`/`WorldResetCompleted` event-bus replay for late subscribers | A subscriber that initialises AFTER the event fires never sees it; misses the current-mission tick. | Engine-side: WorldEventBus is fire-once. Adding "replay last event of type T" surface is a v0.4 task; today late subscribers must poll PathingService.Backpressure.LastShedTransitionMono or equivalent. |
| `TeamReaperDaemon` race against engine `ManNetwork.HostMigrationEvent` (MP-only) | During MP host migration, teams may flip TechCount=0 transiently as the engine re-attributes techs. The 30s grace covers most cases but a >30s migration could evict a real team. | Verified missing: `ManNetwork` does not expose a "host migration in progress" surface. Workaround: operator can raise `AetherTuning.TeamEvictionGraceSeconds` to 120 for known-flaky MP sessions. |
| `[WORKER-ABORT-STORM]` recovery from a daemon's CircuitBreaker permanently trip | If both AbortGuard storm-trip AND CircuitBreaker trip on the same daemon, DaemonWatchdog respawns the daemon but the new instance inherits CircuitBreaker trip state and immediately bails. | By-design tradeoff — see Section 3.1 risks. The respawn-attempts cap (L-049: 5 per session) prevents thrash. Operator must use `smart.workers.respawn <name>` to force a fresh CircuitBreaker for that daemon, which is an ESSENTIAL but manual recovery surface. |

These are the only known blockers. Every other item from every report is in-scope and lands in
the single commit described in Section 6.

---

## What success looks like

When this overhaul ships, an operator can alt-tab away from a Smart-active session for 10
minutes, return, switch forms twice, save and reload, watch one team get converted, force-kill
a trainer with `smart.workers.respawn`, and exit the game — and observe, in
`output_log.txt`, a complete chain of structured `[WORKER-ABORT-ABSORBED]`,
`[WORKER-ABORT-STORM]`, `[DAEMON-RESPAWN]`, `[TECH-ROUTE]`, `[TEAM-LIFECYCLE]`,
`[WORLD-RESET]`, `[PATHING-DEPTH]`, `[TRAINER-HOSTCHANGE]`, `[LEARNING-HOSTCHANGE]`,
`[PROFILE-AUTOSAVE]`, `[SMART-WORKER-RESPAWN]`, and `[PROFILE-QUIT-SAVE]` lines telling
exactly what happened and when — with zero `WorkerTerminated reason=UnexpectedExit`, zero
`[ROUTING-ORPHAN]`, zero `[TECH-LEAK]`, zero `[WORLD-RESET-PARTIAL]`, zero
`[HOSTAUTH-PAUSE-TIMEOUT]`, zero `[PROFILE-LOAD-FAIL] tier=glorot`, and a profile file on
disk that is byte-identical to what a clean Shutdown would have written. The Smart form
becomes the first AI subsystem in TerraTech whose lifecycle is fully self-describing and
self-recovering, where every silent-failure mode the prior audits identified is now either
impossible (locked by invariant) or loudly observable (logged + reflected in the in-game
debug overlay + queryable via console).

---

## 9. Verification pass appendix

After the 10 subsystem reviewers produced the original plan, 3 verifiers cross-checked the
findings against the live source tree (~30 source files plus the Decompiled engine, with
spot-checks on file:line accuracy, API surface feasibility, and dependency-graph completeness).
This appendix records the audit's outcomes.

**Summary statistics.**

| Metric | Count |
|---|---|
| Reviewer findings consolidated | 73 |
| CONFIRMED by ≥2 verifiers (applied) | 65 |
| BLOCKER (auto-applied) | 1 (L-076 ValueTuple) |
| REFUTED by verifiers | 1 |
| NEEDS-MORE-EVIDENCE (logged, runtime verification required) | 5 |
| Missed-by-reviewer items surfaced (applied) | 8 |
| New global IDs assigned (L-081..L-085) | 5 (promoted from previously unnumbered TR-7, TR-9, AC-7, AC-9, PRB-3) |
| Plan LOC pre-audit | 4,593 (per original summary; actual per-table sum was lower due to mis-aggregation) |
| Plan LOC post-audit (per-table sum) | 4,677 |

**REFUTED findings.**

| Reviewer | Finding | Verifier verdict |
|---|---|---|
| r10-worker-watchdog (on L-048) | "Walks `_workers[i]` for `Stopped\|Aborted` may misfire during Dispose; `IsAlive==false` cleaner" | Verifier 1 REFUTED. WorkerPool.cs Dispose sets `_disposed=true` BEFORE joining workers, and `ReplaceDeadWorkers` guards `if (_disposed) return 0`. The pause-cascade absorption keeps the thread alive (TAE handler calls ResetAbort+continue, so thread is NOT in Stopped state during legitimate absorption). The `Stopped\|Aborted` bitwise check is defensible; reviewer's IsAlive recommendation is a style preference, not a bug. Still applied as a clarification in L-048's invariant text because IsAlive is canonical .NET semantics and easier to reason about. |

**NEEDS-MORE-EVIDENCE findings (runtime verification required, not applied to the plan).**

| Reviewer | Finding | Why deferred |
|---|---|---|
| r1-thread-abort | "Mono 2018.4 does not raise UnhandledException for TAE in background threads" | External repro, not source-verifiable. WorkerPool.cs:182-204 comment block independently asserts the same behavior; treated as established. |
| r1-thread-abort | "FMOD's OnApplicationPauseManual True triggers Thread.Abort on every worker" | Behavioral claim observed in output_log.txt; FMOD source not available in decompile. WorkerPool.cs comments already document this as observed. |
| r5-trainer-host | "TestProducerGateCoverage uses Roslyn-style scan" | Plan says "Roslyn-style" but project may not have Roslyn dependency; could be regex/grep. Implementer decides at L-059 landing time. |
| r5-trainer-host | "Holding buffer 2×MinibatchSize semantic (drained EventQueue items vs trained-but-unpublished params)" | Design-ambiguous between two viable interpretations; both are implementable. Resolved by hardcoding 64-slot buffer; semantics chosen at L-058 landing. |
| r7-terrain-reset | "L-068 1000×64-thread stress test may exceed CI budget" | TestRunner.Run is synchronous; 64k operations inside Action body adds latency. Cap at landing time based on observed RunAll budget. |

**Missed-by-reviewer items surfaced (and applied).**

Verifiers caught 8 issues that reviewers did not flag. Major ones:

- **L-054 enumeration source**: SetActive must call `AIECore.IterateAllHelpers()` (the only existing live-helper enumeration pattern, at SmartForm.cs:104) before stamping the new ActiveId. Added to L-054 invariant.
- **L-019 RegisterTech state-check ordering**: Bool refusal must precede `state.OwnerTeam = this` mutation at SmartRuntime.cs:350 to avoid leaving OwnerTeam set on a state without team membership. Added to L-019 invariant.
- **L-019 RWLockSlim disposal**: ReaderWriterLockSlim implements IDisposable; TeamRuntime gains `Dispose()` called from L-044 Disposed transition to release kernel handle. Added to L-019 invariant.
- **L-029 Coordinator lock scope**: The Hungarian read at Coordinator.cs:146 receives `_previousAssignments` by ref and iterates during compute — so the new `_assignmentsLock` must wrap the entire Hungarian-read+reassign stage, not just the ForgetTech mutation. LOC bumped 60→85.
- **L-044 ForgetTeam coupling**: Reaper Disposed transition must call `PathingService.ForgetTeam(teamId)` BEFORE TryRemove to clear the per-team threat-field DoubleBuffer (otherwise leaked indefinitely). Added L-061 to L-044 deps; LOC 85→90.
- **L-049 canonical roster strings**: DaemonWatchdog must use the literal `EnqueueLongRunning` label strings ("AetherFuser", "GlobalPlanner", "GlobalCoordinator", "ThreatFieldRebuild", "PathSolve", "Trainer-Intent", "Trainer-ActionValue", "Trainer-Residual", "Trainer-Threat") — not paraphrased names. Added to L-049 invariant.
- **L-058 finally block + cleanup ordering**: TrainerWorker.RunLoop currently has no finally; must be added with explicit ordering — Unsubscribe in finally AND barrier Deregister in finally BEFORE registry Deregister. Added to L-058 invariant; LOC 90→95.
- **L-066 factory contract clarification**: Factory delegate must capture the static field by closure and read on respawn (must be non-null OR re-throw to skip — L-067 BeginShutdown gates the shutdown-window null). Added to L-066 invariant.

**New global IDs assigned (L-081..L-085).**

| Old ref | New ID | Subsystem | Title | Wave | Why promoted |
|---|---|---|---|---|---|
| TR-7 | L-081 | per-tech-routing | `RoutingCompletenessCatcher.MainThreadTick` | Wave 2 | Wave-topological tooling needs a global ID to sequence; cross-sub deps reference it. |
| TR-9 | L-082 | per-tech-routing | Remove `[TEMP DIAGNOSTIC]` block | Wave 2 | Same. |
| AC-7 | L-083 | orphan-cleanup | Delete duplicate fan-out in `WorldModel.DeregisterTech` | Wave 2 | Cannot land independently of L-055 — needed an explicit ID so the dep graph captures the chunk-pairing. |
| AC-9 | L-084 | orphan-cleanup | `ASSERT_TECH_DEREGISTERED` tests | Wave 4 | Standalone test item like L-068/L-077 — deserves matching ID treatment. |
| PRB-3 | L-085 | path-backpressure | Dequeue-side expiry + `ExpiredCount` | Wave 1 | Wave-1 production item without ID; L-017 rollback notes referenced `L-??`. |

**Post-audit plan integrity.**

The audit confirmed that the plan's overall architecture — `TechLifecycleRegistry`,
`WorkerHealthMonitor`, `TeamRuntime` FSM, `HostAuthorityCoordinator` FSM, `TerrainPublication`
holder, `SmartLifecycleShim` MonoBehaviour, `AbortGuard` + `DaemonWatchdog` — is sound and
addresses the actual root causes documented in the source code's existing comments. Every
cited file exists; every named engine API (`Singleton.ApplicationQuitEvent`,
`EventNoParams.Subscribe`, `ManTechs.IterateTechs`, `ManPauseGame.OnApplicationPauseManual`,
`AppDomain.ProcessExit`, `AppDomain.UnhandledException`, `KickStart.SaveSink`,
`ReaderWriterLockSlim`, `Interlocked.CompareExchange`) is verified present in the decompile
and in-tree source. The 65 applied corrections fall into five clusters: (a) file:line drift
(off-by-one citations and one fabricated `TerrainMapSnapshot.cs`), (b) API-surface mismatches
(L-007 cited #endif lines inverting Install/Uninstall semantics; L-076 ValueTuple
unavailable; L-005/L-020 accessor name collision; L-064 wrong tunable-registration site),
(c) caller-update gaps (L-019 bool-return RegisterTech breaks 2 silent callers; L-046
StepOnce signature change breaks 2 RunLoop callers), (d) hot-path lock-coverage gaps (L-029
Hungarian compute), and (e) cross-subsystem dep gaps (L-049/L-044/L-056/L-076 missing waves).
With all corrections applied, single-commit ship risk is reduced from "5-10 silent
integration failures from off-by-one cites + name collisions + missing deps" to "implementer
ergonomic friction only" — the plan is now ready for landing. Estimated implementation
rework saved by this pass: ~2 hours of debugging during single-commit integration.
