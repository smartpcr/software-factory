using System.Text.Json;
using GraphMemory.Interfaces;
using GraphMemory.Models;
using GraphMemory.Serialization;
using GraphMemory.Validation;
using Json.Schema;

namespace GraphMemory.Tests;

public class SchemaParityTests
{
    private static readonly JsonSchema JsonSchemaDoc;
    private static readonly JsonSerializerOptions Opts = GraphJsonOptions.Default;

    static SchemaParityTests()
    {
        var schemaPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "json-schema", "graph-memory.schema.json");
        var schemaText = File.ReadAllText(schemaPath);
        JsonSchemaDoc = JsonSchema.FromText(schemaText);
    }

    // -----------------------------------------------------------------------
    // Fixture >=50 nodes, validated against JSON Schema
    // -----------------------------------------------------------------------

    [Fact]
    public void Fixture_Has_At_Least_50_Nodes()
    {
        var graph = FixtureBuilder.Build();
        Assert.True(graph.Nodes.Count >= 50, $"Expected >=50 nodes, got {graph.Nodes.Count}");
    }

    [Fact]
    public void Fixture_Serialised_Passes_JsonSchema_Validation()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var doc = JsonDocument.Parse(json);
        var result = JsonSchemaDoc.Evaluate(doc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });
        Assert.True(result.IsValid, FormatSchemaErrors(result));
    }

    [Fact]
    public void Roundtrip_Serialise_Deserialise_Preserves_Graph()
    {
        var original = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(original, Opts);

        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(original.Edges.Count, restored.Edges.Count);
        Assert.Equal(original.Metadata.CreatedAt, restored.Metadata.CreatedAt);
        Assert.Equal(original.Metadata.CreatedBy, restored.Metadata.CreatedBy);

        // Verify each node round-trips with correct kind and properties type
        foreach (var origNode in original.Nodes)
        {
            var restoredNode = restored.Nodes.Single(n => n.Id == origNode.Id);
            Assert.Equal(origNode.Kind, restoredNode.Kind);
            Assert.Equal(origNode.Name, restoredNode.Name);
            Assert.Equal(origNode.Properties.GetType(), restoredNode.Properties.GetType());
            Assert.Equal(origNode.Layer, restoredNode.Layer);
        }

        // Verify each edge round-trips with correct kind and properties type
        foreach (var origEdge in original.Edges)
        {
            var restoredEdge = restored.Edges.Single(e => e.Id == origEdge.Id);
            Assert.Equal(origEdge.Kind, restoredEdge.Kind);
            Assert.Equal(origEdge.Source, restoredEdge.Source);
            Assert.Equal(origEdge.Target, restoredEdge.Target);
            Assert.Equal(origEdge.Properties.GetType(), restoredEdge.Properties.GetType());
        }
    }

    [Fact]
    public void Roundtrip_Preserves_Expertise_Edge_Fields()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var expertiseEdge = restored.Edges.First(e => e.Kind == EdgeKind.Expertise);
        var props = Assert.IsType<ExpertiseProperties>(expertiseEdge.Properties);
        Assert.True(props.Weight > 0);
        Assert.True(props.Confidence > 0);
        Assert.NotNull(props.Insight);
        Assert.True(props.ReinforcementCount > 0);
    }

    [Fact]
    public void Roundtrip_Preserves_Calls_Edge_CallSite()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var callEdge = restored.Edges.First(e => e.Kind == EdgeKind.Calls);
        var props = Assert.IsType<CallsProperties>(callEdge.Properties);
        Assert.NotNull(props.CallSite);
        Assert.True(props.CallSite!.Line > 0);
    }

    [Fact]
    public void Roundtrip_Preserves_Node_Optional_Fields()
    {
        var original = FixtureBuilder.Build();
        // Add a fully-populated node
        original.Nodes.Add(new GraphNode
        {
            Id = "roundtrip-full",
            Kind = NodeKind.File,
            Name = "rt.ts",
            Properties = new FileProperties { Path = "rt.ts", Language = "typescript", Loc = 10, Hash = "sha256:rt" },
            QualifiedName = "src/rt.ts",
            RepoId = "repo-1",
            Provenance = new Provenance { Analyzer = "test", AnalyzerVersion = "1.0", AnalyzedAt = "2026-05-04T00:00:00Z", CommitSha = "abc" },
            Version = 3,
            Layer = Layer.Structural,
            CreatedAt = "2026-05-04T00:00:00Z",
            UpdatedAt = "2026-05-04T00:00:00Z",
            VersionRange = new VersionRange { IntroducedIn = "aaa", LastSeenIn = "bbb", RemovedIn = "ccc" },
            SnapshotId = "snap-1",
            Stale = true,
            ContentHash = "sha256:xyz",
            Embedding = [0.1, 0.2, 0.3],
        });

        var json = JsonSerializer.Serialize(original, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var n = restored.Nodes.Single(n => n.Id == "roundtrip-full");
        Assert.Equal("src/rt.ts", n.QualifiedName);
        Assert.Equal("repo-1", n.RepoId);
        Assert.NotNull(n.Provenance);
        Assert.Equal("test", n.Provenance!.Analyzer);
        Assert.Equal(3, n.Version);
        Assert.Equal(Layer.Structural, n.Layer);
        Assert.NotNull(n.VersionRange);
        Assert.Equal("aaa", n.VersionRange!.IntroducedIn);
        Assert.Equal("ccc", n.VersionRange.RemovedIn);
        Assert.Equal("snap-1", n.SnapshotId);
        Assert.True(n.Stale);
        Assert.Equal("sha256:xyz", n.ContentHash);
        Assert.Equal(3, n.Embedding!.Length);
    }

    // -----------------------------------------------------------------------
    // Enum coverage: every C# enum maps to a valid JSON Schema const
    // -----------------------------------------------------------------------

    [Fact]
    public void All_NodeKind_Values_Have_Wire_Mappings()
    {
        foreach (NodeKind kind in System.Enum.GetValues<NodeKind>())
        {
            Assert.True(SnakeCaseEnumMaps.NodeKindToString.ContainsKey(kind), $"Missing wire mapping for NodeKind.{kind}");
        }
    }

    [Fact]
    public void All_EdgeKind_Values_Have_Wire_Mappings()
    {
        foreach (EdgeKind kind in System.Enum.GetValues<EdgeKind>())
        {
            Assert.True(SnakeCaseEnumMaps.EdgeKindToString.ContainsKey(kind), $"Missing wire mapping for EdgeKind.{kind}");
        }
    }

    [Fact]
    public void All_NodeKind_Values_Have_Property_Type_Mappings()
    {
        foreach (NodeKind kind in System.Enum.GetValues<NodeKind>())
        {
            Assert.True(NodePropertiesConverter.KindToType.ContainsKey(kind),
                $"Missing property type mapping for NodeKind.{kind}");
        }
    }

    [Fact]
    public void All_EdgeKind_Values_Have_Property_Type_Mappings()
    {
        foreach (EdgeKind kind in System.Enum.GetValues<EdgeKind>())
        {
            Assert.True(EdgePropertiesConverter.KindToType.ContainsKey(kind),
                $"Missing property type mapping for EdgeKind.{kind}");
        }
    }

    // -----------------------------------------------------------------------
    // Schema/code parity: JSON Schema node kinds == C# NodeKind enum
    // -----------------------------------------------------------------------

    [Fact]
    public void JsonSchema_Node_Kinds_Match_CSharp_Enum()
    {
        var schemaText = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "json-schema", "graph-memory.schema.json"));
        var doc = JsonDocument.Parse(schemaText);
        var graphNode = doc.RootElement.GetProperty("$defs").GetProperty("GraphNode");
        var oneOf = graphNode.GetProperty("oneOf");

        var schemaKinds = new HashSet<string>();
        foreach (var variant in oneOf.EnumerateArray())
        {
            var kindConst = variant.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString()!;
            schemaKinds.Add(kindConst);
        }

        var csharpKinds = new HashSet<string>(SnakeCaseEnumMaps.NodeKindToString.Values);

        Assert.Equal(schemaKinds.OrderBy(x => x), csharpKinds.OrderBy(x => x));
    }

    [Fact]
    public void JsonSchema_Edge_Kinds_Match_CSharp_Enum()
    {
        var schemaText = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "json-schema", "graph-memory.schema.json"));
        var doc = JsonDocument.Parse(schemaText);
        var graphEdge = doc.RootElement.GetProperty("$defs").GetProperty("GraphEdge");
        var oneOf = graphEdge.GetProperty("oneOf");

        var schemaKinds = new HashSet<string>();
        foreach (var variant in oneOf.EnumerateArray())
        {
            var kindConst = variant.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString()!;
            schemaKinds.Add(kindConst);
        }

        var csharpKinds = new HashSet<string>(SnakeCaseEnumMaps.EdgeKindToString.Values);

        Assert.Equal(schemaKinds.OrderBy(x => x), csharpKinds.OrderBy(x => x));
    }

    // -----------------------------------------------------------------------
    // Expertise edge: insight and reinforcementCount fields
    // -----------------------------------------------------------------------

    [Fact]
    public void ExpertiseEdge_Serialises_Insight_And_ReinforcementCount()
    {
        var edge = new GraphEdge
        {
            Id = "exp-1",
            Kind = EdgeKind.Expertise,
            Source = "n1",
            Target = "n2",
            Properties = new ExpertiseProperties
            {
                Weight = 0.8,
                Confidence = 0.9,
                Insight = "this is critical",
                ReinforcementCount = 5,
                ObservationCount = 10,
            },
        };

        var json = JsonSerializer.Serialize(edge, Opts);
        var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");

        Assert.Equal("this is critical", props.GetProperty("insight").GetString());
        Assert.Equal(5, props.GetProperty("reinforcementCount").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Validator tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Validator_Passes_Valid_Graph()
    {
        var graph = FixtureBuilder.Build();
        var validator = new GraphValidator();
        var result = validator.Validate(graph);
        Assert.True(result.IsValid, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Validator_Detects_Duplicate_Node_Ids()
    {
        var graph = FixtureBuilder.Build();
        graph.Nodes.Add(new GraphNode
        {
            Id = graph.Nodes[0].Id,
            Kind = NodeKind.File,
            Name = "duplicate",
            Properties = new FileProperties { Path = "dup" },
        });

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "DUPLICATE_NODE_ID");
    }

    [Fact]
    public void Validator_Detects_Dangling_Edge_References()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes = [new GraphNode { Id = "n1", Kind = NodeKind.File, Name = "f", Properties = new FileProperties { Path = "f.ts" } }],
            Edges = [new GraphEdge { Id = "e1", Kind = EdgeKind.Contains, Source = "n1", Target = "ghost", Properties = new ContainsProperties() }],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "DANGLING_TARGET");
    }

    [Fact]
    public void Validator_Detects_Property_Type_Mismatch()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes = [new GraphNode { Id = "n1", Kind = NodeKind.File, Name = "f", Properties = new RepoProperties { Languages = ["ts"] } }],
            Edges = [],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "PROPERTY_TYPE_MISMATCH");
    }

    [Fact]
    public void Validator_Detects_Wrong_Schema_Version()
    {
        var graph = FixtureBuilder.Build();
        graph.SchemaVersion = "0.0.1";
        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_VERSION");
    }

    // -----------------------------------------------------------------------
    // Node kind coverage in fixture
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(NodeKind.Repo)]
    [InlineData(NodeKind.Directory)]
    [InlineData(NodeKind.File)]
    [InlineData(NodeKind.Module)]
    [InlineData(NodeKind.Package)]
    [InlineData(NodeKind.Class)]
    [InlineData(NodeKind.Interface)]
    [InlineData(NodeKind.TypeAlias)]
    [InlineData(NodeKind.Function)]
    [InlineData(NodeKind.Method)]
    [InlineData(NodeKind.Variable)]
    [InlineData(NodeKind.Enum)]
    [InlineData(NodeKind.Symbol)]
    [InlineData(NodeKind.SemanticCluster)]
    [InlineData(NodeKind.Topic)]
    [InlineData(NodeKind.ExpertiseNote)]
    [InlineData(NodeKind.Pattern)]
    [InlineData(NodeKind.Pitfall)]
    [InlineData(NodeKind.Preference)]
    [InlineData(NodeKind.Insight)]
    [InlineData(NodeKind.Workspace)]
    public void Fixture_Contains_Node_Of_Kind(NodeKind kind)
    {
        var graph = FixtureBuilder.Build();
        Assert.Contains(graph.Nodes, n => n.Kind == kind);
    }

    // -----------------------------------------------------------------------
    // Edge kind coverage in fixture
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(EdgeKind.Contains)]
    [InlineData(EdgeKind.Calls)]
    [InlineData(EdgeKind.CalledBy)]
    [InlineData(EdgeKind.Imports)]
    [InlineData(EdgeKind.ReExports)]
    [InlineData(EdgeKind.Exports)]
    [InlineData(EdgeKind.Inherits)]
    [InlineData(EdgeKind.Overrides)]
    [InlineData(EdgeKind.Implements)]
    [InlineData(EdgeKind.Instantiates)]
    [InlineData(EdgeKind.Expertise)]
    [InlineData(EdgeKind.AppliesTo)]
    [InlineData(EdgeKind.SimilarTo)]
    [InlineData(EdgeKind.BelongsToCluster)]
    [InlineData(EdgeKind.Supersedes)]
    [InlineData(EdgeKind.DerivedFrom)]
    [InlineData(EdgeKind.FrequentlyUsedWith)]
    [InlineData(EdgeKind.DependsOn)]
    [InlineData(EdgeKind.References)]
    public void Fixture_Contains_Edge_Of_Kind(EdgeKind kind)
    {
        var graph = FixtureBuilder.Build();
        Assert.Contains(graph.Edges, e => e.Kind == kind);
    }

    // -----------------------------------------------------------------------
    // Layer and versioning fields serialise correctly
    // -----------------------------------------------------------------------

    [Fact]
    public void Node_With_All_Optional_Fields_Passes_Schema()
    {
        var node = new GraphNode
        {
            Id = "full-node",
            Kind = NodeKind.File,
            Name = "full.ts",
            Properties = new FileProperties { Path = "src/full.ts", Language = "typescript", Loc = 100, Hash = "sha256:abc" },
            QualifiedName = "src/full.ts",
            RepoId = "repo-1",
            Provenance = new Provenance { Analyzer = "test", AnalyzerVersion = "1.0", AnalyzedAt = "2026-05-04T00:00:00Z", CommitSha = "abc" },
            Version = 2,
            Layer = Layer.Structural,
            CreatedAt = "2026-05-04T00:00:00Z",
            UpdatedAt = "2026-05-04T00:00:00Z",
            VersionRange = new VersionRange { IntroducedIn = "abc", LastSeenIn = "def" },
            SnapshotId = "snap-1",
            Stale = false,
            ContentHash = "sha256:xyz",
            Embedding = [0.1, 0.2, 0.3],
            Extensions = new Dictionary<string, object> { ["x-test"] = "value" },
        };

        var graph = WrapSingleNode(node);
        var json = JsonSerializer.Serialize(graph, Opts);
        var doc = JsonDocument.Parse(json);
        var result = JsonSchemaDoc.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatSchemaErrors(result));
    }

    [Fact]
    public void SimilarTo_Edge_Uses_Cosine_Similarity_Property_Name()
    {
        var edge = new GraphEdge
        {
            Id = "sim-1",
            Kind = EdgeKind.SimilarTo,
            Source = "n1",
            Target = "n2",
            Properties = new SimilarToProperties { CosineSimilarity = 0.95 },
        };

        var json = JsonSerializer.Serialize(edge, Opts);
        Assert.Contains("\"cosine_similarity\"", json);
        Assert.DoesNotContain("\"cosineSimilarity\"", json);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static CodeGraph WrapSingleNode(GraphNode node) => new()
    {
        SchemaVersion = CodeGraph.CurrentSchemaVersion,
        Id = "single-node-graph",
        Nodes = [node],
        Edges = [],
        Metadata = new GraphMetadata { CreatedAt = "2026-05-04T00:00:00Z", UpdatedAt = "2026-05-04T00:00:00Z" },
    };

    [Fact]
    public void Deserialized_Fixture_Passes_Validator()
    {
        var original = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(original, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var validator = new GraphValidator();
        var result = validator.Validate(restored);
        Assert.True(result.IsValid, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Roundtrip_Preserves_SimilarTo_CosineSimilarity()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var simEdge = restored.Edges.First(e => e.Kind == EdgeKind.SimilarTo);
        var props = Assert.IsType<SimilarToProperties>(simEdge.Properties);
        Assert.Equal(0.87, props.CosineSimilarity, 2);
        Assert.Equal("openai-ada-002", props.Algorithm);
    }

    [Fact]
    public void Roundtrip_Preserves_Inherits_Mechanism()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var inhEdge = restored.Edges.First(e => e.Kind == EdgeKind.Inherits);
        var props = Assert.IsType<InheritsProperties>(inhEdge.Properties);
        Assert.Equal(InheritanceMechanism.Implements, props.Mechanism);
    }

    [Fact]
    public void Roundtrip_Preserves_FrequentlyUsedWith_Fields()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var fuwEdge = restored.Edges.First(e => e.Kind == EdgeKind.FrequentlyUsedWith);
        var props = Assert.IsType<FrequentlyUsedWithProperties>(fuwEdge.Properties);
        Assert.Equal(42, props.CoOccurrenceCount);
        Assert.Equal(0.88, props.Strength!.Value, 2);
        Assert.Equal("file", props.Window);
    }

    [Fact]
    public void Roundtrip_Preserves_Imports_With_Symbols()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var impEdge = restored.Edges.First(e => e.Kind == EdgeKind.Imports);
        var props = Assert.IsType<ImportsProperties>(impEdge.Properties);
        Assert.NotNull(props.ImportedSymbols);
        Assert.True(props.ImportedSymbols!.Length > 0);
        Assert.Equal("hashPassword", props.ImportedSymbols[0].Name);
    }

    [Fact]
    public void Roundtrip_Preserves_Function_Parameters()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var fn = restored.Nodes.First(n => n.Id == "fn-hash-password");
        var props = Assert.IsType<FunctionProperties>(fn.Properties);
        Assert.Single(props.Parameters);
        Assert.Equal("plain", props.Parameters[0].Name);
        Assert.Equal("string", props.Parameters[0].TypeAnnotation);
    }

    [Fact]
    public void Roundtrip_Preserves_Method_Visibility()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var meth = restored.Nodes.First(n => n.Id == "meth-login");
        var props = Assert.IsType<MethodProperties>(meth.Properties);
        Assert.Equal(Visibility.Public, props.Visibility);
        Assert.True(props.IsAsync);
    }

    [Fact]
    public void Roundtrip_Preserves_Variable_Kind()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var v = restored.Nodes.First(n => n.Id == "var-max-retries");
        var props = Assert.IsType<VariableProperties>(v.Properties);
        Assert.Equal(VariableKind.Const, props.VariableKind);
    }

    [Fact]
    public void Roundtrip_Preserves_SemanticCluster_Centroid()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var cluster = restored.Nodes.First(n => n.Kind == NodeKind.SemanticCluster);
        var props = Assert.IsType<SemanticClusterProperties>(cluster.Properties);
        Assert.Equal(3, props.Centroid!.Length);
        Assert.Equal(5, props.MemberCount);
    }

    [Fact]
    public void Roundtrip_Preserves_Edge_Layer()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var callEdge = restored.Edges.First(e => e.Kind == EdgeKind.Calls);
        Assert.Equal(Layer.CallChain, callEdge.Layer);
    }

    [Fact]
    public void Calls_Edge_Fields_Are_Both_Optional()
    {
        // Operator answer: both_optional — neither resolved nor target_name required
        var edge = new GraphEdge
        {
            Id = "call-opt",
            Kind = EdgeKind.Calls,
            Source = "n1",
            Target = "n2",
            Properties = new CallsProperties(), // all fields null
        };

        var json = JsonSerializer.Serialize(edge, Opts);
        var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");
        // Empty properties object is valid for calls edge
        Assert.Equal(JsonValueKind.Object, props.ValueKind);
    }

    [Fact]
    public void Schema_Validates_Minimal_Nodes()
    {
        // Minimal node with only required fields
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "minimal",
            Nodes =
            [
                new GraphNode { Id = "r", Kind = NodeKind.Repo, Name = "r", Properties = new RepoProperties { Languages = ["c#"] } },
                new GraphNode { Id = "d", Kind = NodeKind.Directory, Name = "src", Properties = new DirectoryProperties { Path = "src" } },
                new GraphNode { Id = "f", Kind = NodeKind.File, Name = "a.cs", Properties = new FileProperties { Path = "a.cs" } },
            ],
            Edges =
            [
                new GraphEdge { Id = "e1", Kind = EdgeKind.Contains, Source = "r", Target = "d", Properties = new ContainsProperties() },
                new GraphEdge { Id = "e2", Kind = EdgeKind.Contains, Source = "d", Target = "f", Properties = new ContainsProperties() },
            ],
            Metadata = new GraphMetadata { CreatedAt = "2026-05-04T00:00:00Z", UpdatedAt = "2026-05-04T00:00:00Z" },
        };

        var json = JsonSerializer.Serialize(graph, Opts);
        var doc = JsonDocument.Parse(json);
        var result = JsonSchemaDoc.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatSchemaErrors(result));
    }

    [Fact]
    public void JsonSchema_Property_Names_Are_CamelCase()
    {
        // Verify that C# serialized output uses camelCase for all top-level fields
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);

        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"createdAt\"", json);
        Assert.Contains("\"updatedAt\"", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);
        Assert.DoesNotContain("\"CreatedAt\"", json);
    }

    [Fact]
    public void Enum_Wire_Values_Are_SnakeCase()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);

        Assert.Contains("\"type_alias\"", json);
        Assert.Contains("\"semantic_cluster\"", json);
        Assert.Contains("\"expertise_note\"", json);
        Assert.Contains("\"call_chain\"", json);
        Assert.DoesNotContain("\"TypeAlias\"", json);
        Assert.DoesNotContain("\"SemanticCluster\"", json);
    }

    [Fact]
    public void Schema_Rejects_Extension_Key_Without_X_Prefix()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "ext-test",
            Nodes =
            [
                new GraphNode
                {
                    Id = "n1", Kind = NodeKind.File, Name = "f.ts",
                    Properties = new FileProperties { Path = "f.ts" },
                    Extensions = new Dictionary<string, object> { ["badKey"] = "value" },
                },
            ],
            Edges = [],
            Metadata = new GraphMetadata { CreatedAt = "2026-05-04T00:00:00Z", UpdatedAt = "2026-05-04T00:00:00Z" },
        };

        var json = JsonSerializer.Serialize(graph, Opts);
        var doc = JsonDocument.Parse(json);
        var result = JsonSchemaDoc.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(result.IsValid, "Extension key without 'x-' prefix should fail schema validation");
    }

    [Fact]
    public void Schema_Accepts_Extension_Key_With_X_Prefix()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "ext-test",
            Nodes =
            [
                new GraphNode
                {
                    Id = "n1", Kind = NodeKind.File, Name = "f.ts",
                    Properties = new FileProperties { Path = "f.ts" },
                    Extensions = new Dictionary<string, object> { ["x-myTool"] = "value" },
                },
            ],
            Edges = [],
            Metadata = new GraphMetadata { CreatedAt = "2026-05-04T00:00:00Z", UpdatedAt = "2026-05-04T00:00:00Z" },
        };

        var json = JsonSerializer.Serialize(graph, Opts);
        var doc = JsonDocument.Parse(json);
        var result = JsonSchemaDoc.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatSchemaErrors(result));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string FormatSchemaErrors(EvaluationResults result)
    {
        if (result.IsValid) return "Valid";
        var details = result.Details?
            .Where(d => !d.IsValid && d.Errors != null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} - {e.Value}"))
            .Take(20) ?? [];
        return "JSON Schema validation failed:\n" + string.Join("\n", details);
    }

    // -----------------------------------------------------------------------
    // New schema element tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Roundtrip_Preserves_Workspace_Properties()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var ws = restored.Nodes.First(n => n.Kind == NodeKind.Workspace);
        var props = Assert.IsType<WorkspaceProperties>(ws.Properties);
        Assert.Equal("/home/dev/workspace", props.RootPath);
        Assert.Single(props.RepoIds!);
    }

    [Fact]
    public void Roundtrip_Preserves_DependsOn_Edge()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var dep = restored.Edges.First(e => e.Kind == EdgeKind.DependsOn);
        var props = Assert.IsType<DependsOnProperties>(dep.Properties);
        Assert.Equal("runtime", props.DependencyType);
        Assert.Equal("^4.18.0", props.VersionConstraint);
    }

    [Fact]
    public void Roundtrip_Preserves_References_Edge()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var r = restored.Edges.First(e => e.Kind == EdgeKind.References);
        var props = Assert.IsType<ReferencesProperties>(r.Properties);
        Assert.Equal("type_usage", props.ReferenceKind);
    }

    [Fact]
    public void Roundtrip_Preserves_Expertise_SourceAgent_And_Evidence()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var edge = restored.Edges.First(e =>
            e.Kind == EdgeKind.Expertise &&
            e.Properties is ExpertiseProperties { SourceAgent: not null });
        var props = Assert.IsType<ExpertiseProperties>(edge.Properties);
        Assert.Equal("code-review-bot", props.SourceAgent);
        Assert.NotNull(props.Evidence);
        Assert.Equal(2, props.Evidence!.Length);
        Assert.Equal("code-review", props.Evidence[0].Source);
    }

    [Fact]
    public void Roundtrip_Preserves_Calls_IsAsync_And_Confidence()
    {
        var graph = FixtureBuilder.Build();
        var json = JsonSerializer.Serialize(graph, Opts);
        var restored = JsonSerializer.Deserialize<CodeGraph>(json, Opts)!;

        var edge = restored.Edges.First(e =>
            e.Kind == EdgeKind.Calls &&
            e.Properties is CallsProperties { IsAsync: not null });
        var props = Assert.IsType<CallsProperties>(edge.Properties);
        Assert.True(props.IsAsync);
        Assert.Equal(0.95, props.Confidence!.Value, 2);
    }

    [Fact]
    public void Validator_Detects_Expertise_Weight_Out_Of_Range()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes =
            [
                new GraphNode { Id = "n1", Kind = NodeKind.File, Name = "f", Properties = new FileProperties { Path = "f.ts" } },
                new GraphNode { Id = "n2", Kind = NodeKind.File, Name = "g", Properties = new FileProperties { Path = "g.ts" } },
            ],
            Edges =
            [
                new GraphEdge
                {
                    Id = "e1", Kind = EdgeKind.Expertise, Source = "n1", Target = "n2",
                    Properties = new ExpertiseProperties { Weight = 1.5, Confidence = 0.5 },
                },
            ],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "EXPERTISE_WEIGHT_OUT_OF_RANGE");
    }

    [Fact]
    public void Validator_Detects_Invalid_Source_Range()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes =
            [
                new GraphNode
                {
                    Id = "fn1", Kind = NodeKind.Function, Name = "bad",
                    Properties = new FunctionProperties { FilePath = "a.ts", StartLine = 100, EndLine = 5, Parameters = [] },
                },
            ],
            Edges = [],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_SOURCE_RANGE");
    }

    [Fact]
    public void Validator_Detects_Cosine_Similarity_Out_Of_Range()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes =
            [
                new GraphNode { Id = "n1", Kind = NodeKind.File, Name = "f", Properties = new FileProperties { Path = "f.ts" } },
                new GraphNode { Id = "n2", Kind = NodeKind.File, Name = "g", Properties = new FileProperties { Path = "g.ts" } },
            ],
            Edges =
            [
                new GraphEdge
                {
                    Id = "e1", Kind = EdgeKind.SimilarTo, Source = "n1", Target = "n2",
                    Properties = new SimilarToProperties { CosineSimilarity = 2.0 },
                },
            ],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "COSINE_SIMILARITY_OUT_OF_RANGE");
    }

    // -----------------------------------------------------------------------
    // TypeScript interface parity tests
    // -----------------------------------------------------------------------

    private static string ReadTypeScriptInterfaces()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "GraphMemory", "TypeScript", "graph-memory.d.ts");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TypeScript_Interfaces_Exist()
    {
        var ts = ReadTypeScriptInterfaces();
        Assert.NotEmpty(ts);
        // GraphNode and GraphEdge are now discriminated union types, not interfaces
        Assert.Contains("export type GraphNode =", ts);
        Assert.Contains("export type GraphEdge =", ts);
        Assert.Contains("export interface CodeGraph", ts);
        Assert.Contains("export interface IGraphStore", ts);
        Assert.Contains("export interface IGraphValidator", ts);
    }

    [Fact]
    public void TypeScript_Contains_All_NodeKind_Values()
    {
        var ts = ReadTypeScriptInterfaces();
        foreach (var wireValue in SnakeCaseEnumMaps.NodeKindToString.Values)
        {
            Assert.Contains($"\"{wireValue}\"", ts);
        }
    }

    [Fact]
    public void TypeScript_Contains_All_EdgeKind_Values()
    {
        var ts = ReadTypeScriptInterfaces();
        foreach (var wireValue in SnakeCaseEnumMaps.EdgeKindToString.Values)
        {
            Assert.Contains($"\"{wireValue}\"", ts);
        }
    }

    [Fact]
    public void TypeScript_Contains_GraphStore_Methods()
    {
        var ts = ReadTypeScriptInterfaces();
        Assert.Contains("addNode(", ts);
        Assert.Contains("getNode(", ts);
        Assert.Contains("traverse(", ts);
        Assert.Contains("vectorSearch(", ts);
        Assert.Contains("applyDiff(", ts);
        Assert.Contains("snapshot(", ts);
        Assert.Contains("restoreSnapshot(", ts);
        Assert.Contains("findByName(", ts);
        Assert.Contains("getNodeAtCommit(", ts);
    }

    [Fact]
    public void TypeScript_Contains_ExpertiseEvidence_Interface()
    {
        var ts = ReadTypeScriptInterfaces();
        Assert.Contains("export interface ExpertiseEvidence", ts);
        Assert.Contains("sourceAgent?:", ts);
        Assert.Contains("evidence?:", ts);
    }

    [Fact]
    public void TypeScript_Contains_Calls_Async_And_Confidence()
    {
        var ts = ReadTypeScriptInterfaces();
        // CallsProperties should have isAsync and confidence
        Assert.Contains("isAsync?:", ts);
        Assert.Contains("confidence?:", ts);
    }

    // -----------------------------------------------------------------------
    // TypeScript discriminated union tests
    // -----------------------------------------------------------------------

    [Fact]
    public void TypeScript_Has_Discriminated_Node_Types()
    {
        var ts = ReadTypeScriptInterfaces();
        // Each node kind has a per-kind type with discriminant
        foreach (var wireValue in SnakeCaseEnumMaps.NodeKindToString.Values)
        {
            Assert.Contains($"kind: \"{wireValue}\"", ts);
        }
        // GraphNodeCommon is defined
        Assert.Contains("interface GraphNodeCommon", ts);
    }

    [Fact]
    public void TypeScript_Has_Discriminated_Edge_Types()
    {
        var ts = ReadTypeScriptInterfaces();
        foreach (var wireValue in SnakeCaseEnumMaps.EdgeKindToString.Values)
        {
            Assert.Contains($"kind: \"{wireValue}\"", ts);
        }
        Assert.Contains("interface GraphEdgeCommon", ts);
    }

    [Fact]
    public void TypeScript_Node_Union_Is_Exhaustive()
    {
        var ts = ReadTypeScriptInterfaces();
        // The GraphNode union should reference all per-kind types
        Assert.Contains("export type RepoNode =", ts);
        Assert.Contains("export type FileNode =", ts);
        Assert.Contains("export type FunctionNode =", ts);
        Assert.Contains("export type WorkspaceNode =", ts);
        Assert.Contains("export type ExpertiseNoteNode =", ts);
    }

    [Fact]
    public void TypeScript_Edge_Union_Is_Exhaustive()
    {
        var ts = ReadTypeScriptInterfaces();
        Assert.Contains("export type ContainsEdge =", ts);
        Assert.Contains("export type CallsEdge =", ts);
        Assert.Contains("export type ExpertiseEdge =", ts);
        Assert.Contains("export type SimilarToEdge =", ts);
        Assert.Contains("export type DependsOnEdge =", ts);
    }

    // -----------------------------------------------------------------------
    // Validator: null/empty input handling
    // -----------------------------------------------------------------------

    [Fact]
    public void Validator_Throws_On_Null_Graph()
    {
        var validator = new GraphValidator();
        Assert.Throws<ArgumentNullException>(() => validator.Validate(null!));
    }

    [Fact]
    public void Validator_Detects_Null_Nodes_List()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes = null!,
            Edges = [],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NULL_NODES");
    }

    [Fact]
    public void Validator_Detects_Null_Edges_List()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes = [new GraphNode { Id = "n1", Kind = NodeKind.File, Name = "f", Properties = new FileProperties { Path = "f.ts" } }],
            Edges = null!,
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NULL_EDGES");
    }

    [Fact]
    public void Validator_Detects_Null_Node_Properties()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes = [new GraphNode { Id = "n1", Kind = NodeKind.File, Name = "f", Properties = null! }],
            Edges = [],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NULL_NODE_PROPERTIES");
    }

    [Fact]
    public void Validator_Detects_Null_Edge_Properties()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes =
            [
                new GraphNode { Id = "n1", Kind = NodeKind.File, Name = "f", Properties = new FileProperties { Path = "f.ts" } },
                new GraphNode { Id = "n2", Kind = NodeKind.File, Name = "g", Properties = new FileProperties { Path = "g.ts" } },
            ],
            Edges = [new GraphEdge { Id = "e1", Kind = EdgeKind.Contains, Source = "n1", Target = "n2", Properties = null! }],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NULL_EDGE_PROPERTIES");
    }

    // -----------------------------------------------------------------------
    // Validator: semantic edge endpoint constraints
    // -----------------------------------------------------------------------

    [Fact]
    public void Validator_Warns_Calls_Edge_Between_Non_Callable_Nodes()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes =
            [
                new GraphNode { Id = "f1", Kind = NodeKind.File, Name = "a.ts", Properties = new FileProperties { Path = "a.ts" } },
                new GraphNode { Id = "f2", Kind = NodeKind.File, Name = "b.ts", Properties = new FileProperties { Path = "b.ts" } },
            ],
            Edges = [new GraphEdge { Id = "e1", Kind = EdgeKind.Calls, Source = "f1", Target = "f2", Properties = new CallsProperties() }],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        // Still valid (warnings, not errors) — but should have endpoint warnings
        Assert.True(result.Warnings.Count > 0, "Expected warnings for calls edge between file nodes");
        Assert.Contains(result.Warnings, w => w.Contains("Calls") && w.Contains("not a typical"));
    }

    [Fact]
    public void Validator_No_Warning_For_Valid_Calls_Edge()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes =
            [
                new GraphNode { Id = "fn1", Kind = NodeKind.Function, Name = "a",
                    Properties = new FunctionProperties { FilePath = "a.ts", StartLine = 1, EndLine = 5, Parameters = [] } },
                new GraphNode { Id = "fn2", Kind = NodeKind.Function, Name = "b",
                    Properties = new FunctionProperties { FilePath = "b.ts", StartLine = 1, EndLine = 5, Parameters = [] } },
            ],
            Edges = [new GraphEdge { Id = "e1", Kind = EdgeKind.Calls, Source = "fn1", Target = "fn2", Properties = new CallsProperties() }],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Calls"));
    }

    [Fact]
    public void Validator_Warns_Implements_Edge_With_Wrong_Target()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes =
            [
                new GraphNode { Id = "cls1", Kind = NodeKind.Class, Name = "A",
                    Properties = new ClassProperties { FilePath = "a.ts", StartLine = 1, EndLine = 10 } },
                new GraphNode { Id = "cls2", Kind = NodeKind.Class, Name = "B",
                    Properties = new ClassProperties { FilePath = "b.ts", StartLine = 1, EndLine = 10 } },
            ],
            Edges = [new GraphEdge { Id = "e1", Kind = EdgeKind.Implements, Source = "cls1", Target = "cls2",
                Properties = new ImplementsProperties() }],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        // Target should be Interface, not Class
        Assert.Contains(result.Warnings, w => w.Contains("Implements") && w.Contains("not a typical target"));
    }

    [Fact]
    public void Validator_Warns_BelongsToCluster_Edge_With_Non_Cluster_Target()
    {
        var graph = new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "test",
            Nodes =
            [
                new GraphNode { Id = "fn1", Kind = NodeKind.Function, Name = "a",
                    Properties = new FunctionProperties { FilePath = "a.ts", StartLine = 1, EndLine = 5, Parameters = [] } },
                new GraphNode { Id = "fn2", Kind = NodeKind.Function, Name = "b",
                    Properties = new FunctionProperties { FilePath = "b.ts", StartLine = 1, EndLine = 5, Parameters = [] } },
            ],
            Edges = [new GraphEdge { Id = "e1", Kind = EdgeKind.BelongsToCluster, Source = "fn1", Target = "fn2",
                Properties = new BelongsToClusterProperties() }],
            Metadata = new GraphMetadata { CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z" },
        };

        var result = new GraphValidator().Validate(graph);
        Assert.Contains(result.Warnings, w => w.Contains("BelongsToCluster") && w.Contains("not a typical target"));
    }
}
