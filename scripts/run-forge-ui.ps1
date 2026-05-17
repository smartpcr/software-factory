<#
.SYNOPSIS
  Launches, stops, or checks the status of the Forge UI and Forge.Api server.

.PARAMETER Rebuild
  When set, runs a clean build of both the .NET server and the UI before starting.

.PARAMETER UIOnly
  When set, only targets the Vite UI dev server (no .NET backend).

.PARAMETER ServerOnly
  When set, only targets the .NET backend server (no UI).

.PARAMETER Stop
  Stop running Forge UI and/or server processes and exit.

.PARAMETER Status
  Show the current status of Forge UI and server processes and exit.

.EXAMPLE
  .\scripts\run-forge-ui.ps1                # start both
  .\scripts\run-forge-ui.ps1 -Rebuild       # rebuild + start both
  .\scripts\run-forge-ui.ps1 -UIOnly        # UI dev server only
  .\scripts\run-forge-ui.ps1 -ServerOnly    # .NET server only
  .\scripts\run-forge-ui.ps1 -Stop          # stop both
  .\scripts\run-forge-ui.ps1 -Stop -UIOnly  # stop UI only
  .\scripts\run-forge-ui.ps1 -Status        # check status of both
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
$UIDir = Join-Path $RepoRoot 'src\forge\Forge.UI'
$ServerProj = Join-Path $RepoRoot 'src\forge\Forge.Api\Forge.Api.csproj'
$ApiPort = 5180
$UiPort  = 5181

# ── Helper: find running processes ───────────────────────────────────────
function Get-ServerProcesses {
    $procs = @()
    Get-Process -Name "Forge.Api" -ErrorAction SilentlyContinue |
        ForEach-Object { $procs += $_ }
    Get-Process -Name "dotnet" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*Forge.Api*" } |
        ForEach-Object { if ($procs.Id -notcontains $_.Id) { $procs += $_ } }
    $portProcs = Get-NetTCPConnection -LocalPort $ApiPort -State Listen -ErrorAction SilentlyContinue
    foreach ($conn in $portProcs) {
        $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
        if ($proc -and ($procs.Id -notcontains $proc.Id)) { $procs += $proc }
    }
    return $procs
}

function Get-UIProcesses {
    $procs = @()
    Get-Process -Name "node" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*vite*" -and $_.CommandLine -like "*Forge.UI*" } |
        ForEach-Object { $procs += $_ }
    $portProcs = Get-NetTCPConnection -LocalPort $UiPort -State Listen -ErrorAction SilentlyContinue
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
    Get-Job | Where-Object { $_.Name -like "*forge*" -or $_.Command -like "*Forge.UI*" -or $_.Command -like "*Forge.Api*" } |
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
            Write-Host "  Forge.Api (port $ApiPort): ✅ Running" -ForegroundColor Green
            $serverProcs | ForEach-Object {
                Write-Host "    PID $($_.Id) — $($_.ProcessName)" -ForegroundColor Gray
            }
        } else {
            Write-Host "  Forge.Api (port $ApiPort): ⛔ Stopped" -ForegroundColor Red
        }
    }
    if ($targetUI) {
        $uiProcs = Get-UIProcesses
        if ($uiProcs.Count -gt 0) {
            Write-Host "  Forge UI  (port $UiPort): ✅ Running" -ForegroundColor Green
            $uiProcs | ForEach-Object {
                Write-Host "    PID $($_.Id) — $($_.ProcessName)" -ForegroundColor Gray
            }
        } else {
            Write-Host "  Forge UI  (port $UiPort): ⛔ Stopped" -ForegroundColor Red
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
        Write-Host "`n🔨 Building Forge.Api..." -ForegroundColor Cyan
        dotnet build $ServerProj -c Debug --nologo
        if ($LASTEXITCODE -ne 0) { throw "Server build failed" }
    }
    if ($targetUI) {
        Write-Host "`n🔨 Building Forge.UI..." -ForegroundColor Cyan
        Push-Location $UIDir
        try {
            npm ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
            npm run build
            if ($LASTEXITCODE -ne 0) { throw "UI build failed" }
        } finally { Pop-Location }
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
    # Launch Forge.Api.exe directly instead of `dotnet run`. The `dotnet run`
    # wrapper stays alive forwarding stdio between itself and the spawned exe,
    # which keeps inherited handles open and blocks any parent that waits on
    # the process tree (e.g. msys/git-bash, some CI runners — interactive
    # Windows Terminal allocates a fresh console so it doesn't hit this).
    # The leaf exe has no stdio-forwarding behaviour, so the launcher detaches
    # cleanly for every caller.
    $serverExe = Join-Path $RepoRoot 'src\forge\Forge.Api\bin\Debug\net8.0\win-x64\Forge.Api.exe'

    # Ensure the binary is up-to-date. -Rebuild already handled this above;
    # otherwise do an incremental compile (no-op when nothing changed).
    if (-not $Rebuild) {
        Write-Host "🔨 Ensuring Forge.Api is built..." -ForegroundColor Gray
        dotnet build $ServerProj -c Debug --nologo --verbosity quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Server build failed" }
    }
    if (-not (Test-Path $serverExe)) {
        throw "Forge.Api.exe not found at $serverExe — build output path may have changed."
    }

    Write-Host "🚀 Starting Forge.Api on http://localhost:$ApiPort ..." -ForegroundColor Yellow
    $serverLog = Join-Path $RepoRoot 'forge-server-stdout.log'
    $serverErr = Join-Path $RepoRoot 'forge-server-stderr.log'

    # Interactive desktop run — allow Git Credential Manager to pop its
    # browser / system credential dialog when a fetch/push hits a
    # private remote. Default policy (FailFast) is for unattended
    # service deployments and would error out with "Cannot prompt
    # because user interactivity has been disabled". The per-call
    # timeout in RealStoryGitOps.RunGitCaptureAsync (180s by default)
    # bounds the wait if the operator walks away from the prompt.
    $env:FORGE_GIT_PROMPT_POLICY = 'Interactive'

    # Phase 73 — config overrides via env var because the launcher uses
    # `-WorkingDirectory $RepoRoot` and ASP.NET's default `appsettings.json`
    # lookup is relative to the content root (i.e. the working directory).
    # The repo root has no `appsettings.json`, so values defined only in
    # `src/forge/Forge.Api/appsettings.json` do NOT take effect.
    # Env vars use the `__` separator convention to map onto the
    # `Forge:Db:X` config keys.
    #   Forge:Db:SplitEnabled — gates /api/admin/db/split-* admin endpoints.
    #
    # NOT enabled here (deferred — see docs/forge/fix_concurrent_dispatch.md):
    #   Forge:Db:FailLoudOnUnscoped — Phase 73 Stage 2D guard. Enabling it
    #     today crashes Forge.Api at boot because Program.cs:485-710 has
    #     ~10 startup operations (BuildValidationRuns repair, Pr sweep,
    #     ForgeSeed.EnsureCrewCatalogAsync, WorkstreamMergeAdvancer.BackfillAsync,
    #     SpawnAutoMergeResolver.BackfillPrsAsync, etc.) that resolve an
    #     unscoped ProductDbContext. Each needs per-product fanout via
    #     ProductFanout.ForEachProductAsync before the guard can safely
    #     trip on real runtime call sites. Tracked as a follow-up.
    $env:Forge__Db__SplitEnabled = 'true'

    Start-Process -FilePath $serverExe `
        -ArgumentList "--environment","Development" `
        -WorkingDirectory $RepoRoot `
        -RedirectStandardOutput $serverLog `
        -RedirectStandardError $serverErr `
        -WindowStyle Hidden
    Wait-ForPort -Port $ApiPort -Label "Forge.Api" -TimeoutSeconds 30 | Out-Null
}

# ── Start UI (detached) ─────────────────────────────────────────────────
if ($targetUI) {
    Write-Host "🚀 Starting Forge UI on http://localhost:$UiPort ..." -ForegroundColor Yellow

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
    Wait-ForPort -Port $UiPort -Label "Forge UI" -TimeoutSeconds 30 | Out-Null
}

# ── Summary ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════" -ForegroundColor DarkGray
if ($targetServer) {
    Write-Host "  Forge.Api:   http://localhost:$ApiPort"        -ForegroundColor White
    Write-Host "  OpenAPI:     http://localhost:$ApiPort/v1/openapi.json" -ForegroundColor White
}
if ($targetUI) {
    Write-Host "  Forge UI:    http://localhost:$UiPort"         -ForegroundColor White
}
Write-Host "════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Services are running in the background." -ForegroundColor Gray
Write-Host "Use -Status to check, -Stop to shut down." -ForegroundColor Gray
Write-Host ""
