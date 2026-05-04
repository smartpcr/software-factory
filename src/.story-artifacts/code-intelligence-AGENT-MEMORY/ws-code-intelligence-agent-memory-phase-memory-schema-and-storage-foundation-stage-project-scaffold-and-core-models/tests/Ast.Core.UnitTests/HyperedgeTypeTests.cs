using Ast.Core;
using Xunit;

namespace Ast.Core.UnitTests;

/// <summary>
/// Pre-existing tests for Ast.Core HyperedgeType.
/// These must not regress when MemoryRelation is added (additive-only change).
/// </summary>
public class HyperedgeTypeTests
{
    [Fact]
    public void Contains_HasExpectedValue()
    {
        Assert.Equal(0, (int)HyperedgeType.Contains);
    }

    [Fact]
    public void Calls_HasExpectedValue()
    {
        Assert.Equal(1, (int)HyperedgeType.Calls);
    }

    [Fact]
    public void Imports_HasExpectedValue()
    {
        Assert.Equal(2, (int)HyperedgeType.Imports);
    }

    [Fact]
    public void Inherits_HasExpectedValue()
    {
        Assert.Equal(3, (int)HyperedgeType.Inherits);
    }

    [Fact]
    public void Expertise_HasExpectedValue()
    {
        Assert.Equal(4, (int)HyperedgeType.Expertise);
    }

    [Fact]
    public void SimilarTo_HasExpectedValue()
    {
        Assert.Equal(5, (int)HyperedgeType.SimilarTo);
    }

    [Fact]
    public void HypergraphNode_StoresMetadata()
    {
        var meta = new Dictionary<string, string> { ["key"] = "value" };
        var node = new HypergraphNode("n1", "Class", meta);

        Assert.Equal("n1", node.Id);
        Assert.Equal("Class", node.NodeType);
        Assert.Equal("value", node.Metadata["key"]);
    }

    [Fact]
    public void Hyperedge_StoresMetadata()
    {
        var edge = new Hyperedge("e1", HyperedgeType.Calls, ["a"], ["b"],
            new Dictionary<string, string> { ["foo"] = "bar" });

        Assert.Equal("e1", edge.Id);
        Assert.Equal(HyperedgeType.Calls, edge.EdgeType);
        Assert.Equal("bar", edge.Metadata["foo"]);
    }
}
