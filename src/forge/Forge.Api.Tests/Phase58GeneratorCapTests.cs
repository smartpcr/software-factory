// -----------------------------------------------------------------------
// <copyright file="Phase58GeneratorCapTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 2026-05-13 root-cause fix — three compounding bugs caused the
/// "generator not working / almost no token consumption" symptom on
/// the qq:TELEGRAM-MESSENGER-S Outbound Sender stage:
///
/// <list type="bullet">
/// <item><description>
/// <b>Fix #1 (MaxTurns plumbing).</b> <c>CrewStoryArtifactStrategy</c>
/// constructed <see cref="AgentContext"/> without
/// <see cref="AgentContext.MaxTurns"/> set, so the dispatcher's global
/// 60-turn default capped a 13-pt workstream's generator at turn 60 of
/// an expected 390 (Phase 21.5: 30 turns × points). Mirrors the
/// reference plumbing in <c>CrewQueueWorker.cs:1250-1252</c>.
/// </description></item>
/// <item><description>
/// <b>Fix #2 (Failed-event usage).</b>
/// <c>AgentRunnerDispatcher.RunCopilotSdk</c> (and sister paths)
/// yielded a <c>Failed</c> event on cap/timeout/transport-silent
/// without attaching the accumulated <c>InputTokens/OutputTokens/CostUsd</c>,
/// so a 60-turn cap-aborted run wrote $0 to the agent_usage ledger
/// even though the operator paid for ~7.5 minutes of real provider
/// work. Tested indirectly via the
/// <c>DeliverableLoop_ShortCircuits_*</c> tests which assert the
/// short-circuit path persists the gen cost into the deliverable row
/// and DeliverableLoopResult.
/// </description></item>
/// <item><description>
/// <b>Fix #3 (loop short-circuit).</b> <c>DeliverableLoop</c> happily
/// fed a cap-aborted partial generator output to the evaluator (which
/// then scored it 84/100, verdict=iterate, queueing iter 2 against
/// the same broken cap). The new <see cref="GeneratorRunOutput.FailureReason"/>
/// signal short-circuits the loop with <c>verdict="stalled"</c> and
/// the failure context in <c>DeliverableRow.Notes</c>.
/// </description></item>
/// </list>
/// </summary>
public sealed class Phase58GeneratorCapTests
{
    // ── GeneratorFailureKinds.Classify ───────────────────────────────────

    [Theory]
    [InlineData("Max turns (60) reached", GeneratorFailureKinds.MaxTurns)]
    [InlineData("max-turns cap exceeded", GeneratorFailureKinds.MaxTurns)]
    [InlineData("Copilot SDK hard timeout: exceeded 30m budget", GeneratorFailureKinds.Timeout)]
    [InlineData("claude.exe hard timeout: exceeded 60m budget", GeneratorFailureKinds.Timeout)]
    [InlineData("Copilot transport silent for >5min", GeneratorFailureKinds.Inactive)]
    [InlineData("claude.exe exit=137: oom", GeneratorFailureKinds.CliExit)]
    [InlineData("Anthropic 529: overloaded", GeneratorFailureKinds.ProviderError)]
    [InlineData("something we don't recognise", GeneratorFailureKinds.Other)]
    [InlineData("", GeneratorFailureKinds.Other)]
    public void GeneratorFailureKinds_Classify_Recognizes_Dispatcher_Errors(string error, string expected)
    {
        Assert.Equal(expected, GeneratorFailureKinds.Classify(error));
    }

    [Fact]
    public void GeneratorFailureKinds_Classify_Null_Returns_Other()
    {
        Assert.Equal(GeneratorFailureKinds.Other, GeneratorFailureKinds.Classify(null));
    }

    // ── DeliverableLoop short-circuit on FailureReason ───────────────────

    private const string Fence = "```";

    private static string EvalJson(int score) =>
        Fence + "json\n{ \"score\": " + score + " }\n" + Fence;

    private static async Task<(ForgeWebApplicationFactory f, ForgeDbContext db, DeliverableLoop loop)> SetupAsync(
        string workItemId, int points = 0)
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        db.WorkItems.Add(new WorkItemRow
        {
            Id = workItemId,
            Type = "step",
            State = "active",
            Title = "T",
            Points = points,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (f, db, new DeliverableLoop(db));
    }

    [Fact]
    public async Task DeliverableLoop_ShortCircuits_When_Generator_Returns_FailureReason_And_Skips_Evaluator()
    {
        var (f, db, loop) = await SetupAsync(workItemId: "wi-cap");
        try
        {
            var evaluatorCallCount = 0;
            var req = new DeliverableLoopRequest(
                "wi-cap", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput(
                    OutputText: "partial",
                    CostUsd: 7.31m,
                    InputTokens: 458_000L,
                    OutputTokens: 5_800L,
                    TurnSamplesJson: null,
                    FailureReason: "Max turns (60) reached",
                    FailureKind: GeneratorFailureKinds.MaxTurns)),
                Evaluator: (_, _, _) =>
                {
                    evaluatorCallCount++;
                    return Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.5m));
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal(0, evaluatorCallCount);
            Assert.Equal("stalled", result.FinalVerdict);
            Assert.Equal(1, result.Iterations);
            Assert.Equal(0, result.FinalScore);
            Assert.Equal(7.31m, result.TotalCostUsd);

            var rows = await db.Deliverables.Where(d => d.WorkItemId == "wi-cap").ToListAsync();
            Assert.Single(rows);
            Assert.Equal("stalled", rows[0].Verdict);
            Assert.Equal(0, rows[0].Score);
            Assert.Equal(7.31m, rows[0].GenCostUsd);
            Assert.Equal(0m, rows[0].EvalCostUsd);
            Assert.NotNull(rows[0].Notes);
            Assert.Contains("GENERATOR FAILED", rows[0].Notes!);
            Assert.Contains(GeneratorFailureKinds.MaxTurns, rows[0].Notes!);
            Assert.Contains("Max turns (60) reached", rows[0].Notes!);

            // agent_usage row was persisted with the real (non-zero)
            // generator usage despite the abort — Fix #2's effect
            // landing through the loop's WriteUsageAsync.
            var usage = await db.AgentUsage
                .Where(u => u.TaskId == "wi-cap" && u.CostCategory == "generator")
                .ToListAsync();
            Assert.Single(usage);
            Assert.Equal(7.31m, usage[0].CostUsd);
            Assert.Equal(458_000L, usage[0].InputTokens);
            Assert.Equal(5_800L, usage[0].OutputTokens);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task DeliverableLoop_NonCap_Generator_Failure_Still_ShortCircuits_With_Stalled_Verdict()
    {
        // Provider HTTP error (not max-turns) — same short-circuit
        // applies because feeding garbled provider output into the
        // evaluator wastes evaluator tokens regardless of which
        // dispatcher path failed.
        var (f, db, loop) = await SetupAsync(workItemId: "wi-http");
        try
        {
            var evaluatorCalls = 0;
            var req = new DeliverableLoopRequest(
                "wi-http", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput(
                    OutputText: string.Empty,
                    CostUsd: 0m,
                    InputTokens: 0,
                    OutputTokens: 0,
                    TurnSamplesJson: null,
                    FailureReason: "Anthropic 529: overloaded",
                    FailureKind: GeneratorFailureKinds.ProviderError)),
                Evaluator: (_, _, _) =>
                {
                    evaluatorCalls++;
                    return Task.FromResult(new EvaluatorRunOutput(EvalJson(0), 0m));
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal(0, evaluatorCalls);
            Assert.Equal("stalled", result.FinalVerdict);

            var row = await db.Deliverables.Where(d => d.WorkItemId == "wi-http").SingleAsync();
            Assert.Contains(GeneratorFailureKinds.ProviderError, row.Notes!);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task DeliverableLoop_Generator_Without_FailureReason_Runs_Evaluator_As_Before()
    {
        // Regression guard for the happy path: when FailureReason is
        // null the loop must continue to call the evaluator (prior
        // behaviour) so Phase 14 / 16 / 47 / 50 / 52 / 56 semantics
        // are preserved.
        var (f, db, loop) = await SetupAsync(workItemId: "wi-happy");
        try
        {
            var evaluatorCalls = 0;
            var req = new DeliverableLoopRequest(
                "wi-happy", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput(
                    OutputText: "good",
                    CostUsd: 1.0m,
                    InputTokens: 100,
                    OutputTokens: 50)),
                Evaluator: (_, _, _) =>
                {
                    evaluatorCalls++;
                    return Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.5m));
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal(1, evaluatorCalls);
            Assert.Equal("pass", result.FinalVerdict);
        }
        finally { f.Dispose(); }
    }

    // ── ResolveAgentRunConfigAsync MaxTurns plumbing (Fix #1) ────────────

    private const string CrewId = "crew-eng";
    private const string LeadId = "ENG-LEAD";

    private static async Task<(SqliteConnection conn, ForgeDbContext db)> NewDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var opts = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ForgeDbContext(opts);
        await db.Database.EnsureCreatedAsync();
        db.Crews.Add(new CrewRow
        {
            Id = CrewId,
            Name = "Engineer",
            Lead = LeadId,
            Model = "claude-opus-4-7",
            Backend = "copilot",
            InvokeMode = "sdk",
        });
        db.Agents.Add(new AgentRow
        {
            Id = LeadId,
            Name = "Lead",
            Role = "eng",
            CrewId = CrewId,
            Sort = 0,
        });
        await db.SaveChangesAsync();
        return (conn, db);
    }

    [Fact]
    public async Task ResolveAgentRunConfig_With_MaxTurns_Plumbs_To_AgentContext()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var crew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == CrewId);
            var (_, ctx, _, _) = await CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync(
                db, crew,
                useEvaluatorRole: false,
                workspace: "/tmp/test",
                budget: null,
                CancellationToken.None,
                maxTurns: 13 * 30);

            Assert.Equal(390, ctx.MaxTurns);
        }
    }

    [Fact]
    public async Task ResolveAgentRunConfig_Without_MaxTurns_Leaves_AgentContext_Default_Null()
    {
        // When the caller doesn't know the points, MaxTurns stays null
        // so the dispatcher's global default (AgentRunnerOptions.MaxTurns
        // = 60) takes over. Preserves prior behaviour for callers that
        // can't compute the cap.
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var crew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == CrewId);
            var (_, ctx, _, _) = await CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync(
                db, crew,
                useEvaluatorRole: false,
                workspace: "/tmp/test",
                budget: null,
                CancellationToken.None);

            Assert.Null(ctx.MaxTurns);
        }
    }

    [Fact]
    public async Task ResolveAgentRunConfig_With_MaxTurns_For_Evaluator_Role_Plumbs_Same_Cap()
    {
        // Both generator and evaluator roles must receive the same
        // per-task max-turns cap (mirrors CrewQueueWorker.cs:1405 and
        // 1702 which pass the same taskMaxTurns to both).
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var crew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == CrewId);
            var (_, ctx, _, _) = await CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync(
                db, crew,
                useEvaluatorRole: true,
                workspace: "/tmp/test",
                budget: null,
                CancellationToken.None,
                maxTurns: 5 * 30);

            Assert.Equal(150, ctx.MaxTurns);
        }
    }

    // ── AgentRunnerDispatcher.BuildFailedWithUsage (Fix #2) ──────────────

    [Fact]
    public void BuildFailedWithUsage_With_Accumulated_Tokens_Carries_Usage_On_Failed_Event()
    {
        // The Copilot/SDK happy-path emits AssistantUsageEvent before
        // the cap aborts, so accumulators are non-zero. Helper must
        // forward those onto the Failed event so PumpEvents /
        // CrewQueueWorker / CrewStoryArtifactStrategy delegates can
        // record the real spend.
        var f = new ForgeWebApplicationFactory();
        try
        {
            using (f.CreateClient()) { /* boot */ }
            using var scope = f.Services.CreateScope();
            var dispatcher = (AgentRunnerDispatcher)scope.ServiceProvider.GetRequiredService<IAgentRunner>();

            var evt = dispatcher.BuildFailedWithUsage(
                error: "Max turns (60) reached",
                accumulatedInputTokens: 458_000L,
                accumulatedOutputTokens: 5_800L,
                accumulatedCostUsd: 7.31m,
                promptCharCount: 100,
                outputCharCount: 200,
                modelName: "claude-opus-4.7-xhigh");

            Assert.Equal(AgentEventKind.Failed, evt.Kind);
            Assert.Equal("Max turns (60) reached", evt.Error);
            Assert.Equal(458_000L, evt.InputTokens);
            Assert.Equal(5_800L, evt.OutputTokens);
            // Accumulated cost preferred over recomputation when present.
            Assert.Equal(7.31m, evt.CostUsd);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public void BuildFailedWithUsage_With_Zero_Accumulators_Falls_Back_To_CharsOver4_Estimate()
    {
        // CLI-path failure modes (RunCliAsync timedOut / exitCode!=0)
        // never get a usage event, so the helper must fall back to
        // the chars/4 estimate so cost ledger and Phase 51 budget
        // meter still see something — better an estimate than $0.
        var f = new ForgeWebApplicationFactory();
        try
        {
            using (f.CreateClient()) { /* boot */ }
            using var scope = f.Services.CreateScope();
            var dispatcher = (AgentRunnerDispatcher)scope.ServiceProvider.GetRequiredService<IAgentRunner>();

            var evt = dispatcher.BuildFailedWithUsage(
                error: "claude.exe exit=137: oom",
                accumulatedInputTokens: 0,
                accumulatedOutputTokens: 0,
                accumulatedCostUsd: null,
                promptCharCount: 4_000,
                outputCharCount: 800,
                modelName: "claude-opus-4.7-xhigh");

            Assert.Equal(AgentEventKind.Failed, evt.Kind);
            Assert.Equal(1_000L, evt.InputTokens); // 4000 / 4
            Assert.Equal(200L, evt.OutputTokens);  // 800 / 4
            Assert.NotNull(evt.CostUsd);
        }
        finally { f.Dispose(); }
    }
}
