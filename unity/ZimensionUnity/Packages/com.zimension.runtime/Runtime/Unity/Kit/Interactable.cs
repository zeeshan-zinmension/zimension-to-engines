using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// "Press F to {prompt}" on an object. While the player stands within the area the prompt shows;
    /// pressing the key invokes <see cref="onInteract"/>, and the prompt stays hidden for
    /// <see cref="cooldown"/> seconds. Turn it on or off from code with
    /// <see cref="EnableInteraction"/> / <see cref="DisableInteraction"/> (or the component's
    /// enabled box).
    /// </summary>
    [AddComponentMenu("Zimension/Interactable")]
    public sealed class Interactable : PlayerTarget
    {
        public string prompt = "Interact";
        [Tooltip("Seconds before the prompt shows again after use.")]
        public float cooldown = 0.5f;
        [Tooltip("Off: the interaction waits for EnableInteraction() (Zimension's enable event).")]
        public bool startEnabled = true;
        public UnityEvent onInteract = new UnityEvent();

        bool m_Disabled;
        float m_BusyUntil;

        public override string Prompt => prompt;
        public override bool Available => base.Available && !m_Disabled && Time.time >= m_BusyUntil;
        public bool InteractionEnabled => !m_Disabled;

        void Awake() => m_Disabled = !startEnabled;

        public void EnableInteraction() { m_Disabled = false; m_BusyUntil = 0; }
        public void DisableInteraction() => m_Disabled = true;

        public override void Use(PlayerController player) => Interact();

        /// <summary>Run the interaction as if the player pressed the key.</summary>
        public void Interact()
        {
            if (m_Disabled || Time.time < m_BusyUntil) return;
            m_BusyUntil = Time.time + Mathf.Max(cooldown, 0.0001f);
            onInteract.Invoke();
        }
    }
}
