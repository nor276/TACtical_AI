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
    public static class AIFormRegistry
    {
        public const string DefaultFormId = "Modified";

        private static readonly Dictionary<string, IAIForm> forms = new Dictionary<string, IAIForm>();
        private static IAIForm active;

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
            if (!string.IsNullOrEmpty(id) && forms.TryGetValue(id, out var f))
            {
                active = f; ActiveId = id; return;
            }
            if (forms.TryGetValue(DefaultFormId, out var def))
            {
                active = def; ActiveId = DefaultFormId; return;
            }
            using (var en = forms.Values.GetEnumerator())
                if (en.MoveNext()) { active = en.Current; ActiveId = active.Id; }
        }

        public static void Clear() { forms.Clear(); active = null; }
    }
}
