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

# ── Privilege model (2026-05-26) ─────────────────────────────────────────
# Forge.Api runs NON-elevated, so every git / agent (Copilot CLI/SDK) /
# normal-build child it spawns is non-elevated too — which avoids the whole
# class of elevation bugs (shared ~/.gitconfig access denials, MSBuildLocator
# env pollution, worktree pollution). The ONLY thing that needs Administrator
# is the build/test command of a repo flagged RunTestsElevated (its tests
# write HKLM / bind HttpListener URLs). That single command is marshalled to a
# small ELEVATED broker — a second `Forge.Api.exe --elevated-broker` this
# launcher starts via one UAC prompt (Start-ElevatedBroker below). The broker
# watches the web host's PID and self-exits when it stops, so a plain
# non-elevated -Stop tears down both. This launcher therefore NO LONGER
# self-elevates the whole script.

# ── Diagnostic transcript ───────────────────────────────────────────────
# Capture everything the launcher does so the elevated relaunch (which runs
# in a separate, silently-elevated console window that may close on error)
# leaves an audit trail. Tail $RepoRoot\forge-launcher-transcript.log if a
# restart appears to silently fail.
try {
    $transcriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'forge-launcher-transcript.log'
    Start-Transcript -Path $transcriptPath -Append -Force | Out-Null
} catch { Write-Host "  Could not start transcript: $_" -ForegroundColor Yellow }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$UIDir = Join-Path $RepoRoot 'src\forge\Forge.UI'
$ServerProj = Join-Path $RepoRoot 'src\forge\Forge.Api\Forge.Api.csproj'
$ServerBinDir = Join-Path $RepoRoot 'src\forge\Forge.Api\bin\Debug'
$ApiPort = 5180
$UiPort  = 5181

# ── Resolve the built Forge.Api apphost (single source of truth) ──────────
# Don't hard-code the TFM/RID subdir (e.g. net8.0\win-x64) — that's a csproj
# fact a framework/RID change would silently break. Glob under bin\Debug for
# the apphost, newest first. Returns $null when it hasn't been built yet.
function Get-ServerExe {
    Get-ChildItem -Path $ServerBinDir -Recurse -Filter 'Forge.Api.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

# ── Helper: find running processes ───────────────────────────────────────
function Get-ServerProcesses {
    $procs = @()
    Get-Process -Name "Forge.Api" -ErrorAction SilentlyContinue |
        ForEach-Object { $procs += $_ }
    # `dotnet run` fallback: match the Forge.Api host by command line. Use ONE
    # bulk CIM query + in-memory filter instead of touching $_.CommandLine on
    # every dotnet process — the latter triggers a per-process lookup and
    # crawls when hundreds of stray dotnet build-server nodes are alive
    # (observed 2026-05-21: ~700 leaked nodes made -Stop hang for minutes).
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*Forge.Api*" } |
        ForEach-Object {
            if ($procs.Id -notcontains $_.ProcessId) {
                $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
                if ($p) { $procs += $p }
            }
        }
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
        # Gracefully stop the detached ELEVATED broker FIRST. It runs as admin,
        # so a non-elevated Stop-Process can't kill it; instead we tell it to
        # exit over its named pipe (no UAC).
        #
        # 2026-05-28 — probe the pipe BEFORE launching the signaller. The pipe
        # name (forge.elevated.<SID>, see ElevatedCommandBroker.PipeName) only
        # exists while a broker is listening; Test-Path against \\.\pipe\<name>
        # is a sub-ms kernel lookup. When no broker is running we now skip the
        # apphost launch entirely instead of paying ~2-5s cold-start + a 2s
        # ConnectAsync timeout just to discover absence. We still wrap the
        # signaller with an outer WaitForExit timeout for the HUNG-broker case
        # (process alive, pipe listening, but stuck mid-frame — the in-process
        # 5s CTS depends on native pipe I/O honouring cancellation, which is
        # not guaranteed). If the signaller times out we kill it and continue;
        # the main server's hard Stop below tears the broker down anyway via
        # --watch-pid.
        $sid       = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
        $pipePath  = "\\.\pipe\forge.elevated.$sid"
        if (Test-Path $pipePath) {
            $brokerExe = Get-ServerExe
            if ($brokerExe) {
                Write-Host "  Signalling elevated broker to shut down..." -ForegroundColor Gray
                try {
                    $shutProc = Start-Process -FilePath $brokerExe `
                        -ArgumentList '--broker-shutdown' `
                        -WindowStyle Hidden `
                        -PassThru
                    if ($shutProc -and -not $shutProc.WaitForExit(10000)) {
                        Write-Host "  ⚠ Broker-shutdown signalling timed out after 10s (broker likely hung) — killing the signaller and continuing." -ForegroundColor Yellow
                        try { $shutProc.Kill() } catch { }
                    }
                } catch {
                    Write-Host "  ⚠ Could not launch broker-shutdown signaller: $_" -ForegroundColor Yellow
                }
            }
        }
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
    # Ensure the binary is up-to-date. -Rebuild already handled this above;
    # otherwise do an incremental compile (no-op when nothing changed).
    if (-not $Rebuild) {
        Write-Host "🔨 Ensuring Forge.Api is built..." -ForegroundColor Gray
        dotnet build $ServerProj -c Debug --nologo --verbosity quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Server build failed" }
    }
    # Resolve the apphost AFTER the build so a first-ever build is found too.
    $serverExe = Get-ServerExe
    if (-not $serverExe) {
        throw "Forge.Api.exe not found under $ServerBinDir — build output path may have changed."
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

    # 2026-05-21 — promote `Forge:Test:ExcludeFilter` from appsettings.json
    # to an env var for the same reason as Forge__Db__SplitEnabled above:
    # the content root is $RepoRoot (no appsettings.json there), so the key
    # defined only in src/forge/Forge.Api/appsettings.json never loads and
    # the story test-gate silently runs the whole suite (chronic flakes like
    # CloudVsrDiscovery then burn the wall-clock budget → 15-min timeout kill
    # on stage-4.2-migrate-cachecontrollerintegrationtests, 2026-05-21).
    # Reading the value here keeps appsettings.json the single source of
    # truth (no duplicated literal) while making it actually take effect.
    $appsettingsPath = Join-Path $RepoRoot 'src\forge\Forge.Api\appsettings.json'
    if (Test-Path $appsettingsPath) {
        try {
            $appsettings = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
            $excludeFilter = $appsettings.Forge.Test.ExcludeFilter
            if (-not [string]::IsNullOrWhiteSpace($excludeFilter)) {
                $env:Forge__Test__ExcludeFilter = $excludeFilter
                Write-Host "  Forge__Test__ExcludeFilter = $excludeFilter" -ForegroundColor Gray
            }
        } catch {
            Write-Host "  ⚠ Could not read Forge:Test:ExcludeFilter from appsettings.json: $_" -ForegroundColor Yellow
        }
    }

    $serverProc = Start-Process -FilePath $serverExe `
        -ArgumentList "--environment","Development" `
        -WorkingDirectory $RepoRoot `
        -RedirectStandardOutput $serverLog `
        -RedirectStandardError $serverErr `
        -WindowStyle Hidden `
        -PassThru
    Wait-ForPort -Port $ApiPort -Label "Forge.Api" -TimeoutSeconds 30 | Out-Null

    # ── Elevated test broker (one UAC prompt) ─────────────────────────────
    # Start a SECOND Forge.Api.exe in --elevated-broker mode as Administrator.
    # It serves ONLY the build/test command of RunTestsElevated repos over a
    # per-user named pipe; the main host above stays NON-elevated (so git /
    # agents / normal build run non-elevated). --watch-pid ties the broker's
    # lifetime to the web host, so a plain non-elevated -Stop / -Rebuild tears
    # down both. -Verb RunAs uses ShellExecute, which cannot redirect stdio,
    # so we don't pass -RedirectStandard*. If the operator declines the UAC
    # prompt the broker just isn't there: RunTestsElevated gates stall (safe)
    # and everything non-elevated is unaffected.
    if ($serverProc) {
        Write-Host "🔐 Starting elevated test broker (accept the UAC prompt; only RunTestsElevated repos use it)..." -ForegroundColor Yellow
        try {
            Start-Process -FilePath $serverExe `
                -ArgumentList "--elevated-broker","--watch-pid","$($serverProc.Id)" `
                -WorkingDirectory $RepoRoot `
                -Verb RunAs `
                -WindowStyle Hidden | Out-Null
            Write-Host "  ✅ Elevated test broker started (PID $($serverProc.Id) watched)." -ForegroundColor Gray
        } catch {
            Write-Host "  ⚠ Elevated broker not started ($_). RunTestsElevated repos will stall their test gate until it's running; everything else is unaffected." -ForegroundColor Yellow
        }
    }
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
