# Chat plugin Stage 6 validation

Completed 2026-09-10 against baseline `cc774f96` (`chat plugin: stage 5`).
All eleven Stage 6 implementation requirements are complete: **11 complete,
0 partial, 0 missing**. Real-provider gates and an actual operator cutover remain
separate. See the [migration guide](chat-plugin-migration.md) for commands and limits.

| # | Status | Requirement and evidence |
|---|---|---|
| 1 | [x] | `ChatMigrationService.PreviewAsync` reads options, references, counts, cursor, confirmed links and verified scopes. The read-only preview test proves no configuration writes or provider calls. |
| 2 | [x] | `ImportAsync`, stable journal keys and Phase 106 create `legacy-telegram`. Repeat and injected failure tests cover atomic retries; the DPAPI test checks original blob bytes and a reference-only import. |
| 3 | [x] | `RouteAsync` imports the legacy global destination into Host alerts. The compatibility retirement test proves new host events still deliver after old endpoints retire. |
| 4 | [x] | `BackfillAsync` creates one delivery per original notification and preserves known message anchors. Tests cover pending, sent, failed, resolving, answered, acted, expired and superseded states and timestamps. Pending multi-question alerts use current question parts within that single delivery; unsent parts can return to the original legacy row. |
| 5 | [x] | `ImportCoreAsync`, `OwnedAsync` and `ChatScopeResolver` verify product/parent, child and artifact ownership, normalize product IDs and retain topic addresses. Fixtures cover inherited children, artifact questions, ambiguous topics and reused parent IDs. Unverified messages are held for repair under Host attention. |
| 6 | [x] | `CutoverAsync` takes explicit product/spawn choices. The selected-parent test covers inherited future children, another disabled parent, and one subscription per parent. |
| 7 | [x] | `ChatEngineCoordinator` fences both pumps, direct transport operations and plugin work. `ChatEngineNotifier` shares the final routing gate. Blocked HTTP fixtures prove final legacy send/cursor capture, durable events during drain, no shared poll owner, and plugin receiver shutdown before rollback. Transaction faults preserve the prior owner. Network disconnects/missing receipts stay uncertain and never become fresh retries. |
| 8 | [x] | `ChatMigratedIdentity` retains confirmed link proofs with immediate revocation. Inbox dispatch recognizes old `act`, `oq`, host acknowledgements and original replies; exact address, target, choice label and current revision checks precede shared domain operations. Tests cover wrong channel/target, multiple questions, revoked/replaced links, reused question revisions and stale workstream controls. |
| 9 | [x] | `ChatMigrationCompatibility` and `ChatBridgeEndpoints` wrap the selected imported connection. HTTP tests cover default supergroup, explicit other destination, unknown destination, message-ID response, bot-scoped linking, no-store responses, revocation and retirement. Host delivery remains active after retirement. |
| 10 | [x] | Startup reads the durable migration owner before either engine runs. Source settings come from the existing Stage 0A host loader; the launcher exporter remains removed. DPAPI/rotation/restart fixtures and `ChatHostConfigurationTests` verify this path. The new PowerShell helper executes authenticated, revision-checked operations without patching files or restarting services. |
| 11 | [x] | `RollbackPreviewAsync`, `RollbackAsync` and `RestoreLegacyAsync` rehearse exclusive return with the latest cursor, supported states and preserved message IDs. Tests cover failed rollback/retry, recutover, new plugin host-event dedupe, no replay, multipart/Slack/extra routes and explicit pause with retained history. Unknown outcomes and unsettled edits/inbox work block transfer. |

## Validation

- [x] **404 backend tests passed; one live-provider test skipped.** This is the
  combined Chat, Auth, migration-runner, split-context and selected catalog suite.
- [x] **23 new backend cases:** 22 migration cases and one executable PowerShell
  helper fixture. Existing legacy transport/pump tests verify the changed failure
  behavior; API, authorization, provider and runtime regressions remain covered.
- [x] Phase 106 applied twice; model, catalog, execution order and split/global
  boundary registration checked for both new control tables. No product table added.
- [x] Requirement mapping, rollback boundaries, secret exposure, scope/revision
  checks and checklist synchronization reviewed. Imported metadata/history is
  exempt from automatic deletion; ordinary content retention still applies.
- [x] Whitespace and original line-ending preservation checked.

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage6/artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)' `
  --nologo -v quiet --logger 'console;verbosity=normal'
```

Ignored local evidence is in `.tmp-build/chat-stage6-final.log` and
`.tmp-build/chat-stage6-migration.log`. The existing
`Catalog_ClassificationMatchesDbBoundary` exclusion remains: its Phase 83 `repos`
classification mismatch predates this stage. Phase 106's own boundary is tested.
No UI files changed; Stage 5's recorded UI evidence remains historical evidence.

## Correctness issues found and closed

- [x] Different producer dedupe formats could replay an alert after transfer.
  Original keys and new compatible plugin producer keys now cross both directions;
  recutover reuses the existing delivery and message IDs. Incompatible key collisions
  require retaining/pausing the affected route.
- [x] A legacy network exception could be treated as a fresh retry. Durable send
  intent, explicit unknown outcomes and interrupted-intent recovery now prevent it.
- [x] An ambiguous topic or reused parent/question ID could bind an old control to
  new work. Ownership/birth/revision checks now hold those records for repair.
- [x] Unconfirmed preference rows could be mistaken for identity authority. Only
  currently confirmed host-store links produce migration aliases; revocation and
  subsequent unrelated link replacement do not revive the old proof.
- [x] Returning every imported subscription could resume unsupported plugin
  messages or paused parents. Rollback now pauses unsupported routes before its
  restore query, retains their plugin history, and limits legacy producers to the
  active imported scope, generation, destination and event filter.
- [x] A credential rotation could be lost on a rollback restart. Startup now reads
  the current protected reference and stays idle if it cannot be resolved.
- [x] An interrupted edit uses `editing`, not the send-state spelling. Rollback
  now blocks both pending and interrupted edits; the fixture verifies that even an
  explicit pause request leaves plugin ownership intact until edits settle.

## Remaining operator gates

- [ ] Approved real Slack/Telegram integration exercise from the implementation plan.
- [ ] Earlier Stage 1S real-bot/manual exercise.
- [ ] Operator-approved backend restart and actual host migration, when desired.

Tests use isolated databases and HTTP fixtures. No real provider message, service
restart, commit or push was performed. The implementation and documented rollback
boundary are complete; the remaining entries are operational gates.