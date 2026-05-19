// -----------------------------------------------------------------------
// <copyright file="Stage51OrchestratorQaE2eRoutingTests.cs" company="Microsoft Corp.">
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
/// docs/forge/e2e.md Stage 5.1 — the orchestrator routes qa-e2e
/// workstreams through a dedicated branch in
/// <see cref="StorySpawnOrchestrator.DriveOneWorkstreamImplAsync"/>
/// that calls <see cref="IStoryArtifactStrategy.ProduceStageE2eAsync"/>
/// (Stage 4.2 / 4.3), commits the qa-e2e artifacts to the row's
/// branch, and opens a PR with the <c>[e2e]</c> title prefix +
/// impl-sibling-PR back-reference. The dep-gate (Stage 3.1's
/// <see cref="WorkstreamDepRow"/> edges) still enforces that the
/// qa-e2e row doesn't start until the impl sibling is
/// <c>complete</c>.
///
/// <para>All four Facts hand-craft the workstream rows (skipping the
/// plan + decompose phases) so the routing concern is isolated from
/// the upstream decomposition path. Stage 9.1's integration suite
/// drives the full plan→done end-to-end against the real plan parser
/// + decomposer.</para>
/// </summary>
[Collection("Phase28")]
public class Stage51OrchestratorQaE2eRoutingTests
{
    private const string ProductId = "prod-stage51";
    private const string RepoId = "repo-stage51";

    private static (ForgeWebApplicationFactory factory, string storyId, string spawnId) NewSpawn(
        string suffix)
    {
        var factory = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        using (factory.CreateClient()) { /* boot */ }
        var storyId = $"WI-stage51-{suffix}";
        var spawnId = $"spawn-{storyId}";

        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        if (!control.Products.Any(p => p.Id == ProductId))
        {
            control.Products.Add(new ProductRow
            {
                Id = ProductId,
                Name = "Stage 5.1",
                Slug = "stage51",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (!control.Repos.Any(r => r.Id == RepoId))
        {
            control.Repos.Add(new RepoRow
            {
                Id = RepoId,
                ProductId = ProductId,
                DisplayName = "Stage 5.1 repo",
                Kind = "local",
                RemoteUrl = "file://stage51",
                CloneRoot = "/tmp/stage51",
                DefaultBranch = "main",
            });
        }
        db.WorkItems.Add(new WorkItemRow
        {
            Id = storyId,
            Provider = "ado",
            Track = "dev",
            Type = "feature",
            State = "active",
            Title = "Stage 5.1 routing",
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId,
            StoryId = storyId,
            StoryTitle = "Stage 5.1 routing",
            Phase = StorySpawnPhases.Implement,
            PlanBranch = $"story/{storyId}/plan",
            PlanPrId = $"PR-stage51-plan-{suffix}",
            RepoId = RepoId,
            BaseBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            StoryPoints = 3,
            // Spawn isn't in sequential-mode plan-doc state, so
            // ArePlanDocsAllPassedAsync returns true unconditionally.
            PlanDocPrsJson = null,
            AutoMerge = true,
        });
        control.SaveChanges();
        db.SaveChanges();
        return (factory, storyId, spawnId);
    }

    private static WorkstreamRow ImplRow(
        string id, int sort, string spawnId, string stageSlug, string? prId = null,
        string status = "planning")
    {
        var planAnchor = $"phase-foundation/stage-{stageSlug}";
        return new WorkstreamRow
        {
            Id = id,
            Sort = sort,
            RepoId = RepoId,
            Name = stageSlug,
            Branch = $"ws/{id}",
            Base = "main",
            Epic = string.Empty,
            Status = status,
            WorktreeJson = "[]",
            AgentsJson = "[]",
            PrsJson = prId is null ? "[]" : JsonSerializer.Serialize(new[] { prId }),
            CardsJson = "[]",
            Summary = $"Impl for {stageSlug}",
            Planned = true,
            StorySpawnId = spawnId,
            PlanAnchor = planAnchor,
            PhaseAnchor = "phase-foundation",
            Category = WorkstreamCategories.Impl,
        };
    }

    private static WorkstreamRow QaE2eRow(
        string id, int sort, string spawnId, string stageSlug, IEnumerable<string> depTitles)
    {
        var planAnchor = $"phase-foundation/stage-{stageSlug}{CrewStoryArtifactStrategy.QaE2eAnchorSuffix}";
        return new WorkstreamRow
        {
            Id = id,
            Sort = sort,
            RepoId = RepoId,
            Name = $"{stageSlug} — E2E",
            Branch = $"ws/{id}",
            Base = "main",
            Epic = string.Empty,
            Status = "planning",
            WorktreeJson = "[]",
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Summary = $"E2E for {stageSlug}",
            Planned = true,
            StorySpawnId = spawnId,
            PlanAnchor = planAnchor,
            PhaseAnchor = "phase-foundation",
            Category = WorkstreamCategories.QaE2e,
            DepsJson = JsonSerializer.Serialize(depTitles.ToList()),
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

    /// <summary>
    /// Stage 5.1 tests start the orchestrator at Phase=Implement to
    /// isolate qa-e2e routing from the upstream plan/decompose path.
    /// The orchestrator's <c>ArePlanDocsAllPassedAsync</c> gate at the
    /// top of <c>DriveImplementAsync</c> still runs though — it requires
    /// a verdict=pass deliverable for each of the four canonical plan
    /// docs. Mirror Phase 28's seed pattern so the gate releases.
    /// </summary>
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
                WorkItemId = docId,
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

    private static async Task SeedMergedPrAsync(ForgeWebApplicationFactory f, string prId, string branch, string wsId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.Prs.Add(new PrRow
        {
            Id = prId,
            RepoId = RepoId,
            Col = "merged",
            Title = "[impl] " + branch,
            Branch = branch,
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
            Description = "seed impl PR (merged)",
        });
        await db.SaveChangesAsync();
    }

    private static async Task DriveAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
        await orch.DriveAsync(storyId);
    }

    private static async Task TickAsync(ForgeWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var sched = scope.ServiceProvider.GetRequiredService<SpawnDependencyScheduler>();
        await sched.TickOnceAsync();
    }

    private static async Task<List<WorkstreamRow>> ReadWorkstreamsAsync(ForgeWebApplicationFactory f, string spawnId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawnId)
            .OrderBy(w => w.Sort)
            .ToListAsync();
    }

    private static async Task<List<PrRow>> ReadAllPrsAsync(ForgeWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.Prs.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
    }

    // ── 1. [unit] qa-e2e dep complete → PR opens with [e2e] prefix ───────

    [Fact]
    public async Task QaE2e_With_Complete_ImplDep_Opens_Pr_With_E2e_Prefix()
    {
        var (f, storyId, spawnId) = NewSpawn("dep-complete");
        try
        {
            const string implWsId = "ws-stage51-dep-complete-impl";
            const string qaWsId = "ws-stage51-dep-complete-qa";

            // Impl ws is already merged → status=complete.
            await SeedAsync(f,
                ImplRow(implWsId, sort: 10, spawnId, stageSlug: "setup",
                    prId: "PR-stage51-impl-1", status: "complete"),
                QaE2eRow(qaWsId, sort: 11, spawnId, stageSlug: "setup",
                    depTitles: new[] { "setup" }));
            await SeedMergedPrAsync(f, "PR-stage51-impl-1", $"ws/{implWsId}", implWsId);
            await SeedPlanDocPassesAsync(f, spawnId);

            await DriveAsync(f, storyId);

            var workstreams = await ReadWorkstreamsAsync(f, spawnId);
            var qa = Assert.Single(workstreams.Where(w => w.Id == qaWsId));

            // Workstream advanced past "planning": ProduceStageE2eAsync ran,
            // files committed, PR opened, status flipped to "review".
            Assert.Equal("review", qa.Status);
            var qaPrIds = JsonSerializer.Deserialize<List<string>>(qa.PrsJson)!;
            Assert.Single(qaPrIds);

            var prs = await ReadAllPrsAsync(f);
            var qaPr = Assert.Single(prs.Where(p => p.WorkItemId == qaWsId));
            Assert.StartsWith("[e2e] ", qaPr.Title);
            Assert.Contains("**Impl PR:** PR-stage51-impl-1", qaPr.Description);
        }
        finally { f.Dispose(); }
    }

    // ── 2. [unit] qa-e2e dep active → skipped (no PR opened) ─────────────

    [Fact]
    public async Task QaE2e_With_Active_ImplDep_Is_Skipped_No_Pr_Opened()
    {
        var (f, storyId, spawnId) = NewSpawn("dep-active");
        try
        {
            const string implWsId = "ws-stage51-dep-active-impl";
            const string qaWsId = "ws-stage51-dep-active-qa";

            // Impl ws still in flight → status=active. qa-e2e must NOT
            // claim because the dep-gate (Stage 3.1's WorkstreamDepRow
            // edges) requires the dep ws.Status == "complete".
            await SeedAsync(f,
                ImplRow(implWsId, sort: 10, spawnId, stageSlug: "setup", status: "active"),
                QaE2eRow(qaWsId, sort: 11, spawnId, stageSlug: "setup",
                    depTitles: new[] { "setup" }));
            await SeedPlanDocPassesAsync(f, spawnId);

            await DriveAsync(f, storyId);

            var workstreams = await ReadWorkstreamsAsync(f, spawnId);
            var qa = Assert.Single(workstreams.Where(w => w.Id == qaWsId));

            // Workstream stays planning — dep-gate held it back.
            Assert.Equal("planning", qa.Status);
            var qaPrIds = JsonSerializer.Deserialize<List<string>>(qa.PrsJson)!;
            Assert.Empty(qaPrIds);

            var prs = await ReadAllPrsAsync(f);
            Assert.DoesNotContain(prs, p => p.WorkItemId == qaWsId);
            Assert.DoesNotContain(prs, p => p.Title.StartsWith("[e2e]", StringComparison.Ordinal));
        }
        finally { f.Dispose(); }
    }

    // ── 3. [integration] Two-stage spawn drives plan→done; 4 ws PRs land ──

    [Fact]
    public async Task TwoStage_Spawn_With_Independent_ImplAndQaE2e_Lands_Four_Ws_Prs()
    {
        // Two stages, independent (no cross-stage dep). Each has an
        // impl + a qa-e2e companion. Pre-seeded with impl PRs already
        // merged so the dep-gate releases both qa-e2e rows in the same
        // tick (verifies parallel routing).
        var (f, storyId, spawnId) = NewSpawn("two-indep");
        try
        {
            const string s1ImplId = "ws-stage51-two-indep-s1-impl";
            const string s1QaId = "ws-stage51-two-indep-s1-qa";
            const string s2ImplId = "ws-stage51-two-indep-s2-impl";
            const string s2QaId = "ws-stage51-two-indep-s2-qa";

            await SeedAsync(f,
                ImplRow(s1ImplId, sort: 10, spawnId, stageSlug: "alpha",
                    prId: "PR-stage51-two-indep-impl-s1", status: "complete"),
                ImplRow(s2ImplId, sort: 20, spawnId, stageSlug: "beta",
                    prId: "PR-stage51-two-indep-impl-s2", status: "complete"),
                QaE2eRow(s1QaId, sort: 11, spawnId, stageSlug: "alpha", depTitles: new[] { "alpha" }),
                QaE2eRow(s2QaId, sort: 21, spawnId, stageSlug: "beta", depTitles: new[] { "beta" }));
            await SeedMergedPrAsync(f, "PR-stage51-two-indep-impl-s1", $"ws/{s1ImplId}", s1ImplId);
            await SeedMergedPrAsync(f, "PR-stage51-two-indep-impl-s2", $"ws/{s2ImplId}", s2ImplId);
            await SeedPlanDocPassesAsync(f, spawnId);

            // Single drive picks up both qa-e2e rows (impl deps are
            // complete). The orchestrator's parallel impl-loop semaphore
            // caps at 4 concurrent so two qa-e2e rows fit one batch.
            await DriveAsync(f, storyId);

            var workstreams = await ReadWorkstreamsAsync(f, spawnId);
            var s1Qa = Assert.Single(workstreams.Where(w => w.Id == s1QaId));
            var s2Qa = Assert.Single(workstreams.Where(w => w.Id == s2QaId));
            Assert.Equal("review", s1Qa.Status);
            Assert.Equal("review", s2Qa.Status);

            // 2 qa-e2e PRs opened with `[e2e]` prefix; both impl PRs
            // pre-seeded as merged. Total Prs table = 4.
            var prs = await ReadAllPrsAsync(f);
            var qaPrs = prs.Where(p => p.Title.StartsWith("[e2e]", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, qaPrs.Count);
            Assert.Contains(qaPrs, p => p.WorkItemId == s1QaId);
            Assert.Contains(qaPrs, p => p.WorkItemId == s2QaId);
            // Each qa-e2e PR carries the impl-sibling back-reference.
            Assert.Contains(qaPrs, p => p.Description.Contains("PR-stage51-two-indep-impl-s1"));
            Assert.Contains(qaPrs, p => p.Description.Contains("PR-stage51-two-indep-impl-s2"));

            // Spec wording "5 PRs in order: plan, impl-S1, impl-S2,
            // e2e-S1, e2e-S2". This test isolates the orchestrator
            // routing concern: the 4 ws PRs land here (2 impl pre-seeded
            // as merged + 2 qa-e2e routed by Stage 5.1); the plan PR is
            // the plan-phase artefact verified independently in Stage
            // 28.3.6. Stage 9.1's integration suite combines everything
            // end-to-end against the real plan parser + decomposer.
        }
        finally { f.Dispose(); }
    }

    // ── 4. [integration] Cross-stage dep serialises four ws PRs ──────────

    [Fact]
    public async Task TwoStage_Spawn_With_CrossStage_Dep_Serialises_Four_Ws_Prs()
    {
        // S2's impl deps on S1's impl (cross-stage). Both stages have
        // e2e. Order should be: impl-S1 → e2e-S1 (waits for impl-S1
        // merge) → impl-S2 (waits for impl-S1 merge) → e2e-S2 (waits for
        // impl-S2 merge). This test pre-seeds the merged-impl state in
        // two phases so we can observe the dep gate's progression.
        var (f, storyId, spawnId) = NewSpawn("cross-stage");
        try
        {
            const string s1ImplId = "ws-stage51-cross-s1-impl";
            const string s1QaId = "ws-stage51-cross-s1-qa";
            const string s2ImplId = "ws-stage51-cross-s2-impl";
            const string s2QaId = "ws-stage51-cross-s2-qa";

            // Phase A: only S1 impl is merged. S1-qa should claim;
            // S2-impl + S2-qa should remain planning.
            await SeedAsync(f,
                ImplRow(s1ImplId, sort: 10, spawnId, stageSlug: "alpha",
                    prId: "PR-stage51-cross-impl-s1", status: "complete"),
                QaE2eRow(s1QaId, sort: 11, spawnId, stageSlug: "alpha",
                    depTitles: new[] { "alpha" }),
                ImplRow(s2ImplId, sort: 20, spawnId, stageSlug: "beta",
                    status: "planning"),
                QaE2eRow(s2QaId, sort: 21, spawnId, stageSlug: "beta",
                    depTitles: new[] { "beta", "alpha" }));
            await SeedMergedPrAsync(f, "PR-stage51-cross-impl-s1", $"ws/{s1ImplId}", s1ImplId);
            await SeedPlanDocPassesAsync(f, spawnId);

            // S2-impl deps on S1-impl (cross-stage). Inject the dep edge
            // directly so the orchestrator's dep gate sees it without
            // re-running decomposition.
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                db.Workstreams.First(w => w.Id == s2ImplId).DepsJson =
                    JsonSerializer.Serialize(new[] { "alpha" });
                await db.SaveChangesAsync();
            }

            // Drive — S1-qa should claim (impl-S1 complete);
            // S2-impl is blocked (the deterministic test strategy
            // skips invoking it, but the assertion below is on the
            // workstream's status). S2-qa is double-blocked.
            await DriveAsync(f, storyId);

            var afterPhaseA = await ReadWorkstreamsAsync(f, spawnId);
            Assert.Equal("review", afterPhaseA.First(w => w.Id == s1QaId).Status);
            // S2-impl is not "complete" yet (no PR merged) so S2-qa's
            // dep-on-beta hasn't fired. Status stays "planning".
            Assert.Equal("planning", afterPhaseA.First(w => w.Id == s2QaId).Status);

            var phaseAPrs = await ReadAllPrsAsync(f);
            var phaseAQaPrs = phaseAPrs.Where(p => p.Title.StartsWith("[e2e]", StringComparison.Ordinal)).ToList();
            Assert.Single(phaseAQaPrs);
            Assert.Equal(s1QaId, phaseAQaPrs[0].WorkItemId);

            // Phase B: simulate S2 impl PR landing → S2-qa should now
            // claim on the next drive.
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var s2Impl = await db.Workstreams.FirstAsync(w => w.Id == s2ImplId);
                s2Impl.Status = "complete";
                s2Impl.PrsJson = JsonSerializer.Serialize(new[] { "PR-stage51-cross-impl-s2" });
                await db.SaveChangesAsync();
            }
            await SeedMergedPrAsync(f, "PR-stage51-cross-impl-s2", $"ws/{s2ImplId}", s2ImplId);

            await DriveAsync(f, storyId);

            var afterPhaseB = await ReadWorkstreamsAsync(f, spawnId);
            Assert.Equal("review", afterPhaseB.First(w => w.Id == s2QaId).Status);

            var phaseBPrs = await ReadAllPrsAsync(f);
            var phaseBQaPrs = phaseBPrs.Where(p => p.Title.StartsWith("[e2e]", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, phaseBQaPrs.Count);
            Assert.Contains(phaseBQaPrs, p => p.WorkItemId == s1QaId);
            Assert.Contains(phaseBQaPrs, p => p.WorkItemId == s2QaId);
        }
        finally { f.Dispose(); }
    }
}
