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

## Explicit constraints and cross-stage invariants

An **explicit constraint** is any quantified / bounded statement an upstream
stage makes that a downstream stage relies on. Examples (the list is
illustrative, not exhaustive):

- Migration range pins: `0001..0034`, `migrations through 0034`.
- Closed-set assertions: `pr_ingest_mode = {backfill, delta}`,
  `pri_run_status = {claimed, running, succeeded, failed, aborted}`.
- Numeric caps: `default 600 seconds`, `max limit 200`.
- Enumerations: "every `pri_*` knob", "all `TestIntegration*` under
  `internal/pringester/...`", "all four ENUMs added by stage 1.4".

Every explicit constraint creates an **obligation on every downstream stage
that adds an item inside the constraint's referent set**. If Stage 4.3
introduces a feature that needs a new migration, Stage 1.x's "migrations
0001..0034" pin AND Stage 6.x's gate pin BOTH go stale and BOTH must be
updated in the same iteration — or the consumer stage will reference a stale
set and either fail-open (silent skip) or fail-closed (drift the gate from
reality).

**Rules you MUST follow when authoring a plan:**

1. **Anchor every constraint to its producers.** When a stage declares a
   range / closed-set / cap / enumeration, identify which stage(s) populate
   the set. If the set has no producer in this plan (i.e. it's all baseline
   from the repo as of the plan's start), say so explicitly with the phrase
   "baseline only — no producer stages in this plan".
2. **When you add a new producer downstream, update upstream constraints.**
   A new migration file added by Stage 4.x means the gate pin in Stage 6.x
   must be raised. A new ENUM value added by Stage 5.x means Stage 1.4's
   closed-set assertion must be expanded. Don't leave it for the
   implementing engineer to "interpret" — the plan is the contract.
3. **Pin numbers must be 4-digit, well-formed, and consistent.** A pin
   `0001..0034` in Stage 1.4 and a pin `0001..0030` in Stage 6.4 of the
   same plan is a contradiction. Either align them or document the
   intentional divergence in prose immediately adjacent to the lower pin.
4. **Migration files are introduced via explicit `migrations/NNNN_<name>.sql`
   steps.** A stage that needs a schema change MUST include a step adding
   the migration file — never imply it from prose like "compute the
   deterministic `cluster_id_within_run`" without an accompanying step
   that adds the migration that defines the column. The server-side
   `PlanConstraintLinter` flags pin upper bounds that fall below any
   explicitly-added migration file in the same plan.

The doc-evaluator runs `PlanConstraintLinter.Validate` against your draft
every iteration; the evaluator prompt embeds the linter's REFUSAL CRITERIA
verbatim. A constraint violation holds the score below pass until you fix
the producer/consumer alignment.

## Tools available

`read_repo`, `write_file` (only under `docs/specs/`), `search_work_items`,
`create_work_item`. The runner intercepts `create_work_item` and validates the
budgets before the row commits.
