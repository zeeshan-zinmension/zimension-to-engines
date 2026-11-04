using System;
using System.Collections.Generic;
using UnityEngine;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// The identity and saved settings of one Zimension world item. Every imported item carries one,
    /// so events can address it by id even while it is hidden (the registry includes inactive items).
    ///
    /// Editable: the transform (moved items keep their new place), transition states, sound clips,
    /// voice data and the item JSON (events, cards, interaction and collect settings). Runtime code
    /// reads these at play start and never writes them back.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ZimensionItem : MonoBehaviour
    {
        [Header("Identity")]
        public string itemId;
        [Tooltip("sourceWorldItemId: the editor id triggers and copies may name.")]
        public string sourceId;
        public string itemName;
        [Tooltip("Zimension item type: item (model), primitive, light, spline, vfx, empty, terrain, wall, sprite, ...")]
        public string itemType = "item";
        [Tooltip("Order in masterData worldItems; registration order for event triggers.")]
        public int order;

        [Header("Saved item (editable)")]
        [TextArea(4, 30)] public string itemJson = "{}";

        [Header("Placement")]
        [Tooltip("Saved visibility: false keeps an invisible collider (collider-only items).")]
        public bool savedVisible = true;
        [Tooltip("Transition states in this item's parent space. State 0 is always the item's own transform at play start.")]
        public List<ZimensionPose> states = new List<ZimensionPose>();
        [Tooltip("collision: box, object (mesh), none.")]
        public string collision = "box";

        [Header("Content")]
        [Tooltip("The node that owns the model hierarchy and Animator (the imported glTF scene root).")]
        public Transform modelRoot;
        [Tooltip("Clips available to this item's animation cards, in model order then library clips.")]
        public AnimationClip[] clips = Array.Empty<AnimationClip>();
        [Tooltip("The name animation cards use for each clip (parallel to clips; empty uses the clip's own name).")]
        public string[] clipNames = Array.Empty<string>();
        [Tooltip("Parallel to the saved sounds array; a removed/missing track keeps its slot as null.")]
        public AudioClip[] sounds = Array.Empty<AudioClip>();
        [Tooltip("Rhubarb lip-sync JSON per sound slot (voiceDataUrl).")]
        public TextAsset[] voiceData = Array.Empty<TextAsset>();
        [Tooltip("Subtitle text per sound slot (sounds[i].text).")]
        public string[] subtitles = Array.Empty<string>();

        [NonSerialized] JsonNode m_Json;
        [NonSerialized] string m_ParsedFrom;
        [NonSerialized] public int eventIndex = -1;
        [NonSerialized] Renderer[] m_Renderers;
        [NonSerialized] bool[] m_RendererDefaults;
        [NonSerialized] Collider[] m_Colliders;
        [NonSerialized] bool[] m_ColliderDefaults;
        [NonSerialized] Light[] m_Lights;
        [NonSerialized] bool m_RenderVisible = true, m_CollisionActive = true;

        public JsonNode Json
        {
            get
            {
                if (m_Json == null || !ReferenceEquals(m_ParsedFrom, itemJson))
                {
                    m_ParsedFrom = itemJson;
                    m_Json = JsonNode.TryParse(string.IsNullOrEmpty(itemJson) ? "{}" : itemJson, out var parsed) && parsed.IsObject ? parsed : JsonNode.NewObject();
                }
                return m_Json;
            }
        }

        public bool RenderVisible => m_RenderVisible;
        public bool CollisionActive => m_CollisionActive;

        void CacheParts()
        {
            if (m_Renderers != null) return;
            m_Renderers = GetComponentsInChildren<Renderer>(true);
            m_RendererDefaults = new bool[m_Renderers.Length];
            for (int i = 0; i < m_Renderers.Length; i++) m_RendererDefaults[i] = m_Renderers[i].enabled;
            m_Colliders = GetComponentsInChildren<Collider>(true);
            m_ColliderDefaults = new bool[m_Colliders.Length];
            for (int i = 0; i < m_Colliders.Length; i++) m_ColliderDefaults[i] = m_Colliders[i].enabled;
            m_Lights = GetComponentsInChildren<Light>(true);
        }

        /// <summary>Forget cached renderers/colliders (after adding parts at runtime).</summary>
        public void RefreshParts() { m_Renderers = null; CacheParts(); }

        public Renderer[] Renderers { get { CacheParts(); return m_Renderers; } }
        public Collider[] Colliders { get { CacheParts(); return m_Colliders; } }

        /// <summary>
        /// Rendering-only visibility: renderers and lights, preserving parts that were authored
        /// disabled (helpers, collider meshes). Colliders are untouched.
        /// </summary>
        public void SetRenderVisible(bool visible)
        {
            CacheParts();
            m_RenderVisible = visible;
            for (int i = 0; i < m_Renderers.Length; i++) if (m_Renderers[i]) m_Renderers[i].enabled = visible && m_RendererDefaults[i];
            foreach (var l in m_Lights) if (l) l.enabled = visible;
            foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
            {
                if (visible) { if (!ps.isPlaying && ps.main.playOnAwake) ps.Play(true); }
                else ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        /// <summary>Whether the item takes part in physics (an inactive/removed item must not block the player).</summary>
        public void SetCollisionActive(bool active)
        {
            CacheParts();
            m_CollisionActive = active;
            for (int i = 0; i < m_Colliders.Length; i++) if (m_Colliders[i]) m_Colliders[i].enabled = active && m_ColliderDefaults[i];
        }

        /// <summary>World-space bounds of visible geometry (or colliders when there is none).</summary>
        public bool TryGetBounds(out Bounds bounds)
        {
            CacheParts();
            bounds = default;
            bool any = false;
            foreach (var r in m_Renderers)
            {
                if (!r || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
            }
            if (any) return true;
            foreach (var c in m_Colliders)
            {
                if (!c) continue;
                bool was = c.enabled;
                if (!was) c.enabled = true; // Collider.bounds is empty while disabled
                var b = c.bounds;
                if (!was) c.enabled = false;
                if (b.size == Vector3.zero) continue;
                if (!any) { bounds = b; any = true; } else bounds.Encapsulate(b);
            }
            if (!any)
            {
                // Logic-only item: a small box at its origin keeps range tests meaningful.
                bounds = new Bounds(transform.position, Vector3.one * 0.02f);
            }
            return true;
        }

        public bool Owns(Collider c) => c && c.GetComponentInParent<ZimensionItem>() == this;
    }
}
