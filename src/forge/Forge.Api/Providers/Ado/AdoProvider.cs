// -----------------------------------------------------------------------
// <copyright file="AdoProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.Ado;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Forge.Api.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 5.2 — REST-based Azure DevOps provider. Uses a named HttpClient
/// (named "ado") so tests can swap the message handler.
/// </summary>
public class AdoProvider : IForgeProvider
{
    public const string ClientName = "ado";

    private readonly IHttpClientFactory clientFactory;
    private readonly AdoOptions options;
    private readonly ILogger<AdoProvider> logger;
    private readonly ProviderHealthState health = new("ado");

    public AdoProvider(IHttpClientFactory clientFactory, IOptions<AdoOptions> options, ILogger<AdoProvider> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.logger = logger;
    }

    public string Kind => "ado";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.options.OrgUrl) &&
        !string.IsNullOrWhiteSpace(this.options.Project) &&
        !string.IsNullOrWhiteSpace(this.options.Pat);

    public async Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured) return Array.Empty<Card>();
        var client = this.NewClient();

        try
        {
            // 1) Run the WIQL query to pull the matching ids.
            var wiql = string.IsNullOrWhiteSpace(this.options.WorkItemQuery)
                ? "Select [Id] From WorkItems Where [System.TeamProject] = @project Order By [System.ChangedDate] Desc"
                : this.options.WorkItemQuery!;
            var wiqlBody = new { query = wiql };
            var wiqlReq = new HttpRequestMessage(HttpMethod.Post,
                $"{this.options.Project}/_apis/wit/wiql?api-version=7.1")
            {
                Content = new StringContent(JsonSerializer.Serialize(wiqlBody), Encoding.UTF8, "application/json"),
            };
            var wiqlResp = await client.SendAsync(wiqlReq, ct);
            this.health.RecordResponse(wiqlResp);
            wiqlResp.EnsureSuccessStatusCode();
            using var wiqlDoc = JsonDocument.Parse(await wiqlResp.Content.ReadAsStringAsync(ct));
            var ids = wiqlDoc.RootElement.GetProperty("workItems")
                .EnumerateArray()
                .Select(w => w.GetProperty("id").GetInt64().ToString())
                .Take(200)
                .ToArray();
            if (ids.Length == 0) return Array.Empty<Card>();

            // 2) Hydrate the work items in one batch call.
            var idList = string.Join(",", ids);
            var fields = "System.State,System.WorkItemType,System.Title,System.Tags,System.AreaPath,System.AssignedTo,Microsoft.VSTS.Scheduling.StoryPoints,Microsoft.VSTS.Common.Priority";
            var resp = await client.GetAsync(
                $"_apis/wit/workitems?ids={idList}&fields={fields}&api-version=7.1", ct);
            this.health.RecordResponse(resp);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var cards = new List<Card>();
            foreach (var wi in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                cards.Add(AdoMapper.ToCard(wi, this.options.Project, this.options.Project));
            }
            this.health.MarkSync();
            return cards;
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "ADO ListWorkItemsAsync failed");
            return Array.Empty<Card>();
        }
    }

    public async Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default)
    {
        if (!this.IsConfigured) return null;
        var numeric = id.TrimStart('#');
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"_apis/wit/workitems/{numeric}?api-version=7.1", ct);
            this.health.RecordResponse(resp);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return AdoMapper.ToCard(doc.RootElement, this.options.Project, this.options.Project);
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "ADO GetWorkItemAsync({Id}) failed", id);
            return null;
        }
    }

    public async Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured || string.IsNullOrWhiteSpace(this.options.Repository)) return Array.Empty<Pr>();
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync(
                $"{this.options.Project}/_apis/git/repositories/{this.options.Repository}/pullrequests?searchCriteria.status=active&api-version=7.1", ct);
            this.health.RecordResponse(resp);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var prs = new List<Pr>();
            foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                prs.Add(AdoMapper.ToPr(pr, this.options.Project));
            }
            this.health.MarkSync();
            return prs;
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "ADO ListPullRequestsAsync failed");
            return Array.Empty<Pr>();
        }
    }

    public async Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default)
    {
        if (!this.IsConfigured || string.IsNullOrWhiteSpace(this.options.Repository)) return null;
        var numeric = id.Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('#');
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync(
                $"{this.options.Project}/_apis/git/repositories/{this.options.Repository}/pullrequests/{numeric}?api-version=7.1", ct);
            this.health.RecordResponse(resp);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return AdoMapper.ToPr(doc.RootElement, this.options.Project);
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "ADO GetPullRequestAsync({Id}) failed", id);
            return null;
        }
    }

    public Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default) =>
        // Phase 6 wires real ADO Pipelines. Read-side returns null today.
        Task.FromResult<BuildStatus?>(null);

    public async Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured)
        {
            return new ProviderHealth(this.Kind, false, "missing OrgUrl/Project/Pat", null, 0, null, null);
        }
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"_apis/projects/{this.options.Project}?api-version=7.1", ct);
            this.health.RecordResponse(resp);
            return resp.IsSuccessStatusCode
                ? this.health.Snapshot(true, $"connected to {this.options.OrgUrl} / {this.options.Project}")
                : this.health.Snapshot(false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            return this.health.Snapshot(false, ex.Message);
        }
    }

    private HttpClient NewClient()
    {
        var client = this.clientFactory.CreateClient(ClientName);
        if (client.BaseAddress is null && !string.IsNullOrWhiteSpace(this.options.OrgUrl))
        {
            client.BaseAddress = new Uri(this.options.OrgUrl.TrimEnd('/') + "/");
        }
        // Basic auth with empty username + PAT as password — ADO convention.
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{this.options.Pat}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}

/// <summary>Tracks rolling health metrics for a provider — last sync, error count, rate-limit headers.</summary>
public sealed class ProviderHealthState
{
    private readonly object gate = new();
    private DateTimeOffset? lastSync;
    private int errorCount;
    private int? rateRemaining;
    private int? rateLimit;

    public ProviderHealthState(string kind) { this.Kind = kind; }

    public string Kind { get; }

    public void MarkSync()
    {
        lock (this.gate) { this.lastSync = DateTimeOffset.UtcNow; this.errorCount = 0; }
    }

    public void RecordResponse(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var r) &&
            int.TryParse(r.FirstOrDefault(), out var rr))
        {
            lock (this.gate) { this.rateRemaining = rr; }
        }
        if (resp.Headers.TryGetValues("X-RateLimit-Limit", out var l) &&
            int.TryParse(l.FirstOrDefault(), out var ll))
        {
            lock (this.gate) { this.rateLimit = ll; }
        }
    }

    public void RecordError(Exception _)
    {
        lock (this.gate) { this.errorCount++; }
    }

    public ProviderHealth Snapshot(bool ok, string message)
    {
        lock (this.gate)
        {
            return new ProviderHealth(this.Kind, ok, message, this.lastSync, this.errorCount, this.rateRemaining, this.rateLimit);
        }
    }
}
