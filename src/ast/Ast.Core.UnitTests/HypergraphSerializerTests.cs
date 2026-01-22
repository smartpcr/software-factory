// -----------------------------------------------------------------------
// <copyright file="HypergraphSerializerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core.UnitTests;

[TestClass]
public class HypergraphSerializerTests
{
    private static HypergraphData CreateSampleGraph()
    {
        var node1 = new HypergraphNode
        {
            Id = "class_001",
            Name = "UserService",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/Services/UserService.cs",
            Scope = "MyApp.Services",
            LineStart = 10,
            LineEnd = 100,
            Modifiers = ["public", "sealed"]
        };

        var node2 = new HypergraphNode
        {
            Id = "interface_001",
            Name = "IUserService",
            NodeType = "interface",
            Language = "csharp",
            FilePath = "src/Services/IUserService.cs",
            Scope = "MyApp.Services",
            LineStart = 5,
            LineEnd = 20
        };

        var edge = new Hyperedge
        {
            Id = "edge_001",
            EdgeType = HyperedgeType.ClassDefinition,
            NodeIds = ["class_001", "interface_001"],
            Roles = new Dictionary<string, string>
            {
                ["class_001"] = "defined_class",
                ["interface_001"] = "base_type"
            },
            Languages = ["csharp"],
            Weight = 1.0
        };

        return new HypergraphData
        {
            Nodes =
            {
                ["class_001"] = node1,
                ["interface_001"] = node2
            },
            Hyperedges = { edge },
            Metadata = new HypergraphMetadata
            {
                RepoPath = "/path/to/repo",
                NumFiles = 2,
                NumCSharpNodes = 2,
                NumPowerShellNodes = 0,
                NumCrossLanguageEdges = 0,
                Files = ["src/Services/UserService.cs", "src/Services/IUserService.cs"],
                ExtractedAt = new DateTime(2026, 1, 21, 12, 0, 0, DateTimeKind.Utc)
            }
        };
    }

    [TestMethod]
    public void ToJson_ValidGraph_ReturnsValidJson()
    {
        // Arrange
        var data = CreateSampleGraph();

        // Act
        var json = HypergraphSerializer.ToJson(data);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("\"nodes\":");
        json.Should().Contain("\"hyperedges\":");
        json.Should().Contain("\"metadata\":");
    }

    [TestMethod]
    public void ToJson_UsesSnakeCaseNaming()
    {
        // Arrange
        var data = CreateSampleGraph();

        // Act
        var json = HypergraphSerializer.ToJson(data);

        // Assert
        json.Should().Contain("\"node_type\":");
        json.Should().Contain("\"file_path\":");
        json.Should().Contain("\"line_start\":");
        json.Should().Contain("\"edge_type\":");
        json.Should().Contain("\"repo_path\":");
        json.Should().Contain("\"num_files\":");
    }

    [TestMethod]
    public void FromJson_ValidJson_ReturnsGraph()
    {
        // Arrange
        var original = CreateSampleGraph();
        var json = HypergraphSerializer.ToJson(original);

        // Act
        var restored = HypergraphSerializer.FromJson(json);

        // Assert
        restored.Should().NotBeNull();
        restored.Nodes.Should().HaveCount(2);
        restored.Hyperedges.Should().HaveCount(1);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesNodeData()
    {
        // Arrange
        var original = CreateSampleGraph();

        // Act
        var json = HypergraphSerializer.ToJson(original);
        var restored = HypergraphSerializer.FromJson(json);

        // Assert
        var originalNode = original.Nodes["class_001"];
        var restoredNode = restored.Nodes["class_001"];

        restoredNode.Id.Should().Be(originalNode.Id);
        restoredNode.Name.Should().Be(originalNode.Name);
        restoredNode.NodeType.Should().Be(originalNode.NodeType);
        restoredNode.Language.Should().Be(originalNode.Language);
        restoredNode.FilePath.Should().Be(originalNode.FilePath);
        restoredNode.Scope.Should().Be(originalNode.Scope);
        restoredNode.LineStart.Should().Be(originalNode.LineStart);
        restoredNode.LineEnd.Should().Be(originalNode.LineEnd);
        restoredNode.Modifiers.Should().BeEquivalentTo(originalNode.Modifiers);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesEdgeData()
    {
        // Arrange
        var original = CreateSampleGraph();

        // Act
        var json = HypergraphSerializer.ToJson(original);
        var restored = HypergraphSerializer.FromJson(json);

        // Assert
        var originalEdge = original.Hyperedges[0];
        var restoredEdge = restored.Hyperedges[0];

        restoredEdge.Id.Should().Be(originalEdge.Id);
        restoredEdge.EdgeType.Should().Be(originalEdge.EdgeType);
        restoredEdge.NodeIds.Should().BeEquivalentTo(originalEdge.NodeIds);
        restoredEdge.Roles.Should().BeEquivalentTo(originalEdge.Roles);
        restoredEdge.Languages.Should().BeEquivalentTo(originalEdge.Languages);
        restoredEdge.Weight.Should().Be(originalEdge.Weight);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesMetadata()
    {
        // Arrange
        var original = CreateSampleGraph();

        // Act
        var json = HypergraphSerializer.ToJson(original);
        var restored = HypergraphSerializer.FromJson(json);

        // Assert
        restored.Metadata.RepoPath.Should().Be(original.Metadata.RepoPath);
        restored.Metadata.NumFiles.Should().Be(original.Metadata.NumFiles);
        restored.Metadata.NumCSharpNodes.Should().Be(original.Metadata.NumCSharpNodes);
        restored.Metadata.NumPowerShellNodes.Should().Be(original.Metadata.NumPowerShellNodes);
        restored.Metadata.NumCrossLanguageEdges.Should().Be(original.Metadata.NumCrossLanguageEdges);
        restored.Metadata.Files.Should().BeEquivalentTo(original.Metadata.Files);
    }

    [TestMethod]
    public void ToHyperNetXFormat_ReturnsEdgeDict()
    {
        // Arrange
        var data = CreateSampleGraph();

        // Act
        var hypernetxJson = HypergraphSerializer.ToHyperNetXFormat(data);

        // Assert
        hypernetxJson.Should().NotBeNullOrEmpty();
        // HyperNetX format uses PascalCase enum names from ToString()
        hypernetxJson.Should().Contain("ClassDefinition:edge_001");
        hypernetxJson.Should().Contain("class_001");
        hypernetxJson.Should().Contain("interface_001");
    }

    [TestMethod]
    public void ToHyperNetXFormat_EdgeKeyContainsTypeAndId()
    {
        // Arrange
        var data = new HypergraphData
        {
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he_001",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = ["a", "b"],
                    Roles = new Dictionary<string, string> { ["a"] = "caller", ["b"] = "callee" }
                }
            }
        };

        // Act
        var json = HypergraphSerializer.ToHyperNetXFormat(data);

        // Assert
        // HyperNetX format uses PascalCase enum names from ToString()
        json.Should().Contain("MethodInvocation:he_001");
    }

    [TestMethod]
    public void FromJson_InvalidJson_ThrowsException()
    {
        // Arrange
        var invalidJson = "{ invalid json }";

        // Act
        Action act = () => HypergraphSerializer.FromJson(invalidJson);

        // Assert
        act.Should().Throw<Exception>();
    }

    [TestMethod]
    public void ToJson_EmptyGraph_ReturnsValidJson()
    {
        // Arrange
        var data = new HypergraphData();

        // Act
        var json = HypergraphSerializer.ToJson(data);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("\"nodes\": {}");
        json.Should().Contain("\"hyperedges\": []");
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesAllEdgeTypes()
    {
        // Arrange
        var edgeTypes = new[]
        {
            HyperedgeType.ClassDefinition,
            HyperedgeType.MethodInvocation,
            HyperedgeType.Pipeline,
            HyperedgeType.CrossLanguageTypeReference,
            HyperedgeType.DscConfiguration
        };

        var data = new HypergraphData();
        for (int i = 0; i < edgeTypes.Length; i++)
        {
            data.Hyperedges.Add(new Hyperedge
            {
                Id = $"edge_{i:D3}",
                EdgeType = edgeTypes[i],
                NodeIds = [$"node_{i}"],
                Roles = new Dictionary<string, string> { [$"node_{i}"] = "test" }
            });
        }

        // Act
        var json = HypergraphSerializer.ToJson(data);
        var restored = HypergraphSerializer.FromJson(json);

        // Assert
        restored.Hyperedges.Should().HaveCount(edgeTypes.Length);
        for (int i = 0; i < edgeTypes.Length; i++)
        {
            restored.Hyperedges[i].EdgeType.Should().Be(edgeTypes[i]);
        }
    }
}
