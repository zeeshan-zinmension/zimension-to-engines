using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zimension.Editor
{
    /// <summary>
    /// Unity's own retargeting for Zimension characters: a Humanoid Avatar is built for each character
    /// skeleton from its bone names (Mixamo / Ready Player Me, Tripo, Unreal and Blender naming), and
    /// generic library clips are baked once into muscle-space (Humanoid) clips by sampling them on
    /// their own skeleton. Any humanoid character then plays any library clip with its proportions and
    /// rest pose respected, as Mecanim retargeting does for FBX humanoids. Characters whose skeleton
    /// cannot be mapped keep the name-bound generic clips (<see cref="ClipRetarget"/>).
    /// </summary>
    internal static class HumanoidRetarget
    {
        static string Key(string name) => ClipRetarget.BoneKey(name).Replace(".", "").Replace("-", "");

        /// <summary>Humanoid bone to its candidate (normalized) names, in preference order.</summary>
        static Dictionary<string, string[]> Candidates(HashSet<string> keys)
        {
            // Spine chains differ: Mixamo/RPM Spine, Spine1, Spine2; Tripo Waist, Spine01, Spine02; Unreal spine_01..03.
            string[] spine, chest, upper;
            if (keys.Contains("waist")) { spine = new[] { "waist" }; chest = new[] { "spine01" }; upper = new[] { "spine02" }; }
            else if (keys.Contains("spine03")) { spine = new[] { "spine01" }; chest = new[] { "spine02" }; upper = new[] { "spine03" }; }
            else { spine = new[] { "spine", "spine01", "spine0" }; chest = new[] { "spine1", "chest", "spine02" }; upper = new[] { "spine2", "upperchest", "spine03" }; }
            string[] Side(string side, params string[] forms) => forms.Select(f => f.Replace("{s}", side)).ToArray();
            var map = new Dictionary<string, string[]>
            {
                ["Hips"] = new[] { "hips", "hip", "pelvis" },
                ["Spine"] = spine, ["Chest"] = chest, ["UpperChest"] = upper,
                ["Neck"] = new[] { "neck", "necktwist01", "neck01", "neck1" },
                ["Head"] = new[] { "head" },
            };
            foreach (var (side, s) in new[] { ("Left", "l"), ("Right", "r") })
            {
                var word = side.ToLowerInvariant();
                map[$"{side}Shoulder"] = Side(s, $"{word}shoulder", "{s}clavicle", "clavicle{s}", "shoulder{s}");
                map[$"{side}UpperArm"] = Side(s, $"{word}arm", $"{word}upperarm", "{s}upperarm", "upperarm{s}");
                map[$"{side}LowerArm"] = Side(s, $"{word}forearm", $"{word}lowerarm", "{s}forearm", "lowerarm{s}", "forearm{s}");
                map[$"{side}Hand"] = Side(s, $"{word}hand", "{s}hand", "hand{s}");
                map[$"{side}UpperLeg"] = Side(s, $"{word}upleg", $"{word}upperleg", $"{word}thigh", "{s}thigh", "thigh{s}");
                map[$"{side}LowerLeg"] = Side(s, $"{word}leg", $"{word}lowerleg", $"{word}calf", "{s}calf", "calf{s}", "shin{s}");
                map[$"{side}Foot"] = Side(s, $"{word}foot", "{s}foot", "foot{s}");
                map[$"{side}Toes"] = Side(s, $"{word}toebase", $"{word}toes", "{s}toebase", "ball{s}", "toe{s}", "toes{s}");
            }
            return map;
        }

        static readonly string[] Required =
        {
            "Hips", "Spine", "Head", "LeftUpperArm", "LeftLowerArm", "LeftHand", "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "RightUpperLeg", "RightLowerLeg", "RightFoot",
        };

        /// <summary>A Humanoid Avatar for the skeleton under <paramref name="root"/>, in its current (rest) pose; null when it does not map.</summary>
        public static Avatar BuildAvatar(Transform root, out string problem)
        {
            problem = null;
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var bones = new HashSet<Transform>(skinned.SelectMany(s => s.bones ?? new Transform[0]).Where(b => b));
            if (bones.Count < 10) { problem = "no skeleton"; return null; }
            var byKey = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!bones.Contains(t) && !t.GetComponentsInChildren<Transform>(true).Any(bones.Contains)) continue;
                var k = Key(t.name);
                if (k.Length > 0 && !byKey.ContainsKey(k)) byKey[k] = t;
            }
            var human = new List<HumanBone>();
            var used = new HashSet<Transform>();
            foreach (var pair in Candidates(new HashSet<string>(byKey.Keys)))
            {
                foreach (var candidate in pair.Value)
                {
                    if (string.IsNullOrEmpty(candidate) || !byKey.TryGetValue(candidate, out var t) || used.Contains(t)) continue;
                    used.Add(t);
                    human.Add(new HumanBone { humanName = HumanTrait.BoneName.First(n => n.Replace(" ", "") == pair.Key), boneName = t.name, limit = new HumanLimit { useDefaultValues = true } });
                    break;
                }
            }
            var mapped = new HashSet<string>(human.Select(h => h.humanName.Replace(" ", "")));
            var missing = Required.Where(r => !mapped.Contains(r)).ToList();
            if (missing.Count > 0) { problem = "missing " + string.Join(", ", missing); return null; }
            // Unity needs the chain in order: an UpperChest without a Chest is dropped.
            if (!mapped.Contains("Chest")) human.RemoveAll(h => h.humanName.Replace(" ", "") == "UpperChest");
            // Unity's Avatar needs every skeleton name to be unique: duplicates (common in glTF) make it fail.
            var all = root.GetComponentsInChildren<Transform>(true);
            var names = new HashSet<string>();
            foreach (var t in all) if (!names.Add(t.name)) { var b = human.FirstOrDefault(h => h.boneName == t.name); if (b.boneName != null) { problem = $"bone name '{t.name}' is used twice"; return null; } }
            var seen = new HashSet<string>();
            var skeleton = all.Where(t => seen.Add(t.name)).Select(t => new SkeletonBone
            {
                name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
            }).ToArray();
            var description = new HumanDescription
            {
                human = human.ToArray(), skeleton = skeleton,
                upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0, hasTranslationDoF = false,
            };
            var avatar = AvatarBuilder.BuildHumanAvatar(root.gameObject, description);
            if (!avatar || !avatar.isValid || !avatar.isHuman) { problem = "Unity rejected the bone mapping"; if (avatar) Object.DestroyImmediate(avatar); return null; }
            avatar.name = root.name + " Avatar";
            return avatar;
        }

        // ---- Clip baking -------------------------------------------------------------------------

        static readonly Dictionary<AnimationClip, AnimationClip> s_Humanoid = new Dictionary<AnimationClip, AnimationClip>();
        static readonly Dictionary<string, (GameObject rig, Avatar avatar)> s_Sources = new Dictionary<string, (GameObject, Avatar)>();

        /// <summary>Muscle curve name in a clip (finger muscles are written "LeftHand.Index.1 Stretched").</summary>
        static string CurveName(string muscle)
        {
            foreach (var side in new[] { "Left", "Right" })
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    var prefix = $"{side} {finger} ";
                    if (!muscle.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rest = muscle.Substring(prefix.Length);
                    return rest.StartsWith("Spread", StringComparison.Ordinal) ? $"{side}Hand.{finger}.Spread" : $"{side}Hand.{finger}.{rest}";
                }
            return muscle;
        }

        /// <summary>The source skeleton a library clip was authored on (its own glTF), with its Humanoid Avatar.</summary>
        static bool Source(AnimationClip clip, string folder, out GameObject rig, out Avatar avatar)
        {
            rig = null; avatar = null;
            var path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path)) return false;
            if (s_Sources.TryGetValue(path, out var cached) && cached.rig) { rig = cached.rig; avatar = cached.avatar; return avatar; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!prefab) return false;
            rig = (GameObject)Object.Instantiate(prefab);
            rig.hideFlags = HideFlags.HideAndDontSave;
            rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            avatar = BuildAvatar(rig.transform, out _);
            if (avatar) AssetDatabase.CreateAsset(avatar, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{ZimensionImporter.SafeName(prefab.name)} Source Avatar.asset"));
            s_Sources[path] = (rig, avatar);
            return avatar;
        }

        /// <summary>The clip as a Humanoid (muscle) clip, baked once and saved; null when its source skeleton is not humanoid.</summary>
        public static AnimationClip Humanoid(AnimationClip clip, string folder)
        {
            if (!clip) return null;
            if (clip.humanMotion) return clip;
            if (s_Humanoid.TryGetValue(clip, out var done)) return done;
            s_Humanoid[clip] = null;
            ImportStage.Mark("humanoid clip " + clip.name);
            if (!Source(clip, folder, out var rig, out var avatar)) return null;
            var handler = new HumanPoseHandler(avatar, rig.transform);
            var pose = new HumanPose();
            int frames = Mathf.Clamp(Mathf.CeilToInt(clip.length * 30), 1, 9000);
            var muscles = HumanTrait.MuscleName;
            var curves = muscles.Select(_ => new AnimationCurve()).ToArray();
            var root = Enumerable.Range(0, 7).Select(_ => new AnimationCurve()).ToArray();
            var restPosition = rig.transform.position; var restRotation = rig.transform.rotation;
            for (int f = 0; f <= frames; f++)
            {
                float t = clip.length * f / frames;
                clip.SampleAnimation(rig, t);
                rig.transform.SetPositionAndRotation(restPosition, restRotation); // the clip never moves the model root
                handler.GetHumanPose(ref pose);
                for (int m = 0; m < muscles.Length && m < pose.muscles.Length; m++) curves[m].AddKey(t, pose.muscles[m]);
                root[0].AddKey(t, pose.bodyPosition.x); root[1].AddKey(t, pose.bodyPosition.y); root[2].AddKey(t, pose.bodyPosition.z);
                root[3].AddKey(t, pose.bodyRotation.x); root[4].AddKey(t, pose.bodyRotation.y); root[5].AddKey(t, pose.bodyRotation.z); root[6].AddKey(t, pose.bodyRotation.w);
            }
            handler.Dispose();
            var result = new AnimationClip { name = clip.name, frameRate = 30 };
            for (int m = 0; m < muscles.Length; m++)
                AnimationUtility.SetEditorCurve(result, EditorCurveBinding.FloatCurve("", typeof(Animator), CurveName(muscles[m])), curves[m]);
            var rootNames = new[] { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
            for (int k = 0; k < 7; k++) AnimationUtility.SetEditorCurve(result, EditorCurveBinding.FloatCurve("", typeof(Animator), rootNames[k]), root[k]);
            var source = AnimationUtility.GetAnimationClipSettings(clip);
            var settings = AnimationUtility.GetAnimationClipSettings(result);
            settings.loopTime = source.loopTime;
            // In place: the controller moves the character, the clip only animates the body.
            settings.loopBlendOrientation = true; settings.loopBlendPositionY = true; settings.loopBlendPositionXZ = true;
            settings.keepOriginalOrientation = true; settings.keepOriginalPositionY = true; settings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings(result, settings);
            AssetDatabase.CreateAsset(result, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{ZimensionImporter.SafeName(clip.name)} (Humanoid).anim"));
            s_Humanoid[clip] = result;
            return result;
        }

        /// <summary>Remove the hidden source rigs used for baking (end of an import).</summary>
        public static void Cleanup()
        {
            foreach (var s in s_Sources.Values) if (s.rig) Object.DestroyImmediate(s.rig);
            s_Sources.Clear();
            s_Humanoid.Clear();
        }

        /// <summary>
        /// Make a character humanoid: build its Avatar (saved under <paramref name="folder"/>), put it on
        /// the model's Animator and return true; the caller then uses <see cref="Humanoid"/> clips.
        /// </summary>
        public static bool MakeHumanoid(ZimensionImporter.Context ctx, GameObject model, string folder, Zimension.Core.JsonNode owner)
        {
            if (ctx.SkipHumanoid) return false;
            ImportStage.Mark("humanoid avatar " + model.name);
            var avatar = BuildAvatar(model.transform, out var problem);
            if (!avatar)
            {
                ctx.Report.Add(ImportReport.Level.Approximated, $"Character '{model.name}' is not a standard humanoid skeleton ({problem}); its animations are bound by bone name", "animations", owner);
                return false;
            }
            var path = $"{folder}/{ZimensionImporter.SafeName(model.name)} Avatar.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Avatar>(path);
            if (existing) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(avatar, path);
            var animator = model.GetComponent<Animator>() ?? model.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            return true;
        }

        public static string Folder(ZimensionImporter.Context ctx)
        {
            var folder = $"{ctx.GeneratedFolder}/Animation/Humanoid";
            if (!AssetDatabase.IsValidFolder($"{ctx.GeneratedFolder}/Animation")) AssetDatabase.CreateFolder(ctx.GeneratedFolder, "Animation");
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder($"{ctx.GeneratedFolder}/Animation", "Humanoid");
            return folder;
        }
    }
}
