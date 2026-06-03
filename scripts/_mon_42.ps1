# Read-only follow of stage 4.2 self-heal: does the gen/eval pair fix the clusterer
# modularity_score field-name bug, or mask it via the baseline file?
$ErrorActionPreference = 'SilentlyContinue'
$h = @{ 'X-Forge-User' = 'forge-ui'; 'X-Forge-Product-Id' = 'code-intelligence' }
$wid = 'ws-code-intelligence-pr-ingester-phase-cluster-builder-and-concept-writer-stage-cluster-naming-and-description-synthesis'
$spawnUrl = 'http://localhost:5180/api/stories/code-intelligence:PR-INGESTER/spawn'
$trUrl = "http://localhost:5180/api/work-items/$wid/transcript?limit=2000"
$log = 'E:\work\github\crp\workflow\scripts\_mon_42.log'
"=== mon42 start $(Get-Date -Format 'HH:mm:ss') ===" | Out-File $log

$deadline = (Get-Date).AddMinutes(28)
$lastId = 0
$done = $false
while ((Get-Date) -lt $deadline -and -not $done) {
    $now = Get-Date -Format 'HH:mm:ss'
    # status line
    try {
        $d = Invoke-RestMethod -Uri $spawnUrl -Headers $h -TimeoutSec 20
        $w = $d.workstreams | Where-Object { $_.id -eq $wid }
        "${now}  STATUS status=$($w.status) iter=$($w.iter) score=$($w.latestScore) verdict=$($w.latestVerdict) cost=$($w.costUsd) commits=$($w.commits)" | Add-Content $log
        if ($w.status -and $w.status -ne 'active') { "EXIT: status -> $($w.status)" | Add-Content $log; $done = $true }
    } catch { "${now}  spawn-ERR $($_.Exception.Message)" | Add-Content $log }

    try {
        $t = Invoke-RestMethod -Uri $trUrl -Headers $h -TimeoutSec 30
        $new = $t.events | Where-Object { [int]$_.id -gt $lastId }
        foreach ($e in $new) {
            $p = $e.payload
            $role = $p.role
            $ts = $e.at.ToString('HH:mm:ss')
            switch -regex ($e.kind) {
                'task\.(build|test)-(started|passed|failed)|test-gate' {
                    $ex = ''
                    foreach ($pp in $p.PSObject.Properties) { if ($pp.Name -match 'timedOut|exit|code|duration|elapsed|seconds') { $ex += " $($pp.Name)=$($pp.Value)" } }
                    "${ts}  GATE $($e.kind)$ex" | Add-Content $log
                    if ($e.kind -match 'test-passed') { "EXIT: test-gate passed" | Add-Content $log; $done = $true }
                }
                'task\.iteration\.scored' {
                    "${ts}  SCORED score=$($p.score) verdict=$($p.verdict)" | Add-Content $log
                }
                'task\.toolcall' {
                    if ($p.toolName -match 'edit|write|create|str_replace|apply_patch|insert') {
                        $path = ''
                        try { $path = ($p.toolPayload | ConvertFrom-Json).path } catch {}
                        $short = $path -replace '.*\\(internal|docs|services)\\', '$1\'
                        "${ts}  EDIT[$role] $($p.toolName) -> $short" | Add-Content $log
                    } elseif ($p.toolName -eq 'bash' -or $p.toolName -match 'shell|exec') {
                        $cmd = ''
                        try { $cmd = ($p.toolPayload | ConvertFrom-Json).command } catch {}
                        if ($cmd -match 'go test|modularity|baseline|sed|clusterer') { "${ts}  BASH[$role] $(($cmd -replace '\s+',' ').Substring(0,[Math]::Min(120,$cmd.Length)))" | Add-Content $log }
                    }
                }
                'task\.reasoning' {
                    if ($p.text -match 'modularity_score|modularity_contribution|baseline|cluster_dropped|expectedBaselineFailures|clusterer\.go') {
                        $txt = ($p.text -replace '\s+',' ')
                        "${ts}  THINK[$role] $($txt.Substring(0,[Math]::Min(180,$txt.Length)))" | Add-Content $log
                    }
                }
            }
            $lastId = [int]$e.id
        }
    } catch { "${now}  transcript-ERR $($_.Exception.Message)" | Add-Content $log }

    if (-not $done) { Start-Sleep -Seconds 75 }
}
"=== mon42 end $(Get-Date -Format 'HH:mm:ss') ===" | Add-Content $log
