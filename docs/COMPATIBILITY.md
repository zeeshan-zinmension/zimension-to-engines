# Compatibility

Every import writes `Assets/Zimension/Generated/<world>/export-report.json` and a summary in the
`ZimensionProject` asset (*Import report*), listing each finding per item and saved field:
**blocking** (missing mandatory data), **unsupported**, **approximated** and **optional**. The
in-game F3 panel shows the same notes, plus the event trace and variables.

## Supported

* **Events and logic:**
  * world and `@` local events and variables;
  * Trigger Events with delays, messages and links;
  * Change Variable, including JavaScript number/string/boolean coercion;
  * Check Condition (AND binds tighter than OR);
  * Switch Case;
  * `clear_delay_events`;
  * a recursion guard.
* **Item lifecycle:** spawn and remove events, hidden items that later receive events,
  collectables (auto and manual) with counters, interactions (prompt, cooldown, enable/disable),
  range events, solid-contact events on mesh colliders, item-to-item triggers, waypoints.
* **F target selection:** follows the native player's rules: exactly one target, entry-ordered,
  with mounted, equipped and held exclusions.
* **Transitions:** state steps, smooth/linear, loop, ping-pong, stop and finished events,
  sequencer offsets, follow-path along splines. Moving platforms carry the player.
* **Animations:** glTF clips with 0.5 s crossfades, speed, start/end windows, reverse, loop,
  stay in pose, finished and stop events, return to the Play-on-Start cards. Built-in procedural
  effects.
* **Sounds:** item (positional) and world banks, loop, volume, stop, track-based finished events,
  subtitles, pause that never counts as finishing.
* **Lip sync:** Rhubarb JSON on the audio sample clock, with Ready Player Me and generic
  viseme/jaw mapping, intensity 0.6, and weights above 100 where Ready Player Me requires them.
* **Player:**
  * capsule movement: walk, sprint, jump, slopes, steps;
  * third- or first-person camera with wall avoidance;
  * avatar locomotion clips;
  * spawn, reset and checkpoint teleport with floor placement;
  * freeze, launch, health and death;
  * pointer (orbit/click) mode.
* **Scenes:** one Unity scene per Zimension scene, `switchOnEvent` with fade, carried variables
  and delayed events, and no second `on_start`.
* **UI:** messages, prompts, health, waypoint, start/pause/dead/win screens. Custom HUD and
  overlays: text, buttons, images and bars bound to variables, with show/hide and click events.
* **Content:** glTF/GLB models (Draco and KTX through glTFast), Basics shapes, point/spot/
  directional lights, the sun, a panoramic sky from `env`, ground or an invisible floor.

## Approximated

| Area | Difference |
|---|---|
| Rendering | URP Lit, not the web's WebGPU renderer or the native DirectX/NRD path. Light intensities are converted from three's physical units by fixed factors. Ground material presets and shape shader effects are flat colours. |
| Character animation | Library and source clips are bound by bone name; rest-pose differences between rig families (Ready Player Me, Mixamo, Tripo profiles) are not corrected, and non-hip translations are dropped. |
| Custom UI | IMGUI layout of the saved CSS: absolute px/% placement, colours, font size, simple flex stacking. Fonts are Unity's default. |
| Skinned colliders | Animated characters with object collision collide with their rest-pose mesh. |
| Area, ambient and hemisphere lights | Point light, or flat ambient. |
| Audio rolloff | Logarithmic, 4 m to 180 m, for item sounds. |
| `save_game` | Saves world variables to PlayerPrefs rather than the account. |

## Not supported yet (reported, gated as later milestones)

* Vehicles (cars, two-wheelers, airplanes), weapons and shooting, carrying props.
* NPC behaviours (follow, patrol, chase, AI), item damage and health.
* Rigid bodies.
* Terrain, walls, sprites, instance groups, spline sweeps (roads and rails).
* Camera cutscenes, animation sequences (the sequencer dock), environment effects, objectives,
  custom input bindings, VFX particles.
* Video surfaces, item JavaScript (`scriptUrl`), multiplayer, XR.
* Logic flows "Switch Control" and "Return to Previous Control".

When one of these items is the F target, F shows a "not available yet" message. Nothing is
silently dropped: every case appears in the report.

## Target and tested configuration

| | Version |
|---|---|
| Unity | 6.3 LTS, pinned 6000.3.28f1 (any 6000.3 patch should work) |
| URP | 17.3.0 |
| glTFast | 6.20.0 (with Draco for Unity 5.6.0, KTX for Unity 3.8.0) |
| Host / player | Windows x64 first; nothing in the package is Windows-specific |
| Scripting | C# 9, .NET Standard 2.1; no native plugins |
