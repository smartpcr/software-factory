# Implementation Plan — agent memory

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 pts
>
> Build graph-based agent memory on top-down repo structure and call-chain
> context so that agents can learn and get smarter across sessions.
>
> **Canonical authority:** [`architecture.md`](architecture.md) governs all
> naming, data model, edge taxonomy, ID format, interface signatures, and
> constants. This plan describes *work items and phasing*; when literal names
> or values here conflict with `architecture.md`, follow `architecture.md`.
>
> Companion docs (written in parallel — referenced by name, not duplicated):
> - [`architecture.md`](architecture.md) — component diagram, graph layers, data model, interfaces
> - [`tech-spec.md`](tech-spec.md) — problem statement, constraints, risks, success criteria
> - [`e2e-scenarios.md`](e2e-scenarios.md) — Gherkin acceptance scenarios (13 features, 82 scenarios)

## Phase 1: Memory Schema and Storage Foundation

> **Goal:** Define the memory-layer data model (composition wrappers, expertise
> entries, temporal versioning, audit log) and implement both storage backends
> (`SqliteGraphStore`, `Neo4jMemoryGraphStore`) behind `IMemoryGraphStore`.
>
> **Hard dependency:** None — this is the foundation phase.

### Stage 1.1: Project Scaffold and Core Models

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Ast.Memory.csproj` targeting `net8.0` with project references to `Ast.Core`, `Ast.GraphDb`, `Ast.Embedding`, `Ast.VectorDb`, `Ast.Query`, `Ast.Csharp`, `Ast.PowerShell`; add to `Workflow.sln`
- [ ] Create directory structure: `Interfaces/`, `Models/`, `Extraction/`, `Retrieval/`, `Context/`, `Evolution/`, `Sync/`, `Storage/`, `Schema/`, `Mcp/`, `Cli/` (per architecture.md §3.0)
- [ ] Create `src/ast/Ast.Memory/Models/Enums.cs` with enums: `ExpertiseKind` (`Pattern`, `Pitfall`, `Preference`, `Insight`), `RetrievalMode` (`StructuralDrill`, `CallTrace`, `SemanticSearch`, `ExpertiseRecall`, `Hybrid`), `MemoryBackend` (`Sqlite`, `Neo4j`) — per architecture.md §2.1, §5.3
- [ ] Create `src/ast/Ast.Memory/Models/VersionRange.cs` as `record VersionRange(string Since, int SinceIndex, string? Until, int? UntilIndex)` per architecture.md §4.3
- [ ] Create `src/ast/Ast.Memory/Models/MemoryNode.cs` as composition wrapper containing `HypergraphNode Inner { get; }` (NOT inheritance) with strongly-typed accessors: `MemoryLayer` → `Inner.Metadata["MemoryLayer"]`, `NodeKind` → `Inner.NodeType`, `ContentHash` → `Inner.Metadata["ContentHash"]`, plus `VersionRange? Version` property (nullable — null for External boundary nodes which are timeless per architecture.md §4.1, §4.3)
- [ ] Create `src/ast/Ast.Memory/Models/MemoryEdge.cs` as composition wrapper containing `Hyperedge Inner { get; }` (NOT inheritance) with strongly-typed accessors: `SemanticRelation` → `Inner.Metadata["SemanticRelation"]`, `MemoryLayer` → `Inner.Metadata["MemoryLayer"]`, plus `VersionRange Version` property — per architecture.md §4.1
- [ ] Create `src/ast/Ast.Memory/Models/ExpertiseEntry.cs` with fields: `Id` (format `"expertise:{kind}:{hash8}"`), `Kind` (`ExpertiseKind`), `Title` (headline), `Body` (detailed description), `TargetNodeId`, `Scope` (`"exact"`|`"subtree"`, default `"exact"`), `Confidence` (0.0–1.0, default 1.0), `Archived` (bool, default false), `SourceAgent`, `Evidence` (`IReadOnlyList<Citation>`), `Embedding` (`float[]?`), `ContentHash` (SHA-256 of Title+Body), `CreatedAt`, `UpdatedAt` — per architecture.md §4.2
- [ ] Create `src/ast/Ast.Memory/Models/Citation.cs` as `record Citation(string NodeId, string? CommitSha, string? Snippet)` — per architecture.md §4.2
- [ ] Create `src/ast/Ast.Memory/Models/MemoryMetadataKeys.cs` with `static class` defining constants for all curated Metadata key names: `MemoryLayer`, `SemanticRelation`, `ContentHash`, `SinceCommit`, `SinceIndex`, `UntilCommit`, `UntilIndex`, `Summary`, `MemberCount`, `SimilarityScore`, `OriginalEdgeType`, `Confidence`, `Stale`, `QualifiedName`, `LastTouched`, `Embedding`, `EmbeddingModel`, `Package` — per architecture.md §4.5
- [ ] Add `HyperedgeType.MemoryRelation` enum value to `src/ast/Ast.Core/HypergraphModels.cs` — the sole modification to any existing project (architecture.md §4.6)

#### Test Scenarios
- [ ] Scenario: MemoryNode composition — Given a `HypergraphNode` with `Metadata["MemoryLayer"]="structural"` and temporal metadata `SinceCommit="abc123"`, `SinceIndex=42`, When wrapped in a `MemoryNode`, Then `memoryNode.Inner` is the original node, `memoryNode.MemoryLayer` returns `"structural"`, and `memoryNode.Version?.SinceIndex` returns `42`; Given an External boundary node with no temporal metadata, When wrapped in a `MemoryNode`, Then `memoryNode.Version` is `null`
- [ ] Scenario: MemoryEdge composition — Given a `Hyperedge` with `EdgeType=HyperedgeType.MemoryRelation` and `Metadata["SemanticRelation"]="APPLIES_TO"`, When wrapped in a `MemoryEdge`, Then `memoryEdge.Inner` is the original edge and `memoryEdge.SemanticRelation` returns `"APPLIES_TO"`
- [ ] Scenario: ExpertiseEntry ID generation — Given `Title="Fragile null-handling"`, `Body="processRefund assumes non-null"`, `TargetNodeId="/src/Services/PaymentService.cs::PaymentService.processRefund"`, When `ExpertiseEntry` is created with `Kind=Pitfall`, Then `Id` equals `"expertise:pitfall:{first 8 chars of SHA-256(Title+Body+TargetNodeId)}"`
- [ ] Scenario: MemoryRelation enum addition — Given the updated `Ast.Core` with `HyperedgeType.MemoryRelation`, When all existing `Ast.Core.UnitTests` are run, Then zero tests regress (additive-only change)

### Stage 1.2: IMemoryGraphStore Interface and Configuration

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Interfaces/IMemoryGraphStore.cs` extending `IGraphStore` (from `Ast.GraphDb`) with methods per architecture.md §5.1: `GetNodesByLayerAsync(string memoryLayer, int? atCommitIndex, NodeFilter?, int limit, CancellationToken)`, `GetEdgesBySemanticRelationAsync(string semanticRelation, string? memoryLayer, int? atCommitIndex, int limit, CancellationToken)`, `UpsertNodeAsync(HypergraphNode, VersionRange, CancellationToken)`, `RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken)`, `RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken)`, `UpdateEdgeMetadataAsync(string edgeId, IDictionary<string,object?>, CancellationToken)`, `GetExpertiseForNodeAsync(string targetNodeId, double minConfidence, CancellationToken)`, `UpsertExpertiseAsync(ExpertiseEntry, CancellationToken)`, `ArchiveExpertiseAsync(string expertiseId, CancellationToken)`, `GetArchivedExpertiseAsync(string? targetNodeId, int limit, CancellationToken)`, `GetMemoryStatisticsAsync(CancellationToken)`, `LogAuditAsync(string sessionId, string operation, object detail, CancellationToken)`
- [ ] Create `src/ast/Ast.Memory/Models/MemoryGraphStatistics.cs` with per-layer node/edge counts
- [ ] Create `src/ast/Ast.Memory/Models/MemoryOptions.cs` with all constants from architecture.md §2.4: `SimilarityThreshold` (0.85), `StaleDecayFactor` (0.5), `MinorDecayFactor` (0.9), `ArchiveConfidenceFloor` (0.1), `DecayHalfLifeDays` (1095), `TokenBudgetDefault` (8000), `TokenBudgetCeiling` (32000), `IncrementalSyncDebounceMs` (1000), `CentralityBoostFactor` (0.5), `Backend` (`MemoryBackend`), `SqlitePath`, `Neo4jUri`, `QdrantEndpoint`
- [ ] Create `src/ast/Ast.Memory/Models/ScoringWeights.cs` as record with defaults: structural=0.30, semantic=0.25, centrality=0.20, recency=0.15, expertise=0.10 (sum=1.00) — per architecture.md §2.4

#### Test Scenarios
- [ ] Scenario: IMemoryGraphStore contract — Given the `IMemoryGraphStore` interface, When compiled, Then it extends `IGraphStore` and declares all 12 methods from architecture.md §5.1 (`GetNodesByLayerAsync`, `GetEdgesBySemanticRelationAsync`, `UpsertNodeAsync`, `RetireNodeAsync`, `RetireEdgeAsync`, `UpdateEdgeMetadataAsync`, `GetExpertiseForNodeAsync`, `UpsertExpertiseAsync`, `ArchiveExpertiseAsync`, `GetArchivedExpertiseAsync`, `GetMemoryStatisticsAsync`, `LogAuditAsync`)
- [ ] Scenario: MemoryOptions defaults — Given default `MemoryOptions`, When constructed, Then `SimilarityThreshold`=0.85, `TokenBudgetDefault`=8000, `TokenBudgetCeiling`=32000, `DecayHalfLifeDays`=1095, `ArchiveConfidenceFloor`=0.1, `CentralityBoostFactor`=0.5, and scoring weights sum to 1.00

### Stage 1.3: SqliteGraphStore Implementation

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Storage/SqliteGraphStore.cs` implementing `IMemoryGraphStore` with SQLite + FTS5 + sqlite-vss; create tables `nodes`, `edges`, `expertise`, `commits`, `audit_log` per architecture.md §4.4 schema
- [ ] Implement `UpsertNodeAsync` — insert or update node with `VersionRange`; use `SemaphoreSlim(1,1)` for write serialization (architecture.md §3.7)
- [ ] Implement `RetireNodeAsync` — set `until_index` on node and cascade to connected edges; plus `RetireEdgeAsync` for individual edge soft-delete
- [ ] Implement `GetNodesByLayerAsync` with temporal filter: `WHERE (since_index IS NULL OR (since_index <= @target AND (until_index IS NULL OR until_index > @target)))` (NULL `since_index` = External boundary nodes, always included per architecture.md §4.3)
- [ ] Implement `GetExpertiseForNodeAsync` — filter by `target_node_id` and `archived = 0`; `ArchiveExpertiseAsync` sets `archived = 1`; `GetArchivedExpertiseAsync` returns entries where `archived = 1`
- [ ] Implement `LogAuditAsync` — insert into `audit_log` table with `session_id`, `operation`, JSON-serialized detail
- [ ] Create indexes per architecture.md §4.4: `idx_nodes_layer`, `idx_nodes_type`, `idx_nodes_file`, `idx_nodes_since`, `idx_edges_semantic`, `idx_edges_layer`, `idx_edges_since`, `idx_expertise_target`, `idx_expertise_kind`, `idx_expertise_archived`, `idx_audit_session`

#### Test Scenarios
- [ ] Scenario: Node upsert and temporal query — Given `SqliteGraphStore`, When a node is upserted with `SinceIndex=1` and queried at `atCommitIndex=1`, Then the node is returned; When queried at `atCommitIndex=0`, Then the node is NOT returned
- [ ] Scenario: RetireNodeAsync cascades — Given a node with 3 connected edges, When `RetireNodeAsync(nodeId, untilIndex=5)` is called, Then the node and all 3 edges have `until_index=5`
- [ ] Scenario: Expertise archive lifecycle — Given an expertise entry with `Confidence=1.0`, When `ArchiveExpertiseAsync` is called, Then `GetExpertiseForNodeAsync` excludes it and `GetArchivedExpertiseAsync` includes it
- [ ] Scenario: External node temporal inclusion — Given an External boundary node with NULL `since_index`, When `GetNodesByLayerAsync` is called with any `atCommitIndex`, Then the External node is always included in results

### Stage 1.4: Neo4jMemoryGraphStore Implementation

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Storage/Neo4jMemoryGraphStore.cs` implementing `IMemoryGraphStore` by composing existing `Neo4jGraphStore` (from `Ast.GraphDb`) via delegation — zero files in `Ast.GraphDb/` modified (architecture.md §1.3, §3.2)
- [ ] Implement Cypher queries for `GetNodesByLayerAsync`, `GetEdgesBySemanticRelationAsync` with temporal filter matching architecture.md §4.3 (External nodes with NULL `since_index` always included)
- [ ] Implement `UpsertNodeAsync`, `RetireNodeAsync`, `RetireEdgeAsync` using ACID transactions for write serialization (architecture.md §3.7)
- [ ] Implement expertise CRUD against a separate `expertise` collection/node-label in Neo4j, with `ArchiveExpertiseAsync` setting `archived=true`
- [ ] Wire Qdrant (via existing `QdrantVectorStore` from `Ast.VectorDb`) for vector storage; embeddings stored in Qdrant, graph in Neo4j
- [ ] Implement `GetMemoryStatisticsAsync` and `LogAuditAsync` with Cypher

#### Test Scenarios
- [ ] Scenario: Neo4j node upsert round-trip — Given `Neo4jMemoryGraphStore` connected to Neo4j, When a node is upserted with `VersionRange(Since="abc", SinceIndex=1, null, null)`, Then `GetNodesByLayerAsync` at `atCommitIndex=1` returns it
- [ ] Scenario: Neo4j write serialization — Given two concurrent `UpsertNodeAsync` calls, When both complete, Then both nodes exist and the `audit_log` shows serialized timestamps (no interleaving)
- [ ] Scenario: Neo4j+Qdrant vector storage — Given a node with embedding `float[768]`, When upserted via `Neo4jMemoryGraphStore`, Then the embedding is stored in Qdrant and the graph node in Neo4j

### Stage 1.5: DI Registration and Test Infrastructure

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/ServiceCollectionExtensions.cs` with `AddMemoryGraph(Action<MemoryOptions>)` extension method — registers `SqliteGraphStore` or `Neo4jMemoryGraphStore` based on `MemoryOptions.Backend`, plus `AnnotationKeyRegistry` (architecture.md §7)
- [ ] Create `src/ast/Ast.Memory.UnitTests/Ast.Memory.UnitTests.csproj` with references to `Ast.Memory`, `Ast.Core`; add FluentAssertions, MSTest, Reqnroll.MsTest
- [ ] Create `src/ast/Ast.Memory.IntegrationTests/Ast.Memory.IntegrationTests.csproj` with references to `Ast.Memory`, `Ast.GraphDb`, `Ast.VectorDb`, `Ast.Embedding`
- [ ] Create `src/ast/Ast.Memory/Schema/AnnotationKeyRegistry.cs` — code-defined registry of well-known `Metadata` keys with typed schemas; keys without `ext:` prefix that are not registered produce a validation warning (architecture.md §4.5, HC11)

#### Test Scenarios
- [ ] Scenario: DI Sqlite registration — Given `MemoryOptions.Backend = Sqlite`, When `AddMemoryGraph` is called, Then `IMemoryGraphStore` resolves to `SqliteGraphStore`
- [ ] Scenario: DI Neo4j registration — Given `MemoryOptions.Backend = Neo4j`, When `AddMemoryGraph` is called, Then `IMemoryGraphStore` resolves to `Neo4jMemoryGraphStore`
- [ ] Scenario: AnnotationKeyRegistry validation — Given a `Metadata` dict with key `"ext:team-owner"`, When validated by `AnnotationKeyRegistry`, Then no warning is raised; Given key `"unknown-key"`, Then a validation warning is produced

## Phase 2: Extraction Pipeline

> **Goal:** Build the extraction pipeline that transforms source code into the
> four-layer memory graph: Structural → Call-Chain → Semantic. Reuses existing
> `Ast.Csharp`, `Ast.PowerShell` parsers and `Ast.Embedding` service.
>
> **Hard dependency:** Phase 1 (storage must exist).
> **Parallel-safe:** Stages 2.1 and 2.2 are independent after Stage 2.1 completes.

### Stage 2.1: StructureBuilder — Structural Layer

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Extraction/StructureBuilder.cs` — walks `HypergraphData` from existing parsers (`Ast.Csharp`, `Ast.PowerShell`) and emits Structural-layer nodes: `Repo` (Id=`"/"`), `Package` (Id=`"/{projectPath}"`), `Module` (Id=`"/{filePath}"`), `Class`/`Interface` (Id=`"/{filePath}::{qualifiedName}"`), `Function`/`Method`/`Property` (Id=`"/{filePath}::{qualifiedName}"`) — per architecture.md §4.2 ID format
- [ ] Emit `CONTAINS` edges with roles `parent`/`child` — mapped from existing `HyperedgeType.ClassDefinition`, `MethodDefinition`, etc. (architecture.md §2.2); each edge carries `Metadata["SemanticRelation"]="CONTAINS"`, `Metadata["MemoryLayer"]="structural"`, `Metadata["OriginalEdgeType"]` preserving original `HyperedgeType`
- [ ] Emit `DEPENDS_ON` edges with roles `importer`/`imported` — mapped from `UsingDirective`, `ModuleImport`, `DotSource` (architecture.md §2.2); for external imports, create `External` boundary nodes with Id=`"ext:{package}:{qualifiedName}"`, `NodeType="External"`, `MemoryLayer="call_chain"`, no `ContentHash`, no `VersionRange`, no `Embedding` (architecture.md §4.2)
- [ ] Set `Metadata["ContentHash"]` (SHA-256 of source text) on all in-repo nodes; set `VersionRange` from current commit
- [ ] Persist nodes and edges via `IMemoryGraphStore.UpsertNodeAsync` and inherited `IGraphStore` edge-creation methods

#### Test Scenarios
- [ ] Scenario: Structural hierarchy — Given `src/ast/Ast.Core/HypergraphModels.cs`, When `StructureBuilder` processes it, Then nodes exist for Package `/src/ast/Ast.Core`, Module `/src/ast/Ast.Core/HypergraphModels.cs`, Class `/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode`, and Function `/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText`, connected by `CONTAINS` edges with `parent`/`child` roles
- [ ] Scenario: External boundary node — Given a C# file with `using Microsoft.CodeAnalysis;`, When `StructureBuilder` processes it, Then an External node exists with Id `"ext:Microsoft.CodeAnalysis:*"`, `NodeType="External"`, `MemoryLayer="call_chain"`, NULL `since_index`, no `ContentHash`, no `Embedding`
- [ ] Scenario: DEPENDS_ON edge — Given a C# file importing `Ast.Core`, When `StructureBuilder` processes it, Then a `DEPENDS_ON` edge exists with `Metadata["SemanticRelation"]="DEPENDS_ON"` and roles `importer`/`imported`

### Stage 2.2: CallChainAnalyzer — Call-Chain Layer

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Extraction/CallChainAnalyzer.cs` — reads `Hyperedge` entries of types `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`, `ModuleFunctionCall`, `CrossLanguageMethodCall` and emits `CALLS` edges with roles `caller`/`callee` (architecture.md §2.2); direct calls have `Confidence=1.0`, dynamic dispatch `Confidence=0.5–0.7`
- [ ] Map `Extends` hyperedge type to `EXTENDS` edge with roles `subclass`/`superclass`; map `Implements` to `IMPLEMENTS` edge with roles `implementor`/`interface` (architecture.md §2.2, §2.3)
- [ ] Map `DataFlow`, `Assignment`, `CrossLanguageTypeReference` to `REFERENCES` edges with roles `source`/`target` (architecture.md §2.2)
- [ ] All edges use `Metadata["SemanticRelation"]` as sole filtering mechanism; code-derived edges retain original `HyperedgeType` enum value and record it in `Metadata["OriginalEdgeType"]` (architecture.md §4.6)
- [ ] Reverse call-chain lookups are performed by traversing `CALLS` edges backward, not by storing a separate reverse edge (architecture.md §2.3)

#### Test Scenarios
- [ ] Scenario: CALLS edge creation — Given `ServiceA.Process()` calls `RepositoryB.Save()`, When `CallChainAnalyzer` runs, Then a `CALLS` edge exists with `Metadata["SemanticRelation"]="CALLS"`, roles `caller`=ServiceA.Process, `callee`=RepositoryB.Save, and `Confidence=1.0`
- [ ] Scenario: EXTENDS edge — Given `PaymentController` extends `ControllerBase`, When `CallChainAnalyzer` runs, Then an `EXTENDS` edge exists with roles `subclass`/`superclass` and `Metadata["SemanticRelation"]="EXTENDS"`
- [ ] Scenario: No reverse-call stored edge — Given a processed repository, When all edges are queried, Then no edge has a reverse-call `SemanticRelation` — reverse lookups use backward traversal of `CALLS` edges only

### Stage 2.3: SemanticIndexer — Semantic Layer

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Extraction/SemanticIndexer.cs` — batches nodes through `IEmbeddingGenerator.GenerateBatchEmbeddingsAsync()` (from existing `Ast.Embedding`), stores embedding vectors in `Metadata["Embedding"]`; default model is `nomic-embed-text` per existing `EmbeddingGeneratorOptions.ModelName` default (architecture.md §1.3)
- [ ] Compute pairwise cosine similarity; create `SIMILAR_TO` edges (using `HyperedgeType.MemoryRelation`) where cosine ≥ `SimilarityThreshold` (0.85) with roles `node_a`/`node_b` and `Metadata["SimilarityScore"]` (architecture.md §2.2, §2.4)
- [ ] Store vectors in Qdrant (team mode via `IVectorStore`) or sqlite-vss (local mode); selection via `MemoryOptions.Backend`

#### Test Scenarios
- [ ] Scenario: Embedding generation — Given 10 code nodes, When `SemanticIndexer` processes them, Then each node has a non-null `Metadata["Embedding"]` of dimension 768
- [ ] Scenario: SIMILAR_TO edge threshold — Given two nodes with cosine similarity 0.90, When `SemanticIndexer` runs with `SimilarityThreshold=0.85`, Then a `SIMILAR_TO` edge exists between them; Given two nodes with similarity 0.80, Then no `SIMILAR_TO` edge exists
- [ ] Scenario: Graceful degradation — Given `IEmbeddingGenerator` is unavailable, When `SemanticIndexer` runs, Then it logs a warning and skips embedding generation without throwing

### Stage 2.4: FactNormalizer and IExtractionPipeline

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Extraction/FactNormalizer.cs` — deduplicates nodes across languages (e.g., C# class referenced by PowerShell caller) and assigns canonical IDs per architecture.md §4.2 format: `"/{relativePath}::{qualifiedName}"` for symbols, `"/{relativePath}"` for files
- [ ] Create `src/ast/Ast.Memory/Extraction/ExtractionPipeline.cs` implementing `IExtractionPipeline` (architecture.md §5.4) with two entry points: `ExtractAsync(repoRoot, ExtractionOptions, CancellationToken)` for full extraction, `ExtractFilesAsync(repoRoot, IReadOnlyList<string> relativePaths, ExtractionOptions, CancellationToken)` for incremental
- [ ] `ExtractAsync` orchestrates sequential pipeline: `StructureBuilder` → `CallChainAnalyzer` → `SemanticIndexer` (architecture.md §3.1 dependency chain)
- [ ] `ExtractionOptions` includes `Languages` (default `["C#", "PowerShell"]`), `CommitSha`, `IncludeTestProjects` (default false), `GenerateEmbeddings` (default true) — per architecture.md §5.4

#### Test Scenarios
- [ ] Scenario: Full extraction pipeline — Given a fixture repo with 6 C# files, When `ExtractAsync` runs, Then `ExtractionResult.NodesCreated > 0`, `EdgesCreated > 0`, `EmbeddingsGenerated > 0`, and `Errors` is empty
- [ ] Scenario: ExtractFilesAsync partial — Given a previously indexed repo, When `ExtractFilesAsync` is called with 2 changed files, Then only nodes/edges for those files are re-extracted (not the entire repo)
- [ ] Scenario: Cross-language dedup — Given a C# class `PaymentService` and a PowerShell script that calls it, When `FactNormalizer` runs, Then a single canonical node exists for `PaymentService` with edges from both languages

## Phase 3: Retrieval Engine and Context Assembly

> **Goal:** Build the hybrid retrieval engine (`IMemoryRetrievalEngine`) with
> five modes, 5-signal ranker, budget controller, and context assembler.
>
> **Hard dependency:** Phase 2 (graph must be populated).

### Stage 3.1: QueryPlanner and RetrievalMode Selection

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Retrieval/QueryPlanner.cs` — selects `RetrievalMode` values based on query shape: free-text → `Hybrid`; node-ID seed only → `StructuralDrill` + `CallTrace`; explicit mode in `RetrievalRequest.Mode` overrides automatic selection (architecture.md §3.3)
- [ ] Create `src/ast/Ast.Memory/Interfaces/IMemoryRetrievalEngine.cs` with single method `RetrieveAsync(RetrievalRequest, CancellationToken) → RetrievalResult` per architecture.md §5.2
- [ ] Create `src/ast/Ast.Memory/Models/RetrievalRequest.cs` with fields: `QueryText`, `SeedNodeIds`, `Mode` (default `Hybrid`), `TokenBudget` (default 8000, clamped to `[1, TokenBudgetCeiling]`), `AtCommitIndex`, `Weights` (`ScoringWeights?`) — per architecture.md §5.2
- [ ] Create `src/ast/Ast.Memory/Models/RetrievalResult.cs` with fields: `Nodes` (`IReadOnlyList<ScoredMemoryNode>`), `ContextMarkdown` (string), `TokensUsed` (int), `ModesExecuted` (`IReadOnlyList<RetrievalMode>`), `Diagnostics` (`RetrievalDiagnostics?` — per-mode candidate counts and timing) — per architecture.md §5.2
- [ ] Create `src/ast/Ast.Memory/Models/ScoredMemoryNode.cs` with `Node` (`MemoryNode`), `CompositeScore`, `StructuralScore`, `SemanticScore`, `CentralityScore`, `RecencyScore`, `ExpertiseScore` — per architecture.md §5.2
- [ ] Create `src/ast/Ast.Memory/Models/RetrievalDiagnostics.cs` with per-mode candidate counts and timing information — per architecture.md §5.2

#### Test Scenarios
- [ ] Scenario: Hybrid mode selection — Given a `RetrievalRequest` with `QueryText="payment processing"` and no `SeedNodeIds`, When `QueryPlanner.PlanQuery` is called, Then selected modes include `SemanticSearch` and at least one of `StructuralDrill`/`CallTrace`
- [ ] Scenario: Explicit mode override — Given a `RetrievalRequest` with `Mode=CallTrace`, When `QueryPlanner.PlanQuery` is called, Then only `CallTrace` mode is selected
- [ ] Scenario: TokenBudget clamping — Given a `RetrievalRequest` with `TokenBudget=99999`, When validated, Then budget is clamped to `TokenBudgetCeiling` (32000)

### Stage 3.2: Ranker — 5-Signal Scoring

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Retrieval/Ranker.cs` implementing the 5-signal weighted scoring model from architecture.md §2.4: `structural` (0.30, inverse BFS distance), `semantic` (0.25, cosine similarity), `centrality` (0.20, normalized fan-in PageRank-light), `recency` (0.15, `1.0 − (HEAD_idx − node_sinceIdx) / max_distance`, clamped [0,1]), `expertise` (0.10, max Confidence of APPLIES_TO edges)
- [ ] Implement graceful fallback: if `IEmbeddingGenerator` is unavailable at query time, set `semantic=0.0` and re-normalize remaining 4 weights to sum to 1.0 (architecture.md §3.3)
- [ ] Produce `List<ScoredMemoryNode>` sorted by descending `CompositeScore`

#### Test Scenarios
- [ ] Scenario: Scoring weight sum — Given default `ScoringWeights`, When `Ranker` scores nodes, Then individual signal scores are weighted by (0.30, 0.25, 0.20, 0.15, 0.10) and `CompositeScore` equals the sum
- [ ] Scenario: Semantic fallback — Given `IEmbeddingGenerator` is unavailable, When `Ranker` scores nodes, Then `SemanticScore=0.0` for all nodes and remaining weights are re-normalized to sum to 1.0
- [ ] Scenario: Expertise signal — Given a node with an `APPLIES_TO` expertise entry at `Confidence=0.8`, When scored, Then `ExpertiseScore=0.8` (max confidence of targeting expertise)

### Stage 3.3: BudgetController and ContextAssembler

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Context/BudgetController.cs` — takes ranked `List<ScoredMemoryNode>` and token budget (default 8000, ceiling 32000); when budget is tight, collapses child nodes into parent summaries (HiAgent pattern per architecture.md §3.4); deduplicates same code span reached via multiple paths
- [ ] Create `src/ast/Ast.Memory/Context/ContextAssembler.cs` — transforms trimmed ranked nodes into deterministic Markdown with sections: `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise` (architecture.md §3.4)
- [ ] Implement token counting using a tokenizer (tiktoken or equivalent); respect `TokenBudgetDefault` (8000) and `TokenBudgetCeiling` (32000)

#### Test Scenarios
- [ ] Scenario: Budget enforcement — Given 50 ranked nodes totaling 16000 tokens and budget 8000, When `BudgetController` runs, Then output tokens ≤ 8000 and highest-scored nodes are retained
- [ ] Scenario: HiAgent collapse — Given 10 child function nodes under a parent class and budget 2000, When `BudgetController` runs, Then children are collapsed into parent summary and output tokens ≤ 2000
- [ ] Scenario: Context Markdown sections — Given ranked nodes spanning all 4 layers, When `ContextAssembler` produces output, Then the Markdown contains exactly the sections `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise` in that order

### Stage 3.4: MemoryRetrievalEngine Integration

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Retrieval/MemoryRetrievalEngine.cs` implementing `IMemoryRetrievalEngine` — composes `QueryPlanner`, `Ranker`, `BudgetController`, `ContextAssembler`; orchestrates the sequence: plan → per-mode traversal/search → rank → budget → assemble (architecture.md §6.2)
- [ ] For `StructuralDrill` mode: BFS from seed along `CONTAINS`/`DEPENDS_ON` edges via `IMemoryGraphStore`
- [ ] For `CallTrace` mode: forward/backward traversal along `CALLS`/`EXTENDS`/`IMPLEMENTS` edges; reverse call-chain is achieved by traversing `CALLS` edges backward, not via a separate stored edge (architecture.md §2.3)
- [ ] For `SemanticSearch` mode: embed query text via `IEmbeddingGenerator.GenerateEmbeddingAsync()`, ANN lookup via `IVectorStore.SearchAsync()`; exactly one local embedding call per query (architecture.md HC5)
- [ ] For `ExpertiseRecall` mode: filter nodes by `APPLIES_TO` edges from expertise layer
- [ ] Edge filtering uses `Metadata["SemanticRelation"]` exclusively (architecture.md §2.2, §3.3)

#### Test Scenarios
- [ ] Scenario: Hybrid retrieval — Given a populated graph and query "payment processing", When `RetrieveAsync` is called with `Mode=Hybrid`, Then `RetrievalResult.Nodes` is non-empty, `ContextMarkdown` is non-empty, `ModesExecuted` contains multiple modes, and `TokensUsed ≤ TokenBudget`
- [ ] Scenario: Structural drill from seed — Given seed node `/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode`, When `RetrieveAsync` is called with `Mode=StructuralDrill`, Then result includes child function nodes and parent package node
- [ ] Scenario: Edge filtering by SemanticRelation — Given the retrieval engine, When traversing call-chain, Then all edge lookups use `Metadata["SemanticRelation"]` as the filter key (not `HyperedgeType`)

## Phase 4: Incremental Sync

> **Goal:** Keep the graph current without full re-extraction. Detect
> changed files via git diff, re-extract, upsert/retire nodes and edges,
> trigger expertise decay.
>
> **Hard dependency:** Phases 1–2 (storage + extraction).
> **Parallel-safe with Phase 3:** Can be developed concurrently with retrieval.

### Stage 4.1: GitDiffDriver

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Sync/GitDiffDriver.cs` — wraps `git diff --name-status fromCommit..toCommit` to produce a list of `(status, relativePath)` tuples for added, modified, and deleted files (architecture.md §3.6)
- [ ] Handle edge cases: renamed files (detect via `git diff -M`), binary files (skip), submodule changes (skip)

#### Test Scenarios
- [ ] Scenario: Diff detection — Given commits A and B where file `Service.cs` was modified and `OldFile.cs` was deleted, When `GitDiffDriver.GetChangedFiles(A, B)` is called, Then result contains `(Modified, "Service.cs")` and `(Deleted, "OldFile.cs")`
- [ ] Scenario: Rename detection — Given a file renamed from `Old.cs` to `New.cs`, When `GitDiffDriver.GetChangedFiles` is called with rename detection, Then result contains a rename entry mapping old to new path

### Stage 4.2: IncrementalSync Implementation

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Sync/IncrementalSync.cs` implementing `IIncrementalSync` with `ApplyDiffAsync(string repoRoot, string fromCommit, string toCommit, CancellationToken)` per architecture.md §5.4
- [ ] For modified files: re-extract via `IExtractionPipeline.ExtractFilesAsync`, diff old vs new nodes, upsert changes via `IMemoryGraphStore.UpsertNodeAsync`, set `UntilIndex` on removed nodes via `RetireNodeAsync` (architecture.md §3.6)
- [ ] For deleted files: set `UntilIndex` on all nodes/edges rooted in that file via `RetireNodeAsync` (cascades to edges) plus `RetireEdgeAsync` for orphaned edges (architecture.md §3.6)
- [ ] Propagate invalidation: mark downstream `CALLS` edges as stale via `UpdateEdgeMetadataAsync(edgeId, { "Stale": true })` (architecture.md §3.6)
- [ ] Trigger event-driven expertise decay: when target node's `ContentHash` changes, apply `StaleDecayFactor` (0.5) for substantive changes or `MinorDecayFactor` (0.9) for whitespace/comment-only changes (architecture.md §3.5)
- [ ] Re-run `SemanticIndexer` on changed nodes only (incremental embedding)
- [ ] Return `SyncResult` with `FilesChanged`, `NodesAdded`, `NodesRetired`, `EdgesAdded`, `EdgesRetired`, `ExpertiseDecayed`, `Duration` (architecture.md §5.4)

#### Test Scenarios
- [ ] Scenario: Modified file sync — Given an indexed repo, When a file is modified and `ApplyDiffAsync` is called, Then changed nodes are upserted with new `ContentHash`, old nodes are retired with `UntilIndex`, and `SyncResult.FilesChanged > 0`
- [ ] Scenario: Deleted file sync — Given an indexed file with 5 nodes, When the file is deleted and `ApplyDiffAsync` runs, Then all 5 nodes and their edges have `until_index` set and `SyncResult.NodesRetired = 5`
- [ ] Scenario: Expertise decay on change — Given an expertise entry targeting a node, When the node's `ContentHash` changes during sync, Then expertise `Confidence` is multiplied by `StaleDecayFactor` (0.5)
- [ ] Scenario: Stale edge propagation — Given function A calls function B, When function B is modified, Then the `CALLS` edge from A to B has `Metadata["Stale"]=true`

### Stage 4.3: File-System Watcher Integration

#### Implementation Steps
- [ ] Add `FileSystemWatcher`-based trigger to `IncrementalSync` — watches for file saves (`.cs`, `.ps1`, `.psm1`, `.psd1`) in the repo with `IncrementalSyncDebounceMs` (1000 ms) debounce (architecture.md §3.6); coalesces rapid saves into a single sync batch
- [ ] On trigger: collect changed file paths from debounced events, re-extract via `IExtractionPipeline.ExtractFilesAsync(repoRoot, changedPaths, opts)`, diff new extraction results against existing graph state (lookup existing nodes by file path via `IMemoryGraphStore`), upsert changed nodes, retire removed nodes/edges, and apply expertise decay — the same upsert/retire/decay logic as `ApplyDiffAsync` but driven by filesystem events instead of `git diff` (architecture.md §3.6); the working-tree state serves as the "to" snapshot while the current graph state serves as the "from" baseline
- [ ] Track last-synced state per file to avoid redundant re-extraction on unchanged content (`ContentHash` comparison)

#### Test Scenarios
- [ ] Scenario: Debounce — Given rapid file saves (3 saves within 500ms), When the watcher triggers, Then re-extraction runs exactly once with the combined set of changed files (debounced)
- [ ] Scenario: Watcher resilience — Given the watcher is running and a file save triggers an extraction error, When the error is caught, Then the watcher continues running and logs the error
- [ ] Scenario: ContentHash skip — Given a file save that does not change the file's content (e.g., save without edits), When the watcher triggers and `ContentHash` matches the existing graph node, Then no upsert or decay occurs

## Phase 5: Evolution Engine

> **Goal:** Build the expertise write-back, consolidation, decay, and
> community detection components that allow the graph to improve across
> agent sessions.
>
> **Hard dependency:** Phases 1–2 (storage + extraction).
> **Parallel-safe with Phases 3–4:** Can be developed concurrently.

### Stage 5.1: Expertise Write-Back

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Interfaces/IEvolutionEngine.cs` per architecture.md §5.3 with methods: `RecordExpertiseAsync(targetNodeId, ExpertiseKind kind, string title, string body, string sourceAgent, string scope = "exact", IReadOnlyList<Citation>? evidence = null, CancellationToken) → Task<ExpertiseEntry>`, `ConsolidateAsync(CancellationToken) → Task<ConsolidationResult>`, `DecayStaleExpertiseAsync(CancellationToken) → Task<DecayResult>`, `RestoreArchivedExpertiseAsync(string expertiseId, double restoredConfidence = 0.5, CancellationToken) → Task<ExpertiseEntry>`, `DetectCommunitiesAsync(CancellationToken) → Task<CommunityResult>`
- [ ] Create `src/ast/Ast.Memory/Models/EvolutionResults.cs` with result types: `ConsolidationResult` (MergedCount, SupersededCount), `DecayResult` (DecayedCount, ArchivedCount), `CommunityResult` (CommunitiesDetected, TopicNodesCreated) — per architecture.md §5.3
- [ ] Create `src/ast/Ast.Memory/Evolution/EvolutionEngine.cs` implementing `IEvolutionEngine`; `RecordExpertiseAsync` validates target node exists, generates ID as `"expertise:{kind}:{hash8}"` (hash8 = first 8 chars of SHA-256(Title+Body+TargetNodeId)), embeds `Title + " " + Body` via `IEmbeddingGenerator`, creates `APPLIES_TO` edge with `HyperedgeType.MemoryRelation` and roles `expertise`/`target`, persists via `IMemoryGraphStore.UpsertExpertiseAsync` (architecture.md §6.3)
- [ ] Log all write-back operations via `IMemoryGraphStore.LogAuditAsync`

#### Test Scenarios
- [ ] Scenario: Record expertise — Given a target node `/src/Services/PaymentService.cs::PaymentService.processRefund`, When `RecordExpertiseAsync(targetNodeId, Pitfall, "Fragile null-handling", "processRefund assumes non-null return from DB", "agent-session-1")` is called, Then an `ExpertiseEntry` is stored with `Kind=Pitfall`, an `APPLIES_TO` edge exists with roles `expertise`/`target`, and the `audit_log` records the operation
- [ ] Scenario: Duplicate detection — Given an identical expertise entry (same Title+Body+TargetNodeId) already exists, When `RecordExpertiseAsync` is called again, Then the existing entry is returned (deduplicated via `ContentHash`)
- [ ] Scenario: RestoreArchivedExpertiseAsync — Given an archived expertise entry, When `RestoreArchivedExpertiseAsync(expertiseId, 0.5)` is called, Then `Archived` is set to false and `Confidence` is set to 0.5

### Stage 5.2: ConsolidationService

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Evolution/ConsolidationService.cs` — LLM-powered merge of semantically similar expertise entries (cosine similarity of embeddings ≥ `SimilarityThreshold` 0.85); uses configurable LLM endpoint (default: local Qwen 3.6 via Ollama) per operator answer
- [ ] Create `SUPERSEDES` edge from merged entry to each original with roles `newer`/`older`; create `DERIVED_FROM` edge with roles `derived`/`source` (architecture.md §3.5)
- [ ] Acquire write-level lock during consolidation to prevent concurrent merges (architecture.md §3.7)

#### Test Scenarios
- [ ] Scenario: Consolidation merge — Given two expertise entries targeting the same node with cosine similarity 0.90, When `ConsolidateAsync` runs, Then a new merged entry exists with `SUPERSEDES` edges to both originals
- [ ] Scenario: Below-threshold skip — Given two expertise entries with cosine similarity 0.70, When `ConsolidateAsync` runs, Then no merge occurs and both entries remain independent
- [ ] Scenario: Consolidation lock — Given `ConsolidateAsync` is running, When a second `ConsolidateAsync` is called concurrently, Then the second call waits for the first to complete (serialized)

### Stage 5.3: DecayService

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Evolution/DecayService.cs` implementing time-based decay: `decayed_confidence = current_confidence × 2^(−age_days / DecayHalfLifeDays) × centrality_boost` where `centrality_boost = 1.0 + CentralityBoostFactor × normalized_pagerank` (range 1.0–1.5); uses base-2 exponential with `DecayHalfLifeDays=1095` (3 years) per architecture.md §3.5, §2.4
- [ ] Event-driven decay (called by `IncrementalSync`): `StaleDecayFactor` (0.5) on substantive code changes, `MinorDecayFactor` (0.9) on whitespace/comment-only changes — applied multiplicatively with time-based decay (architecture.md §3.5)
- [ ] Archive entries whose `Confidence` drops below `ArchiveConfidenceFloor` (0.1) via `IMemoryGraphStore.ArchiveExpertiseAsync` (sets `archived=true`, excluded from `GetExpertiseForNodeAsync` per architecture.md §5.1)

#### Test Scenarios
- [ ] Scenario: Time-based decay — Given an expertise entry created 1095 days ago with `Confidence=1.0` and `centrality_boost=1.0`, When `DecayStaleExpertiseAsync` runs, Then `Confidence ≈ 0.5` (half-life)
- [ ] Scenario: Centrality boost — Given an expertise entry targeting a high-centrality node (normalized PageRank=1.0), When decay runs, Then `centrality_boost = 1.0 + 0.5 × 1.0 = 1.5`, slowing decay by 50%
- [ ] Scenario: Archive on low confidence — Given an expertise entry with `Confidence=0.08` (below `ArchiveConfidenceFloor` 0.1), When `DecayStaleExpertiseAsync` runs, Then the entry is archived via `ArchiveExpertiseAsync` and excluded from `GetExpertiseForNodeAsync`

### Stage 5.4: Community Detection

#### Implementation Steps
- [ ] Implement `EvolutionEngine.DetectCommunitiesAsync()` — Louvain algorithm over `CALLS`/`SIMILAR_TO` edges; creates `BELONGS_TO_CLUSTER` edges with roles `member`/`cluster` and `Topic` summary nodes (Id=`"topic:{hash8}"`, NodeType=`"Topic"`, `Metadata["MemoryLayer"]="semantic"`, `Metadata["Summary"]`, `Metadata["MemberCount"]`) in the Semantic layer (architecture.md §3.5, §4.2)
- [ ] Topic summaries generated via configurable LLM (default: local Qwen 3.6)

#### Test Scenarios
- [ ] Scenario: Community detection — Given a graph with 20+ nodes and CALLS/SIMILAR_TO edges forming 3 natural clusters, When `DetectCommunitiesAsync` runs, Then at least 3 `Topic` nodes are created with `BELONGS_TO_CLUSTER` edges connecting member nodes
- [ ] Scenario: Topic node structure — Given a detected community, When its Topic node is inspected, Then `NodeType="Topic"`, `Metadata["MemoryLayer"]="semantic"`, `Metadata["Summary"]` is a non-empty LLM-generated string, and `Metadata["MemberCount"]` matches the cluster size

## Phase 6: MCP Tools, CLI, and Integration

> **Goal:** Expose the memory graph to agents via MCP tools and CLI commands.
> Wire everything together with end-to-end integration tests.
>
> **Hard dependency:** Phases 1–5 (all components must exist).

### Stage 6.1: MCP Tool Registration

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Mcp/MemoryMcpToolRegistration.cs` — registers 5 MCP tools per architecture.md §3.8: `memory_query` (→ `IMemoryRetrievalEngine.RetrieveAsync`), `memory_status` (→ `IMemoryGraphStore.GetMemoryStatisticsAsync`), `memory_record_expertise` (→ `IEvolutionEngine.RecordExpertiseAsync`), `memory_sync` (→ `IIncrementalSync.ApplyDiffAsync`), `memory_consolidate` (→ `IEvolutionEngine.ConsolidateAsync`)
- [ ] Create `src/ast/Ast.Memory/Mcp/MemoryMcpHandler.cs` — handles MCP tool invocations, maps JSON parameters to typed requests, returns JSON responses
- [ ] `memory_record_expertise` accepts parameters: `targetNodeId`, `kind`, `title`, `body`, `sourceAgent`, optional `scope`, optional `evidence` (architecture.md §5.3 RecordExpertiseAsync signature)

#### Test Scenarios
- [ ] Scenario: MCP tool registration — Given `MemoryMcpToolRegistration`, When tools are enumerated, Then exactly 5 tools are registered: `memory_query`, `memory_status`, `memory_record_expertise`, `memory_sync`, `memory_consolidate`
- [ ] Scenario: memory_query roundtrip — Given a populated graph, When `memory_query` MCP tool is invoked with `{"queryText": "payment", "tokenBudget": 4000}`, Then the response contains `contextMarkdown` and `tokensUsed ≤ 4000`
- [ ] Scenario: memory_record_expertise via MCP — Given a valid target node, When `memory_record_expertise` is invoked with `{"targetNodeId": "/src/X.cs::Y", "kind": "Pitfall", "title": "...", "body": "...", "sourceAgent": "test"}`, Then an `ExpertiseEntry` is created and returned

### Stage 6.2: CLI Commands

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Cli/MemoryCommandModule.cs` — Spectre.Console.Cli commands: `memory index <repo-path>` (invokes `IExtractionPipeline.ExtractAsync`), `memory query <text>` (invokes `IMemoryRetrievalEngine.RetrieveAsync`), `memory sync <from> <to>` (invokes `IIncrementalSync.ApplyDiffAsync`), `memory stats` (invokes `IMemoryGraphStore.GetMemoryStatisticsAsync`), `memory consolidate` (invokes `IEvolutionEngine.ConsolidateAsync`)
- [ ] Add `config.AddBranch("memory", ...)` to `src/ast/Ast.Cli/Program.cs` — additive only, appended after existing 7 `config.AddCommand<T>()` registrations (architecture.md §1.3); add project reference to `Ast.Memory` in `Ast.Cli.csproj`

#### Test Scenarios
- [ ] Scenario: CLI registration — Given `Ast.Cli` with the memory branch, When `ast memory --help` is run, Then subcommands `index`, `query`, `sync`, `stats`, `consolidate` are listed
- [ ] Scenario: CLI index command — Given a repository path, When `ast memory index ./src/ast/` is run, Then the extraction pipeline runs and the graph is populated (verified via `memory stats`)

### Stage 6.3: End-to-End Integration Tests

#### Implementation Steps
- [ ] Create integration test: full lifecycle — index `src/ast/Ast.Core/` → query "HypergraphNode" → verify structural + call-chain results → record expertise → query again → verify expertise appears in `## Known Expertise` section
- [ ] Create integration test: incremental sync — index repo at commit A → modify file → sync to commit B → verify changed nodes upserted, removed nodes retired, expertise decayed
- [ ] Create integration test: cross-session learning — session 1 records Pitfall expertise → session 2 retrieves context and finds the Pitfall in results (architecture.md §6.5)
- [ ] Create integration test: both storage backends — run the same test suite against `SqliteGraphStore` and `Neo4jMemoryGraphStore` to verify behavioral equivalence

#### Test Scenarios
- [ ] Scenario: Full lifecycle — Given `src/ast/Ast.Core/` is indexed, When `RetrieveAsync("HypergraphNode", Mode=Hybrid)` is called, Then result contains the `HypergraphNode` class node with `StructuralScore > 0`, and after recording a Pitfall expertise on it, a second `RetrieveAsync` includes the expertise in `ContextMarkdown` under `## Known Expertise`
- [ ] Scenario: Storage backend equivalence — Given the same fixture repo, When indexed and queried on both `SqliteGraphStore` and `Neo4jMemoryGraphStore`, Then `RetrievalResult.Nodes` contains the same node IDs (order may differ)
- [ ] Scenario: Performance SLO — Given a graph with 10000 nodes and 50000 edges, When `RetrieveAsync` is called with `TokenBudget=8000`, Then response time is < 500ms p95 (HC5 latency SLO)
