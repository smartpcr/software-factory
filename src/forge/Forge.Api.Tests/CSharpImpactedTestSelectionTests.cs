// -----------------------------------------------------------------------
// <copyright file="CSharpImpactedTestSelectionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories.ImpactedTests.Lsp;
using Forge.Api.Services.Stories.ImpactedTests.Lsp.Languages;
using Xunit;

/// <summary>
/// Proves the C# LSP impacted-test picking is correct: which references
/// count as tests (and the FQN / owning project derived for them), and
/// how the matched set narrows the <c>dotnet test</c> command. These are
/// the deterministic core of "impacted tests are correctly picked" — they
/// don't need a live csharp-ls (the LSP plumbing only feeds reference
/// positions into <see cref="CSharpLanguageSupport.IsTestReference"/>).
/// </summary>
public sealed class CSharpImpactedTestSelectionTests
{
    private static readonly CSharpLanguageSupport Support = new();

    private static string[] TestClassSource(string attribute) => new[]
    {
        "namespace MyApp.Tests;",                          // 0
        "",                                                // 1
        "public class WidgetTests",                        // 2
        "{",                                               // 3
        $"    {attribute}",                                // 4
        "    public void Computes_Total()",                // 5
        "    {",                                            // 6
        "        var w = new Widget();",                   // 7 <- reference here
        "        Assert.Equal(3, w.Total());",             // 8
        "    }",                                            // 9
        "}",                                               // 10
    };

    [Theory]
    [InlineData("[Fact]")]
    [InlineData("[Theory]")]
    [InlineData("[Test]")]
    [InlineData("[TestMethod]")]
    [InlineData("[DataTestMethod]")]
    [InlineData("[TestCase(1, 2)]")]
    [InlineData("[Xunit.Fact]")]
    public void IsTestReference_PicksTestMethod_AcrossFrameworks(string attribute)
    {
        var lines = TestClassSource(attribute);

        // A reference to the changed symbol on line 7 (inside the test body).
        var hit = Support.IsTestReference("/repo/tests/WidgetTests.cs", lines, line: 7, character: 16);

        Assert.NotNull(hit);
        Assert.Equal("MyApp.Tests.WidgetTests.Computes_Total", hit!.TestName);
        Assert.Equal("csharp", hit.LanguageId);
    }

    [Fact]
    public void IsTestReference_RejectsNonTestMethod()
    {
        var lines = new[]
        {
            "namespace MyApp;",
            "public class Helper",
            "{",
            "    public void DoWork()",        // no test attribute
            "    {",
            "        var w = new Widget();",   // line 5 — reference, but not a test
            "    }",
            "}",
        };

        var hit = Support.IsTestReference("/repo/src/Helper.cs", lines, line: 5, character: 16);

        Assert.Null(hit);
    }

    [Fact]
    public void IsTestReference_DoesNotBorrowSiblingMethodAttribute()
    {
        // The reference is inside `Helper` (no attribute), which sits below a
        // DIFFERENT [Fact] method. The walk-up must stop at `Helper` and not
        // pick up the earlier test's attribute.
        var lines = new[]
        {
            "public class Mixed",                  // 0
            "{",                                    // 1
            "    [Fact]",                           // 2
            "    public void RealTest() { }",       // 3
            "",                                     // 4
            "    public void Helper()",             // 5  (no attribute)
            "    {",                                 // 6
            "        var w = new Widget();",        // 7  <- reference, NOT a test
            "    }",                                 // 8
            "}",                                     // 9
        };

        var hit = Support.IsTestReference("/repo/src/Mixed.cs", lines, line: 7, character: 16);

        Assert.Null(hit);
    }

    [Fact]
    public void IsTestReference_DerivesOwningProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-impacted-" + Guid.NewGuid().ToString("N"));
        var testDir = Path.Combine(root, "src", "Widget", "UnitTest");
        Directory.CreateDirectory(testDir);
        var csproj = Path.Combine(testDir, "Widget.UnitTest.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var testFile = Path.Combine(testDir, "WidgetTests.cs");
        File.WriteAllText(testFile, "// test");

        try
        {
            var lines = TestClassSource("[Fact]");
            var hit = Support.IsTestReference(testFile, lines, line: 7, character: 16);

            Assert.NotNull(hit);
            Assert.Equal(csproj, hit!.OwningPath); // nearest .csproj walking up
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RewriteCommand_NarrowsToSingleProject_AndMergesExistingFilter()
    {
        var impacted = new[]
        {
            new TestEntryPoint(
                FilePath: NPath(@"C:\repo\tests\WidgetTests.cs"),
                OwningPath: NPath(@"C:\repo\tests\Widget.Tests.csproj"),
                TestName: "MyApp.Tests.WidgetTests.Computes_Total",
                LanguageId: "csharp"),
        };

        var rewritten = Support.RewriteCommand(
            "dotnet test build.proj --filter \"TestCategory!~CTQ&TestCategory!~Env\"",
            NPath(@"C:\repo"),
            impacted);

        // Targets the impacted project (relative, forward-slash), not build.proj.
        Assert.Contains("dotnet test tests/Widget.Tests.csproj", rewritten);
        Assert.DoesNotContain("build.proj", rewritten);
        // Preserves the standing category exclude AND adds the FQN include,
        // AND-merged so neither clobbers the other.
        Assert.Contains(
            "--filter \"(TestCategory!~CTQ&TestCategory!~Env)&(FullyQualifiedName~MyApp.Tests.WidgetTests.Computes_Total)\"",
            rewritten);
    }

    [Fact]
    public void RewriteCommand_MultipleProjects_KeepsOriginalTarget_WithCombinedFilter()
    {
        var impacted = new[]
        {
            new TestEntryPoint(NPath(@"C:\repo\a\AT.cs"), NPath(@"C:\repo\a\A.Tests.csproj"), "Ns.A.M1", "csharp"),
            new TestEntryPoint(NPath(@"C:\repo\b\BT.cs"), NPath(@"C:\repo\b\B.Tests.csproj"), "Ns.B.M2", "csharp"),
        };

        var rewritten = Support.RewriteCommand(
            "dotnet test build.proj --filter \"TestCategory!~CTQ\"",
            NPath(@"C:\repo"),
            impacted);

        // dotnet test takes one target → keep build.proj, narrow via filter.
        Assert.Contains("dotnet test build.proj", rewritten);
        Assert.Contains("FullyQualifiedName~Ns.A.M1", rewritten);
        Assert.Contains("FullyQualifiedName~Ns.B.M2", rewritten);
        Assert.Contains("(TestCategory!~CTQ)&(", rewritten);
    }

    [Fact]
    public void RewriteCommand_NoExistingFilter_EmitsBareIncludeFilter()
    {
        var impacted = new[]
        {
            new TestEntryPoint(NPath(@"C:\repo\t\T.cs"), NPath(@"C:\repo\t\T.Tests.csproj"), "Ns.T.M", "csharp"),
        };

        var rewritten = Support.RewriteCommand("dotnet test build.proj", NPath(@"C:\repo"), impacted);

        Assert.Contains("dotnet test t/T.Tests.csproj", rewritten);
        Assert.Contains("--filter \"FullyQualifiedName~Ns.T.M\"", rewritten);
    }

    [Fact]
    public void RewriteCommand_PreservesPassthroughFlags()
    {
        var impacted = new[]
        {
            new TestEntryPoint(NPath(@"C:\repo\t\T.cs"), NPath(@"C:\repo\t\T.Tests.csproj"), "Ns.T.M", "csharp"),
        };

        var rewritten = Support.RewriteCommand(
            "dotnet test build.proj --configuration Release --verbosity minimal",
            NPath(@"C:\repo"),
            impacted);

        Assert.Contains("--configuration Release", rewritten);
        Assert.Contains("--verbosity minimal", rewritten);
        Assert.DoesNotContain("build.proj", rewritten);
    }

    [Fact]
    public async Task DetectAsync_DetectsCSharpWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(await Support.DetectAsync(root, default)); // empty → not C#

            File.WriteAllText(Path.Combine(root, "build.proj"), "<Project></Project>");
            Assert.True(await Support.DetectAsync(root, default));  // build.proj → C#
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void DefersGateWhenNoImpactedFound_ForSafety()
    {
        // C# opts to defer (not skip) when its server finds nothing, so a
        // csharp-ls that couldn't load a big sln-less repo doesn't produce a
        // false-green skipped gate.
        Assert.True(Support.DeferGateWhenNoImpactedFound);
    }

    /// <summary>Normalise a backslash path to the host separator so the
    /// rooted-path assertions run on any OS the suite executes on.</summary>
    private static string NPath(string p) => p.Replace('\\', Path.DirectorySeparatorChar);
}
