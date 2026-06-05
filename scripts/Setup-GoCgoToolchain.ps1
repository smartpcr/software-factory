<#
.SYNOPSIS
    Sets up a GCC-compatible C toolchain on Windows for building Go with CGO_ENABLED=1.

.DESCRIPTION
    Installs either:
      - MSYS2 + the UCRT64 GCC toolchain (default, recommended for ongoing C-dep work), or
      - A standalone WinLibs UCRT GCC build (no package manager, zero ceremony).

    Then it puts the compiler's bin directory on PATH, sets CGO_ENABLED=1, and (unless
    -SkipVerify) compiles + runs a tiny cgo program to prove the toolchain works end to end.

    The script is idempotent: re-running it skips work that is already done.

.PARAMETER Method
    'msys2' (default) or 'winlibs'.

.PARAMETER Msys2Root
    Install root for MSYS2 (default C:\msys64). Only used with -Method msys2.

.PARAMETER WinLibsDir
    Target dir for the WinLibs toolchain (default C:\mingw64). Only used with -Method winlibs.

.PARAMETER WinLibsUrl
    Optional explicit .zip URL for a WinLibs build. If omitted, the latest UCRT x86_64
    posix/seh GCC-only release is resolved from GitHub.

.PARAMETER PathScope
    'User' (default, no admin needed) or 'Machine' (requires elevation).

.PARAMETER SkipVerify
    Skip the gcc/go checks and the cgo smoke build.

.EXAMPLE
    .\Setup-GoCgoToolchain.ps1
    Installs MSYS2/UCRT64, updates the user PATH, sets CGO_ENABLED=1, runs a cgo smoke test.

.EXAMPLE
    .\Setup-GoCgoToolchain.ps1 -Method winlibs -WinLibsDir C:\tools\mingw64

.NOTES
    Open a NEW terminal after running so PATH / CGO_ENABLED env changes are picked up.
#>
[CmdletBinding()]
param(
    [ValidateSet('msys2', 'winlibs')]
    [string]$Method = 'msys2',

    [string]$Msys2Root = 'C:\msys64',
    [string]$WinLibsDir = 'C:\mingw64',
    [string]$WinLibsUrl,

    [ValidateSet('User', 'Machine')]
    [string]$PathScope = 'User',

    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ---------- small output helpers ----------
function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Info($m) { Write-Host "    $m" -ForegroundColor Gray }
function Write-Ok($m) { Write-Host "[ ok ] $m" -ForegroundColor Green }
function Write-Warn2($m) { Write-Host "[warn] $m" -ForegroundColor Yellow }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Idempotently prepend a directory to PATH at the chosen scope + the current session.
function Add-ToPath {
    param([string]$Dir, [string]$Scope)

    $target = if ($Scope -eq 'Machine') { 'Machine' } else { 'User' }
    $current = [Environment]::GetEnvironmentVariable('Path', $target)
    $parts = @()
    if ($current) { $parts = $current -split ';' | Where-Object { $_ -ne '' } }

    if ($parts -notcontains $Dir) {
        $new = (@($Dir) + $parts) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $new, $target)
        Write-Ok "Added to $target PATH: $Dir"
    }
    else {
        Write-Info "Already on $target PATH: $Dir"
    }

    # Make it usable in this very session too.
    if (($env:Path -split ';') -notcontains $Dir) {
        $env:Path = "$Dir;$env:Path"
    }
}

function Set-CgoEnabled {
    param([string]$Scope)
    $target = if ($Scope -eq 'Machine') { 'Machine' } else { 'User' }
    [Environment]::SetEnvironmentVariable('CGO_ENABLED', '1', $target)
    $env:CGO_ENABLED = '1'
    Write-Ok "Set CGO_ENABLED=1 ($target)"
}

function Get-LatestGitHubAsset {
    param([string]$Repo, [scriptblock]$Filter)
    $api = "https://api.github.com/repos/$Repo/releases/latest"
    $headers = @{ 'User-Agent' = 'Setup-GoCgoToolchain' }
    $rel = Invoke-RestMethod -Uri $api -Headers $headers
    $asset = $rel.assets | Where-Object $Filter | Select-Object -First 1
    if (-not $asset) { throw "No matching asset found in $Repo latest release." }
    $asset
}

# ---------- toolchain installers ----------
function Install-Msys2 {
    $bash = Join-Path $Msys2Root 'usr\bin\bash.exe'

    if (Test-Path $bash) {
        Write-Ok "MSYS2 already present at $Msys2Root"
    }
    else {
        Write-Step "Downloading MSYS2 headless installer"
        $asset = Get-LatestGitHubAsset -Repo 'msys2/msys2-installer' -Filter {
            $_.name -match '^msys2-x86_64-(latest|\d+)\.exe$'
        }
        $installer = Join-Path $env:TEMP $asset.name
        Write-Info $asset.browser_download_url
        $pp = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installer
        }
        finally { $ProgressPreference = $pp }

        Write-Step "Installing MSYS2 to $Msys2Root (headless)"
        Start-Process -FilePath $installer -Wait -NoNewWindow -ArgumentList @(
            'in', '--confirm-command', '--accept-messages', '--root', $Msys2Root
        )
        if (-not (Test-Path $bash)) { throw "MSYS2 install did not produce $bash" }
        Write-Ok "MSYS2 installed"
    }

    Write-Step "Installing UCRT64 GCC toolchain via pacman"
    # Refresh package DB, then install gcc + pkgconf (cgo commonly needs pkg-config).
    & $bash -lc 'pacman -Sy --noconfirm' | Out-Null
    & $bash -lc 'pacman -S --needed --noconfirm mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-pkgconf'
    if ($LASTEXITCODE -ne 0) { throw "pacman failed (exit $LASTEXITCODE)" }
    Write-Ok "gcc + pkgconf installed"

    return (Join-Path $Msys2Root 'ucrt64\bin')
}

function Install-WinLibs {
    $bin = Join-Path $WinLibsDir 'bin'
    if (Test-Path (Join-Path $bin 'gcc.exe')) {
        Write-Ok "WinLibs already present at $WinLibsDir"
        return $bin
    }

    if (-not $WinLibsUrl) {
        Write-Step "Resolving latest WinLibs UCRT x86_64 GCC build"
        $asset = Get-LatestGitHubAsset -Repo 'brechtsanders/winlibs_mingw' -Filter {
            $_.name -match 'x86_64' -and $_.name -match 'posix' -and $_.name -match 'seh' `
                -and $_.name -match 'ucrt' -and $_.name -notmatch 'llvm' `
                -and $_.name -notmatch 'i686' -and $_.name.EndsWith('.zip')
        }
        $WinLibsUrl = $asset.browser_download_url
    }
    Write-Info $WinLibsUrl

    $zip = Join-Path $env:TEMP 'winlibs-ucrt.zip'
    $stage = Join-Path $env:TEMP ('winlibs-stage-' + [guid]::NewGuid().ToString('N'))
    $pp = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    try {
        Write-Step "Downloading WinLibs"
        Invoke-WebRequest -Uri $WinLibsUrl -OutFile $zip
    }
    finally { $ProgressPreference = $pp }

    Write-Step "Extracting to $WinLibsDir"
    Expand-Archive -Path $zip -DestinationPath $stage -Force
    # The archive contains a top-level 'mingw64' folder.
    $src = Join-Path $stage 'mingw64'
    if (-not (Test-Path $src)) { throw "Unexpected archive layout (no mingw64 folder)." }

    $parent = Split-Path $WinLibsDir -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    if (Test-Path $WinLibsDir) { Remove-Item $WinLibsDir -Recurse -Force }
    Move-Item -Path $src -Destination $WinLibsDir
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zip -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path (Join-Path $bin 'gcc.exe'))) { throw "gcc.exe not found after extract." }
    Write-Ok "WinLibs installed"
    return $bin
}

# ---------- verification ----------
function Invoke-Verify {
    param([string]$BinDir)

    Write-Step "Verifying toolchain"
    $gcc = Join-Path $BinDir 'gcc.exe'
    & $gcc --version | Select-Object -First 1 | ForEach-Object { Write-Info $_ }

    $go = Get-Command go -ErrorAction SilentlyContinue
    if (-not $go) {
        Write-Warn2 "Go was not found on PATH. Install it from https://go.dev/dl/ then re-run with -SkipVerify removed."
        return
    }
    Write-Info ((& go version) -join '')
    Write-Info ("go env CGO_ENABLED -> " + (& go env CGO_ENABLED))

    Write-Step "Running cgo smoke build"
    $work = Join-Path $env:TEMP ('cgo-smoke-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        @'
module cgosmoke

go 1.21
'@ | Set-Content -Path (Join-Path $work 'go.mod') -Encoding ascii

        # single-quoted here-string: nothing is interpolated
        @'
package main

/*
#include <stdio.h>
static void hello(void) { printf("cgo ok\n"); }
*/
import "C"

func main() { C.hello() }
'@ | Set-Content -Path (Join-Path $work 'main.go') -Encoding ascii

        Push-Location $work
        try {
            $env:CGO_ENABLED = '1'
            $out = & go run . 2>&1
            if ($LASTEXITCODE -ne 0) { throw "cgo build/run failed:`n$out" }
            if ($out -join '' -notmatch 'cgo ok') { throw "Unexpected output: $out" }
            Write-Ok "cgo build + run succeeded (output: '$($out -join '')')"
        }
        finally { Pop-Location }
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------- main ----------
try {
    if ($PathScope -eq 'Machine' -and -not (Test-Admin)) {
        throw "-PathScope Machine requires an elevated (Run as Administrator) PowerShell session."
    }

    Write-Step "Method: $Method | PATH scope: $PathScope"

    switch ($Method) {
        'msys2' { $binDir = Install-Msys2 }
        'winlibs' { $binDir = Install-WinLibs }
    }

    Add-ToPath -Dir $binDir -Scope $PathScope
    Set-CgoEnabled -Scope $PathScope

    if (-not $SkipVerify) { Invoke-Verify -BinDir $binDir }

    Write-Host ""
    Write-Ok "Done."
    Write-Host "Compiler bin : $binDir" -ForegroundColor White
    Write-Host "Open a NEW terminal so PATH and CGO_ENABLED are picked up, then: go build ./..." -ForegroundColor White
}
catch {
    Write-Host ""
    Write-Host "[FAILED] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}