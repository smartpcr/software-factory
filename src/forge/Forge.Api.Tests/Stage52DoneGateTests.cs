// -----------------------------------------------------------------------
// <copyright file="Stage52DoneGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 5.2 — the orchestrator's done-gate consults
/// the workstream set only (Category-agnostic): every impl + qa-e2e row
/// must be <c>complete</c> for the spawn to flip to <c>Phase=done</c>.
/// The legacy <c>StorySpawnRow.QaPrId</c> predicate is removed from the
/// new code path but kept on <c>DriveQaAsync</c> for spawns paused in
/// the legacy <c>qa</c> phase.
///
/// <para>The first two Facts hand-construct a `ProductDbContext` against
/// SQLite-in-memory so the
/// <see cref="StorySpawnOrchestrator.IsSpawnDoneAsync"/> static predicate
/// is unit-testable without booting the full host. The third Fact boots
/// <see cref="ForgeWebApplicationFactory"/> + drives the orchestrator
/// after pre-seeding workstreams in the merged-PR state, verifying the
/// spawn lands in <c>Phase=done</c> via <c>DriveReviewAsync</c> WITHOUT
/// visiting <c>Phase=qa</c>.</para>
/// </summary>
[Collection("Phase28")]
public class Stage52DoneGateTests
{
    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Build a fresh SQLite-in-memory ProductDbContext with the
    /// current schema. Caller disposes the conn.</summary>
    private static (Microsoft.Data.Sqlite.SqliteConnection conn, ProductDbContext db) NewInMemoryDb()
    {
        var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ProductDbContext(opts);
        db.Database.EnsureCreated();
        return (conn, db);
    }

    private static WorkstreamRow Row(string id, string spawnId, string status, string category)
        => new()
        {
            Id = id,
            StorySpawnId = spawnId,
            Name = id,
            Branch = $"ws/{id}",
            Base = "main",
            Epic = string.Empty,
            Status = status,
            Category = category,
            WorktreeJson = "[]",
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Summary = string.Empty,
        };

    // ── 1. [unit] IsSpawnDoneAsync true when every ws is complete ────────

    [Fact]
    public async Task IsSpawnDoneAsync_True_When_Every_Workstream_Is_Complete()
    {
        const string spawnId = "spawn-stage52-all-complete";
        var (conn, db) = NewInMemoryDb();
        try
        {
            db.Workstreams.AddRange(
                Row("ws-1-impl", spawnId, "complete", WorkstreamCategories.Impl),
                Row("ws-1-e2e", spawnId, "complete", WorkstreamCategories.QaE2e),
                Row("ws-2-impl", spawnId, "complete", WorkstreamCategories.Impl),
                Row("ws-2-e2e", spawnId, "complete", WorkstreamCategories.QaE2e));
            await db.SaveChangesAsync();

            Assert.True(await StorySpawnOrchestrator.IsSpawnDoneAsync(db, spawnId, default));

            // Historic + cancelled rows are excluded from the requirement
            // (they belong to a superseded decomposition / were explicitly
            // operator-cancelled). Add one of each + verify done still
            // holds.
            db.Workstreams.AddRange(
                Row("ws-old-historic", spawnId, WorkstreamStatuses.Historic, WorkstreamCategories.Impl),
                Row("ws-old-cancelled", spawnId, "cancelled", WorkstreamCategories.QaE2e),
                Row("ws-old-paused", spawnId, "paused", WorkstreamCategories.Impl));
            await db.SaveChangesAsync();

            Assert.True(await StorySpawnOrchestrator.IsSpawnDoneAsync(db, spawnId, default));

            // Defensive: an empty workstream set does NOT count as done —
            // an empty decomposition shouldn't auto-flip the spawn.
            const string emptySpawnId = "spawn-stage52-empty";
            Assert.False(await StorySpawnOrchestrator.IsSpawnDoneAsync(db, emptySpawnId, default));
        }
        finally
        {
            db.Dispose();
            conn.Dispose();
        }
    }

    // ── 2. [unit] IsSpawnDoneAsync false when any non-terminal ws ≠ complete ──

    [Fact]
    public async Task IsSpawnDoneAsync_False_When_Any_QaE2e_Workstream_Is_Active()
    {
        const string spawnId = "spawn-stage52-qa-active";
        var (conn, db) = NewInMemoryDb();
        try
        {
            db.Workstreams.AddRange(
                Row("ws-1-impl", spawnId, "complete", WorkstreamCategories.Impl),
                Row("ws-1-e2e", spawnId, "complete", WorkstreamCategories.QaE2e),
                Row("ws-2-impl", spawnId, "complete", WorkstreamCategories.Impl),
                // One qa-e2e still active — done-gate must not fire.
                Row("ws-2-e2e", spawnId, "active", WorkstreamCategories.QaE2e));
            await db.SaveChangesAsync();

            Assert.False(await StorySpawnOrchestrator.IsSpawnDoneAsync(db, spawnId, default));

            // Same shape but with an impl-only blocker — still false.
            const string spawnId2 = "spawn-stage52-impl-active";
            db.Workstreams.AddRange(
                Row("ws3-1-impl", spawnId2, "complete", WorkstreamCategories.Impl),
                Row("ws3-2-impl", spawnId2, "active", WorkstreamCategories.Impl));
            await db.SaveChangesAsync();
            Assert.False(await StorySpawnOrchestrator.IsSpawnDoneAsync(db, spawnId2, default));

            // A workstream stuck in `review` or `merging` also blocks
            // (no row is allowed to be non-terminal-but-not-complete).
            const string spawnId3 = "spawn-stage52-review";
            db.Workstreams.AddRange(
                Row("ws4-1-impl", spawnId3, "complete", WorkstreamCategories.Impl),
                Row("ws4-1-e2e", spawnId3, "review", WorkstreamCategories.QaE2e));
            await db.SaveChangesAsync();
            Assert.False(await StorySpawnOrchestrator.IsSpawnDoneAsync(db, spawnId3, default));

            const string spawnId4 = "spawn-stage52-merging";
            db.Workstreams.AddRange(
                Row("ws5-1-impl", spawnId4, "merging", WorkstreamCategories.Impl),
                Row("ws5-1-e2e", spawnId4, "complete", WorkstreamCategories.QaE2e));
            await db.SaveChangesAsync();
            Assert.False(await StorySpawnOrchestrator.IsSpawnDoneAsync(db, spawnId4, default));
        }
        finally
        {
            db.Dispose();
            conn.Dispose();
        }
    }

    // ── 3. [integration] Last qa-e2e PR merges → Phase=done (skips qa) ──

    [Fact]
    public async Task Last_QaE2e_Pr_Merging_Advances_Spawn_To_Done_Without_Visiting_Qa_Phase()
    {
        const string storyId = "WI-stage52-done-gate";
        const string spawnId = "spawn-" + storyId;
        const string productId = "prod-stage52";
        const string repoId = "repo-stage52";

        using var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        using (f.CreateClient()) { /* boot + apply migrations */ }

        // Seed product + repo + story + spawn at Phase=Review (mimicking
        // the post-impl state) + 2 impl ws (complete + merged PR) + 2
        // qa-e2e ws (merging + matching PR rows that we'll flip to
        // merged below).
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = productId,
                Name = "Stage 5.2",
                Slug = "stage52",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Repos.Add(new RepoRow
            {
                Id = repoId,
                ProductId = productId,
                DisplayName = "Stage 5.2 repo",
                Kind = "local",
                RemoteUrl = "file://stage52",
                CloneRoot = "/tmp/stage52",
                DefaultBranch = "main",
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = storyId,
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "Stage 5.2 done-gate",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = "Stage 5.2 done-gate",
                Phase = StorySpawnPhases.Review,
                PlanBranch = $"story/{storyId}/plan",
                PlanPrId = "PR-stage52-plan",
                RepoId = repoId,
                BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 3,
                AutoMerge = true,
            });

            void AddRow(string id, string status, string category, string? prId)
            {
                var prsJson = prId is null ? "[]" : JsonSerializer.Serialize(new[] { prId });
                db.Workstreams.Add(new WorkstreamRow
                {
                    Id = id,
                    Sort = id.GetHashCode(),
                    RepoId = repoId,
                    Name = id,
                    Branch = $"ws/{id}",
                    Base = "main",
                    Epic = string.Empty,
                    Status = status,
                    Category = category,
                    WorktreeJson = "[]",
                    AgentsJson = "[]",
                    PrsJson = prsJson,
                    CardsJson = "[]",
                    Summary = string.Empty,
                    StorySpawnId = spawnId,
                    PlanAnchor = $"phase-foundation/stage-{id}",
                    PhaseAnchor = "phase-foundation",
                });
            }
            AddRow("s1-impl", "complete", WorkstreamCategories.Impl, "PR-stage52-impl-1");
            AddRow("s2-impl", "complete", WorkstreamCategories.Impl, "PR-stage52-impl-2");
            // qa-e2e workstreams sit in `merging` with their PR rows
            // about-to-flip-to-merged.
            AddRow("s1-e2e", "merging", WorkstreamCategories.QaE2e, "PR-stage52-e2e-1");
            AddRow("s2-e2e", "merging", WorkstreamCategories.QaE2e, "PR-stage52-e2e-2");

            void AddMergedPr(string prId, string wsId) => db.Prs.Add(new PrRow
            {
                Id = prId,
                RepoId = repoId,
                Col = "merged",
                Title = "[impl] " + wsId,
                Branch = $"ws/{wsId}",
                Base = "main",
                Workstream = wsId,
                Epic = string.Empty,
                EpicColor = string.Empty,
                AuthorJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                IsActive = false,
                LastSyncStatus = "idle",
                Provider = "local",
                WorkItemId = wsId,
                Description = "seed",
            });
            // The two qa-e2e PRs are also already merged on the provider
            // side — the orchestrator just hasn't observed the
            // merging→complete transition yet. DriveReviewAsync will
            // flip both qa-e2e workstreams to complete, then the Stage
            // 5.2 gate fires on the second flip.
            AddMergedPr("PR-stage52-impl-1", "s1-impl");
            AddMergedPr("PR-stage52-impl-2", "s2-impl");
            AddMergedPr("PR-stage52-e2e-1", "s1-e2e");
            AddMergedPr("PR-stage52-e2e-2", "s2-e2e");
            // Seed plan-doc verdict=pass rows so the implement-side
            // gate doesn't regress us to Plan. (The Stage 5.1 test uses
            // the same trick — DriveImplementAsync's
            // ArePlanDocsAllPassedAsync check.)
            foreach (var fileName in new[] { "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md" })
            {
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = $"plan-doc:{spawnId}:{fileName}",
                    CrewId = "crew-arch",
                    GeneratorAgentId = "G",
                    EvaluatorAgentId = "E",
                    Iter = 1,
                    Score = 95,
                    DimsJson = "{}",
                    Verdict = "pass",
                    Notes = string.Empty,
                    GeneratorSummary = string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        // Track every Phase transition the orchestrator writes so we can
        // assert it NEVER visited `qa` along the way. Read spawn before
        // + after the drive.
        StorySpawnRow before;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            before = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
        }
        Assert.Equal(StorySpawnPhases.Review, before.Phase);

        // Drive the orchestrator. DriveReviewAsync sees both qa-e2e
        // workstreams in `merging` with a merged PR row → flips both to
        // `complete` → the Stage 5.2 gate fires after the second flip →
        // spawn.Phase = `done`.
        using (var scope = f.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orch.DriveAsync(storyId);
        }

        // Read back: spawn must be at Done.
        StorySpawnRow after;
        List<WorkstreamRow> finalWorkstreams;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            after = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
            finalWorkstreams = await db.Workstreams.AsNoTracking()
                .Where(w => w.StorySpawnId == spawnId)
                .ToListAsync();
        }

        Assert.Equal(StorySpawnPhases.Done, after.Phase);

        // Every workstream is complete — no qa-e2e left merging.
        Assert.All(finalWorkstreams, w => Assert.Equal("complete", w.Status));

        // Crucially: spawn.QaBranch + spawn.QaPrId were never written
        // (DriveQaAsync was never invoked — we skipped Phase=Qa entirely
        // via the Stage 5.2 gate inside DriveReviewAsync).
        Assert.Null(after.QaBranch);
        Assert.Null(after.QaPrId);
    }
}
