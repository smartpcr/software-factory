// -----------------------------------------------------------------------
// <copyright file="VersionRangeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Memory.Models;

[TestClass]
public class VersionRangeTests
{
    [TestMethod]
    public void VersionRange_Record_StoresAllFields()
    {
        var vr = new VersionRange("abc123", 42, "def456", 99);

        vr.Since.Should().Be("abc123");
        vr.SinceIndex.Should().Be(42);
        vr.Until.Should().Be("def456");
        vr.UntilIndex.Should().Be(99);
    }

    [TestMethod]
    public void VersionRange_OpenEnded_UntilFieldsAreNull()
    {
        var vr = new VersionRange("abc123", 1, null, null);

        vr.Until.Should().BeNull();
        vr.UntilIndex.Should().BeNull();
    }

    [TestMethod]
    public void VersionRange_ValueEquality_RecordSemantic()
    {
        var a = new VersionRange("abc", 1, "def", 2);
        var b = new VersionRange("abc", 1, "def", 2);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [TestMethod]
    public void VersionRange_Inequality_DifferentValues()
    {
        var a = new VersionRange("abc", 1, null, null);
        var b = new VersionRange("xyz", 2, null, null);

        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }
}
