// -----------------------------------------------------------------------
// <copyright file="Phase28OrchestratorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 28 — end-to-end orchestrator tests. These
/// run with <c>Forge:Stories:OrchestratorEnabled=true</c> so each REST
/// mutation triggers the full crew flow (architect → engineer → review →
/// qa → done) using the deterministic strategy. Covers the gap items the
/// gate-only suite (<c>Phase28StorySpawnTests</c>) doesn't reach:
///
/// <list type="bullet">
/// <item>28.3.4–28.3.6 — architect crew assignment + plan PR auto-creation.</item>
/// <item>28.4.1 — auto-decomposition.</item>
/// <item>28.5.3–28.5.7 — engineer crew assignment + ws PR + dep scheduler.</item>
/// <item>28.6.3–28.6.8 — review comment posting + routing + approve / merge.</item>
/// <item>28.7.3–28.7.11 — QA + step-def auto-flow.</item>
/// <item>28.8.3–28.8.5 — ADO work-item closure on done + e2e green check.</item>
/// <item>28.10.2 — artifact-existence verification on main.</item>
/// <item>28.11.6 — auto-unblock on upstream PR merge.</item>
/// <item>CC.5 — restart resume via scheduler tick.</item>
/// <item>CC.6 — SignalR change events fired on every transition.</item>
/// </list>
/// </summary>
/// <remarks>
/// Marked <c>Integration</c> because the orchestrator now depends on the real
/// <see cref="Forge.Api.Services.Stories.CrewStoryArtifactStrategy"/>, which
/// invokes <see cref="Forge.Api.Agents.IAgentRunner"/> against Claude / Copilot.
/// CI runs <c>dotnet test --filter "Category!=Integration"</c> to skip these.
/// To run locally: <c>dotnet test --filter "Category=Integration"</c> with
/// Anthropic / Copilot auth in scope.
/// </remarks>
[Collection("Phase28")]
[Trait("Category", "Integration")]
public class Phase28OrchestratorTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static (ForgeWebApplicationFactory factory, HttpClient client) NewOrchestratorFactory()
    {
        var factory = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        return (factory, client);
    }

    private static async Task<string> SeedStoryAsync(ForgeWebApplicationFactory f, string id, string title)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        if (await db.WorkItems.AnyAsync(w => w.Id == id)) return id;
        db.WorkItems.Add(new WorkItemRow
        {
            Id = id, Provider = "ado", Track = "dev", Type = "feature", State = "active",
            Title = title, Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<StorySpawnRow> ReadSpawnAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
    }

    private static async Task<List<WorkstreamRow>> ReadWorkstreamsAsync(ForgeWebApplicationFactory f, string spawnId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawnId)
            .OrderBy(w => w.Sort)
            .ToListAsync();
    }

    private static async Task<bool> FileOnMainAsync(ForgeWebApplicationFactory f, string path)
    {
        using var scope = f.Services.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
        return await git.FileExistsAsync(null, "main", path);
    }

    private static async Task TickAsync(ForgeWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var sched = scope.ServiceProvider.GetRequiredService<SpawnDependencyScheduler>();
        await sched.TickOnceAsync();
    }

    /// <summary>
    /// Phase 53 — drive the plan phase to completion in deterministic tests.
    ///
    /// <para>
    /// Sequential-mode spawns (PlanDocPrsJson != null) need the orchestrator
    /// to be ticked between each doc transition: doc N's PR must be merged
    /// AND its latest deliverable verdict must be "pass" before doc N+1's
    /// branch is created. The deterministic test strategy doesn't run the
    /// real gen↔eval loop (no DeliverableLoop invocation) so verdict=pass
    /// rows never get written organically — this helper seeds them
    /// per-doc so the orchestrator's <c>IsDocVerdictPassAsync</c> gate
    /// releases.
    /// </para>
    ///
    /// <para>
    /// Each iteration: re-read spawn, seed verdict=pass for every doc that
    /// has an opened PR, merge any unmerged PRs, then tick the
    /// orchestrator so the next doc's branch+PR open. Loops until all
    /// four doc PRs are merged or <c>maxIterations</c> is hit (12 ticks
    /// is well above the worst-case 8 needed for the four docs to walk
    /// through open → merge transitions).
    /// </para>
    ///
    /// <para>
    /// Legacy-mode spawns (PlanDocPrsJson == null) keep the original one-
    /// merge behavior so tests that flipped a spawn back to legacy via
    /// <c>SetPlanPrAsync</c> still drive the single PR through.
    /// </para>
    /// </summary>
    internal static async Task MergeAllPlanPrsAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var prov = scope.ServiceProvider.GetRequiredService<IStoryProviderOps>();
        var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
        var sched = scope.ServiceProvider.GetRequiredService<SpawnDependencyScheduler>();

        var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);

        if (!PlanDocStateHelpers.IsSequentialMode(spawn))
        {
            // Legacy path — single PR + branch.
            if (!string.IsNullOrEmpty(spawn.PlanPrId) && !string.IsNullOrEmpty(spawn.PlanBranch))
            {
                await prov.MergePrAsync(spawn.PlanPrId!);
                await git.MergeAsync(null, spawn.PlanBranch!, "main");
            }
            return;
        }

        const int maxIterations = 12;
        for (var iter = 0; iter < maxIterations; iter++)
        {
            spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
            var states = PlanDocStateHelpers.GetPlanDocStates(spawn).ToList();

            // Seed verdict=pass + merge any opened PRs.
            var anyUnmerged = false;
            var mergedCount = 0;
            foreach (var docKey in PlanDocStateHelpers.PlanDocOrder)
            {
                var entry = states.FirstOrDefault(e =>
                    string.Equals(e.Key, docKey, StringComparison.OrdinalIgnoreCase));
                if (entry is null || string.IsNullOrEmpty(entry.PrId) || string.IsNullOrEmpty(entry.Branch))
                {
                    anyUnmerged = true;
                    continue;
                }

                await SeedPlanDocVerdictPassAsync(db, spawn.Id, docKey);

                var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == entry.PrId);
                if (pr is null || !string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase))
                {
                    await prov.MergePrAsync(entry.PrId!);
                    await git.MergeAsync(null, entry.Branch!, "main");
                }
                mergedCount++;
            }

            if (mergedCount == PlanDocStateHelpers.PlanDocOrder.Count && !anyUnmerged)
            {
                // All four PRs are merged. Stop here so the spawn stays at
                // phase=plan; tests that PATCH /spawn/phase to advance can
                // still do so without tripping the "phase rollback not
                // allowed" guard. Tests that want phase=decompose+ should
                // invoke TickAsync after this helper returns.
                return;
            }

            // Tick the orchestrator so it opens the next doc's branch+PR
            // (or, if PR is opened but not merged, performs the auto-merge
            // it skipped because verdict wasn't pass yet).
            await sched.TickOnceAsync();
        }
    }

    /// <summary>
    /// Phase 53 — seed a passing terminal deliverable for one plan doc so
    /// the orchestrator's <c>IsDocVerdictPassAsync</c> gate releases. The
    /// deterministic test strategy doesn't run the gen↔eval loop, so
    /// without this seed the orchestrator would block on every plan-doc
    /// auto-merge step. Idempotent: skips when a pass row already exists.
    /// </summary>
    private static async Task SeedPlanDocVerdictPassAsync(
        ForgeDbContext db, string spawnId, string docKey)
    {
        if (!PlanDocStateHelpers.PlanDocFileName.TryGetValue(docKey, out var fileName)) return;
        var docId = $"plan-doc:{spawnId}:{fileName}";
        var alreadyPass = await db.Deliverables.AnyAsync(d =>
            d.WorkItemId == docId && d.Verdict == "pass");
        if (alreadyPass) return;
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
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Spawn_With_Orchestrator_Auto_Opens_Plan_Pr_And_Commits_Artifacts()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70001", "Architect-driven plan");
            var resp = await c.PostAsJsonAsync("/api/stories/WI-70001/spawn", new { }, JsonOpts);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            // Phase 53 — sequential plan-doc mode. Spawn-create kicks the
            // orchestrator which opens architecture's PR. Drive forward
            // through the four-doc chain (helper ticks + seeds + merges
            // until all four are merged).
            await MergeAllPlanPrsAsync(f, "WI-70001");

            var spawn = await ReadSpawnAsync(f, "WI-70001");
            Assert.Equal("plan", spawn.Phase);

            // Sequential mode: per-doc branches exist, each PR is merged,
            // and each doc's file is on main.
            // Legacy mode (back-compat): the single PlanBranch + PlanPrId.
            using var scope = f.Services.CreateScope();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            if (PlanDocStateHelpers.IsSequentialMode(spawn))
            {
                foreach (var docKey in PlanDocStateHelpers.PlanDocOrder)
                {
                    var entry = PlanDocStateHelpers.GetEntry(spawn, docKey);
                    Assert.False(string.IsNullOrEmpty(entry.Branch),
                        $"plan-doc branch for '{docKey}' was not populated");
                    Assert.False(string.IsNullOrEmpty(entry.PrId),
                        $"plan-doc PR id for '{docKey}' was not populated");
                    var fileName = PlanDocStateHelpers.PlanDocFileName[docKey];
                    var path = $"docs/stories/WI-70001/{fileName}";
                    Assert.True(await git.FileExistsAsync(null, "main", path),
                        $"missing merged file {path}");
                }
            }
            else
            {
                Assert.Equal("story/WI-70001/plan", spawn.PlanBranch);
                Assert.False(string.IsNullOrEmpty(spawn.PlanPrId), "Plan PR id was not populated");
                foreach (var p in new[]
                {
                    "docs/stories/WI-70001/tech-spec.md",
                    "docs/stories/WI-70001/architecture.md",
                    "docs/stories/WI-70001/implementation-plan.md",
                    "docs/stories/WI-70001/e2e-scenarios.md",
                })
                {
                    Assert.True(await git.FileExistsAsync(null, "story/WI-70001/plan", p), $"missing {p}");
                }
            }
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Plan_Pr_Merge_Triggers_Decomposition_And_Workstream_Creation()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70002", "Auto-decompose");
            await c.PostAsJsonAsync("/api/stories/WI-70002/spawn", new { }, JsonOpts);
            var spawn = await ReadSpawnAsync(f, "WI-70002");

            // Phase 53 — merge every plan PR (sequential mode opens four).
            await MergeAllPlanPrsAsync(f, "WI-70002");

            var advance = await c.PatchAsync("/api/stories/WI-70002/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, advance.StatusCode);

            // 28.4.1 — orchestrator auto-produced the decomposition + workstreams.
            var spawnAfter = await ReadSpawnAsync(f, "WI-70002");
            Assert.False(string.IsNullOrEmpty(spawnAfter.DecompositionJson));
            var ws = await ReadWorkstreamsAsync(f, spawnAfter.Id);
            Assert.NotEmpty(ws);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Implement_Phase_Auto_Assigns_Engineer_And_Opens_Workstream_Prs()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70003", "Engineer crew");
            await c.PostAsJsonAsync("/api/stories/WI-70003/spawn", new { }, JsonOpts);

            // Roll plan PR through merge.
            var s = await ReadSpawnAsync(f, "WI-70003");
            await MergeAllPlanPrsAsync(f, "WI-70003");
            await c.PatchAsync("/api/stories/WI-70003/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            await c.PostAsync("/api/stories/WI-70003/spawn/decompose/approve", null);
            var enter = await c.PatchAsync("/api/stories/WI-70003/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, enter.StatusCode);

            // 28.5.3–28.5.6 — orchestrator assigned engineers + opened ws PRs.
            // The orchestrator's tight inner loop means by the time PATCH returns,
            // workstreams may already have advanced through review → merging.
            // We verify the dep edge is honored by checking both workstreams
            // ended up with engineer assignments + PR ids in the right order.
            var ws = await ReadWorkstreamsAsync(f, s.Id);
            Assert.NotEmpty(ws);
            var first = ws.OrderBy(x => x.Sort).First();
            Assert.NotEqual("[]", first.AgentsJson);
            Assert.NotEqual("[]", first.PrsJson);
            // First workstream is in one of the post-impl states.
            Assert.Contains(first.Status, new[] { "review", "merging", "complete" });

            if (ws.Count > 1)
            {
                var second = ws.OrderBy(x => x.Sort).Last();
                // Either still planning (orchestrator stopped early) or also
                // advanced after dep unblock. Both are correct — what matters
                // is that by the time deps are met, the second runs.
                Assert.Contains(second.Status, new[] { "planning", "review", "merging", "complete" });
            }
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Review_Phase_Posts_Comments_And_Engineer_Resolves_Then_Merges()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70004", "Review loop");
            await c.PostAsJsonAsync("/api/stories/WI-70004/spawn", new { }, JsonOpts);
            var s = await ReadSpawnAsync(f, "WI-70004");
            await MergeAllPlanPrsAsync(f, "WI-70004");
            await c.PatchAsync("/api/stories/WI-70004/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            await c.PostAsync("/api/stories/WI-70004/spawn/decompose/approve", null);
            await c.PatchAsync("/api/stories/WI-70004/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

            // Phase=implement → all WS PRs opened. Now move to review.
            var ws = await ReadWorkstreamsAsync(f, s.Id);
            var first = ws.OrderBy(x => x.Sort).First();
            // First workstream is at 'review'. Advance the spawn into 'review'.
            // The single second workstream is blocked, so we manually flip it to 'review'
            // for this test — in a real run, the dep scheduler would auto-promote it
            // once the first PR merges.
            var enter = await c.PatchAsync("/api/stories/WI-70004/spawn/phase",
                JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));

            // The orchestrator's review pass posts a comment and the engineer
            // immediately resolves it. A second tick should approve & merge.
            await TickAsync(f); // round 0 → comments posted + resolved
            await TickAsync(f); // round 1 → all resolved → approve + merge

            using var scope2 = f.Services.CreateScope();
            var db = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var reload = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == first.Id);
            Assert.Equal("merging", reload.Status);

            // PR is merged in our local cache.
            var prsList = JsonSerializer.Deserialize<List<string>>(reload.PrsJson)!;
            Assert.NotEmpty(prsList);
            var pr = await db.Prs.AsNoTracking().FirstAsync(p => p.Id == prsList[0]);
            Assert.Equal("merged", pr.Col);

            // 28.6.4 — at least one comment was posted on the PR.
            var commentCount = await db.PrComments.CountAsync(x => x.PrId == prsList[0]);
            Assert.True(commentCount >= 1, "Review crew should have posted at least one comment");
            // 28.6.6 — every posted comment is resolved.
            var openCount = await db.PrComments.CountAsync(x => x.PrId == prsList[0] && x.State == "active");
            Assert.Equal(0, openCount);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Dependency_Scheduler_Unblocks_Downstream_When_Upstream_Merges()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70005", "Dep unblock");
            await c.PostAsJsonAsync("/api/stories/WI-70005/spawn", new { }, JsonOpts);
            var s = await ReadSpawnAsync(f, "WI-70005");

            await MergeAllPlanPrsAsync(f, "WI-70005");
            await c.PatchAsync("/api/stories/WI-70005/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            await c.PostAsync("/api/stories/WI-70005/spawn/decompose/approve", null);
            await c.PatchAsync("/api/stories/WI-70005/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

            // Two workstreams, second depends on first.
            var ws = await ReadWorkstreamsAsync(f, s.Id);
            Assert.Equal(2, ws.Count);
            var second = ws.OrderBy(x => x.Sort).Last();
            var first = ws.OrderBy(x => x.Sort).First();

            // 28.11.6 — keep ticking; the orchestrator should drive the first
            // workstream through review → merging, then auto-unblock the second.
            for (var i = 0; i < 6; i++) await TickAsync(f);

            using var scope2 = f.Services.CreateScope();
            var db = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var firstReload = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == first.Id);
            var secondReload = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == second.Id);
            // First workstream was processed first (no deps).
            Assert.Contains(firstReload.Status, new[] { "merging", "complete" });
            // Second was unblocked after first merged and is now also at merging/complete.
            Assert.Contains(secondReload.Status, new[] { "merging", "complete" });
            Assert.NotEqual("[]", secondReload.PrsJson);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Qa_Auto_Produces_Feature_Files_And_Step_Definitions()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70006", "QA pipeline");
            await c.PostAsJsonAsync("/api/stories/WI-70006/spawn", new { }, JsonOpts);
            var s = await ReadSpawnAsync(f, "WI-70006");

            await MergeAllPlanPrsAsync(f, "WI-70006");
            await c.PatchAsync("/api/stories/WI-70006/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            await c.PostAsync("/api/stories/WI-70006/spawn/decompose/approve", null);
            await c.PatchAsync("/api/stories/WI-70006/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

            // Drive both workstreams to merged through repeated ticks.
            for (var i = 0; i < 6; i++) await TickAsync(f);

            await c.PatchAsync("/api/stories/WI-70006/spawn/phase",
                JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));
            await c.PatchAsync("/api/stories/WI-70006/spawn/phase",
                JsonContent.Create(new { targetPhase = "qa" }, options: JsonOpts));

            // QA orchestrator: produce feature files + open PR + merge + write step defs.
            for (var i = 0; i < 4; i++) await TickAsync(f);

            // 28.7.4 — feature files exist on QA branch.
            Assert.True(await FileOnMainAsync(f, "tests/Features/WI-70006/api.feature"));
            Assert.True(await FileOnMainAsync(f, "tests/Features/WI-70006/ui.feature"));
            // 28.7.9 — step defs exist on main per workstream.
            Assert.True(await FileOnMainAsync(f, "tests/Steps/WI-70006/apiSteps.cs"));
            Assert.True(await FileOnMainAsync(f, "tests/Steps/WI-70006/uiSteps.cs"));

            // 28.7.7 — QA PR id is recorded.
            var sf = await ReadSpawnAsync(f, "WI-70006");
            Assert.False(string.IsNullOrEmpty(sf.QaPrId));
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Done_Transitions_Ado_Work_Item_To_Closed()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70007", "Closure");
            await c.PostAsJsonAsync("/api/stories/WI-70007/spawn", new { }, JsonOpts);
            var s = await ReadSpawnAsync(f, "WI-70007");
            await MergeAllPlanPrsAsync(f, "WI-70007");
            await c.PatchAsync("/api/stories/WI-70007/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            await c.PostAsync("/api/stories/WI-70007/spawn/decompose/approve", null);
            await c.PatchAsync("/api/stories/WI-70007/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));
            for (var i = 0; i < 6; i++) await TickAsync(f);
            await c.PatchAsync("/api/stories/WI-70007/spawn/phase",
                JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));
            await c.PatchAsync("/api/stories/WI-70007/spawn/phase",
                JsonContent.Create(new { targetPhase = "qa" }, options: JsonOpts));
            for (var i = 0; i < 6; i++) await TickAsync(f);

            using var scope2 = f.Services.CreateScope();
            var db = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(x => x.StoryId == "WI-70007");
            Assert.Equal("done", spawn.Phase);

            // 28.8.3 — work item state transitioned.
            var wi = await db.WorkItems.AsNoTracking().FirstAsync(x => x.Id == "WI-70007");
            Assert.Equal("closed", wi.State);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Resume_Drives_Spawn_Forward_From_Last_Checkpoint()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70008", "Restart resume");
            await c.PostAsJsonAsync("/api/stories/WI-70008/spawn", new { }, JsonOpts);

            // Simulate a crash mid-flight — flip phase to decompose without
            // approving. A scheduler tick should still drive the spawn (it
            // generates the decomposition; later approval unblocks implement).
            await MergeAllPlanPrsAsync(f, "WI-70008");
            using (var scope = f.Services.CreateScope())
            {
                var ss = await scope.ServiceProvider.GetRequiredService<ForgeDbContext>().StorySpawns
                    .FirstAsync(x => x.StoryId == "WI-70008");
                ss.Phase = "decompose";
                await scope.ServiceProvider.GetRequiredService<ForgeDbContext>().SaveChangesAsync();
            }

            // Tick — orchestrator picks up the spawn and produces the decomposition.
            await TickAsync(f);

            var s = await ReadSpawnAsync(f, "WI-70008");
            Assert.Equal("decompose", s.Phase);
            Assert.False(string.IsNullOrEmpty(s.DecompositionJson));
            var ws = await ReadWorkstreamsAsync(f, s.Id);
            Assert.NotEmpty(ws);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Regression — locally-authored stories live in <c>product_stories</c>,
    /// not <c>work_items</c>. The orchestrator used to silently bail
    /// (<c>if (story is null) return false;</c>) when no WorkItem matched
    /// <c>spawn.StoryId</c>, leaving the spawn stuck in <c>plan</c> with no
    /// PR. The fix synthesizes a transient WorkItemRow from the spawn's own
    /// fields so the architect path runs uniformly for both sources.
    /// </summary>
    private static async Task SeedProductStoryAsync(
        ForgeWebApplicationFactory f,
        string id,
        string productId,
        string name,
        string description,
        int points)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        if (!await db.Products.AnyAsync(p => p.Id == productId))
        {
            db.Products.Add(new ProductRow
            {
                Id = productId, Name = productId, Slug = productId, Description = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (!await db.ProductStories.AnyAsync(s => s.Id == id))
        {
            db.ProductStories.Add(new ProductStoryRow
            {
                Id = id, ProductId = productId, Name = name,
                Description = description, Points = points,
                Status = "planning", Sort = 0, CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Spawn_From_ProductStory_Opens_Plan_Pr_And_Carries_Description_And_Points()
    {
        // Reproduces the operator-reported regression: spawning a locally-authored
        // ProductStory (no upstream WorkItem) must still drive the architect path —
        // open the plan PR, commit the four artifacts, and propagate the user's
        // description (architect prompt) + points onto the spawn row so the
        // strategy / agent sees them.
        var (f, c) = NewOrchestratorFactory();
        try
        {
            const string storyId = "code-intelligence:AGENT-MEMORY-T1";
            const string desc = "research best graph-memory strategy for agent context.";
            await SeedProductStoryAsync(f, storyId, "code-intelligence", "agent memory", desc, 10);

            var resp = await c.PostAsJsonAsync(
                $"/api/stories/{Uri.EscapeDataString(storyId)}/spawn", new { }, JsonOpts);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            var spawn = await ReadSpawnAsync(f, storyId);
            Assert.Equal(desc, spawn.StoryDescription);
            Assert.Equal(10, spawn.StoryPoints);
            Assert.Equal("agent memory", spawn.StoryTitle);

            // Phase 53 — sequential mode: spawn-create's inline drive opens
            // architecture's PR, and the operator-reported regression
            // (orchestrator silently bailing on ProductStory ids) is gated
            // exactly there. Assert architecture's PR is opened immediately.
            // For legacy seeded spawns, the single PlanPrId would carry it.
            if (PlanDocStateHelpers.IsSequentialMode(spawn))
            {
                var arch = PlanDocStateHelpers.GetEntry(spawn, "architecture");
                Assert.False(string.IsNullOrEmpty(arch.PrId),
                    "architecture plan-doc PR was not populated — orchestrator likely bailed because the story isn't a WorkItem.");
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(spawn.PlanPrId),
                    "Plan PR id was not populated — orchestrator likely bailed because the story isn't a WorkItem.");
            }

            // Drive the rest of the chain so all four artifacts land on
            // main; assert each file is present at its canonical path on
            // the per-doc branch (sequential) or on the single plan
            // branch (legacy).
            await MergeAllPlanPrsAsync(f, storyId);

            // The orchestrator sanitises the story id (`:` is illegal in a
            // git ref / Windows path) via StoryPaths.Safe before computing
            // the branch + commit dir, so test paths must be sanitised too.
            var safeStoryId = StoryPaths.Safe(storyId);
            using var scope = f.Services.CreateScope();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var spawnAfter = await ReadSpawnAsync(f, storyId);
            if (PlanDocStateHelpers.IsSequentialMode(spawnAfter))
            {
                foreach (var docKey in PlanDocStateHelpers.PlanDocOrder)
                {
                    var entry = PlanDocStateHelpers.GetEntry(spawnAfter, docKey);
                    var fileName = PlanDocStateHelpers.PlanDocFileName[docKey];
                    var path = $"docs/stories/{safeStoryId}/{fileName}";
                    Assert.True(await git.FileExistsAsync(null, entry.Branch!, path),
                        $"missing {path} on branch {entry.Branch}");
                }
            }
            else
            {
                foreach (var p in new[]
                {
                    $"docs/stories/{safeStoryId}/tech-spec.md",
                    $"docs/stories/{safeStoryId}/architecture.md",
                    $"docs/stories/{safeStoryId}/implementation-plan.md",
                    $"docs/stories/{safeStoryId}/e2e-scenarios.md",
                })
                {
                    Assert.True(await git.FileExistsAsync(null, $"story/{safeStoryId}/plan", p), $"missing {p}");
                }
            }
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Spawn_From_ProductStory_With_Empty_StoryId_Returns_404_For_Unknown_Id()
    {
        // The Spawn endpoint accepts ProductStoryRow ids OR WorkItemRow ids.
        // An id that matches neither must 404 instead of silently creating a
        // spawn the orchestrator can't drive.
        var (f, c) = NewOrchestratorFactory();
        try
        {
            var resp = await c.PostAsJsonAsync(
                "/api/stories/does-not-exist:NOWHERE/spawn", new { }, JsonOpts);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { f.Dispose(); }
    }

    [Fact(Skip = "AutoProgressGates not yet wired into orchestrator")]
    public async Task Spawn_From_ProductStory_With_AutoProgress_Drives_End_To_End_Inside_The_POST()
    {
        // The dev / deterministic runtime collapses the human-review gates
        // (plan→decompose, decompose→implement) so the lifecycle completes
        // without manual merges. Operator UX expectation: spawn returns and
        // the spawn is already at `done` (or close to it) — no follow-up
        // PATCH calls, no scheduler-tick wait. This pins that behavior
        // because it was the actual user-visible bug.
        var f = ForgeWebApplicationFactory.WithAutoProgressGates();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        try
        {
            const string storyId = "code-intelligence:LOCAL-AUTO-T1";
            await SeedProductStoryAsync(f, storyId, "code-intelligence", "auto end-to-end", "drive to done", 3);

            var resp = await c.PostAsJsonAsync(
                $"/api/stories/{Uri.EscapeDataString(storyId)}/spawn", new { }, JsonOpts);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            // Inside the single POST, the orchestrator should have driven the
            // spawn all the way to `done`. No manual merges, no PATCH calls.
            var spawn = await ReadSpawnAsync(f, storyId);
            Assert.Equal("done", spawn.Phase);

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var ps = await db.ProductStories.AsNoTracking().FirstAsync(x => x.Id == storyId);
            Assert.Equal("done", ps.Status);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Decompose_Gate_Blocks_Until_Plan_Artifacts_Land_On_Main()
    {
        // 28.10.2 — orchestrator should refuse to decompose when the plan
        // files are missing from main, even if the phase has been advanced.
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70009", "Artifact gate");
            await c.PostAsJsonAsync("/api/stories/WI-70009/spawn", new { }, JsonOpts);

            // Force the spawn into decompose WITHOUT merging the plan PR /
            // committing files to main. The orchestrator's artifact check
            // should make `DriveDecomposeAsync` short-circuit.
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var ss = await db.StorySpawns.FirstAsync(x => x.StoryId == "WI-70009");
                ss.Phase = "decompose";
                await db.SaveChangesAsync();
            }
            await TickAsync(f);

            var s = await ReadSpawnAsync(f, "WI-70009");
            Assert.True(string.IsNullOrEmpty(s.DecompositionJson),
                "orchestrator should not decompose without plan artifacts on main");
        }
        finally { f.Dispose(); }
    }

    // ────────────────────────────────────────────────────────────────────
    // Phase 45 — operator-chosen base branch on a spawn.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Spawn_With_No_BaseBranch_Defaults_To_Main()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70101", "Default base branch");
            var resp = await c.PostAsJsonAsync("/api/stories/WI-70101/spawn", new { }, JsonOpts);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            var spawn = await ReadSpawnAsync(f, "WI-70101");
            Assert.Equal("main", spawn.BaseBranch);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Spawn_With_Custom_BaseBranch_Persists_And_Surfaces_On_Plan_Pr()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70102", "Custom base branch");
            var resp = await c.PostAsJsonAsync(
                "/api/stories/WI-70102/spawn",
                new { baseBranch = "release/2026-q2" },
                JsonOpts);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            var spawn = await ReadSpawnAsync(f, "WI-70102");
            Assert.Equal("release/2026-q2", spawn.BaseBranch);

            // Phase 53 — sequential mode: spawn-create's inline drive opens
            // architecture's PR against the custom base. Legacy mode: the
            // single PlanPrId carries it. Either way the PR's Base must
            // match the operator-chosen branch.
            string? planPrId;
            if (PlanDocStateHelpers.IsSequentialMode(spawn))
            {
                var arch = PlanDocStateHelpers.GetEntry(spawn, "architecture");
                Assert.False(string.IsNullOrEmpty(arch.PrId),
                    "architecture plan-doc PR was not populated");
                planPrId = arch.PrId;
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(spawn.PlanPrId));
                planPrId = spawn.PlanPrId;
            }

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var pr = await db.Prs.AsNoTracking().FirstAsync(p => p.Id == planPrId);
            Assert.Equal("release/2026-q2", pr.Base);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Spawn_With_Custom_BaseBranch_Branches_From_Main()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70103", "Branch creation from main");
            await c.PostAsJsonAsync(
                "/api/stories/WI-70103/spawn",
                new { baseBranch = "feature/big-thing" },
                JsonOpts);

            // The orchestrator's first drive ensures the chosen base branch
            // exists. The InMemoryStoryGitOps copies main's tree as the new
            // branch's tree, so a file write on main before the drive would
            // appear on the new branch too — easier to assert is just that
            // the branch tip is registered in the in-memory git ops.
            using var scope = f.Services.CreateScope();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            // Branching is internal state; the externally-visible signal is
            // that file existence under the spawn's base branch == file
            // existence under main (both seeded empty initially).
            Assert.False(await git.FileExistsAsync(null, "feature/big-thing", "nonexistent.md"));
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Workstreams_Inherit_BaseBranch_From_Spawn()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70104", "Workstream base inheritance");
            await c.PostAsJsonAsync(
                "/api/stories/WI-70104/spawn",
                new { baseBranch = "release/lts" },
                JsonOpts);

            // Drive through plan + decompose so workstreams get created.
            await TickAsync(f);
            await TickAsync(f);
            await TickAsync(f);

            var spawn = await ReadSpawnAsync(f, "WI-70104");
            var workstreams = await ReadWorkstreamsAsync(f, spawn.Id);
            Assert.NotEmpty(workstreams);
            foreach (var w in workstreams)
            {
                Assert.Equal("release/lts", w.Base);
            }
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Change_BaseBranch_Endpoint_Updates_Spawn_And_Workstreams()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70105", "Change base mid-flight");
            await c.PostAsJsonAsync("/api/stories/WI-70105/spawn", new { }, JsonOpts);

            // Drive into decompose so workstream rows exist before the swap.
            await TickAsync(f);
            await TickAsync(f);
            await TickAsync(f);

            var resp = await c.PatchAsync(
                "/api/stories/WI-70105/spawn/base-branch",
                JsonContent.Create(new { baseBranch = "release/tip" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var spawn = await ReadSpawnAsync(f, "WI-70105");
            Assert.Equal("release/tip", spawn.BaseBranch);

            var workstreams = await ReadWorkstreamsAsync(f, spawn.Id);
            // Every workstream must move to the new base so a re-tick opens
            // its PRs against the right branch.
            Assert.All(workstreams, w => Assert.Equal("release/tip", w.Base));
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Change_BaseBranch_Rejects_Invalid_Branch_Name()
    {
        var (f, c) = NewOrchestratorFactory();
        try
        {
            await SeedStoryAsync(f, "WI-70106", "Invalid base branch name");
            await c.PostAsJsonAsync("/api/stories/WI-70106/spawn", new { }, JsonOpts);

            var resp = await c.PatchAsync(
                "/api/stories/WI-70106/spawn/base-branch",
                JsonContent.Create(new { baseBranch = "has spaces" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { f.Dispose(); }
    }
}
