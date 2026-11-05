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
        string m_Message = "";
        float m_MessageUntil;
        float m_Fade, m_FadeTarget, m_FadeSpeed = 4;
        float m_RevealStarted = -1;
        string m_Screen = ""; // "", "start", "pause", "dead", "win"
        bool m_ShowHealth;
        float m_Health = 100, m_MaxHealth = 100;
        Vector3? m_Waypoint;
        bool m_ShowDiagnostics;
        Texture2D m_White;
        Font m_Regular, m_Medium, m_Bold;
        string m_Title = "";
        Texture2D m_Loading, m_Logo;
        readonly Dictionary<string, GUIStyle> m_Styles = new Dictionary<string, GUIStyle>();

        /// <summary>One default UI element as defaultUISettings.js defines it (normalizeDefaultUISettings).</summary>
        sealed class UiElement
        {
            public bool Enabled = true;
            public string Text = "";
            public Dictionary<string, string> Labels = new Dictionary<string, string>();
            public Color Color = Color.white, Background, Border, Accent, Hover;
            public Color Screen = Color.black;
            public float BorderWidth, BorderRadius, Opacity = 1, FontSize = 14, PaddingX, PaddingY, MinWidth, Top, Bottom, ScreenOpacity = 1;
            public int FontWeight = 500;
            public string Label(string key, string fallback) => Labels.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
        }

        // DEFAULT_UI_ELEMENT_DEFINITIONS (as the native player's defaultUiDefinitions).
        UiElement m_WorldMessage, m_InteractionPrompt, m_PickupPrompt, m_StartButton, m_PauseMenu, m_DeadScreen;

        static UiElement Prompt() => new UiElement
        {
            Color = Color.white, Background = new Color(52 / 255f, 52 / 255f, 52 / 255f, 0.8f), Border = Color.white,
            BorderRadius = 4, FontSize = 14, FontWeight = 500, PaddingX = 10, PaddingY = 7, Top = 50,
        };

        void Defaults()
        {
            m_WorldMessage = new UiElement
            {
                Text = "Quest updated", Color = Color.white, Background = CssStyle.ParseColor("#333333", Color.black), Border = CssStyle.ParseColor("#948d8d", Color.gray),
                BorderWidth = 1, BorderRadius = 5, Opacity = 0.8f, FontSize = 20, FontWeight = 500, PaddingX = 15, PaddingY = 10, Bottom = 20,
            };
            m_InteractionPrompt = Prompt(); m_InteractionPrompt.Text = "To {action}";
            m_PickupPrompt = Prompt(); m_PickupPrompt.Text = "To Pick {action}";
            m_StartButton = new UiElement
            {
                Text = "Play", Color = Color.white, Background = CssStyle.ParseColor("#ed0100", Color.red), Border = CssStyle.ParseColor("#ed0100", Color.red),
                BorderRadius = 30, FontSize = 14, FontWeight = 700, PaddingX = 18, PaddingY = 9, MinWidth = 120,
            };
            m_PauseMenu = new UiElement
            {
                Text = "{world}", Color = CssStyle.ParseColor("#f6f6f8", Color.white), Background = CssStyle.ParseColor("rgba(16, 16, 19, 0.94)", Color.black),
                Border = CssStyle.ParseColor("rgba(255, 255, 255, 0.08)", Color.gray), BorderWidth = 1, BorderRadius = 20, FontSize = 14, FontWeight = 600,
                PaddingX = 16, PaddingY = 16, MinWidth = 270, Accent = CssStyle.ParseColor("#dc2a26", Color.red), Hover = CssStyle.ParseColor("rgba(255, 255, 255, 0.07)", Color.gray),
            };
            m_PauseMenu.Labels["eyebrow"] = "Paused"; m_PauseMenu.Labels["resume"] = "Resume"; m_PauseMenu.Labels["restart"] = "Restart";
            m_DeadScreen = new UiElement { Text = "YOU ARE DEAD", Color = Color.red, Border = Color.white, FontSize = 50, FontWeight = 600, Screen = Color.black };
        }

        /// <summary>world.defaultUISettings over the definitions (parseDefaultUi: allowDisable, font and box limits).</summary>
        static void Merge(UiElement target, JsonNode source, bool allowDisable, float maxFont, bool backdrop, bool menu)
        {
            if (!source.IsObject) return;
            var style = source.Obj("style");
            if (allowDisable) target.Enabled = !(source["enabled"].IsBool && !source["enabled"].AsBool);
            var text = source.Str("text");
            if (!string.IsNullOrEmpty(text)) target.Text = text;
            foreach (var pair in source.Obj("labels").Members) if (pair.Value.IsString && pair.Value.AsString.Length > 0) target.Labels[pair.Key] = pair.Value.AsString;
            Color C(string key, Color fallback) => style[key].IsString ? CssStyle.ParseColor(style[key].AsString, fallback) : fallback;
            float F(string key, float fallback, float min, float max) => Mathf.Clamp(style.Float(key, fallback), min, max);
            target.Color = C("color", target.Color); target.Background = C("backgroundColor", target.Background); target.Border = C("borderColor", target.Border);
            target.BorderWidth = F("borderWidth", target.BorderWidth, 0, 12); target.BorderRadius = F("borderRadius", target.BorderRadius, 0, 80);
            target.Opacity = F("opacity", target.Opacity, 0, 1);
            target.FontSize = F("fontSize", target.FontSize, 8, maxFont); target.FontWeight = (int)F("fontWeight", target.FontWeight, 100, 900);
            target.PaddingX = F("paddingX", target.PaddingX, 0, 80); target.PaddingY = F("paddingY", target.PaddingY, 0, 80);
            target.MinWidth = F("minWidth", target.MinWidth, 0, 720);
            target.Top = F("top", target.Top, 0, 100); target.Bottom = F("bottom", target.Bottom, 0, 240);
            if (menu) { target.Accent = C("accentColor", target.Accent); target.Hover = C("hoverColor", target.Hover); }
            if (backdrop) { target.Screen = C("screenColor", target.Screen); target.ScreenOpacity = F("screenOpacity", target.ScreenOpacity, 0, 1); }
        }

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
            Defaults();
            Merge(m_WorldMessage, ui.Obj("worldMessage"), true, 64, false, false);
            Merge(m_InteractionPrompt, ui.Obj("interactionPrompt"), true, 64, false, false);
            Merge(m_PickupPrompt, ui.Obj("pickupPrompt"), true, 64, false, false);
            Merge(m_StartButton, ui.Obj("startButton"), false, 64, false, false);
            Merge(m_PauseMenu, ui.Obj("pauseMenu"), false, 64, false, true);
            Merge(m_DeadScreen, ui.Obj("deadScreen"), false, 160, true, false);
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

        /// <summary>World message: one at a time, bottom centre, for four seconds.</summary>
        public void ShowMessage(string text, float seconds = 4f)
        {
            m_Message = text ?? "";
            m_MessageUntil = Time.unscaledTime + seconds;
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

        public void ShowScreen(string screen)
        {
            // Leaving the start screen: the world fades in from black (held 0.3 s, then 0.7 s ease-out).
            if (m_Screen == "start" && string.IsNullOrEmpty(screen)) m_RevealStarted = Time.unscaledTime;
            m_Screen = screen ?? "";
        }

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
            if (ZimensionInput.KeyDown(KeyCode.F3)) m_ShowDiagnostics = !m_ShowDiagnostics;
        }

        // ---- Drawing ----------------------------------------------------------------------

        void Styles()
        {
            if (m_White) return;
            m_White = Texture2D.whiteTexture;
            m_Regular = Resources.Load<Font>("ZimensionFonts/Ubuntu-Regular");
            m_Medium = Resources.Load<Font>("ZimensionFonts/Ubuntu-Medium");
            m_Bold = Resources.Load<Font>("ZimensionFonts/Ubuntu-Bold");
        }

        /// <summary>A label style for the Zimension UI font (Ubuntu) at a size, weight and colour.</summary>
        GUIStyle Text(float size, int weight, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false)
        {
            var key = $"{size:0.#}|{weight}|{ColorUtility.ToHtmlStringRGBA(color)}|{anchor}|{wrap}";
            if (m_Styles.TryGetValue(key, out var cached)) return cached;
            var style = new GUIStyle(GUI.skin.label)
            {
                font = weight >= 700 ? (m_Bold ?? m_Medium) : weight >= 500 ? (m_Medium ?? m_Regular) : m_Regular,
                fontSize = Mathf.RoundToInt(size), alignment = anchor, wordWrap = wrap, richText = false,
                fontStyle = !m_Bold && weight >= 600 ? FontStyle.Bold : FontStyle.Normal,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
            };
            style.normal.textColor = color;
            m_Styles[key] = style;
            return style;
        }

        void Fill(Rect r, Color c) => Box(r, c, 0, Color.clear, 0);

        /// <summary>A rounded box with an optional border (CSS background, border, border-radius).</summary>
        void Box(Rect r, Color background, float radius, Color border, float borderWidth)
        {
            if (background.a > 0) GUI.DrawTexture(r, m_White, ScaleMode.StretchToFill, true, 0, background, 0, radius);
            if (borderWidth > 0 && border.a > 0) GUI.DrawTexture(r, m_White, ScaleMode.StretchToFill, true, 0, border, borderWidth, radius);
        }

        static Color WithOpacity(Color c, float opacity) => new Color(c.r, c.g, c.b, c.a * opacity);

        string Format(string text, string action) => (text ?? "").Replace("{action}", action ?? "").Replace("{world}", m_Title).Replace("{mode}", "");

        void OnGUI()
        {
            Styles();
            float w = Screen.width, h = Screen.height;
            DrawCustomHud(w, h);
            DrawWaypoint(w, h);
            if (m_Screen.Length == 0)
            {
                if (m_Prompt.Length > 0) PromptBox(m_Pickup ? m_PickupPrompt : m_InteractionPrompt, m_Prompt, w, h);
                if (m_Message.Length > 0 && Time.unscaledTime < m_MessageUntil && m_WorldMessage.Enabled) MessageBox(w, h);
                if (m_ShowHealth) HealthBar(w, h);
            }
            DrawOverlays(w, h);
            if (m_Fade > 0.001f) Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, m_Fade));
            if (m_RevealStarted >= 0)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - m_RevealStarted - 0.3f) / 0.7f);
                float alpha = 1 - (1 - (1 - t) * (1 - t));
                if (alpha > 0.001f) Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, alpha)); else m_RevealStarted = -1;
            }
            DrawScreens(w, h);
            if (m_ShowDiagnostics && m_World) DrawDiagnostics(w, h);
        }

        /// <summary>Interaction / pickup prompt: an "F" key cap and "To {action}", centred at its top %.</summary>
        void PromptBox(UiElement e, string action, float w, float h)
        {
            if (!e.Enabled) return;
            var style = Text(e.FontSize, e.FontWeight, WithOpacity(e.Color, e.Opacity));
            var content = new GUIContent(Format(e.Text, action));
            var size = style.CalcSize(content);
            const float key = 20, gap = 10;
            float bw = e.PaddingX * 2 + key + gap + size.x, bh = e.PaddingY * 2 + Mathf.Max(key, size.y);
            var box = new Rect((w - bw) / 2, h * e.Top / 100, bw, bh);
            Box(box, WithOpacity(e.Background, e.Opacity), e.BorderRadius, WithOpacity(e.Border, e.Opacity), e.BorderWidth);
            var cap = new Rect(box.x + e.PaddingX, box.y + (bh - key) / 2, key, key);
            Box(cap, Color.clear, 2, WithOpacity(CssStyle.ParseColor("#f1f1f2", Color.white), e.Opacity), 1);
            GUI.Label(cap, "F", Text(16, 500, WithOpacity(e.Color, e.Opacity), TextAnchor.MiddleCenter));
            GUI.Label(new Rect(cap.xMax + gap, box.y, size.x + 2, bh), content, style);
        }

        /// <summary>World message: bottom centre, at most 80% wide.</summary>
        void MessageBox(float w, float h)
        {
            var e = m_WorldMessage;
            var style = Text(e.FontSize, e.FontWeight, WithOpacity(e.Color, e.Opacity), TextAnchor.UpperCenter, wrap: true);
            var content = new GUIContent(m_Message);
            float maxText = w * 0.8f - e.PaddingX * 2;
            var natural = style.CalcSize(content);
            float textWidth = Mathf.Min(natural.x + 2, maxText);
            float textHeight = style.CalcHeight(content, textWidth);
            float bw = Mathf.Max(textWidth + e.PaddingX * 2, e.MinWidth), bh = textHeight + e.PaddingY * 2;
            var box = new Rect((w - bw) / 2, h - e.Bottom - bh, bw, bh);
            Box(box, WithOpacity(e.Background, e.Opacity), e.BorderRadius, WithOpacity(e.Border, e.Opacity), e.BorderWidth);
            GUI.Label(new Rect((w - textWidth) / 2, box.y + e.PaddingY, textWidth, textHeight), content, style);
        }

        void HealthBar(float w, float h)
        {
            var r = new Rect(24, h - 40, 220, 14);
            Box(r, new Color(0, 0, 0, 0.55f), 7, Color.clear, 0);
            float f = Mathf.Clamp01(m_Health / m_MaxHealth);
            if (f > 0) Box(new Rect(r.x + 2, r.y + 2, (r.width - 4) * f, r.height - 4), CssStyle.ParseColor("#dc2a26", Color.red), 5, Color.clear, 0);
            GUI.Label(new Rect(r.x, r.y - 22, 220, 20), $"{Mathf.CeilToInt(m_Health)}", Text(14, 700, Color.white));
        }

        bool PillButton(Rect r, string label, UiElement e, bool primary = true)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            var bg = primary ? e.Background : (hover ? e.Hover : new Color(1, 1, 1, 0.04f));
            if (primary && hover) bg = Color.Lerp(bg, Color.white, 0.12f);
            Box(r, bg, e.BorderRadius, e.Border, e.BorderWidth);
            GUI.Label(r, label, Text(e.FontSize, e.FontWeight, e.Color, TextAnchor.MiddleCenter));
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        Rect ButtonRect(UiElement e, string label, float cx, float y)
        {
            var size = Text(e.FontSize, e.FontWeight, e.Color).CalcSize(new GUIContent(label));
            float bw = Mathf.Max(e.MinWidth, size.x + e.PaddingX * 2), bh = size.y + e.PaddingY * 2;
            return new Rect(cx - bw / 2, y, bw, bh);
        }

        void DrawScreens(float w, float h)
        {
            if (m_Screen.Length == 0) return;
            switch (m_Screen)
            {
                case "start":
                {
                    Fill(new Rect(0, 0, w, h), Color.black);
                    if (m_Loading) GUI.DrawTexture(new Rect(0, 0, w, h), m_Loading, ScaleMode.ScaleAndCrop);
                    if (m_Logo) GUI.DrawTexture(new Rect((w - 160) / 2, h * 0.2f, 160, 160), m_Logo, ScaleMode.ScaleToFit);
                    GUI.Label(new Rect(0, h * 0.45f, w, 48), m_Title, Text(32, 700, Color.white, TextAnchor.MiddleCenter));
                    var label = Format(m_StartButton.Text, "");
                    if (PillButton(ButtonRect(m_StartButton, label, w / 2, h * 0.56f), label, m_StartButton)) m_World.BeginPlay();
                    break;
                }
                case "pause":
                {
                    var e = m_PauseMenu;
                    Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, 0.45f));
                    float pw = Mathf.Max(e.MinWidth, 300), ph = 230;
                    var panel = new Rect((w - pw) / 2, (h - ph) / 2, pw, ph);
                    Box(panel, e.Background, e.BorderRadius, e.Border, e.BorderWidth);
                    GUI.Label(new Rect(panel.x + e.PaddingX, panel.y + e.PaddingY, pw - e.PaddingX * 2, 18), e.Label("eyebrow", "Paused").ToUpperInvariant(), Text(11, 600, e.Accent));
                    GUI.Label(new Rect(panel.x + e.PaddingX, panel.y + e.PaddingY + 20, pw - e.PaddingX * 2, 30), Format(e.Text, ""), Text(20, 700, e.Color));
                    var resume = new Rect(panel.x + e.PaddingX, panel.y + 90, pw - e.PaddingX * 2, 44);
                    var restart = new Rect(resume.x, resume.yMax + 10, resume.width, 44);
                    var primary = new UiElement { Background = e.Accent, Border = Color.clear, BorderRadius = 12, FontSize = e.FontSize, FontWeight = e.FontWeight, Color = Color.white };
                    var secondary = new UiElement { Hover = e.Hover, Border = e.Border, BorderWidth = 1, BorderRadius = 12, FontSize = e.FontSize, FontWeight = e.FontWeight, Color = e.Color };
                    if (PillButton(resume, e.Label("resume", "Resume"), primary)) m_World.SetPaused(false);
                    if (PillButton(restart, e.Label("restart", "Restart"), secondary, primary: false)) { m_World.SetPaused(false); m_World.ResetWorld(); }
                    break;
                }
                case "dead":
                {
                    var e = m_DeadScreen;
                    Fill(new Rect(0, 0, w, h), WithOpacity(e.Screen, e.ScreenOpacity * 0.85f));
                    GUI.Label(new Rect(0, h * 0.38f, w, e.FontSize * 1.4f), Format(e.Text, ""), Text(e.FontSize, e.FontWeight, e.Color, TextAnchor.MiddleCenter));
                    var label = e.Label("respawn", "Respawn");
                    if (PillButton(ButtonRect(m_StartButton, label, w / 2, h * 0.56f), label, m_StartButton)) m_World.RespawnAfterDeath();
                    break;
                }
                case "win":
                {
                    Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, 0.75f));
                    GUI.Label(new Rect(0, h * 0.38f, w, 70), "YOU WIN", Text(50, 700, CssStyle.ParseColor("#ffe45d", Color.yellow), TextAnchor.MiddleCenter));
                    const string label = "Play again";
                    if (PillButton(ButtonRect(m_StartButton, label, w / 2, h * 0.56f), label, m_StartButton)) { m_Screen = ""; m_World.ResetWorld(); }
                    break;
                }
            }
        }

        /// <summary>
        /// Engine.updateWaypoint: the item centre on screen, a 30 px box offset by -15 and clamped to the
        /// window (behind the camera it parks at the right/bottom edge); drawn as .way-point-border, a
        /// 5 px #ffff5b border turned 45 degrees round a 20 px fill.
        /// </summary>
        void DrawWaypoint(float w, float h)
        {
            if (!m_Waypoint.HasValue || !m_World) return;
            var cam = m_World.Player ? m_World.Player.PlayerCamera : Camera.main;
            if (!cam) return;
            var p = cam.WorldToScreenPoint(m_Waypoint.Value);
            float x = 5000, y = 5000;
            if (p.z > 0) { x = p.x; y = h - p.y; }
            float left = Mathf.Max(0, x - 15), top = Mathf.Max(0, y - 15);
            if (left > w) left = w - 30;
            if (top > h) top = h - 30;
            var centre = new Vector2(left + 20, top + 20);
            var yellow = new Color(1, 1, 0x5b / 255f, 1);
            var matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(45, centre);
            Box(new Rect(centre.x - 20, centre.y - 20, 40, 40), Color.clear, 0, yellow, 5);
            Fill(new Rect(centre.x - 10, centre.y - 10, 20, 20), yellow);
            GUI.matrix = matrix;
        }

        void DrawDiagnostics(float w, float h)
        {
            var r = new Rect(12, 12, Mathf.Min(560, w - 24), Mathf.Min(420, h - 24));
            Box(r, new Color(0.06f, 0.06f, 0.08f, 0.85f), 10, new Color(1, 1, 1, 0.08f), 1);
            GUI.Label(new Rect(r.x + 10, r.y + 8, r.width - 20, r.height - 16), m_World.DiagnosticsText(), Text(12, 400, Color.white, TextAnchor.UpperLeft, wrap: true));
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
            int weight = s.Get("fontWeight") == "bold" ? 700 : Mathf.RoundToInt(s.Number("fontWeight", 400));
            var anchor = e.Type == "button" || s.Get("textAlign") == "center" ? TextAnchor.MiddleCenter : s.Get("textAlign") == "right" ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
            var color = s.Color("color", e.Type == "text-box" ? new Color(0.94f, 0.94f, 0.94f) : Color.white);
            return Text(s.Number("fontSize", e.Type == "text-box" ? 14 : 16), weight, color, anchor, wrap: true);
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
            float radius = Mathf.Min(s.Length("borderRadius", Mathf.Min(rect.width, rect.height), e.Type == "button" ? 10 : e.Type == "text-box" ? 4 : 0), Mathf.Min(rect.width, rect.height) / 2);
            var borderColor = s.Color("borderColor", Color.clear);
            float borderWidth = s.Length("borderWidth", 0, 0);
            Box(rect, new Color(background.r, background.g, background.b, background.a * opacity), radius, WithOpacity(borderColor, opacity), borderWidth);
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
