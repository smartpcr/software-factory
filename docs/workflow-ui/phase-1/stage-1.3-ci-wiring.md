# Stage 1.3 — CI wiring

**Phase:** Phase 1 — Host, Contract, CI
**Stage status:** [ ]
**Total effort:** 5 d
**Step count:** 3

## Checkbox legend
- `[ ]` not started.
- `[~]` in progress.
- `[x]` done.


## Context

Stage 1.3 finishes Phase 1 by closing three loose ends in CI: (1) unit-test coverage publishing (coverlet → lcov → codecov action, with PR comments), (2) a Playwright project scaffold + one smoke test that navigates to the Stage 1.1 health endpoint via the running host, and (3) status badges in the repo `readme.md` so reviewers can see CI/coverage/drift state at a glance. Nothing functional changes in the application itself — this stage is observability and developer experience. The CI workflow file authored in Stage 0.3.1 is extended, not forked.

The Playwright scaffold here is deliberately minimal: one `@playwright/test` project, one `.spec.ts` file that opens the built Vite SPA served via `npm run preview` + a spawned dotnet host, and one assertion that the UI rendered the "Workflow UI" heading from Stage 0.1.1. Full designer tests land in Phase 3. Browser-download flakiness is mitigated by caching `~/.cache/ms-playwright` across CI runs.

This stage unblocks Phase 3 (Playwright infrastructure exists), Phase 6 (coverage instrumentation is ready for SignalR tests), and the entire project health dashboard.

## Prerequisites
- Stage 0.3.1 CI workflow exists.
- Stage 1.1.1 `GET /api/health` endpoint is live.
- Stage 1.2 facade + generator are wired.

## Definition of Ready
- [ ] Every `Depends on` reference in Stage 1.3 resolves to an earlier real step ID in `docs/workflow-ui-plan.md` or a prior stage file.
- [ ] This stage file and `docs/workflow-ui/README.md` were re-read end-to-end before implementation starts.
- [ ] Every engine citation used by this stage was re-verified at the current HEAD under `src/ExecutionEngine/...`.
- [ ] Phase-0 parity gates are green: enum parity, DTO parity, and YAML round-trip.
- [ ] The generator re-checks the README banned-identifier guard immediately before implementation starts and again during verification.
- [ ] Any step whose deliverables would exceed 20 file entries is split or grouped before code generation starts.


## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

Coverage
- `.github/workflows/ci-workflow-ui.yml` (modified, Step 1.3.1) — add coverage step + codecov upload.
- `codecov.yml` (new or modified, Step 1.3.1) — coverage thresholds.
- `src/WorkflowUi.ContractTests/CoverageCiTests.cs` (new, Step 1.3.1) — meta-test.

Playwright
- `client/playwright.config.ts` (new, Step 1.3.2).
- `client/tests/e2e/smoke.spec.ts` (new, Step 1.3.2).
- `client/package.json` (modified, Step 1.3.2) — add `@playwright/test`, `test:e2e` script.
- `.github/workflows/ci-workflow-ui.yml` (modified, Step 1.3.2) — add `playwright` job.

Readme
- `readme.md` (modified, Step 1.3.3) — add status badges for CI, coverage, contract-drift, Playwright.

## Shared fixtures / fakes
- `client/tests/e2e/smoke.spec.ts` is the first E2E fixture; Phase 3 adds canvas tests under `client/tests/e2e/`.
- `codecov.yml` lists the coverage policies consumed by both 1.3.1 and all later coverage-touching work.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="1.3"`, `phaseId="1"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-1.3.plan-read`, `workflow-ui.stage-1.3.apply`, `workflow-ui.stage-1.3.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_1_3_started_total`, counter `workflow_ui_stage_1_3_failed_total`, histogram `workflow_ui_stage_1_3_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 1.3 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage13` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

## Step 1.3.1 — Coverage publishing

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 0.3.1

### Goal
Extend the Stage 0.3.1 CI workflow so the `test-dotnet` and `test-client` jobs emit lcov coverage reports that (a) are merged via `reportgenerator` into a single artefact `coverage-merged/`, (b) are uploaded via `codecov/codecov-action@v4` with a repo-scope token stored as `secrets.CODECOV_TOKEN`, and (c) surface a coverage comment on the PR under the "Coverage" section. The 100% threshold on new projects (enforced in Stage 0.3.1) continues to apply; this step is purely about visibility and publishing. `codecov.yml` pins coverage policy: fail PR if coverage on any new file drops by > 0% (strict; per master plan [§0.1 inherited constraint 2](../../workflow-ui-plan.md#01-uniform-step-template)).

### Depends on
0.3.1.

### Inputs
- `.github/workflows/ci-workflow-ui.yml`.
- Existing coverlet integration in `src/WorkflowUi.ContractTests.csproj` and `vitest.config.ts` from Stage 0.1.3.

### Deliverables
- `.github/workflows/ci-workflow-ui.yml` (modified) — under `test-dotnet` add `--collect:"XPlat Code Coverage" --results-directory ./TestResults-dotnet`; under `test-client` ensure vitest emits lcov to `client/coverage/lcov.info`.
- `.github/workflows/ci-workflow-ui.yml` (modified) — new job `coverage-publish` that depends on `test-dotnet` + `test-client`:
  - `actions/setup-dotnet@v4`.
  - Install `reportgenerator`: `dotnet tool install -g dotnet-reportgenerator-globaltool`.
  - Merge: `reportgenerator -reports:"TestResults-dotnet/**/coverage.cobertura.xml;client/coverage/lcov.info" -targetdir:coverage-merged -reporttypes:"Cobertura;Html;lcov"`.
  - Upload artefact `coverage-merged`.
  - `codecov/codecov-action@v4` with `files: coverage-merged/Cobertura.xml,client/coverage/lcov.info`, `fail_ci_if_error: true`.
- `codecov.yml` (new) —
  ```yaml
  coverage:
    status:
      project:
        default:
          target: 100%
          threshold: 0%
          only_pulls: true
      patch:
        default:
          target: 100%
          only_pulls: true
  comment:
    layout: "reach, diff, flags, files"
    behavior: default
  ignore:
    - "client/src/engine-contract.generated.ts"
    - "tools/GenContract/**"
    - "**/bin/**"
    - "**/obj/**"
  ```
- `src/WorkflowUi.ContractTests/CoverageCiTests.cs` (new) — meta-test: parse workflow yaml + `codecov.yml`, assert expected invariants.

### Acceptance criteria
- [ ] PR shows a "Codecov" comment with coverage summary.
- [ ] `coverage-publish` job fails if lcov files missing (not silently green).
- [ ] `codecov.yml` excludes `engine-contract.generated.ts` from coverage.
- [ ] `codecov.yml` excludes `tools/GenContract/**` (generator code has its own tests but is a build-only tool).
- [ ] 100% patch target — a PR that adds un-tested code fails the codecov status check.
- [ ] Artefact `coverage-merged/` is downloadable for 30 days (`retention-days: 30`).
- [ ] `CoverageCiTests_CodecovYaml_Parses` passes.
- [ ] `CoverageCiTests_CodecovYaml_IgnoresGeneratedContract` passes.

### Unit tests
1. `CoverageCiTests_CodecovYaml_Exists`
2. `CoverageCiTests_CodecovYaml_Parses`
3. `CoverageCiTests_CodecovYaml_ProjectTarget_Is100`
4. `CoverageCiTests_CodecovYaml_PatchTarget_Is100`
5. `CoverageCiTests_CodecovYaml_IgnoresGeneratedContract`
6. `CoverageCiTests_CodecovYaml_IgnoresGenContract`
7. `CoverageCiTests_Workflow_HasCoveragePublishJob`
8. `CoverageCiTests_Workflow_CoveragePublish_Needs_TestDotnetAndTestClient`
9. `CoverageCiTests_Workflow_UsesReportGenerator`
10. `CoverageCiTests_Workflow_CodecovActionPinned_V4`

A/A/A for the three most important:

- `CoverageCiTests_CodecovYaml_IgnoresGeneratedContract`
  - Arrange: parse `codecov.yml`.
  - Act: read `ignore` list.
  - Assert: includes `"client/src/engine-contract.generated.ts"`.
- `CoverageCiTests_Workflow_CoveragePublish_Needs_TestDotnetAndTestClient`
  - Arrange: parse workflow yaml.
  - Act: read `jobs['coverage-publish'].needs`.
  - Assert: array contains `"test-dotnet"` and `"test-client"`.
- `CoverageCiTests_CodecovYaml_PatchTarget_Is100`
  - Arrange: parse.
  - Act: `cfg.coverage.status.patch.default.target`.
  - Assert: `"100%"`.

### Integration tests
- End-to-end validation: open a PR that adds a trivial uncovered function; assert the codecov check fails. (Manual; documented.)
- Open a PR that bumps `engine-contract.generated.ts`; assert codecov does NOT flag it (because it's ignored).

### Golden-file / parity tests
- `codecov.yml` itself is a policy golden; diff contract: human review required for threshold changes.

### Edge cases & negative tests
1. Missing `CODECOV_TOKEN` secret — `codecov-action` with `fail_ci_if_error: true` reports clearly and red.
2. Test run produced no coverage (0-byte file) — `reportgenerator` fails; job red.
3. Multiple coverage reports collide on output — `reportgenerator` merges; no false inflation.
4. Contributor moves a file between `src/WorkflowUi.Server` and `client/src` without updating ignore list — codecov counts both; asserted by a secondary `CoverageCiTests_CodecovYaml_NewProjectsIncludedByDefault` test.
5. Codecov service outage — `fail_ci_if_error: true` means CI red; acceptable tradeoff.
6. Non-deterministic lcov output due to temp paths — `reportgenerator` canonicalises.

### Observability hooks
- Artefact retention: `retention-days: 30` ensures post-merge debugging is possible.
- Coverage summary piped to `$GITHUB_STEP_SUMMARY` with top-5 least-covered files.

### Risks & mitigations
- Coverage threshold too aggressive (brittle) — acceptable for early-phase work; escape hatch via explicit `# codecov: exclude` comments where justified.
- Codecov token scope — repo-scope secret, not org-wide.

### Evaluator gap-analysis prompts
1. Does `codecov.yml` ignore `client/src/engine-contract.generated.ts` and `tools/GenContract/**`?
2. Is the codecov action pinned to `@v4`?
3. Does `coverage-publish` job `need` both `test-dotnet` and `test-client`?
4. Are patch and project targets both 100%?
5. Is the coverage artefact uploaded with 30-day retention for post-merge debugging?

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

## Step 1.3.2 — Playwright scaffold + smoke test

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 1.1.1

### Goal
Add a `@playwright/test` project under `client/tests/e2e/` and wire CI to run it headless in Chromium. The single smoke test (a) spawns the Stage 1.1 dotnet host via `webServer` in `playwright.config.ts`, (b) also spawns `npm run preview` for the Vite build, (c) navigates to the SPA root, (d) asserts the "Workflow UI" heading is visible, and (e) performs a single HTTP check against `/api/health`. Browser downloads are cached across CI runs using `actions/cache` keyed on the Playwright version. The scaffold must be extensible — Phase 3 will add canvas/drag/drop tests under the same folder.

### Depends on
1.1.1.

### Inputs
- `client/package.json` from Stage 0.1/1.2.
- `src/WorkflowUi.Server/Program.cs` with the `/api/health` endpoint.

### Deliverables
- `client/playwright.config.ts` (new) —
  ```ts
  import { defineConfig, devices } from '@playwright/test';
  export default defineConfig({
    testDir: './tests/e2e',
    fullyParallel: false,
    forbidOnly: !!process.env.CI,
    retries: process.env.CI ? 2 : 0,
    reporter: [['html', { outputFolder: 'playwright-report' }], ['list']],
    use: { baseURL: 'http://localhost:4173', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
    webServer: [
      { command: 'dotnet run --project ../src/WorkflowUi.Server', port: 5226, reuseExistingServer: !process.env.CI, timeout: 60_000 },
      { command: 'npm run preview', port: 4173, reuseExistingServer: !process.env.CI, timeout: 30_000 }
    ],
    projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }]
  });
  ```
- `client/tests/e2e/smoke.spec.ts` (new) — smoke assertions.
- `client/package.json` (modified) — devDep `@playwright/test` pinned to `^1.47`, scripts `test:e2e`, `test:e2e:install`.
- `.github/workflows/ci-workflow-ui.yml` (modified) — new `playwright` job:
  - `needs: [test-dotnet, test-client]`.
  - `actions/cache@v4` on `~/.cache/ms-playwright` keyed on `hashFiles('client/package-lock.json')` + Playwright version.
  - `npx --prefix client playwright install --with-deps chromium`.
  - `npm --prefix client run test:e2e`.
  - Upload `playwright-report/` artefact on failure.

### Acceptance criteria
- [ ] `npm --prefix client run test:e2e` runs headless in CI.
- [ ] The smoke test asserts the heading "Workflow UI" renders.
- [ ] The smoke test issues a `GET /api/health` and asserts 200 with `status=ok`.
- [ ] Browser download cache hit-rate is visible in CI logs (cache hit message on 2nd run).
- [ ] Trace + screenshot artefacts are captured only on failure (not on success — keeps artefact size small).
- [ ] `retries: 2` in CI, `retries: 0` locally.
- [ ] `forbidOnly: true` in CI (no `test.only` merged to main).
- [ ] Vite preview port (4173) and dotnet host port (5226) are declared in `webServer` so Playwright waits for both.

### Unit tests
1. `PlaywrightConfig_Yaml_Parses` — (via TS dynamic import) assert config default export is a Playwright config object.
2. `PlaywrightConfig_WebServer_Has_DotnetAndViteEntries`
3. `PlaywrightConfig_Retries_IsTwo_InCi`
4. `PlaywrightConfig_ForbidOnly_TrueInCi`
5. `PlaywrightConfig_Reporter_Includes_Html`
6. `SmokeSpec_Parses_Without_Error` — run `tsc --noEmit` on `smoke.spec.ts`.
7. `SmokeSpec_Contains_HeadingAssertion`
8. `SmokeSpec_Contains_HealthEndpointAssertion`
9. `CiPlaywrightJob_Exists`
10. `CiPlaywrightJob_Caches_PlaywrightBrowsers`

A/A/A for the three most important:

- `PlaywrightConfig_WebServer_Has_DotnetAndViteEntries`
  - Arrange: dynamic import of `playwright.config.ts`.
  - Act: read `webServer` array.
  - Assert: length === 2; commands contain `dotnet run` and `npm run preview`.
- `SmokeSpec_Contains_HealthEndpointAssertion`
  - Arrange: read `smoke.spec.ts` source.
  - Act: regex-find `/api/health`.
  - Assert: match found; also `expect(response.status()).toBe(200)` literal is present.
- `CiPlaywrightJob_Caches_PlaywrightBrowsers`
  - Arrange: parse workflow yaml.
  - Act: enumerate `playwright` job steps.
  - Assert: an `actions/cache@v4` step exists with `path:` containing `ms-playwright`.

### Integration tests
- The smoke test itself IS the integration test. Harness: `npm run test:e2e` spawns both `dotnet run` and `vite preview` locally; teardown auto-kills both via Playwright's `webServer` lifecycle.
- Scenarios:
  1. Cold boot → navigate to `/` → heading visible.
  2. `GET /api/health` → 200 + `{status:"ok"}`.
  3. `GET /nonexistent` → 404 (basic negative test).

### Golden-file / parity tests
Not applicable — no DTO or YAML surface touched.

### Edge cases & negative tests
1. Port 4173 or 5226 already in use locally — `reuseExistingServer: !CI` reuses; in CI (fresh runner) always spawns.
2. dotnet host slow to start — `timeout: 60_000` in webServer.
3. Playwright browser download flakes — cache + `--with-deps` reduces flakiness.
4. Test file with `test.only` accidentally committed — `forbidOnly: true` fails CI.
5. Vite preview serves stale content from a prior build — CI job always runs `npm run build` before `test:e2e`.
6. Console errors on page load — smoke test taps `page.on('pageerror')` and fails if any JS error raised.
7. Contract-facade import failing at runtime — smoke test's heading visibility assertion proves App.tsx loaded without throw.

### Observability hooks
- Playwright HTML reporter under `client/playwright-report/` — uploaded as artefact on failure.
- `trace: retain-on-failure` + `screenshot: only-on-failure` attach debugging context.

### Risks & mitigations
- Browser download taking > 5 min — cache + `--with-deps` bundle.
- dotnet host crash on startup — Playwright webServer healthchecks via port open + `timeout`.
- Flaky navigation on cold cache — CI retries: 2.
- Windows-only runner long to warm — acceptable; matches engine constraint.

### Evaluator gap-analysis prompts
1. Does `playwright.config.ts` declare BOTH a dotnet webServer (port 5226) AND a Vite preview webServer (port 4173)?
2. Is `forbidOnly: true` set when `CI` env var is present?
3. Does the CI job cache `~/.cache/ms-playwright`?
4. Is `trace: retain-on-failure` set so successful runs don't bloat artefacts?
5. Does the smoke test include both a DOM assertion (heading) and an HTTP assertion (`/api/health`)?
6. Does the spec file fail if a `pageerror` is raised during navigation?
7. Is `@playwright/test` pinned to a specific major version in `package.json`?

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

## Step 1.3.3 — Status badges

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 1.3.1, 1.3.2

### Goal
Add four status badges to the root `readme.md` under a new "Workflow Designer UI — status" section: (1) CI workflow status, (2) coverage percentage from codecov, (3) contract-drift status from the same workflow, and (4) Playwright status. Badges must reference the default branch (`main`) via a parameter so a future branch rename doesn't break them. The section links back to `docs/workflow-ui-plan.md` and to this spec folder.

### Depends on
1.3.1, 1.3.2.

### Inputs
- Existing `readme.md`.
- `.github/workflows/ci-workflow-ui.yml`.
- codecov repo slug (`smartpcr/workflow`).

### Deliverables
- `readme.md` (modified) — append section:
  ```markdown
  ## Workflow Designer UI — status

  [![CI](https://github.com/smartpcr/workflow/actions/workflows/ci-workflow-ui.yml/badge.svg?branch=main)](https://github.com/smartpcr/workflow/actions/workflows/ci-workflow-ui.yml)
  [![Coverage](https://codecov.io/gh/smartpcr/workflow/branch/main/graph/badge.svg)](https://codecov.io/gh/smartpcr/workflow)
  [![Contract drift](https://github.com/smartpcr/workflow/actions/workflows/ci-workflow-ui.yml/badge.svg?branch=main&event=push&job=contract-drift)](https://github.com/smartpcr/workflow/actions/workflows/ci-workflow-ui.yml)
  [![Playwright](https://github.com/smartpcr/workflow/actions/workflows/ci-workflow-ui.yml/badge.svg?branch=main&event=push&job=playwright)](https://github.com/smartpcr/workflow/actions/workflows/ci-workflow-ui.yml)

  See [`docs/workflow-ui-plan.md`](docs/workflow-ui-plan.md) and [`docs/workflow-ui/`](docs/workflow-ui/).
  ```
- `src/WorkflowUi.ContractTests/ReadmeBadgesTests.cs` (new) — meta-test.

### Acceptance criteria
- [ ] All 4 badges render on GitHub's rendered readme view.
- [ ] Each badge URL uses `branch=main` explicitly (parameterised for future default-branch renames).
- [ ] Clicking each badge lands on the correct status page.
- [ ] Readme tests parse the markdown and assert each badge exists with the correct href.
- [ ] Section is placed after the existing "Workflow Designer UI" section introduced in Stage 0.1.2 (or consolidated under it if that section exists).
- [ ] No badge references `branch=master`.

### Unit tests
1. `ReadmeBadgesTests_Readme_ContainsCiBadge`
2. `ReadmeBadgesTests_Readme_ContainsCoverageBadge`
3. `ReadmeBadgesTests_Readme_ContainsContractDriftBadge`
4. `ReadmeBadgesTests_Readme_ContainsPlaywrightBadge`
5. `ReadmeBadgesTests_AllBadges_TargetMainBranch`
6. `ReadmeBadgesTests_NoBadge_ReferencesMaster`
7. `ReadmeBadgesTests_BadgesSection_LinksToPlan`
8. `ReadmeBadgesTests_BadgesSection_LinksToSpecFolder`

A/A/A for the three most important:

- `ReadmeBadgesTests_AllBadges_TargetMainBranch`
  - Arrange: read `readme.md`.
  - Act: regex-extract all image URLs with `branch=`.
  - Assert: every match ends with `branch=main` (and none with `branch=master`).
- `ReadmeBadgesTests_BadgesSection_LinksToPlan`
  - Arrange: read readme.
  - Act: find a link with text matching `workflow-ui-plan.md`.
  - Assert: href === `docs/workflow-ui-plan.md`.
- `ReadmeBadgesTests_Readme_ContainsCiBadge`
  - Arrange: read readme.
  - Act: search for `actions/workflows/ci-workflow-ui.yml/badge.svg`.
  - Assert: match count ≥ 1 (multiple badges can reuse the same base URL).

### Integration tests
None — visual confirmation is manual on first PR.

### Golden-file / parity tests
Not applicable.

### Edge cases & negative tests
1. Default branch renamed from `main` to `master` — tests red; documented remediation.
2. Codecov repo slug changes — badge 404s; manual fix.
3. Badge URL embedded with `?token=` by accident — meta-test scans for `token=` and fails if present (prevents secret leak).
4. Markdown lint disables image links — `.markdownlint.json` rule MD011 stays disabled.
5. Someone adds a duplicate badges section — tests can detect but don't currently; documented as accepted risk.
6. Workflow file renamed — `ReadmeBadgesTests_Readme_ContainsCiBadge` fails on badge URL mismatch.

### Observability hooks
Not applicable.

### Risks & mitigations
- Branch-reference drift — all badges use `branch=main` explicitly; meta-test enforces.
- codecov outages — badge shows "unknown"; not blocking.
- Hardcoded owner slug `smartpcr/workflow` — documented; future fork would rename.

### Evaluator gap-analysis prompts
1. Are 4 badges present (CI, coverage, contract-drift, Playwright)?
2. Does every badge URL reference `branch=main` explicitly?
3. Is there zero reference to `branch=master` in `readme.md`?
4. Is the badges section accompanied by links to the plan and the spec folder?
5. Does a meta-test fail red if any badge is removed?
6. Does the meta-test scan for `token=` to prevent accidental secret leak in the URL?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.
