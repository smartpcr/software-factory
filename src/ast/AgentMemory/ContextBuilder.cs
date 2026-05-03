// -----------------------------------------------------------------------
// <copyright file="ContextBuilder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

using Ast.Core;

/// <summary>
/// Builds context windows for agent tasks using a staged retrieval pipeline.
/// Follows the MAGMA multi-signal approach: candidate generation → reranking → assembly.
/// </summary>
public interface IContextBuilder
{
    /// <summary>
    /// Builds a context window for the given request by retrieving
    /// relevant episodes, expertise, and code graph data.
    /// </summary>
    Task<ContextWindow> BuildContextAsync(
        ContextRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IContextBuilder"/>. Works with
/// just <see cref="IAgentMemoryStore"/> when graph/vector stores are unavailable,
/// degrading gracefully to memory-only context.
/// </summary>
public class ContextBuilder : IContextBuilder
{
    private readonly IAgentMemoryStore memoryStore;
    private readonly IGraphStoreAdapter? graphAdapter;
    private readonly ICallChainStore? callChainStore;

    /// <summary>
    /// Creates a context builder with memory-only retrieval.
    /// </summary>
    public ContextBuilder(IAgentMemoryStore memoryStore)
    {
        this.memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
    }

    /// <summary>
    /// Creates a context builder with graph-enhanced retrieval.
    /// </summary>
    public ContextBuilder(IAgentMemoryStore memoryStore, IGraphStoreAdapter graphAdapter)
        : this(memoryStore)
    {
        this.graphAdapter = graphAdapter;
    }

    /// <summary>
    /// Creates a context builder with graph and call-chain retrieval.
    /// </summary>
    public ContextBuilder(
        IAgentMemoryStore memoryStore,
        IGraphStoreAdapter? graphAdapter,
        ICallChainStore? callChainStore)
        : this(memoryStore)
    {
        this.graphAdapter = graphAdapter;
        this.callChainStore = callChainStore;
    }

    /// <inheritdoc/>
    public async Task<ContextWindow> BuildContextAsync(
        ContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Stage 1: Candidate generation
        var candidates = await this.GenerateCandidatesAsync(request, cancellationToken);

        // Stage 2: Reranking
        var rankedCandidates = RankCandidates(candidates, request);

        // Stage 3: Assembly
        return this.AssembleContextWindow(rankedCandidates, request);
    }

    private async Task<RetrievalCandidates> GenerateCandidatesAsync(
        ContextRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = new RetrievalCandidates();

        // 1. Direct file/symbol matches from request
        if (request.FilePaths.Count > 0)
        {
            var episodesByFile = await this.memoryStore.GetEpisodesAsync(
                new EpisodeFilter
                {
                    TouchedFiles = request.FilePaths,
                    Limit = request.MaxEpisodes * 2
                },
                cancellationToken);
            candidates.Episodes.AddRange(episodesByFile);

            // Get expertise for each file
            foreach (var file in request.FilePaths)
            {
                var fileExpertise = await this.memoryStore.GetExpertiseForSubjectAsync(
                    file, cancellationToken);
                candidates.Expertise.AddRange(fileExpertise);
            }
        }

        if (request.SymbolIds.Count > 0)
        {
            var episodesBySymbol = await this.memoryStore.GetEpisodesAsync(
                new EpisodeFilter
                {
                    TouchedSymbolIds = request.SymbolIds,
                    Limit = request.MaxEpisodes * 2
                },
                cancellationToken);

            // Add without duplicates
            var existingIds = new HashSet<string>(candidates.Episodes.Select(e => e.Id));
            candidates.Episodes.AddRange(episodesBySymbol.Where(e => !existingIds.Contains(e.Id)));

            foreach (var symbol in request.SymbolIds)
            {
                var symbolExpertise = await this.memoryStore.GetExpertiseForSubjectAsync(
                    symbol, cancellationToken);
                candidates.Expertise.AddRange(symbolExpertise);
            }
        }

        // 2. Graph neighbor expansion (if graph store is available)
        if (this.graphAdapter is not null && request.SymbolIds.Count > 0)
        {
            foreach (var symbolId in request.SymbolIds)
            {
                var neighbors = await this.graphAdapter.GetNeighborNodesAsync(
                    symbolId, request.MaxGraphDepth, cancellationToken);
                candidates.StructuralNodes.AddRange(neighbors.Nodes);
                candidates.RelationalEdges.AddRange(neighbors.Edges);

                // Also find episodes touching neighbor symbols
                var neighborIds = neighbors.Nodes.Select(n => n.Id).ToList();
                if (neighborIds.Count > 0)
                {
                    var neighborEpisodes = await this.memoryStore.GetEpisodesAsync(
                        new EpisodeFilter
                        {
                            TouchedSymbolIds = neighborIds,
                            Limit = request.MaxEpisodes
                        },
                        cancellationToken);

                    var existing = new HashSet<string>(candidates.Episodes.Select(e => e.Id));
                    candidates.Episodes.AddRange(
                        neighborEpisodes.Where(e => !existing.Contains(e.Id)));
                }
            }
        }

        // 3. Find memory edges from touched files/symbols to discover related expertise
        foreach (var file in request.FilePaths)
        {
            var edges = await this.memoryStore.GetIncomingEdgesAsync(
                file, MemoryEdgeType.ExpertiseAboutFile, cancellationToken);

            foreach (var edge in edges)
            {
                var expertise = await this.memoryStore.GetExpertiseAsync(
                    edge.SourceId, cancellationToken);
                if (expertise is not null &&
                    !candidates.Expertise.Any(e => e.Id == expertise.Id))
                {
                    candidates.Expertise.Add(expertise);
                }
            }
        }

        // 4. High-confidence expertise across categories (institutional knowledge)
        var highConfidenceExpertise = await this.memoryStore.GetExpertiseByCategoryAsync(
            ExpertiseCategory.PatternKnowledge,
            minConfidence: 0.7,
            limit: 10,
            cancellationToken);
        candidates.Expertise.AddRange(
            highConfidenceExpertise.Where(e => !candidates.Expertise.Any(x => x.Id == e.Id)));

        // 5. Call chain retrieval (if call chain store is available)
        if (this.callChainStore is not null && request.SymbolIds.Count > 0)
        {
            foreach (var symbolId in request.SymbolIds)
            {
                var chains = await this.callChainStore.GetCallChainsForSymbolAsync(
                    symbolId, cancellationToken);
                var existingIds = new HashSet<string>(candidates.CallChains.Select(c => c.Id));
                candidates.CallChains.AddRange(chains.Where(c => !existingIds.Contains(c.Id)));
            }
        }

        return candidates;
    }

    private static List<ScoredCandidate> RankCandidates(
        RetrievalCandidates candidates,
        ContextRequest request)
    {
        var scored = new List<ScoredCandidate>();
        var now = DateTime.UtcNow;

        // Score episodes
        foreach (var episode in candidates.Episodes)
        {
            var score = 0.0;

            // Exact file match boost
            var fileMatchCount = episode.TouchedFiles
                .Count(f => request.FilePaths.Any(
                    rp => rp.Equals(f, StringComparison.OrdinalIgnoreCase)));
            score += fileMatchCount * 0.3;

            // Symbol match boost
            var symbolMatchCount = episode.TouchedSymbolIds
                .Count(s => request.SymbolIds.Contains(s));
            score += symbolMatchCount * 0.3;

            // Recency: decay over 90 days
            var daysSince = (now - episode.Timestamp).TotalDays;
            score += Math.Max(0, 0.2 * (1.0 - (daysSince / 90.0)));

            // Outcome boost
            score += episode.Outcome switch
            {
                EpisodeOutcome.Success => 0.2,
                EpisodeOutcome.Partial => 0.1,
                EpisodeOutcome.Failed => -0.05,
                _ => 0.0
            };

            scored.Add(new ScoredCandidate
            {
                Type = CandidateType.Episode,
                Episode = episode,
                Score = score
            });
        }

        // Score expertise
        foreach (var expertise in candidates.Expertise)
        {
            var score = 0.0;

            // Subject match
            if (request.FilePaths.Any(f => f.Equals(expertise.Subject, StringComparison.OrdinalIgnoreCase)) ||
                request.SymbolIds.Contains(expertise.Subject))
            {
                score += 0.4;
            }

            // Confidence as weight
            score += expertise.Confidence * 0.3;

            // Freshness
            var daysSinceValidation = (now - expertise.LastValidated).TotalDays;
            score += Math.Max(0, 0.15 * (1.0 - (daysSinceValidation / 60.0)));

            // Episode backing strength
            score += Math.Min(0.15, expertise.EpisodeCount * 0.03);

            scored.Add(new ScoredCandidate
            {
                Type = CandidateType.Expertise,
                Expertise = expertise,
                Score = score
            });
        }

        // Score call chains
        foreach (var chain in candidates.CallChains)
        {
            var score = 0.0;

            // Symbol overlap: how many chain steps match the request symbols
            var symbolOverlap = chain.Steps
                .Count(s => request.SymbolIds.Contains(s.SymbolId));
            score += Math.Min(0.4, symbolOverlap * 0.2);

            // Confidence
            score += chain.Confidence * 0.2;

            // Observed vs. Inferred: observed chains are more reliable
            score += chain.Source == CallChainSource.Observed ? 0.15 : 0.05;

            // Observation count: frequently observed chains are more important
            score += Math.Min(0.15, chain.ObservationCount * 0.03);

            // Recency
            var daysSinceObserved = (now - chain.LastObserved).TotalDays;
            score += Math.Max(0, 0.1 * (1.0 - (daysSinceObserved / 90.0)));

            scored.Add(new ScoredCandidate
            {
                Type = CandidateType.CallChain,
                CallChain = chain,
                Score = score
            });
        }

        return scored.OrderByDescending(c => c.Score).ToList();
    }

    private ContextWindow AssembleContextWindow(
        List<ScoredCandidate> rankedCandidates,
        ContextRequest request)
    {
        var episodes = rankedCandidates
            .Where(c => c.Type == CandidateType.Episode && c.Episode is not null)
            .Take(request.MaxEpisodes)
            .Select(c => c.Episode!)
            .ToList();

        var expertise = rankedCandidates
            .Where(c => c.Type == CandidateType.Expertise && c.Expertise is not null)
            .Where(c => c.Expertise!.Confidence >= request.MinConfidence)
            .Take(request.MaxExpertise)
            .Select(c => c.Expertise!)
            .ToList();

        var callChains = rankedCandidates
            .Where(c => c.Type == CandidateType.CallChain && c.CallChain is not null)
            .Take(request.MaxCallChains)
            .Select(c => c.CallChain!)
            .ToList();

        // Build symbol familiarity from expertise scores
        var familiarity = expertise
            .GroupBy(e => e.Subject)
            .ToDictionary(
                g => g.Key,
                g => g.Max(e => e.Confidence));

        // Detect freshness warnings
        var warnings = new List<string>();
        foreach (var exp in expertise)
        {
            var daysSinceValidation = (DateTime.UtcNow - exp.LastValidated).TotalDays;
            if (daysSinceValidation > 30)
            {
                warnings.Add(
                    $"Expertise on '{exp.Subject}' was last validated {daysSinceValidation:F0} days ago.");
            }
        }

        if (this.graphAdapter is null)
        {
            warnings.Add("Graph store unavailable — context limited to memory-only retrieval.");
        }

        // Overall relevance score: average of top candidates
        var topScores = rankedCandidates.Take(10).Select(c => c.Score).ToList();
        var relevanceScore = topScores.Count > 0 ? topScores.Average() : 0.0;

        return new ContextWindow
        {
            TaskDescription = request.TaskDescription,
            RelevantEpisodes = episodes,
            Expertise = expertise,
            CallChains = callChains,
            SymbolFamiliarity = familiarity,
            FreshnessWarnings = warnings,
            RelevanceScore = Math.Max(0, Math.Min(1.0, relevanceScore))
        };
    }
}

/// <summary>
/// Adapter interface for graph store operations needed by ContextBuilder.
/// Decouples memory from the specific graph store implementation (null-object pattern).
/// </summary>
public interface IGraphStoreAdapter
{
    /// <summary>
    /// Gets neighbor nodes and edges within the specified depth.
    /// </summary>
    Task<GraphNeighborhood> GetNeighborNodesAsync(
        string symbolId,
        int maxDepth,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a graph neighborhood query.
/// </summary>
public class GraphNeighborhood
{
    /// <summary>Neighbor nodes discovered.</summary>
    public List<HypergraphNode> Nodes { get; init; } = [];

    /// <summary>Edges connecting the neighbors.</summary>
    public List<Hyperedge> Edges { get; init; } = [];
}

// ── Internal types ────────────────────────────────────────────────────

internal class RetrievalCandidates
{
    public List<Episode> Episodes { get; } = [];
    public List<ExpertiseRecord> Expertise { get; } = [];
    public List<HypergraphNode> StructuralNodes { get; } = [];
    public List<Hyperedge> RelationalEdges { get; } = [];
    public List<CallChain> CallChains { get; } = [];
}

internal enum CandidateType
{
    Episode,
    Expertise,
    CallChain
}

internal class ScoredCandidate
{
    public required CandidateType Type { get; init; }
    public Episode? Episode { get; init; }
    public ExpertiseRecord? Expertise { get; init; }
    public CallChain? CallChain { get; init; }
    public double Score { get; init; }
}
