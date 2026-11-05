# Compile checks outside Unity

These projects compile the package's C# against Unity reference assemblies, without a Unity
install or licence. They are for development and CI only; Unity never sees them.

```bash
tools/compile-check/fetch-refs.sh .unity-refs
dotnet build tools/compile-check/Core.csproj
dotnet build tools/compile-check/Runtime.csproj
dotnet build tools/compile-check/Editor.csproj
```

Each project builds one Unity assembly on its own (`Zimension.Core`, `Zimension.Runtime`,
`Zimension.Editor`), referencing the others as separate assemblies. That way an `internal`
member used across an assembly boundary fails here exactly as it does in Unity. Obsolete-API
warnings are errors.

The reference assemblies are from Unity 2021 (the newest published on NuGet). They catch syntax,
type and most API errors; APIs added after 2021 are avoided or guarded. The URP setup code is
behind `ZIMENSION_URP` and is only compiled by Unity itself. Opening the project in the pinned
Unity 6 editor is the final compile check.
