using UnityEngine;

namespace Zimension.Unity
{
    /// <summary>
    /// Rest-pose measurements of a character model, independent of renderer bounds (a skinned mesh's
    /// bounds are not reliable before its first rendered frame, and glTF rigs often carry scaled
    /// armatures): the height of the baked vertices, and the facing derived from the upper arms.
    /// </summary>
    public static class AvatarMeasure
    {
        static string Key(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in name.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>The model's forward (in its parent's space), from left/right upper-arm positions.</summary>
        public static bool Forward(Transform model, out Vector3 forward)
        {
            forward = Vector3.forward;
            Transform left = null, right = null;
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                var k = Key(t.name);
                if (left == null && (k.EndsWith("leftarm") || k.EndsWith("leftupperarm") || k == "lupperarm" || k == "upperarml")) left = t;
                if (right == null && (k.EndsWith("rightarm") || k.EndsWith("rightupperarm") || k == "rupperarm" || k == "upperarmr")) right = t;
            }
            if (!left || !right) return false;
            var parent = model.parent;
            var l = parent ? parent.InverseTransformPoint(left.position) : left.position;
            var r = parent ? parent.InverseTransformPoint(right.position) : right.position;
            var across = r - l; across.y = 0; // the character's right
            if (across.sqrMagnitude < 1e-8f) return false;
            forward = Vector3.Cross(across.normalized, Vector3.up); // left-handed: right x up = forward
            return true;
        }

        /// <summary>Lowest and highest vertex heights in <paramref name="space"/> (baked skinned meshes and static meshes).</summary>
        public static bool VerticalExtent(Transform model, Transform space, out float low, out float high)
        {
            low = float.MaxValue; high = float.MinValue;
            bool any = false;
            var baked = new Mesh();
            var toSpace = space.worldToLocalMatrix;
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr.sharedMesh) continue;
                baked.Clear();
                smr.BakeMesh(baked, true);
                // BakeMesh(useScale: true) already applied the renderer's scale: place it with position and rotation only.
                var placement = toSpace * Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                foreach (var v in baked.vertices) { float y = placement.MultiplyPoint3x4(v).y; low = Mathf.Min(low, y); high = Mathf.Max(high, y); any = true; }
            }
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || !mf.sharedMesh.isReadable) continue;
                var placement = toSpace * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) { float y = placement.MultiplyPoint3x4(v).y; low = Mathf.Min(low, y); high = Mathf.Max(high, y); any = true; }
            }
            if (Application.isPlaying) Object.Destroy(baked); else Object.DestroyImmediate(baked);
            if (!any)
            {
                // Non-readable meshes: fall back to renderer bounds.
                foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                {
                    var b = r.bounds;
                    var a = toSpace.MultiplyPoint3x4(b.min); var c = toSpace.MultiplyPoint3x4(b.max);
                    low = Mathf.Min(low, Mathf.Min(a.y, c.y)); high = Mathf.Max(high, Mathf.Max(a.y, c.y)); any = true;
                }
            }
            return any;
        }
    }
}
