// -----------------------------------------------------------------------
// <copyright file="Phase47BlockedStatusTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 47 — orchestrator-side regression tests for the workstream livelock.
/// When a workstream's most-recent deliverable is a non-pass terminal verdict
/// (<c>stalled</c> / <c>stalled-budget</c> / <c>reject</c>), the orchestrator
/// must NOT re-spawn the gen↔eval loop; it must move the workstream to
/// <c>Status="blocked"</c> so the operator can triage. Before this fix, the
/// orchestrator's <c>DriveOneWorkstreamImplAsync</c> only special-cased
/// <c>pass</c> and fell through to <c>ProduceWorkstreamChangesAsync</c> for
/// every other verdict, which combined with the <see cref="DeliverableLoop"/>
/// resume bug to produce 139 deliverables / $9,825 burn on a single workstream.
/// </summary>
public class Phase47BlockedStatusTests
{
    private static async Task<(string storyId, string spawnId, string workstreamId)> SeedSpawnImplementWithWorkstreamAsync(
        ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Story.
        db.WorkItems.Add(new WorkItemRow
        {
            Id = storyId,
            Provider = "ado",
            Track = "dev",
            Type = "feature",
            State = "active",
            Title = $"Story {storyId}",
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Story spawn at phase=implement.
        var spawnId = $"spawn-{storyId}";
        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId,
            StoryId = storyId,
            StoryTitle = $"Story {storyId}",
            Phase = StorySpawnPhases.Implement,
            BaseBranch = "main",
            DecompositionApproved = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            StoryPoints = 5,
        });

        // Workstream child + active.
        var wsId = $"ws-{storyId}-stage-1";
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = wsId,
            StorySpawnId = spawnId,
            Name = "Stage 1",
            Branch = $"ws/{storyId}/stage-1",
            Base = "main",
            Epic = storyId,
            Status = "active",
            Summary = "Stage 1 work",
            AgentsJson = "[\"E9\"]",
        });

        await db.SaveChangesAsync();
        return (storyId, spawnId, wsId);
    }

    private static async Task PersistDeliverableAsync(
        ForgeWebApplicationFactory f, string workstreamId, string verdict, int iter, int score)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = workstreamId,
            CrewId = "crew-eng",
            GeneratorAgentId = "E9",
            EvaluatorAgentId = "R3",
            Iter = iter,
            Score = score,
            DimsJson = "{}",
            Verdict = verdict,
            Notes = $"deterministic test seed: iter {iter} ended {verdict}",
            GenCostUsd = 0m,
            EvalCostUsd = 0m,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> ReadWorkstreamStatusAsync(ForgeWebApplicationFactory f, string workstreamId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return (await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == workstreamId)).Status;
    }

    /// <summary>
    /// The headline regression: a workstream whose latest deliverable is
    /// <c>stalled</c> must NOT re-enter the gen↔eval loop on the next
    /// orchestrator drive. The orchestrator flips it to <c>blocked</c> and
    /// stops touching it until the operator clicks Restart.
    /// </summary>
    [Fact]
    public async Task DriveImplement_With_Stalled_Last_Deliverable_Marks_Blocked()
    {
        var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        try
        {
            using (f.CreateClient()) { /* triggers boot */ }
            var (storyId, _, wsId) = await SeedSpawnImplementWithWorkstreamAsync(f, "WI-47-100");
            await PersistDeliverableAsync(f, wsId, verdict: "stalled", iter: 5, score: 80);

            using var scope = f.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orchestrator.DriveAsync(storyId);

            Assert.Equal("blocked", await ReadWorkstreamStatusAsync(f, wsId));
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Same routing for <c>stalled-budget</c>: the budget cap was tripped, so
    /// re-running with the same cap just burns more dollars. Operator must
    /// raise budget via Restart.
    /// </summary>
    [Fact]
    public async Task DriveImplement_With_StalledBudget_Last_Deliverable_Marks_Blocked()
    {
        var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        try
        {
            using (f.CreateClient()) { /* triggers boot */ }
            var (storyId, _, wsId) = await SeedSpawnImplementWithWorkstreamAsync(f, "WI-47-101");
            await PersistDeliverableAsync(f, wsId, verdict: "stalled-budget", iter: 2, score: 78);

            using var scope = f.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orchestrator.DriveAsync(storyId);

            Assert.Equal("blocked", await ReadWorkstreamStatusAsync(f, wsId));
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// <c>reject</c> is a lead-triage verdict — the loop bounced back to the
    /// product owner. Re-running without operator input would just produce
    /// another reject. Block the workstream.
    /// </summary>
    [Fact]
    public async Task DriveImplement_With_Reject_Last_Deliverable_Marks_Blocked()
    {
        var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        try
        {
            using (f.CreateClient()) { /* triggers boot */ }
            var (storyId, _, wsId) = await SeedSpawnImplementWithWorkstreamAsync(f, "WI-47-102");
            await PersistDeliverableAsync(f, wsId, verdict: "reject", iter: 1, score: 30);

            using var scope = f.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orchestrator.DriveAsync(storyId);

            Assert.Equal("blocked", await ReadWorkstreamStatusAsync(f, wsId));
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Once a workstream is <c>blocked</c>, subsequent drives must skip it
    /// — the in-flight + failure-backoff guards never fire because the
    /// status filter at the top of <c>DriveImplementAsync</c> short-circuits
    /// first. Status remains <c>blocked</c> (no flip back to <c>active</c>),
    /// no new deliverables, no spawn-started events.
    /// </summary>
    [Fact]
    public async Task DriveImplement_Skips_Blocked_Workstream_On_Subsequent_Drives()
    {
        var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        try
        {
            using (f.CreateClient()) { /* triggers boot */ }
            var (storyId, _, wsId) = await SeedSpawnImplementWithWorkstreamAsync(f, "WI-47-103");
            await PersistDeliverableAsync(f, wsId, verdict: "stalled", iter: 5, score: 80);

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync(storyId);
            }
            Assert.Equal("blocked", await ReadWorkstreamStatusAsync(f, wsId));

            int delivBefore;
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                delivBefore = await db.Deliverables.CountAsync(d => d.WorkItemId == wsId);
            }

            // Drive again — blocked must be sticky.
            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync(storyId);
                await orchestrator.DriveAsync(storyId);
            }

            Assert.Equal("blocked", await ReadWorkstreamStatusAsync(f, wsId));

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var delivAfter = await db.Deliverables.CountAsync(d => d.WorkItemId == wsId);
                Assert.Equal(delivBefore, delivAfter);
            }
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// A <c>pass</c>-verdict workstream is NOT routed to blocked — that path
    /// is only for non-pass terminals. Pass workstreams continue to the
    /// commit + PR + review flow as before.
    /// </summary>
    [Fact]
    public async Task DriveImplement_With_Pass_Last_Deliverable_Does_Not_Mark_Blocked()
    {
        var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        try
        {
            using (f.CreateClient()) { /* triggers boot */ }
            var (storyId, _, wsId) = await SeedSpawnImplementWithWorkstreamAsync(f, "WI-47-104");
            await PersistDeliverableAsync(f, wsId, verdict: "pass", iter: 3, score: 92);

            using var scope = f.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orchestrator.DriveAsync(storyId);

            // Pass workstreams advance to "review" via the commit+PR flow,
            // not to "blocked". The exact terminal status depends on the
            // in-memory git ops + provider, but it MUST NOT be "blocked".
            var status = await ReadWorkstreamStatusAsync(f, wsId);
            Assert.NotEqual("blocked", status);
        }
        finally { f.Dispose(); }
    }
}
