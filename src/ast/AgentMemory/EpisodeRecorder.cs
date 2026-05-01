// -----------------------------------------------------------------------
// <copyright file="EpisodeRecorder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// Creates <see cref="EpisodeBuilder"/> instances for recording agent episodes.
/// </summary>
public interface IEpisodeRecorder
{
    /// <summary>
    /// Starts a new episode. The episode is persisted immediately in
    /// <see cref="EpisodeOutcome.InProgress"/> state for crash safety.
    /// </summary>
    Task<EpisodeBuilder> StartEpisodeAsync(
        string agentId,
        string taskDescription,
        EpisodeType type,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IEpisodeRecorder"/>.
/// </summary>
public class EpisodeRecorder : IEpisodeRecorder
{
    private readonly IAgentMemoryStore store;

    public EpisodeRecorder(IAgentMemoryStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc/>
    public async Task<EpisodeBuilder> StartEpisodeAsync(
        string agentId,
        string taskDescription,
        EpisodeType type,
        CancellationToken cancellationToken = default)
    {
        var episode = new Episode
        {
            Id = $"ep-{Guid.NewGuid():N}",
            AgentId = agentId,
            TaskDescription = taskDescription,
            Type = type,
            Outcome = EpisodeOutcome.InProgress,
            Timestamp = DateTime.UtcNow
        };

        // Persist immediately for crash safety
        await this.store.RecordEpisodeAsync(episode, cancellationToken);

        return new EpisodeBuilder(episode, this.store);
    }
}

/// <summary>
/// Fluent builder for accumulating episode data during an agent session.
/// Not thread-safe — intended for single-threaded use within one agent task.
/// The episode is persisted at creation time (InProgress) and finalized
/// via <see cref="CompleteAsync"/>, <see cref="FailAsync"/>, or <see cref="AbandonAsync"/>.
/// </summary>
public class EpisodeBuilder
{
    private readonly IAgentMemoryStore store;
    private readonly Episode episode;
    private readonly DateTime startTime;
    private bool finalized;

    internal EpisodeBuilder(Episode episode, IAgentMemoryStore store)
    {
        this.episode = episode;
        this.store = store;
        this.startTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the episode ID for external reference.
    /// </summary>
    public string EpisodeId => this.episode.Id;

    /// <summary>
    /// Records a file that was touched during this episode.
    /// </summary>
    public EpisodeBuilder TouchedFile(string path, TouchType touchType = TouchType.Read)
    {
        this.ThrowIfFinalized();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var normalized = path.Replace('\\', '/');
        if (!this.episode.TouchedFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            this.episode.TouchedFiles.Add(normalized);
        }

        return this;
    }

    /// <summary>
    /// Records a code symbol that was touched during this episode.
    /// </summary>
    public EpisodeBuilder TouchedSymbol(string symbolId)
    {
        this.ThrowIfFinalized();
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);

        if (!this.episode.TouchedSymbolIds.Contains(symbolId))
        {
            this.episode.TouchedSymbolIds.Add(symbolId);
        }

        return this;
    }

    /// <summary>
    /// Adds an observation made during this episode.
    /// </summary>
    public EpisodeBuilder WithObservation(string category, string content, string? source = null)
    {
        this.ThrowIfFinalized();
        this.episode.Observations.Add(new EvidenceItem
        {
            Category = category,
            Content = content,
            Source = source
        });
        return this;
    }

    /// <summary>
    /// Adds supporting evidence collected during this episode.
    /// </summary>
    public EpisodeBuilder WithEvidence(string category, string content, string? source = null)
    {
        this.ThrowIfFinalized();
        this.episode.Evidence.Add(new EvidenceItem
        {
            Category = category,
            Content = content,
            Source = source
        });
        return this;
    }

    /// <summary>
    /// Adds a categorization tag.
    /// </summary>
    public EpisodeBuilder WithTag(string tag)
    {
        this.ThrowIfFinalized();
        if (!this.episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            this.episode.Tags.Add(tag);
        }

        return this;
    }

    /// <summary>
    /// Sets the git commit SHA at the time of the episode.
    /// </summary>
    public EpisodeBuilder WithCommitSha(string sha)
    {
        this.ThrowIfFinalized();
        // Episode.CommitSha is init-only, but we set it via reflection-free approach:
        // We store commit info in the episode's evidence for now and apply at finalize.
        this.episode.Evidence.Add(new EvidenceItem
        {
            Category = "commit_sha",
            Content = sha,
            Source = "git"
        });
        return this;
    }

    /// <summary>
    /// Sets the git branch at the time of the episode.
    /// </summary>
    public EpisodeBuilder WithBranch(string branch)
    {
        this.ThrowIfFinalized();
        this.episode.Evidence.Add(new EvidenceItem
        {
            Category = "branch",
            Content = branch,
            Source = "git"
        });
        return this;
    }

    /// <summary>
    /// Completes the episode with a successful or partial outcome.
    /// Creates memory edges linking the episode to touched files and symbols.
    /// Idempotent — calling multiple times has no effect after the first call.
    /// </summary>
    public async Task<Episode> CompleteAsync(
        EpisodeOutcome outcome,
        string summary,
        CancellationToken cancellationToken = default)
    {
        if (this.finalized)
        {
            return this.episode;
        }

        this.finalized = true;
        this.episode.Outcome = outcome;
        this.episode.Summary = summary;
        this.episode.Duration = DateTime.UtcNow - this.startTime;

        await this.store.UpdateEpisodeAsync(this.episode, cancellationToken);
        await this.CreateEdgesAsync(cancellationToken);

        return this.episode;
    }

    /// <summary>
    /// Completes the episode as failed.
    /// </summary>
    public Task<Episode> FailAsync(string summary, CancellationToken cancellationToken = default)
    {
        return this.CompleteAsync(EpisodeOutcome.Failed, summary, cancellationToken);
    }

    /// <summary>
    /// Completes the episode as abandoned.
    /// </summary>
    public Task<Episode> AbandonAsync(string reason, CancellationToken cancellationToken = default)
    {
        return this.CompleteAsync(EpisodeOutcome.Abandoned, reason, cancellationToken);
    }

    private async Task CreateEdgesAsync(CancellationToken cancellationToken)
    {
        foreach (var file in this.episode.TouchedFiles)
        {
            await this.store.AddMemoryEdgeAsync(
                new MemoryEdge
                {
                    Id = $"edge-{Guid.NewGuid():N}",
                    EdgeType = MemoryEdgeType.EpisodeTouchedFile,
                    SourceId = this.episode.Id,
                    TargetId = file,
                    TouchType = TouchType.Read
                },
                cancellationToken);
        }

        foreach (var symbolId in this.episode.TouchedSymbolIds)
        {
            await this.store.AddMemoryEdgeAsync(
                new MemoryEdge
                {
                    Id = $"edge-{Guid.NewGuid():N}",
                    EdgeType = MemoryEdgeType.EpisodeTouchedSymbol,
                    SourceId = this.episode.Id,
                    TargetId = symbolId,
                    TouchType = TouchType.Read
                },
                cancellationToken);
        }
    }

    private void ThrowIfFinalized()
    {
        if (this.finalized)
        {
            throw new InvalidOperationException(
                $"Episode {this.episode.Id} has already been finalized. Cannot modify a completed episode.");
        }
    }
}
