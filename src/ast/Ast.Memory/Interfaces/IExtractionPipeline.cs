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
/// Per architecture §5.4.
/// </summary>
public interface IExtractionPipeline
{
    /// <summary>
    /// Full extraction from repo root.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Incremental extraction of specific changed files.
    /// </summary>
    Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default);
}
