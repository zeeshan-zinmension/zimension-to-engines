# Saved-field contract

These are the saved Zimension fields the Unity export reads, and where each one goes. The web
engine's saved-data behaviour is the reference. The native player's `Events.cpp` and
`ProjectSource.cpp` (which the core was ported from) are the regression reference.

## World (`masterData.world`)

| Field | Unity |
|---|---|
| `events` | Only listed events (plus `on_start`, `online_match_*`) bind world triggers (`addTriggerToEvent`). |
| `variables[] {name, dataType, initialValue}` | Core variables; HUD bindings; `ZimensionEngine.GetVariable`. |
| `eventBroadcasters[]` | Logic flows: `actionIndex` 0 Trigger Events (`triggerEvents`, numeric `delay`, `showMessage`, `link`), 1 Change Variable (`targetVariable`, `values[0]`, `arthematicOperators[0]`), 2 Check Condition (`conditions[]`, `conditionOperators`, `trueEvent`/`falseEvent`), `actionType: "Switch Case"` (`switchVariable`, `cases[]`, `defaultEvent`). `ownerItemId` runs in that item's `@` scope. Switch Control / Return to Previous Control: reported, not run. |
| `sounds[]`, `soundTriggers[]` | World sound bank (2D), cards `playmode` auto/event, `event`, `soundStopEvent`, `soundFinishedEvent`, `soundIndex`, `loop`, `volume`, `sequencerAt`, `showSubtitle`. |
| `nodes[]` / `sceneGraph.scenes`, `baseSceneId`, `isBaseScene` | One Unity scene per `type: "scene"` node. `settings.worldSettings` is merged over the world for that scene; `switchOnEvent` switches to it; `transitionSettings {mode, fadeDuration, fadeInDelay}`. |
| `avatar {character|libraryItem, characterAnimationMap, walkSpeed, sprintSpeed, jumpHeight, disbaleJump, disableSprint, autoSprint, cameraType, cameraDistance, cameraHeight, fov, position, rotation, transformation}` | Player avatar prefab and Idle/Walk/Run/Jump clips, movement and camera settings, spawn point. |
| `spawnPosition` | Spawn (metres) when the avatar has no position. |
| `playerSettings.defaultControl`, `playType` | Character (default) or pointer mode; vehicle mode is reported. |
| `pointer {position, target}` | Pointer-mode orbit camera. |
| `sunPosition {x,y}`, `sunIntensity`, `sunColor`, `shadows`, `env`, `exposure`, `showGround`, `size` | Directional sun, panoramic or procedural sky, ambient, ground or invisible floor. |
| `customHUDUI.elements[]`, `customUIOverRelays[]` | IMGUI custom HUD and event overlays: `type` (text-box, button, image, bar), `valueType: variable`, `variableName`, `prefix`/`suffix`, `showEvent`/`hideEvent`, `event`, `closesOverlay`, `style`/`customStyleText`/`barStyle` (px/% lengths, colours, font size), `layout {direction, gap, padding}`. Overlays: `event`, `hideEventName`, `pauseGame`, `closeOnClick`, `containerStyle`. |
| `defaultUISettings` | Prompt, pickup, start and death texts. |
| `loadingScreen`, `loadingGraphic` | Start screen background and logo. |
| `animationSequences`, `environmentEffects`, `objectives`, `inputBindings` | Reported as not run yet. |

## Items (`masterData.worldItems[]`)

| Field | Unity |
|---|---|
| `_id`, `sourceWorldItemId`, `originalWorldItemId`, `name`, `sceneId` | `ZimensionItem` identity, aliases, scene membership. |
| `transformation` (three column-major) / `matrix` / legacy `position`,`rotation`,`scale`/`customScale` (×0.01/2.2, scale /2.2) | Item transform (converted once to Unity space). |
| `type` | `item` (glTF model from `file`), `primitive` (`primitiveSettings.shape` box/sphere/cylinder/cone/pyramid/plane/torus/capsule/wedge with `dimensions`, `color`, `roughness`, `metalness`, `opacity`, `transparent`, `emissive`, `emissiveIntensity`, `textureUrl`, `emptyCollider`), `light` (`lightSettings`), `spline` (`splineSettings` → path transitions), `vfx`/`empty` (logic-only). Other types: placeholder plus a report entry. |
| `visible`, `active`, `useAsColliderMesh`, `castShadow`, `receiveShadows` | Rendering visibility, collider-only items, shadows. |
| `collision` (box/object/none), `sceneCollisionActive`, `collider_mesh_zim` node | BoxCollider around the model, MeshColliders on the real surfaces (or only the collider node), or none. Collectables and weapons do not collide. |
| `spawnType: "event"`, `spawnEvent`, `disappearEvent` | Hidden until spawned; removed (renderers and colliders off). |
| `onEnterRangeEvent`, `onLeaveRangeEvent` | Range events. On `collision: "object"` items they fire on real capsule contact. |
| `collectable`, `collectSettings {autoPickup, collectType}`, `collectCountVariable`, `onCollectEvent` | Auto or manual (F) pickup, counter, inventory message. |
| `enableInteraction`, `interactionName`, `interactionTriggers`, `interactionTime`, `interactionEnableEvent`, `interactionDisableEvent` | F interaction with prompt, cooldown and enable/disable events. |
| `itemInteractionTriggers[]` / `collisionWithItemList[]` | Item-to-item enter/leave on live bounds. |
| `wayPoint {enable, event, deactivateEvent}` | HUD waypoint marker. |
| `variables[]` (`@` names) | Item-local variables. |
| `states[]`, `transitions[] {steps[{stateIndex, duration, movement}], playmode, event, loop, reset, transitionFinishedEvent, transitionStopEvent, sequencerAt, mode: "path", pathItemId, pathSpeed, alignToPath, pathReverse}` | Transitions (state 0 is the item itself), ping-pong, loop, smooth easing, follow-path. |
| `allowAnimations`, `animations[] {playmode, event, animationStopEvent, animationFinishedEvent, animationName, animationIndex, speed, startTime, endTime, loop, reverse, clampOnFinish, sequencerAt, sequencerFadeDuration, animationSource: "default", proceduralSettings}` | `ItemAnimator`: Playables clips with a 0.5 s crossfade, windows, reverse, hold, finished events, return to Play-on-Start cards; procedural spin/hover/float/pulse/swing/wobble/bounce/shake/breathing/tiltLoop. |
| `characterAnimationSources[] {_id, url, format, clips[{name, index, bindingName}]}` | Library clips bound to the model by bone name, named as the web names them. |
| `sounds[] {_id, fileName, url, voiceDataUrl, text}`, `soundTriggers[]` | Positional sound bank per item; Rhubarb lip sync; subtitles (`showSubtitle`). Playing a card stops the item's other tracks. |
| `customSettings` (`COLOR`) | Per-mesh colour overrides; other customization types are reported. |
| `vehicleSettings`, `fireWeaponSettings`, `pickable`, `rigidBodySettings`, `characterBehaviors`, `damageSetting`, `attachments`, `scriptUrl`, `video` | Reported as not supported yet. F still selects these items as targets, using the native rules. |

## Built-in events

`reset_player`, `reset_world`, `die_start_over`, `player_dead`, `game_over_win`, `launch_player_up_5m/10m`,
`hit_damge_10`, `reduce_health_10/50`, `increase_health_20`, `full_health` (max 100), `scene_fade_in/out`,
`freeze/unfreeze_player_movement`, `save_game` (variables to PlayerPrefs), `clear_delay_events`.
Vehicle built-ins (`increase_boost_20`, `full_boost`, `stop_vehicle`) are reported.
`player_health` is mirrored as a read-only variable.
