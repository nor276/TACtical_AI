# 21 - Timing & Cadence Register

> **Category:** Infrastructure (cross-cutting)
> **Scope:** Master clocks + **all 20 pipelines**. Built by the pilot (7 agents on pipelines 06/07/08) plus a full extension sweep (43 agents: 3 per cadence-bearing pipeline, 1 per structural pipeline), merged with every contested value re-verified against source.

## Summary

The control-flow docs (01-20) map *what calls what*; this doc maps *how often, on which clock, and in what unit*. Oscillation, flicker, "twitch", and MP-vs-SP behaviour drift live in the temporal dimension those docs omit.

**Two things dominate everything below:**

1. **There are four clock families**, not one (§1). A value's real-time meaning depends entirely on which clock steps it.
2. **Refresh values come in incompatible units.** A value is **SECONDS** (gated on `Time.time`/`Time.deltaTime`), or a **tick counter** that is either **AIClockPeriod-INVARIANT** (stepped `±AIClockPeriod` per Operations pass — real-time fixed when the period changes) or **AIClockPeriod/role/framerate-DEPENDENT** (stepped `±1`/`++`/`-=AIDodgeCheapness` per pass, or per render frame — real-time drifts with SP↔MP, framerate, or which pass drives it). **The DEPENDENT counters are the single biggest source of "works in SP, breaks in MP" surprises**, and they are where most candidate bugs in §7 cluster.

Normalized figures assume **single-player** (`AIClockPeriod = 10`) and **physics timestep 0.02s (50 Hz)** unless noted. MP (`AIClockPeriod = 30`) and the render-frame Maintainer (~30-60 Hz) are called out where they change the answer.

---

## 1. Clock families

| Clock | Driver | Cadence (SP) | Cadence (MP) | Runs here |
|---|---|---|---|---|
| **Pre/Post** | `TankAIManager.FixedUpdate`, unstaggered ([TankAIManager.cs:815](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIManager.cs#L815)) | every FU = **0.02s / 50 Hz** | 0.02s / 50 Hz | `OnPreUpdate`/`OnPostUpdate`: `actionPause` decrement, `EstTopSped`, alignment latch |
| **Directors** | `FixedUpdate`, staggered by `AIDodgeCheapness=20` ([TankAIManager.cs:699](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIManager.cs#L699)) | 20 FU = **0.4s / 2.5 Hz** | 0.4s / 2.5 Hz (unchanged) | `DriveDirector`, `WeaponDirector`, pathing, anchor counters (RTS-navi) |
| **Operations** | `FixedUpdate`, staggered by `AIClockPeriod=10/30` ([TankAIManager.cs:711](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIManager.cs#L711)) | 10 FU = **0.2s / 5 Hz** | 30 FU = **0.6s / 1.67 Hz** | `CheckEnemyAndAiming`, `FindEnemy`, `TryHandleObstruction`, ops dispatch, repair stepper, Provoke decay |
| **Maintainer** | vanilla `TankControl.Update → ModuleTechController.ExecuteControl → ControlTech` (**render frame**) ([AIECore.cs:20](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIECore.cs#L20)) | per render frame = **~30-60 Hz** | ~30-60 Hz | `DriveMaintainer`, `WeaponMaintainer`, `BeamMaintainer`, dive FSM, `BeamTimeoutClock` |
| **Strategic (unloaded)** | `ManEnemyWorld.FixedUpdate` — *its own* MonoBehaviour ([ManEnemyWorld.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManEnemyWorld.cs)) | `OperatorTickDelay`=**4s** (0.25 Hz) strategic; `MaintainerTickDelay`=**0.5s** (2 Hz) micro-motion | same (Time.time-gated) | NP_Presence sim, tile spawn/recycle, EBU/EMU economy |
| **Team economy** | `BaseFunderManager.FixedUpdate` ([RLoadedBases.cs:372](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RLoadedBases.cs#L372)) | `EnemyTeamAwarenessUpdateDelay`=**6s** (0.167 Hz) | same | team relations, anger decay, loaded-base build/expand |

Plus one-shot/diagnostic schedulers: boot's `EmitHealthPulse` 10s `InvokeSingleRepeat` ([KickStart.cs:733](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/KickStart.cs#L733)), MP `PushTeamDeltasToClients` 1 Hz, siege cooldown in `ManEnemySiege.Update`.

> ✅ **Resolved (was an open ⚠ in the pilot):** the Maintainer is **render-frame** `Update`, confirmed by the call-chain comment at [AIECore.cs:20](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIECore.cs#L20) and corroborated by `ControlOperatorAgeFrames` using `Time.frameCount`. Maintainer-clocked durations (`BeamTimeoutClock`, dive FSM holds) are therefore framerate-dependent.

**Stagger math:** each accumulator adds `helpersActive.Count / divisor` per FixedUpdate and dispatches `floor()`, carrying the remainder — so each helper gets one Directors pass per `AIDodgeCheapness` FU and one Operations pass per `AIClockPeriod` FU, independent of population.

---

## 2. Cadence register

Grouped by unit class (the load-bearing distinction). SP / 50 Hz normalization.

### 2A — Seconds-gated (`Time.time`/`Time.deltaTime`; framerate-independent)

| Name | Decl | Value | Hz | Clock | Gates | Pipelines |
|---|---|---|---|---|---|---|
| `TargetCacheRefreshInterval` / `…Combat` | [AIGlobals.cs:172](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L172)/[175](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L175) | 1.5s / 0.4s | 0.67 / 2.5 | Operations | enemy scan cache (idle/combat) | 04,05,06 |
| `TargetValidationDelay` | [AIGlobals.cs:374](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L374) | 0.6s | 1.67 | Operations | LOS raycast + range purge + LOS streak | 04,05,06,12 |
| `ScanDelay` | [AIGlobals.cs:395](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L395) | 0.5s | 2.0 | Operations | `NextFindTargetTime` re-scan / LollyGag heartbeat | 04,05,06 |
| `PestererSwitchDelay` | [AIGlobals.cs:396](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L396) | 12.5s | 0.08 | Operations | Random-mode target reroll | 06 |
| `LOSLostGraceTime` | [AIGlobals.cs:384](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L384) | 3.0s | — | Operations | hold target behind cover before EndPursuit | 06 |
| `EnemyInitGrace` | [AIGlobals.cs:302](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L302) | 1.0s | — | event | accept late blocks for AbortSelfDestruct after spawn | 02,05 |
| `AISubscribeDelay` | [AIGlobals.cs:297](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L297) | 0.1s (×5 retry) | — | Unity Invoke | defer DelayedSubscribe until bounds settle | 02,11 |
| `EnemyTeamAwarenessUpdateDelay` | [AIGlobals.cs:414](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L414) | 6.0s | 0.167 | Team economy | UpdateTeams: anger decay, retreat clear, build/expand | 14,16 |
| `OperatorTickDelay` | [ManEnemyWorld.cs:35](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManEnemyWorld.cs#L35) | 4.0s | 0.25 | Strategic | unloaded strategic per-team tick | 15,16,17 |
| `MaintainerTickDelay` | [ManEnemyWorld.cs:41](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManEnemyWorld.cs#L41) | 0.5s | 2.0 | Strategic | unloaded micro-motion / queued-move step | 15 |
| `WasInCombat` window | [ManEnemyWorld.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManEnemyWorld.cs) | 8.0s (2×Operator) | — | Strategic | strategic "recently fighting" hold | 15 |
| `CheckEngines` re-survey | [AIControllerAir.cs:126](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/Movement/AIControllerAir.cs#L126) | 1.0s | 1.0 | Maintainer | rate-limit prop/boost geometry re-eval | 10,11 |
| `MinRecoverHold` / `MaxRecoverHold` | [AIGlobals.cs:469](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L469)/[474](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L474) | 1.5s / 8.0s | — | Maintainer | dive-FSM Recover dwell floor / escape | 10 |
| `LeadPredictionMaxTOF` | [AIGlobals.cs:167](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L167) | 3.0s | — | Maintainer | cap aim-lead time-of-flight | 12 |
| `BlockAttachDelay` | [AIGlobals.cs:365](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L365) | 0.75s | — | Unity Invoke | block placement animation before attach | 20 |
| `AirSpawnInterval` (effective) | [SpecialAISpawner.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Templates/SpecialAISpawner.cs) | 30s default → ~60/rate after config | — | `SpecialAISpawner.Update` (deltaTime) | airborne spawn attempt window | 17 |
| `RaidCooldownTimeSecs` (half) | [AIGlobals.cs:484](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L484) | 1200s (600s) | — | `ManEnemySiege.Update` | re-siege cooldown; halved after win / on load | 17,19,03 |
| `EmitHealthPulse` | [KickStart.cs:733](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/KickStart.cs#L733) | 10s repeat | 0.1 | InvokeSingleRepeat | diagnostic helper/team/spawn summary | 01,15 |
| `PushTeamDeltasToClients` | [ManBaseTeams.cs:1474](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManBaseTeams.cs#L1474) | 1.0s (MP) | 1.0 | InvokeSingleRepeat | flush batched team deltas (≤200/packet) | 19,14 |
| `AttackComplainPlayer` throttle | [ManBaseTeams.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManBaseTeams.cs) | 2.0s | 0.5 | Team economy | global (not per-team) complaint popup | 14 |
| `SLDBeforeBuilding` / `DelayBetweenBuilding` | [AIGlobals.cs:486](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L486)/[487](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L487) | 90s / 30s | — | Team economy | loaded-base first-build hold / build cadence (±6s jitter) | 14,16 |

### 2B — Tick counters, **AIClockPeriod-INVARIANT** (stepped `±AIClockPeriod` per Operations pass; SP=MP real-time)

| Name | Decl | Raw | Norm. | Step site | Gates | Pipelines |
|---|---|---|---|---|---|---|
| `ProvokeTime` / `ProvokeTimeShort` | [AIGlobals.cs:378](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L378)/[379](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L379) | 200 / 80 | **4.0s / 1.6s** | `Provoked -= AIClockPeriod` [TankAIHelper.cs:4768](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L4768) | combat-alert hold after a hit | 04,05,06,12,20 |
| `FrustrationMeter` ladder: Fire/Start/Drop/End | [AIGlobals.cs:282](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L282)-[289](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L289) | 25/120/240/260 | **0.5/2.4/4.8/5.2s** | `FM += AIClockPeriod` [TankAIHelper.cs:4372](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L4372); decay `-max(1,AIClockPeriod/2)` [4342](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L4342) | stuck→shoot→beam→drive→reset | 08,09,13 |
| `UrgencyOverloadReconsideration` | [AIGlobals.cs:287](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L287) | 180 | **~3.6s** | `Urgency/UrgencyOverload += AIClockPeriod/n` per Operations | re-calc top speed / re-enable avoidance | 09,13 |
| `LosBlockedStreakThreshold` | [AIGlobals.cs:406](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L406) | 2 | **1.2s** (2 × 0.6s) | streak++ per `CheckEnemyAndAiming` | assert BlockedLineOfSight | 06,07 |
| `WeaponDelayClock` (enemy/sniper) | [RGeneral.cs:382](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RGeneral.cs#L382) | 150 | **3.0s** | `+= AIClockPeriod`, Operations | force-fire when not perfectly aimed | 05,12 |
| `RepairStepperClock` (allied) | [AIERepair.cs:50](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIERepair.cs#L50) | delaySafe 40 / delayCombat 120 | **0.8s / 2.4s** | `-= AIClockPeriod` [AIERepair.cs:1689](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIERepair.cs#L1689) | block-by-block repair cadence (AdvancedAI: 0.2/0.6s, peacetime bypasses — see §7) | 20 |
| `RepairStepperClock` (enemy) | [RRepair.cs:185](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RRepair.cs#L185) | `eDelay / (CommanderSmarts+1)` | variable (~1.5-6s safe) | `-= AIClockPeriod` [RRepair.cs:278](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RRepair.cs#L278) | enemy repair, smarts-scaled | 05,20 |
| `ReserveSuperGrabs` | [AIERepair.cs:1634](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIERepair.cs#L1634) | `5*AIClockPeriod` | ~1.0s | Operations | bolt-grab reservation window | 20 |
| Strategic build gates: `MinimumTicksUntilBuild` / `DelayBetweenBuilding` (ticks) | [ManEnemyWorld.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManEnemyWorld.cs) | 23 / 8 operator-ticks | **92s / 32s** | Strategic (×4s) | unloaded base first-build / expand cadence | 15,16,17 |
| `lastFounderStopUpdateTicks` | [ManEnemyWorld.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManEnemyWorld.cs) | 6 operator-ticks | **24s** | Strategic | founder idle-lock after vendor visit | 15 |

### 2C — Tick counters, **DEPENDENT** ⚠ (stepped `±1`/`++`/per-render-frame; real-time drifts with SP↔MP, framerate, or pass)

| Name (set value) | Decl | Raw | Norm. SP | Norm. MP | Step / clock | Gates | Pipelines |
|---|---|---|---|---|---|---|---|
| `actionPause = ReverseDelay` | [AIGlobals.cs:318](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L318) | 60 | **0.12s** | 0.04s | `-= AIClockPeriod` per FU (OnPreUpdate) [TankAIHelper.cs:3362](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L3362) | reverse-from-base/resource hold | 05,13 |
| `actionPause = ReverseFromResourceDelay` | [AIGlobals.cs:319](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L319) | 35 | 0.08s | 0.02s | same | reverse-from-resource | 13 |
| `actionPause = Random(50,300)` / `(160,420)` | [BBase.cs:43](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AlliedOperations/BBase.cs#L43) / RWheeled | 50-420 | 0.1-0.84s | 0.03-0.28s | same | base idle / circle-strafe hold | 13 |
| `actionPause = 60/30` (DefaultIdle/FlutterAround) | [RGeneral.cs:212](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RGeneral.cs#L212) | 60 / 30 | 0.12 / 0.06s | 0.04 / 0.02s | same | idle wander reroll | 05 |
| `DelayedAnchorClock` / `unanchorCountdown` | [TankAIHelper.cs:431](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L431)/[442](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L442) | 20 / 15 | **~4-8s** | up to 12s | `++`/`--` (by 1) per Directors ([:3151](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L3151)) or Operations ([BAegis.cs:75](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AlliedOperations/BAegis.cs#L75)) | auto-anchor settle / re-anchor delay | 04,11,13 |
| `WeaponDelayClock` (allied AimDefend) | [BGeneral.cs:52](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/BGeneral.cs#L52) | 30 | **6.0s** | 18.0s | `++` (by 1) per pass | allied turret patience-fire (path may be dead — §7) | 12,13 |
| `BeamTimeoutClock` | [TankAIHelper.cs:434](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L434) | 35 arm / 40 timeout | ~0.6-1.3s | (framerate) | `++` per render frame (Maintainer) [AIEBeam.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIEBeam.cs) | max beam-on safety window | 08,09,12 |
| `ErrorsInTakeoff → MaxTakeoffFailiures` | [AIControllerAir.cs:65](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/Movement/AIControllerAir.cs#L65) | 240 | ~0.2-0.4s | (framerate) | per render frame (DriveMaintainer/WatchStability) | demote stuck flyer → Grounded | 10,11 |
| `EnemyNewTechConstruction` clock | [RRepair.cs:301](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RRepair.cs#L301) | 20 | ~4.0s | ~12.0s | `--` (by 1) per Operations (§7) | enemy new-tech build step | 20 |

### 2D — Dimensionless (hysteresis / gates — not time)

| Name | Decl | Value | Role | Pipelines |
|---|---|---|---|---|
| `CombatRangeRetentionMult` / `…Sqr` | [AIGlobals.cs:402](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L402) | 1.5 / 2.25 | drop held target only past 1.5× range | 06,07 |
| `RTSLockMaxRangeMultiplier` / `…Sqr` | [AIGlobals.cs:411](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L411) | 2.5 / 6.25 | hard cap for RTS-locked targets | 06 |
| `AngularProgressThreshold` | [AIGlobals.cs:293](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L293) | 0.5 rad/s | yaw counting as "progress" for FM decay | 08,09 |
| `MinDiveAGL` | [AIGlobals.cs:467](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L467) | 60 m | dive Approach→Commit gate (×0.4 = 24 m commit→recover) | 10 |
| `BaseExpandChance` | [AIGlobals.cs:454](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L454) | 65 (+BB/10000) | expansion roll (saturates 100% at ≥350k BB) | 16 |

### 2E — Staleness diagnostic

| Name | Decl | Threshold | Unit | Note |
|---|---|---|---|---|
| `ControlOperatorAgeFrames` vs `AIClockPeriod*3` | [TankAIHelper.cs:465](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L465) | 30 frames (SP) | **render frames** (`Time.frameCount`) | consumer (Maintainer) IS render-frame, so the unit is now consistent; only the `*3` threshold mixes the tick-based ops period in. Mild. |

---

## 2.5 Migrated AITimer values — ⚠ SOURCE OF TRUTH

> **STRICT RULE — read before changing any timer.** The values below are seconds-based (`AITimer`, see [TimingPrimitives.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/TimingPrimitives.cs)). **This table is the single source of truth for their durations and effects. Whenever you change any value at the cited code line, update its row here in the *same* commit — and vice-versa, edits here are documentation only until the cited line changes.** New timers migrated to `AITimer`/`AIAccumulator` MUST be added here.

### Live migrated timers

| Timer / set-value | Code location(s) | Seconds | Effect | Status |
|---|---|---|---|---|
| `lightBoostFeatherTimer` | field [TankAIHelper.cs:432]; LandAICore.cs:383 & :417, SeaAICore.cs:536, SpaceAICore.cs:527 | **0.5s** | feathers the LightBoost pulse | migrated (was a 25-frame counter, framerate-variant) |
| `actionPause` ← `ReverseDelay` | [AIGlobals.cs:318]; BAssassin.cs:39, BEnergizer.cs:38, BProspector.cs:86, RMiner.cs:65 | **1.0s** | reverse-away-from-base hold | **RETUNED** (was 60t = 0.12s) |
| `actionPause` ← `ReverseFromResourceDelay` | [AIGlobals.cs:319]; BProspector.cs:71, RMiner.cs:49, + `reverseFromResourceTime`/`reverseFromBaseTime` consts | **0.6s** | reverse-away-from-resource hold | **RETUNED** (was 35t = 0.07s) |
| `actionPause` (idle wander) | RGeneral.cs:206 (`=1000`, coast `>250`) | **2.0s** (coast last 0.5s) | DefaultIdle wander; new random dest each cycle | **REVIVED reroll + RETUNED** (was 0.12s; reroll `==1` was dead) |
| `actionPause` (air flutter) | RAircraft.cs:285 (`=1000`) | **2.0s** | FlutterAround heading reroll | **REVIVED reroll + RETUNED** (was 0.06s; reroll `==1` was dead) |
| `actionPause` (circle-strafe) | RWheeled.cs:135 (`Random(160,420)`) | 0.32–0.84s; phase `>120`=0.24s | RWheeled circle-strafe phases | invariant; **not retuned** (multi-phase) |
| `actionPause` (base idle) | BBase.cs:43 (`Random(50,300)`) | 0.1–0.6s; phase `<160`=0.32s | BBase idle pause phases | invariant; **not retuned** (multi-phase) |
| `beamFlipTimer` ← `BeamFlipTippedHoldSecs` | [AIGlobals.cs:320]; AIEBeam.cs:98 (fire), AIEBeam.cs:109 (re-arm) | **1.5s** | 3D-navi tech tipped over continuously → fire build beam to flip upright | **REVIVED** + migrated (was dead: `++` overwhelmed by the decrement) |
| `DelayedAnchorClock` (rest-duration) | shim [TankAIHelper.cs:431]; ~30 sites: BAegis/BAstrotech/BBuccaneer/BEscort/RGuardian + TankAIHelper RTS-navi | **4.0s** (= `BaseAnchorMinimumTimeDelay` 20 / `AnchorTicksPerSecond` 5) | continuous rest required before auto-anchor | migrated; was role/MP-dependent (~4-8s SP, up to 12s MP) |
| `unanchorCountdown` (warn) | shim [TankAIHelper.cs:454]; same files | **3.0s** (= 15 / `AnchorTicksPerSecond` 5) | warn delay before unanchor; `--` decrements removed | migrated |

### Shim mechanic (so the table reads correctly)
`actionPause` is an `int` property backed by `actionPauseTimer` (`AITimer`) at [TankAIHelper.cs:436]. Legacy tick set-values are interpreted at `ActionPauseTicksPerSecond = 500` (= `AIClockPeriodSet 10 / fixedDeltaTime 0.02`), so **seconds = tick-value ÷ 500**. To retune a hold: change the cited tick constant (or migrate that set-site to seconds directly). The 38 redundant `-= AIClockPeriod/5` decrements and the OnPreUpdate decrement were removed — the timer self-counts via `Time.time`. This **supersedes the `actionPause`/`ReverseDelay`/LightBoost rows in [§2C](#2c--tick-counters-dependent--stepped-1per-render-frame-real-time-drifts-with-spmp-framerate-or-pass)** (those are now invariant, not DEPENDENT).

### Layer-2 status: migration COMPLETE
Every framerate/SP-MP-dependent tick-counter above is now seconds-based and invariant. The idle-wander reroll (RGeneral / RAircraft) is **revived + retuned**.

**Open as a playtest-only feel decision (NOT a bug):** `RWheeled` circle-strafe (0.32–0.84s) and `BBase` base-idle (0.1–0.6s) keep their original sub-second multi-phase magnitudes. They are correct and invariant; their feel was left untouched because retuning combat/idle timing blind is a regression risk. To retune, set the cited `Random(...)` ranges (and the phase thresholds proportionally) and update the rows above.

### Layer-3 control timers (dive FSM)

| Constant | Code | Seconds | Effect |
|---|---|---|---|
| `CommitRecoverAltHysteresis` | [AIGlobals.cs]; AirplaneAICore `Commit` case | **0.3s** | altitude-low abort must persist this long before aborting a dive (terrain-column-jitter debounce) — BUG-DA-1 |
| `PostRecoverCooldown` | [AIGlobals.cs]; AirplaneAICore `Idle→Approach` gate + `Recover→Idle` | **2.0s** | minimum gap after a Recover before a new dive Approach — kills the climb-dive yo-yo — BUG-DA-2 |
_(Steering setpoint slew was tried and **reverted** — playtest showed it caused understeer / poor target tracking / sluggish turrets. Turret + steering tracking is a separate problem: aim needs to update FASTER, not be smoothed. See investigation notes.)_

(see also §6 oscillation controls and [10 dive-attack-fsm](10_dive-attack-fsm.md))

## 3. Per-pipeline coverage map

| Pipeline | Own cadence? | Primary clock(s) | Headline values |
|---|---|---|---|
| 01 Boot | one-shot + 1 repeat | InvokeSingleRepeat | EmitHealthPulse 10s; deferred one-shots (0.01-1s) |
| 02 Spawn | event-driven | Unity Invoke | AISubscribeDelay 0.1s (×5), EnemyInitGrace 1s |
| 03 Load/save | event-driven | — | resets OperatorTicker, RaidCooldown 600s on load |
| 04 Allied tick | yes | Operations | TargetValidation 0.6s, Provoke 4s, anchor clocks |
| 05 Enemy tick | yes | Operations | Provoke 4s, WeaponDelayClock 3s, actionPause idle |
| 06 Target acq | yes | Operations | scan/validation/cache, LOS streak, retention hyst |
| 07 Combat FSM | yes | Operations | bucket hysteresis, LOS streak |
| 08 AICore drive | yes | Maintainer + Operations | ControlOperator staleness, FM ladder, actionPause |
| 09 Stuck/unjam | yes | Operations + Maintainer | FM ladder 0.5-5.2s, BeamTimeoutClock |
| 10 Dive FSM | yes | **Maintainer** | MinRecoverHold 1.5s, MaxRecoverHold 8s, MinDiveAGL |
| 11 Movement ctrl | minimal (structural) | Directors | dirty-flag latency 0.4s, CheckEngines 1s, anchor clocks |
| 12 Weapon firing | yes | Director(2.5Hz)+Maintainer | WeaponDelayClock, LeadPredictionMaxTOF 3s |
| 13 Ops dispatch | yes | Operations + PreUpdate | actionPause family, Urgency, WeaponDelayClock |
| 14 Team mgmt | yes | **Team economy 6s** | anger decay, retreat clear, build gates |
| 15 Enemy world | yes | **Strategic 4s/0.5s** | OperatorTickDelay, build 92s/32s, founder 24s |
| 16 Base ops | yes | Team economy 6s + Strategic | harvester 60s, expand, MP BB drip |
| 17 RawTech spawn | yes | SpecialAISpawner.Update + siege | AirSpawnInterval, RaidCooldown 1200s, queue frames |
| 18 Harmony patches | no (one-shot install) | — | installs the Maintainer hook (render-frame) |
| 19 MP sync | yes (MP) | InvokeSingleRepeat | PushTeamDeltas 1Hz, siege invokes 1s/16s |
| 20 Repair/damage | yes | Operations | RepairStepperClock 0.8-24s, BlockAttachDelay 0.75s |

---

## 4. Frequency ladder (SP, 50 Hz)

Watch non-harmonic neighbours — they *beat*.

```
~30-60 Hz | Maintainer: Drive / Weapon / Beam / Dive FSM   per render frame  (framerate)
  50.0 Hz | Pre/Post update (actionPause, EstTopSped)       0.02 s
   5.00 Hz| OPERATIONS pass                                  0.20 s
   2.50 Hz| TargetCache(combat) / DIRECTORS pass            0.40 s
   2.00 Hz| ScanDelay / world MaintainerTickDelay           0.50 s
   1.67 Hz| TargetValidation (LOS raycast)                  0.60 s   <-- beats vs 0.40
   0.67 Hz| TargetCache(idle)                               1.50 s
   0.25 Hz| Provoke decay end / OperatorTickDelay(world)    4.00 s
   0.17 Hz| EnemyTeamAwareness (team economy)               6.00 s
   0.10 Hz| EmitHealthPulse                                10.00 s
   0.03 Hz| DelayBetweenBuilding(loaded) / world build      30-32 s
 0.0008 Hz| RaidCooldown                                  1200.00 s
```

**Beat / mismatch hot-spots:** 0.40s combat-cache vs 0.60s validation (non-harmonic, ~1.2s repeat); 5 Hz Operations producer vs ~30-60 Hz Maintainer consumer (§5.1); loaded-base build 30s vs strategic-world build 32s (two *unsynchronized* economies — §5.4).

---

## 5. Producer / consumer & feedback map

### 5.1 ControlOperator (Operations → Maintainer)
Operations writes (5 Hz SP / 1.67 Hz MP); Maintainer reads (render frame). No mechanical damping — explicit staleness contract ([TankAIHelper.cs:451-467](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L451)). Goal-drift, not oscillation; worse in MP (0.6s refresh).

### 5.2 WeaponState (Directors → Maintainer)
Directors writes (2.5 Hz); Maintainer reads (render frame, ~12-24× faster) and re-validates the target inline + re-asserts `SuppressFiring` every frame ([AIEWeapons.cs:118-141](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIEWeapons.cs)) — mitigated.

### 5.3 FrustrationMeter (Operations, self-loop)
`+AIClockPeriod` stuck vs `-max(1,AIClockPeriod/2)` moving (2:1). Monotonic ladder, hard reset at `SettleDown`. Latch gated at 120 prevents chatter.

### 5.4 Loaded vs unloaded base economy (two clocks, unsynchronized)
Loaded bases: `BaseFunderManager` 6s tick, build window 30s (`Time.time`). Unloaded bases: `ManEnemyWorld` 4s operator tick, build window 32s. The two run on independent clocks and are not synchronized ([RLoadedBases.cs:372](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RLoadedBases.cs#L372) vs [ManEnemyWorld.cs:35](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/ManEnemyWorld.cs#L35)) — a base crossing the load boundary can briefly double- or skip a build window.

### 5.5 Provoke / lastEnemy / BlockedLineOfSight
Covered in pipeline [06](06_target-acquisition.md); damped by range hysteresis (1.5×), LOS streak (1.2s), grace (3s).

---

## 6. Oscillation / flicker controls already in code

| Mechanism | Ref | Prevents |
|---|---|---|
| Range-retention hysteresis (1.5×) | [AIGlobals.cs:398-401](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L398) | single-tick flicker in (1.1, 1.5] band |
| LOS-lost grace (3s) | [AIGlobals.cs:382](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L382) | decay→drop→re-acquire flicker |
| LOS streak (2 checks) | [TankAIHelper.cs:4687](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L4687) | MoveSideways↔stand-and-shoot flicker |
| Combat-bucket hysteresis | [RWheeled.cs:146](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/EnemyOperations/RWheeled.cs#L146),[223](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/EnemyOperations/RWheeled.cs#L223) | hold↔advance "twitch" at spacer+range edge |
| `SettleDown(stopCore:false)` | [TankAIHelper.cs:4527](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L4527) | movement↔idle flap from injected Stop frame |
| FM angular/velocity soft-decay | [AIGlobals.cs:291](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L291) | turn twitches snowballing into beam trigger |
| FireControl owner split | [AIEWeapons.cs:79](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIEWeapons.cs) | one-tick FireControl flicker (two clocks racing) |
| Dive `MinRecoverHold` (1.5s) | [AIGlobals.cs:468](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L468) | Idle↔Approach dive oscillation |

---

## 7. Candidate issues surfaced by the audit

⚠ **Leads, not confirmed defects.** Each carries the corroboration strength (how many independent agents found it). Verify before any change — the user reverts regressions.

**Unscaled-counter bug class (the dominant pattern):**
- **TD-1 (Med, multi-agent) — `actionPause` framerate/period-dependent + double-decremented.** `-= AIClockPeriod` per FU in OnPreUpdate ([TankAIHelper.cs:3362](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L3362)) *and* `-= AIClockPeriod/5` in several Operations handlers. `ReverseDelay=60` ⇒ 0.12s SP / 0.04s MP — far shorter than the name implies.
- **BUG-2 (Med, 4 agents) — `actionPause == 1` dead branch.** `DefaultIdle` ([RGeneral.cs:206](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RGeneral.cs#L206)) and `FlutterAround` ([RAircraft.cs:285](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/EnemyOperations/RAircraft.cs#L285)) check `== 1`, but the counter steps by `AIClockPeriod` (10) so it jumps …→10→0, never hitting 1 → the "pick new wander target" arm never fires.
- **BUG-3 (Med, 7 agents) — anchor clocks unscaled.** `DelayedAnchorClock`/`unanchorCountdown` step `++`/`--` (by 1, not `AIClockPeriod`), so auto-anchor settle time is role- and MP-dependent (~4-8s SP, up to 12s MP). Inconsistent with the §2B invariant pattern.
- **BUG-4 (Med, 3 agents) — `WeaponDelayClock` allied vs enemy mismatch.** Allied `AimDefend` uses `++` (6s SP / 18s MP); enemy uses `+= AIClockPeriod` (3s invariant). Same field, different unit. *Note:* one agent reports `BGeneral.AimDefend` has no live callers — confirm before fixing.
- **BUG-5 (Med, 3 agents) — `EnemyNewTechConstruction` clock `--`.** Decrements by 1 not `AIClockPeriod` ([RRepair.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RRepair.cs)) → TICK-DEPENDENT, ~12s MP vs ~4s SP.

**Integer-division zero/truncation:**
- **BUG-6 (Med→High, 2-3 agents — verify) — `IsOrbiting` `AIClockPeriod / 40 = 0`.** Integer division ⇒ 0 in both SP (10/40) and MP (30/40), disabling the orbit check ([~TankAIHelper.cs:4221](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L4221)). Suggested fix `(float)AIClockPeriod/40f`. (Was a single-agent LOW lead in the pilot; corroborated here.)
- **BUG-7 (Med, 3 agents) — MP BB drip truncation.** `MPEachBaseProfits * (40 / AIClockPeriod)`: `40/30 = 1` in MP (int) ⇒ 250 BB/base instead of the intended ~333 ([RLoadedBases.cs:~1087](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/Enemy/RLoadedBases.cs)).

**Behaviour discontinuities / dead code:**
- **SMELL-8 (Med, 3 agents) — AdvancedAI peacetime bypasses `RepairStepperClock`.** Calls `InstaRepair` every Operations pass (~10 blocks/0.2s) with no rate limiter — a ~50×/40× discontinuity at the peacetime/combat boundary.
- **BUG-9 (Med, 2 agents) — `lastTargetUpdateCount` never decremented.** `OperatorTicksKeepTarget=4` is set but never stepped down ([NP_Presence.cs](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/World/NP_Presence.cs)) → unloaded `lastTarget` never expires.
- **SMELL-10 (Med, 3 agents) — `actionPause++` in `BeamMaintainer` ineffective.** `+1`/render-frame vs `-AIClockPeriod`/FU ⇒ net negative; thresholds 70/80/100 unreachable ([AIEBeam.cs:97](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIEBeam.cs#L97)) — the tipped-over 3D-navi path is dead.
- **DEAD-11 (High, 3 agents each) — dead constants:** `AnchorAimDampening=45` (zero consumers, [AIGlobals.cs:279](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L279)); `AircraftPreCrashDetection=1.6` (zero consumers, [AIGlobals.cs:331](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L331)); `MaxRangeFireAll=125` (consumer removed in P12).

**Naming / config hazards:**
- **TD-12 (Med, 3 agents) — `DamageAngerCoolPerSec` naming trap.** `= 25 * EnemyTeamAwarenessUpdateDelay` is a per-6s-tick lump (effective 25/s), but the name reads as 150/s; changing the delay silently breaks the decay rate ([AIGlobals.cs:416](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AIGlobals.cs#L416)).
- **NOTE — `AirSpawnInterval` default/config mismatch:** field default 30s is overwritten to 60/rate by config; spawns between module-init and options-init use the wrong interval.
- **NOTE — `ModTechsDatabase` 3s deferred load race:** spawns in the first ~3s after mode-start can hit an empty DB.
- **NOTE — `RaidCooldown` not `[SSaveField]`:** every load resets it to 600s, granting a fresh 10-min siege grace.
- **NOTE — `ProvokeTime` comment stale:** "200/40 = 5 seconds" — actual is **4.0s** (invariant).

---

## Reconciliation log

Pilot: 7 agents on 06/07/08. Extension: 43 agents (3 per cadence pipeline, 1 per structural). Consensus held for the clock families and seconds-gated values; contested numbers were re-verified against source by the merge (not majority-voted):

| Value | Split | Verified | Source |
|---|---|---|---|
| Maintainer driver | FixedUpdate vs render-frame | **render-frame `Update`** | [AIECore.cs:20](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIECore.cs#L20) |
| `ProvokeTime` | 6×4.0s · 1×40s | **4.0s** (per-Operations, invariant) | [TankAIHelper.cs:4768](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L4768) |
| `actionPause`(60) | 4×0.12s · 3×1.2s | **0.12s** (per-FU `-=AIClockPeriod`) | [TankAIHelper.cs:3362](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L3362) |
| `FrustrationMeter`→120 | 5-6×2.4s · 1×0.24s | **2.4s** (per-Operations) | [TankAIHelper.cs:4309](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L4309) |
| `RepairStepperClock`(allied safe) | 2×0.8s · 1×8.0s | **0.8s** (`-=AIClockPeriod` per Operations) | [AIERepair.cs:1689](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AIERepair.cs#L1689) |
| anchor clocks pass | Operations vs Maintainer | **`++` per Directors *or* Operations** (role-dependent) | [TankAIHelper.cs:3151](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/TankAIHelper.cs#L3151), [BAegis.cs:75](../Modified/TACtical_AI-master/TACtical_AI-master/TAC_AI/AI/AlliedOperations/BAegis.cs#L75) |

---

## Issues

**NONE** in this register's own data. Candidate leads for the *code* are in [§7](#7-candidate-issues-surfaced-by-the-audit), explicitly marked unverified with corroboration counts.
