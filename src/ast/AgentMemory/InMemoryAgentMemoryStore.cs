// -----------------------------------------------------------------------
// <copyright file="InMemoryAgentMemoryStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

using System.Collections.Concurrent;

/// <summary>
/// In-memory implementation of <see cref="IAgentMemoryStore"/>.
/// Suitable for testing and single-session use. Does not persist
/// across process restarts.
/// </summary>
public class InMemoryAgentMemoryStore : IAgentMemoryStore
{
    private readonly ConcurrentDictionary<string, Episode> episodes = new();
    private readonly ConcurrentDictionary<string, ExpertiseRecord> expertise = new();
    private readonly ConcurrentDictionary<string, MemoryEdge> edges = new();

    /// <inheritdoc/>
    public Task RecordEpisodeAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        this.episodes[episode.Id] = episode;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Episode?> GetEpisodeAsync(string id, CancellationToken cancellationToken = default)
    {
        this.episodes.TryGetValue(id, out var episode);
        return Task.FromResult(episode);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Episode>> GetEpisodesAsync(EpisodeFilter filter, CancellationToken cancellationToken = default)
    {
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

        var results = query
            .OrderByDescending(e => e.Timestamp)
            .Take(filter.Limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<Episode>>(results);
    }

    /// <inheritdoc/>
    public Task UpdateEpisodeAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        this.episodes[episode.Id] = episode;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteEpisodeAsync(string id, CancellationToken cancellationToken = default)
    {
        this.episodes.TryRemove(id, out _);

        // Remove associated edges
        var edgesToRemove = this.edges.Values
            .Where(e => e.SourceId == id || e.TargetId == id)
            .Select(e => e.Id)
            .ToList();

        foreach (var edgeId in edgesToRemove)
        {
            this.edges.TryRemove(edgeId, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpsertExpertiseAsync(ExpertiseRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        this.expertise[record.Id] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ExpertiseRecord?> GetExpertiseAsync(string id, CancellationToken cancellationToken = default)
    {
        this.expertise.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseForSubjectAsync(
        string subject,
        CancellationToken cancellationToken = default)
    {
        var results = this.expertise.Values
            .Where(e => string.Equals(e.Subject, subject, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Confidence)
            .ToList();

        return Task.FromResult<IReadOnlyList<ExpertiseRecord>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseByCategoryAsync(
        ExpertiseCategory category,
        double minConfidence = 0.0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var results = this.expertise.Values
            .Where(e => e.Category == category && e.Confidence >= minConfidence)
            .OrderByDescending(e => e.Confidence)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<ExpertiseRecord>>(results);
    }

    /// <inheritdoc/>
    public Task DeleteExpertiseAsync(string id, CancellationToken cancellationToken = default)
    {
        this.expertise.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AddMemoryEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        this.edges[edge.Id] = edge;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MemoryEdge>> GetOutgoingEdgesAsync(
        string sourceId,
        MemoryEdgeType? edgeType = null,
        CancellationToken cancellationToken = default)
    {
        var query = this.edges.Values.Where(e => e.SourceId == sourceId);

        if (edgeType is not null)
        {
            query = query.Where(e => e.EdgeType == edgeType.Value);
        }

        return Task.FromResult<IReadOnlyList<MemoryEdge>>(query.ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MemoryEdge>> GetIncomingEdgesAsync(
        string targetId,
        MemoryEdgeType? edgeType = null,
        CancellationToken cancellationToken = default)
    {
        var query = this.edges.Values.Where(e => e.TargetId == targetId);

        if (edgeType is not null)
        {
            query = query.Where(e => e.EdgeType == edgeType.Value);
        }

        return Task.FromResult<IReadOnlyList<MemoryEdge>>(query.ToList());
    }

    /// <inheritdoc/>
    public Task DeleteMemoryEdgeAsync(string id, CancellationToken cancellationToken = default)
    {
        this.edges.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<MemoryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new MemoryStatistics
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

        return Task.FromResult(stats);
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        this.episodes.Clear();
        this.expertise.Clear();
        this.edges.Clear();
        return Task.CompletedTask;
    }
}