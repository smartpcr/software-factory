// -----------------------------------------------------------------------
// <copyright file="MemoryEvolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// Evolves agent memory over time through consolidation (episodes → expertise)
/// and decay (reducing confidence of stale knowledge).
/// Inspired by ExpeL (AAAI'24) for insight distillation and Mem0 for scalable
/// graph consolidation.
/// </summary>
public interface IMemoryEvolver
{
    /// <summary>
    /// Consolidates episodes into expertise records. Episodes touching the same
    /// files or symbols with successful outcomes are aggregated into expertise.
    /// Requires diversity gates: multiple sessions/agents/task types.
    /// </summary>
    Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies half-life decay to expertise records that haven't been accessed
    /// or validated recently. Marks stale expertise for review.
    /// </summary>
    Task<DecayResult> DecayAsync(
        DecayOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for the consolidation process.
/// </summary>
public class ConsolidationOptions
{
    /// <summary>
    /// Minimum number of successful episodes touching the same subject
    /// before consolidation occurs. Default: 3.
    /// </summary>
    public int MinEpisodeCount { get; init; } = 3;

    /// <summary>
    /// Minimum number of distinct agent IDs required for consolidation.
    /// Prevents a single agent from creating false expertise by looping. Default: 1.
    /// </summary>
    public int MinDistinctAgents { get; init; } = 1;

    /// <summary>
    /// Whether to include failed episodes as negative evidence
    /// for failure-pattern expertise. Default: true.
    /// </summary>
    public bool DeriveFailurePatterns { get; init; } = true;

    /// <summary>
    /// Minimum number of failed episodes before deriving a failure pattern. Default: 2.
    /// </summary>
    public int MinFailureEpisodes { get; init; } = 2;

    /// <summary>
    /// Maximum number of episodes to process per consolidation run. Default: 500.
    /// </summary>
    public int MaxEpisodesToProcess { get; init; } = 500;
}

/// <summary>
/// Options for the decay process.
/// </summary>
public class DecayOptions
{
    /// <summary>
    /// Half-life for confidence decay in days. After this many days
    /// without access or validation, confidence is halved. Default: 30 days.
    /// </summary>
    public double HalfLifeDays { get; init; } = 30.0;

    /// <summary>
    /// Minimum confidence below which expertise is deleted. Default: 0.05.
    /// </summary>
    public double MinConfidence { get; init; } = 0.05;

    /// <summary>
    /// Reference time for decay calculations. Defaults to UTC now.
    /// </summary>
    public DateTime? ReferenceTime { get; init; }
}

/// <summary>
/// Result of a consolidation run.
/// </summary>
public class ConsolidationResult
{
    /// <summary>Number of episodes processed.</summary>
    public int EpisodesProcessed { get; set; }

    /// <summary>Number of new expertise records created.</summary>
    public int ExpertiseCreated { get; set; }

    /// <summary>Number of existing expertise records updated.</summary>
    public int ExpertiseUpdated { get; set; }

    /// <summary>Number of failure-pattern expertise records created.</summary>
    public int FailurePatternsCreated { get; set; }

    /// <summary>IDs of created/updated expertise records.</summary>
    public List<string> AffectedExpertiseIds { get; init; } = [];
}

/// <summary>
/// Result of a decay run.
/// </summary>
public class DecayResult
{
    /// <summary>Number of expertise records whose confidence was reduced.</summary>
    public int Decayed { get; set; }

    /// <summary>Number of expertise records deleted (below minimum confidence).</summary>
    public int Deleted { get; set; }

    /// <summary>IDs of deleted expertise records.</summary>
    public List<string> DeletedIds { get; init; } = [];
}

/// <summary>
/// Default implementation of <see cref="IMemoryEvolver"/>.
/// </summary>
public class MemoryEvolver : IMemoryEvolver
{
    private readonly IAgentMemoryStore store;

    public MemoryEvolver(IAgentMemoryStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc/>
    public async Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new ConsolidationResult();

        // Get all completed episodes (not already consolidated)
        var filter = new EpisodeFilter
        {
            Limit = options.MaxEpisodesToProcess
        };
        var episodes = await this.store.GetEpisodesAsync(filter, cancellationToken);
        result.EpisodesProcessed = episodes.Count;

        // Group by touched files — each file that appears in enough episodes becomes expertise
        var fileGroups = episodes
            .Where(e => e.Outcome is EpisodeOutcome.Success or EpisodeOutcome.Partial)
            .SelectMany(e => e.TouchedFiles.Select(f => (File: f, Episode: e)))
            .GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= options.MinEpisodeCount);

        foreach (var group in fileGroups)
        {
            var groupEpisodes = group.Select(x => x.Episode).ToList();
            var distinctAgents = groupEpisodes.Select(e => e.AgentId).Distinct().Count();

            if (distinctAgents < options.MinDistinctAgents)
            {
                continue;
            }

            await this.ConsolidateFileExpertiseAsync(
                group.Key, groupEpisodes, result, cancellationToken);
        }

        // Group by touched symbols
        var symbolGroups = episodes
            .Where(e => e.Outcome is EpisodeOutcome.Success or EpisodeOutcome.Partial)
            .SelectMany(e => e.TouchedSymbolIds.Select(s => (Symbol: s, Episode: e)))
            .GroupBy(x => x.Symbol)
            .Where(g => g.Count() >= options.MinEpisodeCount);

        foreach (var group in symbolGroups)
        {
            var groupEpisodes = group.Select(x => x.Episode).ToList();
            var distinctAgents = groupEpisodes.Select(e => e.AgentId).Distinct().Count();

            if (distinctAgents < options.MinDistinctAgents)
            {
                continue;
            }

            await this.ConsolidateSymbolExpertiseAsync(
                group.Key, groupEpisodes, result, cancellationToken);
        }

        // Derive failure patterns from repeated failures
        if (options.DeriveFailurePatterns)
        {
            var failedFileGroups = episodes
                .Where(e => e.Outcome is EpisodeOutcome.Failed)
                .SelectMany(e => e.TouchedFiles.Select(f => (File: f, Episode: e)))
                .GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= options.MinFailureEpisodes);

            foreach (var group in failedFileGroups)
            {
                var groupEpisodes = group.Select(x => x.Episode).ToList();
                await this.ConsolidateFailurePatternAsync(
                    group.Key, groupEpisodes, result, cancellationToken);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<DecayResult> DecayAsync(
        DecayOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new DecayResult();
        var now = options.ReferenceTime ?? DateTime.UtcNow;

        // Get all expertise records by scanning each category
        var allExpertise = new List<ExpertiseRecord>();
        foreach (var category in Enum.GetValues<ExpertiseCategory>())
        {
            var records = await this.store.GetExpertiseByCategoryAsync(
                category, minConfidence: 0.0, limit: 10000, cancellationToken);
            allExpertise.AddRange(records);
        }

        foreach (var expertise in allExpertise)
        {
            var daysSinceValidation = (now - expertise.LastValidated).TotalDays;
            if (daysSinceValidation <= 0)
            {
                continue;
            }

            // Half-life decay: confidence *= 0.5^(days / halfLife)
            var decayFactor = Math.Pow(0.5, daysSinceValidation / options.HalfLifeDays);

            // Access-based reinforcement: slow decay for frequently accessed expertise
            var accessDamping = 1.0 + (expertise.AccessCount * 0.1);
            var effectiveDecay = 1.0 - ((1.0 - decayFactor) / accessDamping);

            var newConfidence = expertise.Confidence * effectiveDecay;

            if (newConfidence < options.MinConfidence)
            {
                await this.store.DeleteExpertiseAsync(expertise.Id, cancellationToken);
                result.Deleted++;
                result.DeletedIds.Add(expertise.Id);
            }
            else if (Math.Abs(newConfidence - expertise.Confidence) > 0.001)
            {
                expertise.Confidence = newConfidence;
                await this.store.UpsertExpertiseAsync(expertise, cancellationToken);
                result.Decayed++;
            }
        }

        return result;
    }

    private async Task ConsolidateFileExpertiseAsync(
        string filePath,
        List<Episode> episodes,
        ConsolidationResult result,
        CancellationToken cancellationToken)
    {
        var existing = await this.store.GetExpertiseForSubjectAsync(filePath, cancellationToken);
        var current = existing.FirstOrDefault(e => e.Category == ExpertiseCategory.FileExpertise);

        if (current is not null)
        {
            // Update existing expertise
            current.EpisodeCount = episodes.Count;
            current.Confidence = Math.Min(1.0, 0.3 + (episodes.Count * 0.1));
            current.LastValidated = DateTime.UtcNow;

            var newEpisodeIds = episodes.Select(e => e.Id)
                .Except(current.DerivedFromEpisodeIds)
                .ToList();
            current.DerivedFromEpisodeIds.AddRange(newEpisodeIds);

            // Aggregate evidence from new episodes
            foreach (var ep in episodes.Where(e => !current.DerivedFromEpisodeIds
                .Take(current.DerivedFromEpisodeIds.Count - newEpisodeIds.Count)
                .Contains(e.Id)))
            {
                foreach (var obs in ep.Observations)
                {
                    current.PositiveEvidence.Add(new EvidenceItem
                    {
                        Category = obs.Category,
                        Content = obs.Content,
                        Source = ep.Id
                    });
                }
            }

            current.Summary = BuildExpertiseSummary(filePath, episodes);
            await this.store.UpsertExpertiseAsync(current, cancellationToken);
            result.ExpertiseUpdated++;
            result.AffectedExpertiseIds.Add(current.Id);
        }
        else
        {
            var expertiseId = $"exp-{Guid.NewGuid():N}";
            var expertise = new ExpertiseRecord
            {
                Id = expertiseId,
                Subject = filePath,
                Category = ExpertiseCategory.FileExpertise,
                Confidence = Math.Min(1.0, 0.3 + (episodes.Count * 0.1)),
                EpisodeCount = episodes.Count,
                DerivedFromEpisodeIds = episodes.Select(e => e.Id).ToList(),
                Summary = BuildExpertiseSummary(filePath, episodes),
                PositiveEvidence = episodes
                    .SelectMany(e => e.Observations.Select(o => new EvidenceItem
                    {
                        Category = o.Category,
                        Content = o.Content,
                        Source = e.Id
                    }))
                    .ToList()
            };

            await this.store.UpsertExpertiseAsync(expertise, cancellationToken);

            // Create edges from episodes to expertise and expertise to file
            await this.store.AddMemoryEdgeAsync(
                new MemoryEdge
                {
                    Id = $"edge-{Guid.NewGuid():N}",
                    EdgeType = MemoryEdgeType.ExpertiseAboutFile,
                    SourceId = expertiseId,
                    TargetId = filePath
                },
                cancellationToken);

            foreach (var ep in episodes)
            {
                await this.store.AddMemoryEdgeAsync(
                    new MemoryEdge
                    {
                        Id = $"edge-{Guid.NewGuid():N}",
                        EdgeType = MemoryEdgeType.EpisodeProducedExpertise,
                        SourceId = ep.Id,
                        TargetId = expertiseId
                    },
                    cancellationToken);
            }

            result.ExpertiseCreated++;
            result.AffectedExpertiseIds.Add(expertiseId);
        }
    }

    private async Task ConsolidateSymbolExpertiseAsync(
        string symbolId,
        List<Episode> episodes,
        ConsolidationResult result,
        CancellationToken cancellationToken)
    {
        var existing = await this.store.GetExpertiseForSubjectAsync(symbolId, cancellationToken);
        var current = existing.FirstOrDefault(e =>
            e.Category is ExpertiseCategory.TypeExpertise or ExpertiseCategory.MethodExpertise);

        if (current is not null)
        {
            current.EpisodeCount = episodes.Count;
            current.Confidence = Math.Min(1.0, 0.3 + (episodes.Count * 0.1));
            current.LastValidated = DateTime.UtcNow;

            var newIds = episodes.Select(e => e.Id).Except(current.DerivedFromEpisodeIds).ToList();
            current.DerivedFromEpisodeIds.AddRange(newIds);
            current.Summary = BuildExpertiseSummary(symbolId, episodes);

            await this.store.UpsertExpertiseAsync(current, cancellationToken);
            result.ExpertiseUpdated++;
            result.AffectedExpertiseIds.Add(current.Id);
        }
        else
        {
            var expertiseId = $"exp-{Guid.NewGuid():N}";
            var expertise = new ExpertiseRecord
            {
                Id = expertiseId,
                Subject = symbolId,
                Category = ExpertiseCategory.TypeExpertise,
                Confidence = Math.Min(1.0, 0.3 + (episodes.Count * 0.1)),
                EpisodeCount = episodes.Count,
                DerivedFromEpisodeIds = episodes.Select(e => e.Id).ToList(),
                Summary = BuildExpertiseSummary(symbolId, episodes),
                PositiveEvidence = episodes
                    .SelectMany(e => e.Observations.Select(o => new EvidenceItem
                    {
                        Category = o.Category,
                        Content = o.Content,
                        Source = e.Id
                    }))
                    .ToList()
            };

            await this.store.UpsertExpertiseAsync(expertise, cancellationToken);

            await this.store.AddMemoryEdgeAsync(
                new MemoryEdge
                {
                    Id = $"edge-{Guid.NewGuid():N}",
                    EdgeType = MemoryEdgeType.ExpertiseAboutSymbol,
                    SourceId = expertiseId,
                    TargetId = symbolId
                },
                cancellationToken);

            result.ExpertiseCreated++;
            result.AffectedExpertiseIds.Add(expertiseId);
        }
    }

    private async Task ConsolidateFailurePatternAsync(
        string filePath,
        List<Episode> failedEpisodes,
        ConsolidationResult result,
        CancellationToken cancellationToken)
    {
        var existing = await this.store.GetExpertiseForSubjectAsync(filePath, cancellationToken);
        var current = existing.FirstOrDefault(e => e.Category == ExpertiseCategory.FailurePattern);

        if (current is not null)
        {
            current.EpisodeCount = failedEpisodes.Count;
            current.LastValidated = DateTime.UtcNow;

            var newIds = failedEpisodes.Select(e => e.Id).Except(current.DerivedFromEpisodeIds).ToList();
            current.DerivedFromEpisodeIds.AddRange(newIds);

            foreach (var ep in failedEpisodes.Where(e => newIds.Contains(e.Id)))
            {
                foreach (var ev in ep.Evidence)
                {
                    current.NegativeEvidence.Add(new EvidenceItem
                    {
                        Category = ev.Category,
                        Content = ev.Content,
                        Source = ep.Id
                    });
                }
            }

            current.Summary = $"Repeated failures in {filePath}: " +
                string.Join("; ", failedEpisodes.Select(e => e.Summary).Where(s => !string.IsNullOrEmpty(s)).Take(3));

            await this.store.UpsertExpertiseAsync(current, cancellationToken);
            result.FailurePatternsCreated++;
            result.AffectedExpertiseIds.Add(current.Id);
        }
        else
        {
            var expertiseId = $"exp-fail-{Guid.NewGuid():N}";
            var expertise = new ExpertiseRecord
            {
                Id = expertiseId,
                Subject = filePath,
                Category = ExpertiseCategory.FailurePattern,
                Confidence = Math.Min(1.0, 0.2 + (failedEpisodes.Count * 0.15)),
                EpisodeCount = failedEpisodes.Count,
                DerivedFromEpisodeIds = failedEpisodes.Select(e => e.Id).ToList(),
                Summary = $"Repeated failures in {filePath}: " +
                    string.Join("; ", failedEpisodes.Select(e => e.Summary).Where(s => !string.IsNullOrEmpty(s)).Take(3)),
                NegativeEvidence = failedEpisodes
                    .SelectMany(e => e.Evidence.Select(ev => new EvidenceItem
                    {
                        Category = ev.Category,
                        Content = ev.Content,
                        Source = e.Id
                    }))
                    .ToList()
            };

            await this.store.UpsertExpertiseAsync(expertise, cancellationToken);

            await this.store.AddMemoryEdgeAsync(
                new MemoryEdge
                {
                    Id = $"edge-{Guid.NewGuid():N}",
                    EdgeType = MemoryEdgeType.ExpertiseAboutFile,
                    SourceId = expertiseId,
                    TargetId = filePath
                },
                cancellationToken);

            result.FailurePatternsCreated++;
            result.AffectedExpertiseIds.Add(expertiseId);
        }
    }

    private static string BuildExpertiseSummary(string subject, List<Episode> episodes)
    {
        var taskTypes = episodes
            .GroupBy(e => e.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        var topTags = episodes
            .SelectMany(e => e.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key)
            .ToList();

        var summary = $"Expertise on {subject} derived from {episodes.Count} episodes. " +
            $"Task types: {string.Join(", ", taskTypes)}.";

        if (topTags.Count > 0)
        {
            summary += $" Common tags: {string.Join(", ", topTags)}.";
        }

        return summary;
    }
}
