// -----------------------------------------------------------------------
// <copyright file="IMemoryRetrievalEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Ast.Memory.Models;

/// <summary>
/// Orchestrates hybrid context retrieval from the memory graph.
/// </summary>
public interface IMemoryRetrievalEngine
{
    /// <summary>
    /// Retrieves context from the memory graph based on the request parameters.
    /// Orchestrates: plan → per-mode traversal/search → rank → budget → assemble.
    /// </summary>
    Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default);
}