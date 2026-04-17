// -----------------------------------------------------------------------
// <copyright file="ETagComputerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Persistence;

using WorkflowUi.Server.Persistence;
using Xunit;

public class ETagComputerTests
{
    [Fact]
    public void Same_Content_Produces_Same_ETag()
    {
        var etag1 = ETagComputer.Compute("yaml: true", "{}");
        var etag2 = ETagComputer.Compute("yaml: true", "{}");
        Assert.Equal(etag1, etag2);
    }

    [Fact]
    public void Different_Yaml_Produces_Different_ETag()
    {
        var etag1 = ETagComputer.Compute("yaml: true", "{}");
        var etag2 = ETagComputer.Compute("yaml: false", "{}");
        Assert.NotEqual(etag1, etag2);
    }

    [Fact]
    public void Different_Sidecar_Produces_Different_ETag()
    {
        var etag1 = ETagComputer.Compute("yaml: true", "{}");
        var etag2 = ETagComputer.Compute("yaml: true", "{\"x\":1}");
        Assert.NotEqual(etag1, etag2);
    }

    [Fact]
    public void ETag_Is_Lowercase_Hex()
    {
        var etag = ETagComputer.Compute("test", "data");
        Assert.Matches("^[0-9a-f]{64}$", etag);
    }

    [Fact]
    public void Empty_Content_Still_Produces_Valid_ETag()
    {
        var etag = ETagComputer.Compute("", "");
        Assert.NotNull(etag);
        Assert.Equal(64, etag.Length);
    }
}
