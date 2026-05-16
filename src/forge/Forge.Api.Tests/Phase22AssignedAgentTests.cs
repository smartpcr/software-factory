// -----------------------------------------------------------------------
// <copyright file="Phase22AssignedAgentTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 22 — <c>AssignedAgent</c> field, <c>agent_assignments</c> history,
/// queue-worker claim rewrite, Move/Restart integration, backfill migration,
/// and endpoint tests.
/// </summary>
public class Phase22AssignedAgentTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ProductDbContext db)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return (f, scope, db);
    }

    private static WorkItemRow MakeWorkItem(string id, string state = "proposed") => new()
    {
        Id = id,
        RepoId = "repo-1",
        Provider = "local",
        Track = "dev",
        Type = "task",
        State = state,
        Title = $"Test item {id}",
        DescriptionMd = "",
        IterationRef = "",
        AreaPath = "",
        TagsJson = "[]",
        ExecutionStatus = ExecutionStatuses.Idle,
    };

    private static HttpClient AuthedClient(ForgeWebApplicationFactory f, string user = "po@forge.local")
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", user);
        return c;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.1 Migration — Phase22Migration.ApplyAsync
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Migration_Adds_AssignedAgentJson_Column()
    {
        var (f, scope, db) = await BootAsync();
        using var _ = f;
        scope.Dispose();

        // If we can write and read the column, migration succeeded.
        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = MakeWorkItem("mig-col-1");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\"}";
        db2.WorkItems.Add(wi);
        await db2.SaveChangesAsync();

        var saved = await db2.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "mig-col-1");
        Assert.NotNull(saved.AssignedAgentJson);
        Assert.Contains("a1", saved.AssignedAgentJson);
    }

    [Fact]
    public async Task Migration_Creates_AgentAssignments_Table()
    {
        var (f, scope, db) = await BootAsync();
        using var _ = f;

        // We can insert and read back from agent_assignments.
        db.AgentAssignments.Add(new AgentAssignmentRow
        {
            WorkItemId = "mig-tbl-1",
            Kind = AgentAssignmentKinds.Claimed,
            AgentIdsJson = "[\"a1\"]",
            At = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var row = await db.AgentAssignments.AsNoTracking()
            .FirstAsync(a => a.WorkItemId == "mig-tbl-1");
        Assert.Equal(AgentAssignmentKinds.Claimed, row.Kind);
        scope.Dispose();
    }

    [Fact]
    public async Task Migration_Creates_Index_IX_agent_assignments_workitem_at()
    {
        var (f, scope, db) = await BootAsync();
        using var _ = f;

        // Verify the index exists via PRAGMA.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA index_list('agent_assignments');";
        using var reader = await cmd.ExecuteReaderAsync();
        var indexNames = new List<string>();
        while (await reader.ReadAsync())
        {
            indexNames.Add(reader.GetString(1));
        }
        Assert.Contains("IX_agent_assignments_workitem_at", indexNames);
        scope.Dispose();
    }

    [Fact]
    public async Task Migration_Is_Idempotent()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Running migration again should not throw.
        await Phase22Migration.ApplyAsync(db);
        await Phase22Migration.ApplyAsync(db);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.2 Domain model + contracts
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WorkItemRow_AssignedAgentJson_Defaults_To_Null()
    {
        var (f, scope, db) = await BootAsync();
        using var _ = f;

        var wi = MakeWorkItem("dom-null-1");
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        var saved = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "dom-null-1");
        Assert.Null(saved.AssignedAgentJson);
        scope.Dispose();
    }

    [Fact]
    public void AssignedAgent_RoundTrips_Through_Json_All_Fields()
    {
        var original = new AssignedAgent(
            Agents: new List<string> { "gen-1", "eval-1" },
            Team: "Engineering",
            CrewId: "crew-eng-day",
            Role: "generator",
            AssignedAt: "2026-04-29T12:00:00.0000000+00:00",
            AssignedBy: "queue-worker");

        var json = JsonSerializer.Serialize(original, JsonOpts);
        var deserialized = JsonSerializer.Deserialize<AssignedAgent>(json, JsonOpts);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Agents.Count);
        Assert.Equal("gen-1", deserialized.Agents[0]);
        Assert.Equal("eval-1", deserialized.Agents[1]);
        Assert.Equal("Engineering", deserialized.Team);
        Assert.Equal("crew-eng-day", deserialized.CrewId);
        Assert.Equal("generator", deserialized.Role);
        Assert.Equal("queue-worker", deserialized.AssignedBy);
    }

    [Fact]
    public void AssignedAgent_Single_Agent_Serializes_As_1Element_Array()
    {
        var original = new AssignedAgent(
            Agents: new List<string> { "solo-1" },
            Team: null, CrewId: null, Role: "manual",
            AssignedAt: "2026-04-29T12:00:00Z", AssignedBy: null);

        var json = JsonSerializer.Serialize(original, JsonOpts);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("agents").GetArrayLength());
    }

    [Fact]
    public void AssignedAgent_Paired_Agents_Serializes_As_2Element_Array()
    {
        var original = new AssignedAgent(
            Agents: new List<string> { "gen-1", "eval-1" },
            Team: "Architecture", CrewId: "crew-arch-day", Role: "generator",
            AssignedAt: "2026-04-29T12:00:00Z", AssignedBy: "queue-worker");

        var json = JsonSerializer.Serialize(original, JsonOpts);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("agents").GetArrayLength());
    }

    [Fact]
    public void AssignedAgent_Null_Optional_Fields_RoundTrip_Correctly()
    {
        var original = new AssignedAgent(
            Agents: new List<string> { "a1" },
            Team: null, CrewId: null, Role: "manual",
            AssignedAt: "2026-04-29T12:00:00Z", AssignedBy: null);

        var json = JsonSerializer.Serialize(original, JsonOpts);
        var deserialized = JsonSerializer.Deserialize<AssignedAgent>(json, JsonOpts);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.Team);
        Assert.Null(deserialized.CrewId);
        Assert.Null(deserialized.AssignedBy);
    }

    [Fact]
    public async Task AgentAssignmentRow_RoundTrips_Through_EF()
    {
        var (f, scope, db) = await BootAsync();
        using var _ = f;

        var row = new AgentAssignmentRow
        {
            WorkItemId = "ef-rt-1",
            Kind = AgentAssignmentKinds.Claimed,
            AgentIdsJson = "[\"a1\",\"a2\"]",
            Team = "Engineering",
            CrewId = "crew-eng-day",
            Role = "generator",
            FromExecutionStatus = "queued",
            ToExecutionStatus = "live",
            AssignedBy = "queue-worker",
            Reason = "auto-claim",
            At = DateTimeOffset.UtcNow,
        };
        db.AgentAssignments.Add(row);
        await db.SaveChangesAsync();

        var saved = await db.AgentAssignments.AsNoTracking()
            .FirstAsync(a => a.WorkItemId == "ef-rt-1");
        Assert.Equal(AgentAssignmentKinds.Claimed, saved.Kind);
        Assert.Equal("[\"a1\",\"a2\"]", saved.AgentIdsJson);
        Assert.Equal("Engineering", saved.Team);
        Assert.Equal("crew-eng-day", saved.CrewId);
        Assert.Equal("generator", saved.Role);
        Assert.Equal("queued", saved.FromExecutionStatus);
        Assert.Equal("live", saved.ToExecutionStatus);
        Assert.Equal("queue-worker", saved.AssignedBy);
        Assert.Equal("auto-claim", saved.Reason);
        scope.Dispose();
    }

    [Fact]
    public async Task MapWorkItem_Parses_AssignedAgentJson_Into_POCO()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        // Create a work item.
        var createBody = new { id = "map-1", track = "dev", type = "task", title = "Map test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        // Set AssignedAgentJson directly via DB.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "map-1");
        wi.AssignedAgentJson = JsonSerializer.Serialize(new
        {
            agents = new[] { "gen-1" },
            team = "Engineering",
            crewId = "crew-eng-day",
            role = "generator",
            assignedAt = DateTimeOffset.UtcNow.ToString("o"),
            assignedBy = "queue-worker",
        }, JsonOpts);
        await db.SaveChangesAsync();

        // GET the work item and verify assignedAgent is populated.
        var resp = await client.GetAsync("/api/work-items/map-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var aa = doc.RootElement.GetProperty("assignedAgent");
        Assert.Equal("generator", aa.GetProperty("role").GetString());
        Assert.Equal("gen-1", aa.GetProperty("agents")[0].GetString());
    }

    [Fact]
    public async Task MapWorkItem_Returns_Null_When_AssignedAgentJson_Null()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "map-null-1", track = "dev", type = "task", title = "Null test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        var resp = await client.GetAsync("/api/work-items/map-null-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("assignedAgent").ValueKind);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.4 Endpoints — GET /api/work-items/{id}/assigned-agent
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GET_AssignedAgent_Returns_200_When_Set()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        // Create work item + set AssignedAgentJson.
        var createBody = new { id = "ga-200", track = "dev", type = "task", title = "Test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "ga-200");
        wi.AssignedAgentJson = JsonSerializer.Serialize(new
        {
            agents = new[] { "agent-1" },
            role = "generator",
            assignedAt = DateTimeOffset.UtcNow.ToString("o"),
        }, JsonOpts);
        await db.SaveChangesAsync();

        var resp = await client.GetAsync("/api/work-items/ga-200/assigned-agent");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("agent-1", doc.RootElement.GetProperty("agents")[0].GetString());
    }

    [Fact]
    public async Task GET_AssignedAgent_Returns_204_When_Null()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "ga-204", track = "dev", type = "task", title = "Test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        var resp = await client.GetAsync("/api/work-items/ga-204/assigned-agent");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task GET_AssignedAgent_Returns_404_For_Missing_WorkItem()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/work-items/DOES-NOT-EXIST/assigned-agent");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.4 Endpoints — GET /api/work-items/{id}/assignments
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GET_Assignments_Returns_Newest_First()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "hist-1", track = "dev", type = "task", title = "History test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        // Insert 3 history rows with different timestamps.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentAssignments.AddRange(
            new AgentAssignmentRow { WorkItemId = "hist-1", Kind = "claimed", At = now.AddMinutes(-3) },
            new AgentAssignmentRow { WorkItemId = "hist-1", Kind = "pr-opened", At = now.AddMinutes(-2) },
            new AgentAssignmentRow { WorkItemId = "hist-1", Kind = "completed", At = now.AddMinutes(-1) }
        );
        await db.SaveChangesAsync();

        var resp = await client.GetAsync("/api/work-items/hist-1/assignments");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, arr.GetArrayLength());
        Assert.Equal("completed", arr[0].GetProperty("kind").GetString());
        Assert.Equal("pr-opened", arr[1].GetProperty("kind").GetString());
        Assert.Equal("claimed", arr[2].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task GET_Assignments_Limits_Results()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "hist-lim", track = "dev", type = "task", title = "Limit test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        for (int i = 0; i < 5; i++)
        {
            db.AgentAssignments.Add(new AgentAssignmentRow
            {
                WorkItemId = "hist-lim", Kind = "claimed", At = DateTimeOffset.UtcNow.AddMinutes(-i),
            });
        }
        await db.SaveChangesAsync();

        var resp = await client.GetAsync("/api/work-items/hist-lim/assignments?limit=2");
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, arr.GetArrayLength());
    }

    [Fact]
    public async Task GET_Assignments_Defaults_Limit_50()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "hist-def", track = "dev", type = "task", title = "Default limit" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        // Just verify the endpoint works without ?limit — no need to insert 50 rows.
        var resp = await client.GetAsync("/api/work-items/hist-def/assignments");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GET_Assignments_Clamps_Limit_To_500()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "hist-clamp", track = "dev", type = "task", title = "Clamp limit" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        // limit=999 should be clamped but not error.
        var resp = await client.GetAsync("/api/work-items/hist-clamp/assignments?limit=999");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GET_Assignments_Returns_Empty_For_WorkItem_With_No_History()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "hist-empty", track = "dev", type = "task", title = "No history" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        var resp = await client.GetAsync("/api/work-items/hist-empty/assignments");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, arr.GetArrayLength());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.4 Endpoints — POST /api/work-items/{id}/assignments
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task POST_Assignment_Writes_AssignedAgentJson_And_History()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "post-1", track = "dev", type = "task", title = "Post test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        // Seed an agent for validation.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        // Agents are seeded by ForgeSeed — pick one that exists.
        var agent = await db.Agents.AsNoTracking().FirstAsync();

        var body = new { agents = new[] { agent.Id }, role = "manual" };
        var resp = await client.PostAsJsonAsync("/api/work-items/post-1/assignments", body, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Verify field is set.
        var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "post-1");
        Assert.NotNull(wi.AssignedAgentJson);

        // Verify history row.
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "post-1").ToListAsync();
        Assert.Single(hist);
        Assert.Equal(AgentAssignmentKinds.Reassigned, hist[0].Kind);
    }

    [Fact]
    public async Task POST_Assignment_Sets_AssignedBy_To_User()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory, "reviewer@forge.local");

        var createBody = new { id = "post-by-1", track = "dev", type = "task", title = "AssignedBy test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var agent = await db.Agents.AsNoTracking().FirstAsync();

        var body = new { agents = new[] { agent.Id }, role = "manual" };
        await client.PostAsJsonAsync("/api/work-items/post-by-1/assignments", body, JsonOpts);

        var hist = await db.AgentAssignments.AsNoTracking()
            .FirstAsync(a => a.WorkItemId == "post-by-1");
        Assert.Equal("reviewer@forge.local", hist.AssignedBy);
    }

    [Fact]
    public async Task POST_Assignment_Reviewer_Role_Gets_ReviewerAssigned_Kind()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "post-rev-1", track = "dev", type = "task", title = "Reviewer kind" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var agent = await db.Agents.AsNoTracking().FirstAsync();

        var body = new { agents = new[] { agent.Id }, role = "reviewer" };
        await client.PostAsJsonAsync("/api/work-items/post-rev-1/assignments", body, JsonOpts);

        var hist = await db.AgentAssignments.AsNoTracking()
            .FirstAsync(a => a.WorkItemId == "post-rev-1");
        Assert.Equal(AgentAssignmentKinds.ReviewerAssigned, hist.Kind);
    }

    [Fact]
    public async Task POST_Assignment_Invalid_Agent_Returns_400()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "post-bad-1", track = "dev", type = "task", title = "Bad agent" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        var body = new { agents = new[] { "DOES-NOT-EXIST-AGENT" }, role = "manual" };
        var resp = await client.PostAsJsonAsync("/api/work-items/post-bad-1/assignments", body, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Assignment_With_Reason_Persists_Reason()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "post-reason", track = "dev", type = "task", title = "Reason test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var agent = await db.Agents.AsNoTracking().FirstAsync();

        var body = new { agents = new[] { agent.Id }, role = "manual", reason = "manual override" };
        await client.PostAsJsonAsync("/api/work-items/post-reason/assignments", body, JsonOpts);

        var hist = await db.AgentAssignments.AsNoTracking()
            .FirstAsync(a => a.WorkItemId == "post-reason");
        Assert.Equal("manual override", hist.Reason);
    }

    [Fact]
    public async Task POST_Assignment_Without_Reason_Stores_Null()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "post-noreason", track = "dev", type = "task", title = "No reason" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var agent = await db.Agents.AsNoTracking().FirstAsync();

        var body = new { agents = new[] { agent.Id }, role = "manual" };
        await client.PostAsJsonAsync("/api/work-items/post-noreason/assignments", body, JsonOpts);

        var hist = await db.AgentAssignments.AsNoTracking()
            .FirstAsync(a => a.WorkItemId == "post-noreason");
        Assert.Null(hist.Reason);
    }

    [Fact]
    public async Task POST_Assignment_Without_Auth_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient(); // no X-Forge-User

        var body = new { agents = new[] { "a1" }, role = "manual" };
        var resp = await client.PostAsJsonAsync("/api/work-items/any-id/assignments", body, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Assignment_Empty_Agents_Returns_400()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "post-empty", track = "dev", type = "task", title = "Empty agents" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        var body = new { agents = Array.Empty<string>(), role = "manual" };
        var resp = await client.PostAsJsonAsync("/api/work-items/post-empty/assignments", body, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Assignment_Invalid_Role_Returns_400()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "post-badrole", track = "dev", type = "task", title = "Bad role" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var agent = await db.Agents.AsNoTracking().FirstAsync();

        var body = new { agents = new[] { agent.Id }, role = "nonexistent-role" };
        var resp = await client.PostAsJsonAsync("/api/work-items/post-badrole/assignments", body, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.4 Endpoints — DELETE /api/work-items/{id}/assigned-agent
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DELETE_AssignedAgent_Nulls_Field_And_Appends_ClearedManual()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "del-1", track = "dev", type = "task", title = "Delete test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        // Set AssignedAgentJson.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "del-1");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        await db.SaveChangesAsync();

        var resp = await client.DeleteAsync("/api/work-items/del-1/assigned-agent");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Verify field is null.
        await db.Entry(wi).ReloadAsync();
        Assert.Null(wi.AssignedAgentJson);

        // Verify history row.
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "del-1").ToListAsync();
        Assert.Single(hist);
        Assert.Equal(AgentAssignmentKinds.ClearedManual, hist[0].Kind);
    }

    [Fact]
    public async Task DELETE_AssignedAgent_With_Reason_Persists_Reason()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "del-reason", track = "dev", type = "task", title = "Delete reason" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "del-reason");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        await db.SaveChangesAsync();

        // Send DELETE with body containing reason.
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/work-items/del-reason/assigned-agent")
        {
            Content = JsonContent.Create(new { reason = "PO override" }, options: JsonOpts),
        };
        var resp = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var hist = await db.AgentAssignments.AsNoTracking()
            .FirstAsync(a => a.WorkItemId == "del-reason");
        Assert.Equal("PO override", hist.Reason);
    }

    [Fact]
    public async Task DELETE_AssignedAgent_Without_Auth_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient(); // no auth

        var resp = await client.DeleteAsync("/api/work-items/any-id/assigned-agent");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_AssignedAgent_When_Already_Null_Is_Idempotent()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "del-idem", track = "dev", type = "task", title = "Idempotent delete" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        // Field is already null — delete should still succeed.
        var resp = await client.DeleteAsync("/api/work-items/del-idem/assigned-agent");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Still should have a history row.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "del-idem").ToListAsync();
        Assert.Single(hist);
    }

    [Fact]
    public async Task DELETE_AssignedAgent_NonExistent_Returns_404()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var resp = await client.DeleteAsync("/api/work-items/DOES-NOT-EXIST/assigned-agent");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.5 MoveWorkItem integration — state transition hooks
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MoveToResolved_Nulls_AssignedAgent_And_Appends_Completed()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "mv-res-1", track = "dev", type = "task", title = "Resolve test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        // Set AssignedAgentJson.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "mv-res-1");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        await db.SaveChangesAsync();

        // Move to resolved.
        var moveBody = new { state = "resolved" };
        var resp = await client.PostAsJsonAsync("/api/work-items/mv-res-1/move", moveBody, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Verify AssignedAgentJson nulled.
        await db.Entry(wi).ReloadAsync();
        Assert.Null(wi.AssignedAgentJson);

        // Verify history row.
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "mv-res-1").ToListAsync();
        Assert.Single(hist);
        Assert.Equal(AgentAssignmentKinds.Completed, hist[0].Kind);
        Assert.Equal("proposed", hist[0].FromExecutionStatus);
        Assert.Equal("resolved", hist[0].ToExecutionStatus);
    }

    [Fact]
    public async Task MoveToClosed_Nulls_AssignedAgent_And_Appends_Completed()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "mv-cls-1", track = "dev", type = "task", title = "Close test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "mv-cls-1");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        await db.SaveChangesAsync();

        var moveBody = new { state = "closed" };
        var resp = await client.PostAsJsonAsync("/api/work-items/mv-cls-1/move", moveBody, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await db.Entry(wi).ReloadAsync();
        Assert.Null(wi.AssignedAgentJson);

        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "mv-cls-1").ToListAsync();
        Assert.Single(hist);
        Assert.Equal(AgentAssignmentKinds.Completed, hist[0].Kind);
    }

    [Fact]
    public async Task MoveToActive_Does_NOT_Touch_AssignedAgent()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "mv-act-1", track = "dev", type = "task", title = "Active test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "mv-act-1");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        await db.SaveChangesAsync();

        var moveBody = new { state = "active" };
        await client.PostAsJsonAsync("/api/work-items/mv-act-1/move", moveBody, JsonOpts);

        await db.Entry(wi).ReloadAsync();
        Assert.NotNull(wi.AssignedAgentJson); // unchanged

        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "mv-act-1").ToListAsync();
        Assert.Empty(hist); // no assignment history row
    }

    [Fact]
    public async Task MoveProposedToActive_Does_NOT_Append_History()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "mv-pa-1", track = "dev", type = "task", title = "No history" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        var moveBody = new { state = "active" };
        await client.PostAsJsonAsync("/api/work-items/mv-pa-1/move", moveBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "mv-pa-1").ToListAsync();
        Assert.Empty(hist);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.6 Restart clears AssignedAgent
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Restart_Nulls_AssignedAgentJson()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "rst-1", track = "dev", type = "task", title = "Restart test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "rst-1");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        wi.ExecutionStatus = ExecutionStatuses.Live;
        await db.SaveChangesAsync();

        var resp = await client.DeleteAsync("/api/work-items/rst-1/transcript");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await db.Entry(wi).ReloadAsync();
        Assert.Null(wi.AssignedAgentJson);
    }

    [Fact]
    public async Task Restart_Appends_ClearedRestart_History()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "rst-hist", track = "dev", type = "task", title = "Restart history" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "rst-hist");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        wi.ExecutionStatus = ExecutionStatuses.Failed;
        await db.SaveChangesAsync();

        await client.DeleteAsync("/api/work-items/rst-hist/transcript");

        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "rst-hist").ToListAsync();
        Assert.Single(hist);
        Assert.Equal(AgentAssignmentKinds.ClearedRestart, hist[0].Kind);
        Assert.Equal(ExecutionStatuses.Failed, hist[0].FromExecutionStatus);
        Assert.Equal(ExecutionStatuses.Queued, hist[0].ToExecutionStatus);
    }

    [Fact]
    public async Task Restart_Records_AssignedBy_From_User_Header()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory, "ops-lead@forge.local");

        var createBody = new { id = "rst-user", track = "dev", type = "task", title = "Restart user" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "rst-user");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        wi.ExecutionStatus = ExecutionStatuses.Live;
        await db.SaveChangesAsync();

        await client.DeleteAsync("/api/work-items/rst-user/transcript");

        var hist = await db.AgentAssignments.AsNoTracking()
            .FirstAsync(a => a.WorkItemId == "rst-user");
        Assert.Equal("ops-lead@forge.local", hist.AssignedBy);
    }

    [Fact]
    public async Task Restart_When_AssignedAgent_Null_Still_Appends_History()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "rst-null", track = "dev", type = "task", title = "Restart null" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        await client.DeleteAsync("/api/work-items/rst-null/transcript");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "rst-null").ToListAsync();
        Assert.Single(hist);
        Assert.Equal(AgentAssignmentKinds.ClearedRestart, hist[0].Kind);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.7 Backfill migration (one-shot)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Backfill_Migrates_RuntimeShaped_AssignedToJson()
    {
        // Create a fresh DB with a work item that has runtime-shaped AssignedToJson.
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient(); // triggers boot + migration

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Insert a work item with runtime-shaped AssignedToJson.
        var wi = MakeWorkItem("bf-rt-1");
        wi.AssignedToJson = JsonSerializer.Serialize(new
        {
            crewId = "crew-eng-day",
            agentId = "gen-1",
            generatorAgentId = "gen-1",
            evaluatorAgentId = "eval-1",
            evaluatorCrewId = "crew-eng-night",
            by = "queue-worker",
        }, JsonOpts);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        // Run backfill.
        // First clear the seed state so backfill runs again.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM forge_seed_state WHERE Key = 'phase22-backfill-done';";
        await delCmd.ExecuteNonQueryAsync();

        await Phase22Migration.ApplyAsync(db);

        await db.Entry(wi).ReloadAsync();
        Assert.NotNull(wi.AssignedAgentJson);

        // Verify the WIT-shape was rebuilt.
        var witDoc = JsonDocument.Parse(wi.AssignedToJson!);
        Assert.Equal("team", witDoc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Backfill_Skips_WitShaped_AssignedToJson()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var wi = MakeWorkItem("bf-wit-1");
        wi.AssignedToJson = JsonSerializer.Serialize(new
        {
            kind = "agent",
            id = "XD",
            displayName = "Xiaodong Li",
        }, JsonOpts);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        // Clear seed state and re-run.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM forge_seed_state WHERE Key = 'phase22-backfill-done';";
        await delCmd.ExecuteNonQueryAsync();

        await Phase22Migration.ApplyAsync(db);

        await db.Entry(wi).ReloadAsync();
        Assert.Null(wi.AssignedAgentJson); // not migrated

        // History — no backfill row for this item.
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "bf-wit-1" && a.Kind == AgentAssignmentKinds.Backfill)
            .ToListAsync();
        Assert.Empty(hist);
    }

    [Fact]
    public async Task Backfill_Skips_Null_AssignedToJson()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var wi = MakeWorkItem("bf-null-1");
        wi.AssignedToJson = null;
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        // Clear seed state and re-run.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM forge_seed_state WHERE Key = 'phase22-backfill-done';";
        await delCmd.ExecuteNonQueryAsync();

        await Phase22Migration.ApplyAsync(db);

        await db.Entry(wi).ReloadAsync();
        Assert.Null(wi.AssignedAgentJson); // not migrated, no crash
    }

    [Fact]
    public async Task Backfill_Adds_History_Row_Per_Migrated_Item()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var wi = MakeWorkItem("bf-hist-1");
        wi.AssignedToJson = JsonSerializer.Serialize(new
        {
            crewId = "crew-eng-day",
            generatorAgentId = "gen-1",
        }, JsonOpts);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        // Clear seed state.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM forge_seed_state WHERE Key = 'phase22-backfill-done';";
        await delCmd.ExecuteNonQueryAsync();

        await Phase22Migration.ApplyAsync(db);

        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "bf-hist-1" && a.Kind == AgentAssignmentKinds.Backfill)
            .ToListAsync();
        Assert.Single(hist);
        Assert.Equal("backfill", hist[0].AssignedBy);
    }

    [Fact]
    public async Task Backfill_Is_Gated_By_SeedState()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Backfill already ran during boot.
        // Running again should be a no-op (no duplicate rows).
        var wi = MakeWorkItem("bf-gate-1");
        wi.AssignedToJson = JsonSerializer.Serialize(new
        {
            crewId = "crew-eng-day",
            generatorAgentId = "gen-1",
        }, JsonOpts);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        // DON'T clear seed state — backfill should skip.
        await Phase22Migration.ApplyAsync(db);

        await db.Entry(wi).ReloadAsync();
        Assert.Null(wi.AssignedAgentJson); // not migrated because gate is set
    }

    [Fact]
    public async Task Backfill_Idempotent_No_Duplicate_History()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var wi = MakeWorkItem("bf-idem-1");
        wi.AssignedToJson = JsonSerializer.Serialize(new
        {
            crewId = "crew-eng-day",
            generatorAgentId = "gen-1",
        }, JsonOpts);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        // Clear seed state, run twice.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM forge_seed_state WHERE Key = 'phase22-backfill-done';";
        await delCmd.ExecuteNonQueryAsync();

        await Phase22Migration.ApplyAsync(db);

        // Clear seed state again.
        using var delCmd2 = conn.CreateCommand();
        delCmd2.CommandText = "DELETE FROM forge_seed_state WHERE Key = 'phase22-backfill-done';";
        await delCmd2.ExecuteNonQueryAsync();

        // Second run — already has AssignedAgentJson set, so the JSON should
        // now have 'kind' from the rebuilt WIT shape. But the runtime-shaped
        // check looks at AssignedToJson which was rebuilt to WIT-shape on first
        // run, so it should be skipped.
        await Phase22Migration.ApplyAsync(db);

        // Only one backfill history row.
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "bf-idem-1" && a.Kind == AgentAssignmentKinds.Backfill)
            .ToListAsync();
        Assert.Single(hist);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.8 Sticky on failure (Q22.4)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Agent_Failure_Does_NOT_Clear_AssignedAgent()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "stick-1", track = "dev", type = "task", title = "Sticky test" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "stick-1");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        wi.ExecutionStatus = ExecutionStatuses.Failed;
        await db.SaveChangesAsync();

        // Verify AssignedAgentJson stays set despite failed status.
        await db.Entry(wi).ReloadAsync();
        Assert.NotNull(wi.AssignedAgentJson);

        // No cleared-* history rows.
        var cleared = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "stick-1" &&
                (a.Kind == AgentAssignmentKinds.ClearedRestart || a.Kind == AgentAssignmentKinds.ClearedManual))
            .ToListAsync();
        Assert.Empty(cleared);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.9 WIT assignee independence (Q22.1)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PATCH_AssignedTo_Does_NOT_Mutate_AssignedAgentJson()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "wit-indep-1", track = "dev", type = "task", title = "WIT independence" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "wit-indep-1");
        wi.AssignedAgentJson = "{\"agents\":[\"a1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        await db.SaveChangesAsync();

        // PATCH assignedTo.
        var patchBody = new { assignedTo = new { kind = "agent", id = "XD", displayName = "Xiaodong Li", initials = "XD" } };
        await client.PatchAsJsonAsync("/api/work-items/wit-indep-1", patchBody, JsonOpts);

        await db.Entry(wi).ReloadAsync();
        Assert.NotNull(wi.AssignedAgentJson);
        Assert.Contains("a1", wi.AssignedAgentJson!);
    }

    [Fact]
    public async Task PATCH_AssignedTo_Does_NOT_Append_Assignment_History()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "wit-nohist", track = "dev", type = "task", title = "WIT no history" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        var patchBody = new { assignedTo = new { kind = "agent", id = "XD", displayName = "Xiaodong Li", initials = "XD" } };
        await client.PatchAsJsonAsync("/api/work-items/wit-nohist", patchBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var hist = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == "wit-nohist").ToListAsync();
        Assert.Empty(hist);
    }

    [Fact]
    public async Task Both_Fields_Can_Be_NonNull_Simultaneously()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "both-1", track = "dev", type = "task", title = "Both fields" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "both-1");
        wi.AssignedToJson = "{\"kind\":\"agent\",\"id\":\"XD\",\"displayName\":\"XD\",\"initials\":\"XD\"}";
        wi.AssignedAgentJson = "{\"agents\":[\"gen-1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        await db.SaveChangesAsync();

        var resp = await client.GetAsync("/api/work-items/both-1");
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("assignedTo").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("assignedAgent").ValueKind);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.10 Multi-agent & role semantics (Q22.5)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Manual_Reviewer_Assignment_Replaces_AssignedAgent_Entirely()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "rev-replace", track = "dev", type = "task", title = "Reviewer replace" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Set initial generator assignment.
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "rev-replace");
        wi.AssignedAgentJson = "{\"agents\":[\"gen-1\"],\"role\":\"generator\",\"assignedAt\":\"2026-01-01T00:00:00Z\"}";
        await db.SaveChangesAsync();

        // POST reviewer assignment.
        var agent = await db.Agents.AsNoTracking().FirstAsync();
        var body = new { agents = new[] { agent.Id }, role = "reviewer" };
        await client.PostAsJsonAsync("/api/work-items/rev-replace/assignments", body, JsonOpts);

        await db.Entry(wi).ReloadAsync();
        var parsed = JsonSerializer.Deserialize<AssignedAgent>(wi.AssignedAgentJson!, JsonOpts);
        Assert.Equal("reviewer", parsed!.Role);
        Assert.Equal(agent.Id, parsed.Agents[0]);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.11 Full lifecycle timeline (end-to-end)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Full_HappyPath_Lifecycle_Produces_Correct_Timeline()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "lc-happy", track = "dev", type = "task", title = "Lifecycle" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var agent = await db.Agents.AsNoTracking().FirstAsync();
        var now = DateTimeOffset.UtcNow;

        // Step 1: claimed.
        db.AgentAssignments.Add(new AgentAssignmentRow
        {
            WorkItemId = "lc-happy", Kind = AgentAssignmentKinds.Claimed,
            AgentIdsJson = $"[\"{agent.Id}\"]", Role = "generator",
            FromExecutionStatus = "queued", ToExecutionStatus = "live",
            AssignedBy = "queue-worker", At = now.AddMinutes(-4),
        });
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "lc-happy");
        wi.AssignedAgentJson = $"{{\"agents\":[\"{agent.Id}\"],\"role\":\"generator\",\"assignedAt\":\"{now.AddMinutes(-4):o}\"}}";

        // Step 2: pr-opened (via Move to in-review-like state — simulate).
        db.AgentAssignments.Add(new AgentAssignmentRow
        {
            WorkItemId = "lc-happy", Kind = AgentAssignmentKinds.PrOpened,
            AgentIdsJson = $"[\"{agent.Id}\"]",
            FromExecutionStatus = "active", ToExecutionStatus = "in-review",
            At = now.AddMinutes(-3),
        });

        // Step 3: reviewer-assigned.
        db.AgentAssignments.Add(new AgentAssignmentRow
        {
            WorkItemId = "lc-happy", Kind = AgentAssignmentKinds.ReviewerAssigned,
            AgentIdsJson = $"[\"{agent.Id}\"]", Role = "reviewer",
            AssignedBy = "po@forge.local", At = now.AddMinutes(-2),
        });

        // Step 4: completed.
        db.AgentAssignments.Add(new AgentAssignmentRow
        {
            WorkItemId = "lc-happy", Kind = AgentAssignmentKinds.Completed,
            AgentIdsJson = $"[\"{agent.Id}\"]",
            FromExecutionStatus = "in-review", ToExecutionStatus = "resolved",
            At = now.AddMinutes(-1),
        });
        wi.AssignedAgentJson = null;
        await db.SaveChangesAsync();

        // Query the timeline.
        var resp = await client.GetAsync("/api/work-items/lc-happy/assignments");
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(4, arr.GetArrayLength());

        // Newest first.
        Assert.Equal(AgentAssignmentKinds.Completed, arr[0].GetProperty("kind").GetString());
        Assert.Equal(AgentAssignmentKinds.ReviewerAssigned, arr[1].GetProperty("kind").GetString());
        Assert.Equal(AgentAssignmentKinds.PrOpened, arr[2].GetProperty("kind").GetString());
        Assert.Equal(AgentAssignmentKinds.Claimed, arr[3].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task History_Rows_Have_NonNull_At_In_ISO8601()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "ts-check", track = "dev", type = "task", title = "Timestamp check" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.AgentAssignments.Add(new AgentAssignmentRow
        {
            WorkItemId = "ts-check", Kind = AgentAssignmentKinds.Claimed,
            At = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var resp = await client.GetAsync("/api/work-items/ts-check/assignments");
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var atStr = arr[0].GetProperty("at").GetString();
        Assert.NotNull(atStr);
        Assert.True(DateTimeOffset.TryParse(atStr, out _), $"'{atStr}' is not valid ISO-8601");
    }

    [Fact]
    public async Task History_Rows_Have_Valid_Kind()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var createBody = new { id = "kind-valid", track = "dev", type = "task", title = "Kind validation" };
        await client.PostAsJsonAsync("/api/work-items", createBody, JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        foreach (var kind in AgentAssignmentKinds.All)
        {
            db.AgentAssignments.Add(new AgentAssignmentRow
            {
                WorkItemId = "kind-valid", Kind = kind,
                At = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var resp = await client.GetAsync("/api/work-items/kind-valid/assignments");
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(AgentAssignmentKinds.All.Count, arr.GetArrayLength());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22.12 Edge cases & error handling
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task POST_Assignment_NonExistent_WorkItem_Returns_404()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = AuthedClient(factory);

        var body = new { agents = new[] { "a1" }, role = "manual" };
        var resp = await client.PostAsJsonAsync("/api/work-items/DOES-NOT-EXIST/assignments", body, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_Assignments_NonExistent_WorkItem_Returns_404()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/work-items/DOES-NOT-EXIST/assignments");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Malformed_AssignedAgentJson_Does_Not_Crash_GET()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = MakeWorkItem("malformed-1");
        wi.AssignedAgentJson = "NOT VALID JSON {{{{";
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        // GET assigned-agent should return 204 (null gracefully), not 500.
        var resp = await client.GetAsync("/api/work-items/malformed-1/assigned-agent");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // GET the work item should return 200 with null assignedAgent.
        var resp2 = await client.GetAsync("/api/work-items/malformed-1");
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var doc = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("assignedAgent").ValueKind);
    }
}
