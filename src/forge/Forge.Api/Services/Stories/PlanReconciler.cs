// -----------------------------------------------------------------------
// <copyright file="PlanReconciler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 42 — keeps persisted <see cref="WorkstreamRow"/> + <see cref="WorkItemRow"/>
/// state in sync with the current <c>implementation-plan.md</c>. The plan is
/// the source of truth; rows are derived. The reconciler runs on every plan
/// change (per-iter commit, Resume/Restart of the implementation-plan artifact,
/// scheduled tick, manual API).
///
/// <para>Diff policy (confirmed with operator):</para>
/// <list type="bullet">
/// <item><b>Anchor in plan, not in DB</b> → insert new <see cref="WorkstreamRow"/> + <see cref="WorkItemRow"/>.</item>
/// <item><b>Anchor in DB, not in plan, status=planning, no merged PR</b> → cancel queue rows, drop the row.</item>
/// <item><b>Anchor in DB, not in plan, in-flight (active/review with open PR or queue)</b> → cancel queue rows + invocations, close PR (handled by <see cref="WorkstreamsEndpoints"/>.Cancel semantics — flips Status=cancelled).</item>
/// <item><b>Anchor in DB, not in plan, has merged PR</b> → flip Status=<c>historic</c> (frozen — can't unship).</item>
/// <item><b>Anchor in both, content hash unchanged</b> → no-op.</item>
/// <item><b>Anchor in both, content hash changed, in-flight</b> → update Summary + DescriptionMd + PlanContentHash; engineer's next iteration reads the new brief.</item>
/// <item><b>Anchor in both, content hash changed, not started</b> → same update; no enqueue change.</item>
/// <item><b>Anchor in both, content hash changed, has merged PR</b> → split: existing row marked <c>historic</c>, new row inserted with anchor suffix <c>@v2</c> for the delta.</item>
/// </list>
///
/// <para>The reconciler is fully idempotent: rerunning on a stable plan
/// produces zero writes. Historic + cancelled rows are filtered out of the
/// match set so they don't re-trigger on subsequent ticks.</para>
/// </summary>
public sealed class PlanReconciler
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IServiceProvider services;
    private readonly IStoryGitOps git;
    private readonly IAgentRunner runner;
    private readonly ILogger<PlanReconciler>? logger;

    public PlanReconciler(
        IServiceProvider services,
        IStoryGitOps git,
        IAgentRunner runner,
        ILogger<PlanReconciler>? logger = null)
    {
        this.services = services;
        this.git = git;
        this.runner = runner;
        this.logger = logger;
    }

    /// <summary>Outcome of a reconcile pass — for audit + the manual API response.</summary>
    public sealed record ReconciliationReport(
        string SpawnId,
        int StagesInPlan,
        int Added,
        int Updated,
        int DroppedNotStarted,
        int CancelledInFlight,
        int MarkedHistoric,
        int SplitForDelta,
        int NoOp,
        bool ArtifactWritten);

    /// <summary>
    /// Reconcile a single spawn. Idempotent and safe to call concurrently
    /// per-spawn (DB writes are serialised by EF's change tracker; the git
    /// commit at the end is serialised per-branch by <see cref="IStoryGitOps"/>).
    /// </summary>
    public async Task<ReconciliationReport?> ReconcileAsync(string spawnId, CancellationToken ct = default)
    {
        using var scope = this.services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.Id == spawnId, ct);
        if (spawn is null)
        {
            this.logger?.LogWarning("Reconcile: no spawn {SpawnId}", spawnId);
            return null;
        }

        var planBranch = PlanDocStateHelpers.FindDocBranch(spawn, "implementation-plan");
        var planPath = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}/implementation-plan.md";

        // The repo + branch may not exist yet (e.g. spawn created but plan
        // phase hasn't started). Treat that as "no plan" — nothing to do.
        var repo = spawn.RepoId is null
            ? null
            : await control.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == spawn.RepoId, ct);

        // Read from the worktree on disk, NOT via `git show origin/{branch}`.
        // The architect agent writes to the worktree and `CommitPathInWorktreeAsync`
        // commits to the LOCAL plan branch — nothing is pushed to origin until the
        // plan PR opens, and per-iter commits made AFTER the PR merge stay
        // local-only. Reading from disk picks up the freshest content the
        // architect actually wrote.
        // Phase 45 — pass the spawn's chosen base branch so worktree
        // creation roots at the right branch when the worktree must be
        // materialised on first read.
        var spawnBase = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var planMd = await this.SafeReadWorktreeAsync(repo, planBranch, planPath, spawnBase, ct);
        if (planMd is null)
        {
            this.logger?.LogInformation(
                "Reconcile: plan file not yet on branch {Branch} for spawn {SpawnId}; skipping.",
                planBranch, spawnId);
            return null;
        }

        var canonical = PlanParser.Parse(planMd);
        var planStages = canonical.AllStages().ToList();
        if (planStages.Count == 0)
        {
            this.logger?.LogInformation(
                "Reconcile: plan parsed empty for spawn {SpawnId} — refusing to drop existing rows on a malformed plan.",
                spawnId);
            return new ReconciliationReport(spawnId, 0, 0, 0, 0, 0, 0, 0, 0, false);
        }

        // Phase 46 — refuse to reconcile when the parser surfaced a
        // structural violation. Same gate as ProduceDecompositionAsync;
        // mirrors that path's "fail loudly so the operator sees the code
        // and re-emits via the architect's evaluator loop".
        if (canonical.ParseErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Reconcile refused: {string.Join(", ", canonical.ParseErrors)}");
        }

        // Phase 46 — resolve every stage's effective deps (explicit +
        // phase-expansion). Cycles short-circuit with plan.dep_cycle. We
        // resolve once and pass the map into the insert/update paths so
        // both produce DepsJson consistent with the plan-derived
        // ProduceDecompositionAsync output.
        var resolvedDeps = DecompositionDepsResolver.ResolveAll(canonical);

        var existing = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ct);

        // Active rows are the ones we diff against. Historic / cancelled are
        // frozen and ignored — they exist for audit only. Pre-Phase42 rows
        // with no PlanAnchor are excluded from the dictionary (they'd collide
        // on string.Empty if there are two of them) and are simply left
        // alone — the reconciler doesn't touch them.
        var active = existing
            .Where(w => w.Status != WorkstreamStatuses.Historic
                     && w.Status != "cancelled"
                     && w.Status != "paused"
                     && !string.IsNullOrEmpty(w.PlanAnchor))
            .ToDictionary(w => w.PlanAnchor!, StringComparer.Ordinal);

        var planByAnchor = planStages.ToDictionary(s => s.Anchor, StringComparer.Ordinal);

        var added = 0;
        var updated = 0;
        var droppedNotStarted = 0;
        var cancelledInFlight = 0;
        var markedHistoric = 0;
        var splitForDelta = 0;
        var noop = 0;

        var maxSort = await db.Workstreams.Select(w => (int?)w.Sort).MaxAsync(ct) ?? -1;

        // Pass 1 — anchors in plan: insert / update / split.
        foreach (var stage in planStages)
        {
            if (active.TryGetValue(stage.Anchor, out var row))
            {
                if (string.Equals(row.PlanContentHash, stage.ContentHash, StringComparison.Ordinal))
                {
                    // Phase 46 — even on a no-op content-hash match, refresh
                    // DepsJson + PhaseAnchor when null so the columns
                    // backfill incrementally on legacy rows the migration
                    // didn't catch (e.g. WorkstreamRow created by the
                    // pre-Phase-46 manual API path).
                    SyncDepsAndAnchor(row, stage, resolvedDeps);
                    noop++;
                    continue;
                }

                // Hash differs → scope edit. Branch on row state.
                var rowState = await this.ClassifyAsync(control, db, row, ct);
                if (rowState == RowState.Shipped)
                {
                    // Shipped + plan modified → split. Old row stays historic
                    // with its merged PR; new row picks up the delta.
                    row.Status = WorkstreamStatuses.Historic;
                    row.PlanAnchor = $"{stage.Anchor}@{row.PlanContentHash?[..8] ?? "shipped"}";
                    UpdateMatchingWorkItem(control, db, row.Id, wi =>
                    {
                        wi.PlanAnchor = row.PlanAnchor;
                    });
                    InsertWorkstream(control, db, spawn, stage, ref maxSort, resolvedDeps);
                    splitForDelta++;
                }
                else
                {
                    // In-flight or not-started → in-place update. Engineer
                    // reads wi.DescriptionMd at next iteration's prompt build.
                    row.Name = stage.Name;
                    row.Summary = StageSummary(stage);
                    row.PlanContentHash = stage.ContentHash;
                    SyncDepsAndAnchor(row, stage, resolvedDeps);
                    UpdateMatchingWorkItem(control, db, row.Id, wi =>
                    {
                        wi.Title = stage.Name;
                        wi.DescriptionMd = StageDescription(stage);
                        wi.UpdatedAt = DateTimeOffset.UtcNow;
                    });
                    updated++;
                }
            }
            else
            {
                InsertWorkstream(control, db, spawn, stage, ref maxSort, resolvedDeps);
                added++;
            }
        }

        // docs/forge/e2e.md Stage 3.1 — qa-e2e companion pass. For each
        // plan stage with at least one test scenario, ensure a trailing
        // qa-e2e WorkstreamRow exists with anchor `{stage.Anchor}/e2e`.
        // Same diff semantics as the impl loop above: insert if missing,
        // update (re-sync DepsJson against the resolver) if present and
        // the content hash drifted. Pass 2 below treats these synthetic
        // anchors as part of the plan so they don't get swept as orphans.
        foreach (var stage in planStages)
        {
            if (stage.TestScenarios.Count == 0) continue;

            var qaAnchor = CrewStoryArtifactStrategy.QaE2eAnchorFor(stage.Anchor);
            if (active.TryGetValue(qaAnchor, out var qaRow))
            {
                if (string.Equals(qaRow.PlanContentHash, stage.ContentHash, StringComparison.Ordinal))
                {
                    // No-op: hash unchanged. Backfill DepsJson + PhaseAnchor
                    // for any legacy qa-e2e row inserted before this layer
                    // existed (DepsJson must reflect impl-sibling title +
                    // cross-stage e2e titles).
                    SyncQaE2eDepsAndAnchor(qaRow, stage, resolvedDeps);
                    noop++;
                    continue;
                }

                // Scope edit on the parent stage → re-sync qa-e2e brief
                // and deps. We never split qa-e2e rows (no shipped/historic
                // delta path); the row is regenerated content-wise on
                // every plan change.
                qaRow.Summary = QaE2eStageSummary(stage);
                qaRow.PlanContentHash = stage.ContentHash;
                SyncQaE2eDepsAndAnchor(qaRow, stage, resolvedDeps);
                UpdateMatchingWorkItem(control, db, qaRow.Id, wi =>
                {
                    wi.Title = $"{stage.Name} — E2E";
                    wi.DescriptionMd = QaE2eStageDescription(stage);
                    wi.PlanContentHash = stage.ContentHash;
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                });
                updated++;
            }
            else
            {
                InsertQaE2eWorkstream(control, db, spawn, stage, ref maxSort, resolvedDeps);
                added++;
            }
        }

        // Pass 2 — orphaned rows: anchors in DB but not in plan, PLUS
        // legacy rows that pre-date Phase 42 and have no anchor at all.
        // Both are obsolete relative to the current plan — they should
        // move to a terminal status (cancelled / historic), never hard
        // deleted, so the audit trail survives.
        //
        // Filtering by StorySpawnId (already applied to `existing`) keeps
        // this scoped to this spawn's decomposition; manually-created
        // workstreams (StorySpawnId=null) are never in the existing set.
        //
        // docs/forge/e2e.md Stage 3.1 — qa-e2e companion anchors
        // (`{stage.Anchor}/e2e`) count as "in plan" when their parent
        // stage is still in the plan AND still declares at least one
        // test scenario. A qa-e2e row whose parent stage's TestScenarios
        // became empty (or whose parent stage was removed entirely) is
        // genuinely orphaned and runs through the same cancel / historic
        // sweep as impl rows.
        var qaE2eAnchorsInPlan = new HashSet<string>(
            planStages
                .Where(s => s.TestScenarios.Count > 0)
                .Select(s => CrewStoryArtifactStrategy.QaE2eAnchorFor(s.Anchor)),
            StringComparer.Ordinal);

        var orphanedAnchored = active.Values
            .Where(w => !planByAnchor.ContainsKey(w.PlanAnchor!)
                     && !qaE2eAnchorsInPlan.Contains(w.PlanAnchor!))
            .ToList();
        var legacyUnanchored = existing
            .Where(w => w.Status != WorkstreamStatuses.Historic
                     && w.Status != "cancelled"
                     && w.Status != "paused"
                     && string.IsNullOrEmpty(w.PlanAnchor))
            .ToList();

        foreach (var row in orphanedAnchored.Concat(legacyUnanchored))
        {
            var rowState = await this.ClassifyAsync(control, db, row, ct);
            switch (rowState)
            {
                case RowState.Shipped:
                    row.Status = WorkstreamStatuses.Historic;
                    markedHistoric++;
                    break;

                case RowState.InFlight:
                    await this.CancelInFlightAsync(control, db, row, ct);
                    cancelledInFlight++;
                    break;

                case RowState.NotStarted:
                    await this.DropNotStartedAsync(control, db, row, ct);
                    droppedNotStarted++;
                    break;
            }
        }

        // Persist DB writes before the artifact commit so a git failure
        // doesn't leave the DB in a half-state.
        await db.SaveChangesAsync(ct);

        // Rewrite spawn.DecompositionJson + commit workstreams.yaml on the
        // plan branch so the artifact stays in lockstep with the plan.
        // Phase 46 — worktree artefact is YAML (was JSON). The DB column
        // DecompositionJson keeps its JSON shape for back-compat with the
        // existing UI store + endpoints; the YAML view is rendered
        // on-the-fly from the same data via DecompositionYamlSerializer.
        var artifactWritten = false;
        try
        {
            var artifactJson = await BuildArtifactJsonAsync(control, db, spawn.Id, planByAnchor, ct);
            spawn.DecompositionJson = artifactJson;
            spawn.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (repo is not null)
            {
                var artifactRepoPath = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}/workstreams.yaml";
                var artifactYaml = ToYaml(artifactJson);
                // Sequential-mode reconciles run on the per-doc
                // implementation-plan worktree; legacy parallel-mode stays
                // on the shared plan worktree.
                var reconcileSlug = PlanDocStateHelpers.IsSequentialMode(spawn)
                    ? PlanDocStateHelpers.CanonicalDocWorktreeSlug(spawn.StoryId, "implementation-plan")
                    : PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug();
                await this.git.CommitFilesAsync(
                    repo,
                    planBranch,
                    reconcileSlug,
                    new Dictionary<string, string> { [artifactRepoPath] = artifactYaml },
                    $"reconcile: regenerate workstreams.yaml from implementation-plan.md ({planStages.Count} stages)",
                    ct);
                artifactWritten = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "Reconcile: workstreams.yaml artifact write failed for spawn {SpawnId}; DB state is up-to-date.",
                spawnId);
        }

        var report = new ReconciliationReport(
            SpawnId: spawnId,
            StagesInPlan: planStages.Count,
            Added: added,
            Updated: updated,
            DroppedNotStarted: droppedNotStarted,
            CancelledInFlight: cancelledInFlight,
            MarkedHistoric: markedHistoric,
            SplitForDelta: splitForDelta,
            NoOp: noop,
            ArtifactWritten: artifactWritten);

        if (added + updated + droppedNotStarted + cancelledInFlight + markedHistoric + splitForDelta > 0)
        {
            this.logger?.LogInformation(
                "Reconcile {SpawnId}: +{Added} ~{Updated} -{Drop} cancel={Cancel} historic={Historic} split={Split} noop={NoOp}",
                spawnId, added, updated, droppedNotStarted, cancelledInFlight, markedHistoric, splitForDelta, noop);

            if (hub is not null)
            {
                await hub.Clients.All.SendAsync(
                    "story-spawn.reconciled",
                    new { spawnId, storyId = spawn.StoryId, report },
                    ct);
            }
        }

        return report;
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task<string?> SafeReadAsync(RepoRow? repo, string branch, string path, CancellationToken ct)
    {
        try
        {
            return await this.git.ReadFileAsync(repo, branch, path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex, "Reconcile: ReadFileAsync failed for {Branch}:{Path}", branch, path);
            return null;
        }
    }

    /// <summary>
    /// Read a repo-relative path from the LOCAL plan-branch worktree on disk.
    /// Falls back to <see cref="SafeReadAsync"/> (which reads <c>git show
    /// origin/{branch}:{path}</c>) when the worktree isn't materialised yet —
    /// e.g. an in-memory test git ops impl, or a spawn that has never had the
    /// architect run on this host. Worktree read is the canonical path because
    /// per-iter commits are local-only (no auto-push), so origin/{branch} can
    /// be many iterations stale once the plan PR has merged.
    /// </summary>
    private async Task<string?> SafeReadWorktreeAsync(RepoRow? repo, string branch, string path, string baseBranch, CancellationToken ct)
    {
        if (repo is null) return await this.SafeReadAsync(repo, branch, path, ct);

        try
        {
            var worktreePath = await this.git.EnsureWorktreeAsync(repo, branch, baseBranch, ct);
            var diskPath = Path.Combine(worktreePath, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(diskPath))
            {
                return await File.ReadAllTextAsync(diskPath, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex,
                "Reconcile: worktree read failed for {Branch}:{Path}; falling back to origin read.",
                branch, path);
        }

        return await this.SafeReadAsync(repo, branch, path, ct);
    }

    private static void InsertWorkstream(
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, PlanParser.CanonicalStage stage, ref int maxSort,
        IReadOnlyDictionary<string, DecompositionDepsResolver.ResolvedStageDeps> resolvedDeps)
    {
        var wsId = WorkstreamIdFor(spawn.StoryId, stage.Anchor);
        if (db.Workstreams.Any(x => x.Id == wsId)) return; // idempotent

        var safeStory = StoryPaths.Safe(spawn.StoryId);
        var branchSlug = stage.Anchor.Replace('/', '-').Replace('@', '-');

        // Phase 46 — derive deps + phase anchor from the resolver so a
        // freshly-inserted row carries the same DepsJson the
        // ProduceDecompositionAsync path would have produced.
        resolvedDeps.TryGetValue(stage.Anchor, out var rd);
        var depsJson = rd is { Deps.Count: > 0 }
            ? JsonSerializer.Serialize(rd.Deps)
            : null;
        var slash = stage.Anchor.IndexOf('/');
        var phaseAnchor = slash > 0 ? stage.Anchor[..slash] : stage.Anchor;

        db.Workstreams.Add(new WorkstreamRow
        {
            Id = wsId,
            Sort = ++maxSort,
            RepoId = spawn.RepoId,
            Name = stage.Name,
            Branch = $"ws/{safeStory}/{branchSlug}",
            // Phase 45 — reconciler-created workstreams inherit the spawn's
            // chosen base branch so PRs they produce target the right branch.
            Base = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch,
            Epic = spawn.StoryId,
            Status = "planning",
            WorktreeJson = "[\"(not yet created)\"]",
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Summary = StageSummary(stage),
            BarStart = 0,
            BarEnd = 5,
            Planned = true,
            StorySpawnId = spawn.Id,
            PlanAnchor = stage.Anchor,
            PlanContentHash = stage.ContentHash,
            DepsJson = depsJson,
            PhaseAnchor = phaseAnchor,
        });

        if (!db.WorkItems.Any(x => x.Id == wsId))
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wsId,
                ParentId = spawn.StoryId,
                FeatureRef = spawn.StoryId,
                RepoId = spawn.RepoId,
                Provider = "local",
                Track = "dev",
                Type = "task",
                State = "active",
                Title = stage.Name,
                DescriptionMd = StageDescription(stage),
                Points = Math.Max(1, spawn.StoryPoints),
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                PlanAnchor = stage.Anchor,
                PlanContentHash = stage.ContentHash,
            });
        }
    }

    private static void UpdateMatchingWorkItem(ControlDbContext control, ProductDbContext db, string wsId, Action<WorkItemRow> mutate)
    {
        var wi = db.WorkItems.FirstOrDefault(w => w.Id == wsId);
        if (wi is null) return;
        mutate(wi);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 3.1 — insert a trailing qa-e2e WorkstreamRow
    /// for a Stage that declares <c>### Test Scenarios</c>. Mirrors
    /// <see cref="InsertWorkstream"/> but uses the synthetic anchor
    /// <c>{stage.Anchor}/e2e</c>, sets <c>Category="qa-e2e"</c>, and
    /// composes the row's DepsJson as <c>[impl-sibling-title,
    /// …cross-stage e2e titles]</c> so the existing title-keyed dep
    /// wiring in <see cref="StorySpawnOrchestrator"/>.PersistDecompositionAsync
    /// routes the edges correctly.
    /// </summary>
    private static void InsertQaE2eWorkstream(
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, PlanParser.CanonicalStage stage, ref int maxSort,
        IReadOnlyDictionary<string, DecompositionDepsResolver.ResolvedStageDeps> resolvedDeps)
    {
        var qaAnchor = CrewStoryArtifactStrategy.QaE2eAnchorFor(stage.Anchor);
        var wsId = WorkstreamIdFor(spawn.StoryId, qaAnchor);
        if (db.Workstreams.Any(x => x.Id == wsId)) return; // idempotent

        var safeStory = StoryPaths.Safe(spawn.StoryId);
        var branchSlug = qaAnchor.Replace('/', '-').Replace('@', '-');

        var deps = BuildQaE2eDeps(stage, resolvedDeps);
        var depsJson = deps.Count > 0 ? JsonSerializer.Serialize(deps) : null;
        var slash = stage.Anchor.IndexOf('/');
        var phaseAnchor = slash > 0 ? stage.Anchor[..slash] : stage.Anchor;

        db.Workstreams.Add(new WorkstreamRow
        {
            Id = wsId,
            Sort = ++maxSort,
            RepoId = spawn.RepoId,
            Name = $"{stage.Name} — E2E",
            // Stage 3.2 will rewrite the branch shape (`ws/{storyId}/{stageSlug}-e2e`).
            // For Stage 3.1 the anchor-derived slug already yields a
            // distinct branch under the same prefix.
            Branch = $"ws/{safeStory}/{branchSlug}",
            Base = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch,
            Epic = spawn.StoryId,
            Status = "planning",
            WorktreeJson = "[\"(not yet created)\"]",
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Summary = QaE2eStageSummary(stage),
            BarStart = 0,
            BarEnd = 5,
            Planned = true,
            StorySpawnId = spawn.Id,
            PlanAnchor = qaAnchor,
            PlanContentHash = stage.ContentHash,
            DepsJson = depsJson,
            PhaseAnchor = phaseAnchor,
            Category = WorkstreamCategories.QaE2e,
        });

        if (!db.WorkItems.Any(x => x.Id == wsId))
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wsId,
                ParentId = spawn.StoryId,
                FeatureRef = spawn.StoryId,
                RepoId = spawn.RepoId,
                Provider = "local",
                Track = "dev",
                Type = "task",
                State = "active",
                Title = $"{stage.Name} — E2E",
                DescriptionMd = QaE2eStageDescription(stage),
                Points = Math.Max(1, spawn.StoryPoints),
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                PlanAnchor = qaAnchor,
                PlanContentHash = stage.ContentHash,
            });
        }
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 3.1 — re-derive DepsJson + PhaseAnchor for
    /// an existing qa-e2e row. Idempotent (only writes when the value
    /// would change). Mirrors <see cref="SyncDepsAndAnchor"/> but uses
    /// the qa-e2e dep composition (impl-sibling + cross-stage e2e
    /// upstreams) instead of the impl stage's resolver deps.
    /// </summary>
    private static void SyncQaE2eDepsAndAnchor(
        WorkstreamRow row,
        PlanParser.CanonicalStage stage,
        IReadOnlyDictionary<string, DecompositionDepsResolver.ResolvedStageDeps> resolvedDeps)
    {
        var deps = BuildQaE2eDeps(stage, resolvedDeps);
        var newDepsJson = deps.Count > 0 ? JsonSerializer.Serialize(deps) : null;
        if (!string.Equals(row.DepsJson, newDepsJson, StringComparison.Ordinal))
        {
            row.DepsJson = newDepsJson;
        }

        var slash = stage.Anchor.IndexOf('/');
        var newPhaseAnchor = slash > 0 ? stage.Anchor[..slash] : stage.Anchor;
        if (!string.Equals(row.PhaseAnchor, newPhaseAnchor, StringComparison.Ordinal))
        {
            row.PhaseAnchor = newPhaseAnchor;
        }

        // Defensive: legacy qa-e2e rows that pre-date the Category column
        // backfill (Stage 1.1 migration runs before this reconcile, but a
        // hand-crafted DB seed could still slip through with Category='impl').
        if (row.Category != WorkstreamCategories.QaE2e)
        {
            row.Category = WorkstreamCategories.QaE2e;
        }
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 3.1 — compose the qa-e2e dep list for a
    /// stage: <c>[impl-sibling-title, …resolver.CrossStageE2eDeps]</c>,
    /// deduped against the impl sibling and stable in plan order.
    /// </summary>
    private static List<string> BuildQaE2eDeps(
        PlanParser.CanonicalStage stage,
        IReadOnlyDictionary<string, DecompositionDepsResolver.ResolvedStageDeps> resolvedDeps)
    {
        resolvedDeps.TryGetValue(stage.Anchor, out var rd);
        var deps = new List<string>(1 + (rd?.CrossStageE2eDeps?.Count ?? 0)) { stage.Name };
        if (rd?.CrossStageE2eDeps is { Count: > 0 } crossE2eDeps)
        {
            foreach (var t in crossE2eDeps)
            {
                if (!deps.Contains(t, StringComparer.OrdinalIgnoreCase))
                {
                    deps.Add(t);
                }
            }
        }
        return deps;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 3.1 — short summary string for a qa-e2e
    /// WorkstreamRow.Summary. Mirrors <c>StageSummary</c>'s shape.
    /// </summary>
    private static string QaE2eStageSummary(PlanParser.CanonicalStage stage) =>
        $"E2E for Stage {stage.PhaseNumber}.{stage.StageNumber}: Gherkin + step-defs + e2e run "
            + $"covering {stage.TestScenarios.Count} acceptance "
            + (stage.TestScenarios.Count == 1 ? "scenario." : "scenarios.");

    /// <summary>
    /// docs/forge/e2e.md Stage 3.1 — long-form description used as the
    /// qa-e2e WorkItem's DescriptionMd. The QA / engineer agents read this
    /// at iter-build time to know which scenarios they're implementing.
    /// </summary>
    private static string QaE2eStageDescription(PlanParser.CanonicalStage stage)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("E2E tests for Stage ").Append(stage.PhaseNumber).Append('.').Append(stage.StageNumber)
            .Append(" of `implementation-plan.md` — ").AppendLine(stage.Name);
        sb.AppendLine();
        sb.AppendLine("Trails the impl workstream for this Stage. Owns the Gherkin");
        sb.AppendLine("`.feature` file, the matching step-definition file, and the e2e");
        sb.AppendLine("run that gates the Stage's completion. Implementation lands");
        sb.AppendLine("under the per-stage `tests/Features/{storyId}/{phaseSlug}/` and");
        sb.AppendLine("`tests/Steps/{storyId}/{phaseSlug}/` subtrees (Stage 4.2 onward).");
        sb.AppendLine();
        if (stage.TestScenarios.Count > 0)
        {
            sb.AppendLine("## Test Scenarios (from implementation-plan.md)");
            foreach (var sc in stage.TestScenarios)
            {
                sb.Append("- ").AppendLine(sc);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phase 46 — re-derive <see cref="WorkstreamRow.DepsJson"/> +
    /// <see cref="WorkstreamRow.PhaseAnchor"/> for a row whose plan stage
    /// is otherwise unchanged. Idempotent: writes only when the value
    /// would actually change so EF's change tracker doesn't bump
    /// UpdatedAt without cause.
    /// </summary>
    private static void SyncDepsAndAnchor(
        WorkstreamRow row,
        PlanParser.CanonicalStage stage,
        IReadOnlyDictionary<string, DecompositionDepsResolver.ResolvedStageDeps> resolvedDeps)
    {
        resolvedDeps.TryGetValue(stage.Anchor, out var rd);
        var newDepsJson = rd is { Deps.Count: > 0 }
            ? JsonSerializer.Serialize(rd.Deps)
            : null;
        if (!string.Equals(row.DepsJson, newDepsJson, StringComparison.Ordinal))
        {
            row.DepsJson = newDepsJson;
        }

        var slash = stage.Anchor.IndexOf('/');
        var newPhaseAnchor = slash > 0 ? stage.Anchor[..slash] : stage.Anchor;
        if (!string.Equals(row.PhaseAnchor, newPhaseAnchor, StringComparison.Ordinal))
        {
            row.PhaseAnchor = newPhaseAnchor;
        }
    }

    private async Task<RowState> ClassifyAsync(ControlDbContext control, ProductDbContext db, WorkstreamRow row, CancellationToken ct)
    {
        // Shipped: any linked PR is in the merged column, or status is a
        // shipped value.
        if (row.Status is "merging" or "complete") return RowState.Shipped;

        var prIds = StoriesJsonHelpers.Deserialize(row.PrsJson) ?? new List<string>();
        if (prIds.Count > 0)
        {
            var hasMerged = await db.Prs
                .AsNoTracking()
                .Where(p => prIds.Contains(p.Id) && p.Col == "merged")
                .AnyAsync(ct);
            if (hasMerged) return RowState.Shipped;

            // Has open PR(s) → in-flight regardless of WorkstreamRow.Status.
            return RowState.InFlight;
        }

        // No PR yet. Anything beyond "planning" with running queue rows = in-flight.
        var hasLiveQueue = await db.TaskQueue
            .AsNoTracking()
            .AnyAsync(q => q.WorkItemId == row.Id && q.State != "completed" && q.State != "cancelled", ct);
        if (hasLiveQueue) return RowState.InFlight;

        if (row.Status is "active" or "review") return RowState.InFlight;
        return RowState.NotStarted;
    }

    private async Task CancelInFlightAsync(ControlDbContext control, ProductDbContext db, WorkstreamRow row, CancellationToken ct)
    {
        // Mirrors WorkstreamsEndpoints.Cancel — kill agent invocations,
        // cancel queue rows, flip statuses to cancelled. We don't touch open
        // PRs here (closing them is a Phase R polish — for now they sit as
        // orphan PRs that the operator can close manually; the queue + agent
        // are stopped which is what stops costs from accruing).
        try { await this.runner.CancelByTaskAsync(row.Id, ct); }
        catch { /* best-effort */ }

        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == row.Id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ct);
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == row.Id, ct);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Cancelled;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }
        row.Status = "cancelled";
    }

    private async Task DropNotStartedAsync(ControlDbContext control, ProductDbContext db, WorkstreamRow row, CancellationToken ct)
    {
        // Cancel queue rows defensively (a "planning" row shouldn't have any
        // but the cost of an empty Where is zero). We keep the WorkstreamRow
        // for audit but flip Status=cancelled — actually deleting it would
        // break any historical link from Card.workstream / PR.workstream
        // that already references the id.
        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == row.Id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ct);
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == row.Id, ct);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Cancelled;
            wi.State = "closed";
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }
        row.Status = "cancelled";
    }

    private static string StageSummary(PlanParser.CanonicalStage stage)
    {
        var stepCount = stage.Steps.Count;
        return stepCount == 0
            ? stage.Name
            : $"{stage.Name} ({stepCount} step{(stepCount == 1 ? string.Empty : "s")})";
    }

    private static string StageDescription(PlanParser.CanonicalStage stage)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Implements Stage ").Append(stage.PhaseNumber).Append('.').Append(stage.StageNumber)
            .Append(" of `implementation-plan.md` — ").AppendLine(stage.Name);
        sb.AppendLine();
        if (stage.Steps.Count > 0)
        {
            sb.AppendLine("## Implementation Steps");
            foreach (var step in stage.Steps)
            {
                sb.Append("- [").Append(step.Checked ? 'x' : ' ').Append("] ").AppendLine(step.Text);
            }
            sb.AppendLine();
        }
        if (stage.TestScenarios.Count > 0)
        {
            sb.AppendLine("## Test Scenarios");
            foreach (var sc in stage.TestScenarios)
            {
                sb.Append("- ").AppendLine(sc);
            }
        }
        return sb.ToString();
    }

    private static string WorkstreamIdFor(string storyId, string anchor)
    {
        var safeStory = StoryPaths.Safe(storyId);
        var anchorSlug = anchor.Replace('/', '-').Replace('@', '-');
        return $"ws-{safeStory}-{anchorSlug}".ToLowerInvariant();
    }

    private static async Task<string> BuildArtifactJsonAsync(
        ControlDbContext control,
        ProductDbContext db,
        string spawnId,
        IReadOnlyDictionary<string, PlanParser.CanonicalStage> planByAnchor,
        CancellationToken ct)
    {
        // Emit one entry per ACTIVE workstream in plan order so the artifact
        // mirrors what the implementation will execute against. Historic /
        // cancelled rows are excluded — they're audit, not work.
        var rows = await db.Workstreams
            .AsNoTracking()
            .Where(w => w.StorySpawnId == spawnId
                     && w.Status != WorkstreamStatuses.Historic
                     && w.Status != "cancelled"
                     && w.Status != "paused")
            .ToListAsync(ct);

        var orderedAnchors = planByAnchor.Keys.ToList();
        var anchorIndex = orderedAnchors
            .Select((a, i) => (a, i))
            .ToDictionary(x => x.a, x => x.i, StringComparer.Ordinal);

        var ordered = rows
            .OrderBy(r => r.PlanAnchor is null ? int.MaxValue
                : anchorIndex.TryGetValue(r.PlanAnchor, out var idx) ? idx : int.MaxValue)
            .ThenBy(r => r.Sort)
            .ToList();

        var artifact = ordered.Select(r => new
        {
            title = r.Name,
            description = r.Summary,
            anchor = r.PlanAnchor,
            contentHash = r.PlanContentHash,
            workstreamId = r.Id,
            branch = r.Branch,
            status = r.Status,
            // docs/forge/e2e.md Stage 1.2 — preserve qa-e2e on reconcile so
            // the regenerated workstreams.yaml still tags trailing-QA rows.
            // Impl rows serialise as null and the YAML serializer's
            // OmitNull handling drops the key so the operator's diff stays
            // quiet for the common case.
            category = r.Category == WorkstreamCategories.Impl ? null : r.Category,
        }).ToList();

        return JsonSerializer.Serialize(artifact, JsonOpts);
    }

    /// <summary>
    /// Phase 46 — convert the JSON artefact string to YAML for the
    /// worktree commit. Round-trips through <see cref="JsonElement"/>
    /// because the artifact shape is anonymous-typed at the call site;
    /// the serializer mirrors the camelCase keys verbatim.
    /// </summary>
    private static string ToYaml(string artifactJson)
    {
        if (string.IsNullOrWhiteSpace(artifactJson)) return string.Empty;
        using var doc = JsonDocument.Parse(artifactJson);
        // Yaml expects a plain object/list, not a JsonElement. Convert
        // by re-deserialising into a List<Dictionary<string,object?>> via
        // System.Text.Json so YamlDotNet can walk it.
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(artifactJson);
        if (rows is null) return string.Empty;
        return DecompositionYamlSerializer.Serialize(rows);
    }

    private enum RowState
    {
        NotStarted,
        InFlight,
        Shipped,
    }
}

/// <summary>Phase 42 — well-known status values (extends the implicit set used elsewhere).</summary>
public static class WorkstreamStatuses
{
    /// <summary>Frozen — shipped under a previous version of the plan; preserved for audit.</summary>
    public const string Historic = "historic";

    /// <summary>
    /// Phase 49 — operator-triage state after a non-recoverable failure
    /// (build-gate failure, no-progress abort, non-pass terminal
    /// verdict). Sticky: the orchestrator's claim loop skips
    /// <c>stalled</c> rows until <c>Restart</c> flips them back to
    /// <c>active</c>. Distinct from <c>blocked</c> (now reserved
    /// exclusively for "waiting on an upstream dependency"); a stalled
    /// row carries a <c>StallReason</c> string for the UI tooltip.
    /// </summary>
    public const string Stalled = "stalled";
}
