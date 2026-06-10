using System;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// L-078: three-headed quit-save coordinator.
    ///
    /// Subscribed sources:
    ///   - <c>Singleton.ApplicationQuitEvent</c>     (engine graceful close, via SmartLifecycleShim)
    ///   - <c>AppDomain.ProcessExit</c>               (process tear-down, including SIGTERM/task-manager)
    ///   - <c>AppDomain.UnhandledException</c>        (last-ditch on unhandled crash)
    ///
    /// HasFired Interlocked-set makes the coordinator idempotent across multiple sources.
    /// SmartRuntime.Shutdown calls <see cref="Flush(int)"/> BEFORE CancelAllAndJoin so the
    /// save runs while workers are still alive (FlushPendingForPersist needs them).
    /// 200ms budget reserved from the 2-second worker-join budget.
    ///
    /// Emits exactly one <c>[PROFILE-QUIT-SAVE] source=&lt;src&gt; saved=&lt;0/1&gt; elapsed=Yms flush=K/4</c>
    /// log line regardless of which source fired.
    /// </summary>
    public static class QuitSaveCoordinator
    {
        public const int DefaultDeadlineMs = 200;

        private static int _hasFired;   // 0=not yet, 1=fired (Interlocked-set)
        private static string _firstSource;
        private static bool _installed;

        public static bool HasFired => Volatile.Read(ref _hasFired) == 1;

        /// <summary>
        /// Idempotent. Wired from SmartLifecycleShim.Install + AppDomain hooks.
        /// </summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try { AppDomain.CurrentDomain.ProcessExit += OnProcessExit; } catch { }
            try { AppDomain.CurrentDomain.UnhandledException += OnUnhandledException; } catch { }
            DebugTAC_AI.LogWarnFileOnly("quit-save-coordinator-install",
                "QuitSaveCoordinator.Install: AppDomain.ProcessExit + UnhandledException subscribed");
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            _installed = false;
            try { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; } catch { }
            try { AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException; } catch { }
            // Do NOT reset _hasFired — if Uninstall is called between Install and Flush we
            // still want the prior fire to dedupe correctly on next process.
        }

        private static void OnProcessExit(object sender, EventArgs e) => Flush(DefaultDeadlineMs, "ProcessExit");
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => Flush(DefaultDeadlineMs, "UnhandledException");

        /// <summary>
        /// Public entry: called from SmartLifecycleShim's quit hooks AND from
        /// SmartRuntime.Shutdown's pre-CancelAllAndJoin flush. Idempotent.
        /// </summary>
        public static void Flush(int deadlineMs, string source = "Manual")
        {
            int prior = Interlocked.CompareExchange(ref _hasFired, 1, 0);
            if (prior != 0)
            {
                DebugTAC_AI.LogWarnFileOnly("quit-save-loser-" + source,
                    "[PROFILE-QUIT-SAVE] source=" + source + " saved=0 (loser; first source=" + (_firstSource ?? "<unknown>") + ")");
                return;
            }
            _firstSource = source;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int saved = 0;
            int flushOk = 0;
            int flushTotal = 0;
            try
            {
                if (LearningService.IsRunning)
                {
                    // BUG-018 / BUG-055: do not write profile from a client process. Even
                    // though LearningService.SaveProfile itself gates on IsHost, the
                    // FlushPendingForPersist pass below mutates model params (runs one
                    // final minibatch) — pointless work on a client that received no
                    // training events. Bail early so a client quit/crash never touches
                    // model state OR the on-disk profile.
                    if (!SmartRuntime.IsHost)
                    {
                        DebugTAC_AI.LogWarnFileOnly("quit-save-nonhost-" + source,
                            "[PROFILE-QUIT-SAVE] source=" + source + " saved=0 (non-host; skipping flush+save)");
                        return;
                    }
                    // Flush each model so unsaved minibatches reach the disk image.
                    var models = new ILearnedModel[]
                    {
                        LearningService.Intent, LearningService.ActionValue,
                        LearningService.Residual, LearningService.Threat,
                    };
                    flushTotal = models.Length;
                    for (int i = 0; i < models.Length; i++)
                    {
                        var m = models[i];
                        if (m == null) continue;
                        try { m.FlushPendingForPersist(); flushOk++; }
                        catch (Exception ex)
                        {
                            DebugTAC_AI.LogWarning("QuitSave flush model[" + i + "]: " + ex.Message);
                        }
                        if (sw.ElapsedMilliseconds > deadlineMs) break;
                    }
                    try
                    {
                        LearningService.SaveProfile(LearningService.CurrentPlayerId);
                        saved = 1;
                    }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarning("QuitSave SaveProfile: " + ex.Message);
                    }
                }
            }
            finally
            {
                DebugTAC_AI.LogWarnFileOnly("profile-quit-save",
                    "[PROFILE-QUIT-SAVE] source=" + source
                    + " saved=" + saved
                    + " elapsed=" + sw.ElapsedMilliseconds + "ms"
                    + " flush=" + flushOk + "/" + flushTotal);
            }
        }
    }
}
