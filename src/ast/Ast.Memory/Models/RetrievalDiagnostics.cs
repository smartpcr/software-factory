// -----------------------------------------------------------------------
// <copyright file="RetrievalDiagnostics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Concurrent;

/// <summary>
/// Per-mode candidate counts and timing from retrieval.
/// </summary>
public class RetrievalDiagnostics
{
    public ConcurrentDictionary<RetrievalMode, ModeDiagnostics> PerMode { get; init; } = new();
    public string? Error { get; set; }
    public long TotalElapsedMs { get; set; }
}

/// <summary>
/// Diagnostics for a single retrieval mode.
/// </summary>
public class ModeDiagnostics
{
    public int CandidateCount { get; set; }
    public long ElapsedMs { get; set; }
    public string? Error { get; set; }
}
