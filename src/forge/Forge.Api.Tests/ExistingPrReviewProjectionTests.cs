// -----------------------------------------------------------------------
// <copyright file="ExistingPrReviewProjectionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Regression coverage for a projection stall where impl workstreams already
/// had PR rows but remained <c>Status=active</c>, causing the orchestrator to
/// retry PR creation every tick instead of moving them to review.
/// </summary>
[Collection("Phase28")]
public class ExistingPrReviewProjectionTests
{
    [Fact]
    public async Task DriveImplement_MovesActiveWorkstreamWithExistingPrToReview_WithoutRespawn()
    {
        const string storyId = "WI-existing-pr-review-projection";
        const string spawnId = "spawn-existing-pr-review-projection";
        const string wsId = "ws-existing-pr-review-projection";
        const string prId = "PR existing-pr-review-projection#1";

        using var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        using (f.CreateClient()) { /* boot + migrations */ }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.AddRange(
                new WorkItemRow
                {
                    Id = storyId,
                    RepoId = ForgeSeed.DefaultRepoId,
                    Provider = "local",
                    Track = "dev",
                    Type = "feature",
                    State = "active",
                    Title = "Existing PR review projection",
                    DescriptionMd = string.Empty,
                    IterationRef = string.Empty,
                    AreaPath = string.Empty,
                    TagsJson = "[]",
                    Priority = "med",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                new WorkItemRow
                {
                    Id = wsId,
                    RepoId = ForgeSeed.DefaultRepoId,
                    Provider = "local",
                    Track = "dev",
                    Type = "task",
                    State = "active",
                    Title = "Existing PR review projection workstream",
                    DescriptionMd = string.Empty,
                    IterationRef = string.Empty,
                    AreaPath = string.Empty,
                    TagsJson = "[]",
                    Priority = "med",
                    ExecutionStatus = ExecutionStatuses.Queued,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });

            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = "Existing PR review projection",
                Phase = StorySpawnPhases.Implement,
                RepoId = ForgeSeed.DefaultRepoId,
                BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 3,
            });

            db.Workstreams.Add(new WorkstreamRow
            {
                Id = wsId,
                RepoId = ForgeSeed.DefaultRepoId,
                StorySpawnId = spawnId,
                Sort = 1,
                Name = "Existing PR review projection workstream",
                Branch = "ws/existing-pr-review-projection",
                Base = "main",
                Epic = storyId,
                Status = "active",
                Category = WorkstreamCategories.Impl,
                WorktreeJson = "[]",
                AgentsJson = "[]",
                PrsJson = JsonSerializer.Serialize(new[] { prId }),
                CardsJson = "[]",
                DepsJson = "[]",
                Summary = string.Empty,
                PlanAnchor = "phase-foundation/stage-existing-pr",
                PhaseAnchor = "phase-foundation",
            });

            db.Prs.Add(new PrRow
            {
                Id = prId,
                RepoId = ForgeSeed.DefaultRepoId,
                Col = "review",
                Title = "[impl] Existing PR review projection workstream",
                Branch = "ws/existing-pr-review-projection",
                Base = "main",
                Workstream = wsId,
                Epic = storyId,
                EpicColor = string.Empty,
                AuthorJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                IsActive = true,
                LastSyncStatus = "idle",
                Provider = "github",
                WorkItemId = storyId,
                Description = "seed",
            });

            foreach (var fileName in PlanDocStateHelpers.PlanDocOrder.Select(d => PlanDocStateHelpers.PlanDocFileName[d]))
            {
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = $"plan-doc:{spawnId}:{fileName}",
                    CrewId = "crew-arch",
                    GeneratorAgentId = "G",
                    EvaluatorAgentId = "E",
                    Iter = 1,
                    Score = 95,
                    DimsJson = "{}",
                    Verdict = "pass",
                    Notes = string.Empty,
                    GeneratorSummary = string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        using (var scope = f.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orch.DriveAsync(storyId);
        }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == wsId);
            var wi = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == wsId);
            var spawnStarted = await db.TaskTranscriptEvents.AsNoTracking().AnyAsync(e =>
                e.WorkItemId == wsId && e.Kind == "task.impl.spawn-started");

            Assert.Equal("review", ws.Status);
            Assert.True(ws.LastActive > 0);
            Assert.Equal(ExecutionStatuses.Completed, wi.ExecutionStatus);
            Assert.False(spawnStarted);
        }
    }
}
