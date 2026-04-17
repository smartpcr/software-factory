// -----------------------------------------------------------------------
// <copyright file="VersionInfoTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Version;

using WorkflowUi.Server.Version;
using Xunit;

public class VersionInfoTests
{
    [Fact]
    public void Current_ReturnsNonNull()
    {
        var info = VersionInfo.Current;
        Assert.NotNull(info);
        Assert.NotNull(info.Version);
        Assert.NotNull(info.CommitSha);
        Assert.NotNull(info.BuildUtc);
    }

    [Fact]
    public void Version_IsNonEmpty()
    {
        var info = VersionInfo.Current;
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    [Fact]
    public void CommitSha_IsNonEmpty()
    {
        var info = VersionInfo.Current;
        Assert.False(string.IsNullOrWhiteSpace(info.CommitSha));
    }

    [Fact]
    public void BuildUtc_IsValidIso8601()
    {
        var info = VersionInfo.Current;
        Assert.True(DateTime.TryParse(info.BuildUtc, out _));
    }

    [Fact]
    public void Current_IsCached()
    {
        var a = VersionInfo.Current;
        var b = VersionInfo.Current;
        Assert.Same(a, b);
    }
}
