// -----------------------------------------------------------------------
// <copyright file="OllamaLlmClientTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System.Net;
using System.Text;
using Ast.Memory.Evolution;
using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="OllamaLlmClient"/> verifying that
/// <see cref="MemoryOptions.ConsolidationModelName"/> and
/// <see cref="MemoryOptions.OllamaEndpoint"/> are correctly used in LLM requests,
/// and that failures propagate as <see cref="LlmGenerationException"/>.
/// </summary>
[TestClass]
public class OllamaLlmClientTests
{
    /// <summary>
    /// When <see cref="MemoryOptions.ConsolidationModelName"/> is null,
    /// <see cref="OllamaLlmClient.GenerateAsync"/> must throw
    /// <see cref="LlmGenerationException"/> mentioning the missing configuration.
    /// </summary>
    [TestMethod]
    public async Task GenerateAsync_NullModelName_ThrowsLlmGenerationException()
    {
        var options = Options.Create(new MemoryOptions { ConsolidationModelName = null });
        var client = new OllamaLlmClient(
            new HttpClient(), options, NullLogger<OllamaLlmClient>.Instance);

        var act = () => client.GenerateAsync("test prompt");

        await act.Should().ThrowAsync<LlmGenerationException>()
            .WithMessage("*ConsolidationModelName*");
    }

    /// <summary>
    /// When <see cref="MemoryOptions.ConsolidationModelName"/> is empty/whitespace,
    /// <see cref="OllamaLlmClient.GenerateAsync"/> must throw
    /// <see cref="LlmGenerationException"/>.
    /// </summary>
    [TestMethod]
    public async Task GenerateAsync_EmptyModelName_ThrowsLlmGenerationException()
    {
        var options = Options.Create(new MemoryOptions { ConsolidationModelName = "  " });
        var client = new OllamaLlmClient(
            new HttpClient(), options, NullLogger<OllamaLlmClient>.Instance);

        var act = () => client.GenerateAsync("test prompt");

        await act.Should().ThrowAsync<LlmGenerationException>()
            .WithMessage("*ConsolidationModelName*");
    }

    /// <summary>
    /// Verifies that the configured <see cref="MemoryOptions.OllamaEndpoint"/>
    /// and <see cref="MemoryOptions.ConsolidationModelName"/> are used in the
    /// HTTP request to the Ollama API.
    /// </summary>
    [TestMethod]
    public async Task GenerateAsync_UsesConfiguredEndpointAndModel()
    {
        var handler = new FakeHttpHandler("""{"response": "LLM-generated topic summary"}""");
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new MemoryOptions
        {
            ConsolidationModelName = "llama3:8b",
            OllamaEndpoint = "http://custom-ollama:11434"
        });
        var client = new OllamaLlmClient(
            httpClient, options, NullLogger<OllamaLlmClient>.Instance);

        var result = await client.GenerateAsync("Summarize this cluster");

        result.Should().Be("LLM-generated topic summary");
        handler.LastRequestUri.Should().Be("http://custom-ollama:11434/api/generate");
        handler.LastRequestBody.Should().Contain("llama3:8b");
        handler.LastRequestBody.Should().Contain("Summarize this cluster");
    }

    /// <summary>
    /// Verifies that a trailing slash on <see cref="MemoryOptions.OllamaEndpoint"/>
    /// does not result in a double-slash URL.
    /// </summary>
    [TestMethod]
    public async Task GenerateAsync_TrailingSlashEndpoint_NormalizesUrl()
    {
        var handler = new FakeHttpHandler("""{"response": "summary"}""");
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new MemoryOptions
        {
            ConsolidationModelName = "codellama",
            OllamaEndpoint = "http://localhost:11434/"
        });
        var client = new OllamaLlmClient(
            httpClient, options, NullLogger<OllamaLlmClient>.Instance);

        await client.GenerateAsync("test");

        handler.LastRequestUri.Should().Be("http://localhost:11434/api/generate");
    }

    /// <summary>
    /// When the Ollama endpoint returns an empty response body,
    /// <see cref="OllamaLlmClient.GenerateAsync"/> throws <see cref="LlmGenerationException"/>.
    /// </summary>
    [TestMethod]
    public async Task GenerateAsync_EmptyResponse_ThrowsLlmGenerationException()
    {
        var handler = new FakeHttpHandler("""{"response": ""}""");
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new MemoryOptions { ConsolidationModelName = "llama3" });
        var client = new OllamaLlmClient(
            httpClient, options, NullLogger<OllamaLlmClient>.Instance);

        var act = () => client.GenerateAsync("test prompt");

        await act.Should().ThrowAsync<LlmGenerationException>()
            .WithMessage("*empty response*");
    }

    /// <summary>
    /// When the Ollama endpoint returns a non-success HTTP status code,
    /// <see cref="OllamaLlmClient.GenerateAsync"/> throws <see cref="LlmGenerationException"/>.
    /// </summary>
    [TestMethod]
    public async Task GenerateAsync_HttpError_ThrowsLlmGenerationException()
    {
        var handler = new FakeHttpHandler(statusCode: HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new MemoryOptions { ConsolidationModelName = "llama3" });
        var client = new OllamaLlmClient(
            httpClient, options, NullLogger<OllamaLlmClient>.Instance);

        var act = () => client.GenerateAsync("test prompt");

        await act.Should().ThrowAsync<LlmGenerationException>()
            .WithMessage("*failed*");
    }

    /// <summary>
    /// When the HTTP client throws a connection exception,
    /// <see cref="OllamaLlmClient.GenerateAsync"/> wraps it in <see cref="LlmGenerationException"/>.
    /// </summary>
    [TestMethod]
    public async Task GenerateAsync_ConnectionFailure_ThrowsLlmGenerationException()
    {
        var handler = new FakeHttpHandler(throwOnSend: new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new MemoryOptions { ConsolidationModelName = "llama3" });
        var client = new OllamaLlmClient(
            httpClient, options, NullLogger<OllamaLlmClient>.Instance);

        var act = () => client.GenerateAsync("test prompt");

        await act.Should().ThrowAsync<LlmGenerationException>()
            .WithMessage("*Connection refused*");
    }

    /// <summary>
    /// Verifies that the default <see cref="MemoryOptions.OllamaEndpoint"/>
    /// points to the standard local Ollama instance.
    /// </summary>
    [TestMethod]
    public void MemoryOptions_DefaultEndpoint_IsLocalhost()
    {
        var options = new MemoryOptions();

        options.OllamaEndpoint.Should().Be("http://localhost:11434");
    }

    // ── Test infrastructure ──────────────────────────────────────────────

    /// <summary>
    /// Fake <see cref="HttpMessageHandler"/> that captures requests and returns
    /// configurable responses for testing <see cref="OllamaLlmClient"/>.
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string? responseBody;
        private readonly HttpStatusCode statusCode;
        private readonly Exception? throwOnSend;

        public FakeHttpHandler(
            string? responseBody = null,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            Exception? throwOnSend = null)
        {
            this.responseBody = responseBody;
            this.statusCode = statusCode;
            this.throwOnSend = throwOnSend;
        }

        public string? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (this.throwOnSend is not null)
            {
                throw this.throwOnSend;
            }

            this.LastRequestUri = request.RequestUri?.ToString();

            if (request.Content is not null)
            {
                this.LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(this.statusCode)
            {
                Content = this.responseBody is not null
                    ? new StringContent(this.responseBody, Encoding.UTF8, "application/json")
                    : null
            };
        }
    }
}