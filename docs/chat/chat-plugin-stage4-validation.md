# Stage 4 — Slack provider validation

Date: 2026-09-10. Baseline: `ceee7e9c` (`chat plugin: stage 3`).

The Slack adapter and its host integration are implemented. Of the ten Stage 4
checklist items, **nine are complete and one is partial**: item 10's rate limiting,
failure reporting and repair APIs are complete, but its connection-UI acceptance
belongs to the still-unimplemented Stage 5 settings screens. That parent checkbox
remains open rather than claiming a working repair screen. No other implementation
gap was found in this review. Real-workspace verification remains a separate gate.

## Requirement mapping

| # | Status | Requirement and evidence |
|---|---|---|
| 1 | [x] Complete | [Common.Chat.Slack](../../src/Common.Chat.Slack/Common.Chat.Slack.csproj) is referenced by Forge.Api and the solution. `ChatPluginRegistration` registers it beside Telegram, using the existing registry and secret-store contracts. The shared conformance suite runs the actual Slack adapter. |
| 2 | [x] Complete | [Manifest v1](slack-app-manifest.yaml) and [setup guide](slack-setup.md) cover public-channel scopes/events, the private-channel option, app-level `connections:write`, installation, channel invitation and authenticated configuration APIs. No DM, user-profile, slash-command or auto-join scope is added. |
| 3 | [x] Complete | `SlackProviderPlugin.OpenSessionAsync` validates bot credentials/workspace through `auth.test` and verifies the app through the socket hello. `SlackConnectionSession` paginates discovery and checks current membership, archive/private/Slack Connect state. Fixed codes distinguish bot/app credentials, workspace, installation and channel failures. Fixtures cover these paths. |
| 4 | [x] Complete | `SlackConnectionSession.SendAsync`/`EditAsync` use Web API calls and retain channel/message/root/workspace IDs as strings. Host bindings give children one parent root per destination. `ChatScopeReconciler` and `ChatDeliveryService.SlackRoots` reconcile completion/reopening to the known root. Retirement updates that root and records local retirement. Tests cover reuse, splits, completion, reopening and uncertain creation. |
| 5 | [x] Complete | `SlackMessageRenderer` emits plain-text blocks and useful fallback text, opaque action keys, accessible labels and buttons on the final part. It preflights every part, Unicode, button labels/values/counts and serialized byte limits. The host preserves all receipts, canonical button correlation and actor-bound custom prompts. Tests cover partial sends and visible field failures. |
| 6 | [x] Complete | `SlackSocketReceiver` verifies hello before events; accepts into the durable sink with a two-second budget, then acknowledges with a separate half-second budget. Retry/failed commits are not acknowledged. Fresh socket URLs, disconnect/refresh handling, backoff, rate hints, cancellation and duplicate receiver exclusion have protocol fixtures. A host fixture verifies actual database commit before acknowledgement. |
| 7 | [x] Complete | A singleton app receiver owns workspace sessions through `IChatSharedReceptionSession`. The supervisor detaches one member without cancelling remaining members. Events match the app, workspace and authorized bot; unroutable events produce bounded durable diagnostics containing a safe code and workspace ID. The two-workspace/private-public/Telegram fixture proves routing and receiver survival. |
| 8 | [x] Complete | `SlackSocketReceiver.Normalize` filters bot/self/edit/delete events, rejects mismatched app/installations and cross-workspace actions, and normalizes buttons/replies. Inbox keys use Events API `event_id` or interactive `envelope_id`. The existing expected-revision domain receipts prevent repeated actions even with different envelope IDs. Cross-provider tests prove one accepted question answer and control retirement on all copies. |
| 9 | [x] Complete | `ChatInboxService.Interactions` treats a thread reply as a root-scoped candidate, never an implicit reply to a particular question. Multiple questions or custom selections require an explicit decimal message ID. Custom selections remain actor-bound and expire. `ChatLinkService` issues workspace/sender/revision-bound channel challenges; `!forge link`, `unlink`, `status`, `questions`, `answer`, `pause` and `resume` use ordinary message events. |
| 10 | [ ] Partial — Stage 5 UI | `SlackRequestBudget` paces workspace/method and channel requests and carries 429 hints without blocking other workspaces. Setup/health/delivery APIs expose repair codes; current revision updates, destination validation and explicit failed-delivery retry support repairs. Tests prove restored membership/archive/credential access retries a failed question without recreating its known root. The connection repair screen itself remains Stage 5 work. |

`auth.test` normally exposes workspace and bot-user identity, not the bot's app ID.
The app token is verified by hello, and Events API authorizations must match the
verified bot installation before dispatch. `bots.info` would add `users:read`, which
the agreed scope set deliberately avoids. This verification split is documented in
the setup guide; fixtures cover mismatched installation authorization.

## Automated evidence

- [x] **369 backend tests passed; one existing live-provider test skipped.**
- [x] **55 added cases:** 38 Slack protocol cases, 16 host/runtime cases and one
  actual-Slack shared conformance case.
- [x] The broad run includes all existing chat/legacy/slice and authorization tests,
  migration runner/catalog tests and split-context regression coverage.
- [x] `Phase105Migration` upgrades existing control rows idempotently. It adds
  bounded receiver diagnostics and root desired/rendered/retry state, with catalog
  and execution-order registration. Both affected tables remain Global/control;
  no product schema changes were introduced.
- [x] API/provider/test compilation uses isolated artifacts and does not restart
  the running host.
- [x] Final diff, per-line file-ending preservation and checklist review completed.

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage4/artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)' `
  --nologo -v quiet --logger 'console;verbosity=normal'
```

Ignored local evidence: `.tmp-build/chat-stage4-regression.log` and
`.tmp-build/chat-stage4/artifacts`. Earlier focused protocol/runtime logs record
intermediate runs. The existing `Catalog_ClassificationMatchesDbBoundary` exclusion
is unchanged: historical Phase 83 calls `repos` product data while its boundary
classification remains control. Stage 4's own migration and boundary assertions
are included and pass. No frontend files changed or browser tests were run.

## Gaps found and addressed

- [x] Disposing the member that started reception used to cancel the shared socket.
  App reception now has its own lifetime, and remaining members continue receiving.
- [x] Answer selectors accepted only integers. Slack selectors now retain decimal
  timestamps as strings and still require the exact qualified message address.
- [x] Thread replies cannot identify an individual question. Candidate selection
  now honors explicit actor-bound custom prompts and refuses ambiguity.
- [x] Local-only retirement previously bypassed the provider, and parent completion
  did not update Slack roots. Known root edits now recover independently of creation.
- [x] Unknown-workspace diagnostics lacked the workspace needed for repair. Bounded
  diagnostics now retain it without retaining the event body or executing an action.
- [x] Slack credentials from another connection could bypass generic credential
  pattern redaction. Bot/app token patterns now join the existing secret redaction.
- [x] Message updates initially reused posting-only parameters. Update payloads now
  use the documented narrower argument set; protocol fixtures enforce it.

## Remaining gates

- [ ] Stage 5 connection UI: display separate setup/health failures, replace
  credential references or credentials, validate channel repairs and expose explicit
  retry. Stage 4 item 10 remains open until this screen is implemented and tested.
- [ ] Approved live Slack workspaces plus Telegram: concurrent answers, cross-copy
  resolution, installation/scope checks, disconnect recovery, two workspace
  installations on one receiver, and continued Telegram reception. Fixtures do not
  close this integration gate.
- [ ] The earlier Stage 1S real-bot/manual row-control exercise remains pending.

Backend activation requires a Forge.Api restart for provider registration, the new
control columns and runtime changes. No running service was restarted, no real
Slack/Telegram message was sent, and no commit was created during implementation.
