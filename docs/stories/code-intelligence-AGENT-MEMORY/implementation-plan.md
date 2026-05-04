# Implementation Plan — Agent Memory

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
> - [`e2e-scenarios.md`](e2e-scenarios.md) — Gherkin acceptance scenarios

## Approach

This plan extends the **existing `src/ast/` C# infrastructure** (24 `.csproj`
projects, 13 test projects, 970+ tests) with a single new project —
**`Ast.Memory`** — plus `Ast.Memory.UnitTests` and
`Ast.Memory.IntegrationTests`. **No existing project is modified** except for
one additive enum value (`HyperedgeType.MemoryRelation`) in `Ast.Core`.

All new interfaces (`IMemoryGraphStore`, `IMemoryRetrievalEngine`,
`IEvolutionEngine`, `IExtractionPipeline`, `IIncrementalSync`) are defined
**within `Ast.Memory`**. Existing projects are referenced via project
dependencies, never mutated (architecture.md §3.0).

The graph schema and lifecycle follow the taxonomy from the Graph-based Agent
Memory survey (Yang et al., arXiv:2602.05665) — **Extraction → Storage →
Retrieval → Evolution** — adapted to code intelligence. Four orthogonal graph
layers (Structural, Call-Chain, Semantic, Expertise) with temporal versioning
via dual-axis `VersionRange` (commit-index + wall-clock).

**Technology stack (anchored to existing repo):**

| Concern | Existing project | How memory layer uses it |
|---------|------------------|------------------------|
| AST parsing | `Ast.Core` (`HypergraphNode`, `Hyperedge`, `HyperedgeType` — 44 enum values), `Ast.Csharp`, `Ast.PowerShell` | Reference types via `Metadata` dict. One additive enum value `MemoryRelation`. |
| Graph storage | `Ast.GraphDb` / `IGraphStore` / `Neo4jGraphStore` | `Ast.Memory` defines `IMemoryGraphStore` extending `IGraphStore`. `Neo4jMemoryGraphStore` composes `Neo4jGraphStore` via delegation. |
| Embeddings | `Ast.Embedding` / `IEmbeddingGenerator` / `CodeEmbeddingService` | Reuse directly — calls `GenerateBatchEmbeddingsAsync()`. Default model: `"nomic-embed-text"`. |
| Vector search | `Ast.VectorDb` / `IVectorStore` / `QdrantVectorStore` | Reuse for team mode. Add in-process HNSW adapter for local mode. |
| Queries | `Ast.Query` / `ICodeQuery` / `HybridQueryService` | Reference only. `IMemoryRetrievalEngine` composes `ICodeQuery` internally. |
| Code analysis | `CodeIntelligence` (27 analyzers, 4 detectors) | Consume as call-chain / structural edge source. No modifications. |

**Project layout** (architecture.md §3.0):

```
src/ast/
  Ast.Memory/                     ← NEW project
    Ast.Memory.csproj
    Interfaces/                   ← IMemoryGraphStore, IMemoryRetrievalEngine, IEvolutionEngine, IExtractionPipeline, IIncrementalSync
    Models/                       ← MemoryNode, MemoryEdge, ExpertiseEntry, Enums, MemoryMetadataKeys
    Extraction/                   ← StructureBuilder, CallChainAnalyzer, SemanticIndexer, FactNormalizer
    Context/                      ← ContextAssembler, BudgetController
    Evolution/                    ← EvolutionEngine, ConsolidationService, DecayService
    Retrieval/                    ← MemoryRetrievalEngine, QueryPlanner, Ranker, ScoringWeights
    Sync/                         ← IncrementalSync, GitDiffDriver
    Storage/                      ← Neo4jMemoryGraphStore, SqliteGraphStore
    Schema/                       ← AnnotationKeyRegistry
    Mcp/                          ← MemoryMcpToolRegistration, MemoryMcpHandler, McpSchemas
    Cli/                          ← MemoryCommandModule
    ServiceCollectionExtensions.cs
  Ast.Memory.UnitTests/           ← NEW
  Ast.Memory.IntegrationTests/    ← NEW
```

---

## Phase 1: Memory Schema & Storage Foundation

> **Goal:** Define the memory-layer data model (composition wrappers, expertise
> entries, temporal versioning, audit log) and implement `IMemoryGraphStore`
> backed by Neo4j + Qdrant.
>
> **Hard dependency:** None — this is the foundation phase.

### Stage 1.1: Project Scaffold and Core Models

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Ast.Memory.csproj` targeting `net8.0` with project references to `Ast.Core`, `Ast.GraphDb`, `Ast.Embedding`, `Ast.VectorDb`, `Ast.Query`; add to `Workflow.sln`
- [ ] Create `src/ast/Ast.Memory/Models/Enums.cs` with `MemoryLayer` enum (`Structural`, `CallChain`, `Semantic`, `Expertise`), `ExpertiseKind` enum (`Pattern`, `Pitfall`, `Preference`, `Insight`), `AuditOperation` enum (`Create`, `Update`, `Delete`, `Annotate`), and `RetrievalMode` enum (`StructuralDrill`, `CallTrace`, `SemanticSearch`, `ExpertiseRecall`, `Hybrid`)
- [ ] Create `src/ast/Ast.Memory/Models/VersionRange.cs` as `record VersionRange(string Since, int SinceIndex, string? Until, int? UntilIndex)` per architecture.md §4.3
- [ ] Create `src/ast/Ast.Memory/Models/MemoryNode.cs` as a **composition wrapper** — contains `HypergraphNode Inner { get; }` property (NOT inheritance) with strongly-typed accessors for `Metadata["MemoryLayer"]`, `Metadata["VersionRange"]`, `Metadata["ContentHash"]`, `Metadata["LastTouched"]`, `Metadata["Embedding"]`, `Metadata["EmbeddingModel"]`, `Metadata["Summary"]`, `Metadata["QualifiedName"]` (architecture.md §4.1)
- [ ] Create `src/ast/Ast.Memory/Models/MemoryEdge.cs` as a **composition wrapper** — contains `Hyperedge Inner { get; }` property (NOT inheritance) with strongly-typed accessors for `Metadata["MemoryLayer"]`, `Metadata["SemanticRelation"]`, `Metadata["VersionRange"]`, `Metadata["Confidence"]`, `Metadata["Stale"]`, `Metadata["OriginalEdgeType"]` (architecture.md §4.1)
- [ ] Create `src/ast/Ast.Memory/Models/ExpertiseEntry.cs` with `Kind` (`ExpertiseKind`), `Title`, `Body`, `AppliesTo` (node ID list), `Scope` ("exact"\|"subtree"), `Evidence` (list of `Citation`), `SourceAgent` — matching architecture.md §5.4
- [ ] Create `src/ast/Ast.Memory/Models/AuditEntry.cs` with `Operation` (`AuditOperation`), `EntityType`, `EntityId`, `SessionId`, `Timestamp`, `DetailJson`
- [ ] Create `src/ast/Ast.Memory/Models/MemoryMetadataKeys.cs` as static constants for all curated `Metadata` key names (e.g., `MemoryLayer`, `VersionRange`, `ContentHash`, `SemanticRelation`, `Confidence`, `Stale`) to avoid magic strings
- [ ] Add `HyperedgeType.MemoryRelation` enum value to `src/ast/Ast.Core/HypergraphModels.cs` — the sole modification to any existing project (architecture.md §4.6)

#### Test Scenarios
- [ ] Scenario: MemoryNode composition — Given a `HypergraphNode` with `Metadata["MemoryLayer"]="structural"` and `Metadata["VersionRange"]=new VersionRange("abc123",42,null,null)`, When wrapped in a `MemoryNode`, Then `memoryNode.Inner` is the original node, `memoryNode.Layer` returns `"structural"`, and `memoryNode.Version.SinceIndex` returns `42`
- [ ] Scenario: MemoryEdge composition — Given a `Hyperedge` with `EdgeType=HyperedgeType.MemoryRelation` and `Metadata["SemanticRelation"]="APPLIES_TO"`, When wrapped in a `MemoryEdge`, Then `memoryEdge.Inner` is the original edge and `memoryEdge.SemanticRelation` returns `"APPLIES_TO"`
- [ ] Scenario: ExpertiseEntry validation — Given an `ExpertiseEntry` with empty `Title`, When validated, Then a validation error lists "Title is required"
- [ ] Scenario: MemoryRelation enum addition — Given the updated `Ast.Core`, When all existing `Ast.Core.UnitTests` are run, Then zero tests regress (additive-only change)

### Stage 1.2: IMemoryGraphStore Interface and Supporting Types

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Interfaces/IMemoryGraphStore.cs` extending `IGraphStore` (from `Ast.GraphDb`) with methods: `ApplyDiffAsync(GraphDiff, string commitSha)`, `TraverseAsync(string startNodeId, TraversalPlan)`, `VectorSearchAsync(float[] queryVector, int topK, NodeFilter?)`, `GetNodeAtCommitAsync(string nodeId, string commitSha)`, `FindByNameAsync(string prefix, IReadOnlyList<string>? kinds, int limit)`, `RebuildVectorIndexAsync()`, `ResolveCommitIndexAsync(string commitSha)`, `RegisterCommitAsync(string commitSha, string? parentSha, DateTime timestamp)` — matching architecture.md §5.2 exactly
- [ ] Create `src/ast/Ast.Memory/Models/TraversalPlan.cs` with `Direction` ("outbound"\|"inbound"\|"both"), `SemanticRelations` (primary edge filter — canonical relation names from §2.2), deprecated `EdgeTypes`, `LayerFilter`, `MaxDepth` (default 3), `Filter` (`NodeFilter`), `Limit` (default 100)
- [ ] Create `src/ast/Ast.Memory/Models/TraversalResult.cs` with `Nodes`, `Edges`, `Paths`
- [ ] Create `src/ast/Ast.Memory/Models/GraphFact.cs` as `abstract record GraphFact(string Id, string ContentHash, VersionRange VersionRange)` with derived `NodeFact` and `EdgeFact` records — matching architecture.md §5.1
- [ ] Create `src/ast/Ast.Memory/Models/GraphDiff.cs` as `record GraphDiff(IReadOnlyList<GraphFact> Inserts, IReadOnlyList<GraphFact> Updates, IReadOnlyList<string> Tombstones)`
- [ ] Create `src/ast/Ast.Memory/Models/Snapshot.cs` with `Id`, `CommitSha`, `CommitIndex`, `ParentSha`, `CreatedAt`, nested `SnapshotStats` (Nodes, Edges, ExpertiseNodes) — matching architecture.md §4.8
- [ ] Create `src/ast/Ast.Memory/Models/Citation.cs` with `NodeId`, `CommitSha`, `Snippet`
- [ ] Create `src/ast/Ast.Memory/Models/MemoryOptions.cs` with all configurable constants from architecture.md §2.4: `SimilarityThreshold` (0.85), `StaleDecayFactor` (0.5), `MinorDecayFactor` (0.9), `ArchiveConfidenceFloor` (0.1), `DecayHalfLifeDays` (1095), `TokenBudgetDefault` (8000), `TokenBudgetCeiling` (32000), `IncrementalSyncDebounceMs` (1000), and scoring weights `ScoringWeights` record (structural=0.30, semantic=0.25, centrality=0.20, recency=0.15, expertise=0.10)

#### Test Scenarios
- [ ] Scenario: IMemoryGraphStore contract — Given the `IMemoryGraphStore` interface, When compiled, Then it extends `IGraphStore` and exposes `ApplyDiffAsync`, `TraverseAsync`, `VectorSearchAsync`, `GetNodeAtCommitAsync`, `FindByNameAsync`, `RebuildVectorIndexAsync`, `ResolveCommitIndexAsync`, `RegisterCommitAsync`
- [ ] Scenario: MemoryOptions defaults — Given default `MemoryOptions`, When constructed, Then `SimilarityThreshold`=0.85, `TokenBudgetDefault`=8000, `TokenBudgetCeiling`=32000, `DecayHalfLifeDays`=1095, and scoring weights sum to 1.00
- [ ] Scenario: TraversalPlan SemanticRelations filter — Given a `TraversalPlan` with `SemanticRelations=["CALLS","EXTENDS"]`, When inspected, Then `EdgeTypes` is null (deprecated) and `SemanticRelations` contains exactly those two entries

### Stage 1.3: Neo4jMemoryGraphStore Implementation

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Storage/Neo4jMemoryStore.cs` implementing `IMemoryStore` by composing the existing `Neo4jGraphStore` and adding Cypher queries for annotation CRUD, temporal filtering (`WHERE n.valid_from <= $version AND (n.valid_until IS NULL OR n.valid_until > $version)`), and audit logging
- [ ] Add Neo4j indexes for `valid_from`, `valid_until`, `content_hash`, and `session_id` properties in `CreateIndexesAsync` override
- [ ] Implement `TombstoneNodeAsync` — sets `valid_until` on node and all connected edges rather than deleting
- [ ] Implement `GetNodeAtVersionAsync` — parameterized Cypher query for point-in-time node retrieval
- [ ] Register `Neo4jMemoryStore` in `src/ast/Ast.Memory/ServiceCollectionExtensions.cs` via DI

#### Test Scenarios
- [ ] Scenario: Annotation persistence — Given a MemoryStore connected to Neo4j, When `UpsertAnnotationAsync` is called with key="known_issues" value="null-deref on empty list", Then `GetAnnotationsAsync` for that node returns the annotation with correct SessionId and ValidFrom
- [ ] Scenario: Tombstone preserves history — Given a node with two annotations, When `TombstoneNodeAsync` is called, Then the node's ValidUntil is set to the given commit SHA and both annotations get ValidUntil set, but `GetNodeAtVersionAsync` with the original commit SHA still returns the node
- [ ] Scenario: Audit trail completeness — Given a sequence of Create, Annotate, Tombstone operations, When `GetAuditLogAsync` is queried, Then all three entries appear in chronological order with correct operation types

### Stage 1.4: Evaluation Fixtures and Gold Sets

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory.UnitTests/Fixtures/` directory with 2 small C# fixture projects: (a) `SimpleApi` — 3 controllers, 2 services, 1 repository with known call chains; (b) `LibraryWithCycles` — circular dependency between 2 modules
- [ ] Create `src/ast/Ast.Memory.UnitTests/GoldSets/` with JSON files defining expected nodes, edges, and call-chain results for each fixture
- [ ] Create `src/ast/Ast.Memory.UnitTests/EvalHarness.cs` — loads gold sets, runs queries against an in-memory graph, computes precision/recall per query
- [ ] Add `Ast.Memory.UnitTests.csproj` to solution with references to `Ast.Memory`, `Ast.Core.UnitTests` (shared helpers), FluentAssertions, MSTest

#### Test Scenarios
- [ ] Scenario: Gold set loading — Given the SimpleApi gold set JSON, When loaded by EvalHarness, Then it contains at least 15 expected nodes and 20 expected edges
- [ ] Scenario: Fixture project parsing — Given the SimpleApi fixture project, When parsed by the existing `RepositoryProcessor`, Then all classes and methods from the 3 controllers appear as HypergraphNodes

---

## Phase 2: Graph Population from Existing Extractors

> **Goal:** Wire the existing Ast.Core/Ast.Csharp extraction pipeline into the
> memory store, populating structural + call-chain layers with canonical IDs and
> temporal metadata.
>
> **Hard dependency:** Phase 1 (memory store must exist).
> **Parallel-safe:** Stages 2.1 and 2.2 are independent after Phase 1.

### Stage 2.1: Structural Layer Population

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Extraction/MemoryGraphPopulator.cs` — takes `RepositoryProcessor` output (list of `HypergraphNode` and `Hyperedge`) and maps them to `MemoryNode`/`MemoryEdge` with canonical IDs (`{repo}:{language}:{fqn}:{hash_short}`) and `VersionRange.Since` set to current HEAD commit
- [ ] Implement canonical ID generation in `src/ast/Ast.Memory/Identity/CanonicalIdGenerator.cs` — stable qualified-name-based IDs with content-hash disambiguation
- [ ] Create `src/ast/Ast.Memory/Identity/IdReconciliationTable.cs` — maps old canonical IDs to new IDs after renames; backed by a dictionary persisted in Neo4j as `ID_MAPS_TO` edges
- [ ] Wire `MemoryGraphPopulator` to write structural nodes (Repository, Directory/Package, File/Module, Class, Function) and CONTAINS edges into `IMemoryStore`
- [ ] Add provenance metadata (extraction_version, source="Ast.Csharp", file_hash) to each node's metadata JSON property

#### Test Scenarios
- [ ] Scenario: Canonical ID stability — Given a C# class `PaymentService` in `src/Services/PaymentService.cs`, When extracted twice without changes, Then the canonical ID is identical both times
- [ ] Scenario: Structural population completeness — Given the SimpleApi fixture, When `MemoryGraphPopulator` runs, Then the memory store contains Repository→Directory→File→Class→Method containment chain with CONTAINS edges at every level
- [ ] Scenario: Provenance metadata — Given a populated memory node, When its metadata JSON is inspected, Then it contains `extraction_version`, `source`, and `file_hash` fields

### Stage 2.2: Call-Chain Layer Population

#### Implementation Steps
- [ ] Extend `MemoryGraphPopulator` to process `MethodInvocation` and `ConstructorInvocation` hyperedges from existing extractors and emit `CALLS`/`CALLED_BY` memory edges with `Layer=CallChain` and confidence based on resolution status
- [ ] Map existing `Implements`, `Extends` hyperedge types to `IMPLEMENTS`, `INHERITS` memory edges
- [ ] Map existing `UsingDirective` and import-related edges to `IMPORTS`/`DEPENDS_ON` memory edges
- [ ] Wire the existing `CodeIntelligence` analyzers (e.g., `InheritanceAnalyzer`, `DependencyInjectionAnalyzer`) so their `DetectedRelationship` outputs are also stored as memory edges with appropriate confidence scores

#### Test Scenarios
- [ ] Scenario: Call-chain edge creation — Given `ServiceA.Process()` calls `RepositoryB.Save()` in the SimpleApi fixture, When call-chain extraction runs, Then a CALLS edge exists from `ServiceA.Process` to `RepositoryB.Save` with confidence ≥ 0.9
- [ ] Scenario: Inheritance edge mapping — Given `PaymentController` extends `ControllerBase`, When extraction runs, Then an INHERITS edge exists between the two class nodes
- [ ] Scenario: Unresolved call handling — Given a method call to an external library not in the repo, When extraction runs, Then a CALLS edge is created with `resolved=false` and `confidence=0.5`

### Stage 2.3: Batch Population Pipeline

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Extraction/FullRepositoryIndexer.cs` — orchestrates full-repo indexing: enumerate files → parse via `RepositoryProcessor` → populate via `MemoryGraphPopulator` → write audit log entry
- [ ] Implement progress reporting via `IProgress<MemoryIndexProgress>` with file count, node count, edge count, and elapsed time
- [ ] Add configurable parallelism (default: `Environment.ProcessorCount`) for file parsing phase with `Parallel.ForEachAsync`
- [ ] Create CLI command `ast memory index <repo-path>` in `Ast.Cli` that invokes `FullRepositoryIndexer`

#### Test Scenarios
- [ ] Scenario: Full indexing pipeline — Given the SimpleApi fixture (6 C# files), When `FullRepositoryIndexer.IndexAsync` is called, Then the memory store contains nodes for all classes and methods, and the audit log records one "Create" entry per node
- [ ] Scenario: Progress reporting — Given a fixture with 6 files, When indexing runs with an `IProgress` callback, Then the callback is invoked at least 6 times with incrementing file counts
- [ ] Scenario: Idempotent re-indexing — Given a fully indexed repo, When `FullRepositoryIndexer.IndexAsync` is called again without changes, Then node count remains the same and no duplicate nodes are created (canonical IDs prevent duplication)

---

## Phase 3: Retrieval & Context Assembly

> **Goal:** Build the hybrid retrieval engine that agents call to get
> token-budgeted, structurally-aware context from the memory graph.
>
> **Hard dependency:** Phase 2 (graph must be populated).
> **Parallel-safe:** Stages 3.1 and 3.2 can run in parallel; Stage 3.3 depends
> on both.

### Stage 3.1: Memory Query Interface

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Retrieval/IMemoryQuery.cs` interface with methods: `QueryContextAsync(string taskDescription, MemoryQueryOptions options)`, `GetCallChainAsync(string functionId, CallChainDirection direction, int depth)`, `FindSimilarAsync(string queryOrNodeId, int topK)`, `GetModuleSummaryAsync(string modulePath)`
- [ ] Create `src/ast/Ast.Memory/Retrieval/MemoryQueryOptions.cs` with `MaxTokens` (default 32K), `Depth` (default 3), `IncludeCallChain` (default true), `IncludeImports` (default true), `IncludeExpertise` (default true), `Branch` (optional), `ScopePrefix` (optional qualified-name prefix to limit traversal)
- [ ] Create `src/ast/Ast.Memory/Retrieval/ContextResult.cs` with `Nodes` (list of ranked nodes), `Edges` (connecting edges), `Provenance` (why each node was selected), `TokenCount`, `TruncatedNodeCount`
- [ ] Implement `MemoryQueryService` composing existing `HybridQueryService` with memory-specific temporal and annotation filtering

#### Test Scenarios
- [ ] Scenario: Query options defaulting — Given default MemoryQueryOptions, When constructed, Then MaxTokens=32768 and Depth=3 and IncludeCallChain=true
- [ ] Scenario: Scope-limited query — Given a populated graph and ScopePrefix="src/Services", When QueryContextAsync is called, Then only nodes under that path prefix are returned

### Stage 3.2: Relevance Scoring and Ranking

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Retrieval/RelevanceScorer.cs` implementing multi-signal scoring: `score = w1*structural_distance + w2*semantic_similarity + w3*centrality + w4*recency + w5*expertise_signal`
- [ ] Implement centrality computation (fan-in count from CALLS edges) as a cached property on memory nodes, recomputed during indexing
- [ ] Implement recency signal from `VersionRange.Since` and `LastTouched` relative to HEAD commit
- [ ] Create `src/ast/Ast.Memory/Retrieval/ScoringWeights.cs` with configurable weights (defaults: structural=0.3, semantic=0.3, centrality=0.15, recency=0.15, expertise=0.1)

#### Test Scenarios
- [ ] Scenario: High-centrality boost — Given function A with 10 callers and function B with 1 caller at equal semantic similarity, When scored, Then A ranks higher than B
- [ ] Scenario: Recency boost — Given two nodes with equal centrality but node X modified today and node Y modified 90 days ago, When scored, Then X ranks higher
- [ ] Scenario: Weight configuration — Given custom weights {structural=0.5, semantic=0.5, centrality=0, recency=0, expertise=0}, When scoring, Then only structural distance and semantic similarity affect the score

### Stage 3.3: Context Assembly with Token Budgeting

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Retrieval/ContextAssembler.cs` — takes ranked nodes + edges, serializes into a structured markdown context block with sections: `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise`
- [ ] Implement token counting using tiktoken-compatible tokenizer (e.g., `Microsoft.ML.Tokenizers` NuGet) to enforce `MaxTokens` budget
- [ ] Implement hierarchy compression: when a parent and all children are selected, collapse to parent summary + child signature list to save tokens
- [ ] Implement provenance footnotes: each included node gets a brief `[via: call-chain from X]` or `[via: semantic similarity 0.87]` annotation

#### Test Scenarios
- [ ] Scenario: Token budget enforcement — Given a graph with 100 nodes producing 50K tokens of context, When assembled with MaxTokens=32768, Then output token count ≤ 32768 and the highest-scored nodes are included
- [ ] Scenario: Hierarchy compression — Given a class with 5 methods all selected, When assembled, Then output contains the class summary + 5 one-line method signatures rather than full details of each
- [ ] Scenario: Provenance inclusion — Given 3 nodes retrieved via different strategies, When assembled, Then each node's context block includes a provenance annotation explaining why it was selected

---

## Phase 4: Incremental Sync & Temporal Tracking

> **Goal:** Keep the memory graph current as code changes without full re-indexing.
>
> **Hard dependency:** Phase 2 (population pipeline must exist).
> **Parallel-safe with Phase 3:** Stages 4.1–4.3 can be built alongside Phase 3
> retrieval work. Stage 4.4 depends on Stage 4.3.

### Stage 4.1: Change Detection

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Sync/ChangeDetector.cs` — given two commit SHAs, shells out to `git diff --name-status` to compute added/modified/deleted/renamed files
- [ ] Create `src/ast/Ast.Memory/Sync/ChangeSet.cs` model with `Added`, `Modified`, `Deleted`, `Renamed` (old→new path) file lists and `BaseCommit`/`HeadCommit` SHAs
- [ ] Handle the "initial index" case where BaseCommit is null — treat all files as Added

#### Test Scenarios
- [ ] Scenario: Detect file changes — Given a git repo with 1 added and 1 modified file between commits A and B, When `ChangeDetector.DetectAsync(A, B)` runs, Then ChangeSet.Added contains the new file and ChangeSet.Modified contains the changed file
- [ ] Scenario: Rename detection — Given a file renamed from `OldService.cs` to `NewService.cs`, When detected, Then ChangeSet.Renamed contains the mapping and ChangeSet.Deleted/Added are empty for that file

### Stage 4.2: Dependency Invalidation

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Sync/DependencyInvalidator.cs` — given a ChangeSet, queries the memory graph for all nodes in changed files, then walks IMPORTS and CALLS edges to find downstream nodes that need re-verification
- [ ] Implement `MarkStaleAsync` — sets `Stale=true` on edges whose source or target is in a changed file
- [ ] Add configurable invalidation depth (default 2 hops) to limit cascading invalidation in large graphs

#### Test Scenarios
- [ ] Scenario: Direct dependency invalidation — Given file A imports file B, and file B is modified, When invalidation runs, Then IMPORTS edge from A→B is marked stale
- [ ] Scenario: Depth-limited invalidation — Given a chain A→B→C→D with invalidation depth=2, When file D changes, Then edges touching C and B are marked stale but edges touching A are not

### Stage 4.3: Incremental Re-extraction

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Sync/IncrementalSyncService.cs` — orchestrates: detect changes → invalidate → re-parse changed files → reconcile nodes (create new, update changed, tombstone deleted) → clear stale flags on re-confirmed edges → write audit entries
- [ ] Implement node reconciliation using canonical IDs: match existing nodes by `{repo}:{language}:{fqn}`, compare `content_hash` to decide update vs. skip
- [ ] Implement rename reconciliation: for renamed files, update `IdReconciliationTable` and re-map all edges pointing to old canonical IDs
- [ ] Create CLI command `ast memory sync [--base-commit <sha>]` in `Ast.Cli`

#### Test Scenarios
- [ ] Scenario: Incremental update — Given a fully indexed repo, When one file is modified (method signature changed), Then only that file's nodes are re-extracted, the changed method node gets a new content_hash, and the audit log records Update operations
- [ ] Scenario: File deletion handling — Given a file with 3 classes is deleted, When sync runs, Then all 3 class nodes and their child nodes are tombstoned (ValidUntil set) rather than hard-deleted
- [ ] Scenario: Rename tracking — Given `PaymentService.cs` renamed to `PaymentGateway.cs`, When sync runs, Then the IdReconciliationTable maps old→new IDs and all CALLS edges are updated to reference the new canonical ID

### Stage 4.4: Staleness Scoring

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Sync/StalenessScorer.cs` — computes a staleness score for each node combining: days since `LastTouched`, inverse of centrality (high fan-in = less stale), and `Stale` edge count ratio
- [ ] Integrate staleness score into `RelevanceScorer` as a penalty term: nodes with staleness > threshold get down-ranked in retrieval
- [ ] Add staleness warning injection into `ContextAssembler`: when retrieved annotations are older than the latest commit touching that file, inject `⚠️ stale annotation` marker

#### Test Scenarios
- [ ] Scenario: Staleness penalty — Given a node not touched in 180 days with low centrality, When staleness is computed, Then its score is above the configurable warning threshold (default 0.7)
- [ ] Scenario: High-centrality protection — Given a core utility function with 50 callers not touched in 180 days, When staleness is computed, Then its score is below the warning threshold because centrality counteracts recency decay

---

## Phase 5: Agent API & MCP Integration

> **Goal:** Expose memory as a callable API that MCP-compatible agents use to
> query context and write back learnings.
>
> **Hard dependency:** Phase 3 (retrieval) and Phase 4 (sync) for full
> functionality. Read API can ship after Phase 3 alone.

### Stage 5.1: Memory API Contract

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Api/IAgentMemoryApi.cs` interface with methods: `QueryAsync(AgentQuery)→ContextResult`, `AnnotateAsync(AnnotationRequest)→void`, `GetCallChainAsync(CallChainRequest)→CallChainResult`, `FindSimilarAsync(SimilarityRequest)→SimilarNode[]`, `RecordFeedbackAsync(FeedbackRequest)→void`
- [ ] Create request/response DTOs in `src/ast/Ast.Memory/Api/Models/` — `AgentQuery`, `AnnotationRequest` (with type: useful|not_useful|expertise_note), `CallChainRequest`, `FeedbackRequest`, `CallChainResult`, `SimilarNode`
- [ ] Create `src/ast/Ast.Memory/Api/AgentMemoryService.cs` implementing `IAgentMemoryApi` by composing `MemoryQueryService`, `IMemoryStore`, and `IExpertiseStore`

#### Test Scenarios
- [ ] Scenario: Query round-trip — Given a populated memory graph, When `QueryAsync` is called with "What calls PaymentService.Process?", Then the result contains the call-chain subgraph with TokenCount ≤ MaxTokens
- [ ] Scenario: Annotation write-back — Given a node ID, When `AnnotateAsync` is called with type="expertise_note" and content="fragile null handling", Then `GetAnnotationsAsync` returns the note and an audit entry is written

### Stage 5.2: MCP Tool Definition

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Mcp/MemoryMcpTool.cs` defining MCP tool schema for `memory_query`, `memory_annotate`, `memory_call_chain`, `memory_find_similar` operations following MCP tool protocol JSON schema
- [ ] Create `src/ast/Ast.Memory/Mcp/MemoryMcpHandler.cs` — MCP request handler that deserializes tool calls and delegates to `IAgentMemoryApi`
- [ ] Add MCP tool registration in `ServiceCollectionExtensions` so the memory tools appear in MCP tool discovery

#### Test Scenarios
- [ ] Scenario: MCP schema validity — Given the `memory_query` tool definition, When serialized to JSON, Then the schema validates against MCP tool protocol specification (required fields: name, description, inputSchema)
- [ ] Scenario: MCP request dispatch — Given an MCP `memory_query` request with `{"query": "auth flow", "maxTokens": 16000}`, When handled by `MemoryMcpHandler`, Then `AgentMemoryService.QueryAsync` is invoked with the deserialized parameters and the response is valid MCP tool result JSON

### Stage 5.3: Interaction Logging & Observability

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Logging/InteractionLogger.cs` — records every agent query (task text, retrieved node IDs, token count, latency) and feedback signal into the Interaction layer of the graph
- [ ] Implement structured OpenTelemetry traces: span per query with attributes for retrieval mode, node count, token count, latency_ms
- [ ] Create `src/ast/Ast.Memory/Logging/MemoryMetrics.cs` — counters for queries_total, annotations_total, cache_hit_ratio, and histograms for query_latency_ms and token_count

#### Test Scenarios
- [ ] Scenario: Interaction log persistence — Given an agent query that returns 5 nodes, When the query completes, Then InteractionLogger has written a log entry with query text, 5 node IDs, and latency > 0ms
- [ ] Scenario: Feedback correlation — Given an agent marks 2 nodes as "useful" and 1 as "not_useful", When feedback is recorded, Then the interaction log entry references the original query ID and contains per-node feedback signals

---

## Phase 6: Expertise & Cross-Session Learning

> **Goal:** Enable the memory to accumulate cross-cutting expertise so agents
> get smarter over time — the core "agent learns" requirement.
>
> **Hard dependency:** Phase 5 (needs interaction logs and annotation API).
> **Note:** This phase is post-MVP but essential for the story's learning goal.

### Stage 6.1: Usage Analytics

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Expertise/UsageAnalytics.cs` — aggregates interaction logs to compute per-node access counts, frequently co-retrieved node pairs, and useful/not-useful signal ratios
- [ ] Implement hot-node detection: nodes with access_count above the 90th percentile are flagged as hot-tier candidates
- [ ] Store computed analytics as derived annotations on nodes (key: `_analytics`, value: JSON with access_count, co_retrieval_partners, usefulness_ratio)

#### Test Scenarios
- [ ] Scenario: Access count aggregation — Given 10 queries where node X appears in 8 results, When analytics runs, Then node X's access_count annotation equals 8
- [ ] Scenario: Hot-node detection — Given 100 nodes where 5 have access_count > 90th percentile, When analytics runs, Then exactly those 5 are flagged as hot-tier
- [ ] Scenario: Usefulness ratio — Given node Y received 6 "useful" and 2 "not_useful" signals, When analytics runs, Then usefulness_ratio for Y is 0.75

### Stage 6.2: Pattern Extraction & Expertise Notes

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Expertise/PatternExtractor.cs` — identifies recurring code patterns by clustering frequently co-retrieved subgraphs using community detection (Louvain algorithm on co-retrieval adjacency)
- [ ] For each detected community/cluster, generate an `ExpertiseNote` with PatternType, a prose Summary (template-based, not LLM-dependent for MVP), and APPLIES_TO edges to member nodes
- [ ] Implement expertise deduplication: before creating a new ExpertiseNote, check for existing notes with overlapping APPLIES_TO sets; merge if overlap > 80%

#### Test Scenarios
- [ ] Scenario: Pattern detection — Given nodes {A, B, C} are co-retrieved in 5 out of 6 queries, When pattern extraction runs, Then an ExpertiseNote is created with APPLIES_TO edges to A, B, and C
- [ ] Scenario: Deduplication — Given an existing ExpertiseNote covering {A, B, C} and a new pattern covering {A, B, C, D}, When extraction runs, Then the existing note is updated (not duplicated) and D is added to APPLIES_TO

### Stage 6.3: Memory Consolidation & Decay

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Expertise/ConsolidationEngine.cs` — periodic job that: (a) merges overlapping ExpertiseNotes, (b) decays confidence on notes whose APPLIES_TO targets have been substantially rewritten (content_hash changed), (c) archives notes with confidence below threshold
- [ ] Implement Ebbinghaus-inspired decay function: `new_confidence = confidence * e^(-λ * days_since_last_access)` where λ is calibrated so half-life = 60 days by default, but modified by centrality (high-centrality targets decay slower)
- [ ] Create `src/ast/Ast.Memory/Expertise/ExpertiseEvolutionService.cs` tying analytics → extraction → consolidation into a single `EvolveAsync()` pipeline callable from CLI (`ast memory evolve`)

#### Test Scenarios
- [ ] Scenario: Confidence decay — Given an ExpertiseNote with confidence=0.9 not accessed for 60 days, When consolidation runs, Then confidence is approximately 0.45 (half-life)
- [ ] Scenario: Centrality-adjusted decay — Given two notes both 60 days old, one targeting a function with 50 callers and one targeting a function with 1 caller, When consolidation runs, Then the high-centrality note retains higher confidence
- [ ] Scenario: Archive threshold — Given a note with confidence decayed to 0.05 (below default threshold 0.1), When consolidation runs, Then the note is archived (ValidUntil set) and excluded from default retrieval

### Stage 6.4: Cross-Session Learning Flow

#### Implementation Steps
- [ ] Create `src/ast/Ast.Memory/Expertise/SessionLearningService.cs` — at session start, loads relevant ExpertiseNotes for the task scope and injects them into the context window as a `## Prior Learnings` section
- [ ] At session end, extracts new annotations written during the session and pipes them through the consolidation engine to merge with existing knowledge
- [ ] Implement conflict resolution for contradictory annotations: most recent from the most frequently validated session wins; contradictions are logged as audit entries for human review

#### Test Scenarios
- [ ] Scenario: Prior learning injection — Given 3 ExpertiseNotes applicable to module "auth", When a new session targets "auth/login.cs", Then the context window includes a "Prior Learnings" section with all 3 notes
- [ ] Scenario: Post-session write-back — Given an agent wrote 2 new annotations during a session, When the session ends and learning service runs, Then both annotations are persisted as ExpertiseNotes with the session's ID as source
- [ ] Scenario: Contradiction handling — Given note A says "function X is safe" and new note B says "function X has null-deref", When consolidation runs, Then note A is superseded by note B (SUPERSEDES edge created) and an audit entry records the contradiction

---

## Dependency Graph

```
Phase 1: Schema & Storage Foundation
  │
  └──→ Phase 2: Graph Population ──┬──→ Phase 3: Retrieval & Context Assembly
                                   │
                                   └──→ Phase 4: Incremental Sync (parallel with Phase 3)
                                          │
                                          └──┬──→ Phase 5: Agent API & MCP
                                             │
                                             └──→ Phase 6: Expertise & Learning (post-MVP)
```

**Critical path:** 1 → 2 → 3 → 5

**Parallel tracks:**
- Phase 4 runs alongside Phase 3 after Phase 2 completes.
- Stage 3.1 and 3.2 are parallel within Phase 3.
- Stage 2.1 and 2.2 are parallel within Phase 2.
- Phase 6 starts after Phase 5 and requires interaction data.

---

## MVP Boundary

| Capability | Phase | Status |
|------------|-------|--------|
| Memory data model (nodes, edges, annotations, audit) | 1 | ✅ MVP |
| Structural + call-chain graph population from existing extractors | 2 | ✅ MVP |
| Hybrid retrieval with token-budgeted context assembly | 3 | ✅ MVP |
| Incremental sync with dependency invalidation | 4 | ✅ MVP |
| Agent API + MCP tool surface | 5 | ✅ MVP |
| Cross-session expertise accumulation | 6 | ⚠️ Post-MVP |
| Multi-language support beyond C#/PowerShell | — | ❌ Deferred |
| LLM-generated summaries | — | ❌ Deferred |

---

## References

- Yang et al., "Graph-based Agent Memory: Taxonomy, Techniques, and Applications," arXiv:2602.05665, 2026.
- [Awesome-GraphMemory](https://github.com/DEEP-PolyU/Awesome-GraphMemory) — curated resource list.
- Zep/Graphiti — temporal knowledge graph for agent memory (arXiv:2501.13956).
- Mem0 — production-ready agent memory with graph storage (arXiv:2504.19413).
- MAGMA — multi-graph agentic memory architecture (arXiv:2601.03236).
- HiAgent — hierarchical working memory for long-horizon tasks (ACL'25).
- GraphRAG — graph-based RAG for summarization (arXiv:2404.16130).


