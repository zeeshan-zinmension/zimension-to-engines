using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// Invokes <see cref="onFinished"/> when <see cref="source"/> reaches the end of a non-looping
    /// clip on its own (not when stopped from code). Zimension's "sound finished" event.
    /// </summary>
    [AddComponentMenu("Zimension/Sound Finished")]
    public sealed class SoundFinished : MonoBehaviour
    {
        public AudioSource source;
        public UnityEvent onFinished = new UnityEvent();
        bool m_WasPlaying;
        float m_LastTime;

        void Reset() => source = GetComponent<AudioSource>();

        void Update()
        {
            if (!source) return;
            bool playing = source.isPlaying;
            if (playing) { m_WasPlaying = true; m_LastTime = source.time; return; }
            if (!m_WasPlaying) return;
            m_WasPlaying = false;
            var clip = source.clip;
            // Ended naturally: it was near the end of the clip when it stopped.
            if (clip && !source.loop && m_LastTime >= clip.length - 0.25f - Time.deltaTime * 2) onFinished.Invoke();
        }
    }
}
