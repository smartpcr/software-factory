# Stage 0.2 — Parity harness

**Phase:** Phase 0 — Foundations & Gates
**Stage status:** [ ]
**Total effort:** 6 d
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Stage 0.2 implements the **engine-parity quality gate** described in master plan [§0.2](../../workflow-ui-plan.md#02-quality-gates-global-before-any-phase-can-start). It is the single most load-bearing stage of Phase 0: if this harness is weak, every subsequent UI DTO can silently drift from the engine and never be caught. The harness has three parts: (0.2.1) enum parity across the 7 public enums, (0.2.2) DTO parity across every `*NodeDefinition.cs` plus the nested `RetryPolicy` / `CircuitBreakerPolicy` shapes, and (0.2.3) a YAML round-trip golden test that forces the UI to use engine `WorkflowLoader` / `WorkflowSerializer` (both in `src/ExecutionEngine/Workflow/`) rather than forking YAML handling.

The ground-truth sources this stage must reflect are, verbatim and with line-number citations:

- `src/ExecutionEngine/Enums/RuntimeType.cs` — 13 members (`Noop, CSharp, CSharpScript, CSharpTask, PowerShell, PowerShellTask, IfElse, ForEach, While, Switch, Subflow, Timer, Container`).
- `src/ExecutionEngine/Enums/NodeExecutionStatus.cs` — 6 members (`Pending, Running, Completed, Failed, Cancelled, Skipped`).
- `src/ExecutionEngine/Enums/WorkflowExecutionStatus.cs` — 6 members (`Pending, Running, Completed, Failed, Cancelled, Paused`).
- `src/ExecutionEngine/Enums/MessageType.cs` — 5 members (`Complete, Fail, Progress, Next, Cancel`).
- `src/ExecutionEngine/Enums/NodePriority.cs` — 3 members (`Low=0, Normal=1, High=2`).
- `src/ExecutionEngine/Enums/JoinType.cs` — 2 members (`Any=0, All=1`).
- `src/ExecutionEngine/Enums/ExecutionMode.cs` — 2 members (`Sequential, Parallel`).
- `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs` — base 11 properties (`NodeId, NodeName, Description, Configuration, JoinType, Priority, MaxConcurrentExecutions, RetryPolicy, CircuitBreakerPolicy, CompensationNodeId, FallbackNodeId`) plus abstract `RuntimeType`.
- `src/ExecutionEngine/Policies/RetryPolicy.cs` — 7 properties (`Strategy, MaxAttempts, InitialDelay, MaxDelay, Multiplier, RetryOn, DoNotRetryOn`).
- `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs` — 4 properties (`FailureThreshold, MinimumThroughput, OpenDuration, HalfOpenSuccesses`).
- All 13 `*NodeDefinition.cs` siblings listed in master plan §0.2.2.
- `src/ExecutionEngine/Workflow/WorkflowLoader.cs` and `WorkflowSerializer.cs`.

This stage unblocks Phase 1 (§7) and is a hard merge-blocker for Phase 2 (§8) per [§3 Quality Gate](../../workflow-ui-plan.md#3-quality-gate-per-step).

## Prerequisites
- Stage 0.1 is complete (all three skeleton projects build).
- `src/WorkflowUi.ContractTests/` exists.
- `client/src/engine-contract.generated.ts` stub exists.

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 0.2 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

Reflection + parity
- `src/WorkflowUi.ContractTests/Reflection/EngineReflection.cs` (new) — shared reflection helpers.
- `src/WorkflowUi.ContractTests/Reflection/NullabilityProbe.cs` (new) — thin wrapper around `System.Reflection.NullabilityInfoContext`.
- `src/WorkflowUi.ContractTests/EnumParityTests.cs` (new, Step 0.2.1).
- `src/WorkflowUi.ContractTests/DtoParityTests.cs` (new, Step 0.2.2).
- `src/WorkflowUi.ContractTests/PolicyParityTests.cs` (new, Step 0.2.2) — retry + circuit-breaker nested shapes.
- `src/WorkflowUi.ContractTests/ConnectionParityTests.cs` (new, Step 0.2.2) — `NodeConnection` 9 properties.
- `src/WorkflowUi.ContractTests/YamlRoundTripTests.cs` (new, Step 0.2.3).
- `src/WorkflowUi.ContractTests/Helpers/CanonicalYaml.cs` (new, Step 0.2.3) — canonicaliser (strips comments, normalises line endings, preserves engine key order).

Generated + fixture
- `client/src/engine-contract.generated.ts` (modified) — Stage 0.2.2 writes a checked-in version; Stage 1.2 regenerates.
- `client/src/__fixtures__/engine-enums.json` (new) — sorted ordinal fixture regenerated by the C# test; committed.
- `client/src/__fixtures__/engine-dto-shape.json` (new) — property-name + nullability fixture.
- `src/WorkflowUi.ContractTests/Fixtures/noop-minimal.yaml` (new, Step 0.2.3).
- `src/WorkflowUi.ContractTests/Fixtures/noop-minimal.canonical.yaml` (new) — the golden canonical form.

Cross-language
- `client/src/__tests__/enum-parity.test.ts` (new, Step 0.2.1) — a TS-side parity facing the JSON fixture.

## Shared fixtures / fakes
- `engine-enums.json` and `engine-dto-shape.json` are the ONLY bridge between the C# reflection test and the TypeScript contract. Both are emitted by `EnumParityTests` / `DtoParityTests` when the `WORKFLOWUI_WRITE_FIXTURES=1` env var is set, and committed to source control so `npm test` can consume them without a .NET install.
- `noop-minimal.yaml` is the first member of a growing YAML corpus at `src/WorkflowUi.ContractTests/Fixtures/`; Phase 2 adds one fixture per node kind.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="0.2"`, `phaseId="0"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-0.2.plan-read`, `workflow-ui.stage-0.2.apply`, `workflow-ui.stage-0.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_0_2_started_total`, counter `workflow_ui_stage_0_2_failed_total`, histogram `workflow_ui_stage_0_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| toolchain bootstrap | SDK / Node version drift | build or setup script exits non-zero | pin versions from `global.json` / lockfile and fail fast |
| parity harness | fixture/schema drift | contract tests turn red | regenerate fixture, review diff, rerun parity |
| CI workflow | required job removed or renamed | meta-tests / GitHub run summary fail | restore named job and re-run workflow |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| dotnet build / npm build | 120 s | 180 s |
| parity / contract test suite | 20 s | 60 s |
| pre-commit no-op | 5 s | 10 s |

## Security callouts
- Pin setup actions/packages to reviewed versions; never use `@main`/`@master` in workflow deliverables.
- Do not log secrets or PATs in bootstrap scripts; scrub command echoes and CI summaries.
- Treat generated contract files as untrusted input until parity tests re-validate them.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 0.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage02` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

## Step 0.2.1 — Enum parity (7 enums)

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 0.1.1

### Goal
Author a reflection-based xUnit fixture that enumerates the 7 public enums listed in master plan §0.2.1 and asserts (a) member-name set equality, (b) ordinal value equality for enums whose order is semantically meaningful (`NodePriority`, `JoinType`, `ExecutionMode`, `RetryStrategy` implied for later reuse), and (c) emits a stable-sorted JSON fixture consumed by a mirror TS-side test. The fixture enforces that adding an enum member on the engine side will fail CI red until both the committed JSON fixture and the `engine-contract.generated.ts` TS union literal are updated. This step does NOT implement the TS generator — that lands in Stage 1.2 — but it MUST produce a failing red state when drift is introduced, because every downstream gate depends on it.

### Depends on
0.1.1.

### Inputs
- `src/ExecutionEngine/Enums/*.cs` (7 files).
- Blank `client/src/engine-contract.generated.ts` stub.

### Deliverables
- `src/WorkflowUi.ContractTests/Reflection/EngineReflection.cs` (new) — static helpers: `IEnumerable<Type> AllPublicEnums()`, `IReadOnlyList<string> MembersOf(Type)`, `IReadOnlyList<int> OrdinalsOf(Type)`.
- `src/WorkflowUi.ContractTests/EnumParityTests.cs` (new) — class `EnumParityTests` with `[Trait("Category", "EnumParity")]` at class level; per-enum `[Theory]`/`[InlineData]` and set-equality `[Fact]`s.
- `client/src/__fixtures__/engine-enums.json` (new) — shape `{ RuntimeType: { members: [...], ordinals: [...] }, NodeExecutionStatus: {...}, ... }`; deterministic ordering: enums alphabetical, members in source-declaration order.
- `client/src/__tests__/enum-parity.test.ts` (new) — imports the fixture, asserts `RuntimeType` array length === 13, each member string is a valid TS string literal, etc.
- `docs/workflow-ui/phase-0/stage-0.2-parity-harness.md` (this file, cross-referenced from the plan).

### Acceptance criteria
- [ ] `dotnet test src/WorkflowUi.ContractTests --filter Category=EnumParity` passes on HEAD.
- [ ] Adding a dummy member to `RuntimeType.cs` (local-only experiment) makes the `RuntimeType_Members_MatchFixture` fact fail red.
- [ ] `engine-enums.json` is byte-stable across runs (sorted by `Enum.GetValues` order, not by `Enum.GetNames` alphabetic order).
- [ ] `NodePriority` ordinals are asserted as `Low=0, Normal=1, High=2` — the plan explicitly forbids a `Critical` member ([§0.2.1](../../workflow-ui-plan.md#021-engine-parity-tests-critical-blocker-for-everything-else)).
- [ ] `JoinType` ordinals are asserted as `Any=0, All=1`.
- [ ] `ExecutionMode` has exactly 2 members: `Sequential, Parallel` (per `src/ExecutionEngine/Enums/ExecutionMode.cs`).
- [ ] `MessageType` has exactly 5 members, values include `Progress` (per `src/ExecutionEngine/Enums/MessageType.cs:27`), excluding any synthetic `Skipped` / `Paused` message.
- [ ] TS-side `enum-parity.test.ts` imports the fixture via `import fixture from '../__fixtures__/engine-enums.json'` with `resolveJsonModule: true`.
- [ ] A `[Fact]` asserts `Enum.GetValues<NodePriority>().Length == 3` AND that no member name equals `"Critical"` (defense-in-depth against v1.0 regression).

### Unit tests
1. `EnumParityTests_RuntimeType_Has13Members_ExactNames`
2. `EnumParityTests_RuntimeType_Ordinals_MatchSourceDeclarationOrder`
3. `EnumParityTests_NodeExecutionStatus_Has6Members_ExactNames`
4. `EnumParityTests_WorkflowExecutionStatus_Has6Members_ExactNames_IncludingPaused`
5. `EnumParityTests_MessageType_Has5Members_IncludingProgressAndNext`
6. `EnumParityTests_NodePriority_Has3Members_NoCritical`
7. `EnumParityTests_NodePriority_Ordinals_AreZeroOneTwo`
8. `EnumParityTests_JoinType_Ordinals_AreZeroOne`
9. `EnumParityTests_ExecutionMode_HasSequentialAndParallel`
10. `EnumParityTests_FixtureFile_ByteStable_Across2Runs` — runs the generator twice, asserts identical bytes.
11. `EnumParityTest_TypeScript_RuntimeType_Has13Members` (vitest).

A/A/A sketch for the three most important:

- `EnumParityTests_RuntimeType_Has13Members_ExactNames`
  - Arrange: `var expected = new[] { "Noop","CSharp","CSharpScript","CSharpTask","PowerShell","PowerShellTask","IfElse","ForEach","While","Switch","Subflow","Timer","Container" };`
  - Act: `var actual = Enum.GetNames<ExecutionEngine.Enums.RuntimeType>();`
  - Assert: `Assert.Equal(expected, actual);` — order matters, matches source-declaration order per `src/ExecutionEngine/Enums/RuntimeType.cs:17-78`.
- `EnumParityTests_NodePriority_Has3Members_NoCritical`
  - Arrange: `var names = Enum.GetNames<NodePriority>();`
  - Act: `var hasCritical = names.Contains("Critical");`
  - Assert: `Assert.Equal(3, names.Length); Assert.False(hasCritical);` — enforces v1.1 changelog fix.
- `EnumParityTests_FixtureFile_ByteStable_Across2Runs`
  - Arrange: invoke `EmitEnumFixture()` twice into two byte arrays.
  - Act: compare.
  - Assert: `Assert.Equal(bytes1, bytes2);`

### Integration tests
None — the generator helper is invoked in-process by the tests themselves. CI invocation is covered in Stage 0.3.

### Golden-file / parity tests
- `client/src/__fixtures__/engine-enums.json` is the golden. Diff contract: any member add/remove/rename or ordinal shift must be accompanied by a one-line change in the committed JSON; CI fails if the emitted file differs from the committed copy.
- Diff tool: C# test uses `Assert.Equal(File.ReadAllText(goldenPath), EmitEnumFixture())` with `StringComparison.Ordinal` after normalising line endings to `\n`.

### Edge cases & negative tests
1. An engine PR renames `RuntimeType.Container` to `RuntimeType.Group` — golden mismatch red; PR must regenerate fixture.
2. An engine PR adds `NodePriority.Critical` — `EnumParityTests_NodePriority_Has3Members_NoCritical` fails red.
3. An engine PR flips `JoinType.Any=0` to `JoinType.Any=1` — `EnumParityTests_JoinType_Ordinals_AreZeroOne` fails.
4. The committed fixture contains an extra phantom enum (e.g., `FooEnum`) — the set-equality test fails because `AllPublicEnums()` returns only engine types.
5. A contributor edits `engine-enums.json` by hand — next test run regenerates and diff-fails if the hand edit was wrong.
6. Line-ending drift (CRLF in the committed file, LF emitted) — `CanonicalYaml` equivalent for JSON normalises before compare.
7. A new enum `CheckpointFrequency` (already exists in `src/ExecutionEngine/Enums/CheckpointFrequency.cs`) is present — the test MUST decide explicitly whether to include it; current decision: out-of-scope for UI parity, added to an explicit exclude list documented in `EngineReflection.cs`.

### Observability hooks
- Log category `WorkflowUi.ContractTests.EnumParity` — emitted at `Information` when generator writes fixture; fields `fixturePath`, `byteLength`, `enumCount`.
- No metrics.

### Risks & mitigations
- Alphabetical vs source-declaration order ambiguity — `EngineReflection.MembersOf(Type)` explicitly uses `Enum.GetValues` (source order) not `Enum.GetNames` (alphabetic on some runtimes).
- `CheckpointFrequency.cs` and similar internal enums leak into reflection — guarded by explicit allow-list.
- Test runs on Linux CI produce LF line endings in the fixture; Windows dev re-generates CRLF — mitigated by writing fixture as LF unconditionally.

### Evaluator gap-analysis prompts
1. Does `EnumParityTests` assert the exact 13-member list for `RuntimeType` in source-declaration order?
2. Is there a dedicated `[Fact]` that rejects a `NodePriority.Critical` member (defense-in-depth for v1.1 fix)?
3. Does the test assert `JoinType.Any == 0` and `JoinType.All == 1` by ordinal, not by name only?
4. Is `client/src/__fixtures__/engine-enums.json` committed, LF-normalised, and consumed by at least one vitest case?
5. Does `EngineReflection.AllPublicEnums()` use an explicit allow-list so `CheckpointFrequency` / internal enums don't silently enter the fixture?
6. Does the generator produce byte-stable output across two consecutive invocations?
7. Is the `EnumParity` category on the test class (for `--filter Category=EnumParity` targeting in CI)?
8. Is `MessageType.Progress` asserted present (v1.1 and v1.3 fixes require it) and `MessageType.MessageSent` NOT present?

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

## Step 0.2.2 — DTO parity (base + 13 kinds + 2 policies + connection)

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 0.2.1

### Goal
Extend the reflection harness to cover every `*NodeDefinition.cs` class plus the nested `RetryPolicy` / `CircuitBreakerPolicy` and the `NodeConnection` shape. For each class, assert: (a) the set of public instance property names matches a checked-in fixture; (b) the CLR type name of each property matches; (c) nullability (reference types, `Nullable<T>`) matches the TS fixture's optionality marker. Emit a stable-sorted JSON fixture at `client/src/__fixtures__/engine-dto-shape.json`. The test MUST use `System.Reflection.NullabilityInfoContext` so that `string?` and `string` are distinguished. This is the fixture that the Stage 1.2 generator will later consume to produce `engine-contract.generated.ts`.

Per master plan [§0.2.2](../../workflow-ui-plan.md#022-dto-parity-tests), retry/circuit-breaker are nested on the base `NodeDefinition` (not on each kind); both are nullable where `null == policy disabled`. The TS parity must preserve `null` vs empty-object distinction (§2.2 of the plan).

### Depends on
0.2.1.

### Inputs
- `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs` + 13 sibling files.
- `src/ExecutionEngine/Policies/RetryPolicy.cs`.
- `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs`.
- `src/ExecutionEngine/Workflow/NodeConnection.cs`.

### Deliverables
- `src/WorkflowUi.ContractTests/Reflection/NullabilityProbe.cs` (new) — wraps `NullabilityInfoContext` with a stable API: `bool IsNullable(PropertyInfo)`.
- `src/WorkflowUi.ContractTests/DtoParityTests.cs` (new) — one `[Theory]` parameterised over `typeof(NoopNodeDefinition)` through `typeof(ContainerNodeDefinition)`; per-type `[Fact]` entries for the three with the most specific shape (`SubflowNodeDefinition`, `ContainerNodeDefinition`, `PowerShellTaskNodeDefinition`).
- `src/WorkflowUi.ContractTests/PolicyParityTests.cs` (new) — facts for `RetryPolicy` and `CircuitBreakerPolicy`.
- `src/WorkflowUi.ContractTests/ConnectionParityTests.cs` (new) — one `[Fact]` asserting the 9 properties on `NodeConnection` from `src/ExecutionEngine/Workflow/NodeConnection.cs:17-69`.
- `client/src/__fixtures__/engine-dto-shape.json` (new) — schema shape:
  ```json
  {
    "NodeDefinition": { "properties": [ { "name": "NodeId", "type": "String", "nullable": false }, ... ] },
    "CSharpNodeDefinition": { "baseOf": "NodeDefinition", "properties": [ ... ] },
    ...
  }
  ```
- `client/src/engine-contract.generated.ts` (modified) — promoted from empty stub to hand-maintained skeleton (will become generator output in Stage 1.2).

### Acceptance criteria
- [ ] `dotnet test src/WorkflowUi.ContractTests --filter Category=DtoParity` passes.
- [ ] `NodeDefinition` parity asserts 11 instance properties (list per `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:27-95`): `NodeId, NodeName, Description, Configuration, JoinType, Priority, MaxConcurrentExecutions, RetryPolicy, CircuitBreakerPolicy, CompensationNodeId, FallbackNodeId`. (Note: the master plan's §2.1.1 sentence "9 base properties" undercounts by 2; this test must defend the true count of 11 — see discrepancy note in stage context.)
- [ ] `RetryPolicy` parity asserts 7 properties: `Strategy, MaxAttempts, InitialDelay, MaxDelay, Multiplier, RetryOn, DoNotRetryOn` (per `src/ExecutionEngine/Policies/RetryPolicy.cs:40-83`).
- [ ] `CircuitBreakerPolicy` parity asserts 4 properties: `FailureThreshold, MinimumThroughput, OpenDuration, HalfOpenSuccesses` (per `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:34-54`).
- [ ] `NodeConnection` parity asserts the 9 properties: `SourceNodeId, TargetNodeId, TriggerMessageType, Condition, IsEnabled, Metadata, Priority, SourcePort, TargetPort`.
- [ ] Nullability is asserted for each property using `NullabilityInfoContext`: e.g., `Description` is nullable; `NodeId` is not nullable; `RetryPolicy` (on base) is nullable; `Configuration` is nullable.
- [ ] `PowerShellScriptNodeDefinition` parity asserts `ScriptPath` as NON-nullable (engine default `string.Empty`) and `RequiredModules`/`ModulePaths` as nullable, reflecting the source per `PowerShellScriptNodeDefinition.cs`.
- [ ] `SubflowNodeDefinition` parity enumerates exactly: `WorkflowFilePath, WorkflowDefinition, InputMappings, OutputMappings, Timeout, SkipValidation` — matching `SubflowNodeDefinition.cs`.
- [ ] `ContainerNodeDefinition` parity asserts `ExecutionMode` is non-nullable and defaults to `Sequential`.
- [ ] `engine-dto-shape.json` is byte-stable across runs.

### Unit tests
1. `DtoParityTests_NodeDefinition_Has11PublicProperties`
2. `DtoParityTests_NoopNodeDefinition_HasZeroExtraProperties`
3. `DtoParityTests_CSharpNodeDefinition_Has_AssemblyPath_And_TypeName`
4. `DtoParityTests_CSharpScriptNodeDefinition_Has_ScriptPath_And_ScriptContent_Both_NonNullable`
5. `DtoParityTests_CSharpTaskNodeDefinition_Has_3NullableProperties`
6. `DtoParityTests_PowerShellScriptNodeDefinition_ScriptPath_IsNonNullable`
7. `DtoParityTests_PowerShellTaskNodeDefinition_ScriptPath_IsNullable`
8. `DtoParityTests_IfElseNodeDefinition_OnlyCondition` — explicitly forbids any `TrueBranch`/`FalseBranch` leakage.
9. `DtoParityTests_SwitchNodeDefinition_HasExpressionAndCases`
10. `DtoParityTests_ForEachNodeDefinition_HasCollectionExpressionAndItemVariableName_WithDefault_item`
11. `DtoParityTests_WhileNodeDefinition_MaxIterations_DefaultIs1000`
12. `DtoParityTests_SubflowNodeDefinition_HasExactly6Properties_AndTimeoutDefaultIs5Minutes`
13. `DtoParityTests_TimerNodeDefinition_OnlySchedule`
14. `DtoParityTests_ContainerNodeDefinition_HasExactly3Properties_AndExecutionModeDefaultIsSequential`
15. `PolicyParityTests_RetryPolicy_Has7Properties`
16. `PolicyParityTests_RetryPolicy_MaxAttemptsDefault_Is3`
17. `PolicyParityTests_CircuitBreakerPolicy_Has4Properties`
18. `PolicyParityTests_CircuitBreakerPolicy_OpenDurationDefault_Is30Seconds`
19. `ConnectionParityTests_NodeConnection_Has9PublicProperties_InExactShape`
20. `DtoParityTests_FixtureFile_ByteStable_Across2Runs`

A/A/A sketch for the three most important:

- `DtoParityTests_NodeDefinition_Has11PublicProperties`
  - Arrange: `var expected = new[] { "NodeId","NodeName","Description","Configuration","JoinType","Priority","MaxConcurrentExecutions","RetryPolicy","CircuitBreakerPolicy","CompensationNodeId","FallbackNodeId" };`
  - Act: `var actual = typeof(NodeDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name).ToArray();`
  - Assert: set-equals `expected`, and also assert `RuntimeType` is abstract on the base and excluded from the DeclaredOnly set when filtered to non-abstract.
- `ConnectionParityTests_NodeConnection_Has9PublicProperties_InExactShape`
  - Arrange: `var expected = new[] { "SourceNodeId","TargetNodeId","TriggerMessageType","Condition","IsEnabled","Metadata","Priority","SourcePort","TargetPort" };`
  - Act: `var actual = typeof(NodeConnection).GetProperties(...).Select(p=>p.Name).ToArray();`
  - Assert: `Assert.Equal(expected.OrderBy(x=>x), actual.OrderBy(x=>x));` — per `src/ExecutionEngine/Workflow/NodeConnection.cs:21-69`.
- `PolicyParityTests_RetryPolicy_Has7Properties`
  - Arrange: expected `{ "Strategy","MaxAttempts","InitialDelay","MaxDelay","Multiplier","RetryOn","DoNotRetryOn" }`.
  - Act: reflect.
  - Assert: set-equals.

### Integration tests
None at this step; CI integration wiring is Stage 0.3.

### Golden-file / parity tests
- **Golden:** `client/src/__fixtures__/engine-dto-shape.json`.
- **Diff contract:** property-set equality per DTO; nullability flag per property; property-order independent (sorted alphabetically in the fixture); engine-assembly version embedded as a `$engineAssemblyVersion` sidecar field so mismatched engine builds fail loudly.
- **Fail-loud behaviour:** if the engine adds a property on an existing DTO without updating the fixture, the parity test prints a diff like `[NodeDefinition] missing in fixture: NewlyAddedProperty (String?)`.

### Edge cases & negative tests
1. Engine adds a new property `NodeDefinition.Owner` — test fails with a "missing in fixture" diff.
2. Engine marks `Description` as non-nullable — nullability probe fails.
3. Engine renames `CSharpNodeDefinition.AssemblyPath` to `AssemblyUri` — set-equality fails.
4. Engine introduces a 14th `RuntimeType` (e.g., `Lambda`) without adding a matching `*NodeDefinition.cs` — meta-test in `DtoParityTests_AllRuntimeTypes_HaveAMatchingDefinitionClass` catches.
5. A `*NodeDefinition.cs` uses a non-public setter — parity test must still enumerate the property; ensure `BindingFlags.Public | BindingFlags.Instance` + optional `GetSetMethod(nonPublic:true)` accepted.
6. `Configuration` type drift (from `Dictionary<string, object>?` to `Dictionary<string, JsonElement>?`) — type-name comparison uses `FullName`, so a change is caught.
7. `NodeConnection.IsEnabled` default changes from `true` to `false` — a dedicated fact `ConnectionParityTests_NodeConnection_IsEnabled_DefaultsTrue` catches.
8. Someone adds a property on `PowerShellScriptNodeDefinition` without rerunning the fixture generator — byte-stability test red.

### Observability hooks
- Log category `WorkflowUi.ContractTests.DtoParity` — emits fixture path, DTO-count, total-property-count.
- A failing test prints a `DiffReport` with missing/extra/type-mismatch entries.

### Risks & mitigations
- `NullabilityInfoContext` requires compiler-embedded metadata; `.csproj` of `ExecutionEngine` must set `<Nullable>enable</Nullable>` for this to work — already true in repo; documented in `NullabilityProbe.cs` preamble.
- Reflection over `BindingFlags.DeclaredOnly` might miss inherited properties on subclasses — mitigated by explicitly collecting declared-only + base separately and unioning.
- Dictionary property ordering — sort alphabetically in fixture.

### Evaluator gap-analysis prompts
1. Does `DtoParityTests` enumerate ALL 13 `*NodeDefinition` subclasses and exactly one base, totalling 14 reflection targets (plus 2 policies + 1 connection = 17 total shapes)?
2. Does `NullabilityProbe.IsNullable` use `NullabilityInfoContext.Create(property).WriteState == NullabilityState.Nullable`?
3. Does the fixture include `$engineAssemblyVersion` so an engine upgrade forces a visible refresh?
4. Does `ConnectionParityTests` assert all 9 `NodeConnection` properties in the exact shape from `src/ExecutionEngine/Workflow/NodeConnection.cs` with `IsEnabled` defaulting to `true`?
5. Does `PolicyParityTests` assert `RetryPolicy.MaxAttempts` default = 3 and `CircuitBreakerPolicy.OpenDuration` default = 30 seconds (matching `RetryPolicy.cs:28` and `CircuitBreakerPolicy.cs:25`)?
6. Is `IfElseNodeDefinition` parity explicitly REJECTING any `trueBranch`/`falseBranch` property — defending v1.1 fix?
7. Does a meta-fact assert that every `RuntimeType` value has at least one `*NodeDefinition` subclass with a matching `RuntimeType` override?
8. Does the fixture file use LF line endings unconditionally (to avoid Linux/Windows drift)?
9. Are the 11 base-node-definition properties (not 9) reflected — correcting the master plan §2.1.1 undercount?

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

## Step 0.2.3 — YAML round-trip golden

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 0.2.2

### Goal
Prove that the UI server will never fork YAML handling by asserting that `ExecutionEngine.Workflow.WorkflowLoader.Load(path)` followed by `ExecutionEngine.Workflow.WorkflowSerializer.ToYaml(workflow)` produces output byte-equal (after canonicalisation) to the committed golden. The canonicaliser (`CanonicalYaml.cs`) strips comments, normalises line endings to `\n`, trims trailing whitespace, and preserves the engine's key order (it does NOT re-sort keys). This test also performs a reflection probe to verify `WorkflowLoader` and `WorkflowSerializer` types exist in the engine assembly — defending [v1.3 changelog](../../workflow-ui-plan.md#changelog) which removed all `YamlWorkflowLoader` mentions.

### Depends on
0.2.2.

### Inputs
- `src/ExecutionEngine/Workflow/WorkflowLoader.cs` (public: `Load(string, bool)`, `Save`).
- `src/ExecutionEngine/Workflow/WorkflowSerializer.cs` (public: `ToYaml`, `FromYaml`, `ToJson`, `FromJson`, `SaveToFile`, `LoadFromFile`).
- First fixture: a Noop workflow.

### Deliverables
- `src/WorkflowUi.ContractTests/Fixtures/noop-minimal.yaml` (new) — a 2-node Noop workflow authored by hand, with a start→end Noop pair and a single `Complete` edge. Exact keys match `WorkflowDefinition` serialization.
- `src/WorkflowUi.ContractTests/Fixtures/noop-minimal.canonical.yaml` (new) — the canonicalised golden (no comments, LF, no trailing whitespace).
- `src/WorkflowUi.ContractTests/Helpers/CanonicalYaml.cs` (new) — static methods: `string Canonicalize(string yaml)`, `bool Equal(string a, string b)`.
- `src/WorkflowUi.ContractTests/YamlRoundTripTests.cs` (new) — class `YamlRoundTripTests` with `[Trait("Category", "YamlRoundTrip")]`.

### Acceptance criteria
- [ ] `dotnet test src/WorkflowUi.ContractTests --filter Category=YamlRoundTrip` passes.
- [ ] `YamlRoundTripTests_NoopMinimal_LoadAndSerialize_MatchesCanonicalGolden` passes.
- [ ] `YamlRoundTripTests_LoaderAndSerializer_ExistInEngineAssembly` asserts both types resolve via `Assembly.GetType("ExecutionEngine.Workflow.WorkflowLoader")` and `...WorkflowSerializer"` — defends against any future rename to `YamlWorkflowLoader`.
- [ ] Canonicalisation is deterministic: comments stripped, CRLF → LF, trailing spaces removed, tab → 4 spaces, BOM stripped.
- [ ] Engine key order is preserved — the test does NOT lexically sort YAML keys; any sort would be a false positive.
- [ ] The fixture is re-loadable: `var wf2 = loader.Load(fixturePath); var yaml2 = serializer.ToYaml(wf2); Canonicalize(yaml1) == Canonicalize(yaml2)`.
- [ ] If the canonical golden is deleted, the test fails red with a clear `missing golden` message (not a generic NRE).
- [ ] The test uses `WorkflowLoader` with `validateOnLoad: true` so the fixture must also pass engine validation.

### Unit tests
1. `YamlRoundTripTests_LoaderAndSerializer_ExistInEngineAssembly`
2. `YamlRoundTripTests_NoopMinimal_Load_Succeeds`
3. `YamlRoundTripTests_NoopMinimal_Serialize_ProducesNonEmpty`
4. `YamlRoundTripTests_NoopMinimal_LoadAndSerialize_MatchesCanonicalGolden`
5. `YamlRoundTripTests_NoopMinimal_DoubleRoundTrip_IsIdempotent` — load → serialize → reload → reserialize produces identical canonical output.
6. `YamlRoundTripTests_Canonicalizer_StripsComments`
7. `YamlRoundTripTests_Canonicalizer_NormalizesCRLFtoLF`
8. `YamlRoundTripTests_Canonicalizer_PreservesEngineKeyOrder` — input `name: A\nid: B` stays in that order after canonicalisation.
9. `YamlRoundTripTests_MissingGolden_FailsWithClearMessage`
10. `YamlRoundTripTests_Loader_ValidationFailure_ThrowsInvalidOperationException` — defends `WorkflowLoader.Load(path, validateOnLoad: true)` behaviour per `src/ExecutionEngine/Workflow/WorkflowLoader.cs:44-64`.

A/A/A sketch for the three most important:

- `YamlRoundTripTests_LoaderAndSerializer_ExistInEngineAssembly`
  - Arrange: `var asm = typeof(WorkflowDefinition).Assembly;`
  - Act: `var loader = asm.GetType("ExecutionEngine.Workflow.WorkflowLoader"); var ser = asm.GetType("ExecutionEngine.Workflow.WorkflowSerializer");`
  - Assert: both non-null; and there is NO type named `ExecutionEngine.Workflow.YamlWorkflowLoader`.
- `YamlRoundTripTests_NoopMinimal_LoadAndSerialize_MatchesCanonicalGolden`
  - Arrange: `var loader = new WorkflowLoader(); var ser = new WorkflowSerializer();`
  - Act: `var wf = loader.Load(fixturePath); var actual = CanonicalYaml.Canonicalize(ser.ToYaml(wf));`
  - Assert: `var expected = CanonicalYaml.Canonicalize(File.ReadAllText(goldenPath)); Assert.Equal(expected, actual);`
- `YamlRoundTripTests_NoopMinimal_DoubleRoundTrip_IsIdempotent`
  - Arrange: load once, serialize to string `s1`.
  - Act: `ser.FromYaml(s1)` then re-serialize to `s2`.
  - Assert: `Canonicalize(s1) == Canonicalize(s2)`.

### Integration tests
Optional end-to-end: save `wf` to a temp file via `WorkflowSerializer.SaveToFile`, reload via `WorkflowLoader.Load`, re-serialize. Same golden contract. Place under `Fixtures/_tmp/` and clean up in `Dispose`.

### Golden-file / parity tests
- **Golden:** `src/WorkflowUi.ContractTests/Fixtures/noop-minimal.canonical.yaml`.
- **Diff contract:** LF line endings, no comments, no trailing whitespace. Engine key order preserved. Equality via `CanonicalYaml.Equal`.
- **Regeneration:** `dotnet test` with env var `WORKFLOWUI_WRITE_GOLDEN=1` overwrites the canonical golden and pretty-prints a review notice.

### Edge cases & negative tests
1. Golden file missing — test fails with `MissingGoldenException("Expected golden at …")`.
2. Engine adds a new default field (e.g., `Priority=Normal`) that starts being serialized — canonical diff fails; contributor regenerates golden.
3. `WorkflowSerializer` emits `null` policies as `retryPolicy: null` rather than omitting — this breaks `null vs empty object` contract; add a dedicated fact `Serializer_NullPolicy_OmitsKey` once Phase 2 lands (this step only asserts Noop, which has no policy).
4. CRLF leaks into the golden on Windows commit — `.gitattributes` must set `*.canonical.yaml text eol=lf`; the test asserts the golden file bytes contain no `\r`.
5. A non-Latin node name — fixture includes one UTF-8 node name to prove encoding round-trip.
6. An obsolete `YamlWorkflowLoader` type is (re-)introduced in engine — `LoaderAndSerializer_ExistInEngineAssembly` test fails loud.
7. `WorkflowLoader.Load(path, validateOnLoad: true)` throws on a corrupt fixture — separate negative fact asserts `InvalidOperationException`.

### Observability hooks
- Log category `WorkflowUi.ContractTests.YamlRoundTrip` — emits fixture-path, golden-path, canonical-byte-length, and a diff preamble on failure.
- On failure, prints unified diff between `expected` and `actual` to stdout (truncated at 200 lines) so CI log is actionable.

### Risks & mitigations
- YamlDotNet default options diverge between engine and UI host — the UI host never instantiates YamlDotNet directly; it must wrap `WorkflowSerializer`. The existence probe in `YamlRoundTripTests_LoaderAndSerializer_ExistInEngineAssembly` is the enforcement.
- Line-ending normalisation drift — `.gitattributes` + explicit byte-level checks.
- A future engine version auto-sorts YAML keys — the test would need an intentional update; such an engine change is out of scope for this plan and would be flagged as a Phase 2 blocker.

### Evaluator gap-analysis prompts
1. Does the step use `ExecutionEngine.Workflow.WorkflowLoader` and `ExecutionEngine.Workflow.WorkflowSerializer` — not `YamlWorkflowLoader` — as required by [v1.3 changelog](../../workflow-ui-plan.md#changelog)?
2. Does `CanonicalYaml.Canonicalize` strip comments, normalise CRLF→LF, and preserve engine key order (no lexical sort)?
3. Is there a fact that asserts `YamlWorkflowLoader` DOES NOT exist in the engine assembly?
4. Does the fixture fail validation on load when corrupted (negative case), proving `validateOnLoad: true` is wired?
5. Does the double-round-trip fact assert idempotence (`canon(s1) == canon(s2)`)?
6. Is `.gitattributes` updated to pin `*.canonical.yaml` to LF?
7. Is the fixture committed at a deterministic path (`src/WorkflowUi.ContractTests/Fixtures/noop-minimal.yaml`) that Phase 2 can extend without re-layout?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.
