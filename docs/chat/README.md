# Forge chat

## Current status

**Updated 2026-09-12. Plugin implementation is available; release acceptance is
pending live-provider validation.** Stages 0A, 0B, 1, 1S, 2–6 and section 5's
documentation/contract work are implemented. A checked implementation item does
not mean a real provider exercise passed. The current
[release gate](chat-plugin-release-gate.md) maps all seven section 5 requirements
to code, tests and remaining work.

The original release regression has **486 backend tests passing, one explicit live test
skipped**, including **37 shared contract cases** for FakeAlpha/FakeBeta and the
real Telegram/Slack adapters against fake peers. The UI build and **197 unit
tests** and **33 isolated browser cases** pass. See the release report for evidence
and exclusions.

A later [Slack identity follow-up](chat-plugin-release-gate.md#slack-identity-confirmation-follow-up-2026-09-12)
replaces copying a link command with a private approval button and automatic
status checks. Its backend regression passes **498 tests** with one live skip;
all 37 browser checks pass. The update is deployed after approval in API PID 43620
(September 12, 09:41 UTC). The running capability/channel lookup and actual Settings
form pass desktop/narrow checks. Private-message approval was unverified at that
checkpoint and was later verified after the callback repair below. The initial
personal Slack member ID remains required when no
verified account or retained previous input is available.

The subsequent [member-ID recovery correction](chat-plugin-release-gate.md#slack-member-id-recovery-2026-09-12)
restores an ID entered in a previous link attempt for the same operator,
connection and workspace. It remains unverified until Slack approval. This
correction was deployed after approval at 21:23 UTC in API PID 36144. The actual
Settings page restores the saved ID on selection and after reload, without typing.
The updated regression passes 508 backend checks (one live skip), 197 UI unit
tests, the UI build and 39 isolated browser checks.

The user's subsequent private confirmation appeared in Slack but its click was
rejected by the receiver. The [callback repair](chat-plugin-release-gate.md#slack-private-confirmation-callback-repair-2026-09-12)
handles Slack's omitted private-message metadata and was deployed after approval
at 21:41 UTC in API PID 35552. The user's private approval was processed at 21:42 UTC;
the saved identity and actual Settings badge remain Linked after reload.
Its selected backend regression passes 521 checks with one explicit live skip.
The broader live acceptance gates remain open.

## Start here

| Task | Guide |
|---|---|
| Add or repair Telegram | [Telegram setup](telegram-setup.md) |
| Add or repair Slack | [Slack setup](slack-setup.md), [exact app manifest](slack-app-manifest.yaml) |
| Select channels and enable a Workstreams parent | [Chat plugin setup](chat-plugin-setup.md) |
| Provider approval or one-time host registration | [Connection authorization](chat-connection-authorization.md) |
| Missing popup, permissions, delivery or identity problems | [Troubleshooting](chat-plugin-troubleshooting.md) |
| Credential replacement, atomic parent setup, checks and previews | [API examples](chat-plugin-api-examples.md) |
| Import an existing bridge or return supported routes to legacy | [Migration and rollback](chat-plugin-migration.md) |
| Release readiness and remaining live checks | [Release gate](chat-plugin-release-gate.md), [validation matrix](chat-plugin-validation-matrix.md) |

## Implemented capabilities

Settings has separate Chat, Repository providers and General tabs. Chat places
**Saved connections** beside **Add a connection** and selected-channel setup;
account linking and collapsible Host alerts sit below. Close stays available and
the layout stacks on narrow screens. Chat is the final compact action on every
top-level Workstreams row, including completed rows, and opens the same drawer
as the story detail header.

Each `(product, parent spawn)` owns its destinations and covers current/future
children. Telegram forum topics and Slack root threads keep parents separate,
even within one channel. Multiple destinations send/retry independently; scoped
domain receipts reconcile accepted answers across providers. Turning Chat off
does not pause work. Permanent Host alerts remain independent of story state.

Connections use write-only protected credentials and explicit product grants.
Authentication, current product permissions, installation-bound identity links,
content policy and revisions protect actions. Saving ordinary connection/route
revisions applies live. Host process environment/content settings require an
approved refresh. Fresh installs default to the legacy engine with chat disabled;
existing installations can explicitly migrate to persisted plugin ownership.

The new-connection UI supports provider consent, verified identity/credential
retrieval, review and save. Telegram requires one owner-created manager first;
the first token handoff remains manual in the browser. Slack can provision an app
after developer approval or use a preconfigured shared OAuth app. The browser's
Slack developer flow still transfers the approval request and challenge manually.
An agent-assisted desktop run is separate from product automation. Personal
account linking is also separate from bot/app installation; first-time Telegram
personal ID discovery remains a UI limitation.

## Observed deployment and remaining acceptance

The recorded host runs the plugin engine at migration revision 2. Real Telegram
consent/save and the new bot's personal identity link are verified. Assisted Slack
consent, app installation, encrypted connection save and membership in `#all-forge`
are verified. The redesigned Settings UI is live via Vite. See
[activation evidence](chat-plugin-activation-review.md) and
[Slack/layout evidence](chat-slack-live-setup.md).

The Slack channel form-encoding correction and section 5's cancellation fixes were
deployed after explicit approval at 08:55 UTC on September 12: API PID 39092,
UI PID 72132 retained. Direct/proxied health and actual Settings channel validation
pass; `#all-forge` is `ready`, with connection saved/applied revision 4/4.
The 09:41 UTC restart deployed Slack identity confirmation in API PID 43620;
the approved 21:23 UTC restart deployed member-ID recovery in API PID 36144.
The approved 21:41 UTC restart deployed the callback repair in API PID 35552.
All three receivers are receiving and Vite remains PID 72132. The personal Slack
account is linked; the four old invalid-interaction diagnostics did not increase
during successful approval. A future host-alert destination is still unset;
see [troubleshooting](chat-plugin-troubleshooting.md#configured-connection-shows-needs-linking).
The new Telegram destination, complete browser-only Slack setup, both providers'
parent setup/delivery/replies, a second Slack workspace sharing the receiver,
credential rotation and the earlier isolated Stage 1S live exercise remain gates.
No full-release claim follows from fixture tests or assisted app installation.

## Design and implementation history

- [Plugin design](chat-plugin-design.md) and [implementation plan](chat-plugin-implementation-plan.md).
- [Stage 0B](chat-plugin-stage0b-validation.md): operator identity and shared capabilities.
- [Stage 1](chat-plugin-stage1-validation.md): contracts, registry, schema and scoped configuration.
- [Stage 1S](chat-plugin-stage1s-validation.md): early Telegram notification slice and isolated live exercise.
- [Stage 2](chat-plugin-stage2-validation.md): durable fan-out, inbox, receipts and retention.
- [Stage 3](chat-plugin-stage3-validation.md): Telegram protocol extraction and interactions.
- [Stage 4](chat-plugin-stage4-validation.md): Slack Socket Mode and workspace/thread boundaries.
- [Stage 5](chat-plugin-stage5-validation.md): setup UI, atomic parent settings and helper APIs.
- [Stage 6](chat-plugin-stage6-validation.md): import, ownership transfer and rollback fixtures.

Earlier stage reports preserve their original dates, counts and limitations.
Use the release report for the latest combined result. Older designs and phase
checkboxes below are historical references, not the current setup procedure:

- [Original Forge bridge](chat-bridge-forge-plan.md).
- [Telegram per-spawn topics](telegram-per-spawn-plan.md).
- [Common.Chat extraction](chat-lib-extraction-plan.md).
- [Legacy host configuration](host-chat-configuration.md).
- [CopilotChatBridge sample](chat-bridge.md), a different integration pattern.
- [Build spine history](net10-build-spine-decomposition-fix.md).