// -----------------------------------------------------------------------
// <copyright file="Phase46PlanFormatTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// forge_implementation.md spec Phase 46 — implementation-plan layout v2.
/// Pure-helper tests covering the parser, the deps resolver, and the YAML
/// round-trip helper. No DB, no HTTP, no SignalR — these run fast in CI.
/// </summary>
public class Phase46PlanFormatTests
{
    // --- v2 parsing ------------------------------------------------------------

    [Fact]
    public void Parser_V2_Detects_Format_From_First_H1_Phase()
    {
        var md = """
            # Phase 1: Setup

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: First

            ### Implementation Steps
            - [ ] do thing

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing happens
            """;

        var plan = PlanParser.Parse(md);

        Assert.Equal(PlanParser.PlanFormat.V2, plan.Format);
        Assert.Single(plan.Phases);
        Assert.Single(plan.Phases[0].Stages);
        Assert.True(plan.Phases[0].IsStartPhase);
        Assert.True(plan.Phases[0].Stages[0].IsStartStage);
        Assert.Empty(plan.ParseErrors);
    }

    [Fact]
    public void Parser_V2_Reads_Front_Matter()
    {
        var md = """
            ---
            title: "Agent Memory"
            storyId: WI-50001
            owner: "lingxd"
            ---

            # Phase 1: Setup

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: First

            ### Implementation Steps
            - [ ] do thing

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing happens
            """;

        var plan = PlanParser.Parse(md);

        Assert.Equal("Agent Memory", plan.FrontMatter["title"]);
        Assert.Equal("WI-50001", plan.FrontMatter["storyId"]);
        Assert.Equal("lingxd", plan.FrontMatter["owner"]);
    }

    [Fact]
    public void Parser_V2_Captures_Phase_And_Stage_Dependencies()
    {
        var md = """
            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Setup

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] verifies setup

            # Phase 2: Build

            ## Dependencies
            - phase-foundation

            ## Stage 2.1: Compile

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-foundation/stage-setup

            ### Test Scenarios
            - [ ] compiles
            """;

        var plan = PlanParser.Parse(md);

        Assert.Equal(2, plan.Phases.Count);
        Assert.Single(plan.Phases[1].Dependencies);
        Assert.Equal("phase-foundation", plan.Phases[1].Dependencies[0]);
        Assert.False(plan.Phases[1].IsStartPhase);
        Assert.Single(plan.Phases[1].Stages[0].Dependencies);
        Assert.Equal("phase-foundation/stage-setup", plan.Phases[1].Stages[0].Dependencies[0]);
        Assert.False(plan.Phases[1].Stages[0].IsStartStage);
    }

    [Fact]
    public void Parser_V2_Subsection_Order_Violation_Records_Error()
    {
        // Test Scenarios appears BEFORE Dependencies — out of order.
        var md = """
            # Phase 1: Setup

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: First

            ### Implementation Steps
            - [ ] step

            ### Test Scenarios
            - [ ] thing

            ### Dependencies
            - _none — start stage_
            """;

        var plan = PlanParser.Parse(md);

        Assert.Contains(PlanParser.ErrorCodes.StageSubsectionOrderInvalid, plan.ParseErrors);
    }

    [Fact]
    public void Parser_V2_Duplicate_Stage_Title_Records_Error()
    {
        var md = """
            # Phase 1: A

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Setup

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing

            # Phase 2: B

            ## Dependencies
            - _none — start phase_

            ## Stage 2.1: Setup

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);

        Assert.Contains(PlanParser.ErrorCodes.DuplicateStageTitle, plan.ParseErrors);
    }

    [Fact]
    public void Parser_V2_ContentHash_Stable_When_Plan_Unchanged()
    {
        var md = """
            # Phase 1: Setup

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: First

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-setup

            ### Test Scenarios
            - [ ] thing
            """;

        var first = PlanParser.Parse(md);
        var second = PlanParser.Parse(md);

        Assert.Equal(first.Phases[0].ContentHash, second.Phases[0].ContentHash);
        Assert.Equal(first.Phases[0].Stages[0].ContentHash, second.Phases[0].Stages[0].ContentHash);
    }

    [Fact]
    public void Parser_V2_ContentHash_Changes_When_Stage_Deps_Change()
    {
        var withDep = """
            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: First

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-foundation/stage-other

            ### Test Scenarios
            - [ ] thing
            """;

        var noDep = """
            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: First

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing
            """;

        var a = PlanParser.Parse(withDep);
        var b = PlanParser.Parse(noDep);

        Assert.NotEqual(a.Phases[0].Stages[0].ContentHash, b.Phases[0].Stages[0].ContentHash);
    }

    // --- v1 back-compat --------------------------------------------------------

    [Fact]
    public void Parser_V1_Legacy_Still_Parses()
    {
        var md = """
            # Implementation Plan — Foo

            ## Phase 1: Setup

            ### Stage 1.1: First

            #### Implementation Steps
            - [ ] step

            #### Test Scenarios
            - [ ] thing happens
            """;

        var plan = PlanParser.Parse(md);

        Assert.Equal(PlanParser.PlanFormat.V1Legacy, plan.Format);
        Assert.Single(plan.Phases);
        Assert.Single(plan.Phases[0].Stages);
        Assert.Empty(plan.Phases[0].Dependencies);
        Assert.Empty(plan.Phases[0].Stages[0].Dependencies);
    }

    [Fact]
    public void Parser_V1_ContentHash_Stable_Against_Pre_Phase46_Format()
    {
        // A v1 doc with no Dependencies and no start markers must hash the
        // same as it would have under the pre-Phase-46 parser, otherwise
        // the reconciler would treat every legacy stage as "scope edited"
        // on first post-upgrade pass.
        var md = """
            ## Phase 1: Setup

            ### Stage 1.1: First

            #### Implementation Steps
            - [ ] step1
            - [ ] step2

            #### Test Scenarios
            - [ ] sc1
            - [ ] sc2
            """;

        var plan = PlanParser.Parse(md);

        // Legacy stage-body key shape: stage|N.M|name + step-hashes + scenarios.
        // No deps / start markers because the v1 doc carries neither.
        var expectedStageBody = string.Join("\n", new[]
        {
            "stage|1.1|First",
            string.Join("\n", plan.Phases[0].Stages[0].Steps.Select(s => s.ContentHash)),
            "sc1\nsc2",
        });
        var expectedHash = PlanParser.HashOf(expectedStageBody);

        Assert.Equal(expectedHash, plan.Phases[0].Stages[0].ContentHash);
    }

    [Fact]
    public void Parser_Empty_Or_Unknown_Format_Returns_Empty()
    {
        var none = PlanParser.Parse("");
        Assert.Empty(none.Phases);

        var headlines = PlanParser.Parse("# Heading\n\nSome prose.");
        Assert.Equal(PlanParser.PlanFormat.Unknown, headlines.Format);
        Assert.Contains(PlanParser.ErrorCodes.FormatUnknown, headlines.ParseErrors);
    }

    // --- DecompositionDepsResolver --------------------------------------------

    [Fact]
    public void Resolver_Empty_Plan_Returns_Empty()
    {
        var plan = new PlanParser.CanonicalPlan(
            Array.Empty<PlanParser.CanonicalPhase>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            PlanParser.PlanFormat.Unknown);

        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Resolver_Explicit_Stage_Dep_By_Anchor_Resolves()
    {
        var md = """
            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Setup

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing

            ## Stage 1.2: Wire

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-foundation/stage-setup

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var wireStage = plan.Phases[0].Stages[1];
        var wireDeps = resolved[wireStage.Anchor];
        Assert.Single(wireDeps.Deps);
        Assert.Equal("Setup", wireDeps.Deps[0]);
        Assert.Empty(wireDeps.UnresolvedDeps);
    }

    [Fact]
    public void Resolver_Explicit_Stage_Dep_By_Title_Resolves()
    {
        var md = """
            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Setup

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing

            ## Stage 1.2: Wire

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - Setup

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var wireStage = plan.Phases[0].Stages[1];
        var wireDeps = resolved[wireStage.Anchor];
        Assert.Equal(new[] { "Setup" }, wireDeps.Deps);
        Assert.Empty(wireDeps.UnresolvedDeps);
    }

    [Fact]
    public void Resolver_Phase_Expansion_Adds_Cross_Product_Edges()
    {
        // Phase B has phase-dep on Phase A. Every stage in B must depend
        // on every stage in A (Q46.2 phase-expansion).
        var md = """
            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: A1

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing

            ## Stage 1.2: A2

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing

            # Phase 2: Downstream

            ## Dependencies
            - phase-foundation

            ## Stage 2.1: B1

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var b1Stage = plan.Phases[1].Stages[0];
        var b1Deps = resolved[b1Stage.Anchor];
        Assert.Contains("A1", b1Deps.Deps);
        Assert.Contains("A2", b1Deps.Deps);
    }

    [Fact]
    public void Resolver_Unresolved_Phase_Dep_Is_Surfaced()
    {
        var md = """
            # Phase 1: Foundation

            ## Dependencies
            - phase-does-not-exist

            ## Stage 1.1: First

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var firstDeps = resolved[plan.Phases[0].Stages[0].Anchor];
        Assert.Contains("phase-does-not-exist", firstDeps.UnresolvedDeps);
    }

    [Fact]
    public void Resolver_Unresolved_Stage_Dep_Is_Surfaced()
    {
        var md = """
            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: First

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-foundation/stage-typo

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var firstDeps = resolved[plan.Phases[0].Stages[0].Anchor];
        Assert.Contains("phase-foundation/stage-typo", firstDeps.UnresolvedDeps);
    }

    [Fact]
    public void Resolver_Cycle_Throws_Plan_Dep_Cycle()
    {
        // a depends on b; b depends on a → cycle.
        var md = """
            # Phase 1: P

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: A

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-p/stage-b

            ### Test Scenarios
            - [ ] thing

            ## Stage 1.2: B

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-p/stage-a

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DecompositionDepsResolver.ResolveAll(plan));
        Assert.Contains("plan.dep_cycle", ex.Message);
    }

    [Fact]
    public void Resolver_Self_Edge_Throws_As_Cycle()
    {
        // A self-stage-dep is a degenerate cycle. Q46.2 says throw, not
        // silently filter.
        var md = """
            # Phase 1: P

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Solo

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-p/stage-solo

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DecompositionDepsResolver.ResolveAll(plan));
        Assert.Contains("plan.dep_cycle", ex.Message);
    }

    [Fact]
    public void Resolver_Explicit_Cross_Phase_Edge_Coexists_With_Phase_Expansion()
    {
        // B's phase-dep on A produces cross-product edges, AND b1's
        // explicit stage-dep on a1 is preserved (Q46.5 test scenario).
        var md = """
            # Phase 1: A

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: A1

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing

            ## Stage 1.2: A2

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] thing

            # Phase 2: B

            ## Dependencies
            - phase-a

            ## Stage 2.1: B1

            ### Implementation Steps
            - [ ] step

            ### Dependencies
            - phase-a/stage-a1

            ### Test Scenarios
            - [ ] thing
            """;

        var plan = PlanParser.Parse(md);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);

        var b1Deps = resolved[plan.Phases[1].Stages[0].Anchor];
        // Both edges should be present (union semantics).
        Assert.Contains("A1", b1Deps.Deps);
        Assert.Contains("A2", b1Deps.Deps);
        // Set semantics — deduplicated.
        Assert.Equal(2, b1Deps.Deps.Count);
    }

    // --- DecompositionYamlSerializer ------------------------------------------

    [Fact]
    public void Yaml_RoundTrip_Preserves_Fields()
    {
        var rows = new List<DecompositionYamlRow>
        {
            new()
            {
                Title = "Setup",
                Description = "kick-off",
                AcceptanceCriteria = new List<string> { "ok" },
                Deps = new List<string>(),
                Deliverables = new List<string> { "foo.cs" },
                Timeline = "day 1",
                EstimatedEffort = "S",
            },
        };

        var yaml = DecompositionYamlSerializer.Serialize(rows);
        var parsed = DecompositionYamlSerializer.Deserialize(yaml);

        Assert.NotNull(parsed);
        Assert.Single(parsed!);
        Assert.Equal("Setup", parsed[0].Title);
        Assert.Equal("kick-off", parsed[0].Description);
        Assert.Equal(new[] { "ok" }, parsed[0].AcceptanceCriteria);
        Assert.Equal(new[] { "foo.cs" }, parsed[0].Deliverables);
        Assert.Equal("day 1", parsed[0].Timeline);
        Assert.Equal("S", parsed[0].EstimatedEffort);
    }

    [Fact]
    public void Yaml_Deserialize_Empty_Returns_Null()
    {
        Assert.Null(DecompositionYamlSerializer.Deserialize(null));
        Assert.Null(DecompositionYamlSerializer.Deserialize(""));
        Assert.Null(DecompositionYamlSerializer.Deserialize("   \n  \n"));
    }

    [Fact]
    public void Yaml_Serialize_Uses_CamelCase_Keys()
    {
        var rows = new List<DecompositionYamlRow>
        {
            new()
            {
                Title = "T",
                AcceptanceCriteria = new List<string> { "x" },
                EstimatedEffort = "M",
            },
        };

        var yaml = DecompositionYamlSerializer.Serialize(rows);

        Assert.Contains("title:", yaml);
        Assert.Contains("acceptanceCriteria:", yaml);
        Assert.Contains("estimatedEffort:", yaml);
        Assert.DoesNotContain("AcceptanceCriteria:", yaml);
        Assert.DoesNotContain("EstimatedEffort:", yaml);
    }
}
