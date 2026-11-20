// -----------------------------------------------------------------------
// <copyright file="StickyEvaluatorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// harness:DSH stage 1.3 (2026-11-19): two host restarts resumed the loop with a
/// different evaluator each time (LL → JS → LL) although the generator kept going.
/// A resumed loop must keep the evaluator of its latest scored iteration.
/// </summary>
public sealed class StickyEvaluatorTests
{
    private const string Wi = "ws-stage-1-3";

    [Fact]
    public async Task Resumed_Loop_Keeps_The_Evaluator_Of_Its_Latest_Iteration()
    {
        var (conn, control, db) = await NewDbsAsync();
        await using (conn) await using (control) await using (db)
        {
            Seed(control, db, ("LL", "BE", 85, "iterate"), ("JS", "BE", 89, "iterate"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            Assert.Equal("JS", await StickyEvaluator.ResolveAsync(control, db, Wi, "BE"));
            // A stale open claim left by the interrupted run on THIS item does not count as busy.
            control.AgentClaims.Add(new AgentClaimRow { AgentId = "JS", WorkItemId = Wi, Role = "evaluator" });
            await control.SaveChangesAsync();
            Assert.Equal("JS", await StickyEvaluator.ResolveAsync(control, db, Wi, "BE"));
        }
    }

    [Fact]
    public async Task Fresh_Or_Finished_Loops_And_Unavailable_Evaluators_Fall_Back_To_The_Normal_Pick()
    {
        var (conn, control, db) = await NewDbsAsync();
        await using (conn) await using (control) await using (db)
        {
            Assert.Null(await StickyEvaluator.ResolveAsync(control, db, Wi, "BE")); // no iteration yet

            Seed(control, db, ("JS", "BE", 92, "pass"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            Assert.Null(await StickyEvaluator.ResolveAsync(control, db, Wi, "BE")); // loop finished

            db.Deliverables.Add(Deliverable("JS", "BE", 88, "iterate"));
            await db.SaveChangesAsync();
            Assert.Null(await StickyEvaluator.ResolveAsync(control, db, Wi, "JS")); // never the generator
            Assert.Null(await StickyEvaluator.ResolveAsync(control, db, Wi, "BE", new[] { "LL" })); // left the crew

            control.AgentClaims.Add(new AgentClaimRow { AgentId = "JS", WorkItemId = "other-item", Role = "evaluator" });
            await control.SaveChangesAsync();
            Assert.Null(await StickyEvaluator.ResolveAsync(control, db, Wi, "BE")); // saturated by other work

            (await control.ClaimsFor("JS")).ForEach(c => c.ReleasedAt = DateTimeOffset.UtcNow);
            (await control.Agents.SingleAsync(a => a.Id == "JS")).State = "paused";
            await control.SaveChangesAsync();
            Assert.Null(await StickyEvaluator.ResolveAsync(control, db, Wi, "BE")); // paused
        }
    }

    private static void Seed(ControlDbContext control, ProductDbContext db, params (string Eval, string Gen, int Score, string Verdict)[] iterations)
    {
        foreach (var id in new[] { "BE", "JS", "LL" })
        {
            if (!control.Agents.Local.Any(a => a.Id == id)) control.Agents.Add(new AgentRow { Id = id, Name = id, Role = "eng", MaxConcurrentTasks = 1 });
        }

        foreach (var it in iterations) db.Deliverables.Add(Deliverable(it.Eval, it.Gen, it.Score, it.Verdict));
    }

    private static DeliverableRow Deliverable(string eval, string gen, int score, string verdict) => new()
    {
        WorkItemId = Wi, CrewId = "crew-eng", GeneratorAgentId = gen, EvaluatorAgentId = eval, Iter = 1, Score = score,
        DimsJson = "{}", Verdict = verdict, Notes = string.Empty, GeneratorSummary = string.Empty, CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<(SqliteConnection Conn, ControlDbContext Control, ProductDbContext Db)> NewDbsAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options);
        await control.Database.EnsureCreatedAsync();
        var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);
        foreach (var stmt in db.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var sql = stmt.Trim()
                .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
                .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
                .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
            if (sql.Length > 0) await db.Database.ExecuteSqlRawAsync(sql);
        }

        return (conn, control, db);
    }
}

internal static class StickyEvaluatorTestExtensions
{
    public static Task<List<AgentClaimRow>> ClaimsFor(this ControlDbContext control, string agentId)
        => control.AgentClaims.Where(c => c.AgentId == agentId).ToListAsync();
}