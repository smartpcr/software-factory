// -----------------------------------------------------------------------
// <copyright file="GitHubProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.GitHub;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Providers.Ado;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 5.3 — REST-based GitHub provider. Uses a named HttpClient
/// (named "github") so tests can swap the message handler.
/// </summary>
public class GitHubProvider : IForgeProvider
{
    public const string ClientName = "github";
    private const string DefaultUserAgent = "forge-api/0.0";

    private readonly IHttpClientFactory clientFactory;
    private readonly GitHubOptions options;
    private readonly IAuthService? ghAuth;
    private readonly ILogger<GitHubProvider> logger;
    private readonly ProviderHealthState health = new("github");

    public GitHubProvider(
        IHttpClientFactory clientFactory,
        IOptions<GitHubOptions> options,
        IEnumerable<IAuthService> authServices,
        ILogger<GitHubProvider> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.ghAuth = authServices.FirstOrDefault(s => s.Kind == "github");
        this.logger = logger;
    }

    public string Kind => "github";

    /// <summary>
    /// Repo-scoped operations require both a configured repo and an
    /// authenticated client. With the gh-CLI auth path the PAT is resolved
    /// dynamically per-call, so callers should generally rely on
    /// <see cref="HasAuthAsync"/> for the "do we have a token" question
    /// and only check this flag when a specific <c>options.Repo</c> matters.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.options.Repo) &&
        !string.IsNullOrWhiteSpace(this.options.Pat);

    /// <summary>True when we can produce a bearer token (PAT or gh CLI).</summary>
    private async Task<bool> HasAuthAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(this.options.Pat)) return true;
        if (this.ghAuth is null) return false;
        var token = await this.ghAuth.GetAccessTokenAsync(ct);
        return !string.IsNullOrEmpty(token);
    }

    /// <summary>Resolves the bearer token: PAT first, gh CLI fallback.</summary>
    private async Task<string?> ResolveTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(this.options.Pat)) return this.options.Pat;
        if (this.ghAuth is null) return null;
        return await this.ghAuth.GetAccessTokenAsync(ct);
    }

    public async Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured) return Array.Empty<Card>();
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"repos/{this.options.Repo}/issues?state=all&per_page=100", ct);
            this.health.RecordResponse(resp);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var cards = new List<Card>();
            foreach (var issue in doc.RootElement.EnumerateArray())
            {
                // The /issues endpoint includes PRs with a `pull_request` key — skip those here.
                if (issue.TryGetProperty("pull_request", out _)) continue;
                cards.Add(GitHubMapper.IssueToCard(issue, this.options.Repo));
            }
            this.health.MarkSync();
            return cards;
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "GitHub ListWorkItemsAsync failed");
            return Array.Empty<Card>();
        }
    }

    public async Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default)
    {
        if (!this.IsConfigured) return null;
        var num = id.TrimStart('#');
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"repos/{this.options.Repo}/issues/{num}", ct);
            this.health.RecordResponse(resp);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return GitHubMapper.IssueToCard(doc.RootElement, this.options.Repo);
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "GitHub GetWorkItemAsync({Id}) failed", id);
            return null;
        }
    }

    public async Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured) return Array.Empty<Pr>();
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"repos/{this.options.Repo}/pulls?state=open&per_page=100", ct);
            this.health.RecordResponse(resp);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var prs = new List<Pr>();
            foreach (var pr in doc.RootElement.EnumerateArray())
            {
                prs.Add(GitHubMapper.PrToPr(pr, this.options.Repo));
            }
            this.health.MarkSync();
            return prs;
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "GitHub ListPullRequestsAsync failed");
            return Array.Empty<Pr>();
        }
    }

    public async Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default)
    {
        if (!this.IsConfigured) return null;
        var num = id.Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('#');
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"repos/{this.options.Repo}/pulls/{num}", ct);
            this.health.RecordResponse(resp);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return GitHubMapper.PrToPr(doc.RootElement, this.options.Repo);
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "GitHub GetPullRequestAsync({Id}) failed", id);
            return null;
        }
    }

    public async Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default)
    {
        if (!this.IsConfigured) return null;
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync(
                $"repos/{this.options.Repo}/commits/{Uri.EscapeDataString(branch)}/check-runs", ct);
            this.health.RecordResponse(resp);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("check_runs", out var arr) || arr.GetArrayLength() == 0) return null;

            // Aggregate: any failing → failing; any in-progress → running; else passing.
            var anyFailing = false;
            var anyRunning = false;
            var lastStep = "ci";
            foreach (var run in arr.EnumerateArray())
            {
                var status = run.TryGetProperty("status", out var s) ? s.GetString() : null;
                var conclusion = run.TryGetProperty("conclusion", out var c) ? c.GetString() : null;
                lastStep = run.TryGetProperty("name", out var n) ? n.GetString() ?? lastStep : lastStep;
                if (status == "in_progress" || status == "queued") anyRunning = true;
                if (conclusion is "failure" or "timed_out" or "cancelled") anyFailing = true;
            }
            var statusName = anyFailing ? "failing" : anyRunning ? "running" : "passing";
            var pct = statusName == "passing" ? 100 : statusName == "running" ? 50 : 100;
            return new BuildStatus(statusName, lastStep, pct, null);
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "GitHub GetLatestBuildAsync({Branch}) failed", branch);
            return null;
        }
    }

    /// <summary>
    /// Phase 8.5 — list repositories the configured PAT can see.
    /// Walks <c>GET /user/repos</c> (which covers personal + org repos visible
    /// to the token) up to 100 entries. Falls back to the configured single
    /// repo when the call fails so the picker still has at least one entry.
    /// </summary>
    public async Task<IReadOnlyList<DiscoverableRepo>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        // Repo discovery works with just a token — no need for a configured
        // default repo. This is the path the UI's "Discover" button uses.
        var client = await this.NewClientAsync(ct);
        if (client is null) return Array.Empty<DiscoverableRepo>();
        try
        {
            var resp = await client.GetAsync("user/repos?per_page=100&sort=pushed", ct);
            this.health.RecordResponse(resp);
            if (!resp.IsSuccessStatusCode) return Array.Empty<DiscoverableRepo>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var list = new List<DiscoverableRepo>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var fullName = r.GetProperty("full_name").GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(fullName)) continue;
                var slash = fullName.IndexOf('/');
                if (slash <= 0) continue;
                var owner = fullName[..slash];
                var name = fullName[(slash + 1)..];
                var clone = r.TryGetProperty("clone_url", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                var defaultBranch = r.TryGetProperty("default_branch", out var db) ? db.GetString() ?? "main" : "main";
                if (string.IsNullOrEmpty(clone)) continue;
                list.Add(new DiscoverableRepo(
                    Kind: "github",
                    DisplayName: fullName,
                    RemoteUrl: clone,
                    DefaultBranch: defaultBranch,
                    GhOwner: owner,
                    GhRepo: name));
            }
            return list;
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "GitHub ListRepositoriesAsync failed");
            return Array.Empty<DiscoverableRepo>();
        }
    }

    public async Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default)
    {
        // Three-state probe:
        //   1) repo + token  → probe /repos/{owner/repo} (most informative)
        //   2) token only    → probe /user (verifies the token works)
        //   3) no token      → "not connected — sign in via gh or set Forge:GitHub:Pat"
        var client = await this.NewClientAsync(ct);
        if (client is null)
        {
            return new ProviderHealth(this.Kind, false,
                "no token — sign in via gh CLI or set Forge:GitHub:Pat", null, 0, null, null);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(this.options.Repo))
            {
                var resp = await client.GetAsync($"repos/{this.options.Repo}", ct);
                this.health.RecordResponse(resp);
                return resp.IsSuccessStatusCode
                    ? this.health.Snapshot(true, $"connected to {this.options.Repo}")
                    : this.health.Snapshot(false, $"HTTP {(int)resp.StatusCode}");
            }

            // Token-only: probe /user. Surfaces the resolved login back to the
            // health card so the operator sees which account the token belongs to.
            var userResp = await client.GetAsync("user", ct);
            this.health.RecordResponse(userResp);
            if (!userResp.IsSuccessStatusCode)
            {
                return this.health.Snapshot(false, $"HTTP {(int)userResp.StatusCode}");
            }
            using var doc = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync(ct));
            var login = doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
            return this.health.Snapshot(true,
                login is null
                    ? "connected (no default repo set)"
                    : $"connected as {login} (set a default repo to enable sync)");
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            return this.health.Snapshot(false, ex.Message);
        }
    }

    // -- Phase 6 write surface ----------------------------------------------

    public async Task<Card> CreateWorkItemAsync(NewWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var labels = req.Tags ?? Array.Empty<string>();
        // Map our `type` to a label so the round-trip via IssueToCard preserves it.
        var typeLabel = req.Type.ToLowerInvariant() switch
        {
            "bug" => "bug",
            "task" => "task",
            _ => "story",
        };
        var allLabels = labels.Append(typeLabel).Distinct().ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            title = req.Title,
            body = req.Description,
            labels = allLabels,
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"repos/{this.options.Repo}/issues")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return GitHubMapper.IssueToCard(doc.RootElement, this.options.Repo);
    }

    public async Task<Card> UpdateWorkItemAsync(string id, UpdateWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.TrimStart('#');
        var fields = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(req.Title))   fields["title"] = req.Title;
        if (!string.IsNullOrEmpty(req.State))   fields["state"] = req.State.ToLowerInvariant() == "done" ? "closed" : "open";
        if (req.Tags is not null)               fields["labels"] = req.Tags;
        if (!string.IsNullOrEmpty(req.AssigneeUniqueName)) fields["assignees"] = new[] { req.AssigneeUniqueName };
        if (fields.Count == 0)
        {
            var fresh = await this.GetWorkItemAsync(id, ct);
            return fresh ?? throw new InvalidOperationException($"Issue {id} not found");
        }
        var payload = JsonSerializer.Serialize(fields);
        using var msg = new HttpRequestMessage(new HttpMethod("PATCH"), $"repos/{this.options.Repo}/issues/{num}")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return GitHubMapper.IssueToCard(doc.RootElement, this.options.Repo);
    }

    public async Task CommentOnWorkItemAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.TrimStart('#');
        var payload = JsonSerializer.Serialize(new { body });
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"repos/{this.options.Repo}/issues/{num}/comments")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<Pr> CreatePullRequestAsync(NewPullRequestRequest req, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var payload = JsonSerializer.Serialize(new
        {
            title = req.Title,
            body = req.Body,
            head = req.Branch,
            @base = req.BaseBranch,
            draft = req.Draft,
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"repos/{this.options.Repo}/pulls")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return GitHubMapper.PrToPr(doc.RootElement, this.options.Repo);
    }

    public Task ApprovePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) =>
        this.PostReviewAsync(id, "APPROVE", body: null, ctx, ct);

    public Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
        this.PostReviewAsync(id, "REQUEST_CHANGES", body, ctx, ct);

    public async Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('#');
        var payload = JsonSerializer.Serialize(new { body });
        // GitHub treats comments on issue threads of PRs the same as issue comments.
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"repos/{this.options.Repo}/issues/{num}/comments")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<Pr> MergePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('#');
        var payload = JsonSerializer.Serialize(new { merge_method = "squash" });
        using var msg = new HttpRequestMessage(HttpMethod.Put, $"repos/{this.options.Repo}/pulls/{num}/merge")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
        var fresh = await this.GetPullRequestAsync(id, ct);
        return fresh ?? throw new InvalidOperationException($"PR {id} not found after merge");
    }

    public async Task<BuildStatus?> TriggerBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        // `workflow_dispatch` requires a workflow filename — we POST to the
        // generic dispatches endpoint with branch ref. Without a configured
        // workflow we return a "queued" stub so callers see optimistic state.
        var payload = JsonSerializer.Serialize(new { @ref = branch });
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"repos/{this.options.Repo}/dispatches")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        if (!resp.IsSuccessStatusCode) return null;
        return new BuildStatus("queued", $"dispatch on {branch}", 0, null);
    }

    public async Task<BuildStatus?> CancelBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        // Find the most-recent in-progress run for the branch and cancel it.
        var listResp = await client.GetAsync(
            $"repos/{this.options.Repo}/actions/runs?branch={Uri.EscapeDataString(branch)}&status=in_progress&per_page=1", ct);
        this.health.RecordResponse(listResp);
        if (!listResp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.GetArrayLength() == 0)
        {
            return new BuildStatus("queued", "no running build to cancel", 0, null);
        }
        var runId = runs[0].GetProperty("id").GetInt64();
        using var cancelMsg = new HttpRequestMessage(HttpMethod.Post,
            $"repos/{this.options.Repo}/actions/runs/{runId}/cancel");
        var cancelResp = await client.SendAsync(cancelMsg, ct);
        this.health.RecordResponse(cancelResp);
        cancelResp.EnsureSuccessStatusCode();
        return new BuildStatus("queued", $"cancel requested for run {runId}", 0, null);
    }

    private async Task PostReviewAsync(string id, string @event, string? body, ProviderCallContext ctx, CancellationToken ct)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('#');
        var payload = JsonSerializer.Serialize(new { body, @event });
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"repos/{this.options.Repo}/pulls/{num}/reviews")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
    }

    private void RequireConfigured()
    {
        if (!this.IsConfigured)
        {
            throw new InvalidOperationException("GitHub provider is not configured (Forge:GitHub:Repo / Pat)");
        }
    }

    private HttpClient NewClient(ProviderCallContext ctx)
    {
        // Per-call user PAT wins (e.g. write actions where the calling user's
        // token must be used for attribution). Otherwise the shared singleton
        // client carries whatever token NewClient() configured.
        var client = this.NewClient();
        if (!string.IsNullOrEmpty(ctx.UserPat))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", ctx.UserPat);
        }
        return client;
    }

    private HttpClient NewClient(string? token = null)
    {
        var client = this.clientFactory.CreateClient(ClientName);
        if (client.BaseAddress is null)
        {
            var apiBase = string.IsNullOrWhiteSpace(this.options.ApiBase)
                ? "https://api.github.com"
                : this.options.ApiBase.TrimEnd('/');
            client.BaseAddress = new Uri(apiBase + "/");
        }
        var effective = token ?? this.options.Pat;
        if (!string.IsNullOrEmpty(effective))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("token", effective);
        }
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        }
        return client;
    }

    /// <summary>
    /// Builds a client with the resolved bearer token (PAT, then gh CLI
    /// fallback). Returns null when no token is available — callers that
    /// previously short-circuited on <see cref="IsConfigured"/> should
    /// short-circuit on this null.
    /// </summary>
    private async Task<HttpClient?> NewClientAsync(CancellationToken ct)
    {
        var token = await this.ResolveTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;
        return this.NewClient(token);
    }
}
