using System;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// L-060: 30s autosave worker. Dirty-only — checks LearningService.HasUnsavedTraining
    /// before saving so a non-training session (alt-tabbed, watching attract mode) doesn't
    /// burn disk I/O. Emits [PROFILE-AUTOSAVE] log per saved cycle.
    ///
    /// Cadence: 30s default. Pauses with IsPaused; gates on IsHost; survives FMOD aborts.
    /// </summary>
    public static class AutosaveWorker
    {
        public const int CyclePeriodMs = 30000;

        private static long _autosavesTotal;
        private static long _autosavesSkipped;
        public static long AutosavesTotal => Interlocked.Read(ref _autosavesTotal);
        public static long AutosavesSkipped => Interlocked.Read(ref _autosavesSkipped);

        public static string Enqueue(TAC_AI.AI.Forms.Smart.Threading.WorkerPool pool)
        {
            if (pool == null) return null;
            return pool.EnqueueLongRunning(RunLoop, "Autosave");
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _autosavesTotal, 0L);
            Interlocked.Exchange(ref _autosavesSkipped, 0L);
        }

        private static void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
                    continue;
                }
                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
                    continue;
                }

                try { TickOnce(); }
                catch (OperationCanceledException) { return; }
                catch (ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("Autosave")
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.AutosaveWorker: " + ex.GetType().Name + ": " + ex.Message);
                }

                if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
            }
        }

        private static void TickOnce()
        {
            if (!LearningService.IsRunning) { Interlocked.Increment(ref _autosavesSkipped); return; }
            if (!LearningService.HasUnsavedTraining)
            {
                Interlocked.Increment(ref _autosavesSkipped);
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LearningService.SaveProfile(LearningService.CurrentPlayerId);
                Interlocked.Increment(ref _autosavesTotal);
                DebugTAC_AI.LogWarnFileOnly("profile-autosave",
                    "[PROFILE-AUTOSAVE] period=" + CyclePeriodMs + "ms saved=1 elapsed=" + sw.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.AutosaveWorker.SaveProfile: " + ex.Message);
            }
        }
    }
}
