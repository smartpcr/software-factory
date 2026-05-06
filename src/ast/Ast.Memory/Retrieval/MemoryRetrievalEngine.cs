// -----------------------------------------------------------------------
// <copyright file="MemoryRetrievalEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Retrieval;

using System.Collections.Concurrent;
using System.Diagnostics;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Context;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Orchestrates hybrid context retrieval from the memory graph.
/// Composes <see cref="QueryPlanner"/>, <see cref="Ranker"/>,
/// <see cref="BudgetController"/>, and <see cref="ContextAssembler"/>
/// in the sequence: plan → per-mode traversal/search → rank → budget → assemble
/// (architecture.md §6.2).
/// Uses the canonical <see cref="IMemoryGraphStore"/> contract (§5.1) and
/// <see cref="Ast.GraphDb.IGraphStore"/> base methods for all graph access.
/// </summary>
public sealed class MemoryRetrievalEngine : IMemoryRetrievalEngine
{
    private const string MemoryEmbeddingsCollection = "memory_embeddings";
    private const string MemoryExpertiseCollection = "memory_expertise";
    private const int DefaultBfsDepth = 3;
    private const int DefaultCallTraceDepth = 2;
    private const int DefaultSemanticTopK = 20;
    private const int CharsPerToken = 4;

    private readonly IMemoryGraphStore graphStore;
    private readonly IEmbeddingGenerator? embeddingGenerator;
    private readonly IVectorStore? vectorStore;
    private readonly MemoryOptions options;
    private readonly ILogger<MemoryRetrievalEngine> logger;
    private readonly QueryPlanner queryPlanner;

    public MemoryRetrievalEngine(
        IMemoryGraphStore graphStore,
        IOptions<MemoryOptions> options,
        ILogger<MemoryRetrievalEngine> logger,
        IEmbeddingGenerator? embeddingGenerator = null,
        IVectorStore? vectorStore = null)
    {
        this.graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.embeddingGenerator = embeddingGenerator;
        this.vectorStore = vectorStore;
        this.queryPlanner = new QueryPlanner();
    }

    /// <inheritdoc />
    public async Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Validate store availability via IGraphStore.IsAvailableAsync
        if (!await this.graphStore.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            this.logger.LogWarning("Memory graph store is unavailable; returning empty result");
            return RetrievalResult.Empty("Memory graph store is unavailable");
        }

        // Clamp token budget
        var tokenBudget = Math.Clamp(
            request.TokenBudget,
            1,
            this.options.TokenBudgetCeiling);

        // Step 1: Plan — automatic mode selection based on query shape
        var modes = this.queryPlanner.PlanQuery(request);
        this.logger.LogDebug("Query planner selected modes: {Modes}", string.Join(", ", modes));

        // Pre-compute query embedding if needed (exactly one call per query — HC5)
        float[]? queryEmbedding = null;
        if (modes.Contains(RetrievalMode.SemanticSearch) || modes.Contains(RetrievalMode.ExpertiseRecall))
        {
            queryEmbedding = await this.TryEmbedQueryAsync(request.QueryText, cancellationToken).ConfigureAwait(false);
        }

        // Step 2: Execute modes in three phases.
        // Phase A: SemanticSearch (query-driven, needs no seeds)
        // Phase B: StructuralDrill + CallTrace (seed-driven — uses original seeds
        //          PLUS semantic hits discovered in Phase A, so a free-text Hybrid
        //          query gathers structural/call-chain context around semantic hits
        //          per architecture.md §3.3)
        // Phase C: ExpertiseRecall (runs last — inspects ALL discovered nodes)

        var mergedCandidates = new Dictionary<string, (MemoryNode Node, int BfsDepth, float AnnScore)>();
        var allExpertiseEntries = new List<ExpertiseEntry>();
        var diagnostics = new RetrievalDiagnostics();
        var semanticSearchProducedResults = false;

        // Phase A: SemanticSearch
        if (modes.Contains(RetrievalMode.SemanticSearch))
        {
            var semanticResult = await this.ExecuteModeAsync(
                RetrievalMode.SemanticSearch, request, queryEmbedding, cancellationToken).ConfigureAwait(false);

            if (semanticResult.Candidates.Count > 0)
            {
                semanticSearchProducedResults = true;
            }

            MergeCandidates(mergedCandidates, semanticResult);
            allExpertiseEntries.AddRange(semanticResult.ExpertiseEntries);
            diagnostics.PerMode[RetrievalMode.SemanticSearch] = semanticResult.Diagnostics;
        }

        // Phase B: StructuralDrill + CallTrace
        // Build seeds = original request seeds ∪ semantic hit IDs
        var phaseBSeeds = request.SeedNodeIds
            .Concat(mergedCandidates.Keys)
            .Distinct()
            .ToList();

        var graphModes = new[] { RetrievalMode.StructuralDrill, RetrievalMode.CallTrace };
        foreach (var gMode in graphModes)
        {
            if (!modes.Contains(gMode))
            {
                continue;
            }

            var graphRequest = new RetrievalRequest
            {
                QueryText = request.QueryText,
                SeedNodeIds = phaseBSeeds,
                Mode = gMode,
                TokenBudget = request.TokenBudget,
                AtCommitIndex = request.AtCommitIndex,
                Weights = request.Weights
            };

            var graphResult = await this.ExecuteModeAsync(
                gMode, graphRequest, queryEmbedding, cancellationToken).ConfigureAwait(false);

            MergeCandidates(mergedCandidates, graphResult);
            allExpertiseEntries.AddRange(graphResult.ExpertiseEntries);
            diagnostics.PerMode[gMode] = graphResult.Diagnostics;
        }

        // Phase C: ExpertiseRecall — uses ALL discovered nodes + query text
        if (modes.Contains(RetrievalMode.ExpertiseRecall))
        {
            var allDiscoveredNodeIds = mergedCandidates.Keys.ToList();
            var expertiseRequest = new RetrievalRequest
            {
                QueryText = request.QueryText,
                SeedNodeIds = request.SeedNodeIds
                    .Concat(allDiscoveredNodeIds)
                    .Distinct()
                    .ToList(),
                Mode = RetrievalMode.ExpertiseRecall,
                TokenBudget = request.TokenBudget,
                AtCommitIndex = request.AtCommitIndex,
                Weights = request.Weights
            };

            var expertiseResult = await this.ExecuteModeAsync(
                RetrievalMode.ExpertiseRecall, expertiseRequest, queryEmbedding, cancellationToken).ConfigureAwait(false);

            MergeCandidates(mergedCandidates, expertiseResult);
            allExpertiseEntries.AddRange(expertiseResult.ExpertiseEntries);
            diagnostics.PerMode[RetrievalMode.ExpertiseRecall] = expertiseResult.Diagnostics;
        }

        // Step 3: Rank — merge all candidates and score with 5-signal model
        var weights = request.Weights ?? new ScoringWeights();

        // If we had a query embedding but semantic search produced no results
        // (vector store unavailable or search failure), renormalize weights
        var effectiveQueryEmbedding = queryEmbedding;
        if (queryEmbedding != null && !semanticSearchProducedResults)
        {
            weights = weights.WithoutSemantic();
            effectiveQueryEmbedding = null;
        }

        var candidateList = mergedCandidates.Values.ToList();
        var maxBfsDepth = candidateList.Count > 0
            ? candidateList.Where(c => c.BfsDepth >= 0).Select(c => c.BfsDepth).DefaultIfEmpty(0).Max()
            : 0;
        if (maxBfsDepth == 0)
        {
            maxBfsDepth = DefaultBfsDepth;
        }

        var headCommitIndex = request.AtCommitIndex ?? 0;

        var ranker = new Ranker(this.graphStore, this.logger);
        var rankedNodes = await ranker.RankAsync(
            candidateList,
            effectiveQueryEmbedding,
            weights,
            headCommitIndex,
            maxBfsDepth,
            cancellationToken).ConfigureAwait(false);

        // Step 4: Budget — enforce token budget
        var budgetController = new BudgetController(this.options.TokenBudgetCeiling);
        var (trimmedNodes, _) = budgetController.ApplyBudget(rankedNodes, tokenBudget);

        // Step 5: Assemble — serialize to Markdown, then enforce the token budget
        // on the actual assembled output. Iteratively trim until it fits.
        var assembler = new ContextAssembler();
        var contextMarkdown = assembler.Assemble(trimmedNodes, allExpertiseEntries);
        var actualTokensUsed = EstimateTokens(contextMarkdown);

        while (actualTokensUsed > tokenBudget && trimmedNodes.Count > 1)
        {
            trimmedNodes = trimmedNodes.Take(trimmedNodes.Count - 1).ToList();
            contextMarkdown = assembler.Assemble(trimmedNodes, allExpertiseEntries);
            actualTokensUsed = EstimateTokens(contextMarkdown);
        }

        // Final clamp: truncate markdown to fit budget
        if (actualTokensUsed > tokenBudget)
        {
            var maxChars = tokenBudget * CharsPerToken;
            if (contextMarkdown.Length > maxChars)
            {
                contextMarkdown = contextMarkdown[..maxChars];
            }

            actualTokensUsed = tokenBudget;
        }

        sw.Stop();
        diagnostics.TotalElapsedMs = sw.ElapsedMilliseconds;

        return new RetrievalResult
        {
            Nodes = trimmedNodes,
            ContextMarkdown = contextMarkdown,
            TokensUsed = actualTokensUsed,
            ModesExecuted = modes,
            Diagnostics = diagnostics
        };
    }

    /// <summary>
    /// Executes a single retrieval mode, returning an isolated <see cref="ModeResult"/>.
    /// </summary>
    private async Task<ModeResult> ExecuteModeAsync(
        RetrievalMode mode,
        RetrievalRequest request,
        float[]? queryEmbedding,
        CancellationToken ct)
    {
        var modeSw = Stopwatch.StartNew();
        var result = new ModeResult();

        try
        {
            switch (mode)
            {
                case RetrievalMode.StructuralDrill:
                    await this.ExecuteStructuralDrillAsync(request, result, ct).ConfigureAwait(false);
                    break;

                case RetrievalMode.CallTrace:
                    await this.ExecuteCallTraceAsync(request, result, ct).ConfigureAwait(false);
                    break;

                case RetrievalMode.SemanticSearch:
                    await this.ExecuteSemanticSearchAsync(queryEmbedding, result, ct).ConfigureAwait(false);
                    break;

                case RetrievalMode.ExpertiseRecall:
                    await this.ExecuteExpertiseRecallAsync(request, queryEmbedding, result, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Retrieval mode {Mode} failed; degrading gracefully", mode);
            result.Diagnostics.Error = ex.Message;
        }

        modeSw.Stop();
        result.Diagnostics.ElapsedMs = modeSw.ElapsedMilliseconds;
        result.Diagnostics.CandidateCount = result.Candidates.Count;

        return result;
    }

    /// <summary>
    /// StructuralDrill: BFS from seed along CONTAINS/DEPENDS_ON edges via
    /// <see cref="Ast.GraphDb.IGraphStore.GetEdgesForNodeAsync"/> with
    /// <c>Metadata["SemanticRelation"]</c> filtering.
    /// </summary>
    private async Task ExecuteStructuralDrillAsync(
        RetrievalRequest request,
        ModeResult result,
        CancellationToken ct)
    {
        if (request.SeedNodeIds.Count == 0)
        {
            return;
        }

        foreach (var seedId in request.SeedNodeIds)
        {
            ct.ThrowIfCancellationRequested();
            await this.BfsTraverseAsync(
                seedId,
                [SemanticRelations.Contains, SemanticRelations.DependsOn],
                DefaultBfsDepth,
                request.AtCommitIndex,
                result,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// CallTrace: forward/backward traversal along CALLS/EXTENDS/IMPLEMENTS edges.
    /// All edge lookups use <c>Metadata["SemanticRelation"]</c> exclusively via
    /// <see cref="Ast.GraphDb.IGraphStore.GetEdgesForNodeAsync"/>.
    /// Reverse call-chain is achieved by traversing CALLS edges backward (BFS follows
    /// all connected nodes in matched edges), not via a separate stored edge
    /// (architecture.md §2.3).
    /// </summary>
    private async Task ExecuteCallTraceAsync(
        RetrievalRequest request,
        ModeResult result,
        CancellationToken ct)
    {
        if (request.SeedNodeIds.Count == 0)
        {
            return;
        }

        foreach (var seedId in request.SeedNodeIds)
        {
            ct.ThrowIfCancellationRequested();
            await this.BfsTraverseAsync(
                seedId,
                [SemanticRelations.Calls, SemanticRelations.Extends, SemanticRelations.Implements],
                DefaultCallTraceDepth,
                request.AtCommitIndex,
                result,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// SemanticSearch: embed query text via IEmbeddingGenerator.GenerateEmbeddingAsync(),
    /// ANN lookup via IVectorStore.SearchAsync(). Exactly one local embedding call
    /// per query (architecture.md HC5). ANN scores are preserved on candidates for ranking.
    /// </summary>
    private async Task ExecuteSemanticSearchAsync(
        float[]? queryEmbedding,
        ModeResult result,
        CancellationToken ct)
    {
        if (queryEmbedding == null || this.vectorStore == null)
        {
            this.logger.LogDebug("SemanticSearch skipped: embedding or vector store unavailable");
            return;
        }

        var results = await this.vectorStore.SearchAsync(
            MemoryEmbeddingsCollection,
            queryEmbedding,
            DefaultSemanticTopK,
            cancellationToken: ct).ConfigureAwait(false);

        foreach (var searchResult in results)
        {
            ct.ThrowIfCancellationRequested();
            // Use IGraphStore.GetNodeAsync from base contract, wrap in MemoryNode
            var innerNode = await this.graphStore.GetNodeAsync(searchResult.Id, ct).ConfigureAwait(false);
            if (innerNode != null)
            {
                var node = new MemoryNode(innerNode);
                AddCandidate(result, node, bfsDepth: -1, atCommitIndex: null, annScore: searchResult.Score);
            }
        }
    }

    /// <summary>
    /// ExpertiseRecall: two-path lookup via APPLIES_TO edges (architecture.md §2.2).
    /// Path A: For each seed node, traverse APPLIES_TO edges to discover expertise
    ///         nodes and their connected code nodes. Expertise entries are constructed
    ///         from the expertise node metadata — GetExpertiseForNodeAsync is NOT called.
    /// Path B: Embed query text and ANN-search the memory_expertise collection, then
    ///         follow APPLIES_TO edges from matched expertise entries to discover code nodes.
    /// All discovery uses edge traversal exclusively via APPLIES_TO edges with
    /// <c>Metadata["MemoryLayer"] == "expertise"</c> and
    /// <c>Metadata["SemanticRelation"] == "APPLIES_TO"</c>.
    /// </summary>
    private async Task ExecuteExpertiseRecallAsync(
        RetrievalRequest request,
        float[]? queryEmbedding,
        ModeResult result,
        CancellationToken ct)
    {
        // Path A: From each seed node, traverse APPLIES_TO edges to find connected
        // expertise and code nodes on the other end.
        foreach (var seedId in request.SeedNodeIds)
        {
            ct.ThrowIfCancellationRequested();

            var allEdges = await this.graphStore.GetEdgesForNodeAsync(seedId, cancellationToken: ct).ConfigureAwait(false);
            var appliesToEdges = FilterEdgesBySemanticRelation(allEdges, SemanticRelations.AppliesTo, MemoryLayers.Expertise);

            foreach (var edge in appliesToEdges)
            {
                foreach (var nodeId in edge.NodeIds)
                {
                    if (nodeId == seedId)
                    {
                        continue;
                    }

                    // The other end is either an expertise node or a code node.
                    // Get the node and add it as a candidate.
                    var innerNode = await this.graphStore.GetNodeAsync(nodeId, ct).ConfigureAwait(false);
                    if (innerNode != null)
                    {
                        AddCandidate(result, new MemoryNode(innerNode), bfsDepth: -1, atCommitIndex: null);

                        // If this is an expertise node, build an ExpertiseEntry from its metadata
                        TryAddExpertiseEntryFromNode(innerNode, seedId, result);

                        // Follow APPLIES_TO edges from this node to discover further code nodes
                        var innerEdges = await this.graphStore.GetEdgesForNodeAsync(nodeId, cancellationToken: ct).ConfigureAwait(false);
                        var innerAppliesTo = FilterEdgesBySemanticRelation(innerEdges, SemanticRelations.AppliesTo, MemoryLayers.Expertise);

                        foreach (var innerEdge in innerAppliesTo)
                        {
                            foreach (var codeNodeId in innerEdge.NodeIds)
                            {
                                if (codeNodeId != nodeId && codeNodeId != seedId)
                                {
                                    var codeInner = await this.graphStore.GetNodeAsync(codeNodeId, ct).ConfigureAwait(false);
                                    if (codeInner != null)
                                    {
                                        AddCandidate(result, new MemoryNode(codeInner), bfsDepth: -1, atCommitIndex: null);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Path B: ANN search over expertise embeddings, then follow APPLIES_TO edges
        if (queryEmbedding != null && this.vectorStore != null)
        {
            try
            {
                var expertiseResults = await this.vectorStore.SearchAsync(
                    MemoryExpertiseCollection,
                    queryEmbedding,
                    DefaultSemanticTopK,
                    cancellationToken: ct).ConfigureAwait(false);

                foreach (var searchResult in expertiseResults)
                {
                    ct.ThrowIfCancellationRequested();

                    // Follow APPLIES_TO edges from the expertise/vector result to discover code nodes.
                    // Ignore Payload["TargetNodeId"] — only APPLIES_TO edges determine code nodes.
                    var allEdges = await this.graphStore.GetEdgesForNodeAsync(
                        searchResult.Id, cancellationToken: ct).ConfigureAwait(false);
                    var appliesToEdges = FilterEdgesBySemanticRelation(allEdges, SemanticRelations.AppliesTo, MemoryLayers.Expertise);

                    // Build an ExpertiseEntry from the expertise node itself
                    var expertiseNode = await this.graphStore.GetNodeAsync(searchResult.Id, ct).ConfigureAwait(false);

                    foreach (var edge in appliesToEdges)
                    {
                        foreach (var nodeId in edge.NodeIds)
                        {
                            if (nodeId != searchResult.Id)
                            {
                                var codeInner = await this.graphStore.GetNodeAsync(nodeId, ct).ConfigureAwait(false);
                                if (codeInner != null)
                                {
                                    AddCandidate(result, new MemoryNode(codeInner), bfsDepth: -1, atCommitIndex: null);

                                    // Build expertise entry from the expertise node metadata,
                                    // targeting the discovered code node
                                    if (expertiseNode != null)
                                    {
                                        TryAddExpertiseEntryFromNode(expertiseNode, nodeId, result);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger.LogDebug(ex, "Expertise vector search failed; continuing with Path A results");
            }
        }
    }

    /// <summary>
    /// Constructs an <see cref="ExpertiseEntry"/> from an expertise node's metadata,
    /// if the node appears to be an expertise node (has Title or Name).
    /// This avoids calling <see cref="IMemoryGraphStore.GetExpertiseForNodeAsync"/>
    /// during discovery — ExpertiseRecall uses edge traversal exclusively.
    /// </summary>
    private static void TryAddExpertiseEntryFromNode(
        HypergraphNode expertiseNode,
        string targetNodeId,
        ModeResult result)
    {
        if (!expertiseNode.Id.StartsWith("expertise:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var title = expertiseNode.Name ?? expertiseNode.Id;
        var body = expertiseNode.Metadata.TryGetValue(MemoryMetadataKeys.Summary, out var s) && s is string summary
            ? summary
            : string.Empty;
        var confidence = expertiseNode.Metadata.TryGetValue(MemoryMetadataKeys.Confidence, out var c) && c is double conf
            ? conf
            : 0.8;

        var entry = new ExpertiseEntry
        {
            Id = expertiseNode.Id,
            Kind = ExpertiseKind.Pattern,
            Title = title,
            Body = body,
            TargetNodeId = targetNodeId,
            Confidence = confidence
        };

        result.ExpertiseEntries.Add(entry);
    }

    /// <summary>
    /// BFS traversal from seed along edges matching the specified semantic relations.
    /// Edge filtering uses Metadata["SemanticRelation"] exclusively (architecture.md §2.2, §3.3).
    /// Uses <see cref="Ast.GraphDb.IGraphStore.GetEdgesForNodeAsync"/> from the base contract,
    /// then filters in-memory by <c>Metadata["SemanticRelation"]</c>.
    /// </summary>
    private async Task BfsTraverseAsync(
        string seedId,
        IReadOnlyList<string> semanticRelations,
        int maxDepth,
        int? atCommitIndex,
        ModeResult result,
        CancellationToken ct)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<(string NodeId, int Depth)>();
        queue.Enqueue((seedId, 0));
        visited.Add(seedId);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (currentId, depth) = queue.Dequeue();

            // Get the current node via IGraphStore.GetNodeAsync, wrap in MemoryNode
            var innerNode = await this.graphStore.GetNodeAsync(currentId, ct).ConfigureAwait(false);
            if (innerNode != null)
            {
                AddCandidate(result, new MemoryNode(innerNode), depth, atCommitIndex);
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            // Get all edges for this node via IGraphStore.GetEdgesForNodeAsync,
            // then filter by Metadata["SemanticRelation"] — NOT by HyperedgeType
            var allEdges = await this.graphStore.GetEdgesForNodeAsync(currentId, cancellationToken: ct).ConfigureAwait(false);
            var matchedEdges = FilterEdgesBySemanticRelations(allEdges, semanticRelations);

            foreach (var edge in matchedEdges)
            {
                // Temporal filter
                if (atCommitIndex.HasValue)
                {
                    var memEdge = new MemoryEdge(edge);
                    if (!memEdge.IsActiveAt(atCommitIndex.Value))
                    {
                        continue;
                    }
                }

                // Traverse to connected nodes
                foreach (var nodeId in edge.NodeIds)
                {
                    if (nodeId != currentId && visited.Add(nodeId))
                    {
                        queue.Enqueue((nodeId, depth + 1));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Filters edges by a single semantic relation and optional memory layer.
    /// Uses <c>Metadata["SemanticRelation"]</c> exclusively (architecture.md §2.2).
    /// </summary>
    private static IReadOnlyList<Hyperedge> FilterEdgesBySemanticRelation(
        IReadOnlyList<Hyperedge> edges,
        string semanticRelation,
        string? memoryLayer = null)
    {
        return edges.Where(e =>
            e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var rel)
            && rel is string s && s == semanticRelation
            && (memoryLayer == null
                || (e.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var layer)
                    && layer is string l && l == memoryLayer)))
            .ToList();
    }

    /// <summary>
    /// Filters edges matching any of the specified semantic relations.
    /// Uses <c>Metadata["SemanticRelation"]</c> exclusively (architecture.md §2.2).
    /// </summary>
    private static IReadOnlyList<Hyperedge> FilterEdgesBySemanticRelations(
        IReadOnlyList<Hyperedge> edges,
        IReadOnlyList<string> semanticRelations)
    {
        var relSet = new HashSet<string>(semanticRelations);
        return edges.Where(e =>
            e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var rel)
            && rel is string s && relSet.Contains(s))
            .ToList();
    }

    /// <summary>
    /// Merges BFS depths preferring real depth over sentinel -1.
    /// When both are real (≥ 0), the shorter depth wins.
    /// </summary>
    private static int MergeBfsDepth(int a, int b)
    {
        if (a < 0 && b < 0)
        {
            return -1;
        }

        if (a < 0)
        {
            return b;
        }

        if (b < 0)
        {
            return a;
        }

        return Math.Min(a, b);
    }

    /// <summary>
    /// Merges candidates from a completed <see cref="ModeResult"/> into the
    /// global merged dictionary.
    /// </summary>
    private static void MergeCandidates(
        Dictionary<string, (MemoryNode Node, int BfsDepth, float AnnScore)> merged,
        ModeResult modeResult)
    {
        foreach (var (nodeId, candidate) in modeResult.Candidates)
        {
            if (merged.TryGetValue(nodeId, out var existing))
            {
                merged[nodeId] = (
                    existing.Node,
                    MergeBfsDepth(existing.BfsDepth, candidate.BfsDepth),
                    Math.Max(existing.AnnScore, candidate.AnnScore));
            }
            else
            {
                merged[nodeId] = candidate;
            }
        }
    }

    private static void AddCandidate(
        ModeResult result,
        MemoryNode node,
        int bfsDepth,
        int? atCommitIndex,
        float annScore = 0f)
    {
        // Temporal filter
        if (atCommitIndex.HasValue && !node.IsActiveAt(atCommitIndex.Value))
        {
            return;
        }

        var nodeId = node.Inner.Id;
        result.Candidates.AddOrUpdate(
            nodeId,
            _ => (node, bfsDepth, annScore),
            (_, existing) => (existing.Node, MergeBfsDepth(existing.BfsDepth, bfsDepth), Math.Max(existing.AnnScore, annScore)));
    }

    /// <summary>
    /// Embeds query text, returning null if the embedding generator is unavailable.
    /// Exactly one local embedding call per query (HC5).
    /// </summary>
    private async Task<float[]?> TryEmbedQueryAsync(string? queryText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queryText) || this.embeddingGenerator == null)
        {
            return null;
        }

        try
        {
            if (!await this.embeddingGenerator.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                this.logger.LogDebug("Embedding generator is unavailable; semantic signals will be zero");
                return null;
            }

            return await this.embeddingGenerator.GenerateEmbeddingAsync(queryText, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Failed to embed query text; semantic signals will be zero");
            return null;
        }
    }

    /// <summary>
    /// Estimates token count from text length. Uses ~4 characters per token approximation.
    /// </summary>
    private static int EstimateTokens(string text)
    {
        return Math.Max(1, text.Length / CharsPerToken);
    }

    /// <summary>
    /// Per-mode result container. Each <see cref="ModeResult"/> is owned by exactly one
    /// retrieval-mode task — no two tasks ever share a ModeResult.
    /// </summary>
    internal sealed class ModeResult
    {
        public ConcurrentDictionary<string, (MemoryNode Node, int BfsDepth, float AnnScore)> Candidates { get; } = new();
        public ConcurrentBag<ExpertiseEntry> ExpertiseEntries { get; } = [];
        public ModeDiagnostics Diagnostics { get; } = new();
    }
}