// -----------------------------------------------------------------------
// <copyright file="ILlmClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

/// <summary>
/// Abstraction over the LLM endpoint used for topic summarization
/// and expertise consolidation. Enables testing without a live Ollama instance.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Generates text from a prompt using the configured model.
    /// </summary>
    /// <param name="prompt">The prompt to send to the LLM.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated text response.</returns>
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
}