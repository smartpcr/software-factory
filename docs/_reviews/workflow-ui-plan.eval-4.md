# Re-evaluation — `docs/workflow-ui-plan.md` v1.3

## Overall score

**94 / 100**

### Rubric breakdown

| Rubric | Weight | Score |
|---|---:|---:|
| Structural completeness | 15 | 14 |
| Constraints compliance | 15 | 15 |
| Engine fidelity | 15 | 13 |
| Technical soundness | 15 | 14 |
| Testability | 10 | 10 |
| Risk & dependency management | 10 | 10 |
| Quality-gate mechanics | 10 | 10 |
| Clarity & ambiguity | 10 | 8 |
| **Total** | **100** | **94** |

## Constraint checks

- **Checkbox legend / roll-up rule:** **Holds.** Verified in §0.1.1 and headings across **9 phases**, **28 stages**, **97 steps**.
- **≤ 20 steps per stage:** **Holds.** Verified programmatically; max stage size is **7** steps.
- **≤ 20 files per step:** **Holds at plan level.** The inherited §0.1 file-budget rule is explicit, and the previously oversized fixture work is split into Steps **5.2.1a/b/c**.
- **Totals consistency:** **Holds.** Verified programmatically: **97 steps / 164 days**.

## Findings (new or previously-unresolved only)

### MEDIUM

1. **Location:** §2.8 Engine event surface; Step 6.1.1; Step 6.1.2  
   **Issue:** Event fidelity is mostly corrected, but the plan still overstates the progress-event shape. `NodeProgressEvent` in the engine exposes `ProgressPercent` and `Status`; the plan says it “carries a `ProgressUpdate` payload” and maps from `ProgressUpdate`. That is not the actual event contract.  
   **Recommended fix:** Reword these references to say the bridge maps `NodeProgressEvent.ProgressPercent` + `NodeProgressEvent.Status` into the telemetry DTO. Keep `ProgressUpdate` references only where the engine actually uses that type.

2. **Location:** Step 6.1.2  
   **Issue:** The plan excludes `Progress` from `EdgeActivated` because it is “non-terminal”, but `Progress` is still a real `MessageType` and a valid `NodeConnection.TriggerMessageType`. That leaves runtime edge-activation semantics under-specified for progress-triggered connections.  
   **Recommended fix:** Either include `Progress` in `EdgeActivated` derivation, or explicitly declare that progress-triggered edges are supported for authoring/round-trip but not visualized at runtime, with rationale.

### LOW

3. **Location:** Step 2.1.1; document header  
   **Issue:** Two minor internal-consistency defects remain:
   - Step 2.1.1 still says “9 base properties from `NodeDefinition.cs`”, while the authoritative base table now lists **11** properties.
   - The document header still says `**Status:** v1.2` although this is v1.3.
   **Recommended fix:** Update both text fragments to remove internal drift.

## Verdicts for eval-3 findings

| Eval-3 finding | Verdict | Citation / note |
|---|---|---|
| 1. Base policies omitted from base contract | **RESOLVED** | §0.2.2 now lists `RetryPolicy` and `CircuitBreakerPolicy` on `NodeDefinition`; §2.2 restores them on `NodeDtoBase`; §4.2 restores them to the Policies panel; Step 5.1.1a restores them in `MapBase`. This matches `src\ExecutionEngine\Nodes\Definitions\NodeDefinition.cs` and `src\ExecutionEngine\Policies\RetryPolicy.cs` / `CircuitBreakerPolicy.cs`. |
| 2. `NodeConnection` not modeled 1:1 | **RESOLVED** | §2.5 now matches all 9 engine properties (`SourceNodeId, TargetNodeId, TriggerMessageType, Condition, IsEnabled, Metadata, Priority, SourcePort, TargetPort`), and Steps 3.3.3 / 5.1.2 explicitly round-trip them. This matches `src\ExecutionEngine\Workflow\NodeConnection.cs`. |
| 3. Event / loader fidelity misnamed or fabricated | **PARTIAL** | Major fixes are in place: §2.8 now removes fabricated `WorkflowPausedEvent` / `MessageSentEvent`, includes `NodeProgressEvent`, and §0.2.3 / Step 2.4.4 now use `WorkflowLoader` / `WorkflowSerializer`. However, the plan still misdescribes `NodeProgressEvent` as carrying `ProgressUpdate`, and Step 6.1.2 leaves `Progress` edge activation under-specified. |

## Promotion decision

**PROMOTE**

Reason: score is **94 / 100**, with **zero CRITICAL** and **zero HIGH** findings.
