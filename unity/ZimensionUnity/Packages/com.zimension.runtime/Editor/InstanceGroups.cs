using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Zimension.Core;

namespace Zimension.Editor
{
    /// <summary>
    /// Instance groups (instancingSource.js buildInstanceOffsets + Engine.updateInstancedMeshGroups), a
    /// C# port of the native player's InstanceGroups.cpp: a recipe (node.settings.instance) that draws
    /// copies of one or more source items in grids, lines, rings, scattered areas, shape perimeters,
    /// along a spline or at manual/brushed points, with the same seeded random numbers as the web.
    /// Works in three.js space with row-vector matrices; each copy's world matrix replaces the source
    /// item's own transform.
    /// </summary>
    public static class InstanceGroups
    {
        public struct Copy { public int Source; public Matrix4x4 World; }

        struct Offset
        {
            public Vector3 P, Rotation, Frame;
            public float Scale, AlignYaw;
            public int Source;
            public bool HasFrame, Absolute;
            public static Offset New => new Offset { Scale = 1, AlignYaw = float.NaN, Source = -1 };
        }

        sealed class Settings
        {
            public List<string> Sources = new List<string>(); public string Source = "", Mode = "together", Distribution = "pattern", Pattern = "grid", Area = "circle", Shape = "circle", Axis = "none", Layout = "generated";
            public int Count = 8; public uint Seed;
            public float Spacing = 3, Radius = 10, Width = 12, Depth = 12, Inner = 3, Outer = 10, RotationMin = 0, RotationMax = 360;
            public bool AlignCenter = true, AlignAround;
            public List<float> Weights = new List<float>();
        }

        static double Num(JsonNode j, string key, double fallback)
        {
            var v = j[key];
            if (v.IsNumber) return JsonNode.IsFinite(v.AsNumber) ? v.AsNumber : fallback;
            if (v.IsString && JsonNode.TryParseNumber(v.AsString, out var n) && JsonNode.IsFinite(n)) return n;
            return fallback;
        }

        static string Pick(JsonNode j, string key, string[] allowed, string fallback)
        {
            var v = j.Str(key, fallback);
            return allowed.Contains(v) ? v : fallback;
        }

        static Settings Parse(JsonNode j)
        {
            var s = new Settings();
            s.Source = j.Str("sourceWorldItemId");
            foreach (var id in j["sourceWorldItemIds"].Items)
                if (id.IsString && id.AsString.Length > 0 && !s.Sources.Contains(id.AsString)) s.Sources.Add(id.AsString);
            s.Mode = j.Str("sourceMode") == "mixed" ? "mixed" : "together";
            foreach (var w in j["sourceWeights"].Items) s.Weights.Add(Math.Clamp(w.IsNumber ? (float)w.AsNumber : 1f, 0f, 100f));
            s.Count = Math.Max(1, (int)Math.Floor(Num(j, "count", 8)));
            double seed = Num(j, "seed", 0); s.Seed = (uint)(Math.Abs(Math.Floor(seed)) % 4294967296.0);
            s.Distribution = Pick(j, "distributionMode", new[] { "manual", "brush", "pattern", "area", "shape", "spline" }, "pattern");
            s.Pattern = Pick(j, "patternType", new[] { "grid", "line", "radial", "random" }, "grid");
            s.Area = Pick(j, "areaType", new[] { "circle", "rectangle", "radial" }, "circle");
            s.Shape = Pick(j, "shapeType", new[] { "circle", "rectangle", "triangle" }, "circle");
            s.Axis = Pick(j, "randomRotationAxis", new[] { "none", "y", "x", "z", "xyz" }, "none");
            s.Layout = j.Str("layoutMode") == "saved" ? "saved" : "generated";
            s.Spacing = Math.Max(0.01f, (float)Num(j, "spacing", 3)); s.Radius = Math.Max(0.01f, (float)Num(j, "radius", 10));
            s.Width = Math.Max(0.01f, (float)Num(j, "width", 12)); s.Depth = Math.Max(0.01f, (float)Num(j, "depth", 12));
            s.Inner = Math.Max(0f, (float)Num(j, "innerRadius", 3)); s.Outer = Math.Max(0.01f, (float)Num(j, "outerRadius", 10));
            s.RotationMin = Math.Clamp((float)Num(j, "rotationMin", 0), -360f, 360f);
            s.RotationMax = Math.Max(s.RotationMin, Math.Clamp((float)Num(j, "rotationMax", 360), -360f, 360f));
            s.AlignCenter = !(j["alignCenter"].IsBool && !j["alignCenter"].AsBool);
            s.AlignAround = j["alignAroundShape"].IsBool && j["alignAroundShape"].AsBool;
            if (s.Mode == "mixed" && !s.Weights.Any(w => w > 0)) s.Weights = Enumerable.Repeat(1f, s.Sources.Count).ToList();
            if (s.Sources.Count == 0 && s.Source.Length > 0) s.Sources.Add(s.Source);
            return s;
        }

        /// <summary>The source item ids a recipe draws (sourceWorldItemIds, else sourceWorldItemId).</summary>
        public static List<string> Sources(JsonNode settings) => Parse(settings).Sources;
        public static bool Collides(JsonNode settings) => settings["collisionEnabled"].IsBool && settings["collisionEnabled"].AsBool;

        static uint HashSeed(string text)
        {
            uint hash = 2166136261u;
            foreach (char c in string.IsNullOrEmpty(text) ? "instances" : text) { hash ^= (byte)c; hash *= 16777619u; }
            return hash;
        }

        /// <summary>mulberry32, bit for bit.</summary>
        sealed class Random
        {
            uint m_State;
            public Random(uint seed) { m_State = seed; }
            public double Next()
            {
                unchecked
                {
                    m_State += 0x6d2b79f5u;
                    uint v = (m_State ^ (m_State >> 15)) * (1u | m_State);
                    v = (v + (v ^ (v >> 7)) * (61u | v)) ^ v;
                    return (v ^ (v >> 14)) / 4294967296.0;
                }
            }
        }

        static int ChooseSource(Settings s, int index)
        {
            var weights = Enumerable.Repeat(1f, s.Sources.Count).ToList();
            for (int i = 0; i < weights.Count && i < s.Weights.Count; i++) weights[i] = s.Weights[i];
            float total = weights.Sum();
            if (total <= 0) return 0;
            unchecked
            {
                uint hash = (s.Seed != 0 ? s.Seed : 1u) + (uint)(index + 1) * 0x9e3779b9u;
                hash = (hash ^ (hash >> 16)) * 0x85ebca6bu; hash = (hash ^ (hash >> 13)) * 0xc2b2ae35u;
                double value = (hash ^ (hash >> 16)) / 4294967296.0 * total;
                for (int i = 0; i < weights.Count; i++) { value -= weights[i]; if (value < 0) return i; }
            }
            return weights.Count - 1;
        }

        static Vector3 Aligned(Offset o, bool around)
        {
            if (!around) return Vector3.Zero;
            if (!float.IsNaN(o.AlignYaw)) return new Vector3(0, o.AlignYaw, 0);
            if (Math.Abs(o.P.X) < 0.0001f && Math.Abs(o.P.Z) < 0.0001f) return Vector3.Zero;
            return new Vector3(0, MathF.Atan2(-o.P.X, -o.P.Z), 0);
        }

        static void Decorate(Settings s, ref Offset o, Random random)
        {
            var align = Aligned(o, s.AlignAround);
            bool follows = s.Distribution == "shape" || (s.Distribution == "pattern" && s.Pattern == "radial") ||
                           (s.Distribution == "area" && (s.Area == "circle" || s.Area == "radial"));
            o.Frame = follows ? Aligned(o, true) : Vector3.Zero; o.HasFrame = true;
            var axis = s.Axis;
            if (axis == "none" && (s.Distribution == "area" || (s.Distribution == "pattern" && s.Pattern == "random")) && !s.AlignAround) axis = "y";
            float Angle() => (s.RotationMin + (float)random.Next() * (s.RotationMax - s.RotationMin)) * MathF.PI / 180;
            var r = Vector3.Zero;
            if (axis == "x" || axis == "xyz") r.X = Angle();
            if (axis == "y" || axis == "xyz") r.Y = Angle();
            if (axis == "z" || axis == "xyz") r.Z = Angle();
            o.Rotation = align + r;
        }

        static List<Offset> Scatter(int count, Func<Random, Vector3> sample, Random random)
        {
            var points = new List<Offset>();
            int candidates = count > 1000 ? 1 : 10;
            for (int i = 0; i < count; i++)
            {
                var best = Vector3.Zero; double bestDistance = -1;
                for (int a = 0; a < candidates; a++)
                {
                    var c = sample(random); double nearest = double.PositiveInfinity;
                    foreach (var p in points) { double dx = p.P.X - c.X, dz = p.P.Z - c.Z; nearest = Math.Min(nearest, dx * dx + dz * dz); }
                    if (nearest > bestDistance) { best = c; bestDistance = nearest; }
                }
                var o = Offset.New; o.P = best; points.Add(o);
            }
            return points;
        }

        static List<Offset> Perimeter(int count, Vector3[] corners, Vector3 center)
        {
            var segments = new List<(Vector3 a, Vector3 b, float length, float yaw)>(); float total = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                var a = corners[i]; var b = corners[(i + 1) % corners.Length];
                float length = Vector3.Distance(a, b);
                float tx = length > 0 ? (b.X - a.X) / length : 0, tz = length > 0 ? (b.Z - a.Z) / length : 0;
                float mx = (a.X + b.X) / 2, mz = (a.Z + b.Z) / 2, cx = center.X - mx, cz = center.Z - mz;
                bool first = (-tz * cx + tx * cz) >= (tz * cx - tx * cz);
                float nx = first ? -tz : tz, nz = first ? tx : -tx;
                segments.Add((a, b, length, MathF.Atan2(nx, nz))); total += length;
            }
            var output = Enumerable.Range(0, count).Select(_ => Offset.New).ToList();
            if (total <= 0) return output;
            for (int i = 0; i < count; i++)
            {
                float remaining = count <= 1 ? 0 : (float)i / count * total;
                var segment = segments[segments.Count - 1];
                foreach (var s in segments) { if (remaining <= s.length) { segment = s; break; } remaining -= s.length; }
                float t = segment.length <= 0 ? 0 : remaining / segment.length;
                var o = output[i]; o.P = Vector3.Lerp(segment.a, segment.b, t); o.AlignYaw = segment.yaw; output[i] = o;
            }
            return output;
        }

        static List<Offset> ManualOffsets(JsonNode j)
        {
            var output = new List<Offset>();
            var buffer = j.Obj("positionBuffer");
            if (buffer.IsObject && (int)Num(buffer, "version", 0) == 1 && buffer["chunks"].IsArray)
            {
                int stride = (int)Num(buffer, "stride", 0);
                if (stride == 3 || stride == 4 || stride == 6 || stride == 7 || stride == 8)
                {
                    var values = new List<float>();
                    foreach (var chunk in buffer["chunks"].Items)
                    {
                        if (!chunk.IsString) return new List<Offset>();
                        byte[] bytes;
                        try { bytes = System.Convert.FromBase64String(chunk.AsString); } catch { return new List<Offset>(); }
                        if (bytes.Length % 4 != 0) return new List<Offset>();
                        for (int i = 0; i < bytes.Length; i += 4) { float v = BitConverter.ToSingle(bytes, i); if (float.IsNaN(v) || float.IsInfinity(v)) return new List<Offset>(); values.Add(v); }
                    }
                    for (int i = 0; i + stride <= values.Count; i += stride)
                    {
                        var o = Offset.New; o.P = new Vector3(values[i], values[i + 1], values[i + 2]);
                        if (stride >= 6) o.Rotation = new Vector3(values[i + 3], values[i + 4], values[i + 5]);
                        if (stride == 8) o.Scale = values[i + 6];
                        if (stride == 4 || stride == 7 || stride == 8) o.Source = (int)values[i + stride - 1];
                        output.Add(o);
                    }
                    return output;
                }
            }
            foreach (var m in j["manualOffsets"].Items)
            {
                if (!m.IsObject) continue;
                var o = Offset.New; o.P = new Vector3((float)Num(m, "x", 0), (float)Num(m, "y", 0), (float)Num(m, "z", 0));
                if (m["scale"].IsNumber) o.Scale = Math.Clamp((float)m["scale"].AsNumber, 0.01f, 100f);
                if (m["sourceIndex"].IsNumber && m["sourceIndex"].AsNumber >= 0) o.Source = (int)m["sourceIndex"].AsNumber;
                var r = m.Obj("rotation");
                if (r.IsObject) o.Rotation = new Vector3((float)Num(r, "x", 0), (float)Num(r, "y", 0), (float)Num(r, "z", 0));
                output.Add(o);
            }
            return output;
        }

        static Quaternion Euler(Vector3 r) => Coordinates.EulerToQuaternion(r.X, r.Y, r.Z);

        /// <summary>
        /// The copies: <paramref name="sources"/> are the source items' three-space world matrices
        /// (sourceWorldItemIds order); <paramref name="transform"/> is node.settings.transform.
        /// <paramref name="spline"/> is the guide spline (three space) for the spline distribution.
        /// </summary>
        public static List<Copy> Copies(JsonNode j, JsonNode transform, IReadOnlyList<Matrix4x4> sources, SplineCurve spline, Matrix4x4 splineWorld, List<string> warnings)
        {
            var copies = new List<Copy>();
            if (j == null || !j.IsObject || sources.Count == 0) return copies;
            var s = Parse(j);
            if (s.Layout == "saved" && j["savedLayout"].IsObject) warnings.Add("Instance groups with a saved (baked) layout are rebuilt from their recipe.");
            if (j["snapToSurface"].IsBool && j["snapToSurface"].AsBool && s.Distribution != "spline") warnings.Add("Instance groups snapped to surfaces keep their recipe height.");
            if (j["deformAlongSpline"].IsBool && j["deformAlongSpline"].AsBool) warnings.Add("Spline-deformed instance groups are placed as undeformed copies.");
            bool mixed = s.Mode == "mixed";
            var offsets = new List<Offset>();
            var random = new Random(s.Seed > 0 ? s.Seed : HashSeed(s.Source.Length > 0 ? s.Source : s.Sources.Count == 0 ? "" : s.Sources[0]));
            int pointIndex = 0;
            List<Offset> Decorated(List<Offset> points)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    var o = points[i]; Decorate(s, ref o, random);
                    if (mixed) o.Source = ChooseSource(s, pointIndex);
                    pointIndex++; points[i] = o;
                }
                return points;
            }
            int count = s.Count;
            float w = s.Width, d = s.Depth; bool c = s.AlignCenter;
            List<Offset> Ring() => Enumerable.Range(0, count).Select(i => { var o = Offset.New; float th = (float)i / count * MathF.PI * 2; o.P = new Vector3(MathF.Cos(th) * s.Radius, 0, MathF.Sin(th) * s.Radius); return o; }).ToList();
            if (s.Distribution == "spline")
            {
                if (spline == null || !spline.Valid) { warnings.Add("An instance group's guide spline was not found."); return copies; }
                spline.Measure();
                for (int i = 0; i < count; i++)
                {
                    float u = count == 1 ? 0 : spline.Closed ? (float)i / count : (float)i / (count - 1);
                    float t = spline.UToT(u);
                    var o = Offset.New; o.Absolute = true; o.HasFrame = true;
                    o.P = Vector3.Transform(spline.Point(t), splineWorld);
                    var a = spline.Point(Math.Max(0, t - 0.0001f)); var b = spline.Point(Math.Min(1, t + 0.0001f));
                    var tangent = Vector3.Normalize(Vector3.TransformNormal(b - a, splineWorld));
                    if (Math.Abs(tangent.Y) > 0.999f) o.Frame = new Vector3(tangent.Y > 0 ? -MathF.PI / 2 : MathF.PI / 2, 0, 0);
                    else
                    {
                        var z = tangent; var x = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, z)); var y = Vector3.Cross(z, x);
                        // three Euler.setFromRotationMatrix 'XYZ' of the basis (columns x, y, z).
                        float m13 = z.X, m11 = x.X, m12 = y.X, m23 = z.Y, m33 = z.Z, m22 = y.Y, m32 = y.Z;
                        o.Frame.Y = MathF.Asin(Math.Clamp(m13, -1f, 1f));
                        if (Math.Abs(m13) < 0.9999999f) { o.Frame.X = MathF.Atan2(-m23, m33); o.Frame.Z = MathF.Atan2(-m12, m11); }
                        else { o.Frame.X = MathF.Atan2(m32, m22); o.Frame.Z = 0; }
                    }
                    o.Rotation = s.AlignAround ? o.Frame : Vector3.Zero;
                    offsets.Add(o);
                }
            }
            else if (s.Distribution == "manual")
            {
                offsets = ManualOffsets(j);
                for (int i = 0; i < offsets.Count; i++)
                    if (mixed && (offsets[i].Source < 0 || offsets[i].Source >= s.Sources.Count)) { var o = offsets[i]; o.Source = ChooseSource(s, i); offsets[i] = o; }
            }
            else if (s.Distribution == "brush")
            {
                foreach (var point in ManualOffsets(j))
                {
                    if (point.Rotation != Vector3.Zero) { var o = point; o.HasFrame = true; offsets.Add(o); pointIndex++; }
                    else offsets.Add(Decorated(new List<Offset> { point })[0]);
                }
            }
            else if (count == 1) offsets = Decorated(new List<Offset> { Offset.New });
            else if (s.Distribution == "area")
            {
                if (s.Area == "rectangle")
                {
                    float minX = c ? -w / 2 : 0, minZ = c ? -d / 2 : 0;
                    offsets = Decorated(Scatter(count, r => { float x = minX + (float)r.Next() * w; return new Vector3(x, 0, minZ + (float)r.Next() * d); }, random));
                }
                else if (s.Area == "radial")
                {
                    float inner = Math.Max(0, s.Inner), outer = Math.Max(inner + 0.01f, s.Outer);
                    offsets = Decorated(Scatter(count, r => { float th = (float)r.Next() * MathF.PI * 2; float rad = MathF.Sqrt((float)r.Next() * (outer * outer - inner * inner) + inner * inner); return new Vector3(MathF.Cos(th) * rad, 0, MathF.Sin(th) * rad); }, random));
                }
                else offsets = Decorated(Scatter(count, r => { float th = (float)r.Next() * MathF.PI * 2; float rad = MathF.Sqrt((float)r.Next()) * s.Radius; return new Vector3(MathF.Cos(th) * rad, 0, MathF.Sin(th) * rad); }, random));
            }
            else if (s.Distribution == "shape")
            {
                if (s.Shape == "rectangle")
                    offsets = Decorated(Perimeter(count, new[] { new Vector3(c ? -w / 2 : 0, 0, c ? -d / 2 : 0), new Vector3(c ? w / 2 : w, 0, c ? -d / 2 : 0), new Vector3(c ? w / 2 : w, 0, c ? d / 2 : d), new Vector3(c ? -w / 2 : 0, 0, c ? d / 2 : d) },
                        new Vector3(c ? 0 : w / 2, 0, c ? 0 : d / 2)));
                else if (s.Shape == "triangle")
                {
                    var left = new Vector3(c ? -w / 2 : 0, 0, c ? -d / 3 : 0); var right = new Vector3(c ? w / 2 : w, 0, c ? -d / 3 : 0); var top = new Vector3(c ? 0 : w / 2, 0, c ? d * 2 / 3 : d);
                    offsets = Decorated(Perimeter(count, new[] { left, right, top }, (left + right + top) / 3));
                }
                else offsets = Decorated(Ring());
            }
            else if (s.Pattern == "line")
            {
                float start = c ? -((count - 1) * s.Spacing) / 2 : 0;
                offsets = Decorated(Enumerable.Range(0, count).Select(i => { var o = Offset.New; o.P = new Vector3(start + i * s.Spacing, 0, 0); return o; }).ToList());
            }
            else if (s.Pattern == "radial") offsets = Decorated(Ring());
            else if (s.Pattern == "random") offsets = Decorated(Scatter(count, r => { float x = ((float)r.Next() * 2 - 1) * s.Radius; return new Vector3(x, 0, ((float)r.Next() * 2 - 1) * s.Radius); }, random));
            else
            {
                int columns = (int)Math.Ceiling(Math.Sqrt(count)), rows = (count + columns - 1) / columns;
                float startX = c ? -((columns - 1) * s.Spacing) / 2 : 0, startZ = c ? -((rows - 1) * s.Spacing) / 2 : 0;
                offsets = Decorated(Enumerable.Range(0, count).Select(i => { var o = Offset.New; o.P = new Vector3(startX + (i % columns) * s.Spacing, 0, startZ + (i / columns) * s.Spacing); return o; }).ToList());
            }
            int effective = s.Distribution == "manual" || s.Distribution == "brush" ? offsets.Count : Math.Min(offsets.Count, count);

            // Group transform: the node's (rotation in degrees), or the first source's position when that is identity.
            Vector3 Vec(string key, float fallback)
            {
                var v = transform != null && transform.IsObject ? transform.Obj(key) : JsonNode.Undefined;
                float C(string k) { double n = Num(v, k, fallback); return (float)(n == 0 && fallback == 1 ? 1 : n); }
                return new Vector3(C("x"), C("y"), C("z"));
            }
            var gp = Vec("position", 0); var gr = Vec("rotation", 0); var gs = Vec("scale", 1);
            bool identity = gp.Length() < 1e-4f && gr.Length() < 1e-4f && (gs - Vector3.One).Length() < 1e-4f;
            Matrix4x4 group;
            if (identity) { Matrix4x4.Decompose(sources[0], out _, out _, out var p); group = Matrix4x4.CreateTranslation(p); }
            else group = Matrix4x4.CreateScale(gs) * Matrix4x4.CreateFromQuaternion(Euler(gr * MathF.PI / 180)) * Matrix4x4.CreateTranslation(gp);

            // Templates: each source's orientation and scale, or for composite sources its whole transform relative to the pivot.
            var pivotNode = j["sourcePivotMatrix"];
            bool composite = sources.Count > 1 || (pivotNode.IsArray && pivotNode.Count == 16);
            var pivotInverse = Matrix4x4.Identity;
            if (composite && pivotNode.IsArray && pivotNode.Count == 16)
            {
                var v = new float[16];
                for (int i = 0; i < 16; i++) v[i] = pivotNode[i].IsNumber ? (float)pivotNode[i].AsNumber : (i % 5 == 0 ? 1 : 0);
                var pivot = new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
                pivotInverse = Coordinates.InverseOrIdentity(pivot);
            }
            var templates = new List<Matrix4x4>();
            foreach (var source in sources)
            {
                Matrix4x4.Decompose(source, out var sc, out var q, out _);
                var orientation = Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(q);
                templates.Add(composite && !mixed ? source * pivotInverse : orientation);
            }
            var po = j.Obj("sourcePositionOffset");
            var sourceOffset = new Vector3((float)Num(po, "x", 0), (float)Num(po, "y", 0), (float)Num(po, "z", 0));
            for (int index = 0; index < effective; index++)
            {
                var o = offsets[index];
                var rotation = Euler(o.Rotation); var frame = Euler(o.HasFrame ? o.Frame : o.Rotation);
                var position = o.P + Vector3.Transform(sourceOffset, frame);
                var offset = Matrix4x4.CreateScale(o.Scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                var place = o.Absolute ? offset : offset * group;
                for (int source = 0; source < templates.Count; source++)
                {
                    if (mixed && source != Math.Clamp(o.Source, 0, templates.Count - 1)) continue;
                    copies.Add(new Copy { Source = source, World = templates[source] * place });
                }
            }
            return copies;
        }
    }
}
