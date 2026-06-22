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
/// 2026-06-22 — regression coverage for the two linked failures observed on
/// code-intelligence:ENABLE-MCP architecture.md:
///
/// <list type="number">
///   <item><b>Root cause</b> — the generator kept trying to <c>session.resume</c>
///     a Copilot SDK session that no longer existed ("Session not found"),
///     and the post-iter memory persist re-wrote the stale id every iter, so
///     all 40 iters failed identically (generator-inactive) until an operator
///     Restart. The fix self-heals by persisting <c>sessionId: null</c> on a
///     stale-resume failure so <see cref="AgentMemoryStore.EnsureSessionIdAsync"/>
///     mints a fresh session next iter.</item>
///   <item><b>The crash it triggered</b> — with zero successful iterations the
///     <c>priorChain</c> is empty at the iteration cap, and the seed-less
///     <c>priorChain.Max(...)</c> in <see cref="DeliverableLoop"/> threw
///     "Sequence contains no elements", which propagated up to the
///     StorySpawnOrchestrator and halted the whole plan-doc pipeline. The fix
///     guards the empty case.</item>
/// </list>
/// </summary>
public class Phase93StaleSessionSelfHealTests
{
    [Theory]
    // The exact shape observed in the incident (matches both substrings).
    [InlineData(
        "Copilot SDK session acquire failed: Communication error with Copilot CLI: "
        + "Request session.resume failed with message: Session not found: 9a912448-7b5b-4edd-afa8-0bf04d5a6d1d",
        true)]
    [InlineData("Request session.resume failed with message: blah", true)]
    [InlineData("Session not found: abc", true)]
    // Case-insensitive.
    [InlineData("SESSION NOT FOUND", true)]
    // Unrelated failures must NOT be treated as stale-session.
    [InlineData("copilot.exe hard timeout: exceeded 30m budget", false)]
    [InlineData("generator-max-turns cap reached", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsStaleSessionResumeFailure_ClassifiesOnlyResumeMisses(string? failureReason, bool expected)
    {
        Assert.Equal(expected, AgentMemoryStore.IsStaleSessionResumeFailure(failureReason));
    }

    /// <summary>
    /// The self-heal mechanism end-to-end at the store level: once a prior iter
    /// has a resumable session (ShouldResume=true), persisting <c>sessionId: null</c>
    /// (what the gen/eval delegates now do on a stale-resume failure) forces the
    /// NEXT <see cref="AgentMemoryStore.EnsureSessionIdAsync"/> to mint a brand-new
    /// session with ShouldResume=false — i.e. create, not resume-a-corpse.
    /// </summary>
    [Fact]
    public async Task PersistingNullSessionId_ForcesFreshSessionNextIter()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AgentMemoryStore>();

        const string wi = "wi-stale-heal";
        const string agent = "agent-stale-heal";

        // Iter 1: mint a session (create). Iter 2: same id, now resumable.
        var first = await store.EnsureSessionIdAsync(
            wi, agent, AgentMemoryRoles.Generator, backend: "copilot", invokeMode: "sdk");
        var beforeHeal = await store.EnsureSessionIdAsync(
            wi, agent, AgentMemoryRoles.Generator, backend: "copilot", invokeMode: "sdk");
        Assert.False(first.ShouldResume);
        Assert.True(beforeHeal.ShouldResume);
        Assert.Equal(first.SessionId, beforeHeal.SessionId);

        // Self-heal: the gen/eval delegate persists null after a stale-resume failure.
        await store.UpsertAsync(
            wi, agent, AgentMemoryRoles.Generator, backend: "copilot", invokeMode: "sdk",
            sessionId: null, messagesJson: null, lastIter: 5, approxInputTokens: 0);

        // The cleared row has no session and no turns, so GetHandleAsync
        // reports "no resumable memory" (null) — the runner starts fresh.
        var handle = await store.GetHandleAsync(wi, agent, AgentMemoryRoles.Generator);
        Assert.Null(handle);

        // Next iter mints a fresh session and does NOT resume the dead one.
        var afterHeal = await store.EnsureSessionIdAsync(
            wi, agent, AgentMemoryRoles.Generator, backend: "copilot", invokeMode: "sdk");
        Assert.False(afterHeal.ShouldResume);
        Assert.NotEqual(first.SessionId, afterHeal.SessionId);
        Assert.True(Guid.TryParse(afterHeal.SessionId, out _));
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
                        + "with message: Session not found: 9a912448-7b5b-4edd-afa8-0bf04d5a6d1d",
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

        // Every iter landed as an "iterate" deliverable row; the last flips to sloppy.
        var rows = await db.Deliverables.Where(d => d.WorkItemId == wi).OrderBy(d => d.Iter).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("sloppy", rows[^1].Verdict);
        Assert.Contains("best score 0", rows[^1].Notes);
    }
}
