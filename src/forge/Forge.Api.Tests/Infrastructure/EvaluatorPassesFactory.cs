// -----------------------------------------------------------------------
// <copyright file="EvaluatorPassesFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using System.Runtime.CompilerServices;
using Forge.Api.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Test factory for delivery-pipeline tests that need a work item to pass
/// review. Unreviewed work no longer passes on its own, and offline tests only
/// have the local-stub model, which echoes its prompt. This factory keeps the
/// real runner for generator turns and answers every evaluator turn
/// (<see cref="AgentContext.IsEvaluator"/>) with a passing score. A crew under
/// test still needs an independent evaluator: a second member and an
/// <c>EvaluatorModel</c> other than <c>Model</c> (see <see cref="EvaluatorModel"/>).
/// </summary>
public class EvaluatorPassesFactory : ForgeWebApplicationFactory
{
    /// <summary>Evaluator model id for test crews; any value other than the generator's model.</summary>
    public const string EvaluatorModel = "local-stub-evaluator";

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentRunner>();
            services.AddSingleton<AgentRunnerDispatcher>();
            services.AddSingleton<IAgentRunner>(sp => new PassingEvaluatorRunner(sp.GetRequiredService<AgentRunnerDispatcher>()));
        });
    }

    private sealed class PassingEvaluatorRunner : IAgentRunner
    {
        private readonly IAgentRunner inner;

        public PassingEvaluatorRunner(IAgentRunner inner) => this.inner = inner;

        public Task<AgentInvocation> StartAsync(string taskId, string agentId, string prompt, AgentContext context, CancellationToken ct)
        {
            if (!context.IsEvaluator)
            {
                return this.inner.StartAsync(taskId, agentId, prompt, context, ct);
            }

            var invocationId = Guid.NewGuid().ToString("N");
            return Task.FromResult(new AgentInvocation(invocationId, agentId, context.CrewId, taskId, context.Model, Pass(ct)));
        }

        public Task CancelAsync(string invocationId, CancellationToken ct) => this.inner.CancelAsync(invocationId, ct);

        public Task<int> CancelByTaskAsync(string taskId, CancellationToken ct) => this.inner.CancelByTaskAsync(taskId, ct);

        public bool IsTaskActive(string taskId) => this.inner.IsTaskActive(taskId);

        public Task PauseAsync(string agentId, CancellationToken ct) => this.inner.PauseAsync(agentId, ct);

        public Task ResumeAsync(string agentId, CancellationToken ct) => this.inner.ResumeAsync(agentId, ct);

        public Task<AgentState> GetStateAsync(string agentId, CancellationToken ct) => this.inner.GetStateAsync(agentId, ct);

        private static async IAsyncEnumerable<AgentEvent> Pass([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new AgentEvent(
                AgentEventKind.Token,
                DateTimeOffset.UtcNow,
                Text: "```json\n{ \"score\": 95, \"notes\": \"Reviewed by the test evaluator.\" }\n```");
            yield return new AgentEvent(AgentEventKind.Finished, DateTimeOffset.UtcNow, InputTokens: 1, OutputTokens: 1, CostUsd: 0m);
        }
    }
}