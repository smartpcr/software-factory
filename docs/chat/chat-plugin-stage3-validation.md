# Chat plugin Stage 3 validation

Validated on 2026-09-10 in `E:\work\github\crp\workflow`, against the Stage 2
baseline `1e5b3641`. This report covers the eight implementation items in
[Stage 3 of the implementation plan](chat-plugin-implementation-plan.md#stage-3--telegram-provider-extraction-and-interaction-repair).

## Implementation review

**8 complete · 0 partial · 0 missing.** The checkboxes below match the implementation
plan. Evidence uses the real Telegram adapter against HTTP fixtures and isolated
Forge host/product databases. No real Telegram bot or running Forge service was used.

| Done | Requirement | Implementation and verification |
|---|---|---|
| [x] | Extract the Telegram provider and preserve compatibility | [Common.Chat.Telegram](../../src/Common.Chat.Telegram/TelegramProvider.cs) owns the Bot API implementation, descriptor, session, rendering and receiver. The old `Common.Chat.TelegramTransport` type and `AddTelegramChatTransport` entry point remain in this assembly for the legacy engine. Forge references the project and the solution includes it. Registry and legacy-engine regression tests pass. |
| [x] | Independent settings, cursors and bot ownership | [TelegramSession.Reception](../../src/Common.Chat.Telegram/TelegramSession.Reception.cs) restores each connection cursor, submits batches of at most 20 updates to the durable sink, advances only after acceptance, and rejects duplicate polls. Provider ownership rejects a second session for the same verified bot. Disposal cancels and drains reception before releasing ownership. Tests cover independent bots, retry offsets, update zero, duplicate ownership and cancellation. |
| [x] | Validate configuration and discover through the receiver | `getMe`, `getWebhookInfo`, `getChat` and `getChatMember` validate token, installation, chat, membership and forum/topic permissions. [ChatProviderSetupService](../../src/forge/Forge.Api/Services/Chat/ChatProviderSetupService.cs) uses the supervised session for host-authorized checks. Discovery reads observed destinations without polling. Webhook conflicts are exposed as `telegram-webhook-conflict`; no `deleteWebhook` call exists. Protocol and host-setup tests cover these paths. |
| [x] | Linking, addressed commands, topic replies and callback acknowledgement | Commands addressed to another bot are ignored; `/start` and `/link` redeem connection/sender-bound challenges. Verified bot usernames produce short-lived deep links. Callback IDs survive normalization and are acknowledged after inbox commit, with a two-second acknowledgement budget independent of domain processing. [ChatInboxResponseService](../../src/forge/Forge.Api/Services/Chat/ChatInboxResponseService.cs) persists response claims and uses the original qualified address. Tests verify acknowledgements and same-topic replies. |
| [x] | Explicit identities, revocation and safe relinking | [ChatLinkService](../../src/forge/Forge.Api/Services/Chat/ChatLinkService.cs) uses canonical enrolled identities and records link history. Inbox challenge values are hashed before storage. Independent pending challenges survive issuance for another target. Relinking updates canonical links without inserting duplicate subscriber preferences; unlink revokes the operator's links. Tests cover existing subscribers, pending challenges, invalid/reused challenges, revocation, and recovery after successful linking but failed inbox checkpoint. |
| [x] | Question interaction, parent commands and bound feedback | [ChatInboxService.Interactions](../../src/forge/Forge.Api/Services/Chat/ChatInboxService.Interactions.cs) implements scoped status, question selection, explicit answers, parent pause/resume and generated feedback. Choices resolve through stored indexes; custom prompts bind one question/revision to one operator for ten minutes. Replies and callbacks require the original message/address and current scope/permission. Partial answers retire only answered controls. Truncated question scans never infer a unique target. Tests cover choices, custom replies, actor/message/topic mismatch, off/paused/notification-only scopes, parent pause/resume, flat chat before its first event, unsafe content and checkpoint recovery. |
| [x] | Parent topics, retirement, flat chat and DM | [ChatDeliveryService](../../src/forge/Forge.Api/Services/Chat/ChatDeliveryService.cs) retains the Stage 2 durable provisioning claim and unknown-outcome recovery. Subscription setting changes preserve the parent binding; future children reuse it. [Topic retirement](../../src/forge/Forge.Api/Services/Chat/ChatDeliveryService.Retirement.cs) removes controls before idempotent remote close. DM/flat policies do not create topics. Tests verify reuse, removal/close and unknown creation without blind retry. |
| [x] | Payload limits and original-message aliases | [TelegramRendering](../../src/Common.Chat.Telegram/TelegramRendering.cs) requires exactly 25 ASCII bytes (`a1_` plus 22 base64url characters), checks the 64-byte UTF-8 callback limit and validates all serialized parts before sending. Errors retain a safe field/limit reason. Split text preserves Unicode and puts controls on the final canonical message; known partial receipts remain durable and uncertain. [ChatLegacyAliasService](../../src/forge/Forge.Api/Services/Chat/ChatLegacyAliasService.cs) preserves original qualified message references and rejects ambiguous/stale targets. Targetless legacy actions resolve only through those aliases and shared authorization. Tests cover split messages, preserved aliases, unknown legacy targets, verified retry, partial sends and visible render failures. |

The shared provider conformance suite now exercises both Stage 1 fake providers and
the actual Telegram adapter. Provider-specific protocol fixtures cover the extra
Telegram requirements; fake providers are not reported as real Slack coverage.

## Schema, security and recovery

- [x] `Phase104Migration` is registered in the catalog and runner as control-only.
  It adds reply state/claims, command revision and content-scope metadata to
  `chat_inbox`, choice indexes to `chat_action_tokens`, and retirement state to
  `chat_conversation_bindings`. Existing table classifications remain Global;
  no product table or legacy import is added.
- [x] The migration upgrades existing rows and can run twice.
- [x] [ChatDpapiSecretStore](../../src/Common.Chat/ChatDpapiSecretStore.cs) implements
  the common secret-store interface with atomic, user-protected Windows blobs.
  Forge uses its profile's `chat-secrets` directory. Legacy token resolution reads
  its existing blob through the shared implementation; it does not import or
  overwrite that blob. Tests cover encryption, isolation, invalid references and
  unreadable blobs.
- [x] Responses have separate durable claims and memory checkpoints. A failed
  response checkpoint retries storage without repeating the send or domain action.
  Unknown sends retain known references and are not automatically replayed.
- [x] Content retention and scoped purge include generated command replies.
  Link challenges, unknown command arguments and unsafe answers do not persist
  plaintext secrets. Host status exposes safe response failure metadata.
- [x] The Stage 1S notification-only path remains selectable. Host alert
  subscriptions remain notification-only. Current enrollment, product role,
  connection grants, route revisions and scope state still bound every domain action.

## Automated evidence

- [x] **314 backend tests passed; 1 existing live-provider exercise skipped.**
- [x] **52 added cases** cover this stage: 21 Telegram protocol cases, 14 interaction
  cases, 14 boundary/recovery cases and 3 shared provider-conformance cases.
- [x] The broad run includes existing chat/legacy/slice, authorization, migration
  runner/catalog and split-context regression coverage.
- [x] The API and extracted provider compile using isolated build artifacts.
- [x] Final diff, file-ending and checklist review completed.

The existing `Catalog_ClassificationMatchesDbBoundary` test remains excluded, as
documented in Stages 1/1S/2: Phase 83 lists `repos` as product data while its
historical boundary classification remains control. Stage 3's own migration and
boundary checks are included and pass.

No frontend files changed; the Stage 2 UI/browser results remain historical
evidence, not a new Stage 3 browser run.

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage3/artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)' `
  --nologo -v quiet --logger 'console;verbosity=normal'
```

Ignored local evidence: `.tmp-build/chat-stage3-regression.log` and isolated
artifacts under `.tmp-build/chat-stage3/artifacts`. Earlier focused logs record
intermediate runs; the broad regression log is the final result.

## Usage and remaining stages

The default engine remains `legacy`. To select the general runtime, configure
`Forge:Chat:Enabled=true`, `Forge:Chat:Engine=plugins`, and
`Forge:Chat:TelegramSlice:Enabled=false`. Configure connection secret references,
product grants, a destination and an explicitly interactive parent subscription
through the existing authenticated configuration APIs.

| Operation | API or command |
|---|---|
| Discover observed destinations | `GET /api/chat/connections/{connectionId}/discovery?cursor=...` |
| Validate a saved destination | `POST /api/chat/connections/{connectionId}/destinations/{id}/validate` |
| Request an identity challenge/deep link | `POST /api/chat/connections/{id}/link-challenge` with external tenant equal to the verified bot ID, and the Telegram sender ID |
| Link / revoke | `/start <challenge>`, `/link <challenge>`, `/unlink` |
| Read status / select questions | `/status`, `/questions` |
| Answer a particular message | Reply to the question or use `/answer <messageId> <text>` |
| Parent scheduling controls | `/pause`, `/resume`; these call the same parent/child domain cores as Forge |
| Select a parent in a shared flat chat | Add `productId:spawnId` to status, questions, pause or resume |
| Inspect failed/uncertain replies | Host status `ReplyFailures` metadata |

The protocol follows Telegram's documented
[update acknowledgement](https://core.telegram.org/bots/api#getupdates),
[callback acknowledgement](https://core.telegram.org/bots/api#answercallbackquery),
[inline button limits](https://core.telegram.org/bots/api#inlinekeyboardbutton),
[forum permissions](https://core.telegram.org/bots/api#createforumtopic), and
[deep-link format](https://core.telegram.org/bots/features#deep-linking).
For forums, the bot needs topic-management permission. In flat groups, privacy
mode supports addressed commands and replies; administrator bots receive group
messages without requiring privacy to be disabled.
[Telegram's message visibility rules](https://core.telegram.org/bots/faq#what-messages-will-my-bot-get)

The Stage 1S real-bot/manual row gate remains unchecked. Slack is Stage 4, the full
setup UI is Stage 5, and importing/backfilling legacy aliases and cutover is Stage 6.
Stage 3 supplies and tests alias preservation; it does not migrate a live queue.
Loading the new backend and migration requires a user-approved Forge.Api restart.

## Corrections found during the gap review

The review repaired topic replacement on subscription edits, missing durable
interaction replies, lost partial-send references, callback replay on another
message, zero-offset acknowledgement, secret-bearing command arguments, scoped
reply purge, link/parent-command success recovery, hidden webhook errors and
unbounded question selection. The affected paths have automated coverage.

