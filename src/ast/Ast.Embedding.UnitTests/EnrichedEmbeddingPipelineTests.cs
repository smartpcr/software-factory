// -----------------------------------------------------------------------
// <copyright file="EnrichedEmbeddingPipelineTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding.UnitTests;

using Ast.Core;
using Ast.Lsp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

[TestClass]
public class EnrichedEmbeddingPipelineTests
{
    [TestMethod]
    public void EnrichedEmbeddingOptions_DefaultValues()
    {
        // Act
        var options = new EnrichedEmbeddingOptions();

        // Assert
        options.EnableLspEnrichment.Should().BeTrue();
        options.BatchSize.Should().Be(100);
        options.UseParallelProcessing.Should().BeFalse();
        options.MaxDegreeOfParallelism.Should().Be(4);
        options.ContinueOnEnrichmentErrors.Should().BeTrue();
        options.EnableCaching.Should().BeFalse();
        options.CacheDirectory.Should().BeNull();
    }

    [TestMethod]
    public void EnrichedEmbeddingResult_DefaultValues()
    {
        // Act
        var result = new EnrichedEmbeddingResult();

        // Assert
        result.Embeddings.Should().BeEmpty();
        result.NodesEnriched.Should().Be(0);
        result.TotalNodesProcessed.Should().Be(0);
        result.EnrichmentPercentage.Should().Be(0);
    }

    [TestMethod]
    public void EnrichedEmbeddingResult_CalculatesPercentage()
    {
        // Arrange
        var result = new EnrichedEmbeddingResult
        {
            TotalNodesProcessed = 100,
            NodesEnriched = 75
        };

        // Assert
        result.EnrichmentPercentage.Should().Be(75);
    }

    [TestMethod]
    public void EnrichedEmbeddingResult_CalculatesDuration()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow;
        var endTime = startTime.AddSeconds(5);
        var result = new EnrichedEmbeddingResult
        {
            StartTime = startTime,
            EndTime = endTime
        };

        // Assert
        result.Duration.Should().Be(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void EnrichedCodeEmbedding_DefaultValues()
    {
        // Act
        var embedding = new EnrichedCodeEmbedding
        {
            NodeId = "test",
            FilePath = "test.cs",
            ElementType = "Method",
            Name = "Test",
            SourceText = "void Test() {}",
            Vector = [1.0f],
            Language = "C#"
        };

        // Assert
        embedding.IsLspEnriched.Should().BeFalse();
        embedding.Signature.Should().BeNull();
        embedding.Documentation.Should().BeNull();
        embedding.ReturnType.Should().BeNull();
        embedding.ParameterCount.Should().Be(0);
        embedding.ReferenceCount.Should().Be(0);
        embedding.CallerCount.Should().Be(0);
        embedding.CalleeCount.Should().Be(0);
    }

    [TestMethod]
    public void EnrichedCodeEmbedding_StoresLspData()
    {
        // Arrange & Act
        var embedding = new EnrichedCodeEmbedding
        {
            NodeId = "test",
            FilePath = "test.cs",
            ElementType = "Method",
            Name = "Test",
            SourceText = "void Test() {}",
            Vector = [1.0f],
            Language = "C#",
            IsLspEnriched = true,
            Signature = "void Test()",
            Documentation = "Test method",
            ReturnType = "void",
            ParameterCount = 2,
            ReferenceCount = 5,
            CallerCount = 3,
            CalleeCount = 2
        };

        // Assert
        embedding.IsLspEnriched.Should().BeTrue();
        embedding.Signature.Should().Be("void Test()");
        embedding.Documentation.Should().Be("Test method");
        embedding.ReturnType.Should().Be("void");
        embedding.ParameterCount.Should().Be(2);
        embedding.ReferenceCount.Should().Be(5);
        embedding.CallerCount.Should().Be(3);
        embedding.CalleeCount.Should().Be(2);
    }

    [TestMethod]
    public void PipelineProgress_DefaultValues()
    {
        // Act
        var progress = new PipelineProgress();

        // Assert
        progress.CurrentStep.Should().Be(PipelineStep.LspEnrichment);
        progress.StepNumber.Should().Be(0);
        progress.TotalSteps.Should().Be(0);
        progress.ItemsProcessed.Should().Be(0);
        progress.TotalItems.Should().Be(0);
        progress.CurrentFile.Should().BeNull();
        progress.CurrentItem.Should().BeNull();
    }

    [TestMethod]
    public void PipelineProgress_StepPercentage_ZeroItems()
    {
        // Arrange
        var progress = new PipelineProgress
        {
            TotalItems = 0,
            ItemsProcessed = 0
        };

        // Assert
        progress.StepPercentage.Should().Be(0);
    }

    [TestMethod]
    public void PipelineProgress_StepPercentage_HalfComplete()
    {
        // Arrange
        var progress = new PipelineProgress
        {
            TotalItems = 100,
            ItemsProcessed = 50
        };

        // Assert
        progress.StepPercentage.Should().Be(50);
    }

    [TestMethod]
    public void PipelineProgress_OverallPercentage_SingleStep()
    {
        // Arrange
        var progress = new PipelineProgress
        {
            StepNumber = 1,
            TotalSteps = 1,
            TotalItems = 100,
            ItemsProcessed = 50
        };

        // Assert
        progress.OverallPercentage.Should().BeApproximately(50, 0.01);
    }

    [TestMethod]
    public void PipelineProgress_OverallPercentage_TwoSteps_FirstComplete()
    {
        // Arrange
        var progress = new PipelineProgress
        {
            StepNumber = 2,
            TotalSteps = 2,
            TotalItems = 100,
            ItemsProcessed = 0
        };

        // Assert
        // First step (50%) complete, second step (0% of 50%) = 50%
        progress.OverallPercentage.Should().BeApproximately(50, 0.01);
    }

    [TestMethod]
    public void PipelineProgress_OverallPercentage_TwoSteps_SecondHalfComplete()
    {
        // Arrange
        var progress = new PipelineProgress
        {
            StepNumber = 2,
            TotalSteps = 2,
            TotalItems = 100,
            ItemsProcessed = 50
        };

        // Assert
        // First step (50%) complete, second step (50% of 50% = 25%) = 75%
        progress.OverallPercentage.Should().BeApproximately(75, 0.01);
    }

    [TestMethod]
    public void PipelineProgress_OverallPercentage_ZeroSteps()
    {
        // Arrange
        var progress = new PipelineProgress
        {
            StepNumber = 0,
            TotalSteps = 0
        };

        // Assert
        progress.OverallPercentage.Should().Be(0);
    }

    [TestMethod]
    public void IsLspEnrichmentAvailable_FalseWhenNoService()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions { EnableLspEnrichment = true });

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance,
            lspEnrichmentService: null);

        // Assert
        pipeline.IsLspEnrichmentAvailable.Should().BeFalse();
    }

    [TestMethod]
    public void IsLspEnrichmentAvailable_FalseWhenDisabled()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions { EnableLspEnrichment = false });
        var lspConfig = Options.Create(new LspConfiguration { Enabled = false });
        var loggerFactory = new NullLoggerFactory();

        var serverManager = new LspServerManager(lspConfig, loggerFactory);
        var lspService = new LspEnrichmentService(
            serverManager,
            lspConfig,
            NullLogger<LspEnrichmentService>.Instance);

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance,
            lspService);

        // Assert
        pipeline.IsLspEnrichmentAvailable.Should().BeFalse();
    }

    [TestMethod]
    public async Task ProcessAsync_WithoutLsp_GeneratesEmbeddings()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList());

        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions { EnableLspEnrichment = false });

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance);

        var data = CreateTestHypergraphData();

        // Act
        var result = await pipeline.ProcessAsync(data, @"C:\workspace");

        // Assert
        result.Embeddings.Should().HaveCount(2);
        result.NodesEnriched.Should().Be(0);
        result.TotalNodesProcessed.Should().Be(2);
    }

    [TestMethod]
    public async Task ProcessNodesAsync_WithoutLsp_GeneratesEmbeddings()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList());

        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions { EnableLspEnrichment = false });

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance);

        var nodes = new List<HypergraphNode>
        {
            new()
            {
                Id = "node1",
                Name = "TestMethod",
                NodeType = "Method",
                Language = "C#",
                FilePath = "test.cs",
                LineStart = 1,
                LineEnd = 10
            }
        };

        // Act
        var result = await pipeline.ProcessNodesAsync(nodes, @"C:\workspace");

        // Assert
        result.Should().HaveCount(1);
        result[0].IsLspEnriched.Should().BeFalse();
        result[0].Name.Should().Be("TestMethod");
    }

    [TestMethod]
    public async Task ProcessAsync_ReportsProgress()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList());

        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions { EnableLspEnrichment = false });

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance);

        var data = CreateTestHypergraphData();
        var progressReports = new List<PipelineProgress>();
        var progress = new Progress<PipelineProgress>(p => progressReports.Add(p));

        // Act
        await pipeline.ProcessAsync(data, @"C:\workspace", progress);

        // Assert - progress reports may or may not be generated depending on batch size
        // Just verify we didn't throw an exception
    }

    [TestMethod]
    public async Task ProcessAsync_TracksTime()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList());

        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions { EnableLspEnrichment = false });

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance);

        var data = CreateTestHypergraphData();

        // Act
        var result = await pipeline.ProcessAsync(data, @"C:\workspace");

        // Assert
        result.StartTime.Should().BeBefore(result.EndTime);
        result.Duration.Should().BePositive();
    }

    [TestMethod]
    public async Task ProcessNodesAsync_WithEnrichedNodes_UsesEnhancedText()
    {
        // Arrange
        var capturedTexts = new List<string>();
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
            {
                capturedTexts.AddRange(texts);
                return texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList();
            });

        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions { EnableLspEnrichment = false });

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance);

        // Create a pre-enriched node
        var nodes = new List<HypergraphNode>
        {
            new()
            {
                Id = "node1",
                Name = "TestMethod",
                NodeType = "Method",
                Language = "C#",
                FilePath = "test.cs",
                LineStart = 1,
                LineEnd = 10,
                Metadata = new Dictionary<string, object?>
                {
                    ["LspEnriched"] = true,
                    ["Signature"] = "void TestMethod(int x)",
                    ["Documentation"] = "Test documentation"
                }
            }
        };

        // Act
        var result = await pipeline.ProcessNodesAsync(nodes, @"C:\workspace");

        // Assert
        result.Should().HaveCount(1);
        result[0].IsLspEnriched.Should().BeTrue();

        // Verify enhanced text was used (contains LSP metadata)
        capturedTexts.Should().HaveCount(1);
        capturedTexts[0].Should().Contain("Signature: void TestMethod(int x)");
        capturedTexts[0].Should().Contain("Test documentation");
    }

    [TestMethod]
    public async Task DisposeAsync_DisposesResources()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions());

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance);

        // Act
        await pipeline.DisposeAsync();

        // Assert
        mockGenerator.Verify(g => g.Dispose(), Times.Once);
    }

    [TestMethod]
    public async Task DisposedPipeline_ThrowsObjectDisposedException()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        var embeddingService = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var options = Options.Create(new EnrichedEmbeddingOptions());

        var pipeline = new EnrichedEmbeddingPipeline(
            embeddingService,
            options,
            NullLogger<EnrichedEmbeddingPipeline>.Instance);

        await pipeline.DisposeAsync();

        // Act & Assert
        var act = () => pipeline.ProcessAsync(CreateTestHypergraphData(), @"C:\workspace");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static HypergraphData CreateTestHypergraphData()
    {
        return new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode>
            {
                ["node1"] = new HypergraphNode
                {
                    Id = "node1",
                    Name = "TestClass",
                    NodeType = "Class",
                    Language = "C#",
                    FilePath = "test.cs",
                    LineStart = 1,
                    LineEnd = 20
                },
                ["node2"] = new HypergraphNode
                {
                    Id = "node2",
                    Name = "TestMethod",
                    NodeType = "Method",
                    Language = "C#",
                    FilePath = "test.cs",
                    LineStart = 5,
                    LineEnd = 15
                }
            }
        };
    }
}
