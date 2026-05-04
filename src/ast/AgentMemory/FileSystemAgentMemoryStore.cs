// -----------------------------------------------------------------------
// <copyright file="FileSystemAgentMemoryStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// File-system-backed implementation of <see cref="IAgentMemoryStore"/>,
/// <see cref="ITieredExpertiseStore"/>, and <see cref="ICallChainStore"/>.
/// Persists memory to JSON files so agents retain knowledge across sessions.
/// Loads lazily on first access and saves on each write operation.
/// </summary>
public class FileSystemAgentMemoryStore : IAgentMemoryStore, ITieredExpertiseStore, ICallChainStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string basePath;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private ConcurrentDictionary<string, Episode> episodes = new();
    private ConcurrentDictionary<string, ExpertiseRecord> expertise = new();
    private ConcurrentDictionary<string, MemoryEdge> edges = new();
    private ConcurrentDictionary<string, CallChain> callChains = new();
    private bool loaded;

    /// <summary>
    /// Creates a file-system-backed memory store at the specified path.
    /// </summary>
    /// <param name="basePath">Directory where memory files are stored.</param>
    public FileSystemAgentMemoryStore(string basePath)
    {
        this.basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        Directory.CreateDirectory(basePath);
    }

    private string EpisodesPath => Path.Combine(this.basePath, "episodes.json");
    private string ExpertisePath => Path.Combine(this.basePath, "expertise.json");
    private string EdgesPath => Path.Combine(this.basePath, "edges.json");
    private string CallChainsPath => Path.Combine(this.basePath, "callchains.json");

    // ── Episodes ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task RecordEpisodeAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        await this.EnsureLoadedAsync(cancellationToken);
        this.episodes[episode.Id] = episode;
        await this.SaveEpisodesAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Episode?> GetEpisodeAsync(string id, CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        this.episodes.TryGetValue(id, out var episode);
        return episode;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(EpisodeFilter filter, CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        var query = this.episodes.Values.AsEnumerable();

        if (filter.AgentId is not null)
        {
            query = query.Where(e => e.AgentId == filter.AgentId);
        }

        if (filter.Type is not null)
        {
            query = query.Where(e => e.Type == filter.Type.Value);
        }

        if (filter.Outcome is not null)
        {
            query = query.Where(e => e.Outcome == filter.Outcome.Value);
        }

        if (filter.TouchedFiles is { Count: > 0 })
        {
            var files = new HashSet<string>(filter.TouchedFiles, StringComparer.OrdinalIgnoreCase);
            query = query.Where(e => e.TouchedFiles.Any(f => files.Contains(f)));
        }

        if (filter.TouchedSymbolIds is { Count: > 0 })
        {
            var symbols = new HashSet<string>(filter.TouchedSymbolIds);
            query = query.Where(e => e.TouchedSymbolIds.Any(s => symbols.Contains(s)));
        }

        if (filter.Tags is { Count: > 0 })
        {
            var tags = new HashSet<string>(filter.Tags, StringComparer.OrdinalIgnoreCase);
            query = query.Where(e => e.Tags.Any(t => tags.Contains(t)));
        }

        if (filter.After is not null)
        {
            query = query.Where(e => e.Timestamp >= filter.After.Value);
        }

        if (filter.Before is not null)
        {
            query = query.Where(e => e.Timestamp <= filter.Before.Value);
        }

        return query
            .OrderByDescending(e => e.Timestamp)
            .Take(filter.Limit)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task UpdateEpisodeAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        await this.EnsureLoadedAsync(cancellationToken);
        this.episodes[episode.Id] = episode;
        await this.SaveEpisodesAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteEpisodeAsync(string id, CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        this.episodes.TryRemove(id, out _);

        var edgesToRemove = this.edges.Values
            .Where(e => e.SourceId == id || e.TargetId == id)
            .Select(e => e.Id)
            .ToList();

        foreach (var edgeId in edgesToRemove)
        {
            this.edges.TryRemove(edgeId, out _);
        }

        await this.SaveEpisodesAsync(cancellationToken);
        await this.SaveEdgesAsync(cancellationToken);
    }

    // ── Expertise ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task UpsertExpertiseAsync(ExpertiseRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await this.EnsureLoadedAsync(cancellationToken);
        this.expertise[record.Id] = record;
        await this.SaveExpertiseAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ExpertiseRecord?> GetExpertiseAsync(string id, CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        this.expertise.TryGetValue(id, out var record);
        return record;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseForSubjectAsync(
        string subject,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        return this.expertise.Values
            .Where(e => string.Equals(e.Subject, subject, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Confidence)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseByCategoryAsync(
        ExpertiseCategory category,
        double minConfidence = 0.0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        return this.expertise.Values
            .Where(e => e.Category == category && e.Confidence >= minConfidence)
            .OrderByDescending(e => e.Confidence)
            .Take(limit)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task DeleteExpertiseAsync(string id, CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        this.expertise.TryRemove(id, out _);

        var edgesToRemove = this.edges.Values
            .Where(e => e.SourceId == id || e.TargetId == id)
            .Select(e => e.Id)
            .ToList();

        foreach (var edgeId in edgesToRemove)
        {
            this.edges.TryRemove(edgeId, out _);
        }

        await this.SaveExpertiseAsync(cancellationToken);
        await this.SaveEdgesAsync(cancellationToken);
    }

    // ── Memory Edges ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task AddMemoryEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        await this.EnsureLoadedAsync(cancellationToken);
        this.edges[edge.Id] = edge;
        await this.SaveEdgesAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemoryEdge>> GetOutgoingEdgesAsync(
        string sourceId,
        MemoryEdgeType? edgeType = null,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        var query = this.edges.Values.Where(e => e.SourceId == sourceId);

        if (edgeType is not null)
        {
            query = query.Where(e => e.EdgeType == edgeType.Value);
        }

        return query.ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemoryEdge>> GetIncomingEdgesAsync(
        string targetId,
        MemoryEdgeType? edgeType = null,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        var query = this.edges.Values.Where(e => e.TargetId == targetId);

        if (edgeType is not null)
        {
            query = query.Where(e => e.EdgeType == edgeType.Value);
        }

        return query.ToList();
    }

    /// <inheritdoc/>
    public async Task DeleteMemoryEdgeAsync(string id, CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        this.edges.TryRemove(id, out _);
        await this.SaveEdgesAsync(cancellationToken);
    }

    // ── Bulk / Stats ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<MemoryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        return new MemoryStatistics
        {
            EpisodeCount = this.episodes.Count,
            ExpertiseCount = this.expertise.Count,
            EdgeCount = this.edges.Count,
            EpisodesByType = this.episodes.Values
                .GroupBy(e => e.Type)
                .ToDictionary(g => g.Key, g => g.Count()),
            EpisodesByOutcome = this.episodes.Values
                .GroupBy(e => e.Outcome)
                .ToDictionary(g => g.Key, g => g.Count()),
            ExpertiseByCategory = this.expertise.Values
                .GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Count()),
            AverageConfidence = this.expertise.Count > 0
                ? this.expertise.Values.Average(e => e.Confidence)
                : 0.0
        };
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        this.episodes.Clear();
        this.expertise.Clear();
        this.edges.Clear();
        this.callChains.Clear();

        await this.SaveEpisodesAsync(cancellationToken);
        await this.SaveExpertiseAsync(cancellationToken);
        await this.SaveEdgesAsync(cancellationToken);
        await this.SaveCallChainsAsync(cancellationToken);
    }

    // ── ITieredExpertiseStore ─────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseByTierAsync(
        MemoryTier tier,
        double minConfidence = 0.0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        return this.expertise.Values
            .Where(e => e.Tier == tier && e.Confidence >= minConfidence)
            .OrderByDescending(e => e.Confidence)
            .Take(limit)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseByTierAndCategoryAsync(
        MemoryTier tier,
        ExpertiseCategory category,
        double minConfidence = 0.0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        return this.expertise.Values
            .Where(e => e.Tier == tier && e.Category == category && e.Confidence >= minConfidence)
            .OrderByDescending(e => e.Confidence)
            .Take(limit)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<Dictionary<MemoryTier, int>> GetTierDistributionAsync(
        CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        var distribution = this.expertise.Values
            .GroupBy(e => e.Tier)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var tier in Enum.GetValues<MemoryTier>())
        {
            distribution.TryAdd(tier, 0);
        }

        return distribution;
    }

    // ── ICallChainStore ───────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task UpsertCallChainAsync(CallChain chain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chain);
        await this.EnsureLoadedAsync(cancellationToken);
        this.callChains[chain.Id] = chain;
        await this.SaveCallChainsAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<CallChain?> GetCallChainAsync(string id, CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        this.callChains.TryGetValue(id, out var chain);
        return chain;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CallChain>> GetCallChainsForSymbolAsync(
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        return this.callChains.Values
            .Where(c => c.Steps.Any(s => s.SymbolId == symbolId) ||
                        c.EntrySymbolId == symbolId ||
                        c.TerminalSymbolId == symbolId)
            .OrderByDescending(c => c.Confidence)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CallChain>> GetCallChainsAsync(
        CallChainFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await this.EnsureLoadedAsync(cancellationToken);
        var query = this.callChains.Values.AsEnumerable();

        if (filter.ContainsSymbolId is not null)
        {
            query = query.Where(c => c.Steps.Any(s => s.SymbolId == filter.ContainsSymbolId));
        }

        if (filter.EntrySymbolId is not null)
        {
            query = query.Where(c => c.EntrySymbolId == filter.EntrySymbolId);
        }

        if (filter.PatternName is not null)
        {
            query = query.Where(c =>
                c.PatternName != null &&
                c.PatternName.Equals(filter.PatternName, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.MinConfidence > 0)
        {
            query = query.Where(c => c.Confidence >= filter.MinConfidence);
        }

        if (filter.MinObservationCount > 0)
        {
            query = query.Where(c => c.ObservationCount >= filter.MinObservationCount);
        }

        return query
            .OrderByDescending(c => c.Confidence)
            .Take(filter.Limit)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task DeleteCallChainAsync(string id, CancellationToken cancellationToken = default)
    {
        await this.EnsureLoadedAsync(cancellationToken);
        this.callChains.TryRemove(id, out _);
        await this.SaveCallChainsAsync(cancellationToken);
    }

    // ── Persistence ───────────────────────────────────────────────────

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (this.loaded)
        {
            return;
        }

        await this.writeLock.WaitAsync(cancellationToken);
        try
        {
            if (this.loaded)
            {
                return;
            }

            this.episodes = await LoadAsync<Episode>(this.EpisodesPath, cancellationToken);
            this.expertise = await LoadAsync<ExpertiseRecord>(this.ExpertisePath, cancellationToken);
            this.edges = await LoadAsync<MemoryEdge>(this.EdgesPath, cancellationToken);
            this.callChains = await LoadAsync<CallChain>(this.CallChainsPath, cancellationToken);
            this.loaded = true;
        }
        finally
        {
            this.writeLock.Release();
        }
    }

    private static async Task<ConcurrentDictionary<string, T>> LoadAsync<T>(
        string path,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return new ConcurrentDictionary<string, T>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var items = JsonSerializer.Deserialize<Dictionary<string, T>>(json, JsonOptions);
            return items is not null
                ? new ConcurrentDictionary<string, T>(items)
                : new ConcurrentDictionary<string, T>();
        }
        catch (JsonException)
        {
            // Corrupted file — start fresh rather than crash
            return new ConcurrentDictionary<string, T>();
        }
    }

    private async Task SaveEpisodesAsync(CancellationToken cancellationToken) =>
        await this.SaveAsync(this.EpisodesPath, this.episodes, cancellationToken);

    private async Task SaveExpertiseAsync(CancellationToken cancellationToken) =>
        await this.SaveAsync(this.ExpertisePath, this.expertise, cancellationToken);

    private async Task SaveEdgesAsync(CancellationToken cancellationToken) =>
        await this.SaveAsync(this.EdgesPath, this.edges, cancellationToken);

    private async Task SaveCallChainsAsync(CancellationToken cancellationToken) =>
        await this.SaveAsync(this.CallChainsPath, this.callChains, cancellationToken);

    private async Task SaveAsync<T>(
        string path,
        ConcurrentDictionary<string, T> data,
        CancellationToken cancellationToken)
    {
        await this.writeLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = new Dictionary<string, T>(data);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);

            // Write to temp file then rename for atomic writes
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            this.writeLock.Release();
        }
    }
}