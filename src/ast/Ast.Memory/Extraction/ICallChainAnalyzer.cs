// -----------------------------------------------------------------------
// <copyright file="ICallChainAnalyzer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Memory.Models;

/// <summary>
/// Stage 2 of the extraction pipeline: analyzes call chains and method invocations
/// to build CALLS edges. Requires Stage 1 nodes to resolve endpoints.
/// Per architecture §3.1 dependency chain.
/// </summary>
public interface ICallChainAnalyzer
{
    Task<StageResult> AnalyzeAsync(HypergraphData data, string repoRoot, ExtractionOptions options, CancellationToken ct = default);
}