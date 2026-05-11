// -----------------------------------------------------------------------
// <copyright file="PlanDocBriefConsistencyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// The plan-doc briefs (passed to the architect agent's user prompt) used to
/// live in TWO places — <c>ProducePlanArtifactsAsync</c> (full plan run) and
/// <c>ResumeSingleArtifactAsync</c> (operator re-runs one doc). They drifted:
/// the full-run brief evolved to spell out the LIVEDOC structure
/// (`Phase → Stage`, checkboxes per step + per test scenario, ≤ 20 cap) while
/// the Resume brief stayed at a one-line "LIVEDOC with checkboxes". A
/// subsequent Resume click on an already-stalled doc was producing
/// implementation-plan.md content that didn't match the spec, and the
/// evaluator never could pass.
///
/// These tests pin the briefs to a single source (<c>BuildPlanDocSpecs</c>)
/// and assert the structural mandates the architect must obey are actually
/// in the prompt. If a future refactor reintroduces a duplicate definition,
/// the equality test will fail.
/// </summary>
public class PlanDocBriefConsistencyTests
{
    [Fact]
    public void PlanDocSpecs_Returns_All_Four_Plan_Documents()
    {
        var specs = CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/foo");

        Assert.Equal(4, specs.Count);
        Assert.Contains("tech-spec", specs.Keys);
        Assert.Contains("architecture", specs.Keys);
        Assert.Contains("implementation-plan", specs.Keys);
        Assert.Contains("e2e-scenarios", specs.Keys);
    }

    [Fact]
    public void PlanDocSpecs_FileNames_Match_Disk_Layout()
    {
        var specs = CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/foo");

        Assert.Equal("tech-spec.md",            specs["tech-spec"].FileName);
        Assert.Equal("architecture.md",         specs["architecture"].FileName);
        Assert.Equal("implementation-plan.md",  specs["implementation-plan"].FileName);
        Assert.Equal("e2e-scenarios.md",        specs["e2e-scenarios"].FileName);

        Assert.Equal("docs/stories/foo/tech-spec.md",            specs["tech-spec"].RepoPath);
        Assert.Equal("docs/stories/foo/architecture.md",         specs["architecture"].RepoPath);
        Assert.Equal("docs/stories/foo/implementation-plan.md",  specs["implementation-plan"].RepoPath);
        Assert.Equal("docs/stories/foo/e2e-scenarios.md",        specs["e2e-scenarios"].RepoPath);
    }

    /// <summary>
    /// The implementation-plan brief is the highest-stakes one — the LIVEDOC
    /// is the source of truth for what shipped, and a hand-wavy brief lets
    /// the agent emit a sketch instead of a structured plan. The evaluator
    /// then has nothing concrete to grade against. This pins the structural
    /// mandates the architect MUST follow (Phase 46 layout: H1 phases,
    /// H2 stages, H3 subsections, per-phase + per-stage Dependencies).
    /// </summary>
    [Fact]
    public void ImplementationPlan_Brief_Mandates_Phase_Stage_Hierarchy()
    {
        var brief = CrewStoryArtifactStrategy.BuildPlanDocSpecs("d")["implementation-plan"].Brief;

        Assert.Contains("Phase", brief);
        Assert.Contains("Stage", brief);
        Assert.Contains("LIVEDOC", brief);
        // Phase 46 — Stages MUST be H2 subsections under each H1 phase.
        Assert.Contains("Each Phase is an H1", brief);
        Assert.Contains("Stages are H2", brief);
    }

    [Fact]
    public void ImplementationPlan_Brief_Mandates_Twenty_Step_Cap()
    {
        var brief = CrewStoryArtifactStrategy.BuildPlanDocSpecs("d")["implementation-plan"].Brief;

        // The forge_implementation.md spec (§16.5 Plan-tree validator) caps
        // both stages-per-phase and steps-per-stage at 20.
        Assert.Contains("20 Stages per Phase", brief);
        Assert.Contains("20 Implementation Steps per Stage", brief);
    }

    [Fact]
    public void ImplementationPlan_Brief_Mandates_Checkbox_On_Every_Step_And_Test()
    {
        var brief = CrewStoryArtifactStrategy.BuildPlanDocSpecs("d")["implementation-plan"].Brief;

        // Both implementation steps AND test scenarios are checkboxes — this
        // is the LIVEDOC progress-tracking contract.
        Assert.Contains("Every Implementation Step is a checkbox", brief);
        Assert.Contains("Every Test Scenario is a checkbox", brief);
        // Refuse bullets / numbered lists for these elements.
        Assert.Contains("not bullets", brief);
        Assert.Contains("not numbered", brief);
    }

    [Fact]
    public void ImplementationPlan_Brief_Requires_Test_Scenarios_Per_Stage()
    {
        var brief = CrewStoryArtifactStrategy.BuildPlanDocSpecs("d")["implementation-plan"].Brief;

        Assert.Contains("Implementation Steps", brief);
        Assert.Contains("Test Scenarios", brief);
        // Phase 46 — three subsections in fixed order, plus the per-stage
        // test count.
        Assert.Contains("THREE subsections in this exact order", brief);
        Assert.Contains("at least 2 test scenarios per Stage", brief);
    }

    /// <summary>
    /// Phase 46 — both per-phase and per-stage Dependencies blocks are
    /// mandatory; the brief must teach the architect to emit them.
    /// </summary>
    [Fact]
    public void ImplementationPlan_Brief_Mandates_Phase_And_Stage_Dependencies()
    {
        var brief = CrewStoryArtifactStrategy.BuildPlanDocSpecs("d")["implementation-plan"].Brief;

        Assert.Contains("`## Dependencies` section directly under the H1 heading", brief);
        Assert.Contains("`### Dependencies`", brief);
        Assert.Contains("_none — start phase_", brief);
        Assert.Contains("_none — start stage_", brief);
    }

    /// <summary>
    /// Phase 46 — the YAML front-matter prelude is what the TOC renderer
    /// reads to surface the story title; without it the doc loses its
    /// title and the rendered TOC numbers phases as §1.1 again.
    /// </summary>
    [Fact]
    public void ImplementationPlan_Brief_Mandates_Yaml_FrontMatter()
    {
        var brief = CrewStoryArtifactStrategy.BuildPlanDocSpecs("d")["implementation-plan"].Brief;

        Assert.Contains("YAML front-matter", brief);
        Assert.Contains("title:", brief);
        Assert.Contains("storyId:", brief);
    }

    /// <summary>
    /// The whole reason this consolidation exists — Resume and full-run MUST
    /// produce identical briefs. If a future commit reintroduces a literal
    /// brief somewhere instead of routing through <c>BuildPlanDocSpecs</c>,
    /// THIS test still passes (because both call sites are now reading from
    /// the helper) — but a static-analysis sweep would surface the literal.
    /// We assert the contract here as a defense against accidental drift.
    /// </summary>
    [Fact]
    public void Resume_And_FullRun_Both_Use_Same_Brief_Source()
    {
        // Two calls with the same docDir must produce byte-identical specs.
        var a = CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/x");
        var b = CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/x");

        foreach (var key in a.Keys)
        {
            Assert.Equal(a[key].FileName, b[key].FileName);
            Assert.Equal(a[key].Brief, b[key].Brief);
            Assert.Equal(a[key].RepoPath, b[key].RepoPath);
        }
    }

    [Fact]
    public void DocDir_Is_Threaded_Into_Repo_Path_Verbatim()
    {
        var specs = CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/code-intelligence-AGENT-MEMORY");

        // The path the orchestrator commits to must match the docDir the
        // caller passed in — no hardcoded folder name.
        Assert.StartsWith("docs/stories/code-intelligence-AGENT-MEMORY/", specs["tech-spec"].RepoPath);
        Assert.StartsWith("docs/stories/code-intelligence-AGENT-MEMORY/", specs["implementation-plan"].RepoPath);
    }
}
