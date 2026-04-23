<#
.SYNOPSIS
  Launches, stops, or checks the status of the Workflow UI and backend API server.

.PARAMETER Rebuild
  When set, runs a clean build of both the .NET server and the UI before starting.

.PARAMETER UIOnly
  When set, only targets the Vite UI dev server (no .NET backend).

.PARAMETER ServerOnly
  When set, only targets the .NET backend server (no UI).

.PARAMETER Stop
  Stop running Workflow UI and/or server processes and exit.

.PARAMETER Status
  Show the current status of Workflow UI and server processes and exit.

.EXAMPLE
  .\scripts\run-workflow-ui.ps1                # start both
  .\scripts\run-workflow-ui.ps1 -Rebuild       # rebuild + start both
  .\scripts\run-workflow-ui.ps1 -UIOnly        # UI dev server only
  .\scripts\run-workflow-ui.ps1 -ServerOnly    # .NET server only
  .\scripts\run-workflow-ui.ps1 -Stop          # stop both
  .\scripts\run-workflow-ui.ps1 -Stop -UIOnly  # stop UI only
  .\scripts\run-workflow-ui.ps1 -Status        # check status of both
#>
[CmdletBinding()]
param(
    [switch]$Rebuild,
    [switch]$UIOnly,
    [switch]$ServerOnly,
    [switch]$Stop,
    [switch]$Status
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$UIDir = Join-Path $RepoRoot 'src\WorkflowUI'
$ServerProj = Join-Path $RepoRoot 'src\WorkflowUi.Server\WorkflowUi.Server.csproj'

# ── Helper: find running processes ───────────────────────────────────────
function Get-ServerProcesses {
    $procs = @()
    Get-Process -Name "dotnet" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*WorkflowUi.Server*" } |
        ForEach-Object { $procs += $_ }
    $portProcs = Get-NetTCPConnection -LocalPort 5226 -State Listen -ErrorAction SilentlyContinue
    foreach ($conn in $portProcs) {
        $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
        if ($proc -and ($procs.Id -notcontains $proc.Id)) { $procs += $proc }
    }
    return $procs
}

function Get-UIProcesses {
    $procs = @()
    Get-Process -Name "node" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*vite*" -and $_.CommandLine -like "*WorkflowUI*" } |
        ForEach-Object { $procs += $_ }
    $portProcs = Get-NetTCPConnection -LocalPort 5555 -State Listen -ErrorAction SilentlyContinue
    foreach ($conn in $portProcs) {
        $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
        if ($proc -and ($procs.Id -notcontains $proc.Id)) { $procs += $proc }
    }
    return $procs
}

# ── Stop processes (filtered by -UIOnly / -ServerOnly) ───────────────────
function Stop-TargetProcesses {
    param([switch]$IncludeServer, [switch]$IncludeUI)

    if ($IncludeServer) {
        Get-ServerProcesses | ForEach-Object {
            Write-Host "  Stopping server (PID $($_.Id))..." -ForegroundColor Gray
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if ($IncludeUI) {
        Get-UIProcesses | ForEach-Object {
            Write-Host "  Stopping UI (PID $($_.Id))..." -ForegroundColor Gray
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
    # Clean up stale PowerShell jobs
    Get-Job | Where-Object { $_.Name -like "*workflow*" -or $_.Command -like "*WorkflowUI*" -or $_.Command -like "*WorkflowUi.Server*" } |
        ForEach-Object { Stop-Job $_ -ErrorAction SilentlyContinue; Remove-Job $_ -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}

$targetServer = -not $UIOnly
$targetUI     = -not $ServerOnly

# ── Status action ────────────────────────────────────────────────────────
if ($Status) {
    Write-Host ""
    Write-Host "════════════════════════════════════════════" -ForegroundColor DarkGray
    if ($targetServer) {
        $serverProcs = Get-ServerProcesses
        if ($serverProcs.Count -gt 0) {
            Write-Host "  API Server (port 5226):  ✅ Running" -ForegroundColor Green
            $serverProcs | ForEach-Object {
                Write-Host "    PID $($_.Id) — $($_.ProcessName)" -ForegroundColor Gray
            }
        } else {
            Write-Host "  API Server (port 5226):  ⛔ Stopped" -ForegroundColor Red
        }
    }
    if ($targetUI) {
        $uiProcs = Get-UIProcesses
        if ($uiProcs.Count -gt 0) {
            Write-Host "  Workflow UI (port 5555): ✅ Running" -ForegroundColor Green
            $uiProcs | ForEach-Object {
                Write-Host "    PID $($_.Id) — $($_.ProcessName)" -ForegroundColor Gray
            }
        } else {
            Write-Host "  Workflow UI (port 5555): ⛔ Stopped" -ForegroundColor Red
        }
    }
    Write-Host "════════════════════════════════════════════" -ForegroundColor DarkGray
    Write-Host ""
    return
}

# ── Stop action ──────────────────────────────────────────────────────────
if ($Stop) {
    Write-Host "🛑 Stopping services..." -ForegroundColor Yellow
    Stop-TargetProcesses -IncludeServer:$targetServer -IncludeUI:$targetUI
    Write-Host "✅ Done." -ForegroundColor Green
    return
}

# ── Start action (default) ───────────────────────────────────────────────
Write-Host "🧹 Stopping existing processes..." -ForegroundColor Yellow
Stop-TargetProcesses -IncludeServer:$targetServer -IncludeUI:$targetUI

# ── Rebuild ──────────────────────────────────────────────────────────────
if ($Rebuild) {
    if ($targetServer) {
        Write-Host "`n🔨 Building .NET server..." -ForegroundColor Cyan
        dotnet build $ServerProj -c Debug
        if ($LASTEXITCODE -ne 0) { throw "Server build failed" }
    }
    if ($targetUI) {
        Write-Host "`n🔨 Building UI..." -ForegroundColor Cyan
        Push-Location $UIDir
        npm ci --no-audit --no-fund
        npm run build
        if ($LASTEXITCODE -ne 0) { Pop-Location; throw "UI build failed" }
        Pop-Location
    }
    Write-Host "`n✅ Build complete`n" -ForegroundColor Green
}

# ── Helper: wait for port to become available ────────────────────────────
function Wait-ForPort {
    param([int]$Port, [string]$Label, [int]$TimeoutSeconds = 30)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        if ($conn) {
            $proc = Get-Process -Id $conn[0].OwningProcess -ErrorAction SilentlyContinue
            Write-Host "  ✅ $Label is listening on port $Port (PID $($proc.Id))" -ForegroundColor Green
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "  ⚠️  $Label did not start within $TimeoutSeconds seconds" -ForegroundColor Yellow
    return $false
}

# ── Start Server (detached) ──────────────────────────────────────────────
if ($targetServer) {
    Write-Host "🚀 Starting .NET server on http://localhost:5226 ..." -ForegroundColor Yellow
    $serverLog = Join-Path $RepoRoot 'server-stdout.log'
    $serverErr = Join-Path $RepoRoot 'server-stderr.log'
    Start-Process -FilePath "dotnet" `
        -ArgumentList "run","--project",$ServerProj,"--no-launch-profile","--","--environment","Development" `
        -WorkingDirectory $RepoRoot `
        -RedirectStandardOutput $serverLog `
        -RedirectStandardError $serverErr `
        -WindowStyle Hidden
    Wait-ForPort -Port 5226 -Label "API Server" -TimeoutSeconds 30 | Out-Null
}

# ── Start UI (detached) ─────────────────────────────────────────────────
if ($targetUI) {
    Write-Host "🚀 Starting UI dev server on http://localhost:5555 ..." -ForegroundColor Yellow

    # Ensure node_modules exist
    if (-not (Test-Path (Join-Path $UIDir 'node_modules'))) {
        Write-Host "   Installing npm dependencies..." -ForegroundColor Gray
        Push-Location $UIDir
        npm ci --no-audit --no-fund
        Pop-Location
    }

    Start-Process -FilePath "cmd.exe" `
        -ArgumentList "/c","npm","run","dev","--","--host" `
        -WorkingDirectory $UIDir `
        -WindowStyle Hidden
    Wait-ForPort -Port 5555 -Label "Workflow UI" -TimeoutSeconds 15 | Out-Null
}

# ── Summary ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════" -ForegroundColor DarkGray
if ($targetServer) {
    Write-Host "  API Server:  http://localhost:5226"       -ForegroundColor White
    Write-Host "  Swagger:     http://localhost:5226/swagger" -ForegroundColor White
}
if ($targetUI) {
    Write-Host "  Workflow UI: http://localhost:5555"       -ForegroundColor White
}
Write-Host "════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Services are running in the background." -ForegroundColor Gray
Write-Host "Use -Status to check, -Stop to shut down." -ForegroundColor Gray
Write-Host ""
