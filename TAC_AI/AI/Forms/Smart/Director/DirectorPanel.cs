using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director
{
    // Standalone operator panel for the Training Director. Toggled with Ctrl + the
    // DirectorPanelKey (default Backspace). Spawns a training scenario around the
    // player's tech and tunes intensity without the dev console. Scenario control +
    // read-only status for now.
    public class DirectorPanel : MonoBehaviour
    {
        private static DirectorPanel inst;
        private const int WindowId = 8042;
        private static bool _open;
        private static Rect _rect = new Rect(60, 60, 360, 290);
        private static float _intensity = 1f;

        internal static void Initiate()
        {
            if (inst != null) return;
            inst = new GameObject("TACDirectorPanel").AddComponent<DirectorPanel>();
            DontDestroyOnLoad(inst.gameObject);
        }

        internal static void DeInit()
        {
            if (inst == null) return;
            _open = false;
            Destroy(inst.gameObject);
            inst = null;
        }

        private void Update()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KickStart.DirectorPanelKey))
                _open = !_open;
        }

        private void OnGUI()
        {
            if (!_open) return;
            AltUI.StartUI();
            _rect = GUI.Window(WindowId, _rect, DrawWindow, "Training Director", AltUI.MenuLeft);
            AltUI.EndUI();
        }

        private static void DrawWindow(int id)
        {
            if (!TrainingDirector.IsActive)
            {
                GUILayout.Label("Director not running. Set a tech to the Smart AI form first.");
                GUI.DragWindow();
                return;
            }

            int active = DirectorState.ActiveScenario;
            GUILayout.Label("state: " + (active > 0 ? "running " + NameFor(active) : "idle")
                + "   intensity=" + DirectorState.Intensity.ToString("0.00"));
            GUILayout.Label("constraints OK " + ConstraintEngine.OkCount()
                + "/" + ConstraintRegistry.ConstraintCount);
            GUILayout.Space(6);

            GUILayout.Label("Spawn scenario (around your tech):");
            GUILayout.BeginHorizontal();
            for (int sid = 1; sid <= 6; sid++)
            {
                string label = sid == 6 ? "S6'" : "S" + sid;
                GUIStyle style = active == sid ? AltUI.ButtonBlueActive : AltUI.ButtonBlue;
                if (GUILayout.Button(new GUIContent(label, NameFor(sid)), style))
                    TrainingDirector.OperatorSetScenario(sid, _intensity);
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            GUILayout.Label("Intensity: " + _intensity.ToString("0.00"));
            _intensity = GUILayout.HorizontalSlider(_intensity, 0.25f, 2.0f);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Respawn", AltUI.ButtonBlue))
                TrainingDirector.OperatorRespawnScenario();
            if (GUILayout.Button("Stop", AltUI.ButtonGrey))
                TrainingDirector.OperatorStopScenario();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label(GUI.tooltip);
            GUI.DragWindow();
        }

        private static string NameFor(int sid)
        {
            var recipe = ScenarioRegistry.Lookup(sid);
            return recipe != null ? recipe.Name : ("S" + sid);
        }
    }
}
