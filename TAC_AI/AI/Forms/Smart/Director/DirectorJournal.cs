using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// One journal record. JSONL — every entry serialised as a single line.
    /// </summary>
    public readonly struct JournalEntry
    {
        public readonly long MonoTick;
        public readonly long WallclockUtcSec;
        public readonly string Kind;
        public readonly string Body;

        public JournalEntry(long monoTick, long wallclockUtcSec, string kind, string body)
        {
            MonoTick = monoTick;
            WallclockUtcSec = wallclockUtcSec;
            Kind = kind ?? string.Empty;
            Body = body ?? string.Empty;
        }
    }

    /// <summary>
    /// Append-only JSONL journal. Producer enqueues onto a private disk queue;
    /// TrainingDirector's RunLoop drains it on the daemon thread. Rotation at 5 MB
    /// into journal.&lt;N&gt;.jsonl; entries older than 30 days are gz-compressed.
    ///
    /// JSONL is line-atomic — every entry is one append. We do NOT use
    /// ProfilePersistence's write-temp + Replace pattern; that protects whole-file
    /// rewrites, not append streams. File.AppendAllText with a small retry covers
    /// the Windows AV-lock window.
    /// </summary>
    public static class DirectorJournal
    {
        public const long RotateThresholdBytes = 5L * 1024L * 1024L;
        public const int CompressOlderThanDays = 30;
        public const int RotateRetryMax = 3;

        private static string _journalDir;
        private static string _journalPath;
        private static readonly ConcurrentQueue<JournalEntry> _diskQueue
            = new ConcurrentQueue<JournalEntry>();
        private static int _initialised;
        private static long _lastCompressScanWallSec;

        /// <summary>Idempotent init. Called from TrainingDirector.Init.</summary>
        public static void Init(string modDirectory)
        {
            if (Interlocked.CompareExchange(ref _initialised, 1, 0) != 0) return;
            if (string.IsNullOrEmpty(modDirectory))
            {
                _journalDir = null;
                _journalPath = null;
                return;
            }
            try
            {
                _journalDir = Path.Combine(modDirectory, "SmartAI", "Director");
                if (!Directory.Exists(_journalDir)) Directory.CreateDirectory(_journalDir);
                _journalPath = Path.Combine(_journalDir, "journal.jsonl");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-journal-init",
                    "[DIRECTOR-JOURNAL] Init threw " + ex.GetType().Name + ": " + ex.Message);
                _journalDir = null;
                _journalPath = null;
            }
        }

        /// <summary>Reset on world reset / Director.BeginShutdown finish.</summary>
        public static void Reset()
        {
            Interlocked.Exchange(ref _initialised, 0);
            _journalDir = null;
            _journalPath = null;
            JournalEntry _;
            while (_diskQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _lastCompressScanWallSec, 0L);
        }

        /// <summary>Push onto RAM ring + disk queue. Safe from any thread.</summary>
        public static void Append(JournalEntry entry)
        {
            DirectorState.EnqueueJournal(entry);
            _diskQueue.Enqueue(entry);
        }

        /// <summary>Convenience: build the JournalEntry from kind+body.</summary>
        public static void Append(string kind, string body)
        {
            long mono = World.MonoClock.Now();
            long wallSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Append(new JournalEntry(mono, wallSec, kind, body));
        }

        // Last count entries from the RAM ring, oldest-first. ConcurrentQueue has no
        // random access so we snapshot ToArray then take the tail. LLM-side tail read.
        public static JournalEntry[] GetTail(int count)
        {
            if (count <= 0) return new JournalEntry[0];
            JournalEntry[] all = DirectorState.JournalRingInMem.ToArray();
            if (all.Length <= count) return all;
            var tail = new JournalEntry[count];
            Array.Copy(all, all.Length - count, tail, 0, count);
            return tail;
        }

        // Tail rendered as JSONL text — one complete {...} record per line, matching
        // the on-disk format. For consumers that want the ready stream.
        public static string GetTailJsonl(int count)
        {
            JournalEntry[] tail = GetTail(count);
            if (tail.Length == 0) return string.Empty;
            var sb = new StringBuilder(tail.Length * 96);
            for (int i = 0; i < tail.Length; i++) AppendJsonLine(sb, tail[i]);
            return sb.ToString();
        }

        /// <summary>
        /// Drain the disk queue onto SmartAI/Director/journal.jsonl. Called from
        /// TrainingDirector's RunLoop — not its own daemon. Per-write try/catch +
        /// best-effort rotation + best-effort gzip pass on a slower cadence.
        /// </summary>
        public static void DrainToDisk()
        {
            if (string.IsNullOrEmpty(_journalPath)) return;

            StringBuilder sb = null;
            JournalEntry e;
            int drained = 0;
            while (_diskQueue.TryDequeue(out e))
            {
                if (sb == null) sb = new StringBuilder(512);
                AppendJsonLine(sb, e);
                drained++;
                if (drained >= 256) break; // bound one drain pass
            }
            if (sb == null) return;
            string payload = sb.ToString();

            try
            {
                File.AppendAllText(_journalPath, payload, new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // AV-lock retry once with backoff.
                try
                {
                    Thread.Sleep(15);
                    File.AppendAllText(_journalPath, payload, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("director-journal-append",
                        "[DIRECTOR-JOURNAL] append-retry threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-journal-append",
                    "[DIRECTOR-JOURNAL] append threw " + ex.GetType().Name + ": " + ex.Message);
            }

            TryRotate();
            TryCompressOldFiles();
        }

        private static void AppendJsonLine(StringBuilder sb, JournalEntry e)
        {
            sb.Append('{');
            sb.Append("\"monoTick\":").Append(e.MonoTick).Append(',');
            sb.Append("\"wallclockUtcSec\":").Append(e.WallclockUtcSec).Append(',');
            sb.Append("\"kind\":\"");
            JsonEscape(sb, e.Kind);
            sb.Append("\",\"body\":\"");
            JsonEscape(sb, e.Body);
            sb.Append("\"}\n");
        }

        private static void JsonEscape(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append('\\').Append('\\'); break;
                    case '"': sb.Append('\\').Append('"'); break;
                    case '\n': sb.Append('\\').Append('n'); break;
                    case '\r': sb.Append('\\').Append('r'); break;
                    case '\t': sb.Append('\\').Append('t'); break;
                    case '\b': sb.Append('\\').Append('b'); break;
                    case '\f': sb.Append('\\').Append('f'); break;
                    default:
                        if (c < 0x20) sb.Append('?');
                        else sb.Append(c);
                        break;
                }
            }
        }

        private static void TryRotate()
        {
            try
            {
                if (string.IsNullOrEmpty(_journalPath)) return;
                var fi = new FileInfo(_journalPath);
                if (!fi.Exists || fi.Length < RotateThresholdBytes) return;
                string rotated = NextRotatedPath();
                if (rotated == null) return;
                for (int attempt = 0; attempt < RotateRetryMax; attempt++)
                {
                    try
                    {
                        File.Move(_journalPath, rotated);
                        return;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(20 * (attempt + 1));
                    }
                }
                DebugTAC_AI.LogWarnFileOnly("director-journal-rotate",
                    "[DIRECTOR-JOURNAL] rotation failed after " + RotateRetryMax + " attempts");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-journal-rotate-threw",
                    "[DIRECTOR-JOURNAL] rotate threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string NextRotatedPath()
        {
            if (string.IsNullOrEmpty(_journalDir)) return null;
            for (int n = 0; n < 10000; n++)
            {
                string candidate = Path.Combine(_journalDir, "journal." + n + ".jsonl");
                if (!File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static void TryCompressOldFiles()
        {
            // Run at most once per 5 min wall-time so the scan doesn't run every drain.
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long prev = Interlocked.Read(ref _lastCompressScanWallSec);
            if (prev != 0 && (now - prev) < 300) return;
            if (Interlocked.CompareExchange(ref _lastCompressScanWallSec, now, prev) != prev) return;

            try
            {
                if (string.IsNullOrEmpty(_journalDir) || !Directory.Exists(_journalDir)) return;
                long cutoff = now - (long)CompressOlderThanDays * 24L * 3600L;
                string[] files = Directory.GetFiles(_journalDir, "journal.*.jsonl");
                for (int i = 0; i < files.Length; i++)
                {
                    string path = files[i];
                    try
                    {
                        long mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
                        if (mtime >= cutoff) continue;
                        CompressFile(path);
                    }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarnFileOnly("director-journal-gz-" + Path.GetFileName(path),
                            "[DIRECTOR-JOURNAL] gz threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-journal-gz-scan",
                    "[DIRECTOR-JOURNAL] gz scan threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void CompressFile(string sourcePath)
        {
            // Name pattern: journal-YYYY-MM-DD.jsonl.gz. Use the file's own mtime as
            // the date stamp so multiple rotated files of differing ages don't collide.
            DateTime mt = File.GetLastWriteTimeUtc(sourcePath);
            string stampedName = "journal-" + mt.ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture) + ".jsonl.gz";
            string dest = Path.Combine(_journalDir, stampedName);
            // If a file with that name already exists, append a counter.
            int counter = 1;
            while (File.Exists(dest))
            {
                stampedName = "journal-" + mt.ToString("yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture) + "." + counter + ".jsonl.gz";
                dest = Path.Combine(_journalDir, stampedName);
                counter++;
                if (counter > 1000) return; // give up
            }
            using (var inStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var outFs = new FileStream(dest, FileMode.Create, FileAccess.Write))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            {
                inStream.CopyTo(gz);
            }
            File.Delete(sourcePath);
        }
    }
}
