using System;
using System.Collections.Generic;
using UnityEngine;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// The sound tracks of one owner (an item, or the world) played through AudioSources, one per
    /// saved sound slot so removed/missing tracks keep their indices.
    ///
    /// As Engine.playSound: playing a card stops every other track of the item first, restarts the
    /// track from 0 with the card's loop and volume. A track that ends naturally (not stopped, not
    /// looping, not paused) is reported once per playback so the runtime can fire its
    /// soundFinishedEvent. Item tracks are positional; world tracks are 2D.
    /// </summary>
    public sealed class SoundBank
    {
        sealed class Track
        {
            public AudioSource Source;
            public bool Playing;     // started by a card and not yet finished or stopped
            public int Generation;
            public TextAsset Voice;
            public string Subtitle;
        }

        readonly Track[] m_Tracks;
        readonly GameObject m_Owner;
        readonly bool m_Positional;
        bool m_Paused;

        /// <summary>(track) finished naturally.</summary>
        public event Action<int> TrackFinished;
        /// <summary>(track, source, voice) a track started: lip sync hooks in here.</summary>
        public event Action<int, AudioSource, TextAsset> TrackStarted;
        /// <summary>(track) a track stopped or finished.</summary>
        public event Action<int> TrackStopped;

        public int Count => m_Tracks.Length;

        public SoundBank(GameObject owner, AudioClip[] clips, TextAsset[] voices, string[] subtitles, bool positional)
        {
            m_Owner = owner;
            m_Positional = positional;
            int count = clips?.Length ?? 0;
            m_Tracks = new Track[count];
            for (int i = 0; i < count; i++)
            {
                m_Tracks[i] = new Track
                {
                    Voice = voices != null && i < voices.Length ? voices[i] : null,
                    Subtitle = subtitles != null && i < subtitles.Length ? subtitles[i] : null,
                };
                if (clips[i] == null) continue;
                var go = new GameObject($"Sound {i} ({clips[i].name})");
                go.transform.SetParent(owner.transform, false);
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.clip = clips[i];
                source.spatialBlend = positional ? 1 : 0;
                if (positional)
                {
                    // three PositionalAudio: inverse distance from refDistance; Unity's logarithmic rolloff is the same curve.
                    source.rolloffMode = AudioRolloffMode.Logarithmic;
                    source.minDistance = 4f;
                    source.maxDistance = 180f;
                    source.dopplerLevel = 0;
                }
                m_Tracks[i].Source = source;
            }
        }

        public AudioSource Source(int track) => track >= 0 && track < m_Tracks.Length ? m_Tracks[track].Source : null;
        public string Subtitle(int track) => track >= 0 && track < m_Tracks.Length ? m_Tracks[track].Subtitle : null;
        public bool IsPlaying(int track) => track >= 0 && track < m_Tracks.Length && m_Tracks[track].Playing;

        /// <summary>Engine.playSound / playWorldSound for a card.</summary>
        public void Play(JsonNode card, bool stopOthers)
        {
            int track = card["soundIndex"].IsNumber ? (int)card["soundIndex"].AsNumber : 0;
            if (track < 0 || track >= m_Tracks.Length) return;
            var t = m_Tracks[track];
            if (t.Source == null) return;
            if (stopOthers) StopAll();
            float volume = card.Float("volume", 1);
            t.Source.loop = card.Truthy("loop");
            t.Source.volume = Mathf.Clamp01(float.IsNaN(volume) ? 1 : volume);
            t.Source.pitch = 1;
            t.Source.time = 0;
            t.Source.Play();
            t.Playing = true;
            t.Generation++;
            TrackStarted?.Invoke(track, t.Source, t.Voice);
        }

        public void Stop(JsonNode card)
        {
            int track = card["soundIndex"].IsNumber ? (int)card["soundIndex"].AsNumber : 0;
            StopTrack(track);
        }

        public void StopTrack(int track)
        {
            if (track < 0 || track >= m_Tracks.Length) return;
            var t = m_Tracks[track];
            if (t.Source) t.Source.Stop();
            if (t.Playing) { t.Playing = false; TrackStopped?.Invoke(track); }
        }

        public void StopAll()
        {
            for (int i = 0; i < m_Tracks.Length; i++) StopTrack(i);
        }

        /// <summary>Pause is distinct from completion: a paused track never reports finished.</summary>
        public void SetPaused(bool paused)
        {
            if (m_Paused == paused) return;
            m_Paused = paused;
            foreach (var t in m_Tracks)
            {
                if (!t.Source || !t.Playing) continue;
                if (paused) t.Source.Pause(); else t.Source.UnPause();
            }
        }

        /// <summary>Detect natural completion; call once per frame.</summary>
        public void Tick()
        {
            if (m_Paused || AudioListener.pause) return;
            for (int i = 0; i < m_Tracks.Length; i++)
            {
                var t = m_Tracks[i];
                if (!t.Playing || !t.Source) continue;
                if (t.Source.isPlaying || t.Source.loop) continue;
                t.Playing = false;
                TrackStopped?.Invoke(i);
                TrackFinished?.Invoke(i);
            }
        }
    }
}
