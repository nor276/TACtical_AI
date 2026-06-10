# AUDIT-BUG-LIST

> Status: round-4 polish pass (post round-3 consolidation; 10 + 10 + 10 sweep agents
> across rounds 1-3, polish pass dedupes + cite-verifies + recategorizes).
> Sources: focus areas {todos-fixmes, stubs, smart-ml-pipeline, smart-threading,
> modified-form, pathing, lifecycle, multiplayer, save-load, tunables-config}.

## §1. Summary

Severity counts below are ACTIVE only (OPEN + NEEDS-VERIFY); REFUTED entries do not contribute. Total active = 74.

| Severity  | Count |
|-----------|-------|
| BLOCKER   | 4     |
| MAJOR     | 42    |
| MINOR     | 28    |
| **Total** | **74** |

| Status        | Count |
|---------------|-------|
| OPEN          | 73    |
| NEEDS-VERIFY  | 1     |
| REFUTED       | 7     |
| **Total**     | **81** |

> Note: Active findings (OPEN + NEEDS-VERIFY) = 74; status-table total = 81 because
> 7 REFUTED entries are retained in §5 as historical stubs (their original severity
> rows remain so the audit trail stays diffable). REFUTED set = BUG-027, BUG-044,
> BUG-045 from round 2 (file-deleted / design-intent / lazy-alloc-verified); BUG-061,
> BUG-070 merged as dupes in round 4 polish; BUG-031, BUG-068 closed as DESIGN-INTENT
> in round 4 polish after extended NEEDS-VERIFY — see §5.
>
> Active severity split (74): 4 BLOCKER OPEN + 42 MAJOR OPEN + 27 MINOR OPEN + 1 MINOR NEEDS-VERIFY (BUG-069).
>
> Round-4 polish delta: 2 dedupes (BUG-061 → BUG-006; BUG-070 → BUG-021); 2
> NEEDS-VERIFY entries closed as DESIGN-INTENT (BUG-031, BUG-068); no severity
> reclassifications; all surviving cites spot-checked (BUG-003 sender-deref at
> TankAIHelper.cs:1018, BUG-004 daemon-enqueue at PathingService.cs:142-143, BUG-013
> reassign at PathingService.cs:176 — all verified verbatim).
>
> Round-3 delta: +10 new findings (BUG-072..BUG-081 — 7 MAJOR, 3 MINOR); 0 refuted;
> 0 promoted; 0 severity reclassified. One new BLOCKER-class csproj omission
> (`Templates/EBasePurpose.cs`) folded into BUG-001 footprint (BUG-001 now covers 6
> files, not 5) — no new BLOCKER-numbered bug. All 4 existing BLOCKERs survived
> adversarial review by 10/10 round-3 agents; no BLOCKER refutable. BUG-009 narrative
> sharpened: post-feature-expansion build is specifically a regression for the
> Residual model (architecturally larger model trained against zero inputs learns
> strictly less than v0.1 6-slot would have); ActionValue producer IS wired correctly.
> New Save-Load cluster (BUG-074..077) captures backup-ring fragility under
> writer-fault and hardcoded section-count drift. BUG-042 cluster reduced (`EBasePurpose.cs`
> removed — that file is heavily used, not dead).
>
> Round-2 delta (carried forward): +26 new findings (BUG-046..BUG-071); 2 round-1
> findings promoted NEEDS-VERIFY→OPEN (BUG-003, BUG-038); 3 refuted (BUG-027, BUG-044,
> BUG-045); 1 severity reclassified (BUG-013 MAJOR→MINOR with narrative correction).
> Two BLOCKER-class csproj omissions (AltUI.cs / BlockIndexer.cs) folded into BUG-001.

## §2. BLOCKERs

### BUG-001 — csproj missing 6 referenced files: clean build will fail
- **Severity**: BLOCKER
- **Category**: Config
- **Location**: `TAC_AI/TAC_AI.csproj` (full file) — missing `<Compile Include>` for `Templates/FactionLevel.cs`, `World/TechLoaderExt.cs`, `FactionTypesExt.cs`, `AltUI.cs`, `BlockIndexer.cs`, `Templates/EBasePurpose.cs`
- **Description**: Six .cs files defining types referenced by compiled code are absent from any `<Compile Include>` group. Current `bin/Steam/TAC_AI.dll` was built from a mirror clone at `C:\Users\rbigg\TACtical_AI-master\…` (confirmed via `obj/Steam/TAC_AI.csproj.FileListAbsolute.txt`); a clean build from this working tree fails. Round 3 (Sweep-10) found one more omission beyond round 2's five (Sweep-10 round 2 found two beyond round 1's three).
- **Evidence**:
  - `Templates/FactionLevel.cs:12` declares `public enum FactionLevel`, referenced from `RawTechLoader.cs:1439-1452`, `RLoadedBases.cs:1207-1434`, `ManEnemyWorld.cs:692`, `UnloadedBases.cs:356-512`, etc.
  - `World/TechLoaderExt.cs:18` declares `public class TechLoaderExt` with `Open(...)` at line 204, referenced from `PlayerRTSUI.cs:119`.
  - `FactionTypesExt.cs:9` declares `public enum FactionTypesExt`, referenced from `RawTechLoader.cs`, `DebugRawTechSpawner.cs`, `TempStorage.cs`, `CommunityStorage.cs`.
  - `AltUI.cs:10` declares `public static class AltUI`, referenced 14× from `AIGlobals.cs` and `AIWiki.cs` (`AltUI.ColorDefaultPlayer`, `AltUI.CreateCustomPopupInfo`, `AltUI.Sprite`, `AltUI.TextfieldBorderedBlue`, `AltUI.ButtonBlueLarge`, etc.). [round 2]
  - `BlockIndexer.cs:17` declares `public class BlockIndexer : MonoBehaviour`, referenced 20+× from `KickStart.cs:585/1015`, `TankAIManager.cs:437`, `AIERepair.cs:266/335/402/1966`, `AIGlobals.cs:71`, `TankAIHelper.WeaponFire.cs:22`, `ManEnemyWorld.cs:2311/2480`, `ModTechsDatabase.cs:107/603`, `RawTechExporter.cs:194/216/256/262`, `RawTechLoader.cs:2048/2150/2357`. [round 2]
  - `Templates/EBasePurpose.cs:12,20,30` declares enums `BaseTypeLevel`, `BaseTerrain`, `BasePurpose`, referenced 15+× from `AI/Forms/Smart/Identity/SmartIdentityClassifier.cs:62-153` (the entire Smart-Identity classifier is unbuildable without it), `AI/TankAIHelper.cs`, `AI/AIECore.cs`, `Enemy/RCore.cs`, `Enemy/RLoadedBases.cs`, `Templates/RawTechLoader.cs`, `Templates/SpecialAISpawner.cs`, `Templates/DebugRawTechSpawner.cs`, `Templates/TempStorage.cs`, `World/UnloadedBases.cs`, `CustomAttract.cs`. [round 3] **Round-3 correction**: this file was misclassified in BUG-042's dead-code cluster — it is heavily used, not dead.
- **Impact**: Clean build of Modified working tree errors out (missing type / unresolved symbol). First contributor that attempts a fresh build from this tree hits compile errors. Memory note `modularization-refactor-progress` records the csproj-must-register-each-file pattern.
- **Sources**: Sweep-10 (round 1 + round 2 + round 3)
- **Status**: OPEN

### BUG-002 — `FactionLevel.SJ` referenced but enum has no SJ member
- **Severity**: BLOCKER
- **Category**: Broken
- **Location**: `Templates/RawTechLoader.cs:1450` — `lvl = FactionLevel.SJ;`
- **Description**: `Templates/FactionLevel.cs:12-23` declares enum values NULL, GSO, GC, VEN, HE, BF, EXP, ALL, MOD — no SJ. RawTechLoader.cs is the only writer.
- **Evidence**: Grep on `\bFactionLevel\.SJ\b` returns exactly one hit at `RawTechLoader.cs:1450`.
- **Impact**: Reinforces BUG-001 — even if FactionLevel.cs is added to csproj, build still fails on this symbol.
- **Sources**: Sweep-10
- **Status**: OPEN

### BUG-003 — TrySetAITypeRemote NRE on null sender (host-changed-AI path)
- **Severity**: BLOCKER
- **Category**: Broken
- **Location**: `TAC_AI/AI/TankAIHelper.cs:1014-1018`
- **Description**: When the host changes AI type, `sender` is null. Code logs "Host changed AI", then on line 1018 dereferences `sender.CurTech` directly. Only `CurTech?.Team` is null-protected; `sender` itself is not. NRE on every host AI-type change in MP.
- **Evidence**: `if (sender == null) { DebugTAC_AI.Log(... "Host changed AI"); } if (sender.CurTech?.Team == tank.Team)` — second `if` does not guard `sender`.
- **Impact**: When host changes a tech's AI type in MP, the network message receive aborts with NRE. Crash-class regression on AI-mode changes in MP.
- **Sources**: Sweep-8 (round 1), Sweep-7 (round 2)
- **Status**: OPEN (round 2: Sweep-7 re-verified literal cite; no-else fallthrough confirmed.)

### BUG-004 — PathingService daemons missing DaemonWatchdog respawn factories
- **Severity**: BLOCKER
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/Pathing/PathingService.cs:142-143` (enqueue) + `SmartRuntime.cs:1034-1039` (RegisterCanonical site)
- **Description**: `PathingService.Init` calls `_pool.EnqueueLongRunning(ThreatFieldRebuildLoop, "ThreatFieldRebuild")` and `(PathSolveLoop, "PathSolve")` but never calls `DaemonWatchdog.RegisterCanonical` for either. `SmartRuntime.Init` only registers AetherFuser, GlobalPlanner, GlobalCoordinator. Both pathing-daemon names ARE on `DaemonWatchdog.CanonicalRoster` (DaemonWatchdog.cs:35-36).
- **Evidence**: `_factories.TryGetValue(daemonName, out var factory)` (DaemonWatchdog.cs:150) returns false for both pathing names → emits `[DAEMON-RESPAWN] daemon=… factory=missing` and skips respawn. User memory M1 flags this as the unfixed patch.
- **Impact**: If either pathing daemon thread is aborted (TAE during shutdown spurt, OOM-class crash, unhandled exception escape), the watchdog cannot recover. Pathing requests pile up until the queue capacity-sheds; threat fields never refresh; all Smart-driven pathing dies until hard restart.
- **Sources**: Sweep-6, Sweep-7
- **Status**: OPEN

## §3. MAJORs

### BUG-005 — SpawnSeaBase / SpawnAirBase silently fall back to land
- **Severity**: MAJOR
- **Category**: Stub
- **Location**: `TAC_AI/Templates/RawTechLoader.cs:974-983`
- **Description**: Both methods are `// N/A!!! WIP!!!` stubs that log a warning and return `SpawnLandBase(...)`. Called from real spawn paths at lines 265, 267, 399, 402, 602, 605 on TerrainType {Sea, Air} — so any "spawn an air base" request silently produces a land base.
- **Evidence**: `private static int SpawnSeaBase(...) { DebugTAC_AI.Log(...); return SpawnLandBase(...); }` (and the air variant). Multiple call sites cited.
- **Impact**: Authored sea/air bases never spawn correctly. Land tech is dropped where a flying garrison was requested; in water spawns at land Y → underwater / floating tile, breaking enemy base population.
- **Sources**: Sweep-1
- **Status**: OPEN

### BUG-006 — TrainingMatch.BuildTerrain empty stub; pretraining averages a model with itself (absorbs BUG-061)
- **Severity**: MAJOR
- **Category**: Stub
- **Location**: `TAC_AI/AI/Forms/Smart/Training/TrainingMatch.cs:341-345` + `Training/PretrainingPipeline.cs:30-37` (docstring) vs `:45-98` (Run body)
- **Description**: `BuildTerrain(...)` body is one comment. TrainingMatch's two-instance spawn is admittedly not implemented. PretrainingPipeline's docstring claims "per-parameter-average both training instances' model weights into a baseline file" — body never copies, averages, or weight-combines; it trains for N matches against the LearningService singletons then `ProfilePersistence.Save(baselinePath, new ILearnedModel[]{Intent, ActionValue, Residual, Threat})`. No second instance, no parameter-averaging primitive. (Round 4: absorbs the sharper round-2 BUG-061 framing.)
- **Evidence**: Empty `BuildTerrain` body + PretrainingPipeline docstring contradicting `Run` body.
- **Impact**: SelfPlay/CMA-ES/PretrainingPipeline is wired and callable but produces no actual baseline learning. Doc contract for the "two-instance averaged baseline" is fictional — anyone following TRAINING-CONTRACT §6 will believe the file contains averaged weights. Per the user memory contract this becomes a BLOCKER if anyone actually runs `SelfPlayHarness.BeginAsync`; otherwise dead-callable.
- **Sources**: Sweep-1 (round 1), Sweep-2 (round 2 — BUG-061 sharpening)
- **Status**: OPEN

### BUG-007 — GUINPTInteraction throws instead of recovering on missing data
- **Severity**: MAJOR
- **Category**: Broken
- **Location**: `TAC_AI/GUINPTInteraction.cs:138, 153, 165, 168-169`
- **Description**: Three `try/catch { throw new Exception("X is worthless"); }` blocks unconditionally rethrow on any failure inside an OnGUI menu code path. Followed by `// BROKEN!!!!` comment with the commented-out `AIGlobals.ModularMenu.OpenGUI(...)` call.
- **Evidence**: `catch (Exception) { throw new Exception("teamName is worthless"); } LaunchSubMenuClickable(); // BROKEN!!!! //AIGlobals.ModularMenu.OpenGUI(...)`
- **Impact**: Any transient failure (null cost calc, team-lookup race) → uncaught exception in OnGUI handler. IMGUI loop dies for one frame and may spam log. ModularMenu opening on the front block is removed (BROKEN).
- **Sources**: Sweep-1
- **Status**: OPEN

### BUG-008 — TerrainMap.IsTraversable water gating unimplemented (TODO v0.2)
- **Severity**: MAJOR
- **Category**: TODO
- **Location**: `TAC_AI/AI/Forms/Smart/Pathing/TerrainMap.cs:262-273` (specifically 270-272)
- **Description**: `IsTraversable` short-circuits airplane/hover and tests slope, then unconditionally returns `true` with a `// Water check: TODO v0.2` comment. `KickStart.WaterHeight` exists (KickStart.cs:404) but isn't consulted. `TrajectoryOptimizer.cs:253` calls `terrain.IsTraversable(xz, cap)` to penalize impassable cells.
- **Evidence**: `// Water check: TODO v0.2 — uses KickStart.WaterHeight; non-water vehicles need / height check vs WaterHeight; submarines need the inverse. return true;`
- **Impact**: TrajectoryOptimizer pays no water-traversal penalty (the 50f penalty at line 253 never fires for water). Wheeled tanks route straight across lakes/ocean; subs plan over land.
- **Sources**: Sweep-1, Sweep-6
- **Status**: OPEN

### BUG-009 — Residual feature slice always all-zero at fire-time (regression vs v0.1)
- **Severity**: MAJOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Forms/Smart/Control/ContinuousController.cs:424-436` + `Learning/Features/StrategicStateExtractor.cs:327-334`
- **Description**: `ExtractResidualSlice(aimTargetId)` reads `Raw[ResidualBase..+48]` from the daemon-published StrategicStateVector, but per plan §3.4 the daemon DELIBERATELY leaves the entire Residual block at zero. No other producer fills `Raw[168..215]`. The slice handed to `LeadResidualRecorder.OnFireCommit(..., fireFeatures)` is 48 zeros; `OnFireCommit` accepts length==48 without semantic check. **Round-3 sharpening (Sweep-2, Sweep-1, Sweep-4, Sweep-10)**: the feature-expansion commit `e9bfcd4` is wired-but-dead-on-arrival for the Residual model specifically — `LeadResidualRecorder.OnObservation:95-117` prefers the length-48 zero slice over the 6-slot fallback, so the architecturally larger Residual model now learns strictly less than the v0.1 6-slot skeleton would have. ActionValue producer is correctly wired (PublishActionValueEvent reads `Raw[40..167]`, which IS filled by `FillActionValueSlots`); Intent slice is consumed at `SmartRuntime.cs:267`. Residual is the ONE model where the trainer-input plumbing is fictional.
- **Evidence**: `StrategicStateExtractor.FillVector` calls FillIntentSlots / FillActionValueSlots / FillThreatSlots, no FillResidualSlots. Docstring at lines 327-334 confirms "The daemon does NOT pre-fill Residual on every tick; slots stay zero". Grep on writers to `Raw[ResidualBase..]` returns ZERO production hits (only the read-back in `ContinuousController.cs:430-434`).
- **Impact**: TrajectoryResidualModel trains against zero inputs → can only learn mean residual. Entire 48-dim Residual feature widening is wasted; lead-correction accuracy never improves from observation. Post-`e9bfcd4` is a strict regression for the Residual model vs the v0.1 6-slot baseline.
- **Sources**: Sweep-3 (round 1), Sweep-1/2/3/4/10 (round 3 — sharpened framing)
- **Status**: OPEN

### BUG-010 — SelfStateProbe LastAttacker latch never clears (phantom attacker)
- **Severity**: MAJOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/Features/SelfStateProbe.cs:396-426`
- **Description**: Once `_hadAttacker` becomes true on the rising edge, it is never reset on the falling edge (engine's `TechAI.LastAttacker` going null). Publish path falls through with `hasAttacker = _hadAttacker = true`, emitting the LAST-stamped `_lastAttackerPosWorld` as if current. `OnLifecycleReset()` clears it but has zero call sites.
- **Evidence**: Lines 412-421 set `_hadAttacker = true` only inside `if (last != null && last.tank != null)`; no `else` clears it. StrategicStateExtractor `SelfLastAttackerDistance` / `SelfLastAttackerAge` slots (AV[34],[35]) drift unboundedly.
- **Impact**: ActionValue model's "last attacker" features misleading after first attacker disengages or dies. Reward computation unaffected; feature quality degraded.
- **Sources**: Sweep-3
- **Status**: OPEN

### BUG-011 — ActionValueEstimator Q-target uses same action, not max_a' (SARSA not Q-learning)
- **Severity**: MAJOR
- **Category**: TODO
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ActionValueEstimator.cs:160-177`
- **Description**: `SingleStepGradient` computes `target = r + γ * Evaluate(NextState)` where NextState carries the action one-hot from the just-selected action, not the argmax. Line 167 TODO admits "proper max_a' Q(s', a') would require evaluating many candidate-action embeddings; TODO v0.2".
- **Evidence**: Method body + admitted TODO comment.
- **Impact**: Model learns the value of "this policy" rather than the optimal Q. Adequate for evaluation but not for the doc's claim of Q-learning.
- **Sources**: Sweep-3
- **Status**: OPEN

### BUG-012 — TrainerWorker leaks HostChanged subscription on every respawn
- **Severity**: MAJOR
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/OnlineTrainer.cs:319-326` (subscribe) + `Learning/LearningService.cs:119` (respawn factory)
- **Description**: `TrainerWorker` constructor calls `WorldEventBus.Subscribe<HostChanged>(OnHostChanged)` but no path ever calls the matching `Unsubscribe`. Respawn factory at LearningService.cs:119 is `() => pool.EnqueueLongRunning(new TrainerWorker(model).RunLoop, name)` — every respawn instantiates a fresh TrainerWorker, registering an additional handler. Previous workers' handlers still wired, mutating dead instances' `_hostLost`. `LIFECYCLE-OVERHAUL-PLAN.md:1443` explicitly acknowledges the missing finally.
- **Evidence**: OnlineTrainer.cs:321 subscribe with no Unsubscribe anywhere in file. LearningService.cs:119 instantiates new TrainerWorker on each respawn.
- **Impact**: After N respawns × 4 trainers, HostChanged multicast walks N+1 dead handlers per call. Dead TrainerWorker objects pinned alive via subscriber list. Long sessions accumulate.
- **Sources**: Sweep-4
- **Status**: OPEN

### BUG-013 — PathingService.OnWorldReset terrain-rebuild observation window (refined)
- **Severity**: MINOR (downgraded round 2)
- **Category**: Threading
- **Location**: `TAC_AI/AI/Forms/Smart/Pathing/PathingService.cs:168-177` (reassign) + `:426` (worker read)
- **Description**: `OnWorldReset` reassigns `_terrain = new TerrainMap()` on the main thread without synchronization while the PathSolveLoop worker is still reading the field at line 426. Round 2 narrowed the impact: TerrainMap ctor publishes a fully-constructed (but empty) cache, so worker observes either old or new, never half-published. Real symptom is that during the freshly-allocated window the optimizer biases toward flat-y=0 trajectories (height returns 0, clearance penalty for above-y=10 sample mild). NOT the dramatic "underground-then-above-ground" race round 1 framed.
- **Evidence**: Raw assignment at line 176. Sweep-6 found the canonical fix — `TerrainPublication.cs:17` atomic-swap holder — was AUTHORED but never adopted by PathingService (tracked separately as BUG-050).
- **Impact**: Transition-window glitch in trajectory cost shaping; ground techs may briefly route over flat-y paths. Empirically no crash reported (memory notes do not flag world-reset crashes).
- **Sources**: Sweep-6 (round 1); Sweep-1 / Sweep-3 / Sweep-4 / Sweep-6 / Sweep-7 (round 2 — partial dispute / narrative refinement)
- **Status**: OPEN (severity downgraded MAJOR→MINOR; see BUG-050 for the authored-but-unwired fix)

### BUG-014 — TeamRuntime instances leaked on Shutdown without Dispose
- **Severity**: MAJOR
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/SmartRuntime.cs:1138`
- **Description**: `_teams.Clear()` drops every `TeamRuntime` reference without calling `team.Dispose()`. Each TeamRuntime holds a `ReaderWriterLockSlim _techsLock` (kernel-handle IDisposable). `Dispose()` (line 557-564) is documented as called only by TeamReaperDaemon (line 553-556), but Shutdown happens BEFORE the reaper drains — `CancelAllAndJoin` (line 1116) kills the reaper thread first, then `_teams.Clear()` orphans remaining TeamRuntimes.
- **Evidence**: Shutdown order: CancelAllAndJoin → LearningService.Shutdown → PathingService.Shutdown → Pool.Dispose → `_teams.Clear()`. No iteration calling Dispose before clear.
- **Impact**: One `ReaderWriterLockSlim` kernel handle leak per surviving TeamRuntime per Shutdown. Across many Init/Shutdown cycles (form swap) accumulates kernel handles.
- **Sources**: Sweep-7
- **Status**: OPEN

### BUG-015 — AlliedMovementId returns null for AIType.Aviator/Buccaneer/Astrotech → DediAI silently reset to Escort every tick
- **Severity**: MAJOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Forms/Modified/ModifiedForm.cs:213-231` (AlliedMovementId) + `:192-202` (RunAllied fallthrough)
- **Description**: `TankAIHelper.ReValidateAI` (TankAIHelper.cs:1140-1154) keeps DediAI=Aviator/Buccaneer/Astrotech when corresponding `isAviator/Buccaneer/AstrotechAvail` flag is true. `ModifiedForm.AlliedMovementId`'s switch has no case for these three values → returns null. RunAllied then logs "AIType is set to an invalid state" and forces `helper.DediAI = AIType.Escort;`.
- **Evidence**: Switch lists Escort/Assault/Aegis/Prospector/Scrapper/Energizer/MT* only; default returns null. Reset logic at lines 197-202.
- **Impact**: Any save-loaded allied tech whose DediAI persists as Aviator/Buccaneer/Astrotech (legacy enum values, still settable from save data, AI-corp logic, or RawTechExporter) silently demoted to Escort each Operations tick, with log spam. Behavior regression vs original combined-meaning enum.
- **Sources**: Sweep-5
- **Status**: OPEN

### BUG-016 — ModifiedTargeting.CheckEnemyAndAiming dereferences lastEnemyGet.tank without null-guard
- **Severity**: MAJOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Forms/Modified/Brain/ModifiedTargeting.cs:36-37, 45`
- **Description**: Guard `if (helper.lastEnemyGet)` only checks Unity-null on Visible. Lines 36-37 then dereference `helper.lastEnemyGet.tank.blockman.blockCount` and `helper.lastEnemyGet.tank.Team` — if `.tank` became null between detection and validation, both NRE. Line 45 hits `helper.lastEnemy.tank.boundsCentreWorld` under same Visible-only guard.
- **Evidence**: Per memory `popup-storm-nre-fix`, this is the failure mode `IsLiveTechTarget` was introduced to cover; this method was not converted in the 46-callsite pass.
- **Impact**: Single-frame race when target Tank is destroyed mid-Operations tick → NRE aborts AI tick. Same class as prior FindEnemy bug.
- **Sources**: Sweep-5
- **Status**: OPEN

### BUG-017 — ManWorldRTS.RTSCommand drops per-type state assignment in MP
- **Severity**: MAJOR
- **Category**: MP
- **Location**: `TAC_AI/World/ManWorldRTS.cs:152-153, 162-163, 174-175, 182-183, 193-194`
- **Description**: For Escort/Aegis/Assault/Prospector/Scrapper RTS commands, code calls `SetOptionAuto(helper, TypeSwitch)`, then on `ManNetwork.IsNetworked` does `return;` — bypassing assignment of `helper.lastPlayer` / `helper.lastCloseAlly` / `helper.theResource`. Helper enters mode but has no objective.
- **Evidence**: Case `AIType.Escort` shows SetOptionAuto + SetRTSState + `if (ManNetwork.IsNetworked) return;` BEFORE the `helper.lastPlayer = Subject` assignment.
- **Impact**: In MP, all non-Movement RTS orders silently drop their target. AI enters mode with no objective; behavior diverges from SP.
- **Sources**: Sweep-8
- **Status**: OPEN

### BUG-018 — QuitSaveCoordinator.Flush has no host gate
- **Severity**: MAJOR
- **Category**: MP / Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/QuitSaveCoordinator.cs:62-108`
- **Description**: `Flush()` runs `LearningService.SaveProfile(CurrentPlayerId)` on every machine including clients (also from `AppDomain.ProcessExit` + `UnhandledException`). Clients never train but will overwrite per-player profile with current in-memory model state on quit/crash. Gates only on `LearningService.IsRunning`, not `SmartRuntime.IsHost`.
- **Evidence**: Flush body check is `IsRunning`-only; `LearningService.SaveProfile` (LearningService.cs:470) likewise has no host gate.
- **Impact**: Client quit writes profile from a model that never received training events (`AcceptingTrainingEvents` off on clients) → silent profile corruption across rejoin cycles.
- **Sources**: Sweep-8
- **Status**: OPEN

### BUG-019 — Manual save in SmartForm.OnEngineSave has no host gate
- **Severity**: MAJOR
- **Category**: MP / Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/SmartForm.cs:145-154`
- **Description**: KickStart's save-sink fires on every client on world save and runs `LearningService.SaveProfile` unconditionally — clients overwrite own profile.
- **Evidence**: Only `if (LearningService.IsRunning)` gate; no `SmartRuntime.IsHost`. Contrast with AutosaveWorker.cs:43 (correct pattern).
- **Impact**: Every world-save during MP triggers per-client profile rewrite with non-training-side state.
- **Sources**: Sweep-8
- **Status**: OPEN

### BUG-020 — m_USE_AVOIDANCE never restored on OnTechRecycle
- **Severity**: MAJOR
- **Category**: Lifecycle / MP
- **Location**: `TAC_AI/AI/Forms/Smart/SmartForm.cs:295-297` (set) + `:390-402` (recycle without restore)
- **Description**: OnTechSpawn writes `m_USE_AVOIDANCE = false` when host. OnTechRecycle does not restore it. If a tech survives host→client transition (or Smart form swap), vanilla avoidance stays disabled even though Smart no longer controls. `FIX-PLAN.md:200` flags this as required-but-not-applied.
- **Evidence**: OnTechRecycle body only calls `SmartRuntime.Deregister`; no `m_USE_AVOIDANCE = true` anywhere.
- **Impact**: Stuck-off avoidance on techs after host migration / form switch / Smart shutdown.
- **Sources**: Sweep-8
- **Status**: OPEN

### BUG-021 — Cross-team SetRelations mutations not host-gated (absorbs BUG-070)
- **Severity**: MAJOR
- **Category**: MP
- **Location**: `TAC_AI/Enemy/EnemyMind.cs:230`, `Enemy/RCore.cs:819`, `AI/Forms/Modified/Brain/ModifiedTargeting.cs:96`
- **Description**: Three call sites invoke `ETD.DegradeRelations` / `ManBaseTeams.DegradeRelations` with no `ManNetwork.IsHost` guard. `ManBaseTeams.SetRelations/Degrade/Improve` (lines 1036-1077) does not gate internally. The ManBaseTeams networking layer (CheckNeedNetworkHooks line 1451) pushes deltas FROM host TO clients only — client-side mutations are not replicated and produce desync. (Round 4: absorbs BUG-070, which independently re-cited `ModifiedTargeting.cs:96` and re-narrated it as the hot allied-refresh path — same line, same fix.)
- **Evidence**: No surrounding `if (ManNetwork.IsHost)` at any of the three sites. `ModifiedTargeting.cs:96` specifically sits in `TryRefreshEnemyAllied`'s manual-target branch (fires every tick a player has a manual target), so this site is the highest-frequency offender — every player-directed engagement on a client desyncs team relations.
- **Impact**: Clients running enemy AI ticks silently flip team relations only on the local client, diverging from host's authoritative state. Allied-path frequency at `ModifiedTargeting.cs:96` makes this an everyday-MP-play desync.
- **Sources**: Sweep-8 (round 1), Sweep-5 (round 2 — BUG-070 frequency framing)
- **Status**: OPEN

### BUG-022 — Save bypasses model SaveMutex (torn-write race)
- **Severity**: MAJOR
- **Category**: Threading / Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ProfilePersistence.cs:108-133` (specifically `:112`)
- **Description**: `ProfilePersistence.Save` iterates four models calling `m.StoreParameters(weights)` with NO `lock(m.SaveMutex)` wrap. `ILearnedModel.SaveMutex` (OnlineTrainer.cs:78) is documented for L-013 (per-model mutex coordinating TrainOneMinibatch / StoreParameters / FlushPendingForPersist). `TRAINING-DIRECTOR-PLAN.md:2234` lists this as "B3 | APPLIED-AS-IS" — but the lock was not added. Concurrent TrainerWorker.RunLoop (OnlineTrainer.cs:419 `lock (_model.SaveMutex) { TrainOneMinibatch }`) races against StoreParameters.
- **Evidence**: ProfilePersistence.cs:110-112 reads `m.StoreParameters(weights)` with no lock; compare OnlineTrainer.cs:419.
- **Impact**: Disk-persisted weight vector can be torn-but-CRC-valid (CRC computed over corrupted bytes). Next load applies parameters mid-Adam-step → numerical noise to potentially-NaN, irrecoverable training divergence on reload.
- **Sources**: Sweep-9
- **Status**: OPEN

### BUG-023 — UnknownTags drop on every save-after-load
- **Severity**: MAJOR
- **Category**: Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ProfilePersistence.cs:60` (Save sig) + `:108-133` (body) + `:118-126` (inline admission)
- **Description**: `LoadedProfile.Section.UnknownTags` (LoadedProfile.cs:31) is documented to "preserve unknown tags from disk so re-emit on next Save keeps fields we don't yet understand." But `Save()` takes `ILearnedModel[]`, NOT `LoadedProfile`, and ignores UnknownTags entirely. Every save path goes through `LearningService.SaveProfile → ProfilePersistence.Save(path, models)`; no `Save(LoadedProfile)` overload.
- **Evidence**: Inline comment at lines 118-126 admits: "Save() takes ILearnedModel[] so we have no Section reference here for UnknownTags ... UnknownTags drop on intentional save-after-load — documented limitation".
- **Impact**: The TLV forward-compat scheme (whole point of schema 3 per 0003_TlvSectionBody.cs:4-9) silently fails its design goal. A v0.5 profile saved by v0.5 client, loaded by v0.4 client and re-saved, loses every v0.5-introduced tag forever.
- **Sources**: Sweep-9
- **Status**: OPEN

### BUG-024 — On-disk ArchitectureVersion never validated against the live model
- **Severity**: MAJOR
- **Category**: Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ProfilePersistence.cs:272` (read) + `LearningService.cs:486-507` (Apply)
- **Description**: `0004_StrategicStateExpansion.cs:8-12` documents the v3→v4 bump's purpose: per-model arch ids shift and reject any cached float[] blob whose length no longer matches. The runtime check it relies on is ParameterCount mismatch (in `ILearnedModel.LoadParameters` length guard). But there's no direct comparison of `section.ArchitectureVersion` vs `model.ArchitectureVersion`.
- **Evidence**: ProfilePersistence.cs:272 reads `byte archVer = br.ReadByte()` into `section.ArchitectureVersion` at line 280, but grep outside this assignment returns only ProfileSelfTest tests + nothing in ApplyProfile.
- **Impact**: Documented safety property doesn't hold. Any future same-ParameterCount arch bump (permutation / re-weighting) silently loads old weights into new arch.
- **Sources**: Sweep-9
- **Status**: OPEN

### BUG-025 — TeamReaper / AutosaveWorker / TechLeakWatchdog absent from BOTH watchdogs
- **Severity**: MAJOR
- **Category**: Lifecycle
- **Location**: `TeamReaperDaemon.cs:61` enqueues `"TeamReaper"`; `AutosaveWorker.cs:25` enqueues `"Autosave"`; `TechLeakWatchdog.cs:39` enqueues `"TechLeakWatchdog"` — none in `DaemonWatchdog.CanonicalRoster`, none have `RegisterCanonical` calls.
- **Description**: Three long-running daemons silently lack respawn coverage. If any dies (ThreadAbort, unhandled exception escaping their RunLoop catch), it stays dead until Shutdown/Init.
- **Impact**: TeamReaper dying = team runtimes never reaped → leaked. AutosaveWorker dying = no autosave (only manual + quit-save). TechLeakWatchdog dying = leak detection silently off.
- **Sources**: Sweep-7
- **Status**: OPEN

## §4. MINORs

### BUG-026 — SmartRuntime.EnemyVehicleSnapshots() permanent null stub
- **Severity**: MINOR
- **Category**: Stub
- **Location**: `TAC_AI/AI/Forms/Smart/SmartRuntime.cs:619`
- **Description**: Hardcoded `=> null` with TODO v0.2. Consumed by `PathingService.cs:303-304` (ThreatFieldBuilder.Build); builder tolerates null and falls back to `BaselineEnemyRating = 30f`, `BaselineEnemyRadius = 100f`.
- **Evidence**: `internal IReadOnlyDictionary<TechId, VehicleModelSnapshot> EnemyVehicleSnapshots() => null;` + ThreatField fallback constants.
- **Impact**: ThreatField uses baseline radii for every enemy — threat anisotropy degraded across the board. Caller handles gracefully. No crash; permanent feature degradation.
- **Sources**: Sweep-1, Sweep-2, Sweep-4, Sweep-7
- **Status**: OPEN

### BUG-027 — TeamBelief.InjectObservations throws NotImplementedException on public API
- **Severity**: MINOR
- **Category**: Stub
- **Location**: ~~`TAC_AI/AI/Forms/Smart/Coordination/TeamBelief.cs:58-63`~~
- **Status**: REFUTED (round 2). See §5.

### BUG-028 — IMovementAICore.AvoidAssist(Vector3,Vector3) NotImplementedException on 4 ground/static cores
- **Severity**: MINOR
- **Category**: Stub
- **Location**: `LandAICore.cs:42-45`, `SeaAICore.cs:40-43`, `SpaceAICore.cs:41-44`, `StaticAICore.cs:28-31` (interface decl at `IMovementAICore.cs:41`)
- **Description**: Four ground/static implementations throw `NotImplementedException`. Round 2 (Sweep-2) corrected the round-1 scope: `AirplaneAICore.cs:958` and `HelicopterAICore.cs:550` actually have real bodies (Helper.AvoidAssistPrecise dispatchers, ~50 lines each). Contract publishes the method but no in-tree caller invokes it through the interface — every call site goes through `helper.AvoidAssist(...)` extension on TankAIHelper, defined `ModifiedAvoidance.cs:53`.
- **Evidence**: Grep for `AICore.AvoidAssist` returns zero hits; all 50+ uses are `helper.AvoidAssist(...)`. Sweep-2 round 2: Airplane/Helicopter bodies verified.
- **Impact**: Misleading contract for the 4 ground/static cores. Dead today; v2 plugin form or external mod hitting `IMovementAICore` directly per published contract throws instantly on those 4. Either drop from interface or stub via helper.
- **Sources**: Sweep-1, Sweep-2 (round 1); Sweep-2 (round 2 — scope correction)
- **Status**: OPEN

### BUG-029 — Tune.Float/Int/Bool returns default(Handle) whose Value NREs on unknown key
- **Severity**: MINOR
- **Category**: Stub
- **Location**: `TAC_AI/AI/Tunables/TunableRegistry.cs:145-165` + `Tunable.cs:76-96`
- **Description**: When key is unknown, `Tune.Float("foo")` returns `default(FloatHandle)` whose internal `Tunable T = null`. `Value => T.CurFloat` NREs. The `IsValid` guard exists only on the struct and is easy to miss. No active caller today.
- **Evidence**: `return default(FloatHandle);` (line 150) → `public float Value => T.CurFloat;` (Tunable.cs:80) no null guard.
- **Impact**: First future caller forgetting IsValid crashes. Trivial fix: non-null sentinel handle or throw at lookup.
- **Sources**: Sweep-2
- **Status**: OPEN

### BUG-030 — GUIAIManager unhooked Guard-selection UI (stale TODO)
- **Severity**: MINOR
- **Category**: TODO
- **Location**: `TAC_AI/GUIAIManager.cs:17`
- **Description**: `// TODO - add the hook needed to get the UI to pop up on Guard selection`. Wheel UI never opens when player picks Guard mode. Other AIType paths trigger via `OnPlayerSwap`/`TankDriverChangedEvent`, but Guard has no equivalent path.
- **Evidence**: Line 17 TODO + no subscribe-to-Guard-event observable in file.
- **Impact**: Player sets Guard → no confirmation wheel. Player-feedback hole; AI behavior unaffected.
- **Sources**: Sweep-2
- **Status**: OPEN

### BUG-031 — SmartMovementController.Drive* no-ops bypass EControlCoreSet bus
- **Severity**: ~~MINOR~~ (CLOSED round 4 polish — DESIGN-INTENT)
- **Category**: Stub
- **Location**: ~~`TAC_AI/AI/Forms/Smart/SmartMovementController.cs:32-57`~~
- **Status**: REFUTED (round 4 — DESIGN-INTENT closure after 3 rounds NEEDS-VERIFY). See §5.

### BUG-032 — BGeneral.StopByBase/StopByPosition double-assigns ThrottleState (Yield then PivotOnly)
- **Severity**: MINOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Movement/BGeneral.cs:161-162, 193-194`
- **Description**: Two consecutive assignments: `helper.ThrottleState = AIThrottleState.Yield;` immediately followed by `helper.ThrottleState = AIThrottleState.PivotOnly;`. Yield is a dead write — copy-paste residue or incomplete refactor.
- **Evidence**: Both pairs cited.
- **Impact**: Tech parks in PivotOnly at base proximity; Yield code path never observed. No behavior degradation but signals incomplete refactor / merge.
- **Sources**: Sweep-5
- **Status**: OPEN

### BUG-033 — AICore Initiate-time anchored sanity logs are unconditional Info-level spam
- **Severity**: MINOR
- **Category**: Broken (logspam)
- **Location**: `LandAICore.cs:30-39`, `SeaAICore.cs:30-37`, `SpaceAICore.cs:30-38`
- **Description**: When anchored AICore initializes despite inconsistent autoAnchor flag, logs "Should NOT be active when anchored" via `DebugTAC_AI.Log` — not via `LogWarnFileOnly` per memory `ai-warning-routing-preference`.
- **Evidence**: Plain `DebugTAC_AI.Log(...)` in three Initiate methods.
- **Impact**: Player-visible log noise on save-load edge cases without an actual visible failure.
- **Sources**: Sweep-5
- **Status**: OPEN

### BUG-034 — ThreatField LOS factor doc-vs-behavior mismatch
- **Severity**: MINOR
- **Category**: Broken (doc)
- **Location**: `TAC_AI/AI/Forms/Smart/Pathing/ThreatField.cs:140-144` + class doc at `:69-71` + caller `PathingService.cs:186`
- **Description**: Class doc says "LOS factor at v0.1.0 is a constant 1.0 (no LOS test) — activating it requires passing a TerrainMap reference. TODO v0.2:". But `LosFactor` already calls `_terrain.RaycastSegment(...)` when `_terrain != null`, and `PathingService.GetThreatField` always constructs with a non-null terrain. LOS shading IS active.
- **Evidence**: Code path at lines 142-143 + wiring at PathingService.cs:186.
- **Impact**: Behavior surprise — actual threat field has 0.3× shading when terrain occludes; downstream readers may believe constant-1.0 per doc. CMA-ES tuning will overfit against behavior the docs disclaim.
- **Sources**: Sweep-6
- **Status**: OPEN

### BUG-035 — TrajectoryOptimizer tuning fields publicly mutable static floats with no volatility
- **Severity**: MINOR
- **Category**: Threading
- **Location**: `TAC_AI/AI/Forms/Smart/Pathing/TrajectoryOptimizer.cs:108-119`
- **Description**: `WThreat, WTerrain, WSmooth, WLength, WReach, WVelocity, LearningRate, GradientSteps, SamplePoints, ConvergenceGradientNorm, NumericalEps` non-volatile public static fields, written from main-thread (CMA-ES tuner) and read inside `PathSolveLoop` on a worker. .NET 4.6.1, no Volatile/Interlocked → worker may observe stale tunings until a memory barrier.
- **Evidence**: All `public static float` with no Volatile/Interlocked.
- **Impact**: Mostly benign for float-sized writes on x86, but a tuner sweep landing across multiple fields can be observed half-applied — one outlier path per tuning step. Not a crash, non-reproducible solver runs.
- **Sources**: Sweep-6
- **Status**: OPEN

### BUG-036 — StrategicStateExtractor missing from DaemonWatchdog.CanonicalRoster
- **Severity**: MINOR
- **Category**: Lifecycle
- **Location**: `DaemonWatchdog.cs:30-41` (CanonicalRoster) + `SmartRuntime.cs:1060` (RegisterCanonical)
- **Description**: `RegisterCanonical("StrategicStateExtractor", …)` is called, but the name is NOT in `DaemonWatchdog.CanonicalRoster[]`. `DaemonWatchdog.DoScan` (line 96-99) iterates only the roster, so extractor liveness is never checked by DaemonWatchdog. `WorkerHealthMonitor` still covers it via `_factories` iteration.
- **Evidence**: Comment at `StrategicStateExtractor.cs:30-33` notes "MUST match the EnqueueLongRunning label exactly … plan §4.1 last bullet + round-2 R2-15 BLOCKER on CanonicalRoster string identity" — confirms intent.
- **Impact**: Half the watchdog coverage missing for the extractor — defeats dual-redundancy intent.
- **Sources**: Sweep-7
- **Status**: OPEN

### BUG-037 — OnEngineLoad never reloads profile post-load
- **Severity**: MINOR
- **Category**: Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/SmartForm.cs:168-183`
- **Description**: `OnEngineLoad(false)` (post-load) publishes WorldLoaded but does NOT call `LearningService.LoadProfile`. Inline comment defers to "future SaveProfile/LoadProfile cycle picks up the right file automatically" — but `LoadProfile` is only invoked once at `LearningService.Init:101`, never on world-reload.
- **Evidence**: SmartForm.cs:177 publishes WorldLoaded only; no LoadProfile call. Grep confirms one-time-only invocation.
- **Impact**: World reload mid-session keeps in-memory weights from previous mission. If another process updated disk profile in interim, changes not seen until process restart.
- **Sources**: Sweep-9
- **Status**: OPEN

### BUG-038 — MigrationRunner static init swallows errors then surfaces only on first use
- **Severity**: MINOR
- **Category**: Lifecycle / Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ProfilePersistence.cs:421-477`
- **Description**: Static ctor catches `Exception` into `_initError` (line 475) and rethrows on RunForward (line 481). If a schema bump ships with a hole AND no migration ever runs (cold-start path always at CurrentSchemaVersion), the hole is never detected. `MigrationRunner.MaxSchemaVersion` would be 0 in that state but is not asserted equal to `ProfilePersistence.CurrentSchemaVersion`.
- **Evidence**: Search for `MaxSchemaVersion` outside MigrationRunner returns nothing; invariant exists only in comments.
- **Impact**: Schema-ladder hole introduced during future bump can pass CI if no test loads old-schema fixture. Cold-start always writes CurrentSchemaVersion → bug stays dormant.
- **Sources**: Sweep-9 (round 1 + round 2)
- **Status**: OPEN (round 2: Sweep-9 verified `ProfileSelfTest.Run` round-trips weights only; no `MigrationRunner.MaxSchemaVersion == ProfilePersistence.CurrentSchemaVersion` assertion exists. Promoted NEEDS-VERIFY→OPEN.)

### BUG-039 — TrainingMode tunable invisible to F8 Live Tweaker / Preset save-load
- **Severity**: MINOR
- **Category**: Config
- **Location**: `TAC_AI/AI/Tunables/Catalog/TrainingModeTunables.cs:24` (key `"training.enabled"`) vs `Smart/Tooling/TunableMenuBridge.cs:19-22` (SmartPrefixes whitelist)
- **Description**: `TrainingModeTunables.cs:14-17` documents in-game F8 + console `smart.tunables.list` surface. `TunableMenuBridge.SmartPrefixes` = `{"smart.","aether.","chassis.","threading.","weaponspec.","armormap.","los."}` — `"training."` is missing. LiveTweakerPanel, PresetIO, SnapshotManager, SmartConsoleCommands.TunablesList, SmartTestSuite all iterate `SmartEntries` and don't see the key.
- **Evidence**: Prefix array cited; key cited.
- **Impact**: Documented F8/preset toggle silently absent. Vanilla options menu still works (TunableOptionsPublisher iterates `TunableRegistry.All`); console-toggle command still works via special case. Advertised F8 row + preset save-load are broken.
- **Sources**: Sweep-10
- **Status**: OPEN

### BUG-040 — `Combat.StuckNetProgressFloor` registered as Int, backing field is float
- **Severity**: MINOR
- **Category**: Config
- **Location**: `TAC_AI/AI/Tunables/Catalog/CombatTunables.cs:124-128` vs `AIGlobals.cs:313`
- **Description**: Tunable registered as `RegisterInt(... 1, 0, 50, 1, bind: v => AIGlobals.StuckNetProgressFloor = v)`. AIGlobals declares `public static float StuckNetProgressFloor = 1f;`. Consumer is float-math at `TankAIHelper.Physics.cs:34-35`. Implicit int→float compiles but menu/registry quantize to whole-unit steps.
- **Evidence**: file:line cites.
- **Impact**: User cannot tune below 1.0 or to 0.5; behavior acceptable but the dial is needlessly coarse.
- **Sources**: Sweep-10
- **Status**: OPEN

### BUG-041 — PUCT search rebuilds the tree every tick (no learned-policy priors)
- **Severity**: MINOR
- **Category**: TODO
- **Location**: `TAC_AI/AI/Forms/Smart/Planning/PUCTSearch.cs:103, 196`
- **Description**: `Search` doc says "v0.1.0: tree reuse TODO — rebuild each tick"; `Expand` sets `PriorByAction[hash] = uniform; // TODO v0.2: learned policy when Learning ships.` Learning has shipped (`LearningService.Intent` etc.) but no policy consulted.
- **Impact**: Strategic planner runs full rollout every coordinator tick; learned priors never affect plan selection. Performance + accuracy loss, not a crash.
- **Sources**: Sweep-1
- **Status**: OPEN

### BUG-042 — Multiple dead files / dead tunable / dead method-variants
- **Severity**: MINOR
- **Category**: Stub
- **Location**: Multiple — `SmartIdentityTuning.cs:42` (`UseDrillForGathererBoost`); `Enemy/RPathfinding.cs`, `TerrainOperations.cs` (whole files, not in csproj); `LearningService.cs:247-279` (`ApplyProfileSafe.Try` local function); `StrategicStateExtractor.cs:111-114` (`OnTechSpawn` declared, no caller); `SelfStateProbe.cs:295` (`OnLifecycleReset` no callers)
- **Description**: Cluster of dead code — unused tunable (zero readers, docstring says "v0.3"); two dead files on disk not in csproj (`Enemy/RPathfinding.cs` has no external grep hits; `TerrainOperations.cs` namespace `Sub_Missions` references unknown `Debug_SMissions`/`WorldTile`); dead local function inside `ApplyProfileSafe` (`Try(string, ILearnedModel, byte[])` defined but never invoked → docstring's per-model graceful degradation never runs, falls through to wholesale `ApplyProfile` — see BUG-075 for the host-handover hot-path framing of this same dead code); `StrategicStateExtractor.OnTechSpawn` forwards to `_output.OnTechSpawn(id)` but no caller (matching `OnTechRecycle` IS wired at SmartRuntime.cs:1317); `SelfStateProbe.OnLifecycleReset` defined but zero call sites → recycle doc-guarantee unwired (probe is GC'd with per-tech state via `Forget`, so no actual leak).
- **Impact**: Cosmetic clutter and contract-vs-behavior drift. No runtime harm individually but signals incomplete refactors.
- **Sources**: Sweep-1, Sweep-3, Sweep-4, Sweep-10
- **Status**: OPEN (round 3 — `Templates/EBasePurpose.cs` removed from this cluster; reclassified as BLOCKER-class csproj omission folded into BUG-001)

### BUG-043 — Stale documentation across modules
- **Severity**: MINOR
- **Category**: TODO
- **Location**: Multiple — `Smart/Control/WeaponFireController.cs:71` ("Energy estimation TODO" — WeaponSpec.cs:34-38 reflects from `m_FiringEnergyRequired`, fix shipped); `AIGlobals.cs:669-679` (`DetermineRadarType(int, Vector3, bool)` computes `WorldPosition.FromScenePosition(posScene)` then discards it, forwards to (ID, true, anchored)); `Smart/Threading/AbortGuard.cs:94-100` (literal `[WORKER-ABORT-GUARD-BUG]` tag); `Smart/Control/TacticalOptimizer.cs:106-110` (docstring says ArmorFacing/Cover/TeamRole are no-op stubs but body wires them at lines 125-127); `Smart/Learning/Features/StrategicStateExtractor.cs:268-274` (claims compile collision on `DamageSummary` already resolved — RecentDamageDealtAccumulator.cs:13 declares `DealtDamageSummary`); `LineOfSightProducer.cs:26` (XML-doc default OFF vs field init `= true` at line 33); `SmartRuntime.cs:1070-1072` (TODO v0.2 mod-root accessor — still uses `Environment.CurrentDirectory + "Mods"`, wrong on non-default launchers).
- **Impact**: Documentation drift only; future maintainers reason against wrong contracts. Mod-root TODO has one real side effect (profile saves land at wrong location on non-Steam/non-default launches).
- **Sources**: Sweep-1, Sweep-2, Sweep-3, Sweep-7, Sweep-8, Sweep-10
- **Status**: OPEN

### BUG-044 — TechLeakWatchdog lacks IsHost gate
- **Severity**: MINOR
- **Category**: MP
- **Status**: REFUTED (round 2). See §5.

### BUG-045 — StrategicStateExtractor.OnTechSpawn declared but never wired
- **Severity**: MINOR
- **Category**: Broken
- **Status**: REFUTED (round 2 — folded into BUG-042 dead-method cluster). See §5.

## §3b. MAJORs (round 2 additions)

### BUG-046 — Adam optimizer state (M, V, T) never persisted to profile
- **Severity**: MAJOR
- **Category**: Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/OnlineTrainer.cs:93-144` (AdamState) + `ProfilePersistence.cs:108-133` (Save body) + per-model `LoadParameters` (e.g. `ActionValueEstimator.cs:305-315`)
- **Description**: AdamState carries `float[] M`, `float[] V`, `long T` per model. `ProfilePersistence.Save` only writes `_params` via `m.StoreParameters(weights)`. `LoadParameters` copies only `_params`; `_adam` stays at `M=0, V=0, T=0` from constructor (or in-memory state) post-load.
- **Impact**: Every profile reload restarts Adam bias-correction at T=1, multiplying effective LR by ~10× on the first post-load step against trained weights. Equivalent to a one-shot LR spike per cold-start. Documented "training continues across reloads" invariant doesn't hold.
- **Sources**: Sweep-3 (round 2)
- **Status**: OPEN

### BUG-047 — LoadParameters not under SaveMutex on OnHostGained reload (sibling of BUG-022)
- **Severity**: MAJOR
- **Category**: Threading / Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/LearningService.cs:486-507` (ApplyProfile) called from `:271` (ApplyProfileSafe) from `:223` (OnHostGained); per-model bodies at `ActionValueEstimator.cs:309-315`, `OpponentIntentClassifier.cs:351-356`, `TrajectoryResidualModel.cs:214-219`, `ThreatAssessmentModel.cs:180-185`
- **Description**: Load-side sibling of BUG-022. `ApplyProfile` iterates section IDs and calls `Intent.LoadParameters(section.Weights)` etc. Each implementation does `Array.Copy(src, _params, _params.Length); _publishedParams.Write(Clone(_params));` with NO `lock(_saveMutex)`. At Init this is benign; at `OnHostGained` (WorldEventBus thread) trainer workers are running and concurrently entering `lock(_model.SaveMutex) { TrainOneMinibatch }` → Array.Copy races against Adam.Step's element-wise writes.
- **Impact**: Host-promotion mid-session can install corrupted (half-old, half-new) weights into the live model. Subsequent inference / training operates on a frankenstate until the next StoreParameters → Adam re-converges.
- **Sources**: Sweep-9 (round 2)
- **Status**: OPEN

### BUG-048 — StrategicStateExtractor captures stale TerrainMap across PathingService.OnWorldReset
- **Severity**: MAJOR
- **Category**: Threading / Lifecycle
- **Location**: `Smart/Learning/Features/StrategicStateExtractor.cs:69, 96, 247-261` + `Smart/SmartRuntime.cs:1047-1050` (ctor) + `Smart/Pathing/PathingService.cs:176` (OnWorldReset)
- **Description**: `_terrainMap` is `private readonly TerrainMap` captured once at construction from `PathingService.CurrentTerrain`. When `PathingService.OnWorldReset` does `_terrain = new TerrainMap();` on mission transition, the extractor keeps reading the OLD TerrainMap forever. The `IsFreshlyAllocated` gate at line 173 only catches the first-ever allocation; after that the extractor's reference becomes a dangling pointer to a TerrainMap that PathingService no longer refreshes (PathingService's main-thread tick reads the live `_terrain`, not the extractor's captured copy).
- **Impact**: Every per-tech `selfSlope`, `selfTerrainH`, `tgtSlope`, `tgtTerrainH`, `losBlocked` slot in StrategicStateVector is computed against the previous mission's terrain. Cross-mission training signals are poisoned silently.
- **Sources**: Sweep-4 (round 2)
- **Status**: OPEN

### BUG-049 — ModifiedForm.cache never cleared on DeInitGlobal (stale IBehavior dispatch)
- **Severity**: MAJOR
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Modified/ModifiedForm.cs:116` (cache decl) + `:31` (DeInitGlobal body `{ }`)
- **Description**: `private static readonly Dictionary<string, IBehavior> cache` is never cleared on `DeInitGlobal()`. On a form swap Modified→Vanilla→Modified, or any future re-init of `AIModuleRegistry`, cached `IBehavior` instances pin module-state alive and may dispatch into IDs whose registrations were swapped out. `DeInitGlobal` should `cache.Clear()` to release IBehavior refs and avoid stale-dispatch.
- **Impact**: Form-swap leaks dispatch state; stale module references survive re-init. Compounds with form-selector regressions.
- **Sources**: Sweep-5 (round 2)
- **Status**: OPEN

### BUG-050 — TerrainPublication authored but PathingService never adopts (BUG-013 fix unwired)
- **Severity**: MAJOR
- **Category**: Threading / Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/Pathing/TerrainPublication.cs:17-42` (class) vs `Pathing/PathingService.cs:98, :124, :155, :176, :426`
- **Description**: The L-033 atomic-swap holder was authored — class docstring explicitly says it "replaces the prior pattern where PathingService mutated a `_terrain` field directly" — but PathingService.cs still declares `private static TerrainMap _terrain` and uses raw assignment at Init/Shutdown/OnWorldReset. Grep on `TerrainPublication` outside the class file returns only csproj, LIFECYCLE-OVERHAUL-PLAN.md, and PathingResetTests self-tests. Production code never instantiates it.
- **Impact**: BUG-013's race remains exactly as round-1 described, despite the v0.1 author shipping the fix class. Pathing-Reset tests pass against the holder; production runs without it.
- **Sources**: Sweep-6 (round 2)
- **Status**: OPEN

### BUG-051 — PathingService.Init leaks 3 WorldResetRegistry entries per Init/Shutdown cycle
- **Severity**: MAJOR
- **Category**: Lifecycle
- **Location**: `PathingService.cs:136-140` (Register + 2 RegisterLambda) vs `:150-159` (Shutdown — no unregister) + `WorldResetRegistry.cs:25-55` (List-backed append-only)
- **Description**: PathingService.Init unconditionally appends one IWorldResettable (`bp`) + two lambdas (`ThreatFields`, `LastPaths`) to `WorldResetRegistry._entries`. Shutdown nulls `_backpressure` etc. but never removes the entries. Form-swap (SmartForm.DeInitGlobal → InitGlobal) drives repeated SmartRuntime.Init → PathingService.Init cycles; each cycle += 3 entries; previous-cycle `bp` IWorldResettable closures pin the stale instance alive. Compare `SmartForm.cs:625-633 EnsureLegacyResetsRegistered` which gates on an `Interlocked.Exchange` one-time latch — PathingService has none.
- **Impact**: Per form swap: 3 entries leak + the prior PathRequestBackpressure instance pinned via closure. ResetAll on world-change still works because lambdas dereference current static fields, but on a long session the list grows linearly with form-swap count.
- **Sources**: Sweep-6 (round 2)
- **Status**: OPEN

### BUG-052 — WeaponFireBuffer.Clear leaks live WeaponsFiredEvent subscriptions
- **Severity**: MAJOR
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/World/WeaponFireBuffer.cs:139` (Clear) + `SmartRuntime.cs:1166` (WeaponFires?.Clear)
- **Description**: `Clear()` is one line: `_byTech.Clear(); _subs.Clear();` — drops the closure dict but never iterates per-tech subs to call the matching `Unsubscribe` on each tank's `TechWeapon.WeaponsFiredEvent`. SmartRuntime.Shutdown calls only Clear(), not a DetachAll pass. Subscriptions wired at `SmartEventBridge.cs:202` stay attached to live TechWeapon objects after the buffer is dropped. Compare `CargoStatePublisher.cs:354-359 Clear()` which calls `DetachAll()` first.
- **Impact**: Form swap Smart→Vanilla (or DeInitGlobal) leaves the dead WeaponFireBuffer instance pinned via every live tech's WeaponsFiredEvent multicast list. Each shot walks a dead closure that GetOrAdds into a Clear'd dict. On re-Init, a NEW WeaponFireBuffer + NEW Wires accumulate alongside the orphaned ones.
- **Sources**: Sweep-7 (round 2)
- **Status**: OPEN

### BUG-053 — SmartEventBridge.Uninstall fails to detach WeaponFires/CargoState per-tank wirings
- **Severity**: MAJOR
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/Integration/SmartEventBridge.cs:160-167`
- **Description**: Uninstall iterates `_damageHandlers` for DamageEvent unsubscribe only. It does NOT iterate the same key set to call `SmartRuntime.WeaponFires.Unwire(techId, tank.Weapons)` or `SmartRuntime.CargoState.DetachPerTank(tank)`. AttachPerTank wires three surfaces (damage + cargo + weapon-fire); DetachPerTank unwires all three. Uninstall covers only damage.
- **Impact**: SmartEventBridge.Uninstall called from SmartForm.DeInitGlobal — partial teardown. Compounds BUG-052; also leaks CargoState's per-tank `ManLootHolder.PickupEvent` / `ReleaseEvent` subs.
- **Sources**: Sweep-7 (round 2)
- **Status**: OPEN

### BUG-054 — GUINPTInteraction bribe-to-ally bypasses host (client-side ImproveRelations)
- **Severity**: MAJOR
- **Category**: MP
- **Location**: `TAC_AI/GUINPTInteraction.cs:372`
- **Description**: Bribe-to-ally button calls `ETD.ImproveRelations(playerTeam)` directly from a client OnGUI handler. Distinct from BUG-021 (this is UI-initiated, not OnHit-initiated). Other UI mutations (`Enable Auto`, line 446) correctly route through `TrySendNPTBribe` → `NetworkedNPTBribe` to defer to host (line 590-602). The bribe-improve and DispBaseAnnoy paths don't.
- **Impact**: Client click silently desyncs team relations vs host, and the host won't see the bribe at all.
- **Sources**: Sweep-8 (round 2)
- **Status**: OPEN

### BUG-055 — LearningService.SaveProfile entry has no IsHost gate (root cause of BUG-018/019)
- **Severity**: MAJOR
- **Category**: MP / Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/LearningService.cs:470`
- **Description**: `public static void SaveProfile(string)` has no `IsHost` gate at the entry. Both BUG-018 and BUG-019 list the symptom; the root is that `SaveProfile` itself accepts the call. Even `SnapshotManager.cs:80` calls SaveProfile on whoever clicked the restore button (any client OnGUI), so a client restoring a snapshot writes their local profile too. Gating at the entry would close BUG-018, BUG-019, BUG-054 (cross-team), and this in one place. **Round-3 (Sweep-9)**: enumerates all 5 known SaveProfile call sites — QuitSaveCoordinator.cs:101, SmartForm.cs:150, SnapshotManager.cs:80, AutosaveWorker.cs:76, plus the entry itself. Only AutosaveWorker has an IsHost gate (cs:43, racy against host-loss — see BUG-082).
- **Impact**: Multiple client-side save paths all write profile from non-training state. Compounded by BUG-058 (rotation ring corruption persists across rejoin).
- **Sources**: Sweep-8 (round 2), Sweep-9 (round 2 + round 3)
- **Status**: OPEN

### BUG-056 — ApplyNonPlayerAlignment fires GenerateEnemyAI on clients (Initiate-side gap)
- **Severity**: MAJOR
- **Category**: MP
- **Location**: `TAC_AI/AI/TankAIHelper.cs:3023-3030` (ApplyNonPlayerAlignment)
- **Description**: `EnemyMind.cs:124` `Tank.DamageEvent.Subscribe(OnHit)` happens on every peer because the alignment-dispatch path doesn't role-guard `ApplyNonPlayerAlignment`. Even with BUG-021 fixed by gating each Degrade call, the broader pattern — enemy AI Initiate paths firing on clients — is a structural gap. `ApplyPlayerAlignment` (line 2999) and `ApplyStaticAlignment` (line 3032) both guard with `role != MpRole.MpClient` for specific actions; `ApplyNonPlayerAlignment` does not skip `GenerateEnemyAI`.
- **Impact**: Clients run enemy AI Initiate paths, subscribe damage events, attempt host-authoritative mutations through OnHit. Broader root cause behind BUG-021.
- **Sources**: Sweep-8 (round 2)
- **Status**: OPEN

### BUG-057 — TunableRegistry.Register* wipes user-tuned values on re-init (Init/DeInit cycle bug)
- **Severity**: MAJOR
- **Category**: Config
- **Location**: `TAC_AI/AI/Tunables/TunableRegistry.cs:49-51, 69, 88` (RegisterFloat/Int/Bool)
- **Description**: Each Register* unconditionally sets `t.CurFloat = defaultValue; bind?.Invoke(defaultValue);` even when AddOrGet returned an EXISTING Tunable. `KickStart.cs:930` calls `AIModuleBootstrap.DeInitAIModules()` which resets `initialized = false`. The next `InitAIModules()` (ProfileRunner.cs:16/22, TunableOptionsPublisher.cs:27) re-runs every catalog `Register()`. Every operator-tuned value (CMA-ES results, F8 tweaks, preset-loaded values) is silently reset to compile-time default on mod-toggle / re-init cycle.
- **Impact**: Mod-toggle, scene-reload, or any path triggering DeInit/Init nukes all live-tuned values. Persisted-profile reload also short-circuited.
- **Sources**: Sweep-10 (round 2)
- **Status**: OPEN

### BUG-058 — Client-side Save corrupts backup ring across MP rejoin cycles
- **Severity**: MAJOR
- **Category**: MP / Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ProfilePersistence.cs:74-98` (rotation) + interaction with BUG-018/019/055
- **Description**: Even after BUG-018/019/055 are fixed, the existing `Save()` rotation logic (previous→penultimate, current→previous, new→current) means each erroneous client save destroys one tier of backup. A client that hits ProcessExit + world-save twice in one session promotes a host-trained backup out of `.penultimate` entirely. The Load fallback chain then can't recover even if a fix-forward host-only gate ships later — the disk state is already poisoned.
- **Impact**: Persistent corruption across MP rejoin cycles even after the BUG-018/019/055 fixes ship — affects any save written under prior buggy builds.
- **Sources**: Sweep-9 (round 2)
- **Status**: OPEN

### BUG-059 — RunAllied stale-operator post-demotion on Aviator/Buccaneer/Astrotech (compounds BUG-015)
- **Severity**: MAJOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Forms/Modified/ModifiedForm.cs:43-47`
- **Description**: When `move` is null (BUG-015 path), the form sets `helper.DediAI = AIType.Escort;` and then calls `ctx.CommitOperator()` having performed zero movement work this tick. The tick yields a stale operator from `ctx.LoadOperator()` — no `BGeneral.ResetValues` was called. For one frame post-demotion the operator's `DriveDest`/`FIRE_ALL` flags carry over from prior tick — a transient mis-drive on every Aviator/Buccaneer/Astrotech tech every tick until the next tick picks up Escort and resets.
- **Impact**: One-frame mis-drive per demoted tech per tick. Compounds BUG-015 logspam.
- **Sources**: Sweep-5 (round 2)
- **Status**: OPEN

### BUG-060 — ModifiedTargeting.InRangeOfTarget NREs on .tank null (sibling of BUG-016)
- **Severity**: MAJOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Forms/Modified/Brain/ModifiedTargeting.cs:223`
- **Description**: `InRangeOfTarget(target, distance)` dereferences `target.tank.boundsCentreWorldNoCheck` with no null-guard on either `target` or `target.tank`. Public extension method; reachable from `UpdateTargetCombatFocus` via `:142 helper.InRangeOfTarget(helper.MaxCombatRange) → :217 helper.InRangeOfTarget(helper.lastEnemyGet, distance)`. The `:136 if (!helper.lastEnemyGet)` guard only Unity-null-checks the Visible; if `.tank` becomes null between :136 and :142, NRE inside `InRangeOfTarget`. Same failure mode as BUG-016.
- **Impact**: Same NRE class as BUG-016 — single-frame race on target Tank destruction → AI tick abort. Reachable from a different call path than BUG-016 cite.
- **Sources**: Sweep-5 (round 2)
- **Status**: OPEN

### BUG-061 — PretrainingPipeline.Run docstring claims model averaging but never averages
- **Severity**: ~~MAJOR~~ (MERGED round 4 polish — sharper restatement of BUG-006's PretrainingPipeline half)
- **Category**: Stub
- **Status**: REFUTED-MERGED (round 4 — folded into BUG-006; narrative absorbed into BUG-006's PretrainingPipeline-half description). See §5.

## §4b. MINORs (round 2 additions)

### BUG-062 — OpponentIntentClassifier class-doc claims BPTT frozen but UseFullBPTT branch is live

- **Severity**: MINOR
- **Category**: TODO / Stale doc
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/OpponentIntentClassifier.cs:28-32` (class-doc) vs `:172-180` (UseFullBPTT branch) + `:217-302` (`TrainOneMinibatch_FullBptt`)
- **Description**: Class-doc says "the GRU recurrent state's parameters … are frozen — BPTT through them is TODO v0.2" but the code branches on `LearningTuning.UseFullBPTT` to the full-BPTT path (unfrozen in P8 Item 19 / migration `M0002_BpttUnfreeze` per inline comment at `:45-47`). Class-level docstring contradicts active code path. Same drift pattern as BUG-043.
- **Sources**: Sweep-1 (round 2)
- **Status**: OPEN

### BUG-063 — IntentRegistry/IntentSidecar publishes are dead on arrival (producer never wires)
- **Severity**: MINOR
- **Category**: Stub
- **Location**: `IntentRegistry.cs:25-36` (doc) + `SmartRuntime.cs:954` (Sidecar wire) + `SmartTestSuite.cs:98` (the only publisher)
- **Description**: Class doc says "Producer-only at v0.2 ship — no v0.2 consumer reads ... will publish; … reads in v0.3+". Grep on `IntentSidecar.Publish` / `IntentRegistry.Publish` returns zero production publishers — only `SmartTestSuite.cs:98` publishes a `default(IntentSnapshot)` for a Deregister-cleanup unit test. `OpponentIntentClassifier` (the documented producer) never publishes. Wire-up is dead-on-arrival; sidecar memory wasted per spawn.
- **Impact**: Sidecar allocation per spawn; no consumer. Either delete the wire at `SmartRuntime.cs:954` or wire the classifier output.
- **Sources**: Sweep-1 (round 2)
- **Status**: OPEN

### BUG-064 — PretrainingPipeline BaselineMetadata constructed but never persisted
- **Severity**: MINOR
- **Category**: Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Training/PretrainingPipeline.cs:85`
- **Description**: TODO "write the BaselineMetadata footer as an extension to the binary format." `Run` constructs `BaselineMetadata { … HyperParameterVector = … }`, returns it to caller, but it's never persisted to disk. Baseline file only contains model params via `ProfilePersistence.Save`. Any consumer reading the baseline file gets weights but no provenance (CMA seed, generation count, score weights).
- **Sources**: Sweep-1 (round 2)
- **Status**: OPEN

### BUG-065 — CapabilityFilters.FromMobility silently classifies Walker techs as Wheel
- **Severity**: MINOR
- **Category**: Stub
- **Location**: `TAC_AI/AI/Forms/Smart/Pathing/CapabilityFilters.cs:48`
- **Description**: `else cls = VehicleClass.Wheel; // Walker classification needs leg-block detection (TODO v0.2)`. `VehicleClass.Walker` exists as an enum value but classification path never assigns it. Pathing treats walker techs as wheeled — slope/climb tolerances inappropriate. ClimbAngleMax already comes from MobilityProfile but VehicleClass-keyed CapabilityRelevance switches use Wheel cone, not a leg-arc cone.
- **Sources**: Sweep-2 (round 2)
- **Status**: OPEN

### BUG-066 — `_pendingActionState` per-tick deferral races AcceptingTrainingEvents flip
- **Severity**: MINOR
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/Control/ContinuousController.cs:444-518` (PublishActionValueEvent) + `LearningService.cs:236-242`
- **Description**: `PublishActionValueEvent` early-returns when `!AcceptingTrainingEvents` but doesn't reset `_pendingActionValid` to false. When the gate later re-opens, stale `_pendingActionState` from before the gate closed is enqueued as the (s, a, r) of a tuple whose s' came from a post-resume tick — potentially gigaseconds later. (s, a, r, s') tuple incoherent across pause/host-handover boundary.
- **Impact**: 1-2 spurious training tuples per pause/resume per tech with mismatched s/s'. Trainer rejects NaN, but injects high-magnitude TD-error noise.
- **Sources**: Sweep-3 (round 2)
- **Status**: OPEN

### BUG-067 — AIEAutoPather2D/3D iterateAround4 instance field should be static readonly
- **Severity**: MINOR
- **Category**: Lifecycle (memory)
- **Location**: `TAC_AI/AI/Forms/Modified/Pathing/AIEAutoPather2D.cs:116` + `AIEAutoPather3D.cs:117`
- **Description**: `private List<IntVector2> iterateAround4 = IterateAroundExpand(6);` — instance field initialized once per pather. `FindIdealStart` is the only reader. 6-rad expansion produces 169-entry list kept alive on every pather instance for the pather's lifetime. Should be `private static readonly`.
- **Impact**: Modest memory waste per pather (~169 IntVector2 × per pather × per FixedUpdate window count); not a hot path.
- **Sources**: Sweep-6 (round 2)
- **Status**: OPEN

### BUG-068 — SmartEventBridge.DetachPerTank Unity-dead tank ref reads .Weapons via field access
- **Severity**: ~~MINOR~~ (CLOSED round 4 polish — DESIGN-INTENT)
- **Category**: Broken
- **Location**: ~~`TAC_AI/AI/Forms/Smart/Integration/SmartEventBridge.cs:256-257`~~
- **Status**: REFUTED (round 4 — DESIGN-INTENT closure after 2 rounds NEEDS-VERIFY; try/catch wraps the path and docstring matches "best-effort" semantics). See §5.

### BUG-069 — RGeneral.Monitor target.tank race window between SetPursuit and Team read
- **Severity**: MINOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Forms/Modified/RGeneral.cs:298-306`
- **Description**: `Monitor` calls `helper.SetPursuit(helper.lastEnemyGet)` then reads `helper.lastEnemyGet.tank.Team` for `ManBaseTeams.IsEnemy`. Between SetPursuit and `:304`, `.tank` can be destroyed. Same class as BUG-016. Lower confidence than the others.
- **Sources**: Sweep-5 (round 2)
- **Status**: NEEDS-VERIFY

### BUG-070 — ModifiedTargeting cross-team mutation on hot allied path (extension of BUG-021)
- **Severity**: ~~MINOR~~ (MERGED round 4 polish — duplicate file:line as BUG-021)
- **Category**: MP
- **Status**: REFUTED-MERGED (round 4 — same `ModifiedTargeting.cs:96` cite as BUG-021's third bullet; "hot allied path" narrative absorbed into BUG-021's impact section). See §5.

### BUG-071 — StrategicStateExtractor `nowMono` sampled once before per-tech iteration loop
- **Severity**: MINOR
- **Category**: Broken (doc-vs-behavior, no observable impact)
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/Features/StrategicStateExtractor.cs:159, 210-211, 266-267`
- **Description**: `nowMono = MonoClock.Now()` is sampled once at `:159` and reused for every per-tech vector in the tick. Per-tech window therefore uses tick-start time, not per-tech time. For a 30-tech team this is at most ~1ms drift; effectively no impact, but the comment at `:159` implies fresh per-tech sampling.
- **Impact**: None observable. Flag for doc-vs-behavior clarity only.
- **Sources**: Sweep-3 (round 2)
- **Status**: OPEN

## §3c. MAJORs (round 3 additions)

### BUG-072 — ThreatAssessmentModel slot 30 train/infer feature-definition mismatch
- **Severity**: MAJOR
- **Category**: Broken
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/Features/StrategicStateExtractor.cs:691-700` (inference path — extractor per-tick vector) vs `TAC_AI/AI/Forms/Smart/Learning/LearningService.cs:707-711` (training path — OnDamageObserved producer)
- **Description**: `ThreatSlots.VictimWeakFaceTowardAttackerDot` is computed two different ways. Extractor's per-tick vector fills the slot as `Dot(victimProbe.WeakFaceNormalWorld, -selfProbe.ForwardWorld)` — using attacker's negative-forward as a proxy for LOS. `OnDamageObserved` (which produces training labels via `BuildThreatEventFeatures`) fills the SAME slot as `Dot(victimProbe.WeakFaceNormalWorld, -losDir)` where `losDir = (victimPos - attackerPos)/dist`. Attacker's forward direction is its facing, not the geometric attacker→victim ray; the two are only equivalent when attackers are perfectly facing their targets.
- **Impact**: ThreatAssessmentModel trains on geometric LOS (correct semantics) but infers on attacker-facing (incorrect proxy). The weak-face-exposure signal the model SEES at inference is a different distribution from what it LEARNED to map. Model never converges on a meaningful weak-face signal — silent feature/training distribution shift.
- **Sources**: Sweep-3 (round 3)
- **Status**: OPEN

### BUG-073 — SmartForm.DeInitGlobal partial teardown leaks per-tank cargo + weapon-fire subscriptions
- **Severity**: MAJOR
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/SmartForm.cs:96` (DeInitGlobal invokes SmartEventBridge.Uninstall) → `SmartForm.cs:128` (then SmartRuntime.Shutdown) vs `SmartRuntime.cs:1163` (CargoStatePublisher.Unwire) + `:1166` (WeaponFires?.Clear)
- **Description**: DeInitGlobal invokes `Integration.SmartEventBridge.Uninstall` BEFORE `SmartRuntime.Shutdown`. Uninstall handles damage subs only (see BUG-053). SmartRuntime.Shutdown then calls the static event-bus `CargoStatePublisher.Unwire()` but never iterates still-attached techs to call per-tank `CargoState.DetachPerTank(tank)`; `WeaponFires?.Clear` drops closures without per-tank unsubscribe (compounds BUG-052). Every tech still alive at Shutdown leaks per-tank cargo + weapon-fire subscriptions; `ManLootHolder.PickupEvent`/`ReleaseEvent` + `TechWeapon.WeaponsFiredEvent` walk dead closures until engine purges the tech.
- **Impact**: Form-swap leak across Init/Shutdown cycles — every surviving tech's per-tank event surface stays bound to dead closure state. Compounds with BUG-052 / BUG-053.
- **Sources**: Sweep-7 (round 3)
- **Status**: OPEN

### BUG-074 — ApplyProfileSafe's `Try` graceful-degradation helper is dead code on the host-handover hot path
- **Severity**: MAJOR
- **Category**: Stub
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/LearningService.cs:249` (declaration) + `:271` (call site that bypasses it)
- **Description**: ApplyProfileSafe declares local helper `void Try(string name, ILearnedModel model, byte[] weights)` documented at `:202-204` as the per-model arch-mismatch graceful degradation primitive ("for each model, wrap LoadParameters in try/catch"). Zero call sites — body just delegates `try { ApplyProfile(profile); }` at `:271`. The section-level catch at `ApplyProfile :502-504` covers the symptom (per-section try/catch within ApplyProfile loop), but the documented per-model OnHostGained partial-tolerance path the docstring promises never runs. Overlaps with BUG-042's dead-method cluster, but this one specifically sits on the host-handover hot path where partial-tolerance matters most.
- **Impact**: On OnHostGained, an arch-mismatch on any single model causes the section-level catch to skip that section but leaves no documented per-model log-and-recover; subsequent host operates with a half-loaded profile silently. The "wrap LoadParameters in try/catch" contract is unhonored.
- **Sources**: Sweep-9 (round 3 — sharpened framing of BUG-042's cluster)
- **Status**: OPEN

### BUG-075 — Baseline tier of the load-fallback chain is unreachable
- **Severity**: MAJOR
- **Category**: Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ProfilePersistence.cs:186-191` (chain doc) + `:204-207` (baselineBytes read) vs all 4 callers
- **Description**: ProfilePersistence documents a four-tier load-fallback chain (`current → .previous → .penultimate → embedded baseline → null`). The baseline tier reads `baselineBytes` at `:204-207`; ALL 4 callers (`LearningService.cs:219`, `:347`, `ProfileSelfTest.cs:44`, `SnapshotManager.cs:70`) pass `baselineBytes: null`. `PretrainingPipeline` writes a baseline file (see BUG-006 / BUG-061) but no code path ever reads it back. `LoadTier.Baseline` is never observed in the field.
- **Impact**: Documented four-tier fallback is really three-tier. After `.current` + `.previous` + `.penultimate` all corrupt (e.g., post-BUG-058 backup-ring poisoning), the chain falls through to null instead of recovering from the baseline file the pretraining pipeline produced. Disaster-recovery promise unfulfilled.
- **Sources**: Sweep-9 (round 3)
- **Status**: OPEN

### BUG-076 — Save rotation burns backup generations on transient writer fault
- **Severity**: MAJOR
- **Category**: Save-Load / Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ProfilePersistence.cs:74-98` (rotation block runs BEFORE MemoryStream build at `:100-148`)
- **Description**: `Save` rotates `previous → penultimate` then `current → previous` BEFORE building the new save's MemoryStream. If the writer block (`:101-148`) throws after rotation — e.g., StoreParameters NRE because `LearningService.Shutdown` nulled Intent at `:327-330` between QuitSaveCoordinator capture (QuitSaveCoordinator.cs:84-86) and Save iteration — rotation already happened but no new file was written. Two consecutive failed Saves cost two backup generations: the original `.penultimate` (likely the only intact pre-bug backup) is gone, replaced by what was `.previous`, which was already shifted by the first failure. Compounds BUG-058's MP ring-corruption with a pure-SP failure mode.
- **Impact**: Disaster-recovery ring degenerates under any writer-thread fault. A single Save() exception class (NRE / IOException / OOM) burns two backup tiers per occurrence. Round-3 hypothesis matches an actual failure path: post-Shutdown null-Intent → StoreParameters NRE during a still-pending QuitSaveCoordinator flush.
- **Sources**: Sweep-9 (round 3)
- **Status**: OPEN

### BUG-077 — ProfilePersistence Save writes models.Length sections; Load reads exactly 4 (hardcoded)
- **Severity**: MAJOR
- **Category**: Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/ProfilePersistence.cs:108` (Save iterates `i < models.Length`) vs `:269` (Load reads `for (int i = 0; i < 4; i++)`)
- **Description**: Save iterates the caller-supplied `ILearnedModel[]` length; Load hardcodes 4. A future partial-flush Save (e.g., 3-model intermediate snapshot) decodes with CRC OK over the short body, then hits EndOfStream in the section loop → classified `parse:EndOfStreamException` → PreserveCorrupt + fallthrough to `.previous`. The hardcoded 4 also means a 5th section (e.g., BaselineMetadata footer per BUG-064 or a 5th model added by future expansion) cannot ship without a parallel Load constant bump — silent symmetry-breaking design constraint that no compile error or runtime assertion enforces.
- **Impact**: (a) Any future partial-Save corrupts the load chain. (b) Adding the BUG-064 BaselineMetadata footer triggers EOF-classified-as-corrupt on every load. The hardcoded 4 must be replaced with a section-count header or `while (br.PeekChar() != -1)` loop.
- **Sources**: Sweep-9 (round 3)
- **Status**: OPEN

### BUG-078 — AutosaveWorker.TickOnce host-loss race (SaveProfile call has no re-check)
- **Severity**: MAJOR
- **Category**: MP / Save-Load
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/AutosaveWorker.cs:65-85` (TickOnce body) — host check at `:43` is OUTSIDE the call path
- **Description**: AutosaveWorker.TickOnce calls `LearningService.SaveProfile(...)` at `:76` without re-checking `SmartRuntime.IsHost`. The outer RunLoop's IsHost gate at `:43` is racy against a host-loss event that lands between the gate evaluation and the SaveProfile call. Tight window (~30s tick interval) but identical class to BUG-018/019/055; trips the same backup-ring corruption rotation as BUG-058. The pattern matches every other SaveProfile site enumerated under BUG-055.
- **Impact**: A former-host that lost authority between tick boundaries saves a now-stale profile, immediately consuming a backup-ring rotation. Closing this at the LearningService.SaveProfile entry (BUG-055) closes it everywhere including here.
- **Sources**: Sweep-7 (round 3)
- **Status**: OPEN

## §4d. MINORs (round 3 additions)

### BUG-079 — ThreatAssessmentModel features bypass StrategicStateBuffer (plan-contract violation)
- **Severity**: MINOR
- **Category**: Stale doc / integration
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/LearningService.cs:577-749` (`BuildThreatEventFeatures`) vs `Docs/FEATURE-EXPANSION-PLAN.md:701`
- **Description**: Feature-expansion plan §8 explicitly states `LearningService.cs:694 reads attacker-side StrategicStateBuffer slots`. Actual `BuildThreatEventFeatures` never calls `SmartRuntime.StrategicVectors.TryRead` — it independently re-derives features from `SmartRuntime.LookupSelfStateProbe` + `world.GetPerTechBuffer` + `PathingService.CurrentTerrain`. The Threat trainer's training features are NOT sourced from the daemon's atomic publish; they are re-derived on the DamageObserved consumer thread.
- **Impact**: Doc contract violated. No current behavior bug because both paths read the same upstreams, but if the daemon's published vector ever diverges from a re-derivation (e.g., a future probe normalization change), Threat training silently disagrees with inference — and the BUG-072 train/infer mismatch makes this concrete.
- **Sources**: Sweep-1 (round 3)
- **Status**: OPEN

### BUG-080 — WorkerLifecycleRegistry._live never cleared; stale handles can false-positive watchdog liveness checks
- **Severity**: MINOR
- **Category**: Lifecycle
- **Location**: `TAC_AI/AI/Forms/Smart/Threading/WorkerLifecycleRegistry.cs:36` (`_live` field) + `:106-176` (CancelAllAndJoin) + `:92` (SnapshotLive) + `DaemonWatchdog.cs:130-138` (IsAliveByLabel prefix matcher)
- **Description**: `_live` is a `private static readonly List<WorkerHandle>` that is never cleared. `CancelAllAndJoin` logs stragglers at `:163-167` but doesn't remove them; `_cancelAllInProgress` resets in finally at `:176`, but the orphaned `WorkerHandle` (and its CancellationTokenSource) lives forever. Across Init→Shutdown cycles with stragglers, the list accumulates linearly; on next Init, `SnapshotLive()` returns stale handles, and `DaemonWatchdog.IsAliveByLabel` matches by `"SmartLR-<label>-"` prefix — a stale straggler can false-positive a missing canonical as alive, suppressing real respawns.
- **Impact**: Long-session degradation: after a few Shutdown cycles with stragglers, DaemonWatchdog can decide that a missing canonical daemon is "alive" via stale handle prefix-match and skip the respawn that would have recovered it.
- **Sources**: Sweep-7 (round 3)
- **Status**: OPEN

### BUG-081 — StrategicStateExtractor diagnostic counter mixes tech-count and tick-count units in same log line
- **Severity**: MINOR
- **Category**: Broken (logspam)
- **Location**: `TAC_AI/AI/Forms/Smart/Learning/Features/StrategicStateExtractor.cs:174-178` (call) + `:754-779` (Accumulate body)
- **Description**: `AccumulateTickDiagnostics` is called with `skippedTerrain=1` when the WHOLE tick is skipped, but the accumulator does `_windowSkippedTerrainStale += skippedTerrain` at `:762` — accumulating a tick-count. The diag log emits `"terrainStale={4}"` adjacent to `published={3}` (tech-count) and `noProbe={5}` (also tech-count). Mixed units in the same line; operators reading the log misinterpret terrainStale as a tech-count and over-report stale-terrain incidents by the per-tick tech multiplier.
- **Impact**: Diagnostic confusion only. No runtime behavior impact.
- **Sources**: Sweep-3 (round 3)
- **Status**: OPEN

## §5. Refuted / withdrawn / out-of-scope

### BUG-013 (round 2 — severity downgraded MAJOR → MINOR; not refuted)
- **Round 2 (Sweep-1, Sweep-3, Sweep-7)**: TerrainMap ctor publishes a fully-constructed-but-empty cache; worker observes either old or new, never half-published. Round-1 framing of "underground-then-above-ground trajectories" overstates impact. Actual symptom: transient flat-y bias during freshly-allocated window. Memory notes do not flag world-reset crashes. Severity moved to MINOR; root cause restated; finding kept OPEN. See BUG-050 for the authored-but-unwired TerrainPublication fix.

### BUG-027 (REFUTED round 2 — file deleted)
- **Round 2 (Sweep-2)**: TeamBelief.cs has been deleted per REV 7 P5 Item 22. Grep on `class TeamBelief` returns zero `.cs` hits (only doc reference in V0.2-PLAN-REV4.md to `TeamBeliefPolicy`, a different name). `LineOfSight.cs:10` explicitly cites "the old `TeamBelief.cs`" as removed; LOS aggregation lives in `SmartRuntime.cs:401-429`. Cited file:line `Coordination/TeamBelief.cs:58-63` no longer exists. REFUTED.

### BUG-044 (REFUTED round 2 — design-intent gate is intentional)
- **Round 2 (Sweep-4 strong, Sweep-8 supporting)**: TechLeakWatchdog's Sweep doc (lines 75-80) explicitly states design intent: "any sidecar key outside this union is by definition a leak from Smart's POV regardless of engine state." Sidecars are per-process, populated by `SmartRuntime.RegisterTech` / sidecar events; on a non-host the registration paths still run for local techs. Skipping the host-gate is intentional — leak detection is per-process, not per-MP-authority. Adding a host-gate would suppress legitimate client-process leak detection. REFUTED.

### BUG-045 (REFUTED round 2 — lazy-alloc invariant verified, folded into BUG-042)
- **Round 2 (Sweep-3, Sweep-7)**: StrategicStateExtractor.OnTechSpawn IS declared optional/lazy per the docstring; the equivalent function on the underlying `StrategicStateBuffer` IS wired via `SmartRuntime.cs:516`. Both StrategicStateBuffer.OnTechSpawn (line 44-47) and Publish (line 33) use GetOrAdd, so lazy path works. No behavior regression from missing wiring. Folded into BUG-042's dead-method cluster — no longer tracked separately.

### BUG-031 (CLOSED round 4 polish — DESIGN-INTENT)
- **Round 4 (polish-pass adjudication of 3-round NEEDS-VERIFY backlog)**: SmartMovementController Drive* no-ops have remained NEEDS-VERIFY across rounds 1, 2, and 3. Round-3 Sweep-1 explicitly looked for external `EControlCoreSet` readers and found none; no Harmony patch, no debug/UI consumer, no MP-sync reader. The "v1.3: no-op" comment + "Smart MPC writes directly" design contract therefore holds in-tree. If a future external mod adds a reader, this re-opens. Closed as DESIGN-INTENT.

### BUG-061 (MERGED round 4 polish — into BUG-006)
- **Round 4 (polish-pass dedupe)**: BUG-061's description itself ("Sharper restatement of BUG-006's PretrainingPipeline half") admitted the overlap. Both cite the same PretrainingPipeline.Run docstring vs body contradiction; BUG-006 absorbed BUG-061's specific "no second instance, no parameter-averaging primitive" framing into its description. Lower-numbered ID retained per dedupe policy.

### BUG-068 (CLOSED round 4 polish — DESIGN-INTENT)
- **Round 4 (polish-pass adjudication)**: SmartEventBridge.DetachPerTank's orphan-path read of `tank.Weapons` after engine purge IS wrapped in the try/catch at `:253-259`; bug entry itself documented "path documented as 'engine-purged ref — best-effort', so functionally OK." Round-3 Sweep-2 confirmed no test or runtime path observes the MissingReferenceException as a user-visible failure. Adding a Unity-null check pre-`tank.Weapons` is a hardening polish, not a bug fix. Closed as DESIGN-INTENT.

### BUG-070 (MERGED round 4 polish — into BUG-021)
- **Round 4 (polish-pass dedupe)**: BUG-070 cites the EXACT same file:line as BUG-021's third bullet (`ModifiedTargeting.cs:96`). Verified in source — the cited `ManBaseTeams.DegradeRelations` call on the manual-target branch is the third of BUG-021's three call sites, not a separate offender. BUG-070's only added value was the frequency framing ("hot allied path" vs "enemy AI tick"), now absorbed into BUG-021's Evidence/Impact sections. Lower-numbered ID retained per dedupe policy.

### Out-of-scope / withdrawn round-2 candidate findings

- **Sweep-3 candidate BUG-048 (`nowMono` per-tick reuse)** — kept as BUG-071 MINOR for doc-vs-behavior clarity but agent's own conclusion: no observable impact.
- **Sweep-4 candidate `_pausedAtTickMs` non-volatile** — withdrawn by Sweep-4 in-line (single-threaded access only, benign).
- **Sweep-6 candidate BUG-048 (`_lastRefreshEnvMs` reset)** — withdrawn by Sweep-6 in-line (non-finding after second look — old TerrainMap GC'd, new starts at int.MinValue, behavior correct).
- **Sweep-10 candidate BUG-049 (TunableOptionsPublisher.cs missing from csproj)** — withdrawn (Sweep-10 re-verified file IS in csproj at line 441; false alarm).
- **Memory note "AIEPathMapper.tilesMapped concurrency"** — DISPUTED by Sweep-6: AIEPathMapper is MonoBehaviour, mutators only run from FixedUpdate/Update/OnGUI; Smart-side PathingService worker never touches it. Dictionary is correct. Memory note wrong.
- **Memory note "AIEAutoPather2D/3D static scratch lists not [ThreadStatic]"** — DISPUTED by Sweep-6: CalcRoute is driven exclusively by AIEPathMapper.HandlePathingRequests (FixedUpdate); single-threaded. [ThreadStatic] overkill.
- **Sweep-4 dispute of BUG-035 partial-overstatement** — kept BUG-035 OPEN but noted impact narrative: of the 11 cited fields, only `GradientSteps` is actually written at runtime (TrainingMatch.cs:318 CMA-ES tuner). Other 10 never assigned. "Half-applied tuner sweep" scenario does not exist; concern reduces to single-int torn-write.
- **Sweep-4 dispute of BUG-043 `[WORKER-ABORT-GUARD-BUG]` cite** — agreed: AbortGuard.cs:94 is a defensive log tag, not an unfixed bug marker. Cite removed from BUG-043 implicit scope.

## §6. Audit trail

| Round | Sweep agents | Findings added | Refuted | Promoted (NEEDS-VERIFY→OPEN) | Severity changed |
|---|---|---|---|---|---|
| 1 | 10 | 45 (38 unique, 7 grouped) | 0 | n/a | n/a |
| 2 | 10 | 26 new (BUG-046..BUG-071) + 2 BLOCKER omissions folded into BUG-001 | 3 (BUG-027, BUG-044, BUG-045) | 2 (BUG-003, BUG-038) | 1 (BUG-013 MAJOR→MINOR) |
| 3 | 10 | 10 new (BUG-072..BUG-081 — 7 MAJOR, 3 MINOR) + 1 BLOCKER omission folded into BUG-001 (`EBasePurpose.cs`) + BUG-009 narrative sharpened + BUG-042 cluster reduced (EBasePurpose.cs removed; reclassified as BLOCKER) + BUG-055 callsite enumeration + BUG-001 footprint updated to 6 files | 0 | 0 | 0 |
| 4 (polish) | 1 | 0 new; 2 dedupes (BUG-061→BUG-006, BUG-070→BUG-021); 2 NEEDS-VERIFY closures (BUG-031, BUG-068 → DESIGN-INTENT); cite spot-checks (BUG-003, BUG-004, BUG-013 — verified verbatim); §1 summary counts corrected (round-3 table claimed MAJOR 39 / MINOR 28 / Total 71 — actual was 42/28/74 active before polish; post-polish active is 4 BLOCKER + 42 MAJOR + 28 MINOR = 74); §4b mislabel fixed (BUG-046..061 moved under new §3b MAJORs header; round-3 MAJORs renumbered §4c→§3c) | 4 (BUG-031, BUG-061, BUG-068, BUG-070) | 0 | 0 |

### Round-3 dedupe / Polish-phase flags

The following round-3 agent findings overlap existing entries and were folded rather than numbered:
- Agent 1's BUG-073 (PathingService.OnWorldReset terrain race + missing re-Register) — overlaps BUG-013 / BUG-050 / BUG-051 (double-clear + race window). Flagged as part of BUG-050's "authored fix unwired" cluster.
- Agent 1's BUG-074 (StrategicVectors slot pre-alloc before SelfStateProbe wired) — cosmetic diag-counter inflation only; not numbered.
- Agent 2's BUG-072 (TankAIHelper.cs:1018 second-if guard) — WITHDRAWN by agent on re-read.
- Agent 2's BUG-073 (Residual zero-fill regression framing) — folded into BUG-009 narrative sharpening.
- Agent 2's BUG-074 (StrategicStateExtractor.OnTechSpawn forwarder no callers) — already covered by BUG-042 cluster.
- Agent 3's BUG-074 (TeamRuntime.DeregisterTech doesn't call StrategicVectors.OnTechRecycle) — cosmetic pairing-only; team-change preserves vector by design.
- Agent 3's BUG-075 (OneSecondTicks static-init order vs MonoClock.TickFreq) — theoretical; no caller path reaches the precondition.
- Agent 6's NEW-PATH-1 (SolvedAtMono non-volatile) — Agent 6 self-resolved as benign (ConcurrentDictionary lock flushes the prior write).
- Agent 6's NEW-PATH-2 (PathingService Init lambda re-registration log-spam scaling) — already covered as a consequence of BUG-051.
- Agent 6's NEW-PATH-3 (OnWorldReset early-return on !IsRunning) — idempotent, no crash; not numbered.
- Agent 7's NEW-3.3 (`_teamGenerationCounter` unbounded) — long wraparound impossible in any session; cosmetic.
- Agent 9's NEW-76 (SnapshotManager.cs:80 SaveProfile no host gate) — folded into BUG-055's callsite enumeration (5th site).

### Polish-phase candidates (ambiguous severity, dedupe pressure)

- **BUG-029** (`Tune.Float/Int/Bool` default-handle NRE) — round-1 framed as MINOR / no active caller; first future caller forgetting `IsValid` crashes. Severity could rise to MAJOR depending on whether the registry is exposed to mods. **Round 4: held MINOR — no in-tree caller path observed; mod-API exposure is hypothetical.**
- ~~**BUG-031**~~ — **Round 4: CLOSED as DESIGN-INTENT (see §5).**
- ~~**BUG-068**~~ — **Round 4: CLOSED as DESIGN-INTENT (see §5).**
- **BUG-035** (TrajectoryOptimizer mutable static tuning fields) — round-2 narrative narrowed to "single int (GradientSteps) actually written"; severity could drop to "code smell" — 10 of 11 fields are unused-public-static. **Round 4: held MINOR — torn single-int write is a real concern, even if narrow.**
- **BUG-046 / BUG-022 / BUG-047** are a logically-coupled cluster (Adam state + SaveMutex coverage across both save and load); consider merging in a future audit. **Round 4: held separate — three distinct fix sites, cluster note retained for triage.**
- **BUG-052 / BUG-053 / BUG-073** form a single per-tank-subscription-leak cluster (WeaponFireBuffer.Clear + SmartEventBridge.Uninstall + SmartForm.DeInitGlobal); single coordinated fix at SmartEventBridge.Uninstall plus DetachAll() in WeaponFireBuffer would close all three. **Round 4: held separate — distinct code surfaces; cluster note retained.**
- **BUG-018 / BUG-019 / BUG-054 / BUG-055 / BUG-078** all close at a single fix site (IsHost gate at LearningService.SaveProfile entry). **Round 4: held separate — each cites a distinct entry-path call site; BUG-055 is the root, others are caller-side evidence of how widespread the unguarded entry is.**

### Round-4 polish summary

- Active findings (OPEN + NEEDS-VERIFY): **74** (4 BLOCKER + 42 MAJOR + 28 MINOR)
- Refuted / merged / design-intent (preserved in §5 as historical stubs): **7** (BUG-027, BUG-031, BUG-044, BUG-045, BUG-061, BUG-068, BUG-070)
- NEEDS-VERIFY (remaining after polish): **1** (BUG-069 — lower-confidence target.tank race)
- Status-table total (active + REFUTED stubs): **81** unique BUG IDs from BUG-001 through BUG-081
- Bug list is ready for final validation.

