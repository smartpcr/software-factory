// -----------------------------------------------------------------------
// <copyright file="Stage42DeterministicQaE2eTests.cs" company="Microsoft Corp.">
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
/// docs/forge/e2e.md Stage 4.2 — the deterministic
/// <see cref="DeterministicStoryArtifactStrategy.ProduceStageE2eAsync"/>
/// implementation. The strategy lives in the test assembly and is the
/// reference shape the LLM-driven Stage 4.3 producer will emit at
/// runtime. Pinning its output here also pins the contract Stage 5.1
/// (orchestrator routing) and Stage 9.x (integration suites) build
/// against.
///
/// <para>The first three Facts are pure-helper (no boot, no DB): they
/// hand-construct the strategy and feed a synthetic e2e-scenarios.md
/// to <see cref="E2eScenariosSetupReader"/> indirectly via an
/// <see cref="StubGitOps"/> that returns canned markdown. The fourth
/// Fact boots <see cref="ForgeWebApplicationFactory"/> and seeds the
/// e2e-scenarios.md on the real <c>InMemoryStoryGitOps</c> branch so
/// the DI-resolved strategy reads it through the production code path
/// (proves the boot factory wires git + scope-factory through to the
/// deterministic strategy ctor).</para>
/// </summary>
[Collection("Phase28")]
public class Stage42DeterministicQaE2eTests
{
    private const string StoryId = "WI-stage42-deterministic";
    private const string SpawnId = "spawn-" + StoryId;
    private const string RepoId = "repo-stage42";
    private const string ProductId = "prod-stage42";
    private const string PhaseName = "Foundation";
    private const string StageName = "Setup";

    // Anchor for an impl stage in `phase-foundation/stage-setup`; the
    // qa-e2e companion appends `/e2e`.
    private const string ImplAnchor = "phase-foundation/stage-setup";

    private static WorkstreamRow QaE2eRow() => new()
    {
        Id = $"ws-stage42-{ImplAnchor.Replace('/', '-')}-e2e",
        Name = $"{StageName} — E2E",
        PlanAnchor = ImplAnchor + CrewStoryArtifactStrategy.QaE2eAnchorSuffix,
        PhaseAnchor = "phase-foundation",
        Category = WorkstreamCategories.QaE2e,
        StorySpawnId = SpawnId,
        Branch = $"ws/{StoryId}/{ImplAnchor.Replace('/', '-')}-e2e",
        Status = "planning",
    };

    private static StorySpawnRow Spawn() => new()
    {
        Id = SpawnId,
        StoryId = StoryId,
        StoryTitle = "Stage 4.2 deterministic qa-e2e",
        Phase = StorySpawnPhases.Implement,
        RepoId = RepoId,
        BaseBranch = "main",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ── 1. [unit] `inline` setup → 4 entries (feature + steps + appsettings + E2eBase) ──
    //
    // Stage 7.3 added per-phase appsettings.e2e.json + per-spawn
    // E2eBase.cs emission; the original Stage 4.2 spec asserted 2
    // entries. Count bumped to 4 (first qa-e2e per spawn always
    // carries E2eBase).

    [Fact]
    public async Task ProduceStageE2eAsync_Inline_Setup_Returns_Four_Entries()
    {
        var strategy = new DeterministicStoryArtifactStrategy(); // no git → fallback to inline

        var files = await strategy.ProduceStageE2eAsync(Spawn(), QaE2eRow(), default);

        Assert.Equal(4, files.Count);

        var featurePath = $"tests/Features/{StoryId}/foundation/setup.feature";
        var stepsPath = $"tests/Steps/{StoryId}/foundation/SetupSteps.cs";
        var appsPath = $"tests/e2e/foundation/appsettings.e2e.json";
        var basePath = $"tests/Steps/{StoryId}/E2eBase.cs";
        Assert.True(files.ContainsKey(featurePath),
            $"Inline setup must emit {featurePath}; got [{string.Join(", ", files.Keys)}]");
        Assert.True(files.ContainsKey(stepsPath),
            $"Inline setup must emit {stepsPath}; got [{string.Join(", ", files.Keys)}]");
        Assert.True(files.ContainsKey(appsPath),
            $"Stage 7.3 must emit {appsPath}; got [{string.Join(", ", files.Keys)}]");
        Assert.True(files.ContainsKey(basePath),
            $"Stage 7.3 must emit {basePath} on the first qa-e2e per spawn; got [{string.Join(", ", files.Keys)}]");

        // Feature carries the @setup-inline tag so the test framework can
        // filter by setup type. Steps file is compilable C# with the
        // expected Reqnroll bindings.
        Assert.Contains("@setup-inline", files[featurePath]);
        Assert.Contains("Feature: Setup — E2E", files[featurePath]);
        Assert.Contains("[Binding]", files[stepsPath]);
        Assert.Contains("public class SetupSteps", files[stepsPath]);

        // No pipeline YAML for inline setup.
        Assert.DoesNotContain(files.Keys, k => k.Contains("azure-pipeline.yml", StringComparison.Ordinal));
        Assert.DoesNotContain(files.Keys, k => k.Contains(".github/workflows/", StringComparison.Ordinal));

        // Inline appsettings carries an empty E2e section (no env vars).
        Assert.Contains("\"E2e\":", files[appsPath]);
        Assert.DoesNotContain("LAB_CLUSTER_FQDN", files[appsPath]);
        Assert.DoesNotContain("COMPOSE_REDIS_HOST", files[appsPath]);
    }

    // ── 2. [unit] `compose` setup → 7 entries (above + docker-compose + ADO + GH + Stage 7.3 pair) ──
    //
    // Stage 7.1 added docker-compose.yml emission via E2eComposeRenderer
    // (was 4 → 5); Stage 7.3 added appsettings.e2e.json + E2eBase.cs
    // (5 → 7). Test renamed + count bumped each time.

    [Fact]
    public async Task ProduceStageE2eAsync_Compose_Setup_Returns_Seven_Entries()
    {
        var setupMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: compose
            - **Local**: docker compose up -d
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** Foundation works
            """;
        var strategy = new DeterministicStoryArtifactStrategy(
            git: GitOpsWithE2eScenarios(setupMd));

        var files = await strategy.ProduceStageE2eAsync(Spawn(), QaE2eRow(), default);

        // docs/forge/e2e.md Stage 7.3 — compose now emits the 5 Stage 7.1
        // files + appsettings.e2e.json + E2eBase.cs (7 total).
        Assert.Equal(7, files.Count);

        var featurePath = $"tests/Features/{StoryId}/foundation/setup.feature";
        var stepsPath = $"tests/Steps/{StoryId}/foundation/SetupSteps.cs";
        var composePath = "tests/e2e/foundation/docker-compose.yml";
        var adoPath = "tests/e2e/foundation/azure-pipeline.yml";
        var ghPath = ".github/workflows/e2e-foundation.yml";
        var appsPath = "tests/e2e/foundation/appsettings.e2e.json";
        var basePath = $"tests/Steps/{StoryId}/E2eBase.cs";

        Assert.True(files.ContainsKey(featurePath));
        Assert.True(files.ContainsKey(stepsPath));
        Assert.True(files.ContainsKey(composePath),
            $"Compose setup must emit {composePath} (Stage 7.1); got [{string.Join(", ", files.Keys)}]");
        Assert.True(files.ContainsKey(adoPath),
            $"Compose setup must emit {adoPath}; got [{string.Join(", ", files.Keys)}]");
        Assert.True(files.ContainsKey(ghPath),
            $"Compose setup must emit {ghPath}; got [{string.Join(", ", files.Keys)}]");
        Assert.True(files.ContainsKey(appsPath),
            $"Stage 7.3 must emit {appsPath}; got [{string.Join(", ", files.Keys)}]");
        Assert.True(files.ContainsKey(basePath),
            $"Stage 7.3 must emit {basePath} on the first qa-e2e per spawn; got [{string.Join(", ", files.Keys)}]");

        // Compose appsettings declares the two compose-scaffold env vars
        // (sourced from default app + redis services); confirms Stage
        // 7.3's per-setup-type EnvVarNames routing.
        Assert.Contains("COMPOSE_REDIS_HOST", files[appsPath]);
        Assert.Contains("COMPOSE_REDIS_PORT", files[appsPath]);
        Assert.DoesNotContain("LAB_CLUSTER_FQDN", files[appsPath]);

        // Feature + steps tagged `@setup-compose`.
        Assert.Contains("@setup-compose", files[featurePath]);
        Assert.Contains("Setup type: compose", files[stepsPath]);

        // ADO YAML uses hosted runner; GH workflow uses ubuntu-latest.
        Assert.Contains("vmImage: ubuntu-latest", files[adoPath]);
        Assert.Contains("runs-on: ubuntu-latest", files[ghPath]);

        // Compose setup must NOT carry lab secrets refs.
        Assert.DoesNotContain("LAB_CLUSTER_FQDN", files[adoPath]);
        Assert.DoesNotContain("LAB_CLUSTER_FQDN", files[ghPath]);
        Assert.DoesNotContain("forge-lab-kv", files[adoPath]);
    }

    // ── 3. [unit] `lab-wsfc` setup → 6 entries with lab env-vars + Stage 7.3 pair ──

    [Fact]
    public async Task ProduceStageE2eAsync_LabWsfc_Setup_Returns_Six_Entries_With_LabSecrets()
    {
        var setupMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: lab-wsfc
            - **Local**: requires WSFC cluster
            - **CI runner**: self-hosted forge-lab-wsfc
            - **Secrets**: Azure Key Vault `forge-lab-kv` + GH env `lab-wsfc`

            ### Scenarios
            - **Scenario:** CAU cycles correctly
            """;
        var strategy = new DeterministicStoryArtifactStrategy(
            git: GitOpsWithE2eScenarios(setupMd));

        var files = await strategy.ProduceStageE2eAsync(Spawn(), QaE2eRow(), default);

        // Stage 7.3 bumps lab-wsfc total to 6: feature + steps + ADO +
        // GH workflow + appsettings.e2e.json + E2eBase.cs.
        Assert.Equal(6, files.Count);

        var adoPath = "tests/e2e/foundation/azure-pipeline.yml";
        var ghPath = ".github/workflows/e2e-foundation.yml";
        var appsPath = "tests/e2e/foundation/appsettings.e2e.json";
        var basePath = $"tests/Steps/{StoryId}/E2eBase.cs";

        // ADO YAML: self-hosted lab pool + lab variable group + KeyVault task.
        var ado = files[adoPath];
        Assert.Contains("name: forge-lab-wsfc", ado);
        Assert.Contains("- group: forge-lab-vg", ado);
        Assert.Contains("KeyVaultName: forge-lab-kv", ado);
        Assert.Contains("LAB_CLUSTER_FQDN", ado);
        Assert.Contains("LAB_ADMIN_USER", ado);
        Assert.Contains("LAB_ADMIN_PASSWORD", ado);
        Assert.Contains("$(LAB_CLUSTER_FQDN)", ado);

        // GH workflow: self-hosted lab runner labels + environment.
        var gh = files[ghPath];
        Assert.Contains("runs-on: [self-hosted, wsfc-lab]", gh);
        Assert.Contains("environment: lab-wsfc", gh);
        Assert.Contains("${{ secrets.LAB_CLUSTER_FQDN }}", gh);
        Assert.Contains("${{ secrets.LAB_ADMIN_USER }}", gh);
        Assert.Contains("${{ secrets.LAB_ADMIN_PASSWORD }}", gh);

        // Feature carries the @setup-lab-wsfc tag.
        Assert.Contains("@setup-lab-wsfc",
            files[$"tests/Features/{StoryId}/foundation/setup.feature"]);

        // Stage 7.3 — lab appsettings declares the 3 lab env-vars;
        // E2eBase.cs ships the SetupSkippedException + RequireEnv helper.
        Assert.True(files.ContainsKey(appsPath));
        Assert.True(files.ContainsKey(basePath));
        Assert.Contains("LAB_CLUSTER_FQDN", files[appsPath]);
        Assert.Contains("LAB_ADMIN_USER", files[appsPath]);
        Assert.Contains("LAB_ADMIN_PASSWORD", files[appsPath]);
        Assert.Contains("class SetupSkippedException", files[basePath]);
        Assert.Contains("RequireEnv", files[basePath]);
    }

    // ── 4. [integration] Boot factory + DI-resolved strategy + real git ──

    [Fact]
    public async Task ProduceStageE2eAsync_End_To_End_Via_Boot_Factory_Reads_E2eScenarios_From_Git()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + apply migrations */ }

        // Seed product + repo + story + spawn + qa-e2e workstream.
        const string baseBranch = "main";
        const string e2ePath = "docs/stories/" + StoryId + "/e2e-scenarios.md";
        var setupMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: lab-bare-metal
            - **Local**: requires NIC-X hardware in lab rack 7
            - **CI runner**: self-hosted forge-lab-hw
            - **Secrets**: Key Vault `forge-lab-kv` + GH env `lab-hw`

            ### Scenarios
            - **Scenario:** NIC-X probes cleanly
            """;

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = ProductId,
                Name = "Stage 4.2",
                Slug = "stage42",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Repos.Add(new RepoRow
            {
                Id = RepoId,
                ProductId = ProductId,
                DisplayName = "Stage 4.2 Repo",
                Kind = "local",
                RemoteUrl = "file://stage42",
                CloneRoot = "/tmp/stage42",
                DefaultBranch = "main",
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = StoryId,
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "Stage 4.2",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = SpawnId,
                StoryId = StoryId,
                StoryTitle = "Stage 4.2",
                Phase = StorySpawnPhases.Implement,
                PlanBranch = $"story/{StoryId}/plan",
                RepoId = RepoId,
                BaseBranch = baseBranch,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 3,
            });
            db.Workstreams.Add(QaE2eRow());
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        // Seed e2e-scenarios.md on `main` so the deterministic strategy
        // reads the lab-bare-metal setup type via IStoryGitOps.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var repo = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == RepoId);
            await git.EnsureBranchAsync(repo, baseBranch, baseBranch, default);
            await git.CommitFilesAsync(repo, baseBranch, "stage42-seed",
                new Dictionary<string, string> { [e2ePath] = setupMd },
                "seed e2e-scenarios", default);
        }

        // Resolve the DI-registered strategy and call ProduceStageE2eAsync.
        IReadOnlyDictionary<string, string> files;
        using (var scope = f.Services.CreateScope())
        {
            var strategy = (DeterministicStoryArtifactStrategy)scope.ServiceProvider
                .GetRequiredService<IStoryArtifactStrategy>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == SpawnId);
            var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == QaE2eRow().Id);
            files = await strategy.ProduceStageE2eAsync(spawn, ws, default);
        }

        // Stage 7.3 — 6 entries: feature + steps + ADO YAML + GH
        // workflow + appsettings.e2e.json + E2eBase.cs.
        Assert.Equal(6, files.Count);
        var adoPath = "tests/e2e/foundation/azure-pipeline.yml";
        var ghPath = ".github/workflows/e2e-foundation.yml";
        var appsPath = "tests/e2e/foundation/appsettings.e2e.json";
        var basePath = $"tests/Steps/{StoryId}/E2eBase.cs";

        Assert.True(files.ContainsKey($"tests/Features/{StoryId}/foundation/setup.feature"));
        Assert.True(files.ContainsKey($"tests/Steps/{StoryId}/foundation/SetupSteps.cs"));
        Assert.True(files.ContainsKey(adoPath));
        Assert.True(files.ContainsKey(ghPath));
        Assert.True(files.ContainsKey(appsPath));
        Assert.True(files.ContainsKey(basePath));

        // The end-to-end read picked up the lab-bare-metal setup type
        // — confirms IStoryGitOps + scope-factory wiring through DI.
        Assert.Contains("name: forge-lab-hw", files[adoPath]);
        Assert.Contains("runs-on: [self-hosted, lab-hw]", files[ghPath]);
        Assert.Contains("environment: lab-hw", files[ghPath]);

        // Lab-bare-metal shares the 3 LAB_* env vars with lab-wsfc.
        Assert.Contains("LAB_CLUSTER_FQDN", files[appsPath]);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build a real <see cref="InMemoryStoryGitOps"/> with the e2e-scenarios.md
    /// content pre-seeded on the spawn's base branch + repo. Avoids
    /// re-implementing the (large) <c>IStoryGitOps</c> surface for a stub.
    /// </summary>
    private static IStoryGitOps GitOpsWithE2eScenarios(string e2eScenariosContent)
    {
        var git = new InMemoryStoryGitOps();
        var repo = new RepoRow { Id = RepoId, DefaultBranch = "main", Kind = "local" };
        git.EnsureBranchAsync(repo, "main", "main").GetAwaiter().GetResult();
        git.CommitFilesAsync(
            repo, "main", "stage42-stub",
            new Dictionary<string, string>
            {
                [$"docs/stories/{StoryId}/e2e-scenarios.md"] = e2eScenariosContent,
            },
            "seed e2e-scenarios").GetAwaiter().GetResult();
        return git;
    }

}
