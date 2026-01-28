// -----------------------------------------------------------------------
// <copyright file="EnrichedEmbeddingPipeline.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

using Ast.Core;
using Ast.Lsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Pipeline that combines LSP enrichment with embedding generation for enhanced code understanding.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="EnrichedEmbeddingPipeline"/> class.
/// </remarks>
/// <param name="embeddingService">The embedding service.</param>
/// <param name="options">Pipeline configuration options.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="lspEnrichmentService">Optional LSP enrichment service.</param>
public sealed class EnrichedEmbeddingPipeline(
    CodeEmbeddingService embeddingService,
    IOptions<EnrichedEmbeddingOptions> options,
    ILogger<EnrichedEmbeddingPipeline> logger,
    LspEnrichmentService? lspEnrichmentService = null) : IAsyncDisposable
{
    private readonly LspEnrichmentService? lspEnrichmentService = lspEnrichmentService;
    private readonly CodeEmbeddingService embeddingService = embeddingService;
    private readonly EnrichedEmbeddingOptions options = options.Value;
    private readonly ILogger<EnrichedEmbeddingPipeline> logger = logger;
    private bool disposed;

    /// <summary>
    /// Gets whether LSP enrichment is available.
    /// </summary>
    public bool IsLspEnrichmentAvailable => this.lspEnrichmentService != null && this.options.EnableLspEnrichment;

    /// <summary>
    /// Processes a hypergraph through the full pipeline: LSP enrichment then embedding generation.
    /// </summary>
    /// <param name="data">The hypergraph data to process.</param>
    /// <param name="workspacePath">The workspace root path for LSP operations.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of enriched code embeddings.</returns>
    public async Task<EnrichedEmbeddingResult> ProcessAsync(
        HypergraphData data,
        string workspacePath,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var result = new EnrichedEmbeddingResult
        {
            StartTime = DateTimeOffset.UtcNow
        };

        var processedData = data;
        var totalNodes = data.Nodes.Count;
        var currentStep = 0;
        var totalSteps = this.IsLspEnrichmentAvailable ? 2 : 1;

        // Step 1: LSP Enrichment (if enabled)
        if (this.IsLspEnrichmentAvailable && this.lspEnrichmentService != null)
        {
            currentStep++;
            this.logger.LogInformation("Starting LSP enrichment for {NodeCount} nodes", totalNodes);

            var enrichmentProgress = new Progress<EnrichmentProgress>(p =>
            {
                progress?.Report(new PipelineProgress
                {
                    CurrentStep = PipelineStep.LspEnrichment,
                    StepNumber = currentStep,
                    TotalSteps = totalSteps,
                    ItemsProcessed = p.Processed,
                    TotalItems = p.Total,
                    CurrentFile = p.CurrentFile,
                    CurrentItem = p.CurrentItem
                });
            });

            processedData = await this.lspEnrichmentService.EnrichHypergraphAsync(
                data,
                workspacePath,
                enrichmentProgress,
                cancellationToken);

            result.NodesEnriched = processedData.Nodes.Values.Count(n => n.IsLspEnriched);
            this.logger.LogInformation("LSP enrichment completed: {Enriched}/{Total} nodes enriched",
                result.NodesEnriched, totalNodes);
        }

        // Step 2: Embedding Generation
        currentStep++;
        this.logger.LogInformation("Starting embedding generation for {NodeCount} nodes", totalNodes);

        var embeddingProgress = new Progress<EmbeddingProgress>(p =>
        {
            progress?.Report(new PipelineProgress
            {
                CurrentStep = PipelineStep.EmbeddingGeneration,
                StepNumber = currentStep,
                TotalSteps = totalSteps,
                ItemsProcessed = p.ProcessedItems,
                TotalItems = p.TotalItems,
                CurrentItem = p.CurrentItem
            });
        });

        // Use enhanced embedding text for LSP-enriched nodes
        var embeddings = await this.GenerateEnhancedEmbeddingsAsync(
            processedData,
            embeddingProgress,
            cancellationToken);

        result.Embeddings = embeddings;
        result.EndTime = DateTimeOffset.UtcNow;
        result.TotalNodesProcessed = totalNodes;

        this.logger.LogInformation(
            "Pipeline completed: {Embeddings} embeddings generated, {Enriched} nodes LSP-enriched in {Duration:F2}s",
            embeddings.Count,
            result.NodesEnriched,
            result.Duration.TotalSeconds);

        return result;
    }

    /// <summary>
    /// Processes a batch of nodes through the pipeline.
    /// </summary>
    /// <param name="nodes">The nodes to process.</param>
    /// <param name="workspacePath">The workspace root path for LSP operations.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of enriched code embeddings.</returns>
    public async Task<IReadOnlyList<EnrichedCodeEmbedding>> ProcessNodesAsync(
        IReadOnlyList<HypergraphNode> nodes,
        string workspacePath,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var processedNodes = nodes;

        // Step 1: LSP Enrichment (if enabled)
        if (this.IsLspEnrichmentAvailable && this.lspEnrichmentService != null)
        {
            this.logger.LogInformation("Starting LSP enrichment for {NodeCount} nodes", nodes.Count);

            var enrichmentProgress = new Progress<EnrichmentProgress>(p =>
            {
                progress?.Report(new PipelineProgress
                {
                    CurrentStep = PipelineStep.LspEnrichment,
                    StepNumber = 1,
                    TotalSteps = 2,
                    ItemsProcessed = p.Processed,
                    TotalItems = p.Total,
                    CurrentFile = p.CurrentFile,
                    CurrentItem = p.CurrentItem
                });
            });

            processedNodes = await this.lspEnrichmentService.EnrichBatchAsync(
                nodes,
                workspacePath,
                enrichmentProgress,
                cancellationToken);
        }

        // Step 2: Generate embeddings
        var results = new List<EnrichedCodeEmbedding>(processedNodes.Count);

        // Prepare texts - use enhanced text for enriched nodes
        var nodeTexts = processedNodes.Select(n =>
            n.IsLspEnriched ? n.GetEnhancedEmbeddingText() : n.GetEmbeddingText()).ToList();

        var embeddings = await this.embeddingService.Generator.GenerateBatchEmbeddingsAsync(
            nodeTexts, cancellationToken);

        for (var i = 0; i < processedNodes.Count; i++)
        {
            var node = processedNodes[i];
            var embedding = embeddings[i];

            results.Add(new EnrichedCodeEmbedding
            {
                NodeId = node.Id,
                FilePath = node.FilePath,
                ElementType = node.NodeType,
                Name = node.Name,
                SourceText = nodeTexts[i],
                Vector = embedding,
                StartLine = node.LineStart,
                EndLine = node.LineEnd,
                Language = node.Language,
                IsLspEnriched = node.IsLspEnriched,
                Signature = node.Metadata.TryGetValue("Signature", out var sig) ? sig?.ToString() : null,
                Documentation = node.Metadata.TryGetValue("Documentation", out var doc) ? doc?.ToString() : null,
                ReturnType = node.Metadata.TryGetValue("ReturnType", out var ret) ? ret?.ToString() : null,
                ParameterCount = node.Metadata.TryGetValue("ParameterCount", out var pc) && pc is int count ? count : 0,
                ReferenceCount = node.Metadata.TryGetValue("ReferenceCount", out var rc) && rc is int refCount ? refCount : 0,
                CallerCount = node.Metadata.TryGetValue("IncomingCallCount", out var ic) && ic is int inCount ? inCount : 0,
                CalleeCount = node.Metadata.TryGetValue("OutgoingCallCount", out var oc) && oc is int outCount ? outCount : 0
            });

            if ((i + 1) % 100 == 0)
            {
                progress?.Report(new PipelineProgress
                {
                    CurrentStep = PipelineStep.EmbeddingGeneration,
                    StepNumber = this.IsLspEnrichmentAvailable ? 2 : 1,
                    TotalSteps = this.IsLspEnrichmentAvailable ? 2 : 1,
                    ItemsProcessed = i + 1,
                    TotalItems = processedNodes.Count,
                    CurrentItem = node.Name
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Generates embeddings using enhanced text for LSP-enriched nodes.
    /// </summary>
    private async Task<IReadOnlyList<EnrichedCodeEmbedding>> GenerateEnhancedEmbeddingsAsync(
        HypergraphData data,
        IProgress<EmbeddingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var nodes = data.Nodes.Values.ToList();
        var results = new List<EnrichedCodeEmbedding>(nodes.Count);
        var processedCount = 0;

        // Prepare texts - use enhanced text for enriched nodes
        var nodeTexts = nodes.Select(n =>
            n.IsLspEnriched ? n.GetEnhancedEmbeddingText() : n.GetEmbeddingText()).ToList();

        // Generate embeddings in batch
        var embeddings = await this.embeddingService.Generator.GenerateBatchEmbeddingsAsync(
            nodeTexts, cancellationToken);

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var embedding = embeddings[i];

            results.Add(new EnrichedCodeEmbedding
            {
                NodeId = node.Id,
                FilePath = node.FilePath,
                ElementType = node.NodeType,
                Name = node.Name,
                SourceText = nodeTexts[i],
                Vector = embedding,
                StartLine = node.LineStart,
                EndLine = node.LineEnd,
                Language = node.Language,
                IsLspEnriched = node.IsLspEnriched,
                Signature = node.Metadata.TryGetValue("Signature", out var sig) ? sig?.ToString() : null,
                Documentation = node.Metadata.TryGetValue("Documentation", out var doc) ? doc?.ToString() : null,
                ReturnType = node.Metadata.TryGetValue("ReturnType", out var ret) ? ret?.ToString() : null,
                ParameterCount = node.Metadata.TryGetValue("ParameterCount", out var pc) && pc is int count ? count : 0,
                ReferenceCount = node.Metadata.TryGetValue("ReferenceCount", out var rc) && rc is int refCount ? refCount : 0,
                CallerCount = node.Metadata.TryGetValue("IncomingCallCount", out var ic) && ic is int inCount ? inCount : 0,
                CalleeCount = node.Metadata.TryGetValue("OutgoingCallCount", out var oc) && oc is int outCount ? outCount : 0
            });

            processedCount++;

            if (processedCount % 100 == 0)
            {
                progress?.Report(new EmbeddingProgress
                {
                    TotalItems = nodes.Count,
                    ProcessedItems = processedCount,
                    CurrentItem = node.Name
                });

                this.logger.LogDebug(
                    "Generated embeddings for {Processed}/{Total} nodes",
                    processedCount,
                    nodes.Count);
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        if (this.lspEnrichmentService != null)
        {
            await this.lspEnrichmentService.DisposeAsync();
        }

        this.embeddingService.Dispose();

        this.logger.LogInformation("EnrichedEmbeddingPipeline disposed");
    }
}

/// <summary>
/// Configuration options for the enriched embedding pipeline.
/// </summary>
public class EnrichedEmbeddingOptions
{
    /// <summary>
    /// Gets or sets whether LSP enrichment is enabled.
    /// </summary>
    public bool EnableLspEnrichment { get; set; } = true;

    /// <summary>
    /// Gets or sets the batch size for processing nodes.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets whether to use parallel processing for enrichment.
    /// </summary>
    public bool UseParallelProcessing { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum degree of parallelism.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// Gets or sets whether to continue on enrichment errors.
    /// </summary>
    public bool ContinueOnEnrichmentErrors { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to cache enriched data.
    /// </summary>
    public bool EnableCaching { get; set; } = false;

    /// <summary>
    /// Gets or sets the cache directory path.
    /// </summary>
    public string? CacheDirectory { get; set; }
}

/// <summary>
/// Result of the enriched embedding pipeline.
/// </summary>
public class EnrichedEmbeddingResult
{
    /// <summary>
    /// Gets or sets the generated embeddings.
    /// </summary>
    public IReadOnlyList<EnrichedCodeEmbedding> Embeddings { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of nodes that were LSP-enriched.
    /// </summary>
    public int NodesEnriched { get; set; }

    /// <summary>
    /// Gets or sets the total number of nodes processed.
    /// </summary>
    public int TotalNodesProcessed { get; set; }

    /// <summary>
    /// Gets or sets when processing started.
    /// </summary>
    public DateTimeOffset StartTime { get; set; }

    /// <summary>
    /// Gets or sets when processing ended.
    /// </summary>
    public DateTimeOffset EndTime { get; set; }

    /// <summary>
    /// Gets the total processing duration.
    /// </summary>
    public TimeSpan Duration => this.EndTime - this.StartTime;

    /// <summary>
    /// Gets the enrichment percentage.
    /// </summary>
    public double EnrichmentPercentage =>
        this.TotalNodesProcessed == 0 ? 0 : (double)this.NodesEnriched / this.TotalNodesProcessed * 100;
}

/// <summary>
/// Extended code embedding with LSP enrichment data.
/// </summary>
public class EnrichedCodeEmbedding : CodeEmbedding
{
    /// <summary>
    /// Gets or sets whether this embedding was generated from LSP-enriched data.
    /// </summary>
    public bool IsLspEnriched { get; set; }

    /// <summary>
    /// Gets or sets the type signature from LSP hover.
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>
    /// Gets or sets the documentation from LSP hover.
    /// </summary>
    public string? Documentation { get; set; }

    /// <summary>
    /// Gets or sets the return type for methods.
    /// </summary>
    public string? ReturnType { get; set; }

    /// <summary>
    /// Gets or sets the number of parameters.
    /// </summary>
    public int ParameterCount { get; set; }

    /// <summary>
    /// Gets or sets the number of references found.
    /// </summary>
    public int ReferenceCount { get; set; }

    /// <summary>
    /// Gets or sets the number of callers (incoming calls).
    /// </summary>
    public int CallerCount { get; set; }

    /// <summary>
    /// Gets or sets the number of callees (outgoing calls).
    /// </summary>
    public int CalleeCount { get; set; }
}

/// <summary>
/// Progress information for the pipeline.
/// </summary>
public class PipelineProgress
{
    /// <summary>
    /// Gets or sets the current pipeline step.
    /// </summary>
    public PipelineStep CurrentStep { get; set; }

    /// <summary>
    /// Gets or sets the current step number (1-based).
    /// </summary>
    public int StepNumber { get; set; }

    /// <summary>
    /// Gets or sets the total number of steps.
    /// </summary>
    public int TotalSteps { get; set; }

    /// <summary>
    /// Gets or sets the number of items processed in current step.
    /// </summary>
    public int ItemsProcessed { get; set; }

    /// <summary>
    /// Gets or sets the total number of items in current step.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets the current file being processed.
    /// </summary>
    public string? CurrentFile { get; set; }

    /// <summary>
    /// Gets or sets the current item being processed.
    /// </summary>
    public string? CurrentItem { get; set; }

    /// <summary>
    /// Gets the overall progress percentage.
    /// </summary>
    public double OverallPercentage
    {
        get
        {
            if (this.TotalSteps == 0) return 0;
            var stepWeight = 100.0 / this.TotalSteps;
            var completedSteps = (this.StepNumber - 1) * stepWeight;
            var currentStepProgress = this.TotalItems == 0 ? 0 : (double)this.ItemsProcessed / this.TotalItems * stepWeight;
            return completedSteps + currentStepProgress;
        }
    }

    /// <summary>
    /// Gets the current step progress percentage.
    /// </summary>
    public double StepPercentage => this.TotalItems == 0 ? 0 : (double)this.ItemsProcessed / this.TotalItems * 100;
}

/// <summary>
/// Pipeline processing steps.
/// </summary>
public enum PipelineStep
{
    /// <summary>
    /// LSP enrichment step.
    /// </summary>
    LspEnrichment,

    /// <summary>
    /// Embedding generation step.
    /// </summary>
    EmbeddingGeneration
}