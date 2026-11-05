using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Zimension.Core;
using Zimension.Unity;

namespace Zimension.Editor
{
    /// <summary>
    /// Draws [ZimensionJson] fields collapsed: "Saved Zimension data · 12.3 KB". Expanded, a read-only
    /// formatted preview (the first 20,000 characters for very large documents) with Copy and Edit
    /// buttons; Edit opens the text for changes, for the rare case someone needs it.
    /// </summary>
    [CustomPropertyDrawer(typeof(ZimensionJsonAttribute))]
    internal sealed class ZimensionJsonDrawer : PropertyDrawer
    {
        const int PreviewLimit = 20000;
        const float PreviewHeight = 220;
        static readonly Dictionary<string, (string source, string pretty)> s_Pretty = new Dictionary<string, (string, string)>();
        static readonly HashSet<string> s_Editing = new HashSet<string>();
        static readonly Dictionary<string, Vector2> s_Scroll = new Dictionary<string, Vector2>();
        static GUIStyle s_Code;

        static string Key(SerializedProperty p) => p.serializedObject.targetObject.GetInstanceID() + ":" + p.propertyPath;

        static string Pretty(string key, string json)
        {
            if (s_Pretty.TryGetValue(key, out var cached) && ReferenceEquals(cached.source, json)) return cached.pretty;
            string pretty;
            if (string.IsNullOrEmpty(json)) pretty = "";
            else if (JsonNode.TryParse(json, out var node)) pretty = node.Dump(pretty: true);
            else pretty = json;
            if (pretty.Length > PreviewLimit) pretty = pretty.Substring(0, PreviewLimit) + $"\n… ({pretty.Length - PreviewLimit:N0} more characters; use Copy for all of it)";
            s_Pretty[key] = (json, pretty);
            return pretty;
        }

        static string Size(string json)
        {
            int n = json?.Length ?? 0;
            return n < 1024 ? $"{n} B" : n < 1024 * 1024 ? $"{n / 1024f:0.#} KB" : $"{n / (1024f * 1024f):0.#} MB";
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            if (property.propertyType != SerializedPropertyType.String || !property.isExpanded) return line;
            return line * 2 + PreviewHeight + 4;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String) { EditorGUI.PropertyField(position, property, label); return; }
            var attribute = (ZimensionJsonAttribute)this.attribute;
            var title = string.IsNullOrEmpty(attribute.Label) ? label.text : attribute.Label;
            float line = EditorGUIUtility.singleLineHeight;
            var header = new Rect(position.x, position.y, position.width, line);
            var json = property.stringValue ?? "";
            property.isExpanded = EditorGUI.Foldout(header, property.isExpanded, new GUIContent($"{title}  ·  {Size(json)}", "The data this was imported from. Read-only preview; the imported components are what you edit."), true);
            if (!property.isExpanded) return;
            var key = Key(property);
            var buttons = new Rect(position.x, header.yMax + EditorGUIUtility.standardVerticalSpacing, position.width, line);
            float w = 70;
            if (GUI.Button(new Rect(buttons.xMax - w * 2 - 4, buttons.y, w, line), "Copy")) EditorGUIUtility.systemCopyBuffer = json;
            bool editing = s_Editing.Contains(key);
            if (GUI.Button(new Rect(buttons.xMax - w, buttons.y, w, line), editing ? "Done" : "Edit"))
            {
                if (editing) s_Editing.Remove(key); else s_Editing.Add(key);
                editing = !editing;
            }
            var box = new Rect(position.x, buttons.yMax + 4, position.width, PreviewHeight);
            if (s_Code == null) s_Code = new GUIStyle(EditorStyles.textArea) { wordWrap = false, font = EditorStyles.miniFont, fontSize = 11, richText = false };
            if (editing)
            {
                EditorGUI.BeginChangeCheck();
                s_Scroll.TryGetValue(key, out var scrollEdit);
                var size = s_Code.CalcSize(new GUIContent(json.Length > 200000 ? json.Substring(0, 200000) : json));
                var view = new Rect(0, 0, Mathf.Max(box.width - 16, size.x + 8), Mathf.Max(box.height - 16, size.y + 8));
                scrollEdit = GUI.BeginScrollView(box, scrollEdit, view);
                var edited = EditorGUI.TextArea(view, json, s_Code);
                GUI.EndScrollView();
                s_Scroll[key] = scrollEdit;
                if (EditorGUI.EndChangeCheck()) property.stringValue = edited;
                return;
            }
            var pretty = Pretty(key, json);
            s_Scroll.TryGetValue(key, out var scroll);
            var content = new GUIContent(pretty);
            var textSize = s_Code.CalcSize(content);
            var inner = new Rect(0, 0, Mathf.Max(box.width - 16, textSize.x + 8), Mathf.Max(box.height - 16, textSize.y + 8));
            GUI.Box(box, GUIContent.none, EditorStyles.helpBox);
            scroll = GUI.BeginScrollView(box, scroll, inner);
            EditorGUI.SelectableLabel(inner, pretty, s_Code);
            GUI.EndScrollView();
            s_Scroll[key] = scroll;
        }
    }
}
