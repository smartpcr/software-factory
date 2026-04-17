# Stage 2.3 — Control-flow kinds

**Phase:** Phase 2 — Node Kinds, Schemas, Round-Trip
**Stage status:** [ ]
**Total effort:** 7.5 d
**Step count:** 4

## Steps

- [ ] **Step 1**: `IfElse` kind
- [ ] **Step 2**: `Switch` kind
- [ ] **Step 3**: `ForEach` kind
- [ ] **Step 4**: `While` kind

---
## Context

Stage 2.3 implements the 4 control-flow runtime kinds — nodes whose purpose is to direct message flow rather than execute work. The defining architectural rule, reaffirmed by this stage, is: **branch targets are expressed as edges only**. There is NO `trueBranch`/`falseBranch`/`defaultBranch` property on any control-flow DTO; instead a `NodeConnection` carries a `TriggerMessageType` (`Complete` / `Next` / `Fail` / `Cancel` / `Progress` — from `src/ExecutionEngine/Enums/MessageType.cs:9-35`) and optionally a `Condition` string (evaluated against incoming message payload). This means IfElse, Switch, ForEach, While are ALL represented by a simple extended-base DTO + their condition/expression fields; the graph topology expresses the branching.

Coverage:

| Step | Runtime kind | Engine source file | Extra DTO props | Required outgoing edge shape |
|------|--------------|--------------------|-----------------|------------------------------|
| 2.3.1 | `IfElse` | `IfElseNodeDefinition.cs` | `Condition: string` | 2 outgoing edges w/ conditions (true/false arms expressed as `Condition` strings) |
| 2.3.2 | `Switch` | `SwitchNodeDefinition.cs` | `Expression: string`, `Cases: Dictionary<string,string>` | N outgoing edges whose `Condition` matches a case key |
| 2.3.3a | `ForEach` | `ForEachNodeDefinition.cs` | `CollectionExpression: string`, `ItemVariableName="item"` | Body edge + exit edge |
| 2.3.3b | `While` | `WhileNodeDefinition.cs` | `ConditionExpression: string`, `MaxIterations: int = 1000` | Body edge + exit edge |

**Cross-cutting facts defended by this stage:**
- Engine has no "branch" properties on the node. Stage 2.4.6 port matrix surfaces the edge-shape constraints to the UI, but the DTO itself stays flat.
- `Switch.Cases` is declared `Dictionary<string, string>` (per `SwitchNodeDefinition.cs:32`). Keys are case values; values are **documentation-only** descriptions (engine does NOT use them for routing). Routing still comes from edge `Condition`. The DTO preserves both but the UI informs the author of the semantic split.
- `ForEach.ItemVariableName` defaults to `"item"`; this is the variable accessible inside the child scope. Empty string is invalid (engine `Validate()`).
- `While.MaxIterations` defaults to `1000`. Zero or negative is invalid.

## Prerequisites
- Stages 2.1, 2.2 complete.
- `NodeConnection` DTO not yet needed (Stage 2.4) — but tests here use it in mock form to demonstrate edge-based branching.

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `src/WorkflowUI/src/schemas/node-ifelse.schema.ts` (2.3.1)
- `src/WorkflowUI/src/schemas/node-switch.schema.ts` (2.3.2)
- `src/WorkflowUI/src/schemas/node-foreach.schema.ts` (2.3.3a)
- `src/WorkflowUI/src/schemas/node-while.schema.ts` (2.3.3b)
- `src/WorkflowUI/src/schemas/node-union.ts` (modified each step)
- `src/WorkflowUI/src/schemas/__tests__/node-<kind>.schema.test.ts` per step.
- Fixtures in `src/WorkflowUI/src/__fixtures__/`: `ifelse-*.yaml`, `switch-*.yaml`, `foreach-*.yaml`, `while-*.yaml` (multiple per kind for positive and negative branch-shape coverage).
- `src/WorkflowUi.ContractTests/ControlFlowRoundTripTests.cs`.
- `src/WorkflowUi.ContractTests/BranchAsEdgesInvariantTests.cs` (new, owned by this stage) — asserts no control-flow DTO carries `trueBranch`/`defaultCase`/etc.

## Shared fixtures / fakes
- `ControlFlowRoundTripTests` is a `[Theory]` parameterised over every positive fixture.
- `BranchAsEdgesInvariantTests` enumerates every Zod schema in `node-*.schema.ts` and reflects for the forbidden property names — a belt-and-braces defence against future drift.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="2.3"`, `phaseId="2"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-2.3.plan-read`, `workflow-ui.stage-2.3.apply`, `workflow-ui.stage-2.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_2_3_started_total`, counter `workflow_ui_stage_2_3_failed_total`, histogram `workflow_ui_stage_2_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| schema layer | DTO shape diverges from engine property set | Zod/parity tests fail | update schema from cited engine file and refresh fixtures |
| round-trip API | server canonicalization or validation drift | parse/serialize identity test fails | route through `WorkflowLoader` / `WorkflowSerializer` only |
| sandbox / sidecar store | path traversal or orphan handling bug | endpoint negative tests or warning logs fire | tighten sandbox and preserve orphan metadata explicitly |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| Zod parse (200-node workflow) | 30 ms | 100 ms |
| YAML serialize / validate (200 nodes) | 150 ms | 600 ms |
| sidecar / schema fixture save | 75 ms | 250 ms |

## Security callouts
- Reject path traversal and absolute-path escapes in workflow/sidecar endpoints.
- Treat node names, descriptions, and expressions as untrusted strings; do not render as HTML without escaping.
- Do not introduce a second YAML parser or custom loader; server routes must remain thin wrappers over engine APIs.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 2.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage23` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

### Step 1 — `IfElse` kind

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 2.2.4b

### Goal
Implement `IfElseNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/IfElseNodeDefinition.cs:1-32`. One extra property: `Condition: string`. The UI will render this as a condition editor, but branching is expressed by two outgoing `NodeConnection`s whose `Condition` strings are `"true"` and `"false"` (or richer expressions). This step also owns the authoritative statement of the "branch as edges" rule and the BranchAsEdgesInvariantTests file.

### Depends on
2.2.4b.

### Inputs
- `IfElseNodeDefinition.cs`, `NodeConnection.cs`, `MessageType.cs`, Stage 2.1–2.2 schemas.

### Deliverables
- `src/WorkflowUI/src/schemas/node-ifelse.schema.ts`:
  ```ts
  export const ifElseNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('IfElse'),
    condition: z.string().min(1),
  }).strict();
  export type IfElseNode = z.infer<typeof ifElseNodeSchema>;
  ```
- Fixtures:
  - `ifelse-minimal.yaml` — one IfElse node with two outgoing edges, conditions `"true"` and `"false"`, targeting two Noop nodes.
  - `ifelse-complex-condition.yaml` — condition `"item.count > 0 && item.status == 'ready'"`.
  - `ifelse-empty-condition.yaml` (negative) — engine rejects.
- `src/WorkflowUi.ContractTests/BranchAsEdgesInvariantTests.cs` — reflects all 13 node schemas and asserts none contain `trueBranch`/`falseBranch`/`defaultCase`/`cases.route`/etc.
- Update `node-union.ts` to use `ifElseNodeSchema`.

### Acceptance criteria
- [ ] DTO has exactly 1 extra property: `condition`.
- [ ] Condition is non-empty string; empty fails.
- [ ] `BranchAsEdgesInvariantTests_NoDtoContains_trueBranch` passes.
- [ ] Engine round-trip preserves condition text verbatim (including spaces and unusual characters).
- [ ] `.strict()` rejects `trueBranch` / `falseBranch` if a migration script ever tries to add them — belt-and-braces.
- [ ] Two-edge fixture round-trips; edge `Condition` values (`"true"`, `"false"`) preserved verbatim.

### Unit tests
1. `IfElseSchema_ValidCondition_Parses`
2. `IfElseSchema_EmptyCondition_Fails`
3. `IfElseSchema_ConditionWithQuotes_Parses`
4. `IfElseSchema_ConditionWithNewlines_Parses`
5. `IfElseSchema_ExtraKey_Strict_Fails`
6. `IfElseSchema_NoTrueBranchKey_Strict_Fails` (explicit)
7. `IfElseSchema_ConditionNumber_Fails`
8. `IfElseRoundTrip_Minimal_ConditionPreserved`
9. `IfElseRoundTrip_ComplexCondition_CharsPreserved`
10. `IfElseNode_Parity_1Extra`
11. `BranchAsEdgesInvariantTests_NoDtoContains_trueBranch` (owns shared test file)
12. `BranchAsEdgesInvariantTests_NoDtoContains_defaultCase`

A/A/A for the branch-as-edges invariant:

- `BranchAsEdgesInvariantTests_NoDtoContains_trueBranch`
  - Arrange: enumerate `ExecutionEngine.Nodes.Definitions.*NodeDefinition` via reflection.
  - Act: for each type, read all public instance properties.
  - Assert: none of the property names match `trueBranch`, `falseBranch`, `defaultCase`, `route`, `elseNode`, `thenNode` (case-insensitive).

### Integration tests
- `ControlFlowRoundTripTests[IfElse_minimal]`, `ControlFlowRoundTripTests[IfElse_complex]`.

### Golden-file / parity tests
- **Golden:** `ifelse-minimal.yaml`, `ifelse-complex-condition.yaml`. Diff contract: canonicalised byte-match.
- **Negative golden:** `ifelse-empty-condition.yaml` engine `Load(validateOnLoad: true)` throws per engine `Validate()`.
- **Parity:** reflection on `IfElseNodeDefinition` → 1 extra property (`Condition`). `BranchAsEdgesInvariantTests` confirms no branch-target properties.

### Edge cases & negative tests
1. Condition `"true"` (the constant) — legal; common pattern for always-take edges.
2. Condition with backticks or unusual Unicode — preserved.
3. Condition longer than 10 KB — parses (no limit); Phase 5 UI will warn.
4. Condition containing a YAML-reserved character (`:`, `#`) — YAML block scalar used by serializer to preserve.
5. Condition referring to an undeclared variable — not the DTO's concern; runtime error.
6. Fixture with 3 outgoing edges from IfElse (invalid semantics) — engine accepts at load; Phase 3 linter warns. DTO does not reject.
7. Fixture with 0 outgoing edges — engine accepts at load; runtime-reachable issue. DTO does not reject.
8. Fixture with duplicate `Condition` values on both edges — DTO accepts; Phase 3 linter flags ambiguous branching.

### Observability hooks
- Category `WorkflowUi.ContractTests.ControlFlow.IfElse`.
- `BranchAsEdgesInvariantTests` logs category `WorkflowUi.ContractTests.Invariants.BranchAsEdges` with the list of properties checked — useful for diagnosing future engine additions.

### Risks & mitigations
- Engine could later add a shortcut `thenNodeId`/`elseNodeId` to IfElse — `BranchAsEdgesInvariantTests` fails first, forcing a design review before the UI accepts it.
- Condition parsing by the UI (Phase 5) is non-trivial — out of scope here; stage just preserves the string.

### Evaluator gap-analysis prompts
1. Is `condition` the ONLY extra property, matching engine's 1 extra?
2. Is there an explicit invariant test asserting NO control-flow DTO carries branch-target properties?
3. Is empty-condition rejected by Zod matching engine's `Validate()`?
4. Do fixtures demonstrate branching via edges, NOT via node-level properties?
5. Are complex condition strings (quotes, newlines, Unicode) round-trip-preserved?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings.

---

### Step 2 — `Switch` kind

**Status:** [ ]
**Effort:** 2.5 d
**Depends on:** 2.3.1

### Goal
Implement `SwitchNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/SwitchNodeDefinition.cs:1-42`:
- `Expression: string` (line 27, non-empty required)
- `Cases: Dictionary<string, string>` (line 32, default `new()`)

Critical semantic: `Cases` keys are CASE VALUES (match targets), `Cases` values are DESCRIPTIONS ONLY (not edge targets). Routing is driven by outgoing edges whose `Condition` matches a case key. The DTO preserves the dictionary so the UI can show the author's intent, but documents that routing uses edges.

### Depends on
2.3.1.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/SwitchNodeDefinition.cs:12-39` — authoritative `Expression` / `Cases` property surface.
- `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` — edge-routing fields (`Condition`, `TriggerMessageType`) that remain outside the DTO.
- `src/WorkflowUI/src/schemas/node-base.schema.ts` from Step 2.1.1 — shared base fields and discriminator plumbing.

### Deliverables
- `src/WorkflowUI/src/schemas/node-switch.schema.ts`:
  ```ts
  export const switchNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('Switch'),
    expression: z.string().min(1),
    cases: z.record(z.string(), z.string()).default({}),
  }).strict();
  ```
- Fixtures:
  - `switch-3-cases.yaml` — expression `"status"`, cases `{ready: 'Continue', waiting: 'Hold', error: 'Fail'}`, and 3 outgoing edges with matching conditions.
  - `switch-default-case.yaml` — one outgoing edge with `Condition: "*"` representing default; `Cases` dictionary omits the default entry.
  - `switch-empty-cases.yaml` — `cases: {}` (no routing documented); engine accepts, but Phase 3 linter warns.
- Tests.
- Update `node-union.ts`.

### Acceptance criteria
- [ ] DTO has exactly 2 extras (`expression`, `cases`).
- [ ] `cases` is `Record<string, string>` — both sides strings.
- [ ] Empty expression fails.
- [ ] Empty `cases` object parses (draft).
- [ ] Non-string case value fails (e.g., `{ready: 123}`).
- [ ] Non-string case key impossible (JSON/YAML keys are always strings).
- [ ] Round-trip preserves case iteration order (engine uses `Dictionary<string,string>` — .NET dictionaries preserve insertion order since .NET Core 2.0+).
- [ ] `BranchAsEdgesInvariantTests` still passes (no `defaultCase` field).

### Unit tests
1. `SwitchSchema_3Cases_Parses`
2. `SwitchSchema_EmptyExpression_Fails`
3. `SwitchSchema_EmptyCases_Parses`
4. `SwitchSchema_NonStringCaseValue_Fails`
5. `SwitchSchema_CaseKeyWithSpaces_Parses`
6. `SwitchSchema_CaseKeyDuplicates_ImpossibleByJsonSpec` (documentary)
7. `SwitchSchema_ExtraKey_Strict_Fails`
8. `SwitchSchema_DefaultCaseField_NotAllowed` (negative — asserts no `defaultCase` prop)
9. `SwitchRoundTrip_3Cases_OrderPreserved`
10. `SwitchRoundTrip_UnicodeExpression_Preserved`
11. `SwitchNode_Parity_2Extras`

A/A/A for case-order preservation:

- `SwitchRoundTrip_3Cases_OrderPreserved`
  - Arrange: fixture with `cases: {ready: ..., waiting: ..., error: ...}` (insertion order).
  - Act: engine `FromYaml` → Zod parse → engine `ToYaml`.
  - Assert: resulting YAML lists keys in same order (`ready`, `waiting`, `error`).

### Integration tests
- 3 `ControlFlowRoundTripTests` rows.

### Golden-file / parity tests
- **Golden:** all 3 fixtures; canonicalised byte-match.
- **Parity:** reflection on `SwitchNodeDefinition` yields 2 extras.
- **Order invariant:** `SwitchRoundTrip_3Cases_OrderPreserved`.
- **Invariant:** `BranchAsEdgesInvariantTests_NoDtoContains_defaultCase`.

### Edge cases & negative tests
1. Case key containing a colon (`"ready:phase1"`) — YAML quoted string preserves.
2. Case value containing newlines — block scalar preserves.
3. 100-case switch — no performance issue; parse < 10 ms asserted.
4. Case key that's a YAML boolean literal (`"true"`, `"yes"`, `"null"`) — YAML 1.2 + YamlDotNet preserves when quoted; unquoted would coerce to bool/null — fixture tests use explicit quoting.
5. Case value containing a YAML anchor marker (`&foo`) — preserved as string because value side is always quoted.
6. Edge `Condition` not matching any case key — DTO allows; runtime behaviour is to skip. Phase 3 linter warns.
7. Case key `""` (empty string) — allowed; Phase 3 linter warns.
8. Case dictionary with no edges — legal; no routing happens.

### Observability hooks
- `WorkflowUi.ContractTests.ControlFlow.Switch` logs case count.
- Debug log when case count > 50 (usability signal).

### Risks & mitigations
- Semantic confusion between `cases` (documentation dictionary) and edge `Condition` (routing) — every fixture has comments explaining the split, and Phase 5 UI will render them together but clearly labeled.
- Dictionary-order drift in future engine changes — the round-trip test guards against this.

### Evaluator gap-analysis prompts
1. Does the schema represent `cases` as `Record<string, string>` matching engine `Dictionary<string, string>`?
2. Does a test confirm case insertion order is preserved through round-trip?
3. Is the routing-via-edges documented in the schema file header?
4. Does `BranchAsEdgesInvariantTests` verify no `defaultCase` property exists on SwitchNode?
5. Does an empty `cases` dictionary parse as a draft?
6. Is the parity count pinned at 2 extras?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings.

---

### Step 3 — `ForEach` kind

**Status:** [ ]
**Effort:** 1.5 d
**Depends on:** 2.3.2

### Goal
Implement `ForEachNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:1-34`:
- `CollectionExpression: string` (line 27, non-empty required)
- `ItemVariableName: string` (line 32, default `"item"`)

The body of the loop is a child subgraph reached via an outgoing edge with `TriggerMessageType: Next` (each item); the exit is a separate edge with `TriggerMessageType: Complete`.

### Depends on
2.3.2.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28` — authoritative `CollectionExpression` and `ItemVariableName` defaults.
- `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` — message-type routing fields used by loop body/exit edges.
- `src/ExecutionEngine/Enums/MessageType.cs:13-20` — legal `Next` / `Complete` message values for fixture assertions.

### Deliverables
- `src/WorkflowUI/src/schemas/node-foreach.schema.ts`:
  ```ts
  export const forEachNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('ForEach'),
    collectionExpression: z.string().min(1),
    itemVariableName: z.string().min(1).default('item'),
  }).strict();
  ```
- Fixtures:
  - `foreach-minimal.yaml` — simple collection loop; body edge uses `Next`; exit edge uses `Complete`.
  - `foreach-custom-var.yaml` — `itemVariableName: "record"`.
  - `foreach-empty-var.yaml` (negative) — engine rejects per `Validate()`.
  - `foreach-empty-collection.yaml` (negative).
- Tests pinning the message-type contract for body vs exit edges.

### Acceptance criteria
- [ ] DTO has 2 extras matching engine.
- [ ] `itemVariableName` defaults to `"item"` matching engine default.
- [ ] Empty collection expression fails.
- [ ] Empty item-var fails.
- [ ] Body edge uses `TriggerMessageType: Next`; exit edge uses `Complete`. This is a fixture-level test (Phase 3 port matrix formalises as a lint rule).
- [ ] Round-trip preserves both fields.
- [ ] `BranchAsEdgesInvariantTests` unchanged.

### Unit tests
1. `ForEachSchema_Minimal_Parses`
2. `ForEachSchema_DefaultItemVarApplied`
3. `ForEachSchema_EmptyCollection_Fails`
4. `ForEachSchema_EmptyItemVar_Fails`
5. `ForEachSchema_CustomItemVar_Parses`
6. `ForEachSchema_ExtraKey_Strict_Fails`
7. `ForEachSchema_ItemVar_WithSpaces_Parses` (legal; uncommon)
8. `ForEachRoundTrip_CustomVar_Preserved`
9. `ForEachRoundTrip_EdgeMessageTypes_NextAndComplete` (integration)
10. `ForEachNode_Parity_2Extras`

A/A/A for the default-applied:

- `ForEachSchema_DefaultItemVarApplied`
  - Arrange: `{ runtimeType: 'ForEach', nodeId: 'n', collectionExpression: 'data.items', joinType: 'Any', priority: 'Normal', maxConcurrentExecutions: 0 }` (itemVariableName omitted).
  - Act: parse.
  - Assert: `parsed.itemVariableName === 'item'`.

### Integration tests
- `ControlFlowRoundTripTests[ForEach_minimal]`, `ControlFlowRoundTripTests[ForEach_custom_var]`.

### Golden-file / parity tests
- **Golden:** 2 positive + 2 negative fixtures (negatives assert engine throws).
- **Parity:** reflection yields `CollectionExpression`, `ItemVariableName` only — 2 extras.
- **Edge-type contract:** `ForEachRoundTrip_EdgeMessageTypes_NextAndComplete` asserts the fixture's body edge has `TriggerMessageType: Next` and exit has `Complete`. Formalised later in Phase 3 port matrix.

### Edge cases & negative tests
1. Collection expression `"[]"` (empty array literal) — parses; runtime loops zero times.
2. Collection expression referring to a dictionary — runtime semantics depend on executor; DTO doesn't care.
3. Item variable containing a dot (`"item.foo"`) — legal; runtime may reject. DTO accepts.
4. Item variable matching a reserved keyword (`"for"`) — accepted by DTO.
5. Fixture with a body edge and NO exit edge — engine loads; runtime hangs after collection drains. Phase 3 warns.
6. Fixture with exit edge but no body — legal draft; runtime no-ops. Phase 3 warns.
7. Very large `MaxIterations`-style intent expressed (no such field here; punted to `While`) — not applicable.

### Observability hooks
- Category `WorkflowUi.ContractTests.ControlFlow.ForEach`.

### Risks & mitigations
- Confusion between ForEach iteration semantics and edge routing — fixture comments and Phase 5 UI clearly distinguish "body" from "exit".
- Default `"item"` drift — pinned by `ForEachSchema_DefaultItemVarApplied`.

### Evaluator gap-analysis prompts
1. Does `itemVariableName` default to `"item"` matching engine `ForEachNodeDefinition.cs:32`?
2. Is empty-collection rejected matching engine `Validate()`?
3. Is empty-itemVar rejected?
4. Does a fixture demonstrate the edge message-type contract (Next for body, Complete for exit)?
5. Is the parity count pinned at 2?
6. Does the schema forbid extra keys via `.strict()`?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings.

---

### Step 4 — `While` kind

**Status:** [ ]
**Effort:** 1.5 d
**Depends on:** 2.3.3a

### Goal
Implement `WhileNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:1-34`:
- `ConditionExpression: string` (line 27, non-empty required)
- `MaxIterations: int` (line 32, default `1000`, must be > 0)

Body is an outgoing edge with `TriggerMessageType: Next`; exit is `Complete`. Safety-net `MaxIterations` guards against runaway loops; DTO validates `>=1, <=1_000_000` at parse.

### Depends on
2.3.3a.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35` — authoritative `ConditionExpression` and `MaxIterations` defaults.
- `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` — outgoing edge fields referenced by the body/exit fixtures.
- `src/ExecutionEngine/Enums/MessageType.cs:13-20` — canonical `Next` / `Complete` message names used in tests.

### Deliverables
- `src/WorkflowUI/src/schemas/node-while.schema.ts`:
  ```ts
  export const whileNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('While'),
    conditionExpression: z.string().min(1),
    maxIterations: z.number().int().min(1).max(1_000_000).default(1000),
  }).strict();
  ```
- Fixtures: `while-minimal.yaml`, `while-high-max-iter.yaml`, `while-zero-iter.yaml` (negative), `while-empty-cond.yaml` (negative).
- Tests.

### Acceptance criteria
- [ ] 2 extras matching engine.
- [ ] `maxIterations` default `1000`.
- [ ] `maxIterations: 0` fails.
- [ ] `maxIterations: 1_000_001` fails (DTO-imposed safety ceiling; engine itself accepts larger but we cap for UI sanity).
- [ ] `maxIterations: 1` parses.
- [ ] Empty condition fails.
- [ ] Round-trip preserves `maxIterations`.
- [ ] Body edge `Next`, exit edge `Complete` — fixture-level.

### Unit tests
1. `WhileSchema_Minimal_Parses`
2. `WhileSchema_DefaultMaxIterations_Is1000`
3. `WhileSchema_ZeroMaxIter_Fails`
4. `WhileSchema_NegativeMaxIter_Fails`
5. `WhileSchema_OverCeiling_Fails` (> 1_000_000)
6. `WhileSchema_EmptyCondition_Fails`
7. `WhileSchema_ExtraKey_Strict_Fails`
8. `WhileSchema_MaxIterAsString_Fails`
9. `WhileRoundTrip_Minimal_Preserved`
10. `WhileRoundTrip_HighMaxIter_Preserved` (e.g., 500_000)
11. `WhileNode_Parity_2Extras`

A/A/A for the ceiling:

- `WhileSchema_OverCeiling_Fails`
  - Arrange: fixture with `maxIterations: 1_000_001`.
  - Act: `whileNodeSchema.safeParse(fixture)`.
  - Assert: `success === false`, issue path `maxIterations`.

### Integration tests
- 2 positive `ControlFlowRoundTripTests` rows + negative load assertions.

### Golden-file / parity tests
- **Golden:** 2 positive fixtures; 2 negative fixtures (engine rejects).
- **Parity:** reflection yields 2 extras.
- **Edge-type contract:** same body=Next, exit=Complete as ForEach.

### Edge cases & negative tests
1. Condition `"true"` (infinite-except-for-maxIter) — legal; safety net trips at `maxIterations`.
2. Condition depending on a counter variable incremented in body — legal; DTO not concerned.
3. `maxIterations: 1` — single-shot loop; legal.
4. Very large `maxIterations` near ceiling — parses; runtime may still OOM; out of DTO scope.
5. Body with no exit edge — legal draft; Phase 3 warns.
6. Condition evaluating to non-boolean — not the DTO's concern.
7. `maxIterations: 1.5` — fails (`z.number().int()`).
8. `maxIterations` as JSON string (`"100"`) — fails; YAML may emit as string; canonicaliser converts back to int before compare.

### Observability hooks
- Category `WorkflowUi.ContractTests.ControlFlow.While`.
- Debug log when `maxIterations > 100_000` (usability signal).

### Risks & mitigations
- Engine future-add of timeout field — parity test catches.
- UI-imposed ceiling vs engine ceiling — documented in schema header; if engine raises its own cap we revisit.
- Integer vs float YAML parsing — canonicaliser and `z.number().int()` together prevent drift.

### Evaluator gap-analysis prompts
1. Does `maxIterations` default to `1000` matching engine?
2. Is 0 and negative rejected?
3. Is there a DTO-level sanity ceiling (1_000_000) with a justification?
4. Is empty condition rejected?
5. Does a fixture demonstrate the body=Next, exit=Complete edge contract?
6. Is the parity count pinned at 2?
7. Is `BranchAsEdgesInvariantTests` still green with all 4 control-flow kinds live?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, Stage 2.1.2 placeholders for all 4 control-flow kinds replaced by real schemas, and `BranchAsEdgesInvariantTests` green.
