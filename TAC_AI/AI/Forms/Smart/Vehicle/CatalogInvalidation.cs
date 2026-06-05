using System;
using TerraTechETCUtil;  // InvokeHelper

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P2 Item 9: hot-reload invalidation surface for the typed block catalog.
    ///
    /// Subscribes <see cref="OnAllInvalidated"/> to <c>InvokeHelper.BlocksPostChangeEvent</c>
    /// — the engine event that fires when the block table changes (mod load / unload /
    /// hot reload). Confirmed live: KickStart.cs:654, 729, 829 already subscribe to this
    /// same event to refresh their own caches. RegisterHotReloadHook is idempotent: a
    /// re-subscribe guard prevents duplicate fires if Init runs multiple times.
    /// </summary>
    public static class CatalogInvalidation
    {
        /// <summary>
        /// Raised when a specific typeKey is invalidated. Subscribers (e.g. live ChassisCaptures)
        /// can drop their cached per-type derivations and force a re-probe on next sighting.
        /// </summary>
        public static event Action<int> OnTypeInvalidated;

        /// <summary>
        /// Raised when ALL types are invalidated (mod hot-reload, world reset).
        /// </summary>
        public static event Action OnAllInvalidated;

        private static bool _hookRegistered;
        private static readonly object _gate = new object();

        /// <summary>
        /// Subscribe to <c>InvokeHelper.BlocksPostChangeEvent</c> so any mod hot-reload that
        /// changes the block table fires <see cref="OnAllInvalidated"/>. Idempotent — only
        /// subscribes once per process even if called multiple times (e.g. SmartRuntime.Init
        /// re-runs across mode switches).
        /// </summary>
        public static void RegisterHotReloadHook()
        {
            lock (_gate)
            {
                if (_hookRegistered) return;
                _hookRegistered = true;
            }
            try
            {
                InvokeHelper.BlocksPostChangeEvent.Subscribe(OnBlocksPostChange);
                DebugTAC_AI.Log("[CHASSIS] CatalogInvalidation subscribed to InvokeHelper.BlocksPostChangeEvent.");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly(
                    "catalog-invalidate-subscribe",
                    "CatalogInvalidation.RegisterHotReloadHook subscribe failed "
                    + ex.GetType().Name + ": " + ex.Message
                    + " -- manual OnExternalHotReload still works.");
            }
        }

        // Engine callback: fires after blocks change (mod load / unload / hot reload).
        // Treat as full invalidation since we don't know which specific types churned.
        private static void OnBlocksPostChange()
        {
            DebugTAC_AI.Log("[CHASSIS] BlocksPostChangeEvent fired -- invalidating catalog.");
            OnExternalHotReload();
        }

        /// <summary>
        /// Public entry point for external hot-reload triggers (mod console command,
        /// tools UI, etc.). Fires <see cref="OnAllInvalidated"/>; subscribers should
        /// clear their cached state. The TypedBlockCatalog itself listens (via
        /// SmartRuntime hookup at Init time) and calls its Reset().
        /// </summary>
        public static void OnExternalHotReload()
        {
            try { OnAllInvalidated?.Invoke(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly(
                    "catalog-invalidate-fatal",
                    "CatalogInvalidation.OnExternalHotReload subscriber threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            DebugTAC_AI.Log("[CHASSIS] CatalogInvalidation.OnExternalHotReload fired.");
        }

        /// <summary>
        /// Targeted single-type invalidation. Subscribers may drop their cached
        /// per-type derivations selectively without flushing everything.
        /// </summary>
        public static void InvalidateType(int typeKey)
        {
            try { OnTypeInvalidated?.Invoke(typeKey); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly(
                    "catalog-invalidate-type-fatal",
                    "CatalogInvalidation.InvalidateType(" + typeKey + ") subscriber threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
