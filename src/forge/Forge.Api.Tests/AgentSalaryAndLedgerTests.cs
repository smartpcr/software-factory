// -----------------------------------------------------------------------
// <copyright file="AgentSalaryAndLedgerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Phase 11 Batch D — salary endpoint round-trip, cost-ledger arithmetic, and
/// soul.md hot-reload.
/// </summary>
public class AgentSalaryAndLedgerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetCrewSalary_Aggregates_Today_Sprint_Month()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var now = DateTimeOffset.UtcNow;
            var startOfDay = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            control.AgentUsage.AddRange(
                Row("E4", "crew-eng", 0.50m, startOfDay.AddHours(1)),
                Row("E4", "crew-eng", 0.25m, startOfDay.AddMinutes(-30)), // yesterday
                Row("E4", "crew-eng", 0.75m, startOfDay.AddDays(-3)),
                Row("E4", "crew-eng", 0.10m, startOfDay.AddDays(-40)));   // last month
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/crews/crew-eng/salary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var today = doc.RootElement.GetProperty("today").GetDecimal();
        var sprint = doc.RootElement.GetProperty("sprint").GetDecimal();
        var month = doc.RootElement.GetProperty("month").GetDecimal();
        var sparkline = doc.RootElement.GetProperty("sparkline").EnumerateArray().Select(e => e.GetDecimal()).ToList();

        Assert.Equal(0.5m, today);
        Assert.True(sprint >= 0.5m && sprint <= 1.5m, $"sprint={sprint} expected within [0.5, 1.5]");
        // Month captures rows from start of this calendar month forward — yesterday + 3-days-ago + today.
        Assert.True(month >= 0.5m && month <= 1.5m, $"month={month}");
        Assert.Equal(14, sparkline.Count);
    }

    [Fact]
    public async Task GetSalaryTodayTotal_Sums_Across_Crews()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var startOfDay = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
            control.AgentUsage.AddRange(
                Row("E4", "crew-eng", 1.00m, startOfDay.AddHours(1)),
                Row("R3", "crew-rev", 0.25m, startOfDay.AddHours(2)),
                Row("Q2", "crew-qa",  0.10m, startOfDay.AddDays(-1)));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/crews/salary/today");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(1.25m, doc.RootElement.GetProperty("totalUsd").GetDecimal());
        Assert.Equal(0, doc.RootElement.GetProperty("activeAgents").GetInt32());
    }

    /// <summary>
    /// /api/crews/concurrency must reflect open agent_claims rows (the
    /// modern story-spawn signal), not just task_queue picked-up state.
    /// Regression for the TopBar "ACTIVE 0/cap" bug observed 2026-05-05:
    /// 22 workstreams in flight via CrewStoryArtifactStrategy, but the
    /// chip stayed at 0 because that path never enqueues to task_queue.
    /// </summary>
    [Fact]
    public async Task GetCrewConcurrency_Counts_Open_Agent_Claims_Per_Crew()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            // Pin agent → crew explicitly so the test doesn't depend on the
            // shifting prototypeAlias mapping in ForgeSeed.
            var e4 = await control.Agents.FirstOrDefaultAsync(a => a.Id == "E4");
            var e7 = await control.Agents.FirstOrDefaultAsync(a => a.Id == "E7");
            if (e4 is not null) e4.CrewId = "crew-eng";
            if (e7 is not null) e7.CrewId = "crew-eng";
            // Seed: two open eng claims + one closed (released) eng claim
            // + one open claim on an agent without a CrewId (unassigned bucket).
            control.AgentClaims.AddRange(
                new AgentClaimRow { AgentId = "E4", WorkItemId = "ws-1", Role = "generator", ClaimedAt = DateTimeOffset.UtcNow, ReleasedAt = null },
                new AgentClaimRow { AgentId = "E7", WorkItemId = "ws-1", Role = "evaluator", ClaimedAt = DateTimeOffset.UtcNow, ReleasedAt = null },
                new AgentClaimRow { AgentId = "E4", WorkItemId = "ws-old", Role = "generator", ClaimedAt = DateTimeOffset.UtcNow.AddHours(-2), ReleasedAt = DateTimeOffset.UtcNow.AddHours(-1) },
                new AgentClaimRow { AgentId = "ZZ-orphan", WorkItemId = "ws-2", Role = "generator", ClaimedAt = DateTimeOffset.UtcNow, ReleasedAt = null });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/crews/concurrency");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // 2 open eng claims + 1 unassigned = 3 total active.
        Assert.Equal(3, root.GetProperty("active").GetInt32());
        // Cap is the seeded sum across all crews — it must be > 0.
        Assert.True(root.GetProperty("cap").GetInt32() > 0);

        var perCrew = root.GetProperty("perCrew").EnumerateArray().ToList();
        var eng = perCrew.Single(e => e.GetProperty("id").GetString() == "crew-eng");
        Assert.Equal(2, eng.GetProperty("active").GetInt32());
        Assert.True(eng.GetProperty("cap").GetInt32() > 0);

        // Unassigned bucket is appended only when an open claim has no crew.
        var unassigned = perCrew.SingleOrDefault(e => e.GetProperty("id").GetString() == "(unassigned)");
        Assert.NotEqual(default, unassigned);
        Assert.Equal(1, unassigned.GetProperty("active").GetInt32());
        Assert.Equal(0, unassigned.GetProperty("cap").GetInt32());
    }

    [Fact]
    public async Task CostLedger_Records_Correct_Cost_For_Priced_Crew()
    {
        // End-to-end: priced crew + StartInvocation pump → ledger row carries
        // the right cost. Drives a single "running" ledger row to "finished"
        // with realistic token counts via the local-stub model. We override
        // the crew model to opus-style pricing to check the math.
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var crew = await control.Crews.FirstAsync(c => c.Id == "crew-eng");
            crew.ModelInputPricePerMTok = 15m;
            crew.ModelOutputPricePerMTok = 75m;
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        var resp = await client.PostAsJsonAsync("/api/agents/E4/invocations", new
        {
            taskId = "234",
            // Long-ish prompt so input tokens > 0 deterministically.
            prompt = "lorem ipsum dolor sit amet consectetur adipiscing elit",
            model = "local-stub", // local-stub bypass keeps tests offline.
            crewId = "crew-eng",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var ledgerId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("ledgerId").GetInt64();

        // Wait for pump to finish.
        AgentUsageRow? row = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            row = await control.AgentUsage.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ledgerId);
            if (row?.Status == "finished") break;
            await Task.Delay(50);
        }
        Assert.NotNull(row);
        Assert.Equal("finished", row!.Status);

        // Cost = input × inputPrice/1e6 + output × outputPrice/1e6.
        // local-stub uses model-registry pricing = $0; the endpoint pump uses
        // the *crew's* denormalised price (set above). So the recorded cost
        // should reflect the priced crew, not the registry's local-stub zero.
        var expected = (15m * row.InputTokens / 1_000_000m) + (75m * row.OutputTokens / 1_000_000m);
        Assert.Equal(Math.Round(expected, 6), row.CostUsd);
    }

    [Fact]
    public async Task Soul_HotReload_Sees_External_Edit()
    {
        // External edit (file written outside the API) is picked up on next
        // GET because SoulService reads the file every time.
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        var tmp = Path.Combine(Path.GetTempPath(), $"forge-soul-hr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var row = await control.Agents.FirstAsync(a => a.Id == "E4");
                row.SoulPath = Path.Combine(tmp, "E4.md");
                await control.SaveChangesAsync(); await db.SaveChangesAsync();
            }

            // First read: file doesn't exist.
            var first = await client.GetAsync("/api/agents/E4/soul");
            var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.False(firstDoc.RootElement.GetProperty("exists").GetBoolean());

            // External write.
            await File.WriteAllTextAsync(Path.Combine(tmp, "E4.md"), "---\ntools: [run_command]\n---\nv2");

            // Second read picks up the new content + parsed capability.
            var second = await client.GetAsync("/api/agents/E4/soul");
            var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            Assert.True(secondDoc.RootElement.GetProperty("exists").GetBoolean());
            Assert.Equal("---\ntools: [run_command]\n---\nv2", secondDoc.RootElement.GetProperty("contents").GetString());
            var caps = secondDoc.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "run_command" }, caps);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ClaudeRunner_Cost_Math_Matches_Model_Pricing()
    {
        // Direct unit test of the ComputeCost helper via a public surface:
        // run the local-stub against an opus-priced model in the registry and
        // assert the Finished event's CostUsd matches the formula.
        var registry = new InlineRegistry(new Forge.Api.Contracts.ModelInfo(
            Id: "test-priced-stub",
            Vendor: "forge",
            Family: "local",
            DisplayName: "Test priced stub",
            ContextWindow: 8192,
            InputPricePerMTok: 10m,
            OutputPricePerMTok: 100m,
            SupportsTools: false,
            SupportsStreaming: true,
            Status: "live"));

        // The runner only fast-paths "local-stub". Use a registry with that id
        // mapped to non-zero pricing to test the cost math without HTTP.
        var localRegistry = new InlineRegistry(new Forge.Api.Contracts.ModelInfo(
            Id: "local-stub",
            Vendor: "forge",
            Family: "local",
            DisplayName: "Local Stub (priced)",
            ContextWindow: 8192,
            InputPricePerMTok: 10m,
            OutputPricePerMTok: 100m,
            SupportsTools: false,
            SupportsStreaming: true,
            Status: "live"));

        var runner = new AgentRunnerDispatcher(
            new InlineHttpFactory(),
            localRegistry,
            Options.Create(new AgentRunnerOptions()),
            new CopilotServerManager(
                Options.Create(new AgentRunnerOptions()),
                NullLogger<CopilotServerManager>.Instance,
                Array.Empty<Forge.Api.Auth.IAuthService>()),
            Array.Empty<Forge.Api.Auth.IAuthService>(),
            new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance),
            NullLogger<AgentRunnerDispatcher>.Instance);

        var ctx = new AgentContext(
            CrewId: "engineering",
            Model: "local-stub",
            SoulMd: string.Empty,
            WorktreePath: string.Empty,
            AllowedTools: Array.Empty<string>());

        var inv = runner.StartAsync("234", "E4", "alpha beta gamma", ctx, CancellationToken.None).GetAwaiter().GetResult();
        var events = new List<AgentEvent>();
        foreach (var e in ToList(inv.Events)) events.Add(e);

        var finished = events.Last();
        Assert.Equal(AgentEventKind.Finished, finished.Kind);
        var expected = Math.Round(
            10m * (finished.InputTokens ?? 0) / 1_000_000m + 100m * (finished.OutputTokens ?? 0) / 1_000_000m,
            6);
        Assert.Equal(expected, finished.CostUsd);
    }

    // ----- helpers ---------------------------------------------------------

    private static AgentUsageRow Row(string agentId, string crewId, decimal cost, DateTimeOffset at) => new()
    {
        AgentId = agentId,
        CrewId = crewId,
        TaskId = "test",
        Model = "local-stub",
        InputTokens = 100,
        OutputTokens = 100,
        CostUsd = cost,
        StartedAt = at,
        EndedAt = at,
        Status = "finished",
    };

    private static List<AgentEvent> ToList(IAsyncEnumerable<AgentEvent> seq)
    {
        var list = new List<AgentEvent>();
        var enumerator = seq.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) list.Add(enumerator.Current);
        }
        finally { enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return list;
    }

    private sealed class InlineRegistry : IModelRegistry
    {
        private readonly List<Forge.Api.Contracts.ModelInfo> models;

        public InlineRegistry(params Forge.Api.Contracts.ModelInfo[] models) => this.models = models.ToList();

        public IReadOnlyList<Forge.Api.Contracts.ModelInfo> All => this.models;

        public Forge.Api.Contracts.ModelInfo? Find(string id)
            => this.models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        public void ApplyCatalogOverlay(System.Collections.Generic.IReadOnlyDictionary<string, Forge.Api.Agents.ModelCatalogOverlay> overlay, System.Collections.Generic.IReadOnlyList<Forge.Api.Contracts.ModelInfo>? discoveredModels = null, System.Collections.Generic.IReadOnlyDictionary<string, string>? statusOverrides = null) { }

        public string SeedFilePath => string.Empty;

        public bool CatalogEnriched => false;

        public System.Collections.Generic.IReadOnlyCollection<string> LiveCatalogIds => System.Array.Empty<string>();

        public void RecordLiveCatalog(System.Collections.Generic.IReadOnlyCollection<string> liveModelIds) { }

        public string? ValidateAssignable(string? modelId) => null;
    }

    private sealed class InlineHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
