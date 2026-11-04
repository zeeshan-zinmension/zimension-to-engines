using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Unity
{
    /// <summary>
    /// Inspector hook: invokes a UnityEvent when a Zimension event fires (optionally only for one
    /// item's scope), so ordinary components can react to exported workflows without code.
    /// </summary>
    [AddComponentMenu("Zimension/Zimension Event Listener")]
    public sealed class ZimensionEventListener : MonoBehaviour
    {
        [Tooltip("World event name (or an item's @local event with an item id below).")]
        public string eventName;
        [Tooltip("Optional: only when the event runs in this item's scope.")]
        public string scopeItemId;
        public UnityEvent onEvent = new UnityEvent();

        void OnEnable() => ZimensionEngine.EventFired += Handle;
        void OnDisable() => ZimensionEngine.EventFired -= Handle;

        void Handle(string name, string item)
        {
            if (name != eventName) return;
            if (!string.IsNullOrEmpty(scopeItemId) && scopeItemId != item) return;
            onEvent.Invoke();
        }

        /// <summary>Call from a UnityEvent (button, trigger) to fire this listener's event.</summary>
        public void Fire() => ZimensionEngine.TriggerEvent(eventName, string.IsNullOrEmpty(scopeItemId) ? null : scopeItemId);
    }
}
