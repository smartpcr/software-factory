# Chat plugin validation matrix execution

Started 2026-09-10 from `498ff3b8` (`chat plugin: stage 6`). This records execution
of [implementation plan section 4](chat-plugin-implementation-plan.md#4-validation-matrix).
Validation is in progress. Automated evidence and deployed-host evidence are
recorded separately; neither substitutes for the dedicated real-provider exercise.

## Deployment and host checks

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
| VM-09 | [x] | Five top-level rows | `Forge.UI/e2e/chat/notifications.spec.ts`: five independent rows, explicit provider/channel selection, shared detail state, refresh and product switching; Telegram/Slack setup visibility and focus at desktop/narrow sizes. |
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
| VM-27 | [x] | UI, helper and launcher configuration | UI setup browser cases including connection/channel focus restoration after save/cancel; `ChatHostConfigurationTests` executable helper/launcher fixtures; actual launcher deployment and authenticated configuration read. |
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
- [x] Isolated Edge browser suite after the navigation repair: **12 passed**. API requests are intercepted;
  screenshots and fixture results do not prove live provider behavior.
- [x] Shared provider smoke contract runs for FakeAlpha, FakeBeta, Telegram and Slack;
  provider-specific protocol/HTTP/WebSocket suites pass in the combined run.
- [ ] Complete the shared-suite coverage audit: the two older comprehensive
  parameterized methods still enumerate only FakeAlpha/FakeBeta. Telegram and Slack
  have protocol coverage, but that is not yet the same shared assertion set for all
  capabilities requested by section 4. Do not declare the interface gate complete.
- [x] Final whitespace, documentation links and checkbox synchronization review.

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

# From src/forge/Forge.UI; latest run includes the provider setup navigation repair.
npm run build
npm test
$env:FORGE_CHAT_BROWSER_CHANNEL = 'msedge'
npx playwright test --config playwright.chat.config.ts `
  --output ../../../.tmp-build/chat-slack-freeze/browser-results
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

- [ ] Identify a dedicated Telegram test bot/destination and Slack test app,
  workspace and channel. The deployed host currently has no plugin connections;
  existing legacy credentials are not automatically designated test credentials.
- [ ] Complete connection and workstream setup through the actual UI for both.
- [ ] Send separate questions to both providers and answer one in each.
- [ ] Verify all copies resolve consistently and a reversible stalled action is
  scoped to the selected test workstream.
- [ ] Rotate the test credentials and verify receiver/delivery recovery.
- [ ] Record actual results for the earlier Stage 1S manual test-bot gate, using its
  required isolated profile rather than a production receiver.
- [ ] Perform an explicitly selected actual host import/cutover and rollback only
  when the operator chooses the target parents and rollback boundary.

No real provider test message, import, cutover, role grant, credential rotation,
commit or push was performed during this validation run. Deployment starts the
normal host services with their existing configuration.