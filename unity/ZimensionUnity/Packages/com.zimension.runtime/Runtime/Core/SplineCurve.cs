using System;
using System.Collections.Generic;
using System.Numerics;

namespace Zimension.Core
{
    /// <summary>
    /// basicsGeometry.js buildSplineCurveData: a weighted Catmull-Rom (catmullrom / centripetal) or
    /// a polyline ("linear") through splineSettings.points, with three.js Curve's arc-length
    /// mapping. Ported from the native player's Splines.cpp. Points are in the spline item's local
    /// space (three space as parsed; the importer converts them for Unity with
    /// <see cref="ConvertToUnity"/>).
    /// </summary>
    public sealed class SplineCurve
    {
        public List<Vector3> Points = new List<Vector3>();
        public List<float> Weights = new List<float>();
        public List<float> Angles = new List<float>();
        public bool Closed, Linear, Centripetal;
        public float Tension = 0.5f;
        float[] m_Lengths = Array.Empty<float>();

        public bool Valid => Points.Count >= 2;
        public float Length => m_Lengths.Length == 0 ? 0 : m_Lengths[m_Lengths.Length - 1];

        public static SplineCurve Parse(JsonNode settings)
        {
            var curve = new SplineCurve();
            if (settings == null || !settings.IsObject) return curve;
            foreach (var p in settings["points"].Items)
            {
                if (!p.IsObject) continue;
                curve.Points.Add(new Vector3(p.Float("x", 0), p.Float("y", 0), p.Float("z", 0)));
                float w = p.Float("w", 1);
                curve.Weights.Add(Math.Clamp(w != 0 ? w : 1f, 0.01f, 5f)); // Number(w) || 1
                curve.Angles.Add(p.Float("a", 0));
            }
            curve.Closed = settings.Truthy("closed");
            var type = settings["curveType"].IsString ? settings["curveType"].AsString : "catmullrom";
            curve.Linear = type == "linear";
            curve.Centripetal = type == "centripetal";
            curve.Tension = settings.Float("tension", 0.5f);
            return curve;
        }

        /// <summary>Mirror the points into Unity space (the curve shape is reflection-invariant).</summary>
        public void ConvertToUnity()
        {
            for (int i = 0; i < Points.Count; i++) Points[i] = Coordinates.ThreeToUnityPoint(Points[i]);
            if (m_Lengths.Length > 0) Measure(m_Lengths.Length - 1);
        }

        static float Scale(float w) => 2f / (1f + Math.Clamp(w, 0.01f, 5f));

        /// <summary>Curve.getPoint.</summary>
        public Vector3 Point(float t)
        {
            int n = Points.Count, segments = n - (Closed ? 0 : 1);
            if (n == 0) return Vector3.Zero;
            if (n == 1) return Points[0];
            if (Linear)
            {
                int count = Math.Max(1, segments);
                float scaledT = count * Math.Clamp(t, 0f, 1f);
                int i = Math.Min(count - 1, (int)Math.Floor(scaledT));
                float f = scaledT - i;
                return Vector3.Lerp(Points[i], Points[(i + 1) % n], f);
            }
            float scaled = segments * Math.Clamp(t, 0f, 1f);
            int segment = (int)Math.Floor(scaled);
            float u = scaled - segment;
            if (!Closed && segment == n - 1) { segment = n - 2; u = 1; }
            int start = ((segment % n) + n) % n, end = (start + 1) % n;
            Vector3 p1 = Points[start], p2 = Points[end];
            Vector3 p0 = Closed || segment > 0 ? Points[(start - 1 + n) % n] : p1 - p2 + p1;
            Vector3 p3 = Closed || segment + 2 < n ? Points[(end + 1) % n] : p2 - p1 + p2;
            Vector3 t0, t1;
            if (Centripetal)
            {
                float d0 = (float)Math.Pow((p1 - p0).LengthSquared(), 0.25), d1 = (float)Math.Pow((p2 - p1).LengthSquared(), 0.25), d2 = (float)Math.Pow((p3 - p2).LengthSquared(), 0.25);
                if (d1 < 1e-4f) d1 = 1;
                if (d0 < 1e-4f) d0 = d1;
                if (d2 < 1e-4f) d2 = d1;
                t0 = ((p1 - p0) / d0 - (p2 - p0) / (d0 + d1) + (p2 - p1) / d1) * d1 * Scale(Weights[start]);
                t1 = ((p2 - p1) / d1 - (p3 - p1) / (d1 + d2) + (p3 - p2) / d2) * d1 * Scale(Weights[end]);
            }
            else
            {
                t0 = (p2 - p0) * (Tension * Scale(Weights[start]));
                t1 = (p3 - p1) * (Tension * Scale(Weights[end]));
            }
            float u2 = u * u, u3 = u2 * u;
            return p1 * (2 * u3 - 3 * u2 + 1) + t0 * (u3 - 2 * u2 + u) + p2 * (-2 * u3 + 3 * u2) + t1 * (u3 - u2);
        }

        /// <summary>Curve.getTangent (unit).</summary>
        public Vector3 Tangent(float t)
        {
            if (Linear)
            {
                int n = Points.Count, count = Math.Max(1, n - (Closed ? 0 : 1));
                int index = Math.Min(count - 1, (int)Math.Floor(count * Math.Clamp(t, 0f, 1f)));
                for (int step = 0; step < count; step++)
                {
                    foreach (int i in new[] { index + step, index - step })
                    {
                        if (i < 0 || i >= count) continue;
                        var a = Points[i]; var b = Points[(i + 1) % n];
                        float l = Vector3.Distance(a, b);
                        if (l > 1e-6f) return (b - a) / l;
                    }
                }
                return Vector3.UnitX;
            }
            var pa = Point(Math.Max(0f, t - 0.0001f));
            var pb = Point(Math.Min(1f, t + 0.0001f));
            var d = pb - pa;
            return d.LengthSquared() > 0 ? Vector3.Normalize(d) : Vector3.UnitX;
        }

        /// <summary>Curve.getLengths.</summary>
        public void Measure(int divisions = 200)
        {
            m_Lengths = new float[divisions + 1];
            var last = Point(0);
            for (int d = 1; d <= divisions; d++)
            {
                var p = Point((float)d / divisions);
                m_Lengths[d] = m_Lengths[d - 1] + Vector3.Distance(last, p);
                last = p;
            }
        }

        /// <summary>Curve.getUtoTmapping.</summary>
        public float UToT(float u)
        {
            if (m_Lengths.Length < 2) return u;
            int last = m_Lengths.Length - 1;
            float target = u * m_Lengths[last];
            int low = 0, high = last;
            while (low <= high)
            {
                int i = low + (high - low) / 2;
                float c = m_Lengths[i] - target;
                if (c < 0) low = i + 1;
                else if (c > 0) high = i - 1;
                else { high = i; break; }
            }
            int k = Math.Max(0, high);
            if (m_Lengths[k] == target) return (float)k / last;
            if (k >= last) return 1;
            float before = m_Lengths[k], segment = m_Lengths[k + 1] - before;
            return (k + (segment > 0 ? (target - before) / segment : 0)) / last;
        }

        /// <summary>makeSplineRollInterpolator: per-point roll (degrees) at curve parameter t.</summary>
        public float Roll(float t)
        {
            bool any = false;
            foreach (var a in Angles) if (a != 0) { any = true; break; }
            if (!any) return 0;
            float denominator = Closed ? Points.Count : Math.Max(1, Points.Count - 1);
            var knots = new List<(float u, float a)>();
            for (int i = 0; i < Angles.Count; i++) knots.Add((i / denominator, Angles[i]));
            if (Closed) knots.Add((1f, Angles[0]));
            if (t <= knots[0].u) return knots[0].a;
            for (int i = 1; i < knots.Count; i++)
            {
                if (t > knots[i].u) continue;
                var (u0, a0) = knots[i - 1]; var (u1, a1) = knots[i];
                return a0 + (a1 - a0) * (u1 > u0 ? (t - u0) / (u1 - u0) : 0);
            }
            return knots[knots.Count - 1].a;
        }
    }
}
