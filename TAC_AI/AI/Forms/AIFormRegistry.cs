using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using TerraTechETCUtil;

namespace TAC_AI.AI.Forms
{
    /// <summary>
    /// Discovers and publishes the AI forms, and tracks the globally-selected ACTIVE form. ScanAndRegister scans
    /// the assembly for IAIForm implementations (discovered by existing - no central registration), instantiates
    /// each (parameterless ctor, stateless singleton) and keys it by Id. The in-game selector lists All; ProfileRunner
    /// dispatches to Active. ScanAndRegister is ReflectionTypeLoadException-safe (optional soft-referenced deps).
    /// </summary>
    public static partial class AIFormRegistry
    {
        public const string DefaultFormId = "Modified";

        private static readonly Dictionary<string, IAIForm> forms = new Dictionary<string, IAIForm>();
        private static IAIForm active;

        // L-027: every routed tech lands here keyed by TankAIHelper.GetInstanceID(). Read by
        // L-053 DrawRoutingDebugGUI overlay + L-081 RoutingCompletenessCatcher walk. Removed
        // by L-052 Recycled. ConcurrentDictionary because the funnel can fire from DelayedSubscribe
        // (Invoke 0.1s, main thread), OnPreUpdate reclaim (main thread), or SetActive
        // drain (main thread but possibly during a form-swap mid-frame).
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, RoutingDecision> LiveRoutings
            = new System.Collections.Concurrent.ConcurrentDictionary<int, RoutingDecision>();

        /// <summary>The currently-selected form id (persisted by the options layer). Defaults to Modified.</summary>
        public static string ActiveId { get; private set; } = DefaultFormId;
        public static IAIForm Active => active;
        public static IEnumerable<IAIForm> All => forms.Values;
        public static int Count => forms.Count;

        public static void ScanAndRegister()
        {
            forms.Clear();
            Type[] types;
            try { types = Assembly.GetExecutingAssembly().GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IAIForm).IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                try
                {
                    var form = (IAIForm)Activator.CreateInstance(t);
                    if (!string.IsNullOrEmpty(form.Id))
                        forms[form.Id] = form;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogError("AIFormRegistry: failed to register " + t.FullName + " - " + ex.Message);
                }
            }
            SetActive(ActiveId);   // keep current selection if still present, else fall back to default/first
            DebugTAC_AI.Log("AIFormRegistry: registered " + forms.Count + " AI form(s); active=" + ActiveId);
        }

        public static bool TryGet(string id, out IAIForm form) => forms.TryGetValue(id, out form);

        /// <summary>Select the active form. Falls back to Modified, then to any registered form, if id is unknown.</summary>
        public static void SetActive(string id)
        {
            // resolve the next form (requested id -> Modified default -> any registered)
            IAIForm next = null; string nextId = null;
            if (!string.IsNullOrEmpty(id) && forms.TryGetValue(id, out var f)) { next = f; nextId = id; }
            else if (forms.TryGetValue(DefaultFormId, out var def)) { next = def; nextId = DefaultFormId; }
            else { using (var en = forms.Values.GetEnumerator()) if (en.MoveNext()) { next = en.Current; nextId = next.Id; } }
            if (next == null) return;
            if (ReferenceEquals(next, active)) { ActiveId = nextId; return; }   // no change
            // v2: hand the old form's per-tech state down and bring the new form up (e.g. Vanilla's stock-AI handback)
            string oldId = active != null ? active.Id : "<null>";

            // L-054 teardown step 1: walk live helpers + call OLD form's OnTechRecycle for each
            // so per-tech FormState is released cleanly. Uses AIECore.IterateAllHelpers
            // (mirror pattern at SmartForm.cs:104) — the canonical live-helper enumeration.
            var oldForm = active;
            if (oldForm != null)
            {
                try
                {
                    foreach (var helper in AIECore.IterateAllHelpers())
                    {
                        if (helper == null) continue;
                        try { oldForm.OnTechRecycle(helper); }
                        catch (System.Exception e) { DebugTAC_AI.LogWarning("AIFormRegistry.SetActive: drain OnTechRecycle failed - " + e.Message); }
                    }
                }
                catch (System.Exception e) { DebugTAC_AI.LogWarning("AIFormRegistry.SetActive: drain iteration failed - " + e.Message); }
            }

            try { oldForm?.DeInitGlobal(); } catch (System.Exception e) { DebugTAC_AI.LogError("AIFormRegistry: DeInitGlobal failed - " + e.Message); }
            active = next; ActiveId = nextId;
            try { active.InitGlobal(); } catch (System.Exception e) { DebugTAC_AI.LogError("AIFormRegistry: InitGlobal failed - " + e.Message); }

            // L-054 step 3: re-route every live helper into the new active form. RouteTech
            // is idempotent (FormId==ActiveId fast-path) — since we just set ActiveId to
            // nextId, the funnel will actually dispatch OnTechSpawn (helpers' prior FormId
            // was oldId). Each helper gets Reason=FormSwapped stamped.
            try
            {
                foreach (var helper in AIECore.IterateAllHelpers())
                {
                    if (helper == null) continue;
                    try { RouteTech(helper, "FormSwapped"); }
                    catch (System.Exception e) { DebugTAC_AI.LogWarning("AIFormRegistry.SetActive: re-route failed - " + e.Message); }
                }
            }
            catch (System.Exception e) { DebugTAC_AI.LogWarning("AIFormRegistry.SetActive: re-route iteration failed - " + e.Message); }

            DebugTAC_AI.Log("AIFormRegistry.SetActive: '" + oldId + "' -> '" + nextId + "' (requested='" + id + "')");
        }

        /// <summary>
        /// L-027: single funnel for routing a tech to the active form. Replaces direct
        /// `Active?.OnTechSpawn(helper)` call sites — the funnel adds:
        ///   - idempotency: second call with FormId==ActiveId is a no-op fast-path
        ///   - exception wrap: active form's OnTechSpawn throws → fall back to DefaultFormId
        ///     and stamp Reason=OnTechSpawnFailed (matches L-053 color-code key)
        ///   - LiveRoutings stamp: every routed helper appears in the overlay
        ///   - [TECH-ROUTE] file-only dedup log (one per (instanceID, reason) pair)
        ///
        /// Calling with a null helper or no active form is a silent no-op (matches the
        /// pre-funnel `Active?.OnTechSpawn(helper)` shape).
        /// </summary>
        public static void RouteTech(TankAIHelper helper, string reason)
        {
            if (helper == null) return;
            int key = helper.GetInstanceID();
            string targetFormId = ActiveId;

            // Idempotency fast-path: already routed to the current active form, refresh the
            // timestamp + reason but skip OnTechSpawn (which is NOT internally idempotent on
            // every form — ModifiedForm.OnTechSpawn re-allocates ModifiedTechState
            // unconditionally, dropping combat-cycle state).
            if (helper.Routing.FormId == targetFormId)
            {
                helper.Routing = new RoutingDecision
                {
                    FormId = targetFormId,
                    TimestampMs = System.Environment.TickCount,
                    Reason = reason ?? "Refresh",
                };
                LiveRoutings[key] = helper.Routing;
                return;
            }

            // Dispatch to active form.
            string actualFormId = targetFormId;
            string actualReason = reason ?? "Routed";
            string excType = null, excMsg = null;
            try
            {
                if (active != null)
                {
                    active.OnTechSpawn(helper);
                }
                else
                {
                    actualFormId = null;
                    actualReason = "NoActiveForm";
                }
            }
            catch (System.Exception ex)
            {
                actualReason = "OnTechSpawnFailed";
                excType = ex.GetType().Name;
                excMsg = ex.Message;
                // Fall back to DefaultFormId so the tech runs the well-tested form.
                if (!string.IsNullOrEmpty(DefaultFormId) && forms.TryGetValue(DefaultFormId, out var fallback)
                    && !ReferenceEquals(fallback, active))
                {
                    try
                    {
                        fallback.OnTechSpawn(helper);
                        actualFormId = DefaultFormId;
                    }
                    catch (System.Exception ex2)
                    {
                        DebugTAC_AI.LogError("AIFormRegistry.RouteTech: fallback to '"
                            + DefaultFormId + "' also threw " + ex2.GetType().Name + ": " + ex2.Message);
                        actualFormId = null;
                    }
                }
                else
                {
                    actualFormId = null;
                }
            }

            helper.Routing = new RoutingDecision
            {
                FormId = actualFormId,
                TimestampMs = System.Environment.TickCount,
                Reason = actualReason,
                ExceptionType = excType,
                ExceptionMessage = excMsg,
            };
            LiveRoutings[key] = helper.Routing;

            // Per user AI-warning-routing-preference: file-only, per-key dedup, no popup.
            DebugTAC_AI.LogWarnFileOnly("tech-route-" + key + "-" + actualReason,
                "[TECH-ROUTE] tech=" + key + " form=" + (actualFormId ?? "<unrouted>")
                + " reason=" + actualReason
                + (excType != null ? " exc=" + excType + ":" + excMsg : ""));
        }

        // L-006: close OD-10. Mod-unload / final shutdown reaches Clear() but historically
        // skipped DeInitGlobal — leaving Smart's workers alive past the form-active window
        // (only IsBackground=true prevented the freeze). DeInitGlobal must remain idempotent
        // (it is — SmartRuntime.Shutdown guards on Pool==null). Log before/after so the
        // Clear-vs-SetActive shutdown path is distinguishable in output_log.txt.
        public static void Clear()
        {
            string oldId = active != null ? active.Id : "<null>";
            try { active?.DeInitGlobal(); }
            catch (System.Exception e) { DebugTAC_AI.LogError("AIFormRegistry.Clear: DeInitGlobal failed - " + e.Message); }
            forms.Clear();
            active = null;
            DebugTAC_AI.Log("AIFormRegistry.Clear: drained active form '" + oldId + "'");
        }
    }
}
