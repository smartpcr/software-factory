# Chat plugin validation matrix execution

Started 2026-09-10 from `498ff3b8` (`chat plugin: stage 6`). This records execution
of [implementation plan section 4](chat-plugin-implementation-plan.md#4-validation-matrix).
Validation is in progress. Automated evidence and deployed-host evidence are
recorded separately; neither substitutes for the dedicated real-provider exercise.

## Release gate follow-up (2026-09-12)

Section 5 adds operator guides, exact permissions/rotation/API references and closes
the shared conformance audit. Backend regression: 486 passed, one explicit live
skip, including 37 shared contract cases. [Release evidence and pending checks](chat-plugin-release-gate.md)
are the current acceptance record; older run counts below retain their original dates.

## Deployment and host checks

**September 12 live setup/layout:** [detailed evidence](chat-slack-live-setup.md).
Slack consent, app installation, encrypted save and `#all-forge` membership are
verified. The new Settings layout is live (build + 33 browser cases, desktop/narrow
live checks). The subsequent channel check exposed a JSON transport problem;
its form-encoding correction passes 69 Slack tests and was deployed after approval
at 08:55 UTC: API PID 39092, UI PID 72132 retained. Real discovery/API validation
and the actual Settings UI now validate `#all-forge` successfully, with destination
access `ready` and connection saved/applied revision 4/4. Both `/api/health` paths
return HTTP 200 / `ok`; all three provider receivers recover. Desktop/narrow
Settings checks pass; identity and host-alert drafts leave saved state unchanged.

**Automatic Slack setup follow-up (deployed; live approval prompt verified):** an unregistered host can
open an in-app approval popup. After Slack's private permission prompt and returned
confirmation code, Forge creates/installs the app and obtains both credentials.
The shared OAuth path remains available for configured hosts.

- [x] Validate public/private manifests, owner/role/CSRF isolation, request audit
  redaction, expiry/cancel/replay, workspace conflicts, app-token validation,
  administrator policy and uncertain failures, and cleanup on storage failure.
  Backend regression: 457 passed, one live-provider skip, including 14 Slack setup
  cases and 35 existing authorization cases. Evidence:
  `.tmp-build/slack-connect/slack-regression.trx` and `backend-regression.log`.
- [x] UI build and all 30 isolated browser cases pass, including the new approval
  popup, clipboard/window fallback, reopening, narrow-screen controls and review/save.
  Escape closes only the nested popup and preserves the row-drawer draft.
  Evidence: `.tmp-build/slack-connect/browser-full.log` and
  `slack-approval-narrow.png`.
- [x] Deploy after explicit chat approval (September 12, 04:02 UTC): API PID
  70188 → 21464, UI PID 72132 unchanged. Direct/proxied health returns 200;
  anonymous readiness/setup returns 401. The live Connect button is enabled,
  the old registration notice is absent and Telegram connections/identity remain.
- [x] Record the live failure accurately: Forge's ticket request returned 400.
  The direct Slack endpoint returned `internal_error` without CLI identification
  and succeeded with it. Correct the missing header and provider-error mapping.
  Evidence: `.tmp-build/slack-connect/{deploy-first-after,deployed-smoke-first,protocol-probe}.json`.
  The correction passes all 50 authorization cases (15 Slack setup cases):
  `.tmp-build/slack-connect/slack-protocol-fix.trx` and `backend-protocol-fix.log`.
- [x] Deploy the header fix after the user's second explicit restart approval
  (September 12, 05:29 UTC): API PID 21464 → 54028; UI PID 72132 unchanged. The
  live Slack ticket request returns 200 and opens the actual in-app modal. Desktop
  and 390px checks confirm reachable controls, scrolling and close/reopen; the
  disposable flow is cancelled. No saved connections change, no page/API errors
  occur and the temporary operator session is logged out. Evidence:
  `.tmp-build/slack-connect/{deploy-after,deployed-smoke}.json`,
  `deploy-protocol-fix.log` and `deployed-slack-approval{,-narrow}.png`.
- [x] Reproduce and fix the external `about:blank` window when clipboard permission
  stalls. Open Slack directly without awaiting copying; provide a bounded manual
  fallback and ignore late clipboard results. All 31 browser cases and UI build
  pass. Verify the served UI with a real approval ticket and an intercepted Slack
  page: navigation succeeds with copying unresolved, and the fallback appears.
  Cancel the temporary flow and log out; no connections change or services restart.
  Evidence: `.tmp-build/slack-connect/{clipboard-before,browser-clipboard-full,ui-build-clipboard}.log`,
  `deployed-clipboard.json` and `deployed-clipboard-fallback.png`.
- [x] Add and validate workspace creation through Slack's official signup, with
  workspace discovery, popup fallback and a return prompt. Returning requests a
  fresh approval and retains connection choices. All 33 browser cases and UI build
  pass, including long signup, blocked windows, renewal failure/retry and row-draft
  preservation. Live HMR checks use an intercepted signup page and real ticket
  creation/renewal (both 200); narrow controls stay reachable. Test flows are
  cancelled and the temporary session logged out without changing connections
  or restarting services. Evidence:
  `.tmp-build/slack-connect/{browser-workspace-full,ui-build-workspace}.log`,
  `deployed-workspace.json` and `deployed-workspace-{choice,signup-narrow}.png`.
- [x] Verify the user's `forge` workspace and submit a fresh approval request in
  the existing `#all-forge` browser window using targeted desktop controls.
  Slack's actual **Slack CLI Authentication** permission dialog opens (September
  12, 07:18 UTC). No copied code or app configuration is required from the user
  for this assisted handoff. Consent was subsequently approved; the earlier rejection's
  original cause is not proven. Evidence:
  `.tmp-build/slack-connect/slack-real-permission-dialog.png` and `live-handoff.json`.
- [x] Complete user-approved consent, install the Slack app, save the encrypted
  connection and add it to `#all-forge`; discovery confirms channel membership.
- [x] Diagnose the channel validation failure and test the transport correction.
- [x] Restart after fresh approval, then revalidate the saved channel through Forge
  (September 12, 08:55–09:00 UTC). API PID 54028 → 39092. The corrected running
  request succeeds in the API and actual Settings UI. Record binary/source hashes,
  health, receiver recovery and the 1440px/390px UI checks in
  `.tmp-build/chat-release/{deploy-after,deployed-settings,settings-after-deploy}.json`.

**First-manager follow-up (deployed September 11):** Telegram's missing-registration
screen now has in-app setup and a **Verify and continue** action. The initial
BotFather registration/token handoff is required once; verification, encrypted host
configuration and subsequent consent are automated. Registration survives fresh
runtime state, rejects conflicting/external managers, rechecks the administrator,
and redacts request bodies in audit records. Broader backend regression: 442 pass,
one live-provider skip; final authorization checks: 35 pass. UI build, 197 unit tests
and all 22 browser cases pass. Evidence: `.tmp-build/chat-manager/` and matching
`.tmp-build/chat-manager-*.log` files. API PID 70188 now runs the new code after
the separately approved restart; UI PID 72132 was retained.

- [x] Implement and validate first-manager setup in Settings and the row drawer.
- [x] Deploy that endpoint after a new per-restart approval. Live readiness exposes
  `canConfigure: true` on the unregistered host. The setup panel, BotFather link,
  protected token field, selected-provider highlight and Close are verified.
  Direct/proxied health returns 200; anonymous readiness/setup returns 401.
  No configuration mutations, API errors or page errors occurred. At 390px the
  setup panel/Settings fit; the global topbar separately overflows before and after
  opening setup (847px document width). See `deployed-smoke.json` and adjacent
  screenshots in `.tmp-build/chat-manager/`.
- [x] Complete actual initial Telegram manager registration (September 11): enable
  Bot Management Mode through the selected bot's Desktop settings, save through
  the protected Forge endpoint, and verify live readiness `available: true` and
  the enabled Connect button. See `telegram-manager-live-verified.json`,
  `deployed-telegram-connect-ready.png` and `telegram-manager-mode-enabled.png`
  in `.tmp-build/chat-manager/`. Port 5180 still uses API PID 70188; no restart.
- [x] User completed real managed-bot consent/save. Live checks confirm one enabled
  Telegram connection and the user's five selected product grants.
- [x] Complete the reviewed host activation after explicit chat approval (22:06 UTC).
  Import commits revision 1; cutover commits revision 2 with `plugins` ownership.
  API PID 70188 is unchanged. History retains all 40 notification statuses, Host
  alerts are retained, and only the verified local parent is enabled with a
  noninteractive metadata subscription. Both unverified scope entries remain off.
- [x] Complete the new bot's personal identity proof through Telegram Desktop
  (22:10 UTC). Live Settings and the authenticated API confirm the link; receiver
  health is ready and host management is interactive. Evidence:
  [chat-plugin-activation-review.md](chat-plugin-activation-review.md) and
  `.tmp-build/chat-manager/{chat-activation-result,telegram-identity-linked}.json`.
- [ ] Select and verify a channel for the new Telegram connection. Manager
  registration, connection consent and personal identity proof do not close this gate.
- [ ] Resolve the remaining legacy scope/history repair issues separately. No
  failed historical messages were retried during activation.
- [x] Fix Telegram identity confirmation: reject usernames/group IDs, use the
  selected installation, open the confirmation popup and offer a visible fallback,
  refresh after provider proof and hide the raw Telegram command. Autofill preserves
  Slack workspace boundaries and leaves ambiguous Telegram identities unselected.
  UI build and all 27 isolated browser cases pass, including five identity cases.
  Evidence: `.tmp-build/chat-manager/identity-browser-final.log` and
  `identity-ui-build.log`. Live `identity-ui-live.json` / `identity-ui-live.png`
  confirm the saved identity and updated controls, with no chat mutations or page
  errors; the temporary operator session was logged out. Vite served the change
  without restarting either API PID 70188 or UI PID 72132.
- [ ] Remove first-time personal ID entry from the product flow. Autofill currently
  requires an existing verified identity; Desktop-assisted completion of this
  account does not close that remaining automation gap.

**September 11 authorization follow-up:** [native provider consent](chat-connection-authorization.md)
adds Slack OAuth and Telegram managed-bot creation with protected automatic credential
retrieval. The backend was deployed after separate approval on September 11: API
PID 81444 → 28340, with UI PID 72132 retained. Direct/proxied health returns 200;
paired authorization readiness returns 200, anonymous access 401, and invalid
callback state 400 with `no-store`. Both live Settings screens show the Connect/Close
controls and missing-registration notice without errors or configuration mutations.
Evidence is in `.tmp-build/chat-authorization/deployed-smoke.json` and the adjacent
deployment logs/screenshots. 426 backend checks pass (one live-provider test skipped),
197 UI unit tests and 20 browser cases pass. Both providers initially reported
`authorization-registration-required`; Telegram registration is now complete as
recorded above. Live Slack provisioning and consent are now complete; an HTTPS callback
is needed only for a shared OAuth registration. Telegram consent, host activation and identity proof are now
complete as recorded above.

- [x] Deploy the committed backend with `scripts/run-forge-ui.ps1 -ServerOnly -Rebuild`.
  The user explicitly requested deployment. API PID changed from 48456 to 75668;
  the loaded build reports `1.0.0+498ff3b847b99671a96f149e5c9927f8b8e75b30`.
- [x] API and UI proxy `/api/health` return `ok`; the existing Vite UI remains up.
- [x] Verify Phase 106's two tables in `E:/forge/forge.db` using a read-only SQLite
  connection. There is no migration-owner row and no configured plugin connection.
- [x] Verify the deployed host's authenticated configuration API using the supported
  local Windows sign-in flow. Its selected engine is `legacy`.
- [x] Reproduce the deployed migration-preview failure: HTTP 400 `invalid-scope`.
  One historical topic has an empty product ID. No live import or cutover was run.
- [x] Fix preview/import handling of unscoped historical topics and add two regression
  cases. They fail before the fix and pass afterward, including repeated import,
  held delivery, preserved topic address, and no granted parent/action authority.
- [x] Deploy that fix after the user's separate restart approval. API PID 81444
  serves `498ff3b8` plus the uncommitted scope repair. Actual migration preview now
  returns HTTP 200 with 40 notification records, three scopes and two topic repair
  issues. Owner remains `legacy`, phase `not-imported`, revision 0.
- [x] Verify the actual Workstreams UI in headless Edge using local Windows sign-in:
  14 Chat controls render, the remote-control drawer opens and Cancel closes it.
  No page errors or failing API responses were observed; no row settings were saved.

The changed backend files are `ChatMigrationService.cs` and
`ChatMigrationService.Import.cs`. Invalid historical identifiers now use tuple
keys for reporting and repair; valid configuration scope validation stays strict.

## Provider setup navigation repair

- [x] Reproduce the reported Set up connection failure after selecting either
  Telegram or Slack. The editor mounted below the visible drawer while the parent
  draft became disabled. Before the fix, the live Slack input began at y=1040
  outside the drawer body's y=127–828 viewport, with no input focus.
- [x] Scroll connection/channel setup into view and focus its first enabled
  control. Restore the original route button and scroll position after save or
  cancel, preserving the unsaved row draft.
- [x] Add four browser regressions for Telegram/Slack at 1280×720 and 390×844.
  All four fail before the fix. Visibility and focus are asserted before filling
  fields, because Playwright's fill would otherwise scroll the editor itself.
  Extend the existing connection/channel round trip to check focus after save,
  channel cancellation and keyboard reopening.
- [x] Re-run the UI build, 197 unit tests and all 12 chat browser cases successfully.
- [x] Verify both providers on the actual Workstreams page at 1440×900 and 390×844:
  fields are visible and focused immediately; Cancel restores focus and the draft;
  Enter reopens a fresh editor and Escape returns to the row. No chat mutations,
  failed Chat API responses or JavaScript page errors were observed. Vite serves
  the frontend fix through HMR; this repair required no service restart.

Evidence is under `.tmp-build/chat-slack-freeze/`: `before.json`, before/after
screenshots, four expected browser failures in `browser-before.log`, the 12 passing
cases in `browser-after.log`, UI build/unit logs and `after.json` for the four live
draft flows. No credentials or saved connection settings were changed.

- [ ] Investigate a separate live-page finding: background story-detail GET
  requests returned HTTP 403 `Capability denied` during the extended check.
  These are recorded in `after.json`; the successful Chat setup checks do not
  establish a clean result for the entire Workstreams page.

## Setup guidance and layout refinements (2026-09-11)

- [x] Place the compact Chat action after Pause and Delete. All three controls share
  the same size and vertical alignment; the tooltip and accessible description retain
  the Chat state and destination. Live measurements at 1920, 980 and 390 CSS pixels
  confirmed three 33×28 buttons in that order.
- [x] Keep the drawer's labeled Close button, Save and Cancel visible while its body
  scrolls. Connection/channel setup uses its own footer actions and restores the
  original row draft and focus when it finishes. The body has a visible 10px scrollbar.
- [x] Group connection details, credentials, product access and channel setup into
  collapsible sections. The final Enable connection control remains reachable above
  the footer at 390×620, even after opening the longer walkthrough.
- [x] Add provider-specific, step-by-step guidance to the shared connection editor.
  Slack includes two attributed documentation screenshots, an ID example, and copy/
  download controls for the repository's manifest. Telegram includes an illustrated
  token-to-field mapping. Both explain channel access, saving and verification.
- [x] Pass all 14 isolated browser cases, including two new walkthrough/scrolling
  cases. The screenshots/illustration load and the downloaded manifest contains the
  required settings. All 197 UI unit tests pass.
- [x] Verify both providers in the running Workstreams drawer and verify the shared
  guide, reachable Save and working Close in Settings → Integrations → Chat. No
  configuration writes, test sends or JavaScript page errors occurred; the settings
  screen's two preview POSTs only render samples. Vite serves the changes through HMR.

Evidence is under `.tmp-build/chat-settings-guide/`: `browser.log`, `ui-unit.log`,
`ui-build-final.log`, `live-smoke.json`, `settings-live-smoke.json`, row-action
screenshots, provider walkthrough screenshots and drawer-bottom screenshots. The
scrollbar was also inspected with the browser's headless scrollbar-hiding flag
removed. These checks do not close the separate live-provider or background-403 gates.

## Full Settings page, tabs and scrolling (2026-09-11)

- [x] Correct the full `/settings` page shown in the follow-up screenshot. The
  provider/maintenance area used a second flex scroll container inside the page's
  scroll area. Remove that wrapper and give Settings one visible 12px scrollbar.
- [x] Keep the Settings title, category tabs and labeled Close button outside the
  scrolling body. Close returns to the previous view, or Workstreams for a direct
  Settings link. Changing tabs does not add extra back-navigation entries.
- [x] Separate Chat and Repository providers into tabs; put Browser session and
  Maintenance under General. Sections collapse independently, drafts survive tab
  changes, and the selected tab survives reload. Arrow keys/Home/End navigate tabs.
- [x] Add two regressions using the actual `SettingsView` and shell dimensions at
  1440×620 and 390×620. Verify tab isolation, drafts, reload, Close, collapsing and
  reachable bottom content. The complete browser suite passes all 16 cases; both
  new cases pass again after the narrow provider-action layout refinement.
- [x] Pass the UI production build and all 197 UI unit tests.
- [x] Verify the actual `/settings` page at 1920×900, 980×720 and 390×620. Every tab
  has zero outer overflow and zero clipped horizontal width; its bottom is reachable
  while Close and the tabs stay in view. The visible scrollbar reserves 12px. Check
  both direct-link and previous-page Close navigation. No settings writes, provider
  test sends or JavaScript page errors occurred. Vite serves this update through HMR.

Evidence is under `.tmp-build/settings-page-fix/`: `browser.log`, `browser-final.log`,
`ui-build.log`, `ui-unit.log`, `live-smoke.json` and screenshots of each tab at the
three viewport widths. The earlier shared-editor checks did not cover the full
Settings page; the new fixture mounts that page to retain this regression coverage.

## Automated scenario evidence

All 28 scenarios are numbered below in the same order as the plan. A checked box
means that the listed automated evidence passed. Live validation remains below.
Methods are in `src/forge/Forge.Api.Tests/Chat` unless otherwise specified.

| # | Automated | Scenario | Evidence |
|---|---|---|---|
| VM-01 | [x] | All four current notifier kinds | `ChatValidationMatrixTests.All_four_notifier_kinds_deliver_only_to_their_explicit_parent_or_host_scope` exercises the actual notifier facade. `ChatMigrationBoundaryTests.Compatibility_endpoints_use_the_migrated_group_and_retirement_keeps_host_alerts_active` covers permanent Host alerts. |
| VM-02 | [x] | Zero matches and future kind IDs | `ChatRuntimeTests.Host_route_is_permanent_and_unknown_kinds_do_not_match_old_filters` and `ChatRuntimeCompletionTests.Reenable_does_not_replay_but_explicit_resend_revalidates_and_dedupes_request`. |
| VM-03 | [x] | Earlier failure, newer event and ordering | `ChatRuntimeTests.New_revision_supersedes_blocking_retry_and_old_tokens`; `ChatRuntimeCompletionTests.Failed_earlier_action_blocks_only_its_subscription_until_explicit_skip`. |
| VM-04 | [x] | Forged headers, empty users, all-products and aliases | `Auth/OperatorHttpSecurityTests`, `Auth/OperatorScopeSecurityTests` and `Auth/CapabilityParityTests` verify real cookies/CSRF and shared authority before effects. |
| VM-05 | [x] | Unsupported versions and duplicate registry IDs | `ChatPluginConfigurationTests.Unsupported_Versions_Preserve_Data_And_Pause_Only_Affected_Settings`; `ChatPluginEngineTests.Duplicate_Providers_Disable_Registry_While_Forge_Host_And_Health_Still_Boot`. |
| VM-06 | [x] | Prune, deletion, unavailable DB and batched summaries | `ChatRuntimeCompletionTests.Seed_scrub_reports_dry_run_then_retires_exact_deleted_product_scope`; runtime missing-DB/deletion tests; `ChatManagementTests.Summary_reads_exactly_one_control_query_per_chunk`. |
| VM-07 | [x] | Busy storage before and after provider success | `ChatRuntimeTests.Claim_failure_prevents_send_and_crash_does_not_replay_unknown_send`, `Successful_send_checkpoint_failure_retries_only_local_storage`; `ChatRuntimeBoundaryTests.Inbox_sqlite_busy_budget_refuses_ack_within_two_seconds`. |
| VM-08 | [x] | Unicode, oversized payload, secrets and expiry | `TelegramProviderTests`, `ChatSlackProtocolTests.All_parts_are_preflighted_and_invalid_payloads_have_visible_field_errors`, `ChatTelegramBoundaryTests.Render_failure_is_visible_before_any_provider_send`; runtime/slice retention tests. |
| VM-09 | [x] | Five top-level rows | `Forge.UI/e2e/chat/notifications.spec.ts`: five independent rows, explicit provider/channel selection, shared detail state, refresh and product switching; Telegram/Slack setup visibility, focus, illustrated walkthroughs and fixed actions at desktop/narrow sizes. Live compact-action geometry is recorded above. |
| VM-10 | [x] | 32 children and a future child | `ChatValidationMatrixTests.All_32_children_and_a_later_child_share_one_parent_conversation_per_destination` sends 66 deliveries through two unchanged parent bindings and verifies all 33 child work states remain running. |
| VM-11 | [x] | Planning before child creation | `ChatRuntimeTests.Planning_questions_route_before_child_creation_only_with_persisted_ownership`; migration artifact-ownership fixtures. |
| VM-12 | [x] | Two parents in one destination | `ChatPluginConfigurationTests.Two_Products_Same_Spawn_Id_Three_Connections_And_Future_Children_Stay_Isolated`; provider conversation-isolation tests; Telegram wrong-topic and Slack wrong-thread action tests. |
| VM-13 | [x] | Completed rows, cancellation, filtering and reorder | `ChatManagementTests.Completed_parent_and_current_future_children_keep_their_work_state`; five-row and cancellation/keyboard browser cases. |
| VM-14 | [x] | Pause/resume versus disabling Chat | `ChatTelegramInteractionTests.Status_and_pause_resume_use_parent_domain_core_and_leave_subscription_enabled` and `Off_paused_and_notifications_only_scopes_deny_domain_actions`; `Auth/SharedStoryActionTests`. |
| VM-15 | [x] | Reused IDs across products/workspaces/channels | Plugin configuration isolation, Telegram callback replay boundaries, Slack workspace/address boundaries and `Auth/CapabilityParityTests`. |
| VM-16 | [x] | Chat off for one parent and on for another | Plugin configuration isolation and migration selected-parent/future-parent tests preserve work state and route only opted-in scopes. |
| VM-17 | [x] | Telegram, Slack and another destination | `ChatRuntimeTests.Fanout_retries_only_failed_destination_and_keeps_successful_copy`; `ChatSlackRuntimeTests.Two_workspaces_share_one_live_socket_alongside_Telegram_and_disable_preserves_remaining_receiver` and cross-provider answer/receipt tests. |
| VM-18 | [x] | Connection/destination removed during I/O | `ChatRuntimeTests.Supervisor_cancels_changed_revision_restores_cursor_and_coalesces_health`; subscription retirement tests; `TelegramSliceTests` disabled/removed in-flight route cases. |
| VM-19 | [x] | Multiple questions in one conversation | `ChatTelegramInteractionTests.Ambiguous_text_lists_questions_and_explicit_answer_selects_exact_message`; Slack ambiguous/custom/decimal-selection tests. |
| VM-20 | [x] | Web answers and stale provider buttons | `ChatRuntimeTests.Web_answer_is_reconciled_to_all_provider_copies`; concurrent provider answers; Slack/Telegram shared receipt and all-copy retirement tests. |
| VM-21 | [x] | Expired/replayed links and restart confirmation | `ChatRuntimeCompletionTests.Restart_confirmation_is_short_actor_bound_and_expired_actions_never_mutate`; connection-bound single-use links and relinking tests. |
| VM-22 | [x] | Revoked identity or lost permission | `ChatPluginConfigurationTests.Capability_And_Connection_Grants_Are_Rechecked_And_Duplicates_And_Stale_Writes_Reject`; auth cached-success/revocation and migrated-link proof tests. |
| VM-23 | [x] | Rate limits, socket refresh, credentials and outage | Telegram failure-classification/cancellation tests; Slack socket handshake/reconnect tests; `ChatSlackRuntimeTests.Repairable_failures_are_connection_local_and_host_retry_recovers_without_recreating_a_known_root`. |
| VM-24 | [x] | Receipt durability and process interruption | `ChatRuntimeTests.Inbox_receipt_and_cursor_precede_ack_and_domain_work`; mixed-batch cursor test; Slack duplicate-envelope/withheld-ack and Telegram checkpoint recovery tests. |
| VM-25 | [x] | Domain effect before control checkpoint | `ChatRuntimeTests.Mutation_before_control_checkpoint_recovers_from_product_receipt`; Telegram command/response recovery tests verify no repeated effect. |
| VM-26 | [x] | Shared legacy topics and old controls | Migration alias/choice/revision/ownership tests, including the new unscoped-topic regression. The actual host preview now returns HTTP 200 with repair issues after the approved deployment. |
| VM-27 | [x] | UI, helper and launcher configuration | UI setup browser cases including walkthrough assets, manifest download, full Settings tabs/Close/scrolling, collapsible sections and connection/channel focus restoration after save/cancel; `ChatHostConfigurationTests` executable helper/launcher fixtures; actual launcher deployment and authenticated configuration read. |
| VM-28 | [x] | Secrets in provider exceptions and URLs | Provider safe-failure tests; `ChatSlackRuntimeTests.Slack_credentials_are_redacted_even_when_not_the_current_connections_secret`; authorized/redacted management tests. |

## Runs and qualifications

- [x] Initial combined backend run: **404 passed, one live test skipped**.
- [x] New migration regression: **two passed**, after two expected failures before
  the fix. Tests use temporary legacy rows, not copied production content.
- [x] New direct matrix scenarios: **two passed**.
- [x] Final combined backend regression after the fix and new scenarios: **408
  passed, one live test skipped** (409 total). All four added regression/matrix
  cases are included.
- [x] UI production build succeeds; existing bundle-size warnings remain.
- [x] UI unit suite: **197 passed** across 21 files.
- [x] Isolated Edge browser suite after the full Settings page/tab repair: **16 passed**. API requests are intercepted;
  screenshots and fixture results do not prove live provider behavior.
- [x] Shared provider smoke contract runs for FakeAlpha, FakeBeta, Telegram and Slack;
  provider-specific protocol/HTTP/WebSocket suites pass in the combined run.
- [x] Complete the shared-suite coverage audit (section 5, September 12):
  `ChatProviderConformanceTests.Release.cs` adds the same pagination, conversation/
  retirement, rendering/limits, failures, cancellation and durable receipt assertions
  for FakeAlpha/FakeBeta/Telegram/Slack. Two cancellation regressions were fixed.
  All 37 shared cases pass in the 486-test backend regression. See the
  [release report](chat-plugin-release-gate.md) and `.tmp-build/chat-release/backend.trx`.
  This closes automated conformance, not live-provider acceptance.
- [x] Final whitespace, documentation links and checkbox synchronization review.

September 12 Slack identity follow-up: 12 new protocol/runtime cases bring the
selected backend regression to **498 passed, one live skip**. The updated browser
suite passes **37 checks**; UI build and **197 unit tests** pass. Private approval
uses the existing `chat:write` permission; user/workspace/revision mismatches,
replay, cancellation, expiry, replacement, target grants and safe provider errors
are covered. The [follow-up checklist](chat-plugin-release-gate.md#slack-identity-confirmation-follow-up-2026-09-12)
separates source/browser validation from deployment and the pending real click.
The approved 09:41 UTC restart deployed this follow-up in API PID 43620, retaining
UI PID 72132. Health, authenticated capability/channel lookup, anonymous rejection,
three receiving provider sessions and desktop/narrow Settings checks pass. The
personal Slack account remains unlinked; the live read-only checks did not request
a private message. Evidence: `.tmp-build/chat-identity/{deploy-after,deployed-identity}.json`.
VM-02/VM-22 have added automated evidence; the live release gates remain open.

The subsequent [member-ID recovery correction](chat-plugin-release-gate.md#slack-member-id-recovery-2026-09-12)
restores the same operator's prior input while retaining workspace/connection
boundaries, ownership confirmation and provider proof. It was deployed after
approval at 21:23 UTC: API PID 43620 → 36144, UI PID 72132 retained. The actual
Settings page restores the saved ID without typing, including after reload.
Ownership starts unchecked; routes and identities remain unchanged. Evidence:
`.tmp-build/chat-identity-autofill/{deploy-after,deployed-autofill}.json`.
Ten new authenticated backend cases and two isolated browser cases pass, bringing
the selected regression to 508 backend passes (one live skip), 197 UI unit tests,
39 browser checks and a passing UI build.

The user then saw the private confirmation in Slack, but Forge did not accept
the click. The receiver reports `slack-interaction-invalid`; the fixture had
incorrectly included message metadata that Slack omits for ephemeral messages.
The [callback repair checklist](chat-plugin-release-gate.md#slack-private-confirmation-callback-repair-2026-09-12)
records the failing reproduction, corrected fixture and 521 backend passes (one
live skip). All 13 added protocol cases and 12 identity-confirmation cases pass.
The approved 21:41 UTC restart deployed the repair in API PID 35552. The user's
private approval was processed at 21:42:30 UTC and its challenge consumed; the
actual Settings badge remains Linked after reload. Evidence is under
`.tmp-build/chat-slack-confirmation-repair/`, including `live-proof.json` and
`deployed-linked.json`. VM-02/VM-22 gain format-specific and actual-provider
identity evidence; RG-01/RG-03 remain open for the other required exercises.

The existing `Catalog_ClassificationMatchesDbBoundary` test remains excluded for
its pre-existing Phase 83 `repos` classification mismatch. Phase 106's model,
catalog, runner and boundary checks are included. This is a selected backend
regression suite, not the whole repository's integration suite.

Commands and local evidence:

```powershell
# Repository root; isolated outputs avoid a second service restart for tests.
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage6/artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)' `
  --logger 'trx;LogFileName=backend-final.trx' `
  --results-directory E:/work/github/crp/workflow/.tmp-build/chat-matrix

# From src/forge/Forge.UI; latest run includes the full Settings page and tabs.
npm run build
npm test
$env:FORGE_CHAT_BROWSER_CHANNEL = 'msedge'
npx playwright test --config playwright.chat.config.ts `
  --output ../../../.tmp-build/settings-page-fix/browser-results
```

Ignored evidence is under `.tmp-build/chat-matrix/`: deployment before/after JSON,
launcher/build output, read-only schema checks, backend TRX/logs, UI build/unit logs
and browser results. Browser screenshots use the fixture's existing
`.tmp-build/chat-stage5/` paths. No credential/session token is included in the
deployment or API summaries.

Normal host startup also refreshed `src/forge/Forge.Api/Agents/Models/models.json`
from its live catalog. That generated change was left intact and was not part of
the chat repair. No commit was created.

## Real-provider and operator gates

- [ ] Identify dedicated Telegram and Slack test destinations. The host now has
  two Telegram connections and an assisted Slack installation; operational
  connections are not automatically designated disposable test credentials.
- [ ] Complete connection and workstream setup through the actual UI for both.
- [ ] Enable redacted question content explicitly for the test product and both
  subscriptions; verify the effective setup policy before answer/button tests.
- [ ] Send separate questions to both providers and answer one in each.
- [ ] Verify all copies resolve consistently and a reversible stalled action is
  scoped to the selected test workstream.
- [ ] Rotate the test credentials and verify receiver/delivery recovery.
- [ ] Exercise two Slack workspaces sharing one app receiver while Telegram stays
  active; verify destination-failure isolation, disabling one workspace and reconnect.
- [ ] Record actual results for the earlier Stage 1S manual test-bot gate, using its
  required isolated profile rather than a production receiver.
- [x] Perform the reviewed actual host import/cutover after operator approval;
  migration revision 2 and preserved history are recorded in the activation review.
- [ ] Perform an actual host rollback only after the operator selects the boundary;
  isolated rollback fixtures are complete and documented.

The original September 10 validation sent no real test messages and performed no
cutover. Subsequent approved activation and assisted installations are recorded
above. The September 12 section 5 implementation pass performed no restart; the
subsequent user-approved deployment is recorded above. No provider test message,
credential rotation, commit or push has been performed for section 5. See the [release gate](chat-plugin-release-gate.md)
for the current combined regression and remaining live checks.