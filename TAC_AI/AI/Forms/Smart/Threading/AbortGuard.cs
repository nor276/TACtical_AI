using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// L-001: centralised handler for Unity's `Thread.Abort` cascade on FMOD pause.
    ///
    /// `Absorb` is called from any worker/daemon catch (ThreadAbortException) site. It calls
    /// ResetAbort (idempotent, swallows its own throws), counts the abort in a per-worker
    /// rolling window, and returns:
    ///   - Continue: caller should re-enter its loop body.
    ///   - ExitForRespawn: storm threshold tripped; caller should break its loop and exit
    ///     the thread cleanly with TerminationReason.AbortStorm. DaemonWatchdog respawns.
    ///
    /// Storm threshold: 8 absorbs within 10s. Unity's pause delivers one abort per worker
    /// per pause event; 8 in 10s requires 8 distinct pause storms, well above natural
    /// alt-tab cadence.
    ///
    /// Per-worker state lives in a ConcurrentDictionary keyed by worker name. State is
    /// allocated lazily on first absorb (cheap dictionary GetOrAdd path) and never freed
    /// — sessions are short, worker name count is bounded, and clearing would race
    /// concurrent Absorb calls.
    ///
    /// Contract: never throws. Any internal exception is logged via DebugTAC_AI and the
    /// method returns Continue (safer than terminating a worker on a guard bug).
    /// </summary>
    public static class AbortGuard
    {
        public enum AbortAction
        {
            Continue,
            ExitForRespawn,
        }

        // Storm threshold: N absorbs within window resets to ExitForRespawn.
        public const int StormThresholdAborts = 8;
        public const int StormWindowMs = 10000;

        private sealed class WorkerState
        {
            // Rolling-window timestamps (Environment.TickCount, ms). Single-writer per worker;
            // the writer is the worker thread itself in its catch handler.
            public readonly long[] AbortTimestamps = new long[StormThresholdAborts];
            public int Head;
            public long TotalAbsorbsObserved;
        }

        private static readonly ConcurrentDictionary<string, WorkerState> _byWorker
            = new ConcurrentDictionary<string, WorkerState>();

        /// <summary>
        /// Absorb a ThreadAbortException. Call from inside a catch(ThreadAbortException).
        /// Safe to call from any worker thread. Never throws.
        /// </summary>
        public static AbortAction Absorb(string workerName)
        {
            try
            {
                // Reset first — the rest of the work doesn't matter if reset fails.
                try { Thread.ResetAbort(); }
                catch { /* ResetAbort can throw on unusual abort paths; not fatal */ }

                if (string.IsNullOrEmpty(workerName)) workerName = "<anon>";

                var st = _byWorker.GetOrAdd(workerName, _ => new WorkerState());
                long now = Environment.TickCount;
                st.AbortTimestamps[st.Head] = now;
                st.Head = (st.Head + 1) % StormThresholdAborts;
                st.TotalAbsorbsObserved++;

                // Storm check: oldest entry in the ring is N positions back; if it's within
                // the window we have N aborts in <= window.
                long oldest = st.AbortTimestamps[st.Head]; // Head now points at the oldest
                if (oldest != 0 && unchecked(now - oldest) >= 0 && unchecked(now - oldest) < StormWindowMs)
                {
                    DebugTAC_AI.LogWarnFileOnly("worker-abort-storm-" + workerName,
                        "[WORKER-ABORT-STORM] worker='" + workerName + "' "
                        + StormThresholdAborts + " aborts within "
                        + (now - oldest) + "ms — requesting respawn");
                    return AbortAction.ExitForRespawn;
                }

                DebugTAC_AI.LogWarnFileOnly("worker-abort-absorbed-" + workerName,
                    "[WORKER-ABORT-ABSORBED] worker='" + workerName + "' "
                    + "totalAbsorbsObserved=" + st.TotalAbsorbsObserved);
                return AbortAction.Continue;
            }
            catch (Exception ex)
            {
                try
                {
                    DebugTAC_AI.LogWarnFileOnly("worker-abort-guard-bug",
                        "[WORKER-ABORT-GUARD-BUG] worker='" + (workerName ?? "<null>") + "' "
                        + "guard threw " + ex.GetType().Name + ": " + ex.Message + " — defaulting to Continue");
                }
                catch { /* even logging failed; swallow */ }
                return AbortAction.Continue;
            }
        }

        /// <summary>
        /// Diagnostic: total absorbs observed across this worker's lifetime. Used by tests
        /// + the worker-watchdog status surface.
        /// </summary>
        public static long GetAbsorbsObserved(string workerName)
        {
            if (string.IsNullOrEmpty(workerName)) return 0L;
            return _byWorker.TryGetValue(workerName, out var st) ? st.TotalAbsorbsObserved : 0L;
        }

        /// <summary>SMART_DEV test hook: drop all per-worker state for a fresh run.</summary>
        public static void ResetAllState()
        {
            _byWorker.Clear();
        }
    }
}
