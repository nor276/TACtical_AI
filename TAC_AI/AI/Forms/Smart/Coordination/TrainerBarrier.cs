using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// L-031: CountdownEvent registry that lets <see cref="HostAuthorityCoordinator"/>
    /// (L-030) wait until every trainer has parked at its FSM Paused state before
    /// snapshotting model parameters for a host-loss save (L-070).
    ///
    /// Each TrainerWorker registers a barrier at Init via <see cref="Register"/>; on
    /// HostChanged(IsHost=false) the trainer transitions to Pausing, drains its in-flight
    /// minibatch, transitions to Paused, then signals its barrier via <see cref="Signal"/>.
    /// The coordinator calls <see cref="WaitAll"/> with a timeout; if any barrier doesn't
    /// signal in time we log <c>[HOSTAUTH-PAUSE-TIMEOUT]</c> and proceed anyway (a stuck
    /// trainer is a watchdog concern, not a save concern).
    ///
    /// Re-entrant across host transitions: <see cref="Reset"/> rearms all barriers for
    /// the next IsHost=true→false cycle.
    /// </summary>
    public static class TrainerBarrier
    {
        private static readonly ConcurrentDictionary<string, CountdownEvent> _barriers
            = new ConcurrentDictionary<string, CountdownEvent>();

        /// <summary>
        /// Register a trainer's barrier. Called at TrainerWorker construction.
        /// Idempotent — re-registering the same name overwrites the prior CountdownEvent.
        /// </summary>
        public static void Register(string trainerName)
        {
            if (string.IsNullOrEmpty(trainerName)) return;
            _barriers.AddOrUpdate(trainerName,
                addValueFactory: _ => new CountdownEvent(1),
                updateValueFactory: (_, prev) =>
                {
                    try { prev.Dispose(); } catch { }
                    return new CountdownEvent(1);
                });
        }

        /// <summary>Trainer reports it has reached Paused state.</summary>
        public static void Signal(string trainerName)
        {
            if (string.IsNullOrEmpty(trainerName)) return;
            if (_barriers.TryGetValue(trainerName, out var ce))
            {
                try { if (!ce.IsSet) ce.Signal(); } catch { /* may race Reset */ }
            }
        }

        /// <summary>
        /// Block (with timeout) until every registered barrier signals. Returns true when
        /// all barriers signaled before the timeout; false if any timed out.
        /// </summary>
        public static bool WaitAll(int timeoutMs)
        {
            int deadlineMs = unchecked(Environment.TickCount + timeoutMs);
            bool allSignaled = true;
            foreach (var kv in _barriers)
            {
                int remainingMs = unchecked(deadlineMs - Environment.TickCount);
                if (remainingMs <= 0)
                {
                    if (!kv.Value.IsSet)
                    {
                        DebugTAC_AI.LogWarnFileOnly("hostauth-pause-timeout-" + kv.Key,
                            "[HOSTAUTH-PAUSE-TIMEOUT] trainer='" + kv.Key + "' did not pause within "
                            + timeoutMs + "ms — proceeding with stale state");
                        allSignaled = false;
                    }
                    continue;
                }
                try
                {
                    if (!kv.Value.Wait(remainingMs))
                    {
                        DebugTAC_AI.LogWarnFileOnly("hostauth-pause-timeout-" + kv.Key,
                            "[HOSTAUTH-PAUSE-TIMEOUT] trainer='" + kv.Key + "' did not pause within "
                            + timeoutMs + "ms — proceeding with stale state");
                        allSignaled = false;
                    }
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("TrainerBarrier.WaitAll: " + kv.Key + " " + ex.Message);
                    allSignaled = false;
                }
            }
            return allSignaled;
        }

        /// <summary>Re-arm every barrier for the next pause cycle. Called after WaitAll.</summary>
        public static void Reset()
        {
            foreach (var kv in _barriers)
            {
                try
                {
                    if (kv.Value.IsSet) kv.Value.Reset();
                }
                catch { /* swallow */ }
            }
        }

        /// <summary>Hard reset — Dispose every barrier + clear. Used by SmartRuntime.Shutdown.</summary>
        public static void ClearAll()
        {
            foreach (var kv in _barriers)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _barriers.Clear();
        }

        public static int RegisteredCount => _barriers.Count;

        // ---- Director pause-token (Phase 1, hardened per §7.3 R3-M6) ----
        // Mutual-exclusion lock so host-change racing a rollback serialises. Only
        // one owner may hold the token at a time; the same owner string releases.
        // Director.BeginShutdown MUST NOT acquire — it signals via _killSwitch and
        // waits for the in-flight rollback's finally to release.

        private static string _pausingOwner;
        private static long _pauseAcquiredMonoTicks;
        private static readonly object _ownerLock = new object();
        public const double StuckTokenSeconds = 30.0;

        /// <summary>Block until the token is free, then claim it for <paramref name="owner"/>.</summary>
        public static void AcquirePauseToken(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return;
            lock (_ownerLock)
            {
                while (_pausingOwner != null)
                {
                    try { System.Threading.Monitor.Wait(_ownerLock, 1000); }
                    catch (System.Exception ex)
                    {
                        DebugTAC_AI.LogWarning("TrainerBarrier.AcquirePauseToken: " + ex.Message);
                        return;
                    }
                }
                _pausingOwner = owner;
                _pauseAcquiredMonoTicks = TAC_AI.AI.Forms.Smart.World.MonoClock.Now();
            }
        }

        /// <summary>Release the token if <paramref name="owner"/> currently holds it.</summary>
        public static void ReleasePauseToken(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return;
            lock (_ownerLock)
            {
                if (_pausingOwner == owner)
                {
                    _pausingOwner = null;
                    _pauseAcquiredMonoTicks = 0;
                    try { System.Threading.Monitor.PulseAll(_ownerLock); }
                    catch { /* defensive */ }
                }
            }
        }

        public static string CurrentPauseOwner
        {
            get { lock (_ownerLock) { return _pausingOwner; } }
        }

        /// <summary>
        /// Stuck-token watchdog. Called from TrainingDirector's 1 Hz loop. Force-release
        /// after 30 s if no rollback is in flight, then journal the leak. Director's
        /// own shutdown path does NOT acquire the token; it just signals and waits.
        /// </summary>
        public static void TickPauseTokenWatchdog()
        {
            string ownerSnapshot;
            long acquiredSnapshot;
            lock (_ownerLock)
            {
                ownerSnapshot = _pausingOwner;
                acquiredSnapshot = _pauseAcquiredMonoTicks;
            }
            if (ownerSnapshot == null) return;
            // In Phase 1 there is no rollback; this is always 0.
            if (Director.TrainingDirector.RollbackInProgress != 0) return;
            long now = TAC_AI.AI.Forms.Smart.World.MonoClock.Now();
            double held = (now - acquiredSnapshot) * TAC_AI.AI.Forms.Smart.World.MonoClock.TickFreq;
            if (held < StuckTokenSeconds) return;

            string leaked;
            lock (_ownerLock)
            {
                if (_pausingOwner == ownerSnapshot)
                {
                    leaked = _pausingOwner;
                    _pausingOwner = null;
                    _pauseAcquiredMonoTicks = 0;
                    try { System.Threading.Monitor.PulseAll(_ownerLock); }
                    catch { /* defensive */ }
                }
                else
                {
                    return; // owner changed under us
                }
            }
            DebugTAC_AI.LogWarnFileOnly("trainerbarrier-pausetoken-leaked-" + leaked,
                "[PAUSETOKEN-LEAKED owner=" + leaked + "] held " + held.ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture) + "s — force-released");
            Director.DirectorJournal.Append("PAUSETOKEN-LEAKED",
                "owner=" + leaked + " heldSec=" + held.ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
