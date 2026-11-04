using System;
using System.Collections.Generic;
using UnityEngine;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// The play UI drawn with IMGUI (no assets or prefabs required, editable as code):
    /// - default UI: world messages, interaction/pickup prompts ({action} text), health, waypoint
    ///   marker, scene fades, start/pause/dead/win screens (world.defaultUISettings labels);
    /// - the creator's custom HUD (world.customHUDUI) and event-shown overlays
    ///   (world.customUIOverRelays): text boxes, buttons, images and bars placed by their saved
    ///   CSS (px/% lengths, colours, font size), bound to variables, with show/hide events and
    ///   click events. CSS flex layouts are approximated by stacking children.
    /// Missing or disabled UI stays distinct from a zero value: an unbound variable shows its
    /// element's text, never "0".
    /// </summary>
    public sealed class ZimensionHud : MonoBehaviour
    {
        sealed class Element
        {
            public string Id, ParentId, Type, Text, Src, Event, ShowEvent, HideEvent, VariableName, Prefix, Suffix;
            public bool Variable, ClosesOverlay;
            public float MaxValue = 100;
            public CssStyle Style, BarStyle;
            public string LayoutDirection = "";
            public float LayoutGap = 8, LayoutPadding = 8;
            public bool HasLayout;
            public bool Hidden;
            public JsonNode Source;
        }

        sealed class Overlay
        {
            public string Event, HideEvent;
            public bool PauseGame, CloseOnClick = true, Visible;
            public CssStyle Container;
            public List<Element> Elements = new List<Element>();
        }

        ZimensionWorld m_World;
        readonly List<Element> m_Hud = new List<Element>();
        readonly List<Overlay> m_Overlays = new List<Overlay>();
        readonly Dictionary<string, string> m_Variables = new Dictionary<string, string>();
        readonly Dictionary<string, Texture2D> m_Images = new Dictionary<string, Texture2D>();
        string m_Prompt = "";
        bool m_Pickup;
        string m_PromptTemplate = "Press F to {action}", m_PickupTemplate = "Press F to pick up {action}";
        readonly List<(string text, float until)> m_Messages = new List<(string, float)>();
        float m_Fade, m_FadeTarget, m_FadeSpeed = 4;
        string m_Screen = ""; // "", "start", "pause", "dead", "win"
        bool m_ShowHealth;
        float m_Health = 100, m_MaxHealth = 100;
        Vector3? m_Waypoint;
        bool m_ShowDiagnostics;
        GUIStyle m_Text, m_Center, m_Big, m_Box;
        Texture2D m_White;
        string m_Title = "", m_StartLabel = "Start", m_DeadText = "You died", m_WinText = "You win!";
        Texture2D m_Loading, m_Logo;

        public bool BlocksInput => m_Screen.Length > 0 || PausingOverlay;
        public bool PausingOverlay { get { foreach (var o in m_Overlays) if (o.Visible && o.PauseGame) return true; return false; } }
        public bool ShowingDiagnostics => m_ShowDiagnostics;
        public string Screen_ => m_Screen;

        public void Configure(ZimensionWorld world, JsonNode worldJson, ZimensionProject project, IEnumerable<KeyValuePair<string, Texture2D>> images)
        {
            m_World = world;
            m_Title = worldJson.Str("name", project ? project.worldName : "");
            m_Loading = project ? project.loadingImage : null;
            m_Logo = project ? project.logoImage : null;
            if (images != null) foreach (var pair in images) if (pair.Value) m_Images[pair.Key] = pair.Value;
            var ui = worldJson.Obj("defaultUISettings");
            string Label(string element, string key, string fallback)
            {
                var e = ui.Obj(element);
                var text = e.Str("text");
                if (!string.IsNullOrEmpty(text)) return text;
                var labels = e.Obj("labels");
                return labels.Str(key, fallback);
            }
            m_PromptTemplate = Label("interactionPrompt", "text", m_PromptTemplate);
            m_PickupTemplate = Label("pickupPrompt", "text", m_PickupTemplate);
            m_StartLabel = Label("startButton", "text", m_StartLabel);
            m_DeadText = Label("deadScreen", "title", m_DeadText);
            m_Hud.Clear(); m_Overlays.Clear();
            foreach (var e in worldJson.Obj("customHUDUI")["elements"].Items) if (e.IsObject) m_Hud.Add(ParseElement(e));
            foreach (var o in worldJson["customUIOverRelays"].Items)
            {
                if (!o.IsObject) continue;
                var overlay = new Overlay
                {
                    Event = o.Str("event"),
                    HideEvent = o.Str("hideEventName"),
                    PauseGame = o.Truthy("pauseGame"),
                    CloseOnClick = !(o["closeOnClick"].IsBool && !o["closeOnClick"].AsBool),
                    Container = CssStyle.From(o.Obj("containerStyle")),
                };
                foreach (var e in o["elements"].Items) if (e.IsObject) overlay.Elements.Add(ParseElement(e));
                m_Overlays.Add(overlay);
            }
            foreach (var e in m_Hud) if (!string.IsNullOrEmpty(e.ShowEvent)) e.Hidden = true; // shown by its event
            // The default HUD shows health in worlds that can hurt the player.
            m_ShowHealth = worldJson.Obj("playerSettings").Obj("damageReactions").Truthy("enabled") || worldJson.Truthy("shootingMode");
        }

        static Element ParseElement(JsonNode s)
        {
            var e = new Element
            {
                Source = s,
                Id = s.Str("id"), ParentId = s.Str("parentId"), Type = s.Str("type"),
                Text = s.Str("text"), Src = s.Str("src"), Event = s.Str("event"),
                ShowEvent = s.Str("showEvent"), HideEvent = s.Str("hideEvent"),
                Variable = s.Str("valueType") == "variable", VariableName = s.Str("variableName"),
                Prefix = s.Str("prefix"), Suffix = s.Str("suffix"),
                ClosesOverlay = s.Truthy("closesOverlay"),
                MaxValue = s["maxValue"].IsNumber ? (float)s["maxValue"].AsNumber : 100,
                Style = CssStyle.From(s.Obj("style"), s.Str("customStyleText")),
                BarStyle = CssStyle.From(s.Obj("barStyle")),
            };
            if (e.Type == "textbox") e.Type = "text-box";
            var layout = s.Obj("layout");
            if (layout.IsObject)
            {
                e.HasLayout = true;
                e.LayoutDirection = layout.Str("direction");
                e.LayoutGap = layout.Float("gap", 8);
                e.LayoutPadding = layout.Float("padding", 8);
            }
            return e;
        }

        // ---- API used by the world ----------------------------------------------------------

        public void SetPrompt(string text, bool pickup) { m_Prompt = text ?? ""; m_Pickup = pickup; }

        public void ShowMessage(string text, float seconds = 4f)
        {
            if (string.IsNullOrEmpty(text)) { m_Messages.Clear(); return; }
            m_Messages.Add((text, Time.unscaledTime + seconds));
            if (m_Messages.Count > 4) m_Messages.RemoveAt(0);
        }

        public void SetVariable(string name, string value) => m_Variables[name] = value;

        public void SetHealth(float health, float max)
        {
            if (health < max) m_ShowHealth = true;
            m_Health = health; m_MaxHealth = Mathf.Max(1, max);
        }

        public void SetWaypoint(Vector3? worldPosition) => m_Waypoint = worldPosition;

        public void FadeOut(float seconds) { m_FadeTarget = 1; m_FadeSpeed = seconds > 0 ? 1 / seconds : 100; }
        public void FadeIn(float seconds) { m_FadeTarget = 0; m_FadeSpeed = seconds > 0 ? 1 / seconds : 100; }
        public float FadeAmount => m_Fade;

        public void ShowScreen(string screen) => m_Screen = screen ?? "";

        /// <summary>A world event reached the dispatcher: custom HUD show/hide and overlays.</summary>
        public void OnWorldEvent(string name)
        {
            foreach (var e in m_Hud)
            {
                if (!string.IsNullOrEmpty(e.ShowEvent) && e.ShowEvent == name) e.Hidden = false;
                if (!string.IsNullOrEmpty(e.HideEvent) && e.HideEvent == name) e.Hidden = true;
            }
            bool showing = false;
            foreach (var o in m_Overlays) if (!string.IsNullOrEmpty(o.Event) && o.Event == name) { o.Visible = true; showing = true; }
            foreach (var o in m_Overlays)
                if (!string.IsNullOrEmpty(o.HideEvent) && o.HideEvent == name && !(showing && o.Event == name)) o.Visible = false;
            if (PausingOverlay && m_World) m_World.Player?.UnlockCursor();
        }

        public void HideAllOverlays() { foreach (var o in m_Overlays) o.Visible = false; }

        void Update()
        {
            m_Fade = Mathf.MoveTowards(m_Fade, m_FadeTarget, m_FadeSpeed * Time.unscaledDeltaTime);
            m_Messages.RemoveAll(m => m.until < Time.unscaledTime);
            if (ZimensionInput.KeyDown(KeyCode.F3)) m_ShowDiagnostics = !m_ShowDiagnostics;
        }

        // ---- Drawing ----------------------------------------------------------------------

        void Styles()
        {
            if (m_Text != null) return;
            m_White = Texture2D.whiteTexture;
            m_Text = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, richText = false };
            m_Text.normal.textColor = Color.white;
            m_Center = new GUIStyle(m_Text) { alignment = TextAnchor.MiddleCenter };
            m_Big = new GUIStyle(m_Center) { fontSize = 36, fontStyle = FontStyle.Bold };
            m_Box = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            m_Box.normal.textColor = Color.white;
        }

        void Fill(Rect r, Color c)
        {
            var previous = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, m_White);
            GUI.color = previous;
        }

        void OnGUI()
        {
            Styles();
            float w = Screen.width, h = Screen.height;
            DrawCustomHud(w, h);
            DrawWaypoint();
            // Messages, top centre.
            float y = 24;
            foreach (var (text, _) in m_Messages)
            {
                var size = m_Box.CalcSize(new GUIContent(text));
                float width = Mathf.Min(w * 0.8f, Mathf.Max(240, size.x + 32));
                float height = m_Box.CalcHeight(new GUIContent(text), width) + 16;
                var r = new Rect((w - width) / 2, y, width, height);
                Fill(r, new Color(0, 0, 0, 0.65f));
                GUI.Label(r, text, m_Center);
                y += height + 8;
            }
            // Prompt, lower centre.
            if (m_Prompt.Length > 0 && m_Screen.Length == 0)
            {
                var template = m_Pickup ? m_PickupTemplate : m_PromptTemplate;
                var text = template.Contains("{action}") ? template.Replace("{action}", m_Prompt).Replace("{world}", m_Title) : $"{template} {m_Prompt}";
                var r = new Rect((w - 420) / 2, h * 0.72f, 420, 44);
                Fill(r, new Color(0, 0, 0, 0.6f));
                GUI.Label(r, text, m_Center);
            }
            if (m_ShowHealth)
            {
                var r = new Rect(24, h - 44, 240, 18);
                Fill(r, new Color(0, 0, 0, 0.55f));
                Fill(new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01(m_Health / m_MaxHealth), r.height - 4), new Color(0.86f, 0.16f, 0.15f, 0.95f));
                GUI.Label(new Rect(r.x, r.y - 22, 240, 20), $"Health {Mathf.CeilToInt(m_Health)}", m_Text);
            }
            DrawOverlays(w, h);
            if (m_Fade > 0.001f) Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, m_Fade));
            DrawScreens(w, h);
            if (m_ShowDiagnostics && m_World) DrawDiagnostics(w, h);
            else if (m_Screen.Length == 0 && m_World && m_World.Player && !m_World.Player.CursorLocked && !PausingOverlay)
                GUI.Label(new Rect(0, h - 28, w, 24), "Click to look around - Esc releases the mouse - F3 diagnostics", m_Center);
        }

        void DrawScreens(float w, float h)
        {
            if (m_Screen.Length == 0) return;
            Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, m_Screen == "pause" ? 0.6f : 0.8f));
            if (m_Screen == "start" && m_Loading) GUI.DrawTexture(new Rect(0, 0, w, h), m_Loading, ScaleMode.ScaleAndCrop);
            if (m_Screen == "start" && m_Logo) GUI.DrawTexture(new Rect((w - 160) / 2, h * 0.18f, 160, 160), m_Logo, ScaleMode.ScaleToFit);
            string title = m_Screen == "dead" ? m_DeadText : m_Screen == "win" ? m_WinText : m_Screen == "pause" ? "Paused" : m_Title;
            GUI.Label(new Rect(0, h * 0.38f, w, 60), title, m_Big);
            var button = new Rect((w - 220) / 2, h * 0.52f, 220, 48);
            switch (m_Screen)
            {
                case "start":
                    if (GUI.Button(button, m_StartLabel)) m_World.BeginPlay();
                    break;
                case "pause":
                    if (GUI.Button(button, "Resume")) m_World.SetPaused(false);
                    if (GUI.Button(new Rect(button.x, button.y + 60, 220, 40), "Restart")) { m_World.SetPaused(false); m_World.ResetWorld(); }
                    break;
                case "dead":
                    if (GUI.Button(button, "Respawn")) m_World.RespawnAfterDeath();
                    break;
                case "win":
                    if (GUI.Button(button, "Play again")) { m_Screen = ""; m_World.ResetWorld(); }
                    break;
            }
        }

        void DrawWaypoint()
        {
            if (!m_Waypoint.HasValue || !m_World || !m_World.Player || !m_World.Player.PlayerCamera) return;
            var cam = m_World.Player.PlayerCamera;
            var p = cam.WorldToScreenPoint(m_Waypoint.Value + Vector3.up * 0.5f);
            if (p.z < 0) return;
            var r = new Rect(p.x - 10, Screen.height - p.y - 10, 20, 20);
            Fill(r, new Color(1f, 0.8f, 0.1f, 0.9f));
            float metres = Vector3.Distance(m_World.Player.Feet, m_Waypoint.Value);
            GUI.Label(new Rect(p.x - 40, Screen.height - p.y + 12, 80, 20), $"{metres:0} m", m_Center);
        }

        void DrawDiagnostics(float w, float h)
        {
            var r = new Rect(12, 12, Mathf.Min(560, w - 24), Mathf.Min(420, h - 24));
            Fill(r, new Color(0, 0, 0, 0.75f));
            var lines = m_World.DiagnosticsText();
            GUI.Label(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 12), lines, new GUIStyle(m_Text) { fontSize = 12 });
        }

        // ---- Custom UI ----------------------------------------------------------------------

        string TextOf(Element e)
        {
            if (!e.Variable || string.IsNullOrEmpty(e.VariableName)) return e.Text ?? "";
            // Missing/unbound stays distinct from zero: show the element text, not "0".
            return m_Variables.TryGetValue(e.VariableName, out var value) ? (e.Prefix ?? "") + value + (e.Suffix ?? "") : e.Text ?? "";
        }

        float BarValue(Element e)
        {
            if (!e.Variable || !m_Variables.TryGetValue(e.VariableName ?? "", out var value)) return -1;
            return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1;
        }

        void DrawCustomHud(float w, float h)
        {
            if (m_Hud.Count == 0) return;
            DrawTree(m_Hud, new Rect(0, 0, w, h), null);
        }

        void DrawOverlays(float w, float h)
        {
            foreach (var o in m_Overlays)
            {
                if (!o.Visible) continue;
                var container = new Rect(0, 0, w, h);
                var bg = o.Container.Color("backgroundColor", new Color(0, 0, 0, 0.35f));
                Fill(container, bg);
                bool clicked = DrawTree(o.Elements, container, o);
                if (!clicked && o.CloseOnClick && Event.current.type == EventType.MouseDown && !HasClickable(o))
                {
                    o.Visible = false;
                    Event.current.Use();
                }
            }
        }

        static bool HasClickable(Overlay o)
        {
            foreach (var e in o.Elements) if (!string.IsNullOrEmpty(e.Event) || e.Type == "button" || e.ClosesOverlay) return true;
            return false;
        }

        /// <summary>Draw an element tree; true when an element consumed a click.</summary>
        bool DrawTree(List<Element> elements, Rect screen, Overlay overlay)
        {
            var ids = new HashSet<string>();
            foreach (var e in elements) if (!string.IsNullOrEmpty(e.Id)) ids.Add(e.Id);
            var children = new Dictionary<string, List<Element>>();
            var roots = new List<Element>();
            foreach (var e in elements)
            {
                if (!string.IsNullOrEmpty(e.ParentId) && e.ParentId != e.Id && ids.Contains(e.ParentId))
                {
                    if (!children.TryGetValue(e.ParentId, out var list)) children[e.ParentId] = list = new List<Element>();
                    list.Add(e);
                }
                else roots.Add(e);
            }
            bool clicked = false;
            foreach (var e in roots) clicked |= DrawElement(e, screen, children, overlay, null, 0);
            return clicked;
        }

        Vector2 Measure(Element e, Rect parent)
        {
            var s = e.Style;
            float width = s.Length("width", parent.width, -1), height = s.Length("height", parent.height, -1);
            switch (e.Type)
            {
                case "button": if (width < 0) width = 120; if (height < 0) height = 40; break;
                case "image":
                {
                    var tex = Image(e.Src);
                    float aspect = tex ? (float)tex.width / Mathf.Max(1, tex.height) : 1;
                    if (width < 0 && height < 0) height = 200;
                    if (width < 0) width = height * aspect;
                    if (height < 0) height = width / aspect;
                    break;
                }
                case "text-box":
                {
                    var style = TextStyle(e);
                    var content = new GUIContent(TextOf(e));
                    if (width < 0) width = Mathf.Min(parent.width, style.CalcSize(content).x + 12);
                    if (height < 0) height = style.CalcHeight(content, Mathf.Max(1, width - 8)) + 6;
                    break;
                }
                case "bar": if (width < 0) width = 120; if (height < 0) height = 20; break;
                default: if (width < 0) width = e.HasLayout ? parent.width : 0; if (height < 0) height = e.HasLayout ? 0 : 0; break;
            }
            width = Mathf.Max(width, s.Length("minWidth", parent.width, 0));
            height = Mathf.Max(height, s.Length("minHeight", parent.height, 0));
            return new Vector2(Mathf.Max(0, width), Mathf.Max(0, height));
        }

        Rect Place(Element e, Rect parent, Vector2 size, Vector2? flow)
        {
            var s = e.Style;
            float x, y;
            if (flow.HasValue && !s.Has("left") && !s.Has("right") && !s.Has("top") && !s.Has("bottom"))
            {
                x = flow.Value.x; y = flow.Value.y;
            }
            else
            {
                x = s.Has("left") ? parent.x + s.Length("left", parent.width, 0)
                  : s.Has("right") ? parent.xMax - s.Length("right", parent.width, 0) - size.x
                  : parent.x + (parent.width - size.x) / 2;
                y = s.Has("top") ? parent.y + s.Length("top", parent.height, 0)
                  : s.Has("bottom") ? parent.yMax - s.Length("bottom", parent.height, 0) - size.y
                  : parent.y + (parent.height - size.y) / 2;
                // translate(-50%, -50%) centring is common in saved styles.
                var transform = s.Get("transform");
                if (transform.Contains("-50%")) { x -= size.x / 2; y -= size.y / 2; }
            }
            return new Rect(x, y, size.x, size.y);
        }

        GUIStyle TextStyle(Element e)
        {
            var s = e.Style;
            var style = new GUIStyle(m_Text)
            {
                fontSize = Mathf.RoundToInt(s.Number("fontSize", e.Type == "text-box" ? 14 : 16)),
                alignment = e.Type == "button" || s.Get("textAlign") == "center" ? TextAnchor.MiddleCenter : s.Get("textAlign") == "right" ? TextAnchor.UpperRight : TextAnchor.UpperLeft,
                fontStyle = s.Get("fontWeight") == "bold" || s.Number("fontWeight", 400) >= 600 ? FontStyle.Bold : FontStyle.Normal,
                wordWrap = true,
            };
            style.normal.textColor = s.Color("color", e.Type == "text-box" ? new Color(0.94f, 0.94f, 0.94f) : Color.white);
            return style;
        }

        Texture2D Image(string src) => !string.IsNullOrEmpty(src) && m_Images.TryGetValue(src, out var tex) ? tex : null;

        bool DrawElement(Element e, Rect parent, Dictionary<string, List<Element>> children, Overlay overlay, Vector2? flow, int depth)
        {
            if (e.Hidden || depth > 32) return false;
            var size = Measure(e, parent);
            var rect = Place(e, parent, size, flow);
            var s = e.Style;
            float opacity = Mathf.Clamp01(s.Number("opacity", 1));
            var previous = GUI.color;
            GUI.color = new Color(1, 1, 1, opacity);
            var background = s.Color("backgroundColor", e.Type == "button" ? new Color(23 / 255f, 169 / 255f, 0) : e.Type == "text-box" ? new Color(0.24f, 0.24f, 0.24f) : new Color(0, 0, 0, 0));
            if (background.a > 0) Fill(rect, new Color(background.r, background.g, background.b, background.a * opacity));
            bool clicked = false;
            switch (e.Type)
            {
                case "image":
                {
                    var tex = Image(e.Src);
                    if (tex) GUI.DrawTexture(rect, tex, s.Get("objectFit") == "cover" ? ScaleMode.ScaleAndCrop : ScaleMode.ScaleToFit);
                    break;
                }
                case "bar":
                {
                    float value = BarValue(e);
                    var fill = e.BarStyle.Color("backgroundColor", new Color(0.86f, 0.16f, 0.15f));
                    if (value >= 0) Fill(new Rect(rect.x + 1, rect.y + 1, (rect.width - 2) * Mathf.Clamp01(value / Mathf.Max(0.0001f, e.MaxValue)), rect.height - 2), fill);
                    break;
                }
                case "button":
                case "text-box":
                    GUI.Label(new Rect(rect.x + 4, rect.y + 2, rect.width - 8, rect.height - 4), TextOf(e), TextStyle(e));
                    break;
            }
            GUI.color = previous;
            bool interactive = !string.IsNullOrEmpty(e.Event) || (overlay != null && (e.Type == "button" || e.ClosesOverlay));
            if (interactive && Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                clicked = true;
                Event.current.Use();
                if (!string.IsNullOrEmpty(e.Event)) m_World?.TriggerEvent(e.Event);
                if (overlay != null && (e.ClosesOverlay || e.Type == "button" || overlay.CloseOnClick)) overlay.Visible = false;
            }
            if (!string.IsNullOrEmpty(e.Id) && children.TryGetValue(e.Id, out var kids))
            {
                bool row = e.LayoutDirection == "row";
                var cursor = new Vector2(rect.x + (e.HasLayout ? e.LayoutPadding : 0), rect.y + (e.HasLayout ? e.LayoutPadding : 0));
                foreach (var child in kids)
                {
                    var childSize = Measure(child, rect);
                    clicked |= DrawElement(child, rect, children, overlay, e.HasLayout ? cursor : (Vector2?)null, depth + 1);
                    if (row) cursor.x += childSize.x + e.LayoutGap; else cursor.y += childSize.y + e.LayoutGap;
                }
            }
            return clicked;
        }
    }
}
