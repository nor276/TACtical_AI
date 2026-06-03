# DRIFT-DECISIONS

Per FIX-PLAN.md Phase 10 — one entry per spec-vs-code drift resolved during Phase 7
execution. Each entry names what disagreed, which side won, and why.

Policy (Plan 6 framing): "Spec wins when it's design intent; code wins when it's
engineering reality." Apply case-by-case.

---

## D.1 — Kalman gain divisor: S[col,col], not S[row,row]
- **Spec (WORLD-CONTRACT §3.3):** K = P · S⁻¹ under diagonal-S approximation.
- **Code (before):** divided by `S[row*N+row]`.
- **Decision:** Spec wins. Fixed to use `S[col,col]` (hoisted into `invSDiag[col]`).
- **Why:** The diagonal-S approximation makes S⁻¹ a diagonal matrix with entries
  1/S[i,i]. (P·S⁻¹)[row,col] = P[row,col] · (S⁻¹)[col,col] = P[row,col] / S[col,col].
  Using S[row,row] is only correct on the diagonal — for off-diagonal cells (which
  appear after the first Propagate's F·P·F^T introduces position–velocity coupling)
  the gain matrix was numerically wrong from that tick onward.

## D.2 — MobilityProfile.TopSpeed: sqrt(a/k), not sqrt(2·a/k)
- **Spec (VEHICLE-CONTRACT §8.2):** "Steady-state speed under quadratic drag."
- **Code (before):** `Mathf.Sqrt(2f * a / DragK)` — the factor of 2 had no source.
- **Decision:** Spec wins. Fixed to `Mathf.Sqrt(a / DragK)`.
- **Why:** Force balance under quadratic drag is a_thrust = k·v² ⇒ v = sqrt(a/k).
  The √2 inflated every top speed by ~1.41× and propagated into role-fit, target
  reach, and MPC feasibility.

## D.3 — MobilityProfile.TurningRadius: v² / a_lat, not v / α
- **Spec (VEHICLE-CONTRACT §8.2):** centripetal-limited cornering radius.
- **Code (before):** `topFwd / angAccel.y` — units m·s/rad, dimensionally wrong.
- **Decision:** Spec wins. Fixed to `v² / a_lat`.
- **Why:** Centripetal acceleration a = v²/r ⇒ r = v²/a. Using yaw angular accel as
  the limit conflates "how fast it CAN turn its heading" with "how tight an arc it
  can hold at speed." The latter is bounded by lateral linear accel.

## D.4 — ThreatFieldCost: sum, not average
- **Spec (CONTROL-CONTRACT §5.4):** `c_threat = Σ_k threat(trajectory[k])`.
- **Code (before):** `total / trajectory.Length` (mean form).
- **Decision:** Spec wins. Removed divide; restored sum form.
- **Why:** Averaging makes the term horizon-length-invariant, which defeats the
  spec's W_Threat = 2.0 vs W_Reach = 1.0 dominance — a longer trajectory through
  a high-threat field should cost more than a shorter one. The W_Threat coefficient
  is itself tunable, so the magnitude rescaling is encoded there.

## D.5 — Receding-horizon control: Steps[k] indexed by tick offset
- **Spec (CONTROL-CONTRACT §9.1):** controller advances through Steps[] over time.
- **Code (before):** `OnControlFrame` always picked Steps[0], discarding the
  multi-step MPC plan between publishes.
- **Decision:** Spec wins. Added a frame-tick counter; Steps[clamp(tick − ValidFrom)]
  is read. Stale profile (counter past ValidThrough) holds the final extrapolated
  step rather than reverting to Neutral.
- **Why:** The whole point of publishing N future steps is that the controller can
  drive through them when MPC publishing stalls. Reverting to Neutral on stall
  brakes the tech mid-maneuver.

## D.6 — Brake semantics: actuator-side multiplier, not throttle subtract
- **Spec (CONTROL-CONTRACT §9.2):** throttle ∈ [−1, +1], brake ∈ [0, 1], brake
  attenuates forward motion.
- **Code (before):** `drive = throttle − brake`. Requesting (throttle=0.3, brake=0.7)
  produced reverse.
- **Decision:** Spec wins. Drive command = `max(0, throttle) · (1 − brake) + min(0, throttle)`.
- **Why:** Brake should not flip forward to reverse. The actuator side now matches
  the rollout side (PhysicsRollout previously applied brake twice — once attenuating
  thrust, once damping velocity — fixed to apply once).

## D.7 — WeaponFireController arc test: yaw AND pitch
- **Spec (CONTROL-CONTRACT §10.2):** yaw and pitch arcs are independent constraints.
- **Code (before):** single 3D half-angle test against YawArcRadians only.
- **Decision:** Spec wins. Project required aim into chassis frame; check yaw and
  pitch separately.
- **Why:** A fixed-pitch barrel mounted at 0° could be "in-arc" for a target 60°
  above horizon as long as the bearing was within yaw — clearly not what the spec
  meant.

## D.8 — WeaponFireController hysteresis: keyed by TechId, not slot
- **Spec (CONTROL-CONTRACT §10.6):** sticky-target within an engagement.
- **Code (before):** `CurrentTargetSlot` indexed into the per-tick belief list.
- **Decision:** Spec wins. Switched to `CurrentTargetTechIdValue`.
- **Why:** Belief-list ordering is `Dictionary.ByTech` iteration order — unstable
  across ticks. A weapon "locked on slot 2" would silently swap targets whenever
  the dictionary's underlying buckets renumbered.

## D.9 — Energy reserve refill
- **Spec (CONTROL-CONTRACT §10.4):** energy depletes when firing, refills otherwise.
- **Code (before):** only depletion path existed. Once reserve dropped below
  threshold, every energy weapon was permanently locked out.
- **Decision:** Spec wins. Added `ApplyEnergyRefillTick(dt)`; called by
  ContinuousController before the fire pass.
- **Why:** A one-way depletion model is engineering oversight, not design intent.

## D.10 — TacticalOptimizer.LearningRate default: 0.1, not 0.5
- **Spec (CONTROL-CONTRACT §6.3):** provisional α = 0.1.
- **Code (before):** `LearningRate = 0.5f` (no comment explaining 5× spec).
- **Decision:** Spec wins. Default lowered to 0.1.
- **Why:** α = 0.5 moves the goal ~5 m per Operations tick at 30 Hz × 10 inner
  steps — the MPC chases a target moving at 150 m/s in goal-space.

## D.11 — SamplingMPC seed: deterministic-per-team
- **Spec (TRAINING-CONTRACT §5.2):** reproducibility required for training.
- **Code (before):** `new SamplingMPC()` → `Random()` time-seeded.
- **Decision:** Spec wins. Per-tech deterministic seed from `TechId * 2654435761 + teamId`.
- **Why:** Two techs spawned in the same ms saw identical sample streams →
  identical MPC outputs (lock-step trajectories). Training reproducibility also
  required this fix.

## D.12 — OutcomeScorer.DeriveWinner: symmetric swap requires Them telemetry
- **Spec (TRAINING-CONTRACT §4.3):** "Compute their score symmetrically."
- **Code (before):** swap zeroed `ThemStartingHpTotal`, `ThemEndHpTotal`,
  `ThemShotsFired` (the fields didn't exist on MatchOutcome).
- **Decision:** Spec wins. Added the three fields and populated them from
  TrainingMatch's per-team counters.
- **Why:** Without them, FriendlyPreservation and WeaponEfficiency returned 0 for
  Them — every both-alive tiebreak biased toward Us by +2.5 weight-units.

## D.13 — StrategicValueFunction.ThreatExposure: per-friendly max-then-mean
- **Spec (PLANNING-CONTRACT §5.4):** "exposure of each friendly to the threat field."
- **Code (before):** Σ_i Σ_j contribution / (N_F · N_H) — scale-dependent on team
  sizes; one-on-one and ten-on-ten read different scales at identical geometry.
- **Decision:** Spec wins. Per-friendly clamp-then-mean shape.
- **Why:** The contract intent is "any single friendly in mortal danger pulls value
  down." Hostile-count normalization diluted that. New shape is intrinsically
  scale-invariant in N_H.

## D.14 — PUCT: dictionary access defensive + deterministic FirstAction
- **Spec (PLANNING-CONTRACT §2.4):** reproducible search ordering for training.
- **Code (before):** `node.VisitsByAction[hash]` direct indexer; `FirstAction`
  returned dict-enumerator order.
- **Decision:** Spec wins. TryGetValue with sane defaults + lowest-hash tiebreak.
- **Why:** Dictionary enumeration order is not contractually deterministic in
  .NET; reproducibility for training matters more than the (tiny) cost.

## D.15 — PlanLibrary.EngageDistributed: requires ≥2 hostiles
- **Spec (PLANNING-CONTRACT §3.2):** EngageDistributed = "split friendlies across
  multiple targets."
- **Code (before):** legal when `nHostile > 0`, even with one hostile (duplicate of
  EngageFocused).
- **Decision:** Spec wins. Gate raised to `nHostile >= 2`.
- **Why:** Two plans rolling out to the same outcome waste search budget and let
  the visit-count tiebreak pick either by coincidence.

## D.16 — DefensivePerimeter heading: outward, not the parametric angle
- **Spec (COORDINATION-CONTRACT §6.4):** defenders face outward.
- **Code (before):** heading = the perimeter parametric angle (0..2π) — meaningless
  as a yaw value.
- **Decision:** Spec wins. heading = π/2 − angle (yaw convention atan2(x, z)).

## D.17 — MobileScreen: speed/position units separated
- **Spec (COORDINATION-CONTRACT §6.5):** "screen at speed v_screen, holding line
  formation."
- **Code (before):** plan.Scalar (intended as speed m/s) was used as the advance
  distance in meters — small-fast tech advanced 5 m, big-slow tech advanced 50 m.
- **Decision:** Spec wins. plan.Scalar feeds velocity only; advance distance is a
  fixed 30 m.

## D.18 — Bait flanker distribution: arc-spread, not point-stack
- **Spec (COORDINATION-CONTRACT §6.7):** "flankers encircle the target."
- **Code (before):** every flanker (i ≥ 1) targeted the SAME world point.
- **Decision:** Spec wins. Alternating sides + arc-spread by pair index.

## D.19 — FightingRetreat: group-paced, not per-tech-paced
- **Spec (COORDINATION-CONTRACT §6.8):** "mutual support during withdrawal."
- **Code (before):** each tech retreated at its own top-speed — fast techs left
  slow techs behind, breaking mutual support.
- **Decision:** Spec wins. Pace everyone to the group's slowest top-speed.

## D.20 — Flank decomposition: role-aware
- **Spec (COORDINATION-CONTRACT §6.6):** "flanker approaches from the flank;
  pursuer maintains frontal pressure; holder anchors."
- **Code (before):** every tech went to the deep-flank standoff regardless of role.
- **Decision:** Spec wins. Switch on `Role`.

## D.21 — TargetAssignment.COverlap and broader plan-bias
- **Spec (COORDINATION-CONTRACT §4.2):** COverlap discourages re-piling; plan bias
  spans EngageFocused, Flank, Bait.
- **Code (before):** COverlap declared but never applied; plan bias only EngageFocused.
- **Decision:** Spec wins. Added previous-tick tally penalty; extended bias.

## D.22 — Coordinator publishes unified `CoordinationState`
- **Spec (COORDINATION-CONTRACT §3.1, §8.4):** `CoordinationState { TargetMap,
  RoleMap, LOSCoverage, ActivePlan }` published via team DoubleBuffer.
- **Code (before):** per-tech Goal/Target buffers published, no unified state.
- **Decision:** Spec wins. Added `CoordinationState` + `StateBuffer` on Coordinator.
  Per-tech publish paths retained — they feed Control's existing read shape.

## D.23 — Adam state.T: long, not int
- **Spec (LEARNING-CONTRACT §3.5):** Adam's bias correction terms must not overflow.
- **Code (before):** `int _t` — overflows after ~2¹⁹ updates × Adam every tick.
- **Decision:** Code wins (changed). Spec didn't explicitly call out long, but the
  spec's correctness requirement implicitly requires it. Promoted both
  OnlineTrainer.AdamState.T and TacticalOptimizer._t to long.

## D.24 — WrapAngle: closed-form Mathf.Repeat, not while-loop
- **Spec:** angle normalization to (−π, π].
- **Code (before):** while-loop `while (angle > π) angle -= 2π` — O(|angle|/π).
- **Decision:** Code wins (improved). `Mathf.Repeat(angle + π, 2π) − π` is O(1).
- **Why:** A NaN-poisoned or runaway angle could hang the worker thread for
  ~1.6e9 iterations. Closed form is unconditionally fast.

## D.25 — Library scenario load: procedural fallback, not null
- **Spec (TRAINING-CONTRACT §3.4):** library evaluation against fixed scenarios.
- **Code (before):** `LoadLibrary` returned null — every evaluation skipped every
  library entry, win-rate was always 0%, plateau detector misfired.
- **Decision:** Spec wins. Procedural fallback seeded by name; difficulty
  interpolated by name's index in `LibraryNames`. JSON loader deferred to v0.2.
- **Why:** A no-op library evaluation is worse than a procedural one — at least
  procedural produces a non-degenerate signal for the CMA-ES feedback loop.

## D.26 — Plateau detector orientation: older-half max vs newer-half max
- **Spec (TRAINING-CONTRACT §5.4):** "Plateau = improvement has stopped."
- **Code (before):** `return best - currentRate < 0.01f` — misread "we haven't
  regressed" as "we've stopped improving."
- **Decision:** Spec wins. Compare older-window-half max vs newer-half max.

## D.27 — BeliefState initial intent: combat-leaning, not uniform
- **Spec (WORLD-CONTRACT §2.2):** intent prior reflects domain knowledge.
- **Code (before):** uniform 1/6 across all categories — implies a freshly-spotted
  enemy is equally likely to be Idle as Aggressing.
- **Decision:** Spec wins. Combat-leaning prior (Aggressing 0.35, Repositioning
  0.20, Flanking 0.20, Holding 0.10, Retreating 0.10, Idle 0.05).
- **Why:** Smart only constructs beliefs for techs that matter; the prior should
  reflect that.

## D.29 — TankTeamChangedEvent handler signature: (Tank, TeamChangeInfo), not (int, Tank)
- **Spec assumption:** `Action<int, Tank>` (oldTeam, tank).
- **Engine reality (verified at TankAIManager.cs:191):** `Action<Tank, ManTechs.TeamChangeInfo>`.
- **Decision:** Engine wins. Bridge updated to the real shape; `info.m_NewTeam` is the
  verified new-team field. The old team is recovered from Smart's per-tech state
  (`state.TeamId`) when Smart tracks the tech.
- **Why:** No way to invent an event signature that doesn't exist. SHELL-API-GUIDE
  §7.2 row updated to reflect verified shape.

## D.30 — ManDamage.DamageInfo: HitPosition/HitForceDir don't exist
- **Spec assumption:** Position and direction available on the damage struct.
- **Engine reality:** Only `.Damage`, `.SourceTank`, `.SourceTeamID` are verified in
  the existing TAC_AI codebase (TankAIHelper.cs:1326, EnemyMind.cs:212-217). No
  reads of any HitPosition/HitDirection/HitForce field anywhere.
- **Decision:** Engine wins. SanitizedDamageInfo's position and direction fields
  drop to `Vector3.zero` placeholder. Downstream Learning consumers don't read
  these fields at v0.1.0, so no behavior is lost. v0.2 revisits when the canonical
  hit-point access is identified (likely a Harmony patch on a projectile impact
  callback).

## D.31 — NetPlayer.PlayerName: not on the engine type
- **Spec assumption:** `ManNetwork.inst.MyPlayer.PlayerName` returns a stable
  human-readable identifier for the local player.
- **Engine reality:** The property doesn't exist on NetPlayer. No verified read of
  any name-like field on NetPlayer in the existing codebase.
- **Decision:** Defer the MP-distinct-player profile path to v0.2. MP and SP both
  use `DefaultPlayerId` for now. TerraTech doesn't support multi-player on one
  installation, so single-profile-per-install is correct at v0.1.0; only multi-
  user scenarios (e.g., shared family PC) would need real per-player isolation.

## D.32 — Knuth-hash multiplier overflows int literal type
- **Site:** `mpcSeed: TechId.Value * 2654435761 + teamId.Value` in SmartRuntime.cs.
- **Engine reality:** C# parses 2654435761 (> int.MaxValue) as a long literal, so
  the entire expression is long-typed, rejecting the implicit narrowing to int.
- **Decision:** Use explicit uint arithmetic with `unchecked((int)((uint)... * 2654435761u + ...))`.
  Preserves the multiplicative-hash mixing intent without introducing the long.

## D.28 — SmartForm.OnTechSpawn maxAccelEstimate: thrust magnitude, not vertical authority
- **Spec (WORLD-CONTRACT §3.1):** Q matrix bound is the kinematic max acceleration.
- **Code (before):** `Mobility.VerticalAuthority * 9.81f + 5.0f` — unrelated to the
  ground-driving acceleration.
- **Decision:** Spec wins. Use `Max(MaxLinearAccelPositive.magnitude, …Negative)`.
- **Why:** Vertical authority is hover/lift capacity, not forward acceleration;
  a flat-driving wheeled tech got Q≈25 m²/s⁴ regardless of how fast it could
  actually accelerate.
