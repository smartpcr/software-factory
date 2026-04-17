# Stage 1.1 — Server host

**Phase:** Phase 1 — Host, Contract, CI
**Stage status:** [ ]
**Total effort:** 6 d
**Step count:** 3

## Steps

- [ ] **Step 1**: Health and engine-version endpoints
- [ ] **Step 2**: DI registration of engine services
- [ ] **Step 3**: Problem-details middleware, correlation ID, request logging

---
## Context

Stage 1.1 promotes the empty ASP.NET Core skeleton from Stage 0.1 into a first-class host that the designer UI can actually talk to. It introduces three capabilities: (1) minimal-API health and engine-version endpoints that prove the engine assembly is reachable from HTTP, (2) DI registration of the engine's load/execute surfaces (`WorkflowLoader`, `WorkflowSerializer`, and an executor factory), and (3) RFC-7807 problem-details middleware with correlation-id propagation and structured request logging. Nothing YAML-related, no parity logic, no SignalR — those belong to later stages. The goal is a correct, observable host boot sequence that later stages extend.

The ground truth this stage relies on (master plan [§1.1](../../workflow-ui-plan.md#11-architecture-at-a-glance) and engine sources):

- `ExecutionEngine.Workflow.WorkflowLoader` (`src/ExecutionEngine/Workflow/WorkflowLoader.cs`) — has constructor `WorkflowLoader(IServiceProvider? = null)` that pulls `ILoggerFactory` from DI; we register it as a scoped service and pass the service provider through.
- `ExecutionEngine.Workflow.WorkflowSerializer` (`src/ExecutionEngine/Workflow/WorkflowSerializer.cs`) — parameterless constructor; register as singleton since it's stateless.
- `ExecutionEngine.Workflow.WorkflowValidator` (used internally by `WorkflowLoader`) — do not re-register separately; the Loader instantiates it.
- The engine assembly version is obtained from `typeof(WorkflowDefinition).Assembly.GetName().Version` (per master plan §1.1 risks).

This stage unblocks Stage 1.2 (Contract generation uses the host for reflection hosting in test builds) and Stage 1.3 (CI + Playwright smoke targets this host).

## Prerequisites
- Stage 0.1 complete; `src/WorkflowUi.Server/Program.cs` has the `public partial class Program { }` sentinel for `WebApplicationFactory`.
- Stage 0.2 complete; engine assembly is known to load successfully.
- Stage 0.3 complete; CI runs `dotnet build`/`test` for the server.

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage

Server
- `src/WorkflowUi.Server/Program.cs` (modified) — compose host.
- `src/WorkflowUi.Server/Endpoints/HealthEndpoints.cs` (new, Step 1.1.1).
- `src/WorkflowUi.Server/Endpoints/EngineVersionEndpoints.cs` (new, Step 1.1.1).
- `src/WorkflowUi.Server/Hosting/EngineServiceCollectionExtensions.cs` (new, Step 1.1.2).
- `src/WorkflowUi.Server/Hosting/CorrelationIdMiddleware.cs` (new, Step 1.1.3).
- `src/WorkflowUi.Server/Hosting/ProblemDetailsWriter.cs` (new, Step 1.1.3).
- `src/WorkflowUi.Server/Hosting/RequestLoggingEnricher.cs` (new, Step 1.1.3).

Tests
- `src/WorkflowUi.Server.Tests/WorkflowUi.Server.Tests.csproj` (new) — xUnit + `Microsoft.AspNetCore.Mvc.Testing`.
- `src/WorkflowUi.Server.Tests/HealthEndpointsTests.cs` (new, Step 1.1.1).
- `src/WorkflowUi.Server.Tests/EngineVersionEndpointsTests.cs` (new, Step 1.1.1).
- `src/WorkflowUi.Server.Tests/DiRegistrationTests.cs` (new, Step 1.1.2).
- `src/WorkflowUi.Server.Tests/ProblemDetailsTests.cs` (new, Step 1.1.3).
- `src/WorkflowUi.Server.Tests/CorrelationIdTests.cs` (new, Step 1.1.3).

Solution
- `Workflow.sln` (modified) — add `WorkflowUi.Server.Tests`.

## Shared fixtures / fakes
- A `TestWebApplicationFactory<Program>` helper under `src/WorkflowUi.Server.Tests/Infrastructure/` (new) — seeds an in-memory configuration, overrides `ILoggerFactory` with a `Xunit.Abstractions.ITestOutputHelper`-bound sink, shared across all 1.1.* tests.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="1.1"`, `phaseId="1"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-1.1.plan-read`, `workflow-ui.stage-1.1.apply`, `workflow-ui.stage-1.1.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_1_1_started_total`, counter `workflow_ui_stage_1_1_failed_total`, histogram `workflow_ui_stage_1_1_duration_ms`, and any stage-specific counter named in the step-level observability sections.

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
- User-visible behavior introduced by Stage 1.1 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage11` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

---

### Step 1 — Health and engine-version endpoints

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 0.1.1

### Goal
Expose two minimal-API endpoints: `GET /api/health` (returns 200 + `{"status":"ok","timestamp":"..."}`) and `GET /api/engine/version` (returns 200 + `{"engineAssemblyVersion":"x.y.z.w","engineFileVersion":"x.y.z.w"}`). The engine version must be read from `typeof(WorkflowDefinition).Assembly.GetName().Version` (master plan §1.1 risk note). Wire these endpoints via top-level `MapGet` in `Program.cs`, but delegate handler logic to methods on `HealthEndpoints` / `EngineVersionEndpoints` so they are unit-testable without `WebApplicationFactory`. Add `WebApplicationFactory<Program>` integration tests that assert both endpoints return 200 and a non-empty JSON body with the expected fields.

### Depends on
0.1.1.

### Inputs
- `src/ExecutionEngine/ExecutionEngine.csproj` via `ProjectReference`.
- `src/WorkflowUi.Server/Program.cs` with the `public partial class Program` sentinel.

### Deliverables
- `src/WorkflowUi.Server/Endpoints/HealthEndpoints.cs` (new) — static `MapHealthEndpoints(WebApplication app)` extension; handler returns `Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow })`.
- `src/WorkflowUi.Server/Endpoints/EngineVersionEndpoints.cs` (new) — static `MapEngineVersionEndpoints(WebApplication app)`; handler reads version via `EngineAssemblyVersionProvider`.
- `src/WorkflowUi.Server/Endpoints/EngineAssemblyVersionProvider.cs` (new) — `public sealed class EngineAssemblyVersionProvider { public EngineVersionInfo GetVersion() { ... } }` — isolates reflection for unit testing.
- `src/WorkflowUi.Server/Program.cs` (modified) — registers endpoints, configures JSON serialization (camelCase, ISO-8601 timestamps).
- `src/WorkflowUi.Server.Tests/WorkflowUi.Server.Tests.csproj` (new) — references `Microsoft.AspNetCore.Mvc.Testing`, `FluentAssertions`, `xunit`.
- `src/WorkflowUi.Server.Tests/HealthEndpointsTests.cs` (new) — 4 tests.
- `src/WorkflowUi.Server.Tests/EngineVersionEndpointsTests.cs` (new) — 4 tests.
- `src/WorkflowUi.Server.Tests/Infrastructure/TestWebApplicationFactory.cs` (new) — shared harness.

### Acceptance criteria
- [ ] `GET /api/health` returns 200 with JSON `{"status":"ok","timestamp":"<iso-8601>"}`.
- [ ] `GET /api/engine/version` returns 200 with non-empty `engineAssemblyVersion`.
- [ ] The reported version matches `typeof(ExecutionEngine.Workflow.WorkflowDefinition).Assembly.GetName().Version!.ToString()` byte-for-byte.
- [ ] All endpoints use camelCase JSON property names (asserted by response-body contains `"engineAssemblyVersion"` not `"EngineAssemblyVersion"`).
- [ ] Integration tests use `WebApplicationFactory<Program>` (requires the partial-class sentinel from Stage 0.1.1).
- [ ] The two endpoints are tagged with OpenAPI groups `"Health"` and `"Engine"` respectively (prep for Phase 1.3 swagger).
- [ ] `EngineAssemblyVersionProvider` is a sealed, DI-registered singleton so its reflection cost is amortised.

### Unit tests
1. `HealthEndpointsTests_GetHealth_Returns200`
2. `HealthEndpointsTests_GetHealth_BodyHasStatusOk`
3. `HealthEndpointsTests_GetHealth_BodyHasIsoTimestamp`
4. `HealthEndpointsTests_Handler_ProducesNewTimestampEachCall`
5. `EngineVersionEndpointsTests_GetVersion_Returns200`
6. `EngineVersionEndpointsTests_GetVersion_BodyHasEngineAssemblyVersion_NonEmpty`
7. `EngineVersionEndpointsTests_Provider_ReadsFromWorkflowDefinitionAssembly`
8. `EngineVersionEndpointsTests_Provider_IsSingleton_InDi`
9. `EngineVersionEndpointsTests_JsonProperty_IsCamelCase`
10. `Program_PartialClass_SentinelExists` — Reflection asserts `typeof(Program).IsPublic && !Program.IsAbstract`, a defense test for `WebApplicationFactory`.

A/A/A for the three most important:

- `EngineVersionEndpointsTests_GetVersion_BodyHasEngineAssemblyVersion_NonEmpty`
  - Arrange: `using var client = factory.CreateClient();`
  - Act: `var resp = await client.GetAsync("/api/engine/version"); var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());`
  - Assert: `resp.IsSuccessStatusCode`; `doc.RootElement.GetProperty("engineAssemblyVersion").GetString()` is non-null, non-empty, and `Version.TryParse(...)` succeeds.
- `EngineVersionEndpointsTests_Provider_ReadsFromWorkflowDefinitionAssembly`
  - Arrange: `var provider = new EngineAssemblyVersionProvider();`
  - Act: `var info = provider.GetVersion();`
  - Assert: `info.EngineAssemblyVersion == typeof(ExecutionEngine.Workflow.WorkflowDefinition).Assembly.GetName().Version!.ToString();`
- `HealthEndpointsTests_GetHealth_BodyHasIsoTimestamp`
  - Arrange: GET `/api/health`.
  - Act: parse `timestamp`.
  - Assert: `DateTimeOffset.TryParse(..., out var _)` and value is within 5 seconds of `DateTimeOffset.UtcNow`.

### Integration tests
- `WebApplicationFactory<Program>` boots host in-memory with overridden `IConfiguration`. Setup: build factory once per test class with `IClassFixture<TestWebApplicationFactory>`. Teardown: factory disposes on class teardown.
- Scenarios:
  1. Cold-boot → GET /api/health → 200.
  2. Two sequential GETs to /api/engine/version return the same version string (provider cached).
  3. GET to unknown path returns 404 (not 500) — exercises default pipeline.
  4. GET /api/health under artificial DI failure (override `IEngineAssemblyVersionProvider` with a throwing stub) still returns 200 for /health — proves health is independent of engine DI.

### Golden-file / parity tests
Not applicable — no YAML, no DTOs, no contracts.

### Edge cases & negative tests
1. Engine assembly has no `AssemblyVersion` attribute — provider returns `"0.0.0.0"` and endpoint still returns 200.
2. Request with `Accept: text/xml` — endpoint returns 406 (ASP.NET Core default content-negotiation).
3. Request with malformed query string — still 200 (endpoints ignore query).
4. Endpoint invoked before `WebApplication.Run()` — in tests this is handled by the factory; document explicit early-boot pitfalls.
5. Two requests race the provider — since provider is stateless reflection, no race.
6. `WorkflowDefinition` type moves namespaces in a future engine version — `Provider_ReadsFromWorkflowDefinitionAssembly` fails loud.

### Observability hooks
- Log category `WorkflowUi.Server.Endpoints.Health` — Information on every request, Debug on response.
- Log category `WorkflowUi.Server.Endpoints.EngineVersion` — Information including the reported version.
- Structured fields emitted: `correlationId` (populated in 1.1.3), `route`, `method`, `statusCode`, `elapsedMs`.
- No metric counters yet (Phase 6 adds Prometheus).

### Risks & mitigations
- Assembly version resolution differs per build (Release vs Debug). `typeof(WorkflowDefinition).Assembly.GetName().Version` is stable across configs.
- `WebApplicationFactory` fails if `Program` is not a partial class — already addressed in 0.1.1.
- JSON casing regressions — pin via `builder.Services.ConfigureHttpJsonOptions(...)` with `JsonNamingPolicy.CamelCase`.

### Evaluator gap-analysis prompts
1. Does `GET /api/engine/version` read the version from `typeof(ExecutionEngine.Workflow.WorkflowDefinition).Assembly.GetName().Version`?
2. Is `EngineAssemblyVersionProvider` DI-registered as a singleton?
3. Do the integration tests use `WebApplicationFactory<Program>` with the `public partial class Program` sentinel from Stage 0.1.1?
4. Is the JSON response camelCase (`engineAssemblyVersion`, not `EngineAssemblyVersion`)?
5. Are both endpoints tagged with OpenAPI groups for Phase 1.3 swagger consumption?
6. Does the health handler produce a fresh timestamp on each call (no memoisation bug)?
7. Does a separate test prove `/api/health` remains healthy even if engine DI throws?

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

### Step 2 — DI registration of engine services

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 1.1.1

### Goal
Introduce `EngineServiceCollectionExtensions.AddExecutionEngine(this IServiceCollection services)` which registers: `WorkflowSerializer` as singleton (stateless per `src/ExecutionEngine/Workflow/WorkflowSerializer.cs`), `WorkflowLoader` as scoped (constructor takes `IServiceProvider?` per `WorkflowLoader.cs:27`), and any executor-factory needed for the yet-to-come Phase 7 execution surface (registered behind an `IWorkflowExecutorFactory` abstraction so Stage 1.1 doesn't leak executor details). No executor is actually executed here; we only prove the DI graph composes.

### Depends on
1.1.1.

### Inputs
- `src/ExecutionEngine/Workflow/WorkflowLoader.cs`.
- `src/ExecutionEngine/Workflow/WorkflowSerializer.cs`.

### Deliverables
- `src/WorkflowUi.Server/Hosting/EngineServiceCollectionExtensions.cs` (new) — extension method `AddExecutionEngine` registering both types plus a placeholder `IWorkflowExecutorFactory` abstraction.
- `src/WorkflowUi.Server/Hosting/IWorkflowExecutorFactory.cs` (new) — minimal interface (one method: `IWorkflowExecutor Create(WorkflowDefinition def)`), placeholder implementation throws `NotImplementedException` with a pointer to Phase 7 — documented so test assertions are explicit.
- `src/WorkflowUi.Server/Program.cs` (modified) — `builder.Services.AddExecutionEngine()`.
- `src/WorkflowUi.Server.Tests/DiRegistrationTests.cs` (new).
- `src/WorkflowUi.Server.Tests/Infrastructure/ServiceProviderProbe.cs` (new) — helper to resolve services under test lifetimes (root scope + child scope).

### Acceptance criteria
- [ ] `WorkflowSerializer` resolves as singleton (two resolutions return the same instance).
- [ ] `WorkflowLoader` resolves as scoped (two resolutions in different scopes return distinct instances; same scope returns same).
- [ ] `WorkflowLoader` is constructed with the ambient `IServiceProvider` so its internal `ILoggerFactory` comes from DI (per `WorkflowLoader.cs:28-31`).
- [ ] The placeholder `IWorkflowExecutorFactory` registration produces a service whose `Create()` throws `NotImplementedException("Pending Phase 7")`.
- [ ] Missing `AddExecutionEngine` leaves the host unable to resolve `WorkflowLoader` (negative test).
- [ ] A reflection guard fails red if `WorkflowLoader`'s constructor signature changes in a future engine version (loose-coupling defense).
- [ ] No engine internals (e.g., `WorkflowValidator`) are over-registered — we rely on `WorkflowLoader` to instantiate them internally.

### Unit tests
1. `DiRegistrationTests_Serializer_ResolvesAsSingleton`
2. `DiRegistrationTests_Loader_ResolvesAsScoped`
3. `DiRegistrationTests_Loader_ReceivesServiceProviderWithLoggerFactory`
4. `DiRegistrationTests_ExecutorFactory_Registered_AsSingleton`
5. `DiRegistrationTests_ExecutorFactory_Create_Throws_WithPhase7Marker`
6. `DiRegistrationTests_WithoutAddExecutionEngine_Loader_FailsToResolve`
7. `DiRegistrationTests_LoaderConstructor_SignatureUnchanged` — reflection probe asserts `WorkflowLoader` has a ctor `(IServiceProvider?)`.
8. `DiRegistrationTests_AddExecutionEngine_IsIdempotent` — calling twice doesn't duplicate registrations.

A/A/A for the three most important:

- `DiRegistrationTests_Serializer_ResolvesAsSingleton`
  - Arrange: `var sp = new ServiceCollection().AddExecutionEngine().BuildServiceProvider();`
  - Act: `var a = sp.GetRequiredService<WorkflowSerializer>(); var b = sp.GetRequiredService<WorkflowSerializer>();`
  - Assert: `Assert.Same(a, b);`
- `DiRegistrationTests_Loader_ReceivesServiceProviderWithLoggerFactory`
  - Arrange: `var sp = new ServiceCollection().AddLogging().AddExecutionEngine().BuildServiceProvider();`
  - Act: `var loader = sp.GetRequiredService<WorkflowLoader>();`
  - Assert: Use reflection to peek the private `logger` field; assert non-null and `GetType().FullName` matches an actual `ILogger` implementation (not `NullLogger`).
- `DiRegistrationTests_LoaderConstructor_SignatureUnchanged`
  - Arrange: `var ctors = typeof(WorkflowLoader).GetConstructors();`
  - Act: find ctor with single `IServiceProvider?` param.
  - Assert: exactly one such ctor exists; param name is `serviceProvider`.

### Integration tests
- Spin up `WebApplicationFactory<Program>`. In a test scope, resolve `WorkflowLoader` and `WorkflowSerializer`, verify a trivial round-trip (`ser.ToYaml(new WorkflowDefinition { Name = "x" })` is non-empty — serialization smoke).
- Scenarios:
  1. Cold boot → resolve Loader → non-null.
  2. Two requests on two scopes → two Loader instances.
  3. Stress: 100 concurrent scopes → no deadlock; no `ObjectDisposedException`.

### Golden-file / parity tests
Not applicable.

### Edge cases & negative tests
1. `AddExecutionEngine` called before `AddLogging` — DI graph still builds but `WorkflowLoader` falls back to `NullLoggerFactory.Instance` per `WorkflowLoader.cs:30`.
2. Same call registered twice — `AddExecutionEngine_IsIdempotent` uses `TryAddScoped`/`TryAddSingleton` semantics.
3. A future engine version adds a second constructor parameter — `LoaderConstructor_SignatureUnchanged` fails loud.
4. `WorkflowSerializer` gains state (stops being thread-safe) — we'd catch it in Phase 2 round-trip tests; documented as a forward risk.
5. `IWorkflowExecutorFactory` accidentally resolved in production code before Phase 7 — throws with a very explicit message naming Phase 7.
6. DI captive-dependency bug (singleton holding scoped ref) — not possible here because the dependency edges go scoped → singleton, not the reverse.

### Observability hooks
- Log category `WorkflowUi.Server.Hosting.Di` — Information line on boot: "Registered WorkflowLoader (scoped), WorkflowSerializer (singleton), IWorkflowExecutorFactory (singleton placeholder)".

### Risks & mitigations
- Engine statics (`WorkflowLoader` stores serializer/validator as instance fields) — not static in current source; risk is forward-looking, documented.
- Scoped lifetime surprises in Kestrel middleware — our Loader usage is endpoint-scoped, correct by construction.

### Evaluator gap-analysis prompts
1. Is `WorkflowLoader` registered as scoped (not singleton)?
2. Is `WorkflowSerializer` registered as singleton?
3. Does the Loader receive the ambient `IServiceProvider` so its internal `ILoggerFactory` comes from DI?
4. Does a test prove the constructor signature of `WorkflowLoader` is `(IServiceProvider?)` — catching future engine refactors?
5. Is `AddExecutionEngine` idempotent (no double-registration when called twice)?
6. Does `IWorkflowExecutorFactory` throw with a Phase 7 marker so premature usage fails loud?

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

### Step 3 — Problem-details middleware, correlation ID, request logging

**Status:** [ ]
**Effort:** 2 d
**Depends on:** 1.1.2

### Goal
Add three pieces of middleware in the right order: (1) `CorrelationIdMiddleware` reads `X-Correlation-Id` header or generates a new `Guid.NewGuid().ToString("N")`, stores it in `HttpContext.Items["CorrelationId"]` and echoes it in the response header; (2) a structured request-logging enricher writes `correlationId`, `method`, `path`, `statusCode`, `elapsedMs` on every response using `Serilog`-style enrichers (or `ILogger.BeginScope` for MEL); (3) a `UseExceptionHandler`-based ProblemDetails pipeline that produces RFC-7807-compliant JSON (`{ type, title, status, detail, instance, correlationId }`). Order: correlation → logging → problem-details; verified by integration test.

### Depends on
1.1.2.

### Inputs
- `Program.cs` from 1.1.2.
- ASP.NET Core 8's built-in `IProblemDetailsService`.

### Deliverables
- `src/WorkflowUi.Server/Hosting/CorrelationIdMiddleware.cs` (new) — delegating middleware class; constant `HeaderName = "X-Correlation-Id"`.
- `src/WorkflowUi.Server/Hosting/ProblemDetailsWriter.cs` (new) — custom `IProblemDetailsWriter` that adds `correlationId` extension to the payload.
- `src/WorkflowUi.Server/Hosting/RequestLoggingEnricher.cs` (new) — middleware that wraps handler in a logger scope containing `correlationId`; logs start + end with `elapsedMs`.
- `src/WorkflowUi.Server/Program.cs` (modified) — pipeline wiring: `UseMiddleware<CorrelationIdMiddleware>()`, `UseMiddleware<RequestLoggingEnricher>()`, `UseExceptionHandler(...)`.
- `src/WorkflowUi.Server.Tests/ProblemDetailsTests.cs` (new).
- `src/WorkflowUi.Server.Tests/CorrelationIdTests.cs` (new).
- `src/WorkflowUi.Server.Tests/MiddlewareOrderTests.cs` (new) — integration test asserts ordering.

### Acceptance criteria
- [ ] A 500 response has body `application/problem+json` with fields `type, title, status, detail, instance, correlationId`.
- [ ] Request with `X-Correlation-Id: abc-123` echoes the same header back on the response.
- [ ] Request without that header receives a server-generated 32-char hex correlation ID.
- [ ] Response header `X-Correlation-Id` is always present.
- [ ] Log output (captured via `ITestOutputHelper` sink) contains `correlationId` as a structured field for every request.
- [ ] Middleware ordering is: CorrelationId → RequestLogging → ExceptionHandler → endpoints (verified by an integration test that injects a throwing endpoint and asserts the problem-details body contains the correlation ID produced by the first middleware).
- [ ] Exception stack traces are NOT exposed in production (only in Development).
- [ ] `Problem_Details_Type` field is a stable URL such as `https://workflow-ui/problems/internal-server-error`.

### Unit tests
1. `CorrelationIdTests_MissingHeader_ServerGeneratesGuid`
2. `CorrelationIdTests_ProvidedHeader_IsPreserved_AndEchoed`
3. `CorrelationIdTests_ResponseAlways_HasHeader`
4. `CorrelationIdTests_InvalidHeader_TooLong_Rejected_AndServerRegenerates` — guard against log injection (truncate > 128 chars).
5. `ProblemDetailsTests_500Response_IsProblemPlusJson_ContentType`
6. `ProblemDetailsTests_500Response_BodyHas_Required_Fields`
7. `ProblemDetailsTests_500Response_CorrelationId_MatchesHeader`
8. `ProblemDetailsTests_DevelopmentEnv_IncludesException_Detail`
9. `ProblemDetailsTests_ProductionEnv_DoesNotInclude_Exception_Detail`
10. `RequestLoggingEnricher_LogScope_IncludesCorrelationId`
11. `MiddlewareOrderTests_Correlation_Runs_Before_ProblemDetails`

A/A/A for the three most important:

- `ProblemDetailsTests_500Response_CorrelationId_MatchesHeader`
  - Arrange: configure factory with a throwing endpoint `app.MapGet("/boom", () => throw new InvalidOperationException("boom"));`
  - Act: `var resp = await client.GetAsync("/boom");`
  - Assert: `resp.StatusCode == 500; resp.Headers.GetValues("X-Correlation-Id").Single() == JsonDocument.Parse(body).RootElement.GetProperty("correlationId").GetString();`
- `CorrelationIdTests_ProvidedHeader_IsPreserved_AndEchoed`
  - Arrange: `client.DefaultRequestHeaders.Add("X-Correlation-Id", "test-corr-1");`
  - Act: GET /api/health.
  - Assert: response header value equals `"test-corr-1"`.
- `MiddlewareOrderTests_Correlation_Runs_Before_ProblemDetails`
  - Arrange: throwing endpoint as above.
  - Act: inspect response.
  - Assert: problem-details body's `correlationId` equals response header; proves correlation middleware produced the ID before exception handler read it.

### Integration tests
- WebApplicationFactory with env `"Development"` — problem details include stack-trace-free `detail` field.
- WebApplicationFactory with env `"Production"` — `detail` is generic "An error occurred"; exception stack absent.
- Parallel-request test: 10 concurrent requests produce 10 distinct correlation IDs (no shared state bug).

### Golden-file / parity tests
Not applicable.

### Edge cases & negative tests
1. Malicious `X-Correlation-Id: <script>alert()</script>` — truncated at 128 chars, and safely echoed as a header (not logged into HTML).
2. Correlation ID containing CRLF — rejected/regenerated.
3. Request that doesn't throw — no problem-details body; 200 OK with correlation header.
4. Handler that manually writes a `problem+json` body — middleware must not double-wrap. Integration test with a `Results.Problem(...)` endpoint verifies correlation is still appended without double-serialisation.
5. Exception in middleware itself (e.g., in `RequestLoggingEnricher.Stop`) — must not mask the inner exception; caught and logged at Error.
6. Response already started when exception thrown — ProblemDetails cannot re-write; behaviour is fallthrough with a Warning log.

### Observability hooks
- Log category `WorkflowUi.Server.Hosting.RequestLogging` — Information on every request (method, path, status, elapsedMs, correlationId).
- Log category `WorkflowUi.Server.Hosting.ProblemDetails` — Error on every 500 (type, title, correlationId, exceptionTypeName).
- No metrics counters in this stage.

### Risks & mitigations
- Middleware ordering bugs — `MiddlewareOrderTests_*` series catches.
- Correlation-ID log injection — mitigated by length cap + control-character strip.
- `UseExceptionHandler` + `UseDeveloperExceptionPage` interaction — only one allowed; Program.cs uses env-based branch.

### Evaluator gap-analysis prompts
1. Is the middleware order exactly CorrelationId → RequestLogging → ExceptionHandler → endpoints?
2. Does the ProblemDetails JSON payload include a `correlationId` field matching the response header?
3. Is the correlation ID length-capped at 128 chars and CR/LF-stripped before echoing?
4. Are stack traces suppressed in Production but visible in Development?
5. Does `RequestLoggingEnricher` use `ILogger.BeginScope` (or equivalent) so downstream log lines automatically carry the correlation ID?
6. Is there a test that runs 10 concurrent requests and asserts 10 distinct correlation IDs?
7. Does the problem-details `type` URL follow a stable scheme like `https://workflow-ui/problems/<code>`?

### Scoring rubric (step-level, weights sum to 100)
- Deliverable completeness 25
- Test coverage & assertion quality 25
- Contract/parity fidelity 20
- Edge-case handling 10
- Observability 10
- Clarity & traceability 10

### Definition of done
Evaluator score ≥ 90, zero CRITICAL, zero HIGH findings, all acceptance criteria checked.
