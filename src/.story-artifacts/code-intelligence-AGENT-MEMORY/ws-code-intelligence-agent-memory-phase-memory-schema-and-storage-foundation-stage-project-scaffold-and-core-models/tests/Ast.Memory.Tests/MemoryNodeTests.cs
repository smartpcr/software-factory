using Ast.Core;
using Ast.Memory.Models;
using Xunit;

namespace Ast.Memory.Tests;

public class MemoryNodeTests
{
    [Fact]
    public void Inner_ReturnsOriginalNode()
    {
        var node = new HypergraphNode("n1", "Class", new Dictionary<string, string>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = "abc",
            [MemoryMetadataKeys.SinceCommit] = "abc123",
            [MemoryMetadataKeys.SinceIndex] = "42"
        });

        var memoryNode = new MemoryNode(node);

        Assert.Same(node, memoryNode.Inner);
    }

    [Fact]
    public void MemoryLayer_ReturnsFromMetadata()
    {
        var node = new HypergraphNode("n1", "Class", new Dictionary<string, string>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = "abc",
            [MemoryMetadataKeys.SinceCommit] = "abc123",
            [MemoryMetadataKeys.SinceIndex] = "42"
        });

        var memoryNode = new MemoryNode(node);

        Assert.Equal("structural", memoryNode.MemoryLayer);
    }

    [Fact]
    public void NodeKind_ReturnsInnerNodeType()
    {
        var node = new HypergraphNode("n1", "Class", new Dictionary<string, string>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = "hash",
            [MemoryMetadataKeys.SinceCommit] = "abc123",
            [MemoryMetadataKeys.SinceIndex] = "42"
        });

        var memoryNode = new MemoryNode(node);

        Assert.Equal("Class", memoryNode.NodeKind);
    }

    [Fact]
    public void ContentHash_ReturnsFromMetadata()
    {
        var node = new HypergraphNode("n1", "Class", new Dictionary<string, string>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = "sha256hash",
            [MemoryMetadataKeys.SinceCommit] = "abc123",
            [MemoryMetadataKeys.SinceIndex] = "42"
        });

        var memoryNode = new MemoryNode(node);

        Assert.Equal("sha256hash", memoryNode.ContentHash);
    }

    [Fact]
    public void Version_ExtractsFromTemporalMetadata()
    {
        var node = new HypergraphNode("n1", "Class", new Dictionary<string, string>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = "hash",
            [MemoryMetadataKeys.SinceCommit] = "abc123",
            [MemoryMetadataKeys.SinceIndex] = "42"
        });

        var memoryNode = new MemoryNode(node);

        Assert.NotNull(memoryNode.Version);
        Assert.Equal("abc123", memoryNode.Version!.Since);
        Assert.Equal(42, memoryNode.Version.SinceIndex);
        Assert.Null(memoryNode.Version.Until);
        Assert.Null(memoryNode.Version.UntilIndex);
    }

    [Fact]
    public void Version_IsNull_ForExternalBoundaryNode()
    {
        // External boundary nodes have no temporal metadata — they are timeless
        var node = new HypergraphNode("ext1", "External", new Dictionary<string, string>
        {
            [MemoryMetadataKeys.MemoryLayer] = "external",
            [MemoryMetadataKeys.ContentHash] = "hash"
        });

        var memoryNode = new MemoryNode(node);

        Assert.Null(memoryNode.Version);
    }

    [Fact]
    public void Version_IncludesUntil_WhenClosed()
    {
        var node = new HypergraphNode("n1", "Class", new Dictionary<string, string>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = "hash",
            [MemoryMetadataKeys.SinceCommit] = "abc123",
            [MemoryMetadataKeys.SinceIndex] = "10",
            [MemoryMetadataKeys.UntilCommit] = "def456",
            [MemoryMetadataKeys.UntilIndex] = "20"
        });

        var memoryNode = new MemoryNode(node);

        Assert.NotNull(memoryNode.Version);
        Assert.Equal("def456", memoryNode.Version!.Until);
        Assert.Equal(20, memoryNode.Version.UntilIndex);
    }

    [Fact]
    public void Constructor_ThrowsOnNullInner()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryNode(null!));
    }
}
