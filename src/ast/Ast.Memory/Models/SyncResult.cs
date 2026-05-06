// -----------------------------------------------------------------------
// <copyright file="SyncResult.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Result of an incremental sync operation. See architecture.md §5.5.
/// </summary>
public record SyncResult(
    int FilesChanged,
    int NodesAdded,
    int NodesRetired,
    int EdgesAdded,
    int EdgesRetired,
    int ExpertiseDecayed,
    TimeSpan Duration);
