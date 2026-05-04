# Agent Memory Architecture

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 pts
>
> **Status:** Iteration 48
>
> This document is the **canonical authority** for component boundaries,
> data model, edge/layer naming, ID format, interface signatures, and
> concurrency semantics. When sibling documents
> ([tech-spec.md](tech-spec.md), [implementation-plan.md](implementation-plan.md),
> [e2e-scenarios.md](e2e-scenarios.md)) use different names, formats, or
> thresholds, **this document governs**. Sibling documents own their
> respective scopes:
>
> | Document | Owns | Alignment status |
> |---|---|---|
> | [`tech-spec.md`](tech-spec.md) | Problem statement, scope/non-goals, hard constraints (HC1–HC11), risks, success criteria, resolved operator decisions (D1–D6) | Last updated iteration 23. Contains 12 stale-wording locations enumerated in §8.1 (including 2 vector-backend mismatches added in iteration 47). Architecture governs on all naming/data-model conflicts. |
> | [`implementation-plan.md`](implementation-plan.md) | Phased work items (6 phases), deliverables, evaluation harness | Defers to this doc on conflicts. **Active divergences (3):** (1) Stage 1.2 lists 12 `IMemoryGraphStore` methods, omitting `UpsertEdgeAsync` (canonical: 13 methods, §5.1). (2) Stage 2.1 persists edges via "inherited `IGraphStore` edge-creation methods" instead of `UpsertEdgeAsync`. (3) Stage 5.3 decay formula places `centrality_boost` as output multiplier instead of exponent-denominator modifier (§3.5). See §8.2. |
> | [`e2e-scenarios.md`](e2e-scenarios.md) | Gherkin acceptance scenarios (13 features, 82 scenarios) | Fully aligned. Uses canonical vocabulary throughout. |

---

## 1. Purpose & Scope

This architecture specifies a **graph-based agent memory** that encodes
repository structure, call chains, semantic similarity, and accumulated
expertise into a persistent, queryable knowledge graph. Agents consume this
graph to construct richer context and accumulate institutional knowledge
across sessions — patterns, pitfalls, and architectural insights that make
each subsequent session smarter.

Design draws from the graph-based agent-memory taxonomy (Yang et al.,
arXiv:2602.05665) — **Extraction → Storage → Retrieval → Evolution** —
adapted to the code-intelligence domain where inputs are ASTs, call graphs,
and VCS history.

### 1.1 In / Out of Scope

| In scope | Out of scope |
|---|---|
| Static-analysis extraction (AST, call graph, imports) | Runtime / dynamic tracing |
| Multi-layer graph construction & persistent storage | Multi-repo federation |
| Token-budgeted context retrieval for agent prompts | IDE UI / dashboards |
| Expertise write-back, consolidation, decay | LLM inference hosting |
| Incremental sync on commit / file-save | Inter-agent message passing |
| Concurrent read access from multiple agent sessions | Multi-agent negotiation / consensus |

### 1.2 Multi-Agent Interaction Model

The memory graph is scoped **per-repository**, not per-agent-instance. A
single graph store (one SQLite database or Neo4j instance) serves all agent
sessions working on the same repository.

> **Reconciliation with tech-spec O6:** Tech-spec.md §3 O6 says "The graph is
> per-agent-instance for now" in the context of excluding "Multi-agent shared
> memory." O6 excludes *G-Memory-style coordinated sharing protocols* (agent
> swarms negotiating over a shared memory space). It does **not** require
> separate storage per agent. The architecture's model — a single per-repo
> graph with indirect knowledge sharing via read/write operations and no
> inter-agent messaging — satisfies O6's intent. Multiple agents reading and
> writing the same graph is simple concurrent I/O, not the "shared memory"
> protocol that O6 defers.

The interaction model is:

- **Shared-nothing reads.** Each retrieval call is stateless and independent.
  Multiple concurrent `RetrieveAsync` calls are safe (§3.7).
- **Serialized writes.** All mutations (`ApplyDiffAsync`,
  `RecordExpertiseAsync`, `DecayStaleExpertiseAsync`) are serialized via
  in-process mutex (SQLite) or ACID transactions (Neo4j). No
  optimistic-concurrency errors — writes queue and execute in order.
- **Indirect knowledge sharing.** Agents do not communicate with each other.
  Knowledge is shared indirectly: Agent A writes an expertise node; Agent B
  discovers it on a subsequent `RetrieveAsync` call. The `SourceAgent` field
  on `ExpertiseEntry` provides provenance.
- **No distributed lock for reads.** Only `ConsolidateAsync` (an admin
  operation) acquires a write-level lock to prevent concurrent merges.

### 1.3 Existing Infrastructure (`src/ast/`)

The system builds on the Code Intelligence stack in `src/ast/` (26 `.csproj`
projects total — 12 production, 14 test — verified via directory listing).
**Two existing projects receive additive-only changes:**

1. **`Ast.Core`** (`src/ast/Ast.Core/HypergraphModels.cs`) — one new enum
   value `HyperedgeType.MemoryRelation` appended to the existing 44-value
   `HyperedgeType` enum (lines 12–69 of `HypergraphModels.cs`), bringing the
   total to 45.
2. **`Ast.Cli`** (`src/ast/Ast.Cli/Program.cs`) — additive
   `config.AddBranch("memory", ...)` block appended after the existing 7
   `config.AddCommand<T>()` registrations (lines 17–41). A project reference
   to `Ast.Memory` is added to `Ast.Cli.csproj`.

All new code ships as **`Ast.Memory`** (+ `Ast.Memory.UnitTests` +
`Ast.Memory.IntegrationTests`). Existing projects are referenced as project
dependencies, never mutated beyond the two additive changes above.

| Existing project | Key types (verified source paths) | How memory layer uses it |
|---|---|---|
| **Ast.Core** | `HypergraphNode`, `Hyperedge`, `HyperedgeType` (44 enum values, lines 12–69 of `HypergraphModels.cs`) — `HypergraphModels.cs`; `RepositoryProcessor` — `RepositoryProcessor.cs` | Reference types via `Metadata` dict. One additive enum value `MemoryRelation` (bringing total to 45). |
| **AgentMemory** | `IAgentMemoryStore`, `Episode`, `ExpertiseRecord`, `MemoryEdge`, `MemoryTier`, `MemoryEdgeType` (11 values), `ExpertiseCategory` (11 values) — `AgentMemory.csproj` (references `Ast.Core`) | **Existing prototype.** Contains episode-based memory model with file-based and in-memory stores. `Ast.Memory` supersedes this project by extending the graph-based model with four-layer structure, temporal versioning, and hybrid retrieval. `AgentMemory` remains for backward compatibility until migrated; `Ast.Memory` does not depend on it. |
| **Ast.GraphDb** | `IGraphStore : IAsyncDisposable, IDisposable` — `IGraphStore.cs`; `Neo4jGraphStore` — `Neo4jGraphStore.cs`; `NodeFilter`, `GraphPath`, `NodeWithDepth` — `GraphModels.cs` | `Ast.Memory` defines `IMemoryGraphStore` extending `IGraphStore` in its own project. Zero files in `Ast.GraphDb/` modified. |
| **Ast.Embedding** | `IEmbeddingGenerator : IDisposable` — `IEmbeddingGenerator.cs`; `CodeEmbeddingService` — `CodeEmbeddingService.cs`; `EmbeddingGeneratorOptions.ModelName` defaults to `"nomic-embed-text"` — `EmbeddingModels.cs` | Reuse directly. `Ast.Memory` calls `GenerateBatchEmbeddingsAsync()`. |
| **Ast.Query** | `ICodeQuery` — `ICodeQuery.cs`; `HybridQueryService` — `HybridQueryService.cs`; `ScoredNode` — `QueryModels.cs` | Reference only. `Ast.Memory` defines `IMemoryRetrievalEngine` that internally composes `ICodeQuery`. |
| **Ast.VectorDb** | `IVectorStore : IDisposable` — `IVectorStore.cs` (10 members — see §3.2.1 for full signatures); `QdrantVectorStore` — `QdrantVectorStore.cs`; `VectorPoint` (`Id: string`, `Vector: float[]`, `Payload: Dictionary<string,object>`), `VectorSearchResult` (`Id: string`, `Score: float`, `Vector?: float[]`, `Payload: Dictionary<string,object>`), `VectorFilter` (`Must/Should/MustNot: List<FilterCondition>`), `VectorStoreOptions` — all in `VectorModels.cs` | Reference only. Reuse `QdrantVectorStore` for team mode; `Ast.Memory` adds `SqliteVssVectorStore` (in `Storage/SqliteVssVectorStore.cs`) implementing `IVectorStore` for local mode via sqlite-vss extension. Zero files in `Ast.VectorDb/` modified. |
| **Ast.Lsp** | LSP integration — `src/ast/Ast.Lsp/` | Optional enrichment — pipeline never blocks on LSP. No modifications. |
| **CodeIntelligence** | 27 analyzers in `Analyzers/`, 4 detectors in `Detectors/`, `RelationshipGraphBuilder` in `Graph/` | Consume as call-chain / structural edge source. No modifications. |
| **Ast.Cli** | CLI host — `Program.cs` uses Spectre.Console.Cli with manual `AddCommand<T>()` registration (7 commands: parse, enrich, embed, index, query, pipeline, stats — lines 17–41). | Additive: `config.AddBranch("memory", ...)` + project ref to `Ast.Memory`. |

### 1.4 Research Foundations

| ID | Source | Adopted technique | Where applied |
|---|---|---|---|
| R-1 | MAGMA (arXiv:2601.03236) | Orthogonal graph layers prevent signal entanglement | Four independent layers (§2.1) |
| R-2 | HiAgent (ACL '25) | Hierarchical working memory with abstraction-level control | Budget Controller collapses children into parent summaries (§3.4) |
| R-3 | Zep/Graphiti (arXiv:2501.13956) | Temporal KG with timestamped edges | Dual-axis temporal model: commit-index + wall-clock (§4.3) |
| R-4 | Mem0 (arXiv:2504.19413) | Agent write-back for compounding value | Evolution Engine expertise write-back (§3.5) |
| R-5 | Reflexion (NeurIPS '23) | Verbal RL — reflect on past failures | Cross-session learning via Expertise layer (§6.5) |
| R-6 | GraphRAG (arXiv:2404.16130) | Community detection + LLM summarization | Topic nodes in Semantic layer (§3.5) |
| R-7 | MemGPT (arXiv:2310.08560) | Explicit memory read/write operations | Discrete retrieval modes in Query Planner (§3.3) |

---

## 2. Canonical Vocabulary

All planning documents in this story use these definitions.

### 2.1 Graph Layers

| Layer | Tag value (`MemoryLayer`) | Purpose | Edge types |
|---|---|---|---|
| **Structural** | `structural` | Containment: Repo → [Workspace] → Package → Module → Class → Function (Workspace is optional — present only in monorepo layouts) | `CONTAINS`, `DEPENDS_ON` |
| **Call-Chain** | `call_chain` | Invocations and type relationships between symbols | `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` |
| **Semantic** | `semantic` | Embedding-similarity clusters and topic summaries | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` |
| **Expertise** | `expertise` | Agent-authored knowledge nodes | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` |

Temporal versioning is **orthogonal** — it applies to nodes/edges on every
layer via `VersionRange` (§4.3). It is not a separate layer.

#### Node-Layer Assignment Rules

Every `HypergraphNode` in the memory graph carries exactly one
`Metadata["MemoryLayer"]` value — its **home layer**. The home layer
identifies which pipeline stage created the node and is responsible for its
lifecycle. However, a node may participate as an endpoint of edges on
**other** layers. The home-layer assignment rules:

| Node type(s) | Home layer | Created by | Cross-layer edge participation |
|---|---|---|---|
| `Repo`, `Workspace`, `Package`, `Module`, `Class`, `Interface`, `Function`, `Method`, `Property` | `structural` | `StructureBuilder` | Also appear as endpoints on `call_chain` edges (`CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES`) and `semantic` edges (`SIMILAR_TO`, `BELONGS_TO_CLUSTER`). |
| `External` | `call_chain` | `StructureBuilder` (when processing imports to out-of-repo dependencies) | Also appear as endpoints on `structural` edges (`DEPENDS_ON`). |
| `Topic` | `semantic` | `EvolutionEngine.DetectCommunitiesAsync()` | Only `BELONGS_TO_CLUSTER` edges (Semantic layer). |

`ExpertiseEntry` nodes are **not** `HypergraphNode` instances (§4.2) and do
not carry a `MemoryLayer` metadata key. Their edges (`APPLIES_TO`,
`SUPERSEDES`, `DERIVED_FROM`) are tagged `MemoryLayer = "expertise"`.

**Edges** carry their own `Metadata["MemoryLayer"]` which matches the layer
from the edge taxonomy (§2.2). A `CALLS` edge between two `structural`-homed
nodes still has `MemoryLayer = "call_chain"` on the edge, because the edge
belongs to the Call-Chain layer.

### 2.2 Edge Taxonomy

**Code-derived edges** — produced by the Extraction Pipeline. Each maps to
existing `HyperedgeType` enum values in `Ast.Core`:

| Canonical edge | `HyperedgeType` values | Layer | Roles |
|---|---|---|---|
| `CONTAINS` | `ClassDefinition`, `InterfaceDefinition`, `StructDefinition`, `RecordDefinition`, `MethodDefinition`, `ConstructorDefinition`, `PropertyDefinition`, `FunctionDefinition` | Structural | `parent`, `child` |
| `DEPENDS_ON` | `UsingDirective`, `ModuleImport`, `DotSource` | Structural | `importer`, `imported` |
| `CALLS` | `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`, `ModuleFunctionCall`, `CrossLanguageMethodCall` | Call-Chain | `caller`, `callee` |
| `EXTENDS` | `Extends` | Call-Chain | `subclass`, `superclass` |
| `IMPLEMENTS` | `Implements` | Call-Chain | `implementor`, `interface` |
| `REFERENCES` | `DataFlow`, `Assignment`, `CrossLanguageTypeReference` | Call-Chain | `source`, `target` |

**Memory-only edges** — produced by the Semantic Indexer or Evolution Engine.
All use `HyperedgeType.MemoryRelation` (a single new enum value added to
`Ast.Core`). The true semantic type is in `Metadata["SemanticRelation"]`:

| Canonical edge | Layer | Created by | Roles |
|---|---|---|---|
| `SIMILAR_TO` | Semantic | Semantic Indexer | `node_a`, `node_b` |
| `BELONGS_TO_CLUSTER` | Semantic | Evolution Engine (community detection) | `member`, `cluster` |
| `APPLIES_TO` | Expertise | Evolution Engine (write-back) | `expertise`, `target` |
| `SUPERSEDES` | Expertise | Evolution Engine (consolidation) | `newer`, `older` |
| `DERIVED_FROM` | Expertise | Evolution Engine (consolidation) | `derived`, `source` |

**Uniform filtering:** Every edge carries `Metadata["SemanticRelation"]` with
its canonical name. This is the **sole** edge-filtering mechanism used by the
Retrieval Engine. The `HyperedgeType` enum is preserved for backward
compatibility but is never used as a retrieval filter.

### 2.3 Retired Names

| Retired name | Canonical replacement | Notes |
|---|---|---|
| `IMPORTS` | `DEPENDS_ON` | |
| `CALLED_BY` | Reverse traversal of `CALLS` | Not a stored edge |
| `INHERITS` | `EXTENDS` | |
| `INSTANTIATES` | `CALLS` (with `ConstructorInvocation`) | |
| `ExpertiseNote` | `ExpertiseEntry` | Class renamed |
| `IMemoryStore` | `IMemoryGraphStore` | Interface renamed |
| `IExpertiseStore` | Part of `IEvolutionEngine` | Merged |
| `PatternType` | `ExpertiseKind` | Enum renamed |
| `SemanticCluster` | `Topic` (NodeType `"Topic"`) | Community-detected clusters use `Topic` nodes (§4.2); tech-spec's `SemanticCluster` is subsumed (A17) |
| `IAgentMemoryApi` | `IMemoryRetrievalEngine` + `IEvolutionEngine` | Split into purpose-specific interfaces |

### 2.4 Constants & Thresholds (Single Source of Truth)

All configurable parameters referenced throughout this document and sibling
documents. Implementations read these from `MemoryOptions` configuration.

| Constant | Default | Used by | Description |
|---|---|---|---|
| `SimilarityThreshold` | 0.85 | Semantic Indexer, Evolution Engine | Cosine-similarity floor for `SIMILAR_TO` and `DERIVED_FROM` edges |
| `StaleDecayFactor` | 0.5 | Evolution Engine | Confidence multiplier on major code change |
| `MinorDecayFactor` | 0.9 | Evolution Engine | Confidence multiplier on whitespace/comment-only change |
| `ArchiveConfidenceFloor` | 0.1 | Evolution Engine | Expertise nodes below this are soft-deleted |
| `DecayHalfLifeDays` | 1095 | Evolution Engine | Time-based decay half-life (3 years) |
| `TokenBudgetDefault` | 8,000 | Budget Controller | Per-query default token budget |
| `TokenBudgetCeiling` | 32,000 | Budget Controller | Maximum allowed token budget |
| `IncrementalSyncDebounceMs` | 1,000 | Incremental Sync | Debounce for file-system watcher |
| `CentralityBoostFactor` | 0.5 | Evolution Engine (Decay) | Multiplied by normalized PageRank (range 0–1) and added to 1.0 to form `centrality_boost` (range 1.0–1.5). Centrality boost extends the decay half-life: `effective_half_life = DecayHalfLifeDays × centrality_boost`. The time-based decay factor is then `2^(−age_days / effective_half_life)`, where `age_days = (DateTime.UtcNow − ExpertiseEntry.CreatedAt).TotalDays`. This means high-centrality nodes decay slower (longer half-life) and the time factor is always ≤ 1.0, so confidence never increases through decay. See §3.5 for full computation model and worked example. |

**Scoring weights** (used by the Ranker, sum = 1.00):

| Signal | Default weight | Description |
|---|---|---|
| `structural` | 0.30 | Inverse BFS distance from seed (Structural layer) — bounded [0,1] |
| `semantic` | 0.25 | Cosine similarity of query embedding to node embedding |
| `centrality` | 0.20 | Normalized fan-in from `CALLS` edges (PageRank-light) |
| `recency` | 0.15 | `1.0 − (HEAD_idx − node_sinceIdx) / max_distance`, clamped [0,1] |
| `expertise` | 0.10 | Max `Confidence` of any `APPLIES_TO` edge targeting the node; 0.0 when none |

---

## 3. Components

All new code ships as **`Ast.Memory`** (`src/ast/Ast.Memory/`) with
companion test projects `Ast.Memory.UnitTests` and
`Ast.Memory.IntegrationTests`. The project layout:

```
src/ast/
  Ast.Memory/
    Ast.Memory.csproj
    Interfaces/          ← IMemoryGraphStore, IMemoryRetrievalEngine,
                            IEvolutionEngine, IExtractionPipeline, IIncrementalSync
    Models/              ← MemoryNode, MemoryEdge, ExpertiseEntry, enums,
                            MemoryMetadataKeys, VersionRange
    Extraction/          ← StructureBuilder, CallChainAnalyzer,
                            SemanticIndexer, FactNormalizer
    Retrieval/           ← MemoryRetrievalEngine, QueryPlanner, Ranker,
                            ScoringWeights
    Context/             ← ContextAssembler, BudgetController
    Evolution/           ← EvolutionEngine, ConsolidationService, DecayService
    Sync/                ← IncrementalSync, GitDiffDriver
    Storage/             ← SqliteGraphStore, Neo4jMemoryGraphStore,
                            SqliteVssVectorStore
    Schema/              ← AnnotationKeyRegistry
    Mcp/                 ← MemoryMcpToolRegistration, MemoryMcpHandler
    Cli/                 ← MemoryCommandModule
    ServiceCollectionExtensions.cs
  Ast.Memory.UnitTests/
  Ast.Memory.IntegrationTests/
```

### 3.1 Extraction Pipeline

**Responsibility:** Transform source code into the four-layer memory graph.

| Sub-component | Class | Description |
|---|---|---|
| **Structure Builder** | `StructureBuilder` | Walks existing `HypergraphData` (produced by `Ast.Csharp` / `Ast.PowerShell` parsers) and emits Structural-layer nodes (Repo, Workspace (monorepo only), Package, Module, Class, Function) with `CONTAINS` and `DEPENDS_ON` edges. |
| **Call-Chain Analyzer** | `CallChainAnalyzer` | Reads `Hyperedge` entries of type `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`, etc. and emits Call-Chain-layer edges (`CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES`). |
| **Semantic Indexer** | `SemanticIndexer` | Batches nodes through `IEmbeddingGenerator.GenerateBatchEmbeddingsAsync()` (from existing `Ast.Embedding`), stores resulting vectors in two places — (1) `Metadata["Embedding"]` on each node via `IMemoryGraphStore.UpsertNodeAsync()` and (2) the vector index via `IVectorStore.UpsertAsync(collectionName, IEnumerable<VectorPoint> points, CancellationToken)` where each node is wrapped as a `VectorPoint { Id = nodeId, Vector = embedding, Payload = { MemoryLayer, NodeType } }` for ANN retrieval — then computes pairwise cosine similarity and creates `SIMILAR_TO` edges where cosine ≥ `SimilarityThreshold` (0.85). |
| **Fact Normalizer** | `FactNormalizer` | Deduplicates nodes across languages (e.g., a C# class and its PowerShell caller) and assigns canonical IDs. |

**Dependency chain:** `StructureBuilder` → `CallChainAnalyzer` → `SemanticIndexer` (sequential — each stage requires the previous stage's nodes to exist).

The pipeline exposes two entry points:

```csharp
interface IExtractionPipeline
{
    Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default);

    /// Re-extract only the specified files (used by IncrementalSync).
    Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default);
}
```

`ExtractAsync` performs full repository extraction (first-time indexing).
`ExtractFilesAsync` re-extracts only the listed files, returning their
nodes/edges for diffing by `IncrementalSync`.

### 3.2 Storage Layer

**Responsibility:** Persist and query the four-layer graph and associated
vector embeddings.

#### Graph Store Implementations

| Implementation | Backing store | Use case |
|---|---|---|
| `SqliteGraphStore` | SQLite + FTS5 | Local / single-developer mode. Zero infrastructure. |
| `Neo4jMemoryGraphStore` | Neo4j (via existing `Ast.GraphDb`) | Team / CI mode. Shared graph across developers. |

Both implement `IMemoryGraphStore` (§5.1). Selection is via DI configuration
in `ServiceCollectionExtensions.cs`.

#### Vector Store Implementations

Vector storage uses the existing `IVectorStore` interface from `Ast.VectorDb`.
`Ast.Memory` does **not** define a new vector interface — it consumes
`IVectorStore` as an injected dependency. The DI container wires one of:

| Implementation | Project | Backing store | Use case |
|---|---|---|---|
| `SqliteVssVectorStore` | `Ast.Memory` (`Storage/SqliteVssVectorStore.cs`) | sqlite-vss extension (HNSW index on the same SQLite database used by `SqliteGraphStore`) | Local mode — zero infrastructure |
| `QdrantVectorStore` | `Ast.VectorDb` (existing, unmodified) | Qdrant vector database | Team mode — shared vector index |

`SqliteVssVectorStore` implements all 10 members of `IVectorStore` (§3.2.1)
by translating collection operations into sqlite-vss virtual table commands.
Embeddings are stored in a `vss_embeddings` virtual table co-located in the
same SQLite database file as the graph tables (§4.4). This keeps local mode
as a single-file deployment (the `.db` file contains both graph and vector
data).

Both `SemanticIndexer` (§3.1) and the Retrieval Engine's `SemanticSearch`
mode (§3.3) access vectors exclusively through `IVectorStore` — they are
backend-agnostic.

#### 3.2.1 `IVectorStore` Interface — Full Signatures (from `Ast.VectorDb`)

The `IVectorStore` interface defines 10 members. All methods are consumed
unchanged by `Ast.Memory`; the key interaction types live in
`Ast.VectorDb/VectorModels.cs`.

```csharp
namespace Ast.VectorDb;

public interface IVectorStore : IDisposable
{
    string Name { get; }

    Task CreateCollectionAsync(
        string collectionName, int vectorSize,
        CancellationToken cancellationToken = default);

    Task<bool> CollectionExistsAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    Task DeleteCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        string collectionName,
        IEnumerable<VectorPoint> points,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        int limit = 10,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default);

    Task<VectorPoint?> GetAsync(
        string collectionName, string id,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string collectionName, IEnumerable<string> ids,
        CancellationToken cancellationToken = default);

    Task<long> CountAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(
        CancellationToken cancellationToken = default);
}
```

**Key types** (all from `Ast.VectorDb/VectorModels.cs`):

| Type | Fields | Usage in memory layer |
|---|---|---|
| `VectorPoint` | `Id: string` (required), `Vector: float[]` (required), `Payload: Dictionary<string,object>` | Created by `SemanticIndexer`: `new VectorPoint { Id = nodeId, Vector = embedding, Payload = { ["MemoryLayer"] = layer, ["NodeType"] = nodeType } }` |
| `VectorSearchResult` | `Id: string`, `Score: float`, `Vector: float[]?`, `Payload: Dictionary<string,object>` | Returned by `SearchAsync`; Retrieval Engine uses `Id` to resolve graph nodes and `Score` as the `semantic` signal |
| `VectorFilter` | `Must: List<FilterCondition>`, `Should: List<FilterCondition>`, `MustNot: List<FilterCondition>` | Used by Retrieval Engine to filter by `MemoryLayer` payload field during ANN search |
| `FilterCondition` | `Field: string`, `Operator: FilterOperator`, `Value: object` | `FilterOperator` enum: `Equals`, `NotEquals`, `GreaterThan`, `GreaterThanOrEqual`, `LessThan`, `LessThanOrEqual`, `In`, `NotIn`, `Contains` |

**`SqliteVssVectorStore` mapping:** The `collectionName` parameter maps to
per-collection virtual tables (e.g., `vss_{collectionName}`). Create
translates to `CREATE VIRTUAL TABLE vss_{name} USING vss0(...)`. Search
translates to `SELECT rowid, distance FROM vss_{name} WHERE ...`. The
`vss_id_map` table (§4.4) bridges sqlite-vss integer rowids to string node
IDs. The `Payload` dictionary is serialized to a JSON column in a companion
metadata table alongside each virtual-table row.

#### Auxiliary Tables

The storage layer also maintains:

- **`commits` table** — maps commit SHAs to monotonic `commit_index` integers
  and wall-clock timestamps. Used by the temporal model (§4.3).
- **`audit_log` table** — records every mutation with agent-session-id,
  timestamp, operation type, and detail JSON (HC7).

### 3.3 Retrieval Engine

**Responsibility:** Given a query (task description, file path, symbol name),
return a ranked set of graph nodes within a token budget.

The engine has three internal stages:

1. **Query Planner** (`QueryPlanner`) — selects one or more `RetrievalMode`
   values based on the query shape:
   - `StructuralDrill` — BFS from a seed node along `CONTAINS`/`DEPENDS_ON`.
   - `CallTrace` — forward/backward traversal along `CALLS`/`EXTENDS`/`IMPLEMENTS`.
   - `SemanticSearch` — embed query text via a single local call to `IEmbeddingGenerator.GenerateEmbeddingAsync()` (HC5), then ANN lookup via `IVectorStore.SearchAsync(collectionName, queryVector, limit, filter?, ct)` against pre-computed node embeddings (stored at extraction time by `SemanticIndexer`). Returns `IReadOnlyList<VectorSearchResult>` — the Retrieval Engine uses each result's `Id` to resolve the graph node and `Score` as the `semantic` signal (weight 0.25). See §3.2.1 for full `IVectorStore` signatures and types.
   - `ExpertiseRecall` — filter nodes by `APPLIES_TO` edges from Expertise layer.
   - `Hybrid` — combine all four modes (default for free-text queries).

2. **Ranker** (`Ranker`) — scores candidate nodes using the 5-signal weighted
   model (§2.4). Produces a `List<ScoredMemoryNode>` sorted by descending
   composite score. If `IEmbeddingGenerator` is unavailable at query time,
   semantic scores default to 0.0 and the remaining four signals are
   re-normalized to sum to 1.0.

3. **Budget Controller** (`BudgetController`) — serializes ranked nodes into a
   token-counted context window (default: 8,000 tokens; ceiling: 32,000).
   When budget is tight, collapses child nodes into parent summaries
   (HiAgent pattern). Deduplicates same code span reached via multiple paths.

Edge filtering throughout retrieval uses `Metadata["SemanticRelation"]`
exclusively (§2.2). The `HyperedgeType` enum is preserved for backward
compatibility but is never used as a retrieval filter.

### 3.4 Context Assembler

**Responsibility:** Transform ranked nodes into deterministic Markdown output
for the agent's prompt.

Output sections (fixed order):

```markdown
## Structure
<!-- Containment hierarchy of selected nodes -->

## Call Graph
<!-- Caller → callee chains relevant to the query -->

## Related Code
<!-- Semantically similar code snippets -->

## Known Expertise
<!-- Expertise nodes (patterns, pitfalls) that apply to selected code -->
```

The Context Assembler receives a `List<ScoredMemoryNode>` from the Retrieval
Engine and the token budget from the Budget Controller. It never queries the
graph directly — it formats what the Retrieval Engine provides.

### 3.5 Evolution Engine

**Responsibility:** Write-back, consolidation, decay, and community
summarization — the mechanisms by which the graph improves over sessions.

| Sub-component | Class | Description |
|---|---|---|
| **Write-back** | `EvolutionEngine.RecordExpertiseAsync()` | Creates `ExpertiseEntry` nodes (§4.2) linked to code nodes via `APPLIES_TO` edges. Each entry has a `Title` (headline), `Body` (detailed description), `Confidence` value (0.0–1.0), `ExpertiseKind` (`Pattern`, `Pitfall`, `Preference`, `Insight`), `Scope` (`"exact"` or `"subtree"`), optional `Evidence` citations, and `SourceAgent` provenance. |
| **Consolidation** | `ConsolidationService` | LLM-powered merge of semantically similar expertise entries (cosine ≥ `SimilarityThreshold`). Creates `SUPERSEDES` edge from merged entry to originals. Acquires a write-level lock to prevent concurrent merges. |
| **Decay** | `DecayService` | Two independent decay mechanisms. Their effects compose but are computed separately — event-driven decay modifies the stored `Confidence` immediately, and time-based decay is applied as an idempotent envelope on top. **(a) Event-driven decay** — triggered by `IncrementalSync` when a target code node's `ContentHash` changes. `StaleDecayFactor` (0.5) is applied on substantive code changes; `MinorDecayFactor` (0.9) on whitespace/comment-only changes. Applied immediately at sync time: `Confidence ← Confidence × factor`. Each `ContentHash` change triggers exactly one event-driven decay. The result is persisted to `ExpertiseEntry.Confidence` immediately. **(b) Time-based decay** — applied periodically (or on demand via `DecayStaleExpertiseAsync`). This is an **idempotent** computation: re-running it with the same inputs produces the same output (it overwrites, not multiplies). Definition of `age_days`: `age_days = (DateTime.UtcNow − ExpertiseEntry.CreatedAt).TotalDays` — always measured from the entry's creation date, not from the last decay run. Formula: `time_factor = 2^(−age_days / effective_half_life)`, where `effective_half_life = DecayHalfLifeDays × centrality_boost` and `centrality_boost = 1.0 + CentralityBoostFactor × normalized_pagerank` (range 1.0–1.5). The time factor is bounded [0.0, 1.0]: at `age_days = 0`, `time_factor = 1.0` (no reduction); at `age_days = effective_half_life`, `time_factor = 0.5`. **Combined formula:** `final_confidence = event_adjusted_confidence × time_factor`, where `event_adjusted_confidence` is the stored `Confidence` value (which already incorporates all prior event-driven decays). Because `time_factor ≤ 1.0`, the result never exceeds `event_adjusted_confidence`. Because event-driven decays only reduce the stored value, and time-based decay only applies a ≤ 1.0 multiplier on top, **confidence never increases** through decay. A node with `centrality_boost = 1.5` has an effective half-life of 1095 × 1.5 = 1642.5 days (high-centrality code retains expertise longer). **Worked example:** An expertise entry created 1095 days ago with `Confidence = 0.5` (reduced from 1.0 by one prior event-driven `StaleDecayFactor` application), targeting a node with `normalized_pagerank = 0.6`: `centrality_boost = 1.0 + 0.5 × 0.6 = 1.3`; `effective_half_life = 1095 × 1.3 = 1423.5 days`; `time_factor = 2^(−1095 / 1423.5) ≈ 0.58`; `final = 0.5 × 0.58 = 0.29`. Entries whose `final_confidence` drops below `ArchiveConfidenceFloor` (0.1) are soft-deleted via `IMemoryGraphStore.ArchiveExpertiseAsync` (sets `archived = 1` in storage, excluded from `GetExpertiseForNodeAsync` results). Archived entries can be restored via `IEvolutionEngine.RestoreArchivedExpertiseAsync` or inspected via `IMemoryGraphStore.GetArchivedExpertiseAsync`. |
| **Community Detection** | `EvolutionEngine.DetectCommunitiesAsync()` | Louvain algorithm over `CALLS`/`SIMILAR_TO` edges → creates `BELONGS_TO_CLUSTER` edges and `Topic` summary nodes in the Semantic layer (GraphRAG pattern). |

### 3.6 Incremental Sync

**Responsibility:** Keep the graph current without full re-extraction.

| Trigger | Mechanism |
|---|---|
| `git pull` / commit | `GitDiffDriver` computes changed file set via `git diff --name-status` |
| File save (IDE) | File-system watcher with `IncrementalSyncDebounceMs` (1,000 ms) debounce |

On trigger:

1. `IncrementalSync` registers the new commit SHA and `commit_index` in the
   `commits` table.
2. `GitDiffDriver` identifies added/modified/deleted/renamed files via
   `git diff --name-status` (status codes: `A` = added, `M` = modified,
   `D` = deleted, `R` = renamed).
3. For each batch of modified files (`M`): re-extract via
   `IExtractionPipeline.ExtractFilesAsync(repoRoot, changedPaths, ...)`,
   diff old vs. new nodes, upsert changes, set `UntilIndex` on removed
   nodes/edges.
4. For renamed files (`R`): retire all nodes/edges at the old path (via
   `RetireNodeAsync`), re-extract at the new path. This produces new node
   IDs (since IDs embed the file path) with `SinceIndex` = current commit.
5. For added files (`A`): extract via `ExtractFilesAsync` and upsert all
   new nodes/edges with `SinceIndex` = current commit.
6. For deleted files (`D`): set `UntilIndex` on all nodes/edges rooted in
   that file (via `RetireNodeAsync`, which cascades to edges).
7. Retire orphaned edges: for edges whose source or target node was retired
   in steps 3–6 but the edge was not caught by `RetireNodeAsync`'s cascade
   (e.g., a `CALLS` edge where only the callee was in a deleted file),
   call `RetireEdgeAsync` individually.
8. Propagate invalidation: mark downstream `CALLS` edges for re-validation
   via `UpdateEdgeMetadataAsync(edgeId, { "Stale": true })` — this applies
   to `CALLS` edges whose caller or callee signature changed.
9. Re-run `SemanticIndexer` on changed nodes only: re-compute embeddings
   via `IEmbeddingGenerator`, update the vector index via
   `IVectorStore.UpsertAsync(collectionName, [VectorPoint{...}])` (§3.2.1),
   then recalculate `SIMILAR_TO` edges. Old
   `SIMILAR_TO` edges that referenced retired or re-embedded nodes are
   retired via `RetireEdgeAsync`; new `SIMILAR_TO` edges are created where
   cosine ≥ `SimilarityThreshold`.
10. Trigger event-driven expertise decay for expertise entries targeting
    changed code nodes (`StaleDecayFactor` or `MinorDecayFactor` per §3.5).
11. Log all mutations to the audit log via `LogAuditAsync`.

```csharp
interface IIncrementalSync
{
    Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default);
}
```

### 3.7 Concurrency Model

- **Shared-nothing reads.** Each `RetrieveAsync` call is stateless and
  independent. Multiple concurrent calls are safe.
- **Serialized writes.** All mutations (`ApplyDiffAsync`,
  `RecordExpertiseAsync`, `DecayStaleExpertiseAsync`) are serialized via
  in-process `SemaphoreSlim(1, 1)` (SQLite) or ACID transactions (Neo4j).
- **Consolidation lock.** `ConsolidateAsync` acquires a write-level lock to
  prevent concurrent merges.
- **Indirect knowledge sharing.** Agents share knowledge indirectly: Agent A
  writes; Agent B discovers on subsequent read. No inter-agent messaging.

### 3.8 MCP Tool Interface

The memory graph is exposed to external agents via MCP (Model Context Protocol)
tools registered in `Ast.Memory/Mcp/`. The handler logic lives entirely in
`Ast.Memory`; `Ast.Cli` only adds a `config.AddBranch("memory", ...)` route.

| MCP Tool | Maps to | Description |
|---|---|---|
| `memory_query` | `IMemoryRetrievalEngine.RetrieveAsync()` | Hybrid context retrieval with configurable mode and budget |
| `memory_status` | `IMemoryGraphStore.GetMemoryStatisticsAsync()` | Graph statistics (node/edge counts by layer) |
| `memory_record_expertise` | `IEvolutionEngine.RecordExpertiseAsync()` | Write an expertise entry (title, body, kind, target) |
| `memory_sync` | `IIncrementalSync.ApplyDiffAsync()` | Trigger incremental sync |
| `memory_consolidate` | `IEvolutionEngine.ConsolidateAsync()` | Run consolidation (admin) |

### 3.9 Error Handling and Resilience

The memory layer follows a **degrade-but-proceed** pattern. No single
component failure should block agent work — the agent can always fall back
to reading raw files. Each component has a defined degradation path:

| Component | Failure mode | Degradation behavior |
|---|---|---|
| **Extraction Pipeline** | Parser throws on malformed file | Log error + skip file. Return `ExtractionResult` with partial counts and the file path in `Errors`. The graph contains whatever was extracted before the failure. |
| **Extraction Pipeline** | `IEmbeddingGenerator` unavailable during `SemanticIndexer` phase | Skip embedding generation and `SIMILAR_TO` edge creation. Structural and Call-Chain layers are still fully populated. `ExtractionResult.EmbeddingsGenerated = 0`. |
| **Retrieval Engine** | `IEmbeddingGenerator` unavailable at query time | Skip `SemanticSearch` mode. Re-normalize remaining 4 signal weights to sum to 1.0 (§3.3). Return results from Structural, Call-Chain, and Expertise modes only. `RetrievalDiagnostics` records the skip. |
| **Retrieval Engine** | `IVectorStore` unavailable | Same as embedding unavailable — `SemanticSearch` disabled. Other modes proceed. |
| **Retrieval Engine** | `IMemoryGraphStore` unavailable (graph DB down) | Return empty `RetrievalResult` with zero nodes and a diagnostic error. Agent falls back to raw file reading. |
| **Incremental Sync** | `git diff` fails (not a git repo, or commit SHA not found) | Return `SyncResult` with zero changes and log the error. No graph mutations. |
| **Incremental Sync** | Re-extraction of a changed file fails | Log error, skip that file. Other files in the batch proceed. Failed files are logged in `SyncResult` (future: add `Errors` field). |
| **Incremental Sync** | Expertise decay fails (target node not found) | Log warning, skip that expertise entry. Other entries decay normally. |
| **Evolution Engine** | LLM unavailable during `ConsolidateAsync` | Return `ConsolidationResult` with `MergedCount = 0`. Expertise entries remain unconsolidated — they are still individually queryable. |
| **Evolution Engine** | LLM unavailable during `DetectCommunitiesAsync` | Skip LLM-generated `Topic` summaries. `BELONGS_TO_CLUSTER` edges are still created (Louvain runs locally). `Topic` nodes get empty `Summary` metadata. |
| **Storage Layer** | SQLite write fails (disk full, WAL corruption) | Throw — writes are critical. Caller handles and logs. The `SemaphoreSlim(1,1)` serialization prevents partial-write corruption in SQLite journal mode. |
| **Storage Layer** | Neo4j connection lost mid-transaction | Transaction rolls back (ACID). Caller retries or reports failure. |
| **MCP Handler** | Invalid parameters from agent | Return structured error JSON with field-level validation messages. Do not throw unhandled exceptions through the MCP transport. |

**Retry policy:** Write operations do **not** auto-retry — the
serialized-write model (§3.7) means retries could re-order mutations.
Callers (MCP handler, CLI, IncrementalSync) are responsible for retry
decisions. Read operations (retrieval) are idempotent and safe to retry.

**Circuit breaker for embedding service:** If the local Ollama endpoint
fails 3 consecutive calls within a 60-second window, the Retrieval Engine
enters a "structural-only" mode for 5 minutes before re-probing. This
prevents repeated latency spikes from a downed embedding service.

### 3.10 Observability Model

Every alert has an owner, a runbook, and a kill switch. The memory layer
emits telemetry at three levels:

#### Structured Logging (`ILogger<T>`)

All components log via the standard `Microsoft.Extensions.Logging` pattern
at these levels:

| Level | Examples |
|---|---|
| `Information` | Extraction started/completed, sync completed, retrieval completed (with timing and counts) |
| `Warning` | Embedding service unavailable (fallback activated), expertise decay skipped (target not found), unregistered `Metadata` key used (`ext:` prefix), consolidation skipped (LLM unavailable) |
| `Error` | Parser failure on specific file, storage write failure, MCP handler unhandled exception |
| `Debug` | Per-file extraction counts, per-mode retrieval candidate counts, individual decay computations |

Log messages use structured parameters (`{NodeCount}`, `{Duration}`,
`{FilePath}`) for machine-parseable aggregation.

#### Metrics (Counter / Histogram)

Key metrics exposed for dashboard integration:

| Metric | Type | Labels | Description |
|---|---|---|---|
| `ast_memory_extraction_duration_seconds` | Histogram | `mode` (full/incremental) | Time for extraction pipeline |
| `ast_memory_extraction_nodes_total` | Counter | `layer`, `node_type` | Nodes created per extraction |
| `ast_memory_retrieval_duration_seconds` | Histogram | `mode` (Hybrid/StructuralDrill/...) | End-to-end retrieval latency (SLO: p95 < 500 ms) |
| `ast_memory_retrieval_tokens_used` | Histogram | `mode` | Token budget utilization per query |
| `ast_memory_sync_files_processed` | Counter | `status` (added/modified/deleted) | Files processed per sync |
| `ast_memory_expertise_total` | Gauge | `kind`, `state` (active/archived) | Live expertise entries |
| `ast_memory_expertise_confidence` | Histogram | `kind` | Confidence distribution |
| `ast_memory_decay_entries_processed` | Counter | `outcome` (decayed/archived/skipped) | Decay pass throughput |
| `ast_memory_consolidation_merges` | Counter | — | Entries merged per consolidation |
| `ast_memory_embedding_failures_total` | Counter | `phase` (extraction/retrieval) | Embedding service unavailability |
| `ast_memory_graph_size_bytes` | Gauge | `backend` (sqlite/neo4j) | On-disk graph size |

#### Health Checks

`ServiceCollectionExtensions.AddMemoryGraph()` registers an
`IHealthCheck` implementation (`MemoryGraphHealthCheck`) that verifies:

1. Graph store is reachable (`IMemoryGraphStore.GetMemoryStatisticsAsync()`
   completes within 5 s).
2. Vector store is reachable (`IVectorStore.IsAvailableAsync()`).
3. Embedding service is reachable (probe `IEmbeddingGenerator` with a
   dummy text — optional, reported as `Degraded` not `Unhealthy` if down).

This integrates with ASP.NET `HealthCheckService` for `/health` endpoint
exposure in `Ast.Cli` or hosting scenarios.

#### Audit Log (HC7)

The `audit_log` table (§4.4) is the operational post-mortem trail. Every
graph mutation is recorded with `session_id`, `timestamp`, `operation`,
and `detail` JSON. The audit log is append-only and never truncated by
the memory layer. Retention management (archival, rotation) is left to
operational tooling outside this system.

---

## 4. Data Model

### 4.1 Composition Wrappers

The memory layer uses composition wrappers over the existing `Ast.Core` base
types. This preserves backward compatibility with ~997 existing tests (HC8).

```csharp
/// Wraps HypergraphNode with strongly-typed memory accessors.
/// Never inherits from HypergraphNode — contains it.
public class MemoryNode
{
    public HypergraphNode Inner { get; }

    // Strongly-typed accessors for Metadata keys
    public string MemoryLayer
        => (string)this.Inner.Metadata["MemoryLayer"];

    public string NodeKind
        => this.Inner.NodeType;  // "Repo","Workspace","Package","Module","Class","Interface","Function","Method","Property","Topic","External"

    public VersionRange? Version { get; }
    // Null for External boundary nodes (timeless — no VCS presence; §4.3).

    public string? ContentHash
        => this.Inner.Metadata.TryGetValue("ContentHash", out var v)
            ? (string?)v : null;
}

/// Wraps Hyperedge with strongly-typed memory accessors.
public class MemoryEdge
{
    public Hyperedge Inner { get; }

    public string SemanticRelation
        => (string)this.Inner.Metadata["SemanticRelation"];
        // One of: CONTAINS, DEPENDS_ON, CALLS, EXTENDS, IMPLEMENTS,
        //         REFERENCES, SIMILAR_TO, BELONGS_TO_CLUSTER,
        //         APPLIES_TO, SUPERSEDES, DERIVED_FROM

    public string MemoryLayer
        => (string)this.Inner.Metadata["MemoryLayer"];

    public VersionRange Version { get; }
    // Non-nullable: all edges carry temporal versioning (§4.3).
}
```

### 4.2 Entity Catalog

#### Code Nodes (Structural & Call-Chain layers)

| Field | Type | Source | Description |
|---|---|---|---|
| `Id` | `string` | `HypergraphNode.Id` | Format: `"/{relativePath}::{qualifiedName}"` for symbols (leading `/` always present); `"/{relativePath}"` for files/packages; `"/"` for repo root; `"ext:{package}:{qualifiedName}"` for external stubs (no leading `/`). Note: expertise nodes are **not** `HypergraphNode` instances — see Expertise Nodes below for their ID format. |
| `Name` | `string` | `HypergraphNode.Name` | Short name of the symbol |
| `NodeType` | `string` | `HypergraphNode.NodeType` | `"Repo"`, `"Workspace"` (monorepo only), `"Package"`, `"Module"`, `"Class"`, `"Interface"`, `"Function"`, `"Method"`, `"Property"`, `"External"` |
| `Language` | `string` | `HypergraphNode.Language` | `"C#"`, `"PowerShell"` |
| `FilePath` | `string` | `HypergraphNode.FilePath` | Relative path from repo root |
| `LineStart` / `LineEnd` | `int` | `HypergraphNode.LineStart/LineEnd` | Source location |
| `Metadata["MemoryLayer"]` | `string` | Memory wrapper | Always `"structural"` for in-repo code nodes (home layer — see §2.1 Node-Layer Assignment Rules). These nodes also participate in `call_chain` and `semantic` edges. |
| `Metadata["ContentHash"]` | `string` | Extraction pipeline | SHA-256 of source text — used for decay detection |
| `Metadata["SinceCommit"]` | `string` | Temporal model | Commit SHA when node was introduced |
| `Metadata["SinceIndex"]` | `int` | Temporal model | Monotonic commit index when introduced |
| `Metadata["UntilCommit"]` | `string?` | Temporal model | Commit SHA when node was removed (null = still active) |
| `Metadata["UntilIndex"]` | `int?` | Temporal model | Commit index when removed |

**External boundary nodes** represent out-of-repo dependencies (e.g.,
`Microsoft.CodeAnalysis`, `System.Management.Automation`). They are stored as
`HypergraphNode` with `NodeType = "External"`, `MemoryLayer = "call_chain"`, no
`ContentHash`, no `VersionRange`, and no embedding. They serve as termination
points for `DEPENDS_ON` and `CALLS` edges that cross the repository boundary.

External-node Metadata keys:

| Key | Type | Description |
|---|---|---|
| `MemoryLayer` | `string` | Always `"call_chain"` |
| `Package` | `string` | Package/assembly name (e.g., `"Microsoft.CodeAnalysis"`) |
| `QualifiedName` | `string` | Fully-qualified type/member name, or `"*"` for package-level stubs |

External nodes have `Id` format `"ext:{package}:{qualifiedName}"`, no
`LineStart`/`LineEnd`, empty `FilePath`, and `Language` set to the package
ecosystem (e.g., `"C#"` for NuGet, `"JavaScript"` for npm). They participate
in `CALLS`, `REFERENCES`, and `DEPENDS_ON` edges but are never extracted,
embedded, or versioned. Edges *to* External nodes carry `VersionRange` from
the in-repo endpoint's perspective (the edge's `since_index` tracks when the
reference was first seen, and `until_index` tracks when it was removed by
`RetireEdgeAsync` during incremental sync).

#### Expertise Nodes (Expertise layer)

Expertise entries are **not** `HypergraphNode` instances. They are first-class
entities stored in their own `expertise` table (§4.4) and linked to code nodes
via `APPLIES_TO` edges (which use `HyperedgeType.MemoryRelation`). The
`ExpertiseEntry` class owns all expertise-specific fields directly:

```csharp
public class ExpertiseEntry
{
    public required string Id { get; init; }
    // Format: "expertise:{kind}:{hash8}" where hash8 = first 8 chars of
    // SHA-256(Title + Body + TargetNodeId)

    public required ExpertiseKind Kind { get; init; }
    // Pattern, Pitfall, Preference, Insight

    public required string Title { get; init; }
    // Short headline summarizing the expertise (e.g., "AnalyzeExceptions misses catch-all patterns")

    public required string Body { get; init; }
    // Detailed description of the expertise observation

    public required string TargetNodeId { get; init; }
    // Primary code node this expertise applies to.
    // Additional targets may be linked via APPLIES_TO edges.

    public string Scope { get; init; } = "exact";
    // "exact" = applies to this node only; "subtree" = applies to this node
    // and all its structural children

    public double Confidence { get; set; } = 1.0;
    // [0.0, 1.0] — decays over time and on code changes

    public bool Archived { get; set; } = false;
    // true = soft-deleted (Confidence dropped below ArchiveConfidenceFloor)
    // Excluded from GetExpertiseForNodeAsync; visible via GetArchivedExpertiseAsync.
    // Can be restored via IEvolutionEngine.RestoreArchivedExpertiseAsync.

    public required string SourceAgent { get; init; }
    // Agent session identifier for provenance

    public IReadOnlyList<Citation> Evidence { get; init; } = [];
    // Supporting evidence from the session that created this entry

    public float[]? Embedding { get; set; }
    // Pre-computed embedding of Title + Body

    public string? ContentHash { get; set; }
    // SHA-256 of Title + Body — for deduplication

    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; set; }
}

public record Citation(string NodeId, string? CommitSha, string? Snippet);

public enum ExpertiseKind
{
    Pattern,     // Reusable design pattern observed
    Pitfall,     // Known bug-prone area or anti-pattern
    Preference,  // Team/project convention
    Insight      // General architectural observation
}
```

#### Topic Nodes (Semantic layer)

| Field | Type | Description |
|---|---|---|
| `Id` | `string` | `"topic:{hash8}"` |
| `NodeType` | `string` | `"Topic"` |
| `Metadata["MemoryLayer"]` | `string` | `"semantic"` |
| `Metadata["Summary"]` | `string` | LLM-generated prose summary of the cluster |
| `Metadata["MemberCount"]` | `int` | Number of nodes in the cluster |

### 4.3 Temporal Model (Dual-Axis)

Every node and edge carries a `VersionRange` — **except External boundary
nodes** (§4.2), which represent out-of-repo dependencies and are timeless
(no `SinceCommit`, `SinceIndex`, `UntilCommit`, or `UntilIndex`). External
nodes are never retired by incremental sync; they are garbage-collected only
during full re-extraction when no edge references them.

**Edges to/from External nodes** are versioned normally — they carry
`VersionRange` from the in-repo endpoint's perspective. The edge's
`since_index` tracks when the reference was first observed, and
`until_index` is set by `RetireEdgeAsync` when incremental sync detects
the reference has been removed. Only the External node itself is timeless.

```csharp
public record VersionRange(
    string Since,        // Commit SHA when introduced
    int SinceIndex,      // Monotonic commit index (integer, for range queries)
    string? Until,       // Commit SHA when removed (null = still active)
    int? UntilIndex      // Commit index when removed
);
```

**Axis 1 — Commit index (primary).** All point-in-time node queries use
integer comparison. External nodes (which lack `VersionRange`) are always
included — they are boundary stubs, not versioned entities:

```sql
-- Node temporal filter (External nodes have NULL since_index)
WHERE (since_index IS NULL /* External boundary nodes — always included */
       OR (since_index <= @target AND (until_index IS NULL OR until_index > @target)))
```

Edge temporal queries use a simpler filter since all edges carry `VersionRange`:

```sql
-- Edge temporal filter (all edges are versioned)
WHERE since_index <= @target AND (until_index IS NULL OR until_index > @target)
```

**Axis 2 — Wall-clock timestamp (secondary).** Available via join to the
`commits` table:

```sql
commits(sha TEXT PK, commit_index INT UNIQUE, timestamp DATETIME, author TEXT)
```

Wall-clock is used for time-based decay (`DecayHalfLifeDays`) and human-facing
display, never for graph filtering.

### 4.4 Storage Schema (SQLite mode)

```sql
-- Core graph tables
CREATE TABLE nodes (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    node_type   TEXT NOT NULL,
    language    TEXT NOT NULL,
    file_path   TEXT NOT NULL,
    line_start  INT,
    line_end    INT,
    metadata    TEXT NOT NULL,           -- JSON
    since_sha   TEXT,                    -- NULL for External boundary nodes
    since_index INT,                     -- NULL for External boundary nodes
    until_sha   TEXT,
    until_index INT,
    content_hash TEXT
);

CREATE TABLE edges (
    id              TEXT PRIMARY KEY,
    edge_type       INT NOT NULL,        -- HyperedgeType enum value
    node_ids        TEXT NOT NULL,        -- JSON array
    roles           TEXT NOT NULL,        -- JSON object
    metadata        TEXT NOT NULL,        -- JSON
    semantic_rel    TEXT NOT NULL,        -- denormalized from metadata
    memory_layer    TEXT NOT NULL,        -- denormalized from metadata
    since_index     INT NOT NULL,         -- commit index when edge was introduced
    until_index     INT                   -- set by RetireEdgeAsync
);

CREATE TABLE expertise (
    id              TEXT PRIMARY KEY,
    kind            TEXT NOT NULL,        -- ExpertiseKind enum name
    title           TEXT NOT NULL,
    body            TEXT NOT NULL,
    target_node_id  TEXT NOT NULL REFERENCES nodes(id),
    scope           TEXT NOT NULL DEFAULT 'exact',  -- 'exact' or 'subtree'
    confidence      REAL NOT NULL DEFAULT 1.0,
    archived        INT NOT NULL DEFAULT 0,  -- 1 = soft-deleted (below ArchiveConfidenceFloor)
    source_agent    TEXT NOT NULL,
    evidence        TEXT,                 -- JSON array of Citation objects
    embedding       BLOB,                -- float[] serialized
    content_hash    TEXT,
    created_at      DATETIME NOT NULL,
    updated_at      DATETIME
);

CREATE TABLE commits (
    sha          TEXT PRIMARY KEY,
    commit_index INT NOT NULL UNIQUE,
    timestamp    DATETIME NOT NULL,
    author       TEXT
);

CREATE TABLE audit_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id  TEXT NOT NULL,
    timestamp   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    operation   TEXT NOT NULL,           -- 'create_node','update_edge','record_expertise',...
    detail      TEXT NOT NULL            -- JSON
);

-- Indexes
CREATE INDEX idx_nodes_layer ON nodes((json_extract(metadata, '$.MemoryLayer')));
CREATE INDEX idx_nodes_type ON nodes(node_type);
CREATE INDEX idx_nodes_file ON nodes(file_path);
CREATE INDEX idx_nodes_since ON nodes(since_index);
CREATE INDEX idx_edges_semantic ON edges(semantic_rel);
CREATE INDEX idx_edges_layer ON edges(memory_layer);
CREATE INDEX idx_edges_since ON edges(since_index);
CREATE INDEX idx_expertise_target ON expertise(target_node_id);
CREATE INDEX idx_expertise_kind ON expertise(kind);
CREATE INDEX idx_expertise_archived ON expertise(archived);
CREATE INDEX idx_audit_session ON audit_log(session_id);

-- Vector storage (sqlite-vss extension — local mode only)
-- SqliteVssVectorStore manages this virtual table via IVectorStore methods.
-- Collection names map to separate vss tables (e.g., vss_memory_embeddings).
CREATE VIRTUAL TABLE vss_memory_embeddings USING vss0(
    embedding(768)            -- nomic-embed-text dimension
);
-- Mapping table: links vss rowid to node/expertise ID
CREATE TABLE vss_id_map (
    rowid   INTEGER PRIMARY KEY,
    node_id TEXT NOT NULL UNIQUE   -- references nodes.id or expertise.id
);
CREATE INDEX idx_vss_id_map_node ON vss_id_map(node_id);
```

### 4.5 Metadata Key Registry

`AnnotationKeyRegistry` defines the well-known `Metadata` keys:

| Key | Type | Layer / Scope | Owner |
|---|---|---|---|
| `MemoryLayer` | `string` | All nodes and edges | Extraction Pipeline | Node home layer (§2.1 Node-Layer Assignment Rules): `"structural"` for in-repo code nodes, `"call_chain"` for External boundary nodes, `"semantic"` for Topic nodes. Edge layer per §2.2 edge taxonomy. |
| `SemanticRelation` | `string` | All edges | Extraction Pipeline / Evolution Engine |
| `ContentHash` | `string` | In-repo nodes only (Structural, Call-Chain). **Not** on External boundary nodes. | Extraction Pipeline |
| `SinceCommit` | `string` | In-repo nodes and all edges. **Not** on External boundary nodes (§4.3). | Temporal Model |
| `SinceIndex` | `int` | In-repo nodes and all edges. **Not** on External boundary nodes (§4.3). | Temporal Model |
| `UntilCommit` | `string?` | In-repo nodes and all edges. **Not** on External boundary nodes. | Temporal Model / Incremental Sync |
| `UntilIndex` | `int?` | In-repo nodes and all edges. **Not** on External boundary nodes. | Temporal Model / Incremental Sync |
| `Summary` | `string` | Semantic (Topic nodes) | Evolution Engine |
| `MemberCount` | `int` | Semantic (Topic nodes only — see §4.2; `SemanticCluster` is a retired tech-spec term, see A17 in §8.1) | Evolution Engine |
| `SimilarityScore` | `double` | Semantic (`SIMILAR_TO` edges) | Semantic Indexer |
| `OriginalEdgeType` | `string` | Code-derived edges | Extraction Pipeline |
| `Confidence` | `double` | Call-Chain edges, Expertise edges | Extraction Pipeline / Evolution Engine |
| `Stale` | `bool` | Call-Chain edges | Incremental Sync |
| `QualifiedName` | `string` | In-repo nodes and External nodes | Extraction Pipeline |
| `Package` | `string` | External boundary nodes only | Extraction Pipeline — package/assembly name (e.g., `"Microsoft.CodeAnalysis"`) |
| `LastTouched` | `string` | In-repo nodes only. **Not** on External boundary nodes (they are timeless — §4.3). | Incremental Sync (ISO-8601 wall-clock; informational — not used for graph filtering) |
| `Embedding` | `float[]` | In-repo nodes only (serialized). **Not** on External boundary nodes (they are never embedded — §4.2). | Semantic Indexer |
| `EmbeddingModel` | `string` | In-repo nodes with embeddings | Semantic Indexer |

Agent-authored extension keys use the `ext:` prefix (e.g., `ext:team-owner`).
Keys without `ext:` prefix that are not in the registry produce a validation
warning at write time.

### 4.6 HyperedgeType Preservation Rule

**Code-derived edges** (CONTAINS, DEPENDS_ON, CALLS, EXTENDS, IMPLEMENTS,
REFERENCES) retain their original `HyperedgeType` enum value (e.g.,
`HyperedgeType.MethodInvocation` for a CALLS edge). The canonical semantic
relation is stored in `Metadata["SemanticRelation"]`, and the original enum
value is also recorded in `Metadata["OriginalEdgeType"]` for traceability.

**Memory-only edges** (SIMILAR_TO, BELONGS_TO_CLUSTER, APPLIES_TO, SUPERSEDES,
DERIVED_FROM) use the single new `HyperedgeType.MemoryRelation` enum value.
Their semantic type is exclusively in `Metadata["SemanticRelation"]`.

This split ensures:
- Existing `IGraphStore` queries that filter by `HyperedgeType` continue to
  work unchanged for code-derived edges (backward compatibility).
- Memory-only edges are invisible to legacy code paths that do not know about
  `MemoryRelation`.
- The retrieval engine uses `Metadata["SemanticRelation"]` uniformly for all
  edges, regardless of their `HyperedgeType`.

### 4.7 Expertise Edge Storage Semantics

Expertise-layer edges (`APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM`) use
`HyperedgeType.MemoryRelation` and are stored in the `edges` table alongside
all other edges. Their distinguishing characteristic is that one or both
endpoint IDs in `node_ids` reference the `expertise` table rather than the
`nodes` table. The ID format prefix determines the source table:

| Edge | `node_ids[0]` (role) | `node_ids[1]` (role) | Source tables |
|---|---|---|---|
| `APPLIES_TO` | Expertise entry ID — `"expertise:{kind}:{hash8}"` (role: `expertise`) | Code node ID — `"/{path}::{name}"` (role: `target`) | `expertise` × `nodes` |
| `SUPERSEDES` | Merged entry ID — `"expertise:{kind}:{hash8}"` (role: `newer`) | Original entry ID — `"expertise:{kind}:{hash8}"` (role: `older`) | `expertise` × `expertise` |
| `DERIVED_FROM` | Derived entry ID — `"expertise:{kind}:{hash8}"` (role: `derived`) | Source entry ID — `"expertise:{kind}:{hash8}"` (role: `source`) | `expertise` × `expertise` |

**Cross-table reference model:** The `edges` table does not enforce foreign-key
constraints on `node_ids`. Referential integrity is maintained by the Evolution
Engine's atomic workflows:

- `RecordExpertiseAsync` creates the `ExpertiseEntry` row in `expertise` and the
  `APPLIES_TO` edge in `edges` within the same serialized write (§3.7).
- `ConsolidateAsync` creates the merged `ExpertiseEntry`, `SUPERSEDES` and
  `DERIVED_FROM` edges, and archives the originals — all within a single
  write-locked transaction.
- `ArchiveExpertiseAsync` soft-deletes the expertise row (`archived = 1`) but
  does **not** retire the `APPLIES_TO` edge. The edge remains in `edges` for
  provenance; the Retrieval Engine filters out archived expertise by checking
  `ExpertiseEntry.Archived` after following the edge, not by removing the edge
  itself.

**Retrieval path:** When the Ranker computes the `expertise` score signal for a
code node, it follows `APPLIES_TO` edges *inbound* to that code node (where the
code node appears as `node_ids[1]` with role `target`). For each edge, it looks
up the `node_ids[0]` expertise ID in the `expertise` table and reads the
`Confidence` value. The maximum `Confidence` across all non-archived
`APPLIES_TO` edges becomes the node's expertise score (§2.4).

---

## 5. Interfaces

All interfaces live in `Ast.Memory/Interfaces/`.

### 5.1 IMemoryGraphStore

Extends the existing `IGraphStore` from `Ast.GraphDb` with memory-specific
operations. Both `SqliteGraphStore` and `Neo4jMemoryGraphStore` implement this.

```csharp
namespace Ast.Memory.Interfaces;

using Ast.Core;
using Ast.GraphDb;

public interface IMemoryGraphStore : IGraphStore
{
    /// Query nodes filtered by layer and temporal range.
    /// Filters by the node's **home layer** (Metadata["MemoryLayer"]) — see §2.1
    /// Node-Layer Assignment Rules. A node with home layer "structural" that
    /// participates as an endpoint on a "call_chain" edge will NOT be returned
    /// by GetNodesByLayerAsync("call_chain"). To find nodes participating in
    /// call-chain edges, query edges via GetEdgesBySemanticRelationAsync and
    /// then resolve endpoint node IDs.
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken ct = default);

    /// Query edges filtered by SemanticRelation and layer.
    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer = null,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default);

    /// Upsert a node with temporal versioning.
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken ct = default);

    /// Upsert an edge with temporal versioning.
    /// Sets the edge's since_index / until_index columns from the
    /// VersionRange and writes denormalized semantic_rel / memory_layer
    /// columns from Metadata["SemanticRelation"] / Metadata["MemoryLayer"].
    /// Code-derived edges retain their original HyperedgeType; memory-only
    /// edges use HyperedgeType.MemoryRelation (§4.6).
    Task UpsertEdgeAsync(
        Hyperedge edge,
        VersionRange version,
        CancellationToken ct = default);

    /// Soft-delete: set UntilIndex on a node and its edges.
    Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        CancellationToken ct = default);

    /// Soft-delete a single edge: set UntilIndex.
    /// Used by IncrementalSync when a call-chain or dependency relationship
    /// is removed but both endpoint nodes survive.
    Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        CancellationToken ct = default);

    /// Update Metadata on an existing edge (e.g., set Stale flag,
    /// update Confidence after re-validation).
    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> metadataUpdates,
        CancellationToken ct = default);

    /// Retrieve non-archived expertise entries for a target node.
    /// Excludes entries where archived = true. Use GetArchivedExpertiseAsync
    /// to inspect soft-deleted entries.
    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken ct = default);

    /// Store or update an expertise entry.
    Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken ct = default);

    /// Soft-delete an expertise entry (set archived = true).
    /// Called by DecayService when Confidence drops below ArchiveConfidenceFloor.
    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken ct = default);

    /// Retrieve archived (soft-deleted) expertise entries for review/restore.
    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null,
        int limit = 50,
        CancellationToken ct = default);

    /// Get graph statistics segmented by layer.
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken ct = default);

    /// Append to audit log.
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default);
}
```

### 5.2 IMemoryRetrievalEngine

The primary query interface consumed by agents and the MCP tool handler.

```csharp
namespace Ast.Memory.Interfaces;

public interface IMemoryRetrievalEngine
{
    /// Retrieve ranked context for a query within a token budget.
    Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken ct = default);
}

public class RetrievalRequest
{
    /// Free-text query (task description, question).
    public string? QueryText { get; init; }

    /// Seed node IDs for structural/call-chain traversal.
    public IReadOnlyList<string>? SeedNodeIds { get; init; }

    /// Retrieval mode. Default: Hybrid.
    public RetrievalMode Mode { get; init; } = RetrievalMode.Hybrid;

    /// Token budget (clamped to [1, TokenBudgetCeiling]).
    public int TokenBudget { get; init; } = 8_000;

    /// Optional: target commit index for point-in-time queries.
    public int? AtCommitIndex { get; init; }

    /// Optional: override scoring weights.
    public ScoringWeights? Weights { get; init; }
}

public class RetrievalResult
{
    /// Ranked nodes with scores.
    public required IReadOnlyList<ScoredMemoryNode> Nodes { get; init; }

    /// Assembled context Markdown (ready for prompt injection).
    public required string ContextMarkdown { get; init; }

    /// Actual tokens used.
    public int TokensUsed { get; init; }

    /// Which retrieval modes were executed.
    public IReadOnlyList<RetrievalMode> ModesExecuted { get; init; } = [];

    /// Diagnostics: per-mode candidate counts, timing.
    public RetrievalDiagnostics? Diagnostics { get; init; }
}

public class ScoredMemoryNode
{
    public required MemoryNode Node { get; init; }
    public double CompositeScore { get; init; }
    public double StructuralScore { get; init; }
    public double SemanticScore { get; init; }
    public double CentralityScore { get; init; }
    public double RecencyScore { get; init; }
    public double ExpertiseScore { get; init; }
}
```

### 5.3 IEvolutionEngine

```csharp
namespace Ast.Memory.Interfaces;

public interface IEvolutionEngine
{
    /// Record an expertise entry and link it to a target code node.
    Task<ExpertiseEntry> RecordExpertiseAsync(
        string targetNodeId,
        ExpertiseKind kind,
        string title,
        string body,
        string sourceAgent,
        string scope = "exact",
        IReadOnlyList<Citation>? evidence = null,
        CancellationToken ct = default);

    /// Consolidate similar expertise entries (LLM-powered merge).
    /// Acquires write lock — only one consolidation at a time.
    Task<ConsolidationResult> ConsolidateAsync(
        CancellationToken ct = default);

    /// Apply decay to stale expertise entries.
    /// Entries whose Confidence drops below ArchiveConfidenceFloor (0.1) are
    /// archived via IMemoryGraphStore.ArchiveExpertiseAsync.
    Task<DecayResult> DecayStaleExpertiseAsync(
        CancellationToken ct = default);

    /// Restore an archived expertise entry (un-archive), resetting its
    /// Confidence to a caller-supplied value (default 0.5).
    Task<ExpertiseEntry> RestoreArchivedExpertiseAsync(
        string expertiseId,
        double restoredConfidence = 0.5,
        CancellationToken ct = default);

    /// Run community detection and generate Topic summaries.
    Task<CommunityResult> DetectCommunitiesAsync(
        CancellationToken ct = default);
}

public enum RetrievalMode
{
    StructuralDrill,
    CallTrace,
    SemanticSearch,
    ExpertiseRecall,
    Hybrid
}
```

### 5.4 IExtractionPipeline & IIncrementalSync

```csharp
namespace Ast.Memory.Interfaces;

public interface IExtractionPipeline
{
    /// Full extraction: parse repo → build all four layers.
    Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default);

    /// Re-extract only the specified files (used by IncrementalSync).
    /// Returns nodes/edges for the changed files only.
    Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default);
}

public class ExtractionOptions
{
    public IReadOnlyList<string> Languages { get; init; } = ["C#", "PowerShell"];
    public string? CommitSha { get; init; }
    public bool IncludeTestProjects { get; init; } = false;
    public bool GenerateEmbeddings { get; init; } = true;
}

public class ExtractionResult
{
    public int NodesCreated { get; init; }
    public int EdgesCreated { get; init; }
    public int EmbeddingsGenerated { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public interface IIncrementalSync
{
    /// Incremental update: diff commits → re-extract changed files.
    Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default);
}

public class SyncResult
{
    public int FilesChanged { get; init; }
    public int NodesAdded { get; init; }
    public int NodesRetired { get; init; }
    public int EdgesAdded { get; init; }
    public int EdgesRetired { get; init; }
    public int ExpertiseDecayed { get; init; }
    public TimeSpan Duration { get; init; }
}
```

### 5.5 Dependency Graph (Project References)

```
Ast.Memory
  ├── Ast.Core           (existing — HypergraphNode, Hyperedge, HyperedgeType)
  ├── Ast.GraphDb         (existing — IGraphStore, NodeFilter, GraphPath)
  ├── Ast.Embedding       (existing — IEmbeddingGenerator)
  ├── Ast.VectorDb        (existing — IVectorStore, VectorPoint, VectorSearchResult, VectorFilter)
  ├── Ast.Query           (existing — ICodeQuery)
  ├── Ast.Csharp          (existing — C# parser)
  └── Ast.PowerShell      (existing — PowerShell parser)

Ast.Memory.UnitTests
  ├── Ast.Memory
  └── Ast.Core

Ast.Memory.IntegrationTests
  ├── Ast.Memory
  ├── Ast.GraphDb
  ├── Ast.VectorDb
  └── Ast.Embedding
```

---

## 6. Sequence Flows

### 6.1 Full Extraction (First-Time Indexing)

```
Agent/CLI                     ExtractionPipeline       Parsers            IMemoryGraphStore    IEmbeddingGenerator    IVectorStore
  │                                │                      │                    │                      │                      │
  │  ExtractAsync(repoRoot, opts)  │                      │                    │                      │                      │
  │──────────────────────────────▶│                      │                    │                      │                      │
  │                                │  Parse(repoRoot)     │                    │                      │                      │
  │                                │─────────────────────▶│                    │                      │                      │
  │                                │  HypergraphData      │                    │                      │                      │
  │                                │◀─────────────────────│                    │                      │                      │
  │                                │                      │                    │                      │                      │
  │                                │  ── StructureBuilder ──                   │                      │                      │
  │                                │  Build Repo/Pkg/Module/Class/Fn nodes     │                      │                      │
  │                                │  + CONTAINS, DEPENDS_ON edges             │                      │                      │
  │                                │  UpsertNodeAsync(node, version) ×N        │                      │                      │
  │                                │  UpsertEdgeAsync(edge, version) ×E        │                      │                      │
  │                                │──────────────────────────────────────────▶│                      │                      │
  │                                │                      │                    │                      │                      │
  │                                │  ── CallChainAnalyzer ──                  │                      │                      │
  │                                │  Map MethodInvocation → CALLS edges       │                      │                      │
  │                                │  Map Extends/Implements edges             │                      │                      │
  │                                │  UpsertEdgeAsync(edge, version) ×M        │                      │                      │
  │                                │──────────────────────────────────────────▶│                      │                      │
  │                                │                      │                    │                      │                      │
  │                                │  ── SemanticIndexer ──                    │                      │                      │
  │                                │  GenerateBatchEmbeddingsAsync(texts)      │                      │                      │
  │                                │──────────────────────────────────────────────────────────────────▶│                      │
  │                                │  float[][] embeddings                     │                      │                      │
  │                                │◀──────────────────────────────────────────────────────────────────│                      │
  │                                │                      │                    │                      │                      │
  │                                │  UpsertAsync(collectionName,             │                      │                      │
  │                                │    [VectorPoint{Id=nodeId,               │                      │                      │
  │                                │     Vector=embedding}]) ×N               │                      │                      │
  │                                │  (store vectors for ANN retrieval)        │                      │                      │
  │                                │──────────────────────────────────────────────────────────────────────────────────────────▶│
  │                                │                      │                    │                      │                      │
  │                                │  UpsertNodeAsync(node w/ Metadata["Embedding"]) ×N               │                      │
  │                                │  + UpsertEdgeAsync(SIMILAR_TO where cosine ≥ 0.85) ×S            │                      │
  │                                │──────────────────────────────────────────▶│                      │                      │
  │                                │                      │                    │                      │                      │
  │                                │  LogAuditAsync(...)   │                    │                      │                      │
  │                                │──────────────────────────────────────────▶│                      │                      │
  │  ExtractionResult              │                      │                    │                      │                      │
  │◀──────────────────────────────│                      │                    │                      │                      │
```

### 6.2 Context Retrieval (Agent Query)

```
Agent                  MCP/CLI              RetrievalEngine        QueryPlanner     IVectorStore     IEmbeddingGenerator   Ranker        BudgetController   ContextAssembler
  │                      │                       │                     │              │                │                    │                 │                  │
  │  memory_query(req)   │                       │                     │              │                │                    │                 │                  │
  │─────────────────────▶│                       │                     │              │                │                    │                 │                  │
  │                      │  RetrieveAsync(req)   │                     │              │                │                    │                 │                  │
  │                      │──────────────────────▶│                     │              │                │                    │                 │                  │
  │                      │                       │  PlanQuery(req)     │              │                │                    │                 │                  │
  │                      │                       │───────────────────▶│              │                │                    │                 │                  │
  │                      │                       │  [modes]            │              │                │                    │                 │                  │
  │                      │                       │◀───────────────────│              │                │                    │                 │                  │
  │                      │                       │                     │              │                │                    │                 │                  │
  │                      │                       │  ── per mode ──     │              │                │                    │                 │                  │
  │                      │                       │  StructuralDrill: BFS via IMemoryGraphStore (CONTAINS/DEPENDS_ON edges)  │                 │                  │
  │                      │                       │  CallTrace: forward/backward via IMemoryGraphStore (CALLS/EXTENDS edges) │                 │                  │
  │                      │                       │  SemanticSearch:    │              │                │                    │                 │                  │
  │                      │                       │    Embed query text │              │                │                    │                 │                  │
  │                      │                       │    (1 local call)   │              │                │                    │                 │                  │
  │                      │                       │──────────────────────────────────────────────────▶│                    │                 │                  │
  │                      │                       │    float[] qEmbed   │              │                │                    │                 │                  │
  │                      │                       │◀──────────────────────────────────────────────────│                    │                 │                  │
  │                      │                       │    ANN lookup       │              │                │                    │                 │                  │
  │                      │                       │    SearchAsync(collectionName,             │                │                    │                 │                  │
  │                      │                       │      qEmbed, topK, filter?)                │                │                    │                 │                  │
  │                      │                       │──────────────────────────────────▶│                │                    │                 │                  │
  │                      │                       │    IReadOnlyList<VectorSearchResult>      │                │                    │                 │                  │
  │                      │                       │◀──────────────────────────────────│                │                    │                 │                  │
  │                      │                       │  ExpertiseRecall: filter by APPLIES_TO edges via IMemoryGraphStore      │                 │                  │
  │                      │                       │  → candidate nodes  │              │                │                    │                 │                  │
  │                      │                       │                     │              │                │                    │                 │                  │
  │                      │                       │  Rank(candidates, weights)         │                │                    │                 │                  │
  │                      │                       │────────────────────────────────────────────────────────────────────────▶│                 │                  │
  │                      │                       │  List<ScoredMemoryNode>            │                │                    │                 │                  │
  │                      │                       │◀────────────────────────────────────────────────────────────────────────│                 │                  │
  │                      │                       │                     │              │                │                    │                 │                  │
  │                      │                       │  ApplyBudget(ranked, tokenBudget)  │                │                    │                 │                  │
  │                      │                       │─────────────────────────────────────────────────────────────────────────────────────────▶│                  │
  │                      │                       │  trimmed list                      │                │                    │                 │                  │
  │                      │                       │◀─────────────────────────────────────────────────────────────────────────────────────────│                  │
  │                      │                       │                     │              │                │                    │                 │                  │
  │                      │                       │  Assemble(trimmed)  │              │                │                    │                 │                  │
  │                      │                       │────────────────────────────────────────────────────────────────────────────────────────────────────────────▶│
  │                      │                       │  contextMarkdown    │              │                │                    │                 │                  │
  │                      │                       │◀────────────────────────────────────────────────────────────────────────────────────────────────────────────│
  │                      │  RetrievalResult      │                     │              │                │                    │                 │                  │
  │                      │◀──────────────────────│                     │              │                │                    │                 │                  │
  │  RetrievalResult     │                       │                     │              │                │                    │                 │                  │
  │◀─────────────────────│                       │                     │              │                │                    │                 │                  │
```

### 6.3 Expertise Write-Back (Post-Session Learning)

```
Agent                  MCP/CLI              EvolutionEngine       IMemoryGraphStore   IEmbeddingGenerator
  │                      │                       │                      │                     │
  │  memory_record_      │                       │                      │                     │
  │  expertise(node,     │                       │                      │                     │
  │   kind, title, body) │                       │                      │                     │
  │─────────────────────▶│                       │                      │                     │
  │                      │  RecordExpertiseAsync  │                      │                     │
  │                      │──────────────────────▶│                      │                     │
  │                      │                       │  Validate target     │                     │
  │                      │                       │  GetNodeAsync(id)    │                     │
  │                      │                       │─────────────────────▶│                     │
  │                      │                       │  node (or null)      │                     │
  │                      │                       │◀─────────────────────│                     │
  │                      │                       │                      │                     │
  │                      │                       │  Embed title + body  │                     │
  │                      │                       │  GenerateEmbeddingAsync(title + " " + body) │
  │                      │                       │───────────────────────────────────────────▶│
  │                      │                       │  float[] embedding   │                     │
  │                      │                       │◀───────────────────────────────────────────│
  │                      │                       │                      │                     │
  │                      │                       │  Build ExpertiseEntry│                     │
  │                      │                       │  id = "expertise:{kind}:{hash8}"           │
  │                      │                       │  hash8 = SHA-256(Title+Body+TargetNodeId)  │
  │                      │                       │  Create APPLIES_TO edge                    │
  │                      │                       │                      │                     │
  │                      │                       │  UpsertExpertiseAsync(entry)                │
  │                      │                       │─────────────────────▶│                     │
  │                      │                       │  UpsertEdgeAsync(APPLIES_TO, version)       │
  │                      │                       │─────────────────────▶│                     │
  │                      │                       │  LogAuditAsync(...)  │                     │
  │                      │                       │─────────────────────▶│                     │
  │                      │                       │                      │                     │
  │                      │  ExpertiseEntry       │                      │                     │
  │                      │◀──────────────────────│                      │                     │
  │  ExpertiseEntry      │                       │                      │                     │
  │◀─────────────────────│                       │                      │                     │
```

### 6.4 Incremental Sync (Git Commit)

```
Git hook / Watcher      IncrementalSync         GitDiffDriver      ExtractionPipeline    IMemoryGraphStore     IVectorStore     SemanticIndexer   DecayService
  │                          │                       │                    │                    │                    │                │
  │  ApplyDiffAsync          │                       │                    │                    │                    │                │
  │  (repo, from, to)        │                       │                    │                    │                    │                │
  │─────────────────────────▶│                       │                    │                    │                    │                │
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Step 1: Register commit ──            │                    │                    │                │
  │                          │  Insert into commits table (sha, index, timestamp, author)     │                    │                │
  │                          │──────────────────────────────────────────────────────────────▶│                    │                │
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Step 2: Identify changes ──           │                    │                    │                │
  │                          │  git diff --name-     │                    │                    │                    │                │
  │                          │  status from..to      │                    │                    │                    │                │
  │                          │──────────────────────▶│                    │                    │                    │                │
  │                          │  [(status, path)]     │                    │                    │                    │                │
  │                          │  (A/M/D/R per file)   │                    │                    │                    │                │
  │                          │◀──────────────────────│                    │                    │                    │                │
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Steps 3-5: Re-extract A/M/R files ──  │                    │                    │                │
  │                          │  ExtractFilesAsync(repo, modifiedPaths)    │                    │                    │                │
  │                          │──────────────────────────────────────────▶│                    │                    │                │
  │                          │  new nodes/edges      │                    │                    │                    │                │
  │                          │◀──────────────────────────────────────────│                    │                    │                │
  │                          │                       │                    │                    │                    │                │
  │                          │  Diff old vs new      │                    │                    │                    │                │
  │                          │  UpsertNodeAsync (added/changed)          │                    │                    │                │
  │                          │  RetireNodeAsync (removed/renamed-old-path)│                    │                    │                │
  │                          │──────────────────────────────────────────────────────────────▶│                    │                │
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Step 6: Handle deleted files ──       │                    │                    │                │
  │                          │  RetireNodeAsync(file nodes, untilIndex)  │                    │                    │                │
  │                          │──────────────────────────────────────────────────────────────▶│                    │                │
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Step 7: Retire orphaned edges ──      │                    │                    │                │
  │                          │  RetireEdgeAsync(orphanedEdgeId, untilIndex) per edge          │                    │                │
  │                          │  whose source/target was retired but edge was not cascaded     │                    │                │
  │                          │──────────────────────────────────────────────────────────────▶│                    │                │
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Step 8: Mark downstream edges stale ──│                    │                    │                │
  │                          │  UpdateEdgeMetadataAsync(edgeId, {Stale:true}) per CALLS edge  │                    │                │
  │                          │  whose caller/callee signature changed    │                    │                    │                │
  │                          │──────────────────────────────────────────────────────────────▶│                    │                │
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Step 9: Re-embed changed nodes ──     │                    │                    │                │
  │                          │  SemanticIndexer.IndexAsync(changedNodes) │                    │                    │                │
  │                          │  (re-embeds via IEmbeddingGenerator,      │                    │                    │                │
  │                          │   upserts vectors via IVectorStore.UpsertAsync(collectionName, [VectorPoint{...}]),
  │                          │   retires old SIMILAR_TO edges,           │                    │                    │                │
  │                          │   creates new SIMILAR_TO edges)           │                    │                    │                │
  │                          │──────────────────────────────────────────────────────────────────────────────────────────────────────▶│                │
  │                          │  updated embeddings + SIMILAR_TO edges    │                    │                    │                │
  │                          │◀──────────────────────────────────────────────────────────────────────────────────────────────────────│                │
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Step 10: Decay expertise ──           │                    │                    │                │
  │                          │  Decay expertise targeting changed nodes  │                    │                    │                │
  │                          │─────────────────────────────────────────────────────────────────────────────────────────────────────▶│
  │                          │  DecayResult           │                    │                    │                    │                │
  │                          │◀─────────────────────────────────────────────────────────────────────────────────────────────────────│
  │                          │                       │                    │                    │                    │                │
  │                          │  ── Step 11: Audit log ──                 │                    │                    │                │
  │                          │  LogAuditAsync(...)   │                    │                    │                    │                │
  │                          │──────────────────────────────────────────────────────────────▶│                    │                │
  │  SyncResult              │                       │                    │                    │                    │                │
  │◀─────────────────────────│                       │                    │                    │                    │                │
```

### 6.5 Cross-Session Learning (Expertise Lifecycle)

This is not a single API call but a multi-session lifecycle. The example
uses real symbols from this repository's `src/ast/CodeIntelligence/` project.

1. **Session 1:** Agent investigates fragile exception handling in
   `ErrorHandlingAnalyzer.AnalyzeExceptions()` (`src/ast/CodeIntelligence/Analyzers/ErrorHandlingAnalyzer.cs`, line 50).
   Agent calls `RecordExpertiseAsync` with
   `targetNodeId="/src/ast/CodeIntelligence/Analyzers/ErrorHandlingAnalyzer.cs::ErrorHandlingAnalyzer.AnalyzeExceptions"`,
   `Kind=Pitfall`,
   `title="AnalyzeExceptions misses catch-all patterns"`, and `body` describing
   how the method's `SyntaxTree` traversal does not account for `catch` blocks
   that re-throw without filtering, and how its caller
   `RepositoryAnalysisService.AnalyzeRepositoryAsync()` (`src/ast/CodeIntelligence/Services/RepositoryAnalysisService.cs`, line 51)
   depends on non-null results from all analyzer methods.

2. **Session 2:** A different agent (or the same agent in a new session) works
   on `RepositoryAnalysisService`. During context retrieval (`RetrieveAsync`
   with `Mode=Hybrid`), the Ranker finds the Pitfall expertise node via the
   `APPLIES_TO` edge. The expertise contributes to the `expertise` score signal
   (weight 0.10), boosting `ErrorHandlingAnalyzer.AnalyzeExceptions()` in the
   ranked results. The Context Assembler includes it under `## Known Expertise`.

3. **Between sessions:** `DecayService` runs. If `AnalyzeExceptions()`'s
   `ContentHash` has changed since the expertise was written, the decay factor
   (0.5 for substantive change, 0.9 for whitespace-only) is applied to the
   expertise's `Confidence`. If `Confidence` drops below
   `ArchiveConfidenceFloor` (0.1), the entry is soft-deleted.

4. **Consolidation (periodic):** `ConsolidateAsync` finds two expertise entries
   targeting `ErrorHandlingAnalyzer.AnalyzeExceptions()` with cosine
   similarity ≥ 0.85. The LLM merges them into a single entry. A `SUPERSEDES`
   edge links the merged entry to the originals. A `DERIVED_FROM` edge links
   the merged entry to its sources.

---

## 7. Deployment Modes

| Mode | Graph Store | Vector Store | Infrastructure | Use Case |
|---|---|---|---|---|
| **Local** | `SqliteGraphStore` (SQLite + FTS5) | `SqliteVssVectorStore` (sqlite-vss HNSW, same `.db` file) | Zero — single file on disk | Solo developer, CI pipeline |
| **Team** | `Neo4jMemoryGraphStore` (Neo4j via `Ast.GraphDb`) | `QdrantVectorStore` (via existing `Ast.VectorDb`) | `docker-compose.yml` (existing in `src/ast/`) | Shared team graph, server deployment |

Backend selection is via DI configuration:

```csharp
// Local mode — single-file deployment
services.AddMemoryGraph(options => {
    options.Backend = MemoryBackend.Sqlite;
    options.SqlitePath = "~/.ast-memory/graph.db";
});
// Registers: SqliteGraphStore as IMemoryGraphStore
//            SqliteVssVectorStore as IVectorStore (same .db file)

// Team mode — shared infrastructure
services.AddMemoryGraph(options => {
    options.Backend = MemoryBackend.Neo4j;
    options.Neo4jUri = "bolt://localhost:7687";
    options.QdrantEndpoint = "localhost:6334";
});
// Registers: Neo4jMemoryGraphStore as IMemoryGraphStore
//            QdrantVectorStore as IVectorStore (existing impl from Ast.VectorDb)
```

---

## 8. Cross-Document Alignment

This section documents the relationship between this document and its
siblings. This document (`architecture.md`) is the **sole canonical
authority** for component boundaries, data model, edge/layer naming, ID
format, interface signatures, constants, and concurrency semantics. When
sibling documents disagree, this document governs — immediately and
without requiring the sibling document to be updated first.

### 8.1 `tech-spec.md`

Tech-spec.md was last updated at iteration 23. It contains twelve
locations where wording diverges from the canonical definitions in this
document. None of these represent open disagreements — implementors
follow this document's definitions (§2–§7) for all naming, schema, and
formula details. The 12 stale-wording locations:

1. **§7.1 Node Types table — `SemanticCluster` row.** Tech-spec lists
   `SemanticCluster` as a separate node type alongside `Topic`.
   Canonical: only `Topic` nodes exist for community clusters (§4.2);
   `SemanticCluster` is retired (§2.3).
2. **§7.1 Node Types table — `ExpertiseEntry` row.** Uses `source_session`
   field name. Canonical: field is `SourceAgent` (§4.2).
3. **§7.1 Node Types table — `ExpertiseEntry` row.** Hash formula says
   `SHA-256(Content + TargetNodeId)` with single `Content` field.
   Canonical: `SHA-256(Title + Body + TargetNodeId)` with separate
   `Title` and `Body` fields (§4.2).
4. **§7.1 ID format note.** IDs shown without leading `/` (e.g.,
   `src/ast/...`). Canonical: all in-repo node IDs start with `/`
   (e.g., `/src/ast/...`) per §4.2.
5. **§7.4 Temporal Model.** References `valid_from_idx` / `valid_until_idx`
   in the correction note. Canonical column names: `since_index` /
   `until_index` (§4.4).
6. **§8.4 Evolution — Decay row formula.** Uses
   `confidence × 2^(−age_days / 1095) × centrality_boost` with
   `centrality_boost` as an output multiplier. Canonical: centrality
   modifies the half-life in the exponent denominator:
   `final = event_adjusted_confidence × 2^(−age_days / (DecayHalfLifeDays
   × centrality_boost))` (§3.5). Centrality as output multiplier would
   incorrectly increase confidence above the event-adjusted value.
7. **§7.3 Annotation Metadata table — `VersionRange` key.** Shows
   `VersionRange` as a single Metadata key with a JSON object value.
   Canonical: VersionRange is decomposed into four separate Metadata
   keys (`SinceCommit`, `SinceIndex`, `UntilCommit`, `UntilIndex`) and
   four separate SQLite columns (§4.4, §4.5).
8. **§8.2 Storage — `commits` table schema.** Uses column name
   `parent_sha`. Canonical: the `commits` table has columns `sha`,
   `commit_index`, `timestamp`, `author` — no `parent_sha` column (§4.4).
9. **§8.2 Storage — `nodes` table schema.** Uses column names
   `embedding_blob`, `hash`, `valid_from_idx`, `valid_until_idx`.
   Canonical: `content_hash` (not `hash`), `since_index` (not
   `valid_from_idx`), `until_index` (not `valid_until_idx`), and
   embeddings are stored in the `vss_memory_embeddings` virtual table
   via `IVectorStore`, not as a blob column in `nodes` (§4.4).
10. **§8.2 Storage — `edges` table schema.** Uses `confidence` as a
    dedicated column and `valid_from_idx` / `valid_until_idx`. Canonical:
    `Confidence` lives in `metadata` JSON; temporal columns are
    `since_index` / `until_index`; the table also has denormalized
    `semantic_rel` and `memory_layer` columns (§4.4).
11. **§1.1 Existing Infrastructure table — `Ast.VectorDb` row.** Says
    `Ast.Memory` adds "in-process HNSW adapter for local mode."
    Canonical: local mode uses `SqliteVssVectorStore` implementing the
    existing `IVectorStore` interface via the sqlite-vss extension (§3.2),
    not a standalone in-process HNSW adapter. The `IVectorStore` types
    (`VectorPoint`, `VectorSearchResult`, `VectorFilter`) are consumed
    unchanged from `Ast.VectorDb` (§3.2.1).
12. **§8.2 Storage — Deployment table.** Lists "sqlite-vss or in-process
    HNSW (usearch)" as the vector backend for local mode. Canonical:
    only `SqliteVssVectorStore` is supported for local mode (§3.2).
    There is no usearch backend in the architecture.

### 8.2 `implementation-plan.md`

The implementation-plan defers to this document on all naming/data-model
conflicts (its header explicitly states: "when literal names or values
here conflict with `architecture.md`, follow `architecture.md`").

**Active divergence requiring correction in the next impl-plan iteration:**

The impl-plan's Stage 1.2 (line 49) lists 12 methods for
`IMemoryGraphStore` and its test scenario (line 55) says "declares all
12 methods." **The canonical `IMemoryGraphStore` interface declares 13
methods** (§5.1). The missing method is:

```csharp
Task UpsertEdgeAsync(Hyperedge edge, VersionRange version, CancellationToken ct = default);
```

The complete canonical method list (13 methods):

1. `GetNodesByLayerAsync`
2. `GetEdgesBySemanticRelationAsync`
3. `UpsertNodeAsync`
4. **`UpsertEdgeAsync`** ← omitted from impl-plan
5. `RetireNodeAsync`
6. `RetireEdgeAsync`
7. `UpdateEdgeMetadataAsync`
8. `GetExpertiseForNodeAsync`
9. `UpsertExpertiseAsync`
10. `ArchiveExpertiseAsync`
11. `GetArchivedExpertiseAsync`
12. `GetMemoryStatisticsAsync`
13. `LogAuditAsync`

All edge-writing code paths (StructureBuilder, CallChainAnalyzer,
SemanticIndexer, EvolutionEngine) must use `UpsertEdgeAsync` — not
`IGraphStore.CreateEdgesAsync()` — to set `since_index`, `until_index`,
`semantic_rel`, and `memory_layer` denormalized columns on the `edges`
table (§4.4).

Additionally, the impl-plan's Stage 5.3 uses an incorrect decay formula
(`current_confidence × 2^(…) × centrality_boost`) that places
`centrality_boost` as an output multiplier, which would incorrectly
increase confidence. The canonical formula (§3.5) places centrality in
the exponent denominator: `final = event_adjusted_confidence ×
2^(−age_days / (DecayHalfLifeDays × centrality_boost))`.

Other naming/schema items (composition wrappers, `ExpertiseEntry` naming,
`Title`+`Body` fields, node ID format, `archived` column) have been
corrected in the implementation-plan.

**Vector-store interaction note:** The impl-plan's Stage 2.3
(SemanticIndexer) says "Store vectors in Qdrant (team mode via
`IVectorStore`) or sqlite-vss (local mode)" without specifying the
`IVectorStore` call signatures. Canonical calls use
`IVectorStore.UpsertAsync(string collectionName, IEnumerable<VectorPoint>
points, CancellationToken)` where each node is wrapped as a `VectorPoint`
(§3.2.1). Implementors should consult §3.2.1 for exact types.

### 8.3 `e2e-scenarios.md`

Fully aligned. The e2e-scenarios document uses canonical vocabulary
throughout: correct edge names, qualified node IDs with leading `/`,
single-colon `ext:` prefix, separate `Title`/`Body` fields, and correct
scenario descriptions for all 13 features (82 scenarios).

### 8.4 Existing `AgentMemory` Project

The repository already contains an `AgentMemory` project
(`src/ast/AgentMemory/`) with an episode-based memory model
(`IAgentMemoryStore`, `Episode`, `ExpertiseRecord`, `MemoryEdge`,
`MemoryTier`). This prototype uses a different data model (episodes +
tiered expertise) from the graph-based model defined in this document.

**Relationship:** `Ast.Memory` supersedes `AgentMemory` by extending the
model with four-layer graph structure, temporal versioning, hybrid
retrieval, and evolution. `Ast.Memory` does **not** depend on
`AgentMemory`. The `AgentMemory` project remains in the codebase for
backward compatibility; migration of its concepts into `Ast.Memory` is
out of scope for this story.

### 8.5 Governance Rule

This document (`architecture.md`) is the **sole canonical authority** for
all naming, data-model, edge taxonomy, ID format, interface signatures,
constants, and concurrency semantics. When sibling documents disagree,
this document governs — immediately and without requiring the sibling
document to be updated first. Implementors should always consult this
document for definitive answers.