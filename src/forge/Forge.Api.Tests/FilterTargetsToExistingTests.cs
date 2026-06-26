// -----------------------------------------------------------------------
// <copyright file="FilterTargetsToExistingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.IO;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Tests for <see cref="CrewStoryArtifactStrategy.FilterTargetsToExisting"/> — the
/// #(b) fix that drops declared target paths whose directory doesn't exist in the
/// worktree, so a hallucinated path (e.g. a .NET-style <c>src/CodeIntelligence</c>
/// in a Go repo) can't define a phantom off-task scope that stalls on-task work.
/// </summary>
public class FilterTargetsToExistingTests
{
    [Fact]
    public void KeepsReal_DropsHallucinated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ws-targets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "services", "agent-memory", "internal", "behavioral", "summary"));
        File.WriteAllText(Path.Combine(root, "services", "agent-memory", "internal", "behavioral", "summary", "disclosure.go"), "package summary");
        try
        {
            var targets = new[]
            {
                "services/agent-memory/internal/behavioral/summary/disclosure.go", // exists
                "services/agent-memory/internal/behavioral/summary/store.go",       // new file, dir exists
                "src/CodeIntelligence/Disclose.cs",                                  // hallucinated — dir missing
            };
            var kept = CrewStoryArtifactStrategy.FilterTargetsToExisting(root, targets);
            Assert.Contains("services/agent-memory/internal/behavioral/summary/disclosure.go", kept);
            Assert.Contains("services/agent-memory/internal/behavioral/summary/store.go", kept);
            Assert.DoesNotContain("src/CodeIntelligence/Disclose.cs", kept);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void AllHallucinated_YieldsEmpty_SoScopeIsUnknown_FailOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ws-targets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var kept = CrewStoryArtifactStrategy.FilterTargetsToExisting(
                root, new[] { "src/CodeIntelligence/A.cs", "src/CodeIntelligence/B.cs" });
            Assert.Empty(kept);
            // empty targets → ScopeKeysFromTargets returns empty → guard disabled
            Assert.Empty(WorkstreamScopeGuard.ScopeKeysFromTargets(kept));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void MissingWorktree_ReturnsInputUnchanged_DontSilentlyDisable()
    {
        var targets = new[] { "a/b.go", "c/d.go" };
        var kept = CrewStoryArtifactStrategy.FilterTargetsToExisting(
            @"Z:\does\not\exist\worktree", targets);
        Assert.Equal(targets, kept); // can't verify → leave the guard's input intact
    }
}
