// -----------------------------------------------------------------------
// <copyright file="Phase93StaleSessionSelfHealTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 2026-06-22 — coverage for the two fixes to the Copilot "Session not found"
/// 40-iter burn observed on code-intelligence:ENABLE-MCP architecture.md:
///
/// <list type="number">
///   <item><b>Crash guard</b> — when EVERY iteration is a transient generator
///     failure (a dead session that keeps failing to resume), no iter reaches
///     the evaluator, so <c>priorChain</c> is empty at the iteration cap. The
///     seed-less <c>priorChain.Max(...)</c> threw "Sequence contains no
///     elements" and halted the orchestrator; the guard ends cleanly with
///     verdict=sloppy instead.</item>
///   <item><b>Stale-session matcher</b> —
///     <see cref="AgentMemoryStore.IsStaleSessionResumeFailure"/> classifies the
///     Copilot CLI error. The dispatcher uses it (via its private
///     <c>IsCopilotSessionNotFound</c>) to fall back from a failed
///     <c>session.resume</c> to creating a FRESH session in the same call — the
///     real root-cause fix. The matcher must hit the exact production strings
///     and not over-match unrelated transient failures.</item>
/// </list>
/// </summary>
public class Phase93StaleSessionSelfHealTests
{
    [Theory]
    // The exact shape the Copilot CLI emits (matches both substrings).
    [InlineData(
        "Copilot SDK session acquire failed: Communication error with Copilot CLI: "
        + "Request session.resume failed with message: Session not found: 70824a7b-c200-4edd-b5d5-f925905e2bf5",
        true)]
    [InlineData("Request session.resume failed with message: blah", true)]
    [InlineData("Session not found: abc", true)]
    // Case-insensitive.
    [InlineData("SESSION NOT FOUND", true)]
    // SDK 1.0.x: the shared client still holds an undisposed session for the id.
    [InlineData("Session '8de1fb36-b56c-420a-95fe-ae1203e472fd' is already tracked by this client.", true)]
    // Unrelated failures must NOT be treated as stale-session (no needless reset).
    [InlineData("copilot.exe hard timeout: exceeded 30m budget", false)]
    [InlineData("generator-max-turns cap reached", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsStaleSessionResumeFailure_ClassifiesOnlyResumeMisses(string? failureReason, bool expected)
    {
        Assert.Equal(expected, AgentMemoryStore.IsStaleSessionResumeFailure(failureReason));
    }

    /// <summary>
    /// The crash guard: when EVERY iteration is a transient generator failure
    /// (e.g. a dead Copilot session that keeps failing to resume), no iter ever
    /// reaches the evaluator, so <c>priorChain</c> is empty at the iteration cap.
    /// Before the fix the seed-less <c>priorChain.Max(...)</c> threw "Sequence
    /// contains no elements" and halted the orchestrator. Now the loop finishes
    /// cleanly with verdict=sloppy (iter-cap).
    /// </summary>
    [Fact]
    public async Task AllIterationsTransientFail_HitsIterCap_WithoutThrowing()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (factory.CreateClient()) { /* triggers boot + migrate */ }
        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        const string wi = "plan-doc:spawn-1:architecture.md";
        db.WorkItems.Add(new WorkItemRow
        {
            Id = wi, Type = "step", State = "active",
            Title = "architecture.md", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync();
        await db.SaveChangesAsync();

        var loop = new DeliverableLoop(control, db);
        var generatorCalls = 0;
        var evaluatorCalls = 0;
        var req = new DeliverableLoopRequest(
            wi, "crew-architect", "gen-1", "eval-1",
            Generator: (_, _, _) =>
            {
                generatorCalls++;
                // Mirrors the incident: a transient "Session not found" inactive failure.
                return Task.FromResult(new GeneratorRunOutput(
                    OutputText: string.Empty,
                    CostUsd: 0.05m,
                    FailureReason: "Copilot SDK session acquire failed: Request session.resume failed "
                        + "with message: Session not found: 70824a7b-c200-4edd-b5d5-f925905e2bf5",
                    FailureKind: GeneratorFailureKinds.Inactive));
            },
            Evaluator: (_, _, _) =>
            {
                evaluatorCalls++;
                return Task.FromResult(new EvaluatorRunOutput("{\"score\":0}", 0m));
            },
            MaxIterations: 2);

        // The assertion is primarily "does not throw" — before the guard this
        // threw InvalidOperationException("Sequence contains no elements").
        var result = await loop.RunAsync(req, CancellationToken.None);

        Assert.Equal("sloppy", result.FinalVerdict);
        Assert.Equal(2, generatorCalls);
        Assert.Equal(0, evaluatorCalls); // evaluator skipped against a broken generator
        Assert.Equal(0, result.FinalScore);

        var rows = await db.Deliverables.Where(d => d.WorkItemId == wi).OrderBy(d => d.Iter).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("sloppy", rows[^1].Verdict);
        Assert.Contains("best score 0", rows[^1].Notes);
    }
}
