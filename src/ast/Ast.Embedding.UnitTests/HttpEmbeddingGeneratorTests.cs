// -----------------------------------------------------------------------
// <copyright file="HttpEmbeddingGeneratorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding.UnitTests;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

[TestClass]
public class HttpEmbeddingGeneratorTests
{
    [TestMethod]
    public void Constructor_SetsProperties()
    {
        // Arrange
        var options = Options.Create(new EmbeddingGeneratorOptions
        {
            Provider = EmbeddingProvider.Http,
            Endpoint = "http://test:11434",
            ModelName = "test-model",
            Dimensions = 512
        });

        // Act
        using var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance);

        // Assert
        generator.Dimensions.Should().Be(512);
        generator.ModelName.Should().Be("test-model");
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_EmptyText_ReturnsZeroVector()
    {
        // Arrange
        var options = Options.Create(new EmbeddingGeneratorOptions
        {
            Dimensions = 3
        });

        using var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance);

        // Act
        var result = await generator.GenerateEmbeddingAsync("");

        // Assert
        result.Should().HaveCount(3);
        result.Should().AllBeEquivalentTo(0f);
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_WhitespaceText_ReturnsZeroVector()
    {
        // Arrange
        var options = Options.Create(new EmbeddingGeneratorOptions
        {
            Dimensions = 4
        });

        using var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance);

        // Act
        var result = await generator.GenerateEmbeddingAsync("   ");

        // Assert
        result.Should().HaveCount(4);
        result.Should().AllBeEquivalentTo(0f);
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_OllamaProvider_SendsCorrectRequest()
    {
        // Arrange
        var expectedEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
        var responseContent = JsonSerializer.Serialize(new { embedding = expectedEmbedding });

        var mockHandler = CreateMockHandler(responseContent, HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler.Object);

        var options = Options.Create(new EmbeddingGeneratorOptions
        {
            Provider = EmbeddingProvider.Http,
            Endpoint = "http://test:11434",
            ModelName = "nomic-embed-text",
            Dimensions = 3
        });

        using var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance,
            httpClient);

        // Act
        var result = await generator.GenerateEmbeddingAsync("test text");

        // Assert
        result.Should().BeEquivalentTo(expectedEmbedding);

        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Method == HttpMethod.Post &&
                r.RequestUri!.ToString().Contains("/api/embeddings")),
            ItExpr.IsAny<CancellationToken>());
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_OpenAIProvider_SendsCorrectRequest()
    {
        // Arrange
        var expectedEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
        var responseContent = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { embedding = expectedEmbedding, index = 0 }
            }
        });

        var mockHandler = CreateMockHandler(responseContent, HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler.Object);

        var options = Options.Create(new EmbeddingGeneratorOptions
        {
            Provider = EmbeddingProvider.OpenAI,
            Endpoint = "http://test:8080",
            ModelName = "text-embedding-3-small",
            Dimensions = 3
        });

        using var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance,
            httpClient);

        // Act
        var result = await generator.GenerateEmbeddingAsync("test text");

        // Assert
        result.Should().BeEquivalentTo(expectedEmbedding);

        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Method == HttpMethod.Post &&
                r.RequestUri!.ToString().Contains("/v1/embeddings")),
            ItExpr.IsAny<CancellationToken>());
    }

    [TestMethod]
    public async Task GenerateBatchEmbeddingsAsync_ProcessesInBatches()
    {
        // Arrange
        var embedding1 = new float[] { 0.1f, 0.2f };
        var embedding2 = new float[] { 0.3f, 0.4f };
        var embedding3 = new float[] { 0.5f, 0.6f };

        var callCount = 0;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var embedding = callCount++ switch
                {
                    0 => embedding1,
                    1 => embedding2,
                    _ => embedding3
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { embedding }))
                };
            });

        var httpClient = new HttpClient(mockHandler.Object);

        var options = Options.Create(new EmbeddingGeneratorOptions
        {
            Provider = EmbeddingProvider.Http,
            Endpoint = "http://test:11434",
            Dimensions = 2,
            BatchSize = 2  // Process 2 at a time
        });

        using var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance,
            httpClient);

        var texts = new[] { "text1", "text2", "text3" };

        // Act
        var results = await generator.GenerateBatchEmbeddingsAsync(texts);

        // Assert
        results.Should().HaveCount(3);
        results[0].Should().BeEquivalentTo(embedding1);
        results[1].Should().BeEquivalentTo(embedding2);
        results[2].Should().BeEquivalentTo(embedding3);
    }

    [TestMethod]
    public async Task IsAvailableAsync_EndpointReachable_ReturnsTrue()
    {
        // Arrange
        var mockHandler = CreateMockHandler("{}", HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler.Object);

        var options = Options.Create(new EmbeddingGeneratorOptions
        {
            Endpoint = "http://test:11434"
        });

        using var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance,
            httpClient);

        // Act
        var result = await generator.IsAvailableAsync();

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public async Task IsAvailableAsync_EndpointUnreachable_ReturnsFalse()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var httpClient = new HttpClient(mockHandler.Object);

        var options = Options.Create(new EmbeddingGeneratorOptions
        {
            Endpoint = "http://test:11434"
        });

        using var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance,
            httpClient);

        // Act
        var result = await generator.IsAvailableAsync();

        // Assert
        result.Should().BeFalse();
    }

    [TestMethod]
    public void Dispose_DisposesHttpClient()
    {
        // Arrange
        var options = Options.Create(new EmbeddingGeneratorOptions());
        var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance);

        // Act & Assert - should not throw
        generator.Dispose();
        generator.Dispose(); // Double dispose should be safe
    }

    [TestMethod]
    public async Task DisposedGenerator_ThrowsObjectDisposedException()
    {
        // Arrange
        var options = Options.Create(new EmbeddingGeneratorOptions());
        var generator = new HttpEmbeddingGenerator(
            options,
            NullLogger<HttpEmbeddingGenerator>.Instance);

        generator.Dispose();

        // Act & Assert
        var act = () => generator.GenerateEmbeddingAsync("test");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static Mock<HttpMessageHandler> CreateMockHandler(string content, HttpStatusCode statusCode)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        return mockHandler;
    }
}
