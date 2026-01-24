// -----------------------------------------------------------------------
// <copyright file="GraphModelsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb.UnitTests;

using Ast.Core;
using FluentAssertions;

[TestClass]
public class GraphModelsTests
{
    [TestMethod]
    public void GraphPath_Length_ReturnsEdgeCount()
    {
        // Arrange
        var path = new GraphPath
        {
            Nodes = { CreateTestNode("1"), CreateTestNode("2"), CreateTestNode("3") },
            Edges =
            {
                CreateTestEdge("e1"),
                CreateTestEdge("e2")
            }
        };

        // Act & Assert
        path.Length.Should().Be(2);
    }

    [TestMethod]
    public void GraphPath_IsCrossLanguage_WithSameLanguage_ReturnsFalse()
    {
        // Arrange
        var path = new GraphPath
        {
            Nodes =
            {
                CreateTestNode("1", "C#"),
                CreateTestNode("2", "C#")
            }
        };

        // Act & Assert
        path.IsCrossLanguage.Should().BeFalse();
    }

    [TestMethod]
    public void GraphPath_IsCrossLanguage_WithDifferentLanguages_ReturnsTrue()
    {
        // Arrange
        var path = new GraphPath
        {
            Nodes =
            {
                CreateTestNode("1", "C#"),
                CreateTestNode("2", "PowerShell")
            }
        };

        // Act & Assert
        path.IsCrossLanguage.Should().BeTrue();
    }

    [TestMethod]
    public void ImpactAnalysis_TotalAffected_ReturnsSumOfDirectAndIndirect()
    {
        // Arrange
        var analysis = new ImpactAnalysis
        {
            SourceNode = CreateTestNode("source"),
            DirectlyAffected =
            [
                new() { Node = CreateTestNode("1"), Depth = 1, RelationshipType = HyperedgeType.MethodInvocation },
                new() { Node = CreateTestNode("2"), Depth = 1, RelationshipType = HyperedgeType.MethodInvocation }
            ],
            IndirectlyAffected =
            [
                new() { Node = CreateTestNode("3"), Depth = 2, RelationshipType = HyperedgeType.MethodInvocation }
            ]
        };

        // Act & Assert
        analysis.TotalAffected.Should().Be(3);
    }

    [TestMethod]
    public void GraphStoreOptions_HasDefaultValues()
    {
        // Arrange
        var options = new GraphStoreOptions();

        // Assert
        options.Uri.Should().Be("bolt://localhost:7687");
        options.Username.Should().Be("neo4j");
        options.Password.Should().Be("password");
        options.Database.Should().Be("neo4j");
        options.MaxConnectionPoolSize.Should().Be(50);
        options.ConnectionTimeoutSeconds.Should().Be(30);
        options.QueryTimeoutSeconds.Should().Be(60);
        options.Encrypted.Should().BeFalse();
        options.BatchSize.Should().Be(1000);
    }

    [TestMethod]
    public void NodeLabels_HasExpectedConstants()
    {
        // Assert
        NodeLabels.CodeNode.Should().Be("CodeNode");
        NodeLabels.Class.Should().Be("Class");
        NodeLabels.Interface.Should().Be("Interface");
        NodeLabels.Method.Should().Be("Method");
        NodeLabels.Function.Should().Be("Function");
        NodeLabels.Property.Should().Be("Property");
        NodeLabels.Variable.Should().Be("Variable");
        NodeLabels.File.Should().Be("File");
    }

    [TestMethod]
    public void RelationshipTypes_HasExpectedConstants()
    {
        // Assert
        RelationshipTypes.Calls.Should().Be("CALLS");
        RelationshipTypes.Implements.Should().Be("IMPLEMENTS");
        RelationshipTypes.Extends.Should().Be("EXTENDS");
        RelationshipTypes.Uses.Should().Be("USES");
        RelationshipTypes.Contains.Should().Be("CONTAINS");
        RelationshipTypes.DependsOn.Should().Be("DEPENDS_ON");
        RelationshipTypes.Imports.Should().Be("IMPORTS");
        RelationshipTypes.DefinesIn.Should().Be("DEFINES_IN");
        RelationshipTypes.FlowsTo.Should().Be("FLOWS_TO");
        RelationshipTypes.CatchesException.Should().Be("CATCHES_EXCEPTION");
    }

    [TestMethod]
    public void NodeFilter_PropertiesCanBeSet()
    {
        // Arrange
        var filter = new NodeFilter
        {
            Language = "C#",
            NodeTypes = new[] { "Class", "Interface" },
            FilePath = "/test/file.cs",
            NamePattern = "Test*",
            Scope = "MyNamespace"
        };

        // Assert
        filter.Language.Should().Be("C#");
        filter.NodeTypes.Should().Contain("Class");
        filter.NodeTypes.Should().Contain("Interface");
        filter.FilePath.Should().Be("/test/file.cs");
        filter.NamePattern.Should().Be("Test*");
        filter.Scope.Should().Be("MyNamespace");
    }

    [TestMethod]
    public void NodeWithDepth_PropertiesWork()
    {
        // Arrange
        var node = CreateTestNode("node1");
        var nodeWithDepth = new NodeWithDepth
        {
            Node = node,
            Depth = 2,
            RelationshipType = HyperedgeType.MethodInvocation,
            EdgeId = "edge1"
        };

        // Assert
        nodeWithDepth.Node.Should().Be(node);
        nodeWithDepth.Depth.Should().Be(2);
        nodeWithDepth.RelationshipType.Should().Be(HyperedgeType.MethodInvocation);
        nodeWithDepth.EdgeId.Should().Be("edge1");
    }

    [TestMethod]
    public void GraphStatistics_DefaultsToEmpty()
    {
        // Arrange
        var stats = new GraphStatistics();

        // Assert
        stats.TotalNodes.Should().Be(0);
        stats.TotalEdges.Should().Be(0);
        stats.NodesByLanguage.Should().BeEmpty();
        stats.NodesByType.Should().BeEmpty();
        stats.EdgesByType.Should().BeEmpty();
        stats.CrossLanguageEdges.Should().Be(0);
        stats.UniqueFiles.Should().Be(0);
    }

    [TestMethod]
    public void GraphStatistics_CanPopulateDictionaries()
    {
        // Arrange
        var stats = new GraphStatistics
        {
            TotalNodes = 100,
            TotalEdges = 200,
            NodesByLanguage =
            {
                ["C#"] = 80,
                ["PowerShell"] = 20
            },
            NodesByType =
            {
                ["Class"] = 30,
                ["Method"] = 60,
                ["Property"] = 10
            },
            EdgesByType =
            {
                ["CALLS"] = 150,
                ["USES"] = 50
            },
            CrossLanguageEdges = 5,
            UniqueFiles = 15
        };

        // Assert
        stats.TotalNodes.Should().Be(100);
        stats.TotalEdges.Should().Be(200);
        stats.NodesByLanguage["C#"].Should().Be(80);
        stats.NodesByLanguage["PowerShell"].Should().Be(20);
        stats.NodesByType["Class"].Should().Be(30);
        stats.NodesByType["Method"].Should().Be(60);
        stats.EdgesByType["CALLS"].Should().Be(150);
        stats.CrossLanguageEdges.Should().Be(5);
        stats.UniqueFiles.Should().Be(15);
    }

    private static HypergraphNode CreateTestNode(string id, string language = "C#")
    {
        return new HypergraphNode
        {
            Id = id,
            Name = "Test" + id,
            NodeType = "Class",
            Language = language,
            FilePath = "/test/file.cs",
            LineStart = 1,
            LineEnd = 10
        };
    }

    private static Hyperedge CreateTestEdge(string id)
    {
        return new Hyperedge
        {
            Id = id,
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["source", "target"],
            Roles = []
        };
    }
}