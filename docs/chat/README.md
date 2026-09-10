# Forge chat

## Current design work

The next revision adds **Chat / Remote control** to each top-level row on the
Workstreams screen: enable it, select Telegram or Slack, and pick a channel.
Each row (a story spawn in the data model) owns its settings and covers all its
child workstreams. Multiple providers and channels may be attached to the same row.

- [Plugin design](chat-plugin-design.md) — scope, setup experience, contracts,
  routing, persistence, provider behavior, and compatibility.
- [Implementation plan](chat-plugin-implementation-plan.md) — sequenced work,
  affected code, migration, and acceptance checks.

**Status (2026-09-10): Stages 0A, 0B, 1 and 2 implemented; Stage 1S live gate pending.** The legacy bridge uses
explicit host-user configuration and safe defaults. Authenticated operator pairing
and shared capabilities are implemented. Stage 1 adds provider/plugin contracts,
the six control-DB configuration tables, scoped configuration APIs, Host alerts
setup state, revision checks and protection against reused spawn IDs.

- [Host chat configuration](host-chat-configuration.md) — legacy setup and defaults.
- [Stage 0B validation](chat-plugin-stage0b-validation.md) — identity and authorization.
- [Stage 1 validation](chat-plugin-stage1-validation.md) — all seven requirements,
  194 passing focused checks, the existing catalog-test limitation and API usage.
- [Stage 1S validation](chat-plugin-stage1s-validation.md) — Telegram row controls,
  metadata-only delivery, retention, automated evidence and the pending live exercise.
- [Stage 2 validation](chat-plugin-stage2-validation.md) — all 14 runtime requirements,
  schema registration, 37 new durability/recovery tests and complete regression evidence.

The active engine still defaults to `legacy`. Stage 1S adapts `TelegramTransport`
for one connection, one selected parent row and one destination on a fresh isolated
test profile. Save creates an introduction in the existing outbox; verified child
open-question events produce metadata-only alerts. Row status refreshes without
restarting agents. **Notifications only** remains the live provider boundary. Stage 2 adds the general
logical event/fan-out engine, durable inbox/cursors, shared app reception supervisor,
domain receipts and result reconciliation, scoped lifecycle and full retention. Its
interactive acceptance uses fake providers. Interactive Telegram, the real Slack
provider and full administration UI remain later stages. Fake HTTP and browser
checks are distinguished from the real Telegram exercise, which has not run.

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
