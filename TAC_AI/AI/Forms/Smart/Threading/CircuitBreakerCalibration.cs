using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// P9 Item 31: per-daemon CircuitBreaker trip registry + session report writer.
    ///
    /// <see cref="RegisterTrip"/> is called from <see cref="CircuitBreaker.Tripped"/> when
    /// a daemon trips (process-wide event aggregator; thread-safe via
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/>). <see cref="WriteSessionReport"/>
    /// is called from <c>SmartRuntime.Shutdown</c> to emit a per-daemon trip-count log to
    /// disk for post-session inspection (telemetry for CircuitBreaker threshold tuning).
    ///
    /// Default behavior at v0.2: no trips = no report file (silent). One or more trips →
    /// timestamped JSON-ish line per daemon written to <c>SmartAI/CircuitBreaker.log</c>
    /// in the mod dir. Append-mode; previous sessions preserved.
    /// </summary>
    public static class CircuitBreakerCalibration
    {
        private static readonly ConcurrentDictionary<string, int> _tripsByDaemon =
            new ConcurrentDictionary<string, int>();

        /// <summary>Increment trip counter for <paramref name="daemonName"/>. Safe from any thread.</summary>
        public static void RegisterTrip(string daemonName)
        {
            if (string.IsNullOrEmpty(daemonName)) daemonName = "<unknown>";
            _tripsByDaemon.AddOrUpdate(daemonName, 1, (_, n) => n + 1);
        }

        /// <summary>
        /// Reset session counters. Called from Init so a fresh session doesn't carry stale
        /// trip data from a prior Init/Shutdown cycle within the same process.
        /// </summary>
        public static void ResetSession() => _tripsByDaemon.Clear();

        /// <summary>
        /// Write a session report to <c>{modDir}/SmartAI/CircuitBreaker.log</c> if any
        /// trips were registered. Idempotent; no-op when zero trips. Best-effort I/O
        /// (catches all exceptions and logs file-only on failure).
        /// </summary>
        public static void WriteSessionReport(string modDir)
        {
            if (_tripsByDaemon.IsEmpty) return;
            if (string.IsNullOrEmpty(modDir)) return;
            try
            {
                string smartDir = Path.Combine(modDir, "SmartAI");
                if (!Directory.Exists(smartDir)) Directory.CreateDirectory(smartDir);
                string path = Path.Combine(smartDir, "CircuitBreaker.log");
                var sb = new StringBuilder(256);
                sb.Append("[CB-SESSION] ");
                sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                sb.Append(" trips: ");
                bool first = true;
                foreach (var kv in _tripsByDaemon)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(kv.Key);
                    sb.Append('=');
                    sb.Append(kv.Value);
                    first = false;
                }
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("cb-calibration-write",
                    "CircuitBreakerCalibration.WriteSessionReport failed: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
