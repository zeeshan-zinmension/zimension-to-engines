using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zimension.Core;

namespace Zimension.Editor
{
    /// <summary>
    /// Reads a glTF document's JSON (the GLB JSON chunk, or a .gltf file) without importing it:
    /// animation names by index (animation cards and library sources address clips by index) and
    /// the external buffer/image URIs a .gltf needs beside it.
    /// </summary>
    public static class GltfInfo
    {
        public static JsonNode ReadJson(byte[] data)
        {
            if (data == null || data.Length < 20) return JsonNode.Undefined;
            // GLB: magic "glTF", version, length, then chunk 0 (JSON).
            if (data[0] == 0x67 && data[1] == 0x6C && data[2] == 0x54 && data[3] == 0x46)
            {
                int chunkLength = BitConverter.ToInt32(data, 12);
                int chunkType = BitConverter.ToInt32(data, 16);
                if (chunkType != 0x4E4F534A || chunkLength <= 0 || 20 + chunkLength > data.Length) return JsonNode.Undefined;
                var text = Encoding.UTF8.GetString(data, 20, chunkLength).TrimEnd('\0', ' ');
                return JsonNode.TryParse(text, out var glb) ? glb : JsonNode.Undefined;
            }
            var json = Encoding.UTF8.GetString(data);
            return JsonNode.TryParse(json, out var gltf) ? gltf : JsonNode.Undefined;
        }

        public static List<string> AnimationNames(JsonNode gltf)
        {
            var names = new List<string>();
            int index = 0;
            foreach (var a in gltf["animations"].Items)
            {
                var name = a.Str("name");
                names.Add(string.IsNullOrEmpty(name) ? $"animation_{index}" : name);
                index++;
            }
            return names;
        }

        /// <summary>Relative (non-data:) URIs of buffers and images.</summary>
        public static List<string> ExternalUris(JsonNode gltf)
        {
            var uris = new List<string>();
            foreach (var key in new[] { "buffers", "images" })
                foreach (var entry in gltf[key].Items)
                {
                    var uri = entry.Str("uri");
                    if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.Ordinal)) continue;
                    uris.Add(Uri.UnescapeDataString(uri));
                }
            return uris;
        }

        public static bool HasSkin(JsonNode gltf) => gltf["skins"].Count > 0;

        public static bool HasMorphTargets(JsonNode gltf)
        {
            foreach (var mesh in gltf["meshes"].Items)
                foreach (var primitive in mesh["primitives"].Items)
                    if (primitive["targets"].Count > 0) return true;
            return false;
        }

        /// <summary>
        /// Size of the default scene's static meshes as three.js draws them: every node's matrix or TRS
        /// down the hierarchy applied to the POSITION vertices themselves, decoded from the GLB's binary
        /// chunk (float, or KHR_mesh_quantization integers, normalized as three does). <paramref name="exact"/>
        /// is false when some geometry could not be decoded here (Draco or meshopt compression, sparse
        /// or external buffers) and the accessor's stored min/max stood in; <paramref name="note"/> says why.
        /// </summary>
        public static bool SceneSize(JsonNode gltf, byte[] glb, out System.Numerics.Vector3 size, out bool exact, out string note)
        {
            size = default; exact = true; note = "";
            var nodes = gltf["nodes"];
            var scene = gltf["scenes"][gltf["scene"].IsNumber ? (int)gltf["scene"].AsNumber : 0];
            if (!scene.IsObject) return false;
            var bin = BinaryChunk(glb);
            var min = new System.Numerics.Vector3(float.PositiveInfinity);
            var max = new System.Numerics.Vector3(float.NegativeInfinity);
            bool any = false;
            var reasons = new HashSet<string>();
            void Add(System.Numerics.Vector3 p, System.Numerics.Matrix4x4 world)
            {
                var w = System.Numerics.Vector3.Transform(p, world);
                min = System.Numerics.Vector3.Min(min, w); max = System.Numerics.Vector3.Max(max, w);
            }
            void Visit(int index, System.Numerics.Matrix4x4 parent, int depth)
            {
                var node = nodes[index];
                if (!node.IsObject || depth > 256) return;
                var world = LocalMatrix(node) * parent;
                if (node["mesh"].IsNumber && !node["skin"].IsNumber && node.Str("name") != "collider_mesh_zim")
                    foreach (var primitive in gltf["meshes"][(int)node["mesh"].AsNumber]["primitives"].Items)
                    {
                        var accessor = gltf["accessors"][primitive["attributes"]["POSITION"].IsNumber ? (int)primitive["attributes"]["POSITION"].AsNumber : -1];
                        if (!accessor.IsObject) continue;
                        string why = null;
                        if (primitive.Obj("extensions").Has("KHR_draco_mesh_compression")) why = "Draco";
                        else if (ReadPositions(gltf, accessor, bin, p => Add(p, world), out var reason)) { any = true; continue; }
                        else why = reason;
                        reasons.Add(why);
                        if (!AccessorBounds(accessor, out var lo, out var hi)) continue;
                        for (int c = 0; c < 8; c++)
                            Add(new System.Numerics.Vector3((c & 1) != 0 ? hi.X : lo.X, (c & 2) != 0 ? hi.Y : lo.Y, (c & 4) != 0 ? hi.Z : lo.Z), world);
                        any = true;
                    }
                foreach (var child in node["children"].Items)
                    if (child.IsNumber) Visit((int)child.AsNumber, world, depth + 1);
            }
            foreach (var root in scene["nodes"].Items)
                if (root.IsNumber) Visit((int)root.AsNumber, System.Numerics.Matrix4x4.Identity, 0);
            if (!any) return false;
            exact = reasons.Count == 0;
            note = string.Join(", ", reasons);
            size = max - min;
            return JsonNode.IsFinite(size.X) && JsonNode.IsFinite(size.Y) && JsonNode.IsFinite(size.Z);
        }

        /// <summary>The GLB's binary chunk (buffer 0), or null for a .gltf / no chunk.</summary>
        static byte[] BinaryChunk(byte[] data)
        {
            if (data == null || data.Length < 20 || data[0] != 0x67 || data[1] != 0x6C || data[2] != 0x54 || data[3] != 0x46) return null;
            int offset = 12;
            while (offset + 8 <= data.Length)
            {
                int length = BitConverter.ToInt32(data, offset), type = BitConverter.ToInt32(data, offset + 4);
                if (length < 0 || offset + 8 + length > data.Length) return null;
                if (type == 0x004E4942) { var bin = new byte[length]; Buffer.BlockCopy(data, offset + 8, bin, 0, length); return bin; }
                offset += 8 + ((length + 3) & ~3);
            }
            return null;
        }

        /// <summary>Every POSITION of an accessor, decoded as three.js does (GLTFLoader / getNormalizedComponentScale).</summary>
        static bool ReadPositions(JsonNode gltf, JsonNode accessor, byte[] bin, Action<System.Numerics.Vector3> each, out string reason)
        {
            reason = null;
            if (accessor.Has("sparse")) { reason = "sparse accessor"; return false; }
            if (!accessor["bufferView"].IsNumber) { reason = "no buffer view"; return false; }
            var view = gltf["bufferViews"][(int)accessor["bufferView"].AsNumber];
            if (view.Obj("extensions").Has("EXT_meshopt_compression") || view.Obj("extensions").Has("KHR_meshopt_compression")) { reason = "meshopt"; return false; }
            var buffer = gltf["buffers"][(int)view.Num("buffer", 0)];
            if (bin == null || (int)view.Num("buffer", 0) != 0 || buffer.Has("uri")) { reason = "external buffer"; return false; }
            int componentType = (int)accessor.Num("componentType", 5126);
            int componentSize = componentType == 5126 ? 4 : componentType == 5122 || componentType == 5123 ? 2 : componentType == 5120 || componentType == 5121 ? 1 : 0;
            if (componentSize == 0 || accessor.Str("type") != "VEC3") { reason = "unusual position format"; return false; }
            int count = (int)accessor.Num("count", 0);
            int start = (int)view.Num("byteOffset", 0) + (int)accessor.Num("byteOffset", 0);
            int stride = view["byteStride"].IsNumber ? (int)view["byteStride"].AsNumber : componentSize * 3;
            if (count <= 0 || start < 0 || stride <= 0 || start + (long)stride * (count - 1) + componentSize * 3 > bin.Length) { reason = "buffer out of range"; return false; }
            bool normalized = accessor["normalized"].IsBool && accessor["normalized"].AsBool;
            float Read(int at)
            {
                switch (componentType)
                {
                    case 5126: return BitConverter.ToSingle(bin, at);
                    case 5122: { float v = BitConverter.ToInt16(bin, at); return normalized ? Math.Max(v / 32767f, -1f) : v; }
                    case 5123: { float v = BitConverter.ToUInt16(bin, at); return normalized ? v / 65535f : v; }
                    case 5120: { float v = (sbyte)bin[at]; return normalized ? Math.Max(v / 127f, -1f) : v; }
                    default: { float v = bin[at]; return normalized ? v / 255f : v; }
                }
            }
            for (int i = 0; i < count; i++)
            {
                int at = start + i * stride;
                each(new System.Numerics.Vector3(Read(at), Read(at + componentSize), Read(at + componentSize * 2)));
            }
            return true;
        }

        /// <summary>A node's local transform in the row-vector convention (v * M).</summary>
        static System.Numerics.Matrix4x4 LocalMatrix(JsonNode node)
        {
            var m = node["matrix"];
            if (m.IsArray && m.Count == 16)
            {
                var v = new float[16];
                for (int i = 0; i < 16; i++) v[i] = m[i].IsNumber ? (float)m[i].AsNumber : (i % 5 == 0 ? 1 : 0);
                // glTF matrices are column-major; read sequentially they are the row-vector form.
                return new System.Numerics.Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
            }
            var t = node["translation"]; var r = node["rotation"]; var s = node["scale"];
            var translation = t.Count == 3 ? new System.Numerics.Vector3((float)t[0].AsNumber, (float)t[1].AsNumber, (float)t[2].AsNumber) : System.Numerics.Vector3.Zero;
            var rotation = r.Count == 4 ? System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion((float)r[0].AsNumber, (float)r[1].AsNumber, (float)r[2].AsNumber, (float)r[3].AsNumber)) : System.Numerics.Quaternion.Identity;
            var scale = s.Count == 3 ? new System.Numerics.Vector3((float)s[0].AsNumber, (float)s[1].AsNumber, (float)s[2].AsNumber) : System.Numerics.Vector3.One;
            return System.Numerics.Matrix4x4.CreateScale(scale) * System.Numerics.Matrix4x4.CreateFromQuaternion(rotation) * System.Numerics.Matrix4x4.CreateTranslation(translation);
        }

        static bool AccessorBounds(JsonNode accessor, out System.Numerics.Vector3 lo, out System.Numerics.Vector3 hi)
        {
            lo = hi = default;
            var min = accessor["min"]; var max = accessor["max"];
            if (!accessor.IsObject || min.Count < 3 || max.Count < 3) return false;
            float divisor = 1;
            if (accessor["normalized"].IsBool && accessor["normalized"].AsBool)
                switch (accessor["componentType"].IsNumber ? (int)accessor["componentType"].AsNumber : 0)
                {
                    case 5120: divisor = 127; break;   // BYTE
                    case 5121: divisor = 255; break;   // UNSIGNED_BYTE
                    case 5122: divisor = 32767; break; // SHORT
                    case 5123: divisor = 65535; break; // UNSIGNED_SHORT
                }
            float F(JsonNode n) => Math.Max((float)n.AsNumber / divisor, divisor > 1 ? -1f : float.NegativeInfinity);
            lo = new System.Numerics.Vector3(F(min[0]), F(min[1]), F(min[2]));
            hi = new System.Numerics.Vector3(F(max[0]), F(max[1]), F(max[2]));
            return true;
        }

        public static IEnumerable<string> RequiredExtensions(JsonNode gltf)
        {
            foreach (var e in gltf["extensionsRequired"].Items) if (e.IsString) yield return e.AsString;
        }
    }
}
