// -----------------------------------------------------------------------
// <copyright file="Phase53PlanDocSequentialTests.cs" company="Microsoft Corp.">
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
/// forge_implementation.md Phase 53 — sequential plan-doc tasks. Each
/// of the four plan docs (architecture → tech-spec → implementation-plan
/// → e2e-scenarios) gets its own branch + worktree + PR; doc N+1's
/// gen↔eval loop only kicks off after doc N's PR is merged AND its
/// latest deliverable verdict is <c>pass</c>.
///
/// <para>Helper unit tests + endpoint contract tests covering the
/// discriminator, the canonical ordering, the restart-of-merged-doc
/// 409, and the decompose-gate "all four merged" requirement.</para>
/// </summary>
public class Phase53PlanDocSequentialTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── Helper unit tests ───────────────────────────────────────────────────

    [Fact]
    public void IsSequentialMode_legacy_spawn_returns_false()
    {
        var spawn = new StorySpawnRow { Id = "s", StoryId = "S", PlanDocPrsJson = null };
        Assert.False(PlanDocStateHelpers.IsSequentialMode(spawn));
    }

    [Fact]
    public void IsSequentialMode_new_spawn_with_empty_array_returns_true()
    {
        var spawn = new StorySpawnRow { Id = "s", StoryId = "S", PlanDocPrsJson = "[]" };
        Assert.True(PlanDocStateHelpers.IsSequentialMode(spawn));
    }

    [Fact]
    public void GetPlanDocStates_empty_json_returns_four_canonical_entries()
    {
        var spawn = new StorySpawnRow { Id = "s", StoryId = "S", PlanDocPrsJson = "[]" };
        var states = PlanDocStateHelpers.GetPlanDocStates(spawn);
        Assert.Equal(4, states.Count);
        Assert.Equal("architecture", states[0].Key);
        Assert.Equal("tech-spec", states[1].Key);
        Assert.Equal("implementation-plan", states[2].Key);
        Assert.Equal("e2e-scenarios", states[3].Key);
        Assert.All(states, e =>
        {
            Assert.Null(e.Branch);
            Assert.Null(e.PrId);
        });
    }

    [Fact]
    public void SetPlanDocStates_persists_in_canonical_order_regardless_of_input_order()
    {
        var spawn = new StorySpawnRow { Id = "s", StoryId = "S", PlanDocPrsJson = "[]" };
        var entries = new[]
        {
            new PlanDocPrEntry("e2e-scenarios", "br-e2e", "pr-e2e"),
            new PlanDocPrEntry("architecture", "br-arch", "pr-arch"),
            new PlanDocPrEntry("tech-spec", "br-tech", "pr-tech"),
        };
        PlanDocStateHelpers.SetPlanDocStates(spawn, entries);
        var read = PlanDocStateHelpers.GetPlanDocStates(spawn);
        Assert.Equal(3, read.Count(e => e.PrId != null));
        Assert.Equal("architecture", read[0].Key);
        Assert.Equal("pr-arch", read[0].PrId);
        Assert.Equal("tech-spec", read[1].Key);
        Assert.Equal("pr-tech", read[1].PrId);
        // implementation-plan not in input → present as placeholder.
        Assert.Equal("implementation-plan", read[2].Key);
        Assert.Null(read[2].PrId);
        Assert.Equal("e2e-scenarios", read[3].Key);
        Assert.Equal("pr-e2e", read[3].PrId);
    }

    [Fact]
    public void FindDocBranch_legacy_spawn_returns_PlanBranch()
    {
        var spawn = new StorySpawnRow
        {
            Id = "s", StoryId = "WI-X",
            PlanBranch = "story/WI-X/plan",
            PlanDocPrsJson = null,
        };
        Assert.Equal("story/WI-X/plan",
            PlanDocStateHelpers.FindDocBranch(spawn, "architecture"));
        Assert.Equal("story/WI-X/plan",
            PlanDocStateHelpers.FindDocBranch(spawn, "tech-spec"));
    }

    [Fact]
    public void FindDocBranch_sequential_spawn_with_no_entry_returns_canonical_per_doc_branch()
    {
        var spawn = new StorySpawnRow
        {
            Id = "s", StoryId = "WI-Y",
            PlanDocPrsJson = "[]",
        };
        Assert.Equal("story/WI-Y/plan-architecture",
            PlanDocStateHelpers.FindDocBranch(spawn, "architecture"));
        Assert.Equal("story/WI-Y/plan-e2e-scenarios",
            PlanDocStateHelpers.FindDocBranch(spawn, "e2e-scenarios"));
    }

    [Fact]
    public void FindDocBranch_sequential_spawn_with_recorded_entry_returns_persisted_branch()
    {
        var spawn = new StorySpawnRow { Id = "s", StoryId = "WI-Z", PlanDocPrsJson = "[]" };
        PlanDocStateHelpers.SetPlanDocStates(spawn, new[]
        {
            new PlanDocPrEntry("architecture", "custom/branch-name", null),
        });
        Assert.Equal("custom/branch-name",
            PlanDocStateHelpers.FindDocBranch(spawn, "architecture"));
    }

    [Fact]
    public void CanonicalDocBranch_uses_story_slash_doc_pattern()
    {
        Assert.Equal("story/WI-123/plan-architecture",
            PlanDocStateHelpers.CanonicalDocBranch("WI-123", "architecture"));
        Assert.Equal("story/some-story-id/plan-tech-spec",
            PlanDocStateHelpers.CanonicalDocBranch("some-story-id", "tech-spec"));
    }

    [Fact]
    public void CanonicalDocWorktreeSlug_returns_plan_dash_key()
    {
        Assert.Equal("plan-architecture",
            PlanDocStateHelpers.CanonicalDocWorktreeSlug("architecture"));
        Assert.Equal("plan-e2e-scenarios",
            PlanDocStateHelpers.CanonicalDocWorktreeSlug("e2e-scenarios"));
    }

    [Fact]
    public void GetEntry_unknown_key_throws()
    {
        var spawn = new StorySpawnRow { Id = "s", StoryId = "S", PlanDocPrsJson = "[]" };
        Assert.Throws<ArgumentException>(() => PlanDocStateHelpers.GetEntry(spawn, "bogus"));
    }

    [Fact]
    public async Task FindByPrIdAsync_finds_legacy_spawn_with_PlanPrId()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = "spawn-legacy",
            StoryId = "story-legacy",
            Phase = StorySpawnPhases.Plan,
            PlanBranch = "story/story-legacy/plan",
            PlanPrId = "pr-legacy-42",
            PlanDocPrsJson = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await PlanDocStateHelpers.FindByPrIdAsync(db, "pr-legacy-42", default);
        Assert.NotNull(result);
        Assert.Equal("spawn-legacy", result!.Value.Spawn.Id);
        Assert.Null(result.Value.DocKey);
    }

    [Fact]
    public async Task FindByPrIdAsync_finds_sequential_spawn_with_doc_pr()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var spawn = new StorySpawnRow
        {
            Id = "spawn-seq",
            StoryId = "story-seq",
            Phase = StorySpawnPhases.Plan,
            PlanDocPrsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        PlanDocStateHelpers.SetPlanDocStates(spawn, new[]
        {
            new PlanDocPrEntry("architecture", "story/story-seq/plan-architecture", "pr-arch-7"),
            new PlanDocPrEntry("tech-spec", "story/story-seq/plan-tech-spec", "pr-tech-8"),
        });
        db.StorySpawns.Add(spawn);
        await db.SaveChangesAsync();

        var result = await PlanDocStateHelpers.FindByPrIdAsync(db, "pr-tech-8", default);
        Assert.NotNull(result);
        Assert.Equal("spawn-seq", result!.Value.Spawn.Id);
        Assert.Equal("tech-spec", result.Value.DocKey);
    }

    [Fact]
    public async Task FindByPrIdAsync_unknown_pr_returns_null()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.Null(await PlanDocStateHelpers.FindByPrIdAsync(db, "pr-nope", default));
    }

    // ── Endpoint contract tests ─────────────────────────────────────────────

    [Fact]
    public async Task POST_spawn_initialises_PlanDocPrsJson_to_empty_array_and_leaves_PlanBranch_null()
    {
        var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Seed story.
        using (var seedScope = f.Services.CreateScope())
        {
            var sdb = seedScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            sdb.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-P53-A", Provider = "ado", Track = "dev", Type = "feature",
                State = "active", Title = "Phase 53 spawn-create test", Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await sdb.SaveChangesAsync();
        }

        var resp = await c.PostAsJsonAsync("/api/stories/WI-P53-A/spawn", new { }, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == "WI-P53-A");
        Assert.True(PlanDocStateHelpers.IsSequentialMode(spawn),
            "New spawn must be in sequential mode (PlanDocPrsJson initialised).");
        Assert.True(string.IsNullOrEmpty(spawn.PlanBranch),
            "New spawn must NOT set PlanBranch (per-doc branches live in PlanDocPrsJson).");
    }

    [Fact]
    public async Task Restart_endpoint_rejects_409_when_doc_PR_is_already_merged()
    {
        var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var sdb = seed.ServiceProvider.GetRequiredService<ForgeDbContext>();
            sdb.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-P53-B", Provider = "ado", Track = "dev", Type = "feature",
                State = "active", Title = "Phase 53 restart-rejection test", Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            var spawn = new StorySpawnRow
            {
                Id = "spawn-WI-P53-B",
                StoryId = "WI-P53-B",
                Phase = StorySpawnPhases.Plan,
                BaseBranch = "main",
                PlanDocPrsJson = "[]",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            PlanDocStateHelpers.SetPlanDocStates(spawn, new[]
            {
                new PlanDocPrEntry("architecture", "story/WI-P53-B/plan-architecture", "pr-arch-merged"),
            });
            sdb.StorySpawns.Add(spawn);
            sdb.Prs.Add(new PrRow
            {
                Id = "pr-arch-merged",
                Branch = "story/WI-P53-B/plan-architecture",
                Base = "main",
                Col = "merged",
                Title = "[plan-architecture] test",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                IsActive = false,
            });
            await sdb.SaveChangesAsync();
        }

        var resp = await c.PostAsync(
            "/api/stories/WI-P53-B/spawn/artifacts/architecture/restart", content: null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Decompose_gate_requires_all_four_doc_PRs_merged()
    {
        var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var sdb = seed.ServiceProvider.GetRequiredService<ForgeDbContext>();
            sdb.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-P53-C", Provider = "ado", Track = "dev", Type = "feature",
                State = "active", Title = "Phase 53 decompose-gate", Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            var spawn = new StorySpawnRow
            {
                Id = "spawn-WI-P53-C",
                StoryId = "WI-P53-C",
                Phase = StorySpawnPhases.Plan,
                BaseBranch = "main",
                PlanDocPrsJson = "[]",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            // Only two of the four PRs are tracked, only one is merged.
            PlanDocStateHelpers.SetPlanDocStates(spawn, new[]
            {
                new PlanDocPrEntry("architecture", "story/WI-P53-C/plan-architecture", "pr-arch-c"),
                new PlanDocPrEntry("tech-spec", "story/WI-P53-C/plan-tech-spec", "pr-tech-c"),
            });
            sdb.StorySpawns.Add(spawn);
            sdb.Prs.AddRange(new[]
            {
                new PrRow
                {
                    Id = "pr-arch-c",
                    Branch = "story/WI-P53-C/plan-architecture",
                    Base = "main", Col = "merged", Title = "arch",
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    IsActive = false,
                },
                new PrRow
                {
                    Id = "pr-tech-c",
                    Branch = "story/WI-P53-C/plan-tech-spec",
                    Base = "main", Col = "open", Title = "tech",
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    IsActive = true,
                },
            });
            await sdb.SaveChangesAsync();
        }

        var advance = await c.PatchAsync(
            "/api/stories/WI-P53-C/spawn/phase",
            JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, advance.StatusCode);
    }

    [Fact]
    public async Task Decompose_gate_passes_when_all_four_PRs_merged()
    {
        var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        using (var seed = f.Services.CreateScope())
        {
            var sdb = seed.ServiceProvider.GetRequiredService<ForgeDbContext>();
            sdb.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-P53-D", Provider = "ado", Track = "dev", Type = "feature",
                State = "active", Title = "Phase 53 decompose-pass", Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            var spawn = new StorySpawnRow
            {
                Id = "spawn-WI-P53-D",
                StoryId = "WI-P53-D",
                Phase = StorySpawnPhases.Plan,
                BaseBranch = "main",
                PlanDocPrsJson = "[]",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var entries = new[]
            {
                new PlanDocPrEntry("architecture", "story/WI-P53-D/plan-architecture", "pr-arch-d"),
                new PlanDocPrEntry("tech-spec", "story/WI-P53-D/plan-tech-spec", "pr-tech-d"),
                new PlanDocPrEntry("implementation-plan", "story/WI-P53-D/plan-implementation-plan", "pr-impl-d"),
                new PlanDocPrEntry("e2e-scenarios", "story/WI-P53-D/plan-e2e-scenarios", "pr-e2e-d"),
            };
            PlanDocStateHelpers.SetPlanDocStates(spawn, entries);
            sdb.StorySpawns.Add(spawn);
            foreach (var e in entries)
            {
                sdb.Prs.Add(new PrRow
                {
                    Id = e.PrId!,
                    Branch = e.Branch!,
                    Base = "main", Col = "merged", Title = e.Key,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    IsActive = false,
                });
            }
            await sdb.SaveChangesAsync();
        }

        var advance = await c.PatchAsync(
            "/api/stories/WI-P53-D/spawn/phase",
            JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));

        Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
    }
}
