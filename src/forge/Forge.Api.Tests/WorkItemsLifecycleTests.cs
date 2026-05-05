// -----------------------------------------------------------------------
// <copyright file="WorkItemsLifecycleTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Phase 12.11 — single end-to-end happy-path that the Playwright suite is
/// supposed to cover. Replaces it server-side: create → patch (title +
/// description) → move (state + iteration) → reparent → soft-delete, with
/// revision advances asserted at each step.
/// </summary>
public class WorkItemsLifecycleTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Create_Patch_Move_Reparent_SoftDelete_Roundtrip()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // 1) Create the parent epic.
        var epicResp = await client.PostAsJsonAsync("/api/work-items", new
        {
            id = "LIFE-EP",
            track = "dev",
            type = "epic",
            title = "Lifecycle epic",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, epicResp.StatusCode);

        // 2) Create the feature with no parent yet.
        var featResp = await client.PostAsJsonAsync("/api/work-items", new
        {
            id = "LIFE-FT",
            track = "dev",
            type = "feature",
            title = "Lifecycle feature",
            iterationRef = "atlas-platform:sprint-14",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, featResp.StatusCode);
        var featCreated = JsonDocument.Parse(await featResp.Content.ReadAsStringAsync()).RootElement;
        var rev0 = featCreated.GetProperty("remoteRevision").GetString();
        Assert.NotNull(rev0);
        Assert.Equal("proposed", featCreated.GetProperty("state").GetString());
        Assert.True(featCreated.GetProperty("parentId").ValueKind == JsonValueKind.Null);

        // 3) Patch title + description; revision must advance.
        var patchResp = await client.PatchAsJsonAsync("/api/work-items/LIFE-FT", new
        {
            title = "Lifecycle feature (renamed)",
            descriptionMd = "## Goal\n\nProve the loop works.",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = JsonDocument.Parse(await patchResp.Content.ReadAsStringAsync()).RootElement;
        var rev1 = patched.GetProperty("remoteRevision").GetString();
        Assert.NotEqual(rev0, rev1);
        Assert.Equal("Lifecycle feature (renamed)", patched.GetProperty("title").GetString());

        // 4) Move state + iteration. Revision advances again.
        var moveResp = await client.PostAsJsonAsync("/api/work-items/LIFE-FT/move", new
        {
            state = "active",
            iterationRef = "atlas-platform:sprint-15",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, moveResp.StatusCode);
        var moved = JsonDocument.Parse(await moveResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("active", moved.GetProperty("state").GetString());
        Assert.Equal("atlas-platform:sprint-15", moved.GetProperty("iterationRef").GetString());
        var rev2 = moved.GetProperty("remoteRevision").GetString();
        Assert.NotEqual(rev1, rev2);

        // 5) Reparent under the epic.
        var reparentResp = await client.PostAsJsonAsync("/api/work-items/LIFE-FT/parent", new
        {
            parentId = "LIFE-EP",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, reparentResp.StatusCode);
        var reparented = JsonDocument.Parse(await reparentResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("LIFE-EP", reparented.GetProperty("parentId").GetString());

        // 6) Soft delete — flips state to closed but the row stays addressable.
        var delResp = await client.DeleteAsync("/api/work-items/LIFE-FT");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
        var deleted = JsonDocument.Parse(await delResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("closed", deleted.GetProperty("state").GetString());

        // 7) Final read confirms persistence of all four mutations.
        var getResp = await client.GetAsync("/api/work-items/LIFE-FT");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var final = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Lifecycle feature (renamed)", final.GetProperty("title").GetString());
        Assert.Equal("closed", final.GetProperty("state").GetString());
        Assert.Equal("atlas-platform:sprint-15", final.GetProperty("iterationRef").GetString());
        Assert.Equal("LIFE-EP", final.GetProperty("parentId").GetString());
    }

    [Fact]
    public async Task Hierarchy_Filter_Returns_Children_Of_Parent()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Build a small tree.
        await client.PostAsJsonAsync("/api/work-items", new { id = "PHASE-X", track = "design", type = "phase", title = "Phase X" }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new { id = "STAGE-X1", track = "design", type = "stage", title = "Stage X1", parentId = "PHASE-X" }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new { id = "STAGE-X2", track = "design", type = "stage", title = "Stage X2", parentId = "PHASE-X" }, JsonOpts);
        await client.PostAsJsonAsync("/api/work-items", new { id = "STEP-Y",  track = "design", type = "step",  title = "Step Y", parentId = "STAGE-X1" }, JsonOpts);

        // Children of PHASE-X = both stages, not the deeper step.
        // `productId=all` bypasses the default product scope, which would
        // otherwise filter out work items with null RepoId (the ones we
        // just created — the API doesn't auto-attach a repo on create).
        var resp = await client.GetAsync("/api/work-items?parentId=PHASE-X&productId=all");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "STAGE-X1", "STAGE-X2" }, ids);
    }
}
