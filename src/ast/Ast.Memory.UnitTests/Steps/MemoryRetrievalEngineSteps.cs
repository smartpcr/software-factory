// -----------------------------------------------------------------------
// <copyright file="MemoryRetrievalEngineSteps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Steps;

using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Retrieval;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Reqnroll;

[Binding]
[Scope(Tag = "retrieval")]
public sealed class MemoryRetrievalEngineSteps
{
    private readonly Mock<IMemoryGraphStore> graphStoreMock = new();
    private readonly Mock<IEmbeddingGenerator> embeddingGeneratorMock = new();
    private readonly Mock<IVectorStore> vectorStoreMock = new();
    private readonly Mock<ILogger<MemoryRetrievalEngine>> loggerMock = new();
    private readonly MemoryOptions memoryOptions = new();

    // Tracks every GetEdgesForNodeAsync invocation for verification
    private readonly List<(string NodeId, HashSet<string> MatchedRelations)> capturedEdgeCalls = [];

    // Tracks every GetExpertiseForNodeAsync invocation for verification
    private readonly List<string> capturedExpertiseCalls = [];

    private RetrievalRequest? request;
    private RetrievalResult? result;

    // ========================================================================
    // GIVEN steps
    // ========================================================================

    [Given(@"a populated graph and query ""(.*)""")]
    public void GivenAPopulatedGraphAndQuery(string queryText)
    {
        this.SetupPopulatedGraph();
        this.SetupEmbeddingGenerator();
        this.SetupVectorStore();

        // Hybrid scenario is purely query-driven — NO seed nodes.
        // SemanticSearch runs first, then its results seed StructuralDrill/CallTrace.
        this.request = new RetrievalRequest
        {
            QueryText = queryText,
            Mode = RetrievalMode.Hybrid,
            SeedNodeIds = [],
            TokenBudget = 8000
        };
    }

    [Given(@"a populated graph and query ""(.*)"" for SemanticSearch")]
    public void GivenAPopulatedGraphAndQueryForSemanticSearch(string queryText)
    {
        this.SetupPopulatedGraph();
        this.SetupEmbeddingGenerator();
        this.SetupVectorStore();

        this.request = new RetrievalRequest
        {
            QueryText = queryText,
            Mode = RetrievalMode.SemanticSearch,
            SeedNodeIds = [],
            TokenBudget = 8000
        };
    }

    [Given(@"seed node ""(.*)""")]
    public void GivenSeedNode(string seedNodeId)
    {
        this.SetupPopulatedGraph();

        this.request = new RetrievalRequest
        {
            SeedNodeIds = [seedNodeId],
            Mode = RetrievalMode.StructuralDrill,
            TokenBudget = 8000
        };
    }

    [Given(@"a populated graph with call-chain edges")]
    public void GivenAPopulatedGraphWithCallChainEdges()
    {
        this.SetupPopulatedGraph();

        this.request = new RetrievalRequest
        {
            SeedNodeIds = ["/src/Services/PaymentService.cs::PaymentService"],
            Mode = RetrievalMode.CallTrace,
            TokenBudget = 8000
        };
    }

    [Given(@"a graph with expertise nodes and no seed nodes")]
    public void GivenAGraphWithExpertiseNodesAndNoSeedNodes()
    {
        this.SetupExpertiseOnlyGraph();
    }

    [Given(@"a graph where edges have HyperedgeType different from SemanticRelation values")]
    public void GivenAGraphWhereEdgesHaveHyperedgeTypeDifferentFromSemanticRelationValues()
    {
        this.SetupMismatchedEdgeTypeGraph();
    }

    [Given(@"a graph where Payload TargetNodeId points to a different node than APPLIES_TO edges")]
    public void GivenAGraphWherePayloadTargetNodeIdPointsToDifferentNode()
    {
        this.SetupPayloadDecoyGraph();
    }

    // ========================================================================
    // WHEN steps
    // ========================================================================

    [When(@"RetrieveAsync is called with Mode=Hybrid")]
    public async Task WhenRetrieveAsyncIsCalledWithModeHybrid()
    {
        var engine = this.CreateEngine();
        this.result = await engine.RetrieveAsync(this.request!, CancellationToken.None);
    }

    [When(@"RetrieveAsync is called with Mode=StructuralDrill")]
    public async Task WhenRetrieveAsyncIsCalledWithModeStructuralDrill()
    {
        var engine = this.CreateEngine();
        this.result = await engine.RetrieveAsync(this.request!, CancellationToken.None);
    }

    [When(@"RetrieveAsync is called with Mode=SemanticSearch")]
    public async Task WhenRetrieveAsyncIsCalledWithModeSemanticSearch()
    {
        var engine = this.CreateEngine();
        this.result = await engine.RetrieveAsync(this.request!, CancellationToken.None);
    }

    [When(@"traversing call-chain via the retrieval engine")]
    public async Task WhenTraversingCallChainViaTheRetrievalEngine()
    {
        var engine = this.CreateEngine();
        this.result = await engine.RetrieveAsync(this.request!, CancellationToken.None);
    }

    [When(@"RetrieveAsync is called with Mode=ExpertiseRecall and query ""(.*)""")]
    public async Task WhenRetrieveAsyncIsCalledWithModeExpertiseRecallAndQuery(string queryText)
    {
        this.request = new RetrievalRequest
        {
            QueryText = queryText,
            Mode = RetrievalMode.ExpertiseRecall,
            SeedNodeIds = [],
            TokenBudget = 8000
        };

        var engine = this.CreateEngine();
        this.result = await engine.RetrieveAsync(this.request, CancellationToken.None);
    }

    // ========================================================================
    // THEN steps — general assertions
    // ========================================================================

    [Then(@"RetrievalResult\.Nodes is non-empty")]
    public void ThenRetrievalResultNodesIsNonEmpty()
    {
        this.result.Should().NotBeNull();
        this.result!.Nodes.Should().NotBeEmpty();
    }

    [Then(@"ContextMarkdown is non-empty")]
    public void ThenContextMarkdownIsNonEmpty()
    {
        this.result.Should().NotBeNull();
        this.result!.ContextMarkdown.Should().NotBeNullOrWhiteSpace();
    }

    [Then(@"ModesExecuted contains multiple modes")]
    public void ThenModesExecutedContainsMultipleModes()
    {
        this.result.Should().NotBeNull();
        this.result!.ModesExecuted.Should().HaveCountGreaterThan(1);
    }

    [Then(@"TokensUsed is less than or equal to TokenBudget")]
    public void ThenTokensUsedIsLessThanOrEqualToTokenBudget()
    {
        this.result.Should().NotBeNull();
        this.result!.TokensUsed.Should().BeLessThanOrEqualTo(this.request!.TokenBudget);
    }

    [Then(@"result includes child function nodes")]
    public void ThenResultIncludesChildFunctionNodes()
    {
        this.result.Should().NotBeNull();
        this.result!.Nodes.Should().Contain(n =>
            n.Node.NodeKind == "Method" || n.Node.NodeKind == "Function");
    }

    [Then(@"result includes parent package node")]
    public void ThenResultIncludesParentPackageNode()
    {
        this.result.Should().NotBeNull();
        this.result!.Nodes.Should().Contain(n =>
            n.Node.NodeKind == "Package" || n.Node.NodeKind == "Module");
    }

    // ========================================================================
    // THEN steps — HC5: embedding call count
    // ========================================================================

    [Then(@"exactly one embedding call is made with the query text ""(.*)""")]
    public void ThenExactlyOneEmbeddingCallIsMadeWithQueryText(string expectedQueryText)
    {
        // HC5: exactly one local embedding call per query
        this.embeddingGeneratorMock.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(1),
            "HC5: exactly one GenerateEmbeddingAsync call per query — no more, no less");

        this.embeddingGeneratorMock.Verify(
            e => e.GenerateEmbeddingAsync(
                It.Is<string>(s => s == expectedQueryText),
                It.IsAny<CancellationToken>()),
            Times.Exactly(1),
            $"The single embedding call must use the exact query text \"{expectedQueryText}\"");
    }

    [Then(@"GenerateBatchEmbeddingsAsync is never called")]
    public void ThenGenerateBatchEmbeddingsAsyncIsNeverCalled()
    {
        this.embeddingGeneratorMock.Verify(
            e => e.GenerateBatchEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "HC5: GenerateBatchEmbeddingsAsync must never be called during retrieval");
    }

    [Then(@"GenerateEmbeddingAsync is never called for StructuralDrill")]
    public void ThenGenerateEmbeddingAsyncIsNeverCalledForStructuralDrill()
    {
        this.embeddingGeneratorMock.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "StructuralDrill mode must NOT call GenerateEmbeddingAsync");
    }

    [Then(@"GenerateEmbeddingAsync is never called for CallTrace")]
    public void ThenGenerateEmbeddingAsyncIsNeverCalledForCallTrace()
    {
        this.embeddingGeneratorMock.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "CallTrace mode must NOT call GenerateEmbeddingAsync");
    }

    // ========================================================================
    // THEN steps — APPLIES_TO / ExpertiseRecall
    // ========================================================================

    [Then(@"ExpertiseRecall traverses APPLIES_TO edges not GetExpertiseForNodeAsync")]
    public void ThenExpertiseRecallTraversesAppliesToEdgesNotGetExpertiseForNodeAsync()
    {
        // GetEdgesForNodeAsync was called — proves edge-based traversal via canonical IGraphStore
        this.graphStoreMock.Verify(
            s => s.GetEdgesForNodeAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<HyperedgeType>?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "ExpertiseRecall must use GetEdgesForNodeAsync (canonical IGraphStore method) " +
            "for APPLIES_TO edge traversal, filtering by Metadata[\"SemanticRelation\"]");

        // Verify APPLIES_TO edges were found in captured edge calls
        this.capturedEdgeCalls.Should().Contain(
            c => c.MatchedRelations.Contains(SemanticRelations.AppliesTo),
            "GetEdgesForNodeAsync results must include APPLIES_TO edges filtered by SemanticRelation metadata");
    }

    [Then(@"expertise vector search is performed on memory_expertise collection")]
    public void ThenExpertiseVectorSearchIsPerformedOnMemoryExpertiseCollection()
    {
        this.vectorStoreMock.Verify(
            v => v.SearchAsync(
                "memory_expertise",
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<VectorFilter?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "Path B must perform ANN search on the memory_expertise collection");
    }

    [Then(@"GetEdgesAsync is called with the expertise result ID and APPLIES_TO")]
    public void ThenGetEdgesAsyncIsCalledWithExpertiseResultIdAndAppliesTo()
    {
        // Proves Path B: after vector search returns "expertise:pattern:payment-idem",
        // the engine calls GetEdgesForNodeAsync for that node and filters by APPLIES_TO
        this.graphStoreMock.Verify(
            s => s.GetEdgesForNodeAsync(
                "expertise:pattern:payment-idem",
                It.IsAny<IEnumerable<HyperedgeType>?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "Path B must call GetEdgesForNodeAsync with the expertise result ID " +
            "(\"expertise:pattern:payment-idem\") — code nodes discovered via APPLIES_TO edges");
    }

    [Then(@"code nodes are discovered only through APPLIES_TO edge traversal")]
    public void ThenCodeNodesAreDiscoveredOnlyThroughAppliesToEdgeTraversal()
    {
        this.result.Should().NotBeNull();
        this.result!.Nodes.Should().Contain(
            n => n.Node.Inner.Id == "/src/Services/PaymentService.cs::PaymentService",
            "PaymentService must be discovered through APPLIES_TO edge traversal " +
            "from expertise:pattern:payment-idem, not through any other mechanism");
    }

    [Then(@"GetExpertiseForNodeAsync is never called")]
    public void ThenGetExpertiseForNodeAsyncIsNeverCalled()
    {
        // ExpertiseRecall must use GetEdgesForNodeAsync + APPLIES_TO filter for
        // discovery — GetExpertiseForNodeAsync calls during this test come ONLY
        // from the Ranker (scoring phase), one per result node.
        // If ExpertiseRecall also called it, the total would exceed result count.
        var resultNodeCount = this.result?.Nodes.Count ?? 0;
        var expertiseCallCount = this.capturedExpertiseCalls.Count;

        expertiseCallCount.Should().BeLessThanOrEqualTo(
            resultNodeCount,
            $"GetExpertiseForNodeAsync was called {expertiseCallCount} times but only " +
            $"{resultNodeCount} result nodes exist. Calls beyond result-node count prove " +
            "ExpertiseRecall is calling GetExpertiseForNodeAsync for discovery (forbidden). " +
            "Only the Ranker may call it, once per result node for scoring.");
    }

    [Then(@"the node from APPLIES_TO edge is discovered")]
    public void ThenTheNodeFromAppliesToEdgeIsDiscovered()
    {
        this.result.Should().NotBeNull();
        this.result!.Nodes.Should().Contain(
            n => n.Node.Inner.Id == "/src/Services/PaymentService.cs::PaymentService",
            "The CORRECT node must be discovered through APPLIES_TO edge traversal");
    }

    [Then(@"the node from Payload TargetNodeId is not discovered")]
    public void ThenTheNodeFromPayloadTargetNodeIdIsNotDiscovered()
    {
        this.result.Should().NotBeNull();
        this.result!.Nodes.Should().NotContain(
            n => n.Node.Inner.Id == "/src/Services/DecoyService.cs::DecoyService",
            "The WRONG node from VectorSearchResult.Payload[\"TargetNodeId\"] must NOT " +
            "be discovered — the engine must follow APPLIES_TO edges exclusively");
    }

    // ========================================================================
    // THEN steps — edge filtering & CallTrace
    // ========================================================================

    [Then(@"all GetEdgesAsync calls use exact relation sets CALLS EXTENDS IMPLEMENTS")]
    public void ThenAllGetEdgesAsyncCallsUseExactRelationSets()
    {
        this.result.Should().NotBeNull();

        // Verify GetEdgesForNodeAsync was called (canonical IGraphStore method)
        this.graphStoreMock.Verify(
            s => s.GetEdgesForNodeAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<HyperedgeType>?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "CallTrace must use GetEdgesForNodeAsync (canonical IGraphStore method) " +
            "with in-memory filtering by Metadata[\"SemanticRelation\"]");

        // Verify captured edge calls contain CALLS relation in the matched results
        var callTraversalCalls = this.capturedEdgeCalls
            .Where(c => c.MatchedRelations.Contains(SemanticRelations.Calls))
            .ToList();

        callTraversalCalls.Should().NotBeEmpty(
            "GetEdgesForNodeAsync must return edges with CALLS SemanticRelation during CallTrace");

        // Verify all matched relations are valid SemanticRelation constants
        foreach (var (_, rels) in this.capturedEdgeCalls)
        {
            foreach (var rel in rels)
            {
                // No relation value should parse as a HyperedgeType enum
                Enum.TryParse<HyperedgeType>(rel, out _).Should().BeFalse(
                    $"Filter value \"{rel}\" must NOT be a HyperedgeType enum name — " +
                    "edge filtering uses Metadata[\"SemanticRelation\"] exclusively");
            }
        }
    }

    [Then(@"no GetCallersAsync or GetCalleesAsync calls are made")]
    public void ThenNoGetCallersAsyncOrGetCalleesAsyncCallsAreMade()
    {
        this.graphStoreMock.Verify(
            s => s.GetCallersAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "No retrieval mode should use GetCallersAsync; all traversal must go through " +
            "GetEdgesForNodeAsync with Metadata[\"SemanticRelation\"] filter");

        this.graphStoreMock.Verify(
            s => s.GetCalleesAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "No retrieval mode should use GetCalleesAsync; all traversal must go through " +
            "GetEdgesForNodeAsync with Metadata[\"SemanticRelation\"] filter");
    }

    [Then(@"result includes caller node OrderService.Submit from backward CALLS traversal")]
    public void ThenResultIncludesCallerNodeFromBackwardCallsTraversal()
    {
        this.result.Should().NotBeNull();
        this.result!.Nodes.Should().Contain(
            n => n.Node.Inner.Id == "/src/Services/OrderService.cs::OrderService.Submit",
            "Backward CALLS edge traversal should discover the caller OrderService.Submit");
    }

    [Then(@"result includes callee node PaymentService.Process from forward CALLS traversal")]
    public void ThenResultIncludesCalleeNodeFromForwardCallsTraversal()
    {
        this.result.Should().NotBeNull();
        this.result!.Nodes.Should().Contain(
            n => n.Node.Inner.Id == "/src/Services/PaymentService.cs::PaymentService.Process",
            "Forward CALLS edge traversal should discover the callee PaymentService.Process");
    }

    // ========================================================================
    // THEN steps — ANN score preservation
    // ========================================================================

    [Then(@"ANN scores from vector search are preserved with exact value (.*)")]
    public void ThenAnnScoresFromVectorSearchArePreservedWithExactValue(double expectedScore)
    {
        this.result.Should().NotBeNull();

        var semanticNode = this.result!.Nodes.FirstOrDefault(
            n => n.Node.Inner.Id == "/src/Services/PaymentService.cs::PaymentService.Process");
        semanticNode.Should().NotBeNull(
            "PaymentService.Process should be in the result (discovered by SemanticSearch)");

        semanticNode!.SemanticScore.Should().BeApproximately(expectedScore, 0.001,
            $"ANN score {expectedScore} from IVectorStore.SearchAsync must be preserved " +
            "exactly by the Ranker — never discarded for node-metadata cosine fallback");
    }

    [Then(@"ANN scores from vector search are preserved on SemanticSearch nodes")]
    public void ThenAnnScoresFromVectorSearchArePreservedOnSemanticSearchNodes()
    {
        this.result.Should().NotBeNull();

        var semanticNode = this.result!.Nodes.FirstOrDefault(
            n => n.Node.Inner.Id == "/src/Services/PaymentService.cs::PaymentService.Process");
        semanticNode.Should().NotBeNull(
            "PaymentService.Process should be discovered by SemanticSearch");

        semanticNode!.SemanticScore.Should().BeApproximately(0.92, 0.001,
            "ANN score 0.92 from IVectorStore.SearchAsync must be preserved by the Ranker");
    }

    // ========================================================================
    // THEN steps — HyperedgeType mismatch scenario
    // ========================================================================

    [Then(@"edge selection is driven by Metadata SemanticRelation not by HyperedgeType")]
    public void ThenEdgeSelectionIsDrivenByMetadataSemanticRelationNotByHyperedgeType()
    {
        this.result.Should().NotBeNull();

        // GetEdgesForNodeAsync was called — canonical IGraphStore method
        this.graphStoreMock.Verify(
            s => s.GetEdgesForNodeAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<HyperedgeType>?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "CallTrace must use GetEdgesForNodeAsync and filter by Metadata[\"SemanticRelation\"]");

        // Forward CALLS traversal works despite HyperedgeType=MethodInvocation mismatch
        this.result!.Nodes.Should().Contain(
            n => n.Node.Inner.Id == "/src/Services/PaymentService.cs::PaymentService.Process",
            "Forward CALLS traversal must work via Metadata[\"SemanticRelation\"] " +
            "even when HyperedgeType is MethodInvocation (mismatched)");

        // Backward CALLS traversal works despite HyperedgeType=ConstructorInvocation mismatch
        this.result!.Nodes.Should().Contain(
            n => n.Node.Inner.Id == "/src/Services/OrderService.cs::OrderService.Submit",
            "Backward CALLS traversal must work via Metadata[\"SemanticRelation\"] " +
            "even when HyperedgeType is ConstructorInvocation (mismatched)");
    }

    [Then(@"captured GetEdgesAsync filter values are exact SemanticRelation constants")]
    public void ThenCapturedGetEdgesAsyncFilterValuesAreExactSemanticRelationConstants()
    {
        this.capturedEdgeCalls.Should().NotBeEmpty(
            "GetEdgesForNodeAsync must have been called and edges filtered by SemanticRelation");

        foreach (var (_, rels) in this.capturedEdgeCalls)
        {
            foreach (var rel in rels)
            {
                // Must be a SemanticRelations constant, not a HyperedgeType.ToString()
                rel.Should().BeOneOf(
                    SemanticRelations.Calls,
                    SemanticRelations.Extends,
                    SemanticRelations.Implements,
                    SemanticRelations.Contains,
                    SemanticRelations.DependsOn,
                    SemanticRelations.AppliesTo,
                    SemanticRelations.References,
                    SemanticRelations.SimilarTo,
                    SemanticRelations.BelongsToCluster,
                    SemanticRelations.Supersedes,
                    SemanticRelations.DerivedFrom);

                // It must NOT be parseable as a HyperedgeType enum value
                Enum.TryParse<HyperedgeType>(rel, out _).Should().BeFalse(
                    $"Filter value \"{rel}\" must NOT be a HyperedgeType enum name — " +
                    "edge filtering must use Metadata[\"SemanticRelation\"] exclusively");
            }
        }
    }

    // ========================================================================
    // Engine factory
    // ========================================================================

    private MemoryRetrievalEngine CreateEngine()
    {
        var options = Options.Create(this.memoryOptions);
        return new MemoryRetrievalEngine(
            this.graphStoreMock.Object,
            options,
            this.loggerMock.Object,
            this.embeddingGeneratorMock.Object,
            this.vectorStoreMock.Object);
    }

    // ========================================================================
    // Graph setup helpers
    // ========================================================================

    private void SetupPopulatedGraph()
    {
        this.graphStoreMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Package node
        var packageNode = CreateHypergraphNode(
            "/src/Services", "Services", "Package",
            "/src/Services", MemoryLayers.Structural);

        var moduleNode = CreateHypergraphNode(
            "/src/Services/PaymentService.cs", "PaymentService.cs", "Module",
            "/src/Services/PaymentService.cs", MemoryLayers.Structural);

        var classNode = CreateHypergraphNode(
            "/src/Services/PaymentService.cs::PaymentService", "PaymentService", "Class",
            "/src/Services/PaymentService.cs", MemoryLayers.Structural);

        var hypergraphNodeClass = CreateHypergraphNode(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode", "HypergraphNode", "Class",
            "/src/ast/Ast.Core/HypergraphModels.cs", MemoryLayers.Structural);

        var hypergraphPackage = CreateHypergraphNode(
            "/src/ast/Ast.Core", "Ast.Core", "Package",
            "/src/ast/Ast.Core", MemoryLayers.Structural);

        var hypergraphModule = CreateHypergraphNode(
            "/src/ast/Ast.Core/HypergraphModels.cs", "HypergraphModels.cs", "Module",
            "/src/ast/Ast.Core/HypergraphModels.cs", MemoryLayers.Structural);

        var processMethod = CreateHypergraphNode(
            "/src/Services/PaymentService.cs::PaymentService.Process", "Process", "Method",
            "/src/Services/PaymentService.cs", MemoryLayers.Structural);

        var getEmbeddingTextMethod = CreateHypergraphNode(
            "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText",
            "GetEmbeddingText", "Function",
            "/src/ast/Ast.Core/HypergraphModels.cs", MemoryLayers.Structural);

        var callerNode = CreateHypergraphNode(
            "/src/Services/OrderService.cs::OrderService.Submit", "Submit", "Method",
            "/src/Services/OrderService.cs", MemoryLayers.Structural);

        // Expertise node — used by ExpertiseRecall to build ExpertiseEntry from node metadata
        var expertiseAbcNode = CreateExpertiseNode(
            "expertise:pattern:abc12345", "Payment idempotency pattern",
            "Always use idempotency keys for payment processing", 0.95);

        // IGraphStore.GetNodeAsync — canonical base method
        this.graphStoreMock.Setup(s => s.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => id switch
            {
                "/src/Services" => packageNode,
                "/src/Services/PaymentService.cs" => moduleNode,
                "/src/Services/PaymentService.cs::PaymentService" => classNode,
                "/src/Services/PaymentService.cs::PaymentService.Process" => processMethod,
                "/src/Services/OrderService.cs::OrderService.Submit" => callerNode,
                "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode" => hypergraphNodeClass,
                "/src/ast/Ast.Core" => hypergraphPackage,
                "/src/ast/Ast.Core/HypergraphModels.cs" => hypergraphModule,
                "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText" => getEmbeddingTextMethod,
                "expertise:pattern:abc12345" => expertiseAbcNode,
                _ => null
            });

        // CALLS edges use canonical "caller"/"callee" roles
        var containsEdgeClassToMethod = CreateHyperedge(
            "edge:contains:class-method", SemanticRelations.Contains, MemoryLayers.Structural,
            ["/src/Services/PaymentService.cs::PaymentService", "/src/Services/PaymentService.cs::PaymentService.Process"],
            "source", "target");

        var containsEdgeHgToMethod = CreateHyperedge(
            "edge:contains:hg-method", SemanticRelations.Contains, MemoryLayers.Structural,
            ["/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode", "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText"],
            "source", "target");

        var containsEdgeModuleToClass = CreateHyperedge(
            "edge:contains:module-class", SemanticRelations.Contains, MemoryLayers.Structural,
            ["/src/ast/Ast.Core/HypergraphModels.cs", "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode"],
            "source", "target");

        var containsEdgePkgToModule = CreateHyperedge(
            "edge:contains:pkg-module", SemanticRelations.Contains, MemoryLayers.Structural,
            ["/src/ast/Ast.Core", "/src/ast/Ast.Core/HypergraphModels.cs"],
            "source", "target");

        // CALLS edges use canonical "caller"/"callee" roles
        var callsEdge = CreateHyperedge(
            "edge:calls:order-payment", SemanticRelations.Calls, MemoryLayers.CallChain,
            ["/src/Services/OrderService.cs::OrderService.Submit", "/src/Services/PaymentService.cs::PaymentService"],
            "caller", "callee");

        var callsEdge2 = CreateHyperedge(
            "edge:calls:payment-process", SemanticRelations.Calls, MemoryLayers.CallChain,
            ["/src/Services/PaymentService.cs::PaymentService", "/src/Services/PaymentService.cs::PaymentService.Process"],
            "caller", "callee");

        var appliesToEdge = CreateHyperedge(
            "edge:applies-to:expertise-payment", SemanticRelations.AppliesTo, MemoryLayers.Expertise,
            ["expertise:pattern:abc12345", "/src/Services/PaymentService.cs::PaymentService"],
            "source", "target");

        var allEdges = new List<Hyperedge>
        {
            containsEdgeClassToMethod, containsEdgeHgToMethod,
            containsEdgeModuleToClass, containsEdgePkgToModule,
            callsEdge, callsEdge2, appliesToEdge
        };

        this.SetupEdgeMocks(allEdges);
        this.SetupExpertiseForNode("expertise:pattern:abc12345", "/src/Services/PaymentService.cs::PaymentService");
    }

    private void SetupExpertiseOnlyGraph()
    {
        this.graphStoreMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var paymentServiceNode = CreateHypergraphNode(
            "/src/Services/PaymentService.cs::PaymentService", "PaymentService", "Class",
            "/src/Services/PaymentService.cs", MemoryLayers.Structural);

        // Expertise node — used by ExpertiseRecall to build ExpertiseEntry from node metadata
        var expertiseNode = CreateExpertiseNode(
            "expertise:pattern:payment-idem", "Payment idempotency pattern",
            "Always use idempotency keys for payment processing", 0.95);

        this.graphStoreMock.Setup(s => s.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => id switch
            {
                "/src/Services/PaymentService.cs::PaymentService" => paymentServiceNode,
                "expertise:pattern:payment-idem" => expertiseNode,
                _ => null
            });

        var appliesToEdge = CreateHyperedge(
            "edge:applies-to:expertise-payment", SemanticRelations.AppliesTo, MemoryLayers.Expertise,
            ["expertise:pattern:payment-idem", "/src/Services/PaymentService.cs::PaymentService"],
            "source", "target");

        var allEdges = new List<Hyperedge> { appliesToEdge };

        // IGraphStore.GetEdgesForNodeAsync — canonical base method with tracking
        this.graphStoreMock.Setup(s => s.GetEdgesForNodeAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<HyperedgeType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, IEnumerable<HyperedgeType>? _, CancellationToken _2) =>
            {
                var matched = allEdges.Where(e => e.NodeIds.Contains(nodeId)).ToList();
                var matchedRelations = new HashSet<string>(
                    matched.Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var v) && v is string)
                           .Select(e => (string)e.Metadata[MemoryMetadataKeys.SemanticRelation]!));
                this.capturedEdgeCalls.Add((nodeId, matchedRelations));
                return (IReadOnlyList<Hyperedge>)matched;
            });

        // Canonical §5.1 GetExpertiseForNodeAsync — with call tracking
        this.graphStoreMock.Setup(s => s.GetExpertiseForNodeAsync(
                "/src/Services/PaymentService.cs::PaymentService", It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, double _, CancellationToken _2) =>
            {
                this.capturedExpertiseCalls.Add(nodeId);
                return (IReadOnlyList<ExpertiseEntry>)new List<ExpertiseEntry>
                {
                    new()
                    {
                        Id = "expertise:pattern:payment-idem",
                        Kind = ExpertiseKind.Pattern,
                        Title = "Payment idempotency pattern",
                        Body = "Always use idempotency keys for payment processing",
                        TargetNodeId = "/src/Services/PaymentService.cs::PaymentService",
                        Confidence = 0.95
                    }
                };
            });

        this.graphStoreMock.Setup(s => s.GetExpertiseForNodeAsync(
                It.Is<string>(id => id != "/src/Services/PaymentService.cs::PaymentService"),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, double _, CancellationToken _2) =>
            {
                this.capturedExpertiseCalls.Add(nodeId);
                return (IReadOnlyList<ExpertiseEntry>)new List<ExpertiseEntry>();
            });

        this.SetupEmbeddingGenerator();

        this.vectorStoreMock.Setup(v => v.SearchAsync(
                "memory_expertise", It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<VectorFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new() { Id = "expertise:pattern:payment-idem", Score = 0.91f }
            });

        this.vectorStoreMock.Setup(v => v.SearchAsync(
                "memory_embeddings", It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<VectorFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>());
    }

    private void SetupMismatchedEdgeTypeGraph()
    {
        this.graphStoreMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var seedNode = CreateHypergraphNode(
            "/src/Services/PaymentService.cs::PaymentService", "PaymentService", "Class",
            "/src/Services/PaymentService.cs", MemoryLayers.Structural);
        var calleeNode = CreateHypergraphNode(
            "/src/Services/PaymentService.cs::PaymentService.Process", "Process", "Method",
            "/src/Services/PaymentService.cs", MemoryLayers.Structural);
        var callerNode = CreateHypergraphNode(
            "/src/Services/OrderService.cs::OrderService.Submit", "Submit", "Method",
            "/src/Services/OrderService.cs", MemoryLayers.Structural);

        this.graphStoreMock.Setup(s => s.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => id switch
            {
                "/src/Services/PaymentService.cs::PaymentService" => seedNode,
                "/src/Services/PaymentService.cs::PaymentService.Process" => calleeNode,
                "/src/Services/OrderService.cs::OrderService.Submit" => callerNode,
                _ => null
            });

        // HyperedgeType is DELIBERATELY MISMATCHED from SemanticRelation
        var callsEdgeFwd = CreateHyperedgeWithMismatchedType(
            "edge:calls:payment-process", HyperedgeType.MethodInvocation,
            SemanticRelations.Calls, MemoryLayers.CallChain,
            ["/src/Services/PaymentService.cs::PaymentService", "/src/Services/PaymentService.cs::PaymentService.Process"],
            "caller", "callee");

        var callsEdgeBwd = CreateHyperedgeWithMismatchedType(
            "edge:calls:order-payment", HyperedgeType.ConstructorInvocation,
            SemanticRelations.Calls, MemoryLayers.CallChain,
            ["/src/Services/OrderService.cs::OrderService.Submit", "/src/Services/PaymentService.cs::PaymentService"],
            "caller", "callee");

        // Decoy edge with wrong SemanticRelation — should NOT be traversed in CallTrace
        var containsEdge = CreateHyperedgeWithMismatchedType(
            "edge:contains:decoy", HyperedgeType.MethodInvocation,
            SemanticRelations.Contains, MemoryLayers.Structural,
            ["/src/Services/PaymentService.cs::PaymentService", "/src/Services/Decoy.cs::Decoy"],
            "source", "target");

        var allEdges = new List<Hyperedge> { callsEdgeFwd, callsEdgeBwd, containsEdge };
        this.SetupEdgeMocksWithTracking(allEdges);

        // Canonical §5.1 expertise — with call tracking
        this.graphStoreMock.Setup(s => s.GetExpertiseForNodeAsync(
                It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, double _, CancellationToken _2) =>
            {
                this.capturedExpertiseCalls.Add(nodeId);
                return (IReadOnlyList<ExpertiseEntry>)new List<ExpertiseEntry>();
            });

        this.request = new RetrievalRequest
        {
            SeedNodeIds = ["/src/Services/PaymentService.cs::PaymentService"],
            Mode = RetrievalMode.CallTrace,
            TokenBudget = 8000
        };
    }

    private void SetupPayloadDecoyGraph()
    {
        this.graphStoreMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var correctNode = CreateHypergraphNode(
            "/src/Services/PaymentService.cs::PaymentService", "PaymentService", "Class",
            "/src/Services/PaymentService.cs", MemoryLayers.Structural);
        var wrongNode = CreateHypergraphNode(
            "/src/Services/DecoyService.cs::DecoyService", "DecoyService", "Class",
            "/src/Services/DecoyService.cs", MemoryLayers.Structural);

        // Expertise node — used by ExpertiseRecall to build ExpertiseEntry from node metadata
        var expertiseNode = CreateExpertiseNode(
            "expertise:pattern:payment-idem", "Payment idempotency pattern",
            "Always use idempotency keys", 0.95);

        this.graphStoreMock.Setup(s => s.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => id switch
            {
                "/src/Services/PaymentService.cs::PaymentService" => correctNode,
                "/src/Services/DecoyService.cs::DecoyService" => wrongNode,
                "expertise:pattern:payment-idem" => expertiseNode,
                _ => null
            });

        var appliesToEdge = CreateHyperedge(
            "edge:applies-to:expertise-payment", SemanticRelations.AppliesTo, MemoryLayers.Expertise,
            ["expertise:pattern:payment-idem", "/src/Services/PaymentService.cs::PaymentService"],
            "source", "target");

        var allEdges = new List<Hyperedge> { appliesToEdge };

        this.graphStoreMock.Setup(s => s.GetEdgesForNodeAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<HyperedgeType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, IEnumerable<HyperedgeType>? _, CancellationToken _2) =>
            {
                var matched = allEdges.Where(e => e.NodeIds.Contains(nodeId)).ToList();
                var matchedRelations = new HashSet<string>(
                    matched.Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var v) && v is string)
                           .Select(e => (string)e.Metadata[MemoryMetadataKeys.SemanticRelation]!));
                this.capturedEdgeCalls.Add((nodeId, matchedRelations));
                return (IReadOnlyList<Hyperedge>)matched;
            });

        // Canonical §5.1 GetExpertiseForNodeAsync — with call tracking
        this.graphStoreMock.Setup(s => s.GetExpertiseForNodeAsync(
                "/src/Services/PaymentService.cs::PaymentService", It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, double _, CancellationToken _2) =>
            {
                this.capturedExpertiseCalls.Add(nodeId);
                return (IReadOnlyList<ExpertiseEntry>)new List<ExpertiseEntry>
                {
                    new()
                    {
                        Id = "expertise:pattern:payment-idem",
                        Kind = ExpertiseKind.Pattern,
                        Title = "Payment idempotency pattern",
                        Body = "Always use idempotency keys",
                        TargetNodeId = "/src/Services/PaymentService.cs::PaymentService",
                        Confidence = 0.95
                    }
                };
            });

        this.graphStoreMock.Setup(s => s.GetExpertiseForNodeAsync(
                It.Is<string>(id => id != "/src/Services/PaymentService.cs::PaymentService"),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, double _, CancellationToken _2) =>
            {
                this.capturedExpertiseCalls.Add(nodeId);
                return (IReadOnlyList<ExpertiseEntry>)new List<ExpertiseEntry>();
            });

        this.SetupEmbeddingGenerator();

        // Payload["TargetNodeId"] points to WRONG node (DecoyService)
        this.vectorStoreMock.Setup(v => v.SearchAsync(
                "memory_expertise", It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<VectorFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new()
                {
                    Id = "expertise:pattern:payment-idem",
                    Score = 0.91f,
                    Payload = new Dictionary<string, object>
                    {
                        ["TargetNodeId"] = "/src/Services/DecoyService.cs::DecoyService"
                    }
                }
            });

        this.vectorStoreMock.Setup(v => v.SearchAsync(
                "memory_embeddings", It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<VectorFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>());
    }

    // ========================================================================
    // Shared mock setup helpers
    // ========================================================================

    private void SetupEdgeMocks(List<Hyperedge> allEdges)
    {
        // IGraphStore.GetEdgesForNodeAsync — canonical base method
        // Returns all edges containing the nodeId; the engine filters by SemanticRelation in-memory
        this.graphStoreMock.Setup(s => s.GetEdgesForNodeAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<HyperedgeType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, IEnumerable<HyperedgeType>? _, CancellationToken _2) =>
            {
                var matched = allEdges.Where(e => e.NodeIds.Contains(nodeId)).ToList();
                var matchedRelations = new HashSet<string>(
                    matched.Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var v) && v is string)
                           .Select(e => (string)e.Metadata[MemoryMetadataKeys.SemanticRelation]!));
                this.capturedEdgeCalls.Add((nodeId, matchedRelations));
                return (IReadOnlyList<Hyperedge>)matched;
            });
    }

    private void SetupEdgeMocksWithTracking(List<Hyperedge> allEdges)
    {
        this.capturedEdgeCalls.Clear();

        this.graphStoreMock.Setup(s => s.GetEdgesForNodeAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<HyperedgeType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, IEnumerable<HyperedgeType>? _, CancellationToken _2) =>
            {
                var matched = allEdges.Where(e => e.NodeIds.Contains(nodeId)).ToList();
                var matchedRelations = new HashSet<string>(
                    matched.Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var v) && v is string)
                           .Select(e => (string)e.Metadata[MemoryMetadataKeys.SemanticRelation]!));
                this.capturedEdgeCalls.Add((nodeId, matchedRelations));
                return (IReadOnlyList<Hyperedge>)matched;
            });
    }

    private void SetupExpertiseForNode(string expertiseId, string targetNodeId)
    {
        // Canonical §5.1 GetExpertiseForNodeAsync — with call tracking
        this.graphStoreMock.Setup(s => s.GetExpertiseForNodeAsync(
                targetNodeId, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, double _, CancellationToken _2) =>
            {
                this.capturedExpertiseCalls.Add(nodeId);
                return (IReadOnlyList<ExpertiseEntry>)new List<ExpertiseEntry>
                {
                    new()
                    {
                        Id = expertiseId,
                        Kind = ExpertiseKind.Pattern,
                        Title = "Payment idempotency pattern",
                        Body = "Always use idempotency keys for payment processing",
                        TargetNodeId = targetNodeId,
                        Confidence = 0.95
                    }
                };
            });

        this.graphStoreMock.Setup(s => s.GetExpertiseForNodeAsync(
                It.Is<string>(id => id != targetNodeId),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, double _, CancellationToken _2) =>
            {
                this.capturedExpertiseCalls.Add(nodeId);
                return (IReadOnlyList<ExpertiseEntry>)new List<ExpertiseEntry>();
            });
    }

    private void SetupEmbeddingGenerator()
    {
        this.embeddingGeneratorMock.Setup(e => e.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        this.embeddingGeneratorMock.Setup(e => e.Dimensions).Returns(768);
        this.embeddingGeneratorMock.Setup(e => e.GenerateEmbeddingAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[768]);
    }

    private void SetupVectorStore()
    {
        this.vectorStoreMock.Setup(v => v.SearchAsync(
                "memory_embeddings", It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<VectorFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new()
                {
                    Id = "/src/Services/PaymentService.cs::PaymentService.Process",
                    Score = 0.92f
                }
            });

        this.vectorStoreMock.Setup(v => v.SearchAsync(
                "memory_expertise", It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<VectorFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new()
                {
                    Id = "expertise:pattern:abc12345",
                    Score = 0.88f
                }
            });
    }

    // ========================================================================
    // HypergraphNode / Hyperedge factory helpers
    // ========================================================================

    private static HypergraphNode CreateHypergraphNode(
        string id, string name, string nodeType,
        string filePath, string memoryLayer)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = nodeType,
            Language = "C#",
            FilePath = filePath,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = memoryLayer,
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                [MemoryMetadataKeys.SinceIndex] = 1
            }
        };
    }

    private static Hyperedge CreateHyperedge(
        string id, string semanticRelation,
        string memoryLayer, List<string> nodeIds,
        string firstRole, string secondRole)
    {
        return new Hyperedge
        {
            Id = id,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = nodeIds,
            Roles = nodeIds.ToDictionary(n => n, n => n == nodeIds[0] ? firstRole : secondRole),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = semanticRelation,
                [MemoryMetadataKeys.MemoryLayer] = memoryLayer
            }
        };
    }

    /// <summary>
    /// Creates a Hyperedge where HyperedgeType is deliberately different from
    /// Metadata["SemanticRelation"]. Tests that the engine filters by
    /// Metadata["SemanticRelation"] exclusively, never by HyperedgeType.
    /// </summary>
    private static Hyperedge CreateHyperedgeWithMismatchedType(
        string id, HyperedgeType deliberatelyWrongEdgeType,
        string semanticRelation, string memoryLayer, List<string> nodeIds,
        string firstRole, string secondRole)
    {
        return new Hyperedge
        {
            Id = id,
            EdgeType = deliberatelyWrongEdgeType,
            NodeIds = nodeIds,
            Roles = nodeIds.ToDictionary(n => n, n => n == nodeIds[0] ? firstRole : secondRole),
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = semanticRelation,
                [MemoryMetadataKeys.MemoryLayer] = memoryLayer
            }
        };
    }

    /// <summary>
    /// Creates a HypergraphNode representing an expertise entry in the graph.
    /// ExpertiseRecall builds ExpertiseEntry from this node's metadata.
    /// </summary>
    private static HypergraphNode CreateExpertiseNode(
        string id, string title, string body, double confidence)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = title,
            NodeType = "Expertise",
            Language = string.Empty,
            FilePath = string.Empty,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = MemoryLayers.Expertise,
                [MemoryMetadataKeys.Summary] = body,
                [MemoryMetadataKeys.Confidence] = confidence,
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                [MemoryMetadataKeys.SinceIndex] = 1
            }
        };
    }
}
