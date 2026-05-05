// -----------------------------------------------------------------------
// <copyright file="CallChainAnalyzerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Core;
using Ast.Memory.Extraction;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public class CallChainAnalyzerTests
{
    private FakeMemoryGraphStore store = null!;
    private CallChainAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new FakeMemoryGraphStore();
        this.analyzer = new CallChainAnalyzer(
            this.store,
            NullLogger<CallChainAnalyzer>.Instance);
    }

    // ─── Scenario: CALLS edge creation ──────────────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task CallsEdge_MethodInvocation_CreatesEdgeWithCorrectMetadata()
    {
        // Given ServiceA.Process() calls RepositoryB.Save()
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("ServiceA.Process", "Method", "C#"),
                CreateNode("RepositoryB.Save", "Method", "C#"),
            ],
            edges:
            [
                CreateEdge("inv-1", HyperedgeType.MethodInvocation,
                    ["ServiceA.Process", "RepositoryB.Save"],
                    new Dictionary<string, string>
                    {
                        ["ServiceA.Process"] = "caller",
                        ["RepositoryB.Save"] = "callee"
                    })
            ]);

        var version = new VersionRange("abc123", 1, null, null);

        // When CallChainAnalyzer runs
        var result = await this.analyzer.AnalyzeAsync(data, version);

        // Then a CALLS edge exists with correct metadata
        result.EdgesCreated.Should().Be(1);
        result.CallsEdges.Should().Be(1);
        result.Errors.Should().BeEmpty();

        this.store.UpsertedEdges.Should().HaveCount(1);
        var edge = this.store.UpsertedEdges[0].Edge;

        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
        edge.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("call_chain");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("MethodInvocation");
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(1.0);
        edge.Roles["ServiceA.Process"].Should().Be("caller");
        edge.Roles["RepositoryB.Save"].Should().Be("callee");
        edge.EdgeType.Should().Be(HyperedgeType.MethodInvocation);
    }

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task CallsEdge_ConstructorInvocation_CreatesEdgeWithCorrectMetadata()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("PaymentService.Process", "Method", "C#"),
                CreateNode("OrderRepository.ctor", "Constructor", "C#"),
            ],
            edges:
            [
                CreateEdge("ctor-1", HyperedgeType.ConstructorInvocation,
                    ["PaymentService.Process", "OrderRepository.ctor"],
                    new Dictionary<string, string>
                    {
                        ["PaymentService.Process"] = "caller",
                        ["OrderRepository.ctor"] = "callee"
                    })
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.CallsEdges.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
        edge.EdgeType.Should().Be(HyperedgeType.ConstructorInvocation);
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(1.0);
    }

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task CallsEdge_CmdletCall_CreatesEdgeWithCorrectMetadata()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("Deploy-Service", "Function", "PowerShell"),
                CreateNode("Get-AzResource", "Cmdlet", "PowerShell"),
            ],
            edges:
            [
                CreateEdge("cmd-1", HyperedgeType.CmdletCall,
                    ["Deploy-Service", "Get-AzResource"],
                    new Dictionary<string, string>
                    {
                        ["Deploy-Service"] = "caller",
                        ["Get-AzResource"] = "callee"
                    })
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.CallsEdges.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
        edge.EdgeType.Should().Be(HyperedgeType.CmdletCall);
    }

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task CallsEdge_CrossLanguageMethodCall_HasReducedConfidence()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("Invoke-PaymentService", "Function", "PowerShell"),
                CreateNode("PaymentService.ProcessPayment", "Method", "C#"),
            ],
            edges:
            [
                CreateEdge("cross-1", HyperedgeType.CrossLanguageMethodCall,
                    ["Invoke-PaymentService", "PaymentService.ProcessPayment"],
                    new Dictionary<string, string>
                    {
                        ["Invoke-PaymentService"] = "caller",
                        ["PaymentService.ProcessPayment"] = "callee"
                    },
                    languages: ["PowerShell", "C#"])
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.CallsEdges.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(0.7);
    }

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task CallsEdge_DynamicDispatch_HasReducedConfidence()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("OrderService.Submit", "Method", "C#"),
                CreateNode("IValidator.Validate", "Method", "C#"),
            ],
            edges:
            [
                CreateEdge("dyn-1", HyperedgeType.MethodInvocation,
                    ["OrderService.Submit", "IValidator.Validate"],
                    new Dictionary<string, string>
                    {
                        ["OrderService.Submit"] = "caller",
                        ["IValidator.Validate"] = "callee"
                    },
                    metadata: new Dictionary<string, object?> { ["IsVirtualDispatch"] = true })
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        var edge = this.store.UpsertedEdges[0].Edge;
        ((double)edge.Metadata[MemoryMetadataKeys.Confidence]!).Should().BeLessThan(1.0);
    }

    // ─── Scenario: EXTENDS edge ─────────────────────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task ExtendsEdge_ClassExtension_CreatesEdgeWithCorrectRoles()
    {
        // Given PaymentController extends ControllerBase
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("PaymentController", "Class", "C#"),
                CreateNode("ControllerBase", "Class", "C#"),
            ],
            edges:
            [
                CreateEdge("ext-1", HyperedgeType.Extends,
                    ["PaymentController", "ControllerBase"],
                    new Dictionary<string, string>
                    {
                        ["PaymentController"] = "subclass",
                        ["ControllerBase"] = "superclass"
                    })
            ]);

        var version = new VersionRange("abc123", 1, null, null);

        // When CallChainAnalyzer runs
        var result = await this.analyzer.AnalyzeAsync(data, version);

        // Then an EXTENDS edge exists with roles subclass/superclass
        result.ExtendsEdges.Should().Be(1);
        result.Errors.Should().BeEmpty();

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("EXTENDS");
        edge.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("call_chain");
        edge.Roles["PaymentController"].Should().Be("subclass");
        edge.Roles["ControllerBase"].Should().Be("superclass");
        edge.EdgeType.Should().Be(HyperedgeType.Extends);
    }

    // ─── Scenario: IMPLEMENTS edge ──────────────────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task ImplementsEdge_InterfaceImplementation_CreatesEdgeWithCorrectRoles()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("PaymentService", "Class", "C#"),
                CreateNode("IPaymentService", "Interface", "C#"),
            ],
            edges:
            [
                CreateEdge("impl-1", HyperedgeType.Implements,
                    ["PaymentService", "IPaymentService"],
                    new Dictionary<string, string>
                    {
                        ["PaymentService"] = "implementor",
                        ["IPaymentService"] = "interface"
                    })
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.ImplementsEdges.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("IMPLEMENTS");
        edge.Roles["PaymentService"].Should().Be("implementor");
        edge.Roles["IPaymentService"].Should().Be("interface");
    }

    // ─── Scenario: REFERENCES edge ──────────────────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task ReferencesEdge_DataFlow_CreatesEdgeWithSourceTarget()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("PaymentService.result", "Variable", "C#"),
                CreateNode("OrderService.status", "Variable", "C#"),
            ],
            edges:
            [
                CreateEdge("df-1", HyperedgeType.DataFlow,
                    ["PaymentService.result", "OrderService.status"],
                    new Dictionary<string, string>
                    {
                        ["PaymentService.result"] = "source",
                        ["OrderService.status"] = "target"
                    })
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.ReferencesEdges.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("REFERENCES");
        edge.Roles["PaymentService.result"].Should().Be("source");
        edge.Roles["OrderService.status"].Should().Be("target");
    }

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task ReferencesEdge_Assignment_CreatesEdgeCorrectly()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("Config.Value", "Property", "C#"),
                CreateNode("Service.Setting", "Property", "C#"),
            ],
            edges:
            [
                CreateEdge("assign-1", HyperedgeType.Assignment,
                    ["Config.Value", "Service.Setting"],
                    new Dictionary<string, string>
                    {
                        ["Config.Value"] = "source",
                        ["Service.Setting"] = "target"
                    })
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.ReferencesEdges.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("REFERENCES");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("Assignment");
    }

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task ReferencesEdge_CrossLanguageTypeReference_CreatesEdgeCorrectly()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("Deploy-Script.psm1", "Module", "PowerShell"),
                CreateNode("PaymentService", "Class", "C#"),
            ],
            edges:
            [
                CreateEdge("xref-1", HyperedgeType.CrossLanguageTypeReference,
                    ["Deploy-Script.psm1", "PaymentService"],
                    new Dictionary<string, string>
                    {
                        ["Deploy-Script.psm1"] = "source",
                        ["PaymentService"] = "target"
                    },
                    languages: ["PowerShell", "C#"])
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.ReferencesEdges.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("REFERENCES");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("CrossLanguageTypeReference");
    }

    // ─── Scenario: No reverse-call stored edge ──────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task NoReverseCallStoredEdge_AllEdgesQueried_NoReverseSemanticRelation()
    {
        // Given a processed repository with multiple edge types
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("ServiceA.Process", "Method", "C#"),
                CreateNode("RepositoryB.Save", "Method", "C#"),
                CreateNode("PaymentController", "Class", "C#"),
                CreateNode("ControllerBase", "Class", "C#"),
                CreateNode("PaymentService", "Class", "C#"),
                CreateNode("IPaymentService", "Interface", "C#"),
            ],
            edges:
            [
                CreateEdge("inv-1", HyperedgeType.MethodInvocation,
                    ["ServiceA.Process", "RepositoryB.Save"],
                    new Dictionary<string, string>
                    {
                        ["ServiceA.Process"] = "caller",
                        ["RepositoryB.Save"] = "callee"
                    }),
                CreateEdge("ext-1", HyperedgeType.Extends,
                    ["PaymentController", "ControllerBase"],
                    new Dictionary<string, string>
                    {
                        ["PaymentController"] = "subclass",
                        ["ControllerBase"] = "superclass"
                    }),
                CreateEdge("impl-1", HyperedgeType.Implements,
                    ["PaymentService", "IPaymentService"],
                    new Dictionary<string, string>
                    {
                        ["PaymentService"] = "implementor",
                        ["IPaymentService"] = "interface"
                    }),
            ]);

        // When all edges are queried
        await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        // Then no edge has a reverse-call SemanticRelation
        var reverseRelations = new[] { "CALLED_BY", "REVERSE_CALL", "CALLED_FROM" };
        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            var relation = edge.Metadata[MemoryMetadataKeys.SemanticRelation]?.ToString();
            relation.Should().NotBeNull();
            reverseRelations.Should().NotContain(relation!,
                "reverse lookups use backward traversal of CALLS edges only (architecture.md §2.3)");
        }
    }

    // ─── Scenario: Metadata preservation ────────────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task AllEdges_HaveOriginalEdgeTypeMetadata()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("A", "Method", "C#"),
                CreateNode("B", "Method", "C#"),
                CreateNode("C", "Class", "C#"),
                CreateNode("D", "Class", "C#"),
                CreateNode("E", "Variable", "C#"),
                CreateNode("F", "Variable", "C#"),
            ],
            edges:
            [
                CreateEdge("e1", HyperedgeType.MethodInvocation, ["A", "B"],
                    new Dictionary<string, string> { ["A"] = "caller", ["B"] = "callee" }),
                CreateEdge("e2", HyperedgeType.Extends, ["C", "D"],
                    new Dictionary<string, string> { ["C"] = "subclass", ["D"] = "superclass" }),
                CreateEdge("e3", HyperedgeType.DataFlow, ["E", "F"],
                    new Dictionary<string, string> { ["E"] = "source", ["F"] = "target" }),
            ]);

        await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.OriginalEdgeType);
            edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().NotBeNull();
        }
    }

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task AllEdges_HaveCallChainMemoryLayer()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("A", "Method", "C#"),
                CreateNode("B", "Method", "C#"),
            ],
            edges:
            [
                CreateEdge("e1", HyperedgeType.MethodInvocation, ["A", "B"],
                    new Dictionary<string, string> { ["A"] = "caller", ["B"] = "callee" }),
            ]);

        await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            edge.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("call_chain");
        }
    }

    // ─── Scenario: Unrelated edges are skipped ──────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task UnrelatedEdgeTypes_AreSkipped()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("A", "Class", "C#"),
                CreateNode("B", "Method", "C#"),
            ],
            edges:
            [
                CreateEdge("e1", HyperedgeType.ClassDefinition, ["A", "B"],
                    new Dictionary<string, string> { ["A"] = "parent", ["B"] = "child" }),
                CreateEdge("e2", HyperedgeType.UsingDirective, ["A", "B"],
                    new Dictionary<string, string> { ["A"] = "importer", ["B"] = "imported" }),
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.EdgesCreated.Should().Be(0);
        this.store.UpsertedEdges.Should().BeEmpty();
    }

    // ─── Scenario: Version range is passed through ──────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task VersionRange_IsPassedToStore()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("A", "Method", "C#"),
                CreateNode("B", "Method", "C#"),
            ],
            edges:
            [
                CreateEdge("e1", HyperedgeType.MethodInvocation, ["A", "B"],
                    new Dictionary<string, string> { ["A"] = "caller", ["B"] = "callee" }),
            ]);

        var version = new VersionRange("commit-sha-42", 42, null, null);
        await this.analyzer.AnalyzeAsync(data, version);

        this.store.UpsertedEdges[0].Version.Should().Be(version);
        this.store.UpsertedEdges[0].Version.SinceIndex.Should().Be(42);
    }

    // ─── Scenario: Mixed edge types in one pass ─────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task MixedEdgeTypes_AllProcessedCorrectly()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("A", "Method", "C#"),
                CreateNode("B", "Method", "C#"),
                CreateNode("C", "Class", "C#"),
                CreateNode("D", "Class", "C#"),
                CreateNode("E", "Class", "C#"),
                CreateNode("F", "Interface", "C#"),
                CreateNode("G", "Variable", "C#"),
                CreateNode("H", "Variable", "C#"),
                CreateNode("I", "Function", "PowerShell"),
                CreateNode("J", "Cmdlet", "PowerShell"),
            ],
            edges:
            [
                CreateEdge("e1", HyperedgeType.MethodInvocation, ["A", "B"],
                    new Dictionary<string, string> { ["A"] = "caller", ["B"] = "callee" }),
                CreateEdge("e2", HyperedgeType.Extends, ["C", "D"],
                    new Dictionary<string, string> { ["C"] = "subclass", ["D"] = "superclass" }),
                CreateEdge("e3", HyperedgeType.Implements, ["E", "F"],
                    new Dictionary<string, string> { ["E"] = "implementor", ["F"] = "interface" }),
                CreateEdge("e4", HyperedgeType.DataFlow, ["G", "H"],
                    new Dictionary<string, string> { ["G"] = "source", ["H"] = "target" }),
                CreateEdge("e5", HyperedgeType.CmdletCall, ["I", "J"],
                    new Dictionary<string, string> { ["I"] = "caller", ["J"] = "callee" }),
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.EdgesCreated.Should().Be(5);
        result.CallsEdges.Should().Be(2); // MethodInvocation + CmdletCall
        result.ExtendsEdges.Should().Be(1);
        result.ImplementsEdges.Should().Be(1);
        result.ReferencesEdges.Should().Be(1);
    }

    // ─── Scenario: Semantic relation filtering ──────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task SemanticRelation_IsTheSoleFilteringMechanism()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("A", "Method", "C#"),
                CreateNode("B", "Method", "C#"),
            ],
            edges:
            [
                CreateEdge("e1", HyperedgeType.MethodInvocation, ["A", "B"],
                    new Dictionary<string, string> { ["A"] = "caller", ["B"] = "callee" }),
            ]);

        await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        var edge = this.store.UpsertedEdges[0].Edge;

        // The edge retains its original HyperedgeType
        edge.EdgeType.Should().Be(HyperedgeType.MethodInvocation);

        // SemanticRelation is the canonical filtering key
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");

        // OriginalEdgeType is preserved for reference
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be("MethodInvocation");
    }

    // ─── Scenario: Interface callee gets reduced confidence ─────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task InterfaceCallee_GetsReducedConfidence()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("Service.Execute", "Method", "C#"),
                CreateNode("IRepository.Save", "Interface", "C#"),
            ],
            edges:
            [
                CreateEdge("e1", HyperedgeType.MethodInvocation,
                    ["Service.Execute", "IRepository.Save"],
                    new Dictionary<string, string>
                    {
                        ["Service.Execute"] = "caller",
                        ["IRepository.Save"] = "callee"
                    })
            ]);

        await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        var confidence = (double)this.store.UpsertedEdges[0].Edge.Metadata[MemoryMetadataKeys.Confidence]!;
        confidence.Should().BeLessThan(1.0, "interface dispatch is dynamic");
    }

    // ─── Scenario: ModuleFunctionCall edge ──────────────────────────────

    [TestMethod]
    [TestCategory("call_chain")]
    public async Task ModuleFunctionCall_CreatesCallsEdge()
    {
        var data = CreateHypergraphData(
            nodes:
            [
                CreateNode("Main.ps1", "Function", "PowerShell"),
                CreateNode("Deploy-App", "Function", "PowerShell"),
            ],
            edges:
            [
                CreateEdge("mfc-1", HyperedgeType.ModuleFunctionCall,
                    ["Main.ps1", "Deploy-App"],
                    new Dictionary<string, string>
                    {
                        ["Main.ps1"] = "caller",
                        ["Deploy-App"] = "callee"
                    })
            ]);

        var result = await this.analyzer.AnalyzeAsync(data, new VersionRange("abc", 1, null, null));

        result.CallsEdges.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
        edge.EdgeType.Should().Be(HyperedgeType.ModuleFunctionCall);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static HypergraphData CreateHypergraphData(
        HypergraphNode[] nodes,
        Hyperedge[] edges)
    {
        var nodeDict = new Dictionary<string, HypergraphNode>();
        foreach (var node in nodes)
        {
            nodeDict[node.Id] = node;
        }

        return new HypergraphData
        {
            Nodes = nodeDict,
            Hyperedges = [.. edges],
        };
    }

    private static HypergraphNode CreateNode(
        string id,
        string nodeType,
        string language)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = id,
            NodeType = nodeType,
            Language = language,
            FilePath = $"/src/{id.Replace('.', '/')}.cs",
        };
    }

    private static Hyperedge CreateEdge(
        string id,
        HyperedgeType edgeType,
        string[] nodeIds,
        Dictionary<string, string> roles,
        List<string>? languages = null,
        Dictionary<string, object?>? metadata = null)
    {
        return new Hyperedge
        {
            Id = id,
            EdgeType = edgeType,
            NodeIds = [.. nodeIds],
            Roles = roles,
            Languages = languages ?? [],
            Metadata = metadata ?? [],
        };
    }
}