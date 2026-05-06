// -----------------------------------------------------------------------
// <copyright file="CallChainAnalyzerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Core;
using Ast.Memory.Extraction;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public class CallChainAnalyzerTests
{
    private FakeMemoryGraphStore store = null!;
    private CallChainAnalyzer analyzer = null!;
    private VersionRange version = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new FakeMemoryGraphStore();
        this.analyzer = new CallChainAnalyzer(
            this.store,
            NullLogger<CallChainAnalyzer>.Instance);
        this.version = new VersionRange("abc123", 0, null, null);
    }

    // ── Scenario: CALLS edge creation ───────────────────────────────────
    // Given ServiceA.Process() calls RepositoryB.Save(),
    // When CallChainAnalyzer runs,
    // Then a CALLS edge exists with Metadata["SemanticRelation"]="CALLS",
    // roles caller=ServiceA.Process, callee=RepositoryB.Save, and Confidence=1.0

    [TestMethod]
    public async Task CallsEdge_FromMethodInvocation_WithExplicitCallerRole()
    {
        // Arrange — idealized input with explicit caller/callee roles
        var callerNode = CreateNode("node-process", "Process", "method", scope: "ServiceA");
        var calleeNode = CreateNode("node-save", "Save", "method", scope: "RepositoryB");

        var data = new HypergraphData
        {
            Nodes = { [callerNode.Id] = callerNode, [calleeNode.Id] = calleeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-001",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callerNode.Id, calleeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callerNode.Id] = "caller",
                        [calleeNode.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        result.EdgesCreated.Should().Be(1);
        result.CallsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.NodeIds.Should().HaveCount(2);
        edge.Roles[callerNode.Id].Should().Be("caller");
        edge.Roles[calleeNode.Id].Should().Be("callee");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(1.0);
    }

    [TestMethod]
    public async Task CallsEdge_FromRealCSharpExtractor_MethodRefWithScope()
    {
        // Arrange — mimics CSharpHyperedgeExtractor output:
        // callee is method_ref (not a definition node), with Scope identifying the caller
        var callerDef = CreateNode("node-process", "Process", "method", scope: "ServiceA");
        var calleeRef = CreateNode("node-save-ref", "RepositoryB.Save", "method_ref",
            scope: "ServiceA.Process");
        var argNode = CreateNode("node-arg0", "entity", "expression",
            scope: "ServiceA.Process");

        // Also include the canonical definition for Save
        var calleeDef = CreateNode("node-save-def", "Save", "method", scope: "RepositoryB");

        var data = new HypergraphData
        {
            Nodes =
            {
                [callerDef.Id] = callerDef,
                [calleeRef.Id] = calleeRef,
                [argNode.Id] = argNode,
                [calleeDef.Id] = calleeDef,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-002",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [calleeRef.Id, argNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [calleeRef.Id] = "callee",
                        [argNode.Id] = "arg_0",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        result.EdgesCreated.Should().Be(1);
        result.CallsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.NodeIds.Should().HaveCount(2);

        // Caller resolved from scope, callee resolved to canonical definition
        edge.Roles.Values.Should().Contain("caller");
        edge.Roles.Values.Should().Contain("callee");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(1.0);

        // Callee should resolve to the definition node when unambiguous
        edge.NodeIds.Should().Contain(calleeDef.Id);
    }

    [TestMethod]
    public async Task CallsEdge_FromCmdletCall_ScriptLevelWithoutRealCaller_SynthesizesFileCaller()
    {
        // Arrange — top-level CmdletCall in a PowerShell script:
        // no containing function (no scope), no explicit caller role,
        // and NO existing script/file-level node in the data.
        // The analyzer synthesizes a file-level node as caller so that
        // CmdletCall hyperedges always emit CALLS edges.
        var cmdletNode = CreateNode("node-get-item", "Get-Item", "cmdlet",
            filePath: "scripts/deploy.ps1");
        var paramNode = CreateNode("node-path", "-Path", "param_name",
            filePath: "scripts/deploy.ps1");

        var data = new HypergraphData
        {
            Nodes =
            {
                [cmdletNode.Id] = cmdletNode,
                [paramNode.Id] = paramNode,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-cmdlet",
                    EdgeType = HyperedgeType.CmdletCall,
                    NodeIds = [cmdletNode.Id, paramNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [cmdletNode.Id] = "cmdlet",
                        [paramNode.Id] = "param_Path",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — edge IS produced: a synthetic file-level node is created as caller
        result.CallsEdges.Should().Be(1);
        result.EdgesCreated.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
        edge.Roles.Values.Should().Contain("caller");
        edge.Roles.Values.Should().Contain("callee");

        // The synthetic file node should be upserted to the store with MemoryLayer
        this.store.UpsertedNodes.Should().ContainSingle(n =>
            n.Node.NodeType == "file" && n.Node.FilePath == "scripts/deploy.ps1");
        var syntheticNode = this.store.UpsertedNodes[0].Node;
        syntheticNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("structural",
            because: "synthetic nodes must carry MemoryLayer metadata per §3.11");

        // Input data must NOT be mutated
        data.Nodes.Should().HaveCount(2,
            because: "AnalyzeAsync must not add synthetic nodes to input HypergraphData");
    }

    [TestMethod]
    public async Task CallsEdge_FromCmdletCall_WithScriptNodeAsCaller()
    {
        // Arrange — CmdletCall with an existing script node in the same file;
        // the analyzer prefers a real script/file node over a synthetic file ID
        var scriptNode = CreateNode("node-script", "deploy.ps1", "script",
            filePath: "scripts/deploy.ps1");
        var cmdletNode = CreateNode("node-get-item", "Get-Item", "cmdlet",
            filePath: "scripts/deploy.ps1");

        var data = new HypergraphData
        {
            Nodes =
            {
                [scriptNode.Id] = scriptNode,
                [cmdletNode.Id] = cmdletNode,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-cmdlet-script",
                    EdgeType = HyperedgeType.CmdletCall,
                    NodeIds = [cmdletNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [cmdletNode.Id] = "cmdlet",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — script node used as caller (real node preferred over synthetic)
        result.CallsEdges.Should().Be(1);
        result.EdgesCreated.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.NodeIds.Should().Contain(scriptNode.Id);
        edge.Roles.Values.Should().Contain("caller");
        edge.Roles.Values.Should().Contain("callee");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
    }

    [TestMethod]
    public async Task CallsEdge_FromCmdletCall_WithExplicitCaller()
    {
        // Arrange — CmdletCall with explicit caller role provided
        var callerNode = CreateNode("node-invoke", "Invoke-Build", "function");
        var cmdletNode = CreateNode("node-get-item", "Get-Item", "cmdlet");

        var data = new HypergraphData
        {
            Nodes =
            {
                [callerNode.Id] = callerNode,
                [cmdletNode.Id] = cmdletNode,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-cmdlet-caller",
                    EdgeType = HyperedgeType.CmdletCall,
                    NodeIds = [callerNode.Id, cmdletNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callerNode.Id] = "caller",
                        [cmdletNode.Id] = "cmdlet",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        result.EdgesCreated.Should().Be(1);
        result.CallsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles.Values.Should().Contain("caller");
        edge.Roles.Values.Should().Contain("callee");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
    }

    [TestMethod]
    public async Task CallsEdge_FromConstructorInvocation_UsesInstantiatedTypeAsCallee()
    {
        // Arrange — mimics C# extractor: instantiated_type role + ctor args
        var callerDef = CreateNode("node-init", "Init", "method", scope: "App");
        var typeNode = CreateNode("node-repo-type", "Repository", "type",
            scope: "App.Init");
        var argNode = CreateNode("node-arg0", "connStr", "expression",
            scope: "App.Init");

        var data = new HypergraphData
        {
            Nodes =
            {
                [callerDef.Id] = callerDef,
                [typeNode.Id] = typeNode,
                [argNode.Id] = argNode,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-ctor",
                    EdgeType = HyperedgeType.ConstructorInvocation,
                    NodeIds = [typeNode.Id, argNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [typeNode.Id] = "instantiated_type",
                        [argNode.Id] = "ctor_arg_0",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — caller resolved from scope, callee from instantiated_type
        result.EdgesCreated.Should().Be(1);
        result.CallsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles.Values.Should().Contain("callee");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
    }

    [TestMethod]
    public async Task CallsEdge_FromModuleFunctionCall_UsesFileLevelCallerNotSourceModule()
    {
        // Arrange — mimics PS extractor ModuleFunctionCall:
        // source_module + called_function + params.
        // The source_module ("Az.Storage") is the module containing the function,
        // NOT the calling context. The caller should be the script/file where
        // the module-qualified call appears.
        var moduleNode = CreateNode("node-module", "Az.Storage", "module",
            filePath: "scripts/storage.ps1");
        var funcNode = CreateNode("node-func", "Get-AzStorageBlob", "function_ref",
            filePath: "scripts/storage.ps1");
        var paramNode = CreateNode("node-param", "-Container", "param_name",
            filePath: "scripts/storage.ps1");

        var data = new HypergraphData
        {
            Nodes =
            {
                [moduleNode.Id] = moduleNode,
                [funcNode.Id] = funcNode,
                [paramNode.Id] = paramNode,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-modfunc",
                    EdgeType = HyperedgeType.ModuleFunctionCall,
                    NodeIds = [moduleNode.Id, funcNode.Id, paramNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [moduleNode.Id] = "source_module",
                        [funcNode.Id] = "called_function",
                        [paramNode.Id] = "param_Container",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — file-level caller used, NOT source_module
        result.EdgesCreated.Should().Be(1);
        result.CallsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.NodeIds.Should().HaveCount(2);
        edge.Roles.Values.Should().Contain("caller");
        edge.Roles.Values.Should().Contain("callee");

        // The caller must NOT be the source_module node
        edge.NodeIds.Should().NotContain(moduleNode.Id,
            because: "source_module represents the module containing the function, not the calling context");

        // The caller should be a synthesized file-level node
        var callerId = edge.NodeIds.First(id => edge.Roles.ContainsKey(id) && edge.Roles[id] == "caller");
        var callerNodeInStore = this.store.UpsertedNodes
            .FirstOrDefault(n => n.Node.Id == callerId);
        callerNodeInStore.Node.Should().NotBeNull();
        callerNodeInStore.Node.NodeType.Should().Be("file");
    }

    [TestMethod]
    public async Task CallsEdge_FromModuleFunctionCall_InsideFunction_UsesEnclosingFunctionAsCaller()
    {
        // Arrange — ModuleFunctionCall inside a function: caller resolved from scope
        var funcDef = CreateNode("func-invoke", "Invoke-Build", "function",
            scope: "", filePath: "scripts/build.ps1");
        var moduleNode = CreateNode("node-module", "Az.Storage", "module",
            filePath: "scripts/build.ps1");
        var calledFunc = CreateNode("node-func", "Get-AzStorageBlob", "function_ref",
            scope: "Invoke-Build", filePath: "scripts/build.ps1");

        var data = new HypergraphData
        {
            Nodes =
            {
                [funcDef.Id] = funcDef,
                [moduleNode.Id] = moduleNode,
                [calledFunc.Id] = calledFunc,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-modfunc-scoped",
                    EdgeType = HyperedgeType.ModuleFunctionCall,
                    NodeIds = [moduleNode.Id, calledFunc.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [moduleNode.Id] = "source_module",
                        [calledFunc.Id] = "called_function",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — caller resolved from scope to enclosing function, not source_module
        result.EdgesCreated.Should().Be(1);
        result.CallsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.NodeIds.Should().Contain(funcDef.Id,
            because: "caller should be the enclosing function resolved from scope");
        edge.NodeIds.Should().NotContain(moduleNode.Id,
            because: "source_module is the target module, not the caller");
    }

    [TestMethod]
    public async Task CallsEdge_CrossLanguageMethodCall_HasReducedConfidence()
    {
        // Arrange — mimics RepositoryProcessor CrossLanguageMethodCall:
        // ps_reference is a PowerShell function call-site (distinct from .NET callee)
        var dotnetDef = CreateNode("node-cs-method", "ProcessData", "method", scope: "DataService");
        var psFunc = CreateNode("node-ps-ref", "Invoke-ProcessData", "function", scope: "Deploy");

        var data = new HypergraphData
        {
            Nodes =
            {
                [dotnetDef.Id] = dotnetDef,
                [psFunc.Id] = psFunc,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-cross",
                    EdgeType = HyperedgeType.CrossLanguageMethodCall,
                    NodeIds = [dotnetDef.Id, psFunc.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [dotnetDef.Id] = "dotnet_definition",
                        [psFunc.Id] = "ps_reference",
                    },
                    Languages = ["csharp", "powershell"],
                    Metadata = new Dictionary<string, object?> { ["cross_language"] = true },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — cross-language gets 0.7 confidence
        result.EdgesCreated.Should().Be(1);
        result.CallsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(0.7);
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
    }

    // ── Scenario: EXTENDS edge ──────────────────────────────────────────
    // Given PaymentController extends ControllerBase,
    // When CallChainAnalyzer runs,
    // Then an EXTENDS edge exists with roles subclass/superclass
    // and Metadata["SemanticRelation"]="EXTENDS"

    [TestMethod]
    public async Task ExtendsEdge_WithCanonicalRoles()
    {
        var subclass = CreateNode("node-payment", "PaymentController", "class");
        var superclass = CreateNode("node-base", "ControllerBase", "class");

        var data = new HypergraphData
        {
            Nodes = { [subclass.Id] = subclass, [superclass.Id] = superclass },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-extends",
                    EdgeType = HyperedgeType.Extends,
                    NodeIds = [subclass.Id, superclass.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [subclass.Id] = "subclass",
                        [superclass.Id] = "superclass",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        result.EdgesCreated.Should().Be(1);
        result.ExtendsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.NodeIds.Should().HaveCount(2);
        edge.Roles[subclass.Id].Should().Be("subclass");
        edge.Roles[superclass.Id].Should().Be("superclass");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("EXTENDS");
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(1.0);
    }

    [TestMethod]
    public async Task ExtendsEdge_FromPositionalBinaryInput()
    {
        // Arrange — binary edge without named roles (pre-processed or test input)
        var subclass = CreateNode("node-child", "ChildClass", "class");
        var superclass = CreateNode("node-parent", "ParentClass", "class");

        var data = new HypergraphData
        {
            Nodes = { [subclass.Id] = subclass, [superclass.Id] = superclass },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-ext-pos",
                    EdgeType = HyperedgeType.Extends,
                    NodeIds = [subclass.Id, superclass.Id],
                    Roles = new Dictionary<string, string>(),
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — positional fallback: first=subclass, second=superclass
        result.EdgesCreated.Should().Be(1);
        result.ExtendsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles[subclass.Id].Should().Be("subclass");
        edge.Roles[superclass.Id].Should().Be("superclass");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("EXTENDS");
    }

    // ── Scenario: IMPLEMENTS edge ───────────────────────────────────────

    [TestMethod]
    public async Task ImplementsEdge_WithCanonicalRoles()
    {
        var impl = CreateNode("node-impl", "UserService", "class");
        var iface = CreateNode("node-iface", "IUserService", "interface");

        var data = new HypergraphData
        {
            Nodes = { [impl.Id] = impl, [iface.Id] = iface },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-impl",
                    EdgeType = HyperedgeType.Implements,
                    NodeIds = [impl.Id, iface.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [impl.Id] = "implementor",
                        [iface.Id] = "interface",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        result.EdgesCreated.Should().Be(1);
        result.ImplementsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles[impl.Id].Should().Be("implementor");
        edge.Roles[iface.Id].Should().Be("interface");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("IMPLEMENTS");
    }

    // ── Scenario: REFERENCES edge ───────────────────────────────────────

    [TestMethod]
    public async Task ReferencesEdge_FromDataFlow()
    {
        var sourceNode = CreateNode("node-src", "data", "method");
        var targetNode = CreateNode("node-tgt", "result", "method");

        var data = new HypergraphData
        {
            Nodes = { [sourceNode.Id] = sourceNode, [targetNode.Id] = targetNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-dataflow",
                    EdgeType = HyperedgeType.DataFlow,
                    NodeIds = [sourceNode.Id, targetNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [sourceNode.Id] = "source",
                        [targetNode.Id] = "target",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        result.EdgesCreated.Should().Be(1);
        result.ReferencesEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("REFERENCES");
    }

    // ── Scenario: No reverse-call stored edge ───────────────────────────
    // Given a processed repository,
    // When all edges are queried,
    // Then no edge has a reverse-call SemanticRelation

    [TestMethod]
    public async Task NoReverseCallEdgesStored()
    {
        // Arrange — multiple call-chain edge types
        var nodeA = CreateNode("node-a", "MethodA", "method", scope: "ClassA");
        var nodeB = CreateNode("node-b", "MethodB", "method", scope: "ClassB");
        var nodeC = CreateNode("node-c", "ClassC", "class");
        var nodeD = CreateNode("node-d", "ClassD", "class");
        var nodeE = CreateNode("node-e", "ServiceE", "class");
        var nodeF = CreateNode("node-f", "IServiceE", "interface");

        var data = new HypergraphData
        {
            Nodes =
            {
                [nodeA.Id] = nodeA, [nodeB.Id] = nodeB,
                [nodeC.Id] = nodeC, [nodeD.Id] = nodeD,
                [nodeE.Id] = nodeE, [nodeF.Id] = nodeF,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-call",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [nodeA.Id, nodeB.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeA.Id] = "caller",
                        [nodeB.Id] = "callee",
                    },
                },
                new Hyperedge
                {
                    Id = "he-ext",
                    EdgeType = HyperedgeType.Extends,
                    NodeIds = [nodeC.Id, nodeD.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeC.Id] = "subclass",
                        [nodeD.Id] = "superclass",
                    },
                },
                new Hyperedge
                {
                    Id = "he-impl",
                    EdgeType = HyperedgeType.Implements,
                    NodeIds = [nodeE.Id, nodeF.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeE.Id] = "implementor",
                        [nodeF.Id] = "interface",
                    },
                },
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — no reverse-call edges exist
        result.EdgesCreated.Should().Be(3);
        result.Errors.Should().BeEmpty();

        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            var semanticRelation = edge.Metadata[MemoryMetadataKeys.SemanticRelation] as string;
            semanticRelation.Should().NotContain("REVERSE",
                because: "reverse call-chain lookups use backward traversal, not stored reverse edges");
            semanticRelation.Should().BeOneOf("CALLS", "EXTENDS", "IMPLEMENTS", "REFERENCES");
        }
    }

    // ── Reverse call-chain lookup via backward traversal ────────────────
    // Architecture.md §2.3: reverse lookups traverse CALLS edges backward,
    // no separate reverse edge is stored.

    [TestMethod]
    public async Task ReverseCallChainLookup_TraversesCallsEdgesBackward()
    {
        // Arrange — call graph: A→B, C→B, D→E (B has two callers)
        var nodeA = CreateNode("node-a", "MethodA", "method", scope: "ClassA");
        var nodeB = CreateNode("node-b", "MethodB", "method", scope: "ClassB");
        var nodeC = CreateNode("node-c", "MethodC", "method", scope: "ClassC");
        var nodeD = CreateNode("node-d", "MethodD", "method", scope: "ClassD");
        var nodeE = CreateNode("node-e", "MethodE", "method", scope: "ClassE");

        var data = new HypergraphData
        {
            Nodes =
            {
                [nodeA.Id] = nodeA, [nodeB.Id] = nodeB,
                [nodeC.Id] = nodeC, [nodeD.Id] = nodeD,
                [nodeE.Id] = nodeE,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-ab",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [nodeA.Id, nodeB.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeA.Id] = "caller",
                        [nodeB.Id] = "callee",
                    },
                },
                new Hyperedge
                {
                    Id = "he-cb",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [nodeC.Id, nodeB.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeC.Id] = "caller",
                        [nodeB.Id] = "callee",
                    },
                },
                new Hyperedge
                {
                    Id = "he-de",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [nodeD.Id, nodeE.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeD.Id] = "caller",
                        [nodeE.Id] = "callee",
                    },
                },
            }
        };

        // Act — produce CALLS edges
        var result = await this.analyzer.AnalyzeAsync(data, this.version);
        result.EdgesCreated.Should().Be(3);
        result.CallsEdges.Should().Be(3);

        var callsEdges = this.store.UpsertedEdges.Select(e => e.Edge).ToList();

        // Assert — backward traversal finds callers
        var callersOfB = CallChainAnalyzer.FindCallersOf(nodeB.Id, callsEdges);
        callersOfB.Should().HaveCount(2);
        callersOfB.Should().Contain(nodeA.Id);
        callersOfB.Should().Contain(nodeC.Id);

        var callersOfE = CallChainAnalyzer.FindCallersOf(nodeE.Id, callsEdges);
        callersOfE.Should().HaveCount(1);
        callersOfE.Should().Contain(nodeD.Id);

        // MethodA has no callers
        var callersOfA = CallChainAnalyzer.FindCallersOf(nodeA.Id, callsEdges);
        callersOfA.Should().BeEmpty();

        // No reverse edges were stored — only forward CALLS edges
        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            var sr = edge.Metadata[MemoryMetadataKeys.SemanticRelation] as string;
            sr.Should().Be("CALLS");
            sr.Should().NotContain("REVERSE");
        }
    }

    [TestMethod]
    public async Task ReverseCallChainLookup_SelfCall_IsSkipped()
    {
        // Arrange — recursive method: Recurse calls itself.
        // Self-calls are skipped because the Roles dict cannot carry both
        // "caller" and "callee" for the same node ID.
        var methodDef = CreateNode("node-recurse", "Recurse", "method", scope: "Service");
        var callRef = CreateNode("node-recurse-ref", "Recurse", "method_ref",
            scope: "Service.Recurse");

        var data = new HypergraphData
        {
            Nodes =
            {
                [methodDef.Id] = methodDef,
                [callRef.Id] = callRef,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-self-reverse",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callRef.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callRef.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — self-call edge is NOT emitted (canonical role contract limitation)
        result.EdgesCreated.Should().Be(0);
        result.CallsEdges.Should().Be(0);
        this.store.UpsertedEdges.Should().BeEmpty(
            because: "self-calls are skipped — Roles dict cannot represent caller+callee for same node");
    }

    [TestMethod]
    public async Task FindCallersOf_IgnoresNonCallsEdges()
    {
        // Arrange — mix of CALLS and EXTENDS edges
        var nodeA = CreateNode("node-a", "MethodA", "method", scope: "ClassA");
        var nodeB = CreateNode("node-b", "MethodB", "method", scope: "ClassB");
        var nodeC = CreateNode("node-c", "ClassC", "class");
        var nodeD = CreateNode("node-d", "ClassD", "class");

        var data = new HypergraphData
        {
            Nodes =
            {
                [nodeA.Id] = nodeA, [nodeB.Id] = nodeB,
                [nodeC.Id] = nodeC, [nodeD.Id] = nodeD,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-call",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [nodeA.Id, nodeB.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeA.Id] = "caller",
                        [nodeB.Id] = "callee",
                    },
                },
                new Hyperedge
                {
                    Id = "he-ext",
                    EdgeType = HyperedgeType.Extends,
                    NodeIds = [nodeC.Id, nodeD.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeC.Id] = "subclass",
                        [nodeD.Id] = "superclass",
                    },
                },
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);
        result.EdgesCreated.Should().Be(2);

        var allEdges = this.store.UpsertedEdges.Select(e => e.Edge).ToList();

        // Assert — FindCallersOf only looks at CALLS edges, ignoring EXTENDS
        var callersOfD = CallChainAnalyzer.FindCallersOf(nodeD.Id, allEdges);
        callersOfD.Should().BeEmpty(
            because: "EXTENDS edges are not CALLS edges and should not appear in caller lookup");
    }

    // ── OriginalEdgeType stores HyperedgeType enum value ────────────────

    [TestMethod]
    public async Task OriginalEdgeType_StoresHyperedgeTypeEnumValue_NotString()
    {
        var callerNode = CreateNode("node-caller", "Caller", "method", scope: "Service");
        var calleeNode = CreateNode("node-callee", "Callee", "method", scope: "Repository");

        var data = new HypergraphData
        {
            Nodes = { [callerNode.Id] = callerNode, [calleeNode.Id] = calleeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-enum-check",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callerNode.Id, calleeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callerNode.Id] = "caller",
                        [calleeNode.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — OriginalEdgeType must be the HyperedgeType enum, not its ToString()
        result.EdgesCreated.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        var originalEdgeType = edge.Metadata[MemoryMetadataKeys.OriginalEdgeType];
        originalEdgeType.Should().BeOfType<HyperedgeType>();
        originalEdgeType.Should().Be(HyperedgeType.MethodInvocation);
    }

    // ── Self-edge handling ──────────────────────────────────────────────

    [TestMethod]
    public async Task SelfCall_IsSkipped_CannotRepresentCallerAndCalleeForSameNode()
    {
        // Arrange — realistic recursive call:
        // method_ref node at call-site resolves to same method definition.
        // Self-calls are skipped because the Roles dictionary is keyed by node ID
        // and cannot carry both "caller" and "callee" for the same node.
        var methodDef = CreateNode("node-recurse", "Recurse", "method", scope: "Service");
        var callRef = CreateNode("node-recurse-ref", "Recurse", "method_ref",
            scope: "Service.Recurse");

        var data = new HypergraphData
        {
            Nodes =
            {
                [methodDef.Id] = methodDef,
                [callRef.Id] = callRef,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-self",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callRef.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callRef.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — self-call edge is NOT emitted
        result.EdgesCreated.Should().Be(0);
        result.CallsEdges.Should().Be(0);
        this.store.UpsertedEdges.Should().BeEmpty(
            because: "self-calls are skipped to maintain canonical caller/callee role semantics");
    }

    // ── Persistence failure counter consistency ─────────────────────────

    [TestMethod]
    public async Task PersistenceFailure_PropagatesException()
    {
        // Arrange — use a store configured to fail on persist
        var failingStore = new FakeMemoryGraphStore { ShouldFailOnUpsertEdge = true };
        var failAnalyzer = new CallChainAnalyzer(
            failingStore,
            NullLogger<CallChainAnalyzer>.Instance);

        var callerNode = CreateNode("node-caller", "Caller", "method", scope: "ServiceA");
        var calleeNode = CreateNode("node-callee", "Callee", "method", scope: "ServiceB");

        var data = new HypergraphData
        {
            Nodes = { [callerNode.Id] = callerNode, [calleeNode.Id] = calleeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-fail",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callerNode.Id, calleeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callerNode.Id] = "caller",
                        [calleeNode.Id] = "callee",
                    },
                }
            }
        };

        // Act & Assert — persistence failure propagates as exception, not partial success
        var act = () => failAnalyzer.AnalyzeAsync(data, this.version);
        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "persistence failures must propagate, not be silently swallowed");
    }

    // ── Node resolution ─────────────────────────────────────────────────

    [TestMethod]
    public async Task MethodRefNode_ResolvedToCanonicalDefinition()
    {
        // Arrange — method_ref from extractor + matching definition
        var callerDef = CreateNode("node-process", "Process", "method", scope: "ServiceA");
        var methodRef = CreateNode("node-save-ref", "Save", "method_ref",
            scope: "ServiceA.Process");
        var saveDef = CreateNode("node-save-def", "Save", "method", scope: "RepositoryB");

        var data = new HypergraphData
        {
            Nodes =
            {
                [callerDef.Id] = callerDef,
                [methodRef.Id] = methodRef,
                [saveDef.Id] = saveDef,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-resolve",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [methodRef.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [methodRef.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — callee should be resolved to saveDef
        result.EdgesCreated.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.NodeIds.Should().Contain(saveDef.Id,
            because: "method_ref nodes should resolve to canonical definitions when unambiguous");
    }

    [TestMethod]
    public async Task AmbiguousMethodRef_NotResolved_KeepsOriginalNodeId()
    {
        // Arrange — two definitions with same name, resolution should NOT pick arbitrarily
        var callerDef = CreateNode("node-caller", "Caller", "method", scope: "ServiceA");
        var methodRef = CreateNode("node-save-ref", "Save", "method_ref",
            scope: "ServiceA.Caller");
        var saveDef1 = CreateNode("node-save-1", "Save", "method", scope: "RepoA");
        var saveDef2 = CreateNode("node-save-2", "Save", "method", scope: "RepoB");

        var data = new HypergraphData
        {
            Nodes =
            {
                [callerDef.Id] = callerDef,
                [methodRef.Id] = methodRef,
                [saveDef1.Id] = saveDef1,
                [saveDef2.Id] = saveDef2,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-ambig",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [methodRef.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [methodRef.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — edge created but callee stays as method_ref (unresolved)
        result.EdgesCreated.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.NodeIds.Should().Contain(methodRef.Id,
            because: "ambiguous resolution should keep original node ID");
    }

    // ── Unrecognized edge types are ignored ─────────────────────────────

    [TestMethod]
    public async Task UnrecognizedEdgeType_IsIgnored()
    {
        var nodeA = CreateNode("node-a", "Foo", "class");
        var nodeB = CreateNode("node-b", "Bar", "class");

        var data = new HypergraphData
        {
            Nodes = { [nodeA.Id] = nodeA, [nodeB.Id] = nodeB },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-unknown",
                    EdgeType = HyperedgeType.PropertyDefinition,
                    NodeIds = [nodeA.Id, nodeB.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeA.Id] = "class",
                        [nodeB.Id] = "property",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — PropertyDefinition is not mapped by CallChainAnalyzer
        result.EdgesCreated.Should().Be(0);
        this.store.UpsertedEdges.Should().BeEmpty();
    }

    // ── MemoryLayer metadata ────────────────────────────────────────────

    [TestMethod]
    public async Task AllEdges_HaveCallChainMemoryLayer()
    {
        var nodeA = CreateNode("node-a", "ClassA", "class");
        var nodeB = CreateNode("node-b", "ClassB", "class");

        var data = new HypergraphData
        {
            Nodes = { [nodeA.Id] = nodeA, [nodeB.Id] = nodeB },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-ext",
                    EdgeType = HyperedgeType.Extends,
                    NodeIds = [nodeA.Id, nodeB.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeA.Id] = "subclass",
                        [nodeB.Id] = "superclass",
                    },
                }
            }
        };

        // Act
        await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("call_chain");
    }

    // ── ClassDefinition/InterfaceDefinition/StructDefinition produce inheritance edges ──
    // Real extractors encode inheritance via definition hyperedges with indexed roles.
    // CallChainAnalyzer extracts EXTENDS/IMPLEMENTS edges from these shapes.

    [TestMethod]
    public async Task ClassDefinition_WithBaseClass_ProducesExtendsEdge()
    {
        // Arrange — ClassDefinition as produced by CSharpHyperedgeExtractor:
        // PaymentController : ControllerBase → base_0 role, node type "class"
        var paymentCtrl = CreateNode("node-payment", "PaymentController", "class");
        var controllerBase = CreateNode("node-base", "ControllerBase", "class");

        var data = new HypergraphData
        {
            Nodes = { [paymentCtrl.Id] = paymentCtrl, [controllerBase.Id] = controllerBase },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-classdef",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [paymentCtrl.Id, controllerBase.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [paymentCtrl.Id] = "class",
                        [controllerBase.Id] = "base_0",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — ClassDefinition with base_0 role produces EXTENDS edge
        result.EdgesCreated.Should().Be(1);
        result.ExtendsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles[paymentCtrl.Id].Should().Be("subclass");
        edge.Roles[controllerBase.Id].Should().Be("superclass");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("EXTENDS");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.ClassDefinition);
    }

    [TestMethod]
    public async Task ClassDefinition_WithBaseInterface_ProducesImplementsEdge()
    {
        // Arrange — ClassDefinition where base type has NodeType "interface"
        // This matches C# extractor heuristic: IFoo → interface node type
        var serviceClass = CreateNode("node-service", "UserService", "class");
        var iService = CreateNode("node-iservice", "IUserService", "interface");

        var data = new HypergraphData
        {
            Nodes = { [serviceClass.Id] = serviceClass, [iService.Id] = iService },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-classdef-impl",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [serviceClass.Id, iService.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [serviceClass.Id] = "class",
                        [iService.Id] = "base_0",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — base_0 with NodeType "interface" → IMPLEMENTS
        result.EdgesCreated.Should().Be(1);
        result.ImplementsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles[serviceClass.Id].Should().Be("implementor");
        edge.Roles[iService.Id].Should().Be("interface");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("IMPLEMENTS");
    }

    [TestMethod]
    public async Task ClassDefinition_WithMixedBaseTypes_ProducesBothExtendsAndImplements()
    {
        // Arrange — C# class: OrderService : BaseService, IOrderService
        // base_0 = class → EXTENDS, base_1 = interface → IMPLEMENTS
        var orderSvc = CreateNode("node-ordersvc", "OrderService", "class");
        var baseSvc = CreateNode("node-basesvc", "BaseService", "class");
        var iOrderSvc = CreateNode("node-iordersvc", "IOrderService", "interface");

        var data = new HypergraphData
        {
            Nodes =
            {
                [orderSvc.Id] = orderSvc,
                [baseSvc.Id] = baseSvc,
                [iOrderSvc.Id] = iOrderSvc,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-classdef-mixed",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [orderSvc.Id, baseSvc.Id, iOrderSvc.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [orderSvc.Id] = "class",
                        [baseSvc.Id] = "base_0",
                        [iOrderSvc.Id] = "base_1",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — one EXTENDS (class base) + one IMPLEMENTS (interface base)
        result.EdgesCreated.Should().Be(2);
        result.ExtendsEdges.Should().Be(1);
        result.ImplementsEdges.Should().Be(1);

        var extendsEdge = this.store.UpsertedEdges
            .First(e => (string)e.Edge.Metadata[MemoryMetadataKeys.SemanticRelation]! == "EXTENDS").Edge;
        extendsEdge.Roles[orderSvc.Id].Should().Be("subclass");
        extendsEdge.Roles[baseSvc.Id].Should().Be("superclass");

        var implEdge = this.store.UpsertedEdges
            .First(e => (string)e.Edge.Metadata[MemoryMetadataKeys.SemanticRelation]! == "IMPLEMENTS").Edge;
        implEdge.Roles[orderSvc.Id].Should().Be("implementor");
        implEdge.Roles[iOrderSvc.Id].Should().Be("interface");
    }

    [TestMethod]
    public async Task ClassDefinition_EnumRole_IsIgnored()
    {
        // Arrange — PowerShell ClassDefinition with "enum" role (not "class")
        var enumNode = CreateNode("node-enum", "MyEnum", "enum");
        var memberNode = CreateNode("node-member", "Value1", "field");

        var data = new HypergraphData
        {
            Nodes = { [enumNode.Id] = enumNode, [memberNode.Id] = memberNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-enumdef",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [enumNode.Id, memberNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [enumNode.Id] = "enum",
                        [memberNode.Id] = "member_0",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — enum definition has no "class" primary role → no edges
        result.EdgesCreated.Should().Be(0);
    }

    [TestMethod]
    public async Task InterfaceDefinition_WithExtendsRoles_ProducesExtendsEdge()
    {
        // Arrange — InterfaceDefinition as produced by CSharpHyperedgeExtractor:
        // IOrderService : IService → extends_0 role
        var iOrderSvc = CreateNode("iface-ordersvc", "IOrderService", "interface");
        var iService = CreateNode("iface-svc", "IService", "interface");

        var data = new HypergraphData
        {
            Nodes = { [iOrderSvc.Id] = iOrderSvc, [iService.Id] = iService },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-ifacedef-ext",
                    EdgeType = HyperedgeType.InterfaceDefinition,
                    NodeIds = [iOrderSvc.Id, iService.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [iOrderSvc.Id] = "interface",
                        [iService.Id] = "extends_0",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — InterfaceDefinition with extends_0 produces EXTENDS edge
        result.EdgesCreated.Should().Be(1);
        result.ExtendsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles[iOrderSvc.Id].Should().Be("subclass");
        edge.Roles[iService.Id].Should().Be("superclass");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("EXTENDS");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.InterfaceDefinition);
    }

    [TestMethod]
    public async Task StructDefinition_WithImplementsRoles_ProducesImplementsEdge()
    {
        // Arrange — StructDefinition as produced by CSharpHyperedgeExtractor:
        // Point : IEquatable<Point> → implements_0 role
        var pointStruct = CreateNode("struct-point", "Point", "struct");
        var iEquatable = CreateNode("iface-eq", "IEquatable", "interface");

        var data = new HypergraphData
        {
            Nodes = { [pointStruct.Id] = pointStruct, [iEquatable.Id] = iEquatable },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-structdef-impl",
                    EdgeType = HyperedgeType.StructDefinition,
                    NodeIds = [pointStruct.Id, iEquatable.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [pointStruct.Id] = "struct",
                        [iEquatable.Id] = "implements_0",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — StructDefinition with implements_0 produces IMPLEMENTS edge
        result.EdgesCreated.Should().Be(1);
        result.ImplementsEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles[pointStruct.Id].Should().Be("implementor");
        edge.Roles[iEquatable.Id].Should().Be("interface");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("IMPLEMENTS");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.StructDefinition);
    }

    // ── Assignment REFERENCES edge ──────────────────────────────────────

    [TestMethod]
    public async Task ReferencesEdge_FromAssignment()
    {
        var valueNode = CreateNode("node-val", "config", "method");
        var targetNode = CreateNode("node-tgt", "settings", "method");

        var data = new HypergraphData
        {
            Nodes = { [valueNode.Id] = valueNode, [targetNode.Id] = targetNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-assign",
                    EdgeType = HyperedgeType.Assignment,
                    NodeIds = [valueNode.Id, targetNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [valueNode.Id] = "value",
                        [targetNode.Id] = "target",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        result.EdgesCreated.Should().Be(1);
        result.ReferencesEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles[valueNode.Id].Should().Be("source");
        edge.Roles[targetNode.Id].Should().Be("target");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("REFERENCES");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.Assignment);
    }

    [TestMethod]
    public async Task ReferencesEdge_FromAssignment_FieldAndFieldType()
    {
        // Arrange — field declaration with "field"/"field_type" roles
        // as produced by CSharpHyperedgeExtractor.ExtractFieldDefinition
        var fieldNode = CreateNode("node-field", "logger", "field");
        var typeNode = CreateNode("node-type", "ILogger", "type");

        var data = new HypergraphData
        {
            Nodes = { [fieldNode.Id] = fieldNode, [typeNode.Id] = typeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-field-assign",
                    EdgeType = HyperedgeType.Assignment,
                    NodeIds = [fieldNode.Id, typeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [fieldNode.Id] = "field",
                        [typeNode.Id] = "field_type",
                    },
                    Metadata = new Dictionary<string, object?> { ["is_field"] = true },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — field/field_type roles are recognized as REFERENCES edges
        result.EdgesCreated.Should().Be(1);
        result.ReferencesEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Roles[typeNode.Id].Should().Be("source");
        edge.Roles[fieldNode.Id].Should().Be("target");
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("REFERENCES");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.Assignment);
    }

    // ── CrossLanguageTypeReference REFERENCES edge ──────────────────────

    [TestMethod]
    public async Task ReferencesEdge_FromCrossLanguageTypeReference()
    {
        var psRef = CreateNode("node-ps-type", "DataServiceProxy", "reference");
        var dotnetDef = CreateNode("node-cs-type", "DataService", "class");

        var data = new HypergraphData
        {
            Nodes = { [psRef.Id] = psRef, [dotnetDef.Id] = dotnetDef },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-xref",
                    EdgeType = HyperedgeType.CrossLanguageTypeReference,
                    NodeIds = [psRef.Id, dotnetDef.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [psRef.Id] = "ps_reference",
                        [dotnetDef.Id] = "dotnet_definition",
                    },
                    Languages = ["powershell", "csharp"],
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert
        result.EdgesCreated.Should().Be(1);
        result.ReferencesEdges.Should().Be(1);

        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("REFERENCES");
        edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.CrossLanguageTypeReference);
    }

    // ── Dynamic dispatch confidence ─────────────────────────────────────

    [TestMethod]
    public async Task DynamicDispatch_HasReducedConfidence()
    {
        var callerNode = CreateNode("node-caller", "Process", "method", scope: "Handler");
        var calleeNode = CreateNode("node-callee", "Handle", "method", scope: "IHandler");

        var data = new HypergraphData
        {
            Nodes = { [callerNode.Id] = callerNode, [calleeNode.Id] = calleeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-virtual",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callerNode.Id, calleeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callerNode.Id] = "caller",
                        [calleeNode.Id] = "callee",
                    },
                    Metadata = new Dictionary<string, object?> { ["IsVirtualDispatch"] = true },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — virtual dispatch gets 0.5 confidence
        result.EdgesCreated.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(0.5);
    }

    [TestMethod]
    public async Task DynamicDispatch_IsDynamic_HasReducedConfidence()
    {
        var callerNode = CreateNode("node-caller", "Invoke", "method", scope: "Service");
        var calleeNode = CreateNode("node-callee", "Execute", "method", scope: "Target");

        var data = new HypergraphData
        {
            Nodes = { [callerNode.Id] = callerNode, [calleeNode.Id] = calleeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-dynamic",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callerNode.Id, calleeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callerNode.Id] = "caller",
                        [calleeNode.Id] = "callee",
                    },
                    Metadata = new Dictionary<string, object?> { ["IsDynamicDispatch"] = true },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — dynamic dispatch gets 0.5 confidence
        result.EdgesCreated.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(0.5);
    }

    [TestMethod]
    public async Task DynamicDispatch_VirtualModifier_HasReducedConfidence()
    {
        // Arrange — callee is a virtual method, no upstream metadata flags
        var callerNode = CreateNode("node-caller-vm", "Execute", "method", scope: "ServiceA");
        var calleeNode = new HypergraphNode
        {
            Id = "node-callee-virtual",
            Name = "Process",
            NodeType = "method",
            Language = "csharp",
            FilePath = "src/test.cs",
            Scope = "ServiceB",
            Modifiers = ["public", "virtual"],
        };

        var data = new HypergraphData
        {
            Nodes = { [callerNode.Id] = callerNode, [calleeNode.Id] = calleeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-virtual-dispatch",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callerNode.Id, calleeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callerNode.Id] = "caller",
                        [calleeNode.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — virtual method gets dynamic dispatch confidence (0.5)
        result.EdgesCreated.Should().Be(1);
        var edge = this.store.UpsertedEdges[0].Edge;
        edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(0.5);
    }

    [TestMethod]
    public async Task DataFlow_OneDefMultipleUses_EmitsAllReferencesEdges()
    {
        // Arrange — 1 definition flowing to 3 uses
        var defNode = CreateNode("node-def", "data", "method");
        var use1 = CreateNode("node-use1", "result1", "method");
        var use2 = CreateNode("node-use2", "result2", "method");
        var use3 = CreateNode("node-use3", "result3", "method");

        var data = new HypergraphData
        {
            Nodes =
            {
                [defNode.Id] = defNode,
                [use1.Id] = use1,
                [use2.Id] = use2,
                [use3.Id] = use3,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-dataflow-nary",
                    EdgeType = HyperedgeType.DataFlow,
                    NodeIds = [defNode.Id, use1.Id, use2.Id, use3.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [defNode.Id] = "def_0",
                        [use1.Id] = "use_0",
                        [use2.Id] = "use_1",
                        [use3.Id] = "use_2",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — Cartesian product: 1 def × 3 uses = 3 REFERENCES edges
        result.EdgesCreated.Should().Be(3);
        result.ReferencesEdges.Should().Be(3);
        this.store.UpsertedEdges.Should().HaveCount(3);

        // All edges should have the def node as source
        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            edge.Roles.Values.Should().Contain("source");
            edge.Roles.Values.Should().Contain("target");
            edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("REFERENCES");
        }
    }

    // ── N-ary inheritance decomposition ────────────────────────────────

    [TestMethod]
    public async Task ImplementsEdge_NAry_ClassImplementingMultipleInterfaces()
    {
        // Arrange — class implementing 3 interfaces via a single n-ary Implements edge
        var impl = CreateNode("node-svc", "OrderService", "class");
        var iface1 = CreateNode("node-i1", "IOrderService", "interface");
        var iface2 = CreateNode("node-i2", "IDisposable", "interface");
        var iface3 = CreateNode("node-i3", "IAsyncDisposable", "interface");

        var data = new HypergraphData
        {
            Nodes =
            {
                [impl.Id] = impl,
                [iface1.Id] = iface1,
                [iface2.Id] = iface2,
                [iface3.Id] = iface3,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-impl-nary",
                    EdgeType = HyperedgeType.Implements,
                    NodeIds = [impl.Id, iface1.Id, iface2.Id, iface3.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [impl.Id] = "implementor",
                        [iface1.Id] = "interface_0",
                        [iface2.Id] = "interface_1",
                        [iface3.Id] = "interface_2",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — one IMPLEMENTS edge per interface: 3 binary edges
        result.EdgesCreated.Should().Be(3);
        result.ImplementsEdges.Should().Be(3);
        this.store.UpsertedEdges.Should().HaveCount(3);

        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            edge.NodeIds.Should().HaveCount(2);
            edge.Roles.Values.Should().Contain("implementor");
            edge.Roles.Values.Should().Contain("interface");
            edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("IMPLEMENTS");
        }

        // All edges should reference the implementor
        var implementorIds = this.store.UpsertedEdges.Select(e => e.Edge.NodeIds[0]).ToList();
        implementorIds.Should().AllBe(impl.Id);

        // Verify all three interfaces are covered
        var ifaceIds = this.store.UpsertedEdges.Select(e => e.Edge.NodeIds[1]).ToList();
        ifaceIds.Should().Contain(iface1.Id);
        ifaceIds.Should().Contain(iface2.Id);
        ifaceIds.Should().Contain(iface3.Id);
    }

    [TestMethod]
    public async Task ExtendsEdge_NAry_PositionalFallbackWithMultipleTargets()
    {
        // Arrange — n-ary Extends edge without named roles: first node = subclass,
        // remaining = superclasses (for languages with multiple inheritance)
        var child = CreateNode("node-child", "Child", "class");
        var parent1 = CreateNode("node-parent1", "Parent1", "class");
        var parent2 = CreateNode("node-parent2", "Parent2", "class");

        var data = new HypergraphData
        {
            Nodes =
            {
                [child.Id] = child,
                [parent1.Id] = parent1,
                [parent2.Id] = parent2,
            },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-ext-nary",
                    EdgeType = HyperedgeType.Extends,
                    NodeIds = [child.Id, parent1.Id, parent2.Id],
                    Roles = new Dictionary<string, string>(),
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — positional n-ary: first = subclass, rest = superclasses
        result.EdgesCreated.Should().Be(2);
        result.ExtendsEdges.Should().Be(2);

        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            edge.Roles.Values.Should().Contain("subclass");
            edge.Roles.Values.Should().Contain("superclass");
            edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("EXTENDS");
        }
    }

    // ── Role-based reverse lookup (non-positional) ──────────────────────

    [TestMethod]
    public async Task FindCallersOf_RoleBased_WorksRegardlessOfNodeIdsOrder()
    {
        // Arrange — construct CALLS edges with roles, then verify FindCallersOf
        // uses Roles dict (not NodeIds position) for lookup
        var nodeA = CreateNode("node-a", "MethodA", "method", scope: "ClassA");
        var nodeB = CreateNode("node-b", "MethodB", "method", scope: "ClassB");

        var data = new HypergraphData
        {
            Nodes = { [nodeA.Id] = nodeA, [nodeB.Id] = nodeB },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-role-test",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [nodeA.Id, nodeB.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodeA.Id] = "caller",
                        [nodeB.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);
        result.EdgesCreated.Should().Be(1);

        var callsEdges = this.store.UpsertedEdges.Select(e => e.Edge).ToList();

        // Assert — FindCallersOf correctly identifies caller from Roles
        var callersOfB = CallChainAnalyzer.FindCallersOf(nodeB.Id, callsEdges);
        callersOfB.Should().HaveCount(1);
        callersOfB.Should().Contain(nodeA.Id);

        // A is not a callee of anyone
        var callersOfA = CallChainAnalyzer.FindCallersOf(nodeA.Id, callsEdges);
        callersOfA.Should().BeEmpty();
    }

    // ── Integration-style test: realistic extractor output ──────────────
    // Simulates the complete output from CSharpHyperedgeExtractor for a
    // small class hierarchy with method calls. Uses ClassDefinition with
    // base_X roles for inheritance — the actual shape from real extractors.

    [TestMethod]
    public async Task IntegrationStyle_RealExtractorOutput_ProducesCorrectCallChainGraph()
    {
        // Arrange — simulates extractor output for:
        //   public class OrderService : BaseService, IOrderService
        //   {
        //       public void Process() { this.repo.Save(order); }
        //   }
        //   public class BaseService { }
        //   public interface IOrderService { void Process(); }
        //   public class OrderRepository { public void Save(Order o) { } }

        // Definition nodes (as produced by the C# extractor)
        var orderSvcClass = CreateNode("cls-ordersvc", "OrderService", "class", scope: "App.Services");
        var baseSvcClass = CreateNode("cls-basesvc", "BaseService", "class", scope: "App.Services");
        var iOrderSvc = CreateNode("cls-iordersvc", "IOrderService", "interface", scope: "App.Services");
        var processMethod = CreateNode("meth-process", "Process", "method", scope: "App.Services.OrderService");
        var repoClass = CreateNode("cls-repo", "OrderRepository", "class", scope: "App.Data");
        var saveMethod = CreateNode("meth-save", "Save", "method", scope: "App.Data.OrderRepository");

        // Reference nodes from call-site (as produced by extractor)
        var saveRef = CreateNode("ref-save", "repo.Save", "method_ref",
            scope: "App.Services.OrderService.Process");
        var orderArg = CreateNode("ref-order", "order", "expression",
            scope: "App.Services.OrderService.Process");

        var data = new HypergraphData
        {
            Nodes =
            {
                [orderSvcClass.Id] = orderSvcClass,
                [baseSvcClass.Id] = baseSvcClass,
                [iOrderSvc.Id] = iOrderSvc,
                [processMethod.Id] = processMethod,
                [repoClass.Id] = repoClass,
                [saveMethod.Id] = saveMethod,
                [saveRef.Id] = saveRef,
                [orderArg.Id] = orderArg,
            },
            Hyperedges =
            {
                // ClassDefinition: OrderService : BaseService, IOrderService
                // This is the actual shape from CSharpHyperedgeExtractor —
                // inheritance is encoded via base_0/base_1 roles.
                new Hyperedge
                {
                    Id = "he-classdef-ordersvc",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [orderSvcClass.Id, baseSvcClass.Id, iOrderSvc.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [orderSvcClass.Id] = "class",
                        [baseSvcClass.Id] = "base_0",
                        [iOrderSvc.Id] = "base_1",
                    },
                },
                // MethodInvocation: this.repo.Save(order) within Process()
                new Hyperedge
                {
                    Id = "he-call-save",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [saveRef.Id, orderArg.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [saveRef.Id] = "callee",
                        [orderArg.Id] = "arg_0",
                    },
                },
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — should produce:
        // 1x EXTENDS (OrderService → BaseService)
        // 1x IMPLEMENTS (OrderService → IOrderService)
        // 1x CALLS (Process → Save)
        result.EdgesCreated.Should().Be(3);
        result.ExtendsEdges.Should().Be(1);
        result.ImplementsEdges.Should().Be(1);
        result.CallsEdges.Should().Be(1);

        // Verify EXTENDS edge
        var extendsEdges = this.store.UpsertedEdges
            .Where(e => (string)e.Edge.Metadata[MemoryMetadataKeys.SemanticRelation]! == "EXTENDS")
            .ToList();
        extendsEdges.Should().HaveCount(1);
        extendsEdges[0].Edge.NodeIds.Should().Contain(orderSvcClass.Id);
        extendsEdges[0].Edge.NodeIds.Should().Contain(baseSvcClass.Id);
        extendsEdges[0].Edge.Roles[orderSvcClass.Id].Should().Be("subclass");
        extendsEdges[0].Edge.Roles[baseSvcClass.Id].Should().Be("superclass");

        // Verify IMPLEMENTS edge
        var implEdges = this.store.UpsertedEdges
            .Where(e => (string)e.Edge.Metadata[MemoryMetadataKeys.SemanticRelation]! == "IMPLEMENTS")
            .ToList();
        implEdges.Should().HaveCount(1);
        implEdges[0].Edge.NodeIds.Should().Contain(orderSvcClass.Id);
        implEdges[0].Edge.NodeIds.Should().Contain(iOrderSvc.Id);
        implEdges[0].Edge.Roles[orderSvcClass.Id].Should().Be("implementor");
        implEdges[0].Edge.Roles[iOrderSvc.Id].Should().Be("interface");

        // Verify CALLS edge — caller resolved from scope, callee resolved to definition
        var callsEdges = this.store.UpsertedEdges
            .Where(e => (string)e.Edge.Metadata[MemoryMetadataKeys.SemanticRelation]! == "CALLS")
            .ToList();
        callsEdges.Should().HaveCount(1);
        callsEdges[0].Edge.Roles.Values.Should().Contain("caller");
        callsEdges[0].Edge.Roles.Values.Should().Contain("callee");
        callsEdges[0].Edge.Metadata[MemoryMetadataKeys.Confidence].Should().Be(1.0);

        // The callee should resolve to the Save definition (not the ref node)
        callsEdges[0].Edge.NodeIds.Should().Contain(saveMethod.Id);

        // The caller should be Process (resolved from scope)
        callsEdges[0].Edge.NodeIds.Should().Contain(processMethod.Id);

        // All edges use SemanticRelation as the sole filtering mechanism
        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.SemanticRelation);
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.OriginalEdgeType);
            edge.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer);
            edge.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("call_chain");
        }

        // No reverse-call edges stored
        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            var sr = (string)edge.Metadata[MemoryMetadataKeys.SemanticRelation]!;
            sr.Should().BeOneOf("CALLS", "EXTENDS", "IMPLEMENTS");
            sr.Should().NotContain("REVERSE");
        }

        // Reverse lookup works by backward traversal of CALLS edges
        var callers = CallChainAnalyzer.FindCallersOf(
            saveMethod.Id,
            callsEdges.Select(e => e.Edge));
        callers.Should().Contain(processMethod.Id);

        // Raw nodes are NOT upserted to the store (owned by StructureBuilder)
        this.store.UpsertedNodes.Should().BeEmpty(
            because: "CallChainAnalyzer should not upsert raw parser nodes — StructureBuilder owns them");
    }

    // ── Integration-style: PowerShell CmdletCall chain ──────────────────

    [TestMethod]
    public async Task IntegrationStyle_PowerShellCmdletCalls_AlwaysEmitCallsEdges()
    {
        // Arrange — simulates PowerShellHyperedgeExtractor output for:
        //   # deploy.ps1 (top-level script)
        //   Get-Item -Path "C:\app"
        //   Set-Location -Path "C:\app"
        //   function Invoke-Deploy { Get-ChildItem }
        var funcNode = CreateNode("func-deploy", "Invoke-Deploy", "function",
            filePath: "scripts/deploy.ps1");
        var getItemNode = CreateNode("cmd-get-item", "Get-Item", "cmdlet",
            filePath: "scripts/deploy.ps1");
        var pathParam1 = CreateNode("param-path1", "-Path", "param_name",
            filePath: "scripts/deploy.ps1");
        var setLocNode = CreateNode("cmd-set-loc", "Set-Location", "cmdlet",
            filePath: "scripts/deploy.ps1");
        var pathParam2 = CreateNode("param-path2", "-Path", "param_name",
            filePath: "scripts/deploy.ps1");
        var getChildNode = CreateNode("cmd-get-child", "Get-ChildItem", "cmdlet",
            filePath: "scripts/deploy.ps1");

        var data = new HypergraphData
        {
            Nodes =
            {
                [funcNode.Id] = funcNode,
                [getItemNode.Id] = getItemNode,
                [pathParam1.Id] = pathParam1,
                [setLocNode.Id] = setLocNode,
                [pathParam2.Id] = pathParam2,
                [getChildNode.Id] = getChildNode,
            },
            Hyperedges =
            {
                // Top-level CmdletCall: Get-Item (no function scope)
                new Hyperedge
                {
                    Id = "he-ps-getitem",
                    EdgeType = HyperedgeType.CmdletCall,
                    NodeIds = [getItemNode.Id, pathParam1.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [getItemNode.Id] = "cmdlet",
                        [pathParam1.Id] = "param_Path",
                    },
                },
                // Top-level CmdletCall: Set-Location (no function scope)
                new Hyperedge
                {
                    Id = "he-ps-setloc",
                    EdgeType = HyperedgeType.CmdletCall,
                    NodeIds = [setLocNode.Id, pathParam2.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [setLocNode.Id] = "cmdlet",
                        [pathParam2.Id] = "param_Path",
                    },
                },
                // CmdletCall inside function: Get-ChildItem (scope = Invoke-Deploy)
                new Hyperedge
                {
                    Id = "he-ps-getchild",
                    EdgeType = HyperedgeType.CmdletCall,
                    NodeIds = [getChildNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [getChildNode.Id] = "cmdlet",
                    },
                },
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — all 3 CmdletCall hyperedges should emit CALLS edges.
        // Top-level cmdlets use the synthesized file node as caller;
        // Get-ChildItem uses a file node since it has no scope either.
        result.CallsEdges.Should().Be(3);
        result.EdgesCreated.Should().Be(3);

        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("CALLS");
            edge.Roles.Values.Should().Contain("caller");
            edge.Roles.Values.Should().Contain("callee");
        }
    }

    // ── Raw nodes are NOT upserted; only synthetic nodes are ──────────────

    [TestMethod]
    public async Task RawNodesAreNotUpserted_OnlySyntheticNodesAre()
    {
        // Arrange — simple call edge between raw parser nodes
        var callerNode = CreateNode("node-caller", "Caller", "method", scope: "ServiceA");
        var calleeNode = CreateNode("node-callee", "Callee", "method", scope: "ServiceB");

        var data = new HypergraphData
        {
            Nodes = { [callerNode.Id] = callerNode, [calleeNode.Id] = calleeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-upsert-nodes",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [callerNode.Id, calleeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [callerNode.Id] = "caller",
                        [calleeNode.Id] = "callee",
                    },
                }
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — raw nodes are NOT upserted (they are owned by StructureBuilder)
        result.EdgesCreated.Should().Be(1);
        this.store.UpsertedNodes.Should().BeEmpty(
            because: "CallChainAnalyzer should not upsert raw parser nodes — StructureBuilder owns them per §3.11");
    }

    // ── Integration: Full extractor-shaped graph with canonical IDs ─────
    // Exercises ClassDefinition, InterfaceDefinition, StructDefinition,
    // MethodInvocation, and ModuleFunctionCall with the same shapes
    // produced by real extractors. Definition hyperedges encode inheritance
    // via base_X/extends_X/implements_X roles.

    [TestMethod]
    public async Task IntegrationStyle_AllExtractorShapes_CanonicalStructuralIDs()
    {
        // Arrange — simulates real extractor output for a small codebase:
        //
        //   public interface IRepository { void Save(Order o); }
        //   public interface IAuditable : IRepository { }
        //   public struct CacheKey : IEquatable<CacheKey> { }
        //   public class OrderRepository : IRepository
        //   {
        //       public void Save(Order o) { this.Validate(o); }
        //       private void Validate(Order o) { }
        //   }
        //   # deploy.ps1
        //   Microsoft.PowerShell.Management\Get-ChildItem -Path C:\

        // ── Definition nodes (canonical structural IDs) ──
        var iRepoIface = CreateNode("def:IRepository", "IRepository", "interface",
            scope: "App.Data");
        var iAuditIface = CreateNode("def:IAuditable", "IAuditable", "interface",
            scope: "App.Data");
        var cacheKeyStruct = CreateNode("def:CacheKey", "CacheKey", "struct",
            scope: "App.Data");
        var iEquatable = CreateNode("def:IEquatable", "IEquatable", "interface",
            scope: "System");
        var orderRepoClass = CreateNode("def:OrderRepository", "OrderRepository", "class",
            scope: "App.Data");
        var saveMethod = CreateNode("def:OrderRepository.Save", "Save", "method",
            scope: "App.Data.OrderRepository");
        var validateMethod = CreateNode("def:OrderRepository.Validate", "Validate", "method",
            scope: "App.Data.OrderRepository");

        // ── Reference nodes from call-site (extractor raw output) ──
        var validateRef = CreateNode("ref:Validate@Save", "this.Validate", "method_ref",
            scope: "App.Data.OrderRepository.Save");
        var orderArg = CreateNode("ref:o@Save", "o", "expression",
            scope: "App.Data.OrderRepository.Save");

        // ── PowerShell nodes ──
        var psModuleNode = CreateNode("ps:module:Management", "Microsoft.PowerShell.Management", "module",
            filePath: "scripts/deploy.ps1");
        var psCalledFunc = CreateNode("ps:func:Get-ChildItem", "Get-ChildItem", "function_ref",
            filePath: "scripts/deploy.ps1");
        var psPathParam = CreateNode("ps:param:Path", "-Path", "param_name",
            filePath: "scripts/deploy.ps1");

        var data = new HypergraphData
        {
            Nodes =
            {
                [iRepoIface.Id] = iRepoIface,
                [iAuditIface.Id] = iAuditIface,
                [cacheKeyStruct.Id] = cacheKeyStruct,
                [iEquatable.Id] = iEquatable,
                [orderRepoClass.Id] = orderRepoClass,
                [saveMethod.Id] = saveMethod,
                [validateMethod.Id] = validateMethod,
                [validateRef.Id] = validateRef,
                [orderArg.Id] = orderArg,
                [psModuleNode.Id] = psModuleNode,
                [psCalledFunc.Id] = psCalledFunc,
                [psPathParam.Id] = psPathParam,
            },
            Hyperedges =
            {
                // InterfaceDefinition: IAuditable : IRepository (real extractor shape)
                new Hyperedge
                {
                    Id = "he-ifacedef-audit",
                    EdgeType = HyperedgeType.InterfaceDefinition,
                    NodeIds = [iAuditIface.Id, iRepoIface.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [iAuditIface.Id] = "interface",
                        [iRepoIface.Id] = "extends_0",
                    },
                },
                // StructDefinition: CacheKey : IEquatable (real extractor shape)
                new Hyperedge
                {
                    Id = "he-structdef-cache",
                    EdgeType = HyperedgeType.StructDefinition,
                    NodeIds = [cacheKeyStruct.Id, iEquatable.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [cacheKeyStruct.Id] = "struct",
                        [iEquatable.Id] = "implements_0",
                    },
                },
                // ClassDefinition: OrderRepository : IRepository (real extractor shape)
                new Hyperedge
                {
                    Id = "he-classdef-repo",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = [orderRepoClass.Id, iRepoIface.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [orderRepoClass.Id] = "class",
                        [iRepoIface.Id] = "base_0",
                    },
                },
                // MethodInvocation: Save() calls Validate() via method_ref
                new Hyperedge
                {
                    Id = "he-call-validate",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [validateRef.Id, orderArg.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [validateRef.Id] = "callee",
                        [orderArg.Id] = "arg_0",
                    },
                },
                // ModuleFunctionCall: top-level module-qualified call in PS
                new Hyperedge
                {
                    Id = "he-modfunc-getchild",
                    EdgeType = HyperedgeType.ModuleFunctionCall,
                    NodeIds = [psModuleNode.Id, psCalledFunc.Id, psPathParam.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [psModuleNode.Id] = "source_module",
                        [psCalledFunc.Id] = "called_function",
                        [psPathParam.Id] = "param_Path",
                    },
                },
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — expected edges:
        // 1x EXTENDS (IAuditable → IRepository) from InterfaceDefinition extends_0
        // 1x IMPLEMENTS (CacheKey → IEquatable) from StructDefinition implements_0
        // 1x IMPLEMENTS (OrderRepository → IRepository) from ClassDefinition base_0 (interface node type)
        // 1x CALLS (Save → Validate) from MethodInvocation
        // 1x CALLS (synthetic-file → Get-ChildItem) from ModuleFunctionCall
        result.EdgesCreated.Should().Be(5);
        result.ExtendsEdges.Should().Be(1);
        result.ImplementsEdges.Should().Be(2);
        result.CallsEdges.Should().Be(2);

        // ── Verify EXTENDS: IAuditable → IRepository ──
        var extendsEdges = this.store.UpsertedEdges
            .Where(e => (string)e.Edge.Metadata[MemoryMetadataKeys.SemanticRelation]! == "EXTENDS")
            .ToList();
        extendsEdges.Should().HaveCount(1);
        extendsEdges[0].Edge.NodeIds.Should().Contain(iAuditIface.Id);
        extendsEdges[0].Edge.NodeIds.Should().Contain(iRepoIface.Id);
        extendsEdges[0].Edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.InterfaceDefinition);

        // ── Verify IMPLEMENTS: CacheKey and OrderRepository ──
        var implEdges = this.store.UpsertedEdges
            .Where(e => (string)e.Edge.Metadata[MemoryMetadataKeys.SemanticRelation]! == "IMPLEMENTS")
            .ToList();
        implEdges.Should().HaveCount(2);

        var structImpl = implEdges.First(e => e.Edge.NodeIds.Contains(cacheKeyStruct.Id));
        structImpl.Edge.NodeIds.Should().Contain(iEquatable.Id);
        structImpl.Edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.StructDefinition);

        var classImpl = implEdges.First(e => e.Edge.NodeIds.Contains(orderRepoClass.Id));
        classImpl.Edge.NodeIds.Should().Contain(iRepoIface.Id);
        classImpl.Edge.Metadata[MemoryMetadataKeys.OriginalEdgeType].Should().Be(HyperedgeType.ClassDefinition);

        // ── Verify CALLS: Save → Validate (canonical IDs, not ref nodes) ──
        var callsEdges = this.store.UpsertedEdges
            .Where(e => (string)e.Edge.Metadata[MemoryMetadataKeys.SemanticRelation]! == "CALLS")
            .ToList();
        callsEdges.Should().HaveCount(2);

        var validateCall = callsEdges.FirstOrDefault(e =>
            e.Edge.NodeIds.Contains(validateMethod.Id));
        validateCall.Edge.Should().NotBeNull(
            because: "method_ref should resolve to canonical Validate definition node");
        validateCall.Edge.NodeIds.Should().Contain(saveMethod.Id,
            because: "caller should be resolved from scope to the Save definition");
        validateCall.Edge.NodeIds.Should().NotContain(validateRef.Id,
            because: "ref nodes should be resolved to canonical definitions");

        // ── Verify ModuleFunctionCall: file-level caller, NOT source_module ──
        var psCall = callsEdges.FirstOrDefault(e =>
            e.Edge.NodeIds.Contains(psCalledFunc.Id));
        psCall.Edge.Should().NotBeNull();
        psCall.Edge.NodeIds.Should().NotContain(psModuleNode.Id,
            because: "source_module is the target module, not the caller");

        // The caller should be a file-level node (synthesized or existing)
        var psCaller = psCall.Edge.NodeIds.First(id =>
            psCall.Edge.Roles.ContainsKey(id) && psCall.Edge.Roles[id] == "caller");

        // Synthetic node was upserted to the store with MemoryLayer metadata
        var psCallerNode = this.store.UpsertedNodes
            .Select(n => n.Node)
            .First(n => string.Equals(n.Id, psCaller, StringComparison.Ordinal));
        psCallerNode.NodeType.Should().Be("file",
            because: "top-level ModuleFunctionCall should use a file-level caller");
        psCallerNode.Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("structural",
            because: "synthetic nodes must carry MemoryLayer metadata per §3.11");
        data.Nodes.Should().NotContainKey(psCaller,
            because: "AnalyzeAsync must not mutate the input HypergraphData");

        // ── Only synthetic nodes upserted, NOT raw parser nodes ──
        // Raw parser nodes are owned by StructureBuilder (Stage 2.1).
        this.store.UpsertedNodes.Should().HaveCount(1,
            because: "only the synthetic file node should be upserted, not raw parser nodes");

        // ── No reverse-call edges stored ──
        foreach (var (edge, _) in this.store.UpsertedEdges)
        {
            var sr = (string)edge.Metadata[MemoryMetadataKeys.SemanticRelation]!;
            sr.Should().BeOneOf("CALLS", "EXTENDS", "IMPLEMENTS", "REFERENCES");
            sr.Should().NotContain("REVERSE");
        }
    }

    // ── Skipped-edge error tracking ─────────────────────────────────────

    [TestMethod]
    public async Task SkippedEdges_PopulateResultErrors_WhenCallerOrCalleeCannotBeResolved()
    {
        // Arrange — MethodInvocation hyperedge with no recognizable callee role
        var orphanNode = CreateNode("node-orphan", "Orphan", "expression");
        var data = new HypergraphData
        {
            Nodes = { [orphanNode.Id] = orphanNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-unresolvable",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [orphanNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [orphanNode.Id] = "unknown_role",
                    },
                },
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — no edges created, but errors are populated
        result.EdgesCreated.Should().Be(0);
        result.Errors.Should().NotBeEmpty(
            because: "skipped edges due to unresolvable caller/callee should be tracked in result.Errors");
        result.Errors.Should().Contain(e => e.Contains("he-unresolvable"));
    }

    [TestMethod]
    public async Task SkippedEdges_PopulateResultErrors_WhenCallerCannotBeResolved()
    {
        // Arrange — MethodInvocation with callee but no resolvable caller
        var calleeNode = CreateNode("node-callee-only", "Save", "method_ref");
        var data = new HypergraphData
        {
            Nodes = { [calleeNode.Id] = calleeNode },
            Hyperedges =
            {
                new Hyperedge
                {
                    Id = "he-no-caller",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = [calleeNode.Id],
                    Roles = new Dictionary<string, string>
                    {
                        [calleeNode.Id] = "callee",
                    },
                },
            }
        };

        // Act
        var result = await this.analyzer.AnalyzeAsync(data, this.version);

        // Assert — no edges created, caller resolution error tracked
        result.EdgesCreated.Should().Be(0);
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain(e => e.Contains("he-no-caller"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HypergraphNode CreateNode(
        string id, string name, string nodeType,
        string scope = "", string filePath = "src/test.cs")
    {
        return new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = nodeType,
            Language = "csharp",
            FilePath = filePath,
            Scope = scope,
        };
    }
}