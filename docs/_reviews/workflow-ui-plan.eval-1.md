# Initial Evaluation — `docs/workflow-ui-plan.md`

## Overall score

**67 / 100**

### Rubric breakdown

| Rubric | Weight | Score |
|---|---:|---:|
| Structural completeness | 15 | 7 |
| Constraints compliance | 15 | 7 |
| Engine fidelity | 15 | 8 |
| Technical soundness | 15 | 14 |
| Testability | 10 | 7 |
| Risk & dependency management | 10 | 6 |
| Quality-gate mechanics | 10 | 10 |
| Clarity & ambiguity | 10 | 8 |
| **Total** | **100** | **67** |

## Summary

The plan is strong on architecture, quality-gate mechanics, and most of the React Flow / Zustand / SignalR / YAML decisions. However, it does **not** yet meet promotion quality because it has one engine-fidelity break, several structural/constraint violations, and a few places where the step template is not evaluator-ready.

## Findings

### CRITICAL

1. **Location:** §2.3 Data model, `NodeDtoBase.priority` (line 167); also impacts §0 engine-vocabulary claims and Step 0.2.1 parity work  
   **Issue:** The plan defines `priority: "Low" | "Normal" | "High" | "Critical"`, but the engine enum `NodePriority` only contains `Low | Normal | High`. This is a fabricated engine value and would break parity, codegen, and round-trip guarantees.  
   **Recommended fix:** Remove `Critical` everywhere in the plan and explicitly align all schema/codegen/drift-gate examples to the actual engine enum.

### HIGH

2. **Location:** Multiple steps across §§8–14, e.g. Steps 2.2.1, 3.1.1, 5.2.2, 6.3.1  
   **Issue:** Many later steps do not include the full required step template: explicit **Deliverables**, **Acceptance**, and **specific Tests** sections. §0 says every step is evaluator-scored and PR-sized, but much of the plan compresses steps into one-paragraph summaries plus rubric only.  
   **Recommended fix:** Normalize every step to the same explicit template used in Phase 0/1: Deliverables (with file list), Acceptance, Unit tests, Integration/contract tests where relevant, Risks, Rubric.

3. **Location:** Steps 2.2.1, 2.2.4, 2.3.3, 5.1.1  
   **Issue:** Several steps likely exceed the stated **≤ 20 files changed** constraint. Examples: bundled multi-kind steps (`Noop + CSharp`, `PowerShell + PowerShellTask`, `ForEach + While`) and the all-kinds mapper step (`WorkflowDto ↔ WorkflowDefinition`) are too large to remain PR-sized while still including schemas, mappers, stories, tests, and fixture coverage.  
   **Recommended fix:** Split these into smaller steps, ideally one runtime kind or one mapper slice per step, then recompute stage/phase totals.

4. **Location:** §2.5 Ports/handles matrix; §8 Phase 2 per-kind fan-out rules; §9 semantic edge work  
   **Issue:** The plan claims full engine fidelity, but the per-kind control-port model omits clear support for **`MessageType.Cancel`** and inconsistently treats **`Progress`**. The default accepted target set is `{Complete, Fail, Next}`, while engine `MessageType` also includes `Progress` and `Cancel`. As written, some engine-valid routed workflows could not be authored faithfully.  
   **Recommended fix:** Define explicit authoring semantics for `Cancel` and `Progress` routes across task/control/container kinds, update the matrix, edge components, schemas, and round-trip tests, or narrow the goals if those message types are intentionally deferred.

### MEDIUM

5. **Location:** §2.8 SignalR hub contract, `RunEvent.NodeCompleted`  
   **Issue:** `NodeCompleted` carries `status: "Completed" | "Skipped"`, but the engine event model exposes `NodeCompletedEvent` without a status payload and there is no `NodeSkippedEvent`. The plan does not explain how `Skipped` is derived in the live stream.  
   **Recommended fix:** Either remove `Skipped` from the live event contract or explicitly specify the derivation source (e.g. snapshot/state lookup after workflow completion).

6. **Location:** Execution plan structure across §§6–14  
   **Issue:** Phase dependencies are defined, but **step dependencies are not explicit**. The rubric asks for step-level dependency clarity, and several steps clearly depend on prior specific steps rather than only the whole prior phase.  
   **Recommended fix:** Add a `Depends on:` line to every step, especially where later steps require earlier schema, mapper, auth, or event-contract work.

7. **Location:** Execution plan structure across §§6–14  
   **Issue:** Risk treatment is strong at phase level and in Appendix A, but step-level risk notes are inconsistent for non-trivial steps (dynamic handles, container nesting, reconnect reconciliation, CSP/script gating, sidecar behavior).  
   **Recommended fix:** Add concise step-level risk notes anywhere implementation failure would materially affect fidelity, security, or delivery sequencing.

### LOW

8. **Location:** Step 4.2.1; cross-check with Step 0.2.1 and Step 4.2.2  
   **Issue:** The wording is inconsistent on how many shared node fields are covered. Step 4.2.1 says “All 9 fields rendered,” but the step text names 5 fields and the common schema list earlier names 8 shared policy/reference/config fields.  
   **Recommended fix:** Reconcile the field counts and explicitly partition which fields are delivered in 4.2.1 vs 4.2.2.

## Promotion decision

**REVISE**

Reason: score is below 90, and the plan has **1 CRITICAL** and **3 HIGH** findings.

## Required deltas before resubmission

1. **[CRITICAL]** Remove the fabricated `NodePriority.Critical` value and re-align every affected schema/type/example to engine reality.
2. **[HIGH]** Rewrite every step into a consistent evaluator-ready template with explicit Deliverables, Acceptance, and specific Tests sections.
3. **[HIGH]** Split oversized bundled steps so each one credibly fits the ≤ 20-file PR-size rule; then recompute all stage/phase effort summaries.
4. **[HIGH]** Specify full `MessageType` authoring fidelity, especially `Cancel` and `Progress`, across ports, connection validation, semantic edges, and fixture tests.
5. **[MEDIUM]** Clarify how `Skipped` is produced in the live `RunEvent` stream, or remove it from that contract.
6. **[MEDIUM]** Add explicit step-to-step dependencies throughout the phase plan.
7. **[MEDIUM]** Add step-level risk notes for the non-trivial/high-risk steps.
8. **[LOW]** Reconcile the shared-field counts/scope in the property-grid steps.
