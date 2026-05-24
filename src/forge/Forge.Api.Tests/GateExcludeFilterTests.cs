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
}
