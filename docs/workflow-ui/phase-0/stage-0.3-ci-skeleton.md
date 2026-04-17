# Stage 0.3 — CI skeleton

**Phase:** Phase 0 — Foundations & Gates
**Stage status:** [ ]
**Total effort:** 4 d
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Stage 0.3 wires the Stage 0.1 skeletons and Stage 0.2 parity harness into continuous integration so every subsequent PR automatically exercises the gates. It creates a dedicated GitHub Actions workflow at `.github/workflows/ci-workflow-ui.yml`, a pre-commit hook that mirrors the key gates locally, and branch-protection documentation + a CODEOWNERS entry that forces human review of changes to `docs/workflow-ui-plan.md` (the authoritative plan). Nothing functional in the application changes — but without this stage, all later gates exist only as "we could run this locally", which is not enough.

The CI must pin tool versions deterministically (`actions/setup-dotnet@v4` at the exact net8 SDK from `global.json`, `actions/setup-node@v4` at the pinned LTS), cache NuGet + npm, run reflection parity, run YAML round-trip, and publish a coverage artefact. Coverage gate is 100% on new code (per master plan [§0.1 inherited constraint 2](../../workflow-ui-plan.md#01-uniform-step-template)); generated files are explicitly excluded so the gate is meaningful.

This stage unblocks Phase 1 in the sense that Phase 1's CI-wiring stage (1.3) extends the workflow file created here. Pre-commit is optional locally but mandatory in CI.

## Prerequisites
- Stage 0.1 complete (buildable projects).
- Stage 0.2 complete (parity + YAML tests green locally).
- GitHub repository admin rights to configure branch protection (documented, not automated).

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 0.3 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

GitHub Actions
- `.github/workflows/ci-workflow-ui.yml` (new, Step 0.3.1) — lint, build, test, coverage, parity.
- `.github/workflows/ci-workflow-ui-meta.test.yml` (new, Step 0.3.1) — NOT a workflow file; named as `*.test.yml` under `src/WorkflowUi.ContractTests/Fixtures/CI/` actually. See Deliverables.

Local hooks
- `.lefthook.yml` (new, Step 0.3.2) — lefthook config (Node-launched for Windows compat).
- `client/package.json` (modified) — add `lefthook` devDependency + `prepare` script.

Branch policy & owners
- `CODEOWNERS` (new or modified, Step 0.3.3) — add `docs/workflow-ui-plan.md` and `docs/workflow-ui/` ownership entries.
- `docs/workflow-ui/branch-protection.md` (new, Step 0.3.3) — runbook: which checks must pass, how to configure via GitHub UI / API.

Meta-test
- `src/WorkflowUi.ContractTests/CiWorkflowYamlTests.cs` (new, Step 0.3.1) — parses `.github/workflows/ci-workflow-ui.yml` and asserts required jobs + steps are present (defends against accidental deletion).
- `src/WorkflowUi.ContractTests/CodeownersTests.cs` (new, Step 0.3.3) — asserts the plan path is covered.

## Shared fixtures / fakes
- The CI yaml itself is a golden file: Step 0.3.1 ships the workflow; Step 1.3 extends it. No external fixtures authored here.
- Lefthook config cross-references scripts from `client/package.json` — the `format:check` / `lint` / `test` scripts must stay named stably.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="0.3"`, `phaseId="0"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-0.3.plan-read`, `workflow-ui.stage-0.3.apply`, `workflow-ui.stage-0.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_0_3_started_total`, counter `workflow_ui_stage_0_3_failed_total`, histogram `workflow_ui_stage_0_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 0.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage03` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

## Step 0.3.1 — CI workflow with build / test / coverage / parity

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 0.2.1, 0.2.2, 0.2.3

### Goal
Create `.github/workflows/ci-workflow-ui.yml` that runs on `push` to `main` and on `pull_request` targeting `main`, with jobs that (1) restore + build the full solution, (2) run `dotnet test` with coverlet producing lcov, (3) run `npm ci` + `npm run lint` + `npm run test:coverage` + `npm run build` in `client/`, (4) run the parity test category filters explicitly (`Category=EnumParity`, `Category=DtoParity`, `Category=YamlRoundTrip`) as separate named steps so the evaluator can see which gate failed, and (5) upload both coverage reports as artefacts. A meta-test parses the YAML and asserts each required step is present so accidental deletion fails the test suite.

### Depends on
0.2.1, 0.2.2, 0.2.3.

### Inputs
- `global.json` (for pinned .NET SDK version).
- `client/package.json` (for script names).
- Test category traits from Stage 0.2.

### Deliverables
- `.github/workflows/ci-workflow-ui.yml` (new) — jobs:
  - `build-dotnet` — `actions/checkout@v4`, `actions/setup-dotnet@v4` (reads `global.json`), `dotnet restore Workflow.sln`, `dotnet build Workflow.sln -c Release -warnaserror --no-restore`.
  - `test-dotnet` — `dotnet test src/WorkflowUi.ContractTests -c Release --no-build --collect:"XPlat Code Coverage" --results-directory ./TestResults --logger "trx"`.
    - Subsequent step: `dotnet test --filter Category=EnumParity`, `--filter Category=DtoParity`, `--filter Category=YamlRoundTrip` — each as a named step with explicit `name:`.
  - `test-client` — `actions/setup-node@v4` with `cache: npm` and `cache-dependency-path: client/package-lock.json`, `npm ci`, `npm run lint`, `npm run format:check`, `npm run test:coverage`, `npm run build`.
  - `coverage-gate` — merges lcov reports (C# + TS) via `reportgenerator` or `codecov-action`; asserts line coverage ≥ 100% on new projects (`src/WorkflowUi.Server`, `src/WorkflowUi.ContractTests`, `client/src`). Generated files excluded via `--exclude-by-file`.
- `src/WorkflowUi.ContractTests/CiWorkflowYamlTests.cs` (new) — parses the workflow YAML using `YamlDotNet`, asserts each named step exists and exits with expected conditions.
- `.github/workflows/ci-workflow-ui.yml` must set `permissions: contents: read, pull-requests: write` (for coverage comment in 1.3.1).
- `codecov.yml` (new, optional if using codecov action) — coverage thresholds match 100% policy.

### Acceptance criteria
- [ ] CI is green on an empty-diff PR (smoke-opens a PR that touches only `readme.md` whitespace).
- [ ] Deliberate break: removing `Category=EnumParity` trait on any test causes CI red with a clear job name in the summary.
- [ ] Deliberate break: deleting one line from `engine-dto-shape.json` fails the `Category=DtoParity` step, NOT any other step.
- [ ] Coverage artefact `coverage-lcov.zip` is uploaded on every run (even red) for debuggability.
- [ ] The `.yml` pins all action versions to a major tag (`@v4`) but NOT to `@main` or `@master`.
- [ ] `build-dotnet` uses `-warnaserror` so a new warning on any new project surfaces as red.
- [ ] The coverage-gate job exits non-zero if line coverage on new code falls below 100% (enforces master plan inherited constraint).
- [ ] `CiWorkflowYamlTests_Parses_And_ContainsAllRequiredSteps` asserts step count ≥ 9 and each required name is present.

### Unit tests
1. `CiWorkflowYamlTests_Yaml_IsValid_UTF8_LF`
2. `CiWorkflowYamlTests_Jobs_Count_AtLeast4` — build-dotnet, test-dotnet, test-client, coverage-gate.
3. `CiWorkflowYamlTests_TestDotnet_HasExplicitEnumParityStep`
4. `CiWorkflowYamlTests_TestDotnet_HasExplicitDtoParityStep`
5. `CiWorkflowYamlTests_TestDotnet_HasExplicitYamlRoundTripStep`
6. `CiWorkflowYamlTests_SetupDotnet_ReadsGlobalJson` — asserts `global-json-file: global.json` is used.
7. `CiWorkflowYamlTests_SetupNode_CachesNpm_WithLockfilePath`
8. `CiWorkflowYamlTests_Build_UsesWarnAsError`
9. `CiWorkflowYamlTests_CoverageGate_ExcludesGeneratedFiles` — asserts `engine-contract.generated.ts` is in the exclude list.
10. `CiWorkflowYamlTests_ActionPins_NoMasterOrMainRefs` — regex-scans for `@master` / `@main`, asserts 0 occurrences.

A/A/A sketch for the three most important:

- `CiWorkflowYamlTests_TestDotnet_HasExplicitEnumParityStep`
  - Arrange: load workflow yaml into an object via `YamlDotNet`.
  - Act: traverse `.jobs['test-dotnet'].steps[]`, find a step whose `run` contains `--filter Category=EnumParity`.
  - Assert: step found; step has a non-empty `name:` attribute.
- `CiWorkflowYamlTests_Build_UsesWarnAsError`
  - Arrange: same load.
  - Act: find `build-dotnet` build step.
  - Assert: `run` contains `-warnaserror`.
- `CiWorkflowYamlTests_ActionPins_NoMasterOrMainRefs`
  - Arrange: read raw yaml text.
  - Act: regex `@(main|master)\b`.
  - Assert: `Assert.Empty(matches);`.

### Integration tests
- Opening an empty-diff PR on a feature branch must produce a green CI run. This is asserted manually on the first PR post-merge; the meta-test defends against future regressions.
- Intentional-drift PR (Stage 1.2.2 reuses this): delete one member from `engine-enums.json` and confirm CI red on `EnumParity` step.

### Golden-file / parity tests
- The workflow yaml is itself a "golden" under meta-test coverage. Any unreviewed deletion of the required steps is caught.
- Coverage thresholds in `codecov.yml` (or in the workflow yaml) constitute a golden — asserted in `CiWorkflowYamlTests_CoverageGate_ExcludesGeneratedFiles`.

### Edge cases & negative tests
1. Contributor deletes `engine-contract.generated.ts` exclusion from coverage — coverage-gate falls to ~0% on first run; test `CoverageGate_ExcludesGeneratedFiles` fails red.
2. Someone bumps `actions/setup-dotnet@v3` but leaves the rest at `@v4` — not blocked by our tests, but a policy comment in the yaml flags it.
3. New project added but not included in `coverage-gate` paths — a supplementary test `CoverageGate_IncludesAllNewProjects` is listed as a TODO and tracked as a known gap.
4. CI runs on Windows-only runner (because engine uses Windows-specific APIs per root `CLAUDE.md`): workflow yaml sets `runs-on: windows-latest`. Negative: an accidental `ubuntu-latest` is flagged by `CiWorkflowYamlTests_RunsOn_WindowsLatest`.
5. PR from a fork cannot write coverage comment (token scope) — graceful degrade documented in yaml as a comment.
6. `dotnet test` timeout on slow runners — set `--blame-hang-timeout 5m`.

### Observability hooks
- CI job outputs: test summary written to `$GITHUB_STEP_SUMMARY` via a post-step so evaluators can read the outcomes per category.
- Log category `WorkflowUi.ContractTests.CiMeta` — used by `CiWorkflowYamlTests` to log which step it validated.

### Risks & mitigations
- Coverlet 100% threshold flaky on Windows due to path separators — exclude by file glob that matches both.
- GitHub Actions rate limits on PRs — use `concurrency: cancel-in-progress: true`.
- YAML parser pulls in an engine-alternate version of YamlDotNet — use the one from `Directory.Packages.props`.

### Evaluator gap-analysis prompts
1. Does the workflow pin `actions/*` to `@v4` and NOT `@main` / `@master`?
2. Are the three parity categories (`EnumParity`, `DtoParity`, `YamlRoundTrip`) run as SEPARATE named steps so failures are attributable?
3. Does the coverage gate exclude `engine-contract.generated.ts` and any other generated file from the 100% threshold?
4. Does `build-dotnet` pass `-warnaserror`?
5. Is the meta-test (`CiWorkflowYamlTests`) present, and does it assert step presence (not just workflow validity)?
6. Does the workflow run on `windows-latest` because engine is Windows-only per root `CLAUDE.md`?
7. Is `concurrency: cancel-in-progress: true` set to avoid wasted minutes on stale commits?

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

## Step 0.3.2 — Pre-commit hook

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 0.1.3

### Goal
Wire a lightweight pre-commit hook via `lefthook` (Node-launched, Windows-compatible) that runs ESLint + Prettier on staged client files and `dotnet format --verify-no-changes` scoped to staged `.cs` files. The hook must block commits on failure and be installable via `npm --prefix client run prepare`. It must NOT run the parity or YAML tests (too slow for pre-commit) — those are CI-only.

### Depends on
0.1.3.

### Inputs
- `client/package.json` scripts from 0.1.3.
- `.editorconfig` from 0.1.2.

### Deliverables
- `.lefthook.yml` (new) — pre-commit hooks:
  ```yaml
  pre-commit:
    parallel: true
    commands:
      client-eslint:
        glob: 'client/src/**/*.{ts,tsx}'
        run: npx --prefix client eslint {staged_files}
      client-prettier:
        glob: 'client/**/*.{ts,tsx,json,md,css}'
        run: npx --prefix client prettier --check {staged_files}
      dotnet-format:
        glob: 'src/**/*.cs'
        run: dotnet format --include {staged_files} --verify-no-changes
  ```
- `client/package.json` (modified) — add `lefthook` devDependency, `prepare` script (`lefthook install`).
- `docs/workflow-ui/phase-0/hooks.md` (new, short) — local install instructions; noted that CI mirrors but does not depend on hooks.

### Acceptance criteria
- [ ] `npm --prefix client run prepare` installs the hook in `.git/hooks/pre-commit`.
- [ ] Committing a deliberately unformatted `.ts` file is blocked by the hook.
- [ ] Committing a deliberately untabbed `.cs` file is blocked by `dotnet format`.
- [ ] The hook completes in < 10 seconds on a no-op commit (performance sanity).
- [ ] Running the hook on a commit with no staged files exits 0 (no spurious failures).
- [ ] Windows (`pwsh.exe`) and Unix-like shells (bash) both execute the hook without platform branching.

### Unit tests
1. `LefthookConfig_Yaml_Parses` — parse `.lefthook.yml`, assert non-empty `pre-commit.commands`.
2. `LefthookConfig_HasEslintCommand`
3. `LefthookConfig_HasPrettierCommand`
4. `LefthookConfig_HasDotnetFormatCommand`
5. `LefthookConfig_AllCommands_UseStagedFilesToken` — asserts `{staged_files}` present (not `{files}`).
6. `PackageJson_DevDependencies_IncludesLefthook`
7. `PackageJson_Scripts_Has_Prepare_LefthookInstall`
8. `HookScript_InstalledByPrepare` (integration) — runs `npm --prefix client run prepare` in a sandbox; asserts `.git/hooks/pre-commit` written.

A/A/A for the three most important:

- `LefthookConfig_AllCommands_UseStagedFilesToken`
  - Arrange: parse yaml, enumerate commands.
  - Act: collect `run` strings.
  - Assert: every `run` contains `{staged_files}`.
- `HookScript_InstalledByPrepare`
  - Arrange: clone repo to temp dir.
  - Act: `npm --prefix client ci; npm --prefix client run prepare;`
  - Assert: `.git/hooks/pre-commit` exists and invokes lefthook.
- `LefthookConfig_Yaml_Parses`
  - Arrange: `File.ReadAllText(".lefthook.yml")`.
  - Act: `new DeserializerBuilder().Build().Deserialize<...>`.
  - Assert: deserialisation succeeds.

### Integration tests
- One "unhappy path" test: stage a `.cs` file with trailing whitespace, attempt `git commit -m "bad"`, assert exit code ≠ 0 and stderr contains `dotnet format`.

### Golden-file / parity tests
Not applicable.

### Edge cases & negative tests
1. No staged files — hook must exit 0 (don't fail empty commits).
2. Staged file in `dist/` — exclude pattern must skip.
3. Rename-only commit (new file path, same content) — `{staged_files}` must include the new path.
4. `dotnet format` not installed — hook must print a clear install instruction (not crash).
5. Network-unavailable `npx` first run — lefthook pre-installed by `prepare`; document as a risk.
6. Contributor bypasses with `git commit --no-verify` — allowed (escape hatch); CI still enforces.

### Observability hooks
- Lefthook prints per-command timings — visible locally; not captured server-side.

### Risks & mitigations
- Windows `cmd` incompatibility with POSIX commands — lefthook auto-detects pwsh / bash; documented.
- Long-running lint on mono-repo — `glob:` filter restricts scope to touched files.
- Developer confusion about `--no-verify` escape hatch — documented in `hooks.md`.

### Evaluator gap-analysis prompts
1. Is `lefthook` a devDependency of the CLIENT package (not the repo root) so `npm ci` in `client/` pulls it?
2. Does the `prepare` script run `lefthook install` so a fresh clone gets hooks automatically?
3. Are all three hook commands (eslint, prettier, dotnet format) wired with `{staged_files}` — NOT running against the whole tree?
4. Is the hook documented as CI-mirrored but not CI-required (so `--no-verify` in an emergency doesn't break merges)?
5. Does the config parse as valid YAML and include no trailing whitespace?

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

## Step 0.3.3 — Branch protection + CODEOWNERS

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 0.3.1

### Goal
Document the branch-protection contract and commit a CODEOWNERS entry that forces review on any change to the plan (`docs/workflow-ui-plan.md`), this spec tree (`docs/workflow-ui/`), and the CI workflow itself. The branch-protection rules are configured via the GitHub UI (out-of-band of source) but documented in `docs/workflow-ui/branch-protection.md`. A reflection-style test asserts the CODEOWNERS path coverage.

### Depends on
0.3.1.

### Inputs
- Repo maintainers' GitHub handles (placeholders in the CODEOWNERS until real owners are assigned).
- `.github/workflows/ci-workflow-ui.yml` from 0.3.1.

### Deliverables
- `CODEOWNERS` (new or modified) at repo root (or under `.github/CODEOWNERS`):
  ```
  docs/workflow-ui-plan.md     @workflow-owners
  docs/workflow-ui/            @workflow-owners
  .github/workflows/ci-workflow-ui.yml  @workflow-owners
  src/WorkflowUi.ContractTests/ @workflow-owners
  ```
- `docs/workflow-ui/branch-protection.md` (new) — runbook:
  - Required checks: `build-dotnet`, `test-dotnet (EnumParity)`, `test-dotnet (DtoParity)`, `test-dotnet (YamlRoundTrip)`, `test-client`, `coverage-gate`.
  - Require pull-request review by at least one CODEOWNER.
  - Dismiss stale reviews on push.
  - Require linear history.
- `src/WorkflowUi.ContractTests/CodeownersTests.cs` (new) — parses `CODEOWNERS`, asserts coverage of the four paths above.

### Acceptance criteria
- [ ] `CODEOWNERS` exists and is parseable (no trailing orphans).
- [ ] A PR that modifies `docs/workflow-ui-plan.md` shows a pending-CODEOWNER-review banner in GitHub UI.
- [ ] `CodeownersTests_CoversWorkflowUiPlan` passes.
- [ ] `CodeownersTests_CoversCiWorkflowYaml` passes.
- [ ] `CodeownersTests_CoversContractTestsProject` passes.
- [ ] `docs/workflow-ui/branch-protection.md` lists exactly the job names emitted by the Stage 0.3.1 workflow.

### Unit tests
1. `CodeownersTests_File_Exists`
2. `CodeownersTests_Parses_WithoutError`
3. `CodeownersTests_CoversWorkflowUiPlan` — first matching entry for `docs/workflow-ui-plan.md` is non-empty.
4. `CodeownersTests_CoversWorkflowUiDocsFolder` — entry for `docs/workflow-ui/` present.
5. `CodeownersTests_CoversCiWorkflowYaml`
6. `CodeownersTests_CoversContractTestsProject`
7. `CodeownersTests_NoDanglingHandle` — every right-hand-side token starts with `@`.
8. `BranchProtectionDoc_MentionsAllRequiredChecks` — markdown-parses `branch-protection.md`, asserts all 6 check names present.

A/A/A for the three most important:

- `CodeownersTests_CoversWorkflowUiPlan`
  - Arrange: read `CODEOWNERS`.
  - Act: for each line, match glob against `docs/workflow-ui-plan.md` via `Microsoft.Extensions.FileSystemGlobbing`.
  - Assert: at least one match, and the owners list is non-empty.
- `BranchProtectionDoc_MentionsAllRequiredChecks`
  - Arrange: read markdown.
  - Act: search for each of the 6 required check names (literal substrings).
  - Assert: all 6 present.
- `CodeownersTests_NoDanglingHandle`
  - Arrange: read CODEOWNERS lines.
  - Act: split by whitespace on each non-comment line, take tokens after the path.
  - Assert: every token starts with `@`.

### Integration tests
None automatable in this step — GitHub branch protection is configured via the web UI / API and documented in `branch-protection.md`. A manual smoke check is performed by opening a test PR against the plan file.

### Golden-file / parity tests
Not applicable.

### Edge cases & negative tests
1. Contributor removes the `docs/workflow-ui-plan.md` line from CODEOWNERS — `CodeownersTests_CoversWorkflowUiPlan` fails red.
2. A CODEOWNERS entry with a wildcard that accidentally matches everything — separate test ensures the plan path match is specific, not a fall-through.
3. Branch-protection doc omits a required check — `BranchProtectionDoc_MentionsAllRequiredChecks` fails.
4. Force-pushing to `main` is allowed — documented as forbidden in the runbook but enforcement is GitHub-side.
5. Team handle `@workflow-owners` doesn't exist yet — acceptable placeholder; noted in `branch-protection.md` with a TODO for admin.
6. CI workflow file renamed — `CodeownersTests_CoversCiWorkflowYaml` fails; reviewer must update CODEOWNERS atomically.

### Observability hooks
- None beyond standard test logging.

### Risks & mitigations
- CODEOWNERS scope drift (rule matches too loosely) — tests assert specific-path coverage, not just any match.
- GitHub's CODEOWNERS syntax differs subtly from gitignore — tests explicitly exercise the path syntax actually used.

### Evaluator gap-analysis prompts
1. Does CODEOWNERS include an explicit entry for `docs/workflow-ui-plan.md`?
2. Does CODEOWNERS include an entry for `docs/workflow-ui/` (folder-level)?
3. Does CODEOWNERS include an entry for `.github/workflows/ci-workflow-ui.yml`?
4. Does `branch-protection.md` list exactly the 6 required checks emitted by the Stage 0.3.1 workflow?
5. Is the plan file's path spelled correctly (`docs/workflow-ui-plan.md`, not `docs/workflow-ui-plan` or `docs/workflow-ui/plan.md`)?
6. Does a test fail RED if someone deletes the plan's CODEOWNERS entry?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.
