using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zimension.Core;
using N = System.Numerics;

namespace Zimension.Unity
{
    /// <summary>
    /// Runs one generated Zimension scene. It collects the scene's <see cref="ZimensionItem"/>s
    /// (including inactive ones, so hidden items still receive their show events), builds the
    /// engine-free <see cref="EventRuntime"/> from the saved world and item data, and applies the
    /// runtime's actions through ordinary Unity components:
    /// visibility → renderers/lights (rendering only) and colliders (whole-item removal);
    /// transitions → Transform; animation cards → <see cref="ItemAnimator"/> (Playables);
    /// sound cards → AudioSources (<see cref="SoundBank"/>) with Rhubarb <see cref="LipSync"/>;
    /// prompts/messages/custom UI → <see cref="ZimensionHud"/>; player → <see cref="ZimensionPlayer"/>;
    /// scene switches → SceneManager with variables and delayed events carried over.
    ///
    /// The runtime and every Unity call run on the main thread in one ordered frame step, so an
    /// event's effects are visible to the next condition in the same chain.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public sealed class ZimensionWorld : MonoBehaviour
    {
        [Tooltip("The imported project (world document, scenes, player).")]
        public ZimensionProject project;
        [Tooltip("The Zimension scene id this Unity scene was generated from.")]
        public string sceneId;
        [Tooltip("Camera used for play; created when empty.")]
        public Camera playCamera;
        [Tooltip("Log every event and action to the Console.")]
        public bool traceEvents;

        EventRuntime m_Runtime;
        EventWorld m_EventWorld;
        JsonNode m_World;
        readonly List<ZimensionItem> m_Items = new List<ZimensionItem>();          // event index -> item
        readonly Dictionary<string, ZimensionItem> m_ById = new Dictionary<string, ZimensionItem>(StringComparer.Ordinal);
        readonly Dictionary<ZimensionItem, int> m_Index = new Dictionary<ZimensionItem, int>();
        readonly List<ItemAnimator> m_Animators = new List<ItemAnimator>();        // per event index (may be null)
        readonly List<SoundBank> m_Banks = new List<SoundBank>();                  // per event index (may be null)
        readonly List<LipSync> m_LipSyncs = new List<LipSync>();
        readonly List<ItemAnimator> m_StaticAnimators = new List<ItemAnimator>(); // animated items with no event behaviour
        SoundBank m_WorldBank;
        ZimensionPlayer m_Player;
        ZimensionHud m_Hud;
        Host m_Host;
        bool m_Playing, m_Paused, m_Switching, m_PointerMode;
        readonly List<string> m_Warnings = new List<string>();
        readonly Queue<string> m_RecentEvents = new Queue<string>();
        float m_PointerDistance = 8, m_PointerYaw, m_PointerPitch = 20;
        Vector3 m_PointerTarget;

        public EventRuntime Runtime => m_Runtime;
        public ZimensionPlayer Player => m_Player;
        public ZimensionHud Hud => m_Hud;
        public IReadOnlyList<ZimensionItem> EventItems => m_Items;
        public IReadOnlyList<string> Warnings => m_Warnings;

        // ---- Setup ----------------------------------------------------------------------------

        void Awake()
        {
            if (!project) { Debug.LogError("[Zimension] ZimensionWorld has no project assigned.", this); enabled = false; return; }
            ZimensionEngine.Active = this;
            m_World = project.WorldForScene(sceneId);
            m_Host = new Host(this);
            BuildEventWorld();
            SetupCamera();
            SetupPlayer();
            m_Hud = gameObject.GetComponent<ZimensionHud>() ?? gameObject.AddComponent<ZimensionHud>();
            m_Hud.Configure(this, m_World, project, project.uiImages.Select(i => new KeyValuePair<string, Texture2D>(i.url, i.texture)));
            m_WorldBank = new SoundBank(gameObject, project.worldSounds, null, project.worldSoundSubtitles, positional: false);
            m_WorldBank.TrackFinished += track => m_Runtime.SoundTrackFinished(-1, track);
            m_Runtime = new EventRuntime();
            m_Runtime.Load(m_EventWorld, m_Host);
            foreach (var u in m_EventWorld.Unsupported) Warn(u + " are not run by the Unity runtime yet.");
        }

        void Start()
        {
            if (!enabled) return;
            if (ZimensionSession.Started)
            {
                // A scene switch: carried state, no second on_start.
                m_Runtime.Restore(ZimensionSession.Carry);
                ZimensionSession.Carry = null;
                if (ZimensionSession.Health >= 0 && m_Player) m_Player.health = ZimensionSession.Health;
                BeginPlay();
                if (ZimensionSession.FadeInOnLoad) { m_Hud.FadeOut(0); m_Hud.FadeIn(ZimensionSession.FadeDuration); ZimensionSession.FadeInOnLoad = false; }
            }
            else if (project.showStartScreen) m_Hud.ShowScreen("start");
            else BeginPlay();
        }

        void Warn(string message)
        {
            if (m_Warnings.Contains(message)) return;
            m_Warnings.Add(message);
            Debug.LogWarning("[Zimension] " + message, this);
        }

        void BuildEventWorld()
        {
            m_EventWorld = EventParsing.ParseWorld(m_World);
            var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in m_EventWorld.Flows) if (!string.IsNullOrEmpty(f.Owner)) owners.Add(f.Owner);
            var all = GetComponentsInChildren<ZimensionItem>(true).OrderBy(i => i.order).ToList();
            var rootInverse = transform.worldToLocalMatrix;
            foreach (var item in all)
            {
                if (!string.IsNullOrEmpty(item.itemId)) m_ById[item.itemId] = item;
                if (!string.IsNullOrEmpty(item.sourceId) && !m_ById.ContainsKey(item.sourceId)) m_ById[item.sourceId] = item;
                // Splines: curves for follow-path transitions, in this world's space.
                var spline = item.Json.Obj("splineSettings");
                if (spline.IsObject)
                {
                    var curve = SplineCurve.Parse(spline);
                    if (curve.Valid)
                    {
                        curve.ConvertToUnity();
                        curve.Measure();
                        var path = new EventPath { Curve = curve, World = (rootInverse * item.transform.localToWorldMatrix).ToNumerics() };
                        if (!string.IsNullOrEmpty(item.itemId)) m_EventWorld.Paths[item.itemId] = path;
                        if (!string.IsNullOrEmpty(item.sourceId)) m_EventWorld.Paths[item.sourceId] = path;
                    }
                }
            }
            foreach (var item in all)
            {
                var animator = item.GetComponent<ItemAnimator>() ?? item.gameObject.AddComponent<ItemAnimator>();
                animator.Build(item);
                foreach (var w in animator.Warnings) Warn(w);
                var e = new EventItem();
                if (!EventParsing.ParseItem(e, item.Json, owners))
                {
                    if (animator.HasCards) m_StaticAnimators.Add(animator);
                    continue;
                }
                int index = m_Items.Count;
                item.eventIndex = index;
                m_Items.Add(item);
                m_Index[item] = index;
                // Placement in this world's space (the importer converted everything once; Unity edits are respected).
                e.Local = (rootInverse * item.transform.localToWorldMatrix).ToNumerics();
                e.Parent = N.Matrix4x4.Identity;
                if (item.transform.parent != transform)
                {
                    // Items are placed directly under the world root; a nested item moves in its parent's space.
                    var parent = rootInverse * item.transform.parent.localToWorldMatrix;
                    e.Local = UnityMath.LocalMatrix(item.transform);
                    e.Parent = parent.ToNumerics();
                }
                item.TryGetBounds(out var b);
                var local = TransformBounds(rootInverse, b);
                e.Low = local.min.ToNumerics(); e.High = local.max.ToNumerics();
                e.States.Clear();
                e.States.Add(e.Local);
                for (int s = 1; s < item.states.Count; s++) e.States.Add(item.states[s].ToMatrix());
                if (item.states.Count == 0) foreach (var st in item.Json["states"].Items) e.States.Add(Coordinates.ThreeToUnity(EventParsing.StateMatrix(st)));
                e.Visible = item.savedVisible && !item.Json.Truthy("useAsColliderMesh");
                m_EventWorld.Items.Add(e);
                m_Animators.Add(animator);
                // Sounds and lip sync.
                SoundBank bank = null;
                LipSync lip = null;
                if (item.sounds != null && item.sounds.Length > 0)
                {
                    bank = new SoundBank(item.gameObject, item.sounds, item.voiceData, item.subtitles, positional: true);
                    int captured = index;
                    bank.TrackFinished += track => m_Runtime.SoundTrackFinished(captured, track);
                    if (item.voiceData != null && item.voiceData.Any(v => v))
                    {
                        lip = LipSync.Create(item.modelRoot ? item.modelRoot.gameObject : item.gameObject);
                        if (lip == null) Warn($"'{item.itemName}' has lip-sync data but no viseme blendshapes; its voice plays without facial animation.");
                        else
                        {
                            var l = lip;
                            bank.TrackStarted += (track, source, voice) => { if (voice) l.Play(source, voice); else l.Stop(); };
                            bank.TrackStopped += _ => { };
                        }
                    }
                }
                m_Banks.Add(bank);
                m_LipSyncs.Add(lip);
                int animIndex = index;
                animator.Finished += card => m_Runtime.AnimationFinished(animIndex, card);
            }
        }

        static Bounds TransformBounds(Matrix4x4 m, Bounds b)
        {
            var result = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
            var e = b.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
                result.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return result;
        }

        void SetupCamera()
        {
            if (!playCamera) playCamera = Camera.main;
            if (!playCamera)
            {
                var go = new GameObject("Zimension Camera") { tag = "MainCamera" };
                playCamera = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            playCamera.nearClipPlane = 0.05f;
            playCamera.farClipPlane = Mathf.Max(playCamera.farClipPlane, 5000);
            var avatar = m_World.Obj("avatar");
            float fov = avatar.Float("fov", m_World.Float("fov", 0));
            if (fov > 1 && fov < 170) playCamera.fieldOfView = fov;
            if (!FindFirstObjectByType<AudioListener>()) playCamera.gameObject.AddComponent<AudioListener>();
        }

        void SetupPlayer()
        {
            var control = m_World.Obj("playerSettings").Str("defaultControl",
                m_World.Str("playType") == "interactive" ? "pointer" : m_World.Obj("avatar").Str("playerType") == "vehicle" ? "vehicle" : "character");
            m_PointerMode = control == "pointer";
            if (control == "vehicle") Warn("Vehicle player mode is not supported by the Unity runtime yet; the scene starts with the on-foot player.");
            var spawn = GetComponentInChildren<ZimensionSpawnPoint>(true);
            var spawnPos = spawn ? spawn.transform.position : transform.position;
            float spawnYaw = spawn ? spawn.transform.eulerAngles.y : 0;
            if (m_PointerMode)
            {
                m_PointerTarget = spawnPos + Vector3.up;
                m_PointerYaw = spawnYaw;
                var pointer = m_World.Obj("pointer");
                if (pointer.IsObject)
                {
                    var p = Vec(pointer["position"], new Vector3(0, 2, 5));
                    var t = Vec(pointer["target"], new Vector3(0, 1, 0));
                    m_PointerTarget = t;
                    var offset = p - t;
                    m_PointerDistance = Mathf.Max(0.5f, offset.magnitude);
                    m_PointerYaw = Mathf.Atan2(-offset.x, -offset.z) * Mathf.Rad2Deg;
                    m_PointerPitch = Mathf.Asin(Mathf.Clamp(offset.y / m_PointerDistance, -1, 1)) * Mathf.Rad2Deg;
                }
                return;
            }
            m_Player = FindFirstObjectByType<ZimensionPlayer>();
            if (!m_Player)
            {
                var go = new GameObject("Zimension Player");
                go.AddComponent<CharacterController>();
                m_Player = go.AddComponent<ZimensionPlayer>();
            }
            m_Player.Configure(m_World, project, playCamera);
            m_Player.SetSpawn(spawnPos, spawnYaw);
            m_Player.Teleport(spawnPos, spawnYaw);
        }

        /// <summary>A saved three.js {x,y,z} position converted to Unity space.</summary>
        static Vector3 Vec(JsonNode v, Vector3 fallbackThree)
        {
            var p = new Vector3(v.Float("x", fallbackThree.x), v.Float("y", fallbackThree.y), v.Float("z", fallbackThree.z));
            return new Vector3(-p.x, p.y, p.z);
        }

        // ---- Play lifecycle -------------------------------------------------------------------

        /// <summary>Start (or resume after a scene switch) play: On Start cards, then on_start once per world run.</summary>
        public void BeginPlay()
        {
            if (m_Playing) return;
            m_Playing = true;
            m_Hud.ShowScreen("");
            foreach (var a in m_Animators) a?.PlayStartCards();
            foreach (var a in m_StaticAnimators) a.PlayStartCards();
            bool first = !ZimensionSession.Started;
            ZimensionSession.Started = true;
            m_Runtime.Start(first);
            if (m_Player && !m_PointerMode) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        public void SetPaused(bool paused)
        {
            m_Paused = paused;
            Time.timeScale = paused ? 0 : 1;
            AudioListener.pause = paused;
            m_WorldBank?.SetPaused(paused);
            foreach (var b in m_Banks) b?.SetPaused(paused);
            m_Hud.ShowScreen(paused ? "pause" : "");
            if (paused) m_Player?.UnlockCursor();
        }

        /// <summary>reset_world: the loaded state again (variables, items, transitions, timers, player) and on_start.</summary>
        public void ResetWorld()
        {
            m_WorldBank.StopAll();
            foreach (var b in m_Banks) b?.StopAll();
            foreach (var l in m_LipSyncs) l?.Stop();
            foreach (var a in m_Animators) a?.ResetAll();
            m_Hud.HideAllOverlays();
            m_Hud.SetWaypoint(null);
            m_Runtime.Reset();
            for (int i = 0; i < m_Items.Count; i++) UnityMath.ApplyLocal(m_Items[i].transform, m_EventWorld.Items[i].Local);
            if (m_Player) { m_Player.health = m_Player.maxHealth; m_Player.frozen = false; m_Player.Respawn(); }
            foreach (var a in m_Animators) a?.PlayStartCards();
            m_Runtime.Start(true);
        }

        public void RespawnAfterDeath()
        {
            m_Hud.ShowScreen("");
            if (!m_Player) return;
            m_Player.health = m_Player.maxHealth;
            m_Player.Respawn();
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }

        // ---- Frame ----------------------------------------------------------------------------

        void Update()
        {
            if (!m_Playing || m_Switching) return;
            float dt = Time.deltaTime;
            bool uiBlocks = m_Hud.BlocksInput;
            if (!uiBlocks && ZimensionInput.KeyDown(KeyCode.P)) SetPaused(true);
            else if (m_Paused && ZimensionInput.KeyDown(KeyCode.P)) SetPaused(false);
            if (m_Paused) return;
            if (!uiBlocks && ZimensionInput.KeyDown(KeyCode.F)) m_Runtime.Interact();
            if (m_PointerMode) PointerUpdate(uiBlocks);
            else if (m_Player) m_Player.Tick(dt, !uiBlocks);
            // Live bounds for clip-animated and physically moved items.
            var rootInverse = transform.worldToLocalMatrix;
            for (int i = 0; i < m_Items.Count; i++)
            {
                var item = m_Items[i];
                var animator = m_Animators[i];
                bool moving = (animator && animator.IsPlayingAny) || item.GetComponent<Rigidbody>();
                if (!moving || m_Runtime.IsMoving(i)) continue;
                if (!item.TryGetBounds(out var b)) continue;
                var local = TransformBounds(rootInverse, b);
                m_Runtime.MoveBounds(i, local.min.ToNumerics(), local.max.ToNumerics());
            }
            if (m_Player)
            {
                var feet = rootInverse.MultiplyPoint3x4(m_Player.Feet);
                m_Runtime.Update(dt, feet.ToNumerics());
                m_Runtime.BuiltIn("player_health", EventValue.Of((double)Mathf.Round(m_Player.health)));
                m_Hud.SetHealth(m_Player.health, m_Player.maxHealth);
            }
            else m_Runtime.Update(dt, new N.Vector3(float.MaxValue / 4));
            for (int i = 0; i < m_Animators.Count; i++) m_Animators[i]?.Tick(dt);
            foreach (var a in m_StaticAnimators) a.Tick(dt);
            m_WorldBank.Tick();
            for (int i = 0; i < m_Banks.Count; i++) m_Banks[i]?.Tick();
            for (int i = 0; i < m_LipSyncs.Count; i++) m_LipSyncs[i]?.Tick();
        }

        void PointerUpdate(bool uiBlocks)
        {
            if (!playCamera) return;
            if (!uiBlocks)
            {
                if (ZimensionInput.Mouse(1))
                {
                    m_PointerYaw += ZimensionInput.MouseX * 3;
                    m_PointerPitch = Mathf.Clamp(m_PointerPitch - ZimensionInput.MouseY * 3, -85, 85);
                }
                m_PointerDistance = Mathf.Clamp(m_PointerDistance * (1 - ZimensionInput.Scroll * 0.1f), 0.5f, 500);
                if (ZimensionInput.MouseDown(0))
                {
                    var ray = playCamera.ScreenPointToRay(ZimensionInput.MousePosition);
                    if (Physics.Raycast(ray, out var hit, 1000, ~0, QueryTriggerInteraction.Collide))
                    {
                        var item = hit.collider.GetComponentInParent<ZimensionItem>();
                        if (item && m_Index.TryGetValue(item, out var index)) PointerInteract(index);
                    }
                }
            }
            var rotation = Quaternion.Euler(m_PointerPitch, m_PointerYaw, 0);
            playCamera.transform.position = m_PointerTarget - rotation * Vector3.forward * m_PointerDistance;
            playCamera.transform.rotation = rotation;
        }

        void PointerInteract(int index)
        {
            // Pointer mode: clicking an interactive item runs its interaction triggers (no range needed).
            var e = m_EventWorld.Items[index];
            if (!e.Interaction || !m_Runtime.ItemActive(index)) return;
            foreach (var name in e.InteractionTriggers) m_Runtime.TriggerEvent(name, index);
            for (int c = 0; c < e.Animations.Count; c++) if (e.Animations[c].Playmode == "interaction") m_Animators[index]?.Play(c);
            for (int c = 0; c < e.Sounds.Count; c++) if (e.Sounds[c].Playmode == "interaction") m_Host.Sound(index, c, true);
        }

        // ---- Scripting API ----------------------------------------------------------------------

        int ResolveItem(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            return m_ById.TryGetValue(id, out var item) && m_Index.TryGetValue(item, out var index) ? index : -1;
        }

        public void TriggerEvent(string name, string ownerItemId = null)
        {
            if (m_Runtime == null) return;
            m_Runtime.TriggerEvent(name, ResolveItem(ownerItemId));
        }

        public string GetVariable(string name, string ownerItemId = null)
        {
            var v = m_Runtime?.Variable(name, ResolveItem(ownerItemId));
            return v == null ? null : EventValue.Display(v.Current);
        }

        public void SetVariable(string name, string value, string ownerItemId = null) => SetVariable(name, EventValue.Of(value), ownerItemId);
        public void SetVariable(string name, EventValue value, string ownerItemId = null) => m_Runtime?.SetVariable(name, value, ResolveItem(ownerItemId));

        /// <summary>The Unity item for an id or source id (hidden items included).</summary>
        public ZimensionItem FindItem(string id) => !string.IsNullOrEmpty(id) && m_ById.TryGetValue(id, out var item) ? item : null;

        // ---- Scene switching ------------------------------------------------------------------

        void SwitchScene(ZimensionProject.SceneEntry entry)
        {
            if (m_Switching || string.IsNullOrEmpty(entry.unityScene)) return;
            if (!Application.CanStreamedLevelBeLoaded(entry.unityScene))
            {
                Warn($"Scene '{entry.name}' ({entry.unityScene}) is not in Build Settings; switch skipped.");
                return;
            }
            m_Switching = true;
            ZimensionSession.Carry = m_Runtime.TakeCarry();
            ZimensionSession.Health = m_Player ? m_Player.health : -1;
            ZimensionSession.FadeInOnLoad = entry.fade;
            ZimensionSession.FadeDuration = entry.fadeDuration;
            m_WorldBank.StopAll();
            foreach (var b in m_Banks) b?.StopAll();
            foreach (var l in m_LipSyncs) l?.Stop();
            StartCoroutine(SwitchRoutine(entry));
        }

        System.Collections.IEnumerator SwitchRoutine(ZimensionProject.SceneEntry entry)
        {
            if (entry.fade && entry.fadeDuration > 0)
            {
                m_Hud.FadeOut(entry.fadeDuration);
                yield return new WaitForSecondsRealtime(entry.fadeDuration);
            }
            if (entry.fadeInDelay > 0) yield return new WaitForSecondsRealtime(entry.fadeInDelay);
            SceneManager.LoadSceneAsync(entry.unityScene, LoadSceneMode.Single);
        }

        void OnDestroy()
        {
            if (ZimensionEngine.Active == this) ZimensionEngine.Active = null;
            if (m_Paused) { Time.timeScale = 1; AudioListener.pause = false; }
        }

        // ---- Diagnostics ----------------------------------------------------------------------

        void Trace(string line)
        {
            m_RecentEvents.Enqueue(line);
            while (m_RecentEvents.Count > 24) m_RecentEvents.Dequeue();
            if (traceEvents) Debug.Log("[Zimension] " + line, this);
        }

        public string DiagnosticsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Zimension: {project.worldName}  scene {sceneId}  items {m_Items.Count} scripted / {m_ById.Count} ids  events run {m_Runtime?.EventsRun}");
            if (m_Runtime != null)
            {
                sb.AppendLine($"Interaction target: {(m_Runtime.InteractionItem >= 0 ? m_Items[m_Runtime.InteractionItem].itemName : "-")}   waypoint: {(m_Runtime.Waypoint >= 0 ? m_Items[m_Runtime.Waypoint].itemName : "-")}");
                var vars = m_Runtime.VariableNames.Take(12).Select(n => $"{n}={EventValue.Display(m_Runtime.Variable(n).Current)}");
                sb.AppendLine("Variables: " + string.Join(", ", vars));
            }
            sb.AppendLine("Recent:");
            foreach (var line in m_RecentEvents) sb.AppendLine("  " + line);
            if (m_Warnings.Count > 0)
            {
                sb.AppendLine("Not supported / approximated:");
                foreach (var w in m_Warnings.Take(8)) sb.AppendLine("  " + w);
            }
            return sb.ToString();
        }

        void OnDrawGizmos()
        {
            if (m_Runtime == null || !Application.isPlaying) return;
            var m = transform.localToWorldMatrix;
            foreach (var b in m_Runtime.GetDebugBounds())
            {
                Gizmos.color = !b.Active ? new Color(0.5f, 0.5f, 0.5f, 0.4f) : b.InRange ? Color.green : b.Disabled ? new Color(1, 0.5f, 0) : Color.cyan;
                var low = b.Low.ToUnity(); var high = b.High.ToUnity();
                Gizmos.matrix = m;
                Gizmos.DrawWireCube((low + high) / 2, high - low);
            }
            var (pl, ph) = m_Runtime.PlayerBounds;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube((pl.ToUnity() + ph.ToUnity()) / 2, ph.ToUnity() - pl.ToUnity());
            Gizmos.matrix = Matrix4x4.identity;
        }

        // ---- Host: the runtime's actions as Unity operations -----------------------------------

        sealed class Host : EventHost
        {
            readonly ZimensionWorld w;
            public Host(ZimensionWorld world) { w = world; }

            ZimensionItem Item(int i) => i >= 0 && i < w.m_Items.Count ? w.m_Items[i] : null;

            public override void OnWorldEvent(string name, int scopeItem)
            {
                var item = Item(scopeItem);
                ZimensionEngine.RaiseEvent(name, item ? item.itemId : null);
                w.m_Hud.OnWorldEvent(name);
                // Scene switches (setupSceneSwitchTriggers): a scene whose switchOnEvent names this event.
                foreach (var entry in w.project.scenes)
                    if (!string.IsNullOrEmpty(entry.switchOnEvent) && entry.switchOnEvent == name && entry.sceneId != w.sceneId) { w.SwitchScene(entry); break; }
            }

            public override void VariableChanged(string name, string value)
            {
                w.m_Hud?.SetVariable(name, value);
                ZimensionEngine.RaiseVariable(name, value);
            }

            public override void Message(string text) => w.m_Hud.ShowMessage(text);
            public override void Prompt(string text, bool pickup) => w.m_Hud.SetPrompt(text, pickup);
            public override void Link(string url) { if (url.StartsWith("http://") || url.StartsWith("https://")) Application.OpenURL(url); }
            public override void Trace(string line) => w.Trace(line);

            public override void Collected(int item, bool inventory)
            {
                var it = Item(item);
                if (it && inventory) w.m_Hud.ShowMessage($"{it.itemName} added to inventory", 2.5f);
            }

            public override void UseGameplay(int item)
            {
                var it = Item(item);
                if (!it) return;
                var e = w.m_EventWorld.Items[item];
                string what = e.Vehicle ? "Vehicles" : e.Weapon ? "Weapons" : "Carrying props";
                w.Warn($"{what} are not supported by the Unity runtime yet ('{it.itemName}').");
                w.m_Hud.ShowMessage($"{what} are not available in this Unity export yet.", 2.5f);
            }

            public override bool PlayerTouches(int item)
            {
                var it = Item(item);
                if (!it || !w.m_Player) return false;
                foreach (var c in w.m_Player.Touching) if (it.Owns(c)) return true;
                return false;
            }

            public override void ItemVisible(int item, bool visible)
            {
                var it = Item(item);
                if (!it) return;
                it.SetRenderVisible(visible);
                // Whole-item removal also removes it from physics; a saved-invisible item keeps its collider.
                bool active = w.m_Runtime == null || w.m_Runtime.ItemActive(item);
                it.SetCollisionActive(active);
                if (!active) w.m_Banks[item]?.StopAll();
            }

            public override void ItemMoved(int item, N.Matrix4x4 local)
            {
                var it = Item(item);
                if (it) UnityMath.ApplyLocal(it.transform, local);
            }

            public override void Animation(int item, int card, bool play)
            {
                var a = item >= 0 && item < w.m_Animators.Count ? w.m_Animators[item] : null;
                if (!a) return;
                if (play) a.Play(card); else a.Stop(card);
            }

            public override void Sound(int item, int card, bool play)
            {
                if (item < 0)
                {
                    var worldCards = w.m_EventWorld.Sounds;
                    if (card < 0 || card >= worldCards.Count) return;
                    if (play) { w.m_WorldBank.Play(worldCards[card].Source, stopOthers: false); Subtitle(w.m_WorldBank, worldCards[card].Source); }
                    else w.m_WorldBank.Stop(worldCards[card].Source);
                    return;
                }
                var bank = item < w.m_Banks.Count ? w.m_Banks[item] : null;
                var cards = w.m_EventWorld.Items[item].Sounds;
                if (bank == null || card < 0 || card >= cards.Count) return;
                if (play)
                {
                    bank.Play(cards[card].Source, stopOthers: true);
                    Subtitle(bank, cards[card].Source);
                }
                else bank.Stop(cards[card].Source);
            }

            void Subtitle(SoundBank bank, JsonNode card)
            {
                if (!card.Truthy("showSubtitle")) return;
                int track = card["soundIndex"].IsNumber ? (int)card["soundIndex"].AsNumber : 0;
                var text = bank.Subtitle(track);
                if (!string.IsNullOrWhiteSpace(text)) w.m_Hud.ShowMessage(text.Trim(), 6f);
            }

            public override void WaypointChanged(int item)
            {
                var it = Item(item);
                if (it && it.TryGetBounds(out var b)) w.m_Hud.SetWaypoint(b.center);
                else w.m_Hud.SetWaypoint(null);
            }

            public override void UnsupportedFlow(LogicFlow flow, int scopeItem)
            {
                w.Warn($"Logic flow '{flow.Name}' ({(string.IsNullOrEmpty(flow.ActionType) ? "player control" : flow.ActionType)}) is not run by the Unity runtime yet.");
            }

            public override void Builtin(string name)
            {
                var p = w.m_Player;
                switch (name)
                {
                    case "reset_player": p?.Respawn(); break;
                    case "reset_world": w.ResetWorld(); break;
                    case "die_start_over": w.ResetWorld(); break;
                    case "player_dead":
                        if (p) { p.health = 0; p.UnlockCursor(); }
                        w.m_Hud.ShowScreen("dead");
                        break;
                    case "game_over_win":
                        p?.UnlockCursor();
                        w.m_Hud.ShowScreen("win");
                        break;
                    case "launch_player_up_5m": p?.Launch(5); break;
                    case "launch_player_up_10m": p?.Launch(10); break;
                    case "hit_damge_10": Damage(10); break;
                    case "reduce_health_10": Damage(10); break;
                    case "reduce_health_50": Damage(50); break;
                    case "increase_health_20": if (p) p.health = Mathf.Min(p.maxHealth, p.health + 20); break;
                    case "full_health": if (p) p.health = Mathf.Min(p.maxHealth, p.health + 200); break;
                    case "scene_fade_in": w.m_Hud.FadeIn(0.6f); break;
                    case "scene_fade_out": w.m_Hud.FadeOut(0.6f); break;
                    case "freeze_player_movement": if (p) p.frozen = true; break;
                    case "unfreeze_player_movement": if (p) p.frozen = false; break;
                    case "save_game": SaveGame(); break;
                    case "increase_boost_20":
                    case "full_boost":
                    case "stop_vehicle":
                        w.Warn($"Built-in '{name}' needs vehicles, which the Unity runtime does not support yet.");
                        break;
                    default: w.Warn($"Built-in event '{name}' is not handled by the Unity runtime."); break;
                }
            }

            void Damage(float amount)
            {
                var p = w.m_Player;
                if (!p) return;
                p.health = Mathf.Max(0, p.health - amount);
                if (p.health <= 0) Builtin("player_dead");
            }

            void SaveGame()
            {
                // Saves world variables locally (PlayerPrefs); the web saves to the account.
                var data = JsonNode.NewObject();
                foreach (var n in w.m_Runtime.VariableNames)
                {
                    var v = w.m_Runtime.Variable(n);
                    if (v != null) data.Set(n, v.Current.ToJson());
                }
                PlayerPrefs.SetString("zimension:" + w.project.worldId, data.Dump());
                PlayerPrefs.Save();
                w.m_Hud.ShowMessage("Game saved", 2f);
            }
        }
    }
}
