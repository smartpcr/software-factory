// -----------------------------------------------------------------------
// <copyright file="Phase28E2eRefactorTests.cs" company="Microsoft Corp.">
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
/// docs/forge/e2e.md Stage 9.1 — integration sweep covering the headline
/// end-to-end scenarios for the Phase 28 → e2e-refactor migration. These
/// tests pre-seed a story spawn at <c>Phase=Implement</c> with hand-
/// crafted workstreams (impl + trailing qa-e2e per Stage 3.1's decomposer
/// shape) and drive the orchestrator through to <c>Done</c>, asserting
/// the new routing + dep-gate + done-gate contracts hold end-to-end:
///
/// <list type="bullet">
///   <item>3-stage all-e2e + 3-stage stage-2-no-e2e PR-count assertions
///         (Sc1 + Sc2) — proves the decomposer's "qa-e2e only when the
///         stage has Test Scenarios" rule survives orchestrator
///         scheduling.</item>
///   <item>Dep gate honours impl-sibling merge ordering even when an
///         out-of-order downstream impl merged earlier (Sc3).</item>
///   <item>Cross-stage e2e deps from Stage 2.1's H3 parser also gate
///         qa-e2e claims (Sc4).</item>
///   <item>Restart resilience — mid-qa-e2e crash + restart still drives
///         to Done (Sc5).</item>
///   <item>New-shape spawns NEVER transition through <c>Phase='qa'</c>
///         (Sc6) — the legacy phase is drained for back-compat only
///         (Sc7).</item>
/// </list>
///
/// <para>The collection name <c>Phase28E2eRefactor</c> isolates these
/// from <c>Phase28</c> (the legacy gate-only suite) and <c>Phase28</c>
/// integration tests so they can run in parallel without DB collisions.
/// The deterministic <see cref="DeterministicStoryArtifactStrategy"/>
/// is registered via <c>ForgeWebApplicationFactory.WithOrchestratorEnabled()</c>
/// — no real LLM invocations.</para>
///
/// <para>Why hand-craft workstreams instead of driving from
/// <c>Phase=Plan</c>: the deterministic strategy's
/// <c>ProduceDecompositionAsync</c> returns a hardcoded 2-workstream
/// (api + ui) shape with NO qa-e2e companions — it doesn't run
/// <c>CrewStoryArtifactStrategy.BuildDecompositionFromPlan</c>'s
/// plan-parser-driven emission. To exercise the full impl+qa-e2e
/// routing path against deterministic content, the test pre-seeds the
/// workstream rows in the shape Stage 3.1's decomposer would produce
/// against a real plan, then drives the orchestrator from
/// <c>Phase=Implement</c>. The plan PR is pre-seeded as a merged row
/// so the "1 plan + N impl + M qa-e2e" total still includes it.</para>
/// </summary>
[Collection("Phase28E2eRefactor")]
public class Phase28E2eRefactorTests
{
    private const string ProductId = "prod-phase28-e2e-refactor";
    private const string RepoId = "repo-phase28-e2e-refactor";

    // ── factory + spawn seed ─────────────────────────────────────────────

    private static (ForgeWebApplicationFactory factory, string storyId, string spawnId) NewSpawn(
        string suffix, string phase = "implement", string? qaPrId = null)
    {
        var factory = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        using (factory.CreateClient()) { /* boot + migrations */ }

        var storyId = $"WI-phase28-e2e-{suffix}";
        var spawnId = $"spawn-{storyId}";

        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        if (!control.Products.Any(p => p.Id == ProductId))
        {
            control.Products.Add(new ProductRow
            {
                Id = ProductId,
                Name = "Phase 28 E2E refactor",
                Slug = "phase28e2erefactor",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (!db.Repos.Any(r => r.Id == RepoId))
        {
            db.Repos.Add(new RepoRow
            {
                Id = RepoId,
                ProductId = ProductId,
                DisplayName = "Phase 28 E2E repo",
                Kind = "local",
                RemoteUrl = "file://phase28-e2e-refactor",
                CloneRoot = "/tmp/phase28-e2e-refactor",
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
            Title = $"Phase 28 e2e refactor / {suffix}",
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId,
            StoryId = storyId,
            StoryTitle = $"Phase 28 e2e refactor / {suffix}",
            Phase = phase,
            PlanBranch = $"story/{storyId}/plan",
            PlanPrId = $"PR-{suffix}-plan",
            RepoId = RepoId,
            BaseBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            StoryPoints = 3,
            PlanDocPrsJson = null,
            AutoMerge = true,
            QaBranch = qaPrId is null ? null : $"story/{storyId}/qa",
            QaPrId = qaPrId,
        });
        control.SaveChanges();
        db.SaveChanges();
        return (factory, storyId, spawnId);
    }

    // ── workstream row factories ─────────────────────────────────────────

    private static WorkstreamRow ImplRow(
        string id, int sort, string spawnId, string stageSlug,
        IEnumerable<string>? depTitles = null,
        string? prId = null, string status = "planning")
    {
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
            PlanAnchor = $"phase-foundation/stage-{stageSlug}",
            PhaseAnchor = "phase-foundation",
            Category = WorkstreamCategories.Impl,
            DepsJson = depTitles is null
                ? "[]"
                : JsonSerializer.Serialize(depTitles.ToList()),
        };
    }

    private static WorkstreamRow QaE2eRow(
        string id, int sort, string spawnId, string stageSlug,
        IEnumerable<string> depTitles)
    {
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
            PlanAnchor = $"phase-foundation/stage-{stageSlug}" + CrewStoryArtifactStrategy.QaE2eAnchorSuffix,
            PhaseAnchor = "phase-foundation",
            Category = WorkstreamCategories.QaE2e,
            DepsJson = JsonSerializer.Serialize(depTitles.ToList()),
        };
    }

    // ── seeding + driving helpers ────────────────────────────────────────

    private static async Task SeedAsync(ForgeWebApplicationFactory f, params WorkstreamRow[] rows)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.Workstreams.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static async Task SeedMergedPrAsync(
        ForgeWebApplicationFactory f, string prId, string branch, string? wsId, string titlePrefix = "[impl]")
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.Prs.Add(new PrRow
        {
            Id = prId,
            RepoId = RepoId,
            Col = "merged",
            Title = $"{titlePrefix} {branch}",
            Branch = branch,
            Base = "main",
            Workstream = wsId ?? string.Empty,
            Epic = string.Empty,
            EpicColor = string.Empty,
            AuthorJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            IsActive = false,
            LastSyncStatus = "idle",
            Provider = "local",
            WorkItemId = wsId,
            Description = "seeded merged PR",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Pre-seed verdict=pass deliverables for the four canonical plan-doc
    /// keys so <c>StorySpawnOrchestrator.ArePlanDocsAllPassedAsync</c>
    /// (the gate at the top of <c>DriveImplementAsync</c>) releases.
    /// </summary>
    private static async Task SeedPlanDocPassesAsync(ForgeWebApplicationFactory f, string spawnId)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        foreach (var fileName in new[]
        {
            "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md",
        })
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

    private static async Task DriveAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
        await orch.DriveAsync(storyId);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 9.1 — auto-merge every PR opened by the
    /// orchestrator (qa-e2e + impl alike) so the spawn can advance.
    /// Without this, the orchestrator opens PRs (status=review) and
    /// stalls because no external `PrSyncJob` flips them to merged in
    /// the test harness. Returns the count of PRs flipped on this pass.
    /// </summary>
    private static async Task<int> AutoMergeOpenedPrsAsync(ForgeWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var open = await db.Prs
            .Where(p => p.Col != "merged" && p.Col != "abandoned")
            .ToListAsync();
        foreach (var pr in open)
        {
            pr.Col = "merged";
            pr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            pr.IsActive = false;
        }
        await db.SaveChangesAsync();
        return open.Count;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 9.1 — drive the orchestrator + auto-merge
    /// loop. Each iteration: <c>DriveAsync</c> once, then auto-merge
    /// every open PR. Stops when <c>spawn.Phase == Done</c> OR the
    /// loop has spun <paramref name="maxIterations"/> times. Returns
    /// the per-iteration spawn Phase log so callers can assert the
    /// transition sequence (e.g. Scenario 6 — never <c>qa</c>).
    /// </summary>
    private static async Task<List<string>> DriveUntilDoneAsync(
        ForgeWebApplicationFactory f, string storyId, int maxIterations = 24)
    {
        var phases = new List<string>();
        for (var i = 0; i < maxIterations; i++)
        {
            await DriveAsync(f, storyId);
            await AutoMergeOpenedPrsAsync(f);

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking()
                .FirstAsync(s => s.StoryId == storyId);
            phases.Add(spawn.Phase);
            if (spawn.Phase == StorySpawnPhases.Done) return phases;
        }
        return phases;
    }

    // ── read helpers ─────────────────────────────────────────────────────

    private static async Task<StorySpawnRow> ReadSpawnAsync(ForgeWebApplicationFactory f, string spawnId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
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

    // ════════════════════════════════════════════════════════════════════
    //
    //                          S C E N A R I O S
    //
    // ════════════════════════════════════════════════════════════════════

    // ── Sc1: 3-stage all-with-e2e → 7 PRs total (1 plan + 3 impl + 3 qa-e2e) ──

    [Fact]
    public async Task ThreeStage_All_With_E2e_Lands_Seven_Prs_Total()
    {
        var (f, storyId, spawnId) = NewSpawn("sc1");
        try
        {
            const string s1Impl = "ws-sc1-s1-impl";
            const string s2Impl = "ws-sc1-s2-impl";
            const string s3Impl = "ws-sc1-s3-impl";
            const string s1Qa = "ws-sc1-s1-qa";
            const string s2Qa = "ws-sc1-s2-qa";
            const string s3Qa = "ws-sc1-s3-qa";

            await SeedAsync(f,
                ImplRow(s1Impl, sort: 10, spawnId, stageSlug: "alpha"),
                QaE2eRow(s1Qa, sort: 11, spawnId, stageSlug: "alpha", depTitles: new[] { "alpha" }),
                ImplRow(s2Impl, sort: 20, spawnId, stageSlug: "beta"),
                QaE2eRow(s2Qa, sort: 21, spawnId, stageSlug: "beta", depTitles: new[] { "beta" }),
                ImplRow(s3Impl, sort: 30, spawnId, stageSlug: "gamma"),
                QaE2eRow(s3Qa, sort: 31, spawnId, stageSlug: "gamma", depTitles: new[] { "gamma" }));
            await SeedPlanDocPassesAsync(f, spawnId);
            // Pre-seed the plan PR as merged so the "7 PRs total" tally
            // captures the plan artefact alongside the 6 ws PRs the
            // orchestrator will produce.
            await SeedMergedPrAsync(f, $"PR-sc1-plan", $"story/{storyId}/plan", wsId: null, titlePrefix: "[plan]");

            var phases = await DriveUntilDoneAsync(f, storyId);
            Assert.Equal(StorySpawnPhases.Done, phases.Last());

            var prs = await ReadAllPrsAsync(f);
            Assert.Equal(7, prs.Count);

            var planPrs = prs.Where(p => p.Title.StartsWith("[plan]", StringComparison.Ordinal)).ToList();
            var implPrs = prs.Where(p => p.Title.StartsWith("[impl]", StringComparison.Ordinal)).ToList();
            var qaE2ePrs = prs.Where(p => p.Title.StartsWith("[e2e]", StringComparison.Ordinal)).ToList();
            Assert.Single(planPrs);
            Assert.Equal(3, implPrs.Count);
            Assert.Equal(3, qaE2ePrs.Count);
        }
        finally { f.Dispose(); }
    }

    // ── Sc2: 3-stage stage-2-no-e2e → 6 PRs total (no qa-e2e for stage 2) ──

    [Fact]
    public async Task ThreeStage_Stage2_No_E2e_Lands_Six_Prs_Total()
    {
        var (f, storyId, spawnId) = NewSpawn("sc2");
        try
        {
            const string s1Impl = "ws-sc2-s1-impl";
            const string s2Impl = "ws-sc2-s2-impl";
            const string s3Impl = "ws-sc2-s3-impl";
            const string s1Qa = "ws-sc2-s1-qa";
            // No s2Qa — stage 2's plan has zero test scenarios so Stage
            // 3.1's BuildDecompositionFromPlan emits no qa-e2e companion.
            const string s3Qa = "ws-sc2-s3-qa";

            await SeedAsync(f,
                ImplRow(s1Impl, sort: 10, spawnId, stageSlug: "alpha"),
                QaE2eRow(s1Qa, sort: 11, spawnId, stageSlug: "alpha", depTitles: new[] { "alpha" }),
                ImplRow(s2Impl, sort: 20, spawnId, stageSlug: "beta"),
                ImplRow(s3Impl, sort: 30, spawnId, stageSlug: "gamma"),
                QaE2eRow(s3Qa, sort: 31, spawnId, stageSlug: "gamma", depTitles: new[] { "gamma" }));
            await SeedPlanDocPassesAsync(f, spawnId);
            await SeedMergedPrAsync(f, $"PR-sc2-plan", $"story/{storyId}/plan", wsId: null, titlePrefix: "[plan]");

            var phases = await DriveUntilDoneAsync(f, storyId);
            Assert.Equal(StorySpawnPhases.Done, phases.Last());

            var prs = await ReadAllPrsAsync(f);
            Assert.Equal(6, prs.Count);

            var implPrs = prs.Where(p => p.Title.StartsWith("[impl]", StringComparison.Ordinal)).ToList();
            var qaE2ePrs = prs.Where(p => p.Title.StartsWith("[e2e]", StringComparison.Ordinal)).ToList();
            Assert.Equal(3, implPrs.Count);
            Assert.Equal(2, qaE2ePrs.Count);
            // No qa-e2e PR for stage 2 (the no-test-scenarios stage).
            Assert.DoesNotContain(qaE2ePrs, p => p.WorkItemId == "ws-sc2-s2-qa");
        }
        finally { f.Dispose(); }
    }

    // ── Sc3: Dep gate — qa-e2e-S2 waits for impl-S2 even when impl-S3 merged ──

    [Fact]
    public async Task DepGate_QaE2e_S2_Waits_For_Impl_S2_Even_When_Impl_S3_Merged_Earlier()
    {
        var (f, storyId, spawnId) = NewSpawn("sc3");
        try
        {
            const string s1Impl = "ws-sc3-s1-impl";
            const string s2Impl = "ws-sc3-s2-impl";
            const string s3Impl = "ws-sc3-s3-impl";
            const string s2Qa = "ws-sc3-s2-qa";

            // S3 impl is already merged (pre-seeded). S2 impl is still
            // planning. S2-qa must NOT claim — the dep-gate keys on
            // s2-impl's status, not on overall progress.
            await SeedAsync(f,
                ImplRow(s1Impl, sort: 10, spawnId, stageSlug: "alpha", status: "complete",
                    prId: "PR-sc3-impl-s1"),
                ImplRow(s2Impl, sort: 20, spawnId, stageSlug: "beta", status: "planning"),
                ImplRow(s3Impl, sort: 30, spawnId, stageSlug: "gamma", status: "complete",
                    prId: "PR-sc3-impl-s3"),
                QaE2eRow(s2Qa, sort: 21, spawnId, stageSlug: "beta", depTitles: new[] { "beta" }));
            await SeedMergedPrAsync(f, "PR-sc3-impl-s1", $"ws/{s1Impl}", s1Impl);
            await SeedMergedPrAsync(f, "PR-sc3-impl-s3", $"ws/{s3Impl}", s3Impl);
            await SeedPlanDocPassesAsync(f, spawnId);

            // First drive — S2-impl claims and opens a PR. S2-qa MUST
            // NOT claim yet: the dep gate sees s2-impl at status="review"
            // (PR just opened, ws not yet flipped to complete because
            // the merge hasn't happened). This is the headline temporal
            // assertion for Sc3 — the gate holds qa-e2e back even when
            // s3-impl (the out-of-order sibling) is already merged.
            await DriveAsync(f, storyId);
            var afterDrive1 = await ReadWorkstreamsAsync(f, spawnId);
            Assert.Equal("planning", afterDrive1.First(w => w.Id == s2Qa).Status);

            // Drive to Done — the DriveUntilDoneAsync loop auto-merges
            // every opened PR per iteration so s2-impl's PR-merge →
            // DriveReviewAsync's ws.Status flip → DriveImplementAsync's
            // next claim sweep all cascade naturally over multiple
            // iterations. The headline assertion is that the spawn
            // reaches Done AND s2-qa's PR appears exactly once.
            var phases = await DriveUntilDoneAsync(f, storyId);
            Assert.Equal(StorySpawnPhases.Done, phases.Last());

            var qaPrs = (await ReadAllPrsAsync(f))
                .Where(p => p.Title.StartsWith("[e2e]", StringComparison.Ordinal)).ToList();
            Assert.Single(qaPrs);
            Assert.Equal(s2Qa, qaPrs[0].WorkItemId);
        }
        finally { f.Dispose(); }
    }

    // ── Sc4: Cross-stage e2e — qa-e2e-S3 deps=[S2, S3] waits for both ────

    [Fact]
    public async Task CrossStage_E2e_Waits_For_Both_Impl_Siblings_To_Merge()
    {
        var (f, storyId, spawnId) = NewSpawn("sc4");
        try
        {
            const string s2Impl = "ws-sc4-s2-impl";
            const string s3Impl = "ws-sc4-s3-impl";
            const string s3Qa = "ws-sc4-s3-qa";

            // s3-qa carries BOTH `gamma` (its impl sibling) AND `beta`
            // (cross-stage upstream from the Stage 2.1 H3 parser). Until
            // both impl-S2 + impl-S3 are complete, s3-qa must stay
            // planning.
            await SeedAsync(f,
                ImplRow(s2Impl, sort: 10, spawnId, stageSlug: "beta", status: "planning"),
                ImplRow(s3Impl, sort: 20, spawnId, stageSlug: "gamma", status: "planning"),
                QaE2eRow(s3Qa, sort: 21, spawnId, stageSlug: "gamma",
                    depTitles: new[] { "gamma", "beta" }));
            await SeedPlanDocPassesAsync(f, spawnId);

            // Drive ONCE — both impl-S2 + impl-S3 claim + open PRs.
            // s3-qa MUST stay planning: its deps gate (BOTH "beta" +
            // "gamma" must be complete) hasn't fired because neither
            // impl-S2 nor impl-S3 is at status="complete" yet (their
            // PRs just opened, ws still at review).
            await DriveAsync(f, storyId);
            var afterDrive1 = await ReadWorkstreamsAsync(f, spawnId);
            Assert.Equal("planning", afterDrive1.First(w => w.Id == s3Qa).Status);
            // Both impls did claim though (they have no deps).
            Assert.Contains(afterDrive1.First(w => w.Id == s2Impl).Status,
                new[] { "review", "merging", "complete" });
            Assert.Contains(afterDrive1.First(w => w.Id == s3Impl).Status,
                new[] { "review", "merging", "complete" });

            // Drive to Done — DriveUntilDoneAsync handles the cascade
            // (auto-merge PRs → DriveReviewAsync flips impls to
            // complete → DriveImplementAsync claims s3-qa →
            // DriveReviewAsync flips s3-qa to complete →
            // TryAdvanceToDoneAsync fires).
            var phases = await DriveUntilDoneAsync(f, storyId);
            Assert.Equal(StorySpawnPhases.Done, phases.Last());

            var qaPrs = (await ReadAllPrsAsync(f))
                .Where(p => p.Title.StartsWith("[e2e]", StringComparison.Ordinal)).ToList();
            Assert.Single(qaPrs);
            Assert.Equal(s3Qa, qaPrs[0].WorkItemId);
        }
        finally { f.Dispose(); }
    }

    // ── Sc5: Restart resume — kill mid-qa-e2e, restart, drive to done ────

    [Fact]
    public async Task Restart_Resume_MidQaE2e_Drives_Spawn_To_Done()
    {
        var (f, storyId, spawnId) = NewSpawn("sc5");
        try
        {
            const string s1Impl = "ws-sc5-s1-impl";
            const string s1Qa = "ws-sc5-s1-qa";

            // Pre-seed an intermediate "crashed mid-qa-e2e" state: impl
            // is complete + its PR merged; qa-e2e row is at status="review"
            // with a PR already opened. Simulates the orchestrator
            // dying right after the qa-e2e PR opened but before the
            // PR's merge event reached it.
            await SeedAsync(f,
                ImplRow(s1Impl, sort: 10, spawnId, stageSlug: "alpha", status: "complete",
                    prId: "PR-sc5-impl"),
                QaE2eRow(s1Qa, sort: 11, spawnId, stageSlug: "alpha", depTitles: new[] { "alpha" }));
            await SeedMergedPrAsync(f, "PR-sc5-impl", $"ws/{s1Impl}", s1Impl);
            await SeedPlanDocPassesAsync(f, spawnId);

            // Flip the qa-e2e row to "review" + add a PR ID so the
            // orchestrator's "already past planning" guard fires and
            // the row is picked up by DriveReviewAsync (not the
            // ProduceStageE2eAsync path which would try to re-emit).
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var qa = await db.Workstreams.FirstAsync(w => w.Id == s1Qa);
                qa.Status = "review";
                qa.PrsJson = JsonSerializer.Serialize(new[] { "PR-sc5-qa-mid" });
                await db.SaveChangesAsync();
            }
            await SeedMergedPrAsync(f, "PR-sc5-qa-mid", $"ws/{s1Qa}", s1Qa, titlePrefix: "[e2e]");

            // Drive — orchestrator picks up the spawn, sees qa-e2e row
            // at review with a merged PR, advances ws.Status=complete,
            // and fires TryAdvanceToDoneAsync (all ws complete → Phase=Done).
            var phases = await DriveUntilDoneAsync(f, storyId);
            Assert.Equal(StorySpawnPhases.Done, phases.Last());

            var workstreams = await ReadWorkstreamsAsync(f, spawnId);
            Assert.Equal("complete", workstreams.First(w => w.Id == s1Qa).Status);
        }
        finally { f.Dispose(); }
    }

    // ── Sc6: Spawn never enters Phase='qa' (phase log assertion) ─────────

    [Fact]
    public async Task NewShape_Spawn_Never_Enters_Phase_Qa_During_Drive_To_Done()
    {
        var (f, storyId, spawnId) = NewSpawn("sc6");
        try
        {
            const string s1Impl = "ws-sc6-s1-impl";
            const string s1Qa = "ws-sc6-s1-qa";
            const string s2Impl = "ws-sc6-s2-impl";
            const string s2Qa = "ws-sc6-s2-qa";

            await SeedAsync(f,
                ImplRow(s1Impl, sort: 10, spawnId, stageSlug: "alpha"),
                QaE2eRow(s1Qa, sort: 11, spawnId, stageSlug: "alpha", depTitles: new[] { "alpha" }),
                ImplRow(s2Impl, sort: 20, spawnId, stageSlug: "beta"),
                QaE2eRow(s2Qa, sort: 21, spawnId, stageSlug: "beta", depTitles: new[] { "beta" }));
            await SeedPlanDocPassesAsync(f, spawnId);

            // Snapshot the Phase value before every drive so the
            // sequence captures any transient 'qa' state. The phase
            // log starts with the pre-drive value (Implement) and
            // appends one entry per drive iteration.
            var initial = await ReadSpawnAsync(f, spawnId);
            var phaseLog = new List<string> { initial.Phase };
            phaseLog.AddRange(await DriveUntilDoneAsync(f, storyId));

            // Headline assertion: `qa` NEVER shows up. The lifecycle
            // for new-shape spawns goes implement → review → done
            // (Stage 5.2's TryAdvanceToDoneAsync fires from inside
            // DriveReviewAsync without an intermediate Qa stop).
            Assert.DoesNotContain(StorySpawnPhases.Qa, phaseLog);

            // Sanity: did reach Done.
            Assert.Equal(StorySpawnPhases.Done, phaseLog.Last());

            // Sanity: the spawn's recorded QaBranch + QaPrId stayed
            // null — those are the legacy-path artefacts the new
            // routing must NEVER populate.
            var after = await ReadSpawnAsync(f, spawnId);
            Assert.Null(after.QaBranch);
            Assert.Null(after.QaPrId);
        }
        finally { f.Dispose(); }
    }

    // ── Sc7: Legacy Phase='qa' spawn drains under new orchestrator ────────

    [Fact]
    public async Task Legacy_Phase_Qa_Spawn_Drains_To_Done_Under_New_Orchestrator()
    {
        // Pre-Stage-3.1 spawn paused at Phase=qa with the legacy QA PR
        // already opened. The new orchestrator's legacy-drain guard
        // (Stage 5.3) kicks in: DriveQaAsync's batch-QA path is still
        // alive for this back-compat case + drives the spawn to Done
        // without errors. Headline assertion: no exceptions + Phase=Done.
        var (f, storyId, spawnId) = NewSpawn("sc7", phase: StorySpawnPhases.Qa,
            qaPrId: "PR-sc7-legacy-qa");
        try
        {
            const string s1Impl = "ws-sc7-legacy-impl-1";
            const string s2Impl = "ws-sc7-legacy-impl-2";

            // Two legacy impl workstreams — no qa-e2e (the spawn predates
            // Stage 3.1's qa-e2e emission). Both at status=complete so
            // the legacy drain only has to run Step 2 (step-def PRs).
            await SeedAsync(f,
                LegacyImplRow(s1Impl, sort: 10, spawnId, status: "complete"),
                LegacyImplRow(s2Impl, sort: 20, spawnId, status: "complete"));
            await SeedMergedPrAsync(f, "PR-sc7-legacy-qa",
                $"story/{storyId}/qa", wsId: null, titlePrefix: "[qa]");
            await SeedPlanDocPassesAsync(f, spawnId);

            await DriveAsync(f, storyId);

            // Step-def PRs opened by the legacy DriveQaAsync path — one
            // per workstream. Auto-merge them + drive again so the
            // spawn settles at Done.
            await AutoMergeOpenedPrsAsync(f);
            await DriveAsync(f, storyId);

            var after = await ReadSpawnAsync(f, spawnId);
            Assert.Equal(StorySpawnPhases.Done, after.Phase);

            // The legacy drain emitted step-def PRs (`[qa-steps] ...`)
            // — proves the legacy code path actually ran.
            var prs = await ReadAllPrsAsync(f);
            var stepDefPrs = prs.Where(p => p.Title.StartsWith("[qa-steps]", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, stepDefPrs.Count);

            // And the legacy QA PR stayed in the row table (pre-seeded
            // as merged). The legacy drain doesn't open a new one.
            Assert.Single(prs.Where(p => p.Title.StartsWith("[qa]", StringComparison.Ordinal)));
        }
        finally { f.Dispose(); }
    }

    // ── Sc7 helper: legacy impl row with no PlanAnchor (pre-Stage-3.1 shape) ──

    private static WorkstreamRow LegacyImplRow(string id, int sort, string spawnId, string status)
    {
        return new WorkstreamRow
        {
            Id = id,
            Sort = sort,
            RepoId = RepoId,
            Name = id,
            Branch = $"ws/{id}",
            Base = "main",
            Epic = string.Empty,
            Status = status,
            Category = WorkstreamCategories.Impl,
            WorktreeJson = "[]",
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Summary = string.Empty,
            StorySpawnId = spawnId,
            // Pre-Stage-3.1 spawns wrote PlanAnchor=null on their
            // workstreams — keep that shape so the legacy DriveQaAsync
            // path is the one that fires, not the new-shape qa-e2e
            // routing.
        };
    }
}
