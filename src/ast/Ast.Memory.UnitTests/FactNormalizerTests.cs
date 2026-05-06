// -----------------------------------------------------------------------
// <copyright file="FactNormalizerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System.Collections.Generic;
using Ast.Core;
using Ast.Memory.Extraction;
using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class FactNormalizerTests
{
    private readonly FactNormalizer normalizer;
    private const string RepoRoot = "C:/repo";

    public FactNormalizerTests()
    {
        this.normalizer = new FactNormalizer(NullLogger<FactNormalizer>.Instance);
    }

    [TestMethod]
    public void Normalize_AssignsCanonicalIds_ToFileNodes()
    {
        var data = new HypergraphData();
        data.Nodes["old-file-id"] = new HypergraphNode
        {
            Id = "old-file-id",
            Name = "PaymentService.cs",
            NodeType = "File",
            Language = "C#",
            FilePath = "src/Services/PaymentService.cs",
        };

        var result = this.normalizer.Normalize(data, RepoRoot);

        data.Nodes.Should().ContainKey("/src/Services/PaymentService.cs");
        result.NodesNormalized.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Normalize_AssignsCanonicalIds_ToSymbolNodes()
    {
        var data = new HypergraphData();
        data.Nodes["old-class-id"] = new HypergraphNode
        {
            Id = "old-class-id",
            Name = "PaymentService",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Services/PaymentService.cs",
            Scope = "MyApp.Services",
        };

        this.normalizer.Normalize(data, RepoRoot);

        data.Nodes.Should().ContainKey("/src/Services/PaymentService.cs::MyApp.Services.PaymentService");
    }

    [TestMethod]
    public void Normalize_MergesCrossLanguageDuplicates()
    {
        var data = new HypergraphData();

        // C# definition in its own file
        data.Nodes["cs-payment"] = new HypergraphNode
        {
            Id = "cs-payment",
            Name = "PaymentService",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Services/PaymentService.cs",
            Scope = "MyApp.Services",
        };

        // PowerShell reference in a DIFFERENT file — cross-language dedup must still merge
        data.Nodes["ps-payment"] = new HypergraphNode
        {
            Id = "ps-payment",
            Name = "PaymentService",
            NodeType = "Class",
            Language = "PowerShell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "MyApp.Services",
        };

        // A PS function node that is the caller
        data.Nodes["ps-func"] = new HypergraphNode
        {
            Id = "ps-func",
            Name = "Invoke-Deploy",
            NodeType = "Function",
            Language = "PowerShell",
            FilePath = "scripts/Deploy.ps1",
            Scope = string.Empty,
        };

        // Edge from PS function to the PS reference (cross-language call)
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "edge-cross",
            EdgeType = HyperedgeType.CrossLanguageMethodCall,
            NodeIds = new List<string> { "ps-func", "ps-payment" },
            Roles = new Dictionary<string, string>
            {
                { "ps-func", "caller" },
                { "ps-payment", "callee" },
            },
        });

        // Also add a C#-internal containment edge to verify C# edges survive
        data.Nodes["cs-file"] = new HypergraphNode
        {
            Id = "cs-file",
            Name = "PaymentService.cs",
            NodeType = "File",
            Language = "C#",
            FilePath = "src/Services/PaymentService.cs",
        };
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "edge-contains",
            EdgeType = HyperedgeType.ClassDefinition,
            NodeIds = new List<string> { "cs-file", "cs-payment" },
            Roles = new Dictionary<string, string>
            {
                { "cs-file", "parent" },
                { "cs-payment", "child" },
            },
        });

        var result = this.normalizer.Normalize(data, RepoRoot);

        result.DuplicatesMerged.Should().BeGreaterThan(0,
            "cross-language duplicates with same QualifiedName should merge even with different file paths");

        // Only one canonical node should exist for PaymentService
        var paymentNodes = data.Nodes.Values
            .Where(n => n.Name == "PaymentService" && n.NodeType == "Class")
            .ToList();
        paymentNodes.Should().HaveCount(1, "exactly one canonical PaymentService node should exist");

        // C# definition should win over PowerShell reference
        paymentNodes[0].Language.Should().Be("C#");
        var canonicalPaymentId = paymentNodes[0].Id;

        // The cross-language CALLS edge should be preserved, pointing to the canonical node
        var callsEdges = data.Hyperedges
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var r) && r?.ToString() == "CALLS")
            .ToList();
        callsEdges.Should().NotBeEmpty("cross-language CALLS edge should survive the merge");
        callsEdges.Should().Contain(e => e.NodeIds.Contains(canonicalPaymentId),
            "CALLS edge callee should point to the canonical PaymentService node");

        // The C# containment edge should also be preserved
        var containsEdges = data.Hyperedges
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var r) && r?.ToString() == "CONTAINS")
            .ToList();
        containsEdges.Should().NotBeEmpty("C# CONTAINS edge should survive the merge");
        containsEdges.Should().Contain(e => e.NodeIds.Contains(canonicalPaymentId),
            "CONTAINS edge should point to the canonical PaymentService node");

        // Both languages' edges survive: CALLS (from PS) and CONTAINS (from C#)
        data.Hyperedges.Count.Should().BeGreaterThanOrEqualTo(2,
            "edges from both languages should be preserved after cross-language merge");
    }

    [TestMethod]
    public void Normalize_MergesCrossLanguageDuplicates_WithLowercaseExtractorOutput()
    {
        var data = new HypergraphData();

        // C# class with lowercase nodeType from extractor
        data.Nodes["cs-svc"] = new HypergraphNode
        {
            Id = "cs-svc",
            Name = "PaymentService",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/Services/PaymentService.cs",
            Scope = "MyApp.Services",
        };

        // PowerShell reference from different file with lowercase nodeType
        data.Nodes["ps-svc"] = new HypergraphNode
        {
            Id = "ps-svc",
            Name = "PaymentService",
            NodeType = "class",
            Language = "powershell",
            FilePath = "scripts/Invoke-Payment.ps1",
            Scope = "MyApp.Services",
        };

        var result = this.normalizer.Normalize(data, RepoRoot);

        result.DuplicatesMerged.Should().BeGreaterThan(0);

        // After normalization, NodeType should be PascalCase
        var paymentNodes = data.Nodes.Values
            .Where(n => n.Name == "PaymentService")
            .ToList();
        paymentNodes.Should().HaveCount(1);
        paymentNodes[0].NodeType.Should().Be("Class");
        paymentNodes[0].Language.Should().Be("C#");
    }

    [TestMethod]
    public void Normalize_StampsMetadata_QualifiedName_ContentHash_MemoryLayer()
    {
        var data = new HypergraphData();
        data.Nodes["my-class"] = new HypergraphNode
        {
            Id = "my-class",
            Name = "Foo",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Foo.cs",
            Scope = "MyApp",
        };

        this.normalizer.Normalize(data, RepoRoot);

        var node = data.Nodes.Values.First(n => n.Name == "Foo" && n.NodeType == "Class");
        node.Metadata.Should().ContainKey(MemoryMetadataKeys.QualifiedName);
        node.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash);
        node.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer);
    }

    [TestMethod]
    public void Normalize_RewritesEdgeReferences()
    {
        var data = new HypergraphData();
        data.Nodes["old-a"] = new HypergraphNode
        {
            Id = "old-a",
            Name = "A",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/A.cs",
            Scope = "MyApp",
        };
        data.Nodes["old-b"] = new HypergraphNode
        {
            Id = "old-b",
            Name = "B",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/B.cs",
            Scope = "MyApp",
        };
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "edge-1",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "old-a", "old-b" },
            Roles = new Dictionary<string, string>
            {
                { "old-a", "caller" },
                { "old-b", "callee" },
            },
        });

        this.normalizer.Normalize(data, RepoRoot);

        // Edges should now reference canonical IDs
        data.Hyperedges.Should().NotBeEmpty();
        var edge = data.Hyperedges[0];
        edge.NodeIds.Should().AllSatisfy(id => id.Should().StartWith("/"));
    }

    [TestMethod]
    public void Normalize_RemovesSelfReferencingEdges()
    {
        var data = new HypergraphData();
        data.Nodes["node-a"] = new HypergraphNode
        {
            Id = "node-a",
            Name = "A",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/A.cs",
            Scope = "MyApp",
        };
        // Duplicate node with same canonical ID — after merge, edge becomes self-referencing
        data.Nodes["node-a-dup"] = new HypergraphNode
        {
            Id = "node-a-dup",
            Name = "A",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/A.cs",
            Scope = "MyApp",
        };
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "edge-self",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "node-a", "node-a-dup" },
            Roles = new Dictionary<string, string>
            {
                { "node-a", "caller" },
                { "node-a-dup", "callee" },
            },
        });

        this.normalizer.Normalize(data, RepoRoot);

        // Self-referencing edge should be removed
        data.Hyperedges.Should().NotContain(e => e.Id == "edge-self");
    }

    [TestMethod]
    public void Normalize_SynthesizesRepoRootNode()
    {
        var data = new HypergraphData();
        data.Nodes["file-node"] = new HypergraphNode
        {
            Id = "file-node",
            Name = "Foo.cs",
            NodeType = "File",
            Language = "C#",
            FilePath = "src/Foo.cs",
        };

        this.normalizer.Normalize(data, RepoRoot);

        data.Nodes.Should().ContainKey(CanonicalId.RepoRoot);
        data.Nodes[CanonicalId.RepoRoot].NodeType.Should().Be("Repo");
    }

    [TestMethod]
    public void NormalizeFiles_FiltersToChangedFilesOnly()
    {
        var data = new HypergraphData();

        // Node from changed file
        data.Nodes["changed-class"] = new HypergraphNode
        {
            Id = "changed-class",
            Name = "Foo",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Foo.cs",
            Scope = "MyApp",
        };

        // Node from unchanged file — referenced by an edge to a changed file,
        // so it is preserved as a canonical reference endpoint (not filtered out)
        data.Nodes["unchanged-class"] = new HypergraphNode
        {
            Id = "unchanged-class",
            Name = "Bar",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Bar.cs",
            Scope = "MyApp",
        };

        // Unreferenced node from unchanged file — should be removed
        data.Nodes["unreferenced-class"] = new HypergraphNode
        {
            Id = "unreferenced-class",
            Name = "Qux",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Qux.cs",
            Scope = "MyApp",
        };

        // Edge between changed and unchanged — should be kept (one endpoint in changed file)
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "edge-1",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "changed-class", "unchanged-class" },
            Roles = new Dictionary<string, string>
            {
                { "changed-class", "caller" },
                { "unchanged-class", "callee" },
            },
        });

        var result = this.normalizer.NormalizeFiles(
            data, RepoRoot, new[] { "src/Foo.cs" });

        result.Should().NotBeNull();

        // The changed-file node should be preserved
        data.Nodes.Values
            .Where(n => n.Name == "Foo")
            .Should().HaveCount(1, "Foo node from changed file should be preserved");

        // Bar is referenced by a retained edge — it should be preserved as a canonical endpoint
        data.Nodes.Values
            .Where(n => n.Name == "Bar")
            .Should().HaveCount(1, "Bar is referenced by retained edge and should be preserved");

        // Qux is NOT referenced by any retained edge — should be removed
        data.Nodes.Values
            .Where(n => n.Name == "Qux")
            .Should().BeEmpty("Qux from unchanged file with no edge reference should be filtered out");

        // The edge should have valid endpoints (no orphan references)
        foreach (var edge in data.Hyperedges)
        {
            foreach (var nid in edge.NodeIds)
            {
                data.Nodes.Should().ContainKey(nid,
                    $"edge '{edge.Id}' endpoint '{nid}' should reference an existing node");
            }
        }
    }

    [TestMethod]
    public void NormalizeFiles_KeepsEdgesWithChangedFileEndpoint_DropsOthers()
    {
        var data = new HypergraphData();

        var changedNodeId = "changed-foo";
        data.Nodes[changedNodeId] = new HypergraphNode
        {
            Id = changedNodeId,
            Name = "Foo",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Foo.cs",
            Scope = "MyApp",
        };

        var unchangedA = "unchanged-bar";
        data.Nodes[unchangedA] = new HypergraphNode
        {
            Id = unchangedA,
            Name = "Bar",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Bar.cs",
            Scope = "MyApp",
        };

        var unchangedB = "unchanged-baz";
        data.Nodes[unchangedB] = new HypergraphNode
        {
            Id = unchangedB,
            Name = "Baz",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Baz.cs",
            Scope = "MyApp",
        };

        // Edge touching changed file — has one endpoint from changed file
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "edge-changed",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { changedNodeId, unchangedA },
            Roles = new Dictionary<string, string>
            {
                { changedNodeId, "caller" },
                { unchangedA, "callee" },
            },
        });

        // Edge between unchanged files only — should be dropped since
        // neither endpoint belongs to a changed file
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "edge-unchanged",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { unchangedA, unchangedB },
            Roles = new Dictionary<string, string>
            {
                { unchangedA, "caller" },
                { unchangedB, "callee" },
            },
        });

        this.normalizer.NormalizeFiles(data, RepoRoot, new[] { "src/Foo.cs" });

        // Foo (from changed file) should remain
        data.Nodes.Values
            .Where(n => n.Name == "Foo")
            .Should().HaveCount(1, "Foo from changed file should remain");

        // Bar is referenced by the retained edge — it should be preserved
        data.Nodes.Values
            .Where(n => n.Name == "Bar")
            .Should().HaveCount(1, "Bar is an endpoint of a retained edge and should be preserved");

        // Baz is only referenced by the dropped edge — should be removed
        data.Nodes.Values
            .Where(n => n.Name == "Baz")
            .Should().BeEmpty("Baz has no retained edge reference and should be filtered out");

        // The retained edge (changed→unchanged) should have both valid endpoints
        data.Hyperedges.Should().NotBeEmpty("edge with one changed endpoint should be retained");
        foreach (var edge in data.Hyperedges)
        {
            foreach (var nid in edge.NodeIds)
            {
                data.Nodes.Should().ContainKey(nid,
                    $"edge '{edge.Id}' endpoint '{nid}' should reference an existing node — no orphans");
            }
        }

        // The edge-unchanged between only unchanged files should be dropped
        var unchangedOnlyEdges = data.Hyperedges
            .Where(e =>
            {
                var allUnchanged = e.NodeIds.All(id =>
                {
                    var node = data.Nodes.GetValueOrDefault(id);
                    return node != null && (node.Name == "Bar" || node.Name == "Baz");
                });
                return allUnchanged;
            })
            .ToList();
        unchangedOnlyEdges.Should().BeEmpty(
            "edges between only unchanged files should be removed");
    }

    [TestMethod]
    public void Normalize_AssignsCanonicalSemanticRelations_NotRetiredNames()
    {
        var data = new HypergraphData();
        data.Nodes["a"] = new HypergraphNode
        {
            Id = "a",
            Name = "A",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/A.cs",
            Scope = "MyApp",
        };
        data.Nodes["b"] = new HypergraphNode
        {
            Id = "b",
            Name = "B",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/B.cs",
            Scope = "MyApp",
        };

        // UsingDirective should get DEPENDS_ON (not IMPORTS)
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "e-using",
            EdgeType = HyperedgeType.UsingDirective,
            NodeIds = new List<string> { "a", "b" },
            Roles = new Dictionary<string, string> { { "a", "importer" }, { "b", "imported" } },
        });

        // ClassDefinition should get CONTAINS (not DEFINES)
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "e-def",
            EdgeType = HyperedgeType.ClassDefinition,
            NodeIds = new List<string> { "a", "b" },
            Roles = new Dictionary<string, string> { { "a", "parent" }, { "b", "child" } },
        });

        // MethodInvocation should get CALLS
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "e-call",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "a", "b" },
            Roles = new Dictionary<string, string> { { "a", "caller" }, { "b", "callee" } },
        });

        this.normalizer.Normalize(data, RepoRoot);

        var semanticRelations = data.Hyperedges
            .Select(e => e.Metadata["SemanticRelation"]?.ToString())
            .ToList();

        semanticRelations.Should().Contain("DEPENDS_ON", "UsingDirective should map to DEPENDS_ON");
        semanticRelations.Should().Contain("CONTAINS", "ClassDefinition should map to CONTAINS");
        semanticRelations.Should().Contain("CALLS", "MethodInvocation should map to CALLS");
        semanticRelations.Should().NotContain("IMPORTS", "IMPORTS is a retired name");
        semanticRelations.Should().NotContain("DEFINES", "DEFINES is a retired name");
    }

    [TestMethod]
    public void Normalize_MergesCrossLanguageDuplicates_WithSimpleName_NoDots()
    {
        // Cross-language merge should work even for simple names like "PaymentService"
        // without namespace dots, when languages differ
        var data = new HypergraphData();

        // C# definition with scope (produces qualified name "MyApp.PaymentService")
        data.Nodes["cs-svc"] = new HypergraphNode
        {
            Id = "cs-svc",
            Name = "PaymentService",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/PaymentService.cs",
            Scope = "MyApp",
        };

        // PowerShell reference with same name and scope (from cross-language synthesis)
        data.Nodes["ps-svc"] = new HypergraphNode
        {
            Id = "ps-svc",
            Name = "PaymentService",
            NodeType = "Class",
            Language = "PowerShell",
            FilePath = "scripts/Deploy.ps1",
            Scope = "MyApp",
        };

        // PS function as caller
        data.Nodes["ps-func"] = new HypergraphNode
        {
            Id = "ps-func",
            Name = "Invoke-Payment",
            NodeType = "Function",
            Language = "PowerShell",
            FilePath = "scripts/Deploy.ps1",
        };

        // Cross-language edge
        data.Hyperedges.Add(new Hyperedge
        {
            Id = "cross-edge",
            EdgeType = HyperedgeType.CrossLanguageMethodCall,
            NodeIds = new List<string> { "ps-func", "ps-svc" },
            Roles = new Dictionary<string, string>
            {
                { "ps-func", "caller" },
                { "ps-svc", "callee" },
            },
        });

        var result = this.normalizer.Normalize(data, RepoRoot);

        result.DuplicatesMerged.Should().BeGreaterThan(0,
            "cross-language nodes with same QualifiedName should merge even without dots in the short name");

        var paymentNodes = data.Nodes.Values
            .Where(n => n.Name == "PaymentService" && n.NodeType == "Class")
            .ToList();
        paymentNodes.Should().HaveCount(1, "exactly one canonical PaymentService node");
        paymentNodes[0].Language.Should().Be("C#", "C# definition should win");
    }
}