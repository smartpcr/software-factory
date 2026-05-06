// -----------------------------------------------------------------------
// <copyright file="IExtractionPipeline.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// Pipeline for extracting code structure into the memory graph.
/// </summary>
public interface IExtractionPipeline
{
    Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default);

    Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default);
}