# Plugin refactor — Phase 6 report (rebrand, docs, release)

Implements `06-plan.md`. Branch `design_refactor`, on top of Phase 5 (`2c48399`). Docs,
manifests and settings only: no C# changed, no scene or prefab changed.

## What was done

### Rebrand and versions

| Change | Where |
|---|---|
| `productName: XRViz` → `ERUPT` | `ProjectSettings/ProjectSettings.asset` (`companyName` and `bundleVersion` untouched, see Deviations) |
| `version: 0.1.0` → `1.0.0-preview.1`, `"license": "BSD-3-Clause"` added | all four `Packages/com.erupt.*/package.json`; every `com.erupt.*` dependency bumped to match |
| template dependency on core → `1.0.0-preview.1` (generated plugins still start at `0.1.0`) | `com.erupt.core/Editor/Templates/Common/package.json.txt` |
| lock file dependency versions | `Packages/packages-lock.json` (the Editor rewrites this on resolve; edited so the diff is coherent) |

### Release files

- `LICENSE` (root) and `LICENSE.md` in each package: BSD 3-Clause, © 2026 Parasol Laboratory,
  University of Illinois Urbana-Champaign.
- `CHANGELOG.md` in each package (Keep a Changelog; one `1.0.0-preview.1` entry summarising
  what the refactor produced and removed).
- `.meta` files written by hand for the eight new package-root files (fresh GUIDs, the
  `TextScriptImporter` block copied from `README.md.meta`), so opening the Editor creates none.

### Docs

| File | Change |
|---|---|
| `README.md` (root) | rewritten: name expansion, architecture picture, quick start (clone with submodules, ROS IP, scenes, tests), how the app is used (tiers, core flows), ROS 2 host start order, plugin table, how to create a plugin, repo layout, licence |
| `Packages/com.erupt.core/Documentation~/design-guidelines.md` | `git mv` from `ERUPT_Design_Guidelines.md` |
| `Packages/com.erupt.plugin.mtc/Documentation~/ros-interface.md` | `git mv` from `mtc_connector.md` with a provenance header (ROS sections remain the reference for `MtcClient`; § 3 describes the pre-refactor panels, now `SolutionsTab` + trajectory verbs) |
| `mtc_implementation.md` | deleted: an implementation plan for the retired UI Toolkit dashboard, recoverable from git |
| `Packages/com.erupt.plugin.moveit/README.md`, `…mtc/README.md` | rewritten to the shipped Phase 3–5 state (they still described UI Toolkit panels "ported in Phase 3"); numbered setup steps like the rader / template READMEs |
| `Packages/com.erupt.core/README.md` | Documentation and licence pointers |
| `Documentation~/plugin-authoring.md`, `architecture.md` | link to `design-guidelines.md`; history paragraph names the tag, the index and RADER's provenance |
| `refactor/README.md` | new index of both document series with landing commits |
| `.github/workflows/tests.yml` | optional CI: `boundary` job (the three grep gates + a version-agreement check) on push / PR without a licence; `unity-tests` (game-ci, EditMode + PlayMode) on `workflow_dispatch` only, secrets documented in the file header |

The three prompt files the plan lists (`claude_code_refactor_prompt.md`,
`two_handed_ik_agent_prompt.md`, `visionos_capability_probe_prompt.md`) were already gone.

## Deviations

- **Licence chosen, not confirmed.** Nothing in the repo or on GitHub (`parasollab/erupt`
  reports no licence) named one; the lab's RADER repo is BSD-3-Clause, so that was used.
  The maintainer confirms or swaps it before tagging: `LICENSE`, four `LICENSE.md`, four
  `"license"` fields, the root README's Licence section.
- `companyName: DefaultCompany` and `bundleVersion: 0.1.0` left alone: not in the plan, and
  both change store metadata / the persistent-data path. Note that the `productName` change
  alone already moves the Editor's persistent data: test results now land in
  `~/Library/Application Support/DefaultCompany/ERUPT/TestResults.xml`.
- The stray-script grep from `refactor_plan.md` § Verification matches the 34 scripts under
  `Assets/TextMesh Pro/Examples & Extras/Scripts` (Unity's imported sample, not project code).
  The CI job excludes that path; the plan's command is otherwise unchanged.
- `.gitmodules` keeps its SSH URLs; the README documents the `insteadOf` rewrite for HTTPS-only
  machines instead of changing the maintainer's submodule config.

## Gate

| Check | Status |
|---|---|
| Core references no plugin message types (`grep -rlE "RosMessageTypes\.(Moveit\|MoveitTaskConstructorMsgs\|StudyInterfaces)" Packages/com.erupt.core`) | PASS — no matches |
| No asmdef targets `Assembly-CSharp` | PASS — no matches |
| No scripts outside `Erupt.App` (plan exclusions + `Assets/TextMesh Pro`) | PASS |
| `package.json` files parse; versions and `com.erupt.*` dependencies agree | PASS — checked with the same python as the CI job |
| Workflow YAML parses | PASS |
| Compile clean in the open Editor after the package re-resolve | PASS — Editor.log 2026-09-14 08:29 lists the four packages resolved from their new manifests, no `error CS` |
| EditMode `AssemblyBoundaryTests` + `PluginGeneratorTests` (the template `package.json` changed) | PASS — maintainer ran the suite in the Test Runner 2026-09-14 ("all tests pass"); the first Run All click hit the Test Framework's stale-tree NRE (`TestListGUI.ConstructFilter`), cleared by reopening the window |
| PlayMode suites unchanged | PASS — 184/184 (`DefaultCompany/ERUPT/TestResults.xml`, 08:31): Environment 6, Interaction 35, MoveIt 25, Mtc 10, Rader 24, Plugins 21, Ui 63 |
| No `.meta` churn from the new files after the Editor opens | PASS — the only untracked `.meta` files are the eight hand-written ones, unmodified by the import |
| Quest smoke | DEFERRED — no behaviour changed in this phase |

## Carried past Phase 6 (needs a device and a ROS host)

From the Phase 3 and Phase 5 gates, never run offline:

1. Plan → preview → execute with MoveIt on the Quest against a live host; MTC solutions and
   pick/place recording; ROS traffic diff against a baseline (`refactor/ros-traffic-diff.sh`).
2. Teach mode: RADER records a demonstration and publishes (`/{ns}/interaction` false→true,
   one `/{ns}/joint_trajectory`).

## Release checklist for the maintainer

1. Confirm the licence (or replace BSD-3-Clause everywhere listed above).
2. Decide whether the uncommitted Editor upgrade (6000.2.1f1 → 6000.3.15f1: `ProjectVersion`,
   manifest modules, settings / `.meta` churn) is committed with Phase 6 or separately.
3. Run the pending gate rows; paste back.
4. Commit; `git tag v1.0.0-preview.1`; `git push origin design_refactor --tags` (pushes must
   come from the maintainer terminal).
5. Merge `design_refactor` → `main` when the device checks above have run.
