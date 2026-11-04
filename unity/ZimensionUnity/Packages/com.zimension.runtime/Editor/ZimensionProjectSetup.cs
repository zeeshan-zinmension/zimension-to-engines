using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if ZIMENSION_URP
using UnityEngine.Rendering.Universal;
#endif

namespace Zimension.Editor
{
    /// <summary>
    /// Project configuration the importer relies on, applied idempotently:
    /// - URP: a Universal Render Pipeline asset assigned to Graphics and every Quality level
    ///   (glTFast generates URP materials only when URP is active at import time);
    /// - Active Input Handling "Both", so the runtime's Input Manager reads work alongside the
    ///   Input System package (a restart is required when this changes);
    /// - Linear colour space (glTF and Zimension colours are authored for linear lighting);
    /// - non-clamped blendshape weights (lip sync drives some faces above 100).
    /// A project created from Unity Hub's "Universal 3D" template already has URP configured.
    /// </summary>
    [InitializeOnLoad]
    public static class ZimensionProjectSetup
    {
        const string SettingsFolder = "Assets/Zimension/Settings";

        static ZimensionProjectSetup()
        {
            // Run once the editor is idle (not during the domain reload that loaded this class).
            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode || SessionState.GetBool("Zimension.SetupChecked", false)) return;
                SessionState.SetBool("Zimension.SetupChecked", true);
                EnsureConfigured(interactive: false);
            };
        }

        [MenuItem("Zimension/Setup Project (URP, input, colour space)", priority = 20)]
        public static void SetupMenu()
        {
            var message = EnsureConfigured(interactive: true);
            EditorUtility.DisplayDialog("Zimension", message, "OK");
        }

        /// <summary>Apply the settings; returns a human-readable summary.</summary>
        public static string EnsureConfigured(bool interactive)
        {
            var notes = new System.Text.StringBuilder();
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                notes.AppendLine("Colour space set to Linear.");
            }
            if (EnsureInputHandling()) notes.AppendLine("Active Input Handling set to 'Both' (restart the editor to apply).");
            notes.AppendLine(EnsureUrp());
            PlayerSettings.legacyClampBlendShapeWeights = false;
            return notes.ToString().Trim();
        }

        static bool EnsureInputHandling()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (settings == null) return false;
            var so = new SerializedObject(settings);
            var handler = so.FindProperty("activeInputHandler");
            if (handler == null || handler.intValue != 1) return false; // 0 = Input Manager, 2 = Both
            handler.intValue = 2;
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        static string EnsureUrp()
        {
#if ZIMENSION_URP
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset) return "URP is active.";
            ZimensionImporter.EnsureFolder(SettingsFolder);
            var assetPath = SettingsFolder + "/Zimension URP.asset";
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
            if (!asset)
            {
                var rendererPath = SettingsFolder + "/Zimension URP Renderer.asset";
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
                if (!renderer)
                {
                    renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                    AssetDatabase.CreateAsset(renderer, rendererPath);
                }
                asset = UniversalRenderPipelineAsset.Create(renderer);
                asset.supportsHDR = true;
                asset.msaaSampleCount = 4;
                asset.shadowDistance = 80;
                AssetDatabase.CreateAsset(asset, assetPath);
                AssetDatabase.SaveAssets();
            }
            GraphicsSettings.defaultRenderPipeline = asset;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
            }
            QualitySettings.SetQualityLevel(current, false);
            AssetDatabase.SaveAssets();
            return "URP asset created and assigned (" + assetPath + ").";
#else
            return "URP package is not installed: materials use the Built-in pipeline. Add com.unity.render-pipelines.universal for the supported configuration.";
#endif
        }
    }

    /// <summary>
    /// Command-line import for automation:
    /// <code>Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod Zimension.Editor.BatchImport.Run -zimensionExport &lt;export.zip | folder | link | code&gt; [-zimensionApi &lt;api base&gt;] -logFile import.log</code>
    /// Exits with 0 on success, 1 on failure or blocking findings (see export-report.json). Do not
    /// combine with -quit: the method exits the editor itself once the import has finished.
    /// </summary>
    public static class BatchImport
    {
        public static void Run()
        {
            int exit = 1;
            try
            {
                var args = System.Environment.GetCommandLineArgs();
                string bundle = null;
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-zimensionExport" || args[i] == "-zimensionBundle") bundle = args[i + 1];
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-zimensionApi") ZimensionLinkImport.ApiBase = args[i + 1];
                if (string.IsNullOrEmpty(bundle)) { Debug.LogError("[Zimension] Pass -zimensionExport <export .zip, folder, Zimension link or code>."); return; }
                var options = new ZimensionImporter.Options { OpenBaseScene = false, ShowProgress = false };
                bool local = File.Exists(bundle) || Directory.Exists(bundle);
                var result = local
                    ? ZimensionImporter.Import(Path.GetFullPath(bundle), options)
                    : ZimensionLinkImport.ImportBlocking(bundle, options);
                if (result.Success) exit = 0;
                else Debug.LogError("[Zimension] " + result.Error);
            }
            catch (System.Exception e) { Debug.LogException(e); }
            finally { EditorApplication.Exit(exit); }
        }

        /// <summary>Compile-only smoke check for CI: exits 0 when the editor reaches this point.</summary>
        public static void Verify()
        {
            Debug.Log("[Zimension] Package compiled; editor " + Application.unityVersion);
            EditorApplication.Exit(0);
        }
    }
}
