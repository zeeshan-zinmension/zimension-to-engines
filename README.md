# Zimension → Engines

Exports Zimension projects to game engines. **Unity is implemented**; Unreal is a later target that
will reuse the same event core and import contract.

A Zimension project becomes an editable Unity 6 project: one `.unity` scene per Zimension scene,
models as glTF prefabs, sounds as AudioClips, and the world's events, logic flows, variables,
transitions, animations, lip sync, interactions, collectables, HUD and scene switches running
through ordinary Unity components. After import, play uses only local assets: no Zimension API,
no web player.

```
Zimension editor ──Export > Unity──▶ share link (uploaded export: masterData.json + assets)
                                          │  (or a downloaded .zip)
Zimension Unity project ──Zimension > Import from Zimension──▶ download, verify, import
                                          │
                                Press Play ▶ ZimensionWorld runs the scene
                                Pull latest ▶ re-import after the next export
```

## Quick start (Windows)

1. Install **Unity 6.3 LTS** (6000.3.x; tested configuration pinned at 6000.3.28f1) with Windows Build Support.
2. Get the Unity project: download `ZimensionUnity-<version>.zip` from the repository's Releases
   (or the *Release Unity project* workflow artifact), or clone this repository and use
   `unity/ZimensionUnity`. Open it in Unity Hub (*Add > Add project from disk*). The first open
   resolves the pinned packages (glTFast, Draco, KTX, URP) and the Zimension package configures URP,
   input handling and colour space (*Zimension > Setup Project* re-runs it).
3. *Zimension > Import from Zimension…*
   * **From a Zimension link**: paste the link from Zimension's *Export > Unity*. It downloads
     (resumable, verified), imports, and later **Pull latest** re-imports newer exports.
   * **From a file**: an export `.zip` or folder. Try `samples/event-fixture` first; it needs no
     network or account.
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
  project data and assets without the browser player, uploads them, and shows a **copyable link**.
  Exporting the same world again updates the same link. Until the backend serves the link
  endpoints ([docs/BACKEND_UNITY_EXPORT.md](docs/BACKEND_UNITY_EXPORT.md)), the zip is downloaded
  instead.
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
| `tools/dev-export-server.mjs` | Reference implementation of the link backend (no dependencies). Run it to test link imports before the real backend exists. |
| `tools/package_template.py` | Builds the downloadable `ZimensionUnity-<version>.zip`. |
| `tools/generate_metas.py` | Deterministic `.meta` files for the package (needed for Git URL installs). |
| `docs/` | [Architecture](docs/ARCHITECTURE.md), [field contract](docs/FIELD_CONTRACT.md), [compatibility](docs/COMPATIBILITY.md), [testing on Windows](docs/TESTING_ON_WINDOWS.md), [backend link contract](docs/BACKEND_UNITY_EXPORT.md). |

## Status

What has been verified, and what still needs your Windows machine:

* **Verified in this repository (Linux, no Unity):**
  * 167 checks pass: the event core (including the native player's ported regression cases), the export reader, the sample world, and the link downloader against a fault-injecting HTTP server (resume, retries, stalls, version changes, checksums, cancel).
  * 15 contract checks pass for the reference link server, and an end-to-end link import works (reference server to Unity downloader).
  * The runtime and Editor code compile with no errors or warnings against Unity reference assemblies.
* **Not yet verified (needs Unity 6 on Windows):**
  * Opening the project, importing real exports, Play mode, and the standalone build. [docs/TESTING_ON_WINDOWS.md](docs/TESTING_ON_WINDOWS.md) lists each check.
* **Unity CI:** the job in `.github/workflows/ci.yml` runs once a Unity licence is configured (see [docs/TESTING_ON_WINDOWS.md](docs/TESTING_ON_WINDOWS.md#ci)).

See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for which features are supported, approximated or not supported yet (vehicles, weapons, NPC behaviours, terrain and instance groups are later milestones).
