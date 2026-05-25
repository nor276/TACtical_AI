using System;
using System.Collections.Generic;
using TerraTechETCUtil;

namespace TAC_AI.AI.Tunables
{
    /// <summary>
    /// The central registry. Catalog files declare tunables here once; readers resolve O(1) typed handles.
    /// Step 1 owns the value in-process. Step 6 binds each tunable to a persisted backing field
    /// (ModHelper.BindConfig) and Step 7 auto-generates the in-game menu from the registrations (honoring
    /// MenuVisible/MenuLabel). DeInitAll() drops registrations + runs WorldAffecting restores for re-init symmetry.
    /// </summary>
    public static class TunableRegistry
    {
        private static readonly Dictionary<string, Tunable> byKey = new Dictionary<string, Tunable>();
        private static readonly List<Tunable> ordered = new List<Tunable>();

        public static IReadOnlyList<Tunable> All => ordered;

        private static Tunable AddOrGet(string key, string category, TunableKind kind)
        {
            if (byKey.TryGetValue(key, out var existing))
            {
                if (existing.Kind != kind)
                    DebugTAC_AI.Exception("TunableRegistry: key '" + key + "' re-registered with a different kind.");
                return existing;
            }
            var t = new Tunable(key, category, kind);
            byKey.Add(key, t);
            ordered.Add(t);
            return t;
        }

        public static FloatHandle RegisterFloat(string key, string category, float defaultValue,
            float min, float max, float step, TunableApplyMode applyMode = TunableApplyMode.LiveSafe,
            bool menuVisible = true, string menuLabel = null, Func<float, string> display = null,
            Action onApply = null, Action onDeInit = null, string[] readSites = null, Action<float> bind = null)
        {
            var t = AddOrGet(key, category, TunableKind.Float);
            t.ApplyMode = applyMode;
            t.MenuVisible = menuVisible;
            t.MenuLabel = menuLabel;
            t.Min = min; t.Max = max; t.Step = step;
            t.Display = display;
            t.OnApply = onApply;
            t.OnDeInit = onDeInit;
            t.ReadSites = readSites;
            t.DefaultFloat = defaultValue;
            t.CurFloat = defaultValue;
            t.BindFloat = bind;
            bind?.Invoke(defaultValue);   // sync the backing static field to the default
            return new FloatHandle(t);
        }

        public static IntHandle RegisterInt(string key, string category, int defaultValue,
            int min, int max, int step = 1, TunableApplyMode applyMode = TunableApplyMode.LiveSafe,
            bool menuVisible = true, string menuLabel = null,
            Action onApply = null, Action onDeInit = null, string[] readSites = null, Action<int> bind = null)
        {
            var t = AddOrGet(key, category, TunableKind.Int);
            t.ApplyMode = applyMode;
            t.MenuVisible = menuVisible;
            t.MenuLabel = menuLabel;
            t.Min = min; t.Max = max; t.Step = step;
            t.OnApply = onApply;
            t.OnDeInit = onDeInit;
            t.ReadSites = readSites;
            t.DefaultInt = defaultValue;
            t.CurInt = defaultValue;
            t.BindInt = bind;
            bind?.Invoke(defaultValue);
            return new IntHandle(t);
        }

        public static BoolHandle RegisterBool(string key, string category, bool defaultValue,
            TunableApplyMode applyMode = TunableApplyMode.LiveSafe,
            bool menuVisible = true, string menuLabel = null,
            Action onApply = null, Action onDeInit = null, string[] readSites = null, Action<bool> bind = null)
        {
            var t = AddOrGet(key, category, TunableKind.Bool);
            t.ApplyMode = applyMode;
            t.MenuVisible = menuVisible;
            t.MenuLabel = menuLabel;
            t.OnApply = onApply;
            t.OnDeInit = onDeInit;
            t.ReadSites = readSites;
            t.DefaultBool = defaultValue;
            t.CurBool = defaultValue;
            t.BindBool = bind;
            bind?.Invoke(defaultValue);
            return new BoolHandle(t);
        }

        // --- live reads / writes ---
        public static bool TryGet(string key, out Tunable t) => byKey.TryGetValue(key, out t);

        /// <summary>Set a value and, if it actually changed, fire OnApply (per-descriptor dirty-tracking).</summary>
        public static void SetFloat(string key, float value)
        {
            if (!byKey.TryGetValue(key, out var t) || t.Kind != TunableKind.Float) return;
            value = Mathf_Clamp(value, t.Min, t.Max);
            if (t.CurFloat == value) return;
            t.CurFloat = value;
            t.BindFloat?.Invoke(value);
            t.OnApply?.Invoke();
        }
        public static void SetInt(string key, int value)
        {
            if (!byKey.TryGetValue(key, out var t) || t.Kind != TunableKind.Int) return;
            value = (int)Mathf_Clamp(value, t.Min, t.Max);
            if (t.CurInt == value) return;
            t.CurInt = value;
            t.BindInt?.Invoke(value);
            t.OnApply?.Invoke();
        }
        public static void SetBool(string key, bool value)
        {
            if (!byKey.TryGetValue(key, out var t) || t.Kind != TunableKind.Bool) return;
            if (t.CurBool == value) return;
            t.CurBool = value;
            t.BindBool?.Invoke(value);
            t.OnApply?.Invoke();
        }

        /// <summary>Run every WorldAffecting restore (host re-init symmetry); does not clear registrations.</summary>
        public static void DeInitRestores()
        {
            foreach (var t in ordered)
                t.OnDeInit?.Invoke();
        }

        // tiny local clamp so this file has no UnityEngine dependency at the registry layer
        private static float Mathf_Clamp(float v, float min, float max)
        {
            if (min == 0f && max == 0f) return v; // bounds not set
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }

    /// <summary>Short read-side facade. Resolve a handle once (at install), then read Value per-frame O(1).</summary>
    public static class Tune
    {
        public static FloatHandle Float(string key)
        {
            if (TunableRegistry.TryGet(key, out var t) && t.Kind == TunableKind.Float)
                return new FloatHandle(t);
            DebugTAC_AI.Exception("Tune.Float: unknown/mismatched key '" + key + "'");
            return default(FloatHandle);
        }
        public static IntHandle Int(string key)
        {
            if (TunableRegistry.TryGet(key, out var t) && t.Kind == TunableKind.Int)
                return new IntHandle(t);
            DebugTAC_AI.Exception("Tune.Int: unknown/mismatched key '" + key + "'");
            return default(IntHandle);
        }
        public static BoolHandle Bool(string key)
        {
            if (TunableRegistry.TryGet(key, out var t) && t.Kind == TunableKind.Bool)
                return new BoolHandle(t);
            DebugTAC_AI.Exception("Tune.Bool: unknown/mismatched key '" + key + "'");
            return default(BoolHandle);
        }
    }
}
