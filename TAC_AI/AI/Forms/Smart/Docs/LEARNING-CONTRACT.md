# LEARNING-CONTRACT.md

**Subsystem:** Learning/
**Form:** Smart
**Version:** 0.1.0
**Status:** AUTHORITATIVE — Defines Smart's four learned models (opponent intent classifier, action-value estimator, trajectory residual, threat assessment), the online training pipeline, persistent per-player profile format with snapshot-before-write recoverability, schema versioning with forward-only migrations, and the shared-baseline-plus-per-player-refinement policy.

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The four learned models: their inputs, outputs, architectures, parameter counts, evaluation costs, and training signals.
- The online training pipeline: event ingestion, minibatch construction, gradient update cadence, training-worker integration.
- The persistent profile file format: binary layout, schema version field, per-model sections.
- The recoverability mechanism: snapshot-before-write + corruption-preserves-old-as-`.corrupt`.
- The schema migration policy: forward-only with numbered migration files (per DOCTRINE.md §2.8).
- The save/load orchestration: `ManSafeSaves` hook + per-player join/leave Harmony patches.
- The shared baseline + per-player refinement policy: shipped pretrained baseline + per-player profile that initializes from baseline weights.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals, particularly §1.6 (online learning + persistent memory) and §3.1.6 (profile recoverability requirement).
- [ARCHITECTURE.md](ARCHITECTURE.md) for threading model, MP host/client gating, error categories (E2 covers profile load failure).
- [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md) for `ManSafeSaves` API (OQ-7 resolution) and player join/leave Harmony patches (OQ-9 resolution).
- [THREADING-CONTRACT.md](THREADING-CONTRACT.md) for worker pool, double-buffer, cancellable tasks.
- [WORLD-CONTRACT.md](WORLD-CONTRACT.md) for the event bus (training data source) and `BeliefState` (model input).
- [COORDINATION-CONTRACT.md](COORDINATION-CONTRACT.md) for the comms bus (additional training data) and `CoordinationState` (model input).
- [DOCTRINE.md §2.4](../../../../../Doctrine%20Documentation/DOCTRINE.md) (recoverability), [§2.8](../../../../../Doctrine%20Documentation/DOCTRINE.md) (schema migrations), and [DOCTRINE-PATTERNS.md](../../../../../Doctrine%20Documentation/DOCTRINE-PATTERNS.md) (schema-versioning, forward-only migrations, pre-write snapshots, validated/designed annotations).

**This document GOVERNS:**
- The model inference interfaces other subsystems call: `IIntentClassifier`, `IActionValueModel`, `ITrajectoryResidual`, `IThreatAssessment`.
- The save/load lifecycle for per-player profiles.

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What Learning owns

Learning owns Smart's four learned models, their training, and their persistent state. It does NOT own:
- The intent category set — that's [WORLD-CONTRACT.md §2.2](WORLD-CONTRACT.md#section-2-belief-state).
- The strategic state representation — that's [PLANNING-CONTRACT.md §3](PLANNING-CONTRACT.md#section-3-strategic-state-representation).
- The cost function structures — those are owned by their respective consumer contracts (Control, Planning, Pathing).
- The actual gradient updates during inference — only Learning's offline / online training runs gradient updates; inference is forward-pass only.

### 1.2 Decisions resolved at v0.1.0

[NORMATIVE]
- **Model architectures: hybrid (Step 1.10 Q&A round).** Opponent intent classifier is a small GRU (sequence model); action-value, trajectory residual, threat assessment are shallow MLPs.
- **Cross-player policy: shared baseline + per-player refinement.** Smart ships pretrained baseline weights; each player profile initializes from the baseline and refines online.
- **Profile format: binary with schema_version field.** Custom compact layout; no external dependencies. Schema versioning mandatory per DOCTRINE-PATTERNS.
- **Recoverability: snapshot-before-write.** Each save copies current profile to `.previous` before writing; corruption-on-load preserves the bad file as `.corrupt-<timestamp>` and falls back to `.previous`, then to baseline.
- **Migration policy: forward-only.** When schema_version changes, named numbered migration functions transform old → new. No `down()` migrations. Per DOCTRINE-PATTERNS forward-only-with-throwing-down.

---

## SECTION 2: THE FOUR MODELS — OVERVIEW

[NORMATIVE] Per FORM-SPEC §1.6, Smart maintains four learned models. Each has a fixed input shape, output shape, and architecture; each can be loaded, inferred, and trained independently.

| Model | Type | Architecture | Params (provisional) | Inference cost |
|---|---|---|---|---|
| `OpponentIntentClassifier` | Sequence → distribution | GRU 64 hidden, 1 layer | ~15 KB | ~50 µs |
| `ActionValueEstimator` | State → scalar | MLP 2×64 | ~10 KB | ~5 µs |
| `TrajectoryResidualModel` | State + dt → 3D vector | MLP 2×32 | ~3 KB | ~2 µs |
| `ThreatAssessmentModel` | Composition → scalar | MLP 2×32 | ~3 KB | ~2 µs |

Combined per-player: ~30 KB. Inference budget per Operations tick (across all four for one tech): under 100 µs.

[NORMATIVE] Sizes are provisional per [FORM-SPECIFICATION.md §1 disclaimer](FORM-SPECIFICATION.md). FORM-SPEC §1.6 constraint: "Models MUST be small (kilobytes of parameters, microseconds to evaluate)." These designs satisfy.

---

## SECTION 3: PER-MODEL SPECIFICATIONS

### 3.1 OpponentIntentClassifier

[NORMATIVE] **Purpose:** Classify a hostile tech's current high-level intent from its recent observed behavior.

[NORMATIVE] **Input:** A sequence of ~30 ticks (1 second at 30 Hz perception) of per-tick feature vectors for the target tech:

```
per-tick feature vector (size 12):
- relative position (3D) from observer
- relative velocity (3D)
- relative heading vs observer
- recent damage taken (scalar)
- recent damage dealt (scalar)
- distance to nearest friendly of target's team (scalar)
- distance to nearest hostile of target's team (scalar)
- current weapon-fire-rate aggregate (scalar)
```

Total input: 30 × 12 = 360 scalars per inference.

[NORMATIVE] **Architecture:** GRU with 64 hidden units, single layer. Followed by a dense softmax layer producing a distribution over [WORLD-CONTRACT.md §2.2](WORLD-CONTRACT.md#section-2-belief-state)'s 6 intent categories (Aggressing / Retreating / Flanking / Repositioning / Holding / Idle).

```
input (30 × 12) → GRU(64) → Dense(6) → Softmax → output distribution
```

[NORMATIVE] **Parameter count:** ~15 KB. GRU dominates: 3 × (12 × 64 + 64 × 64 + 64) ≈ 14.9 KB.

[NORMATIVE] **Output:** IReadOnlyList<float> of length 6, summing to 1.0. This replaces the uniform prior World assigns at v0.1.0.

[NORMATIVE] **Training signal:** From observed-future-trajectory. The classifier's prediction at time `t` is checked against the intent revealed at `t + horizon` (where horizon ~3 seconds — enough to disambiguate aggressing from holding). Training labels are derived by a labeling rule: if the target closed by > 30% of distance in the horizon, intent was "aggressing"; if it retreated > 30%, "retreating"; etc. Labels are computed retrospectively when enough observations have accumulated.

[NORMATIVE] **Loss:** Categorical cross-entropy.

[NORMATIVE] **Update cadence:** Minibatch of 32 labeled sequences; gradient step when buffer fills.

### 3.2 ActionValueEstimator

[NORMATIVE] **Purpose:** Estimate the expected match value (in terms of [PLANNING-CONTRACT.md §5](PLANNING-CONTRACT.md#section-5-heuristic-value-function)'s value units) given a current strategic state and a candidate strategic action.

[NORMATIVE] **Input:** Flattened strategic state features:

```
state features (size ~50):
- team health aggregates (friendly mean, hostile mean, friendly min, hostile min) — 4
- team position aggregates (centroid, spread, mean distance to enemy centroid) — 6
- team threat aggregates (mean threat, max threat) — 4
- current plan (one-hot over 10 plans) — 10
- tick count since plan started (scalar, normalized) — 1
- team velocity aggregate (3D) — 3
- relative compositions (friendly mobility mean, hostile mobility mean) — 2
- belief uncertainty aggregate (mean position variance over hostiles) — 1
- candidate action (one-hot over 10 plans) — 10
- candidate action parameters (encoded; 9 max) — 9
```

[NORMATIVE] **Architecture:** MLP with two hidden layers of 64 units each, ReLU activations, scalar output.

```
input (~50) → Dense(64) → ReLU → Dense(64) → ReLU → Dense(1) → output Q-value
```

[NORMATIVE] **Parameter count:** ~10 KB.

[NORMATIVE] **Output:** Scalar in roughly [-6, +6] range (matching PLANNING-CONTRACT §5 value scale).

[NORMATIVE] **Training signal:** TD-error from observed outcomes. After taking action `a` from state `s`, observe the new state `s'` after one strategic tick interval. Compute target Q-value as `reward(s, s') + γ * max_a' Q(s', a')`. Loss is `(Q(s, a) - target)²`. Standard Q-learning.

[NORMATIVE] **Use:** Provides the prior `P(s, a)` term in [PLANNING-CONTRACT.md §2.1](PLANNING-CONTRACT.md#section-2-puct-search)'s PUCT selection (replacing the uniform prior). Also contributes to [PLANNING-CONTRACT.md §5](PLANNING-CONTRACT.md#section-5-heuristic-value-function)'s `LearnedValue` term (zero by default; activates when this model is trained).

### 3.3 TrajectoryResidualModel

[NORMATIVE] **Purpose:** Predict the position correction needed beyond linear extrapolation, for use in weapon lead computation.

[NORMATIVE] **Input:** Features describing a target tech's recent motion:

```
features (size ~15):
- target velocity (3D) — 3
- target acceleration estimate (3D) — 3
- target jerk estimate (3D) — 3
- recent damage taken (scalar) — 1
- distance to nearest threat (scalar) — 1
- terrain slope at target position (scalar) — 1
- target heading rate of change (scalar) — 1
- dt to predict ahead (scalar; the weapon's projectile flight time) — 1
- target VehicleClass one-hot (5) — 5
```

[NORMATIVE] **Architecture:** MLP with two hidden layers of 32 units each, ReLU activations, 3D output.

```
input (~15) → Dense(32) → ReLU → Dense(32) → ReLU → Dense(3) → output residual (3D)
```

[NORMATIVE] **Parameter count:** ~3 KB.

[NORMATIVE] **Output:** Vector3 — the residual that, when added to linear extrapolation, gives a better predicted position. Used by [CONTROL-CONTRACT.md §10.3](CONTROL-CONTRACT.md#section-10-weapon-fire-controller)'s `LearnedResidual(targetId, dt)` slot.

[NORMATIVE] **Training signal:** When a weapon fires with predicted lead at time `t` for projectile arrival at `t + dt`, record the predicted position. At `t + dt`, observe the actual target position. Loss = `||actual - linear_extrapolation - residual||²`. The model learns to predict the deviation from linear.

### 3.4 ThreatAssessmentModel

[NORMATIVE] **Purpose:** Estimate a tech's real damage output and survivability from its observable composition. Replaces the heuristic `threat_rating` in [VEHICLE-CONTRACT.md §2](VEHICLE-CONTRACT.md#section-2-the-vehiclemodel-snapshot) when this model is trained.

[NORMATIVE] **Input:** Block-composition aggregates:

```
features (size ~25):
- total mass (normalized) — 1
- top speed (normalized) — 1
- mobility class one-hot (5) — 5
- weapon count per type (~10 weapon-type buckets) — 10
- armor density mean (4 face directions) — 4
- block count (normalized) — 1
- propulsion-type one-hot (3) — 3
```

[NORMATIVE] **Architecture:** MLP with two hidden layers of 32 units each, ReLU, scalar output.

```
input (~25) → Dense(32) → ReLU → Dense(32) → ReLU → Dense(1) → output threat rating
```

[NORMATIVE] **Parameter count:** ~3 KB.

[NORMATIVE] **Output:** Scalar threat rating in roughly [0, 1] range (normalized).

[NORMATIVE] **Training signal:** From observed engagements. When tech `A` engaged tech `B` for duration `T` and dealt total damage `D`, the threat rating of `A` against `B`-type compositions should be approximately `D / T / max_dps_observed`. Loss = MSE against this empirical threat measurement.

[NORMATIVE] **Use:** Feeds the threat-rating slot in `VehicleModel` (when this model is trained). Until then, heuristic threat is used.

---

## SECTION 4: ONLINE TRAINING PIPELINE

### 4.1 Event ingestion

[NORMATIVE] The online trainer subscribes (on main thread) to events from World's `WorldEventBus` and Coordination's `CoordinationCommsBus`:

| Event | Contributes training data to |
|---|---|
| `DamageObserved` | Intent classifier (target's recent behavior); ThreatAssessment (engagement outcome) |
| `ProjectileFired` | Intent (action evidence); TrajectoryResidual (lead prediction outcome at projectile arrival time) |
| `BlockDestroyed` | ThreatAssessment (engagement damage tally) |
| `BeliefUpdated` | Intent (per-tick state sequence); TrajectoryResidual (actual-vs-predicted position) |
| `TechSeen` / `TechLost` | Intent (observation sequence boundaries) |
| `TargetMapUpdated` | ActionValue (state-action-reward sequences from Coordination's plan transitions) |
| `PlanDecomposed` | ActionValue (strategic state when plan changed; reward signal at next state) |

[NORMATIVE] On main thread, each event triggers a value-typed payload capture into a per-model `BoundedQueue<TrainingEvent>` (per [THREADING-CONTRACT.md §5](THREADING-CONTRACT.md#section-5-bounded-queue-drop-oldest)) sized at 1024 events per model.

### 4.2 Training-worker loop

[NORMATIVE] One training worker per model runs in the Threading pool. Each worker:

1. Wait for its event queue to reach minibatch size (32 events).
2. Drain 32 events; assemble into a minibatch.
3. Compute gradient via forward+backward pass.
4. Apply Adam optimizer update to model parameters (in-place).
5. Publish updated parameters via `DoubleBuffer<ModelParameters>`.
6. Inference subscribers automatically see the new parameters on next read.

[NORMATIVE] Workers respect cancellation (per [THREADING-CONTRACT.md §3](THREADING-CONTRACT.md#section-3-cancellation-model)). A mid-minibatch cancel publishes the pre-update parameters; partial gradients are discarded.

### 4.3 Training cadence

[NORMATIVE] Minibatch size: 32 (provisional). Update frequency: whenever the buffer fills, which depends on event rate. In active combat, intent classifier may update every 1-2 seconds; in idle, it may update every minute. The drop-oldest policy ensures fresh events always win.

[NORMATIVE] Total training work per update: ~few hundred µs per model. Sub-millisecond. Negligible against the compute budget.

### 4.4 Inference path

[NORMATIVE] Inference does NOT go through workers — it runs synchronously on whichever thread calls the model. Inference uses the latest published parameters (from the double-buffer); the parameters are read once per inference call.

[NORMATIVE] Inference is allocation-free on the hot path. Pre-allocated forward-pass buffers per model instance.

---

## SECTION 5: PERSISTENT PROFILE FORMAT

### 5.1 File location

[NORMATIVE] Profile files live at `<mod-dir>/SmartAI/Profiles/<player_id>.bin`. The directory is created on first save.

`<player_id>` derivation: TerraTech's player identifier (Steam ID, GOG ID, or local equivalent), sanitized to a filename-safe form (alphanumerics + dashes only). The exact derivation lives in `ProfilePersistence.cs`.

### 5.2 Binary layout

[NORMATIVE] The profile file is a custom compact binary with this structure:

```
Header (16 bytes):
  - magic [4 bytes]: "SMRT"
  - schema_version [4 bytes]: uint32, currently 1
  - saved_at_unix_ms [8 bytes]: int64

Per-model sections (4 sections, in fixed order):
  Each section:
    - model_id [1 byte]: 0=Intent, 1=ActionValue, 2=TrajectoryResidual, 3=ThreatAssessment
    - architecture_version [1 byte]: per-model version; revising a model's architecture bumps this
    - param_count [4 bytes]: uint32
    - param_bytes [4 bytes]: uint32, length of weights blob
    - weights blob [param_bytes]: float32 array, little-endian

Footer:
  - checksum [4 bytes]: CRC-32 over everything before the footer
```

[NORMATIVE] Total typical file size: ~30-40 KB.

[NORMATIVE] The `schema_version` field is mandatory per DOCTRINE-PATTERNS' schema-versioning pattern. Per-model `architecture_version` allows revising one model independently of the others.

### 5.3 Why binary not JSON

[RATIONALE] JSON would be human-readable but ~10× larger and slower to load. At 30+ KB compressed, with thousands of float values per profile, JSON has no real benefit — debugging is rarely done by reading raw weights. Binary with a CRC-32 footer is faster and verifies integrity.

[INFORMATIVE] A debug dump utility (`ProfilePersistence.DumpToJson(profile, path)`) is provided for development use. It's not on the load/save hot path.

### 5.4 Compression

[NORMATIVE] v0.1.0 does NOT compress. Weights are mostly small-magnitude floats; gzip would save ~30% but add a dependency. Revisit when profile sizes exceed ~100 KB.

---

## SECTION 6: RECOVERABILITY (DOCTRINE §2.4)

### 6.1 Snapshot-before-write protocol

[NORMATIVE] Save sequence:

```
1. If <player_id>.bin exists, copy it to <player_id>.previous.bin (overwriting any prior .previous).
2. Compute the new profile bytes (header + sections + footer).
3. Write to <player_id>.tmp.bin.
4. fsync the temp file.
5. Atomically rename <player_id>.tmp.bin → <player_id>.bin.
6. (On Unix, fsync the directory.)
```

[NORMATIVE] On interruption between steps 5 and the next save, the .previous file is the snapshot from before the (interrupted) save — recoverable.

### 6.2 Load with corruption fallback

[NORMATIVE] Load sequence:

```
1. Open <player_id>.bin. If absent, fall through to baseline (§7).
2. Parse header. Validate magic == "SMRT".
3. Parse all model sections. Validate weight blob sizes.
4. Parse footer. Validate CRC-32.
5. If any validation fails:
   a. Rename <player_id>.bin to <player_id>.corrupt-<unix_ms>.bin (preserve for the player to investigate).
   b. Log a WARNING via DebugTAC_AI.LogWarning naming the corruption category.
   c. Try loading <player_id>.previous.bin (steps 2-4 against it). If valid, copy it to <player_id>.bin and proceed.
   d. If .previous is also invalid (or absent), fall through to baseline.
6. Return the loaded profile.
```

[NORMATIVE] The corrupt-file preservation means the player can investigate (or extract bits) if they care. Smart never silently discards profile data.

### 6.3 Where this maps in doctrine

This is the "snapshot-before-write" pattern adapted for the Smart context. Doctrine §2.4 requires recoverability; doctrine §6 (Error categories E2 in ARCHITECTURE.md) prescribes "log + preserve corrupt as `.corrupt-<timestamp>` + fall back to baseline." Both are honored here.

---

## SECTION 7: SHARED BASELINE + PER-PLAYER REFINEMENT

### 7.1 The pretrained baseline

[NORMATIVE] A pretrained baseline ships as an embedded resource in the mod assembly: `Smart.PretrainedBaseline.bin`. The format is the same as a per-player profile (§5.2); load logic is shared.

[NORMATIVE] The baseline is generated by the self-play harness (workflow step 1.11) and committed to the repo before release. Subsequent self-play sessions during development can update the baseline; the dev-only build process re-embeds it.

[NORMATIVE] At v0.1.0 (before self-play has run), the baseline is the "zero-initialized" profile — all weights initialized by Glorot/Xavier from the model architecture metadata. The shipped binary is small (~30 KB of small floats); compresses well.

### 7.2 Load order

[NORMATIVE] On player join (per [SHELL-API-GUIDE.md §7.2 OQ-9 resolution](SHELL-API-GUIDE.md#section-7-game-events)):

```
1. Compute <player_id> from the joining player.
2. Try loading <mod-dir>/SmartAI/Profiles/<player_id>.bin.
3. If absent or invalid (per §6.2): load the embedded baseline.
4. Register the loaded profile in Learning's per-player cache.
5. Publish initial model parameters to inference subscribers via the DoubleBuffers.
```

[NORMATIVE] On player leave (or world unload, or form deactivate):

```
1. If the per-player profile has been modified since load (any training has run), trigger a save (§6.1).
2. Remove from the per-player cache.
3. The save runs synchronously inside the player-leave handler to satisfy ARCHITECTURE §5 I4 (profiles save before player-leave returns).
```

### 7.3 Multi-player handling

[NORMATIVE] Multiple players concurrently in the same mission each have independent profiles. Learning's inference subscribers query by player_id to read the right profile's models.

[NORMATIVE] For models that span all players (action-value estimator works at team-not-player level), Learning maintains a "current host player" reference — typically the host. Other players' profiles are loaded but only the host's drives strategic-level inference until host changes.

---

## SECTION 8: SCHEMA MIGRATIONS

### 8.1 When migration runs

[NORMATIVE] Per DOCTRINE.md §2.8 and DOCTRINE-PATTERNS' forward-only-migrations pattern, schema changes are recorded as numbered migration files:

```
Migrations/
  0001_initial_schema.cs
  0002_add_threat_model_features.cs
  0003_resize_intent_gru_to_64.cs
  ...
```

Each migration file has:

```
public sealed class MigrationNNNN : IMigration
{
    public uint FromVersion => N - 1;
    public uint ToVersion   => N;
    public void Up(ProfileV{FromVersion} from, ProfileV{ToVersion} to);
    public void Down(ProfileV{ToVersion} from, ProfileV{FromVersion} to) =>
        throw new InvalidOperationException("Smart profile migrations are forward-only");
}
```

[NORMATIVE] At load time, if `header.schema_version < current_schema_version`, the migration runner is invoked: run migrations in sequence `header.schema_version → header.schema_version + 1 → ... → current_schema_version`. The Up function for each migration transforms the in-memory representation.

[NORMATIVE] After migration, the profile is saved at the new schema_version (the next save_now triggers this). Until then, the in-memory version is the new one; old file remains on disk.

### 8.2 Pre-migration backup

[NORMATIVE] Per DOCTRINE-PATTERNS' pre-migration-backup pattern: before running migrations on `<player_id>.bin`, copy it to `<player_id>.pre-migration-v<N>.bin`. The migration runner refuses to start if it cannot create this backup (disk full, permissions).

### 8.3 Down migrations forbidden

[NORMATIVE] Per the forward-only policy: the `Down` method on every migration throws. Mistakes are fixed by writing a new migration that corrects the previous. The schema history is unambiguous about what happened.

---

## SECTION 9: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/Learning/` contains seven files:

| File | Owns |
|---|---|
| `LearningService.cs` | Orchestration, event subscriptions, per-player cache, save/load lifecycle. |
| `OpponentIntentClassifier.cs` | GRU model: architecture, forward pass, label derivation, training. |
| `ActionValueEstimator.cs` | MLP model: architecture, forward pass, TD-error training. |
| `TrajectoryResidualModel.cs` | MLP model: architecture, forward pass, lead-prediction outcome training. |
| `ThreatAssessmentModel.cs` | MLP model: architecture, forward pass, engagement-outcome training. |
| `OnlineTrainer.cs` | Minibatch construction, training-worker loop, Adam optimizer (BCL-internal), per-model coordination. |
| `ProfilePersistence.cs` | Binary serialization, recoverability protocol, baseline loading, migration runner, schema-version handling. |

Seven files within [FORM-SPEC §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives)'s range. Justification: each model gets its own file (mapped 1:1 to FORM-SPEC §1.6's four models); the trainer and persistence layers each have substantial responsibility worth isolating. The migration runner is part of ProfilePersistence — they're tightly coupled to file I/O.

Migrations live in `TAC_AI/AI/Forms/Smart/Learning/Migrations/` as separate numbered files (§8.1); the file count there grows over time but each migration is small.

---

## SECTION 10: DIAGNOSTICS INTEGRATION

[NORMATIVE] Learning exposes:

- `ProfileLoaded(string playerId, uint schemaVersion, bool fromBaseline)` — after a successful load.
- `ProfileLoadFailed(string playerId, string failureCategory)` — corruption / missing.
- `ProfileSaved(string playerId, int bytesWritten, TimeSpan duration)` — after a successful save.
- `MigrationRan(string playerId, uint fromVersion, uint toVersion, TimeSpan duration)` — per migration.
- `MinibatchTrained(ModelId model, int batchSize, float lossBefore, float lossAfter, TimeSpan duration)` — per training update.
- `ModelInferenceLatency(ModelId model, TimeSpan duration)` — sampled inference cost.
- `EventQueueDropped(ModelId model, int droppedCount)` — when training-event queue drops oldest.

[NORMATIVE] These flow into Diagnostics when authored.

---

## SECTION 11: OPEN ITEMS

[OPEN] **Model sizes (parameter counts).** Provisional per §2. Tune during self-play.
[OPEN] **Minibatch size.** 32 provisional.
[OPEN] **Adam learning rate per model.** Provisional defaults (lr ≈ 0.001 for MLP; lr ≈ 0.0003 for GRU). Tune.
[OPEN] **Intent labeling horizon.** ~3 seconds provisional.
[OPEN] **Compression.** Off at v0.1.0; revisit if files grow.
[OPEN] **Cross-machine model fusion.** Excluded per Step 1.10 Q&A round (federated averaging rejected as too complex for mod context). Revisit post-v1 if user wants it.
[OPEN] **Profile encryption / signing.** Profiles are local-only at v0.1.0; if they ever leave the player's machine (mod cloud sync, etc.), revisit privacy.
[OPEN] **Multi-player coordination of profile loading.** The host's player_id derivation may need to handle non-host players differently if non-host Smart-driven techs exist.
[OPEN] **Initial baseline weights.** v0.1.0 ships with Glorot-initialized baseline; revisit when self-play harness produces a real baseline at step 1.11.
[OPEN] **Architecture revision protocol.** When changing a model's architecture (e.g., GRU 64 → 128), bump that model's `architecture_version` AND the overall `schema_version`; old models must be migrated or discarded. Concrete migration logic deferred to when revisions happen.

---

## SECTION 12: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not the self-play harness. The harness is what *trains* the baseline (workflow step 1.11; owned by [TRAINING-CONTRACT.md](TRAINING-CONTRACT.md) when authored). Learning consumes the harness's output (the trained baseline weights) but doesn't run the harness itself.

This contract is not strategic decision-making. Models provide signals; Planning and Coordination and Control consume them. The decision is theirs.

This contract is not the model registry. Each model has a fixed identity (one of four); adding a fifth is a contract revision, not a registry entry.

This contract is not the player identity system. Smart consumes whatever `<player_id>` TerraTech exposes; it does not assign or validate identities.

This contract does not own backwards-compatibility for old model architectures. Once a model's `architecture_version` is bumped, the old architecture is gone — migrations transform old data forward, but Smart doesn't run two architecture variants concurrently.

---

END OF LEARNING-CONTRACT.md v0.1.0
