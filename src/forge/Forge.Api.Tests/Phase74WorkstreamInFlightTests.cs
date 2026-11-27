// -----------------------------------------------------------------------
// <copyright file="Phase74WorkstreamInFlightTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Phase 74 follow-up — skipped-concurrent impl runs must stop at the
/// transient in-flight boundary. They must not continue into stale file
/// collection, PR-body assembly, or Phase 75 stall handling.
/// </summary>
public sealed class Phase74WorkstreamInFlightTests
{
    private sealed class WorkstreamInFlightFactory : ForgeWebApplicationFactory
    {
        public WorkstreamInFlightFactory()
            : base(new Dictionary<string, string?>
            {
                ["Forge:Stories:OrchestratorEnabled"] = "true",
                ["Forge:Stories:AutoProgressGates"] = "false",
                ["Forge:Stories:SpawnDriveInline"] = "true",
            })
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStoryArtifactStrategy>();
                services.AddSingleton<IStoryArtifactStrategy, ThrowWorkstreamInFlightStrategy>();
            });
        }
    }

    private sealed class FilesWithoutDeliverableFactory : ForgeWebApplicationFactory
    {
        public FilesWithoutDeliverableFactory()
            : base(new Dictionary<string, string?>
            {
                ["Forge:Stories:OrchestratorEnabled"] = "true",
                ["Forge:Stories:AutoProgressGates"] = "false",
                ["Forge:Stories:SpawnDriveInline"] = "true",
            })
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStoryArtifactStrategy>();
                services.AddSingleton<IStoryArtifactStrategy, FilesWithoutDeliverableStrategy>();
            });
        }
    }

    private sealed class SloppyVerdictFactory : ForgeWebApplicationFactory
    {
        public SloppyVerdictFactory()
            : base(new Dictionary<string, string?>
            {
                ["Forge:Stories:OrchestratorEnabled"] = "true",
                ["Forge:Stories:AutoProgressGates"] = "false",
                ["Forge:Stories:SpawnDriveInline"] = "true",
            })
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStoryArtifactStrategy>();
                services.AddSingleton<IStoryArtifactStrategy, ThrowSloppyVerdictStrategy>();
            });
        }
    }

    private sealed class ThrowWorkstreamInFlightStrategy : IStoryArtifactStrategy
    {
        private readonly DeterministicStoryArtifactStrategy inner = new();

        public Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
            StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default)
            => this.inner.ProducePlanArtifactsAsync(spawn, story, forceResume, ct);

        public Task<(string RepoPath, string Content)> ProduceSinglePlanArtifactAsync(
            StorySpawnRow spawn,
            WorkItemRow story,
            string docKey,
            string worktreePath,
            string planBranch,
            RepoRow? repo,
            bool forceResume = false,
            CancellationToken ct = default)
            => this.inner.ProduceSinglePlanArtifactAsync(spawn, story, docKey, worktreePath, planBranch, repo, forceResume, ct);

        public Task ResumeSingleArtifactAsync(
            StorySpawnRow spawn, WorkItemRow story, string key, bool restartFromScratch = false, CancellationToken ct = default)
            => this.inner.ResumeSingleArtifactAsync(spawn, story, key, restartFromScratch, ct);

        public Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
            StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
            => this.inner.ProduceDecompositionAsync(spawn, story, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => Task.FromException<IReadOnlyDictionary<string, string>>(new WorkstreamInFlightException(workstream.Id));

        public Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ReadCachedWorkstreamFilesAsync(spawn, workstream, ct);

        public Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
            => this.inner.ProduceReviewCommentsAsync(spawn, workstream, prId, round, ct);

        public Task<ReviewFix> ProduceReviewFixAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
            => this.inner.ProduceReviewFixAsync(spawn, workstream, comment, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceStageE2eAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceStageE2eAsync(spawn, workstream, ct);

#pragma warning disable CS0618 // Obsolete legacy-drain methods.
        public Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
            StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
            => this.inner.ProduceQaFeatureFilesAsync(spawn, story, workstreams, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceStepDefinitionsAsync(spawn, workstream, ct);
#pragma warning restore CS0618

        public Task<IReadOnlyDictionary<string, string>> ProduceConflictResolutionAsync(
            StorySpawnRow spawn,
            WorkstreamRow workstream,
            IReadOnlyList<ConflictedFile> conflictedFiles,
            string baseBranch,
            CancellationToken ct = default)
            => this.inner.ProduceConflictResolutionAsync(spawn, workstream, conflictedFiles, baseBranch, ct);

        public Task<MergedPrScore> ScoreMergedPrAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, string mergedPrId, CancellationToken ct = default)
            => this.inner.ScoreMergedPrAsync(spawn, workstream, mergedPrId, ct);
    }

    private sealed class FilesWithoutDeliverableStrategy : IStoryArtifactStrategy
    {
        private readonly DeterministicStoryArtifactStrategy inner = new();

        public Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
            StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default)
            => this.inner.ProducePlanArtifactsAsync(spawn, story, forceResume, ct);

        public Task<(string RepoPath, string Content)> ProduceSinglePlanArtifactAsync(
            StorySpawnRow spawn,
            WorkItemRow story,
            string docKey,
            string worktreePath,
            string planBranch,
            RepoRow? repo,
            bool forceResume = false,
            CancellationToken ct = default)
            => this.inner.ProduceSinglePlanArtifactAsync(spawn, story, docKey, worktreePath, planBranch, repo, forceResume, ct);

        public Task ResumeSingleArtifactAsync(
            StorySpawnRow spawn, WorkItemRow story, string key, bool restartFromScratch = false, CancellationToken ct = default)
            => this.inner.ResumeSingleArtifactAsync(spawn, story, key, restartFromScratch, ct);

        public Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
            StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
            => this.inner.ProduceDecompositionAsync(spawn, story, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"src/.story-artifacts/{StoryPaths.Safe(spawn.StoryId)}/no-pass.md"] =
                    "files without a persisted pass",
            };
            return Task.FromResult(files);
        }

        public Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ReadCachedWorkstreamFilesAsync(spawn, workstream, ct);

        public Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
            => this.inner.ProduceReviewCommentsAsync(spawn, workstream, prId, round, ct);

        public Task<ReviewFix> ProduceReviewFixAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
            => this.inner.ProduceReviewFixAsync(spawn, workstream, comment, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceStageE2eAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceStageE2eAsync(spawn, workstream, ct);

#pragma warning disable CS0618 // Obsolete legacy-drain methods.
        public Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
            StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
            => this.inner.ProduceQaFeatureFilesAsync(spawn, story, workstreams, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceStepDefinitionsAsync(spawn, workstream, ct);
#pragma warning restore CS0618

        public Task<IReadOnlyDictionary<string, string>> ProduceConflictResolutionAsync(
            StorySpawnRow spawn,
            WorkstreamRow workstream,
            IReadOnlyList<ConflictedFile> conflictedFiles,
            string baseBranch,
            CancellationToken ct = default)
            => this.inner.ProduceConflictResolutionAsync(spawn, workstream, conflictedFiles, baseBranch, ct);

        public Task<MergedPrScore> ScoreMergedPrAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, string mergedPrId, CancellationToken ct = default)
            => this.inner.ScoreMergedPrAsync(spawn, workstream, mergedPrId, ct);
    }

    private sealed class ThrowSloppyVerdictStrategy : IStoryArtifactStrategy
    {
        private readonly DeterministicStoryArtifactStrategy inner = new();

        public Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
            StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default)
            => this.inner.ProducePlanArtifactsAsync(spawn, story, forceResume, ct);

        public Task<(string RepoPath, string Content)> ProduceSinglePlanArtifactAsync(
            StorySpawnRow spawn,
            WorkItemRow story,
            string docKey,
            string worktreePath,
            string planBranch,
            RepoRow? repo,
            bool forceResume = false,
            CancellationToken ct = default)
            => this.inner.ProduceSinglePlanArtifactAsync(spawn, story, docKey, worktreePath, planBranch, repo, forceResume, ct);

        public Task ResumeSingleArtifactAsync(
            StorySpawnRow spawn, WorkItemRow story, string key, bool restartFromScratch = false, CancellationToken ct = default)
            => this.inner.ResumeSingleArtifactAsync(spawn, story, key, restartFromScratch, ct);

        public Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
            StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
            => this.inner.ProduceDecompositionAsync(spawn, story, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => Task.FromException<IReadOnlyDictionary<string, string>>(
                new SloppyVerdictException(
                    new DeliverableLoopResult(
                        FinalVerdict: "sloppy",
                        Iterations: 1,
                        FinalScore: 0,
                        DeliverableRowIds: Array.Empty<long>(),
                        TotalCostUsd: 0.12m,
                        SloppyReason: SloppyReasons.NoProgressScoreZero),
                    "empty diff produced score-zero sloppy verdict"));

        public Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ReadCachedWorkstreamFilesAsync(spawn, workstream, ct);

        public Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
            => this.inner.ProduceReviewCommentsAsync(spawn, workstream, prId, round, ct);

        public Task<ReviewFix> ProduceReviewFixAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
            => this.inner.ProduceReviewFixAsync(spawn, workstream, comment, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceStageE2eAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceStageE2eAsync(spawn, workstream, ct);

#pragma warning disable CS0618 // Obsolete legacy-drain methods.
        public Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
            StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
            => this.inner.ProduceQaFeatureFilesAsync(spawn, story, workstreams, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceStepDefinitionsAsync(spawn, workstream, ct);
#pragma warning restore CS0618

        public Task<IReadOnlyDictionary<string, string>> ProduceConflictResolutionAsync(
            StorySpawnRow spawn,
            WorkstreamRow workstream,
            IReadOnlyList<ConflictedFile> conflictedFiles,
            string baseBranch,
            CancellationToken ct = default)
            => this.inner.ProduceConflictResolutionAsync(spawn, workstream, conflictedFiles, baseBranch, ct);

        public Task<MergedPrScore> ScoreMergedPrAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, string mergedPrId, CancellationToken ct = default)
            => this.inner.ScoreMergedPrAsync(spawn, workstream, mergedPrId, ct);
    }

    [Fact]
    public async Task DriveImplement_WorkstreamInFlight_DoesNotMarkStalledOrSuccess()
    {
        var f = new WorkstreamInFlightFactory();
        try
        {
            using (f.CreateClient()) { }

            var (storyId, wsId) = await SeedImplementWorkstreamAsync(f, "WI-74-inflight");

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync(storyId);
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == wsId);
                Assert.Equal("active", ws.Status);
                Assert.Null(ws.StallReason);

                var finish = await db.TaskTranscriptEvents.AsNoTracking()
                    .Where(e => e.WorkItemId == wsId && e.Kind == ImplSpawnGuard.SpawnFinishedKind)
                    .OrderByDescending(e => e.Id)
                    .FirstAsync();
                using var payload = JsonDocument.Parse(finish.PayloadJson);
                Assert.True(payload.RootElement.GetProperty("skippedConcurrent").GetBoolean());
                Assert.False(payload.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == wsId));
                Assert.Equal(0, await db.Prs.CountAsync(p => p.Workstream == wsId));
            }
        }
        finally
        {
            f.Dispose();
        }
    }

    /// <summary>
    /// A tick that finds a drive of the workstream already in flight in this
    /// process (mid-iteration, or held on declared background work) skips it
    /// quietly: no spawn-started / spawn-finished pair, no recovery resume, no
    /// strategy call. Seen on harness:DSH-M2 stage 5.9 2026-11-27 as two or
    /// three skip pairs a minute through a 45-minute hold.
    /// </summary>
    [Fact]
    public async Task DriveImplement_DriveAlreadyInFlight_SkipsWithoutEvents()
    {
        var f = new WorkstreamInFlightFactory();
        try
        {
            using (f.CreateClient()) { }

            var (storyId, wsId) = await SeedImplementWorkstreamAsync(f, "WI-74-held");

            using (var held = DeliverableLoop.TryReserveWorkItem(wsId))
            {
                Assert.NotNull(held);
                using var scope = f.Services.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync(storyId);
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var kinds = await db.TaskTranscriptEvents.AsNoTracking()
                    .Where(e => e.WorkItemId == wsId)
                    .Select(e => e.Kind)
                    .ToListAsync();
                Assert.DoesNotContain(ImplSpawnGuard.SpawnStartedKind, kinds);
                Assert.DoesNotContain(ImplSpawnGuard.SpawnFinishedKind, kinds);
                Assert.DoesNotContain("task.recovery.resumed", kinds);

                var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == wsId);
                Assert.Equal("active", ws.Status);
                Assert.Null(ws.StallReason);
            }
        }
        finally
        {
            f.Dispose();
        }
    }

    [Fact]
    public async Task DriveImplement_FilesWithoutFinalPass_DoesNotOpenPrOrMarkSuccess()
    {
        var f = new FilesWithoutDeliverableFactory();
        try
        {
            using (f.CreateClient()) { }

            var (storyId, wsId) = await SeedImplementWorkstreamAsync(f, "WI-74-no-final-pass");

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync(storyId);
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == wsId);
                Assert.Equal("active", ws.Status);
                Assert.Null(ws.StallReason);
                Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == wsId));
                Assert.Equal(0, await db.Prs.CountAsync(p => p.Workstream == wsId));

                var finish = await db.TaskTranscriptEvents.AsNoTracking()
                    .Where(e => e.WorkItemId == wsId && e.Kind == ImplSpawnGuard.SpawnFinishedKind)
                    .OrderByDescending(e => e.Id)
                    .FirstAsync();
                using var payload = JsonDocument.Parse(finish.PayloadJson);
                Assert.True(payload.RootElement.GetProperty("passGateRefused").GetBoolean());
                Assert.False(payload.RootElement.GetProperty("success").GetBoolean());
            }
        }
        finally
        {
            f.Dispose();
        }
    }

    [Fact]
    public async Task DriveImplement_SloppyVerdictException_ParksWorkItemForReassign()
    {
        var f = new SloppyVerdictFactory();
        try
        {
            using (f.CreateClient()) { }

            var (storyId, wsId) = await SeedImplementWorkstreamAsync(f, "WI-74-sloppy-exception");

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync(storyId);
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == wsId);
                Assert.Equal("active", ws.Status);
                Assert.Null(ws.StallReason);
                Assert.Equal(0, await db.Prs.CountAsync(p => p.Workstream == wsId));

                var wi = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == wsId);
                Assert.Equal(ExecutionStatuses.PendingReassign, wi.ExecutionStatus);
                Assert.False(string.IsNullOrWhiteSpace(wi.PriorPairsJson));
                var priorPairs = JsonSerializer.Deserialize<List<string[]>>(wi.PriorPairsJson!)!;
                var assigned = JsonSerializer.Deserialize<string[]>(ws.AgentsJson)!;
                Assert.Contains(priorPairs, p => p.Length == 2 && p[0] == assigned[0] && p[1] == assigned[1]);

                var finish = await db.TaskTranscriptEvents.AsNoTracking()
                    .Where(e => e.WorkItemId == wsId && e.Kind == ImplSpawnGuard.SpawnFinishedKind)
                    .OrderByDescending(e => e.Id)
                    .FirstAsync();
                using var payload = JsonDocument.Parse(finish.PayloadJson);
                Assert.False(payload.RootElement.GetProperty("success").GetBoolean());
                Assert.True(payload.RootElement.GetProperty("parkedForReassign").GetBoolean());
                Assert.Equal(nameof(SloppyVerdictException), payload.RootElement.GetProperty("exceptionType").GetString());
                Assert.Equal(SloppyReasons.NoProgressScoreZero, payload.RootElement.GetProperty("sloppyReason").GetString());
            }
        }
        finally
        {
            f.Dispose();
        }
    }

    private static async Task<(string StoryId, string WorkstreamId)> SeedImplementWorkstreamAsync(
        ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

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
            Points = 5,
        });

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
            AgentsJson = "[\"E9\",\"R3\"]",
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = wsId,
            Provider = "ado",
            Track = "dev",
            Type = "task",
            State = "active",
            Title = "Stage 1",
            DescriptionMd = "Stage 1 work",
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Points = 3,
            ExecutionStatus = ExecutionStatuses.Queued,
        });

        foreach (var fileName in new[] { "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md" })
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = $"plan-doc:{spawnId}:{fileName}",
                CrewId = "crew-arch",
                GeneratorAgentId = "A1",
                EvaluatorAgentId = "A2",
                Iter = 1,
                Score = 95,
                Verdict = "pass",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
        return (storyId, wsId);
    }
}
