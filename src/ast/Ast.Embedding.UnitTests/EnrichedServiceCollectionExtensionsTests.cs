// -----------------------------------------------------------------------
// <copyright file="EnrichedServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding.UnitTests;

using Ast.Lsp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[TestClass]
public class EnrichedServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddEnrichedEmbeddingPipeline_RegistersAllServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddEnrichedEmbeddingPipeline();
        var provider = services.BuildServiceProvider();

        // Assert
        provider.GetService<IEmbeddingGenerator>().Should().NotBeNull();
        provider.GetService<CodeEmbeddingService>().Should().NotBeNull();
        provider.GetService<LspServerManager>().Should().NotBeNull();
        provider.GetService<LspEnrichmentService>().Should().NotBeNull();
        provider.GetService<EnrichedEmbeddingPipeline>().Should().NotBeNull();
        provider.GetService<IOptions<EnrichedEmbeddingOptions>>().Should().NotBeNull();
        provider.GetService<IOptions<EmbeddingGeneratorOptions>>().Should().NotBeNull();
        provider.GetService<IOptions<LspConfiguration>>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddEnrichedEmbeddingPipeline_AppliesEmbeddingConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddEnrichedEmbeddingPipeline(
            embedding =>
            {
                embedding.Provider = EmbeddingProvider.Http;
                embedding.Endpoint = "http://custom:8080";
                embedding.ModelName = "custom-model";
                embedding.Dimensions = 512;
            });
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<EmbeddingGeneratorOptions>>();
        options.Value.Provider.Should().Be(EmbeddingProvider.Http);
        options.Value.Endpoint.Should().Be("http://custom:8080");
        options.Value.ModelName.Should().Be("custom-model");
        options.Value.Dimensions.Should().Be(512);
    }

    [TestMethod]
    public void AddEnrichedEmbeddingPipeline_AppliesLspConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddEnrichedEmbeddingPipeline(
            configureLsp: lsp =>
            {
                lsp.Enabled = true;
                lsp.CSharp.Enabled = true;
                lsp.CSharp.ServerType = CSharpLspServerType.OmniSharp;
                lsp.PowerShell.Enabled = false;
            });
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<LspConfiguration>>();
        options.Value.Enabled.Should().BeTrue();
        options.Value.CSharp.Enabled.Should().BeTrue();
        options.Value.CSharp.ServerType.Should().Be(CSharpLspServerType.OmniSharp);
        options.Value.PowerShell.Enabled.Should().BeFalse();
    }

    [TestMethod]
    public void AddEnrichedEmbeddingPipeline_AppliesPipelineConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddEnrichedEmbeddingPipeline(
            configurePipeline: pipeline =>
            {
                pipeline.EnableLspEnrichment = true;
                pipeline.BatchSize = 50;
                pipeline.UseParallelProcessing = true;
                pipeline.MaxDegreeOfParallelism = 8;
                pipeline.ContinueOnEnrichmentErrors = false;
                pipeline.EnableCaching = true;
                pipeline.CacheDirectory = "/tmp/cache";
            });
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<EnrichedEmbeddingOptions>>();
        options.Value.EnableLspEnrichment.Should().BeTrue();
        options.Value.BatchSize.Should().Be(50);
        options.Value.UseParallelProcessing.Should().BeTrue();
        options.Value.MaxDegreeOfParallelism.Should().Be(8);
        options.Value.ContinueOnEnrichmentErrors.Should().BeFalse();
        options.Value.EnableCaching.Should().BeTrue();
        options.Value.CacheDirectory.Should().Be("/tmp/cache");
    }

    [TestMethod]
    public void AddCSharpEnrichedEmbedding_ConfiguresCSharpOnly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddCSharpEnrichedEmbedding(
            embeddingEndpoint: "http://localhost:11434",
            embeddingModel: "nomic-embed-text",
            csharpLspServerType: CSharpLspServerType.CSharpLs);
        var provider = services.BuildServiceProvider();

        // Assert
        var lspOptions = provider.GetRequiredService<IOptions<LspConfiguration>>();
        lspOptions.Value.Enabled.Should().BeTrue();
        lspOptions.Value.CSharp.Enabled.Should().BeTrue();
        lspOptions.Value.CSharp.ServerType.Should().Be(CSharpLspServerType.CSharpLs);
        lspOptions.Value.PowerShell.Enabled.Should().BeFalse();

        var embeddingOptions = provider.GetRequiredService<IOptions<EmbeddingGeneratorOptions>>();
        embeddingOptions.Value.Provider.Should().Be(EmbeddingProvider.Http);
        embeddingOptions.Value.Endpoint.Should().Be("http://localhost:11434");
        embeddingOptions.Value.ModelName.Should().Be("nomic-embed-text");
    }

    [TestMethod]
    public void AddCSharpEnrichedEmbedding_WithCustomServerPath()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddCSharpEnrichedEmbedding(
            csharpLspServerType: CSharpLspServerType.OmniSharp,
            csharpLspServerPath: "/custom/path/omnisharp");
        var provider = services.BuildServiceProvider();

        // Assert
        var lspOptions = provider.GetRequiredService<IOptions<LspConfiguration>>();
        lspOptions.Value.CSharp.ServerType.Should().Be(CSharpLspServerType.OmniSharp);
        lspOptions.Value.CSharp.ServerPath.Should().Be("/custom/path/omnisharp");
    }

    [TestMethod]
    public void AddPowerShellEnrichedEmbedding_ConfiguresPowerShellOnly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddPowerShellEnrichedEmbedding(
            embeddingEndpoint: "http://localhost:11434",
            embeddingModel: "nomic-embed-text");
        var provider = services.BuildServiceProvider();

        // Assert
        var lspOptions = provider.GetRequiredService<IOptions<LspConfiguration>>();
        lspOptions.Value.Enabled.Should().BeTrue();
        lspOptions.Value.CSharp.Enabled.Should().BeFalse();
        lspOptions.Value.PowerShell.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void AddPowerShellEnrichedEmbedding_WithCustomPaths()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddPowerShellEnrichedEmbedding(
            powerShellPath: "/usr/bin/pwsh",
            editorServicesPath: "/custom/pses");
        var provider = services.BuildServiceProvider();

        // Assert
        var lspOptions = provider.GetRequiredService<IOptions<LspConfiguration>>();
        lspOptions.Value.PowerShell.PowerShellPath.Should().Be("/usr/bin/pwsh");
        lspOptions.Value.PowerShell.EditorServicesPath.Should().Be("/custom/pses");
    }

    [TestMethod]
    public void AddFullEnrichedEmbedding_ConfiguresBothLanguages()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddFullEnrichedEmbedding(
            embeddingEndpoint: "http://localhost:11434",
            embeddingModel: "nomic-embed-text");
        var provider = services.BuildServiceProvider();

        // Assert
        var lspOptions = provider.GetRequiredService<IOptions<LspConfiguration>>();
        lspOptions.Value.Enabled.Should().BeTrue();
        lspOptions.Value.CSharp.Enabled.Should().BeTrue();
        lspOptions.Value.PowerShell.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void AddEmbeddingOnlyPipeline_DisablesLsp()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddEmbeddingOnlyPipeline();
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<EnrichedEmbeddingOptions>>();
        options.Value.EnableLspEnrichment.Should().BeFalse();

        var pipeline = provider.GetRequiredService<EnrichedEmbeddingPipeline>();
        pipeline.IsLspEnrichmentAvailable.Should().BeFalse();
    }

    [TestMethod]
    public void AddEmbeddingOnlyPipeline_ConfiguresEmbedding()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddEmbeddingOnlyPipeline(options =>
        {
            options.Provider = EmbeddingProvider.LLama;
            options.ModelPath = "/models/test.gguf";
            options.Dimensions = 1024;
        });
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<EmbeddingGeneratorOptions>>();
        options.Value.Provider.Should().Be(EmbeddingProvider.LLama);
        options.Value.ModelPath.Should().Be("/models/test.gguf");
        options.Value.Dimensions.Should().Be(1024);
    }

    [TestMethod]
    public void AddEnrichedEmbeddingPipeline_RegistersAsSingletons()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEnrichedEmbeddingPipeline();
        var provider = services.BuildServiceProvider();

        // Act
        var pipeline1 = provider.GetRequiredService<EnrichedEmbeddingPipeline>();
        var pipeline2 = provider.GetRequiredService<EnrichedEmbeddingPipeline>();
        var embeddingService1 = provider.GetRequiredService<CodeEmbeddingService>();
        var embeddingService2 = provider.GetRequiredService<CodeEmbeddingService>();
        var lspService1 = provider.GetRequiredService<LspEnrichmentService>();
        var lspService2 = provider.GetRequiredService<LspEnrichmentService>();

        // Assert
        pipeline1.Should().BeSameAs(pipeline2);
        embeddingService1.Should().BeSameAs(embeddingService2);
        lspService1.Should().BeSameAs(lspService2);
    }
}