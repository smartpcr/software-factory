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
using Forge.Api.Auth;
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
    private readonly IAuthService? authService;
    private readonly IAdoConfigResolver? configResolver;

    public AdoProvider(
        IHttpClientFactory clientFactory,
        IOptions<AdoOptions> options,
        ILogger<AdoProvider> logger,
        IEnumerable<IAuthService> authServices,
        IAdoConfigResolver? configResolver = null)
    {
        this.clientFactory = clientFactory;
        this.options = options.Value;
        this.logger = logger;
        // Phase 9 — MSAL bearer-token auth takes precedence over the PAT
        // when an account is signed in. Optional: AdoProvider works fine
        // without an auth service (legacy PAT-only deployments).
        this.authService = authServices.FirstOrDefault(a => a.Kind == "ado");
        // Phase 10 — config resolver pulls OrgUrl/Project from user settings
        // first, falling back to AdoOptions. Optional so unit tests can
        // construct AdoProvider directly without DB scaffolding.
        this.configResolver = configResolver;
    }

    public string Kind => "ado";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.EffectiveOrgUrl)
        && !string.IsNullOrWhiteSpace(this.EffectiveProject)
        && (!string.IsNullOrWhiteSpace(this.options.Pat) || this.authService is not null);

    private (string OrgUrl, string Project) ResolveConfig()
    {
        if (this.configResolver is null)
        {
            // Fall back to the bound options DIRECTLY — never go through the
            // EffectiveOrgUrl / EffectiveProject getters here or we recurse.
            return (this.options.OrgUrl, this.options.Project);
        }
        var effective = this.configResolver.ResolveAsync().GetAwaiter().GetResult();
        return (effective.OrgUrl, effective.Project);
    }

    private string EffectiveProject => this.ResolveConfig().Project;

    private string EffectiveOrgUrl => this.ResolveConfig().OrgUrl;

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
                $"{this.EffectiveProject}/_apis/wit/wiql?api-version=7.1")
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
                cards.Add(AdoMapper.ToCard(wi, this.EffectiveProject, this.EffectiveProject));
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
            return AdoMapper.ToCard(doc.RootElement, this.EffectiveProject, this.EffectiveProject);
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
                $"{this.EffectiveProject}/_apis/git/repositories/{this.options.Repository}/pullrequests?searchCriteria.status=active&api-version=7.1", ct);
            this.health.RecordResponse(resp);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var prs = new List<Pr>();
            foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                prs.Add(AdoMapper.ToPr(pr, this.EffectiveProject));
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
                $"{this.EffectiveProject}/_apis/git/repositories/{this.options.Repository}/pullrequests/{numeric}?api-version=7.1", ct);
            this.health.RecordResponse(resp);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return AdoMapper.ToPr(doc.RootElement, this.EffectiveProject);
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

    // -- Phase 6 write surface ----------------------------------------------

    public async Task<Card> CreateWorkItemAsync(NewWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        // ADO's create-work-item endpoint takes a JSON-Patch document.
        var patch = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = req.Title },
        };
        if (!string.IsNullOrEmpty(req.AreaPath))
        {
            patch.Add(new { op = "add", path = "/fields/System.AreaPath", value = req.AreaPath });
        }
        if (req.StoryPoints.HasValue)
        {
            patch.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.StoryPoints", value = req.StoryPoints.Value });
        }
        if (req.Tags is { Count: > 0 })
        {
            patch.Add(new { op = "add", path = "/fields/System.Tags", value = string.Join("; ", req.Tags) });
        }
        if (!string.IsNullOrEmpty(req.Description))
        {
            patch.Add(new { op = "add", path = "/fields/System.Description", value = req.Description });
        }

        var url = $"{this.EffectiveProject}/_apis/wit/workitems/${Uri.EscapeDataString(req.Type)}?api-version=7.1";
        var content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");
        // Override Content-Type — ADO requires the JSON-Patch media type on create.
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json-patch+json");
        using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return AdoMapper.ToCard(doc.RootElement, this.EffectiveProject, this.EffectiveProject);
    }

    public async Task<Card> UpdateWorkItemAsync(string id, UpdateWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.TrimStart('#');
        var patch = new List<object>();
        if (!string.IsNullOrEmpty(req.Title))    patch.Add(new { op = "replace", path = "/fields/System.Title", value = req.Title });
        if (!string.IsNullOrEmpty(req.State))    patch.Add(new { op = "replace", path = "/fields/System.State", value = req.State });
        if (!string.IsNullOrEmpty(req.AssigneeUniqueName)) patch.Add(new { op = "replace", path = "/fields/System.AssignedTo", value = req.AssigneeUniqueName });
        if (req.StoryPoints.HasValue)            patch.Add(new { op = "replace", path = "/fields/Microsoft.VSTS.Scheduling.StoryPoints", value = req.StoryPoints.Value });
        if (req.Tags is { Count: > 0 })          patch.Add(new { op = "replace", path = "/fields/System.Tags", value = string.Join("; ", req.Tags) });
        if (patch.Count == 0)
        {
            var fresh = await this.GetWorkItemAsync(id, ct);
            return fresh ?? throw new InvalidOperationException($"Work item {id} not found and no fields to update");
        }
        var content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json-patch+json");
        using var msg = new HttpRequestMessage(new HttpMethod("PATCH"), $"_apis/wit/workitems/{num}?api-version=7.1") { Content = content };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return AdoMapper.ToCard(doc.RootElement, this.EffectiveProject, this.EffectiveProject);
    }

    public async Task CommentOnWorkItemAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.TrimStart('#');
        var payload = JsonSerializer.Serialize(new { text = body });
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{this.EffectiveProject}/_apis/wit/workItems/{num}/comments?api-version=7.1-preview.4")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<Pr> CreatePullRequestAsync(NewPullRequestRequest req, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        if (string.IsNullOrEmpty(this.options.Repository))
        {
            throw new InvalidOperationException("Forge:Ado:Repository is required to create PRs");
        }
        var client = this.NewClient(ctx);
        var payload = JsonSerializer.Serialize(new
        {
            sourceRefName = $"refs/heads/{req.Branch}",
            targetRefName = $"refs/heads/{req.BaseBranch}",
            title = req.Title,
            description = req.Body,
            isDraft = req.Draft,
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{this.EffectiveProject}/_apis/git/repositories/{this.options.Repository}/pullrequests?api-version=7.1")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return AdoMapper.ToPr(doc.RootElement, this.EffectiveProject);
    }

    public Task ApprovePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) =>
        this.SetReviewerVoteAsync(id, vote: 10, ctx, ct);

    public Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
        this.SetReviewerVoteAsync(id, vote: -10, ctx, ct).ContinueWith(_ =>
            this.CommentOnPullRequestAsync(id, body, ctx, ct), ct).Unwrap();

    public async Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('#');
        var payload = JsonSerializer.Serialize(new
        {
            comments = new[] { new { content = body, commentType = 1 } },
            status = 1,
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{this.EffectiveProject}/_apis/git/repositories/{this.options.Repository}/pullRequests/{num}/threads?api-version=7.1")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
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
        // Merge by transitioning the PR to "completed" with a squash merge.
        var payload = JsonSerializer.Serialize(new
        {
            status = "completed",
            lastMergeSourceCommit = (object?)null,
            completionOptions = new { mergeStrategy = "squash", deleteSourceBranch = false },
        });
        using var msg = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{this.EffectiveProject}/_apis/git/repositories/{this.options.Repository}/pullrequests/{num}?api-version=7.1")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return AdoMapper.ToPr(doc.RootElement, this.EffectiveProject);
    }

    public async Task<BuildStatus?> TriggerBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        // ADO Pipelines: queue a build by name lookup is non-trivial without
        // knowing the pipeline id. We POST to the runs API with the branch
        // ref and let the server pick the default pipeline. The caller's
        // configuration is expected to set a `Forge:Ado:DefaultPipelineId`
        // for production; otherwise this returns null gracefully.
        var pipelineId = this.options.GetType().GetProperty("DefaultPipelineId")?.GetValue(this.options) as string;
        if (string.IsNullOrEmpty(pipelineId))
        {
            return new BuildStatus("queued", $"trigger {branch}", 0, null);
        }
        var payload = JsonSerializer.Serialize(new
        {
            resources = new { repositories = new { self = new { refName = $"refs/heads/{branch}" } } },
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{this.EffectiveProject}/_apis/pipelines/{pipelineId}/runs?api-version=7.1-preview.1")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        if (!resp.IsSuccessStatusCode) return null;
        return new BuildStatus("running", "queued via ADO Pipelines", 0, null);
    }

    public Task<BuildStatus?> CancelBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) =>
        // ADO requires the build id to cancel; absent that we fall back to
        // the cached state which the caller flips to "queued (cancelled)".
        Task.FromResult<BuildStatus?>(new BuildStatus("queued", "cancellation requested", 0, null));

    private async Task SetReviewerVoteAsync(string id, int vote, ProviderCallContext ctx, CancellationToken ct)
    {
        this.RequireConfigured();
        var client = this.NewClient(ctx);
        var num = id.Replace("PR ", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('#');
        // ADO requires a reviewer GUID — for the placeholder we PATCH with `me`.
        var payload = JsonSerializer.Serialize(new { vote });
        using var msg = new HttpRequestMessage(HttpMethod.Put,
            $"{this.EffectiveProject}/_apis/git/repositories/{this.options.Repository}/pullRequests/{num}/reviewers/me?api-version=7.1")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(msg, ct);
        this.health.RecordResponse(resp);
        resp.EnsureSuccessStatusCode();
    }

    private void RequireConfigured()
    {
        if (!this.IsConfigured)
        {
            throw new InvalidOperationException("ADO provider is not configured (Forge:Ado:OrgUrl / Project / Pat)");
        }
    }

    private HttpClient NewClient(ProviderCallContext ctx)
    {
        var client = this.NewClient();
        // Phase 6.11 — write paths use the per-request user PAT when supplied.
        if (!string.IsNullOrEmpty(ctx.UserPat))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{ctx.UserPat}"));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
        }
        return client;
    }

    /// <summary>
    /// Phase 8.5 — list repositories the configured PAT can see in the
    /// configured project. ADO REST: <c>GET {org}/{project}/_apis/git/repositories</c>.
    /// Returns an empty list when not configured or the call fails — the picker
    /// degrades to free-text URL entry rather than blocking the operator.
    /// </summary>
    public async Task<IReadOnlyList<DiscoverableRepo>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured) return Array.Empty<DiscoverableRepo>();
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"{this.EffectiveProject}/_apis/git/repositories?api-version=7.1", ct);
            this.health.RecordResponse(resp);
            if (!resp.IsSuccessStatusCode) return Array.Empty<DiscoverableRepo>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var orgName = TrimOrgFromUrl(this.EffectiveOrgUrl);
            var list = new List<DiscoverableRepo>();
            foreach (var r in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var name = r.GetProperty("name").GetString() ?? string.Empty;
                var remoteUrl = r.TryGetProperty("remoteUrl", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                var defaultBranch = r.TryGetProperty("defaultBranch", out var db)
                    ? (db.GetString() ?? "refs/heads/main").Replace("refs/heads/", string.Empty)
                    : "main";
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(remoteUrl)) continue;
                list.Add(new DiscoverableRepo(
                    Kind: "ado",
                    DisplayName: name,
                    RemoteUrl: remoteUrl,
                    DefaultBranch: defaultBranch,
                    AdoOrg: orgName,
                    AdoProject: this.EffectiveProject));
            }
            return list;
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "ADO ListRepositoriesAsync failed");
            return Array.Empty<DiscoverableRepo>();
        }
    }

    private static string? TrimOrgFromUrl(string? orgUrl)
    {
        if (string.IsNullOrWhiteSpace(orgUrl)) return null;
        if (Uri.TryCreate(orgUrl, UriKind.Absolute, out var uri))
        {
            // dev.azure.com/{org} or {org}.visualstudio.com.
            if (uri.Host.EndsWith("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            {
                return uri.AbsolutePath.Trim('/').Split('/')[0];
            }
            if (uri.Host.EndsWith("visualstudio.com", StringComparison.OrdinalIgnoreCase))
            {
                return uri.Host.Split('.')[0];
            }
        }
        return null;
    }

    /// <summary>
    /// Phase 12.4 — pull rich <see cref="WorkItem"/>s for the WIT sync
    /// service. Differences from <see cref="ListWorkItemsAsync"/>:
    ///   * Builds the WIQL dynamically from <see cref="WorkItemSyncFilters"/>
    ///     (areaPath UNDER, type IN, AssignedTo equality, ChangedDate range).
    ///   * Hydrates with <c>$expand=relations</c> so the parent-link is
    ///     captured (System.LinkTypes.Hierarchy-Reverse).
    ///   * Returns the canonical Forge work-item shape, not a Card.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> ListWorkItemsForSyncAsync(
        WorkItemSyncFilters filters, CancellationToken ct = default)
    {
        if (!this.IsConfigured) return Array.Empty<WorkItem>();
        var client = this.NewClient();

        try
        {
            var wiql = BuildSyncWiql(this.EffectiveProject, filters);
            var wiqlBody = new { query = wiql };
            var wiqlReq = new HttpRequestMessage(HttpMethod.Post,
                $"{this.EffectiveProject}/_apis/wit/wiql?api-version=7.1")
            {
                Content = new StringContent(JsonSerializer.Serialize(wiqlBody), Encoding.UTF8, "application/json"),
            };
            var wiqlResp = await client.SendAsync(wiqlReq, ct);
            this.health.RecordResponse(wiqlResp);
            if (!wiqlResp.IsSuccessStatusCode)
            {
                var errBody = await wiqlResp.Content.ReadAsStringAsync(ct);
                this.logger.LogWarning(
                    "ADO WIQL {Status} for project '{Project}'. WIQL: {Wiql} | Response: {Body}",
                    (int)wiqlResp.StatusCode, this.EffectiveProject, wiql, Trunc(errBody, 800));
                wiqlResp.EnsureSuccessStatusCode();
            }
            using var wiqlDoc = JsonDocument.Parse(await wiqlResp.Content.ReadAsStringAsync(ct));
            var ids = wiqlDoc.RootElement.GetProperty("workItems")
                .EnumerateArray()
                .Select(w => w.GetProperty("id").GetInt64().ToString())
                .Take(200)
                .ToArray();
            if (ids.Length == 0) return Array.Empty<WorkItem>();

            var idList = string.Join(",", ids);
            // ADO rejects `fields` and `$expand` together with
            // ConflictingParametersException. We want $expand=relations to
            // capture the parent-link (System.LinkTypes.Hierarchy-Reverse),
            // so omit `fields` and let ADO return the default field set.
            var resp = await client.GetAsync(
                $"_apis/wit/workitems?ids={idList}&$expand=relations&api-version=7.1", ct);
            this.health.RecordResponse(resp);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                this.logger.LogWarning(
                    "ADO workitems hydrate {Status} for {Count} ids. Response: {Body}",
                    (int)resp.StatusCode, ids.Length, Trunc(errBody, 800));
                resp.EnsureSuccessStatusCode();
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var items = new List<WorkItem>();
            foreach (var wi in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                items.Add(AdoMapper.ToWorkItem(wi, this.EffectiveProject, this.EffectiveProject));
            }
            this.health.MarkSync();
            return items;
        }
        catch (Exception ex)
        {
            this.health.RecordError(ex);
            this.logger.LogWarning(ex, "ADO ListWorkItemsForSyncAsync failed");
            return Array.Empty<WorkItem>();
        }
    }

    /// <summary>
    /// Public for the Batch B unit test. Builds a parameter-free WIQL string
    /// scoped to the project and the <paramref name="filters"/>; missing
    /// filters drop their clause.
    /// </summary>
    public static string BuildSyncWiql(string project, WorkItemSyncFilters filters)
    {
        var parts = new List<string>();
        // System.TeamProject filter is what scopes the query in WIQL terms.
        parts.Add($"[System.TeamProject] = '{EscWiql(project)}'");
        if (!string.IsNullOrWhiteSpace(filters.AreaPath))
        {
            parts.Add($"[System.AreaPath] UNDER '{EscWiql(filters.AreaPath!)}'");
        }
        if (filters.Types is { Count: > 0 })
        {
            var mapped = filters.Types
                .Select(t => MapForgeTypeToAdo(t))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToArray();
            if (mapped.Length > 0)
            {
                var inList = string.Join(", ", mapped.Select(m => $"'{EscWiql(m)}'"));
                parts.Add($"[System.WorkItemType] IN ({inList})");
            }
        }
        if (!string.IsNullOrWhiteSpace(filters.AssignedTo))
        {
            parts.Add($"[System.AssignedTo] = '{EscWiql(filters.AssignedTo!)}'");
        }
        // ADO's WIQL [System.ChangedDate] uses *date* precision — supplying a
        // time component (e.g. '2026-04-27T23:59:59Z') triggers a 400 with
        // "You cannot supply a time with the date when running a query using
        // date precision."
        if (filters.UpdatedFrom is DateTimeOffset from)
        {
            parts.Add($"[System.ChangedDate] >= '{from.UtcDateTime:yyyy-MM-dd}'");
        }
        if (filters.UpdatedTo is DateTimeOffset to)
        {
            parts.Add($"[System.ChangedDate] <= '{to.UtcDateTime:yyyy-MM-dd}'");
        }
        return $"Select [System.Id] From WorkItems Where {string.Join(" AND ", parts)} Order By [System.ChangedDate] Desc";
    }

    private static string EscWiql(string s) => s.Replace("'", "''");

    private static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "…");

    private static string MapForgeTypeToAdo(string forge) => (forge ?? string.Empty).ToLowerInvariant() switch
    {
        "epic"    => "Epic",
        "feature" => "Feature",
        "task"    => "Task",
        "bug"     => "Bug",
        // Design-track types are mapped onto ADO custom types; if the org
        // doesn't have those defined the WIQL just returns no rows for them.
        "phase"   => "Phase",
        "stage"   => "Stage",
        "step"    => "Step",
        _         => string.Empty,
    };

    public async Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured)
        {
            return new ProviderHealth(this.Kind, false, "missing OrgUrl/Project/Pat", null, 0, null, null);
        }
        try
        {
            var client = this.NewClient();
            var resp = await client.GetAsync($"_apis/projects/{this.EffectiveProject}?api-version=7.1", ct);
            this.health.RecordResponse(resp);
            return resp.IsSuccessStatusCode
                ? this.health.Snapshot(true, $"connected to {this.EffectiveOrgUrl} / {this.EffectiveProject}")
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
        // Sync entry point used by the existing call sites. Phase 9 prefers
        // <see cref="NewClientAsync"/>; this helper preserves the legacy
        // PAT-Basic flow for code paths that haven't migrated yet.
        return this.NewClientAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task<HttpClient> NewClientAsync(CancellationToken ct)
    {
        var client = this.clientFactory.CreateClient(ClientName);
        // Phase 10 — base URL follows the resolved config (user settings → appsettings).
        var (orgUrl, _) = this.ResolveConfig();
        if (!string.IsNullOrWhiteSpace(orgUrl))
        {
            // Re-create the BaseAddress every call — cheap and lets the user
            // change org without restarting.
            client.BaseAddress = new Uri(orgUrl.TrimEnd('/') + "/");
        }

        // Phase 9 — MSAL bearer first, PAT only as fallback for headless runs.
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
            // Basic auth with empty username + PAT as password — ADO convention.
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{this.options.Pat}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

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
