using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIntelligence.GraphMemory.Schema;
using CodeIntelligence.GraphMemory.Serialization;
using Json.Schema;
using Xunit;

namespace CodeIntelligence.GraphMemory.Tests;

/// <summary>
/// Validates that JSON produced by <see cref="GraphJsonDefaults"/> conforms to
/// the JSON Schema produced by <see cref="JsonSchemaGenerator"/>.
/// This is the key alignment test: any drift between C# contracts and the schema
/// will surface as a failure here.
/// </summary>
public class SchemaValidationTests
{
    private static JsonSchema LoadGeneratedSchema()
    {
        var schemaJson = JsonSchemaGenerator.Generate();
        return JsonSchema.FromText(schemaJson);
    }

    private static EvaluationResults Validate(JsonSchema schema, string json)
    {
        var node = JsonNode.Parse(json);
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        };
        return schema.Evaluate(node, options);
    }

    [Fact]
    public void FullGraph_ValidatesAgainstSchema()
    {
        var graph = BuildFullGraph();
        var json = GraphJsonDefaults.Serialize(graph);
        var schema = LoadGeneratedSchema();

        var result = Validate(schema, json);

        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void MinimalGraph_ValidatesAgainstSchema()
    {
        // Minimal graph: only required fields, no optional properties.
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
        };

        var json = GraphJsonDefaults.Serialize(graph);
        var schema = LoadGeneratedSchema();

        var result = Validate(schema, json);

        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void NullableFieldsOmitted_ValidatesAgainstSchema()
    {
        // Node with Location present but all nullable int? fields left null.
        // WhenWritingNull omits these fields; the schema must accept their absence.
        var node = new GraphNode
        {
            Id = "file:repo:test.cs",
            Kind = NodeKind.File,
            Name = "test.cs",
            Location = new SourceLocation
            {
                FilePath = "test.cs",
                // StartLine, StartColumn, EndLine, EndColumn are all null.
            },
        };

        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes = [node],
        };

        var json = GraphJsonDefaults.Serialize(graph);
        var schema = LoadGeneratedSchema();

        // Verify the nulls are truly omitted, not serialized as null.
        Assert.DoesNotContain("null", json);

        var result = Validate(schema, json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void AllEdgeKinds_ValidateAgainstSchema()
    {
        // Ensure every EdgeKind (except Unknown) produces valid schema output.
        var schema = LoadGeneratedSchema();
        var nodes = new List<GraphNode>
        {
            new() { Id = "a", Kind = NodeKind.Class, Name = "A" },
            new() { Id = "b", Kind = NodeKind.Function, Name = "B" },
        };

        foreach (var kind in Enum.GetValues<EdgeKind>())
        {
            if (kind == EdgeKind.Unknown) continue;

            var edge = new GraphEdge
            {
                Id = $"e-{kind}",
                SourceNodeId = "a",
                TargetNodeId = "b",
                Kind = kind,
                Weight = kind == EdgeKind.Expertise ? 0.5 : null,
            };

            var graph = new CodeKnowledgeGraph
            {
                SchemaVersion = SchemaVersion.Current,
                Nodes = nodes,
                Edges = [edge],
            };

            var json = GraphJsonDefaults.Serialize(graph);
            var result = Validate(schema, json);
            Assert.True(result.IsValid, $"EdgeKind.{kind} failed: {FormatErrors(result)}");
        }
    }

    [Fact]
    public void AllNodeKinds_ValidateAgainstSchema()
    {
        var schema = LoadGeneratedSchema();

        foreach (var kind in Enum.GetValues<NodeKind>())
        {
            if (kind == NodeKind.Unknown) continue;

            var node = new GraphNode
            {
                Id = $"test:{kind}",
                Kind = kind,
                Name = kind.ToString(),
            };

            var graph = new CodeKnowledgeGraph
            {
                SchemaVersion = SchemaVersion.Current,
                Nodes = [node],
            };

            var json = GraphJsonDefaults.Serialize(graph);
            var result = Validate(schema, json);
            Assert.True(result.IsValid, $"NodeKind.{kind} failed: {FormatErrors(result)}");
        }
    }

    [Fact]
    public void CommittedSchemaFile_MatchesGenerator()
    {
        // Prevents schema file drift: the checked-in schema must match
        // what JsonSchemaGenerator produces.
        var generated = JsonSchemaGenerator.Generate();

        var schemaFilePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "schema", "code-knowledge-graph.schema.json");
        schemaFilePath = Path.GetFullPath(schemaFilePath);

        Assert.True(File.Exists(schemaFilePath), $"Schema file not found at: {schemaFilePath}");

        var committed = File.ReadAllText(schemaFilePath);

        // Normalize both to JsonNode then re-serialize for comparison.
        var generatedNode = JsonNode.Parse(generated);
        var committedNode = JsonNode.Parse(committed);

        var opts = new JsonSerializerOptions { WriteIndented = true };
        var generatedNorm = generatedNode!.ToJsonString(opts);
        var committedNorm = committedNode!.ToJsonString(opts);

        Assert.Equal(generatedNorm, committedNorm);
    }

    [Fact]
    public void InvalidPropertyBagKey_RejectedBySchema()
    {
        // A property bag key starting with a digit violates the propertyNames pattern.
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 0 },
            "nodes": [
                {
                    "id": "repo:test",
                    "kind": "repo",
                    "name": "test",
                    "properties": { "1badKey": "value" },
                    "createdAt": "2025-01-01T00:00:00+00:00",
                    "updatedAt": "2025-01-01T00:00:00+00:00"
                }
            ],
            "edges": [],
            "metadata": {}
        }
        """;

        var schema = LoadGeneratedSchema();
        var result = Validate(schema, json);

        Assert.False(result.IsValid, "Schema should reject property bag key starting with digit.");
    }

    // ── Forward-compatibility schema validation ─────────────────────

    [Fact]
    public void ForwardCompat_UnknownEnumValue_AcceptedBySchema()
    {
        // A newer producer emits a future NodeKind; the schema must accept it
        // because enum definitions use "examples" not strict "enum".
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 2 },
            "nodes": [
                {
                    "id": "test:future",
                    "kind": "futureKind",
                    "name": "future",
                    "properties": {},
                    "createdAt": "2025-01-01T00:00:00+00:00",
                    "updatedAt": "2025-01-01T00:00:00+00:00"
                }
            ],
            "edges": [],
            "metadata": {}
        }
        """;

        var schema = LoadGeneratedSchema();
        var result = Validate(schema, json);

        Assert.True(result.IsValid,
            $"Schema must accept unknown enum values for forward-compat: {FormatErrors(result)}");
    }

    [Fact]
    public void ForwardCompat_UnknownEdgeKind_AcceptedBySchema()
    {
        var json = """
        {
            "schemaVersion": { "major": 1, "minor": 2 },
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

        var schema = LoadGeneratedSchema();
        var result = Validate(schema, json);

        Assert.True(result.IsValid,
            $"Schema must accept unknown edge kinds for forward-compat: {FormatErrors(result)}");
    }

    [Fact]
    public void ForwardCompat_ExtraPropertiesOnNode_AcceptedBySchema()
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

        var schema = LoadGeneratedSchema();
        var result = Validate(schema, json);

        Assert.True(result.IsValid,
            $"Schema must accept extra properties for forward-compat: {FormatErrors(result)}");
    }

    [Fact]
    public void ForwardCompat_ExtraPropertiesOnRoot_AcceptedBySchema()
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

        var schema = LoadGeneratedSchema();
        var result = Validate(schema, json);

        Assert.True(result.IsValid,
            $"Schema must accept extra root properties for forward-compat: {FormatErrors(result)}");
    }

    private static CodeKnowledgeGraph BuildFullGraph()
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
            Location = new SourceLocation
            {
                FilePath = "src/Foo.cs",
                StartLine = 1,
                EndLine = 100,
            },
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
        };
        funcNode.Properties.Set("isAsync", true);
        funcNode.Properties.Set("parameterCount", 3);

        var varNode = new GraphNode
        {
            Id = "variable:my-repo:MyNamespace.Foo:_count",
            Kind = NodeKind.Variable,
            Name = "_count",
        };

        var moduleNode = new GraphNode
        {
            Id = "module:my-repo:MyNamespace",
            Kind = NodeKind.Module,
            Name = "MyNamespace",
            QualifiedName = "MyNamespace",
        };

        var containsEdge = new GraphEdge
        {
            Id = "e1",
            SourceNodeId = repoNode.Id,
            TargetNodeId = fileNode.Id,
            Kind = EdgeKind.Contains,
        };

        var callsEdge = new GraphEdge
        {
            Id = "e2",
            SourceNodeId = funcNode.Id,
            TargetNodeId = varNode.Id,
            Kind = EdgeKind.Calls,
        };

        var importsEdge = new GraphEdge
        {
            Id = "e3",
            SourceNodeId = fileNode.Id,
            TargetNodeId = moduleNode.Id,
            Kind = EdgeKind.Imports,
        };

        var inheritsEdge = new GraphEdge
        {
            Id = "e4",
            SourceNodeId = classNode.Id,
            TargetNodeId = moduleNode.Id,
            Kind = EdgeKind.Inherits,
        };

        var expertiseEdge = new GraphEdge
        {
            Id = "e5",
            SourceNodeId = classNode.Id,
            TargetNodeId = funcNode.Id,
            Kind = EdgeKind.Expertise,
            Weight = 0.85,
        };

        var similarToEdge = new GraphEdge
        {
            Id = "e6",
            SourceNodeId = funcNode.Id,
            TargetNodeId = classNode.Id,
            Kind = EdgeKind.SimilarTo,
            Weight = 0.92,
        };
        similarToEdge.Properties.Set("cosine_similarity", 0.92);

        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes = [repoNode, fileNode, classNode, funcNode, varNode, moduleNode],
            Edges = [containsEdge, callsEdge, importsEdge, inheritsEdge, expertiseEdge, similarToEdge],
        };
        graph.Metadata.Set("producer", "test-harness");

        return graph;
    }

    private static string FormatErrors(EvaluationResults results)
    {
        if (results.IsValid) return "Valid";
        var errors = new List<string>();
        CollectErrors(results, errors);
        return string.Join("\n", errors);
    }

    private static void CollectErrors(EvaluationResults results, List<string> errors)
    {
        if (results.Errors != null)
        {
            foreach (var err in results.Errors)
            {
                errors.Add($"{results.InstanceLocation}: {err.Key} - {err.Value}");
            }
        }

        if (results.Details != null)
        {
            foreach (var detail in results.Details)
            {
                CollectErrors(detail, errors);
            }
        }
    }
}
