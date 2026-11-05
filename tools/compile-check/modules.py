#!/usr/bin/env python3
"""Prints the Unity engine module DLLs the package does NOT declare in package.json, one full path
per line, so the compile check leaves them out exactly as Unity does (CS1069 'type forwarded to
UnityEngine.XModule' when the package uses a module it does not depend on).
Usage: modules.py <unity-refs/engine dir>"""
import json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGE = os.path.join(HERE, "..", "..", "unity", "ZimensionUnity", "Packages", "com.zimension.runtime", "package.json")
# Optional built-in module packages and the engine assemblies they provide.
MODULES = {
    "ai": ["AIModule"], "androidjni": ["AndroidJNIModule"], "animation": ["AnimationModule", "DirectorModule"],
    "assetbundle": ["AssetBundleModule"], "audio": ["AudioModule", "DSPGraphModule"], "cloth": ["ClothModule"],
    "director": [], "imageconversion": ["ImageConversionModule"], "imgui": ["IMGUIModule"],
    "jsonserialize": ["JSONSerializeModule"], "particlesystem": ["ParticleSystemModule"], "physics": ["PhysicsModule"],
    "physics2d": ["Physics2DModule"], "screencapture": ["ScreenCaptureModule"], "terrain": ["TerrainModule"],
    "terrainphysics": ["TerrainPhysicsModule"], "tilemap": ["TilemapModule"], "ui": ["UIModule"],
    "uielements": ["UIElementsModule", "UIElementsNativeModule"], "umbra": ["UmbraModule"],
    "unityanalytics": ["UnityAnalyticsModule", "UnityAnalyticsCommonModule"], "unitywebrequest": ["UnityWebRequestModule"],
    "unitywebrequestassetbundle": ["UnityWebRequestAssetBundleModule"], "unitywebrequestaudio": ["UnityWebRequestAudioModule"],
    "unitywebrequesttexture": ["UnityWebRequestTextureModule"], "unitywebrequestwww": ["UnityWebRequestWWWModule"],
    "vehicles": ["VehiclesModule"], "video": ["VideoModule"], "vr": ["VRModule"], "wind": ["WindModule"], "xr": ["XRModule"],
}
declared = {k.split(".")[-1] for k in json.load(open(PACKAGE))["dependencies"] if k.startswith("com.unity.modules.")}
engine = sys.argv[1]
for module, dlls in MODULES.items():
    if module in declared: continue
    for dll in dlls:
        if module == "animation" and dll == "DirectorModule" and "director" in declared: continue
        path = os.path.join(engine, f"UnityEngine.{dll}.dll")
        if os.path.exists(path): print(path)
