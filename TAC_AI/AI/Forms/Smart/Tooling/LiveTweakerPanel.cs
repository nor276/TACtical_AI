#if SMART_DEV
using UnityEngine;
using TAC_AI.AI.Tunables;

namespace TAC_AI.AI.Forms.Smart.Tooling
{
    /// <summary>
    /// P10 Item 36 (SMART_DEV-gated): in-game tunable panel. Toggle with F8.
    /// Shows every Smart-keyed tunable via <see cref="TunableMenuBridge"/>; per-tunable
    /// slider (float) / numeric (int) / toggle (bool) writes through the central registry
    /// so changes apply live.
    ///
    /// Only compiled when SMART_DEV is defined — release builds ship without the panel.
    /// Production tweaking happens via the in-game options menu (TunableOptionsPublisher)
    /// or console (SmartConsoleCommands).
    /// </summary>
    public sealed class LiveTweakerPanel : MonoBehaviour
    {
        public static KeyCode ToggleKey = KeyCode.F8;

        private static LiveTweakerPanel _instance;
        private bool _visible;
        private Vector2 _scroll;
        private Rect _windowRect = new Rect(40, 40, 480, 600);

        public static void Install()
        {
            if (_instance != null) return;
            var go = new GameObject("Smart.LiveTweakerPanel");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<LiveTweakerPanel>();
        }

        public static void Uninstall()
        {
            if (_instance == null) return;
            DestroyImmediate(_instance.gameObject);
            _instance = null;
        }

        void Update()
        {
            if (Input.GetKeyDown(ToggleKey)) _visible = !_visible;
        }

        void OnGUI()
        {
            if (!_visible) return;
            _windowRect = GUI.Window(0x5A41744, _windowRect, DrawWindow, "Smart Live Tweaker (F8 to toggle)");
        }

        private void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (var t in TunableMenuBridge.SmartEntries)
            {
                if (t == null) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(t.Key, GUILayout.Width(240));
                switch (t.Kind)
                {
                    case TunableKind.Float:
                    {
                        float v = t.CurFloat;
                        float nv = GUILayout.HorizontalSlider(v, t.Min, t.Max, GUILayout.Width(160));
                        GUILayout.Label(nv.ToString("F2"), GUILayout.Width(40));
                        if (!Mathf.Approximately(v, nv)) TunableRegistry.SetFloat(t.Key, nv);
                        break;
                    }
                    case TunableKind.Int:
                    {
                        int v = t.CurInt;
                        string s = GUILayout.TextField(v.ToString(), GUILayout.Width(80));
                        int nv;
                        if (int.TryParse(s, out nv) && nv != v) TunableRegistry.SetInt(t.Key, nv);
                        break;
                    }
                    case TunableKind.Bool:
                    {
                        bool v = t.CurBool;
                        bool nv = GUILayout.Toggle(v, "", GUILayout.Width(40));
                        if (nv != v) TunableRegistry.SetBool(t.Key, nv);
                        break;
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
        }
    }
}
#endif
