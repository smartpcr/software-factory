# Agent Memory Architecture

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 pts
>
> **Status:** Iteration 16
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

The memory graph is scoped **per-repository**, not per-agent. Multiple agent
sessions (e.g., Copilot in VS Code, Cursor, a CI bot) may access the same
graph concurrently. The interaction model is:

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

This design is intentionally minimal. G-Memory-style hierarchical sharing
across agent swarms is architecturally interesting but deferred.

### 1.3 Existing Infrastructure (`src/ast/`)

The system builds on the Code Intelligence stack in `src/ast/` (24 `.csproj`
projects, 13 test projects — verified via directory listing). **Two existing
projects receive additive-only changes:**

1. **`Ast.Core`** (`src/ast/Ast.Core/HypergraphModels.cs`) — one new enum
   value `HyperedgeType.MemoryRelation` appended to the existing 44-value
   `HyperedgeType` enum (lines 12–69).
2. **`Ast.Cli`** (`src/ast/Ast.Cli/Program.cs`) — additive
   `config.AddBranch("memory", ...)` block appended after the existing 7
   `config.AddCommand<T>()` registrations (lines 17–44). A project reference
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

---

## 3. Components & Responsibilities

### 3.0 Project Layout

All new code ships as **one new project** (`Ast.Memory`) plus test projects,
following the `src/ast/Ast.<Name>` convention. Internal namespaces provide
logical separation. **Two existing projects receive additive-only changes:**

1. `Ast.Core` — one new enum value `HyperedgeType.MemoryRelation` in
   `src/ast/Ast.Core/HypergraphModels.cs`.
2. `Ast.Cli` — additive `config.AddBranch("memory", ...)` lines in
   `src/ast/Ast.Cli/Program.cs` to register memory sub-commands. `Ast.Cli`
   uses Spectre.Console.Cli with manual `config.AddCommand<T>()` registration
   (7 existing commands: parse, enrich, embed, index, query, pipeline, stats).
   The memory branch adds sub-commands under the `memory` prefix without
   altering existing command registrations.

All new interfaces (`IMemoryGraphStore`, `IMemoryRetrievalEngine`,
`IEvolutionEngine`, `IExtractionPipeline`, `IIncrementalSync`) are defined
**within** `Ast.Memory`. They reference types from existing projects
(`HypergraphNode`, `Hyperedge`, `IGraphStore`, `IEmbeddingGenerator`,
`IVectorStore`) via project references but never alter those projects.

```
src/ast/
  Ast.Memory/                     ← NEW project
    Ast.Memory.csproj             ← refs: Ast.Core, Ast.GraphDb, Ast.Embedding, Ast.VectorDb, Ast.Query
    Interfaces/                   ← IMemoryGraphStore, IMemoryRetrievalEngine, IEvolutionEngine, IExtractionPipeline, IIncrementalSync
    Models/                       ← MemoryNode, MemoryEdge, ExpertiseEntry, Enums, MemoryMetadataKeys
    Extraction/                   ← StructureBuilder, CallChainAnalyzer, SemanticIndexer, FactNormalizer
    Context/                      ← ContextAssembler, BudgetController
    Evolution/                    ← EvolutionEngine, ConsolidationService, DecayService
    Retrieval/                    ← MemoryRetrievalEngine, QueryPlanner, Ranker, ScoringWeights
    Sync/                         ← IncrementalSync, GitDiffDriver
    Storage/                      ← SqliteGraphStore (implements IMemoryGraphStore)
    Schema/                       ← AnnotationKeyRegistry
    Mcp/                          ← MemoryMcpToolRegistration, MemoryMcpHandler, McpSchemas
    Cli/                          ← MemoryOnboardCommand, MemorySyncCommand, etc. (Spectre.Console.Cli command classes)
    ServiceCollectionExtensions.cs
  Ast.Memory.UnitTests/           ← NEW
  Ast.Memory.IntegrationTests/    ← NEW
  Ast.Core/                       ← ADDITIVE: one enum value HyperedgeType.MemoryRelation
  Ast.Cli/                        ← ADDITIVE: config.AddBranch("memory", ...) in Program.cs + project ref to Ast.Memory
  (all other existing projects unchanged)
```

### 3.0.1 Component Diagram

```
┌────────────────────────────────────────────────────────────────────────┐
│                          Agent Memory System                          │
│                                                                       │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────────────────┐  │
│  │  Extraction   │──▶│  Graph Store  │◀──│  Retrieval Engine        │  │
│  │  Pipeline     │   │  (4 layers)  │   │  (planner + ranker)      │  │
│  └──────┬───────┘   └──────┬───────┘   └────────────┬─────────────┘  │
│         │                  │                         │                │
│         │           ┌──────┴───────┐          ┌──────┴───────┐       │
│         │           │  Evolution   │          │  Context      │       │
│         │           │  Engine      │          │  Assembler    │       │
│         │           └──────────────┘          └──────────────┘       │
│         │                                                            │
│  ┌──────┴───────────────────────────────────────────────────────┐    │
│  │  Incremental Sync (watches repo, triggers extraction)        │    │
│  └──────────────────────────────────────────────────────────────┘    │
└────────────────────────────────────────────────────────────────────────┘
         ▲                                          │
         │  git events / file-system watch          │  context block
         │                                          ▼
    ┌─────────┐                              ┌─────────────┐
    │  Source  │                              │  Agent / LLM │
    │  Repo   │                              │  Consumer    │
    └─────────┘                              └─────────────┘
```

### 3.1 Extraction Pipeline

**Project:** `Ast.Memory` (Extraction namespace)  
**Responsibility:** Parse source code into typed graph nodes and edges.

| Sub-component | Input | Output |
|---|---|---|
| **Language Parser** (existing `Ast.Csharp/CsharpAstParser.cs`, `Ast.PowerShell/PowerShellAstParser.cs`) | Source files | `HypergraphNode` + `Hyperedge` with `HyperedgeType` values |
| **Structure Builder** | Parser output | Structural-layer hierarchy: `CONTAINS` + `DEPENDS_ON` edges |
| **Call-Chain Analyzer** | Parser output + resolved types | Call-chain-layer edges: `CALLS` / `EXTENDS` / `IMPLEMENTS` / `REFERENCES` |
| **Semantic Indexer** | Symbol names, docstrings, signatures | `Metadata["Embedding"]` vectors via `IEmbeddingGenerator` (§1.2); `SIMILAR_TO` edges above `SimilarityThreshold` |
| **Fact Normalizer** | All raw facts | Stable path-based IDs, `VersionRange` stamps, `ContentHash`, deduplication |

**Key design:** Existing `Ast.Core` parsers are the primary extraction
adapters. `RepositoryProcessor` (in `src/ast/Ast.Core/RepositoryProcessor.cs`)
orchestrates file discovery and dispatches to language-specific parsers.
Tree-sitter adapters for TypeScript, Python, Go are additive — no schema
changes needed.

### 3.2 Graph Store

**Project:** `Ast.Memory` (interface `IMemoryGraphStore` in `Ast.Memory/Interfaces/`, implementations in `Ast.Memory/Storage/`)  
**Responsibility:** Persist the four graph layers, serve traversal queries,
manage versioned snapshots.

`IMemoryGraphStore` extends `IGraphStore` (from `Ast.GraphDb`) with temporal,
vector, and diff-based operations. It is defined **in `Ast.Memory`**, not in
`Ast.GraphDb` — no existing project is modified.

| Deployment | Graph backend | Vector backend |
|---|---|---|
| Local / CI | `SqliteGraphStore` (new, in `Ast.Memory/Storage/`) — SQLite + FTS5 + recursive CTEs | sqlite-vss or in-process HNSW (usearch) — adapter in `Ast.Memory/Storage/` |
| Team / Cloud | `Neo4jMemoryGraphStore` (new, in `Ast.Memory/Storage/`) — wraps existing `Neo4jGraphStore` from `src/ast/Ast.GraphDb/Neo4jGraphStore.cs` via composition | Existing `QdrantVectorStore` from `src/ast/Ast.VectorDb/QdrantVectorStore.cs` — used directly via `IVectorStore` reference |

Backend selection is via DI (existing `src/ast/Ast.GraphDb/ServiceCollectionExtensions.cs`
and `src/ast/Ast.VectorDb/ServiceCollectionExtensions.cs` remain unchanged);
the new `Ast.Memory/ServiceCollectionExtensions.cs` registers
`IMemoryGraphStore` implementations and all memory-specific services.
All backends implement `IMemoryGraphStore` (§5.2).

### 3.3 Retrieval Engine

**Project:** `Ast.Memory/Retrieval` (all new code — no modifications to `Ast.Query`)  
**Responsibility:** Accept a query, plan retrieval, traverse the graph, rank
and return memory fragments. Internally composes `ICodeQuery` (from `Ast.Query`)
and `IVectorStore` (from `Ast.VectorDb`) via constructor injection — those
existing interfaces are consumed, not modified.

| Sub-component | Role |
|---|---|
| **Query Planner** | Classifies query intent → selects retrieval mode(s) and graph layers. |
| **Graph Traverser** | Depth-limited BFS / DFS on selected layers. |
| **Vector Searcher** | ANN search over embeddings via `IVectorStore` (`src/ast/Ast.VectorDb/IVectorStore.cs`). |
| **Ranker** | Merges results using the 5-signal scoring model (§2.4). |
| **Budget Controller** | Enforces token budget; substitutes parent summaries when children exceed budget (HiAgent pattern). |

**Retrieval modes:**

| Mode | Layers | `SemanticRelations` filter | Example query |
|---|---|---|---|
| `StructuralDrill` | `structural` | `CONTAINS`, `DEPENDS_ON` | "What does the auth package contain?" |
| `CallTrace` | `call_chain` | `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` | "What calls validateToken?" |
| `SemanticSearch` | `semantic` | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` | "Find code related to rate limiting" |
| `ExpertiseRecall` | `expertise` | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` | "Known pitfalls for payments?" |
| `Hybrid` | all | all of the above | "Explain auth flow and flag issues" |

**Edge filtering:** `TraversalPlan.SemanticRelations` (a `string` list) is
the **sole** edge filter. It works uniformly for code-derived and memory-only
edges because every edge carries `Metadata["SemanticRelation"]`. The legacy
`TraversalPlan.EdgeTypes` (`HyperedgeType` list) is **deprecated** — retained
for backward compatibility, never set by memory queries.

**Cross-branch expertise visibility:** Expertise-layer nodes are **globally
visible** regardless of git branch. Staleness is detected by comparing the
target node's `ContentHash` against the current branch's version. When
mismatched, the Context Assembler injects: `⚠ stale — target code changed`.

### 3.4 Context Assembler

**Project:** `Ast.Memory/Context`  
**Responsibility:** Transform ranked fragments into a structured prompt section.

Three transformations:

1. **Hierarchy compression** — parent + all children selected → collapse to
   parent summary + child signatures.
2. **Deduplication** — same code span from multiple paths emitted once.
3. **Formatting** — deterministic Markdown with sections:
   `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise`.

### 3.5 Evolution Engine

**Project:** `Ast.Memory/Evolution`  
**Responsibility:** Manage the Expertise layer lifecycle.

| Operation | Trigger | Description |
|---|---|---|
| **Write-back** | Agent emits annotation | Creates Expertise node, computes embedding via `IEmbeddingGenerator`, links via `APPLIES_TO` edges. If cosine similarity to existing expertise > `SimilarityThreshold` (0.85), creates `DERIVED_FROM` edge. |
| **Consolidation** | Post-PR-merge or manual | LLM-powered merge of overlapping expertise nodes. Produces `SUPERSEDES` edges. Community detection (Louvain) generates `Topic` + `SemanticCluster` nodes. |
| **Time-based decay** | On every retrieval | `decayed = initial × 2^(−age_days / DecayHalfLifeDays) × centrality_boost` where `centrality_boost = 1.0 + 0.5 × normalized_pagerank` (range 1.0–1.5). |
| **Event-based decay** | On code change | Major change: confidence × `StaleDecayFactor` (0.5). Minor change (whitespace/comments): × `MinorDecayFactor` (0.9). Below `ArchiveConfidenceFloor` (0.1) → soft-delete. |

### 3.6 Incremental Sync

**Project:** `Ast.Memory/Sync`  
**Responsibility:** Keep graph current without full re-extraction.

1. On commit or file-save, diff changed files (`git diff` or FS watcher).
2. Re-run Extraction Pipeline on changed files only.
3. Compute node/edge diff against current graph.
4. Apply inserts, updates, tombstones (`VersionRange.Until` set, not deleted).
5. Propagate: flag downstream `CALLS` edges as `Stale` if source signature changed.

**Performance target:** < 2 seconds for single-file change on repos ≤ 500K LOC.

### 3.7 Concurrency Model

The memory graph supports concurrent reads with serialized writes. The
concurrency strategy differs by deployment mode:

#### Local (SQLite)

- **WAL mode** (`PRAGMA journal_mode=WAL`) enables concurrent readers
  alongside a single writer.
- **In-process write mutex** (`SemaphoreSlim(1,1)`) serializes all write
  operations (`ApplyDiffAsync`, `RecordExpertiseAsync`, `DecayStaleExpertiseAsync`).
  No optimistic-concurrency conflicts are raised — writes queue and execute
  in order.
- **File-system watcher debounce** (`IncrementalSyncDebounceMs = 1000`)
  prevents write storms during rapid saves.
- **Isolation:** Each `SqliteGraphStore` instance owns a single `SqliteConnection`.
  Only one `ast` process should write to a given database file at a time.
  Multiple readers (e.g., concurrent MCP tool calls) are safe under WAL.

#### Team (Neo4j + Qdrant)

- Neo4j natively supports concurrent readers and writers with ACID transactions.
- `Neo4jMemoryGraphStore.ApplyDiffAsync` executes within a single Neo4j
  transaction — all inserts, updates, and tombstones are atomic.
- **Write contention:** Multiple agents writing expertise concurrently is safe
  because `RecordExpertiseAsync` creates new nodes (no read-modify-write).
  Consolidation (`ConsolidateAsync`) acquires a distributed advisory lock
  (Neo4j `apoc.lock.nodes()` or application-level lease) to prevent concurrent
  merge operations.
- Qdrant vector operations are idempotent (upsert semantics).

#### Invariants (both modes)

- Reads never block on writes and never return partial diffs.
- A `Snapshot` is created per successful `ApplyDiffAsync` — point-in-time
  queries via `GetNodeAtCommitAsync` always see a consistent graph state.
- The audit log is append-only and lock-free (inserts only, no updates).

---

## 4. Data Model

### 4.1 Model Extension Strategy

The memory layer extends `HypergraphNode` and `Hyperedge` **non-breakingly**
via their existing `Metadata: Dictionary<string, object?>` dictionaries.
No properties are added to the base classes.

**Existing `HypergraphNode` properties** (from `src/ast/Ast.Core/HypergraphModels.cs` lines 74–86):

| Property | Type | Description |
|---|---|---|
| `Id` | `string` (required) | Unique node identifier |
| `Name` | `string` (required) | Display name |
| `NodeType` | `string` (required) | Kind: `"Class"`, `"Function"`, etc. |
| `Language` | `string` (required) | `"C#"`, `"PowerShell"`, etc. |
| `FilePath` | `string` (required) | Source file path |
| `Scope` | `string` | Containing scope |
| `LineStart` / `LineEnd` | `int` | Source location |
| `Modifiers` | `List<string>` | `["public", "async"]`, etc. |
| `Metadata` | `Dictionary<string, object?>` | **Extension point** — memory uses this |

**Existing `Hyperedge` properties** (from `src/ast/Ast.Core/HypergraphModels.cs` lines 199–207):

| Property | Type | Description |
|---|---|---|
| `Id` | `string` (required) | Unique edge identifier |
| `EdgeType` | `HyperedgeType` (required) | One of 44 enum values (or new `MemoryRelation`) |
| `NodeIds` | `List<string>` (required) | Participating node IDs (≥ 2) |
| `Roles` | `Dictionary<string, string>` (required) | Maps node ID → semantic role |
| `Languages` | `List<string>` | Languages of participating nodes |
| `Weight` | `double` | Default 1.0 |
| `Metadata` | `Dictionary<string, object?>` | **Extension point** — memory uses this |

`MemoryNode` and `MemoryEdge` are **composition wrappers** — they contain a
base-type instance as an `Inner` property and provide strongly-typed accessors
for memory Metadata keys. They do **not** use C# class inheritance
(`: HypergraphNode`). All inter-component interfaces accept/return the base
types (`HypergraphNode`, `Hyperedge`) to preserve backward compatibility with
970+ existing tests.

```csharp
// Composition — NOT inheritance
public class MemoryNode
{
    public HypergraphNode Inner { get; }
    public string Layer => (string)this.Inner.Metadata["MemoryLayer"];
    public VersionRange Version => (VersionRange)this.Inner.Metadata["VersionRange"];
    public string? ContentHash => (string?)this.Inner.Metadata.GetValueOrDefault("ContentHash");
    // ... typed accessors for all memory Metadata keys
}
```

### 4.2 Node Types

All nodes are `HypergraphNode` instances. The `NodeType` property and
`Metadata["MemoryLayer"]` together determine the role.

#### Code Nodes (Structural + Call-Chain layers)

| NodeType | Layer | Key Metadata | Description |
|---|---|---|---|
| `Repo` | structural | `root_path`, `vcs_url`, `default_branch` | Singleton root. ID = `"/"` |
| `Workspace` | structural | `name`, `path`, `manager` | Monorepo child; absent in single-package repos |
| `Package` | structural | `name`, `version`, `path` | Maps to `.csproj`, `package.json`, etc. |
| `Module` | structural | `file_path`, `language`, `hash` | One per source file |
| `Class` | structural | `name`, `fqn`, `is_abstract`, `is_exported` | Classes, interfaces, structs, enums, records |
| `Function` | structural | `name`, `fqn`, `signature`, `line_start`, `line_end` | Leaf structural node |
| `External` | call_chain | `package`, `fqn` | Out-of-repo boundary symbol (stub — no embedding, no `VersionRange`). ID format: `"ext:{package}:{qualifiedName}"` (§4.4). |

#### Semantic Nodes

| NodeType | Layer | Key Metadata | Description |
|---|---|---|---|
| `SemanticCluster` | semantic | `centroid_embedding`, `member_count` | Community of similar nodes |
| `Topic` | semantic | `title`, `summary_prose` | GraphRAG-style cluster summary |

#### Expertise Nodes

| NodeType | Layer | Key Metadata | Description |
|---|---|---|---|
| `Pattern` | expertise | `Title`, `Body`, `Confidence`, `SourceAgent`, `Evidence`, `Tags` | Recurring code pattern |
| `Pitfall` | expertise | (same fields as Pattern) | Known failure mode |
| `Preference` | expertise | (same fields as Pattern) | Coding convention / style |
| `Insight` | expertise | (same fields as Pattern) | Architectural observation |

### 4.3 Temporal Model

Commit SHAs are opaque and not orderable. The system uses a **dual-axis** model:

1. **Commit axis** — each SHA maps to a monotonic `commit_index` in the `commits` table.
2. **Wall-clock axis** — `LastTouched` (ISO-8601) records extraction time (informational only).

```sql
CREATE TABLE commits (
    commit_sha   TEXT PRIMARY KEY,
    commit_index INTEGER NOT NULL UNIQUE,
    timestamp    TEXT NOT NULL,
    parent_sha   TEXT
);
```

Every node and edge carries a `VersionRange` in its `Metadata`:

```csharp
public record VersionRange(
    string Since,       // commit SHA — provenance display
    int SinceIndex,     // monotonic index — range queries
    string? Until,      // null = current; non-null = tombstoned at this commit
    int? UntilIndex     // null = current; non-null = commit index of tombstone
);
```

Point-in-time query predicate:
`WHERE valid_from_idx <= $target_idx AND (valid_until_idx IS NULL OR valid_until_idx > $target_idx)`

### 4.4 Node ID Format

All IDs are **`string`** — no GUIDs, UUIDs, or integers.

| Node kind | Format | Example |
|---|---|---|
| Repo root | `"/"` | `"/"` |
| Directory | `"{relativePath}"` | `"/src/auth"` |
| File / Module | `"{relativePath}"` | `"/src/auth/login"` |
| Symbol (class, function) | `"{filePath}::{qualifiedName}"` | `"/src/auth/login::AuthService.validate"` |
| Expertise node | `"expertise:{kind}:{hash8}"` | `"expertise:pitfall:a1b2c3d4"` |
| SemanticCluster | `"cluster:{hash8}"` | `"cluster:e5f6a7b8"` |
| Topic | `"topic:{hash8}"` | `"topic:c9d0e1f2"` |
| External / boundary node | `"ext:{package}:{qualifiedName}"` | `"ext:Microsoft.CodeAnalysis:SyntaxNode"` |

**External / boundary nodes** are out-of-repo symbols referenced by in-repo
code (e.g., NuGet/npm dependencies). They are created as stub nodes with
`NodeType = "External"`, `Metadata["MemoryLayer"] = "call_chain"`, and no
`ContentHash` or `VersionRange`. They participate in `CALLS` and `REFERENCES`
edges but are never extracted or embedded. The `ext:` prefix ensures they
never collide with in-repo path-based IDs.

Edge IDs are deterministic: `"{sourceId}→{relation}→{targetId}"`.

### 4.5 Metadata Keys (Curated Registry)

Keys on memory nodes are governed by `AnnotationKeyRegistry` — a code-defined
registry with typed schemas. Agent extension keys use `ext:` prefix.

#### Node Metadata (`HypergraphNode.Metadata`)

| Key | Type | Description |
|---|---|---|
| `MemoryLayer` | `string` | `"structural"` \| `"call_chain"` \| `"semantic"` \| `"expertise"` |
| `VersionRange` | `VersionRange` | Commit SHA + index range (§4.3) |
| `ContentHash` | `string` | SHA-256 of source span |
| `LastTouched` | `string` (ISO-8601) | Wall-clock of last extraction (informational) |
| `Embedding` | `float[]` | Dense vector from `IEmbeddingGenerator` |
| `EmbeddingModel` | `string` | Model identifier (e.g., `nomic-embed-text`) |
| `Summary` | `string` | LLM-generated one-liner |
| `QualifiedName` | `string` | Fully-qualified name |

Expertise nodes carry additional keys: `ExpertiseKind`, `Title`, `Body`,
`Confidence`, `SourceAgent`, `Evidence` (list of `Citation`), `Tags`.

#### Edge Metadata (`Hyperedge.Metadata`)

| Key | Type | Description |
|---|---|---|
| `MemoryLayer` | `string` | Layer tag |
| `SemanticRelation` | `string` | Canonical edge name — **primary retrieval filter** |
| `VersionRange` | `VersionRange` | Temporal range |
| `Confidence` | `double` | 0.0–1.0 (direct calls=1.0, dynamic dispatch=0.5–0.7) |
| `Stale` | `bool` | Pending re-confirmation |
| `OriginalEdgeType` | `string` | Original `HyperedgeType` enum name (lossless round-trip) |

Relation-specific extra keys:

| Relation | Extra keys |
|---|---|
| `CALLS` | `ArgCount`, `IsAsync`, `IsConditional`, `CallSiteStart`, `CallSiteEnd` |
| `DEPENDS_ON` | `Alias`, `IsWildcard`, `IsTypeOnly` |
| `APPLIES_TO` | `Scope` (`"exact"` or `"subtree"`) |
| `SUPERSEDES` | `Reason`, `SupersededAt` (ISO-8601) |
| `SIMILAR_TO` | `CosineSim` |
| `REFERENCES` | `ReferenceKind` (`"type_annotation"`, `"variable"`, `"parameter"`, `"data_flow"`) |

### 4.6 Edge Shape & Hyperedge Semantics

`Hyperedge` from `Ast.Core` is a **multi-node hyperedge** with
`NodeIds: List<string>` and `Roles: Dictionary<string, string>`. It is NOT
a binary source→target edge. The memory layer preserves this model.

For binary relationships (the common case), the hyperedge has exactly 2
`NodeIds` with semantic roles defined in the edge taxonomy (§2.2).

**Memory-only edges** use `HyperedgeType.MemoryRelation` — a single new
enum value added to `Ast.Core/HypergraphModels.cs` (additive, non-breaking).
Existing parsers never emit this value, so no existing code path is affected.

```csharp
// In Ast.Core — single additive change
public enum HyperedgeType
{
    // ... existing 44 values unchanged ...
    MemoryRelation  // new — used for Semantic + Expertise layer edges
}
```

Storage example:
```
Hyperedge {
  Id       = "/src/auth/login::AuthService.login→CALLS→/src/auth/token::verify"
  EdgeType = MemoryRelation (or original HyperedgeType for code-derived)
  NodeIds  = ["/src/auth/login::AuthService.login", "/src/auth/token::verify"]
  Roles    = { "/src/auth/login::AuthService.login": "caller",
               "/src/auth/token::verify": "callee" }
  Weight   = 1.0
  Metadata = { "MemoryLayer": "call_chain",
               "SemanticRelation": "CALLS",
               "VersionRange": { Since: "abc123", SinceIndex: 42, Until: null, UntilIndex: null },
               "Confidence": 1.0,
               "Stale": false,
               "OriginalEdgeType": "MethodInvocation" }
}
```

### 4.7 Embedding Lifecycle

All nodes use the same general-purpose text-embedding model (configured in
`ExtractionOptions.EmbeddingModel`; default: `"nomic-embed-text"` — consistent
with the existing `EmbeddingGeneratorOptions.ModelName` default at
`src/ast/Ast.Embedding/EmbeddingModels.cs` line 119).

| Node origin | When embedded | Input text |
|---|---|---|
| Code nodes | Extraction time (Semantic Indexer) | `HypergraphNode.GetEnhancedEmbeddingText()` |
| Expertise nodes | Write-back time (`RecordExpertiseAsync`) | `"{Kind}: {Title}\n{Body}"` |
| Topic / Cluster nodes | Consolidation time | LLM-generated summary |
| Re-embedding on change | Incremental sync | Same as initial |

Model changes require full re-embedding (`ast memory reindex --reembed`).
Vector index is secondary — recoverable via `RebuildVectorIndexAsync()`.

### 4.8 Snapshot

A logical point-in-time bookmark — not a full copy:

```csharp
public class Snapshot
{
    public required string Id { get; init; }           // "snapshot:{commitSha}"
    public required string CommitSha { get; init; }
    public required int CommitIndex { get; init; }
    public string? ParentSha { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public required SnapshotStats Stats { get; init; }
}

public class SnapshotStats
{
    public int Nodes { get; init; }
    public int Edges { get; init; }
    public int ExpertiseNodes { get; init; }
}
```

---

## 5. Interfaces

All interfaces are C# (.NET 8.0) matching the repository stack.

### 5.1 Extraction Pipeline

**Project:** `Ast.Memory`

```csharp
public interface IExtractionPipeline
{
    IAsyncEnumerable<GraphFact> ExtractAllAsync(
        string repoRoot, ExtractionOptions options,
        CancellationToken ct = default);

    Task<GraphDiff> ExtractIncrementalAsync(
        string repoRoot, string baseCommit, string headCommit,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default);
}

public class ExtractionOptions
{
    public required IReadOnlyList<string> Languages { get; init; }
    public bool EnableLsp { get; init; }
    public string EmbeddingModel { get; init; } = "nomic-embed-text";
    public long MaxFileSize { get; init; } = 1_048_576;
}

public abstract record GraphFact(string Id, string ContentHash, VersionRange VersionRange);
public record NodeFact(string Id, string ContentHash, VersionRange VersionRange, HypergraphNode Node)
    : GraphFact(Id, ContentHash, VersionRange);
public record EdgeFact(string Id, string ContentHash, VersionRange VersionRange, Hyperedge Edge)
    : GraphFact(Id, ContentHash, VersionRange);

public record GraphDiff(
    IReadOnlyList<GraphFact> Inserts,
    IReadOnlyList<GraphFact> Updates,
    IReadOnlyList<string> Tombstones);

public record VersionRange(string Since, int SinceIndex, string? Until, int? UntilIndex);
```

### 5.2 Memory Graph Store

**Project:** `Ast.Memory/Interfaces` — extends `IGraphStore` from `Ast.GraphDb` (referenced, not modified)

```csharp
public interface IMemoryGraphStore : IGraphStore
{
    Task<Snapshot> ApplyDiffAsync(GraphDiff diff, string commitSha,
        CancellationToken ct = default);

    Task<TraversalResult> TraverseAsync(string startNodeId, TraversalPlan plan,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScoredNode>> VectorSearchAsync(float[] queryVector,
        int topK, NodeFilter? filter = null, CancellationToken ct = default);

    Task<HypergraphNode?> GetNodeAtCommitAsync(string nodeId, string commitSha,
        CancellationToken ct = default);

    Task<IReadOnlyList<HypergraphNode>> FindByNameAsync(string prefix,
        IReadOnlyList<string>? kinds = null, int limit = 20,
        CancellationToken ct = default);

    Task RebuildVectorIndexAsync(CancellationToken ct = default);

    Task<int?> ResolveCommitIndexAsync(string commitSha,
        CancellationToken ct = default);

    Task<int> RegisterCommitAsync(string commitSha, string? parentSha,
        DateTime timestamp, CancellationToken ct = default);
}

public class TraversalPlan
{
    public required string Direction { get; init; } // "outbound" | "inbound" | "both"

    /// Primary edge filter — canonical relation names from §2.2.
    /// Works uniformly for code-derived and memory-only edges.
    public IReadOnlyList<string>? SemanticRelations { get; init; }

    [Obsolete("Use SemanticRelations. Ignored by memory queries.")]
    public IReadOnlyList<HyperedgeType>? EdgeTypes { get; init; }

    public string? LayerFilter { get; init; }
    public int MaxDepth { get; init; } = 3;
    public NodeFilter? Filter { get; init; }
    public int Limit { get; init; } = 100;
}

public class TraversalResult
{
    public required IReadOnlyList<HypergraphNode> Nodes { get; init; }
    public required IReadOnlyList<Hyperedge> Edges { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Paths { get; init; }
}
```

### 5.3 Retrieval Engine

**Project:** `Ast.Memory/Retrieval` (new — composes `ICodeQuery` from `Ast.Query` and `IVectorStore` from `Ast.VectorDb` via DI, does not modify either)

```csharp
public interface IMemoryRetrievalEngine
{
    Task<RetrievalResult> RetrieveAsync(RetrievalQuery query,
        CancellationToken ct = default);
}

public class RetrievalQuery
{
    public required string Text { get; init; }
    public string? Scope { get; init; }
    public RetrievalMode? Mode { get; init; }
    public int TokenBudget { get; init; } = 8_000;
    public bool IncludeExpertise { get; init; } = true;
    public string? AtCommit { get; init; }
}

public enum RetrievalMode
{
    StructuralDrill, CallTrace, SemanticSearch, ExpertiseRecall, Hybrid
}

public class RetrievalResult
{
    public required string ContextBlock { get; init; }
    public required int TokenCount { get; init; }
    public required IReadOnlyList<Citation> Citations { get; init; }
    public required RetrievalMode ResolvedMode { get; init; }
}

public class Citation
{
    public required string NodeId { get; init; }
    public required string CommitSha { get; init; }
    public string? Snippet { get; init; }
}
```

### 5.4 Evolution Engine

**Project:** `Ast.Memory`

```csharp
public interface IEvolutionEngine
{
    Task<string> RecordExpertiseAsync(ExpertiseEntry entry,
        CancellationToken ct = default);

    Task<ConsolidationReport> ConsolidateAsync(string scope,
        CancellationToken ct = default);

    Task<int> DecayStaleExpertiseAsync(string headCommit,
        CancellationToken ct = default);
}

public class ExpertiseEntry
{
    public required string Kind { get; init; }             // "pattern"|"pitfall"|"preference"|"insight"
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyList<string> AppliesTo { get; init; }
    public string Scope { get; init; } = "exact";          // "exact"|"subtree"
    public IReadOnlyList<Citation> Evidence { get; init; } = [];
    public required string SourceAgent { get; init; }
}

public class ConsolidationReport
{
    public int Merged { get; init; }
    public int Superseded { get; init; }
    public int Retracted { get; init; }
    public IReadOnlyList<string> NewTopics { get; init; } = [];
}
```

### 5.5 Incremental Sync

**Project:** `Ast.Memory`

```csharp
public interface IIncrementalSync
{
    void Watch(string repoRoot, WatchOptions options);
    void Unwatch(string repoRoot);
    Task<Snapshot> SyncToCommitAsync(string repoRoot, string commitSha,
        CancellationToken ct = default);
}

public class WatchOptions
{
    public int DebounceMs { get; init; } = 1_000;
    public bool AutoDecay { get; init; } = true;
}
```

### 5.6 Agent API / MCP Tool Interface

**Project:** `Ast.Memory/Mcp` (new code only; `Ast.Cli/Program.cs` gets additive `config.AddBranch("memory", ...)` lines to register memory sub-commands via Spectre.Console.Cli's manual registration API — consistent with how the existing 7 commands are registered)  
**Responsibility:** Expose the memory graph to external agents via the
[Model Context Protocol](https://modelcontextprotocol.io) (MCP) tool surface.
Agent interaction is **purely programmatic** — typed function calls, no query
DSL (per operator decision D2 in tech-spec.md §11).

The MCP layer is a thin adapter: it deserializes MCP tool-call JSON into typed
requests, delegates to the internal C# interfaces (`IMemoryRetrievalEngine`,
`IEvolutionEngine`, `IIncrementalSync`), and serializes results back to MCP
tool-result JSON. No business logic lives in the MCP layer.

#### 5.6.1 MCP Tool Definitions

Five tools are registered via `ServiceCollectionExtensions.AddMemoryMcpTools()`.
Each tool maps to one or more internal interface methods.

| MCP Tool Name | Internal Method(s) | Description |
|---|---|---|
| `memory_query` | `IMemoryRetrievalEngine.RetrieveAsync` | Retrieve token-budgeted context for a task description |
| `memory_call_chain` | `IMemoryGraphStore.TraverseAsync` | Trace callers/callees of a symbol to a given depth |
| `memory_find_similar` | `IMemoryGraphStore.VectorSearchAsync` | Find semantically similar code nodes |
| `memory_annotate` | `IEvolutionEngine.RecordExpertiseAsync` | Write an expertise entry (pattern/pitfall/preference/insight) |
| `memory_status` | `IMemoryGraphStore.FindByNameAsync` (stats) + conditionally `IIncrementalSync.SyncToCommitAsync` (when `syncToHead=true`) | Check graph status; optionally trigger sync |

#### 5.6.2 MCP Tool Schemas

```json
{
  "name": "memory_query",
  "description": "Retrieve ranked, token-budgeted context from the agent memory graph.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query":          { "type": "string",  "description": "Natural-language task description or symbol name." },
      "scope":          { "type": "string",  "description": "Optional qualified-name prefix to limit traversal (e.g., 'src/payments')." },
      "mode":           { "type": "string",  "enum": ["structural_drill","call_trace","semantic_search","expertise_recall","hybrid"],
                          "description": "Retrieval mode. Default: hybrid." },
      "tokenBudget":    { "type": "integer", "description": "Max tokens in result. Default: 8000, ceiling: 32000." },
      "includeExpertise": { "type": "boolean", "description": "Include expertise nodes. Default: true." },
      "atCommit":       { "type": "string",  "description": "Optional commit SHA for point-in-time query." }
    },
    "required": ["query"]
  }
}
```

```json
{
  "name": "memory_call_chain",
  "description": "Trace the call chain (callers or callees) of a symbol.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "symbol":    { "type": "string",  "description": "Node ID or qualified name of the target symbol." },
      "direction": { "type": "string",  "enum": ["outbound","inbound","both"], "description": "Traversal direction. Default: both." },
      "depth":     { "type": "integer", "description": "Max traversal depth. Default: 3, max: 10." }
    },
    "required": ["symbol"]
  }
}
```

```json
{
  "name": "memory_find_similar",
  "description": "Find code nodes semantically similar to a query or existing node.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query": { "type": "string",  "description": "Text query or node ID to find similar nodes for." },
      "topK":  { "type": "integer", "description": "Number of results. Default: 10, max: 50." },
      "kinds": { "type": "array", "items": { "type": "string" }, "description": "Filter by node kinds (e.g., ['Function','Class'])." }
    },
    "required": ["query"]
  }
}
```

```json
{
  "name": "memory_annotate",
  "description": "Record an expertise entry (pattern, pitfall, preference, or insight) in the memory graph.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "kind":       { "type": "string",  "enum": ["pattern","pitfall","preference","insight"] },
      "title":      { "type": "string",  "description": "Short title for the expertise entry." },
      "body":       { "type": "string",  "description": "Detailed description of the learning." },
      "appliesTo":  { "type": "array",   "items": { "type": "string" }, "description": "Node IDs this expertise applies to." },
      "scope":      { "type": "string",  "enum": ["exact","subtree"], "description": "Scope of applicability. Default: exact." },
      "evidence":   { "type": "array",   "items": {
        "type": "object",
        "properties": {
          "nodeId":    { "type": "string" },
          "commitSha": { "type": "string" },
          "snippet":   { "type": "string" }
        }
      }, "description": "Optional evidence citations." }
    },
    "required": ["kind","title","body","appliesTo"]
  }
}
```

```json
{
  "name": "memory_status",
  "description": "Check memory graph status for a repository and optionally trigger sync.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "repoRoot":   { "type": "string",  "description": "Path to the repository root." },
      "syncToHead": { "type": "boolean", "description": "If true, sync graph to HEAD before returning status. Default: false." }
    },
    "required": ["repoRoot"]
  }
}
```

#### 5.6.3 MCP Response Format

All tools return standard MCP tool-result JSON. The `memory_query` response
wraps a `RetrievalResult` (§5.3):

```json
{
  "content": [
    {
      "type": "text",
      "text": "## Structure\npayments/checkout/\n├── CheckoutService ...\n\n## Call Graph\n...\n\n## Known Expertise\n⚠ PITFALL: ..."
    }
  ],
  "metadata": {
    "tokenCount": 6420,
    "resolvedMode": "hybrid",
    "citationCount": 12,
    "graphCommit": "abc123"
  }
}
```

The `memory_status` response returns graph statistics and sync state:

```json
{
  "content": [
    {
      "type": "text",
      "text": "Graph synced to abc123 (commit_index 42). 1,240 nodes, 3,891 edges, 17 expertise nodes."
    }
  ],
  "metadata": {
    "lastCommit": "abc123",
    "commitIndex": 42,
    "nodeCount": 1240,
    "edgeCount": 3891,
    "expertiseNodeCount": 17,
    "syncedToHead": true
  }
}
```

#### 5.6.4 Internal Class Layout

```
Ast.Memory/
  Mcp/
    MemoryMcpToolRegistration.cs   ← registers 5 tools with MCP discovery
    MemoryMcpHandler.cs            ← dispatches MCP JSON → internal interfaces
    McpSchemas.cs                  ← JSON schema constants for tool definitions
  Cli/
    MemoryCommandModule.cs         ← Spectre.Console.Cli command classes
```

`Ast.Cli/Program.cs` receives additive lines to register the memory branch:

```csharp
// Additive — existing commands unchanged
config.AddBranch("memory", memory =>
{
    memory.AddCommand<MemoryOnboardCommand>("onboard");
    memory.AddCommand<MemorySyncCommand>("sync");
    memory.AddCommand<MemoryQueryCommand>("query");
    memory.AddCommand<MemoryCallersCommand>("callers");
    memory.AddCommand<MemoryCalleesCommand>("callees");
    memory.AddCommand<MemoryAnnotateCommand>("annotate");
    memory.AddCommand<MemoryEvolveCommand>("evolve");
    memory.AddCommand<MemoryReindexCommand>("reindex");
    memory.AddCommand<MemoryStatusCommand>("status");
});
```

This is consistent with Spectre.Console.Cli's manual registration pattern
already used by all 7 existing commands. `Ast.Cli.csproj` gains a project
reference to `Ast.Memory` — no other changes to the `Ast.Cli` project.

`MemoryMcpHandler` composes `IMemoryRetrievalEngine`, `IEvolutionEngine`, and
`IMemoryGraphStore` via constructor injection. It does NOT contain business
logic — only JSON deserialization, delegation, and response formatting.

#### 5.6.5 CLI Commands (non-MCP)

For developer workflows that don't use MCP, the same functionality is exposed
via CLI commands. Command classes live in `Ast.Memory/Cli/` and are registered
in `Ast.Cli/Program.cs` via additive `config.AddBranch("memory", ...)` lines
(see §5.6.4). This is a minimal, non-breaking change: existing command
registrations are untouched; only the new memory branch is added.

| CLI Command | Maps to |
|---|---|
| `ast memory onboard <repo-root>` | Full extraction + initial sync |
| `ast memory sync [--base-commit <sha>]` | Incremental sync |
| `ast memory query <text> [--budget <tokens>]` | `IMemoryRetrievalEngine.RetrieveAsync` |
| `ast memory callers <symbol> [--depth N]` | `IMemoryGraphStore.TraverseAsync` (inbound) |
| `ast memory callees <symbol> [--depth N]` | `IMemoryGraphStore.TraverseAsync` (outbound) |
| `ast memory annotate --kind <k> --title <t> --body <b> --applies-to <ids>` | `IEvolutionEngine.RecordExpertiseAsync` |
| `ast memory evolve [--scope <prefix>]` | `IEvolutionEngine.ConsolidateAsync` |
| `ast memory reindex [--reembed]` | Full re-extraction with optional re-embedding |
| `ast memory status` | Graph stats, last sync commit, node/edge counts |

---

## 6. End-to-End Sequence Flows

### 6.1 Initial Repository Onboarding

```
Actor: Developer runs `ast memory onboard <repo-root>`.

1. IncrementalSync.SyncToCommitAsync(repoRoot, HEAD)
   ├─ 1a. GraphStore.RegisterCommitAsync(HEAD, parentSha, timestamp)
   │      → assigns monotonic commit_index.
   │
   ├─ 2. ExtractionPipeline.ExtractAllAsync(repoRoot, options)
   │     ├─ 2a. Language Parser (Ast.Csharp / Ast.PowerShell) → AST facts
   │     ├─ 2b. Structure Builder → Structural-layer nodes + edges
   │     ├─ 2c. Call-Chain Analyzer → Call-chain-layer edges
   │     ├─ 2d. Semantic Indexer → embeddings via IEmbeddingGenerator
   │     │      + SIMILAR_TO edges where cosine ≥ SimilarityThreshold
   │     └─ 2e. Fact Normalizer → stable IDs, VersionRange, ContentHash
   │
   ├─ 3. GraphStore.ApplyDiffAsync(diff, HEAD)
   │     ├─ 3a. Insert all nodes and edges
   │     ├─ 3b. Write vectors to vector index (synchronous)
   │     └─ 3c. Create Snapshot record
   │
   └─ 4. EvolutionEngine.ConsolidateAsync("*")
         ├─ 4a. Community detection (Louvain) on Structural + Call-chain
         ├─ 4b. LLM generates summary per community
         └─ 4c. Store Topic + SemanticCluster nodes in Semantic layer

Result: Full graph available, Snapshot created, community summaries populated.
```

### 6.2 Incremental Sync on Commit

```
Trigger: Git post-commit hook or file-system watcher.

1. IncrementalSync detects changed files via git diff.
   ├─ 1a. GraphStore.RegisterCommitAsync(headCommit, baseCommit, ts)

2. ExtractionPipeline.ExtractIncrementalAsync(repo, base, head, changedFiles)
   ├─ 2a. Re-parse changed files only
   ├─ 2b. Diff against current graph → GraphDiff
   └─ 2c. Tombstone removed symbols (VersionRange.Until = head)

3. GraphStore.ApplyDiffAsync(diff, headCommit)
   ├─ 3a. Insert new / update modified nodes (re-embedded vectors)
   ├─ 3b. Tombstone deleted nodes/edges
   └─ 3c. Flag downstream CALLS edges as Stale if signature changed

4. EvolutionEngine.DecayStaleExpertiseAsync(headCommit)
   ├─ 4a. Find expertise with APPLIES_TO targets tombstoned or changed
   └─ 4b. Apply StaleDecayFactor (0.5) or MinorDecayFactor (0.9)

5. [If structural change exceeds threshold]
   └─ EvolutionEngine.ConsolidateAsync(affectedPackage)

Result: Graph updated; stale expertise decayed; summaries refreshed.
Latency target: < 2 s for single-file change on repos ≤ 500K LOC.
```

### 6.3 Agent Retrieves Context

```
Actor: Agent needs context for a task in "payments.checkout".

1. Agent calls RetrievalEngine.RetrieveAsync({
     Text: "How does checkout work? Any known pitfalls?",
     Scope: "payments.checkout",
     TokenBudget: 8000,
     IncludeExpertise: true
   })

2. Query Planner → mode = Hybrid (structural + call-chain + expertise intent)

3. Parallel fan-out:
   ├─ Graph Traverser: StructuralDrill from "payments.checkout", depth 3
   ├─ Graph Traverser: CallTrace forward DFS from public functions, depth 5
   ├─ Graph Traverser: ExpertiseRecall — APPLIES_TO in checkout subtree
   └─ Vector Searcher: ANN top-20, filtered to payments.* namespace

4. Ranker merges results:
   score = 0.30×structural + 0.25×semantic + 0.20×centrality
         + 0.15×recency + 0.10×expertise

5. Budget Controller:
   ├─ Estimate tokens per fragment
   ├─ Collapse low-level functions → parent summaries if over budget
   └─ Drop lowest-ranked until within 8K budget

6. Context Assembler formats:
   ## Structure
   payments/checkout/
   ├── CheckoutService — orchestrates checkout flow
   ├── PriceCalculator — discounts, tax
   └── ...

   ## Call Graph
   CheckoutService.process() → PriceCalculator.compute()
                              → InventoryClient.reserve()
                              → PaymentGateway.charge()

   ## Related Code
   [ranked snippets with file:line]

   ## Known Expertise
   ⚠ PITFALL: PaymentGateway.charge() not idempotent (conf: 0.91)
   ✓ PATTERN: CheckoutService uses saga for rollback (conf: 0.88)

7. Return RetrievalResult { ContextBlock, TokenCount: 6420,
     Citations, ResolvedMode: Hybrid }
```

### 6.4 Agent Writes Back Expertise

```
Actor: Agent records a learning after completing a task.

1. Agent calls EvolutionEngine.RecordExpertiseAsync({
     Kind: "pitfall",
     Title: "Race condition in concurrent checkout",
     Body: "Two concurrent requests to CheckoutService.process() for
            the same cart can double-reserve in InventoryClient.reserve().
            Fix: distributed lock on cart_id before saga.",
     AppliesTo: ["/src/payments/checkout::CheckoutService.process",
                 "/src/payments/inventory::InventoryClient.reserve"],
     Scope: "exact",
     Evidence: [{ NodeId: "...", CommitSha: "abc123", Snippet: "..." }],
     SourceAgent: "copilot-session-42"
   })

2. Evolution Engine:
   ├─ 2a. Create Pitfall node (Confidence = 1.0)
   ├─ 2b. Embed "{Kind}: {Title}\n{Body}" via IEmbeddingGenerator
   ├─ 2c. Write vector to index (synchronous)
   ├─ 2d. Create APPLIES_TO edges to both target nodes
   │       (HyperedgeType = MemoryRelation, SemanticRelation = "APPLIES_TO")
   └─ 2e. Check overlap with existing expertise:
          cosine > SimilarityThreshold → DERIVED_FROM edge + schedule consolidation

3. Return new expertise node ID.

Result: Future retrievals for checkout code surface this pitfall.
```

### 6.5 Cross-Session Learning

```
S1: Agent onboards repo. 0 expertise nodes.
    Completes payments refactor.
    Writes: PATTERN "saga-based rollback" (confidence 1.0)

S2: New session retrieves payments context.
    Sees saga pattern in ## Known Expertise section.
    Uses it to extend checkout correctly.
    Writes: INSIGHT "saga steps must be registered in order"

S3: Code changes land — CheckoutService.process() refactored.
    Incremental Sync triggers decay:
    ├─ Saga pattern: target content_hash changed → conf 1.0 → 0.9 (minor)
    └─ Race pitfall: target unchanged → no decay

S5: Consolidation after PR batch:
    ├─ 3 related expertise nodes found
    ├─ LLM merges → SUPERSEDES edges
    └─ Topic "Payments Saga Orchestration" created

S8: New session gets consolidated, high-confidence expertise.
    Higher-quality completions — memory compounded across sessions.
```

### 6.6 MCP Tool Invocation (External Agent)

```
Actor: An MCP-compatible agent (e.g., Copilot, Cursor) invokes memory_query.

1. Agent sends MCP tool call:
   {
     "name": "memory_query",
     "arguments": {
       "query": "How does checkout work? Any known pitfalls?",
       "scope": "payments.checkout",
       "tokenBudget": 8000,
       "includeExpertise": true
     }
   }

2. MemoryMcpHandler receives the call:
   ├─ 2a. Deserialize JSON → RetrievalQuery (§5.3)
   ├─ 2b. Validate tokenBudget ≤ TokenBudgetCeiling (32K)
   └─ 2c. Delegate to IMemoryRetrievalEngine.RetrieveAsync(query)

3. RetrievalEngine executes (same as §6.3 steps 2–7):
   ├─ Query Planner → mode = Hybrid
   ├─ Parallel fan-out: structural + call-chain + expertise + vector search
   ├─ Ranker merges results using 5-signal scoring model
   ├─ Budget Controller enforces 8K token limit
   └─ Context Assembler formats Markdown

4. MemoryMcpHandler serializes RetrievalResult → MCP tool-result JSON:
   {
     "content": [{ "type": "text", "text": "## Structure\n..." }],
     "metadata": { "tokenCount": 6420, "resolvedMode": "hybrid", ... }
   }

5. Agent receives context block and incorporates it into its LLM prompt.

Result: Agent has structurally-aware, expertise-enriched context
without needing to re-discover the codebase.
```

### 6.7 MCP Expertise Write-Back (External Agent)

```
Actor: Agent records a learning via memory_annotate tool.

1. Agent sends MCP tool call:
   {
     "name": "memory_annotate",
     "arguments": {
       "kind": "pitfall",
       "title": "Race condition in concurrent checkout",
       "body": "Two concurrent requests can double-reserve inventory.",
       "appliesTo": ["/src/payments/checkout::CheckoutService.process"],
       "scope": "exact"
     }
   }

2. MemoryMcpHandler:
   ├─ 2a. Deserialize JSON → ExpertiseEntry (§5.4)
   ├─ 2b. Inject SourceAgent from MCP session context
   └─ 2c. Delegate to IEvolutionEngine.RecordExpertiseAsync(entry)

3. Evolution Engine executes (same as §6.4 steps 2a–2e).

4. MemoryMcpHandler returns MCP result:
   {
     "content": [{ "type": "text", "text": "Expertise recorded: expertise:pitfall:a1b2c3d4" }],
     "metadata": { "expertiseId": "expertise:pitfall:a1b2c3d4" }
   }

Result: Future agents querying checkout code will see this pitfall
in their ## Known Expertise section.
```

---

## 7. Deployment Topology

```
┌────────────────────────────────────────────────────────────┐
│  Local (single-dev)                                        │
│  SQLite graph + in-process HNSW (usearch)                  │
│  File-system watcher for incremental sync                  │
│  All in one CLI process                                    │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│  Team / CI (shared)                                        │
│  Neo4j for graph, Qdrant for vectors                       │
│  Git webhook for sync, gRPC/HTTP for retrieval             │
│  Evolution Engine as async worker (queue-driven)           │
└────────────────────────────────────────────────────────────┘
```

---

## 8. Design Decisions

| Decision | Rationale | Trade-off |
|---|---|---|
| Extend `src/ast/` stack, not greenfield | Proven models, existing tests, established conventions | Coupled to `HypergraphNode`/`Hyperedge` |
| **All new code in `Ast.Memory`** — two additive changes to existing projects (enum value in `Ast.Core`, command registration in `Ast.Cli`) | Preserves all existing tests unconditionally; eliminates coupling risk | `Ast.Memory` has project references to 5 existing projects; `Ast.Cli` gains a reference to `Ast.Memory` |
| Single `Ast.Memory` project with namespaces | Follows repo convention; avoids project sprawl | Larger compilation unit |
| Non-breaking extension via `Metadata` dict | Preserves all existing tests | String-typed keys; mitigated by `AnnotationKeyRegistry` constants |
| `HyperedgeType.MemoryRelation` + `SemanticRelation` metadata | One enum value; string identifies specific relation | Not compile-time checked; validated at write time |
| Four layers sharing node namespace | MAGMA: prevents signal entanglement | Cross-layer queries need joins |
| Composition wrappers, not inheritance | `MemoryNode`/`MemoryEdge` compose base types via `Inner` property | Extra indirection; no casting needed |
| Dual-axis temporal (commit_index + wall-clock) | Commit SHAs not orderable; integers enable range queries | Requires `commits` table bookkeeping |
| LLM-powered consolidation | Semantic merge needs NL reasoning | LLM cost; batched and async |
| Token-budget-aware retrieval | Finite agent context windows | Summaries lose detail; citations enable drill-down |
| Confidence decay (time + event) | Prevents stale expertise poisoning | Aggressive decay may lose valid insights; tunable |

---

## 9. Glossary

| Term | Definition |
|---|---|
| **GraphFact** | A single node or edge record from the Extraction Pipeline |
| **GraphDiff** | Batch of inserts, updates, tombstones applied atomically |
| **Tombstone** | Soft-delete: `VersionRange.Until` set, record retained for time-travel |
| **Commit Index** | Monotonic integer assigned per commit SHA; enables orderable queries |
| **Community** | Tightly-coupled node cluster from graph partitioning (Louvain) |
| **Expertise Node** | Agent-authored knowledge (Pattern/Pitfall/Preference/Insight) |
| **Snapshot** | Point-in-time bookmark keyed by commit SHA + index |
| **Context Block** | Markdown document from Context Assembler, ready for LLM prompt |
| **SemanticRelation** | Canonical string in `Metadata["SemanticRelation"]` — primary edge filter |
| **Memory-only Edge** | Edge with `HyperedgeType.MemoryRelation`; exists only in Semantic/Expertise layers |

---

## 10. Cross-Document Alignment

This document governs all naming, data model, edge taxonomy, ID format,
interface signatures, constants, and concurrency semantics. When sibling
documents differ, implementers follow this document. The canonical
resolutions below summarize every known divergence. Each sibling document
has been notified of these resolutions in its respective iteration notes.

### 10.1 tech-spec.md — Fully Aligned

All conflicts resolved as of tech-spec.md iteration 10. Key alignment:

| Topic | Canonical value (this doc) | Governing section |
|---|---|---|
| Embedding model | `"nomic-embed-text"` (general-purpose) | §4.7 |
| Composition wrappers | `MemoryNode.Inner`, not `: HypergraphNode` | §4.1 |
| `HyperedgeType` enum count | 44 existing + 1 `MemoryRelation` | §1.3, §4.6 |
| Token budget | default 8K, ceiling 32K | §2.4 |
| Decay formula | `2^(−age_days / 1095) × centrality_boost` | §3.5 |
| Agent API style | Typed MCP tools (no DSL) — 5 tools in §5.6 | §5.6 |
| Cross-branch expertise | Globally visible; staleness via `ContentHash` | §3.3 |

### 10.2 implementation-plan.md — Divergences Resolved

`implementation-plan.md` was drafted before this architecture stabilized
and contains stale names, formats, and thresholds. **All divergences below
are resolved — this architecture governs.** Implementers apply the
canonical values from this document regardless of what the plan says.

| Category | implementation-plan.md says | Canonical (this doc) |
|---|---|---|
| Class name | `ExpertiseNote` | **`ExpertiseEntry`** (§2.3, §5.4) |
| Interface name | `IMemoryStore`, `IExpertiseStore` | **`IMemoryGraphStore`** extends `IGraphStore`; expertise methods on **`IEvolutionEngine`** (§5.2, §5.4) |
| Extension model | `MemoryNode : HypergraphNode` (inheritance) | **Composition wrapper** with `Inner` property (§4.1) |
| ID format | `{repo}:{language}:{fqn}:{hash_short}` | `"{filePath}::{qualifiedName}"` (§4.4) |
| Edge: callers | `CALLED_BY` (stored) | **Reverse traversal** of `CALLS` — not a stored edge (§2.3) |
| Edge: inheritance | `INHERITS` | **`EXTENDS`** (§2.2) |
| Edge: imports | `IMPORTS` | **`DEPENDS_ON`** (§2.2) |
| Scoring: semantic | 0.30 | **0.25** (§2.4) |
| Scoring: centrality | 0.15 | **0.20** (§2.4) |
| Default token budget | 32K | **8,000** (ceiling is 32K) (§2.4) |
| Decay half-life | 60 days | **1,095 days** (3 years) (§2.4) |
| Decay formula | `e^(−λ × days)` | `2^(−age_days / 1095) × centrality_boost` (§3.5) |
| Enum name | `PatternType` | **`ExpertiseKind`** (§5.4) |
| Interface methods | `UpsertAnnotationAsync`, `GetAnnotationsAsync`, etc. | `ApplyDiffAsync`, `TraverseAsync`, `VectorSearchAsync`, etc. (§5.2) |
| `IMemoryGraphStore` location | implied `Ast.GraphDb` | **`Ast.Memory/Interfaces/`** — zero `Ast.GraphDb` files modified (§3.0) |
| MCP file location | `Ast.Cli` | **`Ast.Memory/Mcp/`** — `Ast.Cli` only gets additive registration (§5.6) |
| Embedding model | unspecified | **`"nomic-embed-text"`** (§4.7) |

### 10.3 e2e-scenarios.md — Presentation Aliases Mapped

The scenarios use readable shorthand. Canonical mappings:

| Scenario label | Canonical equivalent |
|---|---|
| `called_by` edge | Reverse traversal of `CALLS` (not a stored edge) |
| `direct_call` edge | `CALLS` with `Confidence = 1.0` |
| `polymorphic` edge | `CALLS` with `Confidence = 0.5`, `Metadata["IsPolymorphic"] = true` |
| `recursive` edge | Self-referencing `CALLS` edge |
| `service_call` edge | Out of scope for MVP (runtime/dynamic tracing excluded) |
| Unqualified symbol IDs (e.g., `AuthService.login`) | Full path-based IDs: `"{filePath}::{qualifiedName}"` (§4.4) |
| `ext::` prefix | `"ext:{package}:{qualifiedName}"` (§4.4) |
| Concurrent-write "conflict errors" | Serialized writes — no conflict errors raised (§3.7) |
| Cross-repo memory transfer | Out of scope per tech-spec §3 O2 |