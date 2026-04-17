# Iteration 1 — Active Improvement Pass

**Date:** 2025-07-17
**Agent:** Copilot CLI (Opus-class)
**Master plan version:** v1.4 → v1.5-iter1
**Eval baseline:** eval-5 (scored 95/100, PROMOTE, 1 MEDIUM + 1 LOW open)

## Files touched

| # | File | Enhancement type | Lines before → after | Rationale |
|---|------|-----------------|---------------------|-----------|
| 1 | `docs/workflow-ui-plan.md` | Engine fidelity, changelog, risk register | 1366 → 1374 | Resolved eval-5 MEDIUM (§2.8 stale ProgressUpdate sentence) and LOW (Step 6.1.2 Progress/EdgeActivated wording); added risk entries R7, R8; bumped to v1.5-iter1 |
| 2 | `docs/workflow-ui/README.md` | Template improvement | 65 → 75 | Added "Definition of Ready" prerequisite checklist with banned-identifier guard |
| 3 | `docs/workflow-ui/phase-6/stage-6.3-live-monitor-ui.md` | Engine fidelity | 481 → 482 | Replaced stale `ProgressUpdate` reference with accurate `NodeProgressEvent` field names and source citation |

**Total files touched:** 3 (1 master plan + 1 README + 1 stage file)

## Top 10 highest-impact enhancements applied

| # | Impact | Enhancement | Rationale |
|---|--------|------------|-----------|
| 1 | HIGH | §2.8 stale "carries a ProgressUpdate payload" sentence removed and replaced with accurate engine-source-cited description | Directly resolves eval-5 MEDIUM finding; eliminates most likely source of telemetry DTO drift |
| 2 | HIGH | §2.8 added explicit disambiguation: `ProgressUpdate.cs` is a separate workflow-level aggregate snapshot, NOT the `NodeProgressEvent` payload | Prevents future recurrence of the conflation that caused 3 eval iterations of partial fixes |
| 3 | HIGH | Step 6.1.2 Progress/EdgeActivated wording clarified as visualization decision vs engine-fidelity constraint | Resolves eval-5 LOW finding; makes the product choice explicit and defensible |
| 4 | HIGH | Stage 6.3 Step 6.3.3 — removed stale `ProgressUpdate` reference in run-attach picker description | Last remaining stage-file instance of the conflated terminology |
| 5 | MEDIUM | Risk Register — added R7 (ProgressUpdate/NodeProgressEvent confusion) with mitigation | Ensures future contributors have a named risk to reference when touching telemetry code |
| 6 | MEDIUM | Risk Register — added R8 (MessageStatus.Completed vs Removed drift) | Documents an engine internal that could leak if exposed in UI DTOs |
| 7 | MEDIUM | README — added "Definition of Ready" checklist with 6 prerequisite checks | Makes the generator agent's entry conditions explicit; reduces false starts |
| 8 | MEDIUM | README — added banned-identifier guard in Definition of Ready | Eliminates the most common class of engine-fidelity regressions at the start of each step |
| 9 | LOW | Master plan changelog — comprehensive v1.5-iter1 entry documenting all 6 changes | Maintains audit trail for evaluators |
| 10 | LOW | Cross-file totals re-verified: 97 steps / 164 days / 28 stages / 9 phases | Confirms no structural drift from edits |

## Engine-source discrepancies found

| # | Finding | Status |
|---|---------|--------|
| 1 | `ProgressUpdate.cs` exists as a separate class in `Events/` but is not an event — it's a snapshot DTO | FIXED in plan (disambiguation added to §2.8 and R7) |
| 2 | `MessageStatus` enum has `Completed` and `Superseded` (not `Removed` or `Abandoned`) | FIXED in plan (R8 added) |
| 3 | `CheckpointFrequency` and `CircuitState` enums exist but are not listed in §0.2.1 parity tests | NOT FIXED — these are internal engine concerns not surfaced in UI; excluding them is intentional. Flag for iteration 2 if UI ever needs checkpoint config. |
| 4 | `NodeDefinition` has no `ExecutionOrder` or `Tags` property despite some stage file test sketches mentioning sorting | NOT APPLICABLE — test sketches refer to UI-side sorting, not engine fields |

## Banned-identifier verification (post-edit)

| Identifier | Hits in master plan | Hits in stage files | Status |
|------------|-------------------|-------------------|--------|
| `NodePriority.Critical` | 0 (only in changelog history) | 0 (only in parity test defense) | ✅ CLEAN |
| `YamlWorkflowLoader` | 0 (only in changelog history) | 0 (only in negative-test assertions) | ✅ CLEAN |
| `ProgressUpdate` as event payload | 0 (now disambiguated) | 0 (only in disambiguation context) | ✅ CLEAN |
| `PowerShellScript` as RuntimeType | 0 (only in negative-test assertions) | 0 (only in negative-test assertions) | ✅ CLEAN |
| `react-jsonschema-form` as active dep | 0 (only in "NOT" context) | 0 (only in "NOT" context) | ✅ CLEAN |
| `Nodes/Policies/` as path | 0 | 0 | ✅ CLEAN |

## Verified totals

- **Phases:** 9
- **Stages:** 28
- **Steps:** 97
- **Days:** 164
- **Files touched this iteration:** 3
- **Banned-identifier hits remaining:** 0
