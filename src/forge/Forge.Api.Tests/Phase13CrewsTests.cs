// -----------------------------------------------------------------------
// <copyright file="Phase13CrewsTests.cs" company="Microsoft Corp.">
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
/// Phase 13.11 — contract tests for the new crew surface area:
///   * 13.1 schema (Backend / InvokeMode / hierarchy depth cap)
///   * 13.2 product-scoped role (lead-or-PO assign authorization)
///   * 13.3 assign endpoint
///   * 13.4 / 13.5 CLI-mode refusal (501 path stubs)
///   * 13.6 concurrency-cap enforcement
///   * 13.8 capability ingestion + content-id integrity
///   * 13.1 history readout
/// Uses a per-test factory so SQLite is fresh.
/// </summary>
public class Phase13CrewsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Create_Defaults_Backend_Copilot_And_Mode_Sdk()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var resp = await c.PostAsJsonAsync("/api/crews/", new
        {
            id = "crew-a", name = "A", productId = (string?)null, summary = "", lead = (string?)null,
            model = "local-stub", modelInputPricePerMTok = 1m, modelOutputPricePerMTok = 1m,
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("copilot", doc.RootElement.GetProperty("backend").GetString());
        Assert.Equal("sdk",     doc.RootElement.GetProperty("invokeMode").GetString());
        Assert.Equal(0,         doc.RootElement.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Create_Hierarchy_Caps_At_Depth_2()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "root");
        await Make(c, "team", parent: "root");
        await Make(c, "squad", parent: "team");
        // 4th level should fail.
        var resp = await c.PostAsJsonAsync("/api/crews/", new { id = "atom", name = "Atom", parentCrewId = "squad", model = "local-stub" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_Backend_Writes_Crew_History()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "h1");
        var patch = await c.PatchAsJsonAsync("/api/crews/h1", new { backend = "claude" }, JsonOpts);
        Assert.True(patch.IsSuccessStatusCode);

        var hist = await c.GetAsync("/api/crews/h1/history");
        var rows = JsonDocument.Parse(await hist.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("backend", rows[0].GetProperty("kind").GetString());
        Assert.Equal("copilot", rows[0].GetProperty("fromValue").GetString());
        Assert.Equal("claude",  rows[0].GetProperty("toValue").GetString());
    }

    [Fact]
    public async Task Patch_Unknown_Backend_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "u1");
        var resp = await c.PatchAsJsonAsync("/api/crews/u1", new { backend = "openai" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Plan_And_Code_Refuse_Cli_Mode()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "cli1", invokeMode: "cli");
        // Phase 14.2 — disable schedule so this test stays deterministic
        // regardless of wall-clock; it's about CLI-mode refusal, not scheduling.
        await DisableSchedule(f, "cli1");
        var plan = await c.PostAsJsonAsync("/api/crews/cli1/plan", new { workItemId = "wi-1" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, plan.StatusCode);
        var code = await c.PostAsJsonAsync("/api/crews/cli1/code", new { stepWorkItemId = "wi-1" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, code.StatusCode);
    }

    [Fact]
    public async Task Plan_On_Sdk_Crew_Returns_501_Stub()
    {
        // Phase 13.4 not implemented yet; ensures the stub fires deterministically
        // so the UI can disable the button on 501 without mistaking it for a 400.
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "sdk1");
        // Phase 14.2 — disable schedule so we deterministically reach the 501 stub.
        await DisableSchedule(f, "sdk1");
        var plan = await c.PostAsJsonAsync("/api/crews/sdk1/plan", new { workItemId = "wi-1" }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotImplemented, plan.StatusCode);
    }

    /// <summary>Phase 14.2 — turn off the schedule gate on a crew row for tests that don't care about 9-9-6.</summary>
    private static async Task DisableSchedule(ForgeWebApplicationFactory f, string crewId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await db.Crews.FirstAsync(x => x.Id == crewId);
        row.ScheduleEnabled = false;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Capability_Add_Validates_ContentId_Exists_In_Index()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "cap1");

        // Unknown content-id → 404.
        var miss = await c.PostAsJsonAsync("/api/crews/cap1/capabilities", new { contentId = "sha256:00" }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);

        // Seed a domain expert directly into the DB then retry.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.DomainExperts.Add(new DomainExpertRow
            {
                ContentId = "sha256:abc",
                RepoId = "repo-x",
                Path = "docs/domain/x.md",
                Topic = "auth",
                Summary = "auth invariants",
            });
            await db.SaveChangesAsync();
        }
        var hit = await c.PostAsJsonAsync("/api/crews/cap1/capabilities", new { contentId = "sha256:abc" }, JsonOpts);
        Assert.True(hit.IsSuccessStatusCode);
        var crew = JsonDocument.Parse(await hit.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("sha256:abc", crew.GetProperty("capabilities")[0].GetString());

        // Idempotent — adding twice doesn't duplicate.
        var hit2 = await c.PostAsJsonAsync("/api/crews/cap1/capabilities", new { contentId = "sha256:abc" }, JsonOpts);
        Assert.True(hit2.IsSuccessStatusCode);
        var crew2 = JsonDocument.Parse(await hit2.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, crew2.GetProperty("capabilities").GetArrayLength());
    }

    [Fact]
    public async Task Assign_Refuses_When_Member_Not_In_Roster()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        await Make(c, "asg1");

        var resp = await c.PostAsJsonAsync("/api/crews/asg1/assign", new
        {
            workItemId = "wi-1", memberAgentId = "ghost", force = false,
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task Make(HttpClient c, string id, string? parent = null, string invokeMode = "sdk")
    {
        var resp = await c.PostAsJsonAsync("/api/crews/", new
        {
            id,
            name = id,
            productId = (string?)null,
            summary = "",
            lead = (string?)null,
            model = "local-stub",
            modelInputPricePerMTok = 1m,
            modelOutputPricePerMTok = 1m,
            parentCrewId = parent,
            invokeMode,
        }, JsonOpts);
        if (resp.StatusCode != HttpStatusCode.Created)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"create {id}: {(int)resp.StatusCode} {body}");
        }
    }
}
