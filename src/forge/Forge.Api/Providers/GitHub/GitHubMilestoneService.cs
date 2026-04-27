// -----------------------------------------------------------------------
// <copyright file="GitHubMilestoneService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.GitHub;

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 15.4 — REST-based GitHub milestone listing. Reuses the shared
/// "github" HttpClient + <see cref="GitHubOptions.Pat"/> + the GitHub
/// MSAL/PKCE token when signed in. Maps state + due-date into the same
/// <see cref="DiscoveryNode"/> shape ADO classification produces, so the
/// import endpoint stays provider-agnostic.
/// </summary>
public class GitHubMilestoneService : IGitHubMilestoneService
{
    private readonly IHttpClientFactory clientFactory;
    private readonly GitHubOptions options;
    private readonly ILogger<GitHubMilestoneService> logger;
    private readonly IAuthService? authService;

    public GitHubMilestoneService(
        IHttpClientFactory clientFactory,
        IOptions<GitHubOptions> options,
        ILogger<GitHubMilestoneService> logger,
        IEnumerable<IAuthService> authServices)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.logger = logger;
        this.authService = authServices.FirstOrDefault(a => a.Kind == "github");
    }

    public async Task<IReadOnlyList<DiscoveryNode>> DiscoverAsync(RepoRow repo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo.GhOwner) || string.IsNullOrWhiteSpace(repo.GhRepo))
        {
            return Array.Empty<DiscoveryNode>();
        }

        try
        {
            var client = await this.NewClientAsync(ct);
            var url = $"repos/{repo.GhOwner}/{repo.GhRepo}/milestones?state=all&per_page=100";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                this.logger.LogWarning("GitHub milestones returned {Status} for {Owner}/{Repo}",
                    (int)resp.StatusCode, repo.GhOwner, repo.GhRepo);
                return Array.Empty<DiscoveryNode>();
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<DiscoveryNode>();

            var now = DateTimeOffset.UtcNow;
            var nodes = new List<DiscoveryNode>();
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                var number = m.TryGetProperty("number", out var nm) ? nm.GetInt64() : 0;
                var title  = m.TryGetProperty("title", out var t) ? t.GetString() ?? $"#{number}" : $"#{number}";
                var state  = m.TryGetProperty("state", out var st) ? st.GetString() ?? "open" : "open";
                DateTimeOffset? dueOn = null;
                if (m.TryGetProperty("due_on", out var d) && d.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var v))
                {
                    dueOn = v;
                }

                // Map GitHub state + due_on into the iteration timeframe vocabulary.
                // closed → past; open + due past → past; open + due in current week → current; else future.
                string timeFrame;
                if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase)) timeFrame = "past";
                else if (dueOn is null) timeFrame = "future";
                else if (dueOn < now.AddDays(-1)) timeFrame = "past";
                else if (dueOn < now.AddDays(7)) timeFrame = "current";
                else timeFrame = "future";

                var path = $"#{number} {title}";
                nodes.Add(new DiscoveryNode(
                    ProviderNodeId: $"milestone:{number}",
                    Path: path,
                    Name: title,
                    HasChildren: false,
                    Depth: 0,
                    Attributes: new DiscoveryNodeAttributes(StartDate: null, FinishDate: dueOn, TimeFrame: timeFrame),
                    AlreadyImported: false));
            }
            return nodes;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "GitHub DiscoverAsync({Owner}/{Repo}) failed", repo.GhOwner, repo.GhRepo);
            return Array.Empty<DiscoveryNode>();
        }
    }

    private async Task<HttpClient> NewClientAsync(CancellationToken ct)
    {
        var client = this.clientFactory.CreateClient(GitHubProvider.ClientName);
        client.BaseAddress = new Uri("https://api.github.com/");

        string? bearer = null;
        if (this.authService is not null)
        {
            try { bearer = await this.authService.GetAccessTokenAsync(ct); }
            catch (Exception ex) { this.logger.LogDebug(ex, "GH token retrieval failed; falling back to PAT"); }
        }
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        else if (!string.IsNullOrWhiteSpace(this.options.Pat))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.options.Pat);
        }
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Forge/0.1");
        return client;
    }
}
