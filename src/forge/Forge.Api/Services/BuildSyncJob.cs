// -----------------------------------------------------------------------
// <copyright file="BuildSyncJob.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// forge_implementation.md Phase 27 — scheduled build sync sweep.
/// Mirrors <see cref="PrSyncJob"/> in shape: 20-min tick (jittered ±60s),
/// max-parallel = 8, runs only against PRs with <c>IsActive=1</c>. Each tick:
/// <list type="number">
/// <item>List builds for the PR's branch via <see cref="IBuildSyncRunner.ListBuildsForBranchAsync"/>.</item>
/// <item>Upsert into the <c>builds</c> table; flip <c>IsActive=0</c> on terminal transitions.</item>
/// <item>For builds that just transitioned to terminal-and-failed, fetch per-stage detail + log tail
///       and run <see cref="BuildFailureParser"/>. On failure-points found, enqueue a <c>kind='build-fix'</c>
///       row pinned to <c>WorkItemRow.AssignedAgent.agents[0]</c> (Q27.7).
///       On Pass-1 zero AND log-shaped artifact files exist, descend via Pass 1.5; otherwise route to <c>blocked</c>.</item>
/// </list>
/// </summary>
public sealed class BuildSyncJob : BackgroundService
{
    private static readonly TimeSpan StaleRunningThreshold = TimeSpan.FromMinutes(SyncConstants.RunningTimeoutMinutes);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IEnumerable<IBuildSyncRunner> runners;
    private readonly BuildArtifactExpander expander;
    private readonly BuildFailureParser parser;
    private readonly IHubContext<ForgeHub> hub;
    private readonly ForgeMetrics metrics;
    private readonly ILogger<BuildSyncJob> logger;

    public BuildSyncJob(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IBuildSyncRunner> runners,
        BuildArtifactExpander expander,
        BuildFailureParser parser,
        IHubContext<ForgeHub> hub,
        ForgeMetrics metrics,
        ILogger<BuildSyncJob> logger)
    {
        this.scopeFactory = scopeFactory;
        this.runners = runners;
        this.expander = expander;
        this.parser = parser;
        this.hub = hub;
        this.metrics = metrics;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "BuildSyncJob sweep failed");
            }

            // 20 min ± 60 s jitter (Q27.1).
            var jitter = RandomNumberGenerator.GetInt32(-60, 61);
            try { await Task.Delay(TimeSpan.FromSeconds(SyncConstants.BuildSyncIntervalSeconds + jitter), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One tick. Public so tests / the manual-trigger endpoint can fire it directly.</summary>
    public async Task RunSweepAsync(CancellationToken ct)
    {
        List<BuildSweepCandidate> candidates;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            candidates = await db.Prs.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.LastSyncAt ?? string.Empty)
                .Select(p => new BuildSweepCandidate(
                    p.Id, p.Provider ?? string.Empty, p.RepoId, p.Branch, p.WorkItemId))
                .ToListAsync(ct);

            // Phase 34 — per-repo build-sync cadence override. Per-PR eligibility
            // is computed from MAX(builds.LastSyncAt) for that PR; PRs with no
            // build rows yet are always eligible (first sweep wins).
            var prIds = candidates.Select(c => c.PrId).ToArray();
            var lastBuildSyncByPr = prIds.Length == 0
                ? new Dictionary<string, string?>()
                : await db.Builds
                    .AsNoTracking()
                    .Where(b => prIds.Contains(b.PrId!))
                    .GroupBy(b => b.PrId!)
                    .Select(g => new { PrId = g.Key, MaxAt = g.Max(x => x.LastSyncAt) })
                    .ToDictionaryAsync(x => x.PrId, x => x.MaxAt, ct);

            var repoIds = candidates
                .Where(c => !string.IsNullOrEmpty(c.RepoId))
                .Select(c => c.RepoId)
                .Distinct()
                .ToArray();
            var intervals = repoIds.Length == 0
                ? new Dictionary<string, int?>()
                : await db.Repos
                    .AsNoTracking()
                    .Where(r => repoIds.Contains(r.Id))
                    .ToDictionaryAsync(r => r.Id, r => r.BuildSyncIntervalSeconds, ct);

            var nowOff = DateTimeOffset.UtcNow;
            candidates = candidates
                .Where(c => IsBuildDueForSweep(c, lastBuildSyncByPr, intervals, nowOff))
                .ToList();
        }
        if (candidates.Count == 0) return;

        using var semaphore = new SemaphoreSlim(SyncConstants.BuildSyncMaxParallel);
        var tasks = candidates.Select(async c =>
        {
            await semaphore.WaitAsync(ct);
            try { await this.SweepOnePrAsync(c, ct); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);

        // Append a sync_runs row per repo, scoped 'build'.
        using var histScope = this.scopeFactory.CreateScope();
        var histDb = histScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var now = DateTimeOffset.UtcNow.ToString("o");
        foreach (var g in candidates.GroupBy(c => c.RepoId).Where(g => !string.IsNullOrEmpty(g.Key)))
        {
            histDb.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
            {
                Id = $"build-{Guid.NewGuid():N}"[..16],
                RepoId = g.Key!,
                Kind = "build",
                StartedAt = now,
                FinishedAt = now,
                ItemsAdded = 0,
                ItemsUpdated = g.Count(),
                ItemsConflicted = 0,
                CompletedSuccessfully = true,
            });
        }
        await histDb.SaveChangesAsync(ct);
    }

    /// <summary>Run a single-PR sweep on demand (operator override + tests).</summary>
    public async Task RunSweepForAsync(string prId, CancellationToken ct)
    {
        BuildSweepCandidate? candidate;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            candidate = await db.Prs.AsNoTracking()
                .Where(p => p.Id == prId)
                .Select(p => new BuildSweepCandidate(
                    p.Id, p.Provider ?? string.Empty, p.RepoId, p.Branch, p.WorkItemId))
                .FirstOrDefaultAsync(ct);
        }
        if (candidate is null) return;
        await this.SweepOnePrAsync(candidate, ct);
    }

    private async Task SweepOnePrAsync(BuildSweepCandidate c, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var runner = this.runners.FirstOrDefault(r =>
            string.Equals(r.Provider, c.Provider, StringComparison.OrdinalIgnoreCase) && r.IsConfigured);
        if (runner is null)
        {
            this.metrics.RecordBuildSync(c.Provider, "unconfigured", sw.Elapsed.TotalMilliseconds);
            return;
        }
        if (string.IsNullOrEmpty(c.Branch))
        {
            this.metrics.RecordBuildSync(c.Provider, "no-branch", sw.Elapsed.TotalMilliseconds);
            return;
        }

        RepoRow? repo;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == c.RepoId, ct);
        }
        if (repo is null)
        {
            this.metrics.RecordBuildSync(c.Provider, "no-repo", sw.Elapsed.TotalMilliseconds);
            return;
        }

        IReadOnlyList<BuildSyncSnapshot> snapshots;
        try
        {
            snapshots = await runner.ListBuildsForBranchAsync(repo, c.Branch!, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Build list failed for PR {PrId}", c.PrId);
            this.metrics.RecordBuildSync(c.Provider, "failed", sw.Elapsed.TotalMilliseconds);
            return;
        }

        // Upsert + detect transitions.
        var transitionedToFailed = new List<long>();
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            foreach (var s in snapshots)
            {
                var existing = await db.Builds.FirstOrDefaultAsync(
                    b => b.Provider == s.Provider && b.ProviderBuildId == s.ProviderBuildId, ct);
                var fromState = existing?.State ?? "(new)";
                var (canonState, isActive) = NormalizeState(s);

                if (existing is null)
                {
                    existing = new BuildRow
                    {
                        Provider = s.Provider,
                        ProviderBuildId = s.ProviderBuildId,
                        PrId = c.PrId,
                        RepoId = c.RepoId,
                        PipelineId = s.PipelineId,
                        PipelineName = s.PipelineName,
                        State = canonState,
                        Result = s.Result,
                        TriggeringCommit = s.TriggeringCommit,
                        Branch = s.Branch ?? c.Branch,
                        WebUrl = s.WebUrl,
                        StartedAt = s.StartedAt,
                        FinishedAt = s.FinishedAt,
                        IsActive = isActive,
                    };
                    db.Builds.Add(existing);
                    if (canonState == "failed" || canonState == "partially-succeeded")
                    {
                        transitionedToFailed.Add(0); // resolved after SaveChanges below.
                    }
                    await db.SaveChangesAsync(ct);
                    if (canonState == "failed" || canonState == "partially-succeeded")
                    {
                        transitionedToFailed[^1] = existing.Id;
                    }
                }
                else
                {
                    // Q27.13 — terminal is terminal, never re-activate.
                    if (existing.IsActive == false && isActive == true)
                    {
                        // ignore: a re-list of an already-terminal build never re-activates.
                    }
                    if (!string.Equals(existing.State, canonState, StringComparison.Ordinal))
                    {
                        existing.State = canonState;
                        existing.Result = s.Result;
                        existing.FinishedAt = s.FinishedAt ?? existing.FinishedAt;
                        existing.IsActive = isActive && existing.IsActive;
                        this.metrics.BuildStateTransition(fromState, canonState);
                        await this.hub.Clients.All.SendAsync(
                            "pr.changed",
                            new { kind = "build", prId = c.PrId, buildId = existing.Id, from = fromState, to = canonState }, ct);
                        if (canonState == "failed" || canonState == "partially-succeeded")
                        {
                            transitionedToFailed.Add(existing.Id);
                        }
                    }
                    else
                    {
                        existing.Result = s.Result;
                        existing.FinishedAt = s.FinishedAt ?? existing.FinishedAt;
                    }
                    existing.LastSyncAt = DateTimeOffset.UtcNow.ToString("o");
                    existing.LastSyncStatus = "idle";
                    existing.LastSyncError = null;
                    await db.SaveChangesAsync(ct);
                }
            }
        }

        // For each newly-failed build, fetch stage detail + parse + enqueue.
        foreach (var bid in transitionedToFailed.Distinct())
        {
            try
            {
                await this.HandleNewFailureAsync(bid, runner, repo, c, ct);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "HandleNewFailure failed for build {Id}", bid);
            }
        }

        this.metrics.RecordBuildSync(c.Provider, "success", sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Q27.4 / Q27.6 / Q27.7 — fetch per-stage detail, run the failure
    /// parser, and enqueue a <c>build-fix</c> queue row (or defer per
    /// Q27.12). Public so tests can drive a single failure end-to-end.
    /// </summary>
    public async Task HandleNewFailureAsync(
        long buildId, IBuildSyncRunner runner, RepoRow repo, BuildSweepCandidate c, CancellationToken ct)
    {
        // 1) Fetch per-stage detail.
        BuildRow? buildRow;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            buildRow = await db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == buildId, ct);
        }
        if (buildRow is null) return;

        IReadOnlyList<BuildStageSnapshot> stages;
        try
        {
            stages = await runner.FetchStageDetailAsync(repo, buildRow.ProviderBuildId, ct);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "FetchStageDetail failed for build {Id}", buildId);
            stages = Array.Empty<BuildStageSnapshot>();
        }

        // 2) Persist stages + log tails; collect ids for the parser.
        var parsedAny = false;
        var allLogTails = new List<(long StageId, string Content)>();
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            foreach (var s in stages)
            {
                var stage = await db.BuildStages.FirstOrDefaultAsync(
                    x => x.BuildId == buildId && x.ProviderStageRef == s.ProviderStageRef, ct);
                var tailHash = ComputeSha256(s.LogContent);
                if (stage is null)
                {
                    stage = new BuildStageRow
                    {
                        BuildId = buildId,
                        ProviderStageRef = s.ProviderStageRef,
                        StageName = s.StageName,
                        StageOrder = s.StageOrder,
                        State = s.State,
                        Result = s.Result,
                        StartedAt = s.StartedAt,
                        FinishedAt = s.FinishedAt,
                        LogRef = s.LogRef,
                        LogTailHash = tailHash,
                    };
                    db.BuildStages.Add(stage);
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    var hashChanged = stage.LogTailHash != tailHash;
                    stage.StageName = s.StageName;
                    stage.StageOrder = s.StageOrder;
                    stage.State = s.State;
                    stage.Result = s.Result;
                    stage.StartedAt = s.StartedAt;
                    stage.FinishedAt = s.FinishedAt;
                    stage.LogRef = s.LogRef;
                    stage.LogTailHash = tailHash;
                    await db.SaveChangesAsync(ct);
                    if (!hashChanged)
                    {
                        allLogTails.Add((stage.Id, s.LogContent));
                        continue;
                    }
                }

                // Persist log tail.
                var existingLog = await db.BuildStageLogs.FirstOrDefaultAsync(
                    l => l.StageId == stage.Id && l.LogIndex == 0, ct);
                if (existingLog is null)
                {
                    db.BuildStageLogs.Add(new BuildStageLogRow
                    {
                        StageId = stage.Id,
                        LogIndex = 0,
                        Content = s.LogContent,
                        SizeBytes = Encoding.UTF8.GetByteCount(s.LogContent),
                        TruncatedFromBytes = s.OriginalLogSizeBytes,
                        FetchedAt = DateTimeOffset.UtcNow.ToString("o"),
                    });
                }
                else
                {
                    existingLog.Content = s.LogContent;
                    existingLog.SizeBytes = Encoding.UTF8.GetByteCount(s.LogContent);
                    existingLog.TruncatedFromBytes = s.OriginalLogSizeBytes;
                    existingLog.FetchedAt = DateTimeOffset.UtcNow.ToString("o");
                }
                await db.SaveChangesAsync(ct);
                allLogTails.Add((stage.Id, s.LogContent));
            }

            // 3) Pass 1 over each stage tail.
            foreach (var (stageId, content) in allLogTails)
            {
                var pts = await this.parser.ParseAsync(stageId, content, Array.Empty<(string, string)>(), db, ct);
                if (pts.Count > 0) parsedAny = true;
            }
        }

        // 4) Pass 1.5 — descend into log-shaped artifacts.
        if (!parsedAny)
        {
            try
            {
                var arts = await runner.ListArtifactsAsync(repo, buildRow.ProviderBuildId, ct);
                foreach (var art in arts)
                {
                    var er = await this.expander.ExtractAsync(buildId, runner.Provider, art.Name, art.DownloadUrl, ct);
                    if (!er.Succeeded || string.IsNullOrEmpty(er.ExtractedPath)) continue;

                    var logFiles = new List<(string Path, string Content)>();
                    foreach (var rel in er.IndexedFiles)
                    {
                        var full = Path.Combine(er.ExtractedPath!, rel);
                        if (!File.Exists(full)) continue;
                        var ext = Path.GetExtension(rel).ToLowerInvariant();
                        if (ext is not (".log" or ".txt" or ".trx" or ".xml")) continue;
                        try
                        {
                            var content = await File.ReadAllTextAsync(full, ct);
                            logFiles.Add((rel, content));
                        }
                        catch { /* ignore unreadable file */ }
                    }
                    if (logFiles.Count == 0) continue;

                    using var scope = this.scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                    foreach (var (stageId, _) in allLogTails)
                    {
                        var pts = await this.parser.ParseAsync(stageId, string.Empty, logFiles, db, ct);
                        if (pts.Count > 0) parsedAny = true;
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "Pass 1.5 artifact descent failed for build {Id}", buildId);
            }
        }

        // 5) Enqueue a build-fix row OR route to blocked.
        await this.RouteToQueueAsync(buildId, c.PrId, c.WorkItemId, runner.Provider, parsedAny, ct);
    }

    private async Task RouteToQueueAsync(
        long buildId, string prId, string? workItemId, string provider, bool parsed, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId))
        {
            this.metrics.BuildWorkItemMissingAdd();
            return;
        }

        if (!parsed)
        {
            this.metrics.BuildUnparseableAdd(provider);
            await this.hub.Clients.All.SendAsync("task.build-fix-blocked",
                new { workItemId, prId, buildId, reason = "build-fix.unparseable_failure" }, ct);
            return;
        }

        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is null)
        {
            this.metrics.BuildWorkItemMissingAdd();
            return;
        }

        // Q27.7 — pin to original generator (AssignedAgent.agents[0]).
        var preferredAgentId = ResolvePreferredAgentId(wi.AssignedAgentJson);
        if (string.IsNullOrEmpty(preferredAgentId))
        {
            this.metrics.BuildNoOriginalAgentAdd();
            // Continue — the queue service falls through to the lead.
        }

        var crewId = ResolveCrewId(wi.AssignedAgentJson);
        if (string.IsNullOrEmpty(crewId))
        {
            this.metrics.BuildNoOriginalAgentAdd();
            return;
        }

        // Q27.12 — defer if any open comment-response/coding row exists.
        var openStates = TaskQueueStates.OpenStates.ToList();
        var hasOtherOpen = await db.TaskQueue.AnyAsync(r =>
            r.WorkItemId == workItemId
            && (r.Kind == TaskQueueKinds.CommentResponse || r.Kind == TaskQueueKinds.Coding || r.Kind == TaskQueueKinds.Work)
            && openStates.Contains(r.State), ct);
        if (hasOtherOpen)
        {
            // Idempotent insert into build_fix_pending.
            var existing = await db.BuildFixPending.FirstOrDefaultAsync(p => p.WorkItemId == workItemId, ct);
            if (existing is null)
            {
                db.BuildFixPending.Add(new BuildFixPendingRow
                {
                    WorkItemId = workItemId,
                    BuildId = buildId,
                    EnqueuedAt = DateTimeOffset.UtcNow.ToString("o"),
                    ReasonJson = $"{{\"deferred\":\"comment-response-or-coding-open\",\"buildId\":{buildId}}}",
                });
                await db.SaveChangesAsync(ct);
            }
            this.metrics.BuildFixDeferredAdd();
            return;
        }

        // Q27.7 — partial unique index in Phase 17 short-circuits if a
        // build-fix is already open for this work item.
        var hasOpenBuildFix = await db.TaskQueue.AnyAsync(r =>
            r.WorkItemId == workItemId
            && r.Kind == TaskQueueKinds.BuildFix
            && openStates.Contains(r.State), ct);
        if (hasOpenBuildFix) return;

        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        var row = await queue.EnqueueAsync(
            workItemId,
            "crew",
            crewId!,
            ct,
            kind: TaskQueueKinds.BuildFix,
            preferredAgentId: preferredAgentId);

        await this.hub.Clients.All.SendAsync(
            "task.build-fix-enqueued",
            new { workItemId, prId, buildId, queueId = row.Id, preferredAgentId }, ct);
    }

    private static (string State, bool IsActive) NormalizeState(BuildSyncSnapshot s)
    {
        var state = s.State;
        var isActive = state is "queued" or "running" or "not-started";
        return (state, isActive);
    }

    private static string ComputeSha256(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string? ResolveCrewId(string? assignedAgentJson)
    {
        if (string.IsNullOrEmpty(assignedAgentJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assignedAgentJson);
            if (doc.RootElement.TryGetProperty("crewId", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                return c.GetString();
            if (doc.RootElement.TryGetProperty("CrewId", out var c2) && c2.ValueKind == System.Text.Json.JsonValueKind.String)
                return c2.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? ResolvePreferredAgentId(string? assignedAgentJson)
    {
        if (string.IsNullOrEmpty(assignedAgentJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assignedAgentJson);
            if (TryGetAgentsFirst(doc.RootElement, "agents", out var lower)) return lower;
            if (TryGetAgentsFirst(doc.RootElement, "Agents", out var upper)) return upper;
        }
        catch { /* ignore */ }
        return null;

        static bool TryGetAgentsFirst(System.Text.Json.JsonElement root, string name, out string? value)
        {
            value = null;
            if (!root.TryGetProperty(name, out var arr)) return false;
            if (arr.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    value = el.GetString();
                    return !string.IsNullOrEmpty(value);
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Phase 34 — per-PR cadence gate for the build sweep. Effective interval
    /// is the parent repo's <c>BuildSyncIntervalSeconds</c> override, or the
    /// global <see cref="SyncConstants.BuildSyncIntervalSeconds"/>. PRs with
    /// no build rows yet always pass (first sweep wins).
    /// </summary>
    internal static bool IsBuildDueForSweep(
        BuildSweepCandidate c,
        IReadOnlyDictionary<string, string?> lastBuildSyncByPr,
        IReadOnlyDictionary<string, int?> intervals,
        DateTimeOffset now)
    {
        if (!lastBuildSyncByPr.TryGetValue(c.PrId, out var lastIso) || string.IsNullOrEmpty(lastIso))
        {
            return true;
        }
        if (!DateTimeOffset.TryParse(lastIso, out var lastTick)) return true;

        var override_ = !string.IsNullOrEmpty(c.RepoId)
                        && intervals.TryGetValue(c.RepoId, out var v)
            ? v
            : null;
        var effectiveInterval = override_ ?? SyncConstants.BuildSyncIntervalSeconds;
        return (now - lastTick).TotalSeconds >= effectiveInterval;
    }

    /// <summary>One PR row condensed to the columns the sweep needs.</summary>
    public sealed record BuildSweepCandidate(
        string PrId,
        string Provider,
        string RepoId,
        string? Branch,
        string? WorkItemId);
}
