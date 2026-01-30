// -----------------------------------------------------------------------
// <copyright file="MermaidFormatter.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Formatters;

using System.Linq;
using System.Text;
using CodeIntelligence.Services;

/// <summary>
/// Mermaid diagram output formatter.
/// </summary>
public class MermaidFormatter : IOutputFormatter
{
    /// <inheritdoc/>
    public string FormatName => "mermaid";

    /// <inheritdoc/>
    public string Format(RepositoryAnalysisResult result)
    {
        var sb = new StringBuilder();

        // Generate architecture overview diagram
        sb.AppendLine("```mermaid");
        sb.AppendLine("graph TB");
        sb.AppendLine("    subgraph API[\"API Layer\"]");

        var endpoints = result.ApiEndpoints.Take(10).ToList();
        for (int i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            var nodeId = $"EP{i}";
            sb.AppendLine($"        {nodeId}[\"{endpoint.HttpMethod} {this.SanitizeForMermaid(endpoint.RouteTemplate)}\"]");
        }

        sb.AppendLine("    end");
        sb.AppendLine();

        // Services
        if (result.Aggregates.Count > 0)
        {
            sb.AppendLine("    subgraph Domain[\"Domain Layer\"]");
            var aggregates = result.Aggregates
                .Where(a => a.EntityType == Analyzers.DomainEntityType.AggregateRoot)
                .Take(5)
                .ToList();

            for (int i = 0; i < aggregates.Count; i++)
            {
                var agg = aggregates[i];
                sb.AppendLine($"        AGG{i}[[\"{agg.Name}\"]]");
            }

            sb.AppendLine("    end");
            sb.AppendLine();
        }

        // Database
        if (result.DatabaseEntities.Count > 0)
        {
            sb.AppendLine("    subgraph Data[\"Data Layer\"]");
            var entities = result.DatabaseEntities.Take(5).ToList();
            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                sb.AppendLine($"        DB{i}[(\"{entity.ClassName}\")]");
            }

            sb.AppendLine("    end");
            sb.AppendLine();
        }

        // External services (HTTP clients)
        if (result.HttpClientRegistrations.Count > 0)
        {
            sb.AppendLine("    subgraph External[\"External Services\"]");
            var clients = result.HttpClientRegistrations.Take(5).ToList();
            for (int i = 0; i < clients.Count; i++)
            {
                var client = clients[i];
                var name = client.TypedClientType ?? "HttpClient";
                var sanitizedName = this.SanitizeForMermaid(name);
                sb.AppendLine($"        EXT{i}{{\"{sanitizedName}\"}}");
            }

            sb.AppendLine("    end");
            sb.AppendLine();
        }

        // Connections
        sb.AppendLine("    %% Connections");
        if (endpoints.Count > 0 && result.Aggregates.Count > 0)
        {
            sb.AppendLine("    API --> Domain");
        }

        if (result.Aggregates.Count > 0 && result.DatabaseEntities.Count > 0)
        {
            sb.AppendLine("    Domain --> Data");
        }

        if (endpoints.Count > 0 && result.HttpClientRegistrations.Count > 0)
        {
            sb.AppendLine("    API --> External");
        }

        sb.AppendLine("```");

        return sb.ToString();
    }

    /// <inheritdoc/>
    public string FormatSummary(AnalysisSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("pie showData");
        sb.AppendLine("    title Analysis Distribution");
        sb.AppendLine($"    \"API Endpoints\" : {summary.ApiEndpointCount}");
        sb.AppendLine($"    \"Log Statements\" : {summary.LogStatementCount}");
        sb.AppendLine($"    \"Database Entities\" : {summary.DatabaseEntityCount}");
        sb.AppendLine($"    \"Security Items\" : {summary.AuthorizationCount + summary.SecretAccessCount}");
        sb.AppendLine($"    \"Domain Items\" : {summary.AggregateCount + summary.DomainEventCount}");
        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>
    /// Generates an API endpoint flowchart.
    /// </summary>
    public string FormatApiFlow(RepositoryAnalysisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("flowchart LR");

        var groupedEndpoints = result.ApiEndpoints
            .GroupBy(e => e.ControllerName)
            .Take(5);

        int nodeId = 0;
        foreach (var group in groupedEndpoints)
        {
            var controllerName = group.Key ?? "Unknown";
            sb.AppendLine($"    subgraph {this.SanitizeForMermaid(controllerName)}[\"{controllerName}\"]");

            foreach (var endpoint in group.Take(5))
            {
                var method = endpoint.HttpMethod ?? "GET";
                var route = this.SanitizeForMermaid(endpoint.RouteTemplate ?? "/");
                sb.AppendLine($"        N{nodeId}[\"{method} {route}\"]");
                nodeId++;
            }

            sb.AppendLine("    end");
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>
    /// Generates a security overview diagram.
    /// </summary>
    public string FormatSecurityOverview(RepositoryAnalysisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("graph TD");
        sb.AppendLine("    subgraph Security[\"Security Overview\"]");

        int nodeId = 0;

        // Authorization
        var authGroups = result.AuthorizationInfo
            .GroupBy(a => a.Scope)
            .Take(3);

        foreach (var group in authGroups)
        {
            sb.AppendLine($"        AUTH{nodeId}{{\"🔐 {group.Key}: {group.Count()} items\"}}");
            nodeId++;
        }

        // Secrets
        var secretTypes = result.SecretAccesses
            .GroupBy(s => s.SecretType)
            .Take(3);

        foreach (var group in secretTypes)
        {
            sb.AppendLine($"        SEC{nodeId}{{\"🔑 {group.Key}: {group.Count()} accesses\"}}");
            nodeId++;
        }

        sb.AppendLine("    end");
        sb.AppendLine("```");
        return sb.ToString();
    }

    private string SanitizeForMermaid(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "unknown";
        }

        return text
            .Replace("\"", "'")
            .Replace("[", "(")
            .Replace("]", ")")
            .Replace("{", "(")
            .Replace("}", ")")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
