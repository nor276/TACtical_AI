using System;
using System.Collections.Generic;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// Handle carrying a worker's identity, thread, and per-worker CTS so the
    /// registry can enumerate, cancel, and join them at DeInitGlobal.
    /// </summary>
    public sealed class WorkerHandle
    {
        public string Name { get; }
        public Thread Thread { get; }
        public CancellationTokenSource Cts { get; }

        public WorkerHandle(string name, Thread thread, CancellationTokenSource cts)
        {
            Name = name;
            Thread = thread;
            Cts = cts;
        }
    }

    /// <summary>
    /// Centralized tracker of all live worker threads across Smart. Per
    /// <see cref="THREADING-CONTRACT.md §7"/>. Workers register on construction and
    /// deregister on clean exit. <see cref="CancelAllAndJoin"/> is the entry point
    /// Smart's DeInitGlobal calls; it satisfies the
    /// <see cref="ARCHITECTURE.md §5 I3"/> invariant ("all workers terminate within
    /// DeInitGlobal").
    /// </summary>
    public static class WorkerLifecycleRegistry
    {
        private static readonly object _lock = new object();
        private static readonly List<WorkerHandle> _live = new List<WorkerHandle>();
        private static bool _cancelAllInProgress;

        /// <summary>
        /// L-002: read-only view of <see cref="_cancelAllInProgress"/>. Consumed by
        /// DaemonWatchdog (L-049) so it does not attempt respawn while shutdown is draining,
        /// and by WorkerHealthMonitor (L-047) which suppresses [SMART-WORKERS-DEAD] during
        /// teardown. Reads outside the lock are intentional — visibility is best-effort and
        /// the worst case is one extra respawn attempt that the underlying pool's IsRunning
        /// guard then rejects.
        /// </summary>
        public static bool IsTearingDown => _cancelAllInProgress;

        public static void Register(WorkerHandle worker)
        {
            if (worker == null) return;
            lock (_lock)
            {
                if (_cancelAllInProgress)
                {
                    // Late registration during teardown is an invariant violation
                    // per THREADING-CONTRACT §7.3.
                    DebugTAC_AI.LogError(
                        "Smart.Threading: worker '" + worker.Name +
                        "' attempted to register after CancelAllAndJoin began.");
                    return;
                }
                _live.Add(worker);
            }
        }

        public static void Deregister(WorkerHandle worker)
        {
            if (worker == null) return;
            lock (_lock)
            {
                _live.Remove(worker);
            }
        }

        // P11 T7 Item 63 reverted by L-020: SnapshotLive() re-added with named consumers
        // baked into the doc so it cannot be cleaned up again without breaking them.

        /// <summary>
        /// L-020: snapshot of every live worker handle. Returns a defensive copy under the
        /// lock so callers can enumerate without seeing partial Register/Deregister state.
        ///
        /// Canonical consumers (must remain wired):
        ///   - <c>WorkerHealthMonitor.Tick</c> (L-047): scans for missing canonical daemons.
        ///   - <c>SmartTestSuite.Threads_AllBackground_AfterInit</c> (L-005): locks the
        ///     IsBackground invariant so a future refactor cannot silently re-introduce the
        ///     pre-P11 shutdown freeze.
        ///
        /// Not for hot-path use — allocates per call. Watchdog calls it at 1Hz; tests call
        /// it once. Both are fine.
        /// </summary>
        public static IReadOnlyList<WorkerHandle> SnapshotLive()
        {
            lock (_lock)
            {
                return _live.ToArray();
            }
        }

        /// <summary>
        /// Smart's DeInitGlobal MUST call this. Cancels every live worker, joins each
        /// with a proportional share of <paramref name="timeout"/>, and reports any
        /// straggler. Per <see cref="THREADING-CONTRACT.md §7.2"/>, total timeout is
        /// bounded so a form swap cannot cause a UI hang.
        /// </summary>
        public static void CancelAllAndJoin(TimeSpan timeout)
        {
            WorkerHandle[] snapshot;
            lock (_lock)
            {
                _cancelAllInProgress = true;
                snapshot = _live.ToArray();
            }

            try
            {
                if (snapshot.Length == 0) return;

                // Phase 1: signal cancellation to every worker.
                foreach (var w in snapshot)
                {
                    try { w.Cts.Cancel(); }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarning(
                            "Smart.Threading: cancel failed for worker '" + w.Name + "': " + ex.Message);
                    }
                }

                // Phase 2: join each worker with a proportional share of the total timeout.
                var perWorkerMs = timeout.TotalMilliseconds / snapshot.Length;
                var perWorkerTimeout = perWorkerMs > 1.0
                    ? TimeSpan.FromMilliseconds(perWorkerMs)
                    : TimeSpan.FromMilliseconds(1);

                foreach (var w in snapshot)
                {
                    try
                    {
                        if (!w.Thread.Join(perWorkerTimeout))
                        {
                            DebugTAC_AI.LogError(
                                "Smart.Threading: worker '" + w.Name +
                                "' did not exit within its share of the DeInitGlobal timeout.");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarning(
                            "Smart.Threading: Join threw for worker '" + w.Name + "': " + ex.Message);
                    }
                }

                // Phase 3: verify registry is empty. Any straggler is an invariant violation.
                lock (_lock)
                {
                    if (_live.Count > 0)
                    {
                        foreach (var straggler in _live)
                        {
                            DebugTAC_AI.LogError(
                                "Smart.Threading: worker '" + straggler.Name +
                                "' still registered after CancelAllAndJoin.");
                        }
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _cancelAllInProgress = false;
                }
            }
        }
    }
}
