// -----------------------------------------------------------------------
// <copyright file="AdoClassificationService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.Ado;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 15.2 — REST-based ADO classification node discovery. Uses the
/// shared "ado" HttpClient (so tests can swap the message handler), the
/// <see cref="AdoOptions"/> PAT, and an MSAL bearer when available.
/// Cached in-process for 60 s per (project, kind, prefix) so flipping
/// tabs in the SprintManager doesn't re-fetch.
/// </summary>
public class AdoClassificationService : IAdoClassificationService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory clientFactory;
    private readonly AdoOptions options;
    private readonly ILogger<AdoClassificationService> logger;
    private readonly IAuthService? authService;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<DiscoveryNode> Nodes)> cache = new();

    public AdoClassificationService(
        IHttpClientFactory clientFactory,
        IOptions<AdoOptions> options,
        ILogger<AdoClassificationService> logger,
        IEnumerable<IAuthService> authServices)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.logger = logger;
        this.authService = authServices.FirstOrDefault(a => a.Kind == "ado");
    }

    public async Task<IReadOnlyList<DiscoveryNode>> DiscoverAsync(
        RepoRow repo,
        string kind,
        string? prefix,
        CancellationToken ct = default)
    {
        var group = NormaliseKind(kind);
        if (group is null) return Array.Empty<DiscoveryNode>();
        if (string.IsNullOrWhiteSpace(repo.AdoProject)) return Array.Empty<DiscoveryNode>();

        var trimmedPrefix = (prefix ?? string.Empty).Replace("\\\\", "\\").Trim().TrimEnd('\\');
        var cacheKey = $"{repo.AdoOrg}|{repo.AdoProject}|{group}|{trimmedPrefix}";
        if (this.cache.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheTtl)
        {
            return hit.Nodes;
        }

        try
        {
            var client = await this.NewClientAsync(repo, ct);
            // ADO's classification API returns the tree from a root node down
            // to $depth. We always pull the full $depth=10 tree (project-wide
            // sprints rarely nest deeper than a couple of levels) and prune
            // by prefix client-side. The response shape is recursive — we
            // flatten it into one DiscoveryNode per node.
            var url = $"{Uri.EscapeDataString(repo.AdoProject!)}/_apis/wit/classificationnodes/{group}?$depth=10&api-version=7.1";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                this.logger.LogWarning("ADO classificationnodes returned {Status} for {Url}", (int)resp.StatusCode, url);
                return Array.Empty<DiscoveryNode>();
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var rootProject = repo.AdoProject!;
            var flat = new List<DiscoveryNode>();
            FlattenNode(doc.RootElement, parentPath: null, depth: 0, projectRoot: rootProject, flat);

            // Filter by prefix (case-insensitive, path-prefix). An empty
            // prefix returns everything under the project root.
            IReadOnlyList<DiscoveryNode> filtered = string.IsNullOrEmpty(trimmedPrefix)
                ? flat
                : flat.Where(n => n.Path.StartsWith(trimmedPrefix, StringComparison.OrdinalIgnoreCase)).ToList();

            this.cache[cacheKey] = (DateTimeOffset.UtcNow, filtered);
            return filtered;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "ADO classification discovery failed for {Org}/{Project}", repo.AdoOrg, repo.AdoProject);
            return Array.Empty<DiscoveryNode>();
        }
    }

    /// <summary>Drop the cache (used when an import refresh wants live data).</summary>
    public void InvalidateCache() => this.cache.Clear();

    // ── tree flattener ──────────────────────────────────────────────────

    private static void FlattenNode(JsonElement node, string? parentPath, int depth, string projectRoot, List<DiscoveryNode> sink)
    {
        // Build the backslash-delimited path. The root node's `name` is
        // the project name; everything below appends with backslashes.
        var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var path = parentPath is null ? name : $"{parentPath}\\{name}";

        var hasChildren = node.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean();
        DateTimeOffset? start = null, finish = null;
        string? timeFrame = null;
        if (node.TryGetProperty("attributes", out var attrs))
        {
            if (attrs.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String)
            {
                if (DateTimeOffset.TryParse(sd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var v)) start = v;
            }
            if (attrs.TryGetProperty("finishDate", out var fd) && fd.ValueKind == JsonValueKind.String)
            {
                if (DateTimeOffset.TryParse(fd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var v)) finish = v;
            }
            if (attrs.TryGetProperty("timeFrame", out var tf) && tf.ValueKind == JsonValueKind.String)
            {
                timeFrame = tf.GetString()?.ToLowerInvariant();
            }
        }
        var providerNodeId = node.TryGetProperty("identifier", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? string.Empty
            : (node.TryGetProperty("id", out var idNum) && idNum.ValueKind == JsonValueKind.Number
                ? idNum.GetInt64().ToString(CultureInfo.InvariantCulture)
                : path); // fall back to path if no stable id surfaces

        sink.Add(new DiscoveryNode(
            providerNodeId,
            path,
            name,
            hasChildren,
            depth,
            new DiscoveryNodeAttributes(start, finish, timeFrame),
            AlreadyImported: false));

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                FlattenNode(child, path, depth + 1, projectRoot, sink);
            }
        }
    }

    private static string? NormaliseKind(string kind) => kind?.ToLowerInvariant() switch
    {
        "iterations" or "iteration" => "Iterations",
        "areas" or "area" or "areapaths" or "area-paths" => "Areas",
        _ => null,
    };

    // ── client construction ─────────────────────────────────────────────

    private async Task<HttpClient> NewClientAsync(RepoRow repo, CancellationToken ct)
    {
        var client = this.clientFactory.CreateClient(AdoProvider.ClientName);
        client.BaseAddress = new Uri(BuildOrgUrl(repo.AdoOrg) + "/");

        // Phase 9 — MSAL bearer first, PAT fallback (matches AdoProvider).
        string? bearer = null;
        if (this.authService is not null)
        {
            try { bearer = await this.authService.GetAccessTokenAsync(ct); }
            catch (Exception ex) { this.logger.LogDebug(ex, "MSAL silent token failed; falling back to PAT"); }
        }
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        else if (!string.IsNullOrWhiteSpace(this.options.Pat))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{this.options.Pat}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string BuildOrgUrl(string? adoOrg)
    {
        var s = (adoOrg ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(s)) return "https://dev.azure.com/";
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase))
        {
            return s.TrimEnd('/');
        }
        return $"https://dev.azure.com/{s.TrimEnd('/')}";
    }
}
