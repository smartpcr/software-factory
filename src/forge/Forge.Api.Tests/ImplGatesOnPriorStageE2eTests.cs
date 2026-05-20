// -----------------------------------------------------------------------
// <copyright file="ImplGatesOnPriorStageE2eTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// docs/forge/e2e.md — operator gate `Forge:Stories:GateImplOnPriorStageE2e`
/// (default on). When set, an impl stage that depends on an upstream stage
/// which mints a trailing qa-e2e companion is rewritten to depend on that
/// companion (<c>"{D} — E2E"</c>) instead of the bare impl title, so the
/// dependent stage waits for the upstream stage's impl AND its e2e — not
/// just the upstream impl PR.
///
/// <para>Pure-helper tests against
/// <see cref="DecompositionDepsResolver.GateImplDepsOnPriorStageE2e"/> +
/// <see cref="CrewStoryArtifactStrategy.BuildDecompositionFromPlan"/> so they
/// run in the fast-CI bucket without booting the host.</para>
/// </summary>
public class ImplGatesOnPriorStageE2eTests
{
    // Foundation phase with four stages:
    //   1.1 Setup     — has scenarios → mints "Setup — E2E"
    //   1.2 Endpoints — deps on Setup, has scenarios
    //   1.3 Docs      — NO scenarios → no e2e companion
    //   1.4 Publish   — deps on Docs, has scenarios
    private const string PlanMd = """
        ---
        title: "Impl-gates fixture"
        storyId: "WI-impl-gate"
        ---

        # Phase 1: Foundation

        ## Dependencies
        - _none — start phase_

        ## Stage 1.1: Setup

        ### Implementation Steps
        - [ ] scaffold

        ### Dependencies
        - _none — start stage_

        ### Test Scenarios
        - [ ] boots clean

        ## Stage 1.2: Endpoints

        ### Implementation Steps
        - [ ] add routes

        ### Dependencies
        - phase-foundation/stage-setup

        ### Test Scenarios
        - [ ] route returns 200

        ## Stage 1.3: Docs

        ### Implementation Steps
        - [ ] write README

        ### Dependencies
        - _none — start stage_

        ### Test Scenarios

        ## Stage 1.4: Publish

        ### Implementation Steps
        - [ ] publish package

        ### Dependencies
        - phase-foundation/stage-docs

        ### Test Scenarios
        - [ ] package resolves
        """;

    private static (PlanParser.CanonicalPlan Plan,
        System.Collections.Generic.IReadOnlyDictionary<string, DecompositionDepsResolver.ResolvedStageDeps> Resolved)
        ParseAndResolve()
    {
        var plan = PlanParser.Parse(PlanMd);
        Assert.Empty(plan.ParseErrors);
        return (plan, DecompositionDepsResolver.ResolveAll(plan));
    }

    private static DecompositionWorkstream ImplRow(
        System.Collections.Generic.IReadOnlyList<DecompositionWorkstream> ws, string title) =>
        Assert.Single(ws.Where(w => w.Category == WorkstreamCategories.Impl && w.Title == title));

    // ── 1. Gate on: impl dep on an e2e-having upstream is rewritten ──────

    [Fact]
    public void GateOn_Rewrites_ImplDep_To_PriorStageE2e_When_Upstream_Has_Scenarios()
    {
        var (plan, resolved) = ParseAndResolve();
        var gated = DecompositionDepsResolver.GateImplDepsOnPriorStageE2e(plan, resolved);
        var ws = CrewStoryArtifactStrategy.BuildDecompositionFromPlan(plan, gated);

        var endpoints = ImplRow(ws, "Endpoints");

        // Endpoints depended on "Setup" (impl); Setup mints a qa-e2e, so the
        // dep is rewritten to the companion's title.
        Assert.Equal(new[] { "Setup — E2E" }, endpoints.Deps);

        // Drift guard: the rewritten dep must equal the actual qa-e2e row's
        // Title minted by BuildDecompositionFromPlan in the same output.
        var setupQa = Assert.Single(ws.Where(w =>
            w.Category == WorkstreamCategories.QaE2e && w.Title == "Setup — E2E"));
        Assert.Equal(setupQa.Title, endpoints.Deps[0]);
    }

    // ── 2. Gate on: dep on an upstream WITHOUT scenarios is left alone ───

    [Fact]
    public void GateOn_Leaves_ImplDep_Unchanged_When_Upstream_Has_No_Scenarios()
    {
        var (plan, resolved) = ParseAndResolve();
        var gated = DecompositionDepsResolver.GateImplDepsOnPriorStageE2e(plan, resolved);
        var ws = CrewStoryArtifactStrategy.BuildDecompositionFromPlan(plan, gated);

        // Publish depends on "Docs", which has no test scenarios → no qa-e2e
        // companion → the dep stays the bare impl title.
        var publish = ImplRow(ws, "Publish");
        Assert.Equal(new[] { "Docs" }, publish.Deps);
    }

    // ── 3. Gate on: qa-e2e rows' own deps are untouched ─────────────────

    [Fact]
    public void GateOn_Does_Not_Rewrite_QaE2e_Row_Deps()
    {
        var (plan, resolved) = ParseAndResolve();
        var gated = DecompositionDepsResolver.GateImplDepsOnPriorStageE2e(plan, resolved);
        var ws = CrewStoryArtifactStrategy.BuildDecompositionFromPlan(plan, gated);

        // The qa-e2e companion still deps on its IMPL sibling ("Endpoints"),
        // NOT on "Endpoints — E2E" — gating only rewrites impl-row deps.
        var endpointsQa = Assert.Single(ws.Where(w =>
            w.Category == WorkstreamCategories.QaE2e && w.Title == "Endpoints — E2E"));
        Assert.Equal("Endpoints", endpointsQa.Deps[0]);
    }

    // ── 4. Gate off: legacy behaviour — impl deps on the bare impl title ─

    [Fact]
    public void GateOff_Keeps_ImplDep_On_Bare_Upstream_Impl_Title()
    {
        var (plan, resolved) = ParseAndResolve();
        // No post-pass — the default-off path / legacy Phase 58 parallelism.
        var ws = CrewStoryArtifactStrategy.BuildDecompositionFromPlan(plan, resolved);

        var endpoints = ImplRow(ws, "Endpoints");
        Assert.Equal(new[] { "Setup" }, endpoints.Deps);
    }

    // ── 5. No-op when the plan has no qa-e2e companions at all ──────────

    [Fact]
    public void GateOn_Is_NoOp_When_No_Stage_Has_Scenarios()
    {
        const string noScenarioPlan = """
            ---
            title: "No-scenario fixture"
            storyId: "WI-no-scn"
            ---

            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Setup

            ### Implementation Steps
            - [ ] scaffold

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios

            ## Stage 1.2: Endpoints

            ### Implementation Steps
            - [ ] add routes

            ### Dependencies
            - phase-foundation/stage-setup

            ### Test Scenarios
            """;

        var plan = PlanParser.Parse(noScenarioPlan);
        Assert.Empty(plan.ParseErrors);
        var resolved = DecompositionDepsResolver.ResolveAll(plan);
        var gated = DecompositionDepsResolver.GateImplDepsOnPriorStageE2e(plan, resolved);

        // Same instance returned (no e2e companions to gate on).
        Assert.Same(resolved, gated);
    }
}
