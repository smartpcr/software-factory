// -----------------------------------------------------------------------
// <copyright file="SemanticIndexer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Models;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;

/// <summary>
/// Concrete implementation of <see cref="ISemanticIndexer"/>.
/// Generates embeddings for nodes and stores them in the vector store.
/// Creates SIMILAR_TO edges between semantically similar nodes.
/// </summary>
public class SemanticIndexer : ISemanticIndexer
{
    private readonly IEmbeddingGenerator? embeddingGenerator;
    private readonly IVectorStore? vectorStore;
    private readonly ILogger<SemanticIndexer> logger;

    public SemanticIndexer(
        IEmbeddingGenerator? embeddingGenerator,
        IVectorStore? vectorStore,
        ILogger<SemanticIndexer> logger)
    {
        this.embeddingGenerator = embeddingGenerator;
        this.vectorStore = vectorStore;
        this.logger = logger;
    }

    public async Task<StageResult> IndexAsync(
        HypergraphData data,
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        this.logger.LogInformation("SemanticIndexer: starting semantic indexing");

        var embeddingsGenerated = 0;
        var edgesAdded = 0;
        var errors = new List<string>();

        if (!options.GenerateEmbeddings)
        {
            this.logger.LogInformation("SemanticIndexer: embedding generation disabled, skipping");
            return StageResult.Empty;
        }

        if (this.embeddingGenerator == null)
        {
            this.logger.LogWarning("SemanticIndexer: no embedding generator available, skipping");
            return StageResult.Empty;
        }

        try
        {
            var available = await this.embeddingGenerator.IsAvailableAsync(ct).ConfigureAwait(false);
            if (!available)
            {
                this.logger.LogWarning("SemanticIndexer: embedding generator not available, skipping");
                return StageResult.Empty;
            }

            // Collect embeddable nodes (symbols, not structure-only nodes)
            // Use case-insensitive matching since extractors may emit lowercase NodeType
            var embeddableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Class", "Interface", "Method", "Function",
                "Struct", "Record", "Property", "Constructor",
            };
            var embeddableNodes = data.Nodes.Values
                .Where(n => embeddableTypes.Contains(n.NodeType))
                .ToList();

            if (embeddableNodes.Count == 0)
            {
                return StageResult.Empty;
            }

            // Generate embeddings in batch
            var texts = embeddableNodes.Select(n => n.GetEmbeddingText()).ToList();
            var embeddings = await this.embeddingGenerator
                .GenerateBatchEmbeddingsAsync(texts, ct)
                .ConfigureAwait(false);

            // Handle fewer embeddings than inputs (per operator answer)
            var resultCount = Math.Min(embeddings.Count, embeddableNodes.Count);
            for (var i = 0; i < resultCount; i++)
            {
                var node = embeddableNodes[i];
                node.Metadata[MemoryMetadataKeys.Embedding] = embeddings[i];
                embeddingsGenerated++;
            }

            // Store in vector store if available
            if (this.vectorStore != null)
            {
                try
                {
                    var isVectorAvailable = await this.vectorStore.IsAvailableAsync(ct).ConfigureAwait(false);
                    if (isVectorAvailable)
                    {
                        var points = new List<VectorPoint>();
                        for (var i = 0; i < resultCount; i++)
                        {
                            points.Add(new VectorPoint
                            {
                                Id = embeddableNodes[i].Id,
                                Vector = embeddings[i],
                                Payload = new Dictionary<string, object>
                                {
                                    { "nodeType", embeddableNodes[i].NodeType },
                                    { "name", embeddableNodes[i].Name },
                                    { "language", embeddableNodes[i].Language },
                                    { "filePath", embeddableNodes[i].FilePath },
                                },
                            });
                        }

                        await this.vectorStore.UpsertAsync("memory_embeddings", points, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Vector store upsert failed, continuing without");
                    errors.Add($"Vector store upsert failed: {ex.Message}");
                }
            }

            // Compute SIMILAR_TO edges for nodes with high cosine similarity
            // Use configurable threshold per architecture §2.4 (default 0.85)
            var threshold = options.SimilarityThreshold;
            for (var i = 0; i < resultCount; i++)
            {
                for (var j = i + 1; j < resultCount; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    var similarity = CosineSimilarity(embeddings[i], embeddings[j]);
                    if (similarity >= threshold)
                    {
                        var nodeA = embeddableNodes[i];
                        var nodeB = embeddableNodes[j];
                        data.Hyperedges.Add(new Hyperedge
                        {
                            Id = $"similar:{nodeA.Id}↔{nodeB.Id}",
                            EdgeType = HyperedgeType.MemoryRelation,
                            NodeIds = new List<string> { nodeA.Id, nodeB.Id },
                            Roles = new Dictionary<string, string>
                            {
                                { nodeA.Id, "node_a" },
                                { nodeB.Id, "node_b" },
                            },
                            Weight = similarity,
                            Metadata = new Dictionary<string, object?>
                            {
                                { MemoryMetadataKeys.SemanticRelation, "SIMILAR_TO" },
                                { MemoryMetadataKeys.MemoryLayer, "semantic" },
                                { MemoryMetadataKeys.OriginalEdgeType, HyperedgeType.MemoryRelation.ToString() },
                                { MemoryMetadataKeys.SimilarityScore, similarity },
                            },
                        });
                        edgesAdded++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "SemanticIndexer stage failed");
            errors.Add($"SemanticIndexer failed: {ex.Message}");
        }

        this.logger.LogInformation(
            "SemanticIndexer: completed. Embeddings: {Embeddings}, Edges: {Edges}",
            embeddingsGenerated, edgesAdded);

        return new StageResult
        {
            EmbeddingsGenerated = embeddingsGenerated,
            EdgesCreated = edgesAdded,
            Errors = errors,
        };
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom == 0 ? 0 : dot / denom;
    }
}