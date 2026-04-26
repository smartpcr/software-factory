// -----------------------------------------------------------------------
// <copyright file="GitHubProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.GitHub;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
    private readonly ILogger<GitHubProvider> logger;
    private readonly ProviderHealthState health = new("github");

    public GitHubProvider(IHttpClientFactory clientFactory, IOptions<GitHubOptions> options, ILogger<GitHubProvider> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.logger = logger;
    }

    public string Kind => "github";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.options.Repo) &&
        !string.IsNullOrWhiteSpace(this.options.Pat);

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

    public async Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured)
        {
            return new ProviderHealth(this.Kind, false, "missing Repo/Pat", null, 0, null, null);
        }
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"repos/{this.options.Repo}", ct);
            this.health.RecordResponse(resp);
            return resp.IsSuccessStatusCode
                ? this.health.Snapshot(true, $"connected to {this.options.Repo}")
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
        if (client.BaseAddress is null)
        {
            var apiBase = string.IsNullOrWhiteSpace(this.options.ApiBase)
                ? "https://api.github.com"
                : this.options.ApiBase.TrimEnd('/');
            client.BaseAddress = new Uri(apiBase + "/");
        }
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("token", this.options.Pat);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        }
        return client;
    }
}
