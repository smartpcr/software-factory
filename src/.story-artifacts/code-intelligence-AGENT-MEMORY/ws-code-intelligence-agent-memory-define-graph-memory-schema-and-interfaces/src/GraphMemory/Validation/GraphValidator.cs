using GraphMemory.Interfaces;
using GraphMemory.Models;
using GraphMemory.Serialization;

namespace GraphMemory.Validation;

/// <summary>
/// Validates graph-level invariants that JSON Schema cannot express:
/// unique IDs, referential integrity, kind-property type matching,
/// required field constraints, range constraints, and semantic rules.
/// </summary>
public sealed class GraphValidator : IGraphValidator
{
    // Edge endpoint constraints: maps edge kinds to the set of valid source/target node kinds.
    // null means any node kind is acceptable.
    private static readonly Dictionary<EdgeKind, (HashSet<NodeKind>? ValidSources, HashSet<NodeKind>? ValidTargets)> EdgeEndpointRules = new()
    {
        [EdgeKind.Contains] = (null, null), // structural containment can connect many kinds
        [EdgeKind.Calls] = (
            new HashSet<NodeKind> { NodeKind.Function, NodeKind.Method },
            new HashSet<NodeKind> { NodeKind.Function, NodeKind.Method }),
        [EdgeKind.CalledBy] = (
            new HashSet<NodeKind> { NodeKind.Function, NodeKind.Method },
            new HashSet<NodeKind> { NodeKind.Function, NodeKind.Method }),
        [EdgeKind.Imports] = (
            new HashSet<NodeKind> { NodeKind.File, NodeKind.Module },
            new HashSet<NodeKind> { NodeKind.File, NodeKind.Module, NodeKind.Package }),
        [EdgeKind.ReExports] = (
            new HashSet<NodeKind> { NodeKind.File, NodeKind.Module },
            new HashSet<NodeKind> { NodeKind.File, NodeKind.Module }),
        [EdgeKind.Exports] = (
            new HashSet<NodeKind> { NodeKind.File, NodeKind.Module },
            null),
        [EdgeKind.Inherits] = (
            new HashSet<NodeKind> { NodeKind.Class, NodeKind.Interface },
            new HashSet<NodeKind> { NodeKind.Class, NodeKind.Interface }),
        [EdgeKind.Overrides] = (
            new HashSet<NodeKind> { NodeKind.Method },
            new HashSet<NodeKind> { NodeKind.Method }),
        [EdgeKind.Implements] = (
            new HashSet<NodeKind> { NodeKind.Class },
            new HashSet<NodeKind> { NodeKind.Interface }),
        [EdgeKind.Instantiates] = (null, new HashSet<NodeKind> { NodeKind.Class }),
        [EdgeKind.Expertise] = (null, null), // expertise edges can connect many kinds
        [EdgeKind.AppliesTo] = (null, null),
        [EdgeKind.SimilarTo] = (null, null),
        [EdgeKind.BelongsToCluster] = (null, new HashSet<NodeKind> { NodeKind.SemanticCluster }),
        [EdgeKind.Supersedes] = (null, null),
        [EdgeKind.DerivedFrom] = (null, null),
        [EdgeKind.FrequentlyUsedWith] = (null, null),
        [EdgeKind.DependsOn] = (null, null),
        [EdgeKind.References] = (null, null),
    };

    /// <summary>Regex pattern for valid extension keys: must start with "x-".</summary>
    private static readonly System.Text.RegularExpressions.Regex ExtensionKeyPattern =
        new(@"^x-", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ValidationResult Validate(CodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var result = new ValidationResult();

        if (graph.Nodes is null)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "NULL_NODES",
                Message = "Graph nodes list must not be null.",
            });
            return result;
        }

        if (graph.Edges is null)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "NULL_EDGES",
                Message = "Graph edges list must not be null.",
            });
            return result;
        }

        if (graph.SchemaVersion != CodeGraph.CurrentSchemaVersion)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_VERSION",
                Message = $"Expected schema version '{CodeGraph.CurrentSchemaVersion}', got '{graph.SchemaVersion}'.",
            });
        }

        if (string.IsNullOrWhiteSpace(graph.Id))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "MISSING_GRAPH_ID",
                Message = "Graph id must not be empty.",
            });
        }

        // Unique node IDs
        var nodeIds = new HashSet<string>();
        var nodeKindMap = new Dictionary<string, NodeKind>();
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "EMPTY_NODE_ID",
                    Message = "Node id must not be empty.",
                });
                continue;
            }

            if (node.Properties is null)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "NULL_NODE_PROPERTIES",
                    Message = $"Node '{node.Id}' has null properties.",
                    ElementId = node.Id,
                });
                continue;
            }

            if (!nodeIds.Add(node.Id))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "DUPLICATE_NODE_ID",
                    Message = $"Duplicate node id '{node.Id}'.",
                    ElementId = node.Id,
                });
            }
            else
            {
                nodeKindMap[node.Id] = node.Kind;
            }

            if (string.IsNullOrWhiteSpace(node.Name))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "EMPTY_NODE_NAME",
                    Message = $"Node '{node.Id}' has an empty name.",
                    ElementId = node.Id,
                });
            }

            ValidateNodeProperties(node, result);
            ValidateExtensionKeys(node.Extensions, node.Id, "Node", result);
        }

        // Unique edge IDs + referential integrity
        var edgeIds = new HashSet<string>();
        foreach (var edge in graph.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "EMPTY_EDGE_ID",
                    Message = "Edge id must not be empty.",
                });
                continue;
            }

            if (edge.Properties is null)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "NULL_EDGE_PROPERTIES",
                    Message = $"Edge '{edge.Id}' has null properties.",
                    ElementId = edge.Id,
                });
                continue;
            }

            if (!edgeIds.Add(edge.Id))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "DUPLICATE_EDGE_ID",
                    Message = $"Duplicate edge id '{edge.Id}'.",
                    ElementId = edge.Id,
                });
            }

            if (!nodeIds.Contains(edge.Source))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "DANGLING_SOURCE",
                    Message = $"Edge '{edge.Id}' references non-existent source node '{edge.Source}'.",
                    ElementId = edge.Id,
                });
            }

            if (!nodeIds.Contains(edge.Target))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "DANGLING_TARGET",
                    Message = $"Edge '{edge.Id}' references non-existent target node '{edge.Target}'.",
                    ElementId = edge.Id,
                });
            }

            // Semantic edge endpoint constraints
            if (EdgeEndpointRules.TryGetValue(edge.Kind, out var rules))
            {
                if (rules.ValidSources is not null &&
                    nodeKindMap.TryGetValue(edge.Source, out var srcKind) &&
                    !rules.ValidSources.Contains(srcKind))
                {
                    result.Warnings.Add(
                        $"Edge '{edge.Id}' ({edge.Kind}) has source node '{edge.Source}' " +
                        $"of kind '{srcKind}' which is not a typical source for this edge kind.");
                }

                if (rules.ValidTargets is not null &&
                    nodeKindMap.TryGetValue(edge.Target, out var tgtKind) &&
                    !rules.ValidTargets.Contains(tgtKind))
                {
                    result.Warnings.Add(
                        $"Edge '{edge.Id}' ({edge.Kind}) has target node '{edge.Target}' " +
                        $"of kind '{tgtKind}' which is not a typical target for this edge kind.");
                }
            }

            ValidateEdgeProperties(edge, result);
            ValidateExtensionKeys(edge.Extensions, edge.Id, "Edge", result);
        }

        // Metadata required fields
        if (string.IsNullOrWhiteSpace(graph.Metadata?.CreatedAt))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "MISSING_METADATA_CREATED_AT",
                Message = "Graph metadata.createdAt is required.",
            });
        }

        if (string.IsNullOrWhiteSpace(graph.Metadata?.UpdatedAt))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "MISSING_METADATA_UPDATED_AT",
                Message = "Graph metadata.updatedAt is required.",
            });
        }

        ValidateExtensionKeys(graph.Metadata?.Extensions, "metadata", "Metadata", result);

        return result;
    }

    private static void ValidateNodeProperties(GraphNode node, ValidationResult result)
    {
        if (!NodePropertiesConverter.KindToType.TryGetValue(node.Kind, out var expectedType))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "UNKNOWN_NODE_KIND",
                Message = $"Unknown node kind '{node.Kind}' on node '{node.Id}'.",
                ElementId = node.Id,
            });
            return;
        }

        if (node.Properties.GetType() != expectedType)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "PROPERTY_TYPE_MISMATCH",
                Message = $"Node '{node.Id}' (kind={node.Kind}) has properties type " +
                          $"'{node.Properties.GetType().Name}' but expected '{expectedType.Name}'.",
                ElementId = node.Id,
            });
            return;
        }

        // Required string fields per kind
        switch (node.Properties)
        {
            case RepoProperties rp:
                if (rp.Languages is null || rp.Languages.Length == 0)
                    result.Errors.Add(new ValidationError { Code = "MISSING_REQUIRED_FIELD", Message = $"Node '{node.Id}' (repo) requires non-empty 'languages'.", ElementId = node.Id });
                break;
            case PackageProperties pp:
                ValidateRequiredStringField(pp.PackageManager, "packageManager", node.Id, result);
                break;
            case ModuleProperties mp:
                ValidateRequiredStringField(mp.Path, "path", node.Id, result);
                break;
            case DirectoryProperties dp:
                ValidateRequiredStringField(dp.Path, "path", node.Id, result);
                break;
            case FileProperties fp:
                ValidateRequiredStringField(fp.Path, "path", node.Id, result);
                break;
            case WorkspaceProperties wp:
                ValidateRequiredStringField(wp.RootPath, "rootPath", node.Id, result);
                break;
            case ExpertiseNoteProperties en:
                ValidateRequiredStringField(en.Content, "content", node.Id, result);
                break;
            case PatternProperties pat:
                ValidateRequiredStringField(pat.Content, "content", node.Id, result);
                break;
            case PitfallProperties pit:
                ValidateRequiredStringField(pit.Content, "content", node.Id, result);
                break;
            case PreferenceProperties pref:
                ValidateRequiredStringField(pref.Content, "content", node.Id, result);
                break;
            case InsightProperties ins:
                ValidateRequiredStringField(ins.Content, "content", node.Id, result);
                break;
        }

        ValidateSourceLocation(node, result);
        ValidateNodeNumericConstraints(node, result);

        // Validate embedding length consistency
        if (node.Embedding is { Length: 0 })
        {
            result.Warnings.Add($"Node '{node.Id}' has an empty embedding array.");
        }

        // Validate version range consistency
        if (node.VersionRange is { } vr && !string.IsNullOrEmpty(vr.RemovedIn) && string.IsNullOrEmpty(vr.IntroducedIn))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_VERSION_RANGE",
                Message = $"Node '{node.Id}' has removedIn set without introducedIn.",
                ElementId = node.Id,
            });
        }
    }

    private static void ValidateSourceLocation(GraphNode node, ValidationResult result)
    {
        int? startLine = null, endLine = null;
        int? startColumn = null, endColumn = null;
        string? filePath = null;

        switch (node.Properties)
        {
            case ClassProperties p: startLine = p.StartLine; endLine = p.EndLine; startColumn = p.StartColumn; endColumn = p.EndColumn; filePath = p.FilePath; break;
            case InterfaceProperties p: startLine = p.StartLine; endLine = p.EndLine; startColumn = p.StartColumn; endColumn = p.EndColumn; filePath = p.FilePath; break;
            case TypeAliasProperties p: startLine = p.StartLine; endLine = p.EndLine; startColumn = p.StartColumn; endColumn = p.EndColumn; filePath = p.FilePath; break;
            case FunctionProperties p: startLine = p.StartLine; endLine = p.EndLine; startColumn = p.StartColumn; endColumn = p.EndColumn; filePath = p.FilePath; break;
            case MethodProperties p: startLine = p.StartLine; endLine = p.EndLine; startColumn = p.StartColumn; endColumn = p.EndColumn; filePath = p.FilePath; break;
            case VariableProperties p: startLine = p.StartLine; endLine = p.EndLine; startColumn = p.StartColumn; endColumn = p.EndColumn; filePath = p.FilePath; break;
            case EnumProperties p: startLine = p.StartLine; endLine = p.EndLine; startColumn = p.StartColumn; endColumn = p.EndColumn; filePath = p.FilePath; break;
            case SymbolProperties p: startLine = p.StartLine; endLine = p.EndLine; startColumn = p.StartColumn; endColumn = p.EndColumn; filePath = p.FilePath; break;
        }

        if (startLine.HasValue && startLine < 1)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_START_LINE",
                Message = $"Node '{node.Id}' has startLine ({startLine}) < 1.",
                ElementId = node.Id,
            });
        }

        if (endLine.HasValue && endLine < 1)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_END_LINE",
                Message = $"Node '{node.Id}' has endLine ({endLine}) < 1.",
                ElementId = node.Id,
            });
        }

        if (startLine.HasValue && endLine.HasValue && endLine < startLine)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_SOURCE_RANGE",
                Message = $"Node '{node.Id}' has endLine ({endLine}) < startLine ({startLine}).",
                ElementId = node.Id,
            });
        }

        if (startColumn.HasValue && startColumn < 0)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_START_COLUMN",
                Message = $"Node '{node.Id}' has startColumn ({startColumn}) < 0.",
                ElementId = node.Id,
            });
        }

        if (endColumn.HasValue && endColumn < 0)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_END_COLUMN",
                Message = $"Node '{node.Id}' has endColumn ({endColumn}) < 0.",
                ElementId = node.Id,
            });
        }

        if (filePath is not null && string.IsNullOrWhiteSpace(filePath))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_FILE_PATH",
                Message = $"Node '{node.Id}' has an empty filePath.",
                ElementId = node.Id,
            });
        }
    }

    private static void ValidateNodeNumericConstraints(GraphNode node, ValidationResult result)
    {
        switch (node.Properties)
        {
            case FileProperties fp when fp.Loc.HasValue && fp.Loc < 0:
                result.Errors.Add(new ValidationError
                {
                    Code = "NEGATIVE_LOC",
                    Message = $"Node '{node.Id}' has negative loc ({fp.Loc}).",
                    ElementId = node.Id,
                });
                break;
            case SemanticClusterProperties scp when scp.MemberCount.HasValue && scp.MemberCount < 0:
                result.Errors.Add(new ValidationError
                {
                    Code = "NEGATIVE_MEMBER_COUNT",
                    Message = $"Node '{node.Id}' has negative memberCount ({scp.MemberCount}).",
                    ElementId = node.Id,
                });
                break;
            case PatternProperties pp when pp.Confidence.HasValue && (pp.Confidence < 0 || pp.Confidence > 1):
                result.Errors.Add(new ValidationError
                {
                    Code = "PATTERN_CONFIDENCE_OUT_OF_RANGE",
                    Message = $"Node '{node.Id}' has confidence {pp.Confidence} outside [0,1].",
                    ElementId = node.Id,
                });
                break;
            case InsightProperties ip when ip.Confidence.HasValue && (ip.Confidence < 0 || ip.Confidence > 1):
                result.Errors.Add(new ValidationError
                {
                    Code = "INSIGHT_CONFIDENCE_OUT_OF_RANGE",
                    Message = $"Node '{node.Id}' has confidence {ip.Confidence} outside [0,1].",
                    ElementId = node.Id,
                });
                break;
        }
    }

    private static void ValidateEdgeProperties(GraphEdge edge, ValidationResult result)
    {
        if (!EdgePropertiesConverter.KindToType.TryGetValue(edge.Kind, out var expectedType))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "UNKNOWN_EDGE_KIND",
                Message = $"Unknown edge kind '{edge.Kind}' on edge '{edge.Id}'.",
                ElementId = edge.Id,
            });
            return;
        }

        if (edge.Properties.GetType() != expectedType)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "PROPERTY_TYPE_MISMATCH",
                Message = $"Edge '{edge.Id}' (kind={edge.Kind}) has properties type " +
                          $"'{edge.Properties.GetType().Name}' but expected '{expectedType.Name}'.",
                ElementId = edge.Id,
            });
            return;
        }

        // Expertise weight/confidence must be 0..1
        if (edge.Properties is ExpertiseProperties ep)
        {
            if (ep.Weight < 0 || ep.Weight > 1)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "EXPERTISE_WEIGHT_OUT_OF_RANGE",
                    Message = $"Edge '{edge.Id}' expertise weight {ep.Weight} is outside [0,1].",
                    ElementId = edge.Id,
                });
            }
            if (ep.Confidence < 0 || ep.Confidence > 1)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "EXPERTISE_CONFIDENCE_OUT_OF_RANGE",
                    Message = $"Edge '{edge.Id}' expertise confidence {ep.Confidence} is outside [0,1].",
                    ElementId = edge.Id,
                });
            }
            if (ep.ReinforcementCount.HasValue && ep.ReinforcementCount < 0)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "NEGATIVE_REINFORCEMENT_COUNT",
                    Message = $"Edge '{edge.Id}' has negative reinforcementCount ({ep.ReinforcementCount}).",
                    ElementId = edge.Id,
                });
            }
            if (ep.ObservationCount.HasValue && ep.ObservationCount < 0)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "NEGATIVE_OBSERVATION_COUNT",
                    Message = $"Edge '{edge.Id}' has negative observationCount ({ep.ObservationCount}).",
                    ElementId = edge.Id,
                });
            }
        }

        // SimilarTo cosine_similarity must be 0..1
        if (edge.Properties is SimilarToProperties sp)
        {
            if (sp.CosineSimilarity < 0 || sp.CosineSimilarity > 1)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "COSINE_SIMILARITY_OUT_OF_RANGE",
                    Message = $"Edge '{edge.Id}' cosine_similarity {sp.CosineSimilarity} is outside [0,1].",
                    ElementId = edge.Id,
                });
            }
        }

        // Calls/CalledBy confidence must be 0..1
        if (edge.Properties is CallsProperties cp && cp.Confidence.HasValue && (cp.Confidence < 0 || cp.Confidence > 1))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "CALLS_CONFIDENCE_OUT_OF_RANGE",
                Message = $"Edge '{edge.Id}' calls confidence {cp.Confidence} is outside [0,1].",
                ElementId = edge.Id,
            });
        }

        if (edge.Properties is CalledByProperties cbp && cbp.Confidence.HasValue && (cbp.Confidence < 0 || cbp.Confidence > 1))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "CALLED_BY_CONFIDENCE_OUT_OF_RANGE",
                Message = $"Edge '{edge.Id}' calledBy confidence {cbp.Confidence} is outside [0,1].",
                ElementId = edge.Id,
            });
        }

        // BelongsToCluster strength must be 0..1
        if (edge.Properties is BelongsToClusterProperties bcp && bcp.Strength.HasValue && (bcp.Strength < 0 || bcp.Strength > 1))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "CLUSTER_STRENGTH_OUT_OF_RANGE",
                Message = $"Edge '{edge.Id}' strength {bcp.Strength} is outside [0,1].",
                ElementId = edge.Id,
            });
        }

        // FrequentlyUsedWith strength and coOccurrenceCount
        if (edge.Properties is FrequentlyUsedWithProperties fuw)
        {
            if (fuw.Strength.HasValue && (fuw.Strength < 0 || fuw.Strength > 1))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "FREQUENTLY_USED_WITH_STRENGTH_OUT_OF_RANGE",
                    Message = $"Edge '{edge.Id}' strength {fuw.Strength} is outside [0,1].",
                    ElementId = edge.Id,
                });
            }
            if (fuw.CoOccurrenceCount.HasValue && fuw.CoOccurrenceCount < 0)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "NEGATIVE_CO_OCCURRENCE_COUNT",
                    Message = $"Edge '{edge.Id}' has negative coOccurrenceCount ({fuw.CoOccurrenceCount}).",
                    ElementId = edge.Id,
                });
            }
        }

        // Contains order must be >= 0
        if (edge.Properties is ContainsProperties ctp && ctp.Order.HasValue && ctp.Order < 0)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "NEGATIVE_ORDER",
                Message = $"Edge '{edge.Id}' has negative order ({ctp.Order}).",
                ElementId = edge.Id,
            });
        }

        // CallSite line must be >= 1
        CallSite? callSite = edge.Properties switch
        {
            CallsProperties c => c.CallSite,
            CalledByProperties c => c.CallSite,
            _ => null,
        };
        if (callSite is not null && callSite.Line < 1)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_CALL_SITE_LINE",
                Message = $"Edge '{edge.Id}' has callSite.line ({callSite.Line}) < 1.",
                ElementId = edge.Id,
            });
        }
        if (callSite?.Column.HasValue == true && callSite.Column < 0)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_CALL_SITE_COLUMN",
                Message = $"Edge '{edge.Id}' has callSite.column ({callSite.Column}) < 0.",
                ElementId = edge.Id,
            });
        }

        // Self-referencing containment is suspicious
        if (edge.Kind == EdgeKind.Contains && edge.Source == edge.Target)
        {
            result.Warnings.Add($"Edge '{edge.Id}' is a self-referencing 'contains' edge.");
        }
    }

    private static void ValidateExtensionKeys(Dictionary<string, object>? extensions, string elementId, string elementType, ValidationResult result)
    {
        if (extensions is null) return;
        foreach (var key in extensions.Keys)
        {
            if (!ExtensionKeyPattern.IsMatch(key))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_EXTENSION_KEY",
                    Message = $"{elementType} '{elementId}' has extension key '{key}' that does not match required pattern '^x-'.",
                    ElementId = elementId,
                });
            }
        }
    }

    private static void ValidateRequiredStringField(string? value, string fieldName, string nodeId, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "MISSING_REQUIRED_FIELD",
                Message = $"Node '{nodeId}' is missing required field '{fieldName}' (empty or null).",
                ElementId = nodeId,
            });
        }
    }
}
