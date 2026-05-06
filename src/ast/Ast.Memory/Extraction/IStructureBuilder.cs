// -----------------------------------------------------------------------
// <copyright file="IStructureBuilder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Memory.Models;

/// <summary>
/// Stage 1 of the extraction pipeline: builds the structural layer of the hypergraph
/// (file nodes, symbol nodes, containment edges, project/namespace hierarchy).
/// Per architecture §3.1 dependency chain.
/// </summary>
public interface IStructureBuilder
{
    Task<StageResult> BuildAsync(HypergraphData data, string repoRoot, ExtractionOptions options, CancellationToken ct = default);
}