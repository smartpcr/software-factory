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
/// Core retrieval interface for the agent memory system.
/// Given a query, returns a ranked set of graph nodes within a token budget,
/// assembled into a deterministic Markdown context for the agent's prompt.
/// </summary>
public interface IMemoryRetrievalEngine
{
    /// <summary>
    /// Retrieves memory nodes matching the request, scores them using the 5-signal
    /// weighted model, applies budget constraints, and assembles context Markdown.
    /// </summary>
    /// <param name="request">Retrieval parameters including query text, seed nodes, mode, and budget.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ranked nodes, assembled context, and diagnostics.</returns>
    Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken ct = default);
}