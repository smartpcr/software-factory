// -----------------------------------------------------------------------
// <copyright file="CopilotMemoryStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Agents;
using Forge.Api.Tests.Infrastructure;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class CopilotMemoryStoreTests
{
    [Fact]
    public void PrepareCopilotMemoryStore_CreatesWorktreeLocalForgeMemoryDirectory()
    {
        var worktree = Path.Combine(Path.GetTempPath(), "forge-memory-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var context = new AgentContext(
                CrewId: "crew-eng",
                Model: "gpt-5.5",
                SoulMd: string.Empty,
                WorktreePath: worktree,
                AllowedTools: Array.Empty<string>(),
                Memory: new AgentMemoryHandle(SessionId: Guid.NewGuid().ToString("D")));

            var path = AgentRunnerDispatcher.PrepareCopilotMemoryStore(context);

            Assert.Equal(Path.Combine(worktree, ".forge", "memory"), path);
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EnsureSessionIdAsync_CreatesThenReusesProviderSessionId()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var first = await store.EnsureSessionIdAsync(
            "wi-memory-test", "agent-memory-test", AgentMemoryRoles.Generator,
            backend: "copilot", invokeMode: "sdk");
        var second = await store.EnsureSessionIdAsync(
            "wi-memory-test", "agent-memory-test", AgentMemoryRoles.Generator,
            backend: "copilot", invokeMode: "sdk");
        var handle = await store.GetHandleAsync(
            "wi-memory-test", "agent-memory-test", AgentMemoryRoles.Generator);

        Assert.True(Guid.TryParse(first.SessionId, out _));
        Assert.False(first.ShouldResume);
        Assert.True(second.ShouldResume);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(first.SessionId, handle?.SessionId);
        Assert.True(handle?.ResumeSession);
        Assert.Contains(db.AgentMemory, m => m.SessionId == first.SessionId);
    }

    [Fact]
    public async Task EnsureSessionIdAsync_MissingWorktreeMemoryStore_RotatesToFreshSession()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
        var memoryStorePath = Path.Combine(Path.GetTempPath(), "forge-memory-missing-" + Guid.NewGuid().ToString("N"), ".forge", "memory");

        var first = await store.EnsureSessionIdAsync(
            "wi-closed-worktree", "agent-memory-test", AgentMemoryRoles.Generator,
            backend: "copilot", invokeMode: "sdk");
        var second = await store.EnsureSessionIdAsync(
            "wi-closed-worktree", "agent-memory-test", AgentMemoryRoles.Generator,
            backend: "copilot", invokeMode: "sdk",
            memoryStorePath: memoryStorePath);

        Assert.False(Directory.Exists(memoryStorePath));
        Assert.False(second.ShouldResume);
        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task WorkstreamMemoryContextStore_WritesConciseContextFile()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<WorkstreamMemoryContextStore>();
        var worktree = Path.Combine(Path.GetTempPath(), "forge-memory-context-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);

        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-context-test",
                Title = "Implement context memory",
                State = "active",
                ExecutionStatus = ExecutionStatuses.Queued,
                Points = 3,
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-context-test",
                CrewId = "crew-eng",
                GeneratorAgentId = "G1",
                EvaluatorAgentId = "E1",
                Iter = 1,
                Verdict = "iterate",
                Score = 82,
                GeneratorSummary = "Changed the memory writer and left follow-up cleanup.",
                Notes = "- [ ] Preserve memory while worktree is open.\n- [ ] Rotate when store is gone.",
                CommitSha = "abcdef1234567890",
                CommitMessage = "forge iter memory",
                FilesChanged = 2,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var path = await store.WriteAsync("wi-context-test", worktree);
            var content = await File.ReadAllTextAsync(path);

            Assert.Equal(Path.Combine(worktree, ".forge", "memory", WorkstreamMemoryContextStore.FileName), path);
            Assert.Contains("Implement context memory", content);
            Assert.Contains("Changed the memory writer", content);
            Assert.Contains("Preserve memory", content);
            Assert.DoesNotContain("## Full transcript", content);
        }
        finally
        {
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
            }
        }
    }

    [Fact]
    public void MaybePrependWorkstreamMemoryPointer_PointsAgentAtContextFile()
    {
        var worktree = Path.Combine(Path.GetTempPath(), "forge-memory-pointer-test-" + Guid.NewGuid().ToString("N"));
        var memoryDir = Path.Combine(worktree, ".forge", "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(memoryDir, WorkstreamMemoryContextStore.FileName), "context");

        try
        {
            var context = new AgentContext(
                CrewId: "crew-eng",
                Model: "gpt-5.5",
                SoulMd: string.Empty,
                WorktreePath: worktree,
                AllowedTools: Array.Empty<string>(),
                Memory: new AgentMemoryHandle(SessionId: Guid.NewGuid().ToString("D")));

            var prompt = AgentRunnerDispatcher.MaybePrependWorkstreamMemoryPointer("do work", context);

            Assert.StartsWith("## Forge workstream memory", prompt);
            Assert.Contains(".forge/memory/workstream-context.md", prompt);
            Assert.EndsWith("do work", prompt);
        }
        finally
        {
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
            }
        }
    }
}
