# scrub-prod-test-artifacts.ps1 — direct-SQL equivalent of
# ForgeSeed.ScrubTestArtifactsAsync. Removes test-fixture rows that leaked
# into the prod DB at E:\forge\forge.db. Mirrors the canonical set:
#   crews = 8 ids hard-coded in EnsureSeedCrewsAsync
#   agents = every id in agents/allocation.json + A1/A2/E4/E7/E9/R3/R5/Q2/XD
# Order is dependency-aware: child rows before parent rows so no soft-FK
# orphans are left behind. Runs in a single transaction with COMMIT at the
# end (or ROLLBACK if any step throws).
#
# Run:
#   pwsh -NoProfile -File scripts\scrub-prod-test-artifacts.ps1            # dry-run
#   pwsh -NoProfile -File scripts\scrub-prod-test-artifacts.ps1 -Apply     # commit

[CmdletBinding()]
param(
    [string]$DbPath  = 'E:\forge\forge.db',
    [string]$AllocJson = 'E:\work\github\crp\workflow\agents\allocation.json',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

$bin = 'E:\work\github\crp\workflow\src\forge\Forge.Api\bin\Debug\net8.0\win-x64'
Add-Type -Path "$bin\SQLitePCLRaw.core.dll"
Add-Type -Path "$bin\SQLitePCLRaw.provider.e_sqlite3.dll"
Add-Type -Path "$bin\SQLitePCLRaw.batteries_v2.dll"
Add-Type -Path "$bin\Microsoft.Data.Sqlite.dll"
[SQLitePCL.Batteries_V2]::Init()

$canonicalCrews = @(
    'crew-eng', 'crew-rev', 'crew-arch', 'crew-qa',
    'crew-eng-night', 'crew-rev-night', 'crew-arch-night', 'crew-qa-night'
)
$canonicalAgents = New-Object 'System.Collections.Generic.HashSet[string]' (
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($id in @('A1','A2','E4','E7','E9','R3','R5','Q2','XD')) {
    [void]$canonicalAgents.Add($id)
}
$alloc = Get-Content $AllocJson -Raw | ConvertFrom-Json
foreach ($entry in $alloc) {
    if ($entry.id) { [void]$canonicalAgents.Add($entry.id) }
}
Write-Host "[scrub] canonical crews=$($canonicalCrews.Count) agents=$($canonicalAgents.Count)"

$cn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$DbPath")
$cn.Open()
# Wait for any concurrent writer (Forge.Api boot, sync jobs).
$pragma = $cn.CreateCommand(); $pragma.CommandText = 'PRAGMA busy_timeout=15000'; [void]$pragma.ExecuteNonQuery()

$tx = $cn.BeginTransaction()
$mode = if ($Apply) { 'APPLY' } else { 'DRY-RUN' }

function Sql([string]$sql, [hashtable]$params = @{}) {
    $cmd = $cn.CreateCommand()
    $cmd.Transaction = $tx
    $cmd.CommandText = $sql
    foreach ($k in $params.Keys) {
        $p = $cmd.CreateParameter()
        $p.ParameterName = $k
        $p.Value = $params[$k]
        [void]$cmd.Parameters.Add($p)
    }
    return $cmd
}

# Build a temp table for canonical crews + agents so the IN-clause stays small
# even as the canonical set grows. (Inline strings would also work — temp
# tables are cleaner, especially with 99+ agents.)
[void](Sql "CREATE TEMP TABLE canon_crews (id TEXT PRIMARY KEY)").ExecuteNonQuery()
[void](Sql "CREATE TEMP TABLE canon_agents (id TEXT PRIMARY KEY)").ExecuteNonQuery()
foreach ($c in $canonicalCrews) {
    [void](Sql 'INSERT INTO canon_crews(id) VALUES(@v)' @{'@v' = $c}).ExecuteNonQuery()
}
foreach ($a in $canonicalAgents) {
    [void](Sql 'INSERT INTO canon_agents(id) VALUES(@v)' @{'@v' = $a}).ExecuteNonQuery()
}

function Count([string]$sql) {
    $c = Sql $sql
    return [int]$c.ExecuteScalar()
}

$report = [ordered]@{}

$report['crew_members.deleted']           = Count 'SELECT COUNT(*) FROM crew_members WHERE CrewId NOT IN (SELECT id FROM canon_crews) OR AgentId NOT IN (SELECT id FROM canon_agents)'
$report['crew_history.deleted']           = Count 'SELECT COUNT(*) FROM crew_history WHERE CrewId NOT IN (SELECT id FROM canon_crews)'
$report['agent_history.deleted']          = Count 'SELECT COUNT(*) FROM agent_history WHERE AgentId NOT IN (SELECT id FROM canon_agents)'
$report['agent_usage.deleted']            = Count 'SELECT COUNT(*) FROM agent_usage WHERE CrewId NOT IN (SELECT id FROM canon_crews) OR AgentId NOT IN (SELECT id FROM canon_agents)'
$report['agent_level_history.deleted']    = Count 'SELECT COUNT(*) FROM agent_level_history WHERE AgentId NOT IN (SELECT id FROM canon_agents)'
$report['deliverables.deleted']           = Count @'
SELECT COUNT(*) FROM deliverables
WHERE CrewId NOT IN (SELECT id FROM canon_crews)
   OR GeneratorAgentId NOT IN (SELECT id FROM canon_agents)
   OR EvaluatorAgentId NOT IN (SELECT id FROM canon_agents)
   OR (JudgeAgentId IS NOT NULL AND JudgeAgentId NOT IN (SELECT id FROM canon_agents))
'@
$report['task_queue.routed_nulled']       = Count 'SELECT COUNT(*) FROM task_queue WHERE RoutedToCrewId IS NOT NULL AND RoutedToCrewId NOT IN (SELECT id FROM canon_crews)'
$report['task_queue.preferred_nulled']    = Count 'SELECT COUNT(*) FROM task_queue WHERE PreferredAgentId IS NOT NULL AND PreferredAgentId NOT IN (SELECT id FROM canon_agents)'
$report['agent_assignments.crew_nulled']  = Count 'SELECT COUNT(*) FROM agent_assignments WHERE CrewId IS NOT NULL AND CrewId NOT IN (SELECT id FROM canon_crews)'
$report['agents.deleted']                 = Count 'SELECT COUNT(*) FROM agents WHERE Id NOT IN (SELECT id FROM canon_agents)'
$report['crews.deleted']                  = Count 'SELECT COUNT(*) FROM crews WHERE Id NOT IN (SELECT id FROM canon_crews)'

Write-Host ""
Write-Host "[scrub] mode=$mode"
$report.GetEnumerator() | ForEach-Object { '{0,-38} {1}' -f $_.Key, $_.Value }

if ($Apply) {
    Write-Host ""
    Write-Host "[scrub] applying ..."

    [void](Sql 'DELETE FROM crew_members WHERE CrewId NOT IN (SELECT id FROM canon_crews) OR AgentId NOT IN (SELECT id FROM canon_agents)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM crew_history WHERE CrewId NOT IN (SELECT id FROM canon_crews)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM agent_history WHERE AgentId NOT IN (SELECT id FROM canon_agents)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM agent_usage WHERE CrewId NOT IN (SELECT id FROM canon_crews) OR AgentId NOT IN (SELECT id FROM canon_agents)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM agent_level_history WHERE AgentId NOT IN (SELECT id FROM canon_agents)').ExecuteNonQuery()
    [void](Sql @'
DELETE FROM deliverables
WHERE CrewId NOT IN (SELECT id FROM canon_crews)
   OR GeneratorAgentId NOT IN (SELECT id FROM canon_agents)
   OR EvaluatorAgentId NOT IN (SELECT id FROM canon_agents)
   OR (JudgeAgentId IS NOT NULL AND JudgeAgentId NOT IN (SELECT id FROM canon_agents))
'@).ExecuteNonQuery()
    [void](Sql 'UPDATE task_queue SET RoutedToCrewId = NULL WHERE RoutedToCrewId IS NOT NULL AND RoutedToCrewId NOT IN (SELECT id FROM canon_crews)').ExecuteNonQuery()
    [void](Sql 'UPDATE task_queue SET PreferredAgentId = NULL WHERE PreferredAgentId IS NOT NULL AND PreferredAgentId NOT IN (SELECT id FROM canon_agents)').ExecuteNonQuery()
    [void](Sql 'UPDATE agent_assignments SET CrewId = NULL WHERE CrewId IS NOT NULL AND CrewId NOT IN (SELECT id FROM canon_crews)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM agents WHERE Id NOT IN (SELECT id FROM canon_agents)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM crews WHERE Id NOT IN (SELECT id FROM canon_crews)').ExecuteNonQuery()

    $tx.Commit()
    Write-Host "[scrub] APPLIED."
} else {
    $tx.Rollback()
    Write-Host ""
    Write-Host "[scrub] DRY-RUN — no changes written. Re-run with -Apply to commit."
}
$cn.Close()
