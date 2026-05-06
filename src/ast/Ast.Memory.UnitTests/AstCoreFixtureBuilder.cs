// -----------------------------------------------------------------------
// <copyright file="AstCoreFixtureBuilder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Csharp;
using Ast.Memory.InMemory;
using Ast.Memory.Models;

/// <summary>
/// Builds a graph store populated from the actual <c>src/ast/Ast.Core/</c> fixture
/// using the <see cref="CsharpAstParser"/> extraction pipeline, converting
/// <see cref="AstNode"/> results into <see cref="HypergraphNode"/> instances.
/// </summary>
internal static class AstCoreFixtureBuilder
{
    /// <summary>
    /// Locates the Ast.Core source directory by walking up from the test's output directory.
    /// </summary>
    internal static string FindAstCoreSourceDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "ast", "Ast.Core");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "HypergraphModels.cs")))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "Cannot locate src/ast/Ast.Core/ from test output directory. " +
            "Ensure the test runs from within the repository tree.");
    }

    /// <summary>
    /// Converts an <see cref="AstNode"/> (from <see cref="CsharpAstParser"/>) into a
    /// <see cref="HypergraphNode"/> with memory-layer metadata, using the repo-relative
    /// file path convention <c>/src/ast/Ast.Core/{fileName}::{nodeName}</c>.
    /// </summary>
    internal static HypergraphNode ToHypergraphNode(AstNode astNode, string repoRelativeFilePath)
    {
        var id = $"{repoRelativeFilePath}::{astNode.Name}";
        var modifiers = string.IsNullOrEmpty(astNode.Modifiers)
            ? new List<string>()
            : astNode.Modifiers.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        return new HypergraphNode
        {
            Id = id,
            Name = astNode.Name,
            NodeType = astNode.NodeType.ToString(),
            Language = "C#",
            FilePath = repoRelativeFilePath,
            LineStart = astNode.StartLine,
            LineEnd = astNode.EndLine,
            Modifiers = modifiers,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.SinceCommit] = "HEAD",
                [MemoryMetadataKeys.SinceIndex] = 1
            }
        };
    }

    /// <summary>
    /// Parses <c>HypergraphModels.cs</c> through the <see cref="CsharpAstParser"/> pipeline
    /// and populates the given <see cref="InMemoryMemoryGraphStore"/> with converted nodes.
    /// Returns all created <see cref="HypergraphNode"/> instances.
    /// </summary>
    internal static async Task<IReadOnlyList<HypergraphNode>> PopulateFromFixtureAsync(
        InMemoryMemoryGraphStore graphStore)
    {
        var sourceDir = FindAstCoreSourceDir();
        var filePath = Path.Combine(sourceDir, "HypergraphModels.cs");

        var parser = new CsharpAstParser();
        var astNodes = parser.ParseFile(filePath).ToList();

        const string repoRelativePath = "/src/ast/Ast.Core/HypergraphModels.cs";
        var hypergraphNodes = new List<HypergraphNode>();

        foreach (var astNode in astNodes)
        {
            var node = ToHypergraphNode(astNode, repoRelativePath);
            hypergraphNodes.Add(node);
            await graphStore.CreateNodeAsync(node).ConfigureAwait(false);
        }

        // Create structural edges between types and their members
        var typeNodes = hypergraphNodes
            .Where(n => n.NodeType is "Class" or "Record" or "Struct" or "Interface" or "Enum")
            .ToList();

        var memberNodes = hypergraphNodes
            .Where(n => n.NodeType is "Method" or "Property" or "Field" or "Constructor")
            .ToList();

        foreach (var typeNode in typeNodes)
        {
            var members = memberNodes
                .Where(m => m.LineStart >= typeNode.LineStart && m.LineEnd <= typeNode.LineEnd)
                .ToList();

            foreach (var member in members)
            {
                var edge = new Hyperedge
                {
                    Id = $"contains:{typeNode.Id}→{member.Id}",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = new List<string> { typeNode.Id, member.Id },
                    Roles = new Dictionary<string, string>
                    {
                        [typeNode.Id] = "parent",
                        [member.Id] = "member"
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.MemoryLayer] = "structural",
                        [MemoryMetadataKeys.SemanticRelation] = "CONTAINS"
                    }
                };

                await graphStore.CreateEdgeAsync(edge).ConfigureAwait(false);
            }
        }

        return hypergraphNodes;
    }
}
