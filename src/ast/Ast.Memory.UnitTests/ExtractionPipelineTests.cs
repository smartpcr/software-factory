// -----------------------------------------------------------------------
// <copyright file="ExtractionPipelineTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.Memory.Extraction;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.VectorDb;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ExtractionPipelineTests
{
    private readonly string fixtureRoot;

    public ExtractionPipelineTests()
    {
        this.fixtureRoot = Path.Combine(Path.GetTempPath(), "ast-pipeline-test-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [TestInitialize]
    public void Setup()
    {
        Directory.CreateDirectory(this.fixtureRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(this.fixtureRoot))
        {
            try { Directory.Delete(this.fixtureRoot, true); } catch { }
        }
    }

    [TestMethod]
    public async Task ExtractAsync_WithCSharpFiles_ReturnsNodesEdgesAndEmbeddings()
    {
        this.CreateFixtureFiles();

        var embedGen = new FakeEmbeddingGenerator();
        var pipeline = this.CreatePipelineWithEmbeddings(embedGen);
        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
            GenerateEmbeddings = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        result.NodesCreated.Should().BeGreaterThan(0, "should extract nodes from C# files");
        result.EdgesCreated.Should().BeGreaterThan(0, "should create containment and structural edges");
        result.EmbeddingsGenerated.Should().BeGreaterThan(0, "should generate embeddings for symbol nodes");
        result.Errors.Should().BeEmpty("no errors expected for valid fixture files");
    }

    [TestMethod]
    public async Task ExtractAsync_WithPowerShellFiles_ReturnsNodesAndEdges()
    {
        var scriptsDir = Path.Combine(this.fixtureRoot, "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(
            Path.Combine(scriptsDir, "Deploy.ps1"),
            @"function Invoke-Deploy {
    param([string]$Environment)
    Write-Host ""Deploying to $Environment""
}

Invoke-Deploy -Environment 'Production'
");

        var embedGen = new FakeEmbeddingGenerator();
        var pipeline = this.CreatePipelineWithEmbeddings(embedGen);
        var options = new ExtractionOptions
        {
            Languages = new[] { "PowerShell" },
            GenerateEmbeddings = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        result.NodesCreated.Should().BeGreaterThan(0);
        result.Errors.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExtractAsync_DefaultOptions_HasCorrectLanguages()
    {
        var options = new ExtractionOptions();

        options.Languages.Should().BeEquivalentTo(new[] { "C#", "PowerShell" });
        options.IncludeTestProjects.Should().BeFalse();
        options.GenerateEmbeddings.Should().BeTrue();
        options.CommitSha.Should().BeNull();
        options.SimilarityThreshold.Should().Be(0.85, "default SimilarityThreshold per architecture §2.4");
    }

    [TestMethod]
    public async Task ExtractAsync_WithMixedLanguages_ExtractsBoth()
    {
        this.CreateFixtureFiles();
        var scriptsDir = Path.Combine(this.fixtureRoot, "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(
            Path.Combine(scriptsDir, "Build.ps1"),
            @"function Invoke-Build {
    dotnet build
}
");

        var pipeline = this.CreatePipelineWithEmbeddings(new FakeEmbeddingGenerator());
        var options = new ExtractionOptions
        {
            Languages = new[] { "C#", "PowerShell" },
            IncludeTestProjects = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        result.NodesCreated.Should().BeGreaterThan(0);
        result.EdgesCreated.Should().BeGreaterThan(0);
        result.Errors.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExtractFilesAsync_OnlyReExtracts_SpecifiedFiles()
    {
        this.CreateFixtureFiles();

        var pipeline = this.CreatePipelineWithEmbeddings(new FakeEmbeddingGenerator());
        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
        };

        // First, do a full extraction to establish a baseline
        var fullResult = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);
        var fullNodeCount = fullResult.NodesCreated;

        // Now do incremental extraction for just 1 file
        var partialResult = await pipeline.ExtractFilesAsync(
            this.fixtureRoot,
            new[] { "src/Services/PaymentService.cs" },
            options,
            CancellationToken.None);

        partialResult.NodesCreated.Should().BeGreaterThan(0, "should extract nodes for the changed file");
        partialResult.NodesCreated.Should().BeLessThan(fullNodeCount,
            "partial extraction should produce fewer nodes than full extraction");
        partialResult.Errors.Should().BeEmpty();

        // Verify: the incremental result does NOT contain nodes from other files
        // Do a fresh incremental extraction to check the data shape
        var verifyPipeline = this.CreatePipelineWithEmbeddings(new FakeEmbeddingGenerator());
        var verifyStore = new StatefulMockGraphStore();
        var verifyPipelineWithStore = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(new FakeEmbeddingGenerator(), null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            verifyStore,
            NullLogger<ExtractionPipeline>.Instance);
        await verifyPipelineWithStore.ExtractFilesAsync(
            this.fixtureRoot,
            new[] { "src/Services/PaymentService.cs" },
            options,
            CancellationToken.None);

        // All persisted Module (file) nodes must be for the changed file only
        var persistedFileNodes = verifyStore.ActiveNodes.Values
            .Where(n => n.NodeType == "Module" || n.NodeType == "File")
            .ToList();
        foreach (var fNode in persistedFileNodes)
        {
            fNode.FilePath.Should().Contain("PaymentService",
                $"only nodes from changed file 'PaymentService.cs' expected, but got file node for '{fNode.FilePath}'");
        }

        // Symbol nodes with a FilePath should only be from the changed file
        // (excluding structural-only nodes like Repo, Package, Namespace)
        var nonStructuralNodes = verifyStore.ActiveNodes.Values
            .Where(n => !string.IsNullOrEmpty(n.FilePath)
                && n.NodeType is not "Repo" and not "Package" and not "Namespace" and not "Module")
            .ToList();
        foreach (var symNode in nonStructuralNodes)
        {
            // Allow referenced nodes from unchanged files that appear as edge endpoints
            if (symNode.Metadata.TryGetValue("ExternalCanonical", out var ec) && ec is true)
            {
                continue;
            }

            symNode.FilePath.Should().Contain("PaymentService",
                $"only changed-file symbols expected, but got '{symNode.Name}' from '{symNode.FilePath}'");
        }
    }

    [TestMethod]
    public async Task ExtractFilesAsync_WithPreviouslyIndexedRepo_TwoChangedFiles_ValidatesRetirement()
    {
        // Create a fixture repo with 4 files
        this.CreateFixtureFiles();

        var store = new StatefulMockGraphStore();
        var pipeline = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(new FakeEmbeddingGenerator(), null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            store,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
            CommitSha = "abc123",
            CommitIndex = 1,
        };

        // Step 1: Full extraction — indexes all files
        var fullResult = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);
        fullResult.Errors.Should().BeEmpty();

        var nodesAfterFull = store.ActiveNodes.Count;
        var edgesAfterFull = store.ActiveEdges.Count;
        nodesAfterFull.Should().BeGreaterThan(0, "full extraction should persist nodes to store");
        edgesAfterFull.Should().BeGreaterThan(0, "full extraction should persist edges to store");

        // Record which nodes/edges were from unchanged files
        var unchangedFileNodes = store.ActiveNodes.Values
            .Where(n => n.FilePath != null
                && !n.FilePath.Contains("PaymentService")
                && !n.FilePath.Contains("OrderService"))
            .Select(n => n.Id)
            .ToHashSet();

        // Step 2: Incremental extraction — only 2 changed files
        var incrementalOptions = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
            CommitSha = "def456",
            CommitIndex = 2,
        };

        var changedFiles = new[] { "src/Services/PaymentService.cs", "src/Services/OrderService.cs" };
        var partialResult = await pipeline.ExtractFilesAsync(
            this.fixtureRoot,
            changedFiles,
            incrementalOptions,
            CancellationToken.None);

        partialResult.NodesCreated.Should().BeGreaterThan(0, "should re-extract nodes for changed files");
        partialResult.Errors.Should().BeEmpty();

        // Verify: stale nodes from changed files were retired before re-insertion
        store.RetiredNodeIds.Should().NotBeEmpty(
            "nodes from changed files should be retired before re-extraction");

        // Verify: nodes from unchanged files should NOT have been retired
        foreach (var unchangedNodeId in unchangedFileNodes)
        {
            store.RetiredNodeIds.Should().NotContain(unchangedNodeId,
                $"node {unchangedNodeId} is from an unchanged file and should not be retired");
        }

        // Verify: edges entirely between unchanged files should survive
        var unchangedOnlyEdges = store.ActiveEdges.Values
            .Where(e => e.NodeIds.All(id => unchangedFileNodes.Contains(id)))
            .ToList();
        unchangedOnlyEdges.Should().NotBeEmpty(
            "edges between unchanged files should be preserved");

        // Verify: new nodes/edges were persisted for changed files
        store.ActiveNodes.Values
            .Where(n => n.FilePath != null && n.FilePath.Contains("PaymentService"))
            .Should().NotBeEmpty("PaymentService nodes should be re-persisted");

        // Verify: incremental only parsed the changed files, not the entire repo.
        // The partial result node count must be strictly less than the full count.
        partialResult.NodesCreated.Should().BeLessThan(fullResult.NodesCreated,
            "incremental extraction must not re-parse the entire repo — only changed files");

        // Verify: file nodes from unchanged files (e.g., Order.cs, Payment.cs) were not
        // re-emitted via incremental. The store should have them from full extraction,
        // but they should not appear in the retirement set.
        var unchangedModelNodes = store.ActiveNodes.Values
            .Where(n => n.FilePath != null
                && (n.FilePath.Contains("Order.cs") || n.FilePath.Contains("Payment.cs"))
                && n.NodeType == "Module")
            .ToList();
        unchangedModelNodes.Should().NotBeEmpty("unchanged file nodes from full extraction should remain");
        foreach (var modelNode in unchangedModelNodes)
        {
            store.RetiredNodeIds.Should().NotContain(modelNode.Id,
                $"file node {modelNode.Id} is from an unchanged file and must not be retired during incremental");
        }
    }

    [TestMethod]
    public async Task ExtractFilesAsync_WithEmptyList_ReturnsEmptyResult()
    {
        var pipeline = this.CreatePipeline();
        var options = new ExtractionOptions();

        var result = await pipeline.ExtractFilesAsync(
            this.fixtureRoot,
            Array.Empty<string>(),
            options,
            CancellationToken.None);

        result.NodesCreated.Should().Be(0);
        result.EdgesCreated.Should().Be(0);
    }

    [TestMethod]
    public async Task ExtractFilesAsync_WithMissingFile_RecordsError()
    {
        var pipeline = this.CreatePipeline();
        var options = new ExtractionOptions();

        var result = await pipeline.ExtractFilesAsync(
            this.fixtureRoot,
            new[] { "nonexistent/File.cs" },
            options,
            CancellationToken.None);

        result.Errors.Should().NotBeEmpty();
        result.FilesSkipped.Should().Be(1);
    }

    [TestMethod]
    public async Task ExtractAsync_WithNonexistentRepoRoot_ReturnsError()
    {
        var pipeline = this.CreatePipeline();
        var options = new ExtractionOptions();

        var result = await pipeline.ExtractAsync(
            Path.Combine(this.fixtureRoot, "nonexistent"),
            options,
            CancellationToken.None);

        result.Errors.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task ExtractAsync_SkipsTestProjects_WhenIncludeTestProjectsIsFalse()
    {
        var testDir = Path.Combine(this.fixtureRoot, "src", "Services.UnitTests");
        Directory.CreateDirectory(testDir);
        File.WriteAllText(
            Path.Combine(testDir, "PaymentServiceTests.cs"),
            @"namespace Services.UnitTests;
public class PaymentServiceTests
{
    public void TestPayment() { }
}
");

        var pipeline = this.CreatePipeline();
        var optionsExclude = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = false,
        };

        var resultExclude = await pipeline.ExtractAsync(this.fixtureRoot, optionsExclude, CancellationToken.None);

        var optionsInclude = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
        };

        var resultInclude = await pipeline.ExtractAsync(this.fixtureRoot, optionsInclude, CancellationToken.None);

        resultInclude.NodesCreated.Should().BeGreaterThanOrEqualTo(resultExclude.NodesCreated);
    }

    [TestMethod]
    public async Task ExtractAsync_StructureBuilderFailure_SkipsDownstreamStages()
    {
        this.CreateFixtureFiles();

        var failingStructureBuilder = new FailingStructureBuilder();
        var pipeline = new ExtractionPipeline(
            failingStructureBuilder,
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(null, null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            null,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        result.Errors.Should().Contain(e => e.Contains("StructureBuilder failed"));
    }

    [TestMethod]
    public async Task ExtractAsync_CallChainAnalyzerFailure_SkipsSemanticIndexer()
    {
        this.CreateFixtureFiles();

        var failingCallChain = new FailingCallChainAnalyzer();
        var pipeline = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            failingCallChain,
            new SemanticIndexer(null, null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            null,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        result.Errors.Should().Contain(e => e.Contains("CallChainAnalyzer failed"));
    }

    [TestMethod]
    public async Task ExtractAsync_CancellationToken_Throws()
    {
        this.CreateFixtureFiles();

        var pipeline = this.CreatePipeline();
        var options = new ExtractionOptions { Languages = new[] { "C#" }, IncludeTestProjects = true };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await pipeline.ExtractAsync(this.fixtureRoot, options, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task ExtractAsync_PersistsToGraphStore_WithVersionRange()
    {
        this.CreateFixtureFiles();

        var mockStore = new StatefulMockGraphStore();
        var pipeline = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(null, null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            mockStore,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
            CommitSha = "abc123",
            CommitIndex = 5,
        };

        await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        mockStore.ActiveNodes.Should().NotBeEmpty("should persist nodes to store");
        mockStore.LastVersionRange.Should().NotBeNull("should use VersionRange for persistence");
        mockStore.LastVersionRange!.SinceCommit.Should().Be("abc123");
        mockStore.LastVersionRange!.SinceIndex.Should().Be(5);
        mockStore.AuditLog.Should().NotBeEmpty("should log audit events");
    }

    [TestMethod]
    public async Task ExtractAsync_VectorStoreReceivesMemoryEmbeddings()
    {
        this.CreateFixtureFiles();

        var embedGen = new FakeEmbeddingGenerator();
        var vectorStore = new FakeVectorStore();
        var pipeline = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(embedGen, vectorStore, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            null,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
            GenerateEmbeddings = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        result.EmbeddingsGenerated.Should().BeGreaterThan(0, "should generate embeddings");
        vectorStore.UpsertedCollections.Should().Contain("memory_embeddings",
            "vector store should receive points to 'memory_embeddings' collection");
    }

    [TestMethod]
    public async Task ExtractAsync_CrossLanguageDedup_PreservesEdgesFromBothLanguages()
    {
        // Create C# source with a PaymentService class
        var srcDir = Path.Combine(this.fixtureRoot, "src", "Services");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(
            Path.Combine(srcDir, "PaymentService.cs"),
            @"namespace MyApp.Services;

public class PaymentService
{
    public bool ProcessPayment(decimal amount) { return true; }
}
");

        // Create PowerShell script that calls PaymentService via .NET type reference
        var scriptsDir = Path.Combine(this.fixtureRoot, "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(
            Path.Combine(scriptsDir, "InvokePayment.ps1"),
            @"# Cross-language call to PaymentService
function Invoke-Payment {
    param([decimal]$Amount)
    $svc = [MyApp.Services.PaymentService]::new()
    $svc.ProcessPayment($Amount)
}
");

        // Run the REAL pipeline — it should synthesize cross-language type references
        // and merge them with the C# definitions via FactNormalizer
        var store = new StatefulMockGraphStore();
        var pipeline = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(new FakeEmbeddingGenerator(), null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            store,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#", "PowerShell" },
            IncludeTestProjects = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        result.NodesCreated.Should().BeGreaterThan(0);
        result.Errors.Should().BeEmpty();

        // Verify: exactly one canonical PaymentService Class node exists in the store
        var paymentNodes = store.ActiveNodes.Values
            .Where(n => n.Name == "PaymentService"
                && string.Equals(n.NodeType, "Class", StringComparison.OrdinalIgnoreCase))
            .ToList();
        paymentNodes.Should().HaveCount(1,
            "cross-language merge should produce exactly one canonical PaymentService node — "
            + "the pipeline synthesizes a PS reference node for [MyApp.Services.PaymentService]::new() "
            + "and FactNormalizer merges it with the C# definition by QualifiedName");

        var canonicalPayment = paymentNodes[0];
        canonicalPayment.Language.Should().Be("C#",
            "C# definition should win over PowerShell stub in merge");
        canonicalPayment.Id.Should().StartWith("/",
            "canonical ID should start with / per architecture §4.2");

        // Verify: all edge endpoints reference valid node IDs
        foreach (var edge in store.ActiveEdges.Values)
        {
            foreach (var nid in edge.NodeIds)
            {
                store.ActiveNodes.Should().ContainKey(nid,
                    $"edge '{edge.Id}' references node '{nid}' which should exist");
            }
        }

        // Verify: all edges are binary (exactly 2 distinct endpoints)
        foreach (var edge in store.ActiveEdges.Values)
        {
            edge.NodeIds.Distinct().Count().Should().Be(2,
                $"edge '{edge.Id}' of type {edge.EdgeType} should be binary (2 distinct endpoints)");
        }

        // Verify: all edges have MemoryLayer metadata
        foreach (var edge in store.ActiveEdges.Values)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer,
                $"edge '{edge.Id}' should have MemoryLayer metadata after post-stage stamping");
        }

        // Verify: PS function node exists
        var psFunc = store.ActiveNodes.Values
            .FirstOrDefault(n => n.Name == "Invoke-Payment");
        psFunc.Should().NotBeNull("PowerShell function node should be preserved");

        // Verify: at least one CALLS edge connects to the canonical PaymentService
        // This proves the cross-language reference was correctly synthesized and resolved
        // Debug: print all edges to diagnose
        foreach (var edge in store.ActiveEdges.Values)
        {
            var sr = edge.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var r) ? r?.ToString() : "N/A";
            var nodeNames = string.Join(", ", edge.NodeIds.Select(id =>
                store.ActiveNodes.TryGetValue(id, out var n) ? $"{n.Name}({n.Language})" : id));
            Console.WriteLine($"  Edge: {edge.Id} | Type: {edge.EdgeType} | SR: {sr} | Nodes: [{nodeNames}]");
        }

        Console.WriteLine($"  Canonical PaymentService ID: {canonicalPayment.Id}");
        Console.WriteLine($"  Total edges: {store.ActiveEdges.Count}");

        var callsEdgesToPayment = store.ActiveEdges.Values
            .Where(e => e.NodeIds.Contains(canonicalPayment.Id)
                && e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var rel)
                && rel?.ToString() == "CALLS")
            .ToList();
        callsEdgesToPayment.Should().NotBeEmpty(
            "a CALLS edge from PS function to canonical PaymentService should exist — "
            + "this proves the cross-language type reference [MyApp.Services.PaymentService]::new() "
            + "was found via source-text scanning and connected to the C# definition");

        // Verify the CALLS edge has a PowerShell caller endpoint
        var crossLangCallEdge = callsEdgesToPayment.FirstOrDefault(e =>
        {
            var callerIds = e.Roles.Where(r => r.Value == "caller").Select(r => r.Key).ToList();
            return callerIds.Any(id =>
                store.ActiveNodes.TryGetValue(id, out var callerNode)
                && callerNode.Language.Equals("PowerShell", StringComparison.OrdinalIgnoreCase));
        });
        crossLangCallEdge.Should().NotBeNull(
            "a CALLS edge should exist where the caller is a PowerShell node (Invoke-Payment) "
            + "and the callee is the canonical C# PaymentService");

        // Verify: C# file node and containment edge exist
        var csFileNode = store.ActiveNodes.Values
            .FirstOrDefault(n => (n.NodeType == "File" || n.NodeType == "Module")
                && n.FilePath != null
                && n.FilePath.Contains("PaymentService.cs"));
        csFileNode.Should().NotBeNull("C# file/module node should exist");

        // Verify: all nodes have MemoryLayer metadata
        foreach (var node in store.ActiveNodes.Values)
        {
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer,
                $"node '{node.Id}' should have MemoryLayer metadata");
        }
    }

    [TestMethod]
    public async Task ExtractAsync_RealParser_CanonicalQualifiedNames_ResolvedCorrectly()
    {
        // Test that real C# parser output produces correct canonical IDs
        var srcDir = Path.Combine(this.fixtureRoot, "src", "Services");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(
            Path.Combine(srcDir, "PaymentService.cs"),
            @"namespace MyApp.Services;

public class PaymentService
{
    public bool ProcessPayment(decimal amount)
    {
        return amount > 0;
    }
}
");
        File.WriteAllText(
            Path.Combine(srcDir, "OrderService.cs"),
            @"namespace MyApp.Services;

public class OrderService
{
    private readonly PaymentService paymentService;

    public void PlaceOrder(decimal total)
    {
        this.paymentService.ProcessPayment(total);
    }
}
");

        var pipeline = this.CreatePipeline();
        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        result.NodesCreated.Should().BeGreaterThan(0);
        result.Errors.Should().BeEmpty();

        // Verify normalization separately: parse and normalize, check canonical IDs
        var normalizer = new FactNormalizer(NullLogger<FactNormalizer>.Instance);
        var data = new HypergraphData();

        // Parse files using real extractors
        var csExtractor1 = new Ast.Csharp.CSharpHyperedgeExtractor("src/Services/PaymentService.cs");
        var src1 = File.ReadAllText(Path.Combine(srcDir, "PaymentService.cs"));
        var result1 = csExtractor1.Extract(src1);
        foreach (var kvp in result1.Nodes) { data.Nodes.TryAdd(kvp.Key, kvp.Value); }
        data.Hyperedges.AddRange(result1.Hyperedges);

        var csExtractor2 = new Ast.Csharp.CSharpHyperedgeExtractor("src/Services/OrderService.cs");
        var src2 = File.ReadAllText(Path.Combine(srcDir, "OrderService.cs"));
        var result2 = csExtractor2.Extract(src2);
        foreach (var kvp in result2.Nodes) { data.Nodes.TryAdd(kvp.Key, kvp.Value); }
        data.Hyperedges.AddRange(result2.Hyperedges);

        // Add file nodes
        data.Nodes.TryAdd(CanonicalId.ForFile("src/Services/PaymentService.cs"), new HypergraphNode
        {
            Id = CanonicalId.ForFile("src/Services/PaymentService.cs"),
            Name = "PaymentService.cs",
            NodeType = "File",
            Language = "C#",
            FilePath = "src/Services/PaymentService.cs",
        });
        data.Nodes.TryAdd(CanonicalId.ForFile("src/Services/OrderService.cs"), new HypergraphNode
        {
            Id = CanonicalId.ForFile("src/Services/OrderService.cs"),
            Name = "OrderService.cs",
            NodeType = "File",
            Language = "C#",
            FilePath = "src/Services/OrderService.cs",
        });

        var normResult = normalizer.Normalize(data, this.fixtureRoot);

        // All node IDs should be canonical (start with / for in-repo nodes)
        foreach (var nodeId in data.Nodes.Keys)
        {
            nodeId.Should().StartWith("/", $"node ID '{nodeId}' should be canonical (/-prefixed)");
        }

        // Class nodes should have qualified names in their IDs
        var paymentNode = data.Nodes.Values
            .FirstOrDefault(n => n.Name == "PaymentService" && n.NodeType == "Class");
        paymentNode.Should().NotBeNull("PaymentService class should exist");
        paymentNode!.Id.Should().Contain("::", "symbol IDs should contain :: separator");
        paymentNode.Metadata.Should().ContainKey(MemoryMetadataKeys.QualifiedName,
            "class nodes should have QualifiedName metadata");
        paymentNode.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash);
        paymentNode.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer);

        // Edges should reference only canonical IDs
        foreach (var edge in data.Hyperedges)
        {
            foreach (var nid in edge.NodeIds)
            {
                data.Nodes.Should().ContainKey(nid,
                    $"edge '{edge.Id}' references node '{nid}' which should exist after normalization");
            }
        }
    }

    [TestMethod]
    public async Task ExtractFilesAsync_RetiresOnlyEdgesWithChangedFileEndpoint()
    {
        this.CreateFixtureFiles();

        var store = new StatefulMockGraphStore();
        var pipeline = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(null, null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            store,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
            CommitSha = "initial",
            CommitIndex = 1,
        };

        // Full extraction
        await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);

        var edgesBeforeIncremental = store.ActiveEdges.Count;

        // Find edges between only unchanged files (e.g., IPaymentGateway ↔ StripeGateway)
        var unchangedFileEdgesBefore = store.ActiveEdges.Values
            .Where(e => e.NodeIds.All(id =>
            {
                var node = store.ActiveNodes.GetValueOrDefault(id);
                return node != null
                    && node.FilePath != null
                    && !node.FilePath.Contains("PaymentService");
            }))
            .Select(e => e.Id)
            .ToHashSet();

        // Incremental extraction: only PaymentService changed
        var incrementalOptions = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
            CommitSha = "incremental",
            CommitIndex = 2,
        };

        await pipeline.ExtractFilesAsync(
            this.fixtureRoot,
            new[] { "src/Services/PaymentService.cs" },
            incrementalOptions,
            CancellationToken.None);

        // Edges between unchanged files should NOT have been retired
        foreach (var edgeId in unchangedFileEdgesBefore)
        {
            store.RetiredEdgeIds.Should().NotContain(edgeId,
                $"edge {edgeId} is between unchanged files and should not be retired");
        }
    }

    [TestMethod]
    public async Task ExtractAsync_AllEdgesHaveMemoryLayerMetadata()
    {
        this.CreateFixtureFiles();

        var store = new StatefulMockGraphStore();
        var pipeline = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(new FakeEmbeddingGenerator(), null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            store,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);
        result.Errors.Should().BeEmpty();

        // Every persisted edge must be binary (exactly 2 distinct endpoints)
        foreach (var edge in store.ActiveEdges.Values)
        {
            edge.NodeIds.Distinct().Count().Should().Be(2,
                $"edge '{edge.Id}' of type {edge.EdgeType} should be binary (2 distinct endpoints)");
        }

        // Every persisted edge must have MemoryLayer, SemanticRelation, and OriginalEdgeType
        foreach (var edge in store.ActiveEdges.Values)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer,
                $"edge '{edge.Id}' of type {edge.EdgeType} should have MemoryLayer metadata");
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.SemanticRelation,
                $"edge '{edge.Id}' should have SemanticRelation metadata");
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.OriginalEdgeType,
                $"edge '{edge.Id}' should have OriginalEdgeType metadata");
        }

        // Every persisted node must have MemoryLayer
        foreach (var node in store.ActiveNodes.Values)
        {
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer,
                $"node '{node.Id}' should have MemoryLayer metadata");
        }
    }

    [TestMethod]
    public async Task ExtractAsync_CallChainAnalyzer_ResolvesCalleesToDefinitions()
    {
        // Setup: OrderService.PlaceOrder() calls PaymentService.ProcessPayment()
        var srcDir = Path.Combine(this.fixtureRoot, "src", "Services");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(
            Path.Combine(srcDir, "PaymentService.cs"),
            @"namespace MyApp.Services;

public class PaymentService
{
    public bool ProcessPayment(decimal amount) { return amount > 0; }
}
");
        File.WriteAllText(
            Path.Combine(srcDir, "OrderService.cs"),
            @"namespace MyApp.Services;

public class OrderService
{
    private readonly PaymentService paymentService;

    public void PlaceOrder(decimal total)
    {
        this.paymentService.ProcessPayment(total);
    }
}
");

        var store = new StatefulMockGraphStore();
        var pipeline = new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(new FakeEmbeddingGenerator(), null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            store,
            NullLogger<ExtractionPipeline>.Instance);

        var options = new ExtractionOptions
        {
            Languages = new[] { "C#" },
            IncludeTestProjects = true,
        };

        var result = await pipeline.ExtractAsync(this.fixtureRoot, options, CancellationToken.None);
        result.Errors.Should().BeEmpty();

        // Find CallChainAnalyzer-generated CALLS edges (Id starts with "calls:")
        // These are the resolved edges, distinct from raw parser MethodInvocation edges
        var callChainEdges = store.ActiveEdges.Values
            .Where(e => e.Id.StartsWith("calls:", StringComparison.Ordinal))
            .ToList();

        // All CallChainAnalyzer CALLS edge endpoints should point to existing definition nodes
        foreach (var edge in callChainEdges)
        {
            foreach (var nodeId in edge.NodeIds)
            {
                store.ActiveNodes.Should().ContainKey(nodeId,
                    $"CALLS edge '{edge.Id}' endpoint '{nodeId}' should reference an existing node");

                var node = store.ActiveNodes[nodeId];
                // The callee role should point to a definition type, not a pseudo/reference node
                if (edge.Roles.TryGetValue(nodeId, out var role) && role == "callee")
                {
                    var defTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "Class", "Interface", "Method", "Function", "Property", "Constructor",
                    };
                    defTypes.Should().Contain(node.NodeType,
                        $"callee node '{nodeId}' (type '{node.NodeType}') in CALLS edge '{edge.Id}' "
                        + "should be resolved to a canonical definition type, not a reference node");
                }
            }
        }

        // Verify CallChainAnalyzer produced at least some CALLS edges
        callChainEdges.Should().NotBeEmpty(
            "CallChainAnalyzer should produce resolved CALLS edges from method invocations");

        // All CALLS edges (including analyzer-generated) should have MemoryLayer metadata
        var allCallsEdges = store.ActiveEdges.Values
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var r)
                && r?.ToString() == "CALLS")
            .ToList();
        foreach (var edge in allCallsEdges)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer,
                $"CALLS edge '{edge.Id}' should have MemoryLayer metadata");
        }

        // CallChainAnalyzer edges should specifically have MemoryLayer = call_chain
        foreach (var edge in callChainEdges)
        {
            edge.Metadata[MemoryMetadataKeys.MemoryLayer]?.ToString()
                .Should().Be("call_chain",
                    $"CallChainAnalyzer edge '{edge.Id}' should have MemoryLayer = call_chain");
        }
    }

    private ExtractionPipeline CreatePipeline()
    {
        return new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(null, null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            null,
            NullLogger<ExtractionPipeline>.Instance);
    }

    private ExtractionPipeline CreatePipelineWithEmbeddings(IEmbeddingGenerator embedGen)
    {
        return new ExtractionPipeline(
            new StructureBuilder(NullLogger<StructureBuilder>.Instance),
            new CallChainAnalyzer(NullLogger<CallChainAnalyzer>.Instance),
            new SemanticIndexer(embedGen, null, NullLogger<SemanticIndexer>.Instance),
            new FactNormalizer(NullLogger<FactNormalizer>.Instance),
            null,
            NullLogger<ExtractionPipeline>.Instance);
    }

    private void CreateFixtureFiles()
    {
        var srcDir = Path.Combine(this.fixtureRoot, "src", "Services");
        Directory.CreateDirectory(srcDir);

        File.WriteAllText(
            Path.Combine(srcDir, "PaymentService.cs"),
            @"namespace MyApp.Services;

public class PaymentService
{
    private readonly IPaymentGateway gateway;

    public PaymentService(IPaymentGateway gateway)
    {
        this.gateway = gateway;
    }

    public bool ProcessPayment(decimal amount)
    {
        return this.gateway.Charge(amount);
    }
}
");
        File.WriteAllText(
            Path.Combine(srcDir, "IPaymentGateway.cs"),
            @"namespace MyApp.Services;

public interface IPaymentGateway
{
    bool Charge(decimal amount);
}
");
        File.WriteAllText(
            Path.Combine(srcDir, "OrderService.cs"),
            @"namespace MyApp.Services;

public class OrderService
{
    private readonly PaymentService paymentService;

    public OrderService(PaymentService paymentService)
    {
        this.paymentService = paymentService;
    }

    public void PlaceOrder(decimal total)
    {
        this.paymentService.ProcessPayment(total);
    }
}
");
        File.WriteAllText(
            Path.Combine(srcDir, "StripeGateway.cs"),
            @"namespace MyApp.Services;

public class StripeGateway : IPaymentGateway
{
    public bool Charge(decimal amount)
    {
        return amount > 0;
    }
}
");

        var modelsDir = Path.Combine(this.fixtureRoot, "src", "Models");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllText(
            Path.Combine(modelsDir, "Order.cs"),
            @"namespace MyApp.Models;

public class Order
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}
");
        File.WriteAllText(
            Path.Combine(modelsDir, "Payment.cs"),
            @"namespace MyApp.Models;

public class Payment
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; }
}
");
    }

    private class FailingStructureBuilder : IStructureBuilder
    {
        public Task<StageResult> BuildAsync(HypergraphData data, string repoRoot, ExtractionOptions options, CancellationToken ct = default)
            => throw new InvalidOperationException("StructureBuilder simulated failure");
    }

    private class FailingCallChainAnalyzer : ICallChainAnalyzer
    {
        public Task<StageResult> AnalyzeAsync(HypergraphData data, string repoRoot, ExtractionOptions options, CancellationToken ct = default)
            => throw new InvalidOperationException("CallChainAnalyzer simulated failure");
    }

    /// <summary>
    /// Stateful mock graph store that tracks nodes, edges, retirements, versions, and audit log.
    /// Matches architecture §5.1 contract.
    /// </summary>
    private class StatefulMockGraphStore : IMemoryGraphStore
    {
        public Dictionary<string, HypergraphNode> ActiveNodes { get; } = new();
        public Dictionary<string, Hyperedge> ActiveEdges { get; } = new();
        public HashSet<string> RetiredNodeIds { get; } = new();
        public HashSet<string> RetiredEdgeIds { get; } = new();
        public VersionRange? LastVersionRange { get; private set; }
        public List<(string Session, string Op, object Detail)> AuditLog { get; } = new();
        public Dictionary<string, ExpertiseEntry> Expertise { get; } = new();

        public string Name => "stateful-mock-graph-store";

        // §5.1 methods
        public Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
            string memoryLayer, int? atCommitIndex = null, NodeFilter? filter = null,
            int limit = 100, CancellationToken ct = default)
        {
            var result = this.ActiveNodes.Values
                .Where(n => n.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var l) && l?.ToString() == memoryLayer)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<HypergraphNode>>(result);
        }

        public Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
            string semanticRelation, string? memoryLayer = null, int? atCommitIndex = null,
            int limit = 100, CancellationToken ct = default)
        {
            var result = this.ActiveEdges.Values
                .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var r) && r?.ToString() == semanticRelation)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
        }

        public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct = default)
        {
            this.LastVersionRange = version;
            this.ActiveNodes[node.Id] = node;
            return Task.CompletedTask;
        }

        public Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default)
        {
            this.RetiredNodeIds.Add(nodeId);
            this.ActiveNodes.Remove(nodeId);
            return Task.CompletedTask;
        }

        public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default)
        {
            this.RetiredEdgeIds.Add(edgeId);
            this.ActiveEdges.Remove(edgeId);
            return Task.CompletedTask;
        }

        public Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> updates, CancellationToken ct = default)
        {
            if (this.ActiveEdges.TryGetValue(edgeId, out var edge))
            {
                foreach (var kvp in updates)
                {
                    edge.Metadata[kvp.Key] = kvp.Value;
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
            string targetNodeId, double minConfidence = 0.0, CancellationToken ct = default)
        {
            var result = this.Expertise.Values
                .Where(e => e.TargetNodeId == targetNodeId && !e.Archived && e.Confidence >= minConfidence)
                .ToList();
            return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
        }

        public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct = default)
        {
            this.Expertise[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct = default)
        {
            if (this.Expertise.TryGetValue(expertiseId, out var entry))
            {
                entry.Archived = true;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
            string? targetNodeId = null, int limit = 50, CancellationToken ct = default)
        {
            var result = this.Expertise.Values
                .Where(e => e.Archived && (targetNodeId == null || e.TargetNodeId == targetNodeId))
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
        }

        public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new MemoryGraphStatistics
            {
                TotalNodes = this.ActiveNodes.Count + this.RetiredNodeIds.Count,
                ActiveNodes = this.ActiveNodes.Count,
                RetiredNodes = this.RetiredNodeIds.Count,
                TotalEdges = this.ActiveEdges.Count + this.RetiredEdgeIds.Count,
                ActiveEdges = this.ActiveEdges.Count,
                RetiredEdges = this.RetiredEdgeIds.Count,
                ExpertiseEntries = this.Expertise.Count(e => !e.Value.Archived),
                ArchivedExpertise = this.Expertise.Count(e => e.Value.Archived),
            });
        }

        public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default)
        {
            this.AuditLog.Add((sessionId, operation, detail));
            return Task.CompletedTask;
        }

        // Convenience bulk methods
        public Task<int> UpsertNodesAsync(IEnumerable<HypergraphNode> nodes, VersionRange version, CancellationToken ct = default)
        {
            var count = 0;
            foreach (var node in nodes)
            {
                this.LastVersionRange = version;
                this.ActiveNodes[node.Id] = node;
                count++;
            }

            return Task.FromResult(count);
        }

        public Task<int> UpsertEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken ct = default)
        {
            var count = 0;
            foreach (var edge in edges)
            {
                this.ActiveEdges[edge.Id] = edge;
                count++;
            }

            return Task.FromResult(count);
        }

        public Task<IReadOnlyList<HypergraphNode>> GetNodesByFilePathAsync(string filePath, CancellationToken ct = default)
        {
            var result = this.ActiveNodes.Values
                .Where(n => !string.IsNullOrEmpty(n.FilePath) && n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Task.FromResult<IReadOnlyList<HypergraphNode>>(result);
        }

        public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodesAsync(IEnumerable<string> nodeIds, CancellationToken ct = default)
        {
            var nodeIdSet = new HashSet<string>(nodeIds);
            var result = this.ActiveEdges.Values
                .Where(e => e.NodeIds.Any(id => nodeIdSet.Contains(id)))
                .ToList();
            return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
        }

        // IGraphStore base methods
        public Task CreateNodeAsync(HypergraphNode node, CancellationToken cancellationToken = default)
        {
            this.ActiveNodes[node.Id] = node;
            return Task.CompletedTask;
        }

        public Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken cancellationToken = default)
        {
            var count = 0;
            foreach (var n in nodes)
            {
                this.ActiveNodes[n.Id] = n;
                count++;
            }

            return Task.FromResult(count);
        }

        public Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken = default)
        {
            this.ActiveEdges[edge.Id] = edge;
            return Task.CompletedTask;
        }

        public Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken cancellationToken = default)
        {
            var count = 0;
            foreach (var e in edges)
            {
                this.ActiveEdges[e.Id] = e;
                count++;
            }

            return Task.FromResult(count);
        }

        public Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default)
            => Task.FromResult(this.ActiveNodes.GetValueOrDefault(nodeId));

        public Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(NodeFilter? filter = null, int limit = 100, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<HypergraphNode>>(this.ActiveNodes.Values.Take(limit).ToList());

        public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default)
            => Task.FromResult(this.ActiveEdges.GetValueOrDefault(edgeId));

        public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(string nodeId, IEnumerable<HyperedgeType>? edgeTypes = null, CancellationToken cancellationToken = default)
        {
            var result = this.ActiveEdges.Values.Where(e => e.NodeIds.Contains(nodeId)).ToList();
            return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
        }

        public Task<IReadOnlyList<GraphPath>> FindPathsAsync(string startNodeId, string endNodeId, int maxDepth = 5, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GraphPath>>(Array.Empty<GraphPath>());

        public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(string nodeId, int depth = 1, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NodeWithDepth>>(Array.Empty<NodeWithDepth>());

        public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(string nodeId, int depth = 1, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NodeWithDepth>>(Array.Empty<NodeWithDepth>());

        public Task<ImpactAnalysis> AnalyzeImpactAsync(string nodeId, int maxDepth = 3, CancellationToken cancellationToken = default)
            => Task.FromResult(new ImpactAnalysis { SourceNode = new HypergraphNode { Id = nodeId, Name = nodeId, NodeType = "Unknown", Language = "Unknown", FilePath = string.Empty } });

        public Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default)
        {
            this.ActiveNodes.Remove(nodeId);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            this.ActiveNodes.Clear();
            this.ActiveEdges.Clear();
            return Task.CompletedTask;
        }

        public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GraphStatistics { TotalNodes = this.ActiveNodes.Count, TotalEdges = this.ActiveEdges.Count });

        public Task CreateIndexesAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>
    /// Fake embedding generator that produces deterministic embeddings for testing.
    /// </summary>
    private class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        public int Dimensions => 4;
        public string ModelName => "fake-embed-test";

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MakeEmbedding(text));
        }

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
            IEnumerable<string> texts,
            CancellationToken cancellationToken = default)
        {
            var result = texts.Select(t => MakeEmbedding(t)).ToList();
            return Task.FromResult<IReadOnlyList<float[]>>(result);
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public void Dispose() { }

        private static float[] MakeEmbedding(string text)
        {
            var hash = text.GetHashCode();
            return new float[]
            {
                (hash & 0xFF) / 255f,
                ((hash >> 8) & 0xFF) / 255f,
                ((hash >> 16) & 0xFF) / 255f,
                ((hash >> 24) & 0xFF) / 255f,
            };
        }
    }

    /// <summary>
    /// Fake vector store that tracks which collections receive upserts.
    /// </summary>
    private class FakeVectorStore : IVectorStore
    {
        public string Name => "fake-vector-store";
        public HashSet<string> UpsertedCollections { get; } = new();

        public Task CreateCollectionAsync(string collectionName, int vectorSize, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task DeleteCollectionAsync(string collectionName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpsertAsync(string collectionName, IEnumerable<VectorPoint> points, CancellationToken cancellationToken = default)
        {
            this.UpsertedCollections.Add(collectionName);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string collectionName, float[] queryVector, int limit = 10, VectorFilter? filter = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VectorSearchResult>>(Array.Empty<VectorSearchResult>());

        public Task<VectorPoint?> GetAsync(string collectionName, string id, CancellationToken cancellationToken = default)
            => Task.FromResult<VectorPoint?>(null);

        public Task DeleteAsync(string collectionName, IEnumerable<string> ids, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> CountAsync(string collectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public void Dispose() { }
    }
}
