# Testing on Windows

Nothing Unity-specific could be run where this was built (a Linux container with no Unity
licence). The event core and the export reader are tested there, and the Unity code compiles
against Unity reference assemblies. These steps cover the gates that need the real editor.

## 1. Get the code

```bash
git clone -b unity-exporter https://github.com/zeeshan-zinmension/zimension-to-engines.git
```

For the Export > Unity button, also check out the web branch:

```bash
git -C zimension-web fetch origin unity-export
```

```bash
git -C zimension-web checkout unity-export
```

## 2. Open the project

1. Install **Unity 6.3 LTS** (6000.3.26f1, or any 6000.3 patch) with *Windows Build Support (IL2CPP or Mono)*.
2. Unity Hub → *Add → Add project from disk* → `zimension-to-engines/unity/ZimensionUnity`.
3. The first open resolves packages from the Unity registry, so it needs internet once. After
   that, the Zimension package sets up URP, input handling and colour space automatically. If
   the editor asks to restart for input handling, accept.

**Gate A:** the Console shows no compile errors, and the *Zimension* menu exists.

If URP setup reports a problem, run *Zimension → Setup Project*. If it still fails, create the
project from Hub's **Universal 3D** template instead, and add the package from disk
(*Package Manager → + → Add package from disk →
`unity/ZimensionUnity/Packages/com.zimension.runtime/package.json`*).

## 3. Sample fixture (no account or network)

1. *Zimension → Import Zimension Export… → Folder…* → `zimension-to-engines/samples/event-fixture` → **Import**.
2. The scene `Assets/Zimension/Generated/Event_Fixture/Scenes/...Base_Courtyard.unity` opens. Press **Play**, then **Start**.

**Gate B:** each of these should happen:

| Step | Expected |
|---|---|
| Start | Message "Grab the gold coin, then press F at the red button."; HUD shows Coins: 0, Score: 0; the cube spins; the blue platform moves back and forth. |
| Walk to the red button, press F (without the coin) | "The door opens in one second...", then after 1 s the door rises, then "You need the coin first!" |
| Walk over the spinning gold coin | It disappears; Coins: 1, Score: 10 |
| Press F at the button again (after its 2 s cooldown) | The door transition replays; the green reward sphere appears behind the wall and the "You found the reward!" overlay shows; Continue closes it |
| Stand on the blue platform | You ride it |
| F3 | Diagnostics: recent events, variables, interaction target |
| P | Pause screen; Restart resets the world (coin back, door down, reward hidden, on_start message again) |

## 4. Your worlds

1. In Zimension (the `unity-export` web branch), open the project and save it. Then use
   *Export → Unity*. On the current production build, *Export → Web* works too.
2. Import the zip in Unity as above. Read the summary, and
   `Assets/Zimension/Generated/<world>/export-report.json`.

**Gate C**, per world:

* **Billionaires' Meeting:**
  * all 12 voice JSON files are listed in the report as `voice-json` assets;
  * Tony and Elon are animated independently;
  * faces and jaws move in sync with each voice line, while their body animation continues;
  * stop, replay and reset all work;
  * the indoor spawn is grounded;
  * camera workflow: cutscene cameras are not supported yet (see the report).
* **Out of the Trap:**
  * the mouse avatar is shown;
  * the guide conversations unlock in order;
  * F picks exactly one target;
  * collection counters, health and the authored overlays all work.
* **Racing Template / Canyon Rush:** vehicles are not supported yet. Expect static cars, and a
  report that lists vehicles as unsupported.

Report back with the import summary and `export-report.json` for anything that's wrong. Include
the Console log (Editor.log) for import errors.

## 5. Build a player

*File → Build Profiles → Windows → Build*.

**Gate D:** the built `.exe` starts at the base scene and plays without network access.

## CI

The `core` job always runs: event core checks, `.meta` completeness, and the compile checks
against Unity reference assemblies.

The `unity` job imports the sample and builds a Windows player in the pinned editor, using
GameCI. To enable it:

1. Add repository secrets for a Unity licence: `UNITY_LICENSE` (Personal, the contents of the
   `.ulf` file) or `UNITY_SERIAL` (Pro), plus `UNITY_EMAIL` and `UNITY_PASSWORD`.
2. Set the repository variable `UNITY_CI_ENABLED=true`.

The job has not run yet.

Batch import on your machine:

```bash
"C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe" -batchmode -projectPath unity\ZimensionUnity -executeMethod Zimension.Editor.BatchImport.Run -zimensionExport samples\event-fixture -logFile import.log
```
