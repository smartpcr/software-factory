// -----------------------------------------------------------------------
// <copyright file="GitHubBuildSyncRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.GitHub;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.Extensions.Options;

/// <summary>
/// forge_implementation.md Phase 27 — GitHub Actions build sync runner.
/// <para>
/// Endpoints used:
/// </para>
/// <list type="bullet">
/// <item><c>GET /repos/{o}/{r}/actions/runs?branch={branch}&amp;per_page=100</c></item>
/// <item><c>GET /repos/{o}/{r}/actions/runs/{run_id}/jobs?per_page=100</c></item>
/// <item><c>GET /repos/{o}/{r}/actions/jobs/{job_id}/logs</c></item>
/// <item><c>GET /repos/{o}/{r}/actions/runs/{run_id}/artifacts?per_page=100</c></item>
/// </list>
/// <para>
/// Rate-limit handling: when <c>X-RateLimit-Remaining</c> &lt;
/// <see cref="SyncConstants.GithubRateLimitFloor"/>, sleep until
/// <c>X-RateLimit-Reset</c> (capped at
/// <see cref="SyncConstants.MaxRetryAfterSeconds"/>).
/// </para>
/// </summary>
public sealed class GitHubBuildSyncRunner : IBuildSyncRunner
{
    public const string ClientName = "github-build-sync";

    private readonly IHttpClientFactory clientFactory;
    private readonly GitHubOptions options;
    private readonly IEnumerable<IAuthService> authServices;
    private readonly ILogger<GitHubBuildSyncRunner> logger;

    public GitHubBuildSyncRunner(
        IHttpClientFactory clientFactory,
        IOptions<GitHubOptions> options,
        IEnumerable<IAuthService> authServices,
        ILogger<GitHubBuildSyncRunner> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.authServices = authServices;
        this.logger = logger;
    }

    public string Provider => "github";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.options.Pat)
        || this.authServices.Any(a => string.Equals(a.Kind, "github", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<BuildSyncSnapshot>> ListBuildsForBranchAsync(
        RepoRow repo, string branch, CancellationToken ct)
    {
        var (owner, name) = ResolveOwnerRepo(repo);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(branch))
            return Array.Empty<BuildSyncSnapshot>();

        using var http = await this.NewClientAsync(ct);
        var url = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/actions/runs" +
                  $"?branch={Uri.EscapeDataString(branch)}&per_page={SyncConstants.GithubPerPage}";
        var resp = await this.SendWithRetryAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (resp is null || !resp.IsSuccessStatusCode) return Array.Empty<BuildSyncSnapshot>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<BuildSyncSnapshot>();

        var list = new List<BuildSyncSnapshot>(capacity: arr.GetArrayLength());
        foreach (var r in arr.EnumerateArray())
        {
            try { list.Add(MapRun(r, branch)); }
            catch (Exception ex) { this.logger.LogDebug(ex, "GitHub run map failed"); }
        }
        return list;
    }

    public async Task<IReadOnlyList<BuildStageSnapshot>> FetchStageDetailAsync(
        RepoRow repo, string providerBuildId, CancellationToken ct)
    {
        var (owner, name) = ResolveOwnerRepo(repo);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name)) return Array.Empty<BuildStageSnapshot>();
        using var http = await this.NewClientAsync(ct);

        var jobsResp = await this.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get,
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/actions/runs/{Uri.EscapeDataString(providerBuildId)}/jobs?per_page={SyncConstants.GithubPerPage}"),
            ct);
        if (jobsResp is null || !jobsResp.IsSuccessStatusCode) return Array.Empty<BuildStageSnapshot>();

        using var doc = JsonDocument.Parse(await jobsResp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            return Array.Empty<BuildStageSnapshot>();

        var result = new List<BuildStageSnapshot>();
        var order = 0;
        foreach (var j in jobs.EnumerateArray())
        {
            var conclusion = j.TryGetProperty("conclusion", out var ce) ? ce.GetString() : null;
            if (!string.Equals(conclusion, "failure", StringComparison.OrdinalIgnoreCase)) continue;

            var jobId = j.TryGetProperty("id", out var jid)
                ? (jid.ValueKind == JsonValueKind.Number ? jid.GetInt64().ToString() : jid.GetString() ?? string.Empty)
                : string.Empty;
            var jobName = j.TryGetProperty("name", out var jn) ? jn.GetString() ?? string.Empty : string.Empty;
            var status = j.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "completed" : "completed";
            var startedAt = j.TryGetProperty("started_at", out var sta) ? sta.GetString() : null;
            var finishedAt = j.TryGetProperty("completed_at", out var fta) ? fta.GetString() : null;

            string content = string.Empty; int originalBytes = 0;
            if (!string.IsNullOrEmpty(jobId))
            {
                var (logContent, sizeBytes) = await this.FetchLogTailAsync(http, owner, name, jobId, ct);
                content = logContent;
                originalBytes = sizeBytes;
            }

            result.Add(new BuildStageSnapshot(
                ProviderStageRef: jobId,
                StageName: jobName,
                StageOrder: order++,
                State: status,
                Result: conclusion,
                StartedAt: startedAt,
                FinishedAt: finishedAt,
                LogRef: jobId,
                LogContent: content,
                OriginalLogSizeBytes: originalBytes));
        }
        return result;
    }

    public async Task<IReadOnlyList<BuildArtifactSnapshot>> ListArtifactsAsync(
        RepoRow repo, string providerBuildId, CancellationToken ct)
    {
        var (owner, name) = ResolveOwnerRepo(repo);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name)) return Array.Empty<BuildArtifactSnapshot>();
        using var http = await this.NewClientAsync(ct);

        var resp = await this.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get,
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/actions/runs/{Uri.EscapeDataString(providerBuildId)}/artifacts?per_page={SyncConstants.GithubPerPage}"),
            ct);
        if (resp is null || !resp.IsSuccessStatusCode) return Array.Empty<BuildArtifactSnapshot>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("artifacts", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<BuildArtifactSnapshot>();

        var list = new List<BuildArtifactSnapshot>();
        foreach (var a in arr.EnumerateArray())
        {
            var aname = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
            var url = a.TryGetProperty("archive_download_url", out var au) ? au.GetString() : null;
            long? size = null;
            if (a.TryGetProperty("size_in_bytes", out var sb) && sb.ValueKind == JsonValueKind.Number)
                size = sb.GetInt64();
            if (!string.IsNullOrEmpty(url))
                list.Add(new BuildArtifactSnapshot(aname, url, size));
        }
        return list;
    }

    private async Task<(string Content, int OriginalBytes)> FetchLogTailAsync(
        HttpClient http, string owner, string name, string jobId, CancellationToken ct)
    {
        var url = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/actions/jobs/{Uri.EscapeDataString(jobId)}/logs";
        var resp = await this.SendWithRetryAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (resp is null) return (string.Empty, 0);
        // GitHub returns 302 to a signed log URL; the HttpClient follows redirects automatically.
        if (!resp.IsSuccessStatusCode) return (string.Empty, 0);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(raw);
        return (Forge.Api.Providers.Ado.AdoBuildSyncRunner.TailTruncate(raw, SyncConstants.BuildLogTailBytes), bytes);
    }

    private static BuildSyncSnapshot MapRun(JsonElement r, string branch)
    {
        var id = r.TryGetProperty("id", out var idEl)
            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? string.Empty)
            : string.Empty;
        var status = r.TryGetProperty("status", out var st) ? st.GetString() ?? string.Empty : string.Empty;
        var conclusion = r.TryGetProperty("conclusion", out var cn) ? cn.GetString() : null;
        var (canonState, _) = MapGitHubState(status, conclusion);

        string? pipelineId = null, pipelineName = null;
        if (r.TryGetProperty("workflow_id", out var wid))
        {
            pipelineId = wid.ValueKind == JsonValueKind.Number ? wid.GetInt64().ToString() : wid.GetString();
        }
        if (r.TryGetProperty("name", out var n)) pipelineName = n.GetString();
        var commit = r.TryGetProperty("head_sha", out var hs) ? hs.GetString() : null;
        var webUrl = r.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
        var startedAt = r.TryGetProperty("run_started_at", out var sa) ? sa.GetString() : null;
        var finishedAt = r.TryGetProperty("updated_at", out var ua) ? ua.GetString() : null;

        return new BuildSyncSnapshot(
            Provider: "github",
            ProviderBuildId: id,
            PipelineId: pipelineId,
            PipelineName: pipelineName,
            State: canonState,
            Result: conclusion,
            TriggeringCommit: commit,
            Branch: branch,
            WebUrl: webUrl,
            StartedAt: startedAt,
            FinishedAt: finishedAt);
    }

    /// <summary>
    /// Maps GitHub Actions <c>status</c> + <c>conclusion</c> to canonical
    /// (state, isActive). Q27.2 / Q27.3 reference table.
    /// </summary>
    public static (string State, bool IsActive) MapGitHubState(string status, string? conclusion)
    {
        var s = (status ?? string.Empty).ToLowerInvariant();
        var c = (conclusion ?? string.Empty).ToLowerInvariant();
        return s switch
        {
            "queued" => ("queued", true),
            "in_progress" => ("running", true),
            "waiting" => ("queued", true),
            "requested" => ("queued", true),
            "completed" => c switch
            {
                "success" => ("succeeded", false),
                "failure" => ("failed", false),
                "timed_out" => ("failed", false),
                "cancelled" => ("canceled", false),
                "neutral" or "skipped" or "stale" or "action_required" => ("partially-succeeded", false),
                _ => ("failed", false),
            },
            _ => ("queued", true),
        };
    }

    private async Task<HttpResponseMessage?> SendWithRetryAsync(HttpClient http, Func<HttpRequestMessage> reqFactory, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var resp = await http.SendAsync(reqFactory(), ct);
                if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remValues)
                    && int.TryParse(remValues.FirstOrDefault(), out var remaining)
                    && remaining < SyncConstants.GithubRateLimitFloor)
                {
                    if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
                        && long.TryParse(resetValues.FirstOrDefault(), out var resetEpoch))
                    {
                        var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
                        var wait = resetAt - DateTimeOffset.UtcNow;
                        if (wait.TotalSeconds > 0)
                        {
                            if (wait.TotalSeconds > SyncConstants.MaxRetryAfterSeconds)
                                wait = TimeSpan.FromSeconds(SyncConstants.MaxRetryAfterSeconds);
                            await Task.Delay(wait, ct);
                        }
                    }
                }
                if (resp.StatusCode == HttpStatusCode.TooManyRequests
                    || resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                    if (retry.TotalSeconds > SyncConstants.MaxRetryAfterSeconds)
                        retry = TimeSpan.FromSeconds(SyncConstants.MaxRetryAfterSeconds);
                    resp.Dispose();
                    await Task.Delay(retry, ct);
                    continue;
                }
                return resp;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (attempt == maxRetries - 1)
                {
                    this.logger.LogWarning(ex, "GitHub build sync request failed");
                    return null;
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
        return null;
    }

    private static (string Owner, string Repo) ResolveOwnerRepo(RepoRow repo)
    {
        if (!string.IsNullOrEmpty(repo.GhOwner) && !string.IsNullOrEmpty(repo.GhRepo))
            return (repo.GhOwner!, repo.GhRepo!);
        return ParseOwnerRepoFromUrl(repo.RemoteUrl);
    }

    private static (string Owner, string Repo) ParseOwnerRepoFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return (string.Empty, string.Empty);
        var u = url.Trim();
        // strip protocol + ".git"
        var idx = u.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (string.Empty, string.Empty);
        var tail = u[(idx + "github.com".Length)..].Trim('/', ':');
        if (tail.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            tail = tail[..^4];
        var parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : (string.Empty, string.Empty);
    }

    private async Task<HttpClient> NewClientAsync(CancellationToken ct)
    {
        var client = this.clientFactory.CreateClient(ClientName);
        client.BaseAddress = new Uri("https://api.github.com/");
        if (!string.IsNullOrWhiteSpace(this.options.Pat))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.options.Pat);
        }
        else
        {
            var auth = this.authServices.FirstOrDefault(a => string.Equals(a.Kind, "github", StringComparison.OrdinalIgnoreCase));
            if (auth is not null)
            {
                try
                {
                    var bearer = await auth.GetAccessTokenAsync(ct);
                    if (!string.IsNullOrEmpty(bearer))
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "GitHub build sync: bearer fetch failed");
                }
            }
        }
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Forge", "1.0"));
        return client;
    }
}
