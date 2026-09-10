# Stage 2 — implementation and validation

Date: 2026-09-10. Scope: Stage 2 of the [implementation plan](chat-plugin-implementation-plan.md).
All 14 requirements and the four schema increments are implemented and checked.
The review found no remaining gap within Stage 2. Interactive Telegram, the real
Slack provider, full setup UI and legacy import retain their later-stage gates.

## Requirement review

Source paths below are relative to `src/forge/Forge.Api/` unless qualified.

| # | Requirement | Implementation and evidence |
|---|---|---|
| 1 | Provider-neutral logical events; versioned legacy conversion | `Services/Chat/ChatEventCatalog.cs` and `ChatEventService.EnqueueAsync` persist catalog/payload versions and the `plugins` discriminator. `ChatPayloadConverter.Read` converts legacy text/actions without granting old callbacks current authority. Legacy pumps exclude plugin rows. Conversion and legacy-upgrade tests pass; no legacy data is imported. |
| 2 | Independent deliveries, immutable snapshots, message parts and recovery | `Persistence/ChatDeliveryEntities.cs`, `ChatDeliveryModel.cs`, Phase 100 and `ChatDeliveryService` track each subscription copy, exact destination/revisions/generation, question/part IDs, operation owner/deadline, retry time and rendered/desired revisions. Two-destination, multipart, unknown-send and checkpoint-failure tests pass. |
| 3 | Verified scoped routing and atomic fan-out | `ChatScopeResolver.ResolveAsync` verifies the actual child's `StorySpawnId`, parent/repo/product membership and persisted planning-question ownership. `ChatEventService` commits the logical event, bindings and copies together. Wrong-child, pre-child planning, product isolation and injected transaction rollback tests pass. |
| 4 | Durable inbound acceptance, cursors and shared app reception | `ChatInboxService.AcceptBatchAsync` commits receipt keys, normalized envelopes and accepted cursors before returning acceptance. Dispatch has separate attempts/results. `ChatConnectionSupervisor.ReceptionSink` shares Slack app reception and routes by verified workspace installation. Receipt-before-ack, duplicate/mixed cursors, workspace routing and redelivery tests pass. |
| 5 | Connection supervision | `ChatConnectionSupervisor` owns one receiver per reception key, immutable connection revisions, cancellation and health checkpoints. A receiver that has not stopped keeps ownership. Changed revisions close sessions, accepted cursors restore reception, and dead process records retire. Supervisor, shared Slack receiver and health tests pass. |
| 6 | Retry classification and fair scheduling | `ChatDeliveryService` handles provider retry hints, permission/configuration failures and unknown outcomes. A window query chooses one ready delivery per connection/channel, followed by rotation across channels. Permanent failures support authorized retry; uncertain sends/topics remain visible without automatic replay. A 510-item backlog and isolated destination failure tests pass. |
| 7 | Versioned catalog, host scope, zero-match history and ordering | `ChatEventCatalog` defines all four existing kinds and explicit spawn/host defaults. Zero matches retain a reason. `ChatSummaryService` batches control-DB aggregates for requested product/spawn pairs; rows show unrouted counts. Earlier actionable failures block only their subscription; supersession and explicit skip unblock it. Enabling does not replay; explicit resend revalidates domain state and dedupes its request ID. Catalog, ordering, supersession and resend tests pass. |
| 8 | Single-instance SQLite budget and crash checkpoints | `ChatRuntimeState` owns a named mutex on a dedicated thread. `ChatDbBudget` bounds both SQLite connection and EF command timeouts, retries busy writes with jitter, and records busy counts/write timings. Runtime scans use five seconds, health coalesces to one minute, provider calls use 30 seconds, recovery records use 60 seconds. Successful provider receipts stay in memory until the DB checkpoint succeeds. Real SQLite contention, mutex, idle write and successful-send/checkpoint fault tests pass. |
| 9 | Scope retirement and reconciliation | `ChatScopeLifecycleService` is wired into product/spawn deletion and `ForgeSeedSpawnScrub.ScrubMockedSpawnsAsync`, using the reported deleted spawn set. `ChatScopeReconciler` runs at startup and every five minutes. Missing ownership retires bindings as orphaned; an unavailable split product DB pauses them as degraded. Resolution checks guard the factory's legacy control-DB fallback. Missing-parent, unavailable-file and scrub dry-run/apply tests pass. |
| 10 | Content policy and bounded retention | `ChatContentPolicy` applies product/host maximum and subscription restrictions before persistence and every render/retry/edit. Missing redaction inputs fall back to generated metadata. Unsafe inbound answers are rejected without storing their text. `ChatRetentionService` runs hourly, with batches of 100 per operation, and covers events, rendered messages/errors, inbox, tokens/challenges/links, domain receipts, health and configuration dependencies. Scoped product-owner and host-admin purge report remaining content/correlation. Deadline, bounded backlog, scoped purge and uncertain-operation tests pass. |
| 11 | Shared authorization, expected revisions and action receipts | `ChatDomainActionRunner`, `OpenQuestionService` and the action-service receipt wrappers reuse Stage 0B authority. A question answer and its unique product receipt commit together. External work records an operation before execution; unknown outcomes block repeat execution. Browser answer edits require the current question and answer revision. Concurrent FakeAlpha/FakeBeta answers, browser revision conflicts, domain checkpoint failure and nontransactional effect tests pass. |
| 12 | Shared parent/artifact actions and exact selection | The existing `StoryOperatorActionService` cores remain shared by HTTP and chat, now wrapped with receipts and revisions. `ChatStatusService.SpawnAsync` supplies parent/child/artifact identities, expected revisions, child counts, pending questions and unresolved operations. Dispatch uses an exact child/artifact and shared membership checks. Restart requires an actor-bound 30-second confirmation. Status selection, confirmation mismatch/expiry and the Stage 0B action parity suite pass. |
| 13 | Reconcile every copy, including browser actions | `ChatResultReconciler` pages deliveries and reads authoritative product questions/domain revisions. It retires only resolved/superseded parts, preserves unanswered parts, invalidates tokens and queues edits for every sent copy. It also sets terminal notification time for retention. Concurrent answers, browser answers, mutation-before-edit failure and terminal-content tests pass. |
| 14 | Disable, pause and removal | Configuration changes atomically cancel unsent copies and retire controls/tokens. Every send/provision/action revalidates the original route and current authority. Resolution edits can remove controls after a subscription is disabled; unavailable provider edits remain visible. Paused subscription, stale action, grant revocation, parent retirement and settings revision tests pass. |

## Schema registration

| Phase | Boundary | Tables |
|---|---|---|
| 100 | Control / Global | Alters `chat_notifications` and `chat_subscriptions.ContentMode`; creates `chat_deliveries`, `chat_delivery_messages` |
| 101 | Product / DirectProductId | Creates `chat_action_receipts` with required `ProductId` |
| 102 | Control / Global | Creates `chat_inbox`, `chat_connection_state`, `chat_receiver_state` |
| 103 | Control / Global | Creates `chat_operator_links`, `chat_link_challenges`, `chat_action_tokens` |

- [x] Each table appears in the matching EF context, catalog, runner and `DbBoundary`.
- [x] All four phases can be applied twice.
- [x] A legacy outbox upgrade preserves existing content without routing/importing it.
- [x] Split dry-run verifies each new control table is global, with no missing strategy.
- [x] The action receipt table is present only in the product model and product phase.

The host identity store remains the canonical OS-protected enrollment/link authority;
the control-DB link rows record verified correlations. Stage 6 migration state and
legacy aliases are deliberately still absent.

## Automated evidence

- [x] **262 backend tests passed; 1 live Telegram exercise explicitly skipped.**
  This includes **37 new Stage 2 tests** in `ChatRuntimeTests`,
  `ChatRuntimeBoundaryTests` and `ChatRuntimeCompletionTests`, plus existing chat,
  authorization, migration runner/catalog and split-context regression coverage.
- [x] **194 UI unit tests passed.**
- [x] TypeScript and Vite production build passed; the existing bundle-size advisory remains.
- [x] **4 isolated Playwright checks passed** with Microsoft Edge. They render the
  real row/control and intercept APIs; no running Forge API or real bot is used.
- [x] Whitespace/diff review completed with the repository's CRLF convention.

The existing `Catalog_ClassificationMatchesDbBoundary` test is excluded, as already
recorded in Stage 1/1S: Phase 83 lists `repos` as product data while the historical
`DbBoundary` classification remains control. The new Stage 2 table/boundary test is
included and passes. The real Stage 1S bot/manual exercise remains unchecked in the
implementation plan; it is not replaced by a fake-provider pass.

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage2/artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)'

# From src/forge/Forge.UI:
npm test -- --run
npm run build
$env:FORGE_CHAT_BROWSER_CHANNEL = 'msedge'
npx playwright test --config playwright.chat.config.ts
```

Ignored local evidence: `.tmp-build/chat-stage2-regression.log`,
`chat-stage2-ui-tests.log`, `chat-stage2-ui-build.log`, `chat-stage2-browser.log`
and the isolated build artifacts under `.tmp-build/chat-stage2/artifacts`.

## Review corrections

The gap review corrected connection-level SQLite transaction timeouts, mixed-batch
cursor rewind, raw action-token persistence/size/expiry, actor-bound confirmation
expiry, terminal notification timestamps, health snapshot races and lost activity
stamps, stale receiver retirement, large-queue starvation, an unnecessary full repo
read during scrub, and a duplicate UI type declaration. Each affected behavior has
automated coverage; the final suite passed after those changes.

## Runtime and maintenance boundary

The default engine remains `legacy`. The general runtime runs under `plugins` with
the Stage 1S slice disabled; the dedicated Stage 1S path remains available. Public
interactive configuration is still gated off until the provider stages. Tests
explicitly enable fake-provider interactive subscriptions to validate this core.

Current maintenance APIs use the existing authenticated capability boundary:

| Operation | API |
|---|---|
| Exact parent status and targets | `GET /api/products/{productId}/story-spawns/{spawnId}/plugins/chat/status` |
| Host history, ownership/health and write metrics | `GET /api/chat/host/status` |
| Retry a repaired permanent failure on its unchanged route | `POST /api/chat/deliveries/{id}/retry` |
| Skip a pending/retrying/failed delivery | `POST /api/chat/deliveries/{id}/skip` |
| Explicitly regenerate a current spawn alert | `POST /api/chat/notifications/{id}/resend`, body `{ "requestId": "unique-request-id" }` |
| Purge a product owner's selected spawn content | `POST /api/products/{productId}/story-spawns/{spawnId}/plugins/chat/purge-content` |
| Host-admin content purge | `POST /api/chat/retention/purge-content` |

Resend never redirects an old delivery. It creates a new event from verified current
domain state; the same request ID is deduplicated. Host account/queue alerts require
a fresh producer event because their old snapshot cannot prove the condition still
applies. Purge is bounded: repeat while `ContentRemaining` is nonzero.

Unknown send/topic/process outcomes retain operation IDs for inspection and repair.
They are not automatically treated as failed or blindly retried. These tests prove
local durability and idempotent domain mutation; they do not claim exactly-once
network delivery. Retiring controls or purging Forge data does not delete copies
already held by Telegram or Slack. Real provider reception/rendering and their
live acceptance belong to Stages 3/4; administration UI and owned secret storage
belong to Stage 5; legacy import belongs to Stage 6.

No existing Forge service was restarted, no production profile was changed, and
no real Telegram/Slack message was sent during this stage.
