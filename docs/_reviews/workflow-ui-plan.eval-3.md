# Re-evaluation — `docs/workflow-ui-plan.md` v1.2

## Overall score

**88 / 100**

### Rubric breakdown

| Rubric | Weight | Score |
|---|---:|---:|
| Structural completeness | 15 | 14 |
| Constraints compliance | 15 | 13 |
| Engine fidelity | 15 | 9 |
| Technical soundness | 15 | 13 |
| Testability | 10 | 10 |
| Risk & dependency management | 10 | 10 |
| Quality-gate mechanics | 10 | 10 |
| Clarity & ambiguity | 10 | 9 |
| **Total** | **100** | **88** |

## Findings (new or previously-unresolved only)

### CRITICAL

1. **Location:** §0.2.2 DTO-parity table; §2.2 Node DTO base; §4.2–§4.3 field partition; Phase 4 Step 4.2.1  
   **Issue:** The plan still misstates the **base** engine contract. `NodeDefinition.cs` in the repo **does** include `RetryPolicy` and `CircuitBreakerPolicy`, but v1.2 explicitly says the engine does not carry them and omits them from the authoritative base-property table and UI field partition. This means the “exact engine shapes” claim is still false at the base type level.  
   **Evidence:** `src\ExecutionEngine\Nodes\Definitions\NodeDefinition.cs` contains `RetryPolicy` and `CircuitBreakerPolicy`; plan says otherwise in §0.2.2 and excludes them from §4.2.  
   **Recommended fix:** Restore `RetryPolicy` and `CircuitBreakerPolicy` to the authoritative base parity table, base DTO contract, property-grid partition, and related count assertions/tests.

### HIGH

2. **Location:** §2.5 Connection DTO  
   **Issue:** The plan claims the connection DTO is “1:1 with `NodeConnection`”, but it is not. Actual engine `NodeConnection` includes `Condition`, `IsEnabled`, `Metadata`, `Priority`, `SourcePort`, and `TargetPort`; the plan omits four of those and renames the port fields to `sourcePortId` / `targetPortId`. This is still a lossy and non-parity contract.  
   **Evidence:** `src\ExecutionEngine\Workflow\NodeConnection.cs` vs. plan §2.5.  
   **Recommended fix:** Rewrite the connection DTO to match engine property names and full property set exactly, then align mapper/tests accordingly.

3. **Location:** §2.8 Engine event surface; Phase 6 Step 6.1.1; §0.2.3 and Step 2.4.4 API naming  
   **Issue:** The plan still fabricates or misnames engine-level runtime contracts:
   - marks `WorkflowPaused` and `MessageSent` as **native** engine events even though no such event classes exist in `src\ExecutionEngine\Events\`;
   - omits the actual native `NodeProgressEvent`;
   - references `YamlWorkflowLoader`, but the engine surface in-repo is `WorkflowLoader` / `WorkflowSerializer`.  
   This is still not engine-fidelity-clean.  
   **Recommended fix:** Replace the native event list with the actual event classes present in `src\ExecutionEngine\Events\`, include `NodeProgressEvent`, and rename loader/serializer references to the real in-repo types.

## Verdicts for eval-2 findings

| Eval-2 # | Verdict | Citation / note |
|---|---|---|
| 1 | **PARTIAL** | Per-kind property names are now largely corrected in §2.3 and Steps 2.2–2.4 / 5.1.1a–e (e.g. `AssemblyPath`, `ScriptPath`, `CollectionExpression`, `WorkflowFilePath`, `Schedule`). However, base `NodeDefinition` parity still fails because `RetryPolicy` / `CircuitBreakerPolicy` are wrongly removed, and connection/event parity is still off (§0.2.2, §2.5, §2.8). |
| 2 | **RESOLVED** | Verified: plan body now has **9 phases, 28 stages, 97 steps**, and step-header effort sums to **164 d**; §5 phase index matches the actual document counts. |
| 3 | **RESOLVED** | Checkboxes restored on headings and roll-up rule documented in §0.1.1; verified counts across phase/stage/step headers. |
| 4 | **RESOLVED** | The ≤20-file budget and 100% new-code coverage requirement are now hoisted as inherited global rules in §0.1 and referenced from §3. |

## Promotion decision

**REVISE**

Reason: score is below 90, and there is **1 CRITICAL** and **2 HIGH** open findings.
