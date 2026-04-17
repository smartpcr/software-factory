# Stage 2.2 — Task-family kinds

**Phase:** Phase 2 — Node Kinds, Schemas, Round-Trip
**Stage status:** [x]
**Total effort:** 7.5 d
**Step count:** 6

## Steps

- [x] **Step 1**: `Noop` kind
- [x] **Step 2**: `CSharp` kind (assembly + type)
- [x] **Step 3**: `CSharpScript` kind
- [x] **Step 4**: `CSharpTask` kind
- [x] **Step 5**: `PowerShell` kind (script-file)
- [x] **Step 6**: `PowerShellTask` kind

---
## Context

Stage 2.2 implements the 6 "task-family" runtime kinds — nodes that execute a unit of work (no branching, no iteration, no composition). They are the simplest shapes and therefore the best place to refine the per-kind conventions that Stages 2.3/2.4 will reuse.

Coverage:

| Step | Runtime kind | Engine source file | Extra DTO props |
|------|--------------|--------------------|-----------------|
| 2.2.1a | `Noop` | `NoopNodeDefinition.cs` | (none) |
| 2.2.1b | `CSharp` | `CSharpNodeDefinition.cs` | `AssemblyPath?`, `TypeName?` |
| 2.2.2 | `CSharpScript` | `CSharpScriptNodeDefinition.cs` | `ScriptPath`, `ScriptContent` (both `""`-default) |
| 2.2.3 | `CSharpTask` | `CSharpTaskNodeDefinition.cs` | `ScriptContent?`, `ExecutorTypeName?`, `ExecutorAssemblyPath?` |
| 2.2.4a | `PowerShell` | `PowerShellScriptNodeDefinition.cs` | `ScriptPath` (required), `RequiredModules?`, `ModulePaths?` |
| 2.2.4b | `PowerShellTask` | `PowerShellTaskNodeDefinition.cs` | `ScriptPath?` (nullable, default `""`), `ScriptContent?`, `RequiredModules?`, `ModulePaths?` |

Each step follows the same rhythm:
1. Cite the engine file line ranges proving the property list.
2. Extend `nodeBaseSchema` into a kind-specific Zod schema (`runtimeType: z.literal('<Kind>')` + extra properties).
3. Replace the Stage 2.1.2 placeholder in `node-union.ts`.
4. Add a golden YAML fixture that round-trips through engine `WorkflowSerializer`.
5. Add parity tests asserting the extra property set matches engine reflection output.

**Cross-cutting facts defended by this stage:**
- `CSharpScriptNodeDefinition` has both `ScriptPath` AND `ScriptContent`; exactly one must be populated (engine enforces via `Validate()` — see `CSharpScriptNodeDefinition.cs:42-58`).
- `PowerShellScriptNodeDefinition.RuntimeType` is `RuntimeType.PowerShell` (NOT `RuntimeType.PowerShellScript` — no such member exists). Master plan gets this right; just guard against a future drift.
- `PowerShellTaskNodeDefinition.ScriptPath` is declared `public string? ScriptPath { get; set; } = string.Empty;` — nullable TYPE with empty-string INITIALISER (engine code smell). The DTO must accept all three: `null`, `""`, and a real path; but the UI mapper in Phase 5 prefers `null` for "unset".

## Prerequisites
- Stage 2.1 complete (base DTO + discriminated-union skeleton).
- Stage 1.2 generator wired so `RuntimeType` enum values are available in `engine-enums.json`.

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

Client
- `src/WorkflowUI/src/schemas/node-noop.schema.ts` (new, 2.2.1a)
- `src/WorkflowUI/src/schemas/node-csharp.schema.ts` (new, 2.2.1b)
- `src/WorkflowUI/src/schemas/node-csharp-script.schema.ts` (new, 2.2.2)
- `src/WorkflowUI/src/schemas/node-csharp-task.schema.ts` (new, 2.2.3)
- `src/WorkflowUI/src/schemas/node-powershell.schema.ts` (new, 2.2.4a)
- `src/WorkflowUI/src/schemas/node-powershell-task.schema.ts` (new, 2.2.4b)
- `src/WorkflowUI/src/schemas/node-union.ts` (modified each step — one placeholder replaced)
- `src/WorkflowUI/src/schemas/__tests__/node-<kind>.schema.test.ts` per step.

Fixtures (all under `src/WorkflowUI/src/__fixtures__/`)
- `noop-minimal.yaml` + `.json`
- `csharp-minimal.yaml` + `.json`
- `csharp-script-inline.yaml`, `csharp-script-file.yaml` + `.json` pairs
- `csharp-task-executor.yaml`, `csharp-task-inline.yaml` + `.json`
- `powershell-script.yaml` + modules variant
- `powershell-task-inline.yaml`, `powershell-task-filepath.yaml`

Server parity
- `src/WorkflowUi.ContractTests/TaskFamilyRoundTripTests.cs` — one `[Theory]` row per fixture.

## Shared fixtures / fakes
- Every YAML fixture is a SINGLE-NODE workflow wrapped in a `WorkflowDefinition` with empty `Connections` — this is the minimal engine-parsable shape. `TaskFamilyRoundTripTests` loads via `WorkflowLoader.Load(path)` and asserts the deserialised node matches the fixture's property-by-property shape.
- Placeholder schemas from Stage 2.1.2 are swapped one at a time; `node-union.ts` grows in commit atomicity with its paired step.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="2.2"`, `phaseId="2"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-2.2.plan-read`, `workflow-ui.stage-2.2.apply`, `workflow-ui.stage-2.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_2_2_started_total`, counter `workflow_ui_stage_2_2_failed_total`, histogram `workflow_ui_stage_2_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 2.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage22` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

### Step 1 — `Noop` kind

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 2.1.2

### Goal
Implement the simplest runtime kind. `NoopNodeDefinition` (per `src/ExecutionEngine/Nodes/Definitions/NoopNodeDefinition.cs:1-25`) adds NO extra properties beyond the base; its only job is to demonstrate the discriminated-union mechanics and establish the golden-file fixture format all subsequent kinds follow.

### Depends on
2.1.2.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/NoopNodeDefinition.cs`.
- `src/WorkflowUI/src/schemas/node-base.schema.ts`.

### Deliverables
- `src/WorkflowUI/src/schemas/node-noop.schema.ts`:
  ```ts
  export const noopNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('Noop'),
  }).strict();
  export type NoopNode = z.infer<typeof noopNodeSchema>;
  ```
- `src/WorkflowUI/src/schemas/node-union.ts` — replace `makePlaceholder('Noop')` with `noopNodeSchema`.
- `src/WorkflowUI/src/__fixtures__/noop-minimal.yaml`:
  ```yaml
  name: noop-minimal
  version: '1.0'
  nodes:
    - runtimeType: Noop
      nodeId: noop-1
      nodeName: Start
      joinType: Any
      priority: Normal
      maxConcurrentExecutions: 0
  connections: []
  ```
- `src/WorkflowUI/src/__fixtures__/noop-minimal.json` — JSON-equivalent.
- `src/WorkflowUI/src/schemas/__tests__/node-noop.schema.test.ts` (8+ tests).
- `src/WorkflowUi.ContractTests/TaskFamilyRoundTripTests.cs` — one row referencing this fixture.

### Acceptance criteria
- [x] `noopNodeSchema.parse(fixtureNode)` succeeds.
- [x] Adding ANY extra property fails because of `.strict()` — the schema rejects unknown keys.
- [x] Engine round-trip via `WorkflowLoader.Load("noop-minimal.yaml")` yields a single `NoopNodeDefinition` with the matching `NodeId`.
- [x] After re-serializing via `WorkflowSerializer.ToYaml`, the YAML text diffs only in whitespace / key order against the fixture (canonicaliser handles the comparison).
- [x] `node-union.ts` still has 13 members and exhaustiveness compiles.
- [x] `noopNodeSchema.shape` has exactly 12 keys (11 base + discriminator).

### Unit tests
1. `NoopNodeSchema_MinimalFixture_Parses`
2. `NoopNodeSchema_ExtraProperty_Fails_StrictMode`
3. `NoopNodeSchema_RuntimeType_NotNoop_Fails`
4. `NoopNodeSchema_MissingNodeId_Fails`
5. `NoopNodeSchema_WithRetryPolicy_Parses`
6. `NoopNodeSchema_Shape_Has12Keys`
7. `NoopNodeSchema_JoinTypeAll_Parses`
8. `NoopNodeRoundTrip_Yaml_Engine_ToDto_BackToYaml_Matches` (C# + TS pair)
9. `NoopNode_EnginePropertyCount_Matches_DtoPropertyCount` (parity)

A/A/A for the three most important:

- `NoopNodeSchema_MinimalFixture_Parses`
  - Arrange: import `noop-minimal.json`.
  - Act: `noopNodeSchema.parse(fixture.nodes[0])`.
  - Assert: no throw; `parsed.runtimeType === 'Noop'`.
- `NoopNodeSchema_ExtraProperty_Fails_StrictMode`
  - Arrange: `{ ...fixture, bogus: 1 }`.
  - Act: `safeParse`.
  - Assert: `.success === false` with issue code `unrecognized_keys`.
- `NoopNodeRoundTrip_Yaml_Engine_ToDto_BackToYaml_Matches`
  - Arrange: load fixture YAML via engine.
  - Act: serialise back via engine; normalise both with the canonicaliser.
  - Assert: `Assert.Equal(canonicalise(fixture), canonicalise(roundTripped));`

### Integration tests
- `TaskFamilyRoundTripTests[Noop]` — engine→DTO→engine→YAML.

### Golden-file / parity tests
- **Golden:** `noop-minimal.yaml`. Diff contract: canonicalised YAML byte-match. Regen command: `dotnet run --project src/WorkflowUi.ContractTests -- regenerate-fixture noop-minimal` (generates a fresh fixture from engine, used only by maintainers to repair goldens after deliberate engine changes).
- **Parity:** `engine-dto-shape.json` entry for `NoopNodeDefinition` must have 0 "extra" properties beyond base; the test `NoopNode_EnginePropertyCount_Matches_DtoPropertyCount` asserts `reflected.extra.length === schemaExtraKeys.length === 0`.

### Edge cases & negative tests
1. Fixture missing `runtimeType` — fails before discriminator resolution.
2. Fixture with `RuntimeType: Noop` (PascalCase key) — JSON parser accepts (camelCase in YAML is our convention), but Zod key `runtimeType` fails; documents the casing contract.
3. Fixture with BOM — stripped by the loader; canonicalise tolerates.
4. Fixture with CRLF — normalised to LF before compare.
5. Engine adds a future property to `NoopNodeDefinition` (unlikely but possible) — parity test fails first; schema updated in a follow-up.
6. Fixture with duplicate `nodeId` in `nodes[]` — engine `WorkflowLoader.Load` fails; handled in Stage 2.4.1 but documented here.

### Observability hooks
- `TaskFamilyRoundTripTests` logs category `WorkflowUi.ContractTests.TaskFamily.Noop` with elapsed load+serialize time; useful to catch performance regressions in later stages.

### Risks & mitigations
- Zod `.strict()` surprises if engine adds an extra property — parity test fails first (good).
- Dual-format fixture (YAML + JSON) drift — a helper test asserts they deserialise to structurally-equal objects.

### Evaluator gap-analysis prompts
1. Does `noopNodeSchema` extend `nodeBaseSchema` and use `z.literal('Noop')` as the discriminator?
2. Does it use `.strict()` to reject unknown properties?
3. Is there a golden fixture whose canonicalised bytes match after engine round-trip?
4. Does the parity test prove `NoopNodeDefinition` has 0 properties beyond the 11-prop base?
5. Is `node-union.ts` updated to use `noopNodeSchema` in place of the Stage 2.1.2 placeholder?
6. Does the fixture live in a shared `__fixtures__/` directory visible to both client tests and server contract tests?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.

---

### Step 2 — `CSharp` kind (assembly + type)

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 2.2.1a

### Goal
Implement `CSharpNodeDefinition` (per `src/ExecutionEngine/Nodes/Definitions/CSharpNodeDefinition.cs:1-36`). Two extra properties — both nullable strings:
- `AssemblyPath?: string` (line 27)
- `TypeName?: string` (line 32)

### Depends on
2.2.1a.

### Inputs
`CSharpNodeDefinition.cs`; `node-base.schema.ts`.

### Deliverables
- `src/WorkflowUI/src/schemas/node-csharp.schema.ts`:
  ```ts
  export const csharpNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('CSharp'),
    assemblyPath: z.string().nullable().optional(),
    typeName: z.string().nullable().optional(),
  }).strict();
  ```
- Fixture `csharp-minimal.yaml` with real-looking values: `assemblyPath: ./bin/Nodes.dll`, `typeName: Acme.Nodes.CopyFileNode`.
- Fixture `csharp-absent-type.yaml` (both properties absent) — the engine tolerates this at load time, only the runtime binder rejects it; the DTO must also tolerate it so the UI can open drafts.
- Tests: parse, strict-key, null vs absent, typeName without assemblyPath, etc.
- TaskFamilyRoundTripTests row for both fixtures.

### Acceptance criteria
- [x] Schema accepts both null and absent for each property.
- [x] Schema rejects `assemblyPath: 42`.
- [x] `.strict()` rejects unknown keys.
- [x] Round-trip preserves null vs absent distinction exactly (null stays null, undefined drops from YAML emit — document this).
- [x] `csharp-absent-type.yaml` loads successfully via engine (matches C# null handling).
- [x] 2 extra properties matches reflection output.

### Unit tests
1. `CSharpNodeSchema_Minimal_Parses`
2. `CSharpNodeSchema_BothPropsNull_Parses`
3. `CSharpNodeSchema_BothPropsAbsent_Parses`
4. `CSharpNodeSchema_AssemblyPathNotString_Fails`
5. `CSharpNodeSchema_ExtraKey_Fails_Strict`
6. `CSharpNodeSchema_TypeName_DotQualified_Parses`
7. `CSharpNodeRoundTrip_EngineYaml_Matches`
8. `CSharpNode_PropertyCount_Matches_Engine` (parity)
9. `CSharpNodeSchema_RuntimeType_NotCSharp_Fails`

A/A/A for two most important:

- `CSharpNodeSchema_BothPropsAbsent_Parses`
  - Arrange: `{ runtimeType: 'CSharp', nodeId: 'n', joinType: 'Any', priority: 'Normal', maxConcurrentExecutions: 0 }`.
  - Act: `csharpNodeSchema.parse(input)`.
  - Assert: parses; `parsed.assemblyPath === undefined`.
- `CSharpNodeRoundTrip_EngineYaml_Matches`
  - Arrange: load `csharp-minimal.yaml` via `WorkflowLoader`.
  - Act: deserialise via Zod; re-emit via engine; canonicalise; compare.
  - Assert: byte-match canonicalised.

### Integration tests
- `TaskFamilyRoundTripTests[CSharp_minimal]`, `TaskFamilyRoundTripTests[CSharp_absent_type]`.

### Golden-file / parity tests
- **Golden:** `csharp-minimal.yaml`, `csharp-absent-type.yaml`. Diff contract: canonicalised byte-match.
- **Parity:** reflection shows exactly `AssemblyPath`, `TypeName` as extras; Zod extras match.

### Edge cases & negative tests
1. `typeName: "Foo"` without `assemblyPath` — allowed at load; Phase 5 UI warns.
2. Path with backslashes on Windows (`.\\bin\\Nodes.dll`) — preserved verbatim through round-trip.
3. Very long type name — no length limit applied; fixture tested with 200-char name.
4. UTF-8 in type name (unlikely but legal per CLR metadata rules) — handled by JSON/YAML parsers.
5. `typeName` with trailing whitespace — preserved (engine doesn't trim), but a Phase 5 linter flags.
6. Empty string `assemblyPath: ""` — legal; distinct from `null`.

### Observability hooks
- Category `WorkflowUi.ContractTests.TaskFamily.CSharp` — logs when null vs absent distinction diverges.

### Risks & mitigations
- Null vs empty-string drift through YamlDotNet — specific test proves preservation.
- Windows path separators in fixtures — documented convention: fixtures use forward slashes, Windows-specific tests normalise.

### Evaluator gap-analysis prompts
1. Does the schema declare both `assemblyPath` and `typeName` as `.string().nullable().optional()`?
2. Are there tests for BOTH "null" and "absent" states, distinct from each other?
3. Is `.strict()` applied so unknown keys like `AssemblyPath` (wrong case) fail?
4. Does a round-trip fixture prove engine preserves null?
5. Is there a parity test that reflection on `CSharpNodeDefinition` yields exactly these 2 extras?

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

### Step 3 — `CSharpScript` kind

**Status:** [ ]
**Effort:** 1.5 d
**Depends on:** 2.2.1b

### Goal
Implement `CSharpScriptNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:1-60`:
- `ScriptPath: string` (line 27, default `""`)
- `ScriptContent: string` (line 32, default `""`)

Engine validates exactly one is populated (lines 42-58). The DTO accepts either-or-both at parse time (drafts may be saved with both empty or neither) but includes a `.superRefine` that warns (non-fatal) when both are populated and errors (fatal) when neither is populated AND the node appears in a persisted workflow.

### Depends on
2.2.1b.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:12-49` — authoritative `ScriptPath` / `ScriptContent` defaults and validation behavior.
- `src/WorkflowUI/src/schemas/node-base.schema.ts` from Step 2.1.1 — base DTO/nullability rules inherited by this kind.
- `src/WorkflowUI/src/engine-contract.ts` from Stage 1.2 — `RuntimeType.CSharpScript` discriminator and enum-lock fixtures.

### Deliverables
- `src/WorkflowUI/src/schemas/node-csharp-script.schema.ts`:
  ```ts
  export const csharpScriptNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('CSharpScript'),
    scriptPath: z.string().default(''),
    scriptContent: z.string().default(''),
  }).strict().superRefine((val, ctx) => {
    if (val.scriptPath === '' && val.scriptContent === '') {
      ctx.addIssue({ code: 'custom', message: 'ScriptPath or ScriptContent must be set', path: ['scriptPath'] });
    }
  });
  ```
  (Note: draft mode toggles the refine off via a `{ strict: boolean }` wrapper schema.)
- Fixtures: `csharp-script-inline.yaml` (ScriptContent only), `csharp-script-file.yaml` (ScriptPath only), `csharp-script-both.yaml` (negative — engine rejects).
- Tests including the `.superRefine` negative path.

### Acceptance criteria
- [x] `scriptPath`/`scriptContent` both default to `""` matching engine.
- [x] ONE-of validation catches `{ scriptPath: '', scriptContent: '' }`.
- [x] BOTH populated is a warning, not an error, in draft mode; becomes an error in strict publish mode.
- [x] Engine's `Validate()` behaviour (both-empty-throws) matches the Zod superRefine assertion.

### Unit tests
1. `CSharpScriptSchema_InlineOnly_Parses`
2. `CSharpScriptSchema_FileOnly_Parses`
3. `CSharpScriptSchema_NeitherSet_Fails`
4. `CSharpScriptSchema_BothSet_DraftMode_Warns`
5. `CSharpScriptSchema_BothSet_StrictMode_Fails`
6. `CSharpScriptSchema_Defaults_EmptyStrings`
7. `CSharpScriptSchema_ScriptPathNotString_Fails`
8. `CSharpScriptRoundTrip_Inline_EngineMatch`
9. `CSharpScriptRoundTrip_File_EngineMatch`
10. `CSharpScriptNode_Parity_2ExtraProps`

A/A/A for the superRefine case:

- `CSharpScriptSchema_NeitherSet_Fails`
  - Arrange: `{ runtimeType: 'CSharpScript', nodeId: 'n', joinType: 'Any', priority: 'Normal', maxConcurrentExecutions: 0 }` (both scripts default to `""`).
  - Act: `csharpScriptSchema.safeParse(input)`.
  - Assert: `success === false`, issue message includes "ScriptPath or ScriptContent".

### Integration tests
- 3 `TaskFamilyRoundTripTests` rows for `inline`, `file`, `both-engine-rejects`.

### Golden-file / parity tests
- **Golden:** both positive fixtures round-trip byte-match.
- **Negative golden:** `csharp-script-both.yaml` — engine `WorkflowLoader.Load(..., validateOnLoad: true)` THROWS; test asserts exception message.
- **Parity:** reflection on `CSharpScriptNodeDefinition` returns 2 extra props.

### Edge cases & negative tests
1. `scriptPath: "./script.cs"` with Unix-style path — round-trips verbatim.
2. `scriptContent` containing a string-literal `"..."` with embedded newlines — YAML block scalar (`|`) preserves.
3. Very long script content (100 KB) — parses but flagged by an optional warning hook.
4. `scriptContent: "\r\n"` — preserved (YAML literal block preserves line endings).
5. BOM at the start of `scriptContent` — preserved through engine; UI should offer to strip.
6. `scriptPath: null` — schema rejects (engine defaults to `""` not null).
7. Engine's `Validate()` thrown message changes wording — matched loosely in test.
8. Both `scriptPath` AND `scriptContent` set to non-empty — draft allowed; publish rejects.

### Observability hooks
- `WorkflowUi.ContractTests.TaskFamily.CSharpScript` — logs validation outcomes.
- A debug-level log emitted by the schema when scriptContent length > 50 KB.

### Risks & mitigations
- Engine `Validate()` wording drift — test uses `Assert.Contains` not `Assert.Equal`.
- YAML block scalar vs folded scalar — canonicaliser normalises to block scalar on re-emit.

### Evaluator gap-analysis prompts
1. Does the schema enforce "exactly one of" at strict-publish mode?
2. Is draft mode represented (both-empty allowed, both-set warned)?
3. Are two separate fixtures (inline vs file) round-trip-tested?
4. Is there a negative fixture proving engine `Validate()` throws?
5. Does the parity test pin the engine property count at 2?
6. Are multi-line and BOM content cases covered?

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

### Step 4 — `CSharpTask` kind

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 2.2.2

### Goal
Implement `CSharpTaskNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:1-42`:
- `ScriptContent?: string` (line 27, nullable)
- `ExecutorTypeName?: string` (line 32, nullable)
- `ExecutorAssemblyPath?: string` (line 37, nullable)

Unlike `CSharpScript`, all three are genuinely nullable. Engine doesn't require any one of them at load time; runtime binder decides. DTO mirrors this tolerance.

### Depends on
2.2.2.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62` — nullable task/executor fields and engine defaults.
- `src/WorkflowUI/src/schemas/node-base.schema.ts` from Step 2.1.1 — shared base-field/null semantics.
- `src/WorkflowUI/src/engine-contract.ts` from Stage 1.2 — discriminator + generated parity fixtures for `RuntimeType.CSharpTask`.

### Deliverables
- `src/WorkflowUI/src/schemas/node-csharp-task.schema.ts`:
  ```ts
  export const csharpTaskNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('CSharpTask'),
    scriptContent: z.string().nullable().optional(),
    executorTypeName: z.string().nullable().optional(),
    executorAssemblyPath: z.string().nullable().optional(),
  }).strict();
  ```
- Fixtures: `csharp-task-executor.yaml` (executor set, scriptContent null), `csharp-task-inline.yaml` (scriptContent set, executor null), `csharp-task-empty.yaml` (all three absent — draft).
- Tests: nullable handling across all 3 props + combinations.

### Acceptance criteria
- [x] All three properties nullable AND optional.
- [x] Draft fixture (all absent) parses and round-trips.
- [x] Engine reflection shows exactly these 3 extras.
- [x] `.strict()` rejects `executor_type_name` (snake_case).

### Unit tests
1. `CSharpTaskSchema_ExecutorOnly_Parses`
2. `CSharpTaskSchema_InlineOnly_Parses`
3. `CSharpTaskSchema_AllAbsent_Parses`
4. `CSharpTaskSchema_AllPresent_Parses` (engine allows)
5. `CSharpTaskSchema_ExtraKey_Strict_Fails`
6. `CSharpTaskSchema_SnakeCaseKey_Fails`
7. `CSharpTaskSchema_ScriptContentIsBoolean_Fails`
8. `CSharpTaskRoundTrip_Executor_Match`
9. `CSharpTaskRoundTrip_Inline_Match`
10. `CSharpTaskNode_Parity_3Extras`

A/A/A for the all-absent case:

- `CSharpTaskSchema_AllAbsent_Parses`
  - Arrange: base-only object with `runtimeType: 'CSharpTask'`.
  - Act: parse.
  - Assert: `parsed.scriptContent === undefined`; engine accepts the same shape.

### Integration tests
- 3 `TaskFamilyRoundTripTests` rows.

### Golden-file / parity tests
- 3 golden YAML files, each canonicalised-byte-match through engine round-trip.
- Parity test pins 3 extras.

### Edge cases & negative tests
1. Executor type with generic arguments (`Acme.Nodes.TaskNode`1[System.String]`) — preserved.
2. Null vs empty-string distinction — preserved per Stage 2.1.1.
3. Fixture with `scriptContent: ""` vs `scriptContent: null` — both valid, distinct.
4. Executor type in a nested namespace with 10 dots — preserved.
5. `executorAssemblyPath` with absolute Windows path (`C:\\tools\\x.dll`) — preserved; linter later flags portability.
6. All three set — parses; Phase 5 UI may warn about ambiguous config.

### Observability hooks
- Debug log when all three nullable props are absent (a draft node signal).

### Risks & mitigations
- Developer confusion about `CSharpScript` vs `CSharpTask` — both schemas have prominent header comments citing the engine source file.

### Evaluator gap-analysis prompts
1. Does the schema treat all 3 properties as nullable AND optional (distinct from Step 2.2.2)?
2. Is an all-absent draft fixture part of the round-trip suite?
3. Is the parity count pinned at 3?
4. Does a test prove `.strict()` rejects snake_case keys?
5. Are null vs empty-string states separately tested?

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

### Step 5 — `PowerShell` kind (script-file)

**Status:** [ ]
**Effort:** 1.5 d
**Depends on:** 2.2.3

### Goal
Implement `PowerShellScriptNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:1-48`:
- `RuntimeType` => `RuntimeType.PowerShell` (NOT `PowerShellScript` — a deliberate engine naming compression to keep enum count at 13).
- `ScriptPath: string` (line 27, non-nullable, default `""`).
- `RequiredModules?: List<string>` (line 34).
- `ModulePaths?: List<string>` (line 41).

### Depends on
2.2.3.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:12-51` — required `ScriptPath`, nullable `RequiredModules`, and nullable `ModulePaths`.
- `src/ExecutionEngine/Enums/RuntimeType.cs:13-29` — confirms the discriminator is `PowerShell` (not `PowerShellScript`).
- `src/WorkflowUI/src/schemas/node-base.schema.ts` from Step 2.1.1 — shared base DTO fields reused by the schema.

### Deliverables
- `src/WorkflowUI/src/schemas/node-powershell.schema.ts`:
  ```ts
  export const powerShellNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('PowerShell'),
    scriptPath: z.string().default(''),
    requiredModules: z.array(z.string()).nullable().optional(),
    modulePaths: z.array(z.string()).nullable().optional(),
  }).strict();
  ```
- Fixture `powershell-script-minimal.yaml` (scriptPath + empty modules).
- Fixture `powershell-script-with-modules.yaml` (populated `requiredModules` + `modulePaths`).
- Fixture `powershell-script-empty-path.yaml` (scriptPath: `""`) — engine loads; runtime binder fails; DTO parses.

### Acceptance criteria
- [x] Discriminator literal is exactly `'PowerShell'` (not `'PowerShellScript'`).
- [x] `scriptPath` non-nullable; defaults to `""`.
- [x] Both module lists nullable AND optional.
- [x] Arrays of strings enforced (no `z.unknown()`).
- [x] `.strict()` rejects `script_path`.
- [x] Round-trip preserves module list order.
- [x] Engine reflection yields exactly 3 extras.

### Unit tests
1. `PowerShellSchema_MinimalScriptPath_Parses`
2. `PowerShellSchema_WithBothModuleLists_Parses`
3. `PowerShellSchema_RequiredModulesNull_Parses`
4. `PowerShellSchema_ScriptPathNullRejected` (non-nullable)
5. `PowerShellSchema_RequiredModulesNonArray_Fails`
6. `PowerShellSchema_ModulePathsItemBoolean_Fails`
7. `PowerShellSchema_RuntimeTypePowerShellScript_Fails` (must be `PowerShell` exactly)
8. `PowerShellSchema_ExtraKey_Strict_Fails`
9. `PowerShellRoundTrip_WithModules_Order_Preserved`
10. `PowerShellNode_Parity_3Extras`
11. `PowerShellSchema_EmptyPath_Parses_RuntimeTestedElsewhere`

A/A/A for the runtime-type guard:

- `PowerShellSchema_RuntimeTypePowerShellScript_Fails`
  - Arrange: fixture with `runtimeType: 'PowerShellScript'`.
  - Act: `powerShellNodeSchema.safeParse(fixture)`.
  - Assert: `success === false` with literal-mismatch issue.

### Integration tests
- 3 `TaskFamilyRoundTripTests` rows.

### Golden-file / parity tests
- **Golden:** 3 YAML fixtures; canonicalised byte-match after engine round-trip.
- **Module-order parity:** `PowerShellRoundTrip_WithModules_Order_Preserved` asserts exact array-order preservation through YamlDotNet (known issue: some YAML serialisers sort keys, but array order is always preserved — guarded).

### Edge cases & negative tests
1. `requiredModules: []` — legal; distinct from `null`.
2. `requiredModules: ['Az','Az']` — duplicates allowed (engine doesn't dedupe).
3. Module names with version pins (`Az@1.2.3`) — preserved verbatim.
4. `modulePaths` containing absolute Windows path with spaces — preserved.
5. UNC path `\\\\share\\modules` in `modulePaths` — preserved.
6. `scriptPath: ""` — parses; engine loader tolerates; runtime binder errors; Phase 5 UI warns.
7. Missing `scriptPath` entirely — schema applies default `""`.

### Observability hooks
- Category `WorkflowUi.ContractTests.TaskFamily.PowerShell` — logs module list size.

### Risks & mitigations
- Engine enum name (`PowerShell` vs `PowerShellScript`) drift — dedicated test + parity via `engine-enums.json`.
- YAML array order reordering — never happens with YamlDotNet default sequence style; asserted.

### Evaluator gap-analysis prompts
1. Is the discriminator literal exactly `'PowerShell'` matching engine enum `RuntimeType.PowerShell`?
2. Are both module list properties nullable AND optional AND typed as `string[]`?
3. Is `scriptPath` non-nullable matching engine (unlike `PowerShellTask`)?
4. Does a test prove module array order is preserved through engine round-trip?
5. Does a test prove empty arrays and null arrays are distinct?
6. Is there a parity test pinning 3 extras?

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

### Step 6 — `PowerShellTask` kind

**Status:** [ ]
**Effort:** 1.5 d
**Depends on:** 2.2.4a

### Goal
Implement `PowerShellTaskNodeDefinition` per `src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:1-52`:
- `ScriptPath?: string` — declared `public string? ScriptPath { get; set; } = string.Empty;` (line 27). Nullable TYPE but engine initialises to `""`. This is a code smell we model faithfully: the DTO accepts `null`, `""`, and real strings. The mapper in Phase 5 MUST preserve round-trip.
- `ScriptContent?: string` (line 32, nullable).
- `RequiredModules?: List<string>` (line 39).
- `ModulePaths?: List<string>` (line 46).

### Depends on
2.2.4a.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:12-57` — file/content/module field surface for the task variant.
- `src/ExecutionEngine/Enums/RuntimeType.cs:13-29` — authoritative `PowerShellTask` discriminator.
- `src/WorkflowUI/src/schemas/node-base.schema.ts` from Step 2.1.1 — base-field inheritance and null/absent handling.

### Deliverables
- `src/WorkflowUI/src/schemas/node-powershell-task.schema.ts`:
  ```ts
  export const powerShellTaskNodeSchema = nodeBaseSchema.extend({
    runtimeType: z.literal('PowerShellTask'),
    scriptPath: z.string().nullable().optional(),
    scriptContent: z.string().nullable().optional(),
    requiredModules: z.array(z.string()).nullable().optional(),
    modulePaths: z.array(z.string()).nullable().optional(),
  }).strict();
  ```
- Fixtures: `powershell-task-inline.yaml` (scriptContent only), `powershell-task-filepath.yaml` (scriptPath only), `powershell-task-engine-quirk.yaml` (scriptPath: `""` — verifies engine emits `""` not `null`).
- Tests pinning the engine quirk.

### Acceptance criteria
- [x] `scriptPath` nullable (both type and runtime states).
- [x] Preserves null vs `""` distinction.
- [x] Engine emits `""` for a default-constructed node; fixture `powershell-task-engine-quirk.yaml` asserts this as a known quirk.
- [x] 4 extras in parity reflection.
- [x] `.strict()` rejects extras.

### Unit tests
1. `PowerShellTaskSchema_InlineScriptContent_Parses`
2. `PowerShellTaskSchema_FilePath_Parses`
3. `PowerShellTaskSchema_ScriptPathNull_Parses`
4. `PowerShellTaskSchema_ScriptPathEmptyString_Parses`
5. `PowerShellTaskSchema_AllFourAbsent_Parses` (pure draft)
6. `PowerShellTaskSchema_ExtraKey_Strict_Fails`
7. `PowerShellTaskSchema_ModulesNonArray_Fails`
8. `PowerShellTaskRoundTrip_Inline_Match`
9. `PowerShellTaskRoundTrip_EngineQuirk_EmitsEmptyString` (pins the code-smell)
10. `PowerShellTaskNode_Parity_4Extras`

A/A/A for the quirk pin:

- `PowerShellTaskRoundTrip_EngineQuirk_EmitsEmptyString`
  - Arrange: `var def = new PowerShellTaskNodeDefinition { NodeId = "n" };` — constructor leaves `ScriptPath` at default `""`.
  - Act: serialise via `WorkflowSerializer.ToYaml`; deserialise Zod; re-serialise.
  - Assert: the YAML contains `scriptPath: ''` (empty string), NOT `scriptPath: null`. Documents the quirk so a future engine fix is caught.

### Integration tests
- 3 `TaskFamilyRoundTripTests` rows.

### Golden-file / parity tests
- **Golden:** 3 fixtures round-trip.
- **Quirk lock:** `powershell-task-engine-quirk.yaml` — if engine ever fixes the initialiser to `null`, this test fails and the schema + fixture update together.
- **Parity:** 4 extras.

### Edge cases & negative tests
1. Both `scriptPath` and `scriptContent` set — allowed; Phase 5 UI may warn.
2. Both empty — allowed as draft.
3. `scriptContent` with embedded `$env:FOO` — preserved.
4. Module name including `-RequiredVersion` notation — preserved.
5. ModulePath pointing to repository-relative path — preserved verbatim.
6. 0-length modules arrays distinct from null.

### Observability hooks
- Debug log when `scriptPath === ''` AND `scriptContent == null` — a signal the node is mis-configured.

### Risks & mitigations
- Engine quirk (`string? = ""`) could be fixed upstream — our quirk-lock test fails first, giving visibility before drift.
- Large scriptContent (PowerShell often has long scripts) — no size limit imposed, but a warning hook similar to Stage 2.2.2 is reused.

### Evaluator gap-analysis prompts
1. Does the schema model `scriptPath` as nullable to match the engine's nullable type annotation?
2. Does a dedicated test pin the engine's default `""` initialiser behaviour?
3. Are 4 extras reflected AND the parity test pins that count?
4. Does the fixture suite include inline-only, file-only, and draft (all-absent) variants?
5. Is null vs empty-string distinguishable through round-trip?
6. Does the `.strict()` guard reject snake_case and typos?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all 6 task-family kinds replace their Stage 2.1.2 placeholders.
