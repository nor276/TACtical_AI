using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// Per-form-session worker pool. Owns its threads, distributes work via a shared
    /// queue, cooperates with <see cref="WorkerLifecycleRegistry"/> for shutdown.
    ///
    /// Implementation per <see cref="THREADING-CONTRACT.md §2"/>:
    ///   * Own Thread instances (not .NET ThreadPool — see §2.3 for rationale).
    ///   * Shared ConcurrentQueue&lt;T&gt; for v0.1.0; work-stealing upgrade path noted in §11.
    ///   * Workers register with the lifecycle registry at construction; deregister on exit.
    ///   * Worker error recovery per §6: retry budget N=3 per form session.
    ///
    /// Sizing per <see cref="FORM-SPECIFICATION.md §7 OD-4"/> and
    /// <see cref="ARCHITECTURE.md §2.1"/>: pool size is
    /// <c>Math.Max(2, Environment.ProcessorCount - 2)</c> when the caller passes
    /// <see cref="DefaultWorkerCount"/>. Explicit counts permitted for tests.
    ///
    /// Lifecycle: <see cref="Dispose"/> signals cancellation and joins all workers
    /// within a 2-second total timeout (per §7.2). Workers MUST yield to cancellation
    /// at known points; latency bound is 100ms p99 per §2.2.
    /// </summary>
    public sealed class WorkerPool : IDisposable
    {
        private const int RetryBudgetPerSession = 3;
        private const int IdleSpinIterations = 50;

        // Total timeout for joining all workers on Dispose. Matches WorkerLifecycleRegistry §7.2.
        private static readonly TimeSpan TotalShutdownTimeout = TimeSpan.FromSeconds(2);

        static WorkerPool()
        {
            ThreadingDiagnostics.InstallDefaultHandlers();
        }

        /// <summary>
        /// Default worker count per FORM-SPEC §7 OD-4 + ARCHITECTURE §2.1:
        /// <c>Math.Max(2, Environment.ProcessorCount - 2)</c>. The -2 reserves cores
        /// for the Unity main thread and renderer; the Max(2, …) keeps Smart
        /// parallelizable on dual-core systems. No hard upper cap (FORM-SPEC §5.4).
        /// </summary>
        public static int DefaultWorkerCount => Math.Max(2, Environment.ProcessorCount - 2);

        private readonly string _name;
        private readonly Thread[] _workers;
        private readonly WorkerHandle[] _handles;
        private readonly CancellationTokenSource _rootCts;
        private readonly ConcurrentQueue<Action<CancellationToken>> _queue;
        private bool _disposed;

        public string Name => _name;
        public int WorkerCount => _workers.Length;
        public bool IsRunning => !_disposed && !_rootCts.IsCancellationRequested;

        /// <param name="name">Identifies this pool in diagnostic events (e.g. "Smart").</param>
        /// <param name="workerCount">Number of workers; pass <see cref="DefaultWorkerCount"/> for the policy default.</param>
        /// <param name="external">Optional outer cancellation; cancelling it cancels the pool.</param>
        public WorkerPool(string name, int workerCount, CancellationToken external = default)
        {
            if (workerCount < 1)
                throw new ArgumentOutOfRangeException(nameof(workerCount), "WorkerPool needs at least one worker.");

            _name = name ?? "Smart";
            _queue = new ConcurrentQueue<Action<CancellationToken>>();
            _rootCts = CancellationTokenSource.CreateLinkedTokenSource(external);
            _workers = new Thread[workerCount];
            _handles = new WorkerHandle[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                var workerName = _name + "#" + i;
                var workerCts = CancellationTokenSource.CreateLinkedTokenSource(_rootCts.Token);
                var thread = new Thread(WorkerLoop)
                {
                    Name = "SmartWorker-" + workerName,
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,
                };
                var handle = new WorkerHandle(workerName, thread, workerCts);
                _handles[i] = handle;
                _workers[i] = thread;

                WorkerLifecycleRegistry.Register(handle);
                ThreadingDiagnostics.RaiseWorkerStarted(workerName);
                thread.Start(handle);
            }
        }

        /// <summary>
        /// Enqueue a unit of work. Never blocks. The work delegate receives the worker's
        /// cancellation token; per <see cref="THREADING-CONTRACT.md §3.1"/>, long-running
        /// work MUST check the token at known yield points.
        ///
        /// The underlying queue is unbounded; subsystems that need drop-oldest semantics
        /// wrap their requests in a <see cref="BoundedQueue{T}"/>.
        /// </summary>
        public void Enqueue(Action<CancellationToken> work)
        {
            if (work == null) return;
            if (_disposed) return;
            _queue.Enqueue(work);
        }

        /// <summary>
        /// Enqueue a long-running task on a DEDICATED thread instead of consuming a pool
        /// worker slot. Use for permanent RunLoop subsystems (PerceptionWorker, Planner,
        /// Coordinator, PathingService loops, TrainerWorkers). These tasks block their thread
        /// for the entire process lifetime - if they were enqueued via <see cref="Enqueue"/>,
        /// each would permanently capture a pool worker, and once N permanent loops exceed
        /// the pool's worker count, short-task work (per-tick RunMPCWork dispatches) would
        /// queue forever and never run. With 8+ teams each requiring 2 permanent workers
        /// (Planner + Coordinator), pool saturation is reached quickly in normal play -
        /// this manifested as "only 2 controllers ever produce non-zero throttle".
        ///
        /// Dedicated threads bypass the pool's worker count budget entirely. They still
        /// participate in <see cref="WorkerLifecycleRegistry"/> for clean shutdown.
        /// </summary>
        public void EnqueueLongRunning(Action<CancellationToken> work, string label = null)
        {
            if (work == null) return;
            if (_disposed) return;

            string threadName = "SmartLR-" + (label ?? "anon") + "-" + _longRunningCounter;
            System.Threading.Interlocked.Increment(ref _longRunningCounter);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_rootCts.Token);
            var thread = new Thread(_ =>
            {
                try
                {
                    work(cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (System.Threading.ThreadAbortException)
                {
                    try { System.Threading.Thread.ResetAbort(); } catch { }
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.WorkerPool: long-running '" + threadName
                        + "' threw " + ex.GetType().Name + ": " + ex.Message);
                }
            })
            {
                Name = threadName,
                IsBackground = true,
                Priority = ThreadPriority.Normal,
            };
            var handle = new WorkerHandle(threadName, thread, cts);
            WorkerLifecycleRegistry.Register(handle);
            ThreadingDiagnostics.RaiseWorkerStarted(threadName);
            thread.Start();
        }
        private int _longRunningCounter;

        private void WorkerLoop(object state)
        {
            var handle = (WorkerHandle)state;
            var token = handle.Cts.Token;
            int retryCount = 0;
            bool exitedClean = false;
            TerminationReason exitReason = TerminationReason.UnexpectedExit;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_queue.TryDequeue(out var work))
                    {
                        try
                        {
                            work(token);
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected during shutdown; loop ends on the next IsCancellationRequested check.
                        }
                        catch (System.Threading.ThreadAbortException)
                        {
                            // External Thread.Abort (observed on Unity 2018.4 / Mono around the
                            // OnApplicationPause path — FMOD's PauseManual coincides with the
                            // abort cascade in output_log.txt). ThreadAbortException is special:
                            // the CLR auto-rethrows it at the end of any catch unless
                            // ResetAbort is called, so a generic catch (Exception) would log
                            // "Retry 1/3" and then immediately terminate the worker — which is
                            // exactly the UnexpectedExit cascade observed across all 22 workers.
                            //
                            // Per THREADING-CONTRACT §6.1 intent ("retry budget reserved for
                            // unexpected failures"), an external abort is NOT a worker fault;
                            // it is a transient pause-path signal. Reset the abort, log
                            // informatively, do NOT count against the retry budget, continue.
                            //
                            // THREADING-CONTRACT gap: §3 / §6.1 don't explicitly address
                            // ThreadAbortException. This handler implements the intent; the
                            // spec needs a normative entry to make the policy explicit.
                            try { System.Threading.Thread.ResetAbort(); }
                            catch { /* may fail if abort source is unusual; not fatal */ }
                            DebugTAC_AI.Log("Smart.Threading: worker '" + handle.Name
                                + "' absorbed ThreadAbortException (external pause); continuing.");
                        }
                        catch (Exception ex)
                        {
                            retryCount++;
                            ThreadingDiagnostics.RaiseWorkerException(handle.Name, ex, retryCount);

                            if (retryCount >= RetryBudgetPerSession)
                            {
                                exitReason = TerminationReason.RetryBudgetExhausted;
                                return; // permanent termination
                            }
                            // Otherwise: continue; the loop attempts the next dequeue.
                        }
                    }
                    else
                    {
                        // Phase 2.1 (FIX-PLAN.md): the previous idle branch did a SECOND
                        // _queue.TryDequeue(out _) which removed any work item that
                        // landed during SpinWait but silently DISCARDED it (out _). Under
                        // contention this dropped MPC requests, path requests, and
                        // marshalled compute lambdas with no diagnostic. R2 1.R2-G.
                        //
                        // Fix: SpinWait to absorb short idle gaps, then Sleep(0) to yield.
                        // Any work item that landed during SpinWait is picked up by the
                        // NEXT outer-loop TryDequeue at line 119, not destructively
                        // re-tested here. Zero work-loss under contention.
                        Thread.SpinWait(IdleSpinIterations);
                        Thread.Sleep(0);
                    }
                }

                exitedClean = true;
                exitReason = TerminationReason.Clean;
            }
            finally
            {
                WorkerLifecycleRegistry.Deregister(handle);
                ThreadingDiagnostics.RaiseWorkerTerminated(
                    handle.Name,
                    exitedClean ? TerminationReason.Clean : exitReason);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Phase 1: signal cancellation to every worker (their tokens are linked to _rootCts).
            try { _rootCts.Cancel(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Threading: pool '" + _name + "' Cancel threw: " + ex.Message);
            }

            // Phase 2: join workers within the total shutdown timeout, share proportionally.
            var perWorkerMs = TotalShutdownTimeout.TotalMilliseconds / _workers.Length;
            var perWorkerTimeout = perWorkerMs > 1.0
                ? TimeSpan.FromMilliseconds(perWorkerMs)
                : TimeSpan.FromMilliseconds(1);

            for (int i = 0; i < _workers.Length; i++)
            {
                try
                {
                    if (!_workers[i].Join(perWorkerTimeout))
                    {
                        DebugTAC_AI.LogError(
                            "Smart.Threading: pool '" + _name + "' worker " + i +
                            " did not exit within its share of Dispose timeout.");
                    }
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning(
                        "Smart.Threading: Join threw for pool '" + _name + "' worker " + i + ": " + ex.Message);
                }
            }

            // Phase 3: dispose CTS hierarchy.
            for (int i = 0; i < _handles.Length; i++)
            {
                try { _handles[i].Cts.Dispose(); } catch { }
            }
            try { _rootCts.Dispose(); } catch { }
        }
    }
}
