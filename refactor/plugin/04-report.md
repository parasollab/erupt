# Plugin refactor — Phase 4 report (generator and templates)

Implements `04-plan.md`. Branch `design_refactor`, on top of Phase 3 (`8965afc`).

## What was built

### `Erupt.Core.Editor` (`Packages/com.erupt.core/Editor/`, Editor-only; references core only)

| File | Role |
|---|---|
| `PluginTemplate.cs` | one descriptor per family (Blank / Planning / Demonstration): description, base class, and the file list = `Templates/Common/**` + `Templates/<Family>/**` |
| `PluginGenerator.cs` | `PluginSpec` (name, package id, namespace, family, display name; derived id/assembly; validation) and the pure renderer: `Plan` (what would be written) and `Generate` (writes; refuses a non-empty target). Tokens `{{PluginName}}`, `{{PackageId}}`, `{{Namespace}}`, `{{Family}}`, `{{PluginId}}`, `{{DisplayName}}`, `{{AssemblyName}}` in contents; `__PluginName__` in file names; template files end in `.txt` so Unity never compiles them |
| `PluginGeneratorWindow.cs` | `ERUPT/Plugins/Create Plugin...`: name (package id / namespace / display name follow it until edited), family with description, "Add to open scene", live file plan, Create |
| `PluginGeneratorFinisher.cs` | `[InitializeOnLoad]` + `SessionState`: after the new assembly compiles, finds the generated type via `TypeCache`, saves `Prefabs/<Name>Plugin.prefab`, optionally instances it into the open scene next to `ERUPT` (scene instance only; never edits a prefab asset). Gives up loudly after five reloads if the type never compiles |

### Templates (`Editor/Templates/`)

Common (all families): `package.json` (depends on `com.erupt.core`), `Runtime/<Name>.asmdef`
(`Erupt.Plugins.<Name>`, references core + its own `.Messages`), `Runtime/Messages/<Name>.Messages.asmdef`
+ `README.md` (message-browser output path, no duplicates of core messages), `Tests/<Name>.Tests.asmdef`
(references `Erupt.TestSupport` and `Erupt.Plugins.Tests` for `FakeRosBus` / `FakeUiHost`),
`README.md` (numbered setup steps), `MESSAGES.md`.
Per family: `Runtime/<Name>Plugin.cs` deriving from `EruptPluginBehaviour` / `PlanningPlugin` /
`DemonstrationPlugin` with `TODO` hooks and comments citing the guideline part, and
`Tests/<Name>SmokeTests.cs` registering against a fake context (Planning asserts the
`planner-<id>` tab and the bound verbs; Demonstration the `demos-<id>` tab and no recording
outside Teach).

### Tests (`Tests/Editor`, `Erupt.Core.Editor.Tests`, EditMode)

`PluginGeneratorTests`: each family writes the nine-file set with no token left and the right
base class / namespace / id; a non-empty target and bad names are refused; `Plan` equals what
`Generate` writes; the shipped MoveIt and MTC packages have the Planning template's shape
(package.json, README, MESSAGES, runtime + messages + tests asmdefs, `<Name>Plugin.cs : PlanningPlugin`, `Prefabs/`).

### Docs

`Documentation~/plugin-authoring.md` (contract, context, where a feature goes per Part 4,
families, messages, testing, generator) and `Documentation~/architecture.md` (packages,
assembly graph, the ERUPT root, flows).

### Removed

`Assets/Erupt.App/Editor/` (the last migration tool, `UiPortWiring`, and the `Erupt.App.Editor`
assembly). The refactor's scene surgery is complete; nothing editor-side remains in the app.

## Found during the manual gate

- **Two packages from one plugin name.** A second run with package id `demo2` but plugin name
  `Demo` defined the same three assemblies as the first and neither compiled. The window now
  refuses a plugin name whose assemblies already exist under `Packages` or `Assets`
  (`PluginGenerator.AssemblyConflicts`, tested); the finisher ignores a pending job whose package
  folder is gone.
- **The new package was invisible.** `AssetDatabase.Refresh` does not make the Package Manager
  discover a folder added to `Packages/` at runtime, so nothing compiled and the prefab step
  never ran. `Create` now calls `PackageManager.Client.Resolve()` first.

## Gate

| Check | Status |
|---|---|
| Compile clean | PASS — clean on first import |
| EditMode tests (`Erupt.Core.Editor.Tests`, `Erupt.Boundary.Tests`) | PASS — 10/10 (7 generator, 3 boundary; read from the Editor's TestResults.xml) |
| Manual: generate Planning plugin `Demo` → compiles, `Prefabs/DemoPlugin.prefab` exists, scene instance under `ERUPT`, `Erupt.Plugins.Demo.Tests` passes, then deleted | PASS — after the two fixes above: package imported and compiled, prefab created, instance under `ERUPT`, PlayMode 155/155 including `Erupt.Plugins.Demo.Tests` 1/1; package and instance removed afterwards |

## Next

Phase 5 (`refactor_plan.md` § Phase 5): absorb RADER's generic parts into core, prove the
Demonstration template with `com.erupt.plugin.rader`, drop the submodule.
