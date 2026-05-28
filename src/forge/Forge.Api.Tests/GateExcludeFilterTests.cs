// -----------------------------------------------------------------------
// <copyright file="GateExcludeFilterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// 2026-05-21 — unit contract for
/// <see cref="CrewStoryArtifactStrategy.ApplyTestExcludeFilter"/>, the
/// helper that merges a vstest <c>--filter</c> exclusion (config key
/// <c>Forge:Test:ExcludeFilter</c>) into the resolved story-gate test
/// command so chronic pre-existing flakes can be skipped without burning
/// the gate's wall-clock budget.
/// </summary>
public class GateExcludeFilterTests
{
    private const string Exclude =
        "FullyQualifiedName!~CloudVsrDiscovery&FullyQualifiedName!~PostUpdateOperation";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptyExclusion_IsNoOp(string? exclude)
    {
        const string cmd = "dotnet test src/Foo/Foo.IntegrationTest.csproj";
        Assert.Equal(cmd, CrewStoryArtifactStrategy.ApplyTestExcludeFilter(cmd, exclude));
    }

    [Theory]
    [InlineData("cargo test")]
    [InlineData("go test ./...")]
    [InlineData("npm test")]
    [InlineData("pytest -q")]
    public void NonDotnetTest_IsNoOp(string cmd)
    {
        // vstest --filter syntax only applies to `dotnet test`; other
        // ecosystems use their own flags, so we leave them untouched.
        Assert.Equal(cmd, CrewStoryArtifactStrategy.ApplyTestExcludeFilter(cmd, Exclude));
    }

    [Fact]
    public void NoExistingFilter_AppendsFreshFilter()
    {
        const string cmd = "dotnet test src/Foo/Foo.IntegrationTest.csproj";
        var result = CrewStoryArtifactStrategy.ApplyTestExcludeFilter(cmd, Exclude);
        Assert.Equal(
            "dotnet test src/Foo/Foo.IntegrationTest.csproj --filter \"" + Exclude + "\"",
            result);
    }

    [Fact]
    public void ExistingQuotedFilter_AndMerges()
    {
        // The Roslyn impacted-test provider can emit a method-level
        // `--filter "FullyQualifiedName~..."`; the exclusion must AND into
        // it, not clobber it.
        const string cmd = "dotnet test Foo.csproj --filter \"FullyQualifiedName~Foo.BarTests\"";
        var result = CrewStoryArtifactStrategy.ApplyTestExcludeFilter(cmd, Exclude);
        Assert.Equal(
            "dotnet test Foo.csproj --filter \"(FullyQualifiedName~Foo.BarTests)&(" + Exclude + ")\"",
            result);
    }

    [Fact]
    public void ExistingBareFilter_AndMerges()
    {
        const string cmd = "dotnet test --filter Category=e2e";
        var result = CrewStoryArtifactStrategy.ApplyTestExcludeFilter(cmd, Exclude);
        Assert.Equal(
            "dotnet test --filter \"(Category=e2e)&(" + Exclude + ")\"",
            result);
    }

    [Fact]
    public void PreservesTrailingArgsAfterFilter()
    {
        const string cmd =
            "dotnet test Foo.csproj --filter \"FullyQualifiedName~A\" --logger trx";
        var result = CrewStoryArtifactStrategy.ApplyTestExcludeFilter(cmd, Exclude);
        Assert.Equal(
            "dotnet test Foo.csproj --filter \"(FullyQualifiedName~A)&(" + Exclude + ")\" --logger trx",
            result);
    }

    // ── SplitStandingFilter (2026-05-26) ─────────────────────────────────
    // Pulls a repo's STANDING --filter off the command before impacted-test
    // resolution (every provider treats a bare --filter as an operator pin
    // and refuses to narrow), to be AND-merged back via ApplyTestExcludeFilter
    // afterward. This is what lets the native Roslyn provider narrow repos
    // like `update` (standing `TestCategory!~…` exclude) instead of leaning on
    // the flaky csharp-ls LSP server.

    [Fact]
    public void Split_ExtractsQuotedFilter_AndStripsIt()
    {
        var (cmd, filter) = CrewStoryArtifactStrategy.SplitStandingFilter(
            "dotnet test build.proj --filter \"TestCategory!~CTQ&TestCategory!~Env\"");
        Assert.Equal("dotnet test build.proj", cmd);
        Assert.Equal("TestCategory!~CTQ&TestCategory!~Env", filter);
    }

    [Fact]
    public void Split_NoFilter_ReturnsNull()
    {
        var (cmd, filter) = CrewStoryArtifactStrategy.SplitStandingFilter("dotnet test build.proj");
        Assert.Equal("dotnet test build.proj", cmd);
        Assert.Null(filter);
    }

    [Theory]
    [InlineData("cargo test --filter foo")]
    [InlineData("go test ./... -run X")]
    public void Split_NonDotnet_IsNoOp(string cmd)
    {
        // Don't strip a non-.NET command's --filter — ApplyTestExcludeFilter
        // (the re-merge) only touches `dotnet test`, so stripping here would
        // silently drop it.
        var (outCmd, filter) = CrewStoryArtifactStrategy.SplitStandingFilter(cmd);
        Assert.Equal(cmd, outCmd);
        Assert.Null(filter);
    }

    [Fact]
    public void SplitThenRemerge_ComposesNarrowedCommandWithStandingFilter()
    {
        // End-to-end of the call-site flow: strip the standing category filter,
        // (resolver narrows the now-clean command to a method-level --filter),
        // then re-merge → the standing exclude AND-composes with the narrowing.
        var (baseCmd, standing) = CrewStoryArtifactStrategy.SplitStandingFilter(
            "dotnet test build.proj --filter \"TestCategory!~CTQ\"");
        Assert.Equal("dotnet test build.proj", baseCmd);

        // What Roslyn would produce from the filter-free command:
        const string narrowed = "dotnet test tests/Widget.Tests.csproj --filter \"FullyQualifiedName~MyApp.WidgetTests.Total\"";
        var final = CrewStoryArtifactStrategy.ApplyTestExcludeFilter(narrowed, standing);

        Assert.Equal(
            "dotnet test tests/Widget.Tests.csproj --filter \"(FullyQualifiedName~MyApp.WidgetTests.Total)&(TestCategory!~CTQ)\"",
            final);
    }

    // gap #1 (2026-05-27) — the caller swaps the Traversal target token
    // (build.proj) for the generated scoped proj before re-merging the filter.
    [Fact]
    public void ReplaceCommandToken_SwapsTraversalTarget_TokenAware()
    {
        var result = CrewStoryArtifactStrategy.ReplaceCommandToken(
            "dotnet test build.proj", "build.proj", ".forge/forge-impacted-tests.proj");
        Assert.Equal("dotnet test .forge/forge-impacted-tests.proj", result);
    }

    [Fact]
    public void ReplaceCommandToken_DoesNotMatchSubstrings()
    {
        // Only a whole-token match is replaced — "build.proj" must not hit
        // "prebuild.proj".
        var result = CrewStoryArtifactStrategy.ReplaceCommandToken(
            "dotnet test prebuild.proj", "build.proj", ".forge/scoped.proj");
        Assert.Equal("dotnet test prebuild.proj", result);
    }
}
