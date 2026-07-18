// -----------------------------------------------------------------------
// <copyright file="WorkstreamScopeGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Pure unit tests for <see cref="WorkstreamScopeGuard"/> — the deterministic
/// detectors behind the off-task wandering guard (#2) and the inherited-gate
/// distinction (#3) on the story workstream gen/eval loop. All DB-free scalar /
/// string logic, modelled on <c>Phase52SloppyDetectorTests</c>.
/// </summary>
public class WorkstreamScopeGuardTests
{
    // ── ScopeKeysFromTargets — parent-area keys ─────────────────────

    [Fact]
    public void ScopeKeys_ModuleRelativeTarget_YieldsParentArea()
    {
        // web/src/api/types.ts → parent area web/src (admits web/src/components/*).
        var keys = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "web/src/api/types.ts" });
        Assert.Equal(new[] { "web/src" }, keys.ToArray());
    }

    [Fact]
    public void ScopeKeys_RepoRelativeDeepTarget_KeepsFullParentArea()
    {
        // Deep repo-relative path must NOT collapse to the module root
        // (that would let internal/** count as in-scope).
        var keys = WorkstreamScopeGuard.ScopeKeysFromTargets(
            new[] { "services/agent-memory/web/src/api/types.ts" });
        Assert.Contains("services/agent-memory/web/src", keys);
        Assert.DoesNotContain("services/agent-memory", keys);
    }

    [Fact]
    public void ScopeKeys_RootLevelTargetsOnly_IsEmpty_FailOpen()
    {
        // Root-level targets (Makefile, go.mod) accompany every kind of work
        // and carry no scoping signal alone — a root-only scope must read as
        // "unknown" so the guard stays disarmed (release RELEASE-PROVIDER
        // stage 4.2, 2026-07-17: targets extracted to root files only → scope
        // {.} armed the guard → the stage's actual internal/pattern/** work
        // counted 100% off-scope and stalled at iter 2).
        var keys = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "Makefile", "go.mod" });
        Assert.Empty(keys);
    }

    [Fact]
    public void IsOffTask_RootOnlyTargets_SubdirWork_DoesNotTrip()
    {
        // Regression for stage 4.2 (see above): root-only targets → guard
        // disarmed → deep subdirectory work never reads as off-task.
        var scope = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "go.mod", "main.go" });
        var changed = new[]
        {
            "internal/pattern/windows_service.go",
            "internal/pattern/windows_service_golden_test.go",
            "internal/pattern/testdata/winsw_windows.xml",
            "internal/pattern/testdata/s4_configure_fresh_windows.ps1",
        };
        Assert.False(WorkstreamScopeGuard.IsOffTask(changed, scope, out _, out _));
        Assert.Equal(0, WorkstreamScopeGuard.CountOutOfScope(changed, scope));
    }

    [Fact]
    public void ScopeKeys_MixedRootAndSubdirTargets_KeepBoth()
    {
        var keys = WorkstreamScopeGuard.ScopeKeysFromTargets(
            new[] { "main.go", "internal/provider/provider.go", "examples/basic/main.tf" });
        Assert.Contains(WorkstreamScopeGuard.RepoRootScopeKey, keys);
        Assert.Contains("internal", keys);
        Assert.Contains("examples", keys);
    }

    [Fact]
    public void ScopeKeys_SingleSegmentDir_UsesItDirectly()
    {
        var keys = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "web/types.ts" });
        Assert.Equal(new[] { "web" }, keys.ToArray());
    }

    [Fact]
    public void ScopeKeys_NullOrEmpty_IsEmpty_FailOpen()
    {
        Assert.Empty(WorkstreamScopeGuard.ScopeKeysFromTargets(null));
        Assert.Empty(WorkstreamScopeGuard.ScopeKeysFromTargets(Array.Empty<string>()));
        Assert.Empty(WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "  ", "" }));
    }

    // ── IsInScope — path-segment containment, relativity-tolerant ────

    [Fact]
    public void IsInScope_EmptyScope_AlwaysTrue_FailOpen()
    {
        var empty = new HashSet<string>();
        Assert.True(WorkstreamScopeGuard.IsInScope("anything/at/all.go", empty));
    }

    [Fact]
    public void IsInScope_MatchesAcrossRelativityMismatch()
    {
        // Scope key is module-relative (web/src); changed path is repo-relative.
        var scope = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "web/src/api/types.ts" });
        Assert.True(WorkstreamScopeGuard.IsInScope("services/agent-memory/web/src/api/types.ts", scope));
        Assert.True(WorkstreamScopeGuard.IsInScope("services/agent-memory/web/src/components/Graph3D.tsx", scope));
        Assert.False(WorkstreamScopeGuard.IsInScope("services/agent-memory/internal/graphsink/sqlite/schema.sql", scope));
        Assert.False(WorkstreamScopeGuard.IsInScope("Makefile", scope));
    }

    [Fact]
    public void IsInScope_RootLevelChange_AdmittedOnlyByRepoRootKey()
    {
        // Without a declared root-level target, root files stay out of scope…
        var subdirOnly = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "internal/provider/provider.go" });
        Assert.False(WorkstreamScopeGuard.IsInScope("go.sum", subdirOnly));

        // …with one, they're in scope; subdir foreign modules still are not.
        var withRoot = WorkstreamScopeGuard.ScopeKeysFromTargets(
            new[] { "main.go", "internal/provider/provider.go" });
        Assert.True(WorkstreamScopeGuard.IsInScope("go.sum", withRoot));
        Assert.True(WorkstreamScopeGuard.IsInScope("Makefile", withRoot));
        Assert.True(WorkstreamScopeGuard.IsInScope(".goreleaser.yml", withRoot));
        Assert.False(WorkstreamScopeGuard.IsInScope("cmd/other/main.go", withRoot));
    }

    [Fact]
    public void IsOffTask_BootstrapStageEditingRootDeliverables_DoesNotTrip()
    {
        // Regression: release RELEASE-PROVIDER stage 1.1 "Repository Bootstrap
        // and Toolchain" — declared targets mixed root files (main.go) with
        // internal/examples paths; the agent's iter edited exactly its
        // unchecked plan steps (go.sum, Makefile, .goreleaser.yml, CI) and the
        // guard read ~100% off-scope → OffTaskException at iter 1, wedging the
        // 55 downstream workstreams.
        var scope = WorkstreamScopeGuard.ScopeKeysFromTargets(new[]
        {
            "main.go", "internal/provider/provider.go", "examples/basic/main.tf",
        });
        var changed = new[]
        {
            "Makefile", ".goreleaser.yml", "go.mod", "go.sum", "main.go", "main_test.go",
        };
        Assert.False(WorkstreamScopeGuard.IsOffTask(changed, scope, out var ratio, out _));
        Assert.Equal(0, ratio);
    }

    // ── NormalizeEditedPath — tool-call paths → worktree-relative ────

    [Fact]
    public void NormalizeEditedPath_StripsWorktreePrefix_AndDedupesWithRelative()
    {
        const string ws = @"C:\forge\release\.worktree\stage-1.1-repository-bootstrap-and-toolchain";
        Assert.Equal("Makefile", WorkstreamScopeGuard.NormalizeEditedPath(ws + @"\Makefile", ws));
        Assert.Equal(
            "internal/provider/provider.go",
            WorkstreamScopeGuard.NormalizeEditedPath(ws + @"\internal\provider\provider.go", ws));
        // Already-relative and foreign-absolute paths pass through (normalized).
        Assert.Equal("Makefile", WorkstreamScopeGuard.NormalizeEditedPath("Makefile", ws));
        Assert.Equal(
            "D:/elsewhere/Makefile",
            WorkstreamScopeGuard.NormalizeEditedPath(@"D:\elsewhere\Makefile", ws));
        Assert.Equal("go.mod", WorkstreamScopeGuard.NormalizeEditedPath("go.mod", null));
    }

    // ── IsOffTask — the #2 trip condition ───────────────────────────

    [Fact]
    public void IsOffTask_DisabledWhenScopeUnknown()
    {
        var empty = new HashSet<string>();
        var changed = new[] { "a/x.go", "b/y.go", "c/z.go" };
        Assert.False(WorkstreamScopeGuard.IsOffTask(changed, empty, out _, out _));
    }

    [Fact]
    public void IsOffTask_DisabledOnTinyDiff()
    {
        var scope = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "web/src/api/types.ts" });
        // Only 2 changed files (< OffTaskMinChangedFiles=3) → never trips,
        // even though both are out of scope.
        var changed = new[] { "internal/a.go", "internal/b.go" };
        Assert.False(WorkstreamScopeGuard.IsOffTask(changed, scope, out _, out _));
    }

    [Fact]
    public void IsOffTask_InScopeDiff_DoesNotTrip()
    {
        var scope = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "web/src/api/types.ts" });
        var changed = new[]
        {
            "services/agent-memory/web/src/api/types.ts",
            "services/agent-memory/web/src/api/types.test.ts",
            "services/agent-memory/web/src/components/Graph3D.tsx",
        };
        Assert.False(WorkstreamScopeGuard.IsOffTask(changed, scope, out var ratio, out var off));
        Assert.Equal(0, off);
        Assert.Equal(0.0, ratio);
    }

    [Fact]
    public void IsOffTask_BackendChurnOnFrontendTask_Trips()
    {
        // The 3.1 pathology: a web/src task whose iter diff is entirely backend.
        var scope = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "web/src/api/types.ts" });
        var changed = new[]
        {
            "services/agent-memory/go.mod",
            "services/agent-memory/go.sum",
            "services/agent-memory/internal/graphsink/sqlite/schema.sql",
            "services/agent-memory/Makefile",
        };
        Assert.True(WorkstreamScopeGuard.IsOffTask(changed, scope, out var ratio, out var off));
        Assert.Equal(4, off);
        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void IsOffTask_MajorityOutOfScope_TripsAtThreshold()
    {
        var scope = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "web/src/api/types.ts" });
        // 4 of 5 out of scope = 0.8 ratio, exactly at OffTaskRatioThreshold.
        var changed = new[]
        {
            "web/src/api/types.ts",     // 1 in scope
            "internal/a.go",
            "internal/b.go",
            "cmd/c.go",
            "Makefile",
        };
        Assert.True(WorkstreamScopeGuard.IsOffTask(changed, scope, out var ratio, out var off));
        Assert.Equal(4, off);
        Assert.True(ratio >= WorkstreamScopeGuard.OffTaskRatioThreshold);
    }

    // ── ExtractFailingFiles ─────────────────────────────────────────

    [Fact]
    public void ExtractFailingFiles_PullsTestFileBasenames()
    {
        var summary =
            "[module services/agent-memory] FAILED TESTS (2):\n"
            + "  probe_harness_gate_test.go:175: make probe-self exited 2, want 0\n"
            + "  services/agent-memory/internal/mcp/transport/e2esuite/suite.go:640: raw grep\n";
        var files = WorkstreamScopeGuard.ExtractFailingFiles(summary);
        Assert.Contains("probe_harness_gate_test.go", files);
        Assert.Contains("suite.go", files);
    }

    [Fact]
    public void ExtractFailingFiles_EmptyOrNull_IsEmpty()
    {
        Assert.Empty(WorkstreamScopeGuard.ExtractFailingFiles(null));
        Assert.Empty(WorkstreamScopeGuard.ExtractFailingFiles(""));
        Assert.Empty(WorkstreamScopeGuard.ExtractFailingFiles("no file tokens here, just prose"));
    }

    // ── IsInheritedGateFailure — the #3 distinction ─────────────────

    [Fact]
    public void IsInherited_FailingTestNotInDiff_IsInherited()
    {
        // 3.1: failing tests live in agent-memory; the ws only changed web/src.
        var failing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "probe_harness_gate_test.go", "suite.go",
        };
        var changed = new[]
        {
            "services/agent-memory/web/src/api/types.ts",
            "services/agent-memory/web/src/api/types.test.ts",
        };
        Assert.True(WorkstreamScopeGuard.IsInheritedGateFailure(failing, changed, out var list));
        Assert.Contains("probe_harness_gate_test.go", list);
    }

    [Fact]
    public void IsInherited_FailingTestInDiff_IsOwned()
    {
        var failing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "isolation_test.go" };
        var changed = new[] { "services/agent-memory/internal/mcp/transport/isolation_test.go" };
        Assert.False(WorkstreamScopeGuard.IsInheritedGateFailure(failing, changed, out _));
    }

    [Fact]
    public void IsInherited_Unknowns_AreTreatedAsOwned()
    {
        // Can't tell → never mislabel a real regression as inherited.
        Assert.False(WorkstreamScopeGuard.IsInheritedGateFailure(
            new HashSet<string>(), new[] { "a.go" }, out _));
        Assert.False(WorkstreamScopeGuard.IsInheritedGateFailure(
            new HashSet<string> { "x.go" }, Array.Empty<string>(), out _));
    }

    // ── (a) mutating tool / edited-path capture ─────────────────────

    [Theory]
    [InlineData("edit", true)]
    [InlineData("create", true)]
    [InlineData("str_replace", true)]
    [InlineData("apply_patch", true)]
    [InlineData("multiEdit", true)]
    [InlineData("write_file", true)]
    [InlineData("view", false)]
    [InlineData("grep", false)]
    [InlineData("read_file", false)]
    [InlineData("ls", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMutatingTool_ClassifiesEditsVsReads(string? tool, bool expected)
    {
        Assert.Equal(expected, WorkstreamScopeGuard.IsMutatingTool(tool));
    }

    [Fact]
    public void TryExtractEditedPath_PullsPathFromMutatingToolPayload()
    {
        Assert.Equal("services/agent-memory/go.mod",
            WorkstreamScopeGuard.TryExtractEditedPath("edit", "{\"path\":\"services/agent-memory/go.mod\"}"));
        Assert.Equal("web/src/api/types.ts",
            WorkstreamScopeGuard.TryExtractEditedPath("create", "{\"filePath\":\"web/src/api/types.ts\",\"content\":\"x\"}"));
    }

    [Fact]
    public void TryExtractEditedPath_NullForReadsAndJunk()
    {
        Assert.Null(WorkstreamScopeGuard.TryExtractEditedPath("view", "{\"path\":\"a.go\"}"));   // read tool
        Assert.Null(WorkstreamScopeGuard.TryExtractEditedPath("edit", "not json"));               // malformed
        Assert.Null(WorkstreamScopeGuard.TryExtractEditedPath("edit", "\"a string\""));           // non-object
        Assert.Null(WorkstreamScopeGuard.TryExtractEditedPath("edit", "{\"foo\":\"bar\"}"));      // no path key
        Assert.Null(WorkstreamScopeGuard.TryExtractEditedPath("edit", null));
    }

    [Fact]
    public void CountOutOfScope_CountsBackendThrashHiddenByCommit_AndCrossesThreshold()
    {
        // The 3.2 case: web/src scope, but the agent's mutating edits hit the
        // backend (go.mod, schema.sql, serve_module.go, go.sum, serve_test.go) —
        // which the committed diff (mostly web/) never surfaced.
        var scope = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "web/src/components/Graph3DCanvas.tsx" });
        var edited = new[]
        {
            "services/agent-memory/web/src/components/Graph3DCanvas.tsx", // in scope — excluded
            "services/agent-memory/go.mod",
            "services/agent-memory/go.sum",
            "services/agent-memory/internal/graphsink/sqlite/schema.sql",
            "services/agent-memory/cmd/agent-mcp/serve_module.go",
            "services/agent-memory/cmd/agent-mcp/serve_test.go",
        };
        var outOfScope = WorkstreamScopeGuard.CountOutOfScope(edited, scope);
        Assert.Equal(5, outOfScope); // the 5 backend files; the web file is in scope
        Assert.True(outOfScope >= WorkstreamScopeGuard.OffTaskOutOfScopeEditThreshold,
            "5 out-of-scope backend edits should trip the off-task edit threshold");
    }

    [Fact]
    public void CountOutOfScope_ZeroWhenScopeUnknown_FailOpen()
    {
        Assert.Equal(0, WorkstreamScopeGuard.CountOutOfScope(
            new[] { "internal/a.go", "internal/b.go" }, new HashSet<string>()));
    }
}
