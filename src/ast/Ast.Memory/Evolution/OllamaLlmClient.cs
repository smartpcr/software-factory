// -----------------------------------------------------------------------
// <copyright file="OllamaLlmClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// LLM client that calls the Ollama <c>/api/generate</c> endpoint
/// to produce text (topic summaries, consolidation merges).
/// Uses <see cref="MemoryOptions.ConsolidationModelName"/> for the model name
/// and <see cref="MemoryOptions.OllamaEndpoint"/> for the base URL.
/// </summary>
public sealed class OllamaLlmClient : ILlmClient
{
    private readonly HttpClient httpClient;
    private readonly MemoryOptions options;
    private readonly ILogger<OllamaLlmClient> logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OllamaLlmClient(
        HttpClient httpClient,
        IOptions<MemoryOptions> options,
        ILogger<OllamaLlmClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <inheritdoc/>
    /// <exception cref="LlmGenerationException">
    /// Thrown when the model is not configured or the Ollama endpoint fails.
    /// Callers must handle this explicitly — summaries are never silently
    /// replaced with deterministic fallbacks.
    /// </exception>
    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var modelName = this.options.ConsolidationModelName;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new LlmGenerationException(
                "ConsolidationModelName is not configured in MemoryOptions. " +
                "Topic summaries require a valid LLM model.");
        }

        var request = new OllamaGenerateRequest
        {
            Model = modelName,
            Prompt = prompt,
            Stream = false
        };

        var endpoint = this.options.OllamaEndpoint.TrimEnd('/');
        var url = $"{endpoint}/api/generate";

        try
        {
            var response = await this.httpClient.PostAsJsonAsync(url, request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonOptions, ct);
            var text = result?.Response;

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new LlmGenerationException(
                    $"Ollama returned an empty response for model '{modelName}'.");
            }

            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LlmGenerationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LlmGenerationException(
                $"Ollama generate call failed for model '{modelName}': {ex.Message}", ex);
        }
    }

    private sealed class OllamaGenerateRequest
    {
        public string Model { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public bool Stream { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        public string Response { get; set; } = string.Empty;
    }
}