#Requires -Version 7.0
<#
.SYNOPSIS
    Configure a Telegram plugin connection through the authenticated Forge APIs.
.DESCRIPTION
    Saves credentials in the host's protected store, discovers or records a channel,
    and validates access. Test messages require -SendTest. No desktop installation,
    provider polling, configuration-file patching, or service restart is performed.
    Use a paired WebRequestSession or a one-use Forge browser pairing code.
.PARAMETER PairingCode
    One-use pairing code from Forge operator enrollment. Do not reuse a chat link challenge.
.PARAMETER UseSavedCredential
    Keep the connection's saved credential instead of prompting for a bot token.
.PARAMETER SpawnId
    Optional parent row to enable, with exactly one ProductIds value. Existing destinations
    are retained. Omit to configure the connection and channel without enabling a row.
.EXAMPLE
    ./scripts/setup-telegram-bridge.ps1 -BotId 12345 -ChannelId '-100123' -ProductIds my-product -PairingCode (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BotId,
    [Parameter(Mandatory)][string]$ChannelId,
    [string]$ChannelName = 'Forge Ops',
    [string]$ConnectionId,
    [string[]]$ProductIds = @(),
    [string]$SpawnId,
    [switch]$NotificationOnly,
    [switch]$UseSavedCredential,
    [switch]$SendTest,
    [securestring]$BotToken,
    [securestring]$PairingCode,
    [Microsoft.PowerShell.Commands.WebRequestSession]$WebSession,
    [string]$ForgeApiUrl = 'http://localhost:5180'
)
$ErrorActionPreference = 'Stop'
$forgeOrigin = [uri]$ForgeApiUrl
if (-not $forgeOrigin.IsAbsoluteUri -or $forgeOrigin.UserInfo -or $forgeOrigin.Query -or $forgeOrigin.Fragment -or
    $forgeOrigin.Scheme -notin @('http', 'https') -or ($forgeOrigin.Scheme -eq 'http' -and -not $forgeOrigin.IsLoopback)) {
    throw 'Use a loopback or HTTPS Forge API URL without embedded credentials.'
}
if ($SpawnId -and $ProductIds.Count -ne 1) { throw 'Row enablement requires exactly one ProductIds value.' }
if (-not $ConnectionId) { $ConnectionId = "telegram-$BotId" }
if (-not $WebSession -and -not $PairingCode) { throw 'Supply an existing paired WebSession or a one-use PairingCode from Forge. Settings > Integrations > Chat also provides this setup.' }
if (-not $WebSession) { $WebSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new() }
$forgeHeaders = @{}
function Invoke-ForgeChat {
    param([string]$Path, [string]$Method = 'GET', $Body, [switch]$AllowMissing)
    $request = @{ Uri = ($ForgeApiUrl.TrimEnd('/') + $Path); Method = $Method; WebSession = $WebSession; Headers = $forgeHeaders; MaximumRedirection = 0 }
    if ($null -ne $Body) { $request.Body = $Body | ConvertTo-Json -Depth 16 -Compress; $request.ContentType = 'application/json' }
    try { Invoke-RestMethod @request }
    catch {
        $status = [int]$_.Exception.Response.StatusCode
        if ($AllowMissing -and $status -eq 404) { return $null }
        throw "Forge chat API failed (HTTP $status). Check operator permissions, saved revisions, and connection health in Settings > Integrations > Chat."
    }
}
$csrf = Invoke-ForgeChat '/api/operator/csrf'
$forgeHeaders['X-Forge-CSRF'] = $csrf.token
if ($PairingCode) {
    $plainPairing = [System.Net.NetworkCredential]::new('', $PairingCode).Password
    try { $paired = Invoke-ForgeChat '/api/operator/pair' 'POST' @{ challenge = $plainPairing }; $forgeHeaders['X-Forge-CSRF'] = $paired.token }
    finally { $plainPairing = $null; $PairingCode = $null }
}
$management = Invoke-ForgeChat '/api/chat/management'
if (-not $management.canManageConnections) { throw 'A Forge host administrator must configure credentials and channels.' }
$path = '/api/chat/connections/' + [uri]::EscapeDataString($ConnectionId)
$existing = Invoke-ForgeChat $path -AllowMissing
$references = if ($existing) { $existing.secretReferences } else { @{} }
$grants = if ($ProductIds.Count) { $ProductIds } elseif ($existing) { $existing.productIds } else { @() }
$write = @{ expectedRevision = $(if ($existing) { $existing.revision } else { 0 }); providerId = 'telegram'; installationId = $BotId;
    displayName = $(if ($existing) { $existing.displayName } else { 'Telegram' }); enabled = $false;
    settingsVersion = 1; settings = @{}; secretReferences = $references; productIds = @($grants) }
$saved = Invoke-ForgeChat $path 'PUT' $write
if (-not $UseSavedCredential) {
    if (-not $BotToken) { $BotToken = Read-Host 'Telegram bot token (stored by the Forge host)' -AsSecureString }
    $plainToken = [System.Net.NetworkCredential]::new('', $BotToken).Password
    try { $null = Invoke-ForgeChat "$path/credentials/bot-token" 'PUT' @{ expectedRevision = $saved.revision; value = $plainToken } }
    finally { $plainToken = $null; $BotToken = $null }
}
$details = Invoke-ForgeChat $path
$write.expectedRevision = $details.revision; $write.secretReferences = $details.secretReferences; $write.enabled = $true
$connection = Invoke-ForgeChat $path 'PUT' $write
$destinations = @(Invoke-ForgeChat "$path/destinations")
$destination = $destinations | Where-Object { $_.channelId -eq $ChannelId -and $_.externalTenantId -eq $BotId } | Select-Object -First 1
if (-not $destination) {
    $destination = Invoke-ForgeChat "$path/destinations/$([guid]::NewGuid().ToString('N'))" 'PUT' @{
        expectedRevision = 0; externalTenantId = $BotId; channelId = $ChannelId; displayName = $ChannelName; kind = 'forum'
    }
}
$destinationPath = "$path/destinations/$([uri]::EscapeDataString($destination.id))"
$validation = Invoke-ForgeChat "$destinationPath/validate" 'POST'
if (-not $validation.succeeded) { throw 'Channel access validation failed. Check bot membership and Manage Topics permission, then use Repair in Chat settings.' }
if ($SpawnId) {
    $product = [uri]::EscapeDataString($ProductIds[0]); $spawn = [uri]::EscapeDataString($SpawnId)
    $setupPath = "/api/products/$product/story-spawns/$spawn/plugins/chat/setup"
    $setup = Invoke-ForgeChat $setupPath
    $routes = @($setup.plugin.subscriptions | Where-Object { $_.destinationId -ne $destination.id } | ForEach-Object {
        @{ destinationId = $_.destinationId; conversationPolicy = $_.conversationPolicy; interactive = $_.interactive; contentMode = $_.contentMode; eventKinds = @($_.eventKindsJson | ConvertFrom-Json); paused = $_.paused }
    })
    $routes += @{ destinationId = $destination.id; conversationPolicy = 'topic'; interactive = (-not $NotificationOnly); contentMode = 'metadata' }
    $null = Invoke-ForgeChat $setupPath 'PUT' @{ expectedRevision = $setup.plugin.revision; enabled = $true; destinations = $routes;
        activateNewRun = ($setup.plugin.reason -eq 'new-run-needs-activation') }
    Write-Host 'Parent chat configuration saved. All current and future children inherit its destinations.'
}
if ($SendTest) {
    $check = Invoke-ForgeChat "$destinationPath/checks" 'POST' @{ requestId = [guid]::NewGuid().ToString(); connectionRevision = $connection.revision; destinationRevision = $destination.revision }
    Write-Host "Test state: $($check.state). Reply check to that message to verify inbound delivery in Chat settings."
}
Write-Host 'Connection and channel saved. Use Settings > Integrations > Chat to verify delivery, link your identity, and review permissions.'
if ($management.hostReason) { Write-Host 'The host chat engine is unavailable. Configuration is saved; a host administrator must select the plugin engine and arrange a host refresh.' }
