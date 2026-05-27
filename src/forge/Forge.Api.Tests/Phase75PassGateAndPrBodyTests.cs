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

    // ── ShouldRefusePrBody (Phase 70 × Phase 75 cross-cut) ──────────────

    [Fact]
    public void ShouldRefusePrBody_OperatorAccepted_FailureMarkerBody_BypassesAndAllows()
    {
        // The 2026-05-27 local:UPDATE-HISTORY-IN-CL stage-1.4 case:
        // ACCEPT flipped iter 2 verdict iterate→pass on a generator-timeout
        // row (score=0, FailureKind=generator-timeout, agent commit already
        // pushed). The orchestrator built the PR body from finalDel which
        // still had Score=0 and Notes containing "GENERATOR FAILED"; the
        // body-marker check would otherwise re-stall the workstream forever.
        // OPERATOR ACCEPT bypasses the body check by design.
        var body = "## [impl] Implement ClusterDbPersistenceProvider base class\n"
            + "**Score:** 0 | **Iterations:** 2\n\n"
            + "### Evaluator Review\n"
            + "GENERATOR FAILED (generator-timeout): Copilot SDK hard timeout: exceeded 24m budget\n\n"
            + "OPERATOR ACCEPT (po@forge.local) — verdict overridden from 'iterate' to 'pass' at score 0.";
        Assert.False(StorySpawnOrchestrator.ShouldRefusePrBody(
            StorySpawnOrchestrator.PassGateClassification.OperatorAccepted, body));
    }

    [Fact]
    public void ShouldRefusePrBody_OperatorAccepted_CleanBody_AllowsTrivially()
    {
        var body = "## [impl] OK ws\n**Score:** 95 | **Iterations:** 1\n";
        Assert.False(StorySpawnOrchestrator.ShouldRefusePrBody(
            StorySpawnOrchestrator.PassGateClassification.OperatorAccepted, body));
    }

    [Fact]
    public void ShouldRefusePrBody_NaturalPass_FailureMarkerBody_Refuses()
    {
        // NaturalPass should NOT bypass — if a high-score legitimate-pass row
        // somehow produced a body containing "GENERATOR FAILED" (would be a
        // bug elsewhere), refuse to open the PR. Belt-and-suspenders intent
        // of Phase 75 Fix 3 is preserved for every non-operator-accept path.
        var body = "## [impl] WS\n**Score:** 95 | **Iterations:** 3\n\nGENERATOR FAILED elsewhere";
        Assert.True(StorySpawnOrchestrator.ShouldRefusePrBody(
            StorySpawnOrchestrator.PassGateClassification.NaturalPass, body));
    }

    [Fact]
    public void ShouldRefusePrBody_CorruptPass_FailureMarkerBody_Refuses()
    {
        // The DriveOneWorkstreamImplAsync top-of-method gate normally
        // demotes CorruptPass to "iterate" before the body assembly runs,
        // but if a future code path ever reaches this gate in the
        // CorruptPass state, refuse just as Phase 75 Fix 3 intended.
        var body = "**Score:** 0 | **Iterations:** 1\n\nGENERATOR FAILED";
        Assert.True(StorySpawnOrchestrator.ShouldRefusePrBody(
            StorySpawnOrchestrator.PassGateClassification.CorruptPass, body));
    }

    [Fact]
    public void ShouldRefusePrBody_NotPass_FailureMarkerBody_Refuses()
    {
        // Defensive — caller shouldn't reach this helper on NotPass, but
        // if they do, treat as a non-operator path and apply the body check.
        var body = "**Score:** 0 | **Iterations:** 1";
        Assert.True(StorySpawnOrchestrator.ShouldRefusePrBody(
            StorySpawnOrchestrator.PassGateClassification.NotPass, body));
    }

    [Fact]
    public void ShouldRefusePrBody_OperatorAccepted_NullBody_AllowsTrivially()
    {
        Assert.False(StorySpawnOrchestrator.ShouldRefusePrBody(
            StorySpawnOrchestrator.PassGateClassification.OperatorAccepted, null));
        Assert.False(StorySpawnOrchestrator.ShouldRefusePrBody(
            StorySpawnOrchestrator.PassGateClassification.OperatorAccepted, string.Empty));
    }
}
