using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Zimension.Core;
using Zimension.Kit;
using Zimension.Unity;
using N = System.Numerics;
using Object = UnityEngine.Object;

namespace Zimension.Editor.Conversion
{
    /// <summary>
    /// Turns a built scene (models placed, colliders and materials done) into a plain Unity scene:
    /// each item's Zimension settings become standard components (Interactable, PlayerZone,
    /// Collectable, Weapon, Vehicle, Health, ProceduralMotion, AudioSources), its animation cards an
    /// Animator Controller, its transitions baked movement clips on a second Animator, and the
    /// scene gets a Player, a Game object (GameManager + GameHud) and the world's sounds. The
    /// Zimension runtime components and saved JSON are removed; the generated logic script is added
    /// to the Game object once Unity has compiled it (<see cref="LogicWiring"/>).
    /// </summary>
    internal static class NativeConverter
    {
        internal sealed class SceneResult
        {
            public GameObject Game;
            public List<FieldBinding> Bindings = new List<FieldBinding>();
        }

        static string Folder(ZimensionImporter.Context ctx, string sub)
        {
            var path = $"{ctx.GeneratedFolder}/{sub}";
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parent = Path.GetDirectoryName(path).Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(parent)) Folder(ctx, Path.GetDirectoryName(sub).Replace('\\', '/'));
                AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
            }
            return path;
        }

        // ---- Scene -------------------------------------------------------------------------------

        public static SceneResult Convert(ZimensionImporter.Context ctx, LogicOutput logic, GameObject worldRoot, Camera camera, JsonNode sceneWorld)
        {
            var result = new SceneResult();
            var items = worldRoot.GetComponentsInChildren<ZimensionItem>(true).ToList();
            var byId = new Dictionary<string, ZimensionItem>(StringComparer.Ordinal);
            foreach (var zi in items)
            {
                if (!string.IsNullOrEmpty(zi.itemId)) byId[zi.itemId] = zi;
                if (!string.IsNullOrEmpty(zi.sourceId) && !byId.ContainsKey(zi.sourceId)) byId[zi.sourceId] = zi;
            }
            var plans = logic.Items.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
            var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in EventParsing.ParseWorld(ctx.World).Flows) if (f.Owner.Length > 0) owners.Add(f.Owner);

            // Splines for follow-path transitions (world space; the world root is the identity).
            var paths = new Dictionary<string, (SplineCurve curve, Matrix4x4 world)>(StringComparer.Ordinal);
            foreach (var zi in items)
            {
                var spline = zi.Json.Obj("splineSettings");
                if (!spline.IsObject) continue;
                var curve = SplineCurve.Parse(spline);
                if (!curve.Valid) continue;
                curve.ConvertToUnity(); curve.Measure();
                if (!string.IsNullOrEmpty(zi.itemId)) paths[zi.itemId] = (curve, zi.transform.localToWorldMatrix);
                if (!string.IsNullOrEmpty(zi.sourceId)) paths[zi.sourceId] = (curve, zi.transform.localToWorldMatrix);
            }

            var hide = new List<GameObject>();
            foreach (var zi in items)
            {
                var json = zi.Json;
                var go = zi.gameObject;
                var id = go.AddComponent<WorldItemId>();
                id.itemId = zi.itemId; id.sourceId = zi.sourceId;
                var e = new EventItem();
                EventParsing.ParseItem(e, json, owners);
                plans.TryGetValue(zi.itemId ?? "", out var plan);
                ImportStage.Mark("convert item " + zi.itemName);
                if (ctx.SkipConvertItem == zi.itemName) ctx.Report.Add(ImportReport.Level.Blocking, "Unity stopped while converting this item last time; its behaviour was skipped", "", json);
                else try { ConvertItem(ctx, zi, e, plan, byId, paths); }
                catch (Exception error)
                {
                    ctx.Report.Add(ImportReport.Level.Blocking, "Item behaviour could not be converted: " + error.Message, "", json);
                    Debug.LogException(error);
                }
                if (!zi.savedVisible) foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                if (!e.Active || e.Hidden) hide.Add(go);
            }

            ResolveBehaviourWeapons(byId);

            // World sounds (2D) under one object.
            var worldSounds = new GameObject("World Sounds");
            worldSounds.transform.SetParent(worldRoot.transform, false);
            var worldCards = EventParsing.ParseWorld(sceneWorld).Sounds;
            for (int c = 0; c < worldCards.Count; c++)
            {
                var card = worldCards[c];
                var clips = ctx.Project.worldSounds;
                var clip = clips != null && card.Sound >= 0 && card.Sound < clips.Length ? clips[card.Sound] : null;
                var source = SoundSource(worldSounds.transform, c, clip, card, positional: false);
                if (c < logic.WorldSoundFinished.Count && logic.WorldSoundFinished[c]) source.gameObject.AddComponent<SoundFinished>().source = source;
            }

            if (sceneWorld["customHUDUI"].Count > 0 || sceneWorld["customUIOverRelays"].Count > 0 || sceneWorld["customScreens"].Count > 0)
                ctx.Report.Add(ImportReport.Level.Unsupported, "Custom HUD, overlays and screens are not converted yet; the default Zimension UI (GameHud) is used", "customHUDUI");
            if (sceneWorld["objectives"].Count > 0 || sceneWorld["quests"].Count > 0)
                ctx.Report.Add(ImportReport.Level.Unsupported, "Objectives and quests are not converted yet", "objectives");
            if (sceneWorld["cutscenes"].Count > 0) ctx.Report.Add(ImportReport.Level.Unsupported, "Cutscenes are not converted yet", "cutscenes");

            // Player, game manager and HUD.
            var spawn = worldRoot.GetComponentInChildren<ZimensionSpawnPoint>(true);
            ImportStage.Mark("player animator");
            var player = BuildPlayer(ctx, sceneWorld, spawn ? spawn.transform : worldRoot.transform, camera);
            ImportStage.Mark("building scenes");
            var game = new GameObject("Game");
            var manager = game.AddComponent<GameManager>();
            var hud = game.AddComponent<GameHud>();
            hud.worldTitle = ctx.World.Str("name");
            manager.player = player; manager.hud = hud;
            if (worldRoot.GetComponentInChildren<CharacterBehaviour>(true)) game.AddComponent<RuntimeNavMesh>();
            result.Game = game;

            // The Zimension runtime is replaced by the converted components.
            foreach (var zi in items) Object.DestroyImmediate(zi);
            var zw = worldRoot.GetComponent<ZimensionWorld>();
            if (zw) Object.DestroyImmediate(zw);
            worldRoot.name = "World";
            foreach (var go in hide) go.SetActive(false);
            result.Bindings.AddRange(logic.Fields);
            return result;
        }

        // ---- Items -------------------------------------------------------------------------------

        /// <summary>The item's renderers' bounds in its own space (the box Zimension tests ranges against).</summary>
        static Bounds LocalArea(Transform t)
        {
            var renderers = t.GetComponentsInChildren<Renderer>(true);
            var inverse = t.worldToLocalMatrix;
            Bounds local = new Bounds(Vector3.up * 0.5f, Vector3.one); bool any = false;
            foreach (var r in renderers)
            {
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
                    var p = inverse.MultiplyPoint3x4(corner);
                    if (!any) { local = new Bounds(p, Vector3.zero); any = true; } else local.Encapsulate(p);
                }
            }
            return local;
        }

        static void ConvertItem(ZimensionImporter.Context ctx, ZimensionItem zi, EventItem e, ItemPlan plan,
            Dictionary<string, ZimensionItem> byId, Dictionary<string, (SplineCurve curve, Matrix4x4 world)> paths)
        {
            var go = zi.gameObject;
            var json = zi.Json;
            var area = LocalArea(zi.transform);
            float reach = e.Interaction || e.Vehicle || e.Weapon || e.Pickable || (e.Collectable && !e.AutoPickup) ? 0.2f : 0.02f;

            // Sounds.
            if (e.Sounds.Count > 0)
            {
                var holder = new GameObject("Sounds").transform;
                holder.SetParent(zi.transform, false);
                for (int c = 0; c < e.Sounds.Count; c++)
                {
                    var card = e.Sounds[c];
                    var clip = zi.sounds != null && card.Sound >= 0 && card.Sound < zi.sounds.Length ? zi.sounds[card.Sound] : null;
                    var source = SoundSource(holder, c, clip, card, positional: true);
                    if (plan != null && c < plan.SoundFinished.Count && plan.SoundFinished[c]) source.gameObject.AddComponent<SoundFinished>().source = source;
                    if (!clip) ctx.Report.Add(ImportReport.Level.Approximated, $"Sound card {c} has no audio file in the export; it plays nothing", "soundTriggers", json);
                }
            }

            // Animation cards: an Animator Controller on the model, ProceduralMotion for the built-in ones.
            var states = LogicGenerator.AnimationStates(e, out var rest);
            var behaviour = json.Obj("characterBehavior");
            bool npc = behaviour.Truthy("enable") && behaviour["behaviors"].Count > 0 && zi.modelRoot;
            if ((states.Any(s => s != null) || npc) && zi.modelRoot)
                BuildModelAnimator(ctx, zi, e, states, rest, plan != null && plan.AnimationEvents, npc ? NpcStates : null);
            if (npc) BuildBehaviour(ctx, zi, json, behaviour, area);
            for (int c = 0; c < e.Animations.Count; c++)
                if (states[c] == null) AddProcedural(zi, e.Animations[c]);

            // Transitions: baked clips on an Animator on the item itself.
            if (e.Transitions.Count > 0) BuildTransitions(ctx, zi, e, plan != null && plan.TransitionEvents, paths);

            if (e.Interaction)
            {
                var interactable = go.AddComponent<Interactable>();
                interactable.prompt = string.IsNullOrEmpty(e.InteractionName) ? "Interact" : e.InteractionName;
                interactable.cooldown = Mathf.Max(e.InteractionTime, 0.0001f);
                interactable.startEnabled = EventValue.Trim(e.EnableEvent).Length == 0;
                interactable.area = area; interactable.reach = reach;
            }
            if (e.EnterEvent.Length > 0 || e.LeaveEvent.Length > 0)
            {
                var zone = go.AddComponent<PlayerZone>();
                zone.area = area; zone.reach = reach;
            }
            if (e.Collectable)
            {
                var collect = go.AddComponent<Collectable>();
                collect.autoPickup = e.AutoPickup; collect.inventory = e.Inventory; collect.displayName = e.Name;
                collect.area = area; collect.reach = reach;
                collect.collectSound = Sound("collect");
            }
            foreach (var trigger in e.ItemTriggers)
            {
                var contact = go.AddComponent<ItemContact>();
                if (byId.TryGetValue(trigger.Target, out var other)) contact.other = other.transform;
                else ctx.Report.Add(ImportReport.Level.Approximated, "Item trigger target is not in this scene", "itemInteractionTriggers", json);
            }
            if (e.Weapon) BuildWeapon(zi, json, area, reach);
            if (e.Vehicle) BuildVehicle(ctx, zi, json, area, reach);
            var damage = json.Obj("damageSetting");
            if (damage.Truthy("enable"))
            {
                var health = go.AddComponent<Health>();
                health.maxHealth = health.health = Mathf.Max(1, damage.Float("health", 100));
                health.showHealthBar = damage.Truthy("showHealthBar");
                health.healthBarColor = CssStyle.ParseColor(damage.Str("healthBarColor", "#ff0000"), Color.red);
                health.disappearOnDeath = damage.Truthy("disappearOnDeath");
                health.disappearDelay = damage.Float("disappearDelay", 2);
                health.respawn = damage.Truthy("respawnEnabled");
                health.respawnDelay = damage.Float("respawnDelay", 5);
                health.respawnLimit = (int)damage.Num("respawnLimit", 0);
                health.randomRespawnPoint = damage.Flag("respawnRandom", true);
                // Something to hit: a box collider when the model has none.
                if (!go.GetComponentInChildren<Collider>(true)) { var box = go.AddComponent<BoxCollider>(); box.center = area.center; box.size = area.size; }
            }
            if (e.Variables.Count > 0)
            {
                var locals = go.AddComponent<LocalVariables>();
                foreach (var v in e.Variables) locals.variables.Add(new LocalVariables.Variable { name = v.Name, value = EventValue.Display(v.Current) });
            }
            if (e.Pickable) ctx.Report.Add(ImportReport.Level.Unsupported, "Carrying props (pickable) is not converted yet", "pickable", json);
            if (json["characterBehaviors"].Count > 0 || json.Obj("characterBehavior").IsObject)
                ctx.Report.Add(ImportReport.Level.Unsupported, "NPC behaviours (follow, patrol, chase) are not converted yet; the character stands and plays its animation cards", "characterBehaviors", json);
        }

        static AudioSource SoundSource(Transform holder, int index, AudioClip clip, EventCard card, bool positional)
        {
            var child = new GameObject($"Sound {index}" + (clip ? $" ({clip.name})" : ""));
            child.transform.SetParent(holder, false);
            var source = child.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.clip = clip;
            source.loop = card.Loop;
            float volume = card.Source.Float("volume", 1);
            source.volume = Mathf.Clamp01(float.IsNaN(volume) ? 1 : volume);
            source.spatialBlend = positional ? 1 : 0;
            if (positional) { source.rolloffMode = AudioRolloffMode.Logarithmic; source.minDistance = 4; source.maxDistance = 180; source.dopplerLevel = 0; }
            return source;
        }

        // ---- Animation ---------------------------------------------------------------------------

        static int ResolveClip(ZimensionItem zi, JsonNode card)
        {
            var clips = zi.clips;
            if (clips == null || clips.Length == 0) return -1;
            var name = card.Str("animationName");
            if (!string.IsNullOrEmpty(name))
            {
                var names = zi.clipNames;
                if (names != null) for (int i = 0; i < names.Length && i < clips.Length; i++) if (clips[i] && names[i] == name) return i;
                for (int i = 0; i < clips.Length; i++) if (clips[i] && clips[i].name == name) return i;
                for (int i = 0; i < clips.Length; i++) if (clips[i] && clips[i].name.StartsWith(name + " [", StringComparison.Ordinal)) return i;
                return -1;
            }
            int index = (int)card.Num("animationIndex", -1);
            return index >= 0 && index < clips.Length && clips[index] ? index : -1;
        }

        static readonly Dictionary<(AnimationClip, bool), AnimationClip> s_LoopCopies = new Dictionary<(AnimationClip, bool), AnimationClip>();

        /// <summary>The clip with the wanted looping (a copy saved beside the controllers when it differs).</summary>
        static AnimationClip WithLoop(ZimensionImporter.Context ctx, AnimationClip clip, bool loop)
        {
            if (!clip) return null;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.loopTime == loop) return clip;
            if (s_LoopCopies.TryGetValue((clip, loop), out var copy) && copy) return copy;
            copy = Object.Instantiate(clip);
            copy.name = clip.name + (loop ? " (loop)" : " (once)");
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(copy, settings);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{Folder(ctx, "Animation/Clips")}/{ZimensionImporter.SafeName(copy.name)}.anim");
            AssetDatabase.CreateAsset(copy, path);
            s_LoopCopies[(clip, loop)] = copy;
            return copy;
        }

        static AnimatorController NewController(ZimensionImporter.Context ctx, string name)
        {
            var path = AssetDatabase.GenerateUniqueAssetPath($"{Folder(ctx, "Animation")}/{ZimensionImporter.SafeName(name)}.controller");
            return AnimatorController.CreateAnimatorControllerAtPath(path);
        }

        /// <summary>The locomotion states a character with behaviours needs: state, candidate clip names, loops.</summary>
        static readonly (string state, string[] names, bool loop)[] NpcStates =
        {
            ("Idle", new[] { "Idle", "stand_idle" }, true), ("Walk", new[] { "Walk", "walk_forward" }, true),
            ("Run", new[] { "Run", "running_forward", "Walk" }, true), ("Right_Hook", new[] { "Right_Hook", "Attack", "Attack_1" }, false),
            ("Dying", new[] { "Dying", "Death", "Die" }, false),
        };

        static bool IsCharacter(GameObject model) => model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(r => r.bones != null && r.bones.Length >= 10);

        /// <summary>The clip as authored (a glTF/FBX sub-asset): bound copies are traced back to the library clip of the same name.</summary>
        static AnimationClip OriginalOf(ZimensionImporter.Context ctx, AnimationClip clip)
        {
            if (!clip) return null;
            var path = AssetDatabase.GetAssetPath(clip).ToLowerInvariant();
            if (path.EndsWith(".glb") || path.EndsWith(".gltf") || path.EndsWith(".fbx")) return clip;
            return ctx.LibraryClips.FirstOrDefault(c => c && c.name == clip.name);
        }

        /// <summary>A character's clip by name: its own or card clips first, then the matching animation library.</summary>
        static AnimationClip ClipNamed(ZimensionImporter.Context ctx, ZimensionItem zi, IEnumerable<string> names)
        {
            var map = zi.Json.Obj("characterAnimationMap");
            var wanted = names.Select(n => map.Str(n, n)).Concat(names).Distinct().ToList();
            for (int i = 0; zi.clips != null && i < zi.clips.Length; i++)
                if (zi.clips[i] && wanted.Any(w => w == zi.clipNames[i] || w == zi.clips[i].name || ZimensionImporter.NormalizeClipName(w) == ZimensionImporter.NormalizeClipName(zi.clips[i].name))) return zi.clips[i];
            return ZimensionImporter.FindBestClip(ctx, wanted, zi.modelRoot, new List<AnimationClip>(), null, zi.itemName);
        }

        /// <summary>
        /// The item's Animator Controller: a state per animation card (named as the generated logic
        /// plays them), plus locomotion states for characters with behaviours. Humanoid characters get
        /// a Humanoid Avatar and muscle-space clips (Unity retargeting); others keep bone-bound clips.
        /// </summary>
        static void BuildModelAnimator(ZimensionImporter.Context ctx, ZimensionItem zi, EventItem e, List<string> states, string rest, bool events,
            (string state, string[] names, bool loop)[] extra)
        {
            var target = zi.modelRoot.gameObject;
            // Every state: name, clip, loop, hold, speed, reports finished.
            var wantedStates = new List<(string name, AnimationClip clip, bool loop, bool hold, float speed, bool finished)>();
            for (int c = 0; c < e.Animations.Count; c++)
            {
                if (states[c] == null) continue;
                var card = e.Animations[c].Source;
                int index = ResolveClip(zi, card);
                var clip = index >= 0 ? zi.clips[index] : null;
                if (!clip) ctx.Report.Add(ImportReport.Level.Approximated, $"Animation '{states[c]}' is not in the model or a matching library; its Animator state is empty", "animations", zi.Json);
                else if (clip.legacy) { ctx.Report.Add(ImportReport.Level.Approximated, $"Animation '{clip.name}' is a Legacy clip", "animations", zi.Json); clip = null; }
                float speed = card.Float("speed", 1);
                wantedStates.Add((states[c], clip, card.Flag("loop", e.Animations[c].Playmode == "auto"), card.Truthy("clampOnFinish"), Mathf.Approximately(speed, 0) ? 1 : speed, events));
            }
            if (extra != null)
                foreach (var (state, names, loop) in extra)
                {
                    if (wantedStates.Any(w => w.name == state)) continue;
                    var clip = ClipNamed(ctx, zi, names);
                    if (!clip) { ctx.Report.Add(ImportReport.Level.Approximated, $"Character has no '{state}' animation; its behaviour skips it", "characterBehavior", zi.Json); continue; }
                    wantedStates.Add((state, clip, loop, state == "Dying", 1, false));
                }
            if (extra != null && rest == "Rest" && wantedStates.Any(w => w.name == "Idle" && w.clip)) rest = "Idle";

            // Humanoid retargeting for characters: every clip must convert, else the bone-bound clips stay.
            bool humanoid = false;
            var humanoidClips = new Dictionary<string, AnimationClip>();
            if (IsCharacter(target) && wantedStates.Any(w => w.clip))
            {
                var folder = HumanoidRetarget.Folder(ctx);
                var previous = target.GetComponent<Animator>() ? target.GetComponent<Animator>().avatar : null;
                if (HumanoidRetarget.MakeHumanoid(ctx, target, folder, zi.Json))
                {
                    humanoid = true;
                    foreach (var w in wantedStates)
                    {
                        if (!w.clip) continue;
                        var converted = HumanoidRetarget.Humanoid(OriginalOf(ctx, w.clip), folder);
                        if (!converted) { humanoid = false; break; }
                        humanoidClips[w.name] = converted;
                    }
                    if (!humanoid)
                    {
                        target.GetComponent<Animator>().avatar = previous;
                        ctx.Report.Add(ImportReport.Level.Approximated, $"Some animations of '{zi.itemName}' come from a non-humanoid skeleton; bone-name binding is used", "animations", zi.Json);
                    }
                }
            }

            var controller = NewController(ctx, zi.itemName + " Animations");
            var machine = controller.layers[0].stateMachine;
            var restState = machine.AddState("Rest");
            restState.writeDefaultValues = false;
            var byName = new Dictionary<string, AnimatorState>(StringComparer.Ordinal) { ["Rest"] = restState };
            foreach (var w in wantedStates)
            {
                var state = machine.AddState(w.name);
                state.writeDefaultValues = false;
                byName[w.name] = state;
                if (w.clip) state.motion = WithLoop(ctx, humanoid && humanoidClips.TryGetValue(w.name, out var h) ? h : w.clip, w.loop);
                state.speed = w.speed;
                if (w.finished) { var b = state.AddStateMachineBehaviour<StateFinished>(); b.stateName = w.name; }
            }
            var home = byName.TryGetValue(rest, out var r) ? r : restState;
            machine.defaultState = home;
            // A one-shot that does not hold its last pose returns to the resting state.
            foreach (var w in wantedStates)
            {
                if (w.loop || w.hold || byName[w.name] == home) continue;
                var back = byName[w.name].AddTransition(home);
                back.hasExitTime = true; back.exitTime = 1; back.duration = 0.25f; back.hasFixedDuration = true;
            }
            var animator = target.GetComponent<Animator>() ?? target.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (events && !target.GetComponent<AnimationEvents>()) target.AddComponent<AnimationEvents>();
        }

        /// <summary>characterBehavior: a CharacterController and CharacterBehaviour on the item, its own colliders replaced.</summary>
        static void BuildBehaviour(ZimensionImporter.Context ctx, ZimensionItem zi, JsonNode json, JsonNode config, Bounds area)
        {
            var go = zi.gameObject;
            foreach (var c in go.GetComponents<Collider>()) Object.DestroyImmediate(c);
            foreach (var c in zi.modelRoot.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            var controller = go.AddComponent<CharacterController>();
            // The capsule in the item's own (scaled) space: the model's footprint and height.
            float scale = Mathf.Max(1e-4f, go.transform.lossyScale.y);
            float height = Mathf.Max(0.5f, area.size.y);
            controller.height = height;
            controller.radius = Mathf.Clamp(Mathf.Min(area.size.x, area.size.z) * 0.35f, 0.1f, height / 2);
            controller.center = new Vector3(area.center.x, area.min.y + height / 2 + controller.skinWidth / scale, area.center.z);
            controller.stepOffset = Mathf.Min(0.3f / scale, height * 0.3f);
            controller.slopeLimit = 50;
            var b = go.AddComponent<CharacterBehaviour>();
            b.animator = zi.modelRoot.GetComponent<Animator>();
            b.walkSpeed = Mathf.Clamp(config.Float("walkSpeed", 1.8f), 0.01f, 30);
            b.runSpeed = Mathf.Clamp(config.Float("runSpeed", 4.5f), 0.01f, 40);
            if (AvatarMeasure.Forward(zi.modelRoot, out var forward)) b.modelYawOffset = -Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            var damage = json.Obj("damageSetting");
            foreach (var j in config["behaviors"].Items)
            {
                if (!j.IsObject) continue;
                var kind = j.Str("behaviorType", "idle");
                var behaviour = new CharacterBehaviour.Behaviour
                {
                    kind = kind == "follow" ? CharacterBehaviour.Kind.Follow : kind == "attack" ? CharacterBehaviour.Kind.Attack : kind == "shoot" ? CharacterBehaviour.Kind.Shoot :
                           kind == "patrol" ? CharacterBehaviour.Kind.Patrol : CharacterBehaviour.Kind.Idle,
                    activateEvent = EventValue.Trim(j.Str("activateEvent")), deactivateEvent = EventValue.Trim(j.Str("deactivateEvent")),
                    runWhileChasing = j.Flag("runWhileChasing", true),
                    attackImpactDelay = Mathf.Clamp(j.Float("attackImpactDelay", 0.5f), 0, 30),
                    attackDamage = Mathf.Max(0, j.Float("attackDamage", 10)),
                    difficulty = j.Str("difficulty", "medium") == "easy" ? CharacterBehaviour.Difficulty.Easy : j.Str("difficulty", "medium") == "hard" ? CharacterBehaviour.Difficulty.Hard : CharacterBehaviour.Difficulty.Medium,
                    aimReactionDelay = Mathf.Max(0, j.Float("aimReactionDelay", 0.22f)),
                    loop = j.Flag("loop", true), useRun = j.Truthy("useRun"), waitAtPoint = Mathf.Max(0, j.Float("waitAtPoint", 0)),
                };
                foreach (var point in j["patrolPoints"].Items)
                {
                    var p = point.Obj("position").IsObject ? point.Obj("position") : point;
                    behaviour.patrolPoints.Add(new Vector3(-p.Float("x", 0), p.Float("y", 0), p.Float("z", 0)));
                }
                if (behaviour.kind == CharacterBehaviour.Kind.Shoot && j.Str("weaponItemId").Length == 0)
                    ctx.Report.Add(ImportReport.Level.Approximated, "Shoot behaviour has no weapon item; it fires with default damage", "characterBehavior", json);
                if (!new[] { "idle", "follow", "attack", "shoot", "patrol" }.Contains(kind))
                    ctx.Report.Add(ImportReport.Level.Approximated, $"Character behaviour '{kind}' is converted as Idle", "characterBehavior", json);
                b.behaviours.Add(behaviour);
            }
            s_PendingWeapons.Add((b, config));
        }

        static readonly List<(CharacterBehaviour behaviour, JsonNode config)> s_PendingWeapons = new List<(CharacterBehaviour, JsonNode)>();

        /// <summary>Shoot behaviours name their weapon item by id: resolved once every item is converted.</summary>
        static void ResolveBehaviourWeapons(Dictionary<string, ZimensionItem> byId)
        {
            foreach (var (b, config) in s_PendingWeapons)
            {
                if (!b) continue;
                int k = 0;
                foreach (var j in config["behaviors"].Items)
                {
                    if (!j.IsObject) continue;
                    if (k < b.behaviours.Count && byId.TryGetValue(j.Str("weaponItemId"), out var weapon)) b.behaviours[k].weapon = weapon.GetComponent<Weapon>();
                    k++;
                }
            }
            s_PendingWeapons.Clear();
        }

        static void AddProcedural(ZimensionItem zi, EventCard card)
        {
            var settings = card.Source.Obj("proceduralSettings");
            var motion = zi.gameObject.AddComponent<ProceduralMotion>();
            motion.target = zi.modelRoot ? zi.modelRoot : zi.transform;
            var type = settings.Str("type", card.Source.Str("animationName", "spin")).ToLowerInvariant();
            motion.kind = type == "hover" ? ProceduralMotion.Kind.Hover : type == "float" ? ProceduralMotion.Kind.Float : type == "pulse" ? ProceduralMotion.Kind.Pulse :
                type == "swing" ? ProceduralMotion.Kind.Swing : type == "wobble" ? ProceduralMotion.Kind.Wobble : type == "bounce" ? ProceduralMotion.Kind.Bounce :
                type == "shake" ? ProceduralMotion.Kind.Shake : type == "breathing" ? ProceduralMotion.Kind.Breathing : type == "tiltloop" ? ProceduralMotion.Kind.TiltLoop : ProceduralMotion.Kind.Spin;
            var defaults = new (float speed, float intensity, int axis)[] { (0.5f, 1, 1), (0.6f, 0.25f, 1), (0.35f, 0.2f, 1), (1.2f, 0.15f, 1), (0.7f, 20, 2), (1.6f, 8, 1), (1, 0.4f, 1), (6, 0.05f, 1), (0.25f, 0.05f, 1), (0.4f, 1, 2) }[(int)motion.kind];
            var axis = settings.Str("axis");
            int a = axis == "x" ? 0 : axis == "y" ? 1 : axis == "z" ? 2 : defaults.axis;
            motion.axis = (ProceduralMotion.Axis)a;
            motion.speed = Mathf.Clamp(settings.Float("speed", defaults.speed), 0.05f, 10);
            motion.intensity = Mathf.Clamp(settings.Float("intensity", defaults.intensity), 0, 10);
            motion.startDelay = Mathf.Clamp(settings.Float("startDelay", 0), 0, 10);
            motion.tiltFrom = settings.Float("tiltFrom", -15); motion.tiltTo = settings.Float("tiltTo", 15);
            motion.loop = settings.Flag("loop", true);
            motion.restoreOnStop = settings.Flag("restoreOnStop", true);
            motion.playOnStart = card.Playmode == "auto" || (card.Playmode == "event" && EventValue.Trim(card.Event) == "on_start");
            // three is right-handed: turning about Y or Z, and moving along X, mirror into Unity.
            bool rotates = motion.kind == ProceduralMotion.Kind.Spin || motion.kind == ProceduralMotion.Kind.Swing || motion.kind == ProceduralMotion.Kind.TiltLoop;
            if (rotates && a != 0) { motion.intensity = -motion.intensity; }
            if (!rotates && a == 0 && (motion.kind == ProceduralMotion.Kind.Hover || motion.kind == ProceduralMotion.Kind.Bounce)) motion.intensity = -motion.intensity;
        }

        // ---- Transitions -------------------------------------------------------------------------

        static float EaseInOutCubic(float t) => t < 0.5f ? 4 * t * t * t : 1 - Mathf.Pow(-2 * t + 2, 3) / 2;

        sealed class Track
        {
            public readonly AnimationCurve PX = new AnimationCurve(), PY = new AnimationCurve(), PZ = new AnimationCurve();
            public readonly AnimationCurve RX = new AnimationCurve(), RY = new AnimationCurve(), RZ = new AnimationCurve(), RW = new AnimationCurve();
            public readonly AnimationCurve SX = new AnimationCurve(), SY = new AnimationCurve(), SZ = new AnimationCurve();
            Quaternion m_Last = Quaternion.identity; bool m_Any;

            public void Key(float time, Vector3 p, Quaternion r, Vector3 s)
            {
                if (m_Any && Quaternion.Dot(m_Last, r) < 0) r = new Quaternion(-r.x, -r.y, -r.z, -r.w);
                m_Last = r; m_Any = true;
                PX.AddKey(time, p.x); PY.AddKey(time, p.y); PZ.AddKey(time, p.z);
                RX.AddKey(time, r.x); RY.AddKey(time, r.y); RZ.AddKey(time, r.z); RW.AddKey(time, r.w);
                SX.AddKey(time, s.x); SY.AddKey(time, s.y); SZ.AddKey(time, s.z);
            }

            public AnimationClip ToClip(string name, bool loop)
            {
                var clip = new AnimationClip { name = name, frameRate = 30 };
                void Set(string property, AnimationCurve curve)
                {
                    for (int k = 0; k < curve.length; k++)
                    {
                        AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
                        AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
                    }
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Transform), property), curve);
                }
                Set("m_LocalPosition.x", PX); Set("m_LocalPosition.y", PY); Set("m_LocalPosition.z", PZ);
                Set("m_LocalRotation.x", RX); Set("m_LocalRotation.y", RY); Set("m_LocalRotation.z", RZ); Set("m_LocalRotation.w", RW);
                Set("m_LocalScale.x", SX); Set("m_LocalScale.y", SY); Set("m_LocalScale.z", SZ);
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = loop;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                return clip;
            }
        }

        static void Segment(Track track, ref float time, ZimensionPose from, ZimensionPose to, float duration, bool smooth)
        {
            int samples = smooth ? Mathf.Clamp(Mathf.CeilToInt(duration * 30), 2, 600) : 1;
            for (int k = 1; k <= samples; k++)
            {
                float u = (float)k / samples, e = smooth ? EaseInOutCubic(u) : u;
                track.Key(time + duration * u, Vector3.Lerp(from.position, to.position, e), Quaternion.Slerp(from.rotation, to.rotation, e), Vector3.Lerp(from.scale, to.scale, e));
            }
            time += duration;
        }

        static AnimationClip BakeSteps(EventTransition tr, List<ZimensionPose> poses, string name)
        {
            var track = new Track();
            int count = poses.Count;
            int At(int step) => Math.Min(tr.States[step], count - 1);
            float time = 0;
            var start = poses[At(0)];
            track.Key(0, start.position, start.rotation, start.scale);
            int steps = tr.States.Count;
            for (int k = 0; k + 1 < steps; k++)
            {
                float d = tr.Durations[k];
                if (k == 0 && !(d > 0)) d = 1; // the first step's duration is read with `|| 1`
                Segment(track, ref time, poses[At(k)], poses[At(k + 1)], Mathf.Max(0, d), tr.Smooth[k]);
            }
            if (tr.Reverse)
                for (int k = steps - 1; k > 0; k--)
                    Segment(track, ref time, poses[At(k)], poses[At(k - 1)], Mathf.Max(0, tr.Durations[k - 1]), tr.Smooth[k - 1]);
            if (time <= 0) track.Key(1f / 30, start.position, start.rotation, start.scale);
            return track.ToClip(name, tr.Card.Loop);
        }

        static AnimationClip BakePath(EventTransition tr, ZimensionPose home, SplineCurve curve, Matrix4x4 world, string name)
        {
            var track = new Track();
            float length = curve.Length, speed = Mathf.Max(0.01f, tr.PathSpeed);
            float duration = length / speed;
            void Sample(float time, float distance, int direction)
            {
                float t = curve.UToT(Mathf.Clamp01(distance / length));
                var point = world.MultiplyPoint3x4(curve.Point(t).ToUnity());
                var rotation = home.rotation;
                if (tr.Align)
                {
                    var z = world.MultiplyVector(curve.Tangent(t).ToUnity());
                    if (direction < 0) z = -z;
                    if (z.sqrMagnitude > 1e-6f) rotation = Quaternion.LookRotation(z.normalized, Vector3.up);
                }
                track.Key(time, point, rotation, home.scale);
            }
            int samples = Mathf.Clamp(Mathf.CeilToInt(duration * 30), 2, 3000);
            bool reverse = tr.PathReverse;
            for (int k = 0; k <= samples; k++) Sample(duration * k / samples, reverse ? length * (1 - (float)k / samples) : length * k / samples, reverse ? -1 : 1);
            if (tr.Reverse)
                for (int k = 1; k <= samples; k++) Sample(duration + duration * k / samples, reverse ? length * k / samples : length * (1 - (float)k / samples), reverse ? 1 : -1);
            return track.ToClip(name, tr.Card.Loop);
        }

        static void BuildTransitions(ZimensionImporter.Context ctx, ZimensionItem zi, EventItem e, bool events, Dictionary<string, (SplineCurve curve, Matrix4x4 world)> paths)
        {
            var poses = zi.states != null && zi.states.Count > 0 ? zi.states : new List<ZimensionPose> { ZimensionPose.FromTransform(zi.transform) };
            var names = LogicGenerator.TransitionStates(e, out var rest);
            var controller = NewController(ctx, zi.itemName + " Transitions");
            var machine = controller.layers[0].stateMachine;
            var restState = machine.AddState("Rest");
            restState.writeDefaultValues = false;
            machine.defaultState = restState;
            var clipFolder = Folder(ctx, "Animation/Transitions");
            for (int t = 0; t < e.Transitions.Count; t++)
            {
                var tr = e.Transitions[t];
                AnimationClip clip = null;
                if (tr.Path)
                {
                    if (paths.TryGetValue(tr.PathItem, out var path)) clip = BakePath(tr, poses[0], path.curve, path.world, names[t]);
                    else ctx.Report.Add(ImportReport.Level.Approximated, $"Path transition '{names[t]}' follows a spline that is not in this scene", "transitions", zi.Json);
                }
                else if (tr.States.Count > 0) clip = BakeSteps(tr, poses, names[t]);
                var state = machine.AddState(names[t]);
                state.writeDefaultValues = false;
                if (clip)
                {
                    clip.name = $"{zi.itemName} - {names[t]}";
                    AssetDatabase.CreateAsset(clip, AssetDatabase.GenerateUniqueAssetPath($"{clipFolder}/{ZimensionImporter.SafeName(clip.name)}.anim"));
                    state.motion = clip;
                }
                if (events) { var b = state.AddStateMachineBehaviour<StateFinished>(); b.stateName = names[t]; }
                if (names[t] == rest) machine.defaultState = state;
            }
            var animator = zi.gameObject.GetComponent<Animator>() ?? zi.gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (events && !zi.GetComponent<AnimationEvents>()) zi.gameObject.AddComponent<AnimationEvents>();
        }

        // ---- Weapons and vehicles ----------------------------------------------------------------

        static JsonNode Merged(JsonNode json, string key)
        {
            var own = json.Obj(key);
            var library = json.Obj("libraryItem").Obj(key);
            var merged = JsonNode.NewObject();
            if (library.IsObject) foreach (var m in library.Members) merged.Set(m.Key, m.Value);
            if (own.IsObject) foreach (var m in own.Members) merged.Set(m.Key, m.Value);
            return merged;
        }

        static void BuildWeapon(ZimensionItem zi, JsonNode json, Bounds area, float reach)
        {
            var s = Merged(json, "fireWeaponSettings");
            var w = zi.gameObject.AddComponent<Weapon>();
            w.area = area; w.reach = reach;
            w.weaponName = string.IsNullOrEmpty(zi.itemName) ? s.Str("name", "Weapon") : zi.itemName;
            w.type = s.Str("weaponType", "gun") == "melee" ? Weapon.WeaponType.Melee : Weapon.WeaponType.Gun;
            w.weaponClass = s.Str("weaponClass", "rifle");
            w.damage = s.Float("damage", 10);
            w.range = Mathf.Max(1, s.Float("range", 100));
            w.fireRate = Mathf.Max(0.1f, s.Float("fireRate", 10));
            w.capacity = Mathf.Max(1, (int)s.Num("capacity", 20));
            w.automatic = s.Truthy("automatic");
            w.accuracy = Mathf.Clamp(s.Float("accuracy", 80), 0, 100);
            w.reloadTime = Mathf.Max(0, s.Float("reloadTime", 2));
            w.recoil = s["recoil"].IsNumber ? (float)s["recoil"].AsNumber : 3;
            w.recoilAffectsCamera = s.Flag("recoilAffectsCamera", true);
            w.adsZoom = Mathf.Max(1, s.Float("adsZoom", 1.3f));
            w.infiniteAmmo = s.Truthy("infiniteAmmo");
            w.initialAmmo = Mathf.Max(0, (int)s.Num("initialAmmo", 30));
            w.bulletColor = CssStyle.ParseColor(s.Str("bulletColor", "#ff0000"), Color.red);
            w.fireSound = Sound(w.type == Weapon.WeaponType.Gun ? "gun-shot" : "hit"); w.reloadSound = Sound("reload");
            // The muzzle: gunFireTransform in the weapon's own (three) space.
            if (Coordinates.TryMatrixFromThreeElements(s["gunFireTransform"], out var fire))
            {
                var muzzle = new GameObject("Muzzle").transform;
                muzzle.SetParent(zi.transform, false);
                UnityMath.ApplyLocal(muzzle, Coordinates.ThreeToUnity(fire));
                w.muzzle = muzzle;
            }
            // pickTransformation: the pose in the hand's grip frame.
            var pick = json.Obj("pickTransformation");
            if (Coordinates.TryMatrixFromThreeElements(pick["transformation"], out var hold))
            {
                var pose = ZimensionPose.FromMatrix(Coordinates.ThreeToUnity(hold));
                w.holdPosition = pose.position; w.holdEuler = pose.rotation.eulerAngles;
            }
            else if (pick.IsObject)
            {
                var p = pick.Obj("position"); var r = pick.Obj("rotation");
                w.holdPosition = new Vector3(-p.Float("x", 0), p.Float("y", 0), p.Float("z", 0)) * (0.01f / 2.2f);
                var q = Coordinates.ThreeToUnityRotation(Coordinates.EulerToQuaternion(r.Float("x", 0), r.Float("y", 0), r.Float("z", 0))).ToUnity();
                w.holdEuler = q.eulerAngles;
            }
        }

        static Transform[] Named(Transform root, string list)
        {
            var names = (list ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
            var all = root.GetComponentsInChildren<Transform>(true);
            return names.Select(n => all.FirstOrDefault(t => t.name == n)).Where(t => t).ToArray();
        }

        static void BuildVehicle(ZimensionImporter.Context ctx, ZimensionItem zi, JsonNode json, Bounds area, float reach)
        {
            var s = Merged(json, "vehicleSettings");
            var v = zi.gameObject.AddComponent<Vehicle>();
            v.area = area; v.reach = reach;
            v.enterLabel = s.Str("interactionLabel", "Enter Vehicle");
            v.exitLabel = s.Str("exitLabel", "Exit Vehicle");
            v.enterMode = s.Str("enterMode", "hide") == "seat" ? Vehicle.EnterMode.Seat : Vehicle.EnterMode.Hide;
            v.maxSpeed = s.Float("maxSpeed", 14); v.reverseSpeed = s.Float("reverseSpeed", 5);
            v.maxAcceleration = s.Float("maxAcceleration", 18); v.brakeAcceleration = s.Float("brakeAcceleration", 24);
            v.steerSpeed = s.Float("steerSpeed", 2.4f); v.steerReturnSpeed = s.Float("steerReturnSpeed", 3.2f);
            v.maxSteerAngle = s.Float("maxSteerAngle", 0.55f);
            v.wheelRadius = Mathf.Max(0.05f, s.Float("wheelRadius", 0.35f));
            v.cameraDistance = s.Float("cameraDistance", 8); v.cameraHeight = s.Float("cameraHeight", 4);
            v.boostEnabled = s.Truthy("boostEnabled"); v.boostMultiplier = s.Float("boostMultiplier", 1.8f);
            v.boostDuration = s.Float("boostDuration", 3); v.boostRechargeTime = s.Float("boostRechargeTime", 4);
            // forwardAxis in the model's three space; X mirrors into Unity.
            switch (s.Str("forwardAxis", "z"))
            {
                case "-z": v.forwardAxis = Vehicle.ForwardAxis.NegativeZ; break;
                case "x": v.forwardAxis = Vehicle.ForwardAxis.NegativeX; break;
                case "-x": v.forwardAxis = Vehicle.ForwardAxis.PositiveX; break;
                default: v.forwardAxis = Vehicle.ForwardAxis.PositiveZ; break;
            }
            v.wheelBase = Mathf.Clamp(Mathf.Max(area.size.x, area.size.z) * 0.6f, 1, 8);
            v.wheels = Named(zi.transform, s.Str("wheelNames"));
            v.steeringWheels = Named(zi.transform, s.Str("steerWheelNames"));
            var spin = s.Str("wheelRotationAxis", s.Str("wheelSpinAxis", "x"));
            v.wheelSpinAxis = spin == "y" ? Vector3.up : spin == "z" ? Vector3.forward : Vector3.right;
            var seatName = s.Str("seatTargetName");
            if (seatName.Length > 0) v.seat = zi.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == seatName);
            var engine = new GameObject("Engine Sound").AddComponent<AudioSource>();
            engine.transform.SetParent(zi.transform, false);
            engine.clip = Sound("engine_idle_5"); engine.loop = true; engine.playOnAwake = false; engine.spatialBlend = 1;
            engine.volume = Mathf.Clamp01(s.Float("soundVolume", 0.5f)); engine.minDistance = 4; engine.maxDistance = 120;
            v.engineSound = engine;
            if (v.wheels.Length == 0) ctx.Report.Add(ImportReport.Level.Optional, "Vehicle has no wheel names set; it drives without spinning wheels", "vehicleSettings.wheelNames", json);
        }

        // ---- Player ------------------------------------------------------------------------------

        static PlayerController BuildPlayer(ZimensionImporter.Context ctx, JsonNode world, Transform spawn, Camera camera)
        {
            var go = new GameObject("Player");
            go.transform.SetPositionAndRotation(spawn.position, Quaternion.Euler(0, spawn.eulerAngles.y, 0));
            go.AddComponent<CharacterController>();
            var p = go.AddComponent<PlayerController>();
            var avatar = world.Obj("avatar");
            p.walkSpeed = Mathf.Clamp(2.8f * avatar.Float("walkSpeed", 1), 0, 50);
            p.runSpeed = Mathf.Clamp(6.8f * avatar.Float("sprintSpeed", 1), 0, 80);
            p.jumpVelocity = 6 * Mathf.Sqrt(Mathf.Clamp(avatar.Float("jumpHeight", 1), 0, 25));
            p.jumpEnabled = !avatar.Flag("disbaleJump", false);
            p.sprintEnabled = !avatar.Flag("disableSprint", false);
            p.autoSprint = avatar.Flag("autoSprint", false);
            p.thirdPerson = avatar.Str("cameraType") != "FPP";
            p.cameraDistance = Mathf.Clamp(avatar.Float("cameraDistance", 3), 0.2f, 30);
            p.cameraHeight = Mathf.Clamp(avatar.Float("cameraHeight", 1.8f), 0.2f, 10);
            p.playerCamera = camera;
            p.walkLoop = Sound("footstep_2"); p.runLoop = Sound("Runing_final"); p.jumpSound = Sound("jump");
            p.hitSound = Sound("hit"); p.dieSound = Sound("die-sound"); p.fallSound = Sound("Body_Fall"); p.inventorySound = Sound("inventory");
            var project = ctx.Project;
            if (project && project.avatarPrefab && p.thirdPerson)
            {
                var pivot = new GameObject("Avatar").transform;
                pivot.SetParent(go.transform, false);
                var model = (GameObject)PrefabUtility.InstantiatePrefab(project.avatarPrefab, pivot);
                model.transform.localPosition = Vector3.zero; model.transform.localRotation = Quaternion.identity;
                foreach (var c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                if (AvatarMeasure.Forward(model.transform, out var forward)) model.transform.localRotation = Quaternion.FromToRotation(forward, Vector3.forward);
                if (AvatarMeasure.VerticalExtent(model.transform, pivot, out var low, out var high) && high - low > 0.01f)
                {
                    float scale = Mathf.Clamp(project.avatarHeight, 0.2f, 10f) / (high - low);
                    model.transform.localScale *= scale;
                    model.transform.localPosition = new Vector3(0, -low * scale, 0);
                }
                p.avatar = pivot;
                var animator = model.GetComponent<Animator>() ?? model.AddComponent<Animator>();
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = PlayerAnimator(ctx, p, model);
                p.animator = animator;
            }
            return p;
        }

        static readonly (string state, string[] names, bool loop)[] PlayerStates =
        {
            ("Idle", new[] { "Idle" }, true), ("Walk", new[] { "Walk" }, true), ("Run", new[] { "Run" }, true),
            ("Walk_Backward", new[] { "Walk_Backward" }, true), ("Walk_Left", new[] { "Walk_Left" }, true), ("Walk_Right", new[] { "Walk_Right" }, true),
            ("Jump", new[] { "Jump" }, false), ("Jump_forward", new[] { "Jump_forward", "Jump" }, false),
            ("Falling_idle", new[] { "Falling_idle", "Falling_Idle" }, true), ("Falling", new[] { "Falling" }, true),
            ("Falling_To_Landing", new[] { "Falling_To_Landing" }, false), ("Dying", new[] { "Dying", "Death" }, false),
        };

        /// <summary>
        /// The player's Animator: Zimension's player states by name (Idle, Walk, Run, the strafe and back
        /// walks, Jump_forward, Falling_idle, Falling, Falling_To_Landing, Dying) that PlayerController
        /// cross-fades, plus a Locomotion blend tree on Speed. Humanoid characters use muscle-space clips.
        /// </summary>
        static AnimatorController PlayerAnimator(ZimensionImporter.Context ctx, PlayerController p, GameObject model)
        {
            var project = ctx.Project;
            var known = new Dictionary<string, AnimationClip> { ["Idle"] = project.idleClip, ["Walk"] = project.walkClip, ["Run"] = project.runClip, ["Jump"] = project.jumpClip };
            var clips = new List<(string state, AnimationClip clip, bool loop)>();
            foreach (var (state, names, loop) in PlayerStates)
            {
                var clip = known.TryGetValue(state, out var k) && k ? k : ZimensionImporter.FindBestClip(ctx, names.ToList(), model.transform, new List<AnimationClip>(), null, "Player");
                if (clip && !clip.legacy) clips.Add((state, clip, loop));
            }
            if (!clips.Any(c => c.state == "Idle" || c.state == "Walk")) { ctx.Report.Add(ImportReport.Level.Approximated, "The player character has no Idle/Walk/Run animations", "avatar"); return null; }
            var folder = HumanoidRetarget.Folder(ctx);
            var converted = new Dictionary<string, AnimationClip>();
            bool humanoid = IsCharacter(model) && HumanoidRetarget.MakeHumanoid(ctx, model, folder, ctx.World.Obj("avatar"));
            if (humanoid)
            {
                foreach (var c in clips)
                {
                    var h = HumanoidRetarget.Humanoid(OriginalOf(ctx, c.clip), folder);
                    if (!h) { humanoid = false; break; }
                    converted[c.state] = h;
                }
                if (!humanoid) model.GetComponent<Animator>().avatar = null;
            }
            AnimationClip Clip(string state) { var c = clips.FirstOrDefault(x => x.state == state); return c.clip ? WithLoop(ctx, humanoid ? converted[state] : c.clip, c.loop) : null; }
            var controller = NewController(ctx, "Player");
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            var parameters = controller.parameters;
            foreach (var param in parameters) if (param.name == "Grounded") param.defaultBool = true;
            controller.parameters = parameters;
            var machine = controller.layers[0].stateMachine;
            var locomotion = controller.CreateBlendTreeInController("Locomotion", out var tree, 0);
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            var idle = Clip("Idle"); var walk = Clip("Walk"); var run = Clip("Run");
            if (idle) tree.AddChild(idle, 0);
            if (walk) tree.AddChild(walk, p.walkSpeed);
            if (run) tree.AddChild(run, p.runSpeed);
            foreach (var c in clips)
            {
                var state = machine.AddState(c.state);
                state.motion = Clip(c.state);
                state.writeDefaultValues = true;
            }
            machine.defaultState = machine.states.FirstOrDefault(x => x.state.name == "Idle").state ?? locomotion;
            return controller;
        }

        static AudioClip Sound(string name) => Resources.Load<AudioClip>("ZimensionSounds/" + name);

        // ---- Wiring (after the logic script compiled) --------------------------------------------

        /// <summary>The scene object a generated field refers to.</summary>
        public static Object Resolve(FieldBinding b, Dictionary<string, GameObject> items, GameObject worldSounds)
        {
            if (b.itemId == "@worldsounds")
            {
                if (!worldSounds || b.index >= worldSounds.transform.childCount) return null;
                var child = worldSounds.transform.GetChild(b.index);
                return b.kind == "WorldSoundFinished" ? (Object)child.GetComponent<SoundFinished>() : child.GetComponent<AudioSource>();
            }
            if (!items.TryGetValue(b.itemId ?? "", out var go) || !go) return null;
            Transform SoundChild()
            {
                var holder = go.transform.Find("Sounds");
                return holder && b.index < holder.childCount ? holder.GetChild(b.index) : null;
            }
            Animator ModelAnimator() => go.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.gameObject != go);
            switch (b.kind)
            {
                case "Object": return go;
                case "Interactable": return go.GetComponent<Interactable>();
                case "PlayerZone": return go.GetComponent<PlayerZone>();
                case "Collectable": return go.GetComponent<Collectable>();
                case "Weapon": return go.GetComponent<Weapon>();
                case "Vehicle": return go.GetComponent<Vehicle>();
                case "Health": return go.GetComponent<Health>();
                case "Behaviour": return go.GetComponent<CharacterBehaviour>();
                case "ModelAnimator": return ModelAnimator();
                case "ModelEvents": { var a = ModelAnimator(); return a ? a.GetComponent<AnimationEvents>() : null; }
                case "TransitionAnimator": return go.GetComponent<Animator>();
                case "TransitionEvents": return go.GetComponent<AnimationEvents>();
                case "Sound": { var c = SoundChild(); return c ? c.GetComponent<AudioSource>() : null; }
                case "SoundFinished": { var c = SoundChild(); return c ? c.GetComponent<SoundFinished>() : null; }
                case "Procedural": { var all = go.GetComponents<ProceduralMotion>(); return b.index < all.Length ? all[b.index] : null; }
                case "Contact": { var all = go.GetComponents<ItemContact>(); return b.index < all.Length ? all[b.index] : null; }
                default: return null;
            }
        }
    }
}
