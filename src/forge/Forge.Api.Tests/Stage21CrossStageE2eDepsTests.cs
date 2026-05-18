// -----------------------------------------------------------------------
// <copyright file="Stage21CrossStageE2eDepsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 2.1 — parser + resolver contract for the
/// optional <c>### Cross-Stage Dependencies</c> H3 inside a Stage body.
///
/// <para>The H3 holds anchor refs that gate the TRAILING qa-e2e
/// workstream (Stage 3.1 emits that workstream), not the impl
/// workstream itself. The resolver expands these refs against the same
/// anchor/title fallback chain as ordinary stage deps, but treats an
/// unresolved cross-stage e2e ref as FATAL — a missing upstream means
/// the qa-e2e workstream would wait forever, so the resolver throws
/// <c>plan.dep_unresolved</c> rather than tucking it into
/// <see cref="DecompositionDepsResolver.ResolvedStageDeps.UnresolvedDeps"/>.</para>
///
/// <para>All five Facts are pure-helper (no boot, no DB, no SignalR) so
/// they run inside the existing fast-CI bucket alongside the Phase 46
/// plan-format tests.</para>
/// </summary>
public class Stage21CrossStageE2eDepsTests
{
    // Shared two-phase plan harness — three stages: foundation/setup,
    // platform/api, platform/ui. The platform stages are where we attach
    // cross-stage e2e deps in the various test variants. Yields a stable
    // anchor space the resolver tests can reference verbatim.
    private const string TwoPhaseHarness = """
        ---
        title: "Stage 2.1 fixture"
        storyId: "WI-stage21"
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
        - [ ] scaffold compiles

        # Phase 2: Platform

        ## Dependencies
        - phase-foundation

        ## Stage 2.1: API

        ### Implementation Steps
        - [ ] add endpoints

        ### Dependencies
        - phase-foundation/stage-setup

        ### Test Scenarios
        - [ ] endpoint responds

        ## Stage 2.2: UI

        ### Implementation Steps
        - [ ] wire modal

        ### Dependencies
        - phase-platform/stage-api

        ### Test Scenarios
        - [ ] modal renders
        """;

    // ── 1. [unit] Stage without the H3 parses with empty CrossStageE2eDeps ──

    [Fact]
    public void Parser_Stage_Without_H3_Has_Empty_CrossStageE2eDeps()
    {
        var plan = PlanParser.Parse(TwoPhaseHarness);

        Assert.Empty(plan.ParseErrors);
        Assert.Equal(3, plan.AllStages().Count());
        Assert.All(plan.AllStages(), s =>
        {
            Assert.NotNull(s.CrossStageE2eDeps);
            Assert.Empty(s.CrossStageE2eDeps!);
        });

        // And the existing three-subsection contract is unchanged — the
        // optional H3's presence-or-absence does not perturb the existing
        // Dependencies / TestScenarios fields.
        var setup = plan.AllStages().Single(s => s.Name == "Setup");
        Assert.True(setup.IsStartStage);
        Assert.Single(setup.TestScenarios);

        var api = plan.AllStages().Single(s => s.Name == "API");
        Assert.Single(api.Dependencies);
        Assert.Equal("phase-foundation/stage-setup", api.Dependencies[0]);
    }

    // ── 2. [unit] Stage with the H3 listing two anchors resolves both ──

    [Fact]
    public void Parser_Stage_With_H3_Captures_Both_Anchors()
    {
        // Attach a cross-stage e2e dep to Stage 2.2 (UI) that pulls in
        // both the API stage AND the Foundation/Setup stage — i.e. the
        // e2e for UI exercises the full stack and so must wait for both.
        var md = TwoPhaseHarness.Replace(
            """
            ## Stage 2.2: UI

            ### Implementation Steps
            - [ ] wire modal

            ### Dependencies
            - phase-platform/stage-api

            ### Test Scenarios
            - [ ] modal renders
            """,
            """
            ## Stage 2.2: UI

            ### Implementation Steps
            - [ ] wire modal

            ### Dependencies
            - phase-platform/stage-api

            ### Test Scenarios
            - [ ] modal renders

            ### Cross-Stage Dependencies
            - phase-platform/stage-api
            - phase-foundation/stage-setup
            """);

        var plan = PlanParser.Parse(md);
        Assert.Empty(plan.ParseErrors);

        var ui = plan.AllStages().Single(s => s.Name == "UI");
        Assert.NotNull(ui.CrossStageE2eDeps);
        Assert.Equal(2, ui.CrossStageE2eDeps!.Count);
        Assert.Contains("phase-platform/stage-api", ui.CrossStageE2eDeps);
        Assert.Contains("phase-foundation/stage-setup", ui.CrossStageE2eDeps);

        // Resolver projects them to the trailing qa-e2e workstream's
        // upstream titles. Both anchors resolve, so UnresolvedDeps is empty
        // and CrossStageE2eDeps carries both upstream stage names.
        var resolved = DecompositionDepsResolver.ResolveAll(plan);
        var uiResolved = resolved[ui.Anchor];
        Assert.NotNull(uiResolved.CrossStageE2eDeps);
        Assert.Equal(2, uiResolved.CrossStageE2eDeps!.Count);
        Assert.Contains("API", uiResolved.CrossStageE2eDeps);
        Assert.Contains("Setup", uiResolved.CrossStageE2eDeps);

        // Content hash differs from the no-H3 baseline because the
        // resolver-facing data changed; the reconciler treats this stage
        // as "scope edited" so engineers re-prompt on open PRs.
        var baseline = PlanParser.Parse(TwoPhaseHarness).AllStages().Single(s => s.Name == "UI");
        Assert.NotEqual(baseline.ContentHash, ui.ContentHash);
    }

    // ── 3. [unit] Unresolved anchor in the H3 surfaces as plan.dep_unresolved ──

    [Fact]
    public void Parser_Stage_With_Unresolved_H3_Anchor_Throws_DepUnresolved()
    {
        // Reference a stage anchor that does not exist anywhere in the
        // plan (`phase-platform/stage-mobile` — there is no Mobile stage).
        var md = TwoPhaseHarness.Replace(
            """
            ## Stage 2.2: UI

            ### Implementation Steps
            - [ ] wire modal

            ### Dependencies
            - phase-platform/stage-api

            ### Test Scenarios
            - [ ] modal renders
            """,
            """
            ## Stage 2.2: UI

            ### Implementation Steps
            - [ ] wire modal

            ### Dependencies
            - phase-platform/stage-api

            ### Test Scenarios
            - [ ] modal renders

            ### Cross-Stage Dependencies
            - phase-platform/stage-mobile
            """);

        var plan = PlanParser.Parse(md);

        // Parser captures the H3 verbatim — it doesn't know what's
        // resolvable. The resolver is the layer that throws.
        var ui = plan.AllStages().Single(s => s.Name == "UI");
        Assert.Single(ui.CrossStageE2eDeps!);
        Assert.Equal("phase-platform/stage-mobile", ui.CrossStageE2eDeps![0]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DecompositionDepsResolver.ResolveAll(plan));

        // The message starts with the structured code so the orchestrator's
        // LastError projection and any operator UI can branch on the prefix.
        Assert.StartsWith(PlanParser.ErrorCodes.DepUnresolved, ex.Message, StringComparison.Ordinal);
        // And the message names BOTH the offending stage and the unresolved
        // ref so the operator can fix the plan without re-running the parser.
        Assert.Contains("UI", ex.Message);
        Assert.Contains("phase-platform/stage-mobile", ex.Message);
    }

    // ── 4. [unit] Cycle via cross-stage deps fires plan.dep_cycle ──

    [Fact]
    public void Resolver_Cycle_Through_CrossStage_Edge_Fires_DepCycle()
    {
        // Set up a cycle: Stage 2.1 (API) declares an explicit stage dep
        // on Stage 2.2 (UI); Stage 2.2 (UI) declares a Cross-Stage
        // Dependency back on Stage 2.1 (API). The two-way edge forms a
        // cycle on the effective DAG. The cycle detector must fire even
        // though the second edge was introduced via the new H3.
        var md = TwoPhaseHarness
            .Replace(
                """
                ## Stage 2.1: API

                ### Implementation Steps
                - [ ] add endpoints

                ### Dependencies
                - phase-foundation/stage-setup

                ### Test Scenarios
                - [ ] endpoint responds
                """,
                """
                ## Stage 2.1: API

                ### Implementation Steps
                - [ ] add endpoints

                ### Dependencies
                - phase-foundation/stage-setup
                - phase-platform/stage-ui

                ### Test Scenarios
                - [ ] endpoint responds
                """)
            .Replace(
                """
                ## Stage 2.2: UI

                ### Implementation Steps
                - [ ] wire modal

                ### Dependencies
                - phase-platform/stage-api

                ### Test Scenarios
                - [ ] modal renders
                """,
                """
                ## Stage 2.2: UI

                ### Implementation Steps
                - [ ] wire modal

                ### Dependencies
                - phase-platform/stage-api

                ### Test Scenarios
                - [ ] modal renders

                ### Cross-Stage Dependencies
                - phase-platform/stage-api
                """);

        var plan = PlanParser.Parse(md);
        var ex = Assert.Throws<InvalidOperationException>(
            () => DecompositionDepsResolver.ResolveAll(plan));

        Assert.StartsWith(PlanParser.ErrorCodes.DepCycle, ex.Message, StringComparison.Ordinal);
        // The cycle path names both stages in the loop.
        Assert.Contains("API", ex.Message);
        Assert.Contains("UI", ex.Message);
    }

    // ── 5. [integration] Full plan + resolver pipeline preserves anchors ──

    /// <summary>
    /// docs/forge/e2e.md Stage 2.1 integration scenario — the spec wording
    /// is "decompose endpoint round-trip"; in practice the decompose
    /// endpoint never sees plan markdown (it accepts a workstreams
    /// dictionary in the request body). The path that DOES convert
    /// markdown → resolver output is `PlanParser.Parse` →
    /// `DecompositionDepsResolver.ResolveAll`, which is the same pipeline
    /// <c>CrewStoryArtifactStrategy.ProduceDecompositionAsync</c> invokes
    /// before persisting workstreams. This Fact pins that pipeline so
    /// Stage 3.1 (which emits the trailing qa-e2e workstream with
    /// cross-stage deps baked into its <c>Deps[]</c>) inherits a
    /// known-good resolver shape.
    /// </summary>
    [Fact]
    public void Pipeline_RoundTrips_CrossStageE2eDeps_Through_Parser_And_Resolver()
    {
        // Three phases, six stages, two cross-stage e2e edges + one
        // ordinary stage dep + one phase-expansion dep. A representative
        // workload that exercises every code path the resolver supports.
        var md = """
            ---
            title: "Stage 2.1 pipeline fixture"
            storyId: "WI-stage21-pipeline"
            ---

            # Phase 1: Foundation

            ## Dependencies
            - _none — start phase_

            ## Stage 1.1: Schema

            ### Implementation Steps
            - [ ] tables

            ### Dependencies
            - _none — start stage_

            ### Test Scenarios
            - [ ] migration runs

            ## Stage 1.2: Auth model

            ### Implementation Steps
            - [ ] entities

            ### Dependencies
            - phase-foundation/stage-schema

            ### Test Scenarios
            - [ ] entities round-trip

            # Phase 2: API

            ## Dependencies
            - phase-foundation

            ## Stage 2.1: Auth endpoints

            ### Implementation Steps
            - [ ] login

            ### Dependencies
            - phase-foundation/stage-auth-model

            ### Test Scenarios
            - [ ] login works

            ## Stage 2.2: Gateway

            ### Implementation Steps
            - [ ] routes

            ### Dependencies
            - phase-api/stage-auth-endpoints

            ### Test Scenarios
            - [ ] routes work

            ### Cross-Stage Dependencies
            - phase-foundation/stage-auth-model

            # Phase 3: UI

            ## Dependencies
            - phase-api

            ## Stage 3.1: Login screen

            ### Implementation Steps
            - [ ] form

            ### Dependencies
            - phase-api/stage-auth-endpoints

            ### Test Scenarios
            - [ ] login form works

            ### Cross-Stage Dependencies
            - phase-foundation/stage-schema
            - phase-api/stage-gateway
            """;

        var plan = PlanParser.Parse(md);
        Assert.Equal(PlanParser.PlanFormat.V2, plan.Format);
        Assert.Empty(plan.ParseErrors);
        Assert.Equal(3, plan.Phases.Count);
        Assert.Equal(5, plan.AllStages().Count());

        var resolved = DecompositionDepsResolver.ResolveAll(plan);
        Assert.Equal(5, resolved.Count);

        // Gateway has 1 ordinary dep (auth-endpoints) + 1 cross-stage e2e
        // dep (auth-model). The ordinary edge propagates through
        // ResolvedStageDeps.Deps; the cross-stage edge propagates through
        // ResolvedStageDeps.CrossStageE2eDeps.
        var gatewayAnchor = plan.AllStages().Single(s => s.Name == "Gateway").Anchor;
        var gateway = resolved[gatewayAnchor];
        Assert.Contains("Auth endpoints", gateway.Deps);
        Assert.Single(gateway.CrossStageE2eDeps!);
        Assert.Equal("Auth model", gateway.CrossStageE2eDeps![0]);

        // Login screen has 1 ordinary dep + 2 cross-stage e2e deps. The
        // qa-e2e workstream Stage 3.1 will emit must wait on Schema AND
        // Gateway AND the impl-side dep (Auth endpoints), so all three
        // anchors must survive the pipeline.
        var loginAnchor = plan.AllStages().Single(s => s.Name == "Login screen").Anchor;
        var login = resolved[loginAnchor];
        Assert.Contains("Auth endpoints", login.Deps);
        Assert.Equal(2, login.CrossStageE2eDeps!.Count);
        Assert.Contains("Schema", login.CrossStageE2eDeps);
        Assert.Contains("Gateway", login.CrossStageE2eDeps);

        // Cross-stage anchors ALSO appear in the ordinary Deps surface
        // (the resolver unions them) so the cycle detector sees the same
        // graph the future qa-e2e scheduler will see. Verify by spot-
        // checking the login stage: its Deps[] must include the cross-
        // stage targets, not just the explicit Dependencies bullet.
        Assert.Contains("Schema", login.Deps);
        Assert.Contains("Gateway", login.Deps);

        // Sanity: no stage carries an UnresolvedDeps entry — every anchor
        // ref in the fixture resolves to a known phase or stage.
        Assert.All(resolved.Values, r => Assert.Empty(r.UnresolvedDeps));
    }
}
