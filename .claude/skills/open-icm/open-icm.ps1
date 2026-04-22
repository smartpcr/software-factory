# -----------------------------------------------------------------------
# <copyright file="open-icm.ps1" company="Microsoft Corp.">
#     Copyright (c) Microsoft Corp. All rights reserved.
# </copyright>
# -----------------------------------------------------------------------
[CmdletBinding()]
param(
    [Alias('Id')]
    [string]$IncidentId,

    [Alias('Out')]
    [string]$OutputBase = 'E:\icms',

    [Alias('S')]
    [string]$Service,

    [Alias('Q')]
    [string]$Query,

    [Alias('L')]
    [int]$Limit = 0,

    [switch]$Headless
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir     = $PSScriptRoot
$extractScript = Join-Path $scriptDir 'extract-icm.js'

# --- Node.js check ---
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error 'Node.js 18+ is required. Install from https://nodejs.org/'
    exit 1
}

# --- Install npm dependencies on first run ---
$nodeModules = Join-Path $scriptDir 'node_modules'
if (-not (Test-Path (Join-Path $nodeModules 'playwright'))) {
    Write-Host 'Installing dependencies (first run, may take a minute)...' -ForegroundColor Cyan
    Push-Location $scriptDir
    try {
        npm install --no-fund --no-audit
    } finally {
        Pop-Location
    }

    # Ensure msedge browser binary is available
    Write-Host 'Installing Playwright msedge browser...' -ForegroundColor Cyan
    Push-Location $scriptDir
    try {
        node node_modules/playwright/cli.js install msedge 2>&1 | ForEach-Object { Write-Host "  $_" }
    } finally {
        Pop-Location
    }
}

# --- Build node argument list ---
$nodeArgs = [System.Collections.Generic.List[string]]::new()
$nodeArgs.Add($extractScript)
if ($IncidentId)  { $nodeArgs.Add('--incidentId'); $nodeArgs.Add($IncidentId) }
if ($OutputBase)  { $nodeArgs.Add('--outputBase'); $nodeArgs.Add($OutputBase) }
if ($Query)       { $nodeArgs.Add('--query');      $nodeArgs.Add($Query) }
if ($Limit -gt 0) { $nodeArgs.Add('--limit');      $nodeArgs.Add($Limit.ToString()) }
if ($Headless)    { $nodeArgs.Add('--headless');   $nodeArgs.Add('true') }

# --- Run ---
node @nodeArgs
exit $LASTEXITCODE
