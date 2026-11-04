using UnityEngine;
using N = System.Numerics;

namespace Zimension.Unity
{
    /// <summary>
    /// Conversions between UnityEngine math types and the System.Numerics types the core uses.
    /// Both sides are already in Unity space (the importer converts three.js data once); these only
    /// change representation. System.Numerics matrices are row-vector (translation in M41..M43),
    /// UnityEngine matrices column-vector, so matrices are transposed.
    /// </summary>
    public static class UnityMath
    {
        public static N.Vector3 ToNumerics(this Vector3 v) => new N.Vector3(v.x, v.y, v.z);
        public static Vector3 ToUnity(this N.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
        public static N.Quaternion ToNumerics(this Quaternion q) => new N.Quaternion(q.x, q.y, q.z, q.w);
        public static Quaternion ToUnity(this N.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);

        public static N.Matrix4x4 ToNumerics(this Matrix4x4 m) => new N.Matrix4x4(
            m.m00, m.m10, m.m20, m.m30,
            m.m01, m.m11, m.m21, m.m31,
            m.m02, m.m12, m.m22, m.m32,
            m.m03, m.m13, m.m23, m.m33);

        public static Matrix4x4 ToUnity(this N.Matrix4x4 m)
        {
            var r = new Matrix4x4();
            r.m00 = m.M11; r.m10 = m.M12; r.m20 = m.M13; r.m30 = m.M14;
            r.m01 = m.M21; r.m11 = m.M22; r.m21 = m.M23; r.m31 = m.M24;
            r.m02 = m.M31; r.m12 = m.M32; r.m22 = m.M33; r.m32 = m.M34;
            r.m03 = m.M41; r.m13 = m.M42; r.m23 = m.M43; r.m33 = m.M44;
            return r;
        }

        /// <summary>A transform's local TRS as a row-vector matrix (relative to its parent).</summary>
        public static N.Matrix4x4 LocalMatrix(Transform t) =>
            Core.Coordinates.Compose(t.localPosition.ToNumerics(), t.localRotation.ToNumerics(), t.localScale.ToNumerics());

        /// <summary>Apply a row-vector matrix as a transform's local TRS.</summary>
        public static void ApplyLocal(Transform t, N.Matrix4x4 m)
        {
            Core.Coordinates.Decompose(m, out var p, out var r, out var s);
            t.localPosition = p.ToUnity();
            t.localRotation = Normalize(r.ToUnity());
            t.localScale = s.ToUnity();
        }

        public static Quaternion Normalize(Quaternion q)
        {
            float l = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return l > 1e-6f ? new Quaternion(q.x / l, q.y / l, q.z / l, q.w / l) : Quaternion.identity;
        }
    }

    /// <summary>A serializable local pose (Unity space), used for authored transition states.</summary>
    [System.Serializable]
    public struct ZimensionPose
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;

        public ZimensionPose(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            this.position = position; this.rotation = rotation; this.scale = scale;
        }

        public static ZimensionPose FromTransform(Transform t) => new ZimensionPose(t.localPosition, t.localRotation, t.localScale);

        public N.Matrix4x4 ToMatrix() => Core.Coordinates.Compose(position.ToNumerics(), UnityMath.Normalize(rotation).ToNumerics(), scale.ToNumerics());

        public static ZimensionPose FromMatrix(N.Matrix4x4 m)
        {
            Core.Coordinates.Decompose(m, out var p, out var r, out var s);
            return new ZimensionPose(p.ToUnity(), UnityMath.Normalize(r.ToUnity()), s.ToUnity());
        }
    }
}
