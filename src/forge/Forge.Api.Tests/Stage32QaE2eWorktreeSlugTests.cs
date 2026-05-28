// -----------------------------------------------------------------------
// <copyright file="Stage32QaE2eWorktreeSlugTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 3.2 — pin the qa-e2e worktree slug shape so
/// the swarm UI's stacked impl-over-e2e pair lines up visually and the
/// <c>.worktree/</c> path is predictable on disk. qa-e2e rows must:
///
/// <list type="number">
///   <item>Shadow their impl sibling's <c>{phaseNum}.{stageNum}</c>
///         numbering with a trailing <c>-e2e</c> suffix (NOT bump the
///         impl-stage count by occupying their own bucket slot).</item>
///   <item>Stay collision-free across an entire 20-stage spawn.</item>
///   <item>Fit comfortably inside the OS <c>MAX_PATH</c> budget so the
///         <c>{cloneRoot}/.worktree/{slug}</c> path doesn't trip the
///         Win32 260-char wall on a realistic stage title.</item>
/// </list>
///
/// <para>All three Facts are pure-helper (no boot, no DB, no SignalR).</para>
/// </summary>
public class Stage32QaE2eWorktreeSlugTests
{
    private static WorkstreamRow Ws(
        string id,
        int sort,
        string? phaseAnchor,
        string? planAnchor,
        string category = WorkstreamCategories.Impl,
        string branch = "",
        string? storySpawnId = "spawn-stage32")
        => new()
        {
            Id = id,
            Sort = sort,
            PhaseAnchor = phaseAnchor,
            PlanAnchor = planAnchor,
            Category = category,
            Branch = branch,
            StorySpawnId = storySpawnId,
        };

    // ── 1. [unit] qa-e2e slug shape: stage-N.M-{stageSlug}-e2e ───────────

    [Fact]
    public void Compute_QaE2e_Row_Shadows_Impl_Sibling_Position_With_E2e_Suffix()
    {
        // Phase 3 / stage 3.3 + its qa-e2e companion. Both rows sit in
        // the same PhaseAnchor; the qa-e2e immediately follows the impl
        // by Sort. Expected: impl=`stage-3.3-log-replication`,
        // qa-e2e=`stage-3.3-log-replication-e2e`.
        var siblings = new[]
        {
            Ws("ws-1", 1, "phase-foundation", "phase-foundation/stage-bootstrap"),
            Ws("ws-2", 2, "phase-rpc", "phase-rpc/stage-message-types"),
            Ws("ws-3", 3, "phase-consensus-engine", "phase-consensus-engine/stage-leader-election"),
            Ws("ws-4", 4, "phase-consensus-engine", "phase-consensus-engine/stage-append-entries"),
            Ws("ws-5", 5, "phase-consensus-engine", "phase-consensus-engine/stage-log-replication"),
            Ws("ws-5-e2e", 6, "phase-consensus-engine", "phase-consensus-engine/stage-log-replication/e2e",
                category: WorkstreamCategories.QaE2e),
        };

        var implSlug = WorkstreamWorktreeSlug.Compute(siblings[4], siblings);
        var qaSlug = WorkstreamWorktreeSlug.Compute(siblings[5], siblings);

        Assert.Equal("stage-3.3-log-replication", implSlug);
        Assert.Equal("stage-3.3-log-replication-e2e", qaSlug);

        // Sanity: qa-e2e slug is impl + `-e2e` literal suffix — no
        // phase/stage renumbering, no embedded `/e2e` segment from the
        // anchor leaking through.
        Assert.Equal(implSlug + "-e2e", qaSlug);
        Assert.DoesNotContain("/", qaSlug);
        Assert.DoesNotContain("3.4", qaSlug);
    }

    [Fact]
    public void Compute_QaE2e_Row_FallsThrough_When_Impl_Sibling_Missing_FromList()
    {
        // Defensive fall-through: qa-e2e target present but its impl
        // sibling isn't in the spawn list (production never hits this;
        // tests / direct callers might). The slug still surfaces the
        // -e2e suffix derived from the anchor's `/e2e` segment, and
        // shadows the most-recent impl's stage number in the phase.
        var siblings = new[]
        {
            Ws("ws-1", 1, "phase-foo", "phase-foo/stage-alpha"),
            Ws("ws-1-e2e", 2, "phase-foo", "phase-foo/stage-alpha/e2e",
                category: WorkstreamCategories.QaE2e),
            // Note: the impl sibling for ws-2-e2e is INTENTIONALLY absent.
            Ws("ws-2-e2e", 3, "phase-foo", "phase-foo/stage-beta/e2e",
                category: WorkstreamCategories.QaE2e),
        };

        var qaOrphan = WorkstreamWorktreeSlug.Compute(siblings[2], siblings);

        Assert.StartsWith("stage-", qaOrphan);
        Assert.EndsWith("-e2e", qaOrphan);
        Assert.Contains("beta", qaOrphan);
        // The orphan's stage number falls back to the impl-bucket count
        // (1 in this case — only one impl in phase-foo).
        Assert.Equal("stage-1.1-beta-e2e", qaOrphan);
    }

    // ── 2. [unit] Slug uniqueness across a 20-stage spawn ────────────────

    [Fact]
    public void Compute_Slug_Is_Unique_Across_TwentyStage_Story_With_QaE2e_Companions()
    {
        // Maximum spawn size per the spec: 20 stages, each in its own
        // phase + its own qa-e2e companion (40 workstreams total). Verify
        // every Compute result is distinct so two workstreams never
        // collide on the same `.worktree/` leaf — the invariant
        // `docs/forge_implementation.md:47-58` calls out.
        const int StageCount = 20;
        var siblings = new List<WorkstreamRow>(StageCount * 2);
        for (var i = 1; i <= StageCount; i++)
        {
            var phaseAnchor = $"phase-{i}-feature";
            var implAnchor = $"{phaseAnchor}/stage-stage{i}-impl";
            var qaAnchor = $"{implAnchor}/e2e";
            siblings.Add(Ws($"ws-{i}-impl", i * 2 - 1, phaseAnchor, implAnchor));
            siblings.Add(Ws($"ws-{i}-e2e", i * 2, phaseAnchor, qaAnchor,
                category: WorkstreamCategories.QaE2e));
        }

        var slugs = siblings.Select(w => WorkstreamWorktreeSlug.Compute(w, siblings)).ToList();

        Assert.Equal(siblings.Count, slugs.Count);
        Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.Ordinal).Count());

        // Spot-check the pairing: every impl slug + "-e2e" appears as
        // the matching qa-e2e slug.
        for (var i = 0; i < StageCount; i++)
        {
            var implSlug = slugs[i * 2];
            var qaSlug = slugs[i * 2 + 1];
            Assert.Equal(implSlug + "-e2e", qaSlug);
        }

        // And every slug starts with the canonical prefix (no legacy
        // `spawn-` regression, no hand-crafted `ws-` fallback path
        // sneaking in for a plan-anchored row).
        Assert.All(slugs, s => Assert.StartsWith("stage-", s));
    }

    [Fact]
    public void Compute_QaE2e_Does_Not_Renumber_Following_Impl_Stages()
    {
        // Phase with 3 impls where the second impl has a qa-e2e
        // companion sitting in the middle of the Sort order. The third
        // impl MUST still be stage-1.3 (not stage-1.4) — the qa-e2e
        // doesn't bump the impl bucket position.
        var siblings = new[]
        {
            Ws("ws-1", 1, "phase-alpha", "phase-alpha/stage-first"),
            Ws("ws-2", 2, "phase-alpha", "phase-alpha/stage-second"),
            Ws("ws-2-e2e", 3, "phase-alpha", "phase-alpha/stage-second/e2e",
                category: WorkstreamCategories.QaE2e),
            Ws("ws-3", 4, "phase-alpha", "phase-alpha/stage-third"),
        };

        Assert.Equal("stage-1.1-first", WorkstreamWorktreeSlug.Compute(siblings[0], siblings));
        Assert.Equal("stage-1.2-second", WorkstreamWorktreeSlug.Compute(siblings[1], siblings));
        Assert.Equal("stage-1.2-second-e2e", WorkstreamWorktreeSlug.Compute(siblings[2], siblings));
        Assert.Equal("stage-1.3-third", WorkstreamWorktreeSlug.Compute(siblings[3], siblings));
    }

    // ── 3. [unit] Slug stays within OS MAX_PATH budget ───────────────────

    [Fact]
    public void Compute_QaE2e_Slug_Stays_Under_MaxPath_For_Realistic_Stage_Titles()
    {
        // Realistic upper-bound stage title: 60 chars of kebab-case
        // (e.g. the longest stage names we've seen in production are
        // ~50 chars; round up to 60 for headroom). The slug shape is
        // `stage-{N.M}-{stageTitle}-e2e`; verify the resulting slug fits
        // comfortably inside the headroom needed for the surrounding
        // `{cloneRoot}/.worktree/{slug}` path on Windows.
        //
        // Win32 MAX_PATH is 260 (without long-path mode). A typical
        // cloneRoot like `C:\dev\projects\forge` is ~24 chars; plus
        // `\.worktree\` is 11 chars; plus an inner test-file path of
        // ~60 chars after the worktree leaf. So the worktree leaf itself
        // should comfortably stay under ~100 chars to leave room for
        // file paths inside the worktree.
        const int MaxComfortablePathSegment = 100;
        const int RealisticStageTitleLength = 60;

        var stageTitle = string.Join("-", Enumerable.Range(0, RealisticStageTitleLength / 8)
            .Select(i => $"word{i:D2}"));
        Assert.True(stageTitle.Length >= 40 && stageTitle.Length <= RealisticStageTitleLength,
            $"Test bench stage title is {stageTitle.Length} chars; expected ~40-60.");

        // Spawn the worst-case position: phase 20, stage 20. (Two-digit
        // phase + two-digit stage adds 5 chars to the prefix.) A qa-e2e
        // companion at that position is the longest plausible shape.
        var implAnchor = $"phase-twenty/stage-{stageTitle}";
        var qaAnchor = $"{implAnchor}/e2e";

        // Build 20 phases each with one impl + one qa-e2e so the target
        // qa-e2e lands at phase 20 in ComputePosition's bucketing.
        var siblings = new List<WorkstreamRow>();
        for (var i = 1; i <= 19; i++)
        {
            var pa = $"phase-{i}-filler";
            siblings.Add(Ws($"ws-{i}-impl", i * 3 - 2, pa, $"{pa}/stage-stage{i}"));
            siblings.Add(Ws($"ws-{i}-e2e", i * 3 - 1, pa, $"{pa}/stage-stage{i}/e2e",
                category: WorkstreamCategories.QaE2e));
        }
        // Add 19 more impls under phase-twenty so the target qa-e2e
        // shadows position 20.20 (the worst-case stage number).
        for (var s = 1; s <= 19; s++)
        {
            siblings.Add(Ws($"ws-20-impl-{s}", 100 + s, "phase-twenty", $"phase-twenty/stage-prefix{s}"));
        }
        // Target: stage 20 within phase-twenty + qa-e2e companion.
        var implTarget = Ws("ws-20-impl-20", 200, "phase-twenty", implAnchor);
        var qaTarget = Ws("ws-20-e2e-20", 201, "phase-twenty", qaAnchor,
            category: WorkstreamCategories.QaE2e);
        siblings.Add(implTarget);
        siblings.Add(qaTarget);

        var implSlug = WorkstreamWorktreeSlug.Compute(implTarget, siblings);
        var qaSlug = WorkstreamWorktreeSlug.Compute(qaTarget, siblings);

        // Headline: qa-e2e slug stays under our comfortable segment budget.
        Assert.True(qaSlug.Length <= MaxComfortablePathSegment,
            $"qa-e2e worktree slug is {qaSlug.Length} chars (budget {MaxComfortablePathSegment}): {qaSlug}");

        // And under the absolute Win32 MAX_PATH (260) with massive
        // headroom — this is the hard wall, not the comfortable one.
        const int Win32MaxPath = 260;
        Assert.True(qaSlug.Length < Win32MaxPath,
            $"qa-e2e worktree slug is {qaSlug.Length} chars; exceeds Win32 MAX_PATH={Win32MaxPath}.");

        // Sanity: the slug carries the expected shape pieces.
        Assert.StartsWith("stage-20.20-", implSlug);
        Assert.StartsWith("stage-20.20-", qaSlug);
        Assert.EndsWith("-e2e", qaSlug);
        Assert.Equal(implSlug + "-e2e", qaSlug);
    }

    [Fact]
    public void QaE2eWorkspacePhaseTag_Uses_Canonical_Slug_Instead_Of_Long_Workstream_Id()
    {
        const string LongWorkstreamId =
            "ws-local-update-history-in-cl-phase-clusterdb-substrate-scaffolding-stage-implement-clusterdbpersistenceprovider-base-class-e2e";
        var impl = Ws(
            "ws-local-update-history-in-cl-phase-clusterdb-substrate-scaffolding-stage-implement-clusterdbpersistenceprovider-base-class",
            1,
            "phase-clusterdb-substrate-scaffolding",
            "phase-clusterdb-substrate-scaffolding/stage-implement-clusterdbpersistenceprovider-base-class");
        var qa = Ws(
            LongWorkstreamId,
            2,
            "phase-clusterdb-substrate-scaffolding",
            "phase-clusterdb-substrate-scaffolding/stage-implement-clusterdbpersistenceprovider-base-class/e2e",
            category: WorkstreamCategories.QaE2e);
        var siblings = new[] { impl, qa };

        var phaseTag = CrewStoryArtifactStrategy.QaE2eWorkspacePhaseTag(qa, siblings);
        var checkpointPath = Path.Combine(
            "E:\\forge\\storyworkspaces\\spawn-local-UPDATE-HISTORY-IN-CL",
            phaseTag,
            ".forge",
            "memory",
            "session-state",
            Guid.Empty.ToString(),
            "checkpoints",
            "index.md");

        Assert.Equal("qa-e2e-stage-1.1-implement-clusterdbpersistenceprovider-base-class-e2e", phaseTag);
        Assert.DoesNotContain(LongWorkstreamId, phaseTag);
        Assert.True(checkpointPath.Length < 260, $"checkpoint path length={checkpointPath.Length}: {checkpointPath}");
    }
}
