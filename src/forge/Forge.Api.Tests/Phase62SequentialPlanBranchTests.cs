// -----------------------------------------------------------------------
// <copyright file="Phase62SequentialPlanBranchTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 62 — sequential-mode plan-doc branch invariant. Operator-reported
/// 2026-05-14 on <c>local:UPDATE-HISTORY-IN-CL → architecture.md</c>: doc
/// halted at iter 11 with <c>$1316</c> spent and the UI showing "no doc
/// generated", even though 23 <c>plan(architecture.md): iter N</c> commits
/// existed on the legacy shared <c>story/{id}/plan</c> branch and the
/// canonical per-doc branch <c>story/{id}/plan-architecture</c> was empty.
///
/// <para>Root cause: the Phase 55 fix forced the canonical worktree slug
/// (<c>plan-architecture</c>) but several call sites still resolved the
/// commit branch via <c>spawn.PlanBranch ?? "story/{id}/plan"</c>. On
/// sequential-mode spawns <c>spawn.PlanBranch</c> is null (Phase 53 left
/// it unset and stores per-doc branches in <c>PlanDocPrsJson</c>) so the
/// null-coalesce resolved to the legacy shared branch. The architect's
/// worktree was therefore at <c>.worktree/plan-architecture/</c> on disk
/// but checked out on the legacy shared <c>plan</c> branch — every iter's
/// commit landed on <c>plan</c> while the UI / PR-open / accept-artifact
/// flow looked at the canonical per-doc branch and saw nothing.</para>
///
/// <para>Phase 62 fix: every call site that previously inlined
/// <c>spawn.PlanBranch ?? "story/{id}/plan"</c> now calls
/// <see cref="PlanDocStateHelpers.FindDocBranch"/> with the doc key, which
/// returns the canonical per-doc branch in sequential mode and the
/// legacy shared branch in legacy mode. These tests pin the resolution
/// behaviour so a future refactor can't silently re-introduce the
/// divergence.</para>
/// </summary>
public class Phase62SequentialPlanBranchTests
{
    [Fact]
    public void FindDocBranch_LegacyMode_NullPlanBranch_ReturnsLegacySharedBranch()
    {
        // Legacy spawn (PlanDocPrsJson == null) with PlanBranch unset
        // returns the legacy shared `story/{id}/plan` branch, matching
        // the inline expression that lived at every call site pre-Phase-62.
        var spawn = new StorySpawnRow
        {
            Id = "spawn-legacy",
            StoryId = "WI-12345",
            PlanBranch = null,
            PlanDocPrsJson = null,
        };

        var branch = PlanDocStateHelpers.FindDocBranch(spawn, "architecture");

        Assert.Equal("story/WI-12345/plan", branch);
    }

    [Fact]
    public void FindDocBranch_LegacyMode_WithCustomPlanBranch_ReturnsThatBranch()
    {
        // Legacy spawn with a pre-set PlanBranch returns it verbatim
        // (an operator may have customised it; FindDocBranch must not
        // override).
        var spawn = new StorySpawnRow
        {
            Id = "spawn-legacy-custom",
            StoryId = "WI-22222",
            PlanBranch = "custom/branch/name",
            PlanDocPrsJson = null,
        };

        var branch = PlanDocStateHelpers.FindDocBranch(spawn, "architecture");

        Assert.Equal("custom/branch/name", branch);
    }

    [Fact]
    public void FindDocBranch_SequentialMode_EmptyState_ReturnsCanonicalPerDocBranch()
    {
        // Sequential-mode spawn (PlanDocPrsJson set to an empty list, the
        // shape created at spawn-init before any doc has started) returns
        // the canonical per-doc branch `story/{id}/plan-{key}`. This is
        // the headline assertion for the operator-reported regression:
        // PRE-PHASE-62 this returned "story/{id}/plan" (legacy shared)
        // for sequential rows because the inline null-coalesce ignored
        // PlanDocPrsJson.
        var spawn = new StorySpawnRow
        {
            Id = "spawn-seq",
            StoryId = "local:UPDATE-HISTORY-IN-CL",
            PlanBranch = null,
            PlanDocPrsJson = "[]",
        };

        var branch = PlanDocStateHelpers.FindDocBranch(spawn, "architecture");

        // StoryPaths.Safe replaces ':' with '-' so the branch resolves to
        // the canonical "story/local-UPDATE-HISTORY-IN-CL/plan-architecture".
        Assert.Equal("story/local-UPDATE-HISTORY-IN-CL/plan-architecture", branch);
    }

    [Fact]
    public void FindDocBranch_SequentialMode_PersistedEntry_ReturnsPersistedBranch()
    {
        // Sequential-mode spawn with a persisted entry returns the entry's
        // branch (even if it differs from the canonical default — the
        // entry is the source of truth once the orchestrator has anchored
        // it).
        var spawn = new StorySpawnRow
        {
            Id = "spawn-seq-persisted",
            StoryId = "WI-33333",
            PlanBranch = null,
            PlanDocPrsJson =
                @"[{""key"":""architecture"",""branch"":""story/WI-33333/plan-architecture"",""prId"":""pr-1""}," +
                @"{""key"":""tech-spec"",""branch"":null,""prId"":null}," +
                @"{""key"":""implementation-plan"",""branch"":null,""prId"":null}," +
                @"{""key"":""e2e-scenarios"",""branch"":null,""prId"":null}]",
        };

        var archBranch = PlanDocStateHelpers.FindDocBranch(spawn, "architecture");
        var techBranch = PlanDocStateHelpers.FindDocBranch(spawn, "tech-spec");

        Assert.Equal("story/WI-33333/plan-architecture", archBranch);
        // tech-spec hasn't started yet — entry.Branch is null — so the
        // helper falls back to the canonical default for that key.
        Assert.Equal("story/WI-33333/plan-tech-spec", techBranch);
    }

    [Fact]
    public void FindDocBranch_SequentialMode_ImplementationPlan_ReturnsPerDocBranch()
    {
        // Pin the implementation-plan case explicitly because two Phase
        // 62 call sites (ProduceDecompositionAsync, ReadPlanStageBodyAsync)
        // hardcode this docKey. A regression here would silently break
        // workstream decomposition and target-path mining.
        var spawn = new StorySpawnRow
        {
            Id = "spawn-impl-plan",
            StoryId = "WI-44444",
            PlanBranch = null,
            PlanDocPrsJson = "[]",
        };

        var branch = PlanDocStateHelpers.FindDocBranch(spawn, "implementation-plan");

        Assert.Equal("story/WI-44444/plan-implementation-plan", branch);
    }

    [Fact]
    public void FindDocBranch_UnknownDocKey_Throws()
    {
        // FindDocBranch validates the docKey against PlanDocFileName.
        // Callers must pass one of the 4 canonical keys; an unknown key
        // is a programming error worth surfacing loudly.
        var spawn = new StorySpawnRow
        {
            Id = "spawn-bad-key",
            StoryId = "WI-55555",
            PlanDocPrsJson = "[]",
        };

        Assert.Throws<ArgumentException>(() =>
            PlanDocStateHelpers.FindDocBranch(spawn, "not-a-doc-key"));
    }

    [Fact]
    public void FindDocBranch_AllFourCanonicalKeys_ResolveDistinctBranches()
    {
        // Smoke test that each of the four plan docs resolves to a
        // distinct branch under sequential mode (no aliasing).
        var spawn = new StorySpawnRow
        {
            Id = "spawn-four",
            StoryId = "WI-66666",
            PlanBranch = null,
            PlanDocPrsJson = "[]",
        };

        var branches = new[]
        {
            PlanDocStateHelpers.FindDocBranch(spawn, "architecture"),
            PlanDocStateHelpers.FindDocBranch(spawn, "tech-spec"),
            PlanDocStateHelpers.FindDocBranch(spawn, "implementation-plan"),
            PlanDocStateHelpers.FindDocBranch(spawn, "e2e-scenarios"),
        };

        Assert.Equal(4, new HashSet<string>(branches).Count);
        Assert.All(branches, b => Assert.StartsWith("story/WI-66666/plan-", b));
    }

    [Fact]
    public void OperatorRegression_SpawnLocalUpdateHistory_ResolvesToCanonicalArchBranch()
    {
        // Reproduces the operator's exact 2026-05-14 spawn row state
        // (PlanBranch="", PlanDocPrsJson has architecture entry with
        // canonical branch, three siblings with branch=null). Pre-Phase-62
        // this resolved to "story/local-UPDATE-HISTORY-IN-CL/plan"; the
        // 23 iter commits landed on the legacy shared branch instead of
        // plan-architecture. This test pins the FIX so that exact spawn
        // shape now resolves to the canonical per-doc branch.
        var spawn = new StorySpawnRow
        {
            Id = "spawn-local-UPDATE-HISTORY-IN-CL",
            StoryId = "local:UPDATE-HISTORY-IN-CL",
            PlanBranch = string.Empty,
            BaseBranch = "feature/history",
            PlanDocPrsJson =
                @"[{""key"":""architecture"",""branch"":""story/local-UPDATE-HISTORY-IN-CL/plan-architecture"",""prId"":null}," +
                @"{""key"":""tech-spec"",""branch"":null,""prId"":null}," +
                @"{""key"":""implementation-plan"",""branch"":null,""prId"":null}," +
                @"{""key"":""e2e-scenarios"",""branch"":null,""prId"":null}]",
        };

        var branch = PlanDocStateHelpers.FindDocBranch(spawn, "architecture");

        Assert.Equal("story/local-UPDATE-HISTORY-IN-CL/plan-architecture", branch);
        Assert.NotEqual("story/local-UPDATE-HISTORY-IN-CL/plan", branch);
    }
}
