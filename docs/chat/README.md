# Forge chat

## Current implementation

Stage 5 adds **Chat / Remote control** to each top-level row on the
Workstreams screen: enable it, select Telegram or Slack, and pick a channel.
Each row (a story spawn in the data model) owns its settings and covers all its
child workstreams. Multiple providers and channels may be attached to the same row.

- [Plugin design](chat-plugin-design.md) — scope, setup experience, contracts,
  routing, persistence, provider behavior, and compatibility.
- [Implementation plan](chat-plugin-implementation-plan.md) — sequenced work,
  affected code, migration, and acceptance checks.
- [Validation matrix execution](chat-plugin-validation-matrix.md) — deployment,
  all 28 scenario mappings, 408 backend checks, 197 UI unit tests, 12 browser
  cases and the remaining shared-conformance and real-provider gates.

**Status (2026-09-10): Stages 0A, 0B, 1, 2, 3, 4, 5 and 6 implemented. The Stage 1S manual exercise and the separate real-provider live gates remain pending; Stage 6 cutover/rollback is rehearsed in isolated fixtures.** The legacy bridge uses
explicit host-user configuration and safe defaults. Authenticated operator pairing
and shared capabilities are implemented. Stage 1 adds provider/plugin contracts,
the six control-DB configuration tables, scoped configuration APIs, Host alerts
setup state, revision checks and protection against reused spawn IDs.

- [Chat plugin setup](chat-plugin-setup.md) — current settings, row drawer, verification,
  APIs, Telegram helper and host activation.
- [Host chat configuration](host-chat-configuration.md) — legacy configuration reference.
- [Stage 0B validation](chat-plugin-stage0b-validation.md) — identity and authorization.
- [Stage 1 validation](chat-plugin-stage1-validation.md) — all seven requirements,
  194 passing focused checks, the existing catalog-test limitation and API usage.
- [Stage 1S validation](chat-plugin-stage1s-validation.md) — Telegram row controls,
  metadata-only delivery, retention, automated evidence and the pending live exercise.
- [Stage 2 validation](chat-plugin-stage2-validation.md) — all 14 runtime requirements,
  schema registration, 37 new durability/recovery tests and complete regression evidence.
- [Stage 3 validation](chat-plugin-stage3-validation.md) — Telegram extraction, commands,
  linking, topic lifecycle, callback correlation, and the eight-item gap review.
- [Stage 4 validation](chat-plugin-stage4-validation.md) — Slack Socket Mode, workspace
  routing, thread replies/choices and the connection UI follow-up completed in Stage 5.
- [Stage 5 validation](chat-plugin-stage5-validation.md) — all 14 requirements,
  381 backend checks, 197 UI unit tests, eight browser cases and remaining live gates.
- [Stage 6 validation](chat-plugin-stage6-validation.md) — all 11 migration requirements,
  22 migration cases, 404 backend checks and the compatibility gap review.
- [Migration and rollback](chat-plugin-migration.md) — read-only preview, stable import,
  explicit parent selection, exclusive ownership, latest-cursor rollback and retirement.
- [Slack setup](slack-setup.md) and [app manifest](slack-app-manifest.yaml).

The active engine still defaults to `legacy`. Stage 1S adapts `TelegramTransport`
for one connection, one selected parent row and one destination on a fresh isolated
test profile. Save creates an introduction in the existing outbox; verified child
open-question events produce metadata-only alerts. Row status refreshes without
restarting agents. Stage 1S retains its **notifications only** boundary. Stage 2 adds the general
logical event/fan-out engine, durable inbox/cursors, shared app reception supervisor,
domain receipts and result reconciliation, scoped lifecycle and full retention. Its
interactive acceptance uses fake providers. Stage 3 registers `Common.Chat.Telegram`
for the general engine, supports per-bot reception, identity linking, scoped commands,
question choices/custom replies and parent topics, and opens interactive subscriptions
for capable providers. Enable `Forge:Chat:Enabled`, select the `plugins` engine and
leave `TelegramSlice:Enabled` false to use it. Existing configuration APIs and
secret references remain supported. Stage 4 adds `Common.Chat.Slack`, one Socket Mode
receiver per app, workspace-qualified inbox routing, Web API threads and Block Kit
controls, channel linking and root completion/retirement updates. Stage 5 adds
Settings → Integrations → Chat, connection repair and verification, Host alerts,
confirmed identity linking and one shared row/detail drawer. Atomic saves queue
introductions and preserve running work; shared product/spawn summaries avoid
per-row chat queries. The Telegram helper now calls the same authenticated APIs.
Stage 6 adds Phase 106's control journal, stable legacy import, exclusive persisted
engine ownership and explicit migration/rollback commands. Existing alerts and
message IDs survive transfer; unsupported routes stay paused with retained history.
New backend code needs an operator-approved host
restart; implementation did not restart services or send real provider messages.

**Review status (round 1, 2026-09-09): all 15 comments fixed in documentation.**
The inline `REVIEW-D-nn` / `REVIEW-P-nn` records preserve the original feedback and
resolutions. Checked review comments do not imply implementation of later stages.

## Existing implementation and history

- [Forge bridge implementation](chat-bridge-forge-plan.md) records the original
  Telegram outbox, inbound actions, and operator linking.
- [Telegram per-spawn topics](telegram-per-spawn-plan.md) records the existing
  forum-topic routing and its remaining limitations.
- [Common.Chat extraction](chat-lib-extraction-plan.md) records the shared library
  already in the repository.
- [CopilotChatBridge sample](chat-bridge.md) is background material about a
  different conversation-to-agent pattern, not the setup guide for Forge.
- [Build spine notes](net10-build-spine-decomposition-fix.md) concern build history.

Earlier phase checkboxes describe their historical implementations; they do not
mean the new plugin design or Slack support is complete.