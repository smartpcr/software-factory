// -----------------------------------------------------------------------
// <copyright file="Phase74ConcurrentDispatchGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Agents;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Phase 74 Fix 3 — verifies the concurrent-dispatch discriminator on
/// <see cref="AgentRunnerDispatcher.StartAsync"/>. Two concurrent
/// dispatches on the same taskId must throw; the throw must be
/// classifiable as <see cref="GeneratorFailureKinds.ConcurrentDispatch"/>;
/// sequential dispatch must succeed; different taskIds must run
/// concurrently.
/// </summary>
public class Phase74ConcurrentDispatchGuardTests
{
    [Fact]
    public async Task SentinelString_IsExposedAsPublicConst()
    {
        Assert.Equal(
            "Concurrent dispatch refused",
            AgentRunnerDispatcher.ConcurrentDispatchRefusalSentinel);
        await Task.CompletedTask;
    }

    [Fact]
    public void GeneratorFailureKinds_Classify_MapsConcurrentDispatch()
    {
        var msg = $"{AgentRunnerDispatcher.ConcurrentDispatchRefusalSentinel}: taskId='ws-x' already has live invocation='inv-y'.";
        var kind = GeneratorFailureKinds.Classify(msg);
        Assert.Equal(GeneratorFailureKinds.ConcurrentDispatch, kind);
    }

    [Fact]
    public void GeneratorFailureKinds_Classify_PrefersConcurrentDispatch_OverMaxTurnsSubstring()
    {
        // The discriminator message MUST be classified as ConcurrentDispatch
        // even if its body happens to mention 'Max turns' (defense against
        // future message rewording).
        var msg = AgentRunnerDispatcher.ConcurrentDispatchRefusalSentinel
            + ": Max turns context — taskId='ws-x' already has live invocation='inv-y'.";
        Assert.Equal(GeneratorFailureKinds.ConcurrentDispatch, GeneratorFailureKinds.Classify(msg));
    }

    [Fact]
    public async Task SecondStartAsync_SameTaskId_ThrowsWithSentinel()
    {
        var dispatcher = BuildDispatcher();
        var ctx = new AgentContext(
            CrewId: "crew-x", Model: "local-stub", SoulMd: "",
            WorktreePath: "", AllowedTools: Array.Empty<string>());

        var first = await dispatcher.StartAsync(
            taskId: "ws-shared",
            agentId: "agent-g",
            prompt: "p1",
            context: ctx,
            ct: CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.StartAsync(
                taskId: "ws-shared",
                agentId: "agent-g",
                prompt: "p2",
                context: ctx,
                ct: CancellationToken.None));
        Assert.Contains(
            AgentRunnerDispatcher.ConcurrentDispatchRefusalSentinel,
            ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ws-shared", ex.Message, StringComparison.Ordinal);

        // Drain the first invocation so its WrapEvents finally clears
        // the discriminator slot — this is needed for clean test isolation.
        await foreach (var _ in first.Events) { }
    }

    [Fact]
    public async Task DifferentTaskIds_RunConcurrently()
    {
        var dispatcher = BuildDispatcher();
        var ctx = new AgentContext(
            CrewId: "crew-x", Model: "local-stub", SoulMd: "",
            WorktreePath: "", AllowedTools: Array.Empty<string>());

        var a = await dispatcher.StartAsync("ws-A", "agent-g", "p1", ctx, CancellationToken.None);
        var b = await dispatcher.StartAsync("ws-B", "agent-g", "p2", ctx, CancellationToken.None);

        // Both started cleanly — the second StartAsync DIDN'T throw.
        Assert.NotEqual(a.InvocationId, b.InvocationId);

        await foreach (var _ in a.Events) { }
        await foreach (var _ in b.Events) { }
    }

    [Fact]
    public async Task SequentialStartAsync_SameTaskId_BothSucceedAfterDrain()
    {
        var dispatcher = BuildDispatcher();
        var ctx = new AgentContext(
            CrewId: "crew-x", Model: "local-stub", SoulMd: "",
            WorktreePath: "", AllowedTools: Array.Empty<string>());

        // First call.
        var first = await dispatcher.StartAsync("ws-seq", "agent-g", "p1", ctx, CancellationToken.None);
        await foreach (var _ in first.Events) { }

        // After drain, the discriminator slot is free; the second call must succeed.
        var second = await dispatcher.StartAsync("ws-seq", "agent-g", "p2", ctx, CancellationToken.None);
        await foreach (var _ in second.Events) { }
    }

    [Fact]
    public async Task CancelByTaskAsync_FreesDiscriminatorSlot()
    {
        var dispatcher = BuildDispatcher();
        var ctx = new AgentContext(
            CrewId: "crew-x", Model: "local-stub", SoulMd: "",
            WorktreePath: "", AllowedTools: Array.Empty<string>());

        var first = await dispatcher.StartAsync("ws-cancel", "agent-g", "p", ctx, CancellationToken.None);

        // Cancel without draining — discriminator slot must clear so a
        // new dispatch can immediately retry. Mirrors the operator
        // Restart / Retry flows.
        var cancelled = await dispatcher.CancelByTaskAsync("ws-cancel", CancellationToken.None);
        Assert.True(cancelled > 0);

        // Now a fresh StartAsync must succeed without throwing.
        var second = await dispatcher.StartAsync("ws-cancel", "agent-g", "p2", ctx, CancellationToken.None);
        await foreach (var _ in second.Events) { }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static AgentRunnerDispatcher BuildDispatcher()
    {
        var options = Options.Create(new AgentRunnerOptions());
        return new AgentRunnerDispatcher(
            http: new EmptyHttpClientFactory(),
            models: new StubModelRegistry(),
            options: options,
            copilotServer: null!,
            authServices: Array.Empty<IAuthService>(),
            copilotPool: new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance),
            log: NullLogger<AgentRunnerDispatcher>.Instance);
    }

    private sealed class EmptyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubModelRegistry : IModelRegistry
    {
        public IReadOnlyList<ModelInfo> All => Array.Empty<ModelInfo>();
        public ModelInfo? Find(string id) => null;
    }
}
