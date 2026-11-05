using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Zimension.Kit
{
    /// <summary>
    /// Bakes a NavMesh from the scene's colliders when play starts (Zimension's generated ground
    /// navmesh), so characters with behaviours walk around walls and props. Agent size matches a
    /// person (0.3 m radius, 1.8 m tall, 0.4 m steps, 45 degree slopes). Replace it with a NavMesh you
    /// bake yourself (Window > AI > Navigation) whenever you like; characters use whichever exists.
    /// </summary>
    [AddComponentMenu("Zimension/Runtime NavMesh"), DefaultExecutionOrder(-200)]
    public sealed class RuntimeNavMesh : MonoBehaviour
    {
        public float agentRadius = 0.3f, agentHeight = 1.8f, agentClimb = 0.4f, agentSlope = 45;
        [Tooltip("Half-size of the baked area around this object.")] public Vector3 extents = new Vector3(300, 60, 300);
        NavMeshDataInstance m_Instance;

        public static bool Ready { get; private set; }

        void Start()
        {
            var sources = new List<NavMeshBuildSource>();
            var bounds = new Bounds(transform.position, extents * 2);
            NavMeshBuilder.CollectSources(bounds, ~0, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), sources);
            // Characters and the player are not obstacles of the walkable surface.
            sources.RemoveAll(s => s.component && (s.component.GetComponentInParent<CharacterBehaviour>() || s.component.GetComponentInParent<PlayerController>()));
            var settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = agentRadius; settings.agentHeight = agentHeight; settings.agentClimb = agentClimb; settings.agentSlope = agentSlope;
            var data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            if (data == null) return;
            m_Instance = NavMesh.AddNavMeshData(data);
            Ready = true;
        }

        void OnDestroy()
        {
            if (m_Instance.valid) m_Instance.Remove();
            Ready = false;
        }
    }
}
