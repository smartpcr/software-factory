# Stage 4.3 — Validation surface

**Phase:** Property Grid & Validation  
**Stage status:** [ ]  
**Total effort:** 4 d  
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context
Stage 4.3 makes validation visible to authors at both field and workflow scope. The plan calls for inline field errors, a server-backed `/validate` flow that uses the engine validator, and an error panel that can jump the camera to the faulting node (docs/workflow-ui-plan.md:808-898). This stage therefore bridges local zod issues and authoritative engine validation without duplicating server logic in the browser.
Field-level validation begins with the form schemas introduced in Stage 4.1, but workflow-level validation must ultimately come from `WorkflowValidator` through `WorkflowLoader.Validate` because only the engine knows about graph structure, duplicate ids, missing nodes, disabled connections, cycles, and entry-point rules (src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149, src/ExecutionEngine/Workflow/WorkflowValidator.cs:40-285). The UI should surface those results clearly without paraphrasing or mutating the engine error messages.
Deep-linking matters because validation is only useful if authors can navigate directly to the failing graph element. This stage therefore connects validation findings back into the canvas viewport helpers from Stage 3 so users can jump from an error row to the relevant node quickly and predictably.

## Prerequisites
- Phase gates (§0.2) green
- Prior stages completed
- External deps (React Flow v12, zustand, zundo, react-hook-form, zod, shadcn, ELKjs)

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 4.3 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `e2e/property-grid-inline-validation.spec.ts`
- `e2e/validation-panel.spec.ts`
- `e2e/workflow-validation.spec.ts`
- `src/host/Validation/WorkflowValidationEndpoint.cs`
- `src/host/Validation/WorkflowValidationMapper.cs`
- `src/host/Validation/WorkflowValidationResultDto.cs`
- `src/host/__tests__/Validation/WorkflowValidationEndpointTests.cs`
- `src/ui/__tests__/property-grid/FieldErrorBadge.test.tsx`
- `src/ui/__tests__/property-grid/mapZodIssues.test.ts`
- `src/ui/__tests__/validation/ValidationPanel.test.tsx`
- `src/ui/__tests__/validation/focusValidationTarget.test.ts`
- `src/ui/__tests__/validation/useWorkflowValidation.test.ts`
- `src/ui/app/layout/StatusBar.tsx`
- `src/ui/property-grid/FieldRenderer.tsx`
- `src/ui/property-grid/validation/FieldErrorBadge.tsx`
- `src/ui/property-grid/validation/mapZodIssues.ts`
- `src/ui/toolbar/SaveButton.tsx`
- `src/ui/validation/ValidationPanel.tsx`
- `src/ui/validation/focusValidationTarget.ts`
- `src/ui/validation/useWorkflowValidation.ts`

## Shared fixtures / fakes
- `src/ui/__tests__/fixtures/validation/invalid-fields.ts` — field-level invalid DTO values for zod issue mapping.
- `src/ui/__tests__/fixtures/validation/invalid-workflows.yaml` — workflows that trigger engine validator errors and warnings.
- `src/WorkflowUi.ContractTests/Fixtures/validation/*.yaml` — server-side validation corpus for WebApplicationFactory tests.
- `e2e/fixtures/validation/jump-to-node.ui.json` — viewport baseline for error-panel deep-link tests.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="4.3"`, `phaseId="4"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-4.3.plan-read`, `workflow-ui.stage-4.3.apply`, `workflow-ui.stage-4.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_4_3_started_total`, counter `workflow_ui_stage_4_3_failed_total`, histogram `workflow_ui_stage_4_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 4.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage43` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Tab through the full property grid, edit a required field, and confirm focus/error placement.
- Paste a multiline script/expression and verify validation state updates once per commit.
- Toggle between two nodes rapidly and confirm unsaved warnings / dirty state remain correct.

---

## Step 4.3.1 — Inline field error badges

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Map zod field issues into consistent inline error badges on the property grid so authors can correct invalid values without leaving the form. Errors must appear near the offending field, reuse shared shadcn styling, and update quickly as form values change. The mapping layer should preserve the original issue path and message so later workflow-level validation can reference the same field paths where appropriate. This is strictly field-local validation; it should not attempt graph or engine semantics. The implementation must also be performant enough to update within the 100 ms acceptance bar in the plan.

### 4. Depends on
- Step 4.1.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:808-898` requires inline error badges derived from zod issues within 100 ms.
- **Form framework** — Stage 4.1 already provides zod-backed field descriptors and form-state plumbing.
- **Node contracts** — Field paths still correspond to engine-authored properties from `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95` and the concrete node-definition files.

### 6. Deliverables
- **src/ui/property-grid/validation/**
  - `src/ui/property-grid/validation/mapZodIssues.ts` (new) — Exports `mapZodIssues`; converts zod issues into UI badge models with field paths.
  - `src/ui/property-grid/validation/FieldErrorBadge.tsx` (new) — Exports `FieldErrorBadge`; inline badge component for field-level errors.
- **src/ui/property-grid/**
  - `src/ui/property-grid/FieldRenderer.tsx` (modified) — Attaches badges and aria-describedby wiring to rendered fields.
- **src/ui/__tests__/property-grid/**
  - `src/ui/__tests__/property-grid/mapZodIssues.test.ts` (new) — Issue-path and message-mapping coverage.
  - `src/ui/__tests__/property-grid/FieldErrorBadge.test.tsx` (new) — Render and accessibility coverage for the inline badge component.
- **e2e/**
  - `e2e/property-grid-inline-validation.spec.ts` (new) — Playwright invalid-input coverage for inline errors.

### 7. Acceptance criteria
- [ ] Invalid field values show inline error badges next to the field.
  - Evidence: RTL and Playwright both assert the badge appears adjacent to the offending input.
- [ ] Error text comes directly from mapped zod issues.
  - Evidence: Mapping tests compare badge messages to source issue messages and paths.
- [ ] Error badges clear when the field becomes valid.
  - Evidence: Component tests change a field from invalid to valid and assert the badge disappears.
- [ ] Error badges include accessible associations with the input.
  - Evidence: Tests verify `aria-invalid` and `aria-describedby` wiring.
- [ ] Only field-local issues appear in this layer; workflow issues wait for the server validation step.
  - Evidence: The mapping helper ignores graph-level conditions not emitted by zod.
- [ ] The invalid-to-badge update path meets the 100 ms interaction bar.
  - Evidence: Browser tests measure or at least bound badge appearance timing under local interaction.

### 8. Unit tests
1. **mapZodIssues_preserves_field_paths**
   - Purpose: Keeps issue localization precise.
   - Harness: Vitest
   - Arrange: Provide multi-field zod issues from a fixture.
   - Act: Map the issues to badge models.
   - Assert: Each badge model retains the original field path.
2. **mapZodIssues_preserves_issue_messages**
   - Purpose: Avoids UI paraphrasing drift.
   - Harness: Vitest
   - Assertion focus: Mapped messages match source issue messages exactly.
3. **FieldErrorBadge_renders_accessible_error_text**
   - Purpose: Covers semantics, not just visuals.
   - Harness: Vitest + RTL
   - Assertion focus: The badge renders readable text with an accessible id.
4. **FieldRenderer_marks_invalid_inputs**
   - Purpose: Connects the mapper to the rendered controls.
   - Harness: Vitest + RTL
   - Assertion focus: Invalid fields receive `aria-invalid` and badge linkage.
5. **FieldRenderer_clears_badge_when_value_becomes_valid**
   - Purpose: Covers the recovery path.
   - Harness: Vitest + RTL
   - Assertion focus: Correcting the value removes the badge and invalid styling.
6. **mapZodIssues_handles_nested_field_paths**
   - Purpose: Supports policy sub-fields and configuration entries.
   - Harness: Vitest
   - Assertion focus: Nested paths map cleanly to UI badge models.
7. **Badge_mapping_ignores_non_field_errors**
   - Purpose: Separates field and workflow validation layers.
   - Harness: Vitest
   - Assertion focus: Issues without a usable field path do not create inline badges.
8. **Badge_updates_are_fast_enough_for_local_feedback**
   - Purpose: Checks the 100 ms target in a unit-friendly way.
   - Harness: Vitest fake timers
   - Assertion focus: The badge mapping and render commit within the expected threshold.

### 9. Integration tests
1. **Typing_invalid_value_shows_badge**
   - Purpose: Exercise the most common field-validation flow.
   - Harness: Playwright
   - Arrange: Open a node with a required field.
   - Act: Clear the field or type an invalid value.
   - Assert: An inline error badge appears promptly near the field.
2. **Correcting_value_clears_badge**
   - Purpose: Verify recovery UX in browser.
   - Harness: Playwright
   - Arrange: Start from a field already showing an error.
   - Act: Enter a valid value.
   - Assert: The badge disappears and save can proceed to the next validation stage.
3. **Nested_policy_field_error_maps_correctly**
   - Purpose: Ensure nested editors also receive inline badges.
   - Harness: Playwright
   - Arrange: Open a nested policy editor and enter an invalid value.
   - Act: Blur the field.
   - Assert: The inline error appears inside the nested editor at the right field.

### 10. Golden-file / parity tests
- **Issue-mapping snapshot** — Snapshot zod issue arrays to mapped badge models so field-path drift is visible in diffs.
- **No-save drift** — Inline validation states do not serialize into YAML or sidecar output.
- **Parity carry-forward** — Field-level badges decorate engine-backed field paths but do not change the underlying DTO contract.

### 11. Edge cases & negative tests
- Multiple invalid fields update at once; each receives its own badge rather than a shared generic message.
- A nested field path points into an array or map; the badge still anchors to the correct rendered control.
- An issue message contains server-like terminology; the UI preserves it rather than paraphrasing inconsistently.
- The user types quickly through valid/invalid states; badge rendering does not flicker excessively.
- A field unmounts because of a visibility rule while invalid; its badge disappears cleanly and reappears if the field returns.

### 12. Observability hooks
- **Zustand action** — Emit `property_grid.inline_error_shown` with `nodeId`, `fieldPath`, and `issueCode`.
- **Structured log field set** — Track `issueCount`, `firstInvalidFieldPath`, and `latencyMs` for local validation cycles.
- **UI trace** — Expose `data-error-field-path` on badges so Playwright can target them directly.
- **Performance mark** — Record `property_grid.inline_validation_ms` from input change to badge render.

### 13. Risks & mitigations
- **Risk:** Inline validation duplicates server messages poorly.
  - Mitigation: Map zod issues directly and avoid paraphrasing.
- **Risk:** Badge rendering becomes noisy for nested editors.
  - Mitigation: Anchor badges tightly to field paths and cover nested cases explicitly.
- **Risk:** Performance degrades as schemas grow.
  - Mitigation: Keep issue mapping pure and lightweight, then measure it in tests.

### 14. Evaluator gap-analysis prompts
1. How are zod issue paths translated into badge placement metadata?
2. What proves badge text is derived from zod rather than handcrafted strings?
3. How quickly do badges appear after invalid input, and where is that tested?
4. How does the implementation avoid mixing field-local issues with workflow-level validator output?
5. What happens when a hidden field is invalid because of a visibility rule?
6. Which browser test shows a nested policy field receiving an inline error badge?

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
- Invalid field badges appear and clear reliably in browser tests.
- Badge metadata preserves precise field paths for later diagnostics.


## Step 4.3.2 — Workflow-level validation via host `/validate` endpoint

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Add the server-backed workflow validation path that sends the current workflow DTO to the host and returns authoritative engine validation results. The host must call `WorkflowLoader.Validate` / `WorkflowValidator.Validate` rather than reimplementing graph rules, then normalize the response into a client-friendly error/warning DTO. The UI should disable Save while blocking errors are present and show warnings without preventing persistence. Because engine validation can fail by returning errors or throwing, the host wrapper must normalize both outcomes into a stable response contract. This step is the pivot from client-only form validation to engine-authoritative workflow validation.

### 4. Depends on
- Step 2.4.4

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:808-898` calls for `/api/workflows/{id}/validate` and save disabling while errors are present.
- **WorkflowLoader path** — `src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149` exposes `Validate(WorkflowDefinition workflow)` for host reuse.
- **WorkflowValidator rules** — `src/ExecutionEngine/Workflow/WorkflowValidator.cs:40-285` defines workflow-id, node-id, connection, and graph-structure checks that the UI must not duplicate.
- **Workflow graph contract** — `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` defines the DTO shape passed into server validation.

### 6. Deliverables
- **src/host/Validation/**
  - `src/host/Validation/WorkflowValidationEndpoint.cs` (new) — Exports the minimal-API route or handler for workflow validation requests.
  - `src/host/Validation/WorkflowValidationResultDto.cs` (new) — Exports the server response DTO for validation errors and warnings.
  - `src/host/Validation/WorkflowValidationMapper.cs` (new) — Maps engine validation output and exceptions into the host response contract.
- **src/ui/validation/**
  - `src/ui/validation/useWorkflowValidation.ts` (new) — Client hook for invoking the host validation endpoint and exposing save-enabled state.
- **src/ui/toolbar/**
  - `src/ui/toolbar/SaveButton.tsx` (modified) — Disables save when blocking validation errors are present.
- **src/ui/__tests__/validation/**
  - `src/ui/__tests__/validation/useWorkflowValidation.test.ts` (new) — Client-hook coverage for valid, invalid, and exception responses.
- **src/host/__tests__/Validation/**
  - `src/host/__tests__/Validation/WorkflowValidationEndpointTests.cs` (new) — WebApplicationFactory coverage for valid, invalid, and throwing engine-validation paths.
- **e2e/**
  - `e2e/workflow-validation.spec.ts` (new) — Playwright save-disable and validation-flow coverage.

### 7. Acceptance criteria
- [ ] The host exposes a validation endpoint that runs the engine validator.
  - Evidence: WebApplicationFactory tests hit the endpoint and observe engine-derived results.
- [ ] Validation errors disable Save in the UI.
  - Evidence: Playwright creates an invalid workflow and verifies the Save button disables.
- [ ] Warnings are surfaced but do not disable Save.
  - Evidence: Tests feed a warning-only workflow and confirm Save remains enabled.
- [ ] Thrown engine-validation exceptions are normalized into a stable error response.
  - Evidence: Server tests simulate or exercise throwing validation paths and assert predictable output.
- [ ] The UI does not duplicate graph-structure validation logic client-side.
  - Evidence: Code review shows the authoritative workflow validation call goes through the host hook.
- [ ] Validation request/response payloads preserve engine error text and node ids where available.
  - Evidence: Server and client tests assert the mapped response contains original messages and targets.

### 8. Unit tests
1. **useWorkflowValidation_handles_valid_response**
   - Purpose: Covers the green path on the client.
   - Harness: Vitest
   - Arrange: Mock a validation response with no errors.
   - Act: Run the hook against a sample workflow.
   - Assert: The hook reports `canSave = true` and exposes no blocking issues.
2. **useWorkflowValidation_handles_error_response**
   - Purpose: Covers blocking validation failures.
   - Harness: Vitest
   - Assertion focus: The hook reports `canSave = false` when errors are returned.
3. **useWorkflowValidation_handles_warning_only_response**
   - Purpose: Separates warnings from blocking errors.
   - Harness: Vitest
   - Assertion focus: Warnings appear but do not disable save.
4. **useWorkflowValidation_normalizes_exception_response**
   - Purpose: Covers server failure mapping on the client.
   - Harness: Vitest
   - Assertion focus: Thrown or 500-like responses become stable client error state.
5. **WorkflowValidationEndpoint_returns_engine_errors**
   - Purpose: Proves the server delegates to the engine validator.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Post an invalid workflow fixture to the endpoint.
   - Act: Read the response DTO.
   - Assert: The response contains engine-derived error text.
6. **WorkflowValidationEndpoint_returns_engine_warnings**
   - Purpose: Covers warning-only workflows.
   - Harness: xUnit + WebApplicationFactory
   - Assertion focus: Warnings are returned separately from errors.
7. **WorkflowValidationEndpoint_normalizes_throws**
   - Purpose: Covers validator throw behavior.
   - Harness: xUnit + WebApplicationFactory
   - Assertion focus: Thrown validation paths still return a stable error payload.
8. **SaveButton_reads_validation_state**
   - Purpose: Connects validation to save disabling.
   - Harness: Vitest + RTL
   - Assertion focus: The save button disables only when blocking errors are present.

### 9. Integration tests
1. **Invalid_workflow_disables_save**
   - Purpose: Exercise the core browser acceptance path.
   - Harness: Playwright
   - Arrange: Edit a workflow into an invalid state such as a missing required node field or bad connection.
   - Act: Trigger validation or wait for auto-validation.
   - Assert: Save disables and the validation summary shows the engine-reported error.
2. **Warning_only_workflow_keeps_save_enabled**
   - Purpose: Verify warnings do not block persistence.
   - Harness: Playwright
   - Arrange: Load a fixture that produces only warnings.
   - Act: Trigger validation.
   - Assert: Warnings are visible but the Save button remains enabled.
3. **Server_validation_exception_surfaces_cleanly**
   - Purpose: Cover the unhappy-path UX.
   - Harness: Playwright with fault injection
   - Arrange: Force the validation endpoint to return a normalized error response.
   - Act: Trigger validation from the UI.
   - Assert: The error banner/panel shows the normalized message without crashing the app.

### 10. Golden-file / parity tests
- **Validation corpus** — Server validation fixtures must include valid, warning-only, invalid, and throwing cases with stable expected responses.
- **Engine-message preservation** — Golden response snapshots prove host mapping preserves the underlying engine error text.
- **No duplicated rules** — There is no separate browser-only golden for graph validation logic; the server endpoint is the single source of truth.

### 11. Edge cases & negative tests
- The workflow is valid locally but invalid by engine graph rules such as duplicate node ids; the server response still blocks Save.
- Validation warnings arrive alongside errors; the UI shows both but Save remains blocked because of errors.
- The validation endpoint times out or returns a network error; Save state falls back to safe disabled-until-known or explicit retry messaging per implementation policy.
- A user edits rapidly while validation requests are in flight; stale responses are discarded in favor of the latest workflow hash.
- The engine throws before returning a result; the host still returns a stable response DTO rather than a raw stack trace.

### 12. Observability hooks
- **Structured log field set** — Emit `workflow.validation.requested` / `completed` with `workflowId`, `errorCount`, `warningCount`, and `durationMs`.
- **Host trace** — Log `validation_source=engine` so there is no ambiguity about where graph validation came from.
- **Client metric** — Expose `save_disabled_reason` with values like `blocking_validation_errors`, `validation_pending`, or `none`.
- **Correlation id** — Attach a validation request id to host and client logs so race conditions are diagnosable.

### 13. Risks & mitigations
- **Risk:** The UI reimplements parts of engine validation and drifts.
  - Mitigation: Keep browser logic to request orchestration and presentation only.
- **Risk:** Thrown validation errors produce inconsistent UX.
  - Mitigation: Normalize throws in the host mapper and test them with WebApplicationFactory.
- **Risk:** Rapid edits cause stale validation responses to overwrite current state.
  - Mitigation: Track request ids or workflow hashes and discard late responses.

### 14. Evaluator gap-analysis prompts
1. Which server file actually calls `WorkflowLoader.Validate` or `WorkflowValidator.Validate`?
2. How does the UI decide whether Save is enabled or disabled?
3. What distinguishes errors from warnings in the response contract?
4. How are thrown validation failures normalized for the client?
5. Which WebApplicationFactory test proves engine messages are preserved?
6. What race-condition mitigation exists for overlapping validation requests?

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
- The validation endpoint is covered by WebApplicationFactory tests for valid, invalid, warning, and throw paths.
- Playwright proves Save disables only when blocking errors are present.


## Step 4.3.3 — Validation error panel with jump-to-node deep links

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Add the validation summary panel that lists current workflow-level findings and lets users jump directly to the implicated node on the canvas. The panel should group errors and warnings, preserve server-provided message text, and integrate with the fit/focus helpers from Phase 3 so camera movement is consistent. Clicking an item should select the target node, center it in view, and focus the relevant surface when possible. Findings without a node target should still be visible but not claim a deep-link they cannot satisfy. This step closes the validation loop by turning diagnostics into navigable authoring actions.

### 4. Depends on
- Step 4.3.2

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:808-898` requires a validation panel with deep-linking to the faulting node.
- **Validation response contract** — Step 4.3.2 defines the client-visible error/warning DTO from the host endpoint.
- **Canvas navigation hooks** — Phase 3 already provides fit-view and node-focus helpers that the panel should reuse.
- **Workflow graph** — `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` is the source of valid node ids for deep-link targets.

### 6. Deliverables
- **src/ui/validation/**
  - `src/ui/validation/ValidationPanel.tsx` (new) — Exports `ValidationPanel`; lists errors and warnings with clickable rows.
  - `src/ui/validation/focusValidationTarget.ts` (new) — Exports `focusValidationTarget`; selects and centers the node referenced by a validation finding.
- **src/ui/app/layout/**
  - `src/ui/app/layout/StatusBar.tsx` (modified) — Hosts summary counts and opens the validation panel.
- **src/ui/__tests__/validation/**
  - `src/ui/__tests__/validation/ValidationPanel.test.tsx` (new) — Render, grouping, and deep-link wiring coverage.
  - `src/ui/__tests__/validation/focusValidationTarget.test.ts` (new) — Selection and viewport-centering logic coverage.
- **e2e/**
  - `e2e/validation-panel.spec.ts` (new) — Playwright jump-to-node coverage for validation findings.

### 7. Acceptance criteria
- [ ] The panel lists current errors and warnings separately.
  - Evidence: RTL tests assert section grouping and count summaries.
- [ ] Clicking a node-targeted finding selects and centers the relevant node.
  - Evidence: Unit and browser tests both verify selection plus viewport movement.
- [ ] Findings without a node target remain visible but are not falsely clickable.
  - Evidence: Panel tests cover global workflow errors with no node id.
- [ ] The panel preserves server-provided message text.
  - Evidence: Component tests compare rendered rows to the validation DTO payload.
- [ ] Jumping to a node reuses existing canvas navigation helpers.
  - Evidence: Code review and spy-based tests confirm `focusValidationTarget` delegates to shared viewport helpers.
- [ ] The panel stays in sync with the latest validation run.
  - Evidence: Client tests replace the validation result and assert the panel updates immediately.

### 8. Unit tests
1. **ValidationPanel_groups_errors_and_warnings**
   - Purpose: Covers the panel’s top-level organization.
   - Harness: Vitest + RTL
   - Arrange: Render the panel with mixed errors and warnings.
   - Act: Query group headers and rows.
   - Assert: Errors and warnings appear in separate sections with correct counts.
2. **ValidationPanel_preserves_server_messages**
   - Purpose: Avoids paraphrasing drift.
   - Harness: Vitest + RTL
   - Assertion focus: Rendered row text matches the incoming validation DTO messages exactly.
3. **ValidationPanel_marks_non_targeted_findings_non_clickable**
   - Purpose: Clarifies deep-link capability.
   - Harness: Vitest + RTL
   - Assertion focus: Rows without node ids do not expose click actions.
4. **focusValidationTarget_selects_node_and_centers_view**
   - Purpose: Implements the core deep-link behavior.
   - Harness: Vitest store test
   - Arrange: Provide a node-targeted finding and a graph containing that node.
   - Act: Invoke `focusValidationTarget`.
   - Assert: The node becomes selected and the viewport helper is called with its coordinates.
5. **focusValidationTarget_ignores_missing_node**
   - Purpose: Covers stale-validation edge cases.
   - Harness: Vitest store test
   - Assertion focus: Missing targets produce a safe no-op plus diagnostic event.
6. **ValidationPanel_updates_when_results_change**
   - Purpose: Keeps the panel reactive.
   - Harness: Vitest + RTL
   - Assertion focus: Replacing validation results rerenders rows without remount errors.
7. **StatusBar_shows_validation_summary_counts**
   - Purpose: Connects the panel to app chrome.
   - Harness: Vitest + RTL
   - Assertion focus: The status bar reflects current error/warning totals and opens the panel.
8. **Deep_link_reuses_shared_viewport_helper**
   - Purpose: Prevents duplicate camera logic.
   - Harness: Vitest spy test
   - Assertion focus: The deep-link helper delegates to the shared Phase 3 focus/fit API.

### 9. Integration tests
1. **Click_error_row_jumps_to_node**
   - Purpose: Exercise the main UX path end to end.
   - Harness: Playwright
   - Arrange: Load a workflow with a validation error tied to a known node.
   - Act: Open the validation panel and click the error row.
   - Assert: The canvas centers on the node and the node becomes selected.
2. **Global_error_row_is_non_clickable**
   - Purpose: Cover non-targeted findings.
   - Harness: Playwright
   - Arrange: Load a workflow with a global validation error.
   - Act: Open the panel and inspect the row.
   - Assert: The row is visible but does not trigger camera movement when clicked.
3. **Updated_validation_results_refresh_panel**
   - Purpose: Ensure the panel follows the latest validation run.
   - Harness: Playwright
   - Arrange: Open the panel on an invalid workflow, then fix the error and revalidate.
   - Act: Observe the panel after the second validation result arrives.
   - Assert: The old error disappears and summary counts update immediately.

### 10. Golden-file / parity tests
- **Validation-panel snapshot** — Snapshot rendered validation groups and row payloads so message/target drift is reviewable.
- **Jump-to-node viewport diff** — A sidecar viewport diff confirms deep-linking moves the camera without mutating YAML.
- **Reuse of navigation helpers** — No separate camera golden exists; tests prove the panel uses the shared navigation helper already covered elsewhere.

### 11. Edge cases & negative tests
- A validation result targets a node that has been deleted since the response was produced; the panel shows the row but the click becomes a safe no-op.
- Multiple findings target the same node; clicking each row still selects the correct node and preserves message context.
- The panel is open while validation results refresh; focus and scroll position remain usable.
- A node is inside a collapsed or off-screen container in a later phase; the deep-link helper can be extended, but the current step already centralizes the navigation call.
- Warnings and errors share similar text; grouping and counts still distinguish severity clearly.

### 12. Observability hooks
- **Zustand action** — Emit `validation_panel.row_clicked` with `severity`, `nodeId`, and `messageHash`.
- **Structured log field set** — Track `errorCount`, `warningCount`, `targetedCount`, and `untargetedCount` when the panel renders.
- **UI trace** — Expose `data-validation-row` and `data-validation-targeted` markers for Playwright.
- **Navigation trace** — Emit `validation_panel.jump_completed` with target coordinates when a deep-link succeeds.

### 13. Risks & mitigations
- **Risk:** Deep-link logic forks from existing canvas navigation helpers.
  - Mitigation: Delegate through one shared focus helper and spy on that helper in tests.
- **Risk:** Global workflow errors confuse users if shown as clickable.
  - Mitigation: Represent targeted and untargeted rows differently and test both branches.
- **Risk:** Validation panels lag behind the newest result.
  - Mitigation: Treat validation state as reactive data and cover updates while the panel is open.

### 14. Evaluator gap-analysis prompts
1. How does the panel distinguish targeted findings from global ones?
2. Which helper actually selects and centers the node when a row is clicked?
3. How do tests prove server-provided message text is preserved unchanged?
4. What happens when a validation finding references a missing node?
5. Which browser scenario shows a click jumping the camera to the failing node?
6. What telemetry would you inspect if users say clicking a validation row does nothing?

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
- Playwright proves clicking a targeted validation row jumps to the correct node.
- The panel refreshes live as validation results change.

## Appendix A — Validation responsibility split

| Layer | Source of truth | Examples | Save impact |
|---|---|---|---|
| Inline field validation | zod / form descriptors | empty required field, invalid numeric format | immediate local feedback |
| Workflow validation | engine host endpoint | duplicate node id, invalid connection, missing entry point | can disable Save |
| Validation panel deep-links | latest server validation result + canvas focus helpers | jump to bad node, list untargeted errors | navigation only |

## Appendix B — Failure triage rules

- If only inline badges fail, inspect `mapZodIssues.ts` and field descriptors first.
- If `/validate` responses drift, inspect the host validation mapper before changing UI text.
- If Save disables unexpectedly, check whether the response contains errors or warnings.
- If the validation panel shows stale rows, inspect request-id or workflow-hash race handling.
- If a row click does nothing, inspect `focusValidationTarget` plus node-target presence.
- If a finding has no node target, it must remain visible but not clickable.
- Client code should never paraphrase engine validation text.
- Network or engine exceptions should be normalized, not surfaced raw.
- Inline validation state must never serialize into YAML or sidecar output.
- Deep-linking should move only viewport/selection state, never authoring data.
- Warnings must not disable Save.
- Validation telemetry should include counts, duration, and correlation ids.

## Appendix C — Reviewer flow

1. Trigger an inline field error and watch the badge appear.
2. Correct the field and confirm the badge clears.
3. Submit an invalid workflow to the host validation endpoint and inspect the normalized response.
4. Confirm Save is disabled only when blocking errors are present.
5. Run a warning-only fixture and confirm Save remains enabled.
6. Open the validation panel and inspect grouping by severity.
7. Click a targeted row and confirm the node is selected and centered.
8. Click an untargeted/global row and confirm it does not claim a jump action.
9. Inspect logs for `workflow.validation.completed` and `validation_panel.row_clicked`.
10. Verify request ids or workflow hashes protect against stale validation responses.
11. Review browser tests for invalid, warning-only, and exception-validation flows.
12. Confirm no client-side graph rule implementation duplicates `WorkflowValidator` logic.

## Appendix D — Validation review heuristics

- Start with inline badge failures locally; start with host endpoint failures when save-state or graph-level issues change.
- If Save is disabled but no errors are visible, inspect the hook state and request-race handling.
- If warnings block Save, inspect client severity classification before changing server code.
- If engine text changes unexpectedly, confirm whether the engine validator changed before updating snapshots or UI copy.
- If jump-to-node fails, inspect whether the validation finding still references a live node id.
- If the validation panel shows stale rows, inspect correlation ids or workflow hashes.
- If network failures surface raw exceptions, inspect the host normalization mapper immediately.
- If inline and server errors duplicate confusingly, verify the layer split table in Appendix A.

## Appendix E — Minimal manual smoke set

- Trigger one inline field error and watch the badge appear.
- Correct the field and confirm the badge clears.
- Trigger one blocking workflow validation error and confirm Save disables.
- Trigger one warning-only validation result and confirm Save stays enabled.
- Open the validation panel and inspect grouped findings.
- Click a targeted finding and confirm jump-to-node behavior.
- Click an untargeted finding and confirm no jump action occurs.
- Review validation request, completion, and panel-click logs.

## Appendix F — One-line audit questions

- Are engine validation errors preserved verbatim?
- Do warnings remain non-blocking for Save?
- Does jump-to-node reuse the shared canvas focus helper?

## Appendix G — Three fast spot checks

- Confirm a zod field issue renders as an inline badge within the expected latency budget.
- Confirm a server validation error disables Save while a warning-only result does not.
- Confirm a targeted validation row selects and centers the correct node.
