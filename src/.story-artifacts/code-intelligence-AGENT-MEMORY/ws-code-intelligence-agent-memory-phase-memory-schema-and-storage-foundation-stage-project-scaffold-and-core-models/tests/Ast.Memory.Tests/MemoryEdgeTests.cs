using Ast.Core;
using Ast.Memory.Models;
using Xunit;

namespace Ast.Memory.Tests;

public class MemoryEdgeTests
{
    private static readonly VersionRange DefaultVersion = new("abc123", 1, null, null);

    [Fact]
    public void Inner_ReturnsOriginalEdge()
    {
        var edge = new Hyperedge("e1", HyperedgeType.MemoryRelation, ["s1"], ["t1"],
            new Dictionary<string, string>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO"
            });

        var memoryEdge = new MemoryEdge(edge, DefaultVersion);

        Assert.Same(edge, memoryEdge.Inner);
    }

    [Fact]
    public void SemanticRelation_ReturnsFromMetadata()
    {
        var edge = new Hyperedge("e1", HyperedgeType.MemoryRelation, ["s1"], ["t1"],
            new Dictionary<string, string>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO"
            });

        var memoryEdge = new MemoryEdge(edge, DefaultVersion);

        Assert.Equal("APPLIES_TO", memoryEdge.SemanticRelation);
    }

    [Fact]
    public void MemoryLayer_ReturnsFromMetadata()
    {
        var edge = new Hyperedge("e1", HyperedgeType.MemoryRelation, ["s1"], ["t1"],
            new Dictionary<string, string>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "semantic"
            });

        var memoryEdge = new MemoryEdge(edge, new VersionRange("abc123", 5, null, null));

        Assert.Equal("semantic", memoryEdge.MemoryLayer);
    }

    [Fact]
    public void Version_ReturnsProvidedVersionRange()
    {
        var edge = new Hyperedge("e1", HyperedgeType.MemoryRelation, ["s1"], ["t1"],
            new Dictionary<string, string>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
                [MemoryMetadataKeys.MemoryLayer] = "expertise"
            });

        var version = new VersionRange("commit1", 10, "commit2", 20);
        var memoryEdge = new MemoryEdge(edge, version);

        Assert.Same(version, memoryEdge.Version);
        Assert.Equal("commit1", memoryEdge.Version.Since);
        Assert.Equal(10, memoryEdge.Version.SinceIndex);
        Assert.Equal("commit2", memoryEdge.Version.Until);
        Assert.Equal(20, memoryEdge.Version.UntilIndex);
    }

    [Fact]
    public void Version_IsNeverNull()
    {
        var edge = new Hyperedge("e1", HyperedgeType.MemoryRelation, ["s1"], ["t1"],
            new Dictionary<string, string>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO"
            });

        var memoryEdge = new MemoryEdge(edge, DefaultVersion);

        Assert.NotNull(memoryEdge.Version);
    }

    [Fact]
    public void Version_OpenEnded_WhenUntilIsNull()
    {
        var edge = new Hyperedge("e1", HyperedgeType.MemoryRelation, ["s1"], ["t1"],
            new Dictionary<string, string>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
                [MemoryMetadataKeys.MemoryLayer] = "expertise"
            });

        var version = new VersionRange("abc", 0, null, null);
        var memoryEdge = new MemoryEdge(edge, version);

        Assert.NotNull(memoryEdge.Version);
        Assert.Null(memoryEdge.Version.Until);
        Assert.Null(memoryEdge.Version.UntilIndex);
    }

    [Fact]
    public void Constructor_ThrowsOnNullInner()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryEdge(null!, DefaultVersion));
    }

    [Fact]
    public void Constructor_ThrowsOnNullVersion()
    {
        var edge = new Hyperedge("e1", HyperedgeType.MemoryRelation, ["s1"], ["t1"],
            new Dictionary<string, string>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO"
            });

        Assert.Throws<ArgumentNullException>(() => new MemoryEdge(edge, null!));
    }
}
