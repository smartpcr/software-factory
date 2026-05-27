// -----------------------------------------------------------------------
// <copyright file="MergedPrRescoreReconcilerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Recovery coverage for <see cref="MergedPrRescoreReconciler"/>: a workstream
/// whose PR merged but whose latest deliverable is a non-pass (the
/// generator-timeout score-0 case) gets the evaluator run against the LANDED
/// code so the work item can close on a real score — instead of the operator
/// Restarting merged work into the same timeout forever.
/// </summary>
[Collection("Phase28")]
public class MergedPrRescoreReconcilerTests
{
    [Fact]
    public async Task Rescore_MergedPr_WithTimeoutDeliverable_WritesPass_And_ClosesWorkItem()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }

        const string wsId = "ws-rescore-pass";
        const string prId = "pr-rescore-pass";
        await SeedMergedTimeoutWorkstreamAsync(f, wsId, prId);

        var reconciler = f.Services.GetRequiredService<MergedPrRescoreReconciler>();
        var n = await reconciler.RunOnceAsync(default);

        Assert.Equal(1, n);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var latest = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == wsId)
                .OrderByDescending(d => d.Iter).ThenByDescending(d => d.Id)
                .FirstAsync();
            Assert.Equal("pass", latest.Verdict);
            Assert.True(latest.Score >= ScoringConstants.PassThreshold);
            Assert.Null(latest.FailureKind); // NaturalPass requires no FailureKind.
            Assert.Contains(MergedPrRescoreReconciler.RescoreMarker, latest.Notes);

            var wi = await db.WorkItems.AsNoTracking().SingleAsync(x => x.Id == wsId);
            Assert.Equal("closed", wi.State);
            Assert.Equal(ExecutionStatuses.Completed, wi.ExecutionStatus);
        }

        // Idempotent: the marker on the latest deliverable suppresses a re-run.
        var n2 = await reconciler.RunOnceAsync(default);
        Assert.Equal(0, n2);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            // 1 seeded timeout deliverable + 1 re-score deliverable, no more.
            Assert.Equal(2, await db.Deliverables.CountAsync(d => d.WorkItemId == wsId));
        }
    }

    [Fact]
    public async Task Rescore_MergedPr_SubPass_RecordsHonestScore_DoesNotCloseWorkItem()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }

        // Force the evaluator to return a sub-pass score.
        var strategy = (DeterministicStoryArtifactStrategy)f.Services.GetRequiredService<IStoryArtifactStrategy>();
        strategy.MergedPrScoreFactory = ws => new MergedPrScore(
            Score: 70,
            Dims: new Dictionary<string, int> { ["correctness"] = 30 },
            Verdict: "iterate",
            Notes: "merged code has a real gap in error handling.",
            EvalCostUsd: 0m,
            EvaluatorAgentId: "eval-deterministic");

        const string wsId = "ws-rescore-subpass";
        const string prId = "pr-rescore-subpass";
        try
        {
            await SeedMergedTimeoutWorkstreamAsync(f, wsId, prId);

            var reconciler = f.Services.GetRequiredService<MergedPrRescoreReconciler>();
            var n = await reconciler.RunOnceAsync(default);
            Assert.Equal(1, n);

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var latest = await db.Deliverables.AsNoTracking()
                    .Where(d => d.WorkItemId == wsId)
                    .OrderByDescending(d => d.Iter).ThenByDescending(d => d.Id)
                    .FirstAsync();
                Assert.Equal("iterate", latest.Verdict);
                Assert.Equal(70, latest.Score);
                Assert.Contains(MergedPrRescoreReconciler.RescoreMarker, latest.Notes);

                // Sub-pass does NOT close the work item.
                var wi = await db.WorkItems.AsNoTracking().SingleAsync(x => x.Id == wsId);
                Assert.NotEqual("closed", wi.State);
            }

            // Idempotent: marker suppresses a second re-score.
            var n2 = await reconciler.RunOnceAsync(default);
            Assert.Equal(0, n2);
        }
        finally
        {
            strategy.MergedPrScoreFactory = null;
        }
    }

    [Fact]
    public async Task Rescore_Skips_Workstream_Without_MergedPr()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }

        const string wsId = "ws-rescore-nopr";
        const string prId = "pr-rescore-active";
        await SeedMergedTimeoutWorkstreamAsync(f, wsId, prId, prCol: "open");

        var reconciler = f.Services.GetRequiredService<MergedPrRescoreReconciler>();
        Assert.Equal(0, await reconciler.RunOnceAsync(default));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == wsId));
    }

    [Fact]
    public async Task Rescore_Skips_Workstream_AlreadyNaturalPass()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }

        const string wsId = "ws-rescore-alreadypass";
        const string prId = "pr-rescore-alreadypass";
        await SeedMergedTimeoutWorkstreamAsync(f, wsId, prId, deliverableScore: 95, deliverableVerdict: "pass", failureKind: null);

        var reconciler = f.Services.GetRequiredService<MergedPrRescoreReconciler>();
        Assert.Equal(0, await reconciler.RunOnceAsync(default));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == wsId));
    }

    private static async Task SeedMergedTimeoutWorkstreamAsync(
        ForgeWebApplicationFactory f,
        string wsId,
        string prId,
        string prCol = "merged",
        int deliverableScore = 0,
        string deliverableVerdict = "iterate",
        string? failureKind = "generator-timeout")
    {
        // NB: rely on ForgeSeed's single default product (atlas). In test mode
        // every ProductDbContext falls back to the shared control DB, so adding
        // a SECOND product would make ProductFanout visit the same rows twice
        // and double-process. `productId` here is just a label for Epic/StoryId
        // strings (not an FK).
        const string productId = "prod-rescore";
        var spawnId = $"spawn-{wsId}";

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId,
            StoryId = $"{productId}:STORY",
            StoryTitle = "rescore regression",
            Phase = StorySpawnPhases.Implement,
            StoryPoints = 8,
            BaseBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.Workstreams.Add(new WorkstreamRow
        {
            Id = wsId,
            Sort = 0,
            Name = wsId,
            Branch = $"ws/{wsId}",
            Base = "main",
            Epic = $"{productId}:STORY",
            Status = "complete",
            StorySpawnId = spawnId,
            WorktreeJson = JsonSerializer.Serialize(new[] { "(not yet created)" }),
            AgentsJson = "[]",
            PrsJson = JsonSerializer.Serialize(new[] { prId }),
            CardsJson = "[]",
            Summary = wsId,
            Category = WorkstreamCategories.Impl,
        });

        db.Prs.Add(new PrRow
        {
            Id = prId,
            Sort = 0,
            Col = prCol,
            Epic = $"{productId}:STORY",
            Workstream = wsId,
            Base = "main",
            Branch = $"ws/{wsId}",
            Title = "[impl] rescore regression",
            IsActive = prCol != "merged",
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        db.WorkItems.Add(new WorkItemRow
        {
            Id = wsId,
            Provider = "local",
            Track = "dev",
            Type = "task",
            State = "proposed",
            Title = wsId,
            DescriptionMd = "rescore regression",
            Points = 8,
            Priority = "med",
            ExecutionStatus = ExecutionStatuses.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wsId,
            CrewId = "crew-eng",
            GeneratorAgentId = "HD3",
            EvaluatorAgentId = "HU",
            Iter = 1,
            PairAttempt = 1,
            Score = deliverableScore,
            DimsJson = "{}",
            Verdict = deliverableVerdict,
            Notes = failureKind is null ? "ok" : $"GENERATOR FAILED ({failureKind}): hard timeout.",
            FailureKind = failureKind,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }
}
