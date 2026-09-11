#Requires -Version 7.0
<#
.SYNOPSIS
    Preview, import, transfer or roll back the legacy Telegram connection.
.DESCRIPTION
    Uses paired Forge APIs. Preview is the default and performs no writes or provider I/O.
    Import creates a disabled draft. Cutover and Rollback drain the current engine and
    persist exclusive ownership without restarting Forge. No credential is exported.
.PARAMETER Activate
    Explicit parent choices for Cutover: @{ productId='p'; spawnId='id'; contentMode='metadata' }.
    An empty array leaves all parents off and activates only the permanent host alerts.
.EXAMPLE
    ./scripts/migrate-chat-plugin.ps1 -WebSession $session
.EXAMPLE
    ./scripts/migrate-chat-plugin.ps1 -Action Import -ExpectedRevision 0 -SourceHash $preview.sourceHash -BotId 12345 -WebSession $session
.EXAMPLE
    ./scripts/migrate-chat-plugin.ps1 -Action Cutover -ExpectedRevision 1 -Activate @(@{productId='p';spawnId='parent'}) -WebSession $session
#>
[CmdletBinding()]
param(
    [ValidateSet('Preview', 'Import', 'Cutover', 'RollbackPreview', 'Rollback', 'RetireCompatibility')][string]$Action = 'Preview',
    [long]$ExpectedRevision = -1,
    [string]$SourceHash,
    [string]$BotId,
    [hashtable[]]$Activate = @(),
    [switch]$PauseUnsupported,
    [securestring]$PairingCode,
    [Microsoft.PowerShell.Commands.WebRequestSession]$WebSession,
    [string]$ForgeApiUrl = 'http://localhost:5180'
)
$ErrorActionPreference = 'Stop'
$migrationOrigin = [uri]$ForgeApiUrl
if (-not $migrationOrigin.IsAbsoluteUri -or $migrationOrigin.UserInfo -or $migrationOrigin.Query -or $migrationOrigin.Fragment -or
    $migrationOrigin.Scheme -notin @('http', 'https') -or ($migrationOrigin.Scheme -eq 'http' -and -not $migrationOrigin.IsLoopback)) {
    throw 'Use a loopback or HTTPS Forge API URL without embedded credentials.'
}
$isRead = $Action -in @('Preview', 'RollbackPreview')
if (-not $isRead -and $ExpectedRevision -lt 0) { throw 'Use the expected revision from a fresh migration preview.' }
if ($Action -eq 'Import' -and (-not $SourceHash -or $BotId -notmatch '^[1-9][0-9]*$')) { throw 'Import requires the reviewed SourceHash and numeric BotId.' }
if ($Action -ne 'Cutover' -and $Activate.Count) { throw 'Activate is only valid for Cutover.' }
if ($Action -ne 'Rollback' -and $PauseUnsupported) { throw 'PauseUnsupported is only valid for Rollback.' }
if (-not $WebSession -and -not $PairingCode) { throw 'Supply a paired WebSession or a one-use Forge browser PairingCode.' }
if (-not $WebSession) { $WebSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new() }
$migrationHeaders = @{}
function Invoke-ChatMigrationApi {
    param([string]$Path, [string]$Method = 'GET', $Body)
    $request = @{ Uri = ($ForgeApiUrl.TrimEnd('/') + $Path); Method = $Method; WebSession = $WebSession; Headers = $migrationHeaders; MaximumRedirection = 0 }
    if ($null -ne $Body) { $request.Body = $Body | ConvertTo-Json -Depth 12 -Compress; $request.ContentType = 'application/json' }
    try { Invoke-RestMethod @request }
    catch {
        $status = [int]$_.Exception.Response.StatusCode
        throw "Forge migration API failed (HTTP $status). Read a fresh preview and rollback preview; review permissions, revisions, and repair issues before retrying."
    }
}
$csrf = Invoke-ChatMigrationApi '/api/operator/csrf'
$migrationHeaders['X-Forge-CSRF'] = $csrf.token
if ($PairingCode) {
    $migrationPairing = [System.Net.NetworkCredential]::new('', $PairingCode).Password
    try {
        $paired = Invoke-ChatMigrationApi '/api/operator/pair' 'POST' @{ challenge = $migrationPairing }
        $migrationHeaders['X-Forge-CSRF'] = $paired.token
    } finally { $migrationPairing = $null; $PairingCode = $null }
}
$operation = switch ($Action) {
    'Preview' { 'preview' }
    'Import' { 'import' }
    'Cutover' { 'cutover' }
    'RollbackPreview' { 'rollback-preview' }
    'Rollback' { 'rollback' }
    'RetireCompatibility' { 'retire-compatibility' }
}
if ($isRead) { Invoke-ChatMigrationApi "/api/chat/migration/$operation"; return }
$body = @{ expectedRevision = $ExpectedRevision }
switch ($Action) {
    'Import' { $body.sourceHash = $SourceHash; $body.botId = $BotId }
    'Cutover' { $body.activate = @($Activate) }
    'Rollback' { $body.pauseUnsupported = [bool]$PauseUnsupported }
}
Invoke-ChatMigrationApi "/api/chat/migration/$operation" 'POST' $body