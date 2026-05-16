// -----------------------------------------------------------------------
// <copyright file="Phase15SprintTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers.Ado;
using Forge.Api.Providers.GitHub;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Phase 15.12 — sprint registry endpoint contract tests. Stubs the
/// classification + milestone services so the tests run without network.
/// Covers the seven items called out in the spec:
///   (a) discovery returns the expected subtree for a prefix
///   (b) bulk import is idempotent — second call only refreshes
///   (c) GitHub milestone state mapping
///   (d) repo binding with role='current' enforces uniqueness
///   (e) orphan handling — deleted upstream node flips state without
///       dropping bindings
///   (f) Product.activeIterationId override beats the primary-repo fallback
///   (g) saved-prefix refresh appends new nodes without duplicating.
/// </summary>
public class Phase15SprintTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Stubs ───────────────────────────────────────────────────────────

    private sealed class StubAdo : IAdoClassificationService
    {
        public List<DiscoveryNode> Iterations { get; set; } = new();
        public List<DiscoveryNode> Areas { get; set; } = new();
        public Task<IReadOnlyList<DiscoveryNode>> DiscoverAsync(RepoRow repo, string kind, string? prefix, CancellationToken ct = default)
        {
            var src = string.Equals(kind, "areas", StringComparison.OrdinalIgnoreCase) ? this.Areas : this.Iterations;
            var trimmed = (prefix ?? string.Empty).Trim().TrimEnd('\\');
            IReadOnlyList<DiscoveryNode> result = string.IsNullOrEmpty(trimmed)
                ? src
                : src.Where(n => n.Path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(result);
        }
    }

    private sealed class StubGitHub : IGitHubMilestoneService
    {
        public List<DiscoveryNode> Milestones { get; set; } = new();
        public Task<IReadOnlyList<DiscoveryNode>> DiscoverAsync(RepoRow repo, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DiscoveryNode>>(this.Milestones);
    }

    private sealed class StubFactory : ForgeWebApplicationFactory
    {
        public StubAdo Ado { get; } = new();
        public StubGitHub GitHub { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IAdoClassificationService>();
                s.RemoveAll<IGitHubMilestoneService>();
                s.AddSingleton<IAdoClassificationService>(this.Ado);
                s.AddSingleton<IGitHubMilestoneService>(this.GitHub);
            });
        }
    }

    private static HttpClient WriterClient(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        return c;
    }

    private static async Task<RepoRow> EnsureAdoRepo(StubFactory f, string id = "atlas-platform-test")
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var existing = await db.Repos.FirstOrDefaultAsync(r => r.Id == id);
        if (existing is not null) return existing;
        var repo = new RepoRow
        {
            Id = id,
            ProductId = "atlas",
            Kind = "ado",
            RemoteUrl = $"https://example.com/{id}.git",
            DisplayName = id,
            DefaultBranch = "main",
            CloneRoot = $"C:\\tmp\\{id}",
            AdoOrg = "contoso",
            AdoProject = "Atlas",
        };
        db.Repos.Add(repo);
        await db.SaveChangesAsync();
        return repo;
    }

    private static DiscoveryNode IterNode(string path, string nodeId, string timeFrame = "future")
        => new(nodeId, path, path[(path.LastIndexOf('\\') + 1)..], false, 1,
            new DiscoveryNodeAttributes(
                StartDate: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                FinishDate: new DateTimeOffset(2026, 4, 14, 0, 0, 0, TimeSpan.Zero),
                TimeFrame: timeFrame),
            AlreadyImported: false);

    // ── (a) discovery: subtree filtered by prefix ───────────────────────

    [Fact]
    public async Task Discovery_Returns_Subtree_For_Prefix()
    {
        using var f = new StubFactory();
        var repo = await EnsureAdoRepo(f);
        f.Ado.Iterations =
        [
            IterNode("Atlas\\Sprint 12", "ado:iter:12", "past"),
            IterNode("Atlas\\Sprint 13", "ado:iter:13", "current"),
            IterNode("Atlas\\Releases\\v2", "ado:iter:99"),
        ];

        using var c = WriterClient(f);
        var resp = await c.PostAsJsonAsync("/api/products/atlas/sprint-discovery",
            new SprintDiscoveryRequest(repo.Id, "iterations", "Atlas\\Sprint"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var nodes = await resp.Content.ReadFromJsonAsync<DiscoveryNode[]>(Json);
        Assert.NotNull(nodes);
        Assert.Equal(2, nodes!.Length);
        Assert.All(nodes, n => Assert.StartsWith("Atlas\\Sprint", n.Path));
    }

    // ── (b) idempotency ─────────────────────────────────────────────────

    [Fact]
    public async Task Bulk_Import_Is_Idempotent()
    {
        using var f = new StubFactory();
        var repo = await EnsureAdoRepo(f);
        f.Ado.Iterations =
        [
            IterNode("Atlas\\Sprint 14", "ado:iter:14", "current"),
            IterNode("Atlas\\Sprint 15", "ado:iter:15"),
        ];

        using var c = WriterClient(f);
        var first = await c.PostAsJsonAsync("/api/products/atlas/iterations/import",
            new ImportNodesRequest(repo.Id, new[] { "ado:iter:14", "ado:iter:15" }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // First DB row's import + refresh stamps.
        DateTimeOffset firstImported, firstRefreshed;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = await db.ProductIterations.AsNoTracking()
                .FirstAsync(i => i.ProviderNodeId == "ado:iter:14");
            firstImported = row.ImportedAt;
            firstRefreshed = row.RefreshedAt;
        }
        await Task.Delay(10);

        var second = await c.PostAsJsonAsync("/api/products/atlas/iterations/import",
            new ImportNodesRequest(repo.Id, new[] { "ado:iter:14", "ado:iter:15" }));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            // Still only two rows — re-import is upsert by (ProductId, ProviderNodeId).
            var count = await db.ProductIterations.CountAsync(i => i.ProductId == "atlas");
            Assert.Equal(2, count);
            var refreshed = await db.ProductIterations.AsNoTracking()
                .FirstAsync(i => i.ProviderNodeId == "ado:iter:14");
            Assert.Equal(firstImported, refreshed.ImportedAt);     // never changes after first import
            Assert.True(refreshed.RefreshedAt > firstRefreshed);    // bumped on every re-run
        }
    }

    // ── (c) GitHub milestone state mapping ──────────────────────────────

    [Fact]
    public async Task GitHub_Milestone_State_Maps_To_TimeFrame()
    {
        using var f = new StubFactory();
        // Repo is github-kind; no need for AdoProject.
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.Repos.Add(new RepoRow
        {
            Id = "atlas-portal-test", ProductId = "atlas", Kind = "github",
            RemoteUrl = "https://github.com/atlas-corp/portal", DisplayName = "portal",
            DefaultBranch = "main", CloneRoot = "C:\\tmp\\portal",
            GhOwner = "atlas-corp", GhRepo = "portal",
        });
        await db.SaveChangesAsync();
        scope.Dispose();

        f.GitHub.Milestones =
        [
            new("milestone:1", "#1 v1.0", "v1.0", false, 0,
                new DiscoveryNodeAttributes(null, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), "future"), false),
        ];

        using var c = WriterClient(f);
        var resp = await c.PostAsJsonAsync("/api/products/atlas/sprint-discovery",
            new SprintDiscoveryRequest("atlas-portal-test", "iterations", null));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var nodes = await resp.Content.ReadFromJsonAsync<DiscoveryNode[]>(Json);
        Assert.NotNull(nodes);
        Assert.Single(nodes!);
        Assert.Equal("future", nodes![0].Attributes.TimeFrame);
    }

    // ── (d) binding role uniqueness ─────────────────────────────────────

    [Fact]
    public async Task Repo_Iteration_Binding_Current_Role_Is_Unique()
    {
        using var f = new StubFactory();
        var repo = await EnsureAdoRepo(f);
        f.Ado.Iterations =
        [
            IterNode("Atlas\\Sprint 14", "ado:iter:14"),
            IterNode("Atlas\\Sprint 15", "ado:iter:15"),
        ];

        using var c = WriterClient(f);
        // Import both so we have iteration ids to bind to.
        var imp = await c.PostAsJsonAsync("/api/products/atlas/iterations/import",
            new ImportNodesRequest(repo.Id, new[] { "ado:iter:14", "ado:iter:15" }));
        Assert.Equal(HttpStatusCode.OK, imp.StatusCode);
        string iter14, iter15;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            iter14 = (await db.ProductIterations.AsNoTracking()
                .FirstAsync(i => i.ProviderNodeId == "ado:iter:14")).Id;
            iter15 = (await db.ProductIterations.AsNoTracking()
                .FirstAsync(i => i.ProviderNodeId == "ado:iter:15")).Id;
        }

        var bind1 = await c.PostAsJsonAsync($"/api/repos/{repo.Id}/iteration-binding",
            new IterationBindingRequest(iter14, "current"));
        Assert.Equal(HttpStatusCode.Created, bind1.StatusCode);

        // Same iteration + same role → idempotent OK.
        var bind1Again = await c.PostAsJsonAsync($"/api/repos/{repo.Id}/iteration-binding",
            new IterationBindingRequest(iter14, "current"));
        Assert.Equal(HttpStatusCode.OK, bind1Again.StatusCode);

        // Different iteration, same role → 409.
        var bind2 = await c.PostAsJsonAsync($"/api/repos/{repo.Id}/iteration-binding",
            new IterationBindingRequest(iter15, "current"));
        Assert.Equal(HttpStatusCode.Conflict, bind2.StatusCode);

        // Same repo can hold a 'next' role too — independent slot.
        var bindNext = await c.PostAsJsonAsync($"/api/repos/{repo.Id}/iteration-binding",
            new IterationBindingRequest(iter15, "next"));
        Assert.Equal(HttpStatusCode.Created, bindNext.StatusCode);
    }

    // ── (e) orphan handling ─────────────────────────────────────────────

    [Fact]
    public async Task Refresh_Flips_Orphans_Without_Dropping_Bindings()
    {
        using var f = new StubFactory();
        var repo = await EnsureAdoRepo(f);
        f.Ado.Iterations =
        [
            IterNode("Atlas\\Sprint 14", "ado:iter:14"),
            IterNode("Atlas\\Sprint 15", "ado:iter:15"),
        ];

        using var c = WriterClient(f);
        await c.PostAsJsonAsync("/api/products/atlas/iterations/import",
            new ImportNodesRequest(repo.Id, new[] { "ado:iter:14", "ado:iter:15" }));

        // Bind iter:15 as 'current' to prove the binding survives orphaning.
        string iter15Id;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            iter15Id = (await db.ProductIterations.AsNoTracking()
                .FirstAsync(i => i.ProviderNodeId == "ado:iter:15")).Id;
        }
        await c.PostAsJsonAsync($"/api/repos/{repo.Id}/iteration-binding",
            new IterationBindingRequest(iter15Id, "current"));

        // Save a prefix so we have something to refresh.
        var save = await c.PostAsJsonAsync("/api/products/atlas/saved-prefixes",
            new SavePrefixRequest("Atlas\\Sprint", "iterations", repo.Id));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var savedDoc = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
        var prefixId = savedDoc.RootElement.GetProperty("id").GetString();

        // Upstream now drops Sprint 15 from the discovery response.
        f.Ado.Iterations =
        [
            IterNode("Atlas\\Sprint 14", "ado:iter:14"),
        ];

        var refresh = await c.PostAsync($"/api/products/atlas/saved-prefixes/{prefixId}/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var orphan = await db.ProductIterations.AsNoTracking()
                .FirstAsync(i => i.ProviderNodeId == "ado:iter:15");
            Assert.Equal("provider-orphan", orphan.State);
            // Binding is intact.
            var bindings = await db.RepoIterationBindings.AsNoTracking()
                .Where(b => b.ProductIterationId == iter15Id).ToListAsync();
            Assert.Single(bindings);
            Assert.Equal("current", bindings[0].Role);
        }
    }

    // ── (f) active iteration override ───────────────────────────────────

    [Fact]
    public async Task Product_ActiveIterationId_Override_Round_Trips()
    {
        using var f = new StubFactory();
        var repo = await EnsureAdoRepo(f);
        f.Ado.Iterations = [IterNode("Atlas\\Sprint 14", "ado:iter:14", "current")];

        using var c = WriterClient(f);
        await c.PostAsJsonAsync("/api/products/atlas/iterations/import",
            new ImportNodesRequest(repo.Id, new[] { "ado:iter:14" }));
        string iterId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            iterId = (await db.ProductIterations.AsNoTracking()
                .FirstAsync(i => i.ProviderNodeId == "ado:iter:14")).Id;
        }

        var patch = await c.PatchAsJsonAsync("/api/products/atlas",
            new UpdateProductRequest(ActiveIterationId: iterId));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var product = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(iterId, product.GetProperty("activeIterationId").GetString());

        // Setting an iteration that doesn't belong to the product → 400.
        var bogus = await c.PatchAsJsonAsync("/api/products/atlas",
            new UpdateProductRequest(ActiveIterationId: "does-not-exist"));
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);

        // Empty string clears the override.
        var clear = await c.PatchAsJsonAsync("/api/products/atlas",
            new UpdateProductRequest(ActiveIterationId: string.Empty));
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var cleared = await clear.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(cleared.TryGetProperty("activeIterationId", out var v) && v.ValueKind == JsonValueKind.String);
    }

    // ── (g) saved-prefix refresh — adds new nodes ───────────────────────

    [Fact]
    public async Task SavedPrefix_Refresh_Appends_New_Nodes()
    {
        using var f = new StubFactory();
        var repo = await EnsureAdoRepo(f);
        f.Ado.Iterations =
        [
            IterNode("Atlas\\Sprint 14", "ado:iter:14"),
            IterNode("Atlas\\Sprint 15", "ado:iter:15"),
        ];

        using var c = WriterClient(f);
        // Initial import + save prefix.
        await c.PostAsJsonAsync("/api/products/atlas/iterations/import",
            new ImportNodesRequest(repo.Id, new[] { "ado:iter:14", "ado:iter:15" }));
        var save = await c.PostAsJsonAsync("/api/products/atlas/saved-prefixes",
            new SavePrefixRequest("Atlas\\Sprint", "iterations", repo.Id));
        var savedDoc = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
        var prefixId = savedDoc.RootElement.GetProperty("id").GetString();

        // ADO admin cuts Sprint 16 — refresh picks it up.
        f.Ado.Iterations =
        [
            IterNode("Atlas\\Sprint 14", "ado:iter:14"),
            IterNode("Atlas\\Sprint 15", "ado:iter:15"),
            IterNode("Atlas\\Sprint 16", "ado:iter:16"),
        ];
        var refresh = await c.PostAsync($"/api/products/atlas/saved-prefixes/{prefixId}/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var savedAfter = await refresh.Content.ReadFromJsonAsync<SavedPrefix>(Json);
        Assert.NotNull(savedAfter);
        Assert.Equal(1, savedAfter!.Delta.Added);
        Assert.Equal(0, savedAfter.Delta.Removed);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            // Three rows total — no duplicates.
            var ids = await db.ProductIterations.AsNoTracking()
                .Where(i => i.ProductId == "atlas")
                .Select(i => i.ProviderNodeId).ToListAsync();
            Assert.Equal(3, ids.Distinct().Count());
            Assert.Contains("ado:iter:16", ids);
        }
    }
}

// PatchAsJsonAsync is in Microsoft.Extensions.Http but not on HttpClient by default.
// Provide a thin extension that mirrors PostAsJsonAsync.
internal static class HttpClientPatchExtensions
{
    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(
        this HttpClient c, string url, T body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        return c.SendAsync(req, ct);
    }
}
