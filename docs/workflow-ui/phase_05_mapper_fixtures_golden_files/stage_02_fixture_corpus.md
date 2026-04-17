# Stage 5.2 — Fixture corpus (split per audit)

**Phase:** Mapper, Fixtures, Golden Files  
**Stage status:** [ ]  
**Total effort:** 3 d  
**Step count:** 3

## Steps

- [ ] **Step 1**: Task-family fixture corpus
- [ ] **Step 2**: Control-flow fixture corpus
- [ ] **Step 3**: Composition fixture corpus

---
## Context
Stage 5.2 grows the mapper coverage into a durable fixture corpus that future contributors and evaluators can rely on. The master plan split the corpus into three audited sub-steps to honor the ≤20-file budget, but the updated output rules also require a `.golden.json` alongside every `.yaml` and `.ui.json` fixture (docs/workflow-ui-plan.md:902-1025). The result is a triplet-based corpus rather than the original pair-based wording, which is still compatible with the budget because each sub-step remains below twenty authored files.
These fixtures are not casual samples; they are the authoritative contract corpus for round-trip, validation, and golden-file review. Each file set must deserialize through `WorkflowLoader`, serialize through `WorkflowSerializer`, and map through the DTO layer without field drift (src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149, src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212). The corpus must also keep kind names, defaults, connection fields, and sidecar metadata aligned with the cited engine classes and enums.
The corpus is deliberately organized by task-family, control-flow, and composition so failures point to the smallest possible surface area. One invalid control-flow fixture is also included to ensure the harness can prove rejection behavior rather than only happy paths.

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
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-invalid-empty-case.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-invalid-empty-case.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-invalid-empty-case.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-basic.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-basic.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-basic.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.yaml`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.golden.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.ui.json`
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.yaml`
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
- `src/host/__tests__/Mapping/CompositionFixtureTheoryTests.cs`
- `src/host/__tests__/Mapping/ControlFlowFixtureTheoryTests.cs`
- `src/host/__tests__/Mapping/TaskFamilyFixtureTheoryTests.cs`

## Shared fixtures / fakes
- `src/WorkflowUi.ContractTests/Fixtures/mapper/task/` — happy-path task-family triplets.
- `src/WorkflowUi.ContractTests/Fixtures/mapper/control/` — happy-path control-flow triplets plus one intentionally invalid case.
- `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/` — composition-family triplets covering inline and nested workflow forms.
- `src/WorkflowUi.ContractTests/FixtureManifest.json` — optional manifest enumerating every fixture name, runtime family, and expected result.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="5.2"`, `phaseId="5"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-5.2.plan-read`, `workflow-ui.stage-5.2.apply`, `workflow-ui.stage-5.2.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_5_2_started_total`, counter `workflow_ui_stage_5_2_failed_total`, histogram `workflow_ui_stage_5_2_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| mapper | base field omitted or renamed | golden/parity tests fail | trace mapper against cited engine DTO and regenerate fixture |
| fixture corpus | sample YAML loses canonical form | unified diff / golden test fails | re-emit through engine serializer and inspect diff artifact |
| sidecar merge | UI metadata leaks into semantic YAML | round-trip parity fails | keep YAML and sidecar writes on separate code paths |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| DTO <-> engine map (200 nodes) | 40 ms | 150 ms |
| golden diff generation | 300 ms | 1000 ms |
| fixture hydration / snapshot save | 150 ms | 500 ms |

## Security callouts
- Mapper code must reject unknown discriminators and never instantiate arbitrary CLR types from client strings.
- Keep sidecar metadata separated from semantic workflow payloads so untrusted UI state cannot alter execution semantics.
- Golden fixtures that contain file paths or module names must avoid secrets and machine-specific roots.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 5.2 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage52` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

### Step 1 — Task-family fixture corpus

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Author the canonical task-family fixture triplets used by mapper, validation, and golden-file tests. The set must cover Noop, compiled CSharp, CSharpScript, CSharpTask inline, PowerShell path-only, and PowerShellTask inline/path behavior as defined by the master plan. Each fixture includes canonical YAML, a sidecar file, and a hydrated golden DTO snapshot so reviewers can inspect both engine and UI projections. The authored examples should prefer small but representative workflows with stable node ids and minimal noise. The task-family set becomes the baseline corpus for later aggregate and all-kinds tests.

### 4. Depends on
- Step 5.1.1a–c

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the task-family corpus and round-trip expectations.
- **Task-family engine contracts** — Use `src/ExecutionEngine/Nodes/Definitions/NoopNodeDefinition.cs:12-19`, `src/ExecutionEngine/Nodes/Definitions/CSharpNodeDefinition.cs:13-59`, `src/ExecutionEngine/Nodes/Definitions/CSharpScriptNodeDefinition.cs:12-49`, `src/ExecutionEngine/Nodes/Definitions/CSharpTaskNodeDefinition.cs:13-62`, `src/ExecutionEngine/Nodes/Definitions/PowerShellScriptNodeDefinition.cs:12-51`, and `src/ExecutionEngine/Nodes/Definitions/PowerShellTaskNodeDefinition.cs:12-57` as the authoritative field sources.
- **Mapper behavior** — Stage 5.1 established the per-kind mapper partials that these fixtures must exercise.

### 6. Deliverables
- **src/WorkflowUi.ContractTests/Fixtures/mapper/task/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.yaml` (new) — Noop happy-path YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.ui.json` (new) — Sidecar paired with the Noop fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/noop-basic.golden.json` (new) — Hydrated Noop golden snapshot.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.yaml` (new) — Compiled CSharp YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.ui.json` (new) — Sidecar paired with compiled CSharp.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-compiled.golden.json` (new) — Hydrated compiled CSharp golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.yaml` (new) — CSharpScript inline fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.ui.json` (new) — Sidecar paired with CSharpScript inline.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-script-inline.golden.json` (new) — Hydrated CSharpScript inline golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.yaml` (new) — CSharpTask inline fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.ui.json` (new) — Sidecar paired with CSharpTask inline.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/csharp-task-inline.golden.json` (new) — Hydrated CSharpTask inline golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.yaml` (new) — PowerShell path-only fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.ui.json` (new) — Sidecar paired with PowerShell path-only.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-path.golden.json` (new) — Hydrated PowerShell path-only golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.yaml` (new) — PowerShellTask inline fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.ui.json` (new) — Sidecar paired with PowerShellTask inline.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/task/powershell-task-inline.golden.json` (new) — Hydrated PowerShellTask inline golden.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/TaskFamilyFixtureTheoryTests.cs` (new) — Data-driven round-trip theory for the authored task-family corpus.

### 7. Acceptance criteria
- [ ] Every authored task-family fixture passes mapper round-trip and engine validation.
  - Evidence: Theory tests load each triplet, round-trip it, and validate the canonical workflow.
- [ ] Every authored fixture has `.yaml`, `.ui.json`, and `.golden.json` artifacts.
  - Evidence: A manifest or directory enumeration test asserts the triplet count and names.
- [ ] The corpus covers the runtime variants promised in the stage scope.
  - Evidence: Fixture-manifest tests compare expected fixture ids to actual files.
- [ ] Canonical YAML is stable and comment-free for diff review.
  - Evidence: Round-trip theories compare emitted YAML to the authored source exactly.
- [ ] Sidecar files carry only UI metadata and no engine fields.
  - Evidence: Leak tests inspect `.ui.json` content and ensure engine-owned fields are absent.
- [ ] Hydrated golden snapshots match the mapper output from the host path.
  - Evidence: WebApplicationFactory or host round-trip theories compare returned DTOs to `.golden.json`.

### 8. Unit tests
1. **Task_family_manifest_matches_authored_triplets**
   - Purpose: Checks corpus completeness.
   - Harness: xUnit
   - Arrange: Enumerate authored task-family fixture files.
   - Act: Group them by fixture id and extension.
   - Assert: Each expected fixture id has all three file types.
2. **Task_family_roundtrip_theory**
   - Purpose: Covers the happy-path corpus end to end.
   - Harness: xUnit theory
   - Assertion focus: Every authored task-family triplet round-trips canonically.
3. **Task_family_validation_theory**
   - Purpose: Ensures engine validation acceptance.
   - Harness: xUnit theory
   - Assertion focus: Every authored fixture validates successfully through the engine validator.
4. **Task_family_sidecars_contain_ui_only_state**
   - Purpose: Protects sidecar cleanliness.
   - Harness: xUnit
   - Assertion focus: Task-family `.ui.json` files contain viewport/position metadata only.
5. **Task_family_goldens_match_mapper_output**
   - Purpose: Covers hydrated DTO fidelity.
   - Harness: xUnit theory
   - Assertion focus: Hydrated DTO output matches the `.golden.json` companion for each fixture.
6. **Task_family_fixture_ids_are_stable**
   - Purpose: Prevents churn in downstream golden file names.
   - Harness: xUnit
   - Assertion focus: The fixture ids match the expected naming convention exactly.
7. **Task_family_yaml_is_canonical**
   - Purpose: Protects diff readability.
   - Harness: xUnit theory
   - Assertion focus: Re-serializing each YAML fixture yields byte-identical output.
8. **Task_family_host_roundtrip_matches_goldens**
   - Purpose: Connects file corpus to host behavior.
   - Harness: xUnit + WebApplicationFactory
   - Assertion focus: Host round-trip output matches each `.golden.json` snapshot.

### 9. Integration tests
1. **Host_roundtrip_all_task_family_fixtures**
   - Purpose: Exercise every task-family triplet through the server path.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Iterate over the task-family fixture ids.
   - Act: Submit each fixture to the host round-trip surface.
   - Assert: Canonical YAML and hydrated DTO outputs match authored goldens.
2. **Host_validate_all_task_family_fixtures**
   - Purpose: Exercise the server validation path for the corpus.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit each task-family fixture to `/validate`.
   - Act: Read validation responses.
   - Assert: Every fixture is accepted without blocking errors.
3. **Task_family_sidecar_hydration**
   - Purpose: Verify sidecar metadata merges with task-family workflows.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit YAML + `.ui.json` pairs.
   - Act: Read hydrated response DTOs.
   - Assert: UI metadata reappears only in the hydrated view, not the canonical YAML.

### 10. Golden-file / parity tests
- **Triplet contract** — Every fixture uses the required `.yaml` + `.ui.json` + `.golden.json` triplet format.
- **Canonical task-family diff** — Round-trip goldens make any mapper or serializer drift visible immediately.
- **Family coverage note** — The task-family corpus is the baseline consumed later by aggregate golden and parity tests.

### 11. Edge cases & negative tests
- A fixture is missing its `.golden.json`; manifest tests fail before mapper theories run.
- A sidecar file accidentally contains engine-owned fields; sidecar cleanliness tests fail immediately.
- A YAML fixture is not canonical; re-serialization theories surface a byte diff.
- A fixture id naming change would break downstream harness expectations; stable-id tests catch it.
- A host round-trip passes unit mapping but diverges in canonical YAML formatting; server theories expose the mismatch.

### 12. Observability hooks
- **Fixture manifest log** — Emit fixture id, runtime family, and triplet completeness for every corpus run.
- **Host trace** — Track `fixtureId`, `family=task`, `validationResult`, and `canonicalDiffBytes` in server integration runs.
- **Golden metadata** — Annotate failures with fixture id and expected file paths for rapid triage.
- **Corpus metric** — Record authored fixture counts per family so missing files are obvious in CI output.

### 13. Risks & mitigations
- **Risk:** Corpus files drift from mapper logic and become stale.
  - Mitigation: Run all fixtures through both mapper and host paths in CI.
- **Risk:** File counts exceed the step budget.
  - Mitigation: Keep the task family to six triplet-style fixtures plus one theory file, remaining under the limit.
- **Risk:** Triplet names become inconsistent and hard to consume.
  - Mitigation: Enforce naming conventions in manifest tests.

### 14. Evaluator gap-analysis prompts
1. How many task-family fixture triplets are authored, and which runtime variants do they cover?
2. What proves every task-family fixture is canonical YAML, not just parseable YAML?
3. How are sidecar files checked for UI-only cleanliness?
4. Which host integration suite runs the entire task-family corpus?
5. How would an evaluator confirm that the corpus stays under the file-budget rule?
6. What manifest or naming tests make missing triplet components obvious?

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
- Task-family fixture triplets are complete, canonical, and exercised by theory tests.
- Host round-trip and validation coverage both pass for the full task-family corpus.

### Step 2 — Control-flow fixture corpus

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Author the control-flow fixture triplets and one intentionally invalid control-flow case used to prove rejection behavior. The happy-path set covers IfElse, Switch with three cases, ForEach, and While. The negative fixture must be invalid for a clear engine reason — for example, an empty switch case value or missing condition — and the validation harness must assert the specific error path rather than simply expecting failure. These fixtures make the control-flow mapper and validation logic reviewable without needing browser interaction. They also become the seed set for later graph-rule and branch-behavior tests.

### 4. Depends on
- Step 5.1.1d

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the control-flow corpus and requires one corrupt/invalid case.
- **Control-flow engine contracts** — Use `src/ExecutionEngine/Nodes/Definitions/IfElseNodeDefinition.cs:12-24`, `src/ExecutionEngine/Nodes/Definitions/SwitchNodeDefinition.cs:12-39`, `src/ExecutionEngine/Nodes/Definitions/ForEachNodeDefinition.cs:12-28`, and `src/ExecutionEngine/Nodes/Definitions/WhileNodeDefinition.cs:12-35` as the authoritative field sources.
- **Engine validation rules** — `src/ExecutionEngine/Workflow/WorkflowValidator.cs:40-285` and per-node validation methods define what makes the negative fixture invalid.

### 6. Deliverables
- **src/WorkflowUi.ContractTests/Fixtures/mapper/control/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.yaml` (new) — IfElse happy-path YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.ui.json` (new) — IfElse sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/ifelse-basic.golden.json` (new) — IfElse hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.yaml` (new) — Switch happy-path YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.ui.json` (new) — Switch sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-three-case.golden.json` (new) — Switch hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.yaml` (new) — ForEach happy-path YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.ui.json` (new) — ForEach sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/foreach-basic.golden.json` (new) — ForEach hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-basic.yaml` (new) — While happy-path YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-basic.ui.json` (new) — While sidecar fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/while-basic.golden.json` (new) — While hydrated golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-invalid-empty-case.yaml` (new) — Intentionally invalid control-flow YAML fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-invalid-empty-case.ui.json` (new) — Sidecar paired with the invalid control-flow fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/control/switch-invalid-empty-case.golden.json` (new) — Expected hydrated DTO or diagnostic snapshot for the invalid fixture.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/ControlFlowFixtureTheoryTests.cs` (new) — Data-driven happy-path and invalid-fixture assertions.

### 7. Acceptance criteria
- [ ] All happy-path control-flow fixtures pass round-trip and validation.
  - Evidence: Theory tests run the four valid fixture triplets through mapper and engine validation.
- [ ] The invalid control-flow fixture is rejected for the expected reason.
  - Evidence: Validation tests assert a specific error message or code tied to the invalid field.
- [ ] Every control-flow fixture has a complete triplet.
  - Evidence: Manifest tests enumerate `.yaml`, `.ui.json`, and `.golden.json` for each fixture id.
- [ ] Happy-path YAML remains canonical.
  - Evidence: Round-trip theories compare re-serialized YAML to the authored source.
- [ ] Hydrated goldens match mapper output for the happy-path fixtures.
  - Evidence: Golden comparisons run after round-trip or hydration.
- [ ] The invalid fixture is not silently auto-corrected by the mapper or serializer.
  - Evidence: Negative-path tests confirm the invalid shape stays invalid and surfaces diagnostics.

### 8. Unit tests
1. **Control_flow_manifest_matches_triplets**
   - Purpose: Checks corpus completeness.
   - Harness: xUnit
   - Arrange: Enumerate control-flow fixture files.
   - Act: Group by fixture id.
   - Assert: Every fixture id has the expected triplet files.
2. **Control_flow_happy_path_roundtrip_theory**
   - Purpose: Covers the four valid fixtures.
   - Harness: xUnit theory
   - Assertion focus: Each valid control-flow triplet round-trips canonically.
3. **Control_flow_happy_path_validation_theory**
   - Purpose: Ensures engine acceptance for valid control-flow fixtures.
   - Harness: xUnit theory
   - Assertion focus: All valid control-flow fixtures pass engine validation.
4. **Invalid_control_flow_fixture_is_rejected**
   - Purpose: Covers the explicit negative corpus item.
   - Harness: xUnit
   - Assertion focus: The invalid fixture fails validation with the expected error.
5. **Invalid_control_flow_fixture_does_not_roundtrip_cleanly**
   - Purpose: Ensures the harness does not accidentally bless corrupt input.
   - Harness: xUnit
   - Assertion focus: The invalid fixture cannot be promoted to a canonical happy-path golden.
6. **Control_flow_sidecars_are_ui_only**
   - Purpose: Protects sidecar cleanliness for this family.
   - Harness: xUnit
   - Assertion focus: Sidecar files do not contain engine-owned fields.
7. **Control_flow_goldens_match_hydrated_output**
   - Purpose: Covers DTO-side fidelity.
   - Harness: xUnit theory
   - Assertion focus: Happy-path goldens match the hydrated mapper output.
8. **Invalid_fixture_error_message_is_specific**
   - Purpose: Avoids vague negative assertions.
   - Harness: xUnit
   - Assertion focus: The validation error mentions the actual invalid field or rule.

### 9. Integration tests
1. **Host_roundtrip_valid_control_flow_fixtures**
   - Purpose: Exercise happy-path control-flow triplets through the server.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Iterate over the four valid control-flow fixture ids.
   - Act: Round-trip each through the host.
   - Assert: Canonical YAML and hydrated DTOs match the goldens.
2. **Host_validate_invalid_control_flow_fixture**
   - Purpose: Exercise the negative fixture through the server validation endpoint.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit the invalid fixture to `/validate`.
   - Act: Read the validation response.
   - Assert: The host returns the expected validation failure details.
3. **Host_sidecar_hydration_for_control_flow**
   - Purpose: Verify control-flow `.ui.json` files merge cleanly.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit valid control-flow YAML + sidecar pairs.
   - Act: Read hydrated responses.
   - Assert: UI metadata reappears only in the hydrated output and not in canonical YAML.

### 10. Golden-file / parity tests
- **Triplet contract** — Every control-flow fixture, including the invalid case, has `.yaml`, `.ui.json`, and `.golden.json` companions.
- **Negative-fixture diagnostic snapshot** — The invalid fixture’s `.golden.json` documents expected validation diagnostics or rejected hydration output.
- **Control-flow corpus diff** — Canonical YAML and hydrated DTO goldens make control-flow drift easy to review.

### 11. Edge cases & negative tests
- The invalid fixture accidentally becomes valid due to mapper normalization; negative tests catch the mistake.
- A switch case ordering change alters canonical YAML without changing semantics; golden diff still surfaces the churn for review.
- A control-flow sidecar file picks up engine-owned data; sidecar cleanliness tests fail immediately.
- A fixture id collides with a happy-path case; manifest tests catch the duplicate name.
- Error text changes due to engine updates; the negative-fixture snapshot requires deliberate review before update.

### 12. Observability hooks
- **Fixture manifest log** — Emit fixture id, control-flow kind, and expected validity for each theory run.
- **Host trace** — Track `fixtureId`, `family=control`, `isInvalidFixture`, and `validationErrorCount`.
- **Golden metadata** — Annotate invalid-fixture failures with the expected diagnostic token or message.
- **Corpus metric** — Record valid-vs-invalid fixture counts for this family.

### 13. Risks & mitigations
- **Risk:** Negative fixtures become too vague to be useful.
  - Mitigation: Assert specific validation messages or codes, not just failure.
- **Risk:** Control-flow happy-path coverage misses defaults.
  - Mitigation: Keep explicit While and ForEach fixtures with their default-sensitive fields.
- **Risk:** Corrupt inputs are silently corrected by the mapper.
  - Mitigation: Negative tests verify the mapper does not normalize invalid shapes into passing fixtures.

### 14. Evaluator gap-analysis prompts
1. Which control-flow fixture is intentionally invalid, and what rule is it meant to violate?
2. How do manifest tests prove every control-flow fixture has a full triplet?
3. What evidence shows the invalid fixture is rejected for a specific reason rather than generically?
4. Which host integration suite runs the valid control-flow fixtures and the invalid fixture separately?
5. How are sidecar files checked for UI-only cleanliness in this family?
6. What reviewer signal would indicate the engine changed its validation message for the invalid fixture?

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
- Happy-path control-flow fixtures pass, and the negative fixture fails for the expected reason.
- All control-flow fixtures are represented as complete triplets.

### Step 3 — Composition fixture corpus

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Author the composition-family fixture triplets covering Subflow (file), Subflow (inline), Timer, and Container in `Parallel` mode with nested child nodes. The goal is to capture the shapes that are easiest to regress: TimeSpan formatting, nested workflows, schedules, and inline child graphs. Each fixture should be small enough to review quickly but rich enough to prove the mapper and serializer are handling composition concerns correctly. The corpus also provides the last prerequisite set for the stage 5.3 golden harness. Because composition fixtures can become noisy, the golden snapshots should stay focused and stable.

### 4. Depends on
- Step 5.1.1e

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` defines the four composition fixtures and their key behaviors.
- **Composition engine contracts** — Use `src/ExecutionEngine/Nodes/Definitions/SubflowNodeDefinition.cs:14-60`, `src/ExecutionEngine/Nodes/Definitions/TimerNodeDefinition.cs:12-24`, `src/ExecutionEngine/Nodes/Definitions/ContainerNodeDefinition.cs:14-32`, and `src/ExecutionEngine/Enums/ExecutionMode.cs:9-13` as the authoritative field sources.
- **Workflow graph contract** — `src/ExecutionEngine/Workflow/WorkflowDefinition.cs:15-76` defines nested workflow and child-graph shapes.

### 6. Deliverables
- **src/WorkflowUi.ContractTests/Fixtures/mapper/composition/**
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.yaml` (new) — Subflow file-path fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.ui.json` (new) — Subflow file-path sidecar.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-file.golden.json` (new) — Subflow file-path golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.yaml` (new) — Subflow inline-workflow fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.ui.json` (new) — Subflow inline sidecar.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/subflow-inline.golden.json` (new) — Subflow inline golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.yaml` (new) — Timer schedule fixture.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.ui.json` (new) — Timer sidecar.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/timer-cron.golden.json` (new) — Timer golden.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.yaml` (new) — Container parallel fixture with two child nodes.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.ui.json` (new) — Container sidecar.
  - `src/WorkflowUi.ContractTests/Fixtures/mapper/composition/container-parallel.golden.json` (new) — Container golden.
- **src/host/__tests__/Mapping/**
  - `src/host/__tests__/Mapping/CompositionFixtureTheoryTests.cs` (new) — Data-driven round-trip and validation theories for composition fixtures.

### 7. Acceptance criteria
- [ ] All four composition fixtures pass round-trip and validation.
  - Evidence: Theory tests run each triplet through mapper and engine validation.
- [ ] Each composition fixture has a complete triplet.
  - Evidence: Manifest tests enumerate `.yaml`, `.ui.json`, and `.golden.json` for every fixture id.
- [ ] Subflow file and inline forms both remain canonical.
  - Evidence: Round-trip theories compare authored YAML to re-serialized output exactly.
- [ ] Timer schedule strings remain unchanged.
  - Evidence: Golden comparisons check schedule text directly.
- [ ] Container child graphs and `Parallel` mode survive round-trip.
  - Evidence: Golden comparisons inspect nested nodes, connections, and execution mode.
- [ ] TimeSpan formatting in composition fixtures remains stable.
  - Evidence: Subflow timeout goldens lock the serialized representation.

### 8. Unit tests
1. **Composition_manifest_matches_triplets**
   - Purpose: Checks corpus completeness.
   - Harness: xUnit
   - Arrange: Enumerate composition fixture files.
   - Act: Group by fixture id.
   - Assert: Every fixture id has all three file types.
2. **Composition_roundtrip_theory**
   - Purpose: Covers all four composition fixtures.
   - Harness: xUnit theory
   - Assertion focus: Each composition fixture round-trips canonically.
3. **Composition_validation_theory**
   - Purpose: Ensures engine acceptance for the corpus.
   - Harness: xUnit theory
   - Assertion focus: Each composition fixture validates successfully.
4. **Subflow_timeouts_are_canonical**
   - Purpose: Protects timeout formatting.
   - Harness: xUnit
   - Assertion focus: Subflow timeout fields match the golden serialized representation.
5. **Container_parallel_mode_preserved**
   - Purpose: Covers execution mode fidelity.
   - Harness: xUnit
   - Assertion focus: The container fixture rehydrates with `Parallel` mode unchanged.
6. **Composition_sidecars_are_ui_only**
   - Purpose: Protects sidecar cleanliness.
   - Harness: xUnit
   - Assertion focus: Composition `.ui.json` files contain only UI metadata.
7. **Composition_goldens_match_hydrated_output**
   - Purpose: Covers DTO-side fidelity.
   - Harness: xUnit theory
   - Assertion focus: Hydrated outputs match the `.golden.json` files exactly.
8. **Composition_yaml_is_canonical**
   - Purpose: Protects diff readability for nested graphs.
   - Harness: xUnit theory
   - Assertion focus: Re-serializing the YAML fixtures yields byte-identical output.

### 9. Integration tests
1. **Host_roundtrip_all_composition_fixtures**
   - Purpose: Exercise the full composition corpus through the server.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Iterate over the four composition fixture ids.
   - Act: Round-trip each through the host.
   - Assert: Canonical YAML and hydrated DTOs match authored goldens.
2. **Host_validate_all_composition_fixtures**
   - Purpose: Exercise the server validation path for the composition corpus.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit each composition fixture to `/validate`.
   - Act: Read validation responses.
   - Assert: All fixtures validate successfully.
3. **Host_sidecar_hydration_for_nested_graphs**
   - Purpose: Verify nested-workflow UI metadata hydration.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Submit composition YAML + `.ui.json` pairs.
   - Act: Read hydrated responses.
   - Assert: Viewport and node positions hydrate into the DTO view without leaking into canonical YAML.

### 10. Golden-file / parity tests
- **Triplet contract** — Each composition fixture is authored as `.yaml` + `.ui.json` + `.golden.json`.
- **Nested-graph stability** — Container and inline-subflow goldens make nested structure drift visible in diffs.
- **TimeSpan regression** — Subflow timeout goldens catch serializer-format drift immediately.

### 11. Edge cases & negative tests
- A nested child graph accidentally picks up UI-only metadata in YAML; sidecar cleanliness tests fail.
- A timeout format changes because of serializer behavior; canonical diff exposes the change.
- Container child ordering changes and affects readability; goldens surface the churn for explicit review.
- A Subflow inline definition becomes too large; fixture-review guidance keeps examples intentionally small.
- A composition fixture id changes and breaks stage 5.3 harness assumptions; manifest tests catch the rename.

### 12. Observability hooks
- **Fixture manifest log** — Emit fixture id, composition variant, and nested counts during corpus runs.
- **Host trace** — Track `fixtureId`, `family=composition`, `hasInlineWorkflow`, and `childNodeCount`.
- **Golden metadata** — Annotate failures with timeout format and execution mode when relevant.
- **Corpus metric** — Record authored composition fixture counts and triplet completeness.

### 13. Risks & mitigations
- **Risk:** Nested fixtures become too noisy for reviewers.
  - Mitigation: Keep examples minimal and focused on one composition behavior each.
- **Risk:** TimeSpan formatting drift goes unnoticed.
  - Mitigation: Lock explicit subflow timeout goldens and re-serialization tests.
- **Risk:** Container nested graphs hide sidecar leakage.
  - Mitigation: Run sidecar cleanliness tests on the entire composition corpus.

### 14. Evaluator gap-analysis prompts
1. Which four composition variants are authored, and why were they chosen?
2. How do tests prove nested graphs and `Parallel` mode survive round-trip?
3. Where is TimeSpan formatting locked for subflows?
4. How is sidecar cleanliness checked for nested graph fixtures?
5. Which host integration suite runs the full composition corpus?
6. What manifest tests would fail if a composition fixture id were renamed?

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
- Composition triplets are complete, canonical, and consumed by theory tests.
- The full composition corpus passes both mapper round-trip and server validation paths.

## Appendix A — Corpus inventory summary

| Family | Happy-path fixtures | Invalid fixtures | Triplet count |
|---|---:|---:|---:|
| Task | 6 | 0 | 18 authored files |
| Control-flow | 4 | 1 | 15 authored files |
| Composition | 4 | 0 | 12 authored files |

## Appendix B — Fixture authoring rules

- Every fixture id owns exactly three files: `.yaml`, `.ui.json`, `.golden.json`.
- YAML should stay minimal, canonical, and comment-free.
- `.ui.json` must contain UI metadata only.
- `.golden.json` should reflect hydrated DTO output, not raw source YAML.
- Fixture ids should remain stable once published.
- Negative fixtures must assert a specific failure reason.
- Re-serializing authored YAML should yield byte-identical output.
- Host and unit tests must both consume the corpus.
- Sidecar cleanliness should be asserted for every family.
- Aggregate harnesses in Stage 5.3 depend on these ids staying consistent.

## Appendix C — Reviewer quick-check

1. Count triplets per family against the summary table.
2. Spot-check one `.ui.json` file from each family for UI-only data.
3. Spot-check one `.golden.json` file from each family for hydrated DTO shape.
4. Run the family theory tests and confirm all happy-path fixtures pass.
5. Run the invalid control-flow fixture and confirm it fails for the documented reason.
6. Verify fixture ids are stable and human-readable.
7. Confirm re-serialization yields canonical YAML.
8. Check host integration tests for all three families.
