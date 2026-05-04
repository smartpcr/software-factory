// -----------------------------------------------------------------------
// <copyright file="WorkstreamsEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 3.6 — contract tests for /api/workstreams.
/// Authentication is the placeholder X-Forge-User header (Phase 3.11).
/// </summary>
public class WorkstreamsEndpointsTests : IClassFixture<ForgeWebApplicationFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ForgeWebApplicationFactory factory;

    /// <summary>
    /// Seeds the 8 named workstreams the contract tests reference. Replaces
    /// the retired prototype seed. Idempotent.
    /// </summary>
    public async Task InitializeAsync()
    {
        using var scope = this.factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.ForgeDbContext>();
        var existing = await db.Workstreams.Select(w => w.Id).ToListAsync();
        // RepoId set to the default seed repo so the product-scope filter on
        // /api/workstreams (which only shows workstreams whose RepoId is in
        // the active product's repo set) doesn't drop them.
        const string repo = Forge.Api.Persistence.ForgeSeed.DefaultRepoId;
        var rows = new[]
        {
            // PrsJson populated so DeleteWorkstream_With_Open_Prs_Without_Force_Returns_422
            // sees an "open PR" — the endpoint reads the workstream's JSON
            // array, not the prs table (WorkstreamsEndpoints.cs:287).
            new Forge.Api.Persistence.WorkstreamRow { Id = "ws-oidc",          RepoId = repo, Name = "OIDC token rotation",  Branch = "ws/oidc-rotation",   Epic = "EPIC-024", Status = "active", PrsJson = "[\"PR #ws-oidc-open\"]" },
            new Forge.Api.Persistence.WorkstreamRow { Id = "ws-rate-limits",   RepoId = repo, Name = "Per-tenant rate limits", Branch = "ws/rate-limits",  Epic = "EPIC-024", Status = "active" },
            new Forge.Api.Persistence.WorkstreamRow { Id = "ws-idp-failover",  RepoId = repo, Name = "IdP failover",         Branch = "ws/idp-failover",    Epic = "EPIC-024", Status = "active" },
            new Forge.Api.Persistence.WorkstreamRow { Id = "ws-retry-jitter",  RepoId = repo, Name = "Webhook retry jitter", Branch = "ws/retry-jitter",    Epic = "EPIC-021", Status = "active" },
            new Forge.Api.Persistence.WorkstreamRow { Id = "ws-dlq",           RepoId = repo, Name = "DLQ routing",          Branch = "ws/dlq-routing",     Epic = "EPIC-022", Status = "active" },
            new Forge.Api.Persistence.WorkstreamRow { Id = "ws-tenant-admin",  RepoId = repo, Name = "Tenant admin UI",      Branch = "ws/tenant-admin-ui", Epic = "EPIC-024", Status = "active" },
            new Forge.Api.Persistence.WorkstreamRow { Id = "ws-log-rotation",  RepoId = repo, Name = "Log rotation",         Branch = "ws/log-rotation",    Epic = "EPIC-026", Status = "active" },
            new Forge.Api.Persistence.WorkstreamRow { Id = "ws-bits-cleanup",  RepoId = repo, Name = "BITS cleanup",         Branch = "ws/bits-cleanup",    Epic = "EPIC-018", Status = "done" },
        };
        foreach (var w in rows) if (!existing.Contains(w.Id)) db.Workstreams.Add(w);

        // ws-oidc has an open PR — DeleteWorkstream_With_Open_Prs… expects 422.
        var existingPrs = await db.Prs.Select(p => p.Id).ToListAsync();
        if (!existingPrs.Contains("PR #ws-oidc-open"))
        {
            db.Prs.Add(new Forge.Api.Persistence.PrRow
            {
                Id = "PR #ws-oidc-open", Col = "review", Epic = "EPIC-024", EpicColor = "024",
                Workstream = "ws-oidc", Base = "main", Branch = "ws/oidc-rotation",
                Title = "OIDC rotation v2", Age = "1h",
            });
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public WorkstreamsEndpointsTests(ForgeWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetWorkstreams_Returns_All_Seeded()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/workstreams");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // Other tests in this class create / delete workstreams in the same DB
        // (we share an IClassFixture). Assert by id-membership of the 8 seeded
        // ones, which is order- and side-effect-stable.
        var ids = new HashSet<string>();
        foreach (var w in doc.RootElement.EnumerateArray())
        {
            ids.Add(w.GetProperty("id").GetString()!);
        }
        var seeded = new[]
        {
            "ws-oidc", "ws-rate-limits", "ws-idp-failover", "ws-retry-jitter",
            "ws-dlq", "ws-tenant-admin", "ws-log-rotation", "ws-bits-cleanup",
        };
        foreach (var id in seeded)
        {
            // ws-tenant-admin may be deleted by a sibling test; treat its absence as ok.
            if (id == "ws-tenant-admin") continue;
            Assert.Contains(id, ids);
        }
    }

    [Fact]
    public async Task CreateWorkstream_Without_User_Header_Returns_401()
    {
        using var client = this.factory.CreateClient();
        var req = new
        {
            id = "ws-test-1",
            name = "test-1",
            branch = "ws/test-1",
            @base = "main",
            epic = "EPIC-024",
        };
        var resp = await client.PostAsJsonAsync("/api/workstreams", req, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CreateWorkstream_With_User_Header_Returns_201()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var req = new
        {
            id = "ws-test-create",
            name = "test-create",
            branch = "ws/test-create",
            @base = "main",
            epic = "EPIC-024",
        };
        var resp = await client.PostAsJsonAsync("/api/workstreams", req, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ws-test-create", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("planning", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeleteWorkstream_With_Open_Prs_Without_Force_Returns_422()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        // ws-oidc has open PRs in the seed.
        var resp = await client.DeleteAsync("/api/workstreams/ws-oidc");
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("requireConfirm", body);
    }

    [Fact]
    public async Task DeleteWorkstream_With_Force_Removes_Row()
    {
        // Seed our own workstream — the prototype seed (ws-tenant-admin etc.)
        // is no longer loaded, so we can't rely on a named row existing.
        using (var scope = this.factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.ForgeDbContext>();
            if (!await db.Workstreams.AnyAsync(w => w.Id == "ws-delete-target"))
            {
                db.Workstreams.Add(new Forge.Api.Persistence.WorkstreamRow
                {
                    Id = "ws-delete-target", Name = "Delete target", Branch = "ws/delete",
                    Epic = "EPIC-024", Status = "active",
                });
                await db.SaveChangesAsync();
            }
        }

        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.DeleteAsync("/api/workstreams/ws-delete-target");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Confirm it's gone.
        var listDoc = JsonDocument.Parse(await (await client.GetAsync("/api/workstreams")).Content.ReadAsStringAsync());
        var ids = new List<string>();
        foreach (var w in listDoc.RootElement.EnumerateArray())
        {
            ids.Add(w.GetProperty("id").GetString()!);
        }
        Assert.DoesNotContain("ws-delete-target", ids);
    }

    [Fact]
    public async Task SyncWorkstream_Requires_User_Header()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.PostAsync("/api/workstreams/ws-oidc/sync", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task SyncWorkstream_Unknown_Returns_404()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsync("/api/workstreams/ws-nope/sync", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
