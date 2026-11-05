using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// Fires <see cref="onPlayerEnter"/> when the player's box first overlaps this object's area and
    /// <see cref="onPlayerExit"/> when it leaves (Zimension's On Enter / On Leave range events).
    /// Hiding the object ends the overlap without an exit event, as in Zimension.
    /// </summary>
    [AddComponentMenu("Zimension/Player Zone")]
    public sealed class PlayerZone : MonoBehaviour
    {
        public Bounds area = new Bounds(Vector3.zero, Vector3.one);
        public float reach = 0.02f;
        public UnityEvent onPlayerEnter = new UnityEvent();
        public UnityEvent onPlayerExit = new UnityEvent();

        bool m_Inside;
        public bool PlayerInside => m_Inside;

        void OnDisable() => m_Inside = false;

        void Update()
        {
            var player = PlayerController.Current;
            if (!player || !player.isActiveAndEnabled) return;
            bool inside = PlayerTarget.WorldBox(transform, area, reach).Intersects(player.RangeBox);
            if (inside == m_Inside) return;
            m_Inside = inside;
            (inside ? onPlayerEnter : onPlayerExit).Invoke();
        }

        void OnDrawGizmosSelected()
        {
            var b = PlayerTarget.WorldBox(transform, area, reach);
            Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
