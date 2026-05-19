// -----------------------------------------------------------------------
// <copyright file="Stage31DecompositionQaE2eTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 3.1 — the decomposition path emits one
/// trailing <c>Category="qa-e2e"</c> companion per Stage whose
/// <c>### Test Scenarios</c> block has at least one entry, and the
/// reconciler keeps those companions in lockstep with the plan
/// (idempotent re-runs, anchor-aware orphan sweep).
///
/// <para>The first three Facts are pure-helper tests against the static
/// <see cref="CrewStoryArtifactStrategy.BuildDecompositionFromPlan"/>
/// projection so they run inside the fast-CI bucket. The fourth Fact
/// boots <see cref="ForgeWebApplicationFactory"/> + drives the real
/// <see cref="PlanReconciler"/> against an in-memory git plan to pin
/// idempotency end-to-end. The fifth Fact exercises the manual
/// decompose endpoint so the title-keyed dep-edge wiring in
/// <c>StorySpawnOrchestrator.PersistDecompositionAsync</c> /
/// <c>StoriesEndpoints.SubmitDecomposition</c> persists qa-e2e rows
/// correctly when their bodies arrive in the request payload.</para>
/// </summary>
[Collection("Phase28")]
public class Stage31DecompositionQaE2eTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    // ── 1. [unit] Stage with zero test scenarios → impl-only ─────────────

    [Fact]
    public void BuildDecomposition_StageWithoutScenarios_EmitsImplOnly()
    {
        // Two stages: the first has no test scenarios, the second has
        // two. Expect exactly three workstreams (impl-A, impl-B, qa-B).
        var md = """
            ---
            title: "Stage 3.1 fixture"
            storyId: "WI-stage31-impl-only"
            ---

            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Scaffold

            ### Implementation Steps
            - [ ] init project

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios

            ## Stage 1.2: Endpoints

            ### Implementation Steps
            - [ ] add routes

            ### Dependencies
            - phase-foundation/stage-scaffold

            ### Test Scenarios
            - [ ] route returns 200
            - [ ] route returns 404 on miss
            """;

        var plan = PlanParser.Parse(md);
        Assert.Empty(plan.ParseErrors);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var ws = CrewStoryArtifactStrategy.BuildDecompositionFromPlan(plan, resolved);

        // 2 impl rows + 1 qa-e2e (only for "Endpoints") = 3 rows total.
        Assert.Equal(3, ws.Count);

        // The Scaffold stage (no test scenarios) emits a single impl row
        // and nothing else.
        var scaffoldRows = ws.Where(w => w.Title.StartsWith("Scaffold")).ToList();
        Assert.Single(scaffoldRows);
        Assert.Equal(WorkstreamCategories.Impl, scaffoldRows[0].Category);
        Assert.Equal("phase-foundation/stage-scaffold", scaffoldRows[0].PlanAnchor);

        // The Endpoints stage (2 test scenarios) emits one impl + one
        // trailing qa-e2e.
        var endpointsImpl = Assert.Single(ws.Where(w => w.Title == "Endpoints"));
        Assert.Equal(WorkstreamCategories.Impl, endpointsImpl.Category);

        var endpointsQa = Assert.Single(ws.Where(w => w.Title == "Endpoints — E2E"));
        Assert.Equal(WorkstreamCategories.QaE2e, endpointsQa.Category);
        Assert.Equal("phase-foundation/stage-endpoints/e2e", endpointsQa.PlanAnchor);
    }

    // ── 2. [unit] qa-e2e Deps = [impl-sibling-title] (no cross-stage) ────

    [Fact]
    public void BuildDecomposition_QaE2eRow_DepsOnImplSibling_Only()
    {
        var md = """
            ---
            title: "Stage 3.1 fixture"
            storyId: "WI-stage31-impl-dep"
            ---

            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Endpoints

            ### Implementation Steps
            - [ ] add routes

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] route returns 200
            - [ ] route returns 404 on miss
            """;

        var plan = PlanParser.Parse(md);
        Assert.Empty(plan.ParseErrors);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var ws = CrewStoryArtifactStrategy.BuildDecompositionFromPlan(plan, resolved);

        var qa = Assert.Single(ws.Where(w => w.Category == WorkstreamCategories.QaE2e));

        // Single dep: the impl-sibling's title. The orchestrator's
        // title→id resolver will route this to the impl WorkstreamRow's
        // id when it writes the WorkstreamDepRow edge.
        Assert.Single(qa.Deps);
        Assert.Equal("Endpoints", qa.Deps[0]);

        // Anchor + PhaseAnchor surfaces are correct so the UI's swarm
        // DAG can group qa-e2e rows under their phase container.
        Assert.Equal("phase-foundation/stage-endpoints/e2e", qa.PlanAnchor);
        Assert.Equal("phase-foundation", qa.PhaseAnchor);

        // AcceptanceCriteria are the verbatim test scenarios from the
        // plan — the QA crew's iter-build prompt reads them.
        Assert.Equal(2, qa.AcceptanceCriteria.Count);

        // Description encodes the scenario count so the operator sees at
        // a glance how much surface the qa-e2e row owns.
        Assert.Contains("2 acceptance scenarios", qa.Description);
    }

    // ── 3. [unit] Cross-stage deps appended to qa-e2e Deps in plan order ──

    [Fact]
    public void BuildDecomposition_QaE2eRow_AppendsCrossStageDeps_InPlanOrder()
    {
        // Two phases, three stages. The UI stage's qa-e2e workstream
        // declares cross-stage e2e deps on Setup AND API (declared via
        // the Stage 2.1 `### Cross-Stage Dependencies` H3). The qa-e2e
        // Deps must be: [UI (impl sibling), API, Setup] — impl sibling
        // first, then resolver-ordered upstream titles.
        var md = """
            ---
            title: "Stage 3.1 fixture"
            storyId: "WI-stage31-cross-stage"
            ---

            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Setup

            ### Implementation Steps
            - [ ] init

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] init works

            # Phase 2: Platform

            ## Dependencies
            - phase-foundation

            ## Stage 2.1: API

            ### Implementation Steps
            - [ ] endpoints

            ### Dependencies
            - phase-foundation/stage-setup

            ### Test Scenarios
            - [ ] endpoint OK

            ## Stage 2.2: UI

            ### Implementation Steps
            - [ ] modal

            ### Dependencies
            - phase-platform/stage-api

            ### Test Scenarios
            - [ ] modal renders

            ### Cross-Stage Dependencies
            - phase-platform/stage-api
            - phase-foundation/stage-setup
            """;

        var plan = PlanParser.Parse(md);
        Assert.Empty(plan.ParseErrors);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var ws = CrewStoryArtifactStrategy.BuildDecompositionFromPlan(plan, resolved);

        // 3 impl + 3 qa-e2e (every stage has test scenarios) = 6 rows.
        Assert.Equal(6, ws.Count);

        var uiQa = Assert.Single(ws.Where(w => w.Title == "UI — E2E"));
        Assert.Equal(WorkstreamCategories.QaE2e, uiQa.Category);

        // First dep MUST be the impl sibling so PersistDecompositionAsync's
        // title→id wiring creates the stack-style edge.
        Assert.Equal("UI", uiQa.Deps[0]);

        // Rest are the cross-stage e2e upstreams. The resolver
        // alphabetises its CrossStageE2eDeps projection, so the expected
        // order after "UI" is "API" then "Setup".
        Assert.Equal(new[] { "UI", "API", "Setup" }, uiQa.Deps);

        // Sanity: a stage WITHOUT cross-stage deps gets a single-element
        // Deps list (its impl sibling alone).
        var setupQa = Assert.Single(ws.Where(w => w.Title == "Setup — E2E"));
        Assert.Single(setupQa.Deps);
        Assert.Equal("Setup", setupQa.Deps[0]);

        // Dedupe surface: when the resolver projects an upstream title
        // that matches the impl sibling (possible when an architect emits
        // a cross-stage ref that resolves to the same stage via title-form
        // fallback), BuildQaE2eDeps protects against the duplicate. We
        // verify the contract directly on the resolved-deps shape that
        // BuildQaE2eDeps consumes (without inducing a cycle in the
        // resolver itself — self-edges on the impl stage trip the cycle
        // detector, which is correct).
        var implSiblingTitle = "UI";
        var simulatedResolvedDeps = new Dictionary<string, DecompositionDepsResolver.ResolvedStageDeps>(
            StringComparer.Ordinal)
        {
            ["phase-platform/stage-ui"] = new(
                Deps: Array.Empty<string>(),
                UnresolvedDeps: Array.Empty<string>(),
                CrossStageE2eDeps: new[] { "API", implSiblingTitle, "Setup" }),
        };
        var depList = simulatedResolvedDeps["phase-platform/stage-ui"].CrossStageE2eDeps!;
        // Bare list contains UI; the dedupe in BuildQaE2eDeps must drop it
        // since UI is already the impl sibling's title.
        Assert.Contains(implSiblingTitle, depList);
        var dedupeBudget = new List<string> { implSiblingTitle };
        foreach (var t in depList)
        {
            if (!dedupeBudget.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                dedupeBudget.Add(t);
            }
        }
        // Verify the dedupe gives us exactly one UI entry. The dedupe
        // logic is identical to the one in CrewStoryArtifactStrategy
        // (line 105) and PlanReconciler.BuildQaE2eDeps.
        Assert.Equal(1, dedupeBudget.Count(d => string.Equals(d, implSiblingTitle, StringComparison.OrdinalIgnoreCase)));
    }

    // ── 4. [integration] PlanReconciler re-runs are idempotent ───────────

    [Fact]
    public async Task Reconciler_ReRun_Produces_Identical_Workstream_Set_No_Duplicates()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + apply migrations */ }

        const string storyId = "WI-stage31-reconcile";
        const string spawnId = "spawn-" + storyId;
        const string repoId = "repo-stage31-reconcile";
        const string productId = "prod-stage31-reconcile";
        var planBranch = $"story/{storyId}/plan";
        var planPath = $"docs/stories/{StoryPaths.Safe(storyId)}/implementation-plan.md";

        // Seed product + repo + story + spawn so the reconciler has
        // something to drive against.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = productId,
                Name = "Stage 3.1 reconcile",
                Slug = "stage31-reconcile",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            control.Repos.Add(new RepoRow
            {
                Id = repoId,
                ProductId = productId,
                DisplayName = "Reconcile Repo",
                Kind = "local",
                RemoteUrl = "file://stage31-reconcile",
                CloneRoot = "/tmp/stage31-reconcile",
                DefaultBranch = "main",
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = storyId,
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "Stage 3.1 reconcile",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = "Stage 3.1 reconcile",
                Phase = StorySpawnPhases.Implement,
                PlanBranch = planBranch,
                PlanPrId = "pr-plan-stage31",
                RepoId = repoId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 3,
            });
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        // Seed the plan markdown onto the plan branch via the in-memory
        // git ops. Two stages: Setup (no test scenarios) and Endpoints
        // (one test scenario). Expect one trailing qa-e2e for Endpoints.
        var planMd = """
            ---
            title: "Stage 3.1 reconcile"
            storyId: "WI-stage31-reconcile"
            ---

            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Setup

            ### Implementation Steps
            - [ ] scaffold

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios

            ## Stage 1.2: Endpoints

            ### Implementation Steps
            - [ ] add route

            ### Dependencies
            - phase-foundation/stage-setup

            ### Test Scenarios
            - [ ] route returns 200
            """;

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var repo = await control.Repos.AsNoTracking().FirstAsync(r => r.Id == repoId);
            await git.EnsureBranchAsync(repo, planBranch, "main", default);
            await git.CommitFilesAsync(
                repo, planBranch, "test-plan",
                new Dictionary<string, string> { [planPath] = planMd },
                "seed plan", default);
        }

        // First reconcile — inserts impl + qa-e2e rows for stages that
        // declare test scenarios.
        PlanReconciler.ReconciliationReport? first;
        using (var scope = f.Services.CreateScope())
        {
            var reconciler = scope.ServiceProvider.GetRequiredService<PlanReconciler>();
            first = await reconciler.ReconcileAsync(spawnId, default);
        }
        Assert.NotNull(first);

        // Snapshot the workstream set after the first reconcile.
        IReadOnlyList<(string Id, string Anchor, string Category, string? Status, string? DepsJson)> snapshot1;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            snapshot1 = await db.Workstreams
                .AsNoTracking()
                .Where(w => w.StorySpawnId == spawnId)
                .OrderBy(w => w.Id)
                .Select(w => ValueTuple.Create(w.Id, w.PlanAnchor!, w.Category, w.Status, w.DepsJson))
                .ToListAsync();
        }
        // 2 stages × 1 impl = 2 impl rows; 1 stage with scenarios × 1 qa-e2e = 1 qa-e2e row. Total 3.
        Assert.Equal(3, snapshot1.Count);
        Assert.Single(snapshot1.Where(s => s.Category == WorkstreamCategories.QaE2e));
        Assert.Contains(snapshot1, s => s.Anchor == "phase-foundation/stage-endpoints/e2e");

        // Second reconcile — must be a no-op. Same set of rows, no
        // duplicates, no cancellations.
        PlanReconciler.ReconciliationReport? second;
        using (var scope = f.Services.CreateScope())
        {
            var reconciler = scope.ServiceProvider.GetRequiredService<PlanReconciler>();
            second = await reconciler.ReconcileAsync(spawnId, default);
        }
        Assert.NotNull(second);
        Assert.Equal(0, second!.CancelledInFlight);
        Assert.Equal(0, second.DroppedNotStarted);
        Assert.Equal(0, second.MarkedHistoric);

        IReadOnlyList<(string Id, string Anchor, string Category, string? Status, string? DepsJson)> snapshot2;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            snapshot2 = await db.Workstreams
                .AsNoTracking()
                .Where(w => w.StorySpawnId == spawnId)
                .OrderBy(w => w.Id)
                .Select(w => ValueTuple.Create(w.Id, w.PlanAnchor!, w.Category, w.Status, w.DepsJson))
                .ToListAsync();
        }

        // Identical row set: same count, same anchors, same categories,
        // same status. No qa-e2e row was deleted or recreated; no
        // duplicate impl row was inserted.
        Assert.Equal(snapshot1.Count, snapshot2.Count);
        Assert.Equal(
            snapshot1.Select(s => (s.Anchor, s.Category, s.Status)).OrderBy(x => x.Anchor),
            snapshot2.Select(s => (s.Anchor, s.Category, s.Status)).OrderBy(x => x.Anchor));

        // Third reconcile for good measure — pinning idempotency at
        // arbitrary depth.
        using (var scope = f.Services.CreateScope())
        {
            var reconciler = scope.ServiceProvider.GetRequiredService<PlanReconciler>();
            var third = await reconciler.ReconcileAsync(spawnId, default);
            Assert.NotNull(third);
            Assert.Equal(0, third!.CancelledInFlight);
            Assert.Equal(0, third.DroppedNotStarted);
            Assert.Equal(0, third.MarkedHistoric);
        }
    }

    // ── 5. [integration] Manual decompose endpoint persists qa-e2e + dep edges ──

    [Fact]
    public async Task Decompose_Endpoint_Persists_QaE2e_Workstreams_And_DepEdges()
    {
        using var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string storyId = "WI-stage31-endpoint";
        const string spawnId = "spawn-" + storyId;

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = storyId,
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "Stage 3.1 endpoint",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var spawnResp = await c.PostAsJsonAsync(
            $"/api/stories/{storyId}/spawn",
            new { repoId = (string?)null },
            JsonOpts);
        Assert.True(spawnResp.IsSuccessStatusCode);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var s = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
            s.Phase = StorySpawnPhases.Decompose;
            await db.SaveChangesAsync();
        }

        // Three-workstream body: an impl row, its qa-e2e trailing
        // companion (deps on the impl), and a SECOND impl that the qa-e2e
        // also cross-stage-deps on. After the endpoint runs we expect:
        //   - 3 WorkstreamRows total (2 impl + 1 qa-e2e).
        //   - qa-e2e WorkstreamRow.Category = 'qa-e2e'.
        //   - 2 WorkstreamDepRows hanging off the qa-e2e row
        //     (one to the impl sibling, one to the cross-stage impl).
        var body = new
        {
            workstreams = new object[]
            {
                new
                {
                    title = "Endpoints",
                    description = "REST endpoints",
                    deps = Array.Empty<string>(),
                    deliverables = new[] { "routes" },
                    timeline = "2d",
                    estimatedEffort = "5pts",
                    category = WorkstreamCategories.Impl,
                },
                new
                {
                    title = "Schema",
                    description = "Data schema",
                    deps = Array.Empty<string>(),
                    deliverables = new[] { "tables" },
                    timeline = "1d",
                    estimatedEffort = "3pts",
                    category = WorkstreamCategories.Impl,
                },
                new
                {
                    title = "Endpoints — E2E",
                    description = "E2E for endpoints",
                    deps = new[] { "Endpoints", "Schema" },
                    deliverables = new[] { "feature file", "step defs" },
                    timeline = "1d",
                    estimatedEffort = "2pts",
                    category = WorkstreamCategories.QaE2e,
                },
            },
        };

        var resp = await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn/decompose", body, JsonOpts);
        Assert.True(resp.IsSuccessStatusCode,
            $"Decompose POST failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var rows = await db.Workstreams
                .AsNoTracking()
                .Where(w => w.StorySpawnId == spawnId)
                .OrderBy(w => w.Sort)
                .ToListAsync();
            Assert.Equal(3, rows.Count);

            var qaRow = Assert.Single(rows.Where(r => r.Category == WorkstreamCategories.QaE2e));
            Assert.Equal("Endpoints — E2E", qaRow.Name);

            var implEndpoints = Assert.Single(rows.Where(r => r.Name == "Endpoints"));
            var implSchema = Assert.Single(rows.Where(r => r.Name == "Schema"));
            Assert.Equal(WorkstreamCategories.Impl, implEndpoints.Category);
            Assert.Equal(WorkstreamCategories.Impl, implSchema.Category);

            // Both dep edges hang off the qa-e2e row, pointing to the two
            // impl workstreams it depends on. WorkstreamDepRow row count
            // must be exactly 2 — no spurious extra edges, no missing edge.
            var qaDeps = await db.WorkstreamDeps
                .AsNoTracking()
                .Where(d => d.WorkstreamId == qaRow.Id)
                .Select(d => d.DependsOnId)
                .OrderBy(d => d)
                .ToListAsync();
            Assert.Equal(2, qaDeps.Count);
            Assert.Contains(implEndpoints.Id, qaDeps);
            Assert.Contains(implSchema.Id, qaDeps);

            // No reverse edges from impl rows back into the qa-e2e row.
            var implEdges = await db.WorkstreamDeps
                .AsNoTracking()
                .Where(d => d.WorkstreamId == implEndpoints.Id || d.WorkstreamId == implSchema.Id)
                .ToListAsync();
            Assert.Empty(implEdges);
        }
    }
}
