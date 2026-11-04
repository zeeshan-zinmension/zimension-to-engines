using System;
using System.Collections.Generic;
using UnityEngine;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// One imported Zimension project: the saved world document (events, logic flows, variables,
    /// player and UI settings) and the Unity scene generated for each Zimension scene.
    ///
    /// The world JSON is the editable source for world-level logic. Edit it here (or re-import);
    /// runtime code never writes back to it, so Inspector edits are never overwritten at play.
    /// </summary>
    [CreateAssetMenu(menuName = "Zimension/Project", fileName = "ZimensionProject")]
    public sealed class ZimensionProject : ScriptableObject
    {
        [Serializable]
        public sealed class SceneEntry
        {
            [Tooltip("Zimension scene node id.")]
            public string sceneId;
            public string name;
            [Tooltip("Unity scene name (as listed in Build Settings).")]
            public string unityScene;
            [Tooltip("World event that switches to this scene (scene settings > switchOnEvent).")]
            public string switchOnEvent;
            public bool fade = true;
            public float fadeDuration = 0.35f;
            public float fadeInDelay;
            [Tooltip("This scene's world-settings overrides (JSON object merged over the world).")]
            [TextArea(2, 8)] public string worldSettingsJson = "{}";
        }

        [Header("Source")]
        public string worldId;
        public string worldName;
        public string exporterVersion;
        public string importedAtUtc;

        [Header("World document (editable)")]
        [Tooltip("The saved world object from masterData.json: events, eventBroadcasters, variables, avatar, playerSettings, sounds, soundTriggers, ...")]
        [TextArea(4, 30)] public string worldJson = "{}";

        [Header("World sounds")]
        [Tooltip("Parallel to world.sounds (missing tracks keep their slot as null).")]
        public AudioClip[] worldSounds = Array.Empty<AudioClip>();
        public string[] worldSoundSubtitles = Array.Empty<string>();

        [Header("Scenes")]
        public string baseSceneId;
        public List<SceneEntry> scenes = new List<SceneEntry>();

        [Header("Player")]
        [Tooltip("The avatar character model (an imported glTF prefab) used for the third-person player.")]
        public GameObject avatarPrefab;
        [Tooltip("Locomotion clips for the avatar: Idle, Walk, Run, Jump (resolved at import).")]
        public AnimationClip idleClip, walkClip, runClip, jumpClip;
        public float avatarHeight = 1.8f;

        [Header("UI")]
        public Texture2D loadingImage;
        public Texture2D logoImage;
        [Tooltip("Custom HUD/overlay images by their saved src URL.")]
        public List<NamedTexture> uiImages = new List<NamedTexture>();
        [Tooltip("Show the start screen (title, Start button) before play begins, as the web player does.")]
        public bool showStartScreen = true;

        [Header("Import report")]
        [Tooltip("Features this project uses that the Unity runtime approximates or does not run.")]
        [TextArea(2, 20)] public string compatibilitySummary = "";

        [Serializable]
        public sealed class NamedTexture
        {
            public string url;
            public Texture2D texture;
        }

        [NonSerialized] JsonNode m_World;
        [NonSerialized] string m_ParsedFrom;

        /// <summary>The parsed world document (cached until <see cref="worldJson"/> changes).</summary>
        public JsonNode World
        {
            get
            {
                if (m_World == null || !ReferenceEquals(m_ParsedFrom, worldJson))
                {
                    m_ParsedFrom = worldJson;
                    m_World = JsonNode.TryParse(string.IsNullOrEmpty(worldJson) ? "{}" : worldJson, out var parsed) && parsed.IsObject ? parsed : JsonNode.NewObject();
                }
                return m_World;
            }
        }

        public SceneEntry FindScene(string sceneId)
        {
            foreach (var s in scenes) if (s.sceneId == sceneId) return s;
            return null;
        }

        public SceneEntry FindSceneByUnityName(string unityScene)
        {
            foreach (var s in scenes) if (s.unityScene == unityScene) return s;
            return null;
        }

        /// <summary>
        /// The world as a scene sees it: the scene's worldSettings merged over the world document
        /// (applySceneGraphSettingsToWorld).
        /// </summary>
        public JsonNode WorldForScene(string sceneId)
        {
            var world = World;
            var entry = FindScene(sceneId);
            if (entry == null || string.IsNullOrEmpty(entry.worldSettingsJson)) return world;
            if (!JsonNode.TryParse(entry.worldSettingsJson, out var overrides) || !overrides.IsObject || overrides.Count == 0) return world;
            var merged = JsonNode.NewObject();
            foreach (var pair in world.Members) merged.Set(pair.Key, pair.Value);
            foreach (var pair in overrides.Members) merged.Set(pair.Key, pair.Value);
            return merged;
        }
    }
}
