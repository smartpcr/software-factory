using System.Text.Json;
using CodeIntelligence.GraphMemory.Schema;
using CodeIntelligence.GraphMemory.Serialization;
using Xunit;

namespace CodeIntelligence.GraphMemory.Tests;

public class SerializationTests
{
    private static CodeKnowledgeGraph BuildSampleGraph()
    {
        var repoNode = new GraphNode
        {
            Id = "repo:my-repo",
            Kind = NodeKind.Repo,
            Name = "my-repo",
        };

        var fileNode = new GraphNode
        {
            Id = "file:my-repo:src/Foo.cs",
            Kind = NodeKind.File,
            Name = "Foo.cs",
            QualifiedName = "src/Foo.cs",
            Location = new SourceLocation { FilePath = "src/Foo.cs" },
            Language = "csharp",
        };

        var classNode = new GraphNode
        {
            Id = "class:my-repo:MyNamespace.Foo",
            Kind = NodeKind.Class,
            Name = "Foo",
            QualifiedName = "MyNamespace.Foo",
            Location = new SourceLocation
            {
                FilePath = "src/Foo.cs",
                StartLine = 5,
                StartColumn = 1,
                EndLine = 50,
                EndColumn = 1,
            },
            Language = "csharp",
        };

        var funcNode = new GraphNode
        {
            Id = "function:my-repo:MyNamespace.Foo.Bar:abc123",
            Kind = NodeKind.Function,
            Name = "Bar",
            QualifiedName = "MyNamespace.Foo.Bar",
            Location = new SourceLocation
            {
                FilePath = "src/Foo.cs",
                StartLine = 10,
                EndLine = 20,
            },
            Language = "csharp",
        };

        funcNode.Properties.Set("isAsync", true);
        funcNode.Properties.Set("parameterCount", 3);

        var containsEdge = new GraphEdge
        {
            Id = "edge:contains:repo:file:Foo",
            SourceNodeId = repoNode.Id,
            TargetNodeId = fileNode.Id,
            Kind = EdgeKind.Contains,
        };

        var containsEdge2 = new GraphEdge
        {
            Id = "edge:contains:file:class:Foo",
            SourceNodeId = fileNode.Id,
            TargetNodeId = classNode.Id,
            Kind = EdgeKind.Contains,
        };

        var expertiseEdge = new GraphEdge
        {
            Id = "edge:expertise:agent1:Foo",
            SourceNodeId = classNode.Id,
            TargetNodeId = funcNode.Id,
            Kind = EdgeKind.Expertise,
            Weight = 0.85,
        };

        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes = [repoNode, fileNode, classNode, funcNode],
            Edges = [containsEdge, containsEdge2, expertiseEdge],
        };

        graph.Metadata.Set("producer", "test-harness");
        graph.Metadata.Set("commit", "abc123def");

        return graph;
    }

    [Fact]
    public void Roundtrip_PreservesAllData()
    {
        var graph = BuildSampleGraph();
        var json = GraphJsonDefaults.Serialize(graph);
        var deserialized = GraphJsonDefaults.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(graph.SchemaVersion.Major, deserialized.SchemaVersion.Major);
        Assert.Equal(graph.SchemaVersion.Minor, deserialized.SchemaVersion.Minor);
        Assert.Equal(graph.Nodes.Count, deserialized.Nodes.Count);
        Assert.Equal(graph.Edges.Count, deserialized.Edges.Count);

        // Spot-check node data
        var funcNode = deserialized.Nodes.Single(n => n.Kind == NodeKind.Function);
        Assert.Equal("Bar", funcNode.Name);
        Assert.Equal("MyNamespace.Foo.Bar", funcNode.QualifiedName);
        Assert.NotNull(funcNode.Location);
        Assert.Equal("src/Foo.cs", funcNode.Location.FilePath);
        Assert.Equal(10, funcNode.Location.StartLine);

        // Check property bag roundtrip
        Assert.True(funcNode.Properties.TryGet<bool>("isAsync", out var isAsync));
        Assert.True(isAsync);
        Assert.True(funcNode.Properties.TryGet<int>("parameterCount", out var paramCount));
        Assert.Equal(3, paramCount);

        // Check edge
        var expertiseEdge = deserialized.Edges.Single(e => e.Kind == EdgeKind.Expertise);
        Assert.Equal(0.85, expertiseEdge.Weight);

        // Check metadata
        Assert.True(deserialized.Metadata.TryGet<string>("producer", out var producer));
        Assert.Equal("test-harness", producer);
    }

    [Fact]
    public void Enums_SerializeAsStrings()
    {
        var graph = BuildSampleGraph();
        var json = GraphJsonDefaults.Serialize(graph);

        // Enum values should appear as strings, not numbers.
        Assert.Contains("\"repo\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"file\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"contains\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"expertise\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownNodeKindValue_DeserializesToUnknown()
    {
        // Simulate a future NodeKind from a newer producer.
        // LenientEnumConverter maps unrecognized strings to default(T) = Unknown.
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 1 },
            "nodes": [
                {
                    "id": "repo:test",
                    "kind": "futureKind",
                    "name": "test",
                    "properties": {},
                    "createdAt": "2025-01-01T00:00:00+00:00",
                    "updatedAt": "2025-01-01T00:00:00+00:00"
                }
            ],
            "edges": [],
            "metadata": {}
        }
        """;

        var graph = GraphJsonDefaults.Deserialize(json);

        Assert.NotNull(graph);
        Assert.Equal(NodeKind.Unknown, graph.Nodes[0].Kind);
    }

    [Fact]
    public void UnknownEdgeKindValue_DeserializesToUnknown()
    {
        // Simulate a future EdgeKind from a newer producer.
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 0 },
            "nodes": [
                { "id": "a", "kind": "repo", "name": "a", "properties": {}, "createdAt": "2025-01-01T00:00:00+00:00", "updatedAt": "2025-01-01T00:00:00+00:00" },
                { "id": "b", "kind": "file", "name": "b", "properties": {}, "createdAt": "2025-01-01T00:00:00+00:00", "updatedAt": "2025-01-01T00:00:00+00:00" }
            ],
            "edges": [
                { "id": "e1", "sourceNodeId": "a", "targetNodeId": "b", "kind": "futureRelation", "properties": {}, "createdAt": "2025-01-01T00:00:00+00:00" }
            ],
            "metadata": {}
        }
        """;

        var graph = GraphJsonDefaults.Deserialize(json);

        Assert.NotNull(graph);
        Assert.Equal(EdgeKind.Unknown, graph.Edges[0].Kind);
    }

    [Fact]
    public void NullOptionalFields_AreOmittedInJson()
    {
        var node = new GraphNode
        {
            Id = "repo:test",
            Kind = NodeKind.Repo,
            Name = "test",
        };

        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes = [node],
        };

        var json = GraphJsonDefaults.Serialize(graph);

        // QualifiedName, Location, Language are null → should not appear
        Assert.DoesNotContain("qualifiedName", json);
        Assert.DoesNotContain("location", json);
        Assert.DoesNotContain("language", json);
    }

    // ── Forward-compatibility deserialization tests ─────────────────

    [Fact]
    public void UnknownPropertiesOnNode_AreIgnored()
    {
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 1 },
            "nodes": [
                {
                    "id": "repo:test",
                    "kind": "repo",
                    "name": "test",
                    "futureField": "future-value",
                    "anotherFuture": 42,
                    "properties": {},
                    "createdAt": "2025-01-01T00:00:00+00:00",
                    "updatedAt": "2025-01-01T00:00:00+00:00"
                }
            ],
            "edges": [],
            "metadata": {}
        }
        """;

        var graph = GraphJsonDefaults.Deserialize(json);
        Assert.NotNull(graph);
        Assert.Equal("repo:test", graph.Nodes[0].Id);
    }

    [Fact]
    public void UnknownPropertiesOnEdge_AreIgnored()
    {
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 0 },
            "nodes": [
                { "id": "a", "kind": "repo", "name": "a", "properties": {}, "createdAt": "2025-01-01T00:00:00+00:00", "updatedAt": "2025-01-01T00:00:00+00:00" },
                { "id": "b", "kind": "file", "name": "b", "properties": {}, "createdAt": "2025-01-01T00:00:00+00:00", "updatedAt": "2025-01-01T00:00:00+00:00" }
            ],
            "edges": [
                { "id": "e1", "sourceNodeId": "a", "targetNodeId": "b", "kind": "contains", "futureEdgeProp": true, "properties": {}, "createdAt": "2025-01-01T00:00:00+00:00" }
            ],
            "metadata": {}
        }
        """;

        var graph = GraphJsonDefaults.Deserialize(json);
        Assert.NotNull(graph);
        Assert.Single(graph.Edges);
    }

    [Fact]
    public void UnknownPropertiesOnTopLevel_AreIgnored()
    {
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 0 },
            "nodes": [],
            "edges": [],
            "metadata": {},
            "futureTopLevel": { "nested": true }
        }
        """;

        var graph = GraphJsonDefaults.Deserialize(json);
        Assert.NotNull(graph);
    }

    [Fact]
    public void UnknownPropertiesOnSourceLocation_AreIgnored()
    {
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 0 },
            "nodes": [
                {
                    "id": "f:test:a.cs",
                    "kind": "file",
                    "name": "a.cs",
                    "location": { "filePath": "a.cs", "startLine": 1, "futureLocField": "x" },
                    "properties": {},
                    "createdAt": "2025-01-01T00:00:00+00:00",
                    "updatedAt": "2025-01-01T00:00:00+00:00"
                }
            ],
            "edges": [],
            "metadata": {}
        }
        """;

        var graph = GraphJsonDefaults.Deserialize(json);
        Assert.NotNull(graph);
        Assert.Equal("a.cs", graph.Nodes[0].Location!.FilePath);
    }
}
