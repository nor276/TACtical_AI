using System;
using System.Collections.Generic;
using UnityEngine;
using TerraTechETCUtil;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: reads engine module fields on a TankBlock (prefab or instance) to build an
    /// immutable <see cref="BlockArchetype"/>. The only place in Chassis that does engine
    /// reflection / module-traversal.
    ///
    /// Per CHASSIS-DESIGN.md probe spec:
    ///   - ModuleBooster -> walks FanJet children (RawTechBase.thrustRate +
    ///     fanThrustRateRev + LocalThrustDirection) and BoosterJet children
    ///     (AIGlobals.BoostForceField i.e. Thruster.m_Force + LocalThrustDirection).
    ///   - ModuleHover / ModuleWing -> 2g placeholder per Decision-#1/S1/S7
    ///     (MaxForceN = block.CurrentMass * 9.81 * 2.0).
    ///   - ModuleWheels -> one WheelRoll emitter per wheel; LocalAxis irrelevant
    ///     (per-EmitterKind substitution in ThrustField.Compute).
    ///   - ModuleWeapon -> WeaponSpec.PlaceholderFixed (Decision #8); Kind = GunFixed (S6).
    ///   - Anchor / Conveyor / Holder / Producer / Shield / EnergyStore / Generator /
    ///     GyroStabilizer -> bit-detection via GetComponent != null.
    ///   - Walker / Drill / Repair -> reserved-unreachable in v0.1 per Decisions #7/#12.
    ///
    /// Reflection failures are caught + logged once (file-only [CHASSIS] tag per
    /// ai-warning-routing-preference memory); the failed channel records 0 force and the
    /// archetype is cached degraded (Failure mode #2 -- never worse than today's behavior).
    /// </summary>
    public static class ArchetypeProbe
    {
        // One-time-init reflection handles. AIGlobals.BoostForceField already caches Thruster.m_Force.
        // RawTechBase.thrustRate / fanThrustRateRev / spinDat are static FieldInfo fields on
        // RawTechBase itself -- accessed live via static field reads (no GetField calls needed).
        // spinDat is deferred to v0.2 per Decision-#8 / S8.

        private static readonly object _failureLogLock = new object();
        private static readonly HashSet<string> _loggedFailures = new HashSet<string>();

        private static void LogOnce(string key, string message)
        {
            lock (_failureLogLock)
            {
                if (!_loggedFailures.Add(key)) return;
            }
            DebugTAC_AI.LogWarnFileOnly("[CHASSIS]:" + key, message);
        }

        public static BlockArchetype Probe(TankBlock block, int typeKey)
        {
            if (block == null) return BlockArchetype.Unknown;

            try
            {
                var emitters = new List<ThrustEmitter>(4);
                var kinds = BlockKindFlags.None;
                int cellCount = 1;
                Bounds localBounds = new Bounds(Vector3.zero, Vector3.one);
                float specMass = 1f;

                try { specMass = block.CurrentMass; } catch { /* fall through to default */ }
                if (specMass <= 0f) specMass = 1f;

                try
                {
                    if (block.filledCells != null && block.filledCells.Length > 0)
                        cellCount = block.filledCells.Length;
                }
                catch { /* default 1 */ }

                // Booster (FanJet + BoosterJet children).
                var booster = block.GetComponent<ModuleBooster>();
                if (booster != null)
                {
                    kinds |= BlockKindFlags.Booster;
                    ProbeBoosterEmitters(block, booster, emitters);
                }

                // Hover (2g placeholder).
                var hover = block.GetComponent<ModuleHover>();
                if (hover != null)
                {
                    kinds |= BlockKindFlags.Hover;
                    emitters.Add(new ThrustEmitter(
                        kind: EmitterKind.HoverPad,
                        localAxis: Vector3.up,
                        localMount: Vector3.zero,
                        maxForceN: specMass * 9.81f * 2.0f,    // 2g placeholder per S1/S7
                        reverseForceN: 0f,
                        flags: EmitterFlags.None));
                }

                // Wing (2g placeholder, aero-scaled).
                var wing = block.GetComponent<ModuleWing>();
                if (wing != null)
                {
                    kinds |= BlockKindFlags.Wing;
                    emitters.Add(new ThrustEmitter(
                        kind: EmitterKind.WingLift,
                        localAxis: Vector3.up,
                        localMount: Vector3.zero,
                        maxForceN: specMass * 9.81f * 2.0f,
                        reverseForceN: 0f,
                        flags: EmitterFlags.AerodynamicScaling));
                }

                // Wheels.
                var wheels = block.GetComponent<ModuleWheels>();
                if (wheels != null)
                {
                    kinds |= BlockKindFlags.Wheel;
                    // One WheelRoll emitter per ModuleWheels block. LocalAxis irrelevant due
                    // to substitution in ThrustField.Compute (chassis +Z per Decision #3).
                    // v0.1 uses a single emitter regardless of internal wheel count -- the
                    // wheel substitution + 30%-of-forward yaw heuristic in ThrustField are
                    // count-agnostic; per-wheel counts can be wired in v0.2 if needed.
                    emitters.Add(new ThrustEmitter(
                        kind: EmitterKind.WheelRoll,
                        localAxis: Vector3.forward,    // ignored by substitution
                        localMount: Vector3.zero,
                        maxForceN: specMass * 9.81f * 2.0f,   // 2g placeholder traction -- matches today's ThrustMap.cs:63 for wheel techs (preserves wheel-tank feel)
                        reverseForceN: specMass * 9.81f * 2.0f,
                        flags: EmitterFlags.Bidirectional));
                }

                // Weapon (v0.1 placeholder per Decision #8; Kind = GunFixed per S6).
                bool hasWeapon = false;
                WeaponSpec weapon = default(WeaponSpec);
                var weaponModule = block.GetComponent<ModuleWeapon>();
                if (weaponModule != null)
                {
                    hasWeapon = true;
                    kinds |= BlockKindFlags.WeaponFixed;   // default fall-through per S6
                    // BarrelDirBlockLocal: per Decision #2, populated but unread by v0.1.
                    // Probe via block.transform.InverseTransformDirection(weapon.transform.forward).
                    Vector3 barrelDir;
                    try
                    {
                        barrelDir = block.transform.InverseTransformDirection(weaponModule.transform.forward);
                    }
                    catch { barrelDir = Vector3.forward; }
                    weapon = WeaponSpec.PlaceholderFixed(barrelDir);
                }

                // Bit-only modules.
                if (block.GetComponent<ModuleAnchor>() != null)        kinds |= BlockKindFlags.Anchor;
                if (block.GetComponent<ModuleItemConveyor>() != null)  kinds |= BlockKindFlags.Conveyor;
                if (block.GetComponent<ModuleItemHolder>() != null)    kinds |= BlockKindFlags.Holder;
                if (block.GetComponent<ModuleItemProducer>() != null)  kinds |= BlockKindFlags.Producer;
                if (block.GetComponent<ModuleShieldGenerator>() != null) kinds |= BlockKindFlags.Shield;
                if (block.GetComponent<ModuleEnergyStore>() != null)   kinds |= BlockKindFlags.EnergyStore;
                if (block.GetComponent<ModuleEnergy>() != null)        kinds |= BlockKindFlags.Generator;
                // GyroStabilizer: no verified probe target in v0.1; reserved-unreachable.

                // If no propulsion / weapon / function modules at all, mark Structural.
                if (kinds == BlockKindFlags.None) kinds = BlockKindFlags.Structural;

                return new BlockArchetype(
                    typeKey: typeKey,
                    kinds: kinds,
                    specMass: specMass,
                    cellCount: cellCount,
                    emitters: emitters.ToArray(),
                    hasWeapon: hasWeapon,
                    weapon: weapon,
                    localBounds: localBounds);
            }
            catch (Exception ex)
            {
                LogOnce("probe-fatal:" + typeKey,
                    "ArchetypeProbe.Probe(" + typeKey + ") threw " + ex.GetType().Name + ": " + ex.Message
                    + " -- returning Unknown sentinel (will retry next sighting).");
                return BlockArchetype.Unknown;
            }
        }

        private static void ProbeBoosterEmitters(TankBlock block, ModuleBooster booster, List<ThrustEmitter> emitters)
        {
            // FanJet children -- RawTechBase.thrustRate + fanThrustRateRev, LocalThrustDirection.
            try
            {
                var fanJets = booster.transform.GetComponentsInChildren<FanJet>(true);
                if (fanJets != null)
                {
                    for (int i = 0; i < fanJets.Length; i++)
                    {
                        var jet = fanJets[i];
                        if (jet == null) continue;
                        float fwd = 0f, rev = 0f;
                        try { fwd = (float)RawTechBase.thrustRate.GetValue(jet); }
                        catch (Exception ex)
                        {
                            LogOnce("fanjet-thrustRate",
                                "FanJet thrustRate reflection failed: " + ex.Message
                                + " -- using 0 force for fan-jet emitters.");
                        }
                        try { rev = (float)RawTechBase.fanThrustRateRev.GetValue(jet); }
                        catch { /* leave 0; rev not load-bearing for many fan-jet techs */ }

                        Vector3 localAxis;
                        try
                        {
                            // jet.LocalThrustDirection is jet-local; wrap to block-local
                            // per the production aggregator pattern verified at
                            // AIControllerDefault.cs:257/263 + AIECore.cs:602.
                            localAxis = block.transform.InverseTransformDirection(
                                jet.transform.TransformDirection(jet.LocalThrustDirection));
                        }
                        catch { localAxis = Vector3.forward; }

                        var flags = rev > 0f ? EmitterFlags.Bidirectional : EmitterFlags.None;
                        emitters.Add(new ThrustEmitter(
                            kind: EmitterKind.FanJet,
                            localAxis: localAxis,
                            localMount: Vector3.zero,
                            maxForceN: fwd,
                            reverseForceN: rev,
                            flags: flags));
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("fanjet-walk",
                    "FanJet child walk threw " + ex.GetType().Name + ": " + ex.Message
                    + " -- skipping fan-jet emitters for this archetype.");
            }

            // BoosterJet children -- AIGlobals.BoostForceField (Thruster.m_Force) + LocalThrustDirection.
            try
            {
                var boosterJets = booster.transform.GetComponentsInChildren<BoosterJet>(true);
                if (boosterJets != null)
                {
                    for (int i = 0; i < boosterJets.Length; i++)
                    {
                        var boost = boosterJets[i];
                        if (boost == null) continue;
                        float force = 0f;
                        try { force = (float)AIGlobals.BoostForceField.GetValue(boost); }
                        catch (Exception ex)
                        {
                            LogOnce("boost-force",
                                "BoosterJet Thruster.m_Force reflection failed: " + ex.Message
                                + " -- using 0 force for booster emitters.");
                        }

                        Vector3 localAxis;
                        try
                        {
                            // boost.LocalThrustDirection is jet-local; wrap to block-local
                            // per the production aggregator pattern verified at
                            // AIControllerDefault.cs:279/285 + AIECore.cs:615 + RCore.cs:295.
                            // Sign convention from AIControllerAir.cs:177: aggregators negate
                            // for bias-direction subtraction; we store the positive push
                            // direction on the archetype, sign convention left to aggregator.
                            localAxis = block.transform.InverseTransformDirection(
                                boost.transform.TransformDirection(boost.LocalThrustDirection));
                        }
                        catch { localAxis = Vector3.forward; }

                        var flags = EmitterFlags.None;
                        try { if (boost.ConsumesFuel) flags |= EmitterFlags.ConsumesFuel; }
                        catch { /* leave flag clear */ }

                        emitters.Add(new ThrustEmitter(
                            kind: EmitterKind.BoosterImpulse,
                            localAxis: localAxis,
                            localMount: Vector3.zero,
                            maxForceN: force,
                            reverseForceN: 0f,
                            flags: flags));
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("boost-walk",
                    "BoosterJet child walk threw " + ex.GetType().Name + ": " + ex.Message
                    + " -- skipping booster emitters for this archetype.");
            }
        }
    }
}
