// -----------------------------------------------------------------------
// <copyright file="Phase59COwningProjectTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories.ImpactedTests.Roslyn;
using Xunit;

/// <summary>
/// Phase 59 (2026-05-21 fix) — owning-project discovery for the Roslyn
/// provider's no-solution path. Repros the FRP <c>update</c> repo shape:
/// hundreds of csprojs, ZERO <c>.sln</c> files. Before the fix, Roslyn
/// loaded "the first csproj found" — an arbitrary project that doesn't
/// contain the changed test — so the load produced nothing and the chain
/// fell back to the project-level heuristic (which graded a one-class
/// migration against the whole 600-test project). These tests pin that
/// the loader now anchors on the project(s) that actually OWN the changed
/// files, which is what enables method-level <c>--filter</c> narrowing.
/// </summary>
public class Phase59COwningProjectTests
{
    private static string NewRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59c-owning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteFile(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void FindOwningCsprojs_PicksNearestCsproj_ForChangedTestFile()
    {
        var root = NewRepo();
        // No .sln anywhere (the update-repo shape). One test project deep
        // in the tree plus an unrelated project elsewhere.
        WriteFile(Path.Combine(root, "src", "Svc", "IntegrationTest", "Foo.IntegrationTest.csproj"));
        WriteFile(Path.Combine(root, "src", "Svc", "IntegrationTest", "Source", "CacheControllerIntegrationTests.cs"));
        WriteFile(Path.Combine(root, "src", "Other", "Other.csproj"));

        var owning = RoslynImpactedTestProvider.FindOwningCsprojs(
            root,
            new[] { "src/Svc/IntegrationTest/Source/CacheControllerIntegrationTests.cs" });

        Assert.Single(owning);
        Assert.EndsWith("Foo.IntegrationTest.csproj", owning[0]);
    }

    [Fact]
    public void FindOwningCsprojs_DedupesAndCollectsMultiple_ButOnlyCsFiles()
    {
        var root = NewRepo();
        WriteFile(Path.Combine(root, "tests", "A", "A.Tests.csproj"));
        WriteFile(Path.Combine(root, "tests", "A", "OneTests.cs"));
        WriteFile(Path.Combine(root, "tests", "A", "TwoTests.cs"));   // same project → de-duped
        WriteFile(Path.Combine(root, "src", "B", "B.csproj"));
        WriteFile(Path.Combine(root, "src", "B", "Widget.cs"));

        var owning = RoslynImpactedTestProvider.FindOwningCsprojs(
            root,
            new[]
            {
                "tests/A/OneTests.cs",
                "tests/A/TwoTests.cs",
                "src/B/Widget.cs",
                "docs/readme.md",            // non-.cs → ignored
                "tests/A/data.json",         // non-.cs → ignored
            });

        Assert.Equal(2, owning.Count);
        Assert.Contains(owning, p => p.EndsWith("A.Tests.csproj"));
        Assert.Contains(owning, p => p.EndsWith("B.csproj"));
    }

    [Fact]
    public void FindOwningCsprojs_ReturnsEmpty_WhenChangedFileIsUnderNoCsproj()
    {
        var root = NewRepo();
        // A csproj exists, but the changed file lives outside any project
        // directory (e.g. a repo-root doc/script). Walking up stops at the
        // worktree root without a hit.
        WriteFile(Path.Combine(root, "src", "B", "B.csproj"));
        WriteFile(Path.Combine(root, "scripts", "tool.cs"));

        var owning = RoslynImpactedTestProvider.FindOwningCsprojs(
            root, new[] { "scripts/tool.cs" });

        Assert.Empty(owning);
    }

    [Fact]
    public void NearestCsproj_DoesNotEscapeWorktreeRoot()
    {
        var root = NewRepo();
        var nested = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "Thing.cs");
        WriteFile(file);

        // No csproj anywhere under root → null, and the walk must stop at
        // root (never inspect the OS temp dir above it).
        var result = RoslynImpactedTestProvider.NearestCsproj(file, Path.GetFullPath(root));
        Assert.Null(result);
    }
}
