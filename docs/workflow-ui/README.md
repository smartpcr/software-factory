# Workflow Designer UI — Per-Stage Specs

This folder contains per-stage implementation specs expanded from the master plan at [`../workflow-ui-plan.md`](../workflow-ui-plan.md).

## Structure

```
workflow-ui/
├── README.md                      # this file
├── phase-0/                       # Foundations & Gates  (3 stages)
├── phase-1/                       # Host, Contract, CI   (3 stages)
├── phase-2/                       # Node Kinds, Schemas  (4 stages)
├── phase-3/                       # Canvas, Palette      (3 stages)
├── phase-4/                       # Property Grid        (3 stages)
├── phase-5/                       # Mapper, Fixtures     (3 stages)
├── phase-6/                       # SignalR Telemetry    (3 stages)
├── phase-7/                       # Auth, Persistence    (3 stages)
└── phase-8/                       # Packaging, Release   (3 stages)
```

Each stage file uses the per-step template defined in §0.1 of the master plan and adds the extended fields required for generator↔evaluator scoring.

## Per-step spec template (mandatory sections)

Every step in every stage file MUST contain the following sections, in order:

1. **Status** — `[ ]` | `[~]` | `[x]` (roll-up rule per §0.1.1)
2. **Effort** — engineering days
3. **Goal** — one-paragraph statement of the outcome this step produces
4. **Depends on** — list of prior step IDs (empty if none)
5. **Inputs** — source files, contracts, fixtures required to start
6. **Deliverables** — bulleted, concrete list of files created/modified (paths), code artifacts, or configuration entries; ≤ 20 files
7. **Acceptance criteria** — objective, testable conditions with pass/fail bar (checklist form)
8. **Unit tests** — enumerated test cases with assertion-level detail; 100% new-code coverage enforced by coverlet
9. **Integration tests** — when applicable; scenario + harness + assertion outline
10. **Golden-file / parity tests** — when touching DTOs, YAML, or engine surface
11. **Edge cases & negative tests** — explicit list (invalid input, missing field, wrong type)
12. **Observability hooks** — logs, metrics, traces this step introduces or extends
13. **Risks & mitigations** — what could fail + fallback
14. **Evaluator gap-analysis prompts** — 5–10 seed questions the evaluator agent must answer when scoring this step (used by the generator↔evaluator loop)
15. **Scoring rubric (step-level, weights sum to 100)**
    - Deliverable completeness 25
    - Test coverage & assertion quality 25
    - Contract/parity fidelity 20
    - Edge-case handling 10
    - Observability 10
    - Clarity & traceability 10
16. **Definition of done** — evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.

## Definition of Ready (per-step prerequisite checklist)

Before a generator agent begins work on a step, the following prerequisites must be satisfied:

- [ ] All steps listed in the step's "Depends on" section are `[x]` (done).
- [ ] Engine parity tests (§0.2.1, §0.2.2) are green in CI.
- [ ] YAML round-trip gate (§0.2.3) is green for all fixtures that the step's surface touches.
- [ ] The stage file for this step has been read in full by the generator (context priming).
- [ ] Any engine source files cited in the step's "Inputs" section have been verified against the current HEAD of `src/ExecutionEngine/`.
- [ ] The generator confirms no banned identifiers are present in its output: `NodePriority.Critical`, `YamlWorkflowLoader`, `ProgressUpdate` as event payload, `PowerShellScript` as RuntimeType, `react-jsonschema-form` as active dep, `Nodes/Policies/` as path.

## How this folder is used

The multi-agent generator↔evaluator loop consumes one stage file per iteration:

- **Generator** (Opus-4.7 or GPT-5.4) reads a stage file and produces code for one step at a time.
- **Evaluator** (the OTHER model) scores the result against §15 of the step using the gap prompts in §14.
- A step flips to `[x]` only when the evaluator's score ≥ 90 with no CRITICAL/HIGH findings.

See `../workflow-ui-plan.md` §3 for the full quality gate and §0.2 for global engine-parity gates.

## Model rotation rules

- Only Opus-4.7 and GPT-5.4 are permitted (no light models).
- Generator and evaluator MUST be different models for each step.
- Cross-model re-scoring is required when a step fails promotion twice.
