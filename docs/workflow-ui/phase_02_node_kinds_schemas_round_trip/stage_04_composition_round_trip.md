# Stage 2.4 — Composition, round-trip, port matrix

**Phase:** Phase 2 — Node Kinds, Schemas, Round-Trip
**Stage status:** [ ]
**Total effort:** 11 d
**Step count:** 6

## Steps

- [ ] **Step 1**: `Subflow` kind
- [ ] **Step 2**: `Timer` kind
- [ ] **Step 3**: `Container` kind + `NodeConnection` DTO
- [ ] **Step 4**: YAML round-trip server endpoints
- [ ] **Step 5**: Sidecar (`.workflow-ui.json`) I/O
- [ ] **Step 6**: Port matrix

---
## Context

Stage 2.4 is the **largest and last** Phase 2 stage. It finishes the 13-kind coverage with the three "composition" kinds (`Subflow`, `Timer`, `Container`), wraps the engine `WorkflowLoader`/`WorkflowSerializer` in HTTP endpoints for YAML round-trip, defines the sidecar metadata schema (non-semantic coordinates/colours/notes), and formalises the port-matrix rules that the UI will enforce via lint in Phase 3.

Coverage:

| Step | Runtime kind / concern | Engine source | Notes |
|------|------------------------|---------------|-------|
| 2.4.1 | `Subflow` | `SubflowNodeDefinition.cs` | Has external-file OR inline workflow variants. Default timeout `00:05:00`. |
| 2.4.2 | `Timer` | `TimerNodeDefinition.cs` | `Schedule: string` (CRON or ISO 8601 duration). |
| 2.4.3 | `Container` | `ContainerNodeDefinition.cs` | Nested `ChildNodes?`, `ChildConnections?`, `ExecutionMode=Sequential`. |
| 2.4.4 | Server YAML round-trip API | `WorkflowLoader.cs`, `WorkflowSerializer.cs` | Wraps engine loader+serializer in `/api/workflows/*`. |
| 2.4.5 | Sidecar I/O | (UI-owned metadata) | `.workflow-ui.json` sibling file; no engine analog. |
| 2.4.6 | Port matrix | `MessageType.cs`, `NodeConnection.cs` | Formalises which edge types are legal per kind. |

**Cross-cutting facts defended by this stage:**
- `SubflowNodeDefinition` has BOTH `WorkflowFilePath: string` (line 27, default `""`) AND `WorkflowDefinition?: WorkflowDefinition` (line 32, nullable). Engine validation enforces exactly one.
- `ContainerNodeDefinition.ChildConnections` is `List<NodeConnection>?` (line 39) — connections LIVE on the container, not globally. The top-level workflow has its own `Connections`.
- Sidecar file is OPTIONAL; engine-only workflows MUST load cleanly without it. When present, it augments UI rendering but never influences engine behaviour.
- Port matrix is about which `TriggerMessageType` values are semantically allowed on outgoing edges per source-node kind; the engine itself doesn't enforce this, but the UI and Phase 3 lint will.

## Prerequisites
- Stages 2.1, 2.2, 2.3 complete.
- Server host from Stage 1.1 available for endpoint wiring (Step 2.4.4).

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

Client schemas
- `client/src/schemas/node-subflow.schema.ts` (2.4.1)
- `client/src/schemas/node-timer.schema.ts` (2.4.2)
- `client/src/schemas/node-container.schema.ts` (2.4.3)
- `client/src/schemas/node-connection.schema.ts` (new, 2.4.3 introduces — finalised in 2.4.6)
- `client/src/schemas/sidecar.schema.ts` (2.4.5)
- `client/src/schemas/port-matrix.ts` (2.4.6)
- `client/src/schemas/node-union.ts` (modified; final shape after 2.4.3).

Server
- `src/WorkflowUi.Server/Endpoints/WorkflowRoundTripEndpoints.cs` (2.4.4)
- `src/WorkflowUi.Server/Endpoints/SidecarEndpoints.cs` (2.4.5)

Fixtures (under `client/src/__fixtures__/`)
- Subflow: `subflow-external.yaml`, `subflow-inline.yaml`, `subflow-both-rejected.yaml` (negative).
- Timer: `timer-cron.yaml`, `timer-duration.yaml`, `timer-empty-schedule.yaml` (negative).
- Container: `container-sequential.yaml`, `container-parallel.yaml`, `container-nested.yaml`.
- Round-trip: `roundtrip-all-13-kinds.yaml` (the full integration fixture).
- Sidecar: `sidecar-basic.json`, `sidecar-missing-node.json`.

Server parity tests
- `src/WorkflowUi.ContractTests/CompositionRoundTripTests.cs`
- `src/WorkflowUi.ContractTests/YamlEndpointTests.cs`
- `src/WorkflowUi.ContractTests/SidecarEndpointTests.cs`
- `src/WorkflowUi.ContractTests/PortMatrixTests.cs`

## Shared fixtures / fakes
- `roundtrip-all-13-kinds.yaml` is the canonical "full-coverage" workflow: 13 nodes (one per RuntimeType) wired in a realistic topology. Used by 2.4.4 round-trip test AND as a smoke check in Phase 3+.
- `PortMatrixTests` is theory-driven over a table of `(sourceRuntimeType, messageType, expected)` rows so new engine kinds/message types fail fast.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="2.4"`, `phaseId="2"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-2.4.plan-read`, `workflow-ui.stage-2.4.apply`, `workflow-ui.stage-2.4.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_2_4_started_total`, counter `workflow_ui_stage_2_4_failed_total`, histogram `workflow_ui_stage_2_4_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 2.4 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage24` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

### Step 1 — `Subflow` kind

**Status:** [ ]
**Effort:** 3 d
**Depends on:** 2.3.3b

### Goal
Implement `SubflowNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:1-72`. Six extra properties:
- `WorkflowFilePath: string` (line 27, default `""`) — external-file mode.
- `WorkflowDefinition?: WorkflowDefinition` (line 32, nullable) — inline mode.
- `InputMappings: Dictionary<string, string>` (line 39, default `new()`).
- `OutputMappings: Dictionary<string, string>` (line 46, default `new()`).
- `Timeout: TimeSpan` (line 53, default `TimeSpan.FromMinutes(5)`).
- `SkipValidation: bool` (line 60, default `false`).

Engine `Validate()` enforces exactly one of `WorkflowFilePath` / `WorkflowDefinition`. Two full golden fixtures (external and inline) are required. This is the most complex kind; allow 3 days.

### Depends on
2.3.3b.

### Inputs
- `SubflowNodeDefinition.cs`.
- `WorkflowDefinition` class (referenced from `src/ExecutionEngine/Workflow/`).
- Zod schemas for nested workflows (this step introduces a mutually-recursive pattern).

### Deliverables
- `client/src/schemas/node-subflow.schema.ts`:
  ```ts
  // Forward declaration to break recursion — the full workflow schema references nodeDtoSchema.
  export const subflowNodeSchema: z.ZodType<SubflowNode> = z.lazy(() =>
    nodeBaseSchema.extend({
      runtimeType: z.literal('Subflow'),
      workflowFilePath: z.string().default(''),
      workflowDefinition: workflowDefinitionSchema.nullable().optional(),
      inputMappings: z.record(z.string(), z.string()).default({}),
      outputMappings: z.record(z.string(), z.string()).default({}),
      timeout: z.string().default('00:05:00'),
      skipValidation: z.boolean().default(false),
    }).strict().superRefine((val, ctx) => {
      const hasPath = (val.workflowFilePath ?? '') !== '';
      const hasInline = val.workflowDefinition != null;
      if (hasPath === hasInline) {
        ctx.addIssue({ code: 'custom', message: 'Exactly one of WorkflowFilePath or WorkflowDefinition must be set' });
      }
    })
  );
  ```
- Fixtures:
  - `subflow-external.yaml` — references `subflow-child.yaml` via `workflowFilePath`.
  - `subflow-child.yaml` — tiny child workflow used by external variant.
  - `subflow-inline.yaml` — embeds a child workflow inline.
  - `subflow-both-rejected.yaml` (negative) — both path and inline set; engine throws.
  - `subflow-neither-rejected.yaml` (negative).
  - `subflow-with-mappings.yaml` — non-empty input/output mappings.
- `CompositionRoundTripTests` rows.
- Update `node-union.ts`.

### Acceptance criteria
- [ ] All 6 extras present in schema.
- [ ] Inline mode: `workflowDefinition` is a valid nested workflow (recursive schema).
- [ ] External mode: `workflowFilePath` is a non-empty string; `workflowDefinition` is null.
- [ ] Exactly-one-of validation catches both-set and neither-set.
- [ ] `timeout` default `"00:05:00"` matches engine `TimeSpan.FromMinutes(5)`.
- [ ] `skipValidation` default `false`.
- [ ] `inputMappings` / `outputMappings` preserve insertion order.
- [ ] Round-trip byte-matches canonicalised YAML for BOTH fixtures.
- [ ] Parity reflection yields 6 extras.

### Unit tests
1. `SubflowSchema_External_Parses`
2. `SubflowSchema_Inline_Parses`
3. `SubflowSchema_BothSet_Fails`
4. `SubflowSchema_NeitherSet_Fails`
5. `SubflowSchema_DefaultTimeout`
6. `SubflowSchema_DefaultSkipValidation_False`
7. `SubflowSchema_EmptyMappings_Parse`
8. `SubflowSchema_NonStringMappingValue_Fails`
9. `SubflowSchema_ExtraKey_Strict_Fails`
10. `SubflowSchema_Timeout_InvalidFormat_Fails`
11. `SubflowRoundTrip_External_Match`
12. `SubflowRoundTrip_Inline_Match`
13. `SubflowRoundTrip_Mappings_OrderPreserved`
14. `SubflowNode_Parity_6Extras`
15. `SubflowSchema_Inline_NestedSubflow_Parses` (recursion test)

A/A/A for the recursion:

- `SubflowSchema_Inline_NestedSubflow_Parses`
  - Arrange: fixture with inline workflow containing another Subflow node with its own inline workflow (depth 2).
  - Act: parse.
  - Assert: both levels parse; no infinite recursion; `z.lazy` resolves.

### Integration tests
- 4 `CompositionRoundTripTests` rows (external, inline, mappings, nested).
- Negative-path tests for engine `Validate()` behaviour.

### Golden-file / parity tests
- **Golden:** 3 positive fixtures (external, inline, mappings). Canonicalised YAML byte-match.
- **Negative golden:** 2 fixtures proving engine throws on both-set / neither-set.
- **Recursion depth:** a depth-3 fixture parses and round-trips.
- **Parity:** 6 extras.

### Edge cases & negative tests
1. `workflowFilePath` with a file that doesn't exist on disk — DTO accepts (engine `SkipValidation: true` tolerates too).
2. `workflowFilePath` and `workflowDefinition` set simultaneously — fails.
3. `timeout: "00:00:00"` — zero-timeout, engine rejects at runtime, DTO accepts.
4. `timeout: "-00:01:00"` — negative; DTO refuses via refine (engine throws).
5. `inputMappings` with 1000 entries — parses; no length cap.
6. Circular subflow reference (fixture A → B → A via `workflowFilePath`) — DTO parses individually; engine detects at runtime; Phase 3 linter flags.
7. Inline `workflowDefinition` containing a node with the same `NodeId` as the parent — legal (separate namespace); test asserts.
8. `skipValidation: true` combined with invalid inline workflow — DTO still validates inline workflow (DTO validation is separate from engine `SkipValidation`).

### Observability hooks
- Category `WorkflowUi.ContractTests.Composition.Subflow` — logs recursion depth and mapping count.

### Risks & mitigations
- Recursive schema performance — Zod `z.lazy` has measurable overhead; a size-100 parse benchmark confirms < 20 ms.
- Infinite recursion via cycle (A embeds A inline) — impossible in YAML (must serialise finitely), but a runtime cycle via `workflowFilePath` is possible; Phase 3 lint.
- Timeout format drift (`hh:mm:ss` vs ISO 8601 `PT5M`) — document both; prefer `hh:mm:ss` to match YamlDotNet default.

### Evaluator gap-analysis prompts
1. Are all 6 extras present, correctly typed, with correct defaults?
2. Does the exactly-one-of refine fire in BOTH directions?
3. Does a depth-2+ fixture prove recursion via `z.lazy` works?
4. Does the round-trip preserve mapping insertion order?
5. Is the parity count pinned at 6?
6. Does a cycle-detection concern (A→B→A) get documented (even if deferred)?
7. Is `timeout` format explicitly restricted to `hh:mm:ss`?

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

### Step 2 — `Timer` kind

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 2.4.1

### Goal
Implement `TimerNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/TimerNodeDefinition.cs:1-26`. Single extra property: `Schedule: string` (line 21, non-empty). Engine accepts CRON expressions OR ISO-8601 duration strings; DTO validates with a lightweight refine that matches either pattern.

### Depends on
2.4.1.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/TimerNodeDefinition.cs:12-24` — authoritative `Schedule` surface for timer nodes.
- `src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212` — canonical YAML emission path used by timer round-trip tests.
- `client/src/schemas/node-base.schema.ts` from Step 2.1.1 — shared discriminator/base-field rules.

### Deliverables
- `client/src/schemas/node-timer.schema.ts`:
  ```ts
  const CRON_REGEX = /^(\S+\s+){4,5}\S+$/;           // 5 or 6 fields
  const DURATION_REGEX = /^P(?=[^T])?(\d+Y)?(\d+M)?(\d+D)?(T(\d+H)?(\d+M)?(\d+S)?)?$|^\d{2}:\d{2}:\d{2}$/;
  export const timerNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('Timer'),
    schedule: z.string().min(1).refine(
      v => CRON_REGEX.test(v) || DURATION_REGEX.test(v),
      { message: 'schedule must be CRON or ISO-8601/hh:mm:ss duration' }
    ),
  }).strict();
  ```
- Fixtures: `timer-cron-5field.yaml`, `timer-cron-6field.yaml` (with seconds), `timer-duration-iso.yaml`, `timer-duration-hms.yaml`, `timer-empty-schedule.yaml` (negative), `timer-invalid-schedule.yaml` (negative).

### Acceptance criteria
- [ ] `schedule` non-empty.
- [ ] CRON 5- and 6-field formats both parse.
- [ ] ISO 8601 duration (`PT5M`, `P1D`) parses.
- [ ] `hh:mm:ss` format parses.
- [ ] Obviously invalid strings (`not-a-schedule`) fail.
- [ ] 1 extra matching engine.

### Unit tests
1. `TimerSchema_Cron5Field_Parses`
2. `TimerSchema_Cron6Field_Parses`
3. `TimerSchema_IsoDuration_Parses`
4. `TimerSchema_HmsDuration_Parses`
5. `TimerSchema_Empty_Fails`
6. `TimerSchema_JibberishSchedule_Fails`
7. `TimerSchema_ExtraKey_Strict_Fails`
8. `TimerRoundTrip_Cron_Preserved`
9. `TimerRoundTrip_Duration_Preserved`
10. `TimerNode_Parity_1Extra`

A/A/A for the flexible regex:

- `TimerSchema_Cron6Field_Parses`
  - Arrange: `{ ..., schedule: '*/10 * * * * *' }` (6-field with seconds).
  - Act: parse.
  - Assert: success.

### Integration tests
- 2 positive `CompositionRoundTripTests` rows.

### Golden-file / parity tests
- **Golden:** 4 positive schedules round-trip preserving the raw string.
- **Parity:** 1 extra.

### Edge cases & negative tests
1. Schedule with 4-field CRON — rejected (needs 5 or 6).
2. Schedule `"* * * * ?"` (Quartz-style with `?`) — may or may not match regex; document that engine uses NCrontab which accepts the subset in our regex; Quartz-specific characters are rejected with a clear error.
3. Leading/trailing whitespace in schedule — trimmed by the refine? No — preserve verbatim; engine may reject. Decision: reject via a second refine that `v.trim() === v`.
4. Unicode whitespace in CRON — rejected.
5. ISO duration with zero months `"P0M"` — legal syntactically; engine may reject; DTO accepts.
6. Very long duration (`P100Y`) — DTO accepts; runtime may overflow.
7. `hh:mm:ss` with > 24 hours (`"25:00:00"`) — legal per .NET TimeSpan; DTO accepts.

### Observability hooks
- Category `WorkflowUi.ContractTests.Composition.Timer` — logs which format branch matched.

### Risks & mitigations
- Our regex is lax (won't reject every malformed CRON). Stage 2.4 is not the linter; Phase 3 linter can be stricter. Schema keeps draft-mode tolerance.
- Engine could swap CRON libraries — refine is documented as "lightweight", and a full semantic check would need a server round-trip (out of scope).

### Evaluator gap-analysis prompts
1. Does the schema accept CRON 5 AND 6-field formats?
2. Does it accept ISO-8601 AND hh:mm:ss duration formats?
3. Does it reject obviously invalid strings?
4. Is trailing whitespace rejected?
5. Is the parity count pinned at 1?
6. Is the regex documented as "draft-mode lax; Phase 3 lint is stricter"?

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

### Step 3 — `Container` kind + `NodeConnection` DTO

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 2.4.2

### Goal
Implement `ContainerNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:1-46`:
- `ChildNodes?: List<NodeDefinition>` (line 27).
- `ChildConnections?: List<NodeConnection>` (line 34).
- `ExecutionMode: ExecutionMode` (line 41, default `Sequential`).

This step also formally introduces `NodeConnection` to the Zod schema world, since Container's `ChildConnections` needs it. `NodeConnection` has 9 public properties per `src/ExecutionEngine/Workflow/NodeConnection.cs:11-89`:

| # | Property | Type | Default |
|---|----------|------|---------|
| 1 | `SourceNodeId` | `string` | `""` (required non-empty) |
| 2 | `TargetNodeId` | `string` | `""` (required non-empty) |
| 3 | `TriggerMessageType` | `MessageType` | `Complete` |
| 4 | `Condition` | `string?` | `null` |
| 5 | `IsEnabled` | `bool` | `true` |
| 6 | `Metadata` | `Dictionary<string,string>?` | `null` |
| 7 | `Priority` | `int` | `0` |
| 8 | `SourcePort` | `string?` | `null` |
| 9 | `TargetPort` | `string?` | `null` |

### Depends on
2.4.2.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:14-32` — authoritative `ChildNodes`, `ChildConnections`, and `ExecutionMode` fields.
- `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` — nine-property connection surface mirrored by the DTO.
- `client/src/schemas/node-union.ts` from Step 2.1.2 — discriminated-union placeholder infrastructure replaced by this step.

### Deliverables
- `client/src/schemas/node-connection.schema.ts` (new):
  ```ts
  export const nodeConnectionSchema = z.object({
    sourceNodeId: z.string().min(1),
    targetNodeId: z.string().min(1),
    triggerMessageType: messageTypeSchema.default('Complete'),
    condition: z.string().nullable().optional(),
    isEnabled: z.boolean().default(true),
    metadata: z.record(z.string(), z.string()).nullable().optional(),
    priority: z.number().int().default(0),
    sourcePort: z.string().nullable().optional(),
    targetPort: z.string().nullable().optional(),
  }).strict();
  ```
- `client/src/schemas/node-container.schema.ts`:
  ```ts
  export const containerNodeSchema: z.ZodType<ContainerNode> = z.lazy(() =>
    nodeBaseSchema.extend({
      runtimeType: z.literal('Container'),
      childNodes: z.array(nodeDtoSchema).nullable().optional(),
      childConnections: z.array(nodeConnectionSchema).nullable().optional(),
      executionMode: z.enum(['Sequential','Parallel']).default('Sequential'),
    }).strict()
  );
  ```
- Fixtures: `container-sequential.yaml`, `container-parallel.yaml`, `container-nested.yaml`, `container-empty.yaml`.
- `NodeConnection` unit tests (mostly delegated from Stage 2.4.6 but baseline here).
- Update `node-union.ts` (last placeholder replaced → union is complete).

### Acceptance criteria
- [ ] `NodeConnection` DTO has all 9 engine properties.
- [ ] `Container` DTO has 3 extras matching engine.
- [ ] Child nodes can recursively include any of the 13 kinds including another Container (recursion via `z.lazy`).
- [ ] `executionMode` defaults to `Sequential`.
- [ ] Fixture `container-nested.yaml` parses (depth 2).
- [ ] `isEnabled` default `true` preserved.
- [ ] `triggerMessageType` default `Complete` preserved.
- [ ] After this step, `node-union.ts` has NO placeholders — every member is a real schema.

### Unit tests
1. `ContainerSchema_Sequential_Parses`
2. `ContainerSchema_Parallel_Parses`
3. `ContainerSchema_EmptyChildren_Parses`
4. `ContainerSchema_NestedContainer_Parses`
5. `ContainerSchema_ExecutionMode_Invalid_Fails`
6. `NodeConnectionSchema_Minimal_Parses`
7. `NodeConnectionSchema_MissingSource_Fails`
8. `NodeConnectionSchema_MissingTarget_Fails`
9. `NodeConnectionSchema_DefaultMessageType_Complete`
10. `NodeConnectionSchema_IsEnabledDefault_True`
11. `NodeConnectionSchema_PriorityDefault_0`
12. `NodeConnectionSchema_NegativePriority_Parses` (engine allows)
13. `NodeConnectionSchema_ExtraKey_Strict_Fails`
14. `ContainerRoundTrip_Nested_Match`
15. `ContainerNode_Parity_3Extras`
16. `NodeConnection_Parity_9Props`
17. `NodeUnion_AllPlaceholdersReplaced` (asserts 13 real schemas present)

A/A/A for the union-complete:

- `NodeUnion_AllPlaceholdersReplaced`
  - Arrange: introspect `nodeDtoSchema._def.options`.
  - Act: check each option's `.shape.runtimeType` literal value and non-placeholder extras.
  - Assert: 13 distinct literals; none equal to a placeholder marker.

### Integration tests
- `CompositionRoundTripTests[Container_sequential]`, `[Container_nested]`, and `[AllKinds_smoke]` (loads `roundtrip-all-13-kinds.yaml`).

### Golden-file / parity tests
- **Golden:** 3 Container fixtures; canonicalised YAML byte-match.
- **Parity:** reflection pins 3 extras on Container AND 9 props on NodeConnection.
- **Union completeness:** `NodeUnion_AllPlaceholdersReplaced`.

### Edge cases & negative tests
1. Container with empty `childNodes` array (not null) — parses; distinct from null.
2. Container with cyclical child connection (child A → child B → child A) — DTO parses; engine doesn't detect at load; runtime loops. Phase 3 lint.
3. Child node with `NodeId` colliding with parent — engine namespaces by container, DTO preserves.
4. `executionMode: "Concurrent"` (non-enum value) — fails.
5. Connection with `priority: -5` — legal per engine; no negative check.
6. Connection with `sourceNodeId == targetNodeId` (self-loop) — DTO accepts; engine accepts; runtime may loop. Phase 3 lint.
7. Connection `metadata: {}` (empty) — accepts; distinct from null.
8. Extremely deep container nesting (depth 10) — parses; benchmark stays under 100 ms.

### Observability hooks
- Category `WorkflowUi.ContractTests.Composition.Container` — logs child count and depth.

### Risks & mitigations
- Mutual recursion `nodeDtoSchema <-> containerNodeSchema` — `z.lazy` is the standard solution; depth benchmark validates performance.
- Union refresh: replacing the last placeholder must preserve union member count — asserted by `NodeUnion_AllPlaceholdersReplaced`.

### Evaluator gap-analysis prompts
1. Does the Zod NodeConnection schema have all 9 engine properties with matching defaults?
2. Does `priority: 0` and `isEnabled: true` default correctly?
3. Does Container use `z.lazy` + `z.array(nodeDtoSchema)` to allow nesting?
4. Is `executionMode` a 2-member enum matching `src/ExecutionEngine/Enums/ExecutionMode.cs`?
5. Is there a depth-2 fixture proving recursion works?
6. After this step, are ALL Stage 2.1.2 placeholders replaced (no `makePlaceholder` calls remain)?
7. Is NodeConnection strict-mode enforced?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, ALL 13 kinds live.

---

### Step 4 — YAML round-trip server endpoints

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 2.4.3

### Goal
Wrap engine `WorkflowLoader.Load(filePath, validateOnLoad)` and `WorkflowSerializer.ToYaml/FromYaml/ToJson/FromJson/SaveToFile/LoadFromFile` (per `src/ExecutionEngine/Workflow/WorkflowLoader.cs` and `WorkflowSerializer.cs`) behind HTTP endpoints:

- `POST /api/workflows/parse` — body: YAML text → returns normalised JSON DTO tree (validated against Zod client-side separately).
- `POST /api/workflows/serialize` — body: JSON DTO tree → returns canonical YAML text.
- `GET /api/workflows/{path}` — loads from disk via `WorkflowLoader.Load` (path restricted to a configured sandbox root).
- `PUT /api/workflows/{path}` — saves to disk via `WorkflowSerializer.SaveToFile`.
- `POST /api/workflows/validate` — runs engine `WorkflowLoader`'s `validateOnLoad: true` path over a supplied YAML body; returns engine's exception message(s) without loading into memory for use.

### Depends on
2.4.3, 1.1.3.

### Inputs
- `src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149` — authoritative load/validate surface that the endpoints must wrap directly.
- `src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212` — authoritative serialize/save surface and canonical key ordering.
- `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` — top-level workflow contract returned by parse/serialize endpoints.

### Deliverables
- `src/WorkflowUi.Server/Endpoints/WorkflowRoundTripEndpoints.cs` — minimal-API endpoints with strongly-typed request/response records.
- `src/WorkflowUi.Server/Services/WorkflowFileSandbox.cs` — path resolver that canonicalises `{path}` and rejects traversal outside the configured root.
- `src/WorkflowUi.ContractTests/YamlEndpointTests.cs` — integration tests via `WebApplicationFactory`.
- `client/src/api/workflows.ts` — thin typed fetch wrappers.

### Acceptance criteria
- [ ] `POST /api/workflows/parse` with a known-good fixture returns a JSON body that, parsed by `nodeDtoSchema`, yields the expected nodes.
- [ ] `POST /api/workflows/serialize` round-trips: parse → serialise → parse again ≡ identity.
- [ ] `GET /api/workflows/{path}` returns 404 for missing files, 400 for traversal attempts, 200 + body for valid files.
- [ ] `PUT /api/workflows/{path}` rejects writes that fail Zod validation BEFORE touching disk.
- [ ] `POST /api/workflows/validate` returns engine's validation error messages verbatim.
- [ ] All endpoints emit structured logs under `WorkflowUi.Server.RoundTrip`.
- [ ] `WebApplicationFactory` integration tests cover all 5 endpoints.
- [ ] Canonicaliser from Stage 0.2.3 is used server-side for response emission.

### Unit tests
1. `ParseEndpoint_ValidYaml_ReturnsJson`
2. `ParseEndpoint_MalformedYaml_Returns400`
3. `ParseEndpoint_ExtraEngineProp_Returns200_WithJsonKept` (engine tolerance — generator-unaware prop)
4. `SerializeEndpoint_RoundTripIdentity`
5. `SerializeEndpoint_InvalidDto_Returns400`
6. `GetWorkflowEndpoint_Missing_Returns404`
7. `GetWorkflowEndpoint_Traversal_Returns400`
8. `GetWorkflowEndpoint_Valid_Returns200`
9. `PutWorkflowEndpoint_ValidDto_Writes`
10. `PutWorkflowEndpoint_InvalidDto_DoesNotWrite`
11. `ValidateEndpoint_Passes_Returns200`
12. `ValidateEndpoint_Fails_Returns422_WithEngineMessage`
13. `AllEndpoints_LogCategoryIs_WorkflowUi_Server_RoundTrip`
14. `Sandbox_TraversalDotDot_Rejected`
15. `Sandbox_AbsolutePath_Rejected`
16. `Sandbox_SymlinkEscape_Rejected` (if symlinks exist in the test root)

A/A/A for the serialise identity:

- `SerializeEndpoint_RoundTripIdentity`
  - Arrange: `var dto = await Parse(readFixture("roundtrip-all-13-kinds.yaml"));`
  - Act: `var yaml2 = await Serialize(dto); var dto2 = await Parse(yaml2);`
  - Assert: `Canonicalise(dto).Should().BeEquivalentTo(Canonicalise(dto2));`

### Integration tests
- Full `WebApplicationFactory`-based tests covering the 5 endpoints with `roundtrip-all-13-kinds.yaml`.
- A cross-process test loads the fixture, sends to `/parse`, pipes to `/serialize`, asserts byte-match after canonicalise.

### Golden-file / parity tests
- **Golden:** `roundtrip-all-13-kinds.yaml` — the canonical full-coverage fixture. Parity: parse → serialise → parse yields identical DTO tree AND canonicalised YAML.
- **Diff contract:** any failure produces a unified diff log artifact via the Stage 0.2.3 canonicaliser.
- **Engine parity:** endpoint `POST /parse` output JSON matches what Stage 0.2.2 parity test produces when running the engine directly against the same fixture.

### Edge cases & negative tests
1. YAML with a tab character (YAML 1.2 forbids tabs in indentation) — engine rejects; endpoint returns 400 with engine's message.
2. YAML with alias+anchor (`&x` / `*x`) — YamlDotNet expands; round-trip may NOT preserve the alias but will preserve the expanded value. Documented as a known lossy corner.
3. YAML using flow style (`[a, b, c]`) — YamlDotNet normalises to block style on re-emit. Canonicaliser treats as equivalent.
4. File path with backslashes on POSIX (CI cross-platform) — sandbox rejects.
5. Very large workflow (10_000 nodes) — parse < 2 s; serialise < 2 s; endpoint streams response.
6. `validateOnLoad: false` path — engine tolerates more; DTO rejects more. Endpoint documents the gap in a `/api/workflows/limits` docs route.
7. Writing to a read-only file — returns 500 with structured error.
8. Concurrent PUT to same file — no locking implemented; last-write-wins. Documented as a Phase 6 (multi-user) concern.

### Observability hooks
- Log category `WorkflowUi.Server.RoundTrip` at `Information` on success (with bytes in/out), `Warning` on validation failure, `Error` on engine exception.
- Metric counters: `workflow_parse_total`, `workflow_serialize_total`, `workflow_validate_failures_total`.
- Request-id correlation: every response header `X-Request-Id`, echoed in logs.

### Risks & mitigations
- Path-traversal vulnerabilities — centralised sandbox + explicit rejection tests including symlink cases.
- YamlDotNet canonicalisation losses (aliases, flow style) — documented in Stage 0.2.3; round-trip equivalence defined over the canonicalised form, not byte identity.
- Long-lived YAML parse holding threads — endpoints use `async`; large uploads capped via Kestrel `MaxRequestBodySize` (default 30 MB is fine).
- Sandbox misconfiguration (root == `/`) — startup check refuses to start with a root at `/` or `C:\`.

### Evaluator gap-analysis prompts
1. Does every endpoint wrap engine `WorkflowLoader`/`WorkflowSerializer` directly, without reimplementation?
2. Is there a sandbox rejecting `..`, absolute paths, and symlink escapes?
3. Does the serialise-identity round-trip pass on `roundtrip-all-13-kinds.yaml`?
4. Do validation failures return engine's exception message verbatim?
5. Are structured logs and metric counters wired?
6. Is the endpoint test coverage using `WebApplicationFactory` (not unit mocks)?
7. Is `validateOnLoad: true` the default? (Matches engine `WorkflowLoader.Load` default per `WorkflowLoader.cs:42`.)

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all 5 endpoints reachable and covered by integration tests.

---

### Step 5 — Sidecar (`.workflow-ui.json`) I/O

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 2.4.4

### Goal
Define and implement the UI-only sidecar file that lives next to each `*.yaml` workflow (e.g., `orders.yaml` + `orders.workflow-ui.json`). The sidecar carries **non-semantic** metadata: per-node coordinates, colours, collapsed/expanded state, sticky notes, per-edge label offsets. It MUST NOT affect engine behaviour; loading a workflow without a sidecar yields sensible defaults.

Sidecar schema:
```ts
export const sidecarSchema = z.object({
  version: z.literal('1.0'),
  workflowFile: z.string().min(1),       // relative path pointing at the YAML
  nodePositions: z.record(z.string(), z.object({ x: z.number(), y: z.number() })).default({}),
  nodeColors: z.record(z.string(), z.string().regex(/^#[0-9a-fA-F]{6}$/)).default({}),
  nodeCollapsed: z.record(z.string(), z.boolean()).default({}),
  notes: z.array(z.object({
    id: z.string().min(1),
    x: z.number(), y: z.number(),
    text: z.string(),
    color: z.string().regex(/^#[0-9a-fA-F]{6}$/).optional(),
  })).default([]),
  edgeLabelOffsets: z.record(z.string(), z.object({ dx: z.number(), dy: z.number() })).default({}),
}).strict();
```

Endpoints:
- `GET /api/workflows/{path}/sidecar` — returns sidecar JSON or 404.
- `PUT /api/workflows/{path}/sidecar` — saves sidecar (creates if absent).

### Depends on
2.4.4.

### Inputs
- `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` — semantic workflow shape that must remain independent from sidecar metadata.
- `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` — edge ids/ports referenced by sidecar label offsets.
- Step 2.4.4 deliverables (`WorkflowRoundTripEndpoints.cs`, `WorkflowFileSandbox.cs`) — sandbox/path-handling behavior reused for sidecars.

### Deliverables
- `client/src/schemas/sidecar.schema.ts`.
- `src/WorkflowUi.Server/Endpoints/SidecarEndpoints.cs`.
- `src/WorkflowUi.Server/Services/SidecarStore.cs` — JSON read/write with atomic replace.
- Fixtures: `sidecar-basic.json`, `sidecar-with-notes.json`, `sidecar-missing-node.json` (references a node that doesn't exist in the paired YAML — the server allows this but the UI warns).
- `SidecarEndpointTests.cs`.

### Acceptance criteria
- [ ] Sidecar absence does NOT break workflow load.
- [ ] Sidecar load is independent of workflow load (separate endpoint).
- [ ] Node IDs in sidecar that don't exist in the workflow are preserved (forward-compatibility) but flagged by a server-side warning log.
- [ ] Coordinate numbers accept fractional values.
- [ ] Colour strings require `#RRGGBB`.
- [ ] Writing a sidecar uses atomic rename (temp file + `File.Move` with overwrite) to avoid corruption on crash.
- [ ] Round-trip: save → load yields equivalent JSON (order-insensitive for record properties, order-sensitive for `notes` array).
- [ ] Sandbox from Stage 2.4.4 reused for sidecar paths.
- [ ] Engine never reads `.workflow-ui.json` — verified by grep test.

### Unit tests
1. `SidecarSchema_Basic_Parses`
2. `SidecarSchema_InvalidColor_Fails`
3. `SidecarSchema_MissingVersion_Fails`
4. `SidecarSchema_EmptyPositions_Parses`
5. `SidecarSchema_FractionalCoords_Parses`
6. `SidecarSchema_NegativeCoords_Parses`
7. `SidecarSchema_StrictMode_RejectsExtraKeys`
8. `SidecarStore_SaveLoadRoundTrip`
9. `SidecarStore_AtomicReplace_OnCrash` (simulated via mid-write crash)
10. `SidecarStore_OrphanNodeIds_Preserved_WithWarning`
11. `SidecarEndpoints_Get_Missing_Returns404`
12. `SidecarEndpoints_Get_Present_Returns200`
13. `SidecarEndpoints_Put_Creates`
14. `SidecarEndpoints_Put_InvalidColor_Returns400`
15. `Engine_NeverReads_SidecarFile` (grep test over engine source)
16. `WorkflowLoad_WithoutSidecar_Succeeds`

A/A/A for the orphan-id case:

- `SidecarStore_OrphanNodeIds_Preserved_WithWarning`
  - Arrange: workflow has nodes `{a, b}`; sidecar has positions `{a: ..., b: ..., ghost: ...}`.
  - Act: load sidecar alongside workflow.
  - Assert: sidecar object still contains `ghost` (not stripped); a `Warning`-level log was emitted mentioning `ghost`.

### Integration tests
- `SidecarEndpointTests` — Create + Read + Update + Delete cycle via `WebApplicationFactory`.
- Cross-test: load a workflow via `/api/workflows/{path}` + its sidecar via `/api/workflows/{path}/sidecar` and verify they compose without conflict.

### Golden-file / parity tests
- **Golden:** `sidecar-basic.json` — round-trips through `SidecarStore` byte-for-byte (after JSON canonicalisation: sorted keys, 2-space indent, LF).
- **Non-parity assertion:** `Engine_NeverReads_SidecarFile` — a `grep` over `src/ExecutionEngine/**/*.cs` asserts the string `.workflow-ui.json` is absent.
- **Independence test:** loading a workflow without a sidecar must yield identical DTO tree to loading with an empty sidecar.

### Edge cases & negative tests
1. Sidecar with a different `version` (e.g., `"2.0"`) — fails on schema; endpoint returns 400.
2. Sidecar `workflowFile` pointing to a different YAML — allowed (just metadata); UI shows a warning; server logs.
3. Sidecar for a YAML that doesn't exist — allowed to save (orphan); endpoint returns 200.
4. Writing sidecar with 100_000 notes — parses; write takes < 500 ms; enforce a 10 MB body cap at Kestrel.
5. Colour `"#RRGGBBAA"` (with alpha) — rejected by regex; only 6 hex chars allowed.
6. Colour `"red"` (named) — rejected.
7. Sidecar with BOM — tolerated on read, stripped on write.
8. Concurrent PUTs to same sidecar — atomic rename prevents corruption; last-write-wins.

### Observability hooks
- Category `WorkflowUi.Server.Sidecar`.
- Warn on orphan node IDs; debug on every save with byte count.

### Risks & mitigations
- Developer confusion about what belongs in sidecar vs DTO — header comment in `sidecar.schema.ts` enumerates the UI-only concerns + a "do NOT put semantic config here" note.
- Engine accidentally starts reading the sidecar — grep test prevents; reinforced by ADR.
- Sidecar growing unbounded with many notes — soft cap (warn above 1000 notes).

### Evaluator gap-analysis prompts
1. Is the engine proven to never read the sidecar (grep test)?
2. Does sidecar-absent load succeed cleanly?
3. Are orphan node IDs preserved with a warning (not silently stripped)?
4. Is the save atomic (temp file + rename)?
5. Are colour strings restricted to `#RRGGBB`?
6. Does the sandbox from 2.4.4 guard sidecar paths?
7. Is the sidecar versioned, with schema rejecting unknown versions?
8. Are orphan-node-id warnings structured-logged for UI surfacing?

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

### Step 6 — Port matrix

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 2.4.5

### Goal
Formalise as data-driven tables the "port matrix" that Phase 3 lint and Phase 5 UI will consume: which `TriggerMessageType` values are legal on outgoing edges per source-node `runtimeType`, and which are legal on incoming edges per target-node `runtimeType`. The engine itself does not enforce these rules — they encode idiomatic workflow construction. Examples:
- `ForEach` emits `Next` for each item (body edge) and `Complete` when exhausted (exit edge). `Fail` is allowed (exceptions propagate); `Progress` and `Cancel` are uncommon but legal.
- `IfElse` emits `Complete` (both branches end with a completion signal — differentiated by edge `Condition`). It does NOT idiomatically emit `Next`.
- `Subflow` emits `Complete` / `Fail` / `Cancel`; may relay `Progress` from the child workflow.

The matrix is authored once here as a typed TypeScript table AND a mirror JSON fixture used by server-side `PortMatrixTests` for parity.

### Depends on
2.4.5.

### Inputs
- `src/ExecutionEngine/Enums/RuntimeType.cs:13-29` — authoritative 13 runtime-type values that define the matrix rows.
- `src/ExecutionEngine/Enums/MessageType.cs:13-20` — authoritative 5 message-type values that define the matrix columns.
- Stage 2 fixtures from Steps 2.3.3a, 2.3.3b, and 2.4.3 — canonical examples used to justify idiomatic cells.

### Deliverables
- `client/src/schemas/port-matrix.ts`:
  ```ts
  type MessageLegality = 'idiomatic' | 'allowed' | 'rare' | 'forbidden';
  type PerKindRow = Record<MessageType, MessageLegality>;
  export const outgoingPortMatrix: Record<RuntimeType, PerKindRow> = { ... };
  export const incomingPortMatrix: Record<RuntimeType, PerKindRow> = { ... };
  export function classifyOutgoing(source: RuntimeType, m: MessageType): MessageLegality;
  ```
- `client/src/__fixtures__/port-matrix.json` — mirrored table for server parity.
- `src/WorkflowUi.ContractTests/PortMatrixTests.cs` — loads the JSON, asserts: (a) every `(runtimeType, messageType)` pair has an entry, (b) no unknown keys, (c) the matrix count = 13 × 5 for each direction = 65 rows.
- Update `client/src/api/lint.ts` stub (Phase 3 will flesh this out) so it can import the matrices.

### Acceptance criteria
- [ ] 13 runtime types × 5 message types = 65 cells per direction; no missing cells.
- [ ] Every cell has exactly one legality value.
- [ ] `classifyOutgoing` returns `'idiomatic'` for the documented canonical pairs (e.g., `ForEach` + `Next` + body).
- [ ] `PortMatrixTests` parity: fixture count matches engine enum counts (13 × 5).
- [ ] Adding a new `RuntimeType` or `MessageType` upstream makes `PortMatrixTests` fail until the matrix is extended.
- [ ] UI-only concern — engine untouched.

### Unit tests
1. `PortMatrix_OutgoingHasAll65Cells`
2. `PortMatrix_IncomingHasAll65Cells`
3. `PortMatrix_NoUnknownRuntimeType`
4. `PortMatrix_NoUnknownMessageType`
5. `PortMatrix_ClassifyOutgoing_ForEachNext_Idiomatic`
6. `PortMatrix_ClassifyOutgoing_IfElseComplete_Idiomatic`
7. `PortMatrix_ClassifyOutgoing_IfElseNext_Forbidden` (or 'rare' — whichever the matrix decides)
8. `PortMatrix_ClassifyOutgoing_UnknownKind_Throws`
9. `PortMatrix_MirrorParity_ClientJsonMatchesTsTable`
10. `PortMatrix_EngineEnum_Lock_13x5_Cells`
11. `PortMatrix_NewKindAdded_TestFails` (synthetic — run via a test helper that adds a fake enum entry)

A/A/A for parity:

- `PortMatrix_EngineEnum_Lock_13x5_Cells`
  - Arrange: load `engine-enums.json` from Stage 0.2.1 parity fixture.
  - Act: read `RuntimeType.members.length` and `MessageType.members.length`.
  - Assert: `13 * 5 === 65 === outgoing cell count === incoming cell count`.

### Integration tests
- Cross-file parity: the client TS table and the JSON fixture have identical structure; a Jest test parses both and asserts deep equality.

### Golden-file / parity tests
- **Parity:** `port-matrix.json` row count = 65 per direction.
- **Locked:** `PortMatrix_EngineEnum_Lock_13x5_Cells` fails immediately on engine enum growth, guiding the update.
- **Canonical pairs (documented):**
  - `ForEach → Next` = idiomatic (body edge).
  - `ForEach → Complete` = idiomatic (exit edge).
  - `While → Next` = idiomatic (body).
  - `While → Complete` = idiomatic (exit).
  - `IfElse → Complete` = idiomatic (both branches).
  - `Switch → Complete` = idiomatic.
  - Any kind → `Fail` = allowed (exception path).
  - Any kind → `Progress` = allowed (status beacon).
  - `Timer → Next` = idiomatic (tick).
  - `Timer → Complete` = idiomatic (schedule ended / one-shot).
  - `Subflow → Complete` = idiomatic; `→ Progress` = allowed (child workflow relay).
  - `Container → Complete` = idiomatic.
  - `Noop → Complete` = idiomatic (only option).

### Edge cases & negative tests
1. A developer adds a new RuntimeType `Foo` — `PortMatrix_NoUnknownRuntimeType` fails because `port-matrix.json` lacks an entry; forcing a deliberate matrix update.
2. A developer removes a MessageType — existing fixtures fail parity.
3. Matrix cell typo (`"idiomtic"` instead of `"idiomatic"`) — Zod union rejects.
4. Client TS table and JSON fixture drift — mirror-parity test fails.
5. Phase 3 lint consumers rely on `classifyOutgoing` never throwing for legal inputs — test confirms it throws ONLY on invalid inputs.
6. Matrix rule change (e.g., `IfElse → Next` from `forbidden` to `rare`) — one line-changed diff is auditable.
7. Empty/missing matrix file — Phase 3 lint disables with a fallback warning; out of scope here but documented.

### Observability hooks
- Category `WorkflowUi.ContractTests.PortMatrix` — logs cell count and any legality distribution stats.

### Risks & mitigations
- Matrix becomes stale — engine enum parity tests force updates.
- Over-restrictive matrix blocks legitimate workflows — legality bucket `'rare'` gives UI a way to warn rather than error; `'forbidden'` is reserved for truly nonsensical combinations.
- Conflating engine enforcement with UI convention — header comment makes explicit that the matrix is UI-only.

### Evaluator gap-analysis prompts
1. Is the matrix 13 × 5 for BOTH outgoing and incoming directions?
2. Is every cell one of `idiomatic | allowed | rare | forbidden`?
3. Does the matrix clearly document that it is UI-only, not engine-enforced?
4. Does a parity test compare the client TS table with the JSON fixture?
5. Does a parity test lock the matrix to engine enum counts (13 × 5)?
6. Are the canonical pairs for `ForEach`, `While`, `IfElse`, `Switch`, `Subflow`, `Timer`, `Container`, `Noop` documented?
7. Does `classifyOutgoing` have deterministic throw behaviour on unknown inputs?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, port matrix complete and Phase 3 lint can import without further work.
