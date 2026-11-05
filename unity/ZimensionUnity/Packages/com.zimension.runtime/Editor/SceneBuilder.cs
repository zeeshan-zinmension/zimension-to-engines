using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Zimension.Core;
using Zimension.Unity;
using N = System.Numerics;

namespace Zimension.Editor
{
    /// <summary>
    /// Generates one Unity scene per Zimension scene with Editor APIs (no hand-written YAML):
    /// a "Zimension World" root running <see cref="ZimensionWorld"/>, one GameObject per placed item
    /// carrying <see cref="ZimensionItem"/> (model prefab instance, Basics mesh, light or logic-only
    /// node), colliders by the item's collision mode, sounds and voice data, transition states in
    /// Unity space, the sun, environment, ground and an editable spawn point.
    ///
    /// Placement follows the native player's ProjectSource: items keep their saved world transforms
    /// (scene-graph groups are folders), copies resolve through source aliases, an item tied to a
    /// different scene is skipped, and the base scene loads first.
    /// </summary>
    internal static class SceneBuilder
    {
        sealed class SceneInfo
        {
            public string Id, Name, SwitchOnEvent;
            public JsonNode Settings = JsonNode.Undefined;
            public bool Fade = true;
            public float FadeDuration = 0.35f, FadeInDelay;
        }

        static readonly HashSet<string> LogicOnlyTypes = new HashSet<string> { "vfx", "empty", "connector", "spawnPoint", "spawn" };

        public static List<string> BuildScenes(ZimensionImporter.Context ctx)
        {
            var world = ctx.World;
            // Scene graph (world.nodes, or sceneGraph.scenes).
            var nodes = world["nodes"];
            if (!nodes.IsArray || nodes.Count == 0) nodes = world.Obj("sceneGraph")["scenes"];
            var flat = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            void Collect(JsonNode n, int depth)
            {
                if (!n.IsObject || depth > 128) return;
                var id = n.Str("id");
                if (id.Length > 0) flat[id] = n;
                foreach (var c in n["children"].Items) Collect(c, depth + 1);
            }
            foreach (var n in nodes.Items) Collect(n, 0);
            var scenes = new List<SceneInfo>();
            foreach (var n in flat.Values)
            {
                if (n.Str("type") != "scene") continue;
                var settings = n.Obj("settings");
                var transition = settings.Obj("transitionSettings");
                scenes.Add(new SceneInfo
                {
                    Id = n.Str("id"), Name = n.Str("name", n.Str("id")),
                    SwitchOnEvent = settings.Obj("worldSettings").Str("switchOnEvent"),
                    Settings = settings.Obj("worldSettings"),
                    Fade = transition.Str("mode", "fade") == "fade",
                    FadeDuration = Mathf.Clamp(transition.Float("fadeDuration", 0.35f), 0, 10),
                    FadeInDelay = Mathf.Clamp(transition.Float("fadeInDelay", 0), 0, 10),
                });
            }
            var baseId = world.Str("baseSceneId", world.Obj("sceneGraph").Str("baseSceneId"));
            if (baseId.Length > 0 && !flat.ContainsKey(baseId)) baseId = "";
            if (baseId.Length == 0) baseId = scenes.FirstOrDefault(s => flat[s.Id].Truthy("isBaseScene"))?.Id ?? scenes.FirstOrDefault()?.Id ?? "";
            if (scenes.Count == 0) scenes.Add(new SceneInfo { Id = "", Name = "Main" });
            // Base scene first (it is the Build Settings entry that plays first).
            scenes = scenes.OrderBy(s => s.Id == baseId ? 0 : 1).ToList();

            // Aliases: copies and references resolve to the item record's _id.
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in ctx.Items)
                foreach (var key in new[] { "_id", "id" }) { var r = item.Str(key); if (r.Length > 0) aliases[r] = item.Str("_id"); }
            foreach (var item in ctx.Items)
                foreach (var key in new[] { "sourceWorldItemId", "originalWorldItemId", "worldItemId" })
                { var r = item.Str(key); if (r.Length > 0 && !aliases.ContainsKey(r)) aliases[r] = item.Str("_id"); }
            string Reference(JsonNode n) => n.Str("worldItemId", n.Str("itemId", n.Str("worldItem")));
            string Canonical(JsonNode n) { var r = Reference(n); return aliases.TryGetValue(r, out var id) ? id : r; }
            var referenced = new HashSet<string>(flat.Values.Select(Canonical).Where(r => r.Length > 0), StringComparer.Ordinal);
            var roots = new List<JsonNode>();
            var children = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
            foreach (var n in nodes.Items)
            {
                if (!n.IsObject) continue;
                var parent = n.Str("parentId");
                if (parent.Length == 0 || !flat.ContainsKey(parent)) roots.Add(n);
                else { if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<JsonNode>(); list.Add(n); }
            }

            ctx.Project.scenes.Clear();
            ctx.Project.baseSceneId = baseId;
            var paths = new List<string>();
            var sceneFolder = $"{ctx.GeneratedFolder}/Scenes";
            int sceneIndex = 0;
            foreach (var scene in scenes)
            {
                // Items placed by this scene's subtree.
                var placed = new HashSet<string>(StringComparer.Ordinal);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                void Visit(JsonNode node, int depth)
                {
                    if (depth > 128) return;
                    var id = node.Str("id");
                    if (id.Length > 0 && !visited.Add(id)) return;
                    if (node.Str("type") == "scene" && scene.Id.Length > 0 && id != scene.Id) return;
                    var r = Canonical(node);
                    if (r.Length > 0) placed.Add(r);
                    if (node.Str("type") == "instanceGroup")
                        ctx.Report.Add(ImportReport.Level.Unsupported, $"Instance group '{node.Str("name")}' (procedural/brushed copies) is not generated yet", "nodes.settings.instance");
                    foreach (var c in node["children"].Items)
                    {
                        if (c.IsObject) Visit(c, depth + 1);
                        else if (c.IsString && flat.TryGetValue(c.AsString, out var child)) Visit(child, depth + 1);
                    }
                    if (id.Length > 0 && children.TryGetValue(id, out var kids)) foreach (var k in kids) Visit(k, depth + 1);
                }
                foreach (var root in roots) Visit(root, 0);

                var unityName = ZimensionImporter.SafeName($"{ctx.WorldKey}_{(sceneIndex == 0 ? "Base" : sceneIndex.ToString())}_{scene.Name}");
                var path = $"{sceneFolder}/{unityName}.unity";
                ZimensionImporter.Progress(ctx, $"Building scene {scene.Name}", 0.7f + 0.25f * sceneIndex / Math.Max(1, scenes.Count));
                var sceneWorld = ctx.Project.WorldForScene(scene.Id);
                // WorldForScene needs the entry: add it first.
                var entry = new ZimensionProject.SceneEntry
                {
                    sceneId = scene.Id, name = scene.Name, unityScene = unityName, switchOnEvent = scene.SwitchOnEvent,
                    fade = scene.Fade, fadeDuration = scene.FadeDuration, fadeInDelay = scene.FadeInDelay,
                    worldSettingsJson = scene.Settings.IsObject ? scene.Settings.Dump() : "{}",
                };
                ctx.Project.scenes.Add(entry);
                sceneWorld = ctx.Project.WorldForScene(scene.Id);

                var unityScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                BuildEnvironment(ctx, sceneWorld);
                var rootGo = new GameObject("Zimension World");
                var zw = rootGo.AddComponent<ZimensionWorld>();
                zw.project = ctx.Project;
                zw.sceneId = scene.Id;
                var camera = new GameObject("Main Camera") { tag = "MainCamera" };
                var cam = camera.AddComponent<Camera>();
                camera.AddComponent<AudioListener>();
                cam.nearClipPlane = 0.05f; cam.farClipPlane = 5000;
                cam.fieldOfView = Mathf.Clamp(sceneWorld.Obj("avatar").Float("fov", 60), 20, 120);
                zw.playCamera = cam;
                BuildSpawn(sceneWorld, rootGo.transform, camera.transform);
                int order = 0;
                foreach (var item in ctx.Items)
                {
                    order++;
                    var id = item.Str("_id");
                    if (!placed.Contains(id) && referenced.Contains(id)) continue;           // placed in another scene's tree
                    var itemScene = item.Str("sceneId");
                    if (scene.Id.Length > 0 && itemScene.Length > 0 && itemScene != scene.Id) continue;
                    try
                    {
                        if (BuildItem(ctx, item, rootGo.transform, order) && sceneIndex == 0) ctx.Report.ItemsPlaced++;
                    }
                    catch (Exception error)
                    {
                        ctx.Report.Add(ImportReport.Level.Blocking, "Item could not be generated: " + error.Message, "", item);
                        Debug.LogException(error);
                    }
                }
                EditorSceneManager.SaveScene(unityScene, path);
                paths.Add(path);
                ctx.Report.Scenes.Add(path);
                sceneIndex++;
            }
            EditorUtility.SetDirty(ctx.Project);
            return paths;
        }

        // ---- Environment ---------------------------------------------------------------------

        static Color ParseColor(string hex, Color fallback) => CssStyle.ParseColor(hex, fallback);

        static void BuildEnvironment(ZimensionImporter.Context ctx, JsonNode world)
        {
            // Sun: setSunPosition's top-view hemisphere (x east-west, y north-south), mirrored into Unity space.
            var sunXY = new Vector2(Mathf.Clamp(world.Obj("sunPosition").Float("x", 0.3f), -1, 1), Mathf.Clamp(world.Obj("sunPosition").Float("y", 0.3f), -1, 1));
            if (sunXY.sqrMagnitude > 1) sunXY.Normalize();
            var sunThree = new Vector3(sunXY.x, Mathf.Sqrt(Mathf.Max(0, 1 - sunXY.sqrMagnitude)), sunXY.y).normalized;
            var sunUnity = new Vector3(-sunThree.x, sunThree.y, sunThree.z);
            var sun = new GameObject("Sun");
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = ParseColor(world.Str("sunColor", "#ffffff"), Color.white);
            // three's physically based directional intensity (default ~3) maps to Unity's ~1.2 (approximated).
            light.intensity = Mathf.Clamp(world.Float("sunIntensity", 3) * 0.4f, 0, 8);
            light.shadows = world.Str("shadows") == "off" ? LightShadows.None : LightShadows.Soft;
            sun.transform.rotation = Quaternion.LookRotation(-sunUnity, Vector3.up);
            RenderSettings.sun = light;
            // Environment image (world.env) as a panoramic sky; otherwise the procedural sky.
            Material sky = null;
            var envEntry = ctx.Resolver.Url(world.Str("env"));
            var envTex = envEntry != null ? AssetDatabase.LoadAssetAtPath<Texture>(ctx.AssetFor(envEntry)) : null;
            var panoramic = Shader.Find("Skybox/Panoramic");
            if (envTex && panoramic)
            {
                sky = new Material(panoramic) { name = "Zimension Sky" };
                sky.SetTexture("_MainTex", envTex);
                AssetDatabase.CreateAsset(sky, AssetDatabase.GenerateUniqueAssetPath($"{ctx.GeneratedFolder}/Materials/Sky.mat"));
            }
            else
            {
                var procedural = Shader.Find("Skybox/Procedural");
                if (procedural)
                {
                    sky = new Material(procedural) { name = "Zimension Procedural Sky" };
                    AssetDatabase.CreateAsset(sky, AssetDatabase.GenerateUniqueAssetPath($"{ctx.GeneratedFolder}/Materials/Sky.mat"));
                }
                if (!string.IsNullOrEmpty(world.Str("env"))) ctx.Report.Add(ImportReport.Level.Approximated, "Environment map is not a Unity-importable image; the procedural sky is used", "env");
            }
            if (sky) RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = Mathf.Clamp(world.Float("exposure", 1), 0.2f, 3);
            // Ground: the saved floor, or an invisible floor that keeps the player from falling forever.
            bool showGround = world.Flag("showGround", true);
            float size = Mathf.Max(world.Float("size", 500), 50);
            var ground = new GameObject(showGround ? "Ground" : "Floor (invisible)");
            ground.isStatic = true;
            if (showGround)
            {
                var mesh = GetMesh(ctx, new MeshFactory.Shape { Name = "plane", Width = size, Depth = size, Segments = 1 });
                ground.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = ground.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = GetMaterial(ctx, ParseColor(world.Str("groundColor", world.Str("floorColor", "#7a8a6a")), new Color(0.48f, 0.54f, 0.42f)), 0.95f, 0, 1, Color.black, null, false);
                if (world.Has("floorMaterialIndex")) ctx.Report.Add(ImportReport.Level.Approximated, "Ground material presets are shown as a flat colour", "floorMaterialIndex");
            }
            var floor = ground.AddComponent<BoxCollider>();
            float extent = Mathf.Max(10000, size * 4);
            floor.size = new Vector3(extent, 0.2f, extent);
            floor.center = new Vector3(0, -0.1f, 0);
        }

        static void BuildSpawn(JsonNode world, Transform root, Transform camera)
        {
            var spawnGo = new GameObject("Player Spawn");
            spawnGo.transform.SetParent(root, false);
            spawnGo.AddComponent<ZimensionSpawnPoint>();
            var avatar = world.Obj("avatar");
            var feet = new Vector3(world.Obj("spawnPosition").Float("x", 0), world.Obj("spawnPosition").Float("y", 0), world.Obj("spawnPosition").Float("z", 0));
            float yawDegrees = 0;
            if (avatar["position"].IsObject || avatar["position"].IsString)
            {
                var p = avatar["position"].IsString && JsonNode.TryParse(avatar["position"].AsString, out var parsed) ? parsed : avatar["position"];
                feet = new Vector3(p.Float("x", 0), p.Float("y", 0), p.Float("z", 0)) * (0.01f / 2.2f);
            }
            var rotation = avatar.Obj("rotation");
            if (rotation.IsObject)
            {
                // three camera: forward (0,0,-1) turned by the saved Euler; mirrored into Unity.
                var q = Coordinates.EulerToQuaternion(rotation.Float("x", 0), rotation.Float("y", 0), rotation.Float("z", 0));
                var f = N.Vector3.Transform(new N.Vector3(0, 0, -1), q);
                yawDegrees = Mathf.Atan2(-f.X, f.Z) * Mathf.Rad2Deg;
            }
            if (Coordinates.TryMatrixFromThreeElements(avatar["transformation"], out var m))
            {
                feet = new Vector3(m.M41, m.M42, m.M43);
                var f = N.Vector3.Normalize(N.Vector3.TransformNormal(new N.Vector3(0, 0, -1), m));
                yawDegrees = Mathf.Atan2(-f.X, f.Z) * Mathf.Rad2Deg;
            }
            spawnGo.transform.position = new Vector3(-feet.x, feet.y, feet.z);
            spawnGo.transform.rotation = Quaternion.Euler(0, yawDegrees, 0);
            camera.position = spawnGo.transform.position + Vector3.up * 1.6f - spawnGo.transform.forward * 3;
            camera.rotation = spawnGo.transform.rotation;
        }

        // ---- Items -----------------------------------------------------------------------------

        /// <summary>The item's saved transform (three space): its matrix, or the legacy position/rotation/scale.</summary>
        static N.Matrix4x4 ItemPose(JsonNode item)
        {
            if (Coordinates.TryMatrixFromThreeElements(item["transformation"], out var m)) return m;
            if (Coordinates.TryMatrixFromThreeElements(item["matrix"], out m)) return m;
            JsonNode V(JsonNode v) => v.IsString && JsonNode.TryParse(v.AsString, out var parsed) ? parsed : v;
            var p = V(item["position"]); var r = V(item["rotation"]);
            var scaleNode = item["customScale"].Exists ? item["customScale"] : item["scale"];
            var s = scaleNode.IsNumber ? new N.Vector3((float)scaleNode.AsNumber) : new N.Vector3(V(scaleNode).Float("x", 1), V(scaleNode).Float("y", 1), V(scaleNode).Float("z", 1));
            const float legacy = 0.01f / 2.2f;
            return Coordinates.Compose(
                new N.Vector3(p.Float("x", 0), p.Float("y", 0), p.Float("z", 0)) * legacy,
                Coordinates.EulerToQuaternion(r.Float("x", 0), r.Float("y", 0), r.Float("z", 0)),
                s / 2.2f);
        }

        static bool BuildItem(ZimensionImporter.Context ctx, JsonNode item, Transform root, int order)
        {
            var type = item.Str("type", "item");
            var name = item.Str("name", type);
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            var pose = Coordinates.ThreeToUnity(ItemPose(item));
            if (!Coordinates.IsFinite(pose)) { ctx.Report.Add(ImportReport.Level.Blocking, "Item transform is not finite", "transformation", item); UnityEngine.Object.DestroyImmediate(go); return false; }
            UnityMath.ApplyLocal(go.transform, pose);
            var zi = go.AddComponent<ZimensionItem>();
            zi.itemId = item.Str("_id");
            zi.sourceId = item.Str("sourceWorldItemId");
            zi.itemName = name;
            zi.itemType = type;
            zi.order = order;
            zi.itemJson = item.Dump();
            zi.savedVisible = !(item["visible"].IsBool && !item["visible"].AsBool);
            // Transition states (state 0 is the item itself at play start).
            zi.states = new List<ZimensionPose> { ZimensionPose.FromTransform(go.transform) };
            foreach (var s in item["states"].Items)
                if (s.IsObject) zi.states.Add(ZimensionPose.FromMatrix(Coordinates.ThreeToUnity(EventParsing.StateMatrix(s))));

            bool hasGeometry = false;
            switch (type)
            {
                case "item": hasGeometry = BuildModel(ctx, item, zi); break;
                case "primitive": hasGeometry = BuildPrimitive(ctx, item, zi); break;
                case "light": BuildLight(item, zi); break;
                case "spline":
                    if (item.Obj("splineSettings").Obj("sweep").Truthy("enable"))
                        ctx.Report.Add(ImportReport.Level.Unsupported, "Spline sweep geometry (roads, rails) is not generated yet; the spline still drives path transitions", "splineSettings.sweep", item);
                    break;
                default:
                    if (!LogicOnlyTypes.Contains(type))
                        ctx.Report.Add(ImportReport.Level.Unsupported, $"Item type '{type}' is imported as an empty placeholder", "type", item);
                    break;
            }
            Colliders(ctx, item, zi, type, hasGeometry);
            Sounds(ctx, item, zi);
            ReportFeatures(ctx, item);
            return true;
        }

        static bool BuildModel(ZimensionImporter.Context ctx, JsonNode item, ZimensionItem zi)
        {
            var file = item.Obj("file");
            var entry = file.IsObject ? ctx.Resolver.ModelFile(file) : null;
            var path = ctx.AssetFor(entry);
            var prefab = path != null ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
            if (!prefab)
            {
                if (path != null) ctx.Report.Add(ImportReport.Level.Blocking, $"Model '{file.Str("name")}' did not import (see the Console for glTFast errors)", "file", item);
                return false;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, zi.transform);
            instance.name = prefab.name;
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            zi.modelRoot = instance.transform;
            // A model's collider_mesh_zim node is collision only.
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                if (t.name == "collider_mesh_zim")
                    foreach (var r in t.GetComponents<Renderer>()) r.enabled = false;
            bool hidden = item.Truthy("useAsColliderMesh");
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (hidden) r.enabled = false;
                r.shadowCastingMode = item.Flag("castShadow", true) && ctx.World.Str("shadows") != "off" ? ShadowCastingMode.On : ShadowCastingMode.Off;
                r.receiveShadows = item.Flag("receiveShadows", true);
            }
            // Clips: the model's own (glTF order, so animationIndex matches), then the cards' library clips.
            var clips = new List<AnimationClip>(ctx.ModelClips.TryGetValue(path, out var own) ? own : new List<AnimationClip>());
            var clipNames = clips.Select(c => c ? c.name : "").ToList();
            if (item.Truthy("allowAnimations"))
            {
                var sources = ZimensionImporter.SourceClipsByName(ctx, item["characterAnimationSources"]);
                foreach (var card in item["animations"].Items)
                {
                    var clipName = card.Str("animationName");
                    if (card.Str("animationSource") == "default" || string.IsNullOrEmpty(clipName)) continue;
                    if (clipNames.Contains(clipName)) continue;
                    // Character library clips (Idle, Talking, ...) chosen by how well they fit this model's skeleton.
                    var bound = ZimensionImporter.FindBestClip(ctx, new List<string> { clipName }, instance.transform, new List<AnimationClip>(), sources, zi.itemName);
                    if (!bound)
                    {
                        ctx.Report.Add(ImportReport.Level.Approximated, $"Animation '{clipName}' is not in the export or does not fit this model's skeleton; its card does nothing", "animations", item);
                        continue;
                    }
                    clips.Add(bound);
                    clipNames.Add(clipName); // the card's name, so the runtime resolves it
                }
            }
            zi.clips = clips.ToArray();
            zi.clipNames = clipNames.ToArray();
            ApplyCustomizations(ctx, item, instance);
            if (item.Has("rpmRetargetProfile")) ctx.Report.Add(ImportReport.Level.Approximated, "Ready Player Me retarget profile offsets are approximated by bone-name binding", "rpmRetargetProfile", item);
            return true;
        }

        /// <summary>
        /// WorldItem.checkCustomSettings (as the native ProjectSource.applyCustomizations): the library
        /// item's parameters (libraryItem.customSettings.parameters) applied to this item's meshes by node
        /// name ("all" for every mesh) or material name (targetType "material"). The item's own value
        /// (customSettings[name].value, or a variable's starting value) wins over the parameter default.
        /// Customized meshes get their own material; SIZE_* and ROTATION_* adjust the mesh node in its
        /// own axes, under the model hierarchy and the item transform.
        /// </summary>
        static void ApplyCustomizations(ZimensionImporter.Context ctx, JsonNode item, GameObject instance)
        {
            var parameters = item.Obj("libraryItem").Obj("customSettings")["parameters"];
            if (!parameters.IsArray || parameters.Count == 0) return;
            var customs = item.Obj("customSettings");
            JsonNode ValueOf(JsonNode parameter)
            {
                var value = parameter["default"];
                var custom = customs.Obj(parameter.Str("name"));
                if (custom.Has("value"))
                {
                    if (custom.Str("valueType") == "variable")
                    {
                        var variable = custom.Str("value");
                        foreach (var v in ctx.World["variables"].Items)
                            if (v.Str("name") == variable && v.Has("initialValue")) value = v["initialValue"];
                    }
                    else value = custom["value"];
                }
                return value;
            }
            string Text(JsonNode v) => v.IsString ? v.AsString : v.IsNumber ? EventValue.DisplayNumber(v.AsNumber) : "";
            float Number(JsonNode v, float fallback)
            {
                if (v.IsNumber) return (float)v.AsNumber;
                return v.IsString && float.TryParse(v.AsString, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
            }
            Texture2D Texture(string url)
            {
                var entry = ctx.Resolver.Url(url);
                var tex = entry != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(ctx.AssetFor(entry)) : null;
                if (!tex && !string.IsNullOrEmpty(url)) ctx.Report.Add(ImportReport.Level.Approximated, "A customization texture is missing from the export", "customSettings", item);
                return tex;
            }
            var unsupported = new HashSet<string>();
            int materialCount = 0;
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                var node = renderer.gameObject.name;
                var materials = renderer.sharedMaterials;
                bool changedAny = false;
                var adjust = N.Matrix4x4.Identity; bool moved = false;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var original = materials[slot];
                    if (!original) continue;
                    var materialName = original.name.Replace(" (Instance)", "");
                    Material copy = null;
                    foreach (var parameter in parameters.Items)
                    {
                        if (!parameter.IsObject) continue;
                        var names = new List<string>();
                        var target = parameter["targetMeshName"];
                        if (target.IsString) names.Add(target.AsString);
                        else foreach (var n in target.Items) if (n.IsString) names.Add(n.AsString);
                        bool materialTarget = parameter.Str("targetType") == "material";
                        if (materialTarget ? !names.Contains(materialName) : !(names.Contains(node) || names.Contains("all"))) continue;
                        var value = ValueOf(parameter);
                        if (!value.Exists) continue;
                        var type = parameter.Str("type");
                        var text = Text(value);
                        Material M() => copy ?? (copy = new Material(original) { name = original.name + " (Zimension)" });
                        switch (type)
                        {
                            case "COLOR": SetColor(M(), ParseColor(text, Color.white), keepAlpha: true); break;
                            case "EMISSIVE_COLOR": SetEmission(M(), ParseColor(text, Color.black), null); break;
                            case "EMISSIVE_INTENSITY": SetEmission(M(), null, Mathf.Max(0, Number(value, 1))); break;
                            case "ROUGHNESS": SetFloat(M(), "roughnessFactor", "_Smoothness", Mathf.Clamp(Number(value, 0.5f), 0.05f, 1), invertForSmoothness: true); break;
                            case "METALNESS": SetFloat(M(), "metallicFactor", "_Metallic", Mathf.Clamp01(Number(value, 0))); break;
                            case "OPACITY":
                            {
                                // Opacity makes the material transparent; 0 hides the mesh (an invisible box stays a collider).
                                var m = M();
                                var c = GetColor(m);
                                c.a = Mathf.Clamp01(Number(value, 0));
                                SetColor(m, c, keepAlpha: false);
                                if (c.a < 1) MakeTransparent(m);
                                break;
                            }
                            case "TEXTURE":
                            case "IMAGE_TEXTURE":
                            {
                                var tex = Texture(text);
                                if (tex) SetTexture(M(), "baseColorTexture", "_BaseMap", tex);
                                break;
                            }
                            case "NORMAL_MAP_TEXTURE": { var tex = Texture(text); if (tex) SetTexture(M(), "normalTexture", "_BumpMap", tex); break; }
                            case "ROUGHNESS_MAP_TEXTURE": { var tex = Texture(text); if (tex) SetTexture(M(), "metallicRoughnessTexture", "_MetallicGlossMap", tex); break; }
                            case "IMAGE_EMISSIVE_TEXTURE":
                            case "IMAGE_EMISSIVE_CONATAINED_TEXTURE":
                            case "IMAGE_EMISSIVE_TRANSPARENT_TEXTURE":
                            {
                                // A glowing picture (screens, boards): the image as colour and emission.
                                var tex = Texture(text);
                                if (!tex) break;
                                var m = M();
                                SetTexture(m, "baseColorTexture", "_BaseMap", tex);
                                SetTexture(m, "emissiveTexture", "_EmissionMap", tex);
                                SetEmission(m, Color.white, 1);
                                if (type == "IMAGE_EMISSIVE_TRANSPARENT_TEXTURE") MakeTransparent(m);
                                break;
                            }
                            case "SIZE_X": case "SIZE_Y": case "SIZE_Z":
                            {
                                float f = Number(value, 100) / 100f;
                                adjust = adjust * N.Matrix4x4.CreateScale(type == "SIZE_X" ? f : 1, type == "SIZE_Y" ? f : 1, type == "SIZE_Z" ? f : 1);
                                moved = true;
                                break;
                            }
                            case "ROTATION_X": case "ROTATION_Y": case "ROTATION_Z":
                            {
                                float a = Number(value, 0) * Mathf.Deg2Rad;
                                adjust = adjust * (type == "ROTATION_X" ? N.Matrix4x4.CreateRotationX(a) : type == "ROTATION_Y" ? N.Matrix4x4.CreateRotationY(a) : N.Matrix4x4.CreateRotationZ(a));
                                moved = true;
                                break;
                            }
                            default: unsupported.Add(type); break;
                        }
                    }
                    if (copy)
                    {
                        AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath($"{ctx.GeneratedFolder}/Materials/{ZimensionImporter.SafeName(item.Str("name"))}_{ZimensionImporter.SafeName(materialName)}.mat"));
                        materials[slot] = copy;
                        changedAny = true;
                        materialCount++;
                    }
                }
                if (changedAny) renderer.sharedMaterials = materials;
                if (moved)
                {
                    // In three space the adjustment applies first, in the mesh's own axes: local' = adjust * local
                    // (row-vector). Mirrored into Unity space with the same basis change as everything else.
                    var t = renderer.transform;
                    var local = UnityMath.LocalMatrix(t);
                    var unityAdjust = Coordinates.ThreeToUnity(adjust);
                    UnityMath.ApplyLocal(t, unityAdjust * local);
                }
            }
            foreach (var type in unsupported)
                ctx.Report.Add(ImportReport.Level.Approximated, $"Material customization '{type}' is not applied", "customSettings", item);
        }

        // glTFast materials (shader graph or built-in) use glTF property names; URP Lit and Standard use their own.
        static Color GetColor(Material m)
        {
            if (m.HasProperty("baseColorFactor")) return m.GetColor("baseColorFactor");
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
            return m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
        }

        static void SetColor(Material m, Color c, bool keepAlpha)
        {
            if (keepAlpha) c.a = GetColor(m).a;
            foreach (var p in new[] { "baseColorFactor", "_BaseColor", "_Color" }) if (m.HasProperty(p)) m.SetColor(p, c);
        }

        static void SetFloat(Material m, string gltf, string unity, float value, bool invertForSmoothness = false)
        {
            if (m.HasProperty(gltf)) m.SetFloat(gltf, value);
            if (m.HasProperty(unity)) m.SetFloat(unity, invertForSmoothness ? 1 - value : value);
        }

        static void SetTexture(Material m, string gltf, string unity, Texture2D tex)
        {
            if (m.HasProperty(gltf)) m.SetTexture(gltf, tex);
            if (m.HasProperty(unity)) m.SetTexture(unity, tex);
            if (unity == "_BaseMap" && m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        }

        static void SetEmission(Material m, Color? color, float? intensity)
        {
            var current = m.HasProperty("emissiveFactor") ? m.GetColor("emissiveFactor") : m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black;
            var c = color ?? current;
            if (intensity.HasValue) c *= intensity.Value;
            foreach (var p in new[] { "emissiveFactor", "_EmissionColor" }) if (m.HasProperty(p)) m.SetColor(p, c);
            m.EnableKeyword("_EMISSION");
        }

        static void MakeTransparent(Material m)
        {
            // glTFast shader graphs and URP Lit both read _Surface / the transparent keyword.
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        static bool BuildPrimitive(ZimensionImporter.Context ctx, JsonNode item, ZimensionItem zi)
        {
            var settings = item.Obj("primitiveSettings");
            var shape = MeshFactory.FromSettings(settings);
            var mesh = GetMesh(ctx, shape);
            var body = new GameObject("Shape");
            body.transform.SetParent(zi.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = body.AddComponent<MeshRenderer>();
            zi.modelRoot = body.transform;
            float opacity = Mathf.Clamp01(settings.Float("opacity", 1));
            var emissive = ParseColor(settings.Str("emissive", "#000000"), Color.black) * Mathf.Max(0, settings.Float("emissiveIntensity", 1));
            Texture2D texture = null;
            var textureUrl = settings.Str("textureUrl");
            if (!string.IsNullOrEmpty(textureUrl))
            {
                var entry = ctx.Resolver.Url(textureUrl);
                texture = entry != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(ctx.AssetFor(entry)) : null;
                if (!texture) ctx.Report.Add(ImportReport.Level.Approximated, "Shape texture is missing; the flat colour is used", "primitiveSettings.textureUrl", item);
            }
            renderer.sharedMaterial = GetMaterial(ctx,
                ParseColor(settings.Str("color", "#b8bcc4"), new Color(0.72f, 0.74f, 0.77f)),
                Mathf.Clamp(settings.Float("roughness", 0.6f), 0.05f, 1),
                Mathf.Clamp01(settings.Float("metalness", 0.05f)),
                opacity, emissive, texture,
                doubleSided: shape.Name == "plane",
                transparent: settings.Truthy("transparent") || opacity < 1);
            if (settings.Truthy("emptyCollider") || item.Truthy("useAsColliderMesh")) renderer.enabled = false;
            if (!string.IsNullOrEmpty(settings.Str("shaderKey"))) ctx.Report.Add(ImportReport.Level.Approximated, $"Shape effect shader '{settings.Str("shaderKey")}' is shown with its flat colour", "primitiveSettings.shaderKey", item);
            if (settings.Has("materialIndex") && settings.Num("materialIndex", -1) >= 0) ctx.Report.Add(ImportReport.Level.Approximated, "Library material preset on a shape is shown with its colour", "primitiveSettings.materialIndex", item);
            renderer.shadowCastingMode = item.Flag("castShadow", true) ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return true;
        }

        static void BuildLight(JsonNode item, ZimensionItem zi)
        {
            var s = item.Obj("lightSettings");
            var kind = s.Str("kind", "point");
            var color = ParseColor(s.Str("color", "#ffffff"), Color.white);
            if (kind == "ambient" || kind == "hemisphere")
            {
                // Scene-wide fill light: folded into the ambient term.
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = color * Mathf.Clamp(s.Float("intensity", 1.2f) * 0.5f, 0, 2);
                return;
            }
            var go = new GameObject("Light");
            go.transform.SetParent(zi.transform, false);
            var light = go.AddComponent<Light>();
            light.color = color;
            float intensity = s.Float("intensity", 1.2f);
            float distance = s.Float("distance", 0);
            switch (kind)
            {
                case "spot":
                    light.type = LightType.Spot;
                    light.spotAngle = Mathf.Clamp(s.Float("angle", 45) * 2, 1, 179);
                    light.innerSpotAngle = light.spotAngle * (1 - Mathf.Clamp01(s.Float("penumbra", 0.3f)));
                    go.transform.localRotation = Quaternion.Euler(90, 0, 0); // aims at its local -Y target
                    light.intensity = Mathf.Clamp(intensity / 150f, 0, 50); // candela -> Unity units (approximated)
                    light.range = distance > 0 ? distance : 35;
                    break;
                case "directional":
                    light.type = LightType.Directional;
                    go.transform.localRotation = Quaternion.Euler(90, 0, 0);
                    light.intensity = Mathf.Clamp(intensity * 0.4f, 0, 8);
                    break;
                case "area":
                    light.type = LightType.Point; // realtime area lights are not available in URP forward
                    light.intensity = Mathf.Clamp(intensity / 20f, 0, 20);
                    light.range = 15;
                    break;
                default:
                    light.type = LightType.Point;
                    light.intensity = Mathf.Clamp(intensity / 150f, 0, 50);
                    light.range = distance > 0 ? distance : 30;
                    break;
            }
            light.shadows = s.Truthy("castShadow") ? LightShadows.Soft : LightShadows.None;
            if (s["visible"].IsBool && !s["visible"].AsBool) light.enabled = false;
        }

        static void Colliders(ZimensionImporter.Context ctx, JsonNode item, ZimensionItem zi, string type, bool hasGeometry)
        {
            // Native ProjectSource collision rules.
            var collision = item.Truthy("useAsColliderMesh") ? "object" : item.Str("collision", item.Obj("primitiveSettings").Str("collision", "box"));
            if (type == "primitive")
            {
                var ps = item.Obj("primitiveSettings");
                collision = ps.Truthy("emptyCollider") || collision == "none" || ps.Str("collision") == "none" ? "none" : "object";
            }
            if (type == "terrain") collision = "object";
            zi.collision = collision;
            bool collides = hasGeometry && collision != "none" && item.Flag("active", true) && item.Flag("sceneCollisionActive", true)
                            && !item.Truthy("collectable") && !item.Truthy("weapon") && !item.Obj("fireWeaponSettings").Truthy("enable");
            if (!collides) return;
            var root = zi.modelRoot ? zi.modelRoot : zi.transform;
            if (collision == "box")
            {
                // One box around the model in the item's local space.
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) return;
                var inverse = zi.transform.worldToLocalMatrix;
                Bounds local = default; bool any = false;
                foreach (var r in renderers)
                {
                    var b = r.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
                        var p = inverse.MultiplyPoint3x4(corner);
                        if (!any) { local = new Bounds(p, Vector3.zero); any = true; } else local.Encapsulate(p);
                    }
                }
                var box = zi.gameObject.AddComponent<BoxCollider>();
                box.center = local.center;
                box.size = local.size;
                return;
            }
            // Object collision: real mesh surfaces (never the whole AABB). A collider_mesh_zim node is the only collider when present.
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            bool colliderNode = filters.Any(f => f.name == "collider_mesh_zim");
            int count = 0;
            foreach (var f in filters)
            {
                if (!f.sharedMesh || (colliderNode && f.name != "collider_mesh_zim")) continue;
                var mc = f.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = f.sharedMesh;
                count++;
            }
            foreach (var s in skinned)
            {
                if (!s.sharedMesh || colliderNode) continue;
                var mc = s.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = s.sharedMesh; // bind pose; animated characters collide in their rest pose (approximated)
                count++;
            }
            if (skinned.Length > 0 && !colliderNode)
                ctx.Report.Add(ImportReport.Level.Approximated, "Skinned model collides with its rest-pose mesh", "collision", item);
            if (count > 0 && item.Obj("rigidBodySettings").Truthy("enable"))
                ctx.Report.Add(ImportReport.Level.Unsupported, "Rigid body simulation is not generated yet; the item is static", "rigidBodySettings", item);
        }

        static void Sounds(ZimensionImporter.Context ctx, JsonNode item, ZimensionItem zi)
        {
            var sounds = new List<AudioClip>(); var voices = new List<TextAsset>(); var subtitles = new List<string>();
            foreach (var sound in item["sounds"].Items)
            {
                AudioClip clip = null; TextAsset voice = null;
                if (sound.IsObject)
                {
                    var entry = ctx.Resolver.Sound(sound);
                    clip = entry != null ? AssetDatabase.LoadAssetAtPath<AudioClip>(ctx.AssetFor(entry)) : null;
                    var voiceEntry = ctx.Resolver.Voice(sound);
                    voice = voiceEntry != null ? AssetDatabase.LoadAssetAtPath<TextAsset>(ctx.AssetFor(voiceEntry)) : null;
                }
                sounds.Add(clip); voices.Add(voice); subtitles.Add(sound.IsObject ? sound.Str("text") : "");
            }
            zi.sounds = sounds.ToArray();
            zi.voiceData = voices.ToArray();
            zi.subtitles = subtitles.ToArray();
        }

        static void ReportFeatures(ZimensionImporter.Context ctx, JsonNode item)
        {
            void Unsupported(bool when, string what, string field) { if (when) ctx.Report.Add(ImportReport.Level.Unsupported, what, field, item); }
            Unsupported(item.Obj("vehicleSettings").Truthy("enable"), "Vehicles are not driven by the Unity runtime yet", "vehicleSettings");
            Unsupported(item.Obj("fireWeaponSettings").Truthy("enable"), "Weapons are not supported by the Unity runtime yet", "fireWeaponSettings");
            Unsupported(item.Truthy("pickable"), "Carrying props is not supported by the Unity runtime yet", "pickable");
            Unsupported(item["characterBehaviors"].Count > 0 || item.Obj("characterBehavior").IsObject, "NPC behaviours (follow, patrol, chase) are not run yet", "characterBehaviors");
            Unsupported(!string.IsNullOrEmpty(item.Str("scriptUrl")), "Item JavaScript is not run in Unity", "scriptUrl");
            Unsupported(item.Has("video") || item.Has("videoUrl"), "Video surfaces are not generated yet", "video");
            Unsupported(item.Obj("damageSetting").Truthy("enable"), "Item damage/health is not simulated yet", "damageSetting");
            Unsupported(item["attachments"].Count > 0, "Item attachments are not run yet", "attachments");
            foreach (var card in item["animations"].Items)
                if (card.Str("playmode") == "event" && card.Str("animationSource") != "default" && !item.Truthy("allowAnimations"))
                    ctx.Report.Add(ImportReport.Level.Optional, "Animation cards are present but 'Allow animations' is off", "allowAnimations", item);
        }

        // ---- Shared generated assets -----------------------------------------------------------

        static readonly Dictionary<string, Mesh> s_Meshes = new Dictionary<string, Mesh>();
        static readonly Dictionary<string, Material> s_Materials = new Dictionary<string, Material>();

        static Mesh GetMesh(ZimensionImporter.Context ctx, MeshFactory.Shape shape)
        {
            var key = ctx.GeneratedFolder + "|" + shape.Key;
            if (s_Meshes.TryGetValue(key, out var cached) && cached) return cached;
            var mesh = MeshFactory.Build(shape);
            AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath($"{ctx.GeneratedFolder}/Meshes/{ZimensionImporter.SafeName(shape.Name)}.asset"));
            s_Meshes[key] = mesh;
            return mesh;
        }

        static Material GetMaterial(ZimensionImporter.Context ctx, Color color, float roughness, float metallic, float opacity, Color emission, Texture2D texture, bool doubleSided, bool transparent = false)
        {
            var key = $"{ctx.GeneratedFolder}|{ColorUtility.ToHtmlStringRGBA(color)}|{roughness:0.##}|{metallic:0.##}|{opacity:0.##}|{ColorUtility.ToHtmlStringRGB(emission)}|{(texture ? AssetDatabase.GetAssetPath(texture) : "")}|{doubleSided}|{transparent}";
            if (s_Materials.TryGetValue(key, out var cached) && cached) return cached;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = "Zimension " + ColorUtility.ToHtmlStringRGB(color) };
            color.a = opacity;
            bool urp = shader.name.StartsWith("Universal", StringComparison.Ordinal);
            if (urp)
            {
                m.SetColor("_BaseColor", color);
                if (texture) m.SetTexture("_BaseMap", texture);
                m.SetFloat("_Smoothness", 1 - roughness);
                m.SetFloat("_Metallic", metallic);
                if (doubleSided) m.SetFloat("_Cull", (float)CullMode.Off);
                if (transparent)
                {
                    m.SetFloat("_Surface", 1);
                    m.SetFloat("_Blend", 0);
                    m.SetOverrideTag("RenderType", "Transparent");
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    m.SetFloat("_ZWrite", 0);
                    m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    m.renderQueue = (int)RenderQueue.Transparent;
                }
                if (emission.maxColorComponent > 0)
                {
                    m.SetColor("_EmissionColor", emission);
                    m.EnableKeyword("_EMISSION");
                }
            }
            else
            {
                m.color = color;
                if (texture) m.mainTexture = texture;
                m.SetFloat("_Glossiness", 1 - roughness);
                m.SetFloat("_Metallic", metallic);
                if (emission.maxColorComponent > 0) { m.SetColor("_EmissionColor", emission); m.EnableKeyword("_EMISSION"); }
            }
            AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath($"{ctx.GeneratedFolder}/Materials/Shape_{ColorUtility.ToHtmlStringRGB(color)}.mat"));
            s_Materials[key] = m;
            return m;
        }
    }
}
