// -----------------------------------------------------------------------
// <copyright file="IExtractionPipeline.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ast.Memory.Models;

/// <summary>
/// Orchestrates the extraction pipeline: StructureBuilder → CallChainAnalyzer → SemanticIndexer.
/// Per architecture.md §5.4.
/// </summary>
public interface IExtractionPipeline
{
    /// <summary>
    /// Full extraction of a repository.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Re-extract only the specified files (used by IncrementalSync).
    /// </summary>
    Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default);
}
