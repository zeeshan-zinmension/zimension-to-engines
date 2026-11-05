using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Zimension.Core
{
    /// <summary>
    /// A variable value as the web runtime holds it: a JavaScript number, boolean or string
    /// (or undefined). Arithmetic, comparison and display follow Engine.js / util.services
    /// (performArithmetic, performComparisonOperation, String(value)) so flows reach the same
    /// results in Unity as in the browser.
    /// </summary>
    public readonly struct EventValue : IEquatable<EventValue>
    {
        public enum ValueKind : byte { Undefined, Number, Boolean, Text }

        public readonly ValueKind Kind;
        public readonly double Number;
        public readonly bool Boolean;
        public readonly string Text;

        EventValue(ValueKind kind, double number, bool boolean, string text)
        {
            Kind = kind; Number = number; Boolean = boolean; Text = text ?? string.Empty;
        }

        public static readonly EventValue Undefined = default;
        public static EventValue Of(double n) => new EventValue(ValueKind.Number, n, false, null);
        public static EventValue Of(bool b) => new EventValue(ValueKind.Boolean, 0, b, null);
        public static EventValue Of(string s) => new EventValue(ValueKind.Text, 0, false, s);

        public bool IsDefined => Kind != ValueKind.Undefined;

        /// <summary>A saved JSON value: numbers, booleans and strings; anything else is undefined.</summary>
        public static EventValue FromJson(JsonNode node)
        {
            if (node == null) return Undefined;
            if (node.IsBool) return Of(node.AsBool);
            if (node.IsNumber) return Of(node.AsNumber);
            if (node.IsString) return Of(node.AsString);
            return Undefined;
        }

        public JsonNode ToJson()
        {
            switch (Kind)
            {
                case ValueKind.Number: return new JsonNode(Number);
                case ValueKind.Boolean: return new JsonNode(Boolean);
                case ValueKind.Text: return new JsonNode(Text);
                default: return JsonNode.Null;
            }
        }

        public override string ToString() => Display(this);
        public bool Equals(EventValue other) => Kind == other.Kind && Number.Equals(other.Number) && Boolean == other.Boolean && Text == other.Text;
        public override bool Equals(object obj) => obj is EventValue v && Equals(v);
        public override int GetHashCode() => ((int)Kind * 397) ^ Number.GetHashCode() ^ Boolean.GetHashCode() ^ (Text?.GetHashCode() ?? 0);

        // ---- JavaScript conversions --------------------------------------------------------

        public static string Trim(string s) => (s ?? string.Empty).Trim(' ', '\t', '\r', '\n');

        /// <summary>JavaScript String(value).</summary>
        public static string Display(EventValue v)
        {
            switch (v.Kind)
            {
                case ValueKind.Boolean: return v.Boolean ? "true" : "false";
                case ValueKind.Text: return v.Text;
                case ValueKind.Number: return DisplayNumber(v.Number);
                default: return string.Empty;
            }
        }

        public static string DisplayNumber(double n)
        {
            if (double.IsNaN(n)) return "NaN";
            if (double.IsPositiveInfinity(n)) return "Infinity";
            if (double.IsNegativeInfinity(n)) return "-Infinity";
            if (n == 0) return "0"; // also -0
            if (n == Math.Floor(n) && Math.Abs(n) < 1e21) return n.ToString("F0", CultureInfo.InvariantCulture);
            var s = n.ToString("R", CultureInfo.InvariantCulture);
            int e = s.IndexOf('E');
            if (e < 0) return s;
            // .NET "1E-07" -> JavaScript "1e-7".
            var mantissa = s.Substring(0, e);
            var exponent = int.Parse(s.Substring(e + 1), CultureInfo.InvariantCulture);
            return mantissa + "e" + (exponent >= 0 ? "+" : "-") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>JavaScript Number(value).</summary>
        public static double ToNumber(EventValue v)
        {
            switch (v.Kind)
            {
                case ValueKind.Number: return v.Number;
                case ValueKind.Boolean: return v.Boolean ? 1 : 0;
                case ValueKind.Text:
                {
                    var s = Trim(v.Text);
                    if (s.Length == 0) return 0;
                    if (s == "Infinity" || s == "+Infinity") return double.PositiveInfinity;
                    if (s == "-Infinity") return double.NegativeInfinity;
                    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
                    return FullNumber.IsMatch(s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
                }
                default: return double.NaN;
            }
        }

        static readonly Regex FullNumber = new Regex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$", RegexOptions.CultureInvariant);
        static readonly Regex NumberPrefix = new Regex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?", RegexOptions.CultureInvariant);

        /// <summary>JavaScript parseFloat(value): the longest numeric prefix of its string form.</summary>
        public static double ParseFloat(EventValue v)
        {
            if (v.Kind == ValueKind.Number) return v.Number;
            if (v.Kind != ValueKind.Text) return double.NaN; // parseFloat(true) is NaN
            var s = Trim(v.Text);
            if (s.StartsWith("Infinity", StringComparison.Ordinal) || s.StartsWith("+Infinity", StringComparison.Ordinal)) return double.PositiveInfinity;
            if (s.StartsWith("-Infinity", StringComparison.Ordinal)) return double.NegativeInfinity;
            var m = NumberPrefix.Match(s);
            return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
        }

        /// <summary>performArithmetic(parseFloat(a), parseFloat(b), op).</summary>
        public static EventValue Arithmetic(EventValue av, EventValue bv, string op)
        {
            double a = ParseFloat(av), b = ParseFloat(bv);
            op = op ?? string.Empty;
            // Multiply/divide arrive as the dropdown glyph, or mis-encoded in older saves.
            switch (op)
            {
                case "+": return Of(a + b);
                case "-": return Of(a - b);
                case "*": case "x": case "X": case "×": case "Ã—": return Of(a * b);
                case "/": case "÷": case "Ã·": return Of(a / b);
                case "!": return Of(!(b != 0 && !double.IsNaN(b)));
                case "=": return Of(b);
                default: return Of(0.0);
            }
        }

        static bool Loose(EventValue x, EventValue y)
        {
            if (x.Kind == y.Kind)
            {
                switch (x.Kind)
                {
                    case ValueKind.Number: return x.Number == y.Number;
                    case ValueKind.Boolean: return x.Boolean == y.Boolean;
                    case ValueKind.Text: return x.Text == y.Text;
                    default: return true;
                }
            }
            if (x.Kind == ValueKind.Undefined || y.Kind == ValueKind.Undefined) return false;
            if (x.Kind == ValueKind.Boolean) return Loose(Of(ToNumber(x)), y);
            if (y.Kind == ValueKind.Boolean) return Loose(x, Of(ToNumber(y)));
            return ToNumber(x) == ToNumber(y); // number and string
        }

        /// <summary>performComparisonOperation(a, b, op).</summary>
        public static bool Compare(EventValue a, EventValue b, string op)
        {
            switch (op)
            {
                case "=": case "==": return Loose(a, b);
                case "!=": return !Loose(a, b);
                case "===": return a.Kind == b.Kind && Loose(a, b);
                case ">": case "<": case ">=": case "<=":
                {
                    if (a.Kind == ValueKind.Text && b.Kind == ValueKind.Text)
                    {
                        int c = string.CompareOrdinal(a.Text, b.Text);
                        return op == ">" ? c > 0 : op == "<" ? c < 0 : op == ">=" ? c >= 0 : c <= 0;
                    }
                    double x = ToNumber(a), y = ToNumber(b);
                    return op == ">" ? x > y : op == "<" ? x < y : op == ">=" ? x >= y : x <= y;
                }
                default: return false;
            }
        }

        /// <summary>logicSwitch normalizeBoolean: true/false/1/0 strings and numbers become booleans.</summary>
        public static EventValue AsBoolean(EventValue v)
        {
            if (v.Kind == ValueKind.Number) return Of(v.Number != 0);
            if (v.Kind == ValueKind.Text)
            {
                var s = Trim(v.Text).ToLowerInvariant();
                if (s == "true" || s == "1") return Of(true);
                if (s == "false" || s == "0") return Of(false);
            }
            return v;
        }

        public static bool LooksBoolean(EventValue v)
        {
            if (v.Kind == ValueKind.Boolean) return true;
            var s = v.Kind == ValueKind.Text ? Trim(v.Text).ToLowerInvariant() : string.Empty;
            return s == "true" || s == "false";
        }
    }
}
