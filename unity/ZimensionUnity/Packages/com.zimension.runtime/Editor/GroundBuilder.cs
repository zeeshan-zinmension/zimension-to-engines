using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Zimension.Core;
using Zimension.Unity;

namespace Zimension.Editor
{
    /// <summary>
    /// The world's ground and sky as Unity sees them:
    /// - the ground (world.sizeLimit square) is a Unity Terrain, so it can be sculpted and painted
    ///   with Unity's terrain tools. Sculpted ground (world.groundSculpt: Int16 centimetre chunks)
    ///   becomes its heightmap, the floor preset (floorMaterialIndex, the web's floorMaterials) its
    ///   base Terrain Layer tinted by groundColor, painted layers (groundSculpt.paint) further layers
    ///   with their weights as the splat map, and sculpt water a water plane;
    /// - past the ground's edge, an extension in the ground's material and a floor collider, as the web;
    /// - the sky: envMapIndex 0 is the procedural sky, 9 a solid colour, anything else the
    ///   environment image (EXR/HDR/PNG) as a panoramic skybox that also lights the scene.
    /// </summary>
    internal static class GroundBuilder
    {
        // ---- Floor presets -----------------------------------------------------------------------

        static JsonNode s_Presets;

        static string PackagePath(string relative)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GroundBuilder).Assembly);
            var root = info != null ? info.resolvedPath : Path.GetFullPath("Packages/com.zimension.runtime");
            return Path.Combine(root, relative);
        }

        /// <summary>The web's floorMaterials catalog entry (key, properties, textureRepeat, textures).</summary>
        public static JsonNode FloorPreset(int index)
        {
            if (s_Presets == null)
            {
                try { s_Presets = JsonNode.Parse(File.ReadAllText(PackagePath("Editor/Data/floor-presets.json"))); }
                catch (Exception e) { Debug.LogWarning("[Zimension] Floor presets could not be read: " + e.Message); s_Presets = JsonNode.Parse("[]"); }
            }
            return index >= 0 && index < s_Presets.Count ? s_Presets[index] : JsonNode.Undefined;
        }

        static JsonNode PresetMaps(JsonNode preset) => preset.Obj("textures_1").Count > 0 ? preset.Obj("textures_1") : preset.Obj("textures");

        /// <summary>Texture URLs the ground needs (for staging): the floor preset's and the painted layers'.</summary>
        public static IEnumerable<string> TextureUrls(JsonNode world)
        {
            var indices = new List<int> { (int)world.Num("floorMaterialIndex", 0) };
            foreach (var layer in world.Obj("groundSculpt").Obj("paint")["layers"].Items)
                if (layer["materialIndex"].IsNumber) indices.Add((int)Math.Round(layer["materialIndex"].AsNumber));
            foreach (var index in indices.Distinct())
            {
                var maps = PresetMaps(FloorPreset(index));
                foreach (var key in new[] { "diff", "nor", "rough" }) { var url = maps.Str(key); if (!string.IsNullOrEmpty(url)) yield return url; }
            }
        }

        // ---- Environment -------------------------------------------------------------------------

        static readonly string[] Catalog =
        {
            "scythian_tombs_2_2k.exr", "en_suite_4k.exr", "evening_road_01_puresky_1k.exr", "studio_small_08_2k.exr", "kloppenheim_02_4k.exr",
            "snow_field_4k.exr", "Realistic_hdri-exr_VR360_view_infinite_cosmic_1782693717.exr", "Tech_Noir_Cyberpunk_hdri-exr_futuristic_city_night_from_200634193.exr",
        };

        /// <summary>The environment image URL (applyWorldEnvironment order): environmentMap's file, world.env, then the built-in catalog.</summary>
        public static string EnvironmentUrl(JsonNode world)
        {
            int index = (int)world.Num("envMapIndex", 0);
            if (index == 0 || index == 9) return null;
            var env = world.Obj("environmentMap");
            foreach (var key in new[] { "original", "pngUrl", "url", "downloadUrl", "assetUrl" })
            {
                var v = env[key];
                if (v.IsString && v.AsString.Trim().Length > 0) return v.AsString.Trim();
                if (v.IsObject) { var u = v.Str("url", v.Str("downloadUrl", v.Str("pngUrl"))); if (u.Length > 0) return u; }
            }
            if (!string.IsNullOrEmpty(world.Str("env"))) return world.Str("env");
            if (index > 0 && index <= Catalog.Length) return "https://drxkpji2afm3z.cloudfront.net/backgrounds/" + Catalog[index - 1];
            return null;
        }

        /// <summary>
        /// The bundle entry of the environment image: the URL's, or (older exports that packaged the
        /// file without naming it) the one HDR/EXR image nothing else refers to.
        /// </summary>
        public static string EnvironmentEntry(ZimensionImporter.Context ctx)
        {
            var url = EnvironmentUrl(ctx.World);
            var entry = url != null ? ctx.Resolver.Url(url) : null;
            if (entry != null) return entry;
            int index = (int)ctx.World.Num("envMapIndex", 0);
            if (index == 0 || index == 9) return null;
            var candidates = ctx.Bundle.Names.Where(n => { var e = Path.GetExtension(n).ToLowerInvariant(); return e == ".exr" || e == ".hdr"; }).ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1) return candidates.OrderByDescending(n => ctx.Bundle.Length(n)).First();
            if (url != null) ctx.Report.Add(ImportReport.Level.Approximated, $"The environment image ({Path.GetFileName(url)}) is not in the export; the procedural sky is used. Export the world again to include it.", "environmentMap");
            return null;
        }

        public static void BuildSky(ZimensionImporter.Context ctx, JsonNode world, string folder)
        {
            int index = (int)world.Num("envMapIndex", 0);
            Material sky = null;
            var envColor = CssStyle.ParseColor(world.Str("envColor", "#a1c2d1"), new Color(0.63f, 0.76f, 0.82f));
            if (index == 9)
            {
                RenderSettings.skybox = null;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = envColor;
                var cam = Camera.main;
                if (cam) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = envColor; }
                return;
            }
            var entry = ctx.EnvironmentEntry;
            var texture = entry != null ? AssetDatabase.LoadAssetAtPath<Texture>(ctx.AssetFor(entry)) : null;
            var panoramic = Shader.Find("Skybox/Panoramic");
            if (texture && panoramic)
            {
                sky = new Material(panoramic) { name = "Zimension Sky" };
                sky.SetTexture("_MainTex", texture);
                sky.SetFloat("_Exposure", Mathf.Clamp(world.Float("exposure", 0.5f) * 2, 0.1f, 8));
                var rotation = world.Obj("environmentMap").Float("rotation", world.Float("envRotation", 0));
                sky.SetFloat("_Rotation", -rotation);
            }
            else
            {
                var procedural = Shader.Find("Skybox/Procedural");
                if (procedural)
                {
                    sky = new Material(procedural) { name = "Zimension Procedural Sky" };
                    sky.SetColor("_SkyTint", envColor);
                    sky.SetFloat("_Exposure", Mathf.Clamp(world.Float("exposure", 0.5f) * 2.6f, 0.3f, 8));
                }
            }
            if (sky)
            {
                AssetDatabase.CreateAsset(sky, AssetDatabase.GenerateUniqueAssetPath($"{folder}/Sky.mat"));
                RenderSettings.skybox = sky;
            }
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = Mathf.Clamp(world.Float("envIntensity", 0.2f) * 5, 0.2f, 3);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            var fog = world.Obj("fogSettings");
            if (fog.Truthy("enabled"))
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = CssStyle.ParseColor(fog.Str("color", "#ffeeee"), Color.white);
                RenderSettings.fogDensity = Mathf.Max(0, fog.Float("density", 0.2f)) / 50;
            }
        }

        // ---- Ground ------------------------------------------------------------------------------

        sealed class Grid
        {
            public float Width, Length; public int Nx = 1, Nz = 1, Cells = 32;
            public int SamplesX => Nx * Cells; public int SamplesZ => Nz * Cells;
            public float Hx => Width / SamplesX; public float Hz => Length / SamplesZ;
        }

        static Grid GridFor(float width, float length, JsonNode saved)
        {
            var g = new Grid { Width = width, Length = length };
            var chunks = saved["chunks"];
            bool usable = chunks.IsArray && chunks.Count == 2 && chunks[0].IsNumber && chunks[1].IsNumber && chunks[0].AsNumber > 0 && chunks[1].AsNumber > 0;
            g.Nx = usable ? (int)chunks[0].AsNumber : Math.Max(1, (int)Math.Round(width / 16));
            g.Nz = usable ? (int)chunks[1].AsNumber : Math.Max(1, (int)Math.Round(length / 16));
            return g;
        }

        static bool ChunkKey(string key, out int ix, out int iz)
        {
            ix = iz = 0;
            var parts = key.Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], out ix) && int.TryParse(parts[1], out iz);
        }

        /// <summary>groundSculpt heights: Int16 centimetres per touched chunk, the outer border pinned to 0.</summary>
        static float[][] Heights(JsonNode sculpt, Grid g)
        {
            var chunks = new float[g.Nx * g.Nz][];
            if ((int)sculpt.Num("version", 0) != 1 || (int)sculpt.Num("cells", 0) != g.Cells || !sculpt["heights"].IsObject) return chunks;
            int count = g.Cells * g.Cells;
            foreach (var pair in sculpt["heights"].Members)
            {
                if (!pair.Value.IsString || !ChunkKey(pair.Key, out var ix, out var iz) || ix < 0 || iz < 0 || ix >= g.Nx || iz >= g.Nz) continue;
                byte[] bytes;
                try { bytes = System.Convert.FromBase64String(pair.Value.AsString); } catch { continue; }
                if (bytes.Length != count * 2) continue;
                var data = new float[count];
                for (int i = 0; i < count; i++) data[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8) / 100f;
                chunks[iz * g.Nx + ix] = data;
            }
            for (int index = 0; index < chunks.Length; index++)
            {
                var data = chunks[index]; if (data == null) continue;
                for (int i = 0; i < g.Cells; i++) { if (index % g.Nx == 0) data[i * g.Cells] = 0; if (index < g.Nx) data[i] = 0; }
            }
            return chunks;
        }

        static float Height(float[][] chunks, Grid g, int gx, int gz)
        {
            if (gx <= 0 || gz <= 0 || gx >= g.SamplesX || gz >= g.SamplesZ) return 0;
            int ix = gx / g.Cells, iz = gz / g.Cells;
            var data = chunks[iz * g.Nx + ix];
            return data == null ? 0 : data[(gz - iz * g.Cells) * g.Cells + (gx - ix * g.Cells)];
        }

        static float Bilinear(Func<int, int, float> sample, float gx, float gz)
        {
            int x0 = Mathf.FloorToInt(gx), z0 = Mathf.FloorToInt(gz);
            float fx = gx - x0, fz = gz - z0;
            return Mathf.Lerp(Mathf.Lerp(sample(x0, z0), sample(x0 + 1, z0), fx), Mathf.Lerp(sample(x0, z0 + 1), sample(x0 + 1, z0 + 1), fx), fz);
        }

        sealed class PaintLayer { public bool Grass; public int Material = -1; public string Color = ""; public float Scale = 1, Roughness = float.NaN, Metalness = float.NaN; }

        static Texture2D LoadTexture(ZimensionImporter.Context ctx, string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var entry = ctx.Resolver.Url(url);
            var path = entry != null ? ctx.AssetFor(entry) : null;
            return path != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(path) : null;
        }

        static void MarkNormalMap(Texture2D texture)
        {
            if (!texture) return;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
            if (importer && importer.textureType != TextureImporterType.NormalMap) { importer.textureType = TextureImporterType.NormalMap; importer.SaveAndReimport(); }
        }

        static Texture2D s_White;
        static Texture2D White(string folder)
        {
            if (s_White) return s_White;
            var path = $"{folder}/White.png";
            s_White = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (s_White) return s_White;
            var t = new Texture2D(4, 4); var pixels = Enumerable.Repeat(Color.white, 16).ToArray(); t.SetPixels(pixels); t.Apply();
            File.WriteAllBytes(Path.GetFullPath(path), t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path);
            return s_White = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Color PresetColor(JsonNode preset, Color fallback)
        {
            var c = preset.Obj("properties")["color"];
            if (!c.IsNumber) return fallback;
            int v = (int)c.AsNumber;
            return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        }

        static TerrainLayer Layer(ZimensionImporter.Context ctx, string folder, string name, int preset, Color tint, float roughness, float metalness, Vector2 tile)
        {
            var definition = FloorPreset(preset);
            var maps = PresetMaps(definition);
            var layer = new TerrainLayer { name = name };
            layer.diffuseTexture = LoadTexture(ctx, maps.Str("diff")) ?? White(folder);
            var normal = LoadTexture(ctx, maps.Str("nor"));
            if (normal) { MarkNormalMap(normal); layer.normalMapTexture = normal; }
            layer.tileSize = tile;
            layer.diffuseRemapMin = Vector4.zero;
            layer.diffuseRemapMax = new Vector4(tint.r, tint.g, tint.b, 1);
            layer.smoothness = 1 - Mathf.Clamp01(float.IsNaN(roughness) ? definition.Obj("properties").Float("roughness", 1) : roughness);
            layer.metallic = Mathf.Clamp01(float.IsNaN(metalness) ? definition.Obj("properties").Float("metalness", 0) : metalness);
            if (maps.Count > 0 && !LoadTexture(ctx, maps.Str("diff")))
                ctx.Report.Add(ImportReport.Level.Approximated, $"Ground material '{definition.Str("name")}' textures are not in the export; its colour is used", "floorMaterialIndex");
            AssetDatabase.CreateAsset(layer, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{ZimensionImporter.SafeName(name)}.terrainlayer"));
            return layer;
        }

        /// <summary>The ground, its extension past the edge and the floor collider. Returns the ground object.</summary>
        public static GameObject Build(ZimensionImporter.Context ctx, JsonNode world, string folder)
        {
            float size = Mathf.Clamp(world.Float("sizeLimit", 500), 1, 100000);
            if (!world.Flag("showGround", true))
            {
                var floorOnly = new GameObject("Floor (invisible)");
                var box = floorOnly.AddComponent<BoxCollider>();
                float e = Mathf.Max(10000, size * 4);
                box.size = new Vector3(e, 0.2f, e); box.center = new Vector3(0, -0.1f, 0);
                return floorOnly;
            }
            int presetIndex = (int)world.Num("floorMaterialIndex", 0);
            var preset = FloorPreset(presetIndex);
            var groundColor = CssStyle.ParseColor(world.Str("groundColor", "#b3b3b3"), new Color(0.7f, 0.7f, 0.7f));
            var tint = groundColor * PresetColor(preset, Color.white); tint.a = 1;
            float repeat = preset.Float("textureRepeat", 10) * 2.2f * world.Float("sizeLimit", 150) / 150;
            if (world.Truthy("groundDisplacement")) repeat = 1;
            var tile = new Vector2(size / Mathf.Max(0.01f, repeat), size / Mathf.Max(0.01f, repeat));
            float roughness = world.Has("groundRoughness") ? world.Float("groundRoughness", 1) : float.NaN;
            float metal = world.Has("groundReflectivity") ? world.Float("groundReflectivity", 0) : float.NaN;
            bool water = preset.Truthy("animatedWater");

            // Heights.
            var sculpt = world.Obj("groundSculpt");
            var grid = GridFor(size, size, sculpt);
            var chunks = Heights(sculpt, grid);
            bool sculpted = chunks.Any(c => c != null);
            int resolution = 33;
            if (sculpted) { int need = Mathf.Max(grid.SamplesX, grid.SamplesZ); resolution = need <= 256 ? 257 : need <= 512 ? 513 : need <= 1024 ? 1025 : 2049; }
            float low = 0, high = 0.01f;
            if (sculpted) foreach (var c in chunks) if (c != null) foreach (var h in c) { low = Mathf.Min(low, h); high = Mathf.Max(high, h); }
            float range = Mathf.Max(0.01f, high - low);
            var data = new TerrainData { heightmapResolution = resolution, size = new Vector3(size, range, size) };
            var heights = new float[resolution, resolution];
            for (int row = 0; row < resolution; row++)
                for (int col = 0; col < resolution; col++)
                {
                    // Terrain x runs along Unity +X, which is three -X; z is shared.
                    float u = (float)col / (resolution - 1), v = (float)row / (resolution - 1);
                    float h = sculpted ? Bilinear((x, z) => Height(chunks, grid, x, z), (1 - u) * grid.SamplesX, v * grid.SamplesZ) : 0;
                    heights[row, col] = (h - low) / range;
                }
            data.SetHeights(0, 0, heights);

            // Layers: the floor material, then painted layers (bottom to top).
            var layers = new List<TerrainLayer>();
            if (water) layers.Add(Layer(ctx, folder, "Ground (water)", 0, new Color(0.0f, 0.12f, 0.06f), 0.05f, 0, tile));
            else layers.Add(Layer(ctx, folder, $"Ground ({preset.Str("name", "Floor")})", presetIndex, tint, roughness, metal, tile));
            var paint = sculpt.Obj("paint");
            var paintLayers = new List<PaintLayer>();
            if ((int)paint.Num("version", 0) == 1)
                foreach (var l in paint["layers"].Items)
                {
                    if (paintLayers.Count >= 3 || !l.IsObject) continue;
                    var layer = new PaintLayer();
                    if (l.Str("kind") == "grass") layer.Grass = true;
                    else
                    {
                        if (!l["materialIndex"].IsNumber) continue;
                        layer.Material = (int)Math.Round(l["materialIndex"].AsNumber);
                        if (layer.Material < 0) continue;
                        layer.Color = l.Str("color");
                        if (l["scale"].IsNumber) layer.Scale = Mathf.Clamp((float)l["scale"].AsNumber, 0.05f, 20);
                        if (l["roughness"].IsNumber) layer.Roughness = Mathf.Clamp01((float)l["roughness"].AsNumber);
                        if (l["metalness"].IsNumber) layer.Metalness = Mathf.Clamp01((float)l["metalness"].AsNumber);
                    }
                    paintLayers.Add(layer);
                }
            foreach (var p in paintLayers)
            {
                if (p.Grass) { layers.Add(Layer(ctx, folder, "Painted grass", 0, new Color(0.36f, 0.6f, 0.2f), 1, 0, tile)); continue; }
                var definition = FloorPreset(p.Material);
                var color = !string.IsNullOrEmpty(p.Color) ? CssStyle.ParseColor(p.Color, Color.white) : PresetColor(definition, Color.white);
                float layerRepeat = definition.Float("textureRepeat", 10) * 2.2f * (size / 150) / p.Scale;
                layers.Add(Layer(ctx, folder, $"Painted {definition.Str("name", "layer")}", p.Material, color, p.Roughness, p.Metalness, new Vector2(size / layerRepeat, size / layerRepeat)));
            }
            data.terrainLayers = layers.ToArray();
            if (paintLayers.Count > 0)
            {
                var paintGrid = GridFor(size, size, paint);
                int count = paintGrid.Cells * paintGrid.Cells;
                var weights = new List<byte[][]>();
                for (int k = 0; k < paintLayers.Count; k++) weights.Add(new byte[paintGrid.Nx * paintGrid.Nz][]);
                if (paint["weights"].IsObject)
                    foreach (var pair in paint["weights"].Members)
                    {
                        if (!pair.Value.IsArray || !ChunkKey(pair.Key, out var ix, out var iz) || ix < 0 || iz < 0 || ix >= paintGrid.Nx || iz >= paintGrid.Nz) continue;
                        for (int k = 0; k < Math.Min(pair.Value.Count, paintLayers.Count); k++)
                        {
                            if (!pair.Value[k].IsString) continue;
                            var text = pair.Value[k].AsString;
                            byte[] bytes = null;
                            if (text == "F") bytes = Enumerable.Repeat((byte)255, count).ToArray();
                            else try { bytes = System.Convert.FromBase64String(text); } catch { }
                            if (bytes != null && bytes.Length == count) weights[k][iz * paintGrid.Nx + ix] = bytes;
                        }
                    }
                float Weight(int k, int gx, int gz)
                {
                    int x = Mathf.Clamp(gx, 0, paintGrid.SamplesX - 1), z = Mathf.Clamp(gz, 0, paintGrid.SamplesZ - 1);
                    int ix = x / paintGrid.Cells, iz = z / paintGrid.Cells;
                    var d = weights[k][iz * paintGrid.Nx + ix];
                    return d == null ? 0 : d[(z - iz * paintGrid.Cells) * paintGrid.Cells + (x - ix * paintGrid.Cells)] / 255f;
                }
                int res = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(paintGrid.SamplesX, paintGrid.SamplesZ)), 64, 1024);
                data.alphamapResolution = res;
                var alpha = new float[res, res, layers.Count];
                for (int row = 0; row < res; row++)
                    for (int col = 0; col < res; col++)
                    {
                        float u = (col + 0.5f) / res, v = (row + 0.5f) / res;
                        int gx = Mathf.RoundToInt((1 - u) * paintGrid.SamplesX), gz = Mathf.RoundToInt(v * paintGrid.SamplesZ);
                        // Layers stack bottom to top: each covers what is under it by its weight.
                        float remaining = 1;
                        for (int k = paintLayers.Count - 1; k >= 0; k--)
                        {
                            float w = Weight(k, gx, gz);
                            if (paintLayers[k].Grass) { float t = Mathf.Clamp01((w - 0.08f) / 0.52f); w = t * t * (3 - 2 * t); }
                            alpha[row, col, k + 1] = w * remaining; remaining *= 1 - w;
                        }
                        alpha[row, col, 0] = remaining;
                    }
                data.SetAlphamaps(0, 0, alpha);
            }
            AssetDatabase.CreateAsset(data, AssetDatabase.GenerateUniqueAssetPath($"{folder}/Ground.asset"));
            var ground = Terrain.CreateTerrainGameObject(data);
            ground.name = "Ground";
            ground.transform.position = new Vector3(-size / 2, low, -size / 2);
            var terrain = ground.GetComponent<Terrain>();
            terrain.drawInstanced = true;
            terrain.heightmapPixelError = 3;
            ground.isStatic = true;

            // Sculpt water: one flat surface at its level (open sea past the ground when at or above -0.1).
            var waterNode = sculpt.Obj("water");
            if (waterNode.IsObject)
            {
                float level = Mathf.Clamp(waterNode.Float("level", -1), -50, 200);
                float extent = level >= -0.1f ? 1000 : size;
                var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                plane.name = "Water";
                UnityEngine.Object.DestroyImmediate(plane.GetComponent<Collider>());
                plane.transform.position = new Vector3(0, level, 0);
                plane.transform.localScale = new Vector3(extent / 10, 1, extent / 10);
                plane.GetComponent<Renderer>().sharedMaterial = WaterMaterial(folder, waterNode.Str("style", "toonWater") == "realistic" ? new Color(0, 0.12f, 0.06f, 0.85f) : new Color(0.11f, 0.44f, 0.66f, 0.8f));
            }

            // Past the edge: the ground's material 10 cm down, and a floor at 0 around the terrain.
            float far = Mathf.Max(10000, size * 4);
            var extension = new GameObject("Ground Extension");
            extension.isStatic = true;
            var mesh = new Mesh { name = "Ground Extension" };
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
            float g2 = size / 2, e2 = far / 2;
            void Quad(float x0, float z0, float x1, float z1)
            {
                int b = verts.Count;
                foreach (var (x, z) in new[] { (x0, z0), (x1, z0), (x0, z1), (x1, z1) }) { verts.Add(new Vector3(x, -0.1f, z)); uvs.Add(new Vector2(x / tile.x, z / tile.y)); }
                tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            }
            Quad(-e2, -e2, e2, -g2); Quad(-e2, g2, e2, e2); Quad(-e2, -g2, -g2, g2); Quad(g2, -g2, e2, g2);
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath($"{folder}/Ground Extension.asset"));
            extension.AddComponent<MeshFilter>().sharedMesh = mesh;
            var baseLayer = layers[0];
            extension.AddComponent<MeshRenderer>().sharedMaterial = ExtensionMaterial(folder, baseLayer);
            foreach (var (cx, cz, sx, sz) in new[] { (0f, -(g2 + e2) / 2, far, e2 - g2), (0f, (g2 + e2) / 2, far, e2 - g2), (-(g2 + e2) / 2, 0f, e2 - g2, size), ((g2 + e2) / 2, 0f, e2 - g2, size) })
            {
                var box = extension.AddComponent<BoxCollider>();
                box.center = new Vector3(cx, -0.1f, cz); box.size = new Vector3(sx, 0.2f, sz);
            }
            extension.transform.SetParent(ground.transform, true);
            return ground;
        }

        static Shader LitShader() => Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

        static Material ExtensionMaterial(string folder, TerrainLayer layer)
        {
            var m = new Material(LitShader()) { name = "Ground Extension" };
            var tint = new Color(layer.diffuseRemapMax.x, layer.diffuseRemapMax.y, layer.diffuseRemapMax.z, 1);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", layer.diffuseTexture);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", layer.diffuseTexture);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
            m.color = tint;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", layer.smoothness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", layer.smoothness);
            if (layer.normalMapTexture && m.HasProperty("_BumpMap")) { m.SetTexture("_BumpMap", layer.normalMapTexture); m.EnableKeyword("_NORMALMAP"); }
            AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath($"{folder}/Ground Extension.mat"));
            return m;
        }

        static Material WaterMaterial(string folder, Color color)
        {
            var m = new Material(LitShader()) { name = "Water" };
            if (m.HasProperty("_Surface")) { m.SetFloat("_Surface", 1); m.SetOverrideTag("RenderType", "Transparent"); m.renderQueue = (int)RenderQueue.Transparent; m.SetFloat("_ZWrite", 0); m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha); m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            m.color = color;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.95f);
            AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath($"{folder}/Water.mat"));
            return m;
        }
    }
}
