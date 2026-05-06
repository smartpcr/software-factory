// -----------------------------------------------------------------------
// <copyright file="FakeLlmClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;

/// <summary>
/// Fake LLM client that returns deterministic LLM-style summaries for testing.
/// Supports configurable failure mode to verify exception propagation.
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private int callCount;

    public int CallCount => this.callCount;

    /// <summary>
    /// When true, <see cref="GenerateAsync"/> throws <see cref="LlmGenerationException"/>
    /// to simulate Ollama/LLM endpoint failures.
    /// </summary>
    public bool ShouldFail { get; set; }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        Interlocked.Increment(ref this.callCount);

        if (this.ShouldFail)
        {
            throw new LlmGenerationException(
                "Simulated LLM failure for testing.");
        }

        // Return a deterministic non-empty summary based on the prompt length
        var summary = $"LLM-generated summary for cluster (prompt length: {prompt.Length})";
        return Task.FromResult(summary);
    }
}