// -----------------------------------------------------------------------
// <copyright file="Phase87PairAttemptTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 2026-05-26 — Phase 87 regression suite for the per-deliverable
/// pair-attempt counter. Validates:
/// <list type="number">
///   <item>The migration adds <c>deliverables.PairAttempt</c> idempotently
///   to every product DB.</item>
///   <item><see cref="DeliverableRow.PairAttempt"/> round-trips through
///   the EF model (nullable; legacy rows survive as null).</item>
///   <item><see cref="ConversationInsightQueries.GetIterKpisAsync"/>
///   projects <c>pairAttempt</c> into the per-iter rollup AND now orders
///   by <c>CreatedAt</c> first (Phase 87 / Part A) so a post-retry
///   pair lands at the rightmost chip position even when its
///   <c>Iter</c> column resets to 1.</item>
///   <item>The "two rows with Iter=1, different pairs" scenario observed
///   on stage 3.3-e2e CLEAN-CODE renders in chronological order with
///   distinct <c>pairAttempt</c> values, exposing a pair boundary the UI
///   can render.</item>
/// </list>
/// </summary>
public class Phase87PairAttemptTests
{
    private const string WiId = "ws-phase87-pair-attempt-test";

    private static async Task<ForgeWebApplicationFactory> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }
        return f;
    }

    [Fact]
    public async Task Migration_Adds_PairAttempt_Column_To_Deliverables()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('deliverables') WHERE name = 'PairAttempt'";
        var result = await probe.ExecuteScalarAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Migration_Is_Idempotent_When_Reapplied()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // ApplyAsync ran once during boot; a second invocation should be a
        // no-op (no exception, no duplicate ALTER TABLE).
        await Phase87Migration.ApplyAsync(control, db);
        await Phase87Migration.ApplyAsync(control, db);

        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM pragma_table_info('deliverables') WHERE name = 'PairAttempt'";
        var n = Convert.ToInt64(await count.ExecuteScalarAsync());
        Assert.Equal(1L, n);
    }

    [Fact]
    public async Task DeliverableRow_Persists_PairAttempt_Nullable()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId + "-rt",
            CrewId = "crew-x",
            GeneratorAgentId = "ag-gen",
            EvaluatorAgentId = "ag-eval",
            Iter = 1,
            PairAttempt = 2,
            Score = 80,
            Verdict = "iterate",
            DimsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId + "-rt",
            CrewId = "crew-x",
            GeneratorAgentId = "ag-gen",
            EvaluatorAgentId = "ag-eval",
            Iter = 2,
            // Legacy / pre-Phase-87 row — PairAttempt left null.
            PairAttempt = null,
            Score = 70,
            Verdict = "iterate",
            DimsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        using var verify = f.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var rows = await verifyDb.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == WiId + "-rt")
            .OrderBy(d => d.Iter)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].PairAttempt);
        Assert.Null(rows[1].PairAttempt);
    }

    [Fact]
    public async Task IterKpis_Projects_PairAttempt_For_Each_Iter()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId + "-proj",
            CrewId = "c", GeneratorAgentId = "g", EvaluatorAgentId = "e",
            Iter = 1, PairAttempt = 1, Score = 86, Verdict = "iterate",
            DimsJson = "{}", CreatedAt = t0,
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId + "-proj",
            CrewId = "c", GeneratorAgentId = "g", EvaluatorAgentId = "e",
            Iter = 2, PairAttempt = 1, Score = 86, Verdict = "iterate",
            DimsJson = "{}", CreatedAt = t0.AddMinutes(1),
        });
        await db.SaveChangesAsync(); await control.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(
            control, db, WiId + "-proj", CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var iters = doc.RootElement.GetProperty("iters").EnumerateArray().ToArray();
        Assert.Equal(2, iters.Length);
        Assert.Equal(1, iters[0].GetProperty("pairAttempt").GetInt32());
        Assert.Equal(1, iters[1].GetProperty("pairAttempt").GetInt32());
    }

    [Fact]
    public async Task IterKpis_Orders_Chronologically_When_Iter_Resets_Across_Pairs()
    {
        // Mirrors the user-reported stage 3.3-e2e CLEAN-CODE scenario:
        // pair 1 (generator YS4) stuck at score 86 for many iterations,
        // operator Retry, pair 2 (generator FL) lands score 95 on its
        // first iter — but its `Iter` column resets to 1 because
        // SoftResetForResumeAsync wiped pair-attempt accounting. With
        // the old `OrderBy(Iter).ThenBy(CreatedAt)` ordering, the 95
        // chip lands at slot 2 (right after the original Iter=1 86) and
        // the sparkline shows a misleading early spike. With Part A's
        // chronological ordering, the 95 lands at the rightmost slot.
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        // Pair 1: Iter 1..3 all score 86.
        for (var i = 1; i <= 3; i++)
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = WiId + "-order",
                CrewId = "c", GeneratorAgentId = "ys4", EvaluatorAgentId = "bh",
                Iter = i, PairAttempt = 1, Score = 86, Verdict = "iterate",
                DimsJson = "{}", CreatedAt = t0.AddMinutes(i),
            });
        }

        // Pair 2 (post-Retry): Iter resets to 1, lands score 95.
        // Created AFTER all pair-1 rows wall-clock-wise.
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId + "-order",
            CrewId = "c", GeneratorAgentId = "fl", EvaluatorAgentId = "bh",
            Iter = 1, PairAttempt = 2, Score = 95, Verdict = "pass",
            DimsJson = "{}", CreatedAt = t0.AddMinutes(60),
        });
        await db.SaveChangesAsync(); await control.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(
            control, db, WiId + "-order", CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var iters = doc.RootElement.GetProperty("iters").EnumerateArray().ToArray();
        Assert.Equal(4, iters.Length);

        // Chronological order: 86, 86, 86, 95 — the 95 is the rightmost
        // chip, which is exactly what the operator expects to see on
        // the sparkline. Pre-Phase-A this came back as [86, 95, 86, 86]
        // because the two Iter=1 rows were grouped first.
        Assert.Equal(86, iters[0].GetProperty("score").GetInt32());
        Assert.Equal(86, iters[1].GetProperty("score").GetInt32());
        Assert.Equal(86, iters[2].GetProperty("score").GetInt32());
        Assert.Equal(95, iters[3].GetProperty("score").GetInt32());

        // pairAttempt flips from 1 → 1 → 1 → 2 — the UI uses this
        // change-point to render a pair-boundary divider between
        // chips 3 and 4.
        Assert.Equal(1, iters[0].GetProperty("pairAttempt").GetInt32());
        Assert.Equal(1, iters[1].GetProperty("pairAttempt").GetInt32());
        Assert.Equal(1, iters[2].GetProperty("pairAttempt").GetInt32());
        Assert.Equal(2, iters[3].GetProperty("pairAttempt").GetInt32());

        // Both rows where Iter=1 survive distinctly: the pair-1 row at
        // position 0 (score 86) and the pair-2 row at position 3
        // (score 95). Bare `iter` alone can't tell them apart — only
        // (iter, pairAttempt) can.
        Assert.Equal(1, iters[0].GetProperty("iter").GetInt32());
        Assert.Equal(1, iters[3].GetProperty("iter").GetInt32());
    }

    [Fact]
    public async Task IterKpis_Returns_Null_PairAttempt_For_Legacy_Rows()
    {
        // Simulates a pre-Phase-87 row written before the column existed
        // — PairAttempt is null. The projection must propagate null
        // through to the UI so it can default to "pair 1" for legacy
        // workstreams.
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId + "-legacy",
            CrewId = "c", GeneratorAgentId = "g", EvaluatorAgentId = "e",
            Iter = 1, PairAttempt = null, Score = 80, Verdict = "iterate",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(); await control.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(
            control, db, WiId + "-legacy", CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var iter = doc.RootElement.GetProperty("iters").EnumerateArray().First();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, iter.GetProperty("pairAttempt").ValueKind);
    }

    [Fact]
    public async Task IterKpis_Projects_Generator_And_Evaluator_AgentIds_For_Each_Iter()
    {
        // 2026-05-28 — regression for the AST 9.3 stalled-no-convergence
        // scenario. Both cycles have PairAttempt=1 (stalled-no-convergence
        // doesn't burn the sloppy cap), but the (generator, evaluator)
        // tuple changes between cycle 1's last iter and cycle 2's first
        // iter. The UI uses these ids to flip the amber pair-divider
        // even when pairAttempt stays unchanged — exposing the cycle
        // boundary that was previously invisible.
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        // Cycle 1: pair JI/JS, two iters.
        for (var i = 1; i <= 2; i++)
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = WiId + "-agents",
                CrewId = "c", GeneratorAgentId = "JI", EvaluatorAgentId = "JS",
                Iter = i, PairAttempt = 1, Score = 89, Verdict = "iterate",
                DimsJson = "{}", CreatedAt = t0.AddMinutes(i),
            });
        }
        // Cycle 2 (post-stalled-no-convergence rotation): pair YG2/BH,
        // Iter resets to 1, PairAttempt stays 1 (the sloppy cap was NOT
        // burned). Created after cycle 1 wall-clock-wise.
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId + "-agents",
            CrewId = "c", GeneratorAgentId = "YG2", EvaluatorAgentId = "BH",
            Iter = 1, PairAttempt = 1, Score = 86, Verdict = "iterate",
            DimsJson = "{}", CreatedAt = t0.AddMinutes(30),
        });
        await db.SaveChangesAsync(); await control.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(
            control, db, WiId + "-agents", CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var iters = doc.RootElement.GetProperty("iters").EnumerateArray().ToArray();
        Assert.Equal(3, iters.Length);
        Assert.Equal("JI", iters[0].GetProperty("generatorAgentId").GetString());
        Assert.Equal("JS", iters[0].GetProperty("evaluatorAgentId").GetString());
        Assert.Equal("JI", iters[1].GetProperty("generatorAgentId").GetString());
        Assert.Equal("JS", iters[1].GetProperty("evaluatorAgentId").GetString());
        Assert.Equal("YG2", iters[2].GetProperty("generatorAgentId").GetString());
        Assert.Equal("BH", iters[2].GetProperty("evaluatorAgentId").GetString());

        // PairAttempt stays unchanged across the cycle boundary — exactly
        // the case where the pre-2026-05-28 divider was silent.
        Assert.Equal(1, iters[1].GetProperty("pairAttempt").GetInt32());
        Assert.Equal(1, iters[2].GetProperty("pairAttempt").GetInt32());
    }
}
