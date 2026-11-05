using UnityEngine;

namespace Zimension.Kit
{
    /// <summary>
    /// Zimension's default play UI, drawn with IMGUI so it needs no prefabs: the interaction /
    /// pickup prompt (an "F" key cap and "To {action}"), world messages, the crosshair and ammo
    /// while holding a gun, the health bar, the waypoint marker, fades, and the start, pause, dead
    /// and win screens. Every colour, size and text is a field here; replace this component with
    /// your own UI by calling the same methods.
    /// </summary>
    [AddComponentMenu("Zimension/Game HUD")]
    public sealed class GameHud : MonoBehaviour
    {
        public static GameHud Current { get; private set; }
        public enum ScreenKind { None, Start, Pause, Dead, Win }

        [Header("Texts")]
        public string worldTitle = "";
        public string interactionText = "To {action}";
        public string pickupText = "To Pick {action}";
        public string startLabel = "Play";
        public string deadText = "YOU ARE DEAD";
        public string winText = "YOU WIN";
        [Header("Prompt")]
        public Color promptText = Color.white;
        public Color promptBackground = new Color(52 / 255f, 52 / 255f, 52 / 255f, 0.8f);
        [Range(0, 100)] public float promptTop = 50;
        public int promptFontSize = 14;
        [Header("Message")]
        public Color messageText = Color.white;
        public Color messageBackground = new Color(0.2f, 0.2f, 0.2f, 0.8f);
        public Color messageBorder = new Color(0x94 / 255f, 0x8d / 255f, 0x8d / 255f, 0.8f);
        public int messageFontSize = 20;
        public float messageBottom = 20;
        [Header("Screens")]
        public Color accent = new Color(0xed / 255f, 0x01 / 255f, 0);
        public Color panel = new Color(16 / 255f, 16 / 255f, 19 / 255f, 0.94f);
        public Color deadColor = Color.red;
        public bool showStartScreen = true;
        [Header("Gameplay")]
        public bool showHealth = true;
        public Color healthColor = new Color(0xdc / 255f, 0x2a / 255f, 0x26 / 255f);
        public Color waypointColor = new Color(1, 1, 0x5b / 255f);

        string m_Prompt = ""; bool m_Pickup;
        string m_Message = ""; float m_MessageUntil;
        float m_Fade, m_FadeTarget, m_FadeSpeed = 4;
        Transform m_Waypoint;
        ScreenKind m_Screen;
        Font m_Regular, m_Medium, m_Bold;
        readonly System.Collections.Generic.Dictionary<string, GUIStyle> m_Styles = new System.Collections.Generic.Dictionary<string, GUIStyle>();

        public ScreenKind Screen_ => m_Screen;
        public bool BlocksInput => m_Screen != ScreenKind.None;

        void Awake()
        {
            Current = this;
            m_Regular = Resources.Load<Font>("ZimensionFonts/Ubuntu-Regular");
            m_Medium = Resources.Load<Font>("ZimensionFonts/Ubuntu-Medium");
            m_Bold = Resources.Load<Font>("ZimensionFonts/Ubuntu-Bold");
            if (showStartScreen) m_Screen = ScreenKind.Start;
        }

        void OnDestroy() { if (Current == this) Current = null; }

        public void SetPrompt(string action, bool pickup) { m_Prompt = action ?? ""; m_Pickup = pickup; }
        public void ShowMessage(string text, float seconds = 4f) { m_Message = text ?? ""; m_MessageUntil = Time.unscaledTime + Mathf.Max(0.5f, seconds); }
        public void SetWaypoint(Transform target) => m_Waypoint = target;
        public void ClearWaypoint(Transform target) { if (m_Waypoint == target) m_Waypoint = null; }
        public void FadeOut(float seconds) { m_FadeTarget = 1; m_FadeSpeed = seconds > 0 ? 1 / seconds : 100; }
        public void FadeIn(float seconds) { m_FadeTarget = 0; m_FadeSpeed = seconds > 0 ? 1 / seconds : 100; }
        public void ShowScreen(ScreenKind screen) => m_Screen = screen;

        void Update() => m_Fade = Mathf.MoveTowards(m_Fade, m_FadeTarget, m_FadeSpeed * Time.unscaledDeltaTime);

        GUIStyle Text(int size, int weight, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false)
        {
            var key = $"{size}|{weight}|{ColorUtility.ToHtmlStringRGBA(color)}|{anchor}|{wrap}";
            if (m_Styles.TryGetValue(key, out var s)) return s;
            s = new GUIStyle(GUI.skin.label)
            {
                font = weight >= 700 ? (m_Bold ?? m_Medium) : weight >= 500 ? (m_Medium ?? m_Regular) : m_Regular,
                fontSize = size, alignment = anchor, wordWrap = wrap, padding = new RectOffset(0, 0, 0, 0),
            };
            s.normal.textColor = color;
            m_Styles[key] = s;
            return s;
        }

        static void Box(Rect r, Color fill, float radius, Color border = default, float borderWidth = 0)
        {
            if (fill.a > 0) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, fill, 0, radius);
            if (borderWidth > 0 && border.a > 0) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, border, borderWidth, radius);
        }

        void OnGUI()
        {
            float w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
            var player = PlayerController.Current;
            Waypoint(w, h, player);
            if (m_Screen == ScreenKind.None)
            {
                if (m_Prompt.Length > 0) Prompt(w, h);
                if (m_Message.Length > 0 && Time.unscaledTime < m_MessageUntil) Message(w, h);
                if (player)
                {
                    if (showHealth) HealthBar(w, h, player);
                    var weapon = player.CurrentWeapon;
                    if (weapon && weapon.type == Weapon.WeaponType.Gun) { Crosshair(w, h); Ammo(w, h, player, weapon); }
                }
            }
            if (m_Fade > 0.001f) Box(new Rect(0, 0, w, h), new Color(0, 0, 0, m_Fade), 0);
            Screens(w, h);
        }

        void Prompt(float w, float h)
        {
            var style = Text(promptFontSize, 500, promptText);
            var content = new GUIContent((m_Pickup ? pickupText : interactionText).Replace("{action}", m_Prompt));
            var size = style.CalcSize(content);
            const float key = 20, gap = 10, px = 10, py = 7;
            float bw = px * 2 + key + gap + size.x, bh = py * 2 + Mathf.Max(key, size.y);
            var box = new Rect((w - bw) / 2, h * promptTop / 100, bw, bh);
            Box(box, promptBackground, 4);
            var cap = new Rect(box.x + px, box.y + (bh - key) / 2, key, key);
            Box(cap, Color.clear, 2, new Color(0.945f, 0.945f, 0.95f), 1);
            GUI.Label(cap, "F", Text(16, 500, promptText, TextAnchor.MiddleCenter));
            GUI.Label(new Rect(cap.xMax + gap, box.y, size.x + 2, bh), content, style);
        }

        void Message(float w, float h)
        {
            var style = Text(messageFontSize, 500, messageText, TextAnchor.UpperCenter, wrap: true);
            var content = new GUIContent(m_Message);
            float tw = Mathf.Min(style.CalcSize(content).x + 2, w * 0.8f - 30), th = style.CalcHeight(content, tw);
            var box = new Rect((w - tw) / 2 - 15, h - messageBottom - th - 20, tw + 30, th + 20);
            Box(box, messageBackground, 5, messageBorder, 1);
            GUI.Label(new Rect((w - tw) / 2, box.y + 10, tw, th), content, style);
        }

        void HealthBar(float w, float h, PlayerController p)
        {
            var r = new Rect(24, h - 40, 220, 14);
            Box(r, new Color(0, 0, 0, 0.55f), 7);
            float f = p.maxHealth > 0 ? Mathf.Clamp01(p.health / p.maxHealth) : 0;
            if (f > 0) Box(new Rect(r.x + 2, r.y + 2, (r.width - 4) * f, r.height - 4), healthColor, 5);
            GUI.Label(new Rect(r.x, r.y - 22, 220, 20), Mathf.CeilToInt(p.health).ToString(), Text(14, 700, Color.white));
        }

        void Crosshair(float w, float h)
        {
            var c = new Color(1, 1, 1, 0.9f);
            Box(new Rect(w / 2 - 1, h / 2 - 9, 2, 6), c, 0); Box(new Rect(w / 2 - 1, h / 2 + 3, 2, 6), c, 0);
            Box(new Rect(w / 2 - 9, h / 2 - 1, 6, 2), c, 0); Box(new Rect(w / 2 + 3, h / 2 - 1, 6, 2), c, 0);
        }

        void Ammo(float w, float h, PlayerController p, Weapon weapon)
        {
            var text = weapon.infiniteAmmo ? "∞" : $"{weapon.Rounds} / {p.Ammo(weapon.weaponClass)}";
            var r = new Rect(w - 184, h - 58, 160, 38);
            Box(r, new Color(0, 0, 0, 0.55f), 8);
            GUI.Label(new Rect(r.x + 12, r.y, 70, r.height), weapon.weaponName, Text(12, 500, new Color(1, 1, 1, 0.7f)));
            GUI.Label(new Rect(r.x, r.y, r.width - 12, r.height), text, Text(18, 700, Color.white, TextAnchor.MiddleRight));
        }

        void Waypoint(float w, float h, PlayerController player)
        {
            if (!m_Waypoint || !m_Waypoint.gameObject.activeInHierarchy) return;
            var cam = player && player.PlayerCamera ? player.PlayerCamera : Camera.main;
            if (!cam) return;
            var p = cam.WorldToScreenPoint(m_Waypoint.position);
            float x = p.z > 0 ? p.x : 5000, y = p.z > 0 ? h - p.y : 5000;
            float left = Mathf.Min(Mathf.Max(0, x - 15), w - 30), top = Mathf.Min(Mathf.Max(0, y - 15), h - 30);
            var centre = new Vector2(left + 20, top + 20);
            var matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(45, centre);
            Box(new Rect(centre.x - 20, centre.y - 20, 40, 40), Color.clear, 0, waypointColor, 5);
            Box(new Rect(centre.x - 10, centre.y - 10, 20, 20), waypointColor, 0);
            GUI.matrix = matrix;
        }

        bool Button(Rect r, string label, Color fill)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            Box(r, hover ? Color.Lerp(fill, Color.white, 0.12f) : fill, r.height / 2);
            GUI.Label(r, label, Text(14, 700, Color.white, TextAnchor.MiddleCenter));
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        void Screens(float w, float h)
        {
            var game = GameManager.Current;
            var button = new Rect(w / 2 - 70, h * 0.56f, 140, 38);
            switch (m_Screen)
            {
                case ScreenKind.Start:
                    Box(new Rect(0, 0, w, h), Color.black, 0);
                    GUI.Label(new Rect(0, h * 0.45f, w, 48), worldTitle, Text(32, 700, Color.white, TextAnchor.MiddleCenter));
                    if (Button(button, startLabel, accent)) { m_Screen = ScreenKind.None; game?.Begin(); }
                    break;
                case ScreenKind.Pause:
                {
                    Box(new Rect(0, 0, w, h), new Color(0, 0, 0, 0.45f), 0);
                    var r = new Rect((w - 300) / 2, (h - 230) / 2, 300, 230);
                    Box(r, panel, 20, new Color(1, 1, 1, 0.08f), 1);
                    GUI.Label(new Rect(r.x + 16, r.y + 16, 268, 18), "PAUSED", Text(11, 600, accent));
                    GUI.Label(new Rect(r.x + 16, r.y + 36, 268, 30), worldTitle, Text(20, 700, Color.white));
                    if (Button(new Rect(r.x + 16, r.y + 90, 268, 44), "Resume", accent)) game?.Resume();
                    if (Button(new Rect(r.x + 16, r.y + 144, 268, 44), "Restart", new Color(1, 1, 1, 0.07f))) game?.ResetWorld();
                    break;
                }
                case ScreenKind.Dead:
                    Box(new Rect(0, 0, w, h), new Color(0, 0, 0, 0.85f), 0);
                    GUI.Label(new Rect(0, h * 0.38f, w, 70), deadText, Text(50, 600, deadColor, TextAnchor.MiddleCenter));
                    if (Button(button, "Respawn", accent)) game?.RespawnPlayer();
                    break;
                case ScreenKind.Win:
                    Box(new Rect(0, 0, w, h), new Color(0, 0, 0, 0.75f), 0);
                    GUI.Label(new Rect(0, h * 0.38f, w, 70), winText, Text(50, 700, new Color(1, 0.89f, 0.36f), TextAnchor.MiddleCenter));
                    if (Button(button, "Play again", accent)) game?.ResetWorld();
                    break;
            }
        }
    }
}
