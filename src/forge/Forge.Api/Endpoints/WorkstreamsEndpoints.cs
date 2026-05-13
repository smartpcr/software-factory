// -----------------------------------------------------------------------
// <copyright file="WorkstreamsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Agents;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Forge.Api.Services.Git;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Phase 3.6 — workstream CRUD + sync endpoints.
/// Writes go through <see cref="ForgeUser.RequireWriter"/>; the git-worktree
/// service is best-effort (no-ops in environments without a checkout).
/// </summary>
public static class WorkstreamsEndpoints
{
    public static WebApplication MapWorkstreamsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/workstreams").WithTags("Workstreams");

        grp.MapGet("/", GetAll).WithName("GetWorkstreams");
        grp.MapPost("/", Create).WithName("CreateWorkstream");
        grp.MapPost("/{id}/sync", Sync).WithName("SyncWorkstream");
        grp.MapDelete("/{id}", Delete).WithName("DeleteWorkstream");
        grp.MapPost("/{id}/pr", OpenPr).WithName("OpenWorkstreamPr");
        grp.MapPost("/{id}/restart", Restart).WithName("RestartWorkstream");
        grp.MapPost("/{id}/cancel", Cancel).WithName("CancelWorkstream");
        grp.MapGet("/{id}/memory", GetMemory).WithName("GetWorkstreamMemory");
        grp.MapGet("/{id}/iter-notes", GetIterNotes).WithName("GetWorkstreamIterNotes");
        grp.MapGet("/{id}/iter-kpis", GetIterKpis).WithName("GetWorkstreamIterKpis");

        return app;
    }

    // GET /api/workstreams/{id}/iter-kpis — Insight I.3
    // (docs/forge/workstream-insights.md). Joins deliverables +
    // agent_usage + task_transcript_event so the Conversation tab's
    // KPI strip can render per-iter score / token / cost / files /
    // duration chips and the matching sparklines. One round-trip;
    // the client doesn't have to re-derive any of these client-side.
    //
    // <para>Iter window semantics:</para>
    // <list type="number">
    //   <item>Anchor = deliverables.CreatedAt ordered ASC by Iter.</item>
    //   <item>For iter N: usage rows with StartedAt &gt; iter[N-1].At
    //   AND StartedAt &lt;= iter[N].At; iter 1's lower bound is the
    //   FIRST agent_usage.StartedAt for this work item (or
    //   DateTimeOffset.MinValue if none).</item>
    //   <item>Duration = scored.At − started.At from the transcript
    //   for matching iter number; null when either marker is missing.</item>
    //   <item>filesChanged = count of distinct file paths in
    //   `task.worktree-tick` events whose At falls in the iter window.
    //   The tick payload carries `branchDiff.{added,modified,deleted}`
    //   (committed diff from the fork point) and `paths.{added,
    //   modified,deleted}` (uncommitted dirty files); we union both so
    //   the count is non-zero whether the agent has committed yet or
    //   not. Empty when the workstream has no worktree subscriber
    //   (plan-doc spawns, or a coding step whose worktree never
    //   resolved — see CrewQueueWorker.cs:1042-1048).</item>
    // </list>
    private static async Task<IResult> GetIterKpis(
        string id,
        ForgeDbContext db,
        CancellationToken ct)
    {
        // EF/SQLite refuses server-side ORDER BY on DateTimeOffset
        // (NotSupportedException), so we materialise then sort
        // client-side. Memory'd in [[feedback_sqlite_no_order_by_datetimeoffset]].
        var deliverables = (await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct))
            .OrderBy(d => d.Iter)
            .ThenBy(d => d.CreatedAt)
            .ToList();
        if (deliverables.Count == 0)
        {
            return Results.Ok(new { workItemId = id, iters = Array.Empty<object>() });
        }

        var usageRows = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == id)
            .Select(u => new { u.StartedAt, u.InputTokens, u.OutputTokens, u.CostUsd })
            .ToListAsync(ct);

        var transcript = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id
                && (e.Kind == "task.iteration.started"
                    || e.Kind == "task.iteration.scored"
                    || e.Kind == "task.worktree-tick"))
            .Select(e => new { e.Kind, e.At, e.PayloadJson })
            .ToListAsync(ct);

        // Index transcript by kind for cheap per-iter lookups.
        var startedByIter = new Dictionary<int, DateTimeOffset>();
        var scoredByIter = new Dictionary<int, DateTimeOffset>();
        foreach (var e in transcript)
        {
            if (e.Kind == "task.iteration.started" || e.Kind == "task.iteration.scored")
            {
                int? iter = TryExtractIter(e.PayloadJson);
                if (iter is null) continue;
                if (e.Kind == "task.iteration.started") startedByIter[iter.Value] = e.At;
                else scoredByIter[iter.Value] = e.At;
            }
        }
        var worktreeChanged = transcript
            .Where(e => e.Kind == "task.worktree-tick")
            .Select(e => (e.At, paths: ExtractChangedPaths(e.PayloadJson)))
            .ToList();

        var sortedUsage = usageRows.OrderBy(u => u.StartedAt).ToList();
        DateTimeOffset? earliestUsage = sortedUsage.Count > 0 ? sortedUsage[0].StartedAt : null;

        var iters = new List<object>(deliverables.Count);
        DateTimeOffset prevAt = earliestUsage ?? DateTimeOffset.MinValue;
        foreach (var d in deliverables)
        {
            var windowStart = prevAt;
            var windowEnd = d.CreatedAt;

            long inTok = 0, outTok = 0;
            decimal cost = 0m;
            foreach (var u in sortedUsage)
            {
                if (u.StartedAt > windowStart && u.StartedAt <= windowEnd)
                {
                    inTok += u.InputTokens;
                    outTok += u.OutputTokens;
                    cost += u.CostUsd;
                }
            }

            int filesChanged = 0;
            var pathsThisIter = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (at, paths) in worktreeChanged)
            {
                if (at > windowStart && at <= windowEnd)
                {
                    foreach (var p in paths) pathsThisIter.Add(p);
                }
            }
            filesChanged = pathsThisIter.Count;

            long? durationMs = null;
            if (startedByIter.TryGetValue(d.Iter, out var s)
                && scoredByIter.TryGetValue(d.Iter, out var e2))
            {
                durationMs = (long)(e2 - s).TotalMilliseconds;
                if (durationMs < 0) durationMs = null;
            }

            iters.Add(new
            {
                iter = d.Iter,
                score = d.Score,
                verdict = d.Verdict,
                inputTokens = inTok,
                outputTokens = outTok,
                costUsd = cost,
                durationMs,
                filesChanged,
                createdAt = d.CreatedAt,
            });
            prevAt = windowEnd;
        }

        return Results.Ok(new { workItemId = id, iters });
    }

    private static int? TryExtractIter(string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("iter", out var v)) return null;
            if (v.ValueKind != System.Text.Json.JsonValueKind.Number) return null;
            return v.GetInt32();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractChangedPaths(string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return Array.Empty<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var list = new List<string>();

            // task.worktree-tick shape: `branchDiff` (committed diff vs
            // fork point) + `paths` (uncommitted dirty), each split into
            // {added, modified, deleted}. Union both so the count is
            // non-zero whether the agent has committed yet or not.
            AppendCategorizedPaths(root, "branchDiff", list);
            if (root.TryGetProperty("paths", out var pathsEl)
                && pathsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                AppendCategorizedPaths(root, "paths", list);
            }
            // Legacy flat shapes used by older emitters / tests:
            // `paths: ["a", "b"]` or `path: "a"`.
            else if (pathsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in pathsEl.EnumerateArray())
                {
                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var p = el.GetString();
                        if (!string.IsNullOrEmpty(p)) list.Add(p);
                    }
                }
            }
            else if (root.TryGetProperty("path", out var single)
                && single.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var one = single.GetString();
                if (!string.IsNullOrEmpty(one)) list.Add(one);
            }
            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AppendCategorizedPaths(
        System.Text.Json.JsonElement root, string propertyName, List<string> sink)
    {
        if (!root.TryGetProperty(propertyName, out var grp)
            || grp.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var category in new[] { "added", "modified", "deleted" })
        {
            if (!grp.TryGetProperty(category, out var arr)
                || arr.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var p = el.GetString();
                    if (!string.IsNullOrEmpty(p)) sink.Add(p);
                }
            }
        }
    }

    // GET /api/workstreams/{id}/iter-notes — Insight I.2
    // (docs/forge/workstream-insights.md). Read-only enumeration of the
    // agent's per-iter working notes archive
    // (`.forge/notes/iter-{N}.md`, Phase A.2). Used by SummaryView to
    // render the "Working notes" section alongside generator/evaluator
    // markdown so the operator sees what the agent itself reflected on
    // each iter. Returns empty `iters` when the workstream has no
    // worktree on disk (yet-to-claim or post-merge cleanup).
    private static async Task<IResult> GetIterNotes(
        string id,
        ForgeDbContext db,
        CancellationToken ct)
    {
        const int PerIterCap = 16_000;
        var resolved = await WorktreePathResolver.ResolveForWorkstreamAsync(db, id, ct);
        var iters = new List<object>();

        if (!string.IsNullOrEmpty(resolved.WorktreePath))
        {
            var notesDir = Path.Combine(resolved.WorktreePath, ".forge", "notes");
            if (Directory.Exists(notesDir))
            {
                var parsed = new List<(int Iter, string Path)>();
                foreach (var file in Directory.GetFiles(notesDir, "iter-*.md"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var dash = name.IndexOf('-');
                    if (dash >= 0
                        && int.TryParse(name[(dash + 1)..], out var n)
                        && n > 0)
                    {
                        parsed.Add((n, file));
                    }
                }
                parsed.Sort((a, b) => a.Iter.CompareTo(b.Iter));

                foreach (var (iter, path) in parsed)
                {
                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(path, ct);
                    }
                    catch
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    var truncated = false;
                    if (content.Length > PerIterCap)
                    {
                        content = content[..PerIterCap];
                        truncated = true;
                    }
                    iters.Add(new
                    {
                        iter,
                        content,
                        truncated,
                        lengthBytes = content.Length,
                    });
                }
            }
        }

        return Results.Ok(new
        {
            workItemId = id,
            iters,
        });
    }

    // GET /api/workstreams/{id}/memory — Phase 55 / Phase G. Read-only
    // inspection of the (workItemId, agentId, role) memory rows for a
    // workstream so the Conversation tab can show a "Memory" chip and
    // an operator can audit session-id / turn-count / approx-token
    // state from a curl. No auth gate — this is a debug surface, not
    // a write path.
    private static async Task<IResult> GetMemory(
        string id,
        ForgeDbContext db,
        Forge.Api.Agents.CopilotSessionPool copilotPool,
        Forge.Api.Services.MemoryCompactor compactor,
        CancellationToken ct)
    {
        var rows = await db.AgentMemory.AsNoTracking()
            .Where(m => m.WorkItemId == id)
            .ToListAsync(ct);

        // Insight I.4 — resolve each role's model + the relevant crew's
        // compaction-threshold override so the UI can render a
        // green/amber/red bar of "tokens used vs compactor trigger".
        // Look up workstream → crew (for threshold) and agent rows
        // (for model). We avoid the full ResolveAgentRunConfigAsync
        // path here because we only need the model id, not the soul or
        // worktree wiring.
        var ws = await db.Workstreams.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, ct);
        CrewRow? wsCrew = null;
        CrewRow? evalCrew = null;
        if (ws is not null)
        {
            // Workstream rows don't carry the crew id directly; we walk
            // the most recent deliverable row to find which crew owned
            // the iter, then resolve its paired evaluator (if any) for
            // the eval-side compaction threshold.
            var lastDel = (await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == id)
                .ToListAsync(ct))
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefault();
            if (lastDel is not null && !string.IsNullOrEmpty(lastDel.CrewId))
            {
                wsCrew = await db.Crews.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == lastDel.CrewId, ct);
                if (wsCrew is not null && !string.IsNullOrEmpty(wsCrew.PairedWithCrewId))
                {
                    evalCrew = await db.Crews.AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == wsCrew.PairedWithCrewId, ct);
                }
            }
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            int? turnCount = null;
            if (!string.IsNullOrEmpty(row.MessagesJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(row.MessagesJson);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        turnCount = doc.RootElement.GetArrayLength();
                    }
                }
                catch
                {
                    // corrupt payload — leave turnCount null
                }
            }

            // Resolve the model + crew override for THIS role so the
            // threshold reflects what the compactor will actually use.
            var agentRow = await db.Agents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == row.AgentId, ct);
            var roleCrew = string.Equals(row.Role, "evaluator", StringComparison.OrdinalIgnoreCase)
                ? (evalCrew ?? wsCrew)
                : wsCrew;
            string? modelId = string.Equals(row.Role, "evaluator", StringComparison.OrdinalIgnoreCase)
                ? (agentRow?.EvaluatorModel ?? roleCrew?.EvaluatorModel ?? roleCrew?.Model)
                : (agentRow?.GeneratorModel ?? roleCrew?.Model);
            var crewOverride = roleCrew?.CompactionTokenThreshold;
            var detail = !string.IsNullOrEmpty(modelId)
                ? compactor.ComputeThresholdDetail(modelId, crewOverride)
                : new Forge.Api.Services.MemoryCompactor.ThresholdDetail(
                    EffectiveThreshold: crewOverride ?? Forge.Api.Services.MemoryCompactor.DefaultThreshold,
                    ContextWindowTokens: null,
                    ContextWindowRatio: compactor.ContextWindowRatio,
                    DefaultThreshold: Forge.Api.Services.MemoryCompactor.DefaultThreshold,
                    CrewOverride: crewOverride,
                    Source: crewOverride.HasValue
                        ? Forge.Api.Services.MemoryCompactor.ThresholdSource.CrewOverride
                        : Forge.Api.Services.MemoryCompactor.ThresholdSource.GlobalDefault);

            result[row.Role] = new
            {
                agentId = row.AgentId,
                backend = row.Backend,
                invokeMode = row.InvokeMode,
                model = modelId,
                // Truncate session id so a copy/paste from the UI
                // doesn't accidentally include the full Claude Code
                // session identifier in logs.
                sessionId = string.IsNullOrEmpty(row.SessionId)
                    ? null
                    : (row.SessionId.Length <= 12 ? row.SessionId : row.SessionId[..12] + "…"),
                hasMessagesHistory = !string.IsNullOrEmpty(row.MessagesJson),
                turnCount,
                approxInputTokens = row.ApproxInputTokens,
                effectiveThreshold = detail.EffectiveThreshold,
                contextWindowTokens = detail.ContextWindowTokens,
                contextWindowRatio = detail.ContextWindowRatio,
                defaultThreshold = detail.DefaultThreshold,
                crewOverride = detail.CrewOverride,
                thresholdSource = detail.Source.ToString(),
                lastIter = row.LastIter,
                createdAt = row.CreatedAt,
                updatedAt = row.UpdatedAt,
            };
        }

        return Results.Ok(new
        {
            workItemId = id,
            roles = result,
        });
    }

    // POST /api/workstreams/{id}/pr — Phase 6.4. Opens a PR via the named
    // provider with a templated body (workstream summary + linked card ids).
    public sealed record OpenPrRequest(string Title, string Body, string Provider = "github", bool Draft = false);

    private static async Task<IResult> OpenPr(
        HttpContext ctx,
        string id,
        [FromBody] OpenPrRequest req,
        ForgeDbContext db,
        IEnumerable<IForgeProvider> providers,
        Forge.Api.Auth.IUserContext user,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (row is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        var provider = providers.FirstOrDefault(p => p.Kind == req.Provider);
        if (provider is null) return Results.Problem(title: $"Unknown provider {req.Provider}", statusCode: 400);
        if (!provider.IsConfigured) return Results.Problem(title: $"Provider {req.Provider} not configured", statusCode: 400);

        var linkedCards = CardMapper.Deserialize<List<string>>(row.CardsJson) ?? new();
        var body = string.IsNullOrWhiteSpace(req.Body)
            ? $"{row.Summary}\n\nLinked: {string.Join(", ", linkedCards)}"
            : req.Body;

        try
        {
            var pr = await provider.CreatePullRequestAsync(
                new NewPullRequestRequest(row.Branch, row.Base, req.Title, body, req.Draft),
                user.ForProvider(provider.Kind));

            // Stash the new PR id on the workstream row.
            var prs = CardMapper.Deserialize<List<string>>(row.PrsJson) ?? new();
            if (!prs.Contains(pr.Id)) { prs.Add(pr.Id); row.PrsJson = CardMapper.Serialize(prs); }
            await db.SaveChangesAsync();

            await hub.Clients.All.SendAsync("pr.changed", new { kind = "opened", source = req.Provider, pr });
            return Results.Created($"/api/prs/{pr.Id}", pr);
        }
        catch (Exception ex)
        {
            return Results.Problem(title: $"Provider {req.Provider} create-PR failed", detail: ex.Message, statusCode: 502);
        }
    }

    // GET /api/workstreams
    private static async Task<IResult> GetAll(HttpContext ctx, ForgeDbContext db, IGitWorktreeService git)
    {
        var scope = await ProductScope.ResolveAsync(ctx, db);
        var q = db.Workstreams.AsNoTracking().AsQueryable();
        if (!scope.IsAll)
        {
            var ids = scope.RepoIds!;
            q = q.Where(w => w.RepoId != null && ids.Contains(w.RepoId));
        }
        var rows = await q.OrderBy(w => w.Sort).ToListAsync();
        var list = rows.Select(WorkstreamMapper.ToContract).ToList();
        // Best-effort live ahead/behind/conflicts when a git checkout is configured.
        // We do not block the response on git errors.
        try
        {
            foreach (var ws in list)
            {
                if (string.IsNullOrEmpty(ws.Branch) || string.IsNullOrEmpty(ws.Base)) continue;
                var ab = await git.AheadBehindAsync(ws.Base, ws.Branch);
                var conflicts = await git.ConflictPathsAsync(ws.Base, ws.Branch);
                if (ab.Ahead != 0 || ab.Behind != 0 || conflicts.Count > 0)
                {
                    var idx = list.IndexOf(ws);
                    list[idx] = ws with
                    {
                        Ahead = ab.Ahead,
                        Behind = ab.Behind,
                        Conflicts = conflicts.Count,
                    };
                }
            }
        }
        catch
        {
            // Ignore — cached values stay.
        }
        return Results.Ok(list);
    }

    public sealed record CreateWorkstreamRequest(
        string Id,
        string Name,
        string Branch,
        string Base,
        string Epic,
        string? WorktreePath = null,
        /// <summary>Phase 8.10 — repo this workstream belongs to. Auto-clone fires when the repo's cloneRoot is empty.</summary>
        string? RepoId = null);

    // POST /api/workstreams
    private static async Task<IResult> Create(
        HttpContext ctx,
        [FromBody] CreateWorkstreamRequest req,
        ForgeDbContext db,
        IGitWorktreeService git,
        IRepoCloneService cloneSvc,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Branch))
        {
            return Results.Problem(title: "id and branch are required", statusCode: 400);
        }
        if (await db.Workstreams.AnyAsync(w => w.Id == req.Id))
        {
            return Results.Problem(title: $"Workstream {req.Id} already exists", statusCode: 409);
        }

        // Phase 8.10 — if the workstream targets a repo whose cloneRoot is
        // empty, run git clone first (silent / Q6). On hard failure we abort
        // the workstream creation rather than leaving a dangling row.
        var repoId = string.IsNullOrWhiteSpace(req.RepoId) ? ForgeSeed.DefaultRepoId : req.RepoId;
        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId);
        if (repo is not null)
        {
            var clone = await cloneSvc.EnsureClonedAsync(repo);
            if (clone.Outcome == RepoCloneOutcome.Mismatched)
            {
                return Results.Problem(
                    title: "Repo cloneRoot mismatch",
                    detail: clone.Message,
                    statusCode: 409);
            }
            if (clone.Outcome == RepoCloneOutcome.Failed)
            {
                return Results.Problem(
                    title: "Auto-clone failed",
                    detail: clone.Message,
                    statusCode: 502);
            }
        }

        var maxSort = await db.Workstreams.Select(w => (int?)w.Sort).MaxAsync() ?? -1;
        var worktree = req.WorktreePath is null
            ? new[] { "(not yet created)" }
            : new[] { Path.GetDirectoryName(req.WorktreePath) ?? string.Empty, Path.GetFileName(req.WorktreePath) };

        var row = new WorkstreamRow
        {
            Id = req.Id,
            Sort = maxSort + 1,
            RepoId = repo?.Id,
            Name = req.Name,
            Branch = req.Branch,
            Base = req.Base,
            Epic = req.Epic,
            Status = "planning",
            WorktreeJson = CardMapper.Serialize(worktree),
            Created = 0,
            LastActive = 0,
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Commits = 0,
            Ahead = 0,
            Behind = 0,
            Conflicts = 0,
            Summary = $"Spawned workstream {req.Name}.",
            BarStart = 0,
            BarEnd = 5,
            Planned = true,
        };
        db.Workstreams.Add(row);
        await db.SaveChangesAsync();

        if (req.WorktreePath is not null)
        {
            try
            {
                await git.CreateAsync(req.WorktreePath, req.Branch, req.Base);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(title: "Worktree path rejected", detail: ex.Message, statusCode: 403);
            }
            catch (Exception ex)
            {
                // Persistence already succeeded — surface as warning, not failure.
                Console.Error.WriteLine($"forge.git.worktree.create failed: {ex.Message}");
            }
        }

        var ws = WorkstreamMapper.ToContract(row);
        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "created", workstream = ws });
        return Results.Created($"/api/workstreams/{row.Id}", ws);
    }

    // POST /api/workstreams/{id}/sync — refresh ahead/behind/conflicts.
    private static async Task<IResult> Sync(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IGitWorktreeService git,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (row is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        try
        {
            var ab = await git.AheadBehindAsync(row.Base, row.Branch);
            var conflicts = await git.ConflictPathsAsync(row.Base, row.Branch);
            row.Ahead = ab.Ahead;
            row.Behind = ab.Behind;
            row.Conflicts = conflicts.Count;
            await db.SaveChangesAsync();
        }
        catch
        {
            // Leave cached values; the UI surfaces 200 with whatever we have.
        }

        var ws = WorkstreamMapper.ToContract(row);
        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "synced", workstream = ws });
        return Results.Ok(ws);
    }

    // DELETE /api/workstreams/{id}?force=true
    private static async Task<IResult> Delete(
        HttpContext ctx,
        string id,
        bool? force,
        ForgeDbContext db,
        IGitWorktreeService git,
        IConfiguration config,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (row is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        // Safety: if there are open PRs and force != true, ask the caller to confirm.
        var prs = CardMapper.Deserialize<List<string>>(row.PrsJson) ?? new();
        if (prs.Count > 0 && force != true)
        {
            return Results.Json(new
            {
                requireConfirm = true,
                title = "Delete workstream with open PRs?",
                detail = $"Workstream {id} still has {prs.Count} PR(s). Re-call with ?force=true to delete anyway.",
            }, statusCode: 422);
        }

        var report = await CascadeDeleteWorkstreamAsync(db, git, config, row, ct: ctx.RequestAborted);

        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "deleted", id });
        if (!string.IsNullOrEmpty(row.StorySpawnId))
        {
            await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "workstream-deleted", spawnId = row.StorySpawnId, workstreamId = id });
        }
        return Results.Ok(report);
    }

    /// <summary>
    /// Cascading delete for a workstream and every artifact it produced.
    /// Removes the rows referenced by <c>workstream.Id</c> across the
    /// task-execution surface (deliverables, transcripts, queue, links,
    /// dep edges, agent assignments, board card), the matching
    /// <see cref="WorkItemRow"/>, the on-disk gen↔eval workspaces, the git
    /// worktree mount, and the local git branch. Returns a structured
    /// report so the caller can surface "X rows / Y files removed" in the
    /// UI without having to count again.
    ///
    /// <para>What is intentionally KEPT: <see cref="AgentUsageRow"/> (cost
    /// audit), closed/merged <see cref="PrRow"/>s (history), and the
    /// spawn's <c>DecompositionJson</c> (historical record of what the
    /// architect produced — UI sources current state from the Workstreams
    /// table, so a stale entry there isn't load-bearing).</para>
    ///
    /// <para>Phase 50 — when <paramref name="deleteRemoteBranch"/> is
    /// <c>true</c>, also runs <c>git push origin --delete {ws.Branch}</c>
    /// after the local branch has been removed. Used by the spawn-delete
    /// endpoint where a "restart from scratch" must wipe the remote
    /// counterpart so the operator can re-spawn the same story without
    /// resurrecting the previous attempt's PR branch.</para>
    /// </summary>
    internal static async Task<object> CascadeDeleteWorkstreamAsync(
        ForgeDbContext db,
        IGitWorktreeService git,
        IConfiguration config,
        WorkstreamRow row,
        CancellationToken ct,
        bool deleteRemoteBranch = false)
    {
        var id = row.Id;
        var counts = new Dictionary<string, int>();

        // ── 1. Task-execution surface (deliverables, transcripts, queue) ──
        var deliverables = await db.Deliverables.Where(d => d.WorkItemId == id).ToListAsync(ct);
        db.Deliverables.RemoveRange(deliverables);
        counts["deliverables"] = deliverables.Count;

        var deliverableQuestions = await db.DeliverableQuestions.Where(q => q.WorkItemId == id).ToListAsync(ct);
        db.DeliverableQuestions.RemoveRange(deliverableQuestions);
        counts["deliverableQuestions"] = deliverableQuestions.Count;

        var transcript = await db.TaskTranscriptEvents.Where(e => e.WorkItemId == id).ToListAsync(ct);
        db.TaskTranscriptEvents.RemoveRange(transcript);
        counts["transcriptEvents"] = transcript.Count;

        var queueRows = await db.TaskQueue.Where(q => q.WorkItemId == id).ToListAsync(ct);
        var queueIds = queueRows.Select(q => q.Id).ToList();
        var queueHistory = await db.TaskQueueHistory.Where(h => queueIds.Contains(h.QueueId)).ToListAsync(ct);
        db.TaskQueueHistory.RemoveRange(queueHistory);
        db.TaskQueue.RemoveRange(queueRows);
        counts["queueRows"] = queueRows.Count;
        counts["queueHistory"] = queueHistory.Count;

        // ── 2. Cross-table edges that reference this workstream ──────────
        var depEdges = await db.WorkstreamDeps
            .Where(d => d.WorkstreamId == id || d.DependsOnId == id)
            .ToListAsync(ct);
        db.WorkstreamDeps.RemoveRange(depEdges);
        counts["depEdges"] = depEdges.Count;

        var workItemLinks = await db.WorkItemLinks
            .Where(l => l.FromId == id || l.ToId == id)
            .ToListAsync(ct);
        db.WorkItemLinks.RemoveRange(workItemLinks);
        counts["workItemLinks"] = workItemLinks.Count;

        var agentAssignments = await db.AgentAssignments.Where(a => a.WorkItemId == id).ToListAsync(ct);
        db.AgentAssignments.RemoveRange(agentAssignments);
        counts["agentAssignments"] = agentAssignments.Count;

        // Phase 50 follow-up (2026-05-12) — cost ledger rows. TaskId on
        // agent_usage equals the work-item id (DeliverableLoop emits
        // each gen/eval invocation with TaskId = workItemId). Without
        // this purge, deleting a workstream and re-decomposing into
        // another workstream with the same phase/stage slug (very
        // common — operator iterates on a plan) inherits the old
        // cost ledger and the doc / workstream cards show stale dollar
        // totals (operator-reported: re-spawned story showed
        // ~$1300-$2000 per plan-doc card before any LLM had run).
        var agentUsage = await db.AgentUsage.Where(u => u.TaskId == id).ToListAsync(ct);
        db.AgentUsage.RemoveRange(agentUsage);
        counts["agentUsage"] = agentUsage.Count;

        // Phase 52 follow-up (2026-05-13) — pair-attempt audit rows
        // (one per (workItem, pair) attempt; Phase 52 schema). Without
        // this purge the rows are orphaned to a dead WorkItemId and the
        // Phase 14.16 SloppyRate KPI keeps counting them for the agents
        // involved, inflating each agent's sloppy rate after every
        // operator workstream-restart cycle.
        var pairAssignments = await db.AgentPairAssignments
            .Where(p => p.WorkItemId == id).ToListAsync(ct);
        db.AgentPairAssignments.RemoveRange(pairAssignments);
        counts["pairAssignments"] = pairAssignments.Count;

        // Phase 52 follow-up — agent_claims. DeleteSpawn already releases
        // open claims (ReleasedAt = now) but never deletes the rows; the
        // capacity-tracking sweep keeps reading them and the audit grows
        // unboundedly. Wipe historical claims for this work item — the
        // ledger of "which agent picked this up" is meaningless once
        // the work item itself is gone.
        var agentClaims = await db.AgentClaims.Where(c => c.WorkItemId == id).ToListAsync(ct);
        db.AgentClaims.RemoveRange(agentClaims);
        counts["agentClaims"] = agentClaims.Count;

        var wsEvents = await db.WsEvents.Where(e => e.Ws == id).ToListAsync(ct);
        db.WsEvents.RemoveRange(wsEvents);
        counts["wsEvents"] = wsEvents.Count;

        // ── 3. The work-item synthesized for this workstream ─────────────
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is not null)
        {
            db.WorkItems.Remove(wi);
            counts["workItem"] = 1;
        }

        // ── 4. Board card (if one was created via "Dispatch") ────────────
        var card = await db.Cards.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (card is not null)
        {
            db.Cards.Remove(card);
            counts["card"] = 1;
        }

        // ── 5. The workstream itself ─────────────────────────────────────
        db.Workstreams.Remove(row);
        counts["workstream"] = 1;

        await db.SaveChangesAsync(ct);

        // ── 6. On-disk artifacts (best-effort; never block the delete) ───
        var stateDir = Path.GetDirectoryName(config["Forge:Db:Path"] ?? @"E:\forge\forge.db") ?? @"E:\forge";
        var spawnSlug = row.StorySpawnId ?? string.Empty;
        var dirsRemoved = 0;
        foreach (var slug in new[] { spawnSlug, spawnSlug.Replace('-', '_') })
        {
            if (string.IsNullOrEmpty(slug)) continue;
            var spawnRoot = Path.Combine(stateDir, "storyworkspaces", slug);
            if (!Directory.Exists(spawnRoot)) continue;
            // impl-{id}, review-{id}-r*, fix-{id}-* — match on prefix so
            // every per-round / per-fix workspace gets reaped.
            foreach (var dir in Directory.EnumerateDirectories(spawnRoot))
            {
                var name = Path.GetFileName(dir);
                if (name == $"impl-{id}"
                    || name.StartsWith($"review-{id}-", StringComparison.Ordinal)
                    || name.StartsWith($"fix-{id}-", StringComparison.Ordinal))
                {
                    try { Directory.Delete(dir, recursive: true); dirsRemoved++; }
                    catch { /* best-effort */ }
                }
            }
        }
        counts["workspaceDirs"] = dirsRemoved;

        // ── 7. Worktree mount + local branch ─────────────────────────────
        if (!string.IsNullOrEmpty(row.Branch))
        {
            var repoRow = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == row.RepoId, ct);
            var cloneRoot = repoRow?.CloneRoot;
            if (!string.IsNullOrEmpty(cloneRoot))
            {
                // Phase 50 — try EVERY worktree-path convention this codebase
                // ships with (legacy spawn-, legacy ws-, Phase 49 short slug).
                // Mirrors the Restart endpoint's defensive sweep. Before this
                // fix, CascadeDeleteWorkstreamAsync only removed the legacy
                // `spawn-{branch}` shape, so engineer workstreams created
                // under the Phase 49 short slug (`stage-1.1-foo/`) survived
                // delete and surfaced stale commits in the Worktree tab.
                var candidatePaths = new List<string>();
                var sanitized = row.Branch.Replace('/', '-');
                candidatePaths.Add(Path.Combine(cloneRoot, ".worktree", $"spawn-{sanitized}"));
                var sanitizedId = row.Id.Replace("/", "-").Replace("\\", "-").Replace(" ", "-");
                candidatePaths.Add(Path.Combine(cloneRoot, ".worktree", $"ws-{sanitizedId}"));
                if (!string.IsNullOrEmpty(row.StorySpawnId))
                {
                    try
                    {
                        var siblings = await db.Workstreams.AsNoTracking()
                            .Where(w => w.StorySpawnId == row.StorySpawnId)
                            .ToListAsync(ct);
                        if (siblings.All(w => w.Id != row.Id)) siblings.Add(row);
                        var newSlug = Forge.Api.Services.Stories.WorkstreamWorktreeSlug.Compute(row, siblings);
                        candidatePaths.Add(Path.Combine(cloneRoot, ".worktree", newSlug));
                    }
                    catch { /* slug computer is best-effort */ }
                }

                // Run `git worktree prune` first so stale metadata doesn't
                // block a fresh `worktree add` after a re-spawn.
                await RunGitQuietAsync(cloneRoot, "worktree", "prune");

                var wtRemoved = 0;
                foreach (var wtPath in candidatePaths.Distinct())
                {
                    // Always try `git worktree remove --force` first — even
                    // when the dir doesn't exist on disk, git may still
                    // hold a stale worktree registration that blocks new
                    // `worktree add` calls with "already exists" errors.
                    await RunGitQuietAsync(cloneRoot, "worktree", "remove", "--force", wtPath);
                    if (Directory.Exists(wtPath))
                    {
                        try { await git.RemoveAsync(wtPath, force: true); wtRemoved++; }
                        catch
                        {
                            try { Directory.Delete(wtPath, recursive: true); wtRemoved++; }
                            catch { /* locked files — will be pruned on next claim */ }
                        }
                    }
                }
                await RunGitQuietAsync(cloneRoot, "worktree", "prune");
                if (wtRemoved > 0) counts["worktreeRemoved"] = wtRemoved;

                // Delete the local branch. Best-effort — `git branch -D`
                // refuses if the branch is checked out somewhere; in that
                // case the worktree-remove above is the meaningful work.
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("git", $"branch -D {row.Branch}")
                    {
                        WorkingDirectory = cloneRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p is not null && p.WaitForExit(10_000) && p.ExitCode == 0)
                    {
                        counts["branchRemoved"] = 1;
                    }
                }
                catch { /* best-effort */ }

                // Phase 50 — spawn-delete path also wipes the remote so a
                // re-spawn of the same story doesn't pull stale history
                // from origin. Best-effort; failures (no remote, branch
                // never pushed, no auth) are silent.
                if (deleteRemoteBranch)
                {
                    var rc = await RunGitQuietAsync(cloneRoot, "push", "origin", "--delete", row.Branch);
                    if (rc == 0) counts["remoteBranchRemoved"] = 1;
                    else counts["remoteBranchRemoveFailed"] = 1;
                }
            }
        }

        // Also catch the legacy worktree shape (worktreeJson = [base, sub]).
        var legacyWorktree = CardMapper.Deserialize<List<string>>(row.WorktreeJson) ?? new();
        if (legacyWorktree.Count >= 2 && legacyWorktree[0] != "(not yet created)")
        {
            try
            {
                var path = Path.Combine(legacyWorktree[0], legacyWorktree[1]);
                if (Directory.Exists(path)) await git.RemoveAsync(path, force: true);
            }
            catch { /* best-effort */ }
        }

        return new
        {
            id,
            status = "deleted",
            counts,
        };
    }

    /// <summary>
    /// POST /api/workstreams/{id}/restart — nuke everything for this workstream
    /// and start fresh: delete deliverables, clear workspace, reset git branch,
    /// reset status to active, re-enter the gen/eval loop from iter 1.
    ///
    /// <para>The git cleanup is critical: delete the remote branch, remove the
    /// local worktree, and delete the local branch ref so
    /// <see cref="RealStoryGitOps.EnsureBranchAsync"/> creates a clean branch
    /// from <c>origin/main</c> instead of reusing the stale one. Without this,
    /// the new run inherits files from the previous attempt (observed:
    /// 2026-05-04, NodeJS artifacts from a prior run leaked into a C#
    /// workstream creating a 2k-file PR with node_modules).</para>
    /// </summary>
    private static async Task<IResult> Restart(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub,
        IConfiguration config,
        IEnumerable<IForgeProvider> providers,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (ws is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        // ── 0. Cancel in-flight work (mirrors Cancel endpoint logic) ────
        try { await runner.CancelByTaskAsync(id, ctx.RequestAborted); }
        catch { /* best-effort — don't block restart if runner is down */ }

        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();

        // ── 1. Delete all deliverables for this workstream ──────────────
        var deliverables = await db.Deliverables.Where(d => d.WorkItemId == id).ToListAsync();
        db.Deliverables.RemoveRange(deliverables);

        // ── 2. Delete transcript events ─────────────────────────────────
        var events = await db.TaskTranscriptEvents.Where(e => e.WorkItemId == id).ToListAsync();
        db.TaskTranscriptEvents.RemoveRange(events);

        // ── 3. Delete open questions ────────────────────────────────────
        var questions = await db.DeliverableQuestions.Where(q => q.WorkItemId == id).ToListAsync();
        db.DeliverableQuestions.RemoveRange(questions);

        // ── 3b. Wipe Phase 52 pair-attempt history + cost ledger ────────
        // Operator-observed 2026-05-13: Restart used to leave
        // agent_pair_assignment, agent_usage, WorkItemRow.PairAttemptCount,
        // and WorkItemRow.BudgetWindowStartUtc untouched. That left a
        // freshly-restarted workstream born at PairAttemptCount=3 with the
        // prior pairs locked into PriorPairsJson and ~$3,600 of historical
        // generator spend already metered into the dollar/token caps. The
        // SloppyReassignmentReconciler then refused to pick any pair and
        // the orchestrator went straight to terminal-stall on the very
        // first sloppy hit.
        //
        // Wiping these on Restart matches the spec's intent: Restart is
        // the explicit operator unblock that gives the work item a clean
        // slate. agent_usage rows are deleted (not just cutoff via
        // BudgetWindowStartUtc) so the workstream's cost card shows $0
        // for the new attempt — operators were also seeing stale $-totals
        // on stalled rows post-Restart.
        var pairRows = await db.AgentPairAssignments.Where(p => p.WorkItemId == id).ToListAsync();
        db.AgentPairAssignments.RemoveRange(pairRows);

        var usageRows = await db.AgentUsage.Where(u => u.TaskId == id).ToListAsync();
        db.AgentUsage.RemoveRange(usageRows);

        // ── 4. Close upstream PRs and deactivate local rows ─────────────
        var prIds = ParseStringList(ws.PrsJson);
        foreach (var prId in prIds)
        {
            var prRow = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId);
            if (prRow is null) continue;

            // Close the PR upstream (GitHub / ADO) so it doesn't block the
            // new PR on the same branch name.
            if (!string.IsNullOrEmpty(prRow.Provider) && !string.IsNullOrEmpty(prRow.WebUrl))
            {
                try
                {
                    var provider = providers.FirstOrDefault(p =>
                        string.Equals(p.Kind, prRow.Provider, StringComparison.OrdinalIgnoreCase) && p.IsConfigured);
                    if (provider is not null)
                    {
                        // Use the existing PATCH endpoint to close the PR.
                        // GitHub: PATCH /repos/{owner}/{repo}/pulls/{number} { "state": "closed" }
                        await provider.ClosePullRequestAsync(prId, ProviderCallContext.Anonymous, ctx.RequestAborted);
                    }
                }
                catch { /* best-effort — PR may already be closed or deleted */ }
            }

            prRow.IsActive = false;
            prRow.Col = "abandoned";
            prRow.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        }

        // ── 5. Reset workstream status ──────────────────────────────────
        ws.Status = "active";
        ws.PrsJson = "[]";
        ws.Commits = 0;
        ws.Ahead = 0;
        ws.Behind = 0;
        ws.Conflicts = 0;
        ws.AgentsJson = "[]";
        // Phase 49 — clear the operator-triage reason when leaving the
        // stalled state. Restart is the explicit unblock path; any prior
        // stall reason (build failure, no-progress, non-pass verdict) is
        // no longer applicable once the worktree + deliverables are wiped.
        ws.StallReason = null;

        // Reset the work-item execution status so the orchestrator can
        // re-assign agents and re-claim the workstream.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (wi is not null)
        {
            // 2026-05-07 — was `null`, but ExecutionStatus is a non-nullable
            // string (Entities.cs:1528) → SQLite NOT NULL violation at
            // SaveChangesAsync. Match the working precedent in
            // TaskExecutionEndpoints.ClearTranscript: queue it ready to
            // re-claim and zero the trial counter.
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.TrialCount = 0;
            wi.AssignedAgentJson = null;

            // Phase 52 reset — pair-attempt counter, prior-pair exclusion
            // list, terminal failure reason, and the dollar/token budget
            // window all return to their post-claim defaults so the new
            // run is genuinely a clean slate. Without these, a Restart on
            // a row that hit MaxPairAttempts (3) stays terminal-failed and
            // the reconciler/orchestrator refuse to drive it forward.
            // PairAttemptCount = 1 matches the Phase 52 migration default
            // and the "first pair currently working" semantic — only
            // SloppyReassignmentReconciler increments it (newAttempt = +1
            // per rotation), so resetting to 1 gives the new pair a full
            // 3-attempt budget before the cap re-applies.
            wi.PairAttemptCount = 1;
            wi.PriorPairsJson = null;
            wi.FailureReason = null;
            wi.LastPairAgentIdsJson = null;
            wi.BudgetWindowStartUtc = DateTimeOffset.UtcNow;

            wi.UpdatedAt = DateTimeOffset.UtcNow;

            // Refresh DescriptionMd from the current story description
            // so restarted tasks pick up any updates the user made.
            if (!string.IsNullOrEmpty(ws.StorySpawnId))
            {
                var spawnForDesc = await db.StorySpawns.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId);
                if (spawnForDesc is not null)
                {
                    var story = await db.ProductStories.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == spawnForDesc.StoryId);
                    if (story is not null && !string.IsNullOrEmpty(story.Description))
                    {
                        wi.DescriptionMd = story.Description;
                    }
                }
            }
        }

        await db.SaveChangesAsync();

        // ── 6. Clear the cached workspace ───────────────────────────────
        var dbPath = config["Forge:Db:Path"] ?? @"E:\forge\forge.db";
        var stateDir = System.IO.Path.GetDirectoryName(dbPath) ?? @"E:\forge";
        var spawnSlug = ws.StorySpawnId ?? "";
        foreach (var slug in new[] { spawnSlug, spawnSlug.Replace('-', '_') })
        {
            var wsDir = System.IO.Path.Combine(stateDir, "storyworkspaces", slug, $"impl-{id}");
            if (System.IO.Directory.Exists(wsDir))
            {
                try { System.IO.Directory.Delete(wsDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        // ── 7. Nuke git branch — worktree, local ref, AND remote ────────
        if (!string.IsNullOrEmpty(ws.Branch))
        {
            var repoRow = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ws.RepoId);
            if (repoRow is not null && !string.IsNullOrEmpty(repoRow.CloneRoot)
                && System.IO.Directory.Exists(repoRow.CloneRoot))
            {
                var cloneRoot = repoRow.CloneRoot;

                // Phase 49 follow-up — engineer workstreams switched from the
                // legacy `spawn-{branch-slug}` directory shape (often 100+
                // chars on Windows MAX_PATH-fragile builds) to the short
                // `stage-{phaseNum}.{stageNum}-{anchorSlug}` shape. Restart
                // must clean BOTH so a row migrating from old → new doesn't
                // leave a stale 100-char dir behind that the next claim
                // can't see (it'd open the new short path; the old long
                // one would persist forever as an orphan).
                var sanitized = SanitizeBranchSlug(ws.Branch);
                var legacyWtPath = System.IO.Path.Combine(cloneRoot, ".worktree", $"spawn-{sanitized}");

                string? newWtPath = null;
                if (!string.IsNullOrEmpty(ws.StorySpawnId))
                {
                    var siblings = await db.Workstreams.AsNoTracking()
                        .Where(w => w.StorySpawnId == ws.StorySpawnId)
                        .ToListAsync();
                    if (siblings.All(w => w.Id != ws.Id)) siblings.Add(ws);
                    var newSlug = Forge.Api.Services.Stories.WorkstreamWorktreeSlug.Compute(ws, siblings);
                    newWtPath = System.IO.Path.Combine(cloneRoot, ".worktree", newSlug);
                }

                foreach (var wtPath in new[] { newWtPath, legacyWtPath }.Where(p => !string.IsNullOrEmpty(p)).Distinct())
                {
                    // (a) Remove worktree (if registered)
                    await RunGitQuietAsync(cloneRoot, "worktree", "remove", "--force", wtPath!);

                    // (b) Delete directory if it survived the remove
                    if (System.IO.Directory.Exists(wtPath!))
                    {
                        try { System.IO.Directory.Delete(wtPath!, recursive: true); }
                        catch { /* locked files — will be pruned on next tick */ }
                    }
                }

                // (c) Prune stale worktree refs
                await RunGitQuietAsync(cloneRoot, "worktree", "prune");

                // (d) Delete local branch ref so EnsureWorktreeAsync
                //     doesn't resurrect old history.
                await RunGitQuietAsync(cloneRoot, "branch", "-D", ws.Branch);

                // (e) Delete remote branch so EnsureBranchAsync creates
                //     a fresh one from origin/main.
                await RunGitQuietAsync(cloneRoot, "push", "origin", "--delete", ws.Branch);
            }
        }

        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "restarted", id });

        // ── 8. Re-drive the story orchestrator ──────────────────────────
        if (!string.IsNullOrEmpty(ws.StorySpawnId))
        {
            var spawn = await db.StorySpawns.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId);
            if (spawn is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await orchestrator.DriveAsync(spawn.StoryId, forceResume: false, CancellationToken.None); }
                    catch { /* failures persisted via PersistLastErrorAsync */ }
                });
            }
        }

        return Results.Ok(new { id, status = "restarted", deliverablesCleaned = deliverables.Count, eventsCleaned = events.Count });
    }

    /// <summary>
    /// Sanitize a branch name into a filesystem-safe slug. Must match
    /// <see cref="RealStoryGitOps"/>'s <c>SanitizeBranchSlug</c> exactly
    /// so we target the correct worktree directory.
    /// </summary>
    internal static string SanitizeBranchSlug(string branch)
    {
        var sb = new System.Text.StringBuilder(branch.Length);
        foreach (var c in branch)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>Best-effort git command — returns exit code, never throws.</summary>
    internal static async Task<int> RunGitQuietAsync(string cwd, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return -1;

            // Bounded wait — covers the original 2026-05-12 hang path
            // (push --delete against msazure.visualstudio.com) for both
            // FailFast (DNS/network stalls) and Interactive (unanswered
            // GCM prompt) policies. Default 180s; tunable via env.
            var timeoutSeconds = 180;
            var timeoutRaw = Environment.GetEnvironmentVariable("FORGE_GIT_NETWORK_TIMEOUT_SECONDS");
            if (int.TryParse(timeoutRaw, out var parsed) && parsed > 0) timeoutSeconds = parsed;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
                return p.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return -1;
            }
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Phase 50 — capture variant of <see cref="RunGitQuietAsync"/>. Returns
    /// (exit-code, stdout, stderr) so callers can parse output (e.g.
    /// <c>git branch --list</c>, <c>git ls-remote --heads</c>). Never throws.
    /// </summary>
    internal static async Task<(int Code, string StdOut, string StdErr)> RunGitCaptureAsync(string cwd, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (-1, string.Empty, string.Empty);

            // See RunGitQuietAsync — same timeout rationale.
            var timeoutSeconds = 180;
            var timeoutRaw = Environment.GetEnvironmentVariable("FORGE_GIT_NETWORK_TIMEOUT_SECONDS");
            if (int.TryParse(timeoutRaw, out var parsed) && parsed > 0) timeoutSeconds = parsed;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            // No ct on the read tasks — they drain to EOF naturally
            // when the process exits / is killed, preserving stderr.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
                return (p.ExitCode, await stdoutTask, await stderrTask);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                string drainedStderr = string.Empty;
                try { drainedStderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* best-effort */ }
                return (-1, string.Empty, $"git {string.Join(' ', args)} timed out after {timeoutSeconds}s. Partial stderr: {drainedStderr}");
            }
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    internal static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// POST /api/workstreams/{id}/cancel — stop the currently running gen/eval
    /// loop for this workstream. Kills the running agent invocation(s),
    /// marks queue rows as cancelled, and flips ExecutionStatus to idle.
    ///
    /// <para>Before 2026-05-04 this endpoint only flipped DB state — the
    /// in-flight agent stream was unaware of the cancellation and kept
    /// emitting events for minutes after the operator clicked Cancel.
    /// Now we route through <see cref="IAgentRunner.CancelByTaskAsync"/>
    /// which cancels the per-invocation CTS — for Claude/Copilot CLI the
    /// dispatcher's <c>ctReg.Register</c> calls <c>process.Kill(entireProcessTree: true)</c>
    /// on cancel, so the subprocess dies. For the SDK paths the streaming
    /// HTTP request errors out and the agent loop exits.</para>
    /// </summary>
    private static async Task<IResult> Cancel(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (ws is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        // (1) Kill in-flight agent invocations FIRST so no stray events land
        // after we flip the DB state below. Best-effort — the runner is
        // tolerant of unknown taskIds (returns 0).
        var invocationsCancelled = 0;
        try
        {
            invocationsCancelled = await runner.CancelByTaskAsync(id, ctx.RequestAborted);
        }
        catch
        {
            // Don't let runner failures prevent DB cleanup — the queue row
            // is the source of truth for whether the workstream is "running".
        }

        // (2) Cancel any queued/active task_queue rows for this workstream
        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (3) Mark the work item execution as cancelled — TERMINAL state.
        // Earlier this code wrote "idle" which the StorySpawnOrchestrator
        // tick treats as re-claimable, so 15 seconds after a cancel a fresh
        // iter would start (observed 2026-05-04). "cancelled" is a real
        // terminal status and the orchestrator skips it.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Cancelled;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // (4) Mark the workstream itself as cancelled so DriveImplementAsync
        // skips it on subsequent ticks. The Restart endpoint flips it back
        // to "active" and clears deliverables/transcripts to revive the loop.
        ws.Status = "cancelled";

        // (5) Mark the last deliverable as stalled if it's in iterate state
        var lastDel = (await db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync())
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        if (lastDel is not null && lastDel.Verdict is "iterate")
        {
            lastDel.Verdict = "stalled";
        }

        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "cancelled", id });

        return Results.Ok(new
        {
            id,
            status = "cancelled",
            queueRowsCancelled = queueRows.Count,
            invocationsCancelled,
        });
    }
}
