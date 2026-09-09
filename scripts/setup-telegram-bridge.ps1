#Requires -Version 7.0
<#
.SYNOPSIS
    Part A of docs/chat/telegram-per-spawn-plan.md — set up + verify the Telegram
    forum-supergroup that backs Forge's per-spawn chat bridge, on Windows.

.DESCRIPTION
    Automates the scriptable steps: validates the bot token, discovers the
    supergroup chat id and your Telegram user id from getUpdates, verifies the
    group is a forum and the bot is an admin with Manage-Topics, runs a
    createForumTopic + sendMessage + deleteForumTopic smoke test, stores the
    token (DPAPI blob matching ChatTokenResolver, or a user env var), and
    optionally writes the non-secret config. The manual Telegram-app steps
    (create bot / group / enable Topics / add bot admin / disable privacy) are
    printed and gated behind a confirmation — a bot cannot do them for you.

.PARAMETER BotToken
    The @BotFather token. If omitted you'll be prompted (input hidden).

.PARAMETER StoreToken
    dpapi (default) = encrypt to %LOCALAPPDATA%\Forge\chat-token.bin (what
    ChatTokenResolver reads); env = set the user env var
    Forge__ChatBridge__Telegram__BotToken; none = don't persist.

.PARAMETER WriteConfig
    Write %LOCALAPPDATA%\Forge\chat-settings.json (or FORGE_CHAT_SETTINGS_PATH):
    explicitly enable notifications with AllowRemoteActions=false. Without this
    switch, ask before writing. Tracked appsettings files are never modified.

.PARAMETER SkipSmokeTest
    Skip the createForumTopic/sendMessage/deleteForumTopic verification.

.PARAMETER ApiBaseUrl
    Telegram Bot API base. Default https://api.telegram.org.

.EXAMPLE
    .\scripts\setup-telegram-bridge.ps1
.EXAMPLE
    .\scripts\setup-telegram-bridge.ps1 -StoreToken env -WriteConfig
#>
[CmdletBinding()]
param(
    [string]$BotToken,
    [ValidateSet('dpapi', 'env', 'none')][string]$StoreToken = 'dpapi',
    [switch]$WriteConfig,
    [switch]$SkipSmokeTest,
    [switch]$SkipTelegramInstall,
    # Retained for command-line compatibility; notification-only setup does not
    # sign in to Forge or mint account-link tokens.
    [string]$ForgeUser,
    [string]$ForgeApiUrl = 'http://localhost:5180',
    [ValidateSet('ado', 'github')][string]$AuthKind = 'ado',
    [string]$ApiBaseUrl = 'https://api.telegram.org'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'forge-chat-settings.ps1')
$ChatSettingsPath = Get-ForgeChatSettingsPath
$BlobPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Forge/chat-token.bin'

function Write-Step { param([string]$m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "  [OK] $m" -ForegroundColor Green }
function Write-Warn2{ param([string]$m) Write-Host "  [!]  $m" -ForegroundColor Yellow }
function Fail       { param([string]$m) Write-Host "  [X]  $m" -ForegroundColor Red; exit 1 }

if (-not $IsWindows) { Fail 'This setup script targets Windows (DPAPI + Windows host).' }

# --- Telegram Bot API helper -------------------------------------------------
function Invoke-Telegram {
    param([string]$Token, [string]$Method, [hashtable]$Body = @{})
    $uri = "$ApiBaseUrl/bot$Token/$Method"
    try {
        if ($Body.Count -gt 0) {
            $resp = Invoke-RestMethod -Uri $uri -Method Post -Body ($Body | ConvertTo-Json -Depth 6) -ContentType 'application/json'
        }
        else {
            $resp = Invoke-RestMethod -Uri $uri -Method Get
        }
    }
    catch {
        # Telegram returns JSON with ok:false + description even on 4xx.
        $detail = $_.ErrorDetails.Message
        if ($detail) { try { $detail = ($detail | ConvertFrom-Json).description } catch {} }
        return [pscustomobject]@{ ok = $false; description = ($detail ?? $_.Exception.Message) }
    }
    return $resp
}

# --- 0a. Install Telegram Desktop (skip if present) --------------------------
function Test-TelegramInstalled {
    $paths = @(
        (Join-Path $env:APPDATA 'Telegram Desktop\Telegram.exe'),
        (Join-Path ${env:ProgramFiles} 'Telegram Desktop\Telegram.exe'),
        (Join-Path ${env:LOCALAPPDATA} 'Programs\Telegram Desktop\Telegram.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($paths) { return $paths[0] } else { return $null }
}

Write-Step '0a. Telegram Desktop (skip if already installed)'
$tgExe = Test-TelegramInstalled
if ($tgExe) {
    Write-Ok "Already installed: $tgExe"
}
elseif ($SkipTelegramInstall) {
    Write-Warn2 'Not found and -SkipTelegramInstall set; install from https://desktop.telegram.org.'
}
elseif (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Warn2 'winget unavailable. Install Telegram Desktop from https://desktop.telegram.org, then re-run.'
}
else {
    Write-Host '  Installing Telegram Desktop via winget...'
    winget install --id Telegram.TelegramDesktop -e --source winget --accept-package-agreements --accept-source-agreements
    $tgExe = Test-TelegramInstalled
    if ($tgExe) { Write-Ok "Installed: $tgExe" } else { Write-Warn2 'winget finished but exe not located; it may still be on the Start menu.' }
}

# --- 0b. Sign in to Telegram (phone-based; NOT Gmail) ------------------------
Write-Step '0b. Sign in to Telegram Desktop'
Write-Host @'
  Telegram authenticates by PHONE NUMBER (SMS/call code) or QR scan from your
  phone. Forge account linking is disabled in notification-only mode.
'@
if ($tgExe) { try { Start-Process $tgExe } catch {} ; Write-Host '  Launched Telegram Desktop — sign in with your phone number now.' }
$null = Read-Host '  Press Enter once you are signed in to Telegram'

# --- 0c. Manual prerequisites ------------------------------------------------
Write-Step 'Manual Telegram steps (a bot cannot do these) — A1-A4'
@'
  A1. @BotFather -> /newbot -> copy the bot token.
  A2. New Group -> add the bot + yourself -> Group Settings -> Topics -> ENABLE.
  A3. Group -> Administrators -> add the bot -> enable "Manage Topics".
  A4. @BotFather -> /setprivacy -> select the bot -> Disable (so it sees replies).
  Then: send ONE message in the group so getUpdates can discover its id.
'@ | Write-Host
if ((Read-Host 'Have you completed A1-A4 and sent a message in the group? (y/N)') -notmatch '^[yY]') {
    Write-Warn2 'Complete A1-A4 first, then re-run.'; exit 0
}

# --- 1. Token + getMe --------------------------------------------------------
Write-Step '1. Validate bot token (getMe)'
if (-not $BotToken) {
    $sec = Read-Host 'Paste the bot token' -AsSecureString
    $BotToken = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}
$BotToken = $BotToken.Trim()
$me = Invoke-Telegram -Token $BotToken -Method 'getMe'
if (-not $me.ok) { Fail "Token rejected: $($me.description)" }
$botId = $me.result.id
Write-Ok "Bot @$($me.result.username) (id $botId)"

# --- 2. Discover supergroup chat id + your user id (getUpdates) ---------------
Write-Step '2. Discover supergroup + your user id (getUpdates)'
$updates = Invoke-Telegram -Token $BotToken -Method 'getUpdates'
if (-not $updates.ok) { Fail "getUpdates failed: $($updates.description)" }
$msgs = @($updates.result | ForEach-Object { $_.message; $_.channel_post } | Where-Object { $_ })
if (-not $msgs) { Fail 'No recent messages. Send a message in the group, then re-run (Telegram keeps updates ~24h).' }

$chats = $msgs | Where-Object { $_.chat.type -eq 'supergroup' } |
    Select-Object -ExpandProperty chat -Unique | Sort-Object id -Unique
$users = $msgs | Where-Object { $_.from } | Select-Object -ExpandProperty from |
    Sort-Object id -Unique

if (-not $chats) { Fail 'No supergroup found in updates. Ensure Topics are enabled (that makes it a supergroup) and you messaged in it.' }

$chat = if ($chats.Count -eq 1) { $chats[0] } else {
    Write-Host '  Multiple supergroups found:'
    for ($i = 0; $i -lt $chats.Count; $i++) { Write-Host "    [$i] $($chats[$i].title)  (id $($chats[$i].id))" }
    $chats[[int](Read-Host 'Pick the Forge group index')]
}
$chatId = $chat.id
Write-Ok "Supergroup '$($chat.title)' -> chat id $chatId"

$me2 = $users | Where-Object { -not $_.is_bot }
$userId = if ($me2.Count -eq 1) { $me2[0].id } else {
    Write-Host '  Human senders seen:'
    for ($i = 0; $i -lt $me2.Count; $i++) { Write-Host "    [$i] $($me2[$i].first_name) @$($me2[$i].username)  (id $($me2[$i].id))" }
    $me2[[int](Read-Host 'Pick YOUR user index')].id
}
Write-Ok "Your Telegram user id -> $userId  (for AllowedUserIds / linking)"

# --- 3. Verify forum + bot admin + manage-topics -----------------------------
Write-Step '3. Verify forum + bot permissions'
$getChat = Invoke-Telegram -Token $BotToken -Method 'getChat' -Body @{ chat_id = $chatId }
if (-not $getChat.ok) { Fail "getChat failed: $($getChat.description)" }
if (-not $getChat.result.is_forum) { Fail "Group is NOT a forum. Enable Group Settings -> Topics, then re-run." }
Write-Ok 'Group is a forum (Topics enabled).'

$mem = Invoke-Telegram -Token $BotToken -Method 'getChatMember' -Body @{ chat_id = $chatId; user_id = $botId }
if (-not $mem.ok) { Fail "getChatMember failed: $($mem.description)" }
if ($mem.result.status -ne 'administrator') { Fail "Bot is '$($mem.result.status)', not an administrator. Add it as admin." }
if (-not $mem.result.can_manage_topics) { Fail "Bot admin lacks 'Manage Topics'. Enable it in the group admin settings." }
Write-Ok 'Bot is an admin with Manage-Topics.'

# --- 4. Smoke test: create topic -> post -> delete ---------------------------
if (-not $SkipSmokeTest) {
    Write-Step '4. Smoke test (createForumTopic -> sendMessage -> deleteForumTopic)'
    $topic = Invoke-Telegram -Token $BotToken -Method 'createForumTopic' -Body @{ chat_id = $chatId; name = 'Forge setup test' }
    if (-not $topic.ok) { Fail "createForumTopic failed: $($topic.description)" }
    $threadId = $topic.result.message_thread_id
    Write-Ok "Created test topic (message_thread_id $threadId)"

    $send = Invoke-Telegram -Token $BotToken -Method 'sendMessage' -Body @{
        chat_id = $chatId; message_thread_id = $threadId
        text = "✅ Forge chat bridge connected. This test topic will be deleted."
    }
    if (-not $send.ok) { Write-Warn2 "sendMessage into topic failed: $($send.description)" } else { Write-Ok 'Posted into the topic.' }

    $del = Invoke-Telegram -Token $BotToken -Method 'deleteForumTopic' -Body @{ chat_id = $chatId; message_thread_id = $threadId }
    if ($del.ok) { Write-Ok 'Deleted the test topic.' } else { Write-Warn2 "Could not delete test topic ($($del.description)); remove it manually." }
}

# --- 5. Store the token ------------------------------------------------------
Write-Step "5. Store the bot token ($StoreToken)"
switch ($StoreToken) {
    'dpapi' {
        try {
            New-Item -ItemType Directory -Force -Path (Split-Path $BlobPath) | Out-Null
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($BotToken)
            $enc = [System.Security.Cryptography.ProtectedData]::Protect(
                $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
            [System.IO.File]::WriteAllBytes($BlobPath, $enc)
            Write-Ok "Encrypted token -> $BlobPath (DPAPI, current user). ChatTokenResolver reads this."
        }
        catch {
            Write-Warn2 "DPAPI unavailable ($($_.Exception.Message)). Falling back to a user env var."
            [Environment]::SetEnvironmentVariable('Forge__ChatBridge__Telegram__BotToken', $BotToken, 'User')
            $env:Forge__ChatBridge__Telegram__BotToken = $BotToken
            Write-Ok 'Set user env var Forge__ChatBridge__Telegram__BotToken (restart the shell/service to pick it up).'
        }
    }
    'env' {
        [Environment]::SetEnvironmentVariable('Forge__ChatBridge__Telegram__BotToken', $BotToken, 'User')
        $env:Forge__ChatBridge__Telegram__BotToken = $BotToken
        Write-Ok 'Set user env var Forge__ChatBridge__Telegram__BotToken.'
    }
    'none' { Write-Warn2 'Token NOT persisted (per -StoreToken none). Provide it via the environment yourself.' }
}

# --- 6. Explicit notification-only opt-in (non-secret) -----------------------
Write-Step '6. Host-user chat configuration'
Write-Host "  Settings: $ChatSettingsPath"
Write-Host '  This enables notifications. Remote linking, commands, replies, and buttons stay disabled.'
Write-Host '  Forge__ChatBridge__* environment values override this file; a stored token alone never enables chat.'
$chatEnvOverrides = @(Get-ChildItem Env: | Where-Object Name -Like 'Forge__ChatBridge__*')
if ($chatEnvOverrides.Count -gt 0) {
    Write-Warn2 'Chat environment overrides are present. Review their names/values locally before restarting; setup does not clear them.'
}
$doWrite = $WriteConfig -or ((Read-Host 'Enable notifications and save host-user chat settings now? (y/N)') -match '^[yY]')
if ($doWrite) {
    Save-ForgeTelegramSettings -ChatId $chatId -UserId $userId -ApiBaseUrl $ApiBaseUrl -Path $ChatSettingsPath
    Write-Ok "Saved $ChatSettingsPath (Enabled=true, AllowRemoteActions=false; no bot token in JSON)."
} else {
    Write-Warn2 'Settings unchanged. Re-run with -WriteConfig when ready to opt in.'
}

# --- 7. Restart Forge.Api (loads the bridge) ---------------------------------
Write-Step '7. Restart Forge.Api to load the bridge'
Write-Host '  Restart to load saved settings and token changes.'
Write-Host '  NOTE: a restart kills any in-flight agent / LLM work.' -ForegroundColor Yellow
if ((Read-Host '  Restart Forge.Api now? (y/N)') -match '^[yY]') {
    try { & (Join-Path $RepoRoot 'scripts/run-forge-ui.ps1') -ServerOnly; Write-Ok 'Restart invoked.' }
    catch { Write-Warn2 "Restart failed: $($_.Exception.Message). Run it manually." }
}
else {
    Write-Warn2 'Skipped. Restart later:  .\scripts\run-forge-ui.ps1 -ServerOnly'
}

Write-Step 'Done'
@"
  * Settings: $ChatSettingsPath
  * A saved configuration is loaded on the next normal launcher start.
  * Test delivery: POST /api/chat/test-send from Forge, or watch for story alerts.
  * Shipped: outbound notifications and per-spawn Telegram forum topics.
  * Remote actions and account linking are disabled by this setup. Respond in
    Forge; shared authenticated authorization is tracked in plugin plan Stage 0B.
  * Configuration and environment precedence: docs/chat/host-chat-configuration.md
"@ | Write-Host -ForegroundColor Green
