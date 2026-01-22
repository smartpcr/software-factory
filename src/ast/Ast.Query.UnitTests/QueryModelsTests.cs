// -----------------------------------------------------------------------
// <copyright file="QueryModelsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Query.UnitTests;

using Ast.Core;
using Ast.GraphDb;

[TestClass]
public class QueryModelsTests
{
    [TestMethod]
    public void CodeQueryResult_TotalCount_ReturnsSumOfNodesAndSemanticMatches()
    {
        var result = new CodeQueryResult
        {
            Nodes =
            {
                CreateTestNode("1", "Node1"),
                CreateTestNode("2", "Node2")
            },
            SemanticMatches =
            {
                new ScoredNode { Node = CreateTestNode("3", "Node3"), Score = 0.9f }
            }
        };

        result.TotalCount.Should().Be(3);
    }

    [TestMethod]
    public void CodeQueryResult_HasResults_WithNodes_ReturnsTrue()
    {
        var result = new CodeQueryResult
        {
            Nodes = { CreateTestNode("1", "Node1") }
        };

        result.HasResults.Should().BeTrue();
    }

    [TestMethod]
    public void CodeQueryResult_HasResults_WithPaths_ReturnsTrue()
    {
        var result = new CodeQueryResult
        {
            Paths = { new GraphPath { Nodes = { CreateTestNode("1", "Node1") } } }
        };

        result.HasResults.Should().BeTrue();
    }

    [TestMethod]
    public void CodeQueryResult_HasResults_WithImpact_ReturnsTrue()
    {
        var result = new CodeQueryResult
        {
            Impact = new ImpactAnalysis { SourceNode = CreateTestNode("1", "Node1") }
        };

        result.HasResults.Should().BeTrue();
    }

    [TestMethod]
    public void CodeQueryResult_HasResults_WhenEmpty_ReturnsFalse()
    {
        var result = new CodeQueryResult();
        result.HasResults.Should().BeFalse();
    }

    [TestMethod]
    public void ScoredNode_PropertiesWork()
    {
        var node = CreateTestNode("1", "TestNode");
        var scored = new ScoredNode
        {
            Node = node,
            Score = 0.85f
        };

        scored.Node.Should().Be(node);
        scored.Score.Should().Be(0.85f);
    }

    [TestMethod]
    public void QueryOptions_HasDefaultValues()
    {
        var options = new QueryOptions();

        options.Limit.Should().Be(100);
        options.MinScore.Should().Be(0.5f);
        options.IncludeRelated.Should().BeFalse();
        options.TraversalDepth.Should().Be(3);
        options.UseSemanticSearch.Should().BeFalse();
        options.UseGraphSearch.Should().BeTrue();
    }

    [TestMethod]
    public void QueryOptions_CanBeCustomized()
    {
        var options = new QueryOptions
        {
            Limit = 50,
            MinScore = 0.7f,
            IncludeRelated = true,
            TraversalDepth = 5,
            UseSemanticSearch = true,
            UseGraphSearch = false
        };

        options.Limit.Should().Be(50);
        options.MinScore.Should().Be(0.7f);
        options.IncludeRelated.Should().BeTrue();
        options.TraversalDepth.Should().Be(5);
        options.UseSemanticSearch.Should().BeTrue();
        options.UseGraphSearch.Should().BeFalse();
    }

    [TestMethod]
    public void QueryType_HasAllExpectedValues()
    {
        Enum.GetValues<QueryType>().Should().HaveCount(10);
        Enum.IsDefined(QueryType.FindNodes).Should().BeTrue();
        Enum.IsDefined(QueryType.FindPaths).Should().BeTrue();
        Enum.IsDefined(QueryType.FindCallers).Should().BeTrue();
        Enum.IsDefined(QueryType.FindCallees).Should().BeTrue();
        Enum.IsDefined(QueryType.AnalyzeImpact).Should().BeTrue();
        Enum.IsDefined(QueryType.SemanticSearch).Should().BeTrue();
        Enum.IsDefined(QueryType.HybridSearch).Should().BeTrue();
        Enum.IsDefined(QueryType.FindImplementations).Should().BeTrue();
        Enum.IsDefined(QueryType.FindDerivedClasses).Should().BeTrue();
        Enum.IsDefined(QueryType.FindCrossLanguage).Should().BeTrue();
    }

    [TestMethod]
    public void TraversalDirection_HasAllExpectedValues()
    {
        Enum.GetValues<TraversalDirection>().Should().HaveCount(3);
        Enum.IsDefined(TraversalDirection.Outgoing).Should().BeTrue();
        Enum.IsDefined(TraversalDirection.Incoming).Should().BeTrue();
        Enum.IsDefined(TraversalDirection.Both).Should().BeTrue();
    }

    private static HypergraphNode CreateTestNode(string id, string name)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = "Class",
            Language = "C#",
            FilePath = "/test/file.cs",
            LineStart = 1,
            LineEnd = 10
        };
    }
}
