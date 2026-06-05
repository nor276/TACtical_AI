using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.Templates;
using TerraTechETCUtil;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    // Per-tech identity classifier. Pure function over (helper, snapshot, team).
    //
    // Row evaluation order per Docs/SMART-IDENTITY-DESIGN.md sec 5.2:
    //   1. Base   (defensive intent overrides everything - including mobility)
    //   2. Gatherer
    //   3. AircraftSupport (aircraft + has same-team ally)
    //   4. AircraftHunter  (aircraft + alone)
    //   5. Sniper (authored only at v0.1; composition deferred per Q4)
    //   6. Hunter (catch-all for armed mobile ground/sea)
    //   7. Generic (nothing matched)
    //
    // Determinism: pure function, no randomness. Allocation: zero in the hot path.
    public static class SmartIdentityClassifier
    {
        public const float HunterCompositionMinTopSpeed = 5f;
        public const float BaseCompositionMaxTopSpeed = 2f;
        public const float AircraftCompositionMinVerticalAuth = 1.5f;
        public const float HoverAsAircraftMinVerticalAuth = 1.0f;

        public static SmartIdentityStamp Classify(TankAIHelper helper, VehicleModelSnapshot vehicle, int teamId)
        {
            // P11 T1 Item 47: read the live SmartIdentityTuning statics once at the dispatch
            // boundary and delegate to ClassifyWithFlags. The explicit-flags overload exists
            // so the parity catcher at SmartForm.OnTechSpawn can classify once with current
            // flags and once with all-false to detect v0.1→v0.2 reroutes.
            return ClassifyWithFlags(helper, vehicle, teamId,
                enableRepairSupport: SmartIdentityTuning.EnableRepairSupport,
                enablePatrol: SmartIdentityTuning.EnablePatrol,
                useHarvesterModulesForGatherer: SmartIdentityTuning.UseHarvesterModulesForGatherer);
        }

        /// <summary>
        /// P11 T1 Item 47: explicit-flag overload used by the IdentityClassifierParityGate
        /// dispatch site. Runs the same row evaluation as <see cref="Classify"/>, but reads
        /// the three SmartIdentityTuning flags from the parameters instead of the statics —
        /// so a parity caller can pin all flags false to get the v0.1 baseline classification
        /// without mutating static state.
        /// </summary>
        public static SmartIdentityStamp ClassifyWithFlags(TankAIHelper helper, VehicleModelSnapshot vehicle, int teamId,
            bool enableRepairSupport, bool enablePatrol, bool useHarvesterModulesForGatherer)
        {
            Vector3 spawnAnchor = Vector3.zero;
            if (helper != null && helper.tank != null)
                spawnAnchor = helper.tank.boundsCentreWorldNoCheck;

            bool authoredHintValid = helper != null && !helper.AuthoredHintInvalidated;
            HashSet<BasePurpose> purposes = authoredHintValid ? helper.AuthoredPurposes : null;
            BaseTerrain terrain = authoredHintValid ? helper.AuthoredTerrain : BaseTerrain.Any;

            // ---- Row 1: Base ----
            // Defense intent wins over Gatherer per Q5: Defense / Headquarters / TechProduction
            // in purposes ALWAYS triggers Base, regardless of NotStationary. A mobile defensive
            // harvester classifies as Base (intent overrides mobility).
            if (purposes != null && (purposes.Contains(BasePurpose.Headquarters)
                                  || purposes.Contains(BasePurpose.Defense)
                                  || purposes.Contains(BasePurpose.TechProduction)))
            {
                return new SmartIdentityStamp(SmartIdentity.Base, fromAuthored: true,
                    classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
            }
            // Composition: very slow + armed = anchored turret platform.
            if (vehicle.Mobility.TopSpeedForward < BaseCompositionMaxTopSpeed
                && vehicle.Weapons != null && vehicle.Weapons.Count > 0)
            {
                return new SmartIdentityStamp(SmartIdentity.Base, fromAuthored: false,
                    classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
            }

            // ---- Row 2: Gatherer ----
            // Autominer is Gatherer-only (per EBasePurpose.cs:36 comment: "DO NOT ATTACH THIS
            // TAG TO HQs!!!" - it's a mobile-miner flag). DediAI is intentionally NOT consulted
            // here - it's set by GUIAIManager / NetworkHandler / TankAIHelper resets, NOT by
            // EnemyMind.Setup, so authored enemy harvesters wouldn't carry the signal.
            if (purposes != null && (purposes.Contains(BasePurpose.Harvesting)
                                  || purposes.Contains(BasePurpose.HarvestingNoHQ)
                                  || purposes.Contains(BasePurpose.Autominer)))
            {
                return new SmartIdentityStamp(SmartIdentity.Gatherer, fromAuthored: true,
                    classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
            }
            // Composition: lightly-armed + mobile = drone-like collector. v0.1 best-effort
            // (real harvester-block detection deferred per Q1).
            int weaponCount = vehicle.Weapons != null ? vehicle.Weapons.Count : 0;
            if (weaponCount <= 1 && vehicle.Mobility.TopSpeedForward >= 2f
                && vehicle.Mobility.VerticalAuthority < AircraftCompositionMinVerticalAuth)
            {
                // Suppress when authored purposes are clearly non-gatherer (e.g. Sniper).
                bool authoredOverridesGatherer = purposes != null
                    && (purposes.Contains(BasePurpose.Sniper));
                if (!authoredOverridesGatherer)
                {
                    return new SmartIdentityStamp(SmartIdentity.Gatherer, fromAuthored: false,
                        classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
                }
            }

            // ---- Row 2.5 (P6 Item 25, REV 2): Gatherer-via-harvester-modules extension ----
            // Default OFF (SmartIdentityTuning.UseHarvesterModulesForGatherer = false). When ON,
            // techs with Conveyor>0 OR Holder>0 AND ≤1 weapon classify as Gatherer composition
            // EVEN IF they don't meet the speed/aerial gates above (catches cargo-block-carrying
            // techs that today fall through to Hunter). Phantom-module-free: uses already-live
            // BlockKindCounts.Conveyor/Holder (P2 verified). Sniper-purpose still suppresses.
            if (useHarvesterModulesForGatherer
                && weaponCount <= 1
                && (vehicle.KindCounts.Conveyor > 0 || vehicle.KindCounts.Holder > 0))
            {
                bool authoredOverridesHarvester = purposes != null
                    && (purposes.Contains(BasePurpose.Sniper));
                if (!authoredOverridesHarvester)
                {
                    return new SmartIdentityStamp(SmartIdentity.Gatherer, fromAuthored: false,
                        classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
                }
            }

            // ---- Aircraft team split (rows 3 / 4) ----
            // Authored aircraft via terrain. Air / Chopper / Space all route to Aircraft*;
            // the team-population check picks Support vs Hunter.
            bool authoredAircraft = terrain == BaseTerrain.Air
                                 || terrain == BaseTerrain.Chopper
                                 || terrain == BaseTerrain.Space;
            // Composition aircraft: high vertical authority. Hover-class with significant
            // vertical authority counts (catches chopper-style hovercraft that don't reach
            // the Airplane VerticalAuthority threshold).
            bool compositionAircraft = vehicle.Mobility.VerticalAuthority >= AircraftCompositionMinVerticalAuth
                || (vehicle.Mobility.VerticalAuthority >= HoverAsAircraftMinVerticalAuth
                    && vehicle.Weapons != null && vehicle.Weapons.Count >= 1);

            if (authoredAircraft || compositionAircraft)
            {
                bool teamed = helper != null && helper.tank != null && HasSameTeamAlly(helper.tank);
                if (teamed)
                {
                    return new SmartIdentityStamp(SmartIdentity.AircraftSupport,
                        fromAuthored: authoredAircraft, classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
                }
                else
                {
                    return new SmartIdentityStamp(SmartIdentity.AircraftHunter,
                        fromAuthored: authoredAircraft, classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
                }
            }

            // ---- Row 5: Sniper (authored only at v0.1 per Q4) ----
            if (purposes != null && purposes.Contains(BasePurpose.Sniper))
            {
                return new SmartIdentityStamp(SmartIdentity.Sniper, fromAuthored: true,
                    classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
            }

            // ---- Row 6 prelude (P6 Item 26/28): hoisted composition locals ----
            // The armed/mobile/aircraftLike trio is needed by Row 6 RepairSupport (Item 26)
            // and Row 6.5 Patrol (Item 28) BEFORE the Hunter row evaluates. Hoisting them
            // here doesn't change any predicate or default — Hunter's composition check at
            // Row 7 reuses these same locals verbatim.
            bool armed = vehicle.Weapons != null && vehicle.Weapons.Count >= 1;
            bool mobile = vehicle.Mobility.TopSpeedForward >= HunterCompositionMinTopSpeed;
            bool aircraftLike = vehicle.Mobility.VerticalAuthority >= AircraftCompositionMinVerticalAuth;

            // ---- Row 6: RepairSupport (P6 Item 26) ----
            // Composition: armed + mobile + ground-class + has Shield blocks.
            // Default OFF preserves v0.1 classification bit-identically (Row 7 Hunter still
            // catches what would otherwise route here).
            if (enableRepairSupport
                && armed && mobile && !aircraftLike
                && vehicle.KindCounts.Shield > 0)
            {
                return new SmartIdentityStamp(SmartIdentity.RepairSupport, fromAuthored: false,
                    classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
            }

            // ---- Row 6.5: Patrol (P6 Item 28) ----
            // Composition: armed + mobile + ground-class + solo (no same-team ally).
            // Default OFF preserves v0.1 (Row 7 Hunter still catches solo armed mobile ground).
            // Per REV 2 reshape: classifier-time check uses spawn-time signals only; the per-tick
            // threat reaction lives in PatrolGoalSource.Produce (BeliefSnapshot only exists there).
            if (enablePatrol
                && armed && mobile && !aircraftLike
                && helper != null && helper.tank != null && !HasSameTeamAlly(helper.tank))
            {
                return new SmartIdentityStamp(SmartIdentity.Patrol, fromAuthored: false,
                    classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
            }

            // ---- Row 7 (was Row 6): Hunter ----
            if (authoredHintValid && (terrain == BaseTerrain.Land || terrain == BaseTerrain.Sea))
            {
                return new SmartIdentityStamp(SmartIdentity.Hunter, fromAuthored: true,
                    classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
            }
            if (armed && mobile && !aircraftLike)
            {
                return new SmartIdentityStamp(SmartIdentity.Hunter, fromAuthored: false,
                    classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
            }

            // ---- Row 7: Generic ----
            return new SmartIdentityStamp(SmartIdentity.Generic, fromAuthored: false,
                classifiedAtTick: 0L, spawnAnchor: spawnAnchor);
        }

        // O(N) sweep of ManTechs.inst.IterateTechs() looking for a same-team peer (excluding
        // self). Called once at classify time, never repeated. The tank != self filter is
        // LOAD-BEARING (not defensive) because OnTechSpawn fires from DelayedSubscribe AFTER
        // the engine has registered the new tank, so the spawning tech IS in IterateTechs.
        public static bool HasSameTeamAlly(Tank self)
        {
            if (self == null || ManTechs.inst == null) return false;
            int team = self.Team;
            foreach (var t in ManTechs.inst.IterateTechs())
            {
                if (t == null) continue;
                if (t == self) continue;
                if (t.Team == team) return true;
            }
            return false;
        }
    }
}
