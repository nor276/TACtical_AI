# Aether Design REV 3 — 3-Agent Adversarial Review Reconciliation

## 1. Verdict Tally

| Verdict | Count |
|---|---|
| SHIP-AS-IS | 0 |
| **SHIP-WITH-MINOR-FIXES** | **3** |
| NEEDS-REVISION | 0 |
| FATAL-FLAW | 0 |

**Unanimous: SHIP-WITH-MINOR-FIXES (3/3).** The architectural direction is sound. The spec is implementable as written.

## 2. REV 2 → REV 3 Flaw Matrix

Every REV 2 flaw FULLY-FIXED (3/3 agreement) except two R3-only minor singletons (PARTIAL on absolute-value calibration shift, COSMETIC on a label confusion).

| Flaw | R1 | R2 | R3 | Verdict |
|---|---|---|---|---|
| N1 Intent contradiction | FULL | FULL | FULL | **FULLY-FIXED** |
| N2 WithTeam factory missing | FULL | FULL | FULL | **FULLY-FIXED** |
| N3 GC double-count | FULL | FULL | FULL | **FULLY-FIXED** |
| N4 PositionAt signature gaps | FULL | FULL | FULL | **FULLY-FIXED** |
| N5 FromInitial factory missing | FULL | FULL | FULL | **FULLY-FIXED** |
| N6 TeamBelief.cs LOC=0 | FULL | FULL | FULL | **FULLY-FIXED** |
| N7 PositionObservation extract | FULL | FULL | FULL | **FULLY-FIXED** |
| R1 RegisterTech sig (heading → forward) | FULL | FULL | FULL | **FULLY-FIXED** |
| R2 MeanArrayInternal accessor deletion | FULL | FULL | FULL | **FULLY-FIXED** |
| R3 BeliefDecay callers cited | FULL | FULL | FULL | **FULLY-FIXED** |
| R4 R≈0 vs D2 diagnosis contradiction | FULL | FULL | FULL | **FULLY-FIXED** |
| R5 Stopwatch "rebased" wording | FULL | FULL | FULL | **FULLY-FIXED** |
| R5 EventBus.cs:61 obsolete cleanup | FULL | FULL | FULL | **FULLY-FIXED** |
| R2 Step 4 build-broken window | FULL | FULL | FULL | **FULLY-FIXED** (step 4.5 added) |

## 3. NEW Flaws Introduced by REV 3 Edits

### Convergent (≥2 reviewers)

| # | Flaw | Severity | Reviewers | Fix |
|---|---|---|---|---|
| **M1** | Doc title still says "(REV 2)" at line 1; revision history at line 13 marks as REV 3. | MINOR | 3/3 | Bump title to "(REV 3)". One-line edit. |
| **M2** | Missed second `RegisterTech` caller: `SmartEventBridge.cs:286` (`RegisterExternalTech`) also calls `RegisterTech(...heading: 0f, ...)`. Signature change breaks both callers, not one. | MINOR (build-blocker if missed at impl time) | 2/3 | Update Consumer Impact WorldModel row to name both callers; SmartEventBridge row goes from 0 → +2 LOC. |
| **M3** | `BeliefState` ~136 B payload estimate slightly off — field-by-field sum is ~117 B payload + 16 B CLR header on 64-bit ≈ ~144 B; ~136 B is realistic on 32-bit Mono (likely TT's runtime). | MINOR | 2/3 | Either revise to ~144 B or note "32-bit Mono target." |

### Singleton (1 reviewer each — all credible, none architectural)

| # | Flaw | Reviewer | Note |
|---|---|---|---|
| S1 | **Missed `SmartForm.cs:644` orphan-sweep second `PositionObservation.Standard()` callsite.** Build would break at step 5 if missed. | R2 | Build-blocker if missed at impl time. |
| S2 | Step 1.5 alternative clause (extract OR trim-in-place) creates ambiguity vs step 5. | R2 | Pick variant (a) — extract to own file. |
| S3 | Consumer Impact L314 references `PositionObservation.cs` file path that doesn't exist today. | R2 | Mark as "NEW (created in step 1.5)". |
| S4 | D2 recommendation wording undersells the behavior shift — saying "matches today's spirit, low-risk" while D2 IS removing today's data-dependent smoothing. | R2 | Acknowledge "single-frame physics spikes become visible, bounded by MaxAccel·dt." |
| S5 | `BeliefStateFactory` file location unspecified — step 1 lists new files but not `BeliefStateFactory.cs`. | R2 | Either co-locate in `BeliefState.cs` or add as new file in step 1. |
| S6 | `BeliefState` constructor visibility unspecified — factories must call a ctor; spec silent. | R1 | Add note: "internal ctor; public construction via `BeliefStateFactory`." |
| S7 | `IntentProb(int category)` accessor + `IReadOnlyList<float> Mean/Covariance/IntentProbs` not enumerated in deletion list. | R1 | Append to deletion row. |
| S8 | `PositionAt`/`VelocityAt`/`ConfidenceAt` method bodies unspecified — implementer needs to know not to allocate a new BeliefState per read. | R1 | 2-line implementation sketch. |
| S9 | `LastObservedTick` units silently shift — today: worker-tick counter (~30 Hz int); Aether shim: Stopwatch ticks (~10 MHz-1 GHz). No production reader today; latent footgun. | R3 | Add explicit decision row OR rename shim. |
| S10 | `BeliefState.NewlyObserved` static factory deletion not enumerated — today at `BeliefState.cs:145`; called from `WorldModel.cs:59` + 5 test sites. Replaced by `BeliefStateFactory.FromInitial`. | R3 | Append to deletion row OR rename `FromInitial` → `NewlyObserved` to preserve the existing name. |
| S11 | GC "comparison to today" undercounts today's per-tick allocations by ~3× — doc estimates 10-12 KB/tick today; live Kalman temporary arrays push true today figure to ~22-50 KB/tick. **Aether's actual savings are ~70-80%, not 40-50%** — doc UNDERSTATES the win. | R3 | Either revise honestly OR footnote that temporaries aren't counted. |
| S12 | OnTankDamage prose says "publishes Vector3.zero as the damage position" — actually publishes Vector3.zero for BOTH position AND direction. | R3 | Trivial prose fix. |
| S13 | `PerTechEntry.PriorBelief` mutability post-Aether unaddressed — today mutated by both `WorldModel.UpdateTeam` (main) and `WorldModel.PublishPerTechBelief` (worker). "Single writer per channel" conflicts. | R3 | Document the synchronization model OR route `UpdateTeam` through `Observer.Submit`. |
| S14 | "No array sharing" framing for `WithTeam` factory is tautological — new BeliefState has no array fields to share. Leftover concern from old shape. | R3 | Rephrase to "no array sharing concern because the new shape has no array fields." |

## 4. Factual Errors (Live-Code-Contradicted)

| # | Claim | Reality | Source |
|---|---|---|---|
| F1 | "Caller (SmartForm.cs:285) updated accordingly" for RegisterTech sig change | TWO callers: `SmartForm.cs:280-286` AND `SmartEventBridge.cs:286-290` | Live grep |
| F2 | Title "(REV 2)" | Revision history says REV 3 | L1 vs L13 |
| F3 | "today's OnTankDamage publishes Vector3.zero as the damage position" | Publishes Vector3.zero for BOTH position AND force direction | `SmartEventBridge.cs:314-319` |
| F4 | "~136 B (sealed-class with CLR header + 110 B payload)" | Real payload is ~117 B; total ~144 B on 64-bit CLR (or ~136 B on 32-bit Mono) | Field-sum |
| F5 | Single SmartForm `PositionObservation.Standard()` callsite | TWO callsites — primary loop + orphan-sweep at line 644 | `SmartForm.cs:644` |
| F6 | "today ~250 B per tech amortized → 10-12 KB/tick at N=30" | Live Kalman temporary arrays push today to ~22-50 KB/tick (Aether wins by 70-80%, not 40-50%) | `KalmanUpdate.cs:91-92, 105, 167, 177, 197, 224, 240` |

## 5. Ship Verdict

**SHIP-WITH-MINOR-FIXES (3/3 unanimous).** REV 3 is implementable as written. Architectural decisions are sound, the spec is internally coherent, and every REV 2 contradiction is genuinely resolved.

Remaining 3 convergent + 14 singleton findings are **paragraph-level lint**. The two build-window blockers (**M2** + **S1**: missed second `RegisterTech` caller, missed second `PositionObservation.Standard()` callsite) are real but trivial — any implementer hitting the compile error fixes them in seconds.

### Recommended path

1. **REV 3.1 micro-lint** (~10 min): apply M1 (title bump), M2 + S1 (full caller lists), S2/S3 (PositionObservation file timing), S5 (BeliefStateFactory file location), S6 (ctor visibility), S10 (rename `FromInitial` → `NewlyObserved` to preserve the existing name). Single-line doc edits; prevent both real build-window breakages.
2. **Skip another fan-out review** — 3 reviewers converge with no architectural disagreement. Lint edits don't need adversarial review.
3. **Begin Aether migration step 1** (land new types side-by-side).

The remaining S-flaws (S4, S7, S8, S9, S11, S12, S13, S14) are doc-precision improvements that can land as part of the implementation PR's doc updates.
