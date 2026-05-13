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
/// Hybrid retrieval engine for querying the memory graph.
/// </summary>
public interface IMemoryRetrievalEngine
{
    Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken ct = default);
}