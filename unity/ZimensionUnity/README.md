# Zimension worlds in Unity

This project turns a Zimension world into a normal Unity project. After import, nothing interprets
Zimension data at runtime: every behaviour is a standard Unity component, an Animator Controller, an
AudioSource, a Terrain, or C# you can read and change. This guide explains where each Zimension
feature ended up and how to adjust it the Unity way.

- [1. Quick start](#1-quick-start)
- [2. What goes where](#2-what-goes-where)
- [3. How Zimension features map to Unity](#3-how-zimension-features-map-to-unity)
- [4. The player](#4-the-player)
- [5. Sky, light and ground](#5-sky-light-and-ground)
- [6. World items](#6-world-items)
- [7. World logic (events, flows, variables)](#7-world-logic-events-flows-variables)
- [8. Interactions, zones, collectables and contacts](#8-interactions-zones-collectables-and-contacts)
- [9. Animations and retargeting](#9-animations-and-retargeting)
- [10. Transitions (moving objects)](#10-transitions-moving-objects)
- [11. Sounds](#11-sounds)
- [12. Weapons](#12-weapons)
- [13. Vehicles](#13-vehicles)
- [14. Characters (NPC behaviours)](#14-characters-npc-behaviours)
- [15. Health and damage](#15-health-and-damage)
- [16. Instancing (copies of objects)](#16-instancing-copies-of-objects)
- [17. HUD, game flow and built-in events](#17-hud-game-flow-and-built-in-events)
- [18. Scenes](#18-scenes)
- [19. Re-importing and Pull latest](#19-re-importing-and-pull-latest)
- [20. What is not converted yet](#20-what-is-not-converted-yet)
- [21. Troubleshooting](#21-troubleshooting)

---

## 1. Quick start

1. Open the project in Unity 6 (any 6000.x version). On first open the Zimension package sets up the
   Universal Render Pipeline, linear colour and the input handling; let it finish.
2. Go to **Zimension > Import from Zimension**. Paste the world's export link (or code), or choose an
   exported `.zip` / folder under **From file**, and press **Import**.
3. The world's base scene opens. Wait for the Console line
   `Connected <World>Logic in <scene>`: Unity compiles the world's logic script first, then the importer
   connects the scene to it.
4. Press **Play**, then click **Play** on the start screen.

**Controls:** WASD to move, mouse to look (click the Game view to capture the mouse), Shift to run,
Space to jump, F to interact, enter or leave vehicles and pick up weapons or items, left mouse to fire,
right mouse to aim, R to reload, G to drop the weapon, Esc to pause. In vehicles: W/S to drive and
reverse, A/D to steer, Space for the handbrake, Shift to boost.

## 2. What goes where

| Folder | Contents | Re-import |
|---|---|---|
| `Assets/Zimension/SourceAssets/<World>/` | The world's models (glTF/GLB), textures, sounds, animation libraries, exactly as exported | Updated when a file changes; GUIDs are kept, so your references stay valid |
| `Assets/Zimension/Generated/<World>/` | Scenes, Animator Controllers, baked transition clips, Humanoid avatars and clips, terrain, materials, the import report | **Rewritten** on every import |
| `Assets/Zimension/Scripts/<World>/<World>Logic.Generated.cs` | The world's logic, generated | **Rewritten** on every import |
| `Assets/Zimension/Scripts/<World>/<World>Logic.cs` | Your half of the logic class | Created once, **never touched** again |
| `Assets/Zimension/UserContent/` | Put your own assets here | Never touched |
| `Packages/com.zimension.runtime/` | The Zimension Kit: the components every converted world uses | Updated with the package |

The import report is at `Assets/Zimension/Generated/<World>/export-report.json` and in the Import
window: it lists everything that was converted only approximately or not at all.

## 3. How Zimension features map to Unity

| Zimension | Unity |
|---|---|
| World item | A GameObject with a **World Item Id** component (its Zimension id) |
| Item transform, scale, customizations | The Transform; the model's materials and node adjustments |
| Collision box / object | BoxCollider / MeshColliders |
| Events, logic flows (trigger events, change variable, conditions, switch case), delays | Public methods in `<World>Logic` (`OpenDoor()`, `CheckFuseMounted()` ...), `if`/`else`, `After(seconds, ...)` |
| World variables | Typed properties on `<World>Logic` (`FuseMounted`, `Score` ...), editable in the Inspector |
| Item ("@") variables | **Local Variables** component on the item |
| Interaction ("Press F to ...") | **Interactable** + its `onInteract` UnityEvent |
| On Enter / On Leave range | **Player Zone** (`onPlayerEnter`, `onPlayerExit`) |
| Collectables, collect counter | **Collectable** (`onCollected`), the counter is a world variable |
| Item-to-item triggers | **Item Contact** (`onTouch`, `onSeparate`) |
| Animation cards | An **Animator Controller** per item: one state per card |
| Built-in animations (spin, hover, pulse ...) | **Procedural Motion** components |
| Transitions (states A → B → C), follow-path | Baked **Animation clips** on a second Animator on the item |
| Animation / transition / sound finished events | **Animation Events** (per state) and **Sound Finished** components |
| Sound cards | One **AudioSource** per card under the item's `Sounds` child |
| Weapons | **Weapon** component |
| Vehicles | **Vehicle** component |
| Character behaviours (idle, follow, attack, shoot, patrol) | **Character Behaviour** + CharacterController, NavMesh paths |
| Damage settings | **Health** component |
| Character animations / retargeting | Humanoid Avatars and muscle-space clips (Mecanim retargeting) |
| Instance groups | Copies of the source model under `Instances - <group>` |
| Ground, ground sculpting, painted ground, ground material | A Unity **Terrain** with Terrain Layers |
| Sky / environment image (EXR, HDR, PNG) | A Skybox material (and the scene's ambient light) |
| Default UI (prompts, messages, health, ammo, waypoint, screens) | **Game HUD** |
| Built-in events (reset_world, game_over_win ...), pause, death | **Game Manager** |
| Scenes and scene switches | Unity scenes in Build Settings, `Game.LoadScene(...)` |

Every Kit component is under **Add Component > Zimension**, documented in its tooltips, and works in
any scene, including scenes you make yourself.

## 4. The player

Select **Player** in the Hierarchy.

- **Player Controller**: walk speed (2.8 m/s × the world's walk speed), run speed (6.8 m/s ×
  sprint speed), jump velocity (6 × √jump height), gravity (1 = 9.81 m/s², as in Zimension),
  terminal fall speed, first/third person, camera distance and height, mouse sensitivity, health,
  and the footstep, jump, hit and death sounds. Its `onDamaged` and `onDied` events are in the Inspector.
- **Character**: `Player/Avatar/<model>`. To use another character, drop your model under
  `Player/Avatar`, delete the old one, and set the controller's **Avatar** and **Animator** fields.
- **Animations**: the model's Animator uses `Generated/<World>/Animation/Player.controller`, with
  states named as Zimension's player: `Idle`, `Walk`, `Run`, `Walk_Backward`, `Walk_Left`, `Walk_Right`,
  `Jump_forward`, `Falling_idle`, `Falling`, `Falling_To_Landing`, `Dying`, plus a `Locomotion` blend
  tree on the `Speed` parameter and a `Grounded` parameter. The controller cross-fades these states by
  name. Replace a clip by dragging another onto the state's **Motion**, or add a missing state with
  the same name.
- **Holding weapons**: weapons attach to the right-hand bone (found by name; set **Right Hand** to
  override) in Zimension's grip frame.
- **Spawn**: the player starts where it is placed; move the **Player** object to change it.

## 5. Sky, light and ground

- **Sun**: the `Sun` directional light (colour, intensity, shadows).
- **Sky**: **Window > Rendering > Lighting > Environment**. The skybox material is
  `Generated/<World>/Environment/Sky.mat`: a panoramic skybox with the world's environment image
  (EXR/HDR/PNG), or Unity's procedural sky. Change **Exposure** and **Rotation** there, or assign any
  other skybox. Ambient light comes from the skybox; fog uses the world's fog settings.
- **Ground**: `Ground` is a Unity Terrain the size of the world's ground.
  - Sculpted ground becomes its heightmap. Keep sculpting with the Terrain's **Paint Terrain** tools.
  - The ground material is a Terrain Layer (the floor preset's textures, tinted by the ground colour).
    Painted ground layers are further Terrain Layers with their painted weights. Paint more with
    **Paint Texture**, change tiling or textures on each Terrain Layer asset.
  - Sculpt water is the `Water` plane.
  - `Ground/Ground Extension` continues the ground's look past its edge and keeps you from falling off
    the world.
- **Lighting bake**: everything placed is static where it does not move; bake lighting from the
  Lighting window if you want baked GI.

## 6. World items

Items are under `World`, named as in Zimension, each with a **World Item Id**. Move, rotate or scale
them freely (re-importing regenerates the scene, see section 19). The model is
a prefab instance of the source glTF under the item; materials and customizations are on its
renderers. Colliders are on the item (box) or on its meshes (object collision); edit or replace them as
in any Unity scene.

To add your own objects, add them anywhere in the scene. To make one take part in the world's logic,
give it a component (Interactable, Player Zone ...) and connect its UnityEvent to a logic method in the
Inspector, or reference it from your half of the logic script.

## 7. World logic (events, flows, variables)

`Assets/Zimension/Scripts/<World>/` holds a `partial class <World>Logic : ZimensionLogic`, on the
`Game` object in each scene.

- **Events are methods.** Zimension event `open_door` is `public void OpenDoor()`. Its body does,
  in Zimension's order, everything that listens to the event: play sounds and animations, start
  transitions, show or hide items, enable interactions, switch character behaviours, then run the logic
  flows. Call it from anywhere: `logic.OpenDoor();`, or pick it in any UnityEvent in the Inspector.
- **Logic flows are private methods** (`Flow11CheckFuseMounted`) with the flow's conditions as `if`,
  its delay as `After(seconds, () => ...)`, its message as `Message("...")`.
- **Variables are properties** (`FuseMounted`, `ZombieKilled`). Their starting values are fields in the
  Inspector under **Variables**. Setting a property raises `VariableChanged`.
- **"@" events and variables** belong to one object: `ThunderOnScifiGeneratorTripleSmall()`, and the
  object's **Local Variables** component.
- **Awake** connects the scene's components to these methods (interactions, zones, collectables,
  weapon pickups, animation and sound endings ...). The fields at the top of the class are the scene
  references the importer assigned.
- **Your code** goes in `<World>Logic.cs`: new methods, fields, Unity messages, and `partial void
  OnAwake()`, which runs after Awake. That file survives re-imports; the `.Generated.cs` file does not.
  To change generated behaviour, either change it in Zimension and re-import, or stop calling the
  generated method and call your own (for example, connect an Interactable to your method in its
  Inspector instead).
- **Helpers** from `ZimensionLogic`: `After`, `Show`, `Hide`, `Message`, `PlaySound`, `PlayAnimation`,
  `PlayTransition`, `StopTransition`, `SetWaypoint`, `Builtin("reset_player")`, `Game`, `Player`, `Hud`.
- Logic Zimension could not express in Unity is marked `// TODO` in the script and listed in the
  import report.

## 8. Interactions, zones, collectables and contacts

- **Interactable**: prompt text, cooldown, whether it starts enabled (Zimension's enable event),
  reach, and the **area** box (drawn as a yellow gizmo when selected). `onInteract` lists what runs.
  Call `EnableInteraction()` / `DisableInteraction()` from code.
- **Player Zone**: enter/leave events when the player's box overlaps the area (cyan gizmo).
- **Collectable**: auto pickup or press-F pickup, inventory, collect sound and volume, `onCollected`.
- **Item Contact**: events when this object's bounds touch another's.

The player selects the newest target in reach, as Zimension does, and shows its prompt on the HUD.

## 9. Animations and retargeting

- Each animated item has `Generated/<World>/Animation/<Item> Animations.controller`. Open it in the
  **Animator** window: one state per Zimension animation card (named by the card's clip), a `Rest`
  state, and transitions back to the resting state after one-shots. The resting (default) state is the
  card that played on start. Change clips, speeds, loops and fades there.
- Clips that loop differently from their source are saved copies under `Animation/Clips`.
- "Finished" events: the state has a **State Finished** behaviour, and the item's **Animation Events**
  component lists what runs per state.
- **Procedural Motion** (spin, hover, float, pulse, swing, wobble, bounce, shake, breathing, tilt
  loop): kind, axis, speed, intensity, delay, loop, play on start.
- **Retargeting**: characters with a recognisable skeleton (Mixamo, Ready Player Me, Tripo, Unreal or
  Blender bone names) get a **Humanoid Avatar** (`Animation/Humanoid/<Model> Avatar.asset`), and their
  library clips are baked into **Humanoid** clips (`Animation/Humanoid/<Clip> (Humanoid).anim`). These
  play on any humanoid character with its own proportions: drop any Humanoid clip (Mixamo FBX set to
  Humanoid, Asset Store animations ...) onto a state. Characters whose skeleton could not be mapped keep
  clips bound by bone name; the report says why. To fix a mapping, select the Avatar asset,
  **Configure Avatar**, and correct the bones.

## 10. Transitions (moving objects)

An item with Zimension transitions has an Animator on the item itself with
`<Item> Transitions.controller`: one state per transition (named as in Zimension) holding a baked
clip of the item's transform (`Animation/Transitions`). Open the clip in the **Animation** window to
move keys, change timing or easing, or add keys. Follow-path transitions are baked along their spline.
`PlayTransition(animator, "Go up")` plays one from its start; `StopTransition(animator)` freezes it.

## 11. Sounds

- An item's sounds are under its `Sounds` child: one AudioSource per Zimension sound card (clip,
  volume, loop, 3D distance). Playing one stops the item's other sounds, as in Zimension.
- World sounds are under `World/World Sounds` (2D).
- Auto-play sounds start in `BeginPlay()` of the logic script.
- The player's footsteps (walk and run loops), jump, hit and death sounds are on the Player
  Controller; the collect sound on each Collectable; gun shot and reload on each Weapon; engine on each
  Vehicle. The default clips are in `Packages/com.zimension.runtime/Runtime/Resources/ZimensionSounds`.

## 12. Weapons

**Weapon**: name, gun or melee, class (reserve ammo is shared per class), damage, range, fire rate
(shots per second), magazine capacity, automatic, accuracy, reload time, recoil, aim zoom, infinite
ammo, initial ammo, bullet colour, sounds, the hold pose in the hand, and the **Muzzle** child where
tracers start. Events: `onPickup`, `onDrop`, `onFire`, `onReload`. Shots are raycasts from the screen
centre; anything with **Health** takes damage. Turn on **Keep World Size** if a weapon looks too big or
small in the hand.

## 13. Vehicles

**Vehicle**: enter/exit labels, hide or seat the driver, forward axis, top and reverse speed,
acceleration, braking, steering speed and angle, wheel base, boost, the wheel and steering-wheel
transforms (spin axis, radius), chase-camera distance and height, engine sound, `onEnter` / `onExit`.
The handling is arcade (ground-following, stops at obstacles). For physics driving, replace the
component with a WheelCollider setup and call the world's enter/exit methods from it.

## 14. Characters (NPC behaviours)

**Character Behaviour** on a character: a list of behaviours (Idle, Follow, Attack, Shoot, Patrol),
each with its activate/deactivate event, chase/run options, attack impact delay and damage, weapon and
difficulty (shoot), patrol points (cyan gizmos), loop, run and wait. Behaviours without an activate
event start active; the world logic calls `Activate(i)` / `Deactivate(i)` on their events. Walk and
run speeds, chase (120 m), attack (1 m), shoot (8 m) and follow (2 m) distances are fields.

Characters move with their CharacterController along NavMesh paths. **Runtime NavMesh** on the `Game`
object bakes one from the scene's colliders when play starts. For full control, remove it and bake
your own NavMesh (Window > AI > Navigation); characters use whichever exists. The Animator states it
plays are `Idle`, `Walk`, `Run`, `Right_Hook` (attack) and `Dying`; rename them in the component if
your controller uses other names.

## 15. Health and damage

**Health**: max health, health bar (colour, height), disappear on death and delay, respawn (delay,
limit, points). `onHit`, `onDie`, `onRespawn` are UnityEvents. Call `Damage(amount)`, `Heal(amount)`,
`Kill()` from code. The player's own health is on the Player Controller.

## 16. Instancing (copies of objects)

Zimension instance groups (grids, lines, rings, scattered areas, shape outlines, splines, brushed
points) are generated with the same seeded layout as Zimension and placed under
`World/Instances - <group>`: one object per copy, each holding a prefab instance of the source model
with the source's materials. Copies are static, and collide when the group had collision on.

- Move, delete or duplicate copies like any object.
- To change every copy at once, edit the source model's prefab (in `SourceAssets`) or its materials.
- For thousands of copies, enable **GPU Instancing** on the materials, or replace the group with your
  own placement (for example Terrain trees/details for vegetation).
- Changing the recipe (count, spacing, shape) is done in Zimension; re-import to regenerate the copies.

## 17. HUD, game flow and built-in events

- **Game HUD** (on `Game`): every text, colour and size of the default UI: the "F To {action}"
  prompt, world messages, crosshair and ammo, health bar, waypoint marker, fades, and the start, pause,
  dead and win screens. It is drawn with IMGUI so it needs no prefabs. To use your own UI (uGUI or UI
  Toolkit), disable it and call the same things from your scripts: `GameHud` exposes `ShowMessage`,
  `SetPrompt`, `SetWaypoint`, `FadeIn/Out`, `ShowScreen`.
- **Game Manager** (on `Game`): starts play (the start screen), pause and resume, death and respawn,
  winning, restarting, scene loading, and Zimension's built-in events:

| Built-in event | Unity |
|---|---|
| `reset_player` | `Player.Respawn()` |
| `reset_world`, `die_start_over` | reload the scene |
| `player_dead` | the player dies |
| `game_over_win` | the win screen |
| `launch_player_up_5m`, `_10m` | `Player.Launch(metres)` |
| `hit_damge_10`, `reduce_health_10`, `reduce_health_50` | `Player.Damage(...)` |
| `increase_health_20`, `full_health` | `Player.Heal(...)` |
| `increase_boost_20`, `full_boost`, `stop_vehicle` | the current vehicle |
| `scene_fade_in`, `scene_fade_out` | HUD fades |
| `freeze_player_movement`, `unfreeze_player_movement` | `Player.frozen` |
| `clear_delay_events` | `logic.ClearDelays()` |

## 18. Scenes

Each Zimension scene is a Unity scene under `Generated/<World>/Scenes`, added to **Build Settings**
(base scene first). A scene's "switch on event" becomes `Game.LoadScene("<scene>")` in that event's
method; world variables carry over to the next scene, as in Zimension.

## 19. Re-importing and Pull latest

**Pull latest** in **Zimension > Import from Zimension** (or **Zimension > Pull Latest for All Linked Worlds**, or importing the same world again) updates the
project from the newest export:

- `SourceAssets` files are replaced only when their content changed.
- `Generated` content and `<World>Logic.Generated.cs` are regenerated, so changes you made to the
  generated scenes, controllers and clips are replaced. Keep your own changes in your own assets,
  scenes, prefabs and in `<World>Logic.cs`; or copy a generated asset to `UserContent` before editing it
  and point your objects at the copy.
- `<World>Logic.cs` and `UserContent` are never touched.

If you want to treat the conversion as a one-time port, stop re-importing and edit everything in
place: from then on it is your Unity project.

## 20. What is not converted yet

Listed per world in the import report. Currently: custom HUD layouts and overlays (the default HUD is
used), objectives and quests, cutscenes, carrying props, item JavaScript, video surfaces, item
attachments, spline-sweep geometry, painted 3D grass blades (painted grass shows as a grass-coloured
layer), and player-control switching flows (TODOs in the logic script).

## 21. Troubleshooting

- **The scene does nothing on Play**: check the Console for `Connected <World>Logic`. If the logic
  script failed to compile, fix the errors shown; the scene connects automatically after the next
  successful compile.
- **Pink materials**: the Universal Render Pipeline is not active. Run **Zimension > Setup Project (URP, input, colour space)**.
- **A model is the wrong size**: the Console shows `[Zimension] Size check` for every model; send that
  line with your report.
- **A character does not animate**: see the import report for its skeleton mapping; open its
  Animator Controller to check the states have clips.
- **Characters walk through walls**: make sure the `Game` object has **Runtime NavMesh** or bake a
  NavMesh.
