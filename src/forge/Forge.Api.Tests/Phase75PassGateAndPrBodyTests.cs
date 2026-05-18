// -----------------------------------------------------------------------
// <copyright file="Phase75PassGateAndPrBodyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 75 — verifies the pass-gate classifier
/// (<see cref="StorySpawnOrchestrator.ClassifyPassGate"/>) and the
/// PR-body failure-marker detector
/// (<see cref="StorySpawnOrchestrator.PrBodyHasFailureMarker"/>).
///
/// <para>Together these gates close the bug observed 2026-05-17 on
/// failover-cluster:XRAFT stage-8.2 where PR #42 was opened with a
/// body literally containing "GENERATOR FAILED" + "Score: 0 |
/// Iterations: 7" because the orchestrator's gate at
/// <c>StorySpawnOrchestrator.DriveOneWorkstreamImplAsync</c> trusted
/// <c>lastDel.Verdict == "pass"</c> as the sole signal.</para>
/// </summary>
public class Phase75PassGateAndPrBodyTests
{
    // ── ClassifyPassGate ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("iterate")]
    [InlineData("stalled")]
    [InlineData("sloppy")]
    [InlineData("reject")]
    [InlineData("stalled-budget")]
    public void ClassifyPassGate_NonPassVerdict_ReturnsNotPass(string? verdict)
    {
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.NotPass,
            StorySpawnOrchestrator.ClassifyPassGate(verdict, score: 95, failureKind: null, notes: null));
    }

    [Fact]
    public void ClassifyPassGate_VerdictPass_ScoreAbove90_NoFailureKind_ReturnsNaturalPass()
    {
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.NaturalPass,
            StorySpawnOrchestrator.ClassifyPassGate("pass", score: 95, failureKind: null, notes: null));
    }

    [Fact]
    public void ClassifyPassGate_VerdictPass_ScoreAtThreshold_NoFailureKind_ReturnsNaturalPass()
    {
        // PassThreshold = 90 — boundary check
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.NaturalPass,
            StorySpawnOrchestrator.ClassifyPassGate("pass", score: 90, failureKind: null, notes: null));
    }

    [Fact]
    public void ClassifyPassGate_VerdictPass_ScoreBelow90_NoFailureKind_ReturnsCorruptPass()
    {
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.CorruptPass,
            StorySpawnOrchestrator.ClassifyPassGate("pass", score: 89, failureKind: null, notes: null));
    }

    [Fact]
    public void ClassifyPassGate_VerdictPass_ScoreZero_NoFailureKind_ReturnsCorruptPass()
    {
        // The PR #42 case minus the FailureKind: empty-diff synthesis
        // produced score=0 with FailureKind=null; should still be refused.
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.CorruptPass,
            StorySpawnOrchestrator.ClassifyPassGate("pass", score: 0, failureKind: null, notes: null));
    }

    [Fact]
    public void ClassifyPassGate_VerdictPass_HighScoreButFailureKind_ReturnsCorruptPass()
    {
        // Generator failure with score=95 (e.g. flipped from a transport
        // timeout row) — refuse even though score is high.
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.CorruptPass,
            StorySpawnOrchestrator.ClassifyPassGate("pass", score: 95, failureKind: "generator-timeout", notes: null));
    }

    [Fact]
    public void ClassifyPassGate_VerdictPass_OperatorAcceptNote_ReturnsOperatorAccepted()
    {
        // Phase 70 ACCEPT signature — operator override on a sub-90 score
        // is legitimate.
        var note = "Some prior eval notes\n\nOPERATOR ACCEPT (xiaodoli@microsoft.com) — verdict overridden from 'iterate' to 'pass' at score 80.";
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.OperatorAccepted,
            StorySpawnOrchestrator.ClassifyPassGate("pass", score: 80, failureKind: null, notes: note));
    }

    [Fact]
    public void ClassifyPassGate_VerdictPass_OperatorAcceptNote_TrumpsFailureKind()
    {
        // Operator override trumps FailureKind: if the operator manually
        // verified the work despite a transport-timeout deliverable,
        // honour their override.
        var note = "GENERATOR FAILED (generator-timeout): …\n\nOPERATOR ACCEPT (xiaodoli@microsoft.com) — verdict overridden.";
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.OperatorAccepted,
            StorySpawnOrchestrator.ClassifyPassGate("pass", score: 0, failureKind: "generator-timeout", notes: note));
    }

    [Fact]
    public void ClassifyPassGate_VerdictPassMixedCase_NormalizesToPass()
    {
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.NaturalPass,
            StorySpawnOrchestrator.ClassifyPassGate("Pass", score: 95, failureKind: null, notes: null));
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.NaturalPass,
            StorySpawnOrchestrator.ClassifyPassGate("PASS", score: 95, failureKind: null, notes: null));
    }

    [Fact]
    public void ClassifyPassGate_NotesContainsAcceptPhraseButNotPrefix_StillCorrupt()
    {
        // "OPERATOR ACCEPT (" must appear literally — a partial
        // "operator accept" reference in evaluator prose (e.g. "the
        // operator must accept this") does NOT count as an override.
        var note = "The evaluator suggested the operator might want to accept this hack.";
        Assert.Equal(
            StorySpawnOrchestrator.PassGateClassification.CorruptPass,
            StorySpawnOrchestrator.ClassifyPassGate("pass", score: 50, failureKind: null, notes: note));
    }

    // ── PrBodyHasFailureMarker ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PrBodyHasFailureMarker_NullOrEmpty_ReturnsFalse(string? body)
    {
        Assert.False(StorySpawnOrchestrator.PrBodyHasFailureMarker(body));
    }

    [Fact]
    public void PrBodyHasFailureMarker_CleanBody_ReturnsFalse()
    {
        var body = "## [impl] My Workstream\n\n**Story:** my:STORY\n**Score:** 95 | **Iterations:** 3\n\n### What changed\nAdded a thing.";
        Assert.False(StorySpawnOrchestrator.PrBodyHasFailureMarker(body));
    }

    [Fact]
    public void PrBodyHasFailureMarker_BodyWithGeneratorFailed_ReturnsTrue()
    {
        var body = "## [impl] Bad Workstream\n\n**Score:** 0 | **Iterations:** 7\n\n### Evaluator Review\n\nGENERATOR FAILED (generator-timeout): exceeded budget";
        Assert.True(StorySpawnOrchestrator.PrBodyHasFailureMarker(body));
    }

    [Fact]
    public void PrBodyHasFailureMarker_BodyWithZeroScoreHeader_ReturnsTrue()
    {
        var body = "## [impl] Empty Diff WS\n\n**Story:** my:STORY\n**Score:** 0 | **Iterations:** 1";
        Assert.True(StorySpawnOrchestrator.PrBodyHasFailureMarker(body));
    }

    [Fact]
    public void PrBodyHasFailureMarker_BodyWithScoreOne_ReturnsFalse()
    {
        var body = "**Score:** 1 | **Iterations:** 3";
        Assert.False(StorySpawnOrchestrator.PrBodyHasFailureMarker(body));
    }

    [Fact]
    public void PrBodyHasFailureMarker_BodyWithScore0InProse_NoFalsePositive()
    {
        // The body might mention "score 0" in narrative without using
        // the bold header format — those should NOT trip the gate.
        var body = "## [impl] Reflective notes\n\nThe evaluator gave us a score of 0 last iter but recovered fully.\n\n**Score:** 95 | **Iterations:** 4";
        Assert.False(StorySpawnOrchestrator.PrBodyHasFailureMarker(body));
    }

    [Fact]
    public void PrBodyHasFailureMarker_GeneratorFailedCaseInsensitive()
    {
        var body = "## Some PR\n\ngenerator failed (timeout)";
        Assert.True(StorySpawnOrchestrator.PrBodyHasFailureMarker(body));
    }

    [Fact]
    public void PrBodyHasFailureMarker_RealWorldPr42BodySnippet_ReturnsTrue()
    {
        // Verbatim snippet from the failover-cluster:XRAFT PR #42
        // description that triggered this whole investigation.
        var body = "## [impl] Chaos and Stress Testing\n\n"
            + "**Story:** failover-cluster:XRAFT\n"
            + "**Score:** 0 | **Iterations:** 7\n\n"
            + "🤖 **Generator:** Cheng Yu (`claude-opus-4.7-xhigh`) | 🤖 **Evaluator:** Bei Hai Long Wang (`gpt-5.5`)\n\n"
            + "### What changed\n\n"
            + "Now run the stress test again ...\n\n"
            + "### Evaluator Review\n\n"
            + "GENERATOR FAILED (generator-timeout): Copilot SDK hard timeout: exceeded 39m budget";
        Assert.True(StorySpawnOrchestrator.PrBodyHasFailureMarker(body));
    }
}
