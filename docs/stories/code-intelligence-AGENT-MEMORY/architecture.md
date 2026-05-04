# Agent Memory Architecture

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 pts
>
> **Status:** Iteration 18
>
> This document is the **canonical authority** for component boundaries,
> data model, edge/layer naming, ID format, interface signatures, and
> concurrency semantics. When sibling documents
> ([tech-spec.md](tech-spec.md), [implementation-plan.md](implementation-plan.md),
> [e2e-scenarios.md](e2e-scenarios.md)) use different names, formats, or
> thresholds, **this document governs**. Sibling documents own their
> respective scopes (problem statement, work-item phasing, Gherkin
> scenarios) and are not duplicated here.

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

The system builds on the Code Intelligence stack in `src/ast/` (24 `.csproj`
projects, 13 test projects — verified via directory listing). **Two existing
projects receive additive-only changes:**

1. **`Ast.Core`** (`src/ast/Ast.Core/HypergraphModels.cs`) — one new enum
   value `HyperedgeType.MemoryRelation` appended to the existing 44-value
   `HyperedgeType` enum (lines 12–69).
2. **`Ast.Cli`** (`src/ast/Ast.Cli/Program.cs`) — additive
   `config.AddBranch("memory", ...)` block appended after the existing 7
   `config.AddCommand<T>()` registrations (lines 17–41). A project reference
   to `Ast.Memory` is added to `Ast.Cli.csproj`.

All new code ships as **`Ast.Memory`** (+ `Ast.Memory.UnitTests` +
`Ast.Memory.IntegrationTests`). Existing projects are referenced as project
dependencies, never mutated beyond the two additive changes above.

| Existing project | Key types (verified source paths) | How memory layer uses it |
|---|---|---|
| **Ast.Core** | `HypergraphNode`, `Hyperedge`, `HyperedgeType` (44 values) — `HypergraphModels.cs`; `RepositoryProcessor` — `RepositoryProcessor.cs` | Reference types via `Metadata` dict. One additive enum value `MemoryRelation`. |
| **Ast.GraphDb** | `IGraphStore : IAsyncDisposable, IDisposable` — `IGraphStore.cs`; `Neo4jGraphStore` — `Neo4jGraphStore.cs`; `NodeFilter`, `GraphPath`, `NodeWithDepth` — `GraphModels.cs` | `Ast.Memory` defines `IMemoryGraphStore` extending `IGraphStore` in its own project. Zero files in `Ast.GraphDb/` modified. |
| **Ast.Embedding** | `IEmbeddingGenerator : IDisposable` — `IEmbeddingGenerator.cs`; `CodeEmbeddingService` — `CodeEmbeddingService.cs`; `EmbeddingGeneratorOptions.ModelName` defaults to `"nomic-embed-text"` — `EmbeddingModels.cs` | Reuse directly. `Ast.Memory` calls `GenerateBatchEmbeddingsAsync()`. |
| **Ast.Query** | `ICodeQuery` — `ICodeQuery.cs`; `HybridQueryService` — `HybridQueryService.cs`; `ScoredNode` — `QueryModels.cs` | Reference only. `Ast.Memory` defines `IMemoryRetrievalEngine` that internally composes `ICodeQuery`. |
| **Ast.VectorDb** | `IVectorStore : IDisposable` — `IVectorStore.cs`; `QdrantVectorStore` — `QdrantVectorStore.cs` | Reference only. Reuse for team mode; `Ast.Memory` adds in-process HNSW adapter for local. |
| **Ast.Lsp** | LSP integration — `src/ast/Ast.Lsp/` | Optional enrichment — pipeline never blocks on LSP. No modifications. |
| **CodeIntelligence** | 27 analyzers in `Analyzers/`, 4 detectors in `Detectors/`, `RelationshipGraphBuilder` in `Graph/` | Consume as call-chain / structural edge source. No modifications. |
| **Ast.Cli** | CLI host — `Program.cs` uses Spectre.Console.Cli with manual `AddCommand<T>()` registration (7 commands: parse, enrich, embed, index, query, pipeline, stats). | Additive: `config.AddBranch("memory", ...)` + project ref to `Ast.Memory`. |

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
| **Structural** | `structural` | Containment: Repo → Workspace → Package → Module → Class → Function | `CONTAINS`, `DEPENDS_ON` |
| **Call-Chain** | `call_chain` | Invocations and type relationships between symbols | `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` |
| **Semantic** | `semantic` | Embedding-similarity clusters and topic summaries | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` |
| **Expertise** | `expertise` | Agent-authored knowledge nodes | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` |

Temporal versioning is **orthogonal** — it applies to nodes/edges on every
layer via `VersionRange` (§4.3). It is not a separate layer.

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

**Scoring weights** (used by the Ranker, sum = 1.00):

| Signal | Default weight | Description |
|---|---|---|
| `structural` | 0.30 | Inverse BFS distance from seed (Structural layer) — bounded [0,1] |
| `semantic` | 0.25 | Cosine similarity of query embedding to node embedding |
| `centrality` | 0.20 | Normalized fan-in from `CALLS` edges (PageRank-light) |
| `recency` | 0.15 | `1.0 − (HEAD_idx − node_sinceIdx) / max_distance`, clamped [0,1] |
| `expertise` | 0.10 | Max `Confidence` of any `APPLIES_TO` edge targeting the node; 0.0 when none |