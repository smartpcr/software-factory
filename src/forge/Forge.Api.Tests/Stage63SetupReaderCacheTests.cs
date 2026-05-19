// -----------------------------------------------------------------------
// <copyright file="Stage63SetupReaderCacheTests.cs" company="Microsoft Corp.">
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
/// docs/forge/e2e.md Stage 6.3 — the
/// <see cref="E2eScenariosSetupReader"/> gains two new APIs:
/// <see cref="E2eScenariosSetupReader.ReadAllPhases"/> (parse the whole
/// file once) and <see cref="E2eScenariosSetupReader.ReadForPhase"/>
/// (look up one phase, throw when absent). The
/// <see cref="CrewStoryArtifactStrategy"/> wraps the former in a
/// per-spawn cache so an N-stage story whose stages span multiple
/// phases only parses the e2e-scenarios.md once per spawn.
///
/// <para>Facts 1-3 are pure-helper (no boot, no DB). Fact 4 boots
/// <see cref="ForgeWebApplicationFactory"/> + drives two stages of a
/// 2-phase spawn through <c>ProduceStageE2eAsync</c> and asserts the
/// cache short-circuits the second call AND the per-phase setup
/// flows into the per-stage output correctly.</para>
/// </summary>
[Collection("Phase28")]
public class Stage63SetupReaderCacheTests
{
    // ── 1. [unit] ReadForPhase returns inline setup ──────────────────────

    [Fact]
    public void ReadForPhase_Returns_Inline_Setup_For_Phase_With_Inline_Type()
    {
        var md = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: inline
            - **Local**: dotnet test
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** something happens
            """;

        var found = E2eScenariosSetupReader.ReadForPhase(md, "foundation");

        Assert.Equal(E2eSetupType.Inline, found.Type);
        Assert.Equal("foundation", found.PhaseSlug);
        Assert.Equal("Foundation", found.PhaseName);
        // The verbatim block carries the operator-authored bullets.
        Assert.Contains("**Type**: inline", found.BlockMarkdown);
        Assert.Contains("**Local**: dotnet test", found.BlockMarkdown);
        Assert.Contains("**CI runner**: ubuntu-latest", found.BlockMarkdown);
        Assert.Contains("**Secrets**: none", found.BlockMarkdown);
        // The Scenarios section is NOT in the block — block stops at
        // the next H3.
        Assert.DoesNotContain("Scenario:", found.BlockMarkdown);
    }

    [Fact]
    public void ReadAllPhases_Returns_Phase_Map_Keyed_By_Slug()
    {
        var md = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: inline
            - **Local**: dotnet test
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** S1

            # Phase 2: Lab work

            ### Setup
            - **Type**: lab-wsfc
            - **Local**: requires WSFC cluster
            - **CI runner**: self-hosted forge-lab-wsfc
            - **Secrets**: Azure Key Vault `forge-lab-kv` + GitHub environment `lab-wsfc`

            ### Scenarios
            - **Scenario:** CAU cycles correctly
            """;

        var map = E2eScenariosSetupReader.ReadAllPhases(md);

        Assert.Equal(2, map.Count);
        Assert.True(map.ContainsKey("foundation"));
        Assert.True(map.ContainsKey("lab-work"));
        Assert.Equal(E2eSetupType.Inline, map["foundation"].Type);
        Assert.Equal(E2eSetupType.LabWsfc, map["lab-work"].Type);
        Assert.Contains("forge-lab-kv", map["lab-work"].BlockMarkdown);
        Assert.Contains("GitHub environment", map["lab-work"].BlockMarkdown);
        // Lab-work phase block stops before the next H1 (no scenarios from
        // its body leak in).
        Assert.DoesNotContain("Scenario:", map["lab-work"].BlockMarkdown);

        // Defensive: null + empty + whitespace input yields empty map.
        Assert.Empty(E2eScenariosSetupReader.ReadAllPhases(null));
        Assert.Empty(E2eScenariosSetupReader.ReadAllPhases(string.Empty));
        Assert.Empty(E2eScenariosSetupReader.ReadAllPhases("\n  \n  "));
    }

    // ── 2. [unit] ReadForPhase throws when phase absent ──────────────────

    [Fact]
    public void ReadForPhase_Throws_InvalidOperationException_When_Phase_Absent()
    {
        var md = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: inline
            - **Local**: dotnet test
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** something
            """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => E2eScenariosSetupReader.ReadForPhase(md, "platform"));

        Assert.Contains("platform", ex.Message);
        // The error names every phase the parser DID find so the
        // operator can spot the typo (e.g. `platfom` vs `platform`).
        Assert.Contains("foundation", ex.Message);
        Assert.Contains("Found phases", ex.Message);

        // Same shape when the markdown is empty (no phases at all).
        var emptyEx = Assert.Throws<InvalidOperationException>(
            () => E2eScenariosSetupReader.ReadForPhase(string.Empty, "anything"));
        Assert.Contains("anything", emptyEx.Message);
        Assert.Contains("Found phases: []", emptyEx.Message);
    }

    // ── 3. [unit] Cache short-circuits second call for same spawn ────────

    [Fact]
    public async Task PhaseSetupCache_HitsAfter_FirstParse_For_Same_Spawn()
    {
        // Boot factory + DI-construct CrewStoryArtifactStrategy. The
        // factory's InMemoryStoryGitOps lets us seed e2e-scenarios.md
        // on main without touching real disk. Two ProduceStageE2eAsync-
        // adjacent calls for the same spawn id should produce ONE cache
        // miss; the second call hits the cached phase map.
        const string spawnId = "spawn-stage63-cache";
        const string storyId = "WI-stage63-cache";
        const string repoId = "repo-stage63";
        const string productId = "prod-stage63";

        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + apply migrations */ }

        // Seed product + repo + spawn so the strategy's LookupRepoAsync
        // can resolve.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = productId, Name = "Stage 6.3", Slug = "stage63",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            control.Repos.Add(new RepoRow
            {
                Id = repoId, ProductId = productId,
                DisplayName = "Stage 6.3 repo", Kind = "local",
                RemoteUrl = "file://stage63", CloneRoot = "/tmp/stage63",
                DefaultBranch = "main",
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId, StoryId = storyId,
                StoryTitle = "Stage 6.3 cache",
                Phase = StorySpawnPhases.Implement,
                RepoId = repoId, BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        // Seed two phases — one inline, one compose — on main.
        var setupMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: inline
            - **Local**: dotnet test
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** S1

            # Phase 2: Platform

            ### Setup
            - **Type**: compose
            - **Local**: docker compose up
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** S2
            """;
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var repo = await control.Repos.AsNoTracking().FirstAsync(r => r.Id == repoId);
            await git.EnsureBranchAsync(repo, "main", "main", default);
            await git.CommitFilesAsync(repo, "main", "stage63-seed",
                new Dictionary<string, string>
                {
                    [$"docs/stories/{storyId}/e2e-scenarios.md"] = setupMd,
                },
                "seed e2e-scenarios", default);
        }

        // DI-construct CrewStoryArtifactStrategy + drive
        // ReadE2eScenariosSetupAsync (private) via reflection so the
        // test stays focused on the cache without standing up the
        // entire ProduceStageE2eAsync pipeline.
        using var scope2 = f.Services.CreateScope();
        var strategy = Microsoft.Extensions.DependencyInjection.ActivatorUtilities
            .CreateInstance<CrewStoryArtifactStrategy>(scope2.ServiceProvider);
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var spawn = await db2.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);

        var readMethod = typeof(CrewStoryArtifactStrategy).GetMethod(
            "ReadE2eScenariosSetupAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(readMethod);

        async Task<(E2eSetupType, string)> CallAsync(string phaseSlug)
        {
            var task = (Task)readMethod!.Invoke(strategy, new object?[] { spawn, phaseSlug, default(CancellationToken) })!;
            await task;
            var resultProperty = task.GetType().GetProperty("Result")!;
            var tuple = resultProperty.GetValue(task)!;
            var type = (E2eSetupType)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
            var block = (string)tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
            return (type, block);
        }

        // First call — cache miss; parses e2e-scenarios.md from git.
        var first = await CallAsync("foundation");
        Assert.Equal(E2eSetupType.Inline, first.Item1);
        Assert.Equal(1, strategy.PhaseSetupCacheMissesFor(spawnId));

        // Second call for a DIFFERENT phase but the SAME spawn — cache
        // hit (the whole file was parsed on the first call). Miss
        // counter unchanged.
        var second = await CallAsync("platform");
        Assert.Equal(E2eSetupType.Compose, second.Item1);
        Assert.Equal(1, strategy.PhaseSetupCacheMissesFor(spawnId));

        // Third call for the SAME phase — still a cache hit.
        var third = await CallAsync("foundation");
        Assert.Equal(E2eSetupType.Inline, third.Item1);
        Assert.Equal(1, strategy.PhaseSetupCacheMissesFor(spawnId));

        // Invalidate (test hook simulating an artifact-resume scenario)
        // → next call re-parses.
        strategy.InvalidatePhaseSetupCache(spawnId);
        var fourth = await CallAsync("foundation");
        Assert.Equal(E2eSetupType.Inline, fourth.Item1);
        Assert.Equal(1, strategy.PhaseSetupCacheMissesFor(spawnId));
        // After invalidation, the next read re-parses (miss counter
        // resumes from a fresh count since invalidate also wipes the
        // miss counter for this spawn).
    }

    // ── 4. [integration] 2-phase story → per-phase qa-e2e files differ ──

    [Fact]
    public async Task TwoPhase_Spawn_QaE2e_Produces_LabYaml_For_LabPhase_Only()
    {
        // End-to-end: boot factory + deterministic strategy + 2 qa-e2e
        // workstreams in different phases (one inline, one lab-wsfc).
        // The deterministic ProduceStageE2eAsync reads setup type via
        // the same E2eScenariosSetupReader call path the LLM strategy
        // uses. Verify the inline-phase qa-e2e returns 2 files (feature
        // + steps); the lab-phase qa-e2e returns 4 (above + ADO YAML +
        // GH workflow).
        const string storyId = "WI-stage63-twophase";
        const string spawnId = "spawn-" + storyId;
        const string repoId = "repo-stage63-twophase";
        const string productId = "prod-stage63-twophase";

        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }

        // Seed product + repo + spawn + 2 qa-e2e workstreams (one per
        // phase) + e2e-scenarios.md on main carrying both setups.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = productId, Name = "Stage 6.3 two-phase", Slug = "stage63-twophase",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            control.Repos.Add(new RepoRow
            {
                Id = repoId, ProductId = productId,
                DisplayName = "Stage 6.3 two-phase repo", Kind = "local",
                RemoteUrl = "file://stage63-twophase",
                CloneRoot = "/tmp/stage63-twophase", DefaultBranch = "main",
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId, StoryId = storyId,
                StoryTitle = "Stage 6.3 two-phase",
                Phase = StorySpawnPhases.Implement,
                RepoId = repoId, BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            WorkstreamRow QaE2e(string id, string phaseSlug, string stageSlug) => new()
            {
                Id = id, Name = $"{stageSlug} — E2E",
                Branch = $"ws/{id}", Base = "main",
                Status = "planning",
                Category = WorkstreamCategories.QaE2e,
                StorySpawnId = spawnId,
                PlanAnchor = $"phase-{phaseSlug}/stage-{stageSlug}{CrewStoryArtifactStrategy.QaE2eAnchorSuffix}",
                PhaseAnchor = $"phase-{phaseSlug}",
                WorktreeJson = "[]", AgentsJson = "[]",
                PrsJson = "[]", CardsJson = "[]", Summary = string.Empty,
            };
            db.Workstreams.Add(QaE2e("ws-stage63-twophase-foundation-setup-e2e", "foundation", "setup"));
            db.Workstreams.Add(QaE2e("ws-stage63-twophase-lab-work-cau-e2e", "lab-work", "cau"));

            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        var setupMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: inline
            - **Local**: dotnet test
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** S1

            # Phase 2: Lab work

            ### Setup
            - **Type**: lab-wsfc
            - **Local**: requires WSFC cluster
            - **CI runner**: self-hosted forge-lab-wsfc
            - **Secrets**: Azure Key Vault `forge-lab-kv` + GitHub environment `lab-wsfc`

            ### Scenarios
            - **Scenario:** CAU cycles correctly
            """;
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var repo = await control.Repos.AsNoTracking().FirstAsync(r => r.Id == repoId);
            await git.EnsureBranchAsync(repo, "main", "main", default);
            await git.CommitFilesAsync(repo, "main", "stage63-twophase-seed",
                new Dictionary<string, string>
                {
                    [$"docs/stories/{storyId}/e2e-scenarios.md"] = setupMd,
                },
                "seed", default);
        }

        // Resolve the DI-registered DeterministicStoryArtifactStrategy
        // and call ProduceStageE2eAsync once per qa-e2e workstream.
        IReadOnlyDictionary<string, string> foundationFiles;
        IReadOnlyDictionary<string, string> labFiles;
        using (var scope = f.Services.CreateScope())
        {
            var strategy = scope.ServiceProvider.GetRequiredService<IStoryArtifactStrategy>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);

            var foundationWs = await db.Workstreams.AsNoTracking()
                .FirstAsync(w => w.Id == "ws-stage63-twophase-foundation-setup-e2e");
            var labWs = await db.Workstreams.AsNoTracking()
                .FirstAsync(w => w.Id == "ws-stage63-twophase-lab-work-cau-e2e");

            foundationFiles = await strategy.ProduceStageE2eAsync(spawn, foundationWs, default);
            labFiles = await strategy.ProduceStageE2eAsync(spawn, labWs, default);
        }

        // Inline phase: 4 files — feature + steps + Stage 7.3 pair
        // (appsettings.e2e.json + E2eBase.cs). The foundation
        // workstream fires FIRST and wins the per-spawn E2eBase cache
        // TryAdd, so it carries the base class entry.
        Assert.Equal(4, foundationFiles.Count);
        Assert.Contains(foundationFiles.Keys, k => k.EndsWith(".feature", StringComparison.Ordinal));
        Assert.Contains(foundationFiles.Keys, k => k.EndsWith("Steps.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(foundationFiles.Keys,
            k => k.Contains("azure-pipeline.yml", StringComparison.Ordinal));
        Assert.DoesNotContain(foundationFiles.Keys,
            k => k.Contains(".github/workflows/", StringComparison.Ordinal));
        // Stage 7.3 — first qa-e2e per spawn includes E2eBase + per-phase appsettings.
        Assert.Contains("tests/e2e/foundation/appsettings.e2e.json", foundationFiles.Keys);
        Assert.Contains($"tests/Steps/{storyId}/E2eBase.cs", foundationFiles.Keys);

        // Lab phase: 5 files — feature + steps + ADO YAML + GH
        // workflow + per-phase appsettings.e2e.json. E2eBase.cs is
        // NOT re-emitted because the cache marker is already set by
        // the foundation call above (Stage 7.3 per-spawn
        // idempotency).
        Assert.Equal(5, labFiles.Count);
        var adoPath = "tests/e2e/lab-work/azure-pipeline.yml";
        var ghPath = ".github/workflows/e2e-lab-work.yml";
        Assert.True(labFiles.ContainsKey(adoPath),
            $"Lab-phase qa-e2e must emit {adoPath}; got [{string.Join(", ", labFiles.Keys)}]");
        Assert.True(labFiles.ContainsKey(ghPath));
        Assert.Contains("name: forge-lab-wsfc", labFiles[adoPath]);
        Assert.Contains("LAB_CLUSTER_FQDN", labFiles[adoPath]);
        Assert.Contains("runs-on: [self-hosted, wsfc-lab]", labFiles[ghPath]);

        // Stage 7.3 — lab appsettings present, but E2eBase.cs is NOT
        // re-emitted (per-spawn idempotency).
        Assert.Contains("tests/e2e/lab-work/appsettings.e2e.json", labFiles.Keys);
        Assert.DoesNotContain($"tests/Steps/{storyId}/E2eBase.cs", labFiles.Keys);
    }
}
