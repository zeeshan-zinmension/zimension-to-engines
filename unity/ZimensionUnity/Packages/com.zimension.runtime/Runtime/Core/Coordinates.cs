using System;
using System.Numerics;

namespace Zimension.Core
{
    /// <summary>
    /// The one conversion between Zimension (three.js: right-handed, Y up, metres) and Unity
    /// (left-handed, Y up, metres).
    ///
    /// Convention: a change of basis by the reflection S = diag(-1, 1, 1), the same mirror glTFast
    /// applies to glTF nodes (NodeExtension.GetTransform negates the X row/column). For any
    /// transform M in three space the Unity transform is S * M * S. Positions become (-x, y, z),
    /// quaternions (x, -y, -z, w), scales are unchanged. Because glTFast and this class use the
    /// same S, an item placed with <see cref="ThreeToUnity(Matrix4x4)"/> lines up with the model
    /// hierarchy glTFast imports beneath it, with no double conversion.
    ///
    /// Matrix layout: three stores Matrix4.elements column-major. Loading those 16 numbers in
    /// order into System.Numerics.Matrix4x4 (M11, M12, ... M44) yields the same transform in
    /// System.Numerics' row-vector convention (translation in M41..M43), exactly as the native
    /// player loads them into DirectX's XMFLOAT4X4. The core runtime works in that row-vector
    /// convention throughout; the Unity host transposes when it talks to UnityEngine.Matrix4x4.
    /// </summary>
    public static class Coordinates
    {
        /// <summary>three.js Matrix4.elements (column-major, 16 numbers) as a row-vector matrix.</summary>
        public static bool TryMatrixFromThreeElements(JsonNode array, out Matrix4x4 m)
        {
            m = Matrix4x4.Identity;
            if (array == null || !array.IsArray || array.Count != 16) return false;
            var v = new float[16];
            for (int i = 0; i < 16; i++)
            {
                var n = array[i];
                if (!n.IsNumber || !JsonNode.IsFinite(n.AsNumber)) return false;
                v[i] = (float)n.AsNumber;
            }
            m = new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
            return true;
        }

        /// <summary>S * M * S: negate every element that pairs the X axis with Y, Z or W.</summary>
        public static Matrix4x4 ThreeToUnity(Matrix4x4 m)
        {
            m.M12 = -m.M12; m.M13 = -m.M13; m.M14 = -m.M14;
            m.M21 = -m.M21; m.M31 = -m.M31; m.M41 = -m.M41;
            return m;
        }

        /// <summary>The reflection is its own inverse.</summary>
        public static Matrix4x4 UnityToThree(Matrix4x4 m) => ThreeToUnity(m);

        public static Vector3 ThreeToUnityPoint(Vector3 p) => new Vector3(-p.X, p.Y, p.Z);
        public static Vector3 ThreeToUnityDirection(Vector3 d) => new Vector3(-d.X, d.Y, d.Z);
        public static Quaternion ThreeToUnityRotation(Quaternion q) => new Quaternion(q.X, -q.Y, -q.Z, q.W);

        /// <summary>
        /// three.js Euler (radians) to a quaternion in three space. three's default order is
        /// "XYZ": R = Rx * Ry * Rz for column vectors.
        /// </summary>
        public static Quaternion EulerToQuaternion(float x, float y, float z, string order = "XYZ")
        {
            var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, x);
            var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, y);
            var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z);
            // System.Numerics: q1 * q2 rotates by q2 first, then q1 (Hamilton product, column-vector reading),
            // so Rx * Ry * Rz (apply Z, then Y, then X) is qx * qy * qz.
            switch ((order ?? "XYZ").ToUpperInvariant())
            {
                case "YXZ": return qy * qx * qz;
                case "ZXY": return qz * qx * qy;
                case "ZYX": return qz * qy * qx;
                case "YZX": return qy * qz * qx;
                case "XZY": return qx * qz * qy;
                default: return qx * qy * qz;
            }
        }

        /// <summary>
        /// The row-vector matrix scale * rotation * translation (applied in that order), the
        /// composition three's Matrix4.compose produces.
        /// </summary>
        public static Matrix4x4 Compose(Vector3 position, Quaternion rotation, Vector3 scale) =>
            Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);

        /// <summary>Decompose that never fails: a degenerate matrix keeps its translation.</summary>
        public static void Decompose(Matrix4x4 m, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            if (Matrix4x4.Decompose(m, out scale, out rotation, out position)) return;
            position = m.Translation;
            scale = new Vector3(
                new Vector3(m.M11, m.M12, m.M13).Length(),
                new Vector3(m.M21, m.M22, m.M23).Length(),
                new Vector3(m.M31, m.M32, m.M33).Length());
            rotation = Quaternion.Identity;
        }

        /// <summary>Linear TRS interpolation with a shortest-path slerp, as the native transitions use.</summary>
        public static Matrix4x4 Interpolate(Matrix4x4 a, Matrix4x4 b, float t)
        {
            Decompose(a, out var pa, out var ra, out var sa);
            Decompose(b, out var pb, out var rb, out var sb);
            return Compose(Vector3.Lerp(pa, pb, t), Quaternion.Slerp(ra, rb, t), Vector3.Lerp(sa, sb, t));
        }

        public static bool IsFinite(Matrix4x4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    float v = Get(m, r, c);
                    if (float.IsNaN(v) || float.IsInfinity(v)) return false;
                }
            return true;
        }

        public static float Get(Matrix4x4 m, int row, int column)
        {
            switch (row * 4 + column)
            {
                case 0: return m.M11; case 1: return m.M12; case 2: return m.M13; case 3: return m.M14;
                case 4: return m.M21; case 5: return m.M22; case 6: return m.M23; case 7: return m.M24;
                case 8: return m.M31; case 9: return m.M32; case 10: return m.M33; case 11: return m.M34;
                case 12: return m.M41; case 13: return m.M42; case 14: return m.M43; default: return m.M44;
            }
        }

        public static Matrix4x4 InverseOrIdentity(Matrix4x4 m) => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity;
    }
}
