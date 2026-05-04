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

This architecture document is part of a four-document planning set. **This
document is the canonical authority** for component boundaries, data model,
edge/layer naming, and ID format. When any sibling document uses different
terminology, the names defined here govern the implementation.

| Document | Owns | Reads from this doc |
|---|---|---|
| **architecture.md** (this file) | Components, data model, interfaces, sequence flows | — |
| [tech-spec.md](tech-spec.md) | Problem statement, scope/non-goals, hard constraints, risks, success criteria | Layer names, edge names, temporal model, ID format |
| [implementation-plan.md](implementation-plan.md) | Phased work items, deliverables, dependencies, evaluation harness | Project layout, interface signatures, model extension strategy |
| [e2e-scenarios.md](e2e-scenarios.md) | BDD Gherkin scenarios for each feature | Node ID format, edge names, layer names |

### 1.5 Canonical Vocabulary

All documents in this story use the vocabulary defined in this section. Terms
not listed here have their plain-English meaning.

#### Graph layers

| Canonical layer | Tag value | Content |
|---|---|---|
| **Structural** | `structural` | Containment hierarchy: Repo → Workspace → Package → Module → Class → Function. Edges: `CONTAINS`, `DEPENDS_ON`. |
| **Call-Chain** | `call_chain` | Invocation graph between symbol nodes. Edges: `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES`. |
| **Semantic** | `semantic` | Embedding-similarity clusters and topics. Edges: `SIMILAR_TO`, `BELONGS_TO_CLUSTER`. |
| **Expertise** | `expertise` | Agent-authored knowledge. Edges: `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM`. |

Temporal versioning is an **orthogonal concern** applied to nodes and edges on
all layers. It is not a separate layer. Every node and edge carries a `layer`
string tag so queries can filter by any single layer or combine layers.

#### Edge taxonomy

Edges fall into two categories based on their origin:

**Code-derived edges** — produced by the Extraction Pipeline from source-code
analysis. Each maps to one or more existing `HyperedgeType` enum values in
`Ast.Core`:

| Canonical edge | `HyperedgeType` enum values | Layer | Notes |
|---|---|---|---|
| `CONTAINS` | `ClassDefinition`, `InterfaceDefinition`, `StructDefinition`, `RecordDefinition`, `MethodDefinition`, `ConstructorDefinition`, `PropertyDefinition`, `FunctionDefinition` | Structural | Parent→child containment |
| `DEPENDS_ON` | `UsingDirective`, `ModuleImport`, `DotSource` | Structural | Module/package imports |
| `CALLS` | `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`, `ModuleFunctionCall`, `CrossLanguageMethodCall` | Call-Chain | Function invocation |
| `EXTENDS` | `Extends` | Call-Chain | Class inheritance |
| `IMPLEMENTS` | `Implements` | Call-Chain | Interface implementation |
| `REFERENCES` | `DataFlow`, `Assignment`, `CrossLanguageTypeReference` | Call-Chain | Non-call symbol references |

**Memory-only edges** — produced by the Evolution Engine or Semantic Indexer.
These have no corresponding `HyperedgeType` enum value in the existing
`Ast.Core` codebase. They are stored as `Hyperedge` instances whose
`EdgeType` is set to a new `HyperedgeType.MemoryRelation` value (a single
additive enum value added to `Ast.Core` — see §4.6 for rationale). The true
semantic type is always in `Metadata["SemanticRelation"]`:

| Canonical edge | Layer | Created by | Semantics |
|---|---|---|---|
| `SIMILAR_TO` | Semantic | Semantic Indexer (at extraction) | Two code nodes whose embeddings exceed a cosine-similarity threshold |
| `BELONGS_TO_CLUSTER` | Semantic | Evolution Engine (community detection) | A code node is a member of a detected community |
| `APPLIES_TO` | Expertise | Evolution Engine (write-back) | An expertise node annotates a code target |
| `SUPERSEDES` | Expertise | Evolution Engine (consolidation) | A merged expertise node replaces an older one |
| `DERIVED_FROM` | Expertise | Evolution Engine (consolidation) | An expertise node was derived from another |

**Uniform filtering:** Every edge — code-derived and memory-only alike —
carries `Metadata["SemanticRelation"]` with its canonical name. This is the
**sole edge-filtering mechanism** used by the Retrieval Engine. The legacy
`HyperedgeType` enum is preserved for backward compatibility with existing
parsers but is never used as a filter in the memory layer.

#### Retired names

The following names appeared in earlier iterations or appear in sibling
documents. They are replaced by the canonical names above. **Implementers must
use the canonical name, not the retired name.**

| Retired name | Canonical replacement | Where the retired name appears | Notes |
|---|---|---|---|
| `IMPORTS` | `DEPENDS_ON` | `implementation-plan.md` Stage 2.2 | |
| `RE_EXPORTS` | `DEPENDS_ON` | (no current occurrence) | |
| `CALLED_BY` | Reverse traversal of `CALLS` | `implementation-plan.md` Stage 2.2 | Not a stored edge — use reverse traversal direction in `TraversalPlan` |
| `INHERITS` | `EXTENDS` | `implementation-plan.md` Stage 2.2, test scenarios | |
| `INSTANTIATES` | `CALLS` (with `ConstructorInvocation` original type) | (no current occurrence) | |
| `OVERRIDES` | `EXTENDS` (override is an inheritance variant) | (no current occurrence) | |
| Fact / Derived / Interaction / Temporal layers | Structural / Call-Chain / Semantic / Expertise | (no current occurrence) | Temporal versioning is orthogonal, not a layer |
| `ExpertiseNote` | `ExpertiseEntry` | `implementation-plan.md` Stage 1.1 | Class renamed for clarity |
| `IMemoryStore` | `IMemoryGraphStore` | `implementation-plan.md` Stage 1.2 | Interface renamed |
| `IExpertiseStore` | `IEvolutionEngine` | `implementation-plan.md` Stage 1.2 | Merged into Evolution Engine |

### 1.6 Temporal Model

Commit SHAs are opaque identifiers that cannot be compared with `<=` or `>`
operators. The system uses a dual-axis temporal model:

1. **Commit axis** — each processed commit SHA is mapped to a monotonically
   increasing `commit_index` integer in the `commits` table (below). The
   `VersionRange` on every node and edge stores both the commit SHA (for
   human-readable provenance) and the corresponding `commit_index` (for
   efficient range queries).
2. **Wall-clock axis** — `LastTouched` (ISO-8601) records when extraction or
   modification last ran. This is informational; range queries use the commit
   index.

```sql
CREATE TABLE commits (
    commit_sha   TEXT PRIMARY KEY,
    commit_index INTEGER NOT NULL UNIQUE,
    timestamp    TEXT NOT NULL,  -- ISO-8601 wall-clock time
    parent_sha   TEXT            -- for lineage
);
CREATE INDEX idx_commits_index ON commits(commit_index);
```

Point-in-time queries use integer indices:
`WHERE valid_from_idx <= $target_idx AND (valid_until_idx IS NULL OR valid_until_idx > $target_idx)`.

**Reconciliation with tech-spec §7.4:** The tech-spec describes `valid_from` /
`valid_until` as ISO-8601 timestamps for conceptual clarity. In the physical
storage model defined here, those columns store **integer `commit_index`
values**, not timestamps. The wall-clock timestamp for any commit is available
via a join to the `commits` table above. The `VersionRange` record (§4.1)
bridges both representations: it carries the commit SHA strings (`Since`,
`Until`) for provenance display and the integer indices (`SinceIndex`,
`UntilIndex`) for efficient range queries.

**Reconciliation with implementation-plan Stage 1.1:** The implementation plan
describes `VersionRange` as "since/until commit SHA". In the canonical model,
`VersionRange` carries **both** the SHA and the integer index. The SHA alone
is insufficient for range queries; the index is required. See §4.1 for the
full `VersionRange` record shape.

### 1.7 Model Extension Strategy

The memory layer extends `HypergraphNode` and `Hyperedge` from `Ast.Core`
**non-breakingly** via the existing `Metadata` dictionary — no new properties
are added to the base classes. `MemoryNode` / `MemoryEdge` are convenience
**wrapper** classes that **compose** (not inherit from) the base types and
provide strongly-typed accessors for memory Metadata keys. All inter-component
interfaces accept and return the base `HypergraphNode` / `Hyperedge` types to
avoid breaking existing parsers and their 437+ tests.

**Reconciliation with implementation-plan Stage 1.1:** The implementation plan
says `MemoryNode.cs` "extending `HypergraphNode`" and `MemoryEdge.cs`
"extending `Hyperedge`" — this wording implies C# class inheritance. **That is
incorrect.** The canonical strategy is **composition**: `MemoryNode` contains
a `HypergraphNode Inner { get; }` property and delegates to it. It does NOT
use `: HypergraphNode` inheritance. This ensures that code accepting
`HypergraphNode` parameters continues to work without casting. The
implementation plan should be updated to say "wrapping" or "composing" instead
of "extending".

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
existing ones. All projects live under `src/ast/` following the established
naming convention (`Ast.<Capability>`). New projects follow the same pattern —
**no dot-separated sub-namespaces** (e.g., `Ast.Memory`, not
`Ast.Memory.Extraction`). Each new project gets a matching unit-test project
(`Ast.Memory.UnitTests`, etc.).

| Component (architecture) | Project (csproj) | Status | Notes |
|---|---|---|---|
| Extraction Pipeline — Language Parser | `Ast.Core` / `Ast.Csharp` / `Ast.PowerShell` | **Existing** — extend | Add optional `VersionRange` and `ContentHash` to `HypergraphNode.Metadata`; wrap parsers behind a unified `ILanguageParser` interface. **No breaking changes to `HypergraphNode` or `Hyperedge` record shapes.** |
| Extraction Pipeline — Structure Builder, Call-Chain Analyzer, Fact Normalizer | `Ast.Memory` (new) | **New** | Consumes `HypergraphNode` + `Hyperedge` streams from existing parsers. Builds Structural-layer containment hierarchy (Repo → Workspace → Package → Module → Class → Function), Call-chain-layer edges (`CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES`), and normalizes facts with stable path-based IDs and `VersionRange` stamps. Single project to avoid over-splitting. |
| Extraction Pipeline — Semantic Indexer | `Ast.Embedding` (`IEmbeddingGenerator`, `CodeEmbeddingService`) | **Existing** — reuse | Produces dense vectors via `IEmbeddingGenerator`; attaches to `HypergraphNode.Metadata["Embedding"]`. |
| Graph Store (memory-layer extension) | `Ast.GraphDb` (`IGraphStore`, `Neo4jGraphStore`) | **Existing** — extend | Add `IMemoryGraphStore` sub-interface with `ApplyDiffAsync`, `TraverseAsync`, temporal range queries. Add `SqliteGraphStore` for local mode. **The `IMemoryGraphStore` interface operates on `HypergraphNode`/`Hyperedge` — not on wrapper types.** |
| Vector Index | `Ast.VectorDb` (`IVectorStore`, `QdrantVectorStore`) | **Existing** — reuse | Qdrant for team mode; add in-process HNSW adapter for local mode. |
| Retrieval Engine | `Ast.Query` (`ICodeQuery`, `HybridQueryService`) | **Existing** — extend | Add `IMemoryRetrievalEngine` with retrieval modes (`StructuralDrill`, `CallTrace`, `ExpertiseRecall`, `Hybrid`), Budget Controller, Ranker. |
| Context Assembler | `Ast.Memory` (new, same project) | **New** | Markdown context-block formatter with hierarchy compression and dedup. Lives in `Ast.Memory/Context/` namespace. |
| Evolution Engine | `Ast.Memory` (new, same project) | **New** | Expertise write-back, LLM-powered consolidation, confidence decay. Lives in `Ast.Memory/Evolution/` namespace. |
| Incremental Sync | `Ast.Memory` (new, same project) | **New** | File-system watcher + git-diff driver, triggers extraction pipeline. Lives in `Ast.Memory/Sync/` namespace. |
| Convenience Wrappers | `Ast.Memory` (new, same project) | **New** | `MemoryNode` and `MemoryEdge` classes that **compose** (not inherit from) `HypergraphNode`/`Hyperedge`, providing strongly-typed accessors for memory Metadata keys. Lives in `Ast.Memory/Models/` namespace. See §4.1 for details. |
| Agent API / MCP | `Ast.Cli` (existing CLI host) | **Existing** — extend | Expose retrieve + write-back as MCP tools. Avoids a new host binary. |
| Unit Tests | `Ast.Memory.UnitTests` (new) | **New** | Unit tests for extraction, context assembly, evolution, models. |
| Integration Tests | `Ast.Memory.IntegrationTests` (new) | **New** | End-to-end tests for onboarding, sync, retrieval, and write-back flows. |

**Project layout on disk:**

```
src/ast/
  Ast.Memory/                     ← NEW: core memory library
    Ast.Memory.csproj             ←   references Ast.Core, Ast.GraphDb, Ast.Embedding
    Models/                       ←   MemoryNode (composes HypergraphNode), MemoryEdge (composes Hyperedge),
                                  ←   ExpertiseEntry, MemoryMetadataKeys (constants), Enums
    Extraction/                   ←   StructureBuilder, CallChainAnalyzer, FactNormalizer
    Context/                      ←   ContextAssembler, BudgetController
    Evolution/                    ←   EvolutionEngine, ConsolidationService, DecayService
    Sync/                         ←   IncrementalSync, GitDiffDriver
    Storage/                      ←   IMemoryGraphStore, SqliteGraphStore
  Ast.Memory.UnitTests/           ← NEW: unit tests
  Ast.Memory.IntegrationTests/    ← NEW: integration tests
  Ast.Core/                       ← EXISTING: HypergraphNode, Hyperedge, HyperedgeType
  Ast.GraphDb/                    ← EXISTING: IGraphStore, Neo4jGraphStore — extended
  Ast.Embedding/                  ← EXISTING: IEmbeddingGenerator — reused
  Ast.VectorDb/                   ← EXISTING: IVectorStore — reused
  Ast.Query/                      ← EXISTING: ICodeQuery, HybridQueryService — extended
  Ast.Cli/                        ← EXISTING: CLI entry point — extended with MCP tools
  CodeIntelligence/               ← EXISTING: analyzers — consumed as edge source
```

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
| **Structure Builder** | AST facts | Structural-layer nodes + `CONTAINS` / `DEPENDS_ON` edges | Builds the repo → workspace → package → module → symbol hierarchy. `Workspace` nodes appear in monorepos; single-package repos skip directly to Package. |
| **Call-Chain Analyzer** | AST facts + resolved types | Call-chain-layer `CALLS` / `EXTENDS` / `IMPLEMENTS` / `REFERENCES` edges | Static call graph and type relationships. `CALLS` edges carry arity, argument types, and whether the call is conditional/async. `REFERENCES` edges capture non-call symbol usage. |
| **Semantic Indexer** | Docstrings, comments, symbol names | Semantic-layer embedding vectors attached to nodes | Produces dense vector embeddings (e.g., code-embedding model) for similarity retrieval. |
| **Expertise Embedder** | Expertise node Title + Body text | Embedding vectors on Expertise-layer nodes | Re-uses `IEmbeddingGenerator` to embed agent-authored expertise. Enables semantic search over learnings ("find known pitfalls related to concurrency"). Runs at write-back time (inside `EvolutionEngine.RecordExpertiseAsync`). |
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

| Layer | Tag value | Node labels | Edge types | Purpose |
|---|---|---|---|---|
| **Structural** | `structural` | `Repo`, `Workspace`, `Package`, `Module`, `Class`, `Function`, `Variable` | `CONTAINS`, `DEPENDS_ON` | Top-down containment and dependency hierarchy. `Workspace` is present in monorepos (maps to project root / csproj / package.json); absent in single-package repos. |
| **Call-Chain** | `call_chain` | (shares symbol nodes from Structural) | `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` | Directed invocation and type-relationship graph. `CALLS` = who-calls-whom. `EXTENDS` = inheritance. `IMPLEMENTS` = interface conformance. `REFERENCES` = non-call symbol usage (type annotations, data flow). |
| **Semantic** | `semantic` | `SemanticCluster`, `Topic` | `SIMILAR_TO`, `BELONGS_TO_CLUSTER` | Grouping by embedding similarity. Enables "find all code related to X" queries. Embeddings are computed at extraction time for code nodes and at write-back time for expertise nodes; the vector index is updated synchronously in both paths. |
| **Expertise** | `expertise` | `Pattern`, `Pitfall`, `Preference`, `Insight` | `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` | Agent-authored knowledge. Grows over time through the Evolution Engine. Expertise nodes carry their own embeddings (computed from Title + Body via `IEmbeddingGenerator`) so they participate in vector search alongside code nodes. |

**Storage backend:** The existing `Neo4jGraphStore` (in `Ast.GraphDb`) handles
the team/CI deployment. For local/CI use, a new `SqliteGraphStore` in
`Ast.Memory/Storage/` implements `IMemoryGraphStore` using SQLite + recursive
CTEs for graph traversal. The existing `Ast.VectorDb` Qdrant integration
handles vector search in team mode; an in-process HNSW index (via
`sqlite-vss` or `usearch`) is provided for local mode.

### 3.3 Retrieval Engine

**Responsibility:** Accept a natural-language or structured query from the
agent, plan the retrieval strategy, traverse the graph, and return ranked
memory fragments.

| Sub-component | Role |
|---|---|
| **Query Planner** | Classifies the query intent (structural lookup, call-path trace, semantic search, expertise recall) and selects which graph layers to traverse. Inspired by MAGMA's policy-guided traversal. |
| **Graph Traverser** | Executes Cypher / Gremlin-style traversal on the selected layers. Supports depth-limited BFS, shortest-path, and community-walk strategies. |
| **Vector Searcher** | Runs ANN search over the Semantic layer embeddings. Returns top-k similar nodes. |
| **Ranker** | Merges results from Graph Traverser and Vector Searcher. Applies a 5-signal scoring model (see table below). |
| **Budget Controller** | Enforces a token budget (ceiling: 32 K tokens, per-query default: 8 K — see §5.3). Prunes or summarizes fragments to fit the agent's context window. Uses the structural hierarchy to substitute a parent summary when child details exceed budget. |

**Canonical scoring signals and default weights (used by the Ranker):**

These five signals and their default weights are the **single source of truth**
for scoring throughout this document and all sibling documents. The
`ScoringWeights` class (in `Ast.Memory/Retrieval/`) exposes these as
configurable properties.

| Signal key | Signal name | Default weight | Description |
|---|---|---|---|
| `structural` | Structural proximity | 0.30 | Inverse graph distance from seed node in Structural layer (BFS hops⁻¹) |
| `semantic` | Semantic similarity | 0.25 | Cosine similarity of query embedding to node embedding |
| `centrality` | Centrality | 0.20 | Normalized fan-in count from `CALLS` edges (PageRank-light) |
| `recency` | Recency | 0.15 | Recency of last modification: `1.0 − (HEAD_idx − node_sinceIdx) / max_distance`, clamped to `[0, 1]` |
| `expertise` | Expertise confidence | 0.10 | Max `Confidence` of any `APPLIES_TO` expertise edge targeting the node; `0.0` when none |

**Sum = 1.00.** Weights are floats in `[0, 1]` and must sum to 1.0.

**Configurable thresholds (shared across components):**

| Constant | Default | Used by | Description |
|---|---|---|---|
| `SimilarityThreshold` | 0.85 | Semantic Indexer (§3.1), Evolution Engine (§3.5) | Cosine-similarity floor for creating `SIMILAR_TO` and `DERIVED_FROM` edges |
| `StaleDecayFactor` | 0.5 | Evolution Engine (§3.5) | Multiplicative confidence decay when an expertise node's target content-hash changes |
| `MinorDecayFactor` | 0.9 | Evolution Engine (§3.5) | Multiplicative confidence decay for minor changes (whitespace, comment-only) |
| `ArchiveConfidenceFloor` | 0.1 | Evolution Engine (§3.5) | Expertise nodes below this confidence are soft-deleted |

**Retrieval modes** (selected by the Query Planner):

| Mode | Technique | Example query |
|---|---|---|
| `STRUCTURAL_DRILL` | Top-down BFS from repo root | "What does the `auth` package contain?" |
| `CALL_TRACE` | Forward/reverse DFS on call-chain layer | "What calls `validateToken`?" / "What does `login()` invoke?" |
| `SEMANTIC_SEARCH` | Vector ANN + cluster expansion | "Find code related to rate limiting" |
| `EXPERTISE_RECALL` | Pattern/pitfall lookup filtered by `APPLIES_TO` scope | "Known pitfalls for the payments module?" |
| `HYBRID` | Fan-out across ≥2 modes, merge via Ranker | "Explain the authentication flow and flag any known issues" |

**Edge filtering in retrieval:** The `TraversalPlan.SemanticRelations` field
(a list of canonical edge names) is the **sole** edge filter for all retrieval
modes. It works uniformly for both code-derived and memory-only edges because
every edge in the graph carries `Metadata["SemanticRelation"]`. The legacy
`TraversalPlan.EdgeTypes` field (list of `HyperedgeType` enum values) is
**deprecated for retrieval** — it exists only for backward compatibility with
pre-memory queries and is never set by the memory retrieval engine.

Example traversal plans by mode:

| Mode | `SemanticRelations` | `LayerFilter` |
|---|---|---|
| `STRUCTURAL_DRILL` | `["CONTAINS", "DEPENDS_ON"]` | `"structural"` |
| `CALL_TRACE` | `["CALLS", "EXTENDS", "IMPLEMENTS", "REFERENCES"]` | `"call_chain"` |
| `SEMANTIC_SEARCH` | `["SIMILAR_TO", "BELONGS_TO_CLUSTER"]` | `"semantic"` |
| `EXPERTISE_RECALL` | `["APPLIES_TO", "SUPERSEDES", "DERIVED_FROM"]` | `"expertise"` |
| `HYBRID` | all of the above | `null` (all layers) |

The Graph Traverser resolves `SemanticRelations` by matching against
`Metadata["SemanticRelation"]` on each candidate edge. This is a string
comparison, not an enum match. Memory-only edges (whose `HyperedgeType` is
`MemoryRelation`) are reached exclusively through this path.

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
`Ast.Core` with versioning, annotation, and layer-tagging fields. Extensions
are **non-breaking** — they use the existing `Metadata` dictionary on both
types rather than adding new properties to the base classes.

**Critical alignment with existing code:**

- `HypergraphNode.Id` is `string` (not UUID/Guid). The memory layer follows
  this convention. Stable IDs are path-based strings using the format
  `"{filePath}::{qualifiedName}"` for symbol nodes (e.g.,
  `/src/auth/login::AuthService`, `/src/auth/login::AuthService.validate`) and
  `"{filePath}"` for file/module nodes (e.g., `/src/auth/login`). Directory
  nodes use their relative path (e.g., `/src/auth`). The `Repo` root node
  uses `"/"`. This format aligns with the e2e-scenarios node IDs.
  **All IDs across all layers are `string` type.** There are no GUIDs, UUIDs,
  or integer IDs anywhere in the memory layer.
- `Hyperedge` is a **multi-node hyperedge** with `NodeIds: List<string>` and
  `Roles: Dictionary<string, string>`. It is NOT a binary source→target edge.
  The memory layer preserves this model. For binary relationships (e.g., CALLS),
  the hyperedge has exactly 2 `NodeIds` with roles `"caller"` and `"callee"`.
- `Hyperedge.EdgeType` is `HyperedgeType` enum (e.g., `MethodInvocation`,
  `Implements`, `Extends`). The memory layer maps these to semantic relation
  names (e.g., `MethodInvocation` → `CALLS`) but stores the original
  `HyperedgeType` in `Metadata["OriginalEdgeType"]` for lossless round-trips.
- Both `HypergraphNode.Metadata` and `Hyperedge.Metadata` are
  `Dictionary<string, object?>` — the memory layer stores version ranges,
  layer tags, and annotations in these dictionaries.

#### Memory-layer metadata keys (stored in `HypergraphNode.Metadata`)

| Key | Type | Description |
|---|---|---|
| `MemoryLayer` | `string` | Canonical layer tag: `"structural"`, `"call_chain"`, `"semantic"`, `"expertise"` |
| `VersionRange` | `{ Since: string, SinceIndex: int, Until: string?, UntilIndex: int? }` | Commit SHA + monotonic index range. `Since`/`SinceIndex` = commit that introduced this fact. `Until = null` means current; non-null = commit that removed it. Integer indices enable range queries (SHAs are not orderable). Maps to tech-spec's `valid_from`/`valid_until` columns via the index. |
| `ContentHash` | `string` | SHA-256 of the source span. Used for change detection. |
| `LastTouched` | `string` (ISO-8601) | Wall-clock timestamp of last extraction or modification. |
| `Embedding` | `float[]` | Dense vector (dimension = model-dependent). Only present on Semantic-layer nodes. |
| `Summary` | `string` | LLM-generated one-liner. |
| `QualifiedName` | `string` | Fully-qualified name, e.g., `"Ast.Core.HypergraphNode.GetEmbeddingText"`. |

#### Memory-layer metadata keys (stored in `Hyperedge.Metadata`)

| Key | Type | Description |
|---|---|---|
| `MemoryLayer` | `string` | Canonical layer tag. |
| `SemanticRelation` | `string` | Canonical relation name: `"CALLS"`, `"DEPENDS_ON"`, `"CONTAINS"`, `"EXTENDS"`, `"IMPLEMENTS"`, `"REFERENCES"`, `"SIMILAR_TO"`, `"BELONGS_TO_CLUSTER"`, `"APPLIES_TO"`, `"SUPERSEDES"`, `"DERIVED_FROM"`. Present on ALL edges (code-derived and memory-only). |
| `VersionRange` | `{ Since: string, SinceIndex: int, Until: string?, UntilIndex: int? }` | Commit SHA + monotonic index. Maps to tech-spec's `valid_from`/`valid_until` via index. |
| `Confidence` | `double` | `0.0`–`1.0`. Direct calls = `1.0`; dynamic dispatch = `0.5`–`0.7`. |
| `Stale` | `bool` | `true` when pending re-confirmation after upstream signature change. |
| `OriginalEdgeType` | `string` | Original `HyperedgeType` enum name for lossless mapping back to Ast.Core. |

#### Node shape (extended via Metadata)

```
HypergraphNode (from Ast.Core — unchanged)
├── Id             : string      ← path-based: "/src/auth/login::AuthService.validate"
├── Name           : string
├── NodeType       : string      ← e.g., "Class", "Method", "Function"
├── Language       : string
├── FilePath       : string
├── Scope          : string
├── LineStart      : int
├── LineEnd        : int
├── Modifiers      : List<string>
└── Metadata       : Dictionary<string, object?>
    ├── MemoryLayer     : string   ← "structural" | "call_chain" | "semantic" | "expertise"
    ├── VersionRange    : { Since: string (commit SHA), SinceIndex: int, Until: string? (commit SHA or null), UntilIndex: int? }
    ├── ContentHash     : string   ← SHA-256 of source span
    ├── LastTouched     : string   ← ISO-8601 timestamp (wall-clock)
    ├── Embedding       : float[]  ← dense vector (dim = model-dependent, e.g., 768)
    ├── Summary         : string   ← LLM-generated one-liner
    ├── QualifiedName   : string   ← e.g., "Ast.Core.HypergraphNode.GetEmbeddingText"
    └── ... (language-specific extras)
```

#### Edge shape (extended via Metadata)

```
Hyperedge (from Ast.Core — unchanged)
├── Id             : string      ← "{sourceId}→{relation}→{targetId}" for deterministic dedup
├── EdgeType       : HyperedgeType enum   ← original Ast.Core value; MemoryRelation for memory-only edges
├── NodeIds        : List<string>   ← multi-node (typically 2 for binary relations)
├── Roles          : Dictionary<string, string>   ← e.g., { "/src/auth/login::fn": "caller", "/src/auth/token::verify": "callee" }
├── Languages      : List<string>
├── Weight         : double      ← confidence / frequency
└── Metadata       : Dictionary<string, object?>
    ├── MemoryLayer        : string   ← "structural" | "call_chain" | "semantic" | "expertise"
    ├── SemanticRelation   : string   ← canonical name: "CALLS", "DEPENDS_ON", "CONTAINS", etc.
    ├── VersionRange       : { Since: string (commit SHA), SinceIndex: int, Until: string?, UntilIndex: int? }
    ├── Confidence         : double   ← 0.0–1.0 (direct calls=1.0, dynamic dispatch=0.5–0.7)
    ├── Stale              : bool     ← true when pending re-confirmation
    ├── OriginalEdgeType   : string   ← HyperedgeType enum name for lossless mapping
    └── ... (relation-specific payload — see §4.2)
```

#### HyperedgeType → semantic relation mapping

The existing `HyperedgeType` enum in `Ast.Core` has 30+ values. The memory
layer groups them into the four canonical layers:

| HyperedgeType (existing) | Semantic relation | Layer | Typical role names |
|---|---|---|---|
| `ClassDefinition`, `InterfaceDefinition`, `StructDefinition`, `RecordDefinition` | `CONTAINS` (class defines members) | Structural | `parent`, `child` |
| `MethodDefinition`, `ConstructorDefinition`, `PropertyDefinition`, `FunctionDefinition` | `CONTAINS` (scope contains symbol) | Structural | `parent`, `child` |
| `UsingDirective`, `ModuleImport`, `DotSource` | `DEPENDS_ON` | Structural | `importer`, `imported` |
| `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`, `ModuleFunctionCall` | `CALLS` | Call-Chain | `caller`, `callee` |
| `Implements` | `IMPLEMENTS` | Call-Chain | `implementor`, `interface` |
| `Extends` | `EXTENDS` | Call-Chain | `subclass`, `superclass` |
| `DataFlow`, `Assignment` | `REFERENCES` | Call-Chain | `source`, `target` |
| `CrossLanguageMethodCall` | `CALLS` | Call-Chain | `caller`, `callee` |
| `CrossLanguageTypeReference` | `REFERENCES` | Call-Chain | `source`, `target` |
| `MemoryRelation` (new) | `SIMILAR_TO` | Semantic | `node_a`, `node_b` |
| `MemoryRelation` (new) | `BELONGS_TO_CLUSTER` | Semantic | `member`, `cluster` |
| `MemoryRelation` (new) | `APPLIES_TO` | Expertise | `expertise`, `target` |
| `MemoryRelation` (new) | `SUPERSEDES` | Expertise | `newer`, `older` |
| `MemoryRelation` (new) | `DERIVED_FROM` | Expertise | `derived`, `source` |

**Unmapped HyperedgeType values:** The following existing enum values are NOT
mapped to memory-layer semantic relations in the MVP. They are preserved in
`Metadata["OriginalEdgeType"]` for diagnostic purposes but do not generate
memory edges: `AttributeDecoration`, `LambdaExpression`, `LinqQuery`,
`AsyncAwait`, `DependencyInjection`, `EventHandler`, `GenericTypeConstraint`,
`ServiceRegistration`, `ApiEndpoint`, `EntryPoint`, `HttpClientUsage`,
`DatabaseAccess`, `FileSystemAccess`, `ConfigurationAccess`, `LoggingCall`,
`MiddlewarePipeline`, `Pipeline`, `RemoteExecution`, `ParamBlock`,
`DscConfiguration`, `DscResource`, `ControlFlow`, `ExceptionHandling`.
These may be promoted to semantic relations in future iterations.

**Memory-only edge types** (SIMILAR_TO, BELONGS_TO_CLUSTER, APPLIES_TO,
SUPERSEDES, DERIVED_FROM) are not in the existing `HyperedgeType` enum. They
are represented using `Hyperedge.EdgeType = HyperedgeType.MemoryRelation` — a
single new enum value added to `Ast.Core` (see §4.6 for rationale). The true
semantic type is always in `Metadata["SemanticRelation"]`.

**All edges** (both code-derived and memory-only) carry `Metadata["SemanticRelation"]`
with the canonical name (`CALLS`, `DEPENDS_ON`, `CONTAINS`, `EXTENDS`,
`IMPLEMENTS`, `REFERENCES`, `SIMILAR_TO`, `BELONGS_TO_CLUSTER`, `APPLIES_TO`,
`SUPERSEDES`, `DERIVED_FROM`). This provides a uniform filtering path
regardless of origin. The retrieval engine filters exclusively on
`SemanticRelation`, never on `HyperedgeType`.

### 4.2 Edge Property Schemas (via `Hyperedge.Metadata`)

Each semantic relation type stores its payload in `Hyperedge.Metadata`. The
keys below are in addition to the standard memory-layer keys (`MemoryLayer`,
`VersionRange`, `Confidence`, `Stale`) documented in §4.1.

| Semantic relation | Extra metadata keys | Example values |
|---|---|---|
| `CALLS` | `ArgCount: int`, `IsAsync: bool`, `IsConditional: bool`, `CallSiteStart: int`, `CallSiteEnd: int` | `ArgCount=2, IsAsync=true, IsConditional=false, CallSiteStart=120, CallSiteEnd=145` |
| `DEPENDS_ON` | `Alias: string?`, `IsWildcard: bool`, `IsTypeOnly: bool` | `Alias="jwt", IsWildcard=false, IsTypeOnly=false` |
| `APPLIES_TO` | `Scope: string` (`"exact"` or `"subtree"`) | `Scope="subtree"` |
| `SUPERSEDES` | `Reason: string`, `SupersededAt: string (ISO-8601)` | `Reason="Merged duplicate pattern", SupersededAt="2026-05-01T..."` |
| `SIMILAR_TO` | `CosineSim: double` | `CosineSim=0.92` |
| `CONTAINS` | (none — parent/child implicit in `Roles`) | — |
| `EXTENDS` | (none — relationship implicit in `EdgeType`) | — |
| `IMPLEMENTS` | (none — relationship implicit in `EdgeType`) | — |
| `REFERENCES` | `ReferenceKind: string` (`"type_annotation"`, `"variable"`, `"parameter"`, `"data_flow"`) | `ReferenceKind="type_annotation"` |

**Roles dictionary convention for binary relations:**

| Semantic relation | `Roles` keys | Example |
|---|---|---|
| `CALLS` | `{ callerId: "caller", calleeId: "callee" }` | `{ "/src/auth/login::AuthService.login": "caller", "/src/auth/token::verify": "callee" }` |
| `CONTAINS` | `{ parentId: "parent", childId: "child" }` | `{ "/src/auth/login": "parent", "/src/auth/login::AuthService": "child" }` |
| `DEPENDS_ON` | `{ importerId: "importer", importedId: "imported" }` | `{ "/src/auth/login": "importer", "/src/auth/token": "imported" }` |
| `EXTENDS` | `{ subclassId: "subclass", superclassId: "superclass" }` | — |
| `IMPLEMENTS` | `{ implementorId: "implementor", interfaceId: "interface" }` | — |
| `REFERENCES` | `{ sourceId: "source", targetId: "target" }` | — |
| `APPLIES_TO` | `{ expertiseId: "expertise", targetId: "target" }` | — |
| `SIMILAR_TO` | `{ nodeA: "node_a", nodeB: "node_b" }` | — |
| `SUPERSEDES` | `{ newerId: "newer", olderId: "older" }` | — |
| `DERIVED_FROM` | `{ derivedId: "derived", sourceId: "source" }` | — |

### 4.3 Expertise Node Schema

Expertise nodes are regular `HypergraphNode` instances with `NodeType` set to
one of `"Pattern"`, `"Pitfall"`, `"Preference"`, or `"Insight"`, and
`Metadata["MemoryLayer"] = "expertise"`. They carry richer metadata than
code-derived nodes:

| Metadata key | Type | Description |
|---|---|---|
| `ExpertiseKind` | `string` | `"pattern"`, `"pitfall"`, `"preference"`, or `"insight"` |
| `Title` | `string` | Human-readable label |
| `Body` | `string` | Detailed description (Markdown) |
| `Confidence` | `double` | `[0, 1]` — decays over time |
| `SourceAgent` | `string` | Agent session ID that authored it |
| `Evidence` | `List<Citation>` | References to code spans or commits |
| `Tags` | `List<string>` | Free-form taxonomy |

```csharp
/// <summary>
/// A reference to a code location that supports an expertise claim.
/// </summary>
public class Citation
{
    public required string NodeId { get; init; }      // string — path-based, e.g., "/src/auth/login::AuthService"
    public required string CommitSha { get; init; }    // string — git commit SHA
    public string? Snippet { get; init; }             // verbatim code excerpt (≤ 500 chars)
}
```

### 4.4 Embedding Lifecycle

Embeddings are dense vectors stored in `Metadata["Embedding"]` and indexed in
the vector store (Qdrant for team mode, in-process HNSW for local). The
lifecycle differs by node origin:

| Node origin | When embedded | Embedding input | Vector index update |
|---|---|---|---|
| **Code nodes** (Structural / Call-Chain) | At extraction time, inside the Extraction Pipeline's Semantic Indexer step | `HypergraphNode.GetEnhancedEmbeddingText()` — combines symbol type, signature, docstring, callers/callees, modifiers | Synchronous — vector is written to the index in the same `ApplyDiffAsync` transaction |
| **Expertise nodes** (Pattern, Pitfall, Preference, Insight) | At write-back time, inside `EvolutionEngine.RecordExpertiseAsync` | `"{ExpertiseKind}: {Title}\n{Body}"` — uses the same `IEmbeddingGenerator` as code nodes | Synchronous — vector is written before the method returns |
| **Topic / SemanticCluster nodes** | At community-summarization time, inside `EvolutionEngine.ConsolidateAsync` | LLM-generated prose summary of the cluster | Synchronous — written as part of the consolidation pass |
| **Re-embedding on code change** | During incremental sync, when a node's `ContentHash` changes | Same as initial extraction | Old vector is overwritten in-place (same node ID) |

**Model consistency:** All nodes use the same embedding model (configured in
`ExtractionOptions.EmbeddingModel`, default: `code-embedding-v2`, dimension
768). Mixing models produces incomparable vectors, so model changes require a
full re-embedding pass (triggered via `ast memory reindex --reembed`).

**Vector index consistency:** The vector index is a secondary index over the
graph store. It is updated synchronously during `ApplyDiffAsync` (for code
nodes) and `RecordExpertiseAsync` (for expertise nodes). If the vector index
is lost, it can be rebuilt from `Metadata["Embedding"]` on all nodes. The
`IMemoryGraphStore.RebuildVectorIndexAsync()` method supports this recovery
path.

### 4.5 Version Snapshot

A `Snapshot` is a logical point-in-time view of the graph, keyed by commit:

```csharp
public class Snapshot
{
    public required string Id { get; init; }               // string — e.g., "snapshot:{commitSha}"
    public required string CommitSha { get; init; }        // string — git commit SHA
    public required int CommitIndex { get; init; }         // int — monotonic index for ordering
    public string? ParentSha { get; init; }                // string — parent commit SHA
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

Snapshots are not full copies; they are commit-SHA bookmarks. The
`version_range` on each node/edge allows the store to reconstruct any
snapshot view via range filtering.

### 4.6 Memory-Only Edge Lifecycle

This section fully specifies how memory-only edges are created, stored,
queried, and evolved. Memory-only edges exist only in the Semantic and
Expertise layers and have no source-code counterpart.

#### 4.6.1 Enum extension: `HyperedgeType.MemoryRelation`

A single new value `MemoryRelation` is added to the `HyperedgeType` enum in
`Ast.Core/HypergraphModels.cs`. This is an **additive, non-breaking change** —
existing parsers never emit this value, so no existing code path is affected.

```csharp
public enum HyperedgeType
{
    // ... existing values unchanged ...

    /// <summary>
    /// Edges created by the memory layer (Semantic and Expertise layers).
    /// The specific relation is in Metadata["SemanticRelation"].
    /// </summary>
    MemoryRelation
}
```

Why a dedicated enum value instead of reusing `DataFlow` or another existing
value? Because queries that filter on `HyperedgeType` (e.g., existing analyzer
code that asks "show me all DataFlow edges") would incorrectly include
memory-only edges if we piggybacked on an existing value. A dedicated
`MemoryRelation` value keeps the domains separated.

#### 4.6.2 Creation triggers

| Edge | Created by | Trigger | Inputs |
|---|---|---|---|
| `SIMILAR_TO` | Semantic Indexer (in Extraction Pipeline) | After embedding a batch of nodes, pairwise cosine similarity exceeds `SimilarityThreshold` (default: 0.85 — see §3.3 thresholds) | Two node IDs + cosine similarity score |
| `BELONGS_TO_CLUSTER` | Evolution Engine → `ConsolidateAsync` | Community detection (Louvain) on the Structural + Call-Chain subgraph identifies clusters | Cluster ID + member node IDs |
| `APPLIES_TO` | Evolution Engine → `RecordExpertiseAsync` | Agent writes back an expertise entry with `AppliesTo` node ID list | Expertise node ID + target node IDs + scope (`"exact"` or `"subtree"`) |
| `SUPERSEDES` | Evolution Engine → `ConsolidateAsync` | Consolidation merges overlapping expertise nodes | New merged node ID + old node IDs + reason |
| `DERIVED_FROM` | Evolution Engine → `RecordExpertiseAsync` | New expertise overlaps (cosine > `SimilarityThreshold` = 0.85) with an existing expertise node | New node ID + existing node ID |

#### 4.6.3 Storage representation

Every memory-only edge is stored as a `Hyperedge` with:

```
Hyperedge {
  Id            = "{sourceId}→{relation}→{targetId}"  // deterministic, dedup-safe
  EdgeType      = HyperedgeType.MemoryRelation         // the dedicated enum value
  NodeIds       = [sourceId, targetId]                 // always 2 for binary relations
  Roles         = { sourceId: "<role_a>", targetId: "<role_b>" }
  Weight        = <confidence or similarity score>
  Metadata      = {
    "MemoryLayer":       "<semantic|expertise>",
    "SemanticRelation":  "<SIMILAR_TO|BELONGS_TO_CLUSTER|APPLIES_TO|SUPERSEDES|DERIVED_FROM>",
    "VersionRange":      { Since: "<sha>", SinceIndex: <int>, Until: null, UntilIndex: null },
    "Confidence":        <double 0–1>,
    "Stale":             false,
    "OriginalEdgeType":  "MemoryRelation"
    // + relation-specific keys from §4.2
  }
}
```

In SQLite, the `edges` table stores `edge_type = 'MemoryRelation'` and
`semantic_relation = '<canonical name>'`. The retrieval engine's SQL queries
filter on `semantic_relation`, never on `edge_type`.

#### 4.6.4 Retrieval

The Retrieval Engine accesses memory-only edges exclusively through
`TraversalPlan.SemanticRelations`. The Graph Traverser matches candidate edges
by comparing `Metadata["SemanticRelation"]` to the requested relation names.
For example, an `EXPERTISE_RECALL` query sets:

```csharp
var plan = new TraversalPlan
{
    Direction = "outbound",
    SemanticRelations = ["APPLIES_TO", "SUPERSEDES", "DERIVED_FROM"],
    LayerFilter = "expertise",
    MaxDepth = 2
};
```

The `EdgeTypes` field on `TraversalPlan` is ignored for memory-only edges (it
is `null` in all memory-layer queries). Conversely, code-derived edges also
carry `SemanticRelation`, so the same filter works uniformly across all layers.

#### 4.6.5 Evolution

| Event | Effect on memory-only edges |
|---|---|
| **Code target tombstoned** (file deleted, symbol removed) | `APPLIES_TO` edges pointing to the target get `VersionRange.Until` set. Expertise node's `Confidence` decays by 0.5. |
| **Code target content changed** (content_hash differs) | `APPLIES_TO` edges are marked `Stale = true`. The next consolidation pass re-evaluates relevance. |
| **Expertise merged** | Old expertise nodes' outbound `APPLIES_TO` edges get `VersionRange.Until` set. New merged node gets fresh `APPLIES_TO` edges. `SUPERSEDES` edge links new → old. |
| **Embedding model changed** | `SIMILAR_TO` edges are bulk-deleted (all get `VersionRange.Until`). Re-embedding pass re-creates them from scratch. |
| **Community re-detection** | Old `BELONGS_TO_CLUSTER` edges get `VersionRange.Until` set. New cluster assignments create fresh edges. |

---

## 5. Interfaces

Interfaces are defined in C# to match the repository's .NET 8.0 stack. Each
interface maps to its owning project in `src/ast/`.

### 5.1 Extraction Pipeline Interface

**Project:** `Ast.Memory`

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
    public string EmbeddingModel { get; init; } = "code-embedding-v2"; // dimension 768 — see §4.4
    public long MaxFileSize { get; init; } = 1_048_576; // 1 MB
}

/// <summary>
/// A single node or edge record produced by extraction.
/// Wraps the existing HypergraphNode/Hyperedge with version metadata.
/// ID is string to match HypergraphNode.Id / Hyperedge.Id.
/// </summary>
public abstract record GraphFact(string Id, string ContentHash, VersionRange VersionRange);
public record NodeFact(string Id, string ContentHash, VersionRange VersionRange, HypergraphNode Node)
    : GraphFact(Id, ContentHash, VersionRange);
public record EdgeFact(string Id, string ContentHash, VersionRange VersionRange, Hyperedge Edge)
    : GraphFact(Id, ContentHash, VersionRange);

public record GraphDiff(
    IReadOnlyList<GraphFact> Inserts,
    IReadOnlyList<GraphFact> Updates,
    IReadOnlyList<string> Tombstones);   // list of node/edge IDs (string)

public record VersionRange(string Since, int SinceIndex, string? Until, int? UntilIndex);
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

    /// <summary>
    /// Rebuild the vector index from Metadata["Embedding"] on all nodes.
    /// Recovery path if vector index is lost or embedding model changed.
    /// </summary>
    Task RebuildVectorIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve a commit SHA to its integer index (for range queries).
    /// Returns null if the commit is not in the commits table.
    /// </summary>
    Task<int?> ResolveCommitIndexAsync(
        string commitSha,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a new commit and assign the next monotonic index.
    /// </summary>
    Task<int> RegisterCommitAsync(
        string commitSha,
        string? parentSha,
        DateTime timestamp,
        CancellationToken cancellationToken = default);
}

public class TraversalPlan
{
    public required string Direction { get; init; } // "outbound" | "inbound" | "both"

    /// <summary>
    /// Sole edge filter — uses canonical semantic relation names (§1.5).
    /// Works uniformly for code-derived edges (CALLS, CONTAINS, DEPENDS_ON,
    /// EXTENDS, IMPLEMENTS, REFERENCES) and memory-only edges (SIMILAR_TO,
    /// APPLIES_TO, SUPERSEDES, BELONGS_TO_CLUSTER, DERIVED_FROM).
    /// If null or empty, all edges on the selected layer(s) are traversed.
    /// </summary>
    public IReadOnlyList<string>? SemanticRelations { get; init; }

    /// <summary>
    /// Deprecated — retained for backward compatibility with pre-memory queries.
    /// Memory-layer code MUST NOT set this field. Memory-only edges have
    /// HyperedgeType.MemoryRelation and are not reachable through this filter.
    /// </summary>
    [Obsolete("Use SemanticRelations instead. This field is ignored by memory-layer queries.")]
    public IReadOnlyList<HyperedgeType>? EdgeTypes { get; init; }

    public string? LayerFilter { get; init; }    // "structural", "call_chain", "semantic", "expertise", or null for all
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
    public int TokenBudget { get; init; } = 8_000;  // per-query default; ceiling is 32 K (see BudgetController)
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

**Project:** `Ast.Memory`

```csharp
/// <summary>
/// Manages agent-authored expertise: write-back, consolidation, and decay.
/// </summary>
public interface IEvolutionEngine
{
    /// <summary>Agent writes back a learning.</summary>
    Task<string> RecordExpertiseAsync(
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
    public required IReadOnlyList<string> AppliesTo { get; init; }  // node IDs (string — matches HypergraphNode.Id)
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

**Project:** `Ast.Memory`

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
   ├─ 1a. GraphStore.registerCommit(HEAD, parentSha, timestamp)
   │      → assigns monotonic commit_index for temporal ordering.
   ├─ 2. ExtractionPipeline.extractAll(repoRoot, options)
   │     ├─ 2a. Language Parser produces AST facts for each file.
   │     ├─ 2b. Structure Builder emits Structural-layer nodes/edges.
   │     ├─ 2c. Call-Chain Analyzer emits Call-chain-layer edges.
   │     ├─ 2d. Semantic Indexer computes embeddings via IEmbeddingGenerator,
   │     │      attaches float[] to Metadata["Embedding"] on each node.
   │     └─ 2e. Fact Normalizer deduplicates, assigns IDs, attaches
   │            version_range = {since: HEAD, sinceIndex: N, until: null, untilIndex: null}.
   │
   ├─ 3. GraphStore.applyDiff(diff, HEAD)
   │     ├─ 3a. Inserts all nodes and edges.
   │     ├─ 3b. Writes embedding vectors to vector index (synchronous).
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
   ├─ 1a. GraphStore.registerCommit(headCommit, baseCommit, timestamp)
   │      → assigns next commit_index for temporal ordering.

2. ExtractionPipeline.extractIncremental(repoRoot, base, head, changedFiles)
   ├─ 2a. Re-parse only changed files.
   ├─ 2b. Diff new facts against current graph → produces GraphDiff.
   ├─ 2c. Mark removed symbols' version_range.until = headCommit,
   │       version_range.untilIndex = headCommitIndex.

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
   ├─ 5a. Score = 0.30 × structural_proximity
   │     + 0.25 × semantic_similarity
   │     + 0.20 × centrality
   │     + 0.15 × recency
   │     + 0.10 × expertise_confidence
   │     (canonical defaults from §3.3 scoring table)
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
   ├─ 2b. Computes embedding from "{kind}: {title}\n{body}" via
   │       IEmbeddingGenerator, stores in Metadata["Embedding"].
   ├─ 2c. Writes embedding vector to vector index (synchronous).
   ├─ 2d. Creates APPLIES_TO edges (memory-only, filtered by
   │       SemanticRelation="APPLIES_TO") to CheckoutService.process
   │       and InventoryClient.reserve.
   ├─ 2e. Checks for existing pitfall nodes on the same targets:
   │       ├─ If semantically overlapping (cosine > SimilarityThreshold
   │       │   = 0.85, see §3.3): create DERIVED_FROM edge and
   │       │   schedule consolidation.
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
| **Single new project `Ast.Memory` with namespace-separated concerns** | Avoids over-splitting into `Ast.Memory.Extraction`, `Ast.Memory.Evolution`, etc. Follows the repo convention where each `Ast.*` project is a cohesive library. Internal sub-namespaces (Extraction, Evolution, Context, Sync, Storage) provide logical separation without project proliferation. | Larger compilation unit; mitigated by clear namespace boundaries. |
| **Non-breaking extension via `Metadata` dictionary** | Memory-layer fields (version range, layer tag, content hash) are stored in `HypergraphNode.Metadata` and `Hyperedge.Metadata` rather than adding new properties to base classes. This avoids breaking existing parsers and tests. | Metadata keys are string-typed (not compile-time checked); mitigated by a constants class `MemoryMetadataKeys` with strongly-typed accessor extensions. |
| **Memory-only edge types via `HyperedgeType.MemoryRelation` + `SemanticRelation` metadata** | A single new enum value `MemoryRelation` is added to `HyperedgeType` (additive, non-breaking). The specific relation is in `Metadata["SemanticRelation"]`. All edges (code-derived and memory-only) carry this key for uniform filtering. | String-typed relation names (not compile-time checked); mitigated by `MemoryMetadataKeys` constants class and validated at write time. |
| **Four separate graph layers sharing a node namespace** | Avoids the entanglement problem identified by MAGMA. Each layer can be queried, indexed, and evolved independently. | Cross-layer queries require joins; slight overhead vs. a single unified graph. |
| **Existing Ast.Csharp/PowerShell parsers as primary, tree-sitter for new languages** | Existing parsers are battle-tested with 166+ tests. Tree-sitter adds breadth for TypeScript, Python, Go without rewriting C#/PowerShell extraction. | Two parser stacks to maintain; unified `ILanguageParser` interface mitigates this. |
| **Version ranges on nodes/edges instead of full graph snapshots** | Space-efficient: O(changed entities) per commit instead of O(total graph). Enables time-travel queries. | Point-in-time reconstruction requires range filtering, which adds query complexity. |
| **LLM-powered consolidation in the Evolution Engine** | Semantic merge of expertise nodes is inherently a natural-language task. Rules-based merging misses nuance. | Adds LLM cost; consolidation is batched and async to amortize. |
| **Token-budget-aware retrieval with hierarchical fallback** | Agents have finite context windows. The system must be useful even at small budgets (2K tokens). | Summaries lose detail. The system compensates by providing citations so the agent can drill down. |
| **Confidence decay on expertise nodes** | Prevents stale advice from poisoning agent context after code refactors. | Aggressive decay can lose still-valid insights. Tunable decay factor mitigates this. |

---

## 9. Cross-Document Alignment Summary

This document is the **canonical authority** for all naming, data model, and
interface decisions. The tables below list (a) the canonical terms, and (b)
every known contradiction in sibling documents with the resolution.

### 9.1 Canonical Terms (source of truth)

| Topic | Canonical value | Defined in |
|---|---|---|
| Layer names | Structural, Call-Chain, Semantic, Expertise | §1.5 |
| Edge names (code-derived) | `CONTAINS`, `DEPENDS_ON`, `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` | §1.5 |
| Edge names (memory-only) | `SIMILAR_TO`, `BELONGS_TO_CLUSTER`, `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM` | §1.5, §4.6 |
| Retired edge names | `IMPORTS` → `DEPENDS_ON`; `CALLED_BY` → reverse traversal of `CALLS`; `INHERITS` → `EXTENDS`; `INSTANTIATES` → `CALLS`; `OVERRIDES` → `EXTENDS` | §1.5 |
| Node ID format | Path-based `string`: `"{filePath}::{qualifiedName}"` for symbols, `"{filePath}"` for files, `"/"` for repo root | §4.1 |
| ID type | `string` everywhere — no GUIDs, UUIDs, or integer IDs | §4.1 |
| Temporal model | Dual-axis: (1) commit SHA + monotonic `commit_index` integer for range queries, (2) wall-clock `LastTouched` ISO-8601 for informational display | §1.6 |
| VersionRange fields | `Since: string` (commit SHA), `SinceIndex: int`, `Until: string?`, `UntilIndex: int?` — range queries use the integer indices | §1.6, §4.1 |
| Model extension strategy | `MemoryNode` / `MemoryEdge` **compose** (wrap) `HypergraphNode` / `Hyperedge` — they do NOT inherit from the base types | §1.7 |
| Memory-only edge enum | Single additive value `HyperedgeType.MemoryRelation`; specific relation in `Metadata["SemanticRelation"]` | §4.6 |
| Edge filtering | `TraversalPlan.SemanticRelations` (string list) is the **sole** filter; `TraversalPlan.EdgeTypes` is deprecated | §5.2 |
| Expertise model class | `ExpertiseEntry` (for write-back input); expertise nodes are `HypergraphNode` with `NodeType` ∈ {`"Pattern"`, `"Pitfall"`, `"Preference"`, `"Insight"`} | §4.3, §5.4 |
| Retrieval scoring | 5-signal weighted model with canonical defaults: structural=0.30, semantic=0.25, centrality=0.20, recency=0.15, expertise=0.10 (sum=1.0). Full table in §3.3. | §3.3, §6.3 |
| Embedding model | `code-embedding-v2`, dimension 768. Configured in `ExtractionOptions.EmbeddingModel`. | §4.4, §5.1 |
| Similarity threshold | Cosine ≥ 0.85 for `SIMILAR_TO` and `DERIVED_FROM` edges | §3.3, §4.6.2 |
| Token budget | Per-query default = 8 K tokens; system ceiling = 32 K tokens (configurable) | §3.3, §5.3 |
| Embedding lifecycle | Three triggers: extraction time, write-back time, consolidation time | §4.4 |

### 9.2 Known Contradictions in Sibling Documents

The following table enumerates every known discrepancy between this document
and the three sibling documents. **The resolution column is binding — the
sibling document should be updated to match in its next iteration.**

| # | Sibling doc | Section | What it says | What this doc says | Resolution |
|---|---|---|---|---|---|
| C1 | `implementation-plan.md` | Stage 1.1 | `MemoryNode.cs` "extending `HypergraphNode`" / `MemoryEdge.cs` "extending `Hyperedge`" — implies C# class inheritance | §1.7: `MemoryNode` and `MemoryEdge` **compose** (not inherit from) the base types | **Use composition.** `MemoryNode` wraps a `HypergraphNode` field and provides typed Metadata accessors. No class inheritance. |
| C2 | `implementation-plan.md` | Stage 1.1 | Model class named `ExpertiseNote.cs` with field `Summary` | §4.3, §5.4: The write-back input type is `ExpertiseEntry` with fields `Title` and `Body` | **Use `ExpertiseEntry`** with `Title` (short label) and `Body` (detailed Markdown). The name `ExpertiseNote` is retired. |
| C3 | `implementation-plan.md` | Stage 1.1 | `Annotation.cs` with `ValidFrom` / `ValidUntil` temporal fields (unspecified type) | §1.6, §4.1: `VersionRange { Since, SinceIndex, Until, UntilIndex }` — integers for queries, SHAs for provenance | **Use `VersionRange`.** `ValidFrom`/`ValidUntil` column names in SQL map to `SinceIndex`/`UntilIndex` integers. |
| C4 | `implementation-plan.md` | Stage 2.1 | Canonical ID format `{repo}:{language}:{fqn}:{hash_short}` | §4.1: ID format `"{filePath}::{qualifiedName}"` — no repo prefix, no language, no hash | **Use path-based `"{filePath}::{qualifiedName}"`.** This aligns with e2e-scenarios node IDs and `HypergraphNode.Id` convention. |
| C5 | `implementation-plan.md` | Stage 2.2 | Uses edge names `CALLED_BY`, `INHERITS`, `IMPORTS` | §1.5 retired names: `CALLED_BY` → reverse `CALLS`; `INHERITS` → `EXTENDS`; `IMPORTS` → `DEPENDS_ON` | **Use canonical names only.** `CALLED_BY` is a reverse traversal, not a stored edge. `INHERITS` → `EXTENDS`. `IMPORTS` → `DEPENDS_ON`. |
| C6 | `implementation-plan.md` | Stage 2.2, test scenario | "Then an INHERITS edge exists between the two class nodes" | §1.5: `INHERITS` is retired → `EXTENDS` | **Test should assert `EXTENDS` edge.** |
| C7 | `tech-spec.md` | §7.4 | `valid_from` / `valid_until` are ISO-8601 timestamps; point-in-time queries filter `valid_from <= t` | §1.6: `valid_from_idx` / `valid_until_idx` are monotonic integers (`commit_index`); point-in-time queries filter `valid_from_idx <= $target_idx`. Wall-clock is in the `commits` table. | **Use integer `commit_index` for range queries.** Tech-spec §7.4 describes the conceptual model; the storage columns are integers. ISO-8601 timestamps are available via join to the `commits` table. |
| C8 | `tech-spec.md` | §7.3 | Annotations as a flat JSONB property bag on nodes (keys: `known_issues`, `complexity_score`, etc.) | §4.1, §4.3: Annotations are stored in `Metadata` dictionary on `HypergraphNode`; expertise facts are separate `HypergraphNode` instances in the Expertise layer | **Both are correct at different levels.** Simple annotations (complexity, access_count) go in `Metadata`. Structured expertise (patterns, pitfalls) are first-class Expertise-layer nodes linked via `APPLIES_TO`. |
| C9 | `tech-spec.md` | §6 diagram | Shows "Annotation Writer" as a separate component | §3.5: Annotation write-back is handled by the Evolution Engine's `RecordExpertiseAsync` | **Evolution Engine owns write-back.** "Annotation Writer" is not a separate component; it is the write-back path within `IEvolutionEngine`. |
| C10 | `e2e-scenarios.md` | Scenario 1.1 | Node IDs use bare paths: `/src/auth/login`, `/src/auth` | §4.1: Symbol nodes use `"{filePath}::{qualifiedName}"`; file/directory nodes use `"{filePath}"` | **Compatible.** E2e-scenarios show file/directory nodes (no `::` needed). Symbol nodes (Scenario 1.2) correctly use `::` syntax. |
| C11 | `e2e-scenarios.md` | Scenario 2.1 | Edge type column uses `direct_call`, `polymorphic`, `recursive`, `service_call` | §1.5: Canonical edge names are `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES` — subtypes go in `Metadata` | **The `edge_type` column in e2e Gherkin tables represents the `Metadata` subtype, not the canonical `SemanticRelation`.** The stored `SemanticRelation` is `CALLS` for all of these; the subtype (`direct_call`, `polymorphic`, etc.) belongs in `Metadata["CallKind"]`. |
| C12 | `implementation-plan.md` | Stage 1.2 | Interface named `IMemoryStore` extending `IGraphStore` with methods `UpsertAnnotationAsync`, `TombstoneNodeAsync`, `GetNodeAtVersionAsync` | §5.2: Interface named `IMemoryGraphStore` extending `IGraphStore` with methods `ApplyDiffAsync`, `TraverseAsync`, `GetNodeAtCommitAsync` | **Use `IMemoryGraphStore`** with the method signatures from §5.2. The name `IMemoryStore` is retired. |
| C13 | `implementation-plan.md` | Stage 1.2 | Separate `IExpertiseStore` interface | §5.2, §5.4: Expertise operations are on `IEvolutionEngine`, not a separate store interface | **No separate `IExpertiseStore`.** Expertise write/read goes through `IEvolutionEngine` which delegates to `IMemoryGraphStore` internally. |
| C14 | `implementation-plan.md` | Stage 3.2 | Scoring weights: `structural=0.3, semantic=0.3, centrality=0.15, recency=0.15, expertise=0.1` (sum=1.0) | §3.3 canonical scoring table: `structural=0.30, semantic=0.25, centrality=0.20, recency=0.15, expertise=0.10` (sum=1.0) | **Use architecture.md §3.3 canonical weights.** The implementation plan's `semantic=0.3` and `centrality=0.15` are superseded by `semantic=0.25` and `centrality=0.20` to give more weight to structural fan-in signals (high-centrality code hubs matter more than slightly higher semantic similarity). |
| C15 | `implementation-plan.md` | Stage 3.2 | `RelevanceScorer.cs` uses formula `score = w1*structural_distance + w2*semantic_similarity + w3*centrality + w4*recency + w5*expertise_signal` — term `w1*structural_distance` is unbounded (distance, not proximity) | §3.3: The signal is `structural_proximity` = inverse graph distance (BFS hops⁻¹), bounded in `[0, 1]` | **Use `structural_proximity` (inverse distance).** Raw BFS distance grows unboundedly; inverse distance normalizes to `[0, 1]` for weighted sum. |
| C16 | `tech-spec.md` | §5 S5 | Lists retrieval modes including `EXPERTISE_RECALL` and `HYBRID` without specifying scoring weights | §3.3 canonical scoring table defines all 5 signals and weights | **Compatible.** Tech-spec delegates scoring details to architecture.md. No contradiction, but tech-spec should reference §3.3 for the scoring formula. |
| C17 | `tech-spec.md` | §5 HC1 | Token budget "default: 32 K tokens" — implies per-query default is 32 K | §3.3, §5.3: System ceiling = 32 K; per-query default = 8 K tokens | **The 32 K value in HC1 is the system ceiling** (max allowed value for `TokenBudget`). The per-query default is 8 K. Agents may request up to 32 K; the Budget Controller never exceeds it. |
| C18 | `e2e-scenarios.md` | Scenario 2.3 | Polymorphic call confidence = `0.5` for all candidates | §3.3 configurable thresholds: dynamic dispatch confidence = `0.5`–`0.7` (range, not single value) | **Compatible.** The scenario uses `0.5` as a concrete example within the allowed range. The implementation should compute per-candidate confidence based on resolution heuristics. |

---

## 10. Appendix: Glossary

| Term | Definition |
|---|---|
| **Graph Fact** | A single node or edge record produced by the Extraction Pipeline. |
| **Graph Diff** | A batch of inserts, updates, and tombstones applied atomically to the Graph Store. |
| **Tombstone** | A soft-delete: the node/edge's `version_range.until` is set to the current commit SHA and `version_range.untilIndex` to the commit's index. The record is retained for time-travel. |
| **Commit Index** | A monotonically increasing integer assigned to each processed commit SHA. Stored in the `commits` table. Enables orderable point-in-time queries since commit SHAs are not lexicographically orderable. |
| **Community** | A cluster of tightly-coupled nodes detected by graph partitioning (e.g., Louvain algorithm) on the Structural + Call-chain layers. |
| **Expertise Node** | An agent-authored knowledge record (pattern, pitfall, preference, or insight) stored in the Expertise layer. Carries its own embedding vector for vector-search participation. |
| **Snapshot** | A logical point-in-time bookmark keyed by commit SHA + commit index, used for time-travel retrieval. |
| **Decay Factor** | A multiplier (0 < d ≤ 1) applied to an expertise node's confidence when its associated code changes. |
| **Context Block** | The final Markdown document returned by the Context Assembler, ready for insertion into an LLM prompt. |
| **Semantic Relation** | A canonical string name (e.g., `CALLS`, `APPLIES_TO`, `SIMILAR_TO`) stored in `Metadata["SemanticRelation"]` on every edge. The primary filtering mechanism for graph traversal, supporting both code-derived and memory-only edges. |
| **Memory-only Edge** | An edge type that exists only in the memory layer (SIMILAR_TO, BELONGS_TO_CLUSTER, APPLIES_TO, SUPERSEDES, DERIVED_FROM). Uses `HyperedgeType.MemoryRelation` as its enum value; identified by its `SemanticRelation` metadata string. Full lifecycle in §4.6. |
