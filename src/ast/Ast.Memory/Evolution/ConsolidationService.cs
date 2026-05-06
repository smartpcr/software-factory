// -----------------------------------------------------------------------
// <copyright file="ConsolidationService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// LLM-powered merge of semantically similar expertise entries.
/// Finds candidate pairs via <see cref="IVectorStore.SearchAsync"/> with a payload
/// filter for <c>TargetNodeId</c>, ensuring only same-target entries are considered.
/// Resolves candidate IDs through a two-tier strategy: direct ID match (for
/// stores that preserve string IDs) followed by payload-based lookup using
/// <c>TargetNodeId</c> plus embedding distance (for Qdrant which returns
/// deterministic UUID v5 point IDs instead of the original expertise IDs).
/// Verifies exact cosine similarity >= <see cref="MemoryOptions.SimilarityThreshold"/> (the global
/// default, overridable per-call via <see cref="ConsolidationOptions.SimilarityThreshold"/>),
/// then calls Ollama <c>/api/generate</c> to produce merged Title+Body text.
/// Creates <c>SUPERSEDES</c> and <c>DERIVED_FROM</c> edges per architecture.md §3.5, §2.2.
/// Acquires a shared write-level lock (<see cref="IGraphWriteLock"/>) per architecture.md §3.7.
/// </summary>
public class ConsolidationService
{
    private const string ExpertiseCollectionName = "memory_expertise";
    private const int CandidateTopK = 10;

    private readonly IMemoryGraphStore graphStore;
    private readonly IVectorStore vectorStore;
    private readonly IEmbeddingGenerator embeddingGenerator;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IGraphWriteLock writeLock;
    private readonly MemoryOptions options;
    private readonly ILogger<ConsolidationService> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsolidationService"/> class.
    /// </summary>
    public ConsolidationService(
        IMemoryGraphStore graphStore,
        IVectorStore vectorStore,
        IEmbeddingGenerator embeddingGenerator,
        IHttpClientFactory httpClientFactory,
        IGraphWriteLock writeLock,
        IOptions<MemoryOptions> options,
        ILogger<ConsolidationService> logger)
    {
        this.graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        this.vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        this.embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.writeLock = writeLock ?? throw new ArgumentNullException(nameof(writeLock));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs LLM-powered consolidation by gathering active expertise entries from the graph
    /// store <em>inside</em> the write lock, preventing stale-data merges after a concurrent run.
    /// Uses only canonical <see cref="IMemoryGraphStore"/> methods: <see cref="IMemoryGraphStore.GetEdgesBySemanticRelationAsync"/>
    /// to discover APPLIES_TO edges, then <see cref="IMemoryGraphStore.GetExpertiseForNodeAsync"/>
    /// per target node — no consolidation-specific store methods required.
    /// </summary>
    /// <param name="consolidationOptions">Per-run tuning knobs.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationOptions consolidationOptions,
        CancellationToken ct = default)
    {
        using var lockHandle = await this.writeLock.AcquireAsync(ct).ConfigureAwait(false);

        // Gather active entries INSIDE the lock to prevent stale-duplicate merges
        // after a concurrent consolidation has already archived originals.
        var activeEntries = await this.GatherActiveEntriesAsync(ct).ConfigureAwait(false);

        return await this.RunConsolidationAsync(activeEntries, consolidationOptions, ct).ConfigureAwait(false);
    }

    private async Task<ConsolidationResult> RunConsolidationAsync(
        IReadOnlyList<ExpertiseEntry> activeEntries,
        ConsolidationOptions consolidationOptions,
        CancellationToken ct)
    {
        // ConsolidationOptions.SimilarityThreshold is a per-call override;
        // fall back to MemoryOptions.SimilarityThreshold (the global default).
        var threshold = consolidationOptions.SimilarityThreshold ?? this.options.SimilarityThreshold;
        var maxMerges = consolidationOptions.MaxMergesPerRun;

        if (activeEntries.Count < 2)
        {
            this.logger.LogDebug("Fewer than 2 active entries; nothing to consolidate.");
            return new ConsolidationResult(0, 0, []);
        }

        // Verify vector store availability and collection existence before proceeding.
        // Gracefully degrade if the vector store is unreachable or the collection
        // has not been created yet (e.g. first run before any expertise was recorded).
        try
        {
            var isAvailable = await this.vectorStore.IsAvailableAsync(ct).ConfigureAwait(false);
            if (!isAvailable)
            {
                this.logger.LogWarning(
                    "Vector store is not available; consolidation cannot proceed without similarity search.");
                return new ConsolidationResult(0, 0, []);
            }

            var collectionExists = await this.vectorStore.CollectionExistsAsync(ExpertiseCollectionName, ct)
                .ConfigureAwait(false);
            if (!collectionExists)
            {
                this.logger.LogWarning(
                    "Vector collection '{CollectionName}' does not exist; no embeddings to search for consolidation.",
                    ExpertiseCollectionName);
                return new ConsolidationResult(0, 0, []);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(
                ex,
                "Vector store availability check failed; consolidation skipped.");
            return new ConsolidationResult(0, 0, []);
        }

        // Pre-build lookup dictionary for O(1) ID resolution.
        var entriesById = activeEntries.ToDictionary(e => e.Id);

        var candidatesPaired = 0;
        var mergedCount = 0;
        var mergedEntryIds = new List<string>();
        var alreadyMerged = new HashSet<string>();

        foreach (var entry in activeEntries)
        {
            if (mergedCount >= maxMerges)
            {
                break;
            }

            if (alreadyMerged.Contains(entry.Id))
            {
                continue;
            }

            if (entry.Embedding is null || entry.Embedding.Length == 0)
            {
                this.logger.LogDebug("Entry {EntryId} has no embedding; skipping.", entry.Id);
                continue;
            }

            var filter = new VectorFilter
            {
                Must =
                [
                    new FilterCondition { Field = "TargetNodeId", Value = entry.TargetNodeId }
                ]
            };

            IReadOnlyList<VectorSearchResult> candidates;
            try
            {
                candidates = await this.vectorStore.SearchAsync(
                    ExpertiseCollectionName,
                    entry.Embedding,
                    CandidateTopK,
                    filter,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger.LogWarning(
                    ex,
                    "Vector search failed for entry {EntryId}; skipping this entry. " +
                    "Ensure the '{CollectionName}' collection exists in the vector store.",
                    entry.Id, ExpertiseCollectionName);
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (mergedCount >= maxMerges)
                {
                    break;
                }

                // Two-tier candidate resolution:
                // 1. Direct ID match — works for in-memory/mock stores that preserve string IDs
                // 2. Payload-based lookup — works for Qdrant which returns UUID v5 point IDs
                //    instead of original expertise entry IDs. Uses {targetNodeId, kind} from
                //    the architecture-specified payload (architecture.md §3.2.2) plus embedding
                //    distance to resolve the correct entry.
                var matchEntry = this.ResolveCandidateEntry(
                    candidate, entry, entriesById, activeEntries, alreadyMerged);

                if (matchEntry is null)
                {
                    continue;
                }

                candidatesPaired++;

                // Defensive revalidation: vector-store filters are best-effort (e.g.
                // in-memory mocks may not enforce them).
                if (matchEntry.TargetNodeId != entry.TargetNodeId)
                {
                    this.logger.LogDebug(
                        "Candidate {CandidateId} target mismatch with {EntryId}; skipping.",
                        matchEntry.Id, entry.Id);
                    continue;
                }

                // Guard: never merge entries of different ExpertiseKind values,
                // which would produce an incorrectly typed merged entry.
                if (matchEntry.Kind != entry.Kind)
                {
                    this.logger.LogDebug(
                        "Candidate {CandidateId} kind {CandidateKind} differs from {EntryId} kind {EntryKind}; skipping cross-kind merge.",
                        matchEntry.Id, matchEntry.Kind, entry.Id, entry.Kind);
                    continue;
                }

                var exactCosine = ComputeCosineSimilarity(entry.Embedding, matchEntry.Embedding!);
                if (exactCosine < threshold)
                {
                    this.logger.LogDebug(
                        "Pair ({EntryA}, {EntryB}) exact cosine {Cosine:F4} below threshold {Threshold}; skipping.",
                        entry.Id, matchEntry.Id, exactCosine, threshold);
                    continue;
                }

                var mergedText = await this.GenerateMergedTextAsync(entry, matchEntry, ct).ConfigureAwait(false);
                if (mergedText is null)
                {
                    this.logger.LogWarning(
                        "LLM merge failed for pair ({EntryA}, {EntryB}); entries remain unconsolidated.",
                        entry.Id, matchEntry.Id);
                    continue;
                }

                // Generate embedding for merged content.
                var mergedEmbeddingText = $"{mergedText.Title} {mergedText.Body}";
                float[]? mergedEmbedding;
                try
                {
                    mergedEmbedding = await this.embeddingGenerator
                        .GenerateEmbeddingAsync(mergedEmbeddingText, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger.LogWarning(
                        ex,
                        "Embedding generation failed for merged entry from ({EntryA}, {EntryB}); skipping merge.",
                        entry.Id, matchEntry.Id);
                    continue;
                }

                var mergedContentHash = ExpertiseEntry.ComputeContentHash(
                    mergedText.Title, mergedText.Body, entry.TargetNodeId);
                var mergedId = ExpertiseEntry.GenerateId(entry.Kind, mergedContentHash);

                // Guard: if the deterministic merged ID collides with one of the source
                // entries (e.g. the LLM returned unchanged text from one source), skip
                // this merge to avoid self-edges and accidental archival of the merged entry.
                if (mergedId == entry.Id || mergedId == matchEntry.Id)
                {
                    this.logger.LogWarning(
                        "Merged ID {MergedId} collides with source entry; LLM likely returned unchanged text. Skipping merge.",
                        mergedId);
                    continue;
                }

                var mergedEntry = new ExpertiseEntry
                {
                    Id = mergedId,
                    Kind = entry.Kind,
                    Title = mergedText.Title,
                    Body = mergedText.Body,
                    TargetNodeId = entry.TargetNodeId,
                    Scope = entry.Scope,
                    Confidence = Math.Max(entry.Confidence, matchEntry.Confidence),
                    SourceAgent = "consolidation-service",
                    Evidence = CombineEvidence(entry.Evidence, matchEntry.Evidence),
                    Embedding = mergedEmbedding,
                    ContentHash = mergedContentHash
                };

                await this.PersistMergeAsync(
                    mergedEntry, entry, matchEntry, exactCosine, threshold, ct).ConfigureAwait(false);

                alreadyMerged.Add(entry.Id);
                alreadyMerged.Add(matchEntry.Id);
                mergedEntryIds.Add(mergedId);
                mergedCount++;

                this.logger.LogInformation(
                    "Merged entries ({EntryA}, {EntryB}) into {MergedId} (cosine={Cosine:F4}).",
                    entry.Id, matchEntry.Id, mergedId, exactCosine);

                break; // One merge per source entry per run
            }
        }

        this.logger.LogInformation(
            "Consolidation complete: {CandidatesPaired} pairs evaluated, {MergedCount} merges performed.",
            candidatesPaired, mergedCount);

        return new ConsolidationResult(candidatesPaired, mergedCount, mergedEntryIds);
    }

    /// <summary>
    /// Gathers active (non-archived) expertise entries using only canonical
    /// <see cref="IMemoryGraphStore"/> methods. ExpertiseEntry nodes are NOT
    /// HypergraphNode instances (architecture.md §2.1 lines 135-137) and do not
    /// carry a MemoryLayer metadata key, so <c>GetNodesByLayerAsync("expertise")</c>
    /// would return empty. Instead, we discover targets via APPLIES_TO edges
    /// (tagged <c>MemoryLayer = "expertise"</c>) and gather entries per target.
    /// </summary>
    private async Task<IReadOnlyList<ExpertiseEntry>> GatherActiveEntriesAsync(CancellationToken ct)
    {
        var appliesToEdges = await this.graphStore
            .GetEdgesBySemanticRelationAsync("APPLIES_TO", "expertise", limit: 10000, ct: ct)
            .ConfigureAwait(false);

        var targetNodeIds = appliesToEdges
            .SelectMany(e => e.Roles.Where(r => r.Value == "target").Select(r => r.Key))
            .Distinct()
            .ToList();

        var allEntries = new List<ExpertiseEntry>();
        foreach (var targetNodeId in targetNodeIds)
        {
            var entries = await this.graphStore
                .GetExpertiseForNodeAsync(targetNodeId, ct: ct)
                .ConfigureAwait(false);
            allEntries.AddRange(entries);
        }

        return allEntries
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Two-tier candidate resolution that handles both in-memory stores (which
    /// preserve original string IDs) and Qdrant (which converts IDs to deterministic
    /// UUID v5, returning those UUIDs via <see cref="VectorSearchResult.Id"/>).
    ///
    /// Strategy 1: Direct ID lookup in the active entries dictionary.
    /// Strategy 2: Payload-based recovery using architecture-specified field
    ///   <c>TargetNodeId</c>, disambiguated by embedding cosine
    ///   proximity to the search result's score.
    /// </summary>
    private ExpertiseEntry? ResolveCandidateEntry(
        VectorSearchResult candidate,
        ExpertiseEntry queryEntry,
        Dictionary<string, ExpertiseEntry> entriesById,
        IReadOnlyList<ExpertiseEntry> allEntries,
        HashSet<string> alreadyMerged)
    {
        // Strategy 1: Direct ID match (non-Qdrant stores)
        if (entriesById.TryGetValue(candidate.Id, out var directMatch))
        {
            if (directMatch.Id == queryEntry.Id || alreadyMerged.Contains(directMatch.Id))
            {
                return null;
            }

            if (directMatch.Embedding is null)
            {
                return null;
            }

            return directMatch;
        }

        // Strategy 2: Payload-based recovery (Qdrant UUID v5 mapped stores).
        // Architecture.md §3.2.2 specifies payload = { Kind, TargetNodeId }.
        var candidateTarget = candidate.Payload.GetValueOrDefault("TargetNodeId")?.ToString();

        if (candidateTarget is null)
        {
            this.logger.LogDebug(
                "Candidate {CandidateId} has no TargetNodeId in payload; cannot resolve.",
                candidate.Id);
            return null;
        }

        // Find matching entries by target node, disambiguate by cosine distance.
        ExpertiseEntry? bestMatch = null;
        var bestCosineDiff = double.MaxValue;

        foreach (var entry in allEntries)
        {
            if (entry.Id == queryEntry.Id || alreadyMerged.Contains(entry.Id))
            {
                continue;
            }

            if (entry.Embedding is null)
            {
                continue;
            }

            if (entry.TargetNodeId != candidateTarget)
            {
                continue;
            }

            // The candidate.Score from vector search should closely match the
            // recomputed cosine between the query vector and this entry's embedding.
            var recomputedCosine = ComputeCosineSimilarity(queryEntry.Embedding!, entry.Embedding);
            var diff = Math.Abs(recomputedCosine - candidate.Score);
            if (diff < bestCosineDiff)
            {
                bestCosineDiff = diff;
                bestMatch = entry;
            }
        }

        if (bestMatch is not null && bestCosineDiff < 0.01)
        {
            return bestMatch;
        }

        this.logger.LogDebug(
            "Could not resolve candidate {CandidateId} to an active entry (bestDiff={BestDiff:F4}).",
            candidate.Id, bestCosineDiff);
        return null;
    }

    /// <summary>
    /// Persists a merge: upsert merged entry, vector, edges, archive originals, remove source vectors.
    /// Uses compensating rollback on failure, relying only on canonical
    /// <see cref="IMemoryGraphStore"/> methods (ArchiveExpertiseAsync, RetireEdgeAsync).
    /// </summary>
    private async Task PersistMergeAsync(
        ExpertiseEntry mergedEntry,
        ExpertiseEntry entryA,
        ExpertiseEntry entryB,
        double cosine,
        double threshold,
        CancellationToken ct)
    {
        // Phase 1: Upsert merged entry into graph store
        await this.graphStore.UpsertExpertiseAsync(mergedEntry, ct).ConfigureAwait(false);

        var vectorUpserted = false;
        var createdEdgeIds = new List<string>();
        var archivedSourceIds = new List<string>();
        var deletedSourceVectors = false;

        try
        {
            // Phase 2: Upsert merged entry embedding into vector store
            if (mergedEntry.Embedding is not null)
            {
                var point = new VectorPoint
                {
                    Id = mergedEntry.Id,
                    Vector = mergedEntry.Embedding,
                    Payload = new Dictionary<string, object>
                    {
                        ["TargetNodeId"] = mergedEntry.TargetNodeId,
                        ["Kind"] = mergedEntry.Kind.ToString()
                    }
                };

                await this.vectorStore.UpsertAsync(
                    ExpertiseCollectionName,
                    [point],
                    ct).ConfigureAwait(false);
                vectorUpserted = true;
            }

            // Phase 3: SUPERSEDES edges (merged -> original) with roles newer/older
            await this.CreateSupersedesEdgeAsync(mergedEntry.Id, entryA.Id, ct).ConfigureAwait(false);
            createdEdgeIds.Add($"edge:supersedes:{mergedEntry.Id}:{entryA.Id}");

            await this.CreateSupersedesEdgeAsync(mergedEntry.Id, entryB.Id, ct).ConfigureAwait(false);
            createdEdgeIds.Add($"edge:supersedes:{mergedEntry.Id}:{entryB.Id}");

            // Phase 4: DERIVED_FROM edges (merged -> original) with roles derived/source
            await this.CreateDerivedFromEdgeAsync(mergedEntry.Id, entryA.Id, ct).ConfigureAwait(false);
            createdEdgeIds.Add($"edge:derived_from:{mergedEntry.Id}:{entryA.Id}");

            await this.CreateDerivedFromEdgeAsync(mergedEntry.Id, entryB.Id, ct).ConfigureAwait(false);
            createdEdgeIds.Add($"edge:derived_from:{mergedEntry.Id}:{entryB.Id}");

            // Phase 4b: APPLIES_TO edge for expertise graph consistency
            await this.CreateAppliesToEdgeAsync(mergedEntry.Id, mergedEntry.TargetNodeId, ct).ConfigureAwait(false);
            createdEdgeIds.Add($"edge:applies_to:{mergedEntry.Id}:{mergedEntry.TargetNodeId}");

            // Phase 5: Archive original entries and retire their APPLIES_TO edges
            // so retrieval correctly filters archived expertise.
            await this.graphStore.ArchiveExpertiseAsync(entryA.Id, ct).ConfigureAwait(false);
            archivedSourceIds.Add(entryA.Id);
            await this.graphStore.RetireEdgeAsync($"edge:applies_to:{entryA.Id}:{entryA.TargetNodeId}", 0, ct).ConfigureAwait(false);

            await this.graphStore.ArchiveExpertiseAsync(entryB.Id, ct).ConfigureAwait(false);
            archivedSourceIds.Add(entryB.Id);
            await this.graphStore.RetireEdgeAsync($"edge:applies_to:{entryB.Id}:{entryB.TargetNodeId}", 0, ct).ConfigureAwait(false);

            // Phase 6: Remove source vectors
            await this.vectorStore.DeleteAsync(
                ExpertiseCollectionName,
                [entryA.Id, entryB.Id],
                ct).ConfigureAwait(false);
            deletedSourceVectors = true;

            // Audit log (non-critical; failure does NOT trigger rollback)
            try
            {
                await this.graphStore.LogAuditAsync(
                    "consolidation-service",
                    "consolidate-merge",
                    new
                    {
                        MergedId = mergedEntry.Id,
                        SourceIds = new[] { entryA.Id, entryB.Id },
                        CosineSimilarity = cosine,
                        Threshold = threshold
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception auditEx) when (auditEx is not OperationCanceledException)
            {
                this.logger.LogWarning(auditEx, "Audit log failed for merge {MergedId}.", mergedEntry.Id);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Persistence failed during merge of {MergedId}; executing compensating rollback.",
                mergedEntry.Id);

            await this.RollbackMergeAsync(
                mergedEntry.Id, vectorUpserted, createdEdgeIds,
                archivedSourceIds, deletedSourceVectors,
                entryA, entryB).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Compensating rollback using only canonical <see cref="IMemoryGraphStore"/> methods:
    /// <see cref="IMemoryGraphStore.ArchiveExpertiseAsync"/> and
    /// <see cref="IMemoryGraphStore.RetireEdgeAsync"/>.
    /// </summary>
    private async Task RollbackMergeAsync(
        string mergedId,
        bool vectorUpserted,
        List<string> createdEdgeIds,
        List<string> archivedSourceIds,
        bool deletedSourceVectors,
        ExpertiseEntry entryA,
        ExpertiseEntry entryB)
    {
        // 1. Re-activate archived source entries
        foreach (var sourceId in archivedSourceIds)
        {
            try
            {
                var sourceEntry = sourceId == entryA.Id ? entryA : entryB;
                await this.graphStore.UpsertExpertiseAsync(sourceEntry, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Rollback: failed to re-activate source entry {Id}.", sourceId);
            }
        }

        // 2. Re-insert source vectors if they were deleted
        if (deletedSourceVectors)
        {
            try
            {
                var sourcePoints = new List<VectorPoint>();
                foreach (var sourceEntry in new[] { entryA, entryB })
                {
                    if (sourceEntry.Embedding is not null)
                    {
                        sourcePoints.Add(new VectorPoint
                        {
                            Id = sourceEntry.Id,
                            Vector = sourceEntry.Embedding,
                            Payload = new Dictionary<string, object>
                            {
                                ["TargetNodeId"] = sourceEntry.TargetNodeId,
                                ["Kind"] = sourceEntry.Kind.ToString()
                            }
                        });
                    }
                }

                if (sourcePoints.Count > 0)
                {
                    await this.vectorStore.UpsertAsync(ExpertiseCollectionName, sourcePoints, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Rollback: failed to re-insert source vectors.");
            }
        }

        // 3. Retire created edges (canonical RetireEdgeAsync instead of DeleteEdgeAsync)
        foreach (var edgeId in createdEdgeIds)
        {
            try
            {
                await this.graphStore.RetireEdgeAsync(edgeId, 0, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Rollback: failed to retire edge {EdgeId}.", edgeId);
            }
        }

        // 4. Remove merged entry vector
        if (vectorUpserted)
        {
            try
            {
                await this.vectorStore.DeleteAsync(ExpertiseCollectionName, [mergedId], CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Rollback: failed to delete vector for merged entry {MergedId}.", mergedId);
            }
        }

        // 5. Archive the failed merged entry (canonical; no hard-delete needed)
        try
        {
            await this.graphStore.ArchiveExpertiseAsync(mergedId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Rollback: failed to archive merged entry {MergedId}.", mergedId);
        }

        this.logger.LogWarning(
            "Compensating rollback completed for merged entry {MergedId} (edges={EdgeCount}, archives={ArchiveCount}, vectorUpserted={VectorUpserted}, sourceVectorsDeleted={DeletedSourceVectors}).",
            mergedId, createdEdgeIds.Count, archivedSourceIds.Count, vectorUpserted, deletedSourceVectors);
    }

    /// <summary>
    /// Computes exact cosine similarity between two embedding vectors.
    /// </summary>
    internal static double ComputeCosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.0;
        }

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0.0 : dotProduct / denominator;
    }

    private async Task<MergedText?> GenerateMergedTextAsync(
        ExpertiseEntry entryA,
        ExpertiseEntry entryB,
        CancellationToken ct)
    {
        var modelName = this.options.ConsolidationModelName;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            this.logger.LogWarning(
                "MemoryOptions.ConsolidationModelName is not configured. " +
                "Set 'AgentMemory:ConsolidationModelName' in configuration to specify the Ollama model for consolidation. " +
                "Skipping LLM merge for this pair.");
            return null;
        }

        var prompt = $"""
            You are merging two semantically similar expertise entries about the same code.
            Produce a single merged entry that preserves all unique information from both.

            Entry A:
            Title: {entryA.Title}
            Body: {entryA.Body}

            Entry B:
            Title: {entryB.Title}
            Body: {entryB.Body}

            Respond with EXACTLY this format (no extra text):
            TITLE: <merged title>
            BODY: <merged body>
            """;

        try
        {
            var client = this.httpClientFactory.CreateClient("OllamaConsolidation");
            var endpoint = this.options.OllamaEndpoint.TrimEnd('/');
            var requestBody = new OllamaGenerateRequest
            {
                Model = modelName,
                Prompt = prompt,
                Stream = false
            };

            var response = await client.PostAsJsonAsync(
                $"{endpoint}/api/generate",
                requestBody,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning(
                    "Ollama /api/generate returned {StatusCode}.",
                    response.StatusCode);
                return null;
            }

            var result = await response.Content
                .ReadFromJsonAsync<OllamaGenerateResponse>(ct)
                .ConfigureAwait(false);

            if (result?.Response is null)
            {
                return null;
            }

            return ParseMergedText(result.Response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            this.logger.LogWarning(ex, "Ollama LLM call failed during consolidation.");
            return null;
        }
    }

    private static MergedText? ParseMergedText(string llmResponse)
    {
        var lines = llmResponse.Split(new[] { '\n' }, StringSplitOptions.TrimEntries);
        string? title = null;
        var bodyLines = new List<string>();
        var inBody = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
            {
                title = line.Substring("TITLE:".Length).Trim();
            }
            else if (line.StartsWith("BODY:", StringComparison.OrdinalIgnoreCase))
            {
                inBody = true;
                var bodyStart = line.Substring("BODY:".Length).Trim();
                if (!string.IsNullOrEmpty(bodyStart))
                {
                    bodyLines.Add(bodyStart);
                }
            }
            else if (inBody)
            {
                bodyLines.Add(line);
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var body = string.Join('\n', bodyLines).Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return new MergedText(title, body);
    }

    private async Task CreateSupersedesEdgeAsync(string newerId, string olderId, CancellationToken ct)
    {
        var edgeId = $"edge:supersedes:{newerId}:{olderId}";
        var edge = new Hyperedge
        {
            Id = edgeId,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = [newerId, olderId],
            Roles = new Dictionary<string, string>
            {
                [newerId] = "newer",
                [olderId] = "older"
            },
            Metadata = new Dictionary<string, object?>
            {
                ["SemanticRelation"] = "SUPERSEDES",
                ["MemoryLayer"] = "expertise"
            }
        };

        await this.graphStore.CreateEdgeAsync(edge, ct).ConfigureAwait(false);
    }

    private async Task CreateDerivedFromEdgeAsync(string derivedId, string sourceId, CancellationToken ct)
    {
        var edgeId = $"edge:derived_from:{derivedId}:{sourceId}";
        var edge = new Hyperedge
        {
            Id = edgeId,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = [derivedId, sourceId],
            Roles = new Dictionary<string, string>
            {
                [derivedId] = "derived",
                [sourceId] = "source"
            },
            Metadata = new Dictionary<string, object?>
            {
                ["SemanticRelation"] = "DERIVED_FROM",
                ["MemoryLayer"] = "expertise"
            }
        };

        await this.graphStore.CreateEdgeAsync(edge, ct).ConfigureAwait(false);
    }

    private async Task CreateAppliesToEdgeAsync(string expertiseId, string targetNodeId, CancellationToken ct)
    {
        var edgeId = $"edge:applies_to:{expertiseId}:{targetNodeId}";
        var edge = new Hyperedge
        {
            Id = edgeId,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = [expertiseId, targetNodeId],
            Roles = new Dictionary<string, string>
            {
                [expertiseId] = "expertise",
                [targetNodeId] = "target"
            },
            Metadata = new Dictionary<string, object?>
            {
                ["SemanticRelation"] = "APPLIES_TO",
                ["MemoryLayer"] = "expertise"
            }
        };

        await this.graphStore.CreateEdgeAsync(edge, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<Citation>? CombineEvidence(
        IReadOnlyList<Citation>? a,
        IReadOnlyList<Citation>? b)
    {
        if (a is null && b is null)
        {
            return null;
        }

        var combined = new List<Citation>();
        if (a is not null)
        {
            combined.AddRange(a);
        }

        if (b is not null)
        {
            combined.AddRange(b);
        }

        return combined.Count > 0 ? combined : null;
    }

    private sealed record MergedText(string Title, string Body);

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }
    }
}