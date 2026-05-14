// -----------------------------------------------------------------------
// <copyright file="GitHubPrCommentSyncClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 24 — GitHub PR + comment sync client. Walks two REST streams
/// (review comments + issue comments), merges by id, and reads thread
/// resolution via a single GraphQL query (the only GraphQL call in the
/// codebase — REST doesn't expose <c>isResolved</c>).
/// </summary>
public sealed class GitHubPrCommentSyncClient : IPrCommentSyncClient
{
    public const string ClientName = "github-pr-sync";

    private readonly IHttpClientFactory clientFactory;
    private readonly GitHubOptions options;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IEnumerable<IAuthService> authServices;
    private readonly ILogger<GitHubPrCommentSyncClient> logger;

    public GitHubPrCommentSyncClient(
        IHttpClientFactory clientFactory,
        IOptions<GitHubOptions> options,
        IServiceScopeFactory scopeFactory,
        IEnumerable<IAuthService> authServices,
        ILogger<GitHubPrCommentSyncClient> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.scopeFactory = scopeFactory;
        this.authServices = authServices;
        this.logger = logger;
    }

    public string Provider => "github";

    // 2026-05-14 — mirror AdoPrCommentSyncClient.IsConfigured: comment sync
    // can authenticate either via the legacy Forge:GitHub:Pat config
    // or via the gh-CLI-backed GitHubAuthService registered as an
    // IAuthService. Previously this checked options.Pat + options.Repo
    // only, which silently disabled sync for every deployment that
    // relied on `gh auth login` (the documented happy path) — every
    // GitHub PR opened by Forge stalled at LastSyncStatus='failed'
    // with "Sync client for provider 'github' not configured", and the
    // local PrRow / Workstream.PrsJson mirrors never refreshed after a
    // PR was opened or auto-merged (observed on PR smartpcr/qq#14
    // 2026-05-14: closed+merged on GitHub for ~10 h while the Forge UI
    // still showed the workstream as ACTIVE iter 4/50).
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.options.Pat)
        || this.authServices.Any(a => string.Equals(a.Kind, "github", StringComparison.OrdinalIgnoreCase));

    public async Task<PrSyncSnapshot> FetchAsync(PrRow pr, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pr.ProviderRef))
        {
            return new PrSyncSnapshot(pr.Col, pr.LastUpstreamUpdatedAt, Array.Empty<PrSyncComment>(), false);
        }

        var (owner, repo) = await this.ResolveRepoAsync(pr, ct);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            return new PrSyncSnapshot(pr.Col, pr.LastUpstreamUpdatedAt, Array.Empty<PrSyncComment>(), false);
        }

        var num = pr.ProviderRef.TrimStart('#').Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase);

        using var http = await this.NewClientAsync(ct);
        var prResp = await http.GetAsync($"repos/{owner}/{repo}/pulls/{num}", ct);
        if (!prResp.IsSuccessStatusCode)
        {
            this.logger.LogWarning("GitHub PR fetch failed: PR={Pr} status={Status}", pr.Id, prResp.StatusCode);
            return new PrSyncSnapshot(pr.Col, pr.LastUpstreamUpdatedAt, Array.Empty<PrSyncComment>(), false);
        }

        await this.RespectRateLimitAsync(prResp, ct);

        using var prDoc = JsonDocument.Parse(await prResp.Content.ReadAsStringAsync(ct));
        var rawState = prDoc.RootElement.TryGetProperty("state", out var s) ? s.GetString() ?? "open" : "open";
        var mergedAt = prDoc.RootElement.TryGetProperty("merged_at", out var ma) && ma.ValueKind != JsonValueKind.Null
            ? ma.GetString()
            : null;
        // Translate to provider-state used by MapUpstreamState.
        var providerState = rawState switch
        {
            "open" => "open",
            "closed" when !string.IsNullOrEmpty(mergedAt) => "merged",
            "closed" => "closed",
            _ => rawState,
        };
        var canonical = PrSyncJob.MapUpstreamState("github", providerState);
        var prUpdatedAt = prDoc.RootElement.TryGetProperty("updated_at", out var ua) ? ua.GetString() : null;

        // Phase 49 follow-up — capture the PR's running file/commit/diff
        // stats from the SAME response we're already parsing. GitHub
        // returns these on GET /pulls/{n} alongside head/base. Without
        // this, PrRow.Commits / Files / Additions / Deletions stayed at
        // whatever was set at PR-creation time and never reflected the
        // engineer's review-fix commits (observed 2026-05-10 on PR
        // smartpcr/failover-cluster#3: GitHub showed 5 commits after
        // review-fix pushed 3 reply commits, but the Forge PR header
        // still read "2 commits" — the original CreatePullRequest count).
        // Parsing into nullables so the PrSyncJob's existing
        // `if (snapshot.X.HasValue)` write guards still work.
        int? commitsCount = null;
        int? filesChanged = null;
        int? additions = null;
        int? deletions = null;
        if (prDoc.RootElement.TryGetProperty("commits", out var cEl) && cEl.ValueKind == JsonValueKind.Number) commitsCount = cEl.GetInt32();
        if (prDoc.RootElement.TryGetProperty("changed_files", out var fEl) && fEl.ValueKind == JsonValueKind.Number) filesChanged = fEl.GetInt32();
        if (prDoc.RootElement.TryGetProperty("additions", out var aEl) && aEl.ValueKind == JsonValueKind.Number) additions = aEl.GetInt32();
        if (prDoc.RootElement.TryGetProperty("deletions", out var dEl) && dEl.ValueKind == JsonValueKind.Number) deletions = dEl.GetInt32();

        // Capture base + source branch so the local PrRow.Base / .Branch
        // don't go stale when the operator retargets the PR on GitHub.
        // Without this, the merge endpoint's pre-rebase fetches the wrong
        // base, the local merge succeeds against a stale target, and the
        // PUT pulls/N/merge keeps 405-ing because GitHub's mergeable check
        // runs against the current base.
        string? baseRef = null;
        string? headRef = null;
        if (prDoc.RootElement.TryGetProperty("base", out var baseEl)
            && baseEl.ValueKind == JsonValueKind.Object
            && baseEl.TryGetProperty("ref", out var baseRefEl)
            && baseRefEl.ValueKind == JsonValueKind.String)
        {
            baseRef = baseRefEl.GetString();
        }
        if (prDoc.RootElement.TryGetProperty("head", out var headEl)
            && headEl.ValueKind == JsonValueKind.Object
            && headEl.TryGetProperty("ref", out var headRefEl)
            && headRefEl.ValueKind == JsonValueKind.String)
        {
            headRef = headRefEl.GetString();
        }

        // Skip the comment fetch entirely when nothing has changed —
        // but still echo back the freshly-read stats so the PR header
        // counters refresh even when no new comments landed (engineer
        // pushed a fix commit without comment activity).
        if (!string.IsNullOrEmpty(pr.LastUpstreamUpdatedAt)
            && string.Equals(prUpdatedAt, pr.LastUpstreamUpdatedAt, StringComparison.Ordinal))
        {
            return new PrSyncSnapshot(canonical, prUpdatedAt, Array.Empty<PrSyncComment>(), false,
                Files: filesChanged,
                Commits: commitsCount,
                Additions: additions,
                Deletions: deletions,
                BaseBranch: baseRef, SourceBranch: headRef);
        }

        var since = string.IsNullOrEmpty(pr.LastUpstreamUpdatedAt) ? null : pr.LastUpstreamUpdatedAt;
        var pageSize = SyncConstants.PrCommentPageSize;
        var agentLogins = await this.LoadAgentLoginsAsync(ct);

        var reviewCommentsTask = FetchReviewCommentsAsync(http, owner, repo, num, since, pageSize, agentLogins, ct);
        var issueCommentsTask = FetchIssueCommentsAsync(http, owner, repo, num, since, pageSize, agentLogins, ct);
        await Task.WhenAll(reviewCommentsTask, issueCommentsTask);

        // Merge by id (comments live in two streams; ids don't collide between PR review and issue).
        var byId = new Dictionary<string, PrSyncComment>();
        foreach (var c in reviewCommentsTask.Result) byId[c.ProviderCommentId] = c;
        foreach (var c in issueCommentsTask.Result)
        {
            if (!byId.ContainsKey(c.ProviderCommentId)) byId[c.ProviderCommentId] = c;
        }

        // Resolve thread state via GraphQL — each review comment belongs to a thread.
        try
        {
            var resolved = await this.FetchReviewThreadResolutionAsync(http, owner, repo, num, ct);
            for (var i = 0; i < byId.Count; i++)
            {
                var key = byId.Keys.ElementAt(i);
                var c = byId[key];
                if (resolved.TryGetValue(c.ProviderCommentId, out var isResolved) && isResolved)
                {
                    byId[key] = c with { State = "resolved" };
                }
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "GitHub reviewThreads GraphQL probe failed; thread resolution skipped");
        }

        return new PrSyncSnapshot(canonical, prUpdatedAt, byId.Values.ToList(), true,
            Files: filesChanged,
            Commits: commitsCount,
            Additions: additions,
            Deletions: deletions,
            BaseBranch: baseRef, SourceBranch: headRef);
    }

    private static async Task<List<PrSyncComment>> FetchReviewCommentsAsync(
        HttpClient http, string owner, string repo, string num, string? since, int pageSize,
        IReadOnlyDictionary<string, string> agentLogins, CancellationToken ct)
    {
        var url = $"repos/{owner}/{repo}/pulls/{num}/comments?per_page={pageSize}";
        if (!string.IsNullOrEmpty(since)) url += $"&since={Uri.EscapeDataString(since)}";
        var results = new List<PrSyncComment>();

        while (!string.IsNullOrEmpty(url))
        {
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) break;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                results.Add(MapGithubComment(c, isInline: true, agentLogins));
            }
            url = ParseNextLink(resp);
        }
        return results;
    }

    private static async Task<List<PrSyncComment>> FetchIssueCommentsAsync(
        HttpClient http, string owner, string repo, string num, string? since, int pageSize,
        IReadOnlyDictionary<string, string> agentLogins, CancellationToken ct)
    {
        var url = $"repos/{owner}/{repo}/issues/{num}/comments?per_page={pageSize}";
        if (!string.IsNullOrEmpty(since)) url += $"&since={Uri.EscapeDataString(since)}";
        var results = new List<PrSyncComment>();

        while (!string.IsNullOrEmpty(url))
        {
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) break;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                results.Add(MapGithubComment(c, isInline: false, agentLogins));
            }
            url = ParseNextLink(resp);
        }
        return results;
    }

    private static PrSyncComment MapGithubComment(JsonElement c, bool isInline, IReadOnlyDictionary<string, string> agentLogins)
    {
        var id = c.GetProperty("id").GetInt64().ToString();
        var body = c.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
        var created = c.TryGetProperty("created_at", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var d1) ? d1 : DateTimeOffset.UtcNow;
        var updated = c.TryGetProperty("updated_at", out var ua) && DateTimeOffset.TryParse(ua.GetString(), out var d2) ? d2 : created;

        string? login = null;
        if (c.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object &&
            user.TryGetProperty("login", out var lg))
        {
            login = lg.GetString();
        }
        var authorKind = ResolveAuthorKind(login, agentLogins);

        string? path = null;
        int? line = null;
        if (isInline)
        {
            if (c.TryGetProperty("path", out var p)) path = p.GetString();
            if (c.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Number) line = ln.GetInt32();
        }

        string? parent = null;
        if (c.TryGetProperty("in_reply_to_id", out var rt) && rt.ValueKind == JsonValueKind.Number)
        {
            parent = rt.GetInt64().ToString();
        }

        // GitHub doesn't expose a per-comment thread id at REST; reuse the id
        // for inline comments (one-comment threads) or pull_request_review_id
        // when present. For issue comments the body sits in a single PR-level
        // thread; we synthesise a stable thread id per PR for that channel.
        string threadId = id;
        if (isInline && c.TryGetProperty("pull_request_review_id", out var prr) && prr.ValueKind == JsonValueKind.Number)
        {
            threadId = $"review-{prr.GetInt64()}";
        }
        else if (!isInline)
        {
            threadId = "issue-thread";
        }

        return new PrSyncComment(
            ProviderCommentId: id,
            ThreadId: threadId,
            AuthorId: login,
            AuthorKind: authorKind,
            Body: body,
            FilePath: path,
            Line: line,
            State: "active",
            CreatedAt: created,
            UpdatedAt: updated,
            ResolvedByUserId: null,
            ResolvedAt: null,
            ParentProviderCommentId: parent);
    }

    private static string ResolveAuthorKind(string? login, IReadOnlyDictionary<string, string> agentLogins)
    {
        if (string.IsNullOrEmpty(login)) return "human";
        if (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)) return "system";
        if (agentLogins.ContainsKey(login.ToLowerInvariant())) return "agent";
        return "human";
    }

    private static string? ParseNextLink(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var v in values)
        {
            // Parse: <url>; rel="next", <url>; rel="last"
            var parts = v.Split(',');
            foreach (var part in parts)
            {
                if (part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
                {
                    var lt = part.IndexOf('<');
                    var gt = part.IndexOf('>');
                    if (lt >= 0 && gt > lt)
                    {
                        var full = part.Substring(lt + 1, gt - lt - 1);
                        var apiBase = "https://api.github.com/";
                        if (full.StartsWith(apiBase, StringComparison.OrdinalIgnoreCase))
                        {
                            return full.Substring(apiBase.Length);
                        }
                        return full;
                    }
                }
            }
        }
        return null;
    }

    private async Task<Dictionary<string, bool>> FetchReviewThreadResolutionAsync(
        HttpClient http, string owner, string repo, string num, CancellationToken ct)
    {
        var query = """
        query($owner:String!, $repo:String!, $num:Int!) {
          repository(owner:$owner, name:$repo) {
            pullRequest(number:$num) {
              reviewThreads(first:100) {
                nodes {
                  isResolved
                  comments(first:100) { nodes { databaseId } }
                }
              }
            }
          }
        }
        """;
        var body = JsonSerializer.Serialize(new { query, variables = new { owner, repo, num = int.Parse(num) } });
        using var req = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.RequestUri = new Uri("https://api.github.com/graphql");

        var resp = await http.SendAsync(req, ct);
        var result = new Dictionary<string, bool>();
        if (!resp.IsSuccessStatusCode) return result;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("repository", out var rp) ||
            rp.ValueKind != JsonValueKind.Object ||
            !rp.TryGetProperty("pullRequest", out var p) ||
            p.ValueKind != JsonValueKind.Object ||
            !p.TryGetProperty("reviewThreads", out var rts)) return result;

        foreach (var t in rts.GetProperty("nodes").EnumerateArray())
        {
            var isResolved = t.TryGetProperty("isResolved", out var ir) && ir.GetBoolean();
            foreach (var c in t.GetProperty("comments").GetProperty("nodes").EnumerateArray())
            {
                if (c.TryGetProperty("databaseId", out var did) && did.ValueKind == JsonValueKind.Number)
                {
                    result[did.GetInt64().ToString()] = isResolved;
                }
            }
        }
        return result;
    }

    private async Task RespectRateLimitAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.Forbidden &&
            resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) &&
            int.TryParse(rem.FirstOrDefault(), out var remaining) && remaining < 100)
        {
            if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
                long.TryParse(reset.FirstOrDefault(), out var resetEpoch))
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
                var wait = when - DateTimeOffset.UtcNow;
                if (wait.TotalSeconds > 0 && wait.TotalMinutes < 60)
                {
                    this.logger.LogInformation("GitHub rate-limited; waiting {Sec}s for reset", (int)wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                }
            }
        }
    }

    private async Task<(string Owner, string Repo)> ResolveRepoAsync(PrRow pr, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(pr.RepoId))
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var repoRow = await db.Set<RepoRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == pr.RepoId, ct);
            if (repoRow is not null && !string.IsNullOrEmpty(repoRow.GhOwner) && !string.IsNullOrEmpty(repoRow.GhRepo))
            {
                return (repoRow.GhOwner!, repoRow.GhRepo!);
            }
        }
        var parts = (this.options.Repo ?? string.Empty).Split('/', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (string.Empty, string.Empty);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadAgentLoginsAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var agents = await db.Agents.AsNoTracking().Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents)
        {
            if (!string.IsNullOrEmpty(a.Id)) dict[a.Id.ToLowerInvariant()] = a.Id;
            if (!string.IsNullOrEmpty(a.Name)) dict[a.Name.ToLowerInvariant()] = a.Id;
        }
        return dict;
    }

    private async Task<HttpClient> NewClientAsync(CancellationToken ct)
    {
        var client = this.clientFactory.CreateClient(ClientName);
        var apiBase = string.IsNullOrEmpty(this.options.ApiBase) ? "https://api.github.com" : this.options.ApiBase;
        client.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");

        // 2026-05-14 — resolve auth in priority order: configured PAT
        // (legacy / CI path) → gh-CLI bearer via IAuthService (the
        // documented dev path). Mirrors AdoPrCommentSyncClient.NewClient.
        // Anonymous requests still work for public repos (rate-limited
        // to 60 req/h instead of 5000), so we don't fail the call when
        // both paths come back empty — let the request go and the
        // status-code handler upstream decides what to do.
        string? bearer = null;
        if (!string.IsNullOrWhiteSpace(this.options.Pat))
        {
            bearer = this.options.Pat;
        }
        else
        {
            var auth = this.authServices.FirstOrDefault(a => string.Equals(a.Kind, "github", StringComparison.OrdinalIgnoreCase));
            if (auth is not null)
            {
                try
                {
                    bearer = await auth.GetAccessTokenAsync(ct);
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "GitHub comment sync: failed to acquire bearer token from gh CLI");
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("forge-pr-sync", "1.0"));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
