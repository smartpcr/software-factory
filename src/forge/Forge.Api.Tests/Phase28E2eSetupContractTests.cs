// -----------------------------------------------------------------------
// <copyright file="Phase28E2eSetupContractTests.cs" company="Microsoft Corp.">
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
/// docs/forge/e2e.md Stage 9.2 — integration sweep covering the §8
/// setup contract end-to-end. Each scenario:
///
/// <list type="number">
///   <item>Seeds <c>e2e-scenarios.md</c> on <c>main</c> with a real
///         <c>### Setup</c> block declaring the scenario's setup type.</item>
///   <item>Pre-seeds an impl workstream as already-merged + a paired
///         qa-e2e workstream at <c>planning</c>.</item>
///   <item>Drives the orchestrator once — the Stage 5.1 routing path
///         resolves to <see cref="DeterministicStoryArtifactStrategy.ProduceStageE2eAsync"/>
///         which reads the setup block via <see cref="E2eScenariosSetupReader"/>
///         (Stage 6.3) and folds the per-setup-type file set (Stage 7.1
///         compose / Stage 7.2 lab / Stage 7.3 appsettings + E2eBase)
///         into the commit the orchestrator pushes to the qa-e2e branch.</item>
///   <item>Asserts the file tree on the qa-e2e branch matches the
///         contract for that setup type (presence + content).</item>
/// </list>
///
/// <para>The final scenario covers the Stage 6.2 architect-side
/// <see cref="E2eScenariosLinter"/> directly: a missing <c>### Setup</c>
/// block must produce the <c>e2e.setup_missing</c> violation, and the
/// corrected next-iter version must pass clean.</para>
///
/// <para><c>[Collection("Phase28E2eRefactor")]</c> — shares the
/// dedicated collection with the Stage 9.1 suite so the two run
/// without DB collisions against the broader <c>Phase28</c> fleet.</para>
/// </summary>
[Collection("Phase28E2eRefactor")]
public class Phase28E2eSetupContractTests
{
    private const string ProductId = "prod-phase28-e2e-setup";
    private const string RepoId = "repo-phase28-e2e-setup";
    private const string PhaseSlug = "foundation";
    private const string StageSlug = "setup";

    // ── factory + spawn seed ─────────────────────────────────────────────

    private static (ForgeWebApplicationFactory factory, string storyId, string spawnId) NewSpawn(
        string suffix)
    {
        var factory = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        using (factory.CreateClient()) { /* boot + migrations */ }

        var storyId = $"WI-phase28-setup-{suffix}";
        var spawnId = $"spawn-{storyId}";

        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        if (!control.Products.Any(p => p.Id == ProductId))
        {
            control.Products.Add(new ProductRow
            {
                Id = ProductId,
                Name = "Phase 28 e2e setup contract",
                Slug = "phase28e2esetupcontract",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (!control.Repos.Any(r => r.Id == RepoId))
        {
            control.Repos.Add(new RepoRow
            {
                Id = RepoId,
                ProductId = ProductId,
                DisplayName = "Phase 28 e2e setup repo",
                Kind = "local",
                RemoteUrl = "file://phase28-e2e-setup",
                CloneRoot = "/tmp/phase28-e2e-setup",
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
            Title = $"Phase 28 setup contract / {suffix}",
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId,
            StoryId = storyId,
            StoryTitle = $"Phase 28 setup contract / {suffix}",
            Phase = StorySpawnPhases.Implement,
            PlanBranch = $"story/{storyId}/plan",
            PlanPrId = $"PR-setup-{suffix}-plan",
            RepoId = RepoId,
            BaseBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            StoryPoints = 3,
            PlanDocPrsJson = null,
            AutoMerge = true,
        });
        control.SaveChanges();
        db.SaveChanges();
        return (factory, storyId, spawnId);
    }

    private static WorkstreamRow ImplRow(string id, int sort, string spawnId, string status, string? prId = null)
    {
        return new WorkstreamRow
        {
            Id = id,
            Sort = sort,
            RepoId = RepoId,
            Name = StageSlug,
            Branch = $"ws/{id}",
            Base = "main",
            Epic = string.Empty,
            Status = status,
            WorktreeJson = "[]",
            AgentsJson = "[]",
            PrsJson = prId is null ? "[]" : JsonSerializer.Serialize(new[] { prId }),
            CardsJson = "[]",
            Summary = $"Impl for {StageSlug}",
            Planned = true,
            StorySpawnId = spawnId,
            PlanAnchor = $"phase-{PhaseSlug}/stage-{StageSlug}",
            PhaseAnchor = $"phase-{PhaseSlug}",
            Category = WorkstreamCategories.Impl,
        };
    }

    private static WorkstreamRow QaE2eRow(string id, int sort, string spawnId)
    {
        return new WorkstreamRow
        {
            Id = id,
            Sort = sort,
            RepoId = RepoId,
            Name = $"{StageSlug} — E2E",
            Branch = $"ws/{id}",
            Base = "main",
            Epic = string.Empty,
            Status = "planning",
            WorktreeJson = "[]",
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Summary = $"E2E for {StageSlug}",
            Planned = true,
            StorySpawnId = spawnId,
            PlanAnchor = $"phase-{PhaseSlug}/stage-{StageSlug}" + CrewStoryArtifactStrategy.QaE2eAnchorSuffix,
            PhaseAnchor = $"phase-{PhaseSlug}",
            Category = WorkstreamCategories.QaE2e,
            DepsJson = JsonSerializer.Serialize(new[] { StageSlug }),
        };
    }

    // ── seed helpers ─────────────────────────────────────────────────────

    private static async Task SeedAsync(ForgeWebApplicationFactory f, params WorkstreamRow[] rows)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.Workstreams.AddRange(rows);
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
            Title = $"[impl] {branch}",
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
            Description = "seeded merged impl PR",
        });
        await db.SaveChangesAsync();
    }

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

    /// <summary>
    /// Commit a real <c>e2e-scenarios.md</c> on <c>main</c> so the
    /// strategy's <c>ReadE2eScenariosSetupAsync</c> (Stage 6.3) reads
    /// the appropriate setup block + the file routes through
    /// <c>E2eComposeRenderer</c> / <c>E2eLabRenderer</c> /
    /// <c>E2eBaseRenderer</c> per the spec's matrix.
    /// </summary>
    private static async Task SeedE2eScenariosAsync(
        ForgeWebApplicationFactory f, string storyId, string body)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
        var repo = await control.Repos.AsNoTracking().FirstAsync(r => r.Id == RepoId);
        await git.EnsureBranchAsync(repo, "main", "main");
        await git.CommitFilesAsync(repo, "main", "e2e-setup-seed",
            new Dictionary<string, string>
            {
                [$"docs/stories/{storyId}/e2e-scenarios.md"] = body,
            },
            "seed e2e-scenarios for setup contract");
    }

    private static async Task DriveAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
        await orch.DriveAsync(storyId);
    }

    private static async Task<bool> FileOnBranchAsync(
        ForgeWebApplicationFactory f, string branch, string path)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
        var repo = await control.Repos.AsNoTracking().FirstAsync(r => r.Id == RepoId);
        return await git.FileExistsAsync(repo, branch, path);
    }

    private static async Task<string?> ReadFileOnBranchAsync(
        ForgeWebApplicationFactory f, string branch, string path)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
        var repo = await control.Repos.AsNoTracking().FirstAsync(r => r.Id == RepoId);
        return await git.ReadFileAsync(repo, branch, path);
    }

    // ── fixtures: e2e-scenarios.md per setup type ────────────────────────

    private const string InlineE2eScenarios = """
        # Phase 1: Foundation

        ### Setup
        - **Type**: inline
        - **Local**: in-process; no external services
        - **CI runner**: hosted ubuntu-latest

        ### Scenarios
        - **Scenario:** Setup smoke
        """;

    private const string ComposeE2eScenarios = """
        # Phase 1: Foundation

        ### Setup
        - **Type**: compose
        - **Local**: docker compose up -d
        - **CI runner**: ubuntu-latest
        - **Services**: app, redis

        ### Scenarios
        - **Scenario:** Compose smoke
        """;

    private const string LabWsfcE2eScenarios = """
        # Phase 1: Foundation

        ### Setup
        - **Type**: lab-wsfc
        - **Local**: requires WSFC cluster on the operator's box
        - **CI runner**: self-hosted forge-lab-wsfc
        - **Secrets**: Azure Key Vault `forge-lab-kv` + GitHub environment `lab-wsfc`

        ### Scenarios
        - **Scenario:** CAU cycles correctly
        """;

    private const string LabBareMetalE2eScenarios = """
        # Phase 1: Foundation

        ### Setup
        - **Type**: lab-bare-metal
        - **Local**: requires NIC-X hardware on rack 7
        - **CI runner**: self-hosted forge-lab-hw
        - **Secrets**: Azure Key Vault `forge-lab-kv` + GitHub environment `lab-hw`

        ### Scenarios
        - **Scenario:** NIC-X firmware loads
        """;

    // ════════════════════════════════════════════════════════════════════
    //
    //                          S C E N A R I O S
    //
    // ════════════════════════════════════════════════════════════════════

    // ── Sc1: inline → exactly *.feature + *Steps.cs + appsettings + E2eBase ──

    [Fact]
    public async Task Inline_Story_Commits_Feature_Steps_AppSettings_And_E2eBase()
    {
        var (f, storyId, spawnId) = NewSpawn("inline");
        try
        {
            const string implWsId = "ws-setup-inline-impl";
            const string qaWsId = "ws-setup-inline-qa";

            await SeedAsync(f,
                ImplRow(implWsId, sort: 10, spawnId, status: "complete", prId: "PR-setup-inline-impl"),
                QaE2eRow(qaWsId, sort: 11, spawnId));
            await SeedMergedPrAsync(f, "PR-setup-inline-impl", $"ws/{implWsId}", implWsId);
            await SeedE2eScenariosAsync(f, storyId, InlineE2eScenarios);
            await SeedPlanDocPassesAsync(f, spawnId);

            await DriveAsync(f, storyId);

            // The qa-e2e branch is `ws/{qaWsId}` — the orchestrator
            // committed the strategy's output there before opening
            // the PR.
            var qaBranch = $"ws/{qaWsId}";

            // The 4 deterministic-strategy outputs for an inline phase
            // (Stage 4.2 + Stage 7.3 idempotency wins the first qa-e2e
            // for this spawn so E2eBase ships).
            var featurePath = $"tests/Features/{storyId}/{PhaseSlug}/{StageSlug}.feature";
            var stepsPath = $"tests/Steps/{storyId}/{PhaseSlug}/SetupSteps.cs";
            var appSettingsPath = $"tests/e2e/{PhaseSlug}/appsettings.e2e.json";
            var e2eBasePath = $"tests/Steps/{storyId}/E2eBase.cs";

            Assert.True(await FileOnBranchAsync(f, qaBranch, featurePath),
                $"Inline qa-e2e branch must commit {featurePath}");
            Assert.True(await FileOnBranchAsync(f, qaBranch, stepsPath),
                $"Inline qa-e2e branch must commit {stepsPath}");
            Assert.True(await FileOnBranchAsync(f, qaBranch, appSettingsPath),
                $"Stage 7.3 — inline qa-e2e branch must commit {appSettingsPath}");
            Assert.True(await FileOnBranchAsync(f, qaBranch, e2eBasePath),
                $"Stage 7.3 — first qa-e2e of spawn must commit {e2eBasePath}");

            // Inline setups must NOT ship pipeline YAMLs (those are
            // compose / lab artefacts; on an inline phase the test
            // suite runs in-process).
            Assert.False(await FileOnBranchAsync(f, qaBranch,
                $"tests/e2e/{PhaseSlug}/docker-compose.yml"));
            Assert.False(await FileOnBranchAsync(f, qaBranch,
                $"tests/e2e/{PhaseSlug}/azure-pipeline.yml"));
            Assert.False(await FileOnBranchAsync(f, qaBranch,
                $".github/workflows/e2e-{PhaseSlug}.yml"));

            // Sanity: feature carries the @setup-inline tag (Stage 4.2's
            // deterministic feature emitter pins this from the
            // resolved setup type).
            var featureBody = await ReadFileOnBranchAsync(f, qaBranch, featurePath);
            Assert.NotNull(featureBody);
            Assert.Contains("@setup-inline", featureBody);

            // Stage 7.3 — appsettings carries an empty E2e section
            // (inline → no env vars to declare).
            var appsBody = await ReadFileOnBranchAsync(f, qaBranch, appSettingsPath);
            Assert.NotNull(appsBody);
            Assert.Contains("\"E2e\":", appsBody);
            Assert.DoesNotContain("LAB_CLUSTER_FQDN", appsBody);
            Assert.DoesNotContain("COMPOSE_REDIS_HOST", appsBody);
        }
        finally { f.Dispose(); }
    }

    // ── Sc2: compose → above + docker-compose + ADO + GH workflow ────────

    [Fact]
    public async Task Compose_Story_Commits_Compose_File_Plus_Pipeline_Yamls()
    {
        var (f, storyId, spawnId) = NewSpawn("compose");
        try
        {
            const string implWsId = "ws-setup-compose-impl";
            const string qaWsId = "ws-setup-compose-qa";

            await SeedAsync(f,
                ImplRow(implWsId, sort: 10, spawnId, status: "complete", prId: "PR-setup-compose-impl"),
                QaE2eRow(qaWsId, sort: 11, spawnId));
            await SeedMergedPrAsync(f, "PR-setup-compose-impl", $"ws/{implWsId}", implWsId);
            await SeedE2eScenariosAsync(f, storyId, ComposeE2eScenarios);
            await SeedPlanDocPassesAsync(f, spawnId);

            await DriveAsync(f, storyId);

            var qaBranch = $"ws/{qaWsId}";
            var composePath = $"tests/e2e/{PhaseSlug}/docker-compose.yml";
            var adoPath = $"tests/e2e/{PhaseSlug}/azure-pipeline.yml";
            var ghPath = $".github/workflows/e2e-{PhaseSlug}.yml";
            var appSettingsPath = $"tests/e2e/{PhaseSlug}/appsettings.e2e.json";

            // The 3 Stage 7.1 compose-renderer artefacts are present.
            Assert.True(await FileOnBranchAsync(f, qaBranch, composePath));
            Assert.True(await FileOnBranchAsync(f, qaBranch, adoPath));
            Assert.True(await FileOnBranchAsync(f, qaBranch, ghPath));

            // Compose ADO + GH YAMLs use the hosted ubuntu-latest agent
            // (NOT a self-hosted pool — that's lab-* only).
            var adoBody = await ReadFileOnBranchAsync(f, qaBranch, adoPath);
            var ghBody = await ReadFileOnBranchAsync(f, qaBranch, ghPath);
            Assert.NotNull(adoBody);
            Assert.NotNull(ghBody);
            Assert.Contains("vmImage: ubuntu-latest", adoBody);
            Assert.Contains("runs-on: ubuntu-latest", ghBody);
            Assert.Contains("docker compose -f", adoBody);
            Assert.Contains("docker compose -f", ghBody);

            // Stage 7.3 — appsettings carries the COMPOSE_REDIS_* env-var
            // names (NOT the LAB_* set).
            var appsBody = await ReadFileOnBranchAsync(f, qaBranch, appSettingsPath);
            Assert.NotNull(appsBody);
            Assert.Contains("COMPOSE_REDIS_HOST", appsBody);
            Assert.DoesNotContain("LAB_CLUSTER_FQDN", appsBody);
        }
        finally { f.Dispose(); }
    }

    // ── Sc3: lab-wsfc → ADO pool=forge-lab-wsfc + Key Vault=forge-lab-kv ──

    [Fact]
    public async Task LabWsfc_Story_AdoYaml_Names_ForgeLabWsfc_Pool_And_ForgeLabKv_KeyVault()
    {
        var (f, storyId, spawnId) = NewSpawn("lab-wsfc");
        try
        {
            const string implWsId = "ws-setup-labwsfc-impl";
            const string qaWsId = "ws-setup-labwsfc-qa";

            await SeedAsync(f,
                ImplRow(implWsId, sort: 10, spawnId, status: "complete", prId: "PR-setup-labwsfc-impl"),
                QaE2eRow(qaWsId, sort: 11, spawnId));
            await SeedMergedPrAsync(f, "PR-setup-labwsfc-impl", $"ws/{implWsId}", implWsId);
            await SeedE2eScenariosAsync(f, storyId, LabWsfcE2eScenarios);
            await SeedPlanDocPassesAsync(f, spawnId);

            await DriveAsync(f, storyId);

            var qaBranch = $"ws/{qaWsId}";
            var adoPath = $"tests/e2e/{PhaseSlug}/azure-pipeline.yml";

            var adoBody = await ReadFileOnBranchAsync(f, qaBranch, adoPath);
            Assert.NotNull(adoBody);

            // The headline Stage 7.2 assertions: the lab pool name + Key
            // Vault reference both surface in the ADO YAML.
            Assert.Contains("name: forge-lab-wsfc", adoBody);
            Assert.Contains("KeyVaultName: forge-lab-kv", adoBody);
            // Variable group + AzureKeyVault@2 task are the Stage 7.2
            // lab-secret plumbing.
            Assert.Contains("- group: forge-lab-vg", adoBody);
            Assert.Contains("task: AzureKeyVault@2", adoBody);
            Assert.Contains("LAB_CLUSTER_FQDN,LAB_ADMIN_USER,LAB_ADMIN_PASSWORD", adoBody);

            // Lab phases must NOT ship docker-compose.yml — lab tests
            // run against pre-provisioned real infrastructure.
            Assert.False(await FileOnBranchAsync(f, qaBranch,
                $"tests/e2e/{PhaseSlug}/docker-compose.yml"));

            // Stage 7.3 — appsettings carries the 3 LAB_* env-var names.
            var appsPath = $"tests/e2e/{PhaseSlug}/appsettings.e2e.json";
            var appsBody = await ReadFileOnBranchAsync(f, qaBranch, appsPath);
            Assert.NotNull(appsBody);
            Assert.Contains("LAB_CLUSTER_FQDN", appsBody);
            Assert.Contains("LAB_ADMIN_USER", appsBody);
            Assert.Contains("LAB_ADMIN_PASSWORD", appsBody);
        }
        finally { f.Dispose(); }
    }

    // ── Sc4: lab-bare-metal → GH workflow [self-hosted, lab-hw] + env=lab-hw ──

    [Fact]
    public async Task LabBareMetal_Story_GhWorkflow_Names_SelfHosted_LabHw_Labels_And_LabHw_Environment()
    {
        var (f, storyId, spawnId) = NewSpawn("lab-bare-metal");
        try
        {
            const string implWsId = "ws-setup-labhw-impl";
            const string qaWsId = "ws-setup-labhw-qa";

            await SeedAsync(f,
                ImplRow(implWsId, sort: 10, spawnId, status: "complete", prId: "PR-setup-labhw-impl"),
                QaE2eRow(qaWsId, sort: 11, spawnId));
            await SeedMergedPrAsync(f, "PR-setup-labhw-impl", $"ws/{implWsId}", implWsId);
            await SeedE2eScenariosAsync(f, storyId, LabBareMetalE2eScenarios);
            await SeedPlanDocPassesAsync(f, spawnId);

            await DriveAsync(f, storyId);

            var qaBranch = $"ws/{qaWsId}";
            var ghPath = $".github/workflows/e2e-{PhaseSlug}.yml";

            var ghBody = await ReadFileOnBranchAsync(f, qaBranch, ghPath);
            Assert.NotNull(ghBody);

            // The headline Stage 7.2 assertions for lab-bare-metal: the
            // GH workflow targets the hardware-rack self-hosted runner
            // (NOT wsfc-lab) + the `lab-hw` GH environment for review
            // gating.
            Assert.Contains("runs-on: [self-hosted, lab-hw]", ghBody);
            Assert.Contains("environment: lab-hw", ghBody);
            // GH-shape secrets references — lab phases must use the
            // `${{ secrets.* }}` syntax, NOT ADO's `$(VAR)` macros.
            Assert.Contains("${{ secrets.LAB_CLUSTER_FQDN }}", ghBody);
            Assert.Contains("${{ secrets.LAB_ADMIN_USER }}", ghBody);
            Assert.Contains("${{ secrets.LAB_ADMIN_PASSWORD }}", ghBody);
            Assert.DoesNotContain("$(LAB_CLUSTER_FQDN)", ghBody);
            // Sanity: WSFC pool NOT referenced — that's the other lab
            // type's hardware target.
            Assert.DoesNotContain("forge-lab-wsfc", ghBody);
            Assert.DoesNotContain("wsfc-lab", ghBody);

            // ADO YAML sibling targets the hardware pool.
            var adoPath = $"tests/e2e/{PhaseSlug}/azure-pipeline.yml";
            var adoBody = await ReadFileOnBranchAsync(f, qaBranch, adoPath);
            Assert.NotNull(adoBody);
            Assert.Contains("name: forge-lab-hw", adoBody);
        }
        finally { f.Dispose(); }
    }

    // ── Sc5: linter rejects missing ### Setup; accepts corrected next iter ──

    [Fact]
    public void Architect_Linter_Rejects_Missing_Setup_Heading_And_Accepts_Corrected_NextIter()
    {
        // Pure-helper sweep against the Stage 6.2 architect-side linter.
        // No factory / no orchestrator — the linter is the spec's
        // server-side gate on every e2e-scenarios.md the architect
        // generator commits; this scenario pins its before/after
        // behaviour for one canonical Phase 1 iteration.

        // Iter N — `### Setup` H3 is missing (the generator forgot to
        // include it). Linter must surface the `e2e.setup_missing`
        // violation + the evaluator-rules section must flag it as a
        // hard refusal criterion.
        const string brokenMarkdown = """
            # Phase 1: Foundation

            ### Scenarios
            - **Scenario:** Foundation works
            """;
        var beforeResult = E2eScenariosLinter.Validate(brokenMarkdown);
        Assert.False(beforeResult.IsClean,
            "Linter must NOT accept a phase missing the `### Setup` H3.");
        var setupMissing = Assert.Single(beforeResult.Errors,
            e => e.Code == E2eScenariosLinter.ErrorCodes.SetupMissing);
        Assert.Equal("Foundation", setupMissing.PhaseName);
        Assert.Contains("missing the required `### Setup` H3", setupMissing.Detail);

        // The evaluator-rules section the architect's evaluator prompt
        // is built from must surface this violation under "Current
        // violations" so the generator sees the gate criterion AND its
        // current state in-context.
        var rules = E2eScenariosLinter.BuildEvaluatorRulesSection(beforeResult);
        Assert.Contains("Current violations", rules);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.SetupMissing, rules);
        // Hard-refusal language is present so the generator can't
        // ignore the gate.
        Assert.Contains("verdict:", rules);
        Assert.Contains("Score MUST be < 90", rules);

        // Iter N+1 — operator (or the architect generator on the next
        // gen↔eval cycle) added the `### Setup` block with a valid
        // Type. Linter must now return Clean.
        const string fixedMarkdown = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: inline
            - **Local**: in-process; no external services
            - **CI runner**: hosted ubuntu-latest

            ### Scenarios
            - **Scenario:** Foundation works
            """;
        var afterResult = E2eScenariosLinter.Validate(fixedMarkdown);
        Assert.True(afterResult.IsClean,
            "Linter must accept a Phase 1 with `### Setup` + a valid Type. "
            + $"Errors: [{string.Join(", ", afterResult.Errors.Select(e => $"{e.Code}: {e.Detail}"))}]");

        // The evaluator-rules section without a violation list still
        // renders the rules (preflight prompt construction before the
        // file exists). Used by Stage 6.2's prompt scaffolding.
        var rulesClean = E2eScenariosLinter.BuildEvaluatorRulesSection(result: null);
        Assert.DoesNotContain("Current violations", rulesClean);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.SetupMissing, rulesClean);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.SetupTypeInvalid, rulesClean);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.LabSecretsIncomplete, rulesClean);

        // Cross-check the other two violation codes the linter
        // enforces — a lab Type without complete secrets must trip
        // `e2e.lab_secrets_incomplete` (proves the helper actually
        // discriminates between the three codes, not just the one
        // covered by the headline iter).
        const string labMissingSecrets = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: lab-wsfc
            - **Local**: requires WSFC cluster
            - **CI runner**: self-hosted forge-lab-wsfc

            ### Scenarios
            - **Scenario:** CAU cycles
            """;
        var labResult = E2eScenariosLinter.Validate(labMissingSecrets);
        Assert.Contains(labResult.Errors,
            e => e.Code == E2eScenariosLinter.ErrorCodes.LabSecretsIncomplete);
    }
}
