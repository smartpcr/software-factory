// -----------------------------------------------------------------------
// <copyright file="MemoryMcpHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Mcp;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles MCP tool invocations by mapping JSON parameters to typed requests
/// and delegating to the appropriate interface per architecture.md §6.6.
/// </summary>
public sealed class MemoryMcpHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly IMemoryRetrievalEngine retrievalEngine;
    private readonly IMemoryGraphStore graphStore;
    private readonly IEvolutionEngine evolutionEngine;
    private readonly IIncrementalSync incrementalSync;
    private readonly ILogger<MemoryMcpHandler> logger;

    public MemoryMcpHandler(
        IMemoryRetrievalEngine retrievalEngine,
        IMemoryGraphStore graphStore,
        IEvolutionEngine evolutionEngine,
        IIncrementalSync incrementalSync,
        ILogger<MemoryMcpHandler> logger)
    {
        this.retrievalEngine = retrievalEngine;
        this.graphStore = graphStore;
        this.evolutionEngine = evolutionEngine;
        this.incrementalSync = incrementalSync;
        this.logger = logger;
    }

    /// <summary>
    /// Dispatches an MCP tool invocation by name.
    /// </summary>
    public async Task<string> HandleAsync(string toolName, JsonElement parameters, CancellationToken ct = default)
    {
        try
        {
            return toolName switch
            {
                "memory_query" => await this.HandleQueryAsync(parameters, ct).ConfigureAwait(false),
                "memory_status" => await this.HandleStatusAsync(ct).ConfigureAwait(false),
                "memory_record_expertise" => await this.HandleRecordExpertiseAsync(parameters, ct).ConfigureAwait(false),
                "memory_sync" => await this.HandleSyncAsync(parameters, ct).ConfigureAwait(false),
                "memory_consolidate" => await this.HandleConsolidateAsync(parameters, ct).ConfigureAwait(false),
                _ => SerializeError($"Unknown tool: {toolName}")
            };
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "MCP handler error for tool {ToolName}", toolName);
            return SerializeError(ex.Message);
        }
    }

    /// <summary>
    /// memory_query → IMemoryRetrievalEngine.RetrieveAsync
    /// </summary>
    internal async Task<string> HandleQueryAsync(JsonElement parameters, CancellationToken ct)
    {
        var queryText = GetOptionalString(parameters, "queryText");
        var tokenBudget = GetOptionalInt(parameters, "tokenBudget") ?? 8000;
        var modeStr = GetOptionalString(parameters, "mode");
        var atCommitIndex = GetOptionalInt(parameters, "atCommitIndex");

        // Validate tokenBudget range
        if (tokenBudget <= 0 || tokenBudget > 32000)
        {
            return SerializeError($"Invalid tokenBudget: {tokenBudget}. Must be between 1 and 32000");
        }

        // Validate mode if provided
        var mode = RetrievalMode.Hybrid;
        if (!string.IsNullOrEmpty(modeStr))
        {
            if (!Enum.TryParse<RetrievalMode>(modeStr, ignoreCase: true, out var parsedMode))
            {
                return SerializeError($"Invalid retrieval mode: {modeStr}. Valid values: StructuralDrill, CallTrace, SemanticSearch, ExpertiseRecall, Hybrid");
            }

            mode = parsedMode;
        }

        List<string>? seedNodeIds = null;
        if (parameters.TryGetProperty("seedNodeIds", out var seedProp))
        {
            if (seedProp.ValueKind != JsonValueKind.Array)
            {
                return SerializeError("seedNodeIds must be an array of strings");
            }

            seedNodeIds = new List<string>();
            foreach (var item in seedProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return SerializeError("Each seedNodeIds item must be a string");
                }

                var s = item.GetString()!;
                if (string.IsNullOrWhiteSpace(s))
                {
                    return SerializeError("Each seedNodeIds item must be a non-empty string");
                }

                seedNodeIds.Add(s);
            }
        }

        var request = new RetrievalRequest(
            QueryText: queryText,
            SeedNodeIds: seedNodeIds,
            Mode: mode,
            TokenBudget: tokenBudget,
            AtCommitIndex: atCommitIndex);

        var result = await this.retrievalEngine.RetrieveAsync(request, ct).ConfigureAwait(false);

        var responseNodes = new List<object>();
        foreach (var scoredNode in result.Nodes)
        {
            responseNodes.Add(new
            {
                nodeId = scoredNode.Node.Inner.Id,
                nodeType = scoredNode.Node.NodeKind,
                compositeScore = scoredNode.CompositeScore
            });
        }

        var response = new
        {
            contextMarkdown = result.ContextMarkdown,
            tokensUsed = result.TokensUsed,
            nodes = responseNodes,
            modesExecuted = result.ModesExecuted,
            diagnostics = result.Diagnostics
        };

        return JsonSerializer.Serialize(response, SerializerOptions);
    }

    /// <summary>
    /// memory_status → IMemoryGraphStore.GetMemoryStatisticsAsync
    /// </summary>
    internal async Task<string> HandleStatusAsync(CancellationToken ct)
    {
        var stats = await this.graphStore.GetMemoryStatisticsAsync(ct).ConfigureAwait(false);

        var response = new
        {
            totalNodes = stats.TotalNodes,
            totalEdges = stats.TotalEdges,
            totalExpertise = stats.TotalExpertise,
            archivedExpertise = stats.ArchivedExpertise,
            nodesByLayer = stats.NodesByLayer,
            edgesByLayer = stats.EdgesByLayer,
            nodesByType = stats.NodesByType
        };

        return JsonSerializer.Serialize(response, SerializerOptions);
    }

    /// <summary>
    /// memory_record_expertise → IEvolutionEngine.RecordExpertiseAsync
    /// </summary>
    internal async Task<string> HandleRecordExpertiseAsync(JsonElement parameters, CancellationToken ct)
    {
        var targetNodeId = GetRequiredString(parameters, "targetNodeId");
        var kindStr = GetRequiredString(parameters, "kind");
        var title = GetRequiredString(parameters, "title");
        var body = GetRequiredString(parameters, "body");
        var sourceAgent = GetRequiredString(parameters, "sourceAgent");
        var scope = GetOptionalString(parameters, "scope") ?? "exact";

        if (!Enum.TryParse<ExpertiseKind>(kindStr, ignoreCase: true, out var kind))
        {
            return SerializeError($"Invalid expertise kind: {kindStr}. Valid values: Pattern, Pitfall, Preference, Insight");
        }

        // Validate scope
        if (scope != "exact" && scope != "subtree")
        {
            return SerializeError($"Invalid scope: {scope}. Valid values: exact, subtree");
        }

        // Validate evidence shape if provided
        List<Citation>? evidence = null;
        if (parameters.TryGetProperty("evidence", out var evidenceProp))
        {
            if (evidenceProp.ValueKind != JsonValueKind.Array)
            {
                return SerializeError("evidence must be an array of citation objects with 'nodeId' field");
            }

            evidence = new List<Citation>();
            foreach (var item in evidenceProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return SerializeError("Each evidence item must be an object with 'nodeId', optional 'commitSha', optional 'snippet'");
                }

                var nodeId = GetOptionalString(item, "nodeId");
                if (string.IsNullOrEmpty(nodeId))
                {
                    return SerializeError("Each evidence item must have a non-empty 'nodeId' string field");
                }

                var commitSha = GetOptionalString(item, "commitSha");
                var snippet = GetOptionalString(item, "snippet");
                evidence.Add(new Citation(nodeId, commitSha, snippet));
            }
        }

        var entry = await this.evolutionEngine.RecordExpertiseAsync(
            targetNodeId, kind, title, body, sourceAgent, scope, evidence, ct).ConfigureAwait(false);

        var response = new
        {
            id = entry.Id,
            kind = entry.Kind.ToString(),
            title = entry.Title,
            body = entry.Body,
            targetNodeId = entry.TargetNodeId,
            scope = entry.Scope,
            confidence = entry.Confidence,
            sourceAgent = entry.SourceAgent,
            contentHash = entry.ContentHash,
            createdAt = entry.CreatedAt.ToString("O")
        };

        return JsonSerializer.Serialize(response, SerializerOptions);
    }

    /// <summary>
    /// memory_sync → IIncrementalSync.ApplyDiffAsync
    /// </summary>
    internal async Task<string> HandleSyncAsync(JsonElement parameters, CancellationToken ct)
    {
        var repoRoot = GetRequiredString(parameters, "repoRoot");
        var fromCommit = GetRequiredString(parameters, "fromCommit");
        var toCommit = GetRequiredString(parameters, "toCommit");

        var result = await this.incrementalSync.ApplyDiffAsync(repoRoot, fromCommit, toCommit, ct).ConfigureAwait(false);

        var response = new
        {
            filesChanged = result.FilesChanged,
            nodesAdded = result.NodesAdded,
            nodesRetired = result.NodesRetired,
            edgesAdded = result.EdgesAdded,
            edgesRetired = result.EdgesRetired,
            expertiseDecayed = result.ExpertiseDecayed,
            durationMs = result.Duration.TotalMilliseconds
        };

        return JsonSerializer.Serialize(response, SerializerOptions);
    }

    /// <summary>
    /// memory_consolidate → IEvolutionEngine.ConsolidateAsync
    /// </summary>
    internal async Task<string> HandleConsolidateAsync(JsonElement parameters, CancellationToken ct)
    {
        var threshold = GetOptionalDouble(parameters, "similarityThreshold") ?? 0.85;
        var maxMerges = GetOptionalInt(parameters, "maxMergesPerRun") ?? 50;

        var options = new ConsolidationOptions(threshold, maxMerges);
        var result = await this.evolutionEngine.ConsolidateAsync(options, ct).ConfigureAwait(false);

        var response = new
        {
            candidatesPaired = result.CandidatesPaired,
            mergedCount = result.MergedCount,
            mergedEntryIds = result.MergedEntryIds
        };

        return JsonSerializer.Serialize(response, SerializerOptions);
    }

    private static string SerializeError(string message)
    {
        return JsonSerializer.Serialize(new { error = message }, SerializerOptions);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt32();
        }

        return null;
    }

    private static double? GetOptionalDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetDouble();
        }

        return null;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString()!;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Required parameter '{propertyName}' must not be empty or whitespace");
            }

            return value;
        }

        throw new ArgumentException($"Required parameter '{propertyName}' is missing or not a string");
    }
}