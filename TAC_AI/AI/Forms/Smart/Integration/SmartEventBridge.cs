using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Integration
{
    /// <summary>
    /// Per FIX-PLAN.md Phase 3.3: bridges TerraTech-side observable signals into Smart's
    /// <see cref="WorldEventBus"/>. Three sources, each handled in the cheapest non-invasive
    /// way available:
    /// <list type="number">
    /// <item><b>Tank damage</b> — <c>tank.DamageEvent.Subscribe</c> per Smart-driven tech.
    /// Verified pattern at <c>TankAIHelper.cs:766</c>. No Harmony patch required.</item>
    /// <item><b>Team changes</b> — <c>ManTechs.inst.TankTeamChangedEvent.Subscribe</c> once
    /// per session. Verified pattern at <c>TankAIManager.cs:71</c>. No Harmony patch.</item>
    /// <item><b>Projectile fired</b> — no public event; per SHELL-API-GUIDE OQ-8 we own a
    /// Harmony postfix on <c>ModuleWeapon.Fire</c>. The postfix runs on the firing thread
    /// (main thread for TerraTech weapons), so we route through <see cref="WorldEventBus.Publish"/>
    /// directly.</item>
    /// </list>
    ///
    /// Lifecycle: <see cref="Install"/> is called from <c>SmartForm.InitGlobal</c>;
    /// <see cref="Uninstall"/> from <c>DeInitGlobal</c>. Per-tank handlers are attached
    /// from <c>SmartForm.OnTechSpawn</c> via <see cref="AttachPerTank"/>.
    /// </summary>
    internal static class SmartEventBridge
    {
        private const string HarmonyId = "Advanced AI: Smart event bridge";
        private static Harmony _harmony;
        private static bool _installed;

        // Per-tank damage handlers keyed by Tank reference so we can detach cleanly on recycle.
        private static readonly Dictionary<Tank, Action<ManDamage.DamageInfo>> _damageHandlers =
            new Dictionary<Tank, Action<ManDamage.DamageInfo>>();
        // L-010: shadow map keyed by TechId so the orphan-sweep path can detach without
        // needing a live Tank reference. Populated in AttachPerTank, drained in DetachPerTank
        // (both overloads). ConcurrentDictionary because the orphan sweep can fire from the
        // observation tick while the OnTechRecycle path fires from main-thread engine events
        // (they collide rarely but the race exists).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<TechId, Tank> _damageHandlersByTechId =
            new System.Collections.Concurrent.ConcurrentDictionary<TechId, Tank>();

        // Bound delegate so subscribe/unsubscribe target the same Action instance.
        // Engine signature verified against TankAIManager.cs:191 — the real shape is
        // Action<Tank, ManTechs.TeamChangeInfo>, NOT Action<int, Tank> as previously
        // assumed. Phase 11 (verify-build) — SHELL-API-GUIDE §7.2 / OQ-7 resolution
        // with the actual engine surface.
        private static Action<Tank, ManTechs.TeamChangeInfo> _teamChangeHandler;

        // Tank spawn / destroy handlers for WORLD-CONTRACT enemy-belief registration.
        // Engine signatures verified by direct reflection on Assembly-CSharp.dll plus
        // existing TAC_AI usage (TankAIManager.cs:70 OnTankAddition(Tank)):
        //   ManTechs.TankPostSpawnEvent  is Action<Tank>
        //   ManTechs.TankDestroyedEvent  is Action<Tank, ManDamage.DamageInfo>
        // Without these, WorldModel.RecordObservation is never called for non-Smart-driven
        // techs, BeliefSnapshot.ByTech only ever contains friendlies, EngagementUtility
        // returns 0 forever, and the tactical optimizer's goal stays glued to the tech's
        // current position — the observed zero-movement bug.
        private static Action<Tank> _tankSpawnHandler;
        private static Action<Tank, ManDamage.DamageInfo> _tankDestroyedHandler;

        /// <summary>Install the once-per-session subscriptions and Harmony patches.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            // [TEMP DIAGNOSTIC] Per-sub-call timing for the un-instrumented Install path —
            // the prime spawn-freeze suspect since Harmony.PatchAll scans every type in
            // the entire TAC_AI assembly. Tagged with "[TIMING]" for grep; remove once the
            // freeze cause is identified.
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ManTechs.TankTeamChangedEvent — captured/converted techs need re-registration.
            try
            {
                _teamChangeHandler = OnTankTeamChanged;
                ManTechs.inst.TankTeamChangedEvent.Subscribe(_teamChangeHandler);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge: TankTeamChangedEvent subscribe failed: " + ex.Message);
            }
            DebugTAC_AI.Log("SmartEventBridge.Install[TIMING] TankTeamChangedEvent.Subscribe: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // TankPostSpawnEvent + TankDestroyedEvent — fill WorldModel with ALL visible techs,
            // not just Smart-driven ones. SmartForm.OnTechSpawn already registers Smart-driven
            // friendlies; this complements with non-Smart techs (enemies, neutrals, NPC bases)
            // so EngagementUtility has something to engage against. Initial sweep below catches
            // techs that existed at Install time (form swap mid-session). Subscriptions handle
            // anything spawned after.
            try
            {
                _tankSpawnHandler = OnTankSpawned;
                _tankDestroyedHandler = OnTankDestroyed;
                ManTechs.inst.TankPostSpawnEvent.Subscribe(_tankSpawnHandler);
                ManTechs.inst.TankDestroyedEvent.Subscribe(_tankDestroyedHandler);
                // Initial sweep: register every tech that already exists when Smart activates.
                // Without this, switching to Smart mid-game would see an empty world until new
                // techs spawned. ManTechs.IterateTechs is the standard enumeration.
                int swept = 0;
                foreach (var existing in ManTechs.inst.IterateTechs())
                {
                    if (existing == null) continue;
                    RegisterExternalTech(existing);
                    swept++;
                }
                DebugTAC_AI.Log("SmartEventBridge.Install: initial tech sweep registered " + swept + " existing techs.");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge: TankPostSpawn/Destroyed subscribe failed: " + ex.Message);
            }
            DebugTAC_AI.Log("SmartEventBridge.Install[TIMING] TankPostSpawn+Destroyed.Subscribe + initial sweep: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Harmony patches — currently just the ProjectileFired postfix.
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(SmartEventBridge).Assembly);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge: Harmony patch install failed: " + ex.Message);
            }
            DebugTAC_AI.Log("SmartEventBridge.Install[TIMING] Harmony.PatchAll: " + sw.ElapsedMilliseconds + "ms");

            // Director outcome publishers. No-op-safe before Director.Init completes —
            // PublishFromWorker events are ignored by IdentityOutcomeConsumer until the
            // Director's constraint engine reads them.
            try { InstallDirectorPublishers(); }
            catch (Exception ex) { LogPub("install-fanout", ex); }

            DebugTAC_AI.Log("SmartEventBridge.Install[TIMING] TOTAL: " + swTotal.ElapsedMilliseconds + "ms");
        }

        /// <summary>Tear down everything Install established. Safe to call when not installed.</summary>
        public static void Uninstall()
        {
            if (!_installed) return;

            try { UninstallDirectorPublishers(); }
            catch (Exception ex) { LogPub("uninstall-fanout", ex); }

            try
            {
                if (_teamChangeHandler != null)
                    ManTechs.inst.TankTeamChangedEvent.Unsubscribe(_teamChangeHandler);
            }
            catch { /* shell may be torn down; ignore */ }
            _teamChangeHandler = null;

            try
            {
                if (_tankSpawnHandler != null)
                    ManTechs.inst.TankPostSpawnEvent.Unsubscribe(_tankSpawnHandler);
                if (_tankDestroyedHandler != null)
                    ManTechs.inst.TankDestroyedEvent.Unsubscribe(_tankDestroyedHandler);
            }
            catch { /* shell may be torn down; ignore */ }
            _tankSpawnHandler = null;
            _tankDestroyedHandler = null;

            // Drop every still-attached per-tank handler.
            // BUG-053: AttachPerTank wires three surfaces (damage + cargo + weapon-fire);
            // Uninstall must mirror all three or per-tank cargo / fire subscriptions stay
            // bound to live techs after the bridge tears down. Per-tech iteration matches
            // the AttachPerTank pairing.
            foreach (var pair in _damageHandlers)
            {
                var tank = pair.Key;
                try { if (tank != null) tank.DamageEvent.Unsubscribe(pair.Value); }
                catch { /* tank may already be gone */ }
                if (tank == null) continue;
                try
                {
                    var techId = TechId.FromTank(tank);
                    SmartRuntime.CargoState?.DetachPerTank(tank);
                    if (SmartRuntime.WeaponFires != null && tank.Weapons != null)
                        SmartRuntime.WeaponFires.Unwire(techId, tank.Weapons);
                }
                catch { /* tank may be partially recycled */ }
            }
            _damageHandlers.Clear();
            _damageHandlersByTechId.Clear();   // L-010 shadow map

            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
            _harmony = null;
            _installed = false;
        }

        // Per-minute DamageObserved counter for the damage_events_per_min constraint.
        private static long _damageEventsCounter;
        public static long DamageEventsCounter => System.Threading.Interlocked.Read(ref _damageEventsCounter);
        private static System.Action<DamageObserved> _damageCounterHandler;

        // Director publisher fan-out. Installs the 3 outcome publishers (BlockDelivered,
        // BaseHeld+BaseLost, AllyProtected) + the damage counter. GuardWorker is installed
        // via its own daemon (TrainingDirector.Init). Idempotent.
        public static void InstallDirectorPublishers()
        {
            try { Director.Publishers.BlockDeliveredPublisher.Install(); } catch (Exception ex) { LogPub("BlockDelivered", ex); }
            try { Director.Publishers.BaseHeldPublisher.Install(); } catch (Exception ex) { LogPub("BaseHeld", ex); }
            try { Director.Publishers.AllyProtectionPublisher.Install(); } catch (Exception ex) { LogPub("AllyProtection", ex); }
            try
            {
                if (_damageCounterHandler == null)
                {
                    _damageCounterHandler = _ => System.Threading.Interlocked.Increment(ref _damageEventsCounter);
                    WorldEventBus.Subscribe(_damageCounterHandler);
                }
            }
            catch (Exception ex) { LogPub("DamageCounter", ex); }
        }

        public static void UninstallDirectorPublishers()
        {
            try { Director.Publishers.BlockDeliveredPublisher.Uninstall(); } catch (Exception ex) { LogPub("BlockDelivered", ex); }
            try { Director.Publishers.BaseHeldPublisher.Uninstall(); } catch (Exception ex) { LogPub("BaseHeld", ex); }
            try { Director.Publishers.AllyProtectionPublisher.Uninstall(); } catch (Exception ex) { LogPub("AllyProtection", ex); }
            try
            {
                if (_damageCounterHandler != null)
                {
                    WorldEventBus.Unsubscribe(_damageCounterHandler);
                    _damageCounterHandler = null;
                }
            }
            catch (Exception ex) { LogPub("DamageCounter", ex); }
        }

        private static void LogPub(string name, Exception ex)
        {
            DebugTAC_AI.LogWarnFileOnly("director-publisher-" + name,
                "[AIWARN] Director publisher " + name + " " + ex.GetType().Name + ": " + ex.Message);
        }

        /// <summary>
        /// Attach a per-tank damage subscription. Called from <c>SmartForm.OnTechSpawn</c>
        /// once Smart adopts a tech. Idempotent on the same tank.
        /// </summary>
        public static void AttachPerTank(Tank tank)
        {
            if (tank == null) return;
            if (_damageHandlers.ContainsKey(tank)) return;
            Action<ManDamage.DamageInfo> handler = info => OnTankDamage(tank, info);
            _damageHandlers[tank] = handler;
            // L-010: stash by TechId for the orphan-sweep detach path.
            _damageHandlersByTechId[TechId.FromTank(tank)] = tank;
            try { tank.DamageEvent.Subscribe(handler); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge.AttachPerTank: " + ex.Message);
                _damageHandlers.Remove(tank);
                _damageHandlersByTechId.TryRemove(TechId.FromTank(tank), out _);
            }

            // FEATURE-EXPANSION-PLAN Phase-4: wire the per-tank cargo + weapon-fire
            // subscriptions alongside damage. CargoStatePublisher hooks ItemPickup/Release
            // events on Tank.Holders; WeaponFireBuffer subscribes to TechWeapon.WeaponsFiredEvent.
            try
            {
                var techId = TechId.FromTank(tank);
                SmartRuntime.CargoState?.AttachPerTank(tank);
                if (SmartRuntime.WeaponFires != null && tank.Weapons != null)
                    SmartRuntime.WeaponFires.Wire(techId, tank.Weapons);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge.AttachPerTank: cargo/fire wire: " + ex.Message);
            }

            // BlockDelivered: lazy-subscribe Smart Base techs with a Chunks holder so
            // cross-tank cargo handoffs attribute a Gatherer delivery.
            try
            {
                var bd = Director.Publishers.BlockDeliveredPublisher.Instance;
                if (bd != null) bd.TrySubscribeBase(tank);
            }
            catch (Exception ex) { LogPub("attach-base", ex); }
        }

        /// <summary>Detach a per-tank damage subscription. Called from <c>SmartForm.OnTechRecycle</c>.</summary>
        public static void DetachPerTank(Tank tank)
        {
            if (tank == null) return;
            if (!_damageHandlers.TryGetValue(tank, out var handler)) return;
            _damageHandlers.Remove(tank);
            _damageHandlersByTechId.TryRemove(TechId.FromTank(tank), out _);
            try { tank.DamageEvent.Unsubscribe(handler); }
            catch { /* swallow; tank may be partially recycled */ }

            // FEATURE-EXPANSION-PLAN Phase-4: matching tear-down for cargo + weapon-fire
            // subscriptions. Exception-isolated per cargo's null-tolerance contract.
            try
            {
                var techId = TechId.FromTank(tank);
                SmartRuntime.CargoState?.DetachPerTank(tank);
                if (SmartRuntime.WeaponFires != null && tank.Weapons != null)
                    SmartRuntime.WeaponFires.Unwire(techId, tank.Weapons);
            }
            catch { /* tank partially recycled — drop quietly */ }

            try
            {
                var bd = Director.Publishers.BlockDeliveredPublisher.Instance;
                if (bd != null) bd.ForgetTank(tank);
            }
            catch { /* drop quietly */ }
        }

        /// <summary>
        /// L-010: TechId-keyed detach for the orphan-sweep path. Used when the engine has
        /// already purged the GameObject and the Tank reference is gone. Looks up the
        /// (still-alive) Tank ref via the shadow map; if found, unsubscribes; if the shadow
        /// map says we never attached (or already detached), no-ops silently.
        /// </summary>
        public static void DetachPerTank(TechId id)
        {
            if (!_damageHandlersByTechId.TryRemove(id, out var tank)) return;
            if (tank == null) return;
            if (_damageHandlers.TryGetValue(tank, out var handler))
            {
                _damageHandlers.Remove(tank);
                try { tank.DamageEvent.Unsubscribe(handler); }
                catch { /* tank may have been purged by the engine */ }
            }

            // FEATURE-EXPANSION-PLAN Phase-4: cargo + weapon-fire detach for the orphan
            // path. CargoStatePublisher exposes a TechId overload that walks its own shadow
            // map; WeaponFireBuffer takes (TechId, TechWeapon) so we only call it if the
            // tank ref is still alive enough to dereference Weapons.
            // BUG-068: Unity-null-test the tank before reading .Weapons — Unity returns a
            // fake-null when the GameObject has been destroyed but our shadow map still holds
            // the managed ref. Implicit bool cast is the engine's UnityEngine.Object overload.
            try
            {
                SmartRuntime.CargoState?.DetachPerTank(id);
                if (SmartRuntime.WeaponFires != null && tank && tank.Weapons != null)
                    SmartRuntime.WeaponFires.Unwire(id, tank.Weapons);
            }
            catch { /* engine-purged ref — best-effort */ }
        }

        // ------------------ Event handlers ------------------

        /// <summary>
        /// Engine-side TankPostSpawnEvent → WorldModel registration. Fires for every tech
        /// spawn (Smart-driven or not). The check inside RegisterExternalTech keeps this safe
        /// when SmartForm.OnTechSpawn races us on a Smart-driven tech.
        /// </summary>
        private static void OnTankSpawned(Tank tank)
        {
            RegisterExternalTech(tank);
        }

        /// <summary>
        /// Engine-side TankDestroyedEvent → WorldModel deregistration. Drops the tech's belief
        /// entry so the optimizer stops chasing a corpse. SmartForm.OnTechRecycle already does
        /// the equivalent for Smart-driven techs; this handles non-Smart ones the bridge owns.
        /// </summary>
        private static void OnTankDestroyed(Tank tank, ManDamage.DamageInfo info)
        {
            if (tank == null) return;
            try
            {
                var techId = World.TechId.FromTank(tank);
                var deadHelper = tank.GetComponent<TankAIHelper>();
                // Aether/T3: single unified cleanup fan-out (see SmartRuntime.Deregister).
                // For combat death where the helper still has a SmartPerTechState we get the
                // TeamId from there; otherwise default(TeamId) skips the team-side step (which
                // is the historical behavior for non-Smart-driven techs that get destroyed).
                if (deadHelper != null && deadHelper.FormState is SmartPerTechState deadState)
                {
                    SmartRuntime.Deregister(deadState.TechId, deadState.TeamId, deadHelper, tank);
                }
                else
                {
                    SmartRuntime.Deregister(techId, default(World.TeamId), null, tank);
                }

                // Phase 6 (SMART-IDENTITY-DESIGN.md sec 9): publish an identity-tagged
                // KillScored outcome when the attacker is a Smart-driven tech on a different
                // team than the victim. LearningService trainers can later stratify reward
                // signal by identity; v0.1 just collects the tagged event.
                if (info.SourceTank != null && info.SourceTank != tank && info.SourceTank.Team != tank.Team)
                {
                    var attackerHelper = info.SourceTank.GetComponent<TankAIHelper>();
                    if (attackerHelper != null && attackerHelper.FormState is SmartPerTechState attackerState)
                    {
                        var attackerIdentity = attackerState.IdentityStamp.Identity;
                        if (attackerIdentity != Identity.SmartIdentity.Generic)
                        {
                            var attackerTechId = World.TechId.FromTank(info.SourceTank);
                            World.WorldEventBus.Publish(new World.IdentityOutcome(
                                attackerTechId, attackerIdentity, World.OutcomeKind.KillScored, 1f,
                                (byte)(15 << 4)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge.OnTankDestroyed: " + ex.Message);
            }
        }

        /// <summary>
        /// Idempotent registration of a tech into the WorldModel. Skips Smart-driven techs
        /// because SmartForm.OnTechSpawn already handles those (with the correct maxAccel
        /// derived from the vehicle thrust map). For all other techs — enemies, neutrals,
        /// NPC bases, missile-spawned units — this populates BeliefSnapshot.ByTech so
        /// EngagementUtility actually has targets to engage against. Without this path the
        /// world model only ever sees friendlies and the tactical optimizer's gradient is
        /// zero everywhere.
        ///
        /// maxAccelEstimate uses a conservative default (5 m/s²) since we don't have the
        /// thrust-map data for non-Smart techs. KalmanUpdate.Propagate uses this as the
        /// process-noise bound; over-estimating only widens belief uncertainty harmlessly.
        /// </summary>
        public static void RegisterExternalTech(Tank tank)
        {
            if (tank == null) return;
            try
            {
                var techId = World.TechId.FromTank(tank);
                // Skip if already registered (either by an earlier RegisterExternalTech call or
                // by SmartForm.OnTechSpawn for Smart-driven techs). WorldModel.RegisterTech is
                // overwrite-on-write — re-registering would clobber a Smart-driven tech's
                // properly-derived maxAccel with our conservative default, and would also
                // republish a duplicate TechSpawned event on the bus.
                if (SmartRuntime.World == null || SmartRuntime.World.IsRegistered(techId)) return;

                var teamId = new World.TeamId(tank.Team);
                Vector3 pos = tank.boundsCentreWorldNoCheck;
                Vector3 vel = tank.rbody != null ? tank.rbody.velocity : Vector3.zero;
                Vector3 fwd = tank.rootBlockTrans != null ? tank.rootBlockTrans.forward : Vector3.forward;
                SmartRuntime.World.RegisterTech(
                    techId, teamId, pos, vel,
                    forward: fwd,
                    maxAccelEstimate: 5f,
                    currentTick: World.MonoClock.Now());
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge.RegisterExternalTech: " + ex.Message);
            }
        }

        private static void OnTankDamage(Tank victim, ManDamage.DamageInfo info)
        {
            try
            {
                var victimId = TechId.FromTank(victim);
                bool hasAttacker = info.SourceTank != null && info.SourceTank != victim;
                var attackerId = hasAttacker
                    ? TechId.FromTank(info.SourceTank)
                    : default(TechId);
                // P11 T6 Item 68 (post-decompile correction): the previous "fields don't exist"
                // comment was wrong. Decompile of F:\Tac-AI\Decompiled\AssemblyCSharp\ManDamage.cs:54-88
                // proves ManDamage.DamageInfo exposes public Vector3 HitPosition and
                // public Vector3 DamageDirection (auto-normalized in the ctor at line 85),
                // both NetSerialize-safe (lines 134-135 + 156-157). Wiring them through
                // SanitizedDamageInfo unblocks the facing-threat orbit refinement that was
                // mistakenly deferred to "v0.3" — RepairSupportGoalSource now reads the
                // averaged attacker bearing from DamageHintBuffer.TryGetRecent.
                // FEATURE-EXPANSION-PLAN §8.5 R2-01: forward the engine's
                // ManDamage.DamageInfo.DamageType byte (ManDamage.cs:58 — the tech-wide
                // enum, NOT per-block Damageable.DamageableType). Feeds DamageHintBuffer's
                // DominantTypeCode aggregation and Threat[35] / Intent[20].
                byte damageTypeCode = (byte)info.DamageType;
                var sanitized = new SanitizedDamageInfo(
                    info.Damage,
                    info.HitPosition,
                    info.DamageDirection,
                    attackerId,
                    hasAttacker,
                    damageTypeCode);
                WorldEventBus.Publish(new DamageObserved(victimId, sanitized));
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge.OnTankDamage: " + ex.Message);
            }
        }

        // Engine signature: ManTechs.TankTeamChangedEvent fires Action<Tank, TeamChangeInfo>.
        // Verified against TankAIManager.cs:191. info.m_NewTeam carries the new team ID
        // (raw int). The old team is recovered from Smart's per-tech state — we know
        // what team WE thought the tech was on before this fired.
        private static void OnTankTeamChanged(Tank tank, ManTechs.TeamChangeInfo info)
        {
            if (tank == null) return;
            try
            {
                var techId = TechId.FromTank(tank);
                var newTeamId = new TeamId(info.m_NewTeam);

                // Smart's per-team registry must follow the team change so Coordinator
                // does not keep treating a captured tech as a friendly. Look up Smart's
                // per-tech state via the existing TankAIHelper accessor — no parallel
                // dictionary needed. AUDIT-R2 §2.R2.B + FIX-PLAN.md Phase 3.3.
                var helper = tank.GetComponent<TankAIHelper>();
                if (helper != null && helper.FormState is SmartPerTechState state)
                {
                    // We track this tech — its prior team is what Smart had on file.
                    WorldEventBus.Publish(new TechTeamChanged(techId, state.TeamId, newTeamId));
                    // Also rebind the World's BeliefState so threat-field hostile
                    // classification follows. Without this the world-side allegiance
                    // would stay stuck on the original team forever.
                    SmartRuntime.World?.UpdateTeam(state.TechId, newTeamId);
                    // L-043: atomic two-team migration under both RW locks (replaces the
                    // pre-L-043 Deregister + Update + Register sequence that left a window
                    // where the tech was in neither team's snapshot). On false, the new
                    // team refused or the old team had no entry — log + let routing reclaim.
                    var sourceTeamId = state.TeamId;
                    bool migrated = SmartRuntime.MigrateTech(sourceTeamId, newTeamId, state.TechId);
                    if (!migrated)
                    {
                        DebugTAC_AI.LogWarnFileOnly(
                            "smart-team-changed-migrate-failed-" + state.TechId.Value,
                            "SmartEventBridge.OnTankTeamChanged: MigrateTech failed (tech="
                            + state.TechId.Value + " from=" + sourceTeamId.Value
                            + " to=" + newTeamId.Value + ") — tech detached from oldTeam, unattached to newTeam");
                    }
                    else
                    {
                        // L-061: source team may have emptied; drop its per-team threat-field
                        // cache eagerly rather than waiting for the 30s reaper grace. The reaper
                        // still owns TeamRuntime disposal — this is the cache hint only.
                        var source = SmartRuntime.GetTeam(sourceTeamId);
                        if (source != null && source.TechCount == 0)
                        {
                            try { TAC_AI.AI.Forms.Smart.Pathing.PathingService.ForgetTeam(sourceTeamId); }
                            catch (System.Exception ex) { DebugTAC_AI.LogWarning("SmartEventBridge: ForgetTeam: " + ex.Message); }
                        }
                    }
                }
                else
                {
                    // Smart doesn't track this tech, but a Smart-team's belief might.
                    // Publish with default(TeamId) as oldTeam — consumers that care about
                    // the prior team are gated on Smart-tracked techs anyway.
                    WorldEventBus.Publish(new TechTeamChanged(techId, default(TeamId), newTeamId));
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartEventBridge.OnTankTeamChanged: " + ex.Message);
            }
        }
    }

    // Harmony shot-fired observation.
    //
    // The original Phase-3.3 patch targeted ModuleWeapon.Fire but that method DOES NOT EXIST
    // on ModuleWeapon in this TerraTech build (Unity 2018.4.13f1 / current Assembly-CSharp).
    // Direct reflection over Managed/Assembly-CSharp.dll confirmed:
    //   ModuleWeapon (base)            — no Fire method. Closest: AddRemoteShotFired, CanShoot.
    //   ModuleWeaponGun                — PrepareFiring(bool), ProcessFiring(bool) -> int,
    //                                    ReadyToFire(), FiringObstructed(). No Fire.
    //   ModuleWeaponFlamethrower       — same shape as Gun.
    //   ModuleWeaponTeslaCoil          — private Fire() — only class with a literal "Fire".
    //
    // The clean per-subclass hook is the int-returning ProcessFiring(bool firing). The
    // 710 / 733-byte IL bodies + int return value are consistent with "number of shots fired
    // this tick." Postfix-ing it and gating on __result > 0 filters out the per-frame no-fire
    // calls and publishes only when the weapon actually emitted ordnance. For TeslaCoil the
    // literal Fire() is the right hook (per-arc).
    //
    // Coverage: Gun + Flamethrower + TeslaCoil. Specialized weapons (mods adding new
    // ModuleWeapon subclasses with their own firing methods) are not covered; Smart misses
    // their shot events. Smart degrades gracefully when shot events are absent (threat
    // assessment falls back to damage-event observation), so the partial coverage is safe.

    [HarmonyPatch(typeof(ModuleWeaponGun))]
    [HarmonyPatch("ProcessFiring")]
    internal static class ModuleWeaponGun_ProcessFiring_Patch
    {
        private static void Postfix(ModuleWeaponGun __instance, int __result)
        {
            // __result > 0 indicates this tick produced shot output. Per-tick calls with
            // __result == 0 happen on every frame the weapon is active-but-not-firing
            // (cooldown / mid-burst gap); those must NOT publish or we'd flood the bus
            // with per-frame noise.
            if (__result <= 0) return;
            WeaponFirePublisher.PublishShotFired(__instance);
        }
    }

    [HarmonyPatch(typeof(ModuleWeaponFlamethrower))]
    [HarmonyPatch("ProcessFiring")]
    internal static class ModuleWeaponFlamethrower_ProcessFiring_Patch
    {
        private static void Postfix(ModuleWeaponFlamethrower __instance, int __result)
        {
            if (__result <= 0) return;
            WeaponFirePublisher.PublishShotFired(__instance);
        }
    }

    [HarmonyPatch(typeof(ModuleWeaponTeslaCoil))]
    [HarmonyPatch("Fire")]
    internal static class ModuleWeaponTeslaCoil_Fire_Patch
    {
        // TeslaCoil's Fire() is private void, called per arc activation. No return value
        // to gate on — every call corresponds to an actual fire event.
        private static void Postfix(ModuleWeaponTeslaCoil __instance)
        {
            WeaponFirePublisher.PublishShotFired(__instance);
        }
    }

    /// <summary>
    /// Shared shot-fired publisher. Takes any <see cref="Module"/>-derived weapon since the
    /// subclass-specific postfixes route here with the same shape. Pulls block/tank from the
    /// module and emits a ProjectileFired event on the WorldEventBus.
    /// </summary>
    internal static class WeaponFirePublisher
    {
        public static void PublishShotFired(Module weaponModule)
        {
            try
            {
                if (weaponModule == null) return;
                var block = weaponModule.block;
                if (block == null || block.tank == null) return;

                var techId = TechId.FromTank(block.tank);
                var weaponId = new WeaponId(block.GetInstanceID());
                Vector3 origin = block.transform.position;
                Vector3 dir = block.transform.forward;
                WorldEventBus.Publish(new ProjectileFired(techId, weaponId, origin, dir));
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.WeaponFirePublisher: " + ex.Message);
            }
        }
    }
}
