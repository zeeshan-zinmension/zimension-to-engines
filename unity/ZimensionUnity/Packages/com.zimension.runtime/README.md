# Zimension Runtime (com.zimension.runtime)

Imports a Zimension project export into editable Unity scenes and runs its workflows.

* **Zimension > Import from Zimension...**: paste the link from Zimension's Export > Unity (downloaded, resumable,
  verified, then imported; **Pull latest** re-imports newer exports), or pick an export zip/folder.
* **Zimension > Setup Project**: URP, Active Input Handling "Both", Linear colour space.
* Scripting: `Zimension.Unity.ZimensionEngine.TriggerEvent("name")`, `EventFired`, `GetVariable`/`SetVariable`;
  the *Zimension Event Listener* component invokes a UnityEvent when a Zimension event fires.

Assemblies: `Zimension.Core` (engine-free event runtime), `Zimension.Runtime` (Unity host),
`Zimension.Editor` (importer). Documentation: the repository's `docs/` folder
(https://github.com/zeeshan-zinmension/zimension-to-engines).

Third-party: the play UI uses the Ubuntu font (Runtime/Resources/ZimensionFonts), © Canonical Ltd,
under the Ubuntu Font Licence 1.0 (LICENSE-UbuntuFontLicence.txt beside the fonts), matching the
Zimension web player's UI font.
