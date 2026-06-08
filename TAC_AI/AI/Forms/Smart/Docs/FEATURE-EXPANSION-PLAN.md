# FEATURE-EXPANSION-PLAN

> Status: design v1, **round-2 reviewed + finalized** (5 round-2 reviewer reports reconciled atop round-1 — see §12). Scope = the **Smart feature-input expansion** across all four ML models (Intent / ActionValue / Residual / Threat), plus the supporting feature-extraction infrastructure (per-tech publishers, K-nearest cache, daemon, struct definition), plus a **single coordinated `ArchitectureVersion` bump** for all four models to version `3`. Total LOC estimate: **~3 050** (11 new files ≈ 2 220 LOC + 7 modified files ≈ 685 LOC delta + ≈ 145 LOC overhead/wiring/comments). **Dependencies on TRAINING-DIRECTOR-PLAN: none.** This plan is **precondition work** for the Director — the Director's `actionvalue_loss_variance_ratio` constraint relies on real ActionValue production (which doesn't exist today, see `LearningService.cs:25-29` TODO), the `replay_bank` tee point on ActionValue is a no-op until producers ship (see Director plan §5), and the four-bucket reward channel becomes meaningless without features behind it. Build constraint: C# 7.3 / .NET 4.6.1; old-style csproj (every new `.cs` registered explicitly). No `volatile long` (CS0677).

---

## §1. Status header

This document plans the **feature-input expansion** for the four Smart `ILearnedModel`
implementations and the supporting feature-extraction infrastructure. Three deliverables:

  1. A **shared `StrategicStateVector`** struct that defines, in one place, the slot
     layout that every model's per-instance inputs slice into. One source of truth for
     feature names + slot indices + reserved-block ownership.
  2. A **`StrategicStateExtractor` background daemon** that fuses `BeliefSnapshot`
     readings, per-tech `SelfStateProbe` snapshots, ArmorMap + TerrainMap reads, and
     event-driven sidecars (DamageHints, WeaponFire, Cargo, Anchor) into the
     `StrategicStateVector` and atomically publishes per-tech slots.
  3. A **coordinated `ArchitectureVersion` bump from {2, 1, 1, 1} → {3, 3, 3, 3}** across
     `OpponentIntentClassifier` / `ActionValueEstimator` / `TrajectoryResidualModel` /
     `ThreatAssessmentModel`, with input/hidden width changes documented per model.
     `ProfilePersistence` already refuses to load mismatched-version params (see
     `Migrations/0001_initial_schema.cs` discipline + `ProfilePersistence.cs:22,111`
     byte gate); restart-from-scratch is intentional.

LOC estimate **~3 050** total: 11 new files ≈ 2 220 LOC; 7 modified files ≈ 685 LOC
delta; ≈ 145 LOC overhead (csproj rows, comment headers, SmartRuntime Shutdown ripples,
migration file). Per-file breakdown in §8 + §9. The plan is sized so each file row's
budget is defensible against the pseudocode it implies.

**No Director dependency.** This plan must land **before** TRAINING-DIRECTOR-PLAN
implementation because: (a) Director's `actionvalue_loss_variance_ratio` is meaningless
when ActionValue has no producer (zero-input → constant loss → constant ratio);
(b) Director's `replay_bank` action gates on tee sites whose ActionValue point has
ZERO callers today (`LearningService.cs:25-29`); (c) the four-bucket reward channel
(§5 of Director plan) only earns signal from features that meaningfully discriminate
combat / role / movement / safety states. Director without this plan = Director driving
a dial that is not wired to the engine.

---

## §2. Goal

**Why feature expansion before Director implementation.** The current four-model input
shape was scaled for v0.1 stub training: Intent 30 × 12 (`OpponentIntentClassifier.cs:35-36`),
ActionValue 50 (`ActionValueEstimator.cs:38`), Residual 15 (`TrajectoryResidualModel.cs:32`),
Threat 25 (`ThreatAssessmentModel.cs:29`). The 50 ActionValue dims are entirely
ZERO today: documentation calls out "40 strategic + 10 action one-hot" but the producer
side never populates them (see `LearningService.cs:25-29`). The Threat features are
filled by a small hand-pulled subset of `VehicleModelSnapshot` (see `LearningService.cs:680-694`
which only sets `features[23,24]`). The Residual carries projection geometry but reserves
6 trailing slots that nothing fills. Net result: the models are trained on a fraction of
the signal Smart already computes — BeliefState, ArmorMap, TerrainMap, WeaponSpec,
HealthSidecar, DamageHints, all of which are produced and published but never connect to
the trainers. This plan closes that gap by routing the world model into the input
vectors.

**The v0.3 deferral cleanup.** Prior plan revisions allowed "v0.3" deferrals that masked
unfinished work (LearningService TODO at `LearningService.cs:25-29`; the documented but
unwired 40-dim slot in `ActionValueEstimator.cs:38`; reserved 6-slot in
`TrajectoryResidualModel.cs:35-38`). The Director plan was explicit that **NO new v0.3
deferrals are acceptable** (Training-Director plan header line 3). This plan honours
that posture: every reserved slot in §3.2-§3.5 has a documented **future-use category**
(e.g., "reserved-for-mission-state", "reserved-for-shield-charge") that names what kind
of feature the slot is reserved for — not "v0.3" or "future phase." Reserved slots exist
because feature engineering iterates: leaving 20-30% headroom prevents the next plan
from needing another coordinated arch bump for one new input.

**The coordinated-bump rationale.** TerraTech's profile-load path (see
`ProfilePersistence.cs:22,111` + `Migrations/0001_initial_schema.cs`) uses a `byte`
`ArchitectureVersion` per model. A mismatched version on any single model refuses to
load and the model re-initializes from fresh Glorot weights. Bumping the four models
**together to version 3** in a single change has three benefits: (a) operators don't
have to discover one-at-a-time which model lost its profile after pulling the patch;
(b) `ProfilePersistence` round-trips honestly — either ALL four train from scratch or
none do; (c) the trainer's behaviour after the change is uniform across models, simpler
to reason about. The patch ships the four bumped models in one commit; the next session
starts with all four at random init and trains on the new feature shapes from minute
zero.

**No-preservation-needed posture.** Pre-bump, the four models are largely uninformative:
ActionValue is a 0-input identity sink (no producer); Residual fires on a 15-dim feature
vector with 6 reserved slots zeroed; Threat fills 2 / 25 slots from VehicleModelSnapshot;
Intent has a real 12-feature producer but trains only the Dense head (the GRU recurrent
weights froze pre-`0002_BpttUnfreeze` per `OpponentIntentClassifier.cs:30-32`). There is
**no learned policy worth preserving** through a feature bump. Restart from random init
is the right move; the disk-tier `.previous` / `.penultimate` chain
(`ProfilePersistence.cs:64-95`) is preserved for rollback to the old shape if the new
feature surface regresses behavior badly, but in expectation those backups are dead.

---

## §3. StrategicStateVector

### §3.1 Shared struct definition

Single source of truth: `Smart/Learning/Features/StrategicStateVector.cs`.

C# 7.3 implementation — `sealed class` (not struct), because it carries a backing
`float[]` array that is wider than the .NET ValueType register-passing budget and
because the daemon mutates it during build (immutability semantics are conferred by the
double-buffer publish, not by struct readonly). The class is **declared immutable by
discipline**: setters are private, builders fill via internal `Slot` accessors, and the
double-buffer (§4.4) treats every published instance as frozen. **CAVEAT for
implementors**: `Raw` is a `public readonly float[]` — `readonly` blocks field
reassignment but NOT element mutation. The discipline relies on the daemon never
touching a `StrategicStateVector` instance after `DoubleBuffer.Write`, and on no
reader ever writing into `Raw[i]`. The NaN/Inf scrub in §4.2 step 7 runs BEFORE the
atomic Write — it is the daemon's last edit on the to-be-published instance, never
a post-publish mutation. Readers MUST treat `Raw[i]` as read-only.

```
public sealed class StrategicStateVector
{
    // single backing array; total slot count = MaxSlots
    // Layout: [Intent 0..39][ActionValue 40..167][Residual 168..215][Threat 216..263]
    // Each per-view slot constant in §3.2-§3.5 is offset from 0 inside its view; the
    // shared Raw[] is addressed via (ViewBase + ViewSlot). MaxSlots is sized for all
    // four views laid end-to-end with no overlap (40 + 128 + 48 + 48 = 264).
    public const int MaxSlots = 264;            // no overlap across views

    // per-view base offsets into Raw[]
    public const int IntentBase      = 0;       // [0..39]
    public const int ActionValueBase = 40;      // [40..167]
    public const int ResidualBase    = 168;     // [168..215]
    public const int ThreatBase      = 216;     // [216..263]

    public readonly float[] Raw;                // length == MaxSlots; row-major

    // per-model view extents
    public const int IntentDim       = 40;      // per-timestep, 30 timesteps total
    public const int IntentSeqLen    = 30;
    public const int ActionValueDim  = 128;
    public const int ResidualDim     = 48;
    public const int ThreatDim       = 48;

    public readonly long PublishedTickMono;     // MonoClock at build
    public readonly TechId Subject;             // which tech this vector describes
    public readonly TeamId SubjectTeam;
    public readonly int Generation;             // monotonic; trips on lifecycle reset

    internal StrategicStateVector(TechId subject, TeamId team, long now, int gen)
    {
        Subject = subject; SubjectTeam = team;
        PublishedTickMono = now; Generation = gen;
        Raw = new float[MaxSlots];
    }
}
```

`StrategicStateExtractor` (§4) constructs a fresh instance per tech per tick, fills the
Raw array via the §3.2-§3.5 slot maps below, and writes through the per-tech
`DoubleBuffer<StrategicStateVector>` (§4.4). Readers obtain a stable snapshot via
`DoubleBuffer.Read()` (mirrors `Threading/DoubleBuffer.cs:37`).

Slot ownership is declared via a **named-constant enum-like static class** per model
view (avoids the `enum` reflection-cost path; named offsets compile to constants):

```
public static class IntentSlots
{
    public const int RelPosX           = 0;
    public const int RelPosZ           = 1;
    public const int RelHeadingSin     = 2;
    public const int RelHeadingCos     = 3;
    public const int RelVelX           = 4;
    public const int RelVelZ           = 5;
    // ... see §3.2 for the full 40 names
}
public static class ActionValueSlots { /* 128 names; §3.3 */ }
public static class ResidualSlots    { /* 48  names; §3.4 */ }
public static class ThreatSlots      { /* 48  names; §3.5 */ }
```

The four model files (§7) read inputs by indexing `Raw[IntentBase + IntentSlots.RelPosX]`
etc. NO model file owns its own feature offset constants — that would re-introduce the
silent-slot-collision risk we eliminated by routing through the shared vector. The
slot constants are reviewed in lockstep with the four model `ArchitectureVersion` bumps
in §7.5. Per-view slot constants are **view-local** (start at 0 inside each view) and
the call-site always composes with the matching `ViewBase`. The §4.3 derivation table
spells AV[k], Intent[k], etc., as view-local indices into the §3.2-§3.5 slot map.

### §3.2 Intent input layout — 30 × 40 (was 30 × 12)

`OpponentIntentClassifier.SeqLen=30, FeatureDim=40` (was 12 per
`OpponentIntentClassifier.cs:35-36`).

Per-timestep `IntentSlots`:

| Slot | Name | Derivation | Source |
|---|---|---|---|
| 0 | `RelPosX` | target.PositionMean.x − self.PositionMean.x | BeliefSnapshot (`BeliefState.cs:111`) |
| 1 | `RelPosZ` | target.PositionMean.z − self.PositionMean.z | BeliefSnapshot |
| 2 | `RelHeadingSin` | sin(target.HeadingMean − self.HeadingMean) | BeliefSnapshot (`BeliefState.cs:114`) |
| 3 | `RelHeadingCos` | cos(target.HeadingMean − self.HeadingMean) | BeliefSnapshot |
| 4 | `RelVelX` | target.VelocityMean.x − self.VelocityMean.x | BeliefSnapshot (`BeliefState.cs:112`) |
| 5 | `RelVelZ` | target.VelocityMean.z − self.VelocityMean.z | BeliefSnapshot |
| 6 | `TargetSpeed` | target.VelocityMean.magnitude | BeliefSnapshot |
| 7 | `TargetAgeSec` | target.AgeSeconds | BeliefSnapshot (`BeliefState.cs:115`) |
| 8 | `TargetUncertaintyM` | target.UncertaintyMeters | BeliefSnapshot (`BeliefState.cs:116`) |
| 9 | `TargetSightCode` | (Fresh=1, Coasting=0.5, Stale=0.25, Lost=0) | BeliefSnapshot.Sight (`BeliefState.cs:108`) |
| 10 | `Distance` | (target.PositionMean − self.PositionMean).magnitude | derived |
| 11 | `LosBlocked` | 1.0 if TerrainMap.RaycastSegment(self, target) hit | Pathing/TerrainMap.cs |
| 12 | `TargetTeamSignedDelta` | sign(target.Team − self.Team) | BeliefState.Team |
| 13 | `TargetIsHostile` | tank.IsEnemy(target.Team) at last-friendly read | publisher cache |
| 14 | `SelfHp` | self HpFraction from HealthSidecar | HealthSidecar.cs:53 |
| 15 | `TargetHp` | target HpFraction from HealthSidecar | HealthSidecar (when shared) |
| 16 | `SelfTotalWeapons` | self.WeaponCount (cached) | TechWeapon |
| 17 | `TargetTotalWeapons` | target.WeaponCount (cached) | TechWeapon, may be N/A→0 |
| 18 | `SelfMass` | VehicleBuffer.Mass.TotalMass | VehicleModel |
| 19 | `TargetMassRatio` | target.Mass / self.Mass | VehicleModel |
| 20 | `RecentDamageDealtRate` | 1-sec EWMA of dealt-damage by self | RecentDamageDealtAccumulator (§8.4) |
| 21 | `RecentDamageTakenRate` | 1-sec EWMA of taken-damage by self | DamageHintBuffer.cs sum-magnitude |
| 22 | `RecentDamageDealtType` | dominant DamageType byte / 8.0 | DamageHintBuffer (§8.5 byte) |
| 23 | `RecentDamageTakenType` | dominant DamageType byte / 8.0 | DamageHintBuffer (§8.5 byte) |
| 24 | `SelfAnchored` | 1.0 if `tank.Anchors.NumAnchored > 0` | Tank.cs:513 |
| 25 | `TargetAnchored` | 1.0 if target anchored | AnchorStateCache (§5.2) |
| 26 | `RelSlopeAtSelf` | TerrainMap.SlopeAt(self.PositionMean.xz) | Pathing/TerrainMap.cs |
| 27 | `RelSlopeAtTarget` | TerrainMap.SlopeAt(target.PositionMean.xz) | Pathing/TerrainMap.cs |
| 28 | `SelfWeaponFireRateMean` | aggregate from WeaponSpec across self.WeaponCount | WeaponSpec.cs:29 |
| 29 | `TargetWeaponFireRateMean` | aggregate from WeaponSpec across target.WeaponCount | WeaponSpec |
| 30 | `SelfWeaponRangeMax` | max(WeaponSpec.Range) across self | WeaponSpec.cs:26 |
| 31 | `TargetWeaponRangeMax` | max(WeaponSpec.Range) across target | WeaponSpec |
| 32 | `RecentShotsFiredRate` | 1-sec EWMA from WeaponFireBuffer | WeaponFireBuffer (§8.7) |
| 33 | `CargoStateCode` | 0=empty, 0.5=partial, 1=full from CargoStatePublisher | CargoStatePublisher (§8.6) |
| 34 | `TimeOfDayCosine` | cos(2π · timeOfDayHour / 24) where `timeOfDayHour = m_Sky.Cycle.Hour` ∈ [0, 24) | `SelfProbeSnapshot.TimeOfDayHour` (captured main-thread from `ManTimeOfDay.inst.TimeOfDayPrecise` — `ManTimeOfDay` is `Singleton.Manager<T>`; daemon may NOT touch it) |
| 35 | `ProvokedFlag` | `selfProbe.ProvokedCountdown > 0 ? 1f : 0f` (`Provoked` is `int` countdown, not `bool`) | `SelfProbeSnapshot.ProvokedCountdown` (snapshotted main-thread from `TankAIHelper.cs:3117`; daemon may NOT touch the MonoBehaviour) |
| **36** | reserved-mission-state | future-use: scenario kind id when Director wires Scenario → feature | category: mission-state |
| **37** | reserved-mission-state | future-use: scenario age normalized | category: mission-state |
| **38** | reserved-shield-charge | future-use: total shield-energy / max-shield-energy | category: defensive-systems |
| **39** | reserved-shield-charge | future-use: shield-recharge rate-of-change | category: defensive-systems |

Total 40 per timestep × 30 timesteps = 1 200 floats per Intent forward pass. **Reserved
block: 4 slots ≈ 10% headroom.** Reserved categories are concrete (`mission-state`,
`defensive-systems`), not "future phase." When a slot graduates, the model bumps to
arch 4 — the reservation makes the bump cheaper but does not promise it.

Per-timestep buffer is filled by `TargetObservationSequenceBuffer` (existing — see
`TargetObservationSequenceBuffer.cs:186` tee point). The buffer's window-fill loop must
be widened from 12 to 40; see §9 modified rows.

### §3.3 ActionValue input layout — 128 (was 50)

`ActionValueEstimator.StateDim=128` (was 50 per `ActionValueEstimator.cs:38`).

Layout: **40 self-state + 40 target-state + 10 action one-hot + 38 reserved**. Reserved
block is the largest at 30% because ActionValue feeds the policy hot path and the cost of
re-bumping it is highest.

`ActionValueSlots`:

| Slot range | Name | Derivation | Source |
|---|---|---|---|
| **0..9** SELF KINEMATIC | | | |
| 0 | `SelfPosX` (normalized) | self.PositionMean.x / 1024 | BeliefSnapshot |
| 1 | `SelfPosZ` | self.PositionMean.z / 1024 | BeliefSnapshot |
| 2 | `SelfHeadingSin` | sin(self.HeadingMean) | BeliefSnapshot.HeadingMean |
| 3 | `SelfHeadingCos` | cos(self.HeadingMean) | BeliefSnapshot |
| 4 | `SelfVelMag` | self.VelocityMean.magnitude | BeliefSnapshot |
| 5 | `SelfVelHeadingSin` | sin(atan2(VelZ, VelX)) | derived |
| 6 | `SelfVelHeadingCos` | cos(atan2(VelZ, VelX)) | derived |
| 7 | `SelfSlopeUnder` | TerrainMap.SlopeAt(self.xz) | Pathing/TerrainMap.cs |
| 8 | `SelfHeightAboveTerrain` | self.y − TerrainMap.HeightAt(self.xz) | Pathing/TerrainMap.cs |
| 9 | `SelfWaterloggedFrac` | clamp(`selfProbe.WaterHeight` − self.y, 0, 5)/5 | `SelfProbeSnapshot.WaterHeight` (snapshotted main-thread from `KickStart.WaterHeight` → `WaterMod.QPatch.WaterHeight` Unity-touching, daemon may NOT touch) |
| **10..19** SELF VEHICLE | | | |
| 10 | `SelfMass` (norm) | VehicleModel.Mass.TotalMass / 50 000 | VehicleModel |
| 11 | `SelfBlockCount` (norm) | tank.blockman.blockCount / 256 | BlockManager.cs:380-387 (`tank.blockCount` does NOT exist — read via `tank.blockman.blockCount`) |
| 12 | `SelfWeaponCount` (norm) | tank.Weapons.WeaponCount / 16 | TechWeapon |
| 13 | `SelfMeleeFraction` | MeleeWeaponCount / max(1, WeaponCount) | TechWeapon |
| 14 | `SelfWeaponRangeMax` | max(WeaponSpec.Range)/200 | WeaponSpec.cs:26 |
| 15 | `SelfWeaponFireRateMean` | mean(WeaponSpec.FireRateHz)/5 | WeaponSpec.cs:29 |
| 16 | `SelfWeaponDamagePerShotMean` | mean(WeaponSpec.DamagePerShot)/100 | WeaponSpec.cs:28 |
| 17 | `SelfWeaponMuzzleVelMean` | mean(WeaponSpec.ProjectileVelocity)/300 | WeaponSpec.cs:27 |
| 18 | `SelfHpFraction` | HealthSidecar.Get(self) | HealthSidecar.cs:53 |
| 19 | `SelfReadyFireFraction` | ReadyToFire-count / WeaponCount | WeaponReadinessHelper (§8.1) |
| **20..27** SELF ENERGY + ANCHOR + CARGO | | | |
| 20 | `SelfElectricStored` | TechEnergy.Energy(Electric).currentAmount / 10 000 | TechEnergy.cs:41 (`EnergyState.currentAmount` — there is no `Stored` field) |
| 21 | `SelfElectricSpareCapacity` | TechEnergy.Energy(Electric).spareCapacity / 10 000 | TechEnergy.cs:43 |
| 22 | `SelfElectricNetFlow` | (currentAmount − previousAmount) / 1000 (positive = charging) | TechEnergy.cs:41,49 (derived; no `NetFlow` field exists) |
| 23 | `SelfIsGeneratingFraction` | count(ModuleEnergy.IsGenerating) / max(1, energyModuleCount) | ModuleEnergy |
| 24 | `SelfAnchorState` | 0=floating, 0.5=anchored, 1=sky-anchored | Tank.cs:513,515 |
| 25 | `SelfCargoFill` | NumContents / max(1, capacity) where capacity is publisher-computed from `m_CapacityPerStack * m_Stacks.Length` (or `GetTotalCapacityForLimiter()`) and cached on Attach/Detach | ModuleItemHolder + CargoStatePublisher (`TotalCapacity` does NOT exist on ModuleItemHolder) |
| 26 | `SelfCargoValue` (norm) | accumulated saleValue / 10 000 | ResourceManager |
| 27 | `SelfCargoBlockCount` | block-acceptance Holder fill / max(1, capacity) | ModuleItemHolder |
| **28..35** SELF ARMOR + DAMAGE | | | |
| 28 | `SelfWeakFaceForwardDot` | dot(forwardWorld, weakFaceNormalWorld) — both pre-computed main-thread inside SelfStateProbe (daemon may not touch `rootBlockTrans` Unity Transform); ArmorMap exposes `FaceNormalLocal` only (ArmorMap.cs:9), publisher rotates to world via cached `rootBlockTrans.rotation`. | ArmorMap.cs:61 + SelfProbeSnapshot.forwardWorld + SelfProbeSnapshot.weakFaceNormalWorld |
| 29 | `SelfWeakFaceHp` | QueryWeakFace.TotalHP / 1000 | ArmorMap |
| 30 | `SelfRecentDamageTaken1s` | sum(DamageHints.Magnitude where age<1s) / 100 | DamageHintBuffer.cs |
| 31 | `SelfRecentDamageTaken5s` | sum(... age<5s) / 500 | DamageHintBuffer |
| 32 | `SelfRecentDamageDealt1s` | RecentDamageDealtAccumulator EWMA / 100 | RecentDamageDealtAccumulator (§8.4) |
| 33 | `SelfRecentDamageDealt5s` | 5-sec EWMA / 500 | RecentDamageDealtAccumulator |
| 34 | `SelfLastAttackerDistance` | `(selfProbe.LastAttackerPosWorld − self.PositionMean).magnitude` (0 if no attacker; pos captured at the same edge-stamp as `LastAttackerSeenMono` — see slot 35) | `SelfProbeSnapshot.LastAttackerPosWorld` (captured main-thread from `TechAI.LastAttacker.tank.boundsCentreWorld` at TechAI.cs:119; `TechAI` is `TechComponent` MonoBehaviour, daemon may NOT touch) |
| 35 | `SelfLastAttackerAge` | (now − `selfProbe.LastAttackerSeenMono`) / 30 | `SelfProbeSnapshot.LastAttackerSeenMono` (long, captured via `MonoClock.Now()` at the publisher tick when `TechAI.LastAttacker` is first observed non-null this lifecycle. NOTE: `TechAI.LastAttackedTime` at TechAI.cs:131 returns `float` Unity Time.time-derived — NOT portable to MonoClock domain. The publisher edge-detects fresh-attacker transitions and stamps `MonoClock.Now()`; it does NOT cast `Time.time` to a long.) |
| **36..39** SELF MISC | | | |
| 36 | `SelfProvokedFlag` | `selfProbe.ProvokedCountdown > 0 ? 1f : 0f` | `SelfProbeSnapshot.ProvokedCountdown` (snapshotted main-thread from `TankAIHelper.cs:3117`; daemon may NOT touch the MonoBehaviour) |
| 37 | `SelfMaxAccelEstimate` | belief.MaxAccelerationEstimate / 20 | BeliefState.cs:107 |
| 38 | `SelfSpeedAtObserve` | belief.SpeedAtObserve / 50 | BeliefState.cs:106 |
| 39 | `SelfRoleHintCode` | `(int)selfProbe.SmartIdentity / 8` (enum `SmartIdentity : byte` at `Identity/SmartIdentity.cs:14`, set by `Identity.SmartIdentityClassifier.Classify(...).Identity` — the classifier returns a `SmartIdentityStamp` struct at `Identity/SmartIdentity.cs:30` whose `.Identity` member is the enum; cached on `SmartPerTechState.IdentityStamp` at `SmartRuntime.cs:63`; daemon reads only the unpacked `SmartIdentity` enum byte via SelfProbeSnapshot) | `SelfProbeSnapshot.SmartIdentity` (snapshotted main-thread) |
| **40..49** TARGET KINEMATIC | (same shape as self 0..9, target.PositionMean etc.) | derived | BeliefSnapshot |
| **50..59** TARGET VEHICLE | (same shape as self 10..19 when target is on Smart team; zeroed when not) | publisher cache | VehicleModel + publishers |
| **60..67** TARGET ENERGY + ANCHOR + CARGO | (mirrors self 20..27 when shared; else zero) | publisher cache | TechEnergy + AnchorStateCache |
| **68..75** TARGET ARMOR + DAMAGE | (same shape as self 28..35; armor fields zero for opaque enemies) | publisher cache | ArmorMap |
| **76..79** TARGET MISC | (same shape as self 36..39) | derived | BeliefState |
| **80..89** ACTION ONE-HOT | one-hot of 10 plan/action indices from PlanLibrary | publisher | PlanLibrary.PlanType + caller-supplied action |
| **90..127** RESERVED | reserved-mission-state ×10, reserved-shield-charge ×10, reserved-multi-target-context ×10, reserved-coord-handoff ×8 | future categories | n/a |

**Reserved block: 38 slots ≈ 30% headroom**, by category:

  * 90..99 (10 slots) — `reserved-mission-state`: scenario kind, scenario age, scenario
    intensity, scenario target population vs live count, etc. Filled when Director ships.
  * 100..109 (10 slots) — `reserved-shield-charge`: shield-energy ratio, shield-recharge
    rate, shield-disrupted-flag, generator-block-count. Reserved for the shield revamp.
  * 110..119 (10 slots) — `reserved-multi-target-context`: second-nearest hostile pose
    delta, second-nearest friendly pose delta, count of hostiles within K-nearest
    bucket, count of friendlies within K-nearest. Reserved for SamplingMPC's
    multi-target lookahead.
  * 120..127 (8 slots) — `reserved-coord-handoff`: external-goal source code, external
    goal age, friendly-protection target id presence, friendly distance to protectee.
    Reserved for Coordination → ActionValue handoff.

Reserved categories deliberately have headroom but **no behavioural commitment**: they
are zero-filled by `StrategicStateExtractor`. When a category graduates, the model bumps
to arch 4. Reserving the slots is the only thing that lets the bump be small.

### §3.4 Residual input layout — 48 (was 15)

`TrajectoryResidualModel.FeatureDim=48` (was 15 per `TrajectoryResidualModel.cs:32`).

`ResidualSlots`:

| Slot range | Name | Derivation | Source |
|---|---|---|---|
| **0..7** PROJECTION GEOMETRY (preserved from v0.1 + expanded) | | | |
| 0 | `DistAtFire` | distance from shooter to target at fire-time | LeadResidualRecorder |
| 1 | `ProjTimeOfFlight` | distance / muzzleVelocity | derived |
| 2 | `ElapsedSecSinceFire` | wall-clock seconds since fire | derived |
| 3..5 | `LosUnitVecXYZ` | line-of-sight unit vector at fire (already used by v0.1) | LeadResidualRecorder |
| 6 | `LosBlockedAtFire` | TerrainMap.RaycastSegment hit? | Pathing/TerrainMap.cs |
| 7 | `TargetSightCode` | Sight enum at fire-time | BeliefState.Sight |
| **8..15** SHOOTER KINEMATICS | | | |
| 8 | `ShooterSpeed` | self.VelocityMean.magnitude at fire | BeliefSnapshot |
| 9 | `ShooterHeadingSin` | sin(self.HeadingMean) | BeliefSnapshot |
| 10 | `ShooterHeadingCos` | cos(self.HeadingMean) | BeliefSnapshot |
| 11 | `ShooterAngVelZ` | rbody.angularVelocity.y at fire (publisher snapshot) | SelfStateProbe (§8.10) |
| 12 | `ShooterAccelMag` | smoothed (Vel(t) − Vel(t−Δ)).magnitude | KinematicTracker |
| 13 | `ShooterSlopeUnder` | TerrainMap.SlopeAt(self.xz) | Pathing/TerrainMap.cs |
| 14 | `ShooterHeightAboveTerrain` | derived | TerrainMap |
| 15 | `ShooterAnchored` | Anchors.NumAnchored>0? | Tank.cs:513 |
| **16..23** TARGET KINEMATICS | | | |
| 16 | `TargetSpeed` | belief.VelocityMean.magnitude | BeliefSnapshot |
| 17 | `TargetHeadingSin` | sin(belief.HeadingMean) | BeliefSnapshot |
| 18 | `TargetHeadingCos` | cos(belief.HeadingMean) | BeliefSnapshot |
| 19 | `TargetAccelEstMag` | belief.MaxAccelerationEstimate | BeliefState.cs:107 |
| 20 | `TargetAge` | belief.AgeSeconds | BeliefState |
| 21 | `TargetUncertaintyM` | belief.UncertaintyMeters | BeliefState |
| 22 | `TargetSlopeUnder` | TerrainMap.SlopeAt(target.xz) | Pathing/TerrainMap.cs |
| 23 | `TargetHeightAboveTerrain` | target.y − TerrainMap.HeightAt(target.xz) | TerrainMap |
| **24..31** WEAPON SPEC AT FIRE | | | |
| 24 | `WeaponMuzzleVel` | WeaponSpec.ProjectileVelocity | WeaponSpec.cs:27 |
| 25 | `WeaponRange` | WeaponSpec.Range | WeaponSpec.cs:26 |
| 26 | `WeaponYawArcRad` | WeaponSpec.YawArcRad | WeaponSpec.cs:30 |
| 27 | `WeaponPitchArcRad` | WeaponSpec.PitchArcRad | WeaponSpec.cs:31 |
| 28 | `WeaponIsEnergy` | WeaponSpec.IsEnergyWeapon ? 1 : 0 | WeaponSpec.cs:33 |
| 29 | `WeaponFireRateHz` | WeaponSpec.FireRateHz | WeaponSpec.cs:29 |
| 30 | `WeaponDamagePerShot` | WeaponSpec.DamagePerShot | WeaponSpec.cs:28 |
| 31 | `WeaponKindCode` | (int)WeaponKindFlag normalized | WeaponSpec.cs:24 |
| **32..34** INPUT-ONLY CACHED EXTRAP (no label leakage) | | | |
| 32..34 | `LinearExtrapXYZ` (input) | shooter's predicted impact point at fire (input feature only) | LeadResidualRecorder |
| **35..47** RESERVED | | | |
| 35..37 | reserved-projectile-physics | category: ballistics (drag-coefficient, gravity-correction-factor, mass-of-projectile) |
| 38..40 | reserved-wind-state | category: environment (wind-vector when ManTimeOfDay exposes it; today zero) |
| 41..47 | reserved-multi-shooter-context | category: cooperative-aim (delta vs nearest-allied-shooter's lead solution; 7 slots) |

**Label leakage avoided**: `ObservedResidual` is the LABEL on `ResidualEvent` (`TrajectoryResidualModel.cs:17`) — putting `ObservedResidualXYZ` inside `Features[]` would let the model copy the label to the output and achieve 100% train accuracy. The label lives ONLY on the separate `ResidualEvent.ObservedResidual` Vector3 field. `LeadResidualRecorder.cs:99` is the observe-time enqueue. **There is NO existing fire-time slot-fill site in the recorder** — `OnFireCommit` at LeadResidualRecorder.cs:48 currently stashes only `Pending {PredictedPos, OwnPos, ProjTimeSec, FireTickMono}` (LeadResidualRecorder.cs:34-40); ALL feature filling happens at observe-time inside `OnObservation` (LeadResidualRecorder.cs:79+). This plan ADDS the fire-time slot-fill by: (a) widening the `Pending` struct with a `float[] FireTimeFeatures` field (35 input dims), (b) extending `OnFireCommit`'s signature to accept the captured Features array from the caller, (c) `OnObservation` rebuilds the `ResidualEvent` with `Pending.FireTimeFeatures` as inputs and the measured residual as the separate label (no in-queue mutation). The caller (`ContinuousController` / `WeaponFireController` fire dispatch path) reads `StrategicStateBuffer.Read(self).Raw[ResidualBase..ResidualBase+34]` at fire-commit and passes the slice into `OnFireCommit`.

Total 48. **Reserved block: 13 slots ≈ 27% headroom** by category (3 ballistics + 3 wind + 7 multi-shooter).

### §3.5 Threat input layout — 48 (was 25)

`ThreatAssessmentModel.FeatureDim=48` (was 25 per `ThreatAssessmentModel.cs:29`).

`ThreatSlots`:

| Slot range | Name | Derivation | Source |
|---|---|---|---|
| **0..7** ATTACKER COMPOSITION | | | |
| 0 | `AttackerBlockCount` (norm) | blockCount / 256 | BlockManager.cs:380-387 |
| 1 | `AttackerMass` (norm) | TotalMass / 50 000 | VehicleModel.Mass |
| 2 | `AttackerWeaponCount` (norm) | / 16 | TechWeapon |
| 3 | `AttackerMeleeFraction` | meleeWeaponCount / max(1,weaponCount) | TechWeapon |
| 4 | `AttackerHpFraction` | HealthSidecar.Get(attackerId) | HealthSidecar.cs:53 |
| 5 | `AttackerAnchorState` | 0/0.5/1 | Tank.cs:513,515 |
| 6 | `AttackerRoleHintCode` | `(int)selfProbe.SmartIdentity / 8` (when attacker is Smart-team and probe is shared; else 0) | publisher cache (`SmartIdentity : byte` enum at `Identity/SmartIdentity.cs:14`; the `SmartIdentityStamp` struct at `Identity/SmartIdentity.cs:30` carries it as `.Identity` — the probe exposes only the enum byte, not the stamp) |
| 7 | `AttackerProvokedFlag` | for Smart-team attackers: `attackerSelfProbe.ProvokedCountdown > 0 ? 1f : 0f`; for opaque attackers: 0 | publisher cache (SelfStateProbe-derived; `TankAIHelper.Provoked` is MonoBehaviour and may NOT be touched on the daemon thread) |
| **8..15** ATTACKER WEAPON AGGREGATE | | | |
| 8 | `AttackerWeaponRangeMax` (norm) | max(Range)/200 | WeaponSpec.cs:26 |
| 9 | `AttackerWeaponRangeMean` (norm) | mean(Range)/200 | WeaponSpec |
| 10 | `AttackerWeaponFireRateMean` | mean(FireRateHz)/5 | WeaponSpec.cs:29 |
| 11 | `AttackerWeaponDamagePerShotMean` | mean(DamagePerShot)/100 | WeaponSpec.cs:28 |
| 12 | `AttackerWeaponMuzzleVelMean` | mean(ProjectileVelocity)/300 | WeaponSpec.cs:27 |
| 13 | `AttackerEnergyWeaponFraction` | count(IsEnergyWeapon)/max(1,count) | WeaponSpec.cs:33 |
| 14 | `AttackerWeaponKindMix_Gun` | fraction of Kind==GunFixed/GunTurret | WeaponSpec.cs:24 |
| 15 | `AttackerWeaponKindMix_BeamMelee` | fraction of Kind==Beam/Melee | WeaponSpec |
| **16..23** ATTACKER KINEMATIC | | | |
| 16 | `AttackerSpeed` (norm) | / 50 | BeliefSnapshot |
| 17 | `AttackerMaxAccelEstimate` | / 20 | BeliefState.cs:107 |
| 18 | `AttackerSlopeUnder` | TerrainMap.SlopeAt | TerrainMap |
| 19 | `AttackerHeightAboveTerrain` | derived | TerrainMap |
| 20 | `AttackerWaterloggedFrac` | clamp(`selfProbe.WaterHeight` − y, 0, 5)/5 — when attacker is Smart-team; else 0 | publisher cache (KickStart.WaterHeight is Unity-touching; daemon reads via SelfProbeSnapshot) |
| 21 | `AttackerCargoFill` | NumContents / capacity | ModuleItemHolder |
| 22 | `AttackerReadyFireFraction` | ReadyToFire-count / WeaponCount | WeaponReadinessHelper (§8.1) |
| 23 | `AttackerEnergyStored` (norm) | TechEnergy.Energy(Electric).currentAmount / 10 000 (when attacker is Smart-team; else 0) | TechEnergy.cs:41 (`currentAmount`, NOT `Stored`) |
| **24..31** ENGAGEMENT GEOMETRY | | | |
| 24 | `DistanceToVictim` | derived | BeliefSnapshot |
| 25 | `AttackerHeadingTowardVictimDot` | dot(attacker.ForwardXZ, los) | BeliefSnapshot.ForwardXZ |
| 26 | `VictimWeakFaceTowardAttackerDot` | dot(victim weakFaceNormalWorld, −los) — `weakFaceNormalWorld` precomputed by SelfStateProbe via `rootBlockTrans.rotation * FaceNormalLocal` (ArmorMap exposes `FaceNormalLocal` only, ArmorMap.cs:9) | publisher cache (SelfProbeSnapshot) |
| 27 | `LosBlockedToVictim` | TerrainMap.RaycastSegment | TerrainMap |
| 28 | `AttackerWeaponInRange` | distance ≤ AttackerWeaponRangeMax | derived |
| 29 | `AttackerAimableFlag` | distance ≤ AttackerWeaponRangeMax AND ¬LosBlocked AND face-in-arc | derived |
| 30 | `VictimWeakFaceHp` (norm) | weakFace.TotalHP / 1000 | ArmorMap |
| 31 | `VictimHpFraction` | HealthSidecar.Get(victim) | HealthSidecar.cs:53 |
| **32..37** RECENT-DAMAGE WINDOW (1-sec ring) | | | |
| 32 | `RecentDamageDealt1s` | `RecentDamageDealtAccumulator.SumWithin(attacker, nowMono, 1.0)` (new API — see §8.4) | RecentDamageDealtAccumulator (§8.4 new file declares the API) |
| 33 | `RecentDamageDealt5s` | `SumWithin(attacker, nowMono, 5.0)` | RecentDamageDealtAccumulator (§8.4) |
| 34 | `RecentShotsFired1s` | `WeaponFireBuffer.CountWithin(attacker, nowMono, 1.0)` (new API — see §8.7); since `TechWeapon.WeaponsFiredEvent` is `EventNoParams` (TechWeapon.cs:145, no payload), each event counts as 1 fire-tick — NOT per-barrel shot count | WeaponFireBuffer (§8.7 new file declares the API) |
| 35 | `RecentDamageDealtTypeCode` | dominant DamageType byte / 8 (from DamageHint extended with DamageType byte; §8.5 delta) | DamageHintBuffer + §8.5 byte plumbed via `info.DamageType` (ManDamage.DamageInfo.DamageType at ManDamage.cs:58) — NOT `Damageable.DamageableType` (which is per-block, not per-tech) |
| 36 | `RecentDamageTakenByVictim1s` | `DamageHintBuffer.SumWithin(victim, nowMono, 1.0)` (new API — see §8.5 delta) | DamageHintBuffer (§8.5 delta declares the API; existing surface is only `TryGetRecent` at DamageHintBuffer.cs:150) |
| 37 | `RecentDamageTakenByVictim5s` | `DamageHintBuffer.SumWithin(victim, nowMono, 5.0)` | DamageHintBuffer (§8.5 delta) |
| **38..47** RESERVED | | | |
| 38..41 | reserved-mission-state | category: scenario kind/age/intensity/role-pressure |
| 42..44 | reserved-team-context | category: count-of-allies-in-K-nearest, team-relations-with-target-team, sub-neutral-flag |
| 45..47 | reserved-shield-charge | category: defensive-systems (shield ratio, recharge rate, presence-flag) |

Total 48. **Reserved block: 10 slots ≈ 21% headroom** by category.

### §3.6 Reserved-slot ownership rule

Every reserved slot in §3.2-§3.5 carries a documented **category**, not a "future
phase" deferral. Categories are concrete labels for the kind of signal the slot is
held for:

  * `reserved-mission-state` — fillable once Director's `ActiveScenario` becomes a
    per-tech feature. Specific source identified (`Smart/Director/DirectorState.cs`
    when shipped). Until then: zero.
  * `reserved-shield-charge` — fillable when shield modules expose energy state through
    a publisher. Specific source identified (TechEnergy + ModuleShieldGenerator).
  * `reserved-multi-target-context` — fillable when K-nearest publisher exposes second-
    rank entries. Slot owners flagged so the bump that wires them is a one-line edit
    per slot.
  * `reserved-multi-shooter-context` — fillable when LeadResidualRecorder publishes
    cooperative-aim deltas.
  * `reserved-projectile-physics` — fillable when WeaponSpec exposes drag-coefficient
    and projectile-mass (today not reflected; spec-reflection extension needed).
  * `reserved-wind-state` — fillable when ManTimeOfDay or terrain biome exposes wind.
  * `reserved-coord-handoff` — fillable when Coordination's PublishGoals carries source
    metadata (today only the goal, not its provenance).
  * `reserved-team-context` — fillable when team-relations + sub-neutral data is
    surfaced per-tick.
  * (Note: the prior "reserved-ballistics" category has been merged into
    `reserved-projectile-physics` — they were the same physics category. No §3.4 slot
    bank uses the bare "reserved-ballistics" label.)

If a future plan can name the source for a category, the slot graduates. If it cannot
name a source, the slot stays reserved. **No reserved slot is held against a "v0.3" or
"future phase" label.** This is the discipline carry-over from the Director plan's
v0.3-deferral cleanup (Director plan line 3, "OverdueDelivery telemetry-only
reconciliation pass").

---

## §4. StrategicStateExtractor

### §4.1 Threading model

`StrategicStateExtractor` is a **single background daemon** owned by `SmartRuntime`,
modeled on `AetherFuser` (see `AetherFuser.cs:25-60` for the class template;
`AetherFuser.cs:62-154` is the `Tick(CancellationToken)` body; the worker loop is
`AetherFuser.RunLoop` at `AetherFuser.cs:191`). Threading rules:

  * Own thread, host-only (gated `SmartRuntime.IsHost && !SmartRuntime.IsPaused` same
    as `AetherFuser.RunLoop`).
  * Target cadence **~30 Hz** (33 ms tick budget, same as Aether — chosen to feed
    Director's 1 Hz aggregation comfortably while staying inside per-tech CPU budget).
  * Reads **only snapshots**: BeliefSnapshot via the public accessor
    `WorldModel.FusedBuffer.Read()` (`WorldModel.cs:51` — `AetherFuser._snapshotBuffer`
    is `private readonly`), `VehicleBuffer.Read()`, `KinematicBuffer.Read()`, TerrainMap
    queries (snapshot reads are any-thread per `Pathing/TerrainMap.cs:54-60`; sampling
    is main-thread only via `RefreshFromMainThread`; the daemon must also gate on
    `!TerrainMapSnapshot.IsFreshlyAllocated`), ArmorMap query (any-thread per
    chassis-immutability — ArmorMap is constructed once per Attach/Detach and never
    mutated), per-tech `SelfStateProbe` slot (§5.1) — which includes Provoked, Last-
    Attacker pose, TimeOfDayHour, WaterHeight, SmartIdentity, all face-normal-in-world
    rotated values, all snapshotted main-thread.
  * **Never touches**: Unity `Component` instances (TankAIHelper, TechAI, Tank,
    Transform, Rigidbody, Collider), `Physics.OverlapSphere`, `Singleton.Manager<T>.inst`
    (incl. `ManTimeOfDay.inst`), `KickStart.WaterHeight` (which calls into `WaterMod.QPatch`),
    `ManTechs`, transforms/rigidbodies/colliders.
  * Cancellation pattern via `CancellationToken` matches `AetherFuser.RunLoop`
    cancellation contract.
  * Lifecycle via `WorkerPool.EnqueueLongRunning(..., "StrategicStateExtractor")`
    (`WorkerPool.cs:127`) + `DaemonWatchdog.RegisterCanonical("StrategicStateExtractor",
    factory)` for free auto-respawn. As with Director plan §2 BLOCKER, the canonical
    name must ALSO be appended to `DaemonWatchdog.CanonicalRoster`.

`AetherFuser` is the canonical template. The extractor differs in **what it produces**:
Aether publishes one fused `BeliefSnapshot`; the extractor publishes a per-tech
`StrategicStateVector` via a per-tech `DoubleBuffer<StrategicStateVector>` (§4.4).

### §4.2 Per-tech work loop

Each daemon tick:

```
nowMono = MonoClock.Now();
snapshot = world.FusedBuffer.Read();      // immutable BeliefSnapshot; WorldModel.cs:51
                                          //   (NOT AetherFuser._snapshotBuffer — private)
// NOTE: `TerrainMap` itself implements `ITerrainMap` (HeightAt/SlopeAt/RaycastSegment are
// instance methods on `TerrainMap` per Pathing/TerrainMap.cs:218,256,275). There is NO
// `QuerySnapshot()` method; `TerrainMapSnapshot` exposes only `IsPopulated`/`HeightSamples`.
// `IsFreshlyAllocated` is on `TerrainMap` (Pathing/TerrainMap.cs:104), not the snapshot.
// Any-thread query contract documented at Pathing/TerrainMap.cs:54-60 — daemon calls the
// `TerrainMap` instance methods directly and gates on `terrainMap.IsFreshlyAllocated`.
if (terrainMap.IsFreshlyAllocated) skip;  // height grid empty until first MainThreadTick

for each smartTech registered with SmartRuntime:
    BeliefState selfBelief;
    if (!snapshot.ByTech.TryGetValue(techId, out selfBelief)) continue;
                                          // IReadOnlyDictionary; BeliefState.cs:304
    selfProbe = selfProbeBuffer.Read(techId);  // DoubleBuffer<SelfProbeSnapshot> per tech
    if (selfProbe == null) continue;           // not yet published this lifecycle

    // 1) Find K-nearest hostile + K-nearest friendly via NearestTechCache (§8.3).
    //    Instance helper handed in via SmartRuntime registry (no static singletons).
    nearestHostile  = nearestTechCache.NearestHostile(techId, snapshot, k=1)
    nearestFriendly = nearestTechCache.NearestFriendly(techId, snapshot, k=1)

    // 2) Sample terrain at three points: self.xz, target.xz, midpoint.
    //    Pseudocode below uses `.xz` for brevity; implementation materializes a Vector2
    //    explicitly (UnityEngine.Vector3 has no `.xz` swizzle — Vector2 only has limited
    //    swizzles in newer Unity; we are on 4.6.1). e.g.:
    //      `new Vector2(selfBelief.PositionMean.x, selfBelief.PositionMean.z)`
    selfSlope   = terrainMap.SlopeAt(selfBelief.PositionMean.xz)
    selfHeight  = selfBelief.PositionMean.y - terrainMap.HeightAt(selfBelief.PositionMean.xz)
    if nearestHostile != null:
        tgtSlope  = terrainMap.SlopeAt(nearestHostile.PositionMean.xz)
        losBlocked = terrainMap.RaycastSegment(selfBelief.PositionMean, nearestHostile.PositionMean)
    else:
        tgtSlope, losBlocked = 0, 0

    // 3) ArmorMap query for self's weak-face. ArmorMap is immutable-per-chassis
    //    (rebuilt on Attach/Detach by SelfStateProbe and republished into
    //    SelfProbeSnapshot.Armor — see §5.1 field list).
    armorMap = selfProbe.Armor          // ArmorMap reference; any-thread read
    weak = armorMap.QueryWeakFace(localAttackDir)
    selfWeakFaceHp = weak.TotalHP

    // 4) Weapon match-ups: read self.WeaponAggregate from selfProbe (§5.1) plus
    //    target.WeaponAggregate from publisher cache if target is on Smart team.
    selfWeapAgg = selfProbe.WeaponAggregate
    tgtWeapAgg  = nearestHostile != null
                  ? targetPublisherCache.TryGetWeaponAgg(nearestHostile.Id)
                  : default

    // 5) Recent-damage windows from sidecars.
    //    NOTE: SumWithin / CountWithin are NEW instance APIs declared in §8.4 / §8.5
    //    delta / §8.7 — today DamageHintBuffer exposes only TryGetRecent at
    //    DamageHintBuffer.cs:150 (lock-protected copy-out). The new methods walk the
    //    same ring under the same lock and sum/count entries whose tickMono falls
    //    inside the window.
    dmg1s   = damageHintBuffer.SumWithin(techId, nowMono, 1.0)      // §8.5 DELTA
    dmg5s   = damageHintBuffer.SumWithin(techId, nowMono, 5.0)      // §8.5 DELTA
    dealt1s = dealtAccumulator.SumWithin(techId, nowMono, 1.0)      // §8.4 NEW
    dealt5s = dealtAccumulator.SumWithin(techId, nowMono, 5.0)      // §8.4 NEW
    shots1s = weaponFireBuffer.CountWithin(techId, nowMono, 1.0)    // §8.7 NEW; 1-per-event
                                                                    //   (WeaponsFiredEvent
                                                                    //    is EventNoParams)

    // 6) Build the vector.
    vec = new StrategicStateVector(techId, team, nowMono, selfProbe.Generation)
    FillIntentSlots(vec.Raw, selfBelief, nearestHostile, selfProbe, ...) // 40 dims; §3.2
    FillActionValueSlots(vec.Raw, selfBelief, selfProbe, ...)            // 128 dims; §3.3
    FillResidualSlots(vec.Raw, /* fired-time only */)                    // 35 input dims;
                                                                         // §3.4 (no label)
    FillThreatSlots(vec.Raw, selfBelief, attackerCache, ...)             // 48 dims; §3.5

    // 7) NaN/Inf scrub (mirrors ContinuousController.cs:250-255) + atomic publish.
    ScrubNonFinite(vec.Raw)
    strategicStateBuffer.Write(techId, vec)
```

NOTE: Per §3.4, Residual `Features[]` contains **inputs only** — `[0..31]` projection
geometry / kinematics / weapon spec, `[32..34]` `LinearExtrapXYZ` (cached input), and
`[35..47]` reserved (zero on forward). The LABEL is the separate
`ResidualEvent.ObservedResidual` Vector3 field (`TrajectoryResidualModel.cs:17`); it
is NEVER embedded inside `Features[]` (label leakage). The new flow widens
`LeadResidualRecorder.Pending` with a `float[] FireTimeFeatures` field;
`OnFireCommit` (LeadResidualRecorder.cs:48) gains a `float[]` parameter and stashes
the caller-provided array; `OnObservation` at LeadResidualRecorder.cs:99 enqueues
`new ResidualEvent(target, Pending.FireTimeFeatures, residual)` — one enqueue, label
and inputs cleanly separated. The fire-time site does NOT exist today; this plan
adds it.

### §4.3 Derivation table

Single table of all features that require computation beyond a direct snapshot read:

All slot indices below are **view-local** (i.e. AV[k] addresses
`Raw[ActionValueBase + k]`; see §3.1). Per §3.3, target-block in AV is `[40..79]`.

| Feature | Inputs | Formula | Target slot(s) |
|---|---|---|---|
| `RelPos` | self.PositionMean, target.PositionMean | `(target - self).xz` | Intent[0..1]; derived for AV from AV[0..1] (self pos) and AV[40..41] (target pos) at consume time |
| `RelHeading` | self.HeadingMean, target.HeadingMean | `(sin,cos)(target - self)` | Intent[2..3] |
| `RelVel` | self.VelocityMean, target.VelocityMean | `(target - self).xz` | Intent[4..5] |
| `Distance` | self.PositionMean, target.PositionMean | `magnitude` | Intent[10], Threat[24] (no dedicated AV slot — derived at consume time from AV[0..1] vs AV[40..41]) |
| `LosBlocked` | self.PositionMean, target.PositionMean, TerrainMap | `RaycastSegment(self, target)` returns hit? | Intent[11], Threat[27] (AV target-block has no dedicated LOS slot today) |
| `WeakFaceForwardDot` | `selfProbe.forwardWorld`, `selfProbe.weakFaceNormalWorld` | `dot(forwardWorld, normalWorld)` — both precomputed main-thread in SelfStateProbe (daemon may NOT touch `rootBlockTrans` Unity Transform) | AV[28] |
| `RecentDamageTakenEwmaWindow` | DamageHintBuffer per-victim ring (`SumWithin` NEW §8.5) | `sum(magnitude where (now-tickMono) < windowSec)` | AV[30..31], Threat[36..37], Intent[21] |
| `RecentDamageDealtEwma` | RecentDamageDealtAccumulator (`SumWithin` NEW §8.4) | symmetric | AV[32..33], Threat[32..33], Intent[20] |
| `RecentShotsFiredEwma` | WeaponFireBuffer (`CountWithin` NEW §8.7); `WeaponsFiredEvent` is `EventNoParams` so 1 event = 1 fire-tick (not per-barrel shot count) | symmetric | Threat[34], Intent[32] (AV reserved-multi-shooter; no dedicated slot today) |
| `DominantDamageType` | DamageHintBuffer with DamageType byte (§8.5); type comes from `info.DamageType` (ManDamage.cs:58 — `DamageInfo.DamageType` enum) NOT per-block `DamageableType` | mode over last-N entries | Threat[35], Intent[22..23] (NO collision with AV[35] — AV[35] is `SelfLastAttackerAge`; DominantDamageType is not written into the AV self-block) |
| `WeaponRangeMax/Mean` | per-tech WeaponSpec list (cached) | reduce | AV[14], Threat[8..9], Intent[30..31] |
| `WeaponFireRateMean` | WeaponSpec.FireRateHz | mean | AV[15], Threat[10], Intent[28..29] |
| `WeaponDamagePerShotMean` | WeaponSpec.DamagePerShot | mean | AV[16], Threat[11] |
| `WeaponMuzzleVelMean` | WeaponSpec.ProjectileVelocity | mean | AV[17], Threat[12] |
| `ReadyFireFraction` | WeaponReadinessHelper (§8.1) | ready / total | AV[19], Threat[22] |
| `IsGeneratingFraction` | ModuleEnergy iteration on main thread (cached) | count(IsGenerating) / total | AV[23] |
| `ElectricStored / SpareCapacity / NetFlow` | `TechEnergy.Energy(Electric)` snapshot — `currentAmount`, `spareCapacity`, `(currentAmount − previousAmount)` (no `Stored` / `NetFlow` fields exist) | direct + derived | AV[20..22] |
| `AnchorStateCode` | `selfProbe.AnchorState` (publisher-derived from `Tank.cs:513,515` and `Tank.AnchorEvent`) | `0=floating, 0.5=anchored, 1=sky-anchored` | AV[24], Threat[5], Intent[24..25] |
| `CargoFill / Value / BlockCount` | CargoStatePublisher (§8.6) | direct | AV[25..27] |
| `LastAttackerDistance / Age` | `selfProbe.LastAttackerPosWorld`, `selfProbe.LastAttackerSeenMono` (main-thread captured: `LastAttackerPosWorld` from `TechAI.LastAttacker.tank.boundsCentreWorldNoCheck`; `LastAttackerSeenMono` from `MonoClock.Now()` on attacker rising-edge — NOT a cast of `TechAI.LastAttackedTime` which is `float Time.time`-based; daemon may NOT touch `TechAI` MonoBehaviour) | derived | AV[34..35] |
| `WaterloggedFrac` | `selfProbe.WaterHeight`, self.y | `clamp((WaterHeight - y)/5, 0, 1)` | AV[9], Threat[20] |
| `MaxAccelEstimate` | belief.MaxAccelerationEstimate | direct (norm/20) | AV[37], Residual[19] (Target side), Threat[17] |
| `SightCode` | belief.Sight (`BeliefState.cs:108`) | enum → {1.0, 0.5, 0.25, 0.0} | Intent[9], Residual[7] |
| `TimeOfDayCosine` | `selfProbe.TimeOfDayHour` (main-thread snap of `ManTimeOfDay.inst.TimeOfDayPrecise`; Singleton, daemon may NOT touch) | `cos(2π · hour / 24)` | Intent[34] |
| `ProvokedFlag` | `selfProbe.ProvokedCountdown` (main-thread snap of `TankAIHelper.Provoked` int; MonoBehaviour, daemon may NOT touch) | `countdown > 0 ? 1 : 0` | Intent[35], AV[36], Threat[7] |
| `SmartIdentityCode` | `selfProbe.SmartIdentity` enum (set by `Identity.SmartIdentityClassifier.Classify`) | `(int)id / 8` | Intent[39] (placeholder), AV[39], Threat[6] |

All formulas are pure functions of snapshots — none mutate state.

### §4.4 Publication protocol

`Smart/Learning/Features/StrategicStateBuffer.cs` — per-tech double-buffer registry.

```
public sealed class StrategicStateBuffer
{
    private readonly ConcurrentDictionary<TechId, DoubleBuffer<StrategicStateVector>> _byTech
        = new ConcurrentDictionary<TechId, DoubleBuffer<StrategicStateVector>>();

    public void Write(TechId id, StrategicStateVector vec)
    {
        var buf = _byTech.GetOrAdd(id, _ => new DoubleBuffer<StrategicStateVector>(null));
        buf.Write(vec);
    }

    public StrategicStateVector Read(TechId id)
    {
        DoubleBuffer<StrategicStateVector> buf;
        if (!_byTech.TryGetValue(id, out buf)) return null;
        return buf.Read();
    }

    internal void Forget(TechId id)
    {
        DoubleBuffer<StrategicStateVector> _;
        _byTech.TryRemove(id, out _);
    }
}
```

`DoubleBuffer<T>` is the canonical primitive (`Threading/DoubleBuffer.cs:27-44`):
single `Interlocked.Exchange` on Write, `Volatile.Read` on Read, full-fence semantics.
The published `StrategicStateVector` is **immutable by discipline** — the extractor
constructs a fresh instance per tick and never mutates one that has been Written.

Lifecycle:

  * Allocation hooked from `TeamRuntime.RegisterTech(SmartPerTechState)`
    (`SmartRuntime.cs:393`) — there is no `OnTechRegistered` event; per-tech state is
    constructed by the registry call directly, mirroring how `KinematicTracker` is
    constructed alongside per-tech state at `SmartRuntime.cs:86`. The
    `StrategicStateBuffer` does not subscribe to spawn — the daemon discovers new
    techs via `snapshot.ByTech` enumeration and lazily creates the per-tech
    `DoubleBuffer` on first `Write` (see `GetOrAdd` above).
  * Unsubscribe / Forget on `WorldModel.DeregisterTech`, same as DamageHintBuffer's
    Forget hookup (`DamageHintBuffer.cs:163`).
  * Wire-template for event-driven sidecars that DO need a Subscribe (DamageHintBuffer
    et al.): `DamageHintBuffer.Wire` (`DamageHintBuffer.cs:108`) / `Unwire`
    (`DamageHintBuffer.cs:117`) — the correct subscribe/unsubscribe template.
    `HealthSidecar.cs:35-40` is the `Name`/`SnapshotKeys`/`Record` declarations and is
    NOT a wire pattern.

Readers:

  * `LearningService` reads the per-tech vector when assembling typed events. The
    Intent tee point at `TargetObservationSequenceBuffer.cs:186` reads the 30 most
    recent per-target vectors and slices the 40-dim intent-window from each. The
    ActionValue producer (new in §9 — `ContinuousController.cs` modifications)
    reads the per-self vector and the per-action one-hot. The Threat tee point at
    `LearningService.cs:694` reads attacker-side StrategicStateBuffer slots —
    `Read(attackerId)` for ATTACKER-side slots 0..23 (composition / weapon / kinematic)
    plus 32..35 (per-attacker recent-damage / recent-shots windows) per §3.5; and
    `Read(victimId)` for victim-side slots 30, 31, 36, 37. Note: slot 35
    (`RecentDamageDealtTypeCode`) is attacker-side (the damage type the attacker dealt);
    slots 30..31 (`VictimWeakFaceHp` / `VictimHpFraction`) are the only victim slots
    inside the 0..35 range. The Residual fire-time capture path is NEW — see §3.4
    closing note: `OnFireCommit` (LeadResidualRecorder.cs:48) is widened with a
    `float[] FireTimeFeatures` parameter; the caller (ContinuousController/WeaponFireController
    fire dispatch) reads slots `[0..34]` of the per-self vector at fire and passes the
    array in; observe-arrival at LeadResidualRecorder.cs:99 enqueues a single
    `ResidualEvent(targetId, capturedFeatures, observedResidual)` — no in-queue
    mutation, label and inputs cleanly separated.

---

## §5. Main-thread publishers

### §5.1 Per-tech `SelfStateProbe` (FixedUpdate cadence)

`Smart/Learning/Features/SelfStateProbe.cs` — one instance per tech, ticked on the
main thread inside `SmartForm.Operations(helper, host)`. Owned by `SmartPerTechState`,
constructed alongside `KinematicTracker` (`SmartRuntime.cs:86`).

**Cadence**: gated to the existing `SmartForm.ObserveWorldTechsIfDue` 33 ms / ~30 Hz
throttle (SmartForm.cs:770-776; `ObservationIntervalMs = 33`) to avoid double-walk.
`SmartForm.Operations` itself runs at FixedUpdate (~50 Hz), but the publisher's
`Tick` body is gated behind the 33 ms throttle so SelfStateProbe effectively
publishes at ~30 Hz — matching the daemon's read cadence.

**What it snapshots** (only Unity-touching reads — every field below is published as an
immutable `SelfProbeSnapshot` for the background daemon):

  * `tank.boundsCentreWorldNoCheck` (cheap, no NRE on attached techs)
  * `tank.rbody.velocity, angularVelocity, angularVelocity.y, mass` (cached on attach/detach)
  * `tank.Anchors.NumAnchored` + `tank.IsSkyAnchored` (Tank.cs:513,515) — published as
    `AnchorState` byte (0/1/2)
  * `tank.rootBlockTrans.forward` → `forwardWorld` (Vector3, world-frame)
  * `tank.rootBlockTrans.rotation` cached per tick; used to rotate
    `ArmorMap.QueryWeakFace(...).FaceNormalLocal` → `weakFaceNormalWorld` for the
    daemon's dot product (daemon may NOT touch `rootBlockTrans` itself)
  * Per-weapon `ModuleWeapon.m_RotateSpeed`, `m_ShotCooldown` (from
    `WeaponReflectionCache`); ReadyToFire via `WeaponReadinessHelper` (§8.1) —
    methods, NOT properties (`IModuleWeapon.ReadyToFire()` / `FiringObstructed()`)
  * `TechEnergy.Energy(Electric)` snapshot (`currentAmount`, `spareCapacity`,
    `previousAmount` — NOT `Stored` / `NetFlow` which do not exist)
  * `tank.blockman.blockCount` (cached, invalidated on AttachEvent/DetachEvent;
    `tank.blockCount` does NOT exist as a Tank field — access via `blockman`)
  * Aggregate weapon stats over the cached WeaponSpec list
  * `helper.Provoked` → `ProvokedCountdown` (`int` countdown, not `bool`;
    `TankAIHelper.cs:3117`)
  * `helper.lastEnemyGet` + `TechAI.LastAttacker` (`TechAI.cs:119`, returns `Visible`)
    → `LastAttackerPosWorld` (Vector3 from `attacker.tank.boundsCentreWorldNoCheck`,
    or zero if no attacker) and `LastAttackerSeenMono` (long, captured via
    `MonoClock.Now()` on the edge when `TechAI.LastAttacker` transitions to non-null;
    NOT a cast of `TechAI.LastAttackedTime` at `TechAI.cs:131` — that returns `float`
    Unity `Time.time` seconds, which is not portable to MonoClock domain. The publisher
    keeps a `bool _hadAttacker` latch and stamps `MonoClock.Now()` on the rising edge.)
  * `ManTimeOfDay.inst.TimeOfDayPrecise` → `TimeOfDayHour` (float ∈ [0,24);
    `Singleton.Manager<T>` access stays main-thread)
  * `KickStart.WaterHeight` → `WaterHeight` (calls `WaterMod.QPatch.WaterHeight` —
    third-party mod Unity touch, must stay main-thread)
  * `Identity.SmartIdentityClassifier.Classify(...).Identity` → `SmartIdentity` enum
    (cached at `SmartPerTechState` ctor + refreshed on Attach/Detach via the
    invalidation hook; daemon reads the published enum value)
  * `ArmorMap` reference (immutable per-Attach/Detach instance) → published as `Armor`
    field so the daemon can call `armor.QueryWeakFace(localDir)` any-thread

**Atomic-write mechanism**: a single immutable `SelfProbeSnapshot` class with all
fields readonly, published through a per-tech `DoubleBuffer<SelfProbeSnapshot>` slot
on the `SmartPerTechState` (same pattern as `VehicleBuffer` at `SmartRuntime.cs:88`).
Background daemon reads via `DoubleBuffer.Read()`.

**Lifecycle**: subscribed at `SmartRuntime.RegisterTech` (Init order: SelfStateProbe
slot allocated in `SmartPerTechState` constructor; first publish on first
`SmartForm.Operations` tick). Unsubscribed on `WorldModel.DeregisterTech`. Static
cache invalidation on Attach/Detach (`Tank.cs:264, 513`) covered in §5.3.

### §5.2 Event-driven sidecars

One row each.

| Sidecar | Subscribes to | Stores | Per-tick cost |
|---|---|---|---|
| `DamageHintBuffer` extension (§8.5) | `WorldEventBus<DamageObserved>` (`DamageHintBuffer.cs:108`) | adds `DamageType byte` to each `DamageHint` (extending the existing 6-field `readonly struct` at `DamageHintBuffer.cs:12-37` — the struct already carries Magnitude, TickMono, AttackerIfKnown, HasAttacker, ImpactPositionWorld, ImpactDirectionWorld from P11 T6 Item 68; DamageType is the 7th field; ctor at :27 widened to 7 args); existing ring stays 8-deep (`CapacityPerTech = 8` at DamageHintBuffer.cs:62); **adds new `SumWithin(victim, nowMono, windowSec)` instance method** that walks the ring under the existing lock and sums magnitudes whose `(now - tickMono)/MonoFreq < windowSec` | unchanged (~O(1) per event) |
| `WeaponFireBuffer` (§8.7) | `TechWeapon.WeaponsFiredEvent` at `TechWeapon.cs:145` (type is `EventNoParams` — NO shot-count payload; per `Send()` call counts as **one fire-tick** regardless of how many barrels fired) | per-tech ring of `tickMono` 16-deep; adds `CountWithin(tech, nowMono, windowSec)` instance method | ~O(1) per fire event |
| `CargoStatePublisher` (§8.6) | `TechHolders.ItemPickupEvent` + `ItemReleaseEvent` (`TechHolders.cs:163,165`) | per-tech `(numContents, totalCapacity, accumulatedValue, lastUpdateMono)` | ~O(holders) on event |
| `RecentDamageDealtAccumulator` (§8.4) | `WorldEventBus<DamageObserved>` (same path as DamageHintBuffer) | per-attacker ring of (magnitude, type, tickMono) 16-deep — symmetric to DamageHintBuffer | ~O(1) per event |
| `AnchorStateCache` | `Tank.AnchorEvent` (existing on `Tank.cs`) | per-tech `(numAnchored, isSkyAnchored, lastChangeMono)`; refreshes on Anchors.AnchorEvent fire | ~O(1) per event |

All sidecars are subscribed at `SmartRuntime.Init` time (same wire pattern as
`DamageHintBuffer.Wire` at `DamageHintBuffer.cs:108-115`). All subscriber delegates are
held in `static` fields to prevent GC reclaim. All sidecars expose a `Forget(TechId)`
call that runs on `WorldModel.DeregisterTech`.

**Cross-tank attribution for DamageHintBuffer + RecentDamageDealtAccumulator**: the
DamageObserved event already carries `AttackerIfKnown` (see `DamageHintBuffer.cs:131-136`
producer line). The dealt-accumulator subscribes to the same event and indexes by
attacker side, not victim side. The producer at `SmartEventBridge.cs:349` calls
`WorldEventBus.Publish` synchronously (NOT `PublishFromWorker`); subscribers happen
to run on the main thread because `OnTankDamage` itself is invoked by the engine's
main-thread Damageable hook. The `PublishFromWorker` / `DrainMainThreadQueue` pipe
(at `EventBus.cs:269-294`) is the mechanism used when a true background producer
needs to deliver onto the main-thread queue; it is not what carries DamageObserved
today.

### §5.3 Cache invalidation on AttachEvent / DetachEvent

`Tank.AttachEvent` (`Tank.cs:262`) and `Tank.DetachEvent` (`Tank.cs:264`) fire on
every block attach/detach. Multiple per-tech aggregates are invalidated by these
events:

| Cache | Invalidates on | New value |
|---|---|---|
| Per-tech WeaponSpec list aggregate (range/firerate/damage means) | AttachEvent + DetachEvent | recomputed by iterating Tank's weapon blocks via existing `WeaponProfileBuilder` (static class inside `Smart/Vehicle/WeaponProfile.cs:72` — there is no standalone `WeaponProfileBuilder.cs` file) |
| ArmorMap | AttachEvent + DetachEvent | recomputed via `ArmorMap.Compute(poses, catalog, res)` |
| `tank.blockCount` cache | uses the engine's existing field (see `BlockManager.cs:380-387`); no Smart cache needed since the engine asserts `m_RemoveBlockRecursionCounter==0` and the value is cheap |
| Per-tech energy module count | AttachEvent + DetachEvent | iterate `BlockManager.IterateBlockComponents<ModuleEnergy>` to count generators |
| Per-tech hover/space classification | AttachEvent + DetachEvent | uses `HoverReflection.cs` cached probe |

Invalidation hook lives on `SmartPerTechState` — a single delegate registered in the
state's ctor that re-flags an internal `_aggregatesDirty` flag. The next
`SmartForm.Operations` tick re-computes lazily. **The invalidation hook MUST NOT
re-compute synchronously inside the event handler** because Tank's
`m_RemoveBlockRecursionCounter > 0` window (`BlockManager.cs:893-929`) is held when
DetachEvent fires; calling expensive recompute under that lock can produce confusing
recursive-detach paths.

### §5.4 Estimated per-tick main-thread cost per tech

Rough budget at the gated 30 Hz publish cadence (33 ms `ObserveWorldTechsIfDue`
throttle):

  * SelfStateProbe snapshot: ~12 field reads + 1 WeaponAggregate reduce + 1 DoubleBuffer.Write
    ≈ **3-5 µs** per tech per tick.
  * Sidecar event handling: dominated by DamageObserved (~50 µs amortized at typical
    50 dmg-events/sec in a 12-tech engagement). Per-tech amortized ≈ **<1 µs**.
  * Cache invalidation amortized over Attach/Detach rate (~10 events/sec under load):
    re-iteration of WeaponSpec ≈ **100 µs per Attach/Detach event**, ~10/sec ⇒
    amortized **<2 µs per tick per tech**.

**Main-thread budget per tech per publish: ≈ 10 µs.** Twelve techs ≈ 120 µs/publish — well
under the 33 ms publish budget. Twenty techs ≈ 200 µs/publish. Cost-dominant is the SelfProbe;
sidecars are event-rate not tech-count.

---

## §6. Threading contract

### §6.1 Tier table

| Tier | Where | Who runs there | Examples |
|---|---|---|---|
| **Main hot** | Unity FixedUpdate / Update / SmartForm.Operations | one thread | SelfStateProbe.Tick, sidecar event handlers, cache invalidation, BeliefSnapshot intake-slot writers |
| **Main deferred** | end-of-tick `WorldEventBus.DrainMainThreadQueue` (EventBus.cs:280) | one thread | sidecar subscribers (delegates were PublishFromWorker'd onto the queue) |
| **Background hot** | `StrategicStateExtractor` daemon | one thread, ~30 Hz | per-tech state extraction, NearestTechCache query, TerrainMap query, ArmorMap query, vector pack + publish |
| **Background cold** | `AetherFuser` daemon, `Coordinator` daemon, etc. | several threads | belief fusion, plan/coord, path planning |

Tier discipline rules:

  * Main hot writes; Background hot reads. NO main-hot reads of per-tech vectors —
    that would invert the publish boundary.
  * Background hot NEVER touches Unity Components. Verified by code-review against
    the §6.5 failure-mode list.
  * Sidecar event handlers run on main thread (via WorldEventBus's
    PublishFromWorker → DrainMainThreadQueue pattern at `EventBus.cs:269-294`).

### §6.2 Snapshot publish/read protocol

```
Main thread (FixedUpdate):
    +------------------+        +--------------------------+
    |  SelfStateProbe  |  --->  |  DoubleBuffer.Write      |  ->  per-tech SelfProbeSnapshot slot
    +------------------+        +--------------------------+

Main thread (event dispatch via DrainMainThreadQueue):
    +-------------------+
    | DamageHintBuffer  |  ->  per-victim ring (lock-on-Push)
    | WeaponFireBuffer  |
    | CargoStatePub.    |
    | RecentDmgDealt    |
    | AnchorStateCache  |
    +-------------------+

Background thread (StrategicStateExtractor, 30 Hz):
    +---------------------------+
    | aether.snapshotBuffer.Read|  ->  immutable BeliefSnapshot
    | selfProbeBuffer.Read      |  ->  immutable SelfProbeSnapshot
    | terrainMap.SlopeAt/etc.   |  ->  any-thread read of TerrainMapSnapshot
    | armorMap.QueryWeakFace    |  ->  any-thread read of per-tech ArmorMap
    | sidecar.SumWithin etc.    |  ->  lock-on-Push, lock-on-snapshot reads
    +---------------------------+
                |
                v
    +---------------------------+
    | StrategicStateBuffer.Write|  ->  per-tech DoubleBuffer<StrategicStateVector>
    +---------------------------+

Trainer thread (background, reading published vector):
    StrategicStateBuffer.Read(techId)  ->  immutable StrategicStateVector
```

Read-side correctness depends on **immutability discipline**: every Written
`StrategicStateVector` is never mutated. `DoubleBuffer<T>` provides full-fence
semantics on Read+Write (see `Threading/DoubleBuffer.cs:11-25` comment).

### §6.3 Main-thread budget per tech per publish

Detailed accounting (30 Hz publish — gated by the 33 ms `ObserveWorldTechsIfDue`
throttle; `SmartForm.Operations` itself runs at FixedUpdate 50 Hz but the
SelfStateProbe body only executes on the gated path):

  * SelfStateProbe.Tick: **~5 µs/tech**
  * Sidecar amortized: **~1 µs/tech**
  * Cache invalidation amortized: **~2 µs/tech**
  * Total: **~8-10 µs/tech**

At `KickStart.AIPopMaxLimit` default 8 ≈ 80 µs/publish of new main-thread cost.
At the slider max 32 ≈ 320 µs/publish. Both well under 1 ms.

### §6.4 Background daemon budget at N=50 techs

Per-tick (33 ms):

  * BeliefSnapshot read: 1 dictionary lookup per tech. ~50 ns × 50 = 2.5 µs.
  * SelfProbe read: 1 DoubleBuffer.Read per tech. ~50 ns × 50 = 2.5 µs.
  * NearestTechCache query (K-nearest, tile-bucket grid, §8.3): O(buckets-near-self).
    Estimated **~5-10 µs per tech** at N=50, **~250-500 µs total**.
  * TerrainMap sample (HeightAt + SlopeAt + RaycastSegment, 3 reads): ~1 µs per tech
    × 50 = 50 µs.
  * ArmorMap query (per tech): ~2 µs × 50 = 100 µs.
  * Vector pack (256 float writes): ~3 µs × 50 = 150 µs.
  * DoubleBuffer.Write per tech: ~100 ns × 50 = 5 µs.

**Per-tick total at N=50: ~600-900 µs.** Well under the 33 ms budget. Headroom for
N=100 is ~1.5-2 ms — still comfortable.

NEEDS-EVIDENCE: the NearestTechCache bucket size at N=50 is the load-dominant term;
the cost above assumes ~25 cell buckets per query at 64m bucket size. At 50 techs in a
2km × 2km map area, bucket-occupancy averages ~0.4 techs/bucket and the cost estimate
is bounded above by NearestTechCache walk distance. The empirical bucket-size needs
runtime verification under sustained spawn-test load.

### §6.5 Failure modes and mitigations

| Failure mode | Mitigation |
|---|---|
| **Snapshot tear** — daemon reads partially-written `SelfProbeSnapshot` | Snapshot is an immutable-by-discipline class; `DoubleBuffer.Read` is atomic ref-read via `Volatile.Read` (`DoubleBuffer.cs:37`). The published reference is swapped under `Interlocked.Exchange`. CAVEAT: `StrategicStateVector.Raw` is `public readonly float[]` and `SelfProbeSnapshot` fields are `readonly` — these block reassignment, not element mutation. Tear-freedom relies on the publisher never writing into a published instance and the reader never writing at all (see §3.1 disciplinary note). The §4.2 step-7 NaN/Inf scrub runs BEFORE `Write` — it is the daemon's last edit before atomic publish, not a post-publish mutation. |
| **Daemon stall** — extractor hangs >10 s | `DaemonWatchdog.RegisterCanonical` + `WorkerHealthMonitor.RegisterCanonical` provide free auto-respawn (template at AetherFuser registration). MUST also add name to `DaemonWatchdog.CanonicalRoster` array (Director-plan §2 BLOCKER). |
| **Lifecycle race** — tech recycles between snapshot read and slot publish | `StrategicStateVector` carries `Generation` field; readers detect stale generation and discard. Generation increments on `WorldModel.DeregisterTech` and on `IAIForm.OnTechRecycle(helper)` (TankAIHelper.cs:967) — **NOT** by subscribing to `Tank.TankRecycledEvent` directly (per TRAINING-DIRECTOR-PLAN §RM-9 the established pattern is to route recycle cleanup through `IAIForm.OnTechRecycle` so Smart owns the lifecycle, not the engine event). |
| **Sidecar event-storm** — flood of DamageObserved events under heavy combat | EventBus's `DrainMainThreadQueue` caps at 256 events per tick (`EventBus.cs:283`). Sidecars handle the throttle; lost events degrade signal accuracy but never block main thread. |
| **NaN/Inf in derived features** — non-finite from belief NaN slip-through | `StrategicStateExtractor` runs a final NaN/Inf scrub on `vec.Raw` before publish, mirrors `ContinuousController.cs:250-255` guard. NaN slots zeroed. |
| **Reflection NRE on weapon-fire reads** — `ModuleWeaponGun.m_ShotTimer` is private | `WeaponReadinessHelper` (§8.1) caches FieldInfo, catches NRE per-weapon, falls back to ReadyToFire=false (conservative). Mirrors WeaponReflectionCache pattern. |
| **Sustained CPU contention with AetherFuser** | Both daemons own their own threads; OS scheduler balances. NEEDS-EVIDENCE that the two together don't preempt under N≥30 sustained engagements. |

---

## §7. Model architecture changes

All four models bump `ArchitectureVersion` from their current value (2 / 1 / 1 / 1) to
**3** in a single coordinated patch. ProfilePersistence's existing byte-version gate
(`ProfilePersistence.cs:22,111`) refuses mismatched-version load; load failure falls
back to fresh Glorot init — the documented v0.1 behavior. Restart-from-scratch is
intentional (§2 no-preservation-needed posture).

### §7.1 OpponentIntentClassifier

| Before | After |
|---|---|
| `SeqLen=30, FeatureDim=12` (`OpponentIntentClassifier.cs:35-36`) | `SeqLen=30, FeatureDim=40` |
| `Hidden=64`, `OutDim=6` (preserved) | unchanged |
| Forward path: GRU(64) → Dense(6) → Softmax (`OpponentIntentClassifier.cs:24`) | unchanged |
| `ArchitectureVersion=2` (per migration `0002_BpttUnfreeze`) | **`ArchitectureVersion=3`** |
| Parameters layout (`_wrOff,_urOff,_brOff` etc.) | offsets recomputed: GRU gate Ws grow with FeatureDim (12→40); offsets parameterized over `FeatureDim` field |

Param-count delta: each gate W is `Hidden × FeatureDim` = 64 × 40 = 2 560 (was 64 × 12
= 768). Three gates → grows from 2 304 to 7 680. Dense head unchanged at 64 × 6 + 6 =
390. Adam M, V arrays grow to match. Existing constructor pattern at
`OpponentIntentClassifier.cs:70-100` parameterizes correctly because `FeatureDim` is a
`const int`; bumping the constant cascades through the offset math.

LOC delta in `OpponentIntentClassifier.cs`: ~10 LOC (constants + one comment block).

### §7.2 ActionValueEstimator

| Before | After |
|---|---|
| `StateDim=50, H1=64, H2=64` (`ActionValueEstimator.cs:38-40`) | `StateDim=128, H1=128, H2=128` |
| Forward: input(50) → Dense(50→64) → ReLU → Dense(64→64) → ReLU → Dense(64→1) | input(128) → Dense(128→128) → ReLU → Dense(128→128) → ReLU → Dense(128→1) |
| `ArchitectureVersion=1` | **`ArchitectureVersion=3`** |

**Includes building the producer that currently doesn't exist.** `LearningService.cs:25-29`
documents "ActionValueEvent has zero production sites in v0.2." This plan ships the
producer in §9 modifications to `ContinuousController.cs`. The producer reads
`StrategicStateBuffer.Read(techId)` to pull the per-tech `StrategicStateVector`,
slices `Raw[ActionValueBase .. ActionValueBase + 128 - 1]` (128 dims; the per-action
one-hot occupies AV[80..89]) to form the `state` array, takes the action one-hot from
the just-selected action, and enqueues `ActionValueEvent(s, a, r, sNext, gamma)` into
`ActionValueEstimator.EventQueue`. The reward `r` is derived from
`(self.HpFractionDelta − target.HpFractionDelta) / elapsed_sec` over a 0.5-sec
lookback window. **`HealthSidecar.Get(id)` (HealthSidecar.cs:53) returns only
instantaneous HpFraction; the 0.5-sec lookback delta requires the §8.2 HealthSidecar
DELTA to add a small per-tech `(tickMono, hpFraction)` ring (16-deep, ~0.5 s at
30 Hz) so the producer can compute `hpAtNow − hpAt(now − 0.5s)` without each
ContinuousController caller stashing history independently.** The ring lives on
HealthSidecar (publisher); existing `Record(TechId id, int blockCount)` at
HealthSidecar.cs:40 stays as-is; the §8.2 DELTA adds a NEW method
`RecordHpFraction(TechId id, float hpFraction, long tickMono)` and the producer-side
call site is on the existing block-attach/HP path immediately after
`_state.AddOrUpdate` (compute `hpFraction = current / max` and forward to the new
ring). **Action `a` mapping**: `ContinuousController.OnOperationsTick` arbitrates
over `TacticalGoal` (NOT `PlanLibrary.PlanType` — `PlanType` lives in the team-wide
Coordination layer at `Coordination/RoleAssignment.cs:46`; per-tech ContinuousController
selects `TacticalGoal` via the 3-tier external/identity/tactical precedence at
`ContinuousController.cs:189-256`). Action `a` is the one-hot index of the goal-class
in effect at the decision boundary, derived from a small ten-class enumeration of
the three precedence sources × goal kind (e.g.,
`{External_AtCurrent, External_Move, External_Attack, Identity_Generic,
Identity_Hunter, Identity_Base, …}`). The §3.3 ActionValue slot block AV[80..89] is
a one-hot over those ten classes; the producer encodes the just-selected class.
Mapping table lives in `ContinuousController.cs` next to the producer for review.

LOC delta in `ActionValueEstimator.cs`: ~10 LOC (constants).

### §7.3 TrajectoryResidualModel

| Before | After |
|---|---|
| `FeatureDim=15, H1=32, H2=32, OutDim=3` (`TrajectoryResidualModel.cs:32-35`) | `FeatureDim=48, H1=64, H2=64, OutDim=3` |
| `ArchitectureVersion=1` | **`ArchitectureVersion=3`** |

Param-count delta: `W1 = H1 × FeatureDim = 64 × 48 = 3 072` (was 32 × 15 = 480).
`W2 = H2 × H1 = 64 × 64 = 4 096` (was 32 × 32 = 1 024). `W3 = OutDim × H2 = 3 × 64 =
192` (was 3 × 32 = 96).

LOC delta: ~10 LOC.

### §7.4 ThreatAssessmentModel

| Before | After |
|---|---|
| `FeatureDim=25, H1=32, H2=32` (`ThreatAssessmentModel.cs:29-31`) | `FeatureDim=48, H1=64, H2=64` |
| `ArchitectureVersion=1` | **`ArchitectureVersion=3`** |

LOC delta: ~10 LOC.

### §7.5 Coordinated bump rule

All four `byte ArchitectureVersion` getters increment in the same commit. The patch
SHIPS the four model files together; cherry-picking a subset breaks load semantics on
all four. The next session starts with all four at fresh init.

`ProfilePersistence` (`ProfilePersistence.cs:22,111`) reads the per-model architecture
byte at load; mismatch → discard params → fall back to Glorot init → train from
minute zero. This is the **existing behavior** — no new code in `ProfilePersistence`
is required for the bump.

Migration scaffolding: **`0003_StrategicStateExpansion` would collide with the
existing `Smart/Learning/Migrations/0003_TlvSectionBody.cs`** (L-079 TLV body
migration, `[SmartMigration(fromVersion: 2)]`, `Version = 3`). Also: per-model
`ArchitectureVersion` is unrelated to the persistence `SchemaVersion`
(`ProfilePersistence.CurrentSchemaVersion`). Arch-version mismatches are handled
automatically by `LearningService.ApplyProfileSafe` (LearningService.cs:243-264) +
`ProfilePersistence.LoadParameters` length-mismatch fallback to fresh Glorot init —
**no migration class is strictly required for the arch bump**. If a documentary
migration is desired for `ProfileSelfTest` to surface the bump in logs, it must be
renumbered to `0004_StrategicStateExpansion.cs` with `[SmartMigration(fromVersion: 3)]`
(template: `Smart/Learning/Migrations/0002_BpttUnfreeze.cs`). ~10 LOC; modeled on the
existing migration class.

---

## §8. New instrumentation files

| File | LOC | Purpose | Cite for justification |
|---|---|---|---|
| §8.1 `Smart/Vehicle/WeaponReadinessHelper.cs` | ~120 | Per-tech ReadyToFire count + FiringObstructed count. Reflection wrapper around `ModuleWeaponGun.m_ShotTimer` (private) + `IModuleWeapon.ReadyToFire()` / `FiringObstructed()` (methods, NOT properties). Field cache mirrors `WeaponReflectionCache.cs:13-26`. | `Smart/Vehicle/WeaponReflectionCache.cs:13-26` (template); engine: `ModuleWeaponGun` private `m_ShotTimer`; `IModuleWeapon` interface (`IModuleWeapon.cs:11,13`) |
| §8.2 `Smart/World/HealthSidecar.cs` (DELTA) | +~110 | (a) Add HP-accurate sum: aggregate `Damageable.Health` (`Damageable.cs:67`) per block on cache rebuild. Block-count proxy at `HealthSidecar.cs:40` loses partial-block damage. (b) **Add new method `RecordHpFraction(TechId id, float hpFraction, long tickMono)` + per-tech `(tickMono, hpFraction)` companion ring 16-deep + `float GetAt(TechId id, long monoCutoff)` accessor** so the ActionValue reward formula (§7.2) can compute `hp(now) − hp(now − 0.5s)` without per-caller history stashing. The existing `Record(TechId, int blockCount)` (HealthSidecar.cs:40) stays as-is; `RecordHpFraction` is wired immediately downstream of `_state.AddOrUpdate` to push `(tickMono, fraction = current/max)` into the ring. Note `HpState` (HealthSidecar.cs:25) is a value struct in a `ConcurrentDictionary<TechId, HpState>` — the new ring lives in a separate `ConcurrentDictionary<TechId, HpHistoryRing>` companion structure (Ring is a sealed class with a per-instance lock identical to the DamageHintBuffer ring pattern at DamageHintBuffer.cs:67-93). `GetAt` walks the ring under the per-instance lock — safe to call from the background extractor daemon. Augment, do not replace. | `Smart/World/HealthSidecar.cs:25,35-50`; `Smart/World/DamageHintBuffer.cs:67-93` (ring template); engine: `Damageable.cs:67` `public float Health => (float)m_HealthFixed/4096f` |
| §8.3 `Smart/World/NearestTechCache.cs` | ~280 | Tile-bucket spatial grid over `BeliefSnapshot.ByTech`. Bucket size 64 m. Supports K-nearest-hostile + K-nearest-friendly queries from background daemon. Refreshes per-tick on the daemon thread from the read snapshot — pure data, no Unity access. | replaces O(N) iteration in `ManTechs.IteratePlayerTechs` etc. (no spatial index in ManTechs verified); template: lock-free read pattern from `LineOfSightProducer.cs` |
| §8.4 `Smart/World/RecentDamageDealtAccumulator.cs` | ~180 | Per-attacker ring 16-deep of `(magnitude, damageType, tickMono)`. Symmetric to `DamageHintBuffer.cs`. Subscribes to `WorldEventBus<DamageObserved>` and indexes by attacker (`DamageObserved.Damage.AttackerIfKnown`). **Public instance API: `Wire/Unwire/Forget(TechId)` (mirrors DamageHintBuffer) + `float SumWithin(TechId attacker, long nowMono, double windowSec)` (walks the ring under the existing lock and sums magnitudes whose `(now − tickMono)/MonoFreq < windowSec`).** | `Smart/World/DamageHintBuffer.cs:54-115` (template — `Wire` at :108, `Unwire` at :117, `Forget` at :163); event source: `SmartEventBridge.cs:326-340` |
| §8.5 `Smart/World/DamageHintBuffer.cs` (DELTA) | +~50 | (a) Add `DamageType` byte to `DamageHint` `readonly struct` — ctor at `DamageHintBuffer.cs:27` widened, layout change. (b) Plumb DamageType through `OnDamage` and the producer; the source is `info.DamageType` (`ManDamage.DamageInfo.DamageType` at ManDamage.cs:58 — the `DamageType` enum: Impact/Standard/etc.), NOT per-block `Damageable.DamageableType` (which varies by hit location and isn't a tech-wide value). (c) **Add public instance API `float SumWithin(TechId victim, long nowMono, double windowSec)`** — the existing API at `DamageHintBuffer.cs:150` is only `TryGetRecent(victim, dst)`; SumWithin walks the same ring under the same lock. | `Smart/World/DamageHintBuffer.cs:12-37`; engine: `ManDamage.DamageInfo.DamageType` (ManDamage.cs:58) |
| §8.6 `Smart/World/CargoStatePublisher.cs` | ~200 | Per-tech `(numContents, totalCapacity, accumulatedValue, lastUpdateMono)`. Subscribes to `TechHolders.ItemPickupEvent + ItemReleaseEvent` (`TechHolders.cs:163,165`) + iterates `ModuleItemHolder` list on cache rebuild for capacity totals. | `Smart/World/HealthSidecar.cs` lifecycle (template); engine: `TechHolders.cs:163,165` events; `ModuleItemHolder.NumContents/IsFull/Acceptance` |
| §8.7 `Smart/World/WeaponFireBuffer.cs` | ~180 | Per-tech ring 16-deep of `tickMono` only (`TechWeapon.WeaponsFiredEvent` at TechWeapon.cs:145 is `EventNoParams` — no shot-count payload; each `Send()` counts as **one fire-tick**, regardless of how many barrels actually fired). Subscribes per-Tank to the event in `Wire(TechId, Tank)`. **Public instance API: `Wire/Unwire/Forget(TechId)` + `int CountWithin(TechId tech, long nowMono, double windowSec)` (counts ring entries whose `(now − tickMono)/MonoFreq < windowSec`).** | template: `DamageHintBuffer.cs:54-115`; engine: `TechWeapon.WeaponsFiredEvent` at TechWeapon.cs:145 |
| §8.8 `Smart/Learning/Features/StrategicStateVector.cs` | ~280 | The shared struct (§3.1) + the four slot-constant static classes (§3.2-§3.5). Single source of truth for slot layout. | this plan §3.1-§3.5 |
| §8.9 `Smart/Learning/Features/StrategicStateExtractor.cs` | ~520 | The background daemon (§4). Per-tick loop, per-tech fill, NaN/Inf scrub, atomic publish. Template: `Smart/World/AetherFuser.cs`. | `Smart/World/AetherFuser.cs:25-60` (class template), `:62-154` (Tick body), `:191-` (RunLoop with cancellation + diagnostic-window) |
| §8.10 `Smart/Learning/Features/SelfStateProbe.cs` | ~340 | The main-thread per-tech publisher (§5.1). Constructed alongside `KinematicTracker` (`SmartRuntime.cs:86`); ticked on `SmartForm.Operations`. | `Smart/Vehicle/KinematicTracker.cs` (template — per-tech main-thread publisher); engine: `TechEnergy.Energy(Electric)`; `Tank.cs:513,515` |
| §8.11 `Smart/Learning/Features/StrategicStateBuffer.cs` | ~120 | Per-tech `DoubleBuffer<StrategicStateVector>` registry (§4.4). Lifecycle hooks for spawn/Forget. | `Smart/Threading/DoubleBuffer.cs:27-44`; `Smart/World/DamageHintBuffer.cs:163` Forget pattern |

New-file total: ~2 330 LOC across 11 files (§8.1 + §8.3 + §8.4 + §8.6 + §8.7 + §8.8 + §8.9 + §8.10 + §8.11 = 9 new; §8.2 + §8.5 are deltas, counted in §9).

---

## §9. Modified files

| File | LOC delta | What changes | Cite |
|---|---|---|---|
| `Smart/Learning/OpponentIntentClassifier.cs` | ~+15 | `FeatureDim` 12→40; `ArchitectureVersion` 2→3; offsets recompute trivially from `FeatureDim` const | `OpponentIntentClassifier.cs:35-47` |
| `Smart/Learning/ActionValueEstimator.cs` | ~+20 | `StateDim` 50→128; `H1` 64→128; `H2` 64→128; `ArchitectureVersion` 1→3 | `ActionValueEstimator.cs:38-47` |
| `Smart/Learning/TrajectoryResidualModel.cs` | ~+15 | `FeatureDim` 15→48; `H1` 32→64; `H2` 32→64; `ArchitectureVersion` 1→3 | `TrajectoryResidualModel.cs:32-42` |
| `Smart/Learning/ThreatAssessmentModel.cs` | ~+15 | `FeatureDim` 25→48; `H1` 32→64; `H2` 32→64; `ArchitectureVersion` 1→3 | `ThreatAssessmentModel.cs:29-38` |
| `Smart/Control/ContinuousController.cs` | ~+180 | New `ActionValueEvent` publisher at decision sites: at `OnOperationsTick`, after goal arbitration (`ContinuousController.cs:189-256`), capture `s` from `StrategicStateBuffer.Read(techId)`, capture `a` from selected `PlanLibrary.PlanType`, compute reward `r` from 0.5 s `HealthSidecar` lookback delta, enqueue `ActionValueEvent` after the NEXT tick when `s'` is known (one-tick deferred enqueue via per-tech pending-state buffer). | `ContinuousController.cs:189-256`; `ActionValueEstimator.cs:13-25` |
| `Smart/SmartRuntime.cs` | ~+80 | Init wiring: construct `StrategicStateBuffer`; construct `StrategicStateExtractor` via `WorkerPool.EnqueueLongRunning(extractor.RunLoop, "StrategicStateExtractor")`; register with `DaemonWatchdog.RegisterCanonical` + `WorkerHealthMonitor.RegisterCanonical`; append "StrategicStateExtractor" to `DaemonWatchdog.CanonicalRoster`; wire `SelfStateProbe` allocation in `SmartPerTechState` ctor; wire sidecar Wire() calls (DamageHintBuffer already wires per `DamageHintBuffer.cs:108`; add CargoStatePublisher.Wire, WeaponFireBuffer.Wire, RecentDamageDealtAccumulator.Wire, AnchorStateCache.Wire); Shutdown unwire chain. | `SmartRuntime.cs:74-100` (per-tech state construct); `WorkerPool.cs:127`; `DaemonWatchdog.RegisterCanonical` pattern |
| `Smart/Learning/LearningService.cs` | ~+150 | Trainer enqueue for ActionValue (currently absent per `LearningService.cs:23-29`): consumed via `ContinuousController` publisher → existing ActionValueEstimator.EventQueue path. Also: widen `OnDamage` Threat-feature fill from 2 / 25 → 48 by reading `StrategicStateBuffer.Read(attackerId)` for ATTACKER-side slots 0..23 + 32..35 (per §3.5: slot 35 `RecentDamageDealtTypeCode` is the attacker's dealt-damage type) and `StrategicStateBuffer.Read(victimId)` for victim-side slots 30, 31, 36, 37 (`VictimWeakFaceHp`/`VictimHpFraction`/victim-recent-damage windows). Widen `TargetObservationSequenceBuffer.RecordRow` (TargetObservationSequenceBuffer.cs:54 — the per-row fill site that cascades on the `FeatureDim` const) to push 40-dim per-timestep from the same `StrategicStateBuffer` slice; the `TryBuildEvent` snap-copy at TargetObservationSequenceBuffer.cs:88 follows transitively. Add fire-time slot-fill at the new `LeadResidualRecorder.OnFireCommit` (widened per §3.4 closing note — Pending struct gains a `float[] FireTimeFeatures` field, OnFireCommit gains a `float[]` parameter): caller (ContinuousController/WeaponFireController fire dispatch) slices 35 input dims from `StrategicStateBuffer.Read(self).Raw[ResidualBase..ResidualBase+34]` and passes the array in; observe-arrival at LeadResidualRecorder.cs:99 enqueues the complete event with the separate `ObservedResidual` Vector3 label (no two-phase write into a queued event — eliminates label leakage). | `LearningService.cs:680-696` (Threat OnDamage); `Smart/Learning/TargetObservationSequenceBuffer.cs:54,88` (Intent fill + snap copy); `Smart/Learning/LeadResidualRecorder.cs:34-40` (Pending struct), `:48` (OnFireCommit — widened), `:99` (observe-time enqueue) |
| `Smart/Integration/SmartEventBridge.cs` | ~+40 | Plumb DamageType byte through `SanitizedDamageInfo` for `DamageHintBuffer + RecentDamageDealtAccumulator`. Today `DamageObserved` carries `Damage` magnitude only (verified `DamageHintBuffer.cs:131-136`); extend the producer at `SmartEventBridge.cs:326-360` to read `info.DamageType` cast to byte from the engine event source (`ManDamage.DamageInfo.DamageType` at ManDamage.cs:58 — the tech-wide enum). NOT `info.Damageable.DamageableType` (which is per-block and varies by hit location, not a tech-wide value). | `Smart/Integration/SmartEventBridge.cs:326-360`; engine: `ManDamage.DamageInfo.DamageType` (ManDamage.cs:58) |
| `Smart/World/DamageHintBuffer.cs` | already in §8.5 (delta accounted there) | signature change for DamageType byte | `DamageHintBuffer.cs:12-37` |
| `Smart/World/HealthSidecar.cs` | already in §8.2 (delta accounted there) | HP-accurate sum | `HealthSidecar.cs:40` |
| `Smart/Learning/Migrations/0004_StrategicStateExpansion.cs` | ~+30 (effectively new) | (Optional — see §7.5: arch-version mismatch is handled by ProfilePersistence/LoadParameters fallback to Glorot init; no migration is strictly required.) If shipped: empty forward migration `[SmartMigration(fromVersion: 3)]` documenting the arch bump for ProfileSelfTest. Renumbered from `0003_*` to avoid filename collision with existing `0003_TlvSectionBody.cs`. Modeled on `Migrations/0002_BpttUnfreeze.cs`. | template: `Smart/Learning/Migrations/0002_BpttUnfreeze.cs` |

Modified-file LOC delta: 7 distinct paths (4 models + ContinuousController + SmartRuntime + LearningService + SmartEventBridge) ≈ **~545 LOC**. Plus the §8.2 and §8.5 deltas counted in §8 ≈ 110 LOC. Plus the migration ≈ 30. Modified subtotal ≈ **~685 LOC**.

Note: the prompt listed `SmartEventBridge.cs` and the migration as separate rows; LOC math reconciles to roughly the §10 totals below.

---

## §10. LOC totals

| Bucket | LOC |
|---|---|
| New files (§8.1, 8.3, 8.4, 8.6, 8.7, 8.8, 8.9, 8.10, 8.11) | ~2 220 |
| Deltas counted in §8 (8.2 HealthSidecar +110, 8.5 DamageHintBuffer +50) | ~160 |
| Modified files (§9: 4 models + ContinuousController + SmartRuntime + LearningService + SmartEventBridge) | ~545 |
| Migration `0004_StrategicStateExpansion.cs` (optional) | ~30 |
| Overhead: csproj rows, comment headers, SmartRuntime Shutdown ripples | ~95 |
| **Total** | **~3 050 LOC** |

Reconciliation: round-1 review tightened the §1 header from **~3 870 LOC** to
**~3 050 LOC** to match the line-item math. The §8.2 / §8.5 delta budgets were
widened (+30 / +20) to account for the SumWithin / GetAt new APIs surfaced by
round-1 (originally implied but unscoped). The §1 number now matches §10 within
overhead margin.

---

## §11. Open items / NEEDS-EVIDENCE

Only items requiring **runtime observation**. No "v0.3" deferrals.

  1. **NearestTechCache bucket size at N=50** (§6.4). The CPU estimate assumes ~25
     cell buckets per query at 64 m bucket size. Empirical validation under sustained
     spawn-test load is required. Decision lever: shrink the bucket if buckets-per-query
     drops below ~10; grow if it climbs above ~40.
  2. **`StrategicStateExtractor` per-tech cost under sustained load** (§6.4). 600-900
     µs/tick at N=50 is the budget. Needs measurement at N≥30 sustained engagement
     to confirm no cross-daemon preempt with `AetherFuser`. Decision lever: drop
     daemon cadence from 30 Hz to 20 Hz if combined preempt budget breaches.
  3. **WeaponSpec aggregate cost on Attach/Detach storms** (§5.3). Re-iteration is
     ~100 µs per event; at peak Attach/Detach rates under heavy combat (>50 events/sec
     per tech during a chassis breakup), per-tech budget temporarily breaches.
     Needs observation: how often the worst case actually hits, and whether the
     deferred-recompute pattern (flag-and-lazy) holds.
  4. **DamageHintBuffer + RecentDamageDealtAccumulator under simultaneous-victim
     storms**. EventBus's `DrainMainThreadQueue` caps at 256 events/tick
     (`EventBus.cs:283`). At 16-tech engagements with simultaneous attacks, some
     damage events drop. Sidecar accuracy degrades gracefully (still correct, just
     undercounted). Needs observation: does the floor of recent-damage features bias
     learning materially?
  5. **TerrainMap snapshot age tolerance**. The map refreshes at 10 s
     (`Pathing/TerrainMap.cs:69`) and updates time-sliced; per-tick reads see an
     up-to-10-s-stale snapshot. For terrain-stable maps this is fine; for active
     procedural generation, slope features may lag terrain edits. Needs observation
     of slope-feature drift in scenarios with chunk regeneration.
  6. **Sidecar ring depth under sustained combat** (§5.2, §8.4, §8.5, §8.7). New
     `RecentDamageDealtAccumulator` (§8.4) and `WeaponFireBuffer` (§8.7) are specified
     at depth 16 per attacker; `DamageHintBuffer` (§8.5) stays at `CapacityPerTech = 8`
     (DamageHintBuffer.cs:62). At a 5-sec `SumWithin` window and sustained damage rates
     of >10 events/sec/attacker in heavy combat, the 16-deep ring saturates and the
     window measurement floors. Saturation is GRACEFUL (returns the visible-window
     subtotal, never NaN), but the `RecentDamageDealt5s` / `RecentShotsFired1s` features
     under-report under storm. Decision lever: bump ring depth to ~64 if empirical
     saturation rate exceeds ~5% of ticks under N≥16 engagements.
  7. **TerrainMap grid extents vs `NearestTechCache` bucket sizing** (§6.4). Item 1
     assumes a 2 km × 2 km playable area; `TerrainMap` defaults to 512 × 512 × 4 m =
     2.048 km × 2.048 km centered on origin (`Pathing/TerrainMap.cs:61-62`). Larger
     maps require chunked load (OPEN per the TerrainMap.cs:62 comment). The
     bucket-occupancy estimate from item 1 holds while techs cluster inside the
     centered grid; under chunked load it may fragment.

(Round-1 review removed two former rows that were code questions, not runtime
observations: `ActionValueEstimator` one-tick deferred enqueue race — resolved as a
bounded per-tech pending-state mailbox in the §7.2 producer spec (cleared on
pause/host-change/recycle); `SelfStateProbe` `Tank.Anchors` null guard — resolved by
null-check in the SelfStateProbe code path itself, with the published `AnchorState`
defaulting to 0 when `Anchors` is unconstructed. Both belong in implementation
review, not runtime observation. The TerrainMap row is retained because the question
is empirical drift behaviour under chunk regen, not code shape.)

---

## §12. Verifier-pass appendix

### Round 1 — precondition-check pass (12 reviewers, applied)

12 review agents passed over the plan checking: (A) fictional APIs, (B) C# 7.3 / .NET
4.6.1 compliance, (C) Unity threading violations, (D) thread-safety, (E) implementation
completeness, (F) engineering plausibility, (G) markdown / cross-reference format.

C# 7.3 / .NET 4.6.1 scan: **clean** — no banned features (records, switch expressions,
init-only setters, `Span<T>`/`Memory<T>`, nullable refs, target-typed new, async
streams, range/index, using-declarations, `volatile long`) found in any pseudocode
block by any reviewer.

#### Disposition table

| ID | Severity | Finding | Disposition | Edit |
|---|---|---|---|---|
| F-01 | BLOCKER | `EnergyState.Stored` / `NetFlow` fields are fictional (TechEnergy.cs:37-52 exposes `currentAmount`, `spareCapacity`, `storageTotal`, `previousAmount`, `previousSpareCapacity`) — flagged by R1, R3, R4, R6, R7, R8, R9, R11, R12 | APPLIED — §3.3 slots 20-22 renamed to `currentAmount` / `spareCapacity` / `(currentAmount − previousAmount)`; §3.5 slot 23 fixed; §4.3 derivation row updated | §3.3, §3.5, §4.3 |
| F-02 | BLOCKER | Migration `0003_StrategicStateExpansion.cs` collides with existing `0003_TlvSectionBody.cs`; also conflates SchemaVersion with per-model ArchitectureVersion — flagged by R1, R2, R6, R8, R10, R11 | APPLIED — §7.5 + §9 renumbered to `0004_*` with `fromVersion: 3`; clarified migration is optional (arch bump handled by LoadParameters fallback) | §7.5, §9, §10 |
| F-03 | BLOCKER | `SmartRuntime.OnTechRegistered` event is fictional; HealthSidecar.cs:35-40 is not a wire pattern — flagged by R1, R3, R4, R6, R8, R12 | APPLIED — §4.4 Lifecycle rewritten to use `TeamRuntime.RegisterTech` (SmartRuntime.cs:393); wire-template cite redirected to `DamageHintBuffer.Wire` at :108 / `Unwire` at :117 | §4.4 |
| F-04 | BLOCKER | Background-daemon Unity violations: `TankAIHelper.Provoked` (MonoBehaviour), `TechAI.LastAttacker` / `LastAttackedTime` (TechComponent), `ManTimeOfDay.inst.TimeOfDayPrecise` (Singleton), `KickStart.WaterHeight` (WaterMod), `tank.rootBlockTrans` (Transform) all touched on bg thread per §3 / §4.3 — flagged by R1, R2, R3, R4, R6, R7, R8, R9, R11, R12 | APPLIED — §5.1 SelfStateProbe field list expanded to include `ProvokedCountdown`, `LastAttackerPosWorld`, `LastAttackedTimeMono`, `TimeOfDayHour`, `WaterHeight`, `SmartIdentity`, `forwardWorld`, `weakFaceNormalWorld`, `Armor`; §3.2-§3.5 derivation columns rewritten to source from `selfProbe.*`; §4.1 daemon "never touches" list expanded | §3.2, §3.3, §3.5, §4.1, §4.3, §5.1 |
| F-05 | BLOCKER | `DamageHintBuffer.SumWithin` / `WeaponFireBuffer.CountWithin` / `RecentDamageDealtAccumulator.SumWithin` are fictional — existing surface is only `TryGetRecent(victim, dst)` at DamageHintBuffer.cs:150; §8 specs don't declare the SumWithin/CountWithin APIs — flagged by R1, R2, R4, R6, R8, R9, R10, R11, R12 | APPLIED — §8.4 / §8.5 / §8.7 rows now explicitly declare the new instance APIs with signatures + tick-window semantics; §4.2 pseudocode annotates NEW vs existing; §3.5 cells re-cite the new APIs | §3.5, §4.2, §8.4, §8.5, §8.7 |
| F-06 | BLOCKER | `ArmorMap` / `ArmorMapSnapshot` not enumerated in §5.1 SelfStateProbe field list despite §4.2 reading `selfProbe.ArmorMapSnapshot` — flagged by R1, R2, R4, R6 | APPLIED — §5.1 field list adds `Armor` (ArmorMap reference, any-thread per chassis-immutability); §4.2 pseudocode updated to `selfProbe.Armor` | §4.2, §5.1 |
| F-07 | BLOCKER | TerrainMap path is `Smart/Pathing/TerrainMap.cs` not `Smart/World/TerrainMap.cs`; "any-thread by design" misrepresents :54-60 (which says sampling is main-thread only; only queries off the snapshot are any-thread); daemon must gate on `!IsFreshlyAllocated` — flagged by R2, R5, R8, R9, R10 | APPLIED — global `TerrainMap.cs` → `Pathing/TerrainMap.cs`; §4.1 threading text rewritten ("snapshot reads are any-thread; sampling main-thread only; daemon gates on IsFreshlyAllocated"); §4.2 pseudocode adds IsFreshlyAllocated gate | §3.2-§3.5, §4.1, §4.2, §11 |
| F-08 | BLOCKER | `tank.blockCount` does not exist on Tank — actual is `tank.blockman.blockCount` (BlockManager.cs:380) — flagged by R7 (sole reviewer, claim verified against source) | APPLIED — §3.3 slot 11 rewritten + parenthetical note; §5.1 field list updated | §3.3, §5.1 |
| F-09 | BLOCKER | Residual feature/label leakage: §3.4 slots `[32..34]` originally embedded `ObservedResidualXYZ` (the label) inside `Features[]`, despite `ResidualEvent` having a separate `ObservedResidual` field at TrajectoryResidualModel.cs:17 — flagged by R3 (sole reviewer; claim verified at TrajectoryResidualModel.cs:13-23) | APPLIED — §3.4 slot map rewritten: `[32..34]` = `LinearExtrapXYZ` (input only); `[35..47]` reserved; explicit "label leakage avoided" note + correction that LeadResidualRecorder.cs:99 is the observe-time enqueue (not fire-time) | §3.4, §4.2, §4.4, §9 |
| F-10 | MAJOR | `TimeOfDayCosine` formula `/1.0` is incorrect — `TimeOfDayPrecise` returns hours [0,24); divisor should be `/24` — flagged by R1, R3, R4, R5, R6, R7, R8, R9 | APPLIED — §3.2 slot 34 formula fixed to `cos(2π · hour / 24)` and re-routed via SelfStateProbe | §3.2, §4.3 |
| F-11 | MAJOR | `AetherFuser._snapshotBuffer` is private (AetherFuser.cs:28); public accessor is `WorldModel.FusedBuffer` at WorldModel.cs:51 — flagged by R2, R7, R10, R12 | APPLIED — §4.1 + §4.2 cite `world.FusedBuffer.Read()` (WorldModel.cs:51); private-field cite removed | §4.1, §4.2 |
| F-12 | MAJOR | `TechWeapon.WeaponsFiredEvent` is `EventNoParams` (TechWeapon.cs:145) — no shot-count payload; ring spec `(shotCount, tickMono)` was wrong — flagged by R1, R4, R6, R8, R10, R11 | APPLIED — §5.2 row + §8.7 spec now state "1-per-event fire-tick" semantics with rationale; `shots1s` cell re-described | §3.5, §4.2, §5.2, §8.7 |
| F-13 | MAJOR | §5.2 claim "Both reuse `WorldEventBus.PublishFromWorker` → `DrainMainThreadQueue`" misdescribes the producer — `SmartEventBridge.cs:349` calls `WorldEventBus.Publish` synchronously; main-thread delivery comes from the engine's main-thread Damageable hook — flagged by R2, R11, R12 | APPLIED — §5.2 cross-tank-attribution paragraph rewritten to correctly describe the synchronous Publish + engine-side main-thread invocation | §5.2 |
| F-14 | MAJOR | ActionValue reward formula needs a HpFraction history ring that HealthSidecar.Get(id) does not provide — flagged by R1, R2, R6 | APPLIED — §8.2 HealthSidecar DELTA widened (+30 LOC) to add per-tech `(tickMono, hpFraction)` ring 16-deep + `GetAt(TechId, monoCutoff)` accessor; §7.2 reward paragraph updated to cite the new API | §7.2, §8.2, §10 |
| F-15 | MAJOR | `Tank.AttachEvent` is at Tank.cs:262 (not :264); :264 is `DetachEvent` — flagged by R1, R2, R4, R6, R11 | APPLIED — §5.3 cite split: AttachEvent :262, DetachEvent :264 | §5.3 |
| F-16 | MAJOR | §4.3 derivation table internal slot collisions: `Distance → AV[24]` conflicts with `AnchorStateCode → AV[24]`; `DominantDamageType → AV[35]` conflicts with `SelfLastAttackerAge → AV[35]`; row 1 had corrupt placeholder "ActionValue[40-41 minus 0-1]" — flagged by R5, R6 (claim verified against §3.3) | APPLIED — §4.3 table rewritten: Distance is no longer mapped to an AV self-slot (derived at consume time); DominantDamageType is only Threat[35] / Intent[22..23] not AV[35]; corrupt notation replaced with explicit "derived at consume time from AV[0..1] vs AV[40..41]" | §4.3 |
| F-17 | MAJOR | §3.1 declares per-view extents (IntentDim=40, AVDim=128, ResidualDim=48, ThreatDim=48 — sum=264) but `MaxSlots=256` and never declares per-view base offsets; risk of silent slot collision across views — flagged by R6 (also implicit in R5, R8) | APPLIED — §3.1 adds explicit `IntentBase=0 / ActionValueBase=40 / ResidualBase=168 / ThreatBase=216` constants + MaxSlots bumped to 264; clarifies slot constants are view-local | §3.1 |
| F-18 | MAJOR | §4.4 cite "LeadResidualRecorder.cs:99 reads at fire-time" is wrong — :99 is observe-time enqueue; fire-time site is upstream — flagged by R7 (verified by R3's adjacent analysis) | APPLIED — §4.4 readers section + §9 LearningService row corrected: :99 = observe-time enqueue; fire-time site is in the same file upstream | §4.4, §9 |
| F-19 | MAJOR | §6.5 Generation bump on `Tank.TankRecycledEvent` contradicts established pattern (TRAINING-DIRECTOR-PLAN §RM-9 = `IAIForm.OnTechRecycle`) — flagged by R3 | APPLIED — §6.5 Lifecycle-race row now routes Generation through `IAIForm.OnTechRecycle(helper)` at TankAIHelper.cs:967, not the engine event | §6.5 |
| F-20 | MAJOR | §8.5 DamageType source mis-cited: `Damageable.DamageableType` is per-block (varies by hit location), not tech-wide; correct source is `info.DamageType` (`ManDamage.DamageInfo.DamageType` at ManDamage.cs:58) — flagged by R5 | APPLIED — §8.5 row + §3.5 slot 35 source corrected to `info.DamageType` | §3.5, §8.5 |
| F-21 | MINOR | `SmartIdentityStamp` is invented naming — actual is `SmartIdentity` enum + `Identity.SmartIdentityClassifier.Classify(...).Identity` — flagged by R1, R4, R6, R7 | APPLIED — §3.2 slot 39, §3.3 slot 39, §3.5 slot 6 source labels rewritten | §3.2, §3.3, §3.5 |
| F-22 | MINOR | `BeliefSnapshot.ByTech.TryGet` should be `TryGetValue` (IReadOnlyDictionary at BeliefState.cs:304) — flagged by R1, R2, R5, R6, R11 | APPLIED — §4.2 pseudocode | §4.2 |
| F-23 | MINOR | `ModuleItemHolder.TotalCapacity` does not exist — must use `m_CapacityPerStack * m_Stacks.Length` or `GetTotalCapacityForLimiter()` — flagged by R1, R6 | APPLIED — §3.3 slot 25 cell rewritten with publisher-computed capacity | §3.3 |
| F-24 | MINOR | `Provoked` is `int` countdown, not `bool` — flagged by R1, R4, R5, R6, R9, R11 | APPLIED — formulas everywhere now use `Provoked > 0 ? 1f : 0f` semantics, with explicit "countdown not flag" note | §3.2, §3.3, §3.5, §4.3, §5.1 |
| F-25 | MINOR | `IModuleWeapon.ReadyToFire / FiringObstructed` are methods, not properties — flagged by R12 | APPLIED — §8.1 row updated with `()` notation | §8.1 |
| F-26 | MINOR | `WeaponProfileBuilder.cs:84-103` cite is incorrect — class lives inside `Smart/Vehicle/WeaponProfile.cs:72` — flagged by R1, R4 | APPLIED — §5.3 row annotated with correct location | §5.3 |
| F-27 | MINOR | `ArmorMap.QueryWeakFace(...).normal` field name wrong — actual is `FaceNormalLocal` (ArmorMap.cs:9) — flagged by R4, R6 | APPLIED — §3.3 slot 28 + §3.5 slot 26 rewritten to use `FaceNormalLocal` with explicit local→world rotation note | §3.3, §3.5 |
| F-28 | MINOR | §3.6 reserved-ballistics category never used in §3.4 slot tables — flagged by R1, R11 | APPLIED — §3.6 row merged into reserved-projectile-physics | §3.6 |
| F-29 | MINOR | LOC mismatch §1 (3870) vs §10 (~2905) — flagged by R1, R3, R4, R5, R6, R7, R8, R9, R11, R12 | APPLIED — §1 header + body LOC tightened to ~3 050; §10 reconciliation updated; line-items now match within overhead margin | §1, §10 |
| F-30 | MINOR | §11 items 6, 7 are code questions not runtime observations — flagged by R1, R9, R10 | APPLIED — §11 items 6 and 7 removed; resolution recorded in implementation specs (§5.1, §7.2) | §11 |
| F-31 | MINOR | §2 GRU "froze pre-`0002_BpttUnfreeze`" tense wrong — current `ArchitectureVersion=2` means BPTT is already unfrozen | NOT APPLIED — §2 narration is historically correct: prior to migration `0002_BpttUnfreeze`, the GRU recurrent weights WERE frozen; the migration unfroze them. The current `ArchitectureVersion=2` reflects post-unfreeze state. No edit needed. |
| F-32 | MINOR | "AttackerProvoked / AttackerRoleHint / AttackerWaterlogged / AttackerEnergyStored read fields nonexistent on opaque enemies" — attacker-side §3.5 features mostly zero for non-Smart-team — flagged by R9 | APPLIED — affected attacker-side §3.5 cells annotated with "when attacker is Smart-team / else 0" | §3.5 |
| F-33 | MINOR | `WeaponsFiredEvent.Send` is parameterless (TechWeapon.cs:626) — confirms F-12 | APPLIED — covered by F-12 edit | §8.7 |
| F-34 | MINOR | Withdrawn — §3.3 slot allocation math is internally consistent (40+40+10+38=128); R1 self-withdrew M2 after re-check | NOT APPLIED — no defect; left for record | — |
| F-35 | MINOR | `TimeOfDayCosine` background-daemon access to ManTimeOfDay.inst (Singleton) — covered by F-04 | APPLIED — covered by F-04 edit | §3.2 |
| F-36 | MINOR | §11 item 5 (TerrainMap snapshot age tolerance) — borderline runtime vs code, retained as runtime question; §11 itself reworded to call out the removal of items 6 & 7 | APPLIED — §11 reworded; item 5 retained with note | §11 |

#### Counts by category

| Category | APPLIED | REJECTED |
|---|---|---|
| Fictional API (A) | 9 | 0 |
| C# 7.3 / .NET 4.6.1 (B) | 0 | 0 (clean — no violations found) |
| Unity threading (C) | 4 (covered by F-04 and F-07) | 0 |
| Thread safety (D) | 3 (F-09 label leak, F-13 publisher description, F-19 lifecycle race) | 0 |
| Implementation completeness (E) | 8 (F-05 sidecar APIs, F-06 ArmorMap, F-14 HP ring, F-16 slot collisions, F-17 view bases, F-18 fire-time site, F-20 DamageType source, F-30 NEEDS-EVIDENCE cleanup) | 0 |
| Engineering plausibility (F) | 2 (F-29 LOC, F-02 migration) | 0 |
| Format (G) | 8 minor cite/path/typing fixes (F-10, F-11, F-15, F-21, F-22, F-23-F-28) | 1 (F-31 narration is correct; F-34 withdrawn by reviewer) |

#### Items rolled forward to round 2

None — all reviewer findings either applied or rejected with justification. Round 2
should focus on: (a) verifying the new §5.1 SelfStateProbe field list is complete
against the §3.2-§3.5 derivation table; (b) implementation-completeness re-pass on
§4.3 (the table is now denser); (c) confirming the §3.1 view-base reorganization
doesn't break the §7 model files' input-slice math.

### Round 2 — architecture-correctness pass (5 reviewers + finalization agent, applied)

5 reviewers re-read the post-round-1 plan against the source tree. A final
reconciliation agent ran independent spot-checks (~12 cites verified) and merged
findings under the rule: ≥2 reviewers + finalizer = apply; 1 reviewer + finalizer
also sees it = apply; otherwise investigate.

C# 7.3 / .NET 4.6.1 scan: **clean** — round-2 reviewers re-confirmed no banned
features in any code block. Threading discipline post-F-04 remains correct.

#### Round-2 disposition table

| ID | Severity | Finding | Disposition | Edit |
|---|---|---|---|---|
| R2-01 | BLOCKER | §9 SmartEventBridge row at line 1064 STILL cites `info.Damageable.DamageableType` despite F-20 supposedly applying — flagged by R2-Reviewer-3, R2-Reviewer-5 + finalizer (independent spot-check at `Smart/Integration/SmartEventBridge.cs` plus `ManDamage.cs:54-88` confirms `DamageInfo` has no `Damageable` field) | APPLIED — §9 SmartEventBridge row + cite rewritten to `info.DamageType` (ManDamage.cs:58); explicit "NOT `info.Damageable.DamageableType`" guard added | §9 |
| R2-02 | BLOCKER | `TerrainMap.QuerySnapshot()` is fictional — `TerrainMap` exposes `Buffer` (Pathing/TerrainMap.cs:94) and the query methods (`HeightAt`/`SlopeAt`/`RaycastSegment`) are on `TerrainMap` itself (`:218/256/275`), not on `TerrainMapSnapshot` — flagged by R2-Reviewer-4, R2-Reviewer-5 + finalizer (verified at Pathing/TerrainMap.cs) | APPLIED — §4.2 pseudocode rewritten: removed `terrainMap.QuerySnapshot()`, calls go directly to `terrainMap.SlopeAt/HeightAt/RaycastSegment`; explicit comment that the methods live on `TerrainMap` per :218/256/275; F-07's "any-thread" contract is unchanged | §4.2 |
| R2-03 | BLOCKER | `TerrainMapSnapshot.IsFreshlyAllocated` is fictional — `IsFreshlyAllocated` is on `TerrainMap` (Pathing/TerrainMap.cs:104), not the snapshot — flagged by R2-Reviewer-4, R2-Reviewer-5 + finalizer | APPLIED — §4.2 gate is now `terrainMap.IsFreshlyAllocated` (the TerrainMap instance) | §4.2 |
| R2-04 | BLOCKER | LeadResidualRecorder "fire-time site upstream in the same file" is fictional — `OnFireCommit` (LeadResidualRecorder.cs:48) stores only `Pending {PredictedPos, OwnPos, ProjTimeSec, FireTickMono}`; ALL features are built inside `OnObservation` (`:79+`). The plan needs NEW code to widen `Pending` with a `float[] FireTimeFeatures` field and widen `OnFireCommit` to accept a Features array — flagged by R2-Reviewer-2, R2-Reviewer-4 + finalizer (verified at LeadResidualRecorder.cs:34-40,48,79+) | APPLIED — §3.4 closing note, §4.2 NOTE, §4.4 readers paragraph, §9 LearningService row all rewritten to say "Pending struct widened with FireTimeFeatures field; OnFireCommit gains a `float[]` parameter; caller passes the StrategicStateBuffer slice in at fire-commit"; "site exists" framing removed | §3.4, §4.2, §4.4, §9 |
| R2-05 | BLOCKER | `HealthSidecar.Record(TechId, hpFraction)` overload is fictional — actual signature is `Record(TechId id, int blockCount)` (HealthSidecar.cs:40) — flagged by R2-Reviewer-2 + finalizer (verified at HealthSidecar.cs:40) | APPLIED — §7.2 rewritten: existing `Record(TechId, int)` stays; §8.2 DELTA adds a NEW method `RecordHpFraction(TechId, float, long)` and the producer-side push is inside the existing `_state.AddOrUpdate` path. §8.2 row updated to reflect the new method | §7.2, §8.2 |
| R2-06 | BLOCKER | `ContinuousController.OnOperationsTick` arbitrates over `TacticalGoal` (NOT `PlanLibrary.PlanType`); §7.2 said "action `a` is the one-hot index of the PlanLibrary.PlanType plan in effect" — PlanType lives in team Coordination (`Coordination/RoleAssignment.cs:46`), not the per-tech controller — flagged by R2-Reviewer-2 + finalizer (verified at ContinuousController.cs:189-256, Coordination/Coordinator.cs:23/47/65) | APPLIED — §7.2 rewritten: action `a` is the one-hot index of the **goal class** in effect at the decision boundary (external/identity/tactical × goal kind). 10-class enum defined inline; mapping table lives next to the producer in `ContinuousController.cs`. The §3.3 AV[80..89] one-hot is over those 10 classes | §7.2 |
| R2-07 | MAJOR | §3.4 reserved-block count math is wrong: slots 35..47 = 13 (3 ballistics + 3 wind + 7 multi-shooter), not 10; share is ~27% not 21% — flagged by R2-Reviewer-4 + finalizer (verified by counting the §3.4 row) | APPLIED — §3.4 closing line corrected to "13 slots ≈ 27% headroom by category (3 ballistics + 3 wind + 7 multi-shooter)" | §3.4 |
| R2-08 | MAJOR | `SmartIdentityStamp` IS a real type — declared at `Identity/SmartIdentity.cs:30` as `public readonly struct SmartIdentityStamp`; plan repeatedly says "NOT a real type name" — flagged by R2-Reviewer-2 + finalizer (verified at Identity/SmartIdentity.cs:30) | APPLIED — §3.2 slot 39, §3.3 slot 39, §3.5 slot 6 source cells rewritten: `SmartIdentityStamp` is the struct, `SmartIdentity : byte` is the inner enum, `Classify(...)` returns the stamp whose `.Identity` is the enum. The probe still exposes only the byte enum (lighter publish surface), not the full stamp | §3.2, §3.3, §3.5 |
| R2-09 | MAJOR | `TechAI.LastAttackedTime` returns `float` Unity `Time.time`-derived (TechAI.cs:131); plan declared `LastAttackedTimeMono` as `long` MonoClock with no conversion contract — flagged by R2-Reviewer-3, R2-Reviewer-4 + finalizer (verified at TechAI.cs:131) | APPLIED — `LastAttackedTimeMono` renamed to `LastAttackerSeenMono`; the publisher edge-stamps `MonoClock.Now()` on the rising-edge transition of `TechAI.LastAttacker` rather than casting `Time.time` to long; §3.3 slot 35, §4.3 LastAttackerDistance/Age row, §5.1 field-list bullet all updated | §3.3, §4.3, §5.1 |
| R2-10 | MAJOR | §5.1 cadence claim is internally inconsistent: text says "~50 Hz under FixedUpdate" but immediately notes "33 ms cadence" (= 30 Hz) — flagged by R2-Reviewer-1 + finalizer (verified at SmartForm.cs:770-776 `ObservationIntervalMs = 33`) | APPLIED — §5.1 cadence paragraph reconciled to 30 Hz (the gated `ObserveWorldTechsIfDue` throttle); §5.4 / §6.3 budget headers updated from "per tick" to "per publish" | §5.1, §5.4, §6.3 |
| R2-11 | MAJOR | Round-1 §4.4 reader-claim mis-categorizes slot 35 as straddling attacker/victim; slot 35 (`RecentDamageDealtTypeCode`) is attacker-side only — flagged by R2-Reviewer-5 + finalizer (verified against §3.5 layout) | APPLIED — §4.4 reader paragraph and §9 LearningService row both clarify that slot 35 is attacker-side; victim-only slots in the 0..35 range are 30, 31 (`VictimWeakFaceHp` / `VictimHpFraction`) | §4.4, §9 |
| R2-12 | MAJOR | Snapshot tear "Snapshot is an immutable class — no tear possible" understates that `Raw` is `public readonly float[]` (mutable elements). §6.5 mitigation should document writer/reader discipline — flagged by R2-Reviewer-5 + finalizer | APPLIED — §3.1 has a new disciplinary CAVEAT block; §6.5 Snapshot-tear row rewritten to document the readonly-vs-mutable-element distinction and that the NaN/Inf scrub is the last pre-publish edit | §3.1, §6.5 |
| R2-13 | MINOR | DamageHintBuffer ctor "widened from 4 to 5 fields" wording is wrong — the struct already has 6 fields (P11 T6 Item 68 added ImpactPositionWorld / ImpactDirectionWorld); the byte is the 7th — flagged by R2-Reviewer-2 + finalizer (verified at DamageHintBuffer.cs:12-37) | APPLIED — §5.2 DamageHintBuffer extension row corrected to "extending the existing 6-field readonly struct; DamageType is the 7th field; ctor widened to 7 args" | §5.2 |
| R2-14 | MINOR | Vector3.xz pseudocode is a swizzle that does not exist on UnityEngine.Vector3 — flagged by R2-Reviewer-1 + finalizer | APPLIED — §4.2 pseudocode now carries an implementation note that `.xz` must be materialized via `new Vector2(v.x, v.z)` (Unity 4.6.1 has no Vector3 swizzle) | §4.2 |
| R2-15 | MINOR | New sidecar ring depth = 16 may saturate under sustained combat; existing `DamageHintBuffer.CapacityPerTech = 8` is also constrained — flagged by R2-Reviewer-5 + finalizer | APPLIED — §11 adds a new runtime-observation row (#6) for sidecar ring saturation under storm; existing depths preserved pending empirical data | §11 |
| R2-16 | MINOR | HealthSidecar GetAt thread-safety discipline under daemon access was unscoped — flagged by R2-Reviewer-5 + finalizer | APPLIED — §8.2 row now spells out the companion `ConcurrentDictionary<TechId, HpHistoryRing>` + per-instance lock pattern mirroring DamageHintBuffer.Ring (DamageHintBuffer.cs:67-93) | §8.2 |
| R2-17 | MINOR | §11 NEEDS-EVIDENCE row 1 bucket-sizing assumes 2 km × 2 km but `TerrainMap` is configured 512 × 512 × 4 m = 2.048 km × 2.048 km centered on origin; chunked-load OPEN at TerrainMap.cs:62 — flagged by R2-Reviewer-3 + finalizer | APPLIED — §11 adds a new row (#7) noting the configured extents and the chunked-load OPEN | §11 |
| R2-18 | MINOR | §9 LearningService Intent-tee cite `:186` is the classifier-enqueue line; the actual fill-cascade site is `TargetObservationSequenceBuffer.cs:54` (RecordRow) + `:88` (TryBuildEvent snap copy) — flagged by R2-Reviewer-4 + finalizer | APPLIED — §9 LearningService row + cite column updated to `:54,88` | §9 |
| R2-19 | MINOR | ArmorMap source cell "ArmorMap exposes `FaceNormalLocal` only (ArmorMap.cs:9)" is imprecise — `FaceNormalLocal` is on `ArmorQueryResult` struct, not the `ArmorMap` class — flagged by R2-Reviewer-3 | NOT APPLIED — minor wording precision; the existing F-27 edit already documents the rotation step. The reader who follows the cite to ArmorMap.cs:9 lands on the `FaceNormalLocal` declaration line; the call-site math is correct. Leaving as-is | — |
| R2-20 | MINOR | `LearningService.cs:25-29` TODO actually lives at `:23-29` — single-reviewer cite drift, repeated 4× in plan — flagged by R2-Reviewer-2 | NOT APPLIED — cite drift of 2 lines; F-29 reconciliation already corrected the §9 row's `:25-29` to `:23-29`. Other instances are in narrative prose where exact-line precision is not load-bearing; the TODO is the same TODO. Skipped to avoid churn | — |
| R2-21 | MINOR | HealthSidecar producer-side push to the new ring (where does the existing block-attach/HP path call the new method?) — flagged by R2-Reviewer-3 (LOC-optimism concern) | APPLIED — covered by R2-05 edit: §8.2 row now describes the producer touch as "immediately downstream of `_state.AddOrUpdate`" with a single forward to `RecordHpFraction(id, current/max, MonoClock.Now())` | §8.2 |

#### Round-2 counts by category

| Category | APPLIED | REJECTED |
|---|---|---|
| Fictional API (A) | 5 (R2-01, R2-02, R2-03, R2-04, R2-05) | 0 |
| C# 7.3 / .NET 4.6.1 (B) | 0 | 0 (clean — re-confirmed) |
| Unity threading (C) | 0 | 0 (re-confirmed post-F-04) |
| Thread safety (D) | 2 (R2-12 immutability discipline, R2-16 GetAt locking) | 0 |
| Implementation completeness (E) | 6 (R2-06 action mapping, R2-07 reserved math, R2-08 SmartIdentityStamp, R2-09 LastAttackerSeen clock, R2-10 cadence, R2-11 slot 35 attribution) | 0 |
| Engineering plausibility (F) | 1 (R2-15 ring saturation NEEDS-EVIDENCE) | 0 |
| Format (G) | 5 (R2-13 ctor widen wording, R2-14 swizzle note, R2-17 TerrainMap extents, R2-18 Intent-tee cite, R2-21 producer touch) | 2 (R2-19 ArmorMap source wording — load-bearing precision unchanged; R2-20 LearningService cite drift — 2-line minor not worth churn) |

#### Round-2 confidence note

Independent finalization spot-checks performed against source: `HealthSidecar.cs`
(verified `Record(TechId, int)` at :40 — no float overload); `Pathing/TerrainMap.cs`
(verified `IsFreshlyAllocated` on `TerrainMap` at :104, not snapshot; `Buffer`
property at :94; query methods on `TerrainMap` instance per :218/256/275);
`LeadResidualRecorder.cs` (verified `Pending` struct at :34-40 has no Features
array; OnFireCommit at :48 has no Features parameter; ALL feature building inside
OnObservation at :79+); `ContinuousController.cs:189-256` (verified arbitration over
`TacticalGoal`, not `PlanLibrary.PlanType`); `Identity/SmartIdentity.cs:30` (verified
`SmartIdentityStamp` IS a real `readonly struct`); `Coordination/Coordinator.cs:23/47/65`,
`Coordination/PlanDecomposition.cs:27+` (verified `PlanLibrary.PlanType` lives in
team Coordination, not per-tech control); `DamageHintBuffer.cs:12-37,62,67-93`
(verified 6-field struct already exists, ring depth 8, ring-class lock pattern);
`SmartForm.cs:770-776` (verified `ObservationIntervalMs = 33`); `TechAI.cs:131`
(verified `LastAttackedTime` is `float` Unity Time.time); `ManTimeOfDay.cs:161`
(verified `TimeOfDayPrecise` is float, accessed via Singleton.Manager<T>.inst);
`ManDamage.cs:58` (verified `DamageInfo.DamageType`; no `Damageable` field).

Glossed: full LOC reconciliation (totals still match §10 within overhead margin
after the textual edits); §8.3 NearestTechCache sketch (no implementation detail
changed; reviewers did not flag); migration line-by-line (R1 F-02 already shipped
the renumber).

#### Items rolled forward to round 3+ (if scheduled)

None — every reviewer finding is APPLIED or REJECTED with justification recorded.
Plan is **ready-to-ship for implementation**.

  * Round 3 (LOC-budget verifier pass): _(empty)_
  * Round 4 (threading-contract verifier pass): _(empty)_
  * Round 5 (slot-collision verifier pass): _(empty)_
