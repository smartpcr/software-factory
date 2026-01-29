// -----------------------------------------------------------------------
// <copyright file="RelationshipGraphBuilder.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Graph;

using System.Diagnostics;
using CodeIntelligence.Models;
using CodeIntelligence.Rules;
using CodeIntelligence.Strategies;

/// <summary>
/// Orchestrates detection and builds a relationship graph.
/// </summary>
public sealed class RelationshipGraphBuilder : IDisposable
{
    private readonly RelationshipRegistry registry;
    private readonly DetectionStrategyFactory strategyFactory;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelationshipGraphBuilder"/> class.
    /// </summary>
    /// <param name="registry">The rule registry.</param>
    public RelationshipGraphBuilder(RelationshipRegistry registry)
    {
        this.registry = registry;
        this.strategyFactory = new DetectionStrategyFactory();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RelationshipGraphBuilder"/> class.
    /// </summary>
    /// <param name="registry">The rule registry.</param>
    /// <param name="strategyFactory">The strategy factory.</param>
    public RelationshipGraphBuilder(RelationshipRegistry registry, DetectionStrategyFactory strategyFactory)
    {
        this.registry = registry;
        this.strategyFactory = strategyFactory;
    }

    /// <summary>
    /// Builds a relationship graph for a repository.
    /// </summary>
    /// <param name="context">The detection context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The build result containing all detected relationships.</returns>
    public async Task<GraphBuildResult> BuildAsync(
        DetectionContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var allRelationships = new List<DetectedRelationship>();
        var errors = new List<DetectionError>();
        var processedRules = new List<string>();

        // Get all source files
        var sourceFiles = this.CollectSourceFiles(context);

        // Get rules ordered by priority
        var rules = this.registry.GetRulesOrderedByPriority();

        foreach (var rule in rules)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var ruleRelationships = await this.ApplyRuleAsync(rule, sourceFiles, cancellationToken);
                allRelationships.AddRange(ruleRelationships);
                processedRules.Add(rule.Id);
            }
            catch (Exception ex)
            {
                errors.Add(new DetectionError
                {
                    FilePath = string.Empty,
                    Message = $"Rule {rule.Id} failed: {ex.Message}",
                    Exception = ex
                });
            }
        }

        stopwatch.Stop();

        // Filter by confidence threshold and deduplicate
        var filteredRelationships = this.FilterAndDeduplicate(allRelationships);

        return new GraphBuildResult
        {
            Relationships = filteredRelationships,
            ProcessedRules = processedRules,
            AnalyzedFiles = sourceFiles,
            Errors = errors,
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Builds a relationship graph for specific categories.
    /// </summary>
    /// <param name="context">The detection context.</param>
    /// <param name="categories">The categories to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The build result.</returns>
    public async Task<GraphBuildResult> BuildForCategoriesAsync(
        DetectionContext context,
        IEnumerable<string> categories,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var allRelationships = new List<DetectedRelationship>();
        var errors = new List<DetectionError>();
        var processedRules = new List<string>();

        var sourceFiles = this.CollectSourceFiles(context);

        foreach (var category in categories)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var rules = this.registry.GetEnabledRulesByCategory(category)
                .OrderByDescending(r => r.Priority);

            foreach (var rule in rules)
            {
                try
                {
                    var ruleRelationships = await this.ApplyRuleAsync(rule, sourceFiles, cancellationToken);
                    allRelationships.AddRange(ruleRelationships);
                    processedRules.Add(rule.Id);
                }
                catch (Exception ex)
                {
                    errors.Add(new DetectionError
                    {
                        FilePath = string.Empty,
                        Message = $"Rule {rule.Id} failed: {ex.Message}",
                        Exception = ex
                    });
                }
            }
        }

        stopwatch.Stop();

        var filteredRelationships = this.FilterAndDeduplicate(allRelationships);

        return new GraphBuildResult
        {
            Relationships = filteredRelationships,
            ProcessedRules = processedRules,
            AnalyzedFiles = sourceFiles,
            Errors = errors,
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Applies a single rule to the source files.
    /// </summary>
    private async Task<IReadOnlyList<DetectedRelationship>> ApplyRuleAsync(
        DetectionRule rule,
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken)
    {
        var relationships = new List<DetectedRelationship>();

        foreach (var pattern in rule.Detection)
        {
            var strategy = this.strategyFactory.GetStrategyForPattern(pattern);
            if (strategy == null)
            {
                continue;
            }

            var detected = await strategy.DetectAsync(rule, sourceFiles, cancellationToken);
            relationships.AddRange(detected);
        }

        return relationships;
    }

    /// <summary>
    /// Collects source files from the context.
    /// </summary>
    private IReadOnlyList<string> CollectSourceFiles(DetectionContext context)
    {
        var files = new List<string>();

        // Collect from repository root
        if (Directory.Exists(context.RepositoryRoot))
        {
            files.AddRange(Directory.GetFiles(context.RepositoryRoot, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(context.RepositoryRoot, "*.csproj", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(context.RepositoryRoot, "*.json", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(context.RepositoryRoot, "*.xml", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(context.RepositoryRoot, "*.yaml", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(context.RepositoryRoot, "*.yml", SearchOption.AllDirectories));
        }

        // Filter out test files if not included
        if (!context.IncludeTests)
        {
            files = files.Where(f =>
                !f.Contains("\\test\\", StringComparison.OrdinalIgnoreCase) &&
                !f.Contains("/test/", StringComparison.OrdinalIgnoreCase) &&
                !f.Contains("\\tests\\", StringComparison.OrdinalIgnoreCase) &&
                !f.Contains("/tests/", StringComparison.OrdinalIgnoreCase) &&
                !f.EndsWith(".Tests.cs", StringComparison.OrdinalIgnoreCase) &&
                !f.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Filter out common excluded directories
        files = files.Where(f =>
            !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("/.git/", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return files;
    }

    /// <summary>
    /// Filters relationships by confidence and removes duplicates.
    /// </summary>
    private IReadOnlyList<DetectedRelationship> FilterAndDeduplicate(
        IReadOnlyList<DetectedRelationship> relationships)
    {
        // Group by source+target+type to find duplicates
        var grouped = relationships
            .GroupBy(r => $"{r.Source.FullyQualifiedName}|{r.Target?.FullyQualifiedName}|{r.RelationshipType}");

        var result = new List<DetectedRelationship>();

        foreach (var group in grouped)
        {
            // Keep the one with highest confidence
            var best = group.OrderByDescending(r => r.Confidence).First();
            result.Add(best);
        }

        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this.disposed)
        {
            this.strategyFactory.Dispose();
            this.disposed = true;
        }
    }
}

/// <summary>
/// Result of building a relationship graph.
/// </summary>
public record GraphBuildResult
{
    /// <summary>
    /// Gets the detected relationships.
    /// </summary>
    public required IReadOnlyList<DetectedRelationship> Relationships { get; init; }

    /// <summary>
    /// Gets the IDs of rules that were processed.
    /// </summary>
    public required IReadOnlyList<string> ProcessedRules { get; init; }

    /// <summary>
    /// Gets the files that were analyzed.
    /// </summary>
    public required IReadOnlyList<string> AnalyzedFiles { get; init; }

    /// <summary>
    /// Gets any errors that occurred.
    /// </summary>
    public required IReadOnlyList<DetectionError> Errors { get; init; }

    /// <summary>
    /// Gets the total time taken.
    /// </summary>
    public TimeSpan Duration { get; init; }
}
