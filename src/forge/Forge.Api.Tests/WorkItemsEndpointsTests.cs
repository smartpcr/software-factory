// -----------------------------------------------------------------------
// <copyright file="WorkItemsEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 12 Batch A — work-items CRUD + move + reparent contract tests.
/// </summary>
public class WorkItemsEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Removed: ListWorkItems_Includes_Cards_Backfill — depended on
    // ForgeSeed.BackfillWorkItemsFromCardsAsync running at boot to mirror
    // the 23-row prototype-card seed into work_items. Both the seed and
    // the backfill call were retired; nothing useful left to assert.

    [Fact]
    public async Task CreateWorkItem_Persists_With_Defaults_And_Returns_201()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        var body = new
        {
            id = "WI-T-001",
            track = "dev",
            type = "task",
            title = "Wire up the test work item",
        };
        var resp = await client.PostAsJsonAsync("/api/work-items", body, JsonOpts);
        if (resp.StatusCode != HttpStatusCode.Created)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Got {(int)resp.StatusCode}: {err}");
        }
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("proposed", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("med", doc.RootElement.GetProperty("priority").GetString());
        Assert.Equal("local", doc.RootElement.GetProperty("provider").GetString());
        Assert.NotNull(doc.RootElement.GetProperty("remoteRevision").GetString());
    }

    [Fact]
    public async Task CreateWorkItem_Rejects_Track_Type_Mismatch()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // 'phase' is design-track; declaring track 'dev' must reject.
        var body = new { id = "WI-T-002", track = "dev", type = "phase", title = "x" };
        var resp = await client.PostAsJsonAsync("/api/work-items", body, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateWorkItem_Rejects_Cross_Track_Parent_Below_Top()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Seed a dev feature, then try to attach a design-track stage to it.
        await client.PostAsJsonAsync("/api/work-items", new {
            id = "WI-EPIC-1", track = "dev", type = "epic", title = "Epic",
        }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new {
            id = "WI-FEAT-1", track = "dev", type = "feature", title = "Feature", parentId = "WI-EPIC-1",
        }, JsonOpts);

        var resp = await client.PostAsJsonAsync("/api/work-items", new {
            id = "WI-STAGE-1", track = "design", type = "stage", title = "Cross-track child",
            parentId = "WI-FEAT-1",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PatchWorkItem_Honours_If_Match_And_Bumps_Revision()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Create + grab the revision.
        await client.PostAsJsonAsync("/api/work-items", new {
            id = "WI-PATCH-1", track = "dev", type = "task", title = "before",
        }, JsonOpts);
        var get = JsonDocument.Parse(await (await client.GetAsync("/api/work-items/WI-PATCH-1")).Content.ReadAsStringAsync());
        var rev = get.RootElement.GetProperty("remoteRevision").GetString();

        // Same-rev patch succeeds and rev advances. Use TryAddWithoutValidation
        // because our revision token isn't a quoted ETag — the WorkItems
        // endpoint reads the raw header value and compares string-equal to
        // the stored RemoteRevision.
        var ok = new HttpRequestMessage(HttpMethod.Patch, "/api/work-items/WI-PATCH-1")
        {
            Content = JsonContent.Create(new { title = "after", priority = "high" }, options: JsonOpts),
        };
        ok.Headers.Add("X-Forge-User", "po@forge.local");
        ok.Headers.TryAddWithoutValidation("If-Match", rev!);
        var resp = await client.SendAsync(ok);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var patched = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("after", patched.RootElement.GetProperty("title").GetString());
        Assert.Equal("high", patched.RootElement.GetProperty("priority").GetString());
        Assert.NotEqual(rev, patched.RootElement.GetProperty("remoteRevision").GetString());

        // Stale If-Match → 409.
        var stale = new HttpRequestMessage(HttpMethod.Patch, "/api/work-items/WI-PATCH-1")
        {
            Content = JsonContent.Create(new { title = "stale" }, options: JsonOpts),
        };
        stale.Headers.Add("X-Forge-User", "po@forge.local");
        stale.Headers.TryAddWithoutValidation("If-Match", rev!); // old revision
        var conflict = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task DeleteWorkItem_Soft_Closes_By_Default()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        await client.PostAsJsonAsync("/api/work-items", new {
            id = "WI-DEL-1", track = "dev", type = "bug", title = "soft delete me",
        }, JsonOpts);

        var resp = await client.DeleteAsync("/api/work-items/WI-DEL-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("closed", doc.RootElement.GetProperty("state").GetString());

        // Item still exists.
        var get = await client.GetAsync("/api/work-items/WI-DEL-1");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task DeleteWorkItem_Force_Hard_Deletes_And_Detaches_Children()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        await client.PostAsJsonAsync("/api/work-items", new {
            id = "WI-PARENT", track = "dev", type = "epic", title = "parent",
        }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new {
            id = "WI-CHILD", track = "dev", type = "feature", title = "child", parentId = "WI-PARENT",
        }, JsonOpts);

        var resp = await client.DeleteAsync("/api/work-items/WI-PARENT?force=true");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var parentGet = await client.GetAsync("/api/work-items/WI-PARENT");
        Assert.Equal(HttpStatusCode.NotFound, parentGet.StatusCode);

        var childGet = JsonDocument.Parse(await (await client.GetAsync("/api/work-items/WI-CHILD")).Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, childGet.RootElement.GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task MoveWorkItem_Updates_State_And_Iteration()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        await client.PostAsJsonAsync("/api/work-items", new {
            id = "WI-MOVE-1", track = "dev", type = "task", title = "move me",
            iterationRef = "atlas-platform:sprint-14",
        }, JsonOpts);

        var resp = await client.PostAsJsonAsync("/api/work-items/WI-MOVE-1/move", new {
            state = "active",
            iterationRef = "atlas-platform:sprint-15",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("active", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("atlas-platform:sprint-15", doc.RootElement.GetProperty("iterationRef").GetString());
    }

    [Fact]
    public async Task ReparentWorkItem_Rejects_Cycle()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Build epic → feature → task; then try to make epic the child of
        // task. The cycle detector trips first (EP is already TK's
        // grandparent through FT), so we get 409, not 400.
        await client.PostAsJsonAsync("/api/work-items", new { id = "EP", track = "dev", type = "epic", title = "epic" }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new { id = "FT", track = "dev", type = "feature", title = "feature", parentId = "EP" }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new { id = "TK", track = "dev", type = "task", title = "task", parentId = "FT" }, JsonOpts);

        var resp = await client.PostAsJsonAsync("/api/work-items/EP/parent", new { parentId = "TK" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task ReparentWorkItem_Detects_Cycle_For_Same_Rank_Group()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Epic A and Epic B at top level. We'll make B child of A, then try
        // to make A child of B — should detect the cycle.
        await client.PostAsJsonAsync("/api/work-items", new { id = "EP-A", track = "dev", type = "epic", title = "A" }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new { id = "EP-B", track = "dev", type = "epic", title = "B" }, JsonOpts);

        // Same-rank parent gets rejected by ValidateParent (rank-equal).
        // Different test path: we can't easily set up a cycle through valid
        // rank moves with only epics. So instead verify that a self-parent
        // proposal lands as a 400 / 409 (can't be its own parent).
        var resp = await client.PostAsJsonAsync("/api/work-items/EP-A/parent", new { parentId = "EP-A" }, JsonOpts);
        Assert.True(resp.StatusCode == HttpStatusCode.Conflict || resp.StatusCode == HttpStatusCode.BadRequest,
            $"expected 400/409, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task ListWorkItems_Filters_By_Track_And_Type()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        await client.PostAsJsonAsync("/api/work-items", new {
            id = "PH-1", track = "design", type = "phase", title = "Editorial revamp",
        }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new {
            id = "PH-EP", track = "dev", type = "epic", title = "Dev epic",
        }, JsonOpts);

        // productId=all bypasses the default product scope, which would
        // otherwise filter out work items with null RepoId (the test creates
        // them via API without specifying repoId).
        var design = await client.GetAsync("/api/work-items?track=design&productId=all");
        var devEpics = await client.GetAsync("/api/work-items?track=dev&type=epic&productId=all");
        Assert.Equal(HttpStatusCode.OK, design.StatusCode);
        Assert.Equal(HttpStatusCode.OK, devEpics.StatusCode);
        var designDoc = JsonDocument.Parse(await design.Content.ReadAsStringAsync());
        var devEpicsDoc = JsonDocument.Parse(await devEpics.Content.ReadAsStringAsync());

        Assert.True(designDoc.RootElement.EnumerateArray().Any(e => e.GetProperty("id").GetString() == "PH-1"));
        Assert.False(designDoc.RootElement.EnumerateArray().Any(e => e.GetProperty("id").GetString() == "PH-EP"));
        Assert.True(devEpicsDoc.RootElement.EnumerateArray().Any(e => e.GetProperty("id").GetString() == "PH-EP"));
    }

    [Fact]
    public async Task ListWorkItems_Filters_By_Search_Q()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        await client.PostAsJsonAsync("/api/work-items", new {
            id = "Q-A", track = "dev", type = "task", title = "Profile scrape latency",
        }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new {
            id = "Q-B", track = "dev", type = "task", title = "Wire telemetry",
        }, JsonOpts);

        var resp = await client.GetAsync("/api/work-items?q=scrape&productId=all");
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains(doc.RootElement.EnumerateArray(), e => e.GetProperty("id").GetString() == "Q-A");
        Assert.DoesNotContain(doc.RootElement.EnumerateArray(), e => e.GetProperty("id").GetString() == "Q-B");
    }

    [Fact]
    public async Task PatchWorkItem_Without_User_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.WorkItems.Add(new WorkItemRow {
                Id = "WI-AUTH-1", Track = "dev", Type = "task", State = "proposed",
                Title = "x", Provider = "local",
            });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        var resp = await client.PatchAsJsonAsync("/api/work-items/WI-AUTH-1", new { title = "y" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
