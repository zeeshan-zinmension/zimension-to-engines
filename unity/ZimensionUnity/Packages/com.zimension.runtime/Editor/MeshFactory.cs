using System.Collections.Generic;
using UnityEngine;
using Zimension.Core;

namespace Zimension.Editor
{
    /// <summary>
    /// Zimension "Basics" shapes (basicsGeometry.js, primitiveSettings.shape with its dimensions and
    /// defaults) as Unity meshes. Geometry is built in three.js space with three's conventions
    /// (centred; plane flat on XZ facing +Y; torus in the XY plane), then mirrored into Unity space
    /// (x → -x with the triangle winding reversed) so asymmetric shapes such as the wedge keep their
    /// orientation under the same basis change as everything else.
    /// </summary>
    public static class MeshFactory
    {
        sealed class Builder
        {
            public readonly List<Vector3> P = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int> T = new List<int>();
            public int Add(Vector3 p, Vector3 n, Vector2 uv) { P.Add(p); N.Add(n.normalized); UV.Add(uv); return P.Count - 1; }
            public void Tri(int a, int b, int c) { T.Add(a); T.Add(b); T.Add(c); }
            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
            {
                int i0 = Add(a, n, new Vector2(0, 0)), i1 = Add(b, n, new Vector2(1, 0)), i2 = Add(c, n, new Vector2(1, 1)), i3 = Add(d, n, new Vector2(0, 1));
                Tri(i0, i1, i2); Tri(i0, i2, i3);
            }
        }

        public struct Shape
        {
            public string Name;
            public float Width, Height, Depth, Radius, Tube;
            public int Segments;
            public string Key => $"{Name}_{Width:0.###}_{Height:0.###}_{Depth:0.###}_{Radius:0.###}_{Tube:0.###}_{Segments}";
        }

        static float Pick(JsonNode dims, string key, float fallback)
        {
            float v = dims.Float(key, float.NaN);
            return float.IsNaN(v) || v <= 0 ? fallback : v;
        }

        /// <summary>The shape a primitiveSettings object describes, with basicsGeometry defaults.</summary>
        public static Shape FromSettings(JsonNode settings)
        {
            var dims = settings.Obj("dimensions");
            var name = settings.Str("shape", "box");
            var s = new Shape { Name = name, Segments = Mathf.Clamp(Mathf.RoundToInt(Pick(dims, "segments", 32)), 3, 256) };
            switch (name)
            {
                case "sphere": s.Radius = Pick(dims, "radius", 0.6f); break;
                case "cylinder": s.Radius = Pick(dims, "radius", 0.5f); s.Height = Pick(dims, "height", 1.2f); break;
                case "cone": s.Radius = Pick(dims, "radius", 0.6f); s.Height = Pick(dims, "height", 1.2f); break;
                case "pyramid": s.Radius = Pick(dims, "radius", 0.8f); s.Height = Pick(dims, "height", 1.2f); s.Segments = 4; break;
                case "plane": s.Width = Pick(dims, "width", 2); s.Depth = Pick(dims, "depth", 2); break;
                case "torus": s.Radius = Pick(dims, "radius", 0.6f); s.Tube = Pick(dims, "tube", 0.22f); break;
                case "capsule": s.Radius = Pick(dims, "radius", 0.4f); s.Height = Pick(dims, "height", 1); break;
                case "wedge": s.Width = Pick(dims, "width", 1); s.Height = Pick(dims, "height", 1); s.Depth = Pick(dims, "depth", 1); break;
                default: s.Name = "box"; s.Width = Pick(dims, "width", 1); s.Height = Pick(dims, "height", 1); s.Depth = Pick(dims, "depth", 1); break;
            }
            return s;
        }

        public static Mesh Build(Shape s)
        {
            var b = new Builder();
            switch (s.Name)
            {
                case "sphere": Sphere(b, s.Radius, s.Segments, Mathf.Max(2, s.Segments / 2)); break;
                case "cylinder": Cylinder(b, s.Radius, s.Radius, s.Height, s.Segments); break;
                case "cone": Cylinder(b, 0, s.Radius, s.Height, s.Segments); break;
                case "pyramid": Cylinder(b, 0, s.Radius, s.Height, 4); break;
                case "plane": Plane(b, s.Width, s.Depth); break;
                case "torus": Torus(b, s.Radius, s.Tube, Mathf.Max(4, s.Segments / 2), s.Segments); break;
                case "capsule": Capsule(b, s.Radius, s.Height, Mathf.Max(2, s.Segments / 4), s.Segments); break;
                case "wedge": Wedge(b, s.Width, s.Height, s.Depth); break;
                default: Box(b, s.Width, s.Height, s.Depth); break;
            }
            // three space -> Unity space: mirror X, reverse winding.
            for (int i = 0; i < b.P.Count; i++)
            {
                b.P[i] = new Vector3(-b.P[i].x, b.P[i].y, b.P[i].z);
                b.N[i] = new Vector3(-b.N[i].x, b.N[i].y, b.N[i].z);
            }
            for (int i = 0; i < b.T.Count; i += 3) { int t = b.T[i + 1]; b.T[i + 1] = b.T[i + 2]; b.T[i + 2] = t; }
            var mesh = new Mesh { name = "Zimension " + s.Key };
            if (b.P.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(b.P);
            mesh.SetNormals(b.N);
            mesh.SetUVs(0, b.UV);
            mesh.SetTriangles(b.T, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        static void Box(Builder b, float w, float h, float d)
        {
            float x = w / 2, y = h / 2, z = d / 2;
            // Counter-clockwise seen from outside (three's front faces).
            b.Quad(new Vector3(x, -y, z), new Vector3(x, -y, -z), new Vector3(x, y, -z), new Vector3(x, y, z), Vector3.right);
            b.Quad(new Vector3(-x, -y, -z), new Vector3(-x, -y, z), new Vector3(-x, y, z), new Vector3(-x, y, -z), Vector3.left);
            b.Quad(new Vector3(-x, y, z), new Vector3(x, y, z), new Vector3(x, y, -z), new Vector3(-x, y, -z), Vector3.up);
            b.Quad(new Vector3(-x, -y, -z), new Vector3(x, -y, -z), new Vector3(x, -y, z), new Vector3(-x, -y, z), Vector3.down);
            b.Quad(new Vector3(-x, -y, z), new Vector3(x, -y, z), new Vector3(x, y, z), new Vector3(-x, y, z), Vector3.forward);
            b.Quad(new Vector3(x, -y, -z), new Vector3(-x, -y, -z), new Vector3(-x, y, -z), new Vector3(x, y, -z), Vector3.back);
        }

        static void Plane(Builder b, float w, float d)
        {
            // PlaneGeometry rotated -90 degrees about X: flat on XZ, facing +Y.
            float x = w / 2, z = d / 2;
            b.Quad(new Vector3(-x, 0, z), new Vector3(x, 0, z), new Vector3(x, 0, -z), new Vector3(-x, 0, -z), Vector3.up);
        }

        static void Sphere(Builder b, float r, int ws, int hs)
        {
            int first = b.P.Count;
            for (int iy = 0; iy <= hs; iy++)
            {
                float v = (float)iy / hs;
                for (int ix = 0; ix <= ws; ix++)
                {
                    float u = (float)ix / ws;
                    var p = new Vector3(-r * Mathf.Cos(u * 2 * Mathf.PI) * Mathf.Sin(v * Mathf.PI), r * Mathf.Cos(v * Mathf.PI), r * Mathf.Sin(u * 2 * Mathf.PI) * Mathf.Sin(v * Mathf.PI));
                    b.Add(p, p, new Vector2(u, 1 - v));
                }
            }
            int At(int iy, int ix) => first + iy * (ws + 1) + ix;
            for (int iy = 0; iy < hs; iy++)
                for (int ix = 0; ix < ws; ix++)
                {
                    int a = At(iy, ix + 1), bb = At(iy, ix), c = At(iy + 1, ix), d = At(iy + 1, ix + 1);
                    if (iy != 0) b.Tri(a, bb, d);
                    if (iy != hs - 1) b.Tri(bb, c, d);
                }
        }

        static void Cylinder(Builder b, float top, float bottom, float height, int radial)
        {
            float half = height / 2, slope = (bottom - top) / Mathf.Max(0.0001f, height);
            int first = b.P.Count;
            for (int y = 0; y <= 1; y++)
            {
                float radius = y * (bottom - top) + top;
                for (int x = 0; x <= radial; x++)
                {
                    float u = (float)x / radial, theta = u * 2 * Mathf.PI;
                    b.Add(new Vector3(radius * Mathf.Sin(theta), -y * height + half, radius * Mathf.Cos(theta)), new Vector3(Mathf.Sin(theta), slope, Mathf.Cos(theta)), new Vector2(u, 1 - y));
                }
            }
            for (int x = 0; x < radial; x++)
            {
                int a = first + x, bb = first + radial + 1 + x, c = first + radial + 2 + x, d = first + x + 1;
                b.Tri(a, bb, d); b.Tri(bb, c, d);
            }
            if (top > 0) Cap(b, top, half, true, radial);
            if (bottom > 0) Cap(b, bottom, -half, false, radial);
        }

        static void Cap(Builder b, float radius, float y, bool top, int radial)
        {
            var n = top ? Vector3.up : Vector3.down;
            int center = b.Add(new Vector3(0, y, 0), n, new Vector2(0.5f, 0.5f));
            int first = b.P.Count;
            for (int x = 0; x <= radial; x++)
            {
                float theta = (float)x / radial * 2 * Mathf.PI;
                b.Add(new Vector3(radius * Mathf.Sin(theta), y, radius * Mathf.Cos(theta)), n, new Vector2(Mathf.Sin(theta) * 0.5f + 0.5f, Mathf.Cos(theta) * 0.5f + 0.5f));
            }
            for (int x = 0; x < radial; x++)
            {
                if (top) b.Tri(first + x, first + x + 1, center);
                else b.Tri(first + x + 1, first + x, center);
            }
        }

        static void Torus(Builder b, float radius, float tube, int radial, int tubular)
        {
            int first = b.P.Count;
            for (int j = 0; j <= radial; j++)
                for (int i = 0; i <= tubular; i++)
                {
                    float u = (float)i / tubular * 2 * Mathf.PI, v = (float)j / radial * 2 * Mathf.PI;
                    var p = new Vector3((radius + tube * Mathf.Cos(v)) * Mathf.Cos(u), (radius + tube * Mathf.Cos(v)) * Mathf.Sin(u), tube * Mathf.Sin(v));
                    b.Add(p, new Vector3(p.x - radius * Mathf.Cos(u), p.y - radius * Mathf.Sin(u), p.z), new Vector2((float)i / tubular, (float)j / radial));
                }
            for (int j = 1; j <= radial; j++)
                for (int i = 1; i <= tubular; i++)
                {
                    int a = first + (tubular + 1) * j + i - 1, bb = first + (tubular + 1) * (j - 1) + i - 1;
                    int c = first + (tubular + 1) * (j - 1) + i, d = first + (tubular + 1) * j + i;
                    b.Tri(a, bb, d); b.Tri(bb, c, d);
                }
        }

        static void Capsule(Builder b, float radius, float length, int capSegments, int radial)
        {
            // CapsuleGeometry(radius, length): a lathe of two quarter circles joined by the straight middle.
            var profile = new List<Vector2>(); var normals = new List<Vector2>();
            for (int i = 0; i <= capSegments; i++)
            {
                float a = -Mathf.PI / 2 + Mathf.PI / 2 * i / capSegments;
                profile.Add(new Vector2(radius * Mathf.Cos(a), -length / 2 + radius * Mathf.Sin(a))); normals.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)));
            }
            for (int i = 0; i <= capSegments; i++)
            {
                float a = Mathf.PI / 2 * i / capSegments;
                profile.Add(new Vector2(radius * Mathf.Cos(a), length / 2 + radius * Mathf.Sin(a))); normals.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)));
            }
            int first = b.P.Count, rows = profile.Count;
            for (int j = 0; j < rows; j++)
                for (int x = 0; x <= radial; x++)
                {
                    float theta = (float)x / radial * 2 * Mathf.PI;
                    float s = Mathf.Sin(theta), c = Mathf.Cos(theta);
                    b.Add(new Vector3(profile[j].x * s, profile[j].y, profile[j].x * c), new Vector3(normals[j].x * s, normals[j].y, normals[j].x * c), new Vector2((float)x / radial, (float)j / (rows - 1)));
                }
            for (int j = 0; j < rows - 1; j++)
                for (int x = 0; x < radial; x++)
                {
                    int a = first + j * (radial + 1) + x, d = a + 1, bb = a + radial + 1, c = bb + 1;
                    b.Tri(a, d, bb); b.Tri(d, c, bb);
                }
        }

        static void Wedge(Builder b, float w, float h, float d)
        {
            float x0 = -w / 2, x1 = w / 2, y0 = -h / 2, y1 = h / 2, z0 = -d / 2, z1 = d / 2;
            // Right-triangle cross-section in XY (high edge at x0), extruded along Z.
            var front = Vector3.forward; var back = Vector3.back;
            int a = b.Add(new Vector3(x0, y0, z1), front, new Vector2(0, 0)), bb = b.Add(new Vector3(x1, y0, z1), front, new Vector2(1, 0)), c = b.Add(new Vector3(x0, y1, z1), front, new Vector2(0, 1));
            b.Tri(a, bb, c);
            a = b.Add(new Vector3(x0, y0, z0), back, new Vector2(0, 0)); bb = b.Add(new Vector3(x1, y0, z0), back, new Vector2(1, 0)); c = b.Add(new Vector3(x0, y1, z0), back, new Vector2(0, 1));
            b.Tri(a, c, bb);
            b.Quad(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), Vector3.down);
            b.Quad(new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0), Vector3.left);
            var slope = new Vector3(h, w, 0).normalized;
            b.Quad(new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), slope);
        }
    }
}
