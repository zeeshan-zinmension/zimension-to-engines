using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// Zimension's built-in in-place animations (spin, hover, float, pulse, swing, wobble, bounce,
    /// shake, breathing, tilt loop) applied to <see cref="target"/> as offsets from its pose when it
    /// started. Call <see cref="Play"/> / <see cref="Stop"/>, or tick <see cref="playOnStart"/>.
    /// </summary>
    [AddComponentMenu("Zimension/Procedural Motion")]
    public sealed class ProceduralMotion : MonoBehaviour
    {
        public enum Kind { Spin, Hover, Float, Pulse, Swing, Wobble, Bounce, Shake, Breathing, TiltLoop }
        public enum Axis { X, Y, Z }

        public Transform target;
        public Kind kind = Kind.Spin;
        public Axis axis = Axis.Y;
        [Tooltip("Cycles per second.")] public float speed = 0.5f;
        [Tooltip("Metres, degrees or scale fraction, depending on the kind (spin: turns per cycle).")] public float intensity = 1;
        public float startDelay;
        public float tiltFrom = -15, tiltTo = 15;
        public bool loop = true;
        public bool playOnStart;
        [Tooltip("Return to the starting pose when stopped.")] public bool restoreOnStop = true;
        public UnityEvent onFinished = new UnityEvent();

        Vector3 m_Position, m_Scale;
        Quaternion m_Rotation;
        bool m_Playing, m_HasBase;
        float m_Elapsed;
        const float Tau = Mathf.PI * 2, Degree = Mathf.PI / 180;

        public bool Playing => m_Playing;

        void Reset() => target = transform;

        void Start() { if (playOnStart) Play(); }

        public void Play()
        {
            if (!target) target = transform;
            if (!m_HasBase) { m_Position = target.localPosition; m_Rotation = target.localRotation; m_Scale = target.localScale; m_HasBase = true; }
            m_Elapsed = 0;
            m_Playing = true;
        }

        public void Stop()
        {
            if (!m_Playing) return;
            m_Playing = false;
            if (restoreOnStop && m_HasBase && target) { target.localPosition = m_Position; target.localRotation = m_Rotation; target.localScale = m_Scale; }
        }

        Vector3 On(float v) => axis == Axis.X ? new Vector3(v, 0, 0) : axis == Axis.Z ? new Vector3(0, 0, v) : new Vector3(0, v, 0);

        void Update()
        {
            if (!m_Playing || !target) return;
            m_Elapsed += Time.deltaTime;
            float t = Mathf.Max(0, m_Elapsed - startDelay);
            if (!loop && speed * t >= 1) { m_Playing = false; onFinished.Invoke(); return; }
            float w = Tau * speed * t;
            Vector3 pos = Vector3.zero, rot = Vector3.zero; float scale = 1;
            switch (kind)
            {
                case Kind.Spin: rot = On(Tau * speed * intensity * t); break;
                case Kind.Hover: pos = On(Mathf.Sin(w) * intensity); break;
                case Kind.Float:
                    pos = new Vector3(Mathf.Sin(w * 0.73f + 1.3f) * intensity * 0.6f, Mathf.Sin(w) * intensity, Mathf.Sin(w * 0.51f + 2.7f) * intensity * 0.6f);
                    rot = new Vector3(Mathf.Sin(w * 0.37f) * 4 * Degree, Mathf.Sin(w * 0.29f + 0.8f) * 6 * Degree, Mathf.Sin(w * 0.43f + 1.9f) * 4 * Degree);
                    break;
                case Kind.Pulse: case Kind.Breathing: scale = 1 + Mathf.Sin(w) * intensity; break;
                case Kind.Swing: rot = On(Mathf.Sin(w) * intensity * Degree); break;
                case Kind.Wobble:
                {
                    float a = intensity * Degree;
                    rot = new Vector3(Mathf.Sin(w) * a, Mathf.Sin(w * 0.63f + 2.1f) * a * 0.5f, Mathf.Sin(w * 1.37f + 0.9f) * a);
                    break;
                }
                case Kind.Bounce: pos = On(Mathf.Abs(Mathf.Sin(Mathf.PI * speed * t)) * intensity); break;
                case Kind.Shake:
                {
                    float v = speed * t, a = intensity * 20 * Degree;
                    float J(float x, float y) => Mathf.Sin(v * x) * 0.6f + Mathf.Sin(v * y) * 0.4f;
                    pos = new Vector3(J(37.1f, 17.3f), J(29.7f, 13.1f), J(41.3f, 19.7f)) * intensity;
                    rot = new Vector3(J(23.9f, 11.7f) * a, J(31.1f, 15.3f) * a, J(27.3f, 9.7f) * a);
                    break;
                }
                case Kind.TiltLoop:
                {
                    float cycle = Mathf.Repeat(speed * t, 1), tri = cycle < 0.5f ? cycle * 2 : 2 - cycle * 2;
                    rot = On((tiltFrom + (tiltTo - tiltFrom) * tri * tri * (3 - 2 * tri)) * intensity * Degree);
                    break;
                }
            }
            target.localPosition = m_Position + pos;
            target.localRotation = m_Rotation * Quaternion.Euler(rot * Mathf.Rad2Deg);
            target.localScale = m_Scale * scale;
        }
    }
}
