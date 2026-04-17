# Re-evaluation — `docs/workflow-ui-plan.md` v1.1

## Overall score

**79 / 100**

### Rubric breakdown

| Rubric | Weight | Score |
|---|---:|---:|
| Structural completeness | 15 | 12 |
| Constraints compliance | 15 | 9 |
| Engine fidelity | 15 | 7 |
| Technical soundness | 15 | 14 |
| Testability | 10 | 9 |
| Risk & dependency management | 10 | 10 |
| Quality-gate mechanics | 10 | 10 |
| Clarity & ambiguity | 10 | 8 |
| **Total** | **100** | **79** |

## Findings (new or previously-unresolved only)

### CRITICAL

1. **Location:** §7 Phase 2 per-kind schemas and deliverables — especially Steps **2.2.3**, **2.2.4a**, **2.3.3a**, **2.4.2**, **2.4.3**; also mapper Steps **5.1.1b–e**  
   **Issue:** The plan still introduces several UI/config fields that do not match actual engine `NodeDefinition` contracts, violating the “no fabricated engine fields” requirement. Examples:
   - `CSharpTask` step uses `{ assembly, typeName, taskKind }`, but engine `CSharpTaskNodeDefinition` uses `ScriptContent` or `ExecutorTypeName` + `ExecutorAssemblyPath`.
   - `PowerShell` step allows inline script, but engine `PowerShellScriptNodeDefinition` requires `ScriptPath`.
   - `ForEach` step uses `{ itemsExpression, bodyNodeId }`, but engine `ForEachNodeDefinition` exposes `CollectionExpression` and `ItemVariableName`.
   - `Subflow` step uses `{ workflowId, parameters }`, but engine `SubflowNodeDefinition` exposes `WorkflowFilePath` / `WorkflowDefinition`, input/output mappings, timeout, and skip-validation.
   - `Timer` step uses `{ cron?, delayMs? }`, but engine `TimerNodeDefinition` exposes `Schedule`.
   **Recommended fix:** Rework all per-kind config shapes and mapper steps to mirror the actual engine properties exactly, then revalidate the plan against each `*NodeDefinition.cs` file.

### HIGH

2. **Location:** §5 Phase index and aggregated totals vs. the body of §§6–14  
   **Issue:** The document’s summary counts are inconsistent with the actual plan body. The phase index claims **84 steps / 170 days**, but the current document contains **96** `#### Step` headings, and the step-heading effort values sum to **164d**. This breaks the “aggregated totals consistent” rule.  
   **Recommended fix:** Recompute and correct the phase/stage/step totals from the actual step list, or adjust the body so the summary numbers are true.

### MEDIUM

3. **Location:** Whole document; especially §§0, 5–14  
   **Issue:** The rubric requires status checkboxes `[ ] / [~] / [x]` to be present and consistent on phases/stages/steps. v1.1 removed them entirely.  
   **Recommended fix:** Restore consistent checkbox status markers at phase, stage, and step level, and define how they roll up.

4. **Location:** §0.1 Step template; §3 Quality gate; multiple steps  
   **Issue:** The plan still does not explicitly satisfy two constraints from the rubric:  
   - each step should carry the **≤ 20 files** constraint, and  
   - each step should include a **100% new-code unit test coverage statement**.  
   v1.1 adds tests and global coverage gates, but not those per-step constraint statements.  
   **Recommended fix:** Add a short standard line to every step (or a clearly inherited global rule applied to every step) covering file-count cap and 100% new-code unit-test coverage.

## Verdicts for original eval-1 findings

| Eval-1 # | Verdict | Citation / note |
|---|---|---|
| 1 | **RESOLVED** | Changelog states removal of `Critical` (§Changelog); ground-truth gate lists `NodePriority` as `Low, Normal, High` only (§0.2); core DTOs use `type Priority = "Low" \| "Normal" \| "High"` (§2.3); Step 4.2.1 repeats the 3-value set. |
| 2 | **RESOLVED** | Step template is now standardized in §0.1, and the body uses `Deliverables / Acceptance / Unit tests / Integration tests / Depends on / Risks / Rubric` throughout. |
| 3 | **PARTIAL** | The named oversized steps were split (Changelog; Steps 2.2.1a/b, 2.2.4a/b, 2.3.3a/b, 5.1.1a–e), but the plan still does not restate the ≤20-file cap per step, and some steps (e.g. 5.2.1 fixture corpus) still read larger than a 20-file PR. |
| 4 | **RESOLVED** | Cancel/Progress are now explicit in the port matrix (§2.5), edge styling (§2.6), connection DTO/tests (Step 3.1.1), handle matrix rules (Step 3.1.2), and control-flow round-trip fixture work (Step 5.1.1d). |
| 5 | **RESOLVED** | `NodeCompleted` now has no status, and `NodeSkipped` is documented as a derived synthetic event in §2.8 and Step 6.1.2. |
| 6 | **RESOLVED** | `Depends on:` is part of the mandated step template in §0.1 and appears on every step in the body. |
| 7 | **RESOLVED** | `Risks:` is part of the mandated step template in §0.1 and appears across the step body, plus Appendix A consolidates residual risks. |
| 8 | **RESOLVED** | Field counts are reconciled in Steps 4.2.1 (5 fields), 4.2.2 (3 fields), and 4.2.3 (assertion test for 5 + 3 = 8). |

## Promotion decision

**REVISE**

Reason: score is below 90, and there is **1 CRITICAL** and **1 HIGH** open finding.
