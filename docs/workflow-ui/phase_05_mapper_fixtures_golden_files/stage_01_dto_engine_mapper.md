# Stage 5.1 — DTO ↔ engine mapper (`EngineDtoMapper`)

**Phase:** Mapper, Fixtures, Golden Files  
**Stage status:** [ ]  
**Total effort:** 5 d  
**Step count:** 7

## Steps

- [ ] **Step 1**: Base + task-family mapper (Noop, CSharp)
- [ ] **Step 2**: Script/task mapper (CSharpScript, CSharpTask)
- [ ] **Step 3**: PowerShell mapper
- [ ] **Step 4**: Control-flow mapper (IfElse, Switch, ForEach, While)
- [ ] **Step 5**: Composition mapper (Subflow, Timer, Container)
- [ ] **Step 6**: Connection mapper + sidecar merger
- [ ] **Step 7**: Mapper registry and kind factory

---
## Context
Phase 5 is where the UI contract becomes real engine I/O. The mapper layer must turn UI DTOs into the exact `WorkflowDefinition`, `NodeDefinition`, and `NodeConnection` objects the engine already knows how to load, validate, and serialize, then reverse the process without losing sidecar metadata or canonical YAML behavior (src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76, src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149, src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212, src/ExecutionEngine/Workflow/NodeConnection.cs:21-99).
The master plan intentionally splits this work by kind family so each sub-step stays within the file-budget rule while still composing through a single mapper class (docs/workflow-ui-plan.md:902-1025). The base mapper must carry all shared `NodeDefinition` fields, including the nullable `RetryPolicy` and `CircuitBreakerPolicy` shapes (src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95, src/ExecutionEngine/Policies/RetryPolicy.cs:25-170, src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:21-80), while each per-kind partial adds only the properties present in the cited engine node-definition file for that runtime type.
Every step in this stage therefore has two jobs: preserve field fidelity and preserve canonical round-trip behavior. The server remains the only place where YAML is read or written, and every mapper change must be backed by golden or parity evidence so an evaluator can prove no engine field, enum, default, or null/absent distinction drifted.

## Prerequisites
- Phase gates (§0.2) green
- Prior stages completed
- External deps (React Flow v12, zustand, zundo, react-hook-form, zod, shadcn, ELKjs)

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/connections/all-message-types.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/connections/all-message-types.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/connections/all-message-types.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-default-max-iterations.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-default-max-iterations.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-default-max-iterations.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-executor.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-executor.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-executor.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-path.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-path.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-path.yaml`
- `src/host/Mapping/ConnectionMapper.cs`
- `src/host/Mapping/EngineDtoMapper.Base.cs`
- `src/host/Mapping/EngineDtoMapper.CompositionFamily.cs`
- `src/host/Mapping/EngineDtoMapper.ControlFlowFamily.cs`
- `src/host/Mapping/EngineDtoMapper.PowerShellFamily.cs`
- `src/host/Mapping/EngineDtoMapper.Registry.cs`
- `src/host/Mapping/EngineDtoMapper.ScriptFamily.cs`
- `src/host/Mapping/EngineDtoMapper.TaskFamily.cs`
- `src/host/Mapping/MapperDiagnostics.cs`
- `src/host/Mapping/SidecarMerger.cs`
- `src/host/__tests__/Mapping/ConnectionMapperTests.cs`
- `src/host/__tests__/Mapping/ConnectionRoundTripApiTests.cs`
- `src/host/__tests__/Mapping/EngineDtoMapperBaseTests.cs`
- `src/host/__tests__/Mapping/EngineDtoMapperCompositionFamilyTests.cs`
- `src/host/__tests__/Mapping/EngineDtoMapperControlFlowFamilyTests.cs`
- `src/host/__tests__/Mapping/EngineDtoMapperPowerShellFamilyTests.cs`
- `src/host/__tests__/Mapping/EngineDtoMapperRegistryTests.cs`
- `src/host/__tests__/Mapping/EngineDtoMapperScriptFamilyTests.cs`
- `src/host/__tests__/Mapping/MapperRoundTripApiTests.cs`
- `src/host/__tests__/Mapping/SidecarMergerTests.cs`

## Shared fixtures / fakes
- `src/WorkflowUi.ContractTests/Fixtures/mapper/` — canonical YAML, `.ui.json`, and `.golden.json` triplets consumed by mapper unit and integration tests.
- `src/host/__tests__/Mapping/MapperWebAppFactory.cs` — host test harness for round-trip and validation endpoint coverage.
- `src/host/__tests__/Mapping/FakeSidecarStore.cs` — test double for sidecar merge/load behavior.
- `src/host/__tests__/Mapping/TypeNameFixtures.cs` — FQTN fixtures used for `RetryOn` / `DoNotRetryOn` mapping tests.

---

### Step 1 — Base + task-family mapper (Noop, CSharp)
### 1. Status
[ ]
### 2. Effort
0.5 d
### 3. Goal
Implement the shared base mapper that every runtime-specific partial composes with, then use it for the simplest task-family kinds: Noop and compiled CSharp. `MapBase` must carry every shared `NodeDefinition` field, including nullable policy objects, references, and engine-default-preserving omission behavior. The first per-kind partials should then add only `AssemblyPath` and `TypeName` for `CSharp`, while `Noop` remains base-only. Because policy fidelity is the highest-risk part of the base mapper, this step also establishes the canonical list-of-FQTN handling for `RetryOn` and `DoNotRetryOn`. The output of this step becomes the template for every later kind family.
### 4. Depends on
- Step 2.2.1b
- Step 2.4.4
### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the base+task-family scope, YAML byte-match expectation, and policy round-trip requirements for Step 5.1.1a.
- **Node base contract** — `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95` defines every base field `MapBase` must preserve.
- **Retry-policy contract** — `src/ExecutionEngine/Policies/RetryPolicy.cs:25-170` and `src/ExecutionEngine/Enums/RetryStrategy.cs:13-41` define the seven retry sub-fields and enum values.
- **Circuit-breaker contract** — `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:21-80` defines the four circuit-breaker sub-fields.
- **Noop / CSharp shapes** — `src/ExecutionEngine/Nodes/Definitions/NoopNodeDefinition.cs:12-19` and `src/ExecutionEngine/Nodes/Definitions/CSharpNodeDefinition.cs:13-59` define the kind-specific field additions for this step.
### 6. Deliverables
- **src/host/Mapping/**
  - `src/host/Mapping/EngineDtoMapper.Base.cs` (new) — Exports `MapBase`, `MapRetryPolicy`, and `MapCircuitBreakerPolicy` shared helpers.
  - `src/host/Mapping/EngineDtoMapper.TaskFamily.cs` (new) — Exports `MapNoop` and `MapCSharp` partial methods that compose with `MapBase`.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/EngineDtoMapperBaseTests.cs` (new) — Unit coverage for base-field, policy, and Noop/CSharp round-trip behavior.
  - `src/host/__tests__/Mapping/MapperRoundTripApiTests.cs` (new) — WebApplicationFactory coverage for mapper-backed round-trip endpoints or commands.
- **src/WorkflowUi.ContractTests/Fixtures/mapper/task/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.yaml` (new) — Canonical Noop YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.ui.json` (new) — Sidecar fixture paired with the Noop YAML.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.golden.json` (new) — Hydrated Noop golden DTO snapshot.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.yaml` (new) — Canonical compiled-CSharp YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.ui.json` (new) — Sidecar fixture paired with the CSharp YAML.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.golden.json` (new) — Hydrated CSharp golden DTO snapshot.
### 7. Acceptance criteria
- [ ] `MapBase` preserves every shared `NodeDefinition` field.
  - Evidence: Base mapper tests enumerate all base properties and compare source/target DTOs.
- [ ] Policy-populated and policy-null workflows both round-trip without semantic drift.
  - Evidence: Unit and golden tests cover populated retry/circuit-breaker objects plus explicit null/absent cases.
- [ ] `RetryOn` and `DoNotRetryOn` lists round-trip as FQTN strings through `Type.GetType`-compatible values.
  - Evidence: Tests use known fully-qualified type names and assert the same strings come back.
- [ ] Noop YAML ↔ DTO ↔ YAML byte-matches in canonical form.
  - Evidence: The Noop fixture round-trips through the mapper and serializer without diff.
- [ ] Compiled CSharp YAML ↔ DTO ↔ YAML byte-matches in canonical form.
  - Evidence: The CSharp fixture round-trips without diff and preserves path/type fields.
- [ ] The host integration path uses the new mapper and passes through engine validation successfully.
  - Evidence: WebApplicationFactory posts or invokes the round-trip path and receives canonical output.
### 8. Unit tests
1. **MapBase_roundtrips_all_shared_fields**
   - Purpose: Ensures the base mapper carries the full common contract.
   - Harness: xUnit
   - Arrange: Create a DTO with every shared base field populated.
   - Act: Map to engine and back to DTO.
   - Assert: All shared fields match, including references and configuration.
2. **MapBase_roundtrips_retry_policy_populated**
   - Purpose: Covers the seven-field retry object.
   - Harness: xUnit
   - Assertion focus: Every retry field, including type-name lists, survives a full round-trip.
3. **MapBase_roundtrips_retry_policy_null**
   - Purpose: Protects null/absent semantics.
   - Harness: xUnit
   - Assertion focus: A null retry policy stays null and serializes to an absent YAML key.
4. **MapBase_roundtrips_circuit_breaker_populated**
   - Purpose: Covers the four-field circuit-breaker object.
   - Harness: xUnit
   - Assertion focus: All circuit-breaker fields survive a full round-trip.
5. **MapBase_roundtrips_circuit_breaker_null**
   - Purpose: Protects null/absent circuit semantics.
   - Harness: xUnit
   - Assertion focus: Null remains null and serializes canonically.
6. **MapBase_roundtrips_retry_type_name_lists**
   - Purpose: Covers FQTN list mapping.
   - Harness: xUnit
   - Assertion focus: Known type names in `RetryOn` / `DoNotRetryOn` survive exact string round-trip.
7. **Noop_fixture_byte_matches_after_roundtrip**
   - Purpose: Covers the simplest runtime.
   - Harness: xUnit golden test
   - Assertion focus: Canonical Noop YAML matches byte-for-byte after mapper round-trip.
8. **CSharp_fixture_byte_matches_after_roundtrip**
   - Purpose: Covers the compiled task kind.
   - Harness: xUnit golden test
   - Assertion focus: Canonical CSharp YAML matches byte-for-byte after round-trip.
### 9. Integration tests
1. **Roundtrip_api_noop_fixture**
   - Purpose: Exercise the host mapper path with Noop.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: POST the Noop fixture triplet or invoke the host mapper endpoint.
   - Act: Read the canonical YAML and hydrated DTO response.
   - Assert: Both match the golden outputs.
2. **Roundtrip_api_csharp_fixture**
   - Purpose: Exercise the host mapper path with compiled CSharp.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the CSharp fixture through the mapper host surface.
   - Act: Read the response payloads.
   - Assert: The canonical YAML and hydrated DTO match the goldens and validate successfully.
3. **Roundtrip_api_policy_null_vs_populated**
   - Purpose: Verify host behavior for nullable policies.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit one payload with populated policies and another with null policies.
   - Act: Round-trip both through the host.
   - Assert: The host preserves the semantic difference and canonical YAML omission behavior.
### 10. Golden-file / parity tests
- **Fixture triplets required** — Each Noop and CSharp case carries `.yaml`, `.ui.json`, and `.golden.json` artifacts so canonical YAML and hydrated DTO diffs are visible in review.
- **Policy nullability regression** — Golden outputs explicitly distinguish populated policy objects from disabled/null policies.
- **Base-field parity** — Mapper assertions stay anchored to the shared fields defined in `src/ExecutionEngine/Nodes/Definitions/NodeDefinition.cs:21-95`, `src/ExecutionEngine/Policies/RetryPolicy.cs:25-170`, and `src/ExecutionEngine/Policies/CircuitBreakerPolicy.cs:21-80`.
### 11. Edge cases & negative tests
- A retry type name cannot be resolved by `Type.GetType`; the mapper returns a clear host validation error instead of silently dropping it.
- A base configuration bag contains mixed scalar values; mapping preserves them without reordering or coercion.
- CSharp path fields contain Windows separators; the mapper preserves canonical string form and lets engine validation handle existence.
- Policy objects are present but partially populated; the mapper carries the values through and lets validation surface errors later.
- A Noop DTO arrives with unexpected kind-specific fields; the mapper ignores unsupported extras rather than serializing them into engine YAML.
### 12. Observability hooks
- **Structured log field set** — Emit `mapper.base.roundtrip` with `runtimeType`, `policyState`, `warningCount`, and `canonicalDiffBytes`.
- **Host trace** — Log `retryTypeNameCount` and `doNotRetryTypeNameCount` when base policies are mapped.
- **Golden metadata** — Include fixture name and runtime type in snapshot output so CI failures are easy to triage.
- **WebApplicationFactory trace** — Correlate request id, fixture name, and canonicalization result across host and test logs.
### 13. Risks & mitigations
- **Risk:** Base-field omissions cascade into every later mapper partial.
  - Mitigation: Write exhaustive base-field tests before composing any more kind families.
- **Risk:** Type-name list mapping is environment-sensitive.
  - Mitigation: Use deterministic FQTNs in fixtures and fail clearly when a type cannot be resolved.
- **Risk:** Null policies collapse into empty objects.
  - Mitigation: Keep explicit null/absent regression goldens and assert key omission in YAML.
### 14. Evaluator gap-analysis prompts
1. Which file contains `MapBase`, and how does it prove every shared `NodeDefinition` field is handled?
2. How are populated and null policy objects distinguished in both unit tests and goldens?
3. What evidence shows FQTN strings in `RetryOn` and `DoNotRetryOn` survive round-trip unchanged?
4. Which WebApplicationFactory scenario proves the host uses the same mapper path as unit tests?
5. How would an evaluator verify that Noop adds no extra kind-specific fields?
6. What log fields would help debug a report that retry type names disappeared during mapping?
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
- Base-field and policy parity tests are green before any later kind-family work lands.
- Noop and compiled CSharp fixture triplets round-trip without canonical YAML diffs.

### Step 2 — Script/task mapper (CSharpScript, CSharpTask)
### 1. Status
[ ]
### 2. Effort
0.75 d
### 3. Goal
Extend the mapper for the two C# script-bearing runtime types. `CSharpScript` must carry `ScriptPath` and `ScriptContent` exactly as the engine defines them, while `CSharpTask` must preserve the mutually-exclusive inline script vs compiled executor shapes without forcing the UI to choose a side during mapping. This step should trust the engine DTO and validation layers: if both branches are populated, the mapper carries the data through and lets validation decide. The host integration path should prove both inline and executor variants survive round-trip through canonical YAML.
### 4. Depends on
- Step 2.2.2
- Step 2.2.3
### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines inline/file/executor round-trip expectations for Step 5.1.1b.
- **CSharpScript contract** — `src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:12-49` defines `ScriptPath` and `ScriptContent`.
- **CSharpTask contract** — `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62` defines `ScriptContent`, `ExecutorTypeName`, and `ExecutorAssemblyPath`.
- **Base mapper** — Step 5.1.1a already handles all shared `NodeDefinition` fields.
### 6. Deliverables
- **src/host/Mapping/**
  - `src/host/Mapping/EngineDtoMapper.ScriptFamily.cs` (new) — Exports `MapCSharpScript` and `MapCSharpTask` partial methods.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/EngineDtoMapperScriptFamilyTests.cs` (new) — Unit coverage for inline/file/executor variants.
- **src/WorkflowUi.ContractTests/Fixtures/mapper/task/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.yaml` (new) — Canonical CSharpScript fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.ui.json` (new) — Sidecar for CSharpScript fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.golden.json` (new) — Hydrated CSharpScript golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.yaml` (new) — Canonical CSharpTask inline fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.ui.json` (new) — Sidecar for CSharpTask inline fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.golden.json` (new) — Hydrated CSharpTask inline golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-executor.yaml` (new) — Canonical CSharpTask executor fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-executor.ui.json` (new) — Sidecar for CSharpTask executor fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-executor.golden.json` (new) — Hydrated CSharpTask executor golden.
### 7. Acceptance criteria
- [ ] CSharpScript inline/file values round-trip without loss.
  - Evidence: Unit and golden tests cover the script-bearing fields directly.
- [ ] CSharpTask inline mode round-trips.
  - Evidence: The inline fixture preserves `ScriptContent` without introducing executor fields.
- [ ] CSharpTask executor mode round-trips.
  - Evidence: The executor fixture preserves `ExecutorTypeName` and `ExecutorAssemblyPath`.
- [ ] Mutually-exclusive branches are passed through faithfully rather than normalized away.
  - Evidence: Mixed-state tests assert the mapper preserves provided data and lets validation decide.
- [ ] Host integration tests cover both inline and executor variants.
  - Evidence: WebApplicationFactory runs both fixtures through canonicalization.
- [ ] Golden triplets exist for every variant touched by this step.
  - Evidence: Each fixture has `.yaml`, `.ui.json`, and `.golden.json` companions.
### 8. Unit tests
1. **CSharpScript_fixture_roundtrips**
   - Purpose: Covers script path/content mapping.
   - Harness: xUnit golden test
   - Arrange: Load the CSharpScript triplet.
   - Act: Map engine↔DTO and serialize back to YAML.
   - Assert: The YAML and DTO match the golden outputs.
2. **CSharpTask_inline_roundtrips**
   - Purpose: Covers inline-script task mapping.
   - Harness: xUnit golden test
   - Assertion focus: Inline script content survives canonical round-trip without executor fields appearing.
3. **CSharpTask_executor_roundtrips**
   - Purpose: Covers executor-mode task mapping.
   - Harness: xUnit golden test
   - Assertion focus: Executor type and assembly path survive canonical round-trip.
4. **CSharpTask_mixed_state_passthrough**
   - Purpose: Ensures the mapper does not silently choose a branch.
   - Harness: xUnit
   - Assertion focus: If both inline and executor fields are present, the mapper preserves them all for validation to inspect.
5. **CSharpScript_empty_path_with_content_preserved**
   - Purpose: Supports inline-only script authoring.
   - Harness: xUnit
   - Assertion focus: Empty `ScriptPath` with populated `ScriptContent` is preserved exactly.
6. **CSharpTask_empty_script_with_executor_preserved**
   - Purpose: Supports pure executor mode.
   - Harness: xUnit
   - Assertion focus: Executor fields survive even when script content is null or empty.
7. **Script_family_uses_MapBase**
   - Purpose: Prevents drift from the shared base contract.
   - Harness: xUnit
   - Assertion focus: Base fields still round-trip identically for script-family kinds.
8. **Script_family_fixture_goldens_are_canonical**
   - Purpose: Protects serializer behavior.
   - Harness: xUnit golden test
   - Assertion focus: Every emitted YAML fixture is already in canonical key order and formatting.
### 9. Integration tests
1. **Roundtrip_api_csharpscript_fixture**
   - Purpose: Exercise host mapper path for CSharpScript.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the CSharpScript triplet to the host round-trip surface.
   - Act: Read YAML/DTO outputs.
   - Assert: Both outputs match the golden files.
2. **Roundtrip_api_csharptask_inline_fixture**
   - Purpose: Exercise host mapper path for inline CSharpTask.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the inline task fixture.
   - Act: Inspect the host response.
   - Assert: Inline fields are preserved canonically.
3. **Roundtrip_api_csharptask_executor_fixture**
   - Purpose: Exercise host mapper path for executor CSharpTask.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the executor task fixture.
   - Act: Inspect the host response.
   - Assert: Executor fields are preserved canonically.
### 10. Golden-file / parity tests
- **Triplets per variant** — CSharpScript, CSharpTask-inline, and CSharpTask-executor each ship as `.yaml` + `.ui.json` + `.golden.json`.
- **Mixed-state parity note** — A negative or mixed-state fixture is optional for diagnostics but must never normalize fields silently.
- **Contract citations** — All mapped fields are anchored to `src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:12-49` and `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62`.
### 11. Edge cases & negative tests
- A CSharpTask DTO carries both executor fields and script content; mapper preserves both.
- A CSharpScript DTO carries empty `ScriptPath` but valid `ScriptContent`; round-trip remains canonical.
- An executor assembly path is blank while executor type name is populated; mapper preserves data and leaves validation to engine rules.
- A script contains multiline content; canonical YAML block formatting remains stable.
- A fixture omits optional fields entirely; mapper does not synthesize nulls unnecessarily.
### 12. Observability hooks
- **Structured log field set** — Emit `mapper.script_family.roundtrip` with `runtimeType`, `mode=inline|executor|mixed`, and `canonicalDiffBytes`.
- **Host trace** — Track `scriptLength`, `hasExecutorFields`, and `hasScriptPath` during mapping.
- **Golden metadata** — Include fixture variant names in snapshot outputs.
- **Failure diagnostics** — Surface whether a mismatch happened in YAML, DTO hydration, or engine validation.
### 13. Risks & mitigations
- **Risk:** Mapper logic incorrectly forces a branch for CSharpTask.
  - Mitigation: Treat mapping as a faithful transport layer and let engine validation handle exclusivity.
- **Risk:** Multiline scripts produce unstable YAML formatting.
  - Mitigation: Lock canonical fixtures and serializer settings through goldens.
- **Risk:** Script-family partial forgets to compose `MapBase`.
  - Mitigation: Add explicit tests that shared fields still round-trip for these kinds.
### 14. Evaluator gap-analysis prompts
1. How do the CSharpScript and CSharpTask partials differ, and what fields do they add beyond `MapBase`?
2. Which tests prove inline and executor CSharpTask modes both round-trip?
3. How is the mixed-state pass-through behavior documented and tested?
4. Which WebApplicationFactory test covers the host path for executor mode specifically?
5. How would an evaluator confirm canonical YAML stability for multiline script content?
6. What log fields show whether a given fixture used inline or executor mode?
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
- All script/task triplets round-trip through both unit and host integration harnesses.
- No mapper branch normalizes away mutually-exclusive engine fields.

### Step 3 — PowerShell mapper
### 1. Status
[ ]
### 2. Effort
0.5 d
### 3. Goal
Extend the mapper for `PowerShell` and `PowerShellTask`, preserving the exact engine property names `ScriptPath`, `ScriptContent`, `RequiredModules`, and `ModulePaths`. `PowerShell` remains path-only at the engine level, while `PowerShellTask` supports path or inline content. The mapper must not emit extra keys when a path-only PowerShell node has no inline content, and it must preserve module lists/maps exactly. Host and golden coverage should prove both variants remain canonical.
### 4. Depends on
- Step 2.2.4a
- Step 2.2.4b
### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the PowerShell and PowerShellTask mapping requirements.
- **PowerShell contract** — `src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:12-51` defines path-only PowerShell fields.
- **PowerShellTask contract** — `src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:12-57` defines optional path/content plus module fields.
- **Base mapper** — Step 5.1.1a already handles all shared base fields.
### 6. Deliverables
- **src/host/Mapping/**
  - `src/host/Mapping/EngineDtoMapper.PowerShellFamily.cs` (new) — Exports `MapPowerShell` and `MapPowerShellTask` partial methods.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/EngineDtoMapperPowerShellFamilyTests.cs` (new) — Unit coverage for path-only and inline/path PowerShell fixtures.
- **src/WorkflowUi.ContractTests/Fixtures/mapper/task/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.yaml` (new) — Canonical PowerShell path-only fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.ui.json` (new) — Sidecar for PowerShell path-only fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.golden.json` (new) — Hydrated PowerShell path-only golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.yaml` (new) — Canonical PowerShellTask inline fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.ui.json` (new) — Sidecar for PowerShellTask inline fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.golden.json` (new) — Hydrated PowerShellTask inline golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-path.yaml` (new) — Canonical PowerShellTask path fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-path.ui.json` (new) — Sidecar for PowerShellTask path fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-path.golden.json` (new) — Hydrated PowerShellTask path golden.
### 7. Acceptance criteria
- [ ] PowerShell path-only fixtures round-trip with no extra inline-content key.
  - Evidence: Golden tests inspect canonical YAML for absent inline content.
- [ ] PowerShellTask inline fixtures round-trip.
  - Evidence: Inline content survives unit and host integration tests.
- [ ] PowerShellTask path fixtures round-trip.
  - Evidence: Path-plus-modules values survive canonical round-trip.
- [ ] RequiredModules lists and ModulePaths maps preserve values and key casing.
  - Evidence: Unit tests compare collections field-for-field.
- [ ] Host integration covers all three fixtures touched by this step.
  - Evidence: WebApplicationFactory runs path-only, task-inline, and task-path fixtures.
- [ ] Triplet artifacts exist for every fixture variant.
  - Evidence: Each fixture has `.yaml`, `.ui.json`, and `.golden.json` companions.
### 8. Unit tests
1. **PowerShell_path_fixture_roundtrips**
   - Purpose: Covers path-only PowerShell mapping.
   - Harness: xUnit golden test
   - Arrange: Load the PowerShell path-only triplet.
   - Act: Round-trip through mapper and serializer.
   - Assert: Canonical YAML and hydrated DTO match the goldens.
2. **PowerShellTask_inline_fixture_roundtrips**
   - Purpose: Covers inline task mode.
   - Harness: xUnit golden test
   - Assertion focus: Inline content survives canonical round-trip.
3. **PowerShellTask_path_fixture_roundtrips**
   - Purpose: Covers path-based task mode.
   - Harness: xUnit golden test
   - Assertion focus: Path, required modules, and module paths survive round-trip.
4. **PowerShell_path_does_not_emit_scriptContent**
   - Purpose: Protects canonical omission behavior.
   - Harness: xUnit
   - Assertion focus: Path-only PowerShell YAML contains no `scriptContent` key.
5. **PowerShellTask_preserves_required_modules**
   - Purpose: Covers module-list fidelity.
   - Harness: xUnit
   - Assertion focus: RequiredModules round-trip in order and content.
6. **PowerShellTask_preserves_module_paths**
   - Purpose: Covers module-path map fidelity.
   - Harness: xUnit
   - Assertion focus: ModulePaths keys and values survive exactly.
7. **PowerShell_family_composes_MapBase**
   - Purpose: Keeps shared base fields intact.
   - Harness: xUnit
   - Assertion focus: Shared fields still round-trip identically for PowerShell kinds.
8. **PowerShell_family_goldens_are_canonical**
   - Purpose: Protects serializer stability.
   - Harness: xUnit golden test
   - Assertion focus: All emitted YAML fixtures are canonical and diff-clean.
### 9. Integration tests
1. **Roundtrip_api_powershell_path**
   - Purpose: Exercise host mapper path for PowerShell.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the path-only fixture.
   - Act: Read host outputs.
   - Assert: Canonical YAML omits inline content and matches goldens.
2. **Roundtrip_api_powershelltask_inline**
   - Purpose: Exercise host mapper path for inline PowerShellTask.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the inline task fixture.
   - Act: Read host outputs.
   - Assert: Inline content is preserved canonically.
3. **Roundtrip_api_powershelltask_path**
   - Purpose: Exercise host mapper path for path-based PowerShellTask.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the path fixture.
   - Act: Read host outputs.
   - Assert: Path and module settings match goldens.
### 10. Golden-file / parity tests
- **Triplets per PowerShell variant** — PowerShell path-only, PowerShellTask inline, and PowerShellTask path each have full triplets.
- **Inline omission regression** — The path-only fixture golden proves `scriptContent` is not emitted for the `PowerShell` runtime type.
- **Contract citations** — Mapped fields stay anchored to `src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:12-51` and `src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:12-57`.
### 11. Edge cases & negative tests
- RequiredModules is null rather than empty; mapper preserves null/absence canonically.
- ModulePaths contains Windows-style paths; mapper preserves strings without attempting path normalization.
- PowerShellTask carries both path and inline content; mapper preserves both and leaves validation to engine rules.
- A fixture contains module names with differing casing; round-trip preserves the original map keys.
- An empty path-only PowerShell node is mapped; validation later catches it, but the mapper preserves the DTO faithfully.
### 12. Observability hooks
- **Structured log field set** — Emit `mapper.powershell_family.roundtrip` with `runtimeType`, `hasInlineContent`, `requiredModuleCount`, and `modulePathCount`.
- **Host trace** — Record whether `scriptContent` was omitted or emitted in canonical YAML.
- **Golden metadata** — Include variant names (`path`, `task-inline`, `task-path`) in snapshot output.
- **Failure diagnostics** — Surface collection-count mismatches separately from scalar-field mismatches.
### 13. Risks & mitigations
- **Risk:** Path-only PowerShell accidentally emits inline fields.
  - Mitigation: Keep an omission regression test and canonical YAML golden.
- **Risk:** Module collections are coerced or reordered.
  - Mitigation: Compare collection contents exactly in unit tests and goldens.
- **Risk:** PowerShellTask mixed-state behavior gets normalized away.
  - Mitigation: Preserve source data and defer exclusivity to validation.
### 14. Evaluator gap-analysis prompts
1. What differentiates `PowerShell` from `PowerShellTask` in mapper behavior?
2. Which test proves path-only PowerShell does not emit `scriptContent`?
3. How are `RequiredModules` and `ModulePaths` preserved exactly?
4. Which host integration tests cover inline and path-based PowerShellTask separately?
5. How would an evaluator verify that collection casing and ordering survive round-trip?
6. What log fields indicate whether canonical YAML omitted inline content correctly?
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
- All PowerShell family fixtures round-trip through unit and host integration tests.
- Path-only omission behavior is locked by an explicit golden regression test.

### Step 4 — Control-flow mapper (IfElse, Switch, ForEach, While)
### 1. Status
[ ]
### 2. Effort
0.75 d
### 3. Goal
Extend the mapper for the control-flow family so branch, switch, loop, and iteration fields round-trip exactly. This includes `Condition`, `Expression` + `Cases`, `CollectionExpression` + `ItemVariableName`, and `ConditionExpression` + `MaxIterations`. The mapper should preserve explicit defaults such as `While.MaxIterations = 1000` and `ForEach.ItemVariableName = "item"` without inventing extra keys. Control-flow fixtures must prove both normal values and default-preservation behavior. Because control-flow nodes drive downstream port behavior in the UI, field-name fidelity here is especially important.
### 4. Depends on
- Step 2.3.1–2.3.3b
### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the control-flow mapper scope and the `MaxIterations` default note.
- **IfElse contract** — `src/ExecutionEngine/Nodes/Definitions/IfElseNodeDefinition.cs:12-24` defines `Condition`.
- **Switch contract** — `src/ExecutionEngine/Nodes/Definitions/SwitchNodeDefinition.cs:12-39` defines `Expression` and `Cases`.
- **ForEach contract** — `src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28` defines `CollectionExpression` and `ItemVariableName` with default `item`.
- **While contract** — `src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35` defines `ConditionExpression` and `MaxIterations` with default `1000`.
### 6. Deliverables
- **src/host/Mapping/**
  - `src/host/Mapping/EngineDtoMapper.ControlFlowFamily.cs` (new) — Exports `MapIfElse`, `MapSwitch`, `MapForEach`, and `MapWhile` partial methods.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/EngineDtoMapperControlFlowFamilyTests.cs` (new) — Unit coverage for all four control-flow runtimes.
- **src/WorkflowUi.ContractTests/Fixtures/mapper/control/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.yaml` (new) — IfElse fixture triplet root.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.ui.json` (new) — IfElse sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.golden.json` (new) — IfElse hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.yaml` (new) — Switch fixture with three cases.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.ui.json` (new) — Switch sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.golden.json` (new) — Switch hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.yaml` (new) — ForEach fixture triplet root.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.ui.json` (new) — ForEach sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.golden.json` (new) — ForEach hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-default-max-iterations.yaml` (new) — While fixture covering default `MaxIterations`.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-default-max-iterations.ui.json` (new) — While sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-default-max-iterations.golden.json` (new) — While hydrated golden.
### 7. Acceptance criteria
- [ ] IfElse fixtures round-trip with `Condition` unchanged.
  - Evidence: Golden tests compare condition strings exactly.
- [ ] Switch fixtures round-trip with `Expression` and `Cases` unchanged.
  - Evidence: Golden tests compare the three-case map exactly.
- [ ] ForEach fixtures preserve `CollectionExpression` and default/non-default `ItemVariableName` values.
  - Evidence: Unit tests inspect both properties directly.
- [ ] While fixtures preserve `ConditionExpression` and canonical `MaxIterations` behavior.
  - Evidence: Default-value tests check omission vs explicit emission rules as specified.
- [ ] Host integration covers all four control-flow runtime types.
  - Evidence: WebApplicationFactory runs each fixture through the round-trip host surface.
- [ ] Every touched runtime has a full fixture triplet.
  - Evidence: The deliverable list includes `.yaml`, `.ui.json`, and `.golden.json` for each control-flow kind.
### 8. Unit tests
1. **IfElse_fixture_roundtrips**
   - Purpose: Covers simple branch condition mapping.
   - Harness: xUnit golden test
   - Arrange: Load the IfElse triplet.
   - Act: Round-trip through mapper and serializer.
   - Assert: Condition text matches the goldens exactly.
2. **Switch_fixture_roundtrips_three_cases**
   - Purpose: Covers case-map fidelity.
   - Harness: xUnit golden test
   - Assertion focus: The three-case map survives round-trip with keys and values intact.
3. **ForEach_fixture_roundtrips_item_variable**
   - Purpose: Covers iteration variable mapping.
   - Harness: xUnit golden test
   - Assertion focus: CollectionExpression and ItemVariableName survive round-trip.
4. **While_fixture_roundtrips_default_max_iterations**
   - Purpose: Covers default-value handling.
   - Harness: xUnit golden test
   - Assertion focus: Default `1000` survives canonical round-trip according to the spec’s omission rule.
5. **While_fixture_roundtrips_non_default_max_iterations**
   - Purpose: Covers explicit non-default values.
   - Harness: xUnit
   - Assertion focus: A non-default `MaxIterations` value is emitted and rehydrated exactly.
6. **ForEach_default_item_variable_is_preserved**
   - Purpose: Covers default `item` behavior.
   - Harness: xUnit
   - Assertion focus: Default `item` is preserved canonically.
7. **Control_flow_family_composes_MapBase**
   - Purpose: Keeps shared fields intact for control nodes.
   - Harness: xUnit
   - Assertion focus: Base fields still round-trip identically for all four runtimes.
8. **Control_flow_goldens_are_canonical**
   - Purpose: Protects serializer output.
   - Harness: xUnit golden test
   - Assertion focus: All emitted control-flow YAML is canonical and diff-clean.
### 9. Integration tests
1. **Roundtrip_api_ifelse_fixture**
   - Purpose: Exercise host mapper path for IfElse.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the IfElse triplet.
   - Act: Read host outputs.
   - Assert: Canonical YAML and hydrated DTO match the goldens.
2. **Roundtrip_api_switch_fixture**
   - Purpose: Exercise host mapper path for Switch.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the three-case Switch fixture.
   - Act: Read host outputs.
   - Assert: Case maps remain intact and canonical.
3. **Roundtrip_api_loop_fixtures**
   - Purpose: Exercise host mapper path for ForEach and While.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit ForEach and While fixtures.
   - Act: Read host outputs.
   - Assert: Loop expressions, defaults, and max-iteration values round-trip correctly.
### 10. Golden-file / parity tests
- **Per-kind triplets** — IfElse, Switch, ForEach, and While each ship as `.yaml` + `.ui.json` + `.golden.json` artifacts.
- **Default-value regression** — While and ForEach goldens explicitly document default `MaxIterations` and `ItemVariableName` handling.
- **Contract citations** — Mapped fields stay anchored to `src/ExecutionEngine/Nodes/Definitions/IfElseNodeDefinition.cs:12-24`, `src/ExecutionEngine/Nodes/Definitions/SwitchNodeDefinition.cs:12-39`, `src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28`, and `src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35`.
### 11. Edge cases & negative tests
- Switch contains empty case keys or values; mapper preserves data and lets engine validation report the problem.
- ForEach omits `ItemVariableName`; mapper preserves engine-default behavior rather than inventing a new UI default.
- While receives a negative `MaxIterations`; mapper carries the value through and validation later reports it.
- Expressions contain characters that require YAML quoting; canonical serialization stays stable.
- A control-flow fixture omits optional base fields; `MapBase` still keeps the output canonical.
### 12. Observability hooks
- **Structured log field set** — Emit `mapper.control_flow.roundtrip` with `runtimeType`, `caseCount`, `hasDefaultLoopValues`, and `canonicalDiffBytes`.
- **Host trace** — Track `expressionLength` and `loopMaxIterations` during host round-trips.
- **Golden metadata** — Annotate snapshots with control-flow kind and fixture name.
- **Failure diagnostics** — Separate scalar-field mismatches from case-map mismatches in test output.
### 13. Risks & mitigations
- **Risk:** Default loop values drift from engine assumptions.
  - Mitigation: Keep explicit default-value fixtures and tests for While and ForEach.
- **Risk:** Case-map ordering or quoting becomes unstable.
  - Mitigation: Rely on canonical serializer output and snapshot the resulting YAML.
- **Risk:** Mapper logic accidentally validates or normalizes expressions.
  - Mitigation: Treat expressions as opaque strings and leave semantics to engine validation.
### 14. Evaluator gap-analysis prompts
1. How does the control-flow mapper handle default `MaxIterations` and `ItemVariableName` values?
2. Which test proves the Switch case map survives round-trip intact?
3. How are invalid but representable loop values treated by the mapper?
4. Which host integration test exercises the loop runtime types specifically?
5. What fixture would an evaluator inspect to understand canonical quoting for expressions?
6. What logs distinguish a case-map mismatch from a scalar-field mismatch?
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
- All four control-flow runtimes have passing unit and host round-trip coverage.
- Default-loop-value behavior is explicitly documented in the goldens.

### Step 5 — Composition mapper (Subflow, Timer, Container)
### 1. Status
[ ]
### 2. Effort
0.5 d
### 3. Goal
Extend the mapper for the composition family, covering nested workflows, schedules, and inline child graphs. `Subflow` must preserve `WorkflowFilePath`, optional inline `WorkflowDefinition`, input/output mappings, `Timeout`, and `SkipValidation`; `Timer` carries `Schedule`; and `Container` carries `ChildNodes`, `ChildConnections`, and `ExecutionMode`. This step must preserve TimeSpan formatting and nested child graph fidelity without leaking UI-only metadata into engine-owned structures. Because Container mapping nests nodes and connections recursively, it also serves as a dress rehearsal for the connection mapper that follows.
### 4. Depends on
- Step 2.4.1
- Step 2.4.2
- Step 2.4.3
### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the composition-family mapping requirements including TimeSpan fidelity and container execution mode.
- **Subflow contract** — `src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60` defines file path, inline workflow, mappings, timeout, and skip-validation fields.
- **Timer contract** — `src/ExecutionEngine/Nodes/Definitions/TimerNodeDefinition.cs:12-24` defines the schedule field.
- **Container contract** — `src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:14-32` and `src/ExecutionEngine/Enums/ExecutionMode.cs:9-13` define child graph fields and execution mode values.
- **Workflow graph** — `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` defines the nested graph structures used by inline subflows and containers.
### 6. Deliverables
- **src/host/Mapping/**
  - `src/host/Mapping/EngineDtoMapper.CompositionFamily.cs` (new) — Exports `MapSubflow`, `MapTimer`, and `MapContainer` partial methods.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/EngineDtoMapperCompositionFamilyTests.cs` (new) — Unit coverage for Subflow, Timer, and Container fixtures.
- **src/WorkflowUi.ContractTests/Fixtures/mapper/composition/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.yaml` (new) — Subflow file-path fixture triplet root.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.ui.json` (new) — Subflow file-path sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.golden.json` (new) — Subflow file-path hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.yaml` (new) — Inline-subflow fixture triplet root.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.ui.json` (new) — Inline-subflow sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.golden.json` (new) — Inline-subflow hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.yaml` (new) — Timer fixture triplet root.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.ui.json` (new) — Timer sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.golden.json` (new) — Timer hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.yaml` (new) — Container fixture with two child nodes in parallel mode.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.ui.json` (new) — Container sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.golden.json` (new) — Container hydrated golden.
### 7. Acceptance criteria
- [ ] Subflow file-path fixtures round-trip.
  - Evidence: Golden tests compare file-path, mapping, timeout, and skip-validation fields exactly.
- [ ] Subflow inline-workflow fixtures round-trip.
  - Evidence: Nested workflow definition content survives canonical round-trip.
- [ ] Timer fixtures preserve `Schedule` unchanged.
  - Evidence: Golden tests compare schedule strings exactly.
- [ ] Container fixtures preserve child nodes, child connections, and `ExecutionMode`.
  - Evidence: Golden tests compare nested graph structure and `Parallel` mode exactly.
- [ ] TimeSpan values preserve canonical formatting agreed with the engine serializer.
  - Evidence: Subflow timeout goldens lock the serialized representation.
- [ ] Host integration covers all composition variants in this step.
  - Evidence: WebApplicationFactory runs the four fixture variants through the round-trip host surface.
### 8. Unit tests
1. **Subflow_file_fixture_roundtrips**
   - Purpose: Covers file-path subflow mapping.
   - Harness: xUnit golden test
   - Arrange: Load the file-path subflow triplet.
   - Act: Round-trip through mapper and serializer.
   - Assert: Canonical YAML and DTO match the goldens.
2. **Subflow_inline_fixture_roundtrips**
   - Purpose: Covers inline-workflow subflow mapping.
   - Harness: xUnit golden test
   - Assertion focus: The inline nested workflow survives canonical round-trip.
3. **Timer_fixture_roundtrips**
   - Purpose: Covers timer schedule mapping.
   - Harness: xUnit golden test
   - Assertion focus: The schedule string survives round-trip unchanged.
4. **Container_fixture_roundtrips_parallel_mode**
   - Purpose: Covers container nested-graph mapping.
   - Harness: xUnit golden test
   - Assertion focus: Child nodes, child connections, and `Parallel` execution mode survive round-trip.
5. **Subflow_timeout_preserves_format**
   - Purpose: Covers TimeSpan formatting.
   - Harness: xUnit
   - Assertion focus: Timeout serializes and deserializes in the canonical engine-compatible format.
6. **Subflow_skip_validation_preserved**
   - Purpose: Covers the boolean policy-like field.
   - Harness: xUnit
   - Assertion focus: SkipValidation retains its value across mapping.
7. **Container_family_composes_MapBase**
   - Purpose: Keeps shared base fields intact for composition kinds.
   - Harness: xUnit
   - Assertion focus: Base fields still round-trip identically for Subflow, Timer, and Container.
8. **Composition_family_goldens_are_canonical**
   - Purpose: Protects serializer stability for nested graphs.
   - Harness: xUnit golden test
   - Assertion focus: All emitted YAML is canonical and diff-clean.
### 9. Integration tests
1. **Roundtrip_api_subflow_variants**
   - Purpose: Exercise host mapper paths for file and inline Subflow.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit both subflow fixtures.
   - Act: Read the host outputs.
   - Assert: Both variants match their goldens and validate.
2. **Roundtrip_api_timer_fixture**
   - Purpose: Exercise host mapper path for Timer.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the timer fixture.
   - Act: Read host outputs.
   - Assert: Schedule and base fields match the goldens.
3. **Roundtrip_api_container_fixture**
   - Purpose: Exercise host mapper path for Container.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the container fixture with child graph.
   - Act: Read host outputs.
   - Assert: Nested child graph and `Parallel` mode match the goldens.
### 10. Golden-file / parity tests
- **Triplets per composition variant** — Subflow-file, Subflow-inline, Timer, and Container each have full triplets.
- **TimeSpan format regression** — Subflow timeout goldens make any serialization-format drift visible immediately.
- **Contract citations** — Mapped fields stay anchored to `src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60`, `src/ExecutionEngine/Nodes/Definitions/TimerNodeDefinition.cs:12-24`, `src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:14-32`, and `src/ExecutionEngine/Enums/ExecutionMode.cs:9-13`.
### 11. Edge cases & negative tests
- Subflow contains both file path and inline workflow; mapper preserves both and leaves validation to engine rules.
- Container child graph is empty; mapper preserves the structure and validation later reports the issue.
- Timeout uses a format that differs from the engine canonical representation; the serializer golden exposes the drift.
- Container execution mode is missing; mapper preserves engine-default `Sequential` behavior unless fixture sets `Parallel`.
- Nested child connections contain metadata fields later handled by the connection mapper; current step preserves only what the current contract allows.
### 12. Observability hooks
- **Structured log field set** — Emit `mapper.composition_family.roundtrip` with `runtimeType`, `childNodeCount`, `childConnectionCount`, `hasInlineWorkflow`, and `canonicalDiffBytes`.
- **Host trace** — Track `timeoutFormat`, `scheduleLength`, and `executionMode` during host round-trips.
- **Golden metadata** — Annotate composition snapshots with variant type and nested-graph counts.
- **Failure diagnostics** — Surface nested-child mismatch details separately from scalar-field mismatches.
### 13. Risks & mitigations
- **Risk:** Nested graphs leak UI-only metadata into engine structures.
  - Mitigation: Keep sidecar merging separate and assert canonical YAML cleanliness.
- **Risk:** TimeSpan formatting drifts from engine serializer behavior.
  - Mitigation: Lock timeout goldens and rely on the engine serializer for canonical output.
- **Risk:** Container recursion makes debugging diffs difficult.
  - Mitigation: Include nested count metadata in logs and golden snapshot names.
### 14. Evaluator gap-analysis prompts
1. How are Subflow file-path and inline-workflow variants distinguished in the mapper?
2. Which test proves `Parallel` execution mode survives container round-trip?
3. How is canonical timeout formatting enforced?
4. What evidence shows nested child graphs round-trip without UI metadata leakage?
5. Which host integration test covers the container fixture specifically?
6. What log fields help diagnose a nested child-graph mismatch?
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
- All composition variants round-trip through unit and host integration harnesses.
- Timeout and nested-graph behavior are locked by explicit goldens.

### Step 6 — Connection mapper + sidecar merger
### 1. Status
[ ]
### 2. Effort
1.5 d
### 3. Goal
Implement the connection mapper for all nine `NodeConnection` fields and the sidecar merger that hydrates UI-only metadata without leaking it back into YAML. This is the step that proves edge editing from Phase 3 can persist as canonical engine connections while viewport, color, and annotation metadata remain isolated in `*.ui.json`. The mapper must preserve null/absent semantics for optional connection fields and canonical omission for default `TriggerMessageType = Complete`, `IsEnabled = true`, and `Priority = 0`. `Metadata` remains opaque and must retain keys and scalar value types exactly. The merger must also strip any UI-only properties before save, even if callers accidentally attach them to edge DTOs.
### 4. Depends on
- Step 5.1.1e
- Step 2.4.5
### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the connection and sidecar-merger requirements for Step 5.1.2.
- **Connection contract** — `src/ExecutionEngine/Workflow/NodeConnection.cs:21-99` defines all nine connection properties and their defaults.
- **Workflow graph** — `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` defines where connections live inside a workflow definition.
- **Serialization boundary** — `src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149` and `src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212` remain the only YAML entry/exit points.
### 6. Deliverables
- **src/host/Mapping/**
  - `src/host/Mapping/ConnectionMapper.cs` (new) — Exports `MapConnectionToDto` and `MapConnectionToEngine` across all nine fields.
  - `src/host/Mapping/SidecarMerger.cs` (new) — Exports `HydrateWithSidecar` and `StripUiOnlyState` for workflow + sidecar composition.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/ConnectionMapperTests.cs` (new) — Unit coverage for all five message types, optional fields, metadata, and defaults.
  - `src/host/__tests__/Mapping/SidecarMergerTests.cs` (new) — Unit coverage for UI-only leak prevention and sidecar hydration.
  - `src/host/__tests__/Mapping/ConnectionRoundTripApiTests.cs` (new) — WebApplicationFactory coverage for host round-trips involving edges and sidecar metadata.
- **src/WorkflowUi.ContractTests/Fixtures/mapper/connections/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/connections/all-message-types.yaml` (new) — Canonical connection fixture covering all message types.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/connections/all-message-types.ui.json` (new) — Sidecar fixture with viewport and node positions.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/connections/all-message-types.golden.json` (new) — Hydrated DTO golden for the connection fixture.
### 7. Acceptance criteria
- [ ] All nine connection fields round-trip exactly.
  - Evidence: Unit tests compare source and round-tripped DTOs field-for-field.
- [ ] Default `Complete`, `true`, and `0` values serialize canonically to omitted keys.
  - Evidence: Canonical YAML goldens and omission tests cover the default cases.
- [ ] Optional `Condition`, `Metadata`, `SourcePort`, and `TargetPort` preserve null/absent semantics.
  - Evidence: Tests distinguish omitted values from explicitly-populated values.
- [ ] Metadata preserves keys and scalar value types exactly.
  - Evidence: Mixed-scalar metadata tests compare types as well as values.
- [ ] UI-only sidecar fields never leak into YAML.
  - Evidence: Leak-prevention tests inject fake UI-only properties and assert the serialized YAML stays clean.
- [ ] Host integration preserves positions and edge properties together.
  - Evidence: WebApplicationFactory round-trip tests submit YAML + sidecar and verify hydrated UI state plus canonical YAML.
### 8. Unit tests
1. **ConnectionMapper_roundtrips_all_nine_fields**
   - Purpose: Covers the full connection contract.
   - Harness: xUnit
   - Arrange: Create a DTO with every connection field populated.
   - Act: Map to engine and back.
   - Assert: All nine fields match exactly.
2. **ConnectionMapper_omits_default_triggerMessageType**
   - Purpose: Covers canonical default omission.
   - Harness: xUnit golden test
   - Assertion focus: `TriggerMessageType = Complete` is omitted from canonical YAML when it is the default.
3. **ConnectionMapper_omits_default_isEnabled_and_priority**
   - Purpose: Covers the other canonical defaults.
   - Harness: xUnit golden test
   - Assertion focus: Default `IsEnabled` and `Priority` omit cleanly from canonical YAML.
4. **ConnectionMapper_roundtrips_each_message_type**
   - Purpose: Covers the five enum values explicitly.
   - Harness: xUnit theory
   - Assertion focus: Each `MessageType` survives round-trip and hydrates the same enum value.
5. **ConnectionMapper_preserves_metadata_scalar_types**
   - Purpose: Protects the opaque metadata bag.
   - Harness: xUnit
   - Assertion focus: String, bool, int, and double metadata values return with the same types.
6. **SidecarMerger_hydrates_ui_only_state**
   - Purpose: Covers load-time UI metadata hydration.
   - Harness: xUnit
   - Assertion focus: Node positions, viewport, and labels are hydrated from `.ui.json` without touching engine DTO fields.
7. **SidecarMerger_strips_ui_only_fields_before_save**
   - Purpose: Implements the leak-prevention rule.
   - Harness: xUnit
   - Assertion focus: Injected UI-only properties are absent from serialized YAML output.
8. **Connection_fixture_goldens_are_canonical**
   - Purpose: Protects canonical YAML for connection-heavy workflows.
   - Harness: xUnit golden test
   - Assertion focus: The all-message-types fixture round-trips diff-clean.
### 9. Integration tests
1. **Roundtrip_api_preserves_edge_properties**
   - Purpose: Exercise host round-trip with editable edge fields from Phase 3.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit a workflow containing conditions, priorities, and enabled flags.
   - Act: Read canonical YAML and hydrated DTO outputs.
   - Assert: All edge properties match the golden outputs.
2. **Roundtrip_api_preserves_sidecar_positions**
   - Purpose: Exercise UI-state hydration together with engine YAML.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit a YAML + sidecar pair with positions and viewport metadata.
   - Act: Read the hydrated host response.
   - Assert: UI-only state is present in the hydrated DTO response but absent from canonical YAML.
3. **Roundtrip_api_rejects_ui_only_leakage**
   - Purpose: Cover accidental UI-only data on the engine side.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Inject fake UI-only fields into a request payload.
   - Act: Round-trip through the host.
   - Assert: The host strips them before YAML serialization and returns clean output.
### 10. Golden-file / parity tests
- **All-message-type triplet** — One canonical fixture pair plus `.golden.json` exercises all five message types and all nine connection fields.
- **Metadata regression** — Mixed-scalar metadata in the golden fixture makes value-type drift visible immediately.
- **Leak-prevention diff** — A golden diff proves sidecar-only properties never appear in emitted YAML.
### 11. Edge cases & negative tests
- A connection omits both ports; mapper preserves null/absent values rather than forcing `in`/default strings.
- A connection contains empty metadata; mapper distinguishes null from empty object if the contract requires it.
- UI-only labels are attached to the wrong object; sidecar stripping still removes them from YAML.
- A connection uses `Cancel` message type with disabled flag false; mapper preserves both independently.
- Metadata values change numeric type (`1` vs `1.0`) due to serializer differences; goldens catch the drift.
### 12. Observability hooks
- **Structured log field set** — Emit `mapper.connection.roundtrip` with `messageType`, `metadataKeyCount`, `usedDefaultOmission`, and `canonicalDiffBytes`.
- **Host trace** — Track `sidecarNodePositionCount`, `viewportPresent`, and `uiOnlyLeakCount` during hydration/save.
- **Golden metadata** — Annotate connection goldens with fixture name and message-type coverage summary.
- **Failure diagnostics** — Separate YAML diff failures from hydrated-DTO diff failures in test output.
### 13. Risks & mitigations
- **Risk:** UI-only state leaks into engine YAML.
  - Mitigation: Strip UI-only fields centrally in the sidecar merger and regression-test the leak path.
- **Risk:** Connection default omission behavior becomes inconsistent.
  - Mitigation: Lock omissions with canonical YAML goldens and dedicated tests.
- **Risk:** Metadata loses scalar type fidelity.
  - Mitigation: Use mixed-scalar fixtures and assert value types, not just values.
### 14. Evaluator gap-analysis prompts
1. Which file handles all nine `NodeConnection` fields, and how is that completeness tested?
2. How are default omission rules for message type, enabled, and priority proven?
3. What evidence shows metadata scalar types are preserved exactly?
4. How does the sidecar merger hydrate positions and viewport without leaking them into YAML?
5. Which host integration scenario proves UI-only leakage is stripped before save?
6. What log fields would you inspect if a round-trip reintroduced `sourcePortId` or extra UI-only keys?
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
- Connection mapping and sidecar merging are both covered by unit and host integration suites.
- Leak-prevention and metadata-fidelity behaviors are visible in goldens.

### Step 7 — Mapper registry and kind factory
### 1. Status
[ ]
### 2. Effort
0.5 d
### 3. Goal
Finish the mapper stage with a runtime-type registry that dispatches to the correct partial method and fails exhaustively when a kind is missing. The registry should key off the engine `RuntimeType` enum and serve both engine→DTO and DTO→engine directions. This makes add-a-kind work obvious: the build or exhaustive test fails until a new runtime is registered. The registry should stay small, declarative, and reviewable. It is the final integration point that ties the family partials into a single mapper surface for the host.
### 4. Depends on
- Step 5.1.1a–e
### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the registry keyed on `RuntimeType` and the exhaustiveness requirement.
- **Runtime enum** — `src/ExecutionEngine/Enums/RuntimeType.cs:12-78` defines the exhaustive runtime key set the registry must cover.
- **Family partials** — Steps 5.1.1a–e contribute the per-family partial methods the registry composes.
### 6. Deliverables
- **src/host/Mapping/**
  - `src/host/Mapping/EngineDtoMapper.Registry.cs` (new) — Exports the runtime-type dispatch registry for DTO→engine and engine→DTO mapping.
  - `src/host/Mapping/MapperDiagnostics.cs` (new) — Exports diagnostics helpers for unknown or unregistered runtime types.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/EngineDtoMapperRegistryTests.cs` (new) — Exhaustiveness and missing-kind failure coverage.
### 7. Acceptance criteria
- [ ] Every runtime type has a registered mapper branch.
  - Evidence: Registry tests compare runtime enum values to registered handlers.
- [ ] A missing runtime type fails fast in tests or compilation.
  - Evidence: An exhaustive test or `switch` expression default throw makes missing mappings obvious.
- [ ] The registry dispatches correctly in both directions.
  - Evidence: Unit tests map DTO→engine and engine→DTO for representative fixtures across families.
- [ ] Registry code is declarative and centralized.
  - Evidence: Code review confirms runtime dispatch lives in one file rather than scattered switches.
- [ ] Unknown runtime values produce a clear diagnostic.
  - Evidence: Negative tests assert the thrown message or diagnostic output names the missing runtime type.
- [ ] Host mapping surfaces use the registry rather than calling family partials directly.
  - Evidence: Integration tests and code review confirm the host entry point goes through the registry.
### 8. Unit tests
1. **Registry_covers_every_runtime_type**
   - Purpose: Locks the exhaustive runtime set.
   - Harness: xUnit
   - Arrange: Enumerate `RuntimeType` and registry keys.
   - Act: Compare the sets.
   - Assert: Every runtime type is registered exactly once.
2. **Registry_maps_task_family_fixture**
   - Purpose: Proves dispatch works for task kinds.
   - Harness: xUnit
   - Assertion focus: A representative task fixture round-trips through the registry successfully.
3. **Registry_maps_control_flow_fixture**
   - Purpose: Proves dispatch works for control-flow kinds.
   - Harness: xUnit
   - Assertion focus: A representative control-flow fixture round-trips through the registry successfully.
4. **Registry_maps_composition_fixture**
   - Purpose: Proves dispatch works for composition kinds.
   - Harness: xUnit
   - Assertion focus: A representative composition fixture round-trips through the registry successfully.
5. **Registry_maps_connection_heavy_fixture**
   - Purpose: Ensures the registry composes with the connection mapper path.
   - Harness: xUnit
   - Assertion focus: A workflow containing edges and sidecar data round-trips through the top-level mapper.
6. **Unknown_runtime_type_throws_clear_error**
   - Purpose: Covers the negative path.
   - Harness: xUnit
   - Assertion focus: An unknown runtime type triggers a clear diagnostic naming the missing handler.
7. **Registry_entry_points_use_shared_family_partials**
   - Purpose: Prevents duplicate logic from creeping in.
   - Harness: xUnit
   - Assertion focus: The registry delegates to family partials rather than inlining mapping logic.
8. **Registry_exhaustiveness_guard_documented**
   - Purpose: Makes add-a-kind behavior obvious to evaluators.
   - Harness: xUnit or compile-time analyzer test
   - Assertion focus: Removing a registry branch causes a failing test or compilation guard.
### 9. Integration tests
1. **Top_level_mapper_api_roundtrips_mixed_workflow**
   - Purpose: Exercise the registry with a mixed-runtime workflow.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit a fixture containing task, control, and composition nodes.
   - Act: Round-trip through the host.
   - Assert: The host succeeds and outputs canonical YAML plus hydrated DTO matching goldens.
2. **Top_level_mapper_api_uses_registry_for_unknown_kind_failure**
   - Purpose: Cover the negative host path.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit a payload with an unrecognized runtime type via a test-only harness.
   - Act: Read the host failure response.
   - Assert: The response names the missing runtime type and fails cleanly.
3. **Top_level_mapper_api_handles_all_known_kinds**
   - Purpose: Final registry confidence check.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the all-kinds fixture corpus aggregate.
   - Act: Round-trip through the host.
   - Assert: Every known runtime type maps successfully.
### 10. Golden-file / parity tests
- **All-kinds aggregate snapshot** — A mixed-runtime golden fixture proves registry dispatch works across families in one workflow.
- **Runtime coverage snapshot** — Snapshot the registry key list so missing or extra registrations are reviewable in diffs.
- **Exhaustiveness citation** — Registry coverage stays anchored to `src/ExecutionEngine/Enums/RuntimeType.cs:12-78` and the plan parity gate `docs/workflow-ui-plan.md:15-77`.
### 11. Edge cases & negative tests
- A future runtime type is added to the engine enum but not to the registry; the exhaustive test fails immediately.
- A registry branch exists but points to the wrong family partial; mixed-fixture tests catch the mismatch.
- A host entry point bypasses the registry and calls a family partial directly; code review plus integration tests flag the divergence.
- A negative harness injects an invalid runtime string; the host returns a clear diagnostic instead of a null reference.
- Aggregate all-kinds fixtures include sidecar metadata and connections; registry dispatch still covers the whole graph.
### 12. Observability hooks
- **Structured log field set** — Emit `mapper.registry.dispatch` with `runtimeType`, `direction`, and `handlerName`.
- **Host trace** — Track `runtimeTypeCount` and `unknownRuntimeType` for top-level mapping requests.
- **Golden metadata** — Annotate aggregate snapshots with the runtime-type list included in the workflow.
- **Failure diagnostics** — Return an explicit `unregistered_runtime_type` error code for host negative-path responses.
### 13. Risks & mitigations
- **Risk:** Runtime dispatch becomes scattered and hard to audit.
  - Mitigation: Keep the registry in one file and route all top-level mapping through it.
- **Risk:** New kinds fail silently.
  - Mitigation: Use exhaustive tests and clear diagnostics for unknown runtime types.
- **Risk:** Family partials drift from the registry key list.
  - Mitigation: Snapshot runtime keys and test representative fixtures from every family through the registry.
### 14. Evaluator gap-analysis prompts
1. Where is the runtime-type dispatch registry defined, and how does it cover both directions?
2. Which tests prove the registry is exhaustive against the engine enum?
3. How does the host surface unknown runtime types when they appear?
4. What evidence shows all top-level mapping entry points use the registry?
5. Which aggregate fixture demonstrates mixed-family dispatch working together?
6. What log fields help diagnose a missing registry branch after a future add-a-kind change?
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
- The registry is the sole top-level dispatch path for all runtime kinds.
- Exhaustiveness failures are loud and evaluator-friendly.
