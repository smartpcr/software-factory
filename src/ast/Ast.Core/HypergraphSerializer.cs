// -----------------------------------------------------------------------
// <copyright file="HypergraphSerializer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Serialization utilities for hypergraph data.
/// Compatible with HyperNetX and HyperGraphReasoning formats.
/// </summary>
public static class HypergraphSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static void SaveToJson(HypergraphData data, string filePath)
    {
        var exportData = new ExportData
        {
            Nodes = data.Nodes.ToDictionary(
                kvp => kvp.Key,
                kvp => new NodeExport
                {
                    Id = kvp.Value.Id,
                    Name = kvp.Value.Name,
                    NodeType = kvp.Value.NodeType,
                    Language = kvp.Value.Language,
                    FilePath = kvp.Value.FilePath,
                    Scope = kvp.Value.Scope,
                    LineStart = kvp.Value.LineStart,
                    LineEnd = kvp.Value.LineEnd,
                    Modifiers = kvp.Value.Modifiers,
                    Metadata = kvp.Value.Metadata
                }),
            Hyperedges = data.Hyperedges.Select(he => new HyperedgeExport
            {
                Id = he.Id,
                EdgeType = he.EdgeType.ToString(),
                Nodes = he.NodeIds,
                Roles = he.Roles,
                Languages = he.Languages,
                Weight = he.Weight,
                Metadata = he.Metadata
            }).ToList(),
            Metadata = new MetadataExport
            {
                RepoPath = data.Metadata.RepoPath,
                NumFiles = data.Metadata.NumFiles,
                NumCsharpNodes = data.Metadata.NumCSharpNodes,
                NumPowershellNodes = data.Metadata.NumPowerShellNodes,
                NumCrossLanguageEdges = data.Metadata.NumCrossLanguageEdges,
                Files = data.Metadata.Files,
                ExtractedAt = data.Metadata.ExtractedAt.ToString("O"),
                Repository = data.Metadata.Repository,
                Projects = data.Metadata.Projects
            }
        };

        var json = JsonSerializer.Serialize(exportData, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public static string ToJson(HypergraphData data)
    {
        var exportData = new ExportData
        {
            Nodes = data.Nodes.ToDictionary(
                kvp => kvp.Key,
                kvp => new NodeExport
                {
                    Id = kvp.Value.Id,
                    Name = kvp.Value.Name,
                    NodeType = kvp.Value.NodeType,
                    Language = kvp.Value.Language,
                    FilePath = kvp.Value.FilePath,
                    Scope = kvp.Value.Scope,
                    LineStart = kvp.Value.LineStart,
                    LineEnd = kvp.Value.LineEnd,
                    Modifiers = kvp.Value.Modifiers,
                    Metadata = kvp.Value.Metadata
                }),
            Hyperedges = data.Hyperedges.Select(he => new HyperedgeExport
            {
                Id = he.Id,
                EdgeType = he.EdgeType.ToString(),
                Nodes = he.NodeIds,
                Roles = he.Roles,
                Languages = he.Languages,
                Weight = he.Weight,
                Metadata = he.Metadata
            }).ToList(),
            Metadata = new MetadataExport
            {
                RepoPath = data.Metadata.RepoPath,
                NumFiles = data.Metadata.NumFiles,
                NumCsharpNodes = data.Metadata.NumCSharpNodes,
                NumPowershellNodes = data.Metadata.NumPowerShellNodes,
                NumCrossLanguageEdges = data.Metadata.NumCrossLanguageEdges,
                Files = data.Metadata.Files,
                ExtractedAt = data.Metadata.ExtractedAt.ToString("O"),
                Repository = data.Metadata.Repository,
                Projects = data.Metadata.Projects
            }
        };

        return JsonSerializer.Serialize(exportData, JsonOptions);
    }

    public static HypergraphData LoadFromJson(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return FromJson(json);
    }

    public static HypergraphData FromJson(string json)
    {
        var exportData = JsonSerializer.Deserialize<ExportData>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize hypergraph data");

        return new HypergraphData
        {
            Nodes = exportData.Nodes.ToDictionary(
                kvp => kvp.Key,
                kvp => new HypergraphNode
                {
                    Id = kvp.Value.Id,
                    Name = kvp.Value.Name,
                    NodeType = kvp.Value.NodeType,
                    Language = kvp.Value.Language,
                    FilePath = kvp.Value.FilePath,
                    Scope = kvp.Value.Scope,
                    LineStart = kvp.Value.LineStart,
                    LineEnd = kvp.Value.LineEnd,
                    Modifiers = kvp.Value.Modifiers,
                    Metadata = kvp.Value.Metadata
                }),
            Hyperedges = exportData.Hyperedges.Select(he => new Hyperedge
            {
                Id = he.Id,
                EdgeType = Enum.Parse<HyperedgeType>(he.EdgeType, ignoreCase: true),
                NodeIds = he.Nodes,
                Roles = he.Roles,
                Languages = he.Languages,
                Weight = he.Weight,
                Metadata = he.Metadata
            }).ToList(),
            Metadata = new HypergraphMetadata
            {
                RepoPath = exportData.Metadata.RepoPath,
                NumFiles = exportData.Metadata.NumFiles,
                NumCSharpNodes = exportData.Metadata.NumCsharpNodes,
                NumPowerShellNodes = exportData.Metadata.NumPowershellNodes,
                NumCrossLanguageEdges = exportData.Metadata.NumCrossLanguageEdges,
                Files = exportData.Metadata.Files,
                ExtractedAt = DateTime.Parse(exportData.Metadata.ExtractedAt),
                Repository = exportData.Metadata.Repository,
                Projects = exportData.Metadata.Projects
            }
        };
    }

    /// <summary>
    /// Export to HyperNetX-compatible format.
    /// </summary>
    public static void SaveToHyperNetXFormat(HypergraphData data, string filePath)
    {
        var json = ToHyperNetXFormat(data);
        File.WriteAllText(filePath, json);
    }

    public static string ToHyperNetXFormat(HypergraphData data)
    {
        // HyperNetX expects: {"edge_name": ["node1", "node2", ...]}
        var edgeDict = data.Hyperedges.ToDictionary(
            he => $"{he.EdgeType}:{he.Id}",
            he => he.NodeIds.ToArray()
        );

        return JsonSerializer.Serialize(edgeDict, JsonOptions);
    }

    // Export DTOs
    private class ExportData
    {
        public Dictionary<string, NodeExport> Nodes { get; set; } = [];
        public List<HyperedgeExport> Hyperedges { get; set; } = [];
        public MetadataExport Metadata { get; set; } = new();
    }

    private class NodeExport
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string NodeType { get; set; } = "";
        public string Language { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Scope { get; set; } = "";
        public int LineStart { get; set; }
        public int LineEnd { get; set; }
        public List<string> Modifiers { get; set; } = [];
        public Dictionary<string, object?> Metadata { get; set; } = [];
    }

    private class HyperedgeExport
    {
        public string Id { get; set; } = "";
        public string EdgeType { get; set; } = "";
        public List<string> Nodes { get; set; } = [];
        public Dictionary<string, string> Roles { get; set; } = [];
        public List<string> Languages { get; set; } = [];
        public double Weight { get; set; } = 1.0;
        public Dictionary<string, object?> Metadata { get; set; } = [];
    }

    private class MetadataExport
    {
        public string? RepoPath { get; set; }
        public int NumFiles { get; set; }
        public int NumCsharpNodes { get; set; }
        public int NumPowershellNodes { get; set; }
        public int NumCrossLanguageEdges { get; set; }
        public List<string> Files { get; set; } = [];
        public string ExtractedAt { get; set; } = "";
        public RepositoryMetadata? Repository { get; set; }
        public List<ProjectMetadata> Projects { get; set; } = [];
    }
}