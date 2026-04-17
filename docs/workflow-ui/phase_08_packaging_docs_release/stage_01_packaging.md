# Stage 8.1 — Packaging

**Phase:** §14 Phase 8 — Packaging, Docs, Release
**Stage status:** [ ]
**Total effort:** 6 d
**Step count:** 4

## Steps

- [ ] **Step 1**: Dockerfile (multi-stage) (3 d)
- [ ] **Step 2**: Helm chart (1 d)
- [ ] **Step 3**: Self-contained single-file exe (1 d)
- [ ] **Step 4**: Version stamp (assembly + UI footer) (1 d)

---
## Context

Stage 8.1 turns the host + UI into shippable artifacts. The master plan pins two primary
distribution formats: a **single self-contained ASP.NET Core exe with static UI assets** (Step
8.1.3) and a **container image** (Step 8.1.1). A Helm chart (Step 8.1.2) is provided for
Kubernetes deployments. Step 8.1.4 stamps version metadata into the assembly and UI footer. The
master plan also references MSIX/NSIS installer tooling in the release criteria (§8) — this
stage provides hooks (version stamp, self-contained publish) that the installer in 8.1.4 can
consume; installer creation itself is scoped to Stage 8.3 if required.

Ground truth lives in the repo metadata rather than engine source:
- `Directory.Build.props` / `Directory.Packages.props` — centralised versioning.
- `global.json` — .NET SDK pin.
- `version.json` — Nerdbank.GitVersioning spec (existing in repo root), which we reuse so the
  UI footer and binary version agree.

Static UI assets are built by Vite (`src/ui`) into `dist/` and embedded into the server's
`wwwroot/` via the `dotnet publish` step. The container uses a multi-stage build: `node:20`
for UI, `mcr.microsoft.com/dotnet/sdk:8.0` for server, `mcr.microsoft.com/dotnet/aspnet:8.0`
as runtime — all pinned and scanned.

## Prerequisites
- Stage 7.3 complete (feature-complete host).
- CI pipeline from Phase 1 able to run `dotnet publish` and `npm run build`.
- External deps: Docker 25, Helm 3.14, Testcontainers for .NET 3.9, Nerdbank.GitVersioning 3.6,
  MSBuild 17.x.

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `Dockerfile` (new, repo root)
- `.dockerignore` (new)
- `deploy/helm/workflow-ui/Chart.yaml` (new)
- `deploy/helm/workflow-ui/values.yaml` (new)
- `deploy/helm/workflow-ui/templates/deployment.yaml` (new)
- `deploy/helm/workflow-ui/templates/service.yaml` (new)
- `deploy/helm/workflow-ui/templates/ingress.yaml` (new)
- `deploy/helm/workflow-ui/templates/configmap.yaml` (new)
- `deploy/helm/workflow-ui/tests/deployment_test.yaml` (new — helm unittest)
- `deploy/publish/publish-selfcontained.ps1` (new)
- `deploy/publish/publish-selfcontained.sh` (new)
- `src/host/WorkflowUi.Host/Version/VersionInfo.cs` (new)
- `src/host/WorkflowUi.Host/Version/VersionController.cs` (new)
- `src/ui/src/components/AppFooter.tsx` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Packaging/ContainerSmokeTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Packaging/SelfContainedSmokeTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Version/VersionInfoTests.cs` (new)
- `src/ui/src/components/__tests__/AppFooter.test.tsx` (new)
- `.github/workflows/container.yml` (new)
- `.github/workflows/selfcontained.yml` (new)

## Shared fixtures / fakes
- **Testcontainers `workflow-ui:test` image** built during `ContainerSmokeTests` setup.
- **`versioninfo.test.json`** — fixture version metadata used in VersionInfo unit tests.
- **`selfcontained-smoke.sh` / `.ps1`** — launch the publish output and `curl /api/health`.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="8.1"`, `phaseId="8"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-8.1.plan-read`, `workflow-ui.stage-8.1.apply`, `workflow-ui.stage-8.1.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_8_1_started_total`, counter `workflow_ui_stage_8_1_failed_total`, histogram `workflow_ui_stage_8_1_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| packaging | bundle misses runtime dependency | smoke boot or installer test fails | restore manifest/asset entry and rebuild package |
| docs site | screenshots or API docs drift | docs parity / link checks fail | recapture deterministic assets and republish |
| release automation | tag/changelog/publish order breaks | release dry-run fails | roll back tag or package and rerun validated workflow |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| docs / package smoke build | 60 s | 120 s |
| container/app cold boot | 5 s | 15 s |
| release validation bundle | 120 s | 300 s |

## Accessibility checklist
- [ ] Docs pages use semantic headings, landmarks, and accessible code-block labels.
- [ ] Screenshots/media include alt text describing workflow state changes relevant to the page.
- [ ] Release notes tables and badge colors have non-color cues.
- [ ] Installer/package docs list keyboard steps for key flows.
- [ ] Docs-site search results and navigation are reachable without hover-only interactions.

## Security callouts
- Release jobs must use least-privilege tokens and redact package registry credentials from logs.
- Docs/screenshots must not contain secrets, real tenant ids, or local filesystem paths.
- Signed artifacts and checksums should be verified before publish/promote steps continue.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 8.1 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage81` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Build the docs/package from a clean checkout and follow the published quickstart exactly once.
- Run the release dry-run, inspect generated notes/artifacts, then verify rollback steps are documented.
- Open the docs site in light/dark mode and keyboard through the main navigation + search.

---

### Step 1 — Dockerfile (multi-stage) (3 d)

### 1. Status
`[ ]`

### 2. Effort
3 engineering days.

### 3. Goal
Author a multi-stage `Dockerfile` that builds the UI with Node 20, the server with .NET 8 SDK,
and emits a minimal runtime image based on `mcr.microsoft.com/dotnet/aspnet:8.0-alpine`. The
container runs as non-root user `appuser` (UID 1000), exposes port 8080, and emits a healthcheck
hitting `/api/health`. Base images are pinned by digest. A Testcontainers smoke test builds the
image and asserts the UI + API respond inside the container.

### 4. Depends on
- Step 7.3.3

### 5. Inputs
- Built UI output from `src/ui/dist`.
- Published host output from `dotnet publish -c Release`.

### 6. Deliverables
- `Dockerfile` with stages `ui-build`, `server-build`, `runtime`.
- `.dockerignore` excluding `bin/`, `obj/`, `node_modules/`, `TestResults/`, `.git/`, docs.
- `.github/workflows/container.yml` building + pushing image on tag.
- `ContainerSmokeTests.cs` using Testcontainers to start the image, wait for health, query API.

### 7. Acceptance criteria
- [ ] `docker build .` succeeds on CI (Linux).
- [ ] Final image size ≤ 250 MB compressed.
- [ ] Container runs as non-root (`USER 1000:1000`).
- [ ] Healthcheck passes within 30 s of container start.
- [ ] `GET /api/health` returns 200 with `{ "status":"healthy" }`.
- [ ] Image has no HIGH/CRITICAL CVEs per Trivy scan at build time.
- [ ] Base image pinned by digest (not tag).
- [ ] Environment variables documented: `ASPNETCORE_URLS`, `WorkflowsRoot`, `RunsRoot`,
      `WorkflowUi__Auth__Authority`.

### 8. Unit tests
Dockerfile-level unit tests are limited; the 8 test cases below cover the adjacent C# smoke
harness:
1. `Container_Starts_Within30s`.
2. `Container_HealthEndpoint_Returns200`.
3. `Container_StaticUiIndex_Returns200`.
4. `Container_ApiRoutes_Return401_WhenUnauthenticated`.
5. `Container_NonRootUser_Verified` (via `docker exec id -u` → `1000`).
6. `Container_ExposesPort8080_Only`.
7. `Container_Environment_AcceptsConfigOverride`.
8. `DockerBuild_ImageSize_UnderLimit` (CI asserts via `docker images --format`).

Three AAA sketches (Testcontainers-for-.NET):

```csharp
// #2 Container_HealthEndpoint_Returns200
await using var container = new ContainerBuilder()
    .WithImage("workflow-ui:test")
    .WithPortBinding(8080, true)
    .WithEnvironment("WorkflowUi__Auth__AllowDevBypass", "true")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
        r.ForPort(8080).ForPath("/api/health")))
    .Build();
await container.StartAsync();
var http = new HttpClient { BaseAddress = new Uri(container.Hostname + ":" + container.GetMappedPublicPort(8080)) };
// Act
var resp = await http.GetAsync("/api/health");
// Assert
resp.StatusCode.Should().Be(HttpStatusCode.OK);
(await resp.Content.ReadAsStringAsync()).Should().Contain("healthy");

// #5 Container_NonRootUser_Verified
var (stdout, _) = await container.ExecAsync(new[] { "id", "-u" });
stdout.Trim().Should().Be("1000");

// #8 DockerBuild_ImageSize_UnderLimit
var inspect = await DockerClient.Images.InspectImageAsync("workflow-ui:test");
inspect.Size.Should().BeLessThan(250L * 1024 * 1024);
```

### 9. Integration tests
- `ContainerSmokeTests` class itself is the integration harness.

### 10. Golden-file / parity tests
- None specific to container (see 8.1.4 for version parity).

### 11. Edge cases & negative tests
- `WorkflowsRoot` path not mounted: container logs error on startup but still serves `/api/health`.
- Container killed mid-run: data persists via Stage 7.2.3 JSONL writes (hardlink mount).
- Base image CVE detected post-release: release pipeline re-runs Trivy weekly; alerts open.
- Running on ARM64 vs AMD64: image is multi-arch via `buildx`.
- Missing dependency in image (e.g., ICU): `dotnet publish` with `InvariantGlobalization=true`
  is used; test asserts absence of globalization calls that would fail.

### 12. Observability hooks
- Container `HEALTHCHECK` directive.
- Logs written to stdout in JSON (Serilog console sink with `CompactJsonFormatter`).
- `LABEL org.opencontainers.image.*` set from build args.

### 13. Risks & mitigations
- **R8.1.1-A — base image CVE**: weekly scan + renovate-bot update.
- **R8.1.1-B — size blowout from node_modules**: `.dockerignore` excludes; builder stage discarded.
- **R8.1.1-C — missing dependency**: `InvariantGlobalization=true` + alpine-icu only if needed.

### 14. Evaluator gap-analysis prompts
1. Is the final image multi-stage (no SDK in runtime)?
2. Is the user non-root?
3. Are base images pinned by digest?
4. Is the healthcheck actually wired?
5. Is size under 250 MB?
6. Does the smoke test exercise the real built image (not a mock)?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, container smoke tests green, image published to ghcr on tag.

---

### Step 2 — Helm chart (1 d)

### 1. Status
`[ ]`

### 2. Effort
1 engineering day.

### 3. Goal
Ship a Helm chart at `deploy/helm/workflow-ui/` with `Deployment`, `Service`, `Ingress`, and
`ConfigMap` templates. Values file surfaces image tag, replica count, `WorkflowsRoot` PVC
reference, auth options, ingress host + TLS secret. `helm-unittest` suite asserts each template
renders valid YAML with sane defaults.

### 4. Depends on
- Step 8.1.1

### 5. Inputs
- Published image reference.

### 6. Deliverables
- `Chart.yaml` with `apiVersion: v2`, `name: workflow-ui`, `version: 0.1.0`.
- `values.yaml` with commented defaults.
- Templates: deployment, service, ingress, configmap.
- `tests/deployment_test.yaml` using `helm-unittest` v0.5.

### 7. Acceptance criteria
- [ ] `helm template .` produces parseable YAML.
- [ ] `helm lint .` returns 0 errors.
- [ ] `helm unittest .` passes.
- [ ] Ingress template omitted when `ingress.enabled=false`.
- [ ] ConfigMap carries `WorkflowsRoot`, `RunsRoot`, `Auth__Authority`, `Auth__Audience`.
- [ ] Deployment includes liveness + readiness probes pointing at `/api/health`.
- [ ] PodSecurityContext `runAsNonRoot: true`, `runAsUser: 1000`.

### 8. Unit tests (helm-unittest)
1. `deployment_hasExpectedContainer`.
2. `deployment_replicaCount_matchesValues`.
3. `deployment_env_includesAuthAuthority`.
4. `service_port80_mapsToContainer8080`.
5. `ingress_disabled_byDefault`.
6. `ingress_enabled_rendersHost`.
7. `configmap_containsWorkflowsRoot`.
8. `podSecurityContext_nonRoot`.

AAA sketches (helm-unittest YAML form):

```yaml
# #2 deployment_replicaCount_matchesValues
suite: deployment
templates: [deployment.yaml]
tests:
  - it: replicaCount_matchesValues
    set:
      replicaCount: 3
    asserts:
      - equal:
          path: spec.replicas
          value: 3

  # #5 ingress_disabled_byDefault
  - it: ingress_disabled_byDefault
    templates: [ingress.yaml]
    asserts:
      - hasDocuments:
          count: 0

  # #8 podSecurityContext_nonRoot
  - it: podSecurityContext_nonRoot
    asserts:
      - equal:
          path: spec.template.spec.securityContext.runAsNonRoot
          value: true
      - equal:
          path: spec.template.spec.securityContext.runAsUser
          value: 1000
```

### 9. Integration tests
- None (unit + lint sufficient at this stage).

### 10. Golden-file / parity tests
- `helm-template-default.golden.yaml` — pinned output of `helm template .` with defaults.

### 11. Edge cases & negative tests
- `ingress.tls.enabled=true` without `secretName` → helm-unittest asserts render error.
- Negative replica count → helm lint warns.
- Misspelled key in values → chart still renders (Helm is permissive); documented in guide.
- Very long hostname: truncated per k8s label rules.
- PVC not present → Deployment still starts with `emptyDir` fallback.

### 12. Observability hooks
- ConfigMap includes `Logging__Console__FormatterName=json`.
- Deployment annotations `prometheus.io/scrape=true`, port `/metrics`.

### 13. Risks & mitigations
- **R8.1.2-A — chart drift vs container image**: CI step renders chart against image tag and
  fails if image referenced doesn't exist in registry.
- **R8.1.2-B — values doc rot**: comments in values.yaml are validated by a markdown snapshot
  test.

### 14. Evaluator gap-analysis prompts
1. Does `helm lint` pass?
2. Are probes wired to `/api/health`?
3. Is PodSecurityContext non-root?
4. Is ingress optional (feature flag)?
5. Does configmap expose all auth knobs?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, `helm template` + `helm unittest` green.

---

### Step 3 — Self-contained single-file exe (1 d)

### 1. Status
`[ ]`

### 2. Effort
1 engineering day.

### 3. Goal
Produce a self-contained single-file `dotnet publish` artifact for Windows (`win-x64`) and Linux
(`linux-x64`) with UI embedded under `wwwroot/`. The script emits a zip per platform to
`artifacts/publish/`. A GitHub Actions Windows runner executes a smoke script that launches the
exe, waits for `/api/health`, then shuts down cleanly.

### 4. Depends on
- Step 8.1.1

### 5. Inputs
- `publish-selfcontained.ps1` / `.sh` orchestration.

### 6. Deliverables
- `publish-selfcontained.ps1` (Windows) and `.sh` (Linux).
- `selfcontained.yml` workflow matrix over `[windows-latest, ubuntu-latest]`.
- `SelfContainedSmokeTests.cs` (Windows-runner only) that `Process.Start`s the exe with
  environment variables, polls health, then `Process.Kill()`.

### 7. Acceptance criteria
- [ ] Single-file exe under 150 MB (per platform).
- [ ] `--help` prints usage and exits 0.
- [ ] Starts, binds 8080, serves `/api/health` in < 10 s on CI runner.
- [ ] Shuts down gracefully on SIGTERM/SIGINT with code 0.
- [ ] Version string output of `--version` matches `version.json` generated value.
- [ ] Trimmed build passes (no `System.Reflection.Emit` runtime trim errors).
- [ ] `ReadyToRun=true` applied for startup speed.

### 8. Unit tests
1. `Publish_EmitsSingleFileExe`.
2. `Publish_IncludesWwwroot_StaticAssets`.
3. `Publish_VersionMatchesVersionJson`.
4. `SmokeLaunch_HealthResponds_Under10s`.
5. `SmokeLaunch_SigtermShutsdownCleanly`.
6. `SmokeLaunch_HelpExits0`.
7. `SmokeLaunch_VersionExits0_PrintsVersion`.
8. `Publish_NoTrimWarnings`.

Three AAA sketches:

```csharp
// #4 SmokeLaunch_HealthResponds_Under10s
var exe = Path.Combine(artifacts, "WorkflowUi.Host.exe");
var psi = new ProcessStartInfo(exe) {
    UseShellExecute = false, RedirectStandardOutput = true,
    Environment = { ["WorkflowUi__Auth__AllowDevBypass"]="true" }
};
using var p = Process.Start(psi)!;
var client = new HttpClient();
var deadline = DateTime.UtcNow.AddSeconds(10);
HttpResponseMessage? resp = null;
while (DateTime.UtcNow < deadline) {
    try { resp = await client.GetAsync("http://localhost:8080/api/health"); if (resp.IsSuccessStatusCode) break; }
    catch { await Task.Delay(200); }
}
p.Kill(entireProcessTree:true);
resp.Should().NotBeNull();
resp!.StatusCode.Should().Be(HttpStatusCode.OK);

// #5 SmokeLaunch_SigtermShutsdownCleanly
using var p = Process.Start(psi)!;
// ... wait for health ...
p.StandardInput.Close();      // Close stdin to trigger Ctrl+C equivalent via GenericHost
await p.WaitForExitAsync(TimeSpan.FromSeconds(5));
p.ExitCode.Should().Be(0);

// #3 Publish_VersionMatchesVersionJson
var exePath = ...;
var fvi = FileVersionInfo.GetVersionInfo(exePath);
var versionJson = JsonNode.Parse(File.ReadAllText("version.json"))!;
fvi.ProductVersion.Should().StartWith(versionJson["version"]!.ToString());
```

### 9. Integration tests
- `selfcontained.yml` runs smoke on Windows + Linux runners.

### 10. Golden-file / parity tests
- None specific.

### 11. Edge cases & negative tests
- Port 8080 already in use: exe exits with non-zero and friendly message.
- Missing wwwroot (build error scenario): exe serves API but UI returns 404 from SPA fallback.
- Trimming breaks YamlDotNet reflection: mitigated by `DynamicDependency` attrs on loader.
- Running on .NET version mismatch: self-contained bundles runtime, so N/A.
- Antivirus quarantines exe: documented mitigation (code-signing in Stage 8.3.1).

### 12. Observability hooks
- Metric `host_startup_duration_seconds` (custom counter).
- Log `"Host started at {Url}"` and `"Host stopped (exit {Code})"`.

### 13. Risks & mitigations
- **R8.1.3-A — trim warnings break runtime**: CI fails on any `IL2026`/`IL3050` warning.
- **R8.1.3-B — large exe**: `PublishTrimmed=true` with suppressed UI assemblies.

### 14. Evaluator gap-analysis prompts
1. Is the exe truly self-contained (runs without installed .NET)?
2. Does smoke test actually launch and query via HTTP?
3. Does `--version` read from `version.json`?
4. Are trim warnings treated as errors?
5. Is graceful shutdown verified?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, Windows+Linux smoke green, size under limit.

---

### Step 4 — Version stamp (assembly + UI footer) (1 d)

### 1. Status
`[ ]`

### 2. Effort
1 engineering day.

### 3. Goal
Stamp the semantic version (from `version.json` via Nerdbank.GitVersioning) into the assembly
(`AssemblyVersion`, `FileVersion`, `InformationalVersion`) and surface it in the UI footer.
Add `GET /api/version` endpoint returning `{ version, commitSha, buildUtc }` consumed by
`<AppFooter>`. Playwright snapshot locks the footer rendering against the release tag.

### 4. Depends on
- Step 8.1.1

### 5. Inputs
- `version.json` (existing in repo root).
- Git commit SHA from build env.

### 6. Deliverables
- `VersionInfo.cs` static class reading `InformationalVersion` at runtime.
- `VersionController.cs` — `[HttpGet("api/version")]` returning `VersionInfoDto`.
- `AppFooter.tsx` component fetching `/api/version` on mount.
- Unit test + Playwright snapshot test.

### 7. Acceptance criteria
- [ ] `GET /api/version` returns version matching `version.json`.
- [ ] UI footer renders `v{version} · {shortSha}` with tooltip showing build time.
- [ ] Mismatch between server `AssemblyVersion` and `version.json` fails CI.
- [ ] Endpoint is anonymous-accessible (health-adjacent; no auth required).
- [ ] Version exposed via response header `X-Workflow-Ui-Version` on every response.
- [ ] Playwright snapshot matches after tag bump when regenerated.

### 8. Unit tests
1. `VersionInfo_Reads_InformationalVersion`.
2. `VersionController_Get_ReturnsShape`.
3. `VersionController_Anonymous_Allowed`.
4. `ResponseHeader_PresentOnEveryResponse`.
5. `BuildUtc_WithinLastYear_Sanity`.
6. `CommitSha_MatchesEnvVar`.
7. `AppFooter_RendersVersion_FromApi` (RTL).
8. `AppFooter_RendersFallback_WhenApiFails`.

AAA sketch for #7:

```ts
test('AppFooter_RendersVersion_FromApi', async () => {
  server.use(rest.get('/api/version', (req, res, ctx) =>
    res(ctx.json({ version: '1.2.3', commitSha: 'abcdef0', buildUtc: '2025-01-01T00:00:00Z' }))));
  render(<AppFooter />);
  expect(await screen.findByText(/v1\.2\.3.*abcdef0/)).toBeInTheDocument();
});
```

### 9. Integration tests
- Playwright `footer.spec.ts` — assert visible text matches pattern.

### 10. Golden-file / parity tests
- `VersionInfoDto.json` shape golden.
- `version.json` ↔ `AssemblyVersion` parity test (MSBuild + test-time assertion).

### 11. Edge cases & negative tests
- Build with `-dev` suffix: footer shows suffix.
- API fails (500): footer shows `v?` and logs warning.
- Very long version string (with metadata): footer truncates with tooltip.
- Missing commit SHA (local build): `"local"` placeholder.
- Response header excluded on `/hubs/*` (SignalR pre-negotiates): documented.

### 12. Observability hooks
- Log `"Workflow UI version {Version} commit {Sha}"` at startup.

### 13. Risks & mitigations
- **R8.1.4-A — version drift**: CI parity test blocks merge if `version.json` and
  `AssemblyVersion` disagree.
- **R8.1.4-B — snapshot brittleness**: use pattern match, not exact string.

### 14. Evaluator gap-analysis prompts
1. Is `InformationalVersion` the single source (tamper-evident)?
2. Is `/api/version` anonymous (no auth required)?
3. Does the response header appear on every response (middleware)?
4. Does the Playwright snapshot use a pattern, not a literal?
5. Is parity between `version.json` and assembly enforced in CI?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, parity test green, Playwright snapshot stable.
