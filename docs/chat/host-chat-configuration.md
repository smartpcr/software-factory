# Host-user chat configuration

This is the Stage 0A legacy configuration reference (2026-09-09). The legacy
Telegram bridge supports notifications and per-spawn forum topics. For current
plugin settings, Slack, operator linking and the API-based Telegram helper, use
the [setup guide](chat-plugin-setup.md). Stage 6 now provides explicit
[legacy import, cutover and rollback](chat-plugin-migration.md), preserving the
original credential reference and loading engine ownership from the control DB.

## Setup and opt-in

Forge.Api reads `%LOCALAPPDATA%\Forge\chat-settings.json` for the Windows account
running the host. This works both with `scripts/run-forge-ui.ps1` (repo-root working
directory, Development environment) and direct project starts. Edit this local
file only for an installation still using the legacy engine; do not place personal
configuration in tracked application JSON.

The Stage 5 `scripts/setup-telegram-bridge.ps1` now calls paired Forge plugin APIs.
Its old `-WriteConfig`, `-StoreToken` and `-SkipSmokeTest` parameters are retired.
It no longer writes this file, installs a desktop app or performs an automatic
smoke send. Follow the current guide for its parameters and explicit `-SendTest`.

The host file has this shape; replace the destination IDs locally, and set
`Enabled=true` only when ready to receive notifications:

```json
{
  "Forge": {
    "ChatBridge": {
      "Enabled": false,
      "AllowRemoteActions": false,
      "Platform": "telegram",
      "Telegram": {
        "BotToken": "",
        "AllowedChatIds": [],
        "SupergroupChatId": 0,
        "AllowedUserIds": []
      }
    }
  }
}
```

For another host user or an isolated fixture, set `FORGE_CHAT_SETTINGS_PATH` to
an **absolute** JSON file path in the launcher's environment. The helper honors
the same override. A missing file adds no settings, leaving tracked defaults
disabled unless explicitly overridden. Malformed JSON or a relative path fails
startup rather than loading a different user's settings.
The override selects the settings file, not the token location.

Keep tokens out of this file and source control. The resolver prefers a nonblank
`Forge__ChatBridge__Telegram__BotToken` environment value, then the DPAPI
CurrentUser blob at `%LOCALAPPDATA%\Forge\chat-token.bin`. `TokenBlobPath` under
`Forge:ChatBridge` can override the blob path. A valid blob with `Enabled=false`
leaves both hosted chat pumps idle without decrypting it.

## Precedence and restarts

From lowest to highest priority:

1. Application JSON loaded by ASP.NET Core, including Development/user secrets.
2. The host-user chat settings file.
3. Process environment values such as `Forge__ChatBridge__Enabled=false`.
4. Command-line configuration values.

The host file is inserted between JSON and environment providers; other provider
ordering follows [ASP.NET Core configuration precedence](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0).
Environment arrays use indexed keys such as
`Forge__ChatBridge__Telegram__AllowedChatIds__0`. Arrays merge by index across
providers: an empty array or a shorter list does not clear lower-priority entries.
Remove old chat sections from project/Development JSON when moving them to the
host file, and remove obsolete indexed environment variables when changing lists.

The launcher passes explicit environment values through unchanged and no longer
exports project chat JSON. If an older launcher populated chat variables in an
existing PowerShell session, start a fresh shell or remove those obsolete process
variables before launching. Windows user environment changes reach new shells.
The current helper does not alter process or user environment variables.

Restart Forge.Api at an operator-chosen time after editing the host file or
environment. Host-file edits are intentionally loaded at startup, not watched.
Restarting interrupts active agent work. The Stage 0A implementation was verified
with isolated fixtures; it did not restart the running Forge host or send Telegram
messages.

For this checkout, the previously tracked machine-specific chat settings were
copied into the host-user file before defaults were cleared. Its existing
notification opt-in was preserved, with remote actions explicitly disabled.
For another existing installation, move its non-secret chat section into its
own host file, retain the desired explicit `Enabled` setting, set
`AllowRemoteActions=false`, and keep its existing token storage.

## Notification-only behavior

With `Enabled=true` and `AllowRemoteActions=false`, the outbound pump sends
notifications into the existing destination/topic, omits action buttons, and
directs the operator to Forge to respond. It can still edit resolved messages.
The inbound pump consumes updates and advances the durable cursor, but the
dispatcher returns before linking, allow-list bootstrap, commands, answers,
workstream actions, or host/queue acknowledgements. Previously sent buttons and
free-text replies cannot change Forge state. Ignored updates are not replayed
from that cursor when remote handling is later enabled.

`POST /api/chat/link-token` returns 409 in disabled or notification-only mode;
`POST /api/chat/test-send` remains available when the bridge is enabled, subject
to shared authenticated authorization added in Stage 0B. Keep `AllowRemoteActions=false`
for notification-only legacy use. The legacy true setting is an explicit feature
opt-in, separate from authorization. The current helper never changes this setting.

## Stage 0A implementation review

Total requirements: **5 complete, 0 partial, 0 missing**.

| Requirement | Implementation and verification |
|---|---|
| 1. Move host values; safe tracked defaults | Existing settings preserved outside the repo; `Forge.Api/appsettings.json` has disabled switches, empty token/ID arrays, and group ID zero. `Clean_Checkout_With_Preexisting_Dpapi_Blob_Leaves_Both_Pumps_Idle` checks the actual tracked JSON. |
| 2. One local source with explicit opt-in and environment precedence | `ChatBridgeConfiguration.AddHostConfiguration`, Program startup, `Get-ForgeChatSettingsPath`, and `Save-ForgeTelegramSettings` agree on the path. Precedence, missing/malformed files, relative paths, and helper persistence are covered by `ChatHostConfigurationTests`. |
| 3. Accurate comments; preserve local operators | Updated `_comment`, setup output, and this guide describe shipped topics/pumps and notification-only opt-in. Local migration retained the existing destination and opt-in without copying IDs into new tracked content. |
| 4. Guard inbound domain actions | `ChatInboundDispatcher.DispatchAsync` guards all input before state access; link-token and outbound rendering honor the mode. `ChatRemoteActionGuardTests` exercises 32 input/switch combinations plus persisted cursor/state checks; existing interactive tests explicitly opt in. |
| 5. DPAPI idle and launcher restart fixture | A valid isolated DPAPI blob leaves both real hosted pump loops idle. The helper writes a temporary host file, two isolated TestServer startups reload it in the normal launcher's Development/repo-root context, and a recording process stub exercises the actual launcher start statement with unchanged environment overrides. |

Validation: **105 chat tests passed, zero failed/skipped** (baseline: 63 passed).
Tests build to an external artifacts folder to avoid touching the running server's
binaries. PowerShell syntax and the launcher handoff are included in that suite.
The restart fixture excludes the launcher's production process stopping, builds,
elevated broker, and live networking; it checks configuration persistence and the
process handoff. No live delivery claim is made.

```powershell
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj `
  --filter FullyQualifiedName~Forge.Api.Tests.Chat `
  --artifacts-path "$env:TEMP/forge-chat-stage0a-artifacts"
```

Correctness review found no remaining Stage 0A gaps. Stage 0B remains the next
prerequisite before enabling new remote-control entry points; its tasks and all
plugin/Slack implementation tasks remain unchecked.
