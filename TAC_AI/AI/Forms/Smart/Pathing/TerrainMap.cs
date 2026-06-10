using System;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Threading;

namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>
    /// Snapshot of the sampled terrain-height grid. Published via DoubleBuffer; queries
    /// are stateless reads of the latest snapshot. Per PATHING-CONTRACT §3.1.
    /// </summary>
    public sealed class TerrainMapSnapshot
    {
        public long TickStamp { get; }
        public Vector2 WorldOrigin { get; }  // (x, z) of cell [0, 0]
        public float CellSize { get; }
        public int Width { get; }            // cells along x
        public int Height { get; }           // cells along z
        public bool IsPopulated { get; }     // false until first refresh completes
        public System.Collections.Generic.IReadOnlyList<float> HeightSamples { get; }  // row-major [z*Width + x]

        public TerrainMapSnapshot(long tickStamp, Vector2 origin, float cellSize, int width, int height, float[] heights, bool isPopulated)
        {
            TickStamp = tickStamp;
            WorldOrigin = origin;
            CellSize = cellSize;
            Width = width;
            Height = height;
            HeightSamples = heights ?? new float[width * height];
            IsPopulated = isPopulated;
        }

        public static TerrainMapSnapshot Empty(Vector2 origin, float cellSize, int width, int height)
            => new TerrainMapSnapshot(0L, origin, cellSize, width, height, new float[width * height], isPopulated: false);
    }

    /// <summary>
    /// Per PATHING-CONTRACT §3.3.
    /// </summary>
    public interface ITerrainMap
    {
        float HeightAt(Vector2 worldXZ);
        Vector3 NormalAt(Vector2 worldXZ);
        float SlopeAt(Vector2 worldXZ);
        bool IsTraversable(Vector2 worldXZ, VehicleCapability filter);
        bool RaycastSegment(Vector3 from, Vector3 to);
    }

    /// <summary>
    /// Smart's cached terrain-height grid. Per PATHING-CONTRACT §3.
    ///
    /// Sample source: <see cref="ManWorld.inst.TileManager.GetTerrainHeightAtPosition"/>
    /// — same call <see cref="TAC_AI.AI.Movement.TerrainQuery"/> uses, so values agree.
    ///
    /// THREADING: ManWorld.inst is not documented as thread-safe (and Unity APIs
    /// generally are not). Per OQ-pathing-thread, v0.1.0 confines all sampling to
    /// the main thread via <see cref="RefreshFromMainThread"/>, called periodically by
    /// <see cref="PathingService"/> on its <see cref="PathingService.MainThreadTick"/>.
    /// Queries (HeightAt etc.) are pure reads of the published snapshot and safe from
    /// any thread.
    ///
    /// v0.1.0 grid extent: 512 cells × 512 cells × 4 m cellSize = 2.048 km × 2.048 km
    /// centered on origin. Memory: 1 MB. Larger maps require chunked loading (OPEN).
    /// </summary>
    public sealed class TerrainMap : ITerrainMap
    {
        public const int DefaultWidth = 512;
        public const int DefaultHeight = 512;
        public const float DefaultCellSize = 4f;
        public const int DefaultRefreshMs = 10000; // 10 s per §3.2

        private readonly DoubleBuffer<TerrainMapSnapshot> _buffer;
        private readonly Vector2 _origin;
        private readonly float _cellSize;
        private readonly int _w;
        private readonly int _h;
        private readonly float _waterHeight;     // KickStart.WaterHeight sampled at ctor; -9001 sentinel when no water mod
        private readonly bool _waterPresent;     // true iff WaterMod present at ctor time

        private int _lastRefreshEnvMs = int.MinValue;

        // Incremental refresh state. RefreshFromMainThread used to be a single synchronous
        // 512×512 = 262,144-cell sweep that called ManWorld.inst.TileManager.GetTerrainHeightAtPosition
        // for every cell on the main thread — observed as a ~7-second freeze the first time
        // a Smart-driven tech spawned (output_log.txt: "Smart.Operations[TIMING #0]
        // PathingService.MainThreadTick: 7211ms"). The Unity API forces main-thread; the cost
        // is intrinsic to that many calls. The fix is to time-slice: each tick processes a
        // small budget of cells, the snapshot is only published once the full pass completes,
        // and consumers see the existing Empty (isPopulated=false) snapshot until then.
        // Behavior contract for downstream readers is unchanged: snapshot transitions from
        // Empty to Populated atomically when the pass finishes.
        private const int DefaultRefreshBudgetMs = 2;
        private float[] _refreshAccumulator;
        private int _refreshCellIndex;
        private bool _refreshInProgress;

        public DoubleBuffer<TerrainMapSnapshot> Buffer => _buffer;
        public Vector2 Origin => _origin;
        public float CellSize => _cellSize;

        /// <summary>
        /// L-035: true until the first refresh tick has run. Consumers (threat-field
        /// rebuild, path solve) use this to discard cached derivatives held over from a
        /// prior world's terrain — the new TerrainMap is geometrically valid but its cell
        /// heights are zeros until the first MainThreadTick samples them.
        /// </summary>
        public bool IsFreshlyAllocated => System.Threading.Volatile.Read(ref _freshlyAllocated) == 1;
        private int _freshlyAllocated = 1;
        internal void MarkRefreshed() => System.Threading.Volatile.Write(ref _freshlyAllocated, 0);

        public TerrainMap()
            : this(new Vector2(-DefaultWidth * DefaultCellSize * 0.5f, -DefaultHeight * DefaultCellSize * 0.5f),
                   DefaultCellSize, DefaultWidth, DefaultHeight) { }

        public TerrainMap(Vector2 origin, float cellSize, int width, int height)
        {
            // Phase 6 (FIX-PLAN.md) — AUDIT-R2 §2.R2.F: degenerate dimensions cause every
            // downstream query to divide by zero (HeightAt → ±Inf, NormalAt → NaN normal,
            // SlopeAt → NaN, IsTraversable → silently always true). Reject at construction.
            if (cellSize <= 0f || float.IsNaN(cellSize) || float.IsInfinity(cellSize))
                throw new System.ArgumentOutOfRangeException(nameof(cellSize), "TerrainMap cellSize must be positive and finite.");
            if (width <= 0) throw new System.ArgumentOutOfRangeException(nameof(width), "TerrainMap width must be positive.");
            if (height <= 0) throw new System.ArgumentOutOfRangeException(nameof(height), "TerrainMap height must be positive.");
            _origin = origin;
            _cellSize = cellSize;
            _w = width;
            _h = height;
            _buffer = new DoubleBuffer<TerrainMapSnapshot>(TerrainMapSnapshot.Empty(_origin, _cellSize, _w, _h));

            // Snapshot the WaterMod height once at construction. KickStart.WaterHeight
            // calls into WaterMod.QPatch.WaterHeight which is not documented as thread-safe;
            // capturing once here keeps IsTraversable's worker-thread read pure. -9001 is
            // KickStart's "no water mod" sentinel — _waterPresent flips off in that case
            // so the gate degrades to a no-op (matching prior behavior on non-water saves).
            _waterHeight = KickStart.WaterHeight;
            _waterPresent = KickStart.isWaterModPresent && _waterHeight > -9000f;
        }

        /// <summary>
        /// Main-thread incremental refresh. Time-sliced: each call processes cells until the
        /// budget (default 2 ms) is exhausted or the pass completes, whichever comes first.
        /// The snapshot is published only when the full pass finishes — until then, readers
        /// continue to see the previously-published snapshot (initially the Empty placeholder
        /// with isPopulated=false).
        ///
        /// The full 262K-cell sweep used to run synchronously on first call, blocking the main
        /// thread for ~7 s on observed hardware (output_log.txt run, 2026-06). Time-slicing
        /// trades latency-to-fully-populated for a non-blocking main thread: at the 2 ms budget,
        /// a 512×512 grid finishes populating across roughly a few thousand frames (tens of
        /// seconds at 60 fps in the background). Per PATHING-CONTRACT §3.2 cadence is unchanged
        /// (DefaultRefreshMs = 10 s); only the per-call cost shape changed.
        /// </summary>
        public void RefreshFromMainThread(int budgetMs = DefaultRefreshBudgetMs)
        {
            try
            {
                if (ManWorld.inst == null || ManWorld.inst.TileManager == null) return;

                // Start a new pass on demand. The accumulator stays alive across ticks until
                // the pass completes; on completion it's swapped into the published snapshot
                // and a fresh array allocated for the next pass.
                if (!_refreshInProgress)
                {
                    _refreshAccumulator = new float[_w * _h];
                    _refreshCellIndex = 0;
                    _refreshInProgress = true;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int total = _w * _h;
                int idx = _refreshCellIndex;
                // Inner loop: process cells until budget exhausted or pass complete. The
                // budget check is amortized — we recheck every BudgetCheckInterval cells
                // to keep the per-cell overhead negligible.
                const int BudgetCheckInterval = 64;
                while (idx < total)
                {
                    int chunkEnd = System.Math.Min(total, idx + BudgetCheckInterval);
                    for (; idx < chunkEnd; idx++)
                    {
                        int z = idx / _w;
                        int x = idx - z * _w;
                        float worldZ = _origin.y + z * _cellSize;
                        float worldX = _origin.x + x * _cellSize;
                        _refreshAccumulator[idx] = ManWorld.inst.TileManager.GetTerrainHeightAtPosition(
                            new Vector3(worldX, 0f, worldZ), out _);
                    }
                    if (sw.ElapsedMilliseconds >= budgetMs) break;
                }
                _refreshCellIndex = idx;

                if (idx >= total)
                {
                    // Pass complete — publish atomically, clear in-progress state.
                    _buffer.Write(new TerrainMapSnapshot(
                        System.Diagnostics.Stopwatch.GetTimestamp(),
                        _origin, _cellSize, _w, _h, _refreshAccumulator, isPopulated: true));
                    _refreshAccumulator = null;
                    _refreshCellIndex = 0;
                    _refreshInProgress = false;
                    _lastRefreshEnvMs = Environment.TickCount;
                    MarkRefreshed();   // L-035: first complete refresh clears the freshly-allocated flag
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.TerrainMap.RefreshFromMainThread: " + ex.Message);
                // On exception, drop the in-progress state so the next tick restarts cleanly
                // instead of resuming from a possibly-corrupted index.
                _refreshAccumulator = null;
                _refreshCellIndex = 0;
                _refreshInProgress = false;
            }
        }

        public bool IsRefreshDue(int refreshMs = DefaultRefreshMs)
        {
            // Continue an in-progress incremental pass on every tick until it completes,
            // regardless of cadence — partial state is a transient that must finish before
            // the next cadence window opens.
            if (_refreshInProgress) return true;
            if (_lastRefreshEnvMs == int.MinValue) return true;
            int age = unchecked(Environment.TickCount - _lastRefreshEnvMs);
            return age < 0 || age >= refreshMs;
        }

        // ---- Query API (any thread) ----

        public float HeightAt(Vector2 worldXZ)
        {
            var snap = _buffer.Read();
            if (snap == null || !snap.IsPopulated) return 0f;
            // NaN/Inf guard: a NaN worldXZ component slips past the >= / < range check below
            // (NaN comparisons are always false), then (int)NaN converts to int.MinValue on
            // .NET Framework 4.6.1 — the indexer downstream throws ArgumentOutOfRangeException
            // with "Parameter name: index". Observed cascading through WeaponFireController's
            // §10.7 terrain-occlusion raycast when a target belief's Kalman state degenerated.
            if (float.IsNaN(worldXZ.x) || float.IsNaN(worldXZ.y) ||
                float.IsInfinity(worldXZ.x) || float.IsInfinity(worldXZ.y)) return 0f;
            float fx = (worldXZ.x - snap.WorldOrigin.x) / snap.CellSize;
            float fz = (worldXZ.y - snap.WorldOrigin.y) / snap.CellSize;
            if (fx < 0f || fz < 0f || fx >= snap.Width - 1 || fz >= snap.Height - 1) return 0f;
            int x0 = (int)fx, z0 = (int)fz;
            float dx = fx - x0, dz = fz - z0;
            int W = snap.Width;
            float h00 = snap.HeightSamples[z0 * W + x0];
            float h10 = snap.HeightSamples[z0 * W + (x0 + 1)];
            float h01 = snap.HeightSamples[(z0 + 1) * W + x0];
            float h11 = snap.HeightSamples[(z0 + 1) * W + (x0 + 1)];
            float h0 = h00 * (1f - dx) + h10 * dx;
            float h1 = h01 * (1f - dx) + h11 * dx;
            return h0 * (1f - dz) + h1 * dz;
        }

        public Vector3 NormalAt(Vector2 worldXZ)
        {
            var snap = _buffer.Read();
            if (snap == null || !snap.IsPopulated) return Vector3.up;
            float cs = snap.CellSize;
            float dHdx = (HeightAt(new Vector2(worldXZ.x + cs, worldXZ.y)) -
                          HeightAt(new Vector2(worldXZ.x - cs, worldXZ.y))) / (2f * cs);
            float dHdz = (HeightAt(new Vector2(worldXZ.x, worldXZ.y + cs)) -
                          HeightAt(new Vector2(worldXZ.x, worldXZ.y - cs))) / (2f * cs);
            return new Vector3(-dHdx, 1f, -dHdz).normalized;
        }

        public float SlopeAt(Vector2 worldXZ)
        {
            Vector3 n = NormalAt(worldXZ);
            return Mathf.Acos(Mathf.Clamp(n.y, -1f, 1f));
        }

        public bool IsTraversable(Vector2 worldXZ, VehicleCapability filter)
        {
            // Airplane / hover ignore ground slope and water entirely.
            if (filter.Class == VehicleClass.Airplane) return true;
            if (filter.Class == VehicleClass.Hover && filter.VerticalAuthority > 1.0f) return true;

            if (SlopeAt(worldXZ) > filter.ClimbAngleMax) return false;

            // Water gate. Ground-bound wheeled/walker techs without WaterCapable can't
            // traverse cells whose terrain height sits below the water surface (would be
            // under-water). Submarines are the inverse — they only traverse cells where
            // terrain is below the water surface. Hovers handled above. Skip entirely
            // when WaterMod isn't installed (KickStart.WaterHeight returns -9001 sentinel).
            if (_waterPresent)
            {
                float groundH = HeightAt(worldXZ);
                if (filter.Class == VehicleClass.Submarine)
                {
                    if (groundH > _waterHeight) return false;
                }
                else if (!filter.WaterCapable)
                {
                    if (groundH < _waterHeight) return false;
                }
            }
            return true;
        }

        public bool RaycastSegment(Vector3 from, Vector3 to)
        {
            // Caller can pass NaN endpoints when an upstream lead-prediction degenerates
            // (Kalman blow-up → target.PositionMean = NaN propagates through aim math).
            // Treat as "no occlusion" — the fire path then makes its decision on the rest
            // of its checks instead of aborting the whole tick.
            if (float.IsNaN(from.x) || float.IsNaN(from.y) || float.IsNaN(from.z) ||
                float.IsNaN(to.x) || float.IsNaN(to.y) || float.IsNaN(to.z) ||
                float.IsInfinity(from.x) || float.IsInfinity(from.y) || float.IsInfinity(from.z) ||
                float.IsInfinity(to.x) || float.IsInfinity(to.y) || float.IsInfinity(to.z))
                return true;
            const int Samples = 12;
            for (int i = 1; i < Samples; i++)
            {
                float t = i / (float)Samples;
                Vector3 p = Vector3.Lerp(from, to, t);
                float h = HeightAt(new Vector2(p.x, p.z));
                if (p.y < h - 0.5f) return false;
            }
            return true;
        }

        /// <summary>
        /// Terrain biasing for WORLD-CONTRACT §4.2 belief-decay anisotropy.
        /// Returns three orthogonal axes describing the local belief-decay shape: the first
        /// axis is compressed along the uphill direction (techs less likely to disappear into
        /// steep terrain); second is vertical; third is the slope-perpendicular lateral axis.
        ///
        /// v0.1.0: compression = 1/(1 + 2*slope). Flat terrain → identity. Steep terrain →
        /// ~0.3× spread uphill, full lateral spread.
        /// </summary>
        public Vector3[] TerrainBiasingShape(Vector3 meanPosition, float spreadRadius)
        {
            var xz = new Vector2(meanPosition.x, meanPosition.z);
            Vector3 n = NormalAt(xz);
            float slope = SlopeAt(xz);

            Vector3 uphillXZ = new Vector3(-n.x, 0f, -n.z);
            if (uphillXZ.sqrMagnitude < 1e-6f || slope < 1e-3f)
                return new[] { Vector3.right, Vector3.up, Vector3.forward };

            uphillXZ.Normalize();
            Vector3 lateral = Vector3.Cross(Vector3.up, uphillXZ).normalized;
            float compression = 1f / (1f + 2f * slope);
            return new[] { uphillXZ * compression, Vector3.up, lateral };
        }
    }
}
