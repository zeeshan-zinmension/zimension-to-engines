using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Zimension.Unity;

namespace Zimension.Editor
{
    /// <summary>
    /// Zimension > Import from Zimension...
    /// - From a link: paste the link (or its code) from Zimension's Export > Unity; the export is
    ///   downloaded (resumable, verified) and imported. Imported worlds can Pull latest.
    /// - From a file: an export .zip or extracted folder on disk.
    /// </summary>
    public sealed class ZimensionImportWindow : EditorWindow
    {
        int m_Tab;
        string m_Link = "", m_Path = "";
        bool m_OpenScene = true, m_BuildSettings = true, m_ShowAdvanced;
        string m_Status = "", m_Summary = "";
        MessageType m_StatusType = MessageType.None;
        Vector2 m_Scroll;
        ZimensionLinkImport.Job m_Job;
        string m_PullWorldId;

        [MenuItem("Zimension/Import from Zimension...", priority = 0)]
        public static void Open()
        {
            var window = GetWindow<ZimensionImportWindow>(false, "Zimension Import", true);
            window.minSize = new Vector2(560, 420);
            window.m_Path = EditorPrefs.GetString("Zimension.LastExport", "");
        }

        [MenuItem("Zimension/Pull Latest for All Linked Worlds", priority = 2)]
        static void PullAllMenu()
        {
            var window = GetWindow<ZimensionImportWindow>(false, "Zimension Import", true);
            var first = ZimensionLinkImport.All().FirstOrDefault();
            if (first == null) { EditorUtility.DisplayDialog("Zimension", "No world has been imported from a link on this machine yet.", "OK"); return; }
            window.StartPull(first);
        }

        void OnEnable() => EditorApplication.update += Poll;
        void OnDisable()
        {
            EditorApplication.update -= Poll;
            // Closing the window keeps the partial file: the next attempt resumes it.
            m_Job?.Cancel.Cancel();
        }

        void Poll()
        {
            if (m_Job == null) return;
            if (!m_Job.Done) { Repaint(); return; }
            var job = m_Job;
            m_Job = null;
            var error = ZimensionLinkImport.ErrorOf(job);
            if (error != null)
            {
                SetStatus(error, error == "Cancelled." ? MessageType.Warning : MessageType.Error);
                Repaint();
                return;
            }
            var download = job.Download.Result;
            if (m_PullWorldId != null && download.FromCache)
            {
                var project = FindProject(m_PullWorldId);
                if (project && project.sourceSha256 == download.Sha256)
                {
                    SetStatus($"'{download.WorldName}' is already up to date (version {download.Version}).", MessageType.Info);
                    m_PullWorldId = null;
                    Repaint();
                    return;
                }
            }
            m_PullWorldId = null;
            SetStatus("Importing...", MessageType.Info);
            Repaint();
            var result = ZimensionLinkImport.Finish(job, new ZimensionImporter.Options { OpenBaseScene = m_OpenScene, UpdateBuildSettings = m_BuildSettings });
            Report(result, download.WorldName);
        }

        void Report(ZimensionImporter.Result result, string name)
        {
            m_Summary = result.Report != null ? result.Report.Summary() : "";
            if (result.Success) SetStatus($"Imported{(string.IsNullOrEmpty(name) ? "" : " '" + name + "'")}. Press Play.", MessageType.Info);
            else SetStatus("Import finished with problems: " + result.Error, MessageType.Warning);
            Repaint();
        }

        void SetStatus(string text, MessageType type) { m_Status = text; m_StatusType = type; }

        static ZimensionProject FindProject(string worldId) =>
            AssetDatabase.FindAssets("t:ZimensionProject")
                .Select(g => AssetDatabase.LoadAssetAtPath<ZimensionProject>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(p => p && p.worldId == worldId);

        void StartLink(string input)
        {
            if (m_Job != null) return;
            try
            {
                m_Job = ZimensionLinkImport.Start(input);
                m_Summary = "";
                SetStatus("", MessageType.None);
            }
            catch (Exception e) { SetStatus(e.Message, MessageType.Error); }
        }

        void StartPull(ZimensionLinkImport.Remembered remembered)
        {
            m_Tab = 0;
            m_PullWorldId = remembered.WorldId;
            StartLink(remembered.Link);
        }

        void OnGUI()
        {
            m_Tab = GUILayout.Toolbar(m_Tab, new[] { "From a Zimension link", "From a file" });
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(m_Job != null))
            {
                if (m_Tab == 0) LinkTab(); else FileTab();
                m_OpenScene = EditorGUILayout.Toggle("Open base scene", m_OpenScene);
                m_BuildSettings = EditorGUILayout.Toggle("Update Build Settings", m_BuildSettings);
            }
            if (m_Job != null) ProgressBlock();
            if (!string.IsNullOrEmpty(m_Status)) EditorGUILayout.HelpBox(m_Status, m_StatusType);
            if (m_Tab == 0) LinkedWorlds();
            if (!string.IsNullOrEmpty(m_Summary))
            {
                EditorGUILayout.LabelField("Import report", EditorStyles.boldLabel);
                m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.MinHeight(120));
                EditorGUILayout.TextArea(m_Summary, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        void LinkTab()
        {
            EditorGUILayout.HelpBox(
                "In Zimension, open the project and choose Export > Unity. Copy the link it shows and paste it here " +
                "(the short code works too). The export downloads into Library/ZimensionDownloads, resumes if interrupted, " +
                "is verified, then imported. Re-importing replaces only Assets/Zimension/Generated.",
                MessageType.Info);
            m_Link = EditorGUILayout.TextField("Link or code", m_Link);
            if (!string.IsNullOrWhiteSpace(m_Link))
            {
                string kind;
                try { kind = ExportDownloader.Describe(m_Link); } catch (Exception e) { kind = e.Message; }
                EditorGUILayout.LabelField(" ", kind, EditorStyles.miniLabel);
            }
            m_ShowAdvanced = EditorGUILayout.Foldout(m_ShowAdvanced, "Advanced");
            if (m_ShowAdvanced)
            {
                EditorGUI.indentLevel++;
                ZimensionLinkImport.ApiBase = EditorGUILayout.DelayedTextField(new GUIContent("API base", "Used for bare codes. Full links carry their own server."), ZimensionLinkImport.ApiBase);
                if (GUILayout.Button("Reset to Zimension server", GUILayout.Width(200))) ZimensionLinkImport.ApiBase = ExportDownloader.DefaultApiBase;
                if (GUILayout.Button("Clear download cache", GUILayout.Width(200))) ClearCache();
                EditorGUI.indentLevel--;
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Link)))
                if (GUILayout.Button("Download and import", GUILayout.Height(32))) StartLink(m_Link);
        }

        void FileTab()
        {
            EditorGUILayout.HelpBox("Choose a Zimension export .zip (Export > Unity or Export > Web) or its extracted folder.", MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            m_Path = EditorGUILayout.TextField("Export", m_Path);
            if (GUILayout.Button("Zip...", GUILayout.Width(60)))
            {
                var picked = EditorUtility.OpenFilePanel("Zimension export", SafeDirectory(m_Path), "zip");
                if (!string.IsNullOrEmpty(picked)) m_Path = picked;
            }
            if (GUILayout.Button("Folder...", GUILayout.Width(70)))
            {
                var picked = EditorUtility.OpenFolderPanel("Extracted Zimension export", SafeDirectory(m_Path), "");
                if (!string.IsNullOrEmpty(picked)) m_Path = picked;
            }
            EditorGUILayout.EndHorizontal();
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Path)))
            {
                if (GUILayout.Button("Import", GUILayout.Height(32)))
                {
                    EditorPrefs.SetString("Zimension.LastExport", m_Path);
                    var result = ZimensionImporter.Import(m_Path, new ZimensionImporter.Options { OpenBaseScene = m_OpenScene, UpdateBuildSettings = m_BuildSettings });
                    Report(result, null);
                }
            }
        }

        static string SafeDirectory(string path)
        {
            try { return string.IsNullOrEmpty(path) ? "" : Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? ""; }
            catch (ArgumentException) { return ""; }
        }

        void ProgressBlock()
        {
            var p = m_Job.Progress;
            var rect = EditorGUILayout.GetControlRect(false, 22);
            string label = p.Stage ?? "";
            if (p.Total > 0) label += $"  {Mb(p.Received)} / {Mb(p.Total)}";
            else if (p.Received > 0) label += $"  {Mb(p.Received)}";
            if (p.BytesPerSecond > 0) label += $"  ({Mb((long)p.BytesPerSecond)}/s)";
            EditorGUI.ProgressBar(rect, p.Fraction >= 0 ? p.Fraction : 0, label);
            if (GUILayout.Button("Cancel", GUILayout.Width(100))) m_Job.Cancel.Cancel();
        }

        static string Mb(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" : $"{bytes / 1024.0:0} KB";

        void LinkedWorlds()
        {
            var linked = ZimensionLinkImport.All();
            if (linked.Count == 0) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Imported from links (this machine)", EditorStyles.boldLabel);
            foreach (var r in linked)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{(string.IsNullOrEmpty(r.WorldName) ? r.WorldId : r.WorldName)}", $"version {r.Version}");
                using (new EditorGUI.DisabledScope(m_Job != null))
                {
                    if (GUILayout.Button("Pull latest", GUILayout.Width(90))) StartPull(r);
                    if (GUILayout.Button("Forget", GUILayout.Width(60))) ZimensionLinkImport.Forget(r.WorldId);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        void ClearCache()
        {
            var root = ZimensionLinkImport.CacheRoot;
            if (!Directory.Exists(root)) return;
            long freed = 0;
            foreach (var dir in Directory.GetDirectories(root))
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) freed += new FileInfo(f).Length;
                Directory.Delete(dir, true);
            }
            SetStatus($"Freed {Mb(freed)} of downloads (remembered links kept).", MessageType.Info);
        }
    }
}
