using System;
using System.Collections.Generic;
using UnityEngine;
using TerraTechETCUtil;
using Nuterra.NativeOptions;
using TAC_AI.AI.Engine;
using TAC_AI.AI.Forms;

namespace TAC_AI.AI.Tunables
{
    /// <summary>
    /// Auto-generates the in-game options menu from the tunable registry (Step 7). One control per visible
    /// tunable - a slider for float/int (SuperNativeOptions.OptionRangeAutoDisplay) or a toggle for bool - placed
    /// in a tab keyed by the tunable's Category, labelled by ResolvedLabel (menuLabel ?? key), and routed back
    /// through TunableRegistry.Set* on save (which clamps, binds the backing AIGlobals static, and fires apply).
    /// menuVisible=false knobs are skipped (still registered + persisted, just hidden). Self-ensures the registry
    /// is populated (idempotent InitAIModules) so it works regardless of call order. Idempotent.
    /// </summary>
    public static class TunableOptionsPublisher
    {
        private static bool published;

        public static void Publish()
        {
            if (published) return;
            published = true;
            AIModuleBootstrap.InitAIModules();   // ensure the registry + forms are populated before we read them

            PublishFormSelector();

            foreach (var t in TunableRegistry.All)
            {
                if (!t.MenuVisible) continue;
                string tab = KickStart.ModID + " - AI Tunables: " + t.Category;
                string label = t.ResolvedLabel;
                string key = t.Key;
                try
                {
                    switch (t.Kind)
                    {
                        case TunableKind.Bool:
                        {
                            var o = new OptionToggle(label, tab, t.CurBool);
                            o.onValueSaved.AddListener(() => TunableRegistry.SetBool(key, o.SavedValue));
                            break;
                        }
                        case TunableKind.Int:
                        {
                            float step = t.Step <= 0f ? 1f : t.Step;
                            var o = SuperNativeOptions.OptionRangeAutoDisplay(label, tab, t.CurInt,
                                t.Min, t.Max, step, v => ((int)v).ToString());
                            o.onValueSaved.AddListener(() => TunableRegistry.SetInt(key, (int)o.SavedValue));
                            break;
                        }
                        case TunableKind.Float:
                        {
                            float step = t.Step <= 0f ? 0.01f : t.Step;
                            Func<float, string> disp = t.Display ?? (v => v.ToString("0.00"));
                            var o = SuperNativeOptions.OptionRangeAutoDisplay(label, tab, t.CurFloat,
                                t.Min, t.Max, step, v => disp(v));
                            o.onValueSaved.AddListener(() => TunableRegistry.SetFloat(key, o.SavedValue));
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogError(KickStart.ModID + ": TunableOptionsPublisher failed to publish '"
                        + key + "' - " + e.Message);
                }
            }
        }

        /// <summary>
        /// The global "AI Form" (mode) selector - lists every discovered IAIForm and switches AIFormRegistry.Active.
        /// Applies the persisted KickStart.AIFormSelected first, then publishes a slider whose display is the form's
        /// name. Selection is stored by form Id (stable across sessions) on KickStart.AIFormSelected (persisted).
        /// </summary>
        private static void PublishFormSelector()
        {
            try
            {
                var forms = new List<IAIForm>(AIFormRegistry.All);
                forms.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
                if (forms.Count < 2) return;   // nothing to choose between

                AIFormRegistry.SetActive(KickStart.AIFormSelected);   // apply the persisted choice
                int cur = forms.FindIndex(f => f.Id == AIFormRegistry.ActiveId);
                if (cur < 0) cur = 0;

                string tab = KickStart.ModID + " - AI Forms";
                var opt = SuperNativeOptions.OptionRangeAutoDisplay("AI Form (overall AI mode)",
                    tab, cur, 0, forms.Count - 1, 1,
                    v => forms[Mathf.Clamp((int)v, 0, forms.Count - 1)].DisplayName);
                opt.onValueSaved.AddListener(() =>
                {
                    int i = Mathf.Clamp((int)opt.SavedValue, 0, forms.Count - 1);
                    KickStart.AIFormSelected = forms[i].Id;
                    AIFormRegistry.SetActive(forms[i].Id);
                });
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogError(KickStart.ModID + ": PublishFormSelector failed - " + e.Message);
            }
        }
    }
}
