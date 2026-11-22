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

    private static async Task<string?> StatusAsync(ForgeWebApplicationFactory f, string id)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.WorkItems.AsNoTracking().Where(w => w.Id == id).Select(w => w.ExecutionStatus).SingleAsync();
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