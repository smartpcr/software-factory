# Re-evaluation — `docs/workflow-ui-plan.md` v1.4

## Overall score

**95 / 100**

### Rubric breakdown

| Rubric | Weight | Score |
|---|---:|---:|
| Structural completeness | 15 | 14 |
| Constraints compliance | 15 | 15 |
| Engine fidelity | 15 | 14 |
| Technical soundness | 15 | 13 |
| Testability | 10 | 10 |
| Risk & dependency management | 10 | 10 |
| Quality-gate mechanics | 10 | 10 |
| Clarity & ambiguity | 10 | 9 |
| **Total** | **100** | **95** |

## Per-finding verdicts (8-item cross-check list)

| # | Finding | Verdict | Citation / note |
|---|---|---|---|
| 1 | §2.2 base props count 9 → 11 (+ abstract RuntimeType) | **RESOLVED** | Changelog item (1); §2.2 now states `NodeDtoBase` matches the base plus discriminator; Step 2.1.1 now says “**11 base properties + abstract `RuntimeType` discriminator**”. This matches `src\ExecutionEngine\Nodes\Definitions\NodeDefinition.cs`. |
| 2 | §2.8 `NodeProgressEvent` payload corrected | **PARTIAL** | Changelog item (2) and §2.8 final bullet now correctly map `NodeInstanceId`, `ProgressPercent`, `Status`; Step 6.1.1 also uses the real shape. However, one earlier sentence in §2.8 still says `NodeProgressEvent` “carries a `ProgressUpdate` payload,” which is not the actual event contract. |
| 3 | §2.1 `WorkflowDefinition` namespace corrected | **RESOLVED** | §2.1 now cites `ExecutionEngine.Workflow.WorkflowDefinition`; matches `src\ExecutionEngine\Workflow\WorkflowDefinition.cs`. |
| 4 | §2.5 cross-ref `§5.1.4` → `§5.1.2` | **RESOLVED** | §2.5 now points to `§5.1.2`; Phase 5 Step 5.1.2 is the dedicated connection-mapper step. |
| 5 | §4.1 RJSF removed | **RESOLVED** | Step 4.1.1 now pins `react-hook-form + zod + shadcn` and explicitly says NOT `react-jsonschema-form`. No active architecture dependency remains on RJSF. |
| 6 | Policies folder path audit | **RESOLVED** | Changelog item (6), §0.2.2, and §2.2 all reference `src\ExecutionEngine\Policies\`; matches actual source files under `src\ExecutionEngine\Policies\`. |
| 7 | `PowerShellTask.ScriptPath` empty-string quirk pinned | **RESOLVED** | Changelog item (7) and Step 2.2.4b explicitly preserve `public string? ScriptPath { get; set; } = string.Empty;`; matches `src\ExecutionEngine\Nodes\Definitions\PowerShellTaskNodeDefinition.cs`. |
| 8 | Negative test: no `RuntimeType.PowerShellScript` | **RESOLVED** | Changelog item (8) and Step 2.2.4a explicitly assert no `PowerShellScript` literal in generated unions; matches `src\ExecutionEngine\Enums\RuntimeType.cs`, which has `PowerShell` and `PowerShellTask` only. |

## New or previously-unresolved findings

### MEDIUM

1. **Location:** §2.8 Engine event surface  
   **Issue:** One stale sentence still says `NodeProgressEvent` “carries a `ProgressUpdate` payload.” The actual event type exposes `NodeInstanceId`, `ProgressPercent`, and `Status` directly.  
   **Recommended fix:** Replace that sentence with the concrete property names already used later in §2.8 and in Step 6.1.1.

### LOW

2. **Location:** Step 6.1.2  
   **Issue:** The plan still excludes `Progress` from `EdgeActivated` because it is “non-terminal.” That is a defensible product choice, but it should be phrased as an explicit visualization decision rather than an engine-fidelity fact, since `Progress` is still a valid `MessageType` / `TriggerMessageType`.  
   **Recommended fix:** Clarify that progress-triggered connections are preserved for authoring and round-trip, but the runtime monitor intentionally does not animate them as `EdgeActivated`.

## Consistency confirmations

- **§4.2.3 count assertion vs §2.2 base-field enumeration:** **Consistent.**  
  - §2.2 / §0.2.2 enumerate **11 settable base fields** on `NodeDefinition`.  
  - §4.2.3 asserts **3 general + 5 policies + 3 references/configuration = 11 top-level fields**.  
  - These now align.

- **Checkbox legend:** **Holds.** Verified in §0.1.1 and in headings across **9 phases / 28 stages / 97 steps**.
- **≤ 20 steps per stage:** **Holds.** Verified programmatically; max stage size is **7**.
- **≤ 20 files per step:** **Holds at plan level.** Global inherited rule is explicit in §0.1, and oversized fixture work remains split into **5.2.1a/b/c**.
- **Totals consistency:** **Holds.** Verified programmatically: **97 steps / 164 days**.

## Promotion decision

**PROMOTE**

Reason: score is **95 / 100**, with **zero CRITICAL** and **zero HIGH** findings.
