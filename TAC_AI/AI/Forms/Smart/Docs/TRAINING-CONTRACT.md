# TRAINING-CONTRACT.md

**Subsystem:** Training/ (DEV-ONLY)
**Form:** Smart
**Version:** 0.1.0
**Status:** AUTHORITATIVE — Defines Smart's self-play harness (hybrid Smart-vs-Smart + baseline benchmarks), scenario generation (procedural + curated library), multi-objective outcome scoring, CMA-ES hyperparameter search, pretraining pipeline that feeds Learning's baseline, and the `#if SMART_DEV` build mechanism that excludes Training from release.

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The self-play harness orchestration (match coordination, two-instance setup).
- The scenario generator: procedural rules, library scenario format, dispatch between modes.
- The outcome scorer: per-match multi-objective composite score.
- The evolutionary hyperparameter search (CMA-ES) loop.
- The pretraining pipeline that produces the baseline weights consumed by [LEARNING-CONTRACT.md §7](LEARNING-CONTRACT.md#section-7-shared-baseline--per-player-refinement).
- The `#if SMART_DEV` conditional compilation discipline that excludes Training from release builds.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals, particularly §1.7 (self-play training infrastructure, dev-only) and §3.3.x (non-goals about behavior).
- [ARCHITECTURE.md](ARCHITECTURE.md) for threading, MP host/client gating.
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for worker pool, double-buffer, cancellation.
- [LEARNING-CONTRACT.md](LEARNING-CONTRACT.md) for the four model architectures and the pretraining target format.
- All other subsystem contracts — Training runs full Smart instances; each subsystem participates.

**This document GOVERNS:**
- The dev-build artifact pipeline: how `Smart.PretrainedBaseline.bin` (consumed by Learning) is produced.

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What Training owns

Training/ owns Smart's dev-time training infrastructure:
1. **Self-play harness** — runs Smart-vs-Smart matches.
2. **Scenario generation** — procedural battlefields + curated library.
3. **Outcome scoring** — multi-objective composite per match.
4. **Hyperparameter search** — CMA-ES over Smart's tunable constants.
5. **Pretraining pipeline** — generates baseline weights from accumulated training matches.

Training does NOT own:
- The shipped pretrained baseline file (that's an output that Learning consumes).
- The release-time AI behavior — Training only runs in dev builds with `#if SMART_DEV` defined.
- The decision to release a new baseline — that's a human gate.

### 1.2 Decisions resolved at v0.1.0

[NORMATIVE]
- **Self-play mode: hybrid.** Smart-vs-Smart primary; periodic baseline benchmarks (every Nth match, N provisional 20) against a frozen scripted opponent.
- **Scenario generation: hybrid.** Procedural for training matches; library scenarios for evaluation runs.
- **Outcome scoring: multi-objective composite.** Weighted sum of survival, damage ratio, time efficiency, friendly preservation. Weights are themselves tunable hyperparameters.
- **Hyperparameter search: CMA-ES.** Modern, sample-efficient evolution strategy for continuous hyperparameter spaces.
- **Dev-only build: `#if SMART_DEV`.** Every Training/ source file wrapped in conditional compilation. Release builds compile without the symbol; Training is absent from the release DLL.

---

## SECTION 2: SELF-PLAY HARNESS

### 2.1 Match types

[NORMATIVE] The harness runs two match types:

- **Self-play (~95% of matches):** Two Smart instances (`SmartForm`) compete. Both load the same current candidate parameters; both train online during the match; their per-player profiles diverge as the match progresses.
- **Baseline benchmark (~5% of matches):** One Smart vs. one frozen scripted baseline (a `SimpleScriptedOpponent` that implements `IAIForm` with hand-coded reactive logic). The scripted baseline does not change; Smart's win-rate over a rolling window of benchmarks is the absolute progress signal.

[NORMATIVE] The 95/5 split is provisional. The scripted opponent's behavior is intentionally simple (forward engage, retreat at low health, basic target selection) — it's a fixed reference, not an opponent designed to challenge Smart.

### 2.2 Match orchestration

[NORMATIVE] A `TrainingMatch` instance represents one run:

```
public sealed class TrainingMatch
{
    public TrainingMatch(Scenario scenario, MatchMode mode, HyperParams candidate, CancellationToken ct);
    public MatchOutcome Run();   // synchronous from caller's perspective; runs the full match
}
```

[NORMATIVE] `Run()` :
1. Instantiate two `SmartForm` instances (or one Smart + one baseline for benchmarks).
2. Apply `candidate` hyperparameters to both sides (in self-play) or only Smart (in benchmark).
3. Spawn techs per `scenario.spawn_specs`.
4. Activate both AIs; let them tick until match termination condition.
5. Stop the simulation; collect telemetry.
6. Return `MatchOutcome` (raw telemetry; scoring happens separately in §4).

### 2.3 Termination conditions

[NORMATIVE] A match ends when any of:
- One team has zero alive techs.
- Match-time limit reached (provisional: 300 seconds simulated).
- Stalemate detected: both teams have alive techs but no damage dealt in last 60 seconds.
- Cancellation token fires (harness shutdown).

### 2.4 Headless mode

[NORMATIVE] Training matches run **headless** — without rendering. The harness sets `Time.timeScale` to maximum supported (typically 10-20× real time) and suppresses non-essential Unity rendering. A typical 5-minute simulated match runs in ~15-30 wall-clock seconds.

[NORMATIVE] Headless mode is enabled via a flag on `TrainingMatch.Run()`; debug-mode for inspecting individual matches uses headed mode at normal time scale.

### 2.5 Parallelism

[NORMATIVE] Multiple matches MAY run sequentially in one dev session. v0.1.0 does NOT run matches in parallel (Unity is single-process); CMA-ES generations are evaluated in sequence. If wall-clock training time becomes a bottleneck, future versions may use multi-process orchestration; deferred to OPEN.

---

## SECTION 3: SCENARIO GENERATION

### 3.1 Scenario shape

[NORMATIVE] A `Scenario` describes a complete match setup:

```
public sealed class Scenario
{
    public readonly string Id;                       // "procedural-{uuid}" or "library-{name}"
    public readonly TerrainSpec Terrain;
    public readonly IReadOnlyList<SpawnSpec> Spawns;
    public readonly float MatchDurationLimit;
    public readonly Vector2 PlayableAreaSize;
}

public readonly struct SpawnSpec
{
    public readonly TeamId Team;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly TechBlueprint Blueprint;     // block list + control programming
}
```

### 3.2 Procedural generation

[NORMATIVE] Procedural scenarios use the following rules:
1. **Terrain:** sample a height map from a 2D Perlin noise distribution; vary roughness, hill count, elevation range per scenario.
2. **Playable area:** randomized in `[200m, 800m]` square.
3. **Tech compositions:** drawn from a tech-blueprint library (`Smart.Training.Resources.Blueprints.*`); ranges include ~50 hand-built tech designs spanning all vehicle classes.
4. **Per-side composition:** randomly pick 2-5 techs from the library; team size variance creates asymmetric matches.
5. **Spawn positions:** placed in opposite quadrants with random rotation.

[NORMATIVE] Procedural variance is parameterized; the harness can request "easy" (small flat terrain, simple compositions) or "hard" (large hilly terrain, mixed-class compositions). Variance levels drive curriculum learning.

### 3.3 Curated library

[NORMATIVE] Library scenarios live in `Smart.Training.Resources.LibraryScenarios.*` as named text files (JSON or similar; format chosen at implementation). Initial library size: ~15 scenarios.

Initial library categories (provisional):
- `desert_open_duel.json` — flat open terrain, 1v1, similar tech compositions
- `canyon_ambush.json` — narrow canyon, asymmetric composition (defender + attacker)
- `hill_assault.json` — defender on hill, attacker advances uphill
- `naval_engagement.json` — water terrain, water-capable techs only
- `airfield_aerial.json` — flat terrain, airplane-class techs
- `urban_choke.json` — terrain features creating chokepoints
- `mixed_class_fleet.json` — 3v3 with one airplane, one wheel, one hover per team
- `scout_and_screen.json` — uneven LOS coverage requiring scouts
- `heavy_armor_brawl.json` — large-mass techs, slow, high-damage
- `swarm_skirmish.json` — many small fast techs
- `lopsided_advantage.json` — 3v1 with the 1 being heavily armored
- `retreat_and_pursue.json` — one side starts at low HP, must survive a pursuit
- `formation_assault.json` — coordinated 5-tech formation vs. defenders
- `bait_setup.json` — terrain features inviting bait-and-flank
- `time_pressure.json` — 60-second match cap forces aggression

[NORMATIVE] Library scenarios are used during evaluation runs — a regression suite that runs after each generation of evolutionary search. Library win-rates form the evaluation signal.

### 3.4 Mode dispatch

[NORMATIVE] The harness picks scenario mode by counter:

```
if (matchCounter % 20 == 19):   // every 20th match
    use library scenario rotated through the library
else:
    use procedural scenario
```

Provisional cadence; tune.

---

## SECTION 4: OUTCOME SCORING

### 4.1 Multi-objective composite

[NORMATIVE] After a match, compute:

```
score = w_survival   * SurvivalFraction
      + w_damage     * DamageRatio
      + w_time       * TimeEfficiency
      + w_preserve   * FriendlyPreservation
      + w_efficiency * WeaponEfficiency
```

[NORMATIVE] Per-term semantics:

**SurvivalFraction:** `our_alive_count / our_starting_count`. Range [0, 1]. Zero if wiped; 1.0 if no losses.

**DamageRatio:** `damage_dealt / (damage_taken + ε)`. Capped at 5.0 to prevent runaway. Range [0, 5].

**TimeEfficiency:** `1 - (match_duration / match_duration_limit)`. Faster wins score higher. Range [0, 1].

**FriendlyPreservation:** `mean over our techs of (final_HP / starting_HP)`. Range [0, 1]. Captures "we won but every tech is at 5%."

**WeaponEfficiency:** `damage_dealt / shots_fired`. Normalized by max-possible-DPS-per-shot. Range [0, 1].

### 4.2 Weights (provisional)

[NORMATIVE — provisional per FORM-SPEC §1 disclaimer]
- `w_survival = 5.0` (dominant — losing is bad even with great kill ratio)
- `w_damage = 2.0`
- `w_time = 1.0`
- `w_preserve = 2.0`
- `w_efficiency = 0.5`

[NORMATIVE] The weights are themselves hyperparameters subject to CMA-ES (§5). The harness can tune what "winning" means via score weights.

### 4.3 Win/loss derivation

[NORMATIVE] Even with composite scoring, a binary win/loss label is derived for reporting:
- Both sides alive at termination: side with higher score wins.
- One side wiped: the alive side wins.
- True tie (rare): no winner; the match counts for hyperparameter search but not for win-rate reporting.

---

## SECTION 5: EVOLUTIONARY HYPERPARAMETER SEARCH (CMA-ES)

### 5.1 Search space

[NORMATIVE] CMA-ES tunes a vector of Smart's hyperparameters:

```
tunables = [
    // Control
    MPC_sample_count,             // 64-256
    MPC_horizon_seconds,          // 0.5-2.0
    MPC_temperature,              // 0.1-10
    Tactical_learning_rate,       // 0.01-1.0
    Salvo_threshold_damage,       // 20-100
    Sticky_target_ticks,          // 10-90

    // Planning
    PUCT_exploration_constant,    // 0.5-3.0
    MCTS_node_budget,             // 1000-20000
    Rollout_depth_seconds,        // 1.0-5.0

    // Cost-function weights
    w_health_value, w_position_value, w_threat_value,
    w_reach_cost, w_effort_cost, w_threat_cost, w_weapon_cost,
    w_survival_score, w_damage_score, w_time_score,

    // Pathing
    CHOMP_step_count,             // 5-50
    BSpline_control_point_count,  // 4-16
    Threat_field_temperature,     // continuous

    // (other tunables added as they prove load-bearing)
]
```

[NORMATIVE] Total dimension: ~30-50 continuous variables. CMA-ES handles this comfortably.

### 5.2 The CMA-ES loop

[NORMATIVE] Per generation:
1. Sample `population_size` candidate hyperparameter vectors from the current distribution (multivariate Gaussian, mean μ, covariance σ²C).
2. For each candidate: run `eval_matches_per_candidate` matches (provisional: 10).
3. Aggregate scores per candidate (mean composite score).
4. Update μ and C via CMA-ES recombination + adaptation.
5. After every `evaluation_generation` generations (provisional: 5): run all library scenarios with current μ; record absolute library win-rate.
6. Save checkpoint: current μ, C, evaluation results.

Provisional generation parameters: population_size = 16; evals/candidate = 10; library evaluation every 5 generations. Total per generation: 160 matches; per evaluation: 15 library matches. With headless 15-30 sec per match, one generation runs in ~40-80 minutes; library eval adds ~10 minutes.

### 5.3 Reference implementation

[NORMATIVE] CMA-ES has well-known open-source reference implementations. Smart implements it from scratch in pure C# (no external dependencies); reference math is from Hansen's CMA-ES tutorial. The implementation is in `EvolutionarySearch.cs`.

### 5.4 Stopping criteria

[NORMATIVE] Training runs until any of:
- User-specified generation budget reached.
- Library evaluation win-rate plateau (no improvement in last N evaluations; N provisional 10).
- σ shrinks below epsilon (CMA-ES converged on a local optimum).
- User interrupt.

[NORMATIVE] Each session can resume from a saved checkpoint; long training runs span multiple dev sessions.

---

## SECTION 6: PRETRAINING PIPELINE

### 6.1 The output artifact

[NORMATIVE] Training's primary output is `Smart.PretrainedBaseline.bin` — a file in the same format as a per-player profile ([LEARNING-CONTRACT.md §5](LEARNING-CONTRACT.md#section-5-persistent-profile-format)) that ships with the mod as an embedded resource.

### 6.2 Pipeline

[NORMATIVE] After CMA-ES converges (or hits its budget):
1. The best-known hyperparameter vector (current `μ`) is fixed.
2. Run a long batch of training matches (provisional: 1000 matches) with that hyperparameter vector.
3. Throughout the batch, online learning (per [LEARNING-CONTRACT.md §4](LEARNING-CONTRACT.md#section-4-online-training-pipeline)) updates the four models.
4. At the end, the models' weights from both training Smart instances are averaged (per-parameter mean).
5. The averaged weights are saved as the baseline file.
6. Run library evaluation with the baseline to confirm improvement over prior baseline.
7. If improved: replace the embedded resource; commit.

[NORMATIVE] The baseline weights are checked into the repo; the dev build process re-embeds them.

### 6.3 Reproducibility

[NORMATIVE] Each baseline file's metadata includes:
- Generated-at timestamp.
- Source CMA-ES seed and generation count.
- Hyperparameter vector used.
- Training match count.
- Library evaluation win-rate at production time.

[NORMATIVE] This metadata is in the file footer (extension of [LEARNING-CONTRACT.md §5.2](LEARNING-CONTRACT.md#section-5-persistent-profile-format)) so any consumer can audit which Training session produced the baseline.

---

## SECTION 7: `#if SMART_DEV` BUILD MECHANISM

### 7.1 The discipline

[NORMATIVE] **Every source file in `TAC_AI/AI/Forms/Smart/Training/` is wrapped in `#if SMART_DEV` ... `#endif`.**

```
#if SMART_DEV
namespace TAC_AI.AI.Forms.Smart.Training
{
    public sealed class SelfPlayHarness { ... }
}
#endif
```

[NORMATIVE] The `SMART_DEV` symbol is defined in the project's dev build configuration; absent from release. Release builds compile cleanly with no Training types in the output assembly.

### 7.2 Consumers in non-Training subsystems

[NORMATIVE] Other subsystems MUST NOT reference Training types in production code paths. If a consumer needs a Training-specific hook (e.g., Learning needs to know it's currently being trained by self-play vs running in a player mission), the hook is exposed via a `#if SMART_DEV`-gated extension method or a non-Training-prefixed interface that Training implements.

### 7.3 Pretrained baseline path

[NORMATIVE] `Smart.PretrainedBaseline.bin` is NOT gated by `#if SMART_DEV`. It's an embedded resource consumed by Learning at runtime. Training produces it offline; production loads it.

### 7.4 Testing the dev/release boundary

[NORMATIVE] CI (when authored) MUST build both configurations:
- Release: assembly contains no Training types.
- Dev: assembly contains Training types + tests that exercise SelfPlayHarness on a tiny scenario.

A symbol grep in release artifacts (per CNC-style invariant tests) confirms the absence of Training type names.

---

## SECTION 8: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/Training/` contains six files (all wrapped in `#if SMART_DEV`):

| File | Owns |
|---|---|
| `SelfPlayHarness.cs` | Match-type dispatch, two-instance orchestration, match counter. |
| `TrainingMatch.cs` | Single-match runner, termination conditions, headless mode. |
| `ScenarioGenerator.cs` | Procedural scenario rules + library scenario loading + mode dispatch. |
| `OutcomeScorer.cs` | Multi-objective composite + per-term math + win/loss derivation. |
| `EvolutionarySearch.cs` | CMA-ES implementation: sample, evaluate, recombine, adapt; checkpoint save/load. |
| `PretrainingPipeline.cs` | Long-batch training run; model averaging; baseline file emission with metadata. |

Six files within [FORM-SPEC §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives)'s range. Each file is one coherent concern; merging would produce god-files (e.g., merging `SelfPlayHarness` + `TrainingMatch` couples high-level loop with per-match details).

Resources under `TAC_AI/AI/Forms/Smart/Training/Resources/`:
- `Blueprints/*.json` — tech blueprints for procedural composition.
- `LibraryScenarios/*.json` — curated scenarios.
- `Smart.PretrainedBaseline.bin` — the shipped baseline (in the project, but the build only embeds it in releases; dev builds also use it for cold-start).

---

## SECTION 9: DIAGNOSTICS INTEGRATION

[NORMATIVE] Training exposes:

- `MatchCompleted(string scenarioId, MatchMode mode, MatchOutcome outcome, TimeSpan wallClockDuration)` — per match.
- `GenerationCompleted(int generation, float meanScore, float bestScore, double sigma)` — per CMA-ES generation.
- `LibraryEvaluationCompleted(int generation, float libraryWinRate, IReadOnlyDictionary<string, bool> perScenarioResults)` — per evaluation.
- `CheckpointSaved(string checkpointPath, int generation)` — per save.
- `BaselineEmitted(string baselinePath, int sourceMatchCount, float evaluationWinRate)` — per pretraining-pipeline completion.

[NORMATIVE] These flow into Diagnostics when authored.

---

## SECTION 10: OPEN ITEMS

[OPEN] **Population size for CMA-ES.** 16 provisional; depends on dimension and compute budget.
[OPEN] **Evals per candidate.** 10 provisional; balances noise reduction vs. throughput.
[OPEN] **Scripted baseline behavior.** Reactive logic placeholder at v0.1.0; refine to ensure it's a useful reference (not trivial to beat).
[OPEN] **Tech blueprint library size and contents.** ~50 hand-built blueprints provisional; expand based on training observations.
[OPEN] **Library scenario count.** ~15 provisional.
[OPEN] **Outcome scoring weights.** Provisional; CMA-ES tunes them.
[OPEN] **Time scale for headless mode.** 10-20× provisional; depends on physics determinism at high scales.
[OPEN] **Multi-process parallelism.** Not implemented at v0.1.0; revisit if wall-clock training time becomes limiting.
[OPEN] **Procedural variance curriculum.** Hand-tuned at v0.1.0 (easy/medium/hard); consider learning-augmented curriculum post-v1.
[OPEN] **Headed vs headless mismatch.** Some Unity physics behave subtly differently with time-scale > 1 (e.g., joint solver iteration count). Verify training-emergence matches play-time behavior.

---

## SECTION 11: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not the released AI. Training runs in development; the released AI consumes Training's output (the baseline) and learns online from real player behavior.

This contract is not a generic ML framework. Training is purpose-built for Smart. CMA-ES, the harness, and the scoring are tuned for Smart's situation, not for general use.

This contract is not the release decision. When the baseline is "good enough" to ship is a human judgment call based on evaluation win-rate, library scenario regressions, and qualitative behavior observation.

This contract does not own player-facing observability. If a player wants to know "is the AI training me?" the answer is "no, training happened offline" — that messaging is FORM-SPECIFICATION's concern.

---

END OF TRAINING-CONTRACT.md v0.1.0
