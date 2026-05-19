// -----------------------------------------------------------------------
// <copyright file="Stage53LegacyQaDrainTests.cs" company="Microsoft Corp.">
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
/// docs/forge/e2e.md Stage 5.3 — legacy <c>Phase='qa'</c> drain path
/// stays alive for spawns paused mid-flight under a pre-Stage-3.1
/// Forge version, but is short-circuited for new-shape spawns that
/// carry qa-e2e workstreams (Stage 3.1+ decomposer output).
///
/// <para>The legacy path is recognised by <c>spawn.QaPrId != null</c>
/// (the marker the old <c>DriveQaAsync</c> wrote when it opened the
/// batch QA PR). The new-shape no-op fires when <c>QaPrId</c> is null
/// AND the workstream set carries at least one
/// <see cref="WorkstreamCategories.QaE2e"/> row — proving the
/// decomposer emitted the per-stage qa-e2e workstreams Stage 5.1's
/// routing handles.</para>
/// </summary>
[Collection("Phase28")]
public class Stage53LegacyQaDrainTests
{
    private const string ProductId = "prod-stage53";
    private const string RepoId = "repo-stage53";

    private static (ForgeWebApplicationFactory factory, string storyId, string spawnId) Boot(
        string suffix, string? qaPrId)
    {
        var factory = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        using (factory.CreateClient()) { /* boot + migrations */ }

        var storyId = $"WI-stage53-{suffix}";
        var spawnId = $"spawn-{storyId}";

        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        if (!control.Products.Any(p => p.Id == ProductId))
        {
            control.Products.Add(new ProductRow
            {
                Id = ProductId, Name = "Stage 5.3", Slug = "stage53",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (!control.Repos.Any(r => r.Id == RepoId))
        {
            control.Repos.Add(new RepoRow
            {
                Id = RepoId, ProductId = ProductId,
                DisplayName = "Stage 5.3 repo", Kind = "local",
                RemoteUrl = "file://stage53", CloneRoot = "/tmp/stage53",
                DefaultBranch = "main",
            });
        }
        db.WorkItems.Add(new WorkItemRow
        {
            Id = storyId, Provider = "ado", Track = "dev", Type = "feature",
            State = "active", Title = "Stage 5.3 legacy drain",
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId, StoryId = storyId,
            StoryTitle = "Stage 5.3 legacy drain",
            Phase = StorySpawnPhases.Qa,
            PlanBranch = $"story/{storyId}/plan",
            PlanPrId = $"PR-stage53-plan-{suffix}",
            RepoId = RepoId, BaseBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            StoryPoints = 3,
            QaBranch = qaPrId is null ? null : $"story/{storyId}/qa",
            QaPrId = qaPrId,
            AutoMerge = true,
        });
        control.SaveChanges();
        db.SaveChanges();
        return (factory, storyId, spawnId);
    }

    private static WorkstreamRow LegacyImplRow(string id, int sort, string spawnId, string status)
    {
        return new WorkstreamRow
        {
            Id = id, Sort = sort, RepoId = RepoId,
            Name = id, Branch = $"ws/{id}", Base = "main",
            Epic = string.Empty, Status = status,
            Category = WorkstreamCategories.Impl,
            WorktreeJson = "[]", AgentsJson = "[]",
            PrsJson = "[]", CardsJson = "[]",
            Summary = string.Empty, StorySpawnId = spawnId,
            // Legacy spawns wrote PlanAnchor=null on workstreams that
            // were created before Phase 42 + Stage 3.1. Mimic that here.
        };
    }

    private static WorkstreamRow NewShapeQaE2eRow(string id, int sort, string spawnId)
    {
        return new WorkstreamRow
        {
            Id = id, Sort = sort, RepoId = RepoId,
            Name = id, Branch = $"ws/{id}", Base = "main",
            Epic = string.Empty, Status = "planning",
            Category = WorkstreamCategories.QaE2e,
            WorktreeJson = "[]", AgentsJson = "[]",
            PrsJson = "[]", CardsJson = "[]",
            Summary = string.Empty, StorySpawnId = spawnId,
            PlanAnchor = "phase-foundation/stage-setup" + CrewStoryArtifactStrategy.QaE2eAnchorSuffix,
            PhaseAnchor = "phase-foundation",
        };
    }

    private static async Task SeedAsync(ForgeWebApplicationFactory f, params WorkstreamRow[] rows)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.Workstreams.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static async Task SeedMergedPrAsync(ForgeWebApplicationFactory f, string prId, string branch, string? wsId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.Prs.Add(new PrRow
        {
            Id = prId, RepoId = RepoId, Col = "merged",
            Title = "[qa] seeded", Branch = branch, Base = "main",
            Workstream = wsId ?? string.Empty, Epic = string.Empty, EpicColor = string.Empty,
            AuthorJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            IsActive = false, LastSyncStatus = "idle",
            Provider = "local", WorkItemId = wsId,
            Description = "seeded merged PR",
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedPlanDocPassesAsync(ForgeWebApplicationFactory f, string spawnId)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        foreach (var fileName in new[] { "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md" })
        {
            var docId = $"plan-doc:{spawnId}:{fileName}";
            if (await db.Deliverables.AnyAsync(d => d.WorkItemId == docId && d.Verdict == "pass")) continue;
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = docId, CrewId = "crew-arch",
                GeneratorAgentId = "G", EvaluatorAgentId = "E",
                Iter = 1, Score = 95, DimsJson = "{}",
                Verdict = "pass", Notes = string.Empty,
                GeneratorSummary = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
    }

    private static async Task DriveAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
        await orch.DriveAsync(storyId);
    }

    private static async Task<StorySpawnRow> ReadSpawnAsync(ForgeWebApplicationFactory f, string spawnId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
    }

    private static async Task<List<PrRow>> ReadAllPrsAsync(ForgeWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.Prs.AsNoTracking()
            .OrderBy(p => p.CreatedAt).ToListAsync();
    }

    // ── 1. [unit] Legacy drain: Phase=qa + QaPrId set → step-def PRs land ──

    [Fact]
    public async Task Legacy_Spawn_With_QaPrId_Set_Continues_To_Open_StepDef_Prs()
    {
        var (f, storyId, spawnId) = Boot("legacy-set", qaPrId: "PR-stage53-legacy-qa");
        try
        {
            // 2 legacy impl workstreams, no qa-e2e rows (proves this is a
            // pre-Stage-3.1 spawn that never went through Stage 3.1's
            // qa-e2e emission).
            await SeedAsync(f,
                LegacyImplRow("ws-stage53-legacy-set-impl-1", sort: 10, spawnId, status: "complete"),
                LegacyImplRow("ws-stage53-legacy-set-impl-2", sort: 20, spawnId, status: "complete"));

            // Legacy QA PR is already merged so DriveQaAsync's Step 1
            // (open QA feature PR) is skipped and Step 2 (step-def PRs)
            // claims this tick.
            await SeedMergedPrAsync(f, "PR-stage53-legacy-qa", "story/" + storyId + "/qa", wsId: null);
            await SeedPlanDocPassesAsync(f, spawnId);

            await DriveAsync(f, storyId);

            // Step-def PRs opened — one per workstream, named `[qa-steps] ...`.
            var prs = await ReadAllPrsAsync(f);
            var stepDefPrs = prs.Where(p => p.Title.StartsWith("[qa-steps]", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, stepDefPrs.Count);
            Assert.Contains(stepDefPrs, p => p.Workstream == "ws-stage53-legacy-set-impl-1");
            Assert.Contains(stepDefPrs, p => p.Workstream == "ws-stage53-legacy-set-impl-2");

            // The spawn should have advanced to Done once every legacy
            // workstream's step-def PR merged + flipped them to complete.
            var after = await ReadSpawnAsync(f, spawnId);
            Assert.Equal(StorySpawnPhases.Done, after.Phase);
        }
        finally { f.Dispose(); }
    }

    // ── 2. [unit] No-op: Phase=qa + QaPrId null + has qa-e2e → returns false ──

    [Fact]
    public async Task NewShape_Spawn_Reaching_Phase_Qa_Without_QaPrId_NoOps_DriveQa()
    {
        var (f, storyId, spawnId) = Boot("new-shape", qaPrId: null);
        try
        {
            // 1 impl + 1 qa-e2e workstream — the new-shape signal. Both
            // are pre-complete to mimic a spawn that ran the full
            // review-side flow but somehow ended up at Phase=qa.
            await SeedAsync(f,
                LegacyImplRow("ws-stage53-new-shape-impl", sort: 10, spawnId, status: "complete"),
                NewShapeQaE2eRow("ws-stage53-new-shape-e2e", sort: 11, spawnId));

            await SeedPlanDocPassesAsync(f, spawnId);

            // Capture initial state.
            var before = await ReadSpawnAsync(f, spawnId);
            Assert.Equal(StorySpawnPhases.Qa, before.Phase);
            Assert.Null(before.QaPrId);
            Assert.Null(before.QaBranch);

            await DriveAsync(f, storyId);

            // DriveQaAsync's Stage 5.3 guard fired: no QA branch / PR
            // was created, no step-def PRs opened. The legacy batch path
            // stayed parked. (DriveImplementAsync + DriveReviewAsync ran
            // alongside; the qa-e2e workstream's review-side drain may
            // have advanced it but the spawn's QaBranch / QaPrId must
            // stay null — that's the headline assertion: the new-shape
            // workstream set was NOT routed through the legacy batch
            // path.)
            var after = await ReadSpawnAsync(f, spawnId);
            Assert.Null(after.QaBranch);
            Assert.Null(after.QaPrId);

            // No `[qa]` story-level feature PR was opened (the legacy
            // Step-1 artefact that the new-shape spawn must NOT emit).
            var prs = await ReadAllPrsAsync(f);
            Assert.DoesNotContain(prs,
                p => p.Title.StartsWith("[qa] ", StringComparison.Ordinal));
        }
        finally { f.Dispose(); }
    }

    // ── 3. [integration] Full legacy drain to done ───────────────────────

    [Fact]
    public async Task Legacy_Spawn_From_PreStage31_Drains_To_Done_Via_DriveQa()
    {
        // This integration test models a spawn that was paused mid-drain
        // under an older Forge version: it sits at Phase=qa with QaBranch
        // already set but QaPrId still null (mid-Step-1, before the PR
        // open succeeded). Driving the orchestrator should:
        //   - run Step 1: produce qa features + open the QA PR
        //   - run Step 2 on the next tick: open step-def PRs per ws
        //   - advance to Done once every ws is complete
        var (f, storyId, spawnId) = Boot("full-drain", qaPrId: null);
        try
        {
            // Clear the QaBranch + reset the spawn so it's like a brand-
            // new pre-Stage-3.1 spawn entering Phase=qa for the first
            // time. Only impl rows (no qa-e2e) — proves we're on the
            // legacy code path.
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var s = await db.StorySpawns.FirstAsync(x => x.Id == spawnId);
                s.QaBranch = null;
                s.QaPrId = null;
                await db.SaveChangesAsync();
            }

            await SeedAsync(f,
                LegacyImplRow("ws-stage53-full-impl-1", sort: 10, spawnId, status: "complete"),
                LegacyImplRow("ws-stage53-full-impl-2", sort: 20, spawnId, status: "complete"));
            await SeedPlanDocPassesAsync(f, spawnId);

            // Tick 1 — DriveQaAsync Step 1 opens the QA PR (auto-merged
            // inline via ApproveAndMergeAsync), sets spawn.QaPrId.
            await DriveAsync(f, storyId);
            var afterTick1 = await ReadSpawnAsync(f, spawnId);
            Assert.NotNull(afterTick1.QaPrId);
            Assert.NotNull(afterTick1.QaBranch);

            var prsAfterTick1 = await ReadAllPrsAsync(f);
            Assert.Contains(prsAfterTick1,
                p => p.Title.StartsWith("[qa] ", StringComparison.Ordinal) && p.Col == "merged");

            // Tick 2 — DriveQaAsync Step 2: per-ws step-def PRs open +
            // auto-merge. Workstreams flip to complete; spawn advances
            // to Done.
            await DriveAsync(f, storyId);

            var afterTick2 = await ReadSpawnAsync(f, spawnId);
            Assert.Equal(StorySpawnPhases.Done, afterTick2.Phase);

            var prsAfterTick2 = await ReadAllPrsAsync(f);
            var stepDefPrs = prsAfterTick2
                .Where(p => p.Title.StartsWith("[qa-steps]", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, stepDefPrs.Count);
            Assert.All(stepDefPrs, p => Assert.Equal("merged", p.Col));
        }
        finally { f.Dispose(); }
    }
}
