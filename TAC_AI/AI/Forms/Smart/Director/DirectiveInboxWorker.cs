using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    // 5 Hz inbox poller. Watches <UserDir>/SmartAI/Director/inbox/*.directive via
    // FileSystemWatcher with a LastWriteTime poll fallback. Each accepted file's lines
    // feed DirectiveSurface.AddLine. The canonical-roster string "DirectorInbox" already
    // exists (Phase 1) — this supplies the factory + spins from TrainingDirector.Init.
    public static class DirectiveInboxWorker
    {
        public const string CanonicalName = "DirectorInbox";
        public const int PollMs = 200;            // 5 Hz
        public const int InitWaitPollMs = 1000;

        private static volatile bool _daemonInitComplete;
        private static volatile bool _running;
        private static string _inboxDir;
        private static string _processedDir;
        private static string _erroredDir;
        private static FileSystemWatcher _watcher;
        private static readonly ConcurrentQueue<string> _pendingFiles = new ConcurrentQueue<string>();
        private static readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        // Track last-seen write times for the poll fallback.
        private static readonly ConcurrentDictionary<string, long> _seen =
            new ConcurrentDictionary<string, long>();

        public static void Init(WorkerPool pool, string modDirectory)
        {
            if (pool == null) return;
            if (_daemonInitComplete) return;
            _running = true;

            try
            {
                if (!string.IsNullOrEmpty(modDirectory))
                {
                    _inboxDir = Path.Combine(modDirectory, "SmartAI", "Director", "inbox");
                    if (!Directory.Exists(_inboxDir)) Directory.CreateDirectory(_inboxDir);
                    _processedDir = Path.Combine(_inboxDir, "processed");
                    _erroredDir = Path.Combine(_inboxDir, "errored");
                    TryStartWatcher();
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-inbox-init",
                    "[DIRECTOR-INBOX] init threw " + ex.GetType().Name + ": " + ex.Message);
            }

            pool.EnqueueLongRunning(RunLoop, CanonicalName);
            var poolRef = pool;
            Func<string> factory = () => poolRef != null ? poolRef.EnqueueLongRunning(RunLoop, CanonicalName) : null;
            DaemonWatchdog.RegisterCanonical(CanonicalName, factory);
            WorkerHealthMonitor.RegisterCanonical(CanonicalName, factory);

            _daemonInitComplete = true;
        }

        public static void BeginShutdown()
        {
            _running = false;
            _daemonInitComplete = false;
            try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } }
            catch { /* shell may be tearing down */ }
            _watcher = null;
            _processedDir = null;
            _erroredDir = null;
            string _;
            while (_pendingFiles.TryDequeue(out _)) { }
            _seen.Clear();
            _wake.Set();
        }

        private static void TryStartWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(_inboxDir, "*.directive");
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
                _watcher.Created += OnFsEvent;
                _watcher.Changed += OnFsEvent;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-inbox-fsw",
                    "[DIRECTOR-INBOX] FSW init failed, poll-only: " + ex.Message);
                _watcher = null;
            }
        }

        // FSW callback is a thin wrapper — swallow because FSW runs on a threadpool thread.
        private static void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            try { _pendingFiles.Enqueue(e.FullPath); _wake.Set(); } catch { }
        }

        public static void RunLoop(CancellationToken cancellation)
        {
            while (!_daemonInitComplete && !cancellation.IsCancellationRequested)
            {
                if (cancellation.WaitHandle.WaitOne(InitWaitPollMs)) return;
            }

            while (!cancellation.IsCancellationRequested)
            {
                if (!_running)
                {
                    if (cancellation.WaitHandle.WaitOne(PollMs)) return;
                    continue;
                }
                try { Tick(); }
                catch (OperationCanceledException) { return; }
                catch (ThreadAbortException)
                {
                    if (AbortGuard.Absorb(CanonicalName) == AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("director-inbox-tick",
                        "[DIRECTOR-INBOX] tick threw " + ex.GetType().Name + ": " + ex.Message);
                }

                _wake.Reset();
                if (cancellation.WaitHandle.WaitOne(PollMs)) return;
            }
        }

        private static void Tick()
        {
            // FSW-queued files first.
            string path;
            while (_pendingFiles.TryDequeue(out path)) ProcessFile(path);

            // Poll fallback for environments where FSW does not fire.
            if (string.IsNullOrEmpty(_inboxDir) || !Directory.Exists(_inboxDir)) return;
            string[] files;
            try { files = Directory.GetFiles(_inboxDir, "*.directive"); }
            catch { return; }
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    long mt = File.GetLastWriteTimeUtc(files[i]).ToFileTimeUtc();
                    long prev;
                    if (_seen.TryGetValue(files[i], out prev) && prev == mt) continue;
                    _seen[files[i]] = mt;
                    ProcessFile(files[i]);
                }
                catch { /* file may vanish mid-poll */ }
            }
        }

        private static void ProcessFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { return; }

            System.Text.StringBuilder rejects = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i] != null ? lines[i].Trim() : "";
                if (line.Length == 0 || line[0] == '#') continue;
                var r = DirectiveSurface.AddLine(line);
                if (r.Ok)
                {
                    // Surfaces trig=directive:<id> in the next digest header.
                    DigestBuilder.RequestTrigger("directive:" + r.Id);
                }
                else
                {
                    if (rejects == null) rejects = new System.Text.StringBuilder();
                    rejects.Append(r.Error).Append(" line=").Append(line).Append('\n');
                    DebugTAC_AI.LogWarnFileOnly("director-inbox-reject-" + path,
                        "[DIRECTOR-INBOX] " + r.Error + " line=" + line);
                }
            }

            // Move (not delete) so the operator can audit. Any rejected line sends the
            // whole file to errored/ with a .reject sidecar; otherwise processed/.
            if (rejects != null) MoveTo(path, _erroredDir, rejects.ToString());
            else MoveTo(path, _processedDir, null);
            long _; _seen.TryRemove(path, out _);
        }

        private static void MoveTo(string path, string destDir, string rejectBody)
        {
            try
            {
                if (string.IsNullOrEmpty(destDir))
                {
                    // No disposition dir (no modDirectory) — fall back to delete-on-consume.
                    File.Delete(path);
                    return;
                }
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                string stamp = MonoClock.Now().ToString("X");
                string baseName = Path.GetFileNameWithoutExtension(path);
                string dest = Path.Combine(destDir, baseName + "-" + stamp + ".directive");
                if (File.Exists(dest)) { try { File.Delete(dest); } catch { } }
                File.Move(path, dest);
                if (!string.IsNullOrEmpty(rejectBody))
                {
                    try { File.WriteAllText(dest + ".reject", rejectBody, new System.Text.UTF8Encoding(false)); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                // Last resort: drop it so it isn't re-applied on the next poll.
                try { File.Delete(path); } catch { }
                DebugTAC_AI.LogWarnFileOnly("director-inbox-move",
                    "[DIRECTOR-INBOX] move threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
