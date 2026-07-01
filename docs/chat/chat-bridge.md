# CopilotChatBridge

Bridges Discord and/or Telegram to the **GitHub Copilot SDK**. A bot message becomes a
`Prompt` on a per-conversation `CopilotSession`; assistant deltas stream back as throttled
message edits.

```
Discord / Telegram  ->  BackgroundService adapter  ->  CopilotAgentService
                                                          -> CopilotClient (bundled Copilot CLI host)
                                                          -> CopilotSession (one per chat, multi-turn)
```

## Prerequisites

- **.NET 8+**
- **GitHub Copilot CLI** installed and authenticated on the host, plus an active Copilot
  subscription — the SDK drives that CLI. (Or use BYOK by configuring `SessionConfig` with your
  own provider key; then no Copilot sub is needed.)
- A Discord bot token and/or a Telegram bot token (BotFather).

## Setup

```bash
dotnet restore
# configure at least one platform (env vars override appsettings.json)
export DISCORD_TOKEN="..."      # optional
export TELEGRAM_TOKEN="..."     # optional
export COPILOT_MODEL="claude-sonnet-4.5"   # or gpt-5, etc.
dotnet run
```

**Discord:** enable the *Message Content Intent* in the Developer Portal (Bot settings). The bot
replies in DMs, or in a guild when @mentioned.

**Telegram:** create the bot via @BotFather. To let it see all group messages (not just commands),
disable privacy mode in BotFather, otherwise DM it directly.

Type `/reset` in either platform to drop the session and start a fresh conversation.

## Where the real work is (not the SDK call itself)

1. **Session mapping + turn serialization** (`CopilotAgentService`). One `CopilotSession` per
   `conversationId`, and a `SemaphoreSlim` so two messages in the same chat can't run a turn
   against the same session concurrently. A single persistent `session.On(...)` handler routes
   streamed deltas into a per-turn `Channel<string>` — this decouples the SDK's synchronous
   callback from the async message-edit I/O.

2. **Streaming -> throttled edits** (`StreamingReply`). Both platforms rate-limit edits and cap
   message length. The buffer edits on an interval (Discord ~1.2s, Telegram ~1.6s), splits into a
   new message at the char cap (Discord 1900, Telegram 4000), and skips no-op edits (both platforms
   reject "message not modified").

3. **Tool permissions** (`SessionConfig.OnPermissionRequest`). The agent can call `bash`,
   `edit_file`, etc. The default policy approves read-only tools and denies writes. For a bot that
   should *do* things, either widen the policy, run the CLI inside a sandbox (GA cloud/local
   sandboxes), or scope it to a working directory.

## Version notes

- Copilot SDK: GA (`GitHub.Copilot.SDK` 1.x, 2026-06-02). The `CopilotClient` / `CopilotSession` /
  event API (`AssistantMessageDeltaEvent`, `SessionIdleEvent`, ...) is stable from 0.1.x through 1.x.
- Telegram.Bot 22.x uses `SendMessage` / `EditMessageText`. On v19 rename to
  `SendTextMessageAsync` / `EditMessageTextAsync`.
- Discord.Net 3.x.
