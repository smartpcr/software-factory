// -----------------------------------------------------------------------
// <copyright file="ConsolidationSteps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Steps;

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Ast.Core;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.UnitTests.Helpers;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Reqnroll;

[Binding]
public class ConsolidationSteps
{
    private readonly Mock<IMemoryGraphStore> graphStoreMock = new();
    private readonly Mock<IVectorStore> vectorStoreMock = new();
    private readonly Mock<IHttpClientFactory> httpClientFactoryMock = new();
    private readonly Mock<IEmbeddingGenerator> embeddingGeneratorMock = new();
    private readonly IGraphWriteLock graphWriteLock = new GraphWriteLock();
    private readonly MemoryOptions memoryOptions = new()
    {
        ConsolidationModelName = "test-model",
        OllamaEndpoint = "http://localhost:11434",
        SimilarityThreshold = 0.85
    };

    private readonly List<ExpertiseEntry> activeEntries = [];
    private readonly List<ExpertiseEntry> upsertedEntries = [];
    private readonly List<string> archivedIds = [];
    private readonly List<Hyperedge> createdEdges = [];
    private readonly List<VectorPoint> upsertedVectorPoints = [];

    private ConsolidationResult? consolidationResult;
    private ConsolidationResult? task2Result;
    private ConsolidationService? service;
    private RecordingOllamaHandler? ollamaHandler;
    private bool task2WasBlockedWhileTask1Running;
    private int[]? callCompletionOrder;

    // ── Merge scenario ──

    [Given(@"two expertise entries targeting the same node with cosine similarity (.+)")]
    public void GivenTwoExpertiseEntriesWithHighSimilarity(double similarity)
    {
        var embeddingA = CreateEmbeddingVector(1.0f, 0.0f, 0.0f);
        var embeddingB = CreateSimilarEmbedding(embeddingA, (float)similarity);

        var entryA = CreateTestEntry("entry-a", "Pattern: Retry logic", "Use Polly for retries.", "node:method:abc", embeddingA);
        var entryB = CreateTestEntry("entry-b", "Pattern: Retry mechanism", "Implement retry with Polly.", "node:method:abc", embeddingB);

        this.activeEntries.AddRange([entryA, entryB]);
        this.SetupMocks();
    }

    [Given(@"two expertise entries with cosine similarity (.+)")]
    public void GivenTwoExpertiseEntriesWithLowSimilarity(double similarity)
    {
        var embeddingA = CreateEmbeddingVector(1.0f, 0.0f, 0.0f);
        var embeddingB = CreateSimilarEmbedding(embeddingA, (float)similarity);

        // Same target node and same kind — the ONLY reason for skip should be cosine threshold
        var entryA = CreateTestEntry("entry-a", "Pattern: Retry logic", "Use Polly for retries.", "node:method:abc", embeddingA);
        var entryB = CreateTestEntry("entry-b", "Pattern: Retry mechanism variant", "Implement retry differently.", "node:method:abc", embeddingB);

        this.activeEntries.AddRange([entryA, entryB]);
        this.SetupMocks();
    }

    [When(@"ConsolidateAsync runs")]
    public async Task WhenConsolidateAsyncRuns()
    {
        this.consolidationResult = await this.service!.ConsolidateAsync(
            new ConsolidationOptions(MaxMergesPerRun: 50),
            CancellationToken.None);
    }

    [Then(@"a new merged entry exists with SUPERSEDES edges to both originals")]
    public void ThenMergedEntryExistsWithSupersedesEdges()
    {
        this.consolidationResult.Should().NotBeNull();
        this.consolidationResult!.MergedCount.Should().Be(1);
        this.consolidationResult.MergedEntryIds.Should().HaveCount(1);

        // Verify merged entry was upserted
        this.upsertedEntries.Should().HaveCount(1);
        var merged = this.upsertedEntries[0];
        merged.Title.Should().NotBeNullOrWhiteSpace();
        merged.Body.Should().NotBeNullOrWhiteSpace();
        merged.SourceAgent.Should().Be("consolidation-service");
        merged.TargetNodeId.Should().Be("node:method:abc");

        // Verify merged entry got a fresh embedding (not reusing source embedding)
        merged.Embedding.Should().NotBeNull();
        merged.Embedding.Should().NotBeEquivalentTo(this.activeEntries[0].Embedding,
            "merged entry should have a freshly generated embedding, not reuse the source");

        // Verify Ollama request: URI, model, body
        this.ollamaHandler.Should().NotBeNull();
        this.ollamaHandler!.RecordedRequests.Should().HaveCount(1);
        var req = this.ollamaHandler.RecordedRequests[0];
        req.RequestUri!.ToString().Should().Contain("/api/generate");
        req.RequestBody.Should().NotBeNull();
        req.RequestBody!.RootElement.GetProperty("model").GetString().Should().Be("test-model");
        req.RequestBody.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse();
        req.RequestBody.RootElement.GetProperty("prompt").GetString().Should().Contain("Entry A:");
        req.RequestBody.RootElement.GetProperty("prompt").GetString().Should().Contain("Entry B:");

        // Verify merged entry embedding was upserted into vector store
        this.upsertedVectorPoints.Should().HaveCount(1);
        this.upsertedVectorPoints[0].Id.Should().Be(merged.Id);
        this.upsertedVectorPoints[0].Vector.Should().NotBeEmpty();

        // Verify SUPERSEDES edges created (merged → entry-a, merged → entry-b)
        var supersedesEdges = this.createdEdges
            .Where(e => e.Metadata.TryGetValue("SemanticRelation", out var sr) && sr?.ToString() == "SUPERSEDES")
            .ToList();
        supersedesEdges.Should().HaveCount(2);

        foreach (var edge in supersedesEdges)
        {
            edge.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
            edge.Roles.Should().ContainValue("newer");
            edge.Roles.Should().ContainValue("older");
        }

        // Verify DERIVED_FROM edges created
        var derivedEdges = this.createdEdges
            .Where(e => e.Metadata.TryGetValue("SemanticRelation", out var sr) && sr?.ToString() == "DERIVED_FROM")
            .ToList();
        derivedEdges.Should().HaveCount(2);

        foreach (var edge in derivedEdges)
        {
            edge.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
            edge.Roles.Should().ContainValue("derived");
            edge.Roles.Should().ContainValue("source");
        }

        // Verify APPLIES_TO edge connects merged entry to target node
        var appliesToEdges = this.createdEdges
            .Where(e => e.Metadata.TryGetValue("SemanticRelation", out var sr) && sr?.ToString() == "APPLIES_TO")
            .ToList();
        appliesToEdges.Should().HaveCount(1);
        appliesToEdges[0].Roles.Should().ContainValue("expertise");
        appliesToEdges[0].Roles.Should().ContainValue("target");

        // Verify originals were archived
        this.archivedIds.Should().Contain("entry-a");
        this.archivedIds.Should().Contain("entry-b");
    }

    [Then(@"no merge occurs and both entries remain independent")]
    public void ThenNoMergeOccurs()
    {
        this.consolidationResult.Should().NotBeNull();
        this.consolidationResult!.MergedCount.Should().Be(0);
        this.consolidationResult.MergedEntryIds.Should().BeEmpty();

        this.upsertedEntries.Should().BeEmpty();
        this.archivedIds.Should().BeEmpty();
        this.createdEdges.Should().BeEmpty();
        this.upsertedVectorPoints.Should().BeEmpty();
    }

    // ── Lock scenario ──

    [Given(@"ConsolidateAsync is running")]
    public void GivenConsolidateAsyncIsRunning()
    {
        var embeddingA = CreateEmbeddingVector(1.0f, 0.0f, 0.0f);
        var embeddingB = CreateSimilarEmbedding(embeddingA, 0.95f);

        var entryA = CreateTestEntry("entry-lock-a", "Pattern: Cache strategy", "Use distributed cache.", "node:class:xyz", embeddingA);
        var entryB = CreateTestEntry("entry-lock-b", "Pattern: Caching", "Implement distributed cache pattern.", "node:class:xyz", embeddingB);

        this.activeEntries.AddRange([entryA, entryB]);
        this.SetupMocksForLockScenario();
    }

    [When(@"a second ConsolidateAsync is called concurrently")]
    public async Task WhenSecondConsolidateAsyncCalledConcurrently()
    {
        var options = new ConsolidationOptions(MaxMergesPerRun: 50);

        // Use the graph-store-gathering overload (no entries param) so that
        // the second call re-reads from the store INSIDE the lock and sees
        // that the first call already archived the originals.
        var task1InsideLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task1CanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callOrder = new System.Collections.Concurrent.ConcurrentQueue<int>();

        var callCount = 0;
        this.graphStoreMock
            .Setup(g => g.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()))
            .Returns(async (ExpertiseEntry e, CancellationToken _) =>
            {
                this.upsertedEntries.Add(e);
                var myCall = Interlocked.Increment(ref callCount);
                if (myCall == 1)
                {
                    task1InsideLock.TrySetResult();
                    await task1CanFinish.Task;
                }
            });

        var task1 = Task.Run(async () =>
        {
            var result = await this.service!.ConsolidateAsync(options, CancellationToken.None);
            callOrder.Enqueue(1);
            return result;
        });

        await task1InsideLock.Task;

        var task2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task2 = Task.Run(async () =>
        {
            task2Started.TrySetResult();
            var result = await this.service!.ConsolidateAsync(options, CancellationToken.None);
            callOrder.Enqueue(2);
            return result;
        });

        await task2Started.Task;
        await Task.Delay(50);

        this.task2WasBlockedWhileTask1Running = !task2.IsCompleted;

        task1CanFinish.TrySetResult();

        var results = await Task.WhenAll(task1, task2);
        this.consolidationResult = results[0];
        this.task2Result = results[1];

        this.callCompletionOrder = callOrder.ToArray();
    }

    [Then(@"the second call waits for the first to complete")]
    public void ThenSecondCallWaitsForFirstToComplete()
    {
        this.consolidationResult.Should().NotBeNull();

        // Deterministic proof: task2 was blocked while task1 held the lock
        this.task2WasBlockedWhileTask1Running.Should().BeTrue(
            "task2 should be blocked on the write lock while task1 is running");

        // task1 completed before task2 (order [1, 2])
        this.callCompletionOrder.Should().NotBeNull();
        this.callCompletionOrder.Should().HaveCount(2);
        this.callCompletionOrder![0].Should().Be(1,
            "task1 should complete first since it acquired the lock first");
        this.callCompletionOrder[1].Should().Be(2,
            "task2 should complete second, proving serialization");

        // Critical: the second call must NOT re-merge already-archived originals.
        // Because gathering happens inside the lock, task2 re-reads from the store
        // after task1 archived the originals, and finds 0 or 1 active entries
        // (only the merged entry) — not enough for a merge.
        this.task2Result.Should().NotBeNull();
        this.task2Result!.MergedCount.Should().Be(0,
            "second call should not re-merge entries that were already archived by the first call");
    }

    // ── Helper methods ──

    private void SetupMocks()
    {
        this.SetupMocksCore();
    }

    /// <summary>
    /// Sets up mocks for the lock scenario, using the graph-store-gathering overload
    /// so that the second call re-reads from the store after the first archived originals.
    /// Tracks active entries in a thread-safe set that reflects archival.
    /// </summary>
    private void SetupMocksForLockScenario()
    {
        // Vector store availability and collection existence
        this.vectorStoreMock
            .Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        this.vectorStoreMock
            .Setup(v => v.CollectionExistsAsync("memory_expertise", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Thread-safe active entry tracking: archiving removes from this set
        var lockActiveEntries = new System.Collections.Concurrent.ConcurrentDictionary<string, ExpertiseEntry>(
            this.activeEntries.ToDictionary(e => e.Id));

        // GetEdgesBySemanticRelationAsync("APPLIES_TO", "expertise") returns edges
        // for currently active entries (not archived ones).
        this.graphStoreMock
            .Setup(g => g.GetEdgesBySemanticRelationAsync(
                "APPLIES_TO", "expertise", It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, int? _, int _, CancellationToken _) =>
            {
                var edges = lockActiveEntries.Values
                    .Select(e => new Hyperedge
                    {
                        Id = $"edge:applies_to:{e.Id}:{e.TargetNodeId}",
                        EdgeType = HyperedgeType.MemoryRelation,
                        NodeIds = [e.Id, e.TargetNodeId],
                        Roles = new Dictionary<string, string>
                        {
                            [e.Id] = "expertise",
                            [e.TargetNodeId] = "target"
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            ["SemanticRelation"] = "APPLIES_TO",
                            ["MemoryLayer"] = "expertise"
                        }
                    })
                    .ToList();
                return Task.FromResult<IReadOnlyList<Hyperedge>>(edges);
            });

        // GetExpertiseForNodeAsync returns entries from the active set
        this.graphStoreMock
            .Setup(g => g.GetExpertiseForNodeAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Returns((string targetNodeId, double _, CancellationToken _) =>
            {
                var entries = lockActiveEntries.Values
                    .Where(e => e.TargetNodeId == targetNodeId)
                    .ToList();
                return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(entries);
            });

        // UpsertExpertiseAsync adds to the active set (for merged entries)
        this.graphStoreMock
            .Setup(g => g.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ExpertiseEntry, CancellationToken>((e, _) =>
            {
                lockActiveEntries[e.Id] = e;
                this.upsertedEntries.Add(e);
            })
            .Returns(Task.CompletedTask);

        // ArchiveExpertiseAsync removes from the active set — critical for proving
        // that the second call sees archived entries as gone.
        this.graphStoreMock
            .Setup(g => g.ArchiveExpertiseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((id, _) =>
            {
                lockActiveEntries.TryRemove(id, out var _removed);
                this.archivedIds.Add(id);
            })
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.CreateEdgeAsync(It.IsAny<Hyperedge>(), It.IsAny<CancellationToken>()))
            .Callback<Hyperedge, CancellationToken>((e, _) => this.createdEdges.Add(e))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.RetireEdgeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.LogAuditAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        this.embeddingGeneratorMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, CancellationToken _) =>
            {
                var hash = text.GetHashCode();
                var embedding = new float[64];
                embedding[0] = (float)Math.Cos(hash * 0.1);
                embedding[1] = (float)Math.Sin(hash * 0.1);
                embedding[2] = 0.5f;
                var norm = (float)Math.Sqrt(embedding[0] * embedding[0] + embedding[1] * embedding[1] + 0.25f);
                for (var i = 0; i < 3; i++)
                {
                    embedding[i] /= norm;
                }

                return embedding;
            });

        // Vector store search uses the lock-aware active entry set
        this.vectorStoreMock
            .Setup(v => v.SearchAsync(
                "memory_expertise",
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<VectorFilter?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, float[], int, VectorFilter?, CancellationToken>((_, queryVector, _, filter, _) =>
            {
                var results = new List<VectorSearchResult>();
                foreach (var entry in lockActiveEntries.Values)
                {
                    if (entry.Embedding is null)
                    {
                        continue;
                    }

                    if (filter?.Must is not null)
                    {
                        var pass = true;
                        foreach (var condition in filter.Must)
                        {
                            var fieldValue = condition.Field switch
                            {
                                "TargetNodeId" => entry.TargetNodeId,
                                "Kind" => entry.Kind.ToString(),
                                _ => null
                            };

                            if (fieldValue is null || !string.Equals(fieldValue, condition.Value?.ToString(), StringComparison.Ordinal))
                            {
                                pass = false;
                                break;
                            }
                        }

                        if (!pass)
                        {
                            continue;
                        }
                    }

                    var score = (float)ConsolidationService.ComputeCosineSimilarity(queryVector, entry.Embedding);
                    results.Add(new VectorSearchResult
                    {
                        Id = entry.Id,
                        Score = score,
                        Vector = entry.Embedding,
                        Payload = new Dictionary<string, object>
                        {
                            ["TargetNodeId"] = entry.TargetNodeId,
                            ["Kind"] = entry.Kind.ToString()
                        }
                    });
                }

                return Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                    results.OrderByDescending(r => r.Score).ToList());
            });

        this.vectorStoreMock
            .Setup(v => v.UpsertAsync(
                "memory_expertise",
                It.IsAny<IEnumerable<VectorPoint>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<VectorPoint>, CancellationToken>((_, points, _) =>
                this.upsertedVectorPoints.AddRange(points))
            .Returns(Task.CompletedTask);

        this.vectorStoreMock
            .Setup(v => v.DeleteAsync(
                "memory_expertise",
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        this.ollamaHandler = new RecordingOllamaHandler();
        var httpClient = new HttpClient(this.ollamaHandler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        this.httpClientFactoryMock
            .Setup(f => f.CreateClient("OllamaConsolidation"))
            .Returns(httpClient);

        var optionsMock = Options.Create(this.memoryOptions);
        var loggerMock = new Mock<ILogger<ConsolidationService>>();

        this.service = new ConsolidationService(
            this.graphStoreMock.Object,
            this.vectorStoreMock.Object,
            this.embeddingGeneratorMock.Object,
            this.httpClientFactoryMock.Object,
            this.graphWriteLock,
            optionsMock,
            loggerMock.Object);
    }

    private void SetupMocksCore()
    {
        // Vector store availability and collection existence
        this.vectorStoreMock
            .Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        this.vectorStoreMock
            .Setup(v => v.CollectionExistsAsync("memory_expertise", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // GatherActiveEntriesAsync support: the pre-gathered overload now re-validates
        // entries inside the lock by calling GatherActiveEntriesAsync, which uses
        // GetEdgesBySemanticRelationAsync("APPLIES_TO") and GetExpertiseForNodeAsync.
        this.graphStoreMock
            .Setup(g => g.GetEdgesBySemanticRelationAsync(
                "APPLIES_TO", It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, int? _, int _, CancellationToken _) =>
            {
                var edges = this.activeEntries
                    .Select(e => new Hyperedge
                    {
                        Id = $"edge:applies_to:{e.Id}:{e.TargetNodeId}",
                        EdgeType = HyperedgeType.MemoryRelation,
                        NodeIds = [e.Id, e.TargetNodeId],
                        Roles = new Dictionary<string, string>
                        {
                            [e.Id] = "expertise",
                            [e.TargetNodeId] = "target"
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            ["SemanticRelation"] = "APPLIES_TO",
                            ["MemoryLayer"] = "expertise"
                        }
                    })
                    .ToList();
                return Task.FromResult<IReadOnlyList<Hyperedge>>(edges);
            });

        this.graphStoreMock
            .Setup(g => g.GetExpertiseForNodeAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Returns((string targetNodeId, double _, CancellationToken _) =>
            {
                var entries = this.activeEntries
                    .Where(e => e.TargetNodeId == targetNodeId)
                    .ToList();
                return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(entries);
            });

        // Graph store mocks
        this.graphStoreMock
            .Setup(g => g.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ExpertiseEntry, CancellationToken>((e, _) => this.upsertedEntries.Add(e))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.ArchiveExpertiseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((id, _) => this.archivedIds.Add(id))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.CreateEdgeAsync(It.IsAny<Hyperedge>(), It.IsAny<CancellationToken>()))
            .Callback<Hyperedge, CancellationToken>((e, _) => this.createdEdges.Add(e))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.RetireEdgeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.LogAuditAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Embedding generator mock — produces a fresh embedding for merged text
        this.embeddingGeneratorMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, CancellationToken _) =>
            {
                // Deterministic embedding based on text hash for testability
                var hash = text.GetHashCode();
                var embedding = new float[64];
                embedding[0] = (float)Math.Cos(hash * 0.1);
                embedding[1] = (float)Math.Sin(hash * 0.1);
                embedding[2] = 0.5f;
                var norm = (float)Math.Sqrt(embedding[0] * embedding[0] + embedding[1] * embedding[1] + 0.25f);
                for (var i = 0; i < 3; i++)
                {
                    embedding[i] /= norm;
                }

                return embedding;
            });

        // Vector store search mock — applies VectorFilter to restrict candidates by payload fields
        this.vectorStoreMock
            .Setup(v => v.SearchAsync(
                "memory_expertise",
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<VectorFilter?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, float[], int, VectorFilter?, CancellationToken>((_, queryVector, _, filter, _) =>
            {
                var results = new List<VectorSearchResult>();
                foreach (var entry in this.activeEntries)
                {
                    if (entry.Embedding is null)
                    {
                        continue;
                    }

                    // Apply filter conditions against the entry's payload-equivalent fields
                    if (filter?.Must is not null)
                    {
                        var pass = true;
                        foreach (var condition in filter.Must)
                        {
                            var fieldValue = condition.Field switch
                            {
                                "TargetNodeId" => entry.TargetNodeId,
                                "Kind" => entry.Kind.ToString(),
                                _ => null
                            };

                            if (fieldValue is null || !string.Equals(fieldValue, condition.Value?.ToString(), StringComparison.Ordinal))
                            {
                                pass = false;
                                break;
                            }
                        }

                        if (!pass)
                        {
                            continue;
                        }
                    }

                    var score = (float)ConsolidationService.ComputeCosineSimilarity(queryVector, entry.Embedding);
                    results.Add(new VectorSearchResult
                    {
                        Id = entry.Id,
                        Score = score,
                        Vector = entry.Embedding,
                        Payload = new Dictionary<string, object>
                        {
                            ["TargetNodeId"] = entry.TargetNodeId,
                            ["Kind"] = entry.Kind.ToString()
                        }
                    });
                }

                return Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                    results.OrderByDescending(r => r.Score).ToList());
            });

        // Vector store upsert mock — captures upserted points
        this.vectorStoreMock
            .Setup(v => v.UpsertAsync(
                "memory_expertise",
                It.IsAny<IEnumerable<VectorPoint>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<VectorPoint>, CancellationToken>((_, points, _) =>
                this.upsertedVectorPoints.AddRange(points))
            .Returns(Task.CompletedTask);

        // Vector store delete mock: source vectors removed after archiving
        this.vectorStoreMock
            .Setup(v => v.DeleteAsync(
                "memory_expertise",
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // HTTP client mock — simulate Ollama /api/generate response
        this.ollamaHandler = new RecordingOllamaHandler();
        var httpClient = new HttpClient(this.ollamaHandler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        this.httpClientFactoryMock
            .Setup(f => f.CreateClient("OllamaConsolidation"))
            .Returns(httpClient);

        var optionsMock = Options.Create(this.memoryOptions);
        var loggerMock = new Mock<ILogger<ConsolidationService>>();

        this.service = new ConsolidationService(
            this.graphStoreMock.Object,
            this.vectorStoreMock.Object,
            this.embeddingGeneratorMock.Object,
            this.httpClientFactoryMock.Object,
            this.graphWriteLock,
            optionsMock,
            loggerMock.Object);
    }

    private static ExpertiseEntry CreateTestEntry(
        string id, string title, string body, string targetNodeId, float[] embedding,
        ExpertiseKind kind = ExpertiseKind.Pattern)
    {
        var contentHash = ExpertiseEntry.ComputeContentHash(title, body, targetNodeId);
        return new ExpertiseEntry
        {
            Id = id,
            Kind = kind,
            Title = title,
            Body = body,
            TargetNodeId = targetNodeId,
            Confidence = 0.9,
            SourceAgent = "test-agent",
            Embedding = embedding,
            ContentHash = contentHash
        };
    }

    private static float[] CreateEmbeddingVector(float x, float y, float z)
    {
        var embedding = new float[64];
        embedding[0] = x;
        embedding[1] = y;
        embedding[2] = z;

        var norm = (float)Math.Sqrt(x * x + y * y + z * z);
        if (norm > 0)
        {
            for (var i = 0; i < 3; i++)
            {
                embedding[i] /= norm;
            }
        }

        return embedding;
    }

    private static float[] CreateSimilarEmbedding(float[] reference, float targetSimilarity)
    {
        var theta = Math.Acos(Math.Clamp(targetSimilarity, -1.0, 1.0));
        var cosTheta = (float)Math.Cos(theta);
        var sinTheta = (float)Math.Sin(theta);

        var orthogonal = new float[reference.Length];
        orthogonal[1] = 1.0f;

        var dot = 0.0f;
        for (var i = 0; i < reference.Length; i++)
        {
            dot += reference[i] * orthogonal[i];
        }

        for (var i = 0; i < reference.Length; i++)
        {
            orthogonal[i] -= dot * reference[i];
        }

        var norm = 0.0f;
        for (var i = 0; i < orthogonal.Length; i++)
        {
            norm += orthogonal[i] * orthogonal[i];
        }

        norm = (float)Math.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < orthogonal.Length; i++)
            {
                orthogonal[i] /= norm;
            }
        }

        var result = new float[reference.Length];
        for (var i = 0; i < reference.Length; i++)
        {
            result[i] = cosTheta * reference[i] + sinTheta * orthogonal[i];
        }

        return result;
    }

    /// <summary>
    /// Records HTTP requests and returns a canned Ollama /api/generate response.
    /// </summary>
    internal sealed class RecordingOllamaHandler : HttpMessageHandler
    {
        public List<RecordedRequest> RecordedRequests { get; } = [];

        public HttpStatusCode StatusCodeToReturn { get; set; } = HttpStatusCode.OK;

        public bool ThrowOnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (this.ThrowOnSend)
            {
                throw new HttpRequestException("Simulated Ollama failure");
            }

            JsonDocument? body = null;
            if (request.Content is not null)
            {
                var json = await request.Content.ReadAsStringAsync(cancellationToken);
                body = JsonDocument.Parse(json);
            }

            this.RecordedRequests.Add(new RecordedRequest(request.RequestUri, request.Method, body));

            if (this.StatusCodeToReturn != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(this.StatusCodeToReturn)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
            }

            var responseBody = JsonSerializer.Serialize(new
            {
                response = "TITLE: Merged: Retry pattern with Polly\nBODY: Use the Polly library to implement retry logic with exponential backoff. This is a best practice for resilient service calls."
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    internal sealed record RecordedRequest(Uri? RequestUri, HttpMethod Method, JsonDocument? RequestBody);

    // ── Cross-target scenario ──

    [Given(@"two expertise entries targeting different nodes with cosine similarity (.+)")]
    public void GivenTwoExpertiseEntriesTargetingDifferentNodes(double similarity)
    {
        var embeddingA = CreateEmbeddingVector(1.0f, 0.0f, 0.0f);
        var embeddingB = CreateSimilarEmbedding(embeddingA, (float)similarity);

        var entryA = CreateTestEntry("entry-a", "Pattern: Retry logic", "Use Polly for retries.", "node:method:abc", embeddingA);
        var entryB = CreateTestEntry("entry-b", "Pattern: Retry mechanism", "Implement retry with Polly.", "node:method:different", embeddingB);

        this.activeEntries.AddRange([entryA, entryB]);
        this.SetupMocks();
    }

    // ── LLM failure scenario ──

    [Given(@"two expertise entries targeting the same node with LLM failure configured")]
    public void GivenTwoExpertiseEntriesWithLlmFailure()
    {
        var embeddingA = CreateEmbeddingVector(1.0f, 0.0f, 0.0f);
        var embeddingB = CreateSimilarEmbedding(embeddingA, 0.95f);

        var entryA = CreateTestEntry("entry-a", "Pattern: Retry logic", "Use Polly for retries.", "node:method:abc", embeddingA);
        var entryB = CreateTestEntry("entry-b", "Pattern: Retry mechanism", "Implement retry with Polly.", "node:method:abc", embeddingB);

        this.activeEntries.AddRange([entryA, entryB]);
        this.SetupMocks();

        // Reconfigure the handler to throw
        this.ollamaHandler!.ThrowOnSend = true;
    }

    // ── Cancellation scenario ──

    private Exception? caughtException;

    [When(@"ConsolidateAsync is cancelled during execution")]
    public async Task WhenConsolidateAsyncIsCancelledDuringExecution()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await this.service!.ConsolidateAsync(
                new ConsolidationOptions(MaxMergesPerRun: 50),
                cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            this.caughtException = ex;
        }
    }

    [Then(@"the operation is cancelled with OperationCanceledException")]
    public void ThenTheOperationIsCancelledWithOperationCanceledException()
    {
        this.caughtException.Should().NotBeNull();
        this.caughtException.Should().BeAssignableTo<OperationCanceledException>();
    }

    // ── Concrete store (edge persistence) scenario ──

    private InMemoryMemoryGraphStore? concreteGraphStore;
    private ConsolidationService? concreteService;
    private ConsolidationResult? concreteResult;

    [Given(@"two expertise entries targeting the same node with cosine similarity (.+) using concrete store")]
    public void GivenTwoExpertiseEntriesUsingConcreteStore(double similarity)
    {
        var embeddingA = CreateEmbeddingVector(1.0f, 0.0f, 0.0f);
        var embeddingB = CreateSimilarEmbedding(embeddingA, (float)similarity);

        var entryA = CreateTestEntry("entry-a", "Pattern: Retry logic", "Use Polly for retries.", "node:method:abc", embeddingA);
        var entryB = CreateTestEntry("entry-b", "Pattern: Retry mechanism", "Implement retry with Polly.", "node:method:abc", embeddingB);

        this.concreteGraphStore = new InMemoryMemoryGraphStore();
        this.concreteGraphStore.UpsertExpertiseAsync(entryA).GetAwaiter().GetResult();
        this.concreteGraphStore.UpsertExpertiseAsync(entryB).GetAwaiter().GetResult();

        // Create APPLIES_TO edges so GatherActiveEntriesAsync can discover entries
        this.concreteGraphStore.CreateEdgeAsync(new Hyperedge
        {
            Id = $"edge:applies_to:{entryA.Id}:{entryA.TargetNodeId}",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = [entryA.Id, entryA.TargetNodeId],
            Roles = new Dictionary<string, string>
            {
                [entryA.Id] = "expertise",
                [entryA.TargetNodeId] = "target"
            },
            Metadata = new Dictionary<string, object?>
            {
                ["SemanticRelation"] = "APPLIES_TO",
                ["MemoryLayer"] = "expertise"
            }
        }).GetAwaiter().GetResult();

        this.concreteGraphStore.CreateEdgeAsync(new Hyperedge
        {
            Id = $"edge:applies_to:{entryB.Id}:{entryB.TargetNodeId}",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = [entryB.Id, entryB.TargetNodeId],
            Roles = new Dictionary<string, string>
            {
                [entryB.Id] = "expertise",
                [entryB.TargetNodeId] = "target"
            },
            Metadata = new Dictionary<string, object?>
            {
                ["SemanticRelation"] = "APPLIES_TO",
                ["MemoryLayer"] = "expertise"
            }
        }).GetAwaiter().GetResult();

        // Embedding generator mock
        this.embeddingGeneratorMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, CancellationToken _) =>
            {
                var hash = text.GetHashCode();
                var embedding = new float[64];
                embedding[0] = (float)Math.Cos(hash * 0.1);
                embedding[1] = (float)Math.Sin(hash * 0.1);
                embedding[2] = 0.5f;
                var norm = (float)Math.Sqrt(embedding[0] * embedding[0] + embedding[1] * embedding[1] + 0.25f);
                for (var i = 0; i < 3; i++)
                {
                    embedding[i] /= norm;
                }

                return embedding;
            });

        // Vector store availability and collection existence
        this.vectorStoreMock
            .Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        this.vectorStoreMock
            .Setup(v => v.CollectionExistsAsync("memory_expertise", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Vector store search mock that filters by payload
        this.vectorStoreMock
            .Setup(v => v.SearchAsync(
                "memory_expertise",
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<VectorFilter?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, float[], int, VectorFilter?, CancellationToken>((_, queryVector, _, filter, ct2) =>
            {
                var entries = this.concreteGraphStore!.GetAllExpertise();
                var results = new List<VectorSearchResult>();
                foreach (var entry in entries)
                {
                    if (entry.Embedding is null)
                    {
                        continue;
                    }

                    if (filter?.Must is not null)
                    {
                        var pass = true;
                        foreach (var condition in filter.Must)
                        {
                            var fieldValue = condition.Field switch
                            {
                                "TargetNodeId" => entry.TargetNodeId,
                                "Kind" => entry.Kind.ToString(),
                                _ => null
                            };

                            if (fieldValue is null || !string.Equals(fieldValue, condition.Value?.ToString(), StringComparison.Ordinal))
                            {
                                pass = false;
                                break;
                            }
                        }

                        if (!pass)
                        {
                            continue;
                        }
                    }

                    var score = (float)ConsolidationService.ComputeCosineSimilarity(queryVector, entry.Embedding);
                    results.Add(new VectorSearchResult
                    {
                        Id = entry.Id,
                        Score = score,
                        Vector = entry.Embedding,
                        Payload = new Dictionary<string, object>
                        {
                            ["TargetNodeId"] = entry.TargetNodeId,
                            ["Kind"] = entry.Kind.ToString()
                        }
                    });
                }

                return Task.FromResult<IReadOnlyList<VectorSearchResult>>(results.OrderByDescending(r => r.Score).ToList());
            });

        this.vectorStoreMock
            .Setup(v => v.UpsertAsync(
                "memory_expertise",
                It.IsAny<IEnumerable<VectorPoint>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        this.vectorStoreMock
            .Setup(v => v.DeleteAsync(
                "memory_expertise",
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        this.ollamaHandler = new RecordingOllamaHandler();
        var httpClient = new HttpClient(this.ollamaHandler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        this.httpClientFactoryMock
            .Setup(f => f.CreateClient("OllamaConsolidation"))
            .Returns(httpClient);

        var optionsMock = Options.Create(this.memoryOptions);
        var loggerMock = new Mock<ILogger<ConsolidationService>>();

        this.concreteService = new ConsolidationService(
            this.concreteGraphStore,
            this.vectorStoreMock.Object,
            this.embeddingGeneratorMock.Object,
            this.httpClientFactoryMock.Object,
            this.graphWriteLock,
            optionsMock,
            loggerMock.Object);
    }

    [When(@"ConsolidateAsync runs against concrete store")]
    public async Task WhenConsolidateAsyncRunsAgainstConcreteStore()
    {
        this.concreteResult = await this.concreteService!.ConsolidateAsync(
            new ConsolidationOptions(MaxMergesPerRun: 50),
            CancellationToken.None);
    }

    [Then(@"SUPERSEDES and DERIVED_FROM edges are persisted in the concrete graph store")]
    public void ThenEdgesArePersistedInConcreteGraphStore()
    {
        this.concreteResult.Should().NotBeNull();
        this.concreteResult!.MergedCount.Should().Be(1);

        var allEdges = this.concreteGraphStore!.GetAllEdges();
        allEdges.Should().HaveCount(5, "2 SUPERSEDES + 2 DERIVED_FROM + 1 new APPLIES_TO edges expected (source APPLIES_TO edges retired)");

        var supersedesEdges = allEdges
            .Where(e => e.Metadata.TryGetValue("SemanticRelation", out var sr) && sr?.ToString() == "SUPERSEDES")
            .ToList();
        supersedesEdges.Should().HaveCount(2);
        foreach (var edge in supersedesEdges)
        {
            edge.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
            edge.Roles.Should().ContainValue("newer");
            edge.Roles.Should().ContainValue("older");

            // Verify edge is retrievable by ID (proves persistence, not just in-flight state)
            var retrieved = this.concreteGraphStore.GetEdgeAsync(edge.Id).GetAwaiter().GetResult();
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(edge.Id);
        }

        var derivedEdges = allEdges
            .Where(e => e.Metadata.TryGetValue("SemanticRelation", out var sr) && sr?.ToString() == "DERIVED_FROM")
            .ToList();
        derivedEdges.Should().HaveCount(2);
        foreach (var edge in derivedEdges)
        {
            edge.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
            edge.Roles.Should().ContainValue("derived");
            edge.Roles.Should().ContainValue("source");

            var retrieved = this.concreteGraphStore.GetEdgeAsync(edge.Id).GetAwaiter().GetResult();
            retrieved.Should().NotBeNull();
        }

        // Verify merged expertise entry was persisted
        var allExpertise = this.concreteGraphStore.GetAllExpertise();
        allExpertise.Should().HaveCount(1, "only the merged entry should remain active");
        allExpertise[0].SourceAgent.Should().Be("consolidation-service");

        // Verify originals were archived
        var archived = this.concreteGraphStore.GetAllArchived();
        archived.Should().HaveCount(2);

        // Verify edges can be queried by semantic relation
        var persistedSupersedes = this.concreteGraphStore
            .GetEdgesBySemanticRelationAsync("SUPERSEDES").GetAwaiter().GetResult();
        persistedSupersedes.Should().HaveCount(2);

        var persistedDerived = this.concreteGraphStore
            .GetEdgesBySemanticRelationAsync("DERIVED_FROM").GetAwaiter().GetResult();
        persistedDerived.Should().HaveCount(2);

        // Verify APPLIES_TO edges: source entries' edges retired, only merged entry's edge remains
        var persistedAppliesTo = this.concreteGraphStore
            .GetEdgesBySemanticRelationAsync("APPLIES_TO").GetAwaiter().GetResult();
        persistedAppliesTo.Should().HaveCount(1, "only merged entry's APPLIES_TO edge should remain after source edges are retired");
        persistedAppliesTo.Should().OnlyContain(e => e.Roles.ContainsValue("expertise") && e.Roles.ContainsValue("target"));
    }

    // ── UUID vector store ID mapping scenario ──

    private ConsolidationResult? uuidResult;

    [Given(@"two expertise entries with Qdrant-style UUID vector IDs and cosine similarity (.+)")]
    public void GivenTwoExpertiseEntriesWithUuidVectorIds(double similarity)
    {
        var embeddingA = CreateEmbeddingVector(1.0f, 0.0f, 0.0f);
        var embeddingB = CreateSimilarEmbedding(embeddingA, (float)similarity);

        var entryA = CreateTestEntry("entry-uuid-a", "Pattern: Error handling", "Use Result types for errors.", "node:method:uuid1", embeddingA);
        var entryB = CreateTestEntry("entry-uuid-b", "Pattern: Error management", "Implement Result monad for error handling.", "node:method:uuid1", embeddingB);

        this.activeEntries.AddRange([entryA, entryB]);

        // Vector store availability and collection existence
        this.vectorStoreMock
            .Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        this.vectorStoreMock
            .Setup(v => v.CollectionExistsAsync("memory_expertise", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // GatherActiveEntriesAsync support for the pre-gathered overload re-validation
        this.graphStoreMock
            .Setup(g => g.GetEdgesBySemanticRelationAsync(
                "APPLIES_TO", It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, int? _, int _, CancellationToken _) =>
            {
                var edges = this.activeEntries
                    .Select(e => new Hyperedge
                    {
                        Id = $"edge:applies_to:{e.Id}:{e.TargetNodeId}",
                        EdgeType = HyperedgeType.MemoryRelation,
                        NodeIds = [e.Id, e.TargetNodeId],
                        Roles = new Dictionary<string, string>
                        {
                            [e.Id] = "expertise",
                            [e.TargetNodeId] = "target"
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            ["SemanticRelation"] = "APPLIES_TO",
                            ["MemoryLayer"] = "expertise"
                        }
                    })
                    .ToList();
                return Task.FromResult<IReadOnlyList<Hyperedge>>(edges);
            });

        this.graphStoreMock
            .Setup(g => g.GetExpertiseForNodeAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Returns((string targetNodeId, double _, CancellationToken _) =>
            {
                var entries = this.activeEntries
                    .Where(e => e.TargetNodeId == targetNodeId)
                    .ToList();
                return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(entries);
            });

        // Graph store mocks
        this.graphStoreMock
            .Setup(g => g.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ExpertiseEntry, CancellationToken>((e, _) => this.upsertedEntries.Add(e))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.ArchiveExpertiseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((id, _) => this.archivedIds.Add(id))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.CreateEdgeAsync(It.IsAny<Hyperedge>(), It.IsAny<CancellationToken>()))
            .Callback<Hyperedge, CancellationToken>((e, _) => this.createdEdges.Add(e))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.RetireEdgeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        this.graphStoreMock
            .Setup(g => g.LogAuditAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Embedding generator mock
        this.embeddingGeneratorMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, CancellationToken _) =>
            {
                var hash = text.GetHashCode();
                var emb = new float[64];
                emb[0] = (float)Math.Cos(hash * 0.1);
                emb[1] = (float)Math.Sin(hash * 0.1);
                emb[2] = 0.5f;
                var n = (float)Math.Sqrt(emb[0] * emb[0] + emb[1] * emb[1] + 0.25f);
                for (var i = 0; i < 3; i++)
                {
                    emb[i] /= n;
                }

                return emb;
            });

        // Vector store mock: simulates Qdrant UUID v5 ID transformation.
        // Returns UUID-style IDs in search results with only architecture-specified
        // payload {TargetNodeId, Kind} — NO expertiseId field.
        this.vectorStoreMock
            .Setup(v => v.SearchAsync(
                "memory_expertise",
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<VectorFilter?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, float[], int, VectorFilter?, CancellationToken>((_, queryVector, _, filter, _) =>
            {
                var results = new List<VectorSearchResult>();
                foreach (var entry in this.activeEntries)
                {
                    if (entry.Embedding is null)
                    {
                        continue;
                    }

                    if (filter?.Must is not null)
                    {
                        var pass = true;
                        foreach (var condition in filter.Must)
                        {
                            var fieldValue = condition.Field switch
                            {
                                "TargetNodeId" => entry.TargetNodeId,
                                "Kind" => entry.Kind.ToString(),
                                _ => null
                            };

                            if (fieldValue is null || !string.Equals(fieldValue, condition.Value?.ToString(), StringComparison.Ordinal))
                            {
                                pass = false;
                                break;
                            }
                        }

                        if (!pass)
                        {
                            continue;
                        }
                    }

                    var score = (float)ConsolidationService.ComputeCosineSimilarity(queryVector, entry.Embedding);

                    // Return UUID-style ID (simulating QdrantVectorStore.ToUuid behavior)
                    // and architecture-specified payload WITHOUT expertiseId.
                    results.Add(new VectorSearchResult
                    {
                        Id = Guid.NewGuid().ToString(),
                        Score = score,
                        Vector = entry.Embedding,
                        Payload = new Dictionary<string, object>
                        {
                            ["TargetNodeId"] = entry.TargetNodeId,
                            ["Kind"] = entry.Kind.ToString()
                        }
                    });
                }

                return Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                    results.OrderByDescending(r => r.Score).ToList());
            });

        this.vectorStoreMock
            .Setup(v => v.UpsertAsync("memory_expertise", It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<VectorPoint>, CancellationToken>((_, points, _) =>
                this.upsertedVectorPoints.AddRange(points))
            .Returns(Task.CompletedTask);

        this.vectorStoreMock
            .Setup(v => v.DeleteAsync("memory_expertise", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        this.ollamaHandler = new RecordingOllamaHandler();
        var httpClient = new HttpClient(this.ollamaHandler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        this.httpClientFactoryMock
            .Setup(f => f.CreateClient("OllamaConsolidation"))
            .Returns(httpClient);

        var optionsMock = Options.Create(this.memoryOptions);
        var loggerMock = new Mock<ILogger<ConsolidationService>>();

        this.service = new ConsolidationService(
            this.graphStoreMock.Object,
            this.vectorStoreMock.Object,
            this.embeddingGeneratorMock.Object,
            this.httpClientFactoryMock.Object,
            this.graphWriteLock,
            optionsMock,
            loggerMock.Object);
    }

    [When(@"ConsolidateAsync runs with UUID vector IDs")]
    public async Task WhenConsolidateAsyncRunsWithUuidVectorIds()
    {
        this.uuidResult = await this.service!.ConsolidateAsync(
            new ConsolidationOptions(MaxMergesPerRun: 50),
            CancellationToken.None);
    }

    [Then(@"the service resolves candidates via payload-based lookup and merges successfully")]
    public void ThenTheServiceResolvesCandidatesViaPayloadBasedLookup()
    {
        this.uuidResult.Should().NotBeNull();
        this.uuidResult!.MergedCount.Should().Be(1,
            "the service should resolve UUID IDs via payload {TargetNodeId, Kind} and merge successfully");
        this.uuidResult.MergedEntryIds.Should().HaveCount(1);

        // Verify merged entry was created
        this.upsertedEntries.Should().HaveCount(1);
        this.upsertedEntries[0].SourceAgent.Should().Be("consolidation-service");
        this.upsertedEntries[0].TargetNodeId.Should().Be("node:method:uuid1");

        // Verify edges were created
        var supersedesEdges = this.createdEdges
            .Where(e => e.Metadata.TryGetValue("SemanticRelation", out var sr) && sr?.ToString() == "SUPERSEDES")
            .ToList();
        supersedesEdges.Should().HaveCount(2);

        // Verify vector point payload uses architecture-spec fields (no expertiseId)
        this.upsertedVectorPoints.Should().HaveCount(1);
        this.upsertedVectorPoints[0].Payload.Should().ContainKey("TargetNodeId");
        this.upsertedVectorPoints[0].Payload.Should().ContainKey("Kind");

        // Verify originals were archived
        this.archivedIds.Should().HaveCount(2);
    }
}