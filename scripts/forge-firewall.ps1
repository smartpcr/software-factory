# Shared firewall setup for run-forge-ui.ps1. Dot-sourcing has no side effects.
# Only these named Forge rules are managed; other firewall policy is untouched.

function Test-ForgeFirewallRule {
    param([Parameter(Mandatory)][ValidateSet(5180, 5181)][int]$Port)

    $rules = @(Get-NetFirewallRule -Name "Forge-TCP-$Port" -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
    if ($rules.Count -ne 1) { return $false }
    $rule = $rules[0]
    $portFilter = $rule | Get-NetFirewallPortFilter -ErrorAction Stop
    $addressFilter = $rule | Get-NetFirewallAddressFilter -ErrorAction Stop
    $applicationFilter = $rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
    $profiles = @(([string]$rule.Profile -split ',') | ForEach-Object { $_.Trim() } | Sort-Object)
    return ([string]$rule.Enabled -eq 'True' -and [string]$rule.Direction -eq 'Inbound' -and
        [string]$rule.Action -eq 'Allow' -and ($profiles -join ',') -eq 'Domain,Private' -and
        [string]$portFilter.Protocol -in @('TCP', '6') -and
        (@($portFilter.LocalPort) -join ',') -eq [string]$Port -and
        (@($portFilter.RemotePort) -join ',') -eq 'Any' -and
        (@($addressFilter.LocalAddress) -join ',') -eq 'Any' -and
        (@($addressFilter.RemoteAddress) -join ',') -eq 'Any' -and
        [string]$applicationFilter.Program -eq 'Any')
}

function Set-ForgeFirewallRules {
    param([Parameter(Mandatory)][ValidateSet(5180, 5181)][int[]]$Ports)

    foreach ($port in ($Ports | Sort-Object -Unique)) {
        if (Test-ForgeFirewallRule -Port $port) { continue }
        $parameters = @{
            Name = "Forge-TCP-$port"
            PolicyStore = 'PersistentStore'
            Enabled = 'True'
            Direction = 'Inbound'
            Action = 'Allow'
            Profile = @('Domain', 'Private')
            Protocol = 'TCP'
            LocalPort = [string]$port
            RemotePort = 'Any'
            LocalAddress = 'Any'
            RemoteAddress = 'Any'
            Program = 'Any'
            Service = 'Any'
            InterfaceType = 'Any'
            InterfaceAlias = 'Any'
            EdgeTraversalPolicy = 'Block'
            ErrorAction = 'Stop'
        }
        if (Get-NetFirewallRule -Name $parameters.Name -PolicyStore PersistentStore -ErrorAction SilentlyContinue) {
            Set-NetFirewallRule @parameters | Out-Null
        } else {
            New-NetFirewallRule @parameters -DisplayName "Forge TCP $port (local network)" -Group 'Forge' | Out-Null
        }
        if (-not (Test-ForgeFirewallRule -Port $port)) {
            throw "Forge TCP $port firewall rule did not appear correctly in the active policy. Check administrator or Group Policy restrictions."
        }
    }
}

function Test-ForgeFirewallAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } finally { $identity.Dispose() }
}

function Ensure-ForgeFirewallRules {
    param([Parameter(Mandatory)][ValidateSet(5180, 5181)][int[]]$Ports)

    $needed = @($Ports | Sort-Object -Unique | Where-Object { -not (Test-ForgeFirewallRule -Port $_) })
    if ($needed.Count -gt 0) {
        if (Test-ForgeFirewallAdministrator) {
            Set-ForgeFirewallRules -Ports $needed
        } else {
            Write-Host 'Configuring Forge firewall rules (Windows administrator approval required)...' -ForegroundColor Yellow
            # Quote the script path as a PowerShell literal. The port list is validated above.
            # Only this short helper elevates; Forge and its normal workers stay non-elevated.
            $helperPath = (Join-Path $PSScriptRoot 'forge-firewall.ps1').Replace("'", "''")
            $command = "`$ErrorActionPreference = 'Stop'; try { . '$helperPath'; Set-ForgeFirewallRules -Ports $($needed -join ','); exit 0 } catch { exit 1 }"
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
            $shellPath = Join-Path $PSHOME 'pwsh.exe'
            if (-not (Test-Path -LiteralPath $shellPath)) { $shellPath = Join-Path $PSHOME 'powershell.exe' }
            try {
                $elevated = Start-Process -FilePath $shellPath -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded `
                    -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop
            } catch {
                throw "Firewall setup was not approved or could not start. Existing Forge services have not been stopped. $($_.Exception.Message)"
            }
            if ($elevated.ExitCode -ne 0) {
                throw 'Firewall setup failed. Check administrator or Group Policy restrictions. Existing Forge services have not been stopped.'
            }
        }
    }
    foreach ($port in $Ports) {
        if (-not (Test-ForgeFirewallRule -Port $port)) { throw "Forge TCP $port firewall rule verification failed." }
    }
    Write-Host "Firewall rules verified: TCP $($Ports -join ', '), Domain/Private profiles, including routed remote clients." -ForegroundColor Green
}
