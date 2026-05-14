// -----------------------------------------------------------------------
// <copyright file="Phase60PlanDocPointsFloorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Phase 60 — plan-doc Points floor (3 ≡ ≥90 turns / ≥15 min wall-clock).
///
/// <para>Verifies four code paths share the same floor so a plan-doc
/// work item can never land with <c>Points &lt; 3</c>:
/// <list type="number">
///   <item><see cref="Phase60Migration.ApplyAsync"/> bumps existing rows.</item>
///   <item><see cref="Phase38Migration.ApplyAsync"/> backfill respects the floor on insert.</item>
///   <item><see cref="StorySpawnOrchestrator.EnsurePlanDocWorkItemsAsync"/> live-create respects the floor.</item>
///   <item><see cref="CrewStoryArtifactStrategy.BudgetForPhase"/> wall-clock scales with points up to 30 min.</item>
/// </list></para>
///
/// <para>Phase 58 synthetic <c>score=0 / verdict=stalled</c> on
/// <c>plan-doc:spawn-local-UPDATE-HISTORY-IN-CL:architecture.md</c>
/// (2026-05-13) was the proximate motivator: <c>Points=2 → 60</c> turns
/// kept exhausting the architect's grep-verify protocol. The floor at
/// 3 raises that to 90 turns + 15 min wall-clock so the same loop
/// converges instead of stalling.</para>
/// </summary>
public sealed class Phase60PlanDocPointsFloorTests
{
    private static async Task<(SqliteConnection conn, ForgeDbContext db)> NewDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var opts = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ForgeDbContext(opts);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }

    // ── Phase60Migration ─────────────────────────────────────────────────

    [Fact]
    public async Task Phase60Migration_BumpsExistingPlanDocPointsToFloor()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;

        db.WorkItems.Add(new WorkItemRow
        {
            Id = "plan-doc:spawn-x:architecture.md",
            ParentId = "story-x",
            FeatureRef = "story-x",
            Track = "design",
            Type = "step",
            State = "active",
            Title = "plan: architecture.md",
            DescriptionMd = "n/a",
            Points = 2,
            Priority = "med",
            Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await Phase60Migration.ApplyAsync(db);

        db.ChangeTracker.Clear();
        var row = await db.WorkItems.FirstAsync(w => w.Id == "plan-doc:spawn-x:architecture.md");
        Assert.Equal(3, row.Points);
    }

    [Fact]
    public async Task Phase60Migration_LeavesAtOrAboveFloorRowsAlone()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;

        // Floor (Points=3) -- not touched.
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "plan-doc:spawn-y:tech-spec.md",
            ParentId = "story-y", FeatureRef = "story-y",
            Track = "design", Type = "step", State = "active",
            Title = "plan: tech-spec.md", DescriptionMd = "n/a",
            Points = 3, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        // Above floor (Points=8) -- not touched.
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "plan-doc:spawn-z:e2e-scenarios.md",
            ParentId = "story-z", FeatureRef = "story-z",
            Track = "design", Type = "step", State = "active",
            Title = "plan: e2e-scenarios.md", DescriptionMd = "n/a",
            Points = 8, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await Phase60Migration.ApplyAsync(db);

        db.ChangeTracker.Clear();
        Assert.Equal(3, (await db.WorkItems.FirstAsync(w => w.Id == "plan-doc:spawn-y:tech-spec.md")).Points);
        Assert.Equal(8, (await db.WorkItems.FirstAsync(w => w.Id == "plan-doc:spawn-z:e2e-scenarios.md")).Points);
    }

    [Fact]
    public async Task Phase60Migration_LeavesNonPlanDocRowsAlone()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;

        // Story-level row with Points=1 — must NOT be bumped (would
        // corrupt the story's own budget calculations).
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "story-a",
            ParentId = null, FeatureRef = "story-a",
            Track = "design", Type = "story", State = "active",
            Title = "story", DescriptionMd = "n/a",
            Points = 1, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        // Workstream row with Points=2 — must NOT be bumped (workstream
        // sizing is independent of plan-doc sizing).
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "ws-something",
            ParentId = "story-a", FeatureRef = "story-a",
            Track = "dev", Type = "task", State = "active",
            Title = "ws", DescriptionMd = "n/a",
            Points = 2, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await Phase60Migration.ApplyAsync(db);

        db.ChangeTracker.Clear();
        Assert.Equal(1, (await db.WorkItems.FirstAsync(w => w.Id == "story-a")).Points);
        Assert.Equal(2, (await db.WorkItems.FirstAsync(w => w.Id == "ws-something")).Points);
    }

    [Fact]
    public async Task Phase60Migration_IsIdempotent()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;

        db.WorkItems.Add(new WorkItemRow
        {
            Id = "plan-doc:spawn-q:architecture.md",
            ParentId = "story-q", FeatureRef = "story-q",
            Track = "design", Type = "step", State = "active",
            Title = "plan: architecture.md", DescriptionMd = "n/a",
            Points = 1, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await Phase60Migration.ApplyAsync(db);
        await Phase60Migration.ApplyAsync(db);
        await Phase60Migration.ApplyAsync(db);

        db.ChangeTracker.Clear();
        Assert.Equal(3, (await db.WorkItems.FirstAsync(w => w.Id == "plan-doc:spawn-q:architecture.md")).Points);
    }

    // ── Phase38Migration backfill respects the new floor ─────────────────

    [Fact]
    public async Task Phase38Migration_AppliesFloorOnBackfill()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;

        // Seed a 2-point story spawn with NO plan-doc work-item rows
        // (orphan from a pre-Phase-38 install). Phase 38 backfill must
        // create each plan-doc with Points >= 3.
        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = "spawn-2pt",
            StoryId = "story-2pt",
            StoryTitle = "tiny story",
            StoryPoints = 2,
            Phase = StorySpawnPhases.Plan,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        // Also seed the parent story work item so the backfill's parent
        // pointer is satisfiable (Phase 40 handles real backfill; here
        // we just need a row to exist that doesn't masquerade as plan-doc).
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "story-2pt",
            ParentId = null, FeatureRef = "story-2pt",
            Track = "design", Type = "story", State = "active",
            Title = "story", DescriptionMd = "n/a",
            Points = 2, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await Phase38Migration.ApplyAsync(db);

        db.ChangeTracker.Clear();
        var planDocs = await db.WorkItems
            .Where(w => w.Id.StartsWith("plan-doc:spawn-2pt:"))
            .ToListAsync();
        Assert.Equal(4, planDocs.Count); // architecture, tech-spec, implementation-plan, e2e-scenarios
        Assert.All(planDocs, w => Assert.True(
            w.Points >= 3,
            $"{w.Id} Points={w.Points} < floor 3"));
    }

    // ── BudgetForPhase wall-clock floor + scaling ─────────────────────────

    [Theory]
    [InlineData(1, 600.0)]   // 1pt: 600s floor (turn cap is 30 anyway)
    [InlineData(2, 600.0)]   // 2pt: 600s floor (legacy plan-docs)
    [InlineData(3, 900.0)]   // 3pt: 900s (15 min) - aligns with 90-turn cap
    [InlineData(4, 1200.0)]  // 4pt: 20 min
    [InlineData(5, 1500.0)]  // 5pt: 25 min
    [InlineData(6, 1800.0)]  // 6pt: 30 min cap kicks in
    [InlineData(8, 1800.0)]  // 8pt: clamped to 30 min
    [InlineData(13, 1800.0)] // 13pt: clamped to 30 min
    public void BudgetForPhase_PlanPrefix_ScalesWithPointsAndCapsAt30Minutes(int points, double expectedSeconds)
    {
        var budget = CrewStoryArtifactStrategy.BudgetForPhase(points, "plan");
        Assert.Equal(expectedSeconds, budget.TotalSeconds);
    }

    [Fact]
    public void BudgetForPhase_PlanWithFilenameSuffix_StillScales()
    {
        // The phaseTag passed at the doc level is "plan:architecture.md"
        // -- the prefix splitter must extract "plan" so the floor applies.
        var budget = CrewStoryArtifactStrategy.BudgetForPhase(3, "plan:architecture.md");
        Assert.Equal(900.0, budget.TotalSeconds);
    }

    [Theory]
    [InlineData("impl", 1, 360.0)]      // 1pt impl: max(180, 60) * 2.0 = 360s
    [InlineData("review", 5, 300.0)]    // 5pt review: max(180, 300) * 1.0 = 300s
    [InlineData("decompose", 5, 210.0)] // 5pt decompose: max(180, 300) * 0.7 = 210s
    public void BudgetForPhase_NonPlanPhases_UnchangedByPhase60(string prefix, int points, double expectedSeconds)
    {
        // Phase 60's wall-clock change is gated on prefix=="plan" only.
        // Other phases must still use the original base*multiplier formula.
        var budget = CrewStoryArtifactStrategy.BudgetForPhase(points, prefix);
        Assert.Equal(expectedSeconds, budget.TotalSeconds);
    }

    // ── EnsurePlanDocWorkItemsAsync live-create respects the floor ───────

    [Fact]
    public async Task EnsurePlanDocWorkItemsAsync_TinyStory_LandsWithFloor3()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;

        var spawn = new StorySpawnRow
        {
            Id = "spawn-1pt",
            StoryId = "story-1pt",
            StoryTitle = "1-point story",
            StoryPoints = 1,
            Phase = StorySpawnPhases.Plan,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var story = new WorkItemRow
        {
            Id = "story-1pt",
            ParentId = null, FeatureRef = "story-1pt",
            Track = "design", Type = "story", State = "active",
            Title = "story", DescriptionMd = "n/a",
            Points = 1, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.StorySpawns.Add(spawn);
        db.WorkItems.Add(story);
        await db.SaveChangesAsync();

        await StorySpawnOrchestrator.EnsurePlanDocWorkItemsAsync(db, spawn, story, default);

        db.ChangeTracker.Clear();
        var planDocs = await db.WorkItems
            .Where(w => w.Id.StartsWith("plan-doc:spawn-1pt:"))
            .ToListAsync();
        Assert.Equal(4, planDocs.Count);
        Assert.All(planDocs, w => Assert.Equal(3, w.Points));
    }

    [Fact]
    public async Task EnsurePlanDocWorkItemsAsync_LargeStory_PreservesFullPoints()
    {
        // For stories at or above the floor, the live-create path keeps
        // the full story points (the historical behavior); the floor
        // only kicks in for sub-3 stories.
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;

        var spawn = new StorySpawnRow
        {
            Id = "spawn-13pt",
            StoryId = "story-13pt",
            StoryTitle = "13-point epic",
            StoryPoints = 13,
            Phase = StorySpawnPhases.Plan,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var story = new WorkItemRow
        {
            Id = "story-13pt",
            ParentId = null, FeatureRef = "story-13pt",
            Track = "design", Type = "story", State = "active",
            Title = "story", DescriptionMd = "n/a",
            Points = 13, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.StorySpawns.Add(spawn);
        db.WorkItems.Add(story);
        await db.SaveChangesAsync();

        await StorySpawnOrchestrator.EnsurePlanDocWorkItemsAsync(db, spawn, story, default);

        db.ChangeTracker.Clear();
        var planDocs = await db.WorkItems
            .Where(w => w.Id.StartsWith("plan-doc:spawn-13pt:"))
            .ToListAsync();
        Assert.Equal(4, planDocs.Count);
        Assert.All(planDocs, w => Assert.Equal(13, w.Points));
    }

    [Fact]
    public async Task EnsurePlanDocWorkItemsAsync_IsIdempotent_DoesNotResetExistingRowPoints()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = db;

        var spawn = new StorySpawnRow
        {
            Id = "spawn-existing",
            StoryId = "story-existing",
            StoryTitle = "existing",
            StoryPoints = 5,
            Phase = StorySpawnPhases.Plan,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var story = new WorkItemRow
        {
            Id = "story-existing",
            ParentId = null, FeatureRef = "story-existing",
            Track = "design", Type = "story", State = "active",
            Title = "story", DescriptionMd = "n/a",
            Points = 5, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        // Pre-seed a plan-doc row with operator-bumped Points=10 (e.g.
        // bumped via the /bump endpoint after a stall). The live-create
        // path must NOT overwrite this.
        db.StorySpawns.Add(spawn);
        db.WorkItems.Add(story);
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "plan-doc:spawn-existing:architecture.md",
            ParentId = "story-existing", FeatureRef = "story-existing",
            Track = "design", Type = "step", State = "active",
            Title = "plan: architecture.md", DescriptionMd = "n/a",
            Points = 10, Priority = "med", Provider = "local",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await StorySpawnOrchestrator.EnsurePlanDocWorkItemsAsync(db, spawn, story, default);

        db.ChangeTracker.Clear();
        var existing = await db.WorkItems.FirstAsync(w => w.Id == "plan-doc:spawn-existing:architecture.md");
        Assert.Equal(10, existing.Points); // preserved
        // Sibling rows get created at floor (5 here, since 5 >= 3).
        var techSpec = await db.WorkItems.FirstAsync(w => w.Id == "plan-doc:spawn-existing:tech-spec.md");
        Assert.Equal(5, techSpec.Points);
    }
}
