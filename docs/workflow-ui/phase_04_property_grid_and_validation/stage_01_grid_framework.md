# Stage 4.1 — Grid framework

**Phase:** Property Grid & Validation  
**Stage status:** [ ]  
**Total effort:** 7 d  
**Step count:** 3

## Steps

- [ ] **Step 1**: Schema-driven form host with react-hook-form and zod
- [ ] **Step 2**: Dependent visibility rules
- [ ] **Step 3**: Monaco editors for expression and script fields

---
## Context
Phase 4 moves authoring detail from the canvas into a structured property surface. The master plan defines a schema-driven property grid with dependent visibility, code editors for script/expression fields, and workflow validation feedback (docs/workflow-ui-plan.md:808-898, docs/workflow-ui-plan.md:301-315). This stage creates the rendering framework that later panel-partition and validation steps plug into.
The property grid mirrors the engine contract rather than inventing a separate form model. Common fields come from `NodeDefinition` (src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95), policy shapes come from `RetryPolicy` and `CircuitBreakerPolicy` (src/ExecutionEngine/Policies/RetryPolicy.cs:25-170, src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:21-80), and kind-specific properties come from the concrete node definitions cited in the plan (src/ExecutionEngine/Nodes/Definitions/CSharpNodeDefinition.cs:13-59, src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:12-49, src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62, src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:12-51, src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:12-57, src/ExecutionEngine/Nodes/Definitions/IfElseNodeDefinition.cs:12-24, src/ExecutionEngine/Nodes/Definitions/SwitchNodeDefinition.cs:12-39, src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28, src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35, src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60, src/ExecutionEngine/Nodes/Definitions/TimerNodeDefinition.cs:12-24, src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:14-32).
The user-specified technology pins tighten the implementation choice beyond the master plan's older mention of `react-jsonschema-form`: this stage must use react-hook-form + zod + shadcn, with Monaco only for code-like fields. YAML loading and saving remain server-side through `WorkflowLoader` and `WorkflowSerializer` (src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149, src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212); the UI edits hydrated DTOs and sidecar state only.

## Prerequisites
- Phase gates (§0.2) green
- Prior stages completed
- External deps (React Flow v12, zustand, zundo, react-hook-form, zod, shadcn, ELKjs)

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `e2e/property-grid-editors.spec.ts`
- `e2e/property-grid-framework.spec.ts`
- `e2e/property-grid-visibility.spec.ts`
- `src/ui/__tests__/property-grid/MonacoField.test.tsx`
- `src/ui/__tests__/property-grid/PropertyGrid.test.tsx`
- `src/ui/__tests__/property-grid/PropertyGrid.visibility.test.tsx`
- `src/ui/__tests__/property-grid/editorLanguage.test.ts`
- `src/ui/__tests__/property-grid/schemaRegistry.test.ts`
- `src/ui/__tests__/property-grid/visibilityRules.test.ts`
- `src/ui/property-grid/FieldRenderer.tsx`
- `src/ui/property-grid/PropertyGrid.tsx`
- `src/ui/property-grid/editors/MonacoField.tsx`
- `src/ui/property-grid/editors/editorLanguage.ts`
- `src/ui/property-grid/fieldDescriptors.ts`
- `src/ui/property-grid/rules/csharpTaskRules.ts`
- `src/ui/property-grid/rules/subflowRules.ts`
- `src/ui/property-grid/rules/visibilityRules.ts`
- `src/ui/property-grid/schemaRegistry.ts`
- `src/ui/property-grid/useNodeForm.ts`

## Shared fixtures / fakes
- `src/ui/__tests__/fixtures/property-grid/all-kinds-form-data.ts` — one selected node fixture per runtime type.
- `src/ui/__tests__/fixtures/property-grid/visibility-rules.ts` — expected hidden/visible field sets for mutually-exclusive modes.
- `src/ui/__tests__/fixtures/property-grid/editor-fields.ts` — script, YAML, and expression field examples for Monaco mounting.
- `e2e/fixtures/property-grid/property-grid-roundtrip.yaml` + `.ui.json` — browser save/reload target.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="4.1"`, `phaseId="4"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-4.1.plan-read`, `workflow-ui.stage-4.1.apply`, `workflow-ui.stage-4.1.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_4_1_started_total`, counter `workflow_ui_stage_4_1_failed_total`, histogram `workflow_ui_stage_4_1_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 4.1 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage41` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Tab through the full property grid, edit a required field, and confirm focus/error placement.
- Paste a multiline script/expression and verify validation state updates once per commit.
- Toggle between two nodes rapidly and confirm unsaved warnings / dirty state remain correct.

---

### Step 1 — Schema-driven form host with react-hook-form and zod

### 1. Status
[ ]

### 2. Effort
3 d

### 3. Goal
Create the property-grid framework that renders a selected node into a structured editor using react-hook-form, zod, and shadcn field primitives. The framework should separate schema registration, form-state management, and field rendering so later panel partitions and kind-specific sections can plug in without duplicating form infrastructure. Each runtime type must render from the same selected-node contract and commit changes back through one update path. The host should also support read-only fields, nested field groups, and inline validation plumbing needed by later steps. This step is about framework, not final panel composition: it should render the right widgets for the current schema and selected node.

### 4. Depends on
- Step 2.4.6

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:808-898` requires a schema-driven form that renders all runtime kinds.
- **Field partition** — `docs/workflow-ui-plan.md:301-315` defines which base fields later live in general/policies/references panels.
- **Node base contract** — `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95` defines the common fields every form must understand.
- **Kind-specific contracts** — Use the concrete node-definition files cited in `docs/workflow-ui-plan.md:15-77` and summarized in `docs/workflow-ui-plan.md:97-277` for the runtime-specific field sets.

### 6. Deliverables
- **src/ui/property-grid/**
  - `src/ui/property-grid/PropertyGrid.tsx` (new) — Exports `PropertyGrid`; top-level selected-node editor host.
  - `src/ui/property-grid/schemaRegistry.ts` (new) — Exports `schemaRegistry` and `getSchemaForRuntimeType`; maps runtime types to zod-backed form descriptors.
  - `src/ui/property-grid/useNodeForm.ts` (new) — Exports `useNodeForm`; bridges selected-node data into react-hook-form state and commit callbacks.
  - `src/ui/property-grid/FieldRenderer.tsx` (new) — Exports `FieldRenderer`; renders shadcn inputs from schema metadata.
  - `src/ui/property-grid/fieldDescriptors.ts` (new) — Exports shared field descriptor helpers for text, textarea, select, switch, number, and key/value inputs.
- **src/ui/__tests__/property-grid/**
  - `src/ui/__tests__/property-grid/PropertyGrid.test.tsx` (new) — Per-kind render coverage and selection-change behavior tests.
  - `src/ui/__tests__/property-grid/schemaRegistry.test.ts` (new) — Schema registration and runtime-type coverage assertions.
- **e2e/**
  - `e2e/property-grid-framework.spec.ts` (new) — Playwright edit/save/reload smoke across representative kinds.

### 7. Acceptance criteria
- [ ] Selecting any of the thirteen runtime kinds renders a form instead of raw JSON.
  - Evidence: Vitest renders the host for every runtime type using fixture DTOs.
- [ ] The form host uses react-hook-form + zod rather than the deprecated RJSF path.
  - Evidence: Code review and tests confirm the host imports the pinned libraries and no RJSF package is referenced.
- [ ] Base and kind-specific fields are driven by schema descriptors, not hard-coded JSX branches in the host.
  - Evidence: Registry tests and file review show schema metadata determines rendering.
- [ ] Editing a field commits changes back through one selected-node update path.
  - Evidence: Unit and browser tests both change fields and assert the same store command fires.
- [ ] Read-only fields are supported without bespoke components.
  - Evidence: Tests cover `nodeId` and other future read-only fields via descriptor flags.
- [ ] Representative save/reload flows preserve edited values across the YAML boundary.
  - Evidence: Playwright edits form fields, saves, reloads, and checks the same values rehydrate.

### 8. Unit tests
1. **schemaRegistry_covers_every_runtime_type**
   - Purpose: Ensures no kind is missing from the form framework.
   - Harness: Vitest
   - Arrange: Import the runtime contract and registry keys.
   - Act: Compare the sets.
   - Assert: All runtime types have a registered schema descriptor.
2. **PropertyGrid_renders_form_for_each_runtime_type**
   - Purpose: Implements the master-plan “13 kinds render” bar.
   - Harness: Vitest + RTL data-driven
   - Assertion focus: Each fixture mounts without throwing and exposes at least one editable field.
3. **PropertyGrid_uses_read_only_descriptor_for_nodeId**
   - Purpose: Supports later partition rules.
   - Harness: Vitest + RTL
   - Assertion focus: The `nodeId` field is rendered as read-only metadata, not an editable text box.
4. **useNodeForm_commits_changes_through_single_update_callback**
   - Purpose: Keeps form plumbing centralized.
   - Harness: Vitest
   - Arrange: Mount the hook with a mock update command.
   - Act: Change a field value and submit.
   - Assert: The selected-node update callback fires once with the merged DTO.
5. **FieldRenderer_maps_descriptor_kinds_to_shadcn_controls**
   - Purpose: Verifies the widget layer.
   - Harness: Vitest + RTL
   - Assertion focus: Text, number, switch, select, and textarea descriptors all render the expected shadcn component wrappers.
6. **PropertyGrid_swaps_forms_when_selection_changes**
   - Purpose: Covers selected-node transitions.
   - Harness: Vitest + RTL
   - Assertion focus: Changing the selected runtime type tears down the old form and renders the new schema cleanly.
7. **PropertyGrid_ignores_execution_overlay_state**
   - Purpose: Preserves the history/persistence boundary.
   - Harness: Vitest store test
   - Assertion focus: Execution-only state does not appear in the form values or dirty-state tracking.
8. **No_RJSF_dependency_is_reintroduced**
   - Purpose: Protects the user’s pinned technology choice.
   - Harness: Vitest module inspection
   - Assertion focus: The form host dependency graph contains react-hook-form and zod, with no RJSF import path.

### 9. Integration tests
1. **Edit_representative_fields_and_reload**
   - Purpose: Verify the framework can edit and persist values through the host.
   - Harness: Playwright
   - Arrange: Open a workflow with representative task, control, and composition nodes.
   - Act: Edit one field on each node, save, and reload.
   - Assert: Each edited value rehydrates correctly in the property grid.
2. **Selection_changes_swap_forms_without_error**
   - Purpose: Exercise rapid node switching in the browser.
   - Harness: Playwright
   - Arrange: Select different runtime types in succession.
   - Act: Observe the property grid while switching.
   - Assert: The grid updates to the correct field set with no console errors.
3. **Read_only_field_stays_non_editable**
   - Purpose: Confirm descriptor-level read-only support.
   - Harness: Playwright
   - Arrange: Select a node and focus the `nodeId` field region.
   - Act: Attempt to type or paste into it.
   - Assert: The field remains non-editable while other editable fields still work.

### 10. Golden-file / parity tests
- **Schema parity carry-forward** — The DTO parity suite from `docs/workflow-ui-plan.md:15-77` remains the canonical check that rendered fields still mirror engine properties.
- **No-save leakage** — Golden diffs before and after form interaction show only edited DTO fields change; no framework metadata is serialized.
- **Registry snapshot** — A registry snapshot makes runtime-type-to-schema drift reviewable in code review.

### 11. Edge cases & negative tests
- No node is selected; the property grid shows an empty-state placeholder instead of stale form values.
- A selected node changes runtime type while the form is dirty; the host discards or confirms unsaved changes according to the shared authoring policy.
- A descriptor is missing for a runtime type; the grid surfaces a developer-facing error instead of rendering partial fields.
- A read-only field is accidentally marked editable in schema metadata; tests fail before it reaches the browser.
- The form host receives a node DTO with null policy fields; it preserves null instead of coercing to empty objects.

### 12. Observability hooks
- **Zustand action** — Emit `property_grid.form_loaded` with `nodeId`, `runtimeType`, and `fieldCount`.
- **Zustand action** — Emit `property_grid.field_committed` with `nodeId`, `fieldPath`, and `source=blur|submit`.
- **Structured log field set** — Track `schemaKey`, `dirtyFieldCount`, and `validationIssueCount` during form lifecycles.
- **UI trace** — Expose `data-field-path` attributes on rendered controls for Playwright and debug snapshots.

### 13. Risks & mitigations
- **Risk:** Schema and rendered controls drift apart.
  - Mitigation: Keep a registry-level test that enumerates all runtime types and descriptor kinds.
- **Risk:** The team reintroduces RJSF because it seems quicker.
  - Mitigation: Pin imports in the spec and add a module-inspection regression test.
- **Risk:** Selection changes leave stale form state mounted.
  - Mitigation: Use `useNodeForm` keyed by node id and runtime type, with explicit reset coverage.

### 14. Evaluator gap-analysis prompts
1. Where is the runtime-type-to-schema mapping defined, and is it exhaustive?
2. How do the tests prove the implementation uses react-hook-form + zod rather than RJSF?
3. Which path commits form changes back into graph state?
4. What evidence shows `nodeId` is read-only but still rendered?
5. How is form state reset when selection changes to another node kind?
6. Can the evaluator point to a Playwright edit/save/reload scenario for representative kinds?

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
- All thirteen runtime types render through the same form host.
- The framework is library-pinned and free of RJSF imports.

### Step 2 — Dependent visibility rules

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Add a rule engine that shows and hides form fields based on mutually-exclusive engine shapes without changing the underlying DTO. The initial required cases are `CSharpTask` script vs executor mode and `Subflow` file path vs inline workflow definition. Rules should live in per-kind configuration files so new behaviors can be added without bloating the generic form host. Hidden fields remain part of the DTO and round-trip untouched unless the user explicitly edits them. This gives the UI progressive disclosure while still honoring the full engine contract. The rule engine must be deterministic and testable without mounting the entire property grid.

### 4. Depends on
- Step 4.1.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:808-898` defines the dependent-visibility requirement for `CSharpTask` and `Subflow`.
- **CSharpTask shape** — `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62` shows the mutually-exclusive `ScriptContent` vs `ExecutorTypeName`/`ExecutorAssemblyPath` fields.
- **Subflow shape** — `src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60` shows the `WorkflowFilePath` vs `WorkflowDefinition` alternate authoring modes.
- **Form host** — Step 4.1.1 already supplies schema descriptors and field rendering hooks that rules can target.

### 6. Deliverables
- **src/ui/property-grid/rules/**
  - `src/ui/property-grid/rules/visibilityRules.ts` (new) — Exports `evaluateVisibilityRules` and common rule types.
  - `src/ui/property-grid/rules/csharpTaskRules.ts` (new) — Exports `csharpTaskVisibilityRules` for script vs executor fields.
  - `src/ui/property-grid/rules/subflowRules.ts` (new) — Exports `subflowVisibilityRules` for file vs inline workflow fields.
- **src/ui/property-grid/**
  - `src/ui/property-grid/useNodeForm.ts` (modified) — Applies per-kind visibility rules to derived field descriptors.
- **src/ui/__tests__/property-grid/**
  - `src/ui/__tests__/property-grid/visibilityRules.test.ts` (new) — Pure rule-engine coverage for each supported mode.
  - `src/ui/__tests__/property-grid/PropertyGrid.visibility.test.tsx` (new) — RTL coverage for hide/show behavior in the mounted grid.
- **e2e/**
  - `e2e/property-grid-visibility.spec.ts` (new) — Playwright toggle coverage for the required mode switches.

### 7. Acceptance criteria
- [ ] `CSharpTask` toggles between script fields and executor fields predictably.
  - Evidence: Pure rule tests cover both branches plus mixed/invalid input states.
- [ ] `Subflow` toggles between workflow file path and inline definition fields predictably.
  - Evidence: Rule tests and browser tests exercise both authoring modes.
- [ ] Visibility rules live outside the generic form host.
  - Evidence: Code review confirms per-kind rule modules supply the behavior.
- [ ] Hidden fields are preserved in the DTO unless the user explicitly changes them.
  - Evidence: Save/reload tests prove hidden values round-trip untouched.
- [ ] The rule engine is pure and deterministic.
  - Evidence: Unit tests call the rule evaluator without mounting React components.
- [ ] The property grid updates visible controls immediately when the governing field changes.
  - Evidence: Playwright toggles modes and sees the form update without reload.

### 8. Unit tests
1. **csharpTaskRules_show_script_fields_for_inline_mode**
   - Purpose: Covers the inline-script branch.
   - Harness: Vitest
   - Arrange: Build a CSharpTask DTO with script content populated.
   - Act: Evaluate visibility rules.
   - Assert: Script fields are visible and executor fields are hidden.
2. **csharpTaskRules_show_executor_fields_for_executor_mode**
   - Purpose: Covers the compiled-executor branch.
   - Harness: Vitest
   - Assertion focus: Executor assembly/type fields are visible while script content is hidden.
3. **csharpTaskRules_handle_partial_executor_values**
   - Purpose: Covers incomplete but still representable DTOs.
   - Harness: Vitest
   - Assertion focus: The evaluator still returns a deterministic field set for partially-filled executor data.
4. **subflowRules_show_file_path_mode**
   - Purpose: Covers file-path subflow authoring.
   - Harness: Vitest
   - Assertion focus: Workflow file path is visible while inline definition fields collapse.
5. **subflowRules_show_inline_definition_mode**
   - Purpose: Covers embedded-workflow authoring.
   - Harness: Vitest
   - Assertion focus: Inline definition controls appear when `WorkflowDefinition` is present.
6. **evaluateVisibilityRules_is_pure**
   - Purpose: Protects the rule-engine architecture.
   - Harness: Vitest
   - Assertion focus: The input descriptors and DTO are not mutated during evaluation.
7. **PropertyGrid_updates_visible_fields_when_mode_changes**
   - Purpose: Connects pure rules to rendered output.
   - Harness: Vitest + RTL
   - Assertion focus: Toggling the governing field changes visible controls immediately.
8. **Hidden_fields_roundtrip_unchanged**
   - Purpose: Protects non-lossy editing.
   - Harness: Vitest serialization test
   - Assertion focus: Hidden values survive save/load untouched when not edited.

### 9. Integration tests
1. **CSharpTask_mode_toggle**
   - Purpose: Exercise the inline/executor toggle in browser.
   - Harness: Playwright
   - Arrange: Select a CSharpTask node.
   - Act: Switch between script and executor modes.
   - Assert: Only the correct fields are visible for each mode.
2. **Subflow_mode_toggle**
   - Purpose: Exercise file vs inline subflow editing.
   - Harness: Playwright
   - Arrange: Select a Subflow node.
   - Act: Toggle between file path and inline workflow definition modes.
   - Assert: The expected controls appear and hidden values survive save/reload.
3. **Mode_switch_does_not_destroy_hidden_values**
   - Purpose: Verify non-lossy editing end to end.
   - Harness: Playwright
   - Arrange: Populate values in both branches before hiding one branch.
   - Act: Save and reload the workflow.
   - Assert: Previously hidden values are still present when their branch is shown again.

### 10. Golden-file / parity tests
- **Hidden-value round-trip** — Golden fixtures for CSharpTask and Subflow show that toggling UI visibility does not delete hidden engine fields.
- **Rule snapshot** — A small snapshot of visible field ids per mode makes drift easy to review.
- **Parity carry-forward** — The underlying DTO shape remains governed by `docs/workflow-ui-plan.md:15-77` and the cited node-definition files.

### 11. Edge cases & negative tests
- A `CSharpTask` DTO contains both script content and executor fields; the rule engine chooses a deterministic precedence but does not erase data.
- A `Subflow` DTO contains both workflow file path and inline definition; the UI exposes one branch at a time but preserves both until validation decides.
- The governing field is blank; the rule engine falls back to a documented default presentation.
- Mode switches happen while the form is dirty; visible-state recalculation does not lose unsaved changes in unaffected fields.
- A future add-a-kind rule file is missing; the host degrades gracefully to showing all fields rather than none.

### 12. Observability hooks
- **Zustand action** — Emit `property_grid.visibility_recomputed` with `nodeId`, `runtimeType`, and `visibleFieldCount`.
- **Structured log field set** — Track `ruleSet`, `governingFieldPath`, and `hiddenFieldIds` for debugging mode toggles.
- **UI trace** — Expose `data-visible-field` markers so Playwright can assert presence/absence cleanly.
- **Performance mark** — Record `property_grid.visibility.compute_ms` to catch regressions in large schemas.

### 13. Risks & mitigations
- **Risk:** Rule logic sprawls into component code.
  - Mitigation: Keep rules in pure per-kind modules and test them without React.
- **Risk:** Hidden fields are accidentally nulled out on submit.
  - Mitigation: Merge edits onto the existing DTO and explicitly regression-test hidden-field preservation.
- **Risk:** Ambiguous mixed-state DTOs produce flicker or unstable UIs.
  - Mitigation: Document deterministic precedence and cover mixed-state inputs in pure rule tests.

### 14. Evaluator gap-analysis prompts
1. Which modules own the CSharpTask and Subflow visibility rules, and are they pure?
2. How do tests prove hidden values are preserved rather than deleted?
3. What precedence does the UI use when mutually-exclusive fields are both populated?
4. How quickly does the mounted property grid respond when a governing field changes?
5. Which browser test demonstrates non-lossy save/reload across a mode switch?
6. What logs would help debug a report that executor fields still show in script mode?

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
- Rule-engine tests cover all supported modes and mixed-state negatives.
- Playwright confirms visible fields change instantly and hidden data survives reload.

### Step 3 — Monaco editors for expression and script fields

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Introduce Monaco-backed editors for script, expression, and inline YAML fields so complex authoring surfaces are better than plain textareas. The editor integration must be lazy-loaded to protect bundle size, keyed off schema metadata, and limited to the field types that actually benefit from code-aware editing. Initial language support should cover C#, PowerShell, and YAML, matching the engine-facing script fields and inline workflow content. The editor must still flow through the same form-state and validation infrastructure as ordinary fields. This step is about mounting and language selection, not semantic linting or compilation.

### 4. Depends on
- Step 4.1.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:808-898` requires Monaco editors with C#, PowerShell, and YAML highlighting.
- **Script-bearing engine fields** — Script and expression fields come from `src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:12-49`, `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62`, `src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:12-51`, `src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:12-57`, `src/ExecutionEngine/Nodes/Definitions/IfElseNodeDefinition.cs:12-24`, `src/ExecutionEngine/Nodes/Definitions/SwitchNodeDefinition.cs:12-39`, `src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28`, `src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35`, and `src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60`.
- **Form framework** — Step 4.1.1 already supplies field descriptors and change-commit plumbing that the editor widget should reuse.

### 6. Deliverables
- **src/ui/property-grid/editors/**
  - `src/ui/property-grid/editors/MonacoField.tsx` (new) — Exports `MonacoField`; lazy Monaco wrapper bound to react-hook-form.
  - `src/ui/property-grid/editors/editorLanguage.ts` (new) — Exports `getEditorLanguage`; maps schema field kinds to `csharp`, `powershell`, or `yaml`.
- **src/ui/property-grid/**
  - `src/ui/property-grid/fieldDescriptors.ts` (modified) — Adds editor-capable descriptor kinds for expressions, scripts, and inline workflow definitions.
  - `src/ui/property-grid/FieldRenderer.tsx` (modified) — Renders `MonacoField` only for editor-backed descriptors.
- **src/ui/__tests__/property-grid/**
  - `src/ui/__tests__/property-grid/MonacoField.test.tsx` (new) — Mount and lazy-load coverage for editor fields.
  - `src/ui/__tests__/property-grid/editorLanguage.test.ts` (new) — Language-mapping coverage for all editor-backed field kinds.
- **e2e/**
  - `e2e/property-grid-editors.spec.ts` (new) — Playwright typing and lazy-load coverage for Monaco-backed controls.

### 7. Acceptance criteria
- [ ] Script and expression fields mount Monaco rather than plain textareas.
  - Evidence: Component tests inspect the field renderer output for editor-backed descriptors.
- [ ] The editor lazy-loads and does not inflate the initial bundle path for non-editor forms.
  - Evidence: Unit and browser tests check deferred module loading.
- [ ] Language selection matches field semantics (C#, PowerShell, YAML).
  - Evidence: Language-mapping tests cover each editor-backed descriptor.
- [ ] Editor content still commits through the shared form update path.
  - Evidence: RTL tests type into the editor and assert the same update callback is invoked.
- [ ] Non-editor fields continue to use normal shadcn controls.
  - Evidence: Regression tests ensure text/number fields did not regress into editor widgets.
- [ ] Browser typing flows work for representative script and YAML fields.
  - Evidence: Playwright fills Monaco for CSharpTask script content and Subflow inline workflow definition.

### 8. Unit tests
1. **MonacoField_mounts_lazily**
   - Purpose: Protects bundle-splitting behavior.
   - Harness: Vitest + RTL
   - Arrange: Render a form without editor-backed fields.
   - Act: Inspect loaded modules.
   - Assert: Monaco is not requested until an editor field is rendered.
2. **MonacoField_binds_to_form_value**
   - Purpose: Ensures shared form plumbing still applies.
   - Harness: Vitest + RTL
   - Arrange: Render an editor-backed field with an initial value.
   - Act: Simulate content change through the editor wrapper.
   - Assert: The form state updates to the new content.
3. **editorLanguage_maps_csharp_fields**
   - Purpose: Covers C# language selection.
   - Harness: Vitest
   - Assertion focus: CSharpScript and CSharpTask script fields resolve to the C# language id.
4. **editorLanguage_maps_powershell_fields**
   - Purpose: Covers PowerShell language selection.
   - Harness: Vitest
   - Assertion focus: PowerShell and PowerShellTask script fields resolve to PowerShell.
5. **editorLanguage_maps_yaml_fields**
   - Purpose: Covers inline workflow editing.
   - Harness: Vitest
   - Assertion focus: Subflow inline workflow definition resolves to YAML.
6. **FieldRenderer_uses_standard_controls_for_non_editor_fields**
   - Purpose: Avoids overusing Monaco.
   - Harness: Vitest + RTL
   - Assertion focus: Plain text and number fields still render shadcn inputs.
7. **MonacoField_handles_unmount_cleanly**
   - Purpose: Prevents memory leaks on selection changes.
   - Harness: Vitest + RTL
   - Assertion focus: Unmounting the editor tears down subscriptions without console warnings.
8. **MonacoField_preserves_dirty_state**
   - Purpose: Keeps editor edits consistent with ordinary fields.
   - Harness: Vitest + RTL
   - Assertion focus: Typing into Monaco marks the field dirty and submit-ready.

### 9. Integration tests
1. **CSharp_script_field_uses_monaco**
   - Purpose: Check the C# editor path in browser.
   - Harness: Playwright
   - Arrange: Select a CSharpTask node with script mode active.
   - Act: Focus the script field and type a short expression.
   - Assert: The editor accepts input and the value persists after save/reload.
2. **PowerShell_script_field_uses_monaco**
   - Purpose: Check the PowerShell editor path.
   - Harness: Playwright
   - Arrange: Select a PowerShellTask node.
   - Act: Type a PowerShell snippet.
   - Assert: The editor mounts lazily and the value is committed.
3. **Subflow_inline_yaml_uses_monaco**
   - Purpose: Check the YAML editor path.
   - Harness: Playwright
   - Arrange: Select a Subflow node in inline mode.
   - Act: Type or paste inline workflow YAML.
   - Assert: The editor mounts, accepts the content, and rehydrates on reload.

### 10. Golden-file / parity tests
- **No-DOM serialization drift** — Golden diffs before and after editor-based edits show only the underlying DTO field values changing.
- **Language-map snapshot** — Snapshot the editor-language resolution table so later field additions are reviewable.
- **Parity carry-forward** — Editor widgets do not alter the DTO contract governed by `docs/workflow-ui-plan.md:15-77`.

### 11. Edge cases & negative tests
- A node switches away from an editor-backed kind while Monaco is still loading; the UI cancels cleanly.
- The editor module fails to load; the field falls back to a textarea with a warning rather than becoming unusable.
- A very long script value is loaded; the editor mounts without dropping the form value.
- A user tabs quickly through fields; lazy loading does not trap focus.
- The same node contains multiple editor-backed fields in the future; the lazy loader reuses the shared Monaco chunk.

### 12. Observability hooks
- **Structured log field set** — Emit `property_grid.editor_loaded` with `fieldPath`, `language`, and `loadMs`.
- **Zustand action** — Emit `property_grid.editor_changed` with `nodeId`, `fieldPath`, and `contentLength`.
- **UI trace** — Expose `data-editor-language` on the wrapper for Playwright and debug captures.
- **Fallback warning** — Emit `property_grid.editor_fallback_used` if Monaco fails and a textarea fallback is rendered.

### 13. Risks & mitigations
- **Risk:** Monaco bundle weight hurts startup time.
  - Mitigation: Lazy-load the editor and cover non-editor routes to prove Monaco stays deferred.
- **Risk:** Editor fields bypass shared form state.
  - Mitigation: Wrap Monaco in a controlled component that flows through `useNodeForm`.
- **Risk:** Language selection drifts from field semantics.
  - Mitigation: Centralize the mapping table and snapshot it in unit tests.

### 14. Evaluator gap-analysis prompts
1. Which descriptor kinds trigger Monaco, and where is the language mapping defined?
2. How do tests prove Monaco is lazy-loaded rather than part of the initial path?
3. What evidence shows editor content commits through the same form pipeline as normal inputs?
4. How does the browser suite cover C#, PowerShell, and YAML specifically?
5. What fallback behavior is specified if Monaco fails to load?
6. Which log fields would help debug a report that YAML opened with the wrong language mode?

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
- Representative C#, PowerShell, and YAML fields mount Monaco successfully in Playwright.
- Non-editor forms still avoid loading Monaco code paths.

## Appendix A — Editor-capable field inventory

- CSharpScript `scriptContent`
- CSharpTask `scriptContent`
- PowerShellTask `scriptContent`
- IfElse `condition`
- Switch `expression`
- ForEach `collectionExpression`
- While `conditionExpression`
- Subflow inline `workflowDefinition`
- Any future expression-like field should register through `editorLanguage.ts` rather than ad hoc widget code.

## Appendix B — Framework invariants

- All runtime kinds must resolve through `schemaRegistry`.
- Property-grid rendering must be driven by descriptors, not host-level `switch` statements.
- react-hook-form + zod are mandatory for this stage.
- Monaco is opt-in per descriptor and must lazy-load.
- Selection changes should reset form state by node id and runtime type.
- Read-only state is metadata-driven, not hand-coded per field.
- Hidden-field logic belongs to per-kind rule files, not the generic renderer.
- Editor widgets must reuse the same dirty-state and commit pipeline as standard inputs.
- Engine validation is not duplicated here; this stage stays at the form-framework layer.
- Non-editor fields must stay on lightweight shadcn controls.
- All user-visible field paths should be inspectable via `data-field-path` attributes.
- Browser suites should cover representative task, control, and composition nodes.

## Appendix C — Reviewer checklist

1. Confirm `PropertyGrid.tsx` does not import RJSF.
2. Verify `schemaRegistry.ts` contains all runtime kinds.
3. Spot-check read-only handling for `nodeId`.
4. Trace a field update through `useNodeForm` into the shared update callback.
5. Verify `visibilityRules.ts` is pure and tested without React.
6. Confirm Monaco loads only for script/expression/YAML fields.
7. Run the browser suite and confirm representative field edits persist on reload.
8. Inspect logs for `property_grid.form_loaded` and `property_grid.editor_loaded` if mount issues appear.
9. Ensure editor-language mapping covers C#, PowerShell, and YAML exactly.
10. Check that non-editor fields never pull Monaco into the initial path.
11. Verify hidden fields remain in the DTO when not explicitly edited.
12. Review `data-field-path` markers for Playwright stability.

## Appendix D — Framework drift signals

- **A runtime kind renders no form** — inspect `schemaRegistry` coverage first.
- **Fields remount with stale values** — inspect `useNodeForm` reset keys and selection change handling.
- **Monaco loads on a non-editor form** — inspect descriptor-to-editor mapping and lazy-load boundaries.
- **Visibility rules start living in React components** — push them back into pure per-kind rule files.
- **Read-only fields become editable** — inspect descriptor metadata and read-only rendering tests.
- **Property-grid saves execution state** — inspect the selected-node projection, not the field renderer.
- **Validation text appears before Stage 4.3 code runs** — inspect zod descriptors and local issue mapping only.
- **A browser edit persists but unit tests fail** — compare the form host update path used in tests and browser wiring.

## Appendix E — Minimal manual smoke set

- Select a task node and confirm the property grid renders.
- Select a control-flow node and confirm the field set changes.
- Select a composition node and confirm the field set changes.
- Toggle a visibility-rule branch and confirm fields update live.
- Open a Monaco-backed field and type content.
- Switch selection while the form is dirty and confirm reset behavior.
- Edit a representative field and verify save/reload persistence.
- Review form-load and editor-load logs for one node of each family.

## Appendix F — One-line audit questions

- Does every runtime kind resolve through `schemaRegistry`?
- Does any non-editor form accidentally load Monaco?
- Do visibility rules stay outside the generic form host?

## Appendix G — Three fast spot checks

- Confirm one task, one control-flow, and one composition node each mount a distinct schema-driven form.
- Confirm one visibility rule change updates the rendered field set without remount errors.
- Confirm one Monaco-backed field still commits through the shared form pipeline.
