// -----------------------------------------------------------------------
// <copyright file="RetrievalResult.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Result from a memory retrieval operation.
/// </summary>
public record RetrievalResult(
    IReadOnlyList<ScoredMemoryNode> Nodes,
    string ContextMarkdown,
    int TokensUsed,
    IReadOnlyList<RetrievalMode> ModesExecuted,
    IReadOnlyList<string> Diagnostics);