using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>
    /// L-016: central reset orchestrator. Subsystems register an <see cref="IWorldResettable"/>
    /// at Init (or an inline lambda — both shapes supported). On world reset, SmartForm.OnWorldReset
    /// (L-069) collapses to a single <see cref="ResetAll"/> call; the registry walks every
    /// registered hook in registration order, times each one, and emits a [WORLD-RESET] log
    /// line per hook + a [WORLD-RESET-COMPLETED] summary at the end.
    ///
    /// Registration order is preserved (sequential List under a lock) so an operator can
    /// reason about hook order by reading SmartRuntime.Init top-to-bottom.
    /// </summary>
    public static class WorldResetRegistry
    {
        private sealed class Entry
        {
            public string Name;
            public Action Reset;
        }

        private static readonly object _lock = new object();
        private static readonly System.Collections.Generic.List<Entry> _entries
            = new System.Collections.Generic.List<Entry>();

        /// <summary>Register a class instance.</summary>
        public static void Register(IWorldResettable resettable)
        {
            if (resettable == null) return;
            lock (_lock)
            {
                _entries.Add(new Entry
                {
                    Name = resettable.ResetName ?? resettable.GetType().Name,
                    Reset = resettable.ResetForWorldChange,
                });
            }
        }

        /// <summary>
        /// Register an inline lambda. Use for one-off captures of private fields that don't
        /// justify a full IWorldResettable wrapper class (e.g. clearing a private
        /// ConcurrentDictionary on a service).
        /// </summary>
        public static void RegisterLambda(string name, Action resetAction)
        {
            if (string.IsNullOrEmpty(name) || resetAction == null) return;
            lock (_lock)
            {
                _entries.Add(new Entry { Name = name, Reset = resetAction });
            }
        }

        /// <summary>Count of registered hooks. Used by Test L-068.</summary>
        public static int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        /// <summary>
        /// Reset every registered hook. Each runs inside its own try/catch and is timed.
        /// Returns the number of hooks that completed without throwing. SmartForm.OnWorldReset
        /// (L-069) ignores the return — diagnostic only.
        /// </summary>
        public static int ResetAll()
        {
            Entry[] snap;
            lock (_lock) snap = _entries.ToArray();

            int ok = 0;
            var overall = Stopwatch.StartNew();
            DebugTAC_AI.LogWarnFileOnly("world-reset-starting",
                "[WORLD-RESET] starting hooks=" + snap.Length);

            for (int i = 0; i < snap.Length; i++)
            {
                var e = snap[i];
                if (e == null) continue;
                var sw = Stopwatch.StartNew();
                try
                {
                    e.Reset?.Invoke();
                    ok++;
                    DebugTAC_AI.LogWarnFileOnly("world-reset-hook-" + e.Name,
                        "[WORLD-RESET] hook='" + e.Name + "' outcome=ok elapsed=" + sw.ElapsedMilliseconds + "ms");
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("world-reset-fail-" + e.Name,
                        "[WORLD-RESET-FAIL] hook='" + e.Name + "' threw " + ex.GetType().Name
                        + ": " + ex.Message + " elapsed=" + sw.ElapsedMilliseconds + "ms");
                }
            }

            string outcome = ok == snap.Length ? "completed" : "partial";
            DebugTAC_AI.LogWarnFileOnly("world-reset-" + outcome,
                "[WORLD-RESET-" + outcome.ToUpperInvariant() + "] ok=" + ok + "/" + snap.Length
                + " totalElapsed=" + overall.ElapsedMilliseconds + "ms");
            return ok;
        }

        /// <summary>SMART_DEV test hook: drop all registrations for a clean test session.</summary>
        public static void Clear()
        {
            lock (_lock) _entries.Clear();
        }
    }
}
