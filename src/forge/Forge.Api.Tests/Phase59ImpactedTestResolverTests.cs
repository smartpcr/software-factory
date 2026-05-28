// -----------------------------------------------------------------------
// <copyright file="Phase59ImpactedTestResolverTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Phase 59 Stage A — unit contract tests for
/// <see cref="ImpactedTestResolver"/> (T1–T7 in
/// <c>docs/forge_implementation.md</c>).
///
/// <para>Most cases run against a hand-built csproj fixture under
/// <see cref="Path.GetTempPath"/> — the resolver is pure (no git, no
/// network) so the fixture is just a directory tree of empty
/// <c>.csproj</c> files with <c>&lt;ProjectReference&gt;</c> wiring. The
/// changed-file list is synthesized rather than discovered from git so
/// the test stays hermetic; <see cref="ImpactedTestResolver.DiscoverChangedFilesAsync"/>
/// has its own thin wrapper test only as a smoke (covered separately
/// once Stage B lands the git-integrated callers).</para>
/// </summary>
public class Phase59ImpactedTestResolverTests
{
    // ── Fixture helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Build a temp workspace with a hand-rolled csproj graph. Each
    /// entry is <c>(relativePath, optionalProjectReferences)</c>. The
    /// csproj files are deliberately minimal — only the
    /// <c>&lt;ProjectReference&gt;</c> elements matter for the
    /// resolver's reverse-graph build.
    /// </summary>
    private static string BuildFixture(params (string RelPath, string[]? References)[] csprojs)
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (rel, refs) in csprojs)
        {
            var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine("  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>");
            if (refs is { Length: > 0 })
            {
                sb.AppendLine("  <ItemGroup>");
                foreach (var r in refs)
                {
                    sb.AppendLine($"    <ProjectReference Include=\"{r}\" />");
                }
                sb.AppendLine("  </ItemGroup>");
            }
            sb.AppendLine("</Project>");
            File.WriteAllText(abs, sb.ToString());
        }
        return root;
    }

    private static IConfiguration BuildConfig(bool? globalImpactedOnly)
    {
        var dict = new Dictionary<string, string?>();
        if (globalImpactedOnly.HasValue)
        {
            dict["Forge:Test:ImpactedOnly"] = globalImpactedOnly.Value ? "true" : "false";
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // ── T1 — Single-file change → owning test project rewrite ────────────

    [Fact]
    public void T1_OneFileChange_RewritesToOwningTestProject()
    {
        // src/Foo/Foo.csproj + tests/Foo.Tests/Foo.Tests.csproj that
        // references Foo. Change one file inside src/Foo.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("src/Bar/Bar.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("tests/Bar.Tests/Bar.Tests.csproj", new[] { "../../src/Bar/Bar.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.True(result.Rewritten);
        Assert.False(result.Skipped);
        Assert.False(result.WholeSuiteFallback);
        Assert.Single(result.ImpactedProjects);
        Assert.Contains("Foo.Tests.csproj", result.ImpactedProjects[0]);
        Assert.StartsWith("dotnet test ", result.Command);
        Assert.Contains("Foo.Tests/Foo.Tests.csproj", result.Command);
        Assert.DoesNotContain("Bar.Tests", result.Command);
        Assert.Equal(2, result.AllTestProjectCount);
    }

    // ── T2 — Shared csproj change → transitive closure ───────────────────

    [Fact]
    public void T2_SharedCsprojChange_FansToTransitiveClosure()
    {
        // src/Common/Common.csproj is referenced by src/Api (prod),
        // src/Cli (prod). Common.Tests + Api.Tests + Cli.Tests reference
        // their respective siblings. Cli also references Api → so a
        // change to Common ripples to Common.Tests + Api.Tests +
        // Cli.Tests transitively.
        var root = BuildFixture(
            ("src/Common/Common.csproj", null),
            ("src/Api/Api.csproj", new[] { "../Common/Common.csproj" }),
            ("src/Cli/Cli.csproj", new[]
            {
                "../Common/Common.csproj",
                "../Api/Api.csproj",
            }),
            ("tests/Common.Tests/Common.Tests.csproj", new[] { "../../src/Common/Common.csproj" }),
            ("tests/Api.Tests/Api.Tests.csproj", new[] { "../../src/Api/Api.csproj" }),
            ("tests/Cli.Tests/Cli.Tests.csproj", new[] { "../../src/Cli/Cli.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Common/Helpers.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        // The transitive closure correctly identifies all three test
        // projects via the reverse-reference graph (Common → Api/Cli
        // dependents → their respective Tests projects). The
        // contract under test here is the closure walk, NOT the
        // rewrite — and with 3 of 3 test projects matched, the 80%
        // threshold (ceil(0.8 * 3) = 3) flips the resolver into
        // whole-suite-fallback mode. ImpactedProjects stays populated
        // for the transcript even when Rewritten=false. T2b below
        // covers the closure-without-fallback case by widening the
        // suite to 4 test projects so the matched 3 stays below the
        // threshold.
        Assert.Equal(3, result.ImpactedProjects.Count);
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Common.Tests.csproj"));
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Api.Tests.csproj"));
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Cli.Tests.csproj"));
        Assert.True(result.WholeSuiteFallback);
        Assert.False(result.Rewritten);
        Assert.Equal("dotnet test", result.Command);
    }

    [Fact]
    public void T2b_SharedCsprojChange_FallsBackWhenMultipleProjectsAreImpacted()
    {
        // Add a fourth, unrelated test project so the impacted closure
        // (3 of 4) sits BELOW the 80% threshold ceil(0.8 * 4) = 4.
        // Confirms the transitive closure narrowly targets only the
        // dependents.
        var root = BuildFixture(
            ("src/Common/Common.csproj", null),
            ("src/Api/Api.csproj", new[] { "../Common/Common.csproj" }),
            ("src/Cli/Cli.csproj", new[] { "../Common/Common.csproj" }),
            ("src/Standalone/Standalone.csproj", null),
            ("tests/Common.Tests/Common.Tests.csproj", new[] { "../../src/Common/Common.csproj" }),
            ("tests/Api.Tests/Api.Tests.csproj", new[] { "../../src/Api/Api.csproj" }),
            ("tests/Cli.Tests/Cli.Tests.csproj", new[] { "../../src/Cli/Cli.csproj" }),
            ("tests/Standalone.Tests/Standalone.Tests.csproj", new[] { "../../src/Standalone/Standalone.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Common/Helpers.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.Equal(3, result.ImpactedProjects.Count);
        Assert.DoesNotContain(result.ImpactedProjects, p => p.EndsWith("Standalone.Tests.csproj"));
        Assert.True(result.WholeSuiteFallback);
        Assert.Equal("dotnet test", result.Command);
        Assert.Contains("dotnet test accepts only one project target", result.Reason);
        Assert.Equal(4, result.AllTestProjectCount);
    }

    // ── T3 — Whole-suite fallback at 80% threshold ────────────────────────

    [Fact]
    public void T3_WholeSuiteFallback_WhenImpactedAtLeast80Percent()
    {
        // 5 prod / 5 test projects, all reference Common. A Common.cs
        // change fans to 5 of 5 test projects ⇒ 100% ≥ 80% threshold.
        var refsToCommon = new[] { "../Common/Common.csproj" };
        var root = BuildFixture(
            ("src/Common/Common.csproj", null),
            ("src/A/A.csproj", refsToCommon),
            ("src/B/B.csproj", refsToCommon),
            ("src/C/C.csproj", refsToCommon),
            ("src/D/D.csproj", refsToCommon),
            ("tests/Common.Tests/Common.Tests.csproj", new[] { "../../src/Common/Common.csproj" }),
            ("tests/A.Tests/A.Tests.csproj", new[] { "../../src/A/A.csproj" }),
            ("tests/B.Tests/B.Tests.csproj", new[] { "../../src/B/B.csproj" }),
            ("tests/C.Tests/C.Tests.csproj", new[] { "../../src/C/C.csproj" }),
            ("tests/D.Tests/D.Tests.csproj", new[] { "../../src/D/D.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Common/Helpers.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.True(result.WholeSuiteFallback);
        Assert.Equal("dotnet test", result.Command); // unchanged
        Assert.Equal(5, result.ImpactedProjects.Count); // populated for the transcript
        Assert.Contains("of 5", result.Reason);
    }

    // ── T4 — Empty impacted set → Skipped (not Success) ──────────────────

    [Fact]
    public void T4_EmptyImpactedSet_ReturnsSkipped()
    {
        // Workspace has test projects, but the changed file is outside
        // every project directory (a top-level README). Resolver must
        // surface Skipped=true so the gate emits task.test-gate.skipped
        // rather than silently green-lighting.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "README.md", "CHANGELOG.md" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.True(result.Skipped);
        Assert.False(result.Rewritten);
        Assert.False(result.WholeSuiteFallback);
        Assert.Empty(result.ImpactedProjects);
        Assert.Equal("dotnet test", result.Command); // command unchanged but Skipped is the operative flag
        Assert.Equal("no impacted test projects", result.Reason);
    }

    // ── T5 — Operator override args → no-op ──────────────────────────────

    [Theory]
    [InlineData("dotnet test --filter Category=Fast")]
    [InlineData("dotnet test MySolution.sln")]
    [InlineData("dotnet test path/to/Foo.Tests.csproj")]
    [InlineData("dotnet test --list-tests")]
    public void T5_OperatorOverrideArgs_NoOp(string command)
    {
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: command,
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.False(result.Skipped);
        Assert.False(result.WholeSuiteFallback);
        Assert.Equal(command, result.Command);
        Assert.Empty(result.ImpactedProjects);
    }

    // ── T6 — Global impactedOnly=false → no-op ───────────────────────────

    [Fact]
    public void T6_GlobalImpactedOnlyFalse_NoOp()
    {
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: "dotnet test",
            impactedOnly: false);

        Assert.False(result.Rewritten);
        Assert.False(result.Skipped);
        Assert.Equal("dotnet test", result.Command);
        Assert.Equal("impacted-only disabled", result.Reason);
    }

    // ── T7 — Per-repo override beats global ──────────────────────────────

    [Fact]
    public void T7_ResolveImpactedTestOnly_PerRepoBeatsGlobal()
    {
        // Per-repo true, global false → true wins.
        var repo = new RepoRow { Id = "r1", TestImpactedOnly = true };
        var config = BuildConfig(globalImpactedOnly: false);
        Assert.True(CrewStoryArtifactStrategy.ResolveImpactedTestOnly(repo, config));

        // Per-repo false, global true → false wins.
        repo.TestImpactedOnly = false;
        config = BuildConfig(globalImpactedOnly: true);
        Assert.False(CrewStoryArtifactStrategy.ResolveImpactedTestOnly(repo, config));

        // Per-repo null, global true → inherits true.
        repo.TestImpactedOnly = null;
        config = BuildConfig(globalImpactedOnly: true);
        Assert.True(CrewStoryArtifactStrategy.ResolveImpactedTestOnly(repo, config));

        // Per-repo null, global false → inherits false.
        config = BuildConfig(globalImpactedOnly: false);
        Assert.False(CrewStoryArtifactStrategy.ResolveImpactedTestOnly(repo, config));

        // Per-repo null, global unset → default true (operator hasn't
        // opted out anywhere).
        config = BuildConfig(globalImpactedOnly: null);
        Assert.True(CrewStoryArtifactStrategy.ResolveImpactedTestOnly(repo, config));

        // Null repo (orphan workstream or pre-attach state) → inherits
        // global / default.
        Assert.True(CrewStoryArtifactStrategy.ResolveImpactedTestOnly(null, BuildConfig(globalImpactedOnly: null)));
        Assert.False(CrewStoryArtifactStrategy.ResolveImpactedTestOnly(null, BuildConfig(globalImpactedOnly: false)));
    }

    // ── Bonus — global-impact file forces whole-suite ────────────────────

    [Fact]
    public void Bonus_DirectoryBuildPropsChange_ForcesWholeSuite()
    {
        // A Directory.Build.props change affects every project's
        // compilation → resolver must fall back to whole suite rather
        // than narrowing.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "Directory.Build.props" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.True(result.WholeSuiteFallback);
        Assert.False(result.Skipped);
        Assert.Equal("dotnet test", result.Command);
        Assert.Contains("global-impact", result.Reason);
    }

    [Theory]
    [InlineData("src/packages.props")]
    [InlineData("packages.props")]
    [InlineData("Directory.Packages.props")]
    public void Gap3_PackagesPropsChange_ForcesWholeSuite(string changedFile)
    {
        // gap #3 (2026-05-27) — central-package-version files pin every
        // project's dependency graph, so a (non-analyzer) change is
        // genuinely compilation-affecting → whole suite. `packages.props`
        // is the Microsoft.Build.CentralPackageVersions convention
        // (common-infra uses src/packages.props); it must be recognized
        // alongside Directory.Packages.props.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { changedFile },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.True(result.WholeSuiteFallback);
        Assert.False(result.Rewritten);
        Assert.Contains("global-impact", result.Reason);
    }

    [Fact]
    public void Gap1_MultiProjectUnderTraversalAnchor_ScopesTraversal_NotWholeSuite()
    {
        // gap #1 (2026-05-27) — when >1 test project is impacted AND the
        // command anchors on a Traversal `.proj`, the resolver signals the
        // caller to synthesize a SCOPED Traversal proj instead of falling
        // back to the whole suite. (3 impacted of 4, below the 80% gate.)
        var root = BuildFixture(
            ("src/Common/Common.csproj", null),
            ("src/Api/Api.csproj", new[] { "../Common/Common.csproj" }),
            ("src/Cli/Cli.csproj", new[] { "../Common/Common.csproj" }),
            ("src/Standalone/Standalone.csproj", null),
            ("tests/Common.Tests/Common.Tests.csproj", new[] { "../../src/Common/Common.csproj" }),
            ("tests/Api.Tests/Api.Tests.csproj", new[] { "../../src/Api/Api.csproj" }),
            ("tests/Cli.Tests/Cli.Tests.csproj", new[] { "../../src/Cli/Cli.csproj" }),
            ("tests/Standalone.Tests/Standalone.Tests.csproj", new[] { "../../src/Standalone/Standalone.csproj" }));

        // A Traversal anchor the command can pivot to a scoped proj.
        File.WriteAllText(
            Path.Combine(root, "build.proj"),
            "<Project Sdk=\"Microsoft.Build.Traversal/4.1.0\"><ItemGroup><ProjectFile Include=\"src/dirs.proj\" /></ItemGroup></Project>");

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Common/Helpers.cs" },
            originalCommand: "dotnet test build.proj",
            impactedOnly: true);

        Assert.True(result.RequiresScopedTraversal);
        Assert.False(result.WholeSuiteFallback);
        Assert.False(result.Rewritten); // caller does the rewrite after generating the proj
        Assert.Equal(3, result.ImpactedProjects.Count);
        Assert.Equal("build.proj", result.TraversalProjPath);
        Assert.DoesNotContain(result.ImpactedProjects, p => p.EndsWith("Standalone.Tests.csproj"));
    }

    [Fact]
    public void Gap1_MultiProjectWithoutTraversalAnchor_KeepsWholeSuite()
    {
        // No Traversal anchor (bare `dotnet test`) — can't safely build a
        // multi-project target, so the whole-suite fallback is preserved.
        var root = BuildFixture(
            ("src/Common/Common.csproj", null),
            ("src/Api/Api.csproj", new[] { "../Common/Common.csproj" }),
            ("src/Cli/Cli.csproj", new[] { "../Common/Common.csproj" }),
            ("src/Standalone/Standalone.csproj", null),
            ("tests/Common.Tests/Common.Tests.csproj", new[] { "../../src/Common/Common.csproj" }),
            ("tests/Api.Tests/Api.Tests.csproj", new[] { "../../src/Api/Api.csproj" }),
            ("tests/Cli.Tests/Cli.Tests.csproj", new[] { "../../src/Cli/Cli.csproj" }),
            ("tests/Standalone.Tests/Standalone.Tests.csproj", new[] { "../../src/Standalone/Standalone.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Common/Helpers.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.False(result.RequiresScopedTraversal);
        Assert.True(result.WholeSuiteFallback);
        Assert.Null(result.TraversalProjPath);
    }

    [Theory]
    [InlineData("global.json")]
    [InlineData("nuget.config")]
    [InlineData("NuGet.Config")]
    public void Bonus_GlobalJsonAndNugetConfig_DoNotForceWholeSuite(string filename)
    {
        // 2026-05-19 regression — agent created a new `global.json`
        // for an iter on the FRP `update` repo. Pre-fix the resolver
        // treated it as global-impact + fell back to whole suite +
        // hit the 8-min timeout on 31 test projects. Both files
        // affect build resolution (SDK pin / package registry) but
        // NOT the per-test call graph, so they no longer trigger
        // whole-suite fallback. Padding with unrelated test
        // projects so the impacted set (the one Foo.Tests we'd
        // discover from the changed Bar.cs) stays well below the
        // 80% threshold.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("src/Other1/Other1.csproj", null),
            ("tests/Other1.Tests/Other1.Tests.csproj", new[] { "../../src/Other1/Other1.csproj" }),
            ("src/Other2/Other2.csproj", null),
            ("tests/Other2.Tests/Other2.Tests.csproj", new[] { "../../src/Other2/Other2.csproj" }));

        // Change both the global config + an actual source file in
        // Foo. Pre-fix this returned WholeSuiteFallback=true
        // because global.json short-circuited. Post-fix the
        // resolver IGNORES global.json + only sees the Foo.cs
        // change → narrows to Foo.Tests.
        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { filename, "src/Foo/Bar.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.True(result.Rewritten,
            $"`{filename}` should not be classified as global-impact; rewrite should fire for the Foo.cs change.");
        Assert.False(result.WholeSuiteFallback);
        Assert.False(result.Skipped);
        Assert.Single(result.ImpactedProjects);
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Foo.Tests.csproj"));
    }

    // ── Bonus — rewrite preserves trailing flags ──────────────────────────

    [Fact]
    public void Bonus_Rewrite_PreservesTrailingFlags()
    {
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("src/Bar/Bar.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("tests/Bar.Tests/Bar.Tests.csproj", new[] { "../../src/Bar/Bar.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Foo.cs" },
            originalCommand: "dotnet test --logger trx --verbosity minimal",
            impactedOnly: true);

        Assert.True(result.Rewritten);
        // Project paths come right after `dotnet test`, before --logger /
        // --verbosity flags. Sanity-check the order.
        var tokens = ImpactedTestResolver.ParseCommandLine(result.Command);
        Assert.Equal("dotnet", tokens[0]);
        Assert.Equal("test", tokens[1]);
        Assert.EndsWith("Foo.Tests.csproj", tokens[2]);
        Assert.Equal("--logger", tokens[3]);
        Assert.Equal("trx", tokens[4]);
        Assert.Equal("--verbosity", tokens[5]);
        Assert.Equal("minimal", tokens[6]);
    }

    // ── Bonus — empty workspace / non-dotnet-test commands ───────────────

    [Fact]
    public void Bonus_NonDotnetTestCommand_NoOp()
    {
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Foo.cs" },
            originalCommand: "cargo test",
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.Equal("cargo test", result.Command);
        Assert.Contains("not a `dotnet test`", result.Reason);
    }

    [Fact]
    public void Bonus_NoTestProjects_NoOp()
    {
        // Workspace has only a prod csproj — no test projects to
        // narrow against. Resolver returns a no-op so the original
        // command runs (and will likely no-op or fail naturally).
        var root = BuildFixture(("src/Foo/Foo.csproj", null));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Foo.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.False(result.Skipped);
        Assert.Equal("dotnet test", result.Command);
        Assert.Contains("no test projects", result.Reason);
    }

    // ── Broadened detection — singular suffix variants (FRP convention) ──

    [Fact]
    public void Broadened_SingularUnitTestSuffix_IsClassifiedAsTestProject()
    {
        // The FRP / update repo convention: singular `UnitTest` /
        // `IntegrationTest` suffixes (not the xUnit-canonical plural).
        // Pre-broadening, these were silently treated as prod
        // projects and the resolver always fell through to "no test
        // projects discovered". Fixture widens to 5 test projects (2
        // impacted, 3 unrelated) so the closure stays well under the
        // 80% whole-suite threshold and the rewrite actually fires.
        var root = BuildFixture(
            ("src/Foo/Source/Foo.csproj", null),
            ("src/Foo/UnitTest/Foo.UnitTest.csproj", new[] { "../Source/Foo.csproj" }),
            ("src/Foo/IntegrationTest/Foo.IntegrationTest.csproj", new[] { "../Source/Foo.csproj" }),
            ("src/Unrelated1/Source/Unrelated1.csproj", null),
            ("src/Unrelated1/UnitTest/Unrelated1.UnitTest.csproj", new[] { "../Source/Unrelated1.csproj" }),
            ("src/Unrelated2/Source/Unrelated2.csproj", null),
            ("src/Unrelated2/UnitTest/Unrelated2.UnitTest.csproj", new[] { "../Source/Unrelated2.csproj" }),
            ("src/Unrelated3/Source/Unrelated3.csproj", null),
            ("src/Unrelated3/UnitTest/Unrelated3.UnitTest.csproj", new[] { "../Source/Unrelated3.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Source/Bar.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.True(result.WholeSuiteFallback);
        Assert.Equal("dotnet test", result.Command);
        Assert.Contains("dotnet test accepts only one project target", result.Reason);
        Assert.Equal(5, result.AllTestProjectCount);
        Assert.Equal(2, result.ImpactedProjects.Count);
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Foo.UnitTest.csproj"));
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Foo.IntegrationTest.csproj"));
        Assert.DoesNotContain(result.ImpactedProjects, p => p.Contains("Unrelated"));
    }

    // ── Broadened detection — parent directory name ──────────────────────

    [Fact]
    public void Broadened_ParentDirectoryNamedUnitTest_IsClassifiedAsTestProject()
    {
        // Some FRP projects live under `**/UnitTest/{anything}.csproj`
        // — the csproj name may not carry the test suffix when an
        // operator renamed the project. Directory name remains a
        // reliable signal. Padded with unrelated test projects so the
        // single match stays well below the 80% whole-suite threshold.
        var root = BuildFixture(
            ("src/Foo/Source/Foo.csproj", null),
            ("src/Foo/UnitTest/Renamed.csproj", new[] { "../Source/Foo.csproj" }),
            ("src/Other1/Source/Other1.csproj", null),
            ("src/Other1/Tests/Other1.Tests.csproj", new[] { "../Source/Other1.csproj" }),
            ("src/Other2/Source/Other2.csproj", null),
            ("src/Other2/Tests/Other2.Tests.csproj", new[] { "../Source/Other2.csproj" }));

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Source/Bar.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.True(result.Rewritten);
        Assert.Equal(3, result.AllTestProjectCount);
        Assert.Single(result.ImpactedProjects);
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Renamed.csproj"));
    }

    // ── Broadened detection — content-based (Microsoft.NET.Test.Sdk) ─────

    [Fact]
    public void Broadened_ContentBased_TestSdkReferenceMarksProject()
    {
        // CloudTest.csproj / Bvt.csproj on the FRP repo — names give
        // no hint they're test projects, parent dir is unremarkable.
        // The only universal signal is the
        // <PackageReference Include="Microsoft.NET.Test.Sdk" /> entry
        // every modern .NET test runner carries.
        var root = Path.Combine(Path.GetTempPath(), "phase59-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Foo", "Source"));
        Directory.CreateDirectory(Path.Combine(root, "src", "CloudTest"));
        File.WriteAllText(
            Path.Combine(root, "src", "Foo", "Source", "Foo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(root, "src", "CloudTest", "CloudTest.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            + "<PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>"
            + "<ItemGroup>"
            + "  <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.8.0\" />"
            + "  <ProjectReference Include=\"../Foo/Source/Foo.csproj\" />"
            + "</ItemGroup>"
            + "</Project>");
        // Two unrelated test projects so the impacted set (1) stays
        // below the 80% whole-suite threshold (ceil(0.8 * 3) = 3).
        Directory.CreateDirectory(Path.Combine(root, "src", "Other1", "Tests"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Other2", "Tests"));
        File.WriteAllText(
            Path.Combine(root, "src", "Other1", "Tests", "Other1.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(root, "src", "Other2", "Tests", "Other2.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Source/Bar.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.True(result.Rewritten);
        Assert.Equal(3, result.AllTestProjectCount);
        Assert.Single(result.ImpactedProjects);
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("CloudTest.csproj"));
    }

    [Fact]
    public void Broadened_ContentBased_IsTestProjectFlagMarksProject()
    {
        // Some repos opt into the canonical
        // <IsTestProject>true</IsTestProject> MSBuild flag instead of
        // referencing the SDK directly (less common but supported).
        var root = Path.Combine(Path.GetTempPath(), "phase59-content-flag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Foo", "Source"));
        Directory.CreateDirectory(Path.Combine(root, "src", "FooBvt"));
        File.WriteAllText(
            Path.Combine(root, "src", "Foo", "Source", "Foo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(root, "src", "FooBvt", "FooBvt.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            + "<PropertyGroup>"
            + "  <TargetFramework>net8.0</TargetFramework>"
            + "  <IsTestProject>true</IsTestProject>"
            + "</PropertyGroup>"
            + "<ItemGroup><ProjectReference Include=\"../Foo/Source/Foo.csproj\" /></ItemGroup>"
            + "</Project>");
        // Two unrelated test projects so the impacted set (1) stays
        // below the 80% whole-suite threshold (ceil(0.8 * 3) = 3).
        Directory.CreateDirectory(Path.Combine(root, "src", "Other1", "Tests"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Other2", "Tests"));
        File.WriteAllText(
            Path.Combine(root, "src", "Other1", "Tests", "Other1.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(root, "src", "Other2", "Tests", "Other2.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Source/Bar.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.True(result.Rewritten);
        Assert.Equal(3, result.AllTestProjectCount);
        Assert.Single(result.ImpactedProjects);
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("FooBvt.csproj"));
    }

    [Fact]
    public void Broadened_ContentBased_HelperLibraryWithTestInNameButNoTestSdk_IsNotTestProject()
    {
        // `TestCommon.TestUtilities.csproj` on the FRP repo is a test
        // FIXTURE helper — a library referenced by actual test
        // runners, not itself a test runner. Content detection
        // correctly excludes it (no Microsoft.NET.Test.Sdk ref).
        // Confirms we don't over-classify helper projects whose names
        // happen to contain "Test".
        var root = Path.Combine(Path.GetTempPath(), "phase59-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "TestCommon", "TestUtilities", "Source"));
        File.WriteAllText(
            Path.Combine(root, "src", "TestCommon", "TestUtilities", "Source", "TestCommon.TestUtilities.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        // Without a `Microsoft.NET.Test.Sdk` reference + without one of
        // the test-suffix names + without a test-named parent dir,
        // the helper is correctly classified as prod (which means the
        // resolver finds NO test projects and falls back to no-op).
        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/TestCommon/TestUtilities/Source/Foo.cs" },
            originalCommand: "dotnet test",
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.Contains("no test projects", result.Reason);
    }

    // ── T8 — Traversal .proj + --filter compose ──────────────────────────

    // 2026-05-25 — Phase 59 refinement. The pre-fix classifier
    // hard-no-op'd on ANY `--filter`, so a repo with
    // `dotnet test build.proj --filter "TestCategory!~CTQ&TestCategory!~Env"`
    // (orthogonal category EXCLUSION) skipped impacted-test narrowing
    // entirely + ran the whole suite via build.proj's Traversal SDK.
    // Post-fix: a Traversal `.proj` token anchors operator intent
    // ("across all projects, filtered") so we narrow the project set
    // and PRESERVE the filter. Inclusion / FQN filters still no-op
    // (vacuous-pass risk if the targeted test lives in a non-impacted
    // project). Non-Traversal `.proj` orchestrators still no-op (we
    // can't safely replace a custom build target with a list of
    // .csproj paths). `--filter` without a Traversal anchor still
    // no-ops (the plain `dotnet test --filter X` shape is too
    // ambiguous to second-guess).

    /// <summary>Writes a Traversal SDK build.proj at the fixture root.</summary>
    private static void WriteTraversalProj(string root, string fileName = "build.proj")
    {
        File.WriteAllText(
            Path.Combine(root, fileName),
            "<Project Sdk=\"Microsoft.Build.Traversal/2.0.24\">\n" +
            "  <ItemGroup>\n" +
            "    <ProjectReference Include=\"src/**/*.csproj\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>\n");
    }

    [Fact]
    public void T8a_TraversalProj_WithExclusionFilter_NarrowsAndPreservesFilterAndDropsBuildProj()
    {
        // The exact shape that broke for the `update` repo: a build.proj
        // Traversal target + a category-exclusion --filter.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("src/Bar/Bar.csproj", null),
            ("tests/Bar.Tests/Bar.Tests.csproj", new[] { "../../src/Bar/Bar.csproj" }));
        WriteTraversalProj(root);

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: "dotnet test build.proj --filter \"TestCategory!~CTQ&TestCategory!~Env\"",
            impactedOnly: true);

        Assert.True(result.Rewritten);
        Assert.False(result.WholeSuiteFallback);
        Assert.False(result.Skipped);
        Assert.Single(result.ImpactedProjects);
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Foo.Tests.csproj"));

        // build.proj must be DROPPED (else Traversal re-includes everything).
        Assert.DoesNotContain("build.proj", result.Command);
        // Filter must be preserved AND re-quoted (the `&` would otherwise
        // be a cmd.exe command-chain operator in the session-script path).
        Assert.Contains("--filter \"TestCategory!~CTQ&TestCategory!~Env\"", result.Command);
        // Impacted project inserted right after `dotnet test`.
        Assert.StartsWith("dotnet test ", result.Command);
        Assert.Contains("Foo.Tests.csproj", result.Command);
    }

    [Fact]
    public void T8b_TraversalProj_NoFilter_NarrowsAndDropsBuildProj()
    {
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("src/Bar/Bar.csproj", null),
            ("tests/Bar.Tests/Bar.Tests.csproj", new[] { "../../src/Bar/Bar.csproj" }));
        WriteTraversalProj(root);

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: "dotnet test build.proj",
            impactedOnly: true);

        Assert.True(result.Rewritten);
        Assert.DoesNotContain("build.proj", result.Command);
        Assert.Contains("Foo.Tests.csproj", result.Command);
    }

    [Theory]
    [InlineData("FullyQualifiedName~Foo")]
    [InlineData("DisplayName=BarTest")]
    [InlineData("Name~Login")]
    [InlineData("TestCategory=Fast")]
    [InlineData("Priority=1")]
    [InlineData("TestCategory!~Slow&FullyQualifiedName~Foo")]
    public void T8c_TraversalProj_WithFqnOrInclusionFilter_NoOps(string filterValue)
    {
        // Inclusion (`=` / `~`) or FQN-targeted filters could vacuous-
        // pass if the targeted test is in a non-impacted project. Treat
        // as hard override even with a Traversal anchor.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));
        WriteTraversalProj(root);
        var command = $"dotnet test build.proj --filter \"{filterValue}\"";

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: command,
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.Equal(command, result.Command);
        Assert.Contains("--filter", result.Reason);
    }

    [Fact]
    public void T8d_FilterWithoutTraversalAnchor_NoOps()
    {
        // `dotnet test --filter X` (no project, no Traversal target) is
        // ambiguous: operator may have meant FQN-narrowing across the
        // whole workspace. Don't second-guess — preserve pre-fix
        // behaviour and no-op.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));
        const string command = "dotnet test --filter \"TestCategory!~Slow\"";

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: command,
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.Equal(command, result.Command);
        Assert.Contains("without a Traversal", result.Reason);
    }

    [Fact]
    public void T8e_NonTraversalCustomProj_NoOps()
    {
        // A custom `.proj` orchestrator (no Microsoft.Build.Traversal
        // SDK marker) may define properties, imports, or targets we
        // don't understand. Replacing it with a list of .csproj could
        // silently change behaviour — stay conservative and no-op.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }));
        File.WriteAllText(
            Path.Combine(root, "custom-orchestrator.proj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup><CustomBuildLogic>true</CustomBuildLogic></PropertyGroup>\n" +
            "</Project>\n");
        const string command = "dotnet test custom-orchestrator.proj";

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: command,
            impactedOnly: true);

        Assert.False(result.Rewritten);
        Assert.Equal(command, result.Command);
        Assert.Contains("custom (non-Traversal)", result.Reason);
    }

    [Fact]
    public void T8f_OptionValueEndingInProj_IsNotClassifiedAsTraversal()
    {
        // `-p:Prop=value.proj` is an MSBuild property assignment, not a
        // positional Traversal target. The classifier must NOT drop it
        // during rewrite + must not over-eagerly require Traversal
        // content-check. With no Traversal anchor + no --filter, the
        // resolver narrows normally and preserves the -p:... token.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("src/Bar/Bar.csproj", null),
            ("tests/Bar.Tests/Bar.Tests.csproj", new[] { "../../src/Bar/Bar.csproj" }));
        const string command = "dotnet test -p:SomeProp=value.proj";

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: command,
            impactedOnly: true);

        Assert.True(result.Rewritten);
        Assert.Contains("Foo.Tests.csproj", result.Command);
        Assert.Contains("-p:SomeProp=value.proj", result.Command);
    }

    [Fact]
    public void T8g_TraversalProj_TraversalAndOptionValueProj_PreservesOptionAndDropsTraversal()
    {
        // Combined case: build.proj IS a Traversal target (gets dropped)
        // AND a separate -p:File=other.proj option value (must be
        // preserved as-is). Validates that we drop ONLY the classified
        // Traversal index, not every .proj suffix.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.Tests/Foo.Tests.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("src/Bar/Bar.csproj", null),
            ("tests/Bar.Tests/Bar.Tests.csproj", new[] { "../../src/Bar/Bar.csproj" }));
        WriteTraversalProj(root);
        const string command = "dotnet test build.proj -p:SomeProp=value.proj";

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: command,
            impactedOnly: true);

        Assert.True(result.Rewritten);
        // build.proj dropped from rewrite, -p:...proj preserved.
        Assert.DoesNotContain(" build.proj", result.Command);
        Assert.Contains("-p:SomeProp=value.proj", result.Command);
        Assert.Contains("Foo.Tests.csproj", result.Command);
    }

    [Fact]
    public void T8h_TraversalProj_MultipleImpactedProjects_RequestsScopedTraversal()
    {
        // Originally (update/1.3): the resolver couldn't emit
        // `dotnet test proj1.csproj proj2.csproj` (dotnet test takes one
        // target), so multiple impacted projects under a Traversal anchor
        // fell back to the whole suite. gap #1 (2026-05-27): instead of
        // whole-suite, the resolver now SIGNALS the caller to synthesize a
        // scoped Traversal proj (RequiresScopedTraversal=true). The resolver
        // itself does NOT rewrite — it leaves Command as the original and the
        // caller swaps the Traversal target for the generated scoped proj.
        var root = BuildFixture(
            ("src/Foo/Foo.csproj", null),
            ("tests/Foo.UnitTest/Foo.UnitTest.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("tests/Foo.IntegrationTest/Foo.IntegrationTest.csproj", new[] { "../../src/Foo/Foo.csproj" }),
            ("src/Bar/Bar.csproj", null),
            ("tests/Bar.UnitTest/Bar.UnitTest.csproj", new[] { "../../src/Bar/Bar.csproj" }),
            ("src/Baz/Baz.csproj", null),
            ("tests/Baz.UnitTest/Baz.UnitTest.csproj", new[] { "../../src/Baz/Baz.csproj" }));
        WriteTraversalProj(root);
        const string command = "dotnet test build.proj --filter \"TestCategory!~CTQ&TestCategory!~Env\"";

        var result = ImpactedTestResolver.Resolve(
            workspace: root,
            changedFiles: new[] { "src/Foo/Bar.cs" },
            originalCommand: command,
            impactedOnly: true);

        Assert.False(result.Rewritten);             // caller rewrites, not the resolver
        Assert.False(result.WholeSuiteFallback);    // gap #1 — no longer whole-suite
        Assert.True(result.RequiresScopedTraversal);
        Assert.Equal("build.proj", result.TraversalProjPath);
        Assert.Equal(command, result.Command);      // unchanged until the caller swaps the target
        Assert.Equal(2, result.ImpactedProjects.Count);
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Foo.UnitTest.csproj"));
        Assert.Contains(result.ImpactedProjects, p => p.EndsWith("Foo.IntegrationTest.csproj"));
        Assert.Contains("scopes a generated Traversal proj", result.Reason);
    }

    // ── T9 — Helper unit tests for IsFilterNarrowingSafe ────────────────

    [Theory]
    [InlineData("TestCategory!~Slow", true)]
    [InlineData("TestCategory!~Slow&TestCategory!~Env", true)]
    [InlineData("TestCategory!~Slow|TestCategory!=Long", true)]
    [InlineData("TestCategory != Slow", true)]    // spaces between ! and =
    [InlineData("TestCategory!= Slow & TestCategory !~ Env", true)]
    [InlineData("", true)]
    [InlineData("TestCategory=Fast", false)]
    [InlineData("FullyQualifiedName~Foo", false)]
    [InlineData("TestCategory!~Slow&FullyQualifiedName~Foo", false)]   // mixed
    [InlineData("Priority=1", false)]
    public void T9_IsFilterNarrowingSafe_ClassifiesCorrectly(string filterValue, bool expected)
    {
        Assert.Equal(expected, ImpactedTestResolver.IsFilterNarrowingSafe(filterValue));
    }

    // ── T10 — Helper unit tests for IsTraversalProjFile ─────────────────

    [Fact]
    public void T10_IsTraversalProjFile_DetectsTraversalSdkMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59-traversal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Canonical Traversal SDK file → true.
        File.WriteAllText(Path.Combine(root, "build.proj"),
            "<Project Sdk=\"Microsoft.Build.Traversal/2.0.24\"></Project>");
        Assert.True(ImpactedTestResolver.IsTraversalProjFile(root, "build.proj"));

        // Custom non-Traversal .proj → false.
        File.WriteAllText(Path.Combine(root, "custom.proj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Assert.False(ImpactedTestResolver.IsTraversalProjFile(root, "custom.proj"));

        // Missing file → false (defensive, never throws).
        Assert.False(ImpactedTestResolver.IsTraversalProjFile(root, "does-not-exist.proj"));

        // Empty / null guards → false.
        Assert.False(ImpactedTestResolver.IsTraversalProjFile(root, ""));
        Assert.False(ImpactedTestResolver.IsTraversalProjFile(root, "   "));
    }
}
