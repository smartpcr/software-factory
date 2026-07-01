# Telegram per-spawn channels — setup + implementation plan

> **Goal.** Run Telegram on Windows against Forge so **each story spawn gets its own
> Telegram forum topic** (a "channel" thread), and the operator can interact with that
> spawn's crew inside its topic — answer open questions, inject steering, and drive
> stalled-workstream actions.
>
> **Builds on** the committed chat bridge (Phases 1-5, `docs/chat/chat-bridge-forge-plan.md`),
> which already ships: outbox + outbound pump, `TelegramTransport`, inbound long-poll +
> dispatcher, `IOpenQuestionService`/`IOperatorActionService`, and the producers for
> open-questions / stalled-workstream / strain / auth. **This plan is the per-spawn-topic
> layer on top** — the existing pieces target a single operator DM; here they route to a
> per-spawn thread in one shared supergroup.

## Decisions (locked)

| Decision | Choice | Why |
|---|---|---|
| Per-spawn "channel" | **Forum topics in one supergroup** | A bot **can** `createForumTopic` (it cannot create groups/channels). Two-way replies work inside a topic. Fully auto-provisioned per spawn. |
| Interaction scope | **Structured** (questions + steering + stalled actions + strain/auth) | Reuses Phases 1-5 deterministically, scoped per topic. No free-form agent-relay (a later option). |

---

## Part A — Telegram setup on Windows (operator, one-time)

Checklist — no code, ~10 minutes:

- [ ] **A1. Create the bot.** In Telegram, DM **@BotFather** → `/newbot` → name it (e.g. `Forge Ops`) → copy the **bot token** (`123456:ABC-…`).
- [ ] **A2. Create the forum supergroup.** New Group → add the bot + yourself → open **Group Settings → Topics → enable**. (Topics turns the group into a *forum* — `is_forum=true`.) Name it e.g. `Forge — <machine>`.
- [ ] **A3. Make the bot an admin with topic rights.** Group → Administrators → add the bot → enable **Manage Topics** (and Post/Edit Messages). Without *Manage Topics* the bot can't create per-spawn topics.
- [ ] **A4. Disable BotFather privacy mode** (so the bot sees non-command replies in the group): @BotFather → `/setprivacy` → select the bot → **Disable**.
- [ ] **A5. Get the supergroup chat id.** Send any message in the group, then browse
      `https://api.telegram.org/bot<TOKEN>/getUpdates` — the numeric `chat.id` (a large negative id like `-100…`) is the supergroup id. (Or use Forge's `/api/chat/test-send` after A6 with the id from the JSON.)
- [ ] **A6. Configure Forge** (`appsettings.json` / env / DPAPI blob — `ChatTokenResolver` already supports all three):
      ```jsonc
      "Forge": { "ChatBridge": {
        "Enabled": true,
        "Platform": "telegram",
        "Telegram": {
          "BotToken": "",                       // prefer env Forge__ChatBridge__Telegram__BotToken
          "SupergroupChatId": -1001234567890,   // NEW (Part B) — the forum supergroup
          "AllowedUserIds": [ 11111111 ],       // NEW — telegram user ids allowed to drive Forge
          "LongPollTimeoutSec": 25
        }
      }}
      ```
      Keep the token **out of source**: set env `Forge__ChatBridge__Telegram__BotToken` or drop a DPAPI blob at `%LOCALAPPDATA%\Forge\chat-token.bin`.
- [ ] **A7. Link your operator identity.** In any topic (or DM the bot): mint a token via `POST /api/chat/link-token`, then send `/link <token>` — binds your Telegram **user id** to your Forge user (Part B changes this from chat-id to user-id for the group context).
- [ ] **A8. Restart Forge.Api** (operator-approved) so the bridge picks up `Enabled=true` + the token.

**Verify:** `POST /api/chat/test-send` posts to the group; spawn a story and confirm a new **topic** appears named after the story.

---

## Part B — Forge implementation (new phases on Phases 1-5)

### Phase 6 — Transport: forum-topic support
> The transport currently sends to a flat `chatId`. Add topic threading + topic creation.

- [x] **6.1 `IChatTransport` gains threading**
  - [x] `SendAsync` takes an optional `string? threadId` (Telegram `message_thread_id`) — added to `SendAsync`; `EditAsync` unchanged (editMessageText keys on message_id, no thread needed)
  - [x] `Task<string?> CreateTopicAsync(string chatId, string name, CancellationToken ct)` → returns the new `message_thread_id` (Telegram `createForumTopic`)
  - [x] `ChatInbound` gains `ThreadId` (from `message.message_thread_id`) — parsed in `TelegramTransport.ParseUpdates`
  - _Tests:_
    - [x] `SendAsync` with a `threadId` includes `message_thread_id` in the request JSON (+ omitted when null)
    - [x] `CreateTopicAsync` parses `result.message_thread_id` from a `createForumTopic` response (+ null on error)
    - [x] `ParseUpdates` maps `message_thread_id` → `ChatInbound.ThreadId` (null when not a topic)

### Phase 7 — Spawn ↔ topic mapping + lifecycle
> One topic per spawn, auto-created on first need, cached in a mapping.

- [x] **7.1 Persistence** — control-DB `chat_spawn_topics(SpawnId PK, ProductId, ChatId, ThreadId, Name, CreatedAt, ClosedAt)` + `chat_notifications.SpawnId` column (`Phase97Migration`; catalog/runner/`DbBoundary`)
- [x] **7.2 `IChatTopicResolver`** — `EnsureTopicAsync(spawnId, productId, name)`: return the mapped `ThreadId`, else `CreateTopicAsync`, persist, return. Idempotent + concurrency-safe (DB unique on `SpawnId` + `DbUpdateException` re-read on race).
  - [ ] Topic rename on story retitle (`editForumTopic`) — _not done_. `CloseTopicAsync` marks the mapping `ClosedAt` locally but does **not** yet call `closeForumTopic` upstream (no transport method); wire on spawn `done`/deleted later.
  - _Tests:_
    - [x] First `EnsureTopicAsync` creates + persists a topic; second returns the cached thread id (no second create)
    - [ ] Concurrent `EnsureTopicAsync` for one spawn creates exactly one topic — _not tested_ (guarded by the DB unique + catch; in-memory-SQLite parallel test is flaky, deferred)
    - [ ] Spawn completion closes the topic — _deferred with the upstream close above_
    - [x] `EnsureTopicAsync` returns null in single-chat mode (`SupergroupChatId == 0`)

### Phase 8 — Route notifications to the spawn's topic
> Every alert lands in its spawn's thread; the outbound pump resolves + threads it.

- [x] **8.1 Producers tag the spawn** — resolve `SpawnId` at enqueue: `WorkstreamLivenessMonitor` → `WorkstreamRow.StorySpawnId`; `DeliverableLoop` → looks up the work item's workstream `StorySpawnId`. `OpenQuestionsAlert`/`WorkstreamStalledAlert` gained an optional `SpawnId`; `ChatNotifier` maps it to `chat_notifications.SpawnId`.
- [x] **8.2 Outbound pump threads the send** — before send, `EnsureTopicAsync(row.SpawnId, row.ProductId, row.Title)` → send with that `threadId`. Alerts with no resolvable spawn (or topic-create failure, or single-chat mode) fall back to the general (no-thread) group post.
  - _Tests:_
    - [x] An open-questions alert for a workstream is sent into that spawn's topic (`message_thread_id` set)
    - [x] Two spawns' alerts land in two different topics
    - [x] A spawn-less alert (e.g. queue-strain) posts to the group root (no thread)

### Phase 9 — Group-context identity + inbound correlation
> In a shared group the chat id is shared; the **sender** (`from.id`) identifies the operator.

- [x] **9.1 Allowlist by user id** — the inbound dispatcher keys the subscriber lookup on `ChatInbound.FromId` (stored in `chat_subscribers.ChatId`, which equals the user id — in a DM `chat.id == user.id`, so no schema change or backfill). `AllowedUserIds` auto-bootstraps a linked subscriber from the group; `/link` binds `FromId`. Unlinked / non-allow-listed senders ignored.
- [x] **9.2 Correlation stays reply/callback-based** — `ReplyToMessageId → ChatMessageId` (as built) resolves the notification; actions authorized against the linked user's role.
  - [ ] Inbound `ThreadId` cross-check (reply's thread belongs to the notification's spawn) — _not done_; correlation is already unambiguous via `ChatMessageId`, so the thread check is defense-in-depth, deferred.
  - _Tests:_
    - [x] A reply/callback from an allowed user answers/acts; from a non-allow-listed user is ignored
    - [x] `/link` binds the sender's user id (not the group id)
    - [x] An allow-listed user is bootstrapped from the group without `/link`

### Phase 10 — Config + polish
- [x] `ChatBridgeOptions.Telegram`: added `SupergroupChatId`, `AllowedUserIds`; when `SupergroupChatId != 0` the bridge operates in **group mode** — the notifier targets the supergroup, the pump routes by topic, and inbound authorizes by user id. Otherwise DM / single-chat mode (`AllowedChatIds[0]`).
- [ ] `/spawns` command — list this operator's active spawns + their topics; per-topic `/status` — _not done_ (polish, deferred)
- [x] Backfill: existing open spawns get topics lazily on their next alert — satisfied by the create-on-first-need design (no separate backfill needed)

---

## Data flow (per spawn)

```
Story spawn starts ──► (first alert) EnsureTopicAsync ─ createForumTopic ─► message_thread_id (persisted)
        │
DeliverableLoop / monitors ─ enqueue chat_notifications (SpawnId set) ─► outbound pump
        │                                                                     │
        └───────────────────────── sendMessage(chat=supergroup, thread=spawn) ┘
Operator replies IN the topic ─ getUpdates (message_thread_id + from.id) ─► dispatcher
        │                                                                     │
        └── authorize by from.id ─ correlate reply→notification ─ IOpenQuestionService / IOperatorActionService
```

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| Bot lacks *Manage Topics* → `createForumTopic` 400 | Preflight check on boot (call `getChat`, assert `is_forum`); surface a clear setup error; fall back to group-root posts |
| Topic sprawl (many spawns) | `closeForumTopic` on spawn done; optional archive/reap of `ClosedAt` topics |
| Shared-group authorization | Allowlist by `from.id` (not chat id); role-gate actions; ignore non-allowlisted senders (logged) |
| `message_thread_id` missing on some updates (General topic) | Treat absent thread as the group root; correlation still uses reply/callback ids |
| Rate limits (per-topic + per-group) | Existing pump throttling + retry/backoff; batch low-priority alerts |
| Token/secret exposure in a shared group | Never post tokens/codes (already enforced); topics are inside a private supergroup you control |

## Out of scope (call out)
- Free-form agent chat relay (operator message → spawn agent prompt) — a follow-up if wanted.
- Discord parity (the `IChatTransport` seam makes it additive; Discord threads ≈ Telegram topics).
- A Forge.UI surface for managing topics/subscribers (admin REST first).

## Sequencing
Phase 6 (transport) → 7 (mapping) → 8 (routing) → 9 (identity) → 10 (config/polish). Each is independently shippable + testable behind `Forge:ChatBridge:Enabled` (off by default). Part A (operator setup) can happen any time before enabling.
