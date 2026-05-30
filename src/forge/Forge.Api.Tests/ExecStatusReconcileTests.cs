// -----------------------------------------------------------------------
// <copyright file="ExecStatusReconcileTests.cs" company="Microsoft Corp.">
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
/// Drag-to-reconcile contract for POST /api/work-items/{id}/execution-status.
/// The endpoint commits the operator's target column only when it matches the
/// authoritative status computed by <see cref="Forge.Api.Services.WorkItemExecutionStatusInspector"/>;
/// mismatches return 409 with the authoritative value + reason. Covers the
/// real bug scenario from 2026-05-30: WIs stuck in <c>queued</c> exec status
/// while their linked workstream is already <c>complete</c> + PR merged.
/// </summary>
public class ExecStatusReconcileTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static HttpClient NewClient(ForgeWebApplicationFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        return c;
    }

    // ─── happy paths — drag where target equals authoritative ──────────

    [Fact]
    public async Task Drag_To_Completed_Succeeds_When_Workstream_Complete_And_Deliverable_Pass()
    {
        // Real-world bug repro: WI shows `queued` in the Exec board but its
        // linked workstream is `complete` + the latest deliverable is a
        // natural pass. Dragging to Completed must succeed and flip the WI.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "ws-drift-1", Type = "stage", Track = "design",
                State = "active", Title = "drift to completed",
                ExecutionStatus = ExecutionStatuses.Queued,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-drift-1", Name = "drift to completed", Status = "complete",
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "ws-drift-1", Iter = 3,
                Verdict = "pass", Score = 95, FailureKind = null,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/ws-drift-1/execution-status",
            new { targetStatus = ExecutionStatuses.Completed }, JsonOpts);

        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Got {(int)resp.StatusCode}: {err}");
        }
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(ExecutionStatuses.Queued, doc.RootElement.GetProperty("previous").GetString());
        Assert.Equal(ExecutionStatuses.Completed, doc.RootElement.GetProperty("current").GetString());

        using var verifyScope = f.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db2.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "ws-drift-1");
        Assert.Equal(ExecutionStatuses.Completed, wi.ExecutionStatus);

        // Audit row emitted with from/to.
        var audit = await db2.Set<AgentAssignmentRow>().AsNoTracking()
            .Where(a => a.WorkItemId == "ws-drift-1"
                     && a.Kind == AgentAssignmentKinds.OperatorReconciled)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
        Assert.Equal(ExecutionStatuses.Queued, audit!.FromExecutionStatus);
        Assert.Equal(ExecutionStatuses.Completed, audit.ToExecutionStatus);
        Assert.Equal("po@forge.local", audit.AssignedBy);
    }

    [Fact]
    public async Task Drag_To_Queued_Succeeds_When_Open_TaskQueue_Row_Exists()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-drift-q", Type = "task", Track = "dev",
                State = "active", Title = "drift to queued",
                ExecutionStatus = ExecutionStatuses.Idle,
            });
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = "wi-drift-q",
                TargetKind = "crew", TargetId = "crew-x",
                State = TaskQueueStates.Queued,
                Kind = TaskQueueKinds.Work,
                EnqueuedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/wi-drift-q/execution-status",
            new { targetStatus = ExecutionStatuses.Queued }, JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(ExecutionStatuses.Idle, doc.RootElement.GetProperty("previous").GetString());
        Assert.Equal(ExecutionStatuses.Queued, doc.RootElement.GetProperty("current").GetString());
    }

    [Fact]
    public async Task Drag_To_Live_Succeeds_When_Open_TaskQueue_Row_Is_PickedUp()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-drift-live", Type = "task", Track = "dev",
                State = "active", Title = "drift to live",
                ExecutionStatus = ExecutionStatuses.Queued,
            });
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = "wi-drift-live",
                TargetKind = "crew", TargetId = "crew-x",
                State = TaskQueueStates.PickedUp,
                Kind = TaskQueueKinds.Work,
                EnqueuedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/wi-drift-live/execution-status",
            new { targetStatus = ExecutionStatuses.Live }, JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Drag_To_Failed_Succeeds_When_Workstream_Stalled()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "ws-stalled", Type = "stage", Track = "design",
                State = "active", Title = "stalled stage",
                ExecutionStatus = ExecutionStatuses.Queued,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-stalled", Name = "stalled", Status = "stalled",
                StallReason = "Gate failed: build error",
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/ws-stalled/execution-status",
            new { targetStatus = ExecutionStatuses.Failed }, JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Drag_To_Same_Column_Is_NoOp_With_200()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-noop", Type = "task", Track = "dev",
                State = "proposed", Title = "noop",
                ExecutionStatus = ExecutionStatuses.Idle,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/wi-noop/execution-status",
            new { targetStatus = ExecutionStatuses.Idle }, JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(ExecutionStatuses.Idle, doc.RootElement.GetProperty("previous").GetString());
        Assert.Equal(ExecutionStatuses.Idle, doc.RootElement.GetProperty("current").GetString());

        // No audit row for same-status writes.
        using var verifyScope = f.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var auditCount = await db2.Set<AgentAssignmentRow>().AsNoTracking()
            .Where(a => a.WorkItemId == "wi-noop"
                     && a.Kind == AgentAssignmentKinds.OperatorReconciled)
            .CountAsync();
        Assert.Equal(0, auditCount);
    }

    [Fact]
    public async Task Drag_To_Completed_Succeeds_When_Workstream_Complete_Even_With_CorruptPass_Deliverable()
    {
        // Real-world scenario: workstream marked `complete` (merge landed)
        // but the latest deliverable has score below PassThreshold (i.e.
        // `corrupt-pass` per ClassifyPassGate). The merge is authoritative
        // for the operator-drag use case — we don't gate on the stale
        // deliverable score.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "ws-merge-corrupt-pass", Type = "stage", Track = "design",
                State = "active", Title = "merge landed, deliverable corrupt",
                ExecutionStatus = ExecutionStatuses.Queued,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-merge-corrupt-pass", Name = "ws", Status = "complete",
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "ws-merge-corrupt-pass", Iter = 7,
                // verdict=pass but score below ScoringConstants.PassThreshold
                // (=90) → CorruptPass classification. No OPERATOR ACCEPT note.
                Verdict = "pass", Score = 42, FailureKind = null,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/ws-merge-corrupt-pass/execution-status",
            new { targetStatus = ExecutionStatuses.Completed }, JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(ExecutionStatuses.Queued, doc.RootElement.GetProperty("previous").GetString());
        Assert.Equal(ExecutionStatuses.Completed, doc.RootElement.GetProperty("current").GetString());
        // Reason text should hint that the merge is authoritative despite the deliverable.
        Assert.Contains("merge landed", doc.RootElement.GetProperty("reason").GetString() ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Drag_To_Failed_Rejected_When_Workstream_Complete_Even_With_CorruptPass_Deliverable()
    {
        // Same setup as above — verify the inverse: operator can't claim
        // Failed when ws.Status==complete, because the merge has landed.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "ws-merge-corrupt-fail", Type = "stage", Track = "design",
                State = "active", Title = "x",
                ExecutionStatus = ExecutionStatuses.Queued,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-merge-corrupt-fail", Name = "ws", Status = "complete",
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "ws-merge-corrupt-fail", Iter = 1,
                Verdict = "pass", Score = 0, FailureKind = "timeout",
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/ws-merge-corrupt-fail/execution-status",
            new { targetStatus = ExecutionStatuses.Failed }, JsonOpts);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(ExecutionStatuses.Completed, doc.RootElement.GetProperty("authoritative").GetString());
    }

    // ─── 409 — drag where target disagrees with authoritative ──────────

    [Fact]
    public async Task Drag_To_Failed_Rejected_When_Workstream_Complete_With_Pass()
    {
        // Inverse of the happy-path test: same setup (ws complete + pass),
        // but operator drags to Failed — must 409 and tell them truth is Completed.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "ws-misread", Type = "stage", Track = "design",
                State = "active", Title = "misread by operator",
                ExecutionStatus = ExecutionStatuses.Queued,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-misread", Name = "misread", Status = "complete",
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "ws-misread", Iter = 1,
                Verdict = "pass", Score = 92, FailureKind = null,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/ws-misread/execution-status",
            new { targetStatus = ExecutionStatuses.Failed }, JsonOpts);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("execution_status_mismatch", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(ExecutionStatuses.Failed, doc.RootElement.GetProperty("requested").GetString());
        Assert.Equal(ExecutionStatuses.Completed, doc.RootElement.GetProperty("authoritative").GetString());
        Assert.Equal(ExecutionStatuses.Queued, doc.RootElement.GetProperty("current").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("reason").GetString()));

        // Stored value MUST NOT have changed on a 409.
        using var verifyScope = f.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db2.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "ws-misread");
        Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
    }

    [Fact]
    public async Task Drag_To_Completed_Rejected_When_No_Workstream_And_No_Queue()
    {
        // Top-level WI with no workstream, no queue, no deliverable — truth
        // is Idle. Dragging to Completed must refuse.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-bare", Type = "task", Track = "dev",
                State = "proposed", Title = "bare",
                ExecutionStatus = ExecutionStatuses.Idle,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/wi-bare/execution-status",
            new { targetStatus = ExecutionStatuses.Completed }, JsonOpts);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(ExecutionStatuses.Idle, doc.RootElement.GetProperty("authoritative").GetString());
    }

    [Fact]
    public async Task Drag_From_CooldownBudget_Rejected_For_Any_Other_Target()
    {
        // CooldownBudget is owned by BudgetCooldownReconciler — manual
        // drag-move must NOT race it. Inspector returns CooldownBudget;
        // dragging to Queued or anything else 409s.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-cooldown", Type = "task", Track = "dev",
                State = "active", Title = "cooldown",
                ExecutionStatus = ExecutionStatuses.CooldownBudget,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/wi-cooldown/execution-status",
            new { targetStatus = ExecutionStatuses.Queued }, JsonOpts);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(ExecutionStatuses.CooldownBudget, doc.RootElement.GetProperty("authoritative").GetString());
    }

    // ─── 400 / 404 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Invalid_Target_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-bad-target", Type = "task", Track = "dev",
                State = "proposed", Title = "x",
                ExecutionStatus = ExecutionStatuses.Idle,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient(f);
        // "in-flight" is not a valid ExecutionStatuses value.
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/wi-bad-target/execution-status",
            new { targetStatus = "in-flight" }, JsonOpts);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Empty_Target_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/anything/execution-status",
            new { targetStatus = "" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Missing_WorkItem_Returns_404()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = NewClient(f);
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/does-not-exist/execution-status",
            new { targetStatus = ExecutionStatuses.Idle }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ─── auth ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Reader_Without_X_Forge_User_Header_Is_Rejected()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-auth", Type = "task", Track = "dev",
                State = "proposed", Title = "auth",
                ExecutionStatus = ExecutionStatuses.Idle,
            });
            await db.SaveChangesAsync();
        }

        // Anonymous client — no X-Forge-User.
        using var client = f.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/work-items/wi-auth/execution-status",
            new { targetStatus = ExecutionStatuses.Idle }, JsonOpts);
        // RequireWriter returns 401 or 403 depending on the configured auth
        // chain — either is acceptable as "blocked".
        Assert.True(
            resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401/403, got {(int)resp.StatusCode}");
    }
}
