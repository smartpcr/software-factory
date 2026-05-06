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
/// All persisted edges are <b>binary</b> (exactly 2 participating nodes),
/// per architecture.md §5.2. N-ary hyperedges from extractors are decomposed
/// into one or more binary edges using role-based identification.
/// </para>
/// <para>
/// <b>Taxonomy alignment (architecture.md §2.2):</b>
/// <see cref="HyperedgeType.ClassDefinition"/>, <see cref="HyperedgeType.InterfaceDefinition"/>,
/// and <see cref="HyperedgeType.StructDefinition"/> are structural definition hyperedges
/// that also encode inheritance relationships via roles like <c>base_0</c>, <c>extends_0</c>,
/// and <c>implements_0</c>. CallChainAnalyzer extracts these inheritance relationships and
/// emits <c>EXTENDS</c> / <c>IMPLEMENTS</c> edges from them. Explicit
/// <see cref="HyperedgeType.Extends"/> and <see cref="HyperedgeType.Implements"/> edges
/// are also supported for forward compatibility with normalizers.
/// </para>
/// <para>
/// <b>Self-call edges:</b> Recursive calls where caller == callee are skipped because
/// the <c>Roles</c> dictionary is keyed by node ID and cannot carry both <c>"caller"</c>
/// and <c>"callee"</c> for the same node — emitting such an edge would violate the
/// canonical role contract. Self-recursion is detected by callers examining the node's
/// own outgoing call edges at query time rather than via stored edges.
/// </para>
/// <para>
/// <b>CmdletCall / ModuleFunctionCall handling:</b> When no enclosing function
/// provides a natural caller context (top-level script), CallChainAnalyzer
/// synthesizes a file-level node as the caller and upserts it to the store.
/// This ensures CmdletCall and ModuleFunctionCall hyperedges always emit
/// CALLS edges. For ModuleFunctionCall, the <c>source_module</c> role
/// represents the module containing the called function (e.g., "Az.Storage"),
/// NOT the calling context — it is never used as the caller.
/// </para>
/// <para>
/// <b>Node persistence:</b> CallChainAnalyzer upserts all endpoint nodes
    /// referenced by emitted edges to the store independently. This ensures that
    /// edge endpoints are always resolvable, even if StructureBuilder (Stage 2.1)
    /// has not yet created them. Nodes from <see cref="HypergraphData"/> are upserted
    /// with their existing metadata (adding MemoryLayer if absent); synthetic nodes
    /// (e.g., file-level callers for top-level CmdletCall) include
    /// <c>Metadata["MemoryLayer"]</c> per §3.11's invariant that every
    /// <see cref="HypergraphNode"/> has exactly one home layer.
/// </para>
/// <para>
/// Reverse call-chain lookups are performed by traversing <c>CALLS</c> edges
/// backward via <see cref="FindCallersOf"/> — no separate reverse edge is stored
/// (architecture.md §2.3).
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
    /// Accumulates synthetic nodes created during analysis (e.g., file-level
    /// nodes for CmdletCall) that need to be upserted to the store.
    /// Cleared at the start of each <see cref="AnalyzeAsync"/> call.
    /// These are stored in a separate overlay dictionary rather than mutating
    /// the input <see cref="HypergraphData"/>.
    /// </summary>
    private readonly Dictionary<string, HypergraphNode> syntheticNodes = new(StringComparer.Ordinal);

    /// <summary>
    /// Accumulates error messages for skipped edges (e.g., when caller or callee
    /// cannot be resolved). Populated during decomposition and transferred to
    /// <see cref="CallChainResult.Errors"/> at the end of analysis.
    /// Cleared at the start of each <see cref="AnalyzeAsync"/> call.
    /// </summary>
    private readonly List<string> skippedEdgeErrors = [];

    /// <summary>
    /// Count of self-recursive calls skipped during the current analysis.
    /// The Roles dictionary (keyed by node ID) cannot represent both caller
    /// and callee for the same node. This limitation will be addressed in a
    /// future schema extension (per-participant role list).
    /// Cleared at the start of each <see cref="AnalyzeAsync"/> call.
    /// </summary>
    private int selfCallsSkipped;

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

    /// <summary>
    /// Definition edge types from which CallChainAnalyzer extracts inheritance
    /// relationships. These are structural definitions that encode base types
    /// via indexed roles (e.g., <c>base_0</c>, <c>extends_0</c>, <c>implements_0</c>).
    /// </summary>
    private static readonly HashSet<HyperedgeType> DefinitionEdgeTypes =
    [
        HyperedgeType.ClassDefinition,
        HyperedgeType.InterfaceDefinition,
        HyperedgeType.StructDefinition,
    ];

    /// <summary>
    /// Maps definition edge types to the primary role name that identifies the
    /// defining type node (e.g., "class" for ClassDefinition).
    /// </summary>
    private static readonly IReadOnlyDictionary<HyperedgeType, string> DefinitionPrimaryRoles =
        new Dictionary<HyperedgeType, string>
        {
            [HyperedgeType.ClassDefinition] = "class",
            [HyperedgeType.InterfaceDefinition] = "interface",
            [HyperedgeType.StructDefinition] = "struct",
        };

    /// <summary>
    /// Per-edge-type role names used by actual extractors to identify the callee.
    /// Falls through in order; first match wins.
    /// </summary>
    private static readonly IReadOnlyDictionary<HyperedgeType, string[]> CalleeRolesByEdgeType =
        new Dictionary<HyperedgeType, string[]>
        {
            [HyperedgeType.MethodInvocation] = ["callee"],
            [HyperedgeType.ConstructorInvocation] = ["instantiated_type", "callee"],
            [HyperedgeType.CmdletCall] = ["cmdlet", "callee"],
            [HyperedgeType.ModuleFunctionCall] = ["called_function", "callee"],
            [HyperedgeType.CrossLanguageMethodCall] = ["dotnet_definition", "callee"],
        };

    /// <summary>
    /// Per-edge-type role names used by actual extractors to identify the caller.
    /// Most invocation hyperedges do NOT include an explicit caller;
    /// the caller is resolved from the callee node's Scope instead.
    /// </summary>
    private static readonly IReadOnlyDictionary<HyperedgeType, string[]> CallerRolesByEdgeType =
        new Dictionary<HyperedgeType, string[]>
        {
            [HyperedgeType.CrossLanguageMethodCall] = ["ps_reference", "caller"],
        };

    /// <summary>
    /// Node types that represent callable definitions (used for scope-based caller lookup).
    /// </summary>
    private static readonly HashSet<string> DefinitionNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "method", "constructor", "function", "class", "interface", "struct", "record",
    };

    /// <summary>
    /// Node types that represent script/file-level scope. Used as a fallback caller
    /// for <see cref="HyperedgeType.CmdletCall"/> and <see cref="HyperedgeType.ModuleFunctionCall"/>
    /// at the top level of a PowerShell script, where no enclosing function provides
    /// a natural caller context. Excludes "module" because semantic module nodes
    /// (e.g., "Az.Storage" from ModuleFunctionCall) represent the module containing
    /// the called function, not the calling context.
    /// </summary>
    private static readonly HashSet<string> ScriptLevelNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "file",
    };

    public CallChainAnalyzer(IMemoryGraphStore store, ILogger<CallChainAnalyzer> logger)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Analyzes call-chain relationships from parsed hypergraph data and
    /// persists the resulting binary edges to the memory graph store.
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
        var edgesToCreate = new List<(Hyperedge Edge, VersionRange Version, string SemanticRelation)>();
        this.syntheticNodes.Clear();
        this.skippedEdgeErrors.Clear();
        this.selfCallsSkipped = 0;

        // Build scope index for caller resolution from definition nodes
        var scopeIndex = BuildScopeIndex(data);

        // Build resolution index for resolving ref/expression nodes to canonical definitions
        var resolutionIndex = BuildNodeResolutionIndex(data);

        // Track all node IDs referenced by emitted edges so we can upsert them
        var referencedNodeIds = new HashSet<string>(StringComparer.Ordinal);

        // Deduplication: prevent the same (relation, source, target) from being stored twice.
        // This can happen when both explicit Extends/Implements hyperedges AND definition-derived
        // inheritance edges encode the same relationship.
        var seenEdgeKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var hyperedge in data.Hyperedges)
        {
            List<Hyperedge> binaryEdges;

            if (EdgeMappings.TryGetValue(hyperedge.EdgeType, out var mapping))
            {
                // Decompose n-ary hyperedge into one or more binary edges.
                // Exceptions propagate — decomposition failures are bugs, not expected conditions.
                binaryEdges = this.DecomposeToBinaryEdges(
                    hyperedge, mapping, data, scopeIndex, resolutionIndex);
            }
            else if (DefinitionEdgeTypes.Contains(hyperedge.EdgeType))
            {
                // Extract inheritance relationships from definition hyperedges.
                // Real extractors encode inheritance via ClassDefinition/InterfaceDefinition/
                // StructDefinition with base_X/extends_X/implements_X roles.
                binaryEdges = this.DecomposeDefinitionInheritance(
                    hyperedge, data, resolutionIndex);
            }
            else
            {
                continue;
            }

            foreach (var binaryEdge in binaryEdges)
            {
                var edgeRelation = binaryEdge.Metadata[MemoryMetadataKeys.SemanticRelation] as string
                    ?? "UNKNOWN";

                // Dedup: skip if same (relation, source, target) already emitted
                var dedupKey = $"{edgeRelation}:{binaryEdge.NodeIds[0]}:{binaryEdge.NodeIds[1]}";
                if (!seenEdgeKeys.Add(dedupKey))
                {
                    continue;
                }

                edgesToCreate.Add((binaryEdge, version, edgeRelation));
                result.EdgesCreated++;

                // Track referenced nodes
                foreach (var nodeId in binaryEdge.NodeIds)
                {
                    referencedNodeIds.Add(nodeId);
                }

                switch (edgeRelation)
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
        }

        // Upsert only SYNTHETIC nodes created by this analyzer (e.g., file-level
        // caller nodes for top-level CmdletCall/ModuleFunctionCall). Raw parser
        // nodes are owned by StructureBuilder (Stage 2.1) and must NOT be upserted
        // here — CallChainAnalyzer references them by ID in edges but does not
        // take ownership of their lifecycle.
        foreach (var nodeId in referencedNodeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.syntheticNodes.TryGetValue(nodeId, out var syntheticNode))
            {
                await this.store.UpsertNodeAsync(syntheticNode, version, cancellationToken).ConfigureAwait(false);
            }
        }

        // Persist all edges — failures propagate as exceptions (fail-fast, not partial success).
        foreach (var (edge, ver, _) in edgesToCreate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.store.UpsertEdgeAsync(edge, ver, cancellationToken).ConfigureAwait(false);
        }

        // Transfer skipped-edge warnings into result.Errors for caller visibility.
        result.Errors.AddRange(this.skippedEdgeErrors);

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
    /// Decomposes an n-ary hyperedge into one or more binary edges using
    /// role-based identification per architecture.md §5.2.
    /// </summary>
    private List<Hyperedge> DecomposeToBinaryEdges(
        Hyperedge source,
        EdgeMapping mapping,
        HypergraphData data,
        Dictionary<string, string> scopeIndex,
        Dictionary<string, string> resolutionIndex)
    {
        return mapping.SemanticRelation switch
        {
            "CALLS" => this.DecomposeCallEdge(source, mapping, data, scopeIndex, resolutionIndex),
            "EXTENDS" or "IMPLEMENTS" => this.DecomposeInheritanceEdge(source, mapping, data, resolutionIndex),
            "REFERENCES" => this.DecomposeReferenceEdge(source, mapping, data, resolutionIndex),
            _ => [],
        };
    }

    // ── CALLS decomposition ─────────────────────────────────────────────

    /// <summary>
    /// Decomposes an invocation hyperedge into a single binary CALLS edge.
    /// The callee is identified from extractor-specific roles; the caller is
    /// resolved from an explicit "caller" role or from the callee node's Scope.
    /// After caller/callee are identified, ref nodes are resolved to canonical
    /// structural definitions when unambiguous.
    /// </summary>
    private List<Hyperedge> DecomposeCallEdge(
        Hyperedge source,
        EdgeMapping mapping,
        HypergraphData data,
        Dictionary<string, string> scopeIndex,
        Dictionary<string, string> resolutionIndex)
    {
        var calleeId = this.FindCalleeNode(source);
        if (calleeId == null)
        {
            var msg = $"Cannot identify callee in hyperedge {source.Id} ({source.EdgeType})";
            this.logger.LogWarning(msg);
            this.skippedEdgeErrors.Add(msg);
            return [];
        }

        // Resolve caller BEFORE resolving callee to canonical node,
        // because caller inference uses the ref node's call-site Scope.
        var callerId = this.FindCallerNode(source, calleeId, data, scopeIndex);
        if (callerId == null)
        {
            var msg = $"Cannot resolve caller for hyperedge {source.Id} ({source.EdgeType})";
            this.logger.LogWarning(msg);
            this.skippedEdgeErrors.Add(msg);
            return [];
        }

        // Resolve ref/expression nodes to canonical structural definitions
        var resolvedCalleeId = ResolveToCanonical(calleeId, data, resolutionIndex);
        var resolvedCallerId = ResolveToCanonical(callerId, data, resolutionIndex);

        // If resolution caused a false self-edge, fall back to pre-resolution IDs,
        // BUT only if both nodes were changed by resolution.
        var callerChanged = !string.Equals(callerId, resolvedCallerId, StringComparison.Ordinal);
        var calleeChanged = !string.Equals(calleeId, resolvedCalleeId, StringComparison.Ordinal);
        if (string.Equals(resolvedCallerId, resolvedCalleeId, StringComparison.Ordinal) &&
            !string.Equals(callerId, calleeId, StringComparison.Ordinal) &&
            callerChanged && calleeChanged)
        {
            resolvedCallerId = callerId;
            resolvedCalleeId = calleeId;
        }

        // Self-call: caller == callee (recursive method). Skip because the
        // Roles dict is keyed by node ID and cannot carry both "caller" and
        // "callee" for the same node — emitting such an edge would violate the
        // canonical role contract. Self-recursion is detected at query time.
        if (string.Equals(resolvedCallerId, resolvedCalleeId, StringComparison.Ordinal))
        {
            this.logger.LogDebug(
                "Self-call in hyperedge {EdgeId}: node {NodeId} — skipping (cannot represent caller+callee for same node)",
                source.Id, resolvedCallerId);
            return [];
        }

        var confidence = this.ComputeConfidence(source, resolvedCalleeId, data);
        var edge = CreateBinaryEdge(source, mapping, resolvedCallerId, resolvedCalleeId, confidence);
        return [edge];
    }

    /// <summary>
    /// Identifies the callee node from an invocation hyperedge using the
    /// extractor-specific role names (not positional NodeIds).
    /// </summary>
    private string? FindCalleeNode(Hyperedge source)
    {
        // Try extractor-specific callee roles
        if (CalleeRolesByEdgeType.TryGetValue(source.EdgeType, out var calleeRoles))
        {
            foreach (var targetRole in calleeRoles)
            {
                foreach (var (nodeId, role) in source.Roles)
                {
                    if (string.Equals(role, targetRole, StringComparison.Ordinal))
                    {
                        return nodeId;
                    }
                }
            }
        }

        // Fallback: any node with role "callee"
        foreach (var (nodeId, role) in source.Roles)
        {
            if (string.Equals(role, "callee", StringComparison.Ordinal))
            {
                return nodeId;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the caller node for an invocation hyperedge. Tries in order:
    /// 1. Explicit "caller" role in the hyperedge (idealized or pre-processed input)
    /// 2. Extractor-specific caller role (e.g., "ps_reference" for CrossLanguageMethodCall)
    /// 3. Scope-based resolution from the callee node's containing method/function
    /// 4. Module-as-caller fallback for ModuleFunctionCall
    /// </summary>
    private string? FindCallerNode(
        Hyperedge source,
        string calleeId,
        HypergraphData data,
        Dictionary<string, string> scopeIndex)
    {
        // 1. Check for explicit "caller" role
        foreach (var (nodeId, role) in source.Roles)
        {
            if (string.Equals(role, "caller", StringComparison.Ordinal))
            {
                return nodeId;
            }
        }

        // 2. Check extractor-specific caller roles
        if (CallerRolesByEdgeType.TryGetValue(source.EdgeType, out var callerRoles))
        {
            foreach (var targetRole in callerRoles)
            {
                foreach (var (nodeId, role) in source.Roles)
                {
                    if (string.Equals(role, targetRole, StringComparison.Ordinal))
                    {
                        return nodeId;
                    }
                }
            }
        }

        // 3. Resolve caller from callee node's Scope
        if (data.Nodes.TryGetValue(calleeId, out var calleeNode) &&
            !string.IsNullOrEmpty(calleeNode.Scope))
        {
            var resolved = ResolveCallerFromScope(calleeNode.Scope, scopeIndex);
            if (resolved != null)
            {
                return resolved;
            }
        }

        // 3.5. Line-range-based enclosing function resolution.
        // When scope-based resolution fails (common for PowerShell where cmdlet nodes
        // lack Scope data), find the narrowest enclosing function/method definition
        // based on line ranges. This uses LineStart/LineEnd from extractor data.
        if (data.Nodes.TryGetValue(calleeId, out var calleeForLineRange) &&
            calleeForLineRange.LineStart > 0)
        {
            var enclosingFunc = FindEnclosingFunctionByLineRange(
                calleeForLineRange.FilePath, calleeForLineRange.LineStart, data);
            if (enclosingFunc != null)
            {
                return enclosingFunc;
            }
        }

        // 4. For CmdletCall and ModuleFunctionCall at script level:
        // find a real script/file node in the data that shares the same file path
        // as the callee. If no real node exists, synthesize a file-level node and
        // upsert it to the store to ensure these hyperedges always emit CALLS edges.
        // Note: ModuleFunctionCall's "source_module" role represents the module
        // containing the called function, NOT the calling context. The caller is
        // the script/function where the module-qualified call appears.
        if (source.EdgeType is HyperedgeType.CmdletCall or HyperedgeType.ModuleFunctionCall)
        {
            // Collect node IDs that are part of this hyperedge's semantic roles
            // (e.g., source_module, called_function) to exclude them from
            // being picked as script-level caller candidates.
            var hyperedgeParticipants = new HashSet<string>(
                source.Roles.Keys, StringComparer.Ordinal);

            if (data.Nodes.TryGetValue(calleeId, out var calleeFileNode) &&
                !string.IsNullOrEmpty(calleeFileNode.FilePath))
            {
                // Try to find an existing script/file node for this file
                // that is NOT a participant in this hyperedge.
                foreach (var (nodeId, node) in data.Nodes)
                {
                    if (hyperedgeParticipants.Contains(nodeId))
                    {
                        continue;
                    }

                    if (string.Equals(node.FilePath, calleeFileNode.FilePath, StringComparison.OrdinalIgnoreCase) &&
                        ScriptLevelNodeTypes.Contains(node.NodeType))
                    {
                        return nodeId;
                    }
                }

                // No existing script-level node found — synthesize one.
                // Synthetic nodes are stored in a separate overlay dictionary
                // to avoid mutating the input HypergraphData.
                // Use hash-based ID matching the extractor convention.
                var syntheticId = GenerateSyntheticNodeId("file", calleeFileNode.FilePath);
                if (!data.Nodes.ContainsKey(syntheticId) &&
                    !this.syntheticNodes.ContainsKey(syntheticId))
                {
                    var fileName = calleeFileNode.FilePath.Contains('/')
                        ? calleeFileNode.FilePath[(calleeFileNode.FilePath.LastIndexOf('/') + 1)..]
                        : calleeFileNode.FilePath.Contains('\\')
                            ? calleeFileNode.FilePath[(calleeFileNode.FilePath.LastIndexOf('\\') + 1)..]
                            : calleeFileNode.FilePath;

                    var syntheticNode = new HypergraphNode
                    {
                        Id = syntheticId,
                        Name = fileName,
                        NodeType = "file",
                        Language = calleeFileNode.Language,
                        FilePath = calleeFileNode.FilePath,
                        Metadata = new Dictionary<string, object?>
                        {
                            [MemoryMetadataKeys.MemoryLayer] = "structural",
                        },
                    };
                    this.syntheticNodes[syntheticId] = syntheticNode;
                }

                return syntheticId;
            }
        }

        return null;
    }

    // ── Definition-based inheritance extraction ────────────────────────

    /// <summary>
    /// Extracts inheritance relationships from ClassDefinition, InterfaceDefinition,
    /// and StructDefinition hyperedges. Real extractors (Ast.Csharp, Ast.PowerShell)
    /// encode inheritance via indexed roles:
    /// <list type="bullet">
    /// <item><c>ClassDefinition</c>: primary role <c>"class"</c>, base types <c>base_0</c>, <c>base_1</c>, …</item>
    /// <item><c>InterfaceDefinition</c>: primary role <c>"interface"</c>, base types <c>extends_0</c>, <c>extends_1</c>, …</item>
    /// <item><c>StructDefinition</c>: primary role <c>"struct"</c>, interfaces <c>implements_0</c>, <c>implements_1</c>, …</item>
    /// </list>
    /// For <c>ClassDefinition</c> base types, the semantic relation is determined by the
    /// base node's <c>NodeType</c>: <c>"interface"</c> → IMPLEMENTS, otherwise → EXTENDS.
    /// No positional fallback is used for definition types to avoid misinterpreting
    /// member nodes as inheritance targets.
    /// </summary>
    private List<Hyperedge> DecomposeDefinitionInheritance(
        Hyperedge source,
        HypergraphData data,
        Dictionary<string, string> resolutionIndex)
    {
        // Determine the primary role for this definition type
        if (!DefinitionPrimaryRoles.TryGetValue(source.EdgeType, out var primaryRole))
        {
            return [];
        }

        // Find the primary (defining) node
        var primaryNodeId = FindNodeByRole(source, primaryRole);
        if (primaryNodeId == null)
        {
            // Skip enums (PS ClassDefinition with role "enum") and other non-matching shapes
            return [];
        }

        var result = new List<Hyperedge>();
        var subIndex = 0;

        foreach (var (nodeId, role) in source.Roles)
        {
            if (string.Equals(nodeId, primaryNodeId, StringComparison.Ordinal))
            {
                continue;
            }

            // Determine semantic relation from the role pattern
            EdgeMapping mapping;
            if (IsIndexedRole(role, "base_"))
            {
                // ClassDefinition base types: check the base node's NodeType to
                // distinguish EXTENDS (class inheritance) from IMPLEMENTS (interface).
                // C# extractor sets NodeType to "interface" or "class" heuristically.
                // PowerShell extractor uses "base_type" — defaults to EXTENDS.
                if (data.Nodes.TryGetValue(nodeId, out var baseNode) &&
                    string.Equals(baseNode.NodeType, "interface", StringComparison.OrdinalIgnoreCase))
                {
                    mapping = new EdgeMapping("IMPLEMENTS", "implementor", "interface");
                }
                else
                {
                    mapping = new EdgeMapping("EXTENDS", "subclass", "superclass");
                }
            }
            else if (IsIndexedRole(role, "extends_"))
            {
                mapping = new EdgeMapping("EXTENDS", "subclass", "superclass");
            }
            else if (IsIndexedRole(role, "implements_"))
            {
                mapping = new EdgeMapping("IMPLEMENTS", "implementor", "interface");
            }
            else
            {
                // Skip non-inheritance roles (members, method definitions, etc.)
                continue;
            }

            // Resolve nodes to canonical definitions
            var resolvedPrimary = ResolveToCanonical(primaryNodeId, data, resolutionIndex);
            var resolvedTarget = ResolveToCanonical(nodeId, data, resolutionIndex);

            // If resolution caused a false self-edge, fall back
            if (string.Equals(resolvedPrimary, resolvedTarget, StringComparison.Ordinal) &&
                !string.Equals(primaryNodeId, nodeId, StringComparison.Ordinal))
            {
                resolvedPrimary = primaryNodeId;
                resolvedTarget = nodeId;
            }

            // Self-inheritance is invalid
            if (string.Equals(resolvedPrimary, resolvedTarget, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(CreateBinaryEdge(
                source, mapping, resolvedPrimary, resolvedTarget, DirectCallConfidence, subIndex));
            subIndex++;
        }

        return result;
    }

    /// <summary>
    /// Checks whether a role name matches an indexed pattern like <c>base_0</c>, <c>extends_1</c>.
    /// Matches the prefix followed by one or more digits.
    /// </summary>
    private static bool IsIndexedRole(string role, string prefix)
    {
        if (!role.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = role.AsSpan(prefix.Length);
        if (suffix.Length == 0)
        {
            return false;
        }

        foreach (var ch in suffix)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    // ── Inheritance decomposition ───────────────────────────────────────

    /// <summary>
    /// Decomposes an Extends or Implements hyperedge into one or more binary edges.
    /// Already-binary edges pass through; n-ary edges (e.g., class implementing
    /// multiple interfaces) are decomposed into one binary edge per target.
    /// Roles are mapped to canonical names.
    /// </summary>
    private List<Hyperedge> DecomposeInheritanceEdge(
        Hyperedge source,
        EdgeMapping mapping,
        HypergraphData data,
        Dictionary<string, string> resolutionIndex)
    {
        // Find the primary (source) node by canonical role
        var primaryNodeId = FindNodeByRole(source, mapping.Role1);

        // Collect all target nodes by canonical or indexed roles
        var targetNodeIds = new List<string>();

        foreach (var (nodeId, role) in source.Roles)
        {
            if (string.Equals(role, mapping.Role2, StringComparison.Ordinal))
            {
                targetNodeIds.Add(nodeId);
            }
            else if (role.StartsWith(mapping.Role2 + "_", StringComparison.Ordinal))
            {
                targetNodeIds.Add(nodeId);
            }
        }

        // If primary found but no explicit targets, collect all non-primary nodes
        if (primaryNodeId != null && targetNodeIds.Count == 0)
        {
            foreach (var nodeId in source.NodeIds)
            {
                if (!string.Equals(nodeId, primaryNodeId, StringComparison.Ordinal))
                {
                    targetNodeIds.Add(nodeId);
                }
            }
        }

        // Fallback: positional for edges without matching roles
        if (primaryNodeId == null && targetNodeIds.Count == 0 && source.NodeIds.Count >= 2)
        {
            primaryNodeId = source.NodeIds[0];
            targetNodeIds.AddRange(source.NodeIds.Skip(1));
        }

        if (primaryNodeId == null || targetNodeIds.Count == 0)
        {
            var msg = $"Cannot identify source/target in inheritance hyperedge {source.Id}";
            this.logger.LogWarning(msg);
            this.skippedEdgeErrors.Add(msg);
            return [];
        }

        // Create one binary edge per target
        var result = new List<Hyperedge>();
        var subIndex = 0;

        foreach (var targetId in targetNodeIds)
        {
            var resolvedSource = ResolveToCanonical(primaryNodeId, data, resolutionIndex);
            var resolvedTarget = ResolveToCanonical(targetId, data, resolutionIndex);

            // If resolution caused a false self-edge, fall back
            if (string.Equals(resolvedSource, resolvedTarget, StringComparison.Ordinal) &&
                !string.Equals(primaryNodeId, targetId, StringComparison.Ordinal))
            {
                resolvedSource = primaryNodeId;
                resolvedTarget = targetId;
            }

            // Self-inheritance is invalid
            if (string.Equals(resolvedSource, resolvedTarget, StringComparison.Ordinal))
            {
                this.logger.LogDebug(
                    "Skipping self-referencing inheritance edge {EdgeId}: {NodeId}",
                    source.Id, resolvedSource);
                continue;
            }

            var confidence = DirectCallConfidence;
            result.Add(CreateBinaryEdge(
                source, mapping, resolvedSource, resolvedTarget, confidence,
                targetNodeIds.Count > 1 ? subIndex : 0));
            subIndex++;
        }

        return result;
    }

    // ── REFERENCES decomposition ────────────────────────────────────────

    /// <summary>
    /// Decomposes a DataFlow, Assignment, or CrossLanguageTypeReference hyperedge
    /// into one or more binary REFERENCES edges.
    /// </summary>
    private List<Hyperedge> DecomposeReferenceEdge(
        Hyperedge source,
        EdgeMapping mapping,
        HypergraphData data,
        Dictionary<string, string> resolutionIndex)
    {
        return source.EdgeType switch
        {
            HyperedgeType.DataFlow => this.DecomposeDataFlowEdge(source, mapping, data, resolutionIndex),
            HyperedgeType.Assignment => this.DecomposeAssignmentEdge(source, mapping, data, resolutionIndex),
            HyperedgeType.CrossLanguageTypeReference =>
                this.DecomposeCrossLanguageRefEdge(source, mapping, data, resolutionIndex),
            _ => [],
        };
    }

    /// <summary>
    /// Decomposes a DataFlow n-ary hyperedge into binary def→use edges.
    /// </summary>
    private List<Hyperedge> DecomposeDataFlowEdge(
        Hyperedge source,
        EdgeMapping mapping,
        HypergraphData data,
        Dictionary<string, string> resolutionIndex)
    {
        // Collect def and use nodes from roles
        var defs = new List<string>();
        var uses = new List<string>();

        foreach (var (nodeId, role) in source.Roles)
        {
            if (role.StartsWith("def_", StringComparison.Ordinal) ||
                string.Equals(role, "source", StringComparison.Ordinal))
            {
                defs.Add(nodeId);
            }
            else if (role.StartsWith("use_", StringComparison.Ordinal) ||
                     string.Equals(role, "target", StringComparison.Ordinal))
            {
                uses.Add(nodeId);
            }
        }

        // Fallback for binary edges without explicit def/use roles
        if (defs.Count == 0 && uses.Count == 0 && source.NodeIds.Count == 2)
        {
            defs.Add(source.NodeIds[0]);
            uses.Add(source.NodeIds[1]);
        }

        if (defs.Count == 0 || uses.Count == 0)
        {
            return [];
        }

        var result = new List<Hyperedge>();
        var subIndex = 0;

        // Cartesian product: each def paired with each use to capture all relationships
        foreach (var def in defs)
        {
            foreach (var use in uses)
            {
                var defId = ResolveToCanonical(def, data, resolutionIndex);
                var useId = ResolveToCanonical(use, data, resolutionIndex);

                // If resolution caused a false self-reference, fall back to pre-resolution IDs
                var defChanged = !string.Equals(def, defId, StringComparison.Ordinal);
                var useChanged = !string.Equals(use, useId, StringComparison.Ordinal);
                if (string.Equals(defId, useId, StringComparison.Ordinal) &&
                    !string.Equals(def, use, StringComparison.Ordinal) &&
                    defChanged && useChanged)
                {
                    defId = def;
                    useId = use;
                }

                // Skip genuine self-references
                if (string.Equals(defId, useId, StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(CreateBinaryEdge(source, mapping, defId, useId, DirectCallConfidence, subIndex));
                subIndex++;
            }
        }

        return result;
    }

    /// <summary>
    /// Decomposes an Assignment edge. Extractor uses "target"/"value" roles for
    /// variable assignments and "field"/"field_type" roles for field declarations.
    /// The data flows from "value"/"field_type" (source) to "target"/"field" (destination).
    /// </summary>
    private List<Hyperedge> DecomposeAssignmentEdge(
        Hyperedge source,
        EdgeMapping mapping,
        HypergraphData data,
        Dictionary<string, string> resolutionIndex)
    {
        string? sourceId = null;
        string? targetId = null;

        foreach (var (nodeId, role) in source.Roles)
        {
            if (string.Equals(role, "value", StringComparison.Ordinal) ||
                string.Equals(role, "source", StringComparison.Ordinal) ||
                string.Equals(role, "field_type", StringComparison.Ordinal))
            {
                sourceId = nodeId;
            }
            else if (string.Equals(role, "target", StringComparison.Ordinal) ||
                     string.Equals(role, "field", StringComparison.Ordinal))
            {
                targetId = nodeId;
            }
        }

        // Fallback for binary edges
        if (sourceId == null && targetId == null && source.NodeIds.Count == 2)
        {
            sourceId = source.NodeIds[0];
            targetId = source.NodeIds[1];
        }

        if (sourceId == null || targetId == null)
        {
            return [];
        }

        var origAssignSourceId = sourceId;
        var origAssignTargetId = targetId;
        sourceId = ResolveToCanonical(sourceId, data, resolutionIndex);
        targetId = ResolveToCanonical(targetId, data, resolutionIndex);

        // If resolution caused a false self-reference, fall back to pre-resolution IDs
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal) &&
            !string.Equals(origAssignSourceId, origAssignTargetId, StringComparison.Ordinal))
        {
            sourceId = origAssignSourceId;
            targetId = origAssignTargetId;
        }

        if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
        {
            return [];
        }

        return [CreateBinaryEdge(source, mapping, sourceId, targetId, DirectCallConfidence)];
    }

    /// <summary>
    /// Decomposes a CrossLanguageTypeReference edge.
    /// Extractor uses "dotnet_definition"/"ps_reference" or "source"/"target".
    /// </summary>
    private List<Hyperedge> DecomposeCrossLanguageRefEdge(
        Hyperedge source,
        EdgeMapping mapping,
        HypergraphData data,
        Dictionary<string, string> resolutionIndex)
    {
        string? sourceId = null;
        string? targetId = null;

        foreach (var (nodeId, role) in source.Roles)
        {
            if (string.Equals(role, "ps_reference", StringComparison.Ordinal) ||
                string.Equals(role, "source", StringComparison.Ordinal))
            {
                sourceId = nodeId;
            }
            else if (string.Equals(role, "dotnet_definition", StringComparison.Ordinal) ||
                     string.Equals(role, "target", StringComparison.Ordinal))
            {
                targetId = nodeId;
            }
        }

        // Fallback for binary edges
        if (sourceId == null && targetId == null && source.NodeIds.Count == 2)
        {
            sourceId = source.NodeIds[0];
            targetId = source.NodeIds[1];
        }

        if (sourceId == null || targetId == null)
        {
            return [];
        }

        var origXlangSourceId = sourceId;
        var origXlangTargetId = targetId;
        sourceId = ResolveToCanonical(sourceId, data, resolutionIndex);
        targetId = ResolveToCanonical(targetId, data, resolutionIndex);

        // If resolution caused a false self-reference, fall back to pre-resolution IDs
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal) &&
            !string.Equals(origXlangSourceId, origXlangTargetId, StringComparison.Ordinal))
        {
            sourceId = origXlangSourceId;
            targetId = origXlangTargetId;
        }

        if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
        {
            return [];
        }

        return [CreateBinaryEdge(source, mapping, sourceId, targetId, DirectCallConfidence)];
    }

    // ── Confidence ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes the confidence score for a call-chain edge.
    /// Direct calls = 1.0, cross-language = 0.7, dynamic dispatch = 0.5.
    /// Per architecture.md §2.2.
    /// </summary>
    private double ComputeConfidence(Hyperedge source, string calleeId, HypergraphData data)
    {
        if (source.EdgeType == HyperedgeType.CrossLanguageMethodCall)
        {
            return CrossLanguageConfidence;
        }

        if (source.Metadata.TryGetValue("IsVirtualDispatch", out var isVirtual) && isVirtual is true)
        {
            return DynamicDispatchConfidence;
        }

        if (source.Metadata.TryGetValue("IsDynamicDispatch", out var isDynamic) && isDynamic is true)
        {
            return DynamicDispatchConfidence;
        }

        // Check if callee node is an interface, abstract, virtual, or override method.
        // Virtual/override methods indicate dynamic dispatch even without explicit metadata flags.
        if (data.Nodes.TryGetValue(calleeId, out var calleeNode))
        {
            if (string.Equals(calleeNode.NodeType, "Interface", StringComparison.OrdinalIgnoreCase) ||
                calleeNode.Modifiers.Contains("abstract"))
            {
                return CrossLanguageConfidence;
            }

            if (calleeNode.Modifiers.Contains("virtual") ||
                calleeNode.Modifiers.Contains("override"))
            {
                return DynamicDispatchConfidence;
            }
        }

        return DirectCallConfidence;
    }

    // ── Scope index ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a scope index mapping qualified names (Scope.Name) to node IDs
    /// for all definition-type nodes. Used to resolve the caller of an invocation
    /// from the callee node's Scope field.
    /// <para>
    /// Constructor nodes are indexed under additional aliases to handle scope
    /// mismatches where the call-site scope is <c>ClassName.ClassName</c> but the
    /// constructor node is named <c>.ctor</c> with scope <c>ClassName</c>.
    /// </para>
    /// </summary>
    internal static Dictionary<string, string> BuildScopeIndex(HypergraphData data)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (nodeId, node) in data.Nodes)
        {
            if (!DefinitionNodeTypes.Contains(node.NodeType))
            {
                continue;
            }

            var qualifiedName = string.IsNullOrEmpty(node.Scope)
                ? node.Name
                : $"{node.Scope}.{node.Name}";

            index.TryAdd(qualifiedName, nodeId);

            // For constructor nodes, also index as Scope.ClassName (matching
            // the call-site scope pattern where ctor scope = ClassName.ClassName)
            if (string.Equals(node.NodeType, "constructor", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(node.Scope))
            {
                // Extract class name from scope (last segment)
                var scopeLastDot = node.Scope.LastIndexOf('.');
                var className = scopeLastDot >= 0
                    ? node.Scope[(scopeLastDot + 1)..]
                    : node.Scope;

                // Index as Scope.ClassName (e.g., "Namespace.ClassName.ClassName")
                var ctorAlias = $"{node.Scope}.{className}";
                index.TryAdd(ctorAlias, nodeId);
            }
        }

        return index;
    }

    /// <summary>
    /// Resolves the caller definition node from a scope string by looking up
    /// the scope index. Tries the full scope first, then progressively shorter
    /// prefixes to handle constructor/nested scope differences.
    /// Prefers method/constructor/function nodes over class nodes when
    /// resolving from parent scopes to avoid attributing constructor-body
    /// calls to the class instead of the constructor.
    /// </summary>
    private static string? ResolveCallerFromScope(
        string scope,
        Dictionary<string, string> scopeIndex)
    {
        if (scopeIndex.TryGetValue(scope, out var nodeId))
        {
            return nodeId;
        }

        // Try trimming last segment (handles ctor scope mismatch where
        // scope is "Namespace.Class.Method.SubScope")
        var lastDot = scope.LastIndexOf('.');
        if (lastDot > 0)
        {
            var parentScope = scope[..lastDot];
            if (scopeIndex.TryGetValue(parentScope, out nodeId))
            {
                return nodeId;
            }

            // Try one more level up (handles nested scopes like Namespace.Class.Ctor)
            var secondLastDot = parentScope.LastIndexOf('.');
            if (secondLastDot > 0)
            {
                var grandparentScope = parentScope[..secondLastDot];
                if (scopeIndex.TryGetValue(grandparentScope, out nodeId))
                {
                    return nodeId;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the narrowest enclosing function/method/constructor definition node
    /// that contains the given line in the given file. Uses <see cref="HypergraphNode.LineStart"/>
    /// and <see cref="HypergraphNode.LineEnd"/> from extractor data.
    /// <para>
    /// This is particularly useful for PowerShell scripts where cmdlet nodes often
    /// lack Scope data but have accurate line ranges from the extractor.
    /// </para>
    /// </summary>
    private static string? FindEnclosingFunctionByLineRange(
        string filePath, int line, HypergraphData data)
    {
        string? bestMatchId = null;
        var bestSpan = int.MaxValue;

        foreach (var (nodeId, node) in data.Nodes)
        {
            // Only consider callable definition nodes (method, function, constructor)
            if (!DefinitionNodeTypes.Contains(node.NodeType))
            {
                continue;
            }

            // Must be in the same file
            if (!string.Equals(node.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Must have valid line range that contains the call-site
            if (node.LineStart <= 0 || node.LineEnd <= 0)
            {
                continue;
            }

            if (line >= node.LineStart && line <= node.LineEnd)
            {
                var span = node.LineEnd - node.LineStart;
                if (span < bestSpan)
                {
                    bestSpan = span;
                    bestMatchId = nodeId;
                }
            }
        }

        return bestMatchId;
    }

    // ── Node resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Node types that represent references rather than definitions.
    /// These are candidates for resolution to canonical structural nodes.
    /// </summary>
    private static readonly HashSet<string> ReferenceNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "method_ref", "function_ref", "expression", "type", "reference",
        "cmdlet", "remote_cmdlet",
    };

    /// <summary>
    /// Builds a resolution index mapping node names to canonical definition node IDs.
    /// Only definition-type nodes are included; names that appear more than once
    /// (ambiguous) are excluded to avoid incorrect resolution.
    /// </summary>
    internal static Dictionary<string, string> BuildNodeResolutionIndex(HypergraphData data)
    {
        var candidates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (nodeId, node) in data.Nodes)
        {
            if (!DefinitionNodeTypes.Contains(node.NodeType))
            {
                continue;
            }

            // Index by simple name
            if (!candidates.TryGetValue(node.Name, out var list))
            {
                list = [];
                candidates[node.Name] = list;
            }

            list.Add(nodeId);

            // Index by qualified name too
            if (!string.IsNullOrEmpty(node.Scope))
            {
                var qualified = $"{node.Scope}.{node.Name}";
                if (!candidates.TryGetValue(qualified, out var qList))
                {
                    qList = [];
                    candidates[qualified] = qList;
                }

                qList.Add(nodeId);
            }
        }

        // Only keep unambiguous entries (single match)
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, ids) in candidates)
        {
            if (ids.Count == 1)
            {
                index[name] = ids[0];
            }
        }

        return index;
    }

    /// <summary>
    /// Resolves a node ID to a canonical structural definition node if the node
    /// is a reference-type node (method_ref, function_ref, etc.) and an
    /// unambiguous definition exists. If the node is already a definition or
    /// resolution is ambiguous, returns the original ID.
    /// </summary>
    private static string ResolveToCanonical(
        string nodeId,
        HypergraphData data,
        Dictionary<string, string> resolutionIndex)
    {
        if (!data.Nodes.TryGetValue(nodeId, out var node))
        {
            return nodeId;
        }

        if (!ReferenceNodeTypes.Contains(node.NodeType))
        {
            return nodeId;
        }

        // Try qualified name first (Scope.Name), then simple name.
        // Derive simple name from expression text (e.g., "obj.Foo" → "Foo").
        if (!string.IsNullOrEmpty(node.Scope))
        {
            var qualified = $"{node.Scope}.{node.Name}";
            if (resolutionIndex.TryGetValue(qualified, out var resolvedId))
            {
                return resolvedId;
            }
        }

        // Try simple name (last segment after dot for expression-style names)
        var simpleName = node.Name;
        var lastDot = simpleName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < simpleName.Length - 1)
        {
            simpleName = simpleName[(lastDot + 1)..];
        }

        if (resolutionIndex.TryGetValue(simpleName, out var resolved))
        {
            return resolved;
        }

        // Try full name as-is (for non-dot names already equal to simple name)
        if (!string.Equals(simpleName, node.Name, StringComparison.OrdinalIgnoreCase) &&
            resolutionIndex.TryGetValue(node.Name, out resolved))
        {
            return resolved;
        }

        return nodeId;
    }

    /// <summary>
    /// Finds a node ID in a hyperedge by its role name.
    /// </summary>
    private static string? FindNodeByRole(Hyperedge source, string roleName)
    {
        foreach (var (nodeId, role) in source.Roles)
        {
            if (string.Equals(role, roleName, StringComparison.Ordinal))
            {
                return nodeId;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a binary edge (exactly 2 NodeIds) with canonical roles and metadata.
    /// Both sourceNodeId and targetNodeId must be distinct — self-referencing
    /// edges are filtered out by callers before reaching this method.
    /// </summary>
    private static Hyperedge CreateBinaryEdge(
        Hyperedge source,
        EdgeMapping mapping,
        string sourceNodeId,
        string targetNodeId,
        double confidence,
        int subIndex = 0)
    {
        var suffix = subIndex > 0 ? $":{subIndex}" : string.Empty;
        var edgeId = GenerateEdgeId(mapping.SemanticRelation, source, suffix);

        var roles = new Dictionary<string, string>
        {
            [sourceNodeId] = mapping.Role1,
            [targetNodeId] = mapping.Role2,
        };

        return new Hyperedge
        {
            Id = edgeId,
            EdgeType = source.EdgeType,
            NodeIds = [sourceNodeId, targetNodeId],
            Roles = roles,
            Languages = [.. source.Languages],
            Weight = source.Weight,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = mapping.SemanticRelation,
                [MemoryMetadataKeys.MemoryLayer] = CallChainLayer,
                [MemoryMetadataKeys.OriginalEdgeType] = source.EdgeType,
                [MemoryMetadataKeys.Confidence] = confidence,
            }
        };
    }

    /// <summary>
    /// Generates a deterministic edge ID from the semantic relation and source edge.
    /// </summary>
    private static string GenerateEdgeId(string semanticRelation, Hyperedge source, string suffix = "")
    {
        var input = $"{semanticRelation}:{source.Id}{suffix}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash12 = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"cc:{semanticRelation.ToLowerInvariant()}:{hash12}";
    }

    /// <summary>
    /// Generates a deterministic hash-based node ID for synthetic nodes (e.g., file-level callers).
    /// Uses the same 12-char hex format as the extractor node ID convention.
    /// </summary>
    private static string GenerateSyntheticNodeId(string nodeType, string filePath)
    {
        var raw = $"{nodeType}:.{filePath}:0";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    // ── Reverse call-chain lookup ───────────────────────────────────────

    /// <summary>
    /// Performs a reverse call-chain lookup by traversing <c>CALLS</c> edges backward.
    /// Given a callee node ID, returns all caller node IDs from the provided edges.
    /// <para>
    /// Per architecture.md §2.3, reverse lookups are performed by backward traversal
    /// of <c>CALLS</c> edges, not by storing a separate reverse edge. This method
    /// traverses edges by <c>Roles</c> (not by positional <c>NodeIds</c> index)
    /// to correctly handle edges regardless of node order.
    /// </para>
    /// <para>
    /// Self-call (recursive) edges are NOT stored. Self-recursion is detected at
    /// query time rather than via persisted edges, because the <c>Roles</c>
    /// dictionary (keyed by node ID) cannot carry both <c>"caller"</c> and
    /// <c>"callee"</c> for the same node.
    /// </para>
    /// </summary>
    /// <param name="calleeNodeId">The node to find callers for.</param>
    /// <param name="edges">The edges to search (typically all CALLS edges from the graph).</param>
    /// <returns>List of caller node IDs that call the given callee.</returns>
    public static IReadOnlyList<string> FindCallersOf(
        string calleeNodeId,
        IEnumerable<Hyperedge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var callers = new List<string>();

        foreach (var edge in edges)
        {
            if (!edge.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var rel) ||
                rel is not "CALLS")
            {
                continue;
            }

            // Role-based traversal: find caller and callee from Roles dict
            string? callerInEdge = null;
            string? calleeInEdge = null;

            foreach (var (nodeId, role) in edge.Roles)
            {
                if (string.Equals(role, "callee", StringComparison.Ordinal))
                {
                    calleeInEdge = nodeId;
                }
                else if (string.Equals(role, "caller", StringComparison.Ordinal))
                {
                    callerInEdge = nodeId;
                }
            }

            if (callerInEdge != null &&
                string.Equals(calleeInEdge, calleeNodeId, StringComparison.Ordinal))
            {
                callers.Add(callerInEdge);
            }
        }

        return callers;
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

    /// <summary>
    /// Count of self-recursive calls that were skipped because the Roles
    /// dictionary cannot represent both caller and callee for the same node.
    /// Future schema extension (per-participant role list) will support
    /// storing self-call edges. Until then, self-recursion is detected at
    /// query time by examining a node's own outgoing call edges.
    /// </summary>
    public int SelfCallsSkipped { get; set; }

    /// <summary>Count of endpoint nodes upserted to the store.</summary>
    public int NodesUpserted { get; set; }

    /// <summary>Error messages from failed edge creations.</summary>
    public List<string> Errors { get; } = [];
}