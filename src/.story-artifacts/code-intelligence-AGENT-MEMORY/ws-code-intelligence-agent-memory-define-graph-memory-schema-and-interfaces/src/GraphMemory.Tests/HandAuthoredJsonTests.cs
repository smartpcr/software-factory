using System.Text.Json;
using GraphMemory.Models;
using GraphMemory.Serialization;

namespace GraphMemory.Tests;

/// <summary>
/// Tests that hand-authored JSON (representing an external producer) deserializes
/// correctly through the custom converters — catches wire-contract issues that
/// round-tripping through our own serializer would mask.
/// </summary>
public class HandAuthoredJsonTests
{
    private static readonly JsonSerializerOptions Opts = GraphJsonOptions.Default;

    // -----------------------------------------------------------------------
    // Node deserialization from hand-written JSON
    // -----------------------------------------------------------------------

    [Fact]
    public void Deserialise_File_Node_From_External_Json()
    {
        var json = """
        {
            "id": "f1",
            "kind": "file",
            "name": "app.ts",
            "properties": {
                "path": "src/app.ts",
                "language": "typescript",
                "loc": 120,
                "hash": "sha256:abc"
            },
            "layer": "structural",
            "version": 2,
            "stale": false,
            "contentHash": "sha256:xyz"
        }
        """;

        var node = JsonSerializer.Deserialize<GraphNode>(json, Opts)!;
        Assert.Equal("f1", node.Id);
        Assert.Equal(NodeKind.File, node.Kind);
        Assert.Equal("app.ts", node.Name);
        Assert.Equal(Layer.Structural, node.Layer);
        Assert.Equal(2, node.Version);
        Assert.False(node.Stale);
        Assert.Equal("sha256:xyz", node.ContentHash);

        var props = Assert.IsType<FileProperties>(node.Properties);
        Assert.Equal("src/app.ts", props.Path);
        Assert.Equal("typescript", props.Language);
        Assert.Equal(120, props.Loc);
        Assert.Equal("sha256:abc", props.Hash);
    }

    [Fact]
    public void Deserialise_Method_Node_With_Visibility()
    {
        var json = """
        {
            "id": "m1",
            "kind": "method",
            "name": "getData",
            "properties": {
                "filePath": "src/service.ts",
                "startLine": 10,
                "endLine": 30,
                "parameters": [
                    { "name": "id", "typeAnnotation": "string", "isOptional": false },
                    { "name": "opts", "typeAnnotation": "Options", "isOptional": true, "defaultValue": "{}" }
                ],
                "visibility": "protected",
                "isAsync": true,
                "isStatic": false,
                "returnType": "Promise<Data>"
            }
        }
        """;

        var node = JsonSerializer.Deserialize<GraphNode>(json, Opts)!;
        Assert.Equal(NodeKind.Method, node.Kind);

        var props = Assert.IsType<MethodProperties>(node.Properties);
        Assert.Equal("src/service.ts", props.FilePath);
        Assert.Equal(10, props.StartLine);
        Assert.Equal(30, props.EndLine);
        Assert.Equal(2, props.Parameters.Length);
        Assert.Equal("id", props.Parameters[0].Name);
        Assert.Equal("string", props.Parameters[0].TypeAnnotation);
        Assert.False(props.Parameters[0].IsOptional);
        Assert.Equal("opts", props.Parameters[1].Name);
        Assert.True(props.Parameters[1].IsOptional);
        Assert.Equal("{}", props.Parameters[1].DefaultValue);
        Assert.Equal(Visibility.Protected, props.Visibility);
        Assert.True(props.IsAsync);
        Assert.False(props.IsStatic);
        Assert.Equal("Promise<Data>", props.ReturnType);
    }

    [Fact]
    public void Deserialise_Variable_Node_With_VariableKind()
    {
        var json = """
        {
            "id": "v1",
            "kind": "variable",
            "name": "MAX_SIZE",
            "properties": {
                "filePath": "src/config.ts",
                "startLine": 5,
                "endLine": 5,
                "variableKind": "const",
                "isExported": true,
                "typeAnnotation": "number"
            }
        }
        """;

        var node = JsonSerializer.Deserialize<GraphNode>(json, Opts)!;
        var props = Assert.IsType<VariableProperties>(node.Properties);
        Assert.Equal(VariableKind.Const, props.VariableKind);
        Assert.True(props.IsExported);
        Assert.Equal("number", props.TypeAnnotation);
    }

    [Fact]
    public void Deserialise_Node_With_Full_Provenance_And_VersionRange()
    {
        var json = """
        {
            "id": "cls1",
            "kind": "class",
            "name": "Service",
            "properties": {
                "filePath": "src/service.ts",
                "startLine": 1,
                "endLine": 50,
                "isAbstract": true,
                "isExported": true
            },
            "qualifiedName": "src/service.ts::Service",
            "repoId": "repo-1",
            "provenance": {
                "analyzer": "ast-parser",
                "analyzerVersion": "2.0.0",
                "analyzedAt": "2026-05-04T00:00:00Z",
                "commitSha": "abc123"
            },
            "versionRange": {
                "introducedIn": "commit-aaa",
                "lastSeenIn": "commit-bbb",
                "removedIn": "commit-ccc"
            },
            "embedding": [0.1, 0.2, 0.3, 0.4, 0.5],
            "snapshotId": "snap-42",
            "stale": true
        }
        """;

        var node = JsonSerializer.Deserialize<GraphNode>(json, Opts)!;
        Assert.Equal("src/service.ts::Service", node.QualifiedName);
        Assert.Equal("repo-1", node.RepoId);
        Assert.NotNull(node.Provenance);
        Assert.Equal("ast-parser", node.Provenance!.Analyzer);
        Assert.Equal("2.0.0", node.Provenance.AnalyzerVersion);
        Assert.Equal("2026-05-04T00:00:00Z", node.Provenance.AnalyzedAt);
        Assert.Equal("abc123", node.Provenance.CommitSha);
        Assert.NotNull(node.VersionRange);
        Assert.Equal("commit-aaa", node.VersionRange!.IntroducedIn);
        Assert.Equal("commit-bbb", node.VersionRange.LastSeenIn);
        Assert.Equal("commit-ccc", node.VersionRange.RemovedIn);
        Assert.Equal(5, node.Embedding!.Length);
        Assert.Equal("snap-42", node.SnapshotId);
        Assert.True(node.Stale);
    }

    // -----------------------------------------------------------------------
    // Edge deserialization from hand-written JSON
    // -----------------------------------------------------------------------

    [Fact]
    public void Deserialise_SimilarTo_Edge_With_Cosine_Similarity()
    {
        var json = """
        {
            "id": "e1",
            "kind": "similar_to",
            "source": "n1",
            "target": "n2",
            "properties": {
                "cosine_similarity": 0.92,
                "algorithm": "openai-ada-002"
            },
            "layer": "semantic"
        }
        """;

        var edge = JsonSerializer.Deserialize<GraphEdge>(json, Opts)!;
        Assert.Equal(EdgeKind.SimilarTo, edge.Kind);
        Assert.Equal(Layer.Semantic, edge.Layer);

        var props = Assert.IsType<SimilarToProperties>(edge.Properties);
        Assert.Equal(0.92, props.CosineSimilarity, precision: 3);
        Assert.Equal("openai-ada-002", props.Algorithm);
    }

    [Fact]
    public void Deserialise_Imports_Edge_With_Symbols()
    {
        var json = """
        {
            "id": "e2",
            "kind": "imports",
            "source": "file-a",
            "target": "file-b",
            "properties": {
                "importedSymbols": [
                    { "name": "Foo", "alias": "Bar" },
                    { "name": "Baz" }
                ],
                "isTypeOnly": true,
                "isDefault": false
            }
        }
        """;

        var edge = JsonSerializer.Deserialize<GraphEdge>(json, Opts)!;
        var props = Assert.IsType<ImportsProperties>(edge.Properties);
        Assert.Equal(2, props.ImportedSymbols!.Length);
        Assert.Equal("Foo", props.ImportedSymbols[0].Name);
        Assert.Equal("Bar", props.ImportedSymbols[0].Alias);
        Assert.Equal("Baz", props.ImportedSymbols[1].Name);
        Assert.Null(props.ImportedSymbols[1].Alias);
        Assert.True(props.IsTypeOnly);
        Assert.False(props.IsDefault);
    }

    [Fact]
    public void Deserialise_Expertise_Edge_With_All_Fields()
    {
        var json = """
        {
            "id": "e3",
            "kind": "expertise",
            "source": "note-1",
            "target": "fn-1",
            "properties": {
                "weight": 0.85,
                "confidence": 0.92,
                "insight": "Critical performance path",
                "reinforcementCount": 7,
                "relationship": "performance",
                "lastUpdated": "2026-05-04T12:00:00Z",
                "observationCount": 15
            }
        }
        """;

        var edge = JsonSerializer.Deserialize<GraphEdge>(json, Opts)!;
        var props = Assert.IsType<ExpertiseProperties>(edge.Properties);
        Assert.Equal(0.85, props.Weight, precision: 3);
        Assert.Equal(0.92, props.Confidence, precision: 3);
        Assert.Equal("Critical performance path", props.Insight);
        Assert.Equal(7, props.ReinforcementCount);
        Assert.Equal("performance", props.Relationship);
        Assert.Equal("2026-05-04T12:00:00Z", props.LastUpdated);
        Assert.Equal(15, props.ObservationCount);
    }

    [Fact]
    public void Deserialise_Calls_Edge_With_CallSite()
    {
        var json = """
        {
            "id": "e4",
            "kind": "calls",
            "source": "fn-a",
            "target": "fn-b",
            "properties": {
                "callSite": { "line": 42, "column": 8 },
                "isConditional": true,
                "isDynamic": false
            },
            "layer": "call_chain"
        }
        """;

        var edge = JsonSerializer.Deserialize<GraphEdge>(json, Opts)!;
        Assert.Equal(EdgeKind.Calls, edge.Kind);
        Assert.Equal(Layer.CallChain, edge.Layer);

        var props = Assert.IsType<CallsProperties>(edge.Properties);
        Assert.NotNull(props.CallSite);
        Assert.Equal(42, props.CallSite!.Line);
        Assert.Equal(8, props.CallSite.Column);
        Assert.True(props.IsConditional);
        Assert.False(props.IsDynamic);
    }

    [Fact]
    public void Deserialise_Inherits_Edge_With_Mechanism()
    {
        var json = """
        {
            "id": "e5",
            "kind": "inherits",
            "source": "cls-child",
            "target": "cls-parent",
            "properties": {
                "mechanism": "extends"
            }
        }
        """;

        var edge = JsonSerializer.Deserialize<GraphEdge>(json, Opts)!;
        var props = Assert.IsType<InheritsProperties>(edge.Properties);
        Assert.Equal(InheritanceMechanism.Extends, props.Mechanism);
    }

    [Fact]
    public void Deserialise_FrequentlyUsedWith_Edge()
    {
        var json = """
        {
            "id": "e6",
            "kind": "frequently_used_with",
            "source": "fn-a",
            "target": "fn-b",
            "properties": {
                "coOccurrenceCount": 100,
                "strength": 0.95,
                "window": "file"
            }
        }
        """;

        var edge = JsonSerializer.Deserialize<GraphEdge>(json, Opts)!;
        var props = Assert.IsType<FrequentlyUsedWithProperties>(edge.Properties);
        Assert.Equal(100, props.CoOccurrenceCount);
        Assert.Equal(0.95, props.Strength!.Value, precision: 3);
        Assert.Equal("file", props.Window);
    }

    // -----------------------------------------------------------------------
    // Full graph deserialization from hand-written JSON
    // -----------------------------------------------------------------------

    [Fact]
    public void Deserialise_Minimal_Graph_From_External_Json()
    {
        var json = """
        {
            "schemaVersion": "0.3.0",
            "id": "external-graph-1",
            "nodes": [
                {
                    "id": "r1",
                    "kind": "repo",
                    "name": "test-repo",
                    "properties": { "languages": ["csharp"] }
                },
                {
                    "id": "f1",
                    "kind": "file",
                    "name": "Program.cs",
                    "properties": { "path": "src/Program.cs" }
                }
            ],
            "edges": [
                {
                    "id": "e1",
                    "kind": "contains",
                    "source": "r1",
                    "target": "f1",
                    "properties": { "order": 0 }
                }
            ],
            "metadata": {
                "createdAt": "2026-05-04T00:00:00Z",
                "updatedAt": "2026-05-04T00:00:00Z",
                "createdBy": "external-tool"
            }
        }
        """;

        var graph = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;
        Assert.Equal("0.3.0", graph.SchemaVersion);
        Assert.Equal("external-graph-1", graph.Id);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Single(graph.Edges);
        Assert.Equal("external-tool", graph.Metadata.CreatedBy);

        Assert.IsType<RepoProperties>(graph.Nodes[0].Properties);
        Assert.IsType<FileProperties>(graph.Nodes[1].Properties);
        Assert.IsType<ContainsProperties>(graph.Edges[0].Properties);
    }

    // -----------------------------------------------------------------------
    // Error handling: bad input
    // -----------------------------------------------------------------------

    [Fact]
    public void Deserialise_Node_With_Unknown_Kind_Throws()
    {
        var json = """
        {
            "id": "x1",
            "kind": "unknown_kind",
            "name": "bad",
            "properties": {}
        }
        """;

        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<GraphNode>(json, Opts));
    }

    [Fact]
    public void Deserialise_Edge_With_Unknown_Kind_Throws()
    {
        var json = """
        {
            "id": "x1",
            "kind": "unknown_kind",
            "source": "a",
            "target": "b",
            "properties": {}
        }
        """;

        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<GraphEdge>(json, Opts));
    }

    [Fact]
    public void Deserialise_Node_Missing_Kind_Throws()
    {
        var json = """
        {
            "id": "x1",
            "name": "bad",
            "properties": {}
        }
        """;

        Assert.ThrowsAny<Exception>(() =>
            JsonSerializer.Deserialize<GraphNode>(json, Opts));
    }

    [Fact]
    public void Deserialise_Node_Missing_Properties_Throws()
    {
        var json = """
        {
            "id": "x1",
            "kind": "file",
            "name": "bad"
        }
        """;

        Assert.ThrowsAny<Exception>(() =>
            JsonSerializer.Deserialize<GraphNode>(json, Opts));
    }

    // -----------------------------------------------------------------------
    // Minimal properties (only required fields)
    // -----------------------------------------------------------------------

    [Fact]
    public void Deserialise_Calls_Edge_With_Empty_Properties()
    {
        var json = """
        {
            "id": "e-min",
            "kind": "calls",
            "source": "a",
            "target": "b",
            "properties": {}
        }
        """;

        var edge = JsonSerializer.Deserialize<GraphEdge>(json, Opts)!;
        var props = Assert.IsType<CallsProperties>(edge.Properties);
        Assert.Null(props.CallSite);
        Assert.Null(props.IsConditional);
        Assert.Null(props.IsDynamic);
    }

    [Fact]
    public void Deserialise_Contains_Edge_With_Empty_Properties()
    {
        var json = """
        {
            "id": "e-min",
            "kind": "contains",
            "source": "a",
            "target": "b",
            "properties": {}
        }
        """;

        var edge = JsonSerializer.Deserialize<GraphEdge>(json, Opts)!;
        var props = Assert.IsType<ContainsProperties>(edge.Properties);
        Assert.Null(props.Order);
    }

    // -----------------------------------------------------------------------
    // Every node kind can round-trip through hand-authored minimal JSON
    // -----------------------------------------------------------------------

    [Fact]
    public void Deserialise_All_Expertise_Layer_Nodes()
    {
        string[] jsons = [
            """{"id":"n1","kind":"expertise_note","name":"note","properties":{"content":"test content","author":"dev","tags":["a"]}}""",
            """{"id":"n2","kind":"pattern","name":"pat","properties":{"content":"pattern content","confidence":0.8}}""",
            """{"id":"n3","kind":"pitfall","name":"pit","properties":{"content":"pitfall content","severity":"high"}}""",
            """{"id":"n4","kind":"preference","name":"pref","properties":{"content":"prefer this","author":"lead"}}""",
            """{"id":"n5","kind":"insight","name":"ins","properties":{"content":"insight content","confidence":0.7,"source":"profiler"}}""",
        ];

        foreach (var json in jsons)
        {
            var node = JsonSerializer.Deserialize<GraphNode>(json, Opts)!;
            Assert.NotNull(node);
            Assert.True(node.Kind is NodeKind.ExpertiseNote or NodeKind.Pattern or NodeKind.Pitfall or NodeKind.Preference or NodeKind.Insight);
        }
    }

    [Fact]
    public void Deserialise_All_Semantic_Layer_Nodes()
    {
        string[] jsons = [
            """{"id":"sc1","kind":"semantic_cluster","name":"cluster","properties":{"label":"auth","centroid":[0.1,0.2],"memberCount":5}}""",
            """{"id":"t1","kind":"topic","name":"topic","properties":{"description":"desc","keywords":["a","b"]}}""",
        ];

        foreach (var json in jsons)
        {
            var node = JsonSerializer.Deserialize<GraphNode>(json, Opts)!;
            Assert.NotNull(node);
        }
    }
}
