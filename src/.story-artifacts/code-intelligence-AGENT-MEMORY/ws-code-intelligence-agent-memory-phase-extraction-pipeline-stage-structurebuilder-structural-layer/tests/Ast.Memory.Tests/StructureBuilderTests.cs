// -----------------------------------------------------------------------
// <copyright file="StructureBuilderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Extraction;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

[TestClass]
public class StructureBuilderTests
{
    private static readonly VersionRange TestVersion = new("abc123", 1, null, null);

    // Realistic source code for the HypergraphModels.cs file
    private const string HypergraphModelsFileContent =
        "// Copyright header\n"
        + "namespace Ast.Core;\n"
        + "\n"
        + "public enum HyperedgeType { ClassDefinition, MethodDefinition }\n"
        + "\n"
        + "public class HypergraphNode\n"
        + "{\n"
        + "    public required string Id { get; init; }\n"
        + "    public required string Name { get; init; }\n"
        + "    public required string NodeType { get; init; }\n"
        + "    public required string Language { get; init; }\n"
        + "    public required string FilePath { get; init; }\n"
        + "\n"
        + "    public string GetEmbeddingText()\n"
        + "    {\n"
        + "        var parts = new List<string>();\n"
        + "        return string.Join(\"\\n\", parts);\n"
        + "    }\n"
        + "}\n"
        + "\n"
        + "public class Hyperedge\n"
        + "{\n"
        + "    public required string Id { get; init; }\n"
        + "}\n";

    private const string ClassSourceCode =
        "public class HypergraphNode\n{\n    public required string Id { get; init; }\n"
        + "    public required string Name { get; init; }\n"
        + "    public required string NodeType { get; init; }\n"
        + "    public required string Language { get; init; }\n"
        + "    public required string FilePath { get; init; }\n"
        + "\n"
        + "    public string GetEmbeddingText()\n"
        + "    {\n"
        + "        var parts = new List<string>();\n"
        + "        return string.Join(\"\\n\", parts);\n"
        + "    }\n}";

    private const string MethodSourceCode =
        "    public string GetEmbeddingText()\n"
        + "    {\n"
        + "        var parts = new List<string>();\n"
        + "        return string.Join(\"\\n\", parts);\n"
        + "    }";

    private const string CSharpParserFileContent =
        "using Microsoft.CodeAnalysis;\nusing Ast.Core;\n\npublic class CSharpParser { }";

    private const string PowerShellModuleContent =
        "Import-Module Pester\n. ./Helpers.ps1\n\nfunction Deploy-Module {\n    param($Name)\n    Write-Host $Name\n}";

    private static Func<string, string?> CreateFileReader(Dictionary<string, string> files)
    {
        return path =>
        {
            if (files.TryGetValue(path, out var content))
                return content;
            var normalized = StructureBuilder.NormalizePath(path);
            foreach (var kv in files)
            {
                if (StructureBuilder.NormalizePath(kv.Key) == normalized)
                    return kv.Value;
            }
            return null;
        };
    }

    // == Scenario 1: Structural hierarchy ==

    [TestMethod]
    public async Task StructuralHierarchy_CreatesPackageModuleClassFunctionNodes()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // Package node
        var packageNode = store.FindNode("/src/ast/Ast.Core");
        packageNode.Should().NotBeNull("Package node should exist");
        packageNode!.NodeType.Should().Be("Package");
        packageNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("structural");

        // Module node
        var moduleNode = store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs");
        moduleNode.Should().NotBeNull("Module node should exist");
        moduleNode!.NodeType.Should().Be("Module");

        // Class node - ID without namespace prefix
        var classNode = store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode");
        classNode.Should().NotBeNull("Class node should exist with qualified name");
        classNode!.NodeType.Should().Be("Class");
        classNode.Name.Should().Be("HypergraphNode");
        classNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("structural");
        classNode.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash);

        // Method node - type-qualified ID
        var methodNode = store.FindNode(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText");
        methodNode.Should().NotBeNull("Method node should exist with qualified name");
        methodNode!.NodeType.Should().Be("Method");
        methodNode.Name.Should().Be("GetEmbeddingText");
        methodNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("structural");
        methodNode.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash);
    }

    [TestMethod]
    public async Task StructuralHierarchy_CreatesContainsEdgesWithParentChildRoles()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // Repo -> Package
        var repoToPkg = store.FindEdge("/", "/src/ast/Ast.Core", "CONTAINS");
        repoToPkg.Should().NotBeNull("Repo -> Package CONTAINS edge should exist");
        repoToPkg!.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
        repoToPkg.Roles["/"].Should().Be("parent");
        repoToPkg.Roles["/src/ast/Ast.Core"].Should().Be("child");
        repoToPkg.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CONTAINS");
        repoToPkg.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("structural");

        // Package -> Module
        var pkgToModule = store.FindEdge(
            "/src/ast/Ast.Core",
            "/src/ast/Ast.Core/HypergraphModels.cs",
            "CONTAINS");
        pkgToModule.Should().NotBeNull("Package -> Module CONTAINS edge should exist");

        // Module -> Class
        var moduleToClass = store.FindEdge(
            "/src/ast/Ast.Core/HypergraphModels.cs",
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode",
            "CONTAINS");
        moduleToClass.Should().NotBeNull("Module -> Class CONTAINS edge should exist");

        // Class -> Method
        var classToMethod = store.FindEdge(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode",
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText",
            "CONTAINS");
        classToMethod.Should().NotBeNull("Class -> Method CONTAINS edge should exist");
        classToMethod!.Roles["/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode"]
            .Should().Be("parent");
        classToMethod.Roles["/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText"]
            .Should().Be("child");
    }

    [TestMethod]
    public async Task StructuralHierarchy_ContainsEdgesPreserveOriginalEdgeType()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var moduleToClass = store.FindEdge(
            "/src/ast/Ast.Core/HypergraphModels.cs",
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode",
            "CONTAINS");
        moduleToClass.Should().NotBeNull();
        moduleToClass!.Metadata[MemoryMetadataKeys.OriginalEdgeType]
            .Should().Be("ClassDefinition");

        var classToMethod = store.FindEdge(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode",
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText",
            "CONTAINS");
        classToMethod.Should().NotBeNull();
        classToMethod!.Metadata[MemoryMetadataKeys.OriginalEdgeType]
            .Should().Be("MethodDefinition");
    }

    // == Scenario 2: External boundary node ==

    [TestMethod]
    public async Task ExternalBoundaryNode_CreatedForExternalUsingDirective()
    {
        var data = CreateExternalImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Csharp/CSharpParser.cs"] = CSharpParserFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var extNode = store.FindNode("ext:Microsoft.CodeAnalysis:*");
        extNode.Should().NotBeNull("External boundary node should exist");
        extNode!.NodeType.Should().Be("External");
        extNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("call_chain");
        extNode.Metadata.Should().NotContainKey(MemoryMetadataKeys.SinceCommit);
        extNode.Metadata.Should().NotContainKey(MemoryMetadataKeys.SinceIndex);
        extNode.Metadata.Should().NotContainKey(MemoryMetadataKeys.ContentHash);
        extNode.Metadata.Should().NotContainKey(MemoryMetadataKeys.Embedding);
    }

    [TestMethod]
    public async Task ExternalBoundaryNode_PersistedWithTimelessVersion()
    {
        var data = CreateExternalImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Csharp/CSharpParser.cs"] = CSharpParserFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var extEntry = store.UpsertedNodes.First(n => n.Node.Id == "ext:Microsoft.CodeAnalysis:*");
        extEntry.Version.Since.Should().BeNull();
        extEntry.Version.SinceIndex.Should().BeNull();
    }

    [TestMethod]
    public async Task ExternalBoundaryNode_NotCreatedForInRepoImport()
    {
        var data = CreateInRepoImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Csharp/CSharpParser.cs"] = CSharpParserFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var extNode = store.FindNode("ext:Ast.Core:*");
        extNode.Should().BeNull("In-repo import should not create External node");
    }

    [TestMethod]
    public async Task ExternalBoundaryNode_NotCreatedForChildNamespaceImport()
    {
        var data = CreateChildNamespaceImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Csharp/CSharpParser.cs"] = CSharpParserFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var extNode = store.FindNode("ext:Ast.Core.Models:*");
        extNode.Should().BeNull("Child namespace import should resolve to in-repo package");

        var depEdge = store.FindEdge(
            "/src/ast/Ast.Csharp/CSharpParser.cs",
            "/src/ast/Ast.Core",
            "DEPENDS_ON");
        depEdge.Should().NotBeNull("DEPENDS_ON for child namespace should resolve to parent package");
    }

    // == Scenario 3: DEPENDS_ON edge ==

    [TestMethod]
    public async Task DependsOnEdge_CreatedForExternalImport()
    {
        var data = CreateExternalImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Csharp/CSharpParser.cs"] = CSharpParserFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var depEdge = store.FindEdge(
            "/src/ast/Ast.Csharp/CSharpParser.cs",
            "ext:Microsoft.CodeAnalysis:*",
            "DEPENDS_ON");
        depEdge.Should().NotBeNull("DEPENDS_ON edge should exist for external import");
        depEdge!.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
        depEdge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("DEPENDS_ON");
        depEdge.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("structural");
        depEdge.Roles.Values.Should().Contain("importer");
        depEdge.Roles.Values.Should().Contain("imported");
    }

    [TestMethod]
    public async Task DependsOnEdge_CreatedForInRepoImport()
    {
        var data = CreateInRepoImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Csharp/CSharpParser.cs"] = CSharpParserFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var depEdges = store.CreatedEdges
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr)
                && sr?.ToString() == "DEPENDS_ON")
            .ToList();
        depEdges.Should().NotBeEmpty("DEPENDS_ON edge should exist for in-repo import");
        depEdges.First().Roles.Values.Should().Contain("importer");
        depEdges.First().Roles.Values.Should().Contain("imported");
    }

    [TestMethod]
    public async Task DependsOnEdge_PreservesOriginalEdgeType()
    {
        var data = CreateExternalImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Csharp/CSharpParser.cs"] = CSharpParserFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var depEdge = store.FindEdge(
            "/src/ast/Ast.Csharp/CSharpParser.cs",
            "ext:Microsoft.CodeAnalysis:*",
            "DEPENDS_ON");
        depEdge.Should().NotBeNull();
        depEdge!.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("UsingDirective");
    }

    [TestMethod]
    public async Task DependsOnEdge_UnresolvedImportDoesNotSilentlyDrop()
    {
        var nsNode = new HypergraphNode
        {
            Id = "hash_ns_unresolved",
            Name = "Some.Unknown.Namespace",
            NodeType = "namespace",
            Language = "csharp",
            FilePath = "src/ast/Ast.Csharp/CSharpParser.cs",
            Scope = "CSharpParser",
            LineStart = 5,
        };

        var data = new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode> { [nsNode.Id] = nsNode },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_unresolved",
                    EdgeType = HyperedgeType.UsingDirective,
                    NodeIds = [nsNode.Id],
                    Roles = new Dictionary<string, string> { [nsNode.Id] = "imported_namespace" },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/ast/Ast.Csharp/CSharpParser.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "Ast.Csharp",
                        ProjectPath = "src/ast/Ast.Csharp/Ast.Csharp.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/ast/Ast.Csharp/CSharpParser.cs"],
                    }
                ],
            }
        };

        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Csharp/CSharpParser.cs"] = CSharpParserFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // Should create external boundary node (not silently drop)
        var extNode = store.FindNode("ext:Some.Unknown.Namespace:*");
        extNode.Should().NotBeNull("Unresolved import should produce external boundary node");
        extNode!.NodeType.Should().Be("External");

        // Should create DEPENDS_ON edge
        var depEdge = store.FindEdge(
            "/src/ast/Ast.Csharp/CSharpParser.cs",
            "ext:Some.Unknown.Namespace:*",
            "DEPENDS_ON");
        depEdge.Should().NotBeNull("Unresolved import should produce DEPENDS_ON edge");
    }

    // == ContentHash ==

    [TestMethod]
    public async Task ContentHash_SetOnAllInRepoNodes()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var structuralNodes = store.UpsertedNodes
            .Where(n => n.Node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var layer)
                && layer?.ToString() == "structural")
            .ToList();

        structuralNodes.Should().NotBeEmpty();
        foreach (var (node, _) in structuralNodes)
        {
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash,
                $"Node {node.Id} ({node.NodeType}) should have ContentHash");
            var hash = node.Metadata[MemoryMetadataKeys.ContentHash]?.ToString();
            hash.Should().NotBeNullOrEmpty();
            hash!.Length.Should().Be(64, "SHA-256 hex string should be 64 chars");
        }
    }

    [TestMethod]
    public async Task ContentHash_TypeMemberDerivedFromSourceText()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var classNode = store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode");
        classNode.Should().NotBeNull();
        var expectedClassHash = StructureBuilder.ComputeContentHash(ClassSourceCode);
        classNode!.Metadata[MemoryMetadataKeys.ContentHash].Should().Be(expectedClassHash);

        var methodNode = store.FindNode(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText");
        methodNode.Should().NotBeNull();
        var expectedMethodHash = StructureBuilder.ComputeContentHash(MethodSourceCode);
        methodNode!.Metadata[MemoryMetadataKeys.ContentHash].Should().Be(expectedMethodHash);
    }

    [TestMethod]
    public async Task ContentHash_ModuleDerivedFromFullFileSourceText()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var moduleNode = store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs");
        moduleNode.Should().NotBeNull();
        var expectedHash = StructureBuilder.ComputeContentHash(HypergraphModelsFileContent);
        moduleNode!.Metadata[MemoryMetadataKeys.ContentHash].Should().Be(expectedHash);
    }

    [TestMethod]
    public async Task ContentHash_PackageDerivedFromConcatenatedSourceFileTexts()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var packageNode = store.FindNode("/src/ast/Ast.Core");
        packageNode.Should().NotBeNull();
        var expectedInput = "src/ast/Ast.Core/HypergraphModels.cs\n" + HypergraphModelsFileContent;
        var expectedHash = StructureBuilder.ComputeContentHash(expectedInput);
        packageNode!.Metadata[MemoryMetadataKeys.ContentHash].Should().Be(expectedHash);
    }

    [TestMethod]
    public async Task ContentHash_RepoDerivedFromConcatenatedAllFileTexts()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var repoNode = store.FindNode("/");
        repoNode.Should().NotBeNull();
        var expectedInput = "src/ast/Ast.Core/HypergraphModels.cs\n" + HypergraphModelsFileContent;
        var expectedHash = StructureBuilder.ComputeContentHash(expectedInput);
        repoNode!.Metadata[MemoryMetadataKeys.ContentHash].Should().Be(expectedHash);
    }

    [TestMethod]
    public async Task VersionRange_SetFromCurrentCommit()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var inRepoNodes = store.UpsertedNodes
            .Where(n => n.Node.NodeType != "External")
            .ToList();
        inRepoNodes.Should().NotBeEmpty();
        foreach (var (_, version) in inRepoNodes)
        {
            version.Should().Be(TestVersion);
        }
    }

    [TestMethod]
    public async Task VersionRange_DerivedFromRepositoryCommitSha()
    {
        var data = CreateHierarchyTestData();
        data.Metadata.Repository = new RepositoryMetadata
        {
            Name = "TestRepo",
            RootPath = "/",
            GitCommitSha = "deadbeef1234567890abcdef",
            Metadata = new Dictionary<string, object?> { ["CommitIndex"] = 42 },
        };
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        // Uses the public single-arg overload which derives VersionRange from the commit SHA
        await builder.BuildAsync(data, fileReader, CancellationToken.None);

        var inRepoNodes = store.UpsertedNodes
            .Where(n => n.Node.NodeType != "External")
            .ToList();
        inRepoNodes.Should().NotBeEmpty();
        foreach (var (node, version) in inRepoNodes)
        {
            version.Since.Should().Be("deadbeef1234567890abcdef",
                $"Node {node.Id} should have version derived from commit SHA");
            version.SinceIndex.Should().Be(42,
                "SinceIndex should be derived from CommitIndex in Repository.Metadata");
        }
    }

    // == Edge persistence via IGraphStore.CreateEdgeAsync ==

    [TestMethod]
    public async Task EdgesPersisted_ViaIGraphStoreCreateEdgeAsync()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // Edges should be persisted via IGraphStore.CreateEdgeAsync
        store.CreatedEdges.Should().NotBeEmpty(
            "Edges must be persisted via inherited IGraphStore.CreateEdgeAsync");
        store.CreatedEdges.Should().AllSatisfy(e =>
            e.EdgeType.Should().Be(HyperedgeType.MemoryRelation));
    }

    // == Repo node ==

    [TestMethod]
    public async Task RepoNode_AlwaysCreated()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var repoNode = store.FindNode("/");
        repoNode.Should().NotBeNull("Repo root node should always exist");
        repoNode!.NodeType.Should().Be("Repo");
        repoNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("structural");
        repoNode.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash);
    }

    // == PowerShell ModuleImport / DotSource ==

    [TestMethod]
    public async Task PowerShell_ModuleImportCreatesDependsOnEdge()
    {
        var data = CreatePowerShellModuleImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["scripts/Deploy.ps1"] = PowerShellModuleContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var extNode = store.FindNode("ext:Pester:*");
        extNode.Should().NotBeNull("External node for PowerShell module import should exist");
        extNode!.NodeType.Should().Be("External");
        extNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("call_chain");

        var depEdge = store.FindEdge("/scripts/Deploy.ps1", "ext:Pester:*", "DEPENDS_ON");
        depEdge.Should().NotBeNull("DEPENDS_ON edge for ModuleImport should exist");
        depEdge!.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("ModuleImport");
        depEdge.Roles.Values.Should().Contain("importer");
        depEdge.Roles.Values.Should().Contain("imported");
    }

    [TestMethod]
    public async Task PowerShell_DotSourceCreatesDependsOnEdge()
    {
        var data = CreatePowerShellDotSourceTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["scripts/Deploy.ps1"] = PowerShellModuleContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var extNode = store.FindNode("ext:./Helpers.ps1:*");
        extNode.Should().NotBeNull("External node for dot-source import should exist");
        extNode!.NodeType.Should().Be("External");

        var depEdge = store.FindEdge("/scripts/Deploy.ps1", "ext:./Helpers.ps1:*", "DEPENDS_ON");
        depEdge.Should().NotBeNull("DEPENDS_ON edge for DotSource should exist");
        depEdge!.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("DotSource");
    }

    [TestMethod]
    public async Task PowerShell_FunctionNodeCreatedWithContentHash()
    {
        var data = CreatePowerShellModuleImportTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["scripts/Deploy.ps1"] = PowerShellModuleContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var funcNode = store.FindNode("/scripts/Deploy.ps1::Deploy-Module");
        funcNode.Should().NotBeNull("PowerShell function node should exist");
        funcNode!.NodeType.Should().Be("Function");
        funcNode.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash);

        var moduleNode = store.FindNode("/scripts/Deploy.ps1");
        moduleNode.Should().NotBeNull();
        moduleNode!.NodeType.Should().Be("Module");
        var expectedHash = StructureBuilder.ComputeContentHash(PowerShellModuleContent);
        moduleNode.Metadata[MemoryMetadataKeys.ContentHash].Should().Be(expectedHash);
    }

    // == BuildQualifiedName unit tests ==

    [TestMethod]
    public void BuildQualifiedName_ClassInNamespace_ReturnsTypeName()
    {
        var node = new HypergraphNode
        {
            Id = "n1", Name = "HypergraphNode", NodeType = "class",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core"
        };
        StructureBuilder.BuildQualifiedName(node).Should().Be("HypergraphNode");
    }

    [TestMethod]
    public void BuildQualifiedName_MethodInClass_ReturnsTypeAndMember()
    {
        var node = new HypergraphNode
        {
            Id = "n2", Name = "GetEmbeddingText", NodeType = "method",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core.HypergraphNode"
        };
        StructureBuilder.BuildQualifiedName(node).Should().Be("HypergraphNode.GetEmbeddingText");
    }

    [TestMethod]
    public void BuildQualifiedName_NodeWithEmptyScope_ReturnsName()
    {
        var node = new HypergraphNode
        {
            Id = "n3", Name = "Deploy-Module", NodeType = "function",
            Language = "powershell", FilePath = "scripts/Deploy.ps1", Scope = ""
        };
        StructureBuilder.BuildQualifiedName(node).Should().Be("Deploy-Module");
    }

    // == ExtractNamespace unit tests ==

    [TestMethod]
    public void ExtractNamespace_ClassInNamespace_ReturnsNamespace()
    {
        var node = new HypergraphNode
        {
            Id = "n4", Name = "HypergraphNode", NodeType = "class",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core"
        };
        StructureBuilder.ExtractNamespace(node).Should().Be("Ast.Core");
    }

    [TestMethod]
    public void ExtractNamespace_MethodInClass_ReturnsNamespace()
    {
        var node = new HypergraphNode
        {
            Id = "n5", Name = "GetEmbeddingText", NodeType = "method",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core.HypergraphNode"
        };
        StructureBuilder.ExtractNamespace(node).Should().Be("Ast.Core");
    }

    [TestMethod]
    public void ExtractNamespace_NoScope_ReturnsNull()
    {
        var node = new HypergraphNode
        {
            Id = "n6", Name = "Deploy-Module", NodeType = "function",
            Language = "powershell", FilePath = "scripts/Deploy.ps1", Scope = ""
        };
        StructureBuilder.ExtractNamespace(node).Should().BeNull();
    }

    // == Synthetic edge OriginalEdgeType ==

    [TestMethod]
    public async Task SyntheticContainsEdges_HaveOriginalEdgeType()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // ALL CONTAINS edges must have OriginalEdgeType metadata
        var containsEdges = store.CreatedEdges
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr)
                && sr?.ToString() == "CONTAINS")
            .ToList();
        containsEdges.Should().NotBeEmpty();
        foreach (var edge in containsEdges)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.OriginalEdgeType,
                $"CONTAINS edge {edge.Id} must have OriginalEdgeType");
        }

        // Synthetic edges (Repo→Package, Package→Module) have "Containment"
        var repoToPkg = store.FindEdge("/", "/src/ast/Ast.Core", "CONTAINS");
        repoToPkg.Should().NotBeNull();
        repoToPkg!.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("Containment");

        var pkgToModule = store.FindEdge(
            "/src/ast/Ast.Core",
            "/src/ast/Ast.Core/HypergraphModels.cs",
            "CONTAINS");
        pkgToModule.Should().NotBeNull();
        pkgToModule!.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("Containment");
    }

    // == Namespace metadata preservation ==

    [TestMethod]
    public async Task StructuralNodes_PreserveNamespaceMetadata()
    {
        var data = CreateHierarchyTestData();
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var classNode = store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode");
        classNode.Should().NotBeNull();
        classNode!.Metadata.Should().ContainKey("Namespace");
        classNode.Metadata["Namespace"].Should().Be("Ast.Core");

        var methodNode = store.FindNode(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText");
        methodNode.Should().NotBeNull();
        methodNode!.Metadata.Should().ContainKey("Namespace");
        methodNode.Metadata["Namespace"].Should().Be("Ast.Core");
    }

    // == Containment from hyperedge roles ==

    [TestMethod]
    public async Task Containment_DerivedFromHyperedgeRoles_NotScope()
    {
        // Verify that member→parent containment uses the "container" role
        // from definition hyperedges, not scope-based heuristics
        var classNode = new HypergraphNode
        {
            Id = "hash_class_role",
            Name = "MyClass",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/MyProject/MyFile.cs",
            Scope = "MyFile.MyNamespace", // Scope has namespace info
            LineStart = 1,
            LineEnd = 20,
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = "public class MyClass { public void DoWork() { } }"
            }
        };

        var methodNode = new HypergraphNode
        {
            Id = "hash_method_role",
            Name = "DoWork",
            NodeType = "method",
            Language = "csharp",
            FilePath = "src/MyProject/MyFile.cs",
            Scope = "MyFile.MyNamespace.MyClass",
            LineStart = 5,
            LineEnd = 10,
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = "public void DoWork() { }"
            }
        };

        var data = new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode>
            {
                [classNode.Id] = classNode,
                [methodNode.Id] = methodNode,
            },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_class_def",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [classNode.Id],
                    Roles = new Dictionary<string, string> { [classNode.Id] = "class" },
                },
                new Hyperedge
                {
                    Id = "he_method_def",
                    EdgeType = HyperedgeType.MethodDefinition,
                    NodeIds = [classNode.Id, methodNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [classNode.Id] = "container",
                        [methodNode.Id] = "defined_method",
                    },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/MyProject/MyFile.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "MyProject",
                        ProjectPath = "src/MyProject/MyProject.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/MyProject/MyFile.cs"],
                    }
                ],
            }
        };

        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/MyProject/MyFile.cs"] = "public class MyClass { public void DoWork() { } }"
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // Method is contained by MyClass, derived from hyperedge "container" role
        var containsEdge = store.FindEdge(
            "/src/MyProject/MyFile.cs::MyClass",
            "/src/MyProject/MyFile.cs::MyClass.DoWork",
            "CONTAINS");
        containsEdge.Should().NotBeNull(
            "CONTAINS edge should be created from hyperedge container role");
        containsEdge!.Metadata[MemoryMetadataKeys.OriginalEdgeType]
            .Should().Be("MethodDefinition");
    }

    // == VersionRange with explicit CommitIndex ==

    [TestMethod]
    public async Task VersionRange_UsesCommitIndexFromMetadata()
    {
        var data = CreateHierarchyTestData();
        data.Metadata.Repository = new RepositoryMetadata
        {
            Name = "TestRepo",
            RootPath = "/",
            GitCommitSha = "abc123def456",
            Metadata = new Dictionary<string, object?> { ["CommitIndex"] = 7 },
        };
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, fileReader, CancellationToken.None);

        var node = store.UpsertedNodes.First(n => n.Node.NodeType != "External");
        node.Version.Since.Should().Be("abc123def456");
        node.Version.SinceIndex.Should().Be(7, "CommitIndex from metadata should be used");
    }

    // == Parser output compatibility — Ast.Csharp pattern ==

    [TestMethod]
    public async Task Compatibility_CSharpParserOutput_ProcessesCorrectly()
    {
        // Mimics realistic Ast.Csharp parser output: namespace, class with
        // methods/properties, using directives (internal + external)
        var nsNode = new HypergraphNode
        {
            Id = "ns_001",
            Name = "Ast.Core",
            NodeType = "namespace",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels",
            LineStart = 7,
        };

        var classNode = new HypergraphNode
        {
            Id = "cls_001",
            Name = "HypergraphNode",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core",
            LineStart = 10,
            LineEnd = 23,
            Modifiers = ["public"],
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = ClassSourceCode,
            }
        };

        var methodNode = new HypergraphNode
        {
            Id = "mth_001",
            Name = "GetEmbeddingText",
            NodeType = "method",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core.HypergraphNode",
            LineStart = 18,
            LineEnd = 22,
            Modifiers = ["public"],
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = MethodSourceCode,
            }
        };

        var propNode = new HypergraphNode
        {
            Id = "prp_001",
            Name = "Id",
            NodeType = "property",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core.HypergraphNode",
            LineStart = 12,
            LineEnd = 12,
            Modifiers = ["public", "required"],
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = "public required string Id { get; init; }",
            }
        };

        var usingNsNode = new HypergraphNode
        {
            Id = "using_001",
            Name = "System.Collections.Generic",
            NodeType = "namespace",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "",
            LineStart = 1,
        };

        var data = new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode>
            {
                [nsNode.Id] = nsNode,
                [classNode.Id] = classNode,
                [methodNode.Id] = methodNode,
                [propNode.Id] = propNode,
                [usingNsNode.Id] = usingNsNode,
            },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_cls_001",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [classNode.Id],
                    Roles = new Dictionary<string, string> { [classNode.Id] = "class" },
                },
                new Hyperedge
                {
                    Id = "he_mth_001",
                    EdgeType = HyperedgeType.MethodDefinition,
                    NodeIds = [classNode.Id, methodNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [classNode.Id] = "container",
                        [methodNode.Id] = "defined_method",
                    },
                },
                new Hyperedge
                {
                    Id = "he_prp_001",
                    EdgeType = HyperedgeType.PropertyDefinition,
                    NodeIds = [classNode.Id, propNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [classNode.Id] = "container",
                        [propNode.Id] = "defined_property",
                    },
                },
                new Hyperedge
                {
                    Id = "he_using_001",
                    EdgeType = HyperedgeType.UsingDirective,
                    NodeIds = [usingNsNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [usingNsNode.Id] = "imported_namespace",
                    },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/ast/Ast.Core/HypergraphModels.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "Ast.Core",
                        ProjectPath = "src/ast/Ast.Core/Ast.Core.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/ast/Ast.Core/HypergraphModels.cs"],
                    }
                ],
                Repository = new RepositoryMetadata
                {
                    Name = "TestRepo",
                    RootPath = "/",
                    GitCommitSha = "abc123",
                    Metadata = new Dictionary<string, object?> { ["CommitIndex"] = 5 },
                },
            }
        };

        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, fileReader, CancellationToken.None);

        // Verify structural hierarchy: Repo → Package → Module → Class → Method/Property
        store.FindNode("/").Should().NotBeNull();
        store.FindNode("/src/ast/Ast.Core").Should().NotBeNull();
        store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs").Should().NotBeNull();
        store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode").Should().NotBeNull();
        store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText").Should().NotBeNull();
        store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.Id").Should().NotBeNull();

        // Verify containment from hyperedge roles
        store.FindEdge(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode",
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText",
            "CONTAINS").Should().NotBeNull("Method contained by class via hyperedge role");

        store.FindEdge(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode",
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.Id",
            "CONTAINS").Should().NotBeNull("Property contained by class via hyperedge role");

        // Verify external node for System.Collections.Generic
        var extNode = store.FindNode("ext:System.Collections.Generic:*");
        extNode.Should().NotBeNull("External boundary node for using directive");
        extNode!.NodeType.Should().Be("External");
        extNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("call_chain");

        // Verify VersionRange from commit
        var inRepoNodes = store.UpsertedNodes
            .Where(n => n.Node.NodeType != "External")
            .ToList();
        foreach (var (node, version) in inRepoNodes)
        {
            version.Since.Should().Be("abc123");
            version.SinceIndex.Should().Be(5);
        }

        // Verify all CONTAINS edges have OriginalEdgeType
        var containsEdges = store.CreatedEdges
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr)
                && sr?.ToString() == "CONTAINS")
            .ToList();
        foreach (var edge in containsEdges)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.OriginalEdgeType);
        }

        // Verify namespace metadata preserved
        var cls = store.FindNode("/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode");
        cls!.Metadata.Should().ContainKey("Namespace");
        cls.Metadata["Namespace"].Should().Be("Ast.Core");
    }

    // == PowerShell parser output compatibility ==

    [TestMethod]
    public async Task Compatibility_PowerShellParserOutput_ProcessesCorrectly()
    {
        // Mimics realistic Ast.PowerShell parser output: function defs,
        // module imports, dot-source imports
        var funcNode = new HypergraphNode
        {
            Id = "ps_func_001",
            Name = "Deploy-Module",
            NodeType = "function",
            Language = "powershell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "",
            LineStart = 4,
            LineEnd = 7,
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = "function Deploy-Module {\n    param($Name)\n    Write-Host $Name\n}",
            }
        };

        var moduleImportNode = new HypergraphNode
        {
            Id = "ps_import_001",
            Name = "Pester",
            NodeType = "module",
            Language = "powershell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "",
            LineStart = 1,
        };

        var dotSourceNode = new HypergraphNode
        {
            Id = "ps_dotsrc_001",
            Name = "./Helpers.ps1",
            NodeType = "script",
            Language = "powershell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "",
            LineStart = 2,
        };

        var data = new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode>
            {
                [funcNode.Id] = funcNode,
                [moduleImportNode.Id] = moduleImportNode,
                [dotSourceNode.Id] = dotSourceNode,
            },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_ps_func",
                    EdgeType = HyperedgeType.FunctionDefinition,
                    NodeIds = [funcNode.Id],
                    Roles = new Dictionary<string, string> { [funcNode.Id] = "function" },
                },
                new Hyperedge
                {
                    Id = "he_ps_import",
                    EdgeType = HyperedgeType.ModuleImport,
                    NodeIds = [moduleImportNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [moduleImportNode.Id] = "imported_module",
                    },
                },
                new Hyperedge
                {
                    Id = "he_ps_dotsrc",
                    EdgeType = HyperedgeType.DotSource,
                    NodeIds = [dotSourceNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [dotSourceNode.Id] = "dot_source_file",
                    },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["scripts/Deploy.ps1"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "DeployScripts",
                        ProjectPath = "scripts/scripts.psd1",
                        ProjectType = ProjectType.PowerShellModule,
                        Language = "powershell",
                        SourceFiles = ["scripts/Deploy.ps1"],
                    }
                ],
            }
        };

        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["scripts/Deploy.ps1"] = PowerShellModuleContent
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // Function node created at module level
        var func = store.FindNode("/scripts/Deploy.ps1::Deploy-Module");
        func.Should().NotBeNull();
        func!.NodeType.Should().Be("Function");

        // External nodes for imports
        store.FindNode("ext:Pester:*").Should().NotBeNull();
        store.FindNode("ext:./Helpers.ps1:*").Should().NotBeNull();

        // DEPENDS_ON edges
        store.FindEdge("/scripts/Deploy.ps1", "ext:Pester:*", "DEPENDS_ON").Should().NotBeNull();
        store.FindEdge("/scripts/Deploy.ps1", "ext:./Helpers.ps1:*", "DEPENDS_ON").Should().NotBeNull();
    }

    [TestMethod]
    public void NormalizePath_BackslashToForwardSlash()
    {
        StructureBuilder.NormalizePath(@"src\ast\Ast.Core\File.cs")
            .Should().Be("src/ast/Ast.Core/File.cs");
    }

    [TestMethod]
    public void NormalizePath_StripsLeadingSlash()
    {
        StructureBuilder.NormalizePath("/src/ast/File.cs")
            .Should().Be("src/ast/File.cs");
    }

    [TestMethod]
    public void ExtractSourceByLineRange_ValidRange()
    {
        var content = "line1\nline2\nline3\nline4\nline5";
        var result = StructureBuilder.ExtractSourceByLineRange(content, 2, 4);
        result.Should().Be("line2\nline3\nline4");
    }

    [TestMethod]
    public void ExtractSourceByLineRange_NullContent_ReturnsNull()
    {
        StructureBuilder.ExtractSourceByLineRange(null, 1, 5).Should().BeNull();
    }

    [TestMethod]
    public void ExtractSourceByLineRange_InvalidRange_ReturnsNull()
    {
        var content = "line1\nline2";
        StructureBuilder.ExtractSourceByLineRange(content, 0, 5).Should().BeNull();
        StructureBuilder.ExtractSourceByLineRange(content, 3, 1).Should().BeNull();
    }

    // == ResolveDotSegments unit tests ==

    [TestMethod]
    public void ResolveDotSegments_CollapsesDotSlash()
    {
        StructureBuilder.ResolveDotSegments("scripts/./Helpers.ps1")
            .Should().Be("scripts/Helpers.ps1");
    }

    [TestMethod]
    public void ResolveDotSegments_CollapsesDotDotSlash()
    {
        StructureBuilder.ResolveDotSegments("scripts/sub/../Helpers.ps1")
            .Should().Be("scripts/Helpers.ps1");
    }

    // == VersionRange without repository metadata ==

    [TestMethod]
    public async Task VersionRange_SetEvenWithoutRepositoryMetadata()
    {
        var data = CreateHierarchyTestData();
        data.Metadata.Repository = null;
        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/ast/Ast.Core/HypergraphModels.cs"] = HypergraphModelsFileContent
        });

        await builder.BuildAsync(data, fileReader, CancellationToken.None);

        var inRepoNodes = store.UpsertedNodes
            .Where(n => n.Node.NodeType != "External")
            .ToList();
        inRepoNodes.Should().NotBeEmpty();
        foreach (var (node, version) in inRepoNodes)
        {
            version.SinceIndex.Should().Be(0,
                "In-repo nodes should have SinceIndex=0 even without git metadata");

            // Node metadata must also carry SinceIndex=0
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.SinceIndex,
                $"Node {node.Id} metadata should have SinceIndex even without git commit SHA");
            node.Metadata[MemoryMetadataKeys.SinceIndex].Should().Be(0);
        }
    }

    // == ContentHash guaranteed even without source text ==

    [TestMethod]
    public async Task ContentHash_GuaranteedOnAllInRepoNodes_EvenWithoutSourceText()
    {
        // Create data where the file reader returns null for all files
        var classNode = new HypergraphNode
        {
            Id = "hash_no_source_class",
            Name = "NoSourceClass",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/NoSource/File.cs",
            Scope = "",
            LineStart = 1,
            LineEnd = 5,
        };

        var data = new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode> { [classNode.Id] = classNode },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_no_source",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [classNode.Id],
                    Roles = new Dictionary<string, string> { [classNode.Id] = "class" },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/NoSource/File.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "NoSource",
                        ProjectPath = "src/NoSource/NoSource.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/NoSource/File.cs"],
                    }
                ],
            }
        };

        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        Func<string, string?> fileReader = _ => null;

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        var structuralNodes = store.UpsertedNodes
            .Where(n => n.Node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var layer)
                && layer?.ToString() == "structural")
            .ToList();

        structuralNodes.Should().NotBeEmpty();
        foreach (var (node, _) in structuralNodes)
        {
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash,
                $"Node {node.Id} ({node.NodeType}) must have ContentHash even without source");
            var hash = node.Metadata[MemoryMetadataKeys.ContentHash]?.ToString();
            hash.Should().NotBeNullOrEmpty();
            hash!.Length.Should().Be(64, "SHA-256 hex string should be 64 chars");
        }
    }

    // == DotSource in-repo resolution ==

    [TestMethod]
    public async Task PowerShell_DotSourceResolvesToInRepoModule()
    {
        var dotSourceNode = new HypergraphNode
        {
            Id = "ps_dotsrc_inrepo",
            Name = "./Helpers.ps1",
            NodeType = "script",
            Language = "powershell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "",
            LineStart = 2,
        };

        var data = new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode> { [dotSourceNode.Id] = dotSourceNode },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_dotsrc_inrepo",
                    EdgeType = HyperedgeType.DotSource,
                    NodeIds = [dotSourceNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [dotSourceNode.Id] = "dot_source_file",
                    },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["scripts/Deploy.ps1", "scripts/Helpers.ps1"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "DeployScripts",
                        ProjectPath = "scripts/scripts.psd1",
                        ProjectType = ProjectType.PowerShellModule,
                        Language = "powershell",
                        SourceFiles = ["scripts/Deploy.ps1", "scripts/Helpers.ps1"],
                    }
                ],
            }
        };

        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["scripts/Deploy.ps1"] = PowerShellModuleContent,
            ["scripts/Helpers.ps1"] = "function Get-Helper { param() Write-Host 'help' }"
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        store.FindNode("ext:./Helpers.ps1:*").Should().BeNull(
            "In-repo dot-sourced file should NOT create External node");

        var depEdge = store.FindEdge(
            "/scripts/Deploy.ps1", "/scripts/Helpers.ps1", "DEPENDS_ON");
        depEdge.Should().NotBeNull("DotSource should resolve to in-repo module");
        depEdge!.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("DotSource");
    }

    [TestMethod]
    public async Task PowerShell_DotSourceWithParentDirResolvesToInRepoModule()
    {
        var dotSourceNode = new HypergraphNode
        {
            Id = "ps_dotsrc_parent",
            Name = "../lib/Helpers.ps1",
            NodeType = "script",
            Language = "powershell",
            FilePath = "scripts/sub/Runner.ps1",
            Scope = "",
            LineStart = 1,
        };

        var data = new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode> { [dotSourceNode.Id] = dotSourceNode },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_dotsrc_parent",
                    EdgeType = HyperedgeType.DotSource,
                    NodeIds = [dotSourceNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [dotSourceNode.Id] = "dot_source_file",
                    },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["scripts/sub/Runner.ps1", "scripts/lib/Helpers.ps1"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "Scripts",
                        ProjectPath = "scripts/scripts.psd1",
                        ProjectType = ProjectType.PowerShellModule,
                        Language = "powershell",
                        SourceFiles = ["scripts/sub/Runner.ps1", "scripts/lib/Helpers.ps1"],
                    }
                ],
            }
        };

        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["scripts/sub/Runner.ps1"] = ". ../lib/Helpers.ps1\nWrite-Host 'run'",
            ["scripts/lib/Helpers.ps1"] = "function Get-Helper { Write-Host 'help' }"
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // Should resolve to in-repo module, not create external node
        store.FindNode("ext:../lib/Helpers.ps1:*").Should().BeNull(
            "DotSource with ../ should resolve to in-repo module, not external");

        var depEdge = store.FindEdge(
            "/scripts/sub/Runner.ps1", "/scripts/lib/Helpers.ps1", "DEPENDS_ON");
        depEdge.Should().NotBeNull("DotSource with ../ should resolve to in-repo module");
        depEdge!.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("DotSource");
    }

    // == Nested type topological ordering ==

    [TestMethod]
    public async Task NestedTypes_ProcessedInCorrectOrder()
    {
        // Insert inner type BEFORE outer type in the dictionary to force
        // ordering issues if topological sort is not used.
        var innerClass = new HypergraphNode
        {
            Id = "hash_inner",
            Name = "InnerClass",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/Proj/File.cs",
            Scope = "",
            LineStart = 5,
            LineEnd = 10,
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = "public class InnerClass { }"
            }
        };

        var outerClass = new HypergraphNode
        {
            Id = "hash_outer",
            Name = "OuterClass",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/Proj/File.cs",
            Scope = "",
            LineStart = 1,
            LineEnd = 15,
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = "public class OuterClass { public class InnerClass { } }"
            }
        };

        // Deliberately insert inner first
        var nodes = new Dictionary<string, HypergraphNode>
        {
            [innerClass.Id] = innerClass,
            [outerClass.Id] = outerClass,
        };

        var data = new HypergraphData
        {
            Nodes = nodes,
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_outer",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [outerClass.Id],
                    Roles = new Dictionary<string, string> { [outerClass.Id] = "class" },
                },
                new Hyperedge
                {
                    Id = "he_inner",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [outerClass.Id, innerClass.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [outerClass.Id] = "container",
                        [innerClass.Id] = "class",
                    },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/Proj/File.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "Proj",
                        ProjectPath = "src/Proj/Proj.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/Proj/File.cs"],
                    }
                ],
            }
        };

        var store = new TrackingMemoryGraphStore();
        var builder = new StructureBuilder(store);
        var fileReader = CreateFileReader(new Dictionary<string, string>
        {
            ["src/Proj/File.cs"] = "public class OuterClass { public class InnerClass { } }"
        });

        await builder.BuildAsync(data, TestVersion, fileReader, CancellationToken.None);

        // InnerClass should have OuterClass as its parent in the ID
        var innerNode = store.FindNode("/src/Proj/File.cs::OuterClass.InnerClass");
        innerNode.Should().NotBeNull("Nested type should have qualified name OuterClass.InnerClass");
        innerNode!.NodeType.Should().Be("Class");

        // CONTAINS edge from outer to inner
        var containsEdge = store.FindEdge(
            "/src/Proj/File.cs::OuterClass",
            "/src/Proj/File.cs::OuterClass.InnerClass",
            "CONTAINS");
        containsEdge.Should().NotBeNull("CONTAINS edge should exist from outer to inner class");
    }

    // == Test data builders ==

    private static HypergraphData CreateHierarchyTestData()
    {
        var classNode = new HypergraphNode
        {
            Id = "hash_class_001",
            Name = "HypergraphNode",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core",
            LineStart = 10,
            LineEnd = 23,
            Modifiers = ["public"],
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = ClassSourceCode,
            }
        };

        var methodNode = new HypergraphNode
        {
            Id = "hash_method_001",
            Name = "GetEmbeddingText",
            NodeType = "method",
            Language = "csharp",
            FilePath = "src/ast/Ast.Core/HypergraphModels.cs",
            Scope = "HypergraphModels.Ast.Core.HypergraphNode",
            LineStart = 18,
            LineEnd = 22,
            Modifiers = ["public"],
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = MethodSourceCode,
            }
        };

        return new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode>
            {
                [classNode.Id] = classNode,
                [methodNode.Id] = methodNode,
            },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_0001",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [classNode.Id],
                    Roles = new Dictionary<string, string> { [classNode.Id] = "class" },
                },
                new Hyperedge
                {
                    Id = "he_0002",
                    EdgeType = HyperedgeType.MethodDefinition,
                    NodeIds = [classNode.Id, methodNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [classNode.Id] = "container",
                        [methodNode.Id] = "defined_method",
                    },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/ast/Ast.Core/HypergraphModels.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "Ast.Core",
                        ProjectPath = "src/ast/Ast.Core/Ast.Core.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/ast/Ast.Core/HypergraphModels.cs"],
                    }
                ],
            }
        };
    }

    private static HypergraphData CreateExternalImportTestData()
    {
        var nsNode = new HypergraphNode
        {
            Id = "hash_ns_001",
            Name = "Microsoft.CodeAnalysis",
            NodeType = "namespace",
            Language = "csharp",
            FilePath = "src/ast/Ast.Csharp/CSharpParser.cs",
            Scope = "CSharpParser",
            LineStart = 1,
        };

        return new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode> { [nsNode.Id] = nsNode },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_0001",
                    EdgeType = HyperedgeType.UsingDirective,
                    NodeIds = [nsNode.Id],
                    Roles = new Dictionary<string, string> { [nsNode.Id] = "imported_namespace" },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/ast/Ast.Csharp/CSharpParser.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "Ast.Csharp",
                        ProjectPath = "src/ast/Ast.Csharp/Ast.Csharp.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/ast/Ast.Csharp/CSharpParser.cs"],
                    }
                ],
            }
        };
    }

    private static HypergraphData CreateInRepoImportTestData()
    {
        var nsNode = new HypergraphNode
        {
            Id = "hash_ns_002",
            Name = "Ast.Core",
            NodeType = "namespace",
            Language = "csharp",
            FilePath = "src/ast/Ast.Csharp/CSharpParser.cs",
            Scope = "CSharpParser",
            LineStart = 2,
        };

        return new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode> { [nsNode.Id] = nsNode },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_0001",
                    EdgeType = HyperedgeType.UsingDirective,
                    NodeIds = [nsNode.Id],
                    Roles = new Dictionary<string, string> { [nsNode.Id] = "imported_namespace" },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/ast/Ast.Csharp/CSharpParser.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "Ast.Csharp",
                        ProjectPath = "src/ast/Ast.Csharp/Ast.Csharp.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/ast/Ast.Csharp/CSharpParser.cs"],
                    },
                    new ProjectMetadata
                    {
                        Name = "Ast.Core",
                        ProjectPath = "src/ast/Ast.Core/Ast.Core.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = [],
                    }
                ],
            }
        };
    }

    private static HypergraphData CreateChildNamespaceImportTestData()
    {
        var nsNode = new HypergraphNode
        {
            Id = "hash_ns_003",
            Name = "Ast.Core.Models",
            NodeType = "namespace",
            Language = "csharp",
            FilePath = "src/ast/Ast.Csharp/CSharpParser.cs",
            Scope = "CSharpParser",
            LineStart = 3,
        };

        return new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode> { [nsNode.Id] = nsNode },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_0001",
                    EdgeType = HyperedgeType.UsingDirective,
                    NodeIds = [nsNode.Id],
                    Roles = new Dictionary<string, string> { [nsNode.Id] = "imported_namespace" },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["src/ast/Ast.Csharp/CSharpParser.cs"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "Ast.Csharp",
                        ProjectPath = "src/ast/Ast.Csharp/Ast.Csharp.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = ["src/ast/Ast.Csharp/CSharpParser.cs"],
                    },
                    new ProjectMetadata
                    {
                        Name = "Ast.Core",
                        ProjectPath = "src/ast/Ast.Core/Ast.Core.csproj",
                        ProjectType = ProjectType.CSharpLibrary,
                        Language = "csharp",
                        SourceFiles = [],
                    }
                ],
            }
        };
    }

    private static HypergraphData CreatePowerShellModuleImportTestData()
    {
        var moduleNode = new HypergraphNode
        {
            Id = "hash_ps_module_001",
            Name = "Pester",
            NodeType = "module",
            Language = "powershell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "",
            LineStart = 1,
        };

        var funcNode = new HypergraphNode
        {
            Id = "hash_ps_func_001",
            Name = "Deploy-Module",
            NodeType = "function",
            Language = "powershell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "",
            LineStart = 4,
            LineEnd = 7,
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = "function Deploy-Module {\n    param($Name)\n    Write-Host $Name\n}",
            }
        };

        return new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode>
            {
                [moduleNode.Id] = moduleNode,
                [funcNode.Id] = funcNode,
            },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_ps_import_001",
                    EdgeType = HyperedgeType.ModuleImport,
                    NodeIds = [moduleNode.Id],
                    Roles = new Dictionary<string, string> { [moduleNode.Id] = "imported_module" },
                },
                new Hyperedge
                {
                    Id = "he_ps_func_001",
                    EdgeType = HyperedgeType.FunctionDefinition,
                    NodeIds = [funcNode.Id],
                    Roles = new Dictionary<string, string> { [funcNode.Id] = "function" },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["scripts/Deploy.ps1"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "DeployScripts",
                        ProjectPath = "scripts/scripts.psd1",
                        ProjectType = ProjectType.PowerShellModule,
                        Language = "powershell",
                        SourceFiles = ["scripts/Deploy.ps1"],
                    }
                ],
            }
        };
    }

    private static HypergraphData CreatePowerShellDotSourceTestData()
    {
        var dotSourceNode = new HypergraphNode
        {
            Id = "hash_ps_dotsource_001",
            Name = "./Helpers.ps1",
            NodeType = "script",
            Language = "powershell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "",
            LineStart = 2,
        };

        return new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode> { [dotSourceNode.Id] = dotSourceNode },
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "he_ps_dotsource_001",
                    EdgeType = HyperedgeType.DotSource,
                    NodeIds = [dotSourceNode.Id],
                    Roles = new Dictionary<string, string> { [dotSourceNode.Id] = "dot_source_file" },
                },
            ],
            Metadata = new HypergraphMetadata
            {
                Files = ["scripts/Deploy.ps1"],
                Projects =
                [
                    new ProjectMetadata
                    {
                        Name = "DeployScripts",
                        ProjectPath = "scripts/scripts.psd1",
                        ProjectType = ProjectType.PowerShellModule,
                        Language = "powershell",
                        SourceFiles = ["scripts/Deploy.ps1"],
                    }
                ],
            }
        };
    }
}

/// <summary>
/// Test double for <see cref="IMemoryGraphStore"/> that tracks all persisted nodes and edges.
/// Implements <see cref="IGraphStore.CreateEdgeAsync"/> (inherited from <see cref="IGraphStore"/>)
/// and <see cref="IMemoryGraphStore.UpsertNodeAsync"/> / <see cref="IMemoryGraphStore.UpsertEdgeAsync"/>.
/// </summary>
internal class TrackingMemoryGraphStore : IMemoryGraphStore
{
    public List<(HypergraphNode Node, VersionRange Version)> UpsertedNodes { get; } = [];
    public List<Hyperedge> CreatedEdges { get; } = [];

    public HypergraphNode? FindNode(string id) =>
        this.UpsertedNodes.FirstOrDefault(n => n.Node.Id == id).Node;

    public Hyperedge? FindEdge(string fromId, string toId, string semanticRelation) =>
        this.CreatedEdges
            .FirstOrDefault(e =>
                e.NodeIds.Contains(fromId) &&
                e.NodeIds.Contains(toId) &&
                e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr) &&
                sr?.ToString() == semanticRelation);

    // ── IGraphStore.CreateEdgeAsync (inherited from IGraphStore) ─────────

    /// <summary>
    /// Implements <see cref="IGraphStore.CreateEdgeAsync"/>.
    /// This is the method the StructureBuilder calls to persist edges.
    /// </summary>
    public Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken)
    {
        this.CreatedEdges.Add(edge);
        return Task.CompletedTask;
    }

    // ── IMemoryGraphStore node/edge mutations ───────────────────────────

    public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken cancellationToken)
    {
        this.UpsertedNodes.Add((node, version));
        return Task.CompletedTask;
    }

    public Task UpsertEdgeAsync(Hyperedge edge, VersionRange version, CancellationToken cancellationToken)
    {
        this.CreatedEdges.Add(edge);
        return Task.CompletedTask;
    }

    // ── Remaining IMemoryGraphStore members (not used in these tests) ───

    public Task<IReadOnlyList<MemoryNode>> GetNodesByLayerAsync(
        string memoryLayer, int? atCommitIndex, NodeFilter? filter, int limit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<MemoryEdge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation, string? memoryLayer, int? atCommitIndex, int limit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, string? untilCommit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, string? untilCommit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> metadata, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId, double minConfidence, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId, int limit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
