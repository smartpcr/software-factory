// -----------------------------------------------------------------------
// <copyright file="Phase70WorkstreamAcceptTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 70 — operator-override <c>POST /api/workstreams/{id}/accept</c>.
/// Mirrors plan-doc Phase 43 ACCEPT for workstreams: flips the latest
/// deliverable verdict to <c>pass</c> so the orchestrator commits the
/// worktree files + opens the PR on its next tick. Used to bypass Phase
/// 50 / 56 / 66 false-positive demotions when the operator has
/// independently verified the diff is good.
/// </summary>
public class Phase70WorkstreamAcceptTests
{
    private const string WsId = "ws-p70";

    [Fact]
    public async Task Accept_flips_latest_verdict_to_pass_and_returns_priors()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var control = seed.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            SeedWorkstream(control, db, status: "active");
            AddDeliverable(control, db, iter: 6, score: 98, verdict: "iterate");
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/workstreams/{WsId}/accept", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verify = f.Services.CreateScope();
        var control_verifyDb = verify.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var latest = (await verifyDb.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == WsId).ToListAsync())
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .First();
        Assert.Equal("pass", latest.Verdict);
        Assert.Contains("OPERATOR ACCEPT", latest.Notes);
        Assert.Contains("'iterate' to 'pass'", latest.Notes);

        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);
        Assert.Equal("active", ws.Status);
        Assert.Null(ws.StallReason);
    }

    [Fact]
    public async Task Accept_returns_409_when_workstream_status_is_terminal()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var control = seed.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            SeedWorkstream(control, db, status: "complete");
            AddDeliverable(control, db, iter: 1, score: 95, verdict: "pass");
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/workstreams/{WsId}/accept", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("terminal", body);
    }

    [Fact]
    public async Task Accept_returns_400_when_no_deliverables()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var control = seed.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            SeedWorkstream(control, db, status: "active");
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/workstreams/{WsId}/accept", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Nothing to accept", body);
    }

    [Fact]
    public async Task Accept_is_idempotent_when_latest_already_passes()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var control = seed.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            SeedWorkstream(control, db, status: "active");
            AddDeliverable(control, db, iter: 3, score: 95, verdict: "pass");
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/workstreams/{WsId}/accept", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verify = f.Services.CreateScope();
        var control_verifyDb = verify.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var latest = (await verifyDb.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == WsId).ToListAsync())
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .First();
        Assert.Equal("pass", latest.Verdict);
        // Idempotent: the audit note should NOT be appended on a no-op accept.
        Assert.DoesNotContain("OPERATOR ACCEPT", latest.Notes ?? string.Empty);
    }

    [Fact]
    public async Task Accept_cancels_pending_queue_rows()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var control = seed.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            SeedWorkstream(control, db, status: "active");
            AddDeliverable(control, db, iter: 4, score: 92, verdict: "iterate");
            // TaskQueue has UNIQUE(WorkItemId, Kind), so we exercise the
            // single-row case (the typical mid-iter state). The endpoint
            // bulk-cancels via `WHERE State NOT IN ('completed','cancelled')`,
            // so a single row exercises the loop body.
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = WsId,
                State = "picked-up",
                TargetKind = "crew",
                TargetId = "crew-eng",
                Kind = TaskQueueKinds.Work,
                EnqueuedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/workstreams/{WsId}/accept", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verify = f.Services.CreateScope();
        var control_verifyDb = verify.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queueRows = await verifyDb.TaskQueue.AsNoTracking()
            .Where(q => q.WorkItemId == WsId).ToListAsync();
        Assert.Single(queueRows);
        Assert.Equal("cancelled", queueRows[0].State);
        Assert.NotNull(queueRows[0].CompletedAt);
    }

    [Fact]
    public async Task Accept_resets_workitem_execution_status_to_queued()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var control = seed.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            SeedWorkstream(control, db, status: "active");
            // The WorkItem row (backs the workstream's persistent state).
            db.WorkItems.Add(new WorkItemRow
            {
                Id = WsId,
                Type = "step",
                State = "active",
                Title = "Phase 70 WS",
                ExecutionStatus = "live",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            AddDeliverable(control, db, iter: 2, score: 88, verdict: "iterate");
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/workstreams/{WsId}/accept", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verify = f.Services.CreateScope();
        var control_verifyDb = verify.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await verifyDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == WsId);
        Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
    }

    [Fact]
    public async Task Accept_returns_404_when_workstream_does_not_exist()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var resp = await client.PostAsync(
            "/api/workstreams/ws-does-not-exist/accept", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Accept_requires_writer_auth()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        // No X-Forge-User header.

        using (var seed = f.Services.CreateScope())
        {
            var control = seed.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            SeedWorkstream(control, db, status: "active");
            AddDeliverable(control, db, iter: 1, score: 80, verdict: "iterate");
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/workstreams/{WsId}/accept", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void SeedWorkstream(ControlDbContext control, ProductDbContext db, string status)
    {
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = WsId,
            Name = "Phase 70 test",
            Status = status,
            Summary = "Test workstream for ACCEPT endpoint.",
            Branch = "ws/p70",
            StorySpawnId = "spawn-p70",
        });
    }

    private static void AddDeliverable(ControlDbContext control, ProductDbContext db, int iter, int score, string verdict)
    {
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WsId,
            CrewId = "crew-eng",
            GeneratorAgentId = "G",
            EvaluatorAgentId = "E",
            Iter = iter,
            Score = score,
            DimsJson = "{}",
            Verdict = verdict,
            Notes = string.Empty,
            GeneratorSummary = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
