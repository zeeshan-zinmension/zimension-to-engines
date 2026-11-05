#!/usr/bin/env python3
"""Create missing Unity .meta files for the com.zimension.runtime package with deterministic GUIDs.

A package installed from a Git URL is immutable in Unity: files without a .meta are ignored there.
GUIDs are derived from the package-relative path (md5), so they are stable across machines and
regenerating never changes an existing one. Run after adding files:  python3 tools/generate_metas.py
Use --check to fail (exit 1) when a .meta is missing, e.g. in CI."""
import hashlib, os, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "unity", "ZimensionUnity", "Packages", "com.zimension.runtime")
TAIL = "  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"

def guid(rel):
    return hashlib.md5(("com.zimension.runtime/" + rel.replace(os.sep, "/")).encode()).hexdigest()

def meta(rel, is_dir):
    g = guid(rel)
    if is_dir:
        return f"fileFormatVersion: 2\nguid: {g}\nfolderAsset: yes\nDefaultImporter:\n{TAIL}"
    ext = os.path.splitext(rel)[1].lower()
    if ext == ".cs":
        return (f"fileFormatVersion: 2\nguid: {g}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n"
                "  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    if ext in (".ttf", ".otf"):
        return (f"fileFormatVersion: 2\nguid: {g}\nTrueTypeFontImporter:\n  externalObjects: {{}}\n  serializedVersion: 4\n"
                "  fontSize: 16\n  forceTextureCase: -2\n  characterSpacing: 0\n  characterPadding: 1\n  includeFontData: 1\n"
                "  fontNames: []\n  fallbackFontReferences: []\n  customCharacters: \n  fontRenderingMode: 0\n"
                "  ascentCalculationMode: 1\n  useLegacyBoundsCalculation: 0\n  shouldRoundAdvanceValue: 1\n"
                "  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    if ext in (".wav", ".mp3", ".ogg", ".aif", ".aiff"):
        # Unity fills the remaining audio import settings with their defaults.
        return (f"fileFormatVersion: 2\nguid: {g}\nAudioImporter:\n  externalObjects: {{}}\n  serializedVersion: 7\n"
                "  defaultSettings:\n    serializedVersion: 2\n    loadType: 0\n    sampleRateSetting: 0\n    sampleRateOverride: 44100\n"
                "    compressionFormat: 0\n    quality: 1\n    conversionMode: 0\n    preloadAudioData: 1\n"
                "  platformSettingOverrides: {}\n  forceToMono: 0\n  normalize: 1\n  loadInBackground: 0\n  ambisonic: 0\n  3D: 1\n"
                "  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    if ext == ".asmdef":
        return f"fileFormatVersion: 2\nguid: {g}\nAssemblyDefinitionImporter:\n{TAIL}"
    if os.path.basename(rel) == "package.json":
        return f"fileFormatVersion: 2\nguid: {g}\nPackageManifestImporter:\n{TAIL}"
    if ext in (".md", ".txt", ".json"):
        return f"fileFormatVersion: 2\nguid: {g}\nTextScriptImporter:\n{TAIL}"
    return f"fileFormatVersion: 2\nguid: {g}\nDefaultImporter:\n{TAIL}"

def main():
    check = "--check" in sys.argv
    root = os.path.normpath(ROOT)
    missing = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if not d.startswith(".") and not d.endswith("~")]
        entries = [(d, True) for d in dirnames] + [(f, False) for f in filenames if not f.endswith(".meta") and not f.startswith(".")]
        for name, is_dir in entries:
            full = os.path.join(dirpath, name)
            target = full + ".meta"
            if os.path.exists(target):
                continue
            rel = os.path.relpath(full, root)
            missing.append(rel)
            if not check:
                with open(target, "w", newline="\n") as f:
                    f.write(meta(rel, is_dir))
    if check and missing:
        print("Missing .meta files:\n  " + "\n  ".join(missing))
        return 1
    print(("Missing" if check else "Created") + f" {len(missing)} .meta file(s)")
    return 0

if __name__ == "__main__":
    sys.exit(main())
