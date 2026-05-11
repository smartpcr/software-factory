// -----------------------------------------------------------------------
// <copyright file="WorkstreamWorktreeSlugTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 49 follow-up — pin the short, semantic worktree directory naming
/// convention so future refactors don't regress to the legacy
/// <c>spawn-{branch-slug}</c> shape that routinely produced 100+ char
/// directory names and blew past Windows MAX_PATH the moment a Rust /
/// Node / .NET build tree (target/, node_modules/, obj/) landed
/// underneath.
/// </summary>
public class WorkstreamWorktreeSlugTests
{
    private static WorkstreamRow Ws(
        string id, int sort, string? phaseAnchor, string? planAnchor, string branch = "", string? storySpawnId = "spawn-x")
        => new()
        {
            Id = id,
            Sort = sort,
            PhaseAnchor = phaseAnchor,
            PlanAnchor = planAnchor,
            Branch = branch,
            StorySpawnId = storySpawnId,
        };

    /// <summary>
    /// The headline regression: the failover-cluster:XRAFT stage 1.1
    /// workstream (longest observed on prod 2026-05-10) must produce
    /// <c>stage-1.1-project-scaffolding-stage-cargo-workspace-and-crate-layout</c>,
    /// not <c>spawn-ws-failover-cluster-XRAFT-phase-project-scaffolding-stage-cargo-workspace-and-crate-layout</c>.
    /// Shaves ~30 characters off the path and stays under MAX_PATH for the
    /// <c>target/debug/.fingerprint/{crate}-{hash}/dep-build-script-build-script-build</c>
    /// nesting that <c>cargo build</c> writes underneath.
    /// </summary>
    [Fact]
    public void Compute_ProjectScaffolding_Stage1_1_ProducesShortPath()
    {
        var siblings = new[]
        {
            Ws("ws-stage-1-1", 1, "phase-project-scaffolding", "phase-project-scaffolding/stage-cargo-workspace-and-crate-layout"),
            Ws("ws-stage-1-2", 2, "phase-project-scaffolding", "phase-project-scaffolding/stage-core-types-and-configuration"),
            Ws("ws-stage-1-3", 3, "phase-project-scaffolding", "phase-project-scaffolding/stage-rpc-message-definitions"),
            Ws("ws-stage-2-1", 4, "phase-raft-consensus-engine", "phase-raft-consensus-engine/stage-leader-election"),
        };

        var slug = WorkstreamWorktreeSlug.Compute(siblings[0], siblings);

        Assert.Equal(
            "stage-1.1-project-scaffolding-stage-cargo-workspace-and-crate-layout",
            slug);
        Assert.DoesNotContain("ws-failover-cluster", slug);
        Assert.DoesNotContain("phase-project-scaffolding/", slug); // path separator collapsed to '-'
    }

    /// <summary>
    /// Phase numbering follows the first-encounter order of distinct
    /// <see cref="WorkstreamRow.PhaseAnchor"/> values when iterating
    /// siblings sorted by <see cref="WorkstreamRow.Sort"/>. Stage
    /// numbering is the 1-based position within that phase bucket.
    /// </summary>
    [Theory]
    [InlineData(1, "stage-1.1-project-scaffolding-stage-cargo-workspace-and-crate-layout")]
    [InlineData(2, "stage-1.2-project-scaffolding-stage-core-types-and-configuration")]
    [InlineData(3, "stage-1.3-project-scaffolding-stage-rpc-message-definitions")]
    [InlineData(4, "stage-2.1-raft-consensus-engine-stage-leader-election")]
    public void Compute_PositionNumbersFollowSortOrderWithinPhase(int targetSort, string expectedSlug)
    {
        var siblings = new[]
        {
            Ws("ws-1", 1, "phase-project-scaffolding", "phase-project-scaffolding/stage-cargo-workspace-and-crate-layout"),
            Ws("ws-2", 2, "phase-project-scaffolding", "phase-project-scaffolding/stage-core-types-and-configuration"),
            Ws("ws-3", 3, "phase-project-scaffolding", "phase-project-scaffolding/stage-rpc-message-definitions"),
            Ws("ws-4", 4, "phase-raft-consensus-engine", "phase-raft-consensus-engine/stage-leader-election"),
        };

        var target = Array.Find(siblings, w => w.Sort == targetSort)!;
        Assert.Equal(expectedSlug, WorkstreamWorktreeSlug.Compute(target, siblings));
    }

    /// <summary>
    /// Sort order is not necessarily insertion order — verify that the
    /// helper sorts by <see cref="WorkstreamRow.Sort"/> ascending before
    /// computing position, so a phase introduced via a later
    /// <see cref="WorkstreamRow.Sort"/> still gets the correct
    /// 1-based phase index.
    /// </summary>
    [Fact]
    public void Compute_HonorsSortOrderRegardlessOfArrayPosition()
    {
        // Shuffle: high Sort first.
        var siblings = new[]
        {
            Ws("ws-4", 4, "phase-raft-consensus-engine", "phase-raft-consensus-engine/stage-leader-election"),
            Ws("ws-2", 2, "phase-project-scaffolding", "phase-project-scaffolding/stage-core-types-and-configuration"),
            Ws("ws-1", 1, "phase-project-scaffolding", "phase-project-scaffolding/stage-cargo-workspace-and-crate-layout"),
            Ws("ws-3", 3, "phase-project-scaffolding", "phase-project-scaffolding/stage-rpc-message-definitions"),
        };

        Assert.Equal(
            "stage-2.1-raft-consensus-engine-stage-leader-election",
            WorkstreamWorktreeSlug.Compute(siblings[0], siblings));
        Assert.Equal(
            "stage-1.2-project-scaffolding-stage-core-types-and-configuration",
            WorkstreamWorktreeSlug.Compute(siblings[1], siblings));
    }

    /// <summary>
    /// Rows without a <see cref="WorkstreamRow.PlanAnchor"/> (handcrafted
    /// via <c>POST /api/workstreams</c>, or pre-Phase-42 legacy rows that
    /// never went through the reconciler) take the legacy
    /// <c>spawn-{branch-slug}</c> shape as a fallback so the path is
    /// still unique. Plan-doc / QA worktrees are intentionally NOT
    /// renamed (they already have short names).
    /// </summary>
    [Fact]
    public void Compute_NoPlanAnchor_FallsBackToLegacySpawnSlug()
    {
        var ws = Ws("ws-handcrafted", 1, phaseAnchor: null, planAnchor: null, branch: "ws/handcrafted-feature");
        var siblings = new[] { ws };

        var slug = WorkstreamWorktreeSlug.Compute(ws, siblings);

        Assert.StartsWith("spawn-", slug);
        Assert.Contains("ws-handcrafted-feature", slug);
    }

    /// <summary>
    /// A target row that the caller forgot to include in
    /// <c>sameSpawnWorkstreams</c> still produces a unique slug — falls
    /// back to <c>(1, target.Sort)</c> rather than throwing. Defensive
    /// guard against the call-site mistake; the production path always
    /// includes the target.
    /// </summary>
    [Fact]
    public void Compute_TargetMissingFromSiblings_FallsBackGracefully()
    {
        var target = Ws("ws-stray", 7, "phase-unknown-phase", "phase-unknown-phase/stage-some-stage");
        var siblings = new[]
        {
            Ws("ws-other", 1, "phase-different", "phase-different/stage-alpha"),
        };

        var slug = WorkstreamWorktreeSlug.Compute(target, siblings);

        // Unique within the spawn even though target wasn't in the siblings list.
        Assert.StartsWith("stage-", slug);
        Assert.Contains("unknown-phase", slug);
    }
}
