using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Per-(techId, guardId) rolling-window pool. Lock-based mirror of
    // PathRequestBackpressure._latencyReservoir. Single writer / single reader
    // (GuardWorker tick). LRU evicts entries for despawned techs so the pool doesn't grow
    // without bound.
    //
    // Sized 64 techs × 11 guards = 704 slots. Each slot allocates a 60-sample window by
    // default — guards request a per-window capacity via EnsureWindow that matches their
    // declared WindowSec at 1 Hz writer cadence (slot count == WindowSec).
    public static class GuardWindowPool
    {
        public const int MaxTechs = 64;

        // Composite key: (techValue << 8) | guardOrdinal. TechId.Value is 32-bit signed
        // int (Tank.GetInstanceID or netId) — pack as long to avoid collisions.
        private static readonly object _gate = new object();
        private static readonly Dictionary<long, RollingWindow> _windows =
            new Dictionary<long, RollingWindow>(MaxTechs * GuardRegistry.GuardCount);
        private static readonly Dictionary<long, long> _lastTouchMono =
            new Dictionary<long, long>(MaxTechs * GuardRegistry.GuardCount);
        private static readonly HashSet<int> _knownTechs = new HashSet<int>();

        private static long PackKey(TechId techId, int guardOrdinal)
        {
            return ((long)techId.Value << 8) | (long)(guardOrdinal & 0xFF);
        }

        // Get-or-create the window for (techId, guardId) sized at windowSec slots.
        // Touches the LRU timestamp.
        public static RollingWindow EnsureWindow(TechId techId, int guardOrdinal, int windowSec)
        {
            long key = PackKey(techId, guardOrdinal);
            lock (_gate)
            {
                RollingWindow w;
                if (!_windows.TryGetValue(key, out w))
                {
                    int cap = windowSec > 0 ? windowSec : 30;
                    w = new RollingWindow(cap);
                    _windows[key] = w;
                    _knownTechs.Add(techId.Value);
                    if (_knownTechs.Count > MaxTechs) EvictOldestTech();
                }
                _lastTouchMono[key] = MonoClock.Now();
                return w;
            }
        }

        // Drop all windows for a tech. Called on TechDespawned / Recycled paths so
        // pool-reuse doesn't inherit stale window data.
        public static void ForgetTech(TechId techId)
        {
            lock (_gate)
            {
                var toDrop = new List<long>();
                foreach (var kv in _windows)
                {
                    if ((int)(kv.Key >> 8) == techId.Value) toDrop.Add(kv.Key);
                }
                for (int i = 0; i < toDrop.Count; i++)
                {
                    _windows.Remove(toDrop[i]);
                    _lastTouchMono.Remove(toDrop[i]);
                }
                _knownTechs.Remove(techId.Value);
            }
        }

        public static void Clear()
        {
            lock (_gate)
            {
                _windows.Clear();
                _lastTouchMono.Clear();
                _knownTechs.Clear();
            }
        }

        public static int CountTechs()
        {
            lock (_gate) return _knownTechs.Count;
        }

        // Drop the windows for the tech whose oldest-touch timestamp is the smallest.
        // Mutates _knownTechs after _windows so the test above stays consistent.
        private static void EvictOldestTech()
        {
            long oldestStamp = long.MaxValue;
            int oldestTech = 0;
            bool found = false;
            foreach (var kv in _lastTouchMono)
            {
                if (kv.Value < oldestStamp)
                {
                    oldestStamp = kv.Value;
                    oldestTech = (int)(kv.Key >> 8);
                    found = true;
                }
            }
            if (!found) return;
            var toDrop = new List<long>();
            foreach (var kv in _windows)
            {
                if ((int)(kv.Key >> 8) == oldestTech) toDrop.Add(kv.Key);
            }
            for (int i = 0; i < toDrop.Count; i++)
            {
                _windows.Remove(toDrop[i]);
                _lastTouchMono.Remove(toDrop[i]);
            }
            _knownTechs.Remove(oldestTech);
        }
    }
}
