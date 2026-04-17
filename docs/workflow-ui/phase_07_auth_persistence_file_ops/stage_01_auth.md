# Stage 7.1 — Auth

**Phase:** §13 Phase 7 — Auth, Persistence, File Ops
**Stage status:** [ ]
**Total effort:** 9 d
**Step count:** 4

## Steps

- [ ] **Step 1**: JWT bearer scaffold + dev-mode bypass (2 d)
- [ ] **Step 2**: OIDC integration (3 d)
- [ ] **Step 3**: Role-based policies (`Editor`, `Viewer`) (2 d)
- [ ] **Step 4**: Audit log of write endpoints (2 d)

---
## Context

Phase 7.1 introduces authentication and authorization to an otherwise anonymous host. The master
plan (§13) pins two roles — `Editor` and `Viewer` — enforced via policy. Transport is JWT bearer
tokens; the production path is OIDC (Azure AD or any generic provider), while dev mode uses
local PAT-style bearer tokens so contributors can run the designer offline. Audit logging covers
every write endpoint and is consumed by Stage 7.2 persistence for forensic trails.

Ground truth for this stage is architectural, not engine-derived — ExecutionEngine has no
auth surface. The stage therefore establishes conventions the rest of Phase 7 builds on:
- All `/api/*` and `/hubs/*` endpoints require an authenticated principal by default
  (`AuthorizationOptions.FallbackPolicy`).
- `IWorkflowStore` operations (Stage 7.2) take a `ClaimsPrincipal` and enforce `Editor` role on
  writes, `Viewer` on reads.
- Audit log emits a row per write, containing `(Timestamp, UserId, Action, TargetPath, ETag,
  OutcomeCode)` — PII is reduced to principal id only (no email logged).

Dev-mode bypass is gated by `ASPNETCORE_ENVIRONMENT == Development` AND
`WorkflowUi__Auth__AllowDevBypass == "true"`. Any other configuration emits a startup log warning
`"DEV BYPASS ACTIVE — do not use in prod"` and prevents the host from binding to a non-loopback
address.

## Prerequisites
- Phase 1 host skeleton (Step 1.1.3).
- Stage 6.2 hub exists (adds `[Authorize]` to it as part of Step 7.1.1).
- External deps: `Microsoft.AspNetCore.Authentication.JwtBearer 8.0.x`,
  `Microsoft.AspNetCore.Authentication.OpenIdConnect 8.0.x`,
  `Microsoft.Identity.Web 2.x` (used for Azure AD path; generic OIDC does not require it).
- Playwright 1.45 for OIDC flow test (uses `mock-oauth2-server` container).

## Deliverable symbol contract
- Every code-producing step in this stage must list its primary exported symbols inline in the step-level **Deliverables** section using exact names + signatures (for example `FooMapper.Map(BarDto dto) -> BarDefinition`, `useGraphStore() -> GraphStoreApi`, or `POST /api/workflows/parse -> 200 ParseWorkflowResponse`).
- If a deliverables bullet groups multiple files, append the symbol names in that same bullet before implementation begins; evaluators should score unnamed exports as incomplete even when the file path is present.

## Files owned by this stage
- `src/host/WorkflowUi.Host/Auth/AuthOptions.cs` (new)
- `src/host/WorkflowUi.Host/Auth/DevBypassAuthenticationHandler.cs` (new)
- `src/host/WorkflowUi.Host/Auth/RoleClaimsTransformer.cs` (new)
- `src/host/WorkflowUi.Host/Auth/AuthPolicies.cs` (new — `Editor`, `Viewer`)
- `src/host/WorkflowUi.Host/Auth/AuditLog.cs` (new, Step 7.1.4)
- `src/host/WorkflowUi.Host/Auth/AuditLogEntry.cs` (new)
- `src/host/WorkflowUi.Host/Auth/AuditMiddleware.cs` (new — logs writes)
- `src/host/WorkflowUi.Host/DependencyInjection/AuthServiceCollectionExtensions.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Auth/DevBypassTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Auth/RoleClaimsTransformerTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Auth/PolicyTests.cs` (new)
- `src/host/WorkflowUi.Host.UnitTests/Auth/AuditLogTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Auth/UnauthenticatedTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Auth/OidcFlowTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Auth/RoleEnforcementTests.cs` (new)
- `src/host/WorkflowUi.Host.IntegrationTests/Auth/AuditEmissionTests.cs` (new)
- `src/ui/src/auth/AuthProvider.tsx` (new)
- `src/ui/e2e/auth-oidc-mock.spec.ts` (new)

## Shared fixtures / fakes
- **`mock-oauth2-server`** (ghcr.io/navikt/mock-oauth2-server:2.x) — Testcontainers-based OIDC
  mock for `OidcFlowTests`.
- **`TestJwtFactory`** — static helper producing signed JWTs with given `sub`, `roles`, `exp`.
- **`InMemoryAuditLog`** — sink double used by integration tests for assertions.

## Stage-wide observability contract
- **Serilog structured fields** — `stageId="7.1"`, `phaseId="7"`, `stepId`, `operation`, `durationMs`, `workflowId`, `runtimeType`, `correlationId`, `requestId`, `path`, `validationCode`.
- **OTel spans** — `workflow-ui.stage-7.1.plan-read`, `workflow-ui.stage-7.1.apply`, `workflow-ui.stage-7.1.verify`. Child spans SHOULD suffix the concrete action (for example `...verify.roundtrip` or `...apply.signalr`).
- **Metrics** — counter `workflow_ui_stage_7_1_started_total`, counter `workflow_ui_stage_7_1_failed_total`, histogram `workflow_ui_stage_7_1_duration_ms`, and any stage-specific counter named in the step-level observability sections.

## Failure modes table
| Component | Failure | Detection | Recovery |
|---|---|---|---|
| auth surface | token / cookie leakage in logs | security tests or redaction audit fails | redact headers/claims and narrow structured fields |
| persistence layer | concurrent saves or stale writes win | etag/version tests fail | require optimistic concurrency token or last-write warning |
| file sandbox | path traversal or root escape | negative path tests fail | canonicalize full path and reject outside-root access |

## Performance budgets
| Operation | Target p50 | Target p95 |
|---|---:|---:|
| token refresh or auth handshake | 150 ms | 500 ms |
| YAML save / sidecar save | 200 ms | 750 ms |
| sandboxed file open / list | 150 ms | 600 ms |

## Accessibility checklist
- [ ] Auth dialogs/forms support full keyboard navigation and announce failures inline.
- [ ] File pickers and tree views expose ARIA roles, selection state, and current path context.
- [ ] Permission-denied states use clear text, not icon-only indicators.
- [ ] Autosave/toast messages are announced via a polite live region.
- [ ] Sensitive values (tokens/paths) are masked but still copyable through explicit affordances.

## Security callouts
- Protect every persistence/file endpoint against path traversal, CSRF, and confused-deputy writes.
- Redact tokens, refresh secrets, and claims from logs, ProblemDetails, and test artifacts.
- Validate filenames, extensions, and sandbox roots before any disk operation; never echo raw absolute paths back to the browser.

## Rollback / feature-flag guidance
- User-visible behavior introduced by Stage 7.1 should ship behind a reversible flag or rollout switch (for example `WorkflowUi:Features:Stage71` on the host, or a client capability flag with the same suffix).
- Rollback must prefer configuration/flag disablement first, then artifact/package revert, and only then schema/fixture rollback if persistence formats changed.

## Manual verification focus
- Log in, expire the token/session intentionally, and confirm recovery or redirect behavior.
- Save/open a workflow from nested folders and verify traversal attempts are blocked with the documented error.
- Simulate two tabs editing the same workflow and confirm the stale-write guidance is visible.

---

### Step 1 — JWT bearer scaffold + dev-mode bypass (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Wire `AddAuthentication().AddJwtBearer()` into the host, set `FallbackPolicy =
RequireAuthenticatedUser`, and add a `DevBypassAuthenticationHandler` schema gated by
environment + configuration. Every `/api/*` and `/hubs/*` route responds 401 Unauthorized when
no token is presented (in prod). In Dev bypass, requests are auto-authenticated as a synthetic
`dev-user` principal with both `Editor` and `Viewer` roles for local iteration.

### 4. Depends on
- Step 1.1.3

### 5. Inputs
- Host Program.cs.
- `AuthOptions` section in `appsettings.json`.

### 6. Deliverables
- `AuthOptions.cs`:
  ```csharp
  public class AuthOptions {
      public string Authority { get; set; } = "";
      public string Audience  { get; set; } = "";
      public bool   AllowDevBypass { get; set; }
      public string JwksUrl   { get; set; } = "";
      public int    ClockSkewSeconds { get; set; } = 30;
  }
  ```
- `DevBypassAuthenticationHandler` — custom `AuthenticationHandler<AuthenticationSchemeOptions>`
  that returns success with a synthetic `ClaimsPrincipal` only when env=Dev AND
  `AllowDevBypass=true` AND request is from loopback.
- `AuthServiceCollectionExtensions.AddWorkflowUiAuth(cfg)` — wires JWT + dev bypass as two schemes,
  sets `FallbackPolicy`, logs dev warning on startup.
- Unit tests + WebApplicationFactory 401 integration test.

### 7. Acceptance criteria
- [ ] Unauthenticated `/api/runs` returns 401 in non-Dev env.
- [ ] In Dev with `AllowDevBypass=true` and loopback request, `/api/runs` returns 200 with
      `User.Identity.IsAuthenticated == true`.
- [ ] Non-loopback request (e.g., `127.0.0.2` vs `127.0.0.1`) in Dev STILL returns 401.
- [ ] Startup log contains warning `"DEV BYPASS ACTIVE"` when bypass is on.
- [ ] JWT with expired `exp` returns 401 (clock skew honoured).
- [ ] JWT with wrong `aud` returns 401.
- [ ] SignalR hub `/hubs/telemetry` requires auth (rejected `HubException` on connect without
      token).
- [ ] Metrics counter `auth_challenges_total{scheme}` increments on 401.

### 8. Unit tests
1. `DevBypass_EnvProd_NotAllowed`.
2. `DevBypass_EnvDev_AllowFlagFalse_NotAllowed`.
3. `DevBypass_EnvDev_AllowFlagTrue_Loopback_Allowed`.
4. `DevBypass_EnvDev_AllowFlagTrue_NonLoopback_Denied`.
5. `DevBypass_PrincipalHas_EditorAndViewer_Roles`.
6. `Jwt_ExpiredToken_Rejected`.
7. `Jwt_WrongAudience_Rejected`.
8. `Jwt_ValidToken_ProducesPrincipalWithSubClaim`.
9. `StartupLog_WarnsOnDevBypass`.

Three AAA sketches:

```csharp
// #3 DevBypass_EnvDev_AllowFlagTrue_Loopback_Allowed
var opts = new AuthOptions { AllowDevBypass = true };
var env  = new FakeEnv("Development");
var ctx  = new DefaultHttpContext();
ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
var handler = CreateHandler(opts, env);
await handler.InitializeAsync(new AuthenticationScheme("dev",null,typeof(DevBypassAuthenticationHandler)), ctx);
// Act
var result = await handler.AuthenticateAsync();
// Assert
result.Succeeded.Should().BeTrue();
result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
result.Principal.IsInRole("Editor").Should().BeTrue();
result.Principal.IsInRole("Viewer").Should().BeTrue();

// #4 DevBypass_EnvDev_AllowFlagTrue_NonLoopback_Denied
ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
var result = await handler.AuthenticateAsync();
result.Succeeded.Should().BeFalse();

// #6 Jwt_ExpiredToken_Rejected
var token = TestJwtFactory.Sign(sub:"u1", roles:new[]{"Editor"}, exp: DateTime.UtcNow.AddMinutes(-5));
using var factory = new HostFactoryBuilder().Build();
var client = factory.CreateClient();
client.DefaultRequestHeaders.Authorization = new("Bearer", token);
var resp = await client.GetAsync("/api/runs");
resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
```

### 9. Integration tests
- `UnauthenticatedTests.AllEndpoints_Return401_WithoutToken` — iterate over `/api/workflows`,
  `/api/runs`, `/api/files`, `/hubs/telemetry/negotiate`.
- `UnauthenticatedTests.Hub_Rejects_AnonymousConnection`.

### 10. Golden-file / parity tests
- `AuthOptions.json` config shape golden (matches appsettings schema).

### 11. Edge cases & negative tests
- Token with `nbf` in the future (not yet valid): 401.
- Missing `Authorization` header: 401.
- Malformed header (`Bearer`): 401, never 500.
- JWKS endpoint unreachable on startup: host logs warning but starts (lazy key refresh).
- Two schemes both succeed (dev bypass + JWT): prefer dev bypass, principal merges claims.
- Very large token (> 8 KB): rejected with 413 or 401 per Kestrel default.

### 12. Observability hooks
- Metric `auth_challenges_total{scheme, reason}`.
- Log `"Auth challenge: {Reason}"` at Information level.
- Activity `auth.validate_token` with `token.scheme`, `token.sub`.

### 13. Risks & mitigations
- **R7.1.1-A — dev bypass leaking to prod**: triple-gated (env + flag + loopback) plus startup
  warning; integration test asserts non-loopback denial.
- **R7.1.1-B — clock skew between host and IdP**: configurable `ClockSkewSeconds`, default 30 s.

### 14. Evaluator gap-analysis prompts
1. Is dev bypass gated by env AND flag AND loopback?
2. Does the host startup log warn on bypass?
3. Are all `/api/*` and `/hubs/*` routes covered by 401?
4. Is `FallbackPolicy` actually applied?
5. Does the hub authorize on connect?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, 401-on-everything test green, dev bypass triple-gate test green.

---

### Step 2 — OIDC integration (3 d)

### 1. Status
`[ ]`

### 2. Effort
3 engineering days.

### 3. Goal
Add OIDC Authorization Code + PKCE flow to the host so browser users can log in with an upstream
IdP (Azure AD default, any generic OIDC provider configurable). Configure `AddOpenIdConnect` with
`ResponseType=code`, `UsePkce=true`, `SaveTokens=true`. UI bootstrap checks for a session cookie;
if absent, redirects to `/auth/signin` which triggers OIDC challenge. The JWT from IdP is
exchanged for a short-lived API bearer token surfaced to the SPA via `/auth/token` endpoint
(HTTP-only cookie stores the refresh token).

### 4. Depends on
- Step 7.1.1

### 5. Inputs
- `AuthOptions.Authority`, `Audience`, `ClientId`, `ClientSecret`.
- `mock-oauth2-server` fixture for tests.

### 6. Deliverables
- Host program updates to register OIDC scheme.
- `AuthController.SignIn`, `SignOut`, `Token` endpoints.
- `AuthProvider.tsx` — React context managing `user`, `accessToken`, `signIn()`, `signOut()`.
- Playwright spec using mock-oauth2-server container.

### 7. Acceptance criteria
- [ ] `/auth/signin` issues 302 to IdP authorize endpoint with PKCE challenge.
- [ ] Callback `/auth/callback` exchanges code for tokens and sets session cookie.
- [ ] `/auth/token` returns 200 with `{ accessToken, expiresIn }` when session valid; 401 else.
- [ ] `/auth/signout` clears session and redirects to IdP end-session endpoint.
- [ ] OIDC discovery document cached 24 h with ETag revalidation.
- [ ] PKCE verifier NOT logged.
- [ ] SPA boot auto-redirects on 401 from token endpoint.

### 8. Unit tests
1. `SignIn_GeneratesPkceVerifier_AndRedirects`.
2. `Callback_ExchangesCode_AndSetsCookie`.
3. `Callback_InvalidState_Rejected`.
4. `Token_NoSession_Returns401`.
5. `Token_ValidSession_ReturnsAccessToken`.
6. `SignOut_ClearsCookie`.
7. `Discovery_CachedWithETag`.
8. `PkceVerifier_NotEmitted_InLogs` (assertion on Serilog sink).

Three sketches elided for brevity but follow the pattern of `HttpContext` + `DefaultHttpContext`
arrange, Act via controller method, Assert on `StatusCode` + cookies.

### 9. Integration tests
- `OidcFlowTests.SignIn_Redirects_Callback_SetsCookie_TokenReturns200` — full container-based
  flow against mock-oauth2-server.
- Playwright `auth-oidc-mock.spec.ts`: launch dev server + mock IdP, click `Sign In`, complete
  form, assert dashboard renders with user pill.

### 10. Golden-file / parity tests
- `TokenResponse.json` shape golden.

### 11. Edge cases & negative tests
- Callback with tampered state: 400.
- IdP returns `error=access_denied`: UI shows user-friendly error.
- Refresh token expired: next `/auth/token` returns 401, SPA redirects to sign-in.
- Network partition during callback: user-friendly retry screen.
- Multiple concurrent sign-ins in different tabs: last-wins; earlier tabs still have valid
  session cookie (shared origin).

### 12. Observability hooks
- Metric `auth_signin_total{result}`.
- Activity `auth.signin`, `auth.callback`, `auth.refresh` with `provider` attribute.

### 13. Risks & mitigations
- **R7.1.2-A — provider lock-in**: use `AddOpenIdConnect` generic library, no hard `Identity.Web`
  calls in domain layer.
- **R7.1.2-B — refresh token theft via XSS**: refresh stored HTTP-only `SameSite=Strict`;
  access token short-lived (≤ 15 min).

### 14. Evaluator gap-analysis prompts
1. Is PKCE actually enabled?
2. Is refresh token NEVER surfaced to JS?
3. Are cookies marked `HttpOnly`, `Secure`, `SameSite=Strict`?
4. Is the discovery document cached with revalidation?
5. Does the SPA handle 401-on-token by redirecting without loops?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, Playwright OIDC spec green, integration flow green.

---

### Step 3 — Role-based policies (`Editor`, `Viewer`) (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Define two authorization policies: `Editor` (PUT/POST/DELETE allowed) and `Viewer` (GET-only).
Role claims arrive from the IdP under `roles` (Azure AD) or `groups` — `RoleClaimsTransformer`
normalises both into the standard `ClaimTypes.Role` claim. Apply `[Authorize(Policy="Editor")]`
to write endpoints on `WorkflowsController`, `RunsController`, and the Files API (Stage 7.3);
`[Authorize(Policy="Viewer")]` on reads.

### 4. Depends on
- Step 7.1.2

### 5. Inputs
- Claims from JWT principal.
- Enum `ApiRole { Editor, Viewer }`.

### 6. Deliverables
- `AuthPolicies.cs` static class with constants + `AddWorkflowUiPolicies(builder)`.
- `RoleClaimsTransformer` implementing `IClaimsTransformation`.
- `PolicyTests.cs` + `RoleEnforcementTests.cs` integration.

### 7. Acceptance criteria
- [ ] `Viewer` token gets 403 on `PUT /api/workflows/{id}`.
- [ ] `Editor` token gets 200/201 on same.
- [ ] Token with BOTH roles acts as Editor (higher privilege).
- [ ] Token with neither role → 403 on everything except `/auth/*` and `GET /api/health`.
- [ ] `ClaimTypes.Role` populated from both `roles` and `groups` claim sources.
- [ ] Hub connection with Viewer role allowed to attach (read-only); no hub method to mutate.

### 8. Unit tests
1. `Transformer_MapsRolesClaim_ToRoleClaimType`.
2. `Transformer_MapsGroupsClaim_ToRoleClaimType`.
3. `Transformer_NoClaims_ReturnsOriginal`.
4. `EditorPolicy_Requires_EditorRole`.
5. `ViewerPolicy_Requires_ViewerOrEditor`.
6. `PolicyAuthorization_WithEditorToken_Passes`.
7. `PolicyAuthorization_WithViewerToken_OnWriteEndpoint_Fails`.
8. `PolicyAuthorization_WithNoRoles_Fails`.

Three AAA sketches elided (standard policy tests via `IAuthorizationService`).

### 9. Integration tests
- `RoleEnforcementTests.Viewer_PUT_Returns403`.
- `RoleEnforcementTests.Editor_PUT_Returns200`.
- `RoleEnforcementTests.NoRole_GET_Returns403`.

### 10. Golden-file / parity tests
- `authPolicies.golden.json` — pinned role → permitted HTTP verbs matrix.

### 11. Edge cases & negative tests
- Role claim value case-insensitive match (`editor` vs `Editor`).
- Role claim malformed JSON array: parsed defensively.
- Token rotation mid-session: next request re-transforms claims (idempotent).
- Policy misconfigured on endpoint (no `[Authorize]`): caught by a `StartupValidation` test that
  scans controllers for missing attributes.
- Hub method invoked by Viewer for writeable action (if any added later): denied with
  `HubException`.

### 12. Observability hooks
- Metric `auth_policy_denials_total{policy, endpoint}`.
- Log `"Policy {Policy} denied for user {UserId} on {Endpoint}"`.

### 13. Risks & mitigations
- **R7.1.3-A — over-privilege**: `Editor` implies `Viewer` by policy composition, not by claim
  duplication.
- **R7.1.3-B — missing attribute**: startup validation scan.

### 14. Evaluator gap-analysis prompts
1. Does `Editor` imply `Viewer` without requiring both claims?
2. Are `roles` and `groups` both normalised?
3. Is every write endpoint attributed?
4. Does the startup scan fail fast on missing attributes?
5. Is case-insensitive matching explicit?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, role enforcement integration tests green.

---

### Step 4 — Audit log of write endpoints (2 d)

### 1. Status
`[ ]`

### 2. Effort
2 engineering days.

### 3. Goal
Emit one `AuditLogEntry` per write request (PUT/POST/DELETE/PATCH on `/api/*`). Entries are
written to a Serilog sink backed by a **JSONL file** (one line per entry, rotated daily) and also
to an in-memory ring (last 10k) for the `GET /api/admin/audit` endpoint (Editor-only). PII is
restricted to the principal id (`sub` claim) — emails, names, and claims are NOT logged. Targets
(workflow path, etag) are canonicalised before logging.

### 4. Depends on
- Step 7.1.3

### 5. Inputs
- ASP.NET Core request pipeline.
- Writable middleware slot.

### 6. Deliverables
- `AuditLogEntry.cs`:
  ```csharp
  public record AuditLogEntry(
      DateTime TimestampUtc,
      string UserId,
      string Method,          // PUT / POST / DELETE / PATCH
      string Path,
      string? ETag,           // request-supplied or computed
      int    StatusCode,
      string CorrelationId);
  ```
- `AuditMiddleware` — runs AFTER auth, BEFORE endpoint; captures method/path; writes entry on
  response completion.
- `AuditLog.cs` — sink wrapper with JSONL file + in-memory ring.
- `AuditLogTests.cs`, `AuditEmissionTests.cs`.

### 7. Acceptance criteria
- [ ] Every PUT/POST/DELETE/PATCH produces exactly one entry.
- [ ] GET requests produce zero entries.
- [ ] `UserId` is `sub` claim or `"anonymous"` for dev bypass loopback.
- [ ] `Path` normalised (query string stripped).
- [ ] `ETag` captured from request `If-Match` header when present.
- [ ] Entries include `CorrelationId` (W3C `traceparent` or generated `Guid.NewGuid()`).
- [ ] JSONL file rotates daily (`audit-yyyyMMdd.jsonl`).
- [ ] In-memory ring caps at 10 000 entries.

### 8. Unit tests
1. `Middleware_Put_ProducesEntry`.
2. `Middleware_Get_ProducesNoEntry`.
3. `Middleware_UnauthenticatedAnonymous_UserIdAnonymous`.
4. `Entry_ETagFromIfMatchHeader`.
5. `Entry_PathNormalisation_StripsQueryString`.
6. `Ring_OverCapacity_EvictsOldest`.
7. `JsonlFile_WritesValidJsonPerLine`.
8. `PiiFields_NotEmitted` (scan output for email/name keys).

Three AAA sketches:

```csharp
// #1 Middleware_Put_ProducesEntry
var log = new InMemoryAuditLog();
using var factory = new HostFactoryBuilder().WithAuditLog(log).Build();
var client = factory.CreateClient();
client.DefaultRequestHeaders.Authorization = TestJwtFactory.EditorBearer();
// Act
var resp = await client.PutAsync("/api/workflows/foo", new StringContent("yaml"));
// Assert
log.Entries.Should().ContainSingle()
    .Which.Should().BeEquivalentTo(new {
        Method="PUT", Path="/api/workflows/foo", StatusCode=(int)resp.StatusCode
    }, o => o.ExcludingMissingMembers());

// #5 Entry_PathNormalisation_StripsQueryString
await client.DeleteAsync("/api/workflows/foo?force=true");
log.Entries.Last().Path.Should().Be("/api/workflows/foo");

// #8 PiiFields_NotEmitted
await client.PutAsync("/api/workflows/foo", new StringContent("yaml"));
var json = File.ReadAllText(log.JsonlPath);
json.Should().NotContain("email").And.NotContain("preferred_username");
```

### 9. Integration tests
- `AuditEmissionTests.DeleteFlow_GeneratesJsonlLine`.
- `AuditEmissionTests.AdminAuditEndpoint_ReturnsRing`.

### 10. Golden-file / parity tests
- `AuditLogEntry.json` shape golden with exactly 7 keys; schema validated per entry.

### 11. Edge cases & negative tests
- Write request that fails auth (401) still produces audit entry with `UserId=anonymous`,
  `StatusCode=401`.
- Very large request body: entry does NOT include body (metadata only).
- JSONL file disk full: middleware logs error but does not throw (best-effort).
- Correlation id propagated from upstream `traceparent` header if present.
- Admin endpoint denies non-Editor with 403.

### 12. Observability hooks
- Metric `audit_entries_total{method, status}`.
- Log `"Audit {Method} {Path} by {UserId} → {Status}"` at Information.

### 13. Risks & mitigations
- **R7.1.4-A — PII leakage**: explicit allow-list of fields (7 keys), compile-time record type.
- **R7.1.4-B — disk fill**: daily rotation + Stage 7.2.4 cleanup.

### 14. Evaluator gap-analysis prompts
1. Is PII truly absent from the entry record (compile-time enforced)?
2. Are read (GET) requests correctly excluded?
3. Does daily rotation actually rotate at midnight UTC?
4. Is the admin endpoint role-gated?
5. Is `CorrelationId` joined with request-scoped logs?

### 15. Scoring rubric
25/25/20/10/10/10.

### 16. Definition of done
Evaluator ≥ 90, PII-absence test green, JSONL shape golden green.
