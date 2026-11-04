using System;
using System.Collections.Generic;
using UnityEngine;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// Rhubarb lip sync driven by the playing AudioSource's sample clock (not render frames), as
    /// three.utils.js / RuhbarbKeys and the native LipSync.cpp do: each cue's end time keys the
    /// mouth shape (A-H, X) onto the face's viseme blendshapes at 0.6 intensity, with the jaw
    /// following the cue's jaw value. Keys interpolate linearly and the face returns to zero
    /// 0.3 s after the last cue. Weights are converted from three's 0..1 influences to Unity's
    /// 0..100 blendshape scale (Ready Player Me's face mouthOpen is driven to 3x, above 100).
    /// </summary>
    public sealed class LipSync
    {
        static readonly string[] Visemes = { "viseme_PP", "viseme_kk", "viseme_I", "viseme_aa", "viseme_O", "viseme_U", "mouthFunnel", "viseme_TH", "viseme_sil" };
        static readonly float[] Jaw = { 0.1f, 0, 0.3f, 0.8f, 0.3f, 0.1f, 0, 0.4f, 0 };
        const float Intensity = 0.6f;

        struct Control { public SkinnedMeshRenderer Renderer; public int Shape; public float Rest; }

        sealed class Clip
        {
            public readonly List<float> Times = new List<float>();
            public readonly List<float> Values = new List<float>(); // Times.Count * Controls.Count
            public float Duration;
        }

        readonly List<Control> m_Controls = new List<Control>();
        readonly Dictionary<TextAsset, Clip> m_Clips = new Dictionary<TextAsset, Clip>();
        Clip m_Playing;
        AudioSource m_Source;
        bool m_Loop;
        float[] m_Sampled = Array.Empty<float>();
        int[] m_Mapped = new int[9];
        int m_FaceJaw = -1, m_JawControl = -1;

        public bool Available => m_Controls.Count > 0;
        public bool Speaking => m_Playing != null;

        public static LipSync Create(GameObject root)
        {
            var lip = new LipSync();
            return lip.Bind(root) ? lip : null;
        }

        static SkinnedMeshRenderer FindWithShapes(GameObject root, string name)
        {
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.name == name && r.sharedMesh && r.sharedMesh.blendShapeCount > 0) return r;
            return null;
        }

        bool Bind(GameObject root)
        {
            SkinnedMeshRenderer face = null;
            foreach (var name in new[] { "Wolf3D_Head", "head", "stage2_mesh_id" })
                if ((face = FindWithShapes(root, name)) != null) break;
            if (!face)
                foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (r.sharedMesh && r.sharedMesh.GetBlendShapeIndex("viseme_aa") >= 0) { face = r; break; }
            if (!face) return false;
            bool rpm = face.name == "Wolf3D_Head";
            int ControlFor(SkinnedMeshRenderer r, int shape)
            {
                if (!r || shape < 0) return -1;
                for (int c = 0; c < m_Controls.Count; c++) if (m_Controls[c].Renderer == r && m_Controls[c].Shape == shape) return c;
                m_Controls.Add(new Control { Renderer = r, Shape = shape, Rest = r.GetBlendShapeWeight(shape) / 100f });
                return m_Controls.Count - 1;
            }
            for (int i = 0; i < Visemes.Length; i++) m_Mapped[i] = ControlFor(face, face.sharedMesh.GetBlendShapeIndex(Visemes[i]));
            m_FaceJaw = rpm ? ControlFor(face, face.sharedMesh.GetBlendShapeIndex("mouthOpen")) : -1;
            var jaw = rpm ? FindWithShapes(root, "Wolf3D_Teeth") : FindWithShapes(root, "jaw_lower");
            if (!jaw && !rpm) jaw = FindWithShapes(root, "jaw_L");
            if (jaw)
            {
                int target = jaw.sharedMesh.GetBlendShapeIndex(rpm ? "mouthOpen" : "jawOpen");
                if (target < 0 && jaw.sharedMesh.blendShapeCount > 0) target = 0;
                m_JawControl = ControlFor(jaw, target);
            }
            m_Sampled = new float[m_Controls.Count];
            return m_Controls.Count > 0;
        }

        Clip Build(TextAsset voice)
        {
            if (m_Clips.TryGetValue(voice, out var cached)) return cached;
            var clip = new Clip();
            m_Clips[voice] = clip;
            if (!JsonNode.TryParse(voice.text, out var data) || !data["mouthCues"].IsArray)
            {
                Debug.LogWarning($"[Zimension] Lip-sync JSON '{voice.name}' has no mouthCues array; the sound plays without lip sync.");
                return clip;
            }
            float previous = 0;
            int n = m_Controls.Count;
            foreach (var cue in data["mouthCues"].Items)
            {
                if (!cue.IsObject || !cue["end"].IsNumber) continue;
                float start = cue.Float("start", previous), end = cue.Float("end", -1);
                var value = cue.Str("value");
                if (float.IsNaN(end) || end < start || start < 0 || end < previous || value.Length != 1) continue;
                char key = value[0];
                int phoneme = key == 'X' ? 8 : key >= 'A' && key <= 'H' ? key - 'A' : -1;
                if (phoneme < 0) continue;
                clip.Times.Add(end);
                int offset = clip.Values.Count;
                for (int c = 0; c < n; c++) clip.Values.Add(m_Controls[c].Rest);
                if (m_Mapped[phoneme] >= 0) clip.Values[offset + m_Mapped[phoneme]] = Intensity;
                if (m_FaceJaw >= 0) clip.Values[offset + m_FaceJaw] = Jaw[phoneme] * 3;
                if (m_JawControl >= 0) clip.Values[offset + m_JawControl] = Jaw[phoneme];
                previous = end;
            }
            if (clip.Times.Count == 0) return clip;
            clip.Duration = previous + 0.3f;
            clip.Times.Add(clip.Duration);
            for (int c = 0; c < n; c++) clip.Values.Add(0);
            return clip;
        }

        public void Play(AudioSource source, TextAsset voice)
        {
            Stop();
            if (!voice || !source) return;
            var clip = Build(voice);
            if (clip.Times.Count == 0) return;
            m_Playing = clip; m_Source = source; m_Loop = source.loop;
            Apply(0);
        }

        public void Stop()
        {
            if (m_Playing == null) return;
            m_Playing = null; m_Source = null;
            for (int c = 0; c < m_Controls.Count; c++) Set(c, m_Controls[c].Rest);
        }

        /// <summary>Sample at the audio clock; ends with the audio (stop, finish, scene unload).</summary>
        public void Tick()
        {
            if (m_Playing == null) return;
            if (!m_Source || !m_Source.isActiveAndEnabled) { Stop(); return; }
            var audio = m_Source.clip;
            double time = audio ? (double)m_Source.timeSamples / Mathf.Max(1, audio.frequency) : m_Source.time;
            bool paused = !m_Source.isPlaying && m_Source.timeSamples > 0 && AudioListener.pause;
            if (!m_Source.isPlaying && !paused) { Stop(); return; }
            if (m_Loop && audio && audio.length > 0) time %= audio.length;
            if (!m_Loop && time >= m_Playing.Duration) { Stop(); return; }
            Apply((float)time);
        }

        void Apply(float seconds)
        {
            var clip = m_Playing;
            int n = m_Controls.Count, keys = clip.Times.Count;
            int first = 0, last = 0; float blend = 0;
            int next = clip.Times.BinarySearch(seconds);
            next = next >= 0 ? next + 1 : ~next; // upper_bound
            while (next < keys && clip.Times[next] <= seconds) next++;
            if (next >= keys) first = last = keys - 1;
            else if (next > 0)
            {
                last = next; first = last - 1;
                float span = clip.Times[last] - clip.Times[first];
                blend = span > 0 ? Mathf.Clamp01((seconds - clip.Times[first]) / span) : 0;
            }
            for (int c = 0; c < n; c++)
            {
                float a = clip.Values[first * n + c], b = clip.Values[last * n + c];
                Set(c, a + (b - a) * blend);
            }
        }

        void Set(int control, float influence)
        {
            var c = m_Controls[control];
            if (c.Renderer) c.Renderer.SetBlendShapeWeight(c.Shape, influence * 100f);
        }
    }
}
