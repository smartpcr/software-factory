using CodeIntelligence.GraphMemory.Schema;
using Xunit;

namespace CodeIntelligence.GraphMemory.Tests;

public class SchemaVersionTests
{
    [Fact]
    public void Current_ReturnsExpectedVersion()
    {
        var v = SchemaVersion.Current;
        Assert.Equal(1, v.Major);
        Assert.Equal(0, v.Minor);
        Assert.Equal("1.0", v.ToString());
    }

    [Theory]
    [InlineData(1, 0, true)]   // same version
    [InlineData(1, 1, true)]   // newer minor
    [InlineData(1, 5, true)]   // much newer minor
    [InlineData(2, 0, false)]  // different major
    [InlineData(0, 9, false)]  // older major
    public void IsCompatibleWith_BehavesCorrectly(int otherMajor, int otherMinor, bool expected)
    {
        var current = SchemaVersion.Current;
        var other = new SchemaVersion { Major = otherMajor, Minor = otherMinor };
        Assert.Equal(expected, current.IsCompatibleWith(other));
    }
}
