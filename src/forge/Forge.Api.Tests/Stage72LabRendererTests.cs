// -----------------------------------------------------------------------
// <copyright file="Stage72LabRendererTests.cs" company="Microsoft Corp.">
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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// docs/forge/e2e.md Stage 7.2 — pin the
/// <see cref="E2eLabRenderer"/> contract:
/// <list type="bullet">
///   <item>lab-wsfc ADO YAML targets the <c>forge-lab-wsfc</c> pool
///         + references <c>forge-lab-vg</c> variable group + the
///         <c>AzureKeyVault@2</c> task fetches secrets from
///         <c>forge-lab-kv</c>.</item>
///   <item>lab-wsfc GH workflow uses <c>[self-hosted, wsfc-lab]</c>
///         runner labels + <c>environment: lab-wsfc</c> (review gate).</item>
///   <item>lab-bare-metal swaps to the hardware pool / runner labels
///         (<c>forge-lab-hw</c> / <c>lab-hw</c>).</item>
///   <item>Both YAMLs reference <c>LAB_CLUSTER_FQDN</c> /
///         <c>LAB_ADMIN_USER</c> / <c>LAB_ADMIN_PASSWORD</c> as the
///         provider-appropriate env-var syntax (<c>$(VAR)</c> for ADO,
///         <c>${{ secrets.VAR }}</c> for GH).</item>
/// </list>
///
/// <para>The integration Fact smoke-validates BOTH rendered YAMLs
/// against <c>YamlDotNet</c> so we don't ship malformed pipeline
/// files — the rendered text MUST round-trip through a real YAML
/// parser before the operator commits it.</para>
/// </summary>
[Collection("Phase28")]
public class Stage72LabRendererTests
{
    // ── 1. [unit] lab-wsfc ADO YAML names pool + variable group ──────────

    [Fact]
    public void RenderAzurePipeline_LabWsfc_Targets_ForgeLabWsfc_Pool_And_VariableGroup()
    {
        var body = E2eLabRenderer.RenderAzurePipeline(
            E2eSetupType.LabWsfc, "foundation", "setup");

        Assert.False(string.IsNullOrWhiteSpace(body));

        // Self-hosted pool — `pool: name: forge-lab-wsfc`, NOT
        // `vmImage:` (that's the compose / hosted runner shape).
        Assert.Contains("pool:", body);
        Assert.Contains("name: forge-lab-wsfc", body);
        Assert.DoesNotContain("vmImage:", body);

        // Variable group + Key Vault task.
        Assert.Contains("variables:", body);
        Assert.Contains("- group: forge-lab-vg", body);
        Assert.Contains("task: AzureKeyVault@2", body);
        Assert.Contains("KeyVaultName: forge-lab-kv", body);
        Assert.Contains("SecretsFilter:", body);
        Assert.Contains("LAB_CLUSTER_FQDN,LAB_ADMIN_USER,LAB_ADMIN_PASSWORD", body);

        // The test step references the three env-vars via ADO's
        // `$(VAR)` macro syntax (NOT the GH `${{ secrets.* }}` shape).
        Assert.Contains("$(LAB_CLUSTER_FQDN)", body);
        Assert.Contains("$(LAB_ADMIN_USER)", body);
        Assert.Contains("$(LAB_ADMIN_PASSWORD)", body);
        Assert.DoesNotContain("${{ secrets.LAB_", body);

        // Phase + stage names surface for ADO pipeline UI context.
        Assert.Contains("phase foundation", body);
        Assert.Contains("stage setup", body);

        // Bootstrap step is OFF by default.
        Assert.DoesNotContain("Pre-test bootstrap", body);
        Assert.DoesNotContain("bootstrap.ps1", body);
    }

    // ── 2. [unit] lab-wsfc GH workflow uses self-hosted labels + environment ──

    [Fact]
    public void RenderGitHubWorkflow_LabWsfc_Uses_SelfHosted_Labels_And_LabEnvironment()
    {
        var body = E2eLabRenderer.RenderGitHubWorkflow(
            E2eSetupType.LabWsfc, "foundation", "setup");

        Assert.False(string.IsNullOrWhiteSpace(body));

        // Self-hosted runner labels (literal — GH parses the JSON-ish
        // shorthand) + GH environment for review gate.
        Assert.Contains("runs-on: [self-hosted, wsfc-lab]", body);
        Assert.Contains("environment: lab-wsfc", body);

        // Standard GH steps prelude.
        Assert.Contains("name: e2e-foundation", body);
        Assert.Contains("workflow_dispatch:", body);
        Assert.Contains("- uses: actions/checkout@v4", body);

        // Three lab env-vars sourced from GH environment secrets.
        Assert.Contains("${{ secrets.LAB_CLUSTER_FQDN }}", body);
        Assert.Contains("${{ secrets.LAB_ADMIN_USER }}", body);
        Assert.Contains("${{ secrets.LAB_ADMIN_PASSWORD }}", body);
        // No ADO macro syntax in GH file.
        Assert.DoesNotContain("$(LAB_CLUSTER_FQDN)", body);
        Assert.DoesNotContain("forge-lab-vg", body); // ADO-only construct

        // Bootstrap step is OFF by default.
        Assert.DoesNotContain("Pre-test bootstrap", body);
    }

    // ── 3. [unit] lab-bare-metal swaps to hardware pool / labels ─────────

    [Fact]
    public void RenderAll_LabBareMetal_Swaps_To_Hardware_Pool_And_Labels()
    {
        var files = E2eLabRenderer.RenderAll(
            E2eSetupType.LabBareMetal, "drivers", "nic-x");

        // 2 entries (lab DOESN'T ship docker-compose).
        Assert.Equal(2, files.Count);
        var adoPath = E2eLabRenderer.AzurePipelinePathFor("drivers");
        var ghPath = E2eLabRenderer.GitHubWorkflowPathFor("drivers");
        Assert.True(files.ContainsKey(adoPath));
        Assert.True(files.ContainsKey(ghPath));

        var ado = files[adoPath];
        Assert.Contains("name: forge-lab-hw", ado);
        Assert.DoesNotContain("forge-lab-wsfc", ado);
        // Variable group + Key Vault stay the same — both lab types
        // share the same secret store; only the runner pool differs.
        Assert.Contains("- group: forge-lab-vg", ado);
        Assert.Contains("KeyVaultName: forge-lab-kv", ado);

        var gh = files[ghPath];
        Assert.Contains("runs-on: [self-hosted, lab-hw]", gh);
        Assert.Contains("environment: lab-hw", gh);
        Assert.DoesNotContain("wsfc-lab", gh);

        // Non-lab types throw — the renderer is lab-specific.
        var inlineEx = Assert.Throws<ArgumentOutOfRangeException>(
            () => E2eLabRenderer.RenderAll(E2eSetupType.Inline, "foo", "bar"));
        Assert.Contains("lab-wsfc / lab-bare-metal", inlineEx.Message);

        var composeEx = Assert.Throws<ArgumentOutOfRangeException>(
            () => E2eLabRenderer.RenderAzurePipeline(E2eSetupType.Compose, "foo", "bar"));
        Assert.Contains("lab-wsfc / lab-bare-metal", composeEx.Message);
    }

    // ── 4. [unit] Bootstrap step appears when includeBootstrap=true ──────

    [Fact]
    public void RenderAll_With_Bootstrap_Includes_PreTest_Step_In_Both_Providers()
    {
        var files = E2eLabRenderer.RenderAll(
            E2eSetupType.LabWsfc, "foundation", "setup", includeBootstrap: true);

        var ado = files[E2eLabRenderer.AzurePipelinePathFor("foundation")];
        var gh = files[E2eLabRenderer.GitHubWorkflowPathFor("foundation")];

        // ADO uses the PowerShell@2 task pointing at the convention path.
        Assert.Contains("task: PowerShell@2", ado);
        Assert.Contains("Pre-test bootstrap for phase foundation", ado);
        Assert.Contains("filePath: tests/e2e/foundation/bootstrap.ps1", ado);

        // GH uses a `pwsh` shell run pointing at the same path.
        Assert.Contains("Pre-test bootstrap for phase foundation", gh);
        Assert.Contains("run: ./tests/e2e/foundation/bootstrap.ps1", gh);
        Assert.Contains("shell: pwsh", gh);

        // Bootstrap path helper agrees with the file references.
        Assert.Equal("tests/e2e/foundation/bootstrap.ps1",
            E2eLabRenderer.BootstrapPathFor("foundation"));

        // Bootstrap env-vars sourced from the right provider syntax.
        Assert.Contains("LAB_CLUSTER_FQDN: $(LAB_CLUSTER_FQDN)", ado);
        Assert.Contains("LAB_CLUSTER_FQDN: ${{ secrets.LAB_CLUSTER_FQDN }}", gh);
    }

    // ── 5. [integration] lab-wsfc phase produces YAML that parses cleanly ──

    [Fact]
    public async Task LabWsfc_Phase_QaE2e_Produces_YamlDotNet_Parseable_Pipeline_Files()
    {
        // End-to-end: boot factory + seed 1-phase lab-wsfc spawn + 1
        // qa-e2e workstream. Drive ProduceStageE2eAsync via the
        // DI-registered deterministic strategy + assert BOTH rendered
        // YAML files round-trip through a real YamlDotNet parser
        // without throwing — smoke-validates the renderer doesn't
        // emit malformed pipeline files.
        const string storyId = "WI-stage72-labwsfc";
        const string spawnId = "spawn-" + storyId;
        const string repoId = "repo-stage72";
        const string productId = "prod-stage72";
        const string qaWsId = "ws-stage72-labwsfc-foundation-setup-e2e";

        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + apply migrations */ }

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = productId, Name = "Stage 7.2", Slug = "stage72",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Repos.Add(new RepoRow
            {
                Id = repoId, ProductId = productId,
                DisplayName = "Stage 7.2 repo", Kind = "local",
                RemoteUrl = "file://stage72", CloneRoot = "/tmp/stage72",
                DefaultBranch = "main",
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId, StoryId = storyId,
                StoryTitle = "Stage 7.2 lab-wsfc",
                Phase = StorySpawnPhases.Implement,
                RepoId = repoId, BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = qaWsId, Name = "setup — E2E",
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

        // Seed e2e-scenarios.md with a lab-wsfc setup block.
        var setupMd = """
            # Phase 1: Foundation

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
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var repo = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repoId);
            await git.EnsureBranchAsync(repo, "main", "main", default);
            await git.CommitFilesAsync(repo, "main", "stage72-seed",
                new Dictionary<string, string>
                {
                    [$"docs/stories/{storyId}/e2e-scenarios.md"] = setupMd,
                },
                "seed", default);
        }

        IReadOnlyDictionary<string, string> files;
        using (var scope = f.Services.CreateScope())
        {
            var strategy = scope.ServiceProvider.GetRequiredService<IStoryArtifactStrategy>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
            var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == qaWsId);
            files = await strategy.ProduceStageE2eAsync(spawn, ws, default);
        }

        // Stage 7.3 — lab-wsfc now emits 6 files: feature + steps +
        // ADO YAML + GH workflow (no docker-compose for lab) +
        // appsettings.e2e.json + E2eBase.cs.
        Assert.Equal(6, files.Count);

        var adoPath = E2eLabRenderer.AzurePipelinePathFor("foundation");
        var ghPath = E2eLabRenderer.GitHubWorkflowPathFor("foundation");
        Assert.True(files.ContainsKey(adoPath));
        Assert.True(files.ContainsKey(ghPath));
        Assert.True(files.ContainsKey("tests/e2e/foundation/appsettings.e2e.json"));
        Assert.True(files.ContainsKey($"tests/Steps/{storyId}/E2eBase.cs"));

        // Smoke-parse both YAMLs through YamlDotNet — proves the
        // renderer didn't emit malformed pipeline files. We use the
        // lenient deserializer so unknown keys (`pool:`, `steps:`,
        // `jobs:`, etc.) don't trip the parser.
        var deserialiser = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // Deserialise into an arbitrary object — YamlDotNet's strictest
        // failure mode is the parser itself rejecting the document.
        var adoDoc = deserialiser.Deserialize<object?>(files[adoPath]);
        Assert.NotNull(adoDoc);

        var ghDoc = deserialiser.Deserialize<object?>(files[ghPath]);
        Assert.NotNull(ghDoc);

        // Substantive content also flows through (independent of YAML
        // shape parsing).
        Assert.Contains("name: forge-lab-wsfc", files[adoPath]);
        Assert.Contains("KeyVaultName: forge-lab-kv", files[adoPath]);
        Assert.Contains("LAB_CLUSTER_FQDN", files[adoPath]);
        Assert.Contains("runs-on: [self-hosted, wsfc-lab]", files[ghPath]);
        Assert.Contains("environment: lab-wsfc", files[ghPath]);
        Assert.Contains("${{ secrets.LAB_CLUSTER_FQDN }}", files[ghPath]);
    }
}
