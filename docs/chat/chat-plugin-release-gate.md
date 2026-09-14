# Chat plugin release gate

**2026-09-12: release held for live-provider acceptance.** Section 5's source and
documentation work is implemented. The shared conformance gap is closed; two
provider cancellation bugs were found and fixed. Automated acceptance does not
replace actual setup/delivery/rotation evidence.

Baseline: `0d790898b63e189eb4d5f1f112f85f62ddd900f6` plus the working-tree changes.
After explicit restart approval, the tested backend was deployed at 08:55 UTC on
2026-09-12: API PID 54028 → 39092. UI PID 72132 was retained. Real Slack discovery
and channel validation now succeed through both Forge's API and Settings UI.
No commit, provider test message or credential rotation was performed in this pass.

The later Slack identity-button follow-up was deployed after fresh approval at
09:41 UTC: API PID 39092 → 43620; UI PID 72132 was retained. Direct/proxied health,
the new API capability and destinations, and the actual Settings form pass.
The private Slack message and the user's approval were unverified at that
checkpoint; the later successful callback record is below.

The member-ID recovery correction was deployed after approval at 21:23 UTC:
API PID 43620 → 36144, retaining UI PID 72132. The actual Settings page restores
the saved input on selection and after reload without typing. Personal proof
remains separate. The user subsequently reported that the private Slack message
appeared but its confirmation had no effect. The callback-format repair was
deployed after approval at 21:41 UTC in API PID 35552. The user's private approval
was processed at 21:42 UTC, and the actual Settings badge stays Linked after reload.
Its separate checklist is below.

## Section 5 evidence map

| Gate | Status | Evidence and remaining work |
|---|---|---|
| RG-01 Both providers complete connection and parent setup through UI | Pending live acceptance | Browser setup/save/cancel tests pass for both providers. Real Telegram consent/save/link and assisted Slack install/save are recorded. The deployed Settings UI validates Slack `#all-forge` successfully. The new Telegram channel and complete browser-only Slack approval/workstream path remain unverified. |
| RG-02 Parent-owned controls across every layer | Complete | `SwarmStoryRow`, shared `ChatRowControl`, `ChatConfigurationService.Setup`, parent scope/run identity, routing and migration. `ChatManagementTests` verifies atomic, authorized writes and unchanged completed/current/future child state. The five-row browser fixture verifies refresh/reorder/filter/product switching. |
| RG-03 Multi-destination delivery, scoped actions and cross-provider resolution | Pending live acceptance | All VM-01–VM-28 automated mappings pass. `ChatSlackRuntimeTests.Slack_and_Telegram_answers_share_one_domain_receipt_and_retire_all_copies` uses both real adapters against fake peers. Real questions, provider replies, scoped reversible action and test credential rotation remain required. |
| RG-04 Migration and rollback boundaries | Complete | `ChatMigrationTests` and `ChatMigrationBoundaryTests` cover repeatable import, exclusive ownership, latest cursor, unknown outcomes, unsupported routes and compatibility retirement. [Operator guide](chat-plugin-migration.md) explains previews and refusal/pause boundaries. Actual rollback of the user's host is separately authorized work. |
| RG-05 Current guides, troubleshooting, scopes, rotation and APIs | Complete | [Telegram](telegram-setup.md), [Slack](slack-setup.md), [manifest](slack-app-manifest.yaml), [troubleshooting](chat-plugin-troubleshooting.md), [API examples](chat-plugin-api-examples.md). Generated public/private manifests have exact scope/event assertions in `ChatSlackSetupTests`. |
| RG-06 Current index and historical references | Complete | [README](README.md) distinguishes implemented capability, observed deployment, pending acceptance and historical phase reports. |
| RG-07 Checkboxes match recorded evidence | Complete | [Implementation plan section 5](chat-plugin-implementation-plan.md#5-release-gate-and-documentation), this report and the [validation matrix](chat-plugin-validation-matrix.md) remain aligned. Pending live checks stay unchecked. |

## Shared provider contract

`ChatProviderConformanceTests` now runs the common contract for FakeAlpha,
FakeBeta, Telegram and Slack. `.Release.cs` is part of that same parameterized
class, with fixture-specific wire injection and common behavioral assertions.

- [x] Qualified send/edit references; reject a foreign connection before HTTP.
- [x] Distinct parent conversations, explicit unsupported policy, and retirement
  according to the provider descriptor: Telegram remote close; Slack local thread
  retirement with root update. Creation is durably claimed by the host; a repeated
  provider API call is not asserted to be idempotent.
- [x] Drain discovery pages with qualified unique destinations and opaque cursors,
  including an empty Slack page with a continuation; discovery starts no receiver.
- [x] Preserve Unicode at split boundaries, safe plain rendering, per-part character/
  serialized-byte bounds, and preflight rejection of oversized bodies/action tokens.
- [x] Fixed safe errors, rate-limit hints, read unavailability and no silent retry
  of an uncertain send.
- [x] Reject pre-cancelled validation, discovery, conversation mutation, send, edit
  and cursor restoration. Cancellation no longer retires a local Slack conversation
  or silently succeeds on Telegram's local operations. In-flight send uncertainty
  and partial receipts retain their existing handling.
- [x] Stable inbound provider/connection/sender/event identity across retry and
  duplicate receipt. Acknowledgement follows durable Accepted/Duplicate only;
  Telegram's cursor does not advance on Retry.

The initial shared run failed two cancellation cases (31 passed, 2 failed).
After the fixes and additional unknown-outcome cases, the regression contains
**37 passing shared contract cases**. Protocol-specific and host-level suites
add workspace/channel isolation, revocation, lifecycle, DB recovery and result
reconciliation coverage. Test hosts set `FORGE_TEST_GUARDRAIL` and use temporary
databases/credentials; HTTP/WebSocket peers are fixtures, not provider services.

## Validation record

- [x] Backend release regression: **486 passed, one live-provider test skipped**.
  Covers Chat, Auth, migration runner/catalog and split database checks, including
  the 37 shared cases and exact Slack scope/event assertions.
- [x] UI build and **197 unit tests** pass.
- [x] Isolated chat browser suite: **33 passed**, including both provider setup
  flows, five independent parent rows, repair, identity and desktop/narrow Settings.
  External provider pages/API responses are intercepted in this suite.
- [x] Post-deployment documentation audit: 140 local links/anchors across 11
  documents, six JSON examples, public/private manifest permissions and the
  five-complete/two-pending gate split pass. Deployed source/binary hashes, health,
  channel state, receiver recovery and live Settings evidence also match the report.

Evidence: `.tmp-build/chat-release/` contains `conformance-before.trx`,
`conformance.trx`, `backend.trx`, `backend.log`, `ui-build.log`, `ui-unit.json`,
`ui-unit.log`, `browser.log`, `browser-results/` and `release-summary.json`.
The final contract count is from `backend.trx`; `conformance.trx` is the earlier
33-case passing run.

The unrelated existing `Catalog_ClassificationMatchesDbBoundary` test is excluded
for its Phase 83 `repos` mismatch, as in section 4. This selected release regression
is not a claim that the entire repository integration suite passes. The skipped
test is the explicit Stage 1S live exercise, which requires its own isolated bot/profile.

Reproduce from the repository root; isolated artifacts avoid the running API locks:

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path .tmp-build/chat-release-artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)' `
  --logger 'trx;LogFileName=backend.trx' --results-directory .tmp-build/chat-release

# From src/forge/Forge.UI:
npm run build
npm test
$env:FORGE_CHAT_BROWSER_CHANNEL = 'msedge'
npx playwright test --config playwright.chat.config.ts
```

## Deployment and live acceptance handoff

The Slack conversation form-encoding change from the previous pass and the new
Telegram/Slack cancellation guards are deployed in API PID 39092. The user
explicitly approved this restart; `scripts/run-forge-ui.ps1 -ServerOnly -Rebuild`
completed successfully. [CLAUDE.md](../../CLAUDE.md) still requires fresh approval
for any later restart because Forge.Api hosts active agent loops.

- [x] Deploy the tested backend with approval and verify `/api/health` directly
  on port 5180 and through the port 5181 UI proxy: both return HTTP 200 / `ok`.
- [x] Validate the saved Slack destination through Forge. At 08:56 UTC,
  `slack-t0c1atntpnj-all-forge` changes from `unavailable` to `ready`;
  connection `slack-t0c1atntpnj` is `ready`, saved/applied revision 4/4.
  The destination's routing revision remains 1; validation changes health only.
- [x] Repeat channel validation using the actual Settings UI at 09:00 UTC.
  HTTP 200 / `succeeded: true`; desktop 1440px and narrow 390px checks have zero
  horizontal overflow, a visible Close button and no page errors. Prepare and
  cancel the Slack `#all-forge` host-alert draft; saved routes and identities
  remain unchanged. No test message or link proof is generated by this check.
- [x] Verify receiver recovery: one Slack receiver and two Telegram receivers
  report `receiving`, with no receiver diagnostic codes. All three connection
  revisions are applied. This confirms supervisor recovery, not end-to-end replies.
- [ ] Identify a test product and parent plus dedicated Telegram bot/group and
  Slack app/workspace/channel. Existing operational connections are not implicitly
  disposable test credentials. Confirm intended messages, reversible work action
  and credential rotation with the operator before the live exercise.
- [ ] Through the actual UI, complete each provider's connection and channel setup,
  then enable both destinations on that test parent. Record the real request/save
  outcome, parent revision and one topic/thread per destination. The earlier
  assisted Slack CLI installation does not close a failed browser-only endpoint.
- [ ] Explicitly enable redacted question content for the test product and both
  subscriptions before answer/button tests. Check the effective setup response;
  changing the host's product-content allowance requires an approved refresh.
- [ ] Use explicit send/reply checks and link the intended operator in each provider.
  Verify outbound and inbound state independently, without recording proof tokens.
- [ ] Create two harmless questions on the test parent via its normal work flow.
  Answer the first in Telegram and the second in Slack. Record delivery/message
  IDs and one domain receipt per answer; verify controls resolve in both providers.
- [ ] Attempt the stale alternate-provider control and confirm it cannot replace
  the accepted result. On a designated stalled test child, use the reversible
  parent pause/resume action and verify another parent's state remains unchanged.
- [ ] Complete the plan's broader provider-integration gate: two Slack workspaces
  sharing one app receiver while Telegram remains active. Verify that disabling
  one workspace and failing one destination preserve the other routes, then
  verify reconnect. A second workspace approved for that app is still required.
- [ ] Rotate only the selected test credentials using the provider guides. Confirm
  new applied revisions, recovery and no duplicate sends or receivers. Preserve
  the bot/workspace identity and provider-message history.
- [ ] Run the earlier Stage 1S exercise on its separate isolated profile, or record
  an explicit release-scope decision that supersedes it. Do not silently waive it.
- [ ] Record actual UTC results, exact build, scopes, revisions, safe errors and
  remaining failures; only then close RG-01/RG-03 and declare release acceptance.

Live evidence so far: [host activation](chat-plugin-activation-review.md) and
[Slack installation/layout](chat-slack-live-setup.md). No production rollback or
external test exercise is implied by completing the documentation checkboxes.

Deployment evidence under `.tmp-build/chat-release/`: `deploy.log`,
`deploy-before.json`, `deploy-after.json` (process paths, source and binary SHA-256
hashes, direct/proxied health), `deployed-settings.json`,
`settings-verified-{1440,390}.png`, `settings-host-alert-draft.png`, and
`settings-{before,after}-deploy.json`. The earlier Settings probe used an overly
strict label locator; the corrected role-based check passes. The actual Slack
validation succeeded in both probes. Temporary operator sessions were logged out.

The deployed binaries under `src/forge/Forge.Api/bin/Debug/net8.0/win-x64/`
have these SHA-256 hashes:

| Binary | SHA-256 |
|---|---|
| `Forge.Api.dll` | `7C07E2A95787CA899580191533A65F3FCC3A7BA4386CE005BE587A603C67270C` |
| `Common.Chat.Slack.dll` | `3D7D057F5696629A3FCAD9FACE97AC11B3531D91A73F6F39DA357FB56A3B9F9E` |
| `Common.Chat.Telegram.dll` | `8B7E2E082ABED2A73DE511E0BAEB2DE91B60438E9ED9B305E4CED946A4EDBBB3` |

## Current host setup still required

The post-deployment read confirms no host-alert subscriptions and no personal
Slack operator link. App installation and the `ready` receiver do not establish
that personal link. See [account linking and host-alert steps](chat-plugin-troubleshooting.md#configured-connection-shows-needs-linking).

The displayed **Unrouted 38** counts retained routing history: 23
`ownership-unverified`, 13 `unresolved-scope`, and two `filtered` events. It includes
answered, acted, superseded, failed and expired records. The associated historical
deliveries are 30 cancelled, four answered and two acted; the orphaned binding
count is zero. Saving a new host route applies to future events and does not replay
or clear those historical records. Restart approval did not select a destination
for future host alerts or establish a personal Slack proof.

## Slack identity confirmation follow-up (2026-09-12)

The manual command handoff shown in Settings is replaced by a private Slack
approval request. The entered personal account ID and a saved channel determine
the recipient. A sole channel is selected automatically. Forge opens the channel,
calls `chat.postEphemeral` with an interactive button, and waits for the verified
Socket Mode user callback. The native Slack confirmation and Forge's waiting
dialog handle the human approval; no command or challenge code is copied.

- [x] Send a private confirmation using existing bot permissions, scoped product
  grants and a saved destination. No DM/user-token scopes or new migration.
- [x] Keep the raw challenge out of the browser response and durable inbox.
  Bind proof to the requesting operator, Slack user/workspace and connection revision.
- [x] Reject another sender/workspace, replay, expired/cancelled/replaced requests
  and stale connection revisions. Never publish a channel reply to private approval.
- [x] Provide the channel link, missing-message resend, expiry, cancellation,
  automatic link detection and a host-capability check for older deployed APIs.
- [x] Backend regression: **498 passed, one explicit live skip**, including
  **12 new identity-confirmation cases**. UI build and **197 unit tests** pass.
- [x] Final isolated browser suite: **37 passed**, including private approval,
  wrong-workspace status, popup fallback, expiry/resend/cancel, small-screen layout,
  actionable membership failure and the older-backend capability guard.
- [x] Documentation links, API JSON examples and release checkboxes are synchronized.
- [x] Deploy the new backend after fresh restart approval at 09:41 UTC. API PID
  39092 → 43620; the clean build succeeds with zero errors. UI PID 72132 remains.
- [x] Verify direct/proxied health, authenticated confirmation capability and
  saved channel lookup. Anonymous destination/confirmation requests return 401.
  Slack is ready at saved/applied revision 4/4; all three receivers are receiving.
- [x] Verify actual Settings at 1440px and 390px: `#all-forge` is selected
  automatically, **Link my Slack account** enables after entering the member ID
  and confirming ownership, and Close/scrolling work without horizontal overflow.
  No page errors; the temporary operator session was logged out.
- [x] Verify the actual private Slack message and the user's **Confirm link →
  Link account** approval after the callback repair below. The personal account
  was linked at 21:42 UTC, with processed proof and the actual Settings badge verified.

Evidence is separate from the earlier deployment: `.tmp-build/chat-identity/`
contains `backend.trx`, `backend.log`, `ui-unit.log`, `ui-build.log`, `browser.log`
and desktop/mobile confirmation screenshots. All provider traffic in these tests
is simulated. The old deployment evidence remains a historical checkpoint.

The approved deployment and read-only UI/API checks are recorded separately in
`deploy.log`, `deploy-before.json`, `deploy-after.json`, `deployed-identity.json`
and `deployed-identity-{1440,390}.png` under that folder. Those live checks did not
submit an account confirmation or change identity links/host routes. New binary
SHA-256 prefixes are `A2D931AB9C0A7D47` (Forge.Api), `7C667905D8D01CEF`
(Common.Chat.Slack) and `8B7E2E082ABED2A7` (Common.Chat.Telegram); full hashes and
matching source hashes are in `deploy-after.json`.

Slack accepting an ephemeral request does not prove the message is visible; the
UI says it is waiting for approval and offers a fresh request while Slack is
active. The first member ID remains a one-time input for an account without a
verified link. The existing manual API is retained for compatibility. None of
this closes RG-01 or RG-03's remaining real-provider requirements.

## Slack member-ID recovery (2026-09-12)

The user had already entered a personal member ID and issued a link challenge,
but Settings only filled in completed identity links. Reloading left the field
blank. Management now returns `recentLinkTargets` from the operator's retained
pending/cancelled requests. The latest unambiguous, valid target is available
only for its enabled Slack connection and matching workspace with current product
access. This is previous input, never proof or a new permission grant.

- [x] Restore previous input when no verified account is available. Keep the field
  editable, leave ownership unchecked and wait for Slack approval.
- [x] Prefer a verified account; reject other operators/workspaces/connections,
  removed grants, disabled connections, invalid IDs and ambiguous latest inputs.
- [x] Revoke pending and cancelled requests together so revoked input is not reused.
  Existing challenge retention applies; the member ID may be needed again after
  history is cleaned up if the account was never linked.
- [x] Pass **508 backend tests** (one explicit live skip), including ten new
  authenticated input-recovery cases, **197 UI unit tests**, the UI build and
  **39 isolated browser checks** including reload, edit and workspace isolation.
- [x] Check 12 documents, 174 local links, seven JSON examples, whitespace and
  release checkboxes at the pre-deployment checkpoint; all pass.
- [x] Deploy after explicit approval at 21:23 UTC: API PID 43620 → 36144.
  Clean rebuild succeeds with zero errors; all six tested source hashes match.
  UI PID 72132 is retained. Direct/proxied health return HTTP 200.
- [x] Verify the real Settings page restores the saved member ID on connection
  selection and after reload without typing. The only saved confirmation channel
  is selected automatically. Ownership remains unchecked until explicitly selected;
  the link button then enables. At 1440px/390px, Close and scrolling remain available,
  with no horizontal overflow or page errors. Identity links and host routes are unchanged.

Evidence is in `.tmp-build/chat-identity-autofill/`: `deploy-before.json`,
`deploy.log`, `deploy-after.json`, `deployed-autofill.json` and
`deployed-autofill-{1440,390}.png`. Deployment records contain process paths,
source/binary SHA-256 hashes and health responses. The verified API binary hash
starts `946EDAA68A2E`; Slack `675ABE2C7F26`; Telegram `98A2659A6D9C`.
The earlier 498-test/37-browser checkpoint remains historical evidence.
No live approval request was sent by the autofill check. All three receivers
are receiving, but Slack reports four `slack-interaction-invalid` diagnostics;
the personal Slack link remains absent. Autofill success does not prove linking.

## Slack private-confirmation callback repair (2026-09-12)

The user saw the requested private message in Slack and clicked its confirmation,
but Forge continued waiting. The running receiver reported
`slack-interaction-invalid`. The normalizer required `message.ts` and the bot's
`message.user` before accepting any button. Slack's
[ephemeral-message documentation](https://docs.slack.dev/messaging/#ephemeral-messages)
explains that these message bodies are omitted from interaction payloads; the
[block-actions reference](https://docs.slack.dev/reference/interaction-payloads/block_actions-payload/)
also makes the message optional. The former test fixture incorrectly attached
ordinary-message metadata to private confirmations, concealing this incompatibility.
No raw live callback or credential was captured; the documented format reproduces
the failure and is consistent with the live diagnostic.

- [x] Reproduce the missing-message failure before changing the receiver: the
  existing identity round-trip test fails because the account remains unlinked.
- [x] Accept omitted/partial message metadata for `forge_identity_link` only when
  Slack marks the container ephemeral. Reject conflicting metadata when supplied.
  Ordinary work-action buttons retain message-author and timestamp validation.
- [x] Correct the private-message fixture. Verify sender/workspace mismatch,
  cancellation, expiry, revision changes, replacement, replay and token redaction
  using that format. Add 13 protocol cases for omitted/partial/full metadata,
  malformed or foreign context, and rejection of ordinary buttons without messages.
- [x] Complete the broader chat/auth/migration regression: **521 passes**, one
  explicit live skip. All 12 identity-confirmation cases and 13 new protocol cases
  pass. The unchanged frontend retains its 197 unit/39 browser passes and build
  evidence; those suites were not rerun for this receiver-only change.
- [x] Audit 12 documents, 183 local links, seven JSON examples, synchronized release
  checkboxes and whitespace. Record tested source hashes; deployed binaries are
  unchanged from the verified member-ID recovery build.
- [x] Deploy the callback repair after fresh restart approval at 21:41 UTC:
  API PID 36144 → 35552, with UI PID 72132 retained. The clean build has zero
  errors, all ten recorded source hashes match, and direct/proxied health return
  HTTP 200. The deployed Slack binary SHA-256 begins `C337EB1F3E93`.
- [x] Verify the user's actual private approval was processed at 21:42:30 UTC.
  The inbox records `identity-link`, `processed`, `linked` for the intended user
  and workspace; the single-use challenge is consumed and an active identity
  record is created. No public reply was sent (`ResponseState: none`).
- [x] Verify the actual Settings page displays **forge — Linked**, restores the
  verified member ID and disables redundant linking. The badge persists after
  reload; the check changes no identities or host routes and has no page errors.
  Polling/dialog closure remains covered by the existing browser tests; the user's
  original popup closing was not directly observed. RG-01/RG-03 remain incomplete.

Evidence: `.tmp-build/chat-slack-confirmation-repair/` contains `before.trx`
(one failure), `focused.trx` (63 passes), `backend.trx` (521 passes, one live skip),
`summary.json` (source hashes/documentation checks), `before-status.json` (read-only live
health/diagnostics and absent Slack identity), and test logs. Deployment evidence
adds `deploy-before.json`, `deploy.log`, `deploy-after.json`, `deployed-status.json`,
`live-proof.json`, `deployed-linked.json` and `deployed-linked-1440.png`.
The proof read uses query-only SQLite and omits challenge hashes/tokens. The
diagnostic checks sent no external message or identity mutation; the user's
approval created the link through the normal receiver. All three receivers remain
receiving. Slack's diagnostic count remains four at the old pre-deployment
timestamp, with no increase observed; that historical diagnostic is not erased.