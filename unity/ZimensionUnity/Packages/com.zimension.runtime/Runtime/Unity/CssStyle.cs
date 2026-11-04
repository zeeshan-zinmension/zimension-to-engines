using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// The subset of CSS the Zimension custom UI saves (React style objects and customStyleText):
    /// lengths in px or %, colours (#rgb, #rrggbb, #rrggbbaa, rgb(), rgba(), common names),
    /// numbers. Used to place and paint custom HUD/overlay elements with IMGUI.
    /// </summary>
    public sealed class CssStyle
    {
        readonly Dictionary<string, string> m_Values = new Dictionary<string, string>();

        public static CssStyle From(JsonNode style, string customStyleText = null)
        {
            var css = new CssStyle();
            foreach (var pair in style.Members)
            {
                var v = pair.Value;
                css.m_Values[pair.Key] = v.IsString ? v.AsString : v.IsNumber ? EventValue.DisplayNumber(v.AsNumber) : v.IsBool ? (v.AsBool ? "true" : "false") : "";
            }
            if (!string.IsNullOrEmpty(customStyleText))
            {
                foreach (var declaration in customStyleText.Split(';'))
                {
                    int colon = declaration.IndexOf(':');
                    if (colon <= 0) continue;
                    var key = Camel(declaration.Substring(0, colon).Trim());
                    css.m_Values[key] = declaration.Substring(colon + 1).Trim();
                }
            }
            return css;
        }

        static string Camel(string kebab)
        {
            var parts = kebab.Split('-');
            for (int i = 1; i < parts.Length; i++) if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Join("", parts);
        }

        public string Get(string key, string fallback = "") => m_Values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
        public bool Has(string key) => m_Values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);

        /// <summary>A length against a reference size (for %), or the fallback when unset/auto.</summary>
        public float Length(string key, float reference, float fallback)
        {
            if (!m_Values.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw)) return fallback;
            return ParseLength(raw, reference, fallback);
        }

        public static float ParseLength(string raw, float reference, float fallback)
        {
            raw = raw.Trim().ToLowerInvariant();
            if (raw == "auto" || raw.Length == 0) return fallback;
            float scale = 1;
            if (raw.EndsWith("%")) { scale = reference / 100f; raw = raw.Substring(0, raw.Length - 1); }
            else if (raw.EndsWith("px")) raw = raw.Substring(0, raw.Length - 2);
            else if (raw.EndsWith("vw")) { scale = Screen.width / 100f; raw = raw.Substring(0, raw.Length - 2); }
            else if (raw.EndsWith("vh")) { scale = Screen.height / 100f; raw = raw.Substring(0, raw.Length - 2); }
            else if (raw.EndsWith("rem") || raw.EndsWith("em")) { scale = 16; raw = raw.TrimEnd('r', 'e', 'm'); }
            return float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n * scale : fallback;
        }

        public float Number(string key, float fallback)
        {
            if (!m_Values.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw)) return fallback;
            return ParseLength(raw, 0, fallback);
        }

        public Color Color(string key, Color fallback) => m_Values.TryGetValue(key, out var raw) ? ParseColor(raw, fallback) : fallback;

        public static Color ParseColor(string text, Color fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            var s = text.Trim().ToLowerInvariant();
            switch (s)
            {
                case "transparent": return new Color(0, 0, 0, 0);
                case "white": return UnityEngine.Color.white;
                case "black": return UnityEngine.Color.black;
                case "red": return UnityEngine.Color.red;
                case "green": return new Color(0, 0.5f, 0);
                case "blue": return UnityEngine.Color.blue;
                case "yellow": return UnityEngine.Color.yellow;
                case "gray": case "grey": return new Color(0.5f, 0.5f, 0.5f);
                case "orange": return new Color(1, 0.647f, 0);
            }
            if (s.StartsWith("#"))
            {
                var hex = s.Substring(1);
                if (hex.Length == 3 || hex.Length == 4) { var expanded = ""; foreach (var c in hex) expanded += new string(c, 2); hex = expanded; }
                if ((hex.Length == 6 || hex.Length == 8) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                {
                    if (hex.Length == 6) return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1);
                    return new Color(((v >> 24) & 255) / 255f, ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
                }
                return fallback;
            }
            if (s.StartsWith("rgb"))
            {
                int open = s.IndexOf('('), close = s.LastIndexOf(')');
                if (open < 0 || close <= open) return fallback;
                var parts = s.Substring(open + 1, close - open - 1).Replace('/', ',').Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) return fallback;
                float Channel(string p) => p.EndsWith("%") ? ParseLength(p, 255, 0) / 255f : ParseLength(p, 0, 0) / 255f;
                float a = parts.Length > 3 ? (parts[3].EndsWith("%") ? ParseLength(parts[3], 1, 1) : ParseLength(parts[3], 0, 1)) : 1;
                return new Color(Channel(parts[0]), Channel(parts[1]), Channel(parts[2]), a);
            }
            return fallback;
        }
    }
}
