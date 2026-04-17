// -----------------------------------------------------------------------
// <copyright file="PathSafetyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Persistence;

using WorkflowUi.Server.Persistence;
using Xunit;

public class PathSafetyTests
{
    [Fact]
    public void Valid_Id_Returns_Path()
    {
        var result = PathSafety.CanonicalizeAndCheck("C:\\workflows", "myworkflow");
        Assert.NotNull(result);
        Assert.Contains("myworkflow", result);
    }

    [Fact]
    public void PathTraversal_DotDot_Rejected()
    {
        var result = PathSafety.CanonicalizeAndCheck("C:\\workflows", "../etc/passwd");
        Assert.Null(result);
    }

    [Fact]
    public void PathTraversal_MiddleDotDot_Rejected()
    {
        var result = PathSafety.CanonicalizeAndCheck("C:\\workflows", "sub/../../../etc/shadow");
        Assert.Null(result);
    }

    [Fact]
    public void AbsolutePath_Rejected()
    {
        var result = PathSafety.CanonicalizeAndCheck("C:\\workflows", "C:\\other\\file");
        Assert.Null(result);
    }

    [Fact]
    public void DriveLetterInId_Rejected()
    {
        var result = PathSafety.CanonicalizeAndCheck("C:\\workflows", "D:file");
        Assert.Null(result);
    }

    [Fact]
    public void EmptyId_Rejected()
    {
        var result = PathSafety.CanonicalizeAndCheck("C:\\workflows", "");
        Assert.Null(result);
    }

    [Fact]
    public void EmptyRoot_Rejected()
    {
        var result = PathSafety.CanonicalizeAndCheck("", "myworkflow");
        Assert.Null(result);
    }

    [Fact]
    public void NestedPath_Within_Root_Accepted()
    {
        var result = PathSafety.CanonicalizeAndCheck("C:\\workflows", "sub/deep/workflow");
        Assert.NotNull(result);
        Assert.Contains("sub", result);
    }

    [Fact]
    public void ControlCharInId_Rejected()
    {
        var result = PathSafety.CanonicalizeAndCheck("C:\\workflows", "bad\0name");
        Assert.Null(result);
    }
}
