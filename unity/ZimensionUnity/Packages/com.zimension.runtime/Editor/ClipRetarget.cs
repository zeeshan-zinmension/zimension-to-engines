using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Zimension.Editor
{
    /// <summary>
    /// Binds a library clip (Mixamo, Ready Player Me, Zimension rigs, character animation sources)
    /// to another model's hierarchy by bone name, producing a Generic clip with that model's own
    /// transform paths. Names are matched after removing rig prefixes ("mixamorig:", "mixamorig1:",
    /// "Armature/"), case-insensitively. Bone translations other than the hips are dropped (bone
    /// lengths differ between rigs); rest-pose rotation differences between rig families are not
    /// corrected (the web's per-profile retarget offsets are approximated, and reported).
    /// </summary>
    public static class ClipRetarget
    {
        static readonly Regex Prefix = new Regex(@"^(mixamorig\d*[:_]?|armature[:_|/]|def[-_]|bip\d*[ _]?)", RegexOptions.IgnoreCase);

        public static string BoneKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var leaf = name;
            int slash = leaf.LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            string previous;
            do { previous = leaf; leaf = Prefix.Replace(leaf, ""); } while (leaf != previous && leaf.Length > 0);
            return leaf.Replace(" ", "").Replace("_", "").ToLowerInvariant();
        }

        static void Collect(Transform root, Transform t, string path, Dictionary<string, string> map)
        {
            var key = BoneKey(t.name);
            if (t != root && key.Length > 0 && !map.ContainsKey(key)) map[key] = path;
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                Collect(root, child, path.Length == 0 ? child.name : path + "/" + child.name, map);
            }
        }

        static bool PathExists(Transform root, string path) => path.Length == 0 || root.Find(path) != null;

        /// <summary>
        /// The clip bound to <paramref name="targetRoot"/>'s hierarchy, or the source itself when it
        /// already binds; null when too few curves can be bound (an incompatible rig).
        /// </summary>
        public static AnimationClip BindTo(AnimationClip source, Transform targetRoot, out float coverage)
        {
            coverage = 0;
            if (!source || !targetRoot) return null;
            var bindings = AnimationUtility.GetCurveBindings(source);
            if (bindings.Length == 0) return null;
            int direct = 0;
            foreach (var b in bindings) if (PathExists(targetRoot, b.path)) direct++;
            if (direct == bindings.Length) { coverage = 1; return source; }
            var map = new Dictionary<string, string>();
            Collect(targetRoot, targetRoot, "", map);
            var clip = new AnimationClip { name = source.name, frameRate = source.frameRate, legacy = false };
            int bound = 0;
            string hipsKey = "hips";
            foreach (var b in bindings)
            {
                string path = b.path;
                if (!PathExists(targetRoot, path))
                {
                    var key = BoneKey(path);
                    if (!map.TryGetValue(key, out path)) continue;
                    // Bone lengths differ between rigs: keep translation on the hips only.
                    if (b.propertyName.StartsWith("m_LocalPosition") && key != hipsKey) continue;
                    if (b.propertyName.StartsWith("m_LocalScale")) continue;
                }
                var curve = AnimationUtility.GetEditorCurve(source, b);
                if (curve == null) continue;
                var target = new EditorCurveBinding { path = path, type = b.type, propertyName = b.propertyName };
                AnimationUtility.SetEditorCurve(clip, target, curve);
                bound++;
            }
            coverage = (float)bound / bindings.Length;
            if (coverage < 0.3f) { Object.DestroyImmediate(clip); return null; }
            var settings = AnimationUtility.GetAnimationClipSettings(source);
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            clip.EnsureQuaternionContinuity();
            return clip;
        }
    }
}
