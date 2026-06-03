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
        public static event Action<string, int> QueueDepthSampled;
        public static event Action<string, TimeSpan> WorkerIdle;
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

        internal static void RaiseQueueDepthSampled(string queueName, int depth)
        {
            var handler = QueueDepthSampled;
            if (handler == null) return;
            try { handler(queueName, depth); } catch { }
        }

        internal static void RaiseWorkerIdle(string name, TimeSpan idleDuration)
        {
            var handler = WorkerIdle;
            if (handler == null) return;
            try { handler(name, idleDuration); } catch { }
        }

        internal static void RaiseRequestsDropped(string queueName, int count)
        {
            var handler = RequestsDropped;
            if (handler == null) return;
            try { handler(queueName, count); } catch { }
        }
    }
}
