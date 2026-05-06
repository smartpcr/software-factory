// -----------------------------------------------------------------------
// <copyright file="NullSemanticReIndexer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using Ast.Core;
using Ast.Memory.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// No-op implementation of <see cref="ISemanticReIndexer"/> used when no
/// embedding generator is available. Logs a debug message indicating that
/// re-indexing was requested but skipped. This ensures the semantic
/// re-indexing code path in <see cref="IncrementalSync"/> is always
/// exercised (architecture.md §3.6 step 9) rather than being silently
/// skipped when the dependency is null.
/// </summary>
public sealed class NullSemanticReIndexer : ISemanticReIndexer
{
    private readonly ILogger<NullSemanticReIndexer> logger;

    public NullSemanticReIndexer(ILogger<NullSemanticReIndexer> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc/>
    public Task ReIndexAsync(IReadOnlyList<HypergraphNode> changedNodes, CancellationToken ct = default)
    {
        this.logger.LogDebug(
            "NullSemanticReIndexer: skipping re-indexing of {Count} changed nodes " +
            "(no embedding generator configured)",
            changedNodes.Count);
        return Task.CompletedTask;
    }
}