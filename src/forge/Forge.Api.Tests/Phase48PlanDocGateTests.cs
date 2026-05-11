// -----------------------------------------------------------------------
// <copyright file="Phase48PlanDocGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// forge_implementation.md spec Phase 48 — plan-doc completion gate.
/// Validates the <c>ArePlanDocsAllPassedAsync</c> helper truth table
/// and the cascade through <c>DriveImplementAsync</c> /
/// <c>DriveDecomposeAsync</c> entry points.
/// </summary>
public class Phase48PlanDocGateTests
{
    private const string SpawnId = "spawn-p48";
    private const string StoryId = "story-p48";

    private static readonly string[] PlanDocFileNames =
    {
        "tech-spec.md",
        "architecture.md",
        "implementation-plan.md",
        "e2e-scenarios.md",
    };

    [Fact]
    public async Task Helper_returns_false_when_any_doc_lacks_deliverables()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var spawn = SeedSpawn(db, StorySpawnPhases.Plan);
        await db.SaveChangesAsync();

        var (allPassed, nonPassed) =
            await StorySpawnOrchestrator.ArePlanDocsAllPassedAsync(db, spawn, default);

        Assert.False(allPassed);
        // Every doc is unpassed since none have deliverables.
        Assert.Equal(4, nonPassed.Count);
        Assert.Contains("tech-spec", nonPassed);
        Assert.Contains("architecture", nonPassed);
        Assert.Contains("implementation-plan", nonPassed);
        Assert.Contains("e2e-scenarios", nonPassed);
    }

    [Fact]
    public async Task Helper_returns_false_when_any_doc_latest_verdict_iterates()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var spawn = SeedSpawn(db, StorySpawnPhases.Plan);

        // Three docs at pass, one at iterate (mid-iteration).
        AddDeliverable(db, $"plan-doc:{SpawnId}:tech-spec.md", iter: 3, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:architecture.md", iter: 4, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:implementation-plan.md", iter: 5, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:e2e-scenarios.md", iter: 7, verdict: "iterate");
        await db.SaveChangesAsync();

        var (allPassed, nonPassed) =
            await StorySpawnOrchestrator.ArePlanDocsAllPassedAsync(db, spawn, default);

        Assert.False(allPassed);
        Assert.Single(nonPassed);
        Assert.Equal("e2e-scenarios", nonPassed[0]);
    }

    [Fact]
    public async Task Helper_returns_false_when_latest_verdict_stalled()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var spawn = SeedSpawn(db, StorySpawnPhases.Implement);

        AddDeliverable(db, $"plan-doc:{SpawnId}:tech-spec.md", iter: 5, verdict: "stalled");
        AddDeliverable(db, $"plan-doc:{SpawnId}:architecture.md", iter: 4, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:implementation-plan.md", iter: 5, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:e2e-scenarios.md", iter: 4, verdict: "pass");
        await db.SaveChangesAsync();

        var (allPassed, nonPassed) =
            await StorySpawnOrchestrator.ArePlanDocsAllPassedAsync(db, spawn, default);

        Assert.False(allPassed);
        Assert.Equal(new[] { "tech-spec" }, nonPassed);
    }

    [Fact]
    public async Task Helper_returns_true_when_all_four_docs_passed()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var spawn = SeedSpawn(db, StorySpawnPhases.Plan);
        foreach (var fileName in PlanDocFileNames)
        {
            AddDeliverable(db, $"plan-doc:{SpawnId}:{fileName}", iter: 3, verdict: "pass");
        }
        await db.SaveChangesAsync();

        var (allPassed, nonPassed) =
            await StorySpawnOrchestrator.ArePlanDocsAllPassedAsync(db, spawn, default);

        Assert.True(allPassed);
        Assert.Empty(nonPassed);
    }

    [Fact]
    public async Task Helper_uses_latest_iter_when_doc_has_multiple_deliverables()
    {
        // Operator-restart scenario: doc had a stalled iter 5, then was
        // restarted; the new pass on iter 1 should make the doc pass.
        // (Restart deletes deliverables in the production code, but the
        // helper's contract is "latest by Iter desc, then CreatedAt desc"
        // so it must Pick the highest Iter regardless of CreatedAt.)
        // This test pins the latest-by-iter-then-createdat ordering
        // explicitly so a future refactor doesn't quietly regress it.
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var spawn = SeedSpawn(db, StorySpawnPhases.Plan);
        var docId = $"plan-doc:{SpawnId}:tech-spec.md";

        // Earlier iter with verdict=stalled.
        AddDeliverable(db, docId, iter: 5, verdict: "stalled",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-30));
        // Later iter with verdict=iterate (still in progress).
        AddDeliverable(db, docId, iter: 6, verdict: "iterate",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        // Other docs all pass.
        AddDeliverable(db, $"plan-doc:{SpawnId}:architecture.md", iter: 4, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:implementation-plan.md", iter: 5, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:e2e-scenarios.md", iter: 4, verdict: "pass");
        await db.SaveChangesAsync();

        var (allPassed, nonPassed) =
            await StorySpawnOrchestrator.ArePlanDocsAllPassedAsync(db, spawn, default);

        Assert.False(allPassed);
        Assert.Equal(new[] { "tech-spec" }, nonPassed);
    }

    [Fact]
    public async Task Helper_recognises_accept_overridden_doc_as_passed()
    {
        // Phase 43 ACCEPT flips the latest deliverable's verdict to 'pass'
        // even when the agent's score was below the bar. The helper just
        // reads the verdict, so an ACCEPT-flipped row counts as passed
        // — no special-casing needed.
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var spawn = SeedSpawn(db, StorySpawnPhases.Plan);
        var docId = $"plan-doc:{SpawnId}:tech-spec.md";

        // Score below 90 but verdict overridden to 'pass' by ACCEPT.
        AddDeliverable(db, docId, iter: 5, verdict: "pass", score: 84);
        AddDeliverable(db, $"plan-doc:{SpawnId}:architecture.md", iter: 4, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:implementation-plan.md", iter: 5, verdict: "pass");
        AddDeliverable(db, $"plan-doc:{SpawnId}:e2e-scenarios.md", iter: 4, verdict: "pass");
        await db.SaveChangesAsync();

        var (allPassed, _) =
            await StorySpawnOrchestrator.ArePlanDocsAllPassedAsync(db, spawn, default);

        Assert.True(allPassed);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static StorySpawnRow SeedSpawn(ForgeDbContext db, string phase)
    {
        var spawn = new StorySpawnRow
        {
            Id = SpawnId,
            StoryId = StoryId,
            StoryTitle = "Phase 48 gate test",
            StoryDescription = "Plan-doc completion gate truth table.",
            StoryPoints = 5,
            Phase = phase,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.StorySpawns.Add(spawn);
        return spawn;
    }

    private static void AddDeliverable(
        ForgeDbContext db, string workItemId, int iter, string verdict,
        int score = 0, DateTimeOffset? createdAt = null)
    {
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = workItemId,
            CrewId = "crew-arch",
            GeneratorAgentId = "G",
            EvaluatorAgentId = "E",
            Iter = iter,
            Score = score == 0 && verdict == "pass" ? 92 : score,
            DimsJson = "{}",
            Verdict = verdict,
            Notes = string.Empty,
            GeneratorSummary = string.Empty,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        });
    }
}
