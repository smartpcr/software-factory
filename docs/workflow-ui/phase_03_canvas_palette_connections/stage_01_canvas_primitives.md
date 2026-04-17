# Stage 3.1 — Canvas primitives

**Phase:** Canvas, Palette, Connections  
**Stage status:** [ ]  
**Total effort:** 6 d  
**Step count:** 4

## Steps

- [ ] **Step 1**: React Flow shell and empty grid
- [ ] **Step 2**: Custom node renderer and icon registry
- [ ] **Step 3**: Per-node execution status badge
- [ ] **Step 4**: Minimap and fit-view controls

---
## Context
Phase 3 turns the contract work from Phases 1-2 into a usable authoring surface. The master plan calls for a React Flow v12 canvas with a pure-engine YAML core, UI-only state in the sidecar, and parity tests that keep the UI from inventing runtime behavior (docs/workflow-ui-plan.md:78-88, docs/workflow-ui-plan.md:705-804). This stage is the first visible proof that those earlier contracts can be rendered without mutating engine-owned fields or leaking UI metadata into YAML.
The node shell must mirror the engine's runtime taxonomy and status surface instead of introducing UI-only kinds or extra execution states. Runtime kind coverage comes from `RuntimeType` (src/ExecutionEngine/Enums/RuntimeType.cs:12-78), and the status badge must map only the six `NodeExecutionStatus` values that already exist in-engine (src/ExecutionEngine/Enums/NodeExecutionStatus.cs:12-42). Connection-bearing canvas objects also need to stay compatible with the full `NodeConnection` surface so later stages can add editing without re-shaping edge data (src/ExecutionEngine/Workflow/NodeConnection.cs:21-99).
This stage also unblocks palette drops, property-grid selection, fit-view, minimap navigation, and later telemetry overlays. The YAML boundary remains server-side because `WorkflowLoader` and `WorkflowSerializer` already own validation and serialization behavior (src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149, src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212); the canvas only reflects the graph and sidecar viewport state described in the plan (docs/workflow-ui-plan.md:97-277).

## Prerequisites
- Phase gates (§0.2) green
- Prior stages completed
- External deps (React Flow v12, zustand, zundo, react-hook-form, zod, shadcn, ELKjs)

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `e2e/canvas-navigation.spec.ts`
- `e2e/canvas-node-rendering.spec.ts`
- `e2e/canvas-primitives.spec.ts`
- `e2e/node-status-badges.spec.ts`
- `src/ui/__tests__/canvas/WorkflowCanvas.test.tsx`
- `src/ui/__tests__/canvas/useCanvasViewport.test.ts`
- `src/ui/__tests__/canvas/useFitWorkflow.test.ts`
- `src/ui/__tests__/minimap/WorkflowMiniMap.test.tsx`
- `src/ui/__tests__/nodes/NodeStatusBadge.test.tsx`
- `src/ui/__tests__/nodes/WorkflowNode.status.test.tsx`
- `src/ui/__tests__/nodes/WorkflowNode.test.tsx`
- `src/ui/__tests__/nodes/nodeKindRegistry.test.tsx`
- `src/ui/canvas/CanvasSurface.tsx`
- `src/ui/canvas/FitViewButton.tsx`
- `src/ui/canvas/WorkflowCanvas.tsx`
- `src/ui/canvas/canvasConstants.ts`
- `src/ui/canvas/nodeTypes.ts`
- `src/ui/canvas/useCanvasViewport.ts`
- `src/ui/canvas/useFitWorkflow.ts`
- `src/ui/minimap/WorkflowMiniMap.tsx`
- `src/ui/nodes/NodeChrome.tsx`
- `src/ui/nodes/NodeStatusBadge.tsx`
- `src/ui/nodes/WorkflowNode.tsx`
- `src/ui/nodes/nodeKindRegistry.tsx`
- `src/ui/nodes/statusBadgeTokens.ts`
- `src/ui/state/executionOverlay.ts`
- `src/ui/state/graphStore.ts`
- `src/ui/theme/nodeIcons.ts`

## Shared fixtures / fakes
- `src/ui/__tests__/fixtures/workflows/empty-workflow.ts` — empty graph DTO with viewport defaults.
- `src/ui/__tests__/fixtures/workflows/all-kinds-canvas.ts` — one node per runtime type for renderer and fit-view tests.
- `src/ui/__tests__/fixtures/execution/node-statuses.ts` — status-to-badge token fixture covering every `NodeExecutionStatus` value.
- `e2e/fixtures/canvas/empty-workflow.ui.json` — viewport and snap-grid baseline consumed by Playwright smoke tests.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="3.1"`, `phaseId="3"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-3.1.plan-read`, `workflow-ui.stage-3.1.apply`, `workflow-ui.stage-3.1.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_3_1_started_total`, counter `workflow_ui_stage_3_1_failed_total`, histogram `workflow_ui_stage_3_1_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 3.1 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage31` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Keyboard-create a node from the palette, move it, connect it, then undo/redo twice.
- Drag 25 nodes quickly and confirm selection/position state stays stable after a refresh.
- Open the canvas with reduced-motion enabled and confirm animations degrade gracefully.

---

### Step 1 — React Flow shell and empty grid

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Create the React Flow composition root that every later authoring step hangs from. The canvas must render an empty workflow without crashing, expose a predictable snap grid, and block browser scroll-jacking so the designer does not steal page scrolling when the pointer is outside the intentional interaction zone. The wrapper should be thin: it adapts Zustand graph state into React Flow props, centralizes viewport defaults, and leaves node/edge visuals to later steps. It must also reserve extension points for fit-view, minimap, drag-and-drop, and validation overlays so those steps can add behavior without reworking the shell. Because execution and YAML remain outside history, the wrapper must keep execution state read-only and history-neutral from the start.

### 4. Depends on
- - Step 2.1.2

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` defines the empty-grid, snap, and pan/zoom smoke expectations for Step 3.1.1.
- **Architecture rules** — `docs/workflow-ui-plan.md:78-88` keeps React Flow on the client and preserves YAML authority on the server.
- **Workflow graph contract** — `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` shows that canvas state mirrors `WorkflowDefinition.Nodes` and `WorkflowDefinition.Connections` only.
- **Connection surface** — `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` confirms edges already carry engine-owned fields even before editing is enabled.
- **Quality gate** — `docs/workflow-ui-plan.md:292-298` requires parity and round-trip suites to stay green even for UI-only shell work.

### 6. Deliverables
- **src/ui/canvas/**
  - `src/ui/canvas/WorkflowCanvas.tsx` (new) — Exports `WorkflowCanvas`; composes `ReactFlow`, `Background`, `Controls`, and store bindings with scroll-guard wiring.
  - `src/ui/canvas/canvasConstants.ts` (new) — Exports `CANVAS_SNAP_GRID`, `DEFAULT_VIEWPORT`, and wheel/pointer guard constants used across the phase.
  - `src/ui/canvas/useCanvasViewport.ts` (new) — Exports `useCanvasViewport`; syncs React Flow viewport events to sidecar-safe state without entering undo history.
  - `src/ui/canvas/CanvasSurface.tsx` (new) — Exports `CanvasSurface`; isolates the host div and accessibility roles used by tests and future overlays.
- **src/ui/state/**
  - `src/ui/state/graphStore.ts` (modified) — Adds selectors consumed by the canvas shell without storing React Flow instance objects in history.
- **src/ui/__tests__/canvas/**
  - `src/ui/__tests__/canvas/WorkflowCanvas.test.tsx` (new) — Vitest + RTL coverage for empty render, snap-grid props, and scroll-guard behavior.
  - `src/ui/__tests__/canvas/useCanvasViewport.test.ts` (new) — Unit coverage for viewport persistence and history exclusion.
- **e2e/**
  - `e2e/canvas-primitives.spec.ts` (new) — Playwright smoke for empty canvas render, wheel behavior, and baseline pan/zoom interactions.

### 7. Acceptance criteria
- [ ] Empty workflows render a visible canvas shell, grid background, and zero-node state without exceptions.
  - Evidence: RTL test mounts `WorkflowCanvas` with an empty workflow fixture and asserts the grid and aria-labelled surface appear.
- [ ] The canvas exposes snap-to-grid coordinates using the shared constant instead of inline literals.
  - Evidence: Unit tests assert `CANVAS_SNAP_GRID` is passed through to React Flow and reused by future drop logic.
- [ ] Wheel and trackpad input only zoom or pan when the pointer is inside the canvas surface and modifier rules allow it.
  - Evidence: Playwright verifies page scroll still works when the cursor is outside the canvas bounds.
- [ ] Viewport changes persist to sidecar-safe state and do not create undo history frames.
  - Evidence: Store tests assert viewport writes skip the zundo history slice.
- [ ] No engine-owned node or connection fields are created by the empty shell.
  - Evidence: Serialized empty workflow output still contains only `WorkflowDefinition` properties from the contract cited above.
- [ ] The shell reserves hook points for minimap, controls, and selection overlays without requiring a rewrite.
  - Evidence: Component tests and code review confirm `WorkflowCanvas` passes slot props and stable test ids for later stages.

### 8. Unit tests
1. **WorkflowCanvas_renders_empty_grid_with_snap**
   - Purpose: Proves the base shell mounts with an empty workflow and the snap grid configured.
   - Harness: Vitest + RTL
   - Arrange: Render `WorkflowCanvas` with the empty workflow fixture and default viewport.
   - Act: Allow the React Flow host to finish layout and query the canvas surface.
   - Assert: The surface, grid background, and snap-grid prop are all present; no error boundary fires.
2. **WorkflowCanvas_does_not_create_placeholder_edges**
   - Purpose: Guards against accidental synthetic edges on empty load.
   - Harness: Vitest + RTL
   - Assertion focus: The DOM contains no edge paths and the store edge count stays zero.
3. **Wheel_guard_ignores_events_outside_canvas**
   - Purpose: Ensures browser-level scrolling is not hijacked.
   - Harness: Vitest + jsdom synthetic wheel events
   - Arrange: Dispatch a wheel event on a wrapper element that is not the canvas host.
   - Act: Run the wheel handler exported from `CanvasSurface`.
   - Assert: No zoom callback fires and the event is not prevented.
4. **Wheel_guard_handles_ctrl_zoom_inside_canvas**
   - Purpose: Confirms intentional zoom interactions still work.
   - Harness: Vitest
   - Assertion focus: Ctrl+wheel inside the host invokes the zoom branch and prevents default scrolling.
5. **useCanvasViewport_persists_viewport_to_sidecar_slice**
   - Purpose: Checks viewport persistence.
   - Harness: Vitest store test
   - Arrange: Create the graph store, attach the hook callback, and feed a viewport change event.
   - Act: Invoke the change handler with a new x/y/zoom triple.
   - Assert: The sidecar slice updates to the new viewport values.
6. **useCanvasViewport_excludes_execution_state_from_history**
   - Purpose: Preserves the zundo rule from the technology pins.
   - Harness: Vitest store test
   - Assertion focus: Viewport writes and execution overlays are absent from the undo stack.
7. **CanvasSurface_exposes_accessibility_landmarks**
   - Purpose: Makes the shell testable and keyboard-targetable.
   - Harness: Vitest + RTL
   - Assertion focus: The surface exposes role, label, and tab-stop behavior required by later hotkeys.
8. **Canvas_constants_are_shared_and_importable**
   - Purpose: Prevents drift between shell, drag, and fit-view code paths.
   - Harness: Vitest
   - Assertion focus: All consuming modules import the same constant module rather than redefining pixel values.

### 9. Integration tests
1. **Empty_canvas_smoke**
   - Purpose: Open the designer with an empty workflow and confirm the grid renders immediately.
   - Harness: Playwright
   - Arrange: Launch the UI against the dev host seeded with an empty workflow fixture.
   - Act: Navigate to the designer route and wait for the canvas surface.
   - Assert: The grid is visible, the canvas is interactive, and no browser console errors appear.
2. **Scroll_guard_preserves_page_scroll**
   - Purpose: Validate the anti-scroll-jack behavior the plan explicitly calls out.
   - Harness: Playwright
   - Arrange: Load a page with surrounding vertical content and place the mouse outside the canvas.
   - Act: Send wheel input, then repeat inside the canvas with Ctrl held.
   - Assert: Outside the canvas the page scrolls; inside the canvas the viewport zooms instead.
3. **Viewport_reload_keeps_sidecar_values**
   - Purpose: Shows that basic viewport persistence is already wired for later sidecar save/load work.
   - Harness: Playwright
   - Arrange: Pan and zoom to a non-default viewport.
   - Act: Reload the page using the same fixture and re-open the designer.
   - Assert: The viewport returns to the stored x/y/zoom triple.

### 10. Golden-file / parity tests
- **Parity carry-forward** — No new golden artifact is authored here, but the runtime and DTO parity suites from `docs/workflow-ui-plan.md:15-77` must remain green after the shell files are added.
- **Empty-workflow canonicalization** — The empty workflow fixture serializes to the same canonical YAML before and after the shell work, proving that UI shell code does not leak authoring metadata.
- **Viewport sidecar isolation** — A sidecar-only viewport snapshot diff shows changes in `.ui.json` only; YAML diff stays empty.

### 11. Edge cases & negative tests
- Wheel input arrives while the pointer is over a nested control inside the canvas header; the control consumes the event and the canvas does nothing.
- The canvas mounts with `viewport` undefined; the shell falls back to `DEFAULT_VIEWPORT` instead of emitting NaN coordinates.
- A stale store snapshot contains nodes but no connections array; the shell normalizes to an empty array rather than throwing.
- React Flow reports an instance before DOM measurements settle; the shell defers fit/zoom actions until layout is ready.
- A screen-reader user tabs into the empty surface; focus ring appears without forcing panning or zoom.

### 12. Observability hooks
- **Zustand action** — Emit `canvas.viewport.changed` with `workflowId`, `x`, `y`, `zoom`, and `source` when the viewport sync callback commits a change.
- **Zustand action** — Emit `canvas.surface.wheel_blocked` with `reason` (`outside-surface`, `missing-modifier`, `embedded-control`) when wheel events are ignored.
- **Browser trace** — Wrap initial mount in a `workflow-ui.canvas.mount` performance mark so Playwright can assert startup timing.
- **Structured log field set** — Reserve `nodeCount`, `edgeCount`, and `historyFrameCount` on the canvas debug logger even when all are zero.

### 13. Risks & mitigations
- **Risk:** Browser-specific wheel behavior differs between devices and causes accidental zoom.
  - Mitigation: Centralize the guard in `CanvasSurface` and cover mouse wheel plus trackpad-like gestures in Playwright.
- **Risk:** Viewport persistence accidentally pollutes undo history.
  - Mitigation: Route viewport writes through a history-excluded slice and add an explicit regression test.
- **Risk:** The canvas shell grows too opinionated and blocks later steps.
  - Mitigation: Keep nodeTypes, edgeTypes, and overlays injectable via props and exported helpers.

### 14. Evaluator gap-analysis prompts
1. Does `WorkflowCanvas` delegate viewport persistence through a history-excluded path, or does it mutate the main graph history directly?
2. Which file owns the snap-grid constant, and is every consumer importing that file instead of re-declaring the numbers?
3. How do the tests prove the wrapper does not create synthetic engine data for an empty workflow?
4. What evidence shows browser scroll still works when the pointer is outside the canvas surface?
5. Which logs or trace marks would help diagnose a canvas that mounts but does not respond to wheel input?
6. Can the evaluator point to a Playwright assertion that the viewport survives a reload?

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
- The empty-canvas Playwright smoke passes in CI on Chromium.
- No YAML or engine DTO fixture changes are required to support the shell.

### Step 2 — Custom node renderer and icon registry

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Implement the shared node card and registry that render every engine runtime type declared in `RuntimeType` (src/ExecutionEngine/Enums/RuntimeType.cs:12-78). Each runtime type must receive a distinct Lucide-based icon, a stable display label, and shared chrome that later stages can extend with ports, badges, and validation decorations. The renderer should be declarative: kind metadata lives in a registry module, while the canvas host only maps `runtimeType` to the exported node component. That separation keeps future add-a-kind work localized and lets parity tests fail cleanly when the engine adds a runtime type. The renderer must remain faithful to the engine taxonomy and must not introduce start/end pseudo-kinds or alternate labels outside sidecar metadata.

### 4. Depends on
- - Step 3.1.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` requires a custom renderer and thirteen distinct icons in Step 3.1.2.
- **Runtime taxonomy** — `src/ExecutionEngine/Enums/RuntimeType.cs:12-78` enumerates the exact thirteen runtime kinds the registry must cover.
- **Base fields** — `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95` defines the common fields the shared node chrome will display or expose for selection.
- **Per-kind field sources** — Task, control-flow, and composition node property names come from `src/ExecutionEngine/Nodes/Definitions/CSharpNodeDefinition.cs:13-59`, `src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:12-49`, `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62`, `src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:12-51`, `src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:12-57`, `src/ExecutionEngine/Nodes/Definitions/IfElseNodeDefinition.cs:12-24`, `src/ExecutionEngine/Nodes/Definitions/SwitchNodeDefinition.cs:12-39`, `src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28`, `src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35`, `src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60`, `src/ExecutionEngine/Nodes/Definitions/TimerNodeDefinition.cs:12-24`, and `src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:14-32`.

### 6. Deliverables
- **src/ui/nodes/**
  - `src/ui/nodes/nodeKindRegistry.tsx` (new) — Exports `nodeKindRegistry`, `getNodeDescriptor`, and the runtimeType-to-node metadata map.
  - `src/ui/nodes/WorkflowNode.tsx` (new) — Exports `WorkflowNode`; shared node component used by React Flow `nodeTypes`.
  - `src/ui/nodes/NodeChrome.tsx` (new) — Exports `NodeChrome`; shared card layout with icon slot, title, subtitle, and footer slots.
- **src/ui/canvas/**
  - `src/ui/canvas/nodeTypes.ts` (new) — Exports the React Flow `nodeTypes` map and keeps the renderer registration centralized.
- **src/ui/theme/**
  - `src/ui/theme/nodeIcons.ts` (new) — Exports Lucide icon bindings and color tokens with one distinct icon per runtime type.
- **src/ui/__tests__/nodes/**
  - `src/ui/__tests__/nodes/nodeKindRegistry.test.tsx` (new) — Asserts the registry covers every runtime type and emits distinct icons.
  - `src/ui/__tests__/nodes/WorkflowNode.test.tsx` (new) — RTL snapshot and behavior coverage for shared chrome and display labels.
- **e2e/**
  - `e2e/canvas-node-rendering.spec.ts` (new) — Playwright visual and semantic coverage for the all-kinds fixture.

### 7. Acceptance criteria
- [ ] All thirteen runtime types resolve to a registered node descriptor.
  - Evidence: A unit test enumerates the runtime contract and fails if any runtime type lacks a descriptor.
- [ ] Each runtime type renders a distinct icon from the approved Lucide set.
  - Evidence: Snapshot tests compare icon keys and accessible labels instead of only CSS classes.
- [ ] The shared node card shows `nodeName` and a stable fallback label when `nodeName` is empty.
  - Evidence: RTL coverage mounts nodes with and without names and asserts the fallback copy.
- [ ] Node chrome is reusable by later stages for badges, ports, validation, and selection affordances.
  - Evidence: The component exports named slots/props for `status`, `ports`, and `adornments` without hard-coding later behavior.
- [ ] No renderer branch depends on ad-hoc strings outside the registry.
  - Evidence: Code review and tests confirm `WorkflowNode` only consumes registry data and runtimeType values.
- [ ] The all-kinds fixture renders without missing icons, unknown labels, or duplicate descriptors.
  - Evidence: Playwright opens the all-kinds workflow and checks every node card for title, icon, and runtime marker.

### 8. Unit tests
1. **nodeKindRegistry_covers_every_runtime_type**
   - Purpose: Guarantees the UI registry stays exhaustive with the engine enum.
   - Harness: Vitest
   - Arrange: Import the generated runtime-type union and the registry keys.
   - Act: Compare the sorted sets and compute any missing or extra keys.
   - Assert: The sets match exactly; any drift fails the test.
2. **nodeKindRegistry_assigns_distinct_icon_keys**
   - Purpose: Implements the plan requirement that each kind has a distinct icon.
   - Harness: Vitest
   - Assertion focus: No descriptor reuses an icon key across the thirteen runtime types.
3. **WorkflowNode_renders_nodeName_and_runtime_label**
   - Purpose: Checks shared node chrome basics.
   - Harness: Vitest + RTL
   - Assertion focus: The rendered card shows the node name and human-readable runtime label.
4. **WorkflowNode_falls_back_when_nodeName_missing**
   - Purpose: Prevents blank title bars.
   - Harness: Vitest + RTL
   - Assertion focus: Fallback title text uses the registry label when `nodeName` is empty.
5. **WorkflowNode_exposes_test_ids_for_overlays**
   - Purpose: Supports later status, validation, and port assertions.
   - Harness: Vitest + RTL
   - Assertion focus: The shared chrome emits deterministic test ids for header, body, and footer regions.
6. **nodeTypes_exports_single_workflow_node_renderer**
   - Purpose: Keeps React Flow registration centralized.
   - Harness: Vitest
   - Assertion focus: The `nodeTypes` map points runtime-backed nodes to `WorkflowNode` and contains no dead registrations.
7. **icon_tokens_are_theme_safe**
   - Purpose: Prevents light/dark theme regressions.
   - Harness: Vitest
   - Assertion focus: Every descriptor references tokens from the approved theme table rather than raw colors.
8. **unknown_runtime_type_is_rejected_during_test_setup**
   - Purpose: Hardens evaluator coverage for future add-a-kind work.
   - Harness: Vitest
   - Assertion focus: Constructing a test node with an unregistered runtime type throws a registry error.

### 9. Integration tests
1. **All_kinds_canvas_visual_baseline**
   - Purpose: Render one node for every runtime type and capture a stable baseline.
   - Harness: Playwright
   - Arrange: Load the all-kinds fixture and wait for every node card to render.
   - Act: Capture the canvas and individual node card screenshots.
   - Assert: All cards show a non-empty icon region and a visible title.
2. **All_kinds_nodes_expose_accessible_labels**
   - Purpose: Verify the icon registry is not purely visual.
   - Harness: Playwright
   - Arrange: Iterate through every node card.
   - Act: Read the accessible name of the icon region and header.
   - Assert: Each node exposes a unique runtime label that matches the registry descriptor.
3. **Renderer_survives_reload_without_descriptor_drift**
   - Purpose: Proves registry output is deterministic.
   - Harness: Playwright
   - Arrange: Load, reload, and re-query the same all-kinds fixture.
   - Act: Compare icon labels and visible titles before and after reload.
   - Assert: The rendered metadata is identical across reloads.

### 10. Golden-file / parity tests
- **Runtime parity guard** — The enum-parity test from `docs/workflow-ui-plan.md:15-77` must fail if `RuntimeType` changes without a matching registry update.
- **Renderer metadata snapshot** — A small JSON snapshot of `nodeKindRegistry` descriptors is permitted so the evaluator can review icon keys and labels in diff form.
- **All-kinds fixture stability** — The existing all-kinds YAML/UI fixtures remain byte-stable; only rendered output changes.

### 11. Edge cases & negative tests
- A descriptor accidentally reuses an icon already assigned to another runtime type; the distinct-icon test fails immediately.
- The registry omits `Container`; the runtime parity assertion blocks the build.
- A node arrives with an empty `nodeName`; the renderer falls back to the registry label instead of showing a blank header.
- A theme token is missing; the renderer uses a safe neutral token and emits an observable warning.
- A future add-a-kind change updates the engine enum but not the registry; compile/test failure points directly at the missing descriptor.

### 12. Observability hooks
- **Zustand action** — Emit `canvas.node_renderer.missing_descriptor` with `runtimeType` if the UI ever hits an unknown kind at runtime.
- **Structured log field set** — Include `runtimeType`, `nodeId`, `descriptorLabel`, and `iconKey` on node-render debug logs.
- **Visual-regression note** — Store screenshot names that include the runtime type so CI diffs map directly to a failing descriptor.
- **Test telemetry** — Expose `data-runtime-type` on each node card to simplify Playwright diagnostics.

### 13. Risks & mitigations
- **Risk:** Icon choices drift into a license-restricted set.
  - Mitigation: Keep all icon imports in one Lucide-only module and add a snapshot of icon names.
- **Risk:** Renderer logic spreads across multiple files and becomes hard to audit.
  - Mitigation: Require the registry to be the single source for labels, icons, and kind metadata.
- **Risk:** Later status/validation work forces a node-card rewrite.
  - Mitigation: Design `NodeChrome` with explicit slots for later adornments instead of fixed markup.

### 14. Evaluator gap-analysis prompts
1. Can the evaluator trace every runtime type from the engine enum to a concrete descriptor in `nodeKindRegistry.tsx`?
2. Which test proves icon keys are unique rather than just non-null?
3. Does the shared node card leave obvious insertion points for status badges and validation markers?
4. How would a new runtime type fail fast if added to the engine without UI support?
5. Which files are the only ones allowed to know about Lucide icon imports?
6. What Playwright evidence shows all thirteen cards render on the canvas together?

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
- The all-kinds renderer baseline is stable across two consecutive CI runs.
- The registry can be audited from a single file without searching the codebase for per-kind icon constants.

### Step 3 — Per-node execution status badge

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Add a status badge to the shared node card that maps only the six `NodeExecutionStatus` values the engine exposes: `Pending`, `Running`, `Completed`, `Failed`, `Cancelled`, and `Skipped` (src/ExecutionEngine/Enums/NodeExecutionStatus.cs:12-42). The badge must use color plus iconography so the state remains legible to color-blind users, and it must remain read-only because live execution overlays arrive later from SignalR. This step should not invent intermediate UI statuses or conflate message types with execution statuses. Instead, it creates the visual vocabulary that later telemetry stages reuse. The implementation should also be neutral for history and saving: status changes never touch YAML or sidecar authoring data.

### 4. Depends on
- - Step 3.1.2

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` calls for badge color fidelity and six status snapshots in Step 3.1.3.
- **Status enum** — `src/ExecutionEngine/Enums/NodeExecutionStatus.cs:12-42` defines the exact six states that may appear in the badge.
- **Runtime node chrome** — Step 3.1.2 outputs the shared node-card slots the badge occupies.
- **Quality gate** — `docs/workflow-ui-plan.md:15-77` blocks any extra status value from sneaking into the UI contract.

### 6. Deliverables
- **src/ui/nodes/**
  - `src/ui/nodes/NodeStatusBadge.tsx` (new) — Exports `NodeStatusBadge`; renders icon + tokenized color + accessible text for the six statuses.
  - `src/ui/nodes/statusBadgeTokens.ts` (new) — Exports the status-to-icon and status-to-token table used by the badge and later monitor overlays.
  - `src/ui/nodes/WorkflowNode.tsx` (modified) — Injects `NodeStatusBadge` into the shared chrome without mutating graph DTOs.
- **src/ui/state/**
  - `src/ui/state/executionOverlay.ts` (new) — Exports selectors and helpers that read status overlays without storing them in undo history.
- **src/ui/__tests__/nodes/**
  - `src/ui/__tests__/nodes/NodeStatusBadge.test.tsx` (new) — Snapshot and semantics coverage for every engine status value.
  - `src/ui/__tests__/nodes/WorkflowNode.status.test.tsx` (new) — Ensures the shared node card renders badges and hides them when status is undefined.
- **e2e/**
  - `e2e/node-status-badges.spec.ts` (new) — Playwright toggles status values through a fixture/devtools bridge and checks color/icon output.

### 7. Acceptance criteria
- [ ] Each engine status value renders one deterministic badge state.
  - Evidence: Unit snapshots cover `Pending`, `Running`, `Completed`, `Failed`, `Cancelled`, and `Skipped` individually.
- [ ] Badge styling uses dual encoding (color and icon) rather than color alone.
  - Evidence: Accessibility assertions confirm the icon and visible text both change per status.
- [ ] Undefined status hides the badge rather than showing a fabricated default.
  - Evidence: RTL coverage mounts a node without execution data and asserts the badge slot is empty.
- [ ] Execution overlay data stays outside undo history and save payloads.
  - Evidence: Store tests assert status writes are excluded from history and YAML serialization.
- [ ] Badge text matches engine terminology exactly.
  - Evidence: The rendered labels match the enum member names or approved display text tied directly to those names.
- [ ] The status system is reusable by later telemetry stages without changing the node card API.
  - Evidence: The shared node card accepts a status prop and does not fetch execution state internally.

### 8. Unit tests
1. **NodeStatusBadge_pending_snapshot**
   - Purpose: Locks the pending badge visuals and label.
   - Harness: Vitest + RTL
   - Arrange: Render the badge with status `Pending`.
   - Act: Capture the semantic DOM and token attributes.
   - Assert: The badge shows the pending icon, text, and neutral token.
2. **NodeStatusBadge_running_snapshot**
   - Purpose: Locks the running state.
   - Harness: Vitest + RTL
   - Assertion focus: The badge uses the running token and icon expected by the status table.
3. **NodeStatusBadge_completed_snapshot**
   - Purpose: Locks the completed state.
   - Harness: Vitest + RTL
   - Assertion focus: The badge uses the success token and accessible label for `Completed`.
4. **NodeStatusBadge_failed_snapshot**
   - Purpose: Locks the failed state.
   - Harness: Vitest + RTL
   - Assertion focus: The badge uses the failure token and icon for `Failed`.
5. **NodeStatusBadge_cancelled_snapshot**
   - Purpose: Locks the cancelled state.
   - Harness: Vitest + RTL
   - Assertion focus: The cancelled token/icon pair is distinct from failed and skipped.
6. **NodeStatusBadge_skipped_snapshot**
   - Purpose: Locks the skipped state.
   - Harness: Vitest + RTL
   - Assertion focus: Skipped shows its own token and does not alias completed.
7. **WorkflowNode_hides_badge_when_status_missing**
   - Purpose: Prevents accidental default statuses.
   - Harness: Vitest + RTL
   - Arrange: Render a node card with no execution overlay prop.
   - Act: Query the badge slot.
   - Assert: The slot is empty and no status text is exposed.
8. **executionOverlay_updates_skip_history**
   - Purpose: Protects the history exclusion rule.
   - Harness: Vitest store test
   - Assertion focus: Overlay writes update selectors but do not create new zundo frames.

### 9. Integration tests
1. **Status_badges_toggle_through_fixture_bridge**
   - Purpose: Switch a node through all six statuses in-browser.
   - Harness: Playwright
   - Arrange: Open a fixture that exposes a devtools hook for status injection.
   - Act: Apply each status sequentially to the same node.
   - Assert: The visible badge text, icon marker, and token attributes update on every transition.
2. **Status_badges_are_accessible**
   - Purpose: Check the color-blind accessibility mitigation.
   - Harness: Playwright
   - Arrange: Focus a node with a running badge.
   - Act: Read the accessible name and visible text of the badge.
   - Assert: State is understandable without relying on color inspection.
3. **Status_changes_do_not_affect_save_payload**
   - Purpose: Verify live status remains read-only UI state.
   - Harness: Playwright
   - Arrange: Inject a failing status, then save and reload the workflow.
   - Act: Inspect the saved sidecar/YAML payload and reopen the same workflow.
   - Assert: The badge disappears on reload because no authoring payload captured the transient execution status.

### 10. Golden-file / parity tests
- **Enum parity guard** — Status rendering is anchored to `src/ExecutionEngine/Enums/NodeExecutionStatus.cs:12-42`; parity tests fail if a seventh value appears without badge support.
- **No-save guarantee** — A golden diff of the saved workflow before and after status toggling must be empty for both YAML and `.ui.json` authoring payloads.
- **Badge token snapshot** — A small JSON snapshot of the token table makes color/icon drift reviewable without image diffs alone.

### 11. Edge cases & negative tests
- A node receives an unknown status string from a faulty test harness; the UI logs the issue and renders no badge.
- Two overlay updates arrive rapidly; the last-write-wins selector updates without creating multiple history entries.
- A node is removed while a status update is pending; the overlay store discards the orphaned update.
- Skipped and Cancelled colors appear too similar in the active theme; the icon and label still distinguish them.
- A node card is tiny because of zoom; the badge truncates gracefully but retains an accessible label.

### 12. Observability hooks
- **Zustand action** — Emit `execution.node_status.changed` with `nodeId`, `previousStatus`, `nextStatus`, and `source`.
- **Structured log field set** — Include `runtimeType`, `status`, `badgeToken`, and `badgeIcon` on debug logs that trace badge rendering.
- **SignalR readiness note** — Name the overlay adapter entry point now (`executionOverlay.applyStatus`) so telemetry work can attach without renaming.
- **Accessibility diagnostic** — Expose `data-node-status` on the badge root for Playwright and accessibility snapshots.

### 13. Risks & mitigations
- **Risk:** Color-only design would fail accessibility review.
  - Mitigation: Use icon + text + tokenized color and verify all three in tests.
- **Risk:** Live status state leaks into authoring data.
  - Mitigation: Keep overlay selectors in a dedicated history-excluded slice and diff saved payloads in tests.
- **Risk:** The UI invents display names that drift from engine terminology.
  - Mitigation: Tie labels directly to the engine enum names and keep the mapping table next to the badge tests.

### 14. Evaluator gap-analysis prompts
1. Which file proves the UI knows about exactly six statuses and no more?
2. How do the tests show undefined status hides the badge instead of defaulting to Pending?
3. What evidence shows execution overlay writes do not create undo frames or save diffs?
4. Which status pair is most likely to confuse users visually, and how is that mitigated?
5. Can the evaluator find a Playwright assertion that every status text/icon pair updates in browser?
6. Where would SignalR telemetry plug into this status system later without changing the node card API?

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
- All six badge snapshots are stable and reviewed.
- The save-diff regression test confirms status is entirely transient UI state.

### Step 4 — Minimap and fit-view controls

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Add the navigational affordances that make larger workflows usable before drag-and-drop and connection editing arrive. The minimap should reflect the current graph bounds, and the fit-view control must recenter on all nodes using the same React Flow instance the canvas shell already owns. These controls should be thin wrappers rather than bespoke viewport systems so the later ELK layout stage and validation deep-linking can call the same fit/focus helpers. The control bar must stay history-neutral and sidecar-safe: only the viewport moves. The implementation should also expose stable hooks for Playwright and later toolbar integration.

### 4. Depends on
- - Step 3.1.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` requires a minimap and a fit-view recenter behavior in Step 3.1.4.
- **Canvas shell** — Step 3.1.1 already introduced the React Flow host and viewport-sync helper these controls should reuse.
- **Workflow graph** — `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` defines the node/connection collections that drive bounds calculations.
- **Sidecar boundary** — `docs/workflow-ui-plan.md:78-88` and `docs/workflow-ui-plan.md:97-277` keep viewport state in UI metadata rather than YAML.

### 6. Deliverables
- **src/ui/minimap/**
  - `src/ui/minimap/WorkflowMiniMap.tsx` (new) — Exports `WorkflowMiniMap`; thin wrapper around the React Flow minimap with theme tokens and test ids.
- **src/ui/canvas/**
  - `src/ui/canvas/FitViewButton.tsx` (new) — Exports `FitViewButton`; invokes the shared viewport helper on demand.
  - `src/ui/canvas/useFitWorkflow.ts` (new) — Exports `useFitWorkflow`; computes node bounds and applies fit-view through the current React Flow instance.
  - `src/ui/canvas/WorkflowCanvas.tsx` (modified) — Hosts the minimap and control button slots introduced by this step.
- **src/ui/__tests__/minimap/**
  - `src/ui/__tests__/minimap/WorkflowMiniMap.test.tsx` (new) — RTL coverage for minimap presence and graph-bound updates.
- **src/ui/__tests__/canvas/**
  - `src/ui/__tests__/canvas/useFitWorkflow.test.ts` (new) — Unit coverage for fit-view recenter calculations.
- **e2e/**
  - `e2e/canvas-navigation.spec.ts` (new) — Playwright recenter and minimap smoke tests.

### 7. Acceptance criteria
- [ ] The minimap appears only when the workflow has at least one node.
  - Evidence: RTL tests assert the minimap is hidden for empty graphs and present for populated ones.
- [ ] Fit-view recenters on all visible nodes using shared viewport helpers.
  - Evidence: Unit and Playwright tests check that far-apart nodes end up inside the viewport after clicking fit-view.
- [ ] Viewport updates from fit-view persist to sidecar state but not undo history.
  - Evidence: Store tests assert the viewport slice changes while history depth stays constant.
- [ ] The controls do not mutate node positions or engine DTO fields.
  - Evidence: Saved YAML and node coordinates remain unchanged except for viewport metadata in `.ui.json`.
- [ ] The minimap uses theme tokens and exposes deterministic test ids.
  - Evidence: Both visual tests and DOM assertions read the stable ids and token attributes.
- [ ] Future deep-link and auto-layout code can reuse the same `useFitWorkflow` hook.
  - Evidence: The hook exposes an imperative API and a pure bounds helper for later callers.

### 8. Unit tests
1. **WorkflowMiniMap_hidden_for_empty_graph**
   - Purpose: Avoids empty chrome when there is nothing to navigate.
   - Harness: Vitest + RTL
   - Arrange: Render the canvas with an empty workflow fixture.
   - Act: Query the minimap test id.
   - Assert: No minimap is rendered.
2. **WorkflowMiniMap_renders_for_populated_graph**
   - Purpose: Shows the minimap appears when needed.
   - Harness: Vitest + RTL
   - Assertion focus: A populated fixture renders the minimap container.
3. **useFitWorkflow_calculates_bounds_from_all_nodes**
   - Purpose: Protects the recenter math.
   - Harness: Vitest
   - Assertion focus: The bounds helper spans the min/max coordinates of every node in the fixture.
4. **useFitWorkflow_applies_padding_constant**
   - Purpose: Keeps viewport padding consistent.
   - Harness: Vitest
   - Assertion focus: The hook applies the shared fit padding constant rather than an inline magic number.
5. **FitViewButton_invokes_hook_once**
   - Purpose: Avoids duplicate viewport writes on click.
   - Harness: Vitest + RTL
   - Assertion focus: A click triggers exactly one fit-view invocation.
6. **FitView_preserves_node_positions**
   - Purpose: Ensures viewport changes are non-destructive.
   - Harness: Vitest store test
   - Assertion focus: Node x/y values remain identical before and after fit-view.
7. **FitView_persists_viewport_to_sidecar**
   - Purpose: Checks the sidecar boundary.
   - Harness: Vitest store test
   - Assertion focus: Only the viewport slice changes in the persisted UI state.
8. **Minimap_uses_theme_tokens**
   - Purpose: Avoids hard-coded colors.
   - Harness: Vitest + RTL
   - Assertion focus: Token attributes match the approved canvas theme table.

### 9. Integration tests
1. **Fit_view_recenters_sparse_graph**
   - Purpose: Validate the core acceptance bar in browser.
   - Harness: Playwright
   - Arrange: Open a workflow with nodes placed in opposite corners of the canvas.
   - Act: Click the fit-view button.
   - Assert: Both outermost nodes land inside the viewport with visible padding.
2. **Minimap_tracks_panning**
   - Purpose: Confirm the minimap follows viewport movement.
   - Harness: Playwright
   - Arrange: Open a populated workflow and record the initial minimap viewport box.
   - Act: Pan the canvas using pointer drag.
   - Assert: The minimap viewport box moves to reflect the new camera position.
3. **Fit_view_reload_keeps_updated_viewport**
   - Purpose: Checks persistence for later deep-link work.
   - Harness: Playwright
   - Arrange: Use fit-view, save, and reload the same workflow.
   - Act: Return to the designer.
   - Assert: The viewport is restored to the fit-view coordinates from the sidecar.

### 10. Golden-file / parity tests
- **Sidecar-only viewport diff** — The fit-view action changes only viewport metadata in the sidecar golden; YAML remains byte-identical.
- **Parity carry-forward** — The step keeps the parity suites from `docs/workflow-ui-plan.md:15-77` green because no engine-facing contract changes.
- **Navigation helper snapshot** — A serialized bounds-calculation snapshot can be reviewed when node padding or minimap framing changes.

### 11. Edge cases & negative tests
- A single-node workflow uses fit-view; the hook centers on the node without over-zooming.
- The graph contains collapsed or hidden nodes in a later phase; the helper should fit only visible nodes once visibility exists.
- The minimap mounts before node measurements settle; it re-renders once bounds become available.
- A fit-view click occurs while the graph is empty; the handler exits early without logging an error.
- Viewport persistence fails because sidecar state is unavailable; the control still recenters for the current session and logs a warning.

### 12. Observability hooks
- **Zustand action** — Emit `canvas.fit_view.requested` with `nodeCount`, `source`, and `padding`.
- **Zustand action** — Emit `canvas.fit_view.completed` with the resulting viewport triple and computed bounds.
- **Structured log field set** — Include `minX`, `minY`, `maxX`, `maxY`, and `visibleNodeCount` for fit-view diagnostics.
- **UI trace** — Expose `data-testid="fit-view-button"` and `data-testid="workflow-minimap"` for Playwright traces.

### 13. Risks & mitigations
- **Risk:** Fit-view math differs between jsdom and the browser.
  - Mitigation: Keep the core bounds logic pure and test it separately from the React Flow imperative call.
- **Risk:** Minimap and future toolbar controls compete for viewport ownership.
  - Mitigation: Route all navigation actions through the same viewport helper and constants.
- **Risk:** Users mistake viewport changes for node movement.
  - Mitigation: Persist only viewport state, diff YAML in tests, and keep node coordinates untouched.

### 14. Evaluator gap-analysis prompts
1. Which file owns the shared fit-view logic, and can later deep-link code call it directly?
2. How do the tests prove fit-view does not alter node positions?
3. What evidence shows the minimap is absent for empty workflows?
4. Where is the sidecar-only persistence behavior asserted?
5. Which structured log fields would help debug a fit-view that zooms too far out?
6. Can the evaluator find a Playwright case that demonstrates recentering on far-apart nodes?

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
- The fit-view Playwright test passes on a sparse graph with reproducible coordinates.
- Minimap rendering remains entirely optional and does not affect empty-canvas performance.
