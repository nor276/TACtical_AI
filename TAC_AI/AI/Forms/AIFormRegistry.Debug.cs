using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms
{
    /// <summary>
    /// L-053: scrollable IMGUI overlay of every <see cref="AIFormRegistry.LiveRoutings"/>
    /// entry. Invoked from SmartForm.DrawPathingDebugGUI (Wave 3 L-073 wires the call site
    /// adjacent to the Workers GUI line).
    ///
    /// Color-codes by reason:
    ///   - green: routed to active form successfully
    ///   - yellow: fell back to Modified
    ///   - red:    OnTechSpawn threw / DelayedSubscribe failed
    /// </summary>
    public static partial class AIFormRegistry
    {
        private static Vector2 _routingScroll;

        public static void DrawRoutingDebugGUI(Rect rect)
        {
            GUI.Box(rect, GUIContent.none);
            // Per-form-id summary
            var perForm = new Dictionary<string, int>();
            foreach (var kv in LiveRoutings)
            {
                string k = kv.Value.FormId ?? "<unrouted>";
                perForm.TryGetValue(k, out int c);
                perForm[k] = c + 1;
            }
            var headerSb = new System.Text.StringBuilder();
            headerSb.Append("LiveRoutings: ").Append(LiveRoutings.Count).Append(" total");
            foreach (var kv in perForm) headerSb.Append("  ").Append(kv.Key).Append('=').Append(kv.Value);
            GUI.Label(new Rect(rect.x + 6, rect.y + 4, rect.width - 12, 18), headerSb.ToString());

            var scrollRect = new Rect(rect.x + 4, rect.y + 24, rect.width - 8, rect.height - 28);
            int rowCount = LiveRoutings.Count;
            var contentRect = new Rect(0, 0, scrollRect.width - 18, rowCount * 16 + 4);
            _routingScroll = GUI.BeginScrollView(scrollRect, _routingScroll, contentRect);
            int y = 2;
            int nowMs = System.Environment.TickCount;
            foreach (var kv in LiveRoutings)
            {
                var r = kv.Value;
                Color prior = GUI.color;
                if (r.Reason == "OnTechSpawnFailed" || r.Reason == "DelayedSubscribeFailed")
                    GUI.color = Color.red;
                else if (r.Reason == "Reclaimed" || (r.FormId == DefaultFormId && ActiveId != DefaultFormId))
                    GUI.color = Color.yellow;
                else
                    GUI.color = new Color(0.6f, 1f, 0.6f);
                int ageMs = unchecked(nowMs - r.TimestampMs);
                string line = "tech=" + kv.Key
                    + "  form=" + (r.FormId ?? "<null>")
                    + "  reason=" + (r.Reason ?? "?")
                    + "  age=" + (ageMs / 1000) + "s"
                    + (r.ExceptionType != null ? "  exc=" + r.ExceptionType : "");
                GUI.Label(new Rect(2, y, contentRect.width - 4, 16), line);
                GUI.color = prior;
                y += 16;
            }
            GUI.EndScrollView();
        }
    }
}
