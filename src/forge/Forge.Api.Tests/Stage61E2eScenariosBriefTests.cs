// -----------------------------------------------------------------------
// <copyright file="Stage61E2eScenariosBriefTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 6.1 — the architect's
/// <c>e2e-scenarios.md</c> brief in
/// <see cref="CrewStoryArtifactStrategy.BuildPlanDocSpecs"/> teaches
/// the closed-set <c>### Setup</c> contract: one of
/// <c>inline | compose | lab-wsfc | lab-bare-metal</c>, plus the
/// `Local` / `CI runner` / `Secrets` / `Pre-test bootstrap` bullets,
/// plus the lab-secret + env-var rules. Stage 6.2 (next) wires the
/// evaluator linter against this contract; Stage 6.3 threads the
/// parsed setup block into the qa-e2e prompt. Stage 6.1 just pins
/// the prompt shape so the architect crew produces conformant output
/// the linter can verify.
///
/// <para>Both Facts are pure-reflection over the
/// <see cref="CrewStoryArtifactStrategy.BuildPlanDocSpecs"/> dictionary
/// — no boot, no DB, no SignalR.</para>
/// </summary>
public class Stage61E2eScenariosBriefTests
{
    private static string Brief() =>
        CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/x")["e2e-scenarios"].Brief;

    // ── 1. [unit] Brief carries the four required H3 sub-bullets ─────────

    [Fact]
    public void E2eScenarios_Brief_Mandates_Setup_Block_With_Required_Bullets()
    {
        var brief = Brief();

        // The brief states the H3 is mandatory and shows the four
        // canonical bullets the operator + architect agree on.
        Assert.Contains("### Setup", brief);
        Assert.Contains("**Type**:", brief);
        Assert.Contains("**Local**:", brief);
        Assert.Contains("**CI runner**:", brief);
        Assert.Contains("**Secrets**:", brief);
        Assert.Contains("**Pre-test bootstrap**:", brief);

        // GROUPED BY PHASE — the parser walks `# Phase N: ...` H1s; the
        // brief must teach that grouping explicitly so the architect
        // doesn't emit one flat Setup block at the top of the doc.
        Assert.Contains("GROUPED BY PHASE", brief);
        Assert.Contains("# Phase N:", brief);

        // The four recognised setup types — the closed set the Stage 6.2
        // linter and the Stage 4.2 deterministic emitter both compare
        // against.
        Assert.Contains("inline", brief);
        Assert.Contains("compose", brief);
        Assert.Contains("lab-wsfc", brief);
        Assert.Contains("lab-bare-metal", brief);

        // The four "evaluator refuses pass otherwise" hard requirements
        // — the brief makes them explicit so the architect's first-pass
        // doc is conformant.
        Assert.Contains("Hard requirements", brief);
        Assert.Contains("`### Setup` H3 directly before `### Scenarios`", brief);
        Assert.Contains("closed set", brief);
        Assert.Contains("KeyVault entry", brief);
        Assert.Contains("GitHub environment", brief);
        Assert.Contains("env-var names, never literal", brief);

        // Lab-specific guidance: ADO agent pool defaults, GH self-hosted
        // labels, Key Vault + variable group names. The architect's
        // lab-phase emission must name all four.
        Assert.Contains("forge-lab-wsfc", brief);
        Assert.Contains("forge-lab-hw", brief);
        Assert.Contains("Azure Key Vault name and variable group", brief);
        Assert.Contains("GitHub environment name and secret names", brief);
        Assert.Contains("docker-compose.yml", brief);
    }

    // ── 2. [unit] Resume + full-run paths share the same brief source ────

    [Fact]
    public void E2eScenarios_Brief_Is_Single_Source_For_Resume_And_FullRun()
    {
        // Mirror the regression-pin from PlanDocBriefConsistencyTests for
        // the implementation-plan doc: two calls with the same docDir
        // must produce byte-identical specs. The Stage 6.1 expansion
        // pushed enough characters into the brief that any future
        // refactor that re-introduces a literal copy in
        // ResumeSingleArtifactAsync (the 2026-05-04 bug) would diverge
        // here.
        var a = CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/x")["e2e-scenarios"];
        var b = CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/x")["e2e-scenarios"];

        Assert.Equal(a.FileName, b.FileName);
        Assert.Equal(a.RepoPath, b.RepoPath);
        Assert.Equal(a.Brief, b.Brief);

        // Spec must be wired so the docDir flows into the repo path
        // verbatim — the orchestrator commits to the path the spec
        // returns, no hardcoded folder.
        var spec = CrewStoryArtifactStrategy.BuildPlanDocSpecs("docs/stories/code-intelligence-AGENT-MEMORY")["e2e-scenarios"];
        Assert.Equal("docs/stories/code-intelligence-AGENT-MEMORY/e2e-scenarios.md", spec.RepoPath);
        Assert.Equal("e2e-scenarios.md", spec.FileName);

        // The brief is substantive — the one-line pre-Stage-6.1 version
        // was ~140 chars; the expanded brief is >1500 so the architect
        // has the full closed-set contract in the prompt. Pinning a
        // lower bound here catches an accidental truncation.
        Assert.True(spec.Brief.Length > 1500,
            $"e2e-scenarios brief is only {spec.Brief.Length} chars; the Stage 6.1 "
            + "expanded brief should be >1500. A truncation regressed the contract — "
            + "the architect won't get the closed-set Setup instructions.");
    }
}
