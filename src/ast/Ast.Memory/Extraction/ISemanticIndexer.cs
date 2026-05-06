// -----------------------------------------------------------------------
// <copyright file="ISemanticIndexer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Memory.Models;

/// <summary>
/// Stage 3 of the extraction pipeline: generates embeddings and creates SIMILAR_TO edges
/// between semantically similar nodes. Requires all nodes from Stages 1–2.
/// Per architecture §3.1 dependency chain.
/// </summary>
public interface ISemanticIndexer
{
    Task<StageResult> IndexAsync(HypergraphData data, string repoRoot, ExtractionOptions options, CancellationToken ct = default);
}