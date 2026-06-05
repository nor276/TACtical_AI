using System;
using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Pathing;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// L-044: long-running daemon that walks SmartRuntime's TeamRuntime map at 1Hz and
    /// retires empty teams. Two-stage retirement so a team that briefly empties (engine
    /// recycle window, tech switching teams) doesn't get reaped before it's actually idle:
    ///
    ///   1. Team observed with TechCount=0 for ≥ <see cref="DrainingGraceSeconds"/>:
    ///      TryTransitionTo(Draining). The team is now refusing new registrations (L-019
    ///      RegisterTech returns false), but its TeamRuntime / RW-lock kernel handle stay.
    ///   2. Draining team observed empty for an additional ≥ <see cref="DisposeGraceSeconds"/>:
    ///      TryTransitionTo(Disposed) + team.Dispose() + SmartRuntime.TryRemoveTeam +
    ///      PathingService.ForgetTeam(teamId) so the per-team threat-field DoubleBuffer
    ///      releases.
    ///
    /// Anything that bumps TechCount &gt; 0 (e.g. a tank changes back to this team) resets
    /// the team's first-empty timestamp; if it's already Draining, the team is stuck
    /// Draining forever (one-way FSM per L-019). The plan accepts that — a Draining team
    /// signals "engine should not send techs here anymore"; a re-population is a sign
    /// something went wrong upstream.
    ///
    /// The reaper enqueues via WorkerPool.EnqueueLongRunning with label "TeamReaper" so
    /// WorkerHealthMonitor (L-047) can register a respawn factory if desired.
    /// </summary>
    public static class TeamReaperDaemon
    {
        // Grace windows. Plan §3.9 calls these "tunable in AetherTuning (L-064)"; until
        // L-064 wires them as live tunables, the defaults live here.
        public const float DrainingGraceSeconds = 30f;
        public const float DisposeGraceSeconds = 30f;
        public const int CyclePeriodMs = 1000;

        // Per-team first-empty stamps (Environment.TickCount ms). Cleared when TechCount > 0.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int>
            _firstEmptyTickMs = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        // Per-team Draining-entered stamps for the Disposed transition.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int>
            _drainingEnteredTickMs = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();

        // Lifetime counters surfaced by SmartForm GUI (L-065) + smart.runtime.status.
        private static long _teamsEvicted;
        private static long _teamsCreated;   // sampled at cycle start via SmartRuntime.TeamCount delta
        private static int _lastObservedTeamCount;

        public static long TeamsEvictedTotal => System.Threading.Interlocked.Read(ref _teamsEvicted);
        public static long TeamsCreatedObserved => System.Threading.Interlocked.Read(ref _teamsCreated);

        /// <summary>
        /// Enqueue the reaper on the pool. Called from SmartRuntime.Init. Returns the
        /// thread name (matches the WorkerPool.EnqueueLongRunning L-021 signature).
        /// Idempotent at the SmartRuntime layer — Init guards.
        /// </summary>
        public static string Enqueue(TAC_AI.AI.Forms.Smart.Threading.WorkerPool pool)
        {
            if (pool == null) return null;
            return pool.EnqueueLongRunning(RunLoop, "TeamReaper");
        }

        /// <summary>SMART_DEV / Reset hook. Clears per-team grace stamps.</summary>
        public static void Reset()
        {
            _firstEmptyTickMs.Clear();
            _drainingEnteredTickMs.Clear();
            System.Threading.Interlocked.Exchange(ref _teamsEvicted, 0L);
            System.Threading.Interlocked.Exchange(ref _teamsCreated, 0L);
            _lastObservedTeamCount = 0;
        }

        private static void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                // L-026: pause-gate. Reaper is NOT host-gated — every host should sweep its
                // own teams regardless of MP authority (the team registry is per-process).
                if (SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
                    continue;
                }

                try
                {
                    Sweep();
                }
                catch (OperationCanceledException) { return; }
                catch (System.Threading.ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("TeamReaper")
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (System.Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.TeamReaperDaemon: " + ex.GetType().Name + ": " + ex.Message);
                }

                if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
            }
        }

        private static void Sweep()
        {
            int now = System.Environment.TickCount;
            // L-064: read live tunables so the operator can adjust grace at runtime
            // without restart. Defaults match DrainingGraceSeconds / DisposeGraceSeconds
            // const baseline.
            int drainGraceMs = (int)(TAC_AI.AI.Forms.Smart.World.AetherTuning.TeamReaperDrainingGraceSeconds * 1000f);
            int disposeGraceMs = (int)(TAC_AI.AI.Forms.Smart.World.AetherTuning.TeamReaperDisposeGraceSeconds * 1000f);

            int observedCount = 0;
            // Snapshot to a list because we may mutate _teams via TryRemoveTeam.
            var snapshot = new List<TeamRuntimeAccess>();
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                observedCount++;
                snapshot.Add(new TeamRuntimeAccess
                {
                    Team = team,
                    TeamIdInt = team.TeamId.Value,
                    State = team.State,
                    TechCount = team.TechCount,
                });
            }

            // Detect newly-created teams since last sweep (only-grows counter).
            int prevCount = _lastObservedTeamCount;
            int created = System.Math.Max(0, observedCount - prevCount);
            if (created > 0) System.Threading.Interlocked.Add(ref _teamsCreated, created);
            _lastObservedTeamCount = observedCount;

            for (int i = 0; i < snapshot.Count; i++)
            {
                var s = snapshot[i];
                int key = s.TeamIdInt;

                if (s.TechCount > 0)
                {
                    // Team is populated — clear empty stamp (if any).
                    _firstEmptyTickMs.TryRemove(key, out _);
                    // Note: we do NOT clear _drainingEnteredTickMs because Draining→Active
                    // is forbidden by L-019 (one-way FSM). A Draining team with TechCount>0
                    // is in a weird state; leave the stamp so it still gets disposed eventually.
                    continue;
                }

                // TechCount == 0. Stamp first-observed-empty if not already.
                int firstEmpty = _firstEmptyTickMs.GetOrAdd(key, now);
                int emptyAge = unchecked(now - firstEmpty);

                if (s.State == TeamLifecycleState.Active)
                {
                    if (emptyAge >= drainGraceMs)
                    {
                        // Transition Active→Draining. TryTransitionTo logs the event.
                        if (s.Team.TryTransitionTo(TeamLifecycleState.Draining))
                        {
                            _drainingEnteredTickMs[key] = now;
                        }
                    }
                }
                else if (s.State == TeamLifecycleState.Draining)
                {
                    int drainingSince = _drainingEnteredTickMs.GetOrAdd(key, now);
                    int drainingAge = unchecked(now - drainingSince);
                    if (drainingAge >= disposeGraceMs)
                    {
                        // Transition Draining→Disposed + Dispose + remove + ForgetTeam.
                        if (s.Team.TryTransitionTo(TeamLifecycleState.Disposed))
                        {
                            try
                            {
                                // L-044 invariant: ForgetTeam BEFORE TryRemove so the
                                // per-team threat-field DoubleBuffer releases (PathingService
                                // can no longer find the team to clean it afterwards). L-061
                                // exposes the ForgetTeam API.
                                PathingService.ForgetTeam(s.Team.TeamId);
                            }
                            catch (System.Exception ex)
                            {
                                DebugTAC_AI.LogWarning("TeamReaperDaemon: ForgetTeam failed for team "
                                    + key + ": " + ex.Message);
                            }
                            try { s.Team.Dispose(); }
                            catch (System.Exception ex)
                            {
                                DebugTAC_AI.LogWarning("TeamReaperDaemon: Dispose failed for team "
                                    + key + ": " + ex.Message);
                            }
                            SmartRuntime.TryRemoveTeam(s.Team.TeamId);
                            _firstEmptyTickMs.TryRemove(key, out _);
                            _drainingEnteredTickMs.TryRemove(key, out _);
                            System.Threading.Interlocked.Increment(ref _teamsEvicted);
                            DebugTAC_AI.LogWarnFileOnly("team-lifecycle-evicted-" + key,
                                "[TEAM-LIFECYCLE] team=" + key + " state=Disposed evicted (drainingAge=" + drainingAge + "ms)");
                        }
                    }
                }
                // Disposed shouldn't appear in snapshot (TryRemoveTeam already ran) but be safe.
            }
        }

        // Tiny snapshot record so we don't hold references to TeamRuntime across the sweep
        // (the iteration itself yields collection-style ConcurrentDictionary semantics).
        private struct TeamRuntimeAccess
        {
            public TeamRuntime Team;
            public int TeamIdInt;
            public TeamLifecycleState State;
            public int TechCount;
        }
    }
}
