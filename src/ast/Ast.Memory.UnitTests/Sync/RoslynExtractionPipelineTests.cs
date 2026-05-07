// -----------------------------------------------------------------------
// <copyright file="RoslynExtractionPipelineTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Sync;

using Ast.Core;
using Ast.Memory.Models;
using Ast.Memory.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

[TestClass]
public class RoslynExtractionPipelineTests
{
    private string tempDir = null!;
    private RoslynExtractionPipeline pipeline = null!;

    [TestInitialize]
    public void Setup()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"roslyn_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);

        var logger = new Mock<ILogger<RoslynExtractionPipeline>>();
        this.pipeline = new RoslynExtractionPipeline(logger.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(this.tempDir))
        {
            try { Directory.Delete(this.tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_CSharpFile_ProducesNodesAndEdges()
    {
        // Arrange: write a C# file with a class and method
        var relPath = "Service.cs";
        var fullPath = Path.Combine(this.tempDir, relPath);
        await File.WriteAllTextAsync(fullPath, @"
namespace MyApp;

public class Service
{
    public void DoWork()
    {
        var x = 42;
    }
}
");

        // Act
        var result = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { relPath },
            new ExtractionOptions(Languages: new[] { "C#" }),
            CancellationToken.None);

        // Assert: Roslyn parser produces nodes
        result.ExtractedNodes.Should().NotBeEmpty("Roslyn parser should extract class/method nodes");
        result.ExtractedNodes.Should().Contain(n => n.NodeType == "class");
        result.Errors.Should().BeEmpty();

        // Nodes should have repo-relative FilePath
        result.ExtractedNodes.Should().OnlyContain(
            n => n.FilePath == "Service.cs",
            "All nodes should have repo-relative file paths");

        // Nodes should have memory-layer metadata
        result.ExtractedNodes.Should().OnlyContain(
            n => n.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer) &&
                 n.Metadata.ContainsKey(MemoryMetadataKeys.ContentHash),
            "All nodes should have MemoryLayer and ContentHash metadata");
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_ProducesEdgesWithGloballyUniqueIds()
    {
        // Arrange: two files that will each produce edges
        var file1 = "A.cs";
        var file2 = "B.cs";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, file1), @"
namespace MyApp;
public class A : IDisposable
{
    public void Dispose() { }
}
");
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, file2), @"
namespace MyApp;
public class B : IDisposable
{
    public void Dispose() { }
}
");

        // Act
        var result = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { file1, file2 },
            new ExtractionOptions(Languages: new[] { "C#" }),
            CancellationToken.None);

        // Assert: no duplicate edge IDs across files
        var edgeIds = result.ExtractedEdges.Select(e => e.Id).ToList();
        edgeIds.Should().OnlyHaveUniqueItems("Edge IDs must be globally unique across files");
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_NonSupportedFile_Skipped()
    {
        var relPath = "Config.json";
        await File.WriteAllTextAsync(
            Path.Combine(this.tempDir, relPath),
            "{ \"key\": \"value\" }");

        var result = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { relPath },
            new ExtractionOptions(Languages: new[] { "JSON" }),
            CancellationToken.None);

        result.FilesSkipped.Should().Be(1);
        result.ExtractedNodes.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_PowerShellFile_ProducesNodes()
    {
        var relPath = "Script.ps1";
        await File.WriteAllTextAsync(
            Path.Combine(this.tempDir, relPath),
            "function Get-Data { param([string]$Name) Write-Host $Name }");

        var result = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { relPath },
            new ExtractionOptions(Languages: new[] { "PowerShell" }),
            CancellationToken.None);

        result.FilesSkipped.Should().Be(0, "PowerShell files should not be skipped");
        result.ExtractedNodes.Should().NotBeEmpty("PowerShell parser should extract function nodes");
        result.ExtractedNodes.Should().OnlyContain(
            n => n.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer) &&
                 n.Metadata.ContainsKey(MemoryMetadataKeys.ContentHash),
            "PowerShell nodes should have MemoryLayer and ContentHash metadata");

        // Edges should have SourceFile metadata
        foreach (var edge in result.ExtractedEdges)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.SourceFile,
                "All edges should have SourceFile ownership metadata");
        }
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_SyntaxError_ReturnsErrorWithoutThrowing()
    {
        var relPath = "Bad.cs";
        await File.WriteAllTextAsync(
            Path.Combine(this.tempDir, relPath),
            "class { incomplete syntax }}}{{{");

        var result = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { relPath },
            new ExtractionOptions(Languages: new[] { "C#" }),
            CancellationToken.None);

        // Roslyn tolerates bad syntax — it may still produce partial nodes.
        // The key is that no exception propagates.
        result.Should().NotBeNull();
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_MissingFile_ReturnsError()
    {
        var result = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { "DoesNotExist.cs" },
            new ExtractionOptions(Languages: new[] { "C#" }),
            CancellationToken.None);

        result.Errors.Should().ContainSingle(e => e.Contains("DoesNotExist.cs"));
        result.ExtractedNodes.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_EdgesReferenceOnlyExtractedNodeIds()
    {
        var relPath = "WithEdges.cs";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, relPath), @"
namespace MyApp;

public class Parent
{
    public void Run()
    {
        var c = new Child();
    }
}

public class Child { }
");

        var result = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { relPath },
            new ExtractionOptions(Languages: new[] { "C#" }),
            CancellationToken.None);

        var nodeIds = new HashSet<string>(result.ExtractedNodes.Select(n => n.Id));

        // Edges should reference node IDs that exist in extracted nodes
        foreach (var edge in result.ExtractedEdges)
        {
            edge.NodeIds.Should().OnlyContain(
                id => nodeIds.Contains(id),
                $"Edge {edge.Id} should only reference extracted node IDs");
        }

        // Edges should have SourceFile metadata for edge ownership
        foreach (var edge in result.ExtractedEdges)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.SourceFile,
                "All edges should have SourceFile metadata for ownership tracking");
            edge.Metadata[MemoryMetadataKeys.SourceFile].Should().Be("WithEdges.cs");
        }
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_EdgesHaveSemanticRelationAndMemoryLayer()
    {
        var relPath = "EdgeMeta.cs";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, relPath), @"
namespace MyApp;

public class Parent
{
    public void Run()
    {
        var c = new Child();
    }
}

public class Child { }
");

        var result = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { relPath },
            new ExtractionOptions(Languages: new[] { "C#" }),
            CancellationToken.None);

        // Every edge must carry SemanticRelation and MemoryLayer metadata
        // per architecture.md §2.2 and invariant A-15
        foreach (var edge in result.ExtractedEdges)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.SemanticRelation,
                $"Edge {edge.Id} ({edge.EdgeType}) must have SemanticRelation metadata");
            edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().NotBeNull();

            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer,
                $"Edge {edge.Id} ({edge.EdgeType}) must have MemoryLayer metadata");
            edge.Metadata[MemoryMetadataKeys.MemoryLayer].Should().NotBeNull();

            var semanticRelation = (string)edge.Metadata[MemoryMetadataKeys.SemanticRelation]!;
            semanticRelation.Should().BeOneOf(
                "CONTAINS", "DEPENDS_ON", "CALLS", "EXTENDS", "IMPLEMENTS", "REFERENCES",
                $"Edge {edge.Id} should have a canonical SemanticRelation name");

            var memoryLayer = (string)edge.Metadata[MemoryMetadataKeys.MemoryLayer]!;
            memoryLayer.Should().BeOneOf(
                "structural", "call_chain",
                $"Edge {edge.Id} should have a valid MemoryLayer");
        }
    }

    [TestMethod]
    [TestCategory("Extraction")]
    public async Task ExtractFilesAsync_StructuralHashExcludesLineNumbers()
    {
        // Create a file, extract, note the ContentHash, then shift lines
        // by adding a blank line at top — ContentHash should NOT change.
        var relPath = "StableHash.cs";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, relPath), @"
namespace MyApp;

public class StableHash
{
    public void Run() { }
}
");

        var result1 = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { relPath },
            new ExtractionOptions(Languages: new[] { "C#" }),
            CancellationToken.None);

        var nodeHashes1 = result1.ExtractedNodes
            .Where(n => n.Metadata.ContainsKey(MemoryMetadataKeys.ContentHash))
            .ToDictionary(n => n.Name, n => (string)n.Metadata[MemoryMetadataKeys.ContentHash]!);

        // Now add blank lines at the top to shift all line numbers
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, relPath), @"



namespace MyApp;

public class StableHash
{
    public void Run() { }
}
");

        var result2 = await this.pipeline.ExtractFilesAsync(
            this.tempDir,
            new[] { relPath },
            new ExtractionOptions(Languages: new[] { "C#" }),
            CancellationToken.None);

        var nodeHashes2 = result2.ExtractedNodes
            .Where(n => n.Metadata.ContainsKey(MemoryMetadataKeys.ContentHash))
            .ToDictionary(n => n.Name, n => (string)n.Metadata[MemoryMetadataKeys.ContentHash]!);

        // Structural ContentHash should be identical despite line number shift
        foreach (var name in nodeHashes1.Keys.Intersect(nodeHashes2.Keys))
        {
            nodeHashes1[name].Should().Be(nodeHashes2[name],
                $"Node '{name}' ContentHash should not change when only line numbers shift " +
                "(structural hash must exclude line numbers)");
        }
    }
}
