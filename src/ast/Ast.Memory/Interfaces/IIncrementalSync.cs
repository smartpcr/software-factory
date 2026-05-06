// -----------------------------------------------------------------------
// <copyright file="IIncrementalSync.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Ast.Memory.Models;

/// <summary>
/// Incremental sync engine for keeping the memory graph up-to-date with VCS changes.
/// </summary>
public interface IIncrementalSync
{
    Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default);
}