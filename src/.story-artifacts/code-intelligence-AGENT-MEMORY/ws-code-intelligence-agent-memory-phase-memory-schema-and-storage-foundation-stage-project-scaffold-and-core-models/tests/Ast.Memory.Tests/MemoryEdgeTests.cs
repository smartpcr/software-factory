// -----------------------------------------------------------------------
// <copyright file="MemoryEdgeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Core;
using Ast.Memory.Models;

[TestClass]
public class MemoryEdgeTests
{
    [TestMethod]
    public void MemoryEdge_CompositionScenario_ExposesInnerAndSemanticRelation()
    {
        // Acceptance scenario (architecture.md §4.1):
        // Given a Hyperedge with EdgeType=HyperedgeType.MemoryRelation
        //   and Metadata["SemanticRelation"]="APPLIES_TO"
        // When wrapped in a MemoryEdge
        // Then memoryEdge.Inner is the original edge
        //   and memoryEdge.SemanticRelation returns "APPLIES_TO"
        var inner = new Hyperedge
        {
            Id = "edge-1",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-a", "node-b"],
            Roles = new Dictionary<string, string>
            {
                ["node-a"] = "source",
                ["node-b"] = "target",
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
            },
        };

        var memoryEdge = new MemoryEdge(inner);

        memoryEdge.Inner.Should().BeSameAs(inner);
        memoryEdge.SemanticRelation.Should().Be("APPLIES_TO");
    }

    [TestMethod]
    public void MemoryEdge_NoTemporalMetadata_SingleArgUsesDefaultVersion()
    {
        // When no temporal metadata is present in the Hyperedge,
        // the single-arg constructor succeeds with a default empty VersionRange.
        var inner = new Hyperedge
        {
            Id = "edge-no-temporal",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-a"],
            Roles = new Dictionary<string, string>(),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
            },
        };

        var memoryEdge = new MemoryEdge(inner);

        memoryEdge.Version.Should().NotBeNull();
        memoryEdge.Version.Since.Should().BeEmpty();
        memoryEdge.Version.SinceIndex.Should().Be(0);
    }

    [TestMethod]
    public void MemoryEdge_SingleArgCtor_WithTemporalMetadata_ExtractsVersionRange()
    {
        var inner = new Hyperedge
        {
            Id = "edge-1b",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-a", "node-b"],
            Roles = new Dictionary<string, string>
            {
                ["node-a"] = "source",
                ["node-b"] = "target",
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
                [MemoryMetadataKeys.MemoryLayer] = "expertise",
                [MemoryMetadataKeys.SinceCommit] = "commit-abc",
                [MemoryMetadataKeys.SinceIndex] = 5,
            },
        };

        var memoryEdge = new MemoryEdge(inner);

        memoryEdge.MemoryLayer.Should().Be("expertise");
        memoryEdge.Version.Since.Should().Be("commit-abc");
        memoryEdge.Version.SinceIndex.Should().Be(5);
        memoryEdge.Version.Until.Should().BeNull();
        memoryEdge.Version.UntilIndex.Should().BeNull();
    }

    [TestMethod]
    public void MemoryEdge_WithFullVersionRange()
    {
        var inner = new Hyperedge
        {
            Id = "edge-full",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-a"],
            Roles = new Dictionary<string, string>(),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.SinceCommit] = "aaa111",
                [MemoryMetadataKeys.SinceIndex] = 10,
                [MemoryMetadataKeys.UntilCommit] = "bbb222",
                [MemoryMetadataKeys.UntilIndex] = 20,
            },
        };

        var memoryEdge = new MemoryEdge(inner);

        memoryEdge.Version.Since.Should().Be("aaa111");
        memoryEdge.Version.SinceIndex.Should().Be(10);
        memoryEdge.Version.Until.Should().Be("bbb222");
        memoryEdge.Version.UntilIndex.Should().Be(20);
    }

    [TestMethod]
    public void MemoryEdge_ExplicitVersionConstructor_UsesProvidedVersion()
    {
        var inner = new Hyperedge
        {
            Id = "edge-explicit",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-a", "node-b"],
            Roles = new Dictionary<string, string>
            {
                ["node-a"] = "source",
                ["node-b"] = "target",
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
                [MemoryMetadataKeys.MemoryLayer] = "expertise",
            },
        };

        var version = new VersionRange("commit-abc", 5, null, null);
        var memoryEdge = new MemoryEdge(inner, version);

        memoryEdge.Inner.Should().BeSameAs(inner);
        memoryEdge.SemanticRelation.Should().Be("APPLIES_TO");
        memoryEdge.Version.Should().BeSameAs(version);
    }

    [TestMethod]
    public void MemoryEdge_NullInner_ThrowsArgumentNullException()
    {
        var version = new VersionRange("abc", 1, null, null);
        var act = () => new MemoryEdge(null!, version);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void MemoryEdge_SingleArgCtor_NullInner_ThrowsArgumentNullException()
    {
        var act = () => new MemoryEdge(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void MemoryEdge_ExplicitCtor_NullVersion_ThrowsArgumentNullException()
    {
        var inner = new Hyperedge
        {
            Id = "edge-2",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-a"],
            Roles = new Dictionary<string, string>(),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "structural",
            },
        };

        var act = () => new MemoryEdge(inner, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void MemoryEdge_WrongEdgeType_ThrowsArgumentException()
    {
        var inner = new Hyperedge
        {
            Id = "edge-wrong-type",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["node-a"],
            Roles = new Dictionary<string, string>(),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
            },
        };

        var version = new VersionRange("abc", 1, null, null);
        var act = () => new MemoryEdge(inner, version);
        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("MemoryRelation");
    }

    [TestMethod]
    public void MemoryEdge_WrongEdgeType_SingleArgCtor_ThrowsArgumentException()
    {
        var inner = new Hyperedge
        {
            Id = "edge-wrong-type-single",
            EdgeType = HyperedgeType.DataFlow,
            NodeIds = ["node-a"],
            Roles = new Dictionary<string, string>(),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.SinceCommit] = "abc",
                [MemoryMetadataKeys.SinceIndex] = 1,
            },
        };

        var act = () => new MemoryEdge(inner);
        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("MemoryRelation");
    }

    [TestMethod]
    public void MemoryEdge_IncompleteSinceMetadata_ThrowsInvalidOperationException()
    {
        var inner = new Hyperedge
        {
            Id = "edge-bad",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-a"],
            Roles = new Dictionary<string, string>(),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                // SinceIndex missing — malformed
            },
        };

        var act = () => new MemoryEdge(inner);
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("SinceIndex");
    }

    [TestMethod]
    public void MemoryEdge_MissingSemanticRelation_ThrowsOnAccess()
    {
        var inner = new Hyperedge
        {
            Id = "edge-3",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-a"],
            Roles = new Dictionary<string, string>(),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.SinceCommit] = "abc",
                [MemoryMetadataKeys.SinceIndex] = 1,
            },
        };

        var edge = new MemoryEdge(inner);

        var act = () => edge.SemanticRelation;
        act.Should().Throw<KeyNotFoundException>();
    }
}
