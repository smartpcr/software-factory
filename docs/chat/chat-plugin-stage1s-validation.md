# Stage 1S — implementation and validation

Date: 2026-09-10. Scope: [Stage 1S](chat-plugin-implementation-plan.md#telegram-vertical-slice).
All six implementation items are covered. The live Telegram/manual row gate is
**pending**, so Stage 1S is not marked fully accepted. No existing Forge service
was restarted, no production profile was changed, and no real Telegram message
was sent during this implementation session.

## Requirement review

| # | Requirement | Status and evidence |
|---|---|---|
| 1 | Fresh isolated profile; one named Telegram connection, parent row and destination | Implemented. `ChatTelegramSliceService.GuardAsync`, `SaveAsync` and `RouteAsync` require explicit isolation/new-bot confirmation, reject legacy data, verify grants/parent ownership, and enforce one active route. `TelegramNotificationProvider` wraps `TelegramTransport` with connection-specific options and no legacy token fallback. |
| 2 | Row Chat control, toggle, provider/channel picker, persisted status | Implemented in `swarm/plugins/ChatRowControl.tsx`, `chatApi.ts`, and the `notification-setup` API. The spawn list carries explicit product ownership even across products with equal spawn IDs. Drafts stay in browser memory; only Save writes. Off/needs-setup/ready derive from saved activation, route and outbox health. |
| 3 | Setup introduction and metadata-only open-question alert; verified parent target; redaction and health | Implemented in `ChatTelegramSliceService.Outbox.cs`, `ChatEngineNotifier` and `ChatContentPolicy`. The producer's slug, child, repo and parent are verified against the owning product DB. New children resolve the same parent dynamically. Save queues the introduction in the configuration transaction. The worker regenerates/redacts metadata and verifies the immutable snapshot before sending. Health is polled every five seconds without restarting agents. |
| 4 | Retention/purge for the used configuration/outbox subset | Implemented in `ChatSliceRetention` and control-only Phase 99. Hourly batches are limited to 100 per operation. Pending content expires at seven days; terminal content is scrubbed at 30 days; terminal correlations and retired configuration are purged after 90 days in dependency order. Unconfirmed sends retain correlation for inspection, with content scrubbed at 90 days. Missing/replaced parents retire the slice route; unavailable product DBs are preserved. Host-admin content purge reports remaining content/correlation/unconfirmed operations. |
| 5 | New test bot, empty legacy queue, exclusive engine selection, no legacy import | Implemented. Both legacy pumps retain their opt-in behavior under `legacy` and are disabled under `plugins`. The engine-selected notifier also prevents new legacy plaintext rows under `plugins`. The legacy worker explicitly excludes `telegram-1s` rows on rollback. Guards reject existing legacy notifications, subscribers, topics or cursor state. |
| 6 | Notifications only; no Slack/fan-out/inbound mutations | Implemented. The Telegram descriptor advertises no receiver, replies, buttons, edits, topics, threads or discovery. The UI explicitly labels Notifications only. The shared interactive gate remains false, and Stage 3's remote-control requirement remains unchecked. |

Code paths are under `src/forge/Forge.Api/Services/Chat/` unless specified otherwise.
The full multi-provider schema, receiver supervision, retry/recovery policy,
five-minute scope reconciler, remote actions and full connection administration UI
remain in their planned later stages.

## Automated evidence

- [x] Focused backend suite: **225 passed, 1 explicitly skipped live exercise**;
  chat, auth, migration runner/catalog and split-context checks.
  The existing `Catalog_ClassificationMatchesDbBoundary` exception remains excluded:
  Phase 83 classifies `repos` as product data while `DbBoundary` retains its historical
  control classification; the separate repo-move flow predates this slice.
- [x] Fake HTTP tests use the actual Telegram adapter and outbox, with all Bot API
  requests intercepted by `FakeTelegramHttp`. They cover setup/receipt/refresh,
  metadata redaction, wrong identity/destination, disabled rows, forged ownership,
  connection changes, revoked grants, reused runs, supersession, newly added children,
  and a successful provider call followed by a failed local checkpoint.
- [x] Retention tests exercise the 7/30/90-day boundaries, a 105-row backlog, dependency
  ordering, uncertain-operation retention, immediate purge authorization and Phase 99
  upgrade/idempotence.
- [x] UI unit tests: 189 passed.
- [x] UI TypeScript/Vite production build passed; existing large-bundle advisory remains.
- [x] Isolated Playwright browser checks: **4 passed** using installed Microsoft Edge.
  They render the real `SwarmStoryRow`/Chat control and intercept API requests; they
  do not contact the running Forge API or Telegram. Cancel/Escape, Save/reload,
  other-product row off, reader restrictions, conflict feedback and a narrow viewport
  with visible dialog actions are asserted.
- [ ] Real Telegram exercise: skipped by default; credentials/profile not supplied.
- [ ] Manual row exercise against the live test profile: not yet recorded.

Artifacts from this session are in ignored `.tmp-build/chat-stage1s/`: `validation.log`,
`chat-stage1s.trx`, `ui-tests.log`, `ui-build.log`, `browser.log` and `chat-panel.png`.
The screenshot shows the actual component under the existing Forge theme.

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage1s/artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)'

# From src/forge/Forge.UI:
npm test
npm run build
$env:FORGE_CHAT_BROWSER_CHANNEL = 'msedge' # Omit to use installed Playwright Chromium.
npx playwright test --config playwright.chat.config.ts
```

## Test-profile setup

The default host remains on the legacy engine. A separate test host must explicitly
configure the following; the profile root must match its actual `IForgeDbPaths.Folder`
and cannot be the production default `E:\forge`:

```json
{
  "Forge": {
    "Db": { "Folder": "E:\\temp\\forge-chat-1s" },
    "Chat": {
      "Enabled": true,
      "Engine": "plugins",
      "HostId": "telegram-slice-test",
      "TelegramSlice": {
        "Enabled": true,
        "NewTestBotConfirmed": true,
        "IsolatedProfileRoot": "E:\\temp\\forge-chat-1s",
        "ForgeBaseUrl": "http://localhost:5181"
      }
    }
  }
}
```

Use a new bot and an isolated destination. `NewTestBotConfirmed` is an explicit
operator attestation; Telegram `getMe` verifies the configured bot identity, not
its creation date. Give the test process a token in an externally managed
`FORGE_CHAT_TEST_*` environment variable. No token is stored in configuration rows,
returned by APIs, copied from the legacy DPAPI blob or included in the record.
The read-only environment secret store deliberately cannot write/delete credentials;
their lifecycle remains with the operator who supplies the test process.

Using a paired host administrator, create one connection through the existing
Stage 1 API, granting it to the intended product:

`PUT /api/chat/connections/test-bot`

```json
{
  "expectedRevision": 0,
  "providerId": "telegram",
  "installationId": "BOT_ID_FROM_GETME",
  "displayName": "New Telegram test bot",
  "enabled": true,
  "settingsVersion": 1,
  "settings": {},
  "secretReferences": { "bot-token": { "store": "environment", "key": "FORGE_CHAT_TEST_TELEGRAM_TOKEN" } },
  "productIds": ["PRODUCT_ID"]
}
```

`PUT /api/chat/connections/test-bot/destinations/test-channel`

```json
{
  "expectedRevision": 0,
  "externalTenantId": "BOT_ID_FROM_GETME",
  "channelId": "CANONICAL_ISOLATED_CHAT_ID",
  "displayName": "Empty test channel",
  "kind": "supergroup"
}
```

These are admin provisioning operations, not dialog draft operations. The row picker
lists the product's granted connections and saved channels; discovery and inline
connection creation remain later stages. Row Save calls Telegram `getMe` and `getChat`
and requires the canonical returned ID, preventing a mutable channel username from
silently changing its saved target. See the [official Telegram Bot API](https://core.telegram.org/bots/api#getchat).
Requests use the existing paired cookie/CSRF flow and shared capability authorizer.

The product owner opens **Chat**, enables notifications, chooses the named connection
and channel, reviews the preview, then selects **Save notifications**. Save enqueues
an introduction; Ready is shown only after a receipt is persisted. Saving enabled
settings again explicitly creates a fresh activation/introduction. Turning the row
off cancels pending alerts and never replays them on re-enable. It does not unsend
Telegram copies or pause agents.

The slice's `telegram-1s` outbox rows use `ActionsJson` for a versioned immutable target
snapshot, including binding and connection/destination revisions, run identity and
story ID. Legacy senders never consume these rows. A send is durably marked `sending`
before the network call. An ambiguous failure or lost checkpoint becomes `uncertain`
and is not automatically repeated. This is not an exactly-once network claim. Check
the destination before explicitly creating a fresh setup; full repair controls are
Stage 2. No answer controls or resolution edits are promised for metadata alerts.

For an immediate local content purge, a paired host administrator can call
`POST /api/chat/retention/purge-content`. Each call handles at most 100 rows and reports
remaining content, correlations and unconfirmed operations. It does not delete provider
copies or force-remove unresolved correlations. Ordinary retention runs hourly even
when notification delivery is switched off.

## Pending live exercise

1. Copy [the exercise profile](telegram-slice-exercise.example.json) outside source
   control. Supply the new bot ID, isolated destination ID, a test-token environment
   reference and a new absolute JSONL record path. Set both confirmations to true.
   Supply the token to the test process through that environment reference; do not
   paste a credential into the profile or commit it.
2. Explicitly opt in and run just the live exercise:

   ```powershell
   $env:FORGE_CHAT_LIVE_EXERCISE_PROFILE = 'E:\temp\telegram-slice-exercise.json'
   dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
     --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage1s/artifacts `
     --filter 'FullyQualifiedName~TelegramSliceLiveExercise'
   Remove-Item Env:FORGE_CHAT_LIVE_EXERCISE_PROFILE
   ```

   The exercise creates its own fresh, isolated `WebApplicationFactory` profile and
   paired operators; no production host restart is needed. It sends exactly the two
   intended test notifications through the real adapter and records provider receipts,
   saved state and the other row remaining off. A pre-existing record path is refused
   before any send. A started record without a passed record means the exercise did
   not complete; inspect Telegram before rerunning.
3. Record the real row UI exercise separately on an authorized isolated test host:
   initial off → select channel → Cancel (no save/send) → enable and Save → introduction
   received → real child open-question alert received → Ready after a normal refresh;
   another row stays off. Record UTC, profile identity, parent scope, bot/channel IDs,
   message receipts and screenshots without credentials. The in-process live API
   exercise alone does **not** claim a manual live UI run.
4. Check the final Stage 1S gate only after both live provider evidence and manual row
   confirmation are attached. Do not restart an existing Forge service without the
   per-restart authorization required by `CLAUDE.md`.

## Correctness review and remaining gap

Implementation review addressed atomic replacement of the active unique route,
explicit product identity in aggregated rows, row revision races, config revocation,
secret-safe errors, invalid/changed parent runs, cancellation of pending notifications,
retention dependency ordering and ambiguous provider acceptance. Automated results
are separated from real-provider claims. The sole acceptance gap is the missing live
test profile and recorded provider/manual row exercise; no later-stage checkbox was
marked complete to conceal that gap.
