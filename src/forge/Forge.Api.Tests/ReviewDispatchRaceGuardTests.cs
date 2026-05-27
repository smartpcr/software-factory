// -----------------------------------------------------------------------
// <copyright file="ReviewDispatchRaceGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// 2026-05-26 — regression for the DriveReviewAsync ↔ impl-DeliverableLoop
/// concurrent-dispatch race. The impl loop runs a workstream's generator in
/// the BACKGROUND, so a workstream can be Status="review" (PR opened) while
/// its impl loop is still iterating for the same workItemId. If
/// DriveReviewAsync dispatches a reviewer for that workItemId, it collides
/// with the live invocation and AgentRunnerDispatcher's Phase-74 guard throws
/// "Concurrent dispatch refused" → review-failed → the workstream STALLS.
/// (Observed on local:UPDATE-HISTORY-IN-CL stage-1.3.)
///
/// The fix adds an <see cref="IAgentRunner.IsTaskActive"/> guard at the top of
/// DriveReviewAsync's per-workstream loop: a workstream whose impl/agent
/// invocation is still live is DEFERRED. DriveReviewAsync's first action per
/// workstream is stamping Review.verdict="pending" (the guard runs before it),
/// so the verdict is the observable:
///   • idle  (IsTaskActive=false) → verdict stamped "pending" (processed)
///   • active(IsTaskActive=true)  → verdict stays null (deferred — no dispatch)
/// Calls DriveReviewAsync directly to isolate the guard from DriveImplementAsync.
/// </summary>
[Collection("Phase28")]
public class ReviewDispatchRaceGuardTests
{
    [Fact]
    public async Task ActiveImplInvocation_DefersReview_VerdictNotStamped()
        => await RunAsync(taskActive: true, expectVerdictStamped: false);

    [Fact]
    public async Task IdleWorkstream_ReviewProceeds_VerdictStamped()
        => await RunAsync(taskActive: false, expectVerdictStamped: true);

    private static async Task RunAsync(bool taskActive, bool expectVerdictStamped)
    {
        var spawnId = $"spawn-review-race-{(taskActive ? "active" : "idle")}";
        var storyId = $"WI-review-race-{(taskActive ? "active" : "idle")}";
        var wsId = $"ws-review-race-{(taskActive ? "active" : "idle")}";
        var prId = $"PR review-race#{(taskActive ? "1" : "2")}";

        var fakeRunner = new FakeRunner(taskActive ? new[] { wsId } : System.Array.Empty<string>());

        using var f = new GuardFactory(fakeRunner);
        using (f.CreateClient()) { /* boot host + migrations */ }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = wsId,
                StorySpawnId = spawnId,
                Name = "review-race ws",
                Epic = "EPIC-1",
                Branch = "ws/review-race",
                Base = "main",
                Status = "review",
                PrsJson = JsonSerializer.Serialize(new[] { prId }),
                AgentsJson = "[]",
                CardsJson = "[]",
                DepsJson = "[]",
            });
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = "review",
                Epic = "EPIC-1",
                EpicColor = "blue",
                Workstream = "ws/review-race",
                Base = "main",
                Branch = "ws/review-race",
                Title = "review-race fixture",
                Additions = 1,
                Deletions = 0,
                Files = 1,
                Commits = 1,
                Age = "1m",
                AutoMerge = false, // don't let the idle case try to merge
                HumanGate = false,
                Conflicts = false,
                ReviewJson = null, // UNSTAMPED — the observable
                Provider = "github",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        // Drive the review phase directly (bypasses DriveImplementAsync).
        using (var scope = f.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();

            var spawn = new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = "review-race regression",
                Phase = StorySpawnPhases.Review,
                RepoId = ForgeSeed.DefaultRepoId,
                BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 1,
            };
            var story = new WorkItemRow
            {
                Id = storyId,
                Provider = "local",
                State = "active",
                Title = "review-race regression",
                Type = "feature",
                Track = "design",
                IterationRef = string.Empty,
                AreaPath = string.Empty,
                DescriptionMd = string.Empty,
                TagsJson = "[]",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await orch.DriveReviewAsync(control, db, hub, spawn, story, repo: null, ct: default);
        }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var pr = await db.Prs.AsNoTracking().FirstAsync(p => p.Id == prId);
            if (expectVerdictStamped)
            {
                Assert.False(string.IsNullOrEmpty(pr.ReviewJson),
                    "Control (idle): DriveReviewAsync must process the ws and stamp Review.verdict.");
                Assert.Contains("pending", pr.ReviewJson!);
            }
            else
            {
                Assert.True(string.IsNullOrEmpty(pr.ReviewJson),
                    "Guard (impl invocation live): DriveReviewAsync must DEFER the ws — Review.verdict stays " +
                    "unstamped (no dispatch, no Phase-74 concurrent-dispatch stall).");
            }
        }
    }

    private sealed class GuardFactory : ForgeWebApplicationFactory
    {
        private readonly IAgentRunner runner;

        public GuardFactory(IAgentRunner runner) => this.runner = runner;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IAgentRunner>();
                s.AddSingleton(this.runner);
            });
        }
    }

    /// <summary>Fake runner — only <see cref="IsTaskActive"/> matters here.</summary>
    private sealed class FakeRunner : IAgentRunner
    {
        private readonly HashSet<string> active;

        public FakeRunner(IEnumerable<string> activeTaskIds)
            => this.active = new HashSet<string>(activeTaskIds, StringComparer.Ordinal);

        public bool IsTaskActive(string taskId) => this.active.Contains(taskId);

        public Task<AgentInvocation> StartAsync(string taskId, string agentId, string prompt, AgentContext context, CancellationToken ct)
            => throw new InvalidOperationException(
                $"FakeRunner.StartAsync should not be called (taskId={taskId}).");

        public Task CancelAsync(string invocationId, CancellationToken ct) => Task.CompletedTask;

        public Task<int> CancelByTaskAsync(string taskId, CancellationToken ct) => Task.FromResult(0);

        public Task PauseAsync(string agentId, CancellationToken ct) => Task.CompletedTask;

        public Task ResumeAsync(string agentId, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentState> GetStateAsync(string agentId, CancellationToken ct) => Task.FromResult(AgentState.Idle);
    }
}
