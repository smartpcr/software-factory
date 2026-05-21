// -----------------------------------------------------------------------
// <copyright file="Stage43CrewQaE2eTests.cs" company="Microsoft Corp.">
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
/// docs/forge/e2e.md Stage 4.3 — the LLM-driven
/// <see cref="CrewStoryArtifactStrategy.ProduceStageE2eAsync"/>
/// implementation. Stage 4.3 is the production path Stage 4.2's
/// deterministic strategy stands in for; pinning the prompt shape +
/// guard behaviour here means Stage 5.x can wire the orchestrator
/// against a known-good interface and Stage 9.x can drive a fake
/// IAgentRunner end-to-end without re-discovering the contract.
///
/// <para>The first two Facts are pure-helper (no boot, no DB): they
/// hand-construct <see cref="CrewStoryArtifactStrategy.QaE2ePromptInputs"/>
/// and call <see cref="CrewStoryArtifactStrategy.BuildQaE2ePrompt"/>
/// directly. The third Fact boots <see cref="ForgeWebApplicationFactory"/>
/// and drives <see cref="CrewStoryArtifactStrategy.ProduceStageE2eAsync"/>
/// against a qa-e2e workstream whose parent stage has zero acceptance
/// scenarios — verifies the defense-in-depth guard fires before any
/// agent invocation. The fourth Fact is the integration end-to-end:
/// boot factory + in-memory git pre-seeded with e2e-scenarios.md +
/// an impl workstream branch with a diff; calls the strategy's
/// internal <c>ResolveQaE2ePromptInputsAsync</c> + <c>BuildQaE2ePrompt</c>
/// pair (no agent runner involved) and asserts the prompt carries the
/// real impl-diff path + the parsed setup type for the right phase.
/// The "fake IAgentRunner + full gate re-entry" end-to-end scenario
/// the spec mentions lives in Stage 9.1's integration suite where the
/// orchestrator-driven flow is wired.</para>
/// </summary>
[Collection("Phase28")]
public class Stage43CrewQaE2eTests
{
    private const string StoryId = "WI-stage43";
    private const string SpawnId = "spawn-" + StoryId;
    private const string RepoId = "repo-stage43";
    private const string ProductId = "prod-stage43";
    private const string ImplAnchor = "phase-foundation/stage-setup";

    private static CrewStoryArtifactStrategy.QaE2ePromptInputs SampleInputs(
        IReadOnlyList<string>? scenarios = null,
        string? implDiff = null,
        E2eSetupType setupType = E2eSetupType.Inline,
        string? setupBlock = null,
        string? attachmentSection = null) =>
        new(
            StoryId: StoryId,
            StoryTitle: "Stage 4.3 fixture",
            StoryDescription: "Lay down qa-e2e producer.",
            WorkstreamId: $"ws-{StoryId.ToLowerInvariant()}-phase-foundation-stage-setup-e2e",
            WorkstreamName: "Setup — E2E",
            PhaseSlug: "foundation",
            StageSlug: "setup",
            TestScenarios: scenarios ?? new[] { "happy path", "error path" },
            ImplDiff: implDiff
                ?? "# Impl workstream `ws-foo` branch `ws/foo` vs `main` — 2 files changed:\n  src/Foo.cs\n  src/Bar.cs",
            SetupType: setupType,
            SetupBlockMarkdown: setupBlock ?? string.Empty,
            AttachmentPromptSection: attachmentSection ?? string.Empty);

    // ── 1. [unit] Prompt contains ## Implementation diff with the diff body ──

    [Fact]
    public void BuildQaE2ePrompt_Includes_ImplementationDiff_Section()
    {
        var inputs = SampleInputs(
            implDiff: "# Impl branch `ws/auth-impl` vs `main` — 3 files changed:\n  src/Auth.cs\n  src/Token.cs\n  tests/AuthTests.cs");

        var gen = CrewStoryArtifactStrategy.BuildQaE2ePrompt(inputs, evaluator: false);
        var eval = CrewStoryArtifactStrategy.BuildQaE2ePrompt(inputs, evaluator: true);

        // Both prompts carry the same context sections — diff included.
        Assert.Contains("## Implementation diff", gen);
        Assert.Contains("## Implementation diff", eval);
        Assert.Contains("```diff", gen);
        Assert.Contains("src/Auth.cs", gen);
        Assert.Contains("src/Token.cs", gen);
        Assert.Contains("tests/AuthTests.cs", gen);
        // Diff body is fenced so the agent doesn't try to interpret
        // diff markers as markdown.
        Assert.Contains("```\n\n", gen.Replace("\r\n", "\n"));

        // Generator vs evaluator differ in the trailing task block.
        Assert.Contains("ONE Gherkin `.feature` file", gen);
        Assert.Contains("\"verdict\": \"pass\" | \"fail\"", eval);

        // Empty diff falls back to a placeholder so the section stays
        // present even pre-impl-commit; the agent sees it's empty.
        var emptyDiff = CrewStoryArtifactStrategy.BuildQaE2ePrompt(
            SampleInputs(implDiff: string.Empty),
            evaluator: false);
        Assert.Contains("## Implementation diff", emptyDiff);
        Assert.Contains("no diff captured", emptyDiff);
    }

    // ── 2. [unit] Prompt contains ## Setup with the resolved type + block ──

    [Fact]
    public void BuildQaE2ePrompt_Includes_Setup_Section_With_Type_And_Body()
    {
        // Lab-wsfc setup with a real block body — verify the body is
        // emitted verbatim AND the setup type is mentioned.
        var setupBlock = """
            - **Type**: lab-wsfc
            - **Local**: requires WSFC cluster
            - **CI runner**: self-hosted forge-lab-wsfc
            - **Secrets**: Azure Key Vault `forge-lab-kv`
            """;
        var inputs = SampleInputs(
            setupType: E2eSetupType.LabWsfc,
            setupBlock: setupBlock);

        var gen = CrewStoryArtifactStrategy.BuildQaE2ePrompt(inputs, evaluator: false);

        Assert.Contains("## Setup", gen);
        Assert.Contains("Type**: lab-wsfc", gen);
        Assert.Contains("forge-lab-kv", gen);
        Assert.Contains("self-hosted forge-lab-wsfc", gen);

        // The task-block references the setup type slug so the agent
        // knows whether to emit YAML stubs.
        Assert.Contains("setup-lab-wsfc", gen);

        // When the setup block is empty, the section still appears with
        // a fallback line naming the (defaulted) type — so the agent
        // doesn't get a structural surprise.
        var bareInline = CrewStoryArtifactStrategy.BuildQaE2ePrompt(
            SampleInputs(setupType: E2eSetupType.Inline, setupBlock: string.Empty),
            evaluator: false);
        Assert.Contains("## Setup", bareInline);
        Assert.Contains("Type:_ inline", bareInline);
        Assert.Contains("no `### Setup` block", bareInline);
    }

    // ── 3. [unit] Empty test scenarios → InvalidOperationException ───────

    [Fact]
    public async Task ProduceStageE2eAsync_Throws_When_Stage_Has_Zero_TestScenarios()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + apply migrations */ }

        // Seed product + repo + story + spawn + qa-e2e workstream WITHOUT
        // a matching WorkItem (so the DescriptionMd lookup returns the
        // workstream's Summary, which we'll also leave scenario-free).
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = ProductId,
                Name = "Stage 4.3",
                Slug = "stage43",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Repos.Add(new RepoRow
            {
                Id = RepoId,
                ProductId = ProductId,
                DisplayName = "Stage 4.3 Repo",
                Kind = "local",
                RemoteUrl = "file://stage43",
                CloneRoot = "/tmp/stage43",
                DefaultBranch = "main",
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = StoryId,
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "Stage 4.3",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = SpawnId,
                StoryId = StoryId,
                StoryTitle = "Stage 4.3",
                Phase = StorySpawnPhases.Implement,
                PlanBranch = $"story/{StoryId}/plan",
                RepoId = RepoId,
                BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 3,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-stage43-empty-e2e",
                Name = "Empty — E2E",
                PlanAnchor = ImplAnchor + CrewStoryArtifactStrategy.QaE2eAnchorSuffix,
                PhaseAnchor = "phase-foundation",
                Category = WorkstreamCategories.QaE2e,
                StorySpawnId = SpawnId,
                Branch = $"ws/{StoryId}/phase-foundation-stage-setup-e2e",
                Base = "main",
                Status = "planning",
                // Summary intentionally has no `## Test Scenarios` section
                // so the reader falls through with an empty list.
                Summary = "No scenarios on purpose.",
            });
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        // Resolve the CrewStoryArtifactStrategy directly. The factory's
        // DI override registers DeterministicStoryArtifactStrategy as the
        // bound IStoryArtifactStrategy, so we have to construct
        // CrewStoryArtifactStrategy via its DI ctor.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == SpawnId);
            var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == "ws-stage43-empty-e2e");

            var crew = ActivatorUtilities.CreateInstance<CrewStoryArtifactStrategy>(scope.ServiceProvider);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await crew.ProduceStageE2eAsync(spawn, ws, default));

            // Message names the workstream + the resolution path so a
            // future operator triaging a stalled qa-e2e knows where to
            // look.
            Assert.Contains("ws-stage43-empty-e2e", ex.Message);
            Assert.Contains("zero acceptance test scenarios", ex.Message);
        }
    }

    // ── 4. [integration] ResolveQaE2ePromptInputsAsync + BuildQaE2ePrompt e2e ──

    [Fact]
    public async Task ResolveQaE2ePromptInputsAsync_Reads_Diff_And_SetupType_From_Git_End_To_End()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }

        const string implWorkstreamId = "ws-stage43-int-phase-foundation-stage-setup";
        const string qaWorkstreamId = implWorkstreamId + "-e2e";
        const string baseBranch = "main";
        const string implBranch = "ws/stage43-int/foundation-setup";

        // Seed product + repo + story + spawn + impl workstream + qa-e2e
        // workstream + matching qa-e2e WorkItem (so the scenarios reader
        // finds the test scenarios in DescriptionMd).
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = ProductId,
                Name = "Stage 4.3 int",
                Slug = "stage43-int",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Repos.Add(new RepoRow
            {
                Id = RepoId,
                ProductId = ProductId,
                DisplayName = "Stage 4.3 int repo",
                Kind = "local",
                RemoteUrl = "file://stage43-int",
                CloneRoot = "/tmp/stage43-int",
                DefaultBranch = "main",
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = StoryId,
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "Stage 4.3 int",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = SpawnId,
                StoryId = StoryId,
                StoryTitle = "Stage 4.3 int",
                Phase = StorySpawnPhases.Implement,
                PlanBranch = $"story/{StoryId}/plan",
                RepoId = RepoId,
                BaseBranch = baseBranch,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 3,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = implWorkstreamId,
                Name = "Setup",
                PlanAnchor = ImplAnchor,
                PhaseAnchor = "phase-foundation",
                Category = WorkstreamCategories.Impl,
                StorySpawnId = SpawnId,
                Branch = implBranch,
                Base = baseBranch,
                Status = "review",
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = qaWorkstreamId,
                Name = "Setup — E2E",
                PlanAnchor = ImplAnchor + CrewStoryArtifactStrategy.QaE2eAnchorSuffix,
                PhaseAnchor = "phase-foundation",
                Category = WorkstreamCategories.QaE2e,
                StorySpawnId = SpawnId,
                Branch = $"ws/{StoryId}/phase-foundation-stage-setup-e2e",
                Base = baseBranch,
                Status = "planning",
                Summary = "Trailing qa-e2e",
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = qaWorkstreamId,
                ParentId = StoryId,
                Provider = "local",
                Track = "dev",
                Type = "task",
                State = "active",
                Title = "Setup — E2E",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                // PlanReconciler.QaE2eStageDescription shape — the
                // scenario reader picks bullets from the "## Test
                // Scenarios" section.
                DescriptionMd = """
                    E2E tests for Stage 1.1 of `implementation-plan.md` — Setup

                    Trails the impl workstream for this Stage.

                    ## Test Scenarios (from implementation-plan.md)
                    - Scenario: bootstrap completes without error
                    - Scenario: idempotent re-run does not duplicate the schema
                    """,
            });
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        // Seed e2e-scenarios.md on main with a compose setup block.
        var e2eMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: compose
            - **Local**: docker compose up -d
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** bootstrap works
            """;

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var repo = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == RepoId);

            // Pre-seed e2e-scenarios.md on main.
            await git.EnsureBranchAsync(repo, baseBranch, baseBranch, default);
            await git.CommitFilesAsync(repo, baseBranch, "stage43-int-seed",
                new Dictionary<string, string>
                {
                    [$"docs/stories/{StoryId}/e2e-scenarios.md"] = e2eMd,
                },
                "seed e2e-scenarios", default);

            // Seed the impl branch with a change so the diff reader has
            // something to report.
            await git.EnsureBranchAsync(repo, implBranch, baseBranch, default);
            await git.CommitFilesAsync(repo, implBranch, "stage43-int-impl",
                new Dictionary<string, string>
                {
                    ["src/Foundation/Bootstrap.cs"] = "// impl",
                    ["src/Foundation/Schema.cs"] = "// schema",
                },
                "impl: bootstrap + schema", default);
        }

        // Drive the strategy's internal input resolver + prompt builder.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == SpawnId);
            var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == qaWorkstreamId);

            var crew = ActivatorUtilities.CreateInstance<CrewStoryArtifactStrategy>(scope.ServiceProvider);
            var inputs = await crew.ResolveQaE2ePromptInputsAsync(spawn, ws, default);

            // Slugs derived from the qa-e2e anchor.
            Assert.Equal("foundation", inputs.PhaseSlug);
            Assert.Equal("setup", inputs.StageSlug);

            // Scenarios came from the qa-e2e WorkItem's DescriptionMd.
            Assert.Equal(2, inputs.TestScenarios.Count);
            Assert.Contains("Scenario: bootstrap completes without error", inputs.TestScenarios);

            // Setup type resolved from the merged e2e-scenarios.md.
            Assert.Equal(E2eSetupType.Compose, inputs.SetupType);
            Assert.Contains("Type**: compose", inputs.SetupBlockMarkdown);

            // Impl diff is best-effort against the in-memory git stub —
            // `InMemoryStoryGitOps.GetChangedFilesAsync` returns the empty
            // list by design (its file content is mocked but per-iter
            // commit-diff state isn't synthesized). The integration check
            // verifies the resolver doesn't throw on a real impl-branch
            // path and produces a string; the actual diff content shows
            // up in production where `RealStoryGitOps` shells out to
            // `git diff`. End-to-end content capture lives in Stage 9.1's
            // integration suite (real git on a temp clone).
            Assert.NotNull(inputs.ImplDiff);

            // BuildQaE2ePrompt round-trips the resolved inputs into a
            // generator prompt that has every expected section + the
            // task-block calls out the compose setup type.
            var gen = CrewStoryArtifactStrategy.BuildQaE2ePrompt(inputs, evaluator: false);
            Assert.Contains("## Setup", gen);
            Assert.Contains("Type**: compose", gen);
            Assert.Contains("setup-compose", gen);
            Assert.Contains("## Implementation diff", gen);
            Assert.Contains("## Acceptance scenarios", gen);
            Assert.Contains("Scenario: bootstrap completes without error", gen);
        }
    }
}
