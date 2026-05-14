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
/// Pin the canonical engineer-workstream worktree directory naming so
/// future refactors don't regress to a long-anchor or the legacy
/// <c>spawn-</c> prefix. The shape is
/// <c>stage-{phaseNum}.{stageNum}-{stageTitle}</c> where the stage title
/// is the suffix of <see cref="WorkstreamRow.PlanAnchor"/> after the
/// <c>/stage-</c> separator.
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
    /// The headline example: phase 3 "consensus engine" / stage 3.3
    /// "log replication" → <c>stage-3.3-log-replication</c>. Both the
    /// phase title and the legacy <c>spawn-</c> prefix MUST be absent.
    /// </summary>
    [Fact]
    public void Compute_ConsensusEngine_LogReplication_ProducesStageTitleOnlySlug()
    {
        var siblings = new[]
        {
            // Phase 1, stage 1.1
            Ws("ws-1", 1, "phase-foundation", "phase-foundation/stage-bootstrap"),
            // Phase 2, stage 2.1
            Ws("ws-2", 2, "phase-rpc", "phase-rpc/stage-message-types"),
            // Phase 3, stages 3.1 - 3.3
            Ws("ws-3", 3, "phase-consensus-engine", "phase-consensus-engine/stage-leader-election"),
            Ws("ws-4", 4, "phase-consensus-engine", "phase-consensus-engine/stage-append-entries"),
            Ws("ws-5", 5, "phase-consensus-engine", "phase-consensus-engine/stage-log-replication"),
        };

        var slug = WorkstreamWorktreeSlug.Compute(siblings[4], siblings);

        Assert.Equal("stage-3.3-log-replication", slug);
        Assert.DoesNotContain("consensus-engine", slug);
        Assert.DoesNotContain("spawn-", slug);
    }

    /// <summary>
    /// Phase numbering follows the first-encounter order of distinct
    /// <see cref="WorkstreamRow.PhaseAnchor"/> values when iterating
    /// siblings sorted by <see cref="WorkstreamRow.Sort"/>. Stage
    /// numbering is the 1-based position within that phase bucket. The
    /// stage title comes from the portion of PlanAnchor after
    /// <c>/stage-</c>.
    /// </summary>
    [Theory]
    [InlineData(1, "stage-1.1-cargo-workspace-and-crate-layout")]
    [InlineData(2, "stage-1.2-core-types-and-configuration")]
    [InlineData(3, "stage-1.3-rpc-message-definitions")]
    [InlineData(4, "stage-2.1-leader-election")]
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
            "stage-2.1-leader-election",
            WorkstreamWorktreeSlug.Compute(siblings[0], siblings));
        Assert.Equal(
            "stage-1.2-core-types-and-configuration",
            WorkstreamWorktreeSlug.Compute(siblings[1], siblings));
    }

    /// <summary>
    /// The PlanReconciler appends <c>@{contentHash}</c> to PlanAnchor when
    /// it detects a scope edit on an in-flight row. The hash must NOT leak
    /// into the worktree directory name — it changes every plan rev and
    /// would orphan the previous directory.
    /// </summary>
    [Fact]
    public void Compute_StripsContentHashSuffix()
    {
        var siblings = new[]
        {
            Ws("ws-1", 1, "phase-consensus-engine", "phase-consensus-engine/stage-log-replication@abc12345"),
        };

        Assert.Equal("stage-1.1-log-replication", WorkstreamWorktreeSlug.Compute(siblings[0], siblings));
    }

    /// <summary>
    /// Rows without a <see cref="WorkstreamRow.PlanAnchor"/> (handcrafted
    /// via <c>POST /api/workstreams</c>, or pre-Phase-42 legacy rows that
    /// never went through the reconciler) take the crew-queue ad-hoc shape
    /// <c>ws-{sanitizedBranch}</c> so the path is still unique without
    /// reviving the legacy <c>spawn-</c> prefix.
    /// </summary>
    [Fact]
    public void Compute_NoPlanAnchor_FallsBackToAdHocWsSlug()
    {
        var ws = Ws("ws-handcrafted", 1, phaseAnchor: null, planAnchor: null, branch: "ws/handcrafted-feature");
        var siblings = new[] { ws };

        var slug = WorkstreamWorktreeSlug.Compute(ws, siblings);

        Assert.StartsWith("ws-", slug);
        Assert.DoesNotContain("spawn-", slug);
        Assert.Contains("handcrafted-feature", slug);
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
        Assert.Contains("some-stage", slug);
    }
}
