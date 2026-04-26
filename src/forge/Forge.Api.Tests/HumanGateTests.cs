// -----------------------------------------------------------------------
// <copyright file="HumanGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Contracts;
using Forge.Api.Domain;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>Phase 6.9 — verifies branch / title / label gating.</summary>
public class HumanGateTests
{
    private static IHumanGateEvaluator NewEvaluator(HumanGateOptions opts) =>
        new HumanGateEvaluator(Options.Create(opts));

    private static Pr SamplePr(string branch = "ws/x", string title = "x", bool gated = false, string? reason = null) =>
        new(
            Id: "PR #1",
            Col: "review",
            Epic: "EPIC-1",
            EpicColor: "epic024",
            Workstream: branch,
            Base: "main",
            Branch: branch,
            Title: title,
            Author: new Agent("XX", "human", "x"),
            Additions: 0, Deletions: 0, Files: 0, Commits: 0, Age: "1m",
            Build: null, Review: null,
            Conflicts: false,
            HumanGate: gated,
            GateReason: reason);

    [Fact]
    public void Branch_Substring_Match_Triggers_Gate()
    {
        var eval = NewEvaluator(new HumanGateOptions { BranchSubstrings = { "/security" } });
        var d = eval.Evaluate(SamplePr(branch: "ws/security/oidc-rotation"));
        Assert.True(d.Required);
        Assert.Contains("/security", d.Reason!);
    }

    [Fact]
    public void Title_Substring_Match_Triggers_Gate()
    {
        var eval = NewEvaluator(new HumanGateOptions { TitleSubstrings = { "[security]" } });
        var d = eval.Evaluate(SamplePr(title: "[security] Patch CVE"));
        Assert.True(d.Required);
    }

    [Fact]
    public void Provider_Marked_Gated_Stays_Gated()
    {
        var eval = NewEvaluator(new HumanGateOptions());
        var d = eval.Evaluate(SamplePr(gated: true, reason: "label:security"));
        Assert.True(d.Required);
        Assert.Equal("label:security", d.Reason);
    }

    [Fact]
    public void Clean_Pr_Is_Not_Gated()
    {
        var eval = NewEvaluator(new HumanGateOptions { BranchSubstrings = { "/security" }, TitleSubstrings = { "[security]" } });
        var d = eval.Evaluate(SamplePr(branch: "ws/feature", title: "feat: nice"));
        Assert.False(d.Required);
    }
}
