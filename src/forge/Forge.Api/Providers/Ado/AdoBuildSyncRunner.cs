// -----------------------------------------------------------------------
// <copyright file="AdoBuildSyncRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.Ado;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.Extensions.Options;

/// <summary>
/// forge_implementation.md Phase 27 — ADO Pipelines build sync runner.
/// <para>
/// Endpoints used:
/// </para>
/// <list type="bullet">
/// <item><c>GET _apis/build/builds?branchName=refs/heads/{branch}&amp;maxBuildsPerDefinition=10&amp;queryOrder=startTimeDescending&amp;api-version=7.0</c></item>
/// <item><c>GET _apis/build/builds/{id}/timeline?api-version=7.0</c></item>
/// <item><c>GET _apis/build/builds/{id}/logs/{logId}?api-version=7.0</c></item>
/// <item><c>GET _apis/build/builds/{id}/artifacts?api-version=7.0</c></item>
/// </list>
/// <para>
/// 429 / 503 backoff per <c>Retry-After</c> header (cap
/// <see cref="SyncConstants.MaxRetryAfterSeconds"/>, three retries).
/// </para>
/// </summary>
public sealed class AdoBuildSyncRunner : IBuildSyncRunner
{
    public const string ClientName = "ado-build-sync";

    private readonly IHttpClientFactory clientFactory;
    private readonly AdoOptions options;
    private readonly IEnumerable<IAuthService> authServices;
    private readonly ILogger<AdoBuildSyncRunner> logger;

    public AdoBuildSyncRunner(
        IHttpClientFactory clientFactory,
        IOptions<AdoOptions> options,
        IEnumerable<IAuthService> authServices,
        ILogger<AdoBuildSyncRunner> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.authServices = authServices;
        this.logger = logger;
    }

    public string Provider => "ado";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.options.Pat)
        || this.authServices.Any(a => string.Equals(a.Kind, "ado", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<BuildSyncSnapshot>> ListBuildsForBranchAsync(
        RepoRow repo, string branch, CancellationToken ct)
    {
        var (orgUrl, project) = ResolveCfg(repo, this.options);
        if (string.IsNullOrEmpty(orgUrl) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(branch))
        {
            return Array.Empty<BuildSyncSnapshot>();
        }
        using var http = await this.NewClientAsync(orgUrl, ct);
        var refBranch = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch : $"refs/heads/{branch}";
        var url = $"{Uri.EscapeDataString(project)}/_apis/build/builds" +
                  $"?branchName={Uri.EscapeDataString(refBranch)}" +
                  $"&maxBuildsPerDefinition=10&queryOrder=startTimeDescending&api-version=7.0";

        var resp = await this.SendWithRetryAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (resp is null || !resp.IsSuccessStatusCode) return Array.Empty<BuildSyncSnapshot>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<BuildSyncSnapshot>();

        var result = new List<BuildSyncSnapshot>(capacity: arr.GetArrayLength());
        foreach (var b in arr.EnumerateArray())
        {
            try { result.Add(MapBuild(b, branch)); }
            catch (Exception ex) { this.logger.LogDebug(ex, "ADO build map failed"); }
        }
        return result;
    }

    public async Task<IReadOnlyList<BuildStageSnapshot>> FetchStageDetailAsync(
        RepoRow repo, string providerBuildId, CancellationToken ct)
    {
        var (orgUrl, project) = ResolveCfg(repo, this.options);
        if (string.IsNullOrEmpty(orgUrl) || string.IsNullOrEmpty(project)) return Array.Empty<BuildStageSnapshot>();
        using var http = await this.NewClientAsync(orgUrl, ct);

        var timelineResp = await this.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get,
                $"{Uri.EscapeDataString(project)}/_apis/build/builds/{Uri.EscapeDataString(providerBuildId)}/timeline?api-version=7.0"),
            ct);
        if (timelineResp is null || !timelineResp.IsSuccessStatusCode) return Array.Empty<BuildStageSnapshot>();

        using var doc = JsonDocument.Parse(await timelineResp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            return Array.Empty<BuildStageSnapshot>();

        var result = new List<BuildStageSnapshot>();
        var order = 0;
        foreach (var r in records.EnumerateArray())
        {
            var type = r.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (type is not ("Stage" or "Job" or "Phase")) continue;
            var resultStr = r.TryGetProperty("result", out var res) ? res.GetString() : null;
            if (!string.Equals(resultStr, "failed", StringComparison.OrdinalIgnoreCase)) continue;

            var id = r.TryGetProperty("id", out var ridEl) ? ridEl.GetString() ?? string.Empty : string.Empty;
            var name = r.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
            var state = r.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? "completed" : "completed";
            var startedAt = r.TryGetProperty("startTime", out var stm) ? stm.GetString() : null;
            var finishedAt = r.TryGetProperty("finishTime", out var ftm) ? ftm.GetString() : null;
            string? logRef = null;
            if (r.TryGetProperty("log", out var logEl)
                && logEl.ValueKind == JsonValueKind.Object
                && logEl.TryGetProperty("id", out var logId))
            {
                logRef = logId.ValueKind == JsonValueKind.Number
                    ? logId.GetInt64().ToString()
                    : logId.GetString();
            }

            string logContent = string.Empty;
            int originalSize = 0;
            if (!string.IsNullOrEmpty(logRef))
            {
                var (content, originalBytes) = await this.FetchLogTailAsync(http, project, providerBuildId, logRef, ct);
                logContent = content;
                originalSize = originalBytes;
            }

            result.Add(new BuildStageSnapshot(
                ProviderStageRef: id,
                StageName: name,
                StageOrder: order++,
                State: state,
                Result: resultStr,
                StartedAt: startedAt,
                FinishedAt: finishedAt,
                LogRef: logRef,
                LogContent: logContent,
                OriginalLogSizeBytes: originalSize));
        }
        return result;
    }

    public async Task<IReadOnlyList<BuildArtifactSnapshot>> ListArtifactsAsync(
        RepoRow repo, string providerBuildId, CancellationToken ct)
    {
        var (orgUrl, project) = ResolveCfg(repo, this.options);
        if (string.IsNullOrEmpty(orgUrl) || string.IsNullOrEmpty(project)) return Array.Empty<BuildArtifactSnapshot>();
        using var http = await this.NewClientAsync(orgUrl, ct);

        var resp = await this.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get,
                $"{Uri.EscapeDataString(project)}/_apis/build/builds/{Uri.EscapeDataString(providerBuildId)}/artifacts?api-version=7.0"),
            ct);
        if (resp is null || !resp.IsSuccessStatusCode) return Array.Empty<BuildArtifactSnapshot>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<BuildArtifactSnapshot>();

        var list = new List<BuildArtifactSnapshot>();
        foreach (var a in arr.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
            string? downloadUrl = null;
            if (a.TryGetProperty("resource", out var res) && res.ValueKind == JsonValueKind.Object
                && res.TryGetProperty("downloadUrl", out var du)) downloadUrl = du.GetString();
            if (!string.IsNullOrEmpty(downloadUrl))
            {
                list.Add(new BuildArtifactSnapshot(name, downloadUrl, SizeBytesHint: null));
            }
        }
        return list;
    }

    private async Task<(string Content, int OriginalBytes)> FetchLogTailAsync(
        HttpClient http, string project, string buildId, string logId, CancellationToken ct)
    {
        var resp = await this.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get,
                $"{Uri.EscapeDataString(project)}/_apis/build/builds/{Uri.EscapeDataString(buildId)}/logs/{Uri.EscapeDataString(logId)}?api-version=7.0"),
            ct);
        if (resp is null || !resp.IsSuccessStatusCode) return (string.Empty, 0);

        var raw = await resp.Content.ReadAsStringAsync(ct);
        var bytes = Encoding.UTF8.GetByteCount(raw);
        var tail = TailTruncate(raw, SyncConstants.BuildLogTailBytes);
        return (tail, bytes);
    }

    /// <summary>
    /// Tail-truncate a log to the configured byte cap, preserving the
    /// failure point at the end (compile / test stack traces always live
    /// near the bottom).
    /// </summary>
    public static string TailTruncate(string content, int maxBytes)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes <= maxBytes) return content;
        // Walk back from the end measuring UTF-8 bytes until we fit.
        var asBytes = Encoding.UTF8.GetBytes(content);
        var startByte = asBytes.Length - maxBytes;
        // Don't slice mid-codepoint: walk forward until we hit a continuation-byte boundary.
        while (startByte < asBytes.Length && (asBytes[startByte] & 0xC0) == 0x80) startByte++;
        return Encoding.UTF8.GetString(asBytes, startByte, asBytes.Length - startByte);
    }

    private static BuildSyncSnapshot MapBuild(JsonElement b, string branch)
    {
        var id = b.TryGetProperty("id", out var idEl)
            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? string.Empty)
            : string.Empty;
        var rawStatus = b.TryGetProperty("status", out var st) ? st.GetString() ?? string.Empty : string.Empty;
        var rawResult = b.TryGetProperty("result", out var rt) ? rt.GetString() : null;
        var (canonState, isActive) = MapAdoState(rawStatus, rawResult);
        _ = isActive; // mapped on the consumer side via state

        string? pipelineId = null, pipelineName = null;
        if (b.TryGetProperty("definition", out var def) && def.ValueKind == JsonValueKind.Object)
        {
            if (def.TryGetProperty("id", out var pid))
                pipelineId = pid.ValueKind == JsonValueKind.Number ? pid.GetInt64().ToString() : pid.GetString();
            if (def.TryGetProperty("name", out var pname)) pipelineName = pname.GetString();
        }
        var commit = b.TryGetProperty("sourceVersion", out var sv) ? sv.GetString() : null;
        var webUrl = b.TryGetProperty("_links", out var lks)
                     && lks.TryGetProperty("web", out var web)
                     && web.TryGetProperty("href", out var href) ? href.GetString() : null;
        var startedAt = b.TryGetProperty("startTime", out var sta) ? sta.GetString() : null;
        var finishedAt = b.TryGetProperty("finishTime", out var fta) ? fta.GetString() : null;

        return new BuildSyncSnapshot(
            Provider: "ado",
            ProviderBuildId: id,
            PipelineId: pipelineId,
            PipelineName: pipelineName,
            State: canonState,
            Result: rawResult,
            TriggeringCommit: commit,
            Branch: branch,
            WebUrl: webUrl,
            StartedAt: startedAt,
            FinishedAt: finishedAt);
    }

    /// <summary>
    /// Maps ADO <c>status</c> + <c>result</c> to canonical
    /// (state, isActive). Q27.2 Q27.13 reference table.
    /// </summary>
    public static (string State, bool IsActive) MapAdoState(string status, string? result)
    {
        var s = (status ?? string.Empty).ToLowerInvariant();
        var r = (result ?? string.Empty).ToLowerInvariant();
        return s switch
        {
            "inprogress" => ("running", true),
            "notstarted" => ("not-started", true),
            "postponed" => ("queued", true),
            "cancelling" => ("canceled", false),
            "completed" => r switch
            {
                "succeeded" => ("succeeded", false),
                "failed" => ("failed", false),
                "partiallysucceeded" => ("partially-succeeded", false),
                "canceled" or "cancelled" => ("canceled", false),
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
                    this.logger.LogWarning(ex, "ADO build sync request failed after {Attempts} attempts", maxRetries);
                    return null;
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
        return null;
    }

    private static (string OrgUrl, string Project) ResolveCfg(RepoRow repo, AdoOptions options)
    {
        if (!string.IsNullOrEmpty(repo.AdoProject))
        {
            var orgUrl = !string.IsNullOrEmpty(repo.AdoOrg)
                ? BuildAdoOrgUrl(repo.AdoOrg!)
                : options.OrgUrl ?? string.Empty;
            return (orgUrl, repo.AdoProject!);
        }
        return (options.OrgUrl ?? string.Empty, options.Project ?? string.Empty);
    }

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

    private async Task<HttpClient> NewClientAsync(string orgUrl, CancellationToken ct)
    {
        var client = this.clientFactory.CreateClient(ClientName);
        if (!string.IsNullOrEmpty(orgUrl))
            client.BaseAddress = new Uri(orgUrl.TrimEnd('/') + "/");

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
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "ADO build sync: bearer fetch failed");
                }
            }
        }
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
