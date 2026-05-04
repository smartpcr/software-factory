using CodeIntelligence.GraphMemory.Schema;
using Xunit;

namespace CodeIntelligence.GraphMemory.Tests;

public class Neo4jMappingTests
{
    [Theory]
    [InlineData(NodeKind.Repo, "Repo")]
    [InlineData(NodeKind.Module, "Module")]
    [InlineData(NodeKind.File, "File")]
    [InlineData(NodeKind.Class, "Class")]
    [InlineData(NodeKind.Function, "Function")]
    [InlineData(NodeKind.Variable, "Variable")]
    [InlineData(NodeKind.Unknown, "Unknown")]
    public void NodeKind_MapsToExpectedLabel(NodeKind kind, string expectedLabel)
    {
        Assert.Equal(expectedLabel, kind.ToNeo4jLabel());
    }

    [Theory]
    [InlineData(EdgeKind.Contains, "CONTAINS")]
    [InlineData(EdgeKind.Calls, "CALLS")]
    [InlineData(EdgeKind.Imports, "IMPORTS")]
    [InlineData(EdgeKind.Inherits, "INHERITS")]
    [InlineData(EdgeKind.Expertise, "EXPERTISE")]
    [InlineData(EdgeKind.SimilarTo, "SIMILAR_TO")]
    [InlineData(EdgeKind.Unknown, "UNKNOWN")]
    public void EdgeKind_MapsToExpectedRelationshipType(EdgeKind kind, string expectedType)
    {
        Assert.Equal(expectedType, kind.ToNeo4jRelationshipType());
    }

    [Fact]
    public void NodeLabels_ContainsAllKinds()
    {
        var allKinds = Enum.GetValues<NodeKind>();
        Assert.Equal(allKinds.Length, Neo4jMapping.NodeLabels.Count);
        foreach (var kind in allKinds)
        {
            Assert.True(Neo4jMapping.NodeLabels.ContainsKey(kind), $"Missing label for {kind}");
        }
    }

    [Fact]
    public void RelationshipTypes_ContainsAllKinds()
    {
        var allKinds = Enum.GetValues<EdgeKind>();
        Assert.Equal(allKinds.Length, Neo4jMapping.RelationshipTypes.Count);
        foreach (var kind in allKinds)
        {
            Assert.True(Neo4jMapping.RelationshipTypes.ContainsKey(kind), $"Missing type for {kind}");
        }
    }
}
