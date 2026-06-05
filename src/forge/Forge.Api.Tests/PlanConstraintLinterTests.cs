// -----------------------------------------------------------------------
// <copyright file="PlanConstraintLinterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.IO;
using System.Linq;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Regression coverage for <see cref="PlanConstraintLinter"/>. The
/// 2026-06-04 <c>code-intelligence:PR-INGESTER</c> pri-postgres incident
/// (Stage 6.4 pin <c>0001..0034</c> while disk had migrations through
/// <c>0038</c>, infinite engineer-loop on stale plan constraint) is
/// covered by the workspace-aware test below as the canonical fixture.
/// </summary>
public class PlanConstraintLinterTests
{
    [Fact]
    public void Validate_Clean_Doc_Returns_Clean_Result()
    {
        var md = """
            # Phase 1: Migrations

            ## Stage 1.1: PR fact tables migrations 0027 through 0030

            ### Implementation Steps
            - [ ] Add `services/agent-memory/migrations/0027_pr_record.sql` per spec.
            - [ ] Add `services/agent-memory/migrations/0028_pr_change.sql` per spec.
            - [ ] Add `services/agent-memory/migrations/0029_pr_signal.sql` per spec.
            - [ ] Add `services/agent-memory/migrations/0030_pr_label.sql` per spec.

            # Phase 6: Gate jobs

            ## Stage 6.4: Gate job pri-postgres

            ### Implementation Steps
            - [ ] The target applies migrations `0001..0030` in-process via `migrations.New(db).Up(ctx)`.
            """;

        var result = PlanConstraintLinter.Validate(md);

        Assert.True(result.IsClean, $"Unexpected errors: {string.Join(", ", result.Errors.Select(e => e.Code))}");
    }

    [Fact]
    public void Validate_Pin_Below_Doc_Explicit_Max_Flags_It()
    {
        // Stage 1.3 adds migration 0038 in steps; Stage 6.4 pins at 0034.
        // The doc internally contradicts itself — the pin must cover
        // every migration the same doc adds.
        var md = """
            # Phase 1: Migrations

            ## Stage 1.3: Operational tables migrations 0035 through 0038

            ### Implementation Steps
            - [ ] Add `services/agent-memory/migrations/0035_x.sql`.
            - [ ] Add `services/agent-memory/migrations/0038_z.sql`.

            # Phase 6: Gate jobs

            ## Stage 6.4: Gate job pri-postgres

            ### Implementation Steps
            - [ ] The target applies migrations `0001..0034` before each subtest.
            """;

        var result = PlanConstraintLinter.Validate(md);

        Assert.False(result.IsClean);
        var err = Assert.Single(result.Errors, e => e.Code == PlanConstraintLinter.ErrorCodes.RangePinBelowExplicitMax);
        Assert.Contains("0038", err.Detail);
        Assert.Contains("Stage 6.4", err.StageName);
    }

    [Fact]
    public void Validate_Inconsistent_Pins_Flags_Stragglers()
    {
        // Two stages declare pins with different upper bounds and no
        // documented reason → the lower pin is the straggler.
        var md = """
            # Phase 1: Migrations

            ## Stage 1.4: Enum-tail registration

            ### Implementation Steps
            - [ ] Add a positive sub-test that applies the full `0001..0038` range.

            # Phase 6: Gate jobs

            ## Stage 6.4: Gate job pri-postgres

            ### Implementation Steps
            - [ ] The target applies migrations `0001..0034` before each subtest.
            """;

        var result = PlanConstraintLinter.Validate(md);

        Assert.False(result.IsClean);
        var err = Assert.Single(result.Errors, e => e.Code == PlanConstraintLinter.ErrorCodes.RangePinInconsistent);
        Assert.Contains("Stage 6.4", err.StageName);
        Assert.Contains("0038", err.Detail);
    }

    [Fact]
    public void Validate_Format_Errors_Are_Caught()
    {
        // Lower > upper is a malformed pin.
        var md = """
            ## Stage 6.4: Gate job pri-postgres

            ### Implementation Steps
            - [ ] The target applies migrations `0040..0034`.
            """;

        var result = PlanConstraintLinter.Validate(md);

        Assert.False(result.IsClean);
        Assert.Contains(result.Errors, e => e.Code == PlanConstraintLinter.ErrorCodes.RangePinFormat);
    }

    [Fact]
    public void Validate_Through_Style_Pin_Is_Parsed()
    {
        // The Test Scenarios prose style "the migrations through `0034`"
        // is a valid pin and is treated as lower=0001.
        var md = """
            ## Stage 6.4: Gate job pri-postgres

            ### Test Scenarios
            - Given `$AGENT_MEMORY_PG_URL` reachable and the migrations through `0034` ready, …

            ## Stage 1.3: adds migration 0035

            ### Implementation Steps
            - [ ] Add `services/agent-memory/migrations/0035_x.sql`.
            """;

        var result = PlanConstraintLinter.Validate(md);

        Assert.Contains(result.Errors, e => e.Code == PlanConstraintLinter.ErrorCodes.RangePinBelowExplicitMax);
    }

    [Fact]
    public void Validate_NNNN_Through_MMMM_Header_Style_Is_Recognised()
    {
        // The Stage 1.3 heading itself contains "0031 through 0034".
        // This must be picked up as a range pin (lower=0031, upper=0034)
        // so an even-higher migration added downstream still trips
        // `range_pin_below_explicit_max`.
        var md = """
            ## Stage 1.3: Operational tables migrations 0031 through 0034

            ### Implementation Steps
            - [ ] Add `services/agent-memory/migrations/0031_x.sql`.
            - [ ] Add `services/agent-memory/migrations/0034_y.sql`.

            ## Stage 5.5: Mgmt feedback pr record extension

            ### Implementation Steps
            - [ ] Add `services/agent-memory/migrations/0038_pr_record_feedback.sql`.
            """;

        var result = PlanConstraintLinter.Validate(md);

        // Stage 1.3's "0031 through 0034" pin is too low because the
        // same doc later adds 0038.
        Assert.Contains(result.Errors, e => e.Code == PlanConstraintLinter.ErrorCodes.RangePinBelowExplicitMax);
    }

    [Fact]
    public void Validate_Workspace_Aware_Catches_PriPostgres_Incident()
    {
        // The canonical regression: the plan never mentions 0035-0038
        // (so the pure-markdown rules can't see them), but the workspace
        // already has them on disk because sibling stages of the same
        // story merged them. Without the workspace cross-check the
        // engineer-evaluator loops indefinitely against a stale pin.
        using var tmp = new TempWorkspace();
        var migDir = Path.Combine(tmp.Path, "services", "agent-memory", "migrations");
        Directory.CreateDirectory(migDir);
        foreach (var n in new[] { 27, 28, 30, 33, 34, 35, 36, 37, 38 })
        {
            File.WriteAllText(Path.Combine(migDir, $"{n:D4}_dummy.sql"), "-- noop\n");
        }

        var md = """
            ## Stage 1.4: Enum-tail registration

            ### Implementation Steps
            - [ ] Add a positive sub-test that applies the full `0001..0034` range.

            ## Stage 6.4: Gate job pri-postgres

            ### Implementation Steps
            - [ ] The target applies migrations `0001..0034` in-process.
            """;

        var result = PlanConstraintLinter.Validate(md, workspacePath: tmp.Path);

        // Both stages with stale pins must each be flagged.
        var workspaceHits = result.Errors
            .Where(e => e.Code == PlanConstraintLinter.ErrorCodes.RangePinBelowWorkspaceHead)
            .ToList();
        Assert.True(workspaceHits.Count >= 2, $"Expected ≥2 workspace-head hits, got {workspaceHits.Count}: " +
            string.Join(" | ", result.Errors.Select(e => e.Code + ": " + e.Detail)));
        Assert.All(workspaceHits, e => Assert.Contains("0038", e.Detail));
    }

    [Fact]
    public void Validate_Workspace_Aware_Is_Silent_When_Pin_Covers_Head()
    {
        using var tmp = new TempWorkspace();
        var migDir = Path.Combine(tmp.Path, "services", "agent-memory", "migrations");
        Directory.CreateDirectory(migDir);
        foreach (var n in new[] { 27, 28, 30, 34 })
        {
            File.WriteAllText(Path.Combine(migDir, $"{n:D4}_dummy.sql"), "-- noop\n");
        }

        var md = """
            ## Stage 6.4: Gate job pri-postgres

            ### Implementation Steps
            - [ ] The target applies migrations `0001..0034` in-process.
            """;

        var result = PlanConstraintLinter.Validate(md, workspacePath: tmp.Path);

        Assert.DoesNotContain(result.Errors,
            e => e.Code == PlanConstraintLinter.ErrorCodes.RangePinBelowWorkspaceHead);
    }

    [Fact]
    public void BuildEvaluatorRulesSection_With_Result_Surfaces_Current_Violations()
    {
        var md = """
            ## Stage 6.4: Gate job pri-postgres

            ### Implementation Steps
            - [ ] The target applies migrations `0040..0034`.
            """;
        var result = PlanConstraintLinter.Validate(md);

        var block = PlanConstraintLinter.BuildEvaluatorRulesSection(result);

        Assert.Contains("CROSS-STAGE CONSTRAINT CHECK", block);
        Assert.Contains("plan.range_pin_format", block);
        Assert.Contains("plan.range_pin_below_explicit_max", block);
        Assert.Contains("plan.range_pin_below_workspace_head", block);
        Assert.Contains("Current violations", block);
    }

    [Fact]
    public void BuildEvaluatorRulesSection_Without_Result_Lists_Only_Rules()
    {
        var block = PlanConstraintLinter.BuildEvaluatorRulesSection();
        Assert.Contains("CROSS-STAGE CONSTRAINT CHECK", block);
        Assert.DoesNotContain("Current violations", block);
    }

    [Fact]
    public void Validate_Empty_And_Whitespace_Is_Clean()
    {
        Assert.True(PlanConstraintLinter.Validate(null).IsClean);
        Assert.True(PlanConstraintLinter.Validate("").IsClean);
        Assert.True(PlanConstraintLinter.Validate("   \n\n   ").IsClean);
    }

    [Fact]
    public void Validate_Workspace_Missing_Is_Handled_Gracefully()
    {
        var md = """
            ## Stage 6.4
            - [ ] applies `0001..0034`.
            """;
        // Non-existent path: linter must degrade to pure-markdown rules
        // rather than throw.
        var result = PlanConstraintLinter.Validate(md, workspacePath: @"X:\does\not\exist\anywhere");
        Assert.True(result.IsClean);
    }

    private sealed class TempWorkspace : System.IDisposable
    {
        public string Path { get; }

        public TempWorkspace()
        {
            this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "forge-planlint-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(this.Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
