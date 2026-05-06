// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// All configurable parameters from architecture.md §2.4.
/// </summary>
public class MemoryOptions
{
    public double SimilarityThreshold { get; set; } = 0.85;
    public double StaleDecayFactor { get; set; } = 0.5;
    public double MinorDecayFactor { get; set; } = 0.9;
    public double ArchiveConfidenceFloor { get; set; } = 0.1;
    public int DecayHalfLifeDays { get; set; } = 1095;
    public int TokenBudgetDefault { get; set; } = 8000;
    public int TokenBudgetCeiling { get; set; } = 32000;
    public int IncrementalSyncDebounceMs { get; set; } = 1000;
    public double CentralityBoostFactor { get; set; } = 0.5;
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;
    public string? SqlitePath { get; set; }
    public string? Neo4jUri { get; set; }
    public string? QdrantEndpoint { get; set; }
    public string? ConsolidationModelName { get; set; }

    /// <summary>
    /// File extensions watched by the file-system watcher.
    /// Default: .cs, .ps1, .psm1, .psd1
    /// </summary>
    public IReadOnlyList<string> WatchedExtensions { get; set; } = [".cs", ".ps1", ".psm1", ".psd1"];

    /// <summary>
    /// Root path of the repository to watch. Used by the hosted service
    /// (<see cref="Ast.Memory.Sync.FileWatcherHostedService"/>) to
    /// start the file-system watcher automatically.
    /// </summary>
    public string? RepoRoot { get; set; }
}
