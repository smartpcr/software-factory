# Agent Memory Architecture

## 1. Overview

### 1.1 Purpose

This document specifies the architecture for a **graph-based agent memory
system** that builds structured expertise from repository source code. The
system constructs a multi-layer knowledge graph from two primary inputs —
**top-down repository structure** (packages, modules, classes, functions) and
**call-chain context** (invocation paths, data flow, dependency edges) — and
exposes that graph as retrievable, evolving memory that coding agents consume
to produce higher-quality completions, refactors, and reviews.

The core value proposition: agents that interact with the same codebase across
sessions accumulate institutional knowledge — patterns discovered, pitfalls
flagged, architectural context mapped — so that every subsequent session starts
with richer understanding than the one before.

### 1.2 Relationship to Existing Infrastructure

This system builds on top of the existing **Code Intelligence** stack in
`src/ast/`, which already provides:

| Existing project | Capability | Reuse strategy |
|---|---|---|
| **Ast.Core** (`HypergraphNode`, `Hyperedge`, `HyperedgeType`) | AST node/edge models, repository processor | Extend models with version-range and annotation fields; wrap `RepositoryProcessor` as one extraction adapter. |
| **Ast.GraphDb** (`IGraphStore`, `Neo4jGraphStore`) | Graph persistence with Neo4j backend | Extend `IGraphStore` with temporal query methods and layer-aware traversal; add SQLite adapter for local mode. |
| **Ast.Embedding** (`IEmbeddingGenerator`, `CodeEmbeddingService`) | Vector embedding generation | Reuse directly for Semantic layer embeddings; extend `HypergraphNode.GetEnhancedEmbeddingText()` for richer input. |
| **Ast.Query** (`ICodeQuery`, `HybridQueryService`) | Fluent graph + vector search | Extend with memory-aware retrieval modes (EXPERTISE_RECALL, HYBRID); add token-budgeted context assembly. |
| **Ast.VectorDb** | Qdrant-based ANN index | Reuse for Semantic layer vector search; add SQLite-vss adapter for local mode. |
| **Ast.Lsp** | Language Server Protocol integration | Optional enrichment — provides type-resolved call edges when an LSP is running. |
| **CodeIntelligence** | Analyzers, detectors, relationship graph builder | Consume its `RelationshipGraphBuilder` output as a source of call-chain and structural edges. |

See the [Code Intelligence Master Architecture](../../CODE_INTELLIGENCE_ARCHITECTURE.md)
for the full existing system design.

### 1.3 Scope

| In scope | Out of scope |
|---|---|
| Static analysis extraction (AST, call graph, imports) | IDE integration surfaces (UI, keybindings) |
| Graph construction and persistent storage | LLM inference hosting |
| Context retrieval for agent prompts | User authentication and billing |
| Expertise write-back and consolidation | Runtime / dynamic analysis |
| Incremental updates on commit | Multi-repo federated graphs |

### 1.4 Sibling Documents

This architecture document is part of a four-document planning set. Each
document owns a distinct concern; shared concepts are referenced by name.

| Document | Owns |
|---|---|
| **architecture.md** (this file) | Components, data model, interfaces, sequence flows |
| [tech-spec.md](tech-spec.md) | Problem statement, scope/non-goals, hard constraints, risks, success criteria |
| [implementation-plan.md](implementation-plan.md) | Phased work items, deliverables, dependencies, evaluation harness |
| [e2e-scenarios.md](e2e-scenarios.md) | BDD Gherkin scenarios for each feature |

**Layer naming alignment:** The tech-spec uses four *trust-separated* layer
names (Fact, Derived, Interaction, Temporal). This architecture uses four
*purpose-separated* layer names (Structural, Call-Chain, Semantic, Expertise).
The mapping: Fact ≈ Structural + Call-Chain; Derived ≈ Semantic; Interaction ≈
Expertise; Temporal is an orthogonal concern (version ranges on all layers).
Both views are valid projections of the same graph; the implementation uses a
single `layer` tag per node/edge so either lens can be applied at query time.

---

## 2. Research Foundations

The design is grounded in the graph-based agent memory taxonomy from Yang et
al., 2026 (*Graph-based Agent Memory: Taxonomy, Techniques, and Applications*,
[arXiv:2602.05665](https://arxiv.org/abs/2602.05665)), which identifies four
lifecycle phases — **Extraction**, **Storage**, **Retrieval**, and
**Evolution** — and catalogs techniques under each. The traceability matrix
below maps specific research findings to the design decisions in this
architecture.

### 2.1 Research Traceability Matrix

| ID | Research source | Finding adopted | Design decision | Component(s) | Adaptation / limitation |
|---|---|---|---|---|---|
| R-1 | MAGMA (arXiv:2601.03236) | Separating semantic, temporal, causal, and entity graphs prevents retrieval signal entanglement. | Four orthogonal graph layers — Structural, Call-chain, Semantic, Expertise — each with distinct edge types and independent query paths. | Graph Store, Retrieval Engine | We replace MAGMA's "temporal graph" with commit-range versioning on all layers, and "causal graph" with explicit call-chain edges, since code causality maps naturally to invocation. |
| R-2 | HiAgent (ACL '25, aclanthology.org/2025.acl-long.1575) | Hierarchical working memory reduces token cost for long-horizon agent tasks by letting the agent operate at different abstraction levels. | The Structural layer mirrors the repo containment tree (Repo → Package → Module → Symbol). The Budget Controller collapses children into parent summaries when tokens are scarce. | Extraction Pipeline (Structure Builder), Context Assembler | HiAgent targets general tasks; we specialize the hierarchy to source-code containment which is deterministic, not LLM-inferred. |
| R-3 | G-Memory (arXiv:2506.07398) | Hierarchical memory with tracing supports multi-agent coordination by scoping memory visibility. | Retrieval queries accept a `scope` parameter (qualified-name prefix) that limits traversal to a subtree, enabling per-module or per-package focus. | Retrieval Engine (Query Planner) | We do not implement multi-agent access control; scope is advisory, not enforced. |
| R-4 | Zep / Graphiti (arXiv:2501.13956) | Temporal knowledge graphs with timestamped edges enable cross-session reasoning and staleness detection. | Every node and edge carries `version_range {since, until}` (commit SHAs) and `last_touched` (timestamp). Stale edges are flagged and excluded from default retrieval. | Graph Store, Incremental Sync, Evolution Engine | Zep uses wall-clock time; we use commit SHAs as the temporal axis since code changes are commit-atomic. |
| R-5 | Mem0 (arXiv:2504.19413) | Production-ready agent memory requires write-back: agents that record learnings produce compounding value over sessions. | The Evolution Engine accepts agent-authored ExpertiseEntry records, persists them as Expertise-layer nodes, and links them to code via APPLIES_TO edges. | Evolution Engine | Mem0 stores flat key-value memories; we store structured expertise nodes with typed edges, confidence scores, and evidence citations. |
| R-6 | Reflexion (NeurIPS '23) | Verbal reinforcement learning — agents that reflect on past failures improve on subsequent attempts. | Cross-session learning flow: agents write back pitfalls and insights which are surfaced in future sessions via the Expertise layer. Consolidation merges overlapping learnings. | Evolution Engine, Retrieval Engine | Reflexion operates within a single episode; we extend the feedback loop across sessions via persistent graph storage. |
| R-7 | GraphRAG (arXiv:2404.16130) | Community detection + LLM summarization over graph partitions produces useful high-level summaries for RAG. | Community Summarization in the Evolution Engine detects tightly-coupled clusters via graph partitioning and generates Topic nodes with prose summaries. | Evolution Engine, Graph Store (Semantic layer) | GraphRAG summarizes document graphs; we summarize code structure + call-chain graphs, which are denser and more deterministic. |
| R-8 | SGMem (arXiv:2509.21212) | Sentence-graph memory supports efficient long-term conversational agents by linking utterance fragments. | Semantic layer uses SIMILAR_TO edges between code nodes whose embeddings are within a cosine threshold, enabling "find related code" queries without free-text sentences. | Extraction Pipeline (Semantic Indexer), Retrieval Engine (Vector Searcher) | SGMem links natural-language sentences; we link code symbols using code-specific embedding models. |
| R-9 | MemGPT (arXiv:2310.08560) | Treating memory as a virtual address space with explicit read/write operations gives the agent fine-grained control. | The Retrieval Engine exposes discrete retrieval modes (STRUCTURAL_DRILL, CALL_TRACE, SEMANTIC_SEARCH, EXPERTISE_RECALL, HYBRID) that the agent or its orchestrator can select explicitly. | Retrieval Engine (Query Planner) | We do not implement MemGPT's paging/eviction model; we use a token Budget Controller instead. |

---

## 3. Components

### 3.0 Integration Map — New vs. Existing Projects

The agent memory system is delivered as a mix of new projects and extensions to
existing ones. The `src/ast/` directory is the home for all code intelligence
work.

| Component (architecture) | Project | Status | Notes |
|---|---|---|---|
| Extraction Pipeline — Language Parser | `Ast.Core` / `Ast.Csharp` / `Ast.PowerShell` | **Existing** — extend | Add `version_range` to `HypergraphNode`; wrap parsers behind a unified `ILanguageParser` interface. |
| Extraction Pipeline — Structure Builder | New: `Ast.Memory.Extraction` | **New** | Consumes `HypergraphNode` stream, builds Structural-layer containment hierarchy. |
| Extraction Pipeline — Call-Chain Analyzer | New: `Ast.Memory.Extraction` | **New** | Reads `Hyperedge` of type `MethodInvocation`, `ConstructorInvocation`, etc., emits typed CALLS/OVERRIDES edges. |
| Extraction Pipeline — Semantic Indexer | `Ast.Embedding` / `CodeEmbeddingService` | **Existing** — reuse | Produces dense vectors via `IEmbeddingGenerator`; attaches to nodes. |
| Extraction Pipeline — Fact Normalizer | New: `Ast.Memory.Extraction` | **New** | Dedup, stable ID assignment, content hashing, version-range stamping. |
| Graph Store | `Ast.GraphDb` (`IGraphStore`, `Neo4jGraphStore`) | **Existing** — extend | Add `ApplyDiffAsync`, `TraverseAsync`, temporal range queries. Add `SqliteGraphStore` for local mode. |
| Vector Index | `Ast.VectorDb` | **Existing** — reuse | Qdrant for team mode; add in-process HNSW for local mode. |
| Retrieval Engine | `Ast.Query` (`ICodeQuery`, `HybridQueryService`) | **Existing** — extend | Add retrieval modes (STRUCTURAL_DRILL, CALL_TRACE, EXPERTISE_RECALL, HYBRID), Budget Controller, Ranker. |
| Context Assembler | New: `Ast.Memory.Context` | **New** | Markdown context-block formatter with hierarchy compression and dedup. |
| Evolution Engine | New: `Ast.Memory.Evolution` | **New** | Expertise write-back, LLM-powered consolidation, confidence decay. |
| Incremental Sync | New: `Ast.Memory.Sync` | **New** | File-system watcher + git-diff driver, triggers extraction pipeline. |
| Agent API / MCP | New: `Ast.Memory.Api` | **New** | Exposes retrieve + write-back as MCP tool or gRPC/HTTP service. |

```
┌────────────────────────────────────────────────────────────────────────┐
│                          Agent Memory System                          │
│                                                                       │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────────────────┐  │
│  │  Extraction   │──▶│  Graph Store  │◀──│  Retrieval Engine        │  │
│  │  Pipeline     │   │  (4 layers)  │   │  (query planner + fetch) │  │
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
         │  git events / file-system watch          │  assembled context
         │                                          ▼
    ┌─────────┐                              ┌─────────────┐
    │  Source  │                              │  Agent / LLM │
    │  Repo   │                              │  Consumer    │
    └─────────┘                              └─────────────┘
```

### 3.1 Extraction Pipeline

**Responsibility:** Parse source code into typed graph nodes and edges across
the four graph layers.

| Sub-component | Input | Output | Notes |
|---|---|---|---|
| **Language Parser** | Source files | Unresolved AST facts (symbols, scopes, imports) | One parser adapter per language. Uses tree-sitter grammars for breadth; LSP for type-resolved facts where available. |
| **Structure Builder** | AST facts | Structural-layer nodes + `CONTAINS` / `IMPORTS` edges | Builds the repo → package → module → symbol hierarchy. |
| **Call-Chain Analyzer** | AST facts + resolved types | Call-chain-layer `CALLS` / `OVERRIDES` / `IMPLEMENTS` edges | Static call graph. Edges carry arity, argument types, and whether the call is conditional/async. |
| **Semantic Indexer** | Docstrings, comments, symbol names | Semantic-layer embedding vectors attached to nodes | Produces dense vector embeddings (e.g., code-embedding model) for similarity retrieval. |
| **Fact Normalizer** | Raw nodes/edges from above | Canonical graph facts with `version_range` and `content_hash` | De-duplicates, assigns stable IDs, attaches commit metadata. |

**Key design choice — existing parsers first:** The existing `Ast.Core`
`RepositoryProcessor` and `Ast.Csharp` / `Ast.PowerShell` parsers already
extract `HypergraphNode` and `Hyperedge` records with `HyperedgeType` values
covering `MethodInvocation`, `ConstructorInvocation`, `UsingDirective`,
`Implements`, `Extends`, and 30+ other edge types. The Extraction Pipeline
wraps these as its primary adapter. Tree-sitter adapters can be added for
languages not yet covered (TypeScript, Python, Go) without changing the core
pipeline. The LSP adapter (via `Ast.Lsp`) is optional and additive — it
enriches the graph with type-resolved edges when a language server is running.
The pipeline must never block on an LSP.

### 3.2 Graph Store

**Responsibility:** Persist the four graph layers, serve traversal and
pattern queries, manage versioned snapshots.

The store is logically a single property graph partitioned into four
**layers**. Each layer has distinct node labels and edge types but shares the
same node ID namespace so cross-layer joins are natural.

#### Layer definitions

| Layer | Node labels | Edge types | Purpose |
|---|---|---|---|
| **Structural** | `Repo`, `Package`, `Module`, `Class`, `Function`, `Variable` | `CONTAINS`, `IMPORTS`, `RE_EXPORTS` | Top-down containment and dependency hierarchy. |
| **Call-Chain** | (shares symbol nodes from Structural) | `CALLS`, `CALLED_BY`, `OVERRIDES`, `IMPLEMENTS`, `INSTANTIATES` | Directed invocation graph. Captures who-calls-whom and with what signature. |
| **Semantic** | `SemanticCluster`, `Topic` | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` | Grouping by embedding similarity. Enables "find all code related to X" queries. |
| **Expertise** | `Pattern`, `Pitfall`, `Preference`, `Insight` | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` | Agent-authored knowledge. Grows over time through the Evolution Engine. |

**Storage backend:** The existing `Neo4jGraphStore` (in `Ast.GraphDb`) handles
the team/CI deployment. For local/CI use, a new `SqliteGraphStore` will
implement `IMemoryGraphStore` using SQLite + recursive CTEs for graph traversal.
The existing `Ast.VectorDb` Qdrant integration handles vector search in team
mode; an in-process HNSW index (via `sqlite-vss` or `usearch`) is provided for
local mode.

### 3.3 Retrieval Engine

**Responsibility:** Accept a natural-language or structured query from the
agent, plan the retrieval strategy, traverse the graph, and return ranked
memory fragments.

| Sub-component | Role |
|---|---|
| **Query Planner** | Classifies the query intent (structural lookup, call-path trace, semantic search, expertise recall) and selects which graph layers to traverse. Inspired by MAGMA's policy-guided traversal. |
| **Graph Traverser** | Executes Cypher / Gremlin-style traversal on the selected layers. Supports depth-limited BFS, shortest-path, and community-walk strategies. |
| **Vector Searcher** | Runs ANN search over the Semantic layer embeddings. Returns top-k similar nodes. |
| **Ranker** | Merges results from Graph Traverser and Vector Searcher. Applies scoring signals: structural proximity, call-chain distance, recency (temporal decay), expertise confidence, and embedding cosine similarity. |
| **Budget Controller** | Enforces a token budget. Prunes or summarizes fragments to fit the agent's context window. Uses the structural hierarchy to substitute a parent summary when child details exceed budget. |

**Retrieval modes** (selected by the Query Planner):

| Mode | Technique | Example query |
|---|---|---|
| `STRUCTURAL_DRILL` | Top-down BFS from repo root | "What does the `auth` package contain?" |
| `CALL_TRACE` | Forward/reverse DFS on call-chain layer | "What calls `validateToken`?" / "What does `login()` invoke?" |
| `SEMANTIC_SEARCH` | Vector ANN + cluster expansion | "Find code related to rate limiting" |
| `EXPERTISE_RECALL` | Pattern/pitfall lookup filtered by `APPLIES_TO` scope | "Known pitfalls for the payments module?" |
| `HYBRID` | Fan-out across ≥2 modes, merge via Ranker | "Explain the authentication flow and flag any known issues" |

### 3.4 Context Assembler

**Responsibility:** Transform ranked memory fragments into a structured
prompt section ready for the consuming LLM.

The assembler applies three transformations:

1. **Hierarchy compression.** If a parent node and all its children are
   selected, collapse to the parent summary + child signature list.
2. **Deduplication.** Identical code spans reached via different graph paths
   are emitted once, with a footnote listing the paths.
3. **Formatting.** Output a deterministic Markdown document with sections:
   `## Structure`, `## Call Graph`, `## Related Code`, `## Known Expertise`.
   The agent system prompt specifies which sections to include.

### 3.5 Evolution Engine

**Responsibility:** Accept agent-authored learnings and merge them into the
Expertise layer, consolidating, de-duplicating, and versioning knowledge
over time.

| Operation | Trigger | Description |
|---|---|---|
| **Write-back** | Agent emits a structured annotation (pattern, pitfall, preference). | Creates an Expertise node, links it via `APPLIES_TO` edges to the structural/call-chain nodes the annotation references. |
| **Consolidation** | Scheduled (e.g., post-PR-merge) or manual. | LLM-powered pass that reads overlapping expertise nodes and merges them: confirms, supersedes, or retracts. Produces `SUPERSEDES` edges. |
| **Decay** | On every retrieval. | Expertise nodes whose `APPLIES_TO` targets have been deleted or substantially rewritten have their confidence scores reduced. Nodes below a threshold are archived (soft-deleted). |
| **Community Summarization** | On structural change exceeding a threshold. | Inspired by GraphRAG community summaries. Detects tightly coupled clusters in the structural + call-chain layers and generates or updates a prose summary stored as a `Topic` node in the Semantic layer. |

### 3.6 Incremental Sync

**Responsibility:** Keep the graph current with the repository without
full re-extraction.

**Mechanism:**
1. On each commit (or file-save in watch mode), diff the changed files.
2. For each changed file, re-run the Extraction Pipeline on that file only.
3. Compute a node/edge diff against the current graph snapshot.
4. Apply inserts, updates, and tombstones. Shift `version_range.end` on
   deleted nodes rather than hard-deleting them.
5. Propagate invalidation: if a function signature changed, mark downstream
   `CALLS` edges as `stale` until the call-chain analyzer re-confirms them.

**Performance target:** Incremental sync for a single-file change completes
in < 2 seconds for repositories up to 500 K LOC.

---

## 4. Data Model

### 4.1 Core Entities

The data model extends the existing `HypergraphNode` and `Hyperedge` from
`Ast.Core` with versioning, annotation, and layer-tagging fields. The
extensions are additive — existing node/edge shapes remain valid.

```
┌─────────────────────────────────────────────────────────────────┐
│                         Node (base)                             │
├─────────────────────────────────────────────────────────────────┤
│  id             : UUID          — stable across renames (via    │
│                                    content_hash fallback)       │
│  qualified_name : string        — e.g., "auth.jwt.validate"    │
│  kind           : NodeKind      — enum (see below)             │
│  layer          : LayerTag[]    — which layers this node is in  │
│  file_path      : string | null — source location              │
│  span           : {start, end}  — byte range in file           │
│  content_hash   : bytes         — hash of the source span       │
│  summary        : string        — LLM-generated one-liner      │
│  version_range  : {since, until} — commit SHA range             │
│  last_touched   : timestamp     — last extraction time          │
│  embedding      : float[768]    — dense vector (Semantic layer) │
│  metadata       : JSON          — language-specific extras      │
└─────────────────────────────────────────────────────────────────┘

NodeKind enum:
  REPO | PACKAGE | MODULE | CLASS | FUNCTION | METHOD | VARIABLE |
  INTERFACE | ENUM | TYPE_ALIAS |
  SEMANTIC_CLUSTER | TOPIC |
  PATTERN | PITFALL | PREFERENCE | INSIGHT
```

```
┌─────────────────────────────────────────────────────────────────┐
│                         Edge (base)                             │
├─────────────────────────────────────────────────────────────────┤
│  id             : UUID                                          │
│  source         : NodeID                                        │
│  target         : NodeID                                        │
│  relation       : RelationType   — enum (see below)            │
│  layer          : LayerTag                                      │
│  weight         : float          — confidence / frequency      │
│  properties     : JSON           — relation-specific payload   │
│  version_range  : {since, until} — commit SHA range             │
│  stale          : boolean        — pending re-confirmation     │
└─────────────────────────────────────────────────────────────────┘

RelationType enum:
  — Structural: CONTAINS | IMPORTS | RE_EXPORTS
  — Call-chain: CALLS | CALLED_BY | OVERRIDES | IMPLEMENTS | INSTANTIATES
  — Semantic:   SIMILAR_TO | BELONGS_TO_CLUSTER
  — Expertise:  APPLIES_TO | SUPERSEDES | DERIVED_FROM
```

### 4.2 Edge Property Schemas

Each `RelationType` defines the shape of its `properties` JSON:

| Relation | Properties | Example |
|---|---|---|
| `CALLS` | `{ arg_count: int, is_async: bool, is_conditional: bool, call_site: Span }` | `{ arg_count: 2, is_async: true, is_conditional: false, call_site: {start:120, end:145} }` |
| `IMPORTS` | `{ alias: string?, is_wildcard: bool, is_type_only: bool }` | `{ alias: "jwt", is_wildcard: false, is_type_only: false }` |
| `APPLIES_TO` | `{ confidence: float, scope: "exact" \| "subtree" }` | `{ confidence: 0.85, scope: "subtree" }` |
| `SUPERSEDES` | `{ reason: string, superseded_at: timestamp }` | `{ reason: "Merged duplicate pattern", superseded_at: "2026-05-01T..." }` |
| `SIMILAR_TO` | `{ cosine_sim: float }` | `{ cosine_sim: 0.92 }` |

### 4.3 Expertise Node Schema

Expertise nodes carry richer payloads than code-derived nodes:

```
ExpertiseNode extends Node {
  expertise_kind : "pattern" | "pitfall" | "preference" | "insight"
  title          : string        — human-readable label
  body           : string        — detailed description (Markdown)
  confidence     : float [0, 1]  — decays over time
  source_agent   : string        — which agent/session authored it
  evidence       : Citation[]    — references to code spans or commits
  tags           : string[]      — free-form taxonomy
}

Citation {
  node_id     : NodeID
  commit_sha  : string
  snippet     : string     — verbatim code excerpt (≤ 500 chars)
}
```

### 4.4 Version Snapshot

A `Snapshot` is a logical point-in-time view of the graph, keyed by commit:

```
Snapshot {
  id          : UUID
  commit_sha  : string
  parent_sha  : string | null
  created_at  : timestamp
  stats       : { nodes: int, edges: int, expertise_nodes: int }
}
```

Snapshots are not full copies; they are commit-SHA bookmarks. The
`version_range` on each node/edge allows the store to reconstruct any
snapshot view via range filtering.

---

## 5. Interfaces

Interfaces are defined in C# to match the repository's .NET 8.0 stack. Each
interface maps to its owning project in `src/ast/`.

### 5.1 Extraction Pipeline Interface

**Project:** `Ast.Memory.Extraction`

```csharp
/// <summary>
/// Drives full and incremental extraction of graph facts from source code.
/// Wraps existing Ast.Core RepositoryProcessor and language-specific parsers.
/// </summary>
public interface IExtractionPipeline
{
    /// <summary>
    /// Full extraction for an entire repository.
    /// Returns an async stream of graph facts (nodes and edges).
    /// </summary>
    IAsyncEnumerable<GraphFact> ExtractAllAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Incremental extraction for changed files since a base commit.
    /// Returns a diff: inserts, updates, and tombstones.
    /// </summary>
    Task<GraphDiff> ExtractIncrementalAsync(
        string repoRoot,
        string baseCommit,
        string headCommit,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default);
}

public class ExtractionOptions
{
    public required IReadOnlyList<string> Languages { get; init; }
    public bool EnableLsp { get; init; }
    public string EmbeddingModel { get; init; } = "default";
    public long MaxFileSize { get; init; } = 1_048_576; // 1 MB
}

/// <summary>
/// A single node or edge record produced by extraction.
/// Wraps the existing HypergraphNode/Hyperedge with version metadata.
/// </summary>
public abstract record GraphFact(Guid Id, string ContentHash, VersionRange VersionRange);
public record NodeFact(Guid Id, string ContentHash, VersionRange VersionRange, HypergraphNode Node)
    : GraphFact(Id, ContentHash, VersionRange);
public record EdgeFact(Guid Id, string ContentHash, VersionRange VersionRange, Hyperedge Edge)
    : GraphFact(Id, ContentHash, VersionRange);

public record GraphDiff(
    IReadOnlyList<GraphFact> Inserts,
    IReadOnlyList<GraphFact> Updates,
    IReadOnlyList<Guid> Tombstones);

public record VersionRange(string Since, string? Until);
```

### 5.2 Graph Store Interface (Extended)

**Project:** `Ast.GraphDb` — extends existing `IGraphStore`

```csharp
/// <summary>
/// Memory-aware extensions to the existing IGraphStore.
/// The base IGraphStore already provides CreateNodeAsync, GetNodeAsync,
/// GetEdgesForNodeAsync, FindPathsAsync, GetCallersAsync, etc.
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    /// <summary>
    /// Apply a batch of facts atomically (idempotent via content_hash).
    /// </summary>
    Task<Snapshot> ApplyDiffAsync(
        GraphDiff diff,
        string commitSha,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Traverse from a starting node with a query plan.
    /// </summary>
    Task<TraversalResult> TraverseAsync(
        string startNodeId,
        TraversalPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ANN vector search on the Semantic layer.
    /// Delegates to Ast.VectorDb under the hood.
    /// </summary>
    Task<IReadOnlyList<ScoredNode>> VectorSearchAsync(
        float[] queryVector,
        int topK,
        NodeFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read a node at a specific historical snapshot.
    /// </summary>
    Task<HypergraphNode?> GetNodeAtCommitAsync(
        string nodeId,
        string commitSha,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find nodes by qualified name prefix (autocomplete).
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> FindByNameAsync(
        string prefix,
        IReadOnlyList<string>? kinds = null,
        int limit = 20,
        CancellationToken cancellationToken = default);
}

public class TraversalPlan
{
    public required string Direction { get; init; } // "outbound" | "inbound" | "both"
    public required IReadOnlyList<HyperedgeType> EdgeTypes { get; init; }
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

### 5.3 Retrieval Engine Interface

**Project:** `Ast.Query` — extends existing `ICodeQuery` / `HybridQueryService`

```csharp
/// <summary>
/// Primary entry-point for agents. Extends the existing HybridQueryService
/// with memory-aware retrieval modes and token budgeting.
/// </summary>
public interface IMemoryRetrievalEngine
{
    /// <summary>
    /// Retrieve context for an agent task.
    /// Returns a formatted context block ready for prompt insertion.
    /// </summary>
    Task<RetrievalResult> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default);
}

public class RetrievalQuery
{
    public required string Text { get; init; }
    public string? Scope { get; init; }               // qualified-name prefix
    public RetrievalMode? Mode { get; init; }          // override auto-detected mode
    public int TokenBudget { get; init; } = 8000;
    public bool IncludeExpertise { get; init; } = true;
    public string? AtCommit { get; init; }             // optional snapshot pin
}

public enum RetrievalMode
{
    StructuralDrill,
    CallTrace,
    SemanticSearch,
    ExpertiseRecall,
    Hybrid
}

public class RetrievalResult
{
    /// <summary>Markdown-formatted context block for prompt insertion.</summary>
    public required string ContextBlock { get; init; }

    /// <summary>Token count of ContextBlock.</summary>
    public required int TokenCount { get; init; }

    /// <summary>Provenance: which nodes contributed.</summary>
    public required IReadOnlyList<Citation> Citations { get; init; }

    /// <summary>The retrieval mode actually used.</summary>
    public required RetrievalMode ResolvedMode { get; init; }
}
```

### 5.4 Evolution Engine Interface

**Project:** `Ast.Memory.Evolution`

```csharp
/// <summary>
/// Manages agent-authored expertise: write-back, consolidation, and decay.
/// </summary>
public interface IEvolutionEngine
{
    /// <summary>Agent writes back a learning.</summary>
    Task<Guid> RecordExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>Trigger consolidation over a scope (package / module).</summary>
    Task<ConsolidationReport> ConsolidateAsync(
        string scope,
        CancellationToken cancellationToken = default);

    /// <summary>Run decay pass: reduce confidence of expertise whose targets changed.</summary>
    Task<int> DecayStaleExpertiseAsync(
        string headCommit,
        CancellationToken cancellationToken = default);
}

public class ExpertiseEntry
{
    public required string Kind { get; init; }        // "pattern" | "pitfall" | "preference" | "insight"
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyList<string> AppliesTo { get; init; }  // node IDs
    public string Scope { get; init; } = "exact";     // "exact" | "subtree"
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

### 5.5 Incremental Sync Interface

**Project:** `Ast.Memory.Sync`

```csharp
/// <summary>
/// Keeps the memory graph current with repository changes.
/// </summary>
public interface IIncrementalSync
{
    /// <summary>Start watching a repo for changes (file-save or commit).</summary>
    void Watch(string repoRoot, WatchOptions options);

    /// <summary>Stop watching.</summary>
    void Unwatch(string repoRoot);

    /// <summary>Manual trigger: sync to a specific commit.</summary>
    Task<Snapshot> SyncToCommitAsync(
        string repoRoot,
        string commitSha,
        CancellationToken cancellationToken = default);
}

public class WatchOptions
{
    public int DebounceMs { get; init; } = 1000;
    public bool AutoDecay { get; init; } = true;
}
```

---

## 6. End-to-End Sequence Flows

### 6.1 Initial Repository Onboarding

```
Actor: Developer triggers "onboard repo" command.

1. IncrementalSync.syncToCommit(repoRoot, HEAD)
   ├─ 2. ExtractionPipeline.extractAll(repoRoot, options)
   │     ├─ 2a. Language Parser produces AST facts for each file.
   │     ├─ 2b. Structure Builder emits Structural-layer nodes/edges.
   │     ├─ 2c. Call-Chain Analyzer emits Call-chain-layer edges.
   │     ├─ 2d. Semantic Indexer computes embeddings, emits vectors.
   │     └─ 2e. Fact Normalizer deduplicates, assigns IDs, attaches
   │            version_range = {since: HEAD, until: null}.
   │
   ├─ 3. GraphStore.applyDiff(diff, HEAD)
   │     ├─ 3a. Inserts all nodes and edges.
   │     ├─ 3b. Builds vector index for Semantic layer.
   │     └─ 3c. Creates initial Snapshot record.
   │
   └─ 4. EvolutionEngine.consolidate("*")
         ├─ 4a. Community detection on Structural + Call-chain layers.
         ├─ 4b. LLM generates summary for each community.
         └─ 4c. Stores Topic + SemanticCluster nodes in Semantic layer.

Result: Full graph available; Snapshot created; community summaries populated.
```

### 6.2 Incremental Sync on Commit

```
Trigger: Git post-commit hook or file-system watcher detects changes.

1. IncrementalSync detects changed files between baseCommit and headCommit.

2. ExtractionPipeline.extractIncremental(repoRoot, base, head, changedFiles)
   ├─ 2a. Re-parse only changed files.
   ├─ 2b. Diff new facts against current graph → produces GraphDiff.
   └─ 2c. Mark removed symbols' version_range.until = headCommit.

3. GraphStore.applyDiff(diff, headCommit)
   ├─ 3a. Insert new nodes/edges.
   ├─ 3b. Update modified nodes (new content_hash, re-embedded vectors).
   ├─ 3c. Tombstone deleted nodes/edges.
   └─ 3d. Flag downstream CALLS edges as stale if source signature changed.

4. EvolutionEngine.decayStaleExpertise(headCommit)
   ├─ 4a. Find expertise nodes whose APPLIES_TO targets are tombstoned
   │       or have new content_hash.
   └─ 4b. Reduce their confidence by a decay factor (default: 0.5 per
           major change, 0.9 per minor change).

5. [Conditional] If structural change exceeds community-change threshold:
   └─ EvolutionEngine.consolidate(affectedPackage)
      └─ Re-generates community summaries for affected clusters.

Result: Graph reflects new commit; stale expertise decayed; summaries refreshed.
```

### 6.3 Agent Retrieves Context for a Coding Task

```
Actor: Coding agent needs context to implement a change in module "payments.checkout".

1. Agent calls RetrievalEngine.retrieve({
     text: "How does the checkout flow work, and are there known pitfalls?",
     scope: "payments.checkout",
     tokenBudget: 8000,
     includeExpertise: true
   })

2. Query Planner analyzes the query:
   ├─ 2a. Detects structural intent ("checkout flow") → selects STRUCTURAL_DRILL.
   ├─ 2b. Detects call-chain intent ("flow") → selects CALL_TRACE.
   ├─ 2c. Detects expertise intent ("pitfalls") → selects EXPERTISE_RECALL.
   └─ 2d. Resolves to mode = HYBRID.

3. Graph Traverser (parallel fan-out):
   ├─ 3a. STRUCTURAL_DRILL: BFS from "payments.checkout" Module node,
   │       depth 3, collecting CONTAINS children (classes, functions).
   ├─ 3b. CALL_TRACE: Forward DFS from each public function in checkout,
   │       depth 5, collecting CALLS paths.
   └─ 3c. EXPERTISE_RECALL: Find PATTERN/PITFALL nodes with
          APPLIES_TO edges scoped to "payments.checkout" subtree.

4. Vector Searcher:
   └─ 4a. Embed query text, ANN search for top-20 similar nodes
          filtered to payments.* namespace.

5. Ranker merges results:
   ├─ 5a. Score = 0.35 × structural_proximity
   │     + 0.25 × call_chain_distance⁻¹
   │     + 0.20 × cosine_similarity
   │     + 0.10 × recency_score
   │     + 0.10 × expertise_confidence
   └─ 5b. Deduplicate by node ID; keep highest-scored path.

6. Budget Controller:
   ├─ 6a. Estimates tokens per fragment.
   ├─ 6b. If over budget, collapses low-level functions into parent
   │       class/module summaries.
   └─ 6c. Drops lowest-ranked fragments until within budget.

7. Context Assembler formats the output:
   ├─ ## Structure
   │  payments/checkout/
   │  ├── CheckoutService (class) — orchestrates the checkout flow
   │  ├── PriceCalculator (class) — applies discounts, tax
   │  └── ...
   ├─ ## Call Graph
   │  CheckoutService.process() → PriceCalculator.compute()
   │                             → InventoryClient.reserve()
   │                             → PaymentGateway.charge()
   ├─ ## Related Code
   │  [ranked code snippets with file:line references]
   └─ ## Known Expertise
      ⚠ PITFALL: PaymentGateway.charge() is not idempotent — must use
                 idempotency key. (confidence: 0.91)
      ✓ PATTERN: CheckoutService uses saga pattern for rollback.
                 (confidence: 0.88)

8. Returns RetrievalResult { contextBlock, tokenCount: 6420, citations, resolvedMode: "HYBRID" }.

Result: Agent inserts contextBlock into its system prompt and proceeds with the task.
```

### 6.4 Agent Writes Back Expertise

```
Actor: Agent completes a task and records a learning.

1. Agent calls EvolutionEngine.recordExpertise({
     kind: "pitfall",
     title: "Race condition in concurrent checkout",
     body: "When two requests hit CheckoutService.process() for the same
            cart simultaneously, InventoryClient.reserve() can double-reserve.
            Fix: acquire a distributed lock keyed on cart_id before the saga.",
     appliesTo: [nodeId("CheckoutService.process"), nodeId("InventoryClient.reserve")],
     scope: "exact",
     evidence: [{ node_id: ..., commit_sha: "abc123", snippet: "..." }],
     sourceAgent: "copilot-session-42"
   })

2. Evolution Engine:
   ├─ 2a. Creates a new PITFALL node with confidence = 1.0.
   ├─ 2b. Creates APPLIES_TO edges to CheckoutService.process and
   │       InventoryClient.reserve.
   ├─ 2c. Checks for existing pitfall nodes on the same targets:
   │       ├─ If semantically overlapping (cosine > 0.85): create
   │       │   DERIVED_FROM edge and schedule consolidation.
   │       └─ If no overlap: standalone node.
   └─ 2d. Returns new NodeID.

3. [Deferred] Next consolidation pass:
   ├─ 3a. Finds the new pitfall and the earlier "PaymentGateway not
   │       idempotent" pitfall both touch the checkout saga.
   ├─ 3b. LLM reviews both, determines they are distinct issues.
   └─ 3c. No merge; both remain active.

Result: Expertise graph grows by one node; future retrievals for checkout
        code will surface this pitfall.
```

### 6.5 Cross-Session Learning (Agent Gets Smarter)

```
Timeline: Sessions S1 through Sn interact with the same repo over weeks.

S1: Agent onboards repo. Graph has 0 expertise nodes.
    Agent completes a refactor in payments module.
    Writes back: PATTERN "saga-based rollback in checkout" (confidence 1.0).

S2: Different agent session. Retrieves context for payments.
    Sees the saga pattern in the Expertise section of the context block.
    Uses that knowledge to correctly extend the checkout flow without
    breaking the rollback chain.
    Writes back: INSIGHT "saga steps must be registered in order in
    SagaRegistry; out-of-order causes silent rollback failures."

S3: Code changes land — CheckoutService.process() is refactored.
    Incremental Sync triggers decay:
    ├─ Saga pattern node: target's content_hash changed but pattern
    │   still holds → confidence reduced 1.0 → 0.9.
    └─ Race condition pitfall: target signature unchanged → no decay.

S5: Consolidation runs after a batch of PRs merge.
    ├─ Finds 3 expertise nodes related to the payments saga.
    ├─ LLM merges them into a single PATTERN with higher confidence.
    ├─ Old nodes get SUPERSEDED edges pointing to the merged node.
    └─ A new Topic "Payments Saga Orchestration" is created in the
       Semantic layer with a community summary.

S8: A new agent session retrieves context for payments.
    Gets the consolidated, high-confidence pattern and the community
    summary. Produces a higher-quality completion because the memory
    has been refined through multiple sessions.

Result: The system accumulates expertise that compounds across sessions.
        Each agent session is smarter than the last for the same codebase.
```

---

## 7. Deployment Topology

```
┌──────────────────────────────────────────────────────────────────┐
│  Lightweight (single-dev, local)                                 │
│                                                                  │
│  SQLite graph store + in-process vector index (usearch)          │
│  File-system watcher for incremental sync                        │
│  All components in a single process / CLI daemon                 │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│  Team / CI (shared)                                              │
│                                                                  │
│  Neo4j / Memgraph for graph store                                │
│  Qdrant / pgvector for vector index                              │
│  Git webhook for incremental sync                                │
│  Retrieval Engine exposed as gRPC / HTTP service                 │
│  Evolution Engine runs as async worker (queue-driven)            │
└──────────────────────────────────────────────────────────────────┘
```

---

## 8. Key Design Decisions & Trade-offs

| Decision | Rationale | Trade-off |
|---|---|---|
| **Extend existing `src/ast/` stack rather than greenfield** | The Ast.Core, Ast.GraphDb, Ast.Embedding, and Ast.Query projects already provide 437+ tested capabilities for AST extraction, graph storage, embeddings, and querying. Building on top avoids redoing that work. | Couples memory system to existing models (`HypergraphNode`, `Hyperedge`); may need adapters if those models change. |
| **Four separate graph layers sharing a node namespace** | Avoids the entanglement problem identified by MAGMA. Each layer can be queried, indexed, and evolved independently. | Cross-layer queries require joins; slight overhead vs. a single unified graph. |
| **Existing Ast.Csharp/PowerShell parsers as primary, tree-sitter for new languages** | Existing parsers are battle-tested with 166+ tests. Tree-sitter adds breadth for TypeScript, Python, Go without rewriting C#/PowerShell extraction. | Two parser stacks to maintain; unified `ILanguageParser` interface mitigates this. |
| **Version ranges on nodes/edges instead of full graph snapshots** | Space-efficient: O(changed entities) per commit instead of O(total graph). Enables time-travel queries. | Point-in-time reconstruction requires range filtering, which adds query complexity. |
| **LLM-powered consolidation in the Evolution Engine** | Semantic merge of expertise nodes is inherently a natural-language task. Rules-based merging misses nuance. | Adds LLM cost; consolidation is batched and async to amortize. |
| **Token-budget-aware retrieval with hierarchical fallback** | Agents have finite context windows. The system must be useful even at small budgets (2K tokens). | Summaries lose detail. The system compensates by providing citations so the agent can drill down. |
| **Confidence decay on expertise nodes** | Prevents stale advice from poisoning agent context after code refactors. | Aggressive decay can lose still-valid insights. Tunable decay factor mitigates this. |

---

## 9. Appendix: Glossary

| Term | Definition |
|---|---|
| **Graph Fact** | A single node or edge record produced by the Extraction Pipeline. |
| **Graph Diff** | A batch of inserts, updates, and tombstones applied atomically to the Graph Store. |
| **Tombstone** | A soft-delete: the node/edge's `version_range.until` is set to the current commit SHA. The record is retained for time-travel. |
| **Community** | A cluster of tightly-coupled nodes detected by graph partitioning (e.g., Louvain algorithm) on the Structural + Call-chain layers. |
| **Expertise Node** | An agent-authored knowledge record (pattern, pitfall, preference, or insight) stored in the Expertise layer. |
| **Snapshot** | A logical point-in-time bookmark keyed by commit SHA, used for time-travel retrieval. |
| **Decay Factor** | A multiplier (0 < d ≤ 1) applied to an expertise node's confidence when its associated code changes. |
| **Context Block** | The final Markdown document returned by the Context Assembler, ready for insertion into an LLM prompt. |
