# Agent Memory Architecture

> **Story:** `code-intelligence:AGENT-MEMORY` · 10 pts
>
> **Status:** Iteration 33
>
> This document is the **canonical authority** for component boundaries,
> data model, edge/layer naming, ID format, interface signatures, and
> concurrency semantics. When sibling documents
> ([tech-spec.md](tech-spec.md), [implementation-plan.md](implementation-plan.md),
> [e2e-scenarios.md](e2e-scenarios.md)) use different names, formats, or
> thresholds, **this document governs**. Sibling documents own their
> respective scopes:
>
> | Document | Owns | Current state |
> |---|---|---|
> | [`tech-spec.md`](tech-spec.md) | Problem statement, scope/non-goals, hard constraints (HC1–HC11), risks, success criteria, resolved operator decisions (D1–D6) | At iteration 23. Aligned on structural/naming points. Sixteen alignment items (A1–A16) tracked in §8.1, all resolved. A13–A16 added in iteration 28 (node-ID format, `LastTouched` type, `MemoryNode.Version` nullability, expertise hash input). Tech-spec's own header references architecture at "iteration 25" — this is stale header metadata; the alignment conclusions remain valid. |
> | [`implementation-plan.md`](implementation-plan.md) | Phased work items (6 phases), deliverables, evaluation harness | Fully aligned with canonical vocabulary — uses correct names (`IMemoryGraphStore`, `ExpertiseEntry`, composition wrappers, `VersionRange?`, correct method signatures), correctly specifies both hash formulas (ContentHash and Id hash8). One minor presentational gap catalogued in §8.2 below. `architecture.md` governs on all conflicts. |
> | [`e2e-scenarios.md`](e2e-scenarios.md) | Gherkin acceptance scenarios (13 features, 82 scenarios) | All 10 previously-catalogued divergences (E2E-1 through E2E-10) have been resolved in its current iteration — see §8.3 for historical record. |

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
| **Ast.Cli** | CLI host — `Program.cs` uses Spectre.Console.Cli with manual `AddCommand<T>()` registration (7 commands: parse, enrich, embed, index, query, pipeline, stats — lines 17–44). | Additive: `config.AddBranch("memory", ...)` + project ref to `Ast.Memory`. |

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
| `CentralityBoostFactor` | 0.5 | Evolution Engine (Decay) | Multiplied by normalized PageRank (range 0–1) and added to 1.0 to form `centrality_boost` (range 1.0–1.5) |

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
    Storage/             ← SqliteGraphStore, Neo4jMemoryGraphStore
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
| **Semantic Indexer** | `SemanticIndexer` | Batches nodes through `IEmbeddingGenerator.GenerateBatchEmbeddingsAsync()` (from existing `Ast.Embedding`), stores vectors, and creates `SIMILAR_TO` edges where cosine similarity ≥ `SimilarityThreshold` (0.85). |
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

| Implementation | Backing store | Use case |
|---|---|---|
| `SqliteGraphStore` | SQLite + FTS5 + sqlite-vss | Local / single-developer mode. Zero infrastructure. |
| `Neo4jMemoryGraphStore` | Neo4j (via existing `Ast.GraphDb`) + Qdrant (via existing `Ast.VectorDb`) | Team / CI mode. Shared graph across developers. |

Both implement `IMemoryGraphStore` (§5.1). Selection is via DI configuration
in `ServiceCollectionExtensions.cs`.

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
   - `SemanticSearch` — embed query text via `IEmbeddingGenerator.GenerateEmbeddingAsync()`, ANN lookup via `IVectorStore.SearchAsync()`.
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
| **Decay** | `DecayService` | Two decay mechanisms applied **multiplicatively** (both reduce confidence; their effects compound): (a) **Event-driven** — triggered by `IncrementalSync` when a target code node's `ContentHash` changes. `StaleDecayFactor` (0.5) is applied on substantive code changes; `MinorDecayFactor` (0.9) on whitespace/comment-only changes. Applied immediately at sync time — each `ContentHash` change triggers exactly one event-driven decay. (b) **Time-based** — applied periodically (or on demand via `DecayStaleExpertiseAsync`): `decayed_confidence = current_confidence × 2^(−age_days / DecayHalfLifeDays) × centrality_boost`, where `centrality_boost = 1.0 + CentralityBoostFactor × normalized_pagerank` (range 1.0–1.5). Uses base-2 exponential so that `DecayHalfLifeDays` (1,095 = 3 years) is the exact half-life. `current_confidence` already reflects any prior event-driven decays. Entries below `ArchiveConfidenceFloor` (0.1) are soft-deleted via `IMemoryGraphStore.ArchiveExpertiseAsync` (sets `archived = 1` in storage, excluded from `GetExpertiseForNodeAsync` results). Archived entries can be restored via `IEvolutionEngine.RestoreArchivedExpertiseAsync` or inspected via `IMemoryGraphStore.GetArchivedExpertiseAsync`. |
| **Community Detection** | `EvolutionEngine.DetectCommunitiesAsync()` | Louvain algorithm over `CALLS`/`SIMILAR_TO` edges → creates `BELONGS_TO_CLUSTER` edges and `Topic` summary nodes in the Semantic layer (GraphRAG pattern). |

### 3.6 Incremental Sync

**Responsibility:** Keep the graph current without full re-extraction.

| Trigger | Mechanism |
|---|---|
| `git pull` / commit | `GitDiffDriver` computes changed file set via `git diff --name-status` |
| File save (IDE) | File-system watcher with `IncrementalSyncDebounceMs` (1,000 ms) debounce |

On trigger:

1. `GitDiffDriver` identifies added/modified/deleted files.
2. For each batch of modified files: re-extract via
   `IExtractionPipeline.ExtractFilesAsync(repoRoot, changedPaths, ...)`,
   diff old vs. new nodes, upsert changes, set `UntilIndex` on removed
   nodes/edges.
3. For deleted files: set `UntilIndex` on all nodes/edges rooted in that file
   (via `RetireNodeAsync`, which cascades to edges; plus individual
   `RetireEdgeAsync` for orphaned edges not caught by cascade).
4. Propagate invalidation: mark downstream `CALLS` edges for re-validation
   via `UpdateEdgeMetadataAsync(edgeId, { "Stale": true })`.
5. Re-run `SemanticIndexer` on changed nodes only (incremental embedding).

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
| `Id` | `string` | `HypergraphNode.Id` | Format: `"/{relativePath}::{qualifiedName}"` for symbols (leading `/` always present); `"/{relativePath}"` for files/packages; `"/"` for repo root; `"ext:{package}:{qualifiedName}"` for external stubs (no leading `/`); `"expertise:{kind}:{hash8}"` for expertise nodes |
| `Name` | `string` | `HypergraphNode.Name` | Short name of the symbol |
| `NodeType` | `string` | `HypergraphNode.NodeType` | `"Repo"`, `"Workspace"` (monorepo only), `"Package"`, `"Module"`, `"Class"`, `"Interface"`, `"Function"`, `"Method"`, `"Property"`, `"External"` |
| `Language` | `string` | `HypergraphNode.Language` | `"C#"`, `"PowerShell"` |
| `FilePath` | `string` | `HypergraphNode.FilePath` | Relative path from repo root |
| `LineStart` / `LineEnd` | `int` | `HypergraphNode.LineStart/LineEnd` | Source location |
| `Metadata["MemoryLayer"]` | `string` | Memory wrapper | `"structural"` or `"call_chain"` |
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
```

### 4.5 Metadata Key Registry

`AnnotationKeyRegistry` defines the well-known `Metadata` keys:

| Key | Type | Layer / Scope | Owner |
|---|---|---|---|
| `MemoryLayer` | `string` | All nodes and edges | Extraction Pipeline |
| `SemanticRelation` | `string` | All edges | Extraction Pipeline / Evolution Engine |
| `ContentHash` | `string` | In-repo nodes only (Structural, Call-Chain). **Not** on External boundary nodes. | Extraction Pipeline |
| `SinceCommit` | `string` | In-repo nodes and all edges. **Not** on External boundary nodes (§4.3). | Temporal Model |
| `SinceIndex` | `int` | In-repo nodes and all edges. **Not** on External boundary nodes (§4.3). | Temporal Model |
| `UntilCommit` | `string?` | In-repo nodes and all edges. **Not** on External boundary nodes. | Temporal Model / Incremental Sync |
| `UntilIndex` | `int?` | In-repo nodes and all edges. **Not** on External boundary nodes. | Temporal Model / Incremental Sync |
| `Summary` | `string` | Semantic (Topic nodes) | Evolution Engine |
| `MemberCount` | `int` | Semantic (Topic, SemanticCluster nodes) | Evolution Engine |
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
  ├── Ast.VectorDb        (existing — IVectorStore)
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
Agent/CLI                     ExtractionPipeline       Parsers            IMemoryGraphStore    IEmbeddingGenerator
  │                                │                      │                    │                      │
  │  ExtractAsync(repoRoot, opts)  │                      │                    │                      │
  │──────────────────────────────▶│                      │                    │                      │
  │                                │  Parse(repoRoot)     │                    │                      │
  │                                │─────────────────────▶│                    │                      │
  │                                │  HypergraphData      │                    │                      │
  │                                │◀─────────────────────│                    │                      │
  │                                │                      │                    │                      │
  │                                │  ── StructureBuilder ──                   │                      │
  │                                │  Build Repo/Pkg/Module/Class/Fn nodes     │                      │
  │                                │  + CONTAINS, DEPENDS_ON edges             │                      │
  │                                │  UpsertNodeAsync(node, version) ×N        │                      │
  │                                │──────────────────────────────────────────▶│                      │
  │                                │                      │                    │                      │
  │                                │  ── CallChainAnalyzer ──                  │                      │
  │                                │  Map MethodInvocation → CALLS edges       │                      │
  │                                │  Map Extends/Implements edges             │                      │
  │                                │  CreateEdgesAsync(edges)                  │                      │
  │                                │──────────────────────────────────────────▶│                      │
  │                                │                      │                    │                      │
  │                                │  ── SemanticIndexer ──                    │                      │
  │                                │  GenerateBatchEmbeddingsAsync(texts)      │                      │
  │                                │──────────────────────────────────────────────────────────────────▶│
  │                                │  float[][] embeddings                     │                      │
  │                                │◀──────────────────────────────────────────────────────────────────│
  │                                │  Store vectors + SIMILAR_TO edges         │                      │
  │                                │──────────────────────────────────────────▶│                      │
  │                                │                      │                    │                      │
  │                                │  LogAuditAsync(...)   │                    │                      │
  │                                │──────────────────────────────────────────▶│                      │
  │  ExtractionResult              │                      │                    │                      │
  │◀──────────────────────────────│                      │                    │                      │
```

### 6.2 Context Retrieval (Agent Query)

```
Agent                  MCP/CLI              RetrievalEngine        QueryPlanner     Ranker        BudgetController   ContextAssembler
  │                      │                       │                     │              │                 │                  │
  │  memory_query(req)   │                       │                     │              │                 │                  │
  │─────────────────────▶│                       │                     │              │                 │                  │
  │                      │  RetrieveAsync(req)   │                     │              │                 │                  │
  │                      │──────────────────────▶│                     │              │                 │                  │
  │                      │                       │  PlanQuery(req)     │              │                 │                  │
  │                      │                       │───────────────────▶│              │                 │                  │
  │                      │                       │  [modes]            │              │                 │                  │
  │                      │                       │◀───────────────────│              │                 │                  │
  │                      │                       │                     │              │                 │                  │
  │                      │                       │  ── per mode ──     │              │                 │                  │
  │                      │                       │  BFS / ANN / filter │              │                 │                  │
  │                      │                       │  → candidate nodes  │              │                 │                  │
  │                      │                       │                     │              │                 │                  │
  │                      │                       │  Rank(candidates, weights)         │                 │                  │
  │                      │                       │──────────────────────────────────▶│                 │                  │
  │                      │                       │  List<ScoredMemoryNode>            │                 │                  │
  │                      │                       │◀──────────────────────────────────│                 │                  │
  │                      │                       │                     │              │                 │                  │
  │                      │                       │  ApplyBudget(ranked, tokenBudget)  │                 │                  │
  │                      │                       │─────────────────────────────────────────────────────▶│                  │
  │                      │                       │  trimmed list                      │                 │                  │
  │                      │                       │◀─────────────────────────────────────────────────────│                  │
  │                      │                       │                     │              │                 │                  │
  │                      │                       │  Assemble(trimmed)  │              │                 │                  │
  │                      │                       │────────────────────────────────────────────────────────────────────────▶│
  │                      │                       │  contextMarkdown    │              │                 │                  │
  │                      │                       │◀────────────────────────────────────────────────────────────────────────│
  │                      │  RetrievalResult      │                     │              │                 │                  │
  │                      │◀──────────────────────│                     │              │                 │                  │
  │  RetrievalResult     │                       │                     │              │                 │                  │
  │◀─────────────────────│                       │                     │              │                 │                  │
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
  │                      │                       │  CreateEdgeAsync(APPLIES_TO)                │
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
Git hook / Watcher      IncrementalSync         GitDiffDriver      ExtractionPipeline    IMemoryGraphStore     DecayService
  │                          │                       │                    │                    │                    │
  │  ApplyDiffAsync          │                       │                    │                    │                    │
  │  (repo, from, to)        │                       │                    │                    │                    │
  │─────────────────────────▶│                       │                    │                    │                    │
  │                          │  git diff --name-     │                    │                    │                    │
  │                          │  status from..to      │                    │                    │                    │
  │                          │──────────────────────▶│                    │                    │                    │
  │                          │  [(status, path)]     │                    │                    │                    │
  │                          │◀──────────────────────│                    │                    │                    │
  │                          │                       │                    │                    │                    │
  │                          │  ── per batch of modified files ──         │                    │                    │
  │                          │  ExtractFilesAsync(repo, paths)            │                    │                    │                    │
  │                          │──────────────────────────────────────────▶│                    │                    │
  │                          │  new nodes/edges      │                    │                    │                    │
  │                          │◀──────────────────────────────────────────│                    │                    │
  │                          │                       │                    │                    │                    │
  │                          │  Diff old vs new      │                    │                    │                    │
  │                          │  UpsertNodeAsync (added/changed)          │                    │                    │
  │                          │  RetireNodeAsync (removed)                │                    │                    │
  │                          │──────────────────────────────────────────────────────────────▶│                    │
  │                          │                       │                    │                    │                    │
  │                          │  ── per deleted file ──                   │                    │                    │
  │                          │  RetireNodeAsync(file nodes, untilIndex)  │                    │                    │
  │                          │──────────────────────────────────────────────────────────────▶│                    │
  │                          │                       │                    │                    │                    │
  │                          │  Decay expertise targeting changed nodes  │                    │                    │
  │                          │─────────────────────────────────────────────────────────────────────────────────────▶│
  │                          │  DecayResult           │                    │                    │                    │
  │                          │◀─────────────────────────────────────────────────────────────────────────────────────│
  │                          │                       │                    │                    │                    │
  │                          │  LogAuditAsync(...)   │                    │                    │                    │
  │                          │──────────────────────────────────────────────────────────────▶│                    │
  │  SyncResult              │                       │                    │                    │                    │
  │◀─────────────────────────│                       │                    │                    │                    │
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
| **Local** | `SqliteGraphStore` (SQLite + FTS5 + sqlite-vss) | In-process HNSW (via sqlite-vss) | Zero — single file on disk | Solo developer, CI pipeline |
| **Team** | `Neo4jMemoryGraphStore` (Neo4j via `Ast.GraphDb`) | Qdrant (via `Ast.VectorDb`) | `docker-compose.yml` (existing in `src/ast/`) | Shared team graph, server deployment |

Backend selection is via DI configuration:

```csharp
// Local mode
services.AddMemoryGraph(options => {
    options.Backend = MemoryBackend.Sqlite;
    options.SqlitePath = "~/.ast-memory/graph.db";
});

// Team mode
services.AddMemoryGraph(options => {
    options.Backend = MemoryBackend.Neo4j;
    options.Neo4jUri = "bolt://localhost:7687";
    options.QdrantEndpoint = "localhost:6334";
});
```

---

## 8. Cross-Document Alignment Notes

This section tracks known discrepancies with sibling documents. All three
siblings now exist and have been read for this iteration. Each entry is either
resolved (with the canonical answer) or flagged for the next iteration.

### 8.1 Alignment with `tech-spec.md` (Iteration 23)

Most conflicts between this document and `tech-spec.md` are **resolved**.
Tech-spec §12.2 confirms general alignment. The following table includes
both long-resolved items and newly identified divergences.

| # | Topic | Resolution |
|---|---|---|
| A1 | Multi-agent scope | Resolved: tech-spec O6 excludes G-Memory-style coordinated protocols, not shared storage. §1.2 of this doc explains the per-repo shared graph model. |
| A2 | Ast.Cli modification | Aligned: tech-spec §1.1 agrees on additive `config.AddBranch("memory", ...)`. |
| A3 | Embedding model | Aligned: tech-spec HC5, D1, §12.2 all confirm `nomic-embed-text` default. Matches codebase defaults in `EmbeddingGeneratorOptions.ModelName` and `EmbeddingConfiguration.ModelName`. |
| A4 | Edge filtering | Aligned: tech-spec §7.2 uses `Metadata["SemanticRelation"]` as sole filter, matching §2.2 of this doc. |
| A5 | Temporal model | Aligned: tech-spec §7.4 uses `commit_index` integers with `VersionRange`, matching §4.3 of this doc. |
| A6 | Scoring weights | Aligned: tech-spec §8.3 uses the canonical 5-signal weighted model (0.30/0.25/0.20/0.15/0.10) from §2.4 of this doc. |
| A7 | Composition wrappers | Aligned: tech-spec HC8, §4 confirm `MemoryNode`/`MemoryEdge` use composition (`Inner` property), not inheritance, matching §4.1 of this doc. |
| A8 | Token budget | Aligned: tech-spec HC1 clarifies 32K ceiling, 8K default — matching §2.4 of this doc. |
| A9 | ExpertiseEntry field names | **Resolved.** Tech-spec §7.1 uses `title`, `body`, `source_session`. This document (§4.2) uses `Title`, `Body`, `SourceAgent` (with `Scope` and `Evidence`). The field name difference (`source_session` vs `SourceAgent`) is documentation shorthand in tech-spec — both refer to the agent session identifier. `SourceAgent` is canonical. |
| A10 | ExpertiseEntry entity vs node type | **Resolved.** Tech-spec §7.1 lists `ExpertiseEntry` under "Node Types" for schema reference convenience. Canonically, `ExpertiseEntry` is **not** a `HypergraphNode` — it is a first-class entity stored in its own `expertise` table (§4.2, §4.4) and linked to code nodes via `APPLIES_TO` edges. Tech-spec's presentation is a summary shorthand, not a contradiction. |
| A11 | External node Metadata keys | **Resolved.** Tech-spec §7.1 lists External nodes with `package`, `fqn` properties. This document (§4.2) explicitly specifies `Metadata["Package"]` and `Metadata["QualifiedName"]` keys for External nodes. |
| A12 | Temporal-model External-node carve-out | **Resolved.** Tech-spec S4 says "every node and edge carries a `VersionRange`" but External boundary nodes are timeless (no VCS presence). This document (§4.3) explicitly states: External boundary nodes are exempt from `VersionRange`; edges to/from External nodes are versioned normally from the in-repo endpoint's perspective. Temporal queries include External nodes via `WHERE since_index IS NULL` (External) or range-match (versioned). The SQLite schema (§4.4) makes `since_sha` and `since_index` nullable on the `nodes` table only. |
| A13 | Node ID leading-slash format | **Resolved (iteration 28).** Architecture §4.2 previously showed `"{relativePath}::{qualifiedName}"` without leading `/`. E2e-scenarios (line 22) explicitly state "leading `/` always present" for in-repo nodes, and all Gherkin tables use `/src/ast/...` format. Tech-spec §7.1 also omits the leading `/` in its ID format summary (e.g., `src/ast/...` without `/`). Architecture §4.2 now reads `"/{relativePath}::{qualifiedName}"` with explicit leading `/`; this is canonical and governs over both tech-spec and e2e-scenarios. External and expertise node IDs do not start with `/`. |
| A14 | `LastTouched` metadata type | **Resolved (iteration 28).** Architecture §4.5 previously typed `LastTouched` as `int`. Tech-spec §7.3 types it as `string (ISO-8601)` with description "Wall-clock time of last extraction (informational)." Architecture §4.5 now types it as `string` (ISO-8601), matching the tech-spec. Wall-clock timestamps are informational and not used for graph filtering (§4.3). |
| A15 | `MemoryNode.Version` nullability | **Resolved (iteration 28).** Architecture §4.1 previously declared `VersionRange Version { get; }` (non-nullable) on `MemoryNode`. Since External boundary nodes are timeless (§4.3, §4.2), `MemoryNode.Version` is now `VersionRange? Version` (nullable) to accommodate External nodes wrapped as `MemoryNode`. |
| A16 | Expertise hash input in tech-spec | **Resolved (iteration 28).** Tech-spec §7.1 uses `SHA-256(Content + TargetNodeId)` for the expertise-entry `hash8`. Architecture §4.2 canonically specifies `SHA-256(Title + Body + TargetNodeId)` (reflecting the `Title`/`Body` split per tech-spec D5). Architecture governs; tech-spec's `Content` is stale shorthand for `Title + Body`. |

### 8.2 Alignment with `implementation-plan.md` (Current Iteration)

The implementation-plan has been **fully aligned** and now uses
canonical vocabulary defined in this document. Its header states: "when
literal names or values here conflict with `architecture.md`, follow
`architecture.md`."

**Resolved divergences (no longer present in implementation-plan):**

The following categories of conflicts, previously catalogued in earlier
iterations, have been corrected in the implementation-plan:

- **Naming:** Uses `ExpertiseEntry` (not `ExpertiseNote`), `IMemoryGraphStore`
  (not `IMemoryStore`), `ExpertiseKind` (not `PatternType`),
  `Neo4jMemoryGraphStore` (not `Neo4jMemoryStore`).
- **Composition:** `MemoryNode` contains `HypergraphNode Inner` (not
  inheritance). `MemoryEdge` contains `Hyperedge Inner` (not inheritance).
- **`VersionRange` nullability:** `MemoryNode.Version` is `VersionRange?`
  (nullable for External boundary nodes).
- **ExpertiseEntry fields:** Uses `Title` + `Body` (not single `Content`),
  with `Scope`, `Evidence`, `Archived`, `SourceAgent`.
- **Method signatures:** `RecordExpertiseAsync(targetNodeId, kind, title,
  body, sourceAgent, scope?, evidence?)` matches §5.3.
- **Interface methods:** `IMemoryGraphStore` declares all 12 methods per §5.1
  (including `RetireEdgeAsync`, `ArchiveExpertiseAsync`,
  `GetArchivedExpertiseAsync`, `UpdateEdgeMetadataAsync`).
- **Interface placement:** `ApplyDiffAsync` on `IIncrementalSync` with
  correct `(repoRoot, fromCommit, toCommit)` signature per §5.4.
- **Scoring weights:** Uses canonical 0.30/0.25/0.20/0.15/0.10 per §2.4.
- **Constants:** Uses `DecayHalfLifeDays=1095`, `TokenBudgetDefault=8000`,
  `TokenBudgetCeiling=32000` per §2.4.
- **Node ID format:** Uses `"/{relativePath}::{qualifiedName}"` with leading
  `/` per §4.2.
- **Storage schema:** Uses `archived` column in `expertise` table per §4.4.
- **Project placement:** MCP tools in `Ast.Memory/Mcp/`, CLI in
  `Ast.Memory/Cli/`, no files created in `Ast.GraphDb/`.

**Remaining presentational gap (1 item):**

This is minor documentation shorthand in the implementation-plan that does
not affect correctness. The implementation-plan's header defers to this
document on all conflicts.

| # | impl-plan says | Canonical (this doc) | Governing § |
|---|---|---|---|
| R-1 | `MemoryEdge.Version` property listed as `VersionRange Version` without explicit non-nullable annotation | `VersionRange Version` is non-nullable (all edges carry versioning, per §4.3) — impl-plan is correct but could be more explicit | §4.1, §4.3 |

### 8.3 Alignment with `e2e-scenarios.md` (Current Iteration)

The e2e-scenarios document covers **13 features with 82 Gherkin scenarios**.
As of its current iteration, **all 10 previously-catalogued divergences have
been resolved**. The e2e-scenarios document now uses canonical vocabulary
throughout: correct edge names (`CALLS`, `EXTENDS`, `IMPLEMENTS`,
`DEPENDS_ON`, `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM`), qualified node
IDs (`"/{relativePath}::{qualifiedName}"`), single-colon `ext:` prefix for
External boundary nodes, separate `Title`/`Body` fields for
`RecordExpertiseAsync`, and correct scenario descriptions. Two scenarios
previously flagged as out-of-scope (cross-service RPC and cross-repo memory
transfer) have been removed or replaced with in-scope content.

**Historical record** (for traceability — these are all resolved):

| # | Original topic | Original scenario | Resolution |
|---|---|---|---|
| E2E-1 | `called_by` label in result table | 4.1 | **Resolved.** Scenario 4.1 now uses `APPLIES_TO` edge for expertise write-back. No `called_by` label remains. |
| E2E-2 | `direct_call` edge presentation | 2.x | **Resolved.** Scenarios use canonical `CALLS` with `Confidence=1.0`. |
| E2E-3 | `polymorphic` edge presentation | 2.x | **Resolved.** Scenarios use canonical `CALLS` with `Confidence=0.5–0.7`. |
| E2E-4 | `recursive` edge presentation | 2.x | **Resolved.** Scenarios use canonical `CALLS` with self-referencing `NodeIds`. |
| E2E-5 | `service_call` for cross-service RPC | 2.7 | **Resolved.** Scenario 2.7 now covers External boundary nodes for dependency calls — no `service_call` edge type. |
| E2E-6 | Unqualified symbol IDs | Various | **Resolved.** All scenarios use canonical `"/{relativePath}::{qualifiedName}"` format per §4.2. |
| E2E-7 | `ext::` prefix for boundary nodes | 2.4 | **Resolved.** Scenarios use canonical `"ext:{package}:{qualifiedName}"` (single colon) per §4.2. |
| E2E-8 | Cross-repo memory transfer | 6.4 | **Resolved.** Scenario 6.4 now covers ExpertiseRecall retrieval mode — no cross-repo content. |
| E2E-9 | Concurrent write conflict errors | 3.4 | **Resolved.** Scenario 3.4 now covers SemanticSearch retrieval — no concurrent-write conflict scenario. |
| E2E-10 | Expertise `content` parameter | 4.x, 6.x | **Resolved.** Scenarios use separate `Title` and `Body` parameters matching `RecordExpertiseAsync` signature in §5.3. |

### 8.4 Net Assessment

- **tech-spec ↔ architecture:** Aligned on all structural, naming, and data-model points. Sixteen alignment items (A1–A16) tracked, all resolved. Tech-spec §12.2 confirms "No remaining conflicts." Tech-spec's own header references architecture.md at "iteration 25" — this is stale iteration metadata in the tech-spec's header; the alignment conclusions (A1–A16) remain valid and the tech-spec (iteration 23) has not changed since. Three stale presentation items remain in the tech-spec (not conflicts — this document governs): (1) §7.1 line 173 shows ID format without leading `/` (governed by A13); (2) §7.1 line 171 uses `SHA-256(Content + TargetNodeId)` instead of `SHA-256(Title + Body + TargetNodeId)` (governed by A16); (3) §8.4 uses column names `valid_from_idx`/`valid_until_idx` while architecture §4.4 uses `since_index`/`until_index` (architecture governs — tech-spec §8.4 is a quick-reference, not normative).
- **implementation-plan ↔ architecture:** Fully aligned on vocabulary, composition model, interface signatures, scoring weights, constants, node-ID format, and project placement. The implementation-plan correctly specifies both the `ContentHash` formula (SHA-256 of Title+Body for dedup) and the `Id` hash8 formula (SHA-256 of Title+Body+TargetNodeId for uniqueness) in its model definitions and test scenarios. One minor presentational gap remains (R-1 in §8.2: `MemoryEdge.Version` non-nullable annotation), not affecting correctness. The implementation-plan's own header defers to this document on all conflicts.
- **e2e-scenarios ↔ architecture:** All 10 previously-catalogued divergences (E2E-1 through E2E-10) are now **resolved** in the current iteration of e2e-scenarios.md. The e2e-scenarios document uses canonical vocabulary throughout: correct edge names, qualified node IDs with leading `/`, single-colon `ext:` prefix, separate `Title`/`Body` parameters, and in-scope-only scenario content. The e2e-scenarios header (lines 17–23) confirms this alignment and notes that prior architecture.md divergence catalogs were stale — this section has been updated accordingly. No remaining divergences exist between this document and e2e-scenarios.md.