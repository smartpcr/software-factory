// -----------------------------------------------------------------------
// <copyright file="HypergraphDataTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core.UnitTests;

[TestClass]
public class HypergraphDataTests
{
    [TestMethod]
    public void Constructor_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var data = new HypergraphData();

        // Assert
        data.Nodes.Should().BeEmpty();
        data.Hyperedges.Should().BeEmpty();
        data.Metadata.Should().NotBeNull();
    }

    [TestMethod]
    public void Nodes_CanAddAndRetrieve()
    {
        // Arrange
        var data = new HypergraphData();
        var node = new HypergraphNode
        {
            Id = "node_001",
            Name = "TestClass",
            NodeType = "class",
            Language = "csharp",
            FilePath = "Test.cs"
        };

        // Act
        data.Nodes["node_001"] = node;

        // Assert
        data.Nodes.Should().HaveCount(1);
        data.Nodes["node_001"].Name.Should().Be("TestClass");
    }

    [TestMethod]
    public void Hyperedges_CanAddMultiple()
    {
        // Arrange
        var data = new HypergraphData();
        var edge1 = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.ClassDefinition,
            NodeIds = ["node_a"],
            Roles = new Dictionary<string, string> { ["node_a"] = "class" }
        };
        var edge2 = new Hyperedge
        {
            Id = "edge_002",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["node_b", "node_c"],
            Roles = new Dictionary<string, string>
            {
                ["node_b"] = "caller",
                ["node_c"] = "callee"
            }
        };

        // Act
        data.Hyperedges.Add(edge1);
        data.Hyperedges.Add(edge2);

        // Assert
        data.Hyperedges.Should().HaveCount(2);
    }

    [TestMethod]
    public void Metadata_CanSetProperties()
    {
        // Arrange & Act
        var data = new HypergraphData
        {
            Metadata = new HypergraphMetadata
            {
                RepoPath = "/path/to/repo",
                NumFiles = 50,
                NumCSharpNodes = 200,
                NumPowerShellNodes = 30,
                NumCrossLanguageEdges = 5,
                Files = ["file1.cs", "file2.cs", "script.ps1"]
            }
        };

        // Assert
        data.Metadata.RepoPath.Should().Be("/path/to/repo");
        data.Metadata.NumFiles.Should().Be(50);
        data.Metadata.NumCSharpNodes.Should().Be(200);
        data.Metadata.NumPowerShellNodes.Should().Be(30);
        data.Metadata.NumCrossLanguageEdges.Should().Be(5);
        data.Metadata.Files.Should().HaveCount(3);
    }

    [TestMethod]
    public void Metadata_ExtractedAt_HasDefaultValue()
    {
        // Arrange & Act
        var metadata = new HypergraphMetadata();

        // Assert
        metadata.ExtractedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void CompleteGraph_WithNodesAndEdges_IsConsistent()
    {
        // Arrange
        var classNode = new HypergraphNode
        {
            Id = "class_001",
            Name = "Calculator",
            NodeType = "class",
            Language = "csharp",
            FilePath = "Calculator.cs"
        };

        var baseNode = new HypergraphNode
        {
            Id = "interface_001",
            Name = "ICalculator",
            NodeType = "interface",
            Language = "csharp",
            FilePath = "ICalculator.cs"
        };

        var inheritanceEdge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.ClassDefinition,
            NodeIds = ["class_001", "interface_001"],
            Roles = new Dictionary<string, string>
            {
                ["class_001"] = "defined_class",
                ["interface_001"] = "base_type"
            },
            Languages = ["csharp"]
        };

        // Act
        var data = new HypergraphData
        {
            Nodes =
            {
                ["class_001"] = classNode,
                ["interface_001"] = baseNode
            },
            Hyperedges = { inheritanceEdge },
            Metadata = new HypergraphMetadata
            {
                NumFiles = 2,
                NumCSharpNodes = 2
            }
        };

        // Assert
        data.Nodes.Should().HaveCount(2);
        data.Hyperedges.Should().HaveCount(1);

        // Verify edge references valid nodes
        foreach (var nodeId in data.Hyperedges[0].NodeIds)
        {
            data.Nodes.Should().ContainKey(nodeId);
        }
    }
}
