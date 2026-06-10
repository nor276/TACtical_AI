# TRAINING-DIRECTOR-PLAN

> Status: design v1 + BehaviorGuards fold-in (post 3-reviewer + 5-agent guard pass + 3-validator guard verifier pass) + **12-reviewer-3-verifier post-design verification pass** + **19-agent decompile-deep-dive (3 rounds) + 6-agent adversarial pass: 3 disproven, 5 weakened, 13 survived, all 21 amendments applied** + **6 independent plan-only reviewer amendments A1-A6 applied** + **OverdueDelivery telemetry-only reconciliation pass: 5 sections (§9.3 rewrite, §13 design bullet, §15 revert, §14.4.1 R3-M4 + A4 disposition revisions) aligned to V-fix BLOCKER direction; substitution machinery retained for hard-corrects only (AntiCrash + GroundedAircraft)** + **post-feature-expansion (commit e9bfcd4 + producer-closeout) + post-78-bug audit (workflows wsgt4cbj7 + wtxvtdhcr) cross-section reconciliation pass FINALIZED — stage-1 (10-agent) changes applied; stage-2 (5-agent) corrections applied; independent self-validation pass APPLIED (see §14.4.2)**: precondition surfaces now SHIPPED — StrategicStateVector + StrategicStateExtractor daemon + SelfStateProbe + StrategicStateBuffer + ActionValue producer at `ContinuousController.cs:310/444/514` + all-4-models ArchitectureVersion 2/1/1/1 → 3 + DamageHintBuffer 7th DamageType byte field + HealthSidecar HP-history ring + CargoStatePublisher + NearestTechCache + WeaponFireBuffer + EnemyVehicleSnapshots (BUG-026). 78-bug audit RESOLVED M1 (BUG-004: PathingService.cs:170-173 RegisterCanonical), B3 (BUG-022: ProfilePersistence.cs:118-126 SaveMutex wrap), BUG-024 ArchitectureVersion Load guard, BUG-009 FillResidualSlots, BUG-010 SelfStateProbe LastAttacker latch, BUG-011 Q-learning max_a', BUG-012 HostChanged Unsubscribe pairing, BUG-013/050/051 TerrainPublication atomic-swap + one-time worldReset latch, BUG-046 AdamState TLV tag 0x0002, BUG-052 WeaponFireBuffer DetachAll, BUG-053 SmartEventBridge.Uninstall three-surface teardown, BUG-080 WorkerLifecycleRegistry CancelAllAndJoin, BUG-017/018/019/021/055 MP host-gating discipline, BUG-007 GUIInteraction catch-and-log, BUG-008 TerrainMap water gating, BUG-015 ModifiedForm Aviator/Buccaneer/Astrotech dispatch, BUG-028 IMovementAICore.AvoidAssist removed, BUG-035 TrajectoryOptimizer Volatile, BUG-041 PUCTSearch learned-prior wiring, BUG-006 pretraining surface trimmed (no Director consumption); CanonicalRoster now 13 entries (post-BUG-025/036), not 9. A1 rollback EventQueue KEEP semantics (§7.3 step 4.5; ActionValue contamination NOW bounded by per-envelope cap 2, no longer "queue empty"), A2 eval-battery + checkpoint ratchet as fast-follow precondition for GRU BPTT (new §7.5), A3 synthetic-pathology guard selftests (§9.6 + `smart.director.guards.selftest`, test-only LOC), A4 REVERTED — §15 reverted to telemetry-only matching §9.7 step 5 BLOCKER direction (initial A4 substitution rewrite was based on a misread of §9.7), A5 stale "12 behavior monitors" → 11 in §15, A6 live-build bugs M1 / B3 now SHIPPED in live patch (audit BUG-004 / BUG-022); §14.1 backlog cleared. Total LOC estimate: **~11 690** (was ~11 730; ~40 LOC drop — M1 ~5, B3 ~5, ArchitectureVersion guard ~5, FillResidualSlots wiring, ActionValue producer wiring now in baseline; many "NEW" surfaces are now incremental over the post-feature-expansion baseline, not over the prior plan revision).
> New files: **52** (31 base + 19 guard layer + NEW `PathingService.cs` (~80 LOC, replaces fictional `AIEPathQuery`) + NEW `ProjectileFiredPublisher.cs` (~80 LOC, replaces fictional `FireControl.ProjectilesFiredCount`)). Modified files: **19 distinct paths** (17 base + 1 newly explicit `TankAIHelper` + 1 newly explicit `ContinuousController`; guard-extension rows on §12.1 publishers are folded into §12.1 totals, not double-counted).
> Authoring constraint: C# 7.3 / .NET 4.6.1 (no LINQ-async, no nullable refs, no record types, **no `volatile long` — illegal CS0677, use `Interlocked.Read/Exchange`**).
> Build: old-style csproj — every new `.cs` must be registered explicitly.

---

## 1. Executive summary

The Training Director is a 3-layer supervisor that sits between an operator (or LLM)
and the 4 existing Smart-form trainers (`OpponentIntentClassifier`,
`ActionValueEstimator`, `TrajectoryResidualModel`, `ThreatAssessmentModel`).

**Scope**

  * Persistent operator directives in a small, hand-parsed grammar.
  * 1 Hz constraint engine over per-model telemetry reservoirs (loss-variance, entropy,
    param-delta-L2) and per-identity outcome rates.
  * A bounded vocabulary of **7 verbs** the Director may invoke against trainer/scenario state.
  * Semantic 10-minute digests with out-of-band triggers, file + IMGUI + JSON sinks.
  * Three rollback tiers (in-memory ring, ProfilePersistence `.previous`/`.penultimate`,
    Director-named checkpoints) with atomic capture under `model.SaveMutex` and
    `ArchitectureVersion` validation on restore.
  * Six training scenarios (S1..S5 + S6′) composed from verified TerraTech primitives
    — `BasePurpose`, `BaseTerrain`, `AIType`, SubNeutral team minting,
    `RawTechLoader.SpawnMobileTechPrefab`, `ManLooseBlocks.HostSpawnChunk`.
  * **Four** new `IdentityOutcome` publisher sources (`BlockDelivered`, `BaseHeld`+`BaseLost`,
    `AllyProtected`, **plus `BehaviorGuardWorker` as the fourth source emitting
    `GuardViolation_*` outcomes**) on top of the existing `KillScored` at
    `SmartEventBridge.cs:330` (post-audit drift; Generic-attacker filter at `:326`).
  * **BehaviorGuards layer** (§9): **11** per-tech pathology monitors (was 12; `OrbitalLockup`
    dropped per Validator-1 double-jeopardy with `OrbitNoFire`) emitting 3 new
    `OutcomeKind` values (`GuardViolation_Movement`, `GuardViolation_Role`,
    `GuardViolation_Combat`) into the same `IdentityOutcomeConsumer` plumbing,
    feeding 4 new bucket-rate constraints. Guards are training-signal-first:
    **5 observe**, **4 warn**, **1 soft-correct** (`OverdueDelivery`, the operator's
    "return to base" concern), **1 hard-correct** (`GroundedAircraft`, the sole
    catastrophic-safety override — 5 s wall-clock release ceiling + 2 s ramp-down
    `MaintainAltitude` handoff, no state lock). **Guards are observer-only at the
    reward channel** — `OutcomeWeights[GuardViolation_*] = 0.0` by default and the
    trainer reward path explicitly ignores `GuardViolation_*` `OutcomeKind`s. Guards
    feed constraints, not loss.

**The "BEST" framing**

  * Operator/LLM cadence: ~1 directive/hour; reads ~1 digest/10 min.
  * Director cadence: 1 Hz aggregation; 0.2 Hz constraint evaluation.
  * Bandwidth match: digests carry ≤ 10 numbers per model + one identity-rate line per
    populated cell. Plain text + JSON sibling produced from the same `DigestSnapshot`
    struct.
  * Semantic interface: directives in operator language, not hyperparameter values.
  * Declarative: operator says *what to maintain*; Director picks the verb that
    plausibly fixes it.
  * Reversibility: journaled interventions + 3-tier rollback. Operator policy
    (DirectiveTable) is *intentionally* not rolled back; reapplications are surfaced.

**Final scenario list**: S1 Open Brawl, S2 SubNeutral Resource Race,
S3 Defensive Hold, S4 Air Sortie, S5 Mining + Defend, **S6′ Patrol Stress** (replaces
S6 Convoy Escort — no movement-goal authoring API exists for mobile HQs; verified
absence of waypoint plumbing, not absence of `BasePurpose.NotStationary`).

---

## 2. Architecture (3 layers)

| Layer | Cadence | Inputs | Outputs | Implementation |
|---|---|---|---|---|
| **L1 Operator / LLM (Directive Surface)** | ~1 directive/hour; ~1 digest/10 min consumed | (a) `[DevCommand]` `smart.director.tell <line>` (Host + Cheat), (b) 5 Hz polling of `<UserDir>/SmartAI/Director/inbox/*.directive` (FileSystemWatcher with `LastWriteTime` poll fallback), (c) IMGUI text field inside `SmartForm.DrawPathingDebugGUI` Director panel. Read side: `digest.latest.txt` + `journal.jsonl` tail | Validated `Directive` structs into `DirectiveTable` (ConcurrentDictionary, 32-entry cap). Mirrored to `directives.json`. Parse errors emit `[DIRECTOR-REJECT]` lines surfaced in next digest with offending token + legal-subject list | `Smart/Director/DirectiveSurface.cs` owns parse-entry + dedup + expiry sweep. `Smart/Director/DirectiveInboxWorker.cs` long-running daemon (`WorkerPool.EnqueueLongRunning` + `DaemonWatchdog.RegisterCanonical("DirectorInbox", factory)` + `WorkerHealthMonitor.RegisterCanonical`). New `[DevCommand]`s live in a sibling class `SmartDirectorConsoleCommands.cs` (no partial-class fiction — `SmartConsoleCommands` is not partial today) |
| **L2 Training Director (Constraint Engine + Action Planner)** | 1 Hz aggregation; 0.2 Hz constraint evaluation; intervention on healthy→violated edges only with debounce; digest every 600 s + on triggers | 12 reservoirs from L3 (Loss, ParamDelta, Entropy × 4 models); per-identity outcome rates from extended `IdentityOutcomeConsumer.GetRatePerMin`; `SmartRuntime.LiveSmartTechCount` **(NEW — added in §12.2 SmartRuntime.cs row; not extant in current source)**; `DamageObserved` 1-min counter; `DirectiveTable`; `DirectorJournal` tail | Action invocations against the 7-verb vocabulary; journaled to `DirectorJournal` (append-only JSONL); `SemanticDigest` every 600 s + on trigger; `DirectiveAccepted`/`ConstraintViolated`/`InterventionApplied` events on `WorldEventBus` | `Smart/Director/TrainingDirector.cs`: single long-running daemon. Registered the same way as `TrainerWorker` (`LearningService.cs:117-119`). Host-gated by `SmartRuntime.IsHost && !SmartRuntime.IsPaused`. `ConstraintRegistry` is a static array of `Constraint` structs; `ConstraintEngine` is stateless. `ActionPicker` priority = (deviation_from_band / band_width) × dependsOnPublisher_weight. After remediation X for constraint Y, Y enters a 5-min observation state (re-evaluable only for ESCALATION). Director NEVER writes `_params`, `_adam.M/V/T`, or `LearningTuning` outside the 7 Action classes — enforced by code review + `smart.director.audit`. **AdamState encapsulation (V-fix)**: `GetAdamForDirector()` returns `AdamSnapshot` (NEW readonly struct: `{ float[] M; float[] V; long T; float LR; float BaseLR; }`) by-value, NOT the mutable `AdamState` ref-type. Mutation flows through dedicated methods `SetLearningRate(float)` and `LoadAdamState(AdamSnapshot)` so the 7-Action invariant is enforced by language, not just code review |
| **L3 Instrumentation + Scenario Substrate** | Trainer hot path 30-200 Hz (single-writer reservoir push). 1 Hz drain by L2. ScenarioWorker / ChunkRegenWorker 5 s tick. Publishers event-driven | `WorldEventBus` events (`DamageObserved`, `ProjectileFired`, `KillScored`, `PlanTransition`), `TrainStepResult` per minibatch, param deltas (sampled every 32 steps via existing `StoreParameters`/`LoadParameters`), `RawTechLoader` spawn outcomes, chunk-field census via `Physics.OverlapSphere` on the Pickup layer | 12 `TrainerStats` reservoirs feeding L2; per-identity outcome rates; scenario instance state (team-id sets, centroids, age, target counts); **four new outcome publishers** (BlockDelivered, BaseHeld+BaseLost paired, AllyProtected, BehaviorGuardWorker for GuardViolation_*) | `TrainerWorker.RunLoop` gains a 6-line post-`TrainOneMinibatch` push (LossBefore, LossAfter, BatchSize) into `LossReservoir[modelId]` + a param-L2 snapshot every 32 steps. `OpponentIntentClassifier.Forward` reads `LearningTuning.IntentTemperature` ONCE per batch (snapshot at top of Evaluate / TrainOneMinibatch_FullBptt) and pushes Shannon entropy into `EntropyReservoir[Intent]` **at TrainOneMinibatch_FullBptt where softmax probs at line 266 are already computed (single-writer trainer thread); Evaluate is the inference path BUT has zero consumer call sites in the current source (unwired)**. `ScenarioWorker` + `ChunkRegenWorker` + 3 publishers register as canonical daemons. **ScenarioWorker and ChunkRegenWorker are PLANNERS** — they compute requests on the background tick and enqueue `ScenarioSpawnRequest` / `ChunkSpawnRequest` envelopes onto the main-thread queue. The actual `RawTechLoader.SpawnMobileTechPrefab` / `ManLooseBlocks.HostSpawnChunk` call runs on main thread (drained inside `SmartForm.Operations` after `WorldEventBus.DrainMainThreadQueue` and before `LineOfSightProducer.MainThreadTick`). **Per-tick debounce (BLOCKER-fix)**: the new spawn/guard request executors are gated by `CompareExchange`-on-MonoClock interval guard (200 ms debounce via `_lastSpawnExecMs`) so per-tech-per-frame Operations invocation (SmartForm.cs:515-525 "called once per Smart-driven tech per physics frame" comment block — N times/frame at N live techs) collapses to one drain per interval. Wrapped in per-envelope try/catch (same isolation pattern as Phase 4 fix try/catch at SmartForm.cs:498-513). **Reward-scale normalization push site (BLOCKER-fix)**: `actionvalue_loss_variance_ratio` reward-magnitude normalization uses `ActionValueEstimator.EwmaAbsReward` getter (NEW per-minibatch EWMA tracked under SaveMutex inside `TrainOneMinibatch` where `batch[i].Reward` is in-scope); TrainerWorker.RunLoop reads the getter for the LossReservoir push |

**Rationale.** Three layers map to three timescales (operator-hour,
supervisor-second, trainer-30 Hz) and three vocabularies (semantic directives,
bounded verbs, raw telemetry). All **five** new daemons (`TrainingDirector`,
`DirectorInbox`, `ScenarioWorker`, `ChunkRegen`, `BehaviorGuardWorker`) register via
`DaemonWatchdog.RegisterCanonical` + `WorkerHealthMonitor.RegisterCanonical` — zero
new lifecycle code, free auto-respawn. **Critical**: each canonical daemon requires
BOTH a `RegisterCanonical` factory call AND a corresponding entry appended to the
hard-coded `DaemonWatchdog.CanonicalRoster` static `string[]` array (verified at
`DaemonWatchdog.cs:33-48` — **13 entries currently** post-audit BUG-025/036 fix:
AetherFuser + GlobalPlanner + GlobalCoordinator + ThreatFieldRebuild + PathSolve +
4 Trainers (Intent/ActionValue/Residual/Threat) + StrategicStateExtractor + TeamReaper +
Autosave + TechLeakWatchdog = 13; ScanAndRespawn iterates the array, NOT the
`_factories` dict). Without that array extension, registered
factories for daemon names not in `CanonicalRoster` are silently never scanned for
respawn. Reservoirs mirror the verified `PathRequestBackpressure._latencyReservoir`
pattern (**lock-based** ring + sort-on-read for p50/p99/var — `PathRequestBackpressure.cs:155-172`
uses `lock(_latencyLock)`, NOT lock-free single-writer; correction from operator brief;
reservoir field declared at `:61`, lock at `:64`).
Strict downstream-only flow (L1 → L2 → L3 → models) means rollback is well-defined: a
checkpoint replay of (a) model bytes + (b) `LearningTuning` snapshot reproduces trainer
behaviour. **Daemon init order (BLOCKER-fix)**: Director construction order is
(1) construct singletons (DirectorState, ConstraintRegistry, ScenarioRegistry);
(2) THEN call `pool.EnqueueLongRunning + RegisterCanonical` for the daemon. Each
daemon's RunLoop checks a static `volatile bool _daemonInitComplete` latch and
`WaitHandle.WaitOne(1000)` retry until set, mirroring the GlobalPlannerDaemon
defensive pattern. ScenarioWorker registers BEFORE GuardWorker so ScenarioWorker's
`ActiveScenario` data is available when GuardWorker reads `GuardContext.ActiveScenario`.

---

## 3. Directive grammar

**Verbs**: `maintain`, `prefer`, `force`, `freeze`, `lr_scale`, `replay_bank`,
`rollback`, `checkpoint`, `forbid`, `rescind`, `clear`, `digest`, `status`,
`calibrate`.

**Shape**: `<verb> <subject> <predicate> <value> [for <duration> | until <expr>] [tag <name>]`

### 3.1 Syntax examples

**Whitespace discipline (BLOCKER-fix)**: TerraTech's `[DevCommand]` framework
(`ManDevCommands.SplitIntoParts`) tokenises arguments on whitespace. A free-form
`Tell(string line)` invocation receives only the first token. Operator MUST quote
the entire directive: `smart.director.tell "maintain hunter.kills_per_min >= 0.5"`.
The command's helptext and parse-error response surface this requirement inline.
For common one-shot cases, plan also ships aliases with positional args:
`smart.director.tell.lr_scale <model> <factor>`, `smart.director.tell.freeze <model> <sec>`,
`smart.director.tell.rescind <id-or-tag>`.

```
maintain intent.entropy_ratio in [0.30, 0.85]
maintain hunter.kills_per_min >= 0.5
maintain gatherer.delivery_rate >= 0.5/min for 30min
prefer scenario S2 weight 0.5
force scenario S5 intensity 1.5 for 30m
freeze threat for 10min
lr_scale ActionValue 0.5 until rescinded
replay_bank Gatherer 256
rollback to previous
rollback memory threat
checkpoint my-baseline
forbid scenario S4 until ally_protection_rate > 0.05
rescind d-00012
rescind tag s2-baseline
clear directives
digest now
status
calibrate actionvalue_loss_variance_ratio for 10min
```

Note: `maintain hunter pressure` (paraphrase) is NOT parseable. Digest examples use
real subjects only.

### 3.2 Persistence

Three lifetime clauses:

  1. `for <duration>` — auto-expire by wall clock on Director tick.
  2. `until <constraint-expr>` — auto-expire when the inner expression holds for two
     consecutive Director ticks.
  3. no clause → sticky until explicit `rescind` or `clear directives`.

`Directive` = `{ id (monotonic int), tag (optional string), kind (DirectiveKind enum
— distinct from verb taxonomy), subject, predicate, DirectiveValue value (tagged
struct: `{ ValueKind Kind; float Scalar; float IntervalLo; float IntervalHi; string
Label; float RateNumerator; string RateUnit; }` — C# 7.3 has no record/discriminated
union so this is a tagged-struct), lifetimeClause, source ∈ {Op, LLM, Auto}
(Auto = directive injected by Director itself for tracking, e.g. calibrated band
override), createdMono, expiresMono?, rescindedMono?, descriptionFreeText? }`.
Max 32 active; overflow evicts oldest non-sticky with `TRIG=directive-evicted` in
next digest. **All-sticky overflow** explicitly rejects new with `TRIG=directive-overflow-all-sticky`
and operator must `rescind` first.

**32-cap eviction race (MAJOR-fix)**: three concurrent producers (FSW poll thread,
IMGUI main thread, DevCommand main thread) can simultaneously call `DirectiveSurface.Add`
at the 32-entry threshold. `ConcurrentDictionary` is per-key atomic but the compound
find-evict-insert is not. Wrap eviction-then-insert in `lock(_evictLock)` (or
`Monitor.TryEnter` with 50 ms timeout). **Expiry sweep race (MAJOR-fix)**: `until
<constraint-expr>` evaluation runs on the Director tick (0.2 Hz constraint pass),
NOT the DirectiveSurface 1 Hz sweep — sweep only reads the `expiresMono` long.
Eviction by tag/dedup uses the same `_evictLock`. **Snapshot for IMGUI**:
`DirectiveSurface.SnapshotDirectives()` returns an immutable `List<Directive>` so
the OnGUI thread never iterates the live `ConcurrentDictionary` during writer churn.

`_nextId` high-water (MAJOR-fix): on `Director.Init`, after loading `directives.json`,
set `_nextId = max(loadedIds, journalCursor) + 1`. The journal's last `directive-id`
record is the authoritative high-water if `directives.json` is partially-written or
corrupted.

Serialised to `{modDirectory}/SmartAI/Director/directives.json` on every accepted
change (atomic-write: `File.WriteAllText(path + ".tmp", json); File.Replace(path + ".tmp",
path, path + ".bak")` — never truncates the live file mid-write; matches
`ProfilePersistence.cs:153-169` pattern); reloaded on `Director.Init`. Path placeholder
`<UserDir>` is `{modDirectory}` (= `Environment.CurrentDirectory + "/Mods"` per
`LearningService.cs:75` `Init(WorkerPool pool, string modDirectory)` parameter +
`SnapshotManager.cs:21,23,28` convention), NOT
`Application.persistentDataPath`. Mirror-journaled to `journal.jsonl` as
add/rescind/expire records.

**Rollback contract**: directives are NOT rolled back. After a rollback, Director
journals a `DIRECTIVE-REAPPLY` record for every standing directive that mutates
restored state and the forced post-rollback digest emits a
`STANDING-DIRECTIVES-REAPPLIED:` line listing them. Operator can rescind in the same
breath.

### 3.3 Parsing

Parser splits across three files per realistic LOC accounting (MAJOR-fix; original
"~200 LOC" was tight for 14 verb-shape sub-grammars):

  * `Smart/Director/DirectiveTokenizer.cs` (~80 LOC) — recognises `[`, `]`,
    identifiers-with-dots, durations `30m|30min|30s|<int>h`, rate-units `/min`,
    signed numbers, intervals `[lo, hi]`, keywords. Token shapes are explicit:
    rate values `<float>/min` only; duration values `<int>{s|m|min|h}`. Unknown
    unit → reject with `[DIRECTOR-REJECT] unrecognised-unit:<token>`.
  * `Smart/Director/DirectiveParser.cs` (~250 LOC) — hand-written recursive-descent
    over the verb-first grammar. 14 verb sub-parsers. No regex, no Sprache/Antlr
    (C# 7.3 + dependency-free per build-setup memory). Parser is invoked on the
    worker thread inside the supervised RunLoop (AbortGuard handles TAE);
    `Parse(...)` catches all internal exceptions and returns a
    `ParseResult { Ok = false, Error = string }` — no exception propagation past
    the call boundary. FSW callback is a thin wrapper: `try { _pendingFiles.Enqueue(e.FullPath); _wake.Set(); } catch { /* swallow — FSW on threadpool */ }`.
  * `Smart/Director/SubjectResolver.cs` (~100 LOC) — static `SubjectResolver.Map`
    table. Examples: `hunter.kills_per_min` → (IdentityRateMetric, Hunter,
    KillScored, perMin); `intent.entropy_ratio` → (ModelReservoirMetric, Intent,
    EntropyReservoir, mean / ln(OutDim)). Unknown-subject error formatting names
    legal subjects.

Constraint expressions support only `metric op value`, op ∈ {`<`, `>`, `<=`, `>=`,
`=`, `in [lo,hi]`}; no boolean composition. Unknown subject → reject with parse error
containing offending token + legal-subject list. Combined parser surface ≈ 430 LOC
(was claimed 200; revised per V-fix).

---

## 4. Constraint vocabulary

All bands are tunable via `<UserDir>/SmartAI/Director/constraints.json` overlay; C#
defaults below. Each constraint has a 60 s scenario-warmup window (no eval during
warmup), an `INSUFFICIENT_DATA` state for n-below-threshold windows, and the 5-min
post-remediation observation window described in L2.

| Name | Metric (live source) | Healthy band | Violation action chain (debounce 3 ticks; 5-min observation after fire) | Needs new publisher? |
|---|---|---|---|---|
| `intent_entropy_ratio` | Rolling 60 s mean Shannon entropy of `OpponentIntentClassifier` softmax / `ln(OutDim=6) = 1.7918` → ratio. **Push site (BLOCKER-fix)**: `OpponentIntentClassifier.TrainOneMinibatch_FullBptt` after `MlpUtil.Softmax(probs, OutDim)` at line 266 — `probs` already computed for cross-entropy; single-writer trainer thread; no extra forward cost. `Evaluate` is the inference path (softmax at line 129) BUT has zero consumer call sites in the current source (unwired); when wired, it will snapshot tau once and push too. Helper `ShannonEntropy(p, len)` explicitly handles `p[i] <= 1e-9f` → contributes 0 (avoids NaN on degenerate softmax). INSUFFICIENT_DATA if any batch's softmax `sum < 1e-12f` (degenerate). Warmup: 1000 minibatch steps | `ratio ∈ [0.30, 0.85]` (≈ [0.54, 1.52] nats) | Below → `temp_adjust(Intent, +0.10)` (log-tau). Above → `temp_adjust(Intent, -0.10)`. Escalate after 5 consecutive → `freeze_model(Intent, diagnostic, 600 s)` | No |
| `actionvalue_loss_variance_ratio` | `Var(LossBefore) / max(Mean(LossBefore), 1e-6)` over rolling 256 minibatches from `LossReservoir[ActionValue]`, **reward-scale-normalised by rolling EWMA of `|reward|`**. **Reward-access (BLOCKER-fix)**: TrainerWorker.RunLoop has no visibility into `batch[i].Reward` (only `TrainStepResult{LossBefore, LossAfter, BatchSize}` returns from `TrainOneMinibatch`). Solution: `ActionValueEstimator` gains a `private float _ewmaAbsReward` field updated per-minibatch INSIDE `TrainOneMinibatch` under SaveMutex (where `batch[].Reward` is in-scope), exposed via `public float EwmaAbsReward => _ewmaAbsReward`. TrainerWorker.RunLoop reads `_model.EwmaAbsReward` and uses it to normalize the reservoir push. Push gated on `Source==Live` envelopes (NOT replay — see §10 typed-event Source byte) so replay-driven training doesn't inflate variance. **INSUFFICIENT_DATA if recentBatches < 8 OR Mean(|reward|) < 0.05** (sparse cold-start). Normalizer floor `max(EwmaAbsReward, 0.1)`. **Source/Replay gating**: typed events carry their own `Source` byte (separate from IdentityOutcome.Source); LossReservoir push only fires on Live | `ratio < 3.0` (initial; operator can `calibrate` to set bands from cold-start p50) | First → `lr_scale(ActionValue, 0.5)` (primary remediation; SamplingMPC sigma is NOT a lever for ActionValue loss-variance — see §12 design decision). Sustained ≥ 3 evals (≥ 15 s) → `lr_scale(ActionValue, 0.5)` again. Sustained ≥ 6 → `freeze_model(ActionValue, diagnostic, 300 s)`. Sustained ≥ 12 → `rollback(memory, ActionValue)` | No |
| `threat_param_delta_l2_norm` | `‖Δθ‖₂ / sqrt(N_params)` (architecture-invariant) for `ThreatAssessmentModel`, full-pass snapshot every 32 trainer steps (no stride — Threat 1.9k floats; Intent ~16k; ActionValue ~10k; combined ~50 µs across 4 models). p99 over 60 s window. **Snapshot mechanism (MAJOR-fix)**: TrainerWorker.RunLoop owns two cached `float[]` buffers per model (BEFORE / AFTER). BEFORE snapshot via existing `StoreParameters(float[] dest)` accessor on `ILearnedModel` (already public, already in interface contract at OnlineTrainer.cs:54) BEFORE the `lock(model.SaveMutex)` block. AFTER snapshot via the same accessor AFTER lock release. L2 computed CPU-only outside the lock. No need to expose `_params` directly. Snapshot trio captures one shared `capturedTau = Volatile.Read(ref LearningTuning.IntentTemperature)` at top BEFORE iterating models (no per-model tau drift). | `p99 < 0.01` (≈ 10 × per-step LR; per-model calibrated; runaway ≥ 10× band; cold-start: use 5× steady-state band for first 300 s after model init; trigger gated on ParamDeltaReservoir ≥ 32 samples) | First → `lr_scale(Threat, 0.5)`. Sustained → `freeze_model(Threat, diagnostic, 600 s)`. Runaway → `rollback(memory, Threat)` | No |
| `hunter_kills_per_min` | `IdentityOutcomeConsumer.GetRatePerMin(Hunter, KillScored, 300)`. Gated on `LiveSmartTechCount(Hunter) ≥ 4 ∧ ≥ 1 hostile alive` (parameterized per-identity overload of `LiveSmartTechCount` — see §12.2 SmartRuntime.cs row; both the parameterless property AND the `LiveSmartTechCount(SmartIdentity)` overload are added). **Rate accessor mechanism (BLOCKER-fix)**: `IdentityOutcomeConsumer` today is a pure monotonic `ConcurrentDictionary<long,long>` counter (`IdentityOutcomeConsumer.cs:60-71`); rate-over-window state must live IN the consumer, not the caller. Implementation: per-(identity, kind) `ConcurrentQueue<long ticks>` ring of recent publish timestamps, trimmed at read time to entries `>= now − windowSec`. Capacity bounded by `expectedRate × windowSec × safetyFactor`. `GetRatePerMin` returns `INSUFFICIENT_DATA` when no entries OR sampleCount < 3 in window (distinguishes "zero publishes this window" from "consumer state cold"). Note: existing `SmartEventBridge.cs:326` filter `if (attackerIdentity != Identity.SmartIdentity.Generic)` excludes Generic-stamped attackers; hunter rate is inherently over non-Generic Hunter techs only — document this in consumer header | `≥ 0.5/min` when gated | (1) Check `BankFullness(Hunter) ≥ minReplay` then `replay_bank(Hunter, 256)`; (2) sustained 600 s → `scenario_respawn`; (3) still sustained → `scenario_set(S1, intensity=1.25)` | No (KillScored exists, `SmartEventBridge.cs:330`) |
| `gatherer_delivery_rate` | `IdentityOutcomeConsumer.GetRatePerMin(Gatherer, BlockDelivered, 300)`. Gated on `ActiveScenario ∈ {S2, S5}` | `≥ 0.5/min` when ≥ 2 Gatherers alive | (1) Prod ChunkRegen if `chunk_field_deficit > 0` (NEW internal action; check before assuming policy is bad). (2) Check `BankFullness(Gatherer) ≥ minReplay` then `replay_bank(Gatherer, 256)`. (3) 600 s sustained → `scenario_respawn(resetChunks=true)`. (4) 15 min sustained → `scenario_set(S2, intensity=1.0)` | Yes (BlockDelivered) |
| `base_hp_retention_rate` | `BaseHeld_count / (BaseHeld_count + BaseLost_count)` over 60 s window with 5 min EWMA. **Dead bases are counted** (BaseLost is a paired publisher; **BaseLost is a NEW `OutcomeKind` value** added to the enum at `Smart/World/EventBus.cs:163-169` — see §12.2 EventBus.cs row). `INSUFFICIENT_DATA` if `(BaseHeld + BaseLost) < 3`. **Warmup explicit (MINOR-fix)**: requires 3 minutes of windows (3 × 60 s) before HEALTHY can first be reported; digest §6.2 trigger spec suppresses `TRIG=stuck:base_hp_retention_rate` when state is INSUFFICIENT_DATA. HP polled at 0.2 Hz (cached, re-walked only on block-detach via `Tank.DetachEvent` — see §10 row for the cleaner Tank-level subscription vs per-block hook). Gated on `ActiveScenario ∈ {S3, S5}` | `EWMA > 0.6` when ≥ 1 Base alive | (1) `replay_bank(Base, 128)`. (2) Sustained → `scenario_set(S3, intensity=0.5)` (de-escalate). (3) `EWMA < 0.4` → `rollback(previous)` | Yes (BaseHeld + BaseLost; BaseLost is new OutcomeKind=4) |
| `ally_protected_weighted_rate` | `IdentityOutcomeConsumer.GetWeightedRatePerMin({AircraftSupport, RepairSupport, Patrol}, AllyProtected, 300)`. Publisher emits **graded** magnitude `max(0, 1 - friendlyDamageInWindow / damageBudget=500)` (NOT all-or-nothing). Gated on `ActiveScenario == S4` for AircraftSupport; `∈ {S3, S5, S6′}` for RepairSupport / Patrol | weighted `≥ 0.10/min/instance` | (1) `replay_bank(<identity>, 128)` after BankFullness check. (2) Persistent → `scenario_respawn` | Yes (AllyProtected) |
| `live_smart_tech_floor` | `SmartRuntime.LiveSmartTechCount`. Gated on `ActiveScenario != None ∧ ScenarioWorker.HasActiveTemplate` (prevents permanent violation when no scenario active) | `≥ max(4, 0.75 × ActiveScenario.TargetTechCount)` | `scenario_respawn`. 60 s sustained → `scenario_set(top-weight scenario, intensity=1.25)` | No |
| `damage_events_per_min` | `DamageObserved` cumulative counter (NEVER reset; two-snapshot diff for rate). **Rate mechanism (MAJOR-fix)**: maintain a single long counter, on each 1 Hz Director tick capture `(countNow, monoTickNow)`, ratePerMin = `((countNow - countWindowStart) * 60_000) / (monoTickNow - tickWindowStart)`. Window-start values captured on sliding 60 s schedule. This eliminates the increment-and-reset race classical pattern. Gated on `ActiveScenario.HasHostileEngagement ∈ {S1, S3, S4, S5, S6′}` | `≥ 5/min` | `scenario_respawn`. 2nd consecutive → `scenario_set(S1, intensity=1.0)` | No |
| `ttl_discard_rate` | Sum of all 4 `TrainerWorker.TtlDiscardsTotal` over 10 min window | `Δ < 1000 events/10 min` (renamed from `_floor`; direction is a ceiling — operator language: `maintain ttl_discard_rate < 1000`) | **Diagnostic-only**: emit out-of-band digest tagged `[TTL-DISCARD-ALERT]`. No automatic action — operator decision required | No |
| `subneutral_relations_intact` | **No EnumRelations API exists (BLOCKER-fix)**: Director walks `directorOwnedSubNeutralPairs` itself: `int count = 0; foreach (var (a,b) in pairs) if (ManBaseTeams.GetRelationsWritablePriority(a, b, TeamRelations.Enemy) < TeamRelations.SubNeutral) count++;` using only verified public API (`ManBaseTeams.cs:1000`). Note: `EnemyTeamData.angerThreshold` is **per-team, NOT per-pair** (`ManBaseTeams.cs:159`) — zeroing it acts team-wide; the metric tracks per-pair RELATION state separately. **Per-tech `Provoked` (TankAIHelper.cs:3117) is independent of inter-team `angerThreshold`** — zeroing anger does NOT reset per-tech revenge timers, which dissipate by `AIGlobals.ProvokeTime` decay; the S2 invariant only anchors RELATION drift, not individual tech revenge | `count == 0` while ActiveScenario ∈ {S2, S5} | Re-anchor via `ManBaseTeams.SetRelations(a, b, SubNeutral)` and zero `angerThreshold` (internal action) — **marshaled to main thread** via ScenarioRelationsRequest envelope (see §8 BLOCKER-fix). Sustained → `scenario_respawn` | No |
| `movement_pathology_rate` | `IdentityOutcomeConsumer.GetWeightedRatePerMin(*, GuardViolation_Movement, 300) / max(LiveSmartTechCount, 1)` — buckets `{BackwardsLock, WedgedNoProgress, WaterloggedTech}` emissions across all identities (3 guards, not 4 — `OrbitalLockup` dropped per Validator-1 double-jeopardy with `OrbitNoFire`). **Source-byte high-nibble** carries the guard id so the aggregator can attribute the dominant bucket member in the digest without an `OutcomeKind` explosion. Population-invariant. `INSUFFICIENT_DATA` if `LiveSmartTechCount < 3` or guard-tick samples < 30 in window | `< 0.10 violations/tech/min` (tunable `smart.director.guard.movement_band`). Cold-start: first 120 s of any scenario instance excluded (movement-settling phase) | **Sub-guard-aware action chain** (Validator-2 fix — ActionPicker queries `IdentityOutcomeConsumer.GetDominantGuardId(bucket, scenario, 300s)` BEFORE picking the action): BackwardsLock-dominant → `replay_bank(Threat, 128)` (terrain/threat misread); WedgedNoProgress-dominant → `scenario_respawn` (placement); WaterloggedTech-dominant → diagnostic `[GUARD-WATERLOGGED-TECH]` only (authoring/terrain-hint issue, not policy). Sustained 600 s after any of the above → diagnostic `[GUARD-MOVEMENT-STUCK]` digest line (NO automatic `lr_scale` — movement is multi-model). 5-min post-fire observation per existing Director discipline | Yes (guard) |
| `role_task_pathology_rate` | `IdentityOutcomeConsumer.GetWeightedRatePerMin({Gatherer, RepairSupport, AircraftSupport, Hunter, Patrol}, GuardViolation_Role, 300)` — buckets `{IdleGatherer, OverdueDelivery, Homesickness, LostAlly}`. **(Prospector dropped from bucket — does not exist in `SmartIdentity` enum; AIType-Prospector techs classify as Gatherer; per V-fix BLOCKER on enum confusion.)** **OverdueDelivery weighted 1x at ship** (Validator-1: was 2x; soft-correct that injects what the tech is already doing is amplifier-amplifying-noise — promote back to 2x only after field data shows the convergence exception cleared the false-positives). Gated on `ActiveScenario ∈ {S2, S3, S4, S5, S6′}`. `INSUFFICIENT_DATA` if no role-eligible tech alive | `< 0.12 weighted violations/role-tech/min` | **Sub-guard-aware** (Validator-2): for Gatherer-bucket (IdleGatherer/OverdueDelivery dominant), check `chunk_field_deficit` FIRST — same precedence as `gatherer_delivery_rate` (chains cleanly with ChunkRegen prod). Then `replay_bank(<dominant-identity>, 128)` after BankFullness. Sustained 600 s → `scenario_set(current, intensity ×= 0.75)` (de-escalate while policy stabilises). Persistent → `rollback(memory, ActionValue)`. **IdleGatherer** is OBSERVE-only (Validator-1: terrain-isolation false positive surface) and contributes to the bucket but does NOT drive replay-bank on its own (reachability issue, not policy issue) | Yes (guard) |
| `combat_pathology_rate` | `IdentityOutcomeConsumer.GetWeightedRatePerMin({Hunter, Sniper, AircraftHunter, Patrol, RepairSupport}, GuardViolation_Combat, 300)` — buckets `{OrbitNoFire, FriendlyFire}`. **FriendlyFire weighted 2x at ship** (Validator-1: was 3x; the dodge-into-line and beam-sweep cases mean the test is not yet calibrated for unambiguous failures — promote to 3x only after field data validates the tightened attribution gate, see §9.3 Combat bucket). `OverextendedHunter` is OBSERVE-only and NOT included. Gated on `ActiveScenario.HasHostileEngagement ∈ {S1, S3, S4, S5, S6′}` | `< 0.10 weighted violations/combat-tech/min` | **Sub-guard-aware** (Validator-2): FriendlyFire-dominant → `replay_bank(Threat, 256)` (threat-classification failure); OrbitNoFire-dominant → `replay_bank(ActionValue, 128)` (action-decision failure) + `temp_adjust(Intent, +0.10)` if persistent. Sustained 600 s → `temp_adjust(Intent, +0.10)`. Persistent → `rollback(memory, Threat)`. Digest surfaces per-sub-guard `fired_count + weighted_contribution` so operator can see which sub-guard drives the bucket | Yes (guard) |
| `grounded_aircraft_intervention_rate` | Count of `GroundedAircraft` hard-correct fires per 10 min across all Aviator-AIType techs (read directly from `GuardCorrectiveActuator` journal, NOT via `IdentityOutcome` — hard-correct activation IS the event of interest, separate from observation outcomes) | `≤ 2 fires/min total` (tunable `smart.director.guard.aircraft_safety_band`). Above this means the controller cannot recover even with the impulse — a controller-level bug, not a guard issue | **Diagnostic-only**: emit `[GUARD-GROUNDED-AIRCRAFT-STORM]` digest tag with the affected tech ids. No automatic verb (the hard-correct itself was the correction; further automation would mask the upstream controller bug). Plan does NOT auto-disable the guard on storm — operator decides via `smart.director.guards.suppress GroundedAircraft` whether to silence for spawn-testing | Yes (guard) |

---

## 5. Action vocabulary (exactly 7 verbs)

| Verb | Effect | Parameters | Implementation site | Rollbackable |
|---|---|---|---|---|
| `lr_scale` | Multiplicative scale of one model's Adam LR within `[0.05×, 4.0×]` of `BaseLearningRate` (new field — preserves cold-start). Used to dampen loss-variance spikes (factor<1) or unstick stalled training (factor>1). **Rate-limit (MAJOR-fix)**: 1 per 30 s per-model enforced via `Interlocked.CompareExchange(ref _lastLrScaleMono[modelId], …)` — same atomic-CAS pattern as rollback rate-limit | `model ∈ {Intent, ActionValue, Residual, Threat}`; `factor ∈ [0.05, 4.0]` | `Smart/Director/Actions/LrScaleAction.cs`. Under `lock(model.SaveMutex)` sets `_adam.LearningRate = clamp(BaseLearningRate × factor, lo, hi)`. **AdamSnapshot accessor (V-fix BLOCKER)**: ILearnedModel exposes `AdamSnapshot GetAdamSnapshot()` returning the immutable struct, NOT the mutable `AdamState` reference type. Writers route through `ILearnedModel.SetLearningRate(float)` and `ILearnedModel.LoadAdamState(AdamSnapshot)`. The 7-Action invariant is then language-enforced, not just policy. `AdamState.LearningRate` reads from `AdamState.Step` (OnlineTrainer.cs:141) are inside the trainer's own `lock(_model.SaveMutex)` block (line 421) so no cross-thread race. `BaseLearningRate` set ONCE at AdamState construction and treated as immutable. ParamSnapshot also stores `baseLr` for rollback round-trip | Yes (prior LR in journal) |
| `temp_adjust` | Inference-side stochasticity knob, **Intent only**. Divides Intent logits by softmax temperature before `MlpUtil.Softmax` (softmax in TrainOneMinibatch_FullBptt at line 266). Range `[0.25, 4.0]`. Pure inference path — no weights touched. **REJECTED for ActionValue**: `SamplingMPC.SigmaThrottle/Steer/Brake` is a *controller* exploration knob with no causal link to `ActionValueEstimator`'s scalar Q-net loss-variance (verified: `ActionValueEstimator.cs:84-96` outputs scalar, no softmax; `SamplingMPC.cs:130-132` perturbs control vectors only). If MPC sigma tuning ever wanted, that's a separate non-Director knob | `model = Intent`; `delta ∈ [-0.5, +0.5]` in log-tau space | `Smart/Director/Actions/TempAdjustAction.cs`. Writes to `LearningTuning.IntentTemperature` (volatile float; ECMA-335 permits `volatile float`). **Read-once invariant (MAJOR-fix)**: `OpponentIntentClassifier.TrainOneMinibatch_FullBptt` captures `float tau = Volatile.Read(ref LearningTuning.IntentTemperature)` at top into a local; LossAfter forward pass MUST inline the softmax with that captured tau (do NOT call Evaluate which would re-snapshot). Two implementation options: (a) inline the LossAfter forward pass; (b) add `Evaluate(float[] sequence, float tauOverride)` overload and pass the captured tau through. Plan picks (a) for cleanest contract | Yes |
| `replay_bank` | Re-enqueues last N raw typed training events for one `SmartIdentity` from a per-(identity, ModelId) ring. **Tee points are at the typed-queue enqueue sites** (where source-tech's `SmartIdentityStamp.Identity` is attached at enqueue), NOT at `IdentityOutcome` (which is counter-only per `EventBus.cs:144-157`). **Tee site inventory (MAJOR-fix; post-feature-expansion reconciliation)** — source locations (revalidate cites on impl-time, file has drifted post-audit): (1) IntentEvent at `TargetObservationSequenceBuffer.cs:~210` (DrainAndEnqueue body); (2) ResidualEvent at `LeadResidualRecorder.cs:~121`; (3) ThreatEvent at `LearningService.cs:~683` (was 694, line shifted after BUG-026 EnemyVehicleSnapshots + BUG-046 AdamState + BUG-022 SaveMutex wrap pushed Threat.EventQueue.Enqueue up); (4) **ActionValueEvent producer SHIPPED** (feature-expansion commit e9bfcd4): `ContinuousController.PublishActionValueEvent` at ContinuousController.cs:444, called at :310, enqueues `learning.EventQueue.Enqueue(new Learning.ActionValueEvent(...))` at :514. Tee at **4 sites**; `replay_bank(*, ActionValue)` is functional in v1 with the same BankFullness preconditions as the other three models. BUG-011 corrected Q-target to use max_a' (Q-learning, not SARSA). **Type-erasure mechanism (MAJOR-fix)**: 4 type-specialized dictionaries `Dictionary<SmartIdentity, BoundedQueue<EventEnvelope<IntentEvent>>>` (etc.) — NOT a single `BoundedQueue<EventEnvelope>` with boxing. `EventEnvelope<T>` is a wrapper `struct EventEnvelope<T> { byte Source; SmartIdentity Identity; T Event; byte ReplayCount; }` where ReplayCount enforces the per-envelope multiplicity cap = 2. Drain knows which dict it draws from → typed dispatch with no boxing, no GC churn on the 30 Hz trainer hot path. **Per-envelope multiplicity cap = 2** to prevent in-bank over-fit loops (stored in `ReplayCount`). **Skipped journaled** if `BankFullness(identity, model) < minReplay`. **Source byte distinction**: typed-event Source byte (Live=0 / Replay=1) is SEPARATE from `IdentityOutcome.Source` byte (which carries guard-ordinal high-nibble + Replay low-nibble). TrainerWorker.RunLoop reads Source after dequeue; gradient-steps normally; gates LossReservoir push on Source==Live so replay doesn't double-count loss-variance | `identity`; `count ∈ [16, 512]` | `Smart/Director/Actions/ReplayBankAction.cs` + `Smart/Director/IdentityReplayBank.cs`. Per-(SmartIdentity, ModelId) typed `BoundedQueue` capacity 4096. Drain dispatches under `model.SaveMutex` with `Monitor.TryEnter(model.SaveMutex, 100 ms)`; miss journals SKIPPED. Drain does NOT call any other model API that takes a second lock — only `ConcurrentQueue.Enqueue` on the model.EventQueue (lock-free). Drain also bails immediately if Director's `_killSwitch` is set so QuitSaveCoordinator's FlushPendingForPersist (which also takes SaveMutex) is not blocked at shutdown. Replayed envelopes carry `Source=Replay` so `IdentityOutcomeConsumer` ignores them | No (events fold into Adam) |
| `freeze_model` | Two modes: **diagnostic** (default — leaves the typed event queue untouched, lets it backpressure-drop at capacity; on Enqueue-failure emits `[DIAGNOSTIC-DROP]` via rate-limited LogWarnFileOnly with per-100-events key so operator sees the data loss); **soft** (drain-to-discard via new `ILearnedModel.DrainOneMinibatchDiscard()` accessor, ~5 LOC per model = ~20 LOC additional; used for ttl_discard control). Either way, trainer skips `_adam.Step` + `DoubleBuffer.Write` until `FrozenUntilTickMono`. Auto-thaw via clock. **`AdamState.T` is preserved across freeze** (not reset) so bias correction at line 132-133 is mathematically consistent on resume. **Wall-clock semantics**: `durationSec` is wall-clock seconds; a freeze active during game pause expires by wall-clock without training resuming first (MonoClock = `Stopwatch.GetTimestamp()` does not stop during pause) | `model`; `mode ∈ {diagnostic, soft}`; `durationSec ∈ [60, 1800]` | `Smart/Director/Actions/FreezeModelAction.cs`. **`long FrozenUntilTickMono` per `ILearnedModel`** (BLOCKER-fix: `volatile long` is illegal C# — CS0677. Use plain `long` field + `Interlocked.Exchange(ref FrozenUntilTickMono, deadline)` for Director-side writes and `Interlocked.Read(ref FrozenUntilTickMono)` for trainer-side reads, mirroring the existing `_ttlDiscardsTotal` pattern at OnlineTrainer.cs:295-296). Freeze check inserted between TrainerWorker.RunLoop line 409 (IsHost gate) and line 421 (lock acquire) / 423 (TrainOneMinibatch). On Director shutdown, `BeginShutdown` sets `_killSwitch` AND releases any in-flight freeze deadlines so the FSM can drain | Yes (deadline in journal) |
| `scenario_set` | Sets active scenario id with intensity multiplier on target population. **ScenarioWorker is a PLANNER** — composes `RawTechPopParams` per recipe, enqueues `ScenarioSpawnRequest` envelopes onto the main-thread queue; engine spawns happen on main thread inside `SmartForm.Operations`. Prior Director-owned techs (tagged `§DIR-<scenarioId>-<n>`) gradually attrited; non-Director techs untouched | `scenarioId ∈ {S1, S2, S3, S4, S5, S6prime}`; `intensity ∈ [0.25, 2.0]` | `Smart/Director/Actions/ScenarioSetAction.cs` + `ScenarioWorker.cs` + `ScenarioRegistry.cs`. SubNeutral mints via `AIGlobals.GetRandomSubNeutralBaseTeam(forceNew=true)` (verified `AIGlobals.cs:916`); fresh hostile ids via `AIGlobals.GetRandomEnemyBaseTeam(forceNew=true)` (verified `AIGlobals.cs:905`) | Yes (prior id + intensity in journal) |
| `scenario_respawn` | Re-runs active scenario `Spawn()` without changing id. Constraint-target for `live_smart_tech_floor` and `damage_events_per_min`. Globally rate-limited to 1 per 30 s | `resetChunks: bool` (true → ChunkRegenWorker clears + re-seeds) | `Smart/Director/Actions/ScenarioRespawnAction.cs` → `ScenarioWorker.RequestRespawn(activeScenarioId, intensity)` | No |
| `rollback` | Three-tier atomic restore. Restores model `_params` + Adam moments + `LearningTuning` + active scenario id. DirectiveTable is NOT rolled back (operator policy survives); a `STANDING-DIRECTIVES-REAPPLIED` digest line surfaces directives that would re-clobber the restored state. Implicit pre-rollback checkpoint always taken (so rollback-of-rollback works). Globally rate-limited to 1 per 5 min | `tier ∈ {memory, previous, penultimate, named}`; `label?` | `Smart/Director/Actions/RollbackAction.cs` + `Smart/Director/DirectorCheckpoint.cs`. Uses the **new Director-owned pause channel** (see §7.3), not the host-loss `TrainerBarrier` path directly | No (irreversible by design) |

**Rejected verbs and why** — `freeze_layer` (single-layer GRU/MLP models have no
meaningful sub-layer split); `curriculum_swap` (collapsed into `scenario_set` with
intensity); `reset` (rollback `memory` covers catastrophic restore; full re-init out of
scope). `digest` is a `[DevCommand]`, not an action — pure read.

**Guard-awareness in existing verbs.** The BehaviorGuards layer (§9) does NOT add a
new verb; it extends the constraint vocabulary only and lets the Director pick from
the existing 7. Specific guard wiring:

  * `replay_bank` envelopes carry `Source=Replay`; the guard layer ignores replayed
    envelopes so guard-violation outcomes do NOT re-emit when their underlying
    training events are replayed (avoids constraint-loop amplification). Cause
    attribution in the journal names the dominant sub-guard:
    `cause=role_pathology:IdleGatherer=0.045`.
  * `temp_adjust(Intent, +0.10)` is the third escalation step on `combat_pathology_rate`
    because OrbitNoFire + FriendlyFire together indicate Intent-classifier under-exploration.
  * `rollback(memory, ActionValue)` is the final escalation on `role_task_pathology_rate`
    because sustained role-task failure across replays implicates the Q-net.
  * Guard-injected goals (hard-correct only: `AntiCrash`, `GroundedAircraft` — V-fix
    telemetry-only reconciliation: soft-correct `OverdueDelivery` is telemetry-only
    and does NOT flow through the mailbox; it publishes `IdentityOutcome`
    directly) are NOT Director actions — hard-correct guards flow through
    `GuardCorrectiveActuator` writing a `TacticalGoal` into the per-tech
    `TankAIHelper.GuardInjectedGoals` mailbox (verified primitive:
    `ISmartGoalSource` returns a single `TacticalGoal` per tick; there is no
    native priority chain in `SmartIdentityRegistry`). `ContinuousController.OnOperationsTick`
    is modified (§12.2) to read `GuardInjectedGoals` AFTER `SmartIdentityRegistry.For(identity).Produce`
    and substitute under the documented override semantics (§9.7) for
    hard-corrects only. The Director sees aggregate rates only, never
    individual injections; this preserves the **7-verb invariant**.

---

## 6. Digest format

### 6.1 Cadence

Every 600 s, phase-aligned to wall-clock minute boundaries (multi-session
comparability). **Clock-source pin (MAJOR-fix)**: schedule next emit deadline from
`DateTime.UtcNow` rounded UP to next 10-min wall-clock boundary; `DigestSnapshot`
records BOTH `wallclockUtc` (ISO-8601) and `processUptimeMono` (T+HH:MM:SS) so
operators see both views. Guard against system-clock backwards-jump by floor-clamping
the next deadline to `lastEmitMono + 60_000 ms` minimum (resists DST / NTP edits).

### 6.2 Trigger events (out-of-band)

  * periodic 600 s phase-aligned emission → `TRIG=tick`
  * `rollback` action fires → `TRIG=rollback`
  * `scenario_set` / `scenario_respawn` fires → `TRIG=scenario:<id>`
  * constraint first-violation in 30 min → `TRIG=constraint:<name>`
  * constraint stuck > 3 consecutive ticks without successful remediation → `TRIG=stuck:<name>` (suppressed when state is INSUFFICIENT_DATA; not a stuck-trigger)
  * Director or any L3 daemon respawn via DaemonWatchdog → `TRIG=respawn:<daemon>` (mechanism: NEW `DaemonRespawned(string daemonName, long monoTick, RespawnReason reason)` event struct on `WorldEventBus`; `DaemonWatchdog.AttemptRespawn` calls `WorldEventBus.PublishFromWorker(new DaemonRespawned(...))` on success — added in §12.2 EventBus.cs row)
  * directive accepted → `TRIG=directive:<id>`
  * host transfer edge → `TRIG=host-changed`
  * operator `digest now`
  * `‖Δθ‖₂/sqrt(N)` spike > 10 × healthy band → `TRIG=runaway:<model>` (gated on `ParamDeltaReservoir[model] ≥ 32 samples`; cold-start uses 5× steady-state band for first 300 s)
  * ~~publisher running on degraded path → `TRIG=publisher-degraded:<kind>`~~ (DROPPED — no degraded-path producer remains; BlockDelivered fallback was dropped per Validator-1)
  * guard-rate constraint band breach → `TRIG=guard-storm:<bucket>` (movement / role / combat)
  * GroundedAircraft hard-correct activation → `TRIG=guard-correct:GroundedAircraft`
  * OverdueDelivery telemetry-only fire (V-fix reconciliation: soft-correct
    is now telemetry-only — no arbitration, no mailbox; the trigger fires when
    `IdentityOutcome(GuardViolation_OverdueDelivery)` is published by the
    pathology window) → `TRIG=guard-fire:OverdueDelivery` (informational;
    signals a Gatherer pathology was detected, training signal emitted)

**Trigger coalescing (MAJOR-fix)**: if ≥2 triggers fire within one Director 1 Hz tick,
coalesce into a single emission with union trigger list: `TRIG=host-changed+runaway:Threat+scenario:S5`.
Maximum 1 emission per Director tick. Implemented as a pending-trigger set drained at
end-of-tick.

**Cold-start trigger suppression**: triggers ∈ {host-changed, scenario_set, scenario_respawn}
suppress if `TrainingDirector.AgeSinceInitSec < 60`. Suppressed triggers recorded in
next periodic digest as `pending_suppressed_triggers=[host-changed@t-23s]`.

### 6.3 Format spec

Plain text, **≤ 120 chars/line typical**; LEGEND/CONSTRAINTS/ROLLBACK_AVAIL may wrap
to one continuation line each (MINOR-fix: original ≤80 was inconsistent with column
table widths in §6.4 example which require ≥107 chars). ~30 lines.

**DigestSnapshot struct (MAJOR-fix — was undefined)**. Fields:
`WallclockUtcSec` (long, UTC seconds), `ProcessUptimeMonoSec` (long, MonoClock
seconds), `Trigger` (string, possibly comma-separated for coalesced), `Scenario`
(int), `Intensity` (float), `ScenarioAgeSec` (int), `ChunkLive` (int), `ChunkTarget`
(int), `ModelStats` (`ModelStat[4]` { loss_mean, lossvar_over_mean_reward_norm,
entropy_ratio, dL2_p99, lr_now, steps_per_min, frozen }), `IdentityRates`
(`Dictionary<SmartIdentity, RatePayload>`), `GuardRates`
(`Dictionary<SmartIdentity, Dictionary<GuardId, GuardRate>>`), `SystemStats`
(struct), `ConstraintViolations` (`List<ViolationLine>`), `Interventions`
(`List<InterventionLine>`), `DirectivesActive` (`List<DirectiveLine>`),
`RollbackAvailability` (struct), `HintLine` (string). Both projections —
`DigestBuilder.SnapshotToText(snap)` and `SnapshotToJson(snap)` — are derived
PURELY from the snapshot; neither may read DirectorState directly. JSON serializer:
reuse `Smart/Tooling/PresetIO.cs` (MiniJson).

Written to:

  * `{modDirectory}/SmartAI/Director/digest.latest.txt` using the verified
    ProfilePersistence.cs:153-169 atomic-write pattern (File.Replace with
    PlatformNotSupportedException → Delete+Move fallback; File.Move for first-save).
    Unique tmp name per emission: `digest.latest.txt.tmp.<monoTickHex>` so
    concurrent triggers cannot clobber each other's tmp.
  * `digest.history.log` (append-only, rotated at 5 MB → `digest.history.<N>.log`;
    gzip > 30 d to `digest-YYYY-MM-DD.log.gz`; total cap 100 MB then oldest deleted —
    same retention regime as journal per §7.4).
  * **NOT via `DebugTAC_AI.LogWarnFileOnly`** (BLOCKER-fix: `LogWarnFileOnly` uses
    a permanent per-key HashSet dedup at `DebugTAC_AI.cs:120-129` that is never
    cleared. Routing digest body through it would log only the first emission.
    Plan instead routes digest body via direct `UnityEngine.Debug.Log("[DIRECTOR-DIGEST] " + body)`
    bypassing the dedup. TRIG-line summary uses the same direct path with
    `[DIRECTOR-TRIG]` tag, NOT LogWarnFileOnly).
  * IMGUI panel via `DrawPathingDebugGUI` tail — **panel resized**: when
    `smart.director.gui.show==true`, the existing 280×182 `GUI.Box` at
    `SmartForm.cs:688` (single-line call inside `DrawPathingDebugGUI` which begins
    at `:678`) expands to ~560×400 px with `GUI.BeginScrollView` +
    persistent `_directorPanelScrollPos` field. Dynamic content-rect height =
    `lineCount × 18 + padding`. Without this, the digest would clip to the first
    ~10 lines.
  * JSON sibling at `digest.latest.json` produced from the **same `DigestSnapshot`
    struct** so the two cannot disagree by construction. Same atomic-write discipline
    as `.txt` sibling.

**Cross-thread safety (MAJOR-fix)**: `DirectorState.LatestDigest` MUST be
`volatile string` (immutable string, atomic ref-swap). Background DigestBuilder
builds the body into a private StringBuilder, then assigns the resulting `string`
to `LatestDigest` atomically. Main-thread OnGUI reads `LatestDigest` as a single
ref read — never iterates a backing collection. **DigestBuilder.BuildSnapshot is
read-only** of volatile fields + reservoir Percentile (which is self-contained);
it MUST NOT take any per-model SaveMutex (lock-cycle prevention). `model.FrozenUntilTickMono`
read via `Interlocked.Read` (no SaveMutex needed). **Sink exception isolation
(MAJOR-fix)**: each sink wrapped in its own try/catch; order is (1) LatestDigest ref
assignment (cheapest, never throws); (2) WorldEventBus publishes (low-risk);
(3) journal append; (4) digest.history.log append; (5) digest.latest.txt atomic
write (most failure-prone). Last failure does not undo earlier successes.
**Shutdown gate**: `DigestBuilder.Emit` checks `Director.IsShuttingDown` at top and
no-ops if set. `Director.BeginShutdown` BLOCKS via `ManualResetEventSlim _emitLoopGate`
until in-flight Emit completes its current tick. FileStream open path catches
ThreadAbortException explicitly + AbortGuard.Absorb per Threading-contract pattern.

`N/A` is used where a metric does not apply to a model class (legend in footer). One
line per *populated* (identity, kind) pair — no empty-cell matrix.

### 6.4 Example

```
===== Smart Director Digest [2026-06-05T14:30:00Z] T+02:14:33 host=1 trig=tick =====
SCENARIO S5(int=1.0) age=22:31 chunkdef=14/40(live/target) techdef=0 subneutralOK=1
MODELS  (id     loss_mean  lossvar/mean(reward_norm)  entropy_ratio  dL2/sqrtN_p99  lr_now    steps/min  frozen)
  Intent        0.642      0.41                       0.51           N/A            1.0e-4    312        no
  ActVal        0.118      1.61(!)                    N/A            0.0011         5.0e-5(d) 287        no   <-- VIOL actionvalue_loss_variance_ratio
  Resid         0.024      0.22                       N/A            N/A            3.0e-4    198        no
  Threat        0.301      0.55                       N/A            0.0042         1.0e-4    341        no
IDENTITY-RATES (populated cells only)
  Hunter        kills/min=0.71
  Gatherer      deliv/min=0.21(!)   bank=132/4096       <-- VIOL gatherer_delivery_rate
  Base          hp_retain_ewma=0.83 sample=6 (held=5,lost=1)
  AircraftSup   prot_wgt/min=0.12   sample=4
GUARDS (last 10m, populated cells only, per-tech rates/min)
  Movement      backLock=0.04[obs] wedge=0.02(W) water=0.00[obs]
  Role          idleGath=0.03[obs] overdue=0.06(SC/tele)(!) homesick=0.02[obs] lostAlly=0.01(W)   <-- VIOL role_task_pathology_rate
  Combat        orbitNoFire=0.02(W) friendlyFire=0.00(W) overextend=0.05[obs]
  Safety        grounded_aircraft fires=0 releases=0 oscill=0
  Rollup        pathology_total=18 distinct_techs=11 max_per_tech=3 p90_per_tech=2 soft_corrects=1 (telemetry-only) hard_corrects=0
SYSTEM techs=18 dmg/min=6.2 ttl_disc/10m=0 healthy_snap_age=4m32s
CONSTRAINTS OK=12/15 VIOL=3 [actionvalue_loss_variance_ratio:1.61>1.5 (2t), gatherer_delivery_rate:0.21<0.5 (4t), role_task_pathology_rate:0.13>0.12 (3t)]
INTERVENTIONS (last 10min)
  T+02:09:11 lr_scale(ActionValue, 0.5)          cause=loss_var=1.61    j-00043
  T+02:11:48 replay_bank(Gatherer, 256)          cause=deliv=0.21       j-00044
DIRECTIVES_ACTIVE (3)
  d-00007 [sticky] maintain hunter.kills_per_min >= 0.5            age=2h12m
  d-00012 [tag s2-baseline] maintain gatherer.delivery_rate >= 0.5 age=1h05m
  d-00019 [for 24m] prefer scenario S5 weight 0.5                  age=6m
ROLLBACK_AVAIL memory=t-04m(j-00037), previous=t-30m, penultimate=t-60m, named=[my-baseline,prefer-gatherer-1h]
HINT Gatherer rate low + bank thin -- consider scenario_respawn(resetChunks=true) before replay
LEGEND N/A = metric not applicable. chunkdef live/target. dL2/sqrtN = arch-invariant param-delta L2. GUARDS suffixes: [obs]=observe-only (count surfaced not constraint-bound), (W)=warn, (SC/tele)=soft-correct fired (telemetry-only — V-fix reconciliation: never substitutes goals; publishes IdentityOutcome for training-signal only), (HC)=hard-correct fired, (!)=bucket-constraint violated, oscill=guard re-fire within cooldown (anti-stifling telemetry).
=================================================================================================================
```

---

## 7. Rollback semantics

### 7.1 Checkpoint triggers

  1. **AUTO-HEALTHY**: every Director tick where ALL constraints have been HEALTHY for
     the preceding 5 minutes, in-memory `DirectorParamRing` snapshot per model
     (capacity 3, oldest evicted). Disk-tier auto-checkpoint via `ProfilePersistence.Save`
     fires at most every 30 min wall-clock when last 5 min were all-healthy.
     **Piggy-back mechanism (MINOR-fix)**: Director subscribes to a NEW
     `LearningService.ProfileSaved` event fired by `AutosaveWorker.TickOnce` after
     successful `SaveProfile`; on the event, Director writes tunables.json + director-config.json
     siblings next to the just-saved profile and stamps params.bin as a copy of the
     latest profile — NOT a separate write (no double-saving). Audit BUG-055
     (`LearningService.SaveProfile` entry now `IsHost`-gated, see AUDIT-BUG-LIST.md
     §BUG-055) means `ProfileSaved` is fired only on the host machine in MP, so
     the Director's piggy-back write is automatically host-only without an
     additional gate at the subscriber. The `!SmartRuntime.IsHost` skip below
     remains as defense-in-depth (auto-checkpoint also writes sibling JSON via
     `PresetIO`, which does NOT go through SaveProfile's gate). **3-condition skip
     (MAJOR-fix)**: AUTO-HEALTHY skipped if `SmartRuntime.IsPaused` OR `!SmartRuntime.IsHost`
     OR `_directorPaused-token-held` OR `_rollbackInProgress`. Without the last two,
     auto-checkpoint can capture mid-rollback torn state. The `!SmartRuntime.IsHost`
     clause is now defense-in-depth — backed by audit BUG-055 (SaveProfile host-gated)
     + BUG-018 (QuitSaveCoordinator host-gated) + BUG-019 (SmartForm.OnEngineSave
     host-gated) at the four caller sites + entry.
  2. **AUTO-PRE-ACTION**: before any destructive action (`rollback` itself,
     `freeze_model(soft)`, `scenario_set`, `lr_scale` where `|log10(factor)| > 0.3`),
     implicit `pre-<verb>-<ts>` in-memory snapshot, retained 30 min.
  3. **MANUAL**: operator `smart.director.checkpoint <name>` or directive
     `checkpoint <name>` → full disk trio under `SmartAI/Director/checkpoints/<name>/`.

### 7.2 Storage tiers

**T1 in-memory `DirectorParamRing`** — per-model 3-slot ring of `ParamSnapshot`:

```
ParamSnapshot {
  byte ArchitectureVersion;       // VALIDATED on restore — mismatched arch refuses to load
                                  // (V-fix: byte not short — ILearnedModel.ArchitectureVersion
                                  // is byte at OnlineTrainer.cs:50; ProfilePersistence.cs:22,111
                                  // writes byte; widening to short would force interface change
                                  // and break binary format)
  string ModelName;
  float[] paramsCopy;             // populated via existing ILearnedModel.StoreParameters(float[] dest)
                                  // accessor (V-fix: pseudocode previously used private _params.Clone())
  float[] adamM;
  float[] adamV;
  long adamT;
  float lr;
  float baseLr;                   // V-fix: BaseLearningRate also captured for rollback round-trip
  long monoTick;                  // process-relative; rollback step 6 journal-cursor match
  long wallclockUtcSec;           // V-fix: paired wall-clock timestamp for cross-restart restores
                                  // (monoTick resets per process — Stopwatch ticks)
  float intentTemp;
  float[] lrScales;               // per-model LrScale[4] for full LearningTuning restore
  float[] outcomeWeights;
  bool frozen;
  long frozenUntilMono;
}
```

(TuningSnapshot is captured as a separate global once per checkpoint trio. Per-model
snapshots store the per-model lrScale slot for symmetry but the canonical TuningSnapshot
is the source-of-truth on restore.)

Capture sequence is a **single critical section** using the public ILearnedModel API
(V-fix BLOCKER: private _params / _adam are inaccessible to Director; use the public
accessor `StoreParameters` already in the interface contract, plus NEW public
`ReadAdamMoments(float[] mDest, float[] vDest, out long T, out float lr, out float baseLr)`
symmetric to LoadAdamState — added in §12.2 OnlineTrainer.cs row):

```
// Tau captured ONCE at top of trio so all 4 model snapshots agree on tau
float capturedTau = Volatile.Read(ref LearningTuning.IntentTemperature);
foreach (var model in models) {
  lock (model.SaveMutex) {
    snapshot.ArchitectureVersion = model.ArchitectureVersion;     // byte
    model.StoreParameters(snapshot.paramsCopy);                   // public API
    model.ReadAdamMoments(snapshot.adamM, snapshot.adamV,         // NEW public API
                          out snapshot.adamT, out snapshot.lr,
                          out snapshot.baseLr);
    snapshot.intentTemp = capturedTau;
    snapshot.frozen     = MonoClock.Now() < Interlocked.Read(ref model.FrozenUntilTickMono);
    snapshot.frozenUntilMono = Interlocked.Read(ref model.FrozenUntilTickMono);
    snapshot.monoTick = MonoClock.Now();
    snapshot.wallclockUtcSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
  }
}
```

This atomicity invariant is documented in `DirectorParamRing.Capture` and is
load-bearing: without the lock, `params` and `AdamState.T` can be on opposite sides of
a single `_adam.Step` call, producing an internally inconsistent snapshot. The lock
boundary now uses the public accessors so the Director needs no privileged field
access. Validation on restore: snapshot.paramsCopy.Length must equal the model's
internal `_params.Length` (LoadParameters at OpponentIntentClassifier.cs:358 throws
ArgumentException on mismatch).

**Pre-allocated buffer pool (MINOR-fix)**: 'No GC pressure' is achieved via per-slot
pre-allocated `float[]` arrays in the ring, refilled via `Array.Copy` / `StoreParameters`
into the slot's buffer — NOT `.Clone()` per capture. Sized at ParameterCount per model
× 11 slots × 4 models ≈ 1 MB total. Cleared on world reset.

**R3-M2 caller-owned buffer invariant (doc note)**: `AdamSnapshot.adamM` and
`AdamSnapshot.adamV` are caller-owned `float[]` BUFFERS (allocated from the
pre-allocated ring-slot buffer pool above). They are NOT live references to the
model's internal `_adam.M` / `_adam.V`. The capture path uses
`ReadAdamMoments(snapshot.adamM, snapshot.adamV, out T, out lr, out baseLr)` which
`Array.Copy`s INTO the caller-supplied buffers under `model.SaveMutex`. Future
contributors MUST NOT wire capture through `GetAdamSnapshot` (which returns a struct
that may hold pointer-copies) and silently break atomicity.

**LoadParameters internal lock (MINOR-fix)**: Plan now requires LoadParameters in all
4 model implementations to internally `lock(_saveMutex)` (mirrors FlushPendingForPersist
at OpponentIntentClassifier.cs:340), so external callers (Director rollback AND existing
SnapshotManager.Restore at SnapshotManager.cs:79 which currently does NOT lock) cannot
race the trainer's `_publishedParams.Write(Clone(_params))` at line 352.

**T2 disk `ProfilePersistence` chain** — reuses existing `<playerId>.smart` +
`.previous` + `.penultimate` rotation at `ProfilePersistence.cs:64-95`. Director does
not add files at this tier, only reads.

**T3 disk Director-named checkpoints** — `SmartAI/Director/checkpoints/<label>/`:

  * `params.bin` (full ProfilePersistence dump via `SnapshotManager.Save` adapter)
  * `tunables.json` (`PresetIO` dump of all `smart.director.*` + `smart.training.*`)
  * `director-config.json` (`DirectorState` scenario + intensity + `DirectiveTable`
    hash + active constraint overlays + journal cursor)

Ring of 5 named slots; auto-prune older than 14 d at `Director.Init`; never delete a
slot while a directive references it.

### 7.3 Rollback action sequence

**Why a Director-owned pause channel.** The plan must NOT reuse the host-loss
`TrainerBarrier` directly. `TrainerBarrier.Signal` is called from `TrainerWorker.RunLoop`
inside the `if (_hostLost && _phase == Phase.Active)` branch only
(`Smart/Coordination/TrainerBarrier.cs:22`, `OnlineTrainer.cs:329-369`). `_hostLost`
flips only via the `HostChanged` event subscription. A Director call to `WaitAll`
without a host-loss returns instantly, with trainers actively mutating `_params/_adam`
— corrupt restore.

Fix: add a Director-owned pause path.

  * `TrainerWorker` gets a second `volatile bool _directorPaused` next to `_hostLost`.
  * `TrainerBarrier.AcquirePauseToken(string owner)` / `ReleasePauseToken(owner)`
    (mutual-exclusion lock on `_pausingOwner` so a host-change racing a rollback
    serialises; whichever owner held the token first wins). **R3-M6 hardening
    (TrainerBarrier.cs currently has no AcquirePauseToken / `_pausingOwner` — these
    are NEW and cannot be subsumed by existing Register/Signal/WaitAll):**
    * **(a) Mandatory try/finally pattern.** Every `AcquirePauseToken(owner)` call
      site MUST be wrapped in `try { ... } finally { ReleasePauseToken(owner); }`.
      Non-negotiable; reviewer-enforced.
    * **(b) Stuck-token watchdog.** If `_pausingOwner != null` for > 30 seconds with
      no active rollback in progress → force-release with
      `Interlocked.Exchange(ref _pausingOwner, null)` and emit
      `[PAUSETOKEN-LEAKED owner=X]` journal entry. Prevents a single uncaught
      exception from deadlocking all future host-changes.
    * **(c) `Director.BeginShutdown` must NOT acquire the token** (would deadlock if
      rollback already holds it). Instead: signal-and-wait — set `Director._killSwitch`,
      wait for the in-flight rollback's `finally` to release the token, then proceed.
      Bound by the existing 10 s shutdown-timeout invariant.
  * Trainer FSM treats `_directorPaused` identically to `_hostLost` for the
    Active→Pausing→Paused→Signal transitions.
  * `SmartRuntime.RequestDirectorPause()` / `ReleaseDirectorPause()` are the public
    surface.

Rollback sequence (BLOCKER-fix: try/finally restructured so pause-token + all
acquired resources release even on ABORT path; concurrent rollback gated by
atomic CAS; AcceptingTrainingEvents uses ref-counted semantics):

```
// Single-flight gate prevents concurrent rollbacks (was: naive timestamp check)
if (Interlocked.CompareExchange(ref _rollbackInProgress, 1, 0) != 0) {
  Journal("ROLLBACK-REJECTED-INFLIGHT");
  return;
}
try {
  // 1. Journal start
  Journal($"ROLLBACK started tier={tier} target={target}");

  // 2. Implicit pre-rollback checkpoint (so rollback-of-rollback works)
  if (tier != "memory") CapturePreRollbackCheckpoint();

  // Acquire pause token AND request Director pause INSIDE try
  SmartRuntime.AcquirePauseToken("Director");
  try {
    SmartRuntime.RequestDirectorPause();

    // 3. Wait for trainers to quiesce
    if (!TrainerBarrier.WaitAll(5000)) {
      Journal("ROLLBACK-ABORTED-WAIT-TIMEOUT");
      EmitDigest(TRIG_ROLLBACK_ABORT, reason: "trainer drain timeout");
      return;  // finally still releases pause/token below
    }

    // 4. Accept-events ref-count DECREMENT (NOT raw flag write; protects against
    //    OnHostLost / OnHostGained dual-writer race per V-fix BLOCKER)
    SmartRuntime.AcceptingTrainingEvents_Dec();
    try {
      // 4.5 EventQueue disposition: KEEP (do NOT flush) — see post-block doc note
      //     Forced post-rollback digest emits QUEUE-CARRYOVER:<n-events> per model.

      // 5. Per-model restore with INDIVIDUAL try/catch so model-2 fault doesn't
      //    abort the loop after model-1 restored. Per-model lock acquired then
      //    released; rollback is best-effort-per-model, NOT cross-model atomic.
      foreach (var model in affectedModels) {
        try {
          // Pre-validate ArchitectureVersion BEFORE LoadParameters (which only
          // length-checks). Skip with journal on mismatch.
          if (snapshot.ArchitectureVersion != model.ArchitectureVersion) {
            Journal($"ROLLBACK-SKIPPED-ARCH model={model.Id}");
            continue;
          }
          // Finite/range validation on tuning snapshot — guards against persisted
          // NaN/zero from a buggy prior session
          if (!float.IsFinite(snapshot.intentTemp) || snapshot.intentTemp <= 0f
              || snapshot.adamT < 0 || snapshot.lr <= 0f) {
            Journal($"ROLLBACK-SKIPPED-INVALID-TUNING model={model.Id}");
            continue;
          }
          // Per-model atomic load (LoadParameters internally locks SaveMutex
          // per V-fix; rollback's outer lock would still be safe but is no longer
          // load-bearing on the Write race)
          model.LoadParameters(snapshot.paramsCopy);
          model.LoadAdamState(new AdamSnapshot(snapshot.adamM, snapshot.adamV,
                              snapshot.adamT, snapshot.lr, snapshot.baseLr));
        } catch (ThreadAbortException) {
          // CLR auto-rethrows past catch(Exception); explicit catch + Absorb
          AbortGuard.Absorb();
          Journal($"ROLLBACK-ABORTED-TAE model={model.Id}");
          break;  // outer finally runs
        } catch (Exception ex) {
          Journal($"ROLLBACK-FAILED-PARAM-LOAD model={model.Id} err={ex.Message}");
          // Continue to next model — best-effort-per-model semantics
        }
      }

      // 6. Active scenario revert via JOURNAL CURSOR keyed on wall-clock UTC if
      //    cross-restart (monoTick non-comparable across process), else monoTick.
      DirectorState.ScenarioGeneration++;  // stamps invalidate in-flight spawn envelopes
      RestoreActiveScenarioFromJournal(snapshot.wallclockUtcSec, snapshot.monoTick);

      // 6.5 Guard observation state reset (V2 fix)
      GuardWindowPool.Clear();
      GuardCorrectiveActuator.ForceReleaseAll();
      GuardViolationCounters.Clear();
      // Force-shorten hard-correct cooldowns to MAX 2 s so safety-net can re-arm
      // if conditions persist post-rollback (V-fix: was preserved unchanged)
      GuardCooldownTable.ClampAll(maxCooldownMs: 2000);
      Journal("GUARD-WINDOWS-RESET");
    } finally {
      SmartRuntime.AcceptingTrainingEvents_Inc();  // ref-counted, NOT clobber-to-true
    }
  } finally {
    SmartRuntime.ReleaseDirectorPause();
    SmartRuntime.ReleasePauseToken("Director");
  }

  // 8. Final journal + digest (force-emit bypasses IsPaused gate for rollback trigger)
  Journal("ROLLBACK completed");
  ForceEmitDigest(TRIG_ROLLBACK, annotations: new[] {
    "STANDING-DIRECTIVES-REAPPLIED",
    "GUARD-WINDOWS-RESET (post-rollback reset markers in GUARDS section)"
  });
} finally {
  Interlocked.Exchange(ref _rollbackInProgress, 0);
}
```

**AcceptingTrainingEvents ref-count semantics (BLOCKER-fix)**: SmartRuntime exposes
`AcceptingTrainingEvents_Inc()` / `_Dec()`; the flag-bool is computed as
`_acceptingDisableCount == 0 && IsHost`. Concurrent OnHostLost / OnHostGained /
rollback all call Inc/Dec rather than clobbering a shared flag — no last-writer-wins
race. **OnHostChanged also acquires the pause-token** before flipping `_hostLost`
so host-change-during-rollback serializes behind rollback (BLOCKER-fix). **Director.BeginShutdown
is Interlocked-latched** (mirrors L-004 RequestShutdown pattern); it sets `_killSwitch`
and BLOCKS up to 10 s for in-flight rollback to finish; if 10 s elapses, journals
`SHUTDOWN-FORCED-WHILE-ROLLBACK-INFLIGHT` and continues (the half-written model gets
caught on next disk-load arch-version check OR discarded to .previous via
ProfilePersistence chain). BeginShutdown NEVER uses Thread.Abort.

**TrainerBarrier owner-gating (BLOCKER-fix)**: `TrainerBarrier.Reset` is gated on
`_pausingOwner == null` so OnHostGained's `TrainerBarrier.Reset` call (at
LearningService.cs:~240 post-audit drift; was 227 pre-BUG-075/023) defers until
pause holder releases. **Trainer FSM edit sites enumerated (BLOCKER-fix;
post-audit drift cites, revalidate at impl time)**: (a) OnlineTrainer.cs:~357
changes from `if (_hostLost && _phase == Phase.Active)` (was line 355) to
`if ((_hostLost || Volatile.Read(ref _directorPaused)) && _phase == Phase.Active)`;
(b) line ~393 changes from `if (!_hostLost)` (was 391) to
`if (!_hostLost && !Volatile.Read(ref _directorPaused))`;
(c) line ~409 unchanged (was 407; IsHost is orthogonal). Memory barrier `Volatile.Read(ref _directorPaused)`
immediately before the `_phase = Phase.Active` write at line ~398 (was 396) ensures ordering.
**Audit BUG-012 (HostChanged Unsubscribe pairing in TrainerWorker)** is a precondition
for the rollback design's assumption that "OnHostChanged also acquires the pause-token" —
without paired teardown, multiple stale TrainerWorker subscribers would each try to
acquire on a single host event.

**Step 4.5 — EventQueue disposition (KEEP, not flush)** (amendment A1).
Pre-rollback events in each `model.EventQueue` are RETAINED through the rollback.
Rationale: (1) three of four models are observation-based —
`OpponentIntentClassifier` learns enemy intent from observed sequences (input
is opponent behavior, not our actions); `TrajectoryResidualModel` predicts
coast-extrapolation residuals (pure observation error); `ThreatAssessmentModel`
scores configurations (observation in). For these, the policy that produced the
OBSERVATION of those events is irrelevant — the world produced them regardless.
(2) The one policy-dependent model (`ActionValueEstimator`) NOW has a live
producer (feature-expansion commit e9bfcd4: `ContinuousController.PublishActionValueEvent`
at ContinuousController.cs:444, enqueue at :514, per FEATURE-EXPANSION-PLAN §7.2).
Pre-rollback ActionValueEvents represent (s, a, r, s′) tuples whose policy may be
reverted; KEEP is still correct because tuples are anchored to the world state at
observation time and replay contamination surface is bounded by the same per-envelope
multiplicity cap = 2 in `EventEnvelope<ActionValueEvent>` as the other three models.
(3) Queues are bounded (`BoundedQueue` capacity 4096 per type-erased dictionary) —
contamination is small even when present. **Observability**: forced post-rollback
digest emits one extra `QUEUE-CARRYOVER:<n-events>` line per model so operator sees
what survived (the line is load-bearing for ActionValue now that the producer at
`ContinuousController.cs:514` ships per FEATURE-EXPANSION-PLAN §7.2).

### 7.4 Retention policy

  * T1 in-memory: 3 healthy + 8 pre-action with 30 min TTL; > 24 h flagged STALE in
    digest (not discarded).
  * T2 disk: unchanged; owned by `ProfilePersistence` rotation.
  * T3 named: ring of 5, auto-prune > 14 d on `Director.Init`; manual unlimited but
    each ≤ 200 KB. **Init order (MAJOR-fix)**: (1) load directives.json + journal; (2)
    build set of referenced labels; (3) prune checkpoints excluding referenced labels;
    (4) register canonical daemon. Without this ordering, a directive-referenced
    label could be pruned on cold start.
  * Journal: append-only at `SmartAI/Director/journal.jsonl`, rotated at 5 MB into
    `journal.<N>.jsonl`; compressed at 30 d into `journal-YYYY-MM-DD.jsonl.gz` via
    `System.IO.Compression.GZipStream` (available in .NET 4.6.1). RAM cap 2 048
    entries (separate ConcurrentQueue ring; producer enqueues to RAM ring AND
    fire-and-forget to disk worker; cap-hit drops oldest RAM entries silently —
    disk has them). Rotation uses `File.Move` with retry-on-IOException (Windows
    AV lock), max 3 retries with backoff.
  * `digest.history.log`: same retention regime as journal (5 MB rotation; 30 d gzip;
    100 MB total cap then oldest deleted). Applied at Director.Init like journal.

### 7.5 Checkpoint ratchet (fast-follow, NOT v1)

This subsection specifies a deferred quality-gate for checkpoint promotion
(amendment A2). Implementation is fast-follow scope (estimated ~400-600 LOC).
It does NOT count toward v1 LOC totals in §12.4 / §12.3.

**Motivation**. AUTO-HEALTHY's "5 min all green" criterion is recency-based.
Slow drift can stay under all constraint bands while being directionally worse.
Behavioral guards close part of this gap but not all.

**Evaluation battery**. Scenarios S1 (Open Brawl), S2 (SubNeutral Resource
Race), S3 (Defensive Hold) run with PINNED RNG seed
(`DirectorTunables.BatterySeed`) for FIXED duration (5 min each suggested,
tunable as `smart.director.battery_sec`). Battery score = composite of (a)
outcome rates from §4 constraint table and (b) pathology rates from §9 guard
buckets, with explicit weights in `LearningTuning.BatteryScoreWeights` (also
new, ~10 LOC float[]).

**Stage-2 default-weights pre-commitment**. The ratchet rule
`candidate ≥ incumbent − 0.02` is meaningless until the weighting vector is fixed.
Pre-implementation defaults — explicit, ship-with-the-feature:
KillScored = +1.0, BlockDelivered = +1.0, BaseHeld = +1.0, AllyProtected = +1.0,
BaseLost = -1.0, GuardViolation_Movement = -0.5, GuardViolation_Role = -0.5,
GuardViolation_Combat = -0.5. BPTT cannot be enabled while weights are unfixed —
a fast-follow that ships with weights = {1, 1, 1, 1} is the same as no ratchet
at all.

**Ratchet rule**:

  * T3 named-checkpoint promotion: requires
    `battery_score(candidate) >= battery_score(incumbent) - epsilon`
    where `epsilon = DirectorTunables.RatchetTolerance` (default 0.02).
  * T2 disk auto-checkpoint (30-min cadence per §7.4): same gate.
  * T1 in-memory ring: EXEMPT — recency-based promotion preserved for fast
    revert.

**Backlog gate**. See §14 — the ratchet is a PRECONDITION for enabling GRU
BPTT and unsupervised training of the 3 dormant models. This dependency must
be honored — enabling BPTT before the ratchet ships re-opens the slow-drift
self-promotion footgun.

**Fabrication check**. `DirectorTunables.BatterySeed`,
`DirectorTunables.RatchetTolerance`, `LearningTuning.BatteryScoreWeights`,
and `smart.director.battery_sec` are NEW fields to be added when this
fast-follow ships, not existing.

---

## 8. Scenarios

| ID | Name | Composition | Spawn mechanism | Chunk regen? | Trains | Viable |
|---|---|---|---|---|---|---|
| **S1** | Open Brawl | 4 hostile teams; per team 3 mobile Assault techs. Faction = `FactionSubTypes.NULL` (engine picks; avoids sparse-faction-tier empty slots — see §13). `RawTechPopParams{Terrain=Land, Purpose=AnyNonHQ, Progression=FactionLevel.VEN, MaxPrice=12000, grade=3, ExcludeErad=true}` (MAJOR-fix: `Progression=Mid` was fictional — FactionLevel enum at `FactionLevel.cs:12-23` has only NULL/GSO/GC/VEN/HE/BF/EXP/ALL/MOD; VEN = mid-tier). 4-corner ring 200 m around centroid, jittered. All `(i,j) i≠j → Enemy` | Fresh hostile ids via `AIGlobals.GetRandomEnemyBaseTeam(forceNew=true)` (verified `AIGlobals.cs:905`). 4 × 3 SpawnRequest envelopes onto main-thread queue; tagged via `DirectorState.ScenarioOwnedTechs` ConcurrentDictionary keyed by `(int techId, ScenarioOwnership)` stamped at executor return on main thread (MAJOR-fix: Tank.name tagging via `§DIR-S1-<n>` is unsafe because Tank.OnSpawnSetup may regenerate Tank.name from AssetID/template; ownership map by techId survives engine rename). **RM-2 doc note: if Tank.name tagging is retained as a debug aid alongside the techId map, the tag MUST mutate `TechData.Name` BEFORE `RawTechLoader.SpawnMobileTechPrefab` invokes `ManSpawn.SpawnTech_SetNameAndCreator` (which copies `TechData.Name` into `tank.name`). Post-spawn `tank.SetName(tank.name + " §DIR-Sx-n")` is FORBIDDEN — would be stripped by `NetTech.OnServerSetName → RestoreTechName` in any rename event (NetTech.cs:425-432 is `[Server]`-only; host-baked TechData.Name survives, the tank.SetName shortcut does not).** Maintenance top-up: every 5 s if team count < 2, request one replacement; predicate re-evaluated AT MAIN-THREAD DRAIN time (NOT enqueue time) to avoid TOCTOU double-spawn vs ManPop | No | Threat, ActionValue, Intent, Residual, Hunter, Sniper | Yes — engine-default behaviour |
| **S2** | SubNeutral Resource Race | 4 SubNeutral teams (BeSubNeutral suppression verified `Enemy/RCore.cs:802-808`). Per team: 1 Harvesting base + 2-3 Prospector mobile techs (AIType-Prospector — classified as SmartIdentity.Gatherer). Shared chunk pool at centroid. **Anti-degradation (BLOCKER-fix: main-thread marshaling)**: ScenarioWorker is a planner only — it enqueues `ScenarioRelationsRequest { teamA, teamB, relation }` and `ScenarioAngerResetRequest { teamId }` envelopes onto WorldEventBus.PublishFromWorker; main-thread executor in SmartForm.Operations calls `ManBaseTeams.SetRelations(a, b, SubNeutral)` AND zeroes `EnemyTeamData.angerThreshold` (verified accumulation path: `EnemyMind.cs:212-237` → `ManBaseTeams.DegradeRelations_Internal` accumulates to 2500, `AIGlobals.cs:427`). **Direct background SetRelations is unsafe**: ManBaseTeams.align is a plain non-concurrent Dictionary (line 175); SetRelations cascade mutates align + fires TeamAlignmentDeltaEvent + calls ManEnemyWorld.UpdateTeam (line 441-448), all main-thread. Also: SetRelations cascade can hit `AIGlobals.PopupSubNeutralInfo` (lines 397-399, debug flag) which is a Unity UI call. Per-tick anchor gated on `if (current_relation != SubNeutral || angerThreshold > 0)` to avoid thrash. **Team-ID recycling guard**: ScenarioWorker subscribes to `ManBaseTeams.TeamRemovedEvent` (line 753); on fire drops dead team IDs from `DirectorState.ScenarioOwnedTeams` so reclaimed IDs aren't pacified. Otherwise long sessions silently degrade into a 4-way brawl | 4× `GetRandomSubNeutralBaseTeam(forceNew=true)` (verified `AIGlobals.cs:916`). 4 base spawn requests via `FORCESpawnBaseAtPositionNoFounder(FactionSubTypes, pos, team, BasePurpose.Harvesting, grade=2)` (signature verified `RawTechLoader.cs:219` — params are `(FactionSubTypes FTE, Vector3 pos, int Team, BasePurpose purpose, int grade=99)`; **no MaxGrade, no ForceAnchor on mobile**). 8-12 mobile spawn requests. ChunkRegenWorker activated by stamping `ChunkFieldDef` | **Yes (REQUIRED)** | Gatherer (BlockDelivered via new publisher), Intent (low-aggression class disambiguation), Residual (low-speed cargo dynamics), ActionValue (delivery reward) | Yes — conditional on anti-degradation reset path |
| **S3** | Defensive Hold | 1 player-allied team: 1 Defense base (grade=3) at centroid + 3 escorts (free-roaming — `RawTechLoader.cs:1087` unconditionally wipes `filter.ForceAnchor` for mobile spawns, so "anchored escort" is not composable through `SpawnMobileTechPrefab`). 3 hostile attacker teams; each sends 3-tech wave every 90 s, staggered 30 s | Initial base + escort spawn requests. 3 wave timers per scenario instance in ScenarioWorker. Each attacker wave bounded ≤ 12 simultaneous via `LiveAttackerCount`. Director-owned tagged `§DIR-S3-WAVE-<n>` | No | Base (BaseHeld+BaseLost via new publisher), RepairSupport (AllyProtected via new publisher; **NOT "Aegis identity" — that's an AIType, not a SmartIdentity**; `AIType.Aegis` ground escorts classify as `RepairSupport` if Shield-bearing else `Patrol` / `Hunter` per `Smart/Identity/SmartIdentityClassifier.cs`), Threat (incoming wave assessment), ActionValue (defence reward) | Yes |
| **S4** | Air Sortie | 1 player-allied team: 1 Land Defense base (grade=3) + 3 Aviator escorts via `RawTechPopParams{Terrain=Air, Purpose=AnyNonHQ, grade=3}`, spawned at `RawTechOffset.OffGround60Meters` (= ground+60m per RawTechLoader.cs:2199-2200). 1 hostile team: 4 Air attackers every 120 s. **Attacker altitude (MAJOR-fix)**: 400 m altitude / 600 m horizontal achieved via `RawTechOffset.Exact` with caller-computed `pos.y = ManWorld.inst.ProjectToGround(pos).y + 400` (NOT OffGround60Meters which caps at +60m). | Pre-scenario gate: if `RawTechLoader.MatchCount(Terrain=Air, Purpose=AnyNonHQ) < 6` (NEW accessor — see §11) → degrade to S1 with digest warning | No | AircraftHunter (KillScored), AircraftSupport (AllyProtected), Intent on 3D opponents, Residual on aerial lead dynamics | Yes |
| **S5** | Mining + Defend | 1 player-allied **AITeammate** team (decision: see below) — 1 Harvesting base + 2 Prospector gatherers + 2 ground escorts (free-roaming). 1 hostile non-SubNeutral incursion every 75-90 s. ChunkRegenWorker maintains 40-60 chunks. **Player-team relationship decision**: `SubNeutral` and `AITeammate` are different `TeamRelations` (`AIGlobals.cs:916` mints SubNeutral; `ManBaseTeams.cs:845-867 GetTeamAIBaseTeam` mints AITeammate — input param MUST be the human player team `ManPlayer.inst.PlayerTeam`, otherwise line 856-857 FatalError). The plan picks **AITeammate** so the gathering team aligns with the player; this drops the "SubNeutral peace-with-neighbours" framing for S5. **Init order gate (BLOCKER-fix)**: scenario start gated on `SmartRuntime.IsHost && SmartRuntime.WorldLoaded && ManPlayer.inst != null && AIGlobals.IsPlayerTeam(ManPlayer.inst.PlayerTeam)`. The `GetTeamAIBaseTeam(ManPlayer.inst.PlayerTeam)` call is marshaled to main thread (ManPlayer.inst access is Singleton.Manager-bound, not thread-safe from ScenarioWorker background). | 1 AITeammate id + 1 hostile id via `GetRandomEnemyBaseTeam(forceNew=true)`. Base + ChunkRegen init concurrent with first hostile wave at t=30 s | **Yes (REQUIRED)** | Gatherer (BlockDelivered), RepairSupport (AllyProtected; ground escorts), Base (BaseHeld+BaseLost), Mixed plan transitions (defend↔gather), ActionValue (multi-objective tradeoff) | Yes |
| **S6′** | Patrol Stress (replaces S6 Convoy Escort) | 1 player Defense base (stationary) at centroid + 4 Patrol-identity techs (solo armed Land techs classified as `Patrol` per `Smart/Identity/PatrolGoalSource.cs`) ringed at 100 m + 2 RepairSupport defenders. 2 hostile teams send 2-tech `NotStationary` harassment waves alternately every 60 s from opposite bearings | 3 team ids (1 allied + 2 hostile), 1 base, 8 mobile spawn requests + 2 wave timers offset 30 s. Patrol techs use `PatrolGoalSource` for ring patrol | No | Patrol (intercept), RepairSupport (perimeter defence), Threat (multi-bearing engagement), ActionValue (alternating-vector defence), Intent on opponents approaching from different bearings | Yes |

### 8.1 Why S6′ replaces S6 Convoy Escort

`BasePurpose.NotStationary` **does exist** and the spawner branches on it
(`RawTechLoader.cs:228` — `haveBB = (Harvesting || TechProduction) && !NotStationary`).
What does not exist is a **waypoint authoring API** that drives a mobile HQ across a
multi-segment route. Without a movement-goal channel for HQs, a "convoy escort" cannot
be composed from verified primitives. S6′ preserves the training intent
(escort/patrol intercepting attackers along predictable axes) using the verified
`PatrolGoalSource`. If a movement-goal API is identified in a future phase, restore
the original S6 as an additional scenario.

### 8.2 Chunk types (verified)

`Enemy/RLoadedBases.cs:1851-1871 TryGetBiomeResource` returns:

  * Grassland → `EruditeShard`
  * Desert → `OleiteJelly×3 + IgniteShard`
  * Mountains → `RoditeOre`
  * SaltFlats / Ice → `CarbiteOre×3 + CelestiteShard`
  * Pillars → `RubberJelly`
  * default → `PlumbiteOre + TitaniteOre`

`ChunkTypes.Wood` is **NOT** in this map. The plan removes Wood from the
ChunkRegenWorker whitelist; fallback when biome lookup fails is the engine's own
default `{PlumbiteOre 0.5, TitaniteOre 0.5}`. At scenario init, ScenarioWorker calls
`TryGetBiomeResource(centroid)` and uses whatever the engine returns for the actual
biome.

---

## 9. BehaviorGuards (per-tech pathology detection)

### 9.1 Principle

Guards detect **provable pathology**, not heuristic deviation. A behavior is
pathological **iff ALL of**:

  1. **Persistent** (rolling window ≥ 30 s — except `GroundedAircraft` safety, see §9.5)
  2. **Non-progressing** (no positive outcome event in window: no `KillScored`,
     `BlockDelivered`, `BaseHeld`, `AllyProtected`, no `DamageObserved` with target≠self)
  3. **Identity-inappropriate** (gated on `SmartIdentity` × `AIType` × `ActiveScenario`)
  4. **Self-defeating** (correlates with negative outcomes when sustained)

Brief excursions are **NEVER** flagged. Reverse maneuvers, back-into-cover, drift,
sideways orbits, pause-to-assess are emergent learned Modified-form behaviors
(`combat-ai-fixes-round2`, `turret-duty-cycle-feature`, `enemy-reverse-and-circle-aim`
memories) and remain in the texture. **Severity bias**: 6 observe, 4 warn,
1 soft-correct, 1 hard-correct (9 of 12 emit training-signal only, no behavior
injection).

### 9.2 Guard layer placement

Guards are an L3 instrumentation sibling to the publishers in §10. **Difference**:
publishers emit POSITIVE outcomes from engine events (forward flow:
event → reward magnitude); guards emit NEGATIVE pathology from per-tech state-window
analysis (back-pressure flow: window → violation rate → constraint). Co-locating
adjacent to publishers preserves the L3 grouping; folding INTO publishers was
rejected because the lifecycles differ (publishers are event-driven; guards are
rolling-window evaluators at 1 Hz).

The guard worker is a single canonical daemon (`BehaviorGuardWorker`) registered via
`DaemonWatchdog.RegisterCanonical("GuardWorker", factory) + WorkerHealthMonitor.RegisterCanonical`
— bringing the canonical daemon roster from 17 (post-base-plan-extension state; current
source state is **13** verified at DaemonWatchdog.cs:33-48 post-audit BUG-025/036 +
base plan adds TrainingDirector/DirectorInbox/ScenarioWorker/ChunkRegen = 17) to **18**. The TrainingDirector does NOT call `BehaviorGuardWorker.Tick(deltaSec)`
inline (Validator-3 BLOCKER: inline + daemon were mutually exclusive in the prior draft).
The worker owns its own 1 Hz background thread. Like `ScenarioWorker` and `ChunkRegenWorker`,
the GuardWorker is a **planner** for any path that must read a Unity `Transform` —
direct `tank.boundsCentreWorld` / `rb.velocity.y` reads off-main-thread are NOT permitted.
Position / velocity / forward / altitude inputs for Smart-team techs come from per-tech
`SelfStateProbe.TryRead()` → `SelfProbeSnapshot` (shipped feature-expansion §5.1 —
`Smart/Learning/Features/SelfStateProbe.cs`; lookup via `SmartRuntime.LookupSelfStateProbe(techId)`).
Fields available without marshaling: `PositionWorld`, `VelocityWorld`, `AngularVelocityWorld`,
`ForwardWorld`, `Mass`, `Speed`, `AnchorState`, `WaterHeight`, `BlockCount`, `HpFraction`,
`ProvokedCountdown`, `HasLastAttacker`, `LastAttackerPosWorld`, `LastAttackerSeenMono`
(BUG-010 falling-edge clear lands; latch is reliable). `StrategicStateBuffer.Read(techId)`
returning an immutable `StrategicStateVector` is the other shipped per-tech surface
(StrategicStateExtractor daemon fuses BeliefSnapshot + SelfStateProbe + TerrainMap +
ArmorMap into the vector on 30 Hz background cadence). `BeliefSnapshot.ByTech` remains
the channel for cross-team / opaque-target reads (e.g. FriendlyFireGuard's attacker
drift when attacker is non-Smart).

**Rare main-thread-only reads** (terrain height + a small set of fields not in
SelfProbeSnapshot) are marshaled via a **`GuardEvaluationRequest` round-trip
protocol** (MAJOR-fix — original "+30/+20 LOC" undercount). **Scope reduced
post-feature-expansion**: SelfProbeSnapshot publishes `WaterHeight`,
`ForwardWorld`, `PositionWorld`, `VelocityWorld`, `ProvokedCountdown`,
`HasLastAttacker`, `LastAttackerPosWorld`, `LastAttackerSeenMono` directly —
those fields no longer require marshaling. Marshaling now reserved for: terrain
height (`ManWorld.inst.TileManager.GetTerrainHeightAtPosition`), `AILimitSettings`
flags, `FrustrationMeter` (still not in SelfProbeSnapshot), and any future
fields the snapshot does not cover. Check the SelfStateProbe.Publish field list
at impl time before adding a new GuardEvaluationRequest field type.

  * **API name correction (BLOCKER-fix)**: `ManWorld.inst.TileManager.GetTerrainHeightAtPosition(Vector3 scenePos, out bool onTile, bool forceCalculate = false)` — verified at decompile `TileManager.cs:1461` (NOT `CalcTerrainHeightAtPosition` which does not exist). When `onTile = false`, treat as "cannot evaluate, skip this guard tick".
  * **Sea level API (BLOCKER-fix)**: `Singleton.Manager<ManGameMode>.inst.GetCurrentModeSeaLevel()` (ManGameMode.cs:462) OR `KickStart.WaterHeight` (Modified-tree convention at TerrainQuery.cs:26). NOT `ManWorld.SeaHeight` which does not exist. Plan picks `KickStart.WaterHeight` for consistency with existing TAC_AI water-aware paths. Per-mode constant; cached per-tick in GuardContext, invalidated on `ManGameMode.ModeStartEvent`.
  * **Network gating (MINOR-fix)**: `ManNetwork.IsClient` does not exist. Use `!ManNetwork.IsHost && ManNetwork.IsNetworked` (verified ManNetwork.cs:359,361). Or simpler: GuardWorker is already host-only gated, so per-guard `ManNetwork` checks are defensive only.
  * **TankAIHelper field reads**: `Provoked` and `lastEnemyGet` are now mirrored by `SelfProbeSnapshot.ProvokedCountdown` and `SelfProbeSnapshot.HasLastAttacker` / `LastAttackerSeenMono` (BUG-010 falling-edge clear lands; per FEATURE-EXPANSION §5.1) — guards consume the immutable snapshot, no Volatile.Read needed. `FrustrationMeter` (int, line ~321) remains uncovered by SelfProbeSnapshot — TankAIHelper still gains a NEW `volatile int PublishedFrustrationMeter` companion written from UpdatePhysicsInfo on main thread, read by GuardWorker. `AILimitSettings` reads go through GuardEvaluationRequest marshaling as before.
  * **Round-trip mailbox protocol**: worker writes a `GuardEvaluationRequest { techId, monoTick, requestFields }` envelope; main-thread executor in SmartForm.Operations fills cached terrain-height/pitch/sea-level/published-helper-fields into a result slot keyed by `(techId, monoTick)`; worker's next 1 s tick reads the latest fulfilled result. Stale-result threshold > 1 s → skip guard eval that tick. Per-tick budget for main-thread response fills: 16 techs/frame.

**Revised LOC**: +80 BehaviorGuardWorker.cs (request emit + result-read + staleness gate),
+60 SmartForm.cs (main-thread executor with per-envelope try/catch, debounce, response-fill
budget). Folded into §12 totals.

### 9.3 Guard inventory

11 guards in 3 buckets (was 12 — `OrbitalLockup` dropped per Validator-1
double-jeopardy with `OrbitNoFire` in the Combat bucket; same underlying
behaviour, same population). Each entry: pathology test, sane exceptions,
identity/scenario gates, window, severity.

**Primitive availability note** (Validator-3 BLOCKER fix). Several guard tests
reference `recentSpeedSigned` and `recentSpeedXZ` rings; the Modified-tree
`TankAIHelper` exposes only `recentSpeed` (positive scalar). §12.2 explicitly
adds back `recentSpeedSigned` (per-tick signed projection of `rb.velocity` onto
`tank.transform.forward`) and `recentSpeedXZ` (`Vector3.ProjectOnPlane(rb.velocity,
Vector3.up).magnitude`) as small 4 Hz rings on `TankAIHelper` — same shape as the
already-budgeted `recentSpeedY`. Other primitives — `AirborneIntent`,
`TerrainHeightCache`, `AILimitSettings.HoverHeight`, `AILimitSettings.MaxLeashRange`
— were fictional and are replaced with shipped primitives in this revision (see
GroundedAircraft, OverextendedHunter rows below). `AnchorPoint` is renamed to
`SmartIdentityStamp.SpawnAnchor` (the actual shipped field at
`Smart/Identity/SmartIdentity.cs:35`).

Compose only verified `TankAIHelper` primitives + the explicitly-added rings:
`recentSpeed`, `recentSpeedSigned` (added), `recentSpeedXZ` (added), `recentSpeedY`
(added by base plan), `Provoked`, `AIAlign`, `lastEnemyGet` (gated through
`IsLiveTechTarget` per `popup-storm-NRE-fix` memory), `AILimitSettings`,
`FrustrationMeter`, `AdvanceTimer`, `SettleDown`, `IsTechTippedOver`,
`AuthoredTerrain` (per `authored-driver-intent` memory), `TurretFraction` (per
`turret-duty-cycle-feature` memory), `tank.boundsCentreWorld` (read on main thread
via marshaling or via `BeliefSnapshot.ByTech`).

#### Movement bucket

| Guard | Window | Severity | Pathology test | Identity gate | Scenario gate |
|---|---|---|---|---|---|
| **BackwardsLock** | 45 s | observe | mean(`recentSpeedSigned`) < −1.5 m/s **AND** fraction(`recentSpeedSigned`<0) ≥ 0.85 **AND** net displacement < 8 m **AND** (`lastEnemyGet` null OR dist > 60 m) **AND** no-LOS-to-hostile > 50% of window | Hunter, Gatherer, Patrol (BLOCKER-fix: `Scrapper` dropped — does not exist in `SmartIdentity` enum; AIType-Scrapper techs classify as Hunter. Parenthetical `(excl. when Sniper-classified)` removed as redundant — Sniper is its own distinct identity not in this list. Sniper-standoff substantive exception remains below) | any |
| **WedgedNoProgress** | 30 s | warn | `\|recentSpeedXZ\|` mean < 0.5 m/s **AND** net displacement < 4 m **AND** (`Provoked == 0` throughout window **OR** (`lastEnemyGet` null **AND** no `DamageObserved` against this tech in last 30 s — Validator-1 MINOR fix: Provoked-decay edge case)) **AND** `helper.lastDestinationCore` set + > 12 m away (BLOCKER-fix: was fictional `lastDestination`; TankAIHelper exposes `lastDestinationCore` (line 342 — needs visibility elevated to public OR a new `GuardLastDestination` accessor added) and `lastDestinationOp` (line 337); guard uses Core path since Smart-form techs go through ControlCore) **AND** `helper.FrustrationMeter == 0` (NOT in active unjam — BLOCKER-fix: `AdvanceTimer == 0` was fictional; `FrustrationMeter > 0` is the documented unjam-active gate per TankAIHelper.cs:321 + memory `bgeneral-backoff-unjam-deadlock`) | Hunter, Gatherer, Patrol, AircraftHunter, RepairSupport (BLOCKER-fix: `Prospector` dropped — AIType not SmartIdentity; Prospector techs classify as Gatherer) | any |
| **WaterloggedTech** | 40 s | observe | `AuthoredTerrain != Sea` **AND** `AIType ∉ {Buccaneer, Aviator, Astrotech}` **AND** `SelfProbeSnapshot.PositionWorld.y < SelfProbeSnapshot.WaterHeight − 2.0 m` ≥ 75% ticks (post-feature-expansion: both fields are pre-published main-thread by `SelfStateProbe.Publish` — no marshaling needed; skip the guard tick if `SelfProbeSnapshot.WaterHeight == -9001f` (KickStart.WaterHeight fallback when no WaterMod). BLOCKER-fix: `ManWorld.SeaHeight` was fictional; `KickStart.WaterHeight` per Modified-tree convention) **AND** mean(`recentSpeedXZ`) < 1.0 m/s **AND** net displacement < `0.5 m/s × windowSec` | Hunter, Gatherer, Patrol, RepairSupport (BLOCKER-fix: `Prospector` dropped — AIType not SmartIdentity) | any |

(`OrbitalLockup` row deleted — see Validator-1 finding: structurally subsumed by
`OrbitNoFire` in the Combat bucket; double-counted against population-invariance.)

**Sane exceptions (movement bucket)**: Provoked + lastEnemyGet alive + target in
firing arc (combat reverse-orbit is learned); `IsTechTippedOver` (recovery);
`FrustrationMeter > 0` within 5–10 s (unjam FSM owns it — per `bgeneral-backoff-unjam-deadlock`
memory); `SettleDown` active (parking); `AuthoredTerrain == Sea` (boats);
`AIAlign == Player`; within 15–30 m of `SmartIdentityStamp.SpawnAnchor` or
`helper.lastDestination` (Validator-3 MINOR rename: was "AnchorPoint /
OrderedDestination" — corrected to the shipped fields); techAge < 8–15 s
(spawn settle); `ActiveScenario == None`.

**BackwardsLock specific exceptions** (Validator-1 MAJOR fix — Sniper standoff
maintenance is the EXACT signature `mean(recentSpeedSigned) < −1.5 ∧ frac<0 ≥
0.85 ∧ dist > 60 m` and the existing 'firing arc' clause only suppresses
combat-circle reverse, not range-keeping reverse):

  * `SmartIdentity == Sniper AND lastEnemyGet alive AND dist > MaxCombatRange × 0.8`
    (standoff range maintenance).
  * `TurretFraction ≥ 0.5 AND lastEnemyGet alive within last 10 s` (still fighting,
    target temporarily cycled behind cover).
  * `net displacement ≥ 0.3 × |sum(recentSpeedSigned)| × windowSec` (tech is
    actually traveling — kiting — not wedged-in-reverse; loosens the < 8 m gate
    for genuine evasion).

**WedgedNoProgress specific**: additionally exempts the existing combat-fixes
round-2 wedge tracker's active window (do not double-fire).

**WaterloggedTech specific exceptions** (Validator-1 MAJOR fix — Land hunter
chasing hostile across shallow water is correct behaviour):

  * `Provoked > 0 AND lastEnemyGet alive` (chasing target across water).
  * `net displacement > 0.5 m/s × windowSec` (slow but progressing — wading, not
    stuck — already in pathology test as AND-conjunct above).
  * hover/space classifier matches; just-spawned techs (spawner placement glitch,
    not policy).

#### Vehicle-type bucket (safety)

| Guard | Window | Severity | Pathology test | Identity gate | Scenario gate |
|---|---|---|---|---|---|
| **GroundedAircraft** | **3 s sustained** (1.5 s detect + 1.5 s confirm) | **hard-correct** | `AIType == Aviator` **AND** `altitude_above_terrain` < 2.0 m **AND** `recentSpeedY` mean < **−3.0** m/s **AND** `no throttle input for ≥ 50% of detect window` **AND** `pitch_attitude < −45°` **AND** NOT player-controlled | AircraftHunter, AircraftSupport | S4 (active anywhere with aircraft) |

`altitude_above_terrain` is computed as `SelfProbeSnapshot.PositionWorld.y -
ManWorld.inst.TileManager.GetTerrainHeightAtPosition(snap.PositionWorld, out bool onTile)`
(BLOCKER-fix: `CalcTerrainHeightAtPosition` was fictional — actual API is
`GetTerrainHeightAtPosition` at TileManager.cs:1461). When `onTile = false` (off-map
tile not loaded), guard treats result as noisy and skips evaluation for this tick.
`pitch_attitude` is computed as `Mathf.Asin(Vector3.Dot(snap.ForwardWorld, Vector3.up)) * Mathf.Rad2Deg`
(post-feature-expansion: `ForwardWorld` is published main-thread by `SelfStateProbe.Publish` —
no marshaling for pitch; original "MAJOR-fix" bundling pitch + sea-level into
GuardEvaluationRequest no longer needed). Only the terrain-height query needs
GuardEvaluationRequest marshaling; SelfProbeSnapshot supplies position + forward
without round-trip.

This is the **ONE hard-correct** in the design. Justification: a nosediving plane
destroys itself + corrupts the S4 training signal (false `KillScored` = environmental
death, not opponent kill). Per `duty-cycle-revert-nosedive-fix` memory, the
`OffsetFromGroundA` swap was already shipped; this guard catches the residual
failure cases.

**Release semantics (mandatory anti-stifling discipline)**:

  * Injection (BLOCKER-fix: TacticalGoal has 4 fields only — `{ Vector3 Position;
    float Heading; Vector3 Velocity; float LookAheadSeconds; }` at CostFunction.cs:13-30;
    `kind / targetY / expiresMono / sourceId` are fictional). The mailbox slot is a
    NEW sidecar **class** (R3-B2 fix: MUST be `class` not `struct` — `Interlocked.Exchange<T>` requires `T : class` reference type; struct of ~14 native words cannot be atomically written; alternatives — long-pack, Volatile, Unsafe, cache-line alignment — all fail under .NET 4.6.1):
    `class GuardInjectedGoalSlot { TacticalGoal Goal; long ExpiresMono; byte
    GuardOrdinal; byte Kind; Vector3 ExtraTargetPosition; float ExtraTargetHeading; }`.
    Mailbox becomes `GuardInjectedGoalSlot[]` of reference cells. Writers do
    `Interlocked.Exchange<GuardInjectedGoalSlot>(ref slot, newInstance)`. Eviction-clear
    via `Interlocked.CompareExchange(ref slot, null, expectedStale)` so a fresh write
    that races the clear is preserved. `GuardCorrectiveActuator.RaiseGroundedAircraftRecovery`
    computes the goal Position from a main-thread-published `(currentBoundsCentre,
    currentForward)` snapshot passed in via `GuardWritePayload` envelope (the actuator
    NEVER reads `tank.boundsCentreWorld` itself off main thread — V-fix BLOCKER thread-affinity).
    Writes Goal = `new TacticalGoal(new Vector3(currentX, currentY + 30, currentZ),
    currentHeading, Vector3.up * climbRate, 1f)`, Kind=HoverToAltitude,
    GuardOrdinal=(byte)GuardId.GroundedAircraft, `ExpiresMono = MonoClock.Now() +
    MonoClock.FromSeconds(5.0)` (BLOCKER-fix: `MonoClock.Now()` returns Stopwatch ticks
    NOT milliseconds; literal `+ 5000` expires in microseconds. Use new
    `MonoClock.FromSeconds(double sec) => (long)(sec * Stopwatch.Frequency)` helper
    OR `+ (long)(5.0 * Stopwatch.Frequency)`. Apply consistently — also
    `+ 90 s` anti-loop, `+ 2 s` ramp-down. (`+ 45 s` OverdueDelivery and `+ 15 s`
    injection delay STRUCK per V-fix telemetry-only reconciliation — those
    timers were substitution-architecture mechanisms; the telemetry-only
    direction eliminates them.)
  * **Mailbox per-guard slot keying (BLOCKER-fix)**: `GuardInjectedGoals` is keyed
    by `GuardId` per-tech (a fixed-size array
    `GuardInjectedGoalSlot[HardCorrectGuardCount]` on TankAIHelper, indexed by
    `(byte)GuardOrdinal` restricted to hard-correct ordinals), NOT a single slot
    per tech. **OverdueDelivery does NOT have a mailbox slot** (V-fix
    reconciliation: telemetry-only — see §9.3 OverdueDelivery telemetry-only
    mechanism block). Slots exist ONLY for hard-corrects: AntiCrash and
    GroundedAircraft. Writers write only their own slot via
    `Interlocked.Exchange`; readers iterate slots in §9.7 priority order across
    all populated slots. AntiCrash + GroundedAircraft can coexist on the same
    tech without clobbering. The `GuardOrdinal` enum itself keeps OverdueDelivery
    as a value (used by `IdentityOutcome.Source` high-nibble byte for
    guard-attribution in `IdentityOutcomeConsumer`); only the mailbox ARRAY
    indexer is restricted to hard-correct ordinals. Index mapping is held by a
    static `HardCorrectGuardOrdinals` table on `GuardRegistry` — telemetry-only
    ordinals return -1 (no slot).
  * Release: **HARD 5.0 s wall-clock ceiling** enforced by the actuator on
    `MonoClock.Now()`. No condition can extend it. No state lock. **State machine**:
    Active → RampingDown (5 s window) → Cooldown (15 s) → Idle. Slot sweep wrapped
    in try/finally so an exception during sweep does NOT leave a stale slot
    pointing to a 5-s-old goal — watchdog: ContinuousController consumer-side
    rejects mailbox slots where `MonoClock.Now() > slot.ExpiresMono + 2 s` (grace +
    ramp-down + buffer) and increments `[GUARD-MAILBOX-STALE]` counter.
  * **Ramp-down handoff** (Validator-2 fix — was instant Goal.None cliff): for
    the **2 s following the 5 s ceiling**, the actuator emits a low-priority
    `{ kind=MaintainAltitude, targetY=current, expiresMono = MonoClock.Now() + MonoClock.FromSeconds(2.0) }`
    so the controller has a smooth handoff zone. Decoupled from cooldown. Tunable
    `smart.director.guards.aircraft_release_ramp_sec` (default 2.0 s).
  * Cooldown: 15 s per-tech before re-fire eligible. Pathology test must pass
    again — guards do NOT re-arm on cooldown elapse alone.
  * Anti-loop ceiling: if the guard fires ≥ 3 times within **90 s** (Validator-2
    fix: was 60 s; the predictable 20 s cycle of 5 s correct + 15 s cooldown
    + 0.1 s descent recapture = 60.1 s WAS just outside the prior 60 s
    suppression window, evading detection — widen to 90 s catches the
    oscillation after 2 cycles) for the same tech, SUPPRESS for that tech for
    5 min and emit `[GUARD-AIRCRAFT-CHRONIC]` digest line — the controller is
    broken and hard-correct is making it worse, surface to operator instead of
    looping. Suppression message includes `oscill=N` for the near-suppression-
    threshold cycle count.
  * After release: the controller resumes any plan including descent. No FSM
    transition consumed. **No engine-state revert** required (Validator-3
    BLOCKER fix: prior draft mentioned reverting `AILimitSettings.HoverHeight`,
    a fictional field; the actuator simply clears the goal-mailbox slot — no
    other state held).

**Sane exceptions**:

  * **Strafing/diving on a ground target** (Validator-1 MAJOR — the mode-collapse
    risk): `lastEnemyGet IsLiveTechTarget AND (target.boundsCentreWorld.y −
    terrain_height_at_target_pos) < 5 m AND distance_to_target < 80 m`. The
    tech is actively attacking a ground target; descent is intentional weapons
    delivery. The target-position terrain query goes through the SAME
    GuardEvaluationRequest envelope (V-fix: marshaling discipline). Without this,
    hard-correct interrupts the strafe and teaches the policy to AVOID low strafes
    — mode collapse on a useful combat tactic.
  * `AIAlign == Player`; `tank.IsAnchored`; `!ManNetwork.IsHost && ManNetwork.IsNetworked`
    (host owns physics intent — MINOR-fix: was fictional `ManNetwork.IsClient`);
    within 30 m of `SmartIdentityStamp.SpawnAnchor` AND descending (return-to-base
    landing); tech has no functional thrust (engine destroyed — let it crash,
    cannot recover); techAge < 10 s (spawn drop); `AuthoredTerrain == Land`
    (Aviator AIType misclassified, treat as observe-only — hover-miner pattern
    from `authored-driver-intent` memory).

**Mode-collapse telemetry** (Validator-1 fix): `GuardTelemetry` counts
`hard_correct_on_active_target_count` separately. If non-zero in a digest window,
emit `[GUARD-AIRCRAFT-DIVE-INTERRUPT]:N` digest line as operator-visible
mode-collapse warning even when the chronic ceiling has not tripped.

#### Role-task bucket

| Guard | Window | Severity | Pathology test | Identity gate | Scenario gate |
|---|---|---|---|---|---|
| **IdleGatherer** | 60 s | **observe** (Validator-1 demote: was warn; terrain-isolation false-positive surface — warn-severity inflates `role_task_pathology_rate` for unfixable terrain situations) | `SmartIdentity == Gatherer` **AND** `ModuleItemHolder.IsEmpty` (BLOCKER-fix: was fictional `currentItemCount == 0` — real API is `NumContents` int property + `IsEmpty` bool, verified at decompile ModuleItemHolder.cs:621-625) **AND** mean(`recentSpeedXZ`) < 1.0 m/s **AND** no `BlockDelivered` event in window **AND** **reachable** chunks ≥ 3 via NEW `PathingService.IsReachable(origin, dest, VehicleCapability)` filter (R3-B1 fix — MUST route through `TerrainMap.IsTraversable(worldXZ, VehicleCapability)` (O(1) DoubleBuffer read — Smart/Pathing/TerrainMap.cs, exposed via ITerrainMap.cs:44). Optional A* path goes through existing `PathingService.PathSolveLoop` enqueue with a small result cache. Guard with `TerrainMap.IsFreshlyAllocated` so reachability returns "unknown / assume-reachable" until first refresh. **Do NOT call AIEPathMapper / AIEAutoPather2D / AIEAutoPather3D from background threads**: `AIEPathMapper.tilesMapped` is plain `Dictionary` mutated lazily on every read, and AIEAutoPather scratch lists are private static (NOT `[ThreadStatic]`) — background A* through AIE corrupts main-thread FixedUpdate solver. Was raw `Physics.OverlapSphere(pos, 200 m, Pickup) ≥ 3`; cliffs / water / hostile-cordon-blocked chunks counted falsely) | Gatherer (BLOCKER-fix: `Prospector` dropped — not in SmartIdentity enum; AIType-Prospector classifies as Gatherer) | S2, S5 |
| **OverdueDelivery** | 90 s | **soft-correct (telemetry-only)** | `SmartIdentity == Gatherer` **AND** cargo fill ≥ 75% throughout (via `helper.GetCargoFraction()` summing `ModuleItemHolder.NumContents` across holders) **AND** no `BlockDelivered` event in window **AND** distance to nearest friendly Harvesting base > 50 m **AND** friendly base exists **AND** path-to-base reachable via PathingService.IsReachable **AND** `no net-progress to base across FULL window` defined as `dist_to_base_at_end ≥ dist_to_base_at_start − 15 m` | Gatherer (BLOCKER-fix: `Prospector` dropped) | S2, S5 |
| **Homesickness** | 120 s | observe | `SmartIdentity ∈ {Hunter, AircraftHunter, Patrol, Sniper}` **AND** mean distance to friendly base < 25 m **AND** `lastEnemyGet` null ≥ 80% ticks **AND** `Provoked == 0` throughout **AND** mean(`recentSpeedXZ`) < 1.5 m/s **AND** any hostile alive within 300 m of friendly base detected in last 90 s NOT engaged by this tech (Validator-1: anchors the pathology in "there is a thing to do and this tech isn't doing it") | Hunter, AircraftHunter, Patrol, Sniper | S1, S2, S4 (Validator-1 fix: **S5 dropped** — in S5 a Hunter assigned to base-defense sitting at base IS the role; "Homesickness" framing inverts in defend+gather scenarios) |
| **LostAlly** | 90 s | warn | `SmartIdentity ∈ {RepairSupport, AircraftSupport}` **AND** no friendly Smart-driven tech within proximity_radius (50 m ground / 150 m air) ≥ 80% ticks **AND** pair existed at window start **AND** `now − AllyProtectionPublisher.LastAllyLossMono(supportTechId) > 30 s` (Validator-1 MINOR fix: paired ally died DURING window is not a navigation failure) | RepairSupport, AircraftSupport | S3, S4, S5, S6′ |

**Sane exceptions (role bucket)**: `Provoked > 0` (combat preempts); `IsTechTippedOver`;
`AIAlign == Player`; `tank.IsAnchored` (Base / docked); **PathingService recent-failure
gate** (MAJOR-fix: `AIEPathMapper.LastQueryFailedRecently(techId, 30s)` was a fictional
accessor; add as a NEW method on the same PathingService wrapper — caches last-failed-query
timestamp per (techId, dest-bucket) on a small ring); chunks_in_range < 3 (no work
available — `chunk_field_deficit` catches this elsewhere); within 20 s of last
`BlockDelivered` event (legitimate inter-delivery pause); within 30 m of friendly
base AND `DeliveryDock` interaction in progress (final approach); no friendly base
alive (nothing to return to); `Damageable.Health < 50%` (MINOR-fix: `Damageable.Health`
capitalized; computed property at decompile Damageable.cs:67. Per-tank HP summed via
reused `BaseHeldPublisher.GetTechCurrentHP(techId)` cache — see §10 — NOT a fresh
per-tick block walk); `ActiveScenario == None` or non-relevant scenario;
`PatrolGoalSource` short-leash patrol active (Homesickness only); **PatrolGoalSource
on-station heuristic** (MAJOR-fix: `IsAtRingNode` was fictional; PatrolGoalSource uses
Lissajous meander around SpawnAnchor at Identity/PatrolGoalSource.cs with no ring
nodes. Replacement test: `mean(recentSpeedXZ) > 1.0 m/s in last 30s` = tech is in
motion → on patrol); recently adopted pair within 10 s (LostAlly stabilisation grace).

**OverdueDelivery specific exceptions** (Validator-1 + Validator-2 fixes —
prevents flag on the polished round-trip cycle):

  * Heading + speed convergence: `current heading is within 30° of bearing-to-
    nearest-base AND mean(recentSpeedXZ) > 2 m/s` (tech is actively traveling
    toward base).
  * Velocity-vector convergence (Validator-2): `velocity vector projected onto
    base-direction > 2.0 m/s for ≥ 50% of last 15 s` (Gatherer is converging on
    a base in the final approach).
  * Displacement-toward-base in window > 30 m (objectively closing).

**Homesickness specific exceptions** (Validator-1 fixes):

  * `no hostile within 400 m of friendly base in last 60 s` (nothing to hunt —
    staying near base is fine).
  * `tech.lastDestination is friendly-base OR null` (no order pulling tech
    away).

(Plan does NOT rename Homesickness to `HomesicknessUnderHostileSighting`; the
hostile-anchored condition is folded into the pathology test instead.)

**OverdueDelivery telemetry-only mechanism** (V-fix BLOCKER reconciliation —
single soft-correct in the design; the substitution gate's preconditions
`TacticalGoal.None` sentinel + "controller silent" state do not exist in the
codebase, so substitution is structurally infeasible. Telemetry-only is the
binding direction per §9.7 step 5):

  * **Mechanism**: telemetry-only. When the 90s pathology window triggers,
    publish `IdentityOutcome(techId, Gatherer,
    GuardViolation_OverdueDelivery, magnitude = clamp(overdue_duration_sec /
    90, 0, 1))`. NO mailbox slot is written. The controller is never
    influenced. `GuardCorrectiveActuator` does NOT handle OverdueDelivery —
    the actuator's RaiseX surface covers hard-corrects only (AntiCrash,
    GroundedAircraft). Director's `role_task_pathology_rate` consumes the
    IdentityOutcome rate; sustained elevation drives the existing
    temperature_adjust / replay_bank actions as documented in §4.
  * **Kinematic correlation tracker** (separate from control, training-signal
    only): observe the tech's displacement-toward-base over the 30s window
    after each pathology-window trigger and record as a SEPARATE
    `IdentityOutcome` magnitude scaling. This does NOT influence the
    controller; it only records whether the model's policy convergence
    correlates with what the guard would have requested. Friendly base lookup
    via extended `TeamRuntime.HarvestingBases` accessor (the accessor still
    exists and is reused for the kinematic tracker's base-direction query).
    Main-thread `(basePos - techPos)` computation marshaled via
    `GuardEvaluationRequest` envelope as for other guards (V-fix
    thread-affinity).
  * **No mailbox surface**: `TankAIHelper.GuardInjectedGoals[]` carries slots
    ONLY for hard-corrects (AntiCrash, GroundedAircraft). The GuardOrdinal
    enum keeps OverdueDelivery as a value (used by `IdentityOutcome.Source`
    high-nibble for guard attribution in `IdentityOutcomeConsumer`), but the
    array indexer skips it — there is no slot to write, evict, or read.
  * **No injection delay, no Q-margin override, no Provoked-polled eviction,
    no 60s/120s cooldowns** — those were substitution-architecture
    mechanisms that the telemetry-only direction eliminates. The pathology
    window itself + a small (per-tech) re-fire cooldown to prevent
    duplicate `IdentityOutcome` storms per single overdue episode is all
    that's needed (default ~30 s, tunable
    `smart.director.guards.overdue_delivery_refire_cooldown_sec`).
  * **Reachability filter preserved**: R3-B1 `PathingService.IsReachable`
    via `TerrainMap.IsTraversable` O(1) DoubleBuffer read remains as a
    pathology-test AND-conjunct — a Gatherer that cannot reach base
    should not trigger the pathology (terrain-isolated, not behaviorally
    overdue).
  * **`GuardCorrectionInjected` outcome is NOT emitted** for OverdueDelivery.
    Telemetry-only guards never "win arbitration" because they never enter
    arbitration. The kinematic correlation tracker emits a distinct
    correlation-magnitude scaling on the existing `IdentityOutcome` instead.

#### Combat bucket

| Guard | Window | Severity | Pathology test | Identity gate | Scenario gate |
|---|---|---|---|---|---|
| **OrbitNoFire** | 60 s | warn | `Provoked > 0` **AND** `lastEnemyGet IsLiveTechTarget` **AND** distance ∈ [`MinCombatRangeDefault`, `MaxCombatRange`] **AND** kinematic orbit (BLOCKER-fix: `PlanKind.CombatCircle` was fictional; PlanLibrary.PlanType has 10 plans, no CombatCircle. Also StrategicState.CurrentPlan is team-scope not per-tech. Replacement test: `angular displacement around (lastEnemyGet.position) ≥ 270° in window AND linear speed > 5 m/s`) **AND** **target in LOS AND in weapon firing arc for ≥ 30% of window AND no weapon in cooldown for ≥ 30% of window** **AND** `TurretFraction ≥ 0.25` (has weapons) **AND** projectiles_fired == 0 via NEW `ProjectileFiredPublisher` (BLOCKER-fix: `FireControl.ProjectilesFiredCount` was fictional; FireControl is a bool on TankControl.cs:393 / ModuleWeapon.cs:95 with no counter. New publisher subscribes to `TankControl.manualAimFireEvent` (line 1645) + targeted-aim fire events in ModuleWeapon hooks; maintains per-tech rolling fire-edge counter. ~80 LOC new publisher in §10) | Hunter, Sniper, AircraftHunter, Patrol | S1, S3, S4, S5, S6′ |
| **FriendlyFire** | 60 s | warn | count(`DamageObserved` where attacker.team == victim.team AND attacker == self AND magnitude > **15** HP) ≥ 3 OR cumulative friendly-damage > **150** HP in window. **Attribution gate (BLOCKER-fix)**: `SanitizedDamageInfo` (EventBus.cs:34-49) carries only Damage/ImpactPos/ImpactDir/AttackerIfKnown — NO attacker-aim-time-stamp. The original dodge-exclusion is structurally unimplementable. Replacement: stale-aim heuristic — look at attacker's `tank.boundsCentreWorldNoCheck` displacement in last 1 s via `BeliefSnapshot.ByTech`; if attacker drifted > 10 m (lost aim), EXCLUDE the damage event as 'lost-aim collateral'. Beam weapons during target reacquisition are EXCEPTED. Per-tech projectile attribution comes from the new `ProjectileFiredPublisher` (above), NOT a fictional `FireControl.ProjectilesFiredCount` | Hunter, Sniper, AircraftHunter, Patrol, RepairSupport | any combat |
| **OverextendedHunter** | 75 s | observe | `SmartIdentity == Hunter` **AND** mean distance to nearest friendly > `smart.director.guard.overextended_hunter.leash_m` tunable (default 250 m) **AND** no friendly within 100 m ≥ 80% ticks **AND** mean distance to nearest hostile < 60 m **AND** HP dropped > 25% of max in window (HP from reused `BaseHeldPublisher.GetTechCurrentHP/MaxHP` cache to avoid per-tick block walk; baseline at window-start, block detach mid-window aborts the guard window for this tech) | Hunter (BLOCKER-fix: `Scrapper` dropped — AIType not SmartIdentity; AIType-Scrapper techs classify as Hunter; Patrol already dropped per Validator-1) | S3, S4, S5, S6′ |

**Sane exceptions (combat bucket)**: weapons in cooldown / reload / overheat /
no-ammo ≥ 50% window; `FireControl.HoldFire` mode (FireControl is the existing bool
on TankControl/ModuleWeapon — distinct from the fictional `FireControl` class);
target in friendly-fire-mask exclusion (the suppression IS correct);
`TurretFraction < 0.25` (Forwards-classifier dominant — different control surface,
OrbitNoFire scoped to turret-fraction high); **snipe-only weapon cycle**
(Validator-1 fix): `tech has weapon(s) with cycle ≥ 30 s AND no weapon with cycle
< 10 s` — legitimate not-firing over a 60 s window; target is FlyingHigh AND
attacker has no AA weapons; splash damage with primary target hostile within
splash radius (legitimate collateral); damage source is collision not weapon;
beam-fixer overshoot (per `unjam-beam-loop-fix` memory — already handled); within
15 s of last `KillScored` (legitimate post-kill pursuit for OverextendedHunter);
`ActiveScenario == S1 Open Brawl` (overextension is the scenario for
OverextendedHunter); `AIAlign == Player`; **`DirectiveTable` operator
force-aggression tag** (MINOR-fix: `AILimitSettings.LoneWolf` was fictional — no
LoneWolf flag exists on AILimitSettings; verified members at TankAIHelper.cs:191-204
are AdvancedAI/AllMT/FullMelee/SideToThreat/AutoRepair/UseInventory. Use the
DirectiveTable opt-out tag exclusively — operator can express "this tech is solo"
via a directive); tech has solo capability AND HP > 80%.

### 9.4 Rejected guards (anti-stifling discipline)

Guards proposed by the design pass and explicitly REJECTED, with rationale:

  * **StuckOnObstacle** — folded into `WedgedNoProgress`. Separate guard would
    triple-count via the existing wedge tracker + unjam FSM + this guard.
  * **ExcessiveBoosting** — boost-spam is known emergent texture (`organic-vs-bug-design-value`
    memory). Often optimal evasion. Adding a guard would punish the very behaviors
    that work.
  * **RetreatSpiral** — retreat-attack cycling is deliberate Modified-form combat
    behavior. `combat-ai-fixes-round2` + `enemy-obstacle-avoidance-fix` memories
    specifically PRESERVE this. Guard would erode the form's identity.
  * **TargetSwitching** — correct opportunistic behavior in multi-enemy scenarios.
    Intent classifier's entropy reservoir already captures pathological switching
    as collapse.
  * **TurretMisaim / TurretJitter** — the `turret-aim-fix` `StaticTurner+deadband`
    fix already addresses this in-controller. Re-detecting at the guard layer would
    only fire on regression (regression-test job, not runtime guard). `guards-from-turret-chase`
    memory explicitly warns against duplicating turret guards from old failed sweeps.
  * **OvershootApproach** — overshoot-then-correct is legitimate Modified-form
    combat behavior per `enemy-reverse-and-circle-aim` memory (hold dead-band tuning).
    A guard would fight a recently-shipped fix.
  * **GoalThrash / PlanTransitionThrashing** — goal-source rotation under shifting
    Provoked / lastEnemyGet / unjam state is EXPECTED (per `authored-driver-intent`
    priority chain), not pathological. Pathological case structurally equivalent to
    WedgedNoProgress.
  * **BaseAbandoned** — `SmartIdentity.Base` is anchored by definition; if it moved,
    upstream form-selector is broken — handled by `AuthoredTerrain` BaseTerrain hint,
    not runtime guard.
  * **DamageSeekingBehavior** — covered by `OverextendedHunter`; standalone version
    would flag legitimate tanky-defender behavior.
  * **RamAttempt** — per `combat-understeer-beeline-fix` memory, the SettleDown
    regression that caused rams is already fixed. Residual environmental bumps
    would dominate false-positive rate.
  * **PassiveAegis / IdleSupport** — RepairSupport with no repair events might be
    (a) no damaged allies (correct idle), (b) bad positioning. `LostAlly` already
    covers (b); separate guard creates OR-condition false positives.
  * **LowFireRate / NoFireSpree** (Validator-2 INFO) — TurretFraction-low techs
    (Forwards-classifier dominant per `turret-duty-cycle-feature` memory)
    legitimately fire infrequently; OrbitNoFire already gates on TurretFraction
    ≥ 0.25 for high-turret techs. A low-turret variant would punish low-turret
    bruisers.
  * **OrbitalLockup** (newly REJECTED post-Validator-1 — was in v1) —
    structurally subsumed by `OrbitNoFire` in the Combat bucket. Same underlying
    behaviour (circle-plan + no projectiles fired); both guards measured the same
    population and double-counted against `*_pathology_rate` constraints.
    `OrbitNoFire` (now tightened with both-LOS-and-firing-arc + no-weapon-cooldown
    requirements) carries the load alone. Anti-stifling principle §9.4 "would
    triple-count via existing wedge tracker + unjam FSM + this guard" applies
    here too.

### 9.5 Window discipline

ALL guards use ≥ 30 s rolling windows **except `GroundedAircraft`** (3 s combined
detect+confirm). The sub-30 s window is justified ONLY by the catastrophic-safety
asymmetry: a crashed plane costs the entire training sample (destroyed tech +
all in-flight events lost + scenario warmup re-runs) versus a bounded 5 s ascent
assist. Per the principle, this is the **sole** exception.

Brief excursions are NEVER flagged. The principle is honored by storing pathology
as a **fraction of window samples**, never as a single-tick predicate.

### 9.6 Anti-stifling discipline (applied to every guard)

  * **Severity floor + ceiling**: 5 observe + 4 warn + 1 soft-correct + 1
    hard-correct (was 6/4/1/1 — `OrbitalLockup` dropped, `IdleGatherer` demoted
    from warn to observe per Validator-1). 10 of 11 emit training-signal only
    (no behavior injection). The ratio enforces "guards detect provable
    pathology not heuristic deviation" **structurally** — even a buggy
    soft-correct cannot gridlock the system because soft-correct is rare by
    design.
  * **Guards are observer-only at the reward channel** (Validator-2 MAJOR fix —
    fifth bullet added). `OutcomeWeights[GuardViolation_Movement] =
    OutcomeWeights[GuardViolation_Role] = OutcomeWeights[GuardViolation_Combat]
    = 0.0` by default and the trainer reward path in `LearningTuning` explicitly
    ignores `GuardViolation_*` `OutcomeKind`s when computing reward. Guards are
    CONSTRAINT-ENGINE inputs only, NOT reward channels. Without this invariant
    the trainer learns to optimise against guard suppression — which is exactly
    the heuristic-deviation training the principle forbids. The invariant is
    enforced in `IdentityOutcomeConsumer` (filter applied during reward
    aggregation) and exposed as `OutcomeWeights.GuardChannelMuted` for
    operator-visible audit.
  * **Hard-correct release is unconditional + telemetered**: GroundedAircraft's
    5.0 s ceiling is absolute (no condition can extend it). 15 s per-tech
    cooldown + 5-min suppression after 3 fires in **90 s** (Validator-2 fix:
    was 60 s; the 20 s evasion cycle now caught after 2 cycles). `GuardTelemetry`
    surfaces `oscill` count + `hard_correct_on_active_target_count` in the
    digest. The release-within-deadline assertion is a unit-test surface.
  * **R3-B3 MonoClock pause behavior (doc note)**: anti-loop windows and post-fire
    observation periods are stamped in MonoClock ticks (`Stopwatch.GetTimestamp`,
    NOT Unity-time-paused). Daemons gate on `SmartRuntime.IsPaused` so no fires
    occur during pause (verified at `GlobalCoordinatorDaemon.cs:27-31`). On long-pause
    resume, the first post-pause fire counts against an empty window (one extra fire
    allowed) — **this is by design**: pause-resume is rare and the lenient-on-resume
    failure mode is harmless.
  * **Learned-model override telemetry**: when the learned ActionValueEstimator's
    preferred Q-action diverges from the injected guard goal past
    `smart.director.guard_override_qmargin` (default 0.05) AND the Q-action
    points AWAY from the guard direction (Validator-2 fix: signed Q-gap, not
    absolute), the controller FOLLOWS the model and emits
    `guard_overridden_by_model`. This is a **feature** not a bug — guards are
    LOW-priority hints; a confident model should win. **Threshold for digest
    surfacing** (Validator-1 INFO fix): if
    `learned_model_overrode_guard_count(guard) / fired_count(guard) > 0.40`
    over the last 10 min AND `fired_count > 5`, the digest emits
    `[GUARD-MAYBE-FALSE-POSITIVE]:<guardName>:override_rate=N%` automatically,
    letting the operator see candidate-buggy guards without scanning all
    counters.
  * **Sane-exceptions are wide and identity-aware**: every guard has ≥ 4 exception
    conditions; the wide ones (BackwardsLock) have 7. Modified-form's nuanced
    behaviors (short reverses, drift, pause-to-assess, beam-fix beam-loops) are
    each named in at least one exception list — fix memories
    (`combat-understeer-beeline-fix`, `enemy-obstacle-avoidance-fix`,
    `unjam-beam-loop-fix`, `enemy-reverse-and-circle-aim`) explicitly inform the
    exceptions so guards do NOT undo previously-shipped fixes.
  * **Director's 5-min post-remediation observation window applies to guard
    constraints** identically to the rest of §4. When a guard rate constraint
    trips and the Director fires a verb, the constraint enters observation for
    5 minutes before re-evaluation.
  * **Synthetic-pathology selftest** (amendment A3). Under-firing is
    unmodelable from guard-side telemetry alone. Add a DevCommand-invoked
    selftest suite that INJECTS each pathology and asserts the corresponding
    guard fires within a bounded window. One assertion per guard, host-only,
    run via `smart.director.guards.selftest` (added to §12.1 console commands
    row). Scope: test scaffolding only, reusing `ScenarioWorker` spawn plumbing.
    Per-guard injection recipes (one assertion each):
    * **BackwardsLock**: force reverse drive on a Hunter spawn for > 5 s;
      assert fire.
    * **WedgedNoProgress**: spawn a tech against a wedge geometry with
      net-progress = 0 for window; assert fire.
    * **GroundedAircraft**: spawn an Aviator on flat ground with thrust
      available but pinned-down for the safety-band; assert fire.
    * **OverdueDelivery**: spawn a Gatherer at ≥ 75 % cargo > 50 m from base
      with reachable path; assert `IdentityOutcome(GuardViolation_OverdueDelivery)`
      publishes after the 90 s pathology window (telemetry-only — no mailbox
      injection asserted; selftest only verifies the IdentityOutcome emission
      per V-fix reconciliation).
    * **OrbitNoFire**: spawn a weapons-hot tech orbiting an enemy with
      HoldFire off; assert fire within window.
    * **(extend per remaining guard)** — follow the same pattern: induce the
      pathology in a controlled mini-scenario, assert the named guard fires
      within its window. One test per guard, ~30-50 LOC each.
    **Total LOC**: ~400 (test scaffolding + ~30-50 LOC × 11 guards). Add to
    §12.4 / §12.3 totals as test-only LOC (separate column / footnote).

### 9.7 Soft-correct goal-source priority (consumer-side substitution)

**Architectural correction (Validator-3 BLOCKER fix).** The Modified-tree's
shipped goal-source surface is `ISmartGoalSource` registered in a flat
`Dictionary<SmartIdentity, ISmartGoalSource>` (`Smart/Identity/SmartIdentity.cs:104-119`)
— exactly ONE source per identity, no priority field on `TacticalGoal`
(`Smart/Control/CostFunction.cs:13-30`), no `Goal.Priority` enum, no native
priority chain. The prior draft's 8-level `AntiCrash > form-selector > anchor >
authored-intent > Combat > unjam > Hint > default-heuristic` chain was fictional.

Guards integrate via **consumer-side substitution**: `ContinuousController.OnOperationsTick`
is modified (§12.2 row) to insert the AntiCrash mailbox check BEFORE the existing
external-goal short-circuit at line 208-217, then apply remaining arbitration AFTER
calling `SmartIdentityRegistry.For(identity).Produce(...)`. The full priority chain:

  0. **AntiCrash PREEMPTS COORDINATOR** (BLOCKER-fix: previously buried at priority 1
     in identity-fallback branch which `_readExternalGoal != null` skipped at line
     211). Check `GuardInjectedGoals[GuardId.GroundedAircraft]` BEFORE
     `external = _readExternalGoal?.Invoke()`. If present and not expired, substitute
     UNCONDITIONALLY — safety > coordination. Set
     `_lastTickHadExternalGoal = (external != null)` so next non-AntiCrash tick does
     not trigger spurious Adam-reseed transition.
  1. **Coordinator external goal** (existing `_readExternalGoal?.Invoke()` at line
     208-217) wins when AntiCrash slot is empty.
  2. Controller's `ISmartGoalSource.Produce` output (Modified-form heuristic /
     learned policy) — anchor / authored-intent / form-selector signals are all
     already folded into the single returned `TacticalGoal`.
  3. **Combat** (`helper.Provoked > 0 ∧ IsLiveTechTarget(helper.lastEnemyGet)`)
     — combat condition is detected by the controller, not by guards; guards do
     NOT substitute when combat is active EXCEPT when
     `helper.FrustrationMeter >= AIGlobals.UnjamUpdateStart (= 120)` AND `no progress in 8 s`
     (BLOCKER-fix: `0.5 × saturation` was ambiguous — FrustrationMeter has 4 named
     thresholds at AIGlobals.cs:297-304 (UnjamUpdateFire=25, UnjamUpdateStart=120,
     UnjamUpdateDrop=120+ticks, UnjamUpdateEnd=Drop+20). Plan pins the CombatStuck
     branch to UnjamUpdateStart per the unjam FSM "we want to unjam now" gate per
     ModifiedUnjam.cs:65).
  4. **Unjam FSM** (`helper.FrustrationMeter > 0`) — preempts guard substitution.
  5. **GuardSoft TELEMETRY-ONLY tiebreaker — REWRITTEN** (BLOCKER-fix: cannot
     "substitute when controller returned TacticalGoal.None" — `TacticalGoal.None`
     does not exist; CostFunction.cs:13-30 defines 4-field readonly struct; Generic
     identity always produces a goal). Resolution per Reviewer-9 §9.3 reading: the
     OverdueDelivery mailbox slot is **TELEMETRY HINT**, never substitutes for the
     controller's chosen goal. Detection of arbitration "win" is kinematic — over
     the 30 s following the mailbox write, the actuator observes whether `tank`
     actually moved toward `nearestFriendlyHarvestingBase` (displacement-toward-base
     > 5 m). If yes, emit `GuardCorrectionInjected`; if no, the goal is silently
     consumed. This is structurally anti-stifling: guards never win on their own,
     they record influence when the policy happens to converge. Eliminates the
     dead-code §9.7-step-5 path AND the Q-margin-uncertain-loses-to-guard contradiction
     between §9.3 and §9.7.
  6. Default-heuristic fallback (whatever the controller chose).

Cross-cutting:

  * **Priority 3 vs 4 ordering note (Validator-1 MINOR fix — combat vs unjam
    deadlock)**: unjam can override combat under the
    `FrustrationMeter > 0.5 × saturation AND lastEnemyGet alive AND no progress
    in 8 s` condition (the "CombatStuck" branch). Without this, a tech wedged in
    cover with an alive-but-unreachable target gets perpetually-Combat priority,
    never unjams, and BackwardsLock or WedgedNoProgress trip on the resulting
    stuck state. Rationale anchored in `bgeneral-backoff-unjam-deadlock` memory.
  * **MP RTS-state coexistence with mailbox writes (audit cross-ref)**: BUG-017
    (`ManWorldRTS.cs:152-193`) now asserts per-type RTS state
    (`lastPlayer` / `lastCloseAlly` / `theResource`) on host BEFORE the
    `if (ManNetwork.IsNetworked) return;` path. Coordinator external goals
    therefore reflect authoritative RTS intent on both host and client.
    `GuardCorrectiveActuator` mailbox writes (priority 0 / AntiCrash +
    GroundedAircraft) are HOST-ONLY by construction — `BehaviorGuardWorker`
    runs only on the host (§9.2). Clients never have a populated
    `GuardInjectedGoals` slot, so the priority chain on a client collapses to
    priority 1+ (external goal from the host-replicated RTS state). BUG-021
    additionally host-gates cross-team `SetRelations` at three sites
    (EnemyMind/RCore/ModifiedTargeting) so guard-triggered relation changes
    are likewise host-authoritative.

**Vanilla form (Validator-3 MINOR fix — corrected description).** The Vanilla
form does not invoke the `ISmartGoalSource` path at all (per `refactor-v2 —
plugin forms` memory: "Vanilla=true handback" — Vanilla hands back to the
original classic AI). Vanilla techs never see `GuardInjectedGoals`; guards
NEVER cross-pollute the Vanilla form. The Modified form's `ContinuousController`
is the only consumer that reads the mailbox.

**Modified non-Smart drive paths (LandAICore, AirplaneAICore, etc.)**: out of
scope for guards. The §9 scenario gates implicitly narrow to `ActiveForm ==
Smart` because the mailbox-reader modification is only in `ContinuousController`.
Guards on a non-Smart-form Tech are inert; their constraints contribute zero. If
field testing shows a need, a separate modification phase can extend the mailbox
reader into `AICore` drive — out of scope here.

### 9.8 New OutcomeKind values

Three new `OutcomeKind` enum values feed `IdentityOutcomeConsumer`:

  * `GuardViolation_Movement` — emitted by BackwardsLock, WedgedNoProgress,
    WaterloggedTech on edge transition (not every tick). (OrbitalLockup
    dropped — see §9.4.)
  * `GuardViolation_Role` — emitted by IdleGatherer, OverdueDelivery, Homesickness,
    LostAlly.
  * `GuardViolation_Combat` — emitted by OrbitNoFire, FriendlyFire,
    OverextendedHunter.

GroundedAircraft does NOT emit through `IdentityOutcome` — its activation is
journaled directly via `GuardCorrectiveActuator` (the hard-correct activation is
the event of interest, separate from observation outcomes). It feeds the
`grounded_aircraft_intervention_rate` diagnostic constraint via the journal.

**Source-byte high-nibble** (bits 4–7 of the existing `IdentityOutcome.Source`
field added by the base plan for Live/Replay) carries the guard id ordinal.
Encoding revised post-Validator-3 to disambiguate the "no filter" sentinel from
the "no guard" identity (which were both `0` in the prior draft):

  * `0..10` = guard ordinals (0 = BackwardsLock, 1 = WedgedNoProgress,
    2 = WaterloggedTech, 3 = GroundedAircraft, 4 = IdleGatherer,
    5 = OverdueDelivery, 6 = Homesickness, 7 = LostAlly, 8 = OrbitNoFire,
    9 = FriendlyFire, 10 = OverextendedHunter). 11 valid guard ordinals; 4
    slots reserved (11..14) for future guard additions.
  * `15 = wildcard / no-filter` (sentinel used by `GetRatePerMin` when caller
    does not want guard-id filtering).
  * Non-guard publishers (KillScored, BlockDelivered, BaseHeld, AllyProtected)
    set the high nibble to `15` so they never match a guard-id filter.

`IdentityOutcomeConsumer.GetRatePerMin` gains an optional `byte guardFilterByte
= 15` (was `0`) parameter; `15` means "no filter". Legacy call sites stay
back-compatible by omitting the parameter. **Existing `GetCount(SmartIdentity,
OutcomeKind)` accessor is preserved** (Validator-3 MAJOR fix): the new ring is
added as a sidecar; `_counters` stays for back-compat and is reimplemented over
the ring's snapshot count. No existing callers break.

**Expansion ceiling explicit** (Validator-1 MINOR fix): the 4-bit field caps at
14 guards + 1 wildcard. A unit-test in `Smart/Tests/GuardOrdinalTest.cs`
asserts `GuardRegistry.GuardCount <= 14`. If a 15th guard ships, the encoding
must migrate to a 16-bit Source field with the high-byte carrying guard ordinal
and the low-byte carrying Live/Replay. YAGNI — surface the ceiling, defer the
migration.

### 9.9 Open questions (guard-specific; see §14 for full list)

Surfaced here, resolved in §14:

  * OverdueDelivery routing: nearest friendly Harvesting base vs originally-assigned
    dock (if Gatherers have sticky base assignment)?
  * `grounded_aircraft_intervention_rate` storm > 2/min total — auto-disable the
    guard, or stay diagnostic-only (operator decides)?
  * MP soft-correct safety: largely moot under V-fix telemetry-only
    reconciliation — OverdueDelivery does not inject a `ReturnToBase` goal,
    so there is no host-only motion to replicate. The only MP concern is
    that `IdentityOutcome` publish-from-host reaches the Director; same as
    other publishers.

---

## 10. New OutcomeKind publishers

**Four publisher sources** (Validator-3 MAJOR fix — was "three"). `BehaviorGuardWorker`
in §9 IS a fourth publisher source emitting three `GuardViolation_*` outcome
kinds on edge transitions. It is co-installed with the three named publishers in
`SmartEventBridge.Install/Uninstall` and shares the publisher invariants
(`LogWarnFileOnly` `[AIWARN]` routing on internal errors, no popups).

All four honour `LogWarnFileOnly` `[AIWARN]` routing on internal errors (no popups).
Install/Uninstall in one place: `SmartEventBridge.Install/Uninstall`.

| OutcomeKind | Publish site (corrected) | LOC |
|---|---|---|
| **BlockDelivered** | **REVISED**: the original plan named `ModuleHarvestReciever` as the Harmony target. That class is a docking marker (`IAIFollowable`) — its `OnTaken(Visible, Stack)` at `ModuleHarvestReciever.cs:34` is an empty stub. **Subscription target (MAJOR-fix)**: switch from per-holder `ModuleItemHolder.TakeItemEvent` to the cleaner per-tank `tank.Holders.ItemPickupEvent` (verified at decompile TechHolders.cs:163 — `public Event<Tank, Visible> ItemPickupEvent`) paired with `ItemReleaseEvent` (line 165). These fire ONLY on cross-tank transitions (Visible.cs:910,928 gate `tank != tank2`), giving direct cross-tank handoff signal with ONE subscription per Smart Base tech, NOT per-holder. **Attribution mechanism (MAJOR-fix)**: pair Release→Pickup within 2-frame window using transient `Dictionary<Visible, (Tank src, int frameId)>` keyed by Visible identity; GC stale entries at frame end via `Time.frameCount` staleness check. Also subscribe to `Visible.OnPool/OnRecycle` to clear stale dictionary entries on Visible recycle (prevents reused Visible references attributing to a destroyed Gatherer). On unpaired Pickup, suppress publish and journal `BlockDelivered.AttributionDropped`. Emit `IdentityOutcome(carrierTechId, Gatherer, BlockDelivered, magnitude=block.GradePrice/normFactor)` only when carrier tech is Smart-driven AND stamped `SmartIdentity.Gatherer` AND `carrierTech.Team == receivingTech.Team` (verified Acceptance test runs at Pickup time on receiving holder via `holder.Acceptance.HasFlag(AcceptFlags.Chunks)` at ModuleItemHolder.cs:629). **Non-Smart Base spam guard (MAJOR-fix)**: ModuleHarvestReciever.OnAttach (line 41-58) registers ALL Chunk-receiver bases to AIECore.Depots; lazy-subscribe only on first Smart-tech-spawned event whose identity is `Base` AND tech has ≥ 1 Chunks-flag holder. On Tank-destroyed event, drop subscription. Outer try/catch LogWarnFileOnly's only the first 5 events per session then suppresses. The 1 Hz bag-count poll fallback is **dropped**. **OQ-1 is resolved by inspection** | 240 (revised from 180 — original undersized for Release/Pickup pairing + non-Smart spam filter + Visible recycle hook) |
| **BaseHeld + BaseLost (paired)** | Subscribes to `WorldEventBus<DamageObserved>`. Per Base-identity tech (`SmartIdentity.Base`), maintains `{hpAtWindowStart, hpNow, windowStartTick, totalDamageInWindow}` in `ConcurrentDictionary<TechId, BaseWindow>` with AddOrUpdate semantics. HP polled at **0.2 Hz** using `Damageable.Health` (CAPITALIZED — computed property at decompile Damageable.cs:67) sums cached lazily. **Block-detach invalidation (MAJOR-fix)**: subscribe ONCE per Smart Base tech to `Tank.DetachEvent` (verified at Tank.cs:264 — `Event<TankBlock, Tank>`) NOT per-block `TankBlock.SubToBlockAttachConnected` hook list. One subscription per tech, scoped to Tank lifetime; cleanup on Tank-destroyed; eliminates the per-block leak surface. **60 s window**; constraint uses 5 min EWMA over those windows. At window close: if tech survived AND `totalDamageInWindow >= damageThreshold=25`, publish `IdentityOutcome(techId, Base, BaseHeld, magnitude=hpNow/hpAtWindowStart clamped [0,1])`. **Tech-recycle handling (MAJOR-fix)**: on `WorldEventBus<TechDespawned>` after BaseLost emission, delete the per-tech state entry. On TechSpawned with a TechId already in dict (Tank pool reuse), reset windowStart and HP atomically. Tech-destroyed events emit `IdentityOutcome(techId, Base, BaseLost, magnitude=1.0)` where **BaseLost is a NEW OutcomeKind value=4** (per §12.2 EventBus.cs row). Classification: prefer `SmartIdentityClassifier` result; fallback to `helper.NonAIBase==false ∧ BasePurpose ∈ {Headquarters, Defense, Harvesting}`. Exposes `GetTechCurrentHP(techId) + GetTechMaxHP(techId)` getters reused by OverextendedHunterGuard's HP-aggregation check | 220 |
| **AllyProtected (graded magnitude)** | Per-1 Hz scanner driven by Director tick (NOT a separate daemon). Per Smart-driven `AircraftSupport`/`RepairSupport`/`Patrol` tech, maintains `{windowStart, sumTimeInRange, friendlyDamageInWindow, pairedAllyTechIds[]}`. 60 s window. At window close: emit `IdentityOutcome(supportTechId, <identity>, AllyProtected, magnitude=max(0, 1 - friendlyDamageInWindow/damageBudget))` where `damageBudget=500`. If magnitude == 0, skip emission (no signal); if magnitude < 0.1 emit with `[ALLY-PROTECT-DEGRADED]` tag. Position reads via `BeliefSnapshot.ByTech` (snapshot acquired ONCE per scanner tick via `FusedBuffer.Read()`; all ByTech reads in that tick use the same snapshot to avoid mid-tick double-buffer flip). **LRU eviction policy (MAJOR-fix)**: capacity raised 64 → 128. On eviction of a pair with windowAge ≥ 15 s, finalize and publish IdentityOutcome with magnitude scaled by `sumTimeInRange / 60`. INSUFFICIENT_DATA gate: if `LiveSmartTechCount(support identities) > capacity * 0.9`, surface `[ALLY-PROTECT-OVERSUBSCRIBED]` and skip remediation (system-overload, not policy failure). **RM-3 observability:** publisher tracks `evictionsPerMinute` counter; digest line includes it. 128 is nominal sizing for typical encounters (peak realistic pair count 80-100 at ragnarok 32-tech slider max per KickStart caps); operator can monitor whether sustained pressure indicates need to upsize. Exposes `TryGetPairedAlly(int supportTechId, out int allyTechId)` and `LastAllyLossMono(int supportTechId)` accessors for LostAllyGuard | 200 |

Tee points for `IdentityReplayBank`: at the **typed-queue enqueue sites**
(post-audit cites — revalidate at impl time): IntentEvent at
`TargetObservationSequenceBuffer.cs:~210` (DrainAndEnqueue body);
ResidualEvent at `LeadResidualRecorder.cs:~121`; ThreatEvent at
`LearningService.cs:~683` (post-BUG-026/046/022 drift from 694).
**ActionValueEvent producer SHIPPED** (feature-expansion e9bfcd4):
`ContinuousController.PublishActionValueEvent` at ContinuousController.cs:444,
called at :310, enqueue at :514. **Tee at 4 sites, not 3.**
`replay_bank(*, ActionValue)` is functional in v1; BankFullness-gated
SKIPPED-INSUFFICIENT remains the cold-start path. Threat-feature builder now
writes 48-slot rows (`BuildThreatEventFeatures` per FEATURE-EXPANSION §3.5);
Intent producer now writes 40-slot rows via `FillIntentSlots` in
StrategicStateExtractor (FEATURE-EXPANSION §3.2). Replay envelopes inherit the
new slot layouts — no per-event payload mutation needed on tee. Feature-expansion
e9bfcd4 also shipped StrategicStateVector double-buffer + StrategicStateExtractor
daemon + SelfStateProbe + CargoStatePublisher + NearestTechCache + WeaponFireBuffer
+ EnemyVehicleSnapshots (BUG-026) + DamageHintBuffer 7th DamageType byte field
(BUG-046) + HealthSidecar HP-history ring — Director publishers in §10 reuse these
shipped surfaces rather than re-spec them; audit §10 entries that overlap
(`CargoStatePublisher` partially subsumes BlockDelivered carrier tracking) and
delta on top rather than from-scratch.
Attach source-tech `SmartIdentityStamp.Identity` at enqueue time (cheap — already
on `TankAIHelper.FormState`). **R3-M1 fix: `TankAIHelper.FormState` is declared
`public object FormState = null;` at `TankAIHelper.cs:94` — bare-dot access
`helper.FormState.IdentityStamp.Identity` will NOT compile. Use pattern-match cast
at every call site:**
`if (helper.FormState is SmartPerTechState state) { var identity = state.IdentityStamp.Identity; ... }`.
All 19 existing call sites use this pattern; the plan was the only place with a bare
dot accessor. Replay bank uses **4 type-specialized
dictionaries** (NOT a single boxed-erased queue): `Dictionary<SmartIdentity,
BoundedQueue<EventEnvelope<IntentEvent>>>` etc., where `EventEnvelope<T>` wraps the
typed event + `byte Source` (Live=0, Replay=1) + `byte ReplayCount` for multiplicity
cap. Drain dispatches with `BoundedQueue<IntentEvent>` → `Intent.EventQueue.Enqueue(envelope.Event)`
at typed compile time — no boxing, no GC churn on 30 Hz hot path.

---

## 11. ChunkRegenWorker

| Field | Spec |
|---|---|
| **Cadence** | 5 s tick (0.2 Hz) when `DirectorState.ActiveScenario ∈ {S2, S5}`; idle `Wait(5000 ms)` on cancellation token when gated off |
| **Spawn API** | `Singleton.Manager<ManLooseBlocks>.inst.HostSpawnChunk(ChunkTypes, pos, Quaternion.identity, initNew:true, vel:zero, rotVel:zero)` — verified `ManLooseBlocks.cs:165`. **Must run on main thread** (Unity `Transform.SetParent` at `ManLooseBlocks.cs:1166`). Worker computes WHAT to spawn on its background tick and enqueues `ChunkSpawnRequest` envelopes onto `WorldEventBus.PublishFromWorker` — each envelope **stamped with `scenarioGenerationId = DirectorState.ScenarioGeneration`** (MAJOR-fix: prevents post-teardown-and-respawn-into-old-scenario race; ScenarioGeneration increments on every scenario_set / scenario_respawn). Main-thread executor in `SmartForm.Operations` REJECTS envelopes whose stamp ≠ current generation. Also gated on a `volatile bool _enqueuingPermitted` checked at envelope-CREATION site (NOT drain site) so on Director.BeginShutdown ChunkRegenWorker stops emitting immediately. Main-thread drain flushes remaining stale envelopes without invoking spawn API. The ChunkType list pulled from `Enemy/RLoadedBases.cs:1851-1871 TryGetBiomeResource(spawnPos)` (returns `ChunkTypes[]` array, NOT weighted dict — engine default is the 2-element `{PlumbiteOre, TitaniteOre}` from which ChunkRegenWorker picks uniformly at random per spawn — minor doc clarification). Wrapped in try/catch + reflection guard; missing API → `LogWarnFileOnly("[CHUNK-REGEN-DISABLED]")` once and ChunkRegen disabled for that scenario |
| **Spawn logic** | (a) Sweep `_spawnedChunks` dict, drop destroyed entries. (b) Census via `Physics.OverlapSphere(centroid, radius=200 m, LayerMask.GetMask("Pickup"))`; **fallback when OverlapSphere unavailable / suspect: iterate worker-local `List<ResourcePickup> _spawnedChunks` and prune entries where `!resourcePickup.visible.isActive`** (RM-6 fix: `ManLooseBlocks.AllLooseBlocks` does NOT exist — no public enumeration surface; only private `m_ChunkByPoolIDLookup` Dictionary at `ManLooseBlocks.cs:59`). Primary census stays `Physics.OverlapSphere`. **Stage-2 census-semantics doc note**: OverlapSphere returns chunks spawned by ANY source (engine ManLooseBlocks, ResourceDispenser, etc.); the worker-local list only sees Director-spawned. The two censuses are NOT interchangeable — Director-list undercounts ambient chunks → over-spawn. Census semantics = "ambient + Director" (OverlapSphere) is canonical; the fallback path applies only when LayerMask binding fails. Failure-mode bound: no-fallback → ChunkRegen disabled for that scenario. Future maintainers MUST NOT swap to the worker-local list as a "perf optimization" — that would turn ChunkRegen into a spawn-storm. (c) deficit = targetCount − live. **Budget cap**: 6 chunks per worker tick global + 1/sec per scenario sub-budget (S2 gets +50% boost: 9/tick + 2/sec — per Lens B Info finding on S2 throughput); spawn ceil(deficit × 0.25) per tick clamped to budget. (d) Spawn at jittered positions in annulus [40 m..radius] around centroid (40 m inner so chunks don't drop on bases), `Physics.Raycast`-down for ground Y, random rotation. (e) Record returned `ResourcePickup` keyed by `NetTech`/`visibleID`. Scenario intensity scales targetCount |
| **Scenario gate** | Reads `DirectorState.ActiveScenario + ScenarioWorker.ActiveTemplates` at top of each iteration. If not in {S2, S5} or `ChunkFieldDef` list empty → `ct.WaitHandle.WaitOne(5000)` and continue (zero work). Also gated on `SmartRuntime.IsHost && !SmartRuntime.IsPaused`. Registered with `DaemonWatchdog.RegisterCanonical("ChunkRegen", factory)` + `WorkerHealthMonitor.RegisterCanonical` |
| **LOC** | ~250 (was 220; +30 for main-thread marshaling per Lens C blocker) |

---

## 12. Implementation manifest

### 12.1 New files (31 base + 19 guard layer = 50)

(Was 20; `Smart/Director/Guards/Movement/OrbitalLockupGuard.cs` removed per
Validator-1.)

| Path | Purpose | LOC |
|---|---|---|
| `Smart/Director/TrainingDirector.cs` | L2 daemon: 1 Hz aggregation, 0.2 Hz constraint eval, action dispatch, journal append, healthy-snapshot scheduler, digest emission. Registered with `DaemonWatchdog.RegisterCanonical("TrainingDirector", …)` + `WorkerHealthMonitor.RegisterCanonical`, mirroring `LearningService.cs:117-119`. ActionPicker uses severity = deviation/band-width × dependsOnPublisher_weight; 5-min post-fire observation per constraint | 510 |
| `Smart/Director/DirectorState.cs` | Static singleton: `volatile int ActiveScenario`, `float Intensity`, `ConcurrentDictionary<int, Directive> DirectiveTable`, `ConcurrentQueue<JournalEntry> JournalRingInMem` (2048 cap), `LatestDigest`, `ViolationCounters`, `ScenarioOwnedTeams` | 160 |
| `Smart/Director/DirectorTuning.cs` (V-fix MINOR — renamed from LearningTuning to avoid namespace collision with existing `Smart/Learning/LearningTuning.cs`; the existing static class `LearningTuning` in `TAC_AI.AI.Forms.Smart.Learning` namespace is preserved unchanged with `UseFullBPTT`) | Director-scoped fields: `volatile float[] LrScale[4]` (per-model Adam-LR scale; restored on rollback via per-model snapshot.lrScales array), `volatile float IntentTemperature`, `volatile float[] OutcomeWeights[4]`, plain `long[] FrozenUntilTickMono[4]` (NOT volatile — illegal for long; Interlocked.Read/Exchange access). **Read-once-per-batch invariant** documented in header — `TrainOneMinibatch_FullBptt` snapshots via `Volatile.Read(ref DirectorTuning.IntentTemperature)` into a local. Serialises to `SmartAI/Director/state.json` for restart resume via PresetIO MiniJson with atomic write-temp + rename (ProfilePersistence.cs:153-169 pattern). **TuningSnapshot is captured ONCE per checkpoint trio** (V-fix race-fix: `lock(model.SaveMutex)` doesn't protect this global; capture happens at the top of DirectorCheckpoint.Capture under a dedicated `_tuningSnapshotLock` so temp_adjust writers queue) | 140 |
| `Smart/Director/DirectiveParser.cs` | Recursive-descent parser; no regex/Sprache/Antlr | 220 |
| `Smart/Director/DirectiveSurface.cs` | DirectiveTable + persistence + expiry + dedup + overflow eviction | 150 |
| `Smart/Director/DirectiveInboxWorker.cs` | 5 Hz inbox poller (FSW + LastWriteTime fallback) | 110 |
| `Smart/Director/ConstraintRegistry.cs` | Static array of 11 `Constraint` structs | 280 |
| `Smart/Director/ConstraintEngine.cs` | Stateless evaluator; severity ordering; observation windows; insufficient-data state; warmup gating | 220 |
| `Smart/Director/TrainerStats.cs` | 12 reservoirs (Loss/ParamDelta/Entropy × 4 models). Architecture-invariant `‖Δθ‖₂ / sqrt(N_params)` push. Single-writer ring + sort-on-read for p50/p99/mean/var, mirrors `PathRequestBackpressure._latencyReservoir`. Host-process-only, never persisted, reset on host-loss | 260 |
| `Smart/Director/IdentityReplayBank.cs` | Per-(SmartIdentity, ModelId) `BoundedQueue<EventEnvelope>(4096)`. Per-envelope multiplicity cap = 2. Tee from typed-queue enqueue sites in LearningService. Drain under `model.SaveMutex` with `TryEnter(100 ms)` → SKIPPED journal on miss. `BankFullness(identity, model)` accessor used by ActionPicker | 220 |
| `Smart/Director/DigestBuilder.cs` | Text + JSON digest from `DigestSnapshot`. Wall-clock minute-aligned. Populated-cells-only identity rendering | 280 |
| `Smart/Director/DirectorJournal.cs` | Append-only JSONL writer; rotation at 5 MB; in-memory ring 2048 entries; Tail/Range/Query | 180 |
| `Smart/Director/DirectorCheckpoint.cs` | Atomic 3-file checkpoint trio; auto-prune > 14 d; never delete a referenced slot | 220 |
| `Smart/Director/DirectorParamRing.cs` | T1 ring: 3 healthy + 8 pre-action with 30-min TTL. **Capture sequence is a single `lock(model.SaveMutex)` critical section** (atomicity invariant documented in header). `ArchitectureVersion` field validated on restore | 200 |
| `Smart/Director/Actions/IDirectorAction.cs` | `Apply(ctx) → JournalEntry`, `Undo(entry)`, `Describe()` | 40 |
| `Smart/Director/Actions/LrScaleAction.cs` | `lr_scale`. Uses new `BaseLearningRate` per model | 100 |
| `Smart/Director/Actions/TempAdjustAction.cs` | `temp_adjust`. **Intent-only** (rejects ActionValue) | 90 |
| `Smart/Director/Actions/ReplayBankAction.cs` | `replay_bank` with BankFullness gate; SKIPPED-INSUFFICIENT journal | 100 |
| `Smart/Director/Actions/FreezeModelAction.cs` | `freeze_model` with diagnostic/soft modes | 90 |
| `Smart/Director/Actions/ScenarioSetAction.cs` | `scenario_set` | 80 |
| `Smart/Director/Actions/ScenarioRespawnAction.cs` | `scenario_respawn` | 70 |
| `Smart/Director/Actions/RollbackAction.cs` | `rollback` — uses Director pause channel (`SmartRuntime.RequestDirectorPause`), try/finally on `AcceptingTrainingEvents`, validates `ArchitectureVersion`, emits `STANDING-DIRECTIVES-REAPPLIED` | 170 |
| `Smart/Director/Scenarios/ScenarioWorker.cs` | L3 planner daemon. Owns scenario instance state. Composes requests; emits `ScenarioSpawnRequest` envelopes onto main-thread queue (does NOT call `RawTechLoader.SpawnMobileTechPrefab` from background thread). Anchors SubNeutral relations on each tick for S2/S5 (calls `ManBaseTeams.SetRelations(a, b, SubNeutral)` + zeroes `EnemyTeamData.angerThreshold` per pair) | 430 |
| `Smart/Director/Scenarios/ScenarioRegistry.cs` | 6 `ScenarioRecipe` static entries. Each declares expected events/min per (model, identity) for the `INSUFFICIENT_DATA` gating | 300 |
| `Smart/Director/Scenarios/ChunkRegenWorker.cs` | L3 planner daemon; main-thread marshalled | 250 |
| `Smart/Director/Publishers/BaseHeldPublisher.cs` | BaseHeld + BaseLost paired publisher | 220 |
| `Smart/Director/Publishers/BlockDeliveredPublisher.cs` | `tank.Holders.ItemPickupEvent` / `ItemReleaseEvent` per-tank subscriber with Release→Pickup 2-frame attribution; non-Smart spam guard + Visible recycle hook (matches §10 row spec) | 240 |
| `Smart/Director/Publishers/AllyProtectionPublisher.cs` | Graded-magnitude AllyProtected publisher | 200 |
| `Smart/Director/DirectorTunables.cs` | All `smart.director.*` `TunableRegistry` registrations. **Single source of truth** — `TrainingModeTunables.cs` is NOT modified to add Director tunables (avoid double-registration). Tunables that touch Unity from bind callbacks marshal via main-thread queue inside the bind | 130 |
| `Smart/Tooling/SmartDirectorConsoleCommands.cs` | Sibling class. 12 `[DevCommand]`s + aliases with `KickStart.ModCommandID + ".smart.director.<…>"` prefix, `Access=Cheat`, `Users=User.Host`. Commands: `tell` (BLOCKER-fix: requires quoted line — DevCommand framework splits args on whitespace; helptext + parse-error inline documents this), positional aliases `tell.lr_scale <model> <factor>` / `tell.freeze <model> <sec>` / `tell.rescind <id-or-tag>` for common cases, `list`, `rescind`, `status`, `digest.now`, `checkpoint.save`, `rollback`, `scenario.set`, `journal.tail`, `constraints.list`, `actions.list`, `audit`, `guards.selftest` (amendment A3 — Host + Cheat; runs synthetic pathology suite per §9.6; emits per-guard PASS / FAIL digest line; ~20 LOC dispatcher) | 280 |
| `Smart/Docs/TRAINING-DIRECTOR-DESIGN.md` | Companion design doc with verification anchors, op-questions, future-work | 520 |
| `<UserDir>/SmartAI/Director/constraints.json` (sidecar) | Default band overrides; reload via `smart.director.reload` | 80 |
| `Smart/Director/Guards/IBehaviorGuard.cs` | Interface `bool ShouldEvaluate(GuardContext)`, `GuardVerdict Evaluate(GuardContext)`, `SmartIdentity[] AppliesToIdentities`, `int[] AppliesToScenarios`, `int WindowSec`, `GuardSeverity Severity`, `string Name`. `GuardVerdict` struct: `Fired`, `ExceptionMatched`, `ExceptionId`, `Magnitude`, `EvidenceFields`. `GuardSeverity` enum `{Observe=0, Warn=1, SoftCorrect=2, HardCorrect=3}` | 80 |
| `Smart/Director/Guards/GuardRegistry.cs` | Static array of 12 `IBehaviorGuard` instances + `GuardId` enum (12 entries) + bucket-mapping table (which guard → Movement/Role/Combat OutcomeKind). Per-guard tunable hookup via `DirectorTunables.cs`. Init-time validation: pathology test compiles, identity gate non-empty, severity-rationale string non-empty | 180 |
| `Smart/Director/Guards/GuardContext.cs` | Per-tick read-only context: `ActiveScenario`, `ScenarioRegistry` recipe pointer, `TeamRuntime` snapshot, `MonoTick`, `LiveSmartTechCount`, configured tunable values, helper, tank, identity, position window ring, displacement / recentSpeedXZ-mean / target-distance-mean cached. Pre-allocated; no GC per tick | 100 |
| `Smart/Director/Guards/BehaviorGuardWorker.cs` | **L3 daemon, 1 Hz tick.** Registered via `DaemonWatchdog.RegisterCanonical("GuardWorker", factory)` + `WorkerHealthMonitor.RegisterCanonical`. Iterates `SmartRuntime.EnumerateTeams` roster, runs identity-gated + scenario-gated guards per (tech, guard), maintains rolling windows via `GuardWindowPool`. Emits `IdentityOutcome(techId, identity, GuardViolation_<bucket>, magnitude=1.0)` on edge transitions only (not per tick). Host-only, `IsPaused`-gated. Hard-correct path coordinates with `GuardCorrectiveActuator`. Reads-only: never mutates TankAIHelper state directly | 380 |
| `Smart/Director/Guards/GuardWindowPool.cs` | Pre-allocated ring buffers keyed by (techId, guardId). Architecture mirrors `PathRequestBackpressure._latencyReservoir`. Single-writer / single-reader (GuardWorker tick). LRU evicts windows for despawned techs. Sized 64 techs × 12 guards = 768 windows; avg slot count 60 (60 s windows at 1 Hz) | 180 |
| `Smart/Director/Guards/GuardCorrectiveActuator.cs` | Central actuator for the **hard-correct** guards (V-fix reconciliation: soft-correct OverdueDelivery is telemetry-only and does NOT go through the actuator). `RaiseGroundedAircraftRecovery(techId, deadlineMono)` is the only RaiseX surface. (Prior `RaiseBaseReturn(...)` STRUCK — telemetry-only OverdueDelivery publishes directly via `BaseReturnTelemetryObserver`.) Tracks per-tech active corrections with `deadlineMono`. On each watcher tick, sweeps expired deadlines → `ForceClear` injected goal, restores any modified `AILimitSettings.HoverHeight`, journals release. Enforces cooldowns (15 s GroundedAircraft re-trip). Journal entries: `guard-fire`, `guard-release`, `guard-yielded-to-policy`, `guard-aircraft-chronic` | 200 |
| `Smart/Director/Guards/GroundedAircraftRecoveryGoalSource.cs` | **Mailbox writer** (Validator-3 BLOCKER fix — no native `IAIGoalSource` exists). Called by `GuardCorrectiveActuator.RaiseGroundedAircraftRecovery`. Writes `TacticalGoal { Position = pos + (0, 30, 0), Heading = up, expiresMono = MonoClock.Now() + MonoClock.FromSeconds(5.0), sourceId = (byte)GuardId.GroundedAircraft }` into `TankAIHelper.GuardInjectedGoals` mailbox. Self-expires deterministically via `MonoClock` check inside `GuardCorrectiveActuator.SweepExpired` — guard layer does NOT poll. After expiry, emits a 2 s `MaintainAltitude` ramp-down handoff slot (§9.3). Suppressed if `Provoked > 0` AND target alive AND target is a ground tech (strafing exception — §9.3 Vehicle bucket exceptions) | 110 |
| `Smart/Director/Guards/BaseReturnTelemetryObserver.cs` | **Telemetry-only observer** (V-fix reconciliation: file renamed/repurposed from prior `BaseReturnGoalSource.cs` mailbox writer — telemetry-only direction per §9.7 step 5 + §9.3 OverdueDelivery mechanism block). NOT called by `GuardCorrectiveActuator` — the actuator's RaiseX surface is hard-correct only. Invoked directly by `OverdueDeliveryGuard.Fire()`: emits the `IdentityOutcome(GuardViolation_OverdueDelivery)` with magnitude = clamp(overdue_duration_sec/90, 0, 1), then schedules a 30s kinematic-correlation tracker (`(basePos − techPos)` magnitude delta over 30s) emitted as a separate magnitude-scaling `IdentityOutcome` for training-signal correlation. Never writes the mailbox — no `TankAIHelper.GuardInjectedGoals[OverdueDelivery]` slot exists. Friendly base lookup via `TeamRuntime.HarvestingBases` accessor (unchanged). Main-thread `(basePos − techPos)` computation marshaled via `GuardEvaluationRequest` envelope | 30 |
| `Smart/Director/Guards/RollingWindow.cs` | Bounded ring buffer specialised for guards: fraction-above-threshold, mean, fraction-of-ticks-with-predicate. Distinct from `TrainerStats` reservoirs (p50/p99/var oriented). Allocates from `GuardWindowPool` | 140 |
| `Smart/Director/Guards/GuardTelemetry.cs` | Aggregates per-guard fire counts + cooldown-suppressed counts + override-by-model counts (hard-correct only) + release-within-deadline assertions. Drives the GUARDS digest section. Anti-stifling counters: `guards_re_fired_within_cooldown`, `learned_model_overrode_guard_count` (hard-correct only — V-fix telemetry-only reconciliation: soft-correct OverdueDelivery does not enter arbitration so cannot be overridden), `soft_correct_telemetry_fires_count` (replaces prior `soft_correct_won_arbitration_count` — telemetry-only soft-corrects never "win arbitration"; metric now counts emissions of `IdentityOutcome(GuardViolation_OverdueDelivery)`). The kinematic-correlation tracker emits a separate magnitude-scaling `IdentityOutcome` for training-signal correlation — counted here as `soft_correct_kinematic_correlation_count` | 180 |
| `Smart/Director/Guards/Movement/BackwardsLockGuard.cs` | observe; reads `recentSpeedSigned` ring (newly added on `TankAIHelper` per §12.2) + position delta + `lastEnemyGet` (via `IsLiveTechTarget`) + `Provoked` + `AuthoredTerrain` + `FrustrationMeter` + `SmartIdentityStamp.SpawnAnchor` (Validator-3 MINOR rename) + Sniper-standoff + TurretFraction kiting exceptions | 130 |
| `Smart/Director/Guards/Movement/WedgedNoProgressGuard.cs` | warn; **EXTENDS** the existing combat-fixes round-2 wedge net-progress tracker rather than duplicating — exposes its tick counter via a small public getter on `TankAIHelper`. Provoked-decay edge-case gate per Validator-1 MINOR | 110 |
| `Smart/Director/Guards/Movement/WaterloggedTechGuard.cs` | observe; reads `ManWorld.SeaHeight` + `AuthoredTerrain` + `AIType` + MoveStyle classification cache. Sea-height tightened to `SeaHeight − 2.0 m` (Validator-1) + net-displacement progress AND-conjunct + Provoked+chasing exception | 120 |
| `Smart/Director/Guards/Vehicle/GroundedAircraftGuard.cs` | **hard-correct.** Detection only (actuator + mailbox-writer live in their own files). Reads `recentSpeedY` (existing) + altitude via main-thread-marshaled `ManWorld.inst.TileManager.CalcTerrainHeightAtPosition` (Validator-3 BLOCKER fix: `TerrainHeightCache` was fictional) + pitch attitude + throttle telemetry (Validator-1 new gates) + `ManNetwork.IsClient`. Triggers `GuardCorrectiveActuator.RaiseGroundedAircraftRecovery`. Explicit unit-test hooks for release-within-deadline assertion. Strafing-target exception via `lastEnemyGet IsLiveTechTarget` + target ground-altitude check | 130 |
| `Smart/Director/Guards/Role/IdleGathererGuard.cs` | **observe** (Validator-1 demote); reads `ModuleItemHolder` counts + reachability-filtered chunk census via `PathingService.IsReachable` (R3-B1: routes through `TerrainMap.IsTraversable` O(1) DoubleBuffer read, NOT AIEPathMapper which is non-thread-safe; was raw OverlapSphere) + `BlockDelivered` events + `Provoked` + `PathingService.LastQueryFailedRecently` exception (R3-B4: drop AIEPathMapper cite — the prior reference to `AIEPathMapper.cs:27` was wrong (line 27 is the class declaration); the only related method is `AIPathTileCached.GetAIPathTileCached(WorldTile)` at `AIEPathMapper.cs:905` — an internal factory on an inner class with wrong signature + wrong semantics, NOT to be called from background threads) | 130 |
| `Smart/Director/Guards/Role/OverdueDeliveryGuard.cs` | **soft-correct (telemetry-only).** Reads `ModuleItemHolder` counts + nearest friendly base via `TeamRuntime` + `PathingService.IsReachable` (R3-B1: TerrainMap-backed) + `BlockDelivered` events + heading-toward-base + velocity-projection-toward-base exceptions (Validator-1, Validator-2). On window match, calls `BaseReturnTelemetryObserver.Publish(techId, baseTechId)` which emits `IdentityOutcome(GuardViolation_OverdueDelivery)` for training-signal consumption — NEVER writes the mailbox; `GuardCorrectiveActuator.RaiseBaseReturn` does not exist (actuator is hard-correct only). Per-tech re-fire cooldown via `smart.director.guards.overdue_delivery_refire_cooldown_sec` (default 30s) prevents duplicate emissions within a single overdue episode. See §9.3 OverdueDelivery telemetry-only mechanism block for full rationale | 110 |
| `Smart/Director/Guards/Role/HomesicknessGuard.cs` | observe; reads distance to friendly bases + `lastEnemyGet` + `recentSpeedXZ` + `PatrolGoalSource.IsAtRingNode` accessor + hostile-near-base anchor (Validator-1). S5 scenario removed from gate | 110 |
| `Smart/Director/Guards/Role/LostAllyGuard.cs` | warn; reads `SmartRuntime.EnumerateTeams` + `AllyProtectionPublisher.TryGetPairedAlly(supportTechId)` accessor + `AllyProtectionPublisher.LastAllyLossMono(techId)` accessor (publisher gets two small read accessor extensions — see §12.2 row) + `Damageable.health` | 110 |
| `Smart/Director/Guards/Combat/OrbitNoFireGuard.cs` | warn; reads `FireControl.ProjectilesFiredCount` + `TurretFraction` + `MovementController.CurrentPlan` (concrete enum value `PlanKind.CombatCircle` resolved at impl time — Validator-3 INFO; if absent, plan ships a one-line PlanLibrary tag) + `LineOfSightProducer` query + `HoldFireFlag` + weapon overheat + snipe-only-weapon-cycle exception | 140 |
| `Smart/Director/Guards/Combat/FriendlyFireGuard.cs` | warn; `WorldEventBus<DamageObserved>` subscriber filtered on team-attacker. Weapon-classification check for splash exemption. Attacker-aim-time-stamp + ally-line-crossing telemetry for dodge-exclusion (Validator-1) | 130 |
| `Smart/Director/Guards/Combat/OverextendedHunterGuard.cs` | observe; `SmartRuntime` roster radius scan + `Damageable.health` delta + `KillScored` timestamp + `smart.director.guard.overextended_hunter.leash_m` tunable (Validator-3 BLOCKER fix: `MaxLeashRange` was fictional; registered as a new Director tunable in §12.2) + `DirectiveTable` opt-out tag | 110 |

### 12.2 Modified files (17 base + 2 newly explicit = 19 distinct paths)

Newly explicit since the v1 fold-in: `TankAIHelper` (added in fold-in) and
`ContinuousController` (added in this verifier pass — Validator-3 BLOCKER fix
on guard-mailbox reader). Guard-extension rows on §12.1 publishers
(`ChunkRegenWorker`, `AllyProtectionPublisher`, `BlockDeliveredPublisher`) are
LOC-folded into their §12.1 new-file totals rather than counted as separate
modifications — they are NEW files, not modifications of pre-existing files.

| Path | Change | LOC delta |
|---|---|---|
| `Smart/Learning/OnlineTrainer.cs` | (a) `ILearnedModel` gains `bool Frozen { get; }`, plain `long FrozenUntilTickMono` field (NOT volatile — illegal CS0677 for long; use `Interlocked.Read/Exchange` for cross-thread access mirroring `_ttlDiscardsTotal` at lines 295-296), `void LoadAdamState(AdamSnapshot snap)`, `AdamSnapshot GetAdamSnapshot()` (NEW readonly struct: `{float[] M; float[] V; long T; float LR; float BaseLR;}` returned BY VALUE to enforce 7-Action invariant), `void ReadAdamMoments(float[] mDest, float[] vDest, out long T, out float lr, out float baseLr)` (symmetric to existing StoreParameters; used by DirectorParamRing.Capture without exposing private `_adam`), `void DrainOneMinibatchDiscard()` (for soft-freeze mode). **`ArchitectureVersion` already exists as `byte` at line 50 — do NOT add as short** (V-fix: was claimed `short`; widening would break all 4 implementers + ProfilePersistence.cs:22,111 binary format). (b) `TrainerWorker.RunLoop` checks `Interlocked.Read(ref _model.FrozenUntilTickMono) > MonoClock.Now()` between line 409 (IsHost gate) and line 421 (lock acquire) / 423 (TrainOneMinibatch) — Stage-2 cite-verify drift correction; diagnostic-frozen skips Step+Write AND lets queue backpressure-drop with rate-limited `[DIAGNOSTIC-DROP]` LogWarnFileOnly (per-100-events key avoids LogWarnFileOnly's permanent dedup); soft-frozen drains via new DrainOneMinibatchDiscard. (c) After successful `TrainOneMinibatch`, push `(LossBefore, LossAfter, BatchSize)` into `TrainerStats.LossReservoir[model.Id]` **gated on `Source==Live`** so replayed events don't double-count; every 32nd step, snapshot via `StoreParameters(_paramAfter)` (BEFORE/AFTER cached buffers per model) and push `L2(_paramAfter - _paramBefore) / sqrt(N_params)` into `ParamDeltaReservoir`. **Reward access for actionvalue_loss_variance_ratio**: ActionValueEstimator gains `private float _ewmaAbsReward` updated per-minibatch INSIDE TrainOneMinibatch under SaveMutex, exposed as `public float EwmaAbsReward => _ewmaAbsReward`; TrainerWorker reads this for normalization. (d) `AdamState.BaseLearningRate` new field, set ONCE at AdamState construction (immutable; documented in ctor + ParamSnapshot.baseLr). (e) `TtlDiscardsTotal` counter exposed (already at line 296). (f) Director pause channel: new plain `long _directorPaused` (use Volatile.Read/Write for cross-thread visibility) next to `_hostLost`; FSM line edits enumerated (post-audit drift cites; revalidate at impl time): line ~357 `if ((_hostLost || Volatile.Read(ref _directorPaused)) && _phase == Phase.Active)` (was 355); line ~393 `if (!_hostLost && !Volatile.Read(ref _directorPaused))` (was 391); line ~409 unchanged (was 407). Memory barrier `Volatile.Read` immediately before line ~398 `_phase = Phase.Active` write (was 396). (g) **OnHostChanged at line ~331-333 must acquire pause token** before flipping `_hostLost` so host-change-during-rollback serializes; this is the §7.3 BLOCKER-fix. Audit BUG-012 already paired the HostChanged Subscribe/Unsubscribe lifecycle so respawn no longer leaks subscriptions — the rollback path's pause-token acquisition inside the handler is now race-bounded by a fixed subscriber set rather than a growing one. (h) **AcceptingTrainingEvents ref-counted** via `Interlocked.Increment/Decrement` on `_acceptingDisableCount`; effective flag is `_acceptingDisableCount == 0 && SmartRuntime.IsHost`. Producers check the effective flag, NOT the bare bool | 170 (was 130 — undercount per V-fix additions) |
| `Smart/Coordination/TrainerBarrier.cs` | New `AcquirePauseToken(string owner)` / `ReleasePauseToken(owner)` with mutual-exclusion lock on `_pausingOwner` (host-change racing rollback serialises). **R3-M6 hardening:** (a) every call site wrapped in `try { ... } finally { ReleasePauseToken(owner); }`; (b) stuck-token watchdog — if `_pausingOwner != null` > 30s without active rollback, force-release via `Interlocked.Exchange(ref _pausingOwner, null)` + `[PAUSETOKEN-LEAKED owner=X]` journal; (c) `Director.BeginShutdown` MUST NOT acquire the token (deadlock if rollback holds it) — instead signal `Director._killSwitch` + wait for rollback's `finally` to release, bound by 10 s shutdown timeout | 70 |
| `Smart/Learning/OpponentIntentClassifier.cs` | (a) Snapshot `LearningTuning.IntentTemperature` ONCE at top of `TrainOneMinibatch_FullBptt` via `float tau = Volatile.Read(ref LearningTuning.IntentTemperature)`; that local divides logits before `MlpUtil.Softmax` for the entire batch. **LossAfter forward pass MUST inline the softmax with the captured tau** (NOT call Evaluate which would re-snapshot — V-fix). (b) Push `Shannon entropy / ln(OutDim)` at TrainOneMinibatch_FullBptt line 266 (Stage-2 cite-verify; was 256 pre-audit) where probs already computed; helper `ShannonEntropy(p, len)` treats `p[i] <= 1e-9f` as contributing 0 (NaN-safe). `INSUFFICIENT_DATA` audit-log if degenerate softmax `sum < 1e-12f`. (c) `LoadParameters` internally `lock(_saveMutex)` so external callers (Director rollback, existing SnapshotManager.Restore) cannot race the `_publishedParams.Write` at line 352. (d) Implement `Frozen`, `FrozenUntilTickMono` (long with Interlocked accessors), `LoadAdamState(AdamSnapshot)`, `ReadAdamMoments(...)`, `GetAdamSnapshot()`. **M3 per-file LOC breakdown:** ~15-20 LOC for the 4 new members (backing `long _frozenUntilTickMono`, `Frozen` computed via `Interlocked.Read`, `FrozenUntilTickMono` getter/setter via `Interlocked.Read/Exchange`, `LoadAdamState`/`GetAdamSnapshot` wrappers around existing `_adam`) — C# 7.3 has no default-interface-methods, so each sealed implementer must add these. **`ArchitectureVersion` already returns byte at line 47 — no change needed** | 60 (was 50 — undercount per V-fix) |
| `Smart/Learning/ActionValueEstimator.cs` | Implement `Frozen`/`FrozenUntilTickMono`/`LoadAdamState`/`ReadAdamMoments`/`GetAdamSnapshot`. **M3 per-file LOC breakdown (no default-interface-methods in C# 7.3 — sealed implementers each implement):** `long _frozenUntilTickMono` backing field (1), `Frozen` computed property reading via `Interlocked.Read` (3), `FrozenUntilTickMono` property with `Interlocked.Read` getter + `Interlocked.Exchange` setter (5), `LoadAdamState` wrapper around existing `_adam` (3-4), `GetAdamSnapshot` wrapper (3-4) = ~15-20 LOC for the 4 new members alone. Add `LossReservoir` push at `TrainOneMinibatch` return (Source==Live gated). Add `private float _ewmaAbsReward` per-minibatch update under SaveMutex + `public float EwmaAbsReward => _ewmaAbsReward` for reward-normalization at the TrainerWorker push site. `LoadParameters` internally locks `_saveMutex`. **`ArchitectureVersion` already byte at line 46 — no change needed**. **Does NOT bind to `smart.director.actionvalue_sigma_scale`** — SamplingMPC.Sigma* is a controller knob, not a Q-net trainer knob | 35 (was 25) |
| `Smart/Learning/TrajectoryResidualModel.cs` | Implement `Frozen`/`FrozenUntilTickMono`/`LoadAdamState`/`ReadAdamMoments`/`GetAdamSnapshot`. **M3 per-file LOC breakdown:** ~15-20 LOC for the 4 new members (backing `long _frozenUntilTickMono`, `Frozen` computed via `Interlocked.Read`, `FrozenUntilTickMono` getter/setter via `Interlocked.Read/Exchange`, `LoadAdamState`/`GetAdamSnapshot` wrappers around existing `_adam`) — C# 7.3 has no default-interface-methods, so each sealed implementer must add these. `LoadParameters` internally locks `_saveMutex`. `ArchitectureVersion` already byte at line 41 | 18 (was 12) |
| `Smart/Learning/ThreatAssessmentModel.cs` | Implement `Frozen`/`FrozenUntilTickMono`/`LoadAdamState`/`ReadAdamMoments`/`GetAdamSnapshot`. **M3 per-file LOC breakdown:** ~15-20 LOC for the 4 new members (backing `long _frozenUntilTickMono`, `Frozen` computed via `Interlocked.Read`, `FrozenUntilTickMono` getter/setter via `Interlocked.Read/Exchange`, `LoadAdamState`/`GetAdamSnapshot` wrappers around existing `_adam`) — C# 7.3 has no default-interface-methods, so each sealed implementer must add these. Full-pass param snapshot every 32 steps via existing `StoreParameters` (BEFORE/AFTER cached buffers in TrainerWorker, NOT inside the model). `LoadParameters` internally locks `_saveMutex`. `ArchitectureVersion` already byte at line 37 | 25 (was 18) |
| `Smart/Learning/LearningService.cs` | (a) **Publisher install moves to SmartRuntime.Init** (BLOCKER-fix: was after-trainers-enqueued in LearningService.Init — Smart Bases loaded BEFORE that point would miss first events). Publishers gate emissions on `Director.IsActive` (latched after Director.Init completes). Director.IsActive false → publishers no-op. (b) Director.Init call after 4 trainers enqueued. (c) `ReloadFromTier(playerId, tierSuffix)` wrapping `ProfilePersistence.Load` for rollback. (d) `GetModel(ModelId)` accessor. (e) **Attach `SmartIdentityStamp.Identity` at the typed-queue enqueue sites — post-audit cites (revalidate at impl time)**: TargetObservationSequenceBuffer.cs:~210 (IntentEvent DrainAndEnqueue body), LeadResidualRecorder.cs:~121 (ResidualEvent), LearningService.cs:~683 (ThreatEvent — was 694, drifted after BUG-026/046/022). **ActionValueEvent producer SHIPPED** in feature-expansion commit e9bfcd4: tee site is `ContinuousController.cs:514` enqueue inside `PublishActionValueEvent` (def at :444, called at :310); 4-tee inventory complete. No LearningService-side wiring needed beyond the `SmartIdentityStamp.Identity` attachment (already on s/a/r/s′ envelope via the calling tech context). (f) Shutdown: Director lifecycle is **in SmartRuntime.Shutdown** (before `TrainerBarrier.ClearAll`). `Director.BeginShutdown` calls `SmartEventBridge.UninstallDirectorPublishers()` synchronously BEFORE Director queues drain (prevents post-shutdown publish into draining queues). (g) **OnHostLost/OnHostGained use Interlocked Inc/Dec on `_acceptingDisableCount`** rather than clobbering the bare AcceptingTrainingEvents bool (V-fix BLOCKER) | 125 (was 95 — V-fix additions) |
| `Smart/Learning/LearningTuning.cs` | (B2 fix — own the OutcomeWeights design) Current file is ~24 LOC (only `UseFullBPTT`). Add `public static float[] OutcomeWeights = new float[(int)OutcomeKind.Count]` indexed by `(byte)OutcomeKind`. Initialize all entries to `1.0f` except `GuardViolation_Movement` / `GuardViolation_Role` / `GuardViolation_Combat` which default to `0.0f` (observe-only, guards mute reward channel by construction). Requires synthetic `OutcomeKind.Count` sentinel (or "highest defined value + 1" const) added to the `OutcomeKind` enum in `Smart/World/EventBus.cs`. Field is plain `static float[]` — read by `IdentityOutcomeConsumer` reward-aggregation path; if Director temp_adjust ever rewrites entries it must do so under the existing `_tuningSnapshotLock` documented in §12.1 DirectorTuning row. Survives checkpoint via §7 checkpoint-trio serialization | 12 |
| `Smart/Learning/IdentityOutcomeConsumer.cs` | **Bifurcated counter dicts (V-fix MAJOR)**: existing `_counters` ConcurrentDictionary preserved for non-guard outcomes; NEW `_guardCounters` ConcurrentDictionary for `OutcomeKind.GuardViolation_*` outcomes. `OnOutcome` routes by enum-range early-return — guard outcomes never reach the reward-aggregation surface by construction, eliminating the "forgot to filter at second call site" bug class. Add per-(identity,kind) `ConcurrentQueue<long ticks>` ring for `GetRatePerMin(identity, kind, windowSec)` and `GetWeightedRatePerMin({identities…}, kind, windowSec)` — window-state lives IN the consumer per BLOCKER-fix; trim entries to `now − windowSec` at read time. Capacity bounded by `expectedRate × windowSec × safetyFactor`. Returns `INSUFFICIENT_DATA` when no entries OR sampleCount < 3 in window (distinguishes "zero publishes" from "consumer cold"). **`_counters` preserved + reimplemented over ring snapshot count** so existing `GetCount(SmartIdentity, OutcomeKind)` (line 66) callers stay zero-churn back-compatible. Ignore `Source==Replay` envelopes (using IdentityOutcome.Source low-nibble bit). NEW: per-identity overload `LiveSmartTechCount(SmartIdentity identity)` referenced from §4 row — added on SmartRuntime side. `Snapshot()` returns populated (identity, kind, count, weighted_rate) triples | 150 (was 110 — bifurcation + ring + rate-window expansion) |
| `Smart/Learning/ProfilePersistence.cs` | `RestoreFromCheckpoint(modelName, slot)` routes existing `.previous`/`.penultimate` rotation (now at ProfilePersistence.cs:~195-249 post-BUG-046 TLV expansion). `EnumerateExpiredPreSnapshots(maxAge)` for Director's pre-action cleanup. Reuses existing byte format. **B3 RESOLVED (audit BUG-022)**: `ProfilePersistence.Save` now wraps `StoreParameters` + Adam-payload read under `lock (m.SaveMutex)` at ProfilePersistence.cs:118-126 (weights and M/V/T snapshot from one consistent moment). No further Director-side work required for B3. ArchitectureVersion Load-time guard already shipped (BUG-024) at LearningService.cs:272/610 + arch-mismatch keep-fresh-state at ProfilePersistence.cs:332; Director rollback's arch-version-validate layers ON TOP. AdamState TLV tag 0x0002 round-trip already serialized (BUG-046) via `GetAdamStateOrNull` + `BuildAdamPayload` at ProfilePersistence.cs:~265-314 (reflection-cached); Director `ReadAdamMoments` can reuse the helper. Atomic-write `File.Replace` swap with PlatformNotSupportedException fallback now lives at ProfilePersistence.cs:~231-249 (Step 4 of Save) — pattern reference for tunables.json / director-config.json siblings | 40 (was 60 — B3 work already shipped) |
| `Smart/Integration/SmartEventBridge.cs` | `InstallDirectorPublishers()`/`UninstallDirectorPublishers()`. One-line tee at KillScored publish (line ~329-330 post-audit drift; was 273 pre-BUG-052/053) to feed `IdentityReplayBank`. Per-minute `DamageObserved` `Interlocked.Increment` counter for `damage_events_per_min` constraint. **Audit BUG-053** already established a three-surface teardown pattern in `SmartEventBridge.Uninstall` (weapons + cargo per-tank + tee-points); Director publisher uninstall follows the same per-surface DetachAll pattern (BUG-052 WeaponFireBuffer.DetachAll already shipped — see `World/WeaponFireBuffer.cs`). The Generic-attacker filter `if (attackerIdentity != Identity.SmartIdentity.Generic)` is at line ~326 post-audit | 90 |
| `Smart/SmartForm.cs` | (a) `DrawPathingDebugGUI`: collapsible "Training Director" panel gated on `smart.director.gui.show` — when shown, the existing 280×182 GUI.Box at lines 643-645 expands to ~560×400 with `GUI.BeginScrollView(viewportRect, _directorPanelScrollPos, contentRect)` + persistent scroll position field (BLOCKER-fix: without expansion the 30-line digest clips to ~10 lines, hiding most of it). (b) `Operations`: drain main-thread `ScenarioSpawnRequest` + `ChunkSpawnRequest` + `ScenarioRelationsRequest` + `ScenarioAngerResetRequest` + `GuardEvaluationRequest` + `GuardWritePayload` queues (1-2 ScenarioSpawn + budget for guards) between `WorldEventBus.DrainMainThreadQueue` (line 434) and `LineOfSightProducer.MainThreadTick` (line 441). **Per-tick CompareExchange debounce on `_lastSpawnExecMs` (200 ms)** so per-tech-per-frame invocation (SmartForm.cs:472-479) collapses to one drain per interval — mirrors existing `ObserveWorldTechsIfDue` pattern. **Per-envelope try/catch wrapping** matching the per-tech exception isolation from Phase 4 (SmartForm.cs:500-508 pattern) so one bad envelope does NOT abort the drain. Per-envelope failure journals `SPAWN-FAILED` / `ENVELOPE-FAILED` and continues to the next envelope. Envelope-scenario-generation check rejects stale stamps. **No DirectiveInbox drain in Operations** — that runs on the `DirectorInbox` daemon | 180 (was 110 — V-fix: debounce, multiple new envelope types, scrollview expansion, per-envelope isolation) |
| `Smart/SmartRuntime.cs` | (a) `LiveSmartTechCount` property summing roster + per-identity overload `LiveSmartTechCount(SmartIdentity identity)` (both NEW per V-fix; current source has `EnumerateTeams()` at line ~1396 post-audit drift, was 1158 pre-BUG-080/014/051). O(teams) per call. (b) `TechLifeMinutesInWindow(SmartIdentity identity, int scenario, int windowSec)` accessor used by ConstraintEngine. (c) Director lifecycle plug at `Shutdown` (`SmartRuntime.cs:1120 public static void Shutdown()` per Stage-2 verification; Stage-1 said 1128, actual is 1120) between `WorkerHealthMonitor.BeginShutdown()` and `TrainerBarrier.ClearAll()` — call `Director.BeginShutdown()` which: (1) Interlocked.CompareExchange-latches a `_directorShutdownRequested` flag (mirror L-004 pattern); (2) sets `_killSwitch` to halt in-flight rollback; (3) blocks up to 10 s for in-flight rollback to drain; (4) calls `SmartEventBridge.UninstallDirectorPublishers()` synchronously; (5) calls `GuardCorrectiveActuator.ForceReleaseAll()` to clear mailbox slots before shutdown; (6) on 10 s timeout journals `SHUTDOWN-FORCED-WHILE-ROLLBACK-INFLIGHT` and continues. NEVER uses Thread.Abort. (d) `RequestDirectorPause()` / `ReleaseDirectorPause()` public surface. (e) `AcquirePauseToken(string owner)` / `ReleasePauseToken(string owner)` mutex on `_pausingOwner`. (f) `AcceptingTrainingEvents` becomes a computed property: `_acceptingDisableCount == 0 && IsHost` with public `AcceptingTrainingEvents_Inc()` / `_Dec()` — eliminates dual-writer clobber race between OnHostLost / OnHostGained / rollback's finally. (g) `IsHost` promoted from `internal` to `public` for future cross-DLL safety. (h) `IsDirectorPaused` accessor on `_directorPaused` for AUTO-HEALTHY checkpoint skip-condition. (i) GuardWorker / publisher access to per-tech SelfStateProbe via existing `SmartRuntime.LookupSelfStateProbe(TechId)` accessor (shipped feature-expansion; existing `_probeLookup` delegate pattern at `StrategicStateExtractor.cs:84` is the template). Audit BUG-028 already removed `IMovementAICore.AvoidAssist` from the interface — Director publishers / ChunkRegen must NOT assume the removed member. (j) Audit BUG-080 shipped `WorkerLifecycleRegistry.CancelAllAndJoin(TimeSpan)` with shared-deadline straggler logging at `WorkerLifecycleRegistry.cs:106-134`; Director.BeginShutdown can delegate to this surface for cleanly cancelling its 4 daemons rather than rolling its own loop | 130 (was 60) |
| `Smart/Threading/DaemonWatchdog.cs` | Extend `CanonicalRoster` hard-coded string[] (line 33-48, **currently 13 entries** post audit BUG-025/036: AetherFuser + GlobalPlanner + GlobalCoordinator + ThreatFieldRebuild + PathSolve + Trainer-Intent + Trainer-ActionValue + Trainer-Residual + Trainer-Threat + StrategicStateExtractor + TeamReaper + Autosave + TechLeakWatchdog) with `TrainingDirector`, `DirectorInbox`, `ScenarioWorker`, `ChunkRegen` → **17**. **Update docstring** from `9` (stale) → `17 long-running daemon label strings`. **STRIKE the AbortSurvivalTests.cs roster-count assertion update** — no such assertion exists; tests use named RegisterCanonical factory calls + functional asserts (verified absent). NEW: publish `DaemonRespawned(string daemonName, long monoTick, RespawnReason reason)` event from `AttemptRespawn` on success via WorldEventBus.PublishFromWorker (digest §6.2 `TRIG=respawn:<daemon>` needs an event source, not just LogWarnFileOnly). NEW: `IsTearingDown` is checked before respawn — already exists. **Init-order latch**: each daemon RunLoop checks `_directorInitComplete` and `WaitHandle.WaitOne(1000)` retry until set | 30 (was 15) |
| `Smart/World/EventBus.cs` | Add `Source` field (byte) to `IdentityOutcome` (existing readonly struct) with backcompat ctor — Source low-nibble = Live(0)/Replay(1); high-nibble = guard ordinal 0..10, 15=wildcard sentinel. **Update IdentityOutcomeConsumer.OnOutcome key packing** to `(Identity<<16) | (Kind<<8) | Source` so dominant-guard lookups can mask the high-nibble. Existing KillScored publish site updated explicitly with `Source=15<<4` (no-guard sentinel). Add 6 NEW event structs: `DirectiveAccepted`, `ConstraintViolated`, `InterventionApplied`, `ChunkSpawnRequest`, `ScenarioSpawnRequest`, `GuardEvaluationRequest` (BLOCKER-fix: original "3 new structs" missed the 3 main-thread-marshal envelopes referenced throughout §11, §12.1, §9.2). Also add `ScenarioRelationsRequest`, `ScenarioAngerResetRequest`, `GuardWritePayload`, `DaemonRespawned`, `TechDespawned`, `LearningService.ProfileSaved`. **Feature-expansion (e9bfcd4) already shipped** StrategicStateVector double-buffer, CargoStatePublisher, NearestTechCache, WeaponFireBuffer, EnemyVehicleSnapshots (BUG-026), DamageHintBuffer 7th DamageType byte field (BUG-046), HealthSidecar HP-history ring — Director-side envelopes ride on top of those, NOT re-defined. **Extend `EventBus.ClearAll` subscriber-clear list** with new `ClearSubscribers<T>()` calls for all NEW event types — currently hand-rolled enumeration, not registry-pattern. Add NEW OutcomeKind value `BaseLost=4` (MAJOR-fix: paired publisher referenced in §4 + §10 but missing from manifest). Total OutcomeKind cardinality after additions: 8 (KillScored=0, BaseHeld=1, BlockDelivered=2, AllyProtected=3, BaseLost=4, GuardViolation_Movement=5, GuardViolation_Role=6, GuardViolation_Combat=7). OutcomeKind enum location at `EventBus.cs:~163-169` post-audit (was ~151-157 pre-feature-expansion shifts) | 80 (was 35) |
| `Smart/Tooling/SmartConsoleCommands.cs` | No structural change. Director `[DevCommand]`s live in their own sibling class `SmartDirectorConsoleCommands.cs`. Verified: `SmartConsoleCommands.cs:28` is plain non-partial `public static class`; DevCommands attribute scanner is class-agnostic (DevCommandAttribute.cs:5 targets methods). KickStart.ModCommandID = "TAC_AI" (KickStart.cs:30) so command prefix is `TAC_AI.smart.director.*` | 0 |
| `AI/Tunables/Catalog/TrainingModeTunables.cs` | **No Director-side additions** here. `DirectorTunables.cs` owns all `smart.director.*`. Avoids double-registration ambiguity. (Was: planned to register director keys here — dropped per Lens C major) | 0 |
| `Templates/RawTechLoader.cs` | (a) Expose `SpawnScenarioMobileTech(pos, fwd, team, RawTechPopParams, scenarioId)` wrapping `SpawnMobileTechPrefab` + tagging Tank with `§DIR-<scenarioId>-<n>` suffix. **Must be called from main thread** (called by the main-thread executor draining `ScenarioSpawnRequest` envelopes — not from ScenarioWorker directly). (b) NEW: `int MatchCount(RawTechPopParams filter)` accessor used by S4 graceful-degrade gate. (c) Document at `RawTechLoader.cs:1087` that `filter.ForceAnchor` is unconditionally wiped for mobile (rules out anchored escort framing in S3) | 45 |
| `TAC_AI.csproj` | Old-style csproj — register every new `.cs` (~31 base + 20 guard = 51 new). PowerShell-created files need `Read` before `Edit` per memory | 110 |
| `Smart/AI/TankAIHelper.cs` (NEWLY EXPLICIT) | All ring writes happen on main thread inside `UpdatePhysicsInfo()` (existing per-AI-tick site at ~10 Hz per AIClockPeriod) — NEVER from GuardWorker background thread (V-fix thread-affinity: `transform.forward`/`rb.velocity` are Unity main-thread-only). GuardWorker reads rings via snapshot pattern. **NaN guard mirrors existing line 44-46** (`if (tank.rbody.IsNotNull())`). Reset hooks at all existing `recentSpeed = 1;` sites (lines 906, 1263). **Update Physics.cs:7 stale comment** to reflect new fields actually exist post-impl. (a) `recentSpeedY` new 4 Hz ring (480 slots = 120s × 4Hz, sized for largest consumer Homesickness 120s; sub-window queries via window-length param). **(a2) `recentSpeedSigned`** ring (V-fix BLOCKER: tick = `Vector3.Dot(rb.velocity, transform.forward)`). **(a3) `recentSpeedXZ`** ring (V-fix BLOCKER: tick = `Vector3.ProjectOnPlane(rb.velocity, Vector3.up).magnitude`). All three share the 480-slot window depth (~125 KB total at 64-tech LRU cap, single source of truth across guards). (b) `WedgeNetProgressCount(secondsAgo)` (V-fix MAJOR: original "tick counter" was fictional — `MakingNetProgress` at Physics.cs:25 is a single bool. Implementation: NEW `bool[] _netProgressRing` (30 slots × 1 Hz) advanced inside UpdatePhysicsInfo when `netProgressNextCheck` fires; getter scans window). (c) `LastBlockDeliveredMono` / `LastAllySightingMono` / `AssignedAllyTechId` / `LastForceAscendMono` fields. (d) **`GuardInjectedGoals` as fixed-size array `GuardInjectedGoalSlot[HardCorrectGuardCount]` PER-TECH**, indexed by `(byte)GuardOrdinal` restricted to hard-correct ordinals only (AntiCrash + GroundedAircraft) — NOT a `ConcurrentDictionary<int techId, ...>` (BLOCKER-fix: original shape was contradictory — "per-tech mailbox" but keyed by techId; also collided with §12.2 DirectorState global `GuardInjectedGoalTable` — STRIKE that DirectorState entry). **Soft-corrects (OverdueDelivery) do NOT have slots** — V-fix telemetry-only reconciliation: soft-correct guards publish `IdentityOutcome` for training-signal only and never write the mailbox. The `GuardOrdinal` enum keeps OverdueDelivery as a value for `IdentityOutcome.Source` high-nibble attribution, but the array indexer skips it (mapping held in `GuardRegistry.HardCorrectGuardOrdinals` static table; non-hard-correct ordinals return -1 = no slot). **`GuardInjectedGoalSlot` is a `class` (R3-B2): `Interlocked.Exchange<T>` requires `T : class`. Writers (GuardCorrectiveActuator) `Interlocked.Exchange<GuardInjectedGoalSlot>(ref slot, newInstance)` their own slot; eviction-clear via `Interlocked.CompareExchange(ref slot, null, expectedStale)` so a fresh write that races the clear is preserved**. Reader (`ContinuousController.OnOperationsTick`, single reader) iterates slots in §9.7 priority order across hard-correct slots only. **Mailbox cleanup uses existing `IAIForm.OnTechRecycle(helper)` hook at TankAIHelper.cs:967** (RM-9 fix: do NOT subscribe to `Tank.TankRecycledEvent` for mailbox slot cleanup — `IAIForm.OnTechRecycle` is the project's existing recycle pattern, used pervasively by `ConcurrentDictionary<TechId, _>` cleanup; TechId keying preserved — `TechId.FromTank` at `BeliefState.cs:13-43` has SP fallback `tank.GetInstanceID()` when no netTech, and TechId is a non-nullable readonly struct, never null). **Recycled cleanup (R3-M5 reduced scope — explicit cleanup for the NEW fields the plan adds):** in `Recycled()` null all `GuardInjectedGoals` slots; zero `recentSpeedSigned`/`recentSpeedXZ`/`recentSpeedY` ring contents, reset cursor, reset wrapped flag; `PublishedFrustrationMeter = 0f`; identity stamp via existing `Active?.OnTechRecycle(this)` pattern. (Do NOT expand to fixing pre-existing recentSpeed/EstTopSped/FrustrationMeter gaps — adversarial confirmed these re-init in active life paths before consumers read.) (e) `GetCargoFraction()` accessor aggregating `ModuleItemHolder.NumContents` across holders divided by `holder.NumStacks * m_CapacityPerStack` (m_CapacityPerStack private; use reflection or extension). (f) Cached `MoveStyle` classification result. (g) `LastAppliedGoalKind` (byte, written by `ContinuousController` after hard-correct substitution — kinematic-detection sidecar for `GuardCorrectionInjected` attribution on hard-corrects; soft-correct OverdueDelivery never substitutes per V-fix telemetry-only reconciliation, so it does not write this field). (h) NEW `volatile int PublishedFrustrationMeter` companion field written from UpdatePhysicsInfo on main thread, read by GuardWorker (V-fix thread-affinity: bare `FrustrationMeter` non-volatile int reads from bg thread can torn-read on x86 32-bit). (i) Elevate `lastDestinationCore` from `internal` (line 342) to `public` — guard accesses it. (j) NEW `Vector3 GuardLastDestination => ControlCore.lastDestination` if elevation is undesirable | 160 (was 80 — undercount on rings, ring depth, net-progress ring, mailbox array, PublishedFrustrationMeter, NaN guard, reset hooks) |
| `Smart/Control/ContinuousController.cs` (NEWLY EXPLICIT, Validator-3 BLOCKER fix) | `OnOperationsTick` modified per §9.7 priority chain. **CRITICAL: AntiCrash check inserted BEFORE existing external-goal read at line 208** (BLOCKER-fix: Coordinator's external goal at line 208-217 short-circuits past Identity branch; AntiCrash placed after Produce would never fire during coordinated engagement → burning aircraft crashes). New ordering: (0) check `helper.GuardInjectedGoals[(byte)GuardId.GroundedAircraft]` for non-expired slot — if present, `goal = slot.Goal; _lastTickHadExternalGoal = (external != null);` (preserves Adam-reseed semantics); (1) existing `external = _readExternalGoal?.Invoke()`; (2) existing identity Produce; (3) **OverdueDelivery is TELEMETRY ONLY — never reads the mailbox** (V-fix telemetry-only reconciliation: no slot exists for soft-corrects; guard emits `IdentityOutcome` directly from `OverdueDeliveryGuard` via `BaseReturnTelemetryObserver`). Stale-slot watchdog: rejects hard-correct slots where `MonoClock.Now() > slot.ExpiresMono + (long)(2.0 * Stopwatch.Frequency)` and increments `[GUARD-MAILBOX-STALE]` counter. Combat / unjam FSM detection done from `helper.Provoked` + `helper.lastEnemyGet` + `helper.PublishedFrustrationMeter` (V-fix volatile snapshot) reads. NaN guard mirroring lines 250-255 — if mailbox returns non-finite Position, fall back to `TacticalGoal.AtCurrent(beliefPos, currentHeading)` | 70 (was 50 — pre-external insertion + watchdog + NaN guard) |
| `Smart/Director/Scenarios/ChunkRegenWorker.cs` | (Guard refactor) Extract `Physics.OverlapSphere` chunk census into static helper `GathererCensus.ChunksInRange(pos, radius, layerMask, out array)` so `IdleGathererGuard` can reuse without duplicating LayerMask + fallback logic | 10 |
| `Smart/Director/Publishers/AllyProtectionPublisher.cs` | (Guard refactor) Expose read-only `bool TryGetPairedAlly(int supportTechId, out int allyTechId)` for `LostAllyGuard`. **Additionally expose `long LastAllyLossMono(int supportTechId)`** (Validator-1 MINOR fix: LostAlly must not fire when the paired ally died mid-window). Track paired-ally death via a small `Dictionary<int, long>` updated on tech-destroyed events for paired techs. Existing pairing data; no new computation beyond the death-timestamp map | 25 |
| `Smart/Director/Publishers/BlockDeliveredPublisher.cs` | (Guard refactor) Expose `GetLastDeliveryTickMono(int techId)` and `GetCargoFillRatio(int techId)` helpers used by `IdleGathererGuard` + `OverdueDeliveryGuard`. Aggregates existing `ModuleItemHolder` iteration into a per-tech cache invalidated on `TakeItemEvent` | 30 |
| `Smart/Director/ConstraintRegistry.cs` | (Guard extension) Append 4 new `Constraint` structs: `movement_pathology_rate`, `role_task_pathology_rate`, `combat_pathology_rate`, `grounded_aircraft_intervention_rate`. Constraint count: 11 → 15. `ScenarioRegistry` recipes extended to declare expected-guard-fires-per-min per scenario for `INSUFFICIENT_DATA` gating (e.g. S1 expects movement_pathology ≥ 0.01/min from terrain interaction noise) | 90 |
| `Smart/Director/ConstraintEngine.cs` | (Guard extension) Plumb a new `INSUFFICIENT_DATA` criterion: guard-tick-sample-count < 30 per guard-rate constraint. Reuses existing 5-min observation-window logic. Bucketing: sum `IdentityOutcome` `GuardViolation_*` counts filtered by `Source`-byte high-nibble (guard ordinal), then divide by tech-minutes from `SmartRuntime` tech-life accounting | 40 |
| `Smart/Director/DirectorState.cs` | (Guard extension) Add `GuardCooldownTable` (`ConcurrentDictionary<int techId, Dictionary<GuardId, long cooldownUntilMono>>`) + `GuardViolationCounters` for digest sampling. **STRIKE `GuardInjectedGoalTable`** — was a global-mailbox alternative that conflicted with the per-tech `TankAIHelper.GuardInjectedGoals` array (V-fix BLOCKER on mailbox shape: pick per-tech only). `ScenarioOwnedTeams` ConcurrentDictionary + NEW `ScenarioOwnedTechs` (V-fix MAJOR: replaces fragile Tank.name-suffix tagging) + `ScenarioGeneration` int (incremented on every scenario_set / scenario_respawn, stamped onto envelopes to invalidate in-flight stale spawns post-rollback). **Rollback reset surface**: expose `ResetGuardObservationState()` cleared by §7.3 step 6.5 — calls `GuardCorrectiveActuator.ForceReleaseAll()` (clears per-tech GuardInjectedGoals slots) + `GuardViolationCounters`, PRESERVES `GuardCooldownTable` clamped to ≤ 2 s so safety-net can re-arm if conditions persist (V-fix). All cleared on world reset. **`LatestDigest` field MUST be `volatile string`** — atomic ref-swap from background DigestBuilder; main-thread OnGUI reads as immutable string ref (V-fix MAJOR thread-safety) | 60 (was 45 — generation, ScenarioOwnedTechs, volatile-string, struck GuardInjectedGoalTable) |
| `Smart/Director/DigestBuilder.cs` | (Guard extension) Render the GUARDS section between IDENTITY-RATES and SYSTEM. Format populated cells only. Surface `[obs]` / `(W)` / `(SC/tele)` / `(HC)` / `(!)` / `oscill` markers (V-fix telemetry-only reconciliation: `(SC)` is `(SC/tele)` — soft-correct never substitutes, always telemetry; `won_arb` STRUCK — soft-corrects do not enter arbitration). **Distribution rollup** (Validator-2 MINOR fix): replace `pathology_total=N` integer with `pathology_total=N distinct_techs=N max_per_tech=N p90_per_tech=N soft_corrects=N (telemetry-only) hard_corrects=N` so operator can distinguish broad mild texture from concentrated regression. **Override-rate auto-surface** (Validator-1 INFO fix): if `learned_model_overrode_guard_count(guard) / fired_count(guard) > 0.40 over 10 min AND fired_count > 5`, emit `[GUARD-MAYBE-FALSE-POSITIVE]:<guardName>:override_rate=N%` digest line. **Mode-collapse warning** (Validator-1 fix): when `hard_correct_on_active_target_count > 0`, emit `[GUARD-AIRCRAFT-DIVE-INTERRUPT]:N`. **RM-3 AllyProtection LRU pressure**: render an `ally_protect_evict_per_min=N` line in the IDENTITY-RATES section sourced from `AllyProtectionPublisher.evictionsPerMinute` so operator can monitor whether sustained pressure on the 128-pair LRU indicates a need to upsize (nominal peak 80-100 pairs in ragnarok per KickStart caps). Update LEGEND. JSON sibling gains `guards: { <identity>: { <guard_name>: { rate, severity, samples, fires_in_window, weighted_contribution } } }` block populated from same `DigestSnapshot.GuardRates` | 90 |
| `Smart/Director/DirectorJournal.cs` | (Guard extension) Add journal entry kinds: `GuardFired` (observe/warn), `GuardCorrectionApplied` (soft/hard), `GuardCorrectionReleased`, `GuardCorrectionYielded` (yielded to learned policy), `GuardAircraftChronic` (3+ fires in 60 s suppression) | 25 |
| `Smart/Director/DirectorTunables.cs` | (Guard extension) Register `smart.director.guard.*` tunables: per-guard `window_sec`, `threshold`, `cooldown_sec`; per-bucket constraint band; `guard_override_qmargin` (default 0.05, hard-correct only — V-fix telemetry-only reconciliation: soft-corrects do not enter Q-margin arbitration); `guards.enabled` master + per-guard `severity` override; `forceascend_target_altitude` (30 m); `forceascend_duration` (5 s). **STRUCK** under V-fix telemetry-only reconciliation: `returntobase_lifetime` (45 s — no mailbox slot for OverdueDelivery), `smart.director.guards.overdue_delivery_injection_delay_sec` (15s — no injection to delay), `smart.director.guard.overdue_delivery_override_cooldown_sec` (120s — no override to cool down). **NEW** under telemetry-only: `smart.director.guards.overdue_delivery_refire_cooldown_sec` (30s, prevents duplicate `IdentityOutcome` emissions per single overdue episode). **Validator-pass new tunables (retained)**: `smart.director.guards.aircraft_release_ramp_sec` (2.0 s, Validator-2 ramp-down handoff); `smart.director.guard.overextended_hunter.leash_m` (250, Validator-3 BLOCKER fix replacing the fictional `AILimitSettings.MaxLeashRange`); `smart.director.guards.aircraft_chronic_window_sec` (90, Validator-2 — was hard-coded 60). ~32 keys total (was ~35; net -3 from telemetry-only reconciliation) | 75 (was 80) |
| `Smart/Director/Actions/ReplayBankAction.cs` | (Guard extension) Pass-through tee of replay envelopes filters `GuardViolation_*` so guard observations are NOT re-emitted on replay (those are observation outcomes, not training events; they belong only to the constraint engine, not the trainer queues). Cause attribution names dominant sub-guard: `cause=role_pathology:IdleGatherer=0.045` | 15 |
| `Smart/Tooling/SmartDirectorConsoleCommands.cs` | (Guard extension) Add `[DevCommand]`s: `smart.director.guards.list`, `smart.director.guards.snapshot <techId>`, `smart.director.guards.suppress <guardName>` (Host+Cheat, for spawn-testing — emits big warning in digest), `smart.director.guards.force <guardName> <techId>` (manual fire for testing) | 60 |
| `Smart/Threading/DaemonWatchdog.cs` | (Guard extension) Extend `CanonicalRoster` with `GuardWorker`. Update header docstring "17 long-running daemon label strings" → "18 long-running daemon label strings". **STRIKE the AbortSurvivalTests.cs update** — V-fix confirmed no hard-coded count assertion exists in the test file | 8 |
| `Smart/Integration/SmartEventBridge.cs` | (Guard extension) `GuardWorker` subscribes to `WorldEventBus.DamageObserved` (for `FriendlyFireGuard` team-attacker filter) and `KillScored` (for `OverextendedHunterGuard` post-kill exception). Single unified entry point `SmartEventBridge.InstallDirectorPublishers()` fires all 4 publisher sources (3 OutcomeKind publishers + GuardWorker) — reconciles narrative "4 publisher sources" vs prior row "3 publishers" (V-fix MINOR). **STRIKE the "Tee guard fires to IdentityReplayBank"** — guard fires publish `IdentityOutcome` with `GuardViolation_*` OutcomeKind which feeds `IdentityOutcomeConsumer._guardCounters` for the constraint engine. NOT IdentityReplayBank (which is for training-event replay). V-fix MAJOR confused these two systems | 30 (was 25) |
| `Smart/Learning/IdentityOutcomeConsumer.cs` | (Guard extension) `GetRatePerMin` gains an optional `byte guardFilterByte = 15` parameter to filter by `Source`-byte high-nibble (Validator-3 MAJOR + Validator-1 MINOR: default sentinel 15 = no filter; was 0, which collided with valid guard ordinal 0). Used to bucket guard events by `guardName` without expanding `OutcomeKind` enum. When `(Source>>4) & 0x0F != 15`, filter against guard byte map. Backcompat default 15 preserves existing call sites. **New accessor `GetDominantGuardId(OutcomeKind bucket, int scenario, int windowSec)`** (Validator-2 MAJOR fix) returns the guard ordinal contributing the most violations in the bucket; ActionPicker uses this for sub-guard-aware action selection in §4 movement/role/combat constraint chains. Snapshot() includes per-(identity, guard_name, scenario) breakdown for digest | 50 |
| `Smart/World/EventBus.cs` | (Guard extension) Add 3 new `OutcomeKind` enum values: `GuardViolation_Movement`, `GuardViolation_Role`, `GuardViolation_Combat`. Existing `Source` byte (added by base plan for Live/Replay) gains documented high-nibble bits 4–7 reserved for guard ordinal — revised per Validator-3 MAJOR + Validator-1 MINOR: ordinals `0..10` map to the 11 active guards (0=BackwardsLock, … 10=OverextendedHunter); `15 = wildcard / no-filter` sentinel; `11..14` reserved for future expansion. Non-guard publishers set high-nibble to `15`. Unit-test `Smart/Tests/GuardOrdinalTest.cs` asserts `GuardRegistry.GuardCount <= 14`. NO new event struct; const table + comment only | 20 |
| `Smart/SmartRuntime.cs` | (Guard extension) Add `TechLifeMinutesInWindow(SmartIdentity identity, int scenario, int windowSec)` accessor used by `ConstraintEngine` for tech-minutes normalisation of guard-rate constraints. Reuses existing roster enumeration | 25 |
| `Smart/Director/TrainingDirector.cs` | (Guard extension) **`BehaviorGuardWorker.Tick(deltaSec)` is NOT called from TrainingDirector** (Validator-3 BLOCKER fix: prior draft contradicted §9.2 daemon registration; GuardWorker owns its own 1 Hz daemon thread). Director only consumes guard-emitted `GuardViolation_*` outcomes through `IdentityOutcomeConsumer` like any other publisher. ActionPicker `dependsOnPublisher_weight` table extended for `GuardViolation_*` outcome kinds. **Sub-guard-aware action selection** (Validator-2 MAJOR fix): before picking an action for any of the 3 new bucket constraints (movement/role/combat), the Director queries `IdentityOutcomeConsumer.GetDominantGuardId(bucket, scenario, 300s)` and branches per dominant guard (chain documented in §4). Post-rollback `STANDING-DIRECTIVES-REAPPLIED` block extended to also list guard-bound directives, plus a new `GUARD-WINDOWS-RESET` block emitted from §7.3 step 6.5 | 60 |

### 12.3 Total LOC

Re-audited per Validator-3 MAJOR finding and the post-verification (3-verifier) pass
which surfaced additional undercounts in §12.2 modifications (V-fix MAJOR).

  * **New files**: ~6 440 (base) + ~2 980 (guard layer; was ~3 130 — ~150 LOC drop
    from OverdueDelivery telemetry-only reconciliation: BaseReturnGoalSource.cs
    shrunk to BaseReturnTelemetryObserver.cs at ~30 LOC (was ~130);
    OverdueDeliveryGuard.cs trimmed to ~110 LOC (was ~180) — no injection-delay
    timer, no Q-margin override gate, no cooldown tracking; GuardCorrectiveActuator.cs
    trimmed to ~200 LOC (was ~240) — RaiseBaseReturn surface dropped) + ~40
    (IsReachable / LastQueryFailedRecently helpers ADDED onto the pre-existing
    PathingService.cs — note PathingService.cs is NOT a new file in v1 anymore
    (it already exists with the TerrainPublication atomic-swap pattern per audit
    BUG-013/050 at `_terrainPub` line 102 and one-time `_worldResetRegistered`
    latch per BUG-051 at lines 111+150; M1 BUG-004 fix already shipped the
    daemon-factory registration at PathingService.cs:170-173, so no further
    Director-side LOC is needed). R3-B1: `IsReachable(origin, dest, capability)`
    routes through `TerrainMap.IsTraversable` O(1) DoubleBuffer read
    (ITerrainMap.cs:44); optional A* via existing `PathingService.PathSolveLoop`
    enqueue + small result cache; guard with `TerrainMap.IsFreshlyAllocated` →
    "unknown / assume-reachable" until first refresh. Do NOT call AIEPathMapper /
    AIEAutoPather2D/3D from background threads — non-thread-safe.)
    + ~80 (NEW ProjectileFiredPublisher per V-fix; was fictional
    `FireControl.ProjectilesFiredCount`) + ~20 (Stage-2 reconciliation: BlockDeliveredPublisher
    re-credited from 180 → 240 to match the §10 row's Release/Pickup pairing +
    non-Smart spam filter + Visible recycle hook spec) = **~9 560** (was ~9 730;
    PathingService LOC dropped from 80 to 40 because file already exists post-audit;
    BlockDelivered LOC re-credited per Stage-2 cross-section finding so the §12.1
    manifest entry and §10 publisher row agree at 240).
  * **Modified files** (deltas): ~975 (base) + ~690 (guard) + V-fix adjustments:
    +40 OnlineTrainer (170 from 130), +10 OpponentIntentClassifier (60 from 50),
    +10 ActionValueEstimator (35 from 25), +7 TrajectoryResidual (18 from 12),
    +7 ThreatAssessmentModel (25 from 18), +30 LearningService (125 from 95),
    +40 IdentityOutcomeConsumer (150 from 110), +70 SmartForm (180 from 110),
    +70 SmartRuntime (130 from 60), +15 DaemonWatchdog (30 from 15),
    +45 EventBus (80 from 35), +20 ContinuousController (70 from 50),
    +80 TankAIHelper (160 from 80), +40 SmartDirectorConsoleCommands aliases,
    +15 DirectorState (60 from 45), -20 ProfilePersistence (40 from 60 —
    B3 SaveMutex wrap shipped via BUG-022) = **~2 130** (was ~2 150 pre-audit
    reconciliation; was ~1 665 pre-V-fix).
  * **Grand total: ~11 690 LOC** (was ~11 730; post-feature-expansion + post-78-bug
    audit baseline reconciliation dropped ~40 LOC — PathingService.cs goes from 80
    to 40 because the file already exists with TerrainPublication atomic-swap +
    daemon-factory registration shipped via BUG-004/013/050/051; ProfilePersistence
    row drops from 60 to 40 because B3 SaveMutex wrap shipped via BUG-022; ~20 LOC
    of audit-resolved scaffolding subtracted from miscellaneous Director-side
    edits). **52 new files** unchanged (BaseReturnGoalSource.cs renamed in place
    to BaseReturnTelemetryObserver.cs — same file slot, repurposed; PathingService.cs
    moves from "new file" to "modified file" entry per audit baseline).
    **19 distinct modified-file paths** unchanged.
  * **Critical reservation**: many V-fix bumps are bookkeeping correctness;
    a precision spawn-test pass at implementation time may compress where
    boilerplate was overestimated. Treat ~11 690 as upper bound.
  * **Test-only LOC (amendment A3 — separate from core v1 totals)**: ~400 LOC
    synthetic-pathology selftest scaffolding (`smart.director.guards.selftest`
    DevCommand + per-guard injection recipes, ~30-50 LOC × 11 guards) + ~20 LOC
    console-command dispatcher row (folded into the SmartDirectorConsoleCommands
    280 row, additive). Treat as test-only, not core v1 — does not contribute
    to the ~11 690 grand total above.

---

## 13. Key design decisions

  * **3 layers × 5 daemons** (`TrainingDirector`, `DirectorInbox`, `ScenarioWorker`,
    `ChunkRegen`, `BehaviorGuardWorker`). Director is a SINGLE 1 Hz daemon, not 4
    per-model daemons. All register via `DaemonWatchdog.RegisterCanonical` +
    `WorkerHealthMonitor.RegisterCanonical`, mirroring `TrainerWorker`. Zero new
    lifecycle code; free auto-respawn. (Reconciled with §9.2 + §12.2 DaemonWatchdog
    row, both of which already enumerate 18 = 13 base + 5 new with GuardWorker.)
  * **Exactly 7 verbs** (`lr_scale`, `temp_adjust`, `replay_bank`, `freeze_model`,
    `scenario_set`, `scenario_respawn`, `rollback`). Operator framing was 5-7.
    Rejected: `freeze_layer` (single-layer GRU/MLP have no sub-layer split),
    `curriculum_swap` (collapsed into `scenario_set`), `reset` (rollback memory tier
    covers catastrophic restore).
  * **`temp_adjust` is Intent-only.** `SamplingMPC.SigmaThrottle/Steer/Brake` is a
    controller exploration knob and has no causal link to `ActionValueEstimator`'s
    scalar-Q loss-variance (verified `ActionValueEstimator.cs:84-96` + `SamplingMPC.cs:130-132`).
    Pairing them violates the "documented per-action effect" contract.
  * **Reward shaping via `OutcomeWeights[OutcomeKind]`** in `LearningTuning` rather
    than a per-identity persistent boost — continuous knob composes better with the
    constraint engine, survives in checkpoints, rollback-clean. **B2 fix: this field
    does NOT currently exist** — the plan adds it explicitly. `Smart/Learning/LearningTuning.cs`
    (today: ~24 LOC, only `UseFullBPTT`) gains
    `static float[] OutcomeWeights = new float[(int)OutcomeKind.Count]` indexed by
    `(byte)OutcomeKind`. Initialize all to `1.0f` except `GuardViolation_*` kinds which
    default to `0.0f` (observe-only). Requires a synthetic `OutcomeKind.Count` max-value
    sentinel (or "highest defined value + 1" const). See §12.2 LearningTuning.cs row.
  * **TrainerStats lives sidecar** outside model classes — never persisted; reset on
    host-loss. Single-writer / single-reader; no locks beyond model.SaveMutex pass-through.
    Pattern mirrors `PathRequestBackpressure._latencyReservoir`.
  * **Rollback is 3-tier** (memory ring of 3 healthy + 8 pre-action / `.previous` +
    `.penultimate` / Director-named). Capture is `lock(model.SaveMutex)` single-critical-section
    atomic. `ArchitectureVersion` validated on restore (refuses cross-version with
    `ROLLBACK-SKIPPED-ARCH` journal entry).
  * **DirectiveTable is intentionally NOT rolled back.** Rolling back operator
    decisions inverts the safety property the operator may have rolled back PRECISELY
    to clear the consequences of an earlier directive. The forced post-rollback digest
    emits a `STANDING-DIRECTIVES-REAPPLIED` line listing directives that will
    re-clobber the restored state — operator can rescind in the same breath.
  * **Director uses its OWN pause channel**, not the host-loss `TrainerBarrier` path
    directly. `_directorPaused` is added next to `_hostLost` on `TrainerWorker`;
    `TrainerBarrier.AcquirePauseToken(owner)` serialises rollback vs host-change races
    via mutual exclusion on `_pausingOwner`.
  * **ScenarioWorker + ChunkRegenWorker are PLANNERS**, not main-thread callers. They
    compute requests on the background tick; the actual `RawTechLoader.SpawnMobileTechPrefab`
    / `ManLooseBlocks.HostSpawnChunk` invocation runs on the main thread inside
    `SmartForm.Operations`. Unity `Transform.SetParent` would throw from a background
    thread (`ManLooseBlocks.cs:1166`); the `DaemonWatchdog` respawn cap = 5 makes that
    failure permanent.
  * **S2 SubNeutral viability is conditional on anti-degradation.** `BeSubNeutral`
    suppression at `RCore.cs:802-808` only holds while `Provoked == 0 ∧
    EnemyTeamData.angerThreshold < AIGlobals.DamageAngerDropRelations` (M2 fix —
    distinguish the two: **trigger constant** `AIGlobals.DamageAngerDropRelations = 2500f`
    at `AIGlobals.cs:427` is the comparison threshold; **per-team accumulator**
    `EnemyTeamData.angerThreshold` (public float at `ManBaseTeams.cs:159`) is the
    rolling counter. They are orthogonal — the constant is unchanged; ScenarioWorker
    re-anchors by zeroing the per-team accumulator field on each 1 Hz tick).
    ScenarioWorker calls `ManBaseTeams.SetRelations(a, b, SubNeutral)` + zeroes
    `EnemyTeamData.angerThreshold` between every Director-owned SubNeutral pair on
    each 1 Hz tick. New constraint `subneutral_relations_intact` surfaces if
    degradation slips through.
  * **S5 is AITeammate, not SubNeutral.** They are different `TeamRelations` enum
    values (`AIGlobals.cs:916` mints SubNeutral; `ManBaseTeams.cs:845-867` mints
    AITeammate). Picking AITeammate aligns the gathering team with the player; the
    "SubNeutral peace-with-neighbours" framing is dropped for S5.
  * **S6 Convoy Escort rejected on absence of a waypoint authoring API** (NOT on
    absence of `BasePurpose.NotStationary` — that enum slot DOES exist and the
    spawner branches on it at `RawTechLoader.cs:228`). S6′ uses `PatrolGoalSource`
    for ring patrol — verified composable from existing primitives.
  * **No "Aegis identity".** `AIType.Aegis` is a TerraTech engine-level AICore
    selector, not a `SmartIdentity` enum value (verified
    `Smart/Identity/SmartIdentity.cs:14-25`). S3/S5 escort training targets
    `RepairSupport` (Shield-bearing) and `Patrol` (solo armed). The `ally_protected_*`
    constraint reads identity from `{AircraftSupport, RepairSupport, Patrol}`.
  * **`BlockDelivered` subscribes to `ModuleItemHolder.TakeItemEvent`**, NOT
    `ModuleHarvestReciever.OnTaken`. The latter is an empty stub
    (`ModuleHarvestReciever.cs:34`); the former is the real engine event
    (`ModuleItemHolder.cs:422,581`). The 1 Hz bag-count poll fallback is dropped —
    its noise floor would sit at the constraint band.
  * **`BaseHeld` is paired with `BaseLost`.** Constraint metric =
    `BaseHeld / (BaseHeld + BaseLost)` so a destroyed base counts against retention,
    not just absent. `INSUFFICIENT_DATA` if `(held+lost) < 3`.
  * **`AllyProtected` is a graded magnitude** (`max(0, 1 - friendlyDamageInWindow /
    damageBudget=500)`), not all-or-nothing. The original `damageInWindow == 0` gate
    produced permanently unreachable rates under realistic combat.
  * **All inference-side tunables are read-once-per-batch.** `OpponentIntentClassifier.Evaluate`
    and `TrainOneMinibatch_FullBptt` snapshot `LearningTuning.IntentTemperature` at
    entry into a local; that local governs the entire batch. Without this, LossBefore
    and LossAfter could compute at different tau and the `LossReservoir` entries are
    garbage.
  * **`actionvalue_loss_variance_ratio` uses `LossBefore` and reward-magnitude normalises.**
    `LossAfter` is computed on the same batch the gradient just stepped on — biased
    low (`ActionValueEstimator.cs:124`). The TD-error variance signal lives in
    `LossBefore`. Reward-scale normalisation makes the band reward-magnitude-invariant
    so HQ-destruction reward spikes don't false-trip. **Audit BUG-011** corrected
    the Q-target to use max_a' Q(s', a') (true Q-learning) — was SARSA-style — at
    ActionValueEstimator.cs:155-199; the variance ratio now reflects Q-learning
    TD error, not on-policy bootstrap.
  * **Precondition surfaces shipped (feature-expansion e9bfcd4 + 78-bug audit).**
    StrategicStateVector + StrategicStateExtractor daemon + SelfStateProbe +
    StrategicStateBuffer + ActionValue producer (`ContinuousController.cs:444/514`)
    + ArchitectureVersion v3 across all 4 models + DamageHintBuffer 7th DamageType
    byte field + HealthSidecar HP-history ring + CargoStatePublisher +
    NearestTechCache + WeaponFireBuffer + EnemyVehicleSnapshots (BUG-026) are in
    the live build. Director publishers in §10 reuse these surfaces rather than
    re-spec from scratch. SelfStateProbe LastAttacker latch has falling-edge clear
    (BUG-010) so guards relying on `HasLastAttacker` get a stable signal.
    StrategicStateExtractor `FillResidualSlots` (BUG-009) fills v3 slots so
    Director consumers reading the strategic vector see no zero-padded holes.
    Cross-version rollback to a pre-bump T1 snapshot hits the arch-version skip
    path (BUG-024 Load guard at ProfilePersistence.cs:332); the plan's
    `ROLLBACK-SKIPPED-ARCH` journal entry is the right behavior.
  * **Entropy band is a ratio of max-entropy**, `band ∈ [0.30, 0.85] × ln(OutDim)`.
    Architecture-invariant when `OutDim` changes. Cold-start warmup = 1 000 minibatch
    steps (otherwise a fresh GRU sitting near uniform trips the band immediately).
  * **`‖Δθ‖₂ / sqrt(N_params)` is the architecture-invariant param-delta metric.**
    Raw L2 is not comparable across models with different parameter counts. The
    stride-sampling-every-8th idea was dropped — full pass is ~50 µs at 1.9 k params
    × 4 models.
  * **`replay_bank` gates on `BankFullness`.** If the bank holds fewer than `minReplay`
    events for that (identity, model) cell, journal `SKIPPED-INSUFFICIENT` and
    escalate to secondary remediation. For `gatherer_delivery_rate` specifically,
    the FIRST action is a ChunkRegen prod (internal action — checks chunk-field
    deficit before assuming the policy is bad). Replays cap each envelope's
    multiplicity at 2 to prevent in-bank over-fit loops.
  * **`freeze_model` defaults to `diagnostic` mode.** Soft (drain-to-discard) is
    opt-in for ttl_discard control. Diagnostic preserves the events that caused the
    divergence — exactly what an operator wants for post-mortem replay or rollback.
  * **`ttl_discard_floor` renamed to `ttl_discard_rate`** to avoid the operator-language
    inversion (a "floor" that fires when ABOVE the value).
  * **Director-tunable registration in `DirectorTunables.cs` only.** Not split across
    `TrainingModeTunables.cs` — would double-register the same key path.
  * **Director shutdown plug is in `SmartRuntime.Shutdown`** (line 1120 post-audit
    drift; was ~931 pre-audit, BEFORE
    `TrainerBarrier.ClearAll`), NOT in `LearningService.Shutdown` (which runs
    after `CancelAllAndJoin` — too late to coordinate). Rollback wraps the
    `AcceptingTrainingEvents` flip in try/finally so the flag always restores.
  * **`SmartConsoleCommands` is not partial today** and the plan does not pretend
    otherwise. Director DevCommands live in a sibling static class
    `SmartDirectorConsoleCommands.cs`. The attribute scanner is class-agnostic.
  * **All `[DevCommand]` names use the `KickStart.ModCommandID + "."` prefix** matching
    the existing `smart.snapshot.*` discipline. Grammar examples in this doc show the
    post-prefix shortname for readability.
  * **Director runs host-only** (same gates as `TrainerWorker`). On host transfer,
    the new host re-loads `directives.json` from disk and resumes from the journal.
    Client mod instances still receive telemetry pushes for digest display, but Action
    verbs are no-op on non-host.
  * **DaemonWatchdog header docstring updated** from "9 long-running daemon label
    strings" (stale; current source is 13 post-audit BUG-025/036) to "17 …" with
    base plan + GuardWorker → 18 — this is a verifier-relied-on assertion.
  * **Build integration**: old-style csproj, every new `.cs` registered explicitly;
    PowerShell-created files need `Read` before `Edit` per
    `modularization-refactor-progress` memory.
  * **BehaviorGuards anti-stifling discipline (§9).** Guards detect PROVABLE
    PATHOLOGY, not heuristic deviation. The discipline is structural, not
    procedural — it cannot be violated by a future-author edit without measurably
    changing the file shape:
      * **Severity ratio is load-bearing.** 5 observe + 4 warn + 1 soft-correct
        + 1 hard-correct (11 total — was 12 in v1; `OrbitalLockup` dropped per
        Validator-1; `IdleGatherer` demoted from warn to observe per
        Validator-1). 10 of 11 emit training-signal only. Even a buggy
        soft-correct cannot gridlock the system because soft-correct is rare by
        design. If a future author proposes raising a guard's severity, they
        must justify above this floor.
      * **Guards are muted on the reward channel.**
        `OutcomeWeights[GuardViolation_*] = 0.0` and the reward-aggregation
        path explicitly filters out `GuardViolation_*` `OutcomeKind`s
        (Validator-2 MAJOR). Guards feed CONSTRAINTS, not LOSS. This prevents
        the trainer from learning to optimise against guard suppression.
      * **Window floor 30 s** for all guards except GroundedAircraft (3 s, sole
        catastrophic-safety exception). Brief excursions never fire — Modified-form
        nuanced behaviors (reverse-orbit, drift, pause-to-assess, sideways orbit,
        beam-fix loops) pass through windowed averaging without flagging.
      * **Hard-correct release is unconditional.** GroundedAircraft's 5.0 s
        ceiling is absolute. The actuator releases the mailbox slot on a
        `MonoClock` deadline check. The guard layer does NOT poll, does NOT
        re-inject, does NOT extend. 15 s per-tech cooldown + 5-min suppression
        after 3 fires in 90 s (Validator-2 — was 60 s; widened to catch the
        deterministic 20 s oscillation cycle after 2 cycles). 2 s
        MaintainAltitude ramp-down handoff after the 5 s ceiling so the
        controller is not handed a goal cliff.
      * **Soft-correct vs hard-correct architectural asymmetry.** Hard-corrects
        (AntiCrash + GroundedAircraft) substitute under strict precondition gates
        via the per-tech `GuardInjectedGoals` mailbox slot — safety-critical
        intervention warrants the architectural surface. Soft-corrects
        (OverdueDelivery) are telemetry-only — guard publishes
        `IdentityOutcome` for training-signal consumption; controller never
        reads. The asymmetry reflects the codebase: `TacticalGoal.None`
        sentinel does not exist, and Generic identity always produces a goal,
        so "controller silent" is not a detectable precondition for substitution
        gates outside hard-correct contexts. V-fix BLOCKER decision per §9.7
        step 5; A4 substitution-rewrite REVERTED to telemetry-only in the
        reconciliation pass.
      * **Guards extend constraint vocabulary, not action vocabulary.** The
        Director's 7-verb invariant is preserved. Guards add 4 constraints; the
        Director picks from the existing 7 verbs in response (replay_bank,
        scenario_respawn, temp_adjust, rollback). Hard-correct guard goal
        injection is NOT a Director verb — it flows through
        `GuardCorrectiveActuator` writing the `TankAIHelper.GuardInjectedGoals`
        mailbox (HARD-CORRECT SLOTS ONLY — AntiCrash + GroundedAircraft per
        V-fix telemetry-only reconciliation), which is read consumer-side by
        `ContinuousController.OnOperationsTick` (Validator-3: no native
        goal-source priority chain exists). Soft-correct OverdueDelivery is
        telemetry-only — emits `IdentityOutcome` directly, never enters the
        mailbox.
      * **Sub-guard-aware action selection.** Bucket-rate constraints
        (movement/role/combat) carry the high-nibble guard ordinal through to
        the Director's ActionPicker, which queries
        `IdentityOutcomeConsumer.GetDominantGuardId(bucket, scenario, 300s)`
        before branching (Validator-2 — without this, the aggregate bucket
        would pick the same verb for fundamentally different remediations).
      * **Rollback resets guard observation state.** §7.3 step 6.5 clears
        `GuardWindowPool` + `GuardInjectedGoalTable` + `GuardViolationCounters`
        but PRESERVES `GuardCooldownTable` (Validator-2 — prevents
        re-teaching the same guard-flagged lessons after rollback because
        windows + in-flight injections survived).
      * **Sane exceptions encode fix-memory respect.** Every guard's exception
        list explicitly names the shipped fixes it must not undo
        (`combat-understeer-beeline-fix`, `enemy-obstacle-avoidance-fix`,
        `unjam-beam-loop-fix`, `enemy-reverse-and-circle-aim`,
        `bgeneral-backoff-unjam-deadlock`, `turret-aim-fix`,
        `duty-cycle-revert-nosedive-fix`). New guards that would fight a recent
        fix are REJECTED by name (§9.4 lists 12 rejected guards with rationale,
        including `OrbitalLockup` and `LowFireRate`).
      * **The principle in one line**: guards err on the side of OBSERVE not
        WARN, WARN not SOFT-CORRECT, SOFT not HARD. If in doubt, the lower
        severity wins. This is the `organic-vs-bug-design-value` memory applied
        to the guard layer.

---

## 14. Open questions + verifier-pass appendix

### 14.1 Operator open questions

  * **Faction rotation in S1.** Plan drops the explicit GSO/GeoCorp/Venture/HE rotation
    and passes `FactionSubTypes.NULL` so the engine picks; alternative is a per-faction
    `RawTechLoader.MatchCount` pre-gate that drops factions with < 2 matches at the
    target tier. Pick a policy before first-spawn test.
  * **`damageBudget` for AllyProtected.** Plan defaults to 500 HP; this is a tunable
    (`smart.director.allyprotect_damage_budget`). Spawn-test will likely refine.
  * **Calibration directive.** `calibrate <constraint> for <duration>` is included in
    the grammar — confirm that the operator wants bands derived from cold-start p50
    × 2 vs fixed C# defaults. Plan ships fixed defaults; calibration is a
    optional operator action.
  * **S5 player-team identity** is `AITeammate` (per design decision). Operator should
    confirm this is the desired training signal — versus running S5 as 1 SubNeutral
    gather team + 1 separate AITeammate defender team (more teams = more verb cost,
    different training story).
  * **OverdueDelivery kinematic-tracker base routing.** Should the
    telemetry-only kinematic correlation tracker (which measures
    `displacement-toward-base` over the 30s window after the pathology trigger)
    use the NEAREST friendly Harvesting base (current default — nearest by
    `PathingService.IsReachable`), or the originally-assigned dock if Gatherers
    carry a sticky base assignment? Plan defaults to nearest reachable; operator
    preference needed if assignment persistence matters for the correlation
    signal. (V-fix reconciliation: this is no longer a mailbox-routing question
    since OverdueDelivery is telemetry-only — the question is purely about
    which base the kinematic correlation tracker references.)
  * **`grounded_aircraft_intervention_rate` storm.** When the constraint band is
    breached (> 2 fires/min total), should the GroundedAircraft guard
    auto-suppress for 5 min, or stay diagnostic-only? Plan currently leaves it
    diagnostic-only (operator decides via `smart.director.guards.suppress`). A
    real controller-bug failure mode is the impulse only delays the inevitable
    crash; auto-suppress would surface the controller bug faster.
  * **`guard_override_qmargin` per-guard vs global.** Default 0.05 globally —
    applies to hard-correct guards only (V-fix reconciliation: soft-correct
    OverdueDelivery is telemetry-only and does not enter Q-margin arbitration).
    Ship global default with per-guard override hooks in `DirectorTunables`,
    confirm during spawn-test.
  * **MP soft-correct safety.** Largely moot under V-fix telemetry-only
    reconciliation: OverdueDelivery does NOT inject a `ReturnToBase` goal,
    so there is no host-only motion to replicate. The only MP concern is
    that `IdentityOutcome(GuardViolation_OverdueDelivery)` publishes on the
    host's GuardWorker and reaches the Director — same path as other
    `IdentityOutcome` publishers, now backed by audit BUG-055 (SaveProfile
    host-gated) + BUG-018 (QuitSaveCoordinator host-gated) + BUG-019
    (SmartForm.OnEngineSave host-gated) + BUG-021 (cross-team SetRelations
    host-gated at the three sites in EnemyMind/RCore/ModifiedTargeting); the
    Director's training-signal pipeline is now end-to-end host-authoritative.
    Apply the same pattern when reviewing future guard promotions beyond
    telemetry-only: any soft-correct that writes a `GuardInjectedGoalSlot`
    MUST gate the write on `SmartRuntime.IsHost`, mirroring the audit-fixed
    discipline.
  * **OverextendedHunter promote-to-warn tunable.** Currently observe-only. Some
    operators may want tighter leash discipline; should there be a
    `smart.director.guard.overextended_hunter.promote_to_warn` knob? Adds ~5 LOC;
    not included by default — operator must opt-in once the tunable lands.
  * **`Patrol` identity scenario gating.** `SmartIdentityClassifier` classification
    rules vary per scenario. Should `LostAlly` / `Homesickness` gate on `Patrol`
    or treat `Patrol` as identity-specific with a wider tolerance? Plan includes
    `Patrol` in `Homesickness` but excludes from `LostAlly` because patrols are
    designed to be solo; confirm.
  * **Guard re-arm chronic conditions (DESIGN-LATENT — promoted Stage-2).** Re-fire
    requires the pathology test to pass again after cooldown. For chronic conditions
    (Gatherer that simply cannot reach base due to terrain), `OverdueDelivery` would
    fire-cool-fire-cool indefinitely. **Structurally required for digest-budget bound**:
    add per-(tech, guard) chronic-suppression after N consecutive re-fires (default
    N=5) emitting `[GUARD-CHRONIC]:guardName:tech=T` once, then guard-on-that-tech goes
    silent until the pathology test transitions through FALSE for ≥ 5 min. Otherwise
    the IDENTITY-RATES + GUARDS digest sections grow unbounded under terrain-isolated
    Gatherer + reachability=true (Gatherer can reach base by raycast but cannot in
    practice). This is the same anti-stifling principle from §9.6 applied to repeat
    telemetry; promote to design (not operator preference) before mailbox writers ship.
  * **Eval battery + checkpoint ratchet (§7.5)** is a PRECONDITION for enabling
    GRU BPTT / unsupervised training of `OpponentIntentClassifier`,
    `TrajectoryResidualModel`, `ThreatAssessmentModel`. Do NOT enable BPTT
    before the ratchet ships — slow-drift self-promotion risk is unmitigated
    without quality gating on T2 / T3 (amendment A2).
  * **Live-build bugs (amendment A6) — RESOLVED post-audit** (workflow wtxvtdhcr).
    Both bugs cited at `eac2e0e` are now fixed in the live build and removed from
    the Director triage queue:
    * **M1 — DONE — fixed via AUDIT-BUG-004** (see `AUDIT-BUG-LIST.md` §BUG-004).
      `PathingService.Init` now calls `DaemonWatchdog.RegisterCanonical` +
      `WorkerHealthMonitor.RegisterCanonical` for both `ThreatFieldRebuild` and
      `PathSolve` at PathingService.cs:170-173 (poolRef-closure null-guard
      against racing Shutdown).
    * **B3 — DONE — fixed via AUDIT-BUG-022** (see `AUDIT-BUG-LIST.md` §BUG-022).
      `ProfilePersistence.Save` now wraps `StoreParameters` + Adam-payload read
      under `lock (m.SaveMutex)` at ProfilePersistence.cs:118-126 (weights and
      M/V/T snapshot from one consistent moment). Eliminates torn-but-CRC-valid
      persisted vectors that may have contributed to overnight drift / NaN-cascade.

### 14.2 Verifier-pass appendix — disposition of reviewer findings

Legend: APPLIED = absorbed into this plan; SKIPPED = explicitly rejected with
rationale. (DEFERRED is intentionally absent from this table — no row uses it.)

| # | Lens | Severity | Finding (short) | Disposition |
|---|---|---|---|---|
| A1 | Game-State Realism | BLOCKER | `BlockDelivered` hooks wrong API (`ModuleHarvestReciever.OnTaken` is empty) — must use `ModuleItemHolder.TakeItemEvent` | APPLIED — §9 publisher rewritten; OQ-1 resolved by inspection; 1 Hz fallback dropped |
| A2 | Game-State Realism | BLOCKER | `TrainerBarrier` has no Director-initiated pause channel — `WaitAll` returns instantly without `_hostLost` | APPLIED — §7.3 adds `_directorPaused` + `TrainerBarrier.AcquirePauseToken` + `SmartRuntime.RequestDirectorPause` |
| A3 | Game-State Realism | BLOCKER | "Aegis identity" doesn't exist in `SmartIdentity` enum (AIType ≠ SmartIdentity) | APPLIED — S3/S5 retargeted to `RepairSupport` + `Patrol`; constraint metric reads from `{AircraftSupport, RepairSupport, Patrol}` |
| A4 | Game-State Realism | MAJOR | SubNeutral relations degrade via `angerThreshold` accumulation in long sessions | APPLIED — ScenarioWorker zeroes `angerThreshold` per Director-owned SubNeutral pair on each 1 Hz tick; new constraint `subneutral_relations_intact` |
| A5 | Game-State Realism | MAJOR | S5 conflates `SubNeutral` and `AITeammate` | APPLIED — S5 picks `AITeammate`; SubNeutral framing dropped for S5 |
| A6 | Game-State Realism | MAJOR | Biome/chunk type list invented (Wood not in `TryGetBiomeResource`) | APPLIED — Wood removed; engine-default fallback `{PlumbiteOre 0.5, TitaniteOre 0.5}`; scenario init calls `TryGetBiomeResource(centroid)` |
| A7 | Game-State Realism | MAJOR | `FORCESpawnBaseAtPositionNoFounder` invented params (`MaxGrade`, `ForceAnchor`); `filter.ForceAnchor` wiped at `RawTechLoader.cs:1087` for mobile | APPLIED — renamed `MaxGrade=N` to `grade=N`; S3 docs that escorts are free-roaming (not anchored); S3 viabilityNote updated |
| A8 | Game-State Realism | MAJOR | AllyProtected `damageInWindow==0` gate produces permanently-unreachable rates | APPLIED — graded magnitude `max(0, 1 - friendlyDamage/damageBudget=500)`; band lowered to `≥ 0.10/min/instance` |
| A9 | Game-State Realism | MAJOR | BaseHeld `IterateBlocks` cost at 1 Hz × N bases unverified | APPLIED — polled at 0.2 Hz, cached `Damageable`-sum, re-walked only on block-detach; BaseHeld + BaseLost paired, 60s window with 5 min EWMA, `damageThreshold=25` |
| A10 | Game-State Realism | MAJOR | S6′ rejection rationale is wrong (`BasePurpose.NotStationary` DOES exist for bases) | APPLIED — §8.1 corrected; rejection cites missing waypoint authoring API, not missing enum slot |
| A11 | Game-State Realism | MAJOR | `actionvalue_loss_variance_ratio` escalation too aggressive (25 s to memory rollback) | APPLIED — escalation widened: 3 evals (15s) lr_scale; 6 evals freeze; 12 evals rollback; 60 s scenario warmup |
| A12 | Game-State Realism | MAJOR | ChunkRegenWorker runs Unity `Transform.SetParent` from background thread — not optional | APPLIED — §10: planner-only on background tick; main-thread executor in `SmartForm.Operations` |
| A13 | Game-State Realism | MAJOR | Digest example "maintain hunter pressure" isn't parseable | APPLIED — digest example uses real subjects only |
| A14 | Game-State Realism | MINOR | OQ-4 — `GetRandomEnemyBaseTeam` already exists at `AIGlobals.cs:905` | APPLIED — OQ-4 resolved; S1 cites the verified API |
| A15 | Game-State Realism | MINOR | S1 faction rotation may hit sparse-tier factions | APPLIED — drop explicit rotation; use `FactionSubTypes.NULL` (engine picks) |
| A16 | Game-State Realism | MINOR | `live_smart_tech_floor` missing scenario-active gate | APPLIED — gated on `ActiveScenario != None ∧ ScenarioWorker.HasActiveTemplate` |
| A17 | Game-State Realism | INFO | Digest `chunkdef` field is ambiguous | APPLIED — legend documents `chunkdef = live/target` |
| A18 | Game-State Realism | INFO | S4 `MatchCount` accessor unverified | APPLIED — added as explicit `RawTechLoader.cs` modification |
| B1 | ML soundness | BLOCKER | `temp_adjust(ActionValue)` scales `SamplingMPC.Sigma` — no causal link to Q-net loss-variance | APPLIED — `temp_adjust` is Intent-only; ActionValue first remediation is `lr_scale(0.5)`; `SamplingMPC` binding dropped |
| B2 | ML soundness | BLOCKER | `IdentityReplayBank` teed from `OnDamageObserved` / `LeadResidualRecorder` but `IdentityOutcome` is NOT a training event | APPLIED — tee at typed-queue enqueue sites in `LearningService`; attach `SmartIdentityStamp.Identity` at enqueue; per-(identity, ModelId) rings; documented that Gatherer mostly populates Threat + ActionValue queues |
| B3 | ML soundness | BLOCKER | Entropy band `[0.30, 0.80]` nats miscalibrated (0.30 nats is confident classifier, not collapse) | APPLIED — band re-cast as `entropy / ln(OutDim) ∈ [0.30, 0.85]`; warmup 1 000 minibatch steps |
| B4 | ML soundness | BLOCKER | `threat_param_delta_l2` not architecture-invariant; stride-sampling is biased | APPLIED — metric normalised by `sqrt(N_params)`; stride dropped (full pass ~50 µs); per-model warmup |
| B5 | ML soundness | MAJOR | `actionvalue_loss_variance_ratio` uses `LossAfter` (biased low, same-batch eval); rewards not normalised | APPLIED — uses `LossBefore`; reward-magnitude normalised; band widened to `< 3.0` initial with `calibrate` directive |
| B6 | ML soundness | MAJOR | Director priority ordering undefined; risk of constraint-action thrash | APPLIED — explicit severity score = `deviation/band_width × dependsOnPublisher_weight`; 5-min post-fire observation per constraint; Director no-op rate surfaced in digest |
| B7 | ML soundness | MAJOR | Adam snapshot capture not under `SaveMutex` → torn restore | APPLIED — §7.2 capture is a single critical section under `lock(model.SaveMutex)`; `ArchitectureVersion` validated on restore |
| B8 | ML soundness | MAJOR | BlockDelivered 1 Hz fallback too noisy | APPLIED — fallback dropped; degraded path emits `TRIG=publisher-degraded:BlockDelivered`; `ModuleItemHolder.TakeItemEvent` is hard prerequisite |
| B9 | ML soundness | MAJOR | `freeze_model` discard semantics throw away diagnostic evidence | APPLIED — default mode `diagnostic` (leave queue; backpressure-drop with `[DIAGNOSTIC-DROP]`); `soft` mode opt-in |
| B10 | ML soundness | MAJOR | BaseHeld excludes dead bases (HEALTHY immediately after destruction); damageThreshold too high | APPLIED — BaseHeld + BaseLost paired; threshold 25; 60 s window with 5 min EWMA; `INSUFFICIENT_DATA` if `(held+lost) < 3` |
| B11 | ML soundness | MAJOR | `replay_bank` causality — replay can't manufacture novel scenario states; bank may be empty | APPLIED — `BankFullness` precondition; per-envelope multiplicity cap 2; for `gatherer_delivery_rate` first action is ChunkRegen prod (internal action) |
| B12 | ML soundness | MAJOR | `IntentTemperature` torn read mid-batch | APPLIED — snapshot-at-entry invariant documented in `OpponentIntentClassifier` and `LearningTuning` headers; `volatile` markers |
| B13 | ML soundness | MINOR | `ttl_discard_floor` directional inversion | APPLIED — renamed to `ttl_discard_rate` |
| B14 | ML soundness | MINOR | Digest `-` ambiguity; matrix layout wastes bandwidth | APPLIED — `N/A` + legend; identity rates as populated cells only |
| B15 | ML soundness | MINOR | Standing directives re-clobber restored state after rollback | APPLIED — forced post-rollback digest emits `STANDING-DIRECTIVES-REAPPLIED` line |
| B16 | ML soundness | INFO | S2 training-signal density check | APPLIED — `ScenarioRegistry` recipes declare expected events/min per (model, identity); `INSUFFICIENT_DATA` gate on `recentBatches < 8`; ChunkRegen budget boost for S2 (9/tick, 2/sec) |
| B17 | ML soundness | INFO | BPTT cold-start + rollback interaction | APPLIED — `ArchitectureVersion` validated on restore; Intent constraint forced into warmup window after cross-version restore |
| C1 | Engineering feasibility | BLOCKER | `TrainerBarrier` reuse for rollback synchronization is broken | APPLIED — overlaps A2; Director-owned pause channel |
| C2 | Engineering feasibility | BLOCKER | ScenarioWorker / ChunkRegenWorker thread safety — Unity main-thread ops | APPLIED — overlaps A12; both daemons demoted to planners |
| C3 | Engineering feasibility | MAJOR | Shutdown ordering — Director vs `TrainerBarrier.ClearAll` | APPLIED — Director lifecycle in `SmartRuntime.Shutdown` line ~931 before `ClearAll`; `BeginShutdown()` aborts in-flight rollback; rollback uses try/finally on `AcceptingTrainingEvents` |
| C4 | Engineering feasibility | MAJOR | Fabricated `LearningService_Init.cs` / `SmartConsoleCommands` partial-class precedent | APPLIED — sibling class `SmartDirectorConsoleCommands.cs`; no partial-class fiction |
| C5 | Engineering feasibility | MAJOR | SmartIdentity vs AIType confusion | APPLIED — overlaps A3 |
| C6 | Engineering feasibility | MAJOR | Double-registration of Director tunables | APPLIED — only `DirectorTunables.cs` registers; `TrainingModeTunables.cs` unchanged |
| C7 | Engineering feasibility | MAJOR | `SmartForm.Operations` per-tank drain hot-path | APPLIED — drain DirectiveInbox via `DirectorInbox` daemon → `WorldEventBus.PublishFromWorker`; no per-tank drain |
| C8 | Engineering feasibility | MAJOR | `DaemonWatchdog` header docstring "9 daemons" lie + `Tests/AbortSurvivalTests.cs` roster-count check | APPLIED — docstring updated to "13"; test surface flagged for one-line update |
| C9 | Engineering feasibility | MINOR | TrainerBarrier source-location anchor (`OnlineTrainer.cs:316` is the Register call, not the barrier file) | APPLIED — anchors corrected throughout; barrier file is `Smart/Coordination/TrainerBarrier.cs:22` |
| C10 | Engineering feasibility | MINOR | `[DevCommand]` `KickStart.ModCommandID` prefix discipline | APPLIED — all command names use prefix; grammar examples show shortnames with legend |
| C11 | Engineering feasibility | MINOR | `ProfilePersistence.cs` line anchor settled at `:64-95` | APPLIED |
| C12 | Engineering feasibility | MINOR | LOC undercount for marshaling glue | APPLIED — total revised to ~6 500 with per-file numbers absorbing the marshaling |
| C13 | Engineering feasibility | INFO | RawTechLoader `ForceAnchor` wipe for mobile (already in plan's OQ-6) | APPLIED — documented at the modification site |
| C14 | Engineering feasibility | INFO | `AIGlobals.GetRandomBaseTeam` is verified | APPLIED — overlaps A14 |

No reviewer findings were SKIPPED. No findings were DEFERRED — every BLOCKER and
MAJOR has a concrete fix in this plan. Reviewer strong-points (PathRequestBackpressure
reservoir pattern reuse, `DaemonWatchdog` + `WorkerHealthMonitor` dual-registration,
3-tier rollback layering, 7-verb bounded vocabulary, S2 SubNeutral viability call,
S6 rejection discipline, journal-as-source-of-truth) are preserved.

### 14.3 BehaviorGuards verifier-pass appendix (3-validator post-fold-in)

Disposition of the 3-validator pass on the BehaviorGuards layer (Validator-1
Pathology Rigor; Validator-2 Non-Stifling / Training Impact; Validator-3
Engineering Fit / Verified Primitives).

Legend: APPLIED = absorbed into this plan; PARTIAL = fix applied with reduced
scope and rationale; SKIPPED = explicitly rejected with rationale;
MOOT = no longer applicable after upstream change; SUPERSEDED = replaced by a
later finding's disposition; PARTIALLY-SUPERSEDED = some aspects retained, others
replaced. (DEFERRED is intentionally absent — no row uses it.)

| # | Validator | Severity | Finding (short) | Disposition |
|---|---|---|---|---|
| V1-1 | V1 | MAJOR | BackwardsLock fires on Sniper kiting / Hunter standoff retreat | APPLIED — §9.3 BackwardsLock identity gate excludes Sniper; added Sniper-standoff, TurretFraction-still-fighting, and net-displacement-actually-kiting exceptions; added no-LOS-to-hostile AND-conjunct |
| V1-2 | V1 | MAJOR | OrbitalLockup vs OrbitNoFire double-jeopardy + texture risk | APPLIED — `OrbitalLockup` DROPPED from §9.3 (also from §9.4 reject list, §9.8 OutcomeKind, §12.1 file list, severity ratio, headline count). `OrbitNoFire` gates tightened to require BOTH LOS AND firing arc AND no-weapon-cooldown ≥ 30% (was OR). Snipe-only weapon-cycle exception added |
| V1-3 | V1 | MAJOR | WaterloggedTech missing wading / chasing-target exceptions | APPLIED — §9.3 WaterloggedTech submerged threshold tightened to `SeaHeight − 2.0 m`; net-displacement progress AND-conjunct added to pathology test; Provoked+chasing exception added |
| V1-4 | V1 | MAJOR | FriendlyFire dodge-into-path + magnitude+cumulative threshold sensitivity | APPLIED — §9.3 FriendlyFire raised to > 15 HP per event and > 150 HP cumulative; attribution gate excludes ally-crossed-line-after-aim-time; beam-during-reacquisition exception; §4 weight demoted 3x → 2x at ship pending field calibration |
| V1-5 | V1 | MAJOR | GroundedAircraft strafing-target mode collapse | APPLIED — §9.3 Vehicle bucket adds strafing-target exception (`lastEnemyGet IsLiveTechTarget AND target ground-altitude < 5 m AND dist < 80 m`); velocity gate tightened to `< −3.0 m/s` from `−1.0`; pitch-attitude < −45° positive confirm; throttle-modulation check; mode-collapse telemetry `hard_correct_on_active_target_count` with `[GUARD-AIRCRAFT-DIVE-INTERRUPT]` digest tag |
| V1-6 | V1 | MAJOR | OverdueDelivery long-haul mining false-positive + 2x weight amplifies | APPLIED — §9.3 OverdueDelivery requires `dist_to_base_at_end >= dist_to_base_at_start − 15 m` across full window (true no-net-progress); heading-toward-base + velocity-projection-toward-base exceptions; §4 weight demoted 2x → 1x at ship pending field calibration. **V-fix telemetry-only reconciliation note**: pathology test unchanged; false-positive impact reduced because OverdueDelivery no longer substitutes goals — a false-positive now only emits a stray training-signal `IdentityOutcome` (already reward-channel-muted by `OutcomeWeights[GuardViolation_*] = 0.0`), not a goal injection that disrupts mining. |
| V1-7 | V1 | MAJOR | IdleGatherer chunk-distance vs reachability gap | APPLIED — §9.3 IdleGatherer uses `AIEPathQuery.IsReachable` filter not raw OverlapSphere; demoted from warn to OBSERVE; `AIEPathMapper.LastQueryFailedRecently` exception added |
| V1-8 | V1 | MAJOR | Homesickness fires on healthy base-defense in S5 | APPLIED — §9.3 Homesickness drops S5 from scenario gate; adds `no hostile within 400 m of friendly base in last 60 s` exception; pathology test now requires `any hostile alive within 300 m of base detected in last 90 s NOT engaged by this tech` |
| V1-9 | V1 | MINOR | LostAlly pair-existed-at-window-start vs ally-died-mid-window | APPLIED — `AllyProtectionPublisher.LastAllyLossMono(techId)` accessor exposed; LostAlly requires `now − LastAllyLossMono > 30 s` |
| V1-10 | V1 | MINOR | WedgedNoProgress `Provoked == 0` Provoked-decay edge | APPLIED — gate broadened to `Provoked == 0 throughout window OR (lastEnemyGet null AND no DamageObserved against this tech in last 30 s)` |
| V1-11 | V1 | MINOR | OverextendedHunter inclusion of Patrol shapes patrol policy | APPLIED — Patrol DROPPED from §9.3 OverextendedHunter identity gate (was: Hunter, Scrapper, Patrol; now: Hunter, Scrapper) |
| V1-12 | V1 | MINOR | Source-byte 4-bit guard ordinal future expansion | APPLIED — §9.8 + §12.2 EventBus row: documented 11 active ordinals (0..10) + 4 reserved (11..14) + 15 wildcard sentinel; unit-test `GuardOrdinalTest.cs` asserts `GuardRegistry.GuardCount <= 14`; explicit migration plan when 15th guard ships |
| V1-13 | V1 | MINOR | Combat priority above unjam = deadlock loop | APPLIED — §9.7 priority 3 (Combat) vs priority 4 (Unjam) inverted under `FrustrationMeter > 0.5 × saturation AND lastEnemyGet alive AND no progress in 8 s` (the "CombatStuck" branch); cites `bgeneral-backoff-unjam-deadlock` memory |
| V1-14 | V1 | INFO | `learned_model_overrode_guard_count` threshold unmeasured | APPLIED — §9.6 + §12.2 DigestBuilder: when `override_rate > 0.40 over 10 min AND fired_count > 5`, auto-surface `[GUARD-MAYBE-FALSE-POSITIVE]:<guardName>:override_rate=N%` digest line |
| V1-15 | V1 | INFO | FriendlyFire 3x weight compounds with false-positive rate | APPLIED — see V1-4; weight at 2x at ship; per-guard `fired_count + weighted_contribution` surfaced in digest |
| V1-16 | V1 | INFO | OrbitalLockup AircraftHunter 60 s window short for climb-attack | MOOT — `OrbitalLockup` dropped per V1-2 |
| V2-1 | V2 | MAJOR | Guard-violation outcomes will drown positive publisher signal in reward channel | APPLIED — §9.6 fifth bullet + §12.2 IdentityOutcomeConsumer row: `OutcomeWeights[GuardViolation_*] = 0.0` default; trainer reward path explicitly ignores `GuardViolation_*` OutcomeKinds; invariant enforced in consumer and exposed as `OutcomeWeights.GuardChannelMuted` |
| V2-2 | V2 | MAJOR | BaseReturnGoalSource priority above default-heuristic punishes exploration | SUPERSEDED — original disposition revised OverdueDelivery overrides to a substitution-with-Q-margin tiebreaker. **V-fix telemetry-only reconciliation supersedes this**: OverdueDelivery no longer substitutes at all (no mailbox slot, no arbitration), so the exploration-punishment risk is eliminated by construction. Q-margin override, 120s cooldown, and all substitution-arbitration mechanisms are STRUCK. See §9.7 step 5 + §9.3 telemetry-only mechanism block. |
| V2-3 | V2 | MAJOR | GroundedAircraft 5 s + 15 s = 20 s evasion cycle escapes 3-in-60s suppression | APPLIED — §9.3 + §9.6 + DirectorTunables row: anti-loop ceiling widened to **3 fires in 90 s** (was 60 s); `aircraft_release_ramp_sec` 2.0 s `MaintainAltitude` ramp-down handoff after the 5 s ceiling; `oscill` count surfaced |
| V2-4 | V2 | MAJOR | Bucket constraint loses Director-actionable sub-guard specificity | APPLIED — §4 movement / role / combat constraint action chains now sub-guard-aware (ActionPicker queries `IdentityOutcomeConsumer.GetDominantGuardId(bucket, scenario, 300s)` and branches per dominant guard); new `GetDominantGuardId` accessor on `IdentityOutcomeConsumer` (§12.2) |
| V2-5 | V2 | MAJOR | Rollback re-teaches guard-flagged lessons because guard window state survives | APPLIED — §7.3 step 6.5 inserted: reset `GuardWindowPool` + `GuardInjectedGoalTable` + `GuardViolationCounters`; PRESERVE `GuardCooldownTable`; `GuardCorrectiveActuator.ForceReleaseAll()`; new `GUARD-WINDOWS-RESET` digest line + journal entry |
| V2-6 | V2 | MINOR | Digest `pathology_total` integer buries distribution texture | APPLIED — DigestBuilder row: replace integer with `pathology_total=N distinct_techs=N max_per_tech=N p90_per_tech=N` distribution sketch |
| V2-7 | V2 | MINOR | IdleGatherer / OverdueDelivery 100 m round-trip interrupts at delivery moment | PARTIALLY-SUPERSEDED — §9.3 OverdueDelivery velocity-projection-toward-base exception retained as part of the pathology test (still useful: avoids stray training-signal emissions during legitimate final approach). **15 s injection-delay STRUCK** under V-fix telemetry-only reconciliation: there is no mailbox slot to delay-write; the converging-Gatherer protection now comes purely from the velocity-projection-toward-base AND-conjunct in the pathology test. `overdue_delivery_injection_delay_sec` tunable removed. |
| V2-8 | V2 | INFO | 11 rejections missing LowFireRate / NoFireSpree | APPLIED — §9.4 adds LowFireRate to rejected list with rationale (TurretFraction-low Forwards-classifier dominant; OrbitNoFire already gates) |
| V2-9 | V2 | INFO | FriendlyFire 3x weight not visible to future replay-bank attribution | APPLIED — §9.8 documents that guard weights are CONSTRAINT-LAYER only; any future replay-bank-targeted-at-preceding-guard-fire extension must look up weights from `ConstraintRegistry`, not from `OutcomeKind` |
| V3-1 | V3 | BLOCKER | 8-level priority chain references non-existent `IAIGoalSource` / `Goal.Priority` / `ForceAscend` | APPLIED — §9.7 rewritten: `ISmartGoalSource` flat registration is the shipped surface; guard integration via consumer-side substitution in `ContinuousController.OnOperationsTick` (newly explicit modification in §12.2); `TankAIHelper.GuardInjectedGoals` mailbox written by `GuardCorrectiveActuator`, read by ContinuousController; arbitration code lives in the consumer |
| V3-2 | V3 | BLOCKER | `recentSpeedSigned` / `recentSpeedXZ` don't exist in Modified-tree TankAIHelper | APPLIED — §9.3 primitive-availability note added; §12.2 TankAIHelper row expanded to ADD BACK `recentSpeedSigned` ring AND introduce `recentSpeedXZ` ring as 4 Hz rings (same shape as already-budgeted `recentSpeedY`); LOC bumped 60 → 80 |
| V3-3 | V3 | BLOCKER | GuardWorker daemon-vs-inline contradiction | APPLIED — §9.2 + §12.2 TrainingDirector row: GuardWorker owns its own 1 Hz background daemon thread (DaemonWatchdog roster 13 → 14); TrainingDirector does NOT call `Tick(deltaSec)` inline. Main-thread reads marshaled via `GuardEvaluationRequest` queue drained inside `SmartForm.Operations` (same pattern as ScenarioSpawnRequest / ChunkSpawnRequest); +30 LOC on BehaviorGuardWorker, +20 LOC on SmartForm folded into per-file totals |
| V3-4 | V3 | BLOCKER | GroundedAircraft references fictional primitives (AirborneIntent, TerrainHeightCache, HoverHeight) | APPLIED — §9.3 Vehicle bucket: altitude via `ManWorld.inst.TileManager.CalcTerrainHeightAtPosition` (main-thread marshaled); descent rate via `recentSpeedY`; `AirborneIntent != Landing` exception dropped (controller cannot express today; folded into existing `within 30 m of SpawnAnchor AND descending` exception); `AILimitSettings.HoverHeight` revert dropped (release simply clears mailbox slot) |
| V3-5 | V3 | MAJOR | §12.3 LOC arithmetic does not sum | APPLIED — §12.3 honestly re-summed: new ~9 570 + modified ~1 665 = **~10 700 grand total** (was claimed ~9 500); §1 / §15 final-line totals updated accordingly |
| V3-6 | V3 | MAJOR | `FormState.LastSelectedGoalSource` does not exist as a field | APPLIED — §9.3 OverdueDelivery soft-correct goal: kinematic detection (observe whether `tank` actually moved toward `nearestFriendlyHarvestingBase` over 30 s post-write) replaces FormState equality check; `LastAppliedGoalKind` byte added to TankAIHelper sidecar (§12.2) for richer attribution. **V-fix telemetry-only reconciliation note**: kinematic detection mechanism is RETAINED but repurposed — now used by the telemetry-only kinematic correlation tracker (records displacement-toward-base as a separate magnitude-scaling `IdentityOutcome` for training-signal correlation), not as an arbitration-win detector. `LastAppliedGoalKind` still added for hard-correct attribution. |
| V3-7 | V3 | MAJOR | `AILimitSettings.MaxLeashRange` does not exist | APPLIED — §9.3 OverextendedHunter reads `smart.director.guard.overextended_hunter.leash_m` Director tunable (default 250 m); DirectorTunables row updated; field name no longer cites `MaxLeashRange` |
| V3-8 | V3 | MAJOR | OutcomeKind consumer back-compat — `GetCount` would break | APPLIED — §12.2 IdentityOutcomeConsumer row: `_counters` PRESERVED + reimplemented over the ring snapshot count; `GetCount(SmartIdentity, OutcomeKind)` accessor stays zero-churn back-compatible |
| V3-9 | V3 | MAJOR | Source-byte 0=none collides with valid filter 0 | APPLIED — §9.8 + §12.2 IdentityOutcomeConsumer row: ordinals shifted to 0..10 = active guards; 15 = wildcard / no-filter sentinel; `guardFilterByte` default changed 0 → 15; non-guard publishers set high-nibble to 15 |
| V3-10 | V3 | MAJOR | GuardInjectedGoals mailbox has no reader in plan modifications | APPLIED — `Smart/Control/ContinuousController.cs` added as newly-explicit modification (§12.2; ~50 LOC for `OnOperationsTick` to read mailbox and apply §9.7 arbitration); Modified non-Smart drive paths declared OUT OF SCOPE — guards target Smart-form techs only |
| V3-11 | V3 | MAJOR | §1 publisher count says 3 not 4 | APPLIED — §1 + §10 + §15 final line: publisher count corrected to **4 publisher sources** (BlockDelivered, BaseHeld+BaseLost, AllyProtected, BehaviorGuardWorker); §10 opening note added |
| V3-12 | V3 | MINOR | DaemonWatchdog cite "lines 7-21" — load-bearing line is `:9` | APPLIED — §12.2 DaemonWatchdog row cites `DaemonWatchdog.cs:9` specifically |
| V3-13 | V3 | MINOR | `AnchorPoint` / `OrderedDestination` are wrong field names | APPLIED — §9.3 movement bucket sane-exceptions rename to `SmartIdentityStamp.SpawnAnchor` (verified at `Smart/Identity/SmartIdentity.cs:35`) and `helper.lastDestination`; per-guard rows in §12.1 updated |
| V3-14 | V3 | MINOR | Modified file count math doesn't audit cleanly | APPLIED — §12.2 header rewritten: 19 DISTINCT modified-file paths (17 base + TankAIHelper + ContinuousController); guard-extension rows on §12.1 publisher files (ChunkRegenWorker, AllyProtectionPublisher, BlockDeliveredPublisher) are LOC-folded into §12.1 not double-counted as modified |
| V3-15 | V3 | MINOR | Vanilla-form `IAIGoalSource` returns `Goal.None` is fictional | APPLIED — §9.7 Vanilla paragraph rewritten: Vanilla form hands back to classic AI and never invokes the `ISmartGoalSource` path; guards never affect Vanilla |
| V3-16 | V3 | INFO | `MovementController.CurrentPlan` enum value `PlanKind.CombatCircle` unspecified | PARTIAL — §12.1 OrbitNoFireGuard row notes the concrete enum value will be resolved at implementation time; if absent in the existing PlanLibrary, plan ships a one-line PlanKind tag addition. Not blocker because the guard impl is the natural place to verify the enum |

Validators' overall verdicts: V1 SHIP-WITH-REVISIONS; V2 SHIP-WITH-REVISIONS;
V3 REJECT (engineering-fit). All V3 BLOCKERs are now APPLIED — the
architecture-vs-shipped-primitive corrections (consumer-side substitution,
recentSpeed* rings added back, daemon placement consistent, GroundedAircraft
shipped-primitive replacements, ContinuousController as the load-bearing
reader) collectively address the REJECT verdict. Strong points from all three
validators (severity ratio 6/4/1/1 → 5/4/1/1 as load-bearing structure;
§9.4 rejected-guards list with fix-memory anchors; GroundedAircraft 5 s
deterministic release ceiling; soft-correct never wins against a confident
model; sub-guard ordinal scheme avoiding OutcomeKind explosion; populated-cells-only
digest section) are preserved.

### 14.4 12-reviewer + 3-verifier post-design verification pass

Summary: 12 specialist reviewers (one per surface §2/§3/§4-models/§4-identity-system/§5-actions/§6-digest/§7-rollback/§8-scenarios/§9-guards/§10-publishers/etc.) each produced findings + bug-traps against the v1+fold-in plan. 3 independent verifiers cross-checked findings against live source at file:line precision (Modified tree + Decompiled AssemblyCSharp). Verifiers also surfaced bugs reviewers missed across interaction boundaries.

**Disposition rule applied**: a finding became APPLY-WORTHY iff (a) ≥ 2 verifiers CONFIRMED OR (b) BLOCKER + no REFUTED OR (c) verifier-surfaced miss with independent cross-flag. REFUTED items logged below; NEEDS-MORE-EVIDENCE items deferred to runtime verification.

**Counts**: ~115 reviewer findings/bug-traps reviewed; **~85 APPLIED** (corrections applied to §2/§3/§4/§5/§6/§7/§8/§9/§10/§11/§12 directly); **0 REFUTED** (no verifier refuted a finding); **~12 NEEDS-MORE-EVIDENCE** (runtime spawn-test required); **~18 redundant duplicates** across reviewer reports collapsed into single fixes.

#### Categorized fixes applied

| Category | Examples (representative) | Count applied | Sections touched |
|---|---|---|---|
| **Fictional engine API names** (BLOCKER) | `CalcTerrainHeightAtPosition` → `GetTerrainHeightAtPosition`; `ManWorld.SeaHeight` → `KickStart.WaterHeight`; `FireControl.ProjectilesFiredCount` → NEW `ProjectileFiredPublisher`; `ManNetwork.IsClient` → `!ManNetwork.IsHost && ManNetwork.IsNetworked`; `AdvanceTimer` → `FrustrationMeter`; `PatrolGoalSource.IsAtRingNode` → kinematic motion test; `ModuleItemHolder.currentItemCount` → `NumContents`/`IsEmpty`; `AILimitSettings.LoneWolf` → DirectiveTable opt-out; `AIEPathQuery.IsReachable` → NEW `PathingService.IsReachable`; `ManBaseTeams.EnumRelations` → caller-walked GetRelationsWritablePriority; `PlanKind.CombatCircle` → kinematic angular-displacement test; `TacticalGoal.None` → telemetry-only mailbox; `TacticalGoal.{kind,targetY,expiresMono,sourceId}` → sidecar `GuardInjectedGoalSlot` struct; `helper.lastDestination` → `lastDestinationCore`; `FormState.LastSelectedGoalSource` → kinematic detection. | ~16 | §4, §9.3, §9.7, §12.1, §12.2 |
| **C# language errors** (BLOCKER) | `volatile long FrozenUntilTickMono` → plain long + Interlocked.Read/Exchange; `short ArchitectureVersion` → byte (already exists, no add). | 2 | §5, §7.2, §12.2 |
| **Enum confusion** (BLOCKER) | `SmartIdentity.Prospector` / `.Scrapper` (don't exist) → dropped from buckets; AIType-Prospector/Scrapper techs classify as Gatherer/Hunter respectively. `FactionLevel.Mid` → `FactionLevel.VEN`. | 4 | §4, §8, §9.3 |
| **Missing OutcomeKind / event structs** (MAJOR) | `BaseLost=4` added to OutcomeKind enum; `ChunkSpawnRequest`, `ScenarioSpawnRequest`, `GuardEvaluationRequest`, `ScenarioRelationsRequest`, `ScenarioAngerResetRequest`, `GuardWritePayload`, `DaemonRespawned`, `TechDespawned`, `LearningService.ProfileSaved` added to manifest. ClearAll subscriber-list extension noted. | 9 | §12.2 EventBus.cs |
| **Thread-affinity / main-thread marshaling** (BLOCKER) | ScenarioWorker `ManBaseTeams.SetRelations` + angerThreshold writes → ScenarioRelationsRequest envelope to main thread; GuardCorrectiveActuator `tank.boundsCentreWorld` reads → GuardWritePayload + GuardEvaluationRequest round-trip; `TankAIHelper.PublishedFrustrationMeter` volatile snapshot; per-tech mailbox writes use `Interlocked.Exchange`. | 5 | §8, §9.2, §12.1, §12.2 |
| **Lifecycle / race conditions** (BLOCKER) | OnHostChanged acquires pause-token before flipping _hostLost; AcceptingTrainingEvents → Interlocked ref-counted on _acceptingDisableCount; concurrent rollback gated by `Interlocked.CompareExchange(ref _rollbackInProgress)`; rate-limit primitives explicit. | 5 | §7.3, §12.2 |
| **Shutdown ordering** (BLOCKER) | Director.BeginShutdown Interlocked-latched (L-004 mirror); blocks up to 10s for rollback drain; NEVER uses Thread.Abort; cooperative kill-switch + SHUTDOWN-FORCED journal on timeout; calls UninstallDirectorPublishers + GuardCorrectiveActuator.ForceReleaseAll before queue drain. | 4 | §7.3, §12.2 SmartRuntime |
| **Mailbox shape** (BLOCKER) | GuardInjectedGoals = per-tech fixed-size `GuardInjectedGoalSlot[GuardCount]` indexed by GuardOrdinal — multiple guards can coexist; GLOBAL `GuardInjectedGoalTable` STRUCK from DirectorState. | 1 | §12.2 TankAIHelper, DirectorState |
| **MonoClock unit** (BLOCKER) | All `now + Nms` arithmetic → `now + MonoClock.FromSeconds(sec)` since MonoClock.Now() returns Stopwatch ticks not ms. | 1 (recursive) | §9.3, §12.1 GuardCorrectiveActuator |
| **AntiCrash priority over Coordinator** (BLOCKER) | AntiCrash mailbox check inserted BEFORE existing `external = _readExternalGoal?.Invoke()` at ContinuousController.cs:208. | 1 | §9.7, §12.2 ContinuousController |
| **Director-pause FSM gate triplet** (BLOCKER) | OnlineTrainer.cs:355 (entry) + :391 (resume) gates updated; line 407 unchanged; memory barrier at :396. | 1 | §7.3, §12.2 OnlineTrainer |
| **IdentityOutcomeConsumer rate-window mechanism** (BLOCKER) | per-(identity,kind) ConcurrentQueue<long> timestamp ring trimmed at read; bifurcated `_counters` vs `_guardCounters` dicts so guard outcomes never reach reward path. | 1 | §12.2 IdentityOutcomeConsumer |
| **Reward access at TrainerWorker push site** (BLOCKER) | `ActionValueEstimator.EwmaAbsReward` getter updated inside TrainOneMinibatch under SaveMutex; TrainerWorker reads getter for normalization. | 1 | §4, §12.2 ActionValueEstimator |
| **Entropy push at unwired Evaluate** (MAJOR) | Pinned to `TrainOneMinibatch_FullBptt` at line 266 (Stage-2 cite-verify; was 256 pre-audit) where softmax probs already computed. NaN guard `p[i] <= 1e-9f` → 0. | 1 | §4, §12.2 OpponentIntentClassifier |
| **DigestSnapshot shape + DigestBuilder hazards** (MAJOR) | DigestSnapshot fields enumerated; both projections (text + JSON) derived purely from snapshot; `LatestDigest = volatile string`; sink exception isolation; shutdown-during-emit gate; unique tmp filename per emit; LogWarnFileOnly dedup bypass via UnityEngine.Debug.Log direct; IMGUI panel resized + ScrollView; digest.history.log retention 5MB+30d gzip; clock-source pinned (DateTime.UtcNow scheduling + MonoClock uptime in payload). | 6 | §6.1, §6.3, §12.2 SmartForm |
| **Publisher install ordering + cleanup** (MAJOR) | Install at SmartRuntime.Init (not after-trainers); UninstallDirectorPublishers BEFORE Director queues drain at shutdown. | 2 | §12.2 LearningService, SmartRuntime |
| **BlockDelivered attribution** (MAJOR) | Switch from per-holder TakeItemEvent to per-tank `ItemPickupEvent` + `ItemReleaseEvent` (TechHolders.cs:163,165); Release→Pickup 2-frame window pairing; Visible.OnPool/OnRecycle hook clears stale dict entries; lazy-subscribe on Smart-only filter to avoid non-Smart NPC spam. | 1 | §10 |
| **BaseHeld per-block hook → tank-level** (MAJOR) | `Tank.DetachEvent` (Tank.cs:264) single subscription per tech in lieu of per-block `SubToBlockAttachConnected` (avoids N=80 subscription leak surface). | 1 | §10 |
| **Type-erasure for IdentityReplayBank** (MAJOR) | 4 type-specialized dictionaries (no boxing); `EventEnvelope<T>` wrapper with Source byte + ReplayCount; ActionValueEvent producer now SHIPPED (feature-expansion e9bfcd4 closeout) at ContinuousController.cs:444/514 — all 4 tees active. | 1 | §5, §10, §12.1 IdentityReplayBank |
| **DaemonWatchdog roster math + AbortSurvivalTests assertion** (MAJOR/MINOR) | Current source roster = 9 (not 13); plan extends 9 → 13 → 14 (with GuardWorker); AbortSurvivalTests has NO hard-coded roster-count assertion (V-fix REFUTED original plan claim); roster array AND factory dict both must be updated. | 2 | §2, §12.2 DaemonWatchdog |
| **ScenarioWorker scenario-generation stamp** (MAJOR) | Every spawn/chunk/relations envelope stamped with `ScenarioGeneration` int; main-thread drain rejects stale stamps to prevent post-teardown materialize. | 1 | §8, §11 |
| **Tank.name tagging → ScenarioOwnedTechs dict** (MAJOR) | DirectorState.ScenarioOwnedTechs keyed by techId; ownership stamped at executor return on main thread. | 1 | §8.0 S1, §12.2 DirectorState |
| **SmartIdentityRegistry priority correction** (MAJOR) | OverdueDelivery soft-correct REWRITTEN as telemetry-only (never substitutes); kinematic-detection records influence when policy converges. §9.7 step 5 unified with §9.3 (had contradicted on Q-margin). | 1 | §9.7 |
| **AdamSnapshot value type** (V-fix MAJOR) | NEW `AdamSnapshot readonly struct { float[] M; float[] V; long T; float LR; float BaseLR; }` returned by value from `ILearnedModel.GetAdamSnapshot()`; writers route through `SetLearningRate(float)` + `LoadAdamState(AdamSnapshot)`; 7-Action invariant enforced by language, not policy. | 1 | §2 L2 row, §5 lr_scale, §7.2 |
| **MonoClock wall-clock pairing + journal cursor** (V-fix MAJOR) | ParamSnapshot stores BOTH `monoTick` (process-relative) AND `wallclockUtcSec` (cross-restart comparable); rollback step 6 journal-cursor match uses wall-clock when cross-restart. | 1 | §7.2 |
| **DirectorTuning namespace rename** (V-fix MINOR) | `Smart/Director/LearningTuning.cs` renamed to `Smart/Director/DirectorTuning.cs` (class `DirectorTuning`) to avoid namespace collision with existing `Smart/Learning/LearningTuning.cs`. | 1 | §12.1 |
| **Parser LOC + tokenizer split** (MAJOR) | Original 200 LOC parser → 80 tokenizer + 250 parser + 100 SubjectResolver = ~430 LOC for 14 verb sub-grammars. | 1 | §3.3, §12.1 |
| **Quote-the-directive helptext** (BLOCKER) | DevCommand whitespace-split documented; positional aliases ship for common cases. | 1 | §3.1, §12.2 SmartDirectorConsoleCommands |
| **Trigger coalescing + cold-start suppression** (MAJOR/MINOR) | ≥2 triggers per Director tick → coalesce union; cold-start <60s suppresses host-changed/scenario_set; `TRIG=tick` added for periodic; `TRIG=publisher-degraded` dropped (no producer); `DaemonRespawned` event source added. | 4 | §6.2, §12.2 EventBus.cs / DaemonWatchdog.cs |
| **LoadParameters internal lock** (MINOR) | All 4 model implementations internally `lock(_saveMutex)` so external callers (SnapshotManager.Restore + Director rollback) cannot race `_publishedParams.Write`. | 1 | §12.2 model files |
| **AutosaveWorker race vs rollback** (V-fix MAJOR — missed by reviewers) | ProfilePersistence.Save iterates 4 models calling StoreParameters without SaveMutex; concurrent Director-rollback holding SaveMutex on model 1 = AutosaveWorker persists frankenfile. Fix: wrap StoreParameters call site in `lock(model.SaveMutex)` (4 locks per save, cheap). | 1 | §7.1 |
| **Init-order: directives loaded before checkpoint prune** (MAJOR) | Director.Init: (1) load directives.json + journal; (2) build referenced label set; (3) prune unreferenced; (4) register canonical daemon. | 1 | §7.4 |

#### REFUTED reviewer findings (preserved here for record)

None. No verifier marked any reviewer finding as REFUTED. Two reviewer claims about DaemonWatchdog test assertions and one about source-byte sharing were clarified rather than refuted (existing assertion does not exist; sharing semantics defined more precisely).

#### NEEDS-MORE-EVIDENCE items (runtime spawn-test required)

These items are runtime-verification items, NOT plan-revision deferrals — they
ship with the plan and are validated against live behavior post-deploy. Items
flagged DESIGN-LATENT (RM-9, RM-10, RM-12) were promoted by the Stage-2 pass:
they require a design pre-commitment before the relevant code ships, not just a
runtime test.

| # | Item | Why deferred |
|---|---|---|
| RM-1 | DevCommand `ManDevCommands.SplitIntoParts` exact tokenization behavior at line 417 | Reviewer-2 BLOCKER on quoting; not source-verified to exact line. Fix applied is sound regardless (positional aliases). |
| RM-2 | Tank.OnSpawnSetup engine renaming behavior vs `§DIR-Sx-n` Tank.name tagging | Reviewer-5 MAJOR; replaced with ConcurrentDictionary keyed by techId — fix is sound regardless. |
| RM-3 | AllyProtected LRU 64-pair capacity vs realistic peak population | Reviewer-12 MAJOR; raised to 128 + partial-emit-on-eviction + INSUFFICIENT_DATA gate. Verify under sustained stress. |
| RM-4 | Tank.AttachEvent / DetachEvent firing order vs engine block-count update | RESOLVED — §14.4.1 marked DROPPED-DISPROVEN (firing order acceptable for HP cache invalidation). |
| RM-5 | MP soft-correct replication (host-only GoalSource winning arbitration → ghosting on client?) | Original OQ; OverdueDelivery is now telemetry-only so primarily benign. Re-verify if promoting beyond observe. |
| RM-6 | ChunkRegen 5s + 6-budget vs ManPop concurrency at S2 sustained | Plan §8 maintenance + ChunkRegen race confirmed via predicate-re-evaluation at main-thread drain time; sustained stress test needed. |
| RM-7 | TacticalGoal.AtCurrent as "neutral" sentinel kinematic equality threshold | RESOLVED — §14.4.1 marked DROPPED-DISPROVEN (kinematic-detector accuracy preserved without sentinel-equality threshold; §9.7 step 5 telemetry-only resolution). |
| RM-8 | Director.Init full sequence vs ScenarioWorker / GuardWorker registration order | Spec'd in §2 Rationale; pending runtime exercise under abort-during-init races. |
| RM-9 | Stale guard mailbox slots vs Tank-pool recycle | **DESIGN-LATENT (Stage-2 promotion)**: §12.2 TankAIHelper row uses `IAIForm.OnTechRecycle(helper)` at line 967 (per R3-M5); but pool reuse path that does NOT go through `Recycled()` (e.g. host-handover SpawnTech where TankAIHelper survives) is unspecified. Decide between (a) defensive sweep inside `GuardCorrectiveActuator.SweepExpired` that rejects slots where `slot.TechIdAtWrite != helper.tankId` (requires stamping owning techId on the slot), or (b) clearing the array on every `IAIForm.Activate` call. Pick before mailbox writers ship — a leaked GroundedAircraft slot on a recycled Aviator hull = unconditional 5 s ascent impulse on a tech that never triggered the guard. Per-tech array cleared on OnTechDespawn; verify no slot leak across Tank pool reuse. |
| RM-10 | TrainerStats reservoir lock contention at 30 Hz × 4 models | Mirrors PathRequestBackpressure lock pattern (verified lock-based, not lock-free). **DESIGN PRE-COMMITMENT (Stage-2 promotion)**: ring capacity AND sort-on-read sample budget MUST be sized before TrainerStats.cs ships. Chosen caps: `LossReservoir capacity = 256`, `ParamDeltaReservoir capacity = 64` (mirrors `PathRequestBackpressure.LatencyReservoirSize`), p99 sort-on-read budget O(N log N) ≤ 20 µs at N=256. Lock-on-every-push at 4 models × 30 Hz × 1 minibatch ≈ 120 Hz aggregate is bounded; cap pending runtime measurement under sustained load. |
| RM-11 | `GuardEvaluationRequest` round-trip latency (worker emit → main-thread fill → worker read) at scaled tech count | 16 techs/frame budget + 1s staleness gate spec'd; throughput test pending. |
| RM-12 | `Tank.Holders.ItemPickupEvent` cross-tank-only gating (Visible.cs:910,928) interaction with same-tank holder-to-holder moves | Conveyor sort within one tank does not fire — confirmed semantically but not under stress. **DESIGN-LATENT (Stage-2 promotion)**: the deferred question is NOT the same-tank conveyor case (resolved by inspection per §10 / §13 design bullet) — it is whether a multi-stage Gatherer (chunk handed from one holder to a sibling holder before drop on a receiver tech) attributes correctly. If sibling-holder rotation is observed mid-haul, attribution becomes ambiguous; design contingency = stamp `(carrierTechId, firstSeenMono)` on Visible at first holder-acquisition and prefer that over the Release→Pickup pairing. Decide before implementing the publisher, not after — fixing post-spawn-test is a non-trivial publisher rewrite, not a tuning knob. |

#### Document integrity post-verification

The plan's load-bearing architecture (3-layer L1/L2/L3, 7-verb Director vocabulary, atomic checkpoint-trio rollback, IdentityOutcome-driven constraint engine, planner-vs-executor discipline for ScenarioWorker / ChunkRegen / GuardWorker, BehaviorGuards as observer-only training-signal source) is structurally preserved. Every cited engine primitive that survived has been re-verified at file:line precision against live source (Modified tree + Decompiled AssemblyCSharp). Every fictional or wrongly-named primitive has been replaced with its verified counterpart and the LOC budget adjusted to reflect realistic implementation cost. Concurrency primitives are now spelled out precisely (Interlocked.CompareExchange for rate limits, ref-counted AcceptingTrainingEvents, owner-keyed pause channel, per-envelope try/catch isolation, scenario-generation envelope stamping, NaN-safe entropy compute, wall-clock + Stopwatch dual-timestamp on snapshots). The plan is implementation-ready modulo the 12 NEEDS-MORE-EVIDENCE items above which require runtime spawn-test verification.

### 14.4.1 Decompile-deep-dive amendments (post-verification)

Subsequent to §14.4, a 19-agent decompile-deep-dive (3 rounds) + 6-agent adversarial pass was run against the plan. Outcome: **3 disproven** (DROPPED), **5 weakened** (APPLIED-WEAKENED — doc-only notes / reduced scope), **13 survived** (APPLIED-AS-IS). All 21 amendments are applied to this revision.

A subsequent independent plan-only reviewer pass surfaced **6 additional amendments A1-A6** (5 APPLIED + 1 REVERTED post-reconciliation, all real and actionable per full code context): A1 rollback EventQueue KEEP semantics (doc-only step 4.5); A2 eval-battery + checkpoint ratchet as fast-follow / pre-GRU-BPTT gate (new §7.5, NOT v1); A3 synthetic-pathology guard selftests (new §9.6 bullet + new console command, test-only LOC); **A4 REVERTED** — initial disposition rewrote §15 to substitution semantics on a misread of §9.7 (which actually says telemetry-only); reconciliation pass reverted §15 + §9.3 + §13 to V-fix telemetry-only direction; A5 stale "12 behavior monitors" → 11 in §15; A6 live-build M1 / B3 bugs added to §14 backlog independent of Director timing. Table below grows from 26 rows (the original 21 decompile-deep-dive amendments + the 5 paired follow-ups) to 32 rows with A1-A6 appended.

| Id | Disposition | One-line note |
|---|---|---|
| RM-2 | APPLIED-WEAKENED (doc-only) | If Tank.name tagging is retained as a debug aid, bake into TechData.Name BEFORE SpawnTech_SetNameAndCreator copies it; post-spawn tank.SetName is stripped by NetTech.OnServerSetName→RestoreTechName. |
| RM-3 | APPLIED-AS-IS | AllyProtection LRU 128: publisher tracks `evictionsPerMinute`; digest renders `ally_protect_evict_per_min=N`. |
| RM-4 | DROPPED-DISPROVEN | Investigated and ruled out (false positive — Tank.AttachEvent/DetachEvent firing order is acceptable for HP cache invalidation). |
| RM-6 | APPLIED-AS-IS | Worker-local List<ResourcePickup> fallback for census; `ManLooseBlocks.AllLooseBlocks` does NOT exist (only private `m_ChunkByPoolIDLookup` dict at ManLooseBlocks.cs:59). |
| M1 | APPLIED-AND-SHIPPED (audit BUG-004) | PathingService.Init now calls `DaemonWatchdog.RegisterCanonical` + `WorkerHealthMonitor.RegisterCanonical` for both `ThreatFieldRebuild` and `PathSolve` at PathingService.cs:170-173 (poolRef-closure null-guard). Live-patch gap closed. |
| M2 | APPLIED-AS-IS | Distinguish trigger constant `AIGlobals.DamageAngerDropRelations = 2500f` (AIGlobals.cs:427) from per-team accumulator `EnemyTeamData.angerThreshold` (ManBaseTeams.cs:159); ScenarioWorker zeroes the accumulator on each 1Hz tick. |
| M3 | APPLIED-AS-IS | Each of the 4 sealed model files (Intent/ActionValue/Residual/Threat) carries explicit ~15-20 LOC per-file breakdown for the 4 new ILearnedModel members; C# 7.3 has no default-interface-methods. |
| RM-7 | DROPPED-DISPROVEN | Investigated and ruled out (kinematic-detector accuracy preserved without sentinel-equality threshold). |
| RM-9 | APPLIED-WEAKENED | Use `IAIForm.OnTechRecycle(helper)` at TankAIHelper.cs:967 for mailbox cleanup; do NOT subscribe to Tank.TankRecycledEvent. |
| R3-B1 | APPLIED-AS-IS | PathingService.IsReachable routes through `TerrainMap.IsTraversable` O(1) DoubleBuffer read (ITerrainMap.cs:44); AIEPathMapper / AIEAutoPather are NOT thread-safe from background. |
| R3-B2 | APPLIED-AS-IS | `GuardInjectedGoalSlot` MUST be `class` (`Interlocked.Exchange<T>` requires `T : class`). Eviction-clear via CompareExchange to preserve racing fresh writes. |
| R3-B3 | APPLIED-WEAKENED (doc-only) | Anti-loop windows in MonoClock ticks; on long-pause resume, first post-pause fire counts against empty window (one extra fire allowed, by design). |
| R3-B4 | APPLIED-AS-IS | Drop AIEPathMapper.cs:27 cite (line 27 is class declaration, not a reachability accessor). |
| R3-M1 | APPLIED-AS-IS | `helper.FormState` is `public object`; bare-dot access won't compile — use `is SmartPerTechState state` pattern-match (matches 19 existing call sites). |
| R3-M2 | APPLIED-WEAKENED (doc-only) | AdamSnapshot.adamM/.adamV are caller-owned float[] buffers; ReadAdamMoments Array.Copy's INTO them under SaveMutex. Never wire capture through GetAdamSnapshot pointer-copy. |
| R3-M3 | DROPPED-DISPROVEN | Investigated and ruled out (verified primitive existed; no plan change required). |
| R3-M4 | REVISED | Original disposition added Provoked-polled `Interlocked.Exchange` eviction logic assuming OverdueDelivery used the mailbox. Follow-up investigation (two-agent feasibility pass) confirmed V-fix telemetry-only is binding for soft-corrects: no `TacticalGoal.None` sentinel, no "controller silent" detectable state. Provoked-polled eviction mechanism RETAINED for hard-corrects (AntiCrash, GroundedAircraft); OverdueDelivery does NOT use the mailbox so the eviction mechanism is moot for this guard. §9.3 rewritten to telemetry-only in the same pass. |
| R3-M5 | APPLIED-WEAKENED (reduced scope) | Recycled() cleanup ONLY for new fields the plan adds (mailbox slots, recentSpeedSigned/XZ/Y rings, PublishedFrustrationMeter). Do NOT expand to pre-existing gaps. |
| R3-M6 | APPLIED-AS-IS | TrainerBarrier.AcquirePauseToken hardening: (a) mandatory try/finally; (b) 30s stuck-token watchdog → force-release + LEAKED journal; (c) Director.BeginShutdown signal-and-wait, NOT acquire. |
| B1 | APPLIED-AS-IS | 3 stale MonoClock arithmetic sites (lines 942, 1456, 1457) corrected to `MonoClock.Now() + MonoClock.FromSeconds(sec)`. |
| B2 | APPLIED-AS-IS | OutcomeWeights design owned explicitly: NEW field on Smart/Learning/LearningTuning.cs as `static float[] OutcomeWeights = new float[(int)OutcomeKind.Count]`; GuardViolation_* default 0.0, others 1.0. |
| B3 | APPLIED-AND-SHIPPED (audit BUG-022) | ProfilePersistence.Save now wraps StoreParameters + Adam-payload read under `lock (m.SaveMutex)` at ProfilePersistence.cs:118-126; weights and M/V/T snapshot from one consistent moment. Live-patch gap closed. |
| C1 | SKIPPED-NOT-FOUND | No "OnlineTrainer.cs:75+" stale cite found in plan (closest references are line 50 ArchitectureVersion byte, line 141 AdamState.Step). Re-verify if it surfaces. |
| C2 | SKIPPED-ALREADY-CORRECT | Plan already uses `Smart/World/EventBus.cs` filename throughout (4 occurrences); no `WorldEventBus.cs` filename appears. |
| S1 | APPLIED-AS-IS | §1 status header updated to record the 19-agent decompile + 6-agent adversarial pass with disposition counts. |
| S2 | APPLIED-AS-IS | This subsection itself — §14.4.1 added listing all 21 amendments by id with one-line dispositions, plus 3 DROPPED-DISPROVEN rows for RM-4, RM-7, R3-M3. |
| A1 | APPLIED-AS-IS (doc-only; post-feature-expansion update) | Rollback EventQueue KEEP semantics: pre-rollback events RETAINED through rollback (3 of 4 models observation-based; ActionValue now has a live producer at ContinuousController.cs:514 per feature-expansion e9bfcd4, but contamination is bounded by the same per-envelope multiplicity cap = 2 in `EventEnvelope<ActionValueEvent>` as the other three; queues bounded by BoundedQueue 4096). §7.3 step 4.5 carries `QUEUE-CARRYOVER:<n-events>` forced-digest observability — now load-bearing for ActionValue. No LOC impact. |
| A2 | APPLIED-AS-IS (fast-follow, NOT v1) | Eval battery (S1+S2+S3, pinned `DirectorTunables.BatterySeed`, 5 min each) + checkpoint ratchet (`battery_score(candidate) ≥ battery_score(incumbent) − epsilon`, default 0.02 via `DirectorTunables.RatchetTolerance`) gates T2 / T3 promotion. T1 ring exempt. PRECONDITION for enabling GRU BPTT / unsupervised training of the 3 dormant models. New §7.5; §14 backlog gate added. ~400-600 LOC, NOT counted in v1 totals. |
| A3 | APPLIED-AS-IS (test-only LOC) | Synthetic-pathology selftests: DevCommand-invoked suite injects each pathology and asserts the corresponding guard fires within a bounded window. §9.6 new bullet + `smart.director.guards.selftest` console command. ~400 LOC scaffolding + ~20 LOC dispatcher; footnoted on §12.3 totals as test-only, not core v1. Closes under-firing telemetry gap. |
| A4 | REVERTED | Original A4 disposition rewrote §15 to substitution semantics on the (mistaken) premise that §9.7 was binding-substitution. Investigation showed §9.7 step 5 explicitly says "TELEMETRY HINT, never substitutes" — V-fix BLOCKER decision was telemetry-only. A4's substitution-aware §15 rewrite is REVERTED. §15 + §9.3 + §13 all reconciled to V-fix telemetry-only direction. The A4 reviewer correctly identified the §9.7-vs-§15 contradiction; the resolution direction was misjudged in the initial evaluation. |
| A5 | APPLIED-AS-IS (count correction) | §15 "12 behavior monitors" → 11 (OrbitalLockup dropped during V-fix per Validator-1 double-jeopardy with OrbitNoFire; §1 + §9.3 + §9.8 already 11). No LOC impact. |
| A6 | SHIPPED (live patch) | Both pre-existing bugs from `eac2e0e` are now FIXED in the live build: M1 at PathingService.cs:170-173 (audit BUG-004), B3 at ProfilePersistence.cs:118-126 (audit BUG-022). §14.1 backlog cleared; inline §12.3 (M1) and §12.2 (B3) references stay accurate as historical context. No further live-patch action required. |

### 14.4.2 Post-implementation reconciliation pass (post-feature-expansion + post-78-bug audit)

A 10-agent stage-1 reconciliation pass (this revision) reconciled the Director plan against two shipped efforts that landed after the plan was authored:

(a) **FEATURE-EXPANSION-PLAN.md shipped** (commit e9bfcd4 + producer closeout, workflow w4ias0o3g): StrategicStateVector + StrategicStateExtractor daemon + SelfStateProbe + StrategicStateBuffer + ActionValue producer at `ContinuousController.cs:444/514` + all-4-models ArchitectureVersion bump 2/1/1/1 → 3 + DamageHintBuffer 7th DamageType byte field + HealthSidecar HP-history ring + CargoStatePublisher + NearestTechCache + WeaponFireBuffer + EnemyVehicleSnapshots + Threat 48-slot rewrite + Intent 40-slot rewrite (`FillIntentSlots` in StrategicStateExtractor).

(b) **AUDIT-BUG-LIST.md 78-bug pass shipped** (workflows wsgt4cbj7 + wtxvtdhcr): BUG-001/002 false-positives (orphan files deleted, no compile change); BUG-004 PathingService factory registration ADDED (M1 RESOLVED at PathingService.cs:170-173); BUG-022 ProfilePersistence SaveMutex wrap ADDED (B3 RESOLVED at ProfilePersistence.cs:118-126); BUG-023 UnknownTags preservation (Save signature widened); BUG-024 ArchitectureVersion Load guard ADDED; BUG-013/050 TerrainPublication atomic-swap adopted by PathingService (`_terrainPub` at line 102); BUG-046 AdamState persistence TLV tag 0x0002 ADDED; BUG-026 EnemyVehicleSnapshots real implementation; BUG-009 StrategicStateExtractor FillResidualSlots; BUG-010 SelfStateProbe LastAttacker falling-edge clear; BUG-011 ActionValueEstimator SARSA → Q-learning (max_a' helper); BUG-012 TrainerWorker HostChanged Unsubscribe paired; BUG-051 PathingService one-time `_worldResetRegistered` latch; BUG-052 WeaponFireBuffer `_wiredWeapons` shadow dict + DetachAll; BUG-053 SmartEventBridge.Uninstall three-surface teardown; BUG-080 WorkerLifecycleRegistry.CancelAllAndJoin straggler cleanup; BUG-055 SaveProfile entry IsHost-gated; BUG-018/019 caller-side host gates on QuitSaveCoordinator + SmartForm.OnEngineSave; BUG-017 ManWorldRTS state-assign-before-MP-return; BUG-021 cross-team SetRelations host-gated at 3 sites; BUG-015 ModifiedForm Aviator/Buccaneer/Astrotech dispatch; BUG-008 TerrainMap water gating; BUG-035 TrajectoryOptimizer Volatile discipline; BUG-041 PUCTSearch learned-prior wiring; BUG-007 GUIInteraction catch-and-log; BUG-006 pretraining surface trimmed (no Director consumption); BUG-028 IMovementAICore.AvoidAssist removed; BUG-042 SmartIdentityTuning cluster; BUG-043 stale-doc cluster; BUG-025/036 CanonicalRoster expanded 9 → 13 (StrategicStateExtractor + TeamReaper + Autosave + TechLeakWatchdog).

**Stage-1 changes applied** (10-agent fan-out; ≥2-agent agreement applied; 1-agent items source-verified before apply; conflicts resolved against shipped source):

| Section | Change | Disposition |
|---|---|---|
| §1 status header | Append precondition-shipped + 78-bug audit narrative; LOC drop ~11 730 → ~11 690 | APPLIED |
| §2 L3 row + §13 design bullet + §9.2 daemon-roster prose | CanonicalRoster currently 13 (was 9; verified `DaemonWatchdog.cs:33-48`); base plan = 17 → with GuardWorker = 18 | APPLIED |
| §5 replay_bank row | ActionValueEvent producer SHIPPED at ContinuousController.cs:444/514; 4 tee sites; replay_bank(*, ActionValue) functional | APPLIED (BLOCKER per 5-agent agreement) |
| §7.3 step 4.5 (EventQueue KEEP rationale) | ActionValue queue is NO LONGER empty in v1; KEEP rationale reframed (contamination bounded by per-envelope cap 2, same as observation models); QUEUE-CARRYOVER line now load-bearing for ActionValue | APPLIED (BLOCKER) |
| §7.1 piggy-back mechanism | Cross-ref BUG-055 SaveProfile host gate + BUG-018/019 caller-side gates; `!SmartRuntime.IsHost` clause kept as defense-in-depth for Director-owned sibling JSON writes | APPLIED |
| §7.3 trainer FSM block + §12.2 OnlineTrainer row | Line cites drifted: 355 → ~357, 391 → ~393, 407 → ~409, 396 → ~398, 329-333 → ~331-333; BUG-012 HostChanged Unsubscribe noted as load-bearing precondition | APPLIED |
| §9.2 BeliefSnapshot kinematic-channel claim | Replaced with SelfStateProbe.TryRead → SelfProbeSnapshot (FEATURE-EXPANSION §5.1); marshaling scope reduced (terrain height + AILimitSettings + FrustrationMeter only; Provoked / LastAttacker / WaterHeight / ForwardWorld / PositionWorld / VelocityWorld all in snapshot per BUG-009/010); Belief.ByTech reserved for cross-team | APPLIED |
| §9.3 WaterloggedTech row | Reads via SelfProbeSnapshot.PositionWorld.y vs SelfProbeSnapshot.WaterHeight directly (no marshaling); -9001f fallback skip | APPLIED |
| §9.3 GroundedAircraft pitch / altitude derivation | Pitch from SelfProbeSnapshot.ForwardWorld (no marshaling); altitude uses snap.PositionWorld + terrain query (only terrain query marshaled) | APPLIED |
| §9.7 priority chain | New MP RTS-state coexistence note: BUG-017 host-asserts RTS state before MP early-return; guard mailbox writes are HOST-ONLY by construction; clients collapse to priority 1+; BUG-021 host-gates cross-team SetRelations at 3 sites | APPLIED |
| §9.9 MP soft-correct safety bullet | Cross-ref BUG-055 + BUG-018/019 + BUG-021 — Director training-signal pipeline is end-to-end host-authoritative; forward-looking rule recorded for any future soft-correct mailbox writer | APPLIED |
| §10 publisher post-table paragraph | ActionValueEvent producer SHIPPED at ContinuousController.cs:514; tee at 4 sites (not 3); Threat 48-slot + Intent 40-slot shipped; replay envelopes inherit new slot layouts | APPLIED (BLOCKER per 5-agent agreement) |
| §10 lead | StrategicStateVector / Extractor / SelfStateProbe / DamageHintBuffer 7th DamageType byte / HealthSidecar HP-history / CargoStatePublisher / NearestTechCache / WeaponFireBuffer / EnemyVehicleSnapshots SHIPPED; reuse rather than re-spec; CargoStatePublisher partially subsumes BlockDelivered carrier-tracking — delta on top | APPLIED |
| §10 IntentEvent / ResidualEvent / ThreatEvent cite drift | IntentEvent ~210 (was 186, DrainAndEnqueue body); ResidualEvent ~121 (was 99); ThreatEvent ~683 (was 694; drifted after BUG-026/046/022) — cites flagged "post-audit; revalidate at impl time" | APPLIED |
| §12.2 LearningService row | (e) ActionValueEvent producer SHIPPED; no LearningService-side wiring; line drifts noted | APPLIED |
| §12.2 ProfilePersistence row | B3 RESOLVED via BUG-022; ArchitectureVersion Load guard via BUG-024; AdamState TLV tag 0x0002 via BUG-046; atomic-write File.Replace at lines ~231-249 (post-audit drift); LOC dropped 60 → 40 | APPLIED (BLOCKER per 4-agent agreement) |
| §12.2 DaemonWatchdog rows (base + guard extension) | Roster math 13 → 17 → 18 (was 9 → 13 → 14); docstring updates | APPLIED (BLOCKER) |
| §12.2 EventBus row | OutcomeKind enum location ~163-169 (was 151-157); feature-expansion event-struct manifest noted (StrategicStateVector + WeaponFireBuffer + CargoStateEnvelope + EnemyTechSnapshot already shipped) | APPLIED |
| §12.2 SmartRuntime row | EnumerateTeams cite drift 1158 → 1396; Shutdown anchor 931 → 1120 (Stage-2 meta-correction: Stage-1 said 1128, actually 1120 verified); SelfStateProbe lookup accessor noted (item i); BUG-080 CancelAllAndJoin noted (item j); BUG-028 AvoidAssist removed | APPLIED |
| §12.2 SmartEventBridge row | KillScored publish drift 273 → ~329-330; Generic filter ~326 → ~326 (within file growth); BUG-052/053 teardown pattern cross-ref | APPLIED |
| §12.3 PathingService new-file row | Reduced 80 → 40 LOC (file already exists with TerrainPublication atomic-swap + BUG-004 daemon-factory registration shipped); helpers ADDED ONTO existing service | APPLIED |
| §12.3 LOC grand total | ~11 730 → ~11 690 (PathingService -40, ProfilePersistence -20, audit-shipped scaffolding subtracted; partial re-credit) | APPLIED |
| §13 design decisions | New bullet: precondition surfaces shipped (e9bfcd4 + 78-bug audit) — SelfStateProbe, ActionValue producer, ArchitectureVersion v3, FillResidualSlots (BUG-009), LastAttacker latch (BUG-010), Q-learning max_a' (BUG-011), ROLLBACK-SKIPPED-ARCH path (BUG-024) | APPLIED |
| §14.1 backlog M1 + B3 | Both marked DONE (audit BUG-004 + BUG-022); backlog cleared | APPLIED (BLOCKER per 5-agent agreement) |
| §14.4 RM-4 + RM-7 | Marked RESOLVED to match §14.4.1 DROPPED-DISPROVEN | APPLIED |
| §14.4 categorized-fixes row 2165 (Type-erasure / ActionValueEvent) | Replaced `v0.3-dependency` with "now SHIPPED (e9bfcd4 closeout) — all 4 tees active" (no remaining v0.3 references in plan body) | APPLIED |
| §14.4.1 M1 + B3 + A6 + A1 rows | M1 / B3 → APPLIED-AND-SHIPPED with file:line; A6 → SHIPPED (live patch); A1 → updated ActionValue producer cite | APPLIED |
| §15 final-line | LOC ~11 730 → ~11 690 (post-feature-expansion + post-78-bug audit baseline); CanonicalRoster math 9 → 13 → 17 → 18 | APPLIED |

**Stage-1 changes REJECTED**: 0. No agent proposed deferring shipped work to a future plan revision — every "still TODO" reframing was rejected by construction per task brief.

**Items rolled forward to stage 2** (broader-scope follow-ups not landed this pass): full sweep of every drifted file:line cite across §12.2 (SmartForm `OffsetFromGroundA` site, LearningService `modDirectory` convention cite, IdentityOutcomeConsumer counter-dict cite range, OpponentIntentClassifier softmax line in FullBptt path, TrainerBarrier.Signal definition line, OpponentIntentClassifier LoadParameters / `_publishedParams.Write` cites, all SmartForm `Operations`-drain anchor cites) — many flagged inline as "post-audit drift; revalidate at impl time" in this pass; impl-time grep is the canonical resolution. The §10/§12.1 BlockDeliveredPublisher overlap with shipped CargoStatePublisher is a more substantive scope-review (does the Director's BlockDeliveredPublisher reduce to a thin delta on top of CargoStatePublisher?), held for stage 2. SelfStateProbe.TryRead-based optimization of §10 AllyProtectionPublisher (currently BeliefSnapshot.ByTech) is optional and held for stage 2.

**Stage-2 changes applied** (5-agent fan-out — cite-verify, v0.3-residual,
cross-section, missing-acknowledgements, implementation-readiness — plus an
independent validator self-validation pass; ≥2-agent agreement applied,
1-agent items source-verified before apply, false positives rejected):

| Section | Change | Disposition |
|---|---|---|
| §1 status header + §6.3 IMGUI panel | `SmartForm.cs:643-645` cite (pre-existing) → `:688` (single-line `UnityEngine.GUI.Box` call inside `DrawPathingDebugGUI` which begins at `:678`) — Stage-1 missed this drift | APPLIED (cite-verify) |
| §1 status header (KillScored cite) | `SmartEventBridge.cs:273` → `:330` (post-audit drift; Stage-1 noted the drift in the §14.4.2 table but did not propagate to the inline §1 cite) | APPLIED (cite-verify HIGH) |
| §1 status header (LOC) | `~11 700 (was ~11 730; ~30 LOC drop` → `~11 690 (was ~11 730; ~40 LOC drop` — reconciles with §12.3 / §14.4.2 / §15 all consistent at ~11 690 / ~40 | APPLIED (cross-section MAJOR) |
| §2 L3 row (softmax line) + §2 L3 row (Operations cite) + §2 L3 row (Phase 4 isolation cite) | softmax `256` → `266` (TrainOneMinibatch_FullBptt actual location); `SmartForm.cs:472-479` → `:515-525` ("called once per Smart-driven tech per physics frame" comment block); `SmartForm.cs:500-508` → `:498-513` (LearningService.PeriodicTick try/catch) | APPLIED (cite-verify) |
| §2 L3 row + §4 entropy row (v0.2 leftover labels) | "Evaluate is unwired in v0.2" → "Evaluate is the inference path BUT has zero consumer call sites in the current source (unwired); softmax at line 129" — grep-landmine for "v0.2" cleared | APPLIED (v0.3-residual) |
| §2 Rationale | "All four new daemons" → "All five new daemons (TrainingDirector, DirectorInbox, ScenarioWorker, ChunkRegen, BehaviorGuardWorker)" reconciling with §9.2 / §12.2 which already enumerated 18 = 13 base + 5 new; §13 design bullet "3 layers × 4 daemons" → "3 layers × 5 daemons" same fold-in | APPLIED (cross-section MAJOR) |
| §2 Rationale | CanonicalRoster decomposition rewritten: "9 base + ThreatFieldRebuild + PathSolve + StrategicStateExtractor + ..." (which double-counted ThreatFieldRebuild + PathSolve) → "AetherFuser + GlobalPlanner + GlobalCoordinator + ThreatFieldRebuild + PathSolve + 4 Trainers + StrategicStateExtractor + TeamReaper + Autosave + TechLeakWatchdog = 13" matching `DaemonWatchdog.cs:33-48` source | APPLIED (cross-section MINOR) |
| §2 Rationale (PathRequestBackpressure cite) | `PathRequestBackpressure.cs:152-178` → `:155-172` (push at 155-157; snapshot-and-sort at 167-172; field decl at :61, lock at :64) | APPLIED (cite-verify) |
| §3.2 persistence path | `LearningService.cs:78` → `:75` (`Init(WorkerPool pool, string modDirectory)` parameter signature; was rolled-forward in Stage-1 reconciliation table but not updated) | APPLIED (cite-verify) |
| §4 `intent_entropy_ratio` row + §5 `temp_adjust` row | softmax `256` → `266` (all three occurrences) | APPLIED (cite-verify) |
| §4 hunter row + §4 base_hp_retention_rate row | `SmartEventBridge.cs:269` → `:326`; KillScored `:273` → `:330`; EventBus OutcomeKind enum `:151-157` → `:163-169` (Stage-1 noted the §12.2 drift but missed the §4 inline cites) | APPLIED (cite-verify) |
| §5 `freeze_model` row | `TrainerWorker.RunLoop line 411 (IsHost gate) and line 412 (TrainOneMinibatch)` → `line 409 (IsHost gate) and line 421 (lock acquire) / 423 (TrainOneMinibatch)`; inside-lock cite `419` → `421` | APPLIED (cite-verify) |
| §7.2 LoadParameters cite | `OpponentIntentClassifier.cs:350-351` → `:358` | APPLIED (cite-verify) |
| §7.3 step 4.5 (v0.2 leftover) | "was vacuous in v0.2" → "load-bearing for ActionValue now that the producer at `ContinuousController.cs:514` ships per FEATURE-EXPANSION-PLAN §7.2" — grep-landmine cleared | APPLIED (v0.3-residual) |
| §10 BlockDelivered row + §12.1 BlockDeliveredPublisher.cs row | §12.1 LOC `180` → `240` to match §10 row "revised from 180 — original undersized for Release/Pickup pairing + non-Smart spam filter + Visible recycle hook" (Stage-1 updated narrative but not manifest) | APPLIED (cross-section MAJOR) |
| §12.3 LOC arithmetic | New files re-credited ~9 540 → ~9 560 (+20 LOC for BlockDeliveredPublisher 180→240 fold-in); new + modified = 9 560 + 2 130 = 11 690 (was 9 540 + 2 130 = 11 670 ≠ 11 690 advertised) | APPLIED (cross-section MAJOR) |
| §12.2 SmartRuntime row + §14.4.2 Stage-1 table | SmartRuntime.Shutdown anchor `~1128` → `1120` (Stage-1's `1128` was itself an 8-line miscount; verified at `SmartRuntime.cs:1120 public static void Shutdown()`) | APPLIED (cite-verify META) |
| §13 design bullet (Shutdown line) | inline "line ~931" → "line 1120 post-audit drift; was ~931 pre-audit" | APPLIED (cite-verify) |
| §13 OverextendedHunter bullet (v0.x phrasing) | "defer unless requested" → "not included by default — operator must opt-in once the tunable lands" — grep-landmine cleared | APPLIED (v0.3-residual) |
| §14.2 + §14.3 legends | DEFERRED bullet dropped from both legends (no row uses it); §14.3 legend extended with MOOT and SUPERSEDED / PARTIALLY-SUPERSEDED definitions (used in table without being defined) | APPLIED (v0.3-residual + legend-drift) |
| §14.4 NEEDS-MORE-EVIDENCE table | Prefix sentence added clarifying these are runtime-verification items, NOT plan-revision deferrals; RM-8/RM-10 "not yet" phrasing → "pending runtime exercise/measurement"; RM-9/RM-10/RM-12 PROMOTED to DESIGN-LATENT (Stage-2 implementation-readiness) — they require design pre-commitments before code ships, not just runtime tests | APPLIED (v0.3-residual + implementation-readiness MAJOR) |
| §14.1 Operator open questions (Guard re-arm chronic conditions) | PROMOTED to DESIGN-LATENT: per-(tech, guard) chronic-suppression after N=5 consecutive re-fires emitting `[GUARD-CHRONIC]:guardName:tech=T` once, then guard-on-that-tech goes silent until pathology test transitions through FALSE for ≥ 5 min — structurally required for digest-budget bound; not operator preference | APPLIED (implementation-readiness MAJOR) |
| §11 ChunkRegen Spawn-logic row | Census-semantics doc note added: OverlapSphere returns ALL chunks (ambient + Director); worker-local list only Director-spawned; the two are NOT interchangeable; canonical census = OverlapSphere; fallback applies only when LayerMask binding fails (failure-mode bound: ChunkRegen disabled for that scenario); future maintainers MUST NOT swap to worker-local list as a "perf optimization" — spawn-storm risk | APPLIED (implementation-readiness MINOR) |
| §7.5 Checkpoint ratchet | BatteryScoreWeights default vector explicit pre-commitment: KillScored=+1.0, BlockDelivered=+1.0, BaseHeld=+1.0, AllyProtected=+1.0, BaseLost=-1.0, GuardViolation_Movement/Role/Combat=-0.5 each; BPTT cannot be enabled while weights are unfixed — ratchet rule meaningless until weights ship | APPLIED (implementation-readiness MAJOR) |
| §14.4.2 audit cross-references | BUG-014 (TeamRuntime.Dispose at SmartRuntime.cs:1173); BUG-020 (m_USE_AVOIDANCE restore at SmartForm.cs:426); BUG-047 (LoadParameters under SaveMutex at LearningService.cs:282 + :619); BUG-048 (live PathingService.CurrentTerrain read at StrategicStateExtractor.cs:69, 175); BUG-066 (`_pendingActionState` gate-closed clear at ContinuousController.cs:454-463); BUG-072 (slot-30 selfProbe.ForwardWorld alignment at StrategicStateExtractor.cs:803); BUG-073 (SmartForm.DeInitGlobal explicit unwire at SmartForm.cs:109/127); BUG-075 (baseline-tier fourth load fallback at LearningService.cs:223/386/416); BUG-076 (tmp-built-before-rotation at ProfilePersistence.cs:90-103); BUG-077 (Load drops hardcoded 4-section ceiling at :436); BUG-078 (AutosaveWorker.TickOnce host-loss re-check at :73). All 11 shipped audit fixes touch Director-cited code paths but were absent from Stage-1; now cross-referenced | APPLIED (missing-acknowledgements MEDIUM/HIGH) |
| §14.4.2 Stage-1 reject line | "to a future v0.3" → "to a future plan revision" — grep-landmine cleared | APPLIED (v0.3-residual) |
| §14.4.2 Stage-1 row 2375 | "Replaced `v0.3-dependency` with ..." → same text + "(no remaining v0.3 references in plan body)" trailing closure note so a future grep does not re-flag | APPLIED (v0.3-residual) |

**Stage-2 changes REFUTED**: 0. All five Stage-2 reports overlapped on at least
one finding with another or with the validator self-check; every flag survived
the cross-check. The independent self-validation pass located three drifts that
Stage-2 also caught (SmartForm.cs:688, softmax line 266, LearningService.cs:75)
plus one Stage-2-only finding the validator missed (`OnlineTrainer.cs:419` lock
cite is actually `:421`); all applied.

**Stage-2 changes RECONCILED**: the daemon-count desync (§2 / §13 say "4
new daemons"; §9.2 / §12.2 say "18 = 13 + 5 with GuardWorker") + the LOC
arithmetic gap (§12.3 said 9540 + 2130 = 11690 when 9540 + 2130 = 11670)
are the two cross-section findings where Stage-2 cross-section, Stage-2
self-validation, and the cross-section in the FEATURE-EXPANSION plan all
agreed. Both fixed.

**Stage-2 changes META** (Stage-2 self-corrections to Stage-1): Stage-1's
"`Shutdown anchor 931 → 1128`" was itself an 8-line miscount — actual location
verified at `SmartRuntime.cs:1120` via `grep -n "public static void Shutdown"`.
Stage-1's table was updated; the §12.2 inline SmartRuntime row was also
corrected; §13 design bullet "line ~931" was updated to "line 1120 post-audit
drift; was ~931 pre-audit". This is a cosmetic but load-bearing reconciliation
between sibling tables.

---

## 15. What success looks like

A single host runs a multi-hour TerraTech session with `smart.director.enabled=true`.
The operator types one directive every hour or so (`maintain
gatherer.delivery_rate >= 0.5/min for 30min`, `prefer scenario S5 weight 0.5`,
`checkpoint after-good-run`). Every 10 minutes a digest lands in
`SmartAI/Director/digest.latest.txt` with ~10 numbers per model, populated-only
identity rates, a guard-violation roll-up surfacing the 11 behavior monitors at
their per-bucket rates (movement / role / combat / safety), a constraint OK/VIOL
summary, the last 10 minutes of interventions, active directives, available
rollback targets, and a one-line hint. When a metric
drifts out of its band, the Director picks the highest-severity violation, fires a
single verb against trainer state under `model.SaveMutex`, journals the action, and
holds that constraint in observation for 5 minutes while it gives other budgets room.
When a behavior-guard pattern emerges (a Gatherer hauling cargo for 90 s without
returning to base, an Aviator nosediving), the system handles it without operator
intervention. **OverdueDelivery** is a telemetry-only soft-correct: when the
pathology window triggers (Gatherer at >=75% cargo with no `BlockDelivered`
event for 90s AND no net displacement toward nearest friendly Harvesting base),
the guard publishes `IdentityOutcome(GuardViolation_OverdueDelivery)` for
training-signal consumption ONLY. The controller is never influenced by this
guard — there is no mailbox injection, no Q-margin override, no goal
substitution. Kinematic detection over the 30s window after publish records
whether the tech moves toward base (for training-signal correlation only, not
arbitration). See §9.7 step 5 for the structural rationale: the substitution
gate's preconditions (`TacticalGoal.None` sentinel, "controller silent" state)
cannot be implemented because those primitives do not exist in the codebase.
**GroundedAircraft** injects a 5 s ascent impulse that releases unconditionally
on the wall-clock deadline. Both firings are journaled and surfaced in the
digest with arbitration outcomes so the operator can audit which corrections
mattered. When something goes wrong — runaway
parameter delta, a stuck base-defence scenario — the operator types `rollback
memory threat` or `rollback to previous`; the Director
acquires its own pause token (not the host-loss one), restores params + AdamState atomically under
`SaveMutex`, validates `ArchitectureVersion`, releases the token, and emits a forced
digest that names the standing directives about to re-clobber state so the operator
can rescind in the same breath. No popups, no torrents — file-only `[DIRECTOR-DIGEST]`
lines, an IMGUI panel that can be folded away, and a JSON sibling an LLM can ingest.

---

Final plan: **~11 690 LOC** (post-feature-expansion + post-78-bug audit baseline), **52 new files**, **19 distinct modified-file paths** (17 base + 2 newly explicit: `TankAIHelper`, `ContinuousController`), **7 actions**, **15 constraints**, **6 scenarios (S6′ replaces S6)**, **4 publisher sources** — `BlockDelivered` via cleaner per-tank `tank.Holders.ItemPickupEvent` (V-fix MAJOR — was per-holder TakeItemEvent), paired `BaseHeld`+`BaseLost` (where `BaseLost=4` is a NEW OutcomeKind), graded `AllyProtected`, **plus `BehaviorGuardWorker` as the 4th source emitting `GuardViolation_*` outcomes** — plus the existing `KillScored`. **11 BehaviorGuards** (was 12; `OrbitalLockup` dropped per Validator-1) producing 3 new `OutcomeKind` values (`GuardViolation_Movement` / `_Role` / `_Combat`) feeding 4 new bucket-rate constraints. Total NEW OutcomeKind values added: 4 (BaseLost + 3 GuardViolation_*) — cardinality grows 4 → 8. Guard severity: **5 observe / 4 warn / 1 soft-correct (OverdueDelivery — telemetry-only per §9.7 step 5 BLOCKER direction; guard publishes `IdentityOutcome(GuardViolation_OverdueDelivery)` for training-signal consumption ONLY, controller never influenced; substitution gate preconditions `TacticalGoal.None` sentinel + "controller silent" state do not exist in the codebase so substitution is structurally infeasible; kinematic detection records influence when policy converges; amendment A4 REVERTED — the initial substitution rewrite was based on a misread of §9.7) / 1 hard-correct (GroundedAircraft, 5 s release ceiling + 2 s MaintainAltitude ramp-down — release uses MonoClock.FromSeconds(5.0) NOT literal `+5000` per BLOCKER-fix on Stopwatch units)** — 10 of 11 emit training-signal only. The soft-correct vs hard-correct asymmetry: hard-corrects substitute via the per-tech `GuardInjectedGoals` mailbox; soft-correct is telemetry-only and the mailbox array carries slots ONLY for hard-correct ordinals (AntiCrash, GroundedAircraft). Guards are observer-only at the reward channel — bifurcated `_guardCounters` dict in IdentityOutcomeConsumer ensures guards CANNOT reach the reward path by construction (V-fix MAJOR vs single-point filter fragility). The AntiCrash mailbox check moves BEFORE Coordinator's external-goal at ContinuousController.cs:208 so safety > coordination (V-fix BLOCKER). Hard-coded canonical-roster count: **13** (current source post-audit BUG-025/036; was 9 pre-audit) → 17 (base plan + Director/Inbox/Scenario/ChunkRegen) → 18 (with GuardWorker).
