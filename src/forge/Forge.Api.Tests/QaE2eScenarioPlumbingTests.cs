// -----------------------------------------------------------------------
// <copyright file="QaE2eScenarioPlumbingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Regression tests for the qa-e2e scenario plumbing that stalled
/// <c>local:REFACTOR-INTEGRATION</c> repeatedly (2026-05-20). The qa-e2e
/// producer (<c>CrewStoryArtifactStrategy.ReadQaE2eTestScenariosAsync</c>)
/// recovers a stage's acceptance scenarios from a <c>## Test Scenarios</c>
/// section of the qa-e2e <c>WorkItem.DescriptionMd</c>. Two independent code
/// paths used to strip that section, so the producer read zero scenarios and
/// stalled every qa-e2e workstream with "zero acceptance test scenarios":
///
/// <list type="number">
/// <item><c>StorySpawnOrchestrator.PersistDecompositionAsync</c> wrote
/// <c>DescriptionMd</c> from the decomposer's one-line summary, dropping
/// <c>DecompositionWorkstream.AcceptanceCriteria</c>. Fixed via
/// <see cref="StorySpawnOrchestrator.ComposeWorkItemDescriptionMd"/>.</item>
/// <item>The <c>POST /api/workstreams/{id}/restart</c> endpoint refreshed
/// <c>DescriptionMd</c> from the parent story's description (which carries no
/// scenarios), re-stalling a row the moment it was restarted. Fixed by
/// exempting qa-e2e rows from that refresh.</item>
/// </list>
/// </summary>
public class QaE2eScenarioPlumbingTests
{
    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    // ── 1. [unit] qa-e2e DescriptionMd folds the scenarios in ────────────

    [Fact]
    public void ComposeWorkItemDescriptionMd_QaE2e_FoldsScenariosIntoSection()
    {
        var scenarios = new[]
        {
            "Scenario: PortAllocator uniqueness -- Given a PortAllocator, When AllocateBaseUri x10, Then ports unique",
            "Scenario: URI format -- Given a PortAllocator, When AllocateBaseUri(localhost), Then matches http://localhost:{port}/",
        };
        var w = new DecompositionWorkstream(
            Title: "Base Classes — E2E",
            Description: "E2E tests for Base Classes: Gherkin + step defs + e2e run covering 2 acceptance scenarios.",
            AcceptanceCriteria: scenarios,
            Deps: new[] { "Base Classes" },
            Deliverables: System.Array.Empty<string>(),
            Timeline: string.Empty,
            EstimatedEffort: string.Empty,
            Category: WorkstreamCategories.QaE2e);

        var md = StorySpawnOrchestrator.ComposeWorkItemDescriptionMd(w);

        // The summary lead survives, and the parseable section + bullets are appended.
        Assert.Contains("covering 2 acceptance scenarios", md);
        Assert.Contains("## Test Scenarios", md);
        foreach (var s in scenarios)
        {
            Assert.Contains("- " + s, md);
        }

        // And the producer's exact reader recovers >= 1 scenario from it.
        Assert.Equal(scenarios.Length, CountTestScenarioBullets(md));
    }

    [Fact]
    public void ComposeWorkItemDescriptionMd_Impl_LeavesDescriptionUntouched()
    {
        var w = new DecompositionWorkstream(
            Title: "Base Classes",
            Description: "Create directory structure and base classes.",
            AcceptanceCriteria: new[] { "should not leak into impl description" },
            Deps: System.Array.Empty<string>(),
            Deliverables: System.Array.Empty<string>(),
            Timeline: string.Empty,
            EstimatedEffort: string.Empty,
            Category: WorkstreamCategories.Impl);

        var md = StorySpawnOrchestrator.ComposeWorkItemDescriptionMd(w);

        Assert.Equal("Create directory structure and base classes.", md);
        Assert.DoesNotContain("## Test Scenarios", md);
    }

    [Fact]
    public void ComposeWorkItemDescriptionMd_QaE2eWithNoScenarios_OmitsSection()
    {
        var w = new DecompositionWorkstream(
            Title: "Base Classes — E2E",
            Description: "E2E summary.",
            AcceptanceCriteria: System.Array.Empty<string>(),
            Deps: System.Array.Empty<string>(),
            Deliverables: System.Array.Empty<string>(),
            Timeline: string.Empty,
            EstimatedEffort: string.Empty,
            Category: WorkstreamCategories.QaE2e);

        var md = StorySpawnOrchestrator.ComposeWorkItemDescriptionMd(w);

        Assert.Equal("E2E summary.", md);
        Assert.DoesNotContain("## Test Scenarios", md);
    }

    // ── 2. [integration] restart preserves qa-e2e scenarios ──────────────

    [Fact]
    public async Task Restart_QaE2e_PreservesScenarios_ButRefreshesImpl()
    {
        using var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string storyId = "WI-qae2e-restart";
        const string spawnId = "spawn-" + storyId;
        const string qaWsId = "ws-qae2e-restart-stage-x-e2e";
        const string implWsId = "ws-qae2e-restart-stage-x";
        const string storyDesc = "Refactor integration tests. (story body — no test scenarios here.)";
        const string qaDesc =
            "E2E tests for Stage 1.1 — Base Classes\n\n"
            + "## Test Scenarios (from implementation-plan.md)\n"
            + "- Scenario: PortAllocator uniqueness -- ports are unique\n"
            + "- Scenario: URI format -- matches http://localhost:{port}/\n";

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            db.ProductStories.Add(new ProductStoryRow
            {
                Id = storyId,
                ProductId = "prod-x",
                Name = "Refactor integration tests",
                Description = storyDesc,
                CreatedAt = System.DateTimeOffset.UtcNow,
            });

            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = "Refactor integration tests",
                Phase = StorySpawnPhases.Implement,
                RepoId = "repo-x",
                CreatedAt = System.DateTimeOffset.UtcNow,
                UpdatedAt = System.DateTimeOffset.UtcNow,
                StoryPoints = 3,
            });

            // No RepoRow seeded → the restart endpoint's git-branch cleanup
            // (which needs an existing CloneRoot) is skipped, keeping the
            // test hermetic.
            db.Workstreams.Add(MakeWorkstream(qaWsId, spawnId, "qa-e2e WS", WorkstreamCategories.QaE2e));
            db.Workstreams.Add(MakeWorkstream(implWsId, spawnId, "impl WS", WorkstreamCategories.Impl));
            db.WorkItems.Add(MakeWorkItem(qaWsId, "qa-e2e WS", qaDesc));
            db.WorkItems.Add(MakeWorkItem(implWsId, "impl WS", "stale impl description"));

            await db.SaveChangesAsync();
        }

        var qaResp = await c.PostAsync($"/api/workstreams/{qaWsId}/restart", content: null);
        Assert.True(qaResp.IsSuccessStatusCode,
            $"qa-e2e restart failed: {(int)qaResp.StatusCode} {await qaResp.Content.ReadAsStringAsync()}");

        var implResp = await c.PostAsync($"/api/workstreams/{implWsId}/restart", content: null);
        Assert.True(implResp.IsSuccessStatusCode,
            $"impl restart failed: {(int)implResp.StatusCode} {await implResp.Content.ReadAsStringAsync()}");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var qa = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == qaWsId);
            var impl = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == implWsId);

            // qa-e2e DescriptionMd must be preserved verbatim — restart must
            // NOT clobber the scenarios with the story description.
            Assert.Equal(qaDesc, qa.DescriptionMd);
            Assert.Contains("## Test Scenarios", qa.DescriptionMd);
            Assert.Equal(2, CountTestScenarioBullets(qa.DescriptionMd));

            // impl rows keep the documented behaviour: DescriptionMd is
            // refreshed from the current story description on restart.
            Assert.Equal(storyDesc, impl.DescriptionMd);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Replicates the parse in
    /// <c>CrewStoryArtifactStrategy.ReadQaE2eTestScenariosAsync</c> so the
    /// assertion exercises the same contract the producer relies on.</summary>
    private static int CountTestScenarioBullets(string? desc)
    {
        var n = 0;
        var inSection = false;
        foreach (var raw in (desc ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## Test Scenarios", System.StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }
            if (inSection)
            {
                if (line.StartsWith("## ", System.StringComparison.Ordinal)) break;
                if (line.TrimStart().StartsWith("- ", System.StringComparison.Ordinal)) n++;
            }
        }
        return n;
    }

    private static WorkstreamRow MakeWorkstream(string id, string spawnId, string name, string category) => new()
    {
        Id = id,
        Sort = 0,
        RepoId = "repo-x",
        Name = name,
        Branch = string.Empty, // empty → restart skips git-branch cleanup
        Base = "main",
        Epic = "WI-qae2e-restart",
        Status = "stalled",
        WorktreeJson = "[]",
        AgentsJson = "[]",
        PrsJson = "[]",
        CardsJson = "[]",
        Summary = name,
        StorySpawnId = spawnId,
        Category = category,
    };

    private static WorkItemRow MakeWorkItem(string id, string title, string desc) => new()
    {
        Id = id,
        Provider = "local",
        Track = "dev",
        Type = "task",
        State = "active",
        Title = title,
        DescriptionMd = desc,
        Points = 3,
        Priority = "med",
        ExecutionStatus = ExecutionStatuses.Idle,
        CreatedAt = System.DateTimeOffset.UtcNow,
        UpdatedAt = System.DateTimeOffset.UtcNow,
    };
}
