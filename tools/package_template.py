#!/usr/bin/env python3
"""Zip the ready-to-open Zimension Unity project (unity/ZimensionUnity) for download.

  python3 tools/package_template.py [--out out]   ->  out/ZimensionUnity-<package version>.zip

The zip contains the project folder (Assets, Packages with the embedded com.zimension.runtime,
ProjectSettings) and a README; Unity-generated folders (Library, Temp, Logs, UserSettings, obj,
IDE files) are excluded. Users unzip it, open the folder in Unity Hub and use
Zimension > Import from Zimension."""
import json, os, sys, zipfile

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
PROJECT = os.path.join(ROOT, "unity", "ZimensionUnity")
SKIP_DIRS = {"Library", "Temp", "Logs", "UserSettings", "obj", "Build", "Builds", ".vs", ".idea", ".git"}
SKIP_EXT = {".csproj", ".sln", ".user"}

README = """Zimension Unity project
=======================

1. Use any Unity 6 editor (6000.0 or newer; 6.3 LTS recommended) with Windows Build Support.
2. Unzip this folder, then in Unity Hub choose Add > Add project from disk and pick ZimensionUnity.
   The first open downloads the pinned packages (internet needed once) and sets up URP,
   input handling and colour space.
3. Zimension > Import from Zimension...
   - "From a Zimension link": paste the link from Zimension's Export > Unity.
   - "From a file": pick an export .zip (Export > Unity or Export > Web).
4. The base scene opens. Press Play, then Start. WASD/mouse, Shift sprint, Space jump,
   F interact, P pause, F3 diagnostics.

Your own work goes in Assets/Zimension/UserContent (never touched by re-imports).
Documentation: https://github.com/zeeshan-zinmension/zimension-to-engines
"""

def main():
    out_dir = sys.argv[sys.argv.index("--out") + 1] if "--out" in sys.argv else os.path.join(ROOT, "out")
    version = json.load(open(os.path.join(PROJECT, "Packages", "com.zimension.runtime", "package.json")))["version"]
    os.makedirs(out_dir, exist_ok=True)
    target = os.path.join(out_dir, f"ZimensionUnity-{version}.zip")
    count = 0
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("ZimensionUnity/README-FIRST.txt", README)
        for dirpath, dirnames, filenames in os.walk(PROJECT):
            dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
            for name in sorted(filenames):
                if os.path.splitext(name)[1] in SKIP_EXT:
                    continue
                full = os.path.join(dirpath, name)
                arc = os.path.join("ZimensionUnity", os.path.relpath(full, PROJECT)).replace(os.sep, "/")
                zf.write(full, arc)
                count += 1
    print(f"{target}: {count} files, {os.path.getsize(target) // 1024} KB")

if __name__ == "__main__":
    main()
