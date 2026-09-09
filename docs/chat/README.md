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

**Status (2026-09-09): Stage 0A implemented and verified.** The legacy bridge now
uses explicit host-user configuration, safe tracked defaults, and a notification-only
guard. See [host chat configuration and verification](host-chat-configuration.md).
Plugin runtime, Slack, shared authenticated authorization, and settings screens
remain planned.

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
