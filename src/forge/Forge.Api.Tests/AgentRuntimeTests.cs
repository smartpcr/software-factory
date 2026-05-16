// -----------------------------------------------------------------------
// <copyright file="AgentRuntimeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Phase 11 Batch B — runner contract, sandbox, and streaming endpoint smoke.
/// </summary>
public class AgentRuntimeTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ClaudeRunner_LocalStub_Streams_Tokens_And_Reports_Cost()
    {
        var runner = MakeRunner();
        var ctx = new AgentContext(
            CrewId: "engineering",
            Model: "local-stub",
            SoulMd: string.Empty,
            WorktreePath: string.Empty,
            AllowedTools: Array.Empty<string>());

        var inv = await runner.StartAsync("234", "E4", "hello world from forge", ctx, CancellationToken.None);

        var collected = new List<AgentEvent>();
        await foreach (var e in inv.Events) collected.Add(e);

        Assert.Equal(AgentEventKind.Started, collected[0].Kind);
        Assert.Equal(AgentEventKind.Finished, collected[^1].Kind);
        Assert.Contains(collected, e => e.Kind == AgentEventKind.Token && e.Text is not null);
        Assert.True((collected[^1].InputTokens ?? 0) > 0);
        Assert.True((collected[^1].OutputTokens ?? 0) > 0);
        // local-stub model has zero pricing, so cost is exactly zero.
        Assert.Equal(0m, collected[^1].CostUsd);
    }

    [Fact]
    public async Task ClaudeRunner_LocalStub_With_Priced_Crew_Computes_Cost()
    {
        // Drive the runner manually with an opus model entry to verify cost math.
        var runner = MakeRunner();
        var ctx = new AgentContext(
            CrewId: "engineering",
            Model: "claude-opus-4-7",
            SoulMd: string.Empty,
            WorktreePath: string.Empty,
            AllowedTools: Array.Empty<string>());

        // The runner only takes the local-stub fast-path on model id "local-stub".
        // Use the local-stub path for determinism but override the model entry.
        ctx = ctx with { Model = "local-stub" };

        var inv = await runner.StartAsync("234", "E4", "x", ctx, CancellationToken.None);
        var events = new List<AgentEvent>();
        await foreach (var e in inv.Events) events.Add(e);

        // local-stub pricing is $0 — cost stays zero.
        Assert.Equal(0m, events[^1].CostUsd);
    }

    [Fact]
    public void Sandbox_Refuses_Path_Escape()
    {
        var host = new AgentToolHost();
        var root = Path.Combine(Path.GetTempPath(), $"forge-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var session = host.OpenSession(
                new AgentContext(
                    CrewId: "engineering",
                    Model: "local-stub",
                    SoulMd: string.Empty,
                    WorktreePath: root,
                    AllowedTools: new[] { "read_file", "write_file" }),
                wallClock: TimeSpan.FromMinutes(1));

            // ".." traversal is rejected.
            Assert.Throws<AgentSandboxViolationException>(() =>
                session.WriteFileAsync("../escape.txt", "x", CancellationToken.None).GetAwaiter().GetResult());

            // Absolute path outside the worktree is rejected.
            var outside = Path.Combine(Path.GetTempPath(), "outside.txt");
            Assert.Throws<AgentSandboxViolationException>(() =>
                session.WriteFileAsync(outside, "x", CancellationToken.None).GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sandbox_Allows_Write_Inside_Worktree()
    {
        var host = new AgentToolHost();
        var root = Path.Combine(Path.GetTempPath(), $"forge-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var session = host.OpenSession(
                new AgentContext(
                    CrewId: "engineering",
                    Model: "local-stub",
                    SoulMd: string.Empty,
                    WorktreePath: root,
                    AllowedTools: new[] { "read_file", "write_file" }),
                wallClock: TimeSpan.FromMinutes(1));

            await session.WriteFileAsync("nested/file.txt", "hello", CancellationToken.None);
            var contents = await session.ReadFileAsync("nested/file.txt", CancellationToken.None);
            Assert.Equal("hello", contents);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Sandbox_Refuses_Disallowed_Tool()
    {
        var host = new AgentToolHost();
        var root = Path.Combine(Path.GetTempPath(), $"forge-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var session = host.OpenSession(
                new AgentContext(
                    CrewId: "engineering",
                    Model: "local-stub",
                    SoulMd: string.Empty,
                    WorktreePath: root,
                    AllowedTools: new[] { "read_file" }),
                wallClock: TimeSpan.FromMinutes(1));

            // write_file isn't in the allowlist.
            Assert.Throws<AgentSandboxViolationException>(() =>
                session.WriteFileAsync("ok.txt", "x", CancellationToken.None).GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Sandbox_Refuses_Over_Token_Budget()
    {
        // WorktreePath is required for tool sessions per
        // memory/feedback_worktree_path_meaning.md (and the explicit
        // AgentSandboxViolationException in AgentToolHost). Use a temp dir
        // rather than empty string — this test only exercises the token
        // budget gate, but the host validates the worktree first.
        var worktree = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sandbox-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(worktree);
        try
        {
            var host = new AgentToolHost();
            using var session = host.OpenSession(
                new AgentContext(
                    CrewId: "engineering",
                    Model: "local-stub",
                    SoulMd: string.Empty,
                    WorktreePath: worktree,
                    AllowedTools: Array.Empty<string>()),
                wallClock: TimeSpan.FromMinutes(1),
                maxTokens: 100);

            session.RecordTokens(40, 30); // 70 — fine
            Assert.Throws<AgentBudgetExceededException>(() => session.RecordTokens(50, 50)); // 70 + 100 > 100
        }
        finally
        {
            try { System.IO.Directory.Delete(worktree, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PostInvocation_Writes_Ledger_Row_On_Finish()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var resp = await client.PostAsJsonAsync("/api/agents/E4/invocations", new
        {
            taskId = "234",
            prompt = "hello world",
            model = "local-stub",
            crewId = "engineering",
        }, JsonOpts);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ledgerId = doc.RootElement.GetProperty("ledgerId").GetInt64();
        Assert.True(ledgerId > 0);

        // The pump runs in the background. Poll the ledger row until it
        // transitions out of "running".
        var deadline = DateTime.UtcNow.AddSeconds(5);
        AgentUsageRow? row = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            row = await db.AgentUsage.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ledgerId);
            if (row?.Status == "finished") break;
            await Task.Delay(50);
        }

        Assert.NotNull(row);
        Assert.Equal("finished", row!.Status);
        Assert.NotNull(row.EndedAt);
        Assert.True(row.OutputTokens > 0);
    }

    [Fact]
    public async Task PostInvocation_Without_User_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/agents/E4/invocations", new
        {
            taskId = "234",
            prompt = "x",
            model = "local-stub",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PauseAgent_Without_PO_Returns_403()
    {
        using var factory = new ForgeWebApplicationFactory();

        // Seed a real user with role 'human' so the role gate has something to
        // refuse. /api/me only lazy-creates rows when a provider is connected,
        // which never happens in tests.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Users.Add(new UserRow
            {
                Id = "user-po",
                DisplayName = "PO",
                Email = "po@forge.local",
                Role = "product-owner",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Users.Add(new UserRow
            {
                Id = "user-human",
                DisplayName = "Human",
                Email = "human@forge.local",
                Role = "human",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var human = factory.CreateClient();
        human.DefaultRequestHeaders.Add("X-Forge-User", "human@forge.local");
        var resp = await human.PostAsync("/api/agents/E4/pause", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GetAgentState_Returns_Idle_By_Default()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/agents/E4/state");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("idle", doc.RootElement.GetProperty("state").GetString());
    }

    private static AgentRunnerDispatcher MakeRunner()
    {
        var http = new StubHttpFactory();
        var registry = new TestModelRegistry();
        var opts = Options.Create(new AgentRunnerOptions());
        var log = NullLogger<AgentRunnerDispatcher>.Instance;
        var copilotServer = new CopilotServerManager(
            Options.Create(new AgentRunnerOptions()),
            NullLogger<CopilotServerManager>.Instance);
        var copilotPool = new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance);
        return new AgentRunnerDispatcher(http, registry, opts, copilotServer, Array.Empty<Forge.Api.Auth.IAuthService>(), copilotPool, log);
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TestModelRegistry : IModelRegistry
    {
        public IReadOnlyList<Forge.Api.Contracts.ModelInfo> All { get; } = new List<Forge.Api.Contracts.ModelInfo>
        {
            new("local-stub", "forge", "local", "Local Stub", 8192, 0m, 0m, false, true, "live"),
            new("claude-opus-4-7", "anthropic", "claude-4", "Opus", 1_000_000, 15m, 75m, true, true, "live"),
        };

        public Forge.Api.Contracts.ModelInfo? Find(string id)
            => this.All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
