using System.Collections.Generic;
using UnityEngine;

namespace Nore.Runtime
{
    /// <summary>Zero-prefab IMGUI helpers. Everything is generated at runtime.</summary>
    public static class Widgets
    {
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, GUIStyle> Styles = new Dictionary<string, GUIStyle>();

        public static readonly Color Dark = new Color(0.07f, 0.09f, 0.13f, 1f);
        public static readonly Color Panel = new Color(0.11f, 0.14f, 0.20f, 1f);
        public static readonly Color PanelLight = new Color(0.16f, 0.20f, 0.28f, 1f);
        public static readonly Color Accent = new Color(0.25f, 0.55f, 0.85f, 1f);
        public static readonly Color Green = new Color(0.26f, 0.66f, 0.40f, 1f);
        public static readonly Color Red = new Color(0.80f, 0.30f, 0.30f, 1f);
        public static readonly Color Gold = new Color(0.85f, 0.68f, 0.25f, 1f);
        public static readonly Color Purple = new Color(0.55f, 0.40f, 0.75f, 1f);
        public static readonly Color Text = new Color(0.90f, 0.93f, 0.98f, 1f);
        public static readonly Color Muted = new Color(0.62f, 0.68f, 0.78f, 1f);

        private static string Key(Color c) => ColorUtility.ToHtmlStringRGBA(c);

        public static Texture2D Solid(Color color)
        {
            string key = Key(color);
            Texture2D tex;
            if (Textures.TryGetValue(key, out tex) && tex != null) return tex;
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            var pixels = new[] { color, color, color, color };
            tex.SetPixels(pixels);
            tex.Apply();
            Textures[key] = tex;
            return tex;
        }

        public static GUIStyle Box(Color bg, Color fg, int size = 12,
                                   TextAnchor anchor = TextAnchor.MiddleLeft,
                                   FontStyle font = FontStyle.Normal, bool wrap = true)
        {
            string key = "B" + Key(bg) + Key(fg) + size + (int)anchor + (int)font + wrap;
            GUIStyle style;
            if (Styles.TryGetValue(key, out style)) return style;
            style = new GUIStyle(GUI.skin.box)
            {
                alignment = anchor,
                fontSize = size,
                fontStyle = font,
                wordWrap = wrap,
                padding = new RectOffset(6, 6, 4, 4),
                margin = new RectOffset(1, 1, 1, 1),
                richText = false
            };
            style.normal.background = Solid(bg);
            style.normal.textColor = fg;
            style.hover.background = Solid(bg);
            style.hover.textColor = fg;
            style.active.background = Solid(bg);
            style.active.textColor = fg;
            Styles[key] = style;
            return style;
        }

        public static GUIStyle PanelStyle(Color bg)
        {
            string key = "P" + Key(bg);
            GUIStyle style;
            if (Styles.TryGetValue(key, out style)) return style;
            style = new GUIStyle(GUI.skin.box);
            style.normal.background = Solid(bg);
            style.padding = new RectOffset(10, 10, 8, 8);
            Styles[key] = style;
            return style;
        }

        public static GUIStyle ButtonStyle(Color bg, Color fg, int size = 12)
        {
            string key = "Btn" + Key(bg) + Key(fg) + size;
            GUIStyle style;
            if (Styles.TryGetValue(key, out style)) return style;
            style = new GUIStyle(GUI.skin.button)
            {
                fontSize = size,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 4, 4),
                margin = new RectOffset(2, 2, 2, 2),
                wordWrap = true
            };
            style.normal.background = Solid(bg);
            style.normal.textColor = fg;
            style.hover.background = Solid(Color.Lerp(bg, Color.white, 0.18f));
            style.hover.textColor = Color.white;
            style.active.background = Solid(Color.Lerp(bg, Color.black, 0.20f));
            style.active.textColor = fg;
            Styles[key] = style;
            return style;
        }

        public static bool Btn(string label, Color bg, params GUILayoutOption[] options) =>
            GUILayout.Button(label, ButtonStyle(bg, Text), options);

        public static bool Btn(string label, params GUILayoutOption[] options) =>
            GUILayout.Button(label, ButtonStyle(Accent, Text), options);

        public static void Title(string text, int size = 18) =>
            GUILayout.Label(text, Box(PanelLight, Gold, size, TextAnchor.MiddleLeft, FontStyle.Bold));

        public static void Label(string text, Color color, int size = 12) =>
            GUILayout.Label(text, Box(new Color(0, 0, 0, 0), color, size));

        public static void Note(string text) => GUILayout.Label(text, Box(new Color(0, 0, 0, 0), Muted, 11));

        public static void Bar(string label, int value, int max, Color fill)
        {
            Rect rect = GUILayoutUtility.GetRect(20, 18, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, Box(new Color(0.05f, 0.06f, 0.09f, 1f), Text, 10));
            float frac = max <= 0 ? 0f : Mathf.Clamp01((float)value / max);
            if (frac > 0f)
                GUI.Box(new Rect(rect.x, rect.y, rect.width * frac, rect.height), GUIContent.none,
                        Box(fill, Text, 10));
            GUI.Label(rect, label + "  " + value + " / " + max,
                      Box(new Color(0, 0, 0, 0), Text, 10, TextAnchor.MiddleCenter));
        }

        public static void HRule()
        {
            Rect r = GUILayoutUtility.GetRect(4, 2, GUILayout.ExpandWidth(true));
            GUI.Box(r, GUIContent.none, Box(new Color(0.25f, 0.35f, 0.48f, 1f), Text, 1));
        }

        public static void Begin(Color bg) => GUILayout.BeginVertical(PanelStyle(bg));
        public static void End() => GUILayout.EndVertical();

        public static void BeginRow() => GUILayout.BeginHorizontal();
        public static void EndRow() => GUILayout.EndHorizontal();
    }
}
