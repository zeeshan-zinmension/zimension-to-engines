# Zimension → Engines

Exports Zimension projects to game engines. **Unity is implemented**; Unreal is a later target that
will reuse the same event core and import contract.

A Zimension project becomes an editable Unity 6 project: one `.unity` scene per Zimension scene,
models as glTF prefabs, sounds as AudioClips, and the world's events, logic flows, variables,
transitions, animations, lip sync, interactions, collectables, HUD and scene switches running
through ordinary Unity components. After import, play uses only local assets: no Zimension API,
no web player.

```
Zimension editor ──Export > Unity (or Web)──▶ <world>-unity.zip (masterData.json + assets)
                                                 │
Unity 6 + com.zimension.runtime ──Zimension > Import Zimension Export──▶ Assets/Zimension/…
                                                 │
                                       Press Play ▶ ZimensionWorld runs the scene
```

## Quick start (Windows)

1. Install **Unity 6.3 LTS** (6000.3.x; tested configuration pinned at 6000.3.26f1) with Windows Build Support.
2. Clone this repository and open `unity/ZimensionUnity` in Unity Hub (*Add > Add project from disk*).
   The first open resolves the pinned packages (glTFast, Draco, KTX, URP) and the Zimension package
   configures URP, input handling and colour space (*Zimension > Setup Project* re-runs it).
3. *Zimension > Import Zimension Export…* → choose a Zimension export `.zip` (or the extracted folder).
   Try `samples/event-fixture` first; it needs no network or account.
4. The base scene opens. Press **Play**, click **Start**. WASD to move, mouse to look, Shift to sprint,
   Space to jump, **F** to interact, P to pause, F3 for diagnostics.

Full walkthrough and what to check: [docs/TESTING_ON_WINDOWS.md](docs/TESTING_ON_WINDOWS.md).

To use the package in another Unity 6 project instead (for example one created from Hub's
*Universal 3D* template): *Window > Package Manager > + > Add package from git URL…*

```
https://github.com/zeeshan-zinmension/zimension-to-engines.git?path=/unity/ZimensionUnity/Packages/com.zimension.runtime#unity-exporter
```

## Getting an export from Zimension

* **Export > Unity**: added on the `unity-export` branch of `zimension-web`. It packages the
  project data and assets without the browser player.
* **Export > Web**: works today with no web changes. The importer reads the same
  `static/files/masterData.json` layout from the Web export zip.

Both use the editor's existing export permissions and packaging (`Engine.generateApp`). Export
from the editor after saving, so the export carries the saved authoring state.

## Repository layout

| Path | What |
|---|---|
| `unity/ZimensionUnity/` | Unity 6 template project (pinned packages). Open this in Unity. |
| `unity/ZimensionUnity/Packages/com.zimension.runtime/` | The UPM package: `Runtime/Core` (engine-free event runtime), `Runtime/Unity` (Unity host), `Editor` (importer). |
| `samples/event-fixture/` | A small Zimension export built from Basics shapes that exercises the event workflow. |
| `tests/Zimension.Core.Tests/` | Regression checks for the event core, the export reader and the fixture (`dotnet run --project tests/Zimension.Core.Tests`). |
| `tools/compile-check/` | Compiles the package against Unity reference assemblies without a Unity install. |
| `tools/generate_metas.py` | Deterministic `.meta` files for the package (needed for Git URL installs). |
| `docs/` | [Architecture](docs/ARCHITECTURE.md), [field contract](docs/FIELD_CONTRACT.md), [compatibility](docs/COMPATIBILITY.md), [testing on Windows](docs/TESTING_ON_WINDOWS.md). |

## Status

What has been verified, and what still needs your Windows machine:

* **Verified in this repository (Linux, no Unity):**
  * 145 event-core, export-reader and fixture checks pass. They include the native player's event regression cases, ported.
  * The runtime and Editor code compile with no errors or warnings against Unity reference assemblies.
* **Not yet verified (needs Unity 6 on Windows):**
  * Opening the project, importing real exports, Play mode, and the standalone build. [docs/TESTING_ON_WINDOWS.md](docs/TESTING_ON_WINDOWS.md) lists each check.
* **Unity CI:** the job in `.github/workflows/ci.yml` runs once a Unity licence is configured (see [docs/TESTING_ON_WINDOWS.md](docs/TESTING_ON_WINDOWS.md#ci)).

See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for which features are supported, approximated or not supported yet (vehicles, weapons, NPC behaviours, terrain and instance groups are later milestones).
