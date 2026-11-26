using UnityEngine;

namespace Nore.Runtime
{
    /// <summary>Single-window IMGUI shell. No scenes, prefabs or UI packages required.</summary>
    public sealed class RuntimeUI : MonoBehaviour
    {
        private static readonly string[] Tabs =
        {
            "Overview", "Stronghold", "Army", "Heroes", "Dragons", "Battle", "World", "System"
        };

        private GameController _g;
        private Vector2 _scroll;

        private void Awake() => _g = GetComponent<GameController>();

        private void OnGUI()
        {
            if (_g == null) _g = GetComponent<GameController>();
            if (_g == null || _g.State == null || _g.Data == null)
            {
                GUI.Label(new Rect(20, 20, 700, 40), "Loading Nore prototype...", Widgets.Box(Widgets.Dark, Widgets.Text, 16));
                return;
            }

            float width = Mathf.Min(Screen.width - 16f, 1400f);
            float height = Mathf.Min(Screen.height - 16f, 920f);
            Rect area = new Rect((Screen.width - width) * 0.5f, 8f, width, height);
            GUILayout.BeginArea(area, Widgets.PanelStyle(Widgets.Dark));

            DrawHeader(width);
            DrawTabs(width);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            switch (_g.tab)
            {
                case 0: OverviewPanel.Draw(_g); break;
                case 1: StrongholdPanel.Draw(_g); break;
                case 2: ArmyPanel.Draw(_g); break;
                case 3: HeroPanel.Draw(_g); break;
                case 4: DragonPanel.Draw(_g); break;
                case 5: BattlePanel.Draw(_g); break;
                case 6: WorldPanel.Draw(_g); break;
                case 7: SystemPanel.Draw(_g); break;
                default: OverviewPanel.Draw(_g); break;
            }
            GUILayout.EndScrollView();

            DrawFooter();
            GUILayout.EndArea();
        }

        private void DrawHeader(float width)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("NORE  ·  ART OF CONQUEST–STYLE PROTOTYPE",
                            Widgets.Box(Widgets.Panel, Widgets.Gold, 17, TextAnchor.MiddleLeft, FontStyle.Bold),
                            GUILayout.Width(width * 0.52f));
            GUILayout.Label("G " + _g.State.Stronghold.resources.gold +
                            "   W " + _g.State.Stronghold.resources.wood +
                            "   M " + _g.State.Stronghold.resources.mana +
                            "   P " + _g.State.Stronghold.resources.prosperity +
                            "   L " + _g.State.Stronghold.resources.linari,
                            Widgets.Box(Widgets.Panel, Widgets.Text, 12, TextAnchor.MiddleRight));
            GUILayout.EndHorizontal();
        }

        private void DrawTabs(float width)
        {
            GUILayout.BeginHorizontal();
            float tabWidth = (width - 40f) / Tabs.Length - 4f;
            for (int i = 0; i < Tabs.Length; i++)
            {
                Color color = _g.tab == i ? Widgets.Gold : Widgets.PanelLight;
                if (Widgets.Btn(Tabs[i], color, GUILayout.Width(tabWidth), GUILayout.Height(30)))
                {
                    _g.tab = i;
                    _scroll = Vector2.zero;
                }
            }
            GUILayout.EndHorizontal();
            Widgets.HRule();
        }

        private void DrawFooter()
        {
            Widgets.HRule();
            GUILayout.BeginHorizontal();
            GUILayout.Label(_g.status,
                            Widgets.Box(Widgets.Panel, Widgets.Green, 11, TextAnchor.MiddleLeft),
                            GUILayout.ExpandWidth(true));
            if (Widgets.Btn("Save", Widgets.Green, GUILayout.Width(90))) _g.Save();
            if (_g.Battle != null && !_g.Battle.Finished &&
                Widgets.Btn("Battle", Widgets.Red, GUILayout.Width(90))) _g.tab = 5;
            GUILayout.EndHorizontal();
        }
    }
}
