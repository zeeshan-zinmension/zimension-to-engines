using System.IO;
using UnityEditor;
using UnityEngine;

namespace Zimension.Editor
{
    /// <summary>Zimension > Import Zimension Export...: choose the Web export and import it.</summary>
    public sealed class ZimensionImportWindow : EditorWindow
    {
        string m_Path = "";
        bool m_OpenScene = true, m_BuildSettings = true;
        string m_Summary = "";
        Vector2 m_Scroll;

        public static void Open()
        {
            var window = GetWindow<ZimensionImportWindow>(true, "Import Zimension Export", true);
            window.minSize = new Vector2(560, 360);
            window.m_Path = EditorPrefs.GetString("Zimension.LastExport", "");
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "1. In Zimension, open the project and use Export > Web. It downloads <name>.zip containing masterData.json and every asset.\n" +
                "2. Choose that .zip (or its extracted folder) here. The project is imported into Assets/Zimension and one Unity scene is generated per Zimension scene.\n" +
                "Re-importing replaces only Assets/Zimension/Generated; put your own work in Assets/Zimension/UserContent.",
                MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            m_Path = EditorGUILayout.TextField("Export", m_Path);
            if (GUILayout.Button("Zip...", GUILayout.Width(60)))
            {
                var picked = EditorUtility.OpenFilePanel("Zimension Web export", Directory.Exists(Path.GetDirectoryName(m_Path) ?? "") ? Path.GetDirectoryName(m_Path) : "", "zip");
                if (!string.IsNullOrEmpty(picked)) m_Path = picked;
            }
            if (GUILayout.Button("Folder...", GUILayout.Width(70)))
            {
                var picked = EditorUtility.OpenFolderPanel("Extracted Zimension export", m_Path, "");
                if (!string.IsNullOrEmpty(picked)) m_Path = picked;
            }
            EditorGUILayout.EndHorizontal();
            m_OpenScene = EditorGUILayout.Toggle("Open base scene", m_OpenScene);
            m_BuildSettings = EditorGUILayout.Toggle("Update Build Settings", m_BuildSettings);
            GUI.enabled = !string.IsNullOrEmpty(m_Path);
            if (GUILayout.Button("Import", GUILayout.Height(32)))
            {
                EditorPrefs.SetString("Zimension.LastExport", m_Path);
                var result = ZimensionImporter.Import(m_Path, new ZimensionImporter.Options { OpenBaseScene = m_OpenScene, UpdateBuildSettings = m_BuildSettings });
                m_Summary = result.Report != null ? result.Report.Summary() : "";
                if (!result.Success) m_Summary = "Import finished with problems: " + result.Error + "\n\n" + m_Summary;
                else m_Summary = "Import complete. Press Play.\n\n" + m_Summary;
            }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(m_Summary))
            {
                m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
                EditorGUILayout.TextArea(m_Summary, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }
    }
}
