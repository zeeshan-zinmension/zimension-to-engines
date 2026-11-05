using UnityEditor;
using UnityEngine;

namespace Zimension.Editor
{
    /// <summary>
    /// Import settings for Zimension source assets, applied the first time each file is imported
    /// (later changes made in the Inspector are kept):
    /// - glTF/GLB (glTFast importer): Mecanim animation clips (Playables-compatible, not legacy),
    ///   original node names (lip sync, retargeting and animation paths rely on them);
    /// - FBX animation sources: Generic rig, no root-motion baking;
    /// - audio: decompressed on load for short clips, streamed for long music;
    /// - UI images: uncompressed sprites with no mipmaps.
    /// </summary>
    public sealed class ZimensionAssetPostprocessor : AssetPostprocessor
    {
        public const string SourceRoot = "Assets/Zimension/SourceAssets/";

        static bool Ours(string path) => path.Replace('\\', '/').StartsWith(SourceRoot, System.StringComparison.Ordinal);

        void OnPreprocessAsset()
        {
            if (!Ours(assetPath) || !assetImporter.importSettingsMissing) return;
            var lower = assetPath.ToLowerInvariant();
            if (!lower.EndsWith(".glb") && !lower.EndsWith(".gltf")) return;
            ConfigureGltf(assetImporter);
        }

        /// <summary>Set glTFast importer fields by serialized name, so this package does not depend on glTFast's API.</summary>
        public static bool ConfigureGltf(AssetImporter importer)
        {
            if (importer == null) return false;
            var so = new SerializedObject(importer);
            bool changed = false;
            var animation = so.FindProperty("importSettings.animationMethod");
            if (animation != null && animation.propertyType == SerializedPropertyType.Enum && animation.intValue != 2)
            {
                animation.intValue = 2; // AnimationMethod.Mecanim
                changed = true;
            }
            var names = so.FindProperty("importSettings.nodeNameMethod");
            if (names != null && names.propertyType == SerializedPropertyType.Enum && names.intValue != 0)
            {
                names.intValue = 0; // NameImportMethod.Original
                changed = true;
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            return changed;
        }

        void OnPreprocessModel()
        {
            if (!Ours(assetPath) || !assetImporter.importSettingsMissing) return;
            var model = (ModelImporter)assetImporter;
            model.animationType = ModelImporterAnimationType.Generic;
            model.importAnimation = true;
            model.importBlendShapes = true;
            model.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        }

        void OnPreprocessAudio()
        {
            if (!Ours(assetPath) || !assetImporter.importSettingsMissing) return;
            var audio = (AudioImporter)assetImporter;
            var settings = audio.defaultSampleSettings;
            long bytes = new System.IO.FileInfo(assetPath).Length;
            settings.loadType = bytes > 3 * 1024 * 1024 ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
            audio.defaultSampleSettings = settings;
        }

        void OnPreprocessTexture()
        {
            if (!Ours(assetPath) || !assetImporter.importSettingsMissing) return;
            var lower = assetPath.ToLowerInvariant();
            if (lower.EndsWith(".exr") || lower.EndsWith(".hdr"))
            {
                // Environment panoramas: keep their detail, wrap horizontally for the skybox seam.
                var hdr = (TextureImporter)assetImporter;
                hdr.maxTextureSize = 8192;
                hdr.wrapModeU = TextureWrapMode.Repeat;
                hdr.wrapModeV = TextureWrapMode.Clamp;
                hdr.npotScale = TextureImporterNPOTScale.None;
                hdr.textureCompression = TextureImporterCompression.CompressedHQ;
                return;
            }
            if (!assetPath.Replace('\\', '/').Contains("/UI/")) return;
            var texture = (TextureImporter)assetImporter;
            texture.textureType = TextureImporterType.Default;
            texture.mipmapEnabled = false;
            texture.alphaIsTransparency = true;
            texture.textureCompression = TextureImporterCompression.Uncompressed;
            texture.npotScale = TextureImporterNPOTScale.None;
        }
    }
}
