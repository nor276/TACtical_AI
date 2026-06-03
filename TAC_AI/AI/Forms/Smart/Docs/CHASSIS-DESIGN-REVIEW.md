# Chassis Design REV 3 — 3-Reviewer Adversarial Reconciliation

## Verdict tally
**3/3 SHIP-WITH-MINOR-FIXES — unanimous.**

Architecture, threading model, GC accounting, parallel-run gate, 3-commit cleanup strategy, and Decision-row scoping (v0.1 vs v0.2) all rated sound. Reviewer #3 (implementer-readiness lens) explicitly says "ready to start Step 1 after a ~15-minute doc-touch pass."

## REV 2 → REV 3 flaw matrix

| Flaw | R1 | R2 | R3 | Convergent verdict |
|---|---|---|---|---|
| N1 BoosterJet wrap | FULLY | FULLY | FULLY | **FULLY-FIXED 3/3** |
| N2 BlockKindCounts struct/class | PARTIAL | PARTIAL | FULLY | **PARTIAL** (Decision #11 declares struct, but GC profile §line 390 carries stale "struct/class, 64 B") |
| N3 Folder paths | FULLY | FULLY | FULLY | **FULLY-FIXED 3/3** |
| N4 ForwardDirectionLocal v0.1 mud | FULLY | FULLY | FULLY | **FULLY-FIXED 3/3** |
| N5 Step 6 → 3.5 ordering | FULLY | FULLY | FULLY | **FULLY-FIXED 3/3** |
| N6 Quaternion vs Vector3 | FULLY | FULLY | FULLY | **FULLY-FIXED 3/3** |
| N11 JetExhaust dead enum | FULLY | FULLY | FULLY | **FULLY-FIXED 3/3** |
| N13 FanJet double-transform | FULLY | FULLY | FULLY | **FULLY-FIXED 3/3** |
| N16 Self-contradictory rename | FULLY | FULLY | FULLY | **FULLY-FIXED 3/3** |
| F2 LearningService comment-only | FULLY | FULLY | n/a | **FULLY-FIXED 2/2** |

All 10 REV 2 convergent fixes are real (not cosmetic). One PARTIAL (N2) is purely a downstream text-scrub gap.

## Convergent NEW flaws (≥2 reviewers)

| # | Sev | Flaw | Reviewers | Fix |
|---|---|---|---|---|
| **M1** | MINOR | Stale "Step 6" references in body (line 212 inline comment + line 446 Failure mode #4) — should be Step 3.5 | 3/3 | Trivial text edit |
| **M2** | MINOR | `BlockKindCounts` GC-profile entry (line 390) says "struct/class, ~64 B" — contradicts Decision #11's `readonly struct, 72 B` | 3/3 | Line 390 rewrite |
| **M3** | MINOR | `AIControllerAir.cs:158/177` cited as InverseTransformDirection examples — those lines actually read bare `LocalThrustDirection` (no wrap). REV 2 F5 carried into REV 3 uncorrected. | 3/3 | Drop those citations from line 134 |
| **M4** | MINOR | `ThreatField.cs` double-classified: appears in both "touched outside Vehicle" table AND zero-change-consumer list | R1+R3 | Remove from one list |
| **M5** | MINOR | "Touched files outside Vehicle/ (verified count: 6)" — actually 5; row 2 (`Vehicle/VehicleModel.cs`) IS in Vehicle/ | R1+R3 | Update header to 5, remove duplicate row |
| **M6** | MINOR | `BarrelDirBlockLocal` v0.1 population path unspecified — Decision #2 defers honest geometry to v0.2; spec is silent on whether probe sets `BarrelDirBlockLocal` or leaves zero | R2+R3 | Add sentence to Decision #8 or WeaponSpec note |

## Credible singleton findings (1 reviewer each — all worth applying)

| # | Sev | Flaw | Reviewer | Note |
|---|---|---|---|---|
| **S1** | **MAJOR** | `ModuleHover` MaxForce probe source unverified — line 95 says "thrust source verified via ManEnemyWorld.cs patterns, per-pad MaxForce read from existing field access patterns" without file/line citation. **Same epistemic shape as REV 1's `ModuleHammer` phantom.** Implementer cannot write `ProbeHover()` from the spec; v0.1 would ship zero hover lift. | R3 | **Must fix before implementation starts.** Cite the actual field OR explicitly defer to v0.2 with `block.CurrentMass * 9.81 * 2.0f` (2g) placeholder matching today's `ThrustMap.cs:63`. |
| **S2** | **MAJOR** | Probe-side `block.transform.InverseTransformDirection` vs production `tank.rootBlockTrans.InverseTransformDirection` frame-equivalence asserted but not derived. Step 3.5 validates rotation read patterns but NOT the probe-output composition on a tilted booster. | R3 | Add as 5th bullet in Step 3.5: assert `pose.LocalRotation * probeAxis == tank.rootBlockTrans.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection))` on a tilted-mount booster fixture within float-eps. |
| S3 | MINOR | Decision #12 typo: "`FanJet` (was `FanJet` in REV 2)" — should be "was `FanLift`" | R2 | One-token fix |
| S4 | MINOR | Step 8a paragraph oscillates 23 vs 24 consumer counts | R2 | Trivial text edit |
| S5 | MINOR | `BlockKindCounts` has no canonical struct-declaration block in the schema section (siblings all do) | R1 | Add `public readonly struct BlockKindCounts { ... }` block |
| S6 | MINOR | `WeaponKindFlag` turret discrimination at probe time not specified. With placeholder arcs (`yawArc = isTurret ? π : π/4`), the probe must pick a Kind. | R3 | Default to `GunFixed` in v0.1, matching today's `VehicleModel.cs:134` fall-through. Reserved-conservative per Decision-#7 style. |
| S7 | MINOR | `ModuleHover` / `ModuleWing` MaxForce v0.1 placeholder formula unspecified (separate from S1 — S7 is "what value if we defer") | R3 | If S1 deferred, set MaxForce = `block.CurrentMass * 9.81 * 2.0f` (matches `ThrustMap.cs:63`). |
| S8 | MINOR | `spinDat` reflection not pre-cached. Failure mode #8 references it for v0.2; `AIControllerDefault.cs:254` + `AIControllerAir.cs:171` read it today. | R3 | Either pre-cache with v0.2-reserved note OR explicitly state spinDat deferred. |
| S9 | MINOR | Memory footprint understates: per-archetype ~300-600 B not ~200 B; 5000 modded types ~2.5-3 MB | R3 | Restate as 300-600 B/archetype, 100-600 KB typical, bounded ~3 MB worst-case |
| S10 | MINOR | Decision #13 `JetBudget` diagonal-mount aggregation unspecified — does a 45° tilted booster split across `.x` and `.z`? | R2 | Add: "Diagonal-mount emitters contribute to all 3 budget components in proportion to `(pose.LocalRotation * emitter.LocalAxis).{x,y,z} * MaxForceN`." |

## Factual errors (live-code-contradicted)

| Claim | Reality | Source |
|---|---|---|
| Line 134: `AIControllerAir.cs:158/177` are InverseTransformDirection examples | Bare `jet.LocalThrustDirection` / `-boost.LocalThrustDirection`, no inversion. Real sites: `AIControllerDefault.cs:257/263/279/285`, `AIECore.cs:602/615`, `Enemy/RCore.cs:279/295` | Live grep |
| Line 212 inline comment: "rotation-resolved-per-Step-6" | Rotation lock is Step 3.5 in renumbered plan | Doc-internal |
| Line 446 Failure mode #4: "Verified at migration Step 6" | Step 6 is now KickStart flip; fixture verification is Step 3.5 or 5 | Doc-internal |
| Line 390: "BlockKindCounts struct/class, ~64 B" | Decision #11: readonly struct, 72 B (18 × 4 B) | Doc-internal |
| Decision #12: "FanJet (was FanJet in REV 2...)" | History row says rename was "FanLift → FanJet" | Doc-internal |
| Line 305: "Touched files outside Vehicle/ (verified count: 6)" | Table contains 5 unique outside-Vehicle entries + 1 row whose path is inside Vehicle | Doc-internal |

## Ship verdict

**SHIP-WITH-MINOR-FIXES, 3/3 unanimous.** Architecture is sound; every REV 2 convergent flaw is genuinely fixed. The remaining ~16 items are paragraph-level lint, except for **S1 and S2 which are MAJOR (must-fix-before-implementation)** but still small doc edits.

### Recommended path

**REV 3.1 lint pass** — apply all 6 convergent + 10 singletons in one surgical commit (~30 minutes of doc edits). Skip another fan-out review; findings are concrete and reviewer-converged with no architectural disagreement.

Per reviewer #3's implementer-readiness assessment: **after the REV 3.1 lint pass (~15-30 min), an implementer can start Step 1 the next morning.** Migration plan is build-stable with the Step 3.5 hard-gate + parallel-run gate (Step 5) + 3-commit rename (Step 8). No further design work needed.
