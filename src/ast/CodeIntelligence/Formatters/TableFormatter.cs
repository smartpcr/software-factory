// -----------------------------------------------------------------------
// <copyright file="TableFormatter.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Formatters;

using System.Text;
using CodeIntelligence.Services;

/// <summary>
/// Table output formatter for console display.
/// </summary>
public class TableFormatter : IOutputFormatter
{
    /// <inheritdoc/>
    public string FormatName => "table";

    /// <inheritdoc/>
    public string Format(RepositoryAnalysisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════════════════════");
        sb.AppendLine($"  Repository Analysis: {result.RepositoryPath}");
        sb.AppendLine($"  Analyzed: {result.AnalyzedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"  Files: {result.TotalFilesAnalyzed}");
        sb.AppendLine("═══════════════════════════════════════════════════════════════════");
        sb.AppendLine();

        // API Endpoints
        if (result.ApiEndpoints.Count > 0)
        {
            sb.AppendLine("┌─ API Endpoints ─────────────────────────────────────────────────┐");
            foreach (var endpoint in result.ApiEndpoints.Take(20))
            {
                sb.AppendLine($"│ {endpoint.HttpMethod,-6} {endpoint.RouteTemplate,-50} │");
            }

            if (result.ApiEndpoints.Count > 20)
            {
                sb.AppendLine($"│ ... and {result.ApiEndpoints.Count - 20} more endpoints                              │");
            }

            sb.AppendLine("└─────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();
        }

        // Security
        if (result.AuthorizationInfo.Count > 0 || result.SecretAccesses.Count > 0)
        {
            sb.AppendLine("┌─ Security ──────────────────────────────────────────────────────┐");
            sb.AppendLine($"│ Authorization Requirements: {result.AuthorizationInfo.Count,-35} │");
            sb.AppendLine($"│ Secret Accesses: {result.SecretAccesses.Count,-46} │");
            sb.AppendLine($"│ Certificate Usages: {result.CertificateUsages.Count,-43} │");
            sb.AppendLine($"│ Crypto Operations: {result.CryptoOperations.Count,-44} │");
            sb.AppendLine("└─────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();
        }

        // Observability
        if (result.LogStatements.Count > 0 || result.Metrics.Count > 0)
        {
            sb.AppendLine("┌─ Observability ─────────────────────────────────────────────────┐");
            sb.AppendLine($"│ Log Statements: {result.LogStatements.Count,-47} │");
            sb.AppendLine($"│ Metrics: {result.Metrics.Count,-54} │");
            sb.AppendLine($"│ Trace Spans: {result.TraceSpans.Count,-50} │");
            sb.AppendLine($"│ ETW Events: {result.EtwEvents.Count,-51} │");
            sb.AppendLine($"│ Health Checks: {result.HealthChecks.Count,-48} │");
            sb.AppendLine("└─────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();
        }

        // Data
        if (result.DatabaseEntities.Count > 0 || result.CacheAccesses.Count > 0)
        {
            sb.AppendLine("┌─ Data & Persistence ────────────────────────────────────────────┐");
            sb.AppendLine($"│ Database Entities: {result.DatabaseEntities.Count,-44} │");
            sb.AppendLine($"│ Database Queries: {result.DatabaseQueries.Count,-45} │");
            sb.AppendLine($"│ Cache Accesses: {result.CacheAccesses.Count,-47} │");
            sb.AppendLine($"│ File Accesses: {result.FileAccesses.Count,-48} │");
            sb.AppendLine("└─────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();
        }

        // Error Handling
        if (result.Exceptions.Count > 0 || result.ResiliencePolicies.Count > 0)
        {
            sb.AppendLine("┌─ Error Handling ────────────────────────────────────────────────┐");
            sb.AppendLine($"│ Exceptions: {result.Exceptions.Count,-51} │");
            sb.AppendLine($"│ Try-Catch Blocks: {result.TryCatchBlocks.Count,-45} │");
            sb.AppendLine($"│ Resilience Policies: {result.ResiliencePolicies.Count,-42} │");
            sb.AppendLine("└─────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();
        }

        // Domain
        if (result.Aggregates.Count > 0 || result.DomainEvents.Count > 0)
        {
            sb.AppendLine("┌─ Domain Model ──────────────────────────────────────────────────┐");
            sb.AppendLine($"│ Aggregates/Entities: {result.Aggregates.Count,-42} │");
            sb.AppendLine($"│ Domain Events: {result.DomainEvents.Count,-48} │");
            sb.AppendLine($"│ CQRS Patterns: {result.CqrsPatterns.Count,-48} │");
            sb.AppendLine("└─────────────────────────────────────────────────────────────────┘");
        }

        return sb.ToString();
    }

    /// <inheritdoc/>
    public string FormatSummary(AnalysisSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("┌────────────────────────────────────────────────────────┐");
        sb.AppendLine($"│  Analysis Summary                                      │");
        sb.AppendLine("├────────────────────────────────────────────────────────┤");
        sb.AppendLine($"│  Repository: {summary.RepositoryPath,-40} │");
        sb.AppendLine($"│  Files Analyzed: {summary.TotalFilesAnalyzed,-37} │");
        sb.AppendLine($"│  Errors: {summary.ErrorCount,-45} │");
        sb.AppendLine("├────────────────────────────────────────────────────────┤");
        sb.AppendLine($"│  API Endpoints: {summary.ApiEndpointCount,-38} │");
        sb.AppendLine($"│  HTTP Clients: {summary.HttpClientCount,-39} │");
        sb.AppendLine($"│  Authorization: {summary.AuthorizationCount,-38} │");
        sb.AppendLine($"│  Log Statements: {summary.LogStatementCount,-37} │");
        sb.AppendLine($"│  Metrics: {summary.MetricCount,-44} │");
        sb.AppendLine($"│  Database Entities: {summary.DatabaseEntityCount,-34} │");
        sb.AppendLine($"│  Aggregates: {summary.AggregateCount,-41} │");
        sb.AppendLine($"│  Resilience Policies: {summary.ResiliencePolicyCount,-32} │");
        sb.AppendLine("└────────────────────────────────────────────────────────┘");
        return sb.ToString();
    }
}
