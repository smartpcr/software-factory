# Stage 4.2 — Panel partition

**Phase:** Property Grid & Validation  
**Stage status:** [ ]  
**Total effort:** 7 d  
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context
Stage 4.2 freezes the top-level information architecture of the property grid. The master plan is explicit: the General panel has 3 top-level fields, the Policies panel has 5, the References & Configuration panel has 3, and nested policy editors contribute 7 retry sub-fields plus 4 circuit-breaker sub-fields without changing the 11 top-level total (docs/workflow-ui-plan.md:301-315, docs/workflow-ui-plan.md:808-898).
Those counts come straight from the engine contract. `NodeDefinition` contributes the shared fields (`NodeId`, `NodeName`, `Description`, `Configuration`, `JoinType`, `Priority`, `MaxConcurrentExecutions`, `RetryPolicy`, `CircuitBreakerPolicy`, `CompensationNodeId`, `FallbackNodeId`) (src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95); nested policy shapes come from `RetryPolicy` and `CircuitBreakerPolicy` (src/ExecutionEngine/Policies/RetryPolicy.cs:25-170, src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:21-80). This stage therefore acts as a guardrail against accidental field drift or UI category sprawl.
Because later validation and golden-file work depend on stable field counts, this stage adds explicit count assertions and panel-specific tests rather than relying on visual review alone. The property grid remains schema-driven under the hood, but the top-level panel structure is now a fixed contract and must be treated as such.

## Prerequisites
- Phase gates (§0.2) green
- Prior stages completed
- External deps (React Flow v12, zustand, zundo, react-hook-form, zod, shadcn, ELKjs)

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 4.2 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `e2e/property-grid-policies.spec.ts`
- `e2e/property-grid-references.spec.ts`
- `src/ui/__tests__/property-grid/CircuitBreakerPolicyEditor.test.tsx`
- `src/ui/__tests__/property-grid/NodeIdPicker.test.tsx`
- `src/ui/__tests__/property-grid/PoliciesPanel.test.tsx`
- `src/ui/__tests__/property-grid/ReferencesConfigurationPanel.test.tsx`
- `src/ui/__tests__/property-grid/RetryPolicyEditor.test.tsx`
- `src/ui/__tests__/property-grid/partitionCounts.test.ts`
- `src/ui/property-grid/fields/ConfigurationField.tsx`
- `src/ui/property-grid/panels/CircuitBreakerPolicyEditor.tsx`
- `src/ui/property-grid/panels/PoliciesPanel.tsx`
- `src/ui/property-grid/panels/ReferencesConfigurationPanel.tsx`
- `src/ui/property-grid/panels/RetryPolicyEditor.tsx`
- `src/ui/property-grid/panels/panelCountMetadata.ts`
- `src/ui/property-grid/panels/policyFieldCounts.ts`
- `src/ui/property-grid/pickers/NodeIdPicker.tsx`
- `src/ui/property-grid/schemaRegistry.ts`

## Shared fixtures / fakes
- `src/ui/__tests__/fixtures/property-grid/panel-counts.ts` — expected top-level and nested field counts per panel.
- `src/ui/__tests__/fixtures/property-grid/policies-populated.ts` — nodes with retry and circuit-breaker policies populated and null.
- `src/ui/__tests__/fixtures/property-grid/reference-options.ts` — valid and invalid node-id lists for compensation/fallback pickers.
- `e2e/fixtures/property-grid/policy-roundtrip.yaml` + `.ui.json` — save/reload target for nested-policy editing.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="4.2"`, `phaseId="4"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-4.2.plan-read`, `workflow-ui.stage-4.2.apply`, `workflow-ui.stage-4.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_4_2_started_total`, counter `workflow_ui_stage_4_2_failed_total`, histogram `workflow_ui_stage_4_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| property grid | form state overwrites engine defaults | form regression test or YAML diff fails | derive initial values from DTO + preserve null/absent explicitly |
| validation surface | error badges lag behind edits | RTL validation timing tests fail | debounce once, then invalidate synchronously on commit |
| editor widgets | focus trap / keyboard path breaks | accessibility test or manual keyboard pass fails | restore roving tabindex + escape handling |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| property-grid focus / tab advance | 16 ms | 50 ms |
| property edit -> state commit | 40 ms | 120 ms |
| validation refresh banner | 60 ms | 200 ms |

## Accessibility checklist
- [ ] Every form control has a programmatic label, description, and error message association.
- [ ] Section expanders, tabs, and accordions support arrow keys / Home / End as appropriate.
- [ ] Validation summaries move focus predictably and do not rely on color alone.
- [ ] Monaco/code fields expose an accessible fallback description and announce validation status.
- [ ] Modal editors/drawers restore focus to the invoking field after close.

## Security callouts
- Render script/expression fields as text, not HTML; sanitize preview panes and Monaco decorations.
- Preserve auth/session tokens outside form state snapshots so undo/redo cannot expose them.
- Gate any schema-driven component injection behind an explicit allow-list; no arbitrary component lookup by string.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 4.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage42` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Tab through the full property grid, edit a required field, and confirm focus/error placement.
- Paste a multiline script/expression and verify validation state updates once per commit.
- Toggle between two nodes rapidly and confirm unsaved warnings / dirty state remain correct.

---

## Step 4.2.1 — Policies panel with nested retry and circuit-breaker editors

### 1. Status
[ ]

### 2. Effort
4 d

### 3. Goal
Implement the Policies panel exactly as partitioned by the master plan: five top-level controls plus two nested editors that open on demand. The top-level controls are `joinType`, `priority`, `maxConcurrentExecutions`, `retryPolicy`, and `circuitBreakerPolicy`; the nested retry editor must expose all seven engine fields, and the nested circuit-breaker editor must expose all four of its engine fields. The panel also needs an explicit null/disabled affordance so authors can distinguish a disabled policy (`null` / absent key) from an empty-but-enabled object. This is not merely layout work: it is contract-preservation work because policy nullability changes serialized meaning. The UI must therefore keep top-level counts and nested counts testable and stable.

### 4. Depends on
- Step 4.1.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:808-898` and `docs/workflow-ui-plan.md:301-315` define the five top-level policy fields plus the nested 7/4 sub-field counts.
- **Node base fields** — `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95` supplies `JoinType`, `Priority`, `MaxConcurrentExecutions`, `RetryPolicy`, and `CircuitBreakerPolicy`.
- **Retry-policy contract** — `src/ExecutionEngine/Policies/RetryPolicy.cs:25-170` and `src/ExecutionEngine/Enums/RetryStrategy.cs:13-41` define the seven nested retry fields and enum values.
- **Circuit-breaker contract** — `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:21-80` defines the four nested circuit-breaker fields and defaults.

### 6. Deliverables
- **src/ui/property-grid/panels/**
  - `src/ui/property-grid/panels/PoliciesPanel.tsx` (new) — Exports `PoliciesPanel`; top-level host for the five policy controls.
  - `src/ui/property-grid/panels/RetryPolicyEditor.tsx` (new) — Exports `RetryPolicyEditor`; nested editor for the seven retry sub-fields.
  - `src/ui/property-grid/panels/CircuitBreakerPolicyEditor.tsx` (new) — Exports `CircuitBreakerPolicyEditor`; nested editor for the four circuit-breaker sub-fields.
  - `src/ui/property-grid/panels/policyFieldCounts.ts` (new) — Exports field-count constants used by tests and evaluators.
- **src/ui/property-grid/**
  - `src/ui/property-grid/schemaRegistry.ts` (modified) — Routes policy-bearing fields into the new panel components.
- **src/ui/__tests__/property-grid/**
  - `src/ui/__tests__/property-grid/PoliciesPanel.test.tsx` (new) — Top-level field-count, toggle, and nested-editor coverage.
  - `src/ui/__tests__/property-grid/RetryPolicyEditor.test.tsx` (new) — Seven-field coverage and null/absent behavior tests.
  - `src/ui/__tests__/property-grid/CircuitBreakerPolicyEditor.test.tsx` (new) — Four-field coverage and null/absent behavior tests.
- **e2e/**
  - `e2e/property-grid-policies.spec.ts` (new) — Playwright editing and round-trip coverage for policy fields.

### 7. Acceptance criteria
- [ ] Every runtime kind shows a Policies panel with exactly five top-level controls.
  - Evidence: Field-count tests enumerate each runtime type and assert the same top-level policy control count.
- [ ] The RetryPolicy editor exposes exactly seven sub-fields.
  - Evidence: Nested-editor tests count and name every retry field including `RetryOn` and `DoNotRetryOn`.
- [ ] The CircuitBreakerPolicy editor exposes exactly four sub-fields.
  - Evidence: Nested-editor tests count and name every circuit-breaker field.
- [ ] Authors can explicitly disable a policy, preserving `null` ↔ absent-key semantics.
  - Evidence: Round-trip tests save a disabled policy and assert the corresponding YAML key is omitted.
- [ ] Top-level and nested editors are loaded on demand rather than all expanded by default.
  - Evidence: Component tests assert the nested sections mount only when opened.
- [ ] Editing populated nested policy fields survives save/reload without field loss.
  - Evidence: Playwright edits every sub-field, saves, reloads, and rechecks the values.

### 8. Unit tests
1. **PoliciesPanel_has_five_top_level_fields_for_each_runtime**
   - Purpose: Implements the explicit field-count invariant.
   - Harness: Vitest + RTL data-driven
   - Arrange: Render the panel for each runtime type.
   - Act: Count the visible top-level policy controls.
   - Assert: Exactly five controls appear every time.
2. **RetryPolicyEditor_renders_seven_fields**
   - Purpose: Locks retry-field cardinality.
   - Harness: Vitest + RTL
   - Assertion focus: All seven retry sub-fields render when the editor is opened.
3. **CircuitBreakerPolicyEditor_renders_four_fields**
   - Purpose: Locks circuit-breaker cardinality.
   - Harness: Vitest + RTL
   - Assertion focus: All four circuit-breaker sub-fields render when opened.
4. **PoliciesPanel_disabled_retry_maps_to_null**
   - Purpose: Protects null vs empty-object semantics.
   - Harness: Vitest serialization test
   - Assertion focus: Turning retry off yields `null`/omitted YAML rather than `{}`.
5. **PoliciesPanel_disabled_circuit_breaker_maps_to_null**
   - Purpose: Protects circuit-breaker null semantics.
   - Harness: Vitest serialization test
   - Assertion focus: Disabling circuit breaker omits the key during save.
6. **RetryPolicyEditor_supports_retry_strategy_enum_values**
   - Purpose: Covers `RetryStrategy` fidelity.
   - Harness: Vitest + RTL
   - Assertion focus: Only `None`, `Fixed`, `Exponential`, and `Linear` are available.
7. **RetryPolicyEditor_preserves_retryOn_and_doNotRetryOn_lists**
   - Purpose: Covers the string-list representation of type names.
   - Harness: Vitest + RTL
   - Assertion focus: Both lists can be edited independently and are preserved in form state.
8. **Nested_editors_mount_on_demand**
   - Purpose: Keeps the UI manageable and aligns with the plan.
   - Harness: Vitest + RTL
   - Assertion focus: Retry and circuit-breaker editors are absent until opened.

### 9. Integration tests
1. **Edit_all_top_level_policy_fields**
   - Purpose: Exercise the five visible policy controls.
   - Harness: Playwright
   - Arrange: Open the Policies panel for a node.
   - Act: Change joinType, priority, maxConcurrentExecutions, and enable both nested editors.
   - Assert: All changed values persist through save/reload.
2. **Edit_retry_policy_subfields**
   - Purpose: Cover the seven retry fields end to end.
   - Harness: Playwright
   - Arrange: Enable retry policy and open the nested editor.
   - Act: Edit every retry sub-field including list values.
   - Assert: Values survive save/reload and appear in canonical YAML.
3. **Disable_policy_removes_yaml_key**
   - Purpose: Verify null/absent semantics in browser.
   - Harness: Playwright
   - Arrange: Start from a populated policy.
   - Act: Toggle the policy to disabled, save, and inspect the serialized output.
   - Assert: The corresponding YAML key is absent, not an empty object.

### 10. Golden-file / parity tests
- **Null-vs-empty regression fixture** — Golden fixtures include both populated and disabled policy states so absent-key behavior stays visible in diffs.
- **Field-count snapshot** — A snapshot of top-level and nested field ids supports fast evaluator auditing.
- **Enum parity carry-forward** — Retry strategy values remain anchored to `src/ExecutionEngine/Enums/RetryStrategy.cs:13-41` and broader parity coverage in `docs/workflow-ui-plan.md:15-77`.

### 11. Edge cases & negative tests
- Retry policy is enabled but list fields are empty; the UI preserves empty arrays without converting to null.
- Circuit-breaker values are partially populated and then disabled; the UI removes the key but does not lose the values if the author re-enables before save.
- A user opens both nested editors and switches selected nodes; each panel resets cleanly to the next node’s values.
- RetryStrategy is unknown because of a stale generated contract; the UI logs and falls back to `None` while tests fail.
- Authors paste invalid time strings into delay fields; field-level validation blocks commit and surfaces inline errors later in Stage 4.3.

### 12. Observability hooks
- **Zustand action** — Emit `property_grid.policies.opened` with `nodeId`, `runtimeType`, and which nested editor was opened.
- **Zustand action** — Emit `property_grid.policy_toggled` with `policy=retry|circuitBreaker` and `enabled`.
- **Structured log field set** — Track `retryStrategy`, `retryOnCount`, `doNotRetryOnCount`, and `circuitFailureThreshold` when nested editors commit.
- **UI trace** — Expose `data-panel="policies"` and `data-policy-editor` markers for Playwright and diagnostics.

### 13. Risks & mitigations
- **Risk:** Null and empty-object semantics collapse together.
  - Mitigation: Keep explicit enable/disable toggles and regression-test absent YAML keys.
- **Risk:** Field counts drift silently during future edits.
  - Mitigation: Centralize count constants and snapshot named field ids.
- **Risk:** Nested editors overwhelm the host panel.
  - Mitigation: Mount them lazily behind clear toggles and reuse focused editor sections.

### 14. Evaluator gap-analysis prompts
1. Where are the five top-level policy fields defined, and how are they counted?
2. How do the tests prove retry has seven sub-fields and circuit breaker has four?
3. Which browser test demonstrates `null` becoming an absent YAML key rather than an empty object?
4. How are `RetryOn` and `DoNotRetryOn` represented in the UI and preserved on round-trip?
5. What observability hooks tell you whether a user enabled retry but never opened the nested editor?
6. Can the evaluator point to evidence that nested editors mount only on demand?

### 15. Scoring rubric (step-level, weights sum to 100)
- **Deliverable completeness — 25**
  - All promised files, exports, and wiring exist and match the stage contract.
- **Test coverage & assertion quality — 25**
  - Vitest/RTL, Playwright, and server tests exercise the green path plus the required negatives.
- **Contract/parity fidelity — 20**
  - Engine field names, enum values, defaults, and null/absent behavior match the cited engine files exactly.
- **Edge-case handling — 10**
  - The implementation rejects invalid authoring operations and preserves canonical round-trip behavior.
- **Observability — 10**
  - Structured logs, trace points, and state-change hooks make failures diagnosable without a debugger.
- **Clarity & traceability — 10**
  - Spec text links each behavior to plan steps, owned files, and test evidence so an evaluator can score it quickly.

### 16. Definition of done
- Evaluator score is at least 90 for the step.
- No CRITICAL or HIGH findings remain open.
- All acceptance criteria in this section are checked and backed by green tests.
- Parity, round-trip, and coverage gates inherited from the master plan remain green.
- Top-level and nested policy field-count assertions all pass.
- Null-vs-absent policy behavior is demonstrated in both unit and browser tests.


## Step 4.2.2 — References & Configuration panel

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Implement the second fixed partition panel containing `compensationNodeId`, `fallbackNodeId`, and `configuration`. The two node-id fields must use pickers backed by the live graph so authors can only choose existing node ids, while `configuration` remains a general-purpose key/value surface aligned with `NodeDefinition.Configuration`. This panel must stay at exactly three top-level controls across all runtime kinds. It also becomes the place where later validation points users to broken references. The panel should therefore separate live-picker concerns from free-form configuration editing cleanly.

### 4. Depends on
- Step 4.2.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:808-898` and `docs/workflow-ui-plan.md:301-315` define the three references/configuration fields.
- **Node base fields** — `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95` provides `CompensationNodeId`, `FallbackNodeId`, and `Configuration`.
- **Workflow graph** — `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` defines the live node-id source that the pickers must validate against.

### 6. Deliverables
- **src/ui/property-grid/panels/**
  - `src/ui/property-grid/panels/ReferencesConfigurationPanel.tsx` (new) — Exports `ReferencesConfigurationPanel`; hosts the three top-level controls.
- **src/ui/property-grid/pickers/**
  - `src/ui/property-grid/pickers/NodeIdPicker.tsx` (new) — Exports `NodeIdPicker`; searchable live-node picker used for compensation and fallback references.
- **src/ui/property-grid/fields/**
  - `src/ui/property-grid/fields/ConfigurationField.tsx` (new) — Exports `ConfigurationField`; key/value editor for `NodeDefinition.Configuration`.
- **src/ui/__tests__/property-grid/**
  - `src/ui/__tests__/property-grid/ReferencesConfigurationPanel.test.tsx` (new) — Field-count and picker behavior coverage.
  - `src/ui/__tests__/property-grid/NodeIdPicker.test.tsx` (new) — Valid/invalid node-id selection coverage.
- **e2e/**
  - `e2e/property-grid-references.spec.ts` (new) — Playwright selection and round-trip coverage for reference fields.

### 7. Acceptance criteria
- [ ] The panel always renders exactly three top-level controls.
  - Evidence: Field-count tests enumerate visible controls for representative runtime kinds.
- [ ] Compensation and fallback pickers offer only current node ids from the graph.
  - Evidence: Picker tests and Playwright both verify the source list matches current graph nodes.
- [ ] Entering or selecting an invalid node id is rejected inline.
  - Evidence: Unit tests and browser tests cover invalid manual entry and removed-node scenarios.
- [ ] `configuration` edits round-trip through the engine DTO untouched.
  - Evidence: Save/reload tests edit key/value pairs and compare the serialized configuration map.
- [ ] Changing graph nodes updates picker options live.
  - Evidence: Store-driven picker tests remove or add nodes and assert option lists refresh.
- [ ] Compensation/fallback selections survive reload and remain tied to actual node ids.
  - Evidence: Playwright saves selected ids, reloads, and confirms the same valid references appear.

### 8. Unit tests
1. **ReferencesConfigurationPanel_has_three_top_level_fields**
   - Purpose: Locks the panel-count invariant.
   - Harness: Vitest + RTL
   - Arrange: Render the panel for representative node kinds.
   - Act: Count visible top-level controls.
   - Assert: Exactly three controls are present.
2. **NodeIdPicker_lists_current_graph_nodes**
   - Purpose: Ensures live graph integration.
   - Harness: Vitest + RTL
   - Assertion focus: Available options match the current graph node ids and names.
3. **NodeIdPicker_rejects_invalid_manual_entry**
   - Purpose: Covers inline validation.
   - Harness: Vitest + RTL
   - Assertion focus: Unknown ids trigger an error and do not commit.
4. **NodeIdPicker_updates_when_graph_changes**
   - Purpose: Covers dynamic option refresh.
   - Harness: Vitest store test
   - Assertion focus: Adding or removing a node updates picker options immediately.
5. **ConfigurationField_preserves_mixed_scalar_values**
   - Purpose: Protects the free-form config bag.
   - Harness: Vitest + RTL
   - Assertion focus: String, number, and boolean config values round-trip through the editor.
6. **ConfigurationField_supports_empty_state**
   - Purpose: Avoids forcing authors to create config when none exists.
   - Harness: Vitest + RTL
   - Assertion focus: An empty configuration map renders a clean placeholder and can stay empty.
7. **Fallback_and_compensation_fields_commit_through_shared_form**
   - Purpose: Keeps panel behavior aligned with the form framework.
   - Harness: Vitest
   - Assertion focus: Picker changes invoke the same update path as other form fields.
8. **References_roundtrip_preserves_exact_ids**
   - Purpose: Protects node-reference fidelity.
   - Harness: Vitest serialization test
   - Assertion focus: Saved and reloaded DTOs keep the same compensation and fallback ids.

### 9. Integration tests
1. **Select_compensation_and_fallback_nodes**
   - Purpose: Exercise both reference pickers in browser.
   - Harness: Playwright
   - Arrange: Open a workflow with multiple selectable nodes.
   - Act: Choose compensation and fallback ids, save, and reload.
   - Assert: The selected ids persist and show as valid options after reload.
2. **Invalid_reference_is_rejected**
   - Purpose: Cover the negative path.
   - Harness: Playwright
   - Arrange: Attempt to type or paste an id that does not exist in the graph.
   - Act: Blur or submit the field.
   - Assert: The panel shows an error and does not save the invalid id.
3. **Configuration_map_roundtrips**
   - Purpose: Verify the free-form bag survives save/load.
   - Harness: Playwright
   - Arrange: Add several configuration entries with mixed scalar values.
   - Act: Save, reload, and reopen the panel.
   - Assert: All entries reappear with their original types and values.

### 10. Golden-file / parity tests
- **Reference-picker regression fixture** — A golden fixture with valid compensation and fallback ids makes reference drift reviewable.
- **Configuration-map diff** — Golden output shows that only `Configuration` changes when key/value pairs are edited.
- **Parity carry-forward** — The panel remains anchored to the three `NodeDefinition` fields cited in `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95` and broader parity checks in `docs/workflow-ui-plan.md:15-77`.

### 11. Edge cases & negative tests
- The selected compensation node is deleted from the graph; the field surfaces an invalid-reference state without crashing.
- The same node id is chosen for both compensation and fallback; the UI allows it unless later validation forbids it.
- Configuration contains nested objects from a legacy fixture; the v1 field shows them read-only or rejects them clearly rather than flattening incorrectly.
- Picker search returns no matches; the dropdown shows an empty state instead of stale options.
- A graph with many nodes still updates picker options without losing the current selection.

### 12. Observability hooks
- **Zustand action** — Emit `property_grid.reference_selected` with `field=compensation|fallback`, `nodeId`, and `selectedTargetId`.
- **Structured log field set** — Track `configurationEntryCount` and `invalidReference` on panel commits.
- **UI trace** — Expose `data-picker="node-id"` plus field-path markers for Playwright.
- **Validation precursor** — Emit `property_grid.reference_invalid` when a selected id no longer exists in the live graph.

### 13. Risks & mitigations
- **Risk:** Pickers go stale as the graph changes.
  - Mitigation: Bind options to store selectors and cover live-update behavior in tests.
- **Risk:** Configuration editing becomes a dumping ground for nested objects unsupported in v1.
  - Mitigation: Define v1 scalar support clearly and reject unsupported structures with explicit messaging.
- **Risk:** Field counts drift when helper text or hidden controls are added.
  - Mitigation: Count only top-level inputs and keep a dedicated panel-count test.

### 14. Evaluator gap-analysis prompts
1. Which file implements the three-field References & Configuration panel?
2. How do tests prove the node-id pickers read from the live graph rather than stale props?
3. What happens when a previously-selected reference node is removed from the workflow?
4. How is the free-form configuration bag represented and round-tripped?
5. Which browser test proves invalid node ids are rejected?
6. What observability event would help diagnose a report that picker options stopped updating?

### 15. Scoring rubric (step-level, weights sum to 100)
- **Deliverable completeness — 25**
  - All promised files, exports, and wiring exist and match the stage contract.
- **Test coverage & assertion quality — 25**
  - Vitest/RTL, Playwright, and server tests exercise the green path plus the required negatives.
- **Contract/parity fidelity — 20**
  - Engine field names, enum values, defaults, and null/absent behavior match the cited engine files exactly.
- **Edge-case handling — 10**
  - The implementation rejects invalid authoring operations and preserves canonical round-trip behavior.
- **Observability — 10**
  - Structured logs, trace points, and state-change hooks make failures diagnosable without a debugger.
- **Clarity & traceability — 10**
  - Spec text links each behavior to plan steps, owned files, and test evidence so an evaluator can score it quickly.

### 16. Definition of done
- Evaluator score is at least 90 for the step.
- No CRITICAL or HIGH findings remain open.
- All acceptance criteria in this section are checked and backed by green tests.
- Parity, round-trip, and coverage gates inherited from the master plan remain green.
- Field-count tests confirm exactly three top-level controls.
- Browser save/reload tests prove both node-id references and configuration values persist.


## Step 4.2.3 — Partition count assertions

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Add the invariant tests that lock the property-grid partitioning into place. These tests are intentionally brittle in the right way: they fail whenever top-level panel counts drift or nested policy sub-field counts change, because that drift would imply the UI no longer mirrors the agreed engine surface. The step does not add visible UI; it hardens the spec so future edits cannot quietly change the panel contract. The count suite should be easy for evaluators to read, data-driven, and separate from broader render snapshots. It becomes a low-noise guardrail for every future stage that touches the property grid.

### 4. Depends on
- Step 4.2.2

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:301-315` defines the 3 + 5 + 3 top-level partition and 7/4 nested-policy counts that must be asserted.
- **Node base fields** — `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95` supplies the 11 top-level fields the partition derives from.
- **Policy shapes** — `src/ExecutionEngine/Policies/RetryPolicy.cs:25-170` and `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:21-80` define the nested cardinality assertions.

### 6. Deliverables
- **src/ui/__tests__/property-grid/**
  - `src/ui/__tests__/property-grid/partitionCounts.test.ts` (new) — Data-driven invariant suite for general, policies, references/configuration, and nested policy counts.
- **src/ui/property-grid/panels/**
  - `src/ui/property-grid/panels/policyFieldCounts.ts` (modified) — Exports explicit constants consumed by the invariant tests.
  - `src/ui/property-grid/panels/panelCountMetadata.ts` (new) — Exports named count metadata for evaluator readability and future diagnostics.

### 7. Acceptance criteria
- [ ] The General panel count is asserted as 3.
  - Evidence: The invariant test names `nodeId`, `nodeName`, and `description` explicitly.
- [ ] The Policies panel top-level count is asserted as 5.
  - Evidence: The invariant test names the five top-level policy controls explicitly.
- [ ] The References & Configuration panel count is asserted as 3.
  - Evidence: The invariant test names `compensationNodeId`, `fallbackNodeId`, and `configuration` explicitly.
- [ ] The combined top-level total is asserted as 11.
  - Evidence: The suite derives and asserts the combined count rather than only individual panel counts.
- [ ] The RetryPolicy nested-editor count is asserted as 7.
  - Evidence: The suite enumerates all retry sub-fields by name.
- [ ] The CircuitBreakerPolicy nested-editor count is asserted as 4.
  - Evidence: The suite enumerates all circuit-breaker sub-fields by name.

### 8. Unit tests
1. **general_panel_count_is_three**
   - Purpose: Locks the general-panel invariant.
   - Harness: Vitest
   - Arrange: Read panel metadata for the general section.
   - Act: Count the declared top-level field ids.
   - Assert: Exactly three field ids are present and named correctly.
2. **policies_panel_count_is_five**
   - Purpose: Locks the top-level policies invariant.
   - Harness: Vitest
   - Assertion focus: Exactly five top-level field ids are declared for Policies.
3. **references_panel_count_is_three**
   - Purpose: Locks the references/config invariant.
   - Harness: Vitest
   - Assertion focus: Exactly three top-level field ids are declared for References & Configuration.
4. **combined_top_level_count_is_eleven**
   - Purpose: Catches shifts across panels.
   - Harness: Vitest
   - Assertion focus: The summed top-level count is 11 and fails if any section drifts.
5. **retry_policy_nested_count_is_seven**
   - Purpose: Locks retry cardinality.
   - Harness: Vitest
   - Assertion focus: All seven retry sub-fields are declared and named.
6. **circuit_breaker_nested_count_is_four**
   - Purpose: Locks circuit-breaker cardinality.
   - Harness: Vitest
   - Assertion focus: All four circuit-breaker sub-fields are declared and named.
7. **count_metadata_matches_rendered_panels**
   - Purpose: Connects metadata-level counts to actual rendered controls.
   - Harness: Vitest + RTL
   - Assertion focus: Rendered panel counts match the exported metadata for a representative node.
8. **count_suite_fails_on_intentional_drift_fixture**
   - Purpose: Proves the tests are meaningful, not tautologies.
   - Harness: Vitest
   - Assertion focus: A deliberately drifted metadata fixture fails the suite.

### 9. Integration tests
1. **Count_assertions_run_in_ci_only**
   - Purpose: Document that no browser surface is needed for this invariant step.
   - Harness: Vitest in CI
   - Arrange: Run the property-grid invariant suite.
   - Act: Inspect the result output.
   - Assert: All count assertions pass and no browser harness is required.
2. **Rendered_panel_sample_matches_metadata**
   - Purpose: Provide one UI-level cross-check even though the step is mainly unit-level.
   - Harness: Playwright smoke
   - Arrange: Open a representative node and count visible controls by panel.
   - Act: Compare the UI counts to exported metadata.
   - Assert: The browser sample aligns with the invariant constants.
3. **Nested_policy_counts_visible_when_expanded**
   - Purpose: Verify the invariant is observable in the UI as well.
   - Harness: Playwright
   - Arrange: Expand both nested policy editors.
   - Act: Count visible controls inside each editor.
   - Assert: Retry shows seven and circuit breaker shows four.

### 10. Golden-file / parity tests
- **Count-metadata snapshot** — Snapshot the exported count metadata so any contract drift is visible in code review.
- **Panel-id snapshot** — Snapshot the named field ids per section to make evaluator review even faster.
- **Parity carry-forward** — Count assertions are derived from engine-backed plan contracts in `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95`, `src/ExecutionEngine/Policies/RetryPolicy.cs:25-170`, `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:21-80`, and `docs/workflow-ui-plan.md:301-315`.

### 11. Edge cases & negative tests
- A helper text element is added to a panel; the count test still measures actual fields, not decorative markup.
- A field is moved between panels without changing the total; the named-id assertions still fail.
- A nested policy editor gains an extra button that is not a field; field-id based counting avoids false failures.
- Rendered UI hides a field due to permissions in the future; the invariant metadata still documents the contract and separate tests can account for permissions.
- An evaluator misreads the combined count; the suite spells out the component counts explicitly.

### 12. Observability hooks
- **Structured log field set** — Emit `property_grid.partition_counts` with section names and counts during the invariant suite.
- **Test metadata** — Publish the field-id lists in test failure messages so drift is diagnosable without rerunning locally.
- **CI signal** — Tag failures with `property-grid-partition-drift` in test names for rapid triage.
- **UI trace** — Use consistent `data-panel` and `data-field-path` attributes so browser count checks stay stable.

### 13. Risks & mitigations
- **Risk:** Count tests become noisy and get ignored.
  - Mitigation: Keep them focused on invariant fields only and emit clear named-id diffs on failure.
- **Risk:** Future contributors change counts intentionally but forget the contract impact.
  - Mitigation: Make the failing tests point back to the partition section of the spec.
- **Risk:** Rendered UI counts and metadata counts drift apart.
  - Mitigation: Cross-check one representative rendered panel sample against the metadata.

### 14. Evaluator gap-analysis prompts
1. What are the exact named fields counted in each top-level panel?
2. How does the suite distinguish top-level policy fields from nested policy sub-fields?
3. Which test would fail if `configuration` were moved into the General panel?
4. How do failure messages make drift diagnosable without opening the UI?
5. Why is one browser sample still useful even though this step is mostly invariant tests?
6. Can the evaluator point to evidence that the combined top-level count is asserted as 11?

### 15. Scoring rubric (step-level, weights sum to 100)
- **Deliverable completeness — 25**
  - All promised files, exports, and wiring exist and match the stage contract.
- **Test coverage & assertion quality — 25**
  - Vitest/RTL, Playwright, and server tests exercise the green path plus the required negatives.
- **Contract/parity fidelity — 20**
  - Engine field names, enum values, defaults, and null/absent behavior match the cited engine files exactly.
- **Edge-case handling — 10**
  - The implementation rejects invalid authoring operations and preserves canonical round-trip behavior.
- **Observability — 10**
  - Structured logs, trace points, and state-change hooks make failures diagnosable without a debugger.
- **Clarity & traceability — 10**
  - Spec text links each behavior to plan steps, owned files, and test evidence so an evaluator can score it quickly.

### 16. Definition of done
- Evaluator score is at least 90 for the step.
- No CRITICAL or HIGH findings remain open.
- All acceptance criteria in this section are checked and backed by green tests.
- Parity, round-trip, and coverage gates inherited from the master plan remain green.
- Invariant counts fail loudly on intentional drift fixtures.
- The count metadata is easy to audit and referenced by later evaluators.

## Appendix A — Panel field ledger

### General (3)
- `nodeId`
- `nodeName`
- `description`

### Policies top-level (5)
- `joinType`
- `priority`
- `maxConcurrentExecutions`
- `retryPolicy`
- `circuitBreakerPolicy`

### References & Configuration (3)
- `compensationNodeId`
- `fallbackNodeId`
- `configuration`

### Nested RetryPolicy (7)
- `strategy`
- `maxAttempts`
- `initialDelay`
- `maxDelay`
- `multiplier`
- `retryOn`
- `doNotRetryOn`

### Nested CircuitBreakerPolicy (4)
- `failureThreshold`
- `minimumThroughput`
- `openDuration`
- `halfOpenSuccesses`

## Appendix B — Partition invariants

- The top-level total is always 11.
- Nested policy editors do not count toward the top-level total.
- Null policy state must be distinguishable from an empty object.
- Node-id pickers must always source ids from the live graph.
- `configuration` remains a free-form bag, but not a place for policy drift.
- Count tests should fail loudly if a field moves sections.
- Helper text or labels do not count as fields.
- Top-level counts are invariant across all runtime kinds.
- Nested counts are invariant regardless of runtime kind because policies live on `NodeDefinition`.
- Browser tests should verify counts on representative nodes, not just metadata.
- Future contributors must update both metadata and tests deliberately when contracts evolve.
- Evaluators should treat any count drift as a contract review event, not a cosmetic tweak.

## Appendix C — Fast review path

1. Check `PoliciesPanel.tsx` for exactly five top-level controls.
2. Expand RetryPolicy and count seven fields.
3. Expand CircuitBreakerPolicy and count four fields.
4. Disable a policy and confirm null/absent-key behavior is tested.
5. Check `ReferencesConfigurationPanel.tsx` for exactly three controls.
6. Inspect `NodeIdPicker.tsx` and verify it reads from live graph selectors.
7. Confirm `configuration` handles mixed scalar values.
8. Run count invariant tests and read the named-id output.
9. Run browser tests for policy edit, reference pickers, and nested counts.
10. Review logs for `property_grid.policy_toggled` and `property_grid.reference_selected` when debugging.
11. Confirm count metadata matches rendered UI for a representative node.
12. Treat any field movement between panels as a spec update requiring intentional review.

## Appendix D — Count drift interpretation

- A top-level count change is a contract event, not a cosmetic tweak.
- A nested policy count change must be reconciled with engine source before UI changes land.
- Moving a field between panels requires spec, metadata, and test updates together.
- Null/absent semantics deserve golden review whenever policy toggles change.
- Picker-option changes should be driven by graph-state changes, not panel metadata changes.
- Configuration-field expansion should not silently increase top-level counts.
- Count metadata should stay human-readable so evaluators can audit it quickly.
- Browser count checks are only spot checks; invariant tests remain the hard guardrail.

## Appendix E — Minimal manual smoke set

- Count the three General fields on a representative node.
- Count the five top-level Policies fields.
- Expand RetryPolicy and count seven sub-fields.
- Expand CircuitBreakerPolicy and count four sub-fields.
- Count the three References & Configuration fields.
- Disable and re-enable a policy to check null/absent behavior.
- Pick compensation/fallback node ids and save/reload.
- Review partition-count test output and panel logs.
