#if SMART_DEV
using System;
using System.Collections;
using System.Threading;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Training
{
    /// <summary>
    /// Per FIX-PLAN.md Phase 2.3: the harness's host MonoBehaviour. Before this file
    /// existed, <see cref="SelfPlayHarness.BeginAsync"/> required a caller-supplied
    /// MonoBehaviour but no Smart type was a MonoBehaviour (AUDIT-R2 §1.R2-I). The
    /// driver creates itself lazily on a hidden DontDestroyOnLoad GameObject, hosts
    /// the harness coroutine, runs the main-thread debug overlay, and provides the
    /// `smart-train &lt;N&gt;` entry point.
    ///
    /// Per Plan 1 insight #10 + Plan 8 insight #5: the driver's Update() is also the
    /// natural drain point for cross-thread WorldEventBus publishes routed through
    /// the main-thread marshal queue (added in Phase 3). In release builds (no
    /// SMART_DEV defined), the marshal queue is drained by SmartForm.Operations
    /// instead.
    ///
    /// Lifecycle:
    /// - <see cref="Begin"/> creates the host (if absent), starts the harness coroutine,
    ///   sets <see cref="Active"/> = true.
    /// - <see cref="Stop"/> requests cancellation; the coroutine drains and Active flips
    ///   to false on completion.
    /// - The host GameObject persists across scene loads via DontDestroyOnLoad — Smart
    ///   sessions can span multiple missions during a long training run.
    /// </summary>
    internal sealed class SmartTrainingDriver : MonoBehaviour
    {
        private const string HostGameObjectName = "SmartTrainingDriver";
        private static SmartTrainingDriver _instance;
        private static readonly object _instanceLock = new object();

        public static SmartTrainingDriver Instance => _instance;
        public bool Active { get; private set; }
        public SelfPlayHarness Harness { get; private set; }

        private Coroutine _running;
        private CancellationTokenSource _cts;
        private bool _showOverlay = true;

        /// <summary>
        /// Resolve (or lazily create) the singleton host. Safe to call from any
        /// main-thread context.
        /// </summary>
        public static SmartTrainingDriver GetOrCreate()
        {
            lock (_instanceLock)
            {
                if (_instance != null) return _instance;

                var existing = GameObject.Find(HostGameObjectName);
                GameObject host = existing != null ? existing : new GameObject(HostGameObjectName);
                DontDestroyOnLoad(host);

                _instance = host.GetComponent<SmartTrainingDriver>();
                if (_instance == null) _instance = host.AddComponent<SmartTrainingDriver>();
                return _instance;
            }
        }

        /// <summary>
        /// Begin a self-play session for <paramref name="maxGenerations"/> CMA-ES
        /// generations. Idempotent — if a session is already running, the call is a
        /// no-op and logs a warning.
        /// </summary>
        public void Begin(int maxGenerations, int seed = 12345)
        {
            if (Active)
            {
                DebugTAC_AI.LogWarning("SmartTrainingDriver: session already active; ignoring Begin().");
                return;
            }
            // Refuse to run in networked sessions — Time.timeScale change would desync MP per
            // FIX-PLAN.md Phase 5. Phase 5 hardens the inner guard too; this is the early gate.
            if (IsMultiplayerSession())
            {
                DebugTAC_AI.LogWarning("SmartTrainingDriver: refusing to start self-play in a multiplayer session.");
                return;
            }
            Harness = SelfPlayHarness.FromDefaults(seed);
            _cts = new CancellationTokenSource();
            Active = true;
            _running = StartCoroutine(RunSession(maxGenerations, _cts.Token));
        }

        /// <summary>Request cancellation of the active session. Safe to call when idle.</summary>
        public void Stop()
        {
            if (!Active) return;
            _cts?.Cancel();
        }

        public void ToggleOverlay() => _showOverlay = !_showOverlay;

        /// <summary>
        /// Phase 10 (FIX-PLAN.md): run the in-process test suite. Logs a summary and
        /// returns it so dev tooling can surface pass/fail counts. Safe to call from
        /// the OnGUI button.
        /// </summary>
        public string RunTests() => Tests.SmartTestSuite.RunAll();

        private IEnumerator RunSession(int maxGenerations, CancellationToken ct)
        {
            DebugTAC_AI.Log("SmartTrainingDriver: session begin — generations=" + maxGenerations);
            yield return Harness.RunCoroutine(this, maxGenerations, ct);
            DebugTAC_AI.Log("SmartTrainingDriver: session end — generation=" + Harness.Search.Generation
                            + " bestMeanScore=" + Harness.BestKnownMeanScore.ToString("F3"));
            Active = false;
            _running = null;
            _cts?.Dispose();
            _cts = null;
        }

        private static bool IsMultiplayerSession()
        {
            try { return ManNetwork.inst != null && ManNetwork.inst.IsMultiplayer; }
            catch { return false; }
        }

        // ----------------------- Main-thread per-frame work -----------------------

        private void Update()
        {
            // Phase 3.2 (FIX-PLAN.md): drain the WorldEventBus marshal queue every
            // frame in dev builds. SmartForm.Operations also drains, but only when
            // a Smart-driven tech is ticking — the per-frame drain here keeps the
            // queue from accumulating during scenes where Smart has no live techs.
            World.WorldEventBus.DrainMainThreadQueue();
        }

        // ----------------------- Debug overlay -----------------------

        private void OnGUI()
        {
            if (!_showOverlay) return;
            const int W = 360;
            int y = 6;
            GUI.Box(new Rect(4, 4, W, 180), GUIContent.none);
            GUI.Label(new Rect(10, y, W - 12, 18), "Smart Training Driver  [Active=" + Active + "]");
            y += 18;
            if (Harness != null)
            {
                GUI.Label(new Rect(10, y, W - 12, 18),
                    "Gen " + Harness.Search.Generation
                    + "  matchCount=" + Harness.MatchCounter);
                y += 18;
                GUI.Label(new Rect(10, y, W - 12, 18),
                    "BestMeanScore=" + Harness.BestKnownMeanScore.ToString("F3")
                    + "  bestGen=" + Harness.BestKnownGeneration);
                y += 18;
            }
            // SmartRuntime snapshot — provides a coarse view of pool + world state.
            string runtime = SmartRuntime.IsRunning ? "yes" : "no";
            GUI.Label(new Rect(10, y, W - 12, 18), "SmartRuntime.IsRunning=" + runtime);
            y += 18;
            if (SmartRuntime.IsRunning && SmartRuntime.Pool != null)
            {
                GUI.Label(new Rect(10, y, W - 12, 18),
                    "Workers=" + SmartRuntime.Pool.WorkerCount);
                y += 18;
            }
            if (PathingService.IsRunning && PathingService.CurrentTerrain != null)
            {
                var snap = PathingService.CurrentTerrain.Buffer.Read();
                GUI.Label(new Rect(10, y, W - 12, 18),
                    "Terrain populated=" + snap.IsPopulated
                    + "  cells=" + (snap.Width * snap.Height));
                y += 18;
            }
            if (GUI.Button(new Rect(10, y, 100, 22), "Run Tests"))
            {
                string s = RunTests();
                DebugTAC_AI.Log(s);
            }
            // Phase 10 (FIX-PLAN.md): user-facing entry points to the training pipeline.
            // Without these the driver is dead code from a developer-UI perspective —
            // RunCoroutine has to be called by something, and there's no console binding
            // for it. Cheap to start (5 generations) so the dev can sanity-check the
            // pipeline before committing to a long run.
            if (!Active)
            {
                if (GUI.Button(new Rect(116, y, 100, 22), "Begin (5 gens)")) Begin(5);
                if (GUI.Button(new Rect(222, y, 130, 22), "Begin (50 gens)")) Begin(50);
            }
            else
            {
                if (GUI.Button(new Rect(116, y, 100, 22), "Stop")) Stop();
            }
        }
    }
}
#endif
