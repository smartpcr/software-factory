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
    public void ScopeKeys_RootLevelFile_ProducesNoKey()
    {
        // Makefile / go.mod at repo root have no directory → no area key
        // (they can't anchor a scope, and shouldn't widen one).
        var keys = WorkstreamScopeGuard.ScopeKeysFromTargets(new[] { "Makefile", "go.mod" });
        Assert.Empty(keys);
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
}
