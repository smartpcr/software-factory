// -----------------------------------------------------------------------
// <copyright file="IExtractionPipeline.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// Extraction pipeline for transforming source code into the memory graph.
/// See architecture.md §5.4.
/// </summary>
public interface IExtractionPipeline
{
    /// <summary>
    /// Full extraction from repo root (or scoped subtree).
    /// </summary>
    Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Incremental extraction of specific files (used by IncrementalSync).
    /// </summary>
    Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default);
}
