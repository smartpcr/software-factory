# Watch PR #395 (repoindexer git-hermetic fix) until it merges into
# feature/PR-Ingester, then exit so the main session can retry stage 5.2.
$ErrorActionPreference = 'SilentlyContinue'
$log = 'E:\work\github\crp\workflow\scripts\_watch_pr395.log'
"=== watch pr395 start $(Get-Date -Format 'HH:mm:ss') ===" | Out-File $log
$deadline = (Get-Date).AddHours(3)
while ((Get-Date) -lt $deadline) {
    $now = Get-Date -Format 'HH:mm:ss'
    $j = gh pr view 395 --repo smartpcr/code-intelligence --json state,mergedAt,mergeStateStatus 2>$null | ConvertFrom-Json
    if ($null -ne $j) {
        "${now}  state=$($j.state) merged=$($j.mergedAt) mergeable=$($j.mergeStateStatus)" | Add-Content $log
        if ($j.state -eq 'MERGED') { "EXIT: PR #395 MERGED at $($j.mergedAt)" | Add-Content $log; break }
        if ($j.state -eq 'CLOSED') { "EXIT: PR #395 CLOSED without merge" | Add-Content $log; break }
    } else {
        "${now}  gh-query failed" | Add-Content $log
    }
    Start-Sleep -Seconds 90
}
"=== watch pr395 end $(Get-Date -Format 'HH:mm:ss') ===" | Add-Content $log
