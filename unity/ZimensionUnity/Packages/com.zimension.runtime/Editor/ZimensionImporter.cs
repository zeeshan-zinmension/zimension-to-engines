using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Zimension.Core;
using Zimension.Unity;

namespace Zimension.Editor
{
    /// <summary>
    /// Imports a Zimension export into this Unity project as editable assets and scenes.
    ///
    /// 1. Reads masterData.json from the export (zip or folder) - the saved world and items exactly
    ///    as the Zimension editor packaged them.
    /// 2. Stages every referenced file (models, companion buffers/images, sounds, Rhubarb voice
    ///    JSON, UI images, animation libraries) under Assets/Zimension/SourceAssets/&lt;world&gt;,
    ///    rewriting nothing and copying a file only when its content changed (stable GUIDs).
    /// 3. Lets Unity import them (glTFast for glTF/GLB with Mecanim clips and original node names).
    /// 4. Generates one .unity scene per Zimension scene under Assets/Zimension/Generated/&lt;world&gt;,
    ///    a <see cref="ZimensionProject"/> asset, Build Settings and export-report.json.
    ///
    /// Re-importing regenerates only Generated/ content; SourceAssets keep their GUIDs and
    /// Assets/Zimension/UserContent is never touched.
    /// </summary>
    public static class ZimensionImporter
    {
        public const string Version = ZimensionImporterVersion.Value;
        public const string Root = "Assets/Zimension";
        public const string SourceRoot = Root + "/SourceAssets";
        public const string GeneratedRoot = Root + "/Generated";
        public const string UserContent = Root + "/UserContent";

        public sealed class Options
        {
            public bool OpenBaseScene = true;
            public bool UpdateBuildSettings = true;
            public bool ShowProgress = true;
        }

        public sealed class Result
        {
            public bool Success;
            public string Error;
            public ImportReport Report;
            public ZimensionProject Project;
            public List<string> ScenePaths = new List<string>();
        }

        /// <summary>Everything the scene builder needs from staging.</summary>
        internal sealed class Context
        {
            public BundleSource Bundle;
            public AssetResolver Resolver;
            public ImportReport Report;
            public JsonNode Master, World;
            public List<JsonNode> Items = new List<JsonNode>();
            public string WorldKey, SourceFolder, GeneratedFolder;
            public readonly Dictionary<string, string> Staged = new Dictionary<string, string>(StringComparer.Ordinal);   // bundle entry -> asset path
            public readonly Dictionary<string, List<AnimationClip>> ModelClips = new Dictionary<string, List<AnimationClip>>(StringComparer.Ordinal); // asset path -> clips in glTF order
            public readonly Dictionary<string, AnimationClip> LibraryByName = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            /// <summary>Every clip of every imported model and library (several libraries share clip names such as "Idle").</summary>
            public readonly List<AnimationClip> LibraryClips = new List<AnimationClip>();
            public readonly Dictionary<string, List<AnimationClip>> SourceClips = new Dictionary<string, List<AnimationClip>>(StringComparer.Ordinal); // bundle entry -> clips by index
            public readonly Dictionary<string, float> SourceScale = new Dictionary<string, float>(StringComparer.Ordinal); // asset path -> size correction
            public readonly Dictionary<string, string> Retargeted = new Dictionary<string, string>(StringComparer.Ordinal);
            public ZimensionProject Project;
            public bool ShowProgress;

            public string AssetFor(string entry) => entry != null && Staged.TryGetValue(entry, out var path) ? path : null;
        }


        public static Result Import(string bundlePath, Options options = null)
        {
            options = options ?? new Options();
            var result = new Result();
            var ctx = new Context { ShowProgress = options.ShowProgress && !Application.isBatchMode };
            try
            {
                Progress(ctx, "Reading export", 0.02f);
                using (ctx.Bundle = BundleSource.Open(bundlePath))
                {
                    ctx.Resolver = new AssetResolver(ctx.Bundle);
                    ctx.Master = JsonNode.Parse(ctx.Bundle.MasterDataText);
                    ctx.World = ctx.Master.Obj("world");
                    if (!ctx.World.IsObject) throw new InvalidDataException("masterData.json has no world object.");
                    foreach (var item in ctx.Master["worldItems"].Items) if (item.IsObject) ctx.Items.Add(item);
                    ctx.Report = new ImportReport
                    {
                        Bundle = bundlePath, WorldId = ctx.World.Str("_id"), WorldName = ctx.World.Str("name", "Zimension World"), Items = ctx.Items.Count,
                    };
                    ctx.WorldKey = SafeName(ctx.Report.WorldName, ctx.Report.WorldId);
                    ctx.SourceFolder = $"{SourceRoot}/{ctx.WorldKey}";
                    ctx.GeneratedFolder = $"{GeneratedRoot}/{ctx.WorldKey}";
                    EnsureFolder(UserContent);
                    ZimensionProjectSetup.EnsureConfigured(interactive: false);
                    StageAssets(ctx);
                    Progress(ctx, "Importing assets", 0.45f);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    FixGltfImportSettings(ctx);
                    PrepareGenerated(ctx);
                    CollectClips(ctx);
                    BuildProject(ctx);
                    Progress(ctx, "Building scenes", 0.7f);
                    result.ScenePaths = SceneBuilder.BuildScenes(ctx);
                    ctx.Project.compatibilitySummary = ctx.Report.Summary();
                    EditorUtility.SetDirty(ctx.Project);
                    WriteReport(ctx);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    if (options.UpdateBuildSettings) UpdateBuildSettings(result.ScenePaths);
                    result.Report = ctx.Report;
                    result.Project = ctx.Project;
                    result.Success = !ctx.Report.HasBlocking;
                    if (!result.Success) result.Error = "The export has blocking problems; see export-report.json.";
                    Debug.Log("[Zimension] " + ctx.Report.Summary());
                    if (options.OpenBaseScene && result.ScenePaths.Count > 0 && !Application.isBatchMode)
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(result.ScenePaths[0]);
                }
            }
            catch (Exception error)
            {
                result.Success = false;
                result.Error = error.Message;
                result.Report = ctx.Report;
                Debug.LogError("[Zimension] Import failed: " + error);
            }
            finally
            {
                if (ctx.ShowProgress) EditorUtility.ClearProgressBar();
            }
            return result;
        }

        internal static void Progress(Context ctx, string stage, float amount)
        {
            if (ctx.ShowProgress) EditorUtility.DisplayProgressBar("Zimension import", stage, amount);
        }

        // ---- Names and folders -----------------------------------------------------------------

        public static string SafeName(string name, string fallback = "World")
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? "")
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : c == ' ' ? '_' : '_');
            var s = sb.ToString().Trim('_');
            if (s.Length == 0) s = SafeName(fallback, "World");
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        static string SafeFileName(string entry)
        {
            var name = Path.GetFileName(entry);
            var sb = new StringBuilder();
            foreach (var c in name) sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == '#' || c == '%' ? '_' : c);
            var s = sb.ToString();
            return s.Length > 120 ? s.Substring(0, 80) + "_" + Hash(Encoding.UTF8.GetBytes(s)).Substring(0, 12) + Path.GetExtension(s) : s;
        }

        public static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(data);
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ---- Staging ---------------------------------------------------------------------------

        /// <summary>Copy one bundle entry to an asset path (unchanged files are left alone); returns the asset path.</summary>
        internal static string Stage(Context ctx, string entry, string folder, string role, string rename = null, JsonNode item = null)
        {
            if (entry == null) return null;
            if (ctx.Staged.TryGetValue(entry, out var existing)) return existing;
            var data = ctx.Bundle.ReadAllBytes(entry);
            var fileName = rename ?? SafeFileName(entry);
            var assetPath = $"{ctx.SourceFolder}/{folder}/{fileName}";
            var full = Path.GetFullPath(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var hash = Hash(data);
            bool same = File.Exists(full) && new FileInfo(full).Length == data.Length && Hash(File.ReadAllBytes(full)) == hash;
            if (!same) File.WriteAllBytes(full, data);
            ctx.Staged[entry] = assetPath;
            ctx.Report.Assets.Add(new ImportReport.Asset { Role = role, Source = entry, Path = assetPath, Sha256 = hash, Bytes = data.LongLength });
            return assetPath;
        }

        /// <summary>A model and, for .gltf, its external buffers and images (paths relative to it are kept).</summary>
        internal static string StageModel(Context ctx, string entry, string folder, string role, JsonNode item)
        {
            if (entry == null) return null;
            if (ctx.Staged.TryGetValue(entry, out var existing)) return existing;
            var ext = Path.GetExtension(entry).ToLowerInvariant();
            if (ext != ".glb" && ext != ".gltf" && ext != ".fbx" && ext != ".obj")
            {
                // The web packager keeps the uploaded name; sniff GLB content behind an odd extension.
                var head = ctx.Bundle.ReadAllBytes(entry);
                if (head.Length > 4 && head[0] == 0x67 && head[1] == 0x6C && head[2] == 0x54 && head[3] == 0x46)
                    return Stage(ctx, entry, folder, role, SafeFileName(entry) + ".glb", item);
                ctx.Report.Add(ImportReport.Level.Unsupported, $"Model format '{ext}' is not imported", "file", item);
                return null;
            }
            if (ext == ".gltf")
            {
                var gltf = GltfInfo.ReadJson(ctx.Bundle.ReadAllBytes(entry));
                var baseDir = Path.GetDirectoryName(entry)?.Replace('\\', '/') ?? "";
                // A .gltf and its companions go into their own folder so relative URIs stay valid.
                var sub = $"{folder}/{SafeName(Path.GetFileNameWithoutExtension(entry))}";
                foreach (var uri in GltfInfo.ExternalUris(gltf))
                {
                    var relative = BundleSource.Normalize((baseDir.Length > 0 ? baseDir + "/" : "") + uri);
                    var companion = relative != null ? ctx.Bundle.Find(relative) : null;
                    if (companion == null) { ctx.Report.Add(ImportReport.Level.Blocking, $"glTF companion file '{uri}' is missing from the export", "file", item); continue; }
                    var target = BundleSource.Normalize(uri);
                    if (target == null) { ctx.Report.Add(ImportReport.Level.Blocking, $"glTF URI '{uri}' escapes its folder", "file", item); continue; }
                    StageExact(ctx, companion, $"{sub}/{target}", "gltf-companion");
                }
                return StageExact(ctx, entry, $"{sub}/{Path.GetFileName(entry)}", role);
            }
            return Stage(ctx, entry, folder, role, null, item);
        }

        static string StageExact(Context ctx, string entry, string relativeAssetPath, string role)
        {
            if (ctx.Staged.TryGetValue(entry, out var existing)) return existing;
            var data = ctx.Bundle.ReadAllBytes(entry);
            var assetPath = $"{ctx.SourceFolder}/{relativeAssetPath}";
            var full = Path.GetFullPath(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var hash = Hash(data);
            if (!(File.Exists(full) && Hash(File.ReadAllBytes(full)) == hash)) File.WriteAllBytes(full, data);
            ctx.Staged[entry] = assetPath;
            ctx.Report.Assets.Add(new ImportReport.Asset { Role = role, Source = entry, Path = assetPath, Sha256 = hash, Bytes = data.LongLength });
            return assetPath;
        }

        static readonly HashSet<string> AudioExtensions = new HashSet<string> { ".mp3", ".wav", ".ogg", ".aif", ".aiff", ".m4a" };
        static readonly HashSet<string> ImageExtensions = new HashSet<string> { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".exr", ".hdr", ".tif", ".tiff" };

        static void StageSound(Context ctx, JsonNode sound, JsonNode owner)
        {
            if (!sound.IsObject) return;
            var entry = ctx.Resolver.Sound(sound);
            if (entry == null)
            {
                if (!string.IsNullOrEmpty(sound.Str("url")) || !string.IsNullOrEmpty(sound.Str("fileName")))
                    ctx.Report.Add(ImportReport.Level.Blocking, $"Sound '{sound.Str("fileName", sound.Str("name"))}' is missing from the export", "sounds", owner);
            }
            else
            {
                var ext = Path.GetExtension(entry).ToLowerInvariant();
                var name = SafeFileName(entry);
                if (!AudioExtensions.Contains(ext)) name += ".mp3"; // web packager names keep the upload's name
                if (ext == ".webm" || ext == ".opus") ctx.Report.Add(ImportReport.Level.Unsupported, $"Audio format '{ext}' is not supported by Unity", "sounds", owner);
                else Stage(ctx, entry, "Audio", "sound", name, owner);
            }
            if (string.IsNullOrEmpty(sound.Str("voiceDataUrl"))) return;
            var voice = ctx.Resolver.Voice(sound);
            if (voice == null)
            {
                ctx.Report.Add(ImportReport.Level.Optional, "Lip-sync voice JSON is missing; the sound plays without facial animation", "sounds.voiceDataUrl", owner);
                return;
            }
            // The web packager stores the JSON under the sound's file name (e.g. "...mp3"): give it a .json name.
            Stage(ctx, voice, "Voice", "voice-json", Path.GetFileNameWithoutExtension(SafeFileName(voice)) + "_" + sound.Str("_id") + ".json", owner);
        }

        static void StageImage(Context ctx, string url, string role, JsonNode owner = null)
        {
            if (string.IsNullOrEmpty(url)) return;
            var entry = ctx.Resolver.Url(url);
            if (entry == null)
            {
                ctx.Report.Add(ImportReport.Level.Optional, $"Image '{url}' is not in the export", role, owner);
                return;
            }
            var ext = Path.GetExtension(entry).ToLowerInvariant();
            if (ext == ".webp" || ext == ".svg" || ext == ".gif")
            {
                ctx.Report.Add(ImportReport.Level.Approximated, $"Image format '{ext}' is not imported by Unity; convert it to PNG", role, owner);
                return;
            }
            if (!ImageExtensions.Contains(ext)) return;
            Stage(ctx, entry, role == "texture" || role == "environment" ? "Textures" : "UI", role, null, owner);
        }

        static void StageAssets(Context ctx)
        {
            Progress(ctx, "Collecting models", 0.1f);
            var world = ctx.World;
            int index = 0;
            foreach (var item in ctx.Items)
            {
                if (ctx.ShowProgress && index++ % 10 == 0) Progress(ctx, $"Collecting {item.Str("name")}", 0.1f + 0.25f * index / Math.Max(1, ctx.Items.Count));
                var file = item.Obj("file");
                if (file.IsObject && (item.Str("type", "item") == "item" || !item.Has("type")))
                {
                    var entry = ctx.Resolver.ModelFile(file);
                    if (entry == null) ctx.Report.Add(ImportReport.Level.Blocking, $"Model '{file.Str("name")}' is missing from the export", "file", item);
                    else StageModel(ctx, entry, "Models", "model", item);
                }
                foreach (var sound in item["sounds"].Items) StageSound(ctx, sound, item);
                foreach (var source in item["characterAnimationSources"].Items) StageAnimationSource(ctx, source, item);
                var primitive = item.Obj("primitiveSettings");
                if (primitive.IsObject && !string.IsNullOrEmpty(primitive.Str("textureUrl"))) StageImage(ctx, primitive.Str("textureUrl"), "texture", item);
                // Customization textures (library parameters with the item's own values or their defaults).
                foreach (var parameter in item.Obj("libraryItem").Obj("customSettings")["parameters"].Items)
                {
                    if (!parameter.Str("type").Contains("TEXTURE")) continue;
                    var custom = item.Obj("customSettings").Obj(parameter.Str("name"));
                    var url = custom.Has("value") && custom.Str("valueType") != "variable" ? custom.Str("value") : parameter.Str("default");
                    if (!string.IsNullOrEmpty(url)) StageImage(ctx, url, "texture", item);
                }
            }
            foreach (var sound in world["sounds"].Items) StageSound(ctx, sound, null);
            // Player characters.
            var avatar = world.Obj("avatar");
            foreach (var character in new[] { avatar.Obj("character"), avatar.Obj("libraryItem") })
            {
                if (!character.IsObject) continue;
                var file = character.Obj("file");
                var entry = file.IsObject ? ctx.Resolver.ModelFile(file) : ctx.Resolver.Url(character.Str("avatarUrl"));
                if (entry != null) StageModel(ctx, entry, "Characters", "avatar", null);
                else if (file.IsObject || character.Has("avatarUrl")) ctx.Report.Add(ImportReport.Level.Approximated, "Player character model is missing from the export; a capsule player is used", "avatar.character");
                foreach (var source in character["characterAnimationSources"].Items) StageAnimationSource(ctx, source, null);
                break;
            }
            foreach (var sw in avatar["characterSwitches"].Items)
            {
                var file = sw.Obj("character").Obj("file");
                var entry = file.IsObject ? ctx.Resolver.ModelFile(file) : null;
                if (entry != null) StageModel(ctx, entry, "Characters", "avatar-switch", null);
            }
            // UI images.
            StageImage(ctx, world.Str("loadingScreen"), "loading-screen");
            StageImage(ctx, world.Str("loadingGraphic"), "loading-logo");
            foreach (var e in world.Obj("customHUDUI")["elements"].Items) if (e.Str("type") == "image") StageImage(ctx, e.Str("src"), "ui-image");
            foreach (var o in world["customUIOverRelays"].Items)
                foreach (var e in o["elements"].Items) if (e.Str("type") == "image") StageImage(ctx, e.Str("src"), "ui-image");
            if (!string.IsNullOrEmpty(world.Str("env"))) StageImage(ctx, world.Str("env"), "environment");
            // Animation libraries: other glTF files the export packaged (character rigs and clip libraries).
            Progress(ctx, "Collecting animation libraries", 0.38f);
            foreach (var name in ctx.Bundle.Names.ToList())
            {
                if (ctx.Staged.ContainsKey(name)) continue;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext != ".glb") continue;
                if (ctx.Bundle.Length(name) > 64L * 1024 * 1024) continue;
                var gltf = GltfInfo.ReadJson(ctx.Bundle.ReadAllBytes(name));
                if (gltf["animations"].Count == 0) continue;
                StageModel(ctx, name, "Library", "animation-library", null);
            }
            // Raw source data for traceability (never read by the runtime).
            var raw = $"{ctx.SourceFolder}/masterData.json";
            Directory.CreateDirectory(Path.GetFullPath(ctx.SourceFolder));
            File.WriteAllText(Path.GetFullPath(raw), ctx.Bundle.MasterDataText);
        }

        static void StageAnimationSource(Context ctx, JsonNode source, JsonNode owner)
        {
            if (!source.IsObject) return;
            var entry = ctx.Resolver.Url(source.Str("url"));
            if (entry == null)
            {
                ctx.Report.Add(ImportReport.Level.Approximated, $"Character animation source '{source.Str("name", source.Str("_id"))}' is missing from the export", "characterAnimationSources", owner);
                return;
            }
            var format = source.Str("format", Path.GetExtension(entry).TrimStart('.')).ToLowerInvariant();
            if (format == "fbx" && !entry.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                Stage(ctx, entry, "Library", "animation-source", SafeFileName(entry) + ".fbx", owner);
            else StageModel(ctx, entry, "Library", "animation-source", owner);
        }

        // ---- After Unity imported the files ----------------------------------------------------

        static void FixGltfImportSettings(Context ctx)
        {
            foreach (var path in ctx.Staged.Values)
            {
                var lower = path.ToLowerInvariant();
                if (!lower.EndsWith(".glb") && !lower.EndsWith(".gltf")) continue;
                var importer = AssetImporter.GetAtPath(path);
                if (importer == null)
                {
                    ctx.Report.Add(ImportReport.Level.Blocking, $"No glTF importer for {path}. Install com.unity.cloud.gltfast (Zimension > Setup Project).");
                    continue;
                }
                // Clips must be Mecanim (Playables); a legacy import is reconfigured and reimported once.
                var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().ToList();
                bool legacy = clips.Any(c => c.legacy);
                if (ZimensionAssetPostprocessor.ConfigureGltf(importer) || legacy) importer.SaveAndReimport();
            }
        }

        static void CollectClips(Context ctx)
        {
            foreach (var pair in ctx.Staged)
            {
                var path = pair.Value;
                var lower = path.ToLowerInvariant();
                if (!lower.EndsWith(".glb") && !lower.EndsWith(".gltf") && !lower.EndsWith(".fbx")) continue;
                var all = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToList();
                var ordered = new List<AnimationClip>();
                if (!lower.EndsWith(".fbx"))
                {
                    // glTF animation order (cards and sources address clips by index).
                    var names = GltfInfo.AnimationNames(GltfInfo.ReadJson(File.ReadAllBytes(Path.GetFullPath(path))));
                    var pool = new List<AnimationClip>(all);
                    foreach (var name in names)
                    {
                        var clip = pool.FirstOrDefault(c => c.name == name) ?? pool.FirstOrDefault(c => c.name.StartsWith(name, StringComparison.Ordinal));
                        ordered.Add(clip);
                        if (clip) pool.Remove(clip);
                    }
                    foreach (var rest in pool) ordered.Add(rest);
                }
                else ordered.AddRange(all);
                // Playables need Mecanim clips: a clip still imported as Legacy is converted to a generated copy.
                for (int i = 0; i < ordered.Count; i++)
                    if (ordered[i] && ordered[i].legacy) ordered[i] = ConvertLegacy(ctx, ordered[i], path);
                ctx.ModelClips[path] = ordered;
                ctx.SourceClips[pair.Key] = ordered;
                foreach (var clip in ordered)
                {
                    if (!clip) continue;
                    if (!ctx.LibraryByName.ContainsKey(clip.name)) ctx.LibraryByName[clip.name] = clip;
                    ctx.LibraryClips.Add(clip);
                }
            }
        }

        static AnimationClip ConvertLegacy(Context ctx, AnimationClip legacy, string sourcePath)
        {
            var copy = new AnimationClip { name = legacy.name, frameRate = legacy.frameRate, legacy = false, wrapMode = legacy.wrapMode };
            foreach (var binding in AnimationUtility.GetCurveBindings(legacy))
            {
                var curve = AnimationUtility.GetEditorCurve(legacy, binding);
                if (curve != null) AnimationUtility.SetEditorCurve(copy, binding, curve);
            }
            copy.EnsureQuaternionContinuity();
            var folder = $"{ctx.GeneratedFolder}/Animations/Converted";
            EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(Path.GetFileNameWithoutExtension(sourcePath))}_{SafeName(legacy.name)}.anim");
            AssetDatabase.CreateAsset(copy, path);
            ctx.Report.Add(ImportReport.Level.Optional, $"Clip '{legacy.name}' in {Path.GetFileName(sourcePath)} was imported as Legacy and converted for playback", "animations");
            return copy;
        }

        static void PrepareGenerated(Context ctx)
        {
            // Generated content is fully managed: replace it (UserContent and SourceAssets stay).
            foreach (var sub in new[] { "Scenes", "Meshes", "Materials", "Animations" })
            {
                var folder = $"{ctx.GeneratedFolder}/{sub}";
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                EnsureFolder(folder);
            }
        }

        static void BuildProject(Context ctx)
        {
            var path = $"{ctx.GeneratedFolder}/{ctx.WorldKey}.asset";
            var project = AssetDatabase.LoadAssetAtPath<ZimensionProject>(path);
            if (!project)
            {
                project = ScriptableObject.CreateInstance<ZimensionProject>();
                AssetDatabase.CreateAsset(project, path);
            }
            ctx.Project = project;
            var world = ctx.World;
            project.worldId = world.Str("_id");
            project.worldName = world.Str("name", "Zimension World");
            project.exporterVersion = Version;
            project.importedAtUtc = DateTime.UtcNow.ToString("o");
            project.worldJson = world.Dump();
            // World sound bank (slots preserved).
            var sounds = new List<AudioClip>(); var subtitles = new List<string>();
            foreach (var sound in world["sounds"].Items)
            {
                var entry = sound.IsObject ? ctx.Resolver.Sound(sound) : null;
                sounds.Add(entry != null ? AssetDatabase.LoadAssetAtPath<AudioClip>(ctx.AssetFor(entry)) : null);
                subtitles.Add(sound.IsObject ? sound.Str("text") : "");
            }
            project.worldSounds = sounds.ToArray();
            project.worldSoundSubtitles = subtitles.ToArray();
            // UI images.
            project.uiImages.Clear();
            void AddImage(string url)
            {
                if (string.IsNullOrEmpty(url) || project.uiImages.Any(i => i.url == url)) return;
                var entry = ctx.Resolver.Url(url);
                var tex = entry != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(ctx.AssetFor(entry)) : null;
                if (tex) project.uiImages.Add(new ZimensionProject.NamedTexture { url = url, texture = tex });
            }
            foreach (var e in world.Obj("customHUDUI")["elements"].Items) if (e.Str("type") == "image") AddImage(e.Str("src"));
            foreach (var o in world["customUIOverRelays"].Items) foreach (var e in o["elements"].Items) if (e.Str("type") == "image") AddImage(e.Str("src"));
            Texture2D Image(string url) { var entry = ctx.Resolver.Url(url); return entry != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(ctx.AssetFor(entry)) : null; }
            project.loadingImage = Image(world.Str("loadingScreen"));
            project.logoImage = Image(world.Str("loadingGraphic"));
            BuildAvatar(ctx);
            EditorUtility.SetDirty(project);
        }

        static void BuildAvatar(Context ctx)
        {
            var project = ctx.Project;
            project.avatarPrefab = null;
            project.idleClip = project.walkClip = project.runClip = project.jumpClip = null;
            var avatar = ctx.World.Obj("avatar");
            var character = avatar.Obj("character").IsObject ? avatar.Obj("character") : avatar.Obj("libraryItem");
            if (!character.IsObject) return;
            var file = character.Obj("file");
            var entry = file.IsObject ? ctx.Resolver.ModelFile(file) : ctx.Resolver.Url(character.Str("avatarUrl"));
            var path = ctx.AssetFor(entry);
            var prefab = path != null ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
            if (!prefab) return;
            project.avatarPrefab = prefab;
            project.avatarHeight = Mathf.Clamp(character.Float("characterHeight", 1.8f), 0.2f, 10f);
            var map = character.Obj("characterAnimationMap");
            var own = ctx.ModelClips.TryGetValue(path, out var list) ? list : new List<AnimationClip>();
            var sourceClips = SourceClipsByName(ctx, character["characterAnimationSources"]);
            AnimationClip Resolve(string action, params string[] fallbacks)
            {
                // Avatar.getCharacterAnimationName: the creator's map first, then the standard action names.
                var names = new List<string>();
                var mapped = map.Str(action);
                if (!string.IsNullOrEmpty(mapped)) names.Add(mapped);
                names.Add(action);
                names.AddRange(fallbacks);
                var clip = FindBestClip(ctx, names, prefab.transform, own, sourceClips, "Player");
                if (clip)
                    ctx.Report.Add(ImportReport.Level.Optional, $"Player '{action}' plays '{clip.name}' ({Path.GetFileName(AssetDatabase.GetAssetPath(clip))}, skeleton fit {ClipRetarget.DirectCoverage(clip, prefab.transform):P0})", "avatar.character");
                if (!clip)
                    ctx.Report.Add(ImportReport.Level.Approximated, $"Player '{action}' animation is not available for the character; the avatar stays posed for it", "avatar.character.characterAnimationMap");
                return clip;
            }
            // Avatar.js BASE_ACTIONS / getCharacterAnimationName codes.
            project.idleClip = Resolve("Idle", "Breathing Idle", "Standing Idle", "idle");
            project.walkClip = Resolve("Walk", "Walking", "walk_forward");
            project.runClip = Resolve("Run", "Running", "running_forward");
            project.jumpClip = Resolve("Jump_forward", "Jump", "Jumping", "jump_normal");
        }

        /// <summary>
        /// The packaged animation library (rpm_m, rpm_f, ultron, zimension, mixamo, custom, ...) whose
        /// clips fit this skeleton best, in its glTF clip order; null when none fits at least half.
        /// </summary>
        internal static List<AnimationClip> BestLibraryFor(Context ctx, Transform modelRoot, out string name, out float fit)
        {
            name = ""; fit = 0;
            List<AnimationClip> best = null; float bestDirect = -1, bestNamed = -1;
            foreach (var asset in ctx.Report.Assets)
            {
                if (asset.Role != "animation-library" || !ctx.ModelClips.TryGetValue(asset.Path, out var clips) || clips.Count == 0) continue;
                var probe = clips.FirstOrDefault(c => c);
                if (!probe) continue;
                float direct = ClipRetarget.DirectCoverage(probe, modelRoot);
                float named = direct >= 0.999f ? 1 : ClipRetarget.NameCoverage(probe, modelRoot);
                if (direct > bestDirect + 1e-4f || (Mathf.Abs(direct - bestDirect) <= 1e-4f && named > bestNamed))
                {
                    best = clips; bestDirect = direct; bestNamed = named; name = Path.GetFileName(asset.Path);
                }
            }
            if (best == null || bestNamed < 0.5f) { name = ""; return null; }
            fit = Mathf.Max(bestDirect, bestNamed);
            return best;
        }

        /// <summary>Avatar.normalizeAnimationName: lowercase letters and digits only.</summary>
        internal static string NormalizeClipName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// The clip for the first name (in preference order) that this model can play: its own clips,
        /// then its character animation sources, then every imported library. Libraries share names
        /// ("Idle" exists for Ready Player Me, Ultron, Zimension and Mixamo rigs), so among same-named
        /// clips the one whose bones fit this skeleton best wins (Engine.checkCharacterAnimations picks
        /// the library by rig type; fit by bone paths reaches the same choice without the heuristics).
        /// </summary>
        internal static AnimationClip FindBestClip(Context ctx, List<string> names, Transform modelRoot,
            List<AnimationClip> own, Dictionary<string, AnimationClip> sources, string owner)
        {
            foreach (var name in names)
            {
                var key = NormalizeClipName(name);
                if (key.Length == 0) continue;
                var mine = own.FirstOrDefault(c => c && (c.name == name || NormalizeClipName(c.name) == key));
                if (mine) return mine;
                if (sources != null)
                {
                    AnimationClip fromSource = null;
                    if (!sources.TryGetValue(name, out fromSource))
                        foreach (var pair in sources) if (NormalizeClipName(pair.Key) == key) { fromSource = pair.Value; break; }
                    if (fromSource)
                    {
                        var bound = BindClip(ctx, fromSource, modelRoot, owner);
                        if (bound) return bound;
                    }
                }
                AnimationClip best = null; float bestDirect = -1, bestNamed = -1;
                foreach (var clip in ctx.LibraryClips)
                {
                    if (!clip || (clip.name != name && NormalizeClipName(clip.name) != key)) continue;
                    float direct = ClipRetarget.DirectCoverage(clip, modelRoot);
                    float named = direct >= 0.999f ? 1 : ClipRetarget.NameCoverage(clip, modelRoot);
                    if (direct > bestDirect + 1e-4f || (Mathf.Abs(direct - bestDirect) <= 1e-4f && named > bestNamed))
                    {
                        best = clip; bestDirect = direct; bestNamed = named;
                    }
                }
                if (best && bestNamed >= 0.5f)
                {
                    var bound = BindClip(ctx, best, modelRoot, owner);
                    if (bound) return bound;
                }
            }
            return null;
        }

        /// <summary>A clip bound to a model hierarchy, saved as a generated .anim when it had to be remapped.</summary>
        internal static AnimationClip BindClip(Context ctx, AnimationClip clip, Transform modelRoot, string owner)
        {
            var key = AssetDatabase.GetAssetPath(clip) + "|" + clip.name + "|" + AssetDatabase.GetAssetPath(modelRoot.gameObject) + "|" + modelRoot.name;
            if (ctx.Retargeted.TryGetValue(key, out var saved)) return AssetDatabase.LoadAssetAtPath<AnimationClip>(saved);
            var bound = ClipRetarget.BindTo(clip, modelRoot, out var coverage);
            if (!bound) return null;
            if (bound == clip) return clip;
            var folder = $"{ctx.GeneratedFolder}/Animations/{SafeName(owner)}";
            EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(clip.name)}.anim");
            AssetDatabase.CreateAsset(bound, path);
            ctx.Retargeted[key] = path;
            if (coverage < 0.95f)
                ctx.Report.Add(ImportReport.Level.Approximated, $"Animation '{clip.name}' was retargeted by bone name ({coverage:P0} of curves bound); rest-pose differences are not corrected", "animations");
            return bound;
        }

        /// <summary>characterAnimationSources clips by the names the web gives them: bindingName, or "Name [sourceId:index]".</summary>
        internal static Dictionary<string, AnimationClip> SourceClipsByName(Context ctx, JsonNode sources)
        {
            var result = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            foreach (var source in sources.Items)
            {
                if (!source.IsObject) continue;
                var entry = ctx.Resolver.Url(source.Str("url"));
                if (entry == null || !ctx.SourceClips.TryGetValue(entry, out var clips)) continue;
                foreach (var c in source["clips"].Items)
                {
                    int index = (int)c.Num("index", -1);
                    if (index < 0 || index >= clips.Count || !clips[index]) continue;
                    var name = c.Str("bindingName");
                    if (string.IsNullOrEmpty(name)) name = $"{c.Str("name", clips[index].name)} [{source.Str("_id")}:{index}]";
                    result[name] = clips[index];
                    if (!result.ContainsKey(c.Str("name"))) result[c.Str("name")] = clips[index];
                }
            }
            return result;
        }

        static void WriteReport(Context ctx)
        {
            var path = $"{ctx.GeneratedFolder}/export-report.json";
            File.WriteAllText(Path.GetFullPath(path), ctx.Report.ToJson().Dump(pretty: true));
            var manifest = JsonNode.NewObject();
            manifest.Set("exporterVersion", new JsonNode(Version));
            manifest.Set("runtimeVersion", new JsonNode(Version));
            manifest.Set("schemaVersion", new JsonNode("1"));
            manifest.Set("unity", new JsonNode(Application.unityVersion));
            manifest.Set("worldId", new JsonNode(ctx.Report.WorldId));
            manifest.Set("baseScene", new JsonNode(ctx.Project.baseSceneId ?? ""));
            manifest.Set("sourceUpdatedAt", new JsonNode(ctx.World.Str("updatedAt")));
            File.WriteAllText(Path.GetFullPath($"{ctx.GeneratedFolder}/manifest.json"), manifest.Dump(pretty: true));
        }

        static void UpdateBuildSettings(List<string> scenePaths)
        {
            var existing = EditorBuildSettings.scenes.Where(s => !scenePaths.Contains(s.path) && File.Exists(s.path) && !s.path.StartsWith(GeneratedRoot, StringComparison.Ordinal)).ToList();
            var scenes = scenePaths.Select(p => new EditorBuildSettingsScene(p, true)).ToList();
            scenes.AddRange(existing);
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
