# Stage 5.3 — Golden-file harness

**Phase:** Mapper, Fixtures, Golden Files  
**Stage status:** [ ]  
**Total effort:** 2 d  
**Step count:** 2

## Steps

- [ ] **Step 1**: Verify-based canonical YAML + hydrated DTO harness
- [ ] **Step 2**: Golden update workflow documentation

---
## Context
Stage 5.3 turns the fixture corpus into a first-class review tool. The master plan calls for `Verify`-style golden testing so canonical YAML drift and hydrated DTO drift appear as reviewable diffs in CI (docs/workflow-ui-plan.md:902-1025). Because every fixture now exists as a `.yaml` + `.ui.json` + `.golden.json` triplet, the harness must compare both engine-facing and UI-facing outputs explicitly.
The harness is only valuable if it uses the real engine loader and serializer path. That means it must read fixtures through `WorkflowLoader`/`WorkflowSerializer`, run the DTO mapper, and normalize line endings or ordering consistently so diffs point to semantic drift instead of incidental formatting noise (src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149, src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212).
This stage also documents how to update goldens intentionally. Golden files are powerful but only when the team understands what changed, why it changed, and which tests need to be re-reviewed after an update.

## Prerequisites
- Phase gates (§0.2) green
- Prior stages completed
- External deps (React Flow v12, zustand, zundo, react-hook-form, zod, shadcn, ELKjs)

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `docs/workflow-ui/phase-5/stage-5.3-golden-file-harness.md`
- `src/WorkflowUi.ContractTests/Golden/GoldenFileTests.cs`
- `src/WorkflowUi.ContractTests/Golden/GoldenFixtureLoader.cs`
- `src/WorkflowUi.ContractTests/Golden/GoldenFixtureManifestTests.cs`
- `src/WorkflowUi.ContractTests/Golden/GoldenSnapshotModel.cs`
- `src/WorkflowUi.ContractTests/Golden/README.md`
- `src/WorkflowUi.ContractTests/Golden/UpdateGoldenChecklist.md`
- `src/WorkflowUi.ContractTests/Golden/__snapshots__`
- `src/WorkflowUi.ContractTests/VerifySettings.cs`

## Shared fixtures / fakes
- `src/WorkflowUi.ContractTests/Fixtures/mapper/**` — full triplet corpus from Stage 5.2.
- `src/WorkflowUi.ContractTests/Golden/` — Verify snapshots or canonical diff outputs authored by the harness.
- `src/WorkflowUi.ContractTests/VerifySettings.cs` — snapshot normalization settings.
- `docs/workflow-ui/phase-5/` — stage specs that describe update workflow and reviewer expectations.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="5.3"`, `phaseId="5"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-5.3.plan-read`, `workflow-ui.stage-5.3.apply`, `workflow-ui.stage-5.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_5_3_started_total`, counter `workflow_ui_stage_5_3_failed_total`, histogram `workflow_ui_stage_5_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 5.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage53` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

### Step 1 — Verify-based canonical YAML + hydrated DTO harness

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Build the golden-file harness that executes every fixture triplet through the real mapping and serialization pipeline, then snapshots both the canonical YAML and hydrated DTO output. The harness should normalize line endings, stable ordering, and other non-semantic noise so snapshot diffs are meaningful. It must cover the entire Stage 5.2 corpus, not a sample subset, and it must make it obvious which fixture failed and whether the drift was in YAML, DTO hydration, or both. The resulting CI artifact should be reviewer-friendly and deterministic across Windows development environments. This step gives the repo a durable parity alarm for all future workflow-UI changes.

### 4. Depends on
- Step 5.2.1c

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` requires `Verify`-style snapshots over canonical YAML and hydrated DTO JSON for the full fixture corpus.
- **Fixture corpus** — Stage 5.2 provides the complete triplet set the harness must enumerate.
- **Engine serialization path** — `src/ExecutionEngine/Workflow/WorkflowLoader.cs:27-149` and `src/ExecutionEngine/Workflow/WorkflowSerializer.cs:31-212` define the real YAML load/save behavior the harness must reuse.
- **Mapper path** — Stage 5.1 provides the host-side DTO ↔ engine mapping that the harness snapshots.

### 6. Deliverables
- **src/WorkflowUi.ContractTests/Golden/**
  - `src/WorkflowUi.ContractTests/Golden/GoldenFileTests.cs` (new) — Verify-driven test suite that enumerates every fixture triplet.
  - `src/WorkflowUi.ContractTests/Golden/GoldenFixtureLoader.cs` (new) — Loads `.yaml`, `.ui.json`, and `.golden.json` triplets into the harness.
  - `src/WorkflowUi.ContractTests/Golden/GoldenSnapshotModel.cs` (new) — Defines the dual-snapshot payload containing canonical YAML and hydrated DTO JSON.
  - `src/WorkflowUi.ContractTests/Golden/GoldenFixtureManifestTests.cs` (new) — Asserts the harness sees every authored fixture triplet.
  - `src/WorkflowUi.ContractTests/Golden/__snapshots__` (new) — Verify snapshot folder populated by the harness.
- **src/WorkflowUi.ContractTests/**
  - `src/WorkflowUi.ContractTests/VerifySettings.cs` (new) — Normalizes line endings and snapshot settings for stable diffs.

### 7. Acceptance criteria
- [ ] Every Stage 5.2 fixture participates in the golden harness.
  - Evidence: Manifest tests compare authored fixture ids to harness-enumerated fixture ids.
- [ ] Each snapshot includes both canonical YAML and hydrated DTO JSON.
  - Evidence: Golden snapshot payloads visibly contain both views.
- [ ] Line endings and other formatting noise are normalized before comparison.
  - Evidence: Verify settings and normalization tests prove snapshot stability on Windows.
- [ ] Harness failures identify fixture id and output surface clearly.
  - Evidence: Failing snapshot names or payloads show whether YAML, DTO, or both drifted.
- [ ] The harness uses real engine loader/serializer code, not a custom YAML path.
  - Evidence: Code review and tests show `WorkflowLoader` / `WorkflowSerializer` are invoked.
- [ ] Snapshot output remains deterministic across consecutive runs.
  - Evidence: Repeated local/CI runs produce identical snapshot content without nondeterministic ordering.

### 8. Unit tests
1. **Golden_harness_enumerates_every_fixture_triplet**
   - Purpose: Ensures full corpus coverage.
   - Harness: xUnit + Verify
   - Arrange: Enumerate authored fixtures and harness fixture ids.
   - Act: Compare the two sets.
   - Assert: No fixture is missing from the harness.
2. **Golden_snapshot_contains_yaml_and_dto**
   - Purpose: Locks the dual-surface snapshot contract.
   - Harness: xUnit + Verify
   - Assertion focus: Each snapshot model contains both canonical YAML and hydrated DTO JSON.
3. **VerifySettings_normalize_line_endings**
   - Purpose: Protects cross-machine stability.
   - Harness: xUnit
   - Assertion focus: CRLF vs LF input produces the same snapshot content.
4. **Golden_harness_uses_real_serializer**
   - Purpose: Prevents shadow YAML implementations.
   - Harness: xUnit
   - Assertion focus: The harness loads/saves through the engine serializer types.
5. **Golden_harness_labels_failures_by_fixture_id**
   - Purpose: Improves reviewer ergonomics.
   - Harness: xUnit
   - Assertion focus: Snapshot names include the fixture id and family.
6. **Golden_harness_is_deterministic**
   - Purpose: Guards against unstable snapshot ordering.
   - Harness: xUnit
   - Assertion focus: Two consecutive harness runs produce the same snapshot payloads.
7. **Golden_fixture_manifest_matches_stage52_corpus**
   - Purpose: Links this stage back to the authored corpus.
   - Harness: xUnit
   - Assertion focus: The manifest test covers task, control, and composition families.
8. **Golden_harness_surfaces_yaml_vs_dto_drift_separately**
   - Purpose: Makes snapshot failures actionable.
   - Harness: xUnit
   - Assertion focus: The snapshot model distinguishes the YAML and DTO sections clearly.

### 9. Integration tests
1. **Golden_harness_runs_in_contract_test_project**
   - Purpose: Exercise the full suite in the intended host/test environment.
   - Harness: xUnit + Verify
   - Arrange: Run the contract test project against the full fixture corpus.
   - Act: Observe snapshot generation and validation.
   - Assert: All fixtures snapshot successfully and any drift is reported with readable diffs.
2. **Host_roundtrip_outputs_match_verify_snapshots**
   - Purpose: Cross-check harness snapshots against the live host path.
   - Harness: xUnit + WebApplicationFactory
   - Arrange: Select representative fixtures from each family.
   - Act: Round-trip them through the host and compare to Verify snapshot payloads.
   - Assert: Host outputs align with the stored snapshots.
3. **Line_ending_normalization_holds_in_ci**
   - Purpose: Prevent Windows-specific diff noise.
   - Harness: CI + Verify smoke
   - Arrange: Run the suite in CI with normalized settings.
   - Act: Inspect generated snapshot content.
   - Assert: No spurious line-ending diffs appear.

### 10. Golden-file / parity tests
- **Verify snapshot contract** — Each fixture snapshot stores canonical YAML and hydrated DTO JSON side by side.
- **Corpus-wide coverage** — Snapshot folders or metadata show task, control, and composition families are all represented.
- **Normalization policy** — Verify settings lock LF normalization and other deterministic formatting rules.

### 11. Edge cases & negative tests
- A fixture triplet is added but not picked up by the harness; manifest tests fail immediately.
- Only the DTO view drifts while YAML stays stable; snapshot payload structure still makes the failure obvious.
- Only canonical YAML drifts while DTO hydration stays stable; the failure message still identifies the YAML section.
- A Windows-only line-ending change would normally cause noise; normalization removes it from the diff.
- Snapshot ordering changes because of file-system enumeration order; the harness sorts fixture ids deterministically.

### 12. Observability hooks
- **Golden harness log** — Emit `fixtureId`, `family`, `snapshotName`, and `result=pass|fail` for each Verify run.
- **Serializer trace** — Record whether the snapshot used YAML load/save plus mapper hydrate, so failures point to the correct layer.
- **CI artifact metadata** — Publish snapshot diff files with fixture ids in their paths for direct review.
- **Normalization metric** — Log whether any input required line-ending normalization during the run.

### 13. Risks & mitigations
- **Risk:** Snapshots become noisy and lose reviewer trust.
  - Mitigation: Normalize formatting aggressively and keep payloads narrowly focused on YAML + DTO views.
- **Risk:** The harness accidentally snapshots a custom serialization path.
  - Mitigation: Call the engine serializer directly and test for that dependency.
- **Risk:** Fixture enumeration becomes nondeterministic.
  - Mitigation: Sort fixture ids and snapshot names explicitly.

### 14. Evaluator gap-analysis prompts
1. How does the golden harness discover every fixture triplet from Stage 5.2?
2. What exactly is stored in each snapshot payload?
3. How are line endings normalized to keep diffs deterministic on Windows?
4. Which test proves the harness uses the real engine serializer rather than a custom YAML path?
5. How can an evaluator tell whether a failure came from YAML drift, DTO drift, or both?
6. What CI artifact naming scheme makes snapshot failures easy to inspect?

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
- Verify snapshots exist for the full fixture corpus and stay stable across repeated runs.
- Snapshot failures are actionable because they label fixture id and output surface clearly.

### Step 2 — Golden update workflow documentation

### 1. Status
[ ]

### 2. Effort
1 d

### 3. Goal
Document how maintainers should review, regenerate, and approve golden changes without guesswork. The guidance should explain when a golden update is expected, which commands to run, how to inspect canonical YAML vs hydrated DTO drift, and how to confirm the underlying engine contract actually changed. It should also explain when golden updates are *not* appropriate, such as when a drift indicates a bug rather than an intended contract change. The audience is a teammate updating goldens for the first time. The doc must be concise enough to use during a PR but detailed enough to prevent blind snapshot acceptance.

### 4. Depends on
- Step 5.3.1

### 5. Inputs
- **Master-plan scope** — `docs/workflow-ui-plan.md:902-1025` requires documentation explaining how to update goldens.
- **Golden harness** — Step 5.3.1 defines the actual snapshot workflow and fixture layout to document.
- **Fixture corpus** — Stage 5.2 defines the triplet naming and corpus structure maintainers must understand.

### 6. Deliverables
- **src/WorkflowUi.ContractTests/Golden/**
  - `src/WorkflowUi.ContractTests/Golden/README.md` (new) — Maintainer guide for reviewing and updating Verify snapshots and fixture triplets.
  - `src/WorkflowUi.ContractTests/Golden/UpdateGoldenChecklist.md` (new) — Short checklist for PR authors updating goldens.
  - `src/WorkflowUi.ContractTests/Golden/GoldenFileTests.cs` (modified) — Links to or references the update guidance in test failure messages or comments.
- **docs/workflow-ui/phase-5/**
  - `docs/workflow-ui/phase-5/stage-5.3-golden-file-harness.md` (modified) — Stage spec updated if needed to keep the workflow aligned with the published maintainer guidance.

### 7. Acceptance criteria
- [ ] A new maintainer can follow the guide to update a golden without oral guidance.
  - Evidence: Doc review and dry-run feedback confirm the guide is actionable.
- [ ] The guide explains when to update a golden and when to treat drift as a bug.
  - Evidence: Examples distinguish intended contract changes from accidental regressions.
- [ ] Commands, fixture locations, and review steps match the actual harness.
  - Evidence: A doc walkthrough verifies every path and command is current.
- [ ] The checklist calls out YAML-vs-DTO inspection explicitly.
  - Evidence: Reviewers are instructed to examine both surfaces before approving a golden update.
- [ ] The guide references the engine-contract/parity gates maintainers must check before updating snapshots.
  - Evidence: The documentation points back to the parity requirements and fixture corpus.
- [ ] The guide is short enough to be used inside a PR review workflow.
  - Evidence: Content stays procedural, bulleted, and free of unnecessary narrative.

### 8. Unit tests
1. **Golden_readme_paths_are_current**
   - Purpose: Checks that documented paths still exist.
   - Harness: xUnit doc test or file existence check
   - Arrange: Parse referenced paths from the README.
   - Act: Resolve them in the repo.
   - Assert: All referenced files and folders exist.
2. **Golden_checklist_mentions_yaml_and_dto_review**
   - Purpose: Protects the two-surface review rule.
   - Harness: xUnit doc-content test
   - Assertion focus: The checklist explicitly mentions canonical YAML and hydrated DTO review.
3. **Golden_readme_mentions_parity_gates**
   - Purpose: Keeps snapshot updates tied to contract checks.
   - Harness: xUnit doc-content test
   - Assertion focus: The README references parity and validation gates before approving updates.
4. **Golden_readme_contains_update_commands**
   - Purpose: Ensures the workflow is runnable.
   - Harness: xUnit doc-content test
   - Assertion focus: The README documents the commands needed to regenerate or verify snapshots.
5. **Golden_readme_distinguishes_bug_vs_intended_change**
   - Purpose: Prevents blind snapshot acceptance.
   - Harness: xUnit doc-content test
   - Assertion focus: The guide includes explicit “update vs investigate” decision criteria.
6. **Golden_checklist_is_concise**
   - Purpose: Protects PR usability.
   - Harness: xUnit doc-shape test
   - Assertion focus: The checklist stays within an agreed size or heading count budget.
7. **Golden_failure_messages_link_to_docs**
   - Purpose: Connects failing tests to actionable guidance.
   - Harness: xUnit
   - Assertion focus: Golden test failure output references the maintainer guide path.
8. **Golden_docs_reference_triplet_structure**
   - Purpose: Keeps fixture format obvious to maintainers.
   - Harness: xUnit doc-content test
   - Assertion focus: The guide explains the `.yaml` + `.ui.json` + `.golden.json` triplet model.

### 9. Integration tests
1. **Dry_run_update_workflow**
   - Purpose: Verify the documentation matches real commands.
   - Harness: Human-reviewed dry run or scripted smoke via xUnit task test
   - Arrange: Follow the documented steps on a controlled golden change.
   - Act: Regenerate and review snapshots.
   - Assert: The documented workflow succeeds without extra tribal knowledge.
2. **Failure_output_points_to_docs**
   - Purpose: Ensure maintainers can find the guide from a CI failure.
   - Harness: xUnit + Verify failure simulation
   - Arrange: Trigger an intentional snapshot mismatch.
   - Act: Inspect the failure output.
   - Assert: The output directs the maintainer to the README/checklist.
3. **Checklist_covers_pr_review_flow**
   - Purpose: Validate reviewer ergonomics.
   - Harness: Doc review sign-off
   - Arrange: Use the checklist in a mock PR review.
   - Act: Walk through the listed steps.
   - Assert: Reviewers can decide whether to accept or reject the snapshot update.

### 10. Golden-file / parity tests
- **Documentation example snapshots** — The guide may include a small example diff showing YAML vs DTO drift if that helps reviewers.
- **Triplet workflow explanation** — The docs explicitly explain how fixture triplets map to Verify snapshots.
- **Parity gate reminder** — The update workflow reminds maintainers to verify parity tests before accepting snapshot drift.

### 11. Edge cases & negative tests
- A maintainer tries to update goldens when parity tests are already failing for unrelated reasons; the guide tells them to stop and investigate.
- Only the `.golden.json` snapshot changed while YAML stayed stable; the guide still requires DTO-side review.
- Only canonical YAML changed; the guide explains why DTO-side review is still necessary.
- A new fixture is added without a snapshot; the guide instructs maintainers how to generate the first snapshot deliberately.
- The failure output lacks a docs link; doc tests catch the regression.

### 12. Observability hooks
- **Doc metadata** — Record doc version or last-reviewed date so maintainers know the instructions are current.
- **Failure-link trace** — Include the README path in golden harness failure output for discoverability.
- **Checklist telemetry** — Optionally record that the checklist file exists and is referenced by CI docs or test output.
- **Review aid** — Examples in the docs call out which files to inspect when YAML and DTO drifts disagree.

### 13. Risks & mitigations
- **Risk:** Maintainers update snapshots blindly.
  - Mitigation: Document explicit review criteria and bug-vs-change decision points.
- **Risk:** Docs drift from the harness implementation.
  - Mitigation: Add doc-content tests for commands, paths, and triplet structure.
- **Risk:** The workflow is too long to use in a PR.
  - Mitigation: Keep the checklist short and procedural, with deeper explanation in the README.

### 14. Evaluator gap-analysis prompts
1. Where should a maintainer start when a golden snapshot fails in CI?
2. How does the documentation tell users whether to update a golden or investigate a bug?
3. Which commands and paths are required to regenerate snapshots?
4. How are YAML and DTO diffs reviewed separately according to the guide?
5. What test proves the harness failure output links to the docs?
6. How can an evaluator tell the checklist is concise enough for PR use?

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
- A teammate can follow the guide and checklist to perform a controlled golden update unaided.
- Golden failure output and docs stay linked so the workflow is discoverable from CI.

## Appendix A — Fixture-to-snapshot coverage map

### Task-family fixture ids
- `noop-basic`
- `csharp-compiled`
- `csharp-script-inline`
- `csharp-task-inline`
- `powershell-path`
- `powershell-task-inline`
- `powershell-task-path`

### Control-flow fixture ids
- `ifelse-basic`
- `switch-three-case`
- `foreach-basic`
- `while-basic`
- `switch-invalid-empty-case`

### Composition fixture ids
- `subflow-file`
- `subflow-inline`
- `timer-cron`
- `container-parallel`

### Snapshot payload sections required per fixture
- canonical YAML output
- hydrated DTO JSON output
- fixture id metadata
- family metadata
- pass/fail diff context

## Appendix B — Golden failure triage matrix

| Symptom | Likely layer | First file to inspect | Typical next action |
|---|---|---|---|
| YAML drift only | serializer, mapper omission/default logic | `WorkflowSerializer` parity path or mapper partial | review canonical diff, then update mapper or snapshot deliberately |
| DTO drift only | mapper hydrate/dehydrate logic | family mapper partial or sidecar merger | compare `.golden.json` vs actual DTO |
| YAML and DTO drift together | mapper + serializer contract shift | family mapper partial + engine serializer path | inspect fixture, mapper, then engine contract changes |
| Missing fixture snapshot | harness enumeration or triplet naming issue | fixture manifest / loader | fix manifest or missing file before rerunning |
| Line-ending-only diff | snapshot normalization | `VerifySettings.cs` | normalize input and rerun |
| Unknown fixture id in failure | manifest drift | Stage 5.2 corpus or loader | reconcile fixture ids and snapshot naming |
| CI failure only | environment or normalization issue | Verify settings, ordering, path assumptions | reproduce with CI-like settings |
| Host mismatch but local harness pass | host integration path divergence | host round-trip endpoint/tests | compare host output to local snapshot model |

## Appendix C — Golden update playbook

1. Reproduce the failing golden locally before touching snapshots.
2. Read the failure output and note the fixture id, family, and drift surface.
3. Inspect canonical YAML diff first.
4. Inspect hydrated DTO diff second.
5. Run parity tests from the contract layer.
6. Run family-specific mapper tests.
7. Run host round-trip tests for the failing fixture.
8. Decide whether the change is intended or a regression.
9. If unintended, fix code and rerun before updating any golden.
10. If intended, document why the contract changed.
11. Regenerate or accept updated snapshots only after code is final.
12. Re-run the full golden harness.
13. Re-run the relevant fixture family theory tests.
14. Re-run WebApplicationFactory coverage for the affected family.
15. Confirm line endings are normalized in regenerated snapshots.
16. Confirm snapshot names still include fixture id and family.
17. Review `.ui.json` vs YAML boundaries when sidecar-related drift appears.
18. Review null/absent semantics carefully for policy and connection defaults.
19. Review message-type and runtime-type parity if enums were involved.
20. Include the rationale for the golden update in the PR description.
21. Ask reviewers to inspect both YAML and DTO sections, not just the snapshot file count.
22. Verify invalid fixtures still fail for the expected reason after updates.
23. Confirm no new fixture ids were introduced accidentally.
24. Confirm CI artifacts show readable diffs after the update.

## Appendix D — Reviewer questions for golden changes

1. Which fixture ids changed, and which family do they belong to?
2. Did the drift appear in YAML, DTO output, or both?
3. Was the change caused by an intentional engine-contract update, a mapper fix, or serializer normalization?
4. Were parity tests green before the snapshots were updated?
5. Did host round-trip outputs match the new snapshots?
6. Were line endings normalized, or did the diff only remove CRLF noise?
7. Did any `.ui.json` state leak into YAML?
8. Did any default omission behavior change for policies or connections?
9. Did any invalid fixtures become valid unintentionally?
10. Did any happy-path fixtures start failing validation?
11. Did the snapshot update include a rationale in the PR description?
12. Are the changed snapshots still small and reviewable?
13. Did fixture ids or naming conventions change?
14. Do the new snapshots still include both canonical YAML and hydrated DTO JSON?
15. Was the change reproduced locally before approval?
16. Were all family theory tests rerun after the snapshot update?

## Appendix E — Snapshot naming guidance

- Prefix snapshot names with family (`task`, `control`, `composition`).
- Include the fixture id in every snapshot name.
- Include an output-surface suffix if the framework stores split artifacts.
- Avoid machine-specific path fragments.
- Keep names stable across refactors when the fixture meaning does not change.
- Sort fixture ids lexicographically before snapshot generation.
- Record invalid-fixture snapshots distinctly from happy-path snapshots.
- Reserve room for future aggregate/all-kinds fixtures without renaming existing ones.
- Keep snapshot folders parallel to fixture family folders when possible.
- Use names that a reviewer can map back to the authored triplet without opening the test code.

## Appendix F — CI reviewer handoff checklist

- Verify the failing or updated snapshot name includes both family and fixture id.
- Open the canonical YAML section of the snapshot diff first.
- Open the hydrated DTO JSON section second.
- Check whether parity tests also changed in the same PR.
- Check whether any fixture triplet files were added, removed, or renamed.
- Confirm `.ui.json` changes did not leak into YAML snapshots.
- Confirm invalid fixtures still fail where expected.
- Confirm host integration tests were rerun for the affected family.
- Confirm line-ending normalization is still active.
- Confirm snapshot names remain stable and machine-independent.
- Confirm the PR description explains why the golden changed.
- Confirm the update guide or checklist does not need revision.

## Appendix G — Snapshot regeneration command notes

- Run the contract test project first in verify-only mode if available.
- Regenerate snapshots only after code and fixture changes are final.
- Prefer family-targeted runs when iterating locally, then run the full corpus.
- Re-run parity suites before accepting new snapshots.
- Re-run WebApplicationFactory tests for the affected fixture family.
- Review generated snapshot folders for unexpected new files.
- Delete orphaned snapshots only when the corresponding fixture id was intentionally removed.
- Keep fixture ids sorted so snapshot file ordering stays predictable.
- Normalize line endings before committing if your editor changed them.
- Commit updated snapshots and fixture changes together so reviewers see the whole contract shift.

## Appendix H — Common golden anti-patterns

- Blindly re-recording snapshots without reading the diff.
- Updating `.golden.json` but ignoring canonical YAML drift.
- Accepting YAML drift caused by leaked UI-only state.
- Renaming fixture ids casually and forcing broad snapshot churn.
- Treating warning-only validation changes as harmless without reviewing them.
- Updating snapshots before parity tests are green.
- Leaving invalid-fixture expectations stale after engine validation changes.
- Mixing machine-specific paths into snapshot names or payloads.
- Regenerating only local snapshots without checking host integration behavior.
- Hiding serializer drift by loosening normalization too aggressively.

## Appendix I — Maintainer FAQ seeds

1. What if only `.golden.json` changed but YAML did not?
2. What if YAML changed but hydrated DTO output did not?
3. What if both changed after an engine update?
4. What if a fixture id disappears from the harness unexpectedly?
5. What if snapshots pass locally but fail in CI?
6. What if an invalid fixture starts passing?
7. What if line-ending normalization appears broken?
8. What if host round-trip output disagrees with the local harness?
9. What if a new runtime type is added but no snapshot appears for it?
10. What if the update guide itself appears out of date?
11. What if canonical YAML is correct but snapshot names changed?
12. What if a snapshot diff is too large to review confidently?

## Appendix J — Evidence expected in a PR that updates goldens

- The fixture ids affected.
- The family or families affected.
- Whether the drift was YAML, DTO, or both.
- The underlying code change that caused the drift.
- Confirmation that parity tests passed.
- Confirmation that family theory tests passed.
- Confirmation that host integration tests passed.
- Confirmation that invalid fixtures still behave as expected.
- Notes about any serializer or normalization changes.
- A statement on whether docs/checklists also changed.
- Reviewer guidance for the most important diff hunks.
- Any follow-up work if a broader contract change is still underway.

## Appendix K — Snapshot acceptance quick questions

- Was the drift reproduced locally before updating the snapshot?
- Did the reviewer inspect canonical YAML and hydrated DTO output separately?
- Were parity tests green at the time the snapshot was updated?
- Were host integration tests rerun for the affected family?
- Did any invalid fixtures change behavior unexpectedly?
- Did any snapshot names change without a fixture-id rename?
- Did any `.ui.json` data appear in canonical YAML output?
- Is the PR rationale specific about why the golden changed?
- Are the updated snapshots still deterministic and reviewable?
- Is any follow-up work needed if the contract change is broader than one fixture family?
- Did the maintainer guide remain accurate after the update?
- Is the fixture manifest still complete after the change?

## Appendix L — Golden reviewer escalation paths

- If parity tests fail together with golden drift, resolve parity first.
- If only snapshot names changed, inspect the harness naming rules before re-recording.
- If invalid fixtures changed behavior, review the negative-path expectations before updating snapshots.
- If host integration and local harness disagree, trust neither until both are compared to the engine serializer path.
- If line-ending-only noise appears, inspect normalization before changing any fixture or mapper code.
- If UI-only state leaks into YAML, treat it as a bug even if the snapshot could be updated.
- If a fixture id disappears, inspect the corpus manifest before regenerating anything.
- If reviewers cannot explain the diff in plain language, do not approve the golden update yet.
- If the update guide is missing a scenario encountered in practice, extend the guide in the same PR.
- If aggregate/all-kinds coverage is touched later, preserve existing fixture-id stability for current families.

## Appendix M — Snapshot sections reviewers should compare in order

1. fixture id and family metadata
2. canonical YAML body
3. hydrated DTO JSON body
4. sidecar-related hydrated metadata
5. invalid-fixture diagnostic sections when present
6. snapshot naming and file placement
7. CI artifact path and diff readability
8. parity-test state in the same build
9. host integration state in the same build
10. PR rationale for the change

## Appendix N — Golden drift classification examples

- **Serializer-only drift**
  - Canonical YAML spacing, quoting, omission, or ordering changed.
  - Hydrated DTO output stayed stable.
  - Review serializer settings and mapper omission/default logic before updating snapshots.
- **Mapper-only drift**
  - Hydrated DTO output changed while canonical YAML stayed stable.
  - Review family partials, registry dispatch, and sidecar hydration logic.
- **Fixture-only drift**
  - Authored `.yaml` or `.ui.json` changed intentionally while code did not.
  - Review whether the fixture still represents the intended contract case.
- **Host-path drift**
  - Local harness snapshots are stable, but WebApplicationFactory output changed.
  - Review host endpoint wiring, request normalization, and serializer reuse.
- **Parity-coupled drift**
  - Runtime, enum, or DTO parity tests changed in the same PR.
  - Review the engine contract change before touching any snapshots.

## Appendix O — Short “do not update goldens yet” signals

- Parity tests are red.
- Invalid fixtures changed outcome unexpectedly.
- UI-only data started appearing in canonical YAML.
- Snapshot names changed without a deliberate fixture-id change.
- Line-ending normalization appears broken.
- Host integration output and local harness output disagree.
- Reviewers cannot explain why the diff is correct.
- The maintainer guide no longer matches the actual regeneration workflow.

## Appendix P — Last-pass reminder

- Read the diff.
- Check parity.
- Check host output.
- Check invalid fixtures.
- Check .ui.json boundaries.
- Check naming stability.

