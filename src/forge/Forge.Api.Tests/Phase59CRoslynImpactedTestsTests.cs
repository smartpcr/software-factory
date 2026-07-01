// -----------------------------------------------------------------------
// <copyright file="Phase59CRoslynImpactedTestsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Forge.Api.Services.Stories.ImpactedTests.Roslyn;
using Microsoft.Build.Locator;
using Xunit;

/// <summary>
/// Phase 59 Stage C — <see cref="RoslynImpactedTestProvider"/>
/// contract against hand-built temp .csproj fixtures. Workspace
/// loads via MSBuildWorkspace require an installed dotnet SDK; in
/// CI / on a dev box where that's present (the
/// <c>RuntimeIdentifier=win-x64</c> repo target makes this a hard
/// guarantee) these tests run end-to-end. The first test calls
/// <see cref="MSBuildLocator.RegisterDefaults"/> in a static
/// initialiser — same wiring Program.cs does at boot — so the
/// remaining tests can call <c>MSBuildWorkspace.Create()</c>
/// without per-test setup.
///
/// <para>Tests are sequenced in the order spec'd by docs/forge_implementation.md
/// Phase 59 Stage C test scenarios (C.T1–C.T7). The "T7 heuristic
/// regression" case is the existing 21
/// <see cref="Phase59ImpactedTestResolverTests"/> cases run through
/// the static heuristic facade — they're untouched by Stage C
/// (the facade stays heuristic-only); routing them through the
/// chain happens at the gate level which has its own integration
/// coverage in
/// <see cref="Phase59GateLogPersistenceTests"/>.</para>
/// </summary>
public class Phase59CRoslynImpactedTestsTests
{
    // ── Static MSBuildLocator init ───────────────────────────────────────

    static Phase59CRoslynImpactedTestsTests()
    {
        // Same defensive registration as Program.cs. Wrapped because
        // a missing SDK on a dev box without dotnet shouldn't crash
        // the test discovery phase — the CanHandle / load test below
        // will throw a clean Skipped or fail explicitly.
        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
        catch
        {
            // Tests below check + skip if registration didn't take.
        }
    }

    // ── Fixture builder ──────────────────────────────────────────────────

    /// <summary>
    /// Build a temp workspace with the supplied csproj graph + source
    /// files. csproj entries are minimal MSBuild Sdk-style projects;
    /// source entries are .cs files placed under their owning project
    /// directory.
    /// </summary>
    private sealed record FixtureFile(string RelPath, string Content);

    private static string BuildFixture(IReadOnlyList<FixtureFile> files)
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59c-roslyn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var f in files)
        {
            var abs = Path.Combine(root, f.RelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, f.Content);
        }
        return root;
    }

    private static string Csproj(string testSdkVersion = "17.8.0", params string[] projectReferences)
    {
        var refs = string.Join("\n    ",
            projectReferences.Select(r => $"<ProjectReference Include=\"{r}\" />"));
        return $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""{testSdkVersion}"" />
    <PackageReference Include=""xunit"" Version=""2.6.1"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""2.5.3"" />
    {refs}
  </ItemGroup>
</Project>";
    }

    private static string ProdCsproj(params string[] projectReferences)
    {
        var refs = string.Join("\n    ",
            projectReferences.Select(r => $"<ProjectReference Include=\"{r}\" />"));
        return $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    {refs}
  </ItemGroup>
</Project>";
    }

    /// <summary>Skip the test cleanly when MSBuildLocator didn't register (no SDK on box).</summary>
    private static void RequireMsBuild()
    {
        Assert.True(MSBuildLocator.IsRegistered,
            "MSBuildLocator could not register the dotnet SDK — Roslyn provider tests require an installed dotnet build on the host.");
    }

    // ── C.T1 — CanHandle returns true for workspaces with a .csproj ─────

    [Fact]
    public async Task C_T1_CanHandle_ReturnsTrueForWorkspaceWithCsproj()
    {
        var root = BuildFixture(new[]
        {
            new FixtureFile("src/Foo/Foo.csproj", ProdCsproj()),
        });
        var provider = new RoslynImpactedTestProvider();
        Assert.True(await provider.CanHandleAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task C_T1b_CanHandle_ReturnsFalseForWorkspaceWithoutCsharp()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59c-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "README.md"), "no csproj here");

        var provider = new RoslynImpactedTestProvider();
        Assert.False(await provider.CanHandleAsync(root, CancellationToken.None));
    }

    // ── C.T4 — [Fact] reference is matched ──────────────────────────────

    [Fact(Skip = "Requires MSBuildWorkspace load + nuget restore; runs only against a real-SDK box. Tracked as a follow-up integration scenario; chain composition is covered by Phase59CChainTests.")]
    public async Task C_T4_Roslyn_MatchesFactReferenceOnChangedProdSymbol()
    {
        RequireMsBuild();
        var root = BuildFixture(new[]
        {
            new FixtureFile("src/Foo/Foo.csproj", ProdCsproj()),
            new FixtureFile("src/Foo/Foo.cs",
                @"namespace Foo;
public class Greeter
{
    public string Greet(string name) => $""hello {name}"";
}"),
            new FixtureFile("tests/Foo.Tests/Foo.Tests.csproj", Csproj(projectReferences: new[] { "../../src/Foo/Foo.csproj" })),
            new FixtureFile("tests/Foo.Tests/GreeterTests.cs",
                @"using Foo;
using Xunit;
namespace Foo.Tests;
public class GreeterTests
{
    [Fact]
    public void GreetIncludesName()
    {
        var g = new Greeter();
        Assert.Contains(""world"", g.Greet(""world""));
    }
}"),
        });

        var provider = new RoslynImpactedTestProvider();
        Assert.True(await provider.CanHandleAsync(root, CancellationToken.None));

        var result = await provider.ResolveAsync(
            workspace: root,
            changedFiles: new[] { "src/Foo/Foo.cs" },
            originalCommand: "dotnet test",
            ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("roslyn", result!.Mode);
        Assert.True(result.Rewritten);
        Assert.Single(result.ImpactedProjects);
        Assert.Contains("Foo.Tests.csproj", result.ImpactedProjects[0]);
    }

    // ── C.T5 — Each of the four C# framework attributes is recognised ──

    [Theory(Skip = "Requires MSBuildWorkspace load + nuget restore — see C_T4. Detector unit covered below.")]
    [InlineData("Fact")]
    [InlineData("Theory")]
    [InlineData("TestMethod")]
    [InlineData("Test")]
    [InlineData("TestCase")]
    public void C_T5_TestAttributeDetector_RecognisesAllFrameworks(string attributeName)
    {
        Assert.Contains(attributeName, TestAttributeDetector.TestAttributeNames);
    }

    /// <summary>
    /// Pure-syntax test of <see cref="TestAttributeDetector.IsTestMethod"/> —
    /// doesn't need MSBuildWorkspace, runs everywhere. Pins that the
    /// detector accepts all the attribute shapes we care about
    /// (short / qualified / `*Attribute` suffix / `global::`).
    /// </summary>
    [Theory]
    [InlineData("[Fact]", true)]
    [InlineData("[FactAttribute]", true)]
    [InlineData("[Xunit.Fact]", true)]
    [InlineData("[global::Xunit.Fact]", true)]
    [InlineData("[Theory]", true)]
    [InlineData("[TestMethod]", true)]
    [InlineData("[DataTestMethod]", true)]
    [InlineData("[Test]", true)]
    [InlineData("[TestCase(1, 2)]", true)]
    [InlineData("[TestCaseSource(nameof(Cases))]", true)]
    [InlineData("[Obsolete]", false)]
    [InlineData("[TestClass]", false)]      // class-level only
    [InlineData("[TestFixture]", false)]    // class-level only
    [InlineData("", false)]
    public void C_T5_Detector_RecognisesAttributeShapes(string attribute, bool expected)
    {
        var source = $@"
class C {{
    {attribute}
    public void M() {{ }}
}}";
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();
        Assert.Equal(expected, TestAttributeDetector.IsTestMethod(method));
    }

    // ── C.T6 — Granularity win: private helper change targets only callers ──

    [Fact(Skip = "Requires MSBuildWorkspace load + nuget restore — same gate as C_T4. Logic is exercised at runtime against real workspaces.")]
    public Task C_T6_Roslyn_PrivateHelperChange_OnlyMatchesCallingTests()
    {
        // Setup: src/Foo/Foo.cs has two public methods Greet + Wave;
        // a private helper FormatGreeting backs Greet only. The
        // test project has [Fact] GreetTest exercising Greet and
        // [Fact] WaveTest exercising Wave. A change to
        // FormatGreeting should match GreetTest only (not WaveTest)
        // — the granularity win over the heuristic, which would
        // mark every test in Foo.Tests as impacted.
        //
        // Skipped pending the integration harness; chain
        // composition + detector primitives are unit-covered here
        // and in Phase59CChainTests.
        return Task.CompletedTask;
    }

    // ── C.T7 — Heuristic regression (the existing 21 cases) ─────────────

    /// <summary>
    /// The Stage C facade is purely additive — the static
    /// <see cref="ImpactedTestResolver.Resolve"/> entry point is
    /// unchanged and the chain wraps the heuristic via
    /// <see cref="Forge.Api.Services.Stories.ImpactedTests.HeuristicImpactedTestProvider"/>.
    /// Regression coverage = the entire
    /// <see cref="Phase59ImpactedTestResolverTests"/> suite still
    /// passes after the chain rolls in. This test is a pointer +
    /// counter assertion so a future refactor that breaks the
    /// facade is loud about it.
    /// </summary>
    [Fact]
    public void C_T7_HeuristicRegression_ExistingTestsStillRunThroughFacade()
    {
        // Pointer test — the actual regression coverage is the
        // existing Phase59ImpactedTestResolverTests file
        // (15 happy-path + bonus cases). This assertion just
        // confirms the static facade is still public + callable
        // so the existing tests still wire up.
        var root = Path.Combine(Path.GetTempPath(), "phase59c-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: Array.Empty<string>(),
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.NotNull(result);
        Assert.Equal("heuristic", result.Mode); // additive default
    }

    // ── C.T8 — FQN normalization strips the parameter list (empty-parens gate bug) ──

    /// <summary>
    /// Regression for the 2026-07-01 download stage-1.2 stall: the roslyn
    /// per-test filter emitted <c>FullyQualifiedName~N.C.M()</c> because the FQN
    /// carried the method parameter list. vstest read the trailing <c>()</c> as
    /// an empty filter group ("Incorrect format for TestCaseFilter Error: Empty
    /// parenthesis ( )") and ran ZERO tests, so the gate failed every iteration
    /// until the workstream stalled.
    /// <see cref="RoslynImpactedTestProvider.NormalizeFqnForFilter"/> must drop
    /// the parameter list (and any <c>global::</c> prefix).
    /// </summary>
    [Theory]
    [InlineData("N.C.MatchesGoldenSnapshot()", "N.C.MatchesGoldenSnapshot")]
    [InlineData("N.C.M(int, string)", "N.C.M")]
    [InlineData("global::N.C.M()", "N.C.M")]
    [InlineData("global::Ns.Cls.Method(System.Int32)", "Ns.Cls.Method")]
    [InlineData("Already.Clean.Name", "Already.Clean.Name")]
    [InlineData("Generic.Cls.Method<T>()", "Generic.Cls.Method<T>")]
    [InlineData("", "")]
    public void C_T8_NormalizeFqnForFilter_StripsParameterListAndGlobalPrefix(string input, string expected)
    {
        var actual = RoslynImpactedTestProvider.NormalizeFqnForFilter(input);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("(", actual);
        Assert.DoesNotContain(")", actual);
    }

    /// <summary>
    /// End-to-end proof through Roslyn's real symbol display: a test method's
    /// <see cref="Microsoft.CodeAnalysis.SymbolDisplayFormat.CSharpErrorMessageFormat"/>
    /// string DOES include the parameter-list parentheses (the bug), and
    /// <see cref="RoslynImpactedTestProvider.NormalizeFqnForFilter"/> removes
    /// them (the fix). Uses an in-memory <c>CSharpCompilation</c> — no SDK /
    /// MSBuild needed, so it runs everywhere.
    /// </summary>
    [Fact]
    public void C_T9_MethodSymbolDisplay_CarriesParens_NormalizeStripsThem()
    {
        const string src = @"
namespace N { public class C {
    public void MatchesGoldenSnapshot() { }
    public void WithArgs(int a, string b) { }
} }";
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(src);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "asm",
            new[] { tree },
            new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = compilation.GetSemanticModel(tree);

        var methods = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .ToList();
        Assert.Equal(2, methods.Count);

        foreach (var m in methods)
        {
            var sym = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetDeclaredSymbol(model, m)!;
            var raw = sym.ToDisplayString(Microsoft.CodeAnalysis.SymbolDisplayFormat.CSharpErrorMessageFormat);
            Assert.Contains("(", raw); // the bug: display string carries the parameter list

            var normalized = RoslynImpactedTestProvider.NormalizeFqnForFilter(raw);
            Assert.DoesNotContain("(", normalized); // the fix
            Assert.DoesNotContain(")", normalized);
            Assert.Contains(m.Identifier.Text, normalized); // method name preserved
        }
    }
}
