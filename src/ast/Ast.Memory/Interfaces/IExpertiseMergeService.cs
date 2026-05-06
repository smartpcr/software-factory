// -----------------------------------------------------------------------
// <copyright file="IExpertiseMergeService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// LLM-powered merge of semantically similar expertise entries.
/// Given two entries with high cosine similarity, produces a merged
/// Title + Body that subsumes both originals.
/// </summary>
public interface IExpertiseMergeService
{
    /// <summary>
    /// Merges two expertise entries into a single combined Title and Body.
    /// The implementation is expected to use an LLM to produce a coherent merge.
    /// </summary>
    Task<(string MergedTitle, string MergedBody)> MergeExpertiseAsync(
        ExpertiseEntry entryA,
        ExpertiseEntry entryB,
        CancellationToken ct = default);
}