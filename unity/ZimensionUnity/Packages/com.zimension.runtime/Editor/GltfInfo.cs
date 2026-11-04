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

        public static IEnumerable<string> RequiredExtensions(JsonNode gltf)
        {
            foreach (var e in gltf["extensionsRequired"].Items) if (e.IsString) yield return e.AsString;
        }
    }
}
