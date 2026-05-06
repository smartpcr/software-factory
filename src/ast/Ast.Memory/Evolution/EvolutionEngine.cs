// -----------------------------------------------------------------------
// <copyright file="EvolutionEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Implements expertise write-back, consolidation, decay, and community
/// detection for the memory graph evolution pipeline.
/// </summary>
public class EvolutionEngine : IEvolutionEngine
{
    private static readonly HashSet<string> ValidScopes = ["exact", "subtree"];

    private static readonly HyperedgeType[] CallEdgeTypes =
    [
        HyperedgeType.MethodInvocation,
        HyperedgeType.ConstructorInvocation,
        HyperedgeType.CmdletCall,
        HyperedgeType.ModuleFunctionCall,
        HyperedgeType.CrossLanguageMethodCall
    ];

    private readonly IMemoryGraphStore graphStore;
    private readonly IEmbeddingGenerator embeddingGenerator;
    private readonly IVectorStore vectorStore;
    private readonly IExpertiseMergeService mergeService;
    private readonly MemoryOptions options;
    private readonly ILogger<EvolutionEngine> logger;

    public EvolutionEngine(
        IMemoryGraphStore graphStore,
        IEmbeddingGenerator embeddingGenerator,
        IVectorStore vectorStore,
        IExpertiseMergeService mergeService,
        IOptions<MemoryOptions> options,
        ILogger<EvolutionEngine> logger)
    {
        this.graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        this.embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this.vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        this.mergeService = mergeService ?? throw new ArgumentNullException(nameof(mergeService));
        this.options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ExpertiseEntry> RecordExpertiseAsync(
        string targetNodeId,
        ExpertiseKind kind,
        string title,
        string body,
        string sourceAgent,
        string scope = "exact",
        IReadOnlyList<Citation>? evidence = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAgent);

        if (!ValidScopes.Contains(scope))
        {
            throw new ArgumentException($"Scope must be 'exact' or 'subtree', got '{scope}'.", nameof(scope));
        }

        // Validate target node exists — architecture §6.3: throw ArgumentException
        var targetNode = await this.graphStore.GetNodeAsync(targetNodeId, ct).ConfigureAwait(false);
        if (targetNode == null)
        {
            throw new ArgumentException($"Target node '{targetNodeId}' does not exist in the graph store.", nameof(targetNodeId));
        }

        // Deduplicate via full ContentHash (SHA-256)
        var contentHash = ExpertiseEntry.ComputeContentHash(title, body, targetNodeId);
        var existingEntry = await this.graphStore.GetExpertiseByContentHashAsync(contentHash, ct).ConfigureAwait(false);
        if (existingEntry != null)
        {
            this.logger.LogInformation(
                "Duplicate expertise detected (ContentHash={ContentHash}), returning existing entry.",
                contentHash);

            // Audit the duplicate write-back attempt
            await this.graphStore.LogAuditAsync(
                sourceAgent,
                "RecordExpertise_Duplicate",
                new
                {
                    ExistingExpertiseId = existingEntry.Id,
                    ContentHash = contentHash,
                    TargetNodeId = targetNodeId,
                    Kind = kind.ToString()
                },
                ct).ConfigureAwait(false);

            return existingEntry;
        }

        // Generate embedding — must succeed; failures propagate to caller
        var embeddingText = title + " " + body;
        var embedding = await this.embeddingGenerator.GenerateEmbeddingAsync(embeddingText, ct).ConfigureAwait(false);

        var expertiseId = ExpertiseEntry.GenerateId(kind, title, body, targetNodeId);
        var now = DateTime.UtcNow;
        var entry = new ExpertiseEntry
        {
            Id = expertiseId,
            Kind = kind,
            Title = title,
            Body = body,
            TargetNodeId = targetNodeId,
            Scope = scope,
            Confidence = 1.0,
            Archived = false,
            SourceAgent = sourceAgent,
            Evidence = evidence ?? [],
            Embedding = embedding,
            ContentHash = contentHash,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Persist the expertise entry first
        await this.graphStore.UpsertExpertiseAsync(entry, ct).ConfigureAwait(false);

        try
        {
            // Create APPLIES_TO edge linking expertise to target code node
            var edgeId = $"edge:applies_to:{expertiseId}";
            var appliesToEdge = new Hyperedge
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
                    [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
                    [MemoryMetadataKeys.MemoryLayer] = "expertise"
                }
            };

            await this.graphStore.CreateEdgeAsync(appliesToEdge, ct).ConfigureAwait(false);

            // Upsert vector to the expertise collection
            var vectorCollection = this.options.ExpertiseVectorCollection;
            var collectionExists = await this.vectorStore.CollectionExistsAsync(vectorCollection, ct).ConfigureAwait(false);
            if (!collectionExists)
            {
                await this.vectorStore.CreateCollectionAsync(vectorCollection, embedding.Length, ct).ConfigureAwait(false);
            }

            var vectorPoint = new VectorPoint
            {
                Id = expertiseId,
                Vector = embedding,
                Payload = new Dictionary<string, object>
                {
                    ["Kind"] = kind.ToString(),
                    ["TargetNodeId"] = targetNodeId
                }
            };
            await this.vectorStore.UpsertAsync(vectorCollection, [vectorPoint], ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Roll back the persisted expertise to avoid partial state
            this.logger.LogError(
                ex,
                "Failed to persist edge/vector for expertise {ExpertiseId}; rolling back expertise entry.",
                expertiseId);

            await this.graphStore.ArchiveExpertiseAsync(expertiseId, ct).ConfigureAwait(false);
            throw;
        }

        // Log audit
        await this.graphStore.LogAuditAsync(
            sourceAgent,
            "RecordExpertise",
            new
            {
                ExpertiseId = expertiseId,
                Kind = kind.ToString(),
                Title = title,
                TargetNodeId = targetNodeId,
                Scope = scope,
                ContentHash = contentHash
            },
            ct).ConfigureAwait(false);

        this.logger.LogInformation(
            "Recorded expertise {ExpertiseId} (Kind={Kind}) targeting {TargetNodeId}.",
            expertiseId, kind, targetNodeId);

        return entry;
    }

    /// <inheritdoc />
    public async Task<ConsolidationResult> ConsolidateAsync(
        CancellationToken ct = default)
    {
        var allExpertise = await this.graphStore.GetAllActiveExpertiseAsync(ct: ct).ConfigureAwait(false);
        if (allExpertise.Count < 2)
        {
            return new ConsolidationResult(CandidatesPaired: 0, MergedCount: 0, SupersededCount: 0, MergedEntryIds: []);
        }

        var mergedCount = 0;
        var candidatesPaired = 0;
        var supersededCount = 0;
        var mergedEntryIds = new List<string>();
        var supersededIds = new HashSet<string>();
        var vectorCollection = this.options.ExpertiseVectorCollection;
        var similarityThreshold = this.options.SimilarityThreshold;
        var maxMerges = this.options.MaxMergesPerRun;

        foreach (var entry in allExpertise)
        {
            if (mergedCount >= maxMerges)
            {
                break;
            }

            if (supersededIds.Contains(entry.Id) || entry.Embedding == null)
            {
                continue;
            }

            // Search for similar entries via vector store
            var collectionExists = await this.vectorStore.CollectionExistsAsync(vectorCollection, ct).ConfigureAwait(false);
            if (!collectionExists)
            {
                break;
            }

            var similar = await this.vectorStore.SearchAsync(
                vectorCollection,
                entry.Embedding,
                limit: 10,
                cancellationToken: ct).ConfigureAwait(false);

            // Collect candidates for this merge batch
            var candidatesForMerge = new List<ExpertiseEntry>();
            foreach (var match in similar)
            {
                if (match.Id == entry.Id || match.Score < similarityThreshold || supersededIds.Contains(match.Id))
                {
                    continue;
                }

                var matchedEntry = await this.graphStore.GetExpertiseByIdAsync(match.Id, ct).ConfigureAwait(false);
                if (matchedEntry == null || matchedEntry.Archived)
                {
                    continue;
                }

                candidatesForMerge.Add(matchedEntry);
                candidatesPaired++;
            }

            if (candidatesForMerge.Count == 0)
            {
                continue;
            }

            // LLM-powered merge: create a merged entry from the current entry and all its similar candidates
            var mergedEntry = entry;
            foreach (var candidate in candidatesForMerge)
            {
                if (mergedCount >= maxMerges || supersededIds.Contains(candidate.Id))
                {
                    continue;
                }

                // LLM-merge the two entries
                var (mergedTitle, mergedBody) = await this.mergeService.MergeExpertiseAsync(
                    mergedEntry, candidate, ct).ConfigureAwait(false);

                // Create the new merged expertise entry
                var mergedContentHash = ExpertiseEntry.ComputeContentHash(mergedTitle, mergedBody, entry.TargetNodeId);
                var mergedId = ExpertiseEntry.GenerateId(entry.Kind, mergedTitle, mergedBody, entry.TargetNodeId);
                var now = DateTime.UtcNow;
                var embeddingText = mergedTitle + " " + mergedBody;
                var mergedEmbedding = await this.embeddingGenerator.GenerateEmbeddingAsync(embeddingText, ct).ConfigureAwait(false);

                var newMergedEntry = new ExpertiseEntry
                {
                    Id = mergedId,
                    Kind = entry.Kind,
                    Title = mergedTitle,
                    Body = mergedBody,
                    TargetNodeId = entry.TargetNodeId,
                    Scope = entry.Scope,
                    Confidence = Math.Max(mergedEntry.Confidence, candidate.Confidence),
                    Archived = false,
                    SourceAgent = entry.SourceAgent,
                    Evidence = mergedEntry.Evidence.Concat(candidate.Evidence).ToList(),
                    Embedding = mergedEmbedding,
                    ContentHash = mergedContentHash,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                await this.graphStore.UpsertExpertiseAsync(newMergedEntry, ct).ConfigureAwait(false);

                // Create SUPERSEDES edges: merged (newer) → original entries (older)
                var supersedesEdgeA = new Hyperedge
                {
                    Id = $"edge:supersedes:{mergedId}:{mergedEntry.Id}",
                    EdgeType = HyperedgeType.MemoryRelation,
                    NodeIds = [mergedId, mergedEntry.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [mergedId] = "newer",
                        [mergedEntry.Id] = "older"
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.SemanticRelation] = "SUPERSEDES",
                        [MemoryMetadataKeys.MemoryLayer] = "expertise"
                    }
                };
                await this.graphStore.CreateEdgeAsync(supersedesEdgeA, ct).ConfigureAwait(false);

                var supersedesEdgeB = new Hyperedge
                {
                    Id = $"edge:supersedes:{mergedId}:{candidate.Id}",
                    EdgeType = HyperedgeType.MemoryRelation,
                    NodeIds = [mergedId, candidate.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [mergedId] = "newer",
                        [candidate.Id] = "older"
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.SemanticRelation] = "SUPERSEDES",
                        [MemoryMetadataKeys.MemoryLayer] = "expertise"
                    }
                };
                await this.graphStore.CreateEdgeAsync(supersedesEdgeB, ct).ConfigureAwait(false);

                // Archive both originals
                if (!supersededIds.Contains(mergedEntry.Id))
                {
                    await this.graphStore.ArchiveExpertiseAsync(mergedEntry.Id, ct).ConfigureAwait(false);
                    supersededIds.Add(mergedEntry.Id);
                    supersededCount++;
                }

                await this.graphStore.ArchiveExpertiseAsync(candidate.Id, ct).ConfigureAwait(false);
                supersededIds.Add(candidate.Id);
                supersededCount++;

                mergedEntryIds.Add(mergedId);
                mergedEntry = newMergedEntry;
                mergedCount++;
            }
        }

        await this.graphStore.LogAuditAsync(
            "system",
            "Consolidate",
            new { CandidatesPaired = candidatesPaired, MergedCount = mergedCount, SupersededCount = supersededCount, MergedEntryIds = mergedEntryIds },
            ct).ConfigureAwait(false);

        this.logger.LogInformation(
            "Consolidation complete: {CandidatesPaired} paired, {MergedCount} merged, {SupersededCount} superseded.",
            candidatesPaired, mergedCount, supersededCount);

        return new ConsolidationResult(CandidatesPaired: candidatesPaired, MergedCount: mergedCount, SupersededCount: supersededCount, MergedEntryIds: mergedEntryIds);
    }

    /// <inheritdoc />
    public async Task<DecayResult> DecayStaleExpertiseAsync(
        CancellationToken ct = default)
    {
        // Per architecture §3.5.1: time-based decay is transient/idempotent.
        // stored_confidence is NEVER modified by time-based decay.
        // effective_confidence = stored_confidence × time_factor is computed on-read.
        // Only action: archive entries whose effective_confidence < ArchiveConfidenceFloor.
        var allExpertise = await this.graphStore.GetAllActiveExpertiseAsync(ct: ct).ConfigureAwait(false);

        var entriesEvaluated = allExpertise.Count;
        var entriesDecayed = 0;
        var entriesArchived = 0;
        var now = DateTime.UtcNow;
        var halfLifeDays = this.options.DecayHalfLifeDays;
        var archiveFloor = this.options.ArchiveConfidenceFloor;

        foreach (var entry in allExpertise)
        {
            var ageInDays = (now - entry.CreatedAt).TotalDays;
            if (ageInDays <= 0)
            {
                continue;
            }

            // time_factor = 2^(-age_days / effective_half_life)
            // Without PageRank available, centrality_boost = 1.0 (no boost)
            var timeFactor = Math.Pow(2.0, -ageInDays / halfLifeDays);

            // effective_confidence is transient — never persisted
            var effectiveConfidence = entry.Confidence * timeFactor;

            if (effectiveConfidence < entry.Confidence)
            {
                entriesDecayed++;
            }

            if (effectiveConfidence < archiveFloor)
            {
                // Archive via ArchiveExpertiseAsync — the only persistence action
                await this.graphStore.ArchiveExpertiseAsync(entry.Id, ct).ConfigureAwait(false);
                entriesArchived++;
            }
            // Else: no persistence needed. stored_confidence remains unchanged.
        }

        await this.graphStore.LogAuditAsync(
            "system",
            "DecayStaleExpertise",
            new { EntriesEvaluated = entriesEvaluated, EntriesDecayed = entriesDecayed, EntriesArchived = entriesArchived },
            ct).ConfigureAwait(false);

        this.logger.LogInformation(
            "Decay complete: {EntriesEvaluated} evaluated, {EntriesDecayed} decayed, {EntriesArchived} archived.",
            entriesEvaluated, entriesDecayed, entriesArchived);

        return new DecayResult(EntriesEvaluated: entriesEvaluated, EntriesDecayed: entriesDecayed, EntriesArchived: entriesArchived);
    }

    /// <inheritdoc />
    public async Task<ExpertiseEntry> RestoreArchivedExpertiseAsync(
        string expertiseId,
        double restoredConfidence = 0.5,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expertiseId);

        if (restoredConfidence < 0.0 || restoredConfidence > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(restoredConfidence), "Confidence must be between 0.0 and 1.0.");
        }

        var entry = await this.graphStore.GetExpertiseByIdAsync(expertiseId, ct).ConfigureAwait(false);
        if (entry == null)
        {
            throw new InvalidOperationException($"Expertise entry '{expertiseId}' not found.");
        }

        if (!entry.Archived)
        {
            this.logger.LogWarning("Expertise {ExpertiseId} is not archived; returning as-is.", expertiseId);

            // Audit even for no-op restores
            await this.graphStore.LogAuditAsync(
                "system",
                "RestoreArchivedExpertise_AlreadyActive",
                new
                {
                    ExpertiseId = expertiseId,
                    CurrentConfidence = entry.Confidence
                },
                ct).ConfigureAwait(false);

            return entry;
        }

        // Restore: un-archive and set new confidence
        entry.Archived = false;
        entry.Confidence = restoredConfidence;
        entry.UpdatedAt = DateTime.UtcNow;

        await this.graphStore.UpsertExpertiseAsync(entry, ct).ConfigureAwait(false);

        // Log audit
        await this.graphStore.LogAuditAsync(
            "system",
            "RestoreArchivedExpertise",
            new
            {
                ExpertiseId = expertiseId,
                RestoredConfidence = restoredConfidence
            },
            ct).ConfigureAwait(false);

        this.logger.LogInformation(
            "Restored archived expertise {ExpertiseId} with confidence {Confidence}.",
            expertiseId, restoredConfidence);

        return entry;
    }

    /// <inheritdoc />
    public async Task<CommunityResult> DetectCommunitiesAsync(
        CancellationToken ct = default)
    {
        // Build undirected adjacency from CALLS and SIMILAR_TO edges (Louvain input)
        var adjacency = new Dictionary<string, HashSet<string>>();

        // 1. Gather SIMILAR_TO edges
        var similarEdges = await this.graphStore.GetEdgesBySemanticRelationAsync(
            "SIMILAR_TO", limit: 10000, ct: ct).ConfigureAwait(false);
        foreach (var edge in similarEdges)
        {
            if (edge.NodeIds.Count < 2)
            {
                continue;
            }

            var nodeA = edge.NodeIds[0];
            var nodeB = edge.NodeIds[1];
            AddUndirectedEdge(adjacency, nodeA, nodeB);
        }

        // 2. Gather CALLS edges by iterating nodes
        var allNodes = await this.graphStore.GetNodesAsync(limit: 10000, cancellationToken: ct).ConfigureAwait(false);
        foreach (var node in allNodes)
        {
            var callEdges = await this.graphStore.GetEdgesForNodeAsync(
                node.Id, CallEdgeTypes, ct).ConfigureAwait(false);
            foreach (var edge in callEdges)
            {
                foreach (var otherId in edge.NodeIds)
                {
                    if (otherId != node.Id)
                    {
                        AddUndirectedEdge(adjacency, node.Id, otherId);
                    }
                }
            }
        }

        if (adjacency.Count == 0)
        {
            return new CommunityResult(CommunitiesDetected: 0, TopicNodesCreated: 0, ClusterEdgesCreated: 0);
        }

        // 3. Run Louvain community detection
        var communityAssignment = LouvainCommunityDetector.Detect(adjacency);

        // Group nodes by community
        var communities = new Dictionary<int, List<string>>();
        foreach (var (nodeId, communityId) in communityAssignment)
        {
            if (!communities.TryGetValue(communityId, out var list))
            {
                list = [];
                communities[communityId] = list;
            }

            list.Add(nodeId);
        }

        // Only keep communities with ≥2 members
        var validCommunities = communities
            .Where(c => c.Value.Count >= 2)
            .ToList();

        var communitiesDetected = validCommunities.Count;
        var topicNodesCreated = 0;
        var clusterEdgesCreated = 0;

        foreach (var (communityId, members) in validCommunities)
        {
            // Topic ID: topic:{hash8}
            var communityKey = string.Join(",", members.OrderBy(m => m));
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(communityKey));
            var hash8 = Convert.ToHexString(hashBytes).ToLowerInvariant()[..8];
            var topicNodeId = $"topic:{hash8}";

            var topicNode = new HypergraphNode
            {
                Id = topicNodeId,
                Name = $"Topic cluster ({members.Count} nodes)",
                NodeType = "Topic",
                Language = "none",
                FilePath = string.Empty,
                Metadata = new Dictionary<string, object?>
                {
                    [MemoryMetadataKeys.MemoryLayer] = "semantic",
                    [MemoryMetadataKeys.MemberCount] = members.Count,
                    [MemoryMetadataKeys.Summary] = $"Louvain community of {members.Count} nodes"
                }
            };

            await this.graphStore.CreateNodeAsync(topicNode, ct).ConfigureAwait(false);
            topicNodesCreated++;

            // Create BELONGS_TO_CLUSTER edges
            foreach (var memberId in members)
            {
                var clusterEdge = new Hyperedge
                {
                    Id = $"edge:belongs_to_cluster:{memberId}:{topicNodeId}",
                    EdgeType = HyperedgeType.MemoryRelation,
                    NodeIds = [memberId, topicNodeId],
                    Roles = new Dictionary<string, string>
                    {
                        [memberId] = "member",
                        [topicNodeId] = "cluster"
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.SemanticRelation] = "BELONGS_TO_CLUSTER",
                        [MemoryMetadataKeys.MemoryLayer] = "semantic"
                    }
                };

                await this.graphStore.CreateEdgeAsync(clusterEdge, ct).ConfigureAwait(false);
                clusterEdgesCreated++;
            }
        }

        await this.graphStore.LogAuditAsync(
            "system",
            "DetectCommunities",
            new { CommunitiesDetected = communitiesDetected, TopicNodesCreated = topicNodesCreated, ClusterEdgesCreated = clusterEdgesCreated },
            ct).ConfigureAwait(false);

        this.logger.LogInformation(
            "Community detection complete: {Communities} communities, {Topics} topic nodes, {ClusterEdges} cluster edges.",
            communitiesDetected, topicNodesCreated, clusterEdgesCreated);

        return new CommunityResult(CommunitiesDetected: communitiesDetected, TopicNodesCreated: topicNodesCreated, ClusterEdgesCreated: clusterEdgesCreated);
    }

    private static void AddUndirectedEdge(Dictionary<string, HashSet<string>> adjacency, string a, string b)
    {
        if (!adjacency.TryGetValue(a, out var neighborsA))
        {
            neighborsA = [];
            adjacency[a] = neighborsA;
        }

        neighborsA.Add(b);

        if (!adjacency.TryGetValue(b, out var neighborsB))
        {
            neighborsB = [];
            adjacency[b] = neighborsB;
        }

        neighborsB.Add(a);
    }
}