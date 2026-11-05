using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// "Animation finished" events for an Animator: each entry names an Animator state; when that
    /// state plays to its end (a one-shot state, reported by <see cref="StateFinished"/> on the
    /// state), the entry's event runs. Sits next to the Animator.
    /// </summary>
    [AddComponentMenu("Zimension/Animation Events")]
    public sealed class AnimationEvents : MonoBehaviour
    {
        [Serializable]
        public sealed class Entry
        {
            public string state;
            public UnityEvent onFinished = new UnityEvent();
        }

        public List<Entry> finished = new List<Entry>();

        public UnityEvent For(string state)
        {
            foreach (var e in finished) if (e.state == state) return e.onFinished;
            var entry = new Entry { state = state };
            finished.Add(entry);
            return entry.onFinished;
        }

        public void Finished(string state)
        {
            foreach (var e in finished) if (e.state == state) e.onFinished.Invoke();
        }
    }
}
