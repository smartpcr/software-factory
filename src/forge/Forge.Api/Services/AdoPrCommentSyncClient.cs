// -----------------------------------------------------------------------
// <copyright file="AdoPrCommentSyncClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Providers.Ado;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 24 — ADO PR + comment thread sync client.
/// Fetches PR metadata via <c>/pullRequests/{id}</c>, threads via
/// <c>/threads?$top=1000</c>, and maps each <c>commentThread.status</c> to
/// the canonical <see cref="PrSyncComment.State"/> shape.
/// </summary>
public sealed class AdoPrCommentSyncClient : IPrCommentSyncClient
{
    public const string ClientName = "ado-pr-sync";

    private readonly IHttpClientFactory clientFactory;
    private readonly AdoOptions options;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IEnumerable<IAuthService> authServices;
    private readonly ILogger<AdoPrCommentSyncClient> logger;

    public AdoPrCommentSyncClient(
        IHttpClientFactory clientFactory,
        IOptions<AdoOptions> options,
        IServiceScopeFactory scopeFactory,
        IEnumerable<IAuthService> authServices,
        ILogger<AdoPrCommentSyncClient> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.scopeFactory = scopeFactory;
        this.authServices = authServices;
        this.logger = logger;
    }

    public string Provider => "ado";

    /// <summary>
    /// True when comment sync can authenticate against ADO. Either a global
    /// PAT is configured (legacy path) or an interactive sign-in (Phase 9
    /// <see cref="IAuthService"/>) is registered. Per-repo fields are
    /// resolved from <see cref="RepoRow"/> at fetch time so we don't
    /// require <c>options.OrgUrl</c> / <c>options.Project</c> here.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.options.Pat)
        || this.authServices.Any(a => string.Equals(a.Kind, "ado", StringComparison.OrdinalIgnoreCase));

    public async Task<PrSyncSnapshot> FetchAsync(PrRow pr, CancellationToken ct)
    {
        // ProviderRef may be null for story-spawn PRs where the orchestrator
        // stores the ADO number only in the Id field (e.g. "PR #15630125").
        // Fall back to extracting digits from pr.Id.
        var providerRef = pr.ProviderRef;
        if (string.IsNullOrEmpty(providerRef))
        {
            providerRef = new string(pr.Id.Where(char.IsDigit).ToArray());
        }
        if (string.IsNullOrEmpty(providerRef))
        {
            return new PrSyncSnapshot(pr.Col, pr.LastUpstreamUpdatedAt, Array.Empty<PrSyncComment>(), false);
        }

        var (orgUrl, project, repo) = await this.ResolveRepoAsync(pr, ct);
        if (string.IsNullOrEmpty(orgUrl) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(repo))
        {
            this.logger.LogDebug(
                "ADO comment sync: missing repo config for PR={Pr} (orgUrl='{OrgUrl}' project='{Project}' repo='{Repo}')",
                pr.Id, orgUrl, project, repo);
            return new PrSyncSnapshot(pr.Col, pr.LastUpstreamUpdatedAt, Array.Empty<PrSyncComment>(), false);
        }

        // Provider ref may have come in as "PR #123" / "#123" / "123" — normalise to digits.
        var num = new string(providerRef.Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(num)) num = providerRef;

        using var http = await this.NewClientAsync(orgUrl, ct);
        var prResp = await http.GetAsync(
            $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullRequests/{Uri.EscapeDataString(num)}?api-version=7.0",
            ct);
        if (!prResp.IsSuccessStatusCode)
        {
            this.logger.LogWarning("ADO PR fetch failed: PR={Pr} status={Status}", pr.Id, prResp.StatusCode);
            return new PrSyncSnapshot(pr.Col, pr.LastUpstreamUpdatedAt, Array.Empty<PrSyncComment>(), false);
        }

        using var prDoc = JsonDocument.Parse(await prResp.Content.ReadAsStringAsync(ct));
        var rawState = prDoc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "active" : "active";
        var canonical = PrSyncJob.MapUpstreamState("ado", rawState);

        // Capture base + source so the local PrRow.Base / .Branch tracks
        // upstream retargets (e.g. operator changes target from feature/X
        // to main on ADO). Strip the "refs/heads/" prefix.
        static string TrimRef(string? r) => r is null ? string.Empty
            : r.StartsWith("refs/heads/") ? r["refs/heads/".Length..] : r;
        var baseRef = TrimRef(prDoc.RootElement.TryGetProperty("targetRefName", out var tgt) ? tgt.GetString() : null);
        var headRef = TrimRef(prDoc.RootElement.TryGetProperty("sourceRefName", out var src) ? src.GetString() : null);

        var lastUpdated = prDoc.RootElement.TryGetProperty("creationDate", out var cd) ? cd.GetString() : null;
        // ADO doesn't expose lastUpdatedDate at the PR level; fall back to creationDate.
        // The thread endpoint walks regardless because ADO doesn't support delta filters there.

        // Fetch thread + comments
        var threadsResp = await http.GetAsync(
            $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullRequests/{Uri.EscapeDataString(num)}/threads?api-version=7.0&$top=1000",
            ct);
        if (!threadsResp.IsSuccessStatusCode)
        {
            this.logger.LogWarning("ADO threads fetch failed: PR={Pr} status={Status}", pr.Id, threadsResp.StatusCode);
            return new PrSyncSnapshot(canonical, lastUpdated, Array.Empty<PrSyncComment>(), false,
                BaseBranch: string.IsNullOrEmpty(baseRef) ? null : baseRef,
                SourceBranch: string.IsNullOrEmpty(headRef) ? null : headRef);
        }

        using var threadsDoc = JsonDocument.Parse(await threadsResp.Content.ReadAsStringAsync(ct));
        var comments = new List<PrSyncComment>();
        var agentUpns = await this.LoadAgentUpnsAsync(ct);

        foreach (var thread in threadsDoc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (thread.TryGetProperty("isDeleted", out var del) && del.GetBoolean()) continue;

            var threadId = thread.GetProperty("id").GetInt64().ToString();
            var status = thread.TryGetProperty("status", out var st) ? st.GetString() ?? "active" : "active";
            var state = MapAdoStatus(status);

            string? filePath = null;
            int? line = null;
            if (thread.TryGetProperty("threadContext", out var tcx) && tcx.ValueKind == JsonValueKind.Object)
            {
                if (tcx.TryGetProperty("filePath", out var fp)) filePath = fp.GetString();
                if (tcx.TryGetProperty("rightFileStart", out var rfs) && rfs.ValueKind == JsonValueKind.Object &&
                    rfs.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Number)
                {
                    line = ln.GetInt32();
                }
            }

            string? resolvedBy = null;
            DateTimeOffset? resolvedAt = null;
            if (state is "resolved" or "wont-fix" or "closed")
            {
                if (thread.TryGetProperty("lastUpdatedDate", out var lud) && lud.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(lud.GetString(), out var dt)) resolvedAt = dt;
                }
                if (thread.TryGetProperty("identities", out var ids) && ids.ValueKind == JsonValueKind.Object)
                {
                    foreach (var idProp in ids.EnumerateObject())
                    {
                        if (idProp.Value.TryGetProperty("uniqueName", out var un))
                        {
                            resolvedBy = un.GetString();
                            break;
                        }
                    }
                }
            }

            if (!thread.TryGetProperty("comments", out var commentsArr) || commentsArr.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in commentsArr.EnumerateArray())
            {
                if (c.TryGetProperty("isDeleted", out var cd2) && cd2.GetBoolean()) continue;
                var commentId = c.GetProperty("id").GetInt64().ToString();
                var content = c.TryGetProperty("content", out var ct2) ? ct2.GetString() ?? string.Empty : string.Empty;
                var createdRaw = c.TryGetProperty("publishedDate", out var pd) ? pd.GetString() : null;
                var updatedRaw = c.TryGetProperty("lastUpdatedDate", out var lud2) ? lud2.GetString() : createdRaw;
                var created = ParseDate(createdRaw) ?? DateTimeOffset.UtcNow;
                var updated = ParseDate(updatedRaw) ?? created;

                string? authorId = null;
                string authorKind = "human";
                if (c.TryGetProperty("author", out var auth) && auth.ValueKind == JsonValueKind.Object)
                {
                    authorId = auth.TryGetProperty("uniqueName", out var un) ? un.GetString() : null;
                    var commentTypeStr = c.TryGetProperty("commentType", out var ctp) ? ctp.GetString() ?? "text" : "text";
                    authorKind = ResolveAuthorKind(authorId, commentTypeStr, agentUpns);
                }

                string? parent = null;
                if (c.TryGetProperty("parentCommentId", out var pcId) && pcId.ValueKind == JsonValueKind.Number)
                {
                    var parentId = pcId.GetInt64();
                    if (parentId > 0) parent = parentId.ToString();
                }

                comments.Add(new PrSyncComment(
                    ProviderCommentId: $"{threadId}:{commentId}",
                    ThreadId: threadId,
                    AuthorId: authorId,
                    AuthorKind: authorKind,
                    Body: content,
                    FilePath: filePath,
                    Line: line,
                    State: state,
                    CreatedAt: created,
                    UpdatedAt: updated,
                    ResolvedByUserId: resolvedBy,
                    ResolvedAt: resolvedAt,
                    ParentProviderCommentId: parent is null ? null : $"{threadId}:{parent}"));
            }
        }

        return new PrSyncSnapshot(canonical, lastUpdated, comments, true,
            Files: await this.FetchFileCountAsync(http, project, repo, num, ct),
            Commits: await this.FetchCommitCountAsync(http, project, repo, num, ct),
            BaseBranch: string.IsNullOrEmpty(baseRef) ? null : baseRef,
            SourceBranch: string.IsNullOrEmpty(headRef) ? null : headRef);
    }

    /// <summary>
    /// Phase 49 follow-up — count commits on the PR's source branch via
    /// ADO's <c>pullRequests/{id}/commits</c> endpoint. Mirrors the
    /// GitHub client's commit-count plumbing so Forge's PR header
    /// reflects the engineer's review-fix commits in real time. Returns
    /// null on any failure so <see cref="PrSyncJob"/>'s
    /// <c>if (snapshot.Commits.HasValue)</c> guard leaves the existing
    /// value untouched rather than overwriting with zero.
    /// </summary>
    private async Task<int?> FetchCommitCountAsync(
        HttpClient http, string project, string repo, string prNum, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync(
                $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullRequests/{Uri.EscapeDataString(prNum)}/commits?api-version=7.0&$top=1000",
                ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.GetArrayLength();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort fetch of the file count from the PR iterations/changes endpoint.
    /// Also populates commits from the commits endpoint. Returns null on any failure.
    /// </summary>
    private async Task<int?> FetchFileCountAsync(
        HttpClient http, string project, string repo, string prNum, CancellationToken ct)
    {
        try
        {
            // ADO: GET pullRequests/{id}/iterations to count commits, then
            // GET pullRequests/{id}/iterations/{last}/changes for file count.
            var iterResp = await http.GetAsync(
                $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullRequests/{Uri.EscapeDataString(prNum)}/iterations?api-version=7.0",
                ct);
            if (!iterResp.IsSuccessStatusCode) return null;
            using var iterDoc = JsonDocument.Parse(await iterResp.Content.ReadAsStringAsync(ct));
            var iters = iterDoc.RootElement.GetProperty("value");
            if (iters.GetArrayLength() == 0) return null;
            var lastIter = iters[iters.GetArrayLength() - 1];
            var iterId = lastIter.GetProperty("id").GetInt32();

            var changesResp = await http.GetAsync(
                $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullRequests/{Uri.EscapeDataString(prNum)}/iterations/{iterId}/changes?api-version=7.0&$top=1000",
                ct);
            if (!changesResp.IsSuccessStatusCode) return null;
            using var changesDoc = JsonDocument.Parse(await changesResp.Content.ReadAsStringAsync(ct));
            if (changesDoc.RootElement.TryGetProperty("changeEntries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                return entries.GetArrayLength();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps ADO <c>commentThread.status</c> to canonical comment state.
    /// ADO statuses: <c>active | fixed | wontFix | closed | byDesign | pending | unknown</c>.
    /// Forge keeps <c>closed</c> distinct from <c>resolved</c> because ADO's web
    /// UI surfaces them differently — "closed" is the dismissed-without-fix
    /// state shown in <c>pr-comment.png</c>, while "fixed" is the resolved one.
    /// </summary>
    internal static string MapAdoStatus(string status) => status.ToLowerInvariant() switch
    {
        "active" => "active",
        "fixed" or "bydesign" => "resolved",
        "wontfix" => "wont-fix",
        "closed" => "closed",
        "pending" => "pending",
        _ => "active",
    };

    private static DateTimeOffset? ParseDate(string? s) =>
        string.IsNullOrEmpty(s) ? null : DateTimeOffset.TryParse(s, out var dt) ? dt : null;

    private static string ResolveAuthorKind(string? authorId, string commentType, IReadOnlyDictionary<string, string> agentUpns)
    {
        if (string.Equals(commentType, "system", StringComparison.OrdinalIgnoreCase)) return "system";
        if (!string.IsNullOrEmpty(authorId) && agentUpns.ContainsKey(authorId.ToLowerInvariant())) return "agent";
        return "human";
    }

    private async Task<(string OrgUrl, string Project, string Repo)> ResolveRepoAsync(PrRow pr, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(pr.RepoId))
        {
            using var scope = this.scopeFactory.CreateScope();
            // Phase 80.B — this scope has no active product slug, so route the
            // repo lookup through RepoLookup (registry → owning product's DB)
            // rather than an unscoped context (RepoRow moved to the product DB).
            var repoLookup = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.RepoLookup>();
            var repoRow = await repoLookup.GetByIdAsync(pr.RepoId, ct);
            if (repoRow is not null && !string.IsNullOrEmpty(repoRow.AdoProject))
            {
                var orgUrl = !string.IsNullOrEmpty(repoRow.AdoOrg)
                    ? BuildAdoOrgUrl(repoRow.AdoOrg!)
                    : this.options.OrgUrl ?? string.Empty;
                var repoName = ExtractRepoFromUrl(repoRow.RemoteUrl) ?? this.options.Repository ?? string.Empty;
                return (orgUrl, repoRow.AdoProject!, repoName);
            }
        }
        return (this.options.OrgUrl ?? string.Empty, this.options.Project ?? string.Empty, this.options.Repository ?? string.Empty);
    }

    /// <summary>
    /// Mirrors <c>PrsEndpoints.BuildAdoOrgUrl</c>: accepts either a bare org
    /// name (<c>"contoso"</c> → <c>"https://dev.azure.com/contoso"</c>) or an
    /// already-canonical URL.
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

    private static string? ExtractRepoFromUrl(string remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl)) return null;
        // Convention: https://dev.azure.com/{org}/{project}/_git/{repo}
        var idx = remoteUrl.LastIndexOf('/');
        return idx >= 0 ? remoteUrl[(idx + 1)..].Replace(".git", string.Empty) : null;
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadAgentUpnsAsync(CancellationToken ct)
    {
        // Q24.4 — agents marked as 'agent' authorKind via the upstream UPN
        // matching one of the configured agent identities. We treat any agent
        // id prefix in author UPN ("E1@..." or "agent:bot1@...") as a match.
        // The full `PreferredAdoAccount` mapping is Phase 9.11 territory; we
        // return a best-effort case-insensitive lookup here.
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var agents = await control.Agents.AsNoTracking().Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents)
        {
            if (!string.IsNullOrEmpty(a.Id)) dict[a.Id.ToLowerInvariant()] = a.Id;
            if (!string.IsNullOrEmpty(a.Name)) dict[a.Name.ToLowerInvariant()] = a.Id;
        }
        return dict;
    }

    private async Task<HttpClient> NewClientAsync(string orgUrl, CancellationToken ct)
    {
        var client = this.clientFactory.CreateClient(ClientName);
        if (!string.IsNullOrEmpty(orgUrl))
        {
            client.BaseAddress = new Uri(orgUrl.TrimEnd('/') + "/");
        }

        // Prefer Basic-PAT when configured for backwards compat with headless
        // setups; otherwise borrow the bearer token from the interactive ADO
        // sign-in (Phase 9). Without one of these, ADO replies 401 and the
        // sync client silently produces an empty snapshot.
        if (!string.IsNullOrWhiteSpace(this.options.Pat))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{this.options.Pat}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        else
        {
            var auth = this.authServices.FirstOrDefault(a => string.Equals(a.Kind, "ado", StringComparison.OrdinalIgnoreCase));
            if (auth is not null)
            {
                try
                {
                    var bearer = await auth.GetAccessTokenAsync(ct);
                    if (!string.IsNullOrEmpty(bearer))
                    {
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "ADO comment sync: failed to acquire bearer token");
                }
            }
        }

        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
