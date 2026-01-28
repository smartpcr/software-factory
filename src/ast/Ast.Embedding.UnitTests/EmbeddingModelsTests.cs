// -----------------------------------------------------------------------
// <copyright file="EmbeddingModelsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding.UnitTests;

[TestClass]
public class EmbeddingModelsTests
{
    [TestMethod]
    public void EmbeddingResult_RequiredProperties_AreSet()
    {
        // Arrange & Act
        var result = new EmbeddingResult
        {
            Id = "test-id",
            Text = "test text",
            Vector = [0.1f, 0.2f, 0.3f],
            Model = "test-model"
        };

        // Assert
        result.Id.Should().Be("test-id");
        result.Text.Should().Be("test text");
        result.Vector.Should().HaveCount(3);
        result.Model.Should().Be("test-model");
        result.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public void EmbeddingResult_Metadata_IsOptional()
    {
        // Arrange & Act
        var result = new EmbeddingResult
        {
            Id = "test-id",
            Text = "test text",
            Vector = [0.1f],
            Model = "test-model"
        };

        // Assert
        result.Metadata.Should().BeNull();
    }

    [TestMethod]
    public void EmbeddingResult_Metadata_CanBeSet()
    {
        // Arrange & Act
        var result = new EmbeddingResult
        {
            Id = "test-id",
            Text = "test text",
            Vector = [0.1f],
            Model = "test-model",
            Metadata = new Dictionary<string, object>
            {
                ["key1"] = "value1",
                ["key2"] = 42
            }
        };

        // Assert
        result.Metadata.Should().NotBeNull();
        result.Metadata.Should().ContainKey("key1");
        result.Metadata!["key1"].Should().Be("value1");
        result.Metadata["key2"].Should().Be(42);
    }

    [TestMethod]
    public void CodeEmbedding_RequiredProperties_AreSet()
    {
        // Arrange & Act
        var embedding = new CodeEmbedding
        {
            NodeId = "node-123",
            FilePath = "src/MyClass.cs",
            ElementType = "Method",
            Name = "DoSomething",
            SourceText = "public void DoSomething() { }",
            Vector = [0.1f, 0.2f, 0.3f, 0.4f],
            StartLine = 10,
            EndLine = 15,
            Language = "C#"
        };

        // Assert
        embedding.NodeId.Should().Be("node-123");
        embedding.FilePath.Should().Be("src/MyClass.cs");
        embedding.ElementType.Should().Be("Method");
        embedding.Name.Should().Be("DoSomething");
        embedding.SourceText.Should().Contain("DoSomething");
        embedding.Vector.Should().HaveCount(4);
        embedding.StartLine.Should().Be(10);
        embedding.EndLine.Should().Be(15);
        embedding.Language.Should().Be("C#");
    }

    [TestMethod]
    public void EmbeddingGeneratorOptions_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new EmbeddingGeneratorOptions();

        // Assert
        options.Provider.Should().Be(EmbeddingProvider.Http);
        options.Endpoint.Should().Be("http://localhost:11434");
        options.ModelName.Should().Be("nomic-embed-text");
        options.Dimensions.Should().Be(768);
        options.BatchSize.Should().Be(32);
        options.TimeoutSeconds.Should().Be(60);
        options.GpuLayerCount.Should().Be(-1);
        options.ContextSize.Should().Be(2048);
        options.UseMemoryMap.Should().BeTrue();
        options.ApiKey.Should().BeNull();
        options.ModelPath.Should().BeNull();
    }

    [TestMethod]
    [DataRow(EmbeddingProvider.LLama)]
    [DataRow(EmbeddingProvider.Http)]
    [DataRow(EmbeddingProvider.OpenAI)]
    public void EmbeddingProvider_AllValues_AreDefined(EmbeddingProvider provider)
    {
        // Assert
        Enum.IsDefined(typeof(EmbeddingProvider), provider).Should().BeTrue();
    }
}