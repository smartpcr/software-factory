// -----------------------------------------------------------------------
// <copyright file="InMemoryEvolutionEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.InMemory;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// In-memory evolution engine implementing architecture.md §6.3 write-back semantics:
/// validates target node, deduplicates, persists expertise, creates APPLIES_TO edge,
/// and logs the audit trail.
/// </summary>
public sealed class InMemoryEvolutionEngine : IEvolutionEngine
{
    private readonly IMemoryGraphStore graphStore;

    public InMemoryEvolutionEngine(IMemoryGraphStore graphStore)
    {
        this.graphStore = graphStore;
    }

    /// <summary>
    /// Records expertise per §6.3: validate → compute ID → deduplicate → persist → edge → audit.
    /// </summary>
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
        // §6.3 Step 1: Validate targetNodeId exists in graph
        var targetNode = await this.graphStore.GetNodeAsync(targetNodeId, ct).ConfigureAwait(false);
        if (targetNode == null)
        {
            throw new ArgumentException($"Target node '{targetNodeId}' does not exist in the graph");
        }

        // §6.3 Step 2: Compute deterministic ID
        var contentHash = ExpertiseEntry.ComputeContentHash(title, body, targetNodeId);
        var id = ExpertiseEntry.GenerateId(kind, contentHash);

        // Deduplication: check if an expertise entry with same contentHash already exists
        var existingEntries = await this.graphStore.GetExpertiseForNodeAsync(targetNodeId, ct: ct).ConfigureAwait(false);
        var duplicate = existingEntries.FirstOrDefault(e => e.ContentHash == contentHash);
        if (duplicate != null)
        {
            // Return existing entry without creating a duplicate
            return duplicate;
        }

        // §6.3 Step 4: Create ExpertiseEntry with Confidence=1.0, Archived=false
        var entry = new ExpertiseEntry
        {
            Id = id,
            Kind = kind,
            Title = title,
            Body = body,
            TargetNodeId = targetNodeId,
            Scope = scope,
            Confidence = 1.0,
            SourceAgent = sourceAgent,
            Evidence = evidence ?? Array.Empty<Citation>(),
            ContentHash = contentHash,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // §6.3 Step 5: Persist expertise via graph store
        await this.graphStore.UpsertExpertiseAsync(entry, ct).ConfigureAwait(false);

        // §6.3 Step 7: Create APPLIES_TO edge: expertise → target code node
        var edgeId = $"edge:{id}→{targetNodeId}";
        var appliesToEdge = new Hyperedge
        {
            Id = edgeId,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = new List<string> { id, targetNodeId },
            Roles = new Dictionary<string, string>
            {
                [id] = "expertise",
                [targetNodeId] = "target"
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
                [MemoryMetadataKeys.MemoryLayer] = "expertise"
            }
        };

        await this.graphStore.CreateEdgeAsync(appliesToEdge, ct).ConfigureAwait(false);

        // §6.3 Step 8: Audit log
        await this.graphStore.LogAuditAsync(
            sourceAgent,
            "RecordExpertise",
            new { entryId = id, targetNodeId },
            ct).ConfigureAwait(false);

        return entry;
    }

    public async Task<ConsolidationResult> ConsolidateAsync(ConsolidationOptions options, CancellationToken ct = default)
    {
        // Basic deduplication-based consolidation: find entries targeting the same node
        // with matching content hashes and merge them
        var allNodes = await this.graphStore.GetNodesAsync(limit: 10000, cancellationToken: ct).ConfigureAwait(false);
        var mergedIds = new List<string>();
        var candidatesPaired = 0;

        foreach (var node in allNodes)
        {
            var entries = await this.graphStore.GetExpertiseForNodeAsync(node.Id, ct: ct).ConfigureAwait(false);
            if (entries.Count < 2)
            {
                continue;
            }

            // Group by kind to find potential consolidation candidates
            var groups = entries.GroupBy(e => e.Kind).Where(g => g.Count() > 1);
            foreach (var group in groups)
            {
                var items = group.ToList();
                for (var i = 0; i < items.Count - 1 && mergedIds.Count < options.MaxMergesPerRun; i++)
                {
                    for (var j = i + 1; j < items.Count && mergedIds.Count < options.MaxMergesPerRun; j++)
                    {
                        candidatesPaired++;

                        // Simple text similarity: if titles share significant overlap
                        if (items[i].ContentHash == items[j].ContentHash)
                        {
                            // Exact duplicate — archive the newer one
                            await this.graphStore.ArchiveExpertiseAsync(items[j].Id, ct).ConfigureAwait(false);
                            mergedIds.Add(items[j].Id);
                        }
                    }
                }
            }
        }

        return new ConsolidationResult(candidatesPaired, mergedIds.Count, mergedIds);
    }

    public async Task<DecayResult> DecayStaleExpertiseAsync(DecayOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DecayOptions();
        var allNodes = await this.graphStore.GetNodesAsync(limit: 10000, cancellationToken: ct).ConfigureAwait(false);
        var evaluated = 0;
        var decayed = 0;
        var archived = 0;

        foreach (var node in allNodes)
        {
            var entries = await this.graphStore.GetExpertiseForNodeAsync(node.Id, ct: ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                evaluated++;
                var ageDays = (DateTime.UtcNow - entry.CreatedAt).TotalDays;
                var decayFactor = Math.Pow(0.5, ageDays / options.HalfLifeDays);
                var effectiveConfidence = entry.Confidence * decayFactor;

                if (effectiveConfidence < entry.Confidence)
                {
                    decayed++;
                }

                if (effectiveConfidence < options.ArchiveConfidenceFloor)
                {
                    await this.graphStore.ArchiveExpertiseAsync(entry.Id, ct).ConfigureAwait(false);
                    archived++;
                }
            }
        }

        return new DecayResult(evaluated, decayed, archived);
    }

    public Task RestoreArchivedExpertiseAsync(
        string expertiseId, double restoredConfidence = 0.5, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<CommunityResult> DetectCommunitiesAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new CommunityResult(0, 0, 0));
    }
}
