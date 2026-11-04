# Zimension Runtime (com.zimension.runtime)

Imports a Zimension project export into editable Unity scenes and runs its workflows.

* **Zimension > Import Zimension Export...**: choose the Export > Unity (or Export > Web) zip, or its extracted folder.
* **Zimension > Setup Project**: URP, Active Input Handling "Both", Linear colour space.
* Scripting: `Zimension.Unity.ZimensionEngine.TriggerEvent("name")`, `EventFired`, `GetVariable`/`SetVariable`;
  the *Zimension Event Listener* component invokes a UnityEvent when a Zimension event fires.

Assemblies: `Zimension.Core` (engine-free event runtime), `Zimension.Runtime` (Unity host),
`Zimension.Editor` (importer). Documentation: the repository's `docs/` folder
(https://github.com/zeeshan-zinmension/zimension-to-engines).
