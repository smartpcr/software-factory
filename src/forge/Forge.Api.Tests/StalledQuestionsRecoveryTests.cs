// -----------------------------------------------------------------------
// <copyright file="StalledQuestionsRecoveryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 2026-05-15 — Bug 2 + Bug 3 regression tests for the stalled-questions
/// leak. Bug 2 covers
/// <c>TaskExecutionEndpoints.TryUnparkAndResumeAsync</c> workstream
/// branch clearing the bogus <c>"Sloppy-3-attempts:"</c> stall when the
/// operator dismisses the parked questions. Bug 3 covers Phase C of
/// <see cref="StalledMergedRecoveryReconciler"/> auto-recovering rows
/// where the WI passed but no PR was opened.
/// </summary>
public class StalledQuestionsRecoveryTests
{
    private const string WsId = "ws-stalled-recovery-test";

    private static async Task<ForgeWebApplicationFactory> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }
        return f;
    }

    private static async Task SeedStalledWorkstreamAsync(
        ControlDbContext control, ProductDbContext db, string wsId, string stallReason, string executionStatus,
        string? failureReason, string? latestVerdict, string? prsJson = null, int latestScore = 92,
        string? latestFailureKind = null, string latestNotes = "")
    {
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = wsId,
            Name = "test ws",
            Branch = "ws/test",
            Base = "main",
            Epic = "EPIC-test",
            Status = "stalled",
            StallReason = stallReason,
            PrsJson = prsJson ?? "[]",
            CardsJson = $"[\"{wsId}\"]",
            LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = wsId,
            Title = "test",
            ExecutionStatus = executionStatus,
            FailureReason = failureReason,
        });
        if (latestVerdict is not null)
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = wsId,
                CrewId = "crew-x",
                GeneratorAgentId = "gen",
                EvaluatorAgentId = "eval",
                Iter = 3,
                Score = latestScore,
                Verdict = latestVerdict,
                DimsJson = "{}",
                FailureKind = latestFailureKind,
                Notes = latestNotes,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    // ────────────────────────────────────────────────────────────────────
    // Bug 2 — DismissQuestions/AnswerQuestions clears bogus stall
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bug2_Dismiss_Clears_Sloppy3Attempts_Stall_On_Workstream()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Sloppy-3-attempts: Failed after 3 reassignments...",
            executionStatus: ExecutionStatuses.Failed,
            failureReason: "Failed after 3 reassignments...",
            latestVerdict: "stalled-questions");
        db.DeliverableQuestions.Add(new DeliverableQuestionRow
        {
            WorkItemId = WsId,
            ExternalId = "q1",
            Iter = 1,
            Text = "test?",
            Kind = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test-operator");
        var resp = await client.PostAsJsonAsync(
            $"/api/work-items/{WsId}/questions/dismiss",
            new { externalIds = new[] { "q1" } });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);
        var wi = await verifyDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == WsId);

        Assert.Equal("active", ws.Status);
        Assert.Null(ws.StallReason);
        Assert.Null(wi.FailureReason);
        Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
    }

    [Fact]
    public async Task Bug2_Dismiss_Leaves_BuildFailure_Stall_Untouched()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Build-failure stall reason — should NOT be cleared by dismiss.
        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Gate failed (build) running `dotnet build`: 3 consecutive failed iterations...",
            executionStatus: ExecutionStatuses.Failed,
            failureReason: null,
            latestVerdict: "stalled-questions");
        db.DeliverableQuestions.Add(new DeliverableQuestionRow
        {
            WorkItemId = WsId,
            ExternalId = "q1",
            Iter = 1,
            Text = "test?",
            Kind = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test-operator");
        var resp = await client.PostAsJsonAsync(
            $"/api/work-items/{WsId}/questions/dismiss",
            new { externalIds = new[] { "q1" } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);

        // Stall is genuine — preserved.
        Assert.Equal("stalled", ws.Status);
        Assert.NotNull(ws.StallReason);
        Assert.StartsWith("Gate failed", ws.StallReason);
    }

    [Fact]
    public async Task Bug2_Dismiss_Leaves_Cancelled_Workstream_Untouched()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Cancelled workstreams are intentional terminal states.
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = WsId,
            Name = "cancelled ws",
            Branch = "ws/test",
            Base = "main",
            Epic = "EPIC-test",
            Status = "cancelled",
            StallReason = "Sloppy-3-attempts: ...", // historic residue
            PrsJson = "[]",
            CardsJson = $"[\"{WsId}\"]",
            LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        db.WorkItems.Add(new WorkItemRow { Id = WsId, Title = "test" });
        db.DeliverableQuestions.Add(new DeliverableQuestionRow
        {
            WorkItemId = WsId,
            ExternalId = "q1",
            Iter = 1,
            Text = "test?",
            Kind = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test-operator");
        await client.PostAsJsonAsync($"/api/work-items/{WsId}/questions/dismiss",
            new { externalIds = new[] { "q1" } });

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);

        // Cancelled stays cancelled — recovery only fires when Status=="stalled".
        Assert.Equal("cancelled", ws.Status);
    }

    [Fact]
    public async Task PhaseA_MergedPrRecovery_Stamps_WorkItem_Completed_Even_When_Already_Closed()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Sloppy-3-attempts: stale terminal failure",
            executionStatus: ExecutionStatuses.Queued,
            failureReason: null,
            latestVerdict: "pass",
            prsJson: "[\"PR-merged-1\"]");
        var wiSeed = await db.WorkItems.FirstAsync(w => w.Id == WsId);
        wiSeed.State = "closed";
        db.Prs.Add(new PrRow
        {
            Id = "PR-merged-1",
            Workstream = WsId,
            Branch = "ws/test",
            Base = "main",
            Title = "merged implementation",
            Col = "merged",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        var recovered = await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);
        var wi = await verifyDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == WsId);

        Assert.True(recovered >= 1);
        Assert.Equal("complete", ws.Status);
        Assert.Null(ws.StallReason);
        Assert.Equal("closed", wi.State);
        Assert.Equal(ExecutionStatuses.Completed, wi.ExecutionStatus);
    }

    [Fact]
    public async Task PhaseA_MergedPrRecovery_Uses_PrRow_Workstream_When_PrsJson_Was_Cleared()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Sloppy-3-attempts: stale terminal failure",
            executionStatus: ExecutionStatuses.Queued,
            failureReason: null,
            latestVerdict: "pass",
            prsJson: "[]");
        var wiSeed = await db.WorkItems.FirstAsync(w => w.Id == WsId);
        wiSeed.State = "closed";
        db.Prs.Add(new PrRow
        {
            Id = "PR-merged-row-link",
            Workstream = WsId,
            Branch = "ws/test",
            Base = "main",
            Title = "merged implementation",
            Col = "merged",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        var recovered = await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);
        var wi = await verifyDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == WsId);

        Assert.True(recovered >= 1);
        Assert.Equal("complete", ws.Status);
        Assert.Null(ws.StallReason);
        Assert.Equal("closed", wi.State);
        Assert.Equal(ExecutionStatuses.Completed, wi.ExecutionStatus);
    }

    [Fact]
    public async Task PhaseA_CompleteMergedWorkstream_Aligns_WorkItem_Completed_When_Already_Closed()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.Workstreams.Add(new WorkstreamRow
        {
            Id = WsId,
            Name = "complete ws",
            Branch = "ws/test",
            Base = "main",
            Epic = "EPIC-test",
            Status = "complete",
            PrsJson = "[]",
            CardsJson = $"[\"{WsId}\"]",
            LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = WsId,
            Title = "test",
            State = "closed",
            ExecutionStatus = ExecutionStatuses.Queued,
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WsId,
            CrewId = "crew-x",
            GeneratorAgentId = "gen",
            EvaluatorAgentId = "eval",
            Iter = 3,
            Score = 92,
            Verdict = "pass",
            DimsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Prs.Add(new PrRow
        {
            Id = "PR-merged-1",
            Workstream = WsId,
            Branch = "ws/test",
            Base = "main",
            Title = "merged implementation",
            Col = "merged",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        var recovered = await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);
        var wi = await verifyDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == WsId);

        Assert.True(recovered >= 1);
        Assert.Equal("complete", ws.Status);
        Assert.Equal("closed", wi.State);
        Assert.Equal(ExecutionStatuses.Completed, wi.ExecutionStatus);
    }

    [Fact]
    public async Task PhaseA_MergedPrRecovery_Refuses_Completion_When_Latest_Deliverable_Is_Sloppy_Zero()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Sloppy-3-attempts: stale terminal failure",
            executionStatus: ExecutionStatuses.Queued,
            failureReason: null,
            latestVerdict: "sloppy",
            prsJson: "[\"PR-merged-1\"]",
            latestScore: 0);
        db.Prs.Add(new PrRow
        {
            Id = "PR-merged-1",
            Workstream = WsId,
            Branch = "ws/test",
            Base = "main",
            Title = "merged implementation",
            Col = "merged",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        var recovered = await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);
        var wi = await verifyDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == WsId);

        Assert.Equal(0, recovered);
        Assert.Equal("stalled", ws.Status);
        Assert.NotNull(ws.StallReason);
        Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
    }

    [Fact]
    public async Task PhaseA_CompleteMergedWorkstream_Refuses_WorkItem_Completion_When_Latest_Deliverable_Is_Sloppy_Zero()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.Workstreams.Add(new WorkstreamRow
        {
            Id = WsId,
            Name = "complete ws",
            Branch = "ws/test",
            Base = "main",
            Epic = "EPIC-test",
            Status = "complete",
            PrsJson = "[]",
            CardsJson = $"[\"{WsId}\"]",
            LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = WsId,
            Title = "test",
            State = "closed",
            ExecutionStatus = ExecutionStatuses.Queued,
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WsId,
            CrewId = "crew-x",
            GeneratorAgentId = "gen",
            EvaluatorAgentId = "eval",
            Iter = 3,
            Score = 0,
            Verdict = "sloppy",
            DimsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Prs.Add(new PrRow
        {
            Id = "PR-merged-1",
            Workstream = WsId,
            Branch = "ws/test",
            Base = "main",
            Title = "merged implementation",
            Col = "merged",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        var recovered = await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await verifyDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == WsId);

        Assert.Equal(0, recovered);
        Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
    }

    // ────────────────────────────────────────────────────────────────────
    // Bug 3 — StalledMergedRecoveryReconciler Phase C
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bug3_PhaseC_Recovers_Sloppy3Attempts_With_Pass_And_NoPr()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Sloppy-3-attempts: Failed after 3 reassignments...",
            executionStatus: ExecutionStatuses.Completed,
            failureReason: null,
            latestVerdict: "pass");

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        var recovered = await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);
        var wi = await verifyDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == WsId);

        Assert.True(recovered >= 1);
        Assert.Equal("active", ws.Status);
        Assert.Null(ws.StallReason);
        Assert.Null(wi.FailureReason);
    }

    [Fact]
    public async Task Bug3_PhaseC_Skips_When_Pr_Exists()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Sloppy-3-attempts: ...",
            executionStatus: ExecutionStatuses.Completed,
            failureReason: null,
            latestVerdict: "pass",
            prsJson: "[\"PR-99\"]");

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);

        // PR exists → Phase C must skip; row stays stalled for triage.
        Assert.Equal("stalled", ws.Status);
        Assert.NotNull(ws.StallReason);
    }

    [Fact]
    public async Task Bug3_PhaseC_Skips_When_Stall_Is_BuildFailure()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Gate failed (build) running `dotnet build`: 3 consecutive failed iterations...",
            executionStatus: ExecutionStatuses.Completed,
            failureReason: null,
            latestVerdict: "pass");

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);

        // Build-failure stall reason — Phase C must NOT recover.
        Assert.Equal("stalled", ws.Status);
        Assert.StartsWith("Gate failed", ws.StallReason);
    }

    [Fact]
    public async Task Bug3_PhaseC_Skips_When_Latest_Verdict_Is_Not_Pass()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Sloppy-3-attempts: ...",
            executionStatus: ExecutionStatuses.Queued,
            failureReason: null,
            latestVerdict: "iterate");

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);

        // Verdict not pass — Phase C waits.
        Assert.Equal("stalled", ws.Status);
    }

    [Fact]
    public async Task Bug3_PhaseC_Skips_When_Wi_Has_Failure_Reason()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // WI still carries terminal failure indication — don't recover.
        await SeedStalledWorkstreamAsync(control, db, WsId,
            stallReason: "Sloppy-3-attempts: ...",
            executionStatus: ExecutionStatuses.Failed,
            failureReason: "Failed after 3 reassignments...",
            latestVerdict: "pass");

        var reconciler = scope.ServiceProvider.GetRequiredService<StalledMergedRecoveryReconciler>();
        await reconciler.RunOnceAsync(CancellationToken.None);

        using var verifyScope = f.Services.CreateScope();
        var control_verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await verifyDb.Workstreams.AsNoTracking().FirstAsync(w => w.Id == WsId);

        Assert.Equal("stalled", ws.Status);
    }
}
