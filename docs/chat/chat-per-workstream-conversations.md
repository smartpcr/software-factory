# Chat: set up once, one conversation per workstream — design and plan

**Status (2026-11-19):** Stages 1–4 **built and locally verified** (see
[build record](#build-record)). **Not deployed** (needs a Forge.Api restart) and
**not remote accepted** (Stage 5, E1–E5, has not run).
This revision changes two decisions in the [chat plugin design](chat-plugin-design.md):
the per-story conversation policy (§4 "Workstream setup") and per-product
connection grants (§9 "Connections carry explicit product access grants"). Where
the two documents disagree, this one wins for the behavior it describes.

Statuses are tracked separately for each deliverable: **designed**, **built**
(code + unit tests), **locally verified**, **remote accepted** (real Slack/Telegram).

## A. What we want to achieve

### The problem, in operator terms

An operator connects Telegram and Slack once in **Settings → Chat**. Today that
is not enough:

1. **Each product must be granted each connection.** A product created after the
   connection (for example `harness`, created 2026-11-19) cannot see it: the story
   panel says *"No telegram connection is available for this product."* Grants are
   written only when someone edits the connection.
2. **Each story must be enabled by hand.** Remote control is off for every new
   story until someone opens its panel, picks a provider, channel and mode, and saves.
3. **All of a story's workstreams share one conversation.** A story such as
   harness:DSH has 80 workstreams. Every question and stall alert for all of them
   lands in one Telegram topic or one Slack thread, so a reply has to name its
   target and a reader cannot follow one workstream.

### Vocabulary

| Term | Meaning | Example |
|---|---|---|
| Connection | A configured bot/app login. Host-wide, set up once. | Telegram bot `@f176…bot`; Slack app in workspace `forge` |
| Destination | A channel or group the connection can post in | Slack `#all-forge`; a Telegram forum supergroup |
| Story (spawn) | A top-level row on the Workstreams page | harness:DSH |
| Workstream | One child node of a story | `stage-1.1-three-module-workspace` |
| Subscription | "This story posts to this destination", with mode and event filters | DSH → `#all-forge`, interactive |
| Conversation | Where messages land inside a destination: a Telegram forum **topic** or a Slack **thread** (a root message plus replies) | Topic "DSH · three-module-workspace" |
| Binding | Forge's record of one conversation it created: the topic id or Slack root `ts`, and whether it is live or retired | — |
| Host default | New: the destinations every story gets automatically unless the story overrides them | `#all-forge` + Telegram forum |

A destination is a channel; a conversation is a topic or thread inside it. Forge
never creates channels or groups (see [DEC-3](#dec-3)); it creates conversations.

### Outcomes

| ID | Observable outcome | Delivered by | Proven by |
|---|---|---|---|
| <a id="o1"></a>O1 | A connection marked **All products** is usable by every product, including products created later, with no per-product step. | [D1](#d1) | [U1](#u1), [E1](#e1) |
| <a id="o2"></a>O2 | Every new story starts with remote control **on**, posting to the host-default destinations, without anyone opening its panel. A story can still override or turn it off. | [D2](#d2), [D3](#d3) | [U2](#u2), [U3](#u3), [E2](#e2) |
| <a id="o3"></a>O3 | Each workstream that has something to say gets **its own** Telegram topic / Slack thread, created automatically on its first message. Story-level messages (plan questions, decomposition) keep the story conversation. | [D4](#d4), [D5](#d5) | [U4](#u4), [U5](#u5), [E3](#e3) |
| <a id="o4"></a>O4 | A reply or button press inside a workstream's conversation targets **that** workstream without a selector. | [D6](#d6) | [U6](#u6), [E4](#e4) |
| <a id="o5"></a>O5 | When a workstream finishes (complete / cancelled / historic) its Telegram topic is closed and its Slack root is marked done; no new messages go there. | [D7](#d7) | [U7](#u7), [E5](#e5) |

### Scope and non-goals

- **In scope:** Slack and Telegram, plugin engine (`chat_migration_state.Owner = plugins`,
  the live engine on this host). Both destinations at once (operator choice, 2026-11-19).
- **Not in scope:** creating Slack channels or Telegram groups ([DEC-3](#dec-3));
  the legacy `ChatBridge` engine; per-workstream mode/filter overrides; free-form
  agent conversation (still deferred by the base design).
- **Operator prerequisite, outside Forge:** Telegram needs a **forum supergroup**
  (Topics on) with the bot as administrator holding *Manage topics*. A bot cannot
  create one. Slack needs the app invited to the channel (`#all-forge` is already verified).

## B. How we get there

### Design decisions

<a id="dec-1"></a>**DEC-1 — "All products" is stored as grants, not as a bypass.**
*Choice:* a connection flag `AllProducts`. Setting it writes a grant row for every
existing product; creating a product writes grant rows for every `AllProducts`
connection. *Why:* every existing access check (`ListConnectionsAsync`,
`RequireConnectionGrantAsync`, `VisibleSubscriptionsAsync`, `ChatEventService`)
already reads `chat_connection_products`; materialising rows keeps one source of
truth instead of adding an "or AllProducts" branch to each check. *Tradeoff:* a
product created while the flag is set keeps its grant if the flag is later
cleared: clearing the flag keeps the current grant list, and the operator unticks
products in the same editor to revoke (no separate revoke prompt — built that way
because the editor already shows the per-product list).
*Supports* O1. *Verified by* U1, E1.

<a id="dec-2"></a>**DEC-2 — The host default is copied onto a story when it starts, not read live.**
*Choice:* a host-level list of default destinations (destination, mode, content
policy, event filters, conversation scope). When a story spawn is created, or
starts a new run, and it has no explicit chat settings, Forge writes its
`spawn_plugins` row (enabled) and subscriptions from the default, recording
`CopyOrigin = host-default`. *Why:* every delivery, retirement and inbound path
already works from per-story subscriptions; copying keeps them unchanged and
makes a story's settings stable even if the default changes later. *Tradeoff:*
editing the default does not change stories already running; the Settings page
offers **Apply to stories without their own settings** as an explicit action.
A story whose operator saved settings (including "off") is never overwritten.
*Supports* O2. *Verified by* U2, U3, E2.

<a id="dec-3"></a>**DEC-3 — One topic/thread per workstream inside an existing channel; never a channel per workstream.**
*Choice:* new subscription field `ConversationScope = story | workstream`
(default `workstream` for new subscriptions; existing subscriptions keep `story`).
*Why:* Telegram bots cannot create groups at all, and 80 Slack channels per story
would bury the workspace. The base design already rejects "Slack channel per
workstream" (§8); this keeps that and only moves the conversation boundary from
story to workstream. *Supports* O3.

<a id="dec-4"></a>**DEC-4 — Create a workstream's conversation lazily, on its first message.**
*Choice:* a workstream binding is created when the first notification for that
workstream is routed, exactly as story bindings are created today
(`ChatEventService`). *Why:* most workstreams never ask a question or stall;
creating 80 topics up front would flood the Telegram topic list and spend
`createForumTopic` calls (which the base design treats as non-idempotent and
never retries blindly). *Tradeoff:* the first alert for a workstream waits one
extra provider call. *Supports* O3. *Verified by* U4.

<a id="dec-5"></a>**DEC-5 — A binding gains a `ChildKey`; the story conversation is `ChildKey = null`.**
*Choice:* `chat_conversation_bindings.ChildKey` (workstream id). The binding lookup
becomes *(subscription, revision, ChildKey)*. A notification's child key is its
`WorkstreamId`, else its `WorkItemId` when that id is a workstream of the story,
else null (story-level: plan-doc questions, decomposition, spawn status).
*Why:* bindings already own provisioning, crash recovery (`provisioning` →
`uncertain`), Slack root tracking and retirement; one more key reuses all of it.
*Supports* O3, O4, O5.

<a id="dec-6"></a>**DEC-6 — A workstream conversation scopes inbound replies to its workstream.**
*Choice:* when an inbound message's conversation matches a binding with a
`ChildKey`, candidate questions and action targets are filtered to that
workstream before the existing "exactly one question" rule runs. Story
conversations behave as today. The dispatcher still re-checks that the workstream
belongs to the bound story ([base design §6](chat-plugin-design.md)). *Supports* O4.

<a id="dec-7"></a>**DEC-7 — Retire a workstream's binding when the workstream reaches a terminal status.**
*Choice:* the scope lifecycle sweep (`ChatScopeLifecycleService`) marks child
bindings retired when their workstream is `complete`, `cancelled` or `historic`;
the existing retirement loop (`ChatDeliveryService.RetireTopicsAsync`) then closes
the Telegram topic (`closeForumTopic`, idempotent) or updates the Slack root.
*Why:* reuses the retirement path that already handles story deletion. A
workstream that is restarted later gets a fresh binding (new topic) on its next
message. *Supports* O5. *Verified by* U7, E5.

### What happens on failure, retry and cleanup

- **Topic/thread creation outcome unknown** (timeout during `createForumTopic`):
  the binding goes `provisioning → uncertain`, exactly as story bindings do. Forge
  does not retry blindly (it could create a duplicate topic); the delivery shows
  "needs repair" in the story's delivery history.
- **Telegram group lacks Manage topics:** provisioning fails `telegram-manage-topics-required`;
  the Slack destination keeps working (connections fail independently).
- **Ownership:** Forge only closes topics/roots recorded in its own bindings. It
  never closes or edits a topic it did not create, and never deletes messages.
  Disabling a story or retiring a subscription retires its story and workstream
  bindings together.

### Deliverables

| ID | Result (plain English) | Implementation | Depends on | Priority | Done when |
|---|---|---|---|---|---|
| <a id="d1"></a>D1 | One switch makes a connection available to all products, now and future | `chat_connections.AllProducts` column + migration; `PutConnectionAsync` writes grants for all products when set; `ProductsEndpoints.CreateProduct` grants `AllProducts` connections; connection editor checkbox "All products, including new ones" | — | P1 (unblocks harness) | U1 passes; E1 passes |
| <a id="d2"></a>D2 | Host-default destinations can be chosen once in Settings → Chat | New `chat_host_defaults` row set (destination, mode, content policy, event kinds, conversation scope) with revision; `GET/PUT /api/chat/host-defaults` (host admin); Settings panel "Story default" reusing `ChatRouteFields` | D1 | P1 | U2 passes; panel saves and reloads |
| <a id="d3"></a>D3 | New stories are enabled automatically from the default; operators can override or opt out | On spawn create / new run with no explicit settings, materialise `spawn_plugins` + subscriptions (`CopyOrigin = host-default`); story panel shows "Using host default" with **Customize** / **Turn off**; Settings action **Apply to stories without their own settings** | D2 | P1 | U3 passes; E2 passes |
| <a id="d4"></a>D4 | Subscriptions can be per-workstream | `chat_subscriptions.ConversationScope` (`story`/`workstream`, default `story` for existing rows via migration); route editor "Conversation: one per workstream / one per story" | — | P1 | U4 passes |
| <a id="d5"></a>D5 | Each workstream's first message creates its own topic/thread | `chat_conversation_bindings.ChildKey` + migration; `ChatEventService` resolves child key ([DEC-5](#dec-5)) and keys the binding lookup on it; provision title `"{storyId} · {workstream name}"` (Telegram 128-char limit); Slack root per child | D4 | P1 | U4, U5 pass; E3 passes |
| <a id="d6"></a>D6 | Replies inside a workstream conversation target that workstream | **No new code needed** (found during build): inbound question matching already compares the exact delivery address, which includes the topic/thread id (`ChatInboxService.ActiveQuestionsAsync`), and workstream actions already target the alert's own `WorkstreamId`. With D5 each workstream's messages live in its own conversation, so replies there can only reach that workstream | D5 | P2 | U4 (messages land only in their own conversation); E4 passes |
| <a id="d7"></a>D7 | Finished workstreams' conversations close | `ChatScopeLifecycleService` retires child bindings for terminal workstreams; existing retirement loop closes them | D5 | P2 | U7 passes; E5 passes |

Delivery order: D1 → D2 → D3 can ship and be enabled on their own (O1, O2 with
story-level conversations). D4 → D5 enable per-workstream conversations; D6 and
D7 complete the experience and must ship before per-workstream becomes the
default for new subscriptions. Runtime prerequisites (Telegram forum group, Slack
invite, valid tokens) are operator steps, not build dependencies.

### Stages

**Stage 1 — Product access (D1).** Result: harness and every future product see
shared connections. Entry: none. Priority P1.
1. Migration adding `chat_connections.AllProducts` (default false), ordered before any migration that queries the table.
2. `ChatConnectionWrite.AllProducts`; `PutConnectionAsync` grants all products when true.
3. `CreateProduct` grants every `AllProducts` connection to the new product in the same save.
4. Connection editor checkbox; clearing it asks whether to revoke auto-created grants.
5. Unit tests U1.
6. `manual`: tick the box for both connections; harness story panel lists them.
Exit: U1 green; manual check recorded.

**Stage 2 — Host default (D2, D3).** Result: new stories start enabled. Entry: Stage 1. P1.
1. `chat_host_defaults` entity, migration, revisioned PUT with host-admin capability.
2. Materialiser invoked from spawn create and from the new-run activation path.
3. "Explicit settings" marker so an operator save (including Off) is never overwritten.
4. Settings → Chat "Story default" panel; "Apply to stories without their own settings".
5. Story panel "Using host default · Customize · Turn off".
6. Unit tests U2, U3.
Exit: U2–U3 green; E2 authored and linted.

**Stage 3 — Per-workstream conversations (D4, D5).** Entry: Stage 2 not required (independent), P1.
1. Migrations: `chat_subscriptions.ConversationScope`, `chat_conversation_bindings.ChildKey`, unique index on (SubscriptionId, SubscriptionRevision, ChildKey) for live rows.
2. Child-key resolution from `WorkstreamId` / `WorkItemId` with story-membership check.
3. Binding lookup and creation keyed by child key; provisioning title per workstream.
4. Slack: root per child binding; Telegram: topic per child binding.
5. Route editor conversation-scope field (default per-workstream for new routes).
6. Unit tests U4, U5.
Exit: U4–U5 green; E3 authored and linted.

**Stage 4 — Targeted replies and retirement (D6, D7).** Entry: Stage 3. P2.
1. D6: verify address-scoped matching covers per-workstream conversations (no code change, see D6).
2. Lifecycle sweep retires child bindings of terminal workstreams.
3. Unit tests U6, U7.
Exit: U6–U7 green. E5 runs **in this stage** (it closes real topics — a
cleanup scenario, so it cannot wait for the batch).

**Stage 5 — Remote acceptance.** Entry: Stages 1–4 built and Forge.Api restarted
with operator approval. Runs E1–E4 as one batch against the real Slack workspace
and Telegram group. Exit: every scenario passed with recorded evidence.

## C. How we prove it

### Unit / local integration tests

| ID | Proves | Assertion |
|---|---|---|
| <a id="u1"></a>U1 | D1 | Setting AllProducts grants all products; creating a product afterwards grants it; clearing with revoke removes only auto-created grants |
| <a id="u2"></a>U2 | D2 | Host-default PUT requires host admin, enforces revision, rejects ungranted destinations |
| <a id="u3"></a>U3 | D3 | New spawn with no settings gets enabled subscriptions copied from the default; a spawn with explicit Off is untouched; Apply-to-existing skips explicit rows |
| <a id="u4"></a>U4 | D4, D5 | Two stalled workstreams in one story create two child bindings; a plan-doc question uses the story binding (`ChildKey` null); a second alert for the same workstream reuses its binding; `story`-scope subscriptions behave as before |
| <a id="u5"></a>U5 | D5 | Uncertain topic creation is not retried and is surfaced; Telegram missing Manage-topics fails only that destination |
| <a id="u6"></a>U6 | D6 | Covered by U4's assertion that each alert is sent only to its own workstream's conversation; reply matching is by that address. A dedicated inbound-reply test is still open |
| <a id="u7"></a>U7 | D7 | Completing a workstream retires only its child binding; the story binding and siblings stay live; restart creates a new binding |

### Remote E2E scenarios (real providers, deployed Forge.Api)

Common setup: the controller is this machine; the target is the running
Forge.Api (`http://localhost:5180`) after an approved restart onto the tested
build. Slack workspace `forge`, channel `#all-forge`; Telegram forum supergroup
created by the operator with the bot as admin + Manage topics. Tokens are
supplied at runtime from the existing DPAPI store, never from files in the repo.
Record: commit SHA, build time, story/workstream ids, provider message/topic ids,
`chat_deliveries` ids and states.

<a id="e1"></a>**E1 — New product sees shared connections.**
Given both connections have All products ticked. When a product `e2e-chat-{ts}` is
created via the UI. Then its story panel lists both connections. Cleanup: delete
the product; confirm its grant rows are gone.

<a id="e2"></a>**E2 — New story is auto-enabled.**
Given host default = `#all-forge` (interactive, per-workstream) + the Telegram forum.
When a story is spawned in harness. Then its row shows Chat **Ready** with both
destinations, without opening the panel. Cleanup: turn the story's chat off; delete the spawn.

<a id="e3"></a>**E3 — Two workstreams, two conversations.**
Given E2's story with two workstreams. When both stall (seeded via the operator
stall action) and a plan-doc question is raised. Then Slack shows two workstream
root messages plus the story root, and the Telegram group shows two workstream
topics plus the story topic, each holding only its own alert. Cleanup: as E5.

<a id="e4"></a>**E4 — Reply targets its workstream.**
Given E3. When the operator answers in workstream A's Telegram topic. Then A's
question is answered in Forge and B's stays open.

<a id="e5"></a>**E5 — Finished workstream closes its topic** (runs in Stage 4).
Given E3. When workstream A is accepted/completed. Then A's Telegram topic is
closed and its Slack root shows done; B's stays open; no Forge action touches any
topic Forge did not create. Cleanup: complete or cancel B and the story; verify
all three topics closed. If close fails, keep the binding rows (state `pending`)
and record them; do not delete them by hand.

### Scope of the guarantee

Passing evidence proves these contracts for the tested build, the `forge` Slack
workspace and the operator's Telegram group. It does not cover other providers,
Slack Enterprise Grid, or the legacy ChatBridge engine.

## Build record

<a id="build-record"></a>Working tree on top of `3641bc38` (uncommitted), 2026-11-19.

| Deliverable | Status | Evidence |
|---|---|---|
| D1 All products | built, locally verified | `ChatPerWorkstreamConversationTests.U1_AllProducts_Connection_Is_Granted_To_Every_Product_Including_New_Ones` |
| D2 Host default storage/API | built, locally verified | `U2_U3_Host_Default_…`, `U2_Per_Workstream_Scope_Requires_A_Topic_Or_Thread` |
| D3 Auto-enable | built, locally verified | `U2_U3_Host_Default_Enables_New_And_Unconfigured_Stories_But_Never_Overrides_Operator_Settings` |
| D4/D5 Per-workstream conversations | built, locally verified | `U4_U6_U7_Each_Workstream_Gets_Its_Own_Conversation_Which_Closes_When_It_Finishes`, `U5_A_Stuck_Alert_In_One_Workstream_Topic_Does_Not_Hold_Back_Another_Workstream` (real delivery loop, fake providers) |
| D6 Targeted replies | no code change; partially evidenced | see D6 / U6 |
| D7 Close finished conversations | built, locally verified | U4_U6_U7 (retire → `closed`; restart → new conversation) |
| Prerequisite: products without their own DB file | built, locally verified | `Product_Without_Its_Own_Db_File_Is_Available_Only_When_Its_Rows_Live_In_The_Control_Db` |
| Phase 107 upgrade on a real DB | `manual` rehearsal passed | `ChatPhase107UpgradeRehearsalTests` with `FORGE_UPGRADE_DB` = a copy of the live control DB (2026-11-19); negative check with an empty DB failed as expected |

Additional changes found necessary during the build:

- **Products without a DB file (harness, release) dropped every chat event** as
  `ownership-unverified` (35 events, latest 2026-11-19 23:04 UTC). `ChatScopeResolver.ProductAvailable`
  now accepts such a product when its repo rows live in the shared control DB; a
  moved product whose file is missing still reads as unavailable (degraded, never retired).
- **Migration order:** Phase 107 runs at the head of the chain because Phases 98/100
  recreate the chat model's indexes with IF NOT EXISTS and the new index uses `ChildKey`.
- **Delivery ordering is per conversation:** an actionable alert waiting in one
  workstream's topic no longer blocks another workstream's alerts on the same subscription.
- **Auto-enable latency:** the host-default sweep runs with the chat reconciler
  (every 5 minutes), so a new story is enabled within about 5 minutes; events raised
  before that are not replayed.
- **Telegram slice:** host defaults do nothing while `Chat:TelegramSlice:Enabled` is on.

Found while configuring the live host (2026-11-19, after the first restart):

- **Shared-DB products saw each other's stories.** harness and release both read the
  control DB, so the sweep enabled harness:DSH under both. The sweep now takes only
  spawns whose repo belongs to the product, and the reconciler retires a plugin row
  whose spawn belongs to another product (only for products that share the control
  DB). The stray release row was retired; its intro had already been cancelled by the
  delivery-time ownership check, so nothing was posted for it.
- **Migrated Legacy Telegram connection could not be saved** (`invalid-credential-reference`):
  its imported token reference is a file path. Re-saving an unchanged reference is now
  allowed; a changed reference is still validated.
- **Apply to existing stories** also refreshes stories still on the default
  (`CopyOrigin = host-default`), so a connection granted later reaches them. Stories
  with their own settings are still skipped.
- **Slack and the new bot read "token required"** because the secret store lives in
  `{Forge:Db:Folder}\chat-secrets` and the DB folder moved to `E:\forge\split` while
  the encrypted tokens stayed in `E:\forge\chat-secrets`. Fixed on this host by copying
  the files (originals kept). Not fixed in code: moving the DB folder again would
  repeat it.

## Live configuration (2026-11-19, applied with Playwright through the UI's own API)

- All products: Telegram bot `@f176…bot`, Slack `forge`, Legacy Telegram.
- Story default (revision 3, enabled): Slack `#all-forge` (thread) and Legacy Telegram
  forum "Legacy host alerts" (topic), both interactive, metadata only, one conversation
  per workstream.
- Applied to harness:DSH and local:HTTP-MEMORY; code-intelligence ENABLE-MCP keeps its
  own settings. First live per-workstream thread: "harness:DSH · Test Layers and Golden
  Helper" (open question, Slack, sent). This is an observation, not the Stage 5 E2E run.

Regression: chat, migration, schema, DbBoundary, split and product test sets — 624
passed, 24 failed; clean `3641bc38` fails the same 24 (pre-existing), so no new
failures. UI: `tsc` clean, vitest 203/203.

## Stage 5 remote acceptance — run 1 (2026-11-20 00:43–01:05 UTC)

Target: the live Forge.Api on this host (build = working tree on `3641bc38` after the
second restart), real Slack workspace `forge` (`#all-forge`) and the Legacy Telegram
forum group `-1004373092754`. Controller: Playwright driving the signed-in UI (its own
`chatApi`/`operatorFetch`), Telegram Web for provider-side observation. Fixture: a
throwaway product `e2e-chat-11191643` (own DB file) with one PAUSED story
`e2e-chat-11191643:E2E` (no agents run) and two test workstreams. Run id = the product id.

| Scenario | Result | Evidence |
|---|---|---|
| E1 new product sees connections | **pass** | `POST /api/products` 201; `GET /api/chat/connections?productId=…` lists bot, Legacy Telegram, Slack, all `allProducts` |
| E2 new story auto-enabled | **pass** | sweep wrote `spawn_plugins` (Enabled, `host-default`); intros `sent` to Slack root `1795135698.007739` and Telegram topic `97` |
| E3 one conversation per workstream | **pass** | liveness monitor raised both stall alerts; Slack roots `1795135993.960639` / `1795135997.936369`, Telegram topics `99` / `101`, each holding only its workstream's alert (seen in Telegram Web) |
| E4 reply targets its workstream | **blocked** | metadata-only content mode renders no buttons or answer controls (by design, §content policy). Needs redacted content enabled for the product — an operator decision |
| E5 finished workstream closes its conversation | **pass** | `POST /api/workstreams/{alpha}/cancel` 200; reconciler retired Alpha's bindings; Telegram `closeForumTopic` → `closed`, Slack root → retired; Beta stayed open |
| Cleanup | **pass with findings** | `DELETE /api/products/…` 204; topics 97/101 `closed`, Slack roots retired. Left behind: 3 grant rows (fixed in code, not yet deployed) and the product's DB file (product deletion never removes files; not changed) |

Follow-ups from the run: topic/thread titles and message bodies now name the story
and stage ("harness:DSH · Stage 1.2 — Gate Runner Core", "Story harness:DSH · Stage
…") instead of internal ids; the setup intro describes per-workstream conversations.
Built and unit-tested, deployment pending a restart.

## Stage 5 remote acceptance — run 2 (2026-11-20 01:27–01:45 UTC)

Same setup as run 1, product `e2e-chat-11191727`, after `DefaultProductContentMode =
redacted` (operator decision 2026-11-19, host `chat-settings.json`) and the stage-name
titles were deployed.

| Scenario | Result | Evidence |
|---|---|---|
| E1 | **pass** | 201 + three connections listed |
| E2 | **pass** | intros sent: Slack root `1795138300.323749`, Telegram topic `106` |
| E3 | **pass** | topics `108` "…E2E · Stage 1.1 — E2E check one" and `110` "…Stage 1.2 — E2E check two"; Slack roots `1795138596.122129` / `1795138600.096429` |
| E4 | **pass** | Playwright (Telegram Web, operator's linked account) pressed **cancel** under Stage 1.1's alert: Forge replied "Action accepted", the alert was edited to "This request is closed", Stage 1.1 → `cancelled`, Stage 1.2 still `stalled` |
| E5 | **pass** | Stage 1.1 topic `108` → `closed`, Slack root retired; Stage 1.2 open |

## All operator actions in chat (2026-11-19, built, not yet deployed)

Requirement: everything an operator does in Forge in response to an alert can be done
in the chat app. Before: answer questions, Retry/Accept/Cancel/Restart a stalled stage,
`pause`/`resume`/`status`/`questions` commands. Added:

| Action | Where | How | Capability |
|---|---|---|---|
| Story halted alert | new event `spawn-halted`, raised by the chat reconciler from the spawn's LastError (one per distinct error), routed to every subscription that takes stall alerts; resolved (buttons retired) when the error clears | — | — |
| 🔁 Retry story | halted alert | same as the banner's Retry (`StoryOperatorActionService.RetrySpawnAsync`) | `spawn.retry` (new) |
| ⚠ Restart doc | halted alert for a plan-doc halt (`plan doc '<key>' failed…`) | existing artifact restart, behind the existing 30-second confirmation | `artifact.restart` |
| 📌 Pin decision | halted alert and plan-doc question alerts | tap → "reply with the decision" prompt (Telegram force-reply; Slack thread reply) → pin shared with the web Pin (`StoriesEndpoints.AppendDecisionPin`) | `artifact.pin` (new) |
| Dismiss question | every question alert | `IOpenQuestionService.DismissAsync` | `question.answer` (as the web Questions tab) |

Buttons and question text appear only in redacted content mode (product maximum and
subscription), per the base content policy. The chat action revision no longer includes
the artifact key, so story- and document-level buttons on one alert share a revision.
Tests: `ChatAllActionsInChatTests` (halted alert + Retry + resolution, Pin via reply,
Dismiss, halt parsing); existing chat-runtime tests adjusted for the extra Dismiss button.

## Current live state (observed 2026-11-19, read-only)

- Engine: plugins. One story uses remote control (code-intelligence ENABLE-MCP → Slack `#all-forge`, thread per story).
- Grants: the Telegram bot is granted to code-intelligence, failover-cluster,
  local, raft, release; Slack `forge` only to code-intelligence. **harness has none.**
- Destinations: Slack `#all-forge` (ready); Telegram has only the legacy host-alert
  forum, `unverified`. The new Telegram bot has no destination yet.
- Connection health rows (dated Sept 2026) read `telegram-token-required` and
  `slack-bot-token-required`; a Telegram receiver is currently checkpointing.
  Token health must be re-verified before Stage 5.
