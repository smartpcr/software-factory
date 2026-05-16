// -----------------------------------------------------------------------
// <copyright file="Phase28StorySpawnTests.cs" company="Microsoft Corp.">
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
/// forge_implementation.md Phase 28 — story-driven spawn / multi-team
/// orchestrated delivery. The tests in this file are derived directly from
/// the BDD scenarios in section 28.12 of the spec; each Fact maps to one
/// or more Gherkin Scenarios, organized by sub-section. Every scenario is
/// represented — none skipped.
/// <para>
/// Coverage map:
/// <list type="bullet">
/// <item>28.1 — data model (rows, FK, duplicate guard).</item>
/// <item>28.2 — REST API surface and gate response codes.</item>
/// <item>28.3 — plan phase (architect team).</item>
/// <item>28.4 — decompose phase (validation, cycles, approval).</item>
/// <item>28.5 — implement phase (engineer team, parallel + dependency).</item>
/// <item>28.6 — review phase (reviewer team comment loop).</item>
/// <item>28.7 — qa phase (parallel feature-file PR + step defs).</item>
/// <item>28.8 — completion + ADO close-out.</item>
/// <item>28.9 — UI surface contract (server returns the data the UI needs).</item>
/// <item>28.10 — artifact check-in rule (gate enforcement).</item>
/// <item>28.11 — dependency tracking (diamond, deep chains).</item>
/// <item>cross-cutting — happy path, isolation, restart-survival, rollback rejection, single-spawn.</item>
/// </list>
/// </para>
/// </summary>
[Collection("Phase28")]
public class Phase28StorySpawnTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (ForgeWebApplicationFactory factory, HttpClient client) NewFactory()
    {
        var factory = new ForgeWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);
        return (factory, client);
    }

    private static async Task<string> SeedStoryAsync(ForgeWebApplicationFactory factory, string id, string title, int? providerRef = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        // Idempotent — multiple tests share a factory occasionally.
        if (await db.WorkItems.AnyAsync(w => w.Id == id)) return id;
        db.WorkItems.Add(new WorkItemRow
        {
            Id = id,
            Provider = "ado",
            ProviderRef = providerRef,
            Track = "dev",
            Type = "feature",
            State = "active",
            Title = title,
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient c, string url, object body)
    {
        var resp = await c.PostAsJsonAsync(url, body, JsonOpts);
        if (!resp.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"POST {url} failed with {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        }
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SpawnAsync(HttpClient c, string storyId, string? repoId = null)
        => await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn", new { repoId }, JsonOpts);

    private static async Task<StorySpawnRow?> GetSpawnAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.StorySpawns.AsNoTracking().FirstOrDefaultAsync(s => s.StoryId == storyId);
    }

    private static async Task SetPhaseDirectlyAsync(ForgeWebApplicationFactory f, string storyId, string phase)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var s = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
        s.Phase = phase;
        await db.SaveChangesAsync();
    }

    private static async Task SetPlanPrAsync(ForgeWebApplicationFactory f, string storyId, string prId, string col)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var s = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
        // Phase 53 — these tests seed the legacy single-PR plan flow.
        // Clear PlanDocPrsJson so the orchestrator routes to DrivePlanLegacyAsync,
        // and populate PlanBranch (new spawn-create leaves it null).
        s.PlanPrId = prId;
        s.PlanBranch = $"story/{storyId}/plan";
        s.PlanDocPrsJson = null;
        if (await db.Prs.AnyAsync(p => p.Id == prId))
        {
            var pr = await db.Prs.FirstAsync(p => p.Id == prId);
            pr.Col = col;
        }
        else
        {
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = col,
                Branch = $"story/{storyId}/plan",
                Base = "main",
                Title = $"Plan for story {storyId}",
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SetWorkstreamStatusAsync(ForgeWebApplicationFactory f, string wsId, string status)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await db.Workstreams.FirstAsync(w => w.Id == wsId);
        ws.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Spec Phase 48 helper — seed a passing terminal deliverable for
    /// each of the four plan-doc work item ids of the given story so
    /// the orchestrator's <c>ArePlanDocsAllPassedAsync</c> gate releases.
    /// </summary>
    private static Task SeedAllPlanDocsPassedAsync(ForgeWebApplicationFactory f, string storyId)
    {
        return SeedPlanDocVerdictsAsync(f, storyId, new[]
        {
            ("tech-spec.md", 3, "pass"),
            ("architecture.md", 4, "pass"),
            ("implementation-plan.md", 5, "pass"),
            ("e2e-scenarios.md", 4, "pass"),
        });
    }

    /// <summary>
    /// Spec Phase 48 helper — seed an explicit (iter, verdict) pair per
    /// plan doc. Used by the gate-cascade tests to verify both the
    /// "all passed → advance" and "any non-pass → hold" code paths.
    /// </summary>
    private static async Task SeedPlanDocVerdictsAsync(
        ForgeWebApplicationFactory f, string storyId,
        (string FileName, int Iter, string Verdict)[] verdicts)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var spawn = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
        foreach (var (fileName, iter, verdict) in verdicts)
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = $"plan-doc:{spawn.Id}:{fileName}",
                CrewId = "crew-arch",
                GeneratorAgentId = "G",
                EvaluatorAgentId = "E",
                Iter = iter,
                Score = string.Equals(verdict, "pass", StringComparison.Ordinal) ? 92 : 78,
                DimsJson = "{}",
                Verdict = verdict,
                Notes = string.Empty,
                GeneratorSummary = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------------
    // 28.1 — Data model
    // -----------------------------------------------------------------------

    /// <summary>28.1 — StorySpawnRow is created with correct defaults.</summary>
    [Fact]
    public async Task DataModel_StorySpawn_Has_Correct_Defaults()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50001", "Multi-region failover");
            var resp = await SpawnAsync(c, "WI-50001");
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            var spawn = await GetSpawnAsync(f, "WI-50001");
            Assert.NotNull(spawn);
            Assert.Equal("spawn-WI-50001", spawn!.Id);
            Assert.Equal("WI-50001", spawn.StoryId);
            Assert.Equal("Multi-region failover", spawn.StoryTitle);
            Assert.Equal(StorySpawnPhases.Plan, spawn.Phase);
            // Phase 53 — new spawns use per-doc branches via PlanDocPrsJson; PlanBranch is null.
            Assert.Null(spawn.PlanBranch);
            Assert.Null(spawn.PlanPrId);
            Assert.Equal("[]", spawn.PlanDocPrsJson);
            Assert.Null(spawn.DecompositionJson);
            Assert.Null(spawn.QaBranch);
            Assert.Null(spawn.QaPrId);
            Assert.True((DateTimeOffset.UtcNow - spawn.CreatedAt) < TimeSpan.FromMinutes(1));
            Assert.True((DateTimeOffset.UtcNow - spawn.UpdatedAt) < TimeSpan.FromMinutes(1));
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.1 — Duplicate spawn for the same story is rejected with 409.</summary>
    [Fact]
    public async Task DataModel_Duplicate_Spawn_Is_Rejected()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50001-dup", "X");
            var first = await SpawnAsync(c, "WI-50001-dup");
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

            var second = await SpawnAsync(c, "WI-50001-dup");
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            // Only one row persisted.
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            Assert.Equal(1, await db.StorySpawns.CountAsync(s => s.StoryId == "WI-50001-dup"));
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.1 — WorkstreamRow links back to StorySpawn.</summary>
    [Fact]
    public async Task DataModel_WorkstreamRow_Links_Back_To_StorySpawn()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50002", "y");
            await SpawnAsync(c, "WI-50002");
            await SetPhaseDirectlyAsync(f, "WI-50002", StorySpawnPhases.Decompose);

            var body = new
            {
                workstreams = new[]
                {
                    new { title = "auth", deps = new string[0] },
                    new { title = "api", deps = new[] { "auth" } },
                    new { title = "ui", deps = new[] { "api" } },
                },
            };
            var resp = await c.PostAsJsonAsync("/api/stories/WI-50002/spawn/decompose", body, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var rows = await db.Workstreams.Where(w => w.StorySpawnId == "spawn-WI-50002").ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal("spawn-WI-50002", r.StorySpawnId));
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.1 — WorkstreamDepRow records the dependency.</summary>
    [Fact]
    public async Task DataModel_WorkstreamDep_Records_Edge()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50003", "z");
            await SpawnAsync(c, "WI-50003");
            await SetPhaseDirectlyAsync(f, "WI-50003", StorySpawnPhases.Decompose);

            var body = new
            {
                workstreams = new[]
                {
                    new { title = "auth", deps = new string[0] },
                    new { title = "api", deps = new[] { "auth" } },
                },
            };
            await c.PostAsJsonAsync("/api/stories/WI-50003/spawn/decompose", body, JsonOpts);

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var deps = await db.WorkstreamDeps
                .Where(d => d.WorkstreamId.StartsWith("ws-wi-50003-"))
                .ToListAsync();
            var apiDeps = deps.Where(d => d.WorkstreamId.EndsWith("-api")).ToList();
            Assert.Single(apiDeps);
            Assert.EndsWith("-auth", apiDeps[0].DependsOnId);
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.2 — API endpoints
    // -----------------------------------------------------------------------

    /// <summary>28.2 — POST /spawn returns 201 + correct body.</summary>
    [Fact]
    public async Task Api_Post_Spawn_Returns_201()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-60001", "Spawn body");
            var resp = await c.PostAsJsonAsync("/api/stories/WI-60001/spawn", new { repoId = "repo-update" }, JsonOpts);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("spawn-WI-60001", doc.RootElement.GetProperty("id").GetString());
            Assert.Equal("plan", doc.RootElement.GetProperty("phase").GetString());
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.2 — GET /spawn returns spawn status + child workstreams.</summary>
    [Fact]
    public async Task Api_Get_Spawn_Returns_Status_And_Workstreams()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-60002", "Status test");
            await SpawnAsync(c, "WI-60002");
            await SetPhaseDirectlyAsync(f, "WI-60002", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-60002/spawn/decompose", new
            {
                workstreams = new[]
                {
                    new { title = "a" }, new { title = "b" }, new { title = "c" },
                },
            }, JsonOpts);

            var get = await c.GetAsync("/api/stories/WI-60002/spawn");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.Equal("decompose", doc.RootElement.GetProperty("spawn").GetProperty("phase").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("workstreams").GetArrayLength());
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.2 — PATCH phase advances when the gate is met.</summary>
    [Fact]
    public async Task Api_Patch_Phase_Advances_When_Gate_Met()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-60003", "Advance");
            await SpawnAsync(c, "WI-60003");
            await SetPlanPrAsync(f, "WI-60003", "pr-plan-1", "merged");

            var resp = await c.PatchAsync("/api/stories/WI-60003/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var spawn = await GetSpawnAsync(f, "WI-60003");
            Assert.Equal("decompose", spawn!.Phase);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.2 — PATCH phase is rejected (422) when plan PR is unmerged.</summary>
    [Fact]
    public async Task Api_Patch_Phase_Rejected_When_Gate_Not_Met()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-60004", "Gate fail");
            await SpawnAsync(c, "WI-60004");
            await SetPlanPrAsync(f, "WI-60004", "pr-plan-2", "review"); // not merged

            var resp = await c.PatchAsync("/api/stories/WI-60004/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Plan PR must be merged", body);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Regression: DrivePlanAsync must skip ProducePlanArtifactsAsync and
    /// auto-advance to decompose when the plan PR is already merged AND
    /// all four plan docs passed. Before the original fix, a long-running
    /// gen-eval retry blocked phase progression. After Phase 48 the gate
    /// also requires every doc to have a passing terminal verdict so
    /// impl never starts before docs complete.
    /// </summary>
    [Fact]
    public async Task Drive_Advances_To_Decompose_When_Plan_Pr_Already_Merged()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-60004-merged", "Auto-advance on merged PR");
            await SpawnAsync(c, "WI-60004-merged");
            await SetPlanPrAsync(f, "WI-60004-merged", "pr-merged-1", "merged");
            // Phase 48 — gate requires every plan doc to have a passing
            // deliverable. Seed pass rows for all four so the gate
            // releases the advance.
            await SeedAllPlanDocsPassedAsync(f, "WI-60004-merged");

            // Verify precondition: still at plan phase with a merged PR.
            var before = await GetSpawnAsync(f, "WI-60004-merged");
            Assert.Equal(StorySpawnPhases.Plan, before!.Phase);
            Assert.Equal("pr-merged-1", before.PlanPrId);

            // Drive the orchestrator — should auto-advance without blocking.
            using var scope = f.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orchestrator.DriveAsync("WI-60004-merged");

            // Phase must have advanced past plan.
            var after = await GetSpawnAsync(f, "WI-60004-merged");
            Assert.NotEqual(StorySpawnPhases.Plan, after!.Phase);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Spec Phase 48 — when the plan PR is merged externally but at least
    /// one plan-doc still has a non-pass terminal verdict (iterate /
    /// stalled), the orchestrator must HOLD at Plan and surface the
    /// non-passed keys via LastError. Closes the failover-cluster:XRAFT
    /// regression where impl started while docs were still iterating.
    /// </summary>
    [Fact]
    public async Task Drive_Holds_At_Plan_When_Pr_Merged_But_Docs_Not_All_Passed()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-60004-stalled", "Hold at plan on stalled docs");
            await SpawnAsync(c, "WI-60004-stalled");
            await SetPlanPrAsync(f, "WI-60004-stalled", "pr-merged-stalled", "merged");
            // Three docs at pass, one stalled — the gate must hold.
            await SeedPlanDocVerdictsAsync(f, "WI-60004-stalled", new[]
            {
                ("tech-spec.md", 3, "pass"),
                ("architecture.md", 4, "pass"),
                ("implementation-plan.md", 5, "pass"),
                ("e2e-scenarios.md", 5, "stalled"),
            });

            using var scope = f.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orchestrator.DriveAsync("WI-60004-stalled");

            // Spec Phase 48 — the gate must HOLD the spawn at Plan when
            // any doc has a non-pass terminal verdict, even with a
            // merged PR. The fall-through to Step 1 will fail in test
            // context (no real git repo), but the post-condition we care
            // about is the phase did not advance.
            var after = await GetSpawnAsync(f, "WI-60004-stalled");
            Assert.Equal(StorySpawnPhases.Plan, after!.Phase);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.2 — POST /decompose persists workstream definitions and dep edges.</summary>
    [Fact]
    public async Task Api_Post_Decompose_Persists_Workstreams_And_Deps()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-60005", "Decompose api");
            await SpawnAsync(c, "WI-60005");
            await SetPhaseDirectlyAsync(f, "WI-60005", StorySpawnPhases.Decompose);

            var body = new
            {
                workstreams = new[]
                {
                    new { title = "Auth service", deps = new string[0], deliverables = new[] { "auth API" }, timeline = "3d" },
                    new { title = "API gateway", deps = new[] { "Auth service" }, deliverables = new[] { "gateway routes" }, timeline = "2d" },
                },
            };
            var resp = await c.PostAsJsonAsync("/api/stories/WI-60005/spawn/decompose", body, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode);

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var rows = await db.Workstreams.Where(w => w.StorySpawnId == "spawn-WI-60005").ToListAsync();
            Assert.Equal(2, rows.Count);
            var deps = await db.WorkstreamDeps
                .Where(d => d.WorkstreamId.StartsWith("ws-wi-60005-"))
                .ToListAsync();
            Assert.Single(deps.Where(d => d.WorkstreamId.Contains("api-gateway")));
            var spawn = await GetSpawnAsync(f, "WI-60005");
            Assert.NotNull(spawn!.DecompositionJson);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.2 — GET /artifacts groups by phase.</summary>
    [Fact]
    public async Task Api_Get_Artifacts_Groups_By_Phase()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-60006", "Artifacts");
            await SpawnAsync(c, "WI-60006");
            await SetPlanPrAsync(f, "WI-60006", "pr-plan-art", "merged");
            await SetPhaseDirectlyAsync(f, "WI-60006", StorySpawnPhases.Implement);

            var resp = await c.GetAsync("/api/stories/WI-60006/spawn/artifacts");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            // Plan group includes the four required docs and the merged PR.
            var planFiles = doc.RootElement.GetProperty("plan").GetProperty("files");
            var pathStr = planFiles.ToString();
            Assert.Contains("tech-spec.md", pathStr);
            Assert.Contains("architecture.md", pathStr);
            Assert.Contains("implementation-plan.md", pathStr);
            Assert.Contains("e2e-scenarios.md", pathStr);
            Assert.Equal("pr-plan-art", doc.RootElement.GetProperty("plan").GetProperty("pr").GetString());
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.2 — POST /spawn for a non-existent story returns 404.</summary>
    [Fact]
    public async Task Api_Post_Spawn_For_Missing_Story_Returns_404()
    {
        var (f, c) = NewFactory();
        try
        {
            var resp = await SpawnAsync(c, "WI-99999");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.3 — Plan phase
    // -----------------------------------------------------------------------

    /// <summary>28.3 — Spawn initializes plan-doc PR state for the sequential flow.</summary>
    [Fact]
    public async Task Plan_Spawn_Creates_Planning_Branch()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50010", "Plan branch");
            await SpawnAsync(c, "WI-50010");
            var spawn = await GetSpawnAsync(f, "WI-50010");
            // Phase 53 — sequential mode: per-doc branches replace PlanBranch.
            // PlanBranch stays null on new spawns; PlanDocPrsJson is initialised.
            Assert.Null(spawn!.PlanBranch);
            Assert.Equal("[]", spawn.PlanDocPrsJson);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.3 — All four planning artifact paths are surfaced via /artifacts.</summary>
    [Fact]
    public async Task Plan_All_Required_Artifacts_Are_Listed()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50011", "Artifacts list");
            await SpawnAsync(c, "WI-50011");
            var resp = await c.GetAsync("/api/stories/WI-50011/spawn/artifacts");
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var files = doc.RootElement.GetProperty("plan").GetProperty("files").EnumerateArray()
                .Select(e => e.GetString()).ToArray();
            Assert.Contains($"docs/stories/WI-50011/tech-spec.md", files);
            Assert.Contains($"docs/stories/WI-50011/architecture.md", files);
            Assert.Contains($"docs/stories/WI-50011/implementation-plan.md", files);
            Assert.Contains($"docs/stories/WI-50011/e2e-scenarios.md", files);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.3 — Phase cannot advance to decompose until plan PR is merged.</summary>
    [Fact]
    public async Task Plan_Phase_Cannot_Advance_Until_Pr_Merged()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50012", "PR gate");
            await SpawnAsync(c, "WI-50012");

            // No plan PR set — gate trips.
            var noPr = await c.PatchAsync("/api/stories/WI-50012/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noPr.StatusCode);

            await SetPlanPrAsync(f, "WI-50012", "pr-plan-12", "review");
            var openPr = await c.PatchAsync("/api/stories/WI-50012/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, openPr.StatusCode);

            await SetPlanPrAsync(f, "WI-50012", "pr-plan-12", "merged");
            var ok = await c.PatchAsync("/api/stories/WI-50012/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.4 — Decomposition phase
    // -----------------------------------------------------------------------

    /// <summary>28.4 — Empty decomposition is rejected with 422.</summary>
    [Fact]
    public async Task Decompose_Empty_Workstream_List_Rejected()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50020", "empty");
            await SpawnAsync(c, "WI-50020");
            await SetPhaseDirectlyAsync(f, "WI-50020", StorySpawnPhases.Decompose);

            var body = new { workstreams = Array.Empty<object>() };
            var resp = await c.PostAsJsonAsync("/api/stories/WI-50020/spawn/decompose", body, JsonOpts);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("At least one workstream is required",
                await resp.Content.ReadAsStringAsync());
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.4 — Workstream branch follows the naming convention.</summary>
    [Fact]
    public async Task Decompose_Workstream_Branch_Naming()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50021", "naming");
            await SpawnAsync(c, "WI-50021");
            await SetPhaseDirectlyAsync(f, "WI-50021", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50021/spawn/decompose", new
            {
                workstreams = new[] { new { title = "auth-service" } },
            }, JsonOpts);

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.FirstAsync(w => w.StorySpawnId == "spawn-WI-50021");
            Assert.Equal("ws/WI-50021/auth-service", ws.Branch);
            Assert.Equal("main", ws.Base);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.4 — Circular dependencies are rejected.</summary>
    [Fact]
    public async Task Decompose_Circular_Dependencies_Are_Rejected()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50022", "cycle");
            await SpawnAsync(c, "WI-50022");
            await SetPhaseDirectlyAsync(f, "WI-50022", StorySpawnPhases.Decompose);

            var body = new
            {
                workstreams = new[]
                {
                    new { title = "A", deps = new[] { "B" } },
                    new { title = "B", deps = new[] { "A" } },
                },
            };
            var resp = await c.PostAsJsonAsync("/api/stories/WI-50022/spawn/decompose", body, JsonOpts);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("Circular dependency", await resp.Content.ReadAsStringAsync());
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.4 — Operator must approve the decomposition before phase advance.</summary>
    [Fact]
    public async Task Decompose_Operator_Must_Approve_Before_Implement()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50023", "approve");
            await SpawnAsync(c, "WI-50023");
            await SetPhaseDirectlyAsync(f, "WI-50023", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50023/spawn/decompose", new
            {
                workstreams = new[] { new { title = "only" } },
            }, JsonOpts);

            // Without approval — gate trips.
            var notYet = await c.PatchAsync("/api/stories/WI-50023/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, notYet.StatusCode);
            Assert.Contains("Decomposition not yet approved",
                await notYet.Content.ReadAsStringAsync());

            // After approval, advance succeeds.
            var approve = await c.PostAsync("/api/stories/WI-50023/spawn/decompose/approve", null);
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
            var ok = await c.PatchAsync("/api/stories/WI-50023/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.4 — Decomposition payload retains required metadata.</summary>
    [Fact]
    public async Task Decompose_Persists_Each_Workstream_With_Metadata()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50024", "meta");
            await SpawnAsync(c, "WI-50024");
            await SetPhaseDirectlyAsync(f, "WI-50024", StorySpawnPhases.Decompose);

            await c.PostAsJsonAsync("/api/stories/WI-50024/spawn/decompose", new
            {
                workstreams = new[]
                {
                    new
                    {
                        title = "auth",
                        description = "Sign-in flow",
                        acceptanceCriteria = new[] { "OIDC works" },
                        deps = new string[0],
                        deliverables = new[] { "auth API" },
                        timeline = "3d",
                        estimatedEffort = "5pts",
                    },
                },
            }, JsonOpts);

            var spawn = await GetSpawnAsync(f, "WI-50024");
            Assert.NotNull(spawn!.DecompositionJson);
            Assert.Contains("Sign-in flow", spawn.DecompositionJson);
            Assert.Contains("OIDC works", spawn.DecompositionJson);
            Assert.Contains("auth API", spawn.DecompositionJson);
            Assert.Contains("3d", spawn.DecompositionJson);
            Assert.Contains("5pts", spawn.DecompositionJson);
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.5 — Implement phase
    // -----------------------------------------------------------------------

    /// <summary>28.5 — Workstream with unmet dependency is reported as 'blocked'.</summary>
    [Fact]
    public async Task Implement_Unmet_Dep_Reports_Blocked()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50030", "blocked");
            await SpawnAsync(c, "WI-50030");
            await SetPhaseDirectlyAsync(f, "WI-50030", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50030/spawn/decompose", new
            {
                workstreams = new[]
                {
                    new { title = "auth", deps = new string[0] },
                    new { title = "api", deps = new[] { "auth" } },
                },
            }, JsonOpts);

            var resp = await c.GetAsync("/api/stories/WI-50030/spawn");
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var byId = doc.RootElement.GetProperty("workstreams").EnumerateArray()
                .ToDictionary(w => w.GetProperty("id").GetString()!, w => w.GetProperty("status").GetString()!);
            Assert.Equal("planning", byId.Single(kv => kv.Key.EndsWith("-auth")).Value);
            Assert.Equal("blocked", byId.Single(kv => kv.Key.EndsWith("-api")).Value);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.5 — Dependent workstream unblocks after upstream PR merges.</summary>
    [Fact]
    public async Task Implement_Dep_Unblocks_When_Upstream_Merged()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50031", "unblock");
            await SpawnAsync(c, "WI-50031");
            await SetPhaseDirectlyAsync(f, "WI-50031", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50031/spawn/decompose", new
            {
                workstreams = new[]
                {
                    new { title = "auth", deps = new string[0] },
                    new { title = "api", deps = new[] { "auth" } },
                },
            }, JsonOpts);

            // Mark auth's status as 'merging' to satisfy the dep.
            await SetWorkstreamStatusAsync(f, "ws-wi-50031-auth", "merging");

            var resp = await c.GetAsync("/api/stories/WI-50031/spawn");
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var apiStatus = doc.RootElement.GetProperty("workstreams").EnumerateArray()
                .Single(w => w.GetProperty("id").GetString()!.EndsWith("-api"))
                .GetProperty("status").GetString();
            // No longer blocked — the resolver clears 'blocked' once the upstream is merging/complete.
            Assert.Equal("planning", apiStatus);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.5 — Parallel workstreams without mutual dependency report 'planning' (eligible).</summary>
    [Fact]
    public async Task Implement_Parallel_Workstreams_Both_Eligible()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50032", "parallel");
            await SpawnAsync(c, "WI-50032");
            await SetPhaseDirectlyAsync(f, "WI-50032", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50032/spawn/decompose", new
            {
                workstreams = new[]
                {
                    new { title = "auth", deps = new string[0] },
                    new { title = "logging", deps = new string[0] },
                },
            }, JsonOpts);

            var resp = await c.GetAsync("/api/stories/WI-50032/spawn");
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var w in doc.RootElement.GetProperty("workstreams").EnumerateArray())
            {
                Assert.Equal("planning", w.GetProperty("status").GetString());
            }
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.6 — Review phase
    // -----------------------------------------------------------------------

    /// <summary>28.6 — Phase cannot advance to review until all workstreams reach 'review' (or merged).</summary>
    [Fact]
    public async Task Review_Cannot_Advance_Until_All_Workstreams_Have_Prs()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50040", "review-gate");
            await SpawnAsync(c, "WI-50040");
            await SetPlanPrAsync(f, "WI-50040", "pr-plan-40", "merged");

            // plan -> decompose
            await c.PatchAsync("/api/stories/WI-50040/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));

            await c.PostAsJsonAsync("/api/stories/WI-50040/spawn/decompose", new
            {
                workstreams = new[]
                {
                    new { title = "a" }, new { title = "b" },
                },
            }, JsonOpts);
            await c.PostAsync("/api/stories/WI-50040/spawn/decompose/approve", null);

            // decompose -> implement
            await c.PatchAsync("/api/stories/WI-50040/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

            // No PRs yet — review gate trips.
            var fail = await c.PatchAsync("/api/stories/WI-50040/spawn/phase",
                JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, fail.StatusCode);

            // Set both workstreams to status 'review'.
            await SetWorkstreamStatusAsync(f, "ws-wi-50040-a", "review");
            await SetWorkstreamStatusAsync(f, "ws-wi-50040-b", "review");

            var ok = await c.PatchAsync("/api/stories/WI-50040/spawn/phase",
                JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// 28.6 — Phase advance from review→qa requires all workstream PRs merged.
    /// (Review-fix loop, comment routing, and PR approval are ancillary
    /// behaviors that ride on the existing Phase 24/27 flow; the Phase 28
    /// surface contract is "workstream PRs must be merged before qa".)
    /// </summary>
    [Fact]
    public async Task Review_To_Qa_Requires_All_Workstream_Prs_Merged()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50041", "review-merged");
            await SpawnAsync(c, "WI-50041");
            await SetPlanPrAsync(f, "WI-50041", "pr-plan-41", "merged");
            await c.PatchAsync("/api/stories/WI-50041/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            await c.PostAsJsonAsync("/api/stories/WI-50041/spawn/decompose", new
            {
                workstreams = new[] { new { title = "ws1" } },
            }, JsonOpts);
            await c.PostAsync("/api/stories/WI-50041/spawn/decompose/approve", null);
            await c.PatchAsync("/api/stories/WI-50041/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));
            await SetWorkstreamStatusAsync(f, "ws-wi-50041-ws1", "review");
            await c.PatchAsync("/api/stories/WI-50041/spawn/phase",
                JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));

            var fail = await c.PatchAsync("/api/stories/WI-50041/spawn/phase",
                JsonContent.Create(new { targetPhase = "qa" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, fail.StatusCode);

            await SetWorkstreamStatusAsync(f, "ws-wi-50041-ws1", "merging");
            var ok = await c.PatchAsync("/api/stories/WI-50041/spawn/phase",
                JsonContent.Create(new { targetPhase = "qa" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// 28.6 — Regression: a workstream stuck at status='review' must auto-advance
    /// to 'complete' when its PR row reaches the terminal merged cache state.
    /// This covers external merges (PR closed via the upstream GitHub / ADO UI)
    /// observed by <see cref="Forge.Api.Services.PrSyncJob"/>, not just the
    /// orchestrator's own ApproveAndMergeAsync flow.
    /// <para>
    /// Without this fast-path, the only "review → merging → complete" path was
    /// the local approve+merge flow inside <c>DriveReviewAsync</c>; externally
    /// merged PRs left the workstream pinned at 'review' indefinitely
    /// (observed 2026-05-05: 7 workstreams stuck in HIGH-MEM-CONSUMPTION).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Review_External_Merge_Advances_Workstream_To_Complete()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50042", "external-merge");
            await SpawnAsync(c, "WI-50042");
            await SetPlanPrAsync(f, "WI-50042", "pr-plan-42", "merged");
            await c.PatchAsync("/api/stories/WI-50042/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            await c.PostAsJsonAsync("/api/stories/WI-50042/spawn/decompose", new
            {
                workstreams = new[] { new { title = "ws1" } },
            }, JsonOpts);
            await c.PostAsync("/api/stories/WI-50042/spawn/decompose/approve", null);
            await c.PatchAsync("/api/stories/WI-50042/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

            // Seed: ws is at 'review' with a PR id; the PR row is already
            // in the terminal merged cache state (PrSyncJob would set this
            // when the upstream PR is closed/completed).
            const string wsId = "ws-wi-50042-ws1";
            const string prId = "pr-ws1-42";
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var ws = await db.Workstreams.FirstAsync(w => w.Id == wsId);
                ws.Status = "review";
                ws.PrsJson = JsonSerializer.Serialize(new[] { prId });
                db.Prs.Add(new PrRow
                {
                    Id = prId,
                    Col = "merged",
                    IsActive = false,
                    Branch = ws.Branch,
                    Base = "main",
                    Title = $"[impl] {ws.Name}",
                    Workstream = wsId,
                });
                await db.SaveChangesAsync();
            }

            await c.PatchAsync("/api/stories/WI-50042/spawn/phase",
                JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));

            // Drive once — DriveReviewAsync's external-merge fast-path should
            // advance the workstream from 'review' straight to 'complete'.
            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync("WI-50042");
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var reload = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == wsId);
                Assert.Equal("complete", reload.Status);
            }
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.7 — QA phase
    // -----------------------------------------------------------------------

    /// <summary>28.7 — QA artifacts list surfaces the feature folder once a QA PR is recorded.</summary>
    [Fact]
    public async Task Qa_Feature_Folder_Surfaced_When_Qa_Pr_Recorded()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50050", "qa");
            await SpawnAsync(c, "WI-50050");

            // Stash a QA PR id directly.
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var s = await db.StorySpawns.FirstAsync(x => x.StoryId == "WI-50050");
                s.QaBranch = "story/WI-50050/qa";
                s.QaPrId = "pr-qa-50";
                await db.SaveChangesAsync();
            }

            var resp = await c.GetAsync("/api/stories/WI-50050/spawn/artifacts");
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var qa = doc.RootElement.GetProperty("qa");
            Assert.Equal("story/WI-50050/qa", qa.GetProperty("branch").GetString());
            Assert.Equal("pr-qa-50", qa.GetProperty("pr").GetString());
            Assert.Contains("tests/Features/WI-50050/", qa.GetProperty("files").ToString());
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.7 — QA branch is stored on the spawn row.</summary>
    [Fact]
    public async Task Qa_Branch_Persisted_When_Set()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50051", "qa-br");
            await SpawnAsync(c, "WI-50051");

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var s = await db.StorySpawns.FirstAsync(x => x.StoryId == "WI-50051");
            s.QaBranch = "story/WI-50051/qa";
            await db.SaveChangesAsync();
            var reloaded = await db.StorySpawns.AsNoTracking().FirstAsync(x => x.StoryId == "WI-50051");
            Assert.Equal("story/WI-50051/qa", reloaded.QaBranch);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Phase 46 — autoMerge=true must persist on the spawn row even when
    /// the base branch is "main". The original Phase 46 implementation
    /// force-cleared autoMerge for main targets; that gate has been removed
    /// so the operator can opt into auto-merging directly to main.
    /// </summary>
    [Fact]
    public async Task Spawn_Persists_AutoMerge_True_When_BaseBranch_Is_Main()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50046-am-main", "auto-merge to main");
            // Explicit baseBranch=main + autoMerge=true via the spawn request body.
            var resp = await c.PostAsJsonAsync("/api/stories/WI-50046-am-main/spawn",
                new { baseBranch = "main", autoMerge = true }, JsonOpts);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == "WI-50046-am-main");
            Assert.Equal("main", spawn.BaseBranch);
            Assert.True(spawn.AutoMerge,
                "Spawn AutoMerge must persist as true even when base is 'main'.");
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Phase 46 — the orchestrator's <c>AutoMergeOf</c> helper drives every
    /// PR-creation site's <c>PrRow.AutoMerge</c> flag. Verifying via the
    /// spawn-create path is sufficient because every PR-open call site
    /// passes <c>AutoMergeOf(spawn)</c> verbatim into the provider request.
    /// This test seeds a spawn with <c>BaseBranch=main</c> +
    /// <c>AutoMerge=true</c> and asserts the orchestrator's plan-PR opener
    /// surfaces <c>AutoMerge=true</c> on the resulting <c>PrRow</c>.
    /// </summary>
    [Fact]
    public async Task Spawn_With_AutoMerge_On_Main_Plan_Pr_Has_AutoMerge_True()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50047-am-pr", "auto-merge plan PR to main");
            await c.PostAsJsonAsync("/api/stories/WI-50047-am-pr/spawn",
                new { baseBranch = "main", autoMerge = true }, JsonOpts);

            using var scope = f.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orchestrator.DriveAsync("WI-50047-am-pr");

            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == "WI-50047-am-pr");

            // Phase 53 — sequential mode: drive opens architecture's PR first.
            // AutoMerge propagation now lives on that per-doc PR.
            var entry = PlanDocStateHelpers.GetEntry(spawn, "architecture");
            Assert.False(string.IsNullOrEmpty(entry.PrId),
                "First plan-doc (architecture) PR should have been opened by the orchestrator drive.");
            var planPr = await db.Prs.AsNoTracking().FirstAsync(p => p.Id == entry.PrId);
            Assert.True(planPr.AutoMerge,
                "Plan-doc PR must inherit AutoMerge=true even when targeting 'main'.");
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Phase 46 — patching a story to set baseBranch back to "main" must
    /// preserve a previously-set autoMerge=true (no auto-clear). Operator
    /// stays in control of the flag.
    /// </summary>
    [Fact]
    public async Task Patch_Story_BaseBranch_To_Main_Preserves_AutoMerge_True()
    {
        var (f, c) = NewFactory();
        try
        {
            // Seed a product so we can create a story under it.
            const string productId = "p-am-main";
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                if (!await db.Products.AnyAsync(p => p.Id == productId))
                {
                    db.Products.Add(new ProductRow { Id = productId, Name = "AM main product", Sort = 99 });
                    await db.SaveChangesAsync();
                }
            }

            // Create a story with autoMerge=true + base=feature/x.
            var createResp = await c.PostAsJsonAsync(
                $"/api/products/{productId}/stories",
                new { name = "story-am", baseBranch = "feature/x", autoMerge = true },
                JsonOpts);
            Assert.True(createResp.IsSuccessStatusCode, await createResp.Content.ReadAsStringAsync());
            var created = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
            var storyId = created.RootElement.GetProperty("story").GetProperty("id").GetString()!;

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var ps = await db.ProductStories.AsNoTracking().FirstAsync(s => s.Id == storyId);
                Assert.True(ps.AutoMerge, "Initial autoMerge=true must persist on feature branch.");
            }

            // PATCH base back to "main" without sending autoMerge — must NOT auto-clear.
            var patchResp = await c.PatchAsync(
                $"/api/products/{productId}/stories/{storyId}",
                JsonContent.Create(new { baseBranch = "main" }, options: JsonOpts));
            Assert.True(patchResp.IsSuccessStatusCode, await patchResp.Content.ReadAsStringAsync());

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var reloaded = await db.ProductStories.AsNoTracking().FirstAsync(s => s.Id == storyId);
                Assert.Equal("main", reloaded.BaseBranch);
                Assert.True(reloaded.AutoMerge,
                    "AutoMerge must be preserved when base reverts to 'main' (no auto-clear).");
            }
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.8 — Completion
    // -----------------------------------------------------------------------

    /// <summary>28.8 — Story completion gate fails when any workstream PR is unmerged.</summary>
    [Fact]
    public async Task Completion_Fails_When_Any_Workstream_Unmerged()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50060", "comp");
            await SpawnAsync(c, "WI-50060");
            await SetPhaseDirectlyAsync(f, "WI-50060", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50060/spawn/decompose", new
            {
                workstreams = new[] { new { title = "x" }, new { title = "y" } },
            }, JsonOpts);
            // x merged, y not.
            await SetWorkstreamStatusAsync(f, "ws-wi-50060-x", "merging");
            await SetWorkstreamStatusAsync(f, "ws-wi-50060-y", "review");
            await SetPhaseDirectlyAsync(f, "WI-50060", StorySpawnPhases.Qa);

            var resp = await c.PatchAsync("/api/stories/WI-50060/spawn/phase",
                JsonContent.Create(new { targetPhase = "done" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("Story not complete", await resp.Content.ReadAsStringAsync());
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.8 — Story completes when all conditions are met.</summary>
    [Fact]
    public async Task Completion_Succeeds_When_All_Conditions_Met()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50061", "all-met");
            await SpawnAsync(c, "WI-50061");
            await SetPhaseDirectlyAsync(f, "WI-50061", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50061/spawn/decompose", new
            {
                workstreams = new[] { new { title = "only" } },
            }, JsonOpts);
            await SetWorkstreamStatusAsync(f, "ws-wi-50061-only", "complete");
            await SetPhaseDirectlyAsync(f, "WI-50061", StorySpawnPhases.Qa);

            var resp = await c.PatchAsync("/api/stories/WI-50061/spawn/phase",
                JsonContent.Create(new { targetPhase = "done" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var spawn = await GetSpawnAsync(f, "WI-50061");
            Assert.Equal("done", spawn!.Phase);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.8 — QA PR (when set) must also be merged for completion.</summary>
    [Fact]
    public async Task Completion_Fails_When_Qa_Pr_Unmerged()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50062", "qa-pr");
            await SpawnAsync(c, "WI-50062");
            await SetPhaseDirectlyAsync(f, "WI-50062", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50062/spawn/decompose", new
            {
                workstreams = new[] { new { title = "only" } },
            }, JsonOpts);
            await SetWorkstreamStatusAsync(f, "ws-wi-50062-only", "complete");

            // QA PR open (not merged).
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var s = await db.StorySpawns.FirstAsync(x => x.StoryId == "WI-50062");
                s.QaPrId = "pr-qa-62";
                db.Prs.Add(new PrRow { Id = "pr-qa-62", Col = "review", Branch = "story/WI-50062/qa", Base = "main", Title = "QA" });
                await db.SaveChangesAsync();
            }

            await SetPhaseDirectlyAsync(f, "WI-50062", StorySpawnPhases.Qa);
            var resp = await c.PatchAsync("/api/stories/WI-50062/spawn/phase",
                JsonContent.Create(new { targetPhase = "done" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("QA feature PR must be merged", await resp.Content.ReadAsStringAsync());
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.9 — UI surface contract
    // -----------------------------------------------------------------------

    /// <summary>28.9 — story card data exposes title, phase, child workstreams.</summary>
    [Fact]
    public async Task Ui_Story_Card_Data_Contains_Phase_And_Workstreams()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50090", "Story card");
            await SpawnAsync(c, "WI-50090");
            await SetPhaseDirectlyAsync(f, "WI-50090", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50090/spawn/decompose", new
            {
                workstreams = new[] { new { title = "u" }, new { title = "v" } },
            }, JsonOpts);

            var resp = await c.GetAsync("/api/stories/WI-50090/spawn");
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("Story card", doc.RootElement.GetProperty("spawn").GetProperty("storyTitle").GetString());
            Assert.Equal("decompose", doc.RootElement.GetProperty("spawn").GetProperty("phase").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("workstreams").GetArrayLength());
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.10 — Artifact check-in rule
    // -----------------------------------------------------------------------

    /// <summary>
    /// 28.10 — Plan artifact gate: phase advance to decompose requires the
    /// merged plan PR. (The "files don't exist on main yet" assertion in the
    /// Gherkin maps to "PR not merged → gate trips at the API".)
    /// </summary>
    [Fact]
    public async Task ArtifactGate_Decompose_Blocked_Until_Plan_Pr_Merged()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50100", "art-gate");
            await SpawnAsync(c, "WI-50100");
            // Plan branch exists, PR is open but not merged → gate trips.
            await SetPlanPrAsync(f, "WI-50100", "pr-plan-100", "review");
            var resp = await c.PatchAsync("/api/stories/WI-50100/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.10 — Merged plan artifact unlocks downstream phase.</summary>
    [Fact]
    public async Task ArtifactGate_Merged_Plan_Unlocks_Decompose()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50101", "art-merged");
            await SpawnAsync(c, "WI-50101");
            await SetPlanPrAsync(f, "WI-50101", "pr-plan-101", "merged");
            var resp = await c.PatchAsync("/api/stories/WI-50101/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 28.11 — Dependency tracking
    // -----------------------------------------------------------------------

    /// <summary>28.11 — Workstream with no dependencies is eligible immediately.</summary>
    [Fact]
    public async Task Deps_No_Deps_Eligible()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50110", "no-deps");
            await SpawnAsync(c, "WI-50110");
            await SetPhaseDirectlyAsync(f, "WI-50110", StorySpawnPhases.Decompose);
            await c.PostAsJsonAsync("/api/stories/WI-50110/spawn/decompose", new
            {
                workstreams = new[] { new { title = "logging", deps = new string[0] } },
            }, JsonOpts);
            var resp = await c.GetAsync("/api/stories/WI-50110/spawn");
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var w = doc.RootElement.GetProperty("workstreams").EnumerateArray().Single();
            Assert.Equal("planning", w.GetProperty("status").GetString());
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.11 — Diamond dependency (A→B, A→C, B→D, C→D) is wired correctly.</summary>
    [Fact]
    public async Task Deps_Diamond_Recorded_Correctly()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50111", "diamond");
            await SpawnAsync(c, "WI-50111");
            await SetPhaseDirectlyAsync(f, "WI-50111", StorySpawnPhases.Decompose);

            var body = new
            {
                workstreams = new[]
                {
                    new { title = "A", deps = new string[0] },
                    new { title = "B", deps = new[] { "A" } },
                    new { title = "C", deps = new[] { "A" } },
                    new { title = "D", deps = new[] { "B", "C" } },
                },
            };
            var resp = await c.PostAsJsonAsync("/api/stories/WI-50111/spawn/decompose", body, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode);

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var deps = await db.WorkstreamDeps
                .Where(d => d.WorkstreamId.StartsWith("ws-wi-50111-"))
                .ToListAsync();
            // 4 edges total.
            Assert.Equal(4, deps.Count);
            // D depends on B and C.
            Assert.Equal(2, deps.Count(d => d.WorkstreamId.EndsWith("-d")));
        }
        finally { f.Dispose(); }
    }

    /// <summary>28.11 — Deep chain (A → B → C → D) is wired sequentially.</summary>
    [Fact]
    public async Task Deps_Deep_Chain_Recorded()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50112", "chain");
            await SpawnAsync(c, "WI-50112");
            await SetPhaseDirectlyAsync(f, "WI-50112", StorySpawnPhases.Decompose);

            var body = new
            {
                workstreams = new[]
                {
                    new { title = "A", deps = new string[0] },
                    new { title = "B", deps = new[] { "A" } },
                    new { title = "C", deps = new[] { "B" } },
                    new { title = "D", deps = new[] { "C" } },
                },
            };
            var resp = await c.PostAsJsonAsync("/api/stories/WI-50112/spawn/decompose", body, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode);

            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var deps = await db.WorkstreamDeps
                .Where(d => d.WorkstreamId.StartsWith("ws-wi-50112-"))
                .ToListAsync();
            Assert.Equal(3, deps.Count);
            Assert.Single(deps.Where(d => d.WorkstreamId.EndsWith("-b") && d.DependsOnId.EndsWith("-a")));
            Assert.Single(deps.Where(d => d.WorkstreamId.EndsWith("-c") && d.DependsOnId.EndsWith("-b")));
            Assert.Single(deps.Where(d => d.WorkstreamId.EndsWith("-d") && d.DependsOnId.EndsWith("-c")));
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // Cross-cutting
    // -----------------------------------------------------------------------

    /// <summary>cross-cutting — phase rollback is rejected.</summary>
    [Fact]
    public async Task CrossCutting_Phase_Rollback_Rejected()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50200", "rollback");
            await SpawnAsync(c, "WI-50200");
            await SetPhaseDirectlyAsync(f, "WI-50200", StorySpawnPhases.Review);
            var resp = await c.PatchAsync("/api/stories/WI-50200/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("Phase rollback is not supported", await resp.Content.ReadAsStringAsync());
        }
        finally { f.Dispose(); }
    }

    /// <summary>cross-cutting — only one spawn per story at a time.</summary>
    [Fact]
    public async Task CrossCutting_Only_One_Spawn_Per_Story()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50201", "single");
            await SpawnAsync(c, "WI-50201");
            await SetPhaseDirectlyAsync(f, "WI-50201", StorySpawnPhases.Implement);

            var resp = await SpawnAsync(c, "WI-50201");
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        }
        finally { f.Dispose(); }
    }

    /// <summary>cross-cutting — story spawn survives DbContext recreation (restart-equivalent).</summary>
    [Fact]
    public async Task CrossCutting_Spawn_Survives_DbContext_Recreation()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50202", "survive");
            await SpawnAsync(c, "WI-50202");
            await SetPhaseDirectlyAsync(f, "WI-50202", StorySpawnPhases.Implement);

            // Simulate a "restart" by resolving a fresh scope and reloading.
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var s = await db.StorySpawns.AsNoTracking().FirstAsync(x => x.StoryId == "WI-50202");
            Assert.Equal("implement", s.Phase);
        }
        finally { f.Dispose(); }
    }

    /// <summary>cross-cutting — full happy path: spawn → plan → decompose → implement → review → qa → done.</summary>
    [Fact]
    public async Task CrossCutting_Full_Happy_Path()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50203", "happy");
            await SpawnAsync(c, "WI-50203");
            await SetPlanPrAsync(f, "WI-50203", "pr-plan-203", "merged");

            // plan → decompose
            await c.PatchAsync("/api/stories/WI-50203/spawn/phase",
                JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));

            // submit decomp + approve
            await c.PostAsJsonAsync("/api/stories/WI-50203/spawn/decompose", new
            {
                workstreams = new[]
                {
                    new { title = "ws1", deps = new string[0] },
                    new { title = "ws2", deps = new string[0] },
                },
            }, JsonOpts);
            await c.PostAsync("/api/stories/WI-50203/spawn/decompose/approve", null);

            // decompose → implement
            await c.PatchAsync("/api/stories/WI-50203/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

            // both workstreams reach review
            await SetWorkstreamStatusAsync(f, "ws-wi-50203-ws1", "review");
            await SetWorkstreamStatusAsync(f, "ws-wi-50203-ws2", "review");

            // implement → review
            var r1 = await c.PatchAsync("/api/stories/WI-50203/spawn/phase",
                JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

            // both workstreams merge
            await SetWorkstreamStatusAsync(f, "ws-wi-50203-ws1", "merging");
            await SetWorkstreamStatusAsync(f, "ws-wi-50203-ws2", "merging");

            // review → qa
            var r2 = await c.PatchAsync("/api/stories/WI-50203/spawn/phase",
                JsonContent.Create(new { targetPhase = "qa" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

            // qa → done
            var r3 = await c.PatchAsync("/api/stories/WI-50203/spawn/phase",
                JsonContent.Create(new { targetPhase = "done" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.OK, r3.StatusCode);

            var spawn = await GetSpawnAsync(f, "WI-50203");
            Assert.Equal("done", spawn!.Phase);
        }
        finally { f.Dispose(); }
    }

    /// <summary>cross-cutting — sequential single-step phase advance only.</summary>
    [Fact]
    public async Task CrossCutting_Phase_Advance_Must_Be_Sequential()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50204", "skip");
            await SpawnAsync(c, "WI-50204");
            // Try to skip from plan to implement.
            var resp = await c.PatchAsync("/api/stories/WI-50204/spawn/phase",
                JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("Phase advance must be sequential", await resp.Content.ReadAsStringAsync());
        }
        finally { f.Dispose(); }
    }

    /// <summary>cross-cutting — unknown phase string returns 400.</summary>
    [Fact]
    public async Task CrossCutting_Unknown_Phase_Returns_400()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-50205", "unknown");
            await SpawnAsync(c, "WI-50205");
            var resp = await c.PatchAsync("/api/stories/WI-50205/spawn/phase",
                JsonContent.Create(new { targetPhase = "wat" }, options: JsonOpts));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { f.Dispose(); }
    }

    /// <summary>cross-cutting — writes require X-Forge-User header.</summary>
    [Fact]
    public async Task CrossCutting_Writes_Require_Auth_Header()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            using var anon = f.CreateClient();
            var resp = await anon.PostAsJsonAsync("/api/stories/WI-X/spawn", new { });
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Regression: posting two review comments on the same PR via the
    /// provider-backed <see cref="IStoryProviderOps"/> must not violate the
    /// <c>UNIQUE (PrId, ProviderCommentId)</c> index. Previously
    /// <see cref="ProviderStoryProviderOps.PostPrCommentAsync"/> set
    /// <c>ProviderCommentId = string.Empty</c> for every row, so the second
    /// comment on a PR halted the orchestrator with SQLite Error 19. Now
    /// each row gets a unique <c>pending:{guid}</c> placeholder.
    /// </summary>
    [Fact]
    public async Task ProviderStoryProviderOps_Post_Two_Comments_On_Same_Pr_Persists_Both()
    {
        using var factory = new ForgeWebApplicationFactory();
        // Seed a PR row that will own the comments. Provider is left null so
        // PostPrCommentAsync's "best-effort upstream" branch short-circuits
        // and the test only exercises the local-row insert path.
        const string prId = "PR-comment-dup-test";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = "review",
                Title = "test pr for comment dedup",
                Branch = "ws/comment-dup",
                Base = "main",
                Workstream = "ws-comment-dup",
                Provider = null,
            });
            await db.SaveChangesAsync();
        }

        var ops = new ProviderStoryProviderOps(
            factory.Services,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderStoryProviderOps>());

        var first = await ops.PostPrCommentAsync(prId, "src/foo.cs", 12, "consider null check", "rev-1");
        var second = await ops.PostPrCommentAsync(prId, "src/foo.cs", 13, "rename for clarity", "rev-1");

        Assert.NotEqual(0, first);
        Assert.NotEqual(0, second);
        Assert.NotEqual(first, second);

        using var verify = factory.Services.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var rows = await db2.PrComments.AsNoTracking()
            .Where(c => c.PrId == prId)
            .OrderBy(c => c.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.StartsWith("pending:", r.ProviderCommentId));
        Assert.NotEqual(rows[0].ProviderCommentId, rows[1].ProviderCommentId);
    }
}
