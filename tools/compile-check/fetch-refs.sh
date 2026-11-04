#!/usr/bin/env bash
# Downloads UnityEngine/UnityEditor reference assemblies from NuGet for compile-checking the
# package outside Unity (no Unity install or licence needed). These are older (2021.x) reference
# assemblies: they catch syntax, typing and most API mistakes; Unity 6 itself remains the final check.
# Usage: tools/compile-check/fetch-refs.sh [destination]   (default: .unity-refs)
set -euo pipefail
DEST="${1:-.unity-refs}"
mkdir -p "$DEST/engine" "$DEST/editor" "$DEST/tmp"
curl -sSL https://api.nuget.org/v3-flatcontainer/unityengine.modules/2021.3.33/unityengine.modules.2021.3.33.nupkg -o "$DEST/tmp/modules.nupkg"
curl -sSL https://api.nuget.org/v3-flatcontainer/unity3d.sdk/2021.1.14.1/unity3d.sdk.2021.1.14.1.nupkg -o "$DEST/tmp/sdk.nupkg"
(cd "$DEST/tmp" && unzip -q -o modules.nupkg -d modules && unzip -q -o sdk.nupkg -d sdk)
cp "$DEST"/tmp/modules/lib/netstandard2.0/*.dll "$DEST/engine/"
cp "$DEST/tmp/sdk/lib/UnityEditor.dll" "$DEST/editor/"
rm -rf "$DEST/tmp"
echo "Unity reference assemblies in $DEST (engine: $(ls "$DEST/engine" | wc -l) dlls, editor: UnityEditor.dll)"
