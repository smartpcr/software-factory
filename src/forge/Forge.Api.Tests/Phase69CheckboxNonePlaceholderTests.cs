// -----------------------------------------------------------------------
// <copyright file="Phase69CheckboxNonePlaceholderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 69 — <see cref="DeliverableLoop.CountCheckboxMarkers"/> must
/// treat <c>- [ ]</c> markers with semantically-empty bodies (e.g.
/// <c>None.</c>, <c>N/A</c>, <c>—</c>) as zero outstanding items.
///
/// <para>Without this guard the Phase 50 checkbox-attention hard gate
/// produces an unfulfillable demand: the evaluator's
/// <c>Still needs improvement: - [ ] 1. None.</c> shorthand counts as
/// 1 unchecked item, the next iter's generator has nothing to FIX so
/// produces 0 <c>- [x]</c> markings, the gate demotes pass→iterate,
/// repeat indefinitely. Observed 2026-05-15 on
/// <c>failover-cluster:XRAFT:stage-4.1-grpc-transport-layer</c> iter 6
/// at score 98 ($1367.98 burned across 6 iters).</para>
/// </summary>
public class Phase69CheckboxNonePlaceholderTests
{
    // ── Semantically-empty placeholders skipped ────────────────────────────

    [Theory]
    [InlineData("- [ ] None.")]
    [InlineData("- [ ] None")]
    [InlineData("- [ ] none")]
    [InlineData("- [ ] N/A")]
    [InlineData("- [ ] n/a")]
    [InlineData("- [ ] NA")]
    [InlineData("- [ ] —")]
    [InlineData("- [ ] –")]
    [InlineData("- [ ] -")]
    [InlineData("- [ ] nothing")]
    [InlineData("- [ ] No items")]
    [InlineData("- [ ] No outstanding items")]
    [InlineData("- [ ] No remaining items")]
    [InlineData("- [ ] No items remain")]
    [InlineData("- [ ] (none)")]
    [InlineData("- [ ]")]
    [InlineData("- [ ] ")]
    [InlineData("- [ ] 1. None.")]
    [InlineData("- [ ] 1) None")]
    [InlineData("- [ ] 2. n/a")]
    [InlineData("* [ ] None.")]
    public void Empty_placeholder_markers_count_as_zero(string line)
    {
        Assert.Equal(0, DeliverableLoop.CountCheckboxMarkers(line, lookForUnchecked: true));
    }

    // ── Real items still counted ──────────────────────────────────────────

    [Theory]
    [InlineData("- [ ] Add a fix for the auth bug")]
    [InlineData("- [ ] 1. Address the §3.2 race condition")]
    [InlineData("- [ ] Update README with new flag")]
    [InlineData("* [ ] None of the tests cover the rollback path")] // "None of the tests..." is real
    public void Real_items_still_count(string line)
    {
        Assert.Equal(1, DeliverableLoop.CountCheckboxMarkers(line, lookForUnchecked: true));
    }

    // ── Mixed input ───────────────────────────────────────────────────────

    [Fact]
    public void Mixed_input_counts_only_real_items()
    {
        var text = @"
What still needs work:
- [ ] 1. Fix the regex in `OpenQuestionsExtractor.cs`
- [ ] 2. None.
- [ ] 3. Update the test for §3.2
- [ ] N/A
- [ ] 4. (none)
* [ ] Real follow-up item
".Trim();
        // Real items: 1, 3, "Real follow-up item" = 3.
        // Placeholders: "2. None.", "N/A", "4. (none)" = ignored.
        Assert.Equal(3, DeliverableLoop.CountCheckboxMarkers(text, lookForUnchecked: true));
    }

    // ── Regression: the exact XRAFT iter-6 footgun ────────────────────────

    [Fact]
    public void XRAFT_iter6_evaluator_notes_count_as_zero_outstanding()
    {
        // Verbatim shape from the live workstream
        // failover-cluster:XRAFT:stage-4.1-grpc-transport-layer iter 6:
        // evaluator's "Still needs improvement: - [ ] 1. None." was
        // counted by the pre-Phase-69 regex as 1 outstanding item,
        // demoting pass (score 98) → iterate and spinning the loop.
        var evalNotes = @"Improvements this iteration:
- Independently verified `[x] 1. ADDRESSED`: Cargo.toml lists rand workspace dep.
- Independently verified `[x] 2. ADDRESSED`: grpc_client.rs adds jittered_sleep_duration.

Still needs improvement:
- [ ] 1. None.

Why this score: prior findings fixed in code and narrative.";

        var unchecked_ = DeliverableLoop.CountCheckboxMarkers(evalNotes, lookForUnchecked: true);
        Assert.Equal(0, unchecked_);
    }

    // ── `- [x]` counting unchanged (no body-checking on the checked side) ─

    [Fact]
    public void Checked_markers_count_regardless_of_body()
    {
        var text = @"
### Prior feedback resolution
- [x] 1. FIXED — Cargo.toml updated
- [x] 2. DEFERRED — out of scope, tracked in WI-9999
- [X] 3. FIXED (uppercase X)
* [x] 4. FIXED (asterisk bullet)
";
        Assert.Equal(4, DeliverableLoop.CountCheckboxMarkers(text, lookForUnchecked: false));
    }

    [Fact]
    public void Empty_or_null_input_returns_zero()
    {
        Assert.Equal(0, DeliverableLoop.CountCheckboxMarkers(string.Empty, lookForUnchecked: true));
        Assert.Equal(0, DeliverableLoop.CountCheckboxMarkers(string.Empty, lookForUnchecked: false));
        Assert.Equal(0, DeliverableLoop.CountCheckboxMarkers(null!, lookForUnchecked: true));
    }
}
