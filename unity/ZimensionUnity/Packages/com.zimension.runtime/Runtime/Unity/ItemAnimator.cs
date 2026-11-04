using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// The single animation owner of one item: its animation cards (clips and the built-in
    /// procedural effects) played through one Playables mixer on the item's Animator.
    ///
    /// Semantics follow Engine.playCurrentAnimation / playAndFadeIn / stopAnimation and the native
    /// resolveTrack/trackTime: playing a card cross-fades every other running card out over 0.5 s;
    /// speed, start/end windows, reverse, loop and "Stay in pose" (clampOnFinish) are honoured;
    /// a one-shot that ends fires its finished event exactly once for that playback, then the
    /// item returns to its Play-on-Start cards. Clip time is driven explicitly each frame, so a
    /// window or reverse is exact and there is no root-motion double movement.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ItemAnimator : MonoBehaviour
    {
        public const float DefaultFade = 0.5f;

        sealed class Card
        {
            public int Index;
            public JsonNode Source;
            public string Playmode;
            public bool StartsOnPlay;
            public int Clip = -1;           // index into ZimensionItem.clips, -1 when procedural/unavailable
            public Procedural Procedural;   // built-in effect
            // resolveTrack
            public float Speed = 1, Start, End, Duration;
            public bool Loop, Reverse, Hold, Window;
            // playback
            public bool Running;
            public float Elapsed, Weight, TargetWeight, FadeRate;
            public int Generation;
            public bool FinishedReported;
            public int Input = -1;
        }

        ZimensionItem m_Item;
        Animator m_Animator;
        PlayableGraph m_Graph;
        AnimationMixerPlayable m_Mixer;
        readonly List<Card> m_Cards = new List<Card>();
        readonly List<AnimationClipPlayable> m_Inputs = new List<AnimationClipPlayable>();
        Transform m_ProceduralTarget;
        Vector3 m_BasePosition, m_BaseScale;
        Quaternion m_BaseRotation;
        bool m_Built;

        /// <summary>(card) a one-shot clip finished; the world forwards it to the event runtime.</summary>
        public event Action<int> Finished;

        public int CardCount => m_Cards.Count;
        public bool HasCards => m_Cards.Count > 0;
        public bool IsPlayingAny { get { foreach (var c in m_Cards) if (c.Running && c.TargetWeight > 0) return true; return false; } }
        public IEnumerable<string> Warnings => m_Warnings;
        readonly List<string> m_Warnings = new List<string>();

        public void Build(ZimensionItem item)
        {
            if (m_Built) return;
            m_Built = true;
            m_Item = item;
            var json = item.Json;
            if (!json.Truthy("allowAnimations")) return;
            var root = item.modelRoot ? item.modelRoot : item.transform;
            m_ProceduralTarget = root;
            m_BasePosition = root.localPosition; m_BaseRotation = root.localRotation; m_BaseScale = root.localScale;
            int cardIndex = -1;
            foreach (var a in json["animations"].Items)
            {
                cardIndex++;
                var card = new Card { Index = cardIndex, Source = a.IsObject ? a : JsonNode.NewObject() };
                m_Cards.Add(card);
                if (!a.IsObject) continue;
                card.Playmode = a.Str("playmode");
                card.StartsOnPlay = card.Playmode == "auto" || (card.Playmode == "event" && EventValue.Trim(a.Str("event")) == "on_start");
                if (a.Str("animationSource") == "default")
                {
                    card.Procedural = Procedural.FromCard(a, item.itemId, cardIndex);
                    continue;
                }
                card.Clip = ResolveClip(a);
                if (card.Clip < 0)
                {
                    var name = a.Str("animationName");
                    m_Warnings.Add($"Animation card {cardIndex} on '{item.itemName}': clip {(string.IsNullOrEmpty(name) ? "#" + a.Num("animationIndex", -1) : "'" + name + "'")} is not available in the imported model or library.");
                    continue;
                }
                var clip = item.clips[card.Clip];
                bool loop = a.Flag("loop", card.Playmode == "auto");
                Resolve(card, clip ? clip.length : 0, a.Float("speed", 1), a.Float("startTime", -1), a.Float("endTime", -1), loop, a.Truthy("reverse"), a.Truthy("clampOnFinish"));
            }
            BuildGraph(root);
        }

        int ResolveClip(JsonNode card)
        {
            var clips = m_Item.clips;
            if (clips == null || clips.Length == 0) return -1;
            var name = card.Str("animationName");
            if (!string.IsNullOrEmpty(name))
            {
                var names = m_Item.clipNames;
                if (names != null)
                    for (int i = 0; i < names.Length && i < clips.Length; i++) if (clips[i] && names[i] == name) return i;
                for (int i = 0; i < clips.Length; i++) if (clips[i] && clips[i].name == name) return i;
                // Library clips are imported as "Name [sourceId:index]"; a bare name may still match the first of them.
                for (int i = 0; i < clips.Length; i++) if (clips[i] && clips[i].name.StartsWith(name + " [", StringComparison.Ordinal)) return i;
                return -1;
            }
            int index = (int)card.Num("animationIndex", -1);
            return index >= 0 && index < clips.Length && clips[index] ? index : -1;
        }

        /// <summary>Native resolveTrack.</summary>
        static void Resolve(Card c, float duration, float speed, float start, float end, bool loop, bool reverse, bool hold)
        {
            c.Loop = loop; c.Reverse = reverse; c.Hold = !loop && hold; c.Duration = duration;
            c.Speed = !float.IsNaN(speed) && !float.IsInfinity(speed) && Mathf.Abs(speed) > 0 ? Mathf.Abs(speed) : 1;
            float s = !float.IsNaN(start) && start >= 0 ? start : 0, e = !float.IsNaN(end) && end >= 0 ? end : duration;
            if (duration > 0) { s = Mathf.Min(s, duration); e = Mathf.Min(e, duration); if (!(e > s)) e = duration; }
            else s = e = 0;
            c.Start = s; c.End = e;
            c.Window = (duration > 0 && (s > 0 || e < duration - 0.0001f)) || reverse;
        }

        /// <summary>Native trackTime: the clip time for elapsed seconds, or -1 once a one-shot has ended.</summary>
        static float TrackTime(Card c, float elapsed)
        {
            if (c.Duration <= 0) return 0;
            float progress = elapsed * c.Speed;
            if (!c.Window)
            {
                if (c.Loop) return Mathf.Repeat(progress, c.Duration);
                return progress < c.Duration ? progress : c.Hold ? c.Duration : -1f;
            }
            // The web runtime holds a windowed or reversed one-shot on its boundary frame regardless of "Stay in pose".
            float span = c.End - c.Start;
            float p = span <= 0 ? 0 : c.Loop ? Mathf.Repeat(progress, span) : Mathf.Min(progress, span);
            return c.Reverse ? c.End - p : c.Start + p;
        }

        static bool OneShotEnded(Card c, float elapsed)
        {
            if (c.Loop || c.Duration <= 0) return !c.Loop && c.Duration <= 0;
            float progress = elapsed * c.Speed;
            float span = c.Window ? c.End - c.Start : c.Duration;
            return progress >= span;
        }

        void BuildGraph(Transform root)
        {
            bool anyClip = false;
            foreach (var c in m_Cards) if (c.Clip >= 0) { anyClip = true; break; }
            if (!anyClip) return;
            m_Animator = root.GetComponent<Animator>();
            if (!m_Animator) m_Animator = root.gameObject.AddComponent<Animator>();
            m_Animator.applyRootMotion = false;
            m_Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            m_Graph = PlayableGraph.Create($"Zimension {m_Item.itemName}");
            m_Graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            int inputs = 0;
            foreach (var c in m_Cards) if (c.Clip >= 0) inputs++;
            m_Mixer = AnimationMixerPlayable.Create(m_Graph, inputs);
            int input = 0;
            foreach (var c in m_Cards)
            {
                if (c.Clip < 0) continue;
                var playable = AnimationClipPlayable.Create(m_Graph, m_Item.clips[c.Clip]);
                playable.SetApplyFootIK(false);
                playable.SetSpeed(0); // time is set explicitly
                m_Graph.Connect(playable, 0, m_Mixer, input);
                m_Mixer.SetInputWeight(input, 0);
                c.Input = input++;
                m_Inputs.Add(playable);
            }
            var output = AnimationPlayableOutput.Create(m_Graph, "Animation", m_Animator);
            output.SetSourcePlayable(m_Mixer);
            m_Graph.Play();
        }

        /// <summary>Play-on-Start cards (playmode auto, or an event card on on_start).</summary>
        public void PlayStartCards()
        {
            foreach (var c in m_Cards) if (c.StartsOnPlay) StartCard(c, fadeOthers: false, fade: 0);
        }

        /// <summary>Engine.playCurrentAnimation for a card.</summary>
        public void Play(int cardIndex)
        {
            if (cardIndex < 0 || cardIndex >= m_Cards.Count) return;
            var c = m_Cards[cardIndex];
            float fade = DefaultFade;
            var sequencerFade = c.Source["sequencerFadeDuration"];
            if (sequencerFade.IsNumber) fade = Mathf.Max(0, (float)sequencerFade.AsNumber);
            StartCard(c, fadeOthers: true, fade: fade);
        }

        void StartCard(Card c, bool fadeOthers, float fade)
        {
            if (c.Procedural != null)
            {
                c.Running = true; c.Elapsed = 0; c.Generation++; c.FinishedReported = false;
                return;
            }
            if (c.Clip < 0) return;
            if (fadeOthers)
            {
                foreach (var other in m_Cards)
                {
                    if (other == c || !other.Running || other.Procedural != null) continue;
                    FadeTo(other, 0, fade);
                }
            }
            c.Running = true; c.Elapsed = 0; c.Generation++; c.FinishedReported = false;
            if (fade <= 0) { c.Weight = c.TargetWeight = 1; c.FadeRate = 0; }
            else FadeTo(c, 1, fade);
        }

        static void FadeTo(Card c, float target, float seconds)
        {
            c.TargetWeight = target;
            c.FadeRate = seconds > 0 ? 1f / seconds : float.PositiveInfinity;
        }

        /// <summary>Engine.stopAnimation: stop the card, then resume the Play-on-Start cards.</summary>
        public void Stop(int cardIndex)
        {
            if (cardIndex < 0 || cardIndex >= m_Cards.Count) return;
            var c = m_Cards[cardIndex];
            StopCard(c);
            foreach (var start in m_Cards)
                if (start != c && start.StartsOnPlay && !start.Running) StartCard(start, fadeOthers: false, fade: DefaultFade);
        }

        void StopCard(Card c)
        {
            if (c.Procedural != null)
            {
                c.Running = false;
                if (c.Procedural.RestoreOnStop) RestoreProceduralBase();
                return;
            }
            c.Running = false; c.Weight = c.TargetWeight = 0;
            if (c.Input >= 0) m_Mixer.SetInputWeight(c.Input, 0);
        }

        /// <summary>Stop every card and return to the bind pose (reset_world).</summary>
        public void ResetAll()
        {
            foreach (var c in m_Cards) { c.Running = false; c.Weight = c.TargetWeight = 0; c.Elapsed = 0; c.FinishedReported = false; }
            if (m_Graph.IsValid()) for (int i = 0; i < m_Inputs.Count; i++) m_Mixer.SetInputWeight(i, 0);
            RestoreProceduralBase();
        }

        void RestoreProceduralBase()
        {
            if (!m_ProceduralTarget) return;
            m_ProceduralTarget.localPosition = m_BasePosition;
            m_ProceduralTarget.localRotation = m_BaseRotation;
            m_ProceduralTarget.localScale = m_BaseScale;
        }

        /// <summary>Advance playback; called by the world each frame so completions are ordered with events.</summary>
        public void Tick(float dt)
        {
            if (m_Cards.Count == 0) return;
            bool proceduralActive = false;
            var position = Vector3.zero; var rotation = Quaternion.identity; float scale = 1;
            var finished = (List<Card>)null;
            foreach (var c in m_Cards)
            {
                if (!c.Running) continue;
                c.Elapsed += dt;
                if (c.Procedural != null)
                {
                    if (!c.Procedural.Evaluate(c.Elapsed, out var p, out var r, out var s))
                    {
                        c.Running = false;
                        if (c.Procedural.RestoreOnStop) RestoreProceduralBase();
                        continue;
                    }
                    proceduralActive = true;
                    position += p; rotation = rotation * r; scale *= s;
                    continue;
                }
                // Weight fades.
                if (c.Weight != c.TargetWeight)
                {
                    float step = float.IsInfinity(c.FadeRate) ? 1 : c.FadeRate * dt;
                    c.Weight = Mathf.MoveTowards(c.Weight, c.TargetWeight, step);
                    if (c.Weight <= 0 && c.TargetWeight <= 0) { c.Running = false; m_Mixer.SetInputWeight(c.Input, 0); continue; }
                }
                float time = TrackTime(c, c.Elapsed);
                bool ended = OneShotEnded(c, c.Elapsed);
                if (time < 0) time = c.Reverse ? c.Start : c.End;
                m_Inputs[c.Input].SetTime(time);
                m_Mixer.SetInputWeight(c.Input, c.Weight);
                if (ended && !c.FinishedReported && c.TargetWeight > 0)
                {
                    c.FinishedReported = true;
                    (finished ??= new List<Card>()).Add(c);
                }
            }
            if (proceduralActive && m_ProceduralTarget)
            {
                m_ProceduralTarget.localPosition = m_BasePosition + position;
                m_ProceduralTarget.localRotation = m_BaseRotation * rotation;
                m_ProceduralTarget.localScale = m_BaseScale * scale;
            }
            if (m_Graph.IsValid()) m_Graph.Evaluate(0);
            if (finished == null) return;
            foreach (var c in finished)
            {
                // A finished one-shot that does not hold its pose returns the item to its start cards.
                if (!c.Hold && !c.Window)
                {
                    FadeTo(c, 0, 0.25f);
                    foreach (var start in m_Cards)
                        if (start != c && start.StartsOnPlay && (!start.Running || start.TargetWeight <= 0)) StartCard(start, fadeOthers: false, fade: 0.25f);
                }
                Finished?.Invoke(c.Index);
            }
        }

        void OnDestroy()
        {
            if (m_Graph.IsValid()) m_Graph.Destroy();
        }

        /// <summary>
        /// The built-in in-place effects (PROCEDURAL_ANIMATIONS: spin, hover, float, pulse, swing,
        /// wobble, bounce, shake, breathing, tiltLoop). Offsets are computed in three space as the
        /// web does, then mirrored into Unity space.
        /// </summary>
        sealed class Procedural
        {
            enum Kind { Spin, Hover, Float, Pulse, Swing, Wobble, Bounce, Shake, Breathing, TiltLoop }
            static readonly (float speed, float intensity, int axis)[] Defaults =
            {
                (0.5f, 1, 1), (0.6f, 0.25f, 1), (0.35f, 0.2f, 1), (1.2f, 0.15f, 1), (0.7f, 20, 2),
                (1.6f, 8, 1), (1, 0.4f, 1), (6, 0.05f, 1), (0.25f, 0.05f, 1), (0.4f, 1, 2),
            };
            const float Tau = Mathf.PI * 2, Degree = Mathf.PI / 180;

            Kind m_Kind;
            int m_Axis = 1;
            float m_Speed = 0.5f, m_Intensity = 1, m_StartDelay, m_TiltFrom = -15, m_TiltTo = 15, m_StartOffset;
            bool m_Loop = true;
            public bool RestoreOnStop = true;

            public static Procedural FromCard(JsonNode card, string itemId, int index)
            {
                var settings = card.Obj("proceduralSettings");
                var typeName = settings.Str("type", card.Str("animationName", "spin"));
                var p = new Procedural();
                p.m_Kind = Parse(typeName);
                var d = Defaults[(int)p.m_Kind];
                p.m_Speed = d.speed; p.m_Intensity = d.intensity; p.m_Axis = d.axis;
                var axis = settings.Str("axis");
                if (axis == "x") p.m_Axis = 0; else if (axis == "y") p.m_Axis = 1; else if (axis == "z") p.m_Axis = 2;
                p.m_Speed = Mathf.Clamp(settings.Float("speed", p.m_Speed), 0.05f, 10);
                p.m_Intensity = Mathf.Clamp(settings.Float("intensity", p.m_Intensity), 0, 10);
                p.m_StartDelay = Mathf.Clamp(settings.Float("startDelay", 0), 0, 10);
                p.m_TiltFrom = Mathf.Clamp(settings.Float("tiltFrom", -15), -180, 180);
                p.m_TiltTo = Mathf.Clamp(settings.Float("tiltTo", 15), -180, 180);
                p.m_Loop = settings.Flag("loop", true);
                p.RestoreOnStop = settings.Flag("restoreOnStop", true);
                // The web draws a random offset; a stable hash keeps runs repeatable.
                if (settings.Flag("randomStartOffset", false))
                    p.m_StartOffset = (float)((uint)((itemId ?? "") + ":" + index).GetHashCode() % 1000003u) / 1000003f / Mathf.Max(0.0001f, p.m_Speed);
                return p;
            }

            static Kind Parse(string name)
            {
                switch ((name ?? "").ToLowerInvariant())
                {
                    case "hover": return Kind.Hover;
                    case "float": return Kind.Float;
                    case "pulse": return Kind.Pulse;
                    case "swing": return Kind.Swing;
                    case "wobble": return Kind.Wobble;
                    case "bounce": return Kind.Bounce;
                    case "shake": return Kind.Shake;
                    case "breathing": return Kind.Breathing;
                    case "tiltloop": return Kind.TiltLoop;
                    default: return Kind.Spin;
                }
            }

            static Vector3 OnAxis(int axis, float v) => axis == 0 ? new Vector3(v, 0, 0) : axis == 2 ? new Vector3(0, 0, v) : new Vector3(0, v, 0);

            /// <summary>Offsets for elapsed seconds; false once a non-looping effect has completed.</summary>
            public bool Evaluate(float elapsed, out Vector3 position, out Quaternion rotation, out float scale)
            {
                position = Vector3.zero; rotation = Quaternion.identity; scale = 1;
                float t = Mathf.Max(0, elapsed - m_StartDelay) + m_StartOffset;
                if (!m_Loop && m_Speed * (elapsed - m_StartDelay) >= 1) return false;
                float w = Tau * m_Speed * t;
                Vector3 pos = Vector3.zero, rot = Vector3.zero; // three space; rot is Euler XYZ radians
                switch (m_Kind)
                {
                    case Kind.Spin: rot = OnAxis(m_Axis, Tau * m_Speed * m_Intensity * t); break;
                    case Kind.Hover: pos = OnAxis(m_Axis, Mathf.Sin(w) * m_Intensity); break;
                    case Kind.Float:
                        pos = new Vector3(Mathf.Sin(w * 0.73f + 1.3f) * m_Intensity * 0.6f, Mathf.Sin(w) * m_Intensity, Mathf.Sin(w * 0.51f + 2.7f) * m_Intensity * 0.6f);
                        rot = new Vector3(Mathf.Sin(w * 0.37f) * 4 * Degree, Mathf.Sin(w * 0.29f + 0.8f) * 6 * Degree, Mathf.Sin(w * 0.43f + 1.9f) * 4 * Degree);
                        break;
                    case Kind.Pulse: case Kind.Breathing: scale = 1 + Mathf.Sin(w) * m_Intensity; break;
                    case Kind.Swing: rot = OnAxis(m_Axis, Mathf.Sin(w) * m_Intensity * Degree); break;
                    case Kind.Wobble:
                    {
                        float a = m_Intensity * Degree;
                        rot = new Vector3(Mathf.Sin(w) * a, Mathf.Sin(w * 0.63f + 2.1f) * a * 0.5f, Mathf.Sin(w * 1.37f + 0.9f) * a);
                        break;
                    }
                    case Kind.Bounce: pos = OnAxis(m_Axis, Mathf.Abs(Mathf.Sin(Mathf.PI * m_Speed * t)) * m_Intensity); break;
                    case Kind.Shake:
                    {
                        float v = m_Speed * t, a = m_Intensity * 20 * Degree;
                        float J(float x, float y) => Mathf.Sin(v * x) * 0.6f + Mathf.Sin(v * y) * 0.4f;
                        pos = new Vector3(J(37.1f, 17.3f) * m_Intensity, J(29.7f, 13.1f) * m_Intensity, J(41.3f, 19.7f) * m_Intensity);
                        rot = new Vector3(J(23.9f, 11.7f) * a, J(31.1f, 15.3f) * a, J(27.3f, 9.7f) * a);
                        break;
                    }
                    case Kind.TiltLoop:
                    {
                        float cycle = Mathf.Repeat(m_Speed * t, 1), triangle = cycle < 0.5f ? cycle * 2 : 2 - cycle * 2;
                        float eased = triangle * triangle * (3 - 2 * triangle);
                        rot = OnAxis(m_Axis, (m_TiltFrom + (m_TiltTo - m_TiltFrom) * eased) * m_Intensity * Degree);
                        break;
                    }
                }
                position = Coordinates.ThreeToUnityPoint(pos.ToNumerics()).ToUnity();
                rotation = Coordinates.ThreeToUnityRotation(Coordinates.EulerToQuaternion(rot.x, rot.y, rot.z)).ToUnity();
                return true;
            }
        }
    }
}
