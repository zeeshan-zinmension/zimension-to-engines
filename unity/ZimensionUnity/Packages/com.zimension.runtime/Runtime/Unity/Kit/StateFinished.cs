using UnityEngine;

namespace Zimension.Kit
{
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
