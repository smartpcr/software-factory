@retrieval
Feature: MemoryRetrievalEngine Integration
    The MemoryRetrievalEngine orchestrates hybrid context retrieval
    by composing QueryPlanner, Ranker, BudgetController, and ContextAssembler.

    Scenario: Hybrid retrieval
        Given a populated graph and query "payment processing"
        When RetrieveAsync is called with Mode=Hybrid
        Then RetrievalResult.Nodes is non-empty
        And ContextMarkdown is non-empty
        And ModesExecuted contains multiple modes
        And TokensUsed is less than or equal to TokenBudget
        And exactly one embedding call is made with the query text "payment processing"
        And GenerateBatchEmbeddingsAsync is never called
        And ExpertiseRecall traverses APPLIES_TO edges not GetExpertiseForNodeAsync
        And ANN scores from vector search are preserved with exact value 0.92
        And no GetCallersAsync or GetCalleesAsync calls are made

    Scenario: Structural drill from seed
        Given seed node "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode"
        When RetrieveAsync is called with Mode=StructuralDrill
        Then result includes child function nodes
        And result includes parent package node
        And GenerateEmbeddingAsync is never called for StructuralDrill
        And no GetCallersAsync or GetCalleesAsync calls are made

    Scenario: Edge filtering by SemanticRelation
        Given a populated graph with call-chain edges
        When traversing call-chain via the retrieval engine
        Then all GetEdgesAsync calls use exact relation sets CALLS EXTENDS IMPLEMENTS
        And no GetCallersAsync or GetCalleesAsync calls are made
        And result includes caller node OrderService.Submit from backward CALLS traversal
        And result includes callee node PaymentService.Process from forward CALLS traversal

    Scenario: ExpertiseRecall Path B follows APPLIES_TO edges from vector results
        Given a graph with expertise nodes and no seed nodes
        When RetrieveAsync is called with Mode=ExpertiseRecall and query "payment idempotency"
        Then expertise vector search is performed on memory_expertise collection
        And GetEdgesAsync is called with the expertise result ID and APPLIES_TO
        And code nodes are discovered only through APPLIES_TO edge traversal
        And GetExpertiseForNodeAsync is never called
        And exactly one embedding call is made with the query text "payment idempotency"
        And GenerateBatchEmbeddingsAsync is never called

    Scenario: HyperedgeType is never used for edge filtering
        Given a graph where edges have HyperedgeType different from SemanticRelation values
        When traversing call-chain via the retrieval engine
        Then edge selection is driven by Metadata SemanticRelation not by HyperedgeType
        And captured GetEdgesAsync filter values are exact SemanticRelation constants
        And no GetCallersAsync or GetCalleesAsync calls are made

    Scenario: ExpertiseRecall ignores VectorSearchResult Payload
        Given a graph where Payload TargetNodeId points to a different node than APPLIES_TO edges
        When RetrieveAsync is called with Mode=ExpertiseRecall and query "payment idempotency"
        Then the node from APPLIES_TO edge is discovered
        And the node from Payload TargetNodeId is not discovered
        And GetExpertiseForNodeAsync is never called

    Scenario: CallTrace mode does not call GenerateEmbeddingAsync
        Given a populated graph with call-chain edges
        When traversing call-chain via the retrieval engine
        Then GenerateEmbeddingAsync is never called for CallTrace

    Scenario: SemanticSearch mode calls GenerateEmbeddingAsync exactly once
        Given a populated graph and query "payment processing" for SemanticSearch
        When RetrieveAsync is called with Mode=SemanticSearch
        Then exactly one embedding call is made with the query text "payment processing"
        And GenerateBatchEmbeddingsAsync is never called
        And ANN scores from vector search are preserved on SemanticSearch nodes