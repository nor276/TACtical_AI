using UnityEngine;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;
using TAC_AI.AI.Forms.Smart.Identity;

namespace TAC_AI.AI.Forms.Smart.Learning.Features
{
    // Aggregate weapon stats — Smart-team only. Daemon reads this via
    // SelfProbeSnapshot.WeaponAggregate. Filled by SelfStateProbe.Publish on the
    // main thread by walking the cached WeaponSpec list. Per FEATURE-EXPANSION-PLAN.md
    // §5.1 weapon aggregate. Immutable readonly struct — boxed once per Publish.
    public readonly struct WeaponAggregateSnapshot
    {
        public readonly int   WeaponCount;
        public readonly int   MeleeWeaponCount;
        public readonly int   ReadyToFireCount;
        public readonly float RangeMax;
        public readonly float RangeMean;
        public readonly float FireRateMean;
        public readonly float DamagePerShotMean;
        public readonly float MuzzleVelMean;
        public readonly float EnergyWeaponFraction;
        public readonly float KindMixGun;          // fraction of Kind == GunFixed/GunTurret
        public readonly float KindMixBeamMelee;    // fraction of Kind == Beam/Melee

        public WeaponAggregateSnapshot(
            int weaponCount, int meleeWeaponCount, int readyToFireCount,
            float rangeMax, float rangeMean, float fireRateMean,
            float damagePerShotMean, float muzzleVelMean,
            float energyWeaponFraction, float kindMixGun, float kindMixBeamMelee)
        {
            WeaponCount = weaponCount;
            MeleeWeaponCount = meleeWeaponCount;
            ReadyToFireCount = readyToFireCount;
            RangeMax = rangeMax;
            RangeMean = rangeMean;
            FireRateMean = fireRateMean;
            DamagePerShotMean = damagePerShotMean;
            MuzzleVelMean = muzzleVelMean;
            EnergyWeaponFraction = energyWeaponFraction;
            KindMixGun = kindMixGun;
            KindMixBeamMelee = kindMixBeamMelee;
        }

        public static readonly WeaponAggregateSnapshot Empty = new WeaponAggregateSnapshot(
            0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
    }

    // Energy snapshot for one EnergyType (Electric / Hydrocarbon). Mirror of the
    // engine's TechEnergy.EnergyState fields (currentAmount/spareCapacity/etc.).
    // Per FEATURE-EXPANSION-PLAN.md §5.1 — there is NO `Stored` or `NetFlow` field
    // on engine; net flow is derived at consume time as currentAmount-previousAmount.
    public readonly struct EnergyChannelSnapshot
    {
        public readonly float CurrentAmount;
        public readonly float SpareCapacity;
        public readonly float StorageTotal;
        public readonly float PreviousAmount;
        public readonly float PreviousSpareCapacity;

        public EnergyChannelSnapshot(float current, float spare, float storageTotal,
            float previousAmount, float previousSpareCapacity)
        {
            CurrentAmount = current;
            SpareCapacity = spare;
            StorageTotal = storageTotal;
            PreviousAmount = previousAmount;
            PreviousSpareCapacity = previousSpareCapacity;
        }

        public static readonly EnergyChannelSnapshot Empty =
            new EnergyChannelSnapshot(0f, 0f, 0f, 0f, 0f);
    }

    // AnchorState code — published as a byte so the daemon never touches Tank or
    // TechAnchors (MonoBehaviour-only). 0 = floating, 1 = anchored, 2 = sky-anchored.
    public enum AnchorStateCode : byte
    {
        Floating    = 0,
        Anchored    = 1,
        SkyAnchored = 2,
    }

    // Immutable per-tech snapshot published main-thread by SelfStateProbe.Publish,
    // read background-thread by StrategicStateExtractor. The channel through which
    // SELF data crosses the Unity boundary: every field reads a Unity component
    // ONCE on the main thread, stores into a readonly field, then the snapshot is
    // atomically swapped via DoubleBuffer.
    //
    // CAVEAT (mirrors StrategicStateVector §3.1): the snapshot's class-level
    // readonly fields block reference reassignment but the underlying ArmorMap
    // float[] is mutable. ArmorMap is chassis-immutable BY DISCIPLINE — rebuilt on
    // Attach/Detach and never mutated thereafter. Background reads are safe.
    public sealed class SelfProbeSnapshot
    {
        // Identity + generation.
        public readonly TechId Subject;
        public readonly TeamId SubjectTeam;
        public readonly long   PublishedTickMono;
        public readonly int    Generation;          // monotonic; lifecycle reset trips

        // Kinematics (main-thread reads of Tank/rbody/Transform). Stored in WORLD
        // frame so the daemon never touches a Transform.
        public readonly Vector3 PositionWorld;
        public readonly Vector3 VelocityWorld;
        public readonly Vector3 AngularVelocityWorld;
        public readonly Vector3 ForwardWorld;       // tank.rootBlockTrans.forward, world-frame
        public readonly Vector3 WeakFaceNormalWorld; // ArmorMap.QueryWeakFace.FaceNormalLocal rotated by rootBlockTrans.rotation
        public readonly float   Mass;
        public readonly float   Speed;              // VelocityWorld.magnitude, pre-baked

        // Composition. HpFraction proxy via block_count for now per HealthSidecar
        // baseline at HealthSidecar.cs:53 — exposed for the daemon to query the
        // HealthSidecar (any-thread on ConcurrentDictionary). Cached BlockCount is
        // refreshed on AttachEvent / DetachEvent invalidation.
        public readonly int     BlockCount;
        public readonly float   HpFraction;         // 0..1, from HealthSidecar.Get
        public readonly byte    AnchorState;        // AnchorStateCode byte; 0 if Anchors null

        // Energy — both channels. previousAmount fields enable net-flow derivation
        // at the daemon (currentAmount - previousAmount) without touching TechEnergy
        // on the bg thread. Per F-01 disposition (§3.3, §3.5).
        public readonly EnergyChannelSnapshot Electric;
        public readonly EnergyChannelSnapshot Hydrocarbon;
        public readonly int     GeneratingModuleCount;
        public readonly int     EnergyModuleCount;

        // Weapon aggregate from cached WeaponSpec list. Forward-arc DPS, max range,
        // fire-ready count etc. all reduce in one pass on the main thread.
        public readonly WeaponAggregateSnapshot WeaponAggregate;

        // Provoked countdown — TankAIHelper.Provoked is `int` not bool (see plan
        // F-24); daemon compares > 0 for the ProvokedFlag feature.
        public readonly int     ProvokedCountdown;

        // LastAttacker channel. Edge-stamped: PosWorld captured from
        // TechAI.LastAttacker.tank.boundsCentreWorldNoCheck at the SAME tick
        // LastAttackerSeenMono trips on rising-edge. Per R2-09: NOT a cast of
        // TechAI.LastAttackedTime (float Time.time) — the publisher stamps
        // MonoClock.Now() on the rising-edge transition.
        public readonly Vector3 LastAttackerPosWorld;
        public readonly long    LastAttackerSeenMono;
        public readonly bool    HasLastAttacker;

        // World-environment scalars — touched main-thread only. Daemon must NOT
        // dereference ManTimeOfDay.inst (Singleton) or KickStart.WaterHeight
        // (WaterMod.QPatch.WaterHeight).
        public readonly float   TimeOfDayHour;      // [0,24)
        public readonly float   WaterHeight;        // raw KickStart.WaterHeight (may be -9001 when no WaterMod)

        // Identity classification — set by SmartIdentityClassifier on spawn.
        // Daemon reads the byte enum; the SmartIdentityStamp struct itself stays
        // on SmartPerTechState for heavier consumers.
        public readonly SmartIdentity SmartIdentity;

        // Armor map — pass-through reference. ArmorMap is constructed on
        // Attach/Detach and never mutated; background daemon can call
        // armor.QueryWeakFace(localDir) any-thread (per chassis-immutability).
        public readonly ArmorMap Armor;

        // Cargo state — published per-tick from main thread.
        public readonly int     CargoNumContents;
        public readonly int     CargoCapacity;

        public SelfProbeSnapshot(
            TechId subject, TeamId subjectTeam, long publishedTickMono, int generation,
            Vector3 positionWorld, Vector3 velocityWorld, Vector3 angularVelocityWorld,
            Vector3 forwardWorld, Vector3 weakFaceNormalWorld,
            float mass, float speed,
            int blockCount, float hpFraction, byte anchorState,
            EnergyChannelSnapshot electric, EnergyChannelSnapshot hydrocarbon,
            int generatingModuleCount, int energyModuleCount,
            WeaponAggregateSnapshot weaponAggregate,
            int provokedCountdown,
            Vector3 lastAttackerPosWorld, long lastAttackerSeenMono, bool hasLastAttacker,
            float timeOfDayHour, float waterHeight,
            SmartIdentity smartIdentity,
            ArmorMap armor,
            int cargoNumContents, int cargoCapacity)
        {
            Subject = subject;
            SubjectTeam = subjectTeam;
            PublishedTickMono = publishedTickMono;
            Generation = generation;
            PositionWorld = positionWorld;
            VelocityWorld = velocityWorld;
            AngularVelocityWorld = angularVelocityWorld;
            ForwardWorld = forwardWorld;
            WeakFaceNormalWorld = weakFaceNormalWorld;
            Mass = mass;
            Speed = speed;
            BlockCount = blockCount;
            HpFraction = hpFraction;
            AnchorState = anchorState;
            Electric = electric;
            Hydrocarbon = hydrocarbon;
            GeneratingModuleCount = generatingModuleCount;
            EnergyModuleCount = energyModuleCount;
            WeaponAggregate = weaponAggregate;
            ProvokedCountdown = provokedCountdown;
            LastAttackerPosWorld = lastAttackerPosWorld;
            LastAttackerSeenMono = lastAttackerSeenMono;
            HasLastAttacker = hasLastAttacker;
            TimeOfDayHour = timeOfDayHour;
            WaterHeight = waterHeight;
            SmartIdentity = smartIdentity;
            Armor = armor;
            CargoNumContents = cargoNumContents;
            CargoCapacity = cargoCapacity;
        }
    }

    // Per-tech main-thread publisher. One instance per SmartPerTechState; ticked
    // from SmartForm.Operations (gated to the existing 33 ms ObserveWorldTechsIfDue
    // throttle so the effective publish rate is ~30 Hz matching the daemon).
    //
    // Threading: every Publish() call runs on the Unity main thread. The snapshot
    // is sealed-immutable on construction and atomically swapped through the
    // per-tech DoubleBuffer<SelfProbeSnapshot>. Background daemon (extractor)
    // reads via TryRead() on any thread.
    //
    // Edge-stamp discipline for LastAttacker: a `_hadAttacker` latch tracks the
    // engine TechAI.LastAttacker non-null transition. The publisher stamps
    // MonoClock.Now() on the rising edge — NEVER casts the engine's
    // TechAI.LastAttackedTime (which is float Time.time-derived, not MonoClock-
    // domain). Per F-04 + R2-09 dispositions.
    public sealed class SelfStateProbe
    {
        private readonly DoubleBuffer<SelfProbeSnapshot> _buffer =
            new DoubleBuffer<SelfProbeSnapshot>(null);

        // Latch state for edge-stamping LastAttacker. Reads happen only on the
        // main thread inside Publish, so no synchronization needed beyond the
        // DoubleBuffer.Write.
        private bool _hadAttacker;
        private long _lastAttackerSeenMono;
        private Vector3 _lastAttackerPosWorld;

        // Cached weapon aggregate — invalidated on AttachEvent/DetachEvent by
        // SmartPerTechState's invalidation hook; rebuilt lazily on next Publish
        // via Refresh. Mass and BlockCount likewise cached.
        // Held externally — daemon and probe both see the same aggregate.
        public WeaponAggregateSnapshot CachedWeaponAggregate { get; private set; }
        public int CachedBlockCount { get; private set; }
        public int CachedEnergyModuleCount { get; private set; }
        public int CachedGeneratingModuleCount { get; private set; }

        // Identity stamp — cached from SmartIdentityClassifier at spawn; the
        // SmartPerTechState owns the SmartIdentityStamp, the probe carries only
        // the SmartIdentity byte enum.
        public SmartIdentity Identity { get; set; }

        // Bumped on lifecycle reset (recycle / deregister). Carried into every
        // published SelfProbeSnapshot so the StrategicStateVector inherits the
        // current Generation per §6.5 lifecycle-race mitigation.
        private int _generation;

        public SelfStateProbe()
        {
            CachedWeaponAggregate = WeaponAggregateSnapshot.Empty;
            CachedBlockCount = 0;
            CachedEnergyModuleCount = 0;
            CachedGeneratingModuleCount = 0;
            Identity = SmartIdentity.Generic;
            _generation = 0;
        }

        // Background-side read. Returns null until the first Publish.
        public SelfProbeSnapshot TryRead()
        {
            return _buffer.Read();
        }

        // Called from SmartPerTechState invalidation hook on AttachEvent /
        // DetachEvent. Caller computes the new aggregate from cached
        // WeaponProfile + iterates ModuleEnergy count on the main thread (the
        // expensive walk lives at the call site to keep the probe Unity-component-
        // touch confined to Publish).
        public void StampCachedAggregates(
            WeaponAggregateSnapshot weaponAgg,
            int blockCount,
            int energyModuleCount,
            int generatingModuleCount)
        {
            CachedWeaponAggregate = weaponAgg;
            CachedBlockCount = blockCount;
            CachedEnergyModuleCount = energyModuleCount;
            CachedGeneratingModuleCount = generatingModuleCount;
        }

        // Called on lifecycle reset (OnTechRecycle / DeregisterTech). Increments
        // Generation so any pending vector read on the bg side will see the
        // stale-generation discard signal.
        public void OnLifecycleReset()
        {
            _generation++;
            _hadAttacker = false;
            _lastAttackerSeenMono = 0L;
            _lastAttackerPosWorld = Vector3.zero;
            _buffer.Write(null);
        }

        // Main-thread entry. Reads ALL live Unity state, builds a fresh immutable
        // snapshot, atomically swaps through the DoubleBuffer. Caller (SmartForm
        // gated tick) is responsible for the 33 ms throttle.
        public void Publish(
            TechId subject, TeamId subjectTeam,
            Tank tank, TankAIHelper helper,
            ArmorMap armor, float hpFraction,
            int cargoNumContents, int cargoCapacity)
        {
            if (tank == null) return;

            long nowMono = MonoClock.Now();

            // -- Kinematics. boundsCentreWorldNoCheck is cheap and safe on attached techs. --
            Vector3 posWorld = tank.boundsCentreWorldNoCheck;
            Vector3 velWorld = Vector3.zero;
            Vector3 angVelWorld = Vector3.zero;
            float   mass = 0f;
            if (tank.rbody != null)
            {
                velWorld = tank.rbody.velocity;
                angVelWorld = tank.rbody.angularVelocity;
                mass = tank.rbody.mass;
            }
            float speed = velWorld.magnitude;

            // -- Forward + weak-face normal in world frame. Daemon must NOT touch --
            // -- rootBlockTrans; pre-rotate the local face normal here.            --
            Vector3 forwardWorld = Vector3.forward;
            Quaternion rotation = Quaternion.identity;
            if (tank.rootBlockTrans != null)
            {
                forwardWorld = tank.rootBlockTrans.forward;
                rotation = tank.rootBlockTrans.rotation;
            }
            Vector3 weakFaceWorld = Vector3.up;
            if (armor != null && armor != ArmorMap.Empty)
            {
                // Query against a generic frontal-attack direction in tech-local
                // frame. The daemon may re-query the ArmorMap on the actual
                // attacker direction; this baseline is what the AV[28]
                // SelfWeakFaceForwardDot feature consumes.
                ArmorQueryResult q = armor.QueryWeakFace(Vector3.forward);
                weakFaceWorld = rotation * q.FaceNormalLocal;
            }

            // -- Anchor state byte. Per round-1 SelfStateProbe null-guard: 0 if --
            // -- Anchors null (un-initialized component on a freshly spawned tech).--
            byte anchorState = (byte)AnchorStateCode.Floating;
            if (tank.Anchors != null)
            {
                if (tank.IsSkyAnchored) anchorState = (byte)AnchorStateCode.SkyAnchored;
                else if (tank.IsAnchored) anchorState = (byte)AnchorStateCode.Anchored;
            }

            // -- Energy channels. EnergyRegulator may be null on a tech without --
            // -- any energy modules; default to Empty in that case.              --
            EnergyChannelSnapshot electric = EnergyChannelSnapshot.Empty;
            EnergyChannelSnapshot hydrocarbon = EnergyChannelSnapshot.Empty;
            try
            {
                if (tank.EnergyRegulator != null)
                {
                    var eState = tank.EnergyRegulator.Energy(TechEnergy.EnergyType.Electric);
                    if (eState != null)
                    {
                        electric = new EnergyChannelSnapshot(
                            eState.currentAmount,
                            eState.spareCapacity,
                            eState.storageTotal,
                            eState.previousAmount,
                            eState.previousSpareCapacity);
                    }
                    var hState = tank.EnergyRegulator.Energy(TechEnergy.EnergyType.Hydrocarbon);
                    if (hState != null)
                    {
                        hydrocarbon = new EnergyChannelSnapshot(
                            hState.currentAmount,
                            hState.spareCapacity,
                            hState.storageTotal,
                            hState.previousAmount,
                            hState.previousSpareCapacity);
                    }
                }
            }
            catch
            {
                // Pre-init race or torn EnergyRegulator state. Default-empty is correct.
            }

            // -- LastAttacker rising-edge stamp. TechAI.LastAttacker is a Visible --
            // -- (engine MonoBehaviour) so all reads are main-thread.              --
            Vector3 attackerPos = _lastAttackerPosWorld;
            long    attackerSeen = _lastAttackerSeenMono;
            bool    hasAttacker = _hadAttacker;
            try
            {
                Visible last = null;
                if (helper != null && helper.lastEnemyGet != null)
                {
                    last = helper.lastEnemyGet;
                }
                else if (tank.GetComponent<TechAI>() != null)
                {
                    last = tank.GetComponent<TechAI>().LastAttacker;
                }
                if (last != null && last.tank != null)
                {
                    if (!_hadAttacker)
                    {
                        _hadAttacker = true;
                        _lastAttackerSeenMono = nowMono;
                    }
                    _lastAttackerPosWorld = last.tank.boundsCentreWorldNoCheck;
                    attackerPos = _lastAttackerPosWorld;
                    attackerSeen = _lastAttackerSeenMono;
                    hasAttacker = true;
                }
            }
            catch
            {
                // TechAI may not be attached yet on a freshly spawned tech.
            }

            // -- World-environment reads. ManTimeOfDay.inst + KickStart.WaterHeight --
            // -- are Unity-touching; daemon may NEVER call these.                    --
            float timeOfDayHour = 0f;
            try
            {
                if (ManTimeOfDay.inst != null) timeOfDayHour = ManTimeOfDay.inst.TimeOfDayPrecise;
            }
            catch { }
            float waterHeight;
            try { waterHeight = KickStart.WaterHeight; }
            catch { waterHeight = -9001f; }

            // -- Provoked countdown. TankAIHelper.Provoked is an `int` (countdown).--
            int provoked = 0;
            if (helper != null) provoked = helper.Provoked;

            // -- Build and publish. --
            var snap = new SelfProbeSnapshot(
                subject: subject,
                subjectTeam: subjectTeam,
                publishedTickMono: nowMono,
                generation: _generation,
                positionWorld: posWorld,
                velocityWorld: velWorld,
                angularVelocityWorld: angVelWorld,
                forwardWorld: forwardWorld,
                weakFaceNormalWorld: weakFaceWorld,
                mass: mass,
                speed: speed,
                blockCount: CachedBlockCount,
                hpFraction: hpFraction,
                anchorState: anchorState,
                electric: electric,
                hydrocarbon: hydrocarbon,
                generatingModuleCount: CachedGeneratingModuleCount,
                energyModuleCount: CachedEnergyModuleCount,
                weaponAggregate: CachedWeaponAggregate,
                provokedCountdown: provoked,
                lastAttackerPosWorld: attackerPos,
                lastAttackerSeenMono: attackerSeen,
                hasLastAttacker: hasAttacker,
                timeOfDayHour: timeOfDayHour,
                waterHeight: waterHeight,
                smartIdentity: Identity,
                armor: armor,
                cargoNumContents: cargoNumContents,
                cargoCapacity: cargoCapacity);

            _buffer.Write(snap);
        }
    }
}
