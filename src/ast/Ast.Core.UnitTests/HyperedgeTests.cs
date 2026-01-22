// -----------------------------------------------------------------------
// <copyright file="HyperedgeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core.UnitTests;

[TestClass]
public class HyperedgeTests
{
    [TestMethod]
    public void Constructor_WithRequiredProperties_CreatesValidEdge()
    {
        // Arrange & Act
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["node_a", "node_b", "node_c"],
            Roles = new Dictionary<string, string>
            {
                ["node_a"] = "caller",
                ["node_b"] = "callee",
                ["node_c"] = "arg_0"
            }
        };

        // Assert
        edge.Id.Should().Be("edge_001");
        edge.EdgeType.Should().Be(HyperedgeType.MethodInvocation);
        edge.NodeIds.Should().HaveCount(3);
        edge.Roles.Should().HaveCount(3);
    }

    [TestMethod]
    public void Constructor_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.ClassDefinition,
            NodeIds = ["node_a"],
            Roles = new Dictionary<string, string> { ["node_a"] = "class" }
        };

        // Assert
        edge.Languages.Should().BeEmpty();
        edge.Weight.Should().Be(1.0);
        edge.Metadata.Should().BeEmpty();
    }

    [TestMethod]
    public void Cardinality_ReturnsNodeCount()
    {
        // Arrange
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.MethodDefinition,
            NodeIds = ["method", "param1", "param2", "param3", "return_type"],
            Roles = new Dictionary<string, string>
            {
                ["method"] = "defined_method",
                ["param1"] = "parameter_0",
                ["param2"] = "parameter_1",
                ["param3"] = "parameter_2",
                ["return_type"] = "return_type"
            }
        };

        // Act & Assert
        edge.Cardinality.Should().Be(5);
    }

    [TestMethod]
    public void IsCrossLanguage_SingleLanguage_ReturnsFalse()
    {
        // Arrange
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.ClassDefinition,
            NodeIds = ["node_a", "node_b"],
            Roles = new Dictionary<string, string>
            {
                ["node_a"] = "class",
                ["node_b"] = "base_type"
            },
            Languages = ["csharp"]
        };

        // Act & Assert
        edge.IsCrossLanguage.Should().BeFalse();
    }

    [TestMethod]
    public void IsCrossLanguage_MultipleLanguages_ReturnsTrue()
    {
        // Arrange
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.CrossLanguageTypeReference,
            NodeIds = ["cs_type", "ps_reference"],
            Roles = new Dictionary<string, string>
            {
                ["cs_type"] = "dotnet_definition",
                ["ps_reference"] = "ps_reference"
            },
            Languages = ["csharp", "powershell"]
        };

        // Act & Assert
        edge.IsCrossLanguage.Should().BeTrue();
    }

    [TestMethod]
    public void IsCrossLanguage_DuplicateLanguages_ReturnsFalse()
    {
        // Arrange
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["a", "b"],
            Roles = new Dictionary<string, string> { ["a"] = "caller", ["b"] = "callee" },
            Languages = ["csharp", "csharp", "csharp"]
        };

        // Act & Assert
        edge.IsCrossLanguage.Should().BeFalse();
    }

    [TestMethod]
    public void GetEmbeddingText_ReturnsFormattedText()
    {
        // Arrange
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["caller_node", "callee_node", "arg1_node"],
            Roles = new Dictionary<string, string>
            {
                ["caller_node"] = "caller",
                ["callee_node"] = "callee",
                ["arg1_node"] = "arg_0"
            }
        };

        // Act
        var embeddingText = edge.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Edge: MethodInvocation");
        embeddingText.Should().Contain("Nodes: caller_node, callee_node, arg1_node");
        embeddingText.Should().Contain("Roles:");
    }

    [TestMethod]
    public void GetEmbeddingText_CrossLanguage_IncludesLanguageInfo()
    {
        // Arrange
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.CrossLanguageMethodCall,
            NodeIds = ["cs_method", "ps_call"],
            Roles = new Dictionary<string, string>
            {
                ["cs_method"] = "dotnet_definition",
                ["ps_call"] = "ps_reference"
            },
            Languages = ["csharp", "powershell"]
        };

        // Act
        var embeddingText = edge.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Cross-language: csharp, powershell");
    }

    [TestMethod]
    [DataRow(HyperedgeType.ClassDefinition)]
    [DataRow(HyperedgeType.InterfaceDefinition)]
    [DataRow(HyperedgeType.MethodInvocation)]
    [DataRow(HyperedgeType.Pipeline)]
    [DataRow(HyperedgeType.DscConfiguration)]
    public void EdgeType_AllEnumValues_CanBeUsed(HyperedgeType edgeType)
    {
        // Arrange & Act
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = edgeType,
            NodeIds = ["node_a"],
            Roles = new Dictionary<string, string> { ["node_a"] = "test" }
        };

        // Assert
        edge.EdgeType.Should().Be(edgeType);
    }

    [TestMethod]
    public void Metadata_CanStoreComplexData()
    {
        // Arrange
        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.LinqQuery,
            NodeIds = ["source", "result"],
            Roles = new Dictionary<string, string>
            {
                ["source"] = "from",
                ["result"] = "select"
            },
            Metadata = new Dictionary<string, object?>
            {
                ["has_where"] = true,
                ["has_orderby"] = false,
                ["projection_type"] = "anonymous"
            }
        };

        // Act & Assert
        edge.Metadata["has_where"].Should().Be(true);
        edge.Metadata["has_orderby"].Should().Be(false);
        edge.Metadata["projection_type"].Should().Be("anonymous");
    }
}
