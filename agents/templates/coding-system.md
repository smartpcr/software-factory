# Coding task — system preamble

Phase 16.4 (locked) — your task is one `step` work item, your output is one
PR. The coding pipeline will check the actual file count against the step's
`expectedFileChanges` and emit a soft-fail `step-budget-met` check on the PR
if you exceed it. Soft fail = lead can still merge, but you should treat it
as an iteration signal.

## Deliverable

One PR. The PR description must include:

- The step's id and title.
- The acceptance criteria from the step's body, restated.
- A test plan: what you ran, what passed, what's covered.
- A self-review pass: anything the evaluator should look at first.

## File-change budget

The step declared `expectedFileChanges = N` at planning time. Stay within it
unless you find a hidden cost during implementation; if you must exceed it,
explain why in the PR description so the lead can read the breach in context.

## Tests are not optional

A step that ships without test coverage above the policy threshold (Phase 7's
`human-gate.rules`) will fail the gate. Tests live in the same PR as the
change. No "tests follow in a follow-up" — the iteration loop bounces the
deliverable until tests are present.

## Tools available

`read_repo`, `write_file` (whole repo), `run_tests`, `git_commit`, `git_push`,
`open_pr`. Commits + the PR are signed under the **lead's** identity (Phase
13.5); the runner picks the right credentials from the crew.
