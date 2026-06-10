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
        /// Default worker count per FORM-SPEC §7 OD-4 + ARCHITECTURE §2.1.
        /// P9 Item 29 (REV 7): delegates to <see cref="WorkerPoolSizing.Resolve"/>. The
        /// policy default is <c>Math.Max(2, Environment.ProcessorCount - 2)</c>; a runtime
        /// <see cref="WorkerPoolSizing.Override"/> &gt; 0 lets test harnesses pin a count.
        /// No hard upper cap (FORM-SPEC §5.4).
        /// </summary>
        public static int DefaultWorkerCount => WorkerPoolSizing.Resolve();

        private readonly string _name;
        private readonly Thread[] _workers;
        private readonly WorkerHandle[] _handles;
        private readonly CancellationTokenSource _rootCts;
        private readonly ConcurrentQueue<Action<CancellationToken>> _queue;
        // Idle workers BLOCK on this instead of spinning. Enqueue Sets it to wake a worker;
        // the timeout is a safety re-poll so no item ever sits longer than IdleWaitMs.
        private readonly System.Threading.AutoResetEvent _workSignal = new System.Threading.AutoResetEvent(false);
        private const int IdleWaitMs = 5;
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
                // L-005: lock the IsBackground=true invariant. Anchored by SMART_DEV test
                // Threads_AllBackground_AfterInit in SmartTestSuite (enumerates registry,
                // asserts every WorkerHandle.Thread.IsBackground == true). The historical
                // shutdown freeze re-emerges the moment this line is dropped.
                System.Diagnostics.Debug.Assert(thread.IsBackground, "WorkerPool worker must be IsBackground=true");
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
            try { _workSignal.Set(); } catch { /* disposed mid-shutdown */ }
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
        public string EnqueueLongRunning(Action<CancellationToken> work, string label = null)
        {
            // L-021: return thread name so callers (WorkerHealthMonitor registration in L-066,
            // DaemonWatchdog roster wiring in L-049) can correlate enqueue-time identity with
            // the WorkerLifecycleRegistry handle. null return signals "not started" (disposed
            // or null work).
            if (work == null) return null;
            if (_disposed) return null;

            string threadName = "SmartLR-" + (label ?? "anon") + "-" + _longRunningCounter;
            System.Threading.Interlocked.Increment(ref _longRunningCounter);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_rootCts.Token);
            // L-023: self-respawning supervisor. The pre-L-023 lambda caught TAE + ResetAbort
            // but the thread exited permanently on the very next line — that's why the 9
            // long-running daemons all died silently on FMOD pause. The supervisor loops
            // back into work() after AbortGuard.Absorb returns Continue. Continue on TAE;
            // exit on storm trip (DaemonWatchdog respawns); exit on OCE (clean cancel);
            // exit on Exception (let the watchdog respawn — better than infinite throw loop).
            //
            // RISK noted in plan §3.1: re-entering work() restarts the RunLoop's outer while
            // shell; mid-iteration state inside work() (Adam Step partial, AetherFuser
            // _lastBelief mid-fuse, Coordinator _previousAssignments mid-reassign) is left
            // in whatever state TAE interrupted it. L-058 TrainerWorker FSM mitigates the
            // trainer case; other RunLoops are idempotent across mid-iteration TAE
            // (verified during each daemon's L-024 catch landing).
            var thread = new Thread(_ =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        work(cts.Token);
                        // work() returned of its own accord (normal shutdown) — exit loop.
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        // Clean cancellation — exit loop.
                        return;
                    }
                    catch (System.Threading.ThreadAbortException)
                    {
                        // L-023: supervisor absorbs + decides whether to re-enter work().
                        if (AbortGuard.Absorb(threadName) == AbortGuard.AbortAction.ExitForRespawn)
                        {
                            // Storm threshold tripped — exit so DaemonWatchdog can respawn
                            // with a fresh CancellationToken + clean local state.
                            return;
                        }
                        // else: loop back into work() — daemon's outer while resumes.
                    }
                    catch (Exception ex)
                    {
                        // Deterministic bug in work() — let the watchdog respawn so we don't
                        // tight-loop on the same exception. ThreadingDiagnostics records the
                        // exception so the operator sees what happened.
                        DebugTAC_AI.LogWarning("Smart.WorkerPool: long-running '" + threadName
                            + "' threw " + ex.GetType().Name + ": " + ex.Message
                            + " — exiting for watchdog respawn");
                        return;
                    }
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
            // L-005: lock the invariant on the long-running spawn path as well.
            System.Diagnostics.Debug.Assert(thread.IsBackground, "WorkerPool long-running worker must be IsBackground=true");
            thread.Start();
            return threadName;
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
                // L-022: outer try wraps BOTH the work-dispatch branch AND the idle
                // SpinWait/Sleep(0) branch. The pre-L-022 code only caught TAE inside
                // work() — so an abort delivered to a worker idling in SpinWait fell
                // through to `finally` and the worker died with UnexpectedExit (matches
                // the output_log.txt cascade across all 22 Smart#N workers). Catch order
                // OCE→TAE→Exception per ECMA-335 §12.4.2.5 (TAE auto-rethrows past a
                // generic catch(Exception) unless explicitly handled before).
                while (!token.IsCancellationRequested)
                {
                    try
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
                                // L-022: AbortGuard owns ResetAbort + storm detection. On
                                // storm trip, exit cleanly with TerminationReason.AbortStorm
                                // so DaemonWatchdog/WorkerHealthMonitor can respawn us.
                                if (AbortGuard.Absorb(handle.Name) == AbortGuard.AbortAction.ExitForRespawn)
                                {
                                    exitReason = TerminationReason.AbortStorm;
                                    return;
                                }
                                // Continue the loop — work was interrupted but the worker survives.
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
                            // Idle branch — BLOCK on the work signal instead of spinning.
                            // Enqueue Sets the signal to wake a worker; the timeout is a
                            // safety re-poll so a missed Set never strands an item. This is
                            // what keeps idle pool workers off the CPU (was a SpinWait+Sleep(0)
                            // busy-loop that pegged one core per worker). Protected by the
                            // outer try so an abort delivered here still hits our handler.
                            _workSignal.WaitOne(IdleWaitMs);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Outer OCE — also a normal shutdown signal. Fall through to the
                        // IsCancellationRequested check on the next iteration.
                    }
                    catch (System.Threading.ThreadAbortException)
                    {
                        // L-022: idle-branch abort. Same Absorb + storm-exit policy as the
                        // inner work-frame catch above.
                        if (AbortGuard.Absorb(handle.Name) == AbortGuard.AbortAction.ExitForRespawn)
                        {
                            exitReason = TerminationReason.AbortStorm;
                            return;
                        }
                    }
                    // Outer Exception intentionally NOT caught — the inner work-frame
                    // catch already absorbs work-delegate exceptions with retry budget.
                    // Any exception that escapes the inner branch is a structural bug in
                    // the pool itself; let it bubble to finally + UnexpectedExit so we
                    // get a stack-traced log line.
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

        /// <summary>
        /// L-048: scan pool workers, replace any whose ThreadState contains Stopped|Aborted
        /// with fresh threads bound to fresh CancellationTokens. Called from
        /// WorkerHealthMonitor (L-047) when a missing pool worker is detected. Returns the
        /// count replaced. No-op when disposed or registry teardown in progress.
        ///
        /// The dead worker's WorkerLifecycleRegistry.Deregister runs from its own finally;
        /// we don't try to chase its cleanup. The replacement registers normally so the
        /// registry SnapshotLive sees `N` workers again on the next watchdog tick.
        /// </summary>
        public int ReplaceDeadWorkers()
        {
            if (_disposed) return 0;
            if (WorkerLifecycleRegistry.IsTearingDown) return 0;

            int replaced = 0;
            for (int i = 0; i < _workers.Length; i++)
            {
                var t = _workers[i];
                if (t == null) continue;
                var s = t.ThreadState;
                bool dead = (s & ThreadState.Stopped) != 0 || (s & ThreadState.Aborted) != 0;
                if (!dead) continue;

                var oldName = _handles[i] != null ? _handles[i].Name : ("Smart#" + i);
                try
                {
                    var workerCts = CancellationTokenSource.CreateLinkedTokenSource(_rootCts.Token);
                    var newThread = new Thread(WorkerLoop)
                    {
                        Name = "SmartWorker-" + oldName,
                        IsBackground = true,
                        Priority = ThreadPriority.Normal,
                    };
                    var newHandle = new WorkerHandle(oldName, newThread, workerCts);
                    _handles[i] = newHandle;
                    _workers[i] = newThread;
                    WorkerLifecycleRegistry.Register(newHandle);
                    ThreadingDiagnostics.RaiseWorkerStarted(oldName);
                    System.Diagnostics.Debug.Assert(newThread.IsBackground,
                        "WorkerPool replacement worker must be IsBackground=true");
                    newThread.Start(newHandle);
                    replaced++;
                    DebugTAC_AI.LogWarnFileOnly("smart-worker-replaced-" + oldName,
                        "[SMART-WORKER-RESPAWN] pool replaced dead worker name=" + oldName
                        + " priorState=" + s);
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("WorkerPool.ReplaceDeadWorkers: replace failed for slot "
                        + i + " (" + oldName + "): " + ex.Message);
                }
            }
            return replaced;
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
            // Wake any workers blocked in the idle wait so they see the cancel immediately.
            for (int w = 0; w < _workers.Length; w++)
            {
                try { _workSignal.Set(); } catch { break; }
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
            try { _workSignal.Dispose(); } catch { }
        }
    }
}
