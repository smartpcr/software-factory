<#
.SYNOPSIS
    Starts the Workflow UI development environment (backend + frontend).
.DESCRIPTION
    Launches the .NET backend server and the Vite UI dev server in parallel.
    API calls from the UI are proxied to the backend automatically.
    Press Ctrl+C to stop both servers.
.PARAMETER Rebuild
    When set, runs dotnet build and npm ci before starting the servers.
.PARAMETER BackendPort
    Port for the .NET backend. Default: 5226.
.PARAMETER UiPort
    Port for the Vite dev server. Default: 5555.
.EXAMPLE
    .\scripts\dev.ps1
.EXAMPLE
    .\scripts\dev.ps1 -Rebuild
#>
param(
    [switch]$Rebuild,
    [int]$BackendPort = 5226,
    [int]$UiPort = 5555
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverProj = Join-Path $repoRoot "src\WorkflowUi.Server\WorkflowUi.Server.csproj"
$uiDir = Join-Path $repoRoot "src\WorkflowUI"

# --- Kill stale processes on our ports ---
function Stop-PortUser([int]$port) {
    $conn = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($conn) {
        Write-Host "  Killing PID $($conn.OwningProcess) on port $port" -ForegroundColor Yellow
        Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

Write-Host "=== Workflow UI Dev Environment ===" -ForegroundColor Cyan
Stop-PortUser $BackendPort
Stop-PortUser $UiPort

# --- Optional rebuild ---
if ($Rebuild) {
    Write-Host "`n--- Rebuilding backend ---" -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot "Workflow.sln") --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Write-Error "Backend build failed"; exit 1 }

    Write-Host "`n--- Reinstalling UI dependencies ---" -ForegroundColor Cyan
    Push-Location $uiDir
    npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { Pop-Location; Write-Error "npm ci failed"; exit 1 }
    Pop-Location
}

# --- Start backend ---
Write-Host "`n--- Starting backend on http://localhost:$BackendPort ---" -ForegroundColor Green
$env:ASPNETCORE_ENVIRONMENT = "Development"
$backend = Start-Process -FilePath "dotnet" `
    -ArgumentList "run","--project",$serverProj,"--no-launch-profile" `
    -PassThru -NoNewWindow

# --- Start UI dev server ---
Write-Host "--- Starting UI on http://localhost:$UiPort ---`n" -ForegroundColor Green
$ui = Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/c","npm","run","dev" `
    -WorkingDirectory $uiDir `
    -PassThru -NoNewWindow

Write-Host "Both servers starting. Open http://localhost:$UiPort/ in your browser." -ForegroundColor Cyan
Write-Host "Press Ctrl+C to stop both servers.`n" -ForegroundColor DarkGray

# --- Wait and cleanup ---
try {
    # Wait for either process to exit
    while (!$backend.HasExited -and !$ui.HasExited) {
        Start-Sleep -Milliseconds 500
    }
} finally {
    Write-Host "`nShutting down..." -ForegroundColor Yellow
    if (!$backend.HasExited) { Stop-Process -Id $backend.Id -Force -ErrorAction SilentlyContinue }
    if (!$ui.HasExited) { Stop-Process -Id $ui.Id -Force -ErrorAction SilentlyContinue }
    Write-Host "Done." -ForegroundColor Green
}
