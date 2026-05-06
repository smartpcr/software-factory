// -----------------------------------------------------------------------
// <copyright file="IMemoryRetrievalEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// Engine for retrieving context from the memory graph.
/// </summary>
public interface IMemoryRetrievalEngine
{
    Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken ct = default);
}