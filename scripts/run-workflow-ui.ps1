<#
.SYNOPSIS
  Launches the Workflow UI (Vite dev server) and the backend API server.

.PARAMETER Rebuild
  When set, runs a clean build of both the .NET server and the UI before starting.

.PARAMETER UIOnly
  When set, only starts the Vite UI dev server (no .NET backend).

.PARAMETER ServerOnly
  When set, only starts the .NET backend server (no UI).

.EXAMPLE
  .\scripts\run-workflow-ui.ps1                # start both
  .\scripts\run-workflow-ui.ps1 -Rebuild       # rebuild + start both
  .\scripts\run-workflow-ui.ps1 -UIOnly        # UI dev server only
  .\scripts\run-workflow-ui.ps1 -ServerOnly    # .NET server only
#>
[CmdletBinding()]
param(
    [switch]$Rebuild,
    [switch]$UIOnly,
    [switch]$ServerOnly
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$UIDir = Join-Path $RepoRoot 'src\WorkflowUI'
$ServerProj = Join-Path $RepoRoot 'src\WorkflowUi.Server\WorkflowUi.Server.csproj'

# ── Rebuild ──────────────────────────────────────────────────────────────
if ($Rebuild) {
    if (-not $UIOnly) {
        Write-Host "`n🔨 Building .NET server..." -ForegroundColor Cyan
        dotnet build $ServerProj -c Debug
        if ($LASTEXITCODE -ne 0) { throw "Server build failed" }
    }
    if (-not $ServerOnly) {
        Write-Host "`n🔨 Building UI..." -ForegroundColor Cyan
        Push-Location $UIDir
        npm ci --no-audit --no-fund
        npm run build
        if ($LASTEXITCODE -ne 0) { Pop-Location; throw "UI build failed" }
        Pop-Location
    }
    Write-Host "`n✅ Build complete`n" -ForegroundColor Green
}

# ── Start Server ─────────────────────────────────────────────────────────
$serverJob = $null
if (-not $UIOnly) {
    Write-Host "🚀 Starting .NET server on http://localhost:5226 ..." -ForegroundColor Yellow
    $serverJob = Start-Job -ScriptBlock {
        param($proj)
        dotnet run --project $proj --no-launch-profile -- --environment Development
    } -ArgumentList $ServerProj
    Start-Sleep -Seconds 2
}

# ── Start UI ─────────────────────────────────────────────────────────────
$uiJob = $null
if (-not $ServerOnly) {
    Write-Host "🚀 Starting UI dev server on http://localhost:5555 ..." -ForegroundColor Yellow

    # Ensure node_modules exist
    if (-not (Test-Path (Join-Path $UIDir 'node_modules'))) {
        Write-Host "   Installing npm dependencies..." -ForegroundColor Gray
        Push-Location $UIDir
        npm ci --no-audit --no-fund
        Pop-Location
    }

    $uiJob = Start-Job -ScriptBlock {
        param($dir)
        Set-Location $dir
        npx vite --host
    } -ArgumentList $UIDir
    Start-Sleep -Seconds 2
}

# ── Wait ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════" -ForegroundColor DarkGray
if (-not $UIOnly) {
    Write-Host "  API Server:  http://localhost:5226"       -ForegroundColor White
    Write-Host "  Swagger:     http://localhost:5226/swagger" -ForegroundColor White
}
if (-not $ServerOnly) {
    Write-Host "  Workflow UI: http://localhost:5555"       -ForegroundColor White
}
Write-Host "════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Press Ctrl+C to stop all services." -ForegroundColor Gray
Write-Host ""

try {
    while ($true) {
        if ($serverJob -and $serverJob.State -eq 'Failed') {
            Write-Host "❌ Server crashed:" -ForegroundColor Red
            Receive-Job $serverJob
            break
        }
        if ($uiJob -and $uiJob.State -eq 'Failed') {
            Write-Host "❌ UI crashed:" -ForegroundColor Red
            Receive-Job $uiJob
            break
        }
        Start-Sleep -Seconds 1
    }
}
finally {
    Write-Host "`n🛑 Stopping services..." -ForegroundColor Yellow
    if ($serverJob) { Stop-Job $serverJob -ErrorAction SilentlyContinue; Remove-Job $serverJob -Force -ErrorAction SilentlyContinue }
    if ($uiJob) { Stop-Job $uiJob -ErrorAction SilentlyContinue; Remove-Job $uiJob -Force -ErrorAction SilentlyContinue }
    Write-Host "Done." -ForegroundColor Green
}
