using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// Fires <see cref="onTouch"/> when this object's bounds meet <see cref="other"/>'s and
    /// <see cref="onSeparate"/> when they part (Zimension's item-to-item triggers). While either
    /// object is hidden nothing changes.
    /// </summary>
    [AddComponentMenu("Zimension/Item Contact")]
    public sealed class ItemContact : MonoBehaviour
    {
        public Transform other;
        public UnityEvent onTouch = new UnityEvent();
        public UnityEvent onSeparate = new UnityEvent();
        bool m_Touching;

        static bool Box(Transform t, out Bounds b)
        {
            b = default; bool any = false;
            foreach (var r in t.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            foreach (var c in t.GetComponentsInChildren<Collider>())
            {
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            return any;
        }

        void Update()
        {
            if (!other || !other.gameObject.activeInHierarchy) return;
            if (!Box(transform, out var a) || !Box(other, out var b)) return;
            bool touching = a.Intersects(b);
            if (touching == m_Touching) return;
            m_Touching = touching;
            (touching ? onTouch : onSeparate).Invoke();
        }
    }
}
