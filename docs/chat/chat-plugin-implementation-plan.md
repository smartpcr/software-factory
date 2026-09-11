# Forge workstream chat plugins — implementation plan

**Status:** Stages 0A, 0B, 1, 2, 3, 4, 5 and 6 implemented. All 11 Stage 6 requirements are covered, with 404 passing backend checks and one skipped live-provider test. Stage 5's UI evidence remains 197 unit tests and eight browser cases. The Stage 1S manual exercise and separate real-provider integration gate remain pending; no real host cutover was performed. Updated 2026-09-10. See [Stage 6 validation](chat-plugin-stage6-validation.md) and the [migration guide](chat-plugin-migration.md).
**Design:** [chat-plugin-design.md](chat-plugin-design.md).
**Baseline examined:** `70103c83`.

This document plans the implementation of the requested workstream plugin model,
multiple chat providers/connections/channels, and Slack support. Implementation
checkboxes record completed implementation with stage-specific evidence; checked
review items record documentation resolutions only. Live gates remain separate.
Historical completed phases in the older chat plans do not count as completion of
this revision.

**Scope correction from the supplied Workstreams screenshot:** each of its five
visible top-level rows (BEYOND-AST, 3D-RENDER, MCP, AGENT MEMORY, PR INGESTER) owns
one Chat / Remote control configuration. Those rows are `StorySpawnRow` entities
rendered by `SwarmStoryRow`, with child `WorkstreamRow` graph nodes inside them.
Persist configuration by **`(ProductId, SpawnId)`** and keep child workstream IDs
only as action targets. This replaces the earlier draft's child-level enablement.

---

> ## 🔍 REVIEW — all 6 comments fixed (round 1, 2026-09-09)
>
> Inline comments are tagged `REVIEW-P-nn` and placed in the stage they attack.
> Every code claim cites `path:line` verified at review time. Tick a box when the
> plan is updated (or record an explicit "won't fix" with reasoning inline).
> Design-document comments are tagged `REVIEW-D-nn` in
> [chat-plugin-design.md](chat-plugin-design.md). Search the tag to jump to a comment.
>
> **Resolution status:** fixed/checked review items mean the documents were revised.
> This review pass did not implement code. Subsequent Stage 0A implementation and
> isolated validation are recorded below. Original comment text is retained.
>
> | Tag | Severity | Section | Summary | Status |
> |---|---|---|---|---|
> | `REVIEW-P-01` | 🔴 Blocking | §3 Stages 1–2 | Acceptance gates depend on providers that later stages build | Fixed |
> | `REVIEW-P-02` | 🔴 Blocking | §3 Stage 2 | No work item builds the authorization the design mandates (see `REVIEW-D-01`) | Fixed |
> | `REVIEW-P-03` | 🟠 Major | §3 | No thin end-to-end slice; no estimates, sequencing budget or risk register | Fixed |
> | `REVIEW-P-04` | 🟠 Major | §3 Stage 6 | A fix-now repo-hygiene issue is deferred behind six stages | Fixed |
> | `REVIEW-P-05` | 🟠 Major | §2 | New tables must be registered in three places; one mega-phase is unreviewable | Fixed |
> | `REVIEW-P-06` | 🟡 Minor | §4 | No shared provider conformance suite | Fixed |

---

## 1. Requirements and completion criteria

| Requirement | Planned result | Acceptance gate |
|---|---|---|
| Chat remote control on each visible Workstreams row | Visible row control opens provider/channel configuration owned by product/spawn | All five example rows can be configured independently without selecting a child node; changing Chat leaves execution running |
| One or more chat frameworks and channels | Telegram and Slack provider projects; multiple named connections; multiple subscriptions per workstream | The same event reaches two Telegram destinations and a Slack channel, with independent health/retry |
| Slack implementation | Workspace app setup, Socket Mode inbound, Web API outbound, threads, questions/actions | A Slack thread reply or button resolves the correct Forge question/action and updates all destination copies |
| Better setup and operations | Row drawer: Enable remote control → Provider → Channel → Save and enable; inline connection setup and health | Setup requires no numeric ID hunting, source-file editing, child-node navigation, or agent restart |
| Control the complete row's work | Parent status/pause/resume; scoped child/artifact answers and stalled actions | Existing and future children use one parent conversation; a target selector prevents ambiguous actions |
| Preserve existing Telegram usage | Explicit migration from the current configuration, outbox, cursor, links and spawn topics | Cutover keeps existing message correlation and does not resend delivered alerts |

Scope includes parent status/pause/resume, planning/child questions and stalled
actions, plus a permanent **Host alerts** subscription for crew-wide `queue-strain`
and account-scoped `human-interaction`. Unresolved/standalone events are visible in
host attention. Free-form agent chat, a plugin marketplace, additional providers,
public Slack distribution/OAuth hosting and a generalized global alert center are
outside scope. The small Host alerts setup panel is included, not deferred.

## 2. Code ownership and planned changes

Paths marked **new** are proposed files/projects, not existing implementations.

| Area | Existing code | Planned work |
|---|---|---|
| Authentication/authorization prerequisite | `Auth/{ForgeUser,RequireRole}.cs`, `Persistence/ProductScope.cs`, user/role mutation endpoints | Authenticated host-paired sessions, stable user identities and `ForgeCapabilityAuthorizer`; protect web/chat action and grant/role mutation paths before remote control |
| Shared chat contract | `src/Common.Chat/{IChatTransport,ChatTransportOptions,ChatChannelMap,ChatInboundPump,ChatServiceCollectionExtensions}.cs` | Add provider registry/session contracts, capability descriptors, semantic messages and normalized inbound envelopes; retire Telegram assumptions after compatibility migration |
| Telegram | `src/Common.Chat.Telegram/`, `src/Common.Chat/ChatTokenResolver.cs` | Stage 3 extracted protocol/rendering; legacy adapter retained; connection-specific state and common secret-store seam |
| Slack | `src/Common.Chat.Slack/` | **Implemented** `src/Common.Chat.Slack/{SlackProviderPlugin,SlackConnectionSession,SlackApiClient,SlackSocketReceiver,SlackMessageRenderer}.cs` |
| Workstream plugins | No workstream plugin model exists | **New** minimal `src/forge/Forge.Api/Plugins/{IWorkstreamPlugin,WorkstreamPluginRegistry}.cs`; explicit `story-spawn` scope; `ChatWorkstreamPlugin` consumes parent subscriptions |
| Routing and delivery | `Services/Chat/{ChatNotifier,ChatBridgeService,ChatInboundDispatcher,ForgeChatRoutes,ForgeChatChannelStore,ForgeChatCursorStore}.cs` | Scoped subscriptions, independent deliveries, inbox processing, operator linking and connection supervision |
| Domain actions | `Services/{OpenQuestionService,OperatorActionService}.cs`, `Endpoints/StoriesEndpoints.cs` (`PauseSpawn`, `ResumeSpawn`, artifact actions) | Shared authorization, revisions and receipts; extract required parent/artifact action cores so HTTP and chat share behavior |
| Persistence | `Persistence/{Entities,ControlDbContext,PhaseMigrationCatalog,PhaseMigrationRunner}.cs`, `Persistence/Split/DbBoundary.cs` | Add control tables and product action receipts; catalog each table in the correct DB boundary |
| API | `Endpoints/{ChatBridgeEndpoints,StoriesEndpoints}.cs`, spawn DTOs, `Program.cs` | Product/spawn plugin APIs, spawn list/detail chat summaries, connection/link/health APIs and compatibility wrappers |
| UI | `src/forge/Forge.UI/src/swarm/{SwarmIndexView,SwarmStoryRow,SwarmStoryView}.tsx`, `views/SettingsView.tsx`, `api/forgeApi.ts`, spawn state | Shared drawer under **new** `swarm/plugins/`; row Chat control, provider/channel picker and batched per-row status; permanent Host alerts settings |
| Producers/lifecycle | `DeliverableLoop`, `WorkstreamLivenessMonitor`, `CrewQueueWorker`, auth producers, workstream/spawn lifecycle services | Resolve parent spawn for child and planning/artifact events; retain exact target; retire configuration on parent deletion |
| Setup and docs | `scripts/setup-telegram-bridge.ps1`, `scripts/run-forge-ui.ps1`, `docs/chat` | Make setup use application APIs; remove conflicting config export after cutover; add current Telegram/Slack operator guides and Slack manifest |

Keep each stage independently reviewable. Before allocating migrations, inspect
the current catalog for the next available IDs; do not assume the number after
Phase97 is still free. Update solution/project references and package declarations
using existing repository conventions.

<a id="schema-registration"></a>
### Schema increments and per-table registration

Allocate fresh phase IDs immediately before each change. Each increment below is
a separate control migration; the product receipt increment has its own product
phase. Keep creation additive and defaults compatible with the disabled plugin
engine. Stage 1S deliberately reuses the original single-delivery outbox shape on
a fresh profile; Stage 2 adds the multi-delivery model. No legacy-data import occurs
until Stage 6.

For **every table in every row** of this checklist, completion requires all four:
`PhaseMigrationCatalog` phase/table mapping; `PhaseMigrationRunner` ordering and
execution wiring; `DbBoundary` entry with kind/strategy; EF mapping/indexes in the
matching context. Run migration-idempotency and split dry-run checks and assert no
`no-partition-strategy` result. Existing altered tables retain their original
boundary classification and are also listed in the altering phase's catalog.

| Planned increment | Per-table registration checklist | Boundary |
|---|---|---|
| Stage 1 configuration (Phase98) | [x] `spawn_plugins`; [x] `chat_connections`; [x] `chat_connection_products`; [x] `chat_destinations`; [x] `chat_subscriptions`; [x] `chat_conversation_bindings` | Each `Control` / `Global` |
| Stage 2 delivery (Phase100) | [x] existing `chat_notifications` alterations; [x] `chat_subscriptions.ContentMode` alteration; [x] `chat_deliveries`; [x] `chat_delivery_messages` | Each `Control` / `Global` |
| Stage 2 reception (Phase102) | [x] `chat_inbox`; [x] `chat_connection_state`; [x] `chat_receiver_state` | Each `Control` / `Global` |
| Stage 2 identities/actions (Phase103) | [x] `chat_operator_links`; [x] `chat_link_challenges`; [x] `chat_action_tokens` | Each `Control` / `Global` |
| Stage 2 domain receipts (Phase101, product) | [x] `chat_action_receipts`, with required `ProductId` | `Product` / `DirectProductId`, partition column `ProductId` |
| Stage 6 cutover (Phase106) | [x] `chat_migration_state`; [x] `chat_migration_items`; [x] existing `chat_delivery_messages` alias fields reused | Each `Control` / `Global` |

`chat_action_receipts` is the only new **product-DB** chat table. Do not put it in
a control or mixed phase. Authentication enrollment is an OS-protected host store
and browser sessions are ephemeral; those do not add an undeclared product table.
Leaving additive control tables in place is safe when selecting the legacy engine;
never drop data as an engine rollback step. Product receipt compatibility requires
the shared action-boundary build to stay deployed: an older binary that bypasses
authorization is not a supported rollback. Schema-version checks pause unsupported
chat features without preventing Forge boot.

> <a id="review-p-05"></a>
> ### 🟠 REVIEW-P-05 — Major: table registration is a three-place contract, and one mega-phase is unreviewable [FIXED]
>
> **Status: FIXED in documentation (2026-09-09).** Added a per-table registration matrix for catalog, runner, DbBoundary and EF; separated schema increments and the sole product receipt phase; documented additive-schema rollback compatibility. See [schema registration](#schema-registration).
>
> The warning about migration IDs is correct and still current — the highest
> existing migration is `Persistence/Phase97Migration.cs`, so 98 is the next free
> number today. But adding a table is not just a migration: each of the ~15 proposed
> control tables must be registered in **three** places, or the split tooling
> reports it. The four existing chat tables show the pattern:
> - `Persistence/PhaseMigrationCatalog.cs:333-346` — phase → table mapping
> - `Persistence/PhaseMigrationRunner.cs:120-126` — runner ordering
> - `Persistence/Split/DbBoundary.cs:112-115` — `DbKind.Control`,
>   `PartitionStrategy.Global`; unregistered tables surface via
>   `Persistence/Split/SplitDryRunService.cs` (`no-partition-strategy`).
>
> The plan also needs to say that `chat_action_receipts` is the **only** product-DB
> table here, so it needs a separate `PhaseMigrationKind.Product` spec and a
> partition strategy — mixing it into a control phase will be wrong by construction.
>
> - [x] Add an explicit per-table checklist item covering all three registration
>       points plus the boundary kind.
> - [x] Split the schema work into one migration phase **per stage** (Stage 1 config
>       tables, Stage 2 delivery/inbox tables, Stage 6 migration state) rather than
>       one ~15-table phase. A 15-table phase is not independently reviewable, and
>       §3 asks for "each stage independently reviewable".
> - [x] State the rollback/forward-compat story for these migrations: §3 Stage 6
>       keeps old tables, but nothing says whether the new tables are safe to leave
>       in place when the engine is rolled back to `legacy`.

## 3. Implementation stages

<a id="sequencing"></a>
### Sequence, estimates, risks and stopping points

Execution order: **0A → 0B → 1 → 1S → 2 → 3 → (4 and 5) → 6**. Stage 1S is the
first operator-visible Telegram slice, before the full delivery infrastructure.
Early registry/delivery gates explicitly use fake providers; production provider
interoperation is a separate gate after Stages 4 and 5. Stages 4 and 5 can proceed
independently once shared contracts and Telegram behavior settle in Stage 3; they
join before integration/cutover. Slack app approval can be requested early without
blocking Telegram. This is a staffing plan, not an instruction to run agents now.

| Stage | Coarse effort (engineer-days) | Deliverable/stopping point |
|---|---|---|
| 0A defaults | 0.5–1 | Independent defaults/local-config fix; ships without any plugin work |
| 0B identity/permissions | 5–8 | Shared authenticated action boundary; needed for final remote control |
| 1 minimal config/contracts | 2–3 | Fake-provider validated increment; do not deploy by itself |
| 1S Telegram vertical slice | 2–3 | Shippable fresh-profile, one-row notification/status setup |
| 2 durable fan-out/inbox | 4–6 | Shared reliability and host routing proven with fake providers |
| 3 Telegram completion | 3–4 | Shippable Telegram remote control and multiple destinations |
| 4 Slack | 4–6 | Slack adapter and real-provider integration candidate |
| 5 full setup UI | 3–5 | Complete row/provider/channel and Host alerts experience |
| 6 cutover/rollback | 3–5 | Existing installations can adopt the feature |

Base scope is approximately **26.5–41 engineer-days**, with a 25% planning reserve
for the identity and migration uncertainties; elapsed schedule depends on staffing
and external app approval. Re-estimate after 0B and 1S rather than accepting an
unbounded framework build. If paused before 1S, keep 0A/0B and revert unshipped
Stage 1 code/schema additions in development; deployed additive schema remains
unused, never destructively removed. After 1S or Stage 3, keep the functioning
Telegram slice and disable unfinished provider/features. After cutover, use the
explicit Stage 6 rollback boundaries rather than reverting database files.

| Risk / owner | Trigger | Response / release consequence |
|---|---|---|
| Identity boundary / auth work owner | Any supported web alias accepts a forged header, or role/grant writes bypass policy | Stop interactive release; require full 0B parity gate, retain notification-only slice |
| SQLite contention / delivery owner | Sustained busy retries, ack persistence >2 seconds or p95 writes >50 ms under the design load | Batch/checkpoint less; prioritize inbox; remeasure before enabling multiple destinations |
| Cutover / persistence owner | Unmapped legacy target, uncertain send, or stale cursor at handoff | Keep connection paused, resolve preview errors and rehearse recovery; no automatic replay |
| Slack app approval / Slack owner | Workspace admin approval or required scopes unavailable | Continue Telegram; expose Slack Needs setup and exclude live Slack claims until approved |
| Scope/reuse / routing owner | Missing parent or reused spawn ID mismatches generation | Retire or mark orphaned; no action before ownership is reverified |

<a id="defaults-prerequisite"></a>
### Stage 0A — standalone defaults fix, before plugin implementation

This is an immediate independent maintenance change, not Stage 6 migration work.
**Implemented and verified (2026-09-09).** See [host configuration and requirement-by-requirement evidence](host-chat-configuration.md).

- [x] Move machine-specific chat values to explicit host user configuration, then
      set tracked defaults to `Enabled=false`, empty allowed-ID arrays, empty token,
      and `SupergroupChatId=0`. Do not copy personal IDs into examples or history.
- [x] Make the normal launcher and helper read that local configuration consistently
      with documented environment precedence; avoid the current base/Development
      JSON mismatch. Explicit opt-in is required even when a DPAPI blob exists.
- [x] Update the stale `_comment` to describe the actually shipped Telegram bridge
      and disabled defaults; preserve existing operators through their local opt-in.
- [x] Until Stage 0B protects equivalent HTTP and chat actions, keep new interactive
      ingress disabled. Add a legacy `AllowRemoteActions=false` guard so explicit
      local opt-in can deliver notifications without executing inbound domain actions;
      notification-only mode is implemented and the helper sets `AllowRemoteActions=false`.
- [x] Validate a clean checkout with a preexisting DPAPI test blob stays idle; verify
      local opt-in survives a normal launcher restart in an isolated fixture.

**Gate:** a standalone commit is ready independently of plugin schemas/adapters.
Validation: 105 chat tests passed (zero failed/skipped), including an isolated
DPAPI idle fixture, setup persistence, two host starts in normal launcher context,
and a recorded launcher process handoff. No live host restart or provider sends
were performed. No Stage 0A gaps remain; changes are ready for review as an
independent commit (not committed automatically).

<a id="authorization-prerequisite"></a>
### Stage 0B — authenticated identity and shared capabilities

Required v1 posture is the complete [identity/capability boundary](chat-plugin-design.md#auth-boundary),
not binding a remote operator to an unverified header string. This is a separately
reviewable prerequisite and must not be hidden inside a delivery subtask.

- [x] Add explicit host-side operator enrollment and Pair browser command with
      stable user IDs, OS-protected admin/enrollment data and one-use expiring
      challenges. No HTTP endpoint can create the first administrator.
- [x] Register ASP.NET authentication/authorization and host-paired cookie sessions;
      add anti-forgery, loopback Host/Origin validation, session expiry/revocation,
      frontend credential/CSRF handling and **Needs operator enrollment** when empty.
- [x] Implement `ForgeActorResolver` and `ForgeCapabilityAuthorizer` with every
      capability and fixed role mapping in design §9. Fail closed on unknown users,
      missing roles, unknown capabilities, unresolved targets and `productId=all`
      for mutations. Drop `X-Forge-User` as an authority source.
- [x] Enumerate/protect all supported web action endpoints and aliases, existing
      chat link/test endpoints, and user/role/connection-grant mutation paths. Check
      capabilities inside the common action boundary; internal chat calls never
      synthesize an authenticated header.
- [x] Extract pause/resume and artifact action cores while introducing that shared
      check. Keep domain behavior equivalent; authentication behavior intentionally
      changes from header presence to authenticated capability enforcement.
- [x] Gate plugin interactive mode until this entire prerequisite passes. An early
      slice may deliver notifications, but cannot claim remote actions are available.

**Gate:** run table-driven HTTP and chat-invocation parity tests for every supported
capability/target. Forged headers and arbitrary group members cannot create links,
change roles or execute actions; chat grants no more authority than web. No-users,
cross-product, revoked user/link/session, expired pairing, `all` scope, CSRF and
alias-route bypass cases deny before any side effect. Tests use authenticated test
principals instead of retaining the old header-only shortcut.

**Implementation evidence:** [Stage 0B requirement review, validation and host setup](chat-plugin-stage0b-validation.md).

- [x] Automatically sign in a direct localhost browser after verifying its TCP
      connection belongs to the Forge host's Windows user. Retain the existing
      enrolled account and roles, CSRF/Origin/cookie enforcement, explicit logout,
      and manual pairing for unavailable or ambiguous local identity. Validate real
      IPv4/IPv6 socket ownership, denial cases, and the browser request flow.

**Existing-login follow-up:** the local launcher opens an authenticated browser for
an existing enrolled account through `--operator open-browser`. Its short-lived,
one-use fragment is removed before POST redemption. Existing IDs and explicitly
migrated product roles are preserved; no HTTP enrollment or OAuth-cache trust is
introduced. Manual pairing remains a fallback, and multiple enrolled accounts
require explicit local selection.

Focused verification: 139 backend tests and 175 UI tests passed; TypeScript build
passed. All six implementation tasks are checked. **Full-suite validation is not
complete:** the broader API run was stopped with legacy authentication/ownership
fixture failures. Those fixtures still need migration; do not interpret these
checkboxes as a green full-repository test run. Plugin interactive mode remains
gated until its later runtime/delivery stages pass.

> <a id="review-p-03"></a>
> ### 🟠 REVIEW-P-03 — Major: no thin end-to-end slice, and nothing is sizeable [FIXED]
>
> **Status: FIXED in documentation (2026-09-09).** Added the early single-connection Telegram Stage 1S, estimates and dependencies, parallelizable later work, risks/triggers and explicit stopping/rollback points. See [sequence and sizing](#sequencing) and [vertical slice](#telegram-vertical-slice).
>
> Six stages, ~55 checkboxes, two new .NET projects, ~15 new control tables and a
> migration/cutover — and an operator sees **no improvement at all** until Stage 5,
> with the legacy engine (and the checked-in configuration flagged in REVIEW-P-04)
> live throughout. Stages 1–2 are pure infrastructure whose only consumers are built
> in Stages 3–4, which is also why their acceptance gates are unsatisfiable
> (REVIEW-P-01). If this slips or is paused mid-way, the repository is left carrying
> unused contracts and unused tables.
>
> There is also no effort estimate, no ordering budget, and no risk register, so the
> plan cannot be staffed, sequenced, or triaged when something takes twice as long.
>
> - [x] Define a **vertical slice first**: Telegram only, one connection, one spawn
>       subscription, the row toggle and status, no Slack, no multi-destination, no
>       migration. Ship and validate that, then widen. This exercises the contracts
>       against a real provider before they harden, and every later stage becomes an
>       increment on something that works.
> - [x] Add coarse size estimates per stage and mark which stages can run in
>       parallel (Slack vs. UI, for example).
> - [x] Add a short risk register with the top items and their triggers — at minimum:
>       authorization prerequisite (REVIEW-D-01), SQLite write contention
>       (REVIEW-D-05), migration/cutover, and Slack app-approval delays already
>       acknowledged in design §8.
> - [x] State the intended stopping points: which stages produce something
>       shippable on their own, and what is reverted if the work is abandoned.

### Stage 1 — contracts, registry, and workstream configuration

**Implemented and verified (2026-09-09).** See [Stage 1 requirement review and validation](chat-plugin-stage1-validation.md).

- [x] Add the minimal workstream plugin descriptor/catalog with versioned settings;
      register the `chat` plugin. Avoid adding a generic plugin execution framework
      beyond the lifecycle/configuration needed by this feature.
- [x] Introduce provider descriptors, registry, connection session, inbound sink,
      secret store, semantic notification/action, destination, and address contracts.
- [x] Model provider capabilities independently of Telegram, including thread/topic
      support, discovery, edits, buttons, and conversation retirement behavior.
- [x] Add `spawn_plugins` keyed by `(ProductId, SpawnId, PluginId)`, connection/product access grants, connections,
      destinations, subscriptions and versioned bindings to the control DB.
- [x] Implement scoped CRUD/validation services with optimistic revision checks.
      Persist explicit parent enabled state, activation generation and optional
      copy origin; default off. Child creation reads parent settings dynamically.
- [x] Add the permanent host subscription scope alongside spawn scope, with a
      canonical non-null ScopeKey, scope check constraints and Host alerts setup
      state. Restrict its configuration to `chat.connection.manage` / `host.alert.manage`.
- [x] Add host master switch and engine selection (`legacy` / `plugins`) without
      changing the active legacy engine during this stage.

**Acceptance (in-repo fake providers only):** a fixture containing two products
with the same spawn ID is isolated correctly. Register `FakeAlpha` and `FakeBeta`
with two named instances of `FakeAlpha`; prove independent registry/config routing.
Duplicate IDs fail registry construction without crashing Forge; unsupported
versions pause only the affected instances. Inaccessible connections and duplicate
subscriptions are rejected. Renaming/reordering a row
and reconciling its children preserve parent settings. A recreated spawn cannot
reuse an old run's active controls or configuration without explicit activation.

Acceptance evidence (all offline, with real SQLite and paired HTTP identities):

- [x] Two physical product DBs reuse the same spawn ID; 32 existing children and
      a future child read the correct parent settings dynamically.
- [x] `FakeAlpha`, `FakeBeta` and two named `FakeAlpha` connections resolve saved
      subscriptions to independent session addresses.
- [x] Duplicate provider/plugin IDs disable their registry; Forge health still
      responds. Unsupported provider, connection and parent settings versions
      remain isolated and retain saved data.
- [x] Inaccessible connections, duplicate active subscriptions/installations,
      stale revisions, unsupported policies/filters and interactive opt-in reject.
- [x] Rename/phase changes preserve settings; Chat changes leave execution state
      unchanged. Explicit copies start disabled with new subscription identities.
- [x] Recreated spawn reads are off until explicit activation; prior subscriptions
      and bindings retire when the new run is activated.
- [x] Phase98 is idempotent on fresh/upgrade schemas; all six tables have control
      model/catalog/runner/boundary registration and clean split dry-run results.

The focused selection passes **194/194** (26 new). The baseline independently
recorded one existing `Catalog_ClassificationMatchesDbBoundary` failure for
`repos`; it is excluded from that passing selection, with the reason and exact
command recorded in the validation document. No full-suite claim is made.
Production provider adapters, live session supervision, durable delivery/inbox,
global deletion reconciliation and the row UI remain in their later stages.
Tracked engine selection stays `legacy`; no host restart, network message, commit
or push was performed. Stage 1S is the next implementation stage.

> <a id="review-p-01"></a>
> ### 🔴 REVIEW-P-01 — Blocking: acceptance gates depend on later stages [FIXED]
>
> **Status: FIXED in documentation (2026-09-09).** Restated Stage 1/2 acceptance using named in-repo fake providers; live Telegram/Slack behavior has its own gate after both adapters/UI exist. See [sequence](#sequencing) and [real-provider integration](#provider-integration-gate).
>
> Several gates cannot be evaluated in the stage that owns them:
> - **Stage 1** requires "Multiple providers and two connections of one provider
>   resolve independently" — but no provider exists yet. Telegram is extracted in
>   Stage 3 and Slack is built in Stage 4.
> - **Stage 2** requires "Concurrent answers from **Slack and Telegram** produce one
>   accepted answer for that question revision" — Slack does not exist until Stage 4.
> - **Stage 2** also requires "failure in one destination does not resend to a
>   successful one", which needs at least two working destinations.
>
> As written, either the stage cannot be signed off, or it gets signed off against
> something other than the stated gate — which is worse, because the gate is the
> only completion definition the plan has.
>
> - [x] Restate Stage 1–2 gates against **in-repo fake/test providers** and say so
>       explicitly, so a reviewer knows a mock was intended and does not read the
>       gate as proof of real cross-provider behavior.
> - [x] Move the genuinely cross-provider gates ("Slack and Telegram", "two Slack
>       workspaces plus Telegram") into Stage 4 or a dedicated integration gate.
> - [x] Do this consistently with the plan's own rule in Stage 2 — "No test may claim
>       exactly-once network delivery without evidence" — which is exactly the
>       failure this comment is about.

<a id="telegram-vertical-slice"></a>
### Stage 1S — thin Telegram path visible on the row

Implementation and automated checks: [Stage 1S validation](chat-plugin-stage1s-validation.md).
The live Telegram/manual row gate below is still open; checked implementation
items do not claim that exercise has run.

- [x] On a fresh isolated profile, adapt the existing `TelegramTransport` to one
      named connection, one selected top-level row and one destination. Use only
      the narrow contracts exercised by this slice; full provider extraction is Stage 3.
- [x] Add the minimal row Chat control, notification-only toggle, provider/channel
      picker and persisted off/needs-setup/ready status under `swarm/plugins/`.
      Stage 5 extends this same UI; it does not replace it.
- [x] Send an explicit setup introduction and one metadata-only open-question alert
      through the existing single-recipient outbox shape, with the verified parent
      subscription selecting its target. Read health on the row without restarting
      agents. Apply outbound redaction from the start; do not wait for Stage 5.
- [x] Apply the design's retention/purge rules to the subset of configuration and
      outbox tables used by this slice; Stage 2 expands coverage to its new tables.
- [x] Require a new test bot/empty legacy queue for this early slice. Plugin engine
      selection disables legacy polling/sending; there is no parallel owner and no
      legacy-data migration. Existing installations continue their explicitly chosen
      legacy notification path until Stage 6.
- [x] Ship no Slack, multi-destination fan-out or inbound domain mutations in this
      slice. Label it **Notifications only**; the final remote-control requirement
      remains open until Stage 3.

**Gate:** fake HTTP tests plus a recorded Telegram test-bot exercise prove row
toggle → channel → actual send → persisted row status. Other rows remain off.
Cancel has no effects; a normal refresh preserves state. This gate supplies a real
provider/visible UI stopping point before the full delivery/inbox schema is built.

- [x] Fake Telegram HTTP: setup → outbox → receipt → persisted health; verified
      parent/child routing, disabled rows, revocation, redaction and failure paths.
- [x] Isolated browser exercise: row toggle/channel, Cancel/Escape, Save, refresh,
      other row off, reader restrictions and stale revision feedback.
- [ ] Recorded real Telegram test-bot exercise and manual row confirmation.
      Awaiting an explicitly supplied new-bot/isolated-destination profile. The
      opt-in live exercise is skipped by default, never substituted with a fake pass.

### Stage 2 — delivery, inbound durability, and domain result handling

Status (2026-09-10): all 14 requirements implemented and validated. See the
[Stage 2 requirement review and evidence](chat-plugin-stage2-validation.md): 262 backend
tests passed (1 live exercise skipped), 194 UI tests, UI build and 4 browser checks.
The Stage 1S real-bot gate and later provider/setup/cutover stages remain separate.

- [x] Evolve `chat_notifications` into a provider-neutral logical event; add
      versioned payload conversion for legacy text/actions.
- [x] Add `chat_deliveries` and `chat_delivery_messages`, including immutable target
      snapshots, operation/recovery records, retry schedules, rendered revisions and question/part IDs.
- [x] Resolve subscriptions using product/spawn scope and event filters; validate
      `WorkstreamRow.StorySpawnId` for child events and persisted ownership for
      planning/artifact events before child workstreams exist. Retain exact targets;
      create event and delivery rows atomically in the control DB.
- [x] Add a durable inbox, connection cursor/session state and provider receiver
      ownership records. Share Slack reception by app identity and route to workspace connections.
      Store receipt keys before transport acknowledgement; retry domain dispatch
      separately.
- [x] Implement the connection supervisor with in-process reception-key ownership,
      per-connection cancellation, configuration revisions and health reporting.
- [x] Implement provider-aware retry classification and fair scheduling. Handle
      accepted-but-unconfirmed sends and uncertain topic creation explicitly.
- [x] Implement the [versioned event catalog](chat-plugin-design.md#event-catalog),
      explicit default filters, permanent host routing and visible zero-match rows.
      Enforce actionable ordering and supersession across retries.
- [x] Use the [single-instance DB budget](chat-plugin-design.md#single-instance-recovery):
      named host mutex, in-process receiver locks, crash records, 60-second recovery
      window, coalesced health and short batched writes. Test checkpoint failure
      after a successful send without repeating the provider call.
- [x] Add `ChatScopeLifecycleService`, wire spawn/product deletion and the
      `ForgeSeed.ScrubMockedSpawnsAsync` / `StorySpawnIdsDeleted` prune path, and add
      startup/five-minute `ChatScopeReconciler`. Missing parents are Orphaned;
      unavailable product DBs are Degraded. Validate ownership before every action.
- [x] Expand the slice's `ChatContentPolicy` to both providers and all retries/edits;
      implement hourly bounded `ChatRetentionService` for every content-bearing
      table and content-free correlation record in the design's retention matrix.
- [x] Add domain action receipts/expected revisions and use the Stage 0B shared
      authorizer (do not rebuild it). Product mutation and its receipt commit together where transactional;
      worktree/process effects use recorded operation IDs and recovery states.
- [x] Extract parent pause/resume and required artifact action cores from
      `StoriesEndpoints`; share them with chat and the existing HTTP endpoints.
      Add parent status aggregation, child/artifact target selection and membership
      checks. Never map an ambiguous action to every child in the row.
- [x] Reconcile domain results to every copy after restart, including web-UI actions.
      Keep unanswered questions actionable and retire superseded action tokens.
- [x] Implement disable/pause/removal behavior from the design, including cancellation
      of queued sends, refusal of stale actions and retirement of sent controls.

**Acceptance (fake providers and stubbed clocks/storage faults):** failure in one
of two fake destinations does not resend to the successful one. Redelivery of one
event does not repeat a domain action. Concurrent answers delivered through
`FakeAlpha` and `FakeBeta` produce one accepted answer for that question revision. Crash
fixtures cover receipt-before-ack, mutation-before-delivery-edit, and unknown external
send outcomes. No test may claim exactly-once network delivery without evidence.

> <a id="review-p-02"></a>
> ### 🔴 REVIEW-P-02 — Blocking: nothing here builds the authorization the design mandates [FIXED]
>
> **Status: FIXED in documentation (2026-09-09).** Promoted authentication/capabilities to Stage 0B with enrollment, identity, empty-user denial, protected grant/role mutations, common action checks and HTTP/chat parity tests. See [authorization prerequisite](#authorization-prerequisite).
>
> This stage contains "Add domain action receipts, expected revisions and a **shared
> authorization service**" as a single sub-bullet. There is nothing in Forge to build
> that on. See `REVIEW-D-01` in [chat-plugin-design.md](chat-plugin-design.md) for
> the evidence:
> `Auth/ForgeUser.cs:17-35` checks only that an `X-Forge-User` header is present
> ("Phase-5 introduces real auth"), `Auth/RequireRole.cs:103-120` string-matches that
> header and treats an empty user table as open, and `Persistence/ProductScope.cs:17-19`
> records that `productId=all` is ungated because "today every signed-in caller may
> use it". There is no `AddAuthentication`/`AddAuthorization` in `Forge.Api`.
>
> Meanwhile §4's validation matrix asserts "Revoked operator or lost product
> permission → new actions rejected even if a button or link was previously valid",
> and §5's release gate asserts "no provider can silently acquire another's scope".
> Neither is reachable from the current codebase, so both would be signed off on an
> implementation that does not enforce what the row claims.
>
> - [x] Promote authorization to an explicit **prerequisite work item** with its own
>       checkboxes: capability names, identity source decision, enforcement point,
>       and the behavior when no users exist (today: open).
> - [x] Decide the v1 posture and write it down — full capability model, or
>       notification-only plus a single explicitly configured operator. Do not leave
>       it implied by design prose.
> - [x] Add a gate that a chat-originated action can never carry **more** authority
>       than the equivalent web endpoint, and a test that proves it.
> - [x] Extracting the pause/resume and artifact action cores from `StoriesEndpoints`
>       is the right moment to introduce the shared check — note that those endpoints
>       currently gate on `ForgeUser.RequireWriter` alone (e.g.
>       `Endpoints/StoriesEndpoints.cs:175,2317,4421`), so "share them with chat"
>       inherits header-only enforcement unless this is fixed first.

### Stage 3 — Telegram provider extraction and interaction repair

- [x] Create `Common.Chat.Telegram`, move its protocol implementation and add its
      provider descriptor. Retain the old engine through a compatibility adapter.
- [x] Replace the global Telegram options/cursor with connection-scoped settings
      and reception; guard against two sessions owning the same bot.
- [x] Add token/chat/forum/membership validation and destination discovery through
      the existing receiver. Detect webhook conflicts without deleting them.
- [x] Add `/start` linking, addressed-command parsing, same-topic responses, and
      callback acknowledgements using the retained callback query ID.
- [x] Replace the allowlist/duplicate-row linking behavior with explicit identity
      linking and revocation; preserve pending challenges and clear error states.
- [x] Add multi-question selection/custom answers, correct pause semantics, scoped
      status, and feedback for unsupported input in a bound conversation.
- [x] Implement topic provisioning/retirement and DM/flat-chat capability handling;
      use one topic per parent subscription, an in-process claim and recovery record
      for uncertain creation results. Future children reuse the parent's topic.
- [x] Enforce the 25-byte server lookup-key format, Telegram UTF-8 callback limit
      and visible render failures. Preserve original message aliases for targetless
      legacy callbacks when splitting into per-question messages.

**Acceptance:** a Telegram-only workstream works through the new registry; two bots
do not share cursors. A reply stays in its topic. Answering one of several questions
leaves the others usable. Re-linking an already bootstrapped user does not violate
the subscriber uniqueness constraint. Off/paused scopes cannot drive domain actions.

**Validation (2026-09-10):** All eight implementation items and the acceptance
scenarios are covered by protocol HTTP fixtures and isolated host/database tests.
See [Stage 3 validation](chat-plugin-stage3-validation.md) for the gap review,
schema registration, commands and exact test evidence. The Stage 1S live-bot/manual
row gate remains pending; Slack, the full setup UI and legacy import remain later stages.

### Stage 4 — Slack provider

- [x] Create `Common.Chat.Slack` and register it alongside Telegram.
- [x] Supply `docs/chat/slack-app-manifest.yaml` and `slack-setup.md` during this
      implementation stage. Include public-channel scopes/event subscriptions and
      document the private-channel option specified in the design.
- [x] Implement bot credential validation (`auth.test`), app-token/socket validation,
      paginated destination listing, and channel membership checks. Show credential,
      app-installation, workspace and channel failures separately.
- [x] Implement Web API sending/editing and one root thread per top-level row and
      destination, shared by its children; preserve
      `channel`, `ts`, `thread_ts` and workspace identity as strings.
- [x] Implement Block Kit rendering with accessible fallback text, opaque buttons,
      custom-answer selection, content limits and multi-part correlation.
- [x] Implement Socket Mode envelope reception, durable acceptance, timely ack,
      reconnect/refresh, backoff, cancellation and connection ownership.
- [x] Share one app receiver across its workspace installations, validate the
      handshake app ID and route events to the matching workspace connection.
      Diagnose unknown workspaces without dispatching their events as another user.
- [x] Normalize user replies and interactive events; filter self/bot and edit/delete
      events. Use event/envelope dedupe plus logical action idempotency.
- [x] Implement question disambiguation for thread replies and channel-based linking
      challenges. Initial support must not require direct-message or user tokens.
- [x] Respect provider rate-limit hints and make removed channel membership, revoked
      tokens and archived channels repairable from the connection UI.

    - [x] Provider pacing, safe failure codes, validation and revision/retry APIs.
    - [x] Connection repair screen and browser acceptance: completed in Stage 5;
          see [Stage 5 validation](chat-plugin-stage5-validation.md).

**Acceptance:** fixtures exercise public/private channel replies, buttons, split
messages, duplicate envelopes, reconnects, 429s and revoked credentials. Two Slack
workspaces using one app receiver and Telegram can run simultaneously. Disabling
one workspace leaves the other receiving. A channel reply with an ambiguous
question never answers an arbitrary work item. No history polling is required.

See [Stage 4 validation](chat-plugin-stage4-validation.md) for all ten requirements,
fixture evidence and the gap review. Stage 5 closes its connection UI dependency.
Use the [Slack setup guide](slack-setup.md) and [manifest](slack-app-manifest.yaml).

<a id="provider-integration-gate"></a>
**Real-provider integration gate (after Stages 4 and 5):** record a Telegram test
bot and Slack test workspace exercise for concurrent answers, cross-copy resolution,
destination failure isolation, independent scopes and reconnect. Also exercise two
Slack workspace connections to the same app receiver while Telegram remains live.
Fixtures alone do not close this gate. Missing workspace approval marks it pending,
not passed. Enable redacted question content explicitly for answer-interaction tests.

- [ ] Run and record the approved real-provider exercise above. Stage 5's isolated
      protocol, host and browser fixtures do not mark this gate passed.

Protocol references: [Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/),
[message posting](https://docs.slack.dev/reference/methods/chat.postMessage/),
[message updates](https://docs.slack.dev/reference/methods/chat.update/),
[interaction acknowledgement](https://docs.slack.dev/interactivity/handling-user-interaction/),
[authentication check](https://docs.slack.dev/reference/methods/auth.test/).

### Stage 5 — guided UI, API, and live enablement

**Implemented and verified (2026-09-10).** All 14 items below map to code and tests
in [Stage 5 validation](chat-plugin-stage5-validation.md). See the current
[setup and API guide](chat-plugin-setup.md). Runtime activation requires loading
the new backend at an operator-approved restart; no running service was restarted.

- [x] Implement the proposed connection, destination, identity-link, health and
      delivery APIs with explicit verbs/DTOs, authorization, revisions and redaction.
- [x] Add Settings → Integrations → Chat. Persist incomplete setup and expose
      credentials/destination/outbound/inbound/link/permission verification separately.
- [x] Include permanent Host alerts provider/channel selection, default host event
      filters, unrouted counts, orphaned scopes and separate host permissions.
      Add metadata-only/redacted-summary policy selection and a redacted preview;
      explain that retiring a subscription does not remove provider-held messages.
- [x] Add an always-visible Chat control to each `SwarmStoryRow` in `SwarmIndexView`,
      including done rows. Show off/needs-setup/ready/degraded status and provider/
      channel or destination count without opening the child graph.
- [x] Add a shared **Remote control · {row title}** drawer reachable from that row
      and its `SwarmStoryView` header. Stop row-navigation propagation and support
      keyboard activation, focus return, scrolling and the existing dense row layout.
- [x] Implement the exact flow: **Enable chat remote control → Provider → Channel →
      Save and enable**. Default to interactive remote control; offer notification-only
      as an explicit alternative. Show connection selection only when needed.
- [x] Support inline connection setup with return to the same row/draft, channel
      discovery by name, validation failures, explicit save/cancel and **Add provider/
      channel** for additional destinations. Changing provider clears an incompatible
      channel selection; cancellation never activates chat or creates a topic.
- [x] Include chat summaries in spawn list/detail data or a batched product-scoped
      endpoint; cache/update by product/spawn and revision. Render without per-row
      provider calls; refresh the list and detail header from the same saved state.
- [x] Aggregate summaries using the existing spawn list plus one chunked control-DB
      query; test query counts to prevent per-row cross-database reads.
- [x] Add explicit copy-destinations between top-level rows. All current/future
      children are covered by their parent's subscription without individual switches.
- [x] Implement user-confirmed identity linking and revoke/re-link flows. Verify
      linkage before offering domain actions; keep notification-only mode usable.
- [x] Apply configuration revisions live to the affected connection/subscription;
      show externally managed credentials and any host-refresh limitation accurately.
- [x] Add test-send/reply checks, delivery history, next-retry information and retry
      controls. Never send a test merely by viewing or validating a settings page.
- [x] Update the Telegram helper to call these APIs. Retire configuration-file
      patching and redundant desktop installation/sign-in prompts from normal setup.

**Acceptance:** use a five-row fixture matching the supplied screenshot. Enable
BEYOND-AST with Telegram/Forge Ops, 3D-RENDER with Slack/#render, leave MCP off,
enable AGENT MEMORY with both, and leave PR INGESTER off. Verify persisted independent
summaries after refresh, reorder/filter and product switch. BEYOND-AST's 32 children
share one conversation; a new child appears there without setup. Done rows retain
the Chat control and are not restarted by enabling it. Save/cancel, keyboard focus,
inline setup return and recovery errors are covered. Agents remain running during
Chat configuration changes; parent pause/resume runs only from an explicit work action.

### Stage 6 — migration, cutover, and compatibility

**Status: implemented and rehearsed in isolated fixtures (2026-09-10).** All 11
requirements are checked below. [Stage 6 validation](chat-plugin-stage6-validation.md)
maps the implementation and 22 migration cases to each requirement. Use the
[migration/rollback guide](chat-plugin-migration.md) for an explicit operator cutover;
the separate real-provider gates remain open.

> <a id="review-p-04"></a>
> ### 🟠 REVIEW-P-04 — Major: a fix-now repo-hygiene issue is deferred behind six stages [FIXED]
>
> **Status: FIXED in documentation (2026-09-09).** Moved defaults/local-config/comment cleanup into immediate standalone Stage 0A, independent of plugins; Stage 6 now only imports local settings. This review fixes the plan: the source/defaults change is NOT landed in this documentation-only pass and its execution boxes remain open. See [standalone defaults prerequisite](#defaults-prerequisite).
>
> This stage carries "clean-install defaults contain no personal IDs" as a Stage 6
> checkbox. That is describing a condition that exists in the repository **right
> now**, on `main`, on every clean checkout — `src/forge/Forge.Api/appsettings.json`:
> - line 42: `"Enabled": true` — while the `_comment` on line 41 in the same object
>   says "Disabled by default", and design §2 records the same contradiction
> - line 46: `"AllowedChatIds": [ -1004373092754 ]`
> - line 47: `"SupergroupChatId": -1004373092754`
> - line 48: `"AllowedUserIds": [ 8458300047 ]`
>
> A personal Telegram group ID and user ID are committed to a shared repository,
> and the feature ships **on**. The committed `BotToken` is empty, but that does not
> make this inert: `Common.Chat/ChatTokenResolver.cs:31-40` falls back to a
> DPAPI blob when the configured token is blank, and
> `src/forge/Forge.Api/Program.cs:376` unconditionally defaults `TokenBlobPath` to
> `ChatBridgeOptions.DefaultTokenBlobPath()` (`%LOCALAPPDATA%\Forge\chat-token.bin`,
> `Services/Chat/ChatBridgeOptions.cs:54`). So on any machine where an operator has
> ever run the setup script, a clean checkout resolves a real token and posts to the
> committed `SupergroupChatId`. Gating the fix behind an unstarted six-stage plan
> means it stays there for the whole project.
>
> - [x] Land the defaults fix as a **standalone change now**, independent of this
>       plan: `Enabled: false`, empty/absent ID arrays, and the machine-specific
>       values moved to local user configuration.
> - [x] Keep a Stage 6 checkbox only for the *migration* concern (importing an
>       existing operator's values into a named legacy connection).
> - [x] Design §2 lists this row as "Explicit migration and clean-install defaults".
>       Split it: the clean-install default is a bug fix, not a migration step.
> - [x] While there: reconcile the `_comment` on line 41 with the actual values — it
>       also still claims "Phase 1 ships options + transport + POST /api/chat/test-send
>       only", which no longer matches the shipped bridge.

- [x] Implement a read-only migration preview: legacy options/token source, configured
      destinations, topic bindings, operator links, cursor and notification counts.
- [x] Import into a named `legacy-telegram` connection using stable migration keys;
      keep the DPAPI secret by reference and do not print its decrypted value.
- [x] Import crew/account alerts and the legacy global destination into permanent
      Host alerts; retiring old-engine compatibility never disables this destination.
- [x] Backfill one delivery per existing notification with the old target/message
      state. Preserve terminal, pending, failed and resolving states and timestamps.
- [x] Preserve old spawn-topic addresses as parent bindings: one subscription per
      verified product/spawn/destination, not one per child. Associate target children
      and artifacts through verified ownership. Keep ambiguous
      and unscoped rows in permanent Host alerts attention or flag them for repair.
- [x] Require explicit activation for selected top-level rows. New children within
      an enabled row use it automatically; unrelated future rows remain off.
- [x] Quiesce the old sender and receiver, wait for in-flight operations, record the final
      cursor/state, complete the import and atomically transfer connection ownership
      before starting the plugin session. Stop producer routing to the legacy engine
      at the same cutover revision; events during the handoff remain durable.
- [x] Preserve aliases for old reply messages and callback formats. Validate scope
      and operator permissions before dispatching old controls through the new core.
- [x] Keep old endpoints as wrappers for the selected migrated connection. Correct
      test-send destination selection to honor the selected destination/group.
- [x] Replace the launcher chat-settings export only after the new engine uses the
      persisted settings service. Import the operator's Stage 0A local settings;
      tracked defaults have already been fixed in the standalone change.
- [x] Implement and rehearse a rollback command that stops plugin sessions, transfers
      the latest Telegram cursor and supported delivery/message states, and restarts
      the legacy path with exclusive ownership. Never reset to the pre-cutover cursor.

**Rollback boundary:** before new multi-destination traffic, full return to the
legacy engine is supported. After plugin-only Telegram subscriptions or Slack
traffic exist, first pause those subscriptions and retain their history. Only
legacy-compatible Telegram routes can be returned to the old engine. A rollback
preview identifies unsupported routes and outstanding edits; it must refuse a
lossless-rollback claim when it cannot represent them. Keep additive tables until a
later release explicitly retires compatibility.

**Acceptance:** migration is repeatable after interruption, the old and new engines
never share a polling/sending owner, old Telegram buttons still resolve correctly,
and no sent notification is replayed solely because of cutover or rollback.

## 4. Validation matrix

**Execution started 2026-09-10:** Stage 6 and its tested legacy-topic repair are deployed. All 28 scenarios have passing automated evidence: 408 backend tests, 197 UI unit tests and 12 browser cases, including the Telegram/Slack setup navigation repair verified on the running UI. The shared conformance audit, real-provider gates and separate background story-detail HTTP 403 finding remain open in the [execution report](chat-plugin-validation-matrix.md).

| # | Automated | Scenario | Expected evidence |
|---|---|---|---|
| VM-01 | [x] | All four current notifier kinds | Spawn questions/stalls and host crew/sign-in alerts each reach their explicit scope; host delivery survives legacy retirement |
| VM-02 | [x] | Zero matching subscriptions and future kind IDs | Persisted unrouted reason/count; no automatic replay; new kinds remain opt-in |
| VM-03 | [x] | Failed earlier actionable event followed by a newer event | Subscription ordering survives retries; superseded retries cannot restore stale controls; other subscriptions continue |
| VM-04 | [x] | Forged header, empty users, `productId=all`, web alias | Shared capability boundary denies before mutation; browser and chat authority match |
| VM-05 | [x] | Unsupported provider/settings version versus duplicate ID | Degraded instance versus fatal chat registry; Forge UI/agents still boot |
| VM-06 | [x] | Seed prune, spawn deletion, missing product DB | Orphan retirement or degraded pause as specified; batched summary reads have no per-row DB growth |
| VM-07 | [x] | Busy DB before send or after provider success | No unrecorded fresh send; checkpoint-only retry after success; bounded reception ack budget |
| VM-08 | [x] | Unicode oversized payload, source/secret-bearing agent text, expiry | Visible render failure, outbound redaction/metadata fallback, and retention without breaking live dedupe |
| VM-09 | [x] | Five top-level rows in the supplied screen | Each exposes Enable remote control, Provider and Channel; no child selection required |
| VM-10 | [x] | Parent with 32 children plus a newly generated child | One parent topic/thread per destination; all verified child events/actions route within it |
| VM-11 | [x] | Chat configured while the parent has no child workstreams yet | Planning/artifact questions route to the parent's channel |
| VM-12 | [x] | Two top-level rows choose the same channel | Distinct parent conversations; actions cannot cross their boundaries |
| VM-13 | [x] | Done row enabled; draft cancelled; row filtered/reordered | No work restarted; cancellation has no effects; saved settings remain tied to spawn ID |
| VM-14 | [x] | Parent Pause/Resume versus disable Chat | Only the explicit work control changes execution; disabling Chat prevents all its child chat actions |
| VM-15 | [x] | Same IDs in different products/workspaces/channels | Correct scope; no cross-product answer or delivery |
| VM-16 | [x] | Chat off on workstream A, on for B | Only B produces subscribed deliveries; both workstreams continue executing |
| VM-17 | [x] | Telegram + Slack + second Telegram destination | Independent sends/retries; one domain answer updates all copies |
| VM-18 | [x] | Connection/destination removed during an in-flight send | Defined operation completion and cancellation behavior; no retargeting |
| VM-19 | [x] | Multiple unanswered questions in a single thread | Explicit selection; remaining questions stay actionable |
| VM-20 | [x] | Answer in UI or stale button in another provider | Consistent final result; stale action cannot overwrite it |
| VM-21 | [x] | Expired/replayed link or restart confirmation | Rejected without mutation; re-link remains usable |
| VM-22 | [x] | Revoked operator or lost product permission | New actions rejected even if a button or link was previously valid |
| VM-23 | [x] | Rate limit, socket refresh, bad token, network outage | Scoped degraded status, bounded retries, recovery without agent restart |
| VM-24 | [x] | Provider receipt followed by process crash | Inbox resumes handling; duplicate provider receipt does not repeat effects |
| VM-25 | [x] | Domain action followed by control-DB failure | Receipt reconciliation updates all deliveries after recovery |
| VM-26 | [x] | Legacy spawn topic shared by multiple child workstreams | One parent binding retained; reply/action IDs target exactly; ambiguous text asks for selection |
| VM-27 | [x] | Setup through UI, helper script, and normal launcher | Same persisted effective settings; no Development/base JSON mismatch |
| VM-28 | [x] | Secret value in provider exception or URL | Redacted logs, API responses and setup diagnostics |

Use the existing `src/forge/Forge.Api.Tests/Chat` suite as the baseline and add
provider contract fixtures plus fake HTTP/WebSocket peers. Keep provider tests
offline and deterministic; use `FORGE_TEST_GUARDRAIL` so production hosted services
do not start under tests. Add focused UI tests for setup completion, error recovery,
workstream selection and independent destinations. Test Windows secret-store
round-trips only with temporary test credentials and isolated paths.

After automated acceptance, exercise an explicitly configured Telegram test bot and
Slack test workspace/channel: enable a workstream, send a question to both, answer
in each provider on separate questions, perform a reversible stalled action, rotate
test credentials and verify recovery. Capture actual results rather than marking
live provider behavior proven by mocks. This document update runs no such tests
and creates no external messages or resources.

<a id="provider-conformance"></a>
### Shared provider conformance suite

Create one parameterized `ChatProviderConformanceTests` contract suite before
declaring the provider interface stable. Every `IChatProviderPlugin` (including
test providers, Telegram and Slack) runs the same behavior assertions against fake
HTTP/WebSocket peers. Advertised capabilities determine required cases; an
unsupported operation must report NotSupported explicitly rather than silently
succeed. Require send/edit correlation, conversation ensure/retire semantics,
paginated discovery, payload byte/character limits, safe rendering, retry
classification, normalized event identity/dedupe, cancellation and provider
unavailability. Telegram's remote topic closure and Slack's local thread retirement
are both checked against their descriptors, not forced into identical APIs.

Separately run host-level tests for zero-match routing, versioned filters,
ordering/supersession, DB checkpoint recovery, permissions and all-destination
resolution. Stage 1/2 use in-repo fake providers; Stage 3/4 add real adapter fixtures;
the explicit live integration gate remains separate. All fixtures set
`FORGE_TEST_GUARDRAIL` before constructing the host and use temporary databases and
credentials. Never reuse a production chat receiver during the suite.

> <a id="review-p-06"></a>
> ### 🟡 REVIEW-P-06 — Minor: no provider conformance suite; test prerequisite omitted [FIXED]
>
> **Status: FIXED in documentation (2026-09-09).** Required one parameterized provider conformance suite and added zero-routing, future-kind and ordering/retry cases to the shared validation matrix. Distinguished fixture results from live integration. See [conformance suite](#provider-conformance).
>
> The baseline and guardrail references are accurate — `src/forge/Forge.Api.Tests/Chat`
> holds nine existing suites, and `FORGE_TEST_GUARDRAIL` is real
> (`Forge.Api.Tests/Infrastructure/ForgeWebApplicationFactory.cs:89` sets it;
> `Forge.Api/Program.cs:343` gates background services on it). Gaps:
>
> - [x] Require a **shared provider conformance suite** that every
>       `IChatProviderPlugin` must pass (send, edit, retire, discovery, payload
>       limits, retry classification, dedupe). Per-provider fixtures alone let the
>       second provider quietly diverge from the contract, which is the specific risk
>       a plugin model is supposed to remove.
> - [x] The matrix has no row for the empty-routing case or for delivery ordering
>       across retries; add them once REVIEW-D-04 defines the event taxonomy.

## 5. Release gate and documentation

- [ ] Both providers complete connection and workstream setup through the UI.
- [ ] Every top-level Workstreams row exposes remote-control enablement and provider/
      channel selection; configuration is owned by the parent spawn throughout UI,
      API, persistence, routing and migration.
- [ ] Multi-destination delivery, scoped actions and cross-provider resolution pass
      the validation matrix; no provider can silently acquire another's scope.
- [ ] Migration and rollback boundaries are tested and explained to operators.
- [ ] Add current `telegram-setup.md` and `slack-setup.md`, troubleshooting, exact
      app-manifest scopes, credential rotation instructions and API examples.
- [ ] Update `docs/chat/README.md` to distinguish shipped capabilities from remaining
      plans; keep previous phase histories as historical references.
- [ ] Update this plan with actual completed checkboxes and recorded validation only
      as implementation lands. No release claim is implied by this planning change.


**LAN connectivity follow-up (2026-09-09):** the UI binds to `0.0.0.0:5181`;
the launcher verifies the listener and hostname HTTP response, configures exact
remote browser origins, and repairs Domain/Private firewall rules for routed
clients. The API remains loopback-only behind the UI proxy. Remote pairing is
supported through that proxy; automatic host identity is rejected there. Browser
SSO remains separate work.