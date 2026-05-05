// -----------------------------------------------------------------------
// <copyright file="MemoryNodeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Core;
using Ast.Memory.Models;

[TestClass]
public class MemoryNodeTests
{
    [TestMethod]
    public void MemoryNode_WithStructuralLayerAndTemporal_ExposesAllAccessors()
    {
        // Arrange: HypergraphNode with MemoryLayer="structural" and temporal metadata
        var inner = new HypergraphNode
        {
            Id = "node-1",
            Name = "MyClass",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/src/MyClass.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "deadbeef",
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                [MemoryMetadataKeys.SinceIndex] = 42,
            },
        };

        // Act
        var memoryNode = new MemoryNode(inner);

        // Assert: Inner is the original node
        memoryNode.Inner.Should().BeSameAs(inner);

        // Assert: MemoryLayer returns "structural"
        memoryNode.MemoryLayer.Should().Be("structural");

        // Assert: NodeKind delegates to Inner.NodeType
        memoryNode.NodeKind.Should().Be("Class");

        // Assert: ContentHash delegates to metadata
        memoryNode.ContentHash.Should().Be("deadbeef");

        // Assert: Version is extracted with correct SinceIndex
        memoryNode.Version.Should().NotBeNull();
        memoryNode.Version!.Since.Should().Be("abc123");
        memoryNode.Version!.SinceIndex.Should().Be(42);
        memoryNode.Version!.Until.Should().BeNull();
        memoryNode.Version!.UntilIndex.Should().BeNull();
    }

    [TestMethod]
    public void MemoryNode_ExternalBoundaryNode_VersionIsNull()
    {
        // Arrange: An External boundary node with no temporal metadata (timeless)
        var inner = new HypergraphNode
        {
            Id = "ext-nuget-newtonsoft",
            Name = "Newtonsoft.Json",
            NodeType = "ExternalPackage",
            Language = "C#",
            FilePath = "",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "external",
                [MemoryMetadataKeys.ContentHash] = "aabb1122",
            },
        };

        // Act
        var memoryNode = new MemoryNode(inner);

        // Assert: Version is null for external boundary nodes
        memoryNode.Version.Should().BeNull();
    }

    [TestMethod]
    public void MemoryNode_WithFullVersionRange_IncludesUntil()
    {
        var inner = new HypergraphNode
        {
            Id = "node-2",
            Name = "OldMethod",
            NodeType = "Method",
            Language = "C#",
            FilePath = "/src/Old.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "cafebabe",
                [MemoryMetadataKeys.SinceCommit] = "aaa111",
                [MemoryMetadataKeys.SinceIndex] = 10,
                [MemoryMetadataKeys.UntilCommit] = "bbb222",
                [MemoryMetadataKeys.UntilIndex] = 20,
            },
        };

        var memoryNode = new MemoryNode(inner);

        memoryNode.Version.Should().NotBeNull();
        memoryNode.Version!.Since.Should().Be("aaa111");
        memoryNode.Version!.SinceIndex.Should().Be(10);
        memoryNode.Version!.Until.Should().Be("bbb222");
        memoryNode.Version!.UntilIndex.Should().Be(20);
    }

    [TestMethod]
    public void MemoryNode_NullInner_ThrowsArgumentNullException()
    {
        var act = () => new MemoryNode(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void MemoryNode_MissingMemoryLayerKey_ThrowsOnAccess()
    {
        var inner = new HypergraphNode
        {
            Id = "node-3",
            Name = "Test",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/src/Test.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.ContentHash] = "hash",
            },
        };

        var memoryNode = new MemoryNode(inner);
        var act = () => memoryNode.MemoryLayer;
        act.Should().Throw<KeyNotFoundException>();
    }

    [TestMethod]
    public void MemoryNode_SinceCommitWithoutSinceIndex_ThrowsInvalidOperation()
    {
        var inner = new HypergraphNode
        {
            Id = "node-malformed-1",
            Name = "Bad",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/src/Bad.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "hash",
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                // SinceIndex deliberately missing
            },
        };

        var act = () => new MemoryNode(inner);
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("SinceIndex");
    }

    [TestMethod]
    public void MemoryNode_SinceIndexWithoutSinceCommit_ThrowsInvalidOperation()
    {
        var inner = new HypergraphNode
        {
            Id = "node-malformed-2",
            Name = "Bad",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/src/Bad.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "hash",
                [MemoryMetadataKeys.SinceIndex] = 42,
                // SinceCommit deliberately missing
            },
        };

        var act = () => new MemoryNode(inner);
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("SinceCommit");
    }

    [TestMethod]
    public void MemoryNode_UntilCommitWithoutUntilIndex_ThrowsInvalidOperation()
    {
        var inner = new HypergraphNode
        {
            Id = "node-malformed-3",
            Name = "Bad",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/src/Bad.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "hash",
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                [MemoryMetadataKeys.SinceIndex] = 1,
                [MemoryMetadataKeys.UntilCommit] = "def456",
                // UntilIndex deliberately missing
            },
        };

        var act = () => new MemoryNode(inner);
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("UntilIndex");
    }

    [TestMethod]
    public void MemoryNode_VersionMetadataWithStringIndex_ParsesCorrectly()
    {
        var inner = new HypergraphNode
        {
            Id = "node-string-index",
            Name = "StrParsed",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/src/Str.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "hash",
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                [MemoryMetadataKeys.SinceIndex] = "42",
            },
        };

        var memoryNode = new MemoryNode(inner);
        memoryNode.Version.Should().NotBeNull();
        memoryNode.Version!.SinceIndex.Should().Be(42);
    }
}
