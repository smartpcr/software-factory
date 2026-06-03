# After PR #395 merge + retry: confirm 5.1 and 5.2 gates go green and the
# evaluator credits correctness (>0) again — proving the repoindexer gate
# flakiness was the score-capping blocker.
$ErrorActionPreference = 'SilentlyContinue'
$h = @{ 'X-Forge-User' = 'forge-ui'; 'X-Forge-Product-Id' = 'code-intelligence' }
$base = 'http://localhost:5180/api/work-items'
$log = 'E:\work\github\crp\workflow\scripts\_mon_51_52.log'
$ws = @{
  '5.1' = 'ws-code-intelligence-pr-ingester-phase-supervisor-and-management-surface-stage-pr-ingester-supervisor-command'
  '5.2' = 'ws-code-intelligence-pr-ingester-phase-supervisor-and-management-surface-stage-mgmt-verb-ingest-pr-history'
}
"=== mon 5.1/5.2 start $(Get-Date -Format 'HH:mm:ss') ===" | Out-File $log
$deadline = (Get-Date).AddMinutes(45)
$green = @{ '5.1' = $false; '5.2' = $false }
$seenGateId = @{ '5.1' = 0; '5.2' = 0 }
while ((Get-Date) -lt $deadline) {
  $now = Get-Date -Format 'HH:mm:ss'
  foreach ($k in '5.1','5.2') {
    $id = $ws[$k]
    try {
      $t = Invoke-RestMethod -Uri "$base/$id/transcript?limit=2500" -Headers $h -TimeoutSec 40
      # latest scored
      $scored = $t.events | Where-Object { $_.kind -eq 'task.iteration.scored' } | Select-Object -Last 1
      $sc = if ($scored) { "iter=$($scored.payload.iter) score=$($scored.payload.score) correctness=$($scored.payload.dims.correctness) verdict=$($scored.payload.verdict)" } else { 'no-score-yet' }
      # gate events (newest)
      $gate = $t.events | Where-Object { $_.kind -match 'test-gate.passed|test-passed|test-failed|test-gate.resolved|build-gate.failed' } | Select-Object -Last 1
      $gtxt = if ($gate) { "$($gate.at.ToString('HH:mm:ss')) $($gate.kind)" } else { 'no-gate-yet' }
      "${now} [$k] $sc | gate: $gtxt" | Add-Content $log
      # green = a NEW test-gate.passed since start AND latest scored correctness>0
      if ($gate -and $gate.kind -match 'test-passed|test-gate.passed' -and [int]$gate.id -gt $seenGateId[$k]) { $seenGateId[$k] = [int]$gate.id }
      if ($scored -and [int]$scored.payload.dims.correctness -gt 0 -and $seenGateId[$k] -gt 0) { $green[$k] = $true }
    } catch { "${now} [$k] ERR $($_.Exception.Message)" | Add-Content $log }
  }
  if ($green['5.1'] -and $green['5.2']) { "EXIT: both gates green + correctness>0" | Add-Content $log; break }
  Start-Sleep -Seconds 90
}
"final: 5.1 green=$($green['5.1'])  5.2 green=$($green['5.2'])" | Add-Content $log
"=== mon end $(Get-Date -Format 'HH:mm:ss') ===" | Add-Content $log
