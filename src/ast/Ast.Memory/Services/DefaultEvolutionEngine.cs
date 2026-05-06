// -----------------------------------------------------------------------
// <copyright file="DefaultEvolutionEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Services;

using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// Default evolution engine for expertise consolidation, decay, and community detection.
/// Uses <see cref="IEmbeddingGenerator"/> for vector-backed similarity when available,
/// falling back to Jaccard word-overlap otherwise.
/// </summary>
public sealed class DefaultEvolutionEngine : IEvolutionEngine
{
    private readonly IMemoryGraphStore store;
    private readonly IEmbeddingGenerator? embeddingGenerator;

    public DefaultEvolutionEngine(IMemoryGraphStore store, IEmbeddingGenerator? embeddingGenerator = null)
    {
        this.store = store;
        this.embeddingGenerator = embeddingGenerator;
    }

    /// <inheritdoc/>
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
        var entry = new ExpertiseEntry
        {
            Id = ExpertiseEntry.GenerateId(kind, title, body, targetNodeId),
            Kind = kind,
            Title = title,
            Body = body,
            TargetNodeId = targetNodeId,
            Scope = scope,
            SourceAgent = sourceAgent,
            Evidence = evidence ?? Array.Empty<Citation>(),
        };

        await this.store.UpsertExpertiseAsync(entry, ct);
        return entry;
    }

    /// <inheritdoc/>
    public async Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationOptions options,
        CancellationToken ct = default)
    {
        // Gather all non-archived expertise entries
        var allExpertise = new List<ExpertiseEntry>();

        var layers = new[] { "structural", "call_chain", "semantic", "expertise" };
        var seenNodeIds = new HashSet<string>();

        foreach (var layer in layers)
        {
            var nodes = await this.store.GetNodesByLayerAsync(layer, limit: 10000, ct: ct);
            foreach (var node in nodes)
            {
                if (seenNodeIds.Add(node.Id))
                {
                    var entries = await this.store.GetExpertiseForNodeAsync(node.Id, ct: ct);
                    allExpertise.AddRange(entries);
                }
            }
        }

        // Generate embeddings for expertise entries if embedding generator is available
        var embeddings = new Dictionary<string, float[]>();
        if (this.embeddingGenerator != null)
        {
            try
            {
                var available = await this.embeddingGenerator.IsAvailableAsync(ct);
                if (available)
                {
                    var texts = allExpertise.Select(e => $"{e.Title} {e.Body}").ToList();
                    if (texts.Count > 0)
                    {
                        var batchEmbeddings = await this.embeddingGenerator.GenerateBatchEmbeddingsAsync(texts, ct);
                        for (int i = 0; i < allExpertise.Count && i < batchEmbeddings.Count; i++)
                        {
                            embeddings[allExpertise[i].Id] = batchEmbeddings[i];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Embedding generator unavailable; log and fall back to Jaccard
                await this.store.LogAuditAsync(
                    "cli", "consolidate-embedding-fallback",
                    new { error = ex.Message }, ct);
            }
        }

        var candidatesPaired = 0;
        var merged = new List<string>();

        // Compare expertise entries pairwise within the same target node
        var byTarget = allExpertise.GroupBy(e => e.TargetNodeId);
        foreach (var group in byTarget)
        {
            ct.ThrowIfCancellationRequested();
            var entries = group.ToList();

            for (int i = 0; i < entries.Count && merged.Count < options.MaxMergesPerRun; i++)
            {
                for (int j = i + 1; j < entries.Count && merged.Count < options.MaxMergesPerRun; j++)
                {
                    if (merged.Contains(entries[j].Id))
                    {
                        continue;
                    }

                    double similarity;
                    if (embeddings.TryGetValue(entries[i].Id, out var embI) && embeddings.TryGetValue(entries[j].Id, out var embJ))
                    {
                        similarity = CosineSimilarity(embI, embJ);
                    }
                    else
                    {
                        similarity = ComputeTitleSimilarity(entries[i].Title, entries[j].Title);
                    }

                    candidatesPaired++;

                    if (similarity >= options.SimilarityThreshold)
                    {
                        // Merge j into i: keep the one with higher confidence
                        var keeper = entries[i].Confidence >= entries[j].Confidence ? entries[i] : entries[j];
                        var loser = keeper == entries[i] ? entries[j] : entries[i];

                        keeper.Confidence = Math.Min(1.0, keeper.Confidence + loser.Confidence * 0.3);
                        keeper.UpdatedAt = DateTimeOffset.UtcNow;
                        await this.store.UpsertExpertiseAsync(keeper, ct);

                        // Archive the loser (temporal retirement — not deletion)
                        await this.store.ArchiveExpertiseAsync(loser.Id, ct);
                        merged.Add(loser.Id);
                    }
                }
            }
        }

        await this.store.LogAuditAsync(
            "cli", "consolidate",
            new { candidatesPaired, mergedCount = merged.Count, usedEmbeddings = embeddings.Count > 0 }, ct);

        return new ConsolidationResult(
            CandidatesPaired: candidatesPaired,
            MergedCount: merged.Count,
            MergedEntryIds: merged);
    }

    /// <inheritdoc/>
    public async Task<DecayResult> DecayStaleExpertiseAsync(
        DecayOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new DecayOptions();
        var halfLife = TimeSpan.FromDays(options.HalfLifeDays);
        var now = DateTimeOffset.UtcNow;
        var evaluated = 0;
        var decayed = 0;
        var archived = 0;

        var layers = new[] { "structural", "call_chain", "semantic", "expertise" };
        var seenNodeIds = new HashSet<string>();

        foreach (var layer in layers)
        {
            var nodes = await this.store.GetNodesByLayerAsync(layer, limit: 10000, ct: ct);
            foreach (var node in nodes)
            {
                if (!seenNodeIds.Add(node.Id))
                {
                    continue;
                }

                var entries = await this.store.GetExpertiseForNodeAsync(node.Id, ct: ct);
                foreach (var entry in entries)
                {
                    evaluated++;
                    var age = now - entry.UpdatedAt;
                    var decayFactor = Math.Pow(0.5, age.TotalDays / halfLife.TotalDays);
                    var newConfidence = entry.Confidence * decayFactor;

                    if (newConfidence < options.ArchiveConfidenceFloor)
                    {
                        await this.store.ArchiveExpertiseAsync(entry.Id, ct);
                        archived++;
                    }
                    else if (Math.Abs(newConfidence - entry.Confidence) > 0.001)
                    {
                        entry.Confidence = newConfidence;
                        entry.UpdatedAt = now;
                        await this.store.UpsertExpertiseAsync(entry, ct);
                        decayed++;
                    }
                }
            }
        }

        return new DecayResult(
            EntriesEvaluated: evaluated,
            EntriesDecayed: decayed,
            EntriesArchived: archived);
    }

    /// <inheritdoc/>
    public async Task RestoreArchivedExpertiseAsync(
        string expertiseId,
        double restoredConfidence = 0.5,
        CancellationToken ct = default)
    {
        var archived = await this.store.GetArchivedExpertiseAsync(limit: 10000, ct: ct);
        var entry = archived.FirstOrDefault(e => e.Id == expertiseId);
        if (entry != null)
        {
            entry.Archived = false;
            entry.Confidence = restoredConfidence;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            await this.store.UpsertExpertiseAsync(entry, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<CommunityResult> DetectCommunitiesAsync(CancellationToken ct = default)
    {
        // Basic label-propagation community detection:
        // Group nodes by their containing directory/namespace as a community proxy.
        var allNodes = new List<HypergraphNode>();
        var layers = new[] { "structural", "call_chain" };
        var seenNodeIds = new HashSet<string>();

        foreach (var layer in layers)
        {
            var nodes = await this.store.GetNodesByLayerAsync(layer, limit: 10000, ct: ct);
            foreach (var node in nodes)
            {
                if (seenNodeIds.Add(node.Id))
                {
                    allNodes.Add(node);
                }
            }
        }

        // Group by directory path as a simple community signal
        var communities = allNodes
            .Where(n => !string.IsNullOrEmpty(n.FilePath))
            .GroupBy(n =>
            {
                var dir = Path.GetDirectoryName(n.FilePath);
                return string.IsNullOrEmpty(dir) ? "(root)" : dir;
            })
            .Where(g => g.Count() >= 2)
            .ToList();

        var topicNodesCreated = 0;
        var clusterEdgesCreated = 0;

        foreach (var community in communities)
        {
            ct.ThrowIfCancellationRequested();

            var topicId = $"topic:{community.Key}";
            var topicNode = new HypergraphNode
            {
                Id = topicId,
                Name = $"Community: {Path.GetFileName(community.Key.TrimEnd(Path.DirectorySeparatorChar))}",
                NodeType = "Topic",
                Language = "mixed",
                FilePath = community.Key,
                Metadata = new Dictionary<string, object?>
                {
                    [MemoryMetadataKeys.MemoryLayer] = "semantic",
                    ["memberCount"] = community.Count(),
                },
            };
            await this.store.UpsertNodeAsync(topicNode, new VersionRange("community-detection", 0), ct);
            topicNodesCreated++;

            foreach (var member in community)
            {
                var clusterEdge = new Hyperedge
                {
                    Id = $"edge:cluster:{topicId}→{member.Id}",
                    EdgeType = HyperedgeType.MemoryRelation,
                    NodeIds = new List<string> { topicId, member.Id },
                    Roles = new Dictionary<string, string>
                    {
                        [topicId] = "topic",
                        [member.Id] = "member",
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.MemoryLayer] = "semantic",
                        [MemoryMetadataKeys.SemanticRelation] = "CLUSTER_MEMBER",
                    },
                };
                await this.store.CreateEdgeAsync(clusterEdge, ct);
                clusterEdgesCreated++;
            }
        }

        await this.store.LogAuditAsync(
            "cli", "detect-communities",
            new { communitiesDetected = communities.Count, topicNodesCreated, clusterEdgesCreated }, ct);

        return new CommunityResult(
            CommunitiesDetected: communities.Count,
            TopicNodesCreated: topicNodesCreated,
            ClusterEdgesCreated: clusterEdgesCreated);
    }

    private static double ComputeTitleSimilarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        var wordsA = a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (wordsA.Count == 0 || wordsB.Count == 0)
        {
            return 0.0;
        }

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return (double)intersection / union;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.0;
        }

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denominator < 1e-10 ? 0.0 : dot / denominator;
    }
}
