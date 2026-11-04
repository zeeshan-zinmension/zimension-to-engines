# Architecture

## Layers

| Layer | Where | Owns |
|---|---|---|
| Collector | `zimension-web` `Engine.generateApp` (Export > Unity / Web) | A consistent saved snapshot (`masterData.json`: `world` + `worldItems`) and every referenced asset, using the editor's existing export permissions. |
| Importer | `Packages/com.zimension.runtime/Editor` | Reads the export, stages assets, configures glTFast, retargets clips, generates scenes, a `ZimensionProject` asset, Build Settings and `export-report.json`. Editor-only. |
| Event core | `Packages/com.zimension.runtime/Runtime/Core` (`Zimension.Core.asmdef`, `noEngineReferences`) | Event dispatch, `@` local scopes, variables, conditions, arithmetic, switch, delays, sequenced cards, transitions, follow-path moves, ranges, interaction target selection, collectables, waypoints, scene carry. |
| Unity host | `Packages/com.zimension.runtime/Runtime/Unity` | GameObjects, transforms, renderers/colliders, Playables animation, AudioSources, lip sync, player, HUD, scene loading. Turns the core's actions into Unity calls and reports Unity completions back. |

## Decision: one C# event core instead of a C++ plugin

The handoff plan proposed a portable C++ event core behind a C ABI. The implemented core is
**C#, ported line for line from the native player's `Events.cpp`/`Splines.cpp`**, with that file's
regression cases (`tests/EventChecks.cpp`) ported alongside it. Why:

* **Same semantics, one source.** The plan's goal was "one reusable runtime and no second
  interpreter with different semantics". The C# core is a mechanical port of the native
  runtime, so it has the same ordering, scoping and timing, and it is held to the same tests.
  It is the only interpreter inside Unity, and it has no Win32 or DirectX dependencies.
* **No native ABI to get wrong.** Without a plugin there is no marshalling, no per-platform
  binaries, no Editor domain-reload lifetime problems, and no prebuilt DLL that only builds on
  Windows. The project runs on every platform Unity targets and can be debugged in the IDE.
* **Testable here.** The core has no UnityEngine references, so its checks run in CI with plain
  .NET.
* **Reusable for Unreal.** The core is plain C#: Unreal can use it through a .NET host or as a
  generated port. Alternatively, the C++ `Events.cpp` and this C# port can be kept in step by
  sharing `tests/EventChecks` as the contract.

The trade-off is that a fix to the native player's event code has to be mirrored here. The
shared regression cases are what catch it when one side drifts.

## Event ordering and clocks

* `TriggerEvent` runs every trigger immediately, and nested events run in place, as the web's
  `executeEventTriggers` does. A variable written by one flow is therefore visible to the next
  condition in the same chain. There is no command queue, so no frame of latency is added.
* Recursion is capped at 64 nested events. The web recurses without a limit, which freezes the
  tab on a looping flow.
* `ZimensionWorld.Update` runs one ordered step per frame:
  1. input (F interacts, P pauses);
  2. player movement;
  3. live bounds for moving items;
  4. the runtime update (timers, transitions, range checks, item-pair triggers);
  5. animation completions;
  6. sound completions;
  7. lip sync.
* Clocks: simulation and timers use `Time.deltaTime`, so pausing (time scale 0) stops them. Lip
  sync reads the playing `AudioSource`'s sample position, not accumulated frames. Fades and
  messages use unscaled time.
* Completion events fire once per playback:
  * animation finished, per card;
  * sound finished, per track, using the last card on that track with a finished event, as
    `getSoundFinishedEvent` does.
  * A stop or a pause never counts as finishing.
* The first global `on_start` fires once per world run. A scene switch carries world variables
  and delayed world events, and resumes with `Start(false)`.

## Coordinates (one conversion)

* Zimension (three.js) is right-handed, Y-up, in metres. Unity is left-handed, Y-up, in metres.
* The basis change is the reflection S = diag(-1, 1, 1), which is the same mirror glTFast
  applies to glTF nodes.
* For any transform M, the Unity transform is `S·M·S`:
  * positions become (-x, y, z);
  * quaternions become (x, -y, -z, w);
  * scales are unchanged.
* Matrices: three's column-major `elements` are loaded in order into `System.Numerics.Matrix4x4`
  (row-vector convention), exactly as the native player loads them into DirectX matrices.
  `UnityMath` transposes when it talks to `UnityEngine.Matrix4x4`.
* glTFast converts everything inside a model hierarchy. The importer converts:
  * item placement;
  * transition states;
  * spline points;
  * spawn pose;
  * sun direction;
  * Basics meshes (built in three space, then mirrored with their winding reversed).
* The runtime never converts again: everything at play is already in Unity space, so nothing is
  converted twice.
* Unit tests check that the basis change commutes with transforms, and that rotations and
  translations come out mirrored as expected.

## Identity, visibility and editing

* Every item has a `ZimensionItem` holding its `_id`, `sourceWorldItemId` and its saved JSON.
  The world collects items with `GetComponentsInChildren(true)` and never uses an active-only
  `Find`, so hidden items still receive their show events.
* Visibility is split into two separate operations:
  * **Rendering only:** saved `visible: false`, or a collider-only item. Renderers and lights
    are disabled, but colliders stay.
  * **Whole-item removal:** remove, collect, or a spawn event that hasn't fired yet. Renderers
    *and* colliders are disabled, so a hidden blocker never stops the player.
  * `SetActive(false)` is never used on items.
* Editable in Unity:
  * item transforms, which the runtime reads at play start;
  * transition states, sound clips and voice data on `ZimensionItem`;
  * the item and world JSON (events, cards, flows);
  * the spawn point.
* Runtime code never writes these back.
* Re-importing replaces only `Assets/Zimension/Generated/<world>`. `SourceAssets` keep their
  GUIDs (a file is rewritten only when its content changes), and `Assets/Zimension/UserContent`
  is never touched.

## Import pipeline

1. `BundleSource` opens the export zip or folder and finds `masterData.json`. It normalizes the
   web packager's `\` separators and rejects archive traversal.
2. `AssetResolver` resolves each saved reference using the packager's naming rules:
   * models: `{file._id}{file.name}`, including LOD levels;
   * sounds: `sound{_id}{fileName}`;
   * voices: `voice{_id}{fileName}`;
   * CDN assets: the path after the host;
   * local paths: `./static/files/...`.

   Nothing is ever downloaded. A reference that can't be resolved is reported, not fetched.
3. Staging writes files under `Assets/Zimension/SourceAssets/<world>`:
   * `.gltf` companion files keep their relative URIs;
   * voice JSON gets a `.json` name;
   * GLBs in the export that contain animations are staged as animation libraries.
4. Unity imports the staged files. `ZimensionAssetPostprocessor` configures glTFast on first
   import: Mecanim clips (for Playables) and original node names (needed by lip sync and clip
   binding).
5. `ClipRetarget` binds library and character-animation-source clips to each model by bone name.
   It drops non-hip translations and does not correct rest-pose differences between rig
   families; both are reported as approximations.
6. `SceneBuilder` builds one scene per Zimension scene, following the native player's placement
   rules:
   * source aliases resolve copied items;
   * an item whose node is in another scene's tree is skipped;
   * the base scene comes first.

   It generates items, Basics meshes, lights, colliders (box, real mesh surfaces, or a
   `collider_mesh_zim` node), sounds, states, sun, sky, ground or an invisible floor, and the
   spawn point.
7. Outputs: the `ZimensionProject` asset (world JSON, scenes, avatar and clips, world sounds,
   UI images), `export-report.json`, `manifest.json`, and the Build Settings scene list.

Batch mode (do not add `-quit`; the method exits the editor when the import finishes):

```
Unity.exe -batchmode -projectPath unity/ZimensionUnity -executeMethod Zimension.Editor.BatchImport.Run -zimensionExport <export.zip> -logFile import.log
```

It exits 0 on success, or 1 on failure or when there are blocking findings.
