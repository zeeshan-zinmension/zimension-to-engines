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

    /// <summary>
    /// Put on a non-looping Animator state: reports the state's end once per playback to the
    /// <see cref="AnimationEvents"/> on the same object.
    /// </summary>
    public sealed class StateFinished : StateMachineBehaviour
    {
        public string stateName;
        bool m_Reported;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo info, int layer) => m_Reported = false;

        public override void OnStateUpdate(Animator animator, AnimatorStateInfo info, int layer)
        {
            if (m_Reported || info.loop || info.normalizedTime < 1f || animator.IsInTransition(layer)) return;
            m_Reported = true;
            animator.GetComponent<AnimationEvents>()?.Finished(stateName);
        }

        public override void OnStateExit(Animator animator, AnimatorStateInfo info, int layer)
        {
            // Left early by a transition at its exit time: still a natural end.
            if (m_Reported || info.loop || info.normalizedTime < 0.95f) return;
            m_Reported = true;
            animator.GetComponent<AnimationEvents>()?.Finished(stateName);
        }
    }
}
