// -----------------------------------------------------------------------
// <copyright file="CallChainAnalyzer.cs" company="Microsoft Corp.">
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
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Concrete implementation of <see cref="ICallChainAnalyzer"/>.
/// Resolves method invocation hyperedges into CALLS edges by matching
/// caller/callee endpoints against structure nodes.
/// Handles actual extractor output: C# uses "callee"+"scope" metadata (no caller role),
/// PowerShell uses "cmdlet" role instead of "callee".
/// </summary>
public class CallChainAnalyzer : ICallChainAnalyzer
{
    private readonly ILogger<CallChainAnalyzer> logger;

    public CallChainAnalyzer(ILogger<CallChainAnalyzer> logger)
    {
        this.logger = logger;
    }

    public Task<StageResult> AnalyzeAsync(
        HypergraphData data,
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        this.logger.LogInformation("CallChainAnalyzer: analyzing call chains");

        var edgesAdded = 0;
        var errors = new List<string>();

        try
        {
            // Build lookup: name → node IDs for call resolution
            var nameIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in data.Nodes)
            {
                if (!nameIndex.TryGetValue(kvp.Value.Name, out var list))
                {
                    list = new List<string>();
                    nameIndex[kvp.Value.Name] = list;
                }

                list.Add(kvp.Key);
            }

            // Build scope index: scope value → node IDs of containing method/function
            var scopeIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var definitionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Class", "Interface", "Method", "Function", "Property", "Constructor",
            };
            foreach (var kvp in data.Nodes)
            {
                if (definitionTypes.Contains(kvp.Value.NodeType) && !string.IsNullOrEmpty(kvp.Value.Scope))
                {
                    var scopeKey = $"{kvp.Value.Scope}.{kvp.Value.Name}";
                    if (!scopeIndex.TryGetValue(scopeKey, out var sList))
                    {
                        sList = new List<string>();
                        scopeIndex[scopeKey] = sList;
                    }

                    sList.Add(kvp.Key);
                }
            }

            // Build a qualified name index for resolving callees to canonical definitions
            var qualifiedNameIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in data.Nodes)
            {
                if (definitionTypes.Contains(kvp.Value.NodeType))
                {
                    // Index by QualifiedName metadata if present
                    if (kvp.Value.Metadata.TryGetValue(MemoryMetadataKeys.QualifiedName, out var qn)
                        && qn is string qnStr && !string.IsNullOrEmpty(qnStr))
                    {
                        qualifiedNameIndex.TryAdd(qnStr, kvp.Key);
                    }

                    // Also index by scope.name for fallback resolution
                    if (!string.IsNullOrEmpty(kvp.Value.Scope))
                    {
                        var fqn = $"{kvp.Value.Scope}.{kvp.Value.Name}";
                        qualifiedNameIndex.TryAdd(fqn, kvp.Key);
                    }

                    // Index by short name (lowest priority — only if not already there)
                    qualifiedNameIndex.TryAdd(kvp.Value.Name, kvp.Key);
                }
            }

            // Scan existing edges for invocations
            var invocationEdges = data.Hyperedges
                .Where(e => e.EdgeType is HyperedgeType.MethodInvocation
                         or HyperedgeType.ConstructorInvocation
                         or HyperedgeType.CmdletCall
                         or HyperedgeType.ModuleFunctionCall
                         or HyperedgeType.CrossLanguageMethodCall)
                .ToList();

            var addedCallEdgeIds = new HashSet<string>();

            foreach (var edge in invocationEdges)
            {
                ct.ThrowIfCancellationRequested();

                // Find the callee: look for "callee", "target", or "cmdlet" role
                var calleeNodeId = edge.Roles
                    .Where(r => r.Value is "callee" or "target" or "cmdlet")
                    .Select(r => r.Key)
                    .FirstOrDefault();

                if (calleeNodeId == null || !data.Nodes.TryGetValue(calleeNodeId, out var calleeNode))
                {
                    continue;
                }

                // Resolve callee to canonical definition if it's a reference/pseudo node
                var resolvedCalleeId = this.ResolveToCanonicalDefinition(
                    calleeNodeId, calleeNode, data, qualifiedNameIndex, nameIndex, definitionTypes);

                // Find the caller: look for explicit "caller" or "source" role first
                var callerNodeId = edge.Roles
                    .Where(r => r.Value is "caller" or "source" or "scope")
                    .Select(r => r.Key)
                    .FirstOrDefault();

                // If no explicit caller role, try to resolve from "scope" metadata
                if (callerNodeId == null || !data.Nodes.TryGetValue(callerNodeId, out _))
                {
                    callerNodeId = this.ResolveCallerFromScope(edge, data, scopeIndex, nameIndex);
                }

                if (callerNodeId == null || !data.Nodes.TryGetValue(callerNodeId, out _))
                {
                    continue;
                }

                // Skip self-calls
                if (callerNodeId == resolvedCalleeId)
                {
                    continue;
                }

                var callsEdgeId = $"calls:{callerNodeId}→{resolvedCalleeId}";
                if (addedCallEdgeIds.Add(callsEdgeId))
                {
                    var languages = new List<string>();
                    if (data.Nodes.TryGetValue(callerNodeId, out var callerNode))
                    {
                        languages.Add(callerNode.Language);
                    }

                    if (data.Nodes.TryGetValue(resolvedCalleeId, out var resolvedCalleeNode))
                    {
                        languages.Add(resolvedCalleeNode.Language);
                    }

                    data.Hyperedges.Add(new Hyperedge
                    {
                        Id = callsEdgeId,
                        EdgeType = edge.EdgeType,
                        NodeIds = new List<string> { callerNodeId, resolvedCalleeId },
                        Roles = new Dictionary<string, string>
                        {
                            { callerNodeId, "caller" },
                            { resolvedCalleeId, "callee" },
                        },
                        Languages = languages.Distinct().ToList(),
                        Metadata = new Dictionary<string, object?>
                        {
                            { MemoryMetadataKeys.SemanticRelation, "CALLS" },
                            { MemoryMetadataKeys.MemoryLayer, "call_chain" },
                            { MemoryMetadataKeys.OriginalEdgeType, edge.EdgeType.ToString() },
                        },
                    });
                    edgesAdded++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "CallChainAnalyzer stage failed");
            errors.Add($"CallChainAnalyzer failed: {ex.Message}");
        }

        this.logger.LogInformation("CallChainAnalyzer: completed. Edges added: {EdgesAdded}", edgesAdded);

        return Task.FromResult(new StageResult
        {
            EdgesCreated = edgesAdded,
            Errors = errors,
        });
    }

    /// <summary>
    /// Resolves a callee node to its canonical definition node when the callee is a
    /// reference/pseudo node (e.g., a method invocation target that isn't the definition itself).
    /// Uses QualifiedName index to find the actual definition node.
    /// </summary>
    private string ResolveToCanonicalDefinition(
        string calleeNodeId,
        HypergraphNode calleeNode,
        HypergraphData data,
        Dictionary<string, string> qualifiedNameIndex,
        Dictionary<string, List<string>> nameIndex,
        HashSet<string> definitionTypes)
    {
        // If the callee IS already a definition type, it's already canonical
        if (definitionTypes.Contains(calleeNode.NodeType))
        {
            return calleeNodeId;
        }

        // Try to resolve by QualifiedName metadata
        if (calleeNode.Metadata.TryGetValue(MemoryMetadataKeys.QualifiedName, out var qn)
            && qn is string qnStr && !string.IsNullOrEmpty(qnStr)
            && qualifiedNameIndex.TryGetValue(qnStr, out var defId)
            && data.Nodes.ContainsKey(defId))
        {
            return defId;
        }

        // Try to resolve by scope.name
        if (!string.IsNullOrEmpty(calleeNode.Scope))
        {
            var fqn = $"{calleeNode.Scope}.{calleeNode.Name}";
            if (qualifiedNameIndex.TryGetValue(fqn, out var fqnDefId) && data.Nodes.ContainsKey(fqnDefId))
            {
                return fqnDefId;
            }
        }

        // Try to resolve by name → find a definition node with same name
        if (nameIndex.TryGetValue(calleeNode.Name, out var candidates))
        {
            var defCandidate = candidates.FirstOrDefault(id =>
                data.Nodes.TryGetValue(id, out var n) && definitionTypes.Contains(n.NodeType));
            if (defCandidate != null)
            {
                return defCandidate;
            }
        }

        // For method_ref / callee / param_value nodes, the name may be a complex expression
        // like "this.paymentService.ProcessPayment" — extract terminal method name and try again
        var shortName = ExtractTerminalName(calleeNode.Name);
        if (shortName != calleeNode.Name && nameIndex.TryGetValue(shortName, out var shortCandidates))
        {
            var shortDefCandidate = shortCandidates.FirstOrDefault(id =>
                data.Nodes.TryGetValue(id, out var n) && definitionTypes.Contains(n.NodeType));
            if (shortDefCandidate != null)
            {
                return shortDefCandidate;
            }
        }

        // Fallback: use the original callee node
        return calleeNodeId;
    }

    /// <summary>
    /// Extracts the terminal name from a potentially complex expression like
    /// "this.paymentService.ProcessPayment" → "ProcessPayment".
    /// </summary>
    private static string ExtractTerminalName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name[(lastDot + 1)..] : name;
    }

    /// <summary>
    /// Resolves the caller node from edge scope metadata when no explicit caller role exists.
    /// C# extractors store the containing scope as metadata "scope" rather than a role.
    /// </summary>
    private string? ResolveCallerFromScope(
        Hyperedge edge,
        HypergraphData data,
        Dictionary<string, List<string>> scopeIndex,
        Dictionary<string, List<string>> nameIndex)
    {
        // Try to get scope from metadata
        if (edge.Metadata.TryGetValue("scope", out var scopeObj) && scopeObj is string scope
            && !string.IsNullOrEmpty(scope))
        {
            // Look up scope in scopeIndex (scope → node that matches)
            if (scopeIndex.TryGetValue(scope, out var scopeNodes) && scopeNodes.Count > 0)
            {
                return scopeNodes[0];
            }

            // Try as a direct name lookup
            if (nameIndex.TryGetValue(scope, out var namedNodes) && namedNodes.Count > 0)
            {
                return namedNodes[0];
            }

            // Try the last part of the scope (e.g., "MyApp.Services.PaymentService" → "PaymentService")
            var lastDot = scope.LastIndexOf('.');
            if (lastDot >= 0)
            {
                var shortName = scope[(lastDot + 1)..];
                if (nameIndex.TryGetValue(shortName, out var shortNamedNodes) && shortNamedNodes.Count > 0)
                {
                    return shortNamedNodes[0];
                }
            }
        }

        // Fallback: look for a node in the same file as the edge's node
        var anyNodeId = edge.NodeIds.FirstOrDefault(id => data.Nodes.ContainsKey(id));
        if (anyNodeId != null && data.Nodes.TryGetValue(anyNodeId, out var anyNode))
        {
            var filePath = anyNode.FilePath;
            var fileNodes = data.Nodes.Values
                .Where(n => !string.IsNullOrEmpty(n.FilePath)
                    && n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)
                    && (n.NodeType.Equals("Class", StringComparison.OrdinalIgnoreCase)
                        || n.NodeType.Equals("Method", StringComparison.OrdinalIgnoreCase)
                        || n.NodeType.Equals("Function", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (fileNodes.Count > 0)
            {
                return fileNodes[0].Id;
            }
        }

        return null;
    }
}