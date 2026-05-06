// -----------------------------------------------------------------------
// <copyright file="RetrievalModelsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Core;
using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ScoredMemoryNodeTests
{
    [TestMethod]
    public void ScoredMemoryNode_StoresAllScoreComponents()
    {
        var inner = new HypergraphNode
        {
            Id = "test-node",
            Name = "TestClass",
            NodeType = "class",
            Language = "C#",
            FilePath = "/src/Test.cs",
        };
        var memoryNode = new MemoryNode(inner);

        var scored = new ScoredMemoryNode(
            Node: memoryNode,
            CompositeScore: 0.85,
            StructuralScore: 0.90,
            SemanticScore: 0.80,
            CentralityScore: 0.75,
            RecencyScore: 0.95,
            ExpertiseScore: 0.60);

        scored.Node.Should().BeSameAs(memoryNode);
        scored.CompositeScore.Should().Be(0.85);
        scored.StructuralScore.Should().Be(0.90);
        scored.SemanticScore.Should().Be(0.80);
        scored.CentralityScore.Should().Be(0.75);
        scored.RecencyScore.Should().Be(0.95);
        scored.ExpertiseScore.Should().Be(0.60);
    }
}

[TestClass]
public class RetrievalResultTests
{
    [TestMethod]
    public void Empty_ReturnsEmptyResult()
    {
        var result = RetrievalResult.Empty();

        result.Nodes.Should().BeEmpty();
        result.ContextMarkdown.Should().BeEmpty();
        result.TokensUsed.Should().Be(0);
        result.ModesExecuted.Should().BeEmpty();
        result.Diagnostics.Should().BeNull();
    }

    [TestMethod]
    public void Empty_WithError_IncludesDiagnosticError()
    {
        var result = RetrievalResult.Empty("Graph store unavailable");

        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.Errors.Should().ContainSingle()
            .Which.Should().Be("Graph store unavailable");
    }
}

[TestClass]
public class RetrievalDiagnosticsTests
{
    [TestMethod]
    public void ModeDiagnostic_StoresModeAndMetrics()
    {
        var diag = new ModeDiagnostic(
            RetrievalMode.SemanticSearch,
            CandidateCount: 42,
            Duration: System.TimeSpan.FromMilliseconds(150));

        diag.Mode.Should().Be(RetrievalMode.SemanticSearch);
        diag.CandidateCount.Should().Be(42);
        diag.Duration.TotalMilliseconds.Should().Be(150);
    }

    [TestMethod]
    public void RetrievalDiagnostics_AggregatesMultipleModes()
    {
        var metrics = new[]
        {
            new ModeDiagnostic(RetrievalMode.StructuralDrill, 10, System.TimeSpan.FromMilliseconds(50)),
            new ModeDiagnostic(RetrievalMode.SemanticSearch, 25, System.TimeSpan.FromMilliseconds(120)),
        };
        var diag = new RetrievalDiagnostics(
            ModeMetrics: metrics,
            TotalDuration: System.TimeSpan.FromMilliseconds(200),
            TotalCandidatesBeforeRanking: 35,
            TotalCandidatesAfterRanking: 15);

        diag.ModeMetrics.Should().HaveCount(2);
        diag.TotalCandidatesBeforeRanking.Should().Be(35);
        diag.TotalCandidatesAfterRanking.Should().Be(15);
    }
}

[TestClass]
public class MemoryNodeTests
{
    [TestMethod]
    public void MemoryNode_WrapsHypergraphNode()
    {
        var inner = new HypergraphNode
        {
            Id = "node-1",
            Name = "MyClass",
            NodeType = "class",
            Language = "C#",
            FilePath = "/src/MyClass.cs",
            Metadata = { ["MemoryLayer"] = "structural" },
        };

        var node = new MemoryNode(inner);

        node.Inner.Should().BeSameAs(inner);
        node.MemoryLayer.Should().Be("structural");
        node.NodeKind.Should().Be("class");
    }

    [TestMethod]
    public void MemoryNode_VersionRange_ParsedFromMetadata()
    {
        var inner = new HypergraphNode
        {
            Id = "node-2",
            Name = "MyMethod",
            NodeType = "method",
            Language = "C#",
            FilePath = "/src/MyClass.cs",
            Metadata =
            {
                ["SinceCommit"] = "abc123",
                ["SinceIndex"] = 42,
                ["UntilCommit"] = "def456",
                ["UntilIndex"] = 100,
            },
        };

        var node = new MemoryNode(inner);

        node.Version.Should().NotBeNull();
        node.Version!.Since.Should().Be("abc123");
        node.Version.SinceIndex.Should().Be(42);
        node.Version.Until.Should().Be("def456");
        node.Version.UntilIndex.Should().Be(100);
    }

    [TestMethod]
    public void MemoryNode_ExternalNode_VersionIsNull()
    {
        var inner = new HypergraphNode
        {
            Id = "ext-1",
            Name = "System.String",
            NodeType = "class",
            Language = "C#",
            FilePath = "external",
        };

        var node = new MemoryNode(inner);

        node.Version.Should().BeNull();
    }

    [TestMethod]
    public void MemoryNode_NullInner_ThrowsArgumentNullException()
    {
        var act = () => new MemoryNode(null!);

        act.Should().Throw<System.ArgumentNullException>();
    }
}

[TestClass]
public class ScoringWeightsTests
{
    [TestMethod]
    public void DefaultWeights_SumToOne()
    {
        var w = new ScoringWeights();

        var sum = w.Structural + w.Semantic + w.Centrality + w.Recency + w.Expertise;

        sum.Should().BeApproximately(1.0, 0.001);
    }

    [TestMethod]
    public void DefaultWeights_MatchArchSpec()
    {
        var w = new ScoringWeights();

        w.Structural.Should().Be(0.30);
        w.Semantic.Should().Be(0.25);
        w.Centrality.Should().Be(0.20);
        w.Recency.Should().Be(0.15);
        w.Expertise.Should().Be(0.10);
    }

    [TestMethod]
    public void CustomWeights_ArePreserved()
    {
        var w = new ScoringWeights(
            Structural: 0.50,
            Semantic: 0.20,
            Centrality: 0.10,
            Recency: 0.10,
            Expertise: 0.10);

        w.Structural.Should().Be(0.50);
        w.Semantic.Should().Be(0.20);
    }
}
