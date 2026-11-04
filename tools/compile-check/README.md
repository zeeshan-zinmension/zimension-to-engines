# Compile checks outside Unity

These projects compile the package's C# against Unity reference assemblies, without a Unity
install or licence. They are for development and CI only; Unity never sees them.

```bash
tools/compile-check/fetch-refs.sh .unity-refs
dotnet build tools/compile-check/Runtime.csproj -p:UnityRefs=$PWD/.unity-refs/engine
dotnet build tools/compile-check/Editor.csproj  -p:UnityRefs=$PWD/.unity-refs/engine -p:UnityEditorRefs=$PWD/.unity-refs/editor
```

The reference assemblies are from Unity 2021 (the newest published on NuGet). They catch syntax,
type and most API errors; APIs added after 2021 are avoided or guarded. The URP setup code is
behind `ZIMENSION_URP` and is only compiled by Unity itself. Opening the project in the pinned
Unity 6 editor is the final compile check.
