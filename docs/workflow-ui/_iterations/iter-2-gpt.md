# Iteration 2 — Stage-file Enhancement Pass

**Date:** 2026-04-17
**Agent:** Copilot CLI (GPT-5.4 audit perspective)
**Master plan version:** v1.5-iter1 → v1.6-iter2
**Files touched in scope:** 1 master plan + 28 stage files

## Enhancement counts

- Stage files with new **Definition of Ready** sections: 28
- Stage files with new **checkbox legend**: 28
- Stage files with new **deliverable symbol contract**: 28
- Stage files with new **stage-wide observability / failure-mode / performance / security guardrails**: 28
- UI-facing stage files with new **accessibility checklist + manual verification focus**: 14
- Step files/steps repaired to restore the full 16-section template via missing **Inputs** sections: 12 steps across stages 2.2/2.3/2.4
- Engine-fidelity wording fixes applied: 1 high-impact fix (Stage 6.1 `WorkflowExecutionContext.Events` / `.Progress` in place of nonexistent `IEventSource`)

## Line deltas by stage file (top 10)

| File | Before | After | Delta |
|---|---:|---:|---:|
| `docs/workflow-ui/phase-2/stage-2.4-composition-round-trip.md` | 802 | 874 | +72 |
| `docs/workflow-ui/phase-2/stage-2.2-task-family-kinds.md` | 690 | 757 | +67 |
| `docs/workflow-ui/phase-2/stage-2.3-control-flow-kinds.md` | 472 | 534 | +62 |
| `docs/workflow-ui/phase-3/stage-3.1-canvas-primitives.md` | 704 | 763 | +59 |
| `docs/workflow-ui/phase-3/stage-3.2-palette-and-drag.md` | 602 | 661 | +59 |
| `docs/workflow-ui/phase-3/stage-3.3-connections.md` | 603 | 662 | +59 |
| `docs/workflow-ui/phase-4/stage-4.1-grid-framework.md` | 603 | 662 | +59 |
| `docs/workflow-ui/phase-4/stage-4.2-panel-partition.md` | 602 | 661 | +59 |
| `docs/workflow-ui/phase-4/stage-4.3-validation-surface.md` | 601 | 660 | +59 |
| `docs/workflow-ui/phase-6/stage-6.1-engine-event-bridge.md` | 573 | 632 | +59 |

## Full touched stage list

- `docs/workflow-ui/phase-2/stage-2.4-composition-round-trip.md` — 802 → 874 (+72)
- `docs/workflow-ui/phase-2/stage-2.2-task-family-kinds.md` — 690 → 757 (+67)
- `docs/workflow-ui/phase-2/stage-2.3-control-flow-kinds.md` — 472 → 534 (+62)
- `docs/workflow-ui/phase-3/stage-3.1-canvas-primitives.md` — 704 → 763 (+59)
- `docs/workflow-ui/phase-3/stage-3.2-palette-and-drag.md` — 602 → 661 (+59)
- `docs/workflow-ui/phase-3/stage-3.3-connections.md` — 603 → 662 (+59)
- `docs/workflow-ui/phase-4/stage-4.1-grid-framework.md` — 603 → 662 (+59)
- `docs/workflow-ui/phase-4/stage-4.2-panel-partition.md` — 602 → 661 (+59)
- `docs/workflow-ui/phase-4/stage-4.3-validation-surface.md` — 601 → 660 (+59)
- `docs/workflow-ui/phase-6/stage-6.1-engine-event-bridge.md` — 573 → 632 (+59)
- `docs/workflow-ui/phase-6/stage-6.2-hub-and-client.md` — 506 → 565 (+59)
- `docs/workflow-ui/phase-6/stage-6.3-live-monitor-ui.md` — 482 → 541 (+59)
- `docs/workflow-ui/phase-7/stage-7.1-auth.md` — 502 → 561 (+59)
- `docs/workflow-ui/phase-7/stage-7.2-persistence.md` — 565 → 624 (+59)
- `docs/workflow-ui/phase-7/stage-7.3-file-ops.md` — 419 → 478 (+59)
- `docs/workflow-ui/phase-8/stage-8.1-packaging.md` — 505 → 564 (+59)
- `docs/workflow-ui/phase-8/stage-8.2-docs.md` — 382 → 441 (+59)
- `docs/workflow-ui/phase-8/stage-8.3-release.md` — 414 → 473 (+59)
- `docs/workflow-ui/phase-0/stage-0.1-repo-scaffolding.md` — 378 → 425 (+47)
- `docs/workflow-ui/phase-0/stage-0.2-parity-harness.md` — 411 → 458 (+47)
- `docs/workflow-ui/phase-0/stage-0.3-ci-skeleton.md` — 369 → 416 (+47)
- `docs/workflow-ui/phase-1/stage-1.1-server-host.md` — 377 → 424 (+47)
- `docs/workflow-ui/phase-1/stage-1.2-contract-generation.md` — 422 → 469 (+47)
- `docs/workflow-ui/phase-1/stage-1.3-ci-wiring.md` — 405 → 452 (+47)
- `docs/workflow-ui/phase-2/stage-2.1-base-dto-discriminator.md` — 345 → 392 (+47)
- `docs/workflow-ui/phase-5/stage-5.2-fixture-corpus.md` — 603 → 650 (+47)
- `docs/workflow-ui/phase-5/stage-5.3-golden-file-harness.md` — 600 → 647 (+47)
- `docs/workflow-ui/phase-5/stage-5.1-dto-engine-mapper.md` — 1119 → 1138 (+19)

## Banned-identifier delta

| Identifier | Before (plan) | After (plan) | Before (stage docs) | After (stage docs) | Delta |
|---|---:|---:|---:|---:|---:|
| `NodePriority.Critical` | 1 | 1 | 4 | 4 | +0 |
| `YamlWorkflowLoader` | 1 | 1 | 8 | 8 | +0 |
| `ProgressUpdate` | 12 | 12 | 10 | 10 | +0 |
| `RuntimeType.PowerShellScript` | 0 | 0 | 1 | 1 | +0 |
| `Nodes/Policies/` | 1 | 1 | 2 | 2 | +0 |
| `react-jsonschema-form` | 2 | 2 | 3 | 3 | +0 |

## Cross-check results

- Phase effort sums still reconcile to **164 d**: `14, 16, 29, 17, 18, 10, 19, 22, 19`.
- Dependency audit found **0 orphan or later-stage `Depends on` references** after the pass.
- Step/stage/phase structure remains unchanged at **97 steps / 28 stages / 9 phases**.

## Highest-impact changes

1. Raised every stage file to the updated README workflow by adding Definition of Ready + symbol-discipline guidance before implementation begins.
2. Added stage-wide observability contracts with concrete Serilog fields, OTel span names, and metric names for every stage.
3. Added failure-mode tables to every stage so generators/evaluators can reason about detection + recovery rather than prose-only risks.
4. Added explicit p50/p95 budgets across infrastructure, schema, canvas, property-grid, mapper, telemetry, persistence, packaging, and docs work.
5. Added accessibility checklists to all UI-facing stages (canvas, property grid, monitor, auth/file UX, docs/release surfaces).
6. Added security callouts to every stage, with stronger path-traversal / XSS / token-redaction guidance in phases 2, 6, and 7.
7. Added rollback / feature-flag guidance to every stage so user-facing changes have a reversible rollout story.
8. Added manual verification focus lists to UI-heavy stages where automation alone is insufficient.
9. Fixed Stage 6.1 to cite `WorkflowExecutionContext.Events` / `.Progress` instead of a nonexistent `ExecutionEngine.Events.IEventSource`.
10. Filled all missing `Inputs` sections in stages 2.2, 2.3, and 2.4 so the 16-section step template is now complete everywhere.
