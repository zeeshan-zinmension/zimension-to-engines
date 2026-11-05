using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zimension.Core;
using Zimension.Kit;
using Object = UnityEngine.Object;

namespace Zimension.Editor.Conversion
{
    /// <summary>
    /// The world logic script's life cycle: generated during import into
    /// Assets/Zimension/Scripts/&lt;World&gt;/ (the .Generated.cs half is rewritten on every import,
    /// the other half is created once and never touched), then — once Unity has compiled it, which
    /// can be after a domain reload — added to each scene's Game object with its scene references
    /// assigned. Pending work survives reloads in Library/ZimensionPendingWiring.json.
    /// </summary>
    [InitializeOnLoad]
    internal static class LogicWiring
    {
        public const string ScriptsRoot = ZimensionImporter.Root + "/Scripts";
        const string Namespace = "ZimensionWorlds";
        static readonly string PendingFile = Path.GetFullPath("Library/ZimensionPendingWiring.json");

        [Serializable]
        sealed class Pending
        {
            public string scene;
            public string type;
            public string hash;
            public List<FieldBinding> fields = new List<FieldBinding>();
        }

        [Serializable]
        sealed class PendingList { public List<Pending> items = new List<Pending>(); }

        static LogicWiring() => EditorApplication.delayCall += TryApply;

        // ---- Generation --------------------------------------------------------------------------

        public sealed class Generated
        {
            public LogicOutput Output;
            public string ClassName, FullName, Hash;
            public bool ScriptChanged;
        }

        /// <summary>Generate the world's logic and write the script files (only when their content changed).</summary>
        public static Generated Generate(ZimensionImporter.Context ctx, List<KeyValuePair<string, string>> sceneSwitches)
        {
            var world = EventParsing.ParseWorld(ctx.World);
            var owners = new HashSet<string>(world.Flows.Select(f => f.Owner).Where(o => o.Length > 0), StringComparer.Ordinal);
            foreach (var j in ctx.Items)
            {
                var item = new EventItem();
                bool behaves = EventParsing.ParseItem(item, j, owners);
                // Characters with behaviours and items with health are driven by the logic too.
                if (behaves || j.Obj("characterBehavior").Truthy("enable") || j.Obj("damageSetting").Truthy("enable")) world.Items.Add(item);
            }
            var className = LogicGenerator.Pascal(ctx.WorldKey) + "Logic";
            var output = LogicGenerator.Generate(new LogicInput
            {
                World = world, WorldName = ctx.World.Str("name", ctx.WorldKey), ClassName = className, Namespace = Namespace,
                SceneSwitches = sceneSwitches, GeneratorVersion = ZimensionImporter.Version,
            });
            string hash;
            using (var sha = SHA1.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(output.Code))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            var code = output.Code.Replace("__HASH__", hash);
            var folder = $"{ScriptsRoot}/{ctx.WorldKey}";
            Directory.CreateDirectory(Path.GetFullPath(folder));
            var generatedPath = Path.GetFullPath($"{folder}/{className}.Generated.cs");
            var userPath = Path.GetFullPath($"{folder}/{className}.cs");
            bool changed = !File.Exists(generatedPath) || File.ReadAllText(generatedPath) != code;
            if (changed) File.WriteAllText(generatedPath, code);
            if (!File.Exists(userPath)) { File.WriteAllText(userPath, output.UserCode); changed = true; }
            foreach (var note in output.Notes) ctx.Report.Add(ImportReport.Level.Approximated, note, "eventBroadcasters");
            return new Generated { Output = output, ClassName = className, FullName = $"{Namespace}.{className}", Hash = hash, ScriptChanged = changed };
        }

        /// <summary>Remember a saved scene that needs the logic component once the script is compiled.</summary>
        public static void Queue(string scenePath, Generated generated, List<FieldBinding> bindings)
        {
            var list = Load();
            list.items.RemoveAll(p => p.scene == scenePath);
            list.items.Add(new Pending { scene = scenePath, type = generated.FullName, hash = generated.Hash, fields = bindings });
            Save(list);
        }

        static PendingList Load()
        {
            try { return File.Exists(PendingFile) ? JsonUtility.FromJson<PendingList>(File.ReadAllText(PendingFile)) ?? new PendingList() : new PendingList(); }
            catch { return new PendingList(); }
        }

        static void Save(PendingList list)
        {
            if (list.items.Count == 0) { if (File.Exists(PendingFile)) File.Delete(PendingFile); return; }
            File.WriteAllText(PendingFile, JsonUtility.ToJson(list, true));
        }

        // ---- Wiring ------------------------------------------------------------------------------

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = assembly.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        static int s_Attempts;

        /// <summary>Connect every pending scene whose script is compiled (called after each script reload and after an import).</summary>
        public static void TryApply()
        {
            var list = Load();
            if (list.items.Count == 0) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += TryApply;
                return;
            }
            var openScene = EditorSceneManager.GetActiveScene().path;
            var done = new List<Pending>();
            bool waiting = false;
            foreach (var pending in list.items)
            {
                var type = FindType(pending.type);
                var hash = type?.GetField("GeneratedHash")?.GetRawConstantValue() as string;
                if (type == null || hash != pending.hash)
                {
                    waiting = true;
                    continue;
                }
                if (!File.Exists(pending.scene)) { done.Add(pending); continue; }
                try { Apply(pending, type); }
                catch (Exception e) { Debug.LogException(e); }
                done.Add(pending);
            }
            list.items.RemoveAll(done.Contains);
            Save(list);
            if (done.Count > 0 && !string.IsNullOrEmpty(openScene) && File.Exists(openScene) && EditorSceneManager.GetActiveScene().path != openScene)
                EditorSceneManager.OpenScene(openScene);
            if (waiting)
            {
                // The script has not compiled yet (or failed to): look again shortly, then report.
                if (++s_Attempts < 600) EditorApplication.delayCall += TryApply;
                else Debug.LogError("[Zimension] The world logic script did not compile, so the scenes are not connected yet. Fix the errors in the Console; the scenes connect automatically after the next successful compile.");
            }
            else s_Attempts = 0;
        }

        static void Apply(Pending pending, Type type)
        {
            var scene = EditorSceneManager.GetActiveScene().path == pending.scene ? EditorSceneManager.GetActiveScene() : EditorSceneManager.OpenScene(pending.scene, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var manager = roots.SelectMany(r => r.GetComponentsInChildren<GameManager>(true)).FirstOrDefault();
            if (!manager) { Debug.LogWarning($"[Zimension] {pending.scene} has no Game object; the world logic was not added."); return; }
            var items = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var id in roots.SelectMany(r => r.GetComponentsInChildren<WorldItemId>(true)))
                if (!string.IsNullOrEmpty(id.itemId) && !items.ContainsKey(id.itemId)) items[id.itemId] = id.gameObject;
            var worldSounds = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).FirstOrDefault(t => t.name == "World Sounds")?.gameObject;
            var logic = manager.GetComponent(type) as ZimensionLogic;
            if (!logic) logic = (ZimensionLogic)manager.gameObject.AddComponent(type);
            var so = new SerializedObject(logic);
            int assigned = 0, missing = 0;
            foreach (var binding in pending.fields)
            {
                var property = so.FindProperty(binding.field);
                if (property == null) continue;
                var target = NativeConverter.Resolve(binding, items, worldSounds);
                property.objectReferenceValue = target;
                if (target) assigned++; else if (items.ContainsKey(binding.itemId ?? "") || binding.itemId == "@worldsounds") missing++;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            manager.logic = logic;
            EditorUtility.SetDirty(manager);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[Zimension] Connected {type.Name} in {Path.GetFileNameWithoutExtension(pending.scene)}: {assigned} scene references" + (missing > 0 ? $", {missing} not found" : "") + ".");
        }
    }
}
