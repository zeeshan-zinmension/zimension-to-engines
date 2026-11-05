using UnityEngine;

namespace Zimension.Kit
{
    /// <summary>
    /// Which Zimension world item this object was converted from. Re-importing the world matches
    /// objects by this id, so the GameObject (and anything you added to it) is kept and only the
    /// parts that changed in Zimension are updated. Delete it to detach the object from re-imports.
    /// </summary>
    [DisallowMultipleComponent, AddComponentMenu("Zimension/World Item Id")]
    public sealed class WorldItemId : MonoBehaviour
    {
        public string itemId;
        [Tooltip("The editor id that copies of this item share (sourceWorldItemId).")]
        public string sourceId;
    }
}
