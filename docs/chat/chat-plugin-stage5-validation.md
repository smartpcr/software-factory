# Stage 5 — guided chat setup validation

Date: 2026-09-10. Baseline: `7c752cd8` (`chat plugin: stage 4`).

All 14 Stage 5 implementation requirements are covered. Stage 4's connection-repair
UI dependency is also covered. The approved real-provider integration exercise and
the earlier Stage 1S live exercise remain pending; fixture results do not close them.

## Requirement mapping

September 11 identity follow-up: Telegram confirmation now opens directly with a
visible fallback when blocked. The form validates personal numeric IDs, fixes the
installation to the selected connection, preserves provider/workspace identity
boundaries and refreshes after proof. All 27 isolated browser cases and the UI build
pass; live Settings verifies the new form and the linked account without chat
mutations or a service restart. Evidence: `.tmp-build/chat-manager/identity-browser-final.log`,
`identity-ui-build.log` and `identity-ui-live.{json,png}`. First-time numeric ID
discovery remains open in the implementation plan; this account was linked using
Desktop assistance and genuine provider proof.

| # | Status | Requirement and implementation evidence |
|---|---|---|
| 1 | [x] Complete | `ChatPluginEndpoints`, `ChatConfigurationService.Setup`, `ChatManagementService` and `ChatConnectionCheckService` expose explicit management, credential, preview, setup, summary, history and verification operations. Shared capability checks, paired sessions/CSRF, expected revisions and secret references apply. HTTP tests reject readers, other products and stale revisions; credentials never return in DTOs. |
| 2 | [x] Complete | `SettingsView` mounts `ChatIntegrations`. Connections may be saved disabled with missing credentials and completed later. The verification panel separates credential storage, channel access, outbound checks, inbound replies and applied revisions; identity and host/product permissions are separate. |
| 3 | [x] Complete | Permanent `HostAlerts` uses the existing host scope and its separate capability. Default filters are queue-strain/human-interaction. It exposes unrouted counts, orphaned scopes, policy selection, redacted previews, history and retirement copy. |
| 4 | [x] Complete | `SwarmStoryRow` exposes `ChatRowControl` for every row, including done rows. The 2026-09-11 layout refinement places its compact icon after Pause/Delete at the same size and vertical position; its status dot, tooltip and accessible description retain off/needs-setup/ready/degraded and a destination name or count. |
| 5 | [x] Complete | The shared drawer is used by row and `SwarmStoryView` header controls, including the collapsed header. Native dialog focus containment, explicit close-before-focus-return, click/key propagation boundaries and a scrolling body keep the footer accessible. |
| 6 | [x] Complete | Enable chat remote control → Provider → Channel → Save and enable is the default interactive flow. Notification-only is explicit. The connection selector appears only for multiple choices. Atomic save queues introductions/conversations without executing a work action. |
| 7 | [x] Complete | `ChatConnectionEditor` and `ChatDestinationSetup` return to the same row/draft. Discovery is paged and filterable by name; save validates channel access. Changing provider clears incompatible channel state. Cancel does not save the row or provision a conversation. Explicitly saved connection drafts remain available. |
| 8 | [x] Complete | Spawn list/detail DTOs and a product batch endpoint share `ChatSummaryService`. `chatStore` keys by product/spawn, rejects older revisions, coalesces requests and refreshes after writes. List and detail controls render the same saved state. No row invokes a provider. |
| 9 | [x] Complete | Summary aggregation is one parameterized control query per 100 existing parent rows. Tests verify exactly 1/1/3 queries for 5/100/201 rows. Row detail requests explicitly omit chat aggregation; HTTP tests verify zero chat queries for those reads and one for detail/batch summary reads. |
| 10 | [x] Complete | Explicit copy fills a destination draft and requires Save. Backend tests cover all 32 existing children, a later child, completed parents, and one shared conversation per provider. Telegram topics and Slack roots are created by Save's outbox and reused by later work events. |
| 11 | [x] Complete | Self-link challenges require user confirmation in the UI. Link state is checked against the selected provider installation, with revoke/re-link controls and expiry. Notifications remain usable without a link; work actions retain the shared domain authorization boundary. |
| 12 | [x] Complete | Existing live revision supervision is used for affected connections and subscriptions. Unchanged subscriptions keep their revisions. Fresh protected blobs avoid overwriting shared credentials. UI copy distinguishes live revisions from externally managed credentials and engine/environment/content limits requiring a host refresh. |
| 13 | [x] Complete | Explicit checks persist an uncertain intent before sending and reuse a request ID without replay. Both real adapters are tested against local protocol fixtures for send/reply verification. Checks are revision-scoped, contain no domain actions, and expire for reply matching. Read/validate/preview never send. History exposes next attempt, safe failure reasons and explicit failed-delivery retry. |
| 14 | [x] Complete | `setup-telegram-bridge.ps1` uses paired, CSRF-protected Forge APIs. It performs no desktop install/sign-in, provider polling, file patching or host restart. A PowerShell fixture executes normal and explicit-test paths and verifies that only `-SendTest` sends a check. |

## Automated evidence

- [x] 381 backend tests passed; one pre-existing live-provider test skipped.
- [x] 12 new backend cases: 11 management cases (including query-count/provider theories) and one PowerShell helper case.
- [x] The final helper correction passed both focused helper/parser cases; it preserves paused routes when adding a destination.
- [x] 197 frontend unit tests passed, including three cache ownership/concurrency tests.
- [x] Eight Edge Playwright cases passed against an isolated Vite fixture with all API calls intercepted.
- [x] TypeScript/Vite production build passed. Existing bundle-size warnings remain.
- [x] No new schema migration is needed. Configuration uses the existing control tables; explicit checks use the isolated `plugins-check` outbox discriminator and bounded retention.
- [x] Per-line endings, whitespace and requirement/checklist synchronization reviewed.

The five-row fixture uses BEYOND-AST (32 children), 3D-RENDER, MCP (done), AGENT
MEMORY and PR INGESTER. It verifies Telegram/Forge Ops, Slack/#render, off, both,
and off respectively; persistence, filtering, reorder, product switching, shared
header state, copy/cancel, inline setup return, stale revisions, keyboard focus,
mobile footer visibility, host scope, identity confirmation/revocation, and membership,
archived-channel and revoked-token repair. Domain execution is verified separately
in the actual host/provider fixtures; browser HTTP mocks do not certify a live host.

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --artifacts-path E:/work/github/crp/workflow/.tmp-build/chat-stage5/artifacts `
  --filter 'FullyQualifiedName~Forge.Api.Tests.Chat.|FullyQualifiedName~Forge.Api.Tests.Auth.|FullyQualifiedName~PhaseMigrationRunnerTests|FullyQualifiedName~SplitContextsTests|(FullyQualifiedName~PhaseMigrationCatalogTests&FullyQualifiedName!~Catalog_ClassificationMatchesDbBoundary)' `
  --nologo -v quiet --logger 'console;verbosity=normal'

# From src/forge/Forge.UI
npm run build
npm test
$env:FORGE_CHAT_BROWSER_CHANNEL = 'msedge'
npx playwright test --config playwright.chat.config.ts
```

Ignored local evidence is in `.tmp-build/chat-stage5-*.log` and
`.tmp-build/chat-stage5/` (five-row, shared-header, settings and mobile screenshots,
browser results and isolated build artifacts). The known historical
`Catalog_ClassificationMatchesDbBoundary` exclusion is unchanged: Phase 83 classifies
`repos` differently from the boundary catalog. All selected migration/split checks pass.

## Gaps found and addressed

- [x] The old row panel was Telegram-only and polled per row. Shared batched state and the guided drawer replace it; the notification-only test slice keeps its API path through the new setup facade.
- [x] Existing row detail fetches would still repeat chat aggregation. They now request `includeChatSummary=false`; query-count tests cover the HTTP path.
- [x] A multi-destination sequence of separate writes could leave partial activation. One transaction validates all destinations and guards the parent revision before committing.
- [x] Enabling initially waited for a work event to create the conversation. The same save transaction now queues a content-free introduction, using existing durable provisioning and send recovery.
- [x] Saving one changed route could revise every subscription. Unchanged routes now retain their revisions and in-flight work.
- [x] Closing a modal before unmounting could lose focus to the document. Explicit native close restores focus to the row trigger.
- [x] Refreshing immediately on every unauthorized response could cause a fetch loop. Session changes clear cached data without recursively refetching.
- [x] Initial list/detail data could replace newer cached health or row names at the same configuration revision. Initial data now seeds missing/newer revisions only; copied rows retain their display names.
- [x] Old successful checks could appear to verify replacement credentials. Checks and verification displays now match current connection/destination revisions.
- [x] Repair text initially used generic Slack error names. It now matches the adapter's actual safe membership, archive, bot-token and app-token codes.
- [x] Adopting a new revision automatically after a failed connection save could overwrite concurrent edits. Reload is an explicit action; failed drafts keep their original revision.
- [x] Adding a helper destination could unpause an existing route. The helper now preserves its paused state; its execution fixture covers this path.
- [x] Editing the identity target could leave an earlier challenge visible. Target changes now clear the challenge and require fresh confirmation; browser acceptance covers it.

## Remaining gates

- [ ] Approved real Slack workspaces and Telegram test bot: simultaneous providers,
  concurrent answers, cross-copy resolution, failure isolation, shared app receiver,
  reconnect and continuing Telegram reception. This is the plan's separate live gate.
- [ ] Earlier Stage 1S real-bot/manual exercise.
- [x] Stage 6 implementation and isolated migration/rollback rehearsal: see
  [Stage 6 validation](chat-plugin-stage6-validation.md). Actual host cutover remains explicit.

This work does not restart the running Forge.Api host or contact real providers.
The new backend services, routes and summary behavior require an operator-approved
host restart to activate. No commit or push was performed.