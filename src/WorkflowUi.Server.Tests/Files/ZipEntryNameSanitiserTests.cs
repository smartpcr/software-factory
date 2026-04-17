// -----------------------------------------------------------------------
// <copyright file="ZipEntryNameSanitiserTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Files;

using WorkflowUi.Server.Files;
using Xunit;

public class ZipEntryNameSanitiserTests
{
    [Theory]
    [InlineData("workflow.yaml", true)]
    [InlineData("sub/workflow.yaml", true)]
    [InlineData("deep/sub/file.json", true)]
    public void Safe_Names_Accepted(string name, bool expected)
    {
        Assert.Equal(expected, ZipEntryNameSanitiser.IsSafe(name));
    }

    [Theory]
    [InlineData("../bad.yaml")]
    [InlineData("sub/../../../etc/shadow")]
    [InlineData("C:\\absolute\\path")]
    [InlineData("/absolute/path")]
    [InlineData("\\absolute\\path")]
    [InlineData("D:file")]
    [InlineData("")]
    [InlineData(null)]
    public void Unsafe_Names_Rejected(string? name)
    {
        Assert.False(ZipEntryNameSanitiser.IsSafe(name!));
    }
}
