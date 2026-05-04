# scrub-prod-test-artifacts.ps1 — direct-SQL equivalent of
# ForgeSeed.ScrubTestArtifactsAsync. Removes test-fixture rows that leaked
# into the prod DB at E:\forge\forge.db.
#
# Three scopes, all gated on canonical keep-lists. Anything not in the
# keep-list is dropped (or its FK refs nulled where the column is nullable).
#
#   crews    keep = 8 ids hard-coded in EnsureSeedCrewsAsync
#   agents   keep = every id in agents/allocation.json + A1/A2/E4/E7/E9/R3/R5/Q2/XD
#   products keep = $KeepProducts (default: atlas, code-intelligence, local)
#   repos    keep = $KeepRepos    (default: atlas-platform, workflow, update)
#
# The atlas product's Slug + Name are also re-asserted to the canonical
# values from EnsureDefaultProductAndRepoAsync, since the
# `CreateProduct_Duplicate_Id_Returns_409` test mutated them despite the
# 409.
#
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
    [string[]]$KeepProducts = @('atlas', 'code-intelligence', 'local'),
    [string[]]$KeepRepos    = @('atlas-platform', 'workflow', 'update'),
    # work_items are kept by Provider (ado / github sync sources are real) plus
    # any explicit allow-list of local-Provider ids the operator pins. Default
    # pin: WI-1035 ("agent memory" — the user's real local item under workflow).
    [string[]]$KeepWorkItemProviders = @('ado', 'github'),
    [string[]]$KeepWorkItemIds       = @('WI-1035'),
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
Write-Host ("[scrub] canonical crews={0} agents={1} products={2} repos={3}" -f `
    $canonicalCrews.Count, $canonicalAgents.Count, $KeepProducts.Count, $KeepRepos.Count)
Write-Host ("[scrub]   keep products:           {0}" -f ($KeepProducts -join ', '))
Write-Host ("[scrub]   keep repos:              {0}" -f ($KeepRepos -join ', '))
Write-Host ("[scrub]   keep work-item providers:{0}" -f ($KeepWorkItemProviders -join ', '))
Write-Host ("[scrub]   keep work-item ids:      {0}" -f ($KeepWorkItemIds -join ', '))

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

# Build a temp table for canonical crews/agents/products/repos so the IN-clauses
# stay small. Temp tables auto-drop with the connection.
[void](Sql "CREATE TEMP TABLE canon_crews    (id TEXT PRIMARY KEY)").ExecuteNonQuery()
[void](Sql "CREATE TEMP TABLE canon_agents   (id TEXT PRIMARY KEY)").ExecuteNonQuery()
[void](Sql "CREATE TEMP TABLE canon_products (id TEXT PRIMARY KEY)").ExecuteNonQuery()
[void](Sql "CREATE TEMP TABLE canon_repos    (id TEXT PRIMARY KEY)").ExecuteNonQuery()
[void](Sql "CREATE TEMP TABLE canon_wi_providers (provider TEXT PRIMARY KEY)").ExecuteNonQuery()
[void](Sql "CREATE TEMP TABLE canon_wi_ids       (id TEXT PRIMARY KEY)").ExecuteNonQuery()
foreach ($v in $canonicalCrews)         { [void](Sql 'INSERT INTO canon_crews(id) VALUES(@v)'             @{'@v'=$v}).ExecuteNonQuery() }
foreach ($v in $canonicalAgents)        { [void](Sql 'INSERT INTO canon_agents(id) VALUES(@v)'            @{'@v'=$v}).ExecuteNonQuery() }
foreach ($v in $KeepProducts)           { [void](Sql 'INSERT INTO canon_products(id) VALUES(@v)'          @{'@v'=$v}).ExecuteNonQuery() }
foreach ($v in $KeepRepos)              { [void](Sql 'INSERT INTO canon_repos(id) VALUES(@v)'             @{'@v'=$v}).ExecuteNonQuery() }
foreach ($v in $KeepWorkItemProviders)  { [void](Sql 'INSERT INTO canon_wi_providers(provider) VALUES(@v)' @{'@v'=$v}).ExecuteNonQuery() }
foreach ($v in $KeepWorkItemIds)        { [void](Sql 'INSERT INTO canon_wi_ids(id) VALUES(@v)'             @{'@v'=$v}).ExecuteNonQuery() }

# Reusable predicate for "this work item is real (kept)".
$keptWiSql = 'SELECT Id FROM work_items WHERE Provider IN (SELECT provider FROM canon_wi_providers) OR Id IN (SELECT id FROM canon_wi_ids)'

function CountRows([string]$sql) {
    $c = Sql $sql
    return [int]$c.ExecuteScalar()
}

$report = [ordered]@{}

# ── Crews / agents (Phase 13) ──────────────────────────────────────────────
$report['crew_members.deleted']           = CountRows 'SELECT COUNT(*) FROM crew_members WHERE CrewId NOT IN (SELECT id FROM canon_crews) OR AgentId NOT IN (SELECT id FROM canon_agents)'
$report['crew_history.deleted']           = CountRows 'SELECT COUNT(*) FROM crew_history WHERE CrewId NOT IN (SELECT id FROM canon_crews)'
$report['agent_history.deleted']          = CountRows 'SELECT COUNT(*) FROM agent_history WHERE AgentId NOT IN (SELECT id FROM canon_agents)'
$report['agent_usage.deleted']            = CountRows 'SELECT COUNT(*) FROM agent_usage WHERE CrewId NOT IN (SELECT id FROM canon_crews) OR AgentId NOT IN (SELECT id FROM canon_agents)'
$report['agent_level_history.deleted']    = CountRows 'SELECT COUNT(*) FROM agent_level_history WHERE AgentId NOT IN (SELECT id FROM canon_agents)'
$report['deliverables.deleted']           = CountRows @'
SELECT COUNT(*) FROM deliverables
WHERE CrewId NOT IN (SELECT id FROM canon_crews)
   OR GeneratorAgentId NOT IN (SELECT id FROM canon_agents)
   OR EvaluatorAgentId NOT IN (SELECT id FROM canon_agents)
   OR (JudgeAgentId IS NOT NULL AND JudgeAgentId NOT IN (SELECT id FROM canon_agents))
'@
$report['task_queue.routed_nulled']       = CountRows 'SELECT COUNT(*) FROM task_queue WHERE RoutedToCrewId IS NOT NULL AND RoutedToCrewId NOT IN (SELECT id FROM canon_crews)'
$report['task_queue.preferred_nulled']    = CountRows 'SELECT COUNT(*) FROM task_queue WHERE PreferredAgentId IS NOT NULL AND PreferredAgentId NOT IN (SELECT id FROM canon_agents)'
$report['agent_assignments.crew_nulled']  = CountRows 'SELECT COUNT(*) FROM agent_assignments WHERE CrewId IS NOT NULL AND CrewId NOT IN (SELECT id FROM canon_crews)'
$report['agents.deleted']                 = CountRows 'SELECT COUNT(*) FROM agents WHERE Id NOT IN (SELECT id FROM canon_agents)'
$report['crews.deleted']                  = CountRows 'SELECT COUNT(*) FROM crews WHERE Id NOT IN (SELECT id FROM canon_crews)'

# ── Repos (RepoId references) ──────────────────────────────────────────────
# Nullable RepoId — null out (don't delete; the parent rows may still be valid):
$report['cards.repo_nulled']              = CountRows 'SELECT COUNT(*) FROM cards WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)'
$report['workstreams.repo_nulled']        = CountRows 'SELECT COUNT(*) FROM workstreams WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)'
$report['prs.repo_nulled']                = CountRows 'SELECT COUNT(*) FROM prs WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)'
$report['work_items.repo_nulled']         = CountRows 'SELECT COUNT(*) FROM work_items WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)'
$report['story_spawns.repo_nulled']       = CountRows 'SELECT COUNT(*) FROM story_spawns WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)'

# Non-nullable RepoId — delete the dependent row (build child tables first).
# builds  → build_stages  → build_stage_logs + build_failure_points
# builds  → build_artifacts → build_artifact_files
# builds  → build_annotations
# builds  → build_fix_pending
$badBuildsSql = 'SELECT Id FROM builds WHERE RepoId NOT IN (SELECT id FROM canon_repos)'
$badStagesSql = "SELECT Id FROM build_stages WHERE BuildId IN ($badBuildsSql)"
$badArtsSql   = "SELECT Id FROM build_artifacts WHERE BuildId IN ($badBuildsSql)"
$report['build_stage_logs.deleted']       = CountRows "SELECT COUNT(*) FROM build_stage_logs WHERE StageId IN ($badStagesSql)"
$report['build_failure_points.deleted']   = CountRows "SELECT COUNT(*) FROM build_failure_points WHERE StageId IN ($badStagesSql)"
$report['build_artifact_files.deleted']   = CountRows "SELECT COUNT(*) FROM build_artifact_files WHERE ArtifactId IN ($badArtsSql)"
$report['build_stages.deleted']           = CountRows "SELECT COUNT(*) FROM build_stages WHERE BuildId IN ($badBuildsSql)"
$report['build_artifacts.deleted']        = CountRows "SELECT COUNT(*) FROM build_artifacts WHERE BuildId IN ($badBuildsSql)"
$report['build_annotations.deleted']      = CountRows "SELECT COUNT(*) FROM build_annotations WHERE BuildId IN ($badBuildsSql)"
$report['build_fix_pending.deleted']      = CountRows "SELECT COUNT(*) FROM build_fix_pending WHERE BuildId IN ($badBuildsSql)"
$report['builds.deleted']                 = CountRows "SELECT COUNT(*) FROM builds WHERE RepoId NOT IN (SELECT id FROM canon_repos)"
$report['build_validation_runs.deleted']  = CountRows 'SELECT COUNT(*) FROM build_validation_runs WHERE RepoId NOT IN (SELECT id FROM canon_repos)'
$report['domain_experts.deleted']         = CountRows "SELECT COUNT(*) FROM domain_experts WHERE RepoId NOT IN (SELECT id FROM canon_repos) AND RepoId != '*'"
$report['repo_iteration_bindings.deleted']= CountRows 'SELECT COUNT(*) FROM repo_iteration_bindings WHERE RepoId NOT IN (SELECT id FROM canon_repos)'
$report['sync_run_items.deleted']         = CountRows 'SELECT COUNT(*) FROM sync_run_items WHERE RunId IN (SELECT Id FROM work_item_sync_runs WHERE RepoId NOT IN (SELECT id FROM canon_repos))'
$report['work_item_sync_runs.deleted']    = CountRows 'SELECT COUNT(*) FROM work_item_sync_runs WHERE RepoId NOT IN (SELECT id FROM canon_repos)'

# ── Products (ProductId references) ────────────────────────────────────────
$report['epics.product_nulled']           = CountRows 'SELECT COUNT(*) FROM epics WHERE ProductId IS NOT NULL AND ProductId NOT IN (SELECT id FROM canon_products)'
$report['product_stories.deleted']        = CountRows 'SELECT COUNT(*) FROM product_stories WHERE ProductId NOT IN (SELECT id FROM canon_products)'
$report['user_product_roles.deleted']     = CountRows 'SELECT COUNT(*) FROM user_product_roles WHERE ProductId NOT IN (SELECT id FROM canon_products)'
$report['product_iterations.deleted']     = CountRows 'SELECT COUNT(*) FROM product_iterations WHERE ProductId NOT IN (SELECT id FROM canon_products)'
$report['product_area_paths.deleted']     = CountRows 'SELECT COUNT(*) FROM product_area_paths WHERE ProductId NOT IN (SELECT id FROM canon_products)'
$report['product_saved_prefixes.deleted'] = CountRows 'SELECT COUNT(*) FROM product_saved_prefixes WHERE ProductId NOT IN (SELECT id FROM canon_products) OR RepoId NOT IN (SELECT id FROM canon_repos)'

# ── Work items + inbox/queue fan-out ───────────────────────────────────────
# Inbox view (`/inbox`) is backed by `task_queue`. Test fixtures spawn synthetic
# WorkItemIds (`wi-*`, `bf-*`, `mv-*`, single letters, etc.) under Provider='local'
# and 'stub'. Real items are Provider='ado' / 'github' (synced from upstream)
# plus a small explicit allow-list for local items the operator created by hand.
$report['task_queue.deleted']             = CountRows "SELECT COUNT(*) FROM task_queue WHERE WorkItemId NOT IN ($keptWiSql)"
$report['task_queue_history.cascaded']    = CountRows "SELECT COUNT(*) FROM task_queue_history WHERE QueueId IN (SELECT Id FROM task_queue WHERE WorkItemId NOT IN ($keptWiSql))"
$report['task_queue_history.orphans']     = CountRows "SELECT COUNT(*) FROM task_queue_history WHERE QueueId NOT IN (SELECT Id FROM task_queue)"
$report['task_transcript_event.deleted']  = CountRows "SELECT COUNT(*) FROM task_transcript_event WHERE WorkItemId NOT IN ($keptWiSql)"
$report['agent_assignments.deleted']      = CountRows "SELECT COUNT(*) FROM agent_assignments WHERE WorkItemId NOT IN ($keptWiSql)"
$report['deliverables.wi_deleted']        = CountRows "SELECT COUNT(*) FROM deliverables WHERE WorkItemId NOT IN ($keptWiSql)"
$report['build_fix_pending.wi_deleted']   = CountRows "SELECT COUNT(*) FROM build_fix_pending WHERE WorkItemId NOT IN ($keptWiSql)"
$report['work_item_links.deleted']        = CountRows "SELECT COUNT(*) FROM work_item_links WHERE FromId NOT IN ($keptWiSql) OR ToId NOT IN ($keptWiSql)"
$report['prs.work_item_nulled']           = CountRows "SELECT COUNT(*) FROM prs WHERE WorkItemId IS NOT NULL AND WorkItemId NOT IN ($keptWiSql)"
$report['work_items.deleted']             = CountRows "SELECT COUNT(*) FROM work_items WHERE Id NOT IN ($keptWiSql)"

# ── The repo + product rows themselves ─────────────────────────────────────
$report['repos.deleted']                  = CountRows 'SELECT COUNT(*) FROM repos WHERE Id NOT IN (SELECT id FROM canon_repos)'
$report['products.deleted']               = CountRows 'SELECT COUNT(*) FROM products WHERE Id NOT IN (SELECT id FROM canon_products)'

# Atlas slug/name re-assert (cosmetic; the CreateProduct_Duplicate_Id test
# overwrote both even though the endpoint returned 409).
$report['products.atlas_reset']           = CountRows "SELECT COUNT(*) FROM products WHERE Id = 'atlas' AND (Slug != 'atlas' OR Name != 'Atlas')"

Write-Host ""
Write-Host "[scrub] mode=$mode"
$report.GetEnumerator() | ForEach-Object { '{0,-38} {1}' -f $_.Key, $_.Value }

if ($Apply) {
    Write-Host ""
    Write-Host "[scrub] applying ..."

    # ── Crews / agents ─────────────────────────────────────────────────────
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

    # ── Repos: null out nullable FK refs first ─────────────────────────────
    [void](Sql 'UPDATE cards         SET RepoId = NULL WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()
    [void](Sql 'UPDATE workstreams   SET RepoId = NULL WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()
    [void](Sql 'UPDATE prs           SET RepoId = NULL WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()
    [void](Sql 'UPDATE work_items    SET RepoId = NULL WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()
    [void](Sql 'UPDATE story_spawns  SET RepoId = NULL WHERE RepoId IS NOT NULL AND RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()

    # ── Repos: delete non-nullable dependents (children before parents) ────
    [void](Sql "DELETE FROM build_stage_logs      WHERE StageId    IN ($badStagesSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM build_failure_points  WHERE StageId    IN ($badStagesSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM build_artifact_files  WHERE ArtifactId IN ($badArtsSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM build_stages          WHERE BuildId    IN ($badBuildsSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM build_artifacts       WHERE BuildId    IN ($badBuildsSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM build_annotations     WHERE BuildId    IN ($badBuildsSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM build_fix_pending     WHERE BuildId    IN ($badBuildsSql)").ExecuteNonQuery()
    [void](Sql 'DELETE FROM builds                WHERE RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM build_validation_runs   WHERE RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()
    [void](Sql "DELETE FROM domain_experts          WHERE RepoId NOT IN (SELECT id FROM canon_repos) AND RepoId != '*'").ExecuteNonQuery()
    [void](Sql 'DELETE FROM repo_iteration_bindings WHERE RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM sync_run_items          WHERE RunId IN (SELECT Id FROM work_item_sync_runs WHERE RepoId NOT IN (SELECT id FROM canon_repos))').ExecuteNonQuery()
    [void](Sql 'DELETE FROM work_item_sync_runs     WHERE RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()

    # ── Products: null nullable refs first ─────────────────────────────────
    [void](Sql 'UPDATE epics SET ProductId = NULL WHERE ProductId IS NOT NULL AND ProductId NOT IN (SELECT id FROM canon_products)').ExecuteNonQuery()

    # ── Products: delete non-nullable dependents ───────────────────────────
    [void](Sql 'DELETE FROM product_stories        WHERE ProductId NOT IN (SELECT id FROM canon_products)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM user_product_roles    WHERE ProductId NOT IN (SELECT id FROM canon_products)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM product_iterations    WHERE ProductId NOT IN (SELECT id FROM canon_products)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM product_area_paths    WHERE ProductId NOT IN (SELECT id FROM canon_products)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM product_saved_prefixes WHERE ProductId NOT IN (SELECT id FROM canon_products) OR RepoId NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()

    # ── Inbox / queue: cascade through every WorkItemId ref ────────────────
    # task_queue_history → task_queue (FK QueueId). Delete history before its
    # parent. Also nuke pre-existing orphans (rows whose QueueId no longer
    # exists in task_queue, e.g. because Forge.Api previously hard-deleted
    # the queue row without its history).
    [void](Sql "DELETE FROM task_queue_history WHERE QueueId IN (SELECT Id FROM task_queue WHERE WorkItemId NOT IN ($keptWiSql))").ExecuteNonQuery()
    [void](Sql "DELETE FROM task_queue_history WHERE QueueId NOT IN (SELECT Id FROM task_queue)").ExecuteNonQuery()
    [void](Sql "DELETE FROM task_queue              WHERE WorkItemId NOT IN ($keptWiSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM task_transcript_event   WHERE WorkItemId NOT IN ($keptWiSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM agent_assignments       WHERE WorkItemId NOT IN ($keptWiSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM deliverables            WHERE WorkItemId NOT IN ($keptWiSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM build_fix_pending       WHERE WorkItemId NOT IN ($keptWiSql)").ExecuteNonQuery()
    [void](Sql "DELETE FROM work_item_links         WHERE FromId NOT IN ($keptWiSql) OR ToId NOT IN ($keptWiSql)").ExecuteNonQuery()
    # PRs survive (they're tied to real repos); just sever the WorkItemId link.
    [void](Sql "UPDATE prs SET WorkItemId = NULL    WHERE WorkItemId IS NOT NULL AND WorkItemId NOT IN ($keptWiSql)").ExecuteNonQuery()
    # Now safe to drop the work_item rows themselves.
    [void](Sql "DELETE FROM work_items              WHERE Id NOT IN ($keptWiSql)").ExecuteNonQuery()

    # ── Repos + products themselves (parents last) ─────────────────────────
    [void](Sql 'DELETE FROM repos    WHERE Id NOT IN (SELECT id FROM canon_repos)').ExecuteNonQuery()
    [void](Sql 'DELETE FROM products WHERE Id NOT IN (SELECT id FROM canon_products)').ExecuteNonQuery()

    # ── Re-assert atlas product canonical fields (Slug, Name) ──────────────
    [void](Sql "UPDATE products SET Slug = 'atlas', Name = 'Atlas' WHERE Id = 'atlas' AND (Slug != 'atlas' OR Name != 'Atlas')").ExecuteNonQuery()

    $tx.Commit()
    Write-Host "[scrub] APPLIED."
} else {
    $tx.Rollback()
    Write-Host ""
    Write-Host "[scrub] DRY-RUN — no changes written. Re-run with -Apply to commit."
}
$cn.Close()
