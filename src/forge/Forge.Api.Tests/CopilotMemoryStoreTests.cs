// -----------------------------------------------------------------------
// <copyright file="CopilotMemoryStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void ResolveCopilotMemoryStorePath_WorktreeOverloadDoesNotRequireLoadedMemoryHandle()
    {
        var worktree = Path.Combine(Path.GetTempPath(), "forge-memory-path-test-" + Guid.NewGuid().ToString("N"));
        var context = new AgentContext(
            CrewId: "crew-eng",
            Model: "gpt-5.5",
            SoulMd: string.Empty,
            WorktreePath: worktree,
            AllowedTools: Array.Empty<string>());

        Assert.Equal(
            Path.Combine(worktree, ".forge", "memory"),
            AgentRunnerDispatcher.ResolveCopilotMemoryStorePath(worktree));
        Assert.Null(AgentRunnerDispatcher.ResolveCopilotMemoryStorePath(context));
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
    public async Task WorkstreamMemoryContextStore_WritesRequiredContextAndPointers()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<WorkstreamMemoryContextStore>();
        var suffix = Guid.NewGuid().ToString("N");
        var workItemId = "wi-context-test-" + suffix;
        var spawnId = "spawn-memory-story-" + suffix;
        var prId = "PR-" + suffix[..8];
        var worktree = Path.Combine(Path.GetTempPath(), "forge-memory-context-test-" + suffix);
        Directory.CreateDirectory(worktree);

        try
        {
            var notesDir = Path.Combine(worktree, ".forge", "notes");
            Directory.CreateDirectory(notesDir);
            await File.WriteAllTextAsync(
                Path.Combine(notesDir, "iter-2.md"),
                "Remember to inspect session state before changing runner wiring.");

            var promptRow = new IterationPromptRow
            {
                WorkItemId = workItemId,
                Iter = 2,
                Role = "generator",
                Attempt = 1,
                AgentId = "G1",
                CrewId = "crew-eng",
                UserPrompt = "FULL PROMPT BODY SHOULD NOT APPEAR IN MEMORY",
                Model = "gpt-5.5",
                Backend = "copilot",
                InvokeMode = "sdk",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.WorkItems.Add(new WorkItemRow
            {
                Id = workItemId,
                Title = "Implement context memory",
                State = "active",
                ExecutionStatus = ExecutionStatuses.Queued,
                Points = 3,
                TrialCount = 4,
                PairAttemptCount = 2,
                PlanAnchor = "phase-2/stage-memory",
                FailureReason = "Last generator timed out while reading stale context.",
            });
            db.Workstreams.AddRange(
                new WorkstreamRow
                {
                    Id = workItemId,
                    Name = "Stage memory implementation",
                    Branch = "feature/memory",
                    Base = "main",
                    Status = "stalled",
                    Category = "impl",
                    Summary = "Preserve concise workstream context across agent invocations.",
                    StallReason = "Build gate failed after session resume.",
                    StorySpawnId = spawnId,
                    PlanAnchor = "phase-2/stage-memory",
                    DepsJson = JsonSerializer.Serialize(new[] { "Dependency A", "Dependency B" }),
                    PrsJson = JsonSerializer.Serialize(new[] { prId }),
                },
                new WorkstreamRow
                {
                    Id = "dep-a-" + suffix,
                    Name = "Dependency A",
                    Branch = "feature/dep-a",
                    Base = "main",
                    Status = "complete",
                    Category = "impl",
                    StorySpawnId = spawnId,
                },
                new WorkstreamRow
                {
                    Id = "dep-b-" + suffix,
                    Name = "Dependency B",
                    Branch = "feature/dep-b",
                    Base = "main",
                    Status = "active",
                    Category = "impl",
                    StorySpawnId = spawnId,
                });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = "story-42",
                StoryTitle = "Copilot memory continuity",
                Phase = StorySpawnPhases.Implement,
                BaseBranch = "feature/base",
                LastError = "Last orchestrator drive saw a merge conflict.",
                DecisionPinsJson = JsonSerializer.Serialize(new[] { new { text = "Memory stays worktree-local." } }),
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = workItemId,
                CrewId = "crew-eng",
                GeneratorAgentId = "G1",
                EvaluatorAgentId = "E1",
                Iter = 2,
                PairAttempt = 2,
                Verdict = "iterate",
                Score = 82,
                GeneratorSummary = "Changed the memory writer and left follow-up cleanup.",
                Notes = "- [ ] Preserve memory while worktree is open.\n- [ ] Rotate when store is gone.",
                CommitSha = "abcdef1234567890",
                CommitMessage = "forge iter memory",
                FilesAdded = 1,
                FilesModified = 2,
                FilesDeleted = 0,
                FilesChanged = 3,
                FailureKind = "generator-timeout",
                FailureReason = "Copilot SDK hard timeout after stale session resume.",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = workItemId,
                ExternalId = "q-memory",
                Iter = 2,
                Text = "Which cache should carry Copilot session state?",
                Answer = "Use the worktree-local .forge memory store only.",
            });
            db.TaskTranscriptEvents.AddRange(
                new TaskTranscriptEventRow
                {
                    WorkItemId = workItemId,
                    Kind = "task.build-gate.failed",
                    PayloadJson = JsonSerializer.Serialize(new { stage = "test", command = "dotnet test", exitCode = 1, error = "Compiler blew up" }),
                    At = DateTimeOffset.UtcNow,
                },
                new TaskTranscriptEventRow
                {
                    WorkItemId = workItemId,
                    Kind = "task.token",
                    PayloadJson = JsonSerializer.Serialize(new { text = "RAW TRANSCRIPT TOKEN SHOULD NOT APPEAR" }),
                    At = DateTimeOffset.UtcNow,
                });
            db.PrComments.Add(new PrCommentRow
            {
                PrId = prId,
                ProviderCommentId = "comment-" + suffix,
                ThreadId = "thread-" + suffix,
                Body = "Address review memory handoff before approval.",
                FilePath = "src/Memory.cs",
                Line = 42,
                State = "active",
                RequiresAttention = true,
            });
            db.IterationPrompts.Add(promptRow);
            await db.SaveChangesAsync();

            var path = await store.WriteAsync(workItemId, worktree);
            var content = await File.ReadAllTextAsync(path);

            Assert.Equal(Path.Combine(worktree, ".forge", "memory", WorkstreamMemoryContextStore.FileName), path);
            Assert.Contains("Implement context memory", content);
            Assert.Contains("State/execution: `active` / `queued`; pair attempt 2; trial 4; points 3", content);
            Assert.Contains("Stage memory implementation", content);
            Assert.Contains("Branch/base: `feature/memory` -> `main`", content);
            Assert.Contains("Story: `story-42`", content);
            Assert.Contains("Decision pins JSON pointer", content);
            Assert.Contains("Dependency A — status `complete`, branch `feature/dep-a`", content);
            Assert.Contains("Dependency B — status `active`, branch `feature/dep-b`", content);
            Assert.Contains("Iteration 2 — verdict `iterate`, score 82, pair 2", content);
            Assert.Contains("`abcdef123456` forge iter memory", content);
            Assert.Contains("Files changed: 3 (added 1, modified 2, deleted 0)", content);
            Assert.Contains("Changed the memory writer", content);
            Assert.Contains("Preserve memory while worktree is open", content);
            Assert.Contains("Copilot SDK hard timeout", content);
            Assert.Contains("Which cache should carry Copilot session state?", content);
            Assert.Contains("Use the worktree-local .forge memory store only.", content);
            Assert.Contains("`task.build-gate.failed`", content);
            Assert.Contains("command=dotnet test", content);
            Assert.Contains("PR `" + prId + "` `src/Memory.cs:42` state `active` attention=True", content);
            Assert.Contains("Address review memory handoff", content);
            Assert.Contains($"iteration_prompt id {promptRow.Id}", content);
            Assert.Contains("Remember to inspect session state", content);
            Assert.DoesNotContain("FULL PROMPT BODY SHOULD NOT APPEAR", content);
            Assert.DoesNotContain("RAW TRANSCRIPT TOKEN SHOULD NOT APPEAR", content);
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

    [Fact]
    public async Task StartAsync_WithMemoryContext_PassesPointerToEffectiveInvocationPrompt()
    {
        var worktree = Path.Combine(Path.GetTempPath(), "forge-memory-runner-test-" + Guid.NewGuid().ToString("N"));
        var memoryDir = Path.Combine(worktree, ".forge", "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(memoryDir, WorkstreamMemoryContextStore.FileName), "context");

        try
        {
            var runner = MakeRunner();
            var context = new AgentContext(
                CrewId: "crew-eng",
                Model: "local-stub",
                SoulMd: string.Empty,
                WorktreePath: worktree,
                AllowedTools: Array.Empty<string>(),
                Backend: AgentBackend.Copilot,
                InvokeMode: AgentInvokeMode.Sdk,
                Memory: new AgentMemoryHandle(SessionId: "session-memory-test", ResumeSession: true));

            var invocation = await runner.StartAsync("wi-memory-runner", "G1", "do work", context, CancellationToken.None);
            var text = new StringBuilder();
            AgentEvent? finished = null;
            await foreach (var evt in invocation.Events)
            {
                if (evt.Kind == AgentEventKind.Token && evt.Text is not null)
                {
                    text.Append(evt.Text);
                }

                if (evt.Kind == AgentEventKind.Finished)
                {
                    finished = evt;
                }
            }

            var effectivePrompt = text.ToString();
            Assert.StartsWith("## Forge workstream memory", effectivePrompt);
            Assert.Contains("read `.forge/memory/workstream-context.md`", effectivePrompt);
            Assert.EndsWith("do work", effectivePrompt);
            Assert.Equal("session-memory-test", finished?.SessionId);
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
    public void CopilotCliInvocationMemoryConfiguration_SetsHomeAndSessionId()
    {
        var worktree = Path.Combine(Path.GetTempPath(), "forge-memory-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var context = new AgentContext(
                CrewId: "crew-eng",
                Model: "gpt-5.5",
                SoulMd: string.Empty,
                WorktreePath: worktree,
                AllowedTools: Array.Empty<string>(),
                Backend: AgentBackend.Copilot,
                InvokeMode: AgentInvokeMode.Cli,
                Memory: new AgentMemoryHandle(SessionId: "session-cli-test", ResumeSession: true));
            var psi = new ProcessStartInfo
            {
                FileName = "copilot",
                UseShellExecute = false,
            };

            var memoryPath = AgentRunnerDispatcher.ApplyCopilotCliMemory(psi, context);

            Assert.Equal(Path.Combine(worktree, ".forge", "memory"), memoryPath);
            Assert.True(Directory.Exists(memoryPath));
            Assert.Equal(memoryPath, psi.Environment["COPILOT_HOME"]);
            Assert.Contains("--session-id", psi.ArgumentList);
            Assert.Contains("session-cli-test", psi.ArgumentList);
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
    public void CopilotSdkInvocationMemoryConfiguration_SetsSessionConfigAndResumeConfig()
    {
        var worktree = Path.Combine(Path.GetTempPath(), "forge-memory-sdk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var context = new AgentContext(
                CrewId: "crew-eng",
                Model: "gpt-5.5",
                SoulMd: "soul prompt",
                WorktreePath: worktree,
                AllowedTools: Array.Empty<string>(),
                Backend: AgentBackend.Copilot,
                InvokeMode: AgentInvokeMode.Sdk,
                Memory: new AgentMemoryHandle(SessionId: "session-sdk-test", ResumeSession: true));

            var (sessionConfig, resumeConfig, memorySessionId, shouldResumeSession) =
                AgentRunnerDispatcher.CreateCopilotSessionConfigs(context, "gh-token");
            var memoryPath = Path.Combine(worktree, ".forge", "memory");

            Assert.Equal("session-sdk-test", memorySessionId);
            Assert.True(shouldResumeSession);
            Assert.True(Directory.Exists(memoryPath));
            Assert.Equal("session-sdk-test", sessionConfig.SessionId);
            Assert.Equal("gpt-5.5", sessionConfig.Model);
            Assert.Equal(memoryPath, sessionConfig.ConfigDirectory);
            Assert.Equal(worktree, sessionConfig.WorkingDirectory);
            Assert.Equal("gh-token", sessionConfig.GitHubToken);
            Assert.Equal("soul prompt", sessionConfig.SystemMessage?.Content);
            Assert.Equal("gpt-5.5", resumeConfig.Model);
            Assert.Equal(memoryPath, resumeConfig.ConfigDirectory);
            Assert.Equal(worktree, resumeConfig.WorkingDirectory);
            Assert.Equal("gh-token", resumeConfig.GitHubToken);
        }
        finally
        {
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
            }
        }
    }

    private static AgentRunnerDispatcher MakeRunner()
    {
        var http = new StubHttpFactory();
        var registry = new TestModelRegistry();
        var opts = Options.Create(new AgentRunnerOptions());
        var log = NullLogger<AgentRunnerDispatcher>.Instance;
        var copilotServer = new CopilotServerManager(
            Options.Create(new AgentRunnerOptions()),
            NullLogger<CopilotServerManager>.Instance,
            Array.Empty<Forge.Api.Auth.IAuthService>());
        var copilotPool = new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance);
        return new AgentRunnerDispatcher(http, registry, opts, copilotServer, Array.Empty<Forge.Api.Auth.IAuthService>(), copilotPool, log);
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TestModelRegistry : IModelRegistry
    {
        public IReadOnlyList<ModelInfo> All { get; } = new List<ModelInfo>
        {
            new("local-stub", "forge", "local", "Local Stub", 8192, 0m, 0m, false, true, "live"),
        };

        public ModelInfo? Find(string id)
            => this.All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        public void ApplyCatalogOverlay(System.Collections.Generic.IReadOnlyDictionary<string, Forge.Api.Agents.ModelCatalogOverlay> overlay, System.Collections.Generic.IReadOnlyList<Forge.Api.Contracts.ModelInfo>? discoveredModels = null, System.Collections.Generic.IReadOnlyDictionary<string, string>? statusOverrides = null) { }

        public string SeedFilePath => string.Empty;

        public bool CatalogEnriched => false;

        public System.Collections.Generic.IReadOnlyCollection<string> LiveCatalogIds => System.Array.Empty<string>();

        public void RecordLiveCatalog(System.Collections.Generic.IReadOnlyCollection<string> liveModelIds) { }

        public string? ValidateAssignable(string? modelId) => null;
    }
}
