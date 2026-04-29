// -----------------------------------------------------------------------
// <copyright file="AdoPrCommentSyncClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    private readonly ILogger<AdoPrCommentSyncClient> logger;

    public AdoPrCommentSyncClient(
        IHttpClientFactory clientFactory,
        IOptions<AdoOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<AdoPrCommentSyncClient> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public string Provider => "ado";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.options.OrgUrl)
        && !string.IsNullOrWhiteSpace(this.options.Project)
        && !string.IsNullOrWhiteSpace(this.options.Pat);

    public async Task<PrSyncSnapshot> FetchAsync(PrRow pr, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pr.ProviderRef))
        {
            return new PrSyncSnapshot(pr.Col, pr.LastUpstreamUpdatedAt, Array.Empty<PrSyncComment>(), false);
        }

        var (project, repo) = await this.ResolveRepoAsync(pr, ct);
        if (string.IsNullOrEmpty(project) || string.IsNullOrEmpty(repo))
        {
            return new PrSyncSnapshot(pr.Col, pr.LastUpstreamUpdatedAt, Array.Empty<PrSyncComment>(), false);
        }

        var num = pr.ProviderRef.TrimStart('#').Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase);

        using var http = this.NewClient();
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
            return new PrSyncSnapshot(canonical, lastUpdated, Array.Empty<PrSyncComment>(), false);
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

        return new PrSyncSnapshot(canonical, lastUpdated, comments, true);
    }

    /// <summary>
    /// Maps ADO <c>commentThread.status</c> to canonical comment state.
    /// </summary>
    internal static string MapAdoStatus(string status) => status.ToLowerInvariant() switch
    {
        "active" => "active",
        "fixed" or "closed" or "bydesign" => "resolved",
        "wontfix" => "wont-fix",
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

    private async Task<(string Project, string Repo)> ResolveRepoAsync(PrRow pr, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(pr.RepoId))
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var repoRow = await db.Set<RepoRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == pr.RepoId, ct);
            if (repoRow is not null && !string.IsNullOrEmpty(repoRow.AdoProject))
            {
                return (repoRow.AdoProject!, ExtractRepoFromUrl(repoRow.RemoteUrl) ?? this.options.Repository ?? string.Empty);
            }
        }
        return (this.options.Project, this.options.Repository ?? string.Empty);
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

    private HttpClient NewClient()
    {
        var client = this.clientFactory.CreateClient(ClientName);
        if (!string.IsNullOrWhiteSpace(this.options.OrgUrl))
        {
            client.BaseAddress = new Uri(this.options.OrgUrl.TrimEnd('/') + "/");
        }
        if (!string.IsNullOrWhiteSpace(this.options.Pat))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{this.options.Pat}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
