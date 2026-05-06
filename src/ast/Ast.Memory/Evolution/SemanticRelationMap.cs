// -----------------------------------------------------------------------
// <copyright file="SemanticRelationMap.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

using Ast.Core;

/// <summary>
/// Maps semantic relation strings (e.g., "CALLS", "SIMILAR_TO") to
/// canonical <see cref="HyperedgeType"/> values used by the AST graph layer.
/// This bridges the gap between edges written by <c>Ast.GraphDb.Neo4jGraphStore</c>
/// (which use <see cref="HyperedgeType"/>-based Neo4j relationship labels) and
/// memory-layer edges (which carry <c>Metadata["SemanticRelation"]</c>).
/// </summary>
public static class SemanticRelationMap
{
    /// <summary>
    /// Maps "CALLS" → the <see cref="HyperedgeType"/> values that represent
    /// call-chain relationships in the canonical AST graph.
    /// </summary>
    private static readonly Dictionary<string, HashSet<HyperedgeType>> RelationToTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CALLS"] =
        [
            HyperedgeType.MethodInvocation,
            HyperedgeType.CmdletCall,
            HyperedgeType.ConstructorInvocation,
            HyperedgeType.ModuleFunctionCall,
            HyperedgeType.CrossLanguageMethodCall
        ],
        ["SIMILAR_TO"] = [],
        ["BELONGS_TO_CLUSTER"] = []
    };

    /// <summary>
    /// Maps semantic relation strings to the corresponding Neo4j relationship
    /// type labels used by <c>Neo4jGraphStore.GetRelationshipType</c>.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> RelationToNeo4jTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CALLS"] = ["CALLS"],
        ["SIMILAR_TO"] = ["SIMILAR_TO"],
        ["BELONGS_TO_CLUSTER"] = ["BELONGS_TO_CLUSTER", "MEMORY_EDGE"]
    };

    /// <summary>
    /// Returns the set of <see cref="HyperedgeType"/> values that correspond
    /// to the given semantic relation strings.
    /// </summary>
    public static HashSet<HyperedgeType> ToHyperedgeTypes(IEnumerable<string> semanticRelations)
    {
        var result = new HashSet<HyperedgeType>();
        foreach (var rel in semanticRelations)
        {
            if (RelationToTypes.TryGetValue(rel, out var types))
            {
                result.UnionWith(types);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the set of Neo4j relationship type label strings that correspond
    /// to the given semantic relation strings.
    /// </summary>
    public static HashSet<string> ToNeo4jRelationshipTypes(IEnumerable<string> semanticRelations)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in semanticRelations)
        {
            if (RelationToNeo4jTypes.TryGetValue(rel, out var types))
            {
                result.UnionWith(types);
            }
        }

        return result;
    }
}