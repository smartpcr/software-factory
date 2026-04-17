# Stage 3.3 — Connections

**Phase:** Canvas, Palette, Connections  
**Stage status:** [ ]  
**Total effort:** 5 d  
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context
Stage 3.3 turns static nodes into a graph editor by materializing the connection contract already defined in the engine. The UI edge model must stay 1:1 with `NodeConnection` and preserve all nine properties on round-trip: `SourceNodeId`, `TargetNodeId`, `TriggerMessageType`, `Condition`, `IsEnabled`, `Metadata`, `Priority`, `SourcePort`, and `TargetPort` (src/ExecutionEngine/Workflow/NodeConnection.cs:21-99). The stage also enforces the port matrix defined in the master plan so authors cannot draw edges the engine would not understand (docs/workflow-ui-plan.md:97-277, docs/workflow-ui-plan.md:705-804).
Message semantics come from the engine's `MessageType` enum, not from arbitrary UI edge kinds. Styling therefore maps directly to `Complete`, `Fail`, `Progress`, `Next`, and `Cancel` (src/ExecutionEngine/Enums/MessageType.cs:12-45); loop and branch behavior is expressed through ports and edge metadata instead of new message names. This stage must also respect the canonical property names `sourcePort` and `targetPort`, because the master plan explicitly retired older `sourcePortId`/`targetPortId` terminology (docs/workflow-ui-plan.md:97-277).
Undo, reroute, and property editing are included here because edges are part of authoring state, unlike execution overlays. Save/load still flows through the existing loader and serializer (`src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149`, `src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212`), while server-side condition evaluation and workflow validation continue to live in the engine (`src/ExecutionEngine/Workflow/NodeConnection.cs:21-99`, `src/ExecutionEngine/Workflow/WorkflowValidator.cs:40-285`).

## Prerequisites
- Phase gates (§0.2) green
- Prior stages completed
- External deps (React Flow v12, zustand, zundo, react-hook-form, zod, shadcn, ELKjs)

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 3.3 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `e2e/connection-creation.spec.ts`
- `e2e/edge-editing.spec.ts`
- `e2e/edge-styling.spec.ts`
- `src/ui/__tests__/edges/EdgePopover.test.tsx`
- `src/ui/__tests__/edges/WorkflowEdge.test.tsx`
- `src/ui/__tests__/edges/edgeCommands.test.ts`
- `src/ui/__tests__/edges/edgeStyleMap.test.ts`
- `src/ui/__tests__/ports/portMatrix.test.ts`
- `src/ui/__tests__/validation/connectionRules.test.ts`
- `src/ui/canvas/WorkflowCanvas.tsx`
- `src/ui/canvas/edgeTypes.ts`
- `src/ui/canvas/useConnectionCreation.ts`
- `src/ui/canvas/useEdgeInteractions.ts`
- `src/ui/edges/EdgePopover.tsx`
- `src/ui/edges/WorkflowEdge.tsx`
- `src/ui/edges/edgeCommands.ts`
- `src/ui/edges/edgeStyleMap.ts`
- `src/ui/ports/portMatrix.ts`
- `src/ui/state/historyPolicy.ts`
- `src/ui/validation/connectionRules.ts`

## Shared fixtures / fakes
- `src/ui/__tests__/fixtures/connections/all-message-types.ts` — one edge per `MessageType` for styling and round-trip tests.
- `src/ui/__tests__/fixtures/connections/port-matrix.ts` — authoritative per-kind in/out port expectations copied from the master plan matrix.
- `src/ui/__tests__/fixtures/connections/edge-popover.ts` — editable edge payloads with metadata passthrough.
- `e2e/fixtures/connections/connection-roundtrip.yaml` + `.ui.json` — browser save/reload target for connection editing tests.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="3.3"`, `phaseId="3"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-3.3.plan-read`, `workflow-ui.stage-3.3.apply`, `workflow-ui.stage-3.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_3_3_started_total`, counter `workflow_ui_stage_3_3_failed_total`, histogram `workflow_ui_stage_3_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 3.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage33` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Keyboard-create a node from the palette, move it, connect it, then undo/redo twice.
- Drag 25 nodes quickly and confirm selection/position state stays stable after a refresh.
- Open the canvas with reduced-motion enabled and confirm animations degrade gracefully.

---

## Step 3.3.1 — Edge creation with port-matrix enforcement

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Implement connection creation rules that accept only source and target handles allowed by the authoritative port matrix. The rule engine should validate source kind, target kind, source port, target port, and any self-connection restrictions before an edge is committed. Successful connections must immediately create a full nine-field edge object with canonical defaults for `triggerMessageType`, `isEnabled`, and `priority`, while rejected attempts should surface inline feedback. The same rule engine should be callable from tests, drag gestures, and future server-side preflight checks. This keeps connection semantics centralized instead of split between React Flow callbacks and ad hoc component logic.

### 4. Depends on
- Step 2.4.6
- Step 3.2.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` requires port-matrix enforcement and invalid-edge rejection for Step 3.3.1.
- **Connection contract** — `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` defines the fields every accepted edge must carry.
- **Message types** — `src/ExecutionEngine/Enums/MessageType.cs:12-45` limits connection message semantics to five values.
- **Port matrix** — `docs/workflow-ui-plan.md:97-277` lines 264-277 define the allowed output ports per runtime family and the single `in` target port.
- **Runtime taxonomy** — `src/ExecutionEngine/Enums/RuntimeType.cs:12-78` constrains the kinds the matrix must cover.

### 6. Deliverables
- **src/ui/ports/**
  - `src/ui/ports/portMatrix.ts` (new) — Exports `portMatrix` and `getPortsForRuntimeType`; materializes the master-plan matrix as pure data.
- **src/ui/validation/**
  - `src/ui/validation/connectionRules.ts` (new) — Exports `isValidConnection` and `buildConnectionDto`; applies matrix rules and canonical defaults.
- **src/ui/canvas/**
  - `src/ui/canvas/useConnectionCreation.ts` (new) — Exports the React Flow callback adapter that calls the shared rule engine.
  - `src/ui/canvas/WorkflowCanvas.tsx` (modified) — Wires `isValidConnection` and `onConnect` into the canvas shell.
- **src/ui/__tests__/validation/**
  - `src/ui/__tests__/validation/connectionRules.test.ts` (new) — Matrix coverage for valid and invalid source/target/port combinations.
- **src/ui/__tests__/ports/**
  - `src/ui/__tests__/ports/portMatrix.test.ts` (new) — Guards the pure-data port catalog against drift.
- **e2e/**
  - `e2e/connection-creation.spec.ts` (new) — Playwright invalid-edge rejection and valid-edge creation coverage.

### 7. Acceptance criteria
- [ ] Only matrix-approved handle combinations create edges.
  - Evidence: Pure rule tests and Playwright both cover valid and invalid combinations.
- [ ] Accepted edges are created with canonical defaults (`Complete`, `true`, `0`).
  - Evidence: Unit tests inspect the built connection DTO immediately after creation.
- [ ] Rejected edges surface inline feedback and leave graph state unchanged.
  - Evidence: Playwright attempts invalid connections and asserts the error plus unchanged edge count.
- [ ] Every runtime family in the port matrix is covered by tests.
  - Evidence: Matrix tests enumerate all runtime types and expected ports rather than sampling a subset.
- [ ] Edges use `sourcePort` and `targetPort` field names only.
  - Evidence: DTO tests and serialized output confirm no legacy `*PortId` fields appear.
- [ ] Self-connections and impossible targets are blocked before commit.
  - Evidence: Negative cases explicitly cover same-node and non-existent-handle attempts.

### 8. Unit tests
1. **portMatrix_covers_every_runtime_type**
   - Purpose: Ensures no kind is missing from the matrix.
   - Harness: Vitest
   - Arrange: Import the runtime contract and the matrix keys.
   - Act: Compare coverage sets.
   - Assert: Every runtime type has an entry and no extra kinds exist.
2. **isValidConnection_accepts_standard_task_complete_to_in**
   - Purpose: Covers the common success path.
   - Harness: Vitest
   - Arrange: Build source/target handles for a task node using `Complete -> in`.
   - Act: Run `isValidConnection`.
   - Assert: The rule returns valid.
3. **isValidConnection_rejects_source_port_not_in_matrix**
   - Purpose: Blocks invented output handles.
   - Harness: Vitest
   - Assertion focus: A made-up source port fails validation with a descriptive reason.
4. **isValidConnection_rejects_target_port_not_in_matrix**
   - Purpose: Blocks invented input handles.
   - Harness: Vitest
   - Assertion focus: Anything other than `in` fails for current v1 targets.
5. **isValidConnection_rejects_self_connection**
   - Purpose: Prevents trivial graph mistakes.
   - Harness: Vitest
   - Assertion focus: Connecting a node to itself is invalid unless a future explicit exception is added.
6. **buildConnectionDto_populates_canonical_defaults**
   - Purpose: Ensures accepted edges start with engine defaults.
   - Harness: Vitest
   - Arrange: Build a DTO from a valid connect gesture.
   - Act: Inspect the returned object.
   - Assert: The DTO uses `Complete`, `true`, and `0` plus the chosen source/target handles.
7. **buildConnectionDto_uses_canonical_port_field_names**
   - Purpose: Guards the wire contract.
   - Harness: Vitest
   - Assertion focus: The built object contains `sourcePort`/`targetPort` and no legacy aliases.
8. **matrix_tests_cover_switch_and_loop_special_ports**
   - Purpose: Covers the more exotic matrix rows.
   - Harness: Vitest
   - Assertion focus: Switch case ports and loop `Next` ports appear exactly where the plan says they should.

### 9. Integration tests
1. **Valid_edge_creation_commits_connection**
   - Purpose: Exercise a successful canvas connect gesture.
   - Harness: Playwright
   - Arrange: Open a workflow with two compatible nodes and expose their handles.
   - Act: Drag from a valid source port to a valid target port.
   - Assert: A new edge appears and persists after save/reload.
2. **Invalid_edge_rejection_surfaces_inline_feedback**
   - Purpose: Cover the master-plan invalid-edge scenario.
   - Harness: Playwright
   - Arrange: Attempt a handle pair that violates the matrix.
   - Act: Complete the drag gesture.
   - Assert: No edge is created and the UI shows the rejection message.
3. **Self_connection_is_blocked**
   - Purpose: Prevent easy user error.
   - Harness: Playwright
   - Arrange: Attempt to connect a node to its own input.
   - Act: Release on the same node.
   - Assert: The attempt is rejected and edge count stays unchanged.

### 10. Golden-file / parity tests
- **Connection DTO parity** — The connection object created by the UI must still match `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` exactly in serialized snapshots.
- **Matrix snapshot** — A snapshot of the pure-data `portMatrix` table makes drift against the master plan obvious in diff review.
- **Parity carry-forward** — Enum and DTO parity suites from `docs/workflow-ui-plan.md:15-77` remain green because the UI only materializes already-defined contracts.

### 11. Edge cases & negative tests
- Dragging from a hidden or disabled handle returns a validation failure rather than a partial edge.
- A switch case handle label changes in the registry; matrix tests catch the resulting mismatch.
- The target node is deleted mid-drag; the connection adapter drops the gesture safely.
- A loop node tries to use `Next` on a non-loop kind; validation rejects it immediately.
- A corrupted saved edge references a missing port; the canvas loads it as invalid and surfaces diagnostics later rather than crashing.

### 12. Observability hooks
- **Zustand action** — Emit `connection.create_attempted` with `sourceNodeId`, `targetNodeId`, `sourcePort`, `targetPort`, and `valid`.
- **Zustand action** — Emit `connection.create_rejected` with `reason` for invalid attempts.
- **Structured log field set** — Track `triggerMessageType`, `priority`, and `isEnabled` defaulting when a DTO is built.
- **UI trace** — Expose `data-port-name` and `data-runtime-type` on handles for Playwright and debug captures.

### 13. Risks & mitigations
- **Risk:** Matrix rules drift from the authoritative plan.
  - Mitigation: Keep the matrix in one pure-data file and snapshot it in tests.
- **Risk:** React Flow callbacks duplicate validation logic.
  - Mitigation: Route every create attempt through `connectionRules.ts` only.
- **Risk:** Invalid gestures mutate state before failing.
  - Mitigation: Build DTOs only after the rule engine returns success.

### 14. Evaluator gap-analysis prompts
1. How is the master-plan port matrix materialized as code, and where is it tested exhaustively?
2. Which test proves the UI uses `sourcePort`/`targetPort` rather than deprecated aliases?
3. What evidence shows invalid connections leave the graph unchanged?
4. How are switch and loop-specific ports covered, rather than just task-to-task edges?
5. Which logs would identify whether a rejection came from source-port, target-port, or self-edge rules?
6. Can the evaluator point to a Playwright case for self-connection rejection?

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
- The invalid-edge Playwright scenario passes and leaves edge count unchanged.
- The matrix snapshot is reviewed against the authoritative plan table.


## Step 3.3.2 — Edge styling by message type

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Render edges with a message-type-driven visual system that mirrors the semantics already present in the engine contract. `Complete` uses the standard smooth solid path, `Next` is dashed, `Fail` is red, `Progress` is amber, and `Cancel` is grey, matching the master-plan styling table and the five `MessageType` values from the engine. Styling must be data-driven so the visual representation changes automatically when an edge's `triggerMessageType` changes during load or editing. The underlying edge shape remains a React Flow smoothstep edge as pinned by the user; ELK later affects node placement only. This step must not invent extra edge types disconnected from `MessageType`.

### 4. Depends on
- Step 3.3.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` and `docs/workflow-ui-plan.md:97-277` define the style mapping by `MessageType`.
- **Message enum** — `src/ExecutionEngine/Enums/MessageType.cs:12-45` enumerates the only five message values that may drive styling.
- **Connection DTO** — `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` binds styling to the `TriggerMessageType` field already on every edge.

### 6. Deliverables
- **src/ui/edges/**
  - `src/ui/edges/edgeStyleMap.ts` (new) — Exports `edgeStyleMap` and `getEdgeStyleForMessageType`; maps `MessageType` to tokens and line styles.
  - `src/ui/edges/WorkflowEdge.tsx` (new) — Exports `WorkflowEdge`; renders a smoothstep edge using the style map and shared labels.
- **src/ui/canvas/**
  - `src/ui/canvas/edgeTypes.ts` (new) — Exports the React Flow `edgeTypes` map for styled workflow edges.
  - `src/ui/canvas/WorkflowCanvas.tsx` (modified) — Registers `WorkflowEdge` for canvas rendering.
- **src/ui/__tests__/edges/**
  - `src/ui/__tests__/edges/edgeStyleMap.test.ts` (new) — Snapshot coverage for all five message types.
  - `src/ui/__tests__/edges/WorkflowEdge.test.tsx` (new) — RTL coverage for style props and labels.
- **e2e/**
  - `e2e/edge-styling.spec.ts` (new) — Playwright visual baseline for one edge per `MessageType`.

### 7. Acceptance criteria
- [ ] Every `MessageType` value renders a distinct, deterministic style.
  - Evidence: Snapshot tests enumerate all five values and compare stroke, dash, and token output.
- [ ] Edges remain smoothstep paths regardless of message type.
  - Evidence: Component tests inspect the rendered path type and reject alternate routing implementations.
- [ ] Style comes from `triggerMessageType`, not from ad hoc UI-only flags.
  - Evidence: Serialized edge fixtures change style only when the message-type field changes.
- [ ] Visual tokens are theme-safe and use the shared token table.
  - Evidence: Tests assert token references rather than raw colors in edge components.
- [ ] Loaded edges with existing message types re-render correctly after reload.
  - Evidence: Playwright opens a fixture with all message types and confirms the expected styles appear on first paint.
- [ ] No new persisted field is introduced solely for styling.
  - Evidence: Edge serialization remains the same nine-field DTO; only render code changes.

### 8. Unit tests
1. **edgeStyleMap_complete_snapshot**
   - Purpose: Locks the default styling.
   - Harness: Vitest
   - Arrange: Query style output for `Complete`.
   - Act: Serialize the returned style object.
   - Assert: The snapshot shows a solid path with the expected token.
2. **edgeStyleMap_fail_snapshot**
   - Purpose: Locks fail styling.
   - Harness: Vitest
   - Assertion focus: Fail uses the red/failure token and no unexpected dash pattern.
3. **edgeStyleMap_progress_snapshot**
   - Purpose: Locks progress styling.
   - Harness: Vitest
   - Assertion focus: Progress uses the amber/progress token.
4. **edgeStyleMap_next_snapshot**
   - Purpose: Locks loop iteration styling.
   - Harness: Vitest
   - Assertion focus: Next uses a dashed stroke and the expected neutral token.
5. **edgeStyleMap_cancel_snapshot**
   - Purpose: Locks cancellation styling.
   - Harness: Vitest
   - Assertion focus: Cancel uses the grey token and distinct label.
6. **WorkflowEdge_renders_smoothstep_path**
   - Purpose: Enforces the user pin on edge routing.
   - Harness: Vitest + RTL
   - Assertion focus: The rendered edge uses the smoothstep component/path generator.
7. **WorkflowEdge_reads_triggerMessageType_from_data**
   - Purpose: Keeps styling bound to the DTO.
   - Harness: Vitest + RTL
   - Assertion focus: Changing only `triggerMessageType` changes only style-related props.
8. **edgeStyleMap_uses_theme_tokens_only**
   - Purpose: Avoids hard-coded styling drift.
   - Harness: Vitest
   - Assertion focus: All message-style entries reference token names rather than raw literals.

### 9. Integration tests
1. **All_message_types_visual_baseline**
   - Purpose: Render one edge per message type and capture the approved baseline.
   - Harness: Playwright
   - Arrange: Open the all-message-types fixture.
   - Act: Capture the canvas screenshot and semantic attributes of each edge.
   - Assert: Every edge matches the expected style by message type.
2. **Reload_preserves_edge_styles**
   - Purpose: Ensure style is derived from persisted data.
   - Harness: Playwright
   - Arrange: Load a saved fixture containing all message types.
   - Act: Reload the page and re-open the workflow.
   - Assert: Edge styles match the same message-type mapping after reload.
3. **Message_type_change_updates_style_live**
   - Purpose: Future-proofs the style binding for edge editing.
   - Harness: Playwright devtools bridge
   - Arrange: Mutate an edge trigger type in test state.
   - Act: Observe the rendered edge immediately after the state change.
   - Assert: The visual style updates without re-creating the edge.

### 10. Golden-file / parity tests
- **Message-style snapshot** — Keep a per-message-type JSON snapshot of style tokens and dash settings to complement the visual baseline.
- **No DTO drift** — Golden serialization of the same connection set before and after styling work remains identical.
- **Enum parity carry-forward** — The UI still relies solely on the five message types from `src/ExecutionEngine/Enums/MessageType.cs:12-45` and the parity suite in `docs/workflow-ui-plan.md:15-77`.

### 11. Edge cases & negative tests
- An edge loads with an unknown message type because of a corrupt fixture; the renderer falls back to a safe default and logs the issue.
- Theme tokens change globally; snapshots catch unexpected style drift without needing to inspect raw SVG.
- Two overlapping edges of different types remain visually distinguishable by dash and color.
- A disabled edge later keeps the same message-style tokens but can layer opacity from edit state.
- Browser SVG rendering differences slightly alter dash appearance; the visual baseline and JSON snapshot together catch regressions.

### 12. Observability hooks
- **Structured log field set** — Emit `edge.rendered` with `edgeId`, `triggerMessageType`, `strokeToken`, and `dashPattern` in debug mode.
- **Visual test metadata** — Name screenshots with the message type so CI failures identify the broken style immediately.
- **UI trace** — Expose `data-message-type` on each edge path for Playwright and diagnostics.
- **Performance mark** — Track `edge.style.resolve_ms` if style resolution ever becomes dynamic or token-based at runtime.

### 13. Risks & mitigations
- **Risk:** Color tokens clash with the active theme and reduce legibility.
  - Mitigation: Keep style tokens in one map and verify both visual diff plus semantic token snapshots.
- **Risk:** Teams accidentally add a custom edge type unrelated to `MessageType`.
  - Mitigation: Centralize rendering on `WorkflowEdge` and make the style map keyed strictly by the enum.
- **Risk:** A future layout step attempts to replace smoothstep routing.
  - Mitigation: Document and test that routing remains smoothstep; ELK only moves nodes.

### 14. Evaluator gap-analysis prompts
1. Where is the one-to-one mapping between `MessageType` and edge style defined?
2. How do tests prove style changes come from `triggerMessageType` rather than extra UI flags?
3. What evidence shows edges are still smoothstep paths?
4. Which snapshot would catch a theme-token regression even if the visual diff were subtle?
5. How does the browser baseline demonstrate all five message types at once?
6. What log field would you inspect if a `Fail` edge rendered as neutral grey?

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
- All five message-type snapshots are stable and reviewed.
- Playwright visual baselines show the correct edge styling without altering serialized data.


## Step 3.3.3 — Edge rerouting, deletion, undo, and property popover

### 1. Status
[ ]

### 2. Effort
2 d

### 3. Goal
Finish the connection authoring surface by supporting reroute, delete, undo, and a focused property popover for the editable `NodeConnection` fields. In v1 the popover may edit only `condition`, `isEnabled`, and `priority`; `metadata` must round-trip untouched, `sourcePort` and `targetPort` stay read-only because they are matrix-derived, and `triggerMessageType` is communicated by styling rather than edited in the popover. Undo must restore both deleted and rerouted edges using the zundo history policy already pinned for authoring state. The popover should expose the real connection data rather than a UI clone so save/reload coverage proves the nine-field DTO survives intact. Validation remains intentionally light here: only non-empty checks for `condition`, while expression evaluation stays server-side as the plan notes.

### 4. Depends on
- Step 3.3.2

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:705-804` defines the v1 editable fields, undo requirement, and round-trip expectation for Step 3.3.3.
- **Connection contract** — `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` defines all nine properties that must survive edge edits and undo.
- **Condition evaluation behavior** — `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` shows conditions are evaluated server-side via `ConditionEvaluator`; the UI should not attempt semantic parsing.
- **Validation boundary** — `src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149` and `src/ExecutionEngine/Workflow/WorkflowValidator.cs:40-285` keep authoritative validation on the server side.

### 6. Deliverables
- **src/ui/edges/**
  - `src/ui/edges/EdgePopover.tsx` (new) — Exports `EdgePopover`; shows editable `condition`, `isEnabled`, and `priority` plus read-only ports and metadata summary.
  - `src/ui/edges/edgeCommands.ts` (new) — Exports `rerouteEdge`, `deleteEdge`, and `updateEdgeProperties` history-aware graph commands.
- **src/ui/state/**
  - `src/ui/state/historyPolicy.ts` (modified) — Whitelists edge create/update/delete/reroute operations for undo/redo.
- **src/ui/canvas/**
  - `src/ui/canvas/useEdgeInteractions.ts` (new) — Exports event handlers for selecting, rerouting, deleting, and opening the popover.
- **src/ui/__tests__/edges/**
  - `src/ui/__tests__/edges/edgeCommands.test.ts` (new) — Unit coverage for undo-safe edge commands and nine-field preservation.
  - `src/ui/__tests__/edges/EdgePopover.test.tsx` (new) — RTL tests for editable and read-only popover fields.
- **e2e/**
  - `e2e/edge-editing.spec.ts` (new) — Playwright coverage for reroute, delete, undo, and property persistence.

### 7. Acceptance criteria
- [ ] Deleting an edge and pressing Ctrl+Z restores it with all nine fields intact.
  - Evidence: Unit tests and Playwright both assert full DTO restoration after undo.
- [ ] Rerouting an edge updates only source/target handle references while preserving untouched fields.
  - Evidence: Command tests compare pre/post DTOs field by field.
- [ ] The popover edits only `condition`, `isEnabled`, and `priority` in v1.
  - Evidence: RTL tests confirm message type is read-only-by-style and metadata/ports are not editable.
- [ ] `metadata` round-trips untouched even though the UI does not edit it.
  - Evidence: A dedicated preservation test loads metadata, edits priority, saves, and asserts metadata equality.
- [ ] `condition` validation is limited to non-empty checks; parsing stays server-side.
  - Evidence: Tests reject blank strings but do not attempt client-side expression interpretation.
- [ ] Save/reload preserves edited connection fields through canonical YAML without sidecar leakage.
  - Evidence: Playwright edits a connection, saves, reloads, and confirms the same values rehydrate from YAML.

### 8. Unit tests
1. **deleteEdge_then_undo_restores_full_connection**
   - Purpose: Implements the step’s core undo acceptance bar.
   - Harness: Vitest store test
   - Arrange: Start with an edge carrying all nine connection fields, including metadata and ports.
   - Act: Delete the edge and invoke undo.
   - Assert: The restored edge matches the original object field-for-field.
2. **rerouteEdge_preserves_non_endpoint_fields**
   - Purpose: Protects metadata and editable properties during reroute.
   - Harness: Vitest store test
   - Assertion focus: Only `sourceNodeId`, `targetNodeId`, `sourcePort`, or `targetPort` change during reroute.
3. **updateEdgeProperties_updates_condition_isEnabled_priority_only**
   - Purpose: Keeps the popover constrained to v1 fields.
   - Harness: Vitest store test
   - Assertion focus: Attempts to write message type or metadata through the popover command are ignored or rejected.
4. **EdgePopover_shows_read_only_ports_and_metadata_summary**
   - Purpose: Clarifies non-editable fields to the user.
   - Harness: Vitest + RTL
   - Assertion focus: Source/target port values and metadata summary are visible but not editable.
5. **EdgePopover_rejects_blank_condition**
   - Purpose: Implements the only client-side condition validation promised in the plan.
   - Harness: Vitest + RTL
   - Assertion focus: Submitting an empty condition shows an inline error and does not commit the change.
6. **historyPolicy_whitelists_edge_mutations**
   - Purpose: Ensures undo coverage extends to all edge authoring operations.
   - Harness: Vitest
   - Assertion focus: Create, delete, reroute, and property-edit commands all register history frames; execution overlays do not.
7. **metadata_preserved_when_priority_changes**
   - Purpose: Protects passthrough behavior for opaque metadata.
   - Harness: Vitest store test
   - Arrange: Update only priority on an edge that contains mixed-scalar metadata.
   - Act: Serialize the edge after the update.
   - Assert: Metadata keys and scalar types are unchanged.
8. **edge_roundtrip_keeps_all_nine_fields**
   - Purpose: Implements the explicit master-plan round-trip fact.
   - Harness: Vitest serialization test
   - Assertion focus: Every field survives save/load, including untouched metadata and read-only ports.

### 9. Integration tests
1. **Delete_and_undo_edge**
   - Purpose: Exercise destructive authoring plus recovery.
   - Harness: Playwright
   - Arrange: Open a workflow with at least one edge selected.
   - Act: Delete the edge, verify it disappears, then press Ctrl+Z.
   - Assert: The edge returns with the same style and popover values.
2. **Edit_condition_isEnabled_priority_and_reload**
   - Purpose: Cover the property popover persistence path.
   - Harness: Playwright
   - Arrange: Open the edge popover for a known connection.
   - Act: Edit `condition`, toggle `isEnabled`, set `priority`, save, and reload.
   - Assert: The popover reopens with the same values and metadata remains intact.
3. **Reroute_edge_preserves_metadata**
   - Purpose: Verify endpoint changes do not erase opaque fields.
   - Harness: Playwright
   - Arrange: Start from an edge that contains metadata.
   - Act: Reroute the edge to another valid target and save.
   - Assert: The connection endpoints update, but metadata still round-trips unchanged.

### 10. Golden-file / parity tests
- **Nine-field round-trip** — The connection golden must show that all nine `NodeConnection` fields survive delete/undo and property edits.
- **Metadata passthrough diff** — A focused golden diff demonstrates that changing `priority` does not mutate metadata keys or scalar types.
- **No sidecar leakage** — The edge property popover writes to YAML-backed connection fields only; `.ui.json` changes only when geometry moves.

### 11. Edge cases & negative tests
- The user reroutes an edge onto an invalid handle; the operation is rejected and the original edge remains intact.
- Undo is pressed after a non-history action such as live status update; the edge history remains unaffected.
- Metadata contains mixed scalar values (`string`, `bool`, `number`); editing `priority` preserves exact value types.
- A blank `condition` is submitted; the UI blocks the save but leaves the previous non-empty value untouched.
- An edge is deleted while its popover is open; the popover closes and undo can still restore the full edge.

### 12. Observability hooks
- **Zustand action** — Emit `edge.deleted`, `edge.rerouted`, and `edge.properties_updated` with `edgeId` and changed field names.
- **Structured log field set** — Track `conditionLength`, `isEnabled`, `priority`, `sourcePort`, and `targetPort` on edge-edit events.
- **History trace** — Record `history.edge_undo_restored` with the restored edge id and connection hash.
- **UI trace** — Expose stable test ids for the popover fields and delete/undo controls.

### 13. Risks & mitigations
- **Risk:** Undo accidentally restores only edge geometry but not properties.
  - Mitigation: Snapshot full connection objects before mutation and assert deep equality after undo.
- **Risk:** The popover drifts beyond the v1 field budget and becomes a second editor surface.
  - Mitigation: Keep editable fields explicit and treat message type, metadata, and ports as read-only.
- **Risk:** Client-side condition parsing diverges from server behavior.
  - Mitigation: Restrict client validation to non-empty checks and rely on engine validation later.

### 14. Evaluator gap-analysis prompts
1. How does the implementation prove all nine connection fields survive delete/undo cycles?
2. Which fields are intentionally editable in v1, and where is that constraint tested?
3. What evidence shows metadata remains untouched when other fields change?
4. How is undo policy limited to authoring actions and kept separate from execution overlays?
5. Which browser test demonstrates rerouting without metadata loss?
6. What log fields would you inspect when a user claims Ctrl+Z restored the edge but lost its condition?

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
- The undo regression test proves full edge restoration, not just reappearance.
- Popover persistence tests pass against a YAML-backed save/reload cycle.

## Appendix A — Port matrix review notes

| Runtime family | Expected output handles | Common rejection reasons |
|---|---|---|
| Task-family kinds | `Complete`, `Fail`, `Progress`, `Cancel` | made-up source port, self-edge, missing target |
| IfElse | `Complete`, `Fail`, `Cancel`, labelled else via `Next` | connecting from non-existent labelled branch |
| Switch | `Complete`, `Fail`, `Cancel`, one case-labelled output per case key | missing case label, unknown case key |
| ForEach / While | `Next`, `Complete`, `Fail`, `Cancel`, `Progress` | using `Next` on a non-loop target or wrong source kind |
| Subflow / Container | `Complete`, `Fail`, `Progress`, `Cancel` | wrong handle name, disabled or hidden source handle |

## Appendix B — Connection invariants

- Edge DTOs must stay 1:1 with `NodeConnection`.
- `sourcePort` and `targetPort` are the only canonical port property names.
- `triggerMessageType` drives styling; no extra UI-only edge-type field should exist.
- Invalid connect gestures must fail before any state mutation happens.
- Delete, reroute, and popover edits are undoable authoring actions.
- Execution overlays never participate in edge undo history.
- `metadata` is opaque and must survive edits untouched.
- `condition` validation is intentionally shallow on the client.
- Default connection values should omit canonically when saved.
- Sidecar files may capture geometry or viewport, but not engine connection fields.
- Playwright must cover both positive and negative connect gestures using real pointer geometry.
- Edge reroute must preserve untouched fields field-for-field.

## Appendix C — Evaluator spot-check sequence

1. Inspect `portMatrix.ts` and compare it to the master-plan table.
2. Verify `connectionRules.ts` is the single source of connection validation logic.
3. Confirm edge creation builds canonical defaults for message type, enabled flag, and priority.
4. Check that invalid-edge Playwright tests leave edge count unchanged.
5. Review `edgeStyleMap.ts` and ensure only five engine `MessageType` values are represented.
6. Confirm `WorkflowEdge` stays smoothstep-based despite style differences.
7. Open `EdgePopover` and verify only `condition`, `isEnabled`, and `priority` are editable.
8. Check tests proving `metadata` survives reroute and delete/undo unchanged.
9. Confirm undo policy includes edge create/delete/reroute/update but excludes execution overlays.
10. Validate that saved YAML preserves connection fields while sidecar-only UI state stays separate.
11. Review logs for `connection.create_rejected` and `edge.properties_updated` when debugging.
12. Make sure any snapshot or golden output labels fixture id and message type clearly.

## Appendix D — Connection debugging cues

- If valid edges cannot be created, compare handle names to the matrix appendix before debugging React Flow events.
- If an edge style looks wrong, inspect `triggerMessageType` first and the style map second.
- If metadata disappears after reroute, inspect edge command merge logic rather than the popover UI.
- If Ctrl+Z restores geometry but not fields, inspect history snapshots for full DTO preservation.
- If blank conditions are accepted, inspect popover validation rather than engine validation.
- If default omission changes in saved YAML, inspect connection canonicalization tests before editing snapshots.
- If self-edges are unexpectedly allowed, inspect the pure rule engine rather than canvas callbacks.
- If Playwright geometry tests flake, verify stable test ids on handles and edge paths.

## Appendix E — Minimal manual smoke set

- Create one valid edge and save/reload it.
- Attempt one invalid edge and confirm rejection.
- Inspect one edge per message type for correct styling.
- Edit `condition`, `isEnabled`, and `priority` on an existing edge.
- Delete an edge and restore it with Ctrl+Z.
- Reroute an edge and confirm metadata survives.
- Save/reload after edge edits and compare the popover values.
- Review edge action logs for create, reject, reroute, delete, and undo.

## Appendix F — One-line audit questions

- Does every edge operation preserve all nine connection fields?
- Is `triggerMessageType` the sole source of edge styling?
- Are invalid handles rejected before any graph mutation occurs?
