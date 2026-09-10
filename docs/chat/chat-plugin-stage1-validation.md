# Stage 1 implementation and validation

Implemented on 2026-09-09 against `a7e93fe8`. All seven Stage 1 work items and
their acceptance criteria in [the implementation plan](chat-plugin-implementation-plan.md)
are complete. Stage 1S and later stages remain unchecked. No live host restart,
provider message, git commit or push was performed.

## Implementation review

- Total Stage 1 tasks: **7**.
- Complete: **7**; partial: **0**; missing: **0** within Stage 1.
- Validation: **194 passed, zero failed or skipped**, including **26 new checks**.
- One existing migration-catalog test failed in the baseline and is excluded from
  that passing selection. The full repository suite is not claimed green.

| Task | Status | Implementation | Verification |
| --- | --- | --- | --- |
| 1. Minimal versioned workstream plugin catalog | [x] Complete | `Plugins/WorkstreamPluginRegistry.cs` defines descriptors and `IWorkstreamPlugin`; `ChatWorkstreamPlugin` registers `chat` with `story-spawn` scope and settings version 1. | Duplicate plugin IDs disable the registry; unsupported settings remain saved and paused. |
| 2. Provider/session/inbound/secret/message contracts | [x] Complete | `Common.Chat/ChatProviderContracts.cs`, `ChatProviderRegistry.cs`; opaque string IDs, semantic actions, destination/address/message correlation, durable inbound acceptance and credential-reference seams. | Shared `ChatProviderConformanceTests` exercises both fake providers; persisted subscriptions resolve independently through two `FakeAlpha` connections and one `FakeBeta` connection. |
| 3. Independent provider capabilities | [x] Complete | Descriptors advertise reception, text/buttons/edits, topics/threads, discovery, local/remote retirement and character/byte limits. | Parameterized fake fixtures check supported/unsupported operations, paginated discovery, conversation reuse/retirement, send/edit correlation, Unicode limits, safe rendering, retry classification, cancellation and receipt deduplication semantics. |
| 4. Six control-DB tables and registrations | [x] Complete | `ChatConfigurationEntities`, `ChatConfigurationModel`, `Phase98Migration`, `ControlDbContext`, `DbModelControl`, `PhaseMigrationCatalog`, `PhaseMigrationRunner`, `Split/DbBoundary`. | Fresh and upgraded schemas accept the same data and reject invalid scopes/duplicates; rerunning Phase98 preserves data. Actual split dry-run classifies every new table as control/global with no unknown-table or orphan warning; neither product DB contains these tables. |
| 5. Scoped CRUD, revisions and parent lifecycle | [x] Complete | `ChatConfigurationService*.cs`, configuration DTOs and `ChatPluginEndpoints`; revision concurrency tokens, atomic control transactions, current capabilities/grants, immutable destination identities, default-off parents, run fingerprint, activation generation and explicit copy origin. | Real paired HTTP reads/writes/deletes, stale/CAS conflict checks, duplicate rejection, role/grant revocation, cross-product denial, reused spawn IDs, rename/phase changes, 32 children plus a future child, disabled copies with fresh identities. |
| 6. Permanent Host alerts scope | [x] Complete | Stable configured HostId; canonical non-null hex-encoded scope keys and SQLite scope checks. Host setup is independent of any product or parent. | Host admins alone read/configure recipients; host defaults select `queue-strain` and `human-interaction`, notification-only. Missing/removed destinations return `host-alerts-need-destination`. Mixed/null/incomplete scope fields reject, including Unicode/delimiter cases. |
| 7. Host switch and engine selection | [x] Complete | `ChatPluginOptions`, `ChatPluginRegistration` and tracked `Forge:Chat` defaults; both legacy option bindings use the same gate. | Seven engine/master/legacy-opt-in combinations. An invalid engine disables chat without failing host boot. Duplicate registries leave Forge health available. |

Source paths in this table are relative to `src/forge/Forge.Api`, except shared
contracts under `src/Common.Chat`. Tests live in `src/forge/Forge.Api.Tests/Chat`:
`ChatPluginConfigurationTests`, `ChatPluginSchemaTests`, `ChatPluginEngineTests`
and `ChatProviderConformanceTests`.

## Configuration and API behavior

`Forge:Chat:Engine` defaults to `legacy`; `Forge:Chat:HostId` defaults to the stable
host-local identity `local`. An operator can set another stable HostId in the same
host-user JSON file used by the legacy bridge. Do not change it after configuring
Host alerts. `Forge:Chat:Enabled` is deliberately absent in tracked defaults:

- Absent: preserve the existing explicit `Forge:ChatBridge:Enabled` opt-in for
  the legacy engine. A clean checkout still has chat disabled.
- `false`: disable both engines.
- `true` with `legacy`: allow the legacy engine only if its existing opt-in is true.
- `true` with `plugins`: disable both legacy pumps. Stage 1 saves configuration
  but returns `plugin-delivery-not-implemented`; it does not start a receiver/send.

Host JSON/environment changes load at host startup as before. Configuration CRUD
is persisted immediately and requires no agent restart. This change does not edit
the user's host settings or change the running process's selected engine.

New endpoints require the existing paired cookie, trusted Origin and CSRF token
for mutations. Capability metadata is checked before idempotency/audit middleware,
and services independently recheck authority:

| API | Purpose and authority |
| --- | --- |
| `GET /api/plugins`, `GET /api/chat/providers` | Installed descriptors and registry diagnostics; authenticated enrolled operator. Production provider registrations are empty until the adapters land. |
| `GET /api/chat/configuration` | Effective host switches and interactive availability; host admin. |
| `GET /api/chat/connections?productId={id}` | Safe summaries for connections granted to a product the operator can read. Omit `productId` for a host-admin list. |
| `GET/PUT/DELETE /api/chat/connections/{id}` | Host-admin configuration, including explicit product grants and credential references. Details return the grants needed for editing; secret values never appear. |
| `GET /api/chat/connections/{connectionId}/destinations?productId={id}` | Granted destination metadata; host admin when no product is supplied. |
| `PUT/DELETE /api/chat/connections/{connectionId}/destinations/{id}` | Host-admin destination configuration. New destinations are `unverified`; native provider validation is later work. |
| `GET/PUT /api/products/{productId}/story-spawns/{spawnId}/plugins/chat` | Read with product membership; configure with that product's owner capability. |
| Same parent prefix plus `/subscriptions/{id}` (`PUT/DELETE`) | Add/update/retire a destination subscription, using current parent and subscription revisions. |
| `GET /api/chat/host/subscriptions`, `PUT/DELETE .../{id}` | Permanent Host alerts setup, protected by `host.alert.manage`. |

Use `ExpectedRevision=0` for creation and the returned current revision for updates.
Connection PUT replaces its product-grant set atomically. Subscription mutations
also require `ParentRevision` and advance the parent revision; reread the parent
before the next edit. DELETE takes `expectedRevision` in the query, plus
`parentRevision` for a spawn subscription. Removed connections/destinations and
subscriptions are tombstoned; bindings are retired locally rather than retargeted.

Version 1 parent settings are `{}`. Destinations and filters live in subscriptions.
Policies are `flat`, `topic` or `thread`, constrained by the provider descriptor.
Spawn filters default to `open-questions` and `workstream-stalled`; Host alerts
default to the two host kinds. Unknown kinds/catalogs and interactive mode reject.
Unsupported saved provider/connection/parent settings versions pause their affected
instances and retain data for repair.

Parent run identity derives from the source spawn's creation timestamp, repo and
story. The source spawn endpoint stamps a new creation timestamp for each run.
Reads of a reused ID return Off and no inherited subscriptions. Explicit
`ActivateNewRun=true` while enabling the replacement advances its generation and
retires the old subscriptions/bindings. Explicit copies start disabled, preserve
the source scope as `CopyOrigin`, and create fresh subscription IDs; copying an
inaccessible destination rejects the operation rather than partially copying.

## Verification and remaining boundaries

Reproduce without touching the running API's binaries:

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage1/artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)' `
  --logger 'trx;LogFileName=stage1-final.trx' `
  --results-directory E:/work/github/crp/workflow/.tmp-build/chat-stage1/results
```

The baseline selection also included `Catalog_ClassificationMatchesDbBoundary`:
**168 passed, one failed** before Stage 1 edits. Its failure says Phase83 touches
product table `repos` while `DbBoundary` classifies `repos` as control. The existing
split design deliberately uses a separate `RepoMoveToProductService` for that
move; changing the splitter classification belongs to its own migration review.
The new six-table checks independently verify their actual boundaries. Broader
legacy fixture migration remains documented in the Stage 0B validation report.

The new tests set `FORGE_TEST_GUARDRAIL`, use temporary identity stores/databases,
and keep hosted jobs disabled. The conformance fixtures use in-process fake
sessions; they do not prove real Telegram/Slack rendering, networking or delivery.
Those adapters will join the shared suite with fake HTTP/WebSocket peers in
Stages 3/4. No exactly-once network delivery claim is made.

Correctness issues found and fixed during this implementation: EF span-overload
query evaluation, nested subscription route IDs being mistaken for product IDs,
partial copies after a connection grant is revoked, and omitted product grants
in administrative connection reads. Focused regressions cover each fix.

No Stage 1 gap remains in the reviewed requirements. Session supervision,
credential-store implementations, remote validation/provisioning, durable
delivery/inbox/action processing, producer routing, global deletion/reconciliation
and row/settings UI remain assigned to Stage 1S and later stages. Backend changes
become available to the running host only after an operator-chosen restart.