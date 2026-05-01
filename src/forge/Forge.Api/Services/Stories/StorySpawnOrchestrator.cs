// -----------------------------------------------------------------------
// <copyright file="StorySpawnOrchestrator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// forge_implementation.md Phase 28 — orchestration brain. The endpoints
/// in <c>StoriesEndpoints</c> mutate spawn state; this class watches for
/// state transitions and drives the corresponding crew work:
///
/// <list type="number">
/// <item><c>plan</c> entered → architect crew writes 4 docs, opens plan PR.</item>
/// <item>Plan PR merged + caller advances to <c>decompose</c> → architect produces decomposition (which seeds workstream rows).</item>
/// <item><c>implement</c> entered → engineer crew picks up each ready (no-deps-or-deps-merged) workstream, writes its slice, opens a PR.</item>
/// <item>Workstream PR opened → review crew posts comments and routes them back to the engineer; engineer pushes a fix; review approves and merges.</item>
/// <item>Plan PR merged → QA crew (parallel) writes feature files, opens QA PR.</item>
/// <item>QA PR merged → engineer crew writes step definitions per workstream, opens step-def PRs (one per workstream).</item>
/// <item>All workstream PRs merged + QA + step-def PRs merged + e2e green → spawn flips to <c>done</c>; ADO work item transitions to <c>Closed</c>.</item>
/// </list>
///
/// Each method is idempotent: re-running on the same row is a no-op when
/// the side effect has already happened. This is what makes the orchestrator
/// safe to call from the dependency scheduler tick AND from the endpoint
/// after a phase advance.
/// </summary>
public sealed class StorySpawnOrchestrator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider services;
    private readonly IStoryArtifactStrategy strategy;
    private readonly IStoryGitOps git;
    private readonly IStoryProviderOps provider;
    private readonly AgentAllocationLoader allocation;
    private readonly ILogger<StorySpawnOrchestrator>? logger;

    public StorySpawnOrchestrator(
        IServiceProvider services,
        IStoryArtifactStrategy strategy,
        IStoryGitOps git,
        IStoryProviderOps provider,
        AgentAllocationLoader allocation,
        ILogger<StorySpawnOrchestrator>? logger = null)
    {
        this.services = services;
        this.strategy = strategy;
        this.git = git;
        this.provider = provider;
        this.allocation = allocation;
        this.logger = logger;
    }

    /// <summary>
    /// Drive the spawn forward as far as the gate logic allows. Idempotent.
    /// Called: after a phase advance from the API, after a PR merges (via
    /// <c>SpawnDependencyScheduler</c>), and on server boot for resumption.
    /// </summary>
    public async Task DriveAsync(string storyId, CancellationToken ct = default)
    {
        // Loop bound is generous so dev-mode auto-progress can chain
        // plan → decompose → implement → review → qa → done in one call.
        // Each phase's drive method is idempotent so a re-tick is cheap.
        for (var safety = 0; safety < 32; safety++)
        {
            var advanced = false;
            using (var scope = this.services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();
                var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
                if (spawn is null) return;

                advanced = await this.RunOneStepAsync(db, hub, spawn, ct);
            }
            if (!advanced) return;
        }
    }

    /// <summary>
    /// Single advance attempt. Returns true when a side effect was applied
    /// (so the caller can loop). Reads the spawn fresh inside its own scope.
    /// </summary>
    private async Task<bool> RunOneStepAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub, StorySpawnRow spawn, CancellationToken ct)
    {
        // The spawn may originate from either an upstream WorkItem (ADO Feature/Epic)
        // or a locally-authored ProductStory. When no WorkItem matches the spawn id,
        // synthesize a transient WorkItemRow from the spawn fields so downstream
        // strategy / provider calls can read title, description, and id uniformly.
        // The synthesized row is detached — never added to the DbContext.
        var story = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == spawn.StoryId, ct);
        if (story is null)
        {
            story = new WorkItemRow
            {
                Id = spawn.StoryId,
                Title = spawn.StoryTitle,
                DescriptionMd = spawn.StoryDescription ?? string.Empty,
                RepoId = spawn.RepoId,
                Provider = "local",
                Track = "dev",
                Type = "feature",
                State = "active",
                Points = spawn.StoryPoints,
                CreatedAt = spawn.CreatedAt,
                UpdatedAt = spawn.UpdatedAt,
            };
        }

        var repo = spawn.RepoId is null
            ? null
            : await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == spawn.RepoId, ct);

        // Phase-specific drivers. Review/QA also re-run the implement pass
        // so blocked-then-unblocked downstream workstreams get picked up
        // when their dependency PR merges (28.5.7 / 28.11.6).
        var advanced = false;
        switch (spawn.Phase)
        {
            case StorySpawnPhases.Plan:
                advanced |= await this.DrivePlanAsync(db, hub, spawn, story, repo, ct);
                break;
            case StorySpawnPhases.Decompose:
                advanced |= await this.DriveDecomposeAsync(db, hub, spawn, story, repo, ct);
                break;
            case StorySpawnPhases.Implement:
                advanced |= await this.DriveImplementAsync(db, hub, spawn, story, repo, ct);
                advanced |= await this.DriveReviewAsync(db, hub, spawn, story, repo, ct);
                break;
            case StorySpawnPhases.Review:
                advanced |= await this.DriveImplementAsync(db, hub, spawn, story, repo, ct);
                advanced |= await this.DriveReviewAsync(db, hub, spawn, story, repo, ct);
                break;
            case StorySpawnPhases.Qa:
                advanced |= await this.DriveImplementAsync(db, hub, spawn, story, repo, ct);
                advanced |= await this.DriveReviewAsync(db, hub, spawn, story, repo, ct);
                advanced |= await this.DriveQaAsync(db, hub, spawn, story, repo, ct);
                break;
            case StorySpawnPhases.Done:
                advanced |= await this.DriveDoneAsync(db, hub, spawn, story, repo, ct);
                break;
        }
        return advanced;
    }

    // ── plan ─────────────────────────────────────────────────────────────────
    private async Task<bool> DrivePlanAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        // Idempotent — once PlanPrId is set, the gate (review/merge of the
        // plan PR) is owned by the review crew + operator, not by the
        // orchestrator. Advancing the phase happens via PATCH /spawn/phase.
        if (!string.IsNullOrEmpty(spawn.PlanPrId)) return false;

        var planBranch = spawn.PlanBranch ?? $"story/{GitSafe(spawn.StoryId)}/plan";
        await this.git.EnsureBranchAsync(repo, planBranch, "main", ct);

        var artifacts = await this.strategy.ProducePlanArtifactsAsync(spawn, story, ct);
        await this.git.CommitFilesAsync(repo, planBranch, artifacts,
            $"plan: artifacts for story {GitSafe(spawn.StoryId)}", ct);

        var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
            Branch: planBranch,
            BaseBranch: "main",
            Title: $"[plan] {story.Title}",
            Body: $"Planning artifacts for story `{GitSafe(spawn.StoryId)}` produced by the architect crew.",
            Repo: repo,
            WorkItemId: story.Id,
            Workstream: null,
            Epic: spawn.StoryId), ct);

        var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        spawnRow.PlanBranch = planBranch;
        spawnRow.PlanPrId = prId;
        spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Kick off QA in parallel — the spec requires QA to start after the
        // plan PR exists. The QA crew works on a separate qa branch; merging
        // the plan PR is the gate that lets QA *commit* (handled inside
        // DriveQaAsync via qa branch existence check).
        if (string.IsNullOrEmpty(spawnRow.QaBranch))
        {
            spawnRow.QaBranch = $"story/{GitSafe(spawn.StoryId)}/qa";
            await db.SaveChangesAsync(ct);
            await this.git.EnsureBranchAsync(repo, spawnRow.QaBranch, "main", ct);
        }

        await EmitChange(hub, "plan-pr-opened", spawnRow, ct);
        return true;
    }

    // ── decompose ────────────────────────────────────────────────────────────
    private async Task<bool> DriveDecomposeAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        // Idempotent — only generate decomposition once.
        if (!string.IsNullOrEmpty(spawn.DecompositionJson)) return false;

        // Architect crew reads the merged plan and produces the decomposition.
        // We verify the four expected docs exist on main so the architect
        // really has something to read (28.10 artifact-existence check).
        if (!await this.PlanArtifactsLandedAsync(repo, spawn, ct))
        {
            this.logger?.LogWarning("Plan artifacts missing on main for {StoryId}; deferring decomposition.", spawn.StoryId);
            return false;
        }

        var ws = await this.strategy.ProduceDecompositionAsync(spawn, story, ct);
        if (ws.Count == 0) return false;

        var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        spawnRow.DecompositionJson = JsonSerializer.Serialize(ws, JsonOpts);
        spawnRow.UpdatedAt = DateTimeOffset.UtcNow;

        // Persist workstreams + dep edges (mirrors the manual API path).
        var maxSort = await db.Workstreams.Select(w => (int?)w.Sort).MaxAsync(ct) ?? -1;
        var titleToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in ws)
        {
            var slug = SlugFor(w.Title);
            var wsId = $"ws-{GitSafe(spawn.StoryId)}-{slug}".ToLowerInvariant();
            titleToId[w.Title] = wsId;
            // Idempotent — leave any pre-existing row alone.
            if (await db.Workstreams.AnyAsync(x => x.Id == wsId, ct)) continue;
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = wsId,
                Sort = ++maxSort,
                RepoId = spawn.RepoId,
                Name = w.Title,
                Branch = $"ws/{GitSafe(spawn.StoryId)}/{slug}",
                Base = "main",
                Epic = spawn.StoryId,
                Status = "planning",
                WorktreeJson = "[\"(not yet created)\"]",
                AgentsJson = "[]",
                PrsJson = "[]",
                CardsJson = "[]",
                Summary = w.Description,
                BarStart = 0, BarEnd = 5, Planned = true,
                StorySpawnId = spawn.Id,
            });
        }
        foreach (var w in ws)
        {
            var fromId = titleToId[w.Title];
            foreach (var depRaw in w.Deps ?? Array.Empty<string>())
            {
                var dep = depRaw.Trim();
                if (string.IsNullOrEmpty(dep)) continue;
                if (!titleToId.TryGetValue(dep, out var depId)) continue;
                if (await db.WorkstreamDeps.AnyAsync(d => d.WorkstreamId == fromId && d.DependsOnId == depId, ct))
                    continue;
                db.WorkstreamDeps.Add(new WorkstreamDepRow
                {
                    WorkstreamId = fromId,
                    DependsOnId = depId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "decomposed", spawnRow, ct);
        return true;
    }

    // ── implement ────────────────────────────────────────────────────────────
    private async Task<bool> DriveImplementAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync(ct);
        if (workstreams.Count == 0) return false;

        var deps = await db.WorkstreamDeps.ToListAsync(ct);
        var changed = false;
        foreach (var w in workstreams)
        {
            // Already merged into review queue — skip.
            if (w.Status is "review" or "merging" or "complete") continue;

            // Block until every dep is merged.
            var blocking = deps
                .Where(d => d.WorkstreamId == w.Id)
                .Select(d => workstreams.FirstOrDefault(ww => ww.Id == d.DependsOnId))
                .Where(ww => ww is not null)
                .Where(ww => ww!.Status is not ("merging" or "complete"))
                .ToList();
            if (blocking.Count > 0) continue;

            // Engineer crew claim — assign the first eligible engineer agent.
            var engineers = this.allocation.ForTeam("Engineer");
            var engineer = engineers.FirstOrDefault();
            var assignedAgents = new[] { engineer?.Id ?? "eng-default" };
            w.AgentsJson = JsonSerializer.Serialize(assignedAgents);
            w.Status = "active";
            await db.SaveChangesAsync(ct);

            // Branch + commit + PR.
            await this.git.EnsureBranchAsync(repo, w.Branch, "main", ct);
            var files = await this.strategy.ProduceWorkstreamChangesAsync(spawn, w, ct);
            await this.git.CommitFilesAsync(repo, w.Branch, files,
                $"impl({w.Id}): {w.Name}", ct);

            var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: w.Branch,
                BaseBranch: "main",
                Title: $"[impl] {w.Name}",
                Body: $"Implementation of `{w.Id}` for story `{GitSafe(spawn.StoryId)}`.",
                Repo: repo,
                WorkItemId: spawn.StoryId,
                Workstream: w.Id,
                Epic: spawn.StoryId), ct);

            var prsList = ParseStringList(w.PrsJson);
            if (!prsList.Contains(prId)) prsList.Add(prId);
            w.PrsJson = JsonSerializer.Serialize(prsList);
            w.Status = "review";
            await db.SaveChangesAsync(ct);

            await EmitChange(hub, "ws-pr-opened", spawn, ct);
            changed = true;
        }
        return changed;
    }

    // ── review ───────────────────────────────────────────────────────────────
    private async Task<bool> DriveReviewAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ct);
        var changed = false;
        foreach (var w in workstreams)
        {
            if (w.Status != "review") continue;
            var prs = ParseStringList(w.PrsJson);
            if (prs.Count == 0) continue;
            var prId = prs[0];

            // Round 0 → post comments. Engineer fixes immediately. Round 1 → approve & merge.
            // We track rounds via comment count: 0 comments yet = round 0.
            var commentRows = await db.PrComments.Where(c => c.PrId == prId).ToListAsync(ct);
            if (commentRows.Count == 0)
            {
                var reviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
                var comments = await this.strategy.ProduceReviewCommentsAsync(spawn, w, prId, round: 0, ct);
                if (comments.Count == 0)
                {
                    // No findings — straight to approve and merge.
                    await this.provider.ApprovePrAsync(prId, reviewer, ct);
                    await this.provider.MergePrAsync(prId, ct);
                    await this.git.MergeAsync(repo, w.Branch, "main", ct);
                    w.Status = "merging";
                    await db.SaveChangesAsync(ct);
                    changed = true;
                    continue;
                }

                foreach (var c in comments)
                {
                    await this.provider.PostPrCommentAsync(prId, c.FilePath, c.Line, c.Body, reviewer, ct);
                }

                // Engineer immediately addresses each comment.
                var engineerId = ParseStringList(w.AgentsJson).FirstOrDefault() ?? "eng-default";
                var openComments = await db.PrComments.Where(c => c.PrId == prId && c.State == "active").ToListAsync(ct);
                foreach (var oc in openComments)
                {
                    var fix = await this.strategy.ProduceReviewFixAsync(spawn, w,
                        new ReviewCommentContext(oc.FilePath ?? string.Empty, oc.Line ?? 0, oc.Body, engineerId), ct);
                    await this.git.CommitFilesAsync(repo, w.Branch,
                        new Dictionary<string, string>(StringComparer.Ordinal) { [fix.FilePath] = fix.NewContent },
                        $"fix({w.Id}): address review comment", ct);
                    await this.provider.ResolvePrCommentAsync(oc.Id, engineerId, ct);
                }
                changed = true;
                continue;
            }

            // Round 1+: any open comments? If not, approve and merge.
            var stillOpen = commentRows.Count(c => c.State == "active");
            if (stillOpen == 0)
            {
                var reviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
                await this.provider.ApprovePrAsync(prId, reviewer, ct);
                await this.provider.MergePrAsync(prId, ct);
                await this.git.MergeAsync(repo, w.Branch, "main", ct);
                w.Status = "merging";
                await db.SaveChangesAsync(ct);
                changed = true;
            }
        }
        return changed;
    }

    // ── qa ───────────────────────────────────────────────────────────────────
    private async Task<bool> DriveQaAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync(ct);

        // Step 1 — produce the QA feature files & open the QA PR (idempotent).
        if (string.IsNullOrEmpty(spawn.QaPrId))
        {
            var qaBranch = spawn.QaBranch ?? $"story/{GitSafe(spawn.StoryId)}/qa";
            await this.git.EnsureBranchAsync(repo, qaBranch, "main", ct);
            var files = await this.strategy.ProduceQaFeatureFilesAsync(spawn, story, workstreams, ct);
            await this.git.CommitFilesAsync(repo, qaBranch, files,
                $"qa: feature files for story {GitSafe(spawn.StoryId)}", ct);

            var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: qaBranch,
                BaseBranch: "main",
                Title: $"[qa] {story.Title}",
                Body: $"QA feature files for story `{GitSafe(spawn.StoryId)}`.",
                Repo: repo,
                WorkItemId: spawn.StoryId,
                Workstream: null,
                Epic: spawn.StoryId), ct);

            var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
            spawnRow.QaBranch = qaBranch;
            spawnRow.QaPrId = prId;
            spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Auto-merge the QA PR (review of QA artifacts is implicit in the deterministic flow).
            await this.provider.ApprovePrAsync(prId, this.allocation.ForTeam("QA").FirstOrDefault()?.Id ?? "qa-default", ct);
            await this.provider.MergePrAsync(prId, ct);
            await this.git.MergeAsync(repo, qaBranch, "main", ct);
            await EmitChange(hub, "qa-pr-merged", spawnRow, ct);
            return true;
        }

        // Step 2 — after QA PR merged, write step definitions per workstream
        // and open per-workstream step-def PRs (separate from impl PR).
        var qaPr = await db.Prs.FirstOrDefaultAsync(p => p.Id == spawn.QaPrId, ct);
        if (qaPr is null || qaPr.Col != "merged") return false;

        var changed = false;
        foreach (var w in workstreams)
        {
            var stepBranch = $"ws/{GitSafe(spawn.StoryId)}/{w.Id.Split('-').Last()}-steps";
            // Have we already created a step PR for this workstream?
            var existing = await db.Prs
                .Where(p => p.Branch == stepBranch && p.Workstream == w.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is not null) continue;

            await this.git.EnsureBranchAsync(repo, stepBranch, "main", ct);
            var files = await this.strategy.ProduceStepDefinitionsAsync(spawn, w, ct);
            await this.git.CommitFilesAsync(repo, stepBranch, files,
                $"qa-steps({w.Id}): step definitions", ct);

            var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: stepBranch,
                BaseBranch: "main",
                Title: $"[qa-steps] {w.Name}",
                Body: $"Step definitions for `{w.Id}` (story `{GitSafe(spawn.StoryId)}`).",
                Repo: repo,
                WorkItemId: spawn.StoryId,
                Workstream: w.Id,
                Epic: spawn.StoryId), ct);

            // Approve + merge.
            var qaId = this.allocation.ForTeam("QA").FirstOrDefault()?.Id ?? "qa-default";
            await this.provider.ApprovePrAsync(prId, qaId, ct);
            await this.provider.MergePrAsync(prId, ct);
            await this.git.MergeAsync(repo, stepBranch, "main", ct);

            // Stash on workstream PrsJson so artifacts endpoint surfaces it.
            var prsList = ParseStringList(w.PrsJson);
            if (!prsList.Contains(prId)) prsList.Add(prId);
            w.PrsJson = JsonSerializer.Serialize(prsList);
            w.Status = "complete";
            await db.SaveChangesAsync(ct);
            changed = true;
        }

        // Once every workstream is complete, advance to done.
        if (workstreams.All(ww => ww.Status == "complete"))
        {
            var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
            spawnRow.Phase = StorySpawnPhases.Done;
            spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await EmitChange(hub, "done", spawnRow, ct);
            changed = true;
        }
        return changed;
    }

    // ── done ─────────────────────────────────────────────────────────────────
    private async Task<bool> DriveDoneAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        // Transition the upstream ADO work item to Closed (idempotent).
        // ProductStory-sourced spawns have no upstream provider record — we
        // mark the local ProductStoryRow as 'done' instead.
        if (story.State is "closed" or "resolved") return false;
        var hasUpstream = await db.WorkItems.AnyAsync(w => w.Id == spawn.StoryId, ct);
        if (hasUpstream)
        {
            await this.provider.TransitionWorkItemAsync(story.Id, "closed", ct);
        }
        else
        {
            var ps = await db.ProductStories.FirstOrDefaultAsync(s => s.Id == spawn.StoryId, ct);
            if (ps is not null)
            {
                ps.Status = "done";
                await db.SaveChangesAsync(ct);
            }
        }
        await EmitChange(hub, "story-closed", spawn, ct);
        return true;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private async Task<bool> PlanArtifactsLandedAsync(RepoRow? repo, StorySpawnRow spawn, CancellationToken ct)
    {
        var paths = new[]
        {
            $"docs/stories/{GitSafe(spawn.StoryId)}/tech-spec.md",
            $"docs/stories/{GitSafe(spawn.StoryId)}/architecture.md",
            $"docs/stories/{GitSafe(spawn.StoryId)}/implementation-plan.md",
            $"docs/stories/{GitSafe(spawn.StoryId)}/e2e-scenarios.md",
        };
        foreach (var p in paths)
        {
            if (!await this.git.FileExistsAsync(repo, "main", p, ct)) return false;
        }
        return true;
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Make a story id safe to embed in git branch names (`:` is reserved)
    /// and Windows file paths (`:` `/` `\` `*` `?` `"` `&lt;` `&gt;` `|` are
    /// reserved). Replaces every reserved character with `-` and collapses
    /// runs.
    /// </summary>
    private static string GitSafe(string storyId)
    {
        if (string.IsNullOrEmpty(storyId)) return "story";
        var sb = new System.Text.StringBuilder(storyId.Length);
        foreach (var c in storyId)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-');
        }
        var s = sb.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-');
    }

    private static string SlugFor(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-' || ch == '_') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return string.IsNullOrEmpty(s) ? "ws" : s;
    }

    private static async Task EmitChange(IHubContext<ForgeHub>? hub, string kind, StorySpawnRow spawn, CancellationToken ct)
    {
        if (hub is null) return;
        await hub.Clients.All.SendAsync("story-spawn.changed", new
        {
            kind,
            id = spawn.Id,
            storyId = spawn.StoryId,
            phase = spawn.Phase,
        }, ct);
    }
}
