# Common.Chat — extract the chat bridge into a reusable library

> **Goal.** Split the app-agnostic half of the chat bridge (transport, options, token
> resolution, channel/topic mapping, inbound long-poll + subscription) out of `Forge.Api`
> into a standalone class library **`src/Common.Chat`** so (1) any app — not just Forge —
> can send/receive chat messages by subscribing a provider + channel, and (2) an inbound
> message from a channel can be mapped to the correct sender/receiver **inside the app**
> (for Forge: product + repo + spawn + workstream).
>
> **Builds on** the committed bridge (`chat-bridge-forge-plan.md` Phases 1–5 + the
> per-spawn-topic layer in `telegram-per-spawn-plan.md`). This is a refactor + a small
> routing feature, not a rewrite: the outbox, producers, action services, and dispatcher
> behavior stay in Forge and stay behavior-identical.

## Decisions (locked)

| Decision | Choice | Rationale |
|---|---|---|
| Library name / home | **`src/Common.Chat`**, namespace `Common.Chat` | Matches the repo's `Common.*` shared-lib convention (`Common.Diagnostics.EtwParser`). net8.0, `Microsoft.Extensions.*` abstractions only — no Forge, no EF. |
| Library boundary | **Transport + channel map + inbound pump move; outbox stays in Forge** | The outbox (`chat_notifications`), producers, renderer, and action dispatch are Forge domain + EF/control-DB concerns. Durability is the app's job; delivery + subscription is the lib's. |
| App ↔ chat mapping (req 3) | **Opaque `RouteKey` + `IChatChannelStore` seam** | The lib maps a channel address `(chatId, threadId)` ⇄ an app-defined route key both ways. Forge's key encodes `spawn/{productId}/{spawnId}`; workstream/work-item resolution stays reply-correlation in the dispatcher (as today) plus a new spawn-topic fallback. |
| Inbound subscription | **`IChatInboundHandler` DI registration + lib-hosted `ChatInboundPump`** | The app "subscribes" by registering handlers; the lib owns the long-poll loop, cursor persistence seam, and route resolution. Mirrors the existing dual-loop `ChatBridgeService` (which becomes outbound-only). |

## 1. Boundary at a glance

```
 Common.Chat (new lib — app-agnostic)              Forge.Api (stays)
 ────────────────────────────────────              ─────────────────
 IChatTransport + ChatSendResult/ChatButton/       chat_notifications outbox + ChatNotifier
   ChatInbound                                     ChatMessageRenderer (renders outbox rows)
 TelegramTransport (raw HttpClient)                ChatBridgeService → OUTBOUND pump only
 ChatTransportOptions + TelegramOptions            ChatBridgeOptions (section binding + outbox knobs)
 ChatTokenResolver (TokenBlobPath option)          ForgeChatChannelStore : IChatChannelStore
 ChatActions (button JSON)                           (over chat_spawn_topics; route = spawn/{pid}/{sid})
 ChatChannelBinding / IChatChannelStore            ForgeChatCursorStore : IChatCursorStore
 IChatChannelMap / ChatChannelMap                    (over chat_bridge_state)
   (EnsureChannel = create topic on first need,    ChatInboundDispatcher : IChatInboundHandler
    ResolveRoute = thread → app route key)           (link/commands/answers/actions — unchanged
 IChatInboundHandler + ChatInboundMessage             behavior + new route-aware correlation)
 IChatCursorStore (+ in-memory default)            ChatBridgeEndpoints, producers (using-fixes only)
 ChatInboundPump : BackgroundService
   (long-poll → resolve route → fan out to handlers)
```

Send path: producers → outbox row → Forge outbound pump → `IChatChannelMap.EnsureChannelAsync`
(route → thread) → `IChatTransport.SendAsync`. Receive path: `ChatInboundPump` long-polls →
`IChatChannelMap.ResolveRouteAsync(chatId, threadId)` → every registered `IChatInboundHandler`
gets `ChatInboundMessage(update, routeKey)` — Forge's dispatcher then correlates to the exact
work item / workstream via the outbox row (reply/callback) or the spawn route fallback.

## 2. Work breakdown

### Phase 1 — `Common.Chat` project + transport move
- [x] 1.1 `src/Common.Chat/Common.Chat.csproj` (net8.0; Options/Logging.Abstractions/Hosting.Abstractions/Http/DI.Abstractions/ProtectedData); add to `Workflow.sln`; `Forge.Api` gets a `ProjectReference`
- [x] 1.2 Move `IChatTransport` (+`ChatSendResult`/`ChatButton`/`ChatInbound`), `TelegramTransport`, `ChatActions` verbatim (namespace change only)
- [x] 1.3 New `ChatTransportOptions { Enabled, Platform, TokenBlobPath, Telegram }`; `TelegramOptions` moves in unchanged; `ChatTokenResolver` generalized to read `TokenBlobPath` (no hard-coded `%LOCALAPPDATA%\Forge`)

### Phase 2 — channel map + inbound pump (lib)
- [x] 2.1 `ChatChannelBinding` + `IChatChannelStore` (find-by-route / find-by-address / save-with-race-winner / close)
- [x] 2.2 `IChatChannelMap` / `ChatChannelMap`: `EnsureChannelAsync(routeKey, name)` (create-on-first-need via `CreateTopicAsync`, null in single-chat mode — same semantics as `ChatTopicResolver`), `ResolveRouteAsync(chatId, threadId)`
- [x] 2.3 `IChatInboundHandler` + `ChatInboundMessage(Update, RouteKey)`; `IChatCursorStore` + `InMemoryChatCursorStore`; `ChatInboundPump : BackgroundService` with public `RunOnceAsync` (enabled+token gate, per-update scope, per-handler try/catch)
- [x] 2.4 `ChatServiceCollectionExtensions` (`AddTelegramChatTransport`, `AddChatChannelMap<TStore>`, `AddChatInboundHandler<T>`)

### Phase 3 — Forge.Api consumes the lib
- [x] 3.1 `ChatBridgeOptions` keeps `SectionName` + outbox knobs, reuses lib `TelegramOptions`; Program.cs binds **both** option types from `Forge:ChatBridge`; `PostConfigure` sets the DPAPI blob path default (`%LOCALAPPDATA%\Forge\chat-token.bin`)
- [x] 3.2 `ForgeChatChannelStore` over `chat_spawn_topics` (route key `spawn/{productId}/{spawnId}`, `ForgeChatRoutes` helper); delete `ChatTopicResolver`/`IChatTopicResolver`; outbound pump calls `IChatChannelMap`
- [x] 3.3 `ForgeChatCursorStore` over `chat_bridge_state`; `ChatBridgeService` loses the inbound loop (outbound-only); `ChatInboundPump` hosted alongside it behind `enableBackground`
- [x] 3.4 `ChatInboundDispatcher : IChatInboundHandler`; product-scoped DB access via `PushAsyncLocalSlug(notif.ProductId)` for answer/choice/status paths (same idiom the workstream-action path already uses)
- [x] 3.5 **Req-3 routing:** a non-reply message in a spawn topic correlates via `RouteKey` → the spawn's open `open-questions` notification; `/status` in a spawn topic scopes to that spawn's product
- [x] 3.6 Program.cs rewiring + using-fixes in producers/endpoints (`DeliverableLoop`, `WorkstreamLivenessMonitor`, `CrewQueueWorker`, `AdoAuthService`, `ChatBridgeEndpoints`)

### Phase 4 — tests
- [x] 4.1 `Forge.Api.Tests/Chat/*` updated to new namespaces/types (FakeChatTransport → lib interface, TelegramTransport → `ChatTransportOptions`)
- [x] 4.2 Topic-routing tests exercise `ChatChannelMap` + `ForgeChatChannelStore` (same scenarios); inbound tests drive `ChatInboundPump.RunOnceAsync`
- [x] 4.3 New: route-key round-trip (`ForgeChatRoutes`), thread→route resolution, spawn-topic free-text answer fallback (`Free_Text_In_Spawn_Topic_Answers_That_Spawns_Question_Without_Reply`)
- [x] 4.4 `dotnet build` green; chat test suite green — 60/60 Chat tests + 66/66 adjacent StalledQuestions/Restart tests pass (operator-approved stop → test → `-ServerOnly -Rebuild` restart cycle, 2026-07-06); server boots clean with the bridge enabled and serves requests

## 3. Risks

| Risk | Mitigation |
|---|---|
| Behavior drift in the dispatcher during the move | Interface change only (`DispatchAsync(update)` → `HandleAsync(msg)`); existing Chat test suite is the gate. |
| Product-DB scoping change in answer paths | Uses the exact `PushAsyncLocalSlug` + fresh-scope idiom the workstream-action path shipped with; ambient behavior preserved when the notification has no `ProductId`. |
| Options double-binding (`ChatBridgeOptions` + `ChatTransportOptions` from one section) | Both are plain POCOs bound by property name; shape is a strict superset. Options tests assert both. |
| Lib reuse constraints | No EF/Forge deps in `Common.Chat`; persistence enters only through `IChatChannelStore`/`IChatCursorStore` seams (in-memory default provided). |
