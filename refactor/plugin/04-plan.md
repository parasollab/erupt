# Plugin refactor — Phase 4 plan (generator and templates)

Copied verbatim from `refactor_plan.md` § Phase 4 at the start of the phase.

### Phase 4 — Generator and templates

- `Erupt.Core.Editor`: `[MenuItem("ERUPT/Plugins/Create Plugin...")]` → `PluginGeneratorWindow : EditorWindow` (name, package id, namespace, template family: Blank / Planning / Demonstration, output `Packages/com.erupt.plugin.<name>`, "Add to open scene" toggle).
- `PluginTemplate` (plain C# descriptor, one per family) listing template files under `Editor/Templates/<family>/` (`*.txt` with `{{PluginName}}`, `{{PackageId}}`, `{{Namespace}}`, `{{Family}}` tokens): `package.json`, `Runtime/{{PluginName}}.asmdef`, `Runtime/{{PluginName}}Plugin.cs` (derives from the family base with `TODO` hooks and comments citing the guideline part), `Runtime/Messages/README.md` + `{{PluginName}}.Messages.asmdef`, `Tests/{{PluginName}}.Tests.asmdef` + `{{PluginName}}SmokeTests.cs` (asserts the plugin registers against a `FakeRosBus` context), `README.md` with numbered setup steps (generate messages, drop `{{PluginName}}Plugin.prefab` into the scene next to `ERUPT`, assign robot root, run tests), `MESSAGES.md`.
- Prefab creation via `PrefabUtility.SaveAsPrefabAsset` on a temp GameObject carrying the generated behaviour (after `AssetDatabase.Refresh` + compilation; use `[InitializeOnLoad]` + `SessionState` to finish the prefab step post-recompile, the same two-step pattern `OpenCVForUnityDefine.cs` uses for defines). "Add to open scene" instantiates the prefab as a scene object (never edits a prefab asset) and assigns references with `SerializedObject`.
- The MoveIt and MTC packages are re-checked against the Planning template so the template is provably the shape of a real plugin (a test regenerates "Blank" and "Planning" into a temp folder and asserts the file set).
- Docs: `Documentation~/plugin-authoring.md` (contract, lifecycle, where a feature goes per Guidelines Part 4, verb/tab/widget rules, messages workflow, testing with `FakeRosBus`), `Documentation~/architecture.md` (assembly graph).
- Gate: compile; tests (generator file-set test); manual: generate a Planning plugin named `Demo`, project compiles, prefab exists, smoke test passes, delete it.

