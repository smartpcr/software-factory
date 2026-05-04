# Agent Memory Architecture

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 pts
>
> This document is the **canonical authority** for component boundaries,
> data model, edge/layer naming, ID format, interface signatures, and
> concurrency semantics. Sibling documents defer on conflicts:
>
> | Document | Owns | Defers to architecture.md on |
> |---|---|---|
> | [`tech-spec.md`](tech-spec.md) | Problem statement, scope/non-goals, hard constraints, risks, success criteria, resolved operator decisions | All naming, data-model shapes, formulas, constants, interface signatures |
> | [`implementation-plan.md`](implementation-plan.md) | Phased work items, deliverables, evaluation harness | All naming, data-model shapes, formulas, constants, interface signatures |
> | [`e2e-scenarios.md`](e2e-scenarios.md) | Gherkin acceptance scenarios | All vocabulary, edge names, ID formats, thresholds |

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

The interaction model:

- **Shared-nothing reads.** Each retrieval call is stateless and independent.
  Multiple concurrent `RetrieveAsync` calls are safe (§3.7).
- **Serialized writes.** All mutations (`ApplyDiffAsync`,
  `RecordExpertiseAsync`, `DecayStaleExpertiseAsync`) are serialized via
  in-process mutex (SQLite) or ACID transactions (Neo4j).
- **Indirect knowledge sharing.** Agents do not communicate with each other.
  Knowledge is shared indirectly: Agent A writes an expertise node; Agent B
  discovers it on a subsequent `RetrieveAsync` call. The `SourceAgent` field
  on `ExpertiseEntry` provides provenance.
- **No distributed lock for reads.** Only `ConsolidateAsync` (an admin
  operation) acquires a write-level lock to prevent concurrent merges.

### 1.3 Existing Infrastructure (`src/ast/`)

The system builds on the Code Intelligence stack in `src/ast/` (26 projects
total — 13 production, 13 test — verified via directory listing).
**Two existing projects receive additive-only changes:**

1. **`Ast.Core`** (`src/ast/Ast.Core/HypergraphModels.cs`) — one new enum
   value `HyperedgeType.MemoryRelation` appended to the existing 43-value
   `HyperedgeType` enum (lines 12–69 of `HypergraphModels.cs`), bringing the
   total to 44.
2. **`Ast.Cli`** (`src/ast/Ast.Cli/Program.cs`) — additive
   `config.AddBranch("memory", ...)` block appended after the existing 7
   `config.AddCommand<T>()` registrations (lines 17–44). A project reference
   to `Ast.Memory` is added to `Ast.Cli.csproj`.

All new code ships as **`Ast.Memory`** (+ `Ast.Memory.UnitTests` +
`Ast.Memory.IntegrationTests`). Existing projects are referenced as project
dependencies, never mutated beyond the two additive changes above.

| Existing project | Key types (verified) | How memory layer uses it |
|---|---|---|
| **Ast.Core** | `HypergraphNode`, `Hyperedge`, `HyperedgeType` (43 enum values) — `HypergraphModels.cs` | Reference types via `Metadata` dict. One additive enum value `MemoryRelation` (total → 44). |
| **AgentMemory** | `IAgentMemoryStore`, `Episode`, `ExpertiseRecord`, `MemoryEdge` — `AgentMemory.csproj` | Existing prototype with episode-based model. `Ast.Memory` supersedes it; no dependency on it. |
| **Ast.GraphDb** | `IGraphStore` (17 methods including `CreateNodeAsync`, `GetCallersAsync`, `FindPathsAsync`) — `IGraphStore.cs` | `Ast.Memory` defines `IMemoryGraphStore` extending `IGraphStore`. Zero files in `Ast.GraphDb/` modified. |
| **Ast.Embedding** | `IEmbeddingGenerator` (`GenerateEmbeddingAsync`, `GenerateBatchEmbeddingsAsync`, `Dimensions`, `ModelName`) — `IEmbeddingGenerator.cs`; default model `nomic-embed-text` | Reuse directly for embedding code and expertise nodes. |
| **Ast.Query** | `ICodeQuery`, `HybridQueryService` — `ICodeQuery.cs` | Reference only. `Ast.Memory` defines its own `IMemoryRetrievalEngine`. |
| **Ast.VectorDb** | `IVectorStore` (9 methods: `CreateCollectionAsync`, `UpsertAsync`, `SearchAsync`, etc.) — `IVectorStore.cs`; `QdrantVectorStore`, `VectorPoint`, `VectorSearchResult`, `VectorFilter` | Reuse `QdrantVectorStore` for team mode; `Ast.Memory` adds `SqliteVssVectorStore` for local mode. Zero files in `Ast.VectorDb/` modified. |
| **Ast.Lsp** | LSP integration | Optional enrichment — pipeline never blocks on LSP. No modifications. |
| **CodeIntelligence** | 27 analyzers, 4 detectors, `RelationshipGraphBuilder` | Consume as call-chain / structural edge source. No modifications. |
| **Ast.Cli** | CLI host — `Program.cs` with 7 commands: parse, enrich, embed, index, query, pipeline, stats | Additive: `config.AddBranch("memory", ...)` + project ref to `Ast.Memory`. |

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
| **Structural** | `structural` | Containment: Repo → [Workspace] → Package → Module → Class → Function (Workspace optional — monorepo only) | `CONTAINS`, `DEPENDS_ON` |
| **Call-Chain** | `call_chain` | Invocations and type relationships between symbols | `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` |
| **Semantic** | `semantic` | Embedding-similarity clusters and topic summaries | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` |
| **Expertise** | `expertise` | Agent-authored knowledge nodes | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` |

Temporal versioning is **orthogonal** — it applies to nodes/edges on every
layer via `VersionRange` (§4.3). It is not a separate layer.

#### Node-Layer Assignment Rules

Every `HypergraphNode` in the memory graph carries exactly one
`Metadata["MemoryLayer"]` value — its **home layer**. A node may participate
as an endpoint of edges on **other** layers.

| Node type(s) | Home layer | Created by | Cross-layer edge participation |
|---|---|---|---|
| `Repo`, `Workspace`, `Package`, `Module`, `Class`, `Interface`, `Function`, `Method`, `Property` | `structural` | `StructureBuilder` | Also endpoints on `call_chain` and `semantic` edges. |
| `External` | `call_chain` | `StructureBuilder` (when processing imports to out-of-repo dependencies) | Also endpoints on `structural` edges (`DEPENDS_ON`). |
| `Topic` | `semantic` | `EvolutionEngine.DetectCommunitiesAsync()` | Only `BELONGS_TO_CLUSTER` edges. |

`ExpertiseEntry` nodes are **not** `HypergraphNode` instances (§4.2) and do
not carry a `MemoryLayer` metadata key. Their edges (`APPLIES_TO`,
`SUPERSEDES`, `DERIVED_FROM`) are tagged `MemoryLayer = "expertise"`.

**Edges** carry their own `Metadata["MemoryLayer"]` matching the layer from
the edge taxonomy (§2.2). A `CALLS` edge between two `structural`-homed
nodes has `MemoryLayer = "call_chain"` on the edge.

### 2.2 Edge Taxonomy

**Code-derived edges** — produced by the Extraction Pipeline:

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
| `SemanticCluster` | `Topic` (NodeType `"Topic"`) | Use `Topic` nodes |
| `IAgentMemoryApi` | `IMemoryRetrievalEngine` + `IEvolutionEngine` | Split into purpose-specific interfaces |
| `Content` (single field) | `Title` + `Body` (two fields) | On ExpertiseEntry |

### 2.4 Constants & Thresholds (Single Source of Truth)

All configurable parameters. Implementations read from `MemoryOptions`.

| Constant | Default | Used by | Description |
|---|---|---|---|
| `SimilarityThreshold` | 0.85 | Semantic Indexer, Evolution Engine | Cosine-similarity floor for `SIMILAR_TO` and `DERIVED_FROM` edges |
| `StaleDecayFactor` | 0.5 | Evolution Engine | Confidence multiplier on substantive code change |
| `MinorDecayFactor` | 0.9 | Evolution Engine | Confidence multiplier on whitespace/comment-only change |
| `ArchiveConfidenceFloor` | 0.1 | Evolution Engine | Expertise nodes below this are soft-deleted |
| `DecayHalfLifeDays` | 1095 | Evolution Engine | Time-based decay half-life (3 years) |
| `TokenBudgetDefault` | 8,000 | Budget Controller | Per-query default token budget |
| `TokenBudgetCeiling` | 32,000 | Budget Controller | Maximum allowed token budget |
| `IncrementalSyncDebounceMs` | 1,000 | Incremental Sync | Debounce for file-system watcher |
| `CentralityBoostFactor` | 0.5 | Evolution Engine (Decay) | Multiplied by normalized PageRank and added to 1.0 to form `centrality_boost` (range 1.0–1.5). See §3.5 for the decay computation model. |

**Scoring weights** (used by the Ranker, sum = 1.00):

| Signal | Default weight | Description |
|---|---|---|
| `structural` | 0.30 | Inverse BFS distance from seed (Structural layer) — bounded [0,1] |
| `semantic` | 0.25 | Cosine similarity of query embedding to node embedding |
| `centrality` | 0.20 | Normalized fan-in from `CALLS` edges (PageRank-light) |
| `recency` | 0.15 | `1.0 − (HEAD_idx − node_sinceIdx) / max_distance`, clamped [0,1] |
| `expertise` | 0.10 | Max `Confidence` of any non-archived `APPLIES_TO` edge targeting the node; 0.0 when none |
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
| **Semantic Indexer** | `SemanticIndexer` | Batches nodes through `IEmbeddingGenerator.GenerateBatchEmbeddingsAsync()` (from existing `Ast.Embedding`), stores resulting vectors via the dual-storage pattern (§3.2.2), then computes pairwise cosine similarity and creates `SIMILAR_TO` edges where cosine ≥ `SimilarityThreshold` (0.85). |
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

### 3.2 Storage Layer

**Responsibility:** Persist and query the four-layer graph and associated
vector embeddings.

#### 3.2.1 Graph Store Implementations

| Implementation | Backing store | Use case |
|---|---|---|
| `SqliteGraphStore` | SQLite + FTS5 | Local / single-developer mode. Zero infrastructure. |
| `Neo4jMemoryGraphStore` | Neo4j (via existing `Ast.GraphDb`) | Team / CI mode. Shared graph across developers. |

Both implement `IMemoryGraphStore` (§5.1). Selection is via DI configuration
in `ServiceCollectionExtensions.cs`.

#### 3.2.2 Vector Store — Dual-Storage Pattern

Vector storage uses the existing `IVectorStore` interface from `Ast.VectorDb`
(9 methods: `CreateCollectionAsync`, `CollectionExistsAsync`,
`DeleteCollectionAsync`, `UpsertAsync`, `SearchAsync`, `GetAsync`,
`DeleteAsync`, `CountAsync`, `IsAvailableAsync`). `Ast.Memory` does **not**
define a new vector interface — it consumes `IVectorStore` as an injected
dependency.

| Implementation | Project | Backing store | Use case |
|---|---|---|---|
| `SqliteVssVectorStore` | `Ast.Memory` (`Storage/SqliteVssVectorStore.cs`) | sqlite-vss HNSW index on the same SQLite database used by `SqliteGraphStore` | Local mode — zero infrastructure |
| `QdrantVectorStore` | `Ast.VectorDb` (existing, unmodified) | Qdrant vector database | Team mode — shared vector index |

**Two collections** are maintained:

| Collection | Content | Written by | Queried by |
|---|---|---|---|
| `memory_embeddings` | Code-node embeddings | `SemanticIndexer` at extraction time | Retrieval Engine (`SemanticSearch` mode) |
| `memory_expertise` | Expertise-entry embeddings | `EvolutionEngine.RecordExpertiseAsync` at write-back time | `ConsolidationService` (similarity lookup), Retrieval Engine (`ExpertiseRecall` mode) |

**Embedding storage model — authority and paths:**

Embeddings are stored in **two locations** for different access patterns:

1. **Durable copy (authoritative):** Serialized `float[]` stored as a BLOB
   column in the SQLite/Neo4j record itself (`nodes.metadata` JSON for code
   nodes; `expertise.embedding` BLOB for expertise entries). This is the
   source of truth. On startup or after corruption, vector indexes can be
   rebuilt from these durable copies.

2. **ANN index (projection):** The same `float[]` is upserted into the
   `IVectorStore` collection for approximate nearest-neighbor search. This
   is a disposable projection — it can be rebuilt from the durable copies
   without data loss.

| Operation | Path |
|---|---|
| **Write (code node)** | `SemanticIndexer` calls `IEmbeddingGenerator.GenerateBatchEmbeddingsAsync()` → stores embedding in `Metadata["Embedding"]` via `IMemoryGraphStore.UpsertNodeAsync()` → upserts `VectorPoint { Id = nodeId, Vector = embedding, Payload = { MemoryLayer, NodeType } }` into `memory_embeddings` via `IVectorStore.UpsertAsync()` |
| **Write (expertise)** | `EvolutionEngine.RecordExpertiseAsync()` embeds `Title + " " + Body` → stores in `ExpertiseEntry.Embedding` via `IMemoryGraphStore.UpsertExpertiseAsync()` → upserts `VectorPoint { Id = expertiseId, Vector = embedding, Payload = { Kind, TargetNodeId } }` into `memory_expertise` via `IVectorStore.UpsertAsync()` |
| **Query (semantic search)** | Retrieval Engine embeds query text → calls `IVectorStore.SearchAsync("memory_embeddings", queryVector, topK, filter?)` → gets `IReadOnlyList<VectorSearchResult>` → resolves graph nodes by `Id` |
| **Query (expertise recall)** | Retrieval Engine embeds query text → calls `IVectorStore.SearchAsync("memory_expertise", queryVector, topK)` → follows `APPLIES_TO` edges to discover code nodes |
| **Query (consolidation)** | `ConsolidationService` calls `IVectorStore.SearchAsync("memory_expertise", entryEmbedding, topK)` → finds candidates with cosine ≥ 0.85 → verifies exact cosine on loaded BLOB → merges confirmed pairs |
| **Rebuild** | On index corruption or model upgrade: iterate all durable embeddings → batch-upsert into `IVectorStore` |

**Key types** (from `Ast.VectorDb/VectorModels.cs`, consumed unchanged):

| Type | Fields | Usage |
|---|---|---|
| `VectorPoint` | `Id: string`, `Vector: float[]`, `Payload: Dictionary<string,object>` | Created by `SemanticIndexer` and `EvolutionEngine` |
| `VectorSearchResult` | `Id: string`, `Score: float`, `Vector: float[]?`, `Payload: Dictionary<string,object>` | Returned by `SearchAsync`; `Score` feeds the `semantic` signal |
| `VectorFilter` | `Must/Should/MustNot: List<FilterCondition>` | Used to filter by `MemoryLayer` payload field |

**`SqliteVssVectorStore` mapping:** The `collectionName` parameter maps to
per-collection virtual tables (e.g., `vss_memory_embeddings`,
`vss_memory_expertise`). A `vss_id_map` table bridges sqlite-vss integer
rowids to string node/expertise IDs.

#### 3.2.3 Auxiliary Tables

- **`commits` table** — maps commit SHAs to monotonic `commit_index` integers
  and wall-clock timestamps. Used by the temporal model (§4.3).
- **`audit_log` table** — records every mutation with agent-session-id,
  timestamp, operation type, and detail JSON.

### 3.3 Retrieval Engine

**Responsibility:** Given a query (task description, file path, symbol name),
return a ranked set of graph nodes within a token budget.

Three internal stages:

1. **Query Planner** (`QueryPlanner`) — selects one or more `RetrievalMode`
   values based on query shape:
   - `StructuralDrill` — BFS from a seed node along `CONTAINS`/`DEPENDS_ON`.
   - `CallTrace` — forward/backward traversal along `CALLS`/`EXTENDS`/`IMPLEMENTS`.
   - `SemanticSearch` — embed query text via a single local call to
     `IEmbeddingGenerator.GenerateEmbeddingAsync()`, then ANN lookup via
     `IVectorStore.SearchAsync("memory_embeddings", queryVector, limit, filter?, ct)`
     against pre-computed node embeddings.
   - `ExpertiseRecall` — two-path lookup: (1) follow `APPLIES_TO` edges
     inbound to code nodes already discovered by other modes; (2) embed query
     text and call `IVectorStore.SearchAsync("memory_expertise", queryVector, topK)`
     to find semantically relevant expertise entries, then follow their
     `APPLIES_TO` edges to discover additional code nodes. Results from both
     paths are merged and deduplicated.
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
exclusively (§2.2).

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
| **Write-back** | `EvolutionEngine.RecordExpertiseAsync()` | Creates `ExpertiseEntry` nodes (§4.2) linked to code nodes via `APPLIES_TO` edges. Each entry has a `Title`, `Body`, `Confidence` (0.0–1.0), `ExpertiseKind`, `Scope`, optional `Evidence`, and `SourceAgent` provenance. |
| **Consolidation** | `ConsolidationService` | Finds candidate pairs via `IVectorStore.SearchAsync("memory_expertise", entryEmbedding, topK)` (§3.2.2), verifies exact cosine ≥ 0.85 on loaded BLOB embeddings, then LLM-powered merge. Creates `SUPERSEDES` edge from merged entry to originals. Acquires write lock. |
| **Decay** | `DecayService` | Reduces expertise confidence over time and on code changes. See detailed computation model below. |
| **Community Detection** | `EvolutionEngine.DetectCommunitiesAsync()` | Louvain algorithm over `CALLS`/`SIMILAR_TO` edges → creates `BELONGS_TO_CLUSTER` edges and `Topic` summary nodes in the Semantic layer (GraphRAG pattern). |

#### 3.5.1 Decay Computation Model

Decay has **two independent mechanisms** whose effects compose. Neither
mechanism can increase confidence — it can only decrease or remain unchanged.

**(a) Event-driven decay** — triggered by `IncrementalSync` when a target
code node's `ContentHash` changes:

- **Substantive change** (non-trivial edit): `Confidence ← Confidence × StaleDecayFactor` (0.5)
- **Minor change** (whitespace/comment-only): `Confidence ← Confidence × MinorDecayFactor` (0.9)

Each `ContentHash` change triggers exactly one event-driven decay. The result
is persisted to `ExpertiseEntry.Confidence` immediately. This stored value is
the **event-adjusted confidence** — it reflects all accumulated event-driven
decays since creation.

**(b) Time-based decay** — applied periodically or on demand via
`DecayStaleExpertiseAsync`. This is an **idempotent** computation: re-running
it with the same inputs produces the same output (it does not compound).

Definitions:
- `age_days = (DateTime.UtcNow − ExpertiseEntry.CreatedAt).TotalDays` —
  always measured from the entry's creation date, not from the last decay run.
- `centrality_boost = 1.0 + CentralityBoostFactor × normalized_pagerank` —
  range [1.0, 1.5], where `normalized_pagerank` is the target code node's
  PageRank score normalized to [0, 1]. Computed from the current graph state.
- `effective_half_life = DecayHalfLifeDays × centrality_boost` — high-centrality
  code retains expertise longer (larger denominator = slower decay).
- `time_factor = 2^(−age_days / effective_half_life)` — bounded [0.0, 1.0].
  At `age_days = 0`, `time_factor = 1.0` (no reduction).
  At `age_days = effective_half_life`, `time_factor = 0.5`.

**Combined formula:**

```
final_confidence = event_adjusted_confidence × time_factor
```

Where `event_adjusted_confidence` is the stored `Confidence` value (already
incorporating all prior event-driven decays). Because `time_factor ≤ 1.0`,
the result never exceeds `event_adjusted_confidence`. Because event-driven
decays only reduce the stored value, **confidence never increases through
decay**.

**Important execution semantics:**
- Time-based decay **overwrites** `final_confidence` each run — it does not
  multiply with a previously time-decayed value. The input is always the
  durable `event_adjusted_confidence` (the stored Confidence after event
  decays), and `age_days` is always computed from `CreatedAt`. This ensures
  idempotency.
- Event-driven decay **does** mutate the stored base: each code change
  permanently reduces the stored `Confidence`. This is intentional — if code
  changes 3 times, all three event decays apply: `1.0 × 0.5 × 0.5 × 0.9 = 0.225`.
- `centrality_boost` is computed from the current graph state (dynamic). If a
  node gains more callers, its expertise decays slower going forward. This
  cannot increase confidence — it only slows the rate of decrease.

**Worked example:** An expertise entry created 1095 days ago with stored
`Confidence = 0.5` (reduced from 1.0 by one prior substantive code change),
targeting a node with `normalized_pagerank = 0.6`:

1. `centrality_boost = 1.0 + 0.5 × 0.6 = 1.3`
2. `effective_half_life = 1095 × 1.3 = 1423.5 days`
3. `time_factor = 2^(−1095 / 1423.5) ≈ 0.58`
4. `final_confidence = 0.5 × 0.58 = 0.29`

**Anti-example (incorrect):** Do NOT compute
`stored_confidence × time_factor × centrality_boost`. Placing
`centrality_boost` as an output multiplier would push final confidence
above the event-adjusted value (e.g., `0.5 × 0.58 × 1.3 = 0.377 > 0.29`),
violating the monotonic-decrease invariant. `centrality_boost` modifies
the **exponent denominator** (extends the half-life), not the output.

**Archival:** Entries whose `final_confidence` drops below
`ArchiveConfidenceFloor` (0.1) are soft-deleted via
`IMemoryGraphStore.ArchiveExpertiseAsync` (sets `archived = 1`). Archived
entries are excluded from `GetExpertiseForNodeAsync` but remain accessible
via `GetArchivedExpertiseAsync` and can be restored via
`IEvolutionEngine.RestoreArchivedExpertiseAsync`.

### 3.6 Incremental Sync

**Responsibility:** Keep the graph current without full re-extraction.

| Trigger | Mechanism |
|---|---|
| `git pull` / commit | `GitDiffDriver` computes changed file set via `git diff --name-status` |
| File save (IDE) | File-system watcher with `IncrementalSyncDebounceMs` (1,000 ms) debounce |

On trigger:

1. Register new commit SHA and `commit_index` in `commits` table.
2. Identify added/modified/deleted/renamed files via `git diff --name-status`.
3. For modified files (`M`): re-extract via `IExtractionPipeline.ExtractFilesAsync`,
   diff old vs. new nodes, upsert changes, set `UntilIndex` on removed nodes/edges.
4. For renamed files (`R`): retire nodes at old path, re-extract at new path.
5. For added files (`A`): extract and upsert with `SinceIndex` = current commit.
6. For deleted files (`D`): set `UntilIndex` on all nodes/edges rooted in that file.
7. Retire orphaned edges via `RetireEdgeAsync` individually.
8. Mark downstream `CALLS` edges as stale via `UpdateEdgeMetadataAsync(edgeId, { "Stale": true })`.
9. Re-run `SemanticIndexer` on changed nodes only (re-embed, update vector index,
   recalculate `SIMILAR_TO` edges).
10. Trigger event-driven expertise decay for expertise targeting changed code nodes.
11. Log all mutations to the audit log.

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
- **Serialized writes.** All mutations are serialized via in-process
  `SemaphoreSlim(1, 1)` (SQLite) or ACID transactions (Neo4j).
- **Consolidation lock.** `ConsolidateAsync` acquires a write-level lock to
  prevent concurrent merges.
- **Indirect knowledge sharing.** Agents share knowledge indirectly: Agent A
  writes; Agent B discovers on subsequent read. No inter-agent messaging.

### 3.8 MCP Tool Interface

The memory graph is exposed to external agents via MCP (Model Context Protocol)
tools registered in `Ast.Memory/Mcp/`.

| MCP Tool | Maps to | Description |
|---|---|---|
| `memory_query` | `IMemoryRetrievalEngine.RetrieveAsync()` | Hybrid context retrieval with configurable mode and budget |
| `memory_status` | `IMemoryGraphStore.GetMemoryStatisticsAsync()` | Graph statistics (node/edge counts by layer) |
| `memory_record_expertise` | `IEvolutionEngine.RecordExpertiseAsync()` | Write an expertise entry (title, body, kind, target) |
| `memory_sync` | `IIncrementalSync.ApplyDiffAsync()` | Trigger incremental sync |
| `memory_consolidate` | `IEvolutionEngine.ConsolidateAsync()` | Run consolidation (admin) |

### 3.9 Error Handling and Resilience

The memory layer follows a **degrade-but-proceed** pattern:

| Component | Failure mode | Degradation behavior |
|---|---|---|
| **Extraction Pipeline** | Parser throws on malformed file | Log + skip file. Partial `ExtractionResult`. |
| **Extraction Pipeline** | `IEmbeddingGenerator` unavailable | Skip embeddings and `SIMILAR_TO` edges. Structural and Call-Chain layers still populated. |
| **Retrieval Engine** | `IEmbeddingGenerator` unavailable | Skip `SemanticSearch`. Re-normalize remaining 4 weights to sum to 1.0. |
| **Retrieval Engine** | `IVectorStore` unavailable | Same as embedding unavailable — `SemanticSearch` disabled. |
| **Retrieval Engine** | `IMemoryGraphStore` unavailable | Return empty `RetrievalResult` with diagnostic error. Agent falls back to raw file reading. |
| **Incremental Sync** | `git diff` fails | Return `SyncResult` with zero changes. No mutations. |
| **Evolution Engine** | LLM unavailable during consolidation | Return `ConsolidationResult` with `MergedCount = 0`. Entries remain unconsolidated. |
| **Storage Layer** | SQLite write fails | Throw — writes are critical. `SemaphoreSlim(1,1)` prevents partial corruption. |
| **MCP Handler** | Invalid parameters | Return structured error JSON. No unhandled exceptions through MCP transport. |

**Circuit breaker for embedding service:** If the local Ollama endpoint
fails 3 consecutive calls within 60 seconds, the Retrieval Engine enters
"structural-only" mode for 5 minutes before re-probing.

### 3.10 Observability

| Category | Mechanism | Examples |
|---|---|---|
| **Structured logging** | `ILogger<T>` | Extraction/retrieval timing, file skip warnings, decay computations |
| **Metrics** | Counter/Histogram | `ast_memory_retrieval_duration_seconds`, `ast_memory_expertise_total`, `ast_memory_sync_files_processed` |
| **Health checks** | `IHealthCheck` | Graph store reachable, vector store reachable, embedding service probe |
| **Audit log** | `audit_log` table | Every mutation with session-id, timestamp, operation, detail JSON |

### 3.11 Normative Invariants

These hold at all times and are enforced by the implementation:

1. Every `HypergraphNode` has exactly one home layer (`Metadata["MemoryLayer"]`).
2. Every edge carries `Metadata["SemanticRelation"]` with a canonical name from §2.2.
3. Archived expertise is excluded from `GetExpertiseForNodeAsync` results.
4. Writes are serialized; reads are concurrent and stateless.
5. Node IDs follow the canonical format (§4.2) and are immutable once assigned.
6. `VersionRange` semantics are immutable — `SinceIndex` never changes; only
   `UntilIndex` is set (once) by retirement.
7. Confidence never increases through the decay pathway.
8. `MemoryNode` and `MemoryEdge` use composition, never inheritance from base types.

---

## 4. Data Model

This section is the **single source of truth** for all entity shapes, ID
formats, temporal semantics, and storage schemas. Sibling documents
(`tech-spec.md`, `implementation-plan.md`, `e2e-scenarios.md`) defer to this
section on any data-model conflict.

### 4.1 Composition Wrappers — MemoryNode and MemoryEdge

The memory layer does **not** subclass `HypergraphNode` or `Hyperedge` from
`Ast.Core` (`src/ast/Ast.Core/HypergraphModels.cs`). It wraps them via
composition, adding memory-specific accessors. This avoids coupling agent
memory semantics to the code-analysis type hierarchy.

**MemoryNode** — wraps `HypergraphNode`:

```csharp
public sealed class MemoryNode
{
    public HypergraphNode Inner { get; }

    // Strongly-typed accessors (delegate to Inner.Metadata)
    public string MemoryLayer   => (string)Inner.Metadata["MemoryLayer"];
    public string NodeKind      => Inner.NodeType;
    public string? ContentHash  => Inner.Metadata.TryGetValue("ContentHash", out var v) ? (string?)v : null;

    // Nullable — null for External boundary nodes (timeless stubs)
    public VersionRange? Version { get; }
}
```

**MemoryEdge** — wraps `Hyperedge`:

```csharp
public sealed class MemoryEdge
{
    public Hyperedge Inner { get; }

    // Strongly-typed accessors (delegate to Inner.Metadata)
    public string SemanticRelation => (string)Inner.Metadata["SemanticRelation"];
    public string MemoryLayer      => (string)Inner.Metadata["MemoryLayer"];

    public VersionRange Version { get; }
}
```

Both types use `sealed class` (not `record`) because they hold mutable
`Inner` references whose `Metadata` dicts are populated at construction.

### 4.2 ExpertiseEntry

Agent-authored knowledge nodes. These are **not** `HypergraphNode` instances
— they have their own dedicated table (`expertise`) because their fields
(`Confidence`, `Archived`, `Embedding`, `SourceAgent`) are core data, not
incidental metadata.

```csharp
public sealed class ExpertiseEntry
{
    public string Id              { get; init; }   // "expertise:{kind}:{hash8}"
    public ExpertiseKind Kind     { get; init; }   // Pattern | Pitfall | Preference | Insight
    public string Title           { get; init; }   // Headline
    public string Body            { get; init; }   // Detailed description
    public string TargetNodeId    { get; init; }   // Code node this applies to
    public string Scope           { get; init; }   // "exact" (default) | "subtree"
    public double Confidence      { get; set; }    // 0.0–1.0, default 1.0
    public bool Archived          { get; set; }    // Default false; set by DecayService
    public string SourceAgent     { get; init; }   // Agent that authored this entry
    public IReadOnlyList<Citation> Evidence { get; init; } // Supporting references
    public float[]? Embedding     { get; set; }    // Vector of Title + " " + Body
    public string ContentHash     { get; init; }   // SHA-256(Title + Body + TargetNodeId)
    public DateTime CreatedAt     { get; init; }
    public DateTime UpdatedAt     { get; set; }
}

public record Citation(string NodeId, string? CommitSha, string? Snippet);

public enum ExpertiseKind { Pattern, Pitfall, Preference, Insight }
```

**ID format:** `expertise:{kind}:{hash8}` where `hash8` is the first 8
characters of `SHA-256(Title + Body + TargetNodeId)`. This makes IDs
content-addressable for deduplication — identical expertise for the same
target always produces the same ID.

**Node ID formats** (all entity types):

| Entity type | ID format | Examples |
|---|---|---|
| Repo root | `"/"` | `/` |
| Workspace | `"/{workspacePath}"` | `/packages/frontend` |
| Package | `"/{relativePath}"` | `/src/ast/Ast.Core` |
| Module / File | `"/{relativePath}"` | `/src/ast/Ast.Core/HypergraphModels.cs` |
| Symbol (Class, Method, etc.) | `"/{relativePath}::{qualifiedName}"` | `/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode.GetEmbeddingText` |
| External dependency stub | `"ext:{package}:{qualifiedName}"` | `ext:System.Collections.Generic:List<T>` |
| Expertise entry | `"expertise:{kind}:{hash8}"` | `expertise:pitfall:a1b2c3d4` |
| Topic (cluster summary) | `"topic:{hash8}"` | `topic:e5f6a7b8` |

**Rules:** All in-repo node IDs start with `/`. External and expertise IDs
do not start with `/`. IDs are immutable once assigned.

### 4.3 Temporal Model — VersionRange

Every node and edge in the memory graph carries a temporal version range
that records when it existed in the codebase. The model uses **dual-axis**
time — monotonic commit indices for ordering, plus wall-clock timestamps
available via join to the `commits` table.

```csharp
public record VersionRange(
    string SinceCommit,     // SHA of the commit that introduced this node/edge
    int SinceIndex,         // Monotonic commit index (from commits table)
    string? UntilCommit,    // SHA of the commit that retired this; null = still active
    int? UntilIndex         // Commit index when retired; null = still active
);
```

**Semantics:**

- `SinceIndex ≤ target ∧ (UntilIndex = null ∨ UntilIndex > target)` → node
  is active at commit `target`.
- `UntilIndex ≠ null` → node/edge has been retired (soft-deleted).
- `SinceIndex` is set once at creation and **never changes**.
- `UntilIndex` is set at most once by `RetireNodeAsync` / `RetireEdgeAsync`.

**External boundary nodes exception:** Nodes of type `External` (e.g.,
`ext:System.Collections.Generic:List<T>`) represent out-of-repo dependencies.
They have **no VCS presence** — they are timeless stubs. Their `VersionRange`
is `null` (in `MemoryNode.Version`), and their database rows have
`since_index = NULL`. The temporal query includes them unconditionally:

```sql
WHERE (since_index IS NULL       -- External boundary nodes (always included)
   OR (since_index <= @target
       AND (until_index IS NULL OR until_index > @target)))
```

**Commits table:** Maps commit SHAs to monotonic indices and wall-clock
timestamps. Populated by `IncrementalSync` on each `ApplyDiffAsync` call.

| Column | Type | Description |
|---|---|---|
| `sha` | TEXT PK | Git commit SHA |
| `commit_index` | INTEGER UNIQUE | Monotonic index (auto-increment) |
| `timestamp` | TEXT | ISO-8601 wall-clock time of the commit |
| `message` | TEXT | First line of commit message |

### 4.4 Database Schema (SQLite)

The `SqliteGraphStore` uses the following tables. `Neo4jMemoryGraphStore`
stores equivalent data as Neo4j node/relationship properties.

**`nodes` table:**

| Column | Type | Description |
|---|---|---|
| `id` | TEXT PK | Node ID (§4.2 format) |
| `node_type` | TEXT NOT NULL | `Repo`, `Package`, `Module`, `Class`, `Method`, etc. |
| `name` | TEXT NOT NULL | Display name |
| `language` | TEXT | `C#`, `PowerShell`, or empty for structural nodes |
| `file_path` | TEXT | Relative path from repo root |
| `scope` | TEXT | Qualified scope (namespace, class) |
| `line_start` | INTEGER | Start line in source file |
| `line_end` | INTEGER | End line in source file |
| `memory_layer` | TEXT NOT NULL | `structural`, `call_chain`, `semantic` |
| `since_commit` | TEXT | Commit SHA (NULL for External nodes) |
| `since_index` | INTEGER | Commit index (NULL for External nodes) |
| `until_commit` | TEXT | Retirement commit SHA (NULL = active) |
| `until_index` | INTEGER | Retirement commit index (NULL = active) |
| `metadata` | TEXT | JSON-serialized metadata dict |

**`edges` table:**

| Column | Type | Description |
|---|---|---|
| `id` | TEXT PK | Edge ID |
| `edge_type` | TEXT NOT NULL | `HyperedgeType` enum name |
| `semantic_relation` | TEXT NOT NULL | Canonical edge name from §2.2 |
| `memory_layer` | TEXT NOT NULL | Layer tag from §2.1 |
| `source_id` | TEXT NOT NULL | Source node ID (FK → nodes) |
| `target_id` | TEXT NOT NULL | Target node ID (FK → nodes) |
| `since_commit` | TEXT | |
| `since_index` | INTEGER | |
| `until_commit` | TEXT | |
| `until_index` | INTEGER | |
| `metadata` | TEXT | JSON-serialized metadata dict |

**`expertise` table:**

| Column | Type | Description |
|---|---|---|
| `id` | TEXT PK | `expertise:{kind}:{hash8}` |
| `kind` | TEXT NOT NULL | `Pattern`, `Pitfall`, `Preference`, `Insight` |
| `title` | TEXT NOT NULL | Headline |
| `body` | TEXT NOT NULL | Detailed description |
| `target_node_id` | TEXT NOT NULL | FK → nodes.id |
| `scope` | TEXT NOT NULL | `exact` or `subtree` |
| `confidence` | REAL NOT NULL | 0.0–1.0 |
| `archived` | INTEGER NOT NULL | 0 = active, 1 = archived |
| `source_agent` | TEXT NOT NULL | Agent identifier |
| `evidence` | TEXT | JSON array of `Citation` objects |
| `embedding` | BLOB | Serialized `float[]` |
| `content_hash` | TEXT NOT NULL | SHA-256(Title + Body + TargetNodeId) |
| `created_at` | TEXT NOT NULL | ISO-8601 |
| `updated_at` | TEXT NOT NULL | ISO-8601 |

**`audit_log` table:**

| Column | Type | Description |
|---|---|---|
| `id` | INTEGER PK AUTOINCREMENT | |
| `session_id` | TEXT NOT NULL | Agent session identifier |
| `timestamp` | TEXT NOT NULL | ISO-8601 |
| `operation` | TEXT NOT NULL | e.g., `UpsertNode`, `RecordExpertise`, `Decay` |
| `detail` | TEXT | JSON-serialized operation detail |

**Indexes:**

| Index name | Table | Columns | Purpose |
|---|---|---|---|
| `idx_nodes_layer` | nodes | `memory_layer` | Filter by layer |
| `idx_nodes_type` | nodes | `node_type` | Filter by node type |
| `idx_nodes_file` | nodes | `file_path` | File-path lookups |
| `idx_nodes_since` | nodes | `since_index` | Temporal queries |
| `idx_edges_semantic` | edges | `semantic_relation` | Edge-type filtering |
| `idx_edges_layer` | edges | `memory_layer` | Layer filtering |
| `idx_edges_since` | edges | `since_index` | Temporal queries |
| `idx_expertise_target` | expertise | `target_node_id` | Target lookups |
| `idx_expertise_kind` | expertise | `kind` | Kind filtering |
| `idx_expertise_archived` | expertise | `archived` | Active/archived filter |
| `idx_audit_session` | audit_log | `session_id` | Session audit trail |

### 4.5 MemoryMetadataKeys

A `static class` in `Ast.Memory/Models/MemoryMetadataKeys.cs` defining
string constants for all curated `Metadata` dictionary keys. Prevents
typo-induced bugs and enables IDE autocomplete.

```csharp
public static class MemoryMetadataKeys
{
    public const string MemoryLayer      = "MemoryLayer";
    public const string SemanticRelation = "SemanticRelation";
    public const string ContentHash      = "ContentHash";
    public const string SinceCommit      = "SinceCommit";
    public const string SinceIndex       = "SinceIndex";
    public const string UntilCommit      = "UntilCommit";
    public const string UntilIndex       = "UntilIndex";
    public const string Summary          = "Summary";
    public const string MemberCount      = "MemberCount";
    public const string SimilarityScore  = "SimilarityScore";
    public const string OriginalEdgeType = "OriginalEdgeType";
    public const string Confidence       = "Confidence";
    public const string Stale            = "Stale";
    public const string QualifiedName    = "QualifiedName";
    public const string LastTouched      = "LastTouched";
    public const string Embedding        = "Embedding";
    public const string EmbeddingModel   = "EmbeddingModel";
    public const string Package          = "Package";
}
```

### 4.6 HyperedgeType Addition

A single new enum value `MemoryRelation` is appended to the existing
`HyperedgeType` enum in `src/ast/Ast.Core/HypergraphModels.cs` (currently
43 values at lines 12–69, bringing the total to 44). This is the **sole
modification** to any existing project.

All memory-only edges (`SIMILAR_TO`, `BELONGS_TO_CLUSTER`, `APPLIES_TO`,
`SUPERSEDES`, `DERIVED_FROM`) use `HyperedgeType.MemoryRelation` as their
`EdgeType`. The true semantic type is stored in `Metadata["SemanticRelation"]`
and used for all query filtering (§2.2).

---

## 5. Interfaces

This section defines all interface contracts between components. Each
interface lists its namespace, methods, and the component that owns its
implementation.

### 5.1 IMemoryGraphStore

**Namespace:** `Ast.Memory.Interfaces`
**Extends:** `IGraphStore` (from `Ast.GraphDb`, `src/ast/Ast.GraphDb/IGraphStore.cs` — 17 methods including `CreateNodeAsync`, `GetNodeAsync`, `GetEdgesForNodeAsync`, `GetCallersAsync`, `GetCalleesAsync`, `FindPathsAsync`, `AnalyzeImpactAsync`, `GetStatisticsAsync`, `IsAvailableAsync`, etc.)
**Implementations:** `SqliteGraphStore` (local), `Neo4jMemoryGraphStore` (team)

```csharp
public interface IMemoryGraphStore : IGraphStore
{
    // Layer-filtered queries with temporal support
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer = null,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default);

    // Temporal-aware upsert and retirement
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken ct = default);

    Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        CancellationToken ct = default);

    Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        CancellationToken ct = default);

    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> updates,
        CancellationToken ct = default);

    // Expertise CRUD
    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken ct = default);

    Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken ct = default);

    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null,
        int limit = 50,
        CancellationToken ct = default);

    // Statistics and audit
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken ct = default);

    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default);
}
```

**Key behaviors:**

- `GetExpertiseForNodeAsync` excludes entries where `archived = true`.
- `ArchiveExpertiseAsync` sets `archived = true` (soft-delete); `GetArchivedExpertiseAsync` returns only archived entries.
- `UpsertNodeAsync` serializes writes via `SemaphoreSlim(1,1)` (SQLite) or ACID transactions (Neo4j) — see §3.7.
- `RetireNodeAsync` cascades retirement to all edges connected to the node.

### 5.2 IMemoryRetrievalEngine

**Namespace:** `Ast.Memory.Interfaces`
**Implementation:** `MemoryRetrievalEngine` (`Ast.Memory/Retrieval/`)
**Dependencies:** `IMemoryGraphStore`, `IVectorStore` (from `Ast.VectorDb`), `IEmbeddingGenerator` (from `Ast.Embedding`)

```csharp
public interface IMemoryRetrievalEngine
{
    Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken ct = default);
}

// Request/Response types:

public record RetrievalRequest(
    string? QueryText,
    IReadOnlyList<string>? SeedNodeIds,
    RetrievalMode Mode = RetrievalMode.Hybrid,
    int TokenBudget = 8_000,
    int? AtCommitIndex = null,
    ScoringWeights? Weights = null
);

public enum RetrievalMode
{
    StructuralDrill,
    CallTrace,
    SemanticSearch,
    ExpertiseRecall,
    Hybrid
}

public record RetrievalResult(
    IReadOnlyList<ScoredMemoryNode> Nodes,
    string ContextMarkdown,
    int TokensUsed,
    IReadOnlyList<RetrievalMode> ModesExecuted,
    IReadOnlyList<string> Diagnostics
);

public record ScoredMemoryNode(
    MemoryNode Node,
    double CompositeScore,
    double StructuralScore,
    double SemanticScore,
    double CentralityScore,
    double RecencyScore,
    double ExpertiseScore
);
```

### 5.3 IEvolutionEngine

**Namespace:** `Ast.Memory.Interfaces`
**Implementation:** `EvolutionEngine` (`Ast.Memory/Evolution/`)
**Dependencies:** `IMemoryGraphStore`, `IVectorStore`, `IEmbeddingGenerator`

```csharp
public interface IEvolutionEngine
{
    /// Write an expertise entry and link it to a code node via APPLIES_TO edge.
    Task<ExpertiseEntry> RecordExpertiseAsync(
        string targetNodeId,
        ExpertiseKind kind,
        string title,
        string body,
        string sourceAgent,
        string scope = "exact",
        IReadOnlyList<Citation>? evidence = null,
        CancellationToken ct = default);

    /// LLM-powered merge of semantically similar expertise entries.
    /// Acquires write-level lock. Creates SUPERSEDES edges from merged
    /// entry to originals.
    Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationOptions options,
        CancellationToken ct = default);

    /// Apply time-based and event-driven decay to expertise entries.
    /// Archives entries below ArchiveConfidenceFloor (0.1).
    Task<DecayResult> DecayStaleExpertiseAsync(
        DecayOptions? options = null,
        CancellationToken ct = default);

    /// Restore a previously archived expertise entry with new confidence.
    Task RestoreArchivedExpertiseAsync(
        string expertiseId,
        double restoredConfidence = 0.5,
        CancellationToken ct = default);

    /// Louvain community detection → Topic nodes + BELONGS_TO_CLUSTER edges.
    Task<CommunityResult> DetectCommunitiesAsync(
        CancellationToken ct = default);
}

// Supporting types:

public record ConsolidationOptions(
    double SimilarityThreshold = 0.85,
    int MaxMergesPerRun = 50
);

public record ConsolidationResult(
    int CandidatesPaired,
    int MergedCount,
    IReadOnlyList<string> MergedEntryIds
);

public record DecayOptions(
    int HalfLifeDays = 1095,
    double CentralityBoostFactor = 0.5,
    double ArchiveConfidenceFloor = 0.1
);

public record DecayResult(
    int EntriesEvaluated,
    int EntriesDecayed,
    int EntriesArchived
);

public record CommunityResult(
    int CommunitiesDetected,
    int TopicNodesCreated,
    int ClusterEdgesCreated
);
```

### 5.4 IExtractionPipeline

**Namespace:** `Ast.Memory.Interfaces`
**Implementation:** Orchestrates `StructureBuilder` → `CallChainAnalyzer` → `SemanticIndexer`
**Dependencies:** `IMemoryGraphStore`, `IVectorStore`, `IEmbeddingGenerator`, existing parsers from `Ast.Csharp` and `Ast.PowerShell`

```csharp
public interface IExtractionPipeline
{
    /// Full extraction from repo root (or scoped subtree).
    Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default);

    /// Incremental extraction of specific files (used by IncrementalSync).
    Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default);
}

public record ExtractionOptions(
    IReadOnlyList<string> Languages,   // ["C#", "PowerShell"]
    string? ScopeSubtree = null,       // e.g., "src/ast/" — null = entire repo
    string? CommitSha = null,
    int? CommitIndex = null
);

public record ExtractionResult(
    int NodesCreated,
    int EdgesCreated,
    int EmbeddingsGenerated,
    int FilesSkipped,
    IReadOnlyList<string> Errors
);
```

### 5.5 IIncrementalSync

**Namespace:** `Ast.Memory.Interfaces`
**Implementation:** `IncrementalSync` (`Ast.Memory/Sync/`)
**Dependencies:** `IExtractionPipeline`, `IMemoryGraphStore`, `IEvolutionEngine`

```csharp
public interface IIncrementalSync
{
    /// Compute git diff between two commits and update the graph.
    Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default);
}

public record SyncResult(
    int FilesChanged,
    int NodesAdded,
    int NodesRetired,
    int EdgesAdded,
    int EdgesRetired,
    int ExpertiseDecayed,
    TimeSpan Duration
);
```

### 5.6 Interface Dependency Graph

The interfaces form a clear dependency DAG with no cycles:

```
IEmbeddingGenerator (Ast.Embedding — existing, unmodified)
IVectorStore        (Ast.VectorDb  — existing, unmodified)
IGraphStore         (Ast.GraphDb   — existing, unmodified)
        │
        ▼
IMemoryGraphStore   (extends IGraphStore)
        │
        ├──────────────────┬──────────────────┐
        ▼                  ▼                  ▼
IExtractionPipeline  IMemoryRetrievalEngine  IEvolutionEngine
        │                                     │
        ▼                                     │
IIncrementalSync ─────────────────────────────┘
```

All arrows flow downward. No component holds a reference to a component
above it. `IMemoryGraphStore` is the single integration seam — swapping
`SqliteGraphStore` for `Neo4jMemoryGraphStore` changes zero lines in any
consuming component.

---

## 6. End-to-End Sequence Flows

This section describes the primary scenarios as step-by-step flows
through the components and interfaces defined above.

### 6.1 Full Repository Extraction

**Trigger:** CLI command `ast memory extract --repo . --scope src/ast/`
or programmatic call to `IExtractionPipeline.ExtractAsync()`.

```
Agent/CLI
  │
  ▼
IExtractionPipeline.ExtractAsync(repoRoot, options)
  │
  ├─(1)─ Register HEAD commit in commits table
  │       └─ IMemoryGraphStore.LogAuditAsync(session, "RegisterCommit", {sha, index})
  │
  ├─(2)─ StructureBuilder
  │       ├─ Read HypergraphData from Ast.Csharp / Ast.PowerShell parsers
  │       ├─ Emit Structural-layer nodes (Repo, Package, Module, Class, Method, ...)
  │       │   └─ IMemoryGraphStore.UpsertNodeAsync(node, versionRange) × N
  │       └─ Emit CONTAINS and DEPENDS_ON edges
  │           └─ IMemoryGraphStore.CreateEdgeAsync(edge) × M
  │
  ├─(3)─ CallChainAnalyzer
  │       ├─ Read Hyperedge entries of type MethodInvocation, ConstructorInvocation, etc.
  │       └─ Emit CALLS, EXTENDS, IMPLEMENTS, REFERENCES edges
  │           └─ IMemoryGraphStore.CreateEdgeAsync(edge) × K
  │
  └─(4)─ SemanticIndexer
          ├─ Batch all nodes through IEmbeddingGenerator.GenerateBatchEmbeddingsAsync()
          ├─ Store embedding in Metadata["Embedding"] via UpsertNodeAsync
          ├─ Upsert VectorPoint into IVectorStore ("memory_embeddings" collection)
          ├─ Compute pairwise cosine similarity
          └─ Create SIMILAR_TO edges where cosine ≥ 0.85 (SimilarityThreshold)
              └─ IMemoryGraphStore.CreateEdgeAsync(edge) × J

Return: ExtractionResult { NodesCreated, EdgesCreated, EmbeddingsGenerated, ... }
```

**Error handling:** If `IEmbeddingGenerator` is unavailable, step (4) is
skipped entirely (Structural and Call-Chain layers are still populated).
If an individual file fails to parse, it is logged and skipped; the
`ExtractionResult.FilesSkipped` count is incremented and the error message
is added to `ExtractionResult.Errors`.

### 6.2 Hybrid Context Retrieval

**Trigger:** MCP tool `memory_query` or programmatic call to
`IMemoryRetrievalEngine.RetrieveAsync()`.

```
Agent
  │
  ▼
IMemoryRetrievalEngine.RetrieveAsync(request)
  │
  ├─(1)─ QueryPlanner: analyze request → select RetrievalMode(s)
  │       ├─ SeedNodeIds present?     → include StructuralDrill + CallTrace
  │       ├─ QueryText present?       → include SemanticSearch
  │       ├─ Mode = Hybrid?           → include all four modes
  │       └─ Output: Set<RetrievalMode>
  │
  ├─(2)─ Execute selected modes in parallel:
  │       │
  │       ├─ StructuralDrill:
  │       │   └─ BFS from seed along CONTAINS/DEPENDS_ON edges
  │       │       └─ IMemoryGraphStore.GetEdgesForNodeAsync(seedId, [ClassDefinition, ...])
  │       │
  │       ├─ CallTrace:
  │       │   └─ Forward/backward traversal along CALLS/EXTENDS/IMPLEMENTS
  │       │       ├─ IMemoryGraphStore.GetCallersAsync(nodeId, depth=2)
  │       │       └─ IMemoryGraphStore.GetCalleesAsync(nodeId, depth=2)
  │       │
  │       ├─ SemanticSearch:
  │       │   ├─ IEmbeddingGenerator.GenerateEmbeddingAsync(queryText)
  │       │   └─ IVectorStore.SearchAsync("memory_embeddings", queryVector, topK, filter?)
  │       │       └─ Returns: IReadOnlyList<VectorSearchResult>
  │       │
  │       └─ ExpertiseRecall:
  │           ├─ Path A: IMemoryGraphStore.GetExpertiseForNodeAsync(discoveredNodeId)
  │           └─ Path B: IVectorStore.SearchAsync("memory_expertise", queryVector, topK)
  │               └─ Follow APPLIES_TO edges to discover additional code nodes
  │
  ├─(3)─ Ranker: merge all candidates → score with 5-signal model
  │       ├─ structural  = inverse BFS distance from seed (weight 0.30)
  │       ├─ semantic    = cosine similarity of query embedding to node (weight 0.25)
  │       ├─ centrality  = normalized fan-in PageRank (weight 0.20)
  │       ├─ recency     = commit-index proximity to HEAD (weight 0.15)
  │       └─ expertise   = max Confidence of APPLIES_TO expertise (weight 0.10)
  │       Output: List<ScoredMemoryNode> sorted by descending CompositeScore
  │
  ├─(4)─ BudgetController: enforce token budget
  │       ├─ Walk ranked list top-down, accumulating token count
  │       ├─ When budget tight: collapse children into parent summaries (HiAgent)
  │       ├─ Dedup same code span reached via multiple paths
  │       └─ Output: trimmed List<ScoredMemoryNode> within budget
  │
  └─(5)─ ContextAssembler: serialize to Markdown
          ├─ ## Structure    — containment hierarchy
          ├─ ## Call Graph   — caller/callee chains
          ├─ ## Related Code — semantically similar snippets
          └─ ## Known Expertise — patterns, pitfalls

Return: RetrievalResult { Nodes, ContextMarkdown, TokensUsed, ModesExecuted, Diagnostics }
```

**Degradation:** If `IEmbeddingGenerator` is unavailable at query time,
SemanticSearch is skipped; the remaining 4 signals are re-normalized
(structural=0.40, centrality=0.27, recency=0.20, expertise=0.13).
If `IMemoryGraphStore` is unavailable, return empty `RetrievalResult` with
a diagnostic error — the agent falls back to raw file reading.

### 6.3 Expertise Write-Back

**Trigger:** Agent calls MCP tool `memory_record_expertise` or programmatic
call to `IEvolutionEngine.RecordExpertiseAsync()`.

```
Agent
  │
  ▼
IEvolutionEngine.RecordExpertiseAsync(targetNodeId, kind, title, body, sourceAgent, scope?, evidence?)
  │
  ├─(1)─ Validate targetNodeId exists in graph
  │       └─ IMemoryGraphStore.GetNodeAsync(targetNodeId)
  │           └─ If null → throw ArgumentException
  │
  ├─(2)─ Compute ID: "expertise:{kind}:{SHA-256(title+body+targetNodeId)[0:8]}"
  │
  ├─(3)─ Compute embedding: IEmbeddingGenerator.GenerateEmbeddingAsync(title + " " + body)
  │
  ├─(4)─ Create ExpertiseEntry with Confidence=1.0, Archived=false
  │
  ├─(5)─ Persist expertise
  │       └─ IMemoryGraphStore.UpsertExpertiseAsync(entry)
  │
  ├─(6)─ Upsert vector for expertise
  │       └─ IVectorStore.UpsertAsync("memory_expertise",
  │           [VectorPoint { Id=entryId, Vector=embedding, Payload={Kind, TargetNodeId} }])
  │
  ├─(7)─ Create APPLIES_TO edge: expertise → target code node
  │       └─ IMemoryGraphStore.CreateEdgeAsync(Hyperedge {
  │             EdgeType = MemoryRelation,
  │             Metadata = { SemanticRelation="APPLIES_TO", MemoryLayer="expertise" },
  │             Roles = { expertise=entryId, target=targetNodeId }
  │           })
  │
  └─(8)─ Audit log
          └─ IMemoryGraphStore.LogAuditAsync(session, "RecordExpertise", {entryId, targetNodeId})

Return: ExpertiseEntry (the created entry)
```

### 6.4 Incremental Sync (git pull / commit)

**Trigger:** `git pull` or commit detected by file-system watcher; calls
`IIncrementalSync.ApplyDiffAsync()`.

```
Git event
  │
  ▼
IIncrementalSync.ApplyDiffAsync(repoRoot, fromCommit, toCommit)
  │
  ├─(1)─ Register toCommit in commits table (new commit_index)
  │
  ├─(2)─ Run: git diff --name-status fromCommit toCommit
  │       └─ Parse output → List<(Status, FilePath)>
  │           Status: A (added), M (modified), D (deleted), R (renamed)
  │
  ├─(3)─ Process each file by status:
  │       │
  │       ├─ A (added):
  │       │   └─ IExtractionPipeline.ExtractFilesAsync(repoRoot, [path], options)
  │       │       └─ New nodes/edges with SinceIndex = current commit_index
  │       │
  │       ├─ M (modified):
  │       │   ├─ IExtractionPipeline.ExtractFilesAsync(repoRoot, [path], options)
  │       │   ├─ Diff old vs new nodes; upsert changes
  │       │   ├─ Set UntilIndex on removed nodes/edges via RetireNodeAsync/RetireEdgeAsync
  │       │   └─ Mark downstream CALLS edges as stale:
  │       │       └─ IMemoryGraphStore.UpdateEdgeMetadataAsync(edgeId, {"Stale": true})
  │       │
  │       ├─ D (deleted):
  │       │   └─ IMemoryGraphStore.RetireNodeAsync(nodeId, untilCommitIndex) for all
  │       │       nodes rooted in file (cascades to edges)
  │       │
  │       └─ R (renamed):
  │           ├─ Retire old-path nodes via RetireNodeAsync
  │           └─ Extract new-path nodes via ExtractFilesAsync
  │
  ├─(4)─ Re-embed changed nodes via SemanticIndexer
  │       ├─ IEmbeddingGenerator.GenerateBatchEmbeddingsAsync(changedNodeTexts)
  │       ├─ Update IVectorStore ("memory_embeddings")
  │       └─ Recalculate SIMILAR_TO edges for changed nodes
  │
  ├─(5)─ Event-driven expertise decay
  │       ├─ For each expertise targeting a changed code node:
  │       │   ├─ If ContentHash changed substantively:
  │       │   │   └─ Confidence ← Confidence × StaleDecayFactor (0.5)
  │       │   └─ If change is whitespace/comment-only:
  │       │       └─ Confidence ← Confidence × MinorDecayFactor (0.9)
  │       └─ IMemoryGraphStore.UpsertExpertiseAsync(updated entry)
  │
  └─(6)─ Audit log all mutations

Return: SyncResult { FilesChanged, NodesAdded, NodesRetired, ..., Duration }
```

### 6.5 Cross-Session Learning (Consolidation + Decay)

**Trigger:** Periodic admin command via MCP tool `memory_consolidate` or
CLI `ast memory consolidate`, or programmatic call to
`IEvolutionEngine.ConsolidateAsync()` + `DecayStaleExpertiseAsync()`.

```
Admin / Scheduled job
  │
  ▼
IEvolutionEngine.ConsolidateAsync(options)
  │
  ├─(1)─ Acquire write-level lock (SemaphoreSlim for SQLite)
  │
  ├─(2)─ For each active ExpertiseEntry:
  │       ├─ IVectorStore.SearchAsync("memory_expertise", entry.Embedding, topK=10)
  │       │   └─ Returns candidate pairs with cosine ≥ SimilarityThreshold (0.85)
  │       ├─ Verify exact cosine on loaded BLOB embeddings (ANN is approximate)
  │       ├─ If confirmed pair found:
  │       │   ├─ LLM-merge: combine Title + Body of both entries
  │       │   ├─ Create merged ExpertiseEntry with Confidence = max(a, b)
  │       │   ├─ IMemoryGraphStore.UpsertExpertiseAsync(merged)
  │       │   ├─ Create SUPERSEDES edges: merged → original_a, merged → original_b
  │       │   └─ Archive original entries via ArchiveExpertiseAsync
  │       └─ Stop after MaxMergesPerRun (default 50)
  │
  └─(3)─ Release write lock

Return: ConsolidationResult { CandidatesPaired, MergedCount, MergedEntryIds }

──────────────────────────────────────────────────────

IEvolutionEngine.DecayStaleExpertiseAsync(options)
  │
  ├─(1)─ Load all active (non-archived) expertise entries
  │
  ├─(2)─ For each entry:
  │       ├─ Compute age_days = (UtcNow − CreatedAt).TotalDays
  │       ├─ Look up target node's normalized PageRank → centrality_boost
  │       │   └─ centrality_boost = 1.0 + CentralityBoostFactor × normalized_pagerank
  │       ├─ effective_half_life = DecayHalfLifeDays × centrality_boost
  │       ├─ time_factor = 2^(−age_days / effective_half_life)
  │       ├─ final_confidence = stored_confidence × time_factor
  │       │   (stored_confidence already reflects event-driven decays — §3.5.1)
  │       ├─ If final_confidence < ArchiveConfidenceFloor (0.1):
  │       │   └─ IMemoryGraphStore.ArchiveExpertiseAsync(entryId)
  │       └─ Else: update entry.Confidence = final_confidence
  │           └─ IMemoryGraphStore.UpsertExpertiseAsync(entry)
  │
  └─(3)─ Audit log

Return: DecayResult { EntriesEvaluated, EntriesDecayed, EntriesArchived }
```

### 6.6 MCP Tool Dispatch

All MCP tools delegate to the interfaces above. The dispatch is
straightforward routing in `Ast.Memory/Mcp/MemoryMcpHandler.cs`:

| MCP Tool | Handler method | Interface call |
|---|---|---|
| `memory_query` | `HandleQueryAsync` | `IMemoryRetrievalEngine.RetrieveAsync(request)` |
| `memory_status` | `HandleStatusAsync` | `IMemoryGraphStore.GetMemoryStatisticsAsync()` |
| `memory_record_expertise` | `HandleRecordExpertiseAsync` | `IEvolutionEngine.RecordExpertiseAsync(...)` |
| `memory_sync` | `HandleSyncAsync` | `IIncrementalSync.ApplyDiffAsync(...)` |
| `memory_consolidate` | `HandleConsolidateAsync` | `IEvolutionEngine.ConsolidateAsync(options)` |

Each handler performs JSON parameter validation, calls the interface method,
and returns a structured JSON response. Errors return structured error JSON
with `error` field — no unhandled exceptions propagate through the MCP
transport.

---

## 7. Component Interaction Summary

This matrix shows which component calls which interface, providing a
quick cross-reference for the sequence flows in §6.

| Caller | IMemoryGraphStore | IVectorStore | IEmbeddingGenerator | IExtractionPipeline | IMemoryRetrievalEngine | IEvolutionEngine | IIncrementalSync |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **StructureBuilder** | ✓ | | | | | | |
| **CallChainAnalyzer** | ✓ | | | | | | |
| **SemanticIndexer** | ✓ | ✓ | ✓ | | | | |
| **MemoryRetrievalEngine** | ✓ | ✓ | ✓ | | | | |
| **EvolutionEngine** | ✓ | ✓ | ✓ | | | | |
| **IncrementalSync** | ✓ | | | ✓ | | ✓ | |
| **MemoryMcpHandler** | ✓ | | | | ✓ | ✓ | ✓ |
| **MemoryCommandModule** | | | | ✓ | ✓ | ✓ | ✓ |

---

## 8. Cross-Document Alignment

This section catalogues reconciliation status with sibling documents.
`architecture.md` is the canonical authority for all naming, data-model
shapes, formulas, constants, and interface signatures.

### 8.1 Alignment Conclusions

| ID | Topic | Resolution |
|---|---|---|
| A-1 | Layer names (`structural`, `call_chain`, `semantic`, `expertise`) | Canonical in §2.1. All siblings aligned. |
| A-2 | Edge taxonomy (12 canonical edges) | Canonical in §2.2. All siblings use canonical names. |
| A-3 | Retired names (`IMPORTS`, `CALLED_BY`, etc.) | Canonical in §2.3. Siblings corrected in their latest iterations. |
| A-4 | Constants and thresholds | Canonical in §2.4. Siblings reference §2.4 values. |
| A-5 | Node ID format (`/path::symbol`) | Canonical in §4.2. Siblings adopted in latest iterations. |
| A-6 | ExpertiseEntry `Title` + `Body` (not single `Content`) | Canonical in §4.2. e2e-scenarios corrected (E2E-10). |
| A-7 | `RecordExpertiseAsync` signature | Canonical in §5.3. e2e-scenarios aligned. |
| A-8 | `ArchiveExpertiseAsync` on `IMemoryGraphStore` | Canonical in §5.1. Implementation-plan Stage 1.3 aligned. |
| A-9 | `RestoreArchivedExpertiseAsync` on `IEvolutionEngine` | Canonical in §5.3. |
| A-10 | Decay formula: `final = event_adjusted × time_factor` | Canonical in §3.5.1. tech-spec defers to architecture.md. |
| A-11 | External-node temporal carve-out (NULL `since_index`) | Canonical in §4.3. Siblings adopted. |
| A-12 | MemoryNode/MemoryEdge use composition, not inheritance | Canonical in §4.1. Implementation-plan Stage 1.1 aligned. |

### 8.2 Implementation-Plan Divergences

The implementation-plan (latest iteration) contains naming and data-model
items that defer to this document. Key resolved divergences:

- **S-1:** `RetireEdgeAsync` — added to `IMemoryGraphStore` (§5.1).
- **S-2:** `ArchiveExpertiseAsync` — confirmed on `IMemoryGraphStore` (§5.1).
- **S-3:** `expertise.archived` column — confirmed in schema (§4.4).
- **S-4:** Scoring weights — implementation-plan references §2.4 values.
- **S-5:** `ScoringWeights` record — defined in §5.2 via `RetrievalRequest`.
- **S-6:** `UpdateEdgeMetadataAsync` — added to `IMemoryGraphStore` (§5.1).

### 8.3 E2E-Scenarios Divergences

The e2e-scenarios document (latest iteration) corrected 10 divergence
items (E2E-1 through E2E-10):

- E2E-1/2/3/4: Presentation aliases replaced with canonical `CALLS` edges.
- E2E-5/8: Out-of-scope scenarios removed.
- E2E-6: Unqualified IDs replaced with canonical `"/{path}::{name}"` format.
- E2E-7: `ext::` double-colon corrected to `ext:` single-colon prefix.
- E2E-9: Conflict errors rewritten as write-serialization test.
- E2E-10: Single `Content` field split into `Title` + `Body`.

### 8.4 Resolution Status

All catalogued divergences (A-1 through A-12, S-1 through S-6, E2E-1
through E2E-10) are resolved in the latest iterations of all sibling
documents. No open alignment issues remain.