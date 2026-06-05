using System;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart
{
    /// <summary>
    /// L-025: DontDestroyOnLoad MonoBehaviour that translates Unity process-lifecycle
    /// signals into <see cref="SmartRuntime"/> state mutations.
    ///
    /// Subscribed signals (any one fires SmartRuntime.RequestShutdown — the latch in
    /// L-004 dedupes so multiple deliveries reduce to one Shutdown):
    ///   - <c>UnityEngine.Application.quitting</c>          — graceful close
    ///   - <c>Singleton.Manager&lt;Singleton&gt;.inst.ApplicationQuitEvent</c> — engine quit signal
    ///   - <c>OnDestroy</c>                                  — third fallback (game tear-down)
    ///
    /// Pause signals (flip SmartRuntime.IsPaused so daemons gate work; existing TAE catches
    /// remain as last-resort defense):
    ///   - <c>OnApplicationPause(bool paused)</c>
    ///   - <c>OnApplicationFocus(bool focused)</c>
    ///
    /// Install ordering per L-007: SmartForm.InitGlobal MUST call Install BEFORE
    /// SmartRuntime.Init so OnApplicationPause hits a live runtime. Uninstall in
    /// DeInitGlobal after Shutdown so any pause that fires during teardown is benign.
    ///
    /// Subscribed callbacks are static methods (no closures) so the
    /// Application.quitting subscription cannot leak SmartForm instances. SmartForm runs
    /// once per session, so leaking would be a one-off — but the discipline is free.
    /// </summary>
    public sealed class SmartLifecycleShim : MonoBehaviour
    {
        private const string ShimGameObjectName = "Smart.LifecycleShim";

        private static SmartLifecycleShim _instance;
        // Track whether we've subscribed so Uninstall is idempotent.
        private static bool _quittingSubscribed;
        private static bool _engineQuitSubscribed;

        /// <summary>
        /// Idempotent. Creates the DontDestroyOnLoad shim GameObject if not already present
        /// and subscribes the static quit hooks. Safe to call multiple times — second call
        /// detects the existing GameObject and no-ops.
        /// </summary>
        public static void Install()
        {
            try
            {
                if (_instance == null)
                {
                    // Re-find an existing shim across a possible Smart re-init.
                    var existing = GameObject.Find(ShimGameObjectName);
                    if (existing != null)
                    {
                        _instance = existing.GetComponent<SmartLifecycleShim>();
                    }
                    if (_instance == null)
                    {
                        var go = new GameObject(ShimGameObjectName);
                        UnityEngine.Object.DontDestroyOnLoad(go);
                        _instance = go.AddComponent<SmartLifecycleShim>();
                    }
                }

                if (!_quittingSubscribed)
                {
                    Application.quitting += OnApplicationQuitting_Static;
                    _quittingSubscribed = true;
                }

                if (!_engineQuitSubscribed)
                {
                    try
                    {
                        // Decompiled at F:/Tac-AI/Decompiled/AssemblyCSharp/Singleton.cs:249-252
                        // exposes ApplicationQuitEvent as a STATIC EventNoParams on Singleton.
                        // Subscribe takes a delegate; we use the same static method reference
                        // so unsubscribe finds the identical handle.
                        Singleton.ApplicationQuitEvent.Subscribe(OnEngineApplicationQuit_Static);
                        _engineQuitSubscribed = true;
                    }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarning("SmartLifecycleShim.Install: "
                            + "Singleton.ApplicationQuitEvent subscribe failed: " + ex.Message);
                    }
                }

                // L-078: QuitSaveCoordinator subscribes AppDomain.ProcessExit + UnhandledException.
                TAC_AI.AI.Forms.Smart.Learning.QuitSaveCoordinator.Install();

                DebugTAC_AI.Log("SmartLifecycleShim.Install: complete (quitting="
                    + _quittingSubscribed + ", engineQuit=" + _engineQuitSubscribed + ")");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartLifecycleShim.Install: " + ex.Message);
            }
        }

        /// <summary>
        /// Idempotent. Unsubscribes hooks and destroys the GameObject. Safe to call without
        /// a prior Install. Order is intentional: unsubscribe BEFORE destroying so an
        /// engine event that fires mid-Uninstall doesn't reach a dead instance.
        /// </summary>
        public static void Uninstall()
        {
            try
            {
                if (_quittingSubscribed)
                {
                    try { Application.quitting -= OnApplicationQuitting_Static; }
                    catch { /* event may already be teardown */ }
                    _quittingSubscribed = false;
                }
                if (_engineQuitSubscribed)
                {
                    try { Singleton.ApplicationQuitEvent.Unsubscribe(OnEngineApplicationQuit_Static); }
                    catch { /* event may be torn down */ }
                    _engineQuitSubscribed = false;
                }
                if (_instance != null)
                {
                    try { DestroyImmediate(_instance.gameObject); }
                    catch { /* may already be destroyed */ }
                    _instance = null;
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("SmartLifecycleShim.Uninstall: " + ex.Message);
            }
        }

        // ---- Unity message hooks (instance methods on the MonoBehaviour) ----

        private void OnApplicationPause(bool paused)
        {
            SmartRuntime.IsPaused = paused;
            DebugTAC_AI.LogWarnFileOnly("smart-lifecycle-pause",
                "SmartLifecycleShim: OnApplicationPause(" + paused + ")");
        }

        private void OnApplicationFocus(bool focused)
        {
            // Focus loss is functionally equivalent to pause for our purposes — daemons
            // should yield the host process whether the user alt-tabbed or explicitly
            // paused. !focused → IsPaused=true.
            SmartRuntime.IsPaused = !focused;
            DebugTAC_AI.LogWarnFileOnly("smart-lifecycle-focus",
                "SmartLifecycleShim: OnApplicationFocus(" + focused + ")");
        }

        private void OnDestroy()
        {
            DebugTAC_AI.Log("SmartLifecycleShim.OnDestroy — requesting shutdown");
            SmartRuntime.RequestShutdown("SmartLifecycleShim.OnDestroy");
        }

        // ---- Static handlers (no closure → no SmartForm leak) ----

        private static void OnApplicationQuitting_Static()
        {
            try
            {
                // L-078: quit-save fires before shutdown so workers are still alive to flush.
                TAC_AI.AI.Forms.Smart.Learning.QuitSaveCoordinator.Flush(
                    TAC_AI.AI.Forms.Smart.Learning.QuitSaveCoordinator.DefaultDeadlineMs,
                    "Application.quitting");
            }
            catch (Exception ex) { DebugTAC_AI.LogWarning("QuitSaveCoordinator.Flush: " + ex.Message); }
            try { SmartRuntime.RequestShutdown("Application.quitting"); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("SmartLifecycleShim.quitting: " + ex.Message); }
        }

        private static void OnEngineApplicationQuit_Static()
        {
            try
            {
                TAC_AI.AI.Forms.Smart.Learning.QuitSaveCoordinator.Flush(
                    TAC_AI.AI.Forms.Smart.Learning.QuitSaveCoordinator.DefaultDeadlineMs,
                    "Singleton.ApplicationQuitEvent");
            }
            catch (Exception ex) { DebugTAC_AI.LogWarning("QuitSaveCoordinator.Flush: " + ex.Message); }
            try { SmartRuntime.RequestShutdown("Singleton.ApplicationQuitEvent"); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("SmartLifecycleShim.engineQuit: " + ex.Message); }
        }
    }
}
