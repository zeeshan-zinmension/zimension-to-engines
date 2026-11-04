using UnityEngine;

namespace Zimension.Unity
{
    /// <summary>The player start of a generated scene (editable: move it in the Scene view).</summary>
    [AddComponentMenu("Zimension/Zimension Spawn Point")]
    public sealed class ZimensionSpawnPoint : MonoBehaviour
    {
        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.8f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.9f, new Vector3(0.6f, 1.8f, 0.6f));
            Gizmos.DrawLine(transform.position + Vector3.up * 1.6f, transform.position + Vector3.up * 1.6f + transform.forward * 0.8f);
        }
    }
}
