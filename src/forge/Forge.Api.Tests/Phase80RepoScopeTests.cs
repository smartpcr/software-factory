// -----------------------------------------------------------------------
// <copyright file="Phase80RepoScopeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 80.E — `X-Forge-Repo-Id` read scope. Verifies that an active repo
/// narrows list endpoints to that one repo, that a repo id sent ALONE (no
/// product header) still routes + filters, that "all" disables the filter, and
/// that an unknown repo id is flagged in the diagnostic response header.
/// Exercises `/api/workstreams/` as the representative
/// `ProductScope.ResolveAsync` consumer (all 8 list endpoints share the path).
/// </summary>
public class Phase80RepoScopeTests
{
    private const string Atlas = ForgeSeed.DefaultProductId;       // "atlas"
    private const string Repo1 = ForgeSeed.DefaultRepoId;          // "atlas-platform" (seeded)
    private const string Repo2 = "rs-repo2";

    private static async Task SeedSecondRepoAndWorkstreamsAsync(ForgeWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.Repos.Add(new RepoRow
        {
            Id = Repo2, ProductId = Atlas, Kind = "github",
            RemoteUrl = "https://example/rs2", DisplayName = "rs2",
            DefaultBranch = "main", CloneRoot = @"C:\forge\rs2",
        });
        // RepoRegistrySync only runs at boot, so mirror the new repo by hand.
        control.RepoRegistry.Add(new RepoRegistryRow
        {
            Id = Repo2, ProductId = Atlas, ProductSlug = "atlas", Kind = "github",
            RemoteUrl = "https://example/rs2", CloneRoot = @"C:\forge\rs2", DisplayName = "rs2",
        });
        db.Workstreams.Add(new WorkstreamRow { Id = "rs-ws1", Name = "ws1", Branch = "ws/1", Base = "main", Epic = "E", Status = "active", RepoId = Repo1 });
        db.Workstreams.Add(new WorkstreamRow { Id = "rs-ws2", Name = "ws2", Branch = "ws/2", Base = "main", Epic = "E", Status = "active", RepoId = Repo2 });
        await db.SaveChangesAsync();
        await control.SaveChangesAsync();
    }

    private static async Task<(HashSet<string> Ids, string? Resolved, HttpStatusCode Code)> GetWorkstreamsAsync(
        ForgeWebApplicationFactory f, (string Name, string Value)[] headers)
    {
        using var client = f.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/workstreams/");
        foreach (var (name, value) in headers) req.Headers.Add(name, value);
        using var resp = await client.SendAsync(req);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String)
                    ids.Add(idp.GetString()!);
            }
        }
        var resolved = resp.Headers.TryGetValues(RepoScopeMiddleware.ScopeResolvedResponseHeader, out var v) ? v.FirstOrDefault() : null;
        return (ids, resolved, resp.StatusCode);
    }

    [Fact]
    public async Task ActiveRepo_NarrowsList_RepoIdAlone_Routes_UnknownFlagged_AllDisables()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot: seeds atlas + atlas-platform + registry */ }
        await SeedSecondRepoAndWorkstreamsAsync(f);

        // Product scope only → both repos' workstreams.
        var all = await GetWorkstreamsAsync(f, new[] { ("X-Forge-Product-Id", Atlas) });
        Assert.Equal(HttpStatusCode.OK, all.Code);
        Assert.Contains("rs-ws1", all.Ids);
        Assert.Contains("rs-ws2", all.Ids);

        // Repo scope (product + repo) → only that repo's workstream.
        var r2 = await GetWorkstreamsAsync(f, new[] { ("X-Forge-Product-Id", Atlas), ("X-Forge-Repo-Id", Repo2) });
        Assert.Contains("rs-ws2", r2.Ids);
        Assert.DoesNotContain("rs-ws1", r2.Ids);
        Assert.Equal(Repo2, r2.Resolved);

        // Repo id ALONE (no product header) → routes to the repo's product DB AND filters.
        var alone = await GetWorkstreamsAsync(f, new[] { ("X-Forge-Repo-Id", Repo2) });
        Assert.Equal(HttpStatusCode.OK, alone.Code);
        Assert.Contains("rs-ws2", alone.Ids);
        Assert.DoesNotContain("rs-ws1", alone.Ids);

        // Unknown repo id → flagged in the diagnostic header.
        var unknown = await GetWorkstreamsAsync(f, new[] { ("X-Forge-Product-Id", Atlas), ("X-Forge-Repo-Id", "no-such-repo") });
        Assert.Equal("(unknown:no-such-repo)", unknown.Resolved);

        // "all" → no repo filter, both workstreams returned.
        var allRepos = await GetWorkstreamsAsync(f, new[] { ("X-Forge-Product-Id", Atlas), ("X-Forge-Repo-Id", "all") });
        Assert.Contains("rs-ws1", allRepos.Ids);
        Assert.Contains("rs-ws2", allRepos.Ids);
        Assert.Equal("all", allRepos.Resolved);
    }
}
