// -----------------------------------------------------------------------
// <copyright file="Stage71ComposeRendererTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Services.Stories.Templates;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 7.1 — pin the
/// <see cref="E2eComposeRenderer"/> contract: hosted-runner shape
/// (ubuntu-latest), compose stack lifecycle (`up -d` before tests,
/// `down` always after), three files in <see cref="E2eComposeRenderer.RenderAll"/>.
///
/// <para>The first three Facts are pure-helper (no boot, no DB). The
/// fourth Fact boots <see cref="ForgeWebApplicationFactory"/> and
/// drives a compose-phase qa-e2e workstream through the deterministic
/// strategy's <c>ProduceStageE2eAsync</c> to verify the renderer's
/// output flows end-to-end into the qa-e2e file dict (5 entries: the
/// three pipeline files + feature + step-defs).</para>
/// </summary>
[Collection("Phase28")]
public class Stage71ComposeRendererTests
{
    // ── 1. [unit] Renderer produces non-empty YAML for 2-service stack ──

    [Fact]
    public void RenderDockerCompose_TwoService_Stack_Is_NonEmpty_And_NamesBoth()
    {
        // Default 2-service stack (app + redis) when caller doesn't
        // pass services. Verify the body actually mentions both.
        var defaultBody = E2eComposeRenderer.RenderDockerCompose("foundation");

        Assert.False(string.IsNullOrWhiteSpace(defaultBody));
        Assert.Contains("services:", defaultBody);
        Assert.Contains("app:", defaultBody);
        Assert.Contains("redis:", defaultBody);
        Assert.Contains("image: redis:7-alpine", defaultBody);
        Assert.Contains("healthcheck:", defaultBody);

        // Operator-declared services flow through verbatim (after slug
        // sanitisation). Postgres has its own image branch with the
        // canonical pg_isready healthcheck.
        var customBody = E2eComposeRenderer.RenderDockerCompose(
            "platform",
            services: new[] { "postgres", "myService" });

        Assert.False(string.IsNullOrWhiteSpace(customBody));
        Assert.Contains("postgres:", customBody);
        Assert.Contains("image: postgres:16-alpine", customBody);
        Assert.Contains("pg_isready", customBody);
        Assert.Contains("myservice:", customBody); // sanitised lower-kebab

        // Header comment names the phase so the operator can spot which
        // file goes where on disk.
        Assert.Contains("phase `platform`", customBody);

        // Defensive: null / empty services list falls back to the
        // 2-service default.
        var emptyBody = E2eComposeRenderer.RenderDockerCompose("foundation", Array.Empty<string>());
        Assert.Contains("app:", emptyBody);
        Assert.Contains("redis:", emptyBody);
    }

    // ── 2. [unit] ADO YAML uses ubuntu-latest hosted runner (no self-hosted) ──

    [Fact]
    public void RenderAzurePipeline_Uses_Ubuntu_Latest_Hosted_Runner_No_LabPool()
    {
        var body = E2eComposeRenderer.RenderAzurePipeline("foundation", "setup");

        Assert.False(string.IsNullOrWhiteSpace(body));

        // The `pool:` block must use `vmImage: ubuntu-latest` (hosted)
        // — compose stacks always run on hosted agents.
        Assert.Contains("pool:", body);
        Assert.Contains("vmImage: ubuntu-latest", body);

        // MUST NOT reference any lab-specific pool / variable group /
        // Key Vault — those belong to Stage 7.2's lab renderer.
        Assert.DoesNotContain("forge-lab-wsfc", body);
        Assert.DoesNotContain("forge-lab-hw", body);
        Assert.DoesNotContain("forge-lab-vg", body);
        Assert.DoesNotContain("forge-lab-kv", body);
        Assert.DoesNotContain("AzureKeyVault", body);
        Assert.DoesNotContain("LAB_CLUSTER_FQDN", body);
        // No `pool: name:` either — that's the self-hosted shape.
        Assert.DoesNotContain("pool:\n  name:", body);

        // Lifecycle: up → test → down (always). The compose-down step
        // MUST carry `condition: always()` so a failing test still
        // cleans up the stack.
        Assert.Contains("docker compose -f tests/e2e/foundation/docker-compose.yml up -d", body);
        Assert.Contains("dotnet test --filter Category=e2e", body);
        Assert.Contains("docker compose -f tests/e2e/foundation/docker-compose.yml down", body);
        Assert.Contains("condition: always()", body);

        // Phase + stage names appear in displayName / file paths so the
        // operator's pipeline UI surfaces context.
        Assert.Contains("phase foundation", body);
        Assert.Contains("stage setup", body);
    }

    // ── 3. [unit] GitHub workflow uses ubuntu-latest runner ──────────────

    [Fact]
    public void RenderGitHubWorkflow_Uses_Ubuntu_Latest_Runner_No_LabLabels()
    {
        var body = E2eComposeRenderer.RenderGitHubWorkflow("foundation", "setup");

        Assert.False(string.IsNullOrWhiteSpace(body));

        // GitHub Actions syntax: `runs-on: ubuntu-latest` on the job.
        Assert.Contains("runs-on: ubuntu-latest", body);
        Assert.Contains($"name: e2e-foundation", body);
        Assert.Contains("workflow_dispatch:", body);
        Assert.Contains("- uses: actions/checkout@v4", body);

        // MUST NOT carry self-hosted runner labels OR environment refs
        // (lab-only — Stage 7.2).
        Assert.DoesNotContain("self-hosted", body);
        Assert.DoesNotContain("wsfc-lab", body);
        Assert.DoesNotContain("lab-hw", body);
        Assert.DoesNotContain("environment:", body);
        Assert.DoesNotContain("secrets.LAB_", body);
        Assert.DoesNotContain("LAB_CLUSTER_FQDN", body);

        // Lifecycle: up → test → down. The down step uses GH Actions'
        // `if: always()` so failing tests still tear down the stack.
        Assert.Contains("docker compose -f tests/e2e/foundation/docker-compose.yml up -d", body);
        Assert.Contains("dotnet test --filter Category=e2e", body);
        Assert.Contains("docker compose -f tests/e2e/foundation/docker-compose.yml down", body);
        Assert.Contains("if: always()", body);

        // Sanity: RenderAll returns the three expected paths.
        var all = E2eComposeRenderer.RenderAll("foundation", "setup");
        Assert.Equal(3, all.Count);
        Assert.True(all.ContainsKey(E2eComposeRenderer.DockerComposePathFor("foundation")));
        Assert.True(all.ContainsKey(E2eComposeRenderer.AzurePipelinePathFor("foundation")));
        Assert.True(all.ContainsKey(E2eComposeRenderer.GitHubWorkflowPathFor("foundation")));
    }

    // ── 4. [integration] End-to-end compose-phase produces all 3 YAML files ──

    [Fact]
    public async Task ComposePhase_QaE2e_EndToEnd_Produces_All_Three_Pipeline_Files()
    {
        // Boot factory + seed a 1-phase spawn with compose setup +
        // 1 qa-e2e workstream. Drive ProduceStageE2eAsync via the
        // DI-registered deterministic strategy + assert the qa-e2e
        // output dict contains all three Stage 7.1 pipeline files
        // (docker-compose.yml + azure-pipeline.yml + GH workflow)
        // alongside the 2 feature/step files from Stage 4.2.
        const string storyId = "WI-stage71-compose";
        const string spawnId = "spawn-" + storyId;
        const string repoId = "repo-stage71";
        const string productId = "prod-stage71";
        const string qaWsId = "ws-stage71-compose-foundation-setup-e2e";

        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + apply migrations */ }

        // Seed product + repo + spawn + 1 qa-e2e workstream.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = productId, Name = "Stage 7.1", Slug = "stage71",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Repos.Add(new RepoRow
            {
                Id = repoId, ProductId = productId,
                DisplayName = "Stage 7.1 repo", Kind = "local",
                RemoteUrl = "file://stage71", CloneRoot = "/tmp/stage71",
                DefaultBranch = "main",
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId, StoryId = storyId,
                StoryTitle = "Stage 7.1 compose",
                Phase = StorySpawnPhases.Implement,
                RepoId = repoId, BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = qaWsId,
                Name = "setup — E2E",
                Branch = $"ws/{qaWsId}", Base = "main",
                Status = "planning",
                Category = WorkstreamCategories.QaE2e,
                StorySpawnId = spawnId,
                PlanAnchor = "phase-foundation/stage-setup" + CrewStoryArtifactStrategy.QaE2eAnchorSuffix,
                PhaseAnchor = "phase-foundation",
                WorktreeJson = "[]", AgentsJson = "[]",
                PrsJson = "[]", CardsJson = "[]", Summary = string.Empty,
            });
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        // Seed e2e-scenarios.md on main with a compose setup block.
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
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var repo = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repoId);
            await git.EnsureBranchAsync(repo, "main", "main", default);
            await git.CommitFilesAsync(repo, "main", "stage71-seed",
                new Dictionary<string, string>
                {
                    [$"docs/stories/{storyId}/e2e-scenarios.md"] = setupMd,
                },
                "seed e2e-scenarios", default);
        }

        // Drive ProduceStageE2eAsync via DI-registered deterministic strategy.
        IReadOnlyDictionary<string, string> files;
        using (var scope = f.Services.CreateScope())
        {
            var strategy = scope.ServiceProvider.GetRequiredService<IStoryArtifactStrategy>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
            var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == qaWsId);
            files = await strategy.ProduceStageE2eAsync(spawn, ws, default);
        }

        // Stage 7.3 — compose phase now emits 7 files total: the 5
        // Stage 7.1 files (feature + steps + docker-compose.yml +
        // azure-pipeline.yml + GH workflow) + appsettings.e2e.json
        // (per-phase) + E2eBase.cs (first qa-e2e per spawn).
        Assert.Equal(7, files.Count);

        var composePath = "tests/e2e/foundation/docker-compose.yml";
        var adoPath = "tests/e2e/foundation/azure-pipeline.yml";
        var ghPath = ".github/workflows/e2e-foundation.yml";

        // All three Stage 7.1 pipeline files present.
        Assert.True(files.ContainsKey(composePath),
            $"Stage 7.1 must emit {composePath}; got [{string.Join(", ", files.Keys)}]");
        Assert.True(files.ContainsKey(adoPath));
        Assert.True(files.ContainsKey(ghPath));

        // Sanity: feature + steps still emit (Stage 4.2 base output).
        Assert.True(files.ContainsKey($"tests/Features/{storyId}/foundation/setup.feature"));
        Assert.True(files.ContainsKey($"tests/Steps/{storyId}/foundation/SetupSteps.cs"));

        // Stage 7.3 — appsettings.e2e.json + E2eBase.cs ride along.
        Assert.True(files.ContainsKey("tests/e2e/foundation/appsettings.e2e.json"));
        Assert.True(files.ContainsKey($"tests/Steps/{storyId}/E2eBase.cs"));

        // The docker-compose body is the renderer's output (`services:`
        // + at least one service), not a stub placeholder. The pipeline
        // YAMLs reference the compose file path verbatim.
        Assert.Contains("services:", files[composePath]);
        Assert.Contains("docker compose -f tests/e2e/foundation/docker-compose.yml up -d", files[adoPath]);
        Assert.Contains("docker compose -f tests/e2e/foundation/docker-compose.yml up -d", files[ghPath]);

        // Lifecycle's `down` step + always-cleanup also flow through.
        Assert.Contains("condition: always()", files[adoPath]);
        Assert.Contains("if: always()", files[ghPath]);
    }
}
