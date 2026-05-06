// -----------------------------------------------------------------------
// <copyright file="TokenCounter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Context;

using Microsoft.ML.Tokenizers;

/// <summary>
/// Token counting using cl100k_base tokenizer (same as GPT-4 / tiktoken).
/// Thread-safe: the underlying <see cref="Tokenizer"/> instance is immutable
/// after construction.
/// </summary>
public interface ITokenCounter
{
    /// <summary>Counts the number of tokens in the given text.</summary>
    int CountTokens(string text);
}

/// <summary>
/// Production token counter backed by Microsoft.ML.Tokenizers with cl100k_base encoding.
/// </summary>
public sealed class TiktokenCounter : ITokenCounter
{
    private readonly Tokenizer tokenizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="TiktokenCounter"/> class
    /// using the cl100k_base model (GPT-4 / ChatGPT).
    /// </summary>
    public TiktokenCounter()
    {
        this.tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");
    }

    /// <inheritdoc/>
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return this.tokenizer.CountTokens(text);
    }
}