# Stage 0.1 — Repo scaffolding

**Phase:** Phase 0 — Foundations & Gates
**Stage status:** [ ]
**Total effort:** 4 d
**Step count:** 3

## Steps

- [x] **Step 1**: Create server, client, and contract-tests skeletons
- [x] **Step 2**: Editorconfig + readme cross-links
- [x] **Step 3**: Prettier + ESLint + Vitest for the client

---
## Context

Stage 0.1 creates the three brand-new projects that the entire Workflow Designer UI is built on: an ASP.NET Core 8 server host (`src/WorkflowUi.Server`), a Vite + React 18 + TypeScript SPA (`src/WorkflowUI/`), and a reflection-based parity test project (`src/WorkflowUi.ContractTests`). Every later stage assumes these three artefacts exist, build green, and have deterministic lint/test infrastructure wired up. Nothing functional is implemented here — the goal is to make subsequent stages runnable on a clean checkout without re-bootstrapping the toolchain.

The ground truth this stage must acknowledge is the existing `Workflow.sln`, `Directory.Build.props`, and `global.json` at the repo root. All new .NET projects MUST opt into `TargetFramework=net8.0` with `RuntimeIdentifier=win-x64` consistent with the parent repo (per master plan [§1.1](../../workflow-ui-plan.md#11-architecture-at-a-glance)), must reference `src/ExecutionEngine/ExecutionEngine.csproj` through a `ProjectReference` (not a PackageReference), and must preserve the file-header + `this.` qualifier rules from the root `CLAUDE.md`.

This stage unblocks: Stage 0.2 (parity harness) which requires the ContractTests project; Stage 0.3 (CI skeleton) which requires both build targets; and all of Phase 1+ which layer functionality on top.

## Prerequisites
- None. Stage 0.1 is the entry point.
- External tooling required on the agent box: .NET 8 SDK (matching `global.json`), Node.js ≥ 18 LTS, npm ≥ 9, PowerShell 7.
- The root `Workflow.sln`, `Directory.Build.props`, `Directory.Packages.props`, `nuget.config`, `global.json`, and `.editorconfig` already exist and must not be forked.

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

Server host
- `src/WorkflowUi.Server/WorkflowUi.Server.csproj` (new)
- `src/WorkflowUi.Server/Program.cs` (new)
- `src/WorkflowUi.Server/appsettings.json` (new)
- `src/WorkflowUi.Server/appsettings.Development.json` (new)

Client SPA
- `src/WorkflowUI/package.json` (new)
- `src/WorkflowUI/package-lock.json` (new, committed)
- `src/WorkflowUI/vite.config.ts` (new)
- `src/WorkflowUI/tsconfig.json` (new)
- `src/WorkflowUI/tsconfig.node.json` (new)
- `src/WorkflowUI/index.html` (new)
- `src/WorkflowUI/src/main.tsx` (new)
- `src/WorkflowUI/src/App.tsx` (new)
- `src/WorkflowUI/.eslintrc.cjs` (new, Step 0.1.3)
- `src/WorkflowUI/.prettierrc.json` (new, Step 0.1.3)
- `src/WorkflowUI/vitest.config.ts` (new, Step 0.1.3)
- `src/WorkflowUI/src/App.test.tsx` (new, Step 0.1.3)

Contract tests
- `src/WorkflowUi.ContractTests/WorkflowUi.ContractTests.csproj` (new)
- `src/WorkflowUi.ContractTests/AssemblyLoadTests.cs` (new)

Repo root
- `Workflow.sln` (modified — add the two new .NET projects)
- `readme.md` (modified — link to `docs/workflow-ui-plan.md`, Step 0.1.2)
- `.editorconfig` (modified — extend coverage / analyzer rules onto new projects, Step 0.1.2)

## Shared fixtures / fakes

This stage authors **no fixtures**. It only declares empty directories that later stages populate:

- `src/WorkflowUi.ContractTests/Fixtures/` — seeded empty; Stage 0.2 writes `noop-minimal.yaml` here.
- `src/WorkflowUI/src/__fixtures__/` — seeded empty; Phase 2 writes DTO parity fixtures here.
- `src/WorkflowUI/src/engine-contract.generated.ts` — checked in as a stub (`export {};`) so `tsc` never fails on missing module; Stage 1.2 overwrites.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="0.1"`, `phaseId="0"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-0.1.plan-read`, `workflow-ui.stage-0.1.apply`, `workflow-ui.stage-0.1.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_0_1_started_total`, counter `workflow_ui_stage_0_1_failed_total`, histogram `workflow_ui_stage_0_1_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 0.1 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage01` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

### Step 1 — Create server, client, and contract-tests skeletons

**Status:** [ ]
**Effort:** 2 d
**Depends on:** none

### Goal
Create three empty but buildable projects and wire them into the solution. The ASP.NET Core server must boot on `http://localhost:5226` with an empty pipeline, the Vite client must produce a static bundle under `src/WorkflowUI/dist/`, and the xUnit contract-tests project must reference `ExecutionEngine` and run one smoke fact that asserts the engine assembly loads. This step is deliberately scope-limited so the generator cannot drag downstream work (DI, parity, YAML) into it. After this step, `dotnet build Workflow.sln` and `npm --prefix src/WorkflowUI run build` both return 0 on a clean checkout.

### Depends on
(none)

### Inputs
- Root `Workflow.sln`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`.
- `src/ExecutionEngine/ExecutionEngine.csproj` (referenced by the contract-tests project).

### Deliverables
- `src/WorkflowUi.Server/WorkflowUi.Server.csproj` (new) — `Microsoft.NET.Sdk.Web`, `TargetFramework=net8.0`, `Nullable=enable`, `ImplicitUsings=enable`, `<ProjectReference Include="..\ExecutionEngine\ExecutionEngine.csproj" />`.
- `src/WorkflowUi.Server/Program.cs` (new) — exports top-level statements building a minimal `WebApplication` with no endpoints yet; exposes `public partial class Program` sentinel for `WebApplicationFactory`.
- `src/WorkflowUi.Server/appsettings.json` (new) — `Logging`, `AllowedHosts`, empty `WorkflowUi` section.
- `src/WorkflowUi.Server/appsettings.Development.json` (new) — verbose logging.
- `src/WorkflowUI/package.json` (new) — name `workflow-ui-client`, scripts `dev`, `build`, `preview`, deps React 18, TypeScript 5, Vite 5.
- `src/WorkflowUI/vite.config.ts` (new) — React plugin, `base: '/'`, `build.outDir: 'dist'`.
- `src/WorkflowUI/tsconfig.json` (new) — strict, `moduleResolution: 'bundler'`, `jsx: 'react-jsx'`, `noUncheckedIndexedAccess: true`.
- `src/WorkflowUI/tsconfig.node.json` (new) — compilerOptions for `vite.config.ts`.
- `src/WorkflowUI/index.html` (new) — root div + `main.tsx` module import.
- `src/WorkflowUI/src/main.tsx` (new) — renders `<App />` into `#root`.
- `src/WorkflowUI/src/App.tsx` (new) — emits string literal `Workflow UI` in an `<h1>`; exported named.
- `src/WorkflowUI/src/engine-contract.generated.ts` (new) — `export {};` stub.
- `src/WorkflowUi.ContractTests/WorkflowUi.ContractTests.csproj` (new) — xUnit (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`), `ProjectReference` to `ExecutionEngine`.
- `src/WorkflowUi.ContractTests/AssemblyLoadTests.cs` (new) — one public class `AssemblyLoadTests`, one `[Fact] public void EngineAssembly_IsReachable_Loads()`.
- `Workflow.sln` (modified) — both new projects added under `src/`.

### Acceptance criteria
- [ ] `dotnet sln E:\work\github\crp\workflow\Workflow.sln list` includes `src\WorkflowUi.Server\WorkflowUi.Server.csproj` and `src\WorkflowUi.ContractTests\WorkflowUi.ContractTests.csproj`.
- [ ] `dotnet build Workflow.sln -warnaserror` exits 0 from a clean `git clean -xdf` checkout.
- [ ] `dotnet test src/WorkflowUi.ContractTests/WorkflowUi.ContractTests.csproj` passes the single placeholder fact and reports coverage via coverlet.
- [ ] `npm --prefix src/WorkflowUI ci` installs without `ERESOLVE` peer warnings promoted to errors.
- [ ] `npm --prefix src/WorkflowUI run build` emits `src/WorkflowUI/dist/index.html` and at least one JS chunk.
- [ ] `src/WorkflowUi.Server/Program.cs` declares `public partial class Program { }` at the bottom so `WebApplicationFactory<Program>` works in Stage 1.1.
- [ ] No project uses `PackageReference` to `ExecutionEngine` — only `ProjectReference`.
- [ ] Every new `.cs` file has the copyright header required by root `CLAUDE.md`.

### Unit tests
1. `AssemblyLoadTests_EngineAssembly_IsReachable_Loads` — `Assert.NotNull(typeof(ExecutionEngine.Workflow.WorkflowDefinition).Assembly)`.
2. `AssemblyLoadTests_EngineAssembly_Version_HasNonEmptyString` — version string is non-empty.
3. `AssemblyLoadTests_Loader_TypeExists` — `typeof(ExecutionEngine.Workflow.WorkflowLoader)` resolves.
4. `AssemblyLoadTests_Serializer_TypeExists` — `typeof(ExecutionEngine.Workflow.WorkflowSerializer)` resolves.
5. `AssemblyLoadTests_RuntimeTypeEnum_Has13Members` — sanity-check the engine surface before parity tests are added.
6. `AssemblyLoadTests_NodeDefinitionBase_IsAbstract` — `typeof(ExecutionEngine.Nodes.Definitions.NodeDefinition).IsAbstract`.
7. `App_Renders_HeadingText` (vitest) — renders `<App />` with React Testing Library, asserts `getByRole('heading', { name: /workflow ui/i })`.
8. `ViteConfig_Plugin_UsesReact` (vitest) — loads `vite.config.ts` dynamically, asserts plugins list contains the React plugin name.

Arrange / Act / Assert sketch for the three most important:

- `AssemblyLoadTests_EngineAssembly_IsReachable_Loads`
  - Arrange: no-op.
  - Act: `var asm = typeof(WorkflowDefinition).Assembly;`
  - Assert: `Assert.NotNull(asm); Assert.Contains("ExecutionEngine", asm.FullName!);`
- `AssemblyLoadTests_RuntimeTypeEnum_Has13Members`
  - Arrange: no-op.
  - Act: `var members = Enum.GetValues<ExecutionEngine.Enums.RuntimeType>();`
  - Assert: `Assert.Equal(13, members.Length);` — defends the number cited in master plan [§0.2.1](../../workflow-ui-plan.md#021-engine-parity-tests-critical-blocker-for-everything-else).
- `App_Renders_HeadingText`
  - Arrange: `render(<App />)`.
  - Act: `const heading = screen.getByRole('heading', { level: 1 });`
  - Assert: `expect(heading).toHaveTextContent('Workflow UI');`

### Integration tests
None — this step explicitly excludes HTTP and SignalR integration (deferred to 1.1.1). A `dotnet run --project src/WorkflowUi.Server` smoke check is performed manually and documented in the PR description, but not automated at this step.

### Golden-file / parity tests
Not applicable at this step; mandatory golden fixtures land in Stage 0.2. The empty `src/WorkflowUi.ContractTests/Fixtures/` folder is created but contains no goldens.

### Edge cases & negative tests
1. Building with a missing `ExecutionEngine` project reference must fail with MSBuild error `MSB3202` (validated by a deliberate-break local check, not in CI).
2. `npm --prefix src/WorkflowUI run build` must fail if `engine-contract.generated.ts` is deleted — the stub import chain surfaces a hard error.
3. `Program.cs` without the `public partial class Program` sentinel fails Stage 1.1 tests — asserted by a comment-anchored test in 1.1.1; here we only verify the sentinel exists via `grep`-style assertion in review.
4. Duplicate project GUIDs in `Workflow.sln` must fail `dotnet sln list` — covered by `dotnet sln list` CI call.
5. `npm ci` (not `npm install`) is used so unpinned versions fail fast if `package-lock.json` is stale or missing.
6. A project that accidentally targets `net7.0` or `net9.0` must trigger a `NETSDK1045`-family build failure (`global.json` enforces net8 SDK).

### Observability hooks
- Log category `WorkflowUi.Server.Startup` — emitted at `Information` once `WebApplication.Build()` returns; fields `machineName`, `environmentName`, `engineAssemblyVersion`.
- No metrics in this step.
- Vite build emits its own structured log; the CI step captures `vite build` stdout under artefact `vite-build.log`.

### Risks & mitigations
- Solution-filter regressions (projects drift out of `Workflow.sln`) — mitigated by the acceptance-criterion `dotnet sln list` check.
- Transitive version conflict between engine and server (e.g. `Microsoft.Extensions.Logging`) — pinned via `Directory.Packages.props`.
- Contributor develops on `net9.0` SDK — `global.json` pins `rollForward: latestFeature` on 8.0.x and CI enforces.
- npm peer-dep drift — lockfile committed; CI uses `npm ci` not `npm install`.

### Evaluator gap-analysis prompts
1. Does `src/WorkflowUi.Server/Program.cs` declare `public partial class Program { }` so `WebApplicationFactory<Program>` can construct it in Stage 1.1?
2. Do both new `.csproj` files use `ProjectReference` (never `PackageReference`) to `ExecutionEngine.csproj`?
3. Is `src/WorkflowUI/src/engine-contract.generated.ts` present as a stub, so `tsc` in Stage 1.2 never fails on missing module before the generator runs?
4. Does `Workflow.sln` list both new project paths, and does `dotnet sln list` verify it?
5. Is the `AssemblyLoadTests_RuntimeTypeEnum_Has13Members` fact present, defending the 13-member count cited in master plan §0.2.1?
6. Are the copyright headers from `CLAUDE.md` applied to every new `.cs` file?
7. Is `tsconfig.json` set to `strict: true` with `noUncheckedIndexedAccess: true` so later DTO code cannot regress silently?
8. Does `src/WorkflowUI/package.json` commit `package-lock.json` and use `npm ci` in its CI invocation?

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

### Step 2 — Editorconfig + readme cross-links

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 0.1.1

### Goal
Extend the existing `.editorconfig` coverage to `src/WorkflowUi.Server`, `src/WorkflowUi.ContractTests`, and `src/WorkflowUI/` so `dotnet format --verify-no-changes` and `prettier --check` both pass on a fresh checkout. Also add a bidirectional link: the repo `readme.md` gains a "Workflow Designer UI" section pointing at `docs/workflow-ui-plan.md`, and the plan's §0.1 references the readme entry. No functional code is added.

### Depends on
0.1.1.

### Inputs
- `.editorconfig` at repo root.
- `readme.md` at repo root.
- `docs/workflow-ui-plan.md`.

### Deliverables
- `.editorconfig` (modified) — add `[src/WorkflowUI/**/*.{ts,tsx,js,jsx,json,md}]` section with `indent_size = 2`, `end_of_line = lf`, `trim_trailing_whitespace = true`, `insert_final_newline = false` (matching root rule).
- `.editorconfig` (modified) — ensure C# rules from the root `CLAUDE.md` (`dotnet_style_qualification_for_*` = `true:warning`) apply to `src/WorkflowUi.Server/**/*.cs` and `src/WorkflowUi.ContractTests/**/*.cs` via glob coverage.
- `readme.md` (modified) — insert new H2 `## Workflow Designer UI` with a one-paragraph description and a link to `docs/workflow-ui-plan.md`.
- `docs/workflow-ui/README.md` (referenced, not modified) — link from the repo readme.
- Markdown-lint config pinned: either add `.markdownlint.json` at repo root with `MD013: false` and `MD033: false`, or extend an existing one if present.

### Acceptance criteria
- [ ] `dotnet format --verify-no-changes Workflow.sln` exits 0.
- [ ] `npm --prefix src/WorkflowUI run format:check` (prettier) exits 0 on a fresh checkout.
- [ ] `markdownlint docs/workflow-ui-plan.md docs/workflow-ui/README.md readme.md` exits 0.
- [ ] `readme.md` contains an anchor to `docs/workflow-ui-plan.md`.
- [ ] Every new `.cs` file in `src/WorkflowUi.Server` and `src/WorkflowUi.ContractTests` uses `this.` for instance members (per `.editorconfig`).
- [ ] `.editorconfig` has a glob block that explicitly covers the `src/WorkflowUI/` tree with 2-space indent (distinct from the C# 4-space block).

### Unit tests
1. `EditorConfig_CSharp_QualifiesThis_Warning` — loads `.editorconfig`, asserts `dotnet_style_qualification_for_field = true:warning` under a section matching `src/WorkflowUi.Server/**/*.cs`.
2. `EditorConfig_Client_IndentSize_IsTwo` — asserts a section matching `src/WorkflowUI/**/*.ts` declares `indent_size = 2`.
3. `EditorConfig_EndOfLine_CRLF_ForCSharp` — C# section sets `end_of_line = crlf`.
4. `Readme_LinksToPlan_HasAnchor` — regex-scan `readme.md` for `](docs/workflow-ui-plan.md)`.
5. `Readme_LinksToPlan_IsReachableFile` — the linked relative path resolves on disk.
6. `MarkdownLint_Plan_ZeroErrors` — shells out to `markdownlint` against the plan file, parses output, asserts zero.
7. `DotnetFormat_VerifyNoChanges_ExitsZero` — invokes `dotnet format --verify-no-changes` with a 120-second timeout.
8. `PrettierCheck_Client_ExitsZero` — invokes `npm --prefix src/WorkflowUI run format:check`.

Arrange/Act/Assert sketch for the three most important:

- `EditorConfig_CSharp_QualifiesThis_Warning`
  - Arrange: read `.editorconfig` text.
  - Act: parse the first `[*.cs]` (or most specific) block covering `src/WorkflowUi.Server`.
  - Assert: key `dotnet_style_qualification_for_field` has value containing `:warning` and `true`.
- `DotnetFormat_VerifyNoChanges_ExitsZero`
  - Arrange: `Process.Start("dotnet", "format --verify-no-changes Workflow.sln")`.
  - Act: `proc.WaitForExit();`
  - Assert: `Assert.Equal(0, proc.ExitCode);`
- `Readme_LinksToPlan_IsReachableFile`
  - Arrange: read `readme.md`.
  - Act: extract first `](docs/workflow-ui-plan.md)` capture.
  - Assert: `File.Exists(Path.Combine(repoRoot, "docs/workflow-ui-plan.md"))`.

### Integration tests
None — tooling invocations suffice.

### Golden-file / parity tests
Not applicable.

### Edge cases & negative tests
1. A contributor saves a `.cs` file in LF mode — `dotnet format` should re-write to CRLF and CI should red.
2. A `.ts` file in `src/WorkflowUI/` saved with 4-space indent — prettier flags.
3. Markdown file with a dead relative link — `Readme_LinksToPlan_IsReachableFile` test catches.
4. `.editorconfig` root flag missing — contributor edits lose precedence; we explicitly assert `root = true` at the top.
5. Trailing whitespace left in a `.cs` file — `trim_trailing_whitespace` rule + `dotnet format` catches.
6. CRLF/LF mismatch in a shell script — separate `[*.sh]` block enforces LF.

### Observability hooks
None new — relies on tooling exit codes.

### Risks & mitigations
- `.editorconfig` changes silently disable rules on existing projects — mitigated by rerunning `dotnet format --verify-no-changes` across entire solution.
- Prettier ignores TSX by default — add `"plugins": []` and explicit `--write '**/*.{ts,tsx,json,md}'` in `format` script.

### Evaluator gap-analysis prompts
1. Does `.editorconfig` include a distinct glob block for `src/WorkflowUI/**/*.{ts,tsx,js,jsx,json,md}` with 2-space indent?
2. Does `readme.md` link to `docs/workflow-ui-plan.md` with a working relative path?
3. Are C# `this.` qualifier rules present at warning level for both new server and test projects?
4. Does `dotnet format --verify-no-changes Workflow.sln` pass on a fresh clone?
5. Is `markdownlint` wired into either CI or a local script, and does it pass on the plan file?

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

### Step 3 — Prettier + ESLint + Vitest for the client

**Status:** [ ]
**Effort:** 1 d
**Depends on:** 0.1.1

### Goal
Wire three distinct frontend tools so every later UI step can rely on `npm run lint`, `npm run format:check`, and `npm run test` returning 0 on a clean checkout. Configure ESLint with `@typescript-eslint` strict rules + `plugin:react/recommended` + `plugin:react-hooks/recommended`. Configure Prettier with the house style (single quote, trailing comma `'all'`, print-width 100). Configure Vitest against the existing `App.tsx` with React Testing Library and jsdom. Add an `import/no-restricted-paths` rule skeleton so Stage 1.2 (contract facade) can extend it without re-scaffolding.

### Depends on
0.1.1.

### Inputs
- `src/WorkflowUI/package.json` from 0.1.1.
- `src/WorkflowUI/src/App.tsx`.

### Deliverables
- `src/WorkflowUI/.eslintrc.cjs` (new) — extends `eslint:recommended`, `plugin:@typescript-eslint/strict`, `plugin:react/recommended`, `plugin:react-hooks/recommended`, `plugin:import/recommended`, `prettier`. Declares an empty `no-restricted-imports` slot + `no-restricted-paths` stub wired to `engine-contract.generated.ts`.
- `src/WorkflowUI/.eslintignore` (new) — `dist/`, `node_modules/`, `src/engine-contract.generated.ts` (lint the facade, not the generator output).
- `src/WorkflowUI/.prettierrc.json` (new) — `{ "singleQuote": true, "trailingComma": "all", "printWidth": 100, "endOfLine": "lf" }`.
- `src/WorkflowUI/.prettierignore` (new) — same as eslintignore plus `*.generated.ts`.
- `src/WorkflowUI/vitest.config.ts` (new) — `test.environment = 'jsdom'`, `test.globals = true`, `test.setupFiles = ['./vitest.setup.ts']`, coverage via `@vitest/coverage-v8` with 100% threshold on new code (per master plan [§0.1 constraint 2](../../workflow-ui-plan.md#01-uniform-step-template)).
- `src/WorkflowUI/vitest.setup.ts` (new) — imports `@testing-library/jest-dom`.
- `src/WorkflowUI/src/App.test.tsx` (new) — one passing test (`App_Renders_HeadingText`).
- `src/WorkflowUI/package.json` (modified) — scripts: `lint`, `lint:fix`, `format`, `format:check`, `test`, `test:coverage`; devDeps: `eslint`, `@typescript-eslint/*`, `eslint-plugin-react`, `eslint-plugin-react-hooks`, `eslint-plugin-import`, `eslint-config-prettier`, `prettier`, `vitest`, `@vitest/coverage-v8`, `jsdom`, `@testing-library/react`, `@testing-library/jest-dom`.

### Acceptance criteria
- [ ] `npm --prefix src/WorkflowUI run lint` exits 0.
- [ ] `npm --prefix src/WorkflowUI run format:check` exits 0.
- [ ] `npm --prefix src/WorkflowUI run test` exits 0 with exactly 1 passing test and 0 failing.
- [ ] `npm --prefix src/WorkflowUI run test:coverage` produces an lcov report under `src/WorkflowUI/coverage/lcov.info` and fails if coverage falls below 100% on `src/App.tsx`.
- [ ] ESLint errors on a deliberately inserted `any` type (strict rules are real, not informational).
- [ ] ESLint configuration includes a disabled-by-default `no-restricted-imports` entry whose presence is detectable by grep — Stage 1.2.3 will turn it on.
- [ ] `eslint-config-prettier` is the LAST entry in `extends` so style rules never conflict.
- [ ] Vitest config sets `reporters: ['default']` and NOT `['silent']` so failures are visible in CI logs.

### Unit tests
1. `Eslint_Strict_ForbidsAny_OnTestFile` — runs eslint programmatically on a fixture string containing `const x: any = 1;`, asserts a `@typescript-eslint/no-explicit-any` error.
2. `Eslint_Extends_Prettier_Last` — parses `.eslintrc.cjs` AST, asserts `prettier` is the last element of `extends`.
3. `Prettier_Config_SingleQuote_True` — parses `.prettierrc.json`, asserts `singleQuote === true`.
4. `Prettier_Config_PrintWidth_100` — asserts `printWidth === 100`.
5. `Vitest_Config_Jsdom` — dynamic import of `vitest.config.ts`, asserts `test.environment === 'jsdom'`.
6. `Vitest_Coverage_Threshold_100` — asserts coverage thresholds object has `lines: 100, branches: 100, functions: 100, statements: 100`.
7. `App_Renders_HeadingText` — RTL smoke test on `<App />`.
8. `PackageJson_Scripts_AllPresent` — asserts the 6 required scripts exist and are non-empty strings.

A/A/A sketch for the three most important:

- `Eslint_Strict_ForbidsAny_OnTestFile`
  - Arrange: create in-memory source `const x: any = 1;`.
  - Act: `new ESLint({ useEslintrc: true }).lintText(src, { filePath: 'virtual.ts' })`.
  - Assert: result[0].messages contains rule `@typescript-eslint/no-explicit-any` with severity 2.
- `Vitest_Coverage_Threshold_100`
  - Arrange: import `vitest.config.ts` via `tsx` loader.
  - Act: read `config.test.coverage.thresholds`.
  - Assert: `lines === 100 && branches === 100`.
- `App_Renders_HeadingText`
  - Arrange: `render(<App />)`.
  - Act: `screen.getByRole('heading', { level: 1 })`.
  - Assert: heading text matches `/workflow ui/i`.

### Integration tests
None — frontend tooling only.

### Golden-file / parity tests
Not applicable.

### Edge cases & negative tests
1. A `.ts` file with tab indent should be re-written by prettier; CI `format:check` should red first.
2. A React component missing a key in a list should fail `react/jsx-key`.
3. An unused import should fail `@typescript-eslint/no-unused-vars`.
4. A missing return type on an exported function should NOT fail (we deliberately allow inference) — documented in `.eslintrc.cjs` comment.
5. A test file importing `engine-contract.generated.ts` directly should NOT fail yet (Stage 1.2.3 tightens the rule); current state should be a TODO comment.
6. ESLint + Prettier rule conflict (e.g., `arrow-body-style`) — detected by enabling `eslint-config-prettier`'s internal check script `eslint-config-prettier ./src/App.tsx`.

### Observability hooks
- ESLint and Vitest both support `--reporter json`; CI pipe captures JSON reports under `artifacts/` for evaluator consumption.
- Log category (server-side) not applicable.

### Risks & mitigations
- ESLint 9 flat-config migration churn — pin ESLint to `^8.57`, document upgrade plan in CI file.
- Coverage thresholds too strict too early — allowed because this stage has only one trivial component; later stages may need explicit excludes for generated files.
- React 18 strict mode + RTL double-render confusion — `vitest.setup.ts` wraps with StrictMode explicitly to catch this early.

### Evaluator gap-analysis prompts
1. Is `prettier` listed as the last entry in `extends` in `.eslintrc.cjs`?
2. Does `vitest.config.ts` enforce `100` coverage thresholds on `lines`, `branches`, `functions`, and `statements`?
3. Is `@vitest/coverage-v8` in devDependencies (not `c8` or `nyc`)?
4. Does `App.test.tsx` assert the heading text matches the `Workflow UI` literal from `App.tsx`?
5. Is there a `no-restricted-imports`/`no-restricted-paths` stub in the ESLint config that Stage 1.2.3 can activate without restructuring the config?
6. Does `.prettierignore` include `*.generated.ts` so the (later) generated contract file doesn't trigger format drift?
7. Is `@testing-library/jest-dom` imported from `vitest.setup.ts` so `toBeInTheDocument()` works?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.
