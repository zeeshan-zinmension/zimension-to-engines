using System.Collections.Generic;
using UnityEngine;

namespace Zimension.Kit
{
    /// <summary>
    /// Something the player can reach: an area (a box in this object's own space, grown by
    /// <see cref="reach"/>) that is tested against the player's 0.9 x 1.8 m box every frame, the
    /// way Zimension tests item boxes. Subclasses decide what happens when the player is in range
    /// (an interaction prompt, a pickup, entering a vehicle).
    /// </summary>
    public abstract class PlayerTarget : MonoBehaviour
    {
        static readonly List<PlayerTarget> s_All = new List<PlayerTarget>();
        public static IReadOnlyList<PlayerTarget> All => s_All;

        [Tooltip("The reachable area in this object's local space (set from the model's bounds on import).")]
        public Bounds area = new Bounds(Vector3.zero, Vector3.one);
        [Tooltip("Extra distance around the area, in metres.")]
        public float reach = 0.2f;

        /// <summary>The prompt text ("To {action}" fills in this), or empty for none.</summary>
        public abstract string Prompt { get; }
        /// <summary>Pickups show the "To Pick {action}" prompt.</summary>
        public virtual bool IsPickup => false;
        /// <summary>Whether this target currently accepts the interact key.</summary>
        public virtual bool Available => isActiveAndEnabled;
        /// <summary>The interact key was pressed while this target was selected.</summary>
        public abstract void Use(PlayerController player);

        protected virtual void OnEnable() { if (!s_All.Contains(this)) s_All.Add(this); }
        protected virtual void OnDisable() => s_All.Remove(this);

        /// <summary>The area as a world-space axis-aligned box (moving objects carry it along).</summary>
        public Bounds WorldBounds => WorldBox(transform, area, reach);

        public static Bounds WorldBox(Transform t, Bounds local, float grow)
        {
            var m = t.localToWorldMatrix;
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = -min;
            for (int i = 0; i < 8; i++)
            {
                var p = m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1)));
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            var b = new Bounds((min + max) / 2, max - min);
            b.Expand(grow * 2);
            return b;
        }

        protected virtual void OnDrawGizmosSelected()
        {
            var b = WorldBounds;
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
