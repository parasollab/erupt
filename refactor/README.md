# Refactor history

Two document series record how ERUPT reached its current shape. Each plan was copied
verbatim from the governing plan at the start of its phase; each report says what actually
landed, deviations included, and the gate results. Paths inside the older documents are as
they were when written (`ERUPT_Design_Guidelines.md` is now
`Packages/com.erupt.core/Documentation~/design-guidelines.md`; the `claude_code_refactor_prompt.md`
they cite was a working prompt and is gone).

## Guidelines phases (interaction router, tier UI, ROS seam)

| Document | Content |
|---|---|
| `00-survey.md` | conformance survey of the old `xrviz` code against the design guidelines |
| `01-plan.md`, `01-report.md` | Phase 1: interaction abstraction (`InteractionRouter`, backends) |
| `02-plan.md` | Phase 2: tier UI and selection (plan; landed piecemeal, superseded by the plugin series) |
| `backlog.md` | violations and hazards logged out of scope (`open` / `scheduled` / `done` / `wontfix`) |
| `ros-traffic-diff.sh` | records / compares a ROS traffic capture as a behaviour oracle between phases |

## Plugin refactor (`plugin/`), governed by `../refactor_plan.md`

| Phase | Plan / report | Landed |
|---|---|---|
| 0 — hygiene, tag, prune | `plugin/00-plan.md`, `plugin/00-report.md` | `d6f94fb` (tag `pre-plugin-refactor` = `46e3bef`) |
| 1 — extract packages, cut environment from MoveIt | `plugin/01-plan.md`, `plugin/01-report.md` | `9baa7ca` |
| 2 — plugin contract, host, robot interface, selection merge | `plugin/02-plan.md`, `plugin/02-report.md` | `81a73b0` |
| 3 — UI port and legacy retirement | `plugin/03-plan.md`, `plugin/03-report.md` | `8965afc` |
| 4 — generator and templates | `plugin/04-plan.md`, `plugin/04-report.md` | `446587f` |
| 5 — RADER absorb and rader plugin | `plugin/05-plan.md`, `plugin/05-report.md` | `2c48399` |
| 6 — rebrand, docs, release | `plugin/06-plan.md`, `plugin/06-report.md` | this phase |

Open items carried past Phase 6 (device / ROS-host checks that no phase could run offline)
are listed at the end of `plugin/06-report.md`.
