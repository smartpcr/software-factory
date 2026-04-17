# Stage 3.2 — Palette & drag

**Phase:** Canvas, Palette, Connections  
**Stage status:** [ ]  
**Total effort:** 6 d  
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context
Stage 3.2 converts the static canvas shell into an authoring surface by letting users create nodes from the engine-authoritative runtime catalog. The master plan requires all thirteen runtime types to appear in palette groupings, drag onto the canvas with engine defaults, and survive save/reload without default drift (docs/workflow-ui-plan.md:705-804, src/ExecutionEngine/Enums/RuntimeType.cs:12-78). That means the UI must centralize defaults instead of sprinkling them across components.
The defaults come from engine definitions, not UI preference. Base values such as `JoinType.Any`, `NodePriority.Normal`, and `MaxConcurrentExecutions = 0` live on `NodeDefinition` (src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95), while per-kind defaults like `ForEach.ItemVariableName = "item"`, `While.MaxIterations = 1000`, `Subflow.Timeout = 00:05:00`, `Subflow.SkipValidation = false`, and `Container.ExecutionMode = Sequential` come from the specific node-definition classes (src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28, src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35, src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60, src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:14-32).
This stage also establishes keyboard creation and search behavior that later stages reuse for node references, validation deep-links, and add-a-kind work. The palette remains a pure client affordance; the saved workflow is still constrained to `WorkflowDefinition`, `NodeDefinition`, and `NodeConnection` as loaded and saved through `WorkflowLoader`/`WorkflowSerializer` (src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76, src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149, src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212).

## Prerequisites
- Phase gates (§0.2) green
- Prior stages completed
- External deps (React Flow v12, zustand, zundo, react-hook-form, zod, shadcn, ELKjs)

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 3.2 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `e2e/keyboard-node-creation.spec.ts`
- `e2e/palette-drag.spec.ts`
- `e2e/palette-search.spec.ts`
- `src/ui/__tests__/hooks/useCanvasShortcuts.test.tsx`
- `src/ui/__tests__/palette/KindPickerDialog.test.tsx`
- `src/ui/__tests__/palette/Palette.filter.test.tsx`
- `src/ui/__tests__/palette/Palette.test.tsx`
- `src/ui/__tests__/palette/createNodeFromKind.test.ts`
- `src/ui/__tests__/palette/filterPaletteEntries.test.ts`
- `src/ui/__tests__/state/creationCommands.test.ts`
- `src/ui/hooks/useCanvasShortcuts.ts`
- `src/ui/palette/KindPickerDialog.tsx`
- `src/ui/palette/Palette.tsx`
- `src/ui/palette/createNodeFromKind.ts`
- `src/ui/palette/dragPayload.ts`
- `src/ui/palette/filterPaletteEntries.ts`
- `src/ui/palette/paletteCatalog.ts`
- `src/ui/state/creationCommands.ts`

## Shared fixtures / fakes
- `src/ui/__tests__/fixtures/palette/palette-catalog.ts` — expected grouping metadata for all thirteen runtime types.
- `src/ui/__tests__/fixtures/workflows/all-kinds-roundtrip.yaml` + `.ui.json` — save/reload target for palette creation tests.
- `src/ui/__tests__/fixtures/keyboard/kind-picker-state.ts` — focused-canvas and unfocused-canvas hotkey setups.
- `e2e/fixtures/palette/drop-target-grid.json` — canonical drop points used by Playwright drag scenarios.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="3.2"`, `phaseId="3"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-3.2.plan-read`, `workflow-ui.stage-3.2.apply`, `workflow-ui.stage-3.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_3_2_started_total`, counter `workflow_ui_stage_3_2_failed_total`, histogram `workflow_ui_stage_3_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| canvas store | drag / selection races corrupt state | store history or React Flow tests fail | serialize updates through a single zustand action and replay reducer tests |
| palette / drag source | runtime type payload missing | drop integration test fails | validate drag payload before state mutation |
| connection visuals | edge routing or ports desync from matrix | canvas integration test or lint warning fails | recompute ports from matrix and invalidate stale edges |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| canvas drag frame | 16 ms | 50 ms |
| palette drop -> node visible | 60 ms | 150 ms |
| edge connect / reroute commit | 80 ms | 200 ms |

## Accessibility checklist
- [ ] Keyboard-only users can create, move, connect, and delete nodes without a pointer.
- [ ] Canvas affordances expose visible focus, ARIA labels/roles, and non-color-only state.
- [ ] Context menus, drawers, and flyouts keep focus trapped until dismissed and restore focus on close.
- [ ] Zoom/pan shortcuts do not trap assistive technology keys; provide alternate controls.
- [ ] Contrast for node states, handles, and connection badges meets WCAG AA.

## Security callouts
- Escape node labels/tooltips before rendering to prevent XSS from workflow-authored strings.
- Validate drag payloads before mutating zustand state to avoid crafted browser events inserting invalid runtime types.
- Do not leak hidden connection metadata into DOM attributes unless needed for testing.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 3.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage32` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Keyboard-create a node from the palette, move it, connect it, then undo/redo twice.
- Drag 25 nodes quickly and confirm selection/position state stays stable after a refresh.
- Open the canvas with reduced-motion enabled and confirm animations degrade gracefully.

---

## Step 3.2.1 — Palette catalog and drag-to-create

### 1. Status
[ ]

### 2. Effort
3 d

### 3. Goal
Build the palette catalog, grouping model, and drag factory that create authoring-safe nodes directly from runtime type descriptors. Dragging a palette entry onto the canvas must produce a schema-valid DTO with engine defaults already populated, a new stable node id, and a sidecar position captured from the drop location. The factory should be the single place where defaults are encoded so later keyboard creation, duplication, and fixture generation reuse the same path. Grouping must match the plan's Task / Control / Composition organization while still preserving the engine runtime names under the hood. Save/reload coverage is part of the step because default drift is the main failure mode this stage is meant to prevent.

### 4. Depends on
- Step 2.4.6

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` defines the grouping, drag-and-drop, and thirteen-kind round-trip requirement for Step 3.2.1.
- **Runtime catalog** — `src/ExecutionEngine/Enums/RuntimeType.cs:12-78` provides the full list of palette entries that must be present exactly once.
- **Base defaults** — `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95`, `src/ExecutionEngine/Enums/JoinType.cs:12-29`, and `src/ExecutionEngine/Enums/NodePriority.cs:14-33` define the common fields every created node starts with.
- **Per-kind defaults** — Task and control-flow defaults come from `src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:12-49`, `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62`, `src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:12-51`, `src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:12-57`, `src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28`, `src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35`, `src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60`, `src/ExecutionEngine/Nodes/Definitions/TimerNodeDefinition.cs:12-24`, and `src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:14-32`.
- **Serialization boundary** — `src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149` and `src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212` remain the only YAML authorities when the created nodes are round-tripped.

### 6. Deliverables
- **src/ui/palette/**
  - `src/ui/palette/paletteCatalog.ts` (new) — Exports `paletteCatalog`, `paletteGroups`, and runtime-type descriptors grouped as Task, Control, and Composition.
  - `src/ui/palette/Palette.tsx` (new) — Exports `Palette`; renders grouped entries, drag handles, and empty-state messaging.
  - `src/ui/palette/createNodeFromKind.ts` (new) — Exports `createNodeFromKind`; stamps engine defaults and returns a schema-valid node DTO.
  - `src/ui/palette/dragPayload.ts` (new) — Exports drag MIME constants and typed helpers for palette-to-canvas transfers.
- **src/ui/state/**
  - `src/ui/state/creationCommands.ts` (new) — Exports `createNodeAtPoint`; inserts the new node and sidecar position in one history-aware action.
- **src/ui/__tests__/palette/**
  - `src/ui/__tests__/palette/createNodeFromKind.test.ts` (new) — Data-driven default and schema assertions for all thirteen kinds.
  - `src/ui/__tests__/palette/Palette.test.tsx` (new) — RTL coverage for grouping order, entry labels, and drag payload setup.
- **src/ui/__tests__/state/**
  - `src/ui/__tests__/state/creationCommands.test.ts` (new) — Store tests for node insertion and sidecar position capture.
- **e2e/**
  - `e2e/palette-drag.spec.ts` (new) — Playwright drag-drop and save/reload coverage across all runtime types.

### 7. Acceptance criteria
- [ ] The palette shows thirteen entries grouped into Task, Control, and Composition sections.
  - Evidence: A catalog test and RTL render both enumerate the expected groups and exact entry counts.
- [ ] Dragging any palette entry creates a node whose DTO passes the existing schema/contract checks.
  - Evidence: The factory test runs every runtime type through the schema validator and parity helpers.
- [ ] Created nodes start with engine defaults rather than UI-invented values.
  - Evidence: Unit tests assert the defaults cited from the engine source for each runtime family.
- [ ] Drop actions persist node coordinates to the sidecar position map and nowhere else.
  - Evidence: Store tests confirm only `nodePositions` changes in `.ui.json`; YAML remains engine-pure.
- [ ] All thirteen created nodes survive save and reload with their default values intact.
  - Evidence: Playwright drops each kind, saves, reloads, and compares the rehydrated DTOs.
- [ ] Creation uses one shared factory path reusable by keyboard creation and duplication.
  - Evidence: Code review and unit coverage confirm every authoring entry point calls `createNodeFromKind`.

### 8. Unit tests
1. **paletteCatalog_lists_thirteen_runtime_entries**
   - Purpose: Protects against missing or duplicate palette entries.
   - Harness: Vitest
   - Arrange: Import the runtime enum fixture and palette catalog.
   - Act: Compare catalog runtime keys to the runtime contract.
   - Assert: Exactly thirteen runtime-backed entries exist with no extras.
2. **paletteCatalog_groups_entries_correctly**
   - Purpose: Locks Task / Control / Composition grouping.
   - Harness: Vitest
   - Assertion focus: Each runtime type appears in the expected group and group ordering stays stable.
3. **createNodeFromKind_applies_base_defaults**
   - Purpose: Checks `joinType`, `priority`, and `maxConcurrentExecutions` defaults.
   - Harness: Vitest
   - Arrange: Call the factory for representative kinds across all groups.
   - Act: Read the common node fields from the returned DTO.
   - Assert: Base values match the engine defaults exactly.
4. **createNodeFromKind_applies_foreach_and_while_defaults**
   - Purpose: Covers loop-specific default fields.
   - Harness: Vitest
   - Assertion focus: ForEach uses `item`, While uses `1000`, and neither omits required loop fields.
5. **createNodeFromKind_applies_subflow_and_container_defaults**
   - Purpose: Covers composition defaults.
   - Harness: Vitest
   - Assertion focus: Subflow timeout/skipValidation and container executionMode match engine defaults.
6. **createNodeFromKind_returns_schema_valid_nodes_for_all_kinds**
   - Purpose: Implements the master-plan unit-test requirement.
   - Harness: Vitest data-driven theory
   - Assertion focus: Every runtime type validates without manual post-processing.
7. **createNodeAtPoint_records_sidecar_position**
   - Purpose: Ensures drag creates both node and UI position state.
   - Harness: Vitest store test
   - Arrange: Dispatch a create command at a known canvas coordinate.
   - Act: Read graph and sidecar state after insertion.
   - Assert: The node exists once and the sidecar stores the expected x/y pair.
8. **Palette_drag_payload_uses_single_mime_type**
   - Purpose: Prevents browser drag interoperability issues.
   - Harness: Vitest + jsdom DataTransfer shim
   - Assertion focus: The drag helper always sets the approved workflow-node MIME payload.

### 9. Integration tests
1. **Drag_every_runtime_type_round_trips**
   - Purpose: Cover the explicit acceptance bar for all thirteen kinds.
   - Harness: Playwright
   - Arrange: Load an empty workflow and iterate through every palette entry.
   - Act: Drag each entry to a unique grid point, save, reload, and reopen the same workflow.
   - Assert: All thirteen nodes return with the same runtime types, defaults, and positions.
2. **Invalid_drop_outside_canvas_is_ignored**
   - Purpose: Ensure drag logic does not create orphan nodes.
   - Harness: Playwright
   - Arrange: Start dragging a valid palette entry.
   - Act: Release it outside the canvas bounds.
   - Assert: No new node is created and the palette remains usable.
3. **Repeated_creation_generates_unique_node_ids**
   - Purpose: Protect workflow integrity during mass authoring.
   - Harness: Playwright
   - Arrange: Drag the same runtime type onto the canvas three times.
   - Act: Save and inspect the resulting node ids.
   - Assert: All ids are unique and persisted through reload.

### 10. Golden-file / parity tests
- **Defaults parity** — A golden diff against the all-kinds round-trip fixture proves the drag factory creates canonical engine-shaped nodes with no UI-only leakage into YAML.
- **Runtime parity carry-forward** — The runtime and DTO parity suites from `docs/workflow-ui-plan.md:15-77` must stay green after the palette catalog is introduced.
- **Creation snapshot** — A JSON snapshot of the per-kind factory output is permitted so reviewers can audit defaults without stepping through the UI.

### 11. Edge cases & negative tests
- Dragging a runtime type whose registry entry is missing defaults throws a descriptive factory error instead of creating partial state.
- A drop occurs before the canvas measurement is ready; creation is deferred until coordinates can be normalized.
- A schema-required field like `PowerShell.scriptPath` would otherwise be empty; the factory uses the engine default shape rather than omitting the field.
- Multiple rapid drags of the same kind still generate unique node ids.
- A sidecar position already exists for the new id due to a collision; creation retries id allocation before inserting anything.

### 12. Observability hooks
- **Zustand action** — Emit `palette.node_created` with `runtimeType`, `nodeId`, `group`, `x`, `y`, and `source=drag`.
- **Zustand action** — Emit `palette.drag_rejected` with `runtimeType` and `reason` when a drop is outside the canvas or otherwise invalid.
- **Structured log field set** — Include `defaultSetVersion` and `schemaValidationResult` on debug logs from `createNodeFromKind`.
- **Test hook** — Expose `data-runtime-type` on palette entries so Playwright can iterate deterministically.

### 13. Risks & mitigations
- **Risk:** Engine defaults drift from the UI factory.
  - Mitigation: Keep all defaults in a single factory module, cite engine line numbers in the spec, and snapshot every factory output.
- **Risk:** Drag-and-drop behavior differs by browser.
  - Mitigation: Normalize the payload format and use Playwright for real pointer geometry instead of relying on jsdom alone.
- **Risk:** Palette grouping gets treated as engine taxonomy.
  - Mitigation: Preserve runtimeType as the persisted identifier and keep grouping metadata UI-only in the catalog.

### 14. Evaluator gap-analysis prompts
1. Which file is the single source of truth for palette defaults, and do drag plus keyboard creation both call it?
2. How are engine defaults for loops and composition nodes asserted in tests?
3. What test proves all thirteen kinds survive a save/reload cycle after drag creation?
4. How is sidecar position persistence separated from YAML persistence during creation?
5. What log fields would help debug a report that drag creation works for some kinds but not others?
6. Can the evaluator find evidence that invalid drops do not create orphan nodes?

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
- A Playwright run drops all thirteen kinds and reloads them successfully.
- Factory snapshots make default drift reviewable in code review diffs.


## Step 3.2.2 — Keyboard creation with focused kind picker

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Add a focused-canvas `N` shortcut that opens a searchable kind picker and routes creation through the same factory used by drag-and-drop. The picker should respect keyboard-first authoring: arrow navigation, enter-to-create, escape-to-close, and predictable focus return to the canvas. Creation location must be deterministic when triggered from the keyboard, typically near the viewport center or current selection anchor, and must still populate engine defaults through the shared factory. The shortcut must be scoped to the canvas so it never steals `N` keystrokes from Monaco, form fields, or unrelated page elements. This step should also establish command-surface conventions used by later node-reference pickers.

### 4. Depends on
- Step 3.2.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` requires a keyboard hotkey `N` that opens a command palette for node creation.
- **Palette factory** — Step 3.2.1 already provides `createNodeFromKind`; the keyboard path must reuse it rather than cloning defaults.
- **Runtime catalog** — The picker options are derived from the same thirteen runtime kinds in `src/ExecutionEngine/Enums/RuntimeType.cs:12-78`.
- **Canvas shell** — Step 3.1.1 already established focusable canvas landmarks needed to scope the shortcut safely.

### 6. Deliverables
- **src/ui/palette/**
  - `src/ui/palette/KindPickerDialog.tsx` (new) — Exports `KindPickerDialog`; shadcn command-style picker for runtime kinds.
  - `src/ui/palette/createNodeFromKind.ts` (modified) — Reused by the keyboard creation command without a separate default path.
- **src/ui/hooks/**
  - `src/ui/hooks/useCanvasShortcuts.ts` (new) — Exports `useCanvasShortcuts`; scopes `N` to canvas focus and ignores text-entry contexts.
- **src/ui/state/**
  - `src/ui/state/creationCommands.ts` (modified) — Adds `createNodeAtViewportCenter` and focus-return helpers.
- **src/ui/__tests__/palette/**
  - `src/ui/__tests__/palette/KindPickerDialog.test.tsx` (new) — RTL coverage for open, navigate, submit, and escape behaviors.
- **src/ui/__tests__/hooks/**
  - `src/ui/__tests__/hooks/useCanvasShortcuts.test.tsx` (new) — Shortcut scoping tests for canvas focus, form focus, and editor focus.
- **e2e/**
  - `e2e/keyboard-node-creation.spec.ts` (new) — Playwright hotkey and keyboard-navigation coverage.

### 7. Acceptance criteria
- [ ] Pressing `N` with canvas focus opens the kind picker.
  - Evidence: RTL and Playwright both assert the dialog appears only when the canvas owns focus.
- [ ] Pressing `N` while focus is in a form field or editor does nothing.
  - Evidence: Shortcut tests mount Monaco/text inputs and confirm the dialog stays closed.
- [ ] Keyboard creation calls the same factory path as drag creation.
  - Evidence: A unit spy or dependency injection check shows the dialog submit path invokes `createNodeFromKind`.
- [ ] The picker supports arrow navigation, enter to select, and escape to cancel.
  - Evidence: Dialog tests cover all three interactions with focus assertions.
- [ ] Created nodes land at a deterministic viewport-relative location.
  - Evidence: Store tests assert keyboard-created nodes use the shared viewport-center placement helper.
- [ ] Focus returns to the canvas after creation or cancellation.
  - Evidence: Playwright and RTL confirm keyboard users can continue navigating the canvas immediately.

### 8. Unit tests
1. **useCanvasShortcuts_opens_picker_on_N_with_canvas_focus**
   - Purpose: Checks the core hotkey path.
   - Harness: Vitest + RTL
   - Arrange: Focus the canvas surface and mount the shortcut hook.
   - Act: Dispatch the `N` key.
   - Assert: The kind picker opens exactly once.
2. **useCanvasShortcuts_ignores_N_in_text_inputs**
   - Purpose: Prevents shortcut conflicts.
   - Harness: Vitest + RTL
   - Assertion focus: Typing `N` into an input leaves the dialog closed.
3. **useCanvasShortcuts_ignores_N_in_monaco_editor**
   - Purpose: Prevents script editing conflicts.
   - Harness: Vitest with editor focus shim
   - Assertion focus: The shortcut is suppressed when Monaco owns focus.
4. **KindPickerDialog_supports_arrow_navigation**
   - Purpose: Supports keyboard-first interaction.
   - Harness: Vitest + RTL
   - Assertion focus: Arrow keys move the active option predictably through the list.
5. **KindPickerDialog_submits_selected_kind**
   - Purpose: Connects the picker to creation commands.
   - Harness: Vitest + RTL
   - Arrange: Open the dialog with the palette catalog fixture.
   - Act: Move to a target entry and press Enter.
   - Assert: The selected runtime type is passed to the creation callback.
6. **KindPickerDialog_closes_on_escape**
   - Purpose: Checks cancellation behavior.
   - Harness: Vitest + RTL
   - Assertion focus: Escape closes the dialog and restores focus to the canvas.
7. **createNodeAtViewportCenter_uses_current_viewport**
   - Purpose: Makes placement deterministic.
   - Harness: Vitest store test
   - Assertion focus: The helper derives the insertion point from the current viewport center and snap grid.
8. **Keyboard_creation_reuses_createNodeFromKind**
   - Purpose: Prevents default drift between input modes.
   - Harness: Vitest spy test
   - Assertion focus: The submit handler invokes `createNodeFromKind` rather than constructing a node inline.

### 9. Integration tests
1. **Hotkey_opens_picker_and_creates_node**
   - Purpose: Exercise the end-to-end keyboard flow.
   - Harness: Playwright
   - Arrange: Focus the canvas, press `N`, choose `While`, and submit.
   - Act: Save and reload the workflow.
   - Assert: A `While` node appears at the expected location with default values preserved.
2. **Hotkey_is_scoped_to_canvas_focus**
   - Purpose: Validate conflict prevention.
   - Harness: Playwright
   - Arrange: Focus a search box or property-grid input.
   - Act: Press `N`.
   - Assert: No kind picker opens and typed content remains intact.
3. **Escape_cancels_without_side_effects**
   - Purpose: Prove cancellation is history-safe.
   - Harness: Playwright
   - Arrange: Open the kind picker with `N`.
   - Act: Press Escape and then inspect undo/history state.
   - Assert: No node is created and no new history frame is added.

### 10. Golden-file / parity tests
- **Factory parity carry-forward** — Keyboard creation must produce the same golden DTO output as drag creation for the selected kind.
- **Shortcut isolation snapshot** — A keyboard-focus snapshot proves the picker is absent while text inputs or Monaco own focus.
- **Parity suites remain green** — No engine-facing contract changes are introduced, so `docs/workflow-ui-plan.md:15-77` stays unchanged.

### 11. Edge cases & negative tests
- The user presses `N` while the picker is already open; the second keystroke is ignored.
- The viewport center is outside the current canvas bounds because of resize lag; creation clamps to the nearest valid grid cell.
- The picker search list is empty due to a bad filter; Enter is disabled and the dialog remains cancelable.
- A screen-reader user opens the dialog; focus lands on the command input rather than an off-screen list item.
- Escape is pressed after a partial IME composition; the dialog still closes cleanly without inserting text into the canvas.

### 12. Observability hooks
- **Zustand action** — Emit `palette.kind_picker.opened` with `source=hotkey` and the current viewport triple.
- **Zustand action** — Emit `palette.kind_picker.submitted` with `runtimeType`, `nodeId`, and `placementStrategy`.
- **Zustand action** — Emit `palette.kind_picker.dismissed` with `reason=escape|blur|submit`.
- **Structured log field set** — Track `focusedElementRole` when the hotkey is ignored so conflicts can be diagnosed quickly.

### 13. Risks & mitigations
- **Risk:** Global hotkeys conflict with editors and form controls.
  - Mitigation: Scope the hook to canvas focus and explicitly ignore known text-entry contexts.
- **Risk:** Keyboard creation diverges from drag creation defaults.
  - Mitigation: Submit through the shared factory and assert equivalence in unit tests.
- **Risk:** Focus is lost after dialog dismissal, hurting accessibility.
  - Mitigation: Add focus-return helpers and cover them in RTL plus Playwright.

### 14. Evaluator gap-analysis prompts
1. What code path proves keyboard creation uses the same defaults as drag creation?
2. How do the tests show `N` is ignored inside Monaco and form inputs?
3. Which helper determines the placement of a keyboard-created node?
4. What evidence shows cancellation leaves both graph state and history untouched?
5. Can the evaluator point to the exact focus-return assertion after the picker closes?
6. Which logs help diagnose a report that the hotkey fires even when a text field is focused?

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
- The keyboard-creation Playwright test passes with both creation and cancellation branches.
- Accessibility review confirms focus enters and exits the dialog predictably.


## Step 3.2.3 — Palette search filter

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Add fast client-side filtering to the palette and kind picker so authors can find a runtime type without scanning all groups manually. Filtering should match display labels, runtime names, and curated keywords, while preserving group headers and clear empty-state messaging. The search logic must be pure data so the palette list and keyboard picker reuse one implementation. It should also be stable enough for future add-a-kind work: a new runtime type only needs catalog metadata, not custom filter code. Because filtering is UI-only, it must not change saved payloads or history.

### 4. Depends on
- Step 3.2.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` requires that typing filters palette entries in Step 3.2.3.
- **Palette catalog** — Step 3.2.1 already defines the grouped runtime catalog the search logic must query.
- **Runtime taxonomy** — Filter keys should still map back to the thirteen engine runtime types in `src/ExecutionEngine/Enums/RuntimeType.cs:12-78`.

### 6. Deliverables
- **src/ui/palette/**
  - `src/ui/palette/filterPaletteEntries.ts` (new) — Exports `filterPaletteEntries`; pure search helper reused by palette and kind picker UIs.
  - `src/ui/palette/Palette.tsx` (modified) — Adds a search box, filtered groups, and empty-state rendering.
  - `src/ui/palette/KindPickerDialog.tsx` (modified) — Reuses the same filter helper for keyboard creation search.
- **src/ui/__tests__/palette/**
  - `src/ui/__tests__/palette/filterPaletteEntries.test.ts` (new) — Unit tests for search terms, keyword matches, and empty results.
  - `src/ui/__tests__/palette/Palette.filter.test.tsx` (new) — RTL tests for visible entries and empty-state copy.
- **e2e/**
  - `e2e/palette-search.spec.ts` (new) — Playwright coverage for filter-as-you-type in both the palette and the kind picker.

### 7. Acceptance criteria
- [ ] Typing in the palette search box filters entries immediately.
  - Evidence: RTL and Playwright both assert visible entries shrink as the query changes.
- [ ] Filtering is case-insensitive and matches display labels plus keywords.
  - Evidence: Unit tests search by uppercase terms, runtime names, and synonyms.
- [ ] The same filter helper powers the keyboard kind picker.
  - Evidence: A unit-level import check and shared tests prove both surfaces call `filterPaletteEntries`.
- [ ] Empty results show a clear no-matches message instead of a blank panel.
  - Evidence: RTL and Playwright confirm the empty-state copy renders when nothing matches.
- [ ] Clearing the query restores the full grouped catalog.
  - Evidence: Tests verify groups and counts return to their default state after clearing.
- [ ] Filtering does not mutate catalog ordering or persisted workflow state.
  - Evidence: Data-level tests prove the helper returns a derived view without changing source arrays or saved payloads.

### 8. Unit tests
1. **filterPaletteEntries_matches_display_label**
   - Purpose: Covers the basic search path.
   - Harness: Vitest
   - Arrange: Load the full palette catalog fixture.
   - Act: Search for `while`.
   - Assert: The result set contains only the While entry.
2. **filterPaletteEntries_is_case_insensitive**
   - Purpose: Avoids casing bugs.
   - Harness: Vitest
   - Assertion focus: Searching `POWERSHELL` still matches PowerShell entries.
3. **filterPaletteEntries_matches_keywords**
   - Purpose: Supports synonyms beyond visible labels.
   - Harness: Vitest
   - Assertion focus: Search terms like `loop` or `branch` return the expected kinds.
4. **filterPaletteEntries_preserves_group_order**
   - Purpose: Keeps the filtered UI stable.
   - Harness: Vitest
   - Assertion focus: Filtered results keep the canonical group ordering from the catalog.
5. **filterPaletteEntries_returns_empty_result_shape**
   - Purpose: Supports predictable empty-state rendering.
   - Harness: Vitest
   - Assertion focus: No-match queries still return group metadata suitable for UI messaging.
6. **Palette_renders_empty_state_for_no_matches**
   - Purpose: Checks the visual no-results state.
   - Harness: Vitest + RTL
   - Assertion focus: The no-matches message appears when the query removes all entries.
7. **Palette_clearing_query_restores_full_catalog**
   - Purpose: Covers the reset path.
   - Harness: Vitest + RTL
   - Assertion focus: Deleting the query brings all groups and items back.
8. **KindPickerDialog_reuses_filter_helper**
   - Purpose: Prevents diverging search behavior.
   - Harness: Vitest + RTL
   - Assertion focus: The same query yields the same filtered runtime types in the picker and sidebar palette.

### 9. Integration tests
1. **Palette_sidebar_filters_as_you_type**
   - Purpose: Exercise the visible sidebar search.
   - Harness: Playwright
   - Arrange: Open the designer with the palette visible.
   - Act: Type `loop` into the palette search box.
   - Assert: Only loop-related entries remain visible and selectable.
2. **Kind_picker_search_matches_sidebar_results**
   - Purpose: Keep both entry points aligned.
   - Harness: Playwright
   - Arrange: Open the kind picker with `N`.
   - Act: Type the same query used in the sidebar.
   - Assert: The picker shows the same runtime types in the same order.
3. **Clearing_query_restores_full_catalog**
   - Purpose: Check the reset behavior in browser.
   - Harness: Playwright
   - Arrange: Apply a narrowing search.
   - Act: Clear the query.
   - Assert: All groups and entries reappear without a page reload.

### 10. Golden-file / parity tests
- **Catalog snapshot** — A snapshot of filtered result ids for a few stable queries makes search drift easy to review.
- **Parity carry-forward** — The search helper is UI-only, so the engine parity suites from `docs/workflow-ui-plan.md:15-77` remain unchanged and green.
- **No-save guarantee** — Saving a workflow before and after palette searching produces no YAML or sidecar diff.

### 11. Edge cases & negative tests
- A query matches both a keyword and a display label; the entry still appears only once.
- Whitespace-only queries normalize to the unfiltered catalog.
- A future runtime type is added without keywords; it is still searchable by label and runtime name.
- The user pastes a long string with punctuation; filtering remains stable and safe.
- Empty-state copy localizes later without changing the filter helper semantics.

### 12. Observability hooks
- **Zustand action** — Emit `palette.search.changed` with `queryLength`, `matchCount`, and `surface=sidebar|picker`.
- **Structured log field set** — Capture `normalizedQuery` and `matchedRuntimeTypes` on debug logs during filter evaluation.
- **UI trace** — Expose stable test ids for the search input and empty-state message.
- **Performance mark** — Record `palette.search.compute_ms` to catch regressions if the catalog grows.

### 13. Risks & mitigations
- **Risk:** Palette and picker searches diverge over time.
  - Mitigation: Reuse one pure helper module and assert equal outputs in tests.
- **Risk:** Search becomes brittle when new runtime types are added.
  - Mitigation: Drive search keys from catalog metadata rather than switch statements.
- **Risk:** Filtering mutates the source catalog and causes ordering drift.
  - Mitigation: Return derived arrays only and cover immutability in unit tests.

### 14. Evaluator gap-analysis prompts
1. Which module owns the search logic, and do both the sidebar and picker import it?
2. How is case-insensitive and keyword-based matching demonstrated?
3. What evidence shows filtering has no persistence side effects?
4. How do the tests prove clearing the query restores full group order?
5. Which log fields would help diagnose a complaint that `loop` fails to show `While`?
6. Can the evaluator see browser coverage for both sidebar and picker search surfaces?

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
- The same query produces identical results in the sidebar palette and kind picker.
- No search interaction changes saved workflow content or history state.

## Appendix A — Runtime coverage map

| Runtime type | Group | Creation path(s) covered in this stage | Default-sensitive fields to watch |
|---|---|---|---|
| Noop | Task | drag, keyboard, search | base defaults only |
| CSharp | Task | drag, keyboard, search | `assemblyPath`, `typeName` |
| CSharpScript | Task | drag, keyboard, search | `scriptPath`, `scriptContent` |
| CSharpTask | Task | drag, keyboard, search | `scriptContent`, `executorTypeName`, `executorAssemblyPath` |
| PowerShell | Task | drag, keyboard, search | `scriptPath`, `requiredModules`, `modulePaths` |
| PowerShellTask | Task | drag, keyboard, search | `scriptPath`, `scriptContent`, module fields |
| IfElse | Control | drag, keyboard, search | `condition` |
| Switch | Control | drag, keyboard, search | `expression`, `cases` |
| ForEach | Control | drag, keyboard, search | `collectionExpression`, `itemVariableName=item` |
| While | Control | drag, keyboard, search | `conditionExpression`, `maxIterations=1000` |
| Subflow | Composition | drag, keyboard, search | `workflowFilePath`, `workflowDefinition`, `timeout`, `skipValidation` |
| Timer | Composition | drag, keyboard, search | `schedule` |
| Container | Composition | drag, keyboard, search | `childNodes`, `childConnections`, `executionMode=Sequential` |

## Appendix B — Cross-step invariants

- Palette catalog ids must always be keyed by engine `RuntimeType`, never by display labels.
- Grouping metadata is UI-only and must never appear in saved YAML.
- Every creation path must call the same `createNodeFromKind` factory.
- Node ids must be generated once, then reused by sidecar position storage and undo history.
- Drag payloads must use one workflow-specific MIME type for browser compatibility.
- Keyboard creation must be scoped to canvas focus and must ignore Monaco, text inputs, and dialog text boxes.
- Search must be pure and side-effect free so it can power both the sidebar palette and kind picker.
- Filtering must not reorder groups or mutate source catalog arrays.
- Save/reload tests must confirm defaults rather than only confirming node presence.
- Sidecar position writes must stay out of engine YAML.
- Playwright fixtures should place nodes on snap-grid coordinates so diffs stay stable.
- Any future add-a-kind change should require only catalog metadata plus factory defaults, not custom search logic.

## Appendix C — Reviewer walk-through

1. Open `paletteCatalog.ts` and verify there are thirteen runtime-backed entries.
2. Confirm Task / Control / Composition headings match the stage spec rather than ad hoc product wording.
3. Inspect `createNodeFromKind` and spot-check loop/composition defaults against engine source.
4. Verify drag creation and keyboard creation both import the same factory module.
5. Confirm invalid drops outside the canvas produce no persisted node or history frame.
6. Read the shortcut hook and confirm it ignores focused text-entry contexts.
7. Check the search helper for immutability: it should return derived arrays, not mutate the catalog.
8. Run the Playwright drag suite and confirm every runtime type survives save/reload.
9. Run the Playwright hotkey suite and confirm `N` is canvas-scoped.
10. Run the Playwright search suite and confirm the picker and sidebar return identical query results.
11. Inspect any golden or snapshot artifacts and verify sidecar state changes do not appear in YAML.
12. Review logs for `palette.node_created`, `palette.kind_picker.opened`, and `palette.search.changed` when debugging failures.

## Appendix D — Failure signatures

- **Nodes created with wrong defaults** — inspect `createNodeFromKind` first, then compare against the engine citations in the stage steps.
- **Hotkey fires inside text inputs** — inspect `useCanvasShortcuts` focus guards and Playwright scope tests.
- **Search results differ between sidebar and picker** — inspect `filterPaletteEntries` call sites and shared query normalization.
- **Drop positions drift after reload** — inspect sidecar position persistence rather than mapper or YAML code.
- **Duplicate ids appear after repeated creation** — inspect id allocation and creation command deduping.
- **A new runtime type is missing from the palette** — inspect catalog coverage against the engine enum before touching UI labels.
- **Loop defaults are wrong after creation** — spot-check `itemVariableName` and `maxIterations` against the engine source citations.
- **Subflow or Container defaults drift** — inspect composition defaults, especially timeout and execution mode, before editing tests.

## Appendix E — Minimal manual smoke set

- Drag one task node and confirm defaults on reload.
- Drag one control-flow node and confirm defaults on reload.
- Drag one composition node and confirm defaults on reload.
- Press `N` with canvas focus and create a node.
- Press `N` inside a text field and confirm nothing happens.
- Search for `loop` and compare sidebar vs picker results.
- Clear the search and confirm full catalog restoration.
- Review creation logs for a drag, a hotkey create, and a rejected drop.
