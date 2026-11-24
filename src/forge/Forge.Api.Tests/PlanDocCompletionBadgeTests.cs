// -----------------------------------------------------------------------
// <copyright file="PlanDocCompletionBadgeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Agents;
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
/// Regression for plan-doc cards showing "failed" after the doc passed and
/// merged (observed on harness:DSH, all four plan docs). Two causes:
/// <list type="bullet">
///   <item>Nothing marked a passing plan doc Completed, so its Live badge was
///         later healed to Failed by the live-badge sweep.</item>
///   <item>The sweep healed a doc to Failed mid-run when the agent was silent
///         for over 60 seconds, although its invocation was still running.</item>
/// </list>
/// </summary>
public class PlanDocCompletionBadgeTests
{
    [Fact]
    public async Task Sweep_leaves_a_running_but_silent_plan_doc_live()
    {
        const string id = "plan-doc:spawn-badge-active:architecture.md";
        using var f = new RunnerFactory(new FakeRunner(new[] { id }));
        using (f.CreateClient()) { /* boot host + migrations */ }
        await SeedAsync(f, id, ExecutionStatuses.Live);

        await f.Services.GetRequiredService<CrewQueueWorker>().SweepLiveBadgeReconciliationAsync(default);

        Assert.Equal(ExecutionStatuses.Live, await StatusAsync(f, id));
    }

    [Fact]
    public async Task Sweep_still_heals_a_stuck_badge_when_nothing_is_running()
    {
        const string id = "plan-doc:spawn-badge-idle:architecture.md";
        using var f = new RunnerFactory(new FakeRunner(Array.Empty<string>()));
        using (f.CreateClient()) { /* boot host + migrations */ }
        await SeedAsync(f, id, ExecutionStatuses.Live);

        await f.Services.GetRequiredService<CrewQueueWorker>().SweepLiveBadgeReconciliationAsync(default);

        Assert.Equal(ExecutionStatuses.Failed, await StatusAsync(f, id));
    }

    [Fact]
    public async Task Sweep_never_requeues_a_story_workstream_the_orchestrator_owns()
    {
        // Observed on harness:DSH-M2 stage 1.1: after a stalled iteration the sweep
        // queued the workstream to crew-eng, so E4 ran it beside the orchestrator's
        // E9/LC pair, in the main clone instead of the stage worktree.
        const string id = "ws-badge-story-owned";
        using var f = new RunnerFactory(new FakeRunner(Array.Empty<string>()));
        using (f.CreateClient()) { /* boot host + migrations */ }
        await SeedAsync(f, id, ExecutionStatuses.Live);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Workstreams.Add(new WorkstreamRow { Id = id, Name = id, Branch = "ws/b", Base = "main", Epic = "b", Status = "stalled", StorySpawnId = "spawn-badge-story" });
            db.Deliverables.Add(new DeliverableRow { WorkItemId = id, CrewId = "crew-eng", Iter = 1, Verdict = "stalled" });
            await db.SaveChangesAsync();
        }

        await f.Services.GetRequiredService<CrewQueueWorker>().SweepLiveBadgeReconciliationAsync(default);

        Assert.Equal(ExecutionStatuses.Live, await StatusAsync(f, id));
        Assert.Equal(0, await QueueRowsAsync(f, id));
    }

    [Fact]
    public async Task Sweep_heals_a_stalled_plan_doc_without_requeueing_it()
    {
        const string id = "plan-doc:spawn-badge-stalled:architecture.md";
        using var f = new RunnerFactory(new FakeRunner(Array.Empty<string>()));
        using (f.CreateClient()) { /* boot host + migrations */ }
        await SeedAsync(f, id, ExecutionStatuses.Live);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Deliverables.Add(new DeliverableRow { WorkItemId = id, CrewId = "crew-arch", Iter = 1, Verdict = "stalled" });
            await db.SaveChangesAsync();
        }

        await f.Services.GetRequiredService<CrewQueueWorker>().SweepLiveBadgeReconciliationAsync(default);

        Assert.Equal(ExecutionStatuses.Failed, await StatusAsync(f, id));
        Assert.Equal(0, await QueueRowsAsync(f, id));
    }

    [Fact]
    public async Task Passing_plan_doc_is_marked_completed_and_other_items_are_untouched()
    {
        const string live = "plan-doc:spawn-badge-pass:tech-spec.md";
        const string staleFailed = "plan-doc:spawn-badge-pass:architecture.md";
        const string workstream = "ws-badge-pass";
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot host + migrations */ }
        await SeedAsync(f, live, ExecutionStatuses.Live);
        await SeedAsync(f, staleFailed, ExecutionStatuses.Failed);
        await SeedAsync(f, workstream, ExecutionStatuses.Failed);

        var scopes = f.Services.GetRequiredService<IServiceScopeFactory>();
        foreach (var id in new[] { live, staleFailed, workstream })
        {
            await CrewStoryArtifactStrategy.MarkPlanDocCompletedAsync(scopes, id, null, default);
        }

        Assert.Equal(ExecutionStatuses.Completed, await StatusAsync(f, live));
        Assert.Equal(ExecutionStatuses.Completed, await StatusAsync(f, staleFailed));
        Assert.Equal(ExecutionStatuses.Failed, await StatusAsync(f, workstream));
    }

    [Fact]
    public async Task Merging_a_plan_doc_pr_closes_only_that_doc()
    {
        // The Plan & Design board maps State to a column (active → Build,
        // closed → Done); a merged plan doc must leave Build.
        const string spawnId = "spawn-badge-merge";
        const string merged = $"plan-doc:{spawnId}:architecture.md";
        const string pending = $"plan-doc:{spawnId}:tech-spec.md";
        using var f = new RunnerFactory(new FakeRunner(Array.Empty<string>()));
        using (f.CreateClient()) { /* boot host + migrations */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = "WI-badge-merge",
                StoryTitle = "Badge merge",
                Phase = StorySpawnPhases.Plan,
                BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                PlanDocPrsJson = "[{\"key\":\"architecture\",\"branch\":\"story/b/plan-architecture\",\"prId\":\"PR badge#1\"},"
                    + "{\"key\":\"tech-spec\",\"branch\":\"story/b/plan-tech-spec\",\"prId\":\"PR badge#2\"}]",
            });
            await db.SaveChangesAsync();
        }

        await SeedAsync(f, merged, ExecutionStatuses.Failed);
        await SeedAsync(f, pending, ExecutionStatuses.Live);

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await PlanPrMergeReactor.OnPlanPrMergedAsync(sp.GetRequiredService<ControlDbContext>(), sp.GetRequiredService<ProductDbContext>(),
                sp.GetRequiredService<IAgentRunner>(), hub: null, "PR badge#1", log: null, default);
        }

        Assert.Equal(("closed", ExecutionStatuses.Completed), await StateAsync(f, merged));
        Assert.Equal(("active", ExecutionStatuses.Live), await StateAsync(f, pending));
    }

    private static async Task SeedAsync(ForgeWebApplicationFactory f, string id, string status)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.WorkItems.Add(new WorkItemRow
        {
            Id = id,
            Type = "task",
            Track = "dev",
            State = "active",
            Title = id,
            ExecutionStatus = status,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> QueueRowsAsync(ForgeWebApplicationFactory f, string id)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.TaskQueue.AsNoTracking().CountAsync(q => q.WorkItemId == id);
    }

    private static async Task<string?> StatusAsync(ForgeWebApplicationFactory f, string id)
        => (await StateAsync(f, id)).Status;

    private static async Task<(string? State, string? Status)> StateAsync(ForgeWebApplicationFactory f, string id)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await db.WorkItems.AsNoTracking().Where(w => w.Id == id).Select(w => new { w.State, w.ExecutionStatus }).SingleAsync();
        return (row.State, row.ExecutionStatus);
    }

    private sealed class RunnerFactory : ForgeWebApplicationFactory
    {
        private readonly IAgentRunner runner;

        public RunnerFactory(IAgentRunner runner) => this.runner = runner;

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
            => throw new InvalidOperationException($"FakeRunner.StartAsync should not be called (taskId={taskId}).");

        public Task CancelAsync(string invocationId, CancellationToken ct) => Task.CompletedTask;

        public Task<int> CancelByTaskAsync(string taskId, CancellationToken ct) => Task.FromResult(0);

        public Task PauseAsync(string agentId, CancellationToken ct) => Task.CompletedTask;

        public Task ResumeAsync(string agentId, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentState> GetStateAsync(string agentId, CancellationToken ct) => Task.FromResult(AgentState.Idle);
    }
}