using System;
using System.Collections.Generic;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.World
{
    // ============================================================
    // Sanitized event payload types — no engine object references.
    // ============================================================

    /// <summary>Sanitized identifier for a multiplayer player. Wraps NetPlayer.netId-like int.</summary>
    public readonly struct PlayerId : IEquatable<PlayerId>
    {
        public readonly int Value;
        public PlayerId(int value) { Value = value; }
        public bool Equals(PlayerId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PlayerId p && Equals(p);
        public override int GetHashCode() => Value;
        public override string ToString() => "Player#" + Value;
    }

    /// <summary>Sanitized weapon identifier; uniquely identifies one weapon block on one tech.</summary>
    public readonly struct WeaponId : IEquatable<WeaponId>
    {
        public readonly int Value;
        public WeaponId(int value) { Value = value; }
        public bool Equals(WeaponId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is WeaponId w && Equals(w);
        public override int GetHashCode() => Value;
        public override string ToString() => "Weapon#" + Value;
    }

    /// <summary>Sanitized damage info that workers may consume. No engine refs.</summary>
    public readonly struct SanitizedDamageInfo
    {
        public readonly float Damage;
        public readonly Vector3 ImpactPositionWorld;
        public readonly Vector3 ImpactDirectionWorld;
        public readonly TechId AttackerIfKnown;
        public readonly bool HasAttacker;
        // FEATURE-EXPANSION-PLAN §8.5 R2-01: engine ManDamage.DamageInfo.DamageType byte
        // (ManDamage.cs:58 — NOT the per-block Damageable.DamageableType). Sourced at
        // SmartEventBridge.OnTankDamage; consumed by DamageHintBuffer.Push + the §3.5
        // Threat-vector dominant-type slot.
        public readonly byte DamageType;

        // Legacy 5-arg ctor preserved for backward compatibility with anywhere that
        // didn't have a DamageType byte handy (zero defaults to ManDamage.DamageType=0,
        // which the engine's enum treats as "Standard / generic").
        public SanitizedDamageInfo(float damage, Vector3 pos, Vector3 dir, TechId attackerIfKnown, bool hasAttacker)
            : this(damage, pos, dir, attackerIfKnown, hasAttacker, damageType: 0) { }

        public SanitizedDamageInfo(float damage, Vector3 pos, Vector3 dir, TechId attackerIfKnown, bool hasAttacker, byte damageType)
        {
            Damage = damage;
            ImpactPositionWorld = pos;
            ImpactDirectionWorld = dir;
            AttackerIfKnown = attackerIfKnown;
            HasAttacker = hasAttacker;
            DamageType = damageType;
        }
    }

    // ============================================================
    // Event catalog per WORLD-CONTRACT §5.2.
    // All events are structs (value-typed) to avoid GC pressure under heavy combat.
    // ============================================================

    public readonly struct TechSpawned { public readonly TechId Id; public readonly TeamId Team; public readonly Vector3 Position; public TechSpawned(TechId id, TeamId team, Vector3 pos) { Id = id; Team = team; Position = pos; } }
    public readonly struct TechDespawned { public readonly TechId Id; public TechDespawned(TechId id) { Id = id; } }
    public readonly struct TechTeamChanged { public readonly TechId Id; public readonly TeamId OldTeam, NewTeam; public TechTeamChanged(TechId id, TeamId oldT, TeamId newT) { Id = id; OldTeam = oldT; NewTeam = newT; } }
    public readonly struct TechSeen { public readonly TechId Id; public readonly Vector3 Position; public readonly Vector3 Velocity; public readonly float Heading; public TechSeen(TechId id, Vector3 p, Vector3 v, float h) { Id = id; Position = p; Velocity = v; Heading = h; } }
    // P4 Item 14 (REV 7): TechLost producer REACTIVATED. AetherFuser fires this on the
    // Coasting/Stale → Lost edge (per AetherFuser.cs:100-104 insertion). Obsolete attribute
    // removed; consumers can subscribe normally.
    public readonly struct TechLost { public readonly TechId Id; public readonly Vector3 LastKnownPosition; public TechLost(TechId id, Vector3 pos) { Id = id; LastKnownPosition = pos; } }
    public readonly struct DamageObserved { public readonly TechId Id; public readonly SanitizedDamageInfo Damage; public DamageObserved(TechId id, SanitizedDamageInfo info) { Id = id; Damage = info; } }
    // No block-attach/detach producer in v0.1.0 — VehicleModel rebuild
    // observes the whole block list, not transitions. v0.2: bridge from
    // Tank.blockman events when block-level granularity is needed.
    [System.Obsolete("v0.1.0: no producer. Vehicle snapshots rebuild whole-list. Re-enable in v0.2.", error: false)]
    public readonly struct BlockAttached { public readonly TechId Id; public readonly int BlockTypeHash; public readonly Vector3 LocalPosition; public BlockAttached(TechId id, int typeHash, Vector3 pos) { Id = id; BlockTypeHash = typeHash; LocalPosition = pos; } }
    [System.Obsolete("v0.1.0: no producer. Vehicle snapshots rebuild whole-list. Re-enable in v0.2.", error: false)]
    public readonly struct BlockDetached { public readonly TechId Id; public readonly int BlockTypeHash; public readonly Vector3 LocalPosition; public BlockDetached(TechId id, int typeHash, Vector3 pos) { Id = id; BlockTypeHash = typeHash; LocalPosition = pos; } }
    public readonly struct ProjectileFired { public readonly TechId Id; public readonly WeaponId Weapon; public readonly Vector3 FireOrigin, FireDirection; public ProjectileFired(TechId id, WeaponId w, Vector3 o, Vector3 d) { Id = id; Weapon = w; FireOrigin = o; FireDirection = d; } }
    // No player-join/leave producer in v0.1.0 — MP roster tracking is unused
    // by current Smart logic (player identity feeds Learning's profile path, but
    // resolution happens once at world load). v0.2 wires ManNetwork events.
    [System.Obsolete("v0.1.0: no producer. v0.2: bridge ManNetwork player events.", error: false)]
    public readonly struct PlayerJoined { public readonly PlayerId Player; public PlayerJoined(PlayerId p) { Player = p; } }
    [System.Obsolete("v0.1.0: no producer. v0.2: bridge ManNetwork player events.", error: false)]
    public readonly struct PlayerLeft { public readonly PlayerId Player; public PlayerLeft(PlayerId p) { Player = p; } }
    [System.Obsolete("v0.1.0: no producer. v0.2: bridge ManGameMode start signals.", error: false)]
    public readonly struct WorldStarted { }
    [System.Obsolete("v0.1.0: no producer. v0.2: bridge ManGameMode mode-switch signals.", error: false)]
    public readonly struct WorldModeSwitched { public readonly int NewModeHash; public WorldModeSwitched(int hash) { NewModeHash = hash; } }
    public readonly struct WorldSaving { }
    public readonly struct WorldSaved { }
    public readonly struct WorldLoading { }
    public readonly struct WorldLoaded { }
    public readonly struct BeliefUpdated { public readonly TechId Id; public BeliefUpdated(TechId id) { Id = id; } }

    /// <summary>
    /// L-012: published by HostAuthorityCoordinator (L-030) when SmartRuntime.IsHost flips.
    /// Subscribed by LearningService (L-070, OnHostLost/OnHostGained) for checkpoint flush
    /// and trainer pause/resume. PhaseSource tags which Unity hook detected the change so
    /// the [HOSTAUTH-CHANGE] log can distinguish first-tick vs main-thread vs IsHost-poll.
    /// </summary>
    public enum HostAuthorityPhase : byte
    {
        Unknown = 0,
        Initial = 1,       // first detection after Init
        IsHostPoll = 2,    // detected by HostAuthorityCoordinator's main-thread poll
        Forced = 3,        // SMART_DEV test forced a transition
    }

    /// <summary>L-036: published by SmartForm.OnWorldReset before any reset hook runs.</summary>
    public readonly struct WorldResetting { public readonly long TickMono; public WorldResetting(long t) { TickMono = t; } }
    /// <summary>L-036: published after all reset hooks complete (success OR partial).</summary>
    public readonly struct WorldResetCompleted
    {
        public readonly long TickMono;
        public readonly int HookCountOk;
        public readonly int HookCountTotal;
        public WorldResetCompleted(long t, int ok, int total) { TickMono = t; HookCountOk = ok; HookCountTotal = total; }
    }

    public readonly struct HostChanged
    {
        public readonly bool WasHost;
        public readonly bool IsHost;
        public readonly long TickMono;
        public readonly HostAuthorityPhase PhaseSource;
        public HostChanged(bool wasHost, bool isHost, long tickMono, HostAuthorityPhase phase)
        {
            WasHost = wasHost; IsHost = isHost; TickMono = tickMono; PhaseSource = phase;
        }
    }

    // P7 Item 17: published by Coordinator.TickOnce when the observed plan type changes
    // (edge tracker on the Coordinator side per REV 2 C10 fix). Consumed by
    // ActionValueEstimator's training queue (placeholder reward=0f at v0.2 ship).
    public readonly struct PlanTransition
    {
        public readonly TeamId Team;
        public readonly TAC_AI.AI.Forms.Smart.Planning.PlanLibrary.PlanType OldType, NewType;
        public readonly float Reward;
        public readonly long TickMono;
        public PlanTransition(TeamId team, TAC_AI.AI.Forms.Smart.Planning.PlanLibrary.PlanType oldType,
            TAC_AI.AI.Forms.Smart.Planning.PlanLibrary.PlanType newType, float reward, long tickMono)
        {
            Team = team; OldType = oldType; NewType = newType; Reward = reward; TickMono = tickMono;
        }
    }

    // Identity-tagged outcome event. Published whenever a Smart-driven tech achieves an
    // identity-appropriate success: Hunter / Sniper / AircraftHunter kill a hostile,
    // Base absorbs damage without falling, Gatherer delivers a block, AircraftSupport
    // protects an ally. Per Docs/SMART-IDENTITY-DESIGN.md sec 9: LearningService trainers
    // are NOT forked per identity in v0.1 - the Identity tag is collected for future
    // stratification. Phase 6 wires KillScored from SmartEventBridge.OnTankDestroyed.
    // BaseHeld / BlockDelivered / AllyProtected are reserved for follow-on phases.
    public enum OutcomeKind : byte
    {
        KillScored = 0,       // Hunter / Sniper / AircraftHunter / AircraftSupport: hostile destroyed by us
        BaseHeld = 1,         // Base: damage absorbed without falling over a window
        BlockDelivered = 2,   // Gatherer: cargo deposited at delivery point
        AllyProtected = 3,    // AircraftSupport: same-team ally took no damage while we were in support range
        BaseLost = 4,         // Base: tech died — paired with BaseHeld for retention rate
        // Guard-bucket violations emitted by BehaviorGuardWorker on pathology edge
        // transitions. Source-byte high-nibble carries the guard ordinal so the
        // aggregator can attribute the dominant sub-guard. Reward channel is muted
        // by construction (LearningTuning.OutcomeWeights[GuardViolation_*] = 0).
        GuardViolation_Movement = 5,
        GuardViolation_Role = 6,
        GuardViolation_Combat = 7,
        // Sentinel must stay LAST so array sizing tracks cardinality.
        Count = 8,
    }

    // Director envelope structs — background daemons PublishFromWorker these;
    // SmartForm.Operations drains and executes on the main thread. Each carries
    // ScenarioGeneration so post-rollback/teardown writes that re-arrive stamped
    // with a stale generation get rejected at drain time.

    /// <summary>
    /// Background-scenario asks main thread to mint teams + spawn the initial recipe
    /// population for a scenario. Routed by ScenarioWorker on first tick of a new scenario.
    /// Main-thread executor materializes the teams (ManBaseTeams non-concurrent path) +
    /// fires the initial spawn batch.
    /// </summary>
    public readonly struct ScenarioBootRequest
    {
        public readonly int ScenarioId;
        public readonly int ScenarioGeneration;
        public readonly float Intensity;
        public readonly UnityEngine.Vector3 Centroid;
        public ScenarioBootRequest(int scenarioId, int generation, float intensity,
            UnityEngine.Vector3 centroid)
        {
            ScenarioId = scenarioId; ScenarioGeneration = generation;
            Intensity = intensity; Centroid = centroid;
        }
    }

    /// <summary>Background-scenario asks main thread to spawn one mobile tech.</summary>
    public readonly struct ScenarioSpawnRequest
    {
        public readonly UnityEngine.Vector3 Position;
        public readonly UnityEngine.Vector3 Forwards;
        public readonly int Team;
        public readonly int ScenarioId;
        public readonly int ScenarioGeneration;
        public readonly BaseTerrain Terrain;
        public readonly BasePurpose Purpose;
        public readonly FactionLevel Progression;
        public readonly FactionSubTypes Faction;
        public readonly int MaxPrice;
        public readonly int Grade;
        public readonly TAC_AI.Templates.RawTechOffset Offset;
        public readonly bool ExcludeErad;
        // Curated role folder to pull from (null => use the global filter). IsBase routes
        // through the anchored base-spawn path instead of the mobile prefab path.
        public readonly string DirectorFolder;
        public readonly bool IsBase;
        public ScenarioSpawnRequest(UnityEngine.Vector3 pos, UnityEngine.Vector3 fwd, int team,
            int scenarioId, int generation, BaseTerrain terrain,
            BasePurpose purpose, FactionLevel progression,
            FactionSubTypes faction, int maxPrice, int grade,
            TAC_AI.Templates.RawTechOffset offset, bool excludeErad)
            : this(pos, fwd, team, scenarioId, generation, terrain, purpose, progression,
                  faction, maxPrice, grade, offset, excludeErad, null, false)
        {
        }
        public ScenarioSpawnRequest(UnityEngine.Vector3 pos, UnityEngine.Vector3 fwd, int team,
            int scenarioId, int generation, BaseTerrain terrain,
            BasePurpose purpose, FactionLevel progression,
            FactionSubTypes faction, int maxPrice, int grade,
            TAC_AI.Templates.RawTechOffset offset, bool excludeErad,
            string directorFolder, bool isBase)
        {
            Position = pos; Forwards = fwd; Team = team;
            ScenarioId = scenarioId; ScenarioGeneration = generation;
            Terrain = terrain; Purpose = purpose; Progression = progression;
            Faction = faction; MaxPrice = maxPrice; Grade = grade;
            Offset = offset; ExcludeErad = excludeErad;
            DirectorFolder = directorFolder; IsBase = isBase;
        }
    }

    /// <summary>Background-chunkregen asks main thread to spawn a chunk of one type at a position.</summary>
    public readonly struct ChunkSpawnRequest
    {
        public readonly UnityEngine.Vector3 Centroid;
        public readonly float InnerRadius;
        public readonly float OuterRadius;
        public readonly int CountBudget;
        public readonly int ScenarioId;
        public readonly int ScenarioGeneration;
        public ChunkSpawnRequest(UnityEngine.Vector3 centroid, float innerR, float outerR,
            int countBudget, int scenarioId, int generation)
        {
            Centroid = centroid; InnerRadius = innerR; OuterRadius = outerR;
            CountBudget = countBudget; ScenarioId = scenarioId; ScenarioGeneration = generation;
        }
    }

    /// <summary>Background-scenario asks main thread to anchor inter-team relations.</summary>
    public readonly struct ScenarioRelationsRequest
    {
        public readonly int TeamA;
        public readonly int TeamB;
        public readonly byte RelationCode; // matches TeamRelations enum byte
        public readonly int ScenarioGeneration;
        public ScenarioRelationsRequest(int teamA, int teamB, byte relationCode, int generation)
        {
            TeamA = teamA; TeamB = teamB; RelationCode = relationCode; ScenarioGeneration = generation;
        }
    }

    /// <summary>
    /// One-shot ask: executor should subscribe ScenarioWorker's TeamRemovedEvent handler.
    /// Fired by the worker on first tick of any scenario; executor idempotent.
    /// </summary>
    public readonly struct ScenarioTeamSubscribeRequest
    {
    }

    /// <summary>
    /// Background guard worker asks main thread to gather rare Unity-only reads (terrain
    /// height, AILimitSettings flags, FrustrationMeter) for a tech. RequestFields is a
    /// bitmask of which fields the caller wants — main-thread executor fills only those.
    /// Round-trip via a result slot keyed by (techId, monoTick) on SpawnEnvelopeExecutor.
    /// </summary>
    public readonly struct GuardEvaluationRequest
    {
        public readonly TechId TechId;
        public readonly long MonoTick;
        public readonly ushort RequestFields;
        public GuardEvaluationRequest(TechId techId, long monoTick, ushort requestFields)
        {
            TechId = techId; MonoTick = monoTick; RequestFields = requestFields;
        }
    }

    /// <summary>Background-scenario asks main thread to zero a team's angerThreshold.</summary>
    public readonly struct ScenarioAngerResetRequest
    {
        public readonly int TeamId;
        public readonly int ScenarioGeneration;
        public ScenarioAngerResetRequest(int teamId, int generation)
        {
            TeamId = teamId; ScenarioGeneration = generation;
        }
    }

    public readonly struct IdentityOutcome
    {
        public readonly TechId TechId;
        public readonly TAC_AI.AI.Forms.Smart.Identity.SmartIdentity Identity;
        public readonly OutcomeKind Kind;
        public readonly float Magnitude;
        // Low-nibble = Live(0) / Replay(1). High-nibble = guard ordinal 0..10 with
        // 15 = wildcard / no-guard sentinel. Phase 5 wires the guard high-nibble.
        public readonly byte Source;
        // Backcompat 4-arg ctor — Source defaults to wildcard sentinel (15<<4) so old
        // callers stamp "no guard, live".
        public IdentityOutcome(TechId techId, TAC_AI.AI.Forms.Smart.Identity.SmartIdentity identity,
            OutcomeKind kind, float magnitude)
            : this(techId, identity, kind, magnitude, (byte)(15 << 4))
        {
        }
        public IdentityOutcome(TechId techId, TAC_AI.AI.Forms.Smart.Identity.SmartIdentity identity,
            OutcomeKind kind, float magnitude, byte source)
        {
            TechId = techId;
            Identity = identity;
            Kind = kind;
            Magnitude = magnitude;
            Source = source;
        }
    }

    /// <summary>
    /// Smart-internal typed pub/sub bus per WORLD-CONTRACT §5. Synchronous main-thread dispatch.
    /// Events are value-typed structs; subscribers receive by value (per-call allocation is the
    /// 16-32 bytes of the struct; no boxing because Action&lt;T&gt; with a struct T does not box).
    ///
    /// Subscriber exceptions are caught at the dispatch boundary so one bad subscriber does not
    /// break the others (per ARCHITECTURE §4 E4).
    ///
    /// Phase 3.2 (FIX-PLAN.md): the contract previously said "Workers MUST NOT call Publish
    /// directly; they route through a main-thread relay." The relay didn't exist — PerceptionWorker
    /// called <see cref="Publish"/> from a worker thread (AUDIT-R2 §2.R2.G + §2.R2.I). The relay
    /// is now built in: worker code calls <see cref="PublishFromWorker"/> which enqueues into a
    /// process-global main-thread queue drained by <see cref="DrainMainThreadQueue"/>. The drain is
    /// called per-tick by <see cref="SmartForm.Operations"/> (release builds) and additionally per-
    /// frame by <see cref="SmartTrainingDriver.Update"/> (dev builds, for sub-tick latency).
    /// </summary>
    public static class WorldEventBus
    {
        // Phase 9 (FIX-PLAN.md) — AUDIT R1 §3.6: prior shape was a List<> + per-Publish
        // .ToArray() snapshot under lock. That meant the hot Publish path allocated an
        // Action<TEvent>[] EVERY publish — at combat tempo (DamageObserved,
        // ProjectileFired several times per second per tech × 32 techs) that was
        // measurable GC. Switch to copy-on-write: Subscribe/Unsubscribe allocate one
        // new array; Publish reads the volatile reference with zero allocation.
        private static class Subscribers<TEvent> where TEvent : struct
        {
            public static readonly object Sync = new object();
            public static Action<TEvent>[] Handlers = System.Array.Empty<Action<TEvent>>();
        }

        // Phase 3.2 (FIX-PLAN.md): worker-thread → main-thread relay queue.
        // The queue is unbounded; main-thread tick is expected to drain it within
        // each frame. If a flood swamps the main loop, the queue grows transiently
        // — that is a load-shedding signal, not a correctness problem.
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadQueue =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();

        public static void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct
        {
            if (handler == null) return;
            lock (Subscribers<TEvent>.Sync)
            {
                var current = Subscribers<TEvent>.Handlers;
                var next = new Action<TEvent>[current.Length + 1];
                System.Array.Copy(current, next, current.Length);
                next[current.Length] = handler;
                System.Threading.Volatile.Write(ref Subscribers<TEvent>.Handlers, next);
            }
        }

        public static void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : struct
        {
            if (handler == null) return;
            lock (Subscribers<TEvent>.Sync)
            {
                var current = Subscribers<TEvent>.Handlers;
                int idx = System.Array.IndexOf(current, handler);
                if (idx < 0) return;
                if (current.Length == 1)
                {
                    System.Threading.Volatile.Write(ref Subscribers<TEvent>.Handlers, System.Array.Empty<Action<TEvent>>());
                    return;
                }
                var next = new Action<TEvent>[current.Length - 1];
                if (idx > 0) System.Array.Copy(current, 0, next, 0, idx);
                if (idx < current.Length - 1) System.Array.Copy(current, idx + 1, next, idx, current.Length - 1 - idx);
                System.Threading.Volatile.Write(ref Subscribers<TEvent>.Handlers, next);
            }
        }

        public static void Publish<TEvent>(TEvent ev) where TEvent : struct
        {
            // Copy-on-write: read the volatile reference once; iterate without locking.
            // Subscribe/Unsubscribe always publish a NEW array (atomic ref swap) so this
            // dispatch sees a stable snapshot. Zero allocation on the hot path.
            var snapshot = System.Threading.Volatile.Read(ref Subscribers<TEvent>.Handlers);
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i](ev); }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogError(
                        "Smart.WorldEventBus: subscriber for " + typeof(TEvent).Name +
                        " threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Worker-thread-safe publish path. Captures <paramref name="ev"/> into a closure
        /// that will be dispatched the next time <see cref="DrainMainThreadQueue"/> runs
        /// on the main thread. Subscribers always observe the event from the main thread.
        /// Per FIX-PLAN.md Phase 3.2.
        /// </summary>
        public static void PublishFromWorker<TEvent>(TEvent ev) where TEvent : struct
        {
            _mainThreadQueue.Enqueue(() => Publish(ev));
        }

        /// <summary>
        /// Drain the worker→main-thread relay queue. MUST be called from the main thread
        /// per tick; <see cref="SmartForm.Operations"/> calls this. Each dequeued action
        /// dispatches one event via <see cref="Publish"/>. Subscriber exceptions are caught
        /// by Publish itself; queue-drain itself is exception-safe.
        /// </summary>
        public static void DrainMainThreadQueue()
        {
            // Cap drain count per call so a flood doesn't starve the main loop.
            int budget = 256;
            Action work;
            while (budget-- > 0 && _mainThreadQueue.TryDequeue(out work))
            {
                try { work(); }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning(
                        "Smart.WorldEventBus.DrainMainThreadQueue: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>Clear all subscribers. Smart's DeInitGlobal calls this.</summary>
        public static void ClearAll()
        {
            // For each known event type, clear its handler list.
            // Generic-static dispatch means we can't trivially iterate; we explicitly clear the
            // types defined in this file. New event types added in other subsystems should be
            // added here (or a registry pattern adopted).
            ClearSubscribers<TechSpawned>();
            ClearSubscribers<TechDespawned>();
            ClearSubscribers<TechTeamChanged>();
            ClearSubscribers<TechSeen>();
            ClearSubscribers<DamageObserved>();
            ClearSubscribers<ProjectileFired>();
            ClearSubscribers<WorldSaving>();
            ClearSubscribers<WorldSaved>();
            ClearSubscribers<WorldLoading>();
            ClearSubscribers<WorldLoaded>();
            ClearSubscribers<BeliefUpdated>();
            // P4 Item 14 (REV 7): TechLost now has a producer (AetherFuser), so it's no
            // longer Obsolete — move outside the pragma block.
            ClearSubscribers<TechLost>();
            // P7 Item 17 (REV 7): PlanTransition published by Coordinator.TickOnce edge tracker.
            ClearSubscribers<PlanTransition>();
            ClearSubscribers<HostChanged>();   // L-012
            ClearSubscribers<WorldResetting>();        // L-036
            ClearSubscribers<WorldResetCompleted>();   // L-036
            // P6 Item 27 + P7 Item 21: IdentityOutcome consumer in IdentityOutcomeConsumer.
            ClearSubscribers<IdentityOutcome>();
            // Director scenario / chunk-regen envelopes — main-thread executor in
            // SmartForm.Operations is the only subscriber, but ClearAll still hits these
            // for completeness.
            ClearSubscribers<ScenarioBootRequest>();
            ClearSubscribers<ScenarioSpawnRequest>();
            ClearSubscribers<ChunkSpawnRequest>();
            ClearSubscribers<ScenarioRelationsRequest>();
            ClearSubscribers<ScenarioAngerResetRequest>();
            ClearSubscribers<ScenarioTeamSubscribeRequest>();
            ClearSubscribers<GuardEvaluationRequest>();
            // Phase 10 (FIX-PLAN.md): obsolete event types still allocate per-T statics
            // when first JITted; clear them inside #pragma so the [Obsolete] warning is
            // contained to this one place. When v0.2+ wires their producers, remove from
            // the pragma block (TechLost already moved out in P4 Item 14).
#pragma warning disable CS0618
            ClearSubscribers<BlockAttached>();
            ClearSubscribers<BlockDetached>();
            ClearSubscribers<PlayerJoined>();
            ClearSubscribers<PlayerLeft>();
            ClearSubscribers<WorldStarted>();
            ClearSubscribers<WorldModeSwitched>();
#pragma warning restore CS0618

            // Phase 3.2 (FIX-PLAN.md): drain the marshal queue so any pending worker-
            // published events do not leak into a future session's subscribers.
            Action _;
            while (_mainThreadQueue.TryDequeue(out _)) { /* discard */ }
        }

        private static void ClearSubscribers<TEvent>() where TEvent : struct
        {
            lock (Subscribers<TEvent>.Sync)
                System.Threading.Volatile.Write(ref Subscribers<TEvent>.Handlers, System.Array.Empty<Action<TEvent>>());
        }
    }
}
