// -----------------------------------------------------------------------
// <copyright file="RepositoryAnalysisService.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeIntelligence.Analyzers;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Unified service for analyzing a repository using all available analyzers.
/// </summary>
public class RepositoryAnalysisService
{
    private readonly HttpEndpointAnalyzer endpointAnalyzer;
    private readonly ApiInfrastructureAnalyzer apiAnalyzer;
    private readonly SecurityAnalyzer securityAnalyzer;
    private readonly ObservabilityAnalyzer observabilityAnalyzer;
    private readonly ErrorHandlingAnalyzer errorHandlingAnalyzer;
    private readonly DomainModelAnalyzer domainModelAnalyzer;
    private readonly DatabaseAccessAnalyzer databaseAnalyzer;
    private readonly CacheAccessAnalyzer cacheAnalyzer;
    private readonly FileAccessAnalyzer fileAnalyzer;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepositoryAnalysisService"/> class.
    /// </summary>
    public RepositoryAnalysisService()
    {
        this.endpointAnalyzer = new HttpEndpointAnalyzer();
        this.apiAnalyzer = new ApiInfrastructureAnalyzer();
        this.securityAnalyzer = new SecurityAnalyzer();
        this.observabilityAnalyzer = new ObservabilityAnalyzer();
        this.errorHandlingAnalyzer = new ErrorHandlingAnalyzer();
        this.domainModelAnalyzer = new DomainModelAnalyzer();
        this.databaseAnalyzer = new DatabaseAccessAnalyzer();
        this.cacheAnalyzer = new CacheAccessAnalyzer();
        this.fileAnalyzer = new FileAccessAnalyzer();
    }

    /// <summary>
    /// Analyzes a repository and returns comprehensive analysis results.
    /// </summary>
    public async Task<RepositoryAnalysisResult> AnalyzeRepositoryAsync(
        string repositoryPath,
        AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AnalysisOptions();

        var csFiles = Directory.GetFiles(repositoryPath, "*.cs", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        })
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .ToList();

        var result = new RepositoryAnalysisResult
        {
            RepositoryPath = repositoryPath,
            AnalyzedAt = DateTime.UtcNow,
            TotalFilesAnalyzed = csFiles.Count,
        };

        var trees = new List<Microsoft.CodeAnalysis.SyntaxTree>();

        foreach (var file in csFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var code = await File.ReadAllTextAsync(file, cancellationToken);
                var tree = CSharpSyntaxTree.ParseText(code, path: file);
                trees.Add(tree);

                // Analyze each file
                await this.AnalyzeFileAsync(file, tree, result, options);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new AnalysisError
                {
                    FilePath = file,
                    Message = ex.Message,
                });
            }
        }

        // Build HTTP client wrapper chain if requested
        if (options.AnalyzeHttpClients)
        {
            result.HttpClientWrapperTypes = this.apiAnalyzer.BuildHttpClientWrapperChain(trees);
        }

        return result;
    }

    /// <summary>
    /// Gets a summary of the analysis results.
    /// </summary>
    public AnalysisSummary GetSummary(RepositoryAnalysisResult result)
    {
        return new AnalysisSummary
        {
            RepositoryPath = result.RepositoryPath,
            TotalFilesAnalyzed = result.TotalFilesAnalyzed,
            ErrorCount = result.Errors.Count,

            // API
            ApiEndpointCount = result.ApiEndpoints.Count,
            HttpClientCount = result.HttpClientRegistrations.Count,

            // Security
            AuthorizationCount = result.AuthorizationInfo.Count,
            SecretAccessCount = result.SecretAccesses.Count,
            CertificateCount = result.CertificateUsages.Count,

            // Observability
            LogStatementCount = result.LogStatements.Count,
            MetricCount = result.Metrics.Count,
            TraceSpanCount = result.TraceSpans.Count,
            EtwEventCount = result.EtwEvents.Count,
            HealthCheckCount = result.HealthChecks.Count,

            // Data
            DatabaseEntityCount = result.DatabaseEntities.Count,
            CacheAccessCount = result.CacheAccesses.Count,
            FileAccessCount = result.FileAccesses.Count,

            // Error Handling
            ExceptionCount = result.Exceptions.Count,
            TryCatchBlockCount = result.TryCatchBlocks.Count,
            ResiliencePolicyCount = result.ResiliencePolicies.Count,

            // Domain
            AggregateCount = result.Aggregates.Count,
            DomainEventCount = result.DomainEvents.Count,
            CqrsPatternCount = result.CqrsPatterns.Count,
        };
    }

    private async Task AnalyzeFileAsync(
        string filePath,
        Microsoft.CodeAnalysis.SyntaxTree tree,
        RepositoryAnalysisResult result,
        AnalysisOptions options)
    {
        await Task.CompletedTask; // Allow for async file operations

        // API Analysis
        if (options.AnalyzeApi)
        {
            var endpoints = this.endpointAnalyzer.Analyze(tree, filePath);
            result.ApiEndpoints.AddRange(endpoints);

            // HttpClient registrations are analyzed via wrapper chain at repository level
        }

        // Security Analysis
        if (options.AnalyzeSecurity)
        {
            var auth = this.securityAnalyzer.AnalyzeAuthorization(tree, filePath);
            result.AuthorizationInfo.AddRange(auth);

            var secrets = this.securityAnalyzer.AnalyzeSecrets(tree, filePath);
            result.SecretAccesses.AddRange(secrets);

            var certs = this.securityAnalyzer.AnalyzeCertificates(tree, filePath);
            result.CertificateUsages.AddRange(certs);

            var crypto = this.securityAnalyzer.AnalyzeCryptography(tree, filePath);
            result.CryptoOperations.AddRange(crypto);
        }

        // Observability Analysis
        if (options.AnalyzeObservability)
        {
            var logs = this.observabilityAnalyzer.AnalyzeLogging(tree, filePath);
            result.LogStatements.AddRange(logs);

            var metrics = this.observabilityAnalyzer.AnalyzeMetrics(tree, filePath);
            result.Metrics.AddRange(metrics);

            var spans = this.observabilityAnalyzer.AnalyzeTracing(tree, filePath);
            result.TraceSpans.AddRange(spans);

            var etw = this.observabilityAnalyzer.AnalyzeEtwEvents(tree, filePath);
            result.EtwEvents.AddRange(etw);

            var counters = this.observabilityAnalyzer.AnalyzeEventCounters(tree, filePath);
            result.EventCounters.AddRange(counters);

            var health = this.observabilityAnalyzer.AnalyzeHealthChecks(tree, filePath);
            result.HealthChecks.AddRange(health);
        }

        // Data & Persistence Analysis
        if (options.AnalyzeData)
        {
            var entities = this.databaseAnalyzer.AnalyzeEntities(tree, filePath);
            result.DatabaseEntities.AddRange(entities);

            var queries = this.databaseAnalyzer.AnalyzeQueries(tree, filePath);
            result.DatabaseQueries.AddRange(queries);

            var mutations = this.databaseAnalyzer.AnalyzeMutations(tree, filePath);
            result.DatabaseMutations.AddRange(mutations);

            var cache = this.cacheAnalyzer.Analyze(tree, filePath);
            result.CacheAccesses.AddRange(cache);

            var files = this.fileAnalyzer.Analyze(tree, filePath);
            result.FileAccesses.AddRange(files);

            var blobs = this.fileAnalyzer.AnalyzeBlobStorage(tree, filePath);
            result.BlobStorageAccesses.AddRange(blobs);
        }

        // Error Handling Analysis
        if (options.AnalyzeErrorHandling)
        {
            var exceptions = this.errorHandlingAnalyzer.AnalyzeExceptions(tree, filePath);
            result.Exceptions.AddRange(exceptions);

            var tryCatch = this.errorHandlingAnalyzer.AnalyzeTryCatchBlocks(tree, filePath);
            result.TryCatchBlocks.AddRange(tryCatch);

            var resilience = this.errorHandlingAnalyzer.AnalyzeResiliencePolicies(tree, filePath);
            result.ResiliencePolicies.AddRange(resilience);
        }

        // Domain Model Analysis
        if (options.AnalyzeDomain)
        {
            var aggregates = this.domainModelAnalyzer.AnalyzeAggregates(tree, filePath);
            result.Aggregates.AddRange(aggregates);

            var events = this.domainModelAnalyzer.AnalyzeDomainEvents(tree, filePath);
            result.DomainEvents.AddRange(events);

            var cqrs = this.domainModelAnalyzer.AnalyzeCqrsPatterns(tree, filePath);
            result.CqrsPatterns.AddRange(cqrs);

            var raising = this.domainModelAnalyzer.AnalyzeEventRaising(tree, filePath);
            result.EventRaisings.AddRange(raising);
        }
    }
}

/// <summary>
/// Options for repository analysis.
/// </summary>
public class AnalysisOptions
{
    public bool AnalyzeApi { get; set; } = true;

    public bool AnalyzeSecurity { get; set; } = true;

    public bool AnalyzeObservability { get; set; } = true;

    public bool AnalyzeData { get; set; } = true;

    public bool AnalyzeErrorHandling { get; set; } = true;

    public bool AnalyzeDomain { get; set; } = true;

    public bool AnalyzeHttpClients { get; set; } = true;
}

/// <summary>
/// Complete repository analysis result.
/// </summary>
public class RepositoryAnalysisResult
{
    public required string RepositoryPath { get; set; }

    public DateTime AnalyzedAt { get; set; }

    public int TotalFilesAnalyzed { get; set; }

    public List<AnalysisError> Errors { get; set; } = new();

    // API
    public List<HttpEndpointInfo> ApiEndpoints { get; set; } = new();

    public List<HttpClientRegistration> HttpClientRegistrations { get; set; } = new();

    public HashSet<string> HttpClientWrapperTypes { get; set; } = new();

    // Security
    public List<AuthorizationInfo> AuthorizationInfo { get; set; } = new();

    public List<SecretAccessInfo> SecretAccesses { get; set; } = new();

    public List<CertificateUsageInfo> CertificateUsages { get; set; } = new();

    public List<CryptoOperationInfo> CryptoOperations { get; set; } = new();

    // Observability
    public List<LogStatementInfo> LogStatements { get; set; } = new();

    public List<MetricDefinitionInfo> Metrics { get; set; } = new();

    public List<TraceSpanInfo> TraceSpans { get; set; } = new();

    public List<EtwEventInfo> EtwEvents { get; set; } = new();

    public List<EventCounterInfo> EventCounters { get; set; } = new();

    public List<HealthCheckInfo> HealthChecks { get; set; } = new();

    // Data & Persistence
    public List<DatabaseEntityInfo> DatabaseEntities { get; set; } = new();

    public List<DatabaseQueryInfo> DatabaseQueries { get; set; } = new();

    public List<DatabaseMutationInfo> DatabaseMutations { get; set; } = new();

    public List<CacheAccessInfo> CacheAccesses { get; set; } = new();

    public List<FileAccessInfo> FileAccesses { get; set; } = new();

    public List<BlobStorageAccessInfo> BlobStorageAccesses { get; set; } = new();

    // Error Handling
    public List<ExceptionInfo> Exceptions { get; set; } = new();

    public List<TryCatchInfo> TryCatchBlocks { get; set; } = new();

    public List<ResiliencePolicyInfo> ResiliencePolicies { get; set; } = new();

    // Domain
    public List<AggregateInfo> Aggregates { get; set; } = new();

    public List<DomainEventInfo> DomainEvents { get; set; } = new();

    public List<CqrsPatternInfo> CqrsPatterns { get; set; } = new();

    public List<EventRaisingInfo> EventRaisings { get; set; } = new();
}

/// <summary>
/// Analysis error information.
/// </summary>
public class AnalysisError
{
    public required string FilePath { get; set; }

    public required string Message { get; set; }
}

/// <summary>
/// Summary of analysis results.
/// </summary>
public class AnalysisSummary
{
    public required string RepositoryPath { get; set; }

    public int TotalFilesAnalyzed { get; set; }

    public int ErrorCount { get; set; }

    // API
    public int ApiEndpointCount { get; set; }

    public int HttpClientCount { get; set; }

    // Security
    public int AuthorizationCount { get; set; }

    public int SecretAccessCount { get; set; }

    public int CertificateCount { get; set; }

    // Observability
    public int LogStatementCount { get; set; }

    public int MetricCount { get; set; }

    public int TraceSpanCount { get; set; }

    public int EtwEventCount { get; set; }

    public int HealthCheckCount { get; set; }

    // Data
    public int DatabaseEntityCount { get; set; }

    public int CacheAccessCount { get; set; }

    public int FileAccessCount { get; set; }

    // Error Handling
    public int ExceptionCount { get; set; }

    public int TryCatchBlockCount { get; set; }

    public int ResiliencePolicyCount { get; set; }

    // Domain
    public int AggregateCount { get; set; }

    public int DomainEventCount { get; set; }

    public int CqrsPatternCount { get; set; }
}
