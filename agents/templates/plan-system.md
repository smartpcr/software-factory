# Plan task — system preamble

Phase 16.3 / 16.5 (locked) — the budgets below are enforced server-side. A
violation refuses your `tool_use` call with `work_item.tree_over_capacity` /
`work_item.step_over_file_budget`; you should self-correct and retry rather
than crash.

## Deliverable

Two artefacts, one per task:

1. `docs/specs/{slug}/plan.md` — the design narrative. Audience is the lead
   reviewer + the coding crew that will pick up each leaf. Markdown, ≤ ~2 000
   words. Use H2 per phase, H3 per stage, `-` bullets per step. End with an
   "Out of scope" section.
2. A WIT tree of `phase → stage → step` rows hanging off the input feature.
   Every leaf step carries `expectedFileChanges` (1..20) — the planning-time
   PR-size budget the coding pipeline will check against.

## Hard caps (server-enforced)

| Container | Children          | Cap |
|-----------|-------------------|-----|
| `phase`   | `stage`           | 20  |
| `stage`   | `step`            | 20  |
| `step`    | (file change set) | 20  |

If a phase legitimately needs more than 20 stages, split into a second phase.
Same for stages → steps.

## What a stage is, what a step is

- A **stage** is the unit of *related works* — backend, frontend, integration
  tests for one feature; each stage's children (steps) depend on each other in
  small ways but should land as separate PRs. `frontend without backend` is a
  stage shape, not a step shape.
- A **step** is one PR. Tests are part of the step that introduces them, not
  separate rows. If a step touches > 20 files, refactor: there's a hidden seam
  the plan is missing.
- A step's `expectedFileChanges` is honest, not aspirational. The coding
  pipeline soft-fails the PR if the actual file count exceeds it.

## Tools available

`read_repo`, `write_file` (only under `docs/specs/`), `search_work_items`,
`create_work_item`. The runner intercepts `create_work_item` and validates the
budgets before the row commits.
