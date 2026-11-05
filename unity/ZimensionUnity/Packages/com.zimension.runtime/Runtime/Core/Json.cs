using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zimension.Core
{
    /// <summary>
    /// A small JSON document model for Zimension's saved data (masterData.json, items, voice JSON).
    /// Unity's JsonUtility cannot read schemaless documents, and the core must not depend on
    /// UnityEngine, so it carries its own reader. Missing members read as <see cref="Undefined"/>,
    /// which keeps lookups on optional saved fields short and safe.
    /// </summary>
    public sealed class JsonNode
    {
        public enum Kind { Undefined, Null, Bool, Number, String, Array, Object }

        public static readonly JsonNode Undefined = new JsonNode(Kind.Undefined);
        public static readonly JsonNode Null = new JsonNode(Kind.Null);

        public Kind Type { get; }
        readonly bool m_Bool;
        readonly double m_Number;
        readonly string m_String;
        readonly List<JsonNode> m_Array;
        readonly List<KeyValuePair<string, JsonNode>> m_Members; // insertion order, as JavaScript keeps it
        Dictionary<string, JsonNode> m_Index;

        JsonNode(Kind kind) { Type = kind; }
        public JsonNode(bool value) { Type = Kind.Bool; m_Bool = value; }
        public JsonNode(double value) { Type = Kind.Number; m_Number = value; }
        public JsonNode(string value) { Type = value == null ? Kind.Null : Kind.String; m_String = value; }
        public JsonNode(List<JsonNode> items) { Type = Kind.Array; m_Array = items ?? new List<JsonNode>(); }
        public JsonNode(List<KeyValuePair<string, JsonNode>> members) { Type = Kind.Object; m_Members = members ?? new List<KeyValuePair<string, JsonNode>>(); }

        public static JsonNode NewObject() => new JsonNode(new List<KeyValuePair<string, JsonNode>>());
        public static JsonNode NewArray() => new JsonNode(new List<JsonNode>());

        public bool IsUndefined => Type == Kind.Undefined;
        public bool IsNull => Type == Kind.Null;
        public bool IsBool => Type == Kind.Bool;
        public bool IsNumber => Type == Kind.Number;
        public bool IsString => Type == Kind.String;
        public bool IsArray => Type == Kind.Array;
        public bool IsObject => Type == Kind.Object;
        /// <summary>Present and not null.</summary>
        public bool Exists => Type != Kind.Undefined && Type != Kind.Null;

        public bool AsBool => m_Bool;
        public double AsNumber => m_Number;
        public string AsString => m_String;

        public int Count => Type == Kind.Array ? m_Array.Count : Type == Kind.Object ? m_Members.Count : 0;

        public JsonNode this[int index] => Type == Kind.Array && index >= 0 && index < m_Array.Count ? m_Array[index] : Undefined;

        public JsonNode this[string key]
        {
            get
            {
                if (Type != Kind.Object || key == null) return Undefined;
                if (m_Index == null)
                {
                    m_Index = new Dictionary<string, JsonNode>(m_Members.Count, StringComparer.Ordinal);
                    // JSON.parse keeps the last duplicate; so does this index.
                    foreach (var pair in m_Members) m_Index[pair.Key] = pair.Value;
                }
                return m_Index.TryGetValue(key, out var value) ? value : Undefined;
            }
        }

        public bool Has(string key) => !this[key].IsUndefined;

        public IEnumerable<JsonNode> Items
        {
            get
            {
                if (Type != Kind.Array) yield break;
                foreach (var item in m_Array) yield return item;
            }
        }

        public IEnumerable<KeyValuePair<string, JsonNode>> Members
        {
            get
            {
                if (Type != Kind.Object) yield break;
                foreach (var pair in m_Members) yield return pair;
            }
        }

        public void Set(string key, JsonNode value)
        {
            if (Type != Kind.Object) throw new InvalidOperationException("Set on a non-object JSON node");
            value = value ?? Null;
            for (int i = 0; i < m_Members.Count; i++)
            {
                if (m_Members[i].Key == key) { m_Members[i] = new KeyValuePair<string, JsonNode>(key, value); m_Index = null; return; }
            }
            m_Members.Add(new KeyValuePair<string, JsonNode>(key, value));
            m_Index = null;
        }

        public void Add(JsonNode value)
        {
            if (Type != Kind.Array) throw new InvalidOperationException("Add on a non-array JSON node");
            m_Array.Add(value ?? Null);
        }

        // ---- Typed reads with JavaScript-like fallbacks -------------------------------------

        /// <summary>The member as a string, or the fallback when it is missing or not a string.</summary>
        public string Str(string key, string fallback = "")
        {
            var v = this[key];
            return v.IsString ? v.AsString : fallback;
        }

        /// <summary>A number, also accepting numeric strings (saved fields are often strings).</summary>
        public double Num(string key, double fallback)
        {
            var v = this[key];
            if (v.IsNumber) return IsFinite(v.AsNumber) ? v.AsNumber : fallback;
            if (v.IsString && TryParseNumber(v.AsString.Trim(), out var parsed) && IsFinite(parsed)) return parsed;
            return fallback;
        }

        public float Float(string key, float fallback) => (float)Num(key, fallback);

        /// <summary>A boolean when present as a boolean, otherwise the fallback.</summary>
        public bool Flag(string key, bool fallback)
        {
            var v = this[key];
            return v.IsBool ? v.AsBool : fallback;
        }

        /// <summary>True only when the member is the boolean true.</summary>
        public bool Truthy(string key) => Flag(key, false);

        public JsonNode Obj(string key)
        {
            var v = this[key];
            return v.IsObject ? v : Undefined;
        }

        public List<string> Strings(string key)
        {
            var list = new List<string>();
            foreach (var item in this[key].Items) if (item.IsString) list.Add(item.AsString);
            return list;
        }

        public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

        public static bool TryParseNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // ---- Parsing ----------------------------------------------------------------------

        public static JsonNode Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var reader = new Reader(text);
            reader.SkipWhitespace();
            var value = reader.ReadValue(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd) throw reader.Error("Unexpected trailing characters");
            return value;
        }

        public static bool TryParse(string text, out JsonNode node)
        {
            try { node = Parse(text); return true; }
            catch (FormatException) { node = Undefined; return false; }
        }

        struct Reader
        {
            readonly string m_Text;
            int m_Pos;
            public Reader(string text) { m_Text = text; m_Pos = 0; if (text.Length > 0 && text[0] == '﻿') m_Pos = 1; }
            public bool AtEnd => m_Pos >= m_Text.Length;

            public FormatException Error(string message) => new FormatException($"JSON: {message} at character {m_Pos}");

            public void SkipWhitespace()
            {
                while (m_Pos < m_Text.Length)
                {
                    char c = m_Text[m_Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') m_Pos++;
                    else break;
                }
            }

            public JsonNode ReadValue(int depth)
            {
                if (depth > 512) throw Error("Nesting is too deep");
                if (AtEnd) throw Error("Unexpected end");
                char c = m_Text[m_Pos];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return new JsonNode(ReadString());
                    case 't': Expect("true"); return new JsonNode(true);
                    case 'f': Expect("false"); return new JsonNode(false);
                    case 'n': Expect("null"); return Null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return new JsonNode(ReadNumber());
                        throw Error($"Unexpected character '{c}'");
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(m_Text, m_Pos, word, 0, word.Length) != 0) throw Error($"Expected {word}");
                m_Pos += word.Length;
            }

            JsonNode ReadObject(int depth)
            {
                m_Pos++; // {
                var members = new List<KeyValuePair<string, JsonNode>>();
                SkipWhitespace();
                if (!AtEnd && m_Text[m_Pos] == '}') { m_Pos++; return new JsonNode(members); }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || m_Text[m_Pos] != '"') throw Error("Expected a member name");
                    var key = ReadString();
                    SkipWhitespace();
                    if (AtEnd || m_Text[m_Pos] != ':') throw Error("Expected ':'");
                    m_Pos++;
                    SkipWhitespace();
                    members.Add(new KeyValuePair<string, JsonNode>(key, ReadValue(depth + 1)));
                    SkipWhitespace();
                    if (AtEnd) throw Error("Unterminated object");
                    char c = m_Text[m_Pos++];
                    if (c == '}') return new JsonNode(members);
                    if (c != ',') throw Error("Expected ',' or '}'");
                }
            }

            JsonNode ReadArray(int depth)
            {
                m_Pos++; // [
                var items = new List<JsonNode>();
                SkipWhitespace();
                if (!AtEnd && m_Text[m_Pos] == ']') { m_Pos++; return new JsonNode(items); }
                while (true)
                {
                    SkipWhitespace();
                    items.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd) throw Error("Unterminated array");
                    char c = m_Text[m_Pos++];
                    if (c == ']') return new JsonNode(items);
                    if (c != ',') throw Error("Expected ',' or ']'");
                }
            }

            string ReadString()
            {
                m_Pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("Unterminated string");
                    char c = m_Text[m_Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw Error("Unterminated escape");
                    char e = m_Text[m_Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (m_Pos + 4 > m_Text.Length) throw Error("Bad unicode escape");
                            if (!int.TryParse(m_Text.Substring(m_Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw Error("Bad unicode escape");
                            sb.Append((char)code);
                            m_Pos += 4;
                            break;
                        default: throw Error($"Bad escape '\\{e}'");
                    }
                }
            }

            double ReadNumber()
            {
                int start = m_Pos;
                if (m_Text[m_Pos] == '-') m_Pos++;
                while (!AtEnd && char.IsDigit(m_Text[m_Pos])) m_Pos++;
                if (!AtEnd && m_Text[m_Pos] == '.') { m_Pos++; while (!AtEnd && char.IsDigit(m_Text[m_Pos])) m_Pos++; }
                if (!AtEnd && (m_Text[m_Pos] == 'e' || m_Text[m_Pos] == 'E'))
                {
                    m_Pos++;
                    if (!AtEnd && (m_Text[m_Pos] == '+' || m_Text[m_Pos] == '-')) m_Pos++;
                    while (!AtEnd && char.IsDigit(m_Text[m_Pos])) m_Pos++;
                }
                var slice = m_Text.Substring(start, m_Pos - start);
                if (!TryParseNumber(slice, out var value)) throw Error($"Bad number '{slice}'");
                return value;
            }
        }

        // ---- Writing ----------------------------------------------------------------------

        public override string ToString() => Dump();

        public string Dump(bool pretty = false)
        {
            var sb = new StringBuilder();
            Write(sb, pretty, 0);
            return sb.ToString();
        }

        void Write(StringBuilder sb, bool pretty, int indent)
        {
            switch (Type)
            {
                case Kind.Undefined:
                case Kind.Null: sb.Append("null"); break;
                case Kind.Bool: sb.Append(m_Bool ? "true" : "false"); break;
                case Kind.Number: sb.Append(IsFinite(m_Number) ? m_Number.ToString("R", CultureInfo.InvariantCulture) : "null"); break;
                case Kind.String: WriteString(sb, m_String); break;
                case Kind.Array:
                    sb.Append('[');
                    for (int i = 0; i < m_Array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (pretty) { sb.Append('\n'); sb.Append(' ', (indent + 1) * 2); }
                        m_Array[i].Write(sb, pretty, indent + 1);
                    }
                    if (pretty && m_Array.Count > 0) { sb.Append('\n'); sb.Append(' ', indent * 2); }
                    sb.Append(']');
                    break;
                case Kind.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (var pair in m_Members)
                    {
                        if (pair.Value.IsUndefined) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        if (pretty) { sb.Append('\n'); sb.Append(' ', (indent + 1) * 2); }
                        WriteString(sb, pair.Key);
                        sb.Append(pretty ? ": " : ":");
                        pair.Value.Write(sb, pretty, indent + 1);
                    }
                    if (pretty && !first) { sb.Append('\n'); sb.Append(' ', indent * 2); }
                    sb.Append('}');
                    break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
