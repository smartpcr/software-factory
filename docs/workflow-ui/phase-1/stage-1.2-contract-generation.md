# Stage 1.2 — Contract generation

**Phase:** Phase 1 — Host, Contract, CI
**Stage status:** [ ]
**Total effort:** 5 d
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Stage 1.2 replaces the hand-maintained `engine-contract.generated.ts` stub from Stage 0.1/0.2 with a real reflection-based generator. Master plan [§0.2.2](../../workflow-ui-plan.md#022-dto-parity-tests) and [§1.2 Stage](../../workflow-ui-plan.md#stage-12--contract-generation) require: a console tool `tools/GenContract` that reflects over engine enums, `*NodeDefinition.cs`, `NodeConnection`, `RetryPolicy`, and `CircuitBreakerPolicy`, emits sorted-deterministic TypeScript to `client/src/engine-contract.generated.ts`, and a CI step that re-runs the generator and fails the build on any non-empty diff. Finally, a hand-written facade `engine-contract.ts` re-exports generated types + adds UI-only helpers (e.g., exhaustive `assertNever`), and an ESLint rule forbids direct imports of the generated file outside the facade.

Ground-truth property lists this stage emits (engine-sourced, verbatim):

- Base `NodeDefinition` (11 props): `NodeId, NodeName, Description, Configuration, JoinType, Priority, MaxConcurrentExecutions, RetryPolicy, CircuitBreakerPolicy, CompensationNodeId, FallbackNodeId` (per `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:27-95`).
- `RetryPolicy` (7 props): `Strategy, MaxAttempts, InitialDelay, MaxDelay, Multiplier, RetryOn, DoNotRetryOn` (per `src/ExecutionEngine/Policies/RetryPolicy.cs:40-83`).
- `CircuitBreakerPolicy` (4 props): `FailureThreshold, MinimumThroughput, OpenDuration, HalfOpenSuccesses`.
- `NodeConnection` (9 props): `SourceNodeId, TargetNodeId, TriggerMessageType, Condition, IsEnabled, Metadata, Priority, SourcePort, TargetPort`.
- Per-kind shapes verified case-by-case against the 13 `*NodeDefinition.cs` files.

This stage unblocks Phase 2 (the parity tests now have a real TS file to compare against) and Phase 4 (the property-grid reads from `engine-contract.ts`).

## Prerequisites
- Stage 0.2 green (the generator reuses the reflection helpers in `src/WorkflowUi.ContractTests/Reflection/`).
- Stage 1.1 complete (DI for engine types exists, enabling any future generator mode that needs a hosted service — today the generator is a pure console app).

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 1.2 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

Generator
- `tools/GenContract/GenContract.csproj` (new) — `Microsoft.NET.Sdk`, `OutputType=Exe`, net8.0, `ProjectReference` to `ExecutionEngine`.
- `tools/GenContract/Program.cs` (new) — CLI entry.
- `tools/GenContract/Emitters/EnumEmitter.cs` (new).
- `tools/GenContract/Emitters/DtoEmitter.cs` (new).
- `tools/GenContract/Emitters/PolicyEmitter.cs` (new).
- `tools/GenContract/Emitters/ConnectionEmitter.cs` (new).
- `tools/GenContract/Emitters/TsWriter.cs` (new) — line-ending + indent helper.
- `tools/GenContract/Emitters/SortOrder.cs` (new) — deterministic ordering.
- `tools/GenContract.Tests/GenContract.Tests.csproj` (new).
- `tools/GenContract.Tests/DeterminismTests.cs` (new).
- `tools/GenContract.Tests/ShapeTests.cs` (new).

Client facade
- `client/src/engine-contract.generated.ts` (modified — now produced by generator).
- `client/src/engine-contract.ts` (new, Step 1.2.3) — hand-written re-export facade.
- `client/src/contract/assert-never.ts` (new, Step 1.2.3).
- `client/.eslintrc.cjs` (modified, Step 1.2.3) — activate `no-restricted-imports` rule.
- `client/src/__tests__/contract-facade.test.ts` (new, Step 1.2.3).

CI
- `.github/workflows/ci-workflow-ui.yml` (modified, Step 1.2.2) — add `contract-drift` job.
- `src/WorkflowUi.ContractTests/ContractDriftCiTests.cs` (new, Step 1.2.2) — meta-test.

Solution
- `Workflow.sln` (modified) — add `tools/GenContract` and `tools/GenContract.Tests`.

## Shared fixtures / fakes
- `engine-contract.generated.ts` is the sole artefact of this stage, consumed directly by Phase 2 DTO work.
- `client/src/__fixtures__/engine-enums.json` and `engine-dto-shape.json` (from Stage 0.2) are the parity-fixtures — the generator MUST produce TS that is consistent with these, and `ContractDriftCiTests` verifies.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="1.2"`, `phaseId="1"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-1.2.plan-read`, `workflow-ui.stage-1.2.apply`, `workflow-ui.stage-1.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_1_2_started_total`, counter `workflow_ui_stage_1_2_failed_total`, histogram `workflow_ui_stage_1_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| ASP.NET host | endpoint boots without engine services | health/version tests fail | restore DI wiring from Stage 1.1 and rerun factory tests |
| contract generator | reflection output drifts from engine | snapshot/parity diff fails | regenerate emitted contract and review enum/DTO deltas |
| CI publish path | artifacts or summaries missing | workflow summary / artifact assertions fail | re-add upload + summary steps before merge |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| GET /api/health or /api/engine/version | 50 ms | 150 ms |
| contract generation / snapshot emit | 3 s | 6 s |
| CI comment / summary publish | 30 s | 90 s |

## Security callouts
- Health/version endpoints stay non-sensitive: no machine secrets, file paths, or environment dumps in responses.
- ProblemDetails payloads must redact stack traces and only expose correlation ids to clients.
- Do not add YAML/file-loading shortcuts here; all file I/O stays behind later sandboxed endpoints.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 1.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage12` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

## Step 1.2.1 — `tools/GenContract` console generator

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 0.2.2

### Goal
Author a small .NET console tool that (a) references `ExecutionEngine`, (b) reflects over the 7 public enums, 14 node-definition shapes (1 base + 13 kinds), `RetryPolicy`, `CircuitBreakerPolicy`, and `NodeConnection`, (c) emits a single deterministic TypeScript file with a file header, enum string-literal unions, policy interfaces, per-kind node DTO interfaces, a `NodeDto` discriminated union, and a `NodeConnectionDto` interface, and (d) overwrites `client/src/engine-contract.generated.ts` atomically. Output is byte-stable across runs and across platforms (LF line endings, `\n`-only). Properties are sorted alphabetically within each interface; interfaces are emitted in a stable order (base first, then kinds alphabetically, then policy DTOs, then connection).

### Depends on
0.2.2.

### Inputs
- Engine assembly.
- Stage 0.2 reflection helpers (duplicated or referenced — prefer `InternalsVisibleTo` from ContractTests so both share `EngineReflection.cs`).

### Deliverables
- `tools/GenContract/GenContract.csproj` (new) — Exe project.
- `tools/GenContract/Program.cs` (new) — `Main(args)`: parses `--output <path>` flag, invokes each emitter in order, writes file with LF line endings and UTF-8-no-BOM.
- `tools/GenContract/Emitters/EnumEmitter.cs` (new) — emits: `export type RuntimeType = 'Noop' | 'CSharp' | ...;` for each of the 7 enums; enums ordered by source-declaration, members in source order.
- `tools/GenContract/Emitters/DtoEmitter.cs` (new) — emits `interface NodeDtoBase { ... }` and one interface per `*NodeDefinition.cs` subclass with `runtimeType: 'CSharp'` discriminator.
- `tools/GenContract/Emitters/PolicyEmitter.cs` (new) — emits `RetryPolicyDto`, `CircuitBreakerPolicyDto` with the exact 7 and 4 properties from `src/ExecutionEngine/Policies/*.cs`.
- `tools/GenContract/Emitters/ConnectionEmitter.cs` (new) — emits `NodeConnectionDto` with the 9 properties from `src/ExecutionEngine/Workflow/NodeConnection.cs`.
- `tools/GenContract/Emitters/TsWriter.cs` (new) — helper writing lines with `\n`, 2-space indent, sorted keys.
- `tools/GenContract/Emitters/TypeMapping.cs` (new) — C# → TS type map: `string` → `string`, `string?` → `string?` (emitted as `prop?: string` OR `prop: string | null`; CHOICE: we emit `prop?: T` for nullable reference, and `prop: T` for non-nullable; `Dictionary<K,V>` → `Record<K, V>`; `List<T>` → `T[]`; `TimeSpan` → `string` (ISO-8601 or hh:mm:ss — commented); `int/double/bool` pass-through; enum types → the emitted TS union name.
- `tools/GenContract/Program.cs` header block: file-header comment `// GENERATED. Do not edit. Run tools/GenContract to regenerate.` + checksum of engine assembly version.
- `tools/GenContract.Tests/DeterminismTests.cs` (new) — runs generator twice, asserts byte-identical output.
- `tools/GenContract.Tests/ShapeTests.cs` (new) — asserts the emitted TS text contains specific known literals (e.g., `"runtimeType: 'Subflow'"`).
- `Workflow.sln` (modified) — add both projects.

### Acceptance criteria
- [ ] `dotnet run --project tools/GenContract -- --output client/src/engine-contract.generated.ts` exits 0 and overwrites the file.
- [ ] Running the generator twice produces byte-identical output.
- [ ] Emitted enum unions match `engine-enums.json` from Stage 0.2.1 exactly (member names, order, count).
- [ ] Emitted `NodeDtoBase` has 11 properties plus the discriminator `runtimeType: RuntimeType`.
- [ ] Emitted `RetryPolicyDto` has 7 properties: `strategy, maxAttempts, initialDelay, maxDelay, multiplier, retryOn, doNotRetryOn`.
- [ ] Emitted `CircuitBreakerPolicyDto` has 4 properties: `failureThreshold, minimumThroughput, openDuration, halfOpenSuccesses`.
- [ ] Emitted `NodeConnectionDto` has 9 properties: `sourceNodeId, targetNodeId, triggerMessageType, condition, isEnabled, metadata, priority, sourcePort, targetPort`.
- [ ] All properties are camelCase (emitter lowercases first letter).
- [ ] Nullable reference types become `prop?: T` (optional with `undefined`); value-type `Nullable<T>` same treatment.
- [ ] `TimeSpan` properties are typed as `string` with a comment noting the ISO-8601 format.
- [ ] Output file has LF line endings and no trailing whitespace.
- [ ] File header contains `// GENERATED` and engine-assembly version string.
- [ ] `tsc` compiles the generated file without errors.

### Unit tests
1. `DeterminismTests_Run2x_ByteIdentical`
2. `ShapeTests_NodeDtoBase_Has11Properties`
3. `ShapeTests_NodeDtoBase_DiscriminatorTypedAs_RuntimeType`
4. `ShapeTests_RetryPolicyDto_Has_7_Properties_In_Alphabetical_Order`
5. `ShapeTests_CircuitBreakerPolicyDto_Has_4_Properties`
6. `ShapeTests_NodeConnectionDto_Has_9_Properties`
7. `ShapeTests_Subflow_Has_Timeout_As_String_Comment_Mentions_ISO8601`
8. `ShapeTests_Container_ChildNodes_Typed_As_NodeDto_Array`
9. `ShapeTests_Enum_RuntimeType_Emits_13_StringLiteralUnion_Members`
10. `ShapeTests_Enum_NodePriority_Emits_Low_Normal_High_NoCritical`
11. `ShapeTests_CSharpTaskNodeDefinition_ScriptContent_ExecutorTypeName_ExecutorAssemblyPath_AllOptional`
12. `ShapeTests_PowerShellScriptNodeDefinition_ScriptPath_NotOptional`
13. `ShapeTests_NullableString_Emits_Optional_Property_Question_Mark`
14. `ShapeTests_EnumProperty_Emits_UnionName_Not_EnumType` — e.g., `joinType: JoinType` (our union), not `joinType: 'Any' | 'All'` inline.
15. `ShapeTests_TypeScript_Compiles_Emitted_Output` — runs `tsc --noEmit` on the emitted file.

A/A/A for the three most important:

- `DeterminismTests_Run2x_ByteIdentical`
  - Arrange: capture stdout from invocation 1 into byte array.
  - Act: invoke a second time into byte array 2.
  - Assert: `Assert.Equal(bytes1, bytes2);`
- `ShapeTests_NodeDtoBase_Has11Properties`
  - Arrange: run generator into a string.
  - Act: parse TS (regex or a tiny TS-reflection helper that counts `interface NodeDtoBase { ... }` properties).
  - Assert: property count == 11; each named prop from the canonical list is present.
- `ShapeTests_TypeScript_Compiles_Emitted_Output`
  - Arrange: write generator output to temp file; create minimal `tsconfig.json` in temp.
  - Act: `Process.Start("npx", "tsc --noEmit --strict ...");`
  - Assert: exit code 0.

### Integration tests
- Integration harness: spin up a `WebApplicationFactory<Program>` to confirm the generated TS file is not required at runtime (only at build time) — no runtime dependency is allowed. Tests the contract stays a build-time artefact.

### Golden-file / parity tests
- The generated file IS a golden, but it is regenerated on every commit. The parity contract is: `dotnet run --project tools/GenContract` produces a file that is byte-identical to the committed copy. Enforced in Step 1.2.2.
- Diff contract: LF line endings, alphabetical property order within interface, interfaces in the order (enums, policy DTOs, base, kinds alphabetical, connection).

### Edge cases & negative tests
1. Engine adds `RetryPolicy.Jitter` — emitter picks it up automatically; committed golden now drifts; CI step 1.2.2 fails.
2. Engine renames `ScriptPath` to `Path` on `PowerShellScriptNodeDefinition` — emitter re-emits; 1.2.2 red; contributor updates hand fixture in `engine-dto-shape.json`.
3. Emitter called with unknown `--output` directory — creates parent dir or fails with a clear message (not an NRE).
4. Type map encounters an unknown CLR type (e.g., `DateTime`) — emitter fails with `UnsupportedTypeException` naming the type and property; drives us to add an explicit mapping.
5. Two properties with names colliding after camelCase (e.g., `Name` vs `NAME`) — emitter detects duplicate keys and fails.
6. Engine property uses `List<Type>` (as in `RetryPolicy.RetryOn: List<Type>`) — emitter maps to `string[]` per master plan §0.2.2 note; comment in emitted TS explains the FQTN round-trip.
7. Stdout redirection corrupts byte order — generator always writes directly to file path, not stdout.
8. Engine adds a new `*NodeDefinition` kind — emitter finds it via reflection, emits a new interface, and `ContractDriftCiTests` alerts that parity fixtures and phase 2 must be updated.

### Observability hooks
- Log category `GenContract.Run` — Information: "Emitted {fileSize} bytes to {outputPath}; engine v{version}; enums={n}; dtos={m}".
- Exit code 1 on any emitter exception.

### Risks & mitigations
- Non-deterministic ordering when reflection returns different property order per run — explicit `OrderBy(p => p.Name, StringComparer.Ordinal)` at emit time.
- Line-ending drift on Windows — `TsWriter` uses raw `StreamWriter(..., new UTF8Encoding(false))` with explicit `\n`.
- `List<Type>` surface complexity — documented decision to emit `string[]` of FQTNs; the master plan §0.2.2 note is authoritative.

### Evaluator gap-analysis prompts
1. Does the generator produce byte-identical output across two consecutive runs?
2. Does `NodeDtoBase.ts` include `retryPolicy` typed as `RetryPolicyDto | null` (or `?:`) with all 7 engine fields?
3. Does `NodeConnectionDto` include all 9 properties from `src/ExecutionEngine/Workflow/NodeConnection.cs`?
4. Are `RetryOn` / `DoNotRetryOn` typed as `string[]` (FQTN strings) with an explanatory comment?
5. Are enum unions emitted as string-literal unions (`'Any' | 'All'`) rather than TypeScript `enum` declarations?
6. Does the generator fail loud on unknown CLR types instead of silently mapping to `any`?
7. Does the emitted file include a `GENERATED` header and the engine assembly version?
8. Is the emitted file LF-only with no trailing whitespace?
9. Does `tsc --strict` compile the emitted file without errors?

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

## Step 1.2.2 — CI drift gate

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 1.2.1

### Goal
Extend the Stage 0.3.1 CI workflow with a `contract-drift` job that runs the generator into a temp path and diffs the result against the committed `client/src/engine-contract.generated.ts`. Any non-empty diff fails the build with a clear stderr message. Add an intentional-drift PR harness that deliberately modifies the committed file so the evaluator can verify the gate is active (test runs only on a feature branch named `test/contract-drift-*`).

### Depends on
1.2.1.

### Inputs
- `.github/workflows/ci-workflow-ui.yml` from Stage 0.3.1.
- The generator from 1.2.1.

### Deliverables
- `.github/workflows/ci-workflow-ui.yml` (modified) — new job `contract-drift`:
  ```yaml
  contract-drift:
    runs-on: windows-latest
    needs: build-dotnet
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - name: Run generator into temp
        run: dotnet run --project tools/GenContract -- --output _tmp/engine-contract.generated.ts
      - name: Diff against committed file
        shell: pwsh
        run: |
          $diff = git --no-pager diff --no-index --exit-code _tmp/engine-contract.generated.ts client/src/engine-contract.generated.ts
          if ($LASTEXITCODE -ne 0) {
            Write-Error "Contract drift detected. Re-run 'dotnet run --project tools/GenContract -- --output client/src/engine-contract.generated.ts' and commit."
            exit 1
          }
  ```
- `src/WorkflowUi.ContractTests/ContractDriftCiTests.cs` (new) — meta-test: parse workflow yaml, assert `contract-drift` job exists with specific step names.
- `tools/GenContract/README.md` (new) — short doc on how to run + regenerate.

### Acceptance criteria
- [ ] CI `contract-drift` job is present in the workflow yaml.
- [ ] `contract-drift` job needs `build-dotnet` (ensures the generator binary is built once).
- [ ] Deliberate-drift branch (committing a one-character change to the generated file) causes CI red with the expected stderr message.
- [ ] Committed `engine-contract.generated.ts` byte-matches the generator output (gate green on HEAD).
- [ ] `ContractDriftCiTests_Job_Exists_And_UsesWindowsLatest` asserts runner OS.
- [ ] `ContractDriftCiTests_Step_ExplicitlyExits1_OnDiff` asserts the `exit 1` line is present.
- [ ] Stderr message names the exact regeneration command so newcomers can unblock.

### Unit tests
1. `ContractDriftCiTests_Job_Exists`
2. `ContractDriftCiTests_Job_Needs_BuildDotnet`
3. `ContractDriftCiTests_Job_UsesWindowsLatest`
4. `ContractDriftCiTests_Step_ExplicitlyExits1_OnDiff`
5. `ContractDriftCiTests_Step_NamesGeneratorCommand`
6. `ContractDriftCiTests_Diff_UsesGitNoPager_NoIndex` — asserts specific `git diff` flags.
7. `ContractDriftCiTests_CommittedGenerated_MatchesGenerator` — local equivalent: run the generator in-process and byte-compare against the checked-in file; run in `src/WorkflowUi.ContractTests`.
8. `ContractDriftCiTests_GeneratorReadmePresent_AndMentionsCommand`

A/A/A for the three most important:

- `ContractDriftCiTests_CommittedGenerated_MatchesGenerator`
  - Arrange: invoke generator in-process into a `MemoryStream`.
  - Act: read committed file.
  - Assert: `Assert.Equal(commitedBytes, generatedBytes);`
- `ContractDriftCiTests_Step_ExplicitlyExits1_OnDiff`
  - Arrange: load workflow yaml.
  - Act: locate the diff step, read its `run` property.
  - Assert: string contains `exit 1` in the failure branch.
- `ContractDriftCiTests_Step_NamesGeneratorCommand`
  - Arrange: load workflow yaml.
  - Act: locate diff step's run block.
  - Assert: contains `dotnet run --project tools/GenContract`.

### Integration tests
- Intentional-drift PR: open a PR on branch `test/contract-drift-demo` that edits `engine-contract.generated.ts`; assert CI red. Documented as a manual evaluator step; not automated.

### Golden-file / parity tests
- The committed `engine-contract.generated.ts` is the golden; Step 1.2.2 is its enforcement.

### Edge cases & negative tests
1. Line-ending drift between Windows runner and Linux contributor — generator always emits LF; diff uses `git diff` which respects `.gitattributes`.
2. Transient whitespace from editor auto-format — `.editorconfig` covers `*.generated.ts` with `insert_final_newline = false`; `.prettierignore` excludes generated file.
3. Generator project fails to build — `needs: build-dotnet` ensures the generator compiles; otherwise `contract-drift` is skipped with a clear message.
4. PR author forgets to regenerate — error message tells them the exact command.
5. Generator adds a new dependency that fails `dotnet restore` on runner — caught in `build-dotnet` not `contract-drift`.
6. Someone intentionally bypasses by editing the committed file AND the generator — caught only if the evaluator re-runs the ShapeTests from 1.2.1 (which assert absolute property counts).

### Observability hooks
- CI step logs the diff output (truncated at 200 lines) to `$GITHUB_STEP_SUMMARY` so the evaluator sees exactly which lines drifted.

### Risks & mitigations
- False-positive whitespace diffs — generator emits LF + no trailing whitespace by construction.
- Stale `_tmp` directory from a previous run — job uses a fresh checkout.
- Codecov/artefact uploads masking the gate exit code — gate runs as a separate job with clear name.

### Evaluator gap-analysis prompts
1. Does the `contract-drift` job exist and `needs: build-dotnet`?
2. Does the gate fail on a one-character edit to the committed file?
3. Is the regeneration command printed to stderr on failure, with the exact path?
4. Is `git diff --no-index --exit-code` used so exit code 1 is a real signal?
5. Is the meta-test `ContractDriftCiTests_CommittedGenerated_MatchesGenerator` part of the regular test suite (not skipped)?

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

## Step 1.2.3 — Facade + ESLint ban on direct imports

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 1.2.1

### Goal
Hand-author `client/src/engine-contract.ts` as the only allowed import source for UI code. It re-exports every enum and DTO from `engine-contract.generated.ts` and adds UI-only helpers: an `assertNever(x: never): never` function for exhaustive discriminator checks, a `RUNTIME_TYPES` readonly array suitable for palette listing, and a set of narrowing type guards (e.g., `isContainerNode`, `isSwitchNode`). Activate the ESLint `no-restricted-imports` rule (stub seeded in 0.1.3) at error level, forbidding all imports of `engine-contract.generated` outside the facade file itself. An eslint meta-test proves the ban works by linting a fixture that imports the generated file directly.

### Depends on
1.2.1.

### Inputs
- `client/src/engine-contract.generated.ts` from 1.2.1.
- ESLint config stub from 0.1.3.

### Deliverables
- `client/src/engine-contract.ts` (new) — re-exports + helpers:
  ```ts
  export * from './engine-contract.generated';
  export { assertNever } from './contract/assert-never';
  export const RUNTIME_TYPES = ['Noop','CSharp','CSharpScript','CSharpTask','PowerShell','PowerShellTask','IfElse','Switch','ForEach','While','Subflow','Timer','Container'] as const;
  export function isContainerNode(n: NodeDto): n is ContainerNode { return n.runtimeType === 'Container'; }
  export function isSwitchNode(n: NodeDto): n is SwitchNode { return n.runtimeType === 'Switch'; }
  // ... one guard per control-flow kind
  ```
- `client/src/contract/assert-never.ts` (new) — `export function assertNever(x: never): never { throw new Error('Unhandled runtime type: ' + JSON.stringify(x)); }`
- `client/.eslintrc.cjs` (modified) — activate the rule:
  ```js
  'no-restricted-imports': ['error', {
    paths: [{
      name: './engine-contract.generated',
      message: 'Import from "./engine-contract" (the facade) instead.'
    }],
    patterns: ['**/engine-contract.generated']
  }]
  ```
  The rule has an override block that permits `client/src/engine-contract.ts` itself to import the generated file.
- `client/src/__tests__/contract-facade.test.ts` (new) — vitest asserting helpers behave.
- `client/src/__tests__/eslint-no-direct-generated-import.test.ts` (new) — programmatic ESLint run on a synthetic source that imports the generated file, asserts `no-restricted-imports` violation emitted.

### Acceptance criteria
- [ ] `client/src/engine-contract.ts` is the only file that imports `./engine-contract.generated`.
- [ ] ESLint rule `no-restricted-imports` is `error` level.
- [ ] A fixture source `import { RuntimeType } from './engine-contract.generated';` produces a lint error with message "Import from './engine-contract' (the facade) instead.".
- [ ] The facade exports `assertNever`, `RUNTIME_TYPES` (13-element readonly tuple), and type guards for each control-flow kind + container.
- [ ] `tsc --strict` compiles the facade without errors.
- [ ] `RUNTIME_TYPES` is typed `readonly ['Noop', 'CSharp', ...]` so adding an engine kind without updating the facade is a type error.
- [ ] `assertNever` throws with a message including the unhandled discriminator value (useful for debugging).
- [ ] All existing UI code (from Stage 0.1's `App.tsx`) is either untouched or routed through the facade.

### Unit tests
1. `ContractFacade_Exports_RUNTIME_TYPES_With_13_Members`
2. `ContractFacade_RUNTIME_TYPES_Matches_EngineEnumsJson_Fixture`
3. `ContractFacade_AssertNever_Throws_With_Value`
4. `ContractFacade_IsContainerNode_TrueForContainer`
5. `ContractFacade_IsContainerNode_FalseForNoop`
6. `ContractFacade_IsSwitchNode_FalseForIfElse` — guard against easy mistake.
7. `EslintNoDirectGeneratedImport_ForbiddenPath_ProducesError`
8. `EslintNoDirectGeneratedImport_FacadeFile_IsExempt` — linting `engine-contract.ts` itself must pass.
9. `ContractFacade_TypeExhaustiveness_Compiles` — a `tsd` type-level test asserts `switch(n.runtimeType) { default: assertNever(n); }` compiles when all 13 kinds handled and fails if one is removed.

A/A/A for the three most important:

- `EslintNoDirectGeneratedImport_ForbiddenPath_ProducesError`
  - Arrange: load ESLint programmatically with the repo config.
  - Act: `await eslint.lintText("import {RuntimeType} from './engine-contract.generated';", { filePath: 'client/src/foo.ts' });`
  - Assert: `result[0].messages[0].ruleId === 'no-restricted-imports'` with severity 2.
- `ContractFacade_RUNTIME_TYPES_Matches_EngineEnumsJson_Fixture`
  - Arrange: `import fixture from '../__fixtures__/engine-enums.json';`
  - Act: compare `RUNTIME_TYPES` array to `fixture.RuntimeType.members`.
  - Assert: deep-equal arrays (including order).
- `ContractFacade_AssertNever_Throws_With_Value`
  - Arrange: `const x: any = { runtimeType: 'Mystery' };`
  - Act: `expect(() => assertNever(x)).toThrow(/Mystery/);`
  - Assert: error message contains the offending discriminator.

### Integration tests
None — facade is client-side only.

### Golden-file / parity tests
- `RUNTIME_TYPES` is implicitly parity-checked against `engine-enums.json` golden via `ContractFacade_RUNTIME_TYPES_Matches_EngineEnumsJson_Fixture`.

### Edge cases & negative tests
1. A new kind added to the engine — `RUNTIME_TYPES` must be updated manually (intentional — keeps the facade explicit); the parity test fails immediately.
2. A test-helper file wants raw generator types for inspection — it can import `engine-contract.generated` via an explicit ESLint `overrides` exemption documented in `.eslintrc.cjs`. Without the exemption, the rule fires.
3. `assertNever` called with a runtime value (not compile-time-impossible) — still throws; useful for runtime exhaustion guarantees.
4. Accidental wildcard re-export drops a type — `ContractFacade_TypeExhaustiveness_Compiles` catches.
5. Hand-edit of the facade that removes a guard function — downstream callers break at compile time; documented.
6. Ordering mismatch: `RUNTIME_TYPES` reordered alphabetically — the fixture comparison (order-sensitive) fails loud, alerting the contributor to a real drift.

### Observability hooks
- Not applicable — pure TS / client-side code.

### Risks & mitigations
- ESLint rule over-scope (accidentally bans legitimate imports) — overrides section explicitly allows `engine-contract.ts` only.
- Wildcard re-export hides missing types — `tsd` type-level test catches.
- `RUNTIME_TYPES` drifts silently from the enum union — parity fixture test keeps them in sync.

### Evaluator gap-analysis prompts
1. Is `engine-contract.ts` the only file allowed to import `engine-contract.generated.ts`?
2. Is the `no-restricted-imports` rule at `error` severity (not `warn`)?
3. Does `RUNTIME_TYPES` have 13 elements in engine source-declaration order and match `engine-enums.json`?
4. Does `assertNever` throw with the offending discriminator value included?
5. Is there at least one narrowing type guard per control-flow kind (IfElse, Switch, ForEach, While, Subflow, Container)?
6. Does a programmatic ESLint test prove the ban fires on a forbidden import and does NOT fire on the facade file itself?
7. Does a `tsd` or equivalent type-level test prove the exhaustive switch compiles iff all 13 kinds are handled?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.
