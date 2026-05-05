// -----------------------------------------------------------------------
// <copyright file="CallChainAnalyzer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 2.2 of the extraction pipeline. Reads <see cref="Hyperedge"/> entries
/// from parsed <see cref="HypergraphData"/> and emits call-chain-layer edges:
/// <c>CALLS</c>, <c>EXTENDS</c>, <c>IMPLEMENTS</c>, <c>REFERENCES</c>.
/// <para>
/// Per architecture.md §2.2 and §3.1, this stage runs after StructureBuilder
/// and before SemanticIndexer. Code-derived edges retain their original
/// <see cref="HyperedgeType"/> enum value and record it in
/// <c>Metadata["OriginalEdgeType"]</c>. All edge-kind discrimination uses
/// <c>Metadata["SemanticRelation"]</c> as the sole filtering mechanism.
/// </para>
/// <para>
/// Reverse call-chain lookups are performed by traversing <c>CALLS</c> edges
/// backward — no separate reverse edge is stored (architecture.md §2.3).
/// </para>
/// </summary>
public class CallChainAnalyzer
{
    private const string CallChainLayer = "call_chain";
    private const double DirectCallConfidence = 1.0;
    private const double CrossLanguageConfidence = 0.7;
    private const double DynamicDispatchConfidence = 0.5;

    private readonly IMemoryGraphStore store;
    private readonly ILogger<CallChainAnalyzer> logger;

    /// <summary>
    /// Maps <see cref="HyperedgeType"/> values to their canonical edge definitions.
    /// </summary>
    private static readonly IReadOnlyDictionary<HyperedgeType, EdgeMapping> EdgeMappings =
        new Dictionary<HyperedgeType, EdgeMapping>
        {
            // CALLS edges — architecture.md §2.2
            [HyperedgeType.MethodInvocation] = new("CALLS", "caller", "callee"),
            [HyperedgeType.ConstructorInvocation] = new("CALLS", "caller", "callee"),
            [HyperedgeType.CmdletCall] = new("CALLS", "caller", "callee"),
            [HyperedgeType.ModuleFunctionCall] = new("CALLS", "caller", "callee"),
            [HyperedgeType.CrossLanguageMethodCall] = new("CALLS", "caller", "callee"),

            // EXTENDS edges — architecture.md §2.2, §2.3
            [HyperedgeType.Extends] = new("EXTENDS", "subclass", "superclass"),

            // IMPLEMENTS edges — architecture.md §2.2, §2.3
            [HyperedgeType.Implements] = new("IMPLEMENTS", "implementor", "interface"),

            // REFERENCES edges — architecture.md §2.2
            [HyperedgeType.DataFlow] = new("REFERENCES", "source", "target"),
            [HyperedgeType.Assignment] = new("REFERENCES", "source", "target"),
            [HyperedgeType.CrossLanguageTypeReference] = new("REFERENCES", "source", "target"),
        };

    public CallChainAnalyzer(IMemoryGraphStore store, ILogger<CallChainAnalyzer> logger)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Analyzes call-chain relationships from parsed hypergraph data and
    /// persists the resulting edges to the memory graph store.
    /// </summary>
    /// <param name="data">Source hypergraph data from Ast.Csharp / Ast.PowerShell parsers.</param>
    /// <param name="version">Version range for the current extraction run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result summarizing edges created and any errors.</returns>
    public async Task<CallChainResult> AnalyzeAsync(
        HypergraphData data,
        VersionRange version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(version);

        var result = new CallChainResult();
        var edgesToCreate = new List<(Hyperedge Edge, VersionRange Version)>();

        foreach (var hyperedge in data.Hyperedges)
        {
            if (!EdgeMappings.TryGetValue(hyperedge.EdgeType, out var mapping))
            {
                continue;
            }

            try
            {
                var callChainEdge = this.CreateCallChainEdge(hyperedge, mapping, data);
                edgesToCreate.Add((callChainEdge, version));
                result.EdgesCreated++;

                switch (mapping.SemanticRelation)
                {
                    case "CALLS":
                        result.CallsEdges++;
                        break;
                    case "EXTENDS":
                        result.ExtendsEdges++;
                        break;
                    case "IMPLEMENTS":
                        result.ImplementsEdges++;
                        break;
                    case "REFERENCES":
                        result.ReferencesEdges++;
                        break;
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "Failed to create call-chain edge from hyperedge {EdgeId} ({EdgeType})",
                    hyperedge.Id,
                    hyperedge.EdgeType);
                result.Errors.Add($"Edge {hyperedge.Id}: {ex.Message}");
            }
        }

        // Persist all edges
        foreach (var (edge, ver) in edgesToCreate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await this.store.UpsertEdgeAsync(edge, ver, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to persist edge {EdgeId}", edge.Id);
                result.Errors.Add($"Persist {edge.Id}: {ex.Message}");
                result.EdgesCreated--;
            }
        }

        this.logger.LogInformation(
            "CallChainAnalyzer completed: {Total} edges ({Calls} CALLS, {Extends} EXTENDS, " +
            "{Implements} IMPLEMENTS, {References} REFERENCES), {Errors} errors",
            result.EdgesCreated,
            result.CallsEdges,
            result.ExtendsEdges,
            result.ImplementsEdges,
            result.ReferencesEdges,
            result.Errors.Count);

        return result;
    }

    /// <summary>
    /// Creates a call-chain edge from an existing hyperedge, mapping roles
    /// to canonical names and populating required metadata.
    /// </summary>
    private Hyperedge CreateCallChainEdge(
        Hyperedge source,
        EdgeMapping mapping,
        HypergraphData data)
    {
        var roles = this.MapRoles(source, mapping);
        var confidence = this.ComputeConfidence(source, data);

        var edgeId = GenerateEdgeId(mapping.SemanticRelation, source);

        return new Hyperedge
        {
            Id = edgeId,
            EdgeType = source.EdgeType,
            NodeIds = [.. source.NodeIds],
            Roles = roles,
            Languages = [.. source.Languages],
            Weight = source.Weight,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = mapping.SemanticRelation,
                [MemoryMetadataKeys.MemoryLayer] = CallChainLayer,
                [MemoryMetadataKeys.OriginalEdgeType] = source.EdgeType.ToString(),
                [MemoryMetadataKeys.Confidence] = confidence,
            }
        };
    }

    /// <summary>
    /// Maps roles from the source hyperedge to canonical call-chain roles.
    /// If the source has exactly 2 nodes, the first is assigned role1 and
    /// the second role2. If the source has existing role names, those are
    /// mapped to the canonical names.
    /// </summary>
    private Dictionary<string, string> MapRoles(Hyperedge source, EdgeMapping mapping)
    {
        var canonicalRoles = new Dictionary<string, string>();

        if (source.NodeIds.Count >= 2)
        {
            // Convention: first node = caller/subclass/implementor/source,
            // second node = callee/superclass/interface/target
            canonicalRoles[source.NodeIds[0]] = mapping.Role1;
            canonicalRoles[source.NodeIds[1]] = mapping.Role2;

            // Preserve additional participants (e.g., arguments in complex edges)
            for (int i = 2; i < source.NodeIds.Count; i++)
            {
                var existingRole = source.Roles.GetValueOrDefault(source.NodeIds[i], $"participant_{i}");
                canonicalRoles[source.NodeIds[i]] = existingRole;
            }
        }
        else if (source.NodeIds.Count == 1)
        {
            // Degenerate edge with single node — preserve as-is
            var existingRole = source.Roles.GetValueOrDefault(source.NodeIds[0], mapping.Role1);
            canonicalRoles[source.NodeIds[0]] = existingRole;
        }

        return canonicalRoles;
    }

    /// <summary>
    /// Computes the confidence score for a call-chain edge.
    /// Direct calls = 1.0, cross-language = 0.7, dynamic dispatch = 0.5.
    /// Per architecture.md §2.2.
    /// </summary>
    private double ComputeConfidence(Hyperedge source, HypergraphData data)
    {
        // Cross-language calls carry inherent uncertainty
        if (source.EdgeType == HyperedgeType.CrossLanguageMethodCall)
        {
            return CrossLanguageConfidence;
        }

        // Check for dynamic dispatch indicators in source metadata
        if (source.Metadata.TryGetValue("IsVirtualDispatch", out var isVirtual) && isVirtual is true)
        {
            return DynamicDispatchConfidence;
        }

        if (source.Metadata.TryGetValue("IsDynamicDispatch", out var isDynamic) && isDynamic is true)
        {
            return DynamicDispatchConfidence;
        }

        // Check if callee node is on an interface (interface dispatch)
        if (source.NodeIds.Count >= 2 && data.Nodes.TryGetValue(source.NodeIds[1], out var calleeNode))
        {
            if (string.Equals(calleeNode.NodeType, "Interface", StringComparison.OrdinalIgnoreCase) ||
                calleeNode.Modifiers.Contains("abstract"))
            {
                return CrossLanguageConfidence; // 0.7 for interface/abstract dispatch
            }
        }

        return DirectCallConfidence;
    }

    /// <summary>
    /// Generates a deterministic edge ID from the semantic relation and source edge.
    /// </summary>
    private static string GenerateEdgeId(string semanticRelation, Hyperedge source)
    {
        var input = $"{semanticRelation}:{source.Id}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash12 = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"cc:{semanticRelation.ToLowerInvariant()}:{hash12}";
    }

    /// <summary>
    /// Describes the canonical mapping for a call-chain edge type.
    /// </summary>
    internal readonly record struct EdgeMapping(string SemanticRelation, string Role1, string Role2);
}

/// <summary>
/// Result of a call-chain analysis run.
/// </summary>
public class CallChainResult
{
    /// <summary>Total edges created.</summary>
    public int EdgesCreated { get; set; }

    /// <summary>Count of CALLS edges.</summary>
    public int CallsEdges { get; set; }

    /// <summary>Count of EXTENDS edges.</summary>
    public int ExtendsEdges { get; set; }

    /// <summary>Count of IMPLEMENTS edges.</summary>
    public int ImplementsEdges { get; set; }

    /// <summary>Count of REFERENCES edges.</summary>
    public int ReferencesEdges { get; set; }

    /// <summary>Error messages from failed edge creations.</summary>
    public List<string> Errors { get; } = [];
}