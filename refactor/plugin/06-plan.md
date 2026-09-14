# Plugin refactor — Phase 6 plan (rebrand, docs, release)

Copied verbatim from `refactor_plan.md` § Phase 6 at the start of the phase.

### Phase 6 — Rebrand, docs, release

- `ProjectSettings.asset` `productName: ERUPT`; root `README.md` rewritten (name expansion, architecture picture, quick start, ROS commands, plugin list, how to create a plugin); package `displayName`s and versions (`0.1.0` → `1.0.0-preview.1`); `CHANGELOG.md`, `LICENSE` in each package; `refactor/` index updated; move `ERUPT_Design_Guidelines.md` to `Documentation~/design-guidelines.md`; delete the stray prompt files at the repo root (`claude_code_refactor_prompt.md`, `two_handed_ik_agent_prompt.md`, `visionos_capability_probe_prompt.md`, `mtc_connector.md`, `mtc_implementation.md`) or fold them into `docs/`.
- Optional CI: `.github/workflows/tests.yml` using the batchmode commands below (needs a Unity licence secret; documented, not required).

## Interpretation for this phase (decided at start, 2026-09-14)

- Versions: every `com.erupt.*` package and every `com.erupt.*` dependency goes to `1.0.0-preview.1`, including the generator's `package.json` template (its own version stays `0.1.0`: a freshly generated plugin is new code).
- Licence: no `LICENSE` exists anywhere in the repo and GitHub reports `parasollab/erupt` as unlicensed. The lab's RADER repo is BSD-3-Clause, so BSD-3-Clause is used here (`LICENSE.md` per package, `LICENSE` at the root, `"license"` field in each `package.json`). **Maintainer confirms before the release tag.**
- Stray files: only `mtc_connector.md` and `mtc_implementation.md` still exist at the root (the three prompt files are already gone). `mtc_connector.md` is the MTC ROS interface reference and moves to `com.erupt.plugin.mtc/Documentation~/ros-interface.md`; `mtc_implementation.md` planned the retired UI Toolkit dashboard and is deleted (recoverable from git).
- Package READMEs for MoveIt and MTC still describe the Phase 2 state (UI Toolkit panels "ported in Phase 3"); they are refreshed as part of the docs work.
- CI is added as a `workflow_dispatch`-only workflow so it never fails a push before the licence secret exists.
- `companyName` (`DefaultCompany`) and `bundleVersion` (`0.1.0`) are not in the plan and are left for the maintainer (both change the persistent-data path / store metadata).
- Gate: compile clean in the open Editor after the package re-resolve; EditMode `PluginGeneratorTests` + `AssemblyBoundaryTests`; PlayMode suites unchanged; the three grep commands from `refactor_plan.md` § Verification; `git status` shows no stray `.meta` churn from the new files.
