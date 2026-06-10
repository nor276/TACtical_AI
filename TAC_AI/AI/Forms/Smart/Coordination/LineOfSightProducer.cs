using System.Collections.Concurrent;
using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// P5 Item 23 (REV 2 RESHAPE): main-thread LOS-coverage producer.
    ///
    /// Original plan called for invocation from Coordinator.TickOnce, but that runs on
    /// the GlobalCoordinatorDaemon worker thread and Physics.Raycast is main-thread-only
    /// in Unity. REV 7 architecture:
    ///   - Producer (this class): MainThreadTick called from SmartForm.Operations, gated
    ///     by an Interlocked.CompareExchange interval (~100-200ms cadence) cloned from
    ///     the ObserveWorldTechsIfDue pattern.
    ///   - Storage: per-team DoubleBuffer&lt;Dictionary&lt;TechId, List&lt;TechId&gt;&gt;&gt;.
    ///     One ConcurrentDictionary keyed by TeamId.
    ///   - Consumer: TeamRuntime.BuildTeamSnapshot (worker side) reads via
    ///     <see cref="SnapshotForTeam"/> — lock-free Volatile.Read through DoubleBuffer.
    ///
    /// Round-robin observer cursor per team: each tick advances one observer per team
    /// and ComputeLOS scans all enemies for it. Cap at <see cref="MaxRaysPerFrame"/>
    /// total raycasts per tick across all teams (~enemy_count per observer).
    ///
    /// P11 post-flip default ON (<see cref="Enabled"/> = true). When toggled false,
    /// MainThreadTick is a no-op, snapshots stay empty, and TeamRuntime.BuildTeamSnapshot
    /// falls back to the REV 3 S1 per-tech empty sentinel — preserves v0.1 NeedsScout
    /// TRUE-always.
    /// </summary>
    public static class LineOfSightProducer
    {
        /// <summary>P11 post-flip default: ON. Tunable: <c>los.producerEnabled</c>.</summary>
        public static bool Enabled = true;

        /// <summary>Per-tick total raycast budget across all teams.</summary>
        public const int MaxRaysPerFrame = 64;

        /// <summary>~100-200ms cadence per the REV 7 plan; round to 150ms.</summary>
        public const int TickIntervalMs = 150;

        // Per-team published snapshots. ConcurrentDictionary because BuildTeamSnapshot
        // (worker thread) reads in parallel with this MainThreadTick (main thread).
        // Per-team DoubleBuffer gives lock-free reads via Volatile.Read.
        private static readonly ConcurrentDictionary<TeamId, DoubleBuffer<Dictionary<TechId, List<TechId>>>> _byTeam
            = new ConcurrentDictionary<TeamId, DoubleBuffer<Dictionary<TechId, List<TechId>>>>();

        // Round-robin cursor per team. Locked because MainThreadTick can be re-entered
        // through the interval-guard race window (CompareExchange + Operations
        // re-invocation); the lock cost is negligible at 150ms cadence.
        private static readonly Dictionary<TeamId, int> _observerCursor = new Dictionary<TeamId, int>();
        private static readonly object _cursorLock = new object();

        // Shared empty fallback returned by SnapshotForTeam when a team hasn't been
        // ticked yet. Worker-side BuildTeamSnapshot reads from this safely (it only
        // does TryGetValue, never enumerates).
        private static readonly Dictionary<TechId, List<TechId>> _emptyDict =
            new Dictionary<TechId, List<TechId>>();

        // Interval guard for the main-thread tick — mirrors SmartForm.cs:605-612 pattern.
        private static int _lastTickMs = int.MinValue;

        /// <summary>
        /// Worker-safe read of the most-recent published snapshot for <paramref name="team"/>.
        /// Returns an empty (but non-null) dict for teams that haven't been ticked yet.
        /// </summary>
        public static IReadOnlyDictionary<TechId, List<TechId>> SnapshotForTeam(TeamId team)
        {
            DoubleBuffer<Dictionary<TechId, List<TechId>>> buf;
            if (_byTeam.TryGetValue(team, out buf)) return buf.Read();
            return _emptyDict;
        }

        /// <summary>
        /// Drop all per-team snapshots — called from SmartRuntime.Shutdown to release
        /// references for GC, and to reset the producer for a fresh session. Idempotent.
        /// </summary>
        public static void Reset()
        {
            _byTeam.Clear();
            lock (_cursorLock) _observerCursor.Clear();
            System.Threading.Volatile.Write(ref _lastTickMs, int.MinValue);
        }

        /// <summary>
        /// Main-thread per-frame entry point. Gated by an Interlocked.CompareExchange
        /// interval (same pattern as <c>SmartForm.ObserveWorldTechsIfDue</c>) so only
        /// the first invocation per <see cref="TickIntervalMs"/> window does the work.
        /// </summary>
        public static void MainThreadTick()
        {
            if (!Enabled) return;
            if (ManTechs.inst == null) return;
            if (SmartRuntime.World == null) return;

            // Interval guard — exactly one caller per window wins.
            int now = System.Environment.TickCount;
            int last = System.Threading.Volatile.Read(ref _lastTickMs);
            if (last != int.MinValue && unchecked(now - last) >= 0 && unchecked(now - last) < TickIntervalMs) return;
            if (System.Threading.Interlocked.CompareExchange(ref _lastTickMs, now, last) != last) return;

            try
            {
                TickInternal();
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly(
                    "los-producer-tick",
                    "LineOfSightProducer.MainThreadTick threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void TickInternal()
        {
            // Group tanks by team in one pass.
            var teamRoster = new Dictionary<int, List<Tank>>();
            foreach (var tank in ManTechs.inst.IterateTechs())
            {
                if (tank == null) continue;
                List<Tank> roster;
                if (!teamRoster.TryGetValue(tank.Team, out roster))
                {
                    roster = new List<Tank>(8);
                    teamRoster[tank.Team] = roster;
                }
                roster.Add(tank);
            }
            if (teamRoster.Count <= 1) return;  // single-team world: no enemies, nothing to compute

            int raysLeft = MaxRaysPerFrame;
            foreach (var pair in teamRoster)
            {
                if (raysLeft <= 0) break;
                int rawTeam = pair.Key;
                var friendlies = pair.Value;
                if (friendlies.Count == 0) continue;
                var teamId = new TeamId(rawTeam);

                // Build enemy belief list: every tank NOT on this team. Read live beliefs
                // from WorldModel.GetPerTechBuffer so PositionMean is the freshest the
                // Smart subsystem has observed.
                var enemyBeliefs = new List<BeliefState>(8);
                foreach (var other in teamRoster)
                {
                    if (other.Key == rawTeam) continue;
                    for (int i = 0; i < other.Value.Count; i++)
                    {
                        var et = other.Value[i];
                        if (et == null) continue;
                        var eId = TechId.FromTank(et);
                        var bbuf = SmartRuntime.World.GetPerTechBuffer(eId);
                        if (bbuf == null) continue;
                        var bel = bbuf.Read();
                        if (bel != null) enemyBeliefs.Add(bel);
                    }
                }
                if (enemyBeliefs.Count == 0) continue;

                // Round-robin observer pick.
                int cursor;
                lock (_cursorLock)
                {
                    if (!_observerCursor.TryGetValue(teamId, out cursor)) cursor = 0;
                    _observerCursor[teamId] = cursor + 1;
                }
                int observerIdx = (int)((uint)cursor % (uint)friendlies.Count);
                var observerTank = friendlies[observerIdx];
                if (observerTank == null) continue;
                var observerTechId = TechId.FromTank(observerTank);

                // Spend rays. ComputeLOS does enemyBeliefs.Count raycasts via RaycastAll
                // (self-masked per the P5 prerequisite fix in LineOfSight.cs).
                raysLeft -= enemyBeliefs.Count;
                var seenList = LineOfSight.ComputeLOS(observerTank, enemyBeliefs);

                // Clone-write into team's DoubleBuffer (immutable-snapshot discipline).
                var buf = _byTeam.GetOrAdd(teamId,
                    _ => new DoubleBuffer<Dictionary<TechId, List<TechId>>>(
                        new Dictionary<TechId, List<TechId>>()));
                var prior = buf.Read();
                var next = new Dictionary<TechId, List<TechId>>(prior == null ? 4 : prior.Count + 1);
                if (prior != null)
                    foreach (var kv in prior) next[kv.Key] = kv.Value;
                next[observerTechId] = seenList;
                buf.Write(next);
            }
        }
    }
}
