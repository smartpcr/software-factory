// -----------------------------------------------------------------------
// <copyright file="PrsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Domain;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Providers.Ado;
using Forge.Api.Providers.GitHub;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 4 — Pull Request endpoints. The API merely surfaces the cached PR
/// state today; Phase 5/6 land the ADO + GitHub providers and the actual
/// merge / re-run / cancel actions. The Phase-4 stubs return 202 so the
/// UI can wire the buttons end-to-end.
/// </summary>
public static class PrsEndpoints
{
    public static WebApplication MapPrsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/prs").WithTags("Prs");

        grp.MapGet("/", GetAll).WithName("GetPrs");
        grp.MapGet("/{id}", Get).WithName("GetPr");
        grp.MapGet("/{id}/details", GetDetails).WithName("GetPrDetails");
        grp.MapPost("/{id}/auto-merge", SetAutoMerge).WithName("SetPrAutoMerge");
        grp.MapPost("/{id}/build/rerun", RerunBuild).WithName("RerunPrBuild");
        grp.MapPost("/{id}/build/cancel", CancelBuild).WithName("CancelPrBuild");
        grp.MapPost("/{id}/approve", Approve).WithName("ApprovePr");
        grp.MapPost("/{id}/changes", RequestChanges).WithName("RequestChangesOnPr");
        grp.MapPost("/{id}/comment", Comment).WithName("CommentOnPr");
        grp.MapPost("/{id}/merge", Merge).WithName("MergePr");
        grp.MapPost("/sync", SyncPrs).WithName("SyncPrs");

        return app;
    }

    private static IForgeProvider? PickProvider(string preferred, IEnumerable<IForgeProvider> providers) =>
        providers.FirstOrDefault(p => p.Kind == preferred && p.IsConfigured)
        ?? providers.FirstOrDefault(p => p.IsConfigured);

    // GET /api/prs — list with cached state, optionally scoped to a product (Phase 8.9).
    private static async Task<IResult> GetAll(HttpContext ctx, ForgeDbContext db)
    {
        var scope = await ProductScope.ResolveAsync(ctx, db);
        var q = db.Prs.AsNoTracking().AsQueryable();
        if (!scope.IsAll)
        {
            var ids = scope.RepoIds!;
            q = q.Where(p => p.RepoId != null && ids.Contains(p.RepoId));
        }
        var rows = await q.OrderBy(p => p.Sort).ToListAsync();
        // Phase 24 — fetch open + attention comment counts in one batch query.
        var prIds = rows.Select(r => r.Id).ToList();
        var commentCounts = await db.PrComments
            .Where(c => prIds.Contains(c.PrId) && c.State == "active")
            .GroupBy(c => c.PrId)
            .Select(g => new { PrId = g.Key, Open = g.Count(), Attention = g.Count(c => c.RequiresAttention) })
            .ToListAsync();
        var byId = commentCounts.ToDictionary(x => x.PrId, x => x);
        return Results.Ok(rows.Select(r =>
        {
            byId.TryGetValue(r.Id, out var cc);
            return PrMapper.ToContract(r, cc?.Open, cc?.Attention);
        }).ToList());
    }

    // GET /api/prs/{id} — single PR.
    private static async Task<IResult> Get(string id, ForgeDbContext db)
    {
        var row = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);
        var open = await db.PrComments.CountAsync(c => c.PrId == id && c.State == "active");
        var attention = await db.PrComments.CountAsync(c => c.PrId == id && c.State == "active" && c.RequiresAttention);
        return Results.Ok(PrMapper.ToContract(row, open, attention));
    }

    public sealed record PrDetailsResponse(
        Pr Pr,
        string Description,
        IReadOnlyList<string> BuildLogTail,
        IReadOnlyList<string> Reviewers);

    // GET /api/prs/{id}/details — extended payload for the detail panel.
    private static async Task<IResult> GetDetails(string id, ForgeDbContext db)
    {
        var row = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);
        var pr = PrMapper.ToContract(row);

        // Description: use PR title as a meaningful fallback.
        var description = pr.Title;

        // Build log: only show real build data when available.
        var log = new List<string>();
        if (pr.Build is not null)
        {
            log.Add($"build status: {pr.Build.Status} ({pr.Build.Percent}%)");
            log.Add(pr.Build.Step);
            if (pr.Build.Status == "failing" && pr.Build.FailingTest is not null)
            {
                log.Add($"FAIL  {pr.Build.FailingTest}");
            }
        }

        var reviewers = pr.Review is null
            ? Array.Empty<string>()
            : new[] { $"{pr.Review.Agent} {pr.Review.Name} — {pr.Review.Verdict}" };

        return Results.Ok(new PrDetailsResponse(pr, description, log, reviewers));
    }

    public sealed record SetAutoMergeRequest(bool AutoMerge);

    // POST /api/prs/{id}/auto-merge — toggle the per-PR auto-merge flag.
    private static async Task<IResult> SetAutoMerge(
        HttpContext ctx,
        string id,
        [FromBody] SetAutoMergeRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        row.AutoMerge = req.AutoMerge;
        await db.SaveChangesAsync();

        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "autoMerge", pr });
        return Results.Ok(pr);
    }

    // POST /api/prs/{id}/build/rerun — Phase 6.6: route through provider when one is configured.
    private static async Task<IResult> RerunBuild(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        IUserContext user,
        IEnumerable<IForgeProvider> providers)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        // Best-effort provider trigger; cached state always flips so the UI animates.
        var provider = PickProvider("github", providers);
        BuildStatus? trigger = null;
        if (provider is not null)
        {
            try { trigger = await provider.TriggerBuildAsync(row.Branch, user.ForProvider(provider.Kind)); }
            catch (Exception ex) { return Results.Problem(title: "Provider rerun failed", detail: ex.Message, statusCode: 502); }
        }
        var newBuild = trigger ?? new BuildStatus("running", "rerun queued", 0, null);
        row.BuildJson = CardMapper.Serialize(newBuild);
        await db.SaveChangesAsync();

        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "build.rerun", pr });
        // Phase 6.7 — push a tiny synthetic log tail so the UI can demonstrate streaming.
        await StreamFakeBuildLogAsync(hub, pr.Id);
        return Results.Accepted($"/api/prs/{id}", pr);
    }

    // POST /api/prs/{id}/build/cancel — Phase 6.7: route through provider when one is configured.
    private static async Task<IResult> CancelBuild(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        IUserContext user,
        IEnumerable<IForgeProvider> providers)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        var provider = PickProvider("github", providers);
        BuildStatus? cancel = null;
        if (provider is not null)
        {
            try { cancel = await provider.CancelBuildAsync(row.Branch, user.ForProvider(provider.Kind)); }
            catch (Exception ex) { return Results.Problem(title: "Provider cancel failed", detail: ex.Message, statusCode: 502); }
        }
        var newBuild = cancel ?? new BuildStatus("queued", "cancelled by user", 0, null);
        row.BuildJson = CardMapper.Serialize(newBuild);
        await db.SaveChangesAsync();

        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "build.cancel", pr });
        return Results.Accepted($"/api/prs/{id}", pr);
    }

    // POST /api/prs/{id}/approve — Phase 6.5.
    private static async Task<IResult> Approve(
        HttpContext ctx, string id,
        ForgeDbContext db,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        var provider = PickProvider("github", providers);
        if (provider is null) return Results.Problem(title: "No provider configured", statusCode: 400);
        try { await provider.ApprovePullRequestAsync(id, user.ForProvider(provider.Kind)); }
        catch (Exception ex) { return Results.Problem(title: "Provider approve failed", detail: ex.Message, statusCode: 502); }

        row.ReviewJson = CardMapper.Serialize(new ReviewVerdict(
            Agent: user.UserId ?? "human",
            Name: user.UserId ?? "human",
            Verdict: "approved",
            Summary: "Approved via Forge."));
        await db.SaveChangesAsync();
        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "approved", pr });
        return Results.Ok(pr);
    }

    public sealed record ChangesRequest(string Body);

    // POST /api/prs/{id}/changes — Phase 6.5.
    private static async Task<IResult> RequestChanges(
        HttpContext ctx, string id, [FromBody] ChangesRequest req,
        ForgeDbContext db,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (string.IsNullOrWhiteSpace(req?.Body))
        {
            return Results.Problem(title: "Body is required", statusCode: 400);
        }
        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        var provider = PickProvider("github", providers);
        if (provider is null) return Results.Problem(title: "No provider configured", statusCode: 400);
        try { await provider.RequestChangesOnPullRequestAsync(id, req.Body, user.ForProvider(provider.Kind)); }
        catch (Exception ex) { return Results.Problem(title: "Provider request-changes failed", detail: ex.Message, statusCode: 502); }

        row.ReviewJson = CardMapper.Serialize(new ReviewVerdict(
            Agent: user.UserId ?? "human",
            Name: user.UserId ?? "human",
            Verdict: "changes",
            Summary: req.Body));
        await db.SaveChangesAsync();
        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "changes", pr });
        return Results.Ok(pr);
    }

    public sealed record PrCommentRequest(string Body);

    // POST /api/prs/{id}/comment — Phase 6.5.
    private static async Task<IResult> Comment(
        HttpContext ctx, string id, [FromBody] PrCommentRequest req,
        ForgeDbContext db,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (string.IsNullOrWhiteSpace(req?.Body))
        {
            return Results.Problem(title: "Body is required", statusCode: 400);
        }
        if (!await db.Prs.AnyAsync(p => p.Id == id)) return NotFound(id);

        var provider = PickProvider("github", providers);
        if (provider is null) return Results.Problem(title: "No provider configured", statusCode: 400);
        try { await provider.CommentOnPullRequestAsync(id, req.Body, user.ForProvider(provider.Kind)); }
        catch (Exception ex) { return Results.Problem(title: "Provider comment failed", detail: ex.Message, statusCode: 502); }

        await hub.Clients.All.SendAsync("pr.commented", new { id, by = user.UserId });
        return Results.Ok(new { ok = true });
    }

    // POST /api/prs/{id}/merge — Phase 6.5/6.8 explicit-merge path (auto-merge has its own watcher).
    private static async Task<IResult> Merge(
        HttpContext ctx, string id,
        ForgeDbContext db,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        IHumanGateEvaluator gate,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        var pr = PrMapper.ToContract(row);
        var decision = gate.Evaluate(pr);
        if (decision.Required)
        {
            return Results.Json(new { requireConfirm = true, title = "Human gate", detail = decision.Reason }, statusCode: 422);
        }

        var provider = PickProvider("github", providers);
        if (provider is null) return Results.Problem(title: "No provider configured", statusCode: 400);
        Pr merged;
        try { merged = await provider.MergePullRequestAsync(id, user.ForProvider(provider.Kind)); }
        catch (Exception ex) { return Results.Problem(title: "Provider merge failed", detail: ex.Message, statusCode: 502); }

        row.Col = "merged";
        row.AutoMerge = false;
        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "merged", pr = merged });
        return Results.Ok(merged);
    }

    // Phase 6.7 — synthetic log tail. Phase R replaces this with provider log streaming.
    private static async Task StreamFakeBuildLogAsync(IHubContext<ForgeHub> hub, string prId)
    {
        var lines = new[] { "$ rerun", "$ npm ci", "$ npm test", "PASS", "PASS", "passed in 18s" };
        for (var i = 0; i < lines.Length; i++)
        {
            await hub.Clients.All.SendAsync("pr.log", new { id = prId, seq = i, line = lines[i] });
            await Task.Delay(150);
        }
    }

    // POST /api/prs/sync — pull PRs from a provider into the local cache.
    // Uses the repo's ADO org/project/repo config from the DB (not the global
    // provider settings) so each repo in a multi-repo product syncs correctly.
    private static async Task<IResult> SyncPrs(
        HttpContext ctx,
        [FromBody] PrSyncRequest req,
        ForgeDbContext db,
        [FromServices] IEnumerable<IForgeProvider> providers,
        [FromServices] IHttpClientFactory clientFactory,
        [FromServices] IHubContext<ForgeHub> hub,
        [FromServices] IEnumerable<IAuthService> authServices,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Forge.PrSync");
        log.LogInformation("PR sync requested: repoId={RepoId} provider={Provider} createdBy={CreatedBy} status={Status}",
            req.RepoId, req.Provider, req.CreatedBy, req.Status);

        // Resolve repo config from DB.
        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == req.RepoId, ct);
        if (repo is null)
        {
            log.LogWarning("PR sync: repo '{RepoId}' not found in DB", req.RepoId);
            return Results.Problem(title: $"Repo '{req.RepoId}' not found", statusCode: 404);
        }
        log.LogInformation("PR sync: resolved repo kind={Kind} adoOrg={AdoOrg} adoProject={AdoProject} remoteUrl={RemoteUrl}",
            repo.Kind, repo.AdoOrg, repo.AdoProject, repo.RemoteUrl);

        // Determine the ADO repo name from RemoteUrl (last path segment).
        string? adoRepoName = null;
        if (repo.Kind == "ado")
        {
            // ADO remote URLs: https://dev.azure.com/{org}/{project}/_git/{repo}
            // or: https://{org}.visualstudio.com/{project}/_git/{repo}
            var segments = repo.RemoteUrl.TrimEnd('/').Split('/');
            var gitIdx = Array.FindIndex(segments, s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
            adoRepoName = gitIdx >= 0 && gitIdx + 1 < segments.Length
                ? segments[gitIdx + 1]
                : segments.LastOrDefault(s => !string.IsNullOrEmpty(s));
            log.LogInformation("PR sync: derived ADO repo name='{RepoName}' from URL", adoRepoName);
        }

        // Auth — get a token for the provider.
        var authService = authServices.FirstOrDefault(a => a.Kind == repo.Kind);
        string? bearer = null;
        if (authService is not null)
        {
            try
            {
                bearer = await authService.GetAccessTokenAsync(ct);
                log.LogInformation("PR sync: got {Kind} bearer token (length={Len})", repo.Kind, bearer?.Length ?? 0);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "PR sync: failed to get {Kind} bearer token, will fall back to provider", repo.Kind);
            }
        }
        else
        {
            log.LogWarning("PR sync: no auth service for kind '{Kind}'", repo.Kind);
        }

        IReadOnlyList<Pr> remotePrs;
        try
        {
            if (repo.Kind == "ado" && !string.IsNullOrWhiteSpace(repo.AdoOrg) && !string.IsNullOrWhiteSpace(repo.AdoProject) && !string.IsNullOrWhiteSpace(adoRepoName))
            {
                // Build ADO API call with the repo's own config.
                var adoStatus = req.Status?.ToLowerInvariant() switch
                {
                    "completed" or "merged" => "completed",
                    "all" or null or "" => "all",
                    _ => "active",
                };

                var orgUrl = BuildAdoOrgUrl(repo.AdoOrg!);
                var url = $"{repo.AdoProject}/_apis/git/repositories/{Uri.EscapeDataString(adoRepoName)}/pullrequests?searchCriteria.status={adoStatus}&$top=200&api-version=7.1";
                // ADO supports searchCriteria.creatorId for server-side author filtering,
                // but it requires the GUID — use the uniqueName for display matching instead.
                log.LogInformation("PR sync: ADO GET {OrgUrl}/{Url}", orgUrl, url);

                var client = clientFactory.CreateClient(AdoProvider.ClientName);
                client.BaseAddress = new Uri(orgUrl + "/");
                if (bearer is not null)
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                }
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var resp = await client.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                log.LogInformation("PR sync: ADO response status={Status} body-length={Len}", (int)resp.StatusCode, body.Length);

                if (!resp.IsSuccessStatusCode)
                {
                    log.LogError("PR sync: ADO returned {Status}: {Body}", (int)resp.StatusCode, body.Length > 500 ? body[..500] : body);
                    return Results.Problem(title: $"ADO returned {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}", statusCode: 502);
                }

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var prs = new List<Pr>();
                var valueArr = doc.RootElement.GetProperty("value");
                log.LogInformation("PR sync: ADO returned {Count} PRs from API", valueArr.GetArrayLength());

                foreach (var pr in valueArr.EnumerateArray())
                {
                    var mapped = AdoMapper.ToPr(pr, repo.AdoProject!);
                    // Extract the uniqueName (email) from the raw JSON for author matching.
                    string? creatorUniqueName = null;
                    string? creatorDisplayName = null;
                    if (pr.TryGetProperty("createdBy", out var cb))
                    {
                        if (cb.TryGetProperty("uniqueName", out var un)) creatorUniqueName = un.GetString();
                        if (cb.TryGetProperty("displayName", out var dn)) creatorDisplayName = dn.GetString();
                    }

                    // Client-side author filter: match on uniqueName (email) OR displayName.
                    if (!string.IsNullOrWhiteSpace(req.CreatedBy))
                    {
                        var needle = req.CreatedBy.Trim();
                        var matchUnique = (creatorUniqueName ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);
                        var matchDisplay = (creatorDisplayName ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);
                        if (!matchUnique && !matchDisplay)
                        {
                            log.LogDebug("PR sync: skipping {PrId} — author '{DisplayName}' / '{UniqueName}' doesn't match '{Filter}'",
                                mapped.Id, creatorDisplayName, creatorUniqueName, needle);
                            continue;
                        }
                    }
                    prs.Add(mapped);
                }
                remotePrs = prs;
                log.LogInformation("PR sync: {Count} PRs after author filter", prs.Count);
            }
            else if (repo.Kind == "github" && !string.IsNullOrWhiteSpace(repo.GhOwner) && !string.IsNullOrWhiteSpace(repo.GhRepo))
            {
                // GitHub API call.
                var ghStatus = req.Status?.ToLowerInvariant() switch
                {
                    "completed" or "merged" => "closed",
                    "all" or null or "" => "all",
                    _ => "open",
                };
                var url = $"repos/{repo.GhOwner}/{repo.GhRepo}/pulls?state={ghStatus}&per_page=100";
                log.LogInformation("PR sync: GitHub GET {Url}", url);

                var client = clientFactory.CreateClient("github");
                client.BaseAddress = new Uri("https://api.github.com/");
                if (bearer is not null)
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                }
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Forge/1.0");

                var resp = await client.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                log.LogInformation("PR sync: GitHub response status={Status} body-length={Len}", (int)resp.StatusCode, body.Length);

                if (!resp.IsSuccessStatusCode)
                {
                    log.LogError("PR sync: GitHub returned {Status}: {Body}", (int)resp.StatusCode, body.Length > 500 ? body[..500] : body);
                    return Results.Problem(title: $"GitHub returned {(int)resp.StatusCode}", statusCode: 502);
                }

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var prs = new List<Pr>();
                foreach (var pr in doc.RootElement.EnumerateArray())
                {
                    prs.Add(GitHubMapper.PrToPr(pr, repo.GhRepo ?? "unknown"));
                }
                remotePrs = prs;
                log.LogInformation("PR sync: GitHub returned {Count} PRs", prs.Count);
            }
            else
            {
                // Fall back to the global provider (original behavior).
                log.LogWarning("PR sync: repo has no provider-specific config (adoOrg={AdoOrg} adoProject={AdoProject}), falling back to global provider",
                    repo.AdoOrg, repo.AdoProject);
                var provider = providers.FirstOrDefault(p => p.Kind == repo.Kind && p.IsConfigured);
                if (provider is null)
                {
                    log.LogError("PR sync: no configured provider for kind '{Kind}'", repo.Kind);
                    return Results.Problem(title: $"Provider '{repo.Kind}' is not configured", statusCode: 400);
                }
                remotePrs = await provider.ListPullRequestsAsync(ct);
                log.LogInformation("PR sync: global provider returned {Count} PRs", remotePrs.Count);

                // Still apply client-side author filter on the mapped results.
                if (!string.IsNullOrWhiteSpace(req.CreatedBy))
                {
                    var author = req.CreatedBy.Trim();
                    remotePrs = remotePrs.Where(p =>
                        (p.Author.Name ?? "").Contains(author, StringComparison.OrdinalIgnoreCase)
                        || (p.Author.Id ?? "").Contains(author, StringComparison.OrdinalIgnoreCase)).ToList();
                    log.LogInformation("PR sync: {Count} PRs after author filter (global fallback)", remotePrs.Count);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "PR sync: provider call failed");
            return Results.Problem(title: $"Provider error: {ex.Message}", statusCode: 502);
        }

        // Upsert into the local DB.
        var inserted = 0;
        var updated = 0;
        foreach (var pr in remotePrs)
        {
            var existing = await db.Prs.FirstOrDefaultAsync(p => p.Id == pr.Id, ct);
            if (existing is null)
            {
                db.Prs.Add(new PrRow
                {
                    Id = pr.Id,
                    Col = pr.Col,
                    Epic = pr.Epic,
                    EpicColor = pr.EpicColor,
                    Workstream = pr.Workstream,
                    Base = pr.Base,
                    Branch = pr.Branch,
                    Title = pr.Title,
                    AuthorJson = CardMapper.Serialize(pr.Author),
                    Additions = pr.Additions,
                    Deletions = pr.Deletions,
                    Files = pr.Files,
                    Commits = pr.Commits,
                    Age = pr.Age,
                    BuildJson = pr.Build is null ? null : CardMapper.Serialize(pr.Build),
                    ReviewJson = pr.Review is null ? null : CardMapper.Serialize(pr.Review),
                    Conflicts = pr.Conflicts,
                    HumanGate = pr.HumanGate,
                    GateReason = pr.GateReason,
                    ConflictPaths = pr.ConflictPaths,
                    AutoMerge = pr.AutoMerge,
                    Blocked = pr.Blocked,
                    ImplJson = pr.Impl is null ? null : CardMapper.Serialize(pr.Impl),
                    IsActive = pr.Col != "merged",
                    Provider = repo.Kind,
                    LastSyncAt = DateTimeOffset.UtcNow.ToString("o"),
                    LastSyncStatus = "idle",
                    RepoId = req.RepoId,
                });
                inserted++;
            }
            else
            {
                existing.Col = pr.Col;
                existing.Title = pr.Title;
                existing.AuthorJson = CardMapper.Serialize(pr.Author);
                existing.Additions = pr.Additions;
                existing.Deletions = pr.Deletions;
                existing.Files = pr.Files;
                existing.Commits = pr.Commits;
                existing.Age = pr.Age;
                existing.BuildJson = pr.Build is null ? null : CardMapper.Serialize(pr.Build);
                existing.ReviewJson = pr.Review is null ? null : CardMapper.Serialize(pr.Review);
                existing.Conflicts = pr.Conflicts;
                existing.HumanGate = pr.HumanGate;
                existing.IsActive = pr.Col != "merged";
                existing.LastSyncAt = DateTimeOffset.UtcNow.ToString("o");
                existing.LastSyncStatus = "idle";
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("PR sync: completed — pulled={Pulled} inserted={Inserted} updated={Updated}", remotePrs.Count, inserted, updated);

        await hub.Clients.All.SendAsync("pr.changed", new { kind = "synced", inserted, updated }, ct);
        return Results.Ok(new { pulled = remotePrs.Count, inserted, updated });
    }

    private static IResult NotFound(string id) =>
        Results.Problem(title: $"PR {id} not found", statusCode: 404);

    /// <summary>
    /// Build the canonical ADO org URL from the repo row's AdoOrg.
    /// Accepts either a bare org name or a full URL.
    /// </summary>
    private static string BuildAdoOrgUrl(string adoOrg)
    {
        var trimmed = adoOrg.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.TrimEnd('/');
        }
        return $"https://dev.azure.com/{trimmed.Trim('/')}";
    }
}

public sealed record PrSyncRequest(
    string RepoId,
    string Provider,
    string? CreatedBy = null,
    string? Status = null,
    string? FromDate = null,
    string? ToDate = null);
