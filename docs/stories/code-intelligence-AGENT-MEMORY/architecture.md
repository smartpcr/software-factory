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

**Unified layer naming (cross-document alignment):** All four sibling
documents MUST use the same canonical layer names. The canonical names are:

| Canonical layer | Tag value | Content |
|---|---|---|
| **Structural** | `structural` | Containment hierarchy: Repo → Workspace → Package → Module → Class → Function. Edges: `CONTAINS`, `DEPENDS_ON`. |
| **Call-Chain** | `call_chain` | Invocation graph between symbol nodes. Edges: `CALLS`, `EXTENDS`, `IMPLEMENTS`, `REFERENCES`. |
| **Semantic** | `semantic` | Embedding-similarity clusters and topics. Edges: `SIMILAR_TO`, `BELONGS_TO_CLUSTER`. |
| **Expertise** | `expertise` | Agent-authored knowledge. Edges: `APPLIES_TO`, `SUPERSEDES`, `DERIVED_FROM`. |

**Cross-document edge-name alignment (CRITICAL):**

| Canonical edge | Tech-spec name | E2E scenarios name | HyperedgeType enum values (Ast.Core) | Notes |
|---|---|---|---|---|
| `CONTAINS` | `CONTAINS` | containment edge | `ClassDefinition`, `InterfaceDefinition`, `StructDefinition`, `RecordDefinition`, `MethodDefinition`, `ConstructorDefinition`, `PropertyDefinition`, `FunctionDefinition` | Structural hierarchy parent→child |
| `DEPENDS_ON` | `DEPENDS_ON` | dependency edge | `UsingDirective`, `ModuleImport`, `DotSource` | Module/package-level imports. Replaces the earlier name `IMPORTS` used in iteration 1. |
| `CALLS` | `CALLS` | call edge | `MethodInvocation`, `ConstructorInvocation`, `CmdletCall`, `ModuleFunctionCall`, `CrossLanguageMethodCall` | Function-level invocation |
| `EXTENDS` | `EXTENDS` | inheritance edge | `Extends` | Class inheritance |
| `IMPLEMENTS` | `IMPLEMENTS` | interface edge | `Implements` | Interface implementation |
| `REFERENCES` | `REFERENCES` | reference edge | `DataFlow`, `Assignment`, `CrossLanguageTypeReference` | Non-call symbol references (type annotations, variable types, data flow) |
| `SIMILAR_TO` | — | similarity edge | *(memory-only)* | Semantic-layer embedding proximity |
| `BELONGS_TO_CLUSTER` | — | cluster membership | *(memory-only)* | Semantic-layer community grouping |
| `APPLIES_TO` | — | expertise link | *(memory-only)* | Expertise→code target |
| `SUPERSEDES` | — | supersession | *(memory-only)* | Expertise merge chain |
| `DERIVED_FROM` | — | derivation | *(memory-only)* | Expertise provenance |

**⚠ Retired names (authoritative — applies to ALL sibling docs):** `IMPORTS` → now `DEPENDS_ON`; `RE_EXPORTS` → subsumed by `DEPENDS_ON`; `CALLED_BY` → **not a stored edge** (tech-spec §S2 and implementation-plan §2.2 still reference it — they mean reverse traversal of `CALLS`); `INHERITS` → now `EXTENDS`; `INSTANTIATES` → subsumed by `CALLS` with `ConstructorInvocation` original type; `OVERRIDES` → subsumed by `EXTENDS` (override relationship is an inheritance variant). Any occurrence of these retired names in sibling documents should be read as the canonical replacement listed here.

The tech-spec previously referred to Fact / Derived / Interaction / Temporal
layers. Those names are **retired** in favour of the canonical names above.
Temporal versioning is an orthogonal concern applied to nodes and edges on
*all* layers — it is not a separate layer. The implementation stores a `layer`
string tag on every node and edge so queries can filter by any single layer or
combine layers.

**Cross-document temporal model alignment:** The tech-spec (§7.4) defines
temporality as `valid_from` / `valid_until` ISO-8601 timestamps with
provenance. This architecture uses `VersionRange { Since, Until }` with commit
SHAs **plus** a monotonic `CommitIndex` integer for ordering. These are
**complementary, not contradictory**: commit SHAs identify the exact commit,
while `CommitIndex` provides a total ordering for range queries (git's DAG
structure means SHA strings are not lexicographically orderable). ISO-8601
`LastTouched` timestamps provide the wall-clock axis (when extraction ran).

**Temporal ordering via CommitIndex (CRITICAL):** Commit SHAs are opaque
identifiers — they cannot be compared with `<=` or `>` operators. To enable
point-in-time queries, the system maintains a `commits` table that maps each
processed commit SHA to a monotonically increasing integer `commit_index`.
During extraction, `IncrementalSync` resolves `git rev-list --topo-order` to
assign indices. The `VersionRange` on every node and edge stores both the
commit SHA (for human-readable provenance) and the corresponding
`commit_index` (for efficient range queries). The SQLite storage schema
(tech-spec §8.2) maps `VersionRange.SinceIndex` → `valid_from` column and
`VersionRange.UntilIndex` → `valid_until` column, both as `INTEGER`.
Point-in-time queries use the integer index:
`WHERE valid_from_idx <= $target_idx AND (valid_until_idx IS NULL OR valid_until_idx > $target_idx)`.

The `commits` table schema:
```sql
CREATE TABLE commits (
    commit_sha  TEXT PRIMARY KEY,
    commit_index INTEGER NOT NULL UNIQUE,
    timestamp   TEXT NOT NULL,  -- ISO-8601 wall-clock time
    parent_sha  TEXT            -- for lineage
);
CREATE INDEX idx_commits_index ON commits(commit_index);
```

**Cross-document model extension alignment:** The implementation-plan (§1.1)
proposes `MemoryNode` extending `HypergraphNode` via C# inheritance and
`MemoryEdge` extending `Hyperedge`. This architecture specifies a
**non-breaking Metadata-based extension** instead (§4.1). Both approaches
coexist: `MemoryNode` / `MemoryEdge` are convenience wrapper classes that
compose (not inherit from) `HypergraphNode` / `Hyperedge` and provide
strongly-typed accessors for the Metadata keys. The underlying storage and
inter-component interfaces always use the base `HypergraphNode` / `Hyperedge`
types to avoid breaking existing parsers and tests.

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

**Memory-only edge traversal (CRITICAL):** The `EXPERTISE_RECALL` and
`SEMANTIC_SEARCH` modes traverse edges that exist only in the memory layer
(APPLIES_TO, SUPERSEDES, DERIVED_FROM, SIMILAR_TO, BELONGS_TO_CLUSTER). These
edges have no `HyperedgeType` enum value — they are identified solely by their
`Metadata["SemanticRelation"]` string. The `TraversalPlan.SemanticRelations`
field is the **primary** edge filter for all retrieval modes. The legacy
`TraversalPlan.EdgeTypes` field is optional and only matches code-derived edges.
When traversing the Expertise layer, the Graph Traverser uses:
```
SemanticRelations = ["APPLIES_TO", "SUPERSEDES", "DERIVED_FROM"]
LayerFilter = "expertise"
```
When traversing the Semantic layer for similarity:
```
SemanticRelations = ["SIMILAR_TO", "BELONGS_TO_CLUSTER"]
LayerFilter = "semantic"
```

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
├── EdgeType       : HyperedgeType enum   ← original Ast.Core enum value
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
| (memory-layer only) | `SIMILAR_TO` | Semantic | `node_a`, `node_b` |
| (memory-layer only) | `BELONGS_TO_CLUSTER` | Semantic | `member`, `cluster` |
| (memory-layer only) | `APPLIES_TO` | Expertise | `expertise`, `target` |
| (memory-layer only) | `SUPERSEDES` | Expertise | `newer`, `older` |
| (memory-layer only) | `DERIVED_FROM` | Expertise | `derived`, `source` |

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
are represented using a `SemanticRelation` string stored in
`Hyperedge.Metadata["SemanticRelation"]`. The `Hyperedge.EdgeType` field is set
to a placeholder value (e.g., `HyperedgeType.DataFlow`) for these edges, with
the true semantic type in metadata. This avoids modifying the existing enum
(which would be a breaking change to all existing parsers).

**All edges** (both code-derived and memory-only) carry `Metadata["SemanticRelation"]`
with the canonical name (`CALLS`, `DEPENDS_ON`, `CONTAINS`, `EXTENDS`,
`IMPLEMENTS`, `REFERENCES`, `SIMILAR_TO`, `BELONGS_TO_CLUSTER`, `APPLIES_TO`,
`SUPERSEDES`, `DERIVED_FROM`). This provides a uniform filtering path
regardless of whether the edge originated from an Ast.Core parser or the
memory layer.

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
    public string EmbeddingModel { get; init; } = "default";
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
    /// Primary edge filter — uses canonical semantic relation names.
    /// Supports BOTH code-derived edges (CALLS, CONTAINS, DEPENDS_ON, etc.)
    /// AND memory-only edges (SIMILAR_TO, APPLIES_TO, SUPERSEDES, etc.).
    /// If null or empty, all edge types on the selected layer(s) are traversed.
    /// </summary>
    public IReadOnlyList<string>? SemanticRelations { get; init; }  // canonical names: "CALLS", "SIMILAR_TO", "APPLIES_TO", etc.

    /// <summary>
    /// Optional legacy filter — restricts to specific HyperedgeType enum values.
    /// Only matches code-derived edges (memory-only edges have no HyperedgeType).
    /// When both SemanticRelations and EdgeTypes are set, edges matching EITHER filter are included.
    /// </summary>
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
   ├─ 2b. Computes embedding from "{kind}: {title}\n{body}" via
   │       IEmbeddingGenerator, stores in Metadata["Embedding"].
   ├─ 2c. Writes embedding vector to vector index (synchronous).
   ├─ 2d. Creates APPLIES_TO edges (memory-only, filtered by
   │       SemanticRelation="APPLIES_TO") to CheckoutService.process
   │       and InventoryClient.reserve.
   ├─ 2e. Checks for existing pitfall nodes on the same targets:
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
| **Single new project `Ast.Memory` with namespace-separated concerns** | Avoids over-splitting into `Ast.Memory.Extraction`, `Ast.Memory.Evolution`, etc. Follows the repo convention where each `Ast.*` project is a cohesive library. Internal sub-namespaces (Extraction, Evolution, Context, Sync, Storage) provide logical separation without project proliferation. | Larger compilation unit; mitigated by clear namespace boundaries. |
| **Non-breaking extension via `Metadata` dictionary** | Memory-layer fields (version range, layer tag, content hash) are stored in `HypergraphNode.Metadata` and `Hyperedge.Metadata` rather than adding new properties to base classes. This avoids breaking existing parsers and tests. | Metadata keys are string-typed (not compile-time checked); mitigated by a constants class `MemoryMetadataKeys` with strongly-typed accessor extensions. |
| **Memory-only edge types via `SemanticRelation` string in Metadata** | The existing `HyperedgeType` enum is owned by `Ast.Core` and used by all parsers. Adding memory-only values (SIMILAR_TO, APPLIES_TO, etc.) would be a breaking change. Storing them in `Metadata["SemanticRelation"]` keeps the existing enum stable. All edges (code-derived and memory-only) carry this key for uniform filtering. | String-typed relation names (not compile-time checked); mitigated by `MemoryMetadataKeys` constants class and validated at write time. |
| **Four separate graph layers sharing a node namespace** | Avoids the entanglement problem identified by MAGMA. Each layer can be queried, indexed, and evolved independently. | Cross-layer queries require joins; slight overhead vs. a single unified graph. |
| **Existing Ast.Csharp/PowerShell parsers as primary, tree-sitter for new languages** | Existing parsers are battle-tested with 166+ tests. Tree-sitter adds breadth for TypeScript, Python, Go without rewriting C#/PowerShell extraction. | Two parser stacks to maintain; unified `ILanguageParser` interface mitigates this. |
| **Version ranges on nodes/edges instead of full graph snapshots** | Space-efficient: O(changed entities) per commit instead of O(total graph). Enables time-travel queries. | Point-in-time reconstruction requires range filtering, which adds query complexity. |
| **LLM-powered consolidation in the Evolution Engine** | Semantic merge of expertise nodes is inherently a natural-language task. Rules-based merging misses nuance. | Adds LLM cost; consolidation is batched and async to amortize. |
| **Token-budget-aware retrieval with hierarchical fallback** | Agents have finite context windows. The system must be useful even at small budgets (2K tokens). | Summaries lose detail. The system compensates by providing citations so the agent can drill down. |
| **Confidence decay on expertise nodes** | Prevents stale advice from poisoning agent context after code refactors. | Aggressive decay can lose still-valid insights. Tunable decay factor mitigates this. |

---

## 9. Cross-Document Alignment Log

This section records the **authoritative resolution** for every cross-document
discrepancy. Architecture.md is the canonical source for component boundaries,
data model, edge names, and ID formats. When a sibling document deviates from
a decision listed here, the sibling document is out of date — the resolution
below is the correct answer.

| # | Topic | Canonical answer (this doc) | Sibling deviation | Resolution |
|---|---|---|---|---|
| A1 | **Layer names** | Structural, Call-Chain, Semantic, Expertise (§1.4) | tech-spec §7 uses a flat schema without explicit layer tags; e2e uses the same 4 names | ✅ **Resolved.** The 4-layer model is canonical. Tech-spec's flat schema is a storage-level detail — the logical layer tags apply on top of it. No conflict. |
| A2 | **Edge name: `DEPENDS_ON`** | `DEPENDS_ON` for all import/dependency relationships (§1.4, §4.1) | tech-spec §7.2 uses `DEPENDS_ON` ✅; e2e uses "dependency edge" ✅ | ✅ **Resolved.** All docs aligned. The iteration-1 name `IMPORTS` is retired. |
| A3 | **Edge names: `CALLED_BY`, `INHERITS`, `IMPORTS`** | These are **retired names** (§1.4). Canonical names: reverse traversal of `CALLS` (not stored `CALLED_BY`), `EXTENDS` (not `INHERITS`), `DEPENDS_ON` (not `IMPORTS`). | implementation-plan §2.2 uses `CALLED_BY`, `INHERITS`, and `IMPORTS`; tech-spec §S2 mentions `CALLED_BY` as a stored edge | ✅ **Resolved authoritatively.** The architecture does not store a `CALLED_BY` edge — reverse traversal of the `CALLS` edge covers that query. `INHERITS` → use `EXTENDS`. `IMPORTS` → use `DEPENDS_ON`. Sibling docs should be updated to match, but these canonical names are final and implementation must follow them regardless of what sibling text currently says. |
| A4 | **Node ID format** | Path-based strings: `"/src/auth/login::AuthService"` (§4.1) | implementation-plan §2.1 references a `{repo}:{language}:{fqn}:{hash_short}` format from iteration 1 | ✅ **Resolved authoritatively.** The `{repo}:{language}:{fqn}:{hash_short}` format is retired. Path-based `"{filePath}::{qualifiedName}"` is the only valid ID format. E2e-scenarios already use this format. |
| A5 | **Node hierarchy** | Repo → Workspace → Package → Module → Class → Function (§3.2) | tech-spec §7.1 includes `Workspace` ✅; e2e §1.1 shows directory-based hierarchy ✅ | ✅ **Resolved.** All docs aligned. Workspace is optional (monorepo only). |
| A6 | **ID type** | `string` everywhere — no GUIDs, UUIDs, or integer IDs (§4.1) | All sibling docs use `string` ✅ | ✅ **Resolved.** |
| A7 | **Temporal model** | Dual-axis: commit SHA + monotonic `commit_index` (integer) for ordering, plus `LastTouched` ISO-8601 for wall-clock. `VersionRange { Since, SinceIndex, Until, UntilIndex }` (§1.4, §4.1). The `commits` table (§1.4) maps SHA → index. | tech-spec §7.4 uses `valid_from`/`valid_until` ISO-8601 timestamps only | ✅ **Resolved authoritatively.** The tech-spec's ISO-8601 timestamps map to `LastTouched` (wall-clock axis). For ordering in range queries, integer `commit_index` is required because commit SHAs are not lexicographically orderable. The SQLite columns `valid_from_idx` and `valid_until_idx` are `INTEGER` and store the commit index. Tech-spec's timestamp-only model is a simplification that omits the ordering axis — the full model defined here is canonical. |
| A8 | **Embedding lifecycle** | Three-phase: code nodes at extraction, expertise nodes at write-back, topic nodes at consolidation (§4.4) | tech-spec §8.1 step 4 mentions embeddings during extraction only | ✅ **Resolved authoritatively.** The extraction-only model covers code nodes. Expertise and topic nodes require additional embedding at write-back and consolidation time respectively. The full three-phase model in §4.4 is canonical. |
| A9 | **Memory-only edge traversal** | `TraversalPlan.SemanticRelations` (string list) is the primary edge filter for ALL retrieval modes. `EdgeTypes` (HyperedgeType enum list) is optional/legacy and only matches code-derived edges (§5.2, §3.3). | implementation-plan §3.1 references `IMemoryQuery` without specifying the filter mechanism | ✅ **Resolved authoritatively.** Any query touching the Expertise or Semantic layers MUST use `SemanticRelations` because those layers have memory-only edges with no `HyperedgeType` enum value. Implementation must filter on `Metadata["SemanticRelation"]` string, not on `Hyperedge.EdgeType`. |
| A10 | **Model extension strategy** | Non-breaking Metadata-based extension. `MemoryNode`/`MemoryEdge` are convenience wrappers that **compose** (not inherit from) `HypergraphNode`/`Hyperedge` (§1.4, §3.0, §4.1). | implementation-plan §1.1 says "extending `HypergraphNode`" (implying C# inheritance) | ✅ **Resolved authoritatively.** Composition, not inheritance. `MemoryNode` wraps a `HypergraphNode` instance and provides strongly-typed accessors for memory Metadata keys. It does not subclass `HypergraphNode`. This avoids breaking existing parser contracts. Implementation-plan's "extending" should be read as "extending with memory metadata," not as C# class inheritance. |
| A11 | **SQLite storage: binary edges vs. hyperedge model** | The public API preserves the `Hyperedge` model (`NodeIds: List<string>`, `Roles: Dictionary<string, string>`). SQLite decomposes binary relationships into `source_id`/`target_id` columns for indexing efficiency (§4.1). | tech-spec §8.2 uses `source_id, target_id` in the schema | ✅ **Resolved — compatible.** This is not a contradiction. The SQLite schema is a storage optimization; the `IMemoryGraphStore` interface accepts and returns `Hyperedge` objects. The store implementation converts between the two representations internally. |
| A12 | **Retrieval scoring weights** | 5-term model: structural proximity (0.35) + call-chain distance⁻¹ (0.25) + cosine similarity (0.20) + recency (0.10) + expertise confidence (0.10) — see §6.3 | tech-spec §8.3 uses 3 terms; implementation-plan §3.2 uses 5 terms with different default weights | ✅ **Resolved.** The 5-term model in §6.3 is canonical. All weights are configurable at runtime via `RankerOptions`. The tech-spec's 3-term model is a summary that omits recency and call-chain-distance; it is not wrong, just simplified. The implementation-plan's alternative defaults (0.3/0.3/0.15/0.15/0.1) are acceptable initial values that may be tuned empirically. |

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
| **Memory-only Edge** | An edge type that exists only in the memory layer (SIMILAR_TO, BELONGS_TO_CLUSTER, APPLIES_TO, SUPERSEDES, DERIVED_FROM). Has no `HyperedgeType` enum value; identified solely by its `SemanticRelation` string. |
