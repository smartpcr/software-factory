# Shared non-secret host configuration for setup-telegram-bridge.ps1.
# Forge.Api reads the same path directly; run-forge-ui.ps1 preserves env overrides.

function Get-ForgeChatSettingsPath {
    $chatPath = $env:FORGE_CHAT_SETTINGS_PATH
    if ([string]::IsNullOrWhiteSpace($chatPath)) {
        $chatPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Forge/chat-settings.json'
    }
    if (-not [IO.Path]::IsPathFullyQualified($chatPath)) {
        throw 'FORGE_CHAT_SETTINGS_PATH must be an absolute path.'
    }
    return $chatPath
}

function Save-ForgeTelegramSettings {
    param(
        [Parameter(Mandatory)][long]$ChatId,
        [Parameter(Mandatory)][long]$UserId,
        [string]$ApiBaseUrl = 'https://api.telegram.org',
        [string]$Path = (Get-ForgeChatSettingsPath)
    )
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Chat settings path must be absolute.' }
    $settings = if (Test-Path -LiteralPath $Path) {
        Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    } else { @{} }
    if (-not $settings.Forge) { $settings.Forge = @{} }
    if (-not $settings.Forge.ChatBridge) { $settings.Forge.ChatBridge = @{} }
    $chat = $settings.Forge.ChatBridge
    $chat.Enabled = $true
    $chat.AllowRemoteActions = $false
    $chat.Platform = 'telegram'
    $chat.TokenBlobPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Forge/chat-token.bin'
    if (-not $chat.Telegram) { $chat.Telegram = @{} }
    $chat.Telegram.BotToken = ''
    $chat.Telegram.AllowedChatIds = @($ChatId)
    $chat.Telegram.SupergroupChatId = $ChatId
    $chat.Telegram.AllowedUserIds = @($UserId)
    $chat.Telegram.ApiBaseUrl = $ApiBaseUrl

    # Prepare the full file before replacing it; a failed write keeps existing settings.
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $pendingPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($pendingPath, ($settings | ConvertTo-Json -Depth 20))
        [IO.File]::Move($pendingPath, $Path, $true)
    } finally {
        if (Test-Path -LiteralPath $pendingPath) { Remove-Item -LiteralPath $pendingPath }
    }
}
