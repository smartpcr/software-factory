// -----------------------------------------------------------------------
// <copyright file="Phase75PriorIterMarkerLeakTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 75 follow-up — verifies that the literal "GENERATOR FAILED (kind)"
/// marker that <c>DeliverableLoop</c>'s failure-path (line ~840) persists
/// into <c>DeliverableRow.Notes</c> on transient generator failures
/// (timeout, inactive, max-turns) is stripped from the prior-iter chain
/// before it lands in the next iter's generator prelude.
///
/// <para>Root cause observed 2026-05-27 on local-http-memory:
/// extend-requesttraceexpressions-with-stream-evaluator (Stage 2.1):</para>
/// <list type="number">
///   <item>Iter 1 generator hit the 24m hard timeout at 3.6M tokens →
///     failure-path persisted <c>Notes = "GENERATOR FAILED
///     (generator-timeout): ..."</c> with verdict=iterate (transient).</item>
///   <item>Iter 2 resume seeded <c>priorChain</c> with the raw notes;
///     <c>FormatPriorChain</c> emitted the marker verbatim into
///     <c>notesForNext</c>, which became the iter 2 generator prelude.</item>
///   <item>The agent at 3.5M-token context bloat echoed the literal phrase
///     into its <c>## Change Summary</c> output, which landed in
///     <c>DeliverableRow.GeneratorSummary</c>.</item>
///   <item><c>StorySpawnOrchestrator.DriveOneWorkstreamImplAsync</c>'s
///     PR-body assembly piped the summary into <c>### What changed</c>,
///     and Gate B (<c>PrBodyHasFailureMarker</c>) stalled the workstream
///     with the verbatim "Phase 75: refused to open PR because the body
///     embedded a generator-failure marker" message.</item>
/// </list>
///
/// <para>The fix neutralizes the marker phrase at the propagation
/// boundary — agents still learn the prior iter aborted and why, but
/// cannot propagate the literal trigger phrase downstream.</para>
/// </summary>
public class Phase75PriorIterMarkerLeakTests
{
    // ── SanitizePriorIterMarkers ────────────────────────────────────────

    [Fact]
    public void Sanitize_StripsGeneratorFailedTimeoutMarker()
    {
        const string raw = "GENERATOR FAILED (generator-timeout): hard wall-clock budget 24m exceeded at iter 1.";
        var sanitized = DeliverableLoop.SanitizePriorIterMarkers(raw);

        Assert.DoesNotContain("GENERATOR FAILED", sanitized);
        Assert.Contains("[Prior iter aborted: generator-timeout]", sanitized);
        Assert.Contains("hard wall-clock budget 24m exceeded", sanitized);
    }

    [Theory]
    [InlineData("generator-timeout")]
    [InlineData("generator-max-turns")]
    [InlineData("generator-inactive")]
    [InlineData("generator-cli-exit")]
    [InlineData("generator-unknown")]
    public void Sanitize_StripsAllKnownFailureKinds(string kind)
    {
        var raw = $"GENERATOR FAILED ({kind}): some reason.";
        var sanitized = DeliverableLoop.SanitizePriorIterMarkers(raw);

        Assert.DoesNotContain("GENERATOR FAILED", sanitized);
        Assert.Contains($"[Prior iter aborted: {kind}]", sanitized);
    }

    [Fact]
    public void Sanitize_StripsCaseInsensitive()
    {
        const string raw = "generator failed (Generator-Timeout): mixed case in the wild.";
        var sanitized = DeliverableLoop.SanitizePriorIterMarkers(raw);

        // Regex is case-insensitive so the lower-case variant must also be stripped.
        Assert.DoesNotContain("generator failed", sanitized, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Prior iter aborted: Generator-Timeout]", sanitized);
    }

    [Fact]
    public void Sanitize_StripsScoreZeroHeader()
    {
        const string raw = "**Score:** 0 | **Iterations:** 1 — defensive: a prior iter's notes embedded this header.";
        var sanitized = DeliverableLoop.SanitizePriorIterMarkers(raw);

        Assert.DoesNotMatch(@"\*\*Score:\*\*\s*0\s*\|", sanitized);
        Assert.Contains("**Score:** prior-zero |", sanitized);
    }

    [Fact]
    public void Sanitize_PreservesNormalNotes()
    {
        const string raw = "## LATEST evaluator feedback\n\nThe implementation looks solid but needs more tests.";
        var sanitized = DeliverableLoop.SanitizePriorIterMarkers(raw);

        Assert.Equal(raw, sanitized);
    }

    [Fact]
    public void Sanitize_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, DeliverableLoop.SanitizePriorIterMarkers(null));
        Assert.Equal(string.Empty, DeliverableLoop.SanitizePriorIterMarkers(string.Empty));
    }

    [Fact]
    public void Sanitize_StripsMultipleOccurrencesInOneString()
    {
        const string raw =
            "GENERATOR FAILED (generator-timeout): first abort.\n\n" +
            "Later, the operator noted: GENERATOR FAILED (generator-inactive): second abort.";
        var sanitized = DeliverableLoop.SanitizePriorIterMarkers(raw);

        Assert.DoesNotContain("GENERATOR FAILED", sanitized);
        Assert.Contains("[Prior iter aborted: generator-timeout]", sanitized);
        Assert.Contains("[Prior iter aborted: generator-inactive]", sanitized);
    }

    [Fact]
    public void Sanitize_NeutralizedTextDoesNotTripGateB()
    {
        // The whole point: after sanitization, the resulting text must not
        // match Gate B's substring or regex triggers.
        const string raw =
            "GENERATOR FAILED (generator-timeout): bloated context.\n\n" +
            "**Score:** 0 | **Iterations:** 1";
        var sanitized = DeliverableLoop.SanitizePriorIterMarkers(raw);

        Assert.False(
            Forge.Api.Services.Stories.StorySpawnOrchestrator.PrBodyHasFailureMarker(sanitized),
            "Sanitized text must not trip Gate B's PrBodyHasFailureMarker check.");
    }

    // ── End-to-end: raw still trips, sanitized does not ─────────────────

    [Fact]
    public void GateB_StillCatches_OriginalUnsanitizedMarker()
    {
        // Belt-and-suspenders: confirm Gate B's substring check still
        // detects the literal phrase when it is NOT sanitized — i.e., we
        // haven't accidentally broken the gate's ability to catch a
        // legitimate current-iter failure (the original PR #42 scenario).
        const string prBody =
            "## What changed\n\n" +
            "GENERATOR FAILED (generator-timeout): the agent hit the wall-clock.\n\n" +
            "**Score:** 0 | **Iterations:** 7";

        Assert.True(
            Forge.Api.Services.Stories.StorySpawnOrchestrator.PrBodyHasFailureMarker(prBody),
            "Gate B must still fire on a PR body with the unsanitized marker.");
    }
}
