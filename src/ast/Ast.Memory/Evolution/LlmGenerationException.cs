// -----------------------------------------------------------------------
// <copyright file="LlmGenerationException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

/// <summary>
/// Exception thrown when the LLM endpoint fails to generate a summary.
/// This surfaces LLM failures rather than silently substituting a
/// deterministic fallback, ensuring callers know the summary is not
/// LLM-generated.
/// </summary>
public sealed class LlmGenerationException : Exception
{
    public LlmGenerationException(string message)
        : base(message)
    {
    }

    public LlmGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}