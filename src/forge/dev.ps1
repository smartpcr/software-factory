# -----------------------------------------------------------------------
# <copyright file="dev.ps1" company="Microsoft Corp.">
#     Copyright (c) Microsoft Corp. All rights reserved.
# </copyright>
# -----------------------------------------------------------------------
#
# Boots Forge.Api on :5180 and Forge.UI Vite dev server on :5181 together.
# Usage (from repo root or anywhere):  ./src/forge/dev.ps1
# Stop with Ctrl-C; both child processes terminate via the Job cleanup.

$ErrorActionPreference = 'Stop'
$ForgeRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ApiProj = Join-Path $ForgeRoot 'Forge.Api/Forge.Api.csproj'
$UiDir = Join-Path $ForgeRoot 'Forge.UI'

if (-not (Test-Path $ApiProj)) { throw "Forge.Api project not found at $ApiProj" }
if (-not (Test-Path $UiDir))   { throw "Forge.UI not found at $UiDir" }

if (-not (Test-Path (Join-Path $UiDir 'node_modules'))) {
    Write-Host '[forge] installing UI dependencies...' -ForegroundColor Yellow
    Push-Location $UiDir
    try { npm install --no-audit --no-fund } finally { Pop-Location }
}

Write-Host '[forge] starting Forge.Api on http://localhost:5180' -ForegroundColor Cyan
$apiJob = Start-Job -Name 'forge.api' -ArgumentList $ApiProj -ScriptBlock {
    param($proj)
    dotnet run --project $proj
}

Write-Host '[forge] starting Forge.UI on http://localhost:5181' -ForegroundColor Cyan
$uiJob = Start-Job -Name 'forge.ui' -ArgumentList $UiDir -ScriptBlock {
    param($dir)
    Set-Location $dir
    npm run dev
}

try {
    Write-Host '[forge] both running. Ctrl-C to stop.' -ForegroundColor Green
    while ($true) {
        Receive-Job -Job $apiJob, $uiJob
        Start-Sleep -Seconds 1
        if ($apiJob.State -ne 'Running' -and $uiJob.State -ne 'Running') { break }
    }
} finally {
    Write-Host '[forge] stopping...' -ForegroundColor Yellow
    Stop-Job -Job $apiJob, $uiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $apiJob, $uiJob -Force -ErrorAction SilentlyContinue
}
