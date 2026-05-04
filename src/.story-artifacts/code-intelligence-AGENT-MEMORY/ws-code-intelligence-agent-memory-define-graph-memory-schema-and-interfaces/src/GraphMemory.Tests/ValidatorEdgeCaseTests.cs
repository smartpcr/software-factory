using GraphMemory.Models;
using GraphMemory.Validation;

namespace GraphMemory.Tests;

/// <summary>
/// Tests edge-case validation rules: negative values, out-of-range confidence,
/// empty names, invalid columns, and other constraint violations.
/// </summary>
public class ValidatorEdgeCaseTests
{
    private readonly GraphValidator _validator = new();

    private static CodeGraph MakeGraph(List<GraphNode>? nodes = null, List<GraphEdge>? edges = null) => new()
    {
        SchemaVersion = CodeGraph.CurrentSchemaVersion,
        Id = "test-graph",
        Nodes = nodes ?? [],
        Edges = edges ?? [],
        Metadata = new GraphMetadata { CreatedAt = "2026-05-04T00:00:00Z", UpdatedAt = "2026-05-04T00:00:00Z" },
    };

    private static GraphNode MakeFileNode(string id = "n1") => new()
    {
        Id = id, Kind = NodeKind.File, Name = "test.cs",
        Properties = new FileProperties { Path = "src/test.cs" },
    };

    // -----------------------------------------------------------------------
    // Empty node name
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Empty_Node_Name()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "n1", Kind = NodeKind.File, Name = "",
            Properties = new FileProperties { Path = "a.ts" },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "EMPTY_NODE_NAME");
    }

    [Fact]
    public void Detects_Whitespace_Only_Node_Name()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "n1", Kind = NodeKind.File, Name = "   ",
            Properties = new FileProperties { Path = "a.ts" },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "EMPTY_NODE_NAME");
    }

    // -----------------------------------------------------------------------
    // Negative loc
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Negative_Loc()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "n1", Kind = NodeKind.File, Name = "a.ts",
            Properties = new FileProperties { Path = "a.ts", Loc = -5 },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NEGATIVE_LOC");
    }

    // -----------------------------------------------------------------------
    // Negative memberCount
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Negative_MemberCount()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "sc1", Kind = NodeKind.SemanticCluster, Name = "cluster",
            Properties = new SemanticClusterProperties { MemberCount = -1 },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NEGATIVE_MEMBER_COUNT");
    }

    // -----------------------------------------------------------------------
    // Negative order on Contains edge
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Negative_Order()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Contains, Source = "n1", Target = "n2",
                Properties = new ContainsProperties { Order = -1 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NEGATIVE_ORDER");
    }

    // -----------------------------------------------------------------------
    // Calls confidence out of range
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Calls_Confidence_Out_Of_Range()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Calls, Source = "n1", Target = "n2",
                Properties = new CallsProperties { Confidence = 1.5 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "CALLS_CONFIDENCE_OUT_OF_RANGE");
    }

    [Fact]
    public void Detects_Calls_Negative_Confidence()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Calls, Source = "n1", Target = "n2",
                Properties = new CallsProperties { Confidence = -0.1 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "CALLS_CONFIDENCE_OUT_OF_RANGE");
    }

    // -----------------------------------------------------------------------
    // CalledBy confidence out of range
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_CalledBy_Confidence_Out_Of_Range()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.CalledBy, Source = "n1", Target = "n2",
                Properties = new CalledByProperties { Confidence = 2.0 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "CALLED_BY_CONFIDENCE_OUT_OF_RANGE");
    }

    // -----------------------------------------------------------------------
    // BelongsToCluster strength out of range
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Cluster_Strength_Out_Of_Range()
    {
        var n1 = MakeFileNode("n1");
        var n2 = new GraphNode
        {
            Id = "sc1", Kind = NodeKind.SemanticCluster, Name = "cluster",
            Properties = new SemanticClusterProperties(),
        };
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.BelongsToCluster, Source = "n1", Target = "sc1",
                Properties = new BelongsToClusterProperties { Strength = 1.5 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "CLUSTER_STRENGTH_OUT_OF_RANGE");
    }

    // -----------------------------------------------------------------------
    // FrequentlyUsedWith edge constraints
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_FrequentlyUsedWith_Strength_Out_Of_Range()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.FrequentlyUsedWith, Source = "n1", Target = "n2",
                Properties = new FrequentlyUsedWithProperties { Strength = -0.5 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "FREQUENTLY_USED_WITH_STRENGTH_OUT_OF_RANGE");
    }

    [Fact]
    public void Detects_Negative_CoOccurrenceCount()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.FrequentlyUsedWith, Source = "n1", Target = "n2",
                Properties = new FrequentlyUsedWithProperties { CoOccurrenceCount = -1 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NEGATIVE_CO_OCCURRENCE_COUNT");
    }

    // -----------------------------------------------------------------------
    // Invalid columns (< 0)
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Negative_StartColumn()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "cls1", Kind = NodeKind.Class, Name = "Foo",
            Properties = new ClassProperties { FilePath = "a.ts", StartLine = 1, EndLine = 10, StartColumn = -1 },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_START_COLUMN");
    }

    [Fact]
    public void Detects_Negative_EndColumn()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "cls1", Kind = NodeKind.Class, Name = "Foo",
            Properties = new ClassProperties { FilePath = "a.ts", StartLine = 1, EndLine = 10, EndColumn = -1 },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_END_COLUMN");
    }

    // -----------------------------------------------------------------------
    // CallSite line/column validation
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Invalid_CallSite_Line()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Calls, Source = "n1", Target = "n2",
                Properties = new CallsProperties { CallSite = new CallSite { Line = 0 } },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_CALL_SITE_LINE");
    }

    [Fact]
    public void Detects_Invalid_CallSite_Column()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Calls, Source = "n1", Target = "n2",
                Properties = new CallsProperties { CallSite = new CallSite { Line = 5, Column = -1 } },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_CALL_SITE_COLUMN");
    }

    // -----------------------------------------------------------------------
    // Pattern/Insight confidence out of range
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Pattern_Confidence_Out_Of_Range()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "p1", Kind = NodeKind.Pattern, Name = "pat",
            Properties = new PatternProperties { Content = "test", Confidence = 1.5 },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "PATTERN_CONFIDENCE_OUT_OF_RANGE");
    }

    [Fact]
    public void Detects_Insight_Confidence_Out_Of_Range()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "i1", Kind = NodeKind.Insight, Name = "ins",
            Properties = new InsightProperties { Content = "test", Confidence = -0.1 },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INSIGHT_CONFIDENCE_OUT_OF_RANGE");
    }

    // -----------------------------------------------------------------------
    // Negative expertise edge counts
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Negative_ReinforcementCount()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Expertise, Source = "n1", Target = "n2",
                Properties = new ExpertiseProperties
                {
                    Weight = 0.5, Confidence = 0.5, ReinforcementCount = -1,
                },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NEGATIVE_REINFORCEMENT_COUNT");
    }

    [Fact]
    public void Detects_Negative_ObservationCount()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Expertise, Source = "n1", Target = "n2",
                Properties = new ExpertiseProperties
                {
                    Weight = 0.5, Confidence = 0.5, ObservationCount = -1,
                },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "NEGATIVE_OBSERVATION_COUNT");
    }

    // -----------------------------------------------------------------------
    // Valid graphs pass without false positives
    // -----------------------------------------------------------------------

    [Fact]
    public void Valid_Minimal_Graph_Passes()
    {
        var graph = MakeGraph([MakeFileNode()]);
        var result = _validator.Validate(graph);
        Assert.True(result.IsValid, string.Join("\n", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    [Fact]
    public void Valid_Graph_With_Zero_Loc_Passes()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "n1", Kind = NodeKind.File, Name = "empty.ts",
            Properties = new FileProperties { Path = "empty.ts", Loc = 0 },
        }]);

        var result = _validator.Validate(graph);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Valid_Graph_With_Zero_Order_Passes()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Contains, Source = "n1", Target = "n2",
                Properties = new ContainsProperties { Order = 0 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Valid_Calls_Edge_With_Null_Confidence_Passes()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Calls, Source = "n1", Target = "n2",
                Properties = new CallsProperties(),
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Valid_Expertise_With_Boundary_Values_Passes()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Expertise, Source = "n1", Target = "n2",
                Properties = new ExpertiseProperties { Weight = 0.0, Confidence = 1.0 },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.True(result.IsValid);
    }

    // -----------------------------------------------------------------------
    // Extension key restrictions
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Invalid_Extension_Key_On_Node()
    {
        var node = MakeFileNode();
        node.Extensions = new Dictionary<string, object> { ["badKey"] = "value" };
        var graph = MakeGraph([node]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_EXTENSION_KEY");
    }

    [Fact]
    public void Valid_Extension_Key_With_X_Prefix_Passes()
    {
        var node = MakeFileNode();
        node.Extensions = new Dictionary<string, object> { ["x-myTool"] = "value", ["x-neo4j"] = 42 };
        var graph = MakeGraph([node]);

        var result = _validator.Validate(graph);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Detects_Invalid_Extension_Key_On_Edge()
    {
        var n1 = MakeFileNode("n1");
        var n2 = MakeFileNode("n2");
        n2.Name = "b.ts";
        var graph = MakeGraph([n1, n2],
        [
            new GraphEdge
            {
                Id = "e1", Kind = EdgeKind.Contains, Source = "n1", Target = "n2",
                Properties = new ContainsProperties(),
                Extensions = new Dictionary<string, object> { ["nope"] = true },
            },
        ]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_EXTENSION_KEY");
    }

    // -----------------------------------------------------------------------
    // Required field validation
    // -----------------------------------------------------------------------

    [Fact]
    public void Detects_Empty_Content_On_ExpertiseNote()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "n1", Kind = NodeKind.ExpertiseNote, Name = "note",
            Properties = new ExpertiseNoteProperties { Content = "" },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "MISSING_REQUIRED_FIELD" && e.Message.Contains("content"));
    }

    [Fact]
    public void Detects_Empty_Path_On_File()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "n1", Kind = NodeKind.File, Name = "f",
            Properties = new FileProperties { Path = "" },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "MISSING_REQUIRED_FIELD" && e.Message.Contains("path"));
    }

    [Fact]
    public void Detects_Empty_RootPath_On_Workspace()
    {
        var graph = MakeGraph([new GraphNode
        {
            Id = "n1", Kind = NodeKind.Workspace, Name = "ws",
            Properties = new WorkspaceProperties { RootPath = "  " },
        }]);

        var result = _validator.Validate(graph);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "MISSING_REQUIRED_FIELD" && e.Message.Contains("rootPath"));
    }
}
