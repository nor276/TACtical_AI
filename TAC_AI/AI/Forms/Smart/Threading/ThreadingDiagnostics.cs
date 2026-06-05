using System;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// Reasons a worker terminated. Carried in <see cref="ThreadingDiagnostics.WorkerTerminated"/>.
    /// </summary>
    public enum TerminationReason
    {
        /// <summary>Worker exited because its cancellation token fired (normal shutdown).</summary>
        Clean,
        /// <summary>Worker exceeded its per-session retry budget after repeated exceptions.</summary>
        RetryBudgetExhausted,
        /// <summary>Worker exited unexpectedly (bug in worker loop). Should not happen.</summary>
        UnexpectedExit,
        /// <summary>L-003: worker exited after AbortGuard tripped storm threshold (8/10s). DaemonWatchdog respawns.</summary>
        AbortStorm,
    }

    /// <summary>
    /// Threading's diagnostic event surface. Per <see cref="THREADING-CONTRACT.md §10"/>:
    /// Threading does NOT log these events directly. The Diagnostics subsystem subscribes
    /// when authored (workflow step 1.10-ish, opportunistic); until then, default handlers
    /// (registered at <see cref="InstallDefaultHandlers"/>) route to <see cref="DebugTAC_AI"/>.
    ///
    /// Subscriber exceptions are caught at the dispatch boundary so one bad subscriber
    /// does not break the others (per ARCHITECTURE §4 E4 pattern).
    /// </summary>
    public static class ThreadingDiagnostics
    {
        public static event Action<string> WorkerStarted;
        public static event Action<string, TerminationReason> WorkerTerminated;
        public static event Action<string, Exception, int> WorkerException;
        // P11 T7 Item 63: QueueDepthSampled + WorkerIdle deleted — no producer ever raised
        // them (verified by global grep on RaiseQueueDepth / RaiseWorkerIdle). Reintroduce
        // with their Raise* helpers when a real producer materializes.
        public static event Action<string, int> RequestsDropped;

        private static bool _defaultHandlersInstalled;

        /// <summary>
        /// Install default handlers that route events through DebugTAC_AI logging.
        /// Idempotent. Called automatically by WorkerPool's static ctor; safe to call
        /// from Smart.InitGlobal as well.
        /// </summary>
        public static void InstallDefaultHandlers()
        {
            if (_defaultHandlersInstalled) return;
            _defaultHandlersInstalled = true;

            WorkerStarted += name =>
                DebugTAC_AI.Log("Smart.Threading: worker '" + name + "' started.");

            WorkerTerminated += (name, reason) =>
            {
                if (reason == TerminationReason.Clean)
                    DebugTAC_AI.Log("Smart.Threading: worker '" + name + "' exited cleanly.");
                else if (reason == TerminationReason.AbortStorm)
                    // L-003: AbortStorm is recoverable (DaemonWatchdog will respawn). File-only,
                    // distinct tag so the watchdog log + this terminate log correlate.
                    DebugTAC_AI.LogWarnFileOnly("worker-abort-storm-exit-" + name,
                        "[WORKER-ABORT-STORM] worker '" + name + "' exited for respawn (storm threshold tripped)");
                else
                    DebugTAC_AI.LogError("Smart.Threading: worker '" + name + "' terminated: " + reason);
            };

            WorkerException += (name, ex, retryCount) =>
                DebugTAC_AI.LogWarning("Smart.Threading: worker '" + name + "' threw " +
                    ex.GetType().Name + ": " + ex.Message + ". Retry " + retryCount + "/3.\n" + ex.StackTrace);

            RequestsDropped += (queueName, count) =>
            {
                // Avoid log spam: only log if the name is non-empty (caller opted-in to telemetry).
                if (!string.IsNullOrEmpty(queueName))
                    DebugTAC_AI.LogWarning("Smart.Threading: queue '" + queueName + "' dropped " + count + " request(s).");
            };
        }

        // --- raise helpers; internal so only Threading-internal callers fire events ---

        internal static void RaiseWorkerStarted(string name)
        {
            var handler = WorkerStarted;
            if (handler == null) return;
            try { handler(name); } catch { /* per ARCHITECTURE §4 E4 */ }
        }

        internal static void RaiseWorkerTerminated(string name, TerminationReason reason)
        {
            var handler = WorkerTerminated;
            if (handler == null) return;
            try { handler(name, reason); } catch { }
        }

        internal static void RaiseWorkerException(string name, Exception ex, int retryCount)
        {
            var handler = WorkerException;
            if (handler == null) return;
            try { handler(name, ex, retryCount); } catch { }
        }

        // P11 T7 Item 63: RaiseQueueDepthSampled + RaiseWorkerIdle deleted alongside
        // their never-raised events.

        internal static void RaiseRequestsDropped(string queueName, int count)
        {
            var handler = RequestsDropped;
            if (handler == null) return;
            try { handler(queueName, count); } catch { }
        }
    }
}
