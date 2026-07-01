# Forge Chat Bridge — Implementation Plan

> **Goal.** Let Forge reach a human **operator** over a chat app (Telegram first, Discord later)
> to (a) get **answers to open questions** and (b) drive **stalled-workstream actions**, and let the
> operator's chat replies flow back into Forge to resume or act — without opening the web UI.
>
> This adapts the pattern in [`chat-bridge.md`](chat-bridge.md) (Discord/Telegram ⇆ .NET adapter,
> throttled edits, tool-permission gating) but **inverts the direction**: the generic sample turns a
> chat message into an agent prompt; here **Forge initiates** (question / stall alert) and the reply
> resolves an existing Forge wait/resume loop.

## Decisions (locked)

| Decision | Choice | Rationale |
|---|---|---|
| Platform (first) | **Telegram**, behind a platform-agnostic `IChatTransport` | Raw-HTTP long-poll (`getUpdates`/`sendMessage`), no gateway websocket, no heavy SDK — matches Forge's "raw `HttpClient`, small deps" provider house style. Inline keyboards map cleanly to choice questions. Discord slots in later as a second transport. |
| Hosting | **In-process `BackgroundService`** in `Forge.Api` | All triggers (open questions, stalls, strain) are raised in-process; direct DB/service access; simplest reply correlation. Mirrors `BudgetCooldownReconciler`. |
| Inbound scope | **Answer open questions + stalled-workstream actions** | Matches the stated need. Destructive actions (Restart) are confirm-gated. |

---

## 1. Architecture at a glance

```
 Producers (in-process)                Bridge service                    Telegram
 ─────────────────────                 ─────────────                     ────────
 DeliverableLoop / TaskExec  ─┐
 WorkstreamLivenessMonitor   ─┼─ IChatNotifier.Enqueue ─► chat_notifications (control DB, outbox)
 CrewQueueWorker (strain)    ─┘                                   │
                                                                  │  outbound pump (timer)
                                             ChatBridgeService ───┼──► IChatTransport.Send/Edit ──► bot message
                                             (BackgroundService)  │
                                                                  │  inbound pump (long-poll getUpdates)
   Forge actions (resume/act) ◄── ChatInboundDispatcher ◄─────────┘◄── reply / callback_query
        │
        ├─ IOpenQuestionService.AnswerAsync   (extracted from TaskExecutionEndpoints)
        └─ IOperatorActionService.{Retry,Accept,Cancel,Restart} (extracted from WorkstreamsEndpoints)
```

**Two design pillars:**

1. **A durable outbox (`chat_notifications`)** — the exploration confirmed Forge has **no persisted
   notification store** (alerts are ephemeral SignalR fan-outs + local `SystemNotify` toasts). A durable
   row is required for reliable delivery, retry, **reply correlation**, and edit-on-resolution. Producers
   never call Telegram directly; they enqueue a row. If the bridge is disabled, rows simply expire.

2. **Shared action core, two front doors.** The answer/resume logic
   (`TaskExecutionEndpoints.AnswerQuestions` → `TryUnparkAndResumeAsync`) and the workstream actions
   (`WorkstreamsEndpoints.Restart/Retry/Cancel/Accept`) are currently **inline private endpoint methods**.
   They must be **extracted into callable services** so the chat dispatcher and the HTTP endpoints share
   one implementation (no logic fork). This is the single biggest refactor in the plan.

---

## 2. New persistence (control DB)

Add to `Persistence/Entities.cs` + a new **`Phase95Migration.cs`** (Phase94 is the highest slot on disk;
chain it at the end of `Program.cs` after `EnsureCreatedAsync`, guarded idempotently with
`pragma_table_info` / `CREATE TABLE IF NOT EXISTS`). Both tables live in the **control DB**
(`ControlDbContext`) because the operator + their chat identity are cross-product/global.

### `chat_notifications` (outbox + correlation)
| Column | Purpose |
|---|---|
| `Id` (PK) | notification id |
| `Kind` | `open-questions` \| `workstream-stalled` \| `queue-strain` |
| `ProductId`, `RepoId` | scope, so inbound dispatch opens the right `ProductDbContext` (Phase 80 `RepoId`) |
| `WorkItemId` / `WorkstreamId` / `QueueId` | the domain anchor the reply acts on |
| `DedupeKey` (UNIQUE) | e.g. `oq:{workItemId}:{maxIter}` — prevents duplicate sends on repeated ticks |
| `Title`, `Body` | rendered message text |
| `ActionsJson` | available inline actions (choices / action buttons) + their `callback_data` |
| `Status` | `pending → sent → answered/acted \| expired \| failed` |
| `Platform`, `ChatId`, `ChatMessageId` | which chat + the sent message id (for edit-on-resolution) |
| `Attempts`, `LastError`, `CreatedAt`, `SentAt`, `RespondedAt`, `ExpiresAt` | delivery + lifecycle |

Partial-unique on `DedupeKey WHERE Status IN (pending,sent)` (same idempotency idiom as `task_queue`).

### `chat_subscribers` (operator identity + allowlist)
| Column | Purpose |
|---|---|
| `Id` (PK), `Platform`, `ChatId` | the chat account |
| `ForgeUserId` | link to the Forge user (role gating reuses `RequireRole`) |
| `LinkToken`, `LinkedAt` | one-time `/link <token>` flow |
| `ProductFilterJson` | optional: only notify this operator for certain products |
| `Muted`, `CreatedAt` | quiet-hours / opt-out |

**Security invariant:** inbound updates from a `ChatId` **not** present (linked) in `chat_subscribers`
are ignored (logged). Actions are authorized against the linked `ForgeUserId`'s role.

---

## 3. New code (all under `Forge.Api/Services/Chat/` unless noted)

| File | Responsibility |
|---|---|
| `ChatBridgeOptions.cs` | `const SectionName = "Forge:ChatBridge"`; `Enabled`, `Platform`, `Telegram { BotToken, AllowedChatIds[], LongPollTimeoutSec }`, `OutboundTickInterval`, `MaxAttempts`, `NotificationTtl`. POCO convention like `BudgetCooldownOptions`. |
| `IChatTransport.cs` | Platform-agnostic seam: `SendAsync(chatId, text, buttons?)`, `EditAsync(chatId, messageId, text, buttons?)`, `PollUpdatesAsync(offset, ct) → IReadOnlyList<ChatInbound>`. `ChatInbound` normalizes message text-reply **and** `callback_query` into `{ ChatId, FromId, ReplyToMessageId?, Text?, CallbackData?, UpdateId }`. |
| `TelegramTransport.cs` | Raw `HttpClient` (named client `"chat-telegram"`) against `https://api.telegram.org/bot{token}/`. `sendMessage` (with `reply_markup` inline keyboard), `editMessageText`, `getUpdates?offset=&timeout=`. Follows `AdoProvider` client-per-call + `System.Text.Json` parsing. Respects the 4096-char cap + edit rate limits (§`chat-bridge.md` StreamingReply). |
| `IChatNotifier.cs` / `ChatNotifier.cs` | The **only** thing producers call: `EnqueueOpenQuestionsAsync(...)`, `EnqueueWorkstreamStalledAsync(...)`, `EnqueueQueueStrainAsync(...)`. Writes a dedupe-keyed `chat_notifications` row (no network). Also `MarkResolvedAsync(anchor)` so the bridge edits the chat message when the item is resolved **elsewhere** (web UI). |
| `ChatBridgeService.cs` | `BackgroundService` hosting **two loops** via `Task.WhenAll`: (1) **outbound pump** on `OutboundTickInterval` — claim `pending` rows, render, `transport.Send`, store `ChatMessageId`, mark `sent` (+ edit `sent`→resolved rows); (2) **inbound long-poll** — `PollUpdatesAsync(offset)`, persist the `offset` cursor, hand each update to the dispatcher. Public `RunOutboundOnceAsync` / `HandleInboundAsync` for deterministic tests (mirrors `RunOnceAsync`). |
| `ChatInboundDispatcher.cs` | Correlate an inbound update to a `chat_notifications` row (via `ReplyToMessageId → ChatMessageId`, or `CallbackData`), authorize the sender against `chat_subscribers`, resolve `ProductId → ProductDbContext` (`IProductDbContextFactory`), and invoke the right action service. Handles the `/link`, `/status`, `/help` commands and the Restart confirm handshake. |
| `Endpoints/ChatBridgeEndpoints.cs` | Admin/ops REST: `GET /api/chat/subscribers`, `POST /api/chat/link-token` (mint a `/link` token for the current user), `GET /api/chat/outbox` (recent notifications + status), `POST /api/chat/test-send` (smoke test). Role-gated `product-owner`. |

### Extractions (shared action core — no logic fork)
| Extract from | Into | Used by |
|---|---|---|
| `TaskExecutionEndpoints.AnswerQuestions` + `TryUnparkAndResumeAsync` (incl. plan-doc / workstream / bare-WI branches, resume markers, spawn `LastError` clear, SignalR broadcasts) | `Services/IOpenQuestionService.cs` (`AnswerAsync(workItemId, answers, actor, ct)`) | HTTP endpoint **and** `ChatInboundDispatcher` |
| `WorkstreamsEndpoints.Restart/Retry/Cancel/Accept` cores | `Services/IOperatorActionService.cs` | HTTP endpoints **and** dispatcher |

The endpoints become thin wrappers over these services — behavior-preserving. Existing endpoint tests
must stay green (this is the safety net for the extraction).

---

## 4. Producers — where alerts originate

Add one `IChatNotifier` call at each existing raise point (no behavior change beyond an extra outbox row):

- **Open questions** → in the path that persists `deliverable_questions` and flips status to
  `awaiting-answers` (`DeliverableLoop` / where questions are imported in `TaskExecutionEndpoints`).
  Enqueue `open-questions`, dedupe `oq:{workItemId}:{iter}`. `ActionsJson`: for `Kind=choice` questions,
  one inline button per choice; for `open` questions, an "reply with your answer" prompt (free-text
  correlated by `reply_to_message`).
- **Stalled workstreams** → `WorkstreamLivenessMonitor` where it sets `Status=stalled` + `StallReason`
  and broadcasts `workstream.changed`. Enqueue `workstream-stalled` with action buttons
  `[Retry] [Accept] [Cancel] [Restart ⚠]`.
- **Queue strain** (optional, phase 5) → `CrewQueueWorker` where it sends `queue.strained` to
  `user.{leadId}`. Enqueue `queue-strain` with `[Confirm] [Reassign…]`.

**Resolution echo:** the existing resume/act broadcasts already refresh the web UI. To keep chat in sync
when the operator acts in the **web UI** instead, `ChatNotifier.MarkResolvedAsync(anchor)` flips the
outbox row and the outbound pump edits the sent message to "✅ answered by <user>".

---

## 5. Wiring (`Program.cs`) & config

- `builder.Services.Configure<ChatBridgeOptions>(config.GetSection(ChatBridgeOptions.SectionName));`
- `builder.Services.AddHttpClient("chat-telegram");` (near the provider client registrations, ~line 359)
- `AddSingleton<IChatTransport, TelegramTransport>()`, `AddScoped<IChatNotifier, ChatNotifier>()`,
  `AddScoped<IOpenQuestionService,…>()`, `AddScoped<IOperatorActionService,…>()`,
  `AddSingleton<ChatBridgeService>()`
- Gate the hosted service exactly like the others:
  ```csharp
  if (enableBackground && chatOptions.Enabled)
      builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatBridgeService>());
  ```
  (respects the `FORGE_TEST_GUARDRAIL` kill-switch; disabled by default until a token is configured).
- Chain `Phase95Migration` at the end of the migration sequence.

**`appsettings.json`:**
```jsonc
"Forge": {
  "ChatBridge": {
    "Enabled": false,
    "Platform": "telegram",
    "Telegram": { "BotToken": "", "AllowedChatIds": [], "LongPollTimeoutSec": 25 },
    "OutboundTickInterval": "00:00:05",
    "NotificationTtl": "1.00:00:00",
    "MaxAttempts": 5
  }
}
```

**Secret handling:** keep `BotToken` out of source — set via env `Forge__ChatBridge__Telegram__BotToken`,
or (for at-rest encryption) a DPAPI blob under `%LOCALAPPDATA%\Forge\chat-token.bin` mirroring the MSAL
cache in `AdoAuthService`. Never commit the token; `AllowedChatIds` bootstraps the first subscriber.

---

## 6. Message & interaction design

- **Open question (choice):** message = title + rationale (`ExplanationJson.recommendation`) + inline
  keyboard, one button per choice; `callback_data = a:{notifId}:{externalId}:{choiceIdx}`. Tap →
  `IOpenQuestionService.AnswerAsync` → if all answered, resume fires; bridge edits message to the pick.
- **Open question (free-text):** message asks the operator to **reply** to it; the reply's
  `reply_to_message_id` correlates to `ChatMessageId`; body becomes the answer.
- **Stalled workstream:** title + `StallReason` + `[Retry] [Accept] [Cancel] [Restart ⚠]`.
  `Retry/Accept/Cancel` execute immediately; **`Restart` requires a second confirm tap** (edits the
  message to "Tap again within 30s to confirm restart — this wipes the worktree") before calling
  `IOperatorActionService.Restart`. Every action writes the existing audit rows (`forge_actions`,
  `agent_assignments`) via the reused service core.
- **Commands:** `/link <token>` (bind chat↔user), `/status` (list this operator's pending questions +
  stalled workstreams), `/mute`, `/help`.
- Char cap 4096 (truncate + "… open in UI" deep link); skip no-op edits (Telegram rejects
  "message not modified") per `chat-bridge.md`.

---

## 7. Work breakdown & progress tracker

Check the box as each item lands. Every stage lists its **test scenarios** (also checkboxes) — a stage is
"done" only when its steps **and** its test scenarios are checked. All tests are deterministic
(xUnit + `WebApplicationFactory`, `FakeChatTransport`, single-tick driving) — see §8 for shared
conventions. Each phase is independently shippable.

### Phase 1 — Persistence + transport foundation ✅ **COMPLETE** (19/19 tests green; server boots with migration applied)
> `Phase95Migration`, both entities, options, `IChatTransport` + `TelegramTransport`, admin `test-send`.
> No producers, no inbound handling yet — proves an outbound round-trip end to end.

- [x] **1.1 Options & config**
  - [x] `ChatBridgeOptions.cs` (`SectionName="Forge:ChatBridge"`, Telegram sub-options, intervals, TTL, `MaxAttempts`)
  - [x] `Forge:ChatBridge` block added to `appsettings.json` (`Enabled=false` default)
  - [x] Token resolution: env `Forge__ChatBridge__Telegram__BotToken` **or** DPAPI blob `%LOCALAPPDATA%\Forge\chat-token.bin` (`ChatTokenResolver`)
  - _Test scenarios:_
    - [x] Options bind from config section; missing section yields safe defaults (`Enabled=false`)
    - [x] Env-var override wins over `appsettings.json` for `BotToken`
    - [x] Blank `BotToken` → resolves null / transport returns failure, stays idle (no crash)
- [x] **1.2 Entities & migration**
  - [x] `ChatNotificationRow` + `ChatSubscriberRow` in `Entities.cs`; registered on `ControlDbContext` + `DbModelControl`
  - [x] `Phase95Migration.cs` (idempotent `CREATE TABLE IF NOT EXISTS`, partial-unique on `DedupeKey`), catalogued (`PhaseMigrationCatalog`/`ExecutionOrder`) + `DbBoundary` control tables
  - _Test scenarios:_
    - [x] Migration runs on a fresh DB and creates both tables + the partial-unique index
    - [x] Migration is idempotent — running twice is a no-op, no exception
    - [x] Inserting two rows with the same `DedupeKey` in an open state violates the unique index (resolved state does not)
- [x] **1.3 Transport seam + Telegram adapter**
  - [x] `IChatTransport` (`SendAsync`/`EditAsync`/`PollUpdatesAsync`) + normalized `ChatInbound`
  - [x] `TelegramTransport` on named client `"chat-telegram"` (`sendMessage`/`editMessageText`/`getUpdates`); 4096-char cap; inline-keyboard serialization
  - [x] `FakeChatTransport` (test double: captures sends/edits, injects updates)
  - _Test scenarios:_
    - [x] `SendAsync` with buttons produces the expected `reply_markup`/`inline_keyboard` JSON (recording handler)
    - [x] A message > 4096 chars is truncated with an "… open in UI" suffix
    - [x] `PollUpdatesAsync` maps both a text reply and a `callback_query` into `ChatInbound` correctly
    - [x] A Telegram 5xx / network error surfaces as a caught failure, not an unhandled throw
- [x] **1.4 Admin endpoints (smoke)**
  - [x] `Endpoints/ChatBridgeEndpoints.cs`: `POST /api/chat/test-send`, role-gated `product-owner`
  - [x] DI wiring in `Program.cs` (options, named HTTP client, transport singleton) — hosted service **not** started yet
  - _Test scenarios:_
    - [x] `test-send` as `product-owner` calls the transport once with the given text (via `FakeChatTransport`)
    - [x] `test-send` as a non-PO user → 403
    - [x] `test-send` with `Enabled=false` returns a clear "bridge disabled" (409) problem response

### Phase 2 — Outbox + outbound pump
> Producers get a durable enqueue path; the service drains it to chat with dedupe, retry, and TTL.
> Alerts appear in chat; replies still ignored.

- [ ] **2.1 Notifier (enqueue API)**
  - [ ] `IChatNotifier` + `ChatNotifier`: `EnqueueOpenQuestionsAsync` / `EnqueueWorkstreamStalledAsync` / `EnqueueQueueStrainAsync` / `MarkResolvedAsync`
  - [ ] Dedupe-key computation (`oq:{wi}:{iter}`, `stall:{wsId}:{reasonHash}`, `strain:{crewId}`)
  - _Test scenarios:_
    - [ ] Enqueue writes a `pending` row with correct `Kind`, anchor ids, `ProductId`/`RepoId`, `ActionsJson`
    - [ ] Second enqueue with the same `DedupeKey` while first is still open → no duplicate row
    - [ ] `MarkResolvedAsync` flips an open row to a resolved state and stamps `RespondedAt`
- [ ] **2.2 Outbound pump loop**
  - [ ] `ChatBridgeService : BackgroundService`, outbound loop on `OutboundTickInterval`; public `RunOutboundOnceAsync`
  - [ ] Claim `pending` → render → `transport.Send` → store `ChatMessageId` → mark `sent`
  - [ ] Edit pass: rows flipped to resolved get their sent message edited to "✅ …"
  - _Test scenarios:_
    - [ ] `RunOutboundOnceAsync` sends exactly one message per `pending` row and marks it `sent` with the returned `ChatMessageId`
    - [ ] A row already `sent` is not re-sent on the next tick
    - [ ] A resolved row triggers exactly one `EditAsync` and no further edits afterward
    - [ ] Rendering respects the char cap and includes the inline-keyboard actions from `ActionsJson`
- [ ] **2.3 Retry, TTL & failure isolation**
  - [ ] `Attempts++`/`LastError` on send failure; give up + mark `failed` past `MaxAttempts`
  - [ ] `ExpiresAt`/`NotificationTtl` → stale `pending` rows flip to `expired`
  - [ ] Per-tick exceptions swallowed + logged (loop never dies), gated by `FORGE_TEST_GUARDRAIL`
  - _Test scenarios:_
    - [ ] Transient send failure increments `Attempts` and retries next tick; succeeds before `MaxAttempts`
    - [ ] Exceeding `MaxAttempts` marks the row `failed` and stops retrying
    - [ ] A row past `ExpiresAt` is marked `expired` and never sent
    - [ ] An exception thrown mid-tick is caught; the loop processes remaining rows and survives to the next tick

### Phase 3 — Action-core extraction (behavior-preserving)
> Pull the resume/act logic out of the endpoints into shared services so chat and HTTP can't fork.
> **Guard:** all pre-existing endpoint tests stay green.

- [ ] **3.1 Open-question service**
  - [ ] `IOpenQuestionService.AnswerAsync(workItemId, answers, actor, ct)` extracted from `TaskExecutionEndpoints.AnswerQuestions` + `TryUnparkAndResumeAsync` (plan-doc / workstream / bare-WI branches, resume markers, spawn `LastError` clear, SignalR broadcasts intact)
  - [ ] `TaskExecutionEndpoints.AnswerQuestions` reduced to a thin wrapper over the service
  - _Test scenarios:_
    - [ ] Existing `StalledQuestionsRecoveryTests` / answer endpoint tests pass unchanged against the wrapper
    - [ ] Service answering the last question schedules the resume (plan-doc path adds the resume-pending marker)
    - [ ] Workstream-id path demotes `stalled`→`active` and clears only the bogus stall reason (operator stalls untouched)
    - [ ] Partial answer (not all questions) leaves `awaiting-answers` set and schedules no resume
- [ ] **3.2 Operator-action service**
  - [ ] `IOperatorActionService.{Retry,Accept,Cancel,Restart}` extracted from `WorkstreamsEndpoints`; endpoints become wrappers
  - [ ] Audit rows (`forge_actions`, `agent_assignments`) still written from the service core
  - _Test scenarios:_
    - [ ] Existing `WorkstreamsEndpoints` restart/retry/accept/cancel tests pass unchanged
    - [ ] `Restart` via the service wipes worktree/memory/deliverables + clears `StallReason` (parity with the endpoint)
    - [ ] `Accept` flips the latest deliverable verdict to `pass` and lets the orchestrator open the PR
    - [ ] Each action writes the expected audit row(s)

### Phase 4 — Inbound + open-question answering (end-to-end)
> Long-poll inbound, correlate replies, authorize, and answer open questions from chat. Wire the
> open-question **producer**. First full loop: question in chat → answer → Forge resumes.

- [ ] **4.1 Subscriber identity + `/link`**
  - [ ] `chat_subscribers` allowlist; `POST /api/chat/link-token` mints a one-time token; `/link <token>` binds `ChatId`↔`ForgeUserId`
  - [ ] Unknown/unlinked `ChatId` inbound is ignored + logged
  - _Test scenarios:_
    - [ ] `/link` with a valid token creates a subscriber bound to the minting user
    - [ ] `/link` with an expired/used/invalid token is rejected, no subscriber created
    - [ ] Inbound from an unlinked `ChatId` is dropped (no action, logged once)
- [ ] **4.2 Inbound pump + correlation**
  - [ ] Inbound long-poll loop with persisted `offset` cursor; public `HandleInboundAsync`
  - [ ] `ChatInboundDispatcher`: correlate via `ReplyToMessageId→ChatMessageId` **or** `CallbackData`; resolve `ProductId→ProductDbContext`
  - [ ] Command routing (`/status`, `/help`, `/mute`)
  - _Test scenarios:_
    - [ ] A `callback_data` reply resolves to the exact originating notification + product DB
    - [ ] A free-text `reply_to_message` resolves to its notification
    - [ ] An uncorrelatable update is ignored gracefully
    - [ ] `offset` advances so the same update is never processed twice
    - [ ] `/status` returns this operator's pending questions + stalled workstreams
- [ ] **4.3 Open-question answering wired + producer**
  - [ ] Choice callback → `IOpenQuestionService.AnswerAsync`; free-text reply → answer; message edited to reflect the pick
  - [ ] Producer: enqueue `open-questions` where `deliverable_questions` are persisted / status flips to `awaiting-answers`
  - _Test scenarios:_
    - [ ] Enqueue → inject choice callback → `deliverable_questions.Answer` set, `awaiting-answers` cleared, resume scheduled
    - [ ] Free-text reply for an `open`-kind question is stored verbatim as the answer
    - [ ] Answering in the **web UI** flips the outbox row and edits the chat message ("answered by …")
    - [ ] Multi-question item: chat answers only after all are resolved does the resume fire
    - [ ] Unauthorized subscriber's answer attempt is rejected (role gate)

### Phase 5 — Stalled-workstream actions + strain (+ optional Discord)
> Stall/strain producers, action buttons, Restart confirm handshake, resolution echo. Discord adapter
> is additive behind `IChatTransport`.

- [ ] **5.1 Stalled-workstream producer + actions**
  - [ ] Producer at `WorkstreamLivenessMonitor` stall point → enqueue `workstream-stalled` with `[Retry][Accept][Cancel][Restart ⚠]`
  - [ ] Dispatcher maps buttons → `IOperatorActionService`; **Restart** requires a second confirm tap within 30s
  - _Test scenarios:_
    - [ ] Stall enqueues one alert with the four action buttons and the `StallReason` text
    - [ ] `Retry`/`Accept`/`Cancel` callbacks invoke the matching service action once, with audit
    - [ ] First `Restart` tap only arms the confirm (edits message, no wipe); second tap within 30s executes
    - [ ] `Restart` confirm window expiry → tapping again re-arms rather than executing stale
    - [ ] Acting in the web UI edits the chat message and disables the buttons (idempotent — a late tap is a no-op)
- [ ] **5.2 Queue-strain producer (optional)**
  - [ ] Producer at `CrewQueueWorker` `queue.strained` site → enqueue `queue-strain` with `[Confirm][Reassign…]`
  - _Test scenarios:_
    - [ ] Strain enqueues an alert to the lead's linked subscriber
    - [ ] `Confirm` clears the strain flag; `Reassign` routes to the chosen crew (parity with `POST /api/queue/{id}/reassign`)
- [ ] **5.3 Discord adapter (optional / deferred)**
  - [ ] `DiscordTransport : IChatTransport` (gateway or interactions webhook)
  - _Test scenarios:_
    - [ ] The full Phase-4/5 loop passes against `DiscordTransport` behind the same dispatcher (transport-agnostic contract test)

---

## 8. Testing conventions (shared by all stage scenarios above)

- `FakeChatTransport` captures sent/edited messages and injects inbound updates — the whole loop runs
  without a live bot. `ChatBridgeService` exposes `RunOutboundOnceAsync` / `HandleInboundAsync` for
  single-tick driving (mirrors the `RunOnceAsync` convention on other hosted services).
- Outbound HTTP to Telegram is faked with `StubHttpMessageHandler` (no WireMock), matching the
  provider test style.
- **Extraction safety net:** the pre-existing `AnswerQuestions` / workstream-action endpoint tests run
  unchanged against the new service wrappers — the proof that Phase 3 forks no behavior.
- Reuse existing fixtures/assertions from `StalledQuestionsRecoveryTests`,
  `StalledQuestionsLeakTests`, and the `WorkstreamsEndpoints` tests where a scenario mirrors them.
- All bridge tests are guarded by `FORGE_TEST_GUARDRAIL`; the hosted service never auto-starts under test.
- New test files: `ChatBridgeOutboundTests`, `ChatBridgeInboundTests`, `ChatCorrelationTests`,
  `ChatAnswerRoundTripTests`, `ChatActionRoundTripTests`, `Phase95MigrationTests`.

---

## 9. Risks & mitigations

| Risk | Mitigation |
|---|---|
| **Action-core extraction** changes behavior of a hot path (resume/restart) | Behavior-preserving refactor; existing endpoint tests are the gate; ship extraction as its own phase/PR. |
| Reply hits the **wrong product DB** (split DBs) | Outbox row carries `ProductId`/`RepoId`; dispatcher resolves `ProductDbContext` via `IProductDbContextFactory` before acting. |
| **Duplicate alerts** on repeated monitor ticks | `DedupeKey` partial-unique on open outbox rows. |
| **Unauthorized control** of Forge via chat | Hard allowlist (`chat_subscribers` + `AllowedChatIds` bootstrap); role gate via linked `ForgeUserId`; destructive Restart double-confirm; every action audited. |
| Bridge coupled to Forge.Api restart rule | Bridge is off by default; a bad token/network only degrades chat delivery (outbox retries), never the agent loops. Standalone-process split remains a future option behind the same `IChatTransport`. |
| Telegram outage / long-poll stalls | Outbound retries with `MaxAttempts` + TTL expiry; inbound loop reconnects with the persisted `offset`; failures logged, never thrown out of the tick. |
| Bot token leakage | Env var or DPAPI blob; never in source/appsettings; `.gitignore` the local token file. |

---

## 10. Out of scope (call out explicitly)

- Streaming agent **token** output to chat (the `chat-bridge.md` StreamingReply use case) — noisy for an
  ops channel; alerts + actions only.
- Discord adapter is **designed-for but deferred** to phase 5+ (the `IChatTransport` seam makes it
  additive; Discord needs a gateway websocket / `Discord.Net`, unlike Telegram's raw HTTP).
- A Forge.UI settings screen for managing subscribers (admin REST endpoints ship first; UI later).
- Replacing the ephemeral SignalR/`SystemNotify` paths — the bridge is **additive**, not a rewrite.
```
