// -----------------------------------------------------------------------
// <copyright file="Phase14DisputeTests.cs" company="Microsoft Corp.">
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
/// Phase 14.5 — dispute endpoint tests. Exercise the row mutation, the
/// retroactive-pass flip on <c>judgeScore ≥ 90</c>, the one-dispute-per-row
/// guard, and basic input validation.
/// </summary>
public class Phase14DisputeTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Seed a deliverable + supporting agent rows. Returns the deliverable id.
    /// </summary>
    private static async Task<long> SeedDeliverableAsync(
        ForgeWebApplicationFactory f,
        string workItemId = "wi-d1",
        string workItemState = "active",
        int score = 80,
        string verdict = "iterate")
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        // Agents: generator, evaluator, judge.
        if (!await db.Agents.AnyAsync(a => a.Id == "gen-1"))
        {
            db.Agents.Add(new AgentRow { Id = "gen-1", Name = "Gen", Role = "eng" });
            db.Agents.Add(new AgentRow { Id = "eval-1", Name = "Eval", Role = "rev" });
            db.Agents.Add(new AgentRow { Id = "judge-1", Name = "Judge", Role = "arch" });
        }
        // Work item.
        if (!await db.WorkItems.AnyAsync(w => w.Id == workItemId))
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = workItemId, Type = "step", State = workItemState,
                Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        // Deliverable.
        var row = new DeliverableRow
        {
            WorkItemId = workItemId,
            CrewId = "crew-x",
            GeneratorAgentId = "gen-1",
            EvaluatorAgentId = "eval-1",
            Iter = 1,
            Score = score,
            Verdict = verdict,
            Notes = "",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Deliverables.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public async Task Dispute_With_Score_95_Flips_WorkItem_To_Closed()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var id = await SeedDeliverableAsync(f, workItemId: "wi-flip");

        var resp = await c.PostAsJsonAsync($"/api/deliverables/{id}/dispute", new
        {
            judgeAgentId = "judge-1", judgeScore = 95, judgeNotes = "Fine on review.",
        }, JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("judge-1", body.RootElement.GetProperty("judgeAgentId").GetString());
        Assert.Equal(95, body.RootElement.GetProperty("judgeScore").GetInt32());

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "wi-flip");
        Assert.Equal("closed", wi.State); // retroactive pass
    }

    [Fact]
    public async Task Dispute_With_Score_85_Leaves_WorkItem_Unchanged()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var id = await SeedDeliverableAsync(f, workItemId: "wi-noflip", workItemState: "active");

        var resp = await c.PostAsJsonAsync($"/api/deliverables/{id}/dispute", new
        {
            judgeAgentId = "judge-1", judgeScore = 85, judgeNotes = "Still needs work.",
        }, JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.FirstAsync(w => w.Id == "wi-noflip");
        Assert.Equal("active", wi.State); // unchanged
    }

    [Fact]
    public async Task Second_Dispute_On_Same_Deliverable_Returns_409()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var id = await SeedDeliverableAsync(f, workItemId: "wi-twice");

        var first = await c.PostAsJsonAsync($"/api/deliverables/{id}/dispute", new
        {
            judgeAgentId = "judge-1", judgeScore = 75,
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await c.PostAsJsonAsync($"/api/deliverables/{id}/dispute", new
        {
            judgeAgentId = "judge-1", judgeScore = 95,
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already_disputed", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dispute_With_Score_Out_Of_Range_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var id = await SeedDeliverableAsync(f);

        var resp = await c.PostAsJsonAsync($"/api/deliverables/{id}/dispute", new
        {
            judgeAgentId = "judge-1", judgeScore = 150,
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Dispute_With_Unknown_Judge_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var id = await SeedDeliverableAsync(f);

        var resp = await c.PostAsJsonAsync($"/api/deliverables/{id}/dispute", new
        {
            judgeAgentId = "ghost-agent", judgeScore = 95,
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Dispute_For_Unknown_Deliverable_Returns_404()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var resp = await c.PostAsJsonAsync("/api/deliverables/9999/dispute", new
        {
            judgeAgentId = "judge-1", judgeScore = 95,
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_Deliverable_Returns_Row()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var id = await SeedDeliverableAsync(f, workItemId: "wi-get");

        var resp = await c.GetAsync($"/api/deliverables/{id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(id, body.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("wi-get", body.RootElement.GetProperty("workItemId").GetString());
    }

    [Fact]
    public async Task List_Deliverables_For_WorkItem_Returns_All_Iterations_In_Order()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        // Three iterations on the same work item.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Agents.Add(new AgentRow { Id = "gen-1", Name = "Gen", Role = "eng" });
            db.Agents.Add(new AgentRow { Id = "eval-1", Name = "Eval", Role = "rev" });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-list", Type = "step", State = "active",
                Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            for (var i = 1; i <= 3; i++)
            {
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = "wi-list", CrewId = "crew-x",
                    GeneratorAgentId = "gen-1", EvaluatorAgentId = "eval-1",
                    Iter = i, Score = 70 + i, Verdict = "iterate", Notes = "",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        var resp = await c.GetAsync("/api/work-items/wi-list/deliverables");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, arr.GetArrayLength());
        Assert.Equal(1, arr[0].GetProperty("iter").GetInt32());
        Assert.Equal(2, arr[1].GetProperty("iter").GetInt32());
        Assert.Equal(3, arr[2].GetProperty("iter").GetInt32());
    }
}
