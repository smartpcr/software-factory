using CodeIntelligence.GraphMemory.Schema;
using Xunit;

namespace CodeIntelligence.GraphMemory.Tests;

public class ValidationTests
{
    [Fact]
    public void ValidGraph_ReturnsNoErrors()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "repo:test", Kind = NodeKind.Repo, Name = "test" },
                new GraphNode { Id = "file:test:a.cs", Kind = NodeKind.File, Name = "a.cs" },
            ],
            Edges =
            [
                new GraphEdge
                {
                    Id = "e1",
                    SourceNodeId = "repo:test",
                    TargetNodeId = "file:test:a.cs",
                    Kind = EdgeKind.Contains,
                },
            ],
        };

        var diagnostics = GraphValidator.Validate(graph);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void DuplicateNodeId_ReportsError()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "repo:test", Kind = NodeKind.Repo, Name = "test" },
                new GraphNode { Id = "repo:test", Kind = NodeKind.Repo, Name = "test-dup" },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("Duplicate node Id", errors[0]);
    }

    [Fact]
    public void DanglingEdge_ReportsError()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "repo:test", Kind = NodeKind.Repo, Name = "test" },
            ],
            Edges =
            [
                new GraphEdge
                {
                    Id = "e1",
                    SourceNodeId = "repo:test",
                    TargetNodeId = "file:test:missing.cs",
                    Kind = EdgeKind.Contains,
                },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("non-existent TargetNodeId", errors[0]);
    }

    [Fact]
    public void WeightOutOfRange_ReportsError()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Class, Name = "A" },
                new GraphNode { Id = "b", Kind = NodeKind.Function, Name = "B" },
            ],
            Edges =
            [
                new GraphEdge
                {
                    Id = "e1",
                    SourceNodeId = "a",
                    TargetNodeId = "b",
                    Kind = EdgeKind.Expertise,
                    Weight = 1.5,
                },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("outside [0.0, 1.0]", errors[0]);
    }

    [Fact]
    public void UnknownNodeKind_ReportsError()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "x", Kind = NodeKind.Unknown, Name = "x" },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("Kind=Unknown", errors[0]);
    }

    [Fact]
    public void UpdatedAtBeforeCreatedAt_ReportsError()
    {
        var now = DateTimeOffset.UtcNow;
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode
                {
                    Id = "x",
                    Kind = NodeKind.Repo,
                    Name = "x",
                    CreatedAt = now,
                    UpdatedAt = now.AddHours(-1),
                },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("UpdatedAt < CreatedAt", errors[0]);
    }

    // ── Endpoint type constraint tests ─────────────────────────────

    [Fact]
    public void ContainsEdge_RepoToFile_IsValid()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "r", Kind = NodeKind.Repo, Name = "r" },
                new GraphNode { Id = "f", Kind = NodeKind.File, Name = "f" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "r", TargetNodeId = "f", Kind = EdgeKind.Contains },
            ],
        };

        Assert.Empty(GraphValidator.GetErrors(graph));
    }

    [Fact]
    public void ContainsEdge_ClassToModule_IsInvalid()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "c", Kind = NodeKind.Class, Name = "C" },
                new GraphNode { Id = "m", Kind = NodeKind.Module, Name = "M" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "c", TargetNodeId = "m", Kind = EdgeKind.Contains },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("target kind 'Module' is not allowed", errors[0]);
    }

    [Fact]
    public void ContainsEdge_FunctionAsSource_IsInvalid()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "fn", Kind = NodeKind.Function, Name = "fn" },
                new GraphNode { Id = "v", Kind = NodeKind.Variable, Name = "v" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "fn", TargetNodeId = "v", Kind = EdgeKind.Contains },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("source kind 'Function' is not allowed", errors[0]);
    }

    [Fact]
    public void CallsEdge_FunctionToFunction_IsValid()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "f1", Kind = NodeKind.Function, Name = "A" },
                new GraphNode { Id = "f2", Kind = NodeKind.Function, Name = "B" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "f1", TargetNodeId = "f2", Kind = EdgeKind.Calls },
            ],
        };

        Assert.Empty(GraphValidator.GetErrors(graph));
    }

    [Fact]
    public void CallsEdge_ClassToFunction_IsInvalid()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "c", Kind = NodeKind.Class, Name = "C" },
                new GraphNode { Id = "f", Kind = NodeKind.Function, Name = "F" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "c", TargetNodeId = "f", Kind = EdgeKind.Calls },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("source kind 'Class' is not allowed", errors[0]);
    }

    [Fact]
    public void SimilarToEdge_RequiresCosineSimilarity()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Function, Name = "A" },
                new GraphNode { Id = "b", Kind = NodeKind.Function, Name = "B" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "a", TargetNodeId = "b", Kind = EdgeKind.SimilarTo },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("cosine_similarity", errors[0]);
    }

    [Fact]
    public void SimilarToEdge_InvalidCosineSimilarityRange_ReportsError()
    {
        var edge = new GraphEdge
        {
            Id = "e",
            SourceNodeId = "a",
            TargetNodeId = "b",
            Kind = EdgeKind.SimilarTo,
        };
        edge.Properties.Set("cosine_similarity", 2.0);

        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Function, Name = "A" },
                new GraphNode { Id = "b", Kind = NodeKind.Function, Name = "B" },
            ],
            Edges = [edge],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("cosine_similarity", errors[0]);
        Assert.Contains("[0.0, 1.0]", errors[0]);
    }

    [Fact]
    public void SimilarToEdge_ValidCosineSimilarity_NoErrors()
    {
        var edge = new GraphEdge
        {
            Id = "e",
            SourceNodeId = "a",
            TargetNodeId = "b",
            Kind = EdgeKind.SimilarTo,
        };
        edge.Properties.Set("cosine_similarity", 0.85);

        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Function, Name = "A" },
                new GraphNode { Id = "b", Kind = NodeKind.Function, Name = "B" },
            ],
            Edges = [edge],
        };

        Assert.Empty(GraphValidator.GetErrors(graph));
    }

    [Fact]
    public void ExpertiseEdge_MissingWeight_ReportsWarning()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Class, Name = "A" },
                new GraphNode { Id = "b", Kind = NodeKind.Function, Name = "B" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "a", TargetNodeId = "b", Kind = EdgeKind.Expertise },
            ],
        };

        var diagnostics = GraphValidator.Validate(graph);
        var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("Expertise", warnings[0].Message);
        Assert.Contains("Weight", warnings[0].Message);

        // Should NOT appear in GetErrors (error-only)
        Assert.Empty(GraphValidator.GetErrors(graph));
    }

    [Fact]
    public void ExpertiseEdge_WithWeight_NoWarning()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Class, Name = "A" },
                new GraphNode { Id = "b", Kind = NodeKind.Function, Name = "B" },
            ],
            Edges =
            [
                new GraphEdge
                {
                    Id = "e", SourceNodeId = "a", TargetNodeId = "b",
                    Kind = EdgeKind.Expertise, Weight = 0.8
                },
            ],
        };

        Assert.Empty(GraphValidator.Validate(graph));
    }

    [Fact]
    public void EndpointRules_CoverAllConstrainedEdgeKinds()
    {
        var ruledKinds = GraphValidator.EndpointRules.Keys.ToHashSet();
        // Contains, Calls, Imports, Inherits should have rules
        Assert.Contains(EdgeKind.Contains, ruledKinds);
        Assert.Contains(EdgeKind.Calls, ruledKinds);
        Assert.Contains(EdgeKind.Imports, ruledKinds);
        Assert.Contains(EdgeKind.Inherits, ruledKinds);
        // Expertise and SimilarTo are unconstrained on endpoints
        Assert.DoesNotContain(EdgeKind.Expertise, ruledKinds);
        Assert.DoesNotContain(EdgeKind.SimilarTo, ruledKinds);
    }

    // ── Self-loop tests ────────────────────────────────────────────

    [Theory]
    [InlineData(EdgeKind.Contains)]
    [InlineData(EdgeKind.Inherits)]
    [InlineData(EdgeKind.Calls)]
    [InlineData(EdgeKind.Imports)]
    public void SelfLoop_OnStructuralEdge_ReportsError(EdgeKind kind)
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Function, Name = "A" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "a", TargetNodeId = "a", Kind = kind },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Contains(errors, e => e.Contains("self-loop"));
    }

    [Fact]
    public void SelfLoop_OnExpertiseEdge_IsAllowed()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Function, Name = "A" },
            ],
            Edges =
            [
                new GraphEdge { Id = "e", SourceNodeId = "a", TargetNodeId = "a", Kind = EdgeKind.Expertise, Weight = 0.5 },
            ],
        };

        Assert.Empty(GraphValidator.GetErrors(graph));
    }

    // ── SimilarTo Weight consistency ───────────────────────────────

    [Fact]
    public void SimilarToEdge_WeightDisagreesWithCosineSimilarity_ReportsWarning()
    {
        var edge = new GraphEdge
        {
            Id = "e",
            SourceNodeId = "a",
            TargetNodeId = "b",
            Kind = EdgeKind.SimilarTo,
            Weight = 0.5,
        };
        edge.Properties.Set("cosine_similarity", 0.9);

        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode { Id = "a", Kind = NodeKind.Function, Name = "A" },
                new GraphNode { Id = "b", Kind = NodeKind.Function, Name = "B" },
            ],
            Edges = [edge],
        };

        var diagnostics = GraphValidator.Validate(graph);
        var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("differs from cosine_similarity", warnings[0].Message);
        // Should not be an error
        Assert.Empty(GraphValidator.GetErrors(graph));
    }

    // ── SchemaVersion validation ──────────────────────────────────

    [Fact]
    public void NegativeSchemaVersion_ReportsError()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = new SchemaVersion { Major = -1, Minor = 0 },
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("non-negative", errors[0]);
    }

    // ── SourceLocation validation ─────────────────────────────────

    [Fact]
    public void SourceLocation_NegativeLine_ReportsError()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode
                {
                    Id = "f",
                    Kind = NodeKind.File,
                    Name = "f",
                    Location = new SourceLocation { FilePath = "f.cs", StartLine = -1 },
                },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("StartLine must be non-negative", errors[0]);
    }

    [Fact]
    public void SourceLocation_EndBeforeStart_ReportsError()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode
                {
                    Id = "f",
                    Kind = NodeKind.File,
                    Name = "f",
                    Location = new SourceLocation { FilePath = "f.cs", StartLine = 10, EndLine = 5 },
                },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("EndLine", errors[0]);
    }

    [Fact]
    public void SourceLocation_EndColumnBeforeStartColumn_OnSameLine_ReportsError()
    {
        var graph = new CodeKnowledgeGraph
        {
            SchemaVersion = SchemaVersion.Current,
            Nodes =
            [
                new GraphNode
                {
                    Id = "f",
                    Kind = NodeKind.File,
                    Name = "f",
                    Location = new SourceLocation
                    {
                        FilePath = "f.cs",
                        StartLine = 5,
                        EndLine = 5,
                        StartColumn = 20,
                        EndColumn = 10,
                    },
                },
            ],
        };

        var errors = GraphValidator.GetErrors(graph);
        Assert.Single(errors);
        Assert.Contains("EndColumn", errors[0]);
    }
}
