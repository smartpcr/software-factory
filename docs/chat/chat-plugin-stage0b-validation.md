# Stage 0B implementation and validation

Implemented on 2026-09-09. This is the identity prerequisite for chat plugins;
Slack, plugin connection/subscription storage, and the workstream Chat panel remain
in their later stages. No live Forge host was restarted, no provider messages were
sent, and no commit or push was made.

## Requirement review

| Requirement | Implementation evidence | Verification |
| --- | --- | --- |
| Explicit host enrollment and browser pairing | `Auth/HostOperatorCommand.cs`, `HostIdentityStore.cs`; stable `UserRow.Id`, DPAPI CurrentUser store, 256-bit challenges stored as hashes, two-minute single-use redemption. CLI exits before web host startup. | `HostIdentityTests`: encrypted storage, hashes, expiry, purpose/sender binding, concurrent redemption. `OperatorHttpSecurityTests`: HTTP cannot bootstrap an administrator. |
| Authenticated browser boundary | `OperatorAuthentication.cs`, `OperatorSessions.cs`; cookie authentication plus ASP.NET authorization, HttpOnly/SameSite Strict, eight-hour maximum, no sliding renewal, in-memory session registry, trusted loopback Host/Origin, CSRF before mutations. | Real cookie pairing, cookie attributes, logout/revocation, forged headers, missing/bad Origin, bad Host, missing CSRF, deleted users, restart/expiry session checks. UI request tests cover credentials, shared CSRF acquisition, pairing and session loss. |
| Fixed capabilities and current target authorization | `ForgeCapabilities.cs`; immutable capability/role sets, resolver-issued actors, current enrollment/link/session and database-user checks, explicit product roles, repository/spawn/child/artifact ownership. | `CapabilityParityTests`: all 17 capabilities plus unknown capability, all seven fixed roles plus missing/unknown role, with/without host-admin authority, through real cookie HTTP and direct provider invocation. Cross-product, unresolved/conflicting target and revoked-role/link cases reject. |
| Endpoint and common-service coverage | `ForgeEndpointCapabilities.Supported` inventories 22 action/link/role endpoints and aliases. Middleware checks precede idempotency and audit. Operator and question services check again; identity routes, link issuance and test-send check inside their boundary. | Runtime endpoint metadata inventory; each registered action alias denies missing authority before task-queue/subscriber writes. Shared services deny absent actors. Scope tests cover role changes, cross-product route aliases and cached-success replay after revocation. |
| Shared spawn/artifact domain behavior | `Services/StoryOperatorActionService.cs`; seven extracted pause/resume and artifact cores, thin HTTP wrappers, verified provider actor scopes. Pause now also persists released control-DB claims. | Web/provider pause-resume and cancel/accept/retry domain parity, plus migrated existing resume/restart recovery tests. Artifact target validation includes the four plan documents and `qa-e2e`. |
| Interactive plugin gate | `ChatPluginInteractiveGate`, `/api/chat/availability`; plugin interactive mode remains unavailable. Legacy remote actions still require both host-local switches and the new identity boundary. | Gate test and existing 32 notification-only guard inputs; explicit legacy chat tests now enroll and link their operators. |

## Validation results and limits

- Focused backend selection: **139 passed, zero failed or skipped**.
- UI: **175 passed** across 19 files; TypeScript project build passed.
- API and test project compile successfully with existing warnings, including
  Windows-platform DPAPI annotations.
- The broader API suite was **stopped, not passed**. It contains legacy fixtures
  using anonymous `http://localhost` clients, header-only writers and unowned
  targets; these require migration to paired principals and explicit product
  ownership under the new contract. The run also exposed an existing unrelated
  `ImplSpawnGuardTests` constants mismatch (expected 1800, actual 300).
  Remaining full-suite fixture migration is a validation limitation; the full
  repository suite is not claimed green.
- The broad run rewrote the tracked model catalog; that generated change was
  restored to its pre-run contents. Test identity stores and database folders
  were isolated from the live host.
- No live browser pairing, clipboard command, provider transport round-trip, or
  service restart was performed. The pairing HTTP flow uses the real cookie and
  anti-forgery middleware in TestServer; DPAPI is exercised on Windows.

Reproduce the focused backend checks without touching the live host's output:

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow-chat-stage0b-check-artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Phase41ResumeRecoveryTests|FullyQualifiedName~Phase50RestartFromScratchTests'
```

Run `npm test` and `npx tsc -b --pretty false` in `src/forge/Forge.UI` for UI checks.

## Host operator setup

After building and when the operator chooses to restart Forge, use the API
executable's local command mode. Substitute the host's actual build output,
database folder, stable user ID and product ID:

```powershell
& <path-to-Forge.Api.exe> --operator enroll --user-id alice --host-admin --db-folder E:\forge
& <path-to-Forge.Api.exe> --operator grant-role --user-id alice --product-id <product-id> --role product-owner --db-folder E:\forge
& <path-to-Forge.Api.exe> --operator pair --user-id alice --db-folder E:\forge
```

`pair` copies a one-use challenge to the clipboard without printing it or putting
it in process arguments. Paste it into the browser's Pair browser form within two
minutes. Use **Check again** after enrollment. **Settings → Browser session →
Unpair this browser** revokes that session. Host-local
`--operator revoke --user-id alice --db-folder E:\forge` revokes enrollment,
all sessions and links. Re-enrolling also invalidates previous credentials.

The identity store defaults to `%LOCALAPPDATA%\Forge\operator-identities.bin`.
`Forge:Identity:StorePath` or `FORGE_IDENTITY_STORE_PATH` can select another absolute
path; use the same store for the CLI and host. Run both as the same Windows user.
Existing Forge users, global `UserRow.Role`, `BootstrapPoEmail`, `X-Forge-User`,
provider OAuth login and Telegram allow-lists do not enroll an operator or grant
product authority. Grant a product role explicitly for each product. Host admins
can manage identity and connections but do not implicitly own products.

The supported deployment remains loopback-only: API port 5180 and browser port
5181 on localhost/127.0.0.1/[::1]. Cookies use Secure when served over HTTPS;
loopback HTTP remains supported. Custom origins must be explicitly configured as
loopback origins in `Forge:Identity:ApiOrigins` and `BrowserOrigins`.

The legacy SignalR feed mixes host and product payloads. Access therefore requires
a host administrator with explicit membership in every product, checked again
before delivery and subscription. Operators with narrower access use the scoped
HTTP surfaces. Per-product realtime payloads would permit relaxing this restriction
in a later change; unlabelled global broadcasts are not exposed to partial grants.

For test migrations use `OperatorTestIdentity.EnrollAsync`, `PairedClientAsync`,
`SeedProductAsync` and explicit repository/parent relationships. `AnonymousClient`
is the fixture for denial tests. Do not restore header-based authority or an
empty-users bypass to make older tests pass.


## Automatic localhost sign-in follow-up (2026-09-09)

Opening the localhost UI now signs into the single existing enrolled account
without copying a code. The browser calls the API directly; Windows verifies the
client socket's process owner against the host's Windows identity. Vite marks
proxied requests so they cannot borrow its identity. The normal cookie, CSRF,
Origin, enrollment, revocation and product-role checks remain in force. Explicit
sign-out persists as a browser preference, and manual sign-in/pairing remains available.

Focused checks: 21 backend tests pass (including real IPv4/IPv6 socket ownership
and local sign-in denial cases); 13 browser request tests pass; TypeScript build
passes. This supersedes the launch-only workaround, which did not sign in a browser
opened independently of the launcher. No new account or role migration is required
for an already enrolled host account.


Live verification after the authorized redeploy: a fresh, isolated Chrome browser
context opened `http://localhost:5181/workstreams` with no cookies, code or launch
fragment. Automatic local sign-in returned HTTP 200 for the existing enrolled
account; Workstreams rendered with no page errors. Screenshot:
`E:/work/github/crp/workflow-local-sign-in.png`. Remote browser sign-in remains
outside the current loopback-only deployment.
